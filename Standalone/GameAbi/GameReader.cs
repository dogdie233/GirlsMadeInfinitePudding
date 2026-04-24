using GirlsMadeInfinitePudding.ProcessMemory;

namespace GirlsMadeInfinitePudding.GameAbi;

public readonly record struct ItemInfo(
    IntPtr DataPtr,
    string Id,
    int Type,
    int Tier,
    int Priority,
    bool Valid,
    string Name,
    UnityColorImageSource? ImageSource);

/// <summary>
///     Pure-read helpers over the game's object graph.  These don't require a
///     remote call — just ReadProcessMemory, which is very cheap and can be
///     polled continuously for live UI updates.
/// </summary>
public sealed class GameReader
{
    private readonly GameProcess _proc;

    public GameReader(GameProcess proc)
    {
        _proc = proc;
    }

    // ---- IL2CPP List<T> + Array<T> primitives --------------------------------
    public int ReadListSize(IntPtr list)
    {
        return list == IntPtr.Zero ? 0 : _proc.ReadI32(list + Offsets.List.Size);
    }

    public IntPtr ReadListItemsArray(IntPtr list)
    {
        return list == IntPtr.Zero ? IntPtr.Zero : _proc.ReadPtr(list + Offsets.List.Items);
    }

    public int ReadArrayLength(IntPtr arr)
    {
        return arr == IntPtr.Zero ? 0 : (int)_proc.ReadU64(arr + Offsets.Array.MaxLength);
    }

    public IntPtr ReadArrayElement(IntPtr arr, int index)
    {
        return _proc.ReadPtr(arr + Offsets.Array.FirstElem + index * IntPtr.Size);
    }

    // ---- ItemData readers ----------------------------------------------------
    public ItemInfo ReadItemData(IntPtr itemData)
    {
        if (itemData == IntPtr.Zero) return default;
        var valid = _proc.ReadU8(itemData + Offsets.ItemData.Valid) != 0;
        var idPtr = _proc.ReadPtr(itemData + Offsets.ItemData.Id);
        var id = _proc.ReadIl2CppString(idPtr) ?? "";
        var type = _proc.ReadI32(itemData + Offsets.ItemData.Type);
        var tier = _proc.ReadI32(itemData + Offsets.ItemData.Tier);
        var prio = _proc.ReadI32(itemData + Offsets.ItemData.Priority);
        return new ItemInfo(itemData, id, type, tier, prio, valid, "(Not Loaded)", null);
    }

    // ---- Inventory readers ---------------------------------------------------
    public int ReadItemCount(IntPtr inventory)
    {
        return ReadListSize(_proc.ReadPtr(inventory + Offsets.Inventory.Items));
    }

    public int ReadKeyItemCount(IntPtr inventory)
    {
        return ReadListSize(_proc.ReadPtr(inventory + Offsets.Inventory.KeyItems));
    }

    public int ReadCountMax(IntPtr inventory)
    {
        return _proc.ReadI32(inventory + Offsets.Inventory.CountMax);
    }

    /// <summary>Snapshot all ItemContexts in Inventory._Items (or _KeyItems).</summary>
    public List<ItemInfo> ReadInventoryList(IntPtr inventory, bool keyItems)
    {
        var list = _proc.ReadPtr(inventory +
                                 (keyItems ? Offsets.Inventory.KeyItems : Offsets.Inventory.Items));
        var size = ReadListSize(list);
        var arr = ReadListItemsArray(list);
        var r = new List<ItemInfo>(size);
        for (var i = 0; i < size; i++)
        {
            var ctx = ReadArrayElement(arr, i);
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
        if (itemBankHolder == IntPtr.Zero) return new List<ItemInfo>();
        var bank = _proc.ReadPtr(itemBankHolder + Offsets.ItemBankHolder.ItemBank);
        if (bank == IntPtr.Zero) return new List<ItemInfo>();
        var list = _proc.ReadPtr(bank + Offsets.ItemBank.ItemList);
        if (list == IntPtr.Zero) return new List<ItemInfo>();
        var len = ReadArrayLength(list);
        var r = new List<ItemInfo>(len);
        for (var i = 0; i < len; i++)
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