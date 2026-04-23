using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using GirlsMadeInfinitePudding;
using GirlsMadeInfinitePudding.GameAbi;

// =========================================================================
// Girls Made Infinite Pudding — food-inventory trainer (Windows / NativeAOT).
// UI: MewUI (Win32 + Direct2D backend).
// =========================================================================
if (OperatingSystem.IsWindows())
{
    Win32Platform.Register();
    Direct2DBackend.Register();
}
else
{
    throw new PlatformNotSupportedException("This trainer targets Windows only.");
}

Application.DispatcherUnhandledException += e =>
{
    // Never kill the UI thread over a stray exception — surface it.
    NativeMessageBox.Show(IntPtr.Zero,
        $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n{e.Exception.StackTrace}",
        "Infinite Pudding",
        NativeMessageBoxButtons.Ok, NativeMessageBoxIcon.Error);
    e.Handled = true;
};

using var vm = new TrainerViewModel();

// Both list boxes get a Ref'd handle so we can poke InvalidateMeasure when
// the backing list mutates.  (MewUI exposes items via ItemsSource which
// reads IReadOnlyList<T> by reference — no events — so we nudge it.)
ListBox foodList = null!;
ListBox invList  = null!;

// Track list-box selection as observables so button CanExecute logic can bind
// to them.
var selectedFoodIndex = new ObservableValue<int>(-1);
var selectedInvIndex  = new ObservableValue<int>(-1);

ItemInfo? SelectedFood()
{
    var i = selectedFoodIndex.Value;
    return (i >= 0 && i < vm.FoodBankFiltered.Count) ? vm.FoodBankFiltered[i] : null;
}
InventoryGroup? SelectedInv()
{
    var i = selectedInvIndex.Value;
    return (i >= 0 && i < vm.CurrentInventory.Count) ? vm.CurrentInventory[i] : null;
}

vm.FoodBankVersion.Subscribe(() =>
{
    // ReSharper disable once AccessToModifiedClosure
    foodList?.InvalidateMeasure();
    // Collapse stale selection.
    if (selectedFoodIndex.Value >= vm.FoodBankFiltered.Count)
        selectedFoodIndex.Value = -1;
});
vm.InventoryVersion.Subscribe(() =>
{
    // ReSharper disable once AccessToModifiedClosure
    invList?.InvalidateMeasure();
    if (selectedInvIndex.Value >= vm.CurrentInventory.Count)
        selectedInvIndex.Value = -1;
});

// ---- section builders ---------------------------------------------------


Element ConnectionBar() => new GroupBox()
    .Header("Connection")
    .Content(
        new DockPanel()
            .Spacing(8)
            .Children(
                new StackPanel()
                    .DockLeft()
                    .Horizontal()
                    .Spacing(8)
                    .Children(
                        new Button()
                            .Content("Connect")
                            .Width(96)
                            .OnCanClick(() => !vm.IsConnected.Value)
                            .OnClick(() => vm.Connect()),
                        new Button()
                            .Content("Refresh")
                            .Width(96)
                            .OnCanClick(() => vm.IsConnected.Value)
                            .OnClick(() => vm.RefreshAll()),
                        new Button()
                            .Content("Disconnect")
                            .Width(96)
                            .OnCanClick(() => vm.IsConnected.Value)
                            .OnClick(() => vm.Disconnect())),
                new Label()
                    .BindText(vm.ConnectionInfo)
                    .CenterVertical()
                    .FontFamily("Consolas")));

Element InventoryBar() => new StackPanel()
    .Horizontal()
    .Spacing(16)
    .Children(
        new Label().Text("Inventory:").Bold().CenterVertical(),
        new Label().BindText(vm.InventorySummary).FontFamily("Consolas").CenterVertical());

// ---- Tab 1 — Add food ---------------------------------------------------
Element AddFoodTab() => new Grid()
    .Columns("*,240")
    .Rows("Auto,Auto,*")
    .Spacing(8)
    .Children(
        new StackPanel()
            .Row(0).Column(0).ColumnSpan(2)
            .Horizontal().Spacing(8)
            .Children(
                new Label().Text("Filter:").CenterVertical(),
                new TextBox()
                    .Placeholder("type to filter item IDs…")
                    .BindText(vm.FoodFilter)),

        new Label()
            .Row(1).Column(0).ColumnSpan(2)
            .BindText(vm.FoodBankVersion, _ => $"{vm.FoodBankFiltered.Count} food definition(s) shown "
                                                + $"(of {vm.FoodBank.Count} total)"),

        new GroupBox()
            .Row(2).Column(0)
            .Padding(0, 0)
            .BorderThickness(0)
            .Header("ItemBank")
            .Content(
                new ListBox()
                    .Ref(out foodList)
                    .ItemsSource(new ItemsView<ItemInfo>(vm.FoodBankFiltered, i => $"{i.Name} ({i.Id})", i => i.Id))
                    .ItemTemplate<ItemInfo>(build: ctx => new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .CenterVertical()
                            .Children(
                                new Image()
                                    .Register(ctx, "Icon")
                                    .Size(32, 32)
                                    .StretchMode(Stretch.Uniform),
                                new Label()
                                    .Register(ctx, "Text")
                            ),
                        bind: (_, item, _, ctx) =>
                        {
                            ctx.Get<Image>("Icon").Source = item.ImageSource;
                            ctx.Get<Label>("Text").Text = $"{item.Name} ({item.Id})";
                        })
                    .BindSelectedIndex(selectedFoodIndex)),

        new GroupBox()
            .Row(2).Column(1)
            .Header("Add selected")
            .Content(
                new StackPanel()
                    .Vertical().Spacing(8).Padding(4)
                    .Children(
                        new Label()
                            .BindText(selectedFoodIndex, _ =>
                                SelectedFood() is { } f
                                    ? $"Id:    {f.Id}\nName:  {f.Name}\nTier:  {f.Tier}\nPrio:  {f.Priority}\nType:  {f.Type}"
                                    : "(no selection)")
                            .FontFamily("Consolas"),
                        new Label().Text("Count:").Bold(),
                        new StackPanel()
                            .Horizontal().Spacing(8)
                            .Children(
                                new Slider()
                                    .Minimum(1).Maximum(99)
                                    .BindValue(AsDouble(vm.AddCount))
                                    .Width(140),
                                new Label()
                                    .BindText(vm.AddCount, n => $"x{n}")
                                    .Width(48)
                                    .CenterVertical()),
                        new Button()
                            .Content("Add to inventory")
                            .OnCanClick(() => vm.IsConnected.Value && SelectedFood() is not null)
                            .OnClick(() => vm.AddSelectedFood(SelectedFood(), vm.AddCount.Value)),
                        new Button()
                            .Content("Quick x10")
                            .OnCanClick(() => vm.IsConnected.Value && SelectedFood() is not null)
                            .OnClick(() => vm.AddSelectedFood(SelectedFood(), 10)),
                        new Button()
                            .Content("Quick x99")
                            .OnCanClick(() => vm.IsConnected.Value && SelectedFood() is not null)
                            .OnClick(() => vm.AddSelectedFood(SelectedFood(), 99)))));

// ---- Tab 2 — Current inventory ------------------------------------------
Element InventoryTab() => new Grid()
    .Columns("*,240")
    .Rows("*,Auto")
    .Spacing(8)
    .Children(
        new GroupBox()
            .Row(0).Column(0)
            .Header("Current foods (grouped)")
            .Content(
                new ListBox()
                    .Ref(out invList)
                    .ItemsSource(ItemsSource.Create(vm.CurrentInventory, i => i.Sample.Id))
                    .ItemTemplate<InventoryGroup>(
                        build: ctx => new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .CenterVertical()
                            .Children(
                                new Image()
                                    .Register(ctx, "Icon")
                                    .Size(32, 32)
                                    .StretchMode(Stretch.Uniform),
                                new Label()
                                    .Register(ctx, "Name"),
                                new Label()
                                    .Register(ctx, "Amount")
                            ),
                        bind: (_, item, _, ctx) =>
                        {
                            ctx.Get<Image>("Icon").Source = item.Sample.ImageSource;
                            ctx.Get<Label>("Name").Text = $"{item.Sample.Name} ({item.Sample.Id})";
                            ctx.Get<Label>("Amount").Text = $"x{item.Count}";
                        })
                    .BindSelectedIndex(selectedInvIndex)),

        new GroupBox()
            .Row(0).Column(1)
            .Header("Actions")
            .Content(
                new StackPanel()
                    .Vertical().Spacing(8).Padding(4)
                    .Children(
                        new Label()
                            .BindText(selectedInvIndex, _ =>
                                SelectedInv() is { } g
                                    ? $"Id:    {g.Sample.Id}\nName:  {g.Sample.Name}\nCount: {g.Count}\nTier:  {g.Sample.Tier}"
                                    : "(no selection)")
                            .FontFamily("Consolas"),
                        new Button()
                            .Content("Remove 1")
                            .OnCanClick(() => vm.IsConnected.Value && SelectedInv() is not null)
                            .OnClick(() => { if (SelectedInv() is { } g) vm.RemoveGroup(g, 1); }),
                        new Button()
                            .Content("Remove all of this")
                            .OnCanClick(() => vm.IsConnected.Value && SelectedInv() is not null)
                            .OnClick(() => { if (SelectedInv() is { } g) vm.RemoveGroup(g, g.Count); }))),

        new StackPanel()
            .Row(1).Column(0).ColumnSpan(2)
            .Horizontal().Spacing(8).Right()
            .Children(
                new Button()
                    .Content("Refresh")
                    .Width(96)
                    .OnCanClick(() => vm.IsConnected.Value)
                    .OnClick(() => vm.RefreshInventoryOnly()),
                new Button()
                    .Content("Clear all food")
                    .Width(140)
                    .OnCanClick(() => vm.IsConnected.Value)
                    .OnClick(() =>
                    {
                        if (NativeMessageBox.Show(IntPtr.Zero,
                                "Really remove every food from the bag?",
                                "Clear inventory",
                                NativeMessageBoxButtons.OkCancel,
                                NativeMessageBoxIcon.Warning) is true)
                            vm.ClearAllFood();
                    })));

// ---- Tab 3 — Capacity ---------------------------------------------------
var capacityInput = new ObservableValue<int>(99);

Element CapacityTab() => new StackPanel()
    .Vertical().Spacing(12).Padding(8)
    .Children(
        new Label().Text("Inventory.CountMax").Bold().FontSize(14),
        new Label().Text(
            "Base capacity of the food bag.  The 'Inventory' summary above is " +
            "CountMax + any bonus that key items contribute (e.g. capacity-boost tools)."),
        new StackPanel()
            .Horizontal().Spacing(8)
            .Children(
                new Label().Text("New value:").CenterVertical(),
                new Slider()
                    .Minimum(1).Maximum(999)
                    .BindValue(AsDouble(capacityInput))
                    .Width(320),
                new Label()
                    .BindText(capacityInput, n => $"= {n}")
                    .Width(64)
                    .CenterVertical()),
        new StackPanel()
            .Horizontal().Spacing(8)
            .Children(
                new Button()
                    .Content("Apply")
                    .Width(120)
                    .OnCanClick(() => vm.IsConnected.Value)
                    .OnClick(() => vm.SetCountMax(capacityInput.Value)),
                new Button()
                    .Content("Apply 99")
                    .Width(120)
                    .OnCanClick(() => vm.IsConnected.Value)
                    .OnClick(() => { capacityInput.Value = 99;  vm.SetCountMax(99); }),
                new Button()
                    .Content("Apply 999")
                    .Width(120)
                    .OnCanClick(() => vm.IsConnected.Value)
                    .OnClick(() => { capacityInput.Value = 999; vm.SetCountMax(999); })));

// ---- compose window -----------------------------------------------------
var window = new Window()
    .Resizable(860, 640)
    .Title("Infinite Pudding!  —  Girls Made Pudding trainer")
    .Padding(0)
    .Content(
        new DockPanel()
            .LastChildFill()
            .Padding(12)
            .Spacing(10)
            .Children(
                ConnectionBar().DockTop(),
                InventoryBar().DockTop(),
                new Label()
                    .DockBottom()
                    .BindText(vm.Status)
                    .FontFamily("Consolas")
                    .Padding(6),
                new TabControl()
                    .TabItems(
                        new TabItem().Header("Add food")         .Content(AddFoodTab()),
                        new TabItem().Header("Current inventory").Content(InventoryTab()),
                        new TabItem().Header("Capacity")         .Content(CapacityTab()))
            ));

Application.Run(window);

// =========================================================================
//  Helpers
// =========================================================================
// MewUI Slider binds to an ObservableValue<double>, but our view-model
// exposes int values for discrete counts.  This shim keeps the two in
// sync: every slider change is rounded and written back.
static ObservableValue<double> AsDouble(ObservableValue<int> src)
{
    var d = new ObservableValue<double>(src.Value);
    d.Subscribe(() =>
    {
        int rounded = (int)Math.Round(d.Value);
        if (src.Value != rounded) src.Value = rounded;
    });
    src.Subscribe(() =>
    {
        if ((int)Math.Round(d.Value) != src.Value) d.Value = src.Value;
    });
    return d;
}
