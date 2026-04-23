using GirlsMadeInfinitePudding.ProcessMemory;

namespace GirlsMadeInfinitePudding.GameAbi;

public readonly record struct ItemInfo(IntPtr DataPtr, string Id, int Type, int Tier, int Priority, bool Valid);

/// <summary>
/// Pure-read helpers over the game's object graph.  These don't require a
/// remote call — just ReadProcessMemory, which is very cheap and can be
/// polled continuously for live UI updates.
/// </summary>
public sealed class GameReader
{
    private readonly GameProcess _proc;
    public GameReader(GameProcess proc) => _proc = proc;

    // ---- IL2CPP List<T> + Array<T> primitives --------------------------------
    public int ReadListSize(IntPtr list) => list == IntPtr.Zero ? 0 : _proc.ReadI32(list + Offsets.List.Size);

    public IntPtr ReadListItemsArray(IntPtr list) =>
        list == IntPtr.Zero ? IntPtr.Zero : _proc.ReadPtr(list + Offsets.List.Items);

    public int ReadArrayLength(IntPtr arr) =>
        arr == IntPtr.Zero ? 0 : (int)_proc.ReadU64(arr + Offsets.Array.MaxLength);

    public IntPtr ReadArrayElement(IntPtr arr, int index) =>
        _proc.ReadPtr(arr + Offsets.Array.FirstElem + index * IntPtr.Size);

    // ---- ItemData readers ----------------------------------------------------
    public ItemInfo ReadItemData(IntPtr itemData)
    {
        if (itemData == IntPtr.Zero) return default;
        bool valid = _proc.ReadU8(itemData + Offsets.ItemData.Valid) != 0;
        var idPtr  = _proc.ReadPtr(itemData + Offsets.ItemData.Id);
        string id  = _proc.ReadIl2CppString(idPtr) ?? "";
        int type   = _proc.ReadI32(itemData + Offsets.ItemData.Type);
        int tier   = _proc.ReadI32(itemData + Offsets.ItemData.Tier);
        int prio   = _proc.ReadI32(itemData + Offsets.ItemData.Priority);
        return new ItemInfo(itemData, id, type, tier, prio, valid);
    }

    // ---- Inventory readers ---------------------------------------------------
    public int  ReadItemCount(IntPtr inventory) =>
        ReadListSize(_proc.ReadPtr(inventory + Offsets.Inventory.Items));
    public int  ReadKeyItemCount(IntPtr inventory) =>
        ReadListSize(_proc.ReadPtr(inventory + Offsets.Inventory.KeyItems));
    public int  ReadCountMax(IntPtr inventory) =>
        _proc.ReadI32(inventory + Offsets.Inventory.CountMax);

    /// <summary>Snapshot all ItemContexts in Inventory._Items (or _KeyItems).</summary>
    public List<ItemInfo> ReadInventoryList(IntPtr inventory, bool keyItems)
    {
        var list = _proc.ReadPtr(inventory +
                                 (keyItems ? Offsets.Inventory.KeyItems : Offsets.Inventory.Items));
        int size = ReadListSize(list);
        var arr  = ReadListItemsArray(list);
        var r    = new List<ItemInfo>(size);
        for (int i = 0; i < size; i++)
        {
            var ctx  = ReadArrayElement(arr, i);
            if (ctx == IntPtr.Zero) continue;
            var data = _proc.ReadPtr(ctx + Offsets.ItemContext.ItemData);
            r.Add(ReadItemData(data));
        }
        return r;
    }

    // ---- ItemBank dump -------------------------------------------------------
    /// <summary>Walk ItemBankHolder._ItemBank._ItemList and return every ItemData definition.</summary>
    public List<ItemInfo> ReadAllItems(IntPtr itemBankHolder)
    {
        if (itemBankHolder == IntPtr.Zero) return new();
        var bank = _proc.ReadPtr(itemBankHolder + Offsets.ItemBankHolder.ItemBank);
        if (bank == IntPtr.Zero) return new();
        var list = _proc.ReadPtr(bank + Offsets.ItemBank.ItemList);
        if (list == IntPtr.Zero) return new();
        int len  = ReadArrayLength(list);
        var r    = new List<ItemInfo>(len);
        for (int i = 0; i < len; i++)
        {
            var data = ReadArrayElement(list, i);
            if (data == IntPtr.Zero) continue;
            var info = ReadItemData(data);
            if (!info.Valid || string.IsNullOrEmpty(info.Id)) continue;
            r.Add(info);
        }
        return r;
    }
}
