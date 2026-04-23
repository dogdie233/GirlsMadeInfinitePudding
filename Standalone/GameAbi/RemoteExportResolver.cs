using System.Runtime.InteropServices;
using System.Text;
using GirlsMadeInfinitePudding.ProcessMemory;

namespace GirlsMadeInfinitePudding.GameAbi;

/// <summary>
/// Reads the PE export directory of a module loaded in the remote process so
/// we can get the runtime addresses of <c>il2cpp_*</c> C-API functions by
/// name.  This avoids hard-coding tons of RVAs that might shift between
/// builds.
/// </summary>
public sealed class RemoteExportResolver
{
    private readonly GameProcess _proc;
    private readonly IntPtr _moduleBase;
    private readonly Dictionary<string, IntPtr> _exports = new(StringComparer.Ordinal);

    public RemoteExportResolver(GameProcess proc, IntPtr moduleBase)
    {
        _proc       = proc;
        _moduleBase = moduleBase;
        Parse();
    }

    public IntPtr this[string name] =>
        _exports.TryGetValue(name, out var ea)
            ? ea
            : throw new InvalidOperationException($"Export '{name}' not found in module.");

    public bool Has(string name) => _exports.ContainsKey(name);

    private void Parse()
    {
        // DOS header
        Span<byte> dos = stackalloc byte[64];
        _proc.Read(_moduleBase, dos);
        if (dos[0] != 'M' || dos[1] != 'Z')
            throw new InvalidOperationException("Target module is not a PE (bad DOS signature).");
        int e_lfanew = BitConverter.ToInt32(dos[0x3C..0x40]);

        // NT headers (signature + COFF file header + optional header)
        Span<byte> nt = stackalloc byte[0x108];
        _proc.Read(_moduleBase + e_lfanew, nt);
        if (nt[0] != 'P' || nt[1] != 'E' || nt[2] != 0 || nt[3] != 0)
            throw new InvalidOperationException("Bad NT signature.");

        // IMAGE_OPTIONAL_HEADER64 starts at +0x18 inside NT headers.
        // Magic (0x20b = PE32+)
        ushort magic = BitConverter.ToUInt16(nt.Slice(0x18, 2));
        if (magic != 0x20B)
            throw new InvalidOperationException($"Not PE32+ (magic 0x{magic:X}).");
        // DataDirectory[0] = Export
        int exportRva  = BitConverter.ToInt32(nt.Slice(0x18 + 0x70 + 0, 4));
        int exportSize = BitConverter.ToInt32(nt.Slice(0x18 + 0x70 + 4, 4));
        if (exportRva == 0 || exportSize == 0) return;

        // IMAGE_EXPORT_DIRECTORY @ moduleBase + exportRva (40 bytes)
        Span<byte> ed = stackalloc byte[40];
        _proc.Read(_moduleBase + exportRva, ed);
        uint numberOfFunctions = BitConverter.ToUInt32(ed[0x14..0x18]);
        uint numberOfNames     = BitConverter.ToUInt32(ed[0x18..0x1C]);
        uint addrOfFunctions   = BitConverter.ToUInt32(ed[0x1C..0x20]);
        uint addrOfNames       = BitConverter.ToUInt32(ed[0x20..0x24]);
        uint addrOfNameOrdinals= BitConverter.ToUInt32(ed[0x24..0x28]);

        // Read all three tables in one shot.
        var funcs    = ReadU32Array(_moduleBase + (int)addrOfFunctions,   (int)numberOfFunctions);
        var names    = ReadU32Array(_moduleBase + (int)addrOfNames,       (int)numberOfNames);
        var ordinals = ReadU16Array(_moduleBase + (int)addrOfNameOrdinals,(int)numberOfNames);

        for (int i = 0; i < numberOfNames; i++)
        {
            string name = ReadCString(_moduleBase + (int)names[i]);
            ushort ord  = ordinals[i];
            if (ord >= funcs.Length) continue;
            uint funcRva = funcs[ord];
            // Forwarded exports point into the export data-dir range; skip them.
            bool forwarded = funcRva >= exportRva && funcRva < exportRva + exportSize;
            if (forwarded) continue;
            _exports[name] = _moduleBase + (int)funcRva;
        }
    }

    private uint[] ReadU32Array(IntPtr ea, int count)
    {
        var bytes = new byte[count * 4];
        _proc.Read(ea, bytes);
        var r = new uint[count];
        Buffer.BlockCopy(bytes, 0, r, 0, bytes.Length);
        return r;
    }
    private ushort[] ReadU16Array(IntPtr ea, int count)
    {
        var bytes = new byte[count * 2];
        _proc.Read(ea, bytes);
        var r = new ushort[count];
        Buffer.BlockCopy(bytes, 0, r, 0, bytes.Length);
        return r;
    }
    private string ReadCString(IntPtr ea)
    {
        var buf = new byte[256];
        _proc.Read(ea, buf);
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = buf.Length;
        return Encoding.ASCII.GetString(buf, 0, len);
    }
}
