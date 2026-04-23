using System.Text;
using GirlsMadeInfinitePudding.ProcessMemory;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace GirlsMadeInfinitePudding.GameAbi;

/// <summary>
/// Executes arbitrary IL2CPP / native calls inside the game process by
/// assembling an x86-64 shellcode trampoline with <see cref="Assembler"/>
/// (the Iced library) and invoking it via <c>CreateRemoteThread</c>.
/// <para>
/// The remote stub attaches the new thread to the IL2CPP domain once
/// (so managed runtime state is safe to touch from it), sets up the
/// Win64 calling-convention registers with the user-supplied args, calls
/// the target, stores RAX into a "return slot" appended after the code,
/// and returns.  The parent process then reads the slot with
/// <c>ReadProcessMemory</c> to recover the real return value.
/// </para>
/// </summary>
public sealed class RemoteInvoker
{
    private readonly GameProcess _proc;
    public IntPtr Il2CppThreadAttach { get; }
    public IntPtr Il2CppThreadDetach { get; }
    public IntPtr Il2CppDomainGet    { get; }
    public IntPtr Il2CppStringNew    { get; }
    public IntPtr Il2CppObjectNew    { get; }
    public IntPtr Il2CppRuntimeInvoke{ get; }
    public IntPtr Il2CppArrayNew     { get; }

    public RemoteInvoker(GameProcess proc, RemoteExportResolver exports)
    {
        _proc              = proc;
        Il2CppThreadAttach = exports["il2cpp_thread_attach"];
        Il2CppThreadDetach = exports["il2cpp_thread_detach"];
        Il2CppDomainGet    = exports["il2cpp_domain_get"];
        Il2CppStringNew    = exports["il2cpp_string_new"];
        Il2CppObjectNew    = exports["il2cpp_object_new"];
        Il2CppRuntimeInvoke= exports["il2cpp_runtime_invoke"];
        Il2CppArrayNew     = exports["il2cpp_array_new"];
    }

    /// <summary>
    /// Low-level: call an arbitrary native function (Win64 fastcall, up to
    /// four pointer-sized args) in the remote process and return RAX.
    /// The thread attaches to the IL2CPP domain first so managed runtime
    /// code is safe to execute.
    /// </summary>
    public ulong InvokeNative(IntPtr target, IntPtr a0 = default, IntPtr a1 = default,
                              IntPtr a2 = default, IntPtr a3 = default, int timeoutMs = 10000)
    {
        var (code, retSlotOffset) = BuildShellcode(
            target, (ulong)a0, (ulong)a1, (ulong)a2, (ulong)a3);
        var remote = _proc.Allocate(code.Length, executable: true);
        try
        {
            _proc.Write(remote, code);
            _proc.CallRemote(remote, IntPtr.Zero, timeoutMs);
            return _proc.ReadU64(remote + retSlotOffset);
        }
        finally { _proc.Free(remote); }
    }

    /// <summary>Allocate a C-string in the remote process and return an il2cpp System.String* pointer.</summary>
    public IntPtr NewIl2CppString(string value)
    {
        // Push the UTF-8 bytes into a scratch buffer, then call il2cpp_string_new.
        var bytes = Encoding.UTF8.GetBytes(value);
        var buf   = _proc.Allocate(bytes.Length + 1);
        try
        {
            var tmp = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, tmp, 0, bytes.Length);
            _proc.Write(buf, tmp);
            ulong r = InvokeNative(Il2CppStringNew, buf);
            return (IntPtr)r;
        }
        finally { _proc.Free(buf); }
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
        var c = new Assembler(bitness: 64);
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
        c.mov(r8,  a2);
        c.mov(r9,  a3);
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

        using var ms  = new MemoryStream();
        var writer    = new StreamCodeWriter(ms);
        c.Assemble(writer, rip: 0);
        var bytes = ms.ToArray();

        // Compute retSlot's byte offset by re-asking the assembler where
        // that label sits.  Iced doesn't directly expose label IPs after a
        // single Assemble(), so we cheat: the dq qword is the last 8 bytes
        // we emitted, so retSlot == bytes.Length - 8.
        int retSlotOffset = bytes.Length - 8;
        return (bytes, retSlotOffset);
    }
}
