using GirlsMadeInfinitePudding.ProcessMemory;

namespace GirlsMadeInfinitePudding.GameAbi;

/// <summary>
/// High-level "sitting on top of a live game" API: exposes singleton
/// access, item-bank queries, and the mutation primitives the UI will
/// wire up (add food, remove food, fill inventory, clear inventory, ...).
/// </summary>
public sealed class GameSession : IDisposable
{
    public GameProcess          Proc    { get; }
    public RemoteExportResolver Exports { get; }
    public RemoteInvoker        Invoker { get; }
    public GameReader           Reader  { get; }

    /// <summary>Cached <c>Inventory</c> GlobalBehaviour instance.</summary>
    public IntPtr Inventory       { get; private set; } = IntPtr.Zero;
    /// <summary>Cached <c>ItemBankHolder</c> GlobalBehaviour instance.</summary>
    public IntPtr ItemBankHolder  { get; private set; } = IntPtr.Zero;
    /// <summary>Cached <c>StoryManager</c> GlobalBehaviour instance (optional).</summary>
    public IntPtr StoryManager    { get; private set; } = IntPtr.Zero;

    public GameSession(GameProcess proc)
    {
        Proc    = proc;
        Exports = new RemoteExportResolver(proc, proc.GameAssemblyBase);
        Invoker = new RemoteInvoker(proc, Exports);
        Reader  = new GameReader(proc);
    }

    /// <summary>
    /// Resolve the three singletons we care about.  Must be called after the
    /// game's main scene is loaded - in practice once <c>HomeFlow</c> or
    /// later is active all three GlobalBehaviours are registered.
    /// </summary>
    public void RefreshSingletons()
    {
        Inventory      = GetSingleton(Offsets.Slot_Method_GlobalAccess_get_Inventory);
        ItemBankHolder = GetSingleton(Offsets.Slot_Method_GlobalAccess_get_ItemBankHolder);
        try { StoryManager = GetSingleton(Offsets.Slot_Method_GlobalAccess_get_StoryManager); }
        catch { /* StoryManager may not be registered until later - non-fatal */ }
    }

    private IntPtr GetSingleton(ulong methodInfoSlotIdaAddr)
    {
        // The slot holds a MethodInfo* once the runtime has initialised it.
        var slot         = Proc.ResolveRva(methodInfoSlotIdaAddr);
        var methodInfo   = Proc.ReadPtr(slot);
        if (methodInfo == IntPtr.Zero)
            throw new InvalidOperationException(
                $"MethodInfo slot 0x{methodInfoSlotIdaAddr:X} is still null — " +
                "is the game fully booted into the main scene?");
        var getObject = Proc.ResolveRva(Offsets.Fn_GlobalAccess_get_object);
        ulong result  = Invoker.InvokeNative(getObject, /*index=*/ IntPtr.Zero, methodInfo);
        return (IntPtr)result;
    }

    // ======================= READ-SIDE ======================================

    public List<ItemInfo> ListAllItems()
    {
        var items = Reader.ReadAllItems(EnsureItemBank());
        for (int i = 0; i < items.Count; i++)
        {
            items[i] = AugmentItemData(items[i]);
        }
        return items;
    }

    public ItemInfo AugmentItemData(ItemInfo info)
    {
        if (info.DataPtr == IntPtr.Zero || !info.Valid) return info;
        
        string name = info.Id;
        UnityColorImageSource? image = null;

        try
        {
            var getNameFn = Proc.ResolveRva(Offsets.Fn_ItemData_get_Name);
            var namePtr = Invoker.InvokeNative(getNameFn, info.DataPtr);
            if (namePtr != 0)
            {
                var s = Proc.ReadIl2CppString((IntPtr)namePtr);
                if (!string.IsNullOrEmpty(s)) name = s;
            }

            // var getImageFn = Proc.ResolveRva(Offsets.Fn_ItemData_get_Image);
            // var texPtr = (IntPtr)Invoker.InvokeNative(getImageFn, info.DataPtr);
            //
            // if (texPtr != IntPtr.Zero)
            // {
            //     var wFn = Proc.ResolveRva(Offsets.Fn_Texture_get_width);
            //     var hFn = Proc.ResolveRva(Offsets.Fn_Texture_get_height);
            //     var pixFn = Proc.ResolveRva(Offsets.Fn_Texture2D_GetPixels32);
            //
            //     int width = (int)Invoker.InvokeNative(wFn, texPtr);
            //     int height = (int)Invoker.InvokeNative(hFn, texPtr);
            //
            //     var arrPtr = (IntPtr)Invoker.InvokeNative(pixFn, texPtr);
            //     if (arrPtr != IntPtr.Zero && width > 0 && height > 0)
            //     {
            //         int expectedSize = width * height;
            //         int arrLen = Reader.ReadArrayLength(arrPtr);
            //         
            //         if (arrLen >= expectedSize)
            //         {
            //             var byteLen = expectedSize * 4;
            //             var bytes = new byte[byteLen];
            //             Proc.Read(arrPtr + Offsets.Array.FirstElem, bytes);
            //             image = new UnityColorImageSource(bytes, width, height);
            //         }
            //     }
            // }
        }
        catch { }

        return info with { Name = name, ImageSource = image };
    }

    public List<ItemInfo> ListFoodBank() =>
        ListAllItems().Where(i => i.Type == (int)Offsets.ItemType.Food).ToList();

    public List<ItemInfo> ListCurrentInventory() =>
        Reader.ReadInventoryList(EnsureInventory(), keyItems: false).Select(AugmentItemData).ToList();

    public List<ItemInfo> ListCurrentKeyItems() =>
        Reader.ReadInventoryList(EnsureInventory(), keyItems: true).Select(AugmentItemData).ToList();

    public int CurrentFoodCount()  => Reader.ReadItemCount(EnsureInventory());
    public int CurrentKeyCount()   => Reader.ReadKeyItemCount(EnsureInventory());
    public int CurrentCountMax()   => Reader.ReadCountMax(EnsureInventory());

    // ======================= WRITE-SIDE =====================================

    /// <summary>Look up an <see cref="ItemInfo"/> by string ID from the ItemBank dump.</summary>
    public ItemInfo? FindItemById(string id)
    {
        foreach (var it in ListAllItems())
            if (string.Equals(it.Id, id, StringComparison.OrdinalIgnoreCase)) return it;
        return null;
    }

    /// <summary>Add N copies of the given food/ItemData to Inventory.</summary>
    /// <param name="over">If true, ignores CountMax (passes over=1 to Inventory::pushItem).</param>
    public void AddItem(IntPtr itemData, int count = 1, bool over = true)
    {
        if (itemData == IntPtr.Zero || count <= 0) return;
        var push = Proc.ResolveRva(Offsets.Fn_Inventory_pushItem_byData);
        var inv  = EnsureInventory();
        for (int i = 0; i < count; i++)
            Invoker.InvokeNative(push, inv, itemData, (IntPtr)(over ? 1 : 0));
    }

    /// <summary>Add by ID (looked up in ItemBank).</summary>
    public void AddItemById(string id, int count = 1, bool over = true)
    {
        var item = FindItemById(id)
                   ?? throw new ArgumentException($"No ItemData with id '{id}' in ItemBank.");
        AddItem(item.DataPtr, count, over);
    }

    /// <summary>Remove <c>count</c> copies of the given food by data pointer (unstacked).</summary>
    public void RemoveItem(IntPtr itemData, int count = 1)
    {
        if (itemData == IntPtr.Zero || count <= 0) return;
        var remove = Proc.ResolveRva(Offsets.Fn_Inventory_removeItem_byData);
        var inv    = EnsureInventory();
        for (int i = 0; i < count; i++)
            Invoker.InvokeNative(remove, inv, itemData);
    }

    public void RemoveItemById(string id, int count = 1)
    {
        var item = FindItemById(id)
                   ?? throw new ArgumentException($"No ItemData with id '{id}' in ItemBank.");
        RemoveItem(item.DataPtr, count);
    }

    /// <summary>Adds a key/tool item (deduped by the game).</summary>
    public void AddKeyItem(IntPtr itemData)
    {
        if (itemData == IntPtr.Zero) return;
        var push = Proc.ResolveRva(Offsets.Fn_Inventory_pushKeyItem_byData);
        Invoker.InvokeNative(push, EnsureInventory(), itemData);
    }

    /// <summary>
    /// Direct write to <c>Inventory._CountMax</c>.  No remote call required
    /// — it's a plain int field.
    /// </summary>
    public void SetBaseCountMax(int newMax)
        => Proc.WriteI32(EnsureInventory() + Offsets.Inventory.CountMax, newMax);

    /// <summary>Quick "fill the bag with food X" helper — bypasses CountMax via over=true.</summary>
    public void FillWithFood(string foodId, int count)
        => AddItemById(foodId, count, over: true);

    /// <summary>Clear every food (keeps key items).</summary>
    public void ClearFood()
    {
        var snapshot = ListCurrentInventory();
        foreach (var item in snapshot)
            RemoveItem(item.DataPtr, 1);
    }

    // ======================= internals ======================================
    private IntPtr EnsureInventory()
        => Inventory != IntPtr.Zero
            ? Inventory
            : throw new InvalidOperationException(
                "Inventory singleton not resolved — call RefreshSingletons() after the game is in-game.");
    private IntPtr EnsureItemBank()
        => ItemBankHolder != IntPtr.Zero
            ? ItemBankHolder
            : throw new InvalidOperationException(
                "ItemBankHolder singleton not resolved — call RefreshSingletons() after the game is in-game.");

    public void Dispose() => Proc.Dispose();
}
