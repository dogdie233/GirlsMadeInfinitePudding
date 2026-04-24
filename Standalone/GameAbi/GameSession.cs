using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

using GirlsMadeInfinitePudding.ProcessMemory;

namespace GirlsMadeInfinitePudding.GameAbi;

/// <summary>
///     High-level "sitting on top of a live game" API: exposes singleton
///     access, item-bank queries, and the mutation primitives the UI will
///     wire up (add food, remove food, fill inventory, clear inventory, ...).
/// </summary>
public sealed class GameSession : IDisposable
{
    public GameSession(GameProcess proc)
    {
        Proc = proc;
        Exports = new RemoteExportResolver(proc, proc.GameAssemblyBase);
        Invoker = new RemoteInvoker(proc, Exports);
        Reader = new GameReader(proc);
    }

    public GameProcess Proc { get; }
    public RemoteExportResolver Exports { get; }
    public RemoteInvoker Invoker { get; }
    public GameReader Reader { get; }

    /// <summary>Cached <c>Inventory</c> GlobalBehaviour instance.</summary>
    public IntPtr Inventory { get; private set; } = IntPtr.Zero;

    /// <summary>Cached <c>ItemBankHolder</c> GlobalBehaviour instance.</summary>
    public IntPtr ItemBankHolder { get; private set; } = IntPtr.Zero;

    /// <summary>Cached <c>StoryManager</c> GlobalBehaviour instance (optional).</summary>
    public IntPtr StoryManager { get; private set; } = IntPtr.Zero;

    public void Dispose()
    {
        Proc.Dispose();
    }

    /// <summary>
    ///     Resolve the three singletons we care about.  Must be called after the
    ///     game's main scene is loaded - in practice once <c>HomeFlow</c> or
    ///     later is active all three GlobalBehaviours are registered.
    /// </summary>
    public void RefreshSingletons()
    {
        Inventory = GetSingleton(Offsets.Slot_Method_GlobalAccess_get_Inventory);
        ItemBankHolder = GetSingleton(Offsets.Slot_Method_GlobalAccess_get_ItemBankHolder);
        try
        {
            StoryManager = GetSingleton(Offsets.Slot_Method_GlobalAccess_get_StoryManager);
        }
        catch
        {
            /* StoryManager may not be registered until later - non-fatal */
        }
    }

    private IntPtr GetSingleton(ulong methodInfoSlotIdaAddr)
    {
        // The slot holds a MethodInfo* once the runtime has initialised it.
        var slot = Proc.ResolveRva(methodInfoSlotIdaAddr);
        var methodInfo = Proc.ReadPtr(slot);
        if (methodInfo == IntPtr.Zero)
            throw new InvalidOperationException(
                $"MethodInfo slot 0x{methodInfoSlotIdaAddr:X} is still null — " +
                "is the game fully booted into the main scene?");
        var getObject = Proc.ResolveRva(Offsets.Fn_GlobalAccess_get_object);
        var result = Invoker.InvokeNative(getObject, /*index=*/ IntPtr.Zero, methodInfo);
        return (IntPtr)result;
    }

    // ======================= READ-SIDE ======================================

    private List<ItemInfo> ListAllItems()
    {
        var items = Reader.ReadAllItems(EnsureItemBank());
        AugmentItemList(items);
        return items;
    }

    private void AugmentItemList(List<ItemInfo> items)
    {
        var itemsSpan = CollectionsMarshal.AsSpan(items);
        foreach (ref var item in itemsSpan) 
            AugmentItemData(ref item);
    }

    private void AugmentItemData(ref ItemInfo info)
    {
        if (info.DataPtr == IntPtr.Zero || !info.Valid)
            return;

        var name = AugmentItemName(info.DataPtr) ?? info.Id;
        var image = CreateItemImageSource(info.DataPtr);

        info = info with { Name = name, ImageSource = image};
    }

    private string? AugmentItemName(IntPtr itemPtr)
    {
        // Route ItemData.get_Name through il2cpp_runtime_invoke so any
        // managed exception inside the getter (NRE on missing localisation
        // row, etc.) is caught by the IL2CPP runtime rather than unwinding
        // through our SEH-less trampoline and crashing the game.
        var getNameMi = Invoker.ResolveMethod("app", "ItemData", "get_Name", 0);
        if (getNameMi != IntPtr.Zero)
        {
            try
            {
                var namePtr = (IntPtr)Invoker.SafeInvoke(getNameMi, itemPtr);
                if (namePtr != IntPtr.Zero)
                {
                    var s = Proc.ReadIl2CppString(namePtr);
                    if (!string.IsNullOrEmpty(s))
                        return s;
                }
            }
            catch (Il2CppRuntimeException)
            {
                /* leave name = Id fallback */
            }
        }
        else
        {
            // Fallback to the raw function pointer path if the method
            // couldn't be resolved (unlikely — only happens if the class
            // isn't registered yet or the namespace is wrong).
            var getNameFn = Proc.ResolveRva(Offsets.Fn_ItemData_get_Name);
            var namePtr = Invoker.InvokeNative(getNameFn, itemPtr);
            if (namePtr == 0)
                return null;
            var s = Proc.ReadIl2CppString((IntPtr)namePtr);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        return null;
    }
    
    private UnityColorImageSource? CreateItemImageSource(IntPtr itemPtr)
    {
        try
        {
            // Prefer reading the _Image field directly — it's a plain Texture2D*
            // at ItemData+0x40, so no remote call is needed.  (The getter at
            // RVA 0x4479C0 is folded with NMeCab.MeCabNodeBase.get_RPath in
            // the IDB since both are `mov rax, [rcx+40h]; ret`.)
            var texPtr = Proc.ReadPtr(itemPtr + Offsets.ItemData.Image);

            if (texPtr != IntPtr.Zero)
            {
                var wFn = Proc.ResolveRva(Offsets.Fn_Texture_get_width);
                var hFn = Proc.ResolveRva(Offsets.Fn_Texture_get_height);
                var isReadFn = Proc.ResolveRva(Offsets.Fn_Texture2D_get_isReadable);
                var pixFn = Proc.ResolveRva(Offsets.Fn_Texture2D_GetPixels32);

                // Skip textures whose Read/Write flag is off — GetPixels32
                // raises a managed UnityException in that case, and the
                // exception unwinds out through our shellcode trampoline
                // (which has no registered .pdata/.xdata) and crashes the
                // game process.  Most shipped-game sprites are NOT readable.
                //
                // We also can't fall back to the Blit→RenderTexture→ReadPixels
                // dance: every Unity rendering API (GfxDevice, command buffer,
                // etc.) is tied to Unity's main/render thread via TLS, and
                // calling it from our CreateRemoteThread-born worker crashes
                // inside UnityPlayer.dll with a null-vtable deref (observed
                // at unityplayer.dll+0x446FC8).  If we ever want icons for
                // non-readable sprites the right path is to inject a proper
                // DLL, hook a main-thread update point, and do the work
                // there — out of scope for the shellcode trainer.
                var readable = (byte)(Invoker.InvokeNative(isReadFn, texPtr) & 0xFF) != 0;
                if (!readable)
                    return null;

                var width = (int)Invoker.InvokeNative(wFn, texPtr);
                var height = (int)Invoker.InvokeNative(hFn, texPtr);
                if (width <= 0 || height <= 0)
                    return null;
                
                var arrPtr = (IntPtr)Invoker.InvokeNative(pixFn, texPtr);
                if (arrPtr == IntPtr.Zero)
                    return null;
                
                var expectedSize = width * height;
                var arrLen = Reader.ReadArrayLength(arrPtr);
                if (arrLen < expectedSize)
                    return null;
                
                var byteLen = expectedSize * 4;
                var bytes = new byte[byteLen];
                Proc.Read(arrPtr + Offsets.Array.FirstElem, bytes);
                return new UnityColorImageSource(bytes, width, height);
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public List<ItemInfo> ListFoodBank()
    {
        return ListAllItems().Where(i => i.Type == (int)Offsets.ItemType.Food).ToList();
    }

    public List<ItemInfo> ListCurrentInventory()
    {
        var items = Reader.ReadInventoryList(EnsureInventory(), false);
        AugmentItemList(items);
        return items;
    }

    public List<ItemInfo> ListCurrentKeyItems()
    {
        var items = Reader.ReadInventoryList(EnsureInventory(), true);
        AugmentItemList(items);
        return items;
    }

    public int CurrentFoodCount()
    {
        return Reader.ReadItemCount(EnsureInventory());
    }

    public int CurrentKeyCount()
    {
        return Reader.ReadKeyItemCount(EnsureInventory());
    }

    public int CurrentCountMax()
    {
        return Reader.ReadCountMax(EnsureInventory());
    }

    // ======================= WRITE-SIDE =====================================

    /// <summary>Look up an <see cref="ItemInfo" /> by string ID from the ItemBank dump.</summary>
    public ItemInfo? FindItemById(string id)
    {
        foreach (var it in ListAllItems())
            if (string.Equals(it.Id, id, StringComparison.OrdinalIgnoreCase))
                return it;
        return null;
    }

    /// <summary>Add N copies of the given food/ItemData to Inventory.</summary>
    /// <param name="itemData">ItemData to add.</param>
    /// <param name="count">Number of copies to add.</param>
    /// <param name="over">If true, ignores CountMax (passes over=1 to Inventory::pushItem).</param>
    public void AddItem(IntPtr itemData, int count = 1, bool over = true)
    {
        if (itemData == IntPtr.Zero || count <= 0) return;
        var push = Proc.ResolveRva(Offsets.Fn_Inventory_pushItem_byData);
        var inv = EnsureInventory();
        for (var i = 0; i < count; i++)
            Invoker.InvokeNative(push, inv, itemData, over ? 1 : 0);
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
        var inv = EnsureInventory();
        for (var i = 0; i < count; i++)
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
    ///     Direct write to <c>Inventory._CountMax</c>.  No remote call required
    ///     — it's a plain int field.
    /// </summary>
    public void SetBaseCountMax(int newMax)
    {
        Proc.WriteI32(EnsureInventory() + Offsets.Inventory.CountMax, newMax);
    }

    /// <summary>Quick "fill the bag with food X" helper — bypasses CountMax via over=true.</summary>
    public void FillWithFood(string foodId, int count)
    {
        AddItemById(foodId, count);
    }

    /// <summary>Clear every food (keeps key items).</summary>
    public void ClearFood()
    {
        var snapshot = ListCurrentInventory();
        foreach (var item in snapshot)
            RemoveItem(item.DataPtr);
    }

    // ======================= internals ======================================
    private IntPtr EnsureInventory()
    {
        return Inventory != IntPtr.Zero
            ? Inventory
            : throw new InvalidOperationException(
                "Inventory singleton not resolved — call RefreshSingletons() after the game is in-game.");
    }

    private IntPtr EnsureItemBank()
    {
        return ItemBankHolder != IntPtr.Zero
            ? ItemBankHolder
            : throw new InvalidOperationException(
                "ItemBankHolder singleton not resolved — call RefreshSingletons() after the game is in-game.");
    }
}