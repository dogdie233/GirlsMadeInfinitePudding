namespace GirlsMadeInfinitePudding.GameAbi;

/// <summary>
/// Every address / offset / RVA we pulled out of the IDB.
/// All RVAs are relative to <see cref="ProcessMemory.GameProcess.IdaImageBase"/>
/// (0x180000000).  Struct field offsets come from the il2cpp type dump.
/// </summary>
public static class Offsets
{
    // ============ GameAssembly.dll exports (IL2CPP C API) ====================
    // We use these by name through GetProcAddress, not RVA, so the list below
    // is just for reference / double-checking.
    //   il2cpp_thread_attach                 export
    //   il2cpp_thread_detach                 export
    //   il2cpp_domain_get                    export
    //   il2cpp_string_new                    export
    //   il2cpp_runtime_invoke                export
    //   il2cpp_object_new                    export
    //   il2cpp_class_from_name               export
    //   il2cpp_class_get_method_from_name    export
    //   il2cpp_array_new / il2cpp_array_length

    // ============ Function RVAs (image base 0x180000000) =====================
    public const ulong Fn_Inventory_pushItem_byData         = 0x18048e740UL;
    public const ulong Fn_Inventory_pushItem_byContext      = 0x18048e890UL;
    public const ulong Fn_Inventory_pushKeyItem_byData      = 0x18048e9a0UL;
    public const ulong Fn_Inventory_removeItem_byContext    = 0x18048eb10UL;
    public const ulong Fn_Inventory_removeItem_byData       = 0x18048eb70UL;
    public const ulong Fn_Inventory_getItem_byIndex         = 0x18048ddf0UL;
    public const ulong Fn_Inventory_getItem_byId            = 0x18048de70UL;
    public const ulong Fn_Inventory_get_ItemCount           = 0x18048e570UL;
    public const ulong Fn_Inventory_get_KeyItemCount        = 0x18048e5b0UL;
    public const ulong Fn_Inventory_get_CountMax            = 0x18048e550UL;
    public const ulong Fn_Inventory_init                    = 0x18048e5f0UL;

    public const ulong Fn_ItemBankHolder_getItem            = 0x18049eaf0UL;
    public const ulong Fn_ItemBankHolder_getItemsByType     = 0x18049eb90UL;
    public const ulong Fn_ItemBankHolder_getItems           = 0x18049ed10UL;

    public const ulong Fn_ItemData_get_Name                 = 0x18049FE10UL;
    public const ulong Fn_ItemData_get_Image                = 0x1804479C0UL;

    public const ulong Fn_Texture_get_width                 = 0x182173090UL;
    public const ulong Fn_Texture_get_height                = 0x182172FB0UL;
    public const ulong Fn_Texture2D_GetPixels32             = 0x18216CBF0UL;

    public const ulong Fn_GlobalAccess_get_object           = 0x1806f40a0UL;

    // MethodInfo* "slots" in the g_MetadataRegistration table.  The DWORD
    // pointer at each of these addresses is filled in with the actual
    // runtime <c>MethodInfo*</c> during il2cpp metadata initialization;
    // after the first frame, reading the slot gives a valid pointer that
    // we can hand to GlobalAccess.get_object_.
    public const ulong Slot_Method_GlobalAccess_get_Inventory       = 0x182fd0e48UL;
    public const ulong Slot_Method_GlobalAccess_get_ItemBankHolder  = 0x182fd0f60UL;
    public const ulong Slot_Method_GlobalAccess_get_StoryManager    = 0x182fd2bd0UL;
    public const ulong Slot_Method_ItemData_get_Name                = 0x18049FE10UL;

    // ============ Struct offsets (pulled from il2cpp dump) ====================

    public static class Inventory
    {
        // app_Inventory_Fields, starts at object+0x10
        public const int CountMax        = 0x20;   // int32
        public const int DefaultKeyItems = 0x28;   // string[]*
        public const int Items           = 0x30;   // List<ItemContext>*
        public const int KeyItems        = 0x38;   // List<ItemContext>*
    }

    public static class List
    {
        // System.Collections.Generic.List<T>_Fields (object+0x10)
        public const int Items   = 0x10;   // T[]* (i.e. raw il2cpp array object pointer)
        public const int Size    = 0x18;   // int32
        public const int Version = 0x1C;   // int32
    }

    public static class Array
    {
        // Il2CppArray layout (object+0x10):
        //   0x10 bounds
        //   0x18 max_length  (il2cpp_array_size_t == size_t)
        //   0x20 first element
        public const int Bounds    = 0x10;
        public const int MaxLength = 0x18;
        public const int FirstElem = 0x20;
    }

    public static class ItemContext
    {
        // app_ItemContext_Fields (object+0x10) - single field: ItemData*
        public const int ItemData = 0x10;
    }

    public static class ItemData
    {
        // app_ItemData_Fields (object+0x10)
        public const int Comment   = 0x10;   // string*
        public const int Valid     = 0x18;   // bool
        public const int Id        = 0x20;   // string*
        public const int Type      = 0x28;   // int32  (0=Food,1=KeyItem,2=Tool,...)
        public const int Category  = 0x30;   // string*
        public const int Tier      = 0x38;   // int32
        public const int Priority  = 0x3C;   // int32
        public const int Image     = 0x40;   // Texture2D*
        public const int ImageArgs = 0x48;   // Texture2D[]*
        public const int TintColor = 0x50;   // UnityEngine.Color (16 bytes)
        public const int Args      = 0x60;   // string[]*
        public const int Effects   = 0x68;   // ItemEffect[]*
    }

    public static class ItemBank
    {
        // app_ItemBank_Fields (object+0x10):
        //   0x10 UnityEngine.ScriptableObject base (opaque, 8 bytes)
        //   0x18 ItemData[]* _ItemList
        public const int ItemList = 0x18;
    }

    public static class ItemBankHolder
    {
        // app_ItemBankHolder_Fields (object+0x10)
        //   0x10 GlobalBehaviour base (16 bytes)
        //   0x20 ItemBank* _ItemBank
        public const int ItemBank = 0x20;
    }

    public static class Il2CppString
    {
        public const int Length = 0x10; // int32
        public const int Chars  = 0x14; // UTF-16 payload
    }

    // ============ ItemData.Type enum values (best guesses; confirm at runtime) ==
    public enum ItemType
    {
        Food    = 0,
        KeyItem = 1,
        Tool    = 2,
        Other   = 3,
    }
}
