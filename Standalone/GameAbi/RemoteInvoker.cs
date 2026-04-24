using System.Text;

using GirlsMadeInfinitePudding.ProcessMemory;

using Iced.Intel;

using static Iced.Intel.AssemblerRegisters;

namespace GirlsMadeInfinitePudding.GameAbi;

/// <summary>
///     Executes arbitrary IL2CPP / native calls inside the game process by
///     assembling an x86-64 shellcode trampoline with <see cref="Assembler" />
///     (the Iced library) and invoking it via <c>CreateRemoteThread</c>.
///     <para>
///         The remote stub attaches the new thread to the IL2CPP domain once
///         (so managed runtime state is safe to touch from it), sets up the
///         Win64 calling-convention registers with the user-supplied args, calls
///         the target, stores RAX into a "return slot" appended after the code,
///         and returns.  The parent process then reads the slot with
///         <c>ReadProcessMemory</c> to recover the real return value.
///     </para>
/// </summary>
public sealed class RemoteInvoker
{
    // =====================================================================
    //  MethodInfo* resolution via the IL2CPP C API.
    //
    //  For safe calls (those that might throw a managed exception) we have to
    //  route through il2cpp_runtime_invoke — but that wants a MethodInfo*,
    //  not a raw function pointer.  We obtain one by walking every assembly
    //  in the current domain and asking each image for the target class:
    //
    //      for asm in il2cpp_domain_get_assemblies(domain, &size):
    //          img = il2cpp_assembly_get_image(asm)
    //          cls = il2cpp_class_from_name(img, ns, className)
    //          if cls != null:
    //              method = il2cpp_class_get_method_from_name(cls, name, argc)
    //              if method != null: return method
    //      return null
    //
    //  The whole loop runs inside a single remote thread so we pay one
    //  CreateRemoteThread trip per resolve.  Results are cached in
    //  <see cref="_methodCache"/> so we only resolve each method once.
    // =====================================================================
    private readonly Dictionary<string, IntPtr> _methodCache = new(StringComparer.Ordinal);
    private readonly GameProcess _proc;

    public RemoteInvoker(GameProcess proc, RemoteExportResolver exports)
    {
        _proc = proc;
        Il2CppThreadAttach = exports["il2cpp_thread_attach"];
        Il2CppThreadDetach = exports["il2cpp_thread_detach"];
        Il2CppDomainGet = exports["il2cpp_domain_get"];
        Il2CppStringNew = exports["il2cpp_string_new"];
        Il2CppObjectNew = exports["il2cpp_object_new"];
        Il2CppRuntimeInvoke = exports["il2cpp_runtime_invoke"];
        Il2CppArrayNew = exports["il2cpp_array_new"];
        Il2CppDomainGetAssemblies = exports["il2cpp_domain_get_assemblies"];
        Il2CppAssemblyGetImage = exports["il2cpp_assembly_get_image"];
        Il2CppClassFromName = exports["il2cpp_class_from_name"];
        Il2CppClassGetMethodFromName = exports["il2cpp_class_get_method_from_name"];
    }

    public IntPtr Il2CppThreadAttach { get; }
    public IntPtr Il2CppThreadDetach { get; }
    public IntPtr Il2CppDomainGet { get; }
    public IntPtr Il2CppStringNew { get; }
    public IntPtr Il2CppObjectNew { get; }
    public IntPtr Il2CppRuntimeInvoke { get; }
    public IntPtr Il2CppArrayNew { get; }
    public IntPtr Il2CppDomainGetAssemblies { get; }
    public IntPtr Il2CppAssemblyGetImage { get; }
    public IntPtr Il2CppClassFromName { get; }
    public IntPtr Il2CppClassGetMethodFromName { get; }

    /// <summary>
    ///     Low-level: call an arbitrary native function (Win64 fastcall, up to
    ///     four pointer-sized args) in the remote process and return RAX.
    ///     The thread attaches to the IL2CPP domain first so managed runtime
    ///     code is safe to execute.
    /// </summary>
    public ulong InvokeNative(IntPtr target, IntPtr a0 = default, IntPtr a1 = default,
        IntPtr a2 = default, IntPtr a3 = default, int timeoutMs = 10000)
    {
        var (code, retSlotOffset) = BuildShellcode(
            target, (ulong)a0, (ulong)a1, (ulong)a2, (ulong)a3);
        var remote = _proc.Allocate(code.Length, true);
        try
        {
            _proc.Write(remote, code);
            _proc.CallRemote(remote, IntPtr.Zero, timeoutMs);
            return _proc.ReadU64(remote + retSlotOffset);
        }
        finally
        {
            _proc.Free(remote);
        }
    }

    /// <summary>Allocate a C-string in the remote process and return an il2cpp System.String* pointer.</summary>
    public IntPtr NewIl2CppString(string value)
    {
        // Push the UTF-8 bytes into a scratch buffer, then call il2cpp_string_new.
        var bytes = Encoding.UTF8.GetBytes(value);
        var buf = _proc.Allocate(bytes.Length + 1);
        try
        {
            var tmp = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, tmp, 0, bytes.Length);
            _proc.Write(buf, tmp);
            var r = InvokeNative(Il2CppStringNew, buf);
            return (IntPtr)r;
        }
        finally
        {
            _proc.Free(buf);
        }
    }

    /// <summary>
    ///     Chain together several managed Unity calls in one remote-thread trip so
    ///     we can pull pixels out of a non-readable sprite texture without asking
    ///     the game to run a six-step script coordinated from our side.
    ///     <para>
    ///         Inside the remote thread we perform:
    ///         <code>
    ///   rt   = RenderTexture.GetTemporary(w, h)
    ///   Graphics.Blit(src, rt)
    ///   prev = RenderTexture.get_active(); RenderTexture.set_active(rt)
    ///   dst  = il2cpp_object_new(*(void**)src)   // src's klass == Texture2D_c
    ///   Texture2D..ctor(dst, w, h)
    ///   dst.ReadPixels(Rect(0,0,w,h), 0, 0, false)
    ///   px   = dst.GetPixels32()
    ///   RenderTexture.set_active(prev)
    ///   RenderTexture.ReleaseTemporary(rt)
    ///   retSlot = px
    /// </code>
    ///     </para>
    ///     <para>
    ///         <b>⚠ Currently dead — DO NOT CALL.</b> Every Unity rendering API is
    ///         tied to Unity's main/render thread via TLS (GfxDevice and command
    ///         buffers live there).  Running this shellcode from a
    ///         <c>CreateRemoteThread</c>-born worker crashes inside UnityPlayer.dll
    ///         with a null-vtable deref (observed at
    ///         <c>unityplayer.dll+0x446FC8</c>, where the engine loads a null
    ///         <c>GfxDevice</c> and does <c>call [r9+0x7F8]</c>).
    ///         <c>il2cpp_thread_attach</c> only registers the thread with the GC /
    ///         managed runtime — it doesn't promote us to a thread Unity is
    ///         willing to submit rendering work from.
    ///     </para>
    ///     <para>
    ///         Kept here as reference for the "chained remote call" pattern — if we
    ///         ever switch to a real injected DLL with a main-thread hook (or to
    ///         <c>UnitySynchronizationContext.Post</c>), the same body should work
    ///         once it actually runs on the render thread.
    ///     </para>
    /// </summary>
    public IntPtr InvokeBlitReadPixels(IntPtr srcTex, int width, int height, int timeoutMs = 100_000)
    {
        if (srcTex == IntPtr.Zero) return IntPtr.Zero;
        if (width <= 0 || height <= 0) return IntPtr.Zero;

        var (code, retSlotOffset) = BuildBlitShellcode(srcTex, width, height);
        var remote = _proc.Allocate(code.Length, true);
        try
        {
            _proc.Write(remote, code);
            _proc.CallRemote(remote, IntPtr.Zero, timeoutMs);
            return (IntPtr)_proc.ReadU64(remote + retSlotOffset);
        }
        finally
        {
            _proc.Free(remote);
        }
    }

    // =====================================================================
    //  Shellcode emitter for InvokeBlitReadPixels.
    //
    //  Calling convention reminder (Win64):
    //    - First 4 args: rcx, rdx, r8, r9.
    //    - Args 5+ go at [rsp+0x20], [rsp+0x28], ...  (after shadow space).
    //    - Caller allocates shadow space (0x20) below its first stack arg.
    //    - Stack must be 16-byte aligned at the CALL instruction.
    //
    //  Local layout (negative offsets from rbp):
    //    -0x08  rt     (RenderTexture*)
    //    -0x10  prev   (previous active RT*)
    //    -0x18  dst    (Texture2D* we allocate)
    //    -0x20  colors (Color32[]* — mirrors retSlot)
    //    -0x30  Rect   (16 bytes: x=0, y=0, w=width, h=height)
    //
    //  After 'push rbp' the stack is 8 mod 16, so we sub 0x40 to re-align:
    //  0x30 of locals + 0x20 shadow + 8 for stack arg = 0x58, round up to 0x60,
    //  add 8 for re-alignment → sub rsp, 0x68.  But simpler: just use 0x70.
    // =====================================================================
    private (byte[] code, int retSlotOffset) BuildBlitShellcode(IntPtr srcTex, int width, int height)
    {
        var c = new Assembler(64);
        var retSlot = c.CreateLabel("retSlot");
        var fail = c.CreateLabel("fail");
        var cleanup = c.CreateLabel("cleanup");

        // Resolve every IDA-image-base RVA to an actual runtime pointer
        // inside the game process.  Baking the IDB addresses directly
        // into the shellcode would crash immediately under ASLR.
        var fnGetTemporary = (ulong)_proc.ResolveRva(Offsets.Fn_RenderTexture_GetTemporary_2);
        var fnBlit = (ulong)_proc.ResolveRva(Offsets.Fn_Graphics_Blit_2);
        var fnGetActive = (ulong)_proc.ResolveRva(Offsets.Fn_RenderTexture_get_active);
        var fnSetActive = (ulong)_proc.ResolveRva(Offsets.Fn_RenderTexture_set_active);
        var fnTexture2dCtorIi = (ulong)_proc.ResolveRva(Offsets.Fn_Texture2D_ctor_ii);
        var fnReadPixels = (ulong)_proc.ResolveRva(Offsets.Fn_Texture2D_ReadPixels);
        var fnGetPixels32 = (ulong)_proc.ResolveRva(Offsets.Fn_Texture2D_GetPixels32);
        var fnReleaseTemporary = (ulong)_proc.ResolveRva(Offsets.Fn_RenderTexture_ReleaseTemporary);

        // --- Prologue ---------------------------------------------------------
        c.push(rbp);
        c.mov(rbp, rsp);
        c.sub(rsp, 0x80); // 0x30 locals + 0x20 shadow + 0x20 slack for stack args + pad
        // Locals start at [rbp-0x80] .. [rbp-0x08] conceptually; we'll use
        // the highest slots for scratch (closest to rbp).
        //   [rbp-0x08] = rt
        //   [rbp-0x10] = prev
        //   [rbp-0x18] = dst
        //   [rbp-0x20] = colors (also saved to retSlot at the end)
        //   [rbp-0x30] = Rect(0,0,w,h)
        // Stack args for ReadPixels go at [rsp+0x20].

        // --- il2cpp_thread_attach(il2cpp_domain_get()) -------------------------
        c.mov(rax, (ulong)Il2CppDomainGet);
        c.call(rax);
        c.mov(rcx, rax);
        c.mov(rax, (ulong)Il2CppThreadAttach);
        c.call(rax);

        // --- rt = RenderTexture.GetTemporary(width, height) -------------------
        // (int w, int h, MethodInfo* method=0)
        c.mov(ecx, (uint)width);
        c.mov(edx, (uint)height);
        c.xor(r8, r8);
        c.mov(rax, fnGetTemporary);
        c.call(rax);
        c.test(rax, rax);
        c.je(fail);
        c.mov(__[rbp - 0x08], rax); // rt

        // --- Graphics.Blit(src, rt) -------------------------------------------
        c.mov(rcx, (ulong)srcTex);
        c.mov(rdx, __[rbp - 0x08]);
        c.xor(r8, r8);
        c.mov(rax, fnBlit);
        c.call(rax);

        // --- prev = RenderTexture.get_active(); RT.set_active(rt) --------------
        c.xor(rcx, rcx);
        c.mov(rax, fnGetActive);
        c.call(rax);
        c.mov(__[rbp - 0x10], rax); // prev (may be null; fine)

        c.mov(rcx, __[rbp - 0x08]);
        c.xor(rdx, rdx);
        c.mov(rax, fnSetActive);
        c.call(rax);

        // --- dst = il2cpp_object_new(*(void**)srcTex) --------------------------
        // srcTex's klass pointer lives at srcTex+0 (standard IL2CPP layout).
        c.mov(rax, (ulong)srcTex);
        c.mov(rcx, __[rax]); // rcx = klass
        c.mov(rax, (ulong)Il2CppObjectNew);
        c.call(rax);
        c.test(rax, rax);
        c.je(cleanup);
        c.mov(__[rbp - 0x18], rax); // dst

        // --- Texture2D..ctor(dst, w, h) ---------------------------------------
        c.mov(rcx, __[rbp - 0x18]);
        c.mov(edx, (uint)width);
        c.mov(r8d, (uint)height);
        c.xor(r9, r9);
        c.mov(rax, fnTexture2dCtorIi);
        c.call(rax);

        // --- Build Rect(0,0,w,h) on the stack at [rbp-0x30] --------------------
        //   dword[rbp-0x30]=0 (x), [rbp-0x2C]=0 (y), [rbp-0x28]=w, [rbp-0x24]=h
        c.mov(__dword_ptr[rbp - 0x30], 0);
        c.mov(__dword_ptr[rbp - 0x2C], 0);
        // Floats-from-ints bit pattern: use the int value but interpreted as
        // float.  We really want (float)width / (float)height; emit the IEEE
        // bit pattern at encode time so the shellcode itself stores the
        // correct float bytes.
        unsafe
        {
            var fw = (float)width;
            var fh = (float)height;
            var wBits = *(uint*)&fw;
            var hBits = *(uint*)&fh;
            c.mov(__dword_ptr[rbp - 0x28], wBits);
            c.mov(__dword_ptr[rbp - 0x24], hBits);
        }

        // --- dst.ReadPixels(Rect&, destX=0, destY=0, recalc=false) ------------
        // rcx=dst, rdx=&Rect, r8=0, r9=0, [rsp+0x20]=0 (recalc), [rsp+0x28]=0 (method)
        c.mov(rcx, __[rbp - 0x18]);
        c.lea(rdx, __[rbp - 0x30]);
        c.xor(r8, r8);
        c.xor(r9, r9);
        c.mov(__qword_ptr[rsp + 0x20], 0);
        c.mov(__qword_ptr[rsp + 0x28], 0);
        c.mov(rax, fnReadPixels);
        c.call(rax);

        // --- colors = dst.GetPixels32() ---------------------------------------
        c.mov(rcx, __[rbp - 0x18]);
        c.xor(rdx, rdx);
        c.mov(rax, fnGetPixels32);
        c.call(rax);
        c.mov(__[rbp - 0x20], rax); // colors

        // --- fallthrough: cleanup (restore active, release rt, store retSlot) --
        c.Label(ref cleanup);
        c.mov(rcx, __[rbp - 0x10]);
        c.xor(rdx, rdx);
        c.mov(rax, fnSetActive);
        c.call(rax);

        c.mov(rcx, __[rbp - 0x08]);
        c.xor(rdx, rdx);
        c.mov(rax, fnReleaseTemporary);
        c.call(rax);

        // Store colors to retSlot (RIP-relative lea).
        c.lea(r10, __[retSlot]);
        c.mov(rax, __[rbp - 0x20]);
        c.mov(__[r10], rax);

        // Epilogue.
        c.add(rsp, 0x80);
        c.pop(rbp);
        c.xor(eax, eax);
        c.ret();

        // --- fail: clean up whatever we created so far and return 0 -----------
        c.Label(ref fail);
        // Nothing allocated yet, retSlot defaults to 0 via dq below.
        c.lea(r10, __[retSlot]);
        c.xor(rax, rax);
        c.mov(__[r10], rax);
        c.add(rsp, 0x80);
        c.pop(rbp);
        c.xor(eax, eax);
        c.ret();

        // retSlot: qword 0
        c.Label(ref retSlot);
        c.dq(0UL);

        using var ms = new MemoryStream();
        var writer = new StreamCodeWriter(ms);
        c.Assemble(writer, 0);
        var bytes = ms.ToArray();

        // retSlot is the last 8 bytes we emitted (same trick as BuildShellcode).
        var retSlotOffset = bytes.Length - 8;
        return (bytes, retSlotOffset);
    }

    // =====================================================================
    //  Shellcode emitter — Iced.Intel.Assembler
    //
    //  The pseudo-asm we emit (Intel syntax):
    //
    //      push    rbp
    //      mov     rbp, rsp
    //      sub     rsp, 0x40                ; 0x20 shadow + 0x20 slack (aligned)
    //
    //      mov     rax, il2cpp_domain_get
    //      call    rax
    //      mov     rcx, rax                 ; rcx = domain
    //      mov     rax, il2cpp_thread_attach
    //      call    rax
    //
    //      mov     rcx, a0
    //      mov     rdx, a1
    //      mov     r8,  a2
    //      mov     r9,  a3
    //      mov     rax, target
    //      call    rax
    //
    //      lea     r10, [retSlot]           ; RIP-relative via label
    //      mov     [r10], rax
    //
    //      add     rsp, 0x40
    //      pop     rbp
    //      xor     eax, eax
    //      ret
    //
    //   retSlot: dq 0                        ; return-value scratch
    //
    //  Iced lets us use labels for the RIP-relative lea, and encodes
    //  absolute calls as `mov rax, imm64; call rax`.  `Assembler.Assemble`
    //  with a baseline RIP of 0 is fine because the only relative thing
    //  in the code is the label reference, whose displacement is
    //  computed as (label - nextRip) -- i.e. purely relative and
    //  independent of where the buffer ends up being mapped.
    // =====================================================================
    private (byte[] code, int retSlotOffset) BuildShellcode(
        IntPtr target, ulong a0, ulong a1, ulong a2, ulong a3)
    {
        var c = new Assembler(64);
        var retSlot = c.CreateLabel("retSlot");

        // Prologue.  sub rsp, 0x40 = 32-byte shadow space + slack, keeping
        // the stack 16-byte aligned at the call site (call pushed 8 bytes,
        // push rbp added 8, sub 0x40 = aligned).
        c.push(rbp);
        c.mov(rbp, rsp);
        c.sub(rsp, 0x40);

        // il2cpp_thread_attach(il2cpp_domain_get())
        c.mov(rax, (ulong)Il2CppDomainGet);
        c.call(rax);
        c.mov(rcx, rax);
        c.mov(rax, (ulong)Il2CppThreadAttach);
        c.call(rax);

        // Marshal arguments and call the target.  Assembler accepts
        // 64-bit immediates directly for mov reg, imm64.
        c.mov(rcx, a0);
        c.mov(rdx, a1);
        c.mov(r8, a2);
        c.mov(r9, a3);
        c.mov(rax, (ulong)target);
        c.call(rax);

        // Stash RAX at [retSlot].  Iced emits a proper RIP-relative lea
        // against the label, so wherever the buffer lands at runtime the
        // displacement stays valid.
        c.lea(r10, __[retSlot]);
        c.mov(__[r10], rax);

        // Epilogue.  Thread exit code is zero - the real return value is
        // carried via the retSlot scratch word.
        c.add(rsp, 0x40);
        c.pop(rbp);
        c.xor(eax, eax);
        c.ret();

        // retSlot: qword 0
        c.Label(ref retSlot);
        c.dq(0UL);

        using var ms = new MemoryStream();
        var writer = new StreamCodeWriter(ms);
        c.Assemble(writer, 0);
        var bytes = ms.ToArray();

        // Compute retSlot's byte offset by re-asking the assembler where
        // that label sits.  Iced doesn't directly expose label IPs after a
        // single Assemble(), so we cheat: the dq qword is the last 8 bytes
        // we emitted, so retSlot == bytes.Length - 8.
        var retSlotOffset = bytes.Length - 8;
        return (bytes, retSlotOffset);
    }

    /// <summary>
    ///     Resolve an IL2CPP <c>MethodInfo*</c> by namespace + class + method name.
    ///     Returns <see cref="IntPtr.Zero" /> if the method can't be found.
    ///     Results are cached per invoker instance.
    /// </summary>
    public IntPtr ResolveMethod(string @namespace, string className, string methodName, int argc)
    {
        var key = $"{@namespace}|{className}|{methodName}|{argc}";
        if (_methodCache.TryGetValue(key, out var cached)) return cached;

        var nsBuf = WriteRemoteCString(@namespace);
        var clsBuf = WriteRemoteCString(className);
        var mtdBuf = WriteRemoteCString(methodName);
        try
        {
            var (code, retSlotOffset) =
                BuildResolveMethodShellcode(nsBuf, clsBuf, mtdBuf, argc);
            var remote = _proc.Allocate(code.Length, true);
            try
            {
                _proc.Write(remote, code);
                _proc.CallRemote(remote, IntPtr.Zero);
                var result = (IntPtr)_proc.ReadU64(remote + retSlotOffset);
                _methodCache[key] = result;
                return result;
            }
            finally
            {
                _proc.Free(remote);
            }
        }
        finally
        {
            _proc.Free(nsBuf);
            _proc.Free(clsBuf);
            _proc.Free(mtdBuf);
        }
    }

    /// <summary>
    ///     Call an IL2CPP method via <c>il2cpp_runtime_invoke</c> so that any
    ///     managed exception raised inside is caught by the IL2CPP runtime and
    ///     returned via an out-parameter instead of unwinding through our
    ///     shellcode trampoline (which has no SEH tables and would crash the
    ///     game).  If the method raised, throws <see cref="Il2CppRuntimeException" />
    ///     on the host side.
    /// </summary>
    /// <remarks>
    ///     <paramref name="args" /> are raw pointer-sized values and must already
    ///     be boxed where the method signature calls for a reference type.  For
    ///     value types you pass a pointer to the value.  For reference types
    ///     (including <c>System.String</c> and IL2CPP objects) you pass the
    ///     object pointer directly — <c>il2cpp_runtime_invoke</c> expects a
    ///     <c>void*[]</c> of pointers.  A zero-length <paramref name="args" /> is
    ///     fine.
    /// </remarks>
    public ulong SafeInvoke(IntPtr methodInfo, IntPtr thisPtr, params IntPtr[] args)
    {
        if (methodInfo == IntPtr.Zero)
            throw new ArgumentException("methodInfo is null — did ResolveMethod fail?", nameof(methodInfo));

        var argsBuf = IntPtr.Zero;
        var argBufSize = args.Length * IntPtr.Size;
        if (argBufSize > 0)
        {
            argsBuf = _proc.Allocate(argBufSize);
            var packed = new byte[argBufSize];
            for (var i = 0; i < args.Length; i++)
            {
                var v = (long)args[i];
                for (var b = 0; b < 8; b++) packed[i * 8 + b] = (byte)(v >> (b * 8));
            }

            _proc.Write(argsBuf, packed);
        }

        try
        {
            var (code, retSlotOffset, excSlotOffset) =
                BuildSafeInvokeShellcode(methodInfo, thisPtr, argsBuf);
            var remote = _proc.Allocate(code.Length, true);
            try
            {
                _proc.Write(remote, code);
                _proc.CallRemote(remote, IntPtr.Zero);

                var retVal = _proc.ReadU64(remote + retSlotOffset);
                var excPtr = (IntPtr)_proc.ReadU64(remote + excSlotOffset);
                if (excPtr != IntPtr.Zero)
                {
                    // Il2CppException matches System.Exception's layout:
                    //   +0x18 _message (string*)
                    // Read the message (best-effort — swallow if layout surprises us).
                    string? msg = null;
                    try
                    {
                        var msgObj = _proc.ReadPtr(excPtr + 0x18);
                        msg = _proc.ReadIl2CppString(msgObj);
                    }
                    catch
                    {
                        /* leave msg null */
                    }

                    throw new Il2CppRuntimeException(
                        $"IL2CPP method raised: {msg ?? "(no message)"}");
                }

                return retVal;
            }
            finally
            {
                _proc.Free(remote);
            }
        }
        finally
        {
            if (argsBuf != IntPtr.Zero) _proc.Free(argsBuf);
        }
    }

    private IntPtr WriteRemoteCString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var buf = _proc.Allocate(bytes.Length + 1);
        var tmp = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, tmp, 0, bytes.Length);
        _proc.Write(buf, tmp);
        return buf;
    }

    // =====================================================================
    //  Resolve-method shellcode (see ResolveMethod above for the pseudo-code).
    //  Returns MethodInfo* via retSlot (0 = not found).
    //
    //  Stack layout (rbp-relative, 8-byte slots):
    //      [rbp-0x08] domain
    //      [rbp-0x10] asms   (Il2CppAssembly**)
    //      [rbp-0x18] n      (size_t from sizeSlot)
    //      [rbp-0x20] i
    // =====================================================================
    private (byte[] code, int retSlotOffset) BuildResolveMethodShellcode(
        IntPtr nsBuf, IntPtr clsBuf, IntPtr mtdBuf, int argc)
    {
        var c = new Assembler(64);
        var retSlot = c.CreateLabel("retSlot");
        var sizeSlot = c.CreateLabel("sizeSlot");
        var loopTop = c.CreateLabel("loopTop");
        var loopNext = c.CreateLabel("loopNext");
        var loopEnd = c.CreateLabel("loopEnd");
        var done = c.CreateLabel("done");

        c.push(rbp);
        c.mov(rbp, rsp);
        c.sub(rsp, 0x40);

        // domain = il2cpp_domain_get()
        c.mov(rax, (ulong)Il2CppDomainGet);
        c.call(rax);
        c.mov(__[rbp - 0x08], rax);

        // il2cpp_thread_attach(domain)
        c.mov(rcx, __[rbp - 0x08]);
        c.mov(rax, (ulong)Il2CppThreadAttach);
        c.call(rax);

        // Zero sizeSlot.
        c.lea(r10, __[sizeSlot]);
        c.xor(rax, rax);
        c.mov(__[r10], rax);

        // asms = il2cpp_domain_get_assemblies(domain, &sizeSlot)
        c.mov(rcx, __[rbp - 0x08]);
        c.lea(rdx, __[sizeSlot]);
        c.mov(rax, (ulong)Il2CppDomainGetAssemblies);
        c.call(rax);
        c.mov(__[rbp - 0x10], rax);

        // n = *sizeSlot
        c.lea(r10, __[sizeSlot]);
        c.mov(rax, __[r10]);
        c.mov(__[rbp - 0x18], rax);

        // i = 0
        c.xor(rax, rax);
        c.mov(__[rbp - 0x20], rax);

        c.Label(ref loopTop);
        // if (i >= n) goto loopEnd
        c.mov(rax, __[rbp - 0x20]);
        c.cmp(rax, __[rbp - 0x18]);
        c.jae(loopEnd);

        // asm = asms[i]
        c.mov(rcx, __[rbp - 0x10]);
        c.mov(rax, __[rbp - 0x20]);
        c.mov(rcx, __[rcx + rax * 8]);
        c.test(rcx, rcx);
        c.je(loopNext);

        // img = il2cpp_assembly_get_image(asm)
        c.mov(rax, (ulong)Il2CppAssemblyGetImage);
        c.call(rax);
        c.test(rax, rax);
        c.je(loopNext);

        // cls = il2cpp_class_from_name(img, nsBuf, clsBuf)
        c.mov(rcx, rax);
        c.mov(rdx, (ulong)nsBuf);
        c.mov(r8, (ulong)clsBuf);
        c.mov(rax, (ulong)Il2CppClassFromName);
        c.call(rax);
        c.test(rax, rax);
        c.je(loopNext);

        // m = il2cpp_class_get_method_from_name(cls, mtdBuf, argc)
        c.mov(rcx, rax);
        c.mov(rdx, (ulong)mtdBuf);
        c.mov(r8d, (uint)argc);
        c.mov(rax, (ulong)Il2CppClassGetMethodFromName);
        c.call(rax);
        c.test(rax, rax);
        c.je(loopNext);

        // Found: retSlot = m; goto done
        c.mov(r11, rax);
        c.lea(r10, __[retSlot]);
        c.mov(__[r10], r11);
        c.jmp(done);

        c.Label(ref loopNext);
        c.mov(rax, __[rbp - 0x20]);
        c.inc(rax);
        c.mov(__[rbp - 0x20], rax);
        c.jmp(loopTop);

        c.Label(ref loopEnd);
        c.lea(r10, __[retSlot]);
        c.xor(rax, rax);
        c.mov(__[r10], rax);

        c.Label(ref done);
        c.add(rsp, 0x40);
        c.pop(rbp);
        c.xor(eax, eax);
        c.ret();

        // Data slots.  retSlot must come LAST (matches bytes.Length-8 convention).
        c.Label(ref sizeSlot);
        c.dq(0UL);
        c.Label(ref retSlot);
        c.dq(0UL);

        using var ms = new MemoryStream();
        c.Assemble(new StreamCodeWriter(ms), 0);
        var bytes = ms.ToArray();
        return (bytes, bytes.Length - 8);
    }

    // =====================================================================
    //  SafeInvoke shellcode — wraps il2cpp_runtime_invoke so managed
    //  exceptions don't unwind through our SEH-less trampoline.  Signature:
    //
    //      void* il2cpp_runtime_invoke(
    //          const MethodInfo* method, void* obj,
    //          void** params, Il2CppException** exc);
    //
    //  Two data slots at the tail: excSlot, then retSlot (retSlot last).
    // =====================================================================
    private (byte[] code, int retSlotOffset, int excSlotOffset) BuildSafeInvokeShellcode(
        IntPtr methodInfo, IntPtr thisPtr, IntPtr argsBuf)
    {
        var c = new Assembler(64);
        var retSlot = c.CreateLabel("retSlot");
        var excSlot = c.CreateLabel("excSlot");

        c.push(rbp);
        c.mov(rbp, rsp);
        c.sub(rsp, 0x40);

        // il2cpp_thread_attach(il2cpp_domain_get())
        c.mov(rax, (ulong)Il2CppDomainGet);
        c.call(rax);
        c.mov(rcx, rax);
        c.mov(rax, (ulong)Il2CppThreadAttach);
        c.call(rax);

        // Zero excSlot (runtime only writes on throw).
        c.lea(r10, __[excSlot]);
        c.xor(rax, rax);
        c.mov(__[r10], rax);

        // rcx=methodInfo, rdx=thisPtr, r8=argsBuf, r9=&excSlot
        c.mov(rcx, (ulong)methodInfo);
        c.mov(rdx, (ulong)thisPtr);
        c.mov(r8, (ulong)argsBuf);
        c.lea(r9, __[excSlot]);
        c.mov(rax, (ulong)Il2CppRuntimeInvoke);
        c.call(rax);

        // retSlot = rax
        c.mov(r11, rax);
        c.lea(r10, __[retSlot]);
        c.mov(__[r10], r11);

        c.add(rsp, 0x40);
        c.pop(rbp);
        c.xor(eax, eax);
        c.ret();

        // excSlot then retSlot (retSlot last, offset = bytes.Length-8).
        c.Label(ref excSlot);
        c.dq(0UL);
        c.Label(ref retSlot);
        c.dq(0UL);

        using var ms = new MemoryStream();
        c.Assemble(new StreamCodeWriter(ms), 0);
        var bytes = ms.ToArray();
        var retOff = bytes.Length - 8;
        var excOff = bytes.Length - 16;
        return (bytes, retOff, excOff);
    }
}

/// <summary>
///     Thrown on the host side when <see cref="RemoteInvoker.SafeInvoke" /> detects
///     that the wrapped IL2CPP method raised a managed exception.  The runtime
///     caught it (so the game did not crash) — we just re-raise on our side so
///     the caller can handle it like any other failure.
/// </summary>
public sealed class Il2CppRuntimeException : Exception
{
    public Il2CppRuntimeException(string message) : base(message)
    {
    }
}