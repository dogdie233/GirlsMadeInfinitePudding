using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Threading;

using Microsoft.Win32.SafeHandles;

namespace GirlsMadeInfinitePudding.ProcessMemory;

/// <summary>
/// Attached handle to the running game.  Also locates GameAssembly.dll so
/// callers can turn IDA RVAs into absolute pointers, and exposes primitive
/// read/write + remote-call helpers.
/// </summary>
public sealed class GameProcess : IDisposable
{
    private const string ProcessName = "girls-made-pudding";
    private const string ModuleName  = "GameAssembly.dll";
    /// <summary>Image base recorded in the IDB.  Used to translate RVAs.</summary>
    public const ulong IdaImageBase = 0x180000000UL;

    public SafeProcessHandle Handle { get; }
    public int Pid { get; }

    /// <summary>Runtime base of GameAssembly.dll inside the game process.</summary>
    public IntPtr GameAssemblyBase { get; }
    public uint   GameAssemblySize { get; }

    private GameProcess(SafeProcessHandle h, int pid, IntPtr modBase, uint modSize)
    {
        Handle = h; Pid = pid; GameAssemblyBase = modBase; GameAssemblySize = modSize;
    }

    public static GameProcess Attach(string? overrideProcessName = null)
    {
        var name = overrideProcessName ?? ProcessName;
        var procs = Process.GetProcessesByName(name);
        if (procs.Length == 0)
            throw new InvalidOperationException($"Process '{name}.exe' is not running.");
        var proc = procs[0];
        foreach (var extra in procs.Skip(1)) extra.Dispose();

        const PROCESS_ACCESS_RIGHTS access =
            PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ  |
            PROCESS_ACCESS_RIGHTS.PROCESS_VM_WRITE |
            PROCESS_ACCESS_RIGHTS.PROCESS_VM_OPERATION |
            PROCESS_ACCESS_RIGHTS.PROCESS_CREATE_THREAD |
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION;

        var raw = PInvoke.OpenProcess_SafeHandle(access, false, (uint)proc.Id);
        if (raw.IsInvalid)
            throw new InvalidOperationException(
                $"OpenProcess failed ({Marshal.GetLastWin32Error()}). Try running as Administrator.");
        var safe = new SafeProcessHandle(raw.DangerousGetHandle(), true);
        raw.SetHandleAsInvalid();

        if (!TryFindModule(proc.Id, ModuleName, out var modBase, out var modSize))
        {
            safe.Dispose();
            throw new InvalidOperationException($"Module '{ModuleName}' not loaded in target process.");
        }

        return new GameProcess(safe, proc.Id, modBase, modSize);
    }

    /// <summary>Convert an IDA RVA (or image-based address) to an absolute pointer.</summary>
    public IntPtr ResolveRva(ulong idaAddress)
    {
        ulong rva = idaAddress >= IdaImageBase ? idaAddress - IdaImageBase : idaAddress;
        return checked((IntPtr)((ulong)GameAssemblyBase + rva));
    }

    // ---- raw R/W helpers ----------------------------------------------------
    public unsafe void Read(IntPtr address, Span<byte> dest)
    {
        nuint read;
        fixed (byte* p = dest)
        {
            if (!PInvoke.ReadProcessMemory(ProcHandle, (void*)address, p, (nuint)dest.Length, &read)
                || read != (nuint)dest.Length)
                throw new InvalidOperationException(
                    $"ReadProcessMemory failed @ 0x{(long)address:X} len={dest.Length} " +
                    $"(err {Marshal.GetLastWin32Error()})");
        }
    }

    public unsafe void Write(IntPtr address, ReadOnlySpan<byte> src)
    {
        nuint written;
        fixed (byte* p = src)
        {
            if (!PInvoke.WriteProcessMemory(ProcHandle, (void*)address, p, (nuint)src.Length, &written)
                || written != (nuint)src.Length)
                throw new InvalidOperationException(
                    $"WriteProcessMemory failed @ 0x{(long)address:X} len={src.Length} " +
                    $"(err {Marshal.GetLastWin32Error()})");
        }
    }

    public byte   ReadU8 (IntPtr a) { Span<byte> b = stackalloc byte[1]; Read(a, b); return b[0]; }
    public int    ReadI32(IntPtr a) { Span<byte> b = stackalloc byte[4]; Read(a, b); return BitConverter.ToInt32(b); }
    public uint   ReadU32(IntPtr a) { Span<byte> b = stackalloc byte[4]; Read(a, b); return BitConverter.ToUInt32(b); }
    public long   ReadI64(IntPtr a) { Span<byte> b = stackalloc byte[8]; Read(a, b); return BitConverter.ToInt64(b); }
    public ulong  ReadU64(IntPtr a) { Span<byte> b = stackalloc byte[8]; Read(a, b); return BitConverter.ToUInt64(b); }
    public IntPtr ReadPtr(IntPtr a) => (IntPtr)ReadI64(a);

    public void WriteI32(IntPtr a, int v)    => Write(a, BitConverter.GetBytes(v));
    public void WriteU32(IntPtr a, uint v)   => Write(a, BitConverter.GetBytes(v));
    public void WriteU64(IntPtr a, ulong v)  => Write(a, BitConverter.GetBytes(v));
    public void WritePtr(IntPtr a, IntPtr v) => WriteU64(a, (ulong)(long)v);

    /// <summary>
    /// IL2CPP System.String layout (64-bit):
    ///   0x00 klass, 0x08 monitor, 0x10 int length, 0x14 UTF-16 chars
    /// </summary>
    public string? ReadIl2CppString(IntPtr stringObj)
    {
        if (stringObj == IntPtr.Zero) return null;
        int length = ReadI32(stringObj + 0x10);
        if (length <= 0)      return string.Empty;
        if (length > 0x10000) return $"<invalid len={length}>";
        var buf = new byte[length * 2];
        Read(stringObj + 0x14, buf);
        return Encoding.Unicode.GetString(buf);
    }

    // ---- remote allocation / thread ------------------------------------------
    public unsafe IntPtr Allocate(int size, bool executable = false)
    {
        var protect = executable ? PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READWRITE
                                 : PAGE_PROTECTION_FLAGS.PAGE_READWRITE;
        var p = PInvoke.VirtualAllocEx(ProcHandle, null, (nuint)size,
                                       VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT |
                                       VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE,
                                       protect);
        if (p == null)
            throw new InvalidOperationException(
                $"VirtualAllocEx failed (err {Marshal.GetLastWin32Error()})");
        return (IntPtr)p;
    }

    public unsafe void Free(IntPtr address)
    {
        if (address != IntPtr.Zero)
            PInvoke.VirtualFreeEx(ProcHandle, (void*)address, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
    }

    /// <summary>
    /// CreateRemoteThread → WaitForSingleObject; returns the 32-bit thread
    /// exit code.  Real return values should be stashed into a scratch slot
    /// by the shellcode (thread exit codes only give you 32 bits).
    /// </summary>
    public uint CallRemote(IntPtr entry, IntPtr arg, int timeoutMs = 10_000)
    {
        var hProc   = Handle.DangerousGetHandle();
        var hThread = CreateRemoteThread(hProc, IntPtr.Zero, IntPtr.Zero,
                                         entry, arg, 0, IntPtr.Zero);
        if (hThread == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateRemoteThread failed (err {Marshal.GetLastWin32Error()})");
        try
        {
            var h = new HANDLE(hThread);
            var wait = PInvoke.WaitForSingleObject(h, (uint)timeoutMs);
            if (wait != WAIT_EVENT.WAIT_OBJECT_0)
                throw new InvalidOperationException($"Remote thread did not finish (wait={wait}).");
            if (!GetExitCodeThreadNative(hThread, out uint ec))
                throw new InvalidOperationException(
                    $"GetExitCodeThread failed (err {Marshal.GetLastWin32Error()})");
            return ec;
        }
        finally { CloseHandleNative(hThread); }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetExitCodeThread", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThreadNative(IntPtr hThread, out uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandleNative(IntPtr handle);

    // CsWin32's generated CreateRemoteThread overload has a delegate parameter
    // that's awkward to construct from a raw function pointer without an
    // actual managed delegate.  A hand-written P/Invoke is simpler.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess,
                                                    IntPtr threadAttributes,
                                                    IntPtr stackSize,
                                                    IntPtr startAddress,
                                                    IntPtr parameter,
                                                    uint creationFlags,
                                                    IntPtr threadId);

    public void Dispose()
    {
        Handle.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------------
    private HANDLE ProcHandle => new HANDLE(Handle.DangerousGetHandle());

    // ---- module enumeration -------------------------------------------------
    private static unsafe bool TryFindModule(int pid, string needle, out IntPtr baseAddr, out uint size)
    {
        baseAddr = IntPtr.Zero;
        size     = 0;
        using var snap = PInvoke.CreateToolhelp32Snapshot_SafeHandle(
            CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPMODULE |
            CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPMODULE32, (uint)pid);
        if (snap.IsInvalid) return false;

        var me = new MODULEENTRY32W { dwSize = (uint)sizeof(MODULEENTRY32W) };
        if (!PInvoke.Module32FirstW(snap, ref me)) return false;
        do
        {
            if (me.szModule.AsReadOnlySpan().SequenceEqual(needle) ||
                string.Equals(me.szModule.ToString(), needle, StringComparison.OrdinalIgnoreCase))
            {
                baseAddr = (IntPtr)me.modBaseAddr;
                size     = me.modBaseSize;
                return true;
            }
        } while (PInvoke.Module32NextW(snap, ref me));
        return false;
    }
}
