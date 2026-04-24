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

// Main window handle — assigned via .Ref(out window) below.  Declared up
// front so builder lambdas (async button handlers, toast hookup) can
// close over it.
Window window = null!;

// Both grids get a Ref'd handle so we can push a fresh snapshot whenever the
// backing list mutates.  MewUI's GridView is only guaranteed to re-render
// when its items source is *replaced* (SetItemsSource with a new list);
// mutating an IReadOnlyList<T> in place + InvalidateMeasure was racy and
// missed updates.  See "Complex binding" in fba_gallery.cs.
GridView gridFood = null!;
GridView gridInv = null!;

// Track selection as the selected item itself (GridView has no
// BindSelectedIndex — we wire SelectionChanged by hand below).
var selectedFood = new ObservableValue<ItemInfo?>();
var selectedInv = new ObservableValue<InventoryGroup?>();

vm.FoodBankVersion.Subscribe(() =>
{
    // ReSharper disable once AccessToModifiedClosure
    gridFood?.SetItemsSource(vm.FoodBankFiltered.ToList());
    // Keep selection iff the item is still present (ItemInfo is a record
    // struct, so value equality is what we want).
    if (selectedFood.Value is { } cur && !vm.FoodBankFiltered.Contains(cur))
        selectedFood.Value = null;
});
vm.InventoryVersion.Subscribe(() =>
{
    // ReSharper disable once AccessToModifiedClosure
    gridInv?.SetItemsSource(vm.CurrentInventory.ToList());
    // InventoryGroup value-equals by (Sample, Count); since Count changes on
    // add/remove, match by Sample.Id to survive quantity changes.
    if (selectedInv.Value is { } cur)
    {
        InventoryGroup? match = null;
        foreach (var g in vm.CurrentInventory)
            if (g.Sample.Id == cur.Sample.Id)
            {
                match = g;
                break;
            }

        selectedInv.Value = match;
    }
});

// ---- section builders ---------------------------------------------------


Element ConnectionBar()
{
    return new GroupBox()
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
                                .OnClick(async void () =>
                                {
                                    // Show a cancellable busy overlay so the UI stays
                                    // responsive while we attach + dump the item bank.
                                    try
                                    {
                                        using var busy = window.CreateBusyIndicator(
                                            "Connecting to game…", true);
                                        await vm.ConnectAsync(busy.CancellationToken,
                                            busy.NotifyProgress);
                                    }
                                    catch (Exception ex)
                                    {
                                        vm.Status.Value = $"Error: {ex.GetType()}: {ex.Message}";
                                    }
                                }),
                            new Button()
                                .Content("Refresh")
                                .Width(96)
                                .OnCanClick(() => vm.IsConnected.Value)
                                .OnClick(vm.RefreshAll),
                            new Button()
                                .Content("Disconnect")
                                .Width(96)
                                .OnCanClick(() => vm.IsConnected.Value)
                                .OnClick(vm.Disconnect)),
                    new Label()
                        .BindText(vm.ConnectionInfo)
                        .CenterVertical()
                        .FontFamily("Consolas")));
}

Element InventoryBar()
{
    return new StackPanel()
        .Horizontal()
        .Spacing(16)
        .Children(
            new Label().Text("Inventory:").Bold().CenterVertical(),
            new Label().BindText(vm.InventorySummary).FontFamily("Consolas").CenterVertical());
}

// ---- Tab 1 — Add food ---------------------------------------------------
Element AddFoodTab()
{
    return new Grid()
        .Columns("*,240")
        .Rows("Auto,Auto,*")
        .Spacing(8)
        .Children(
            new DockPanel()
                .Row(0).Column(0).ColumnSpan(2)
                .Spacing(8)
                .LastChildFill()
                .Children(
                    new Label().Text("Filter:").DockLeft().CenterVertical(),
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
                    new GridView()
                        .Ref(out gridFood)
                        .ItemsSource(vm.FoodBankFiltered.ToList())
                        .Apply(g => g.SelectionChanged += o =>
                            selectedFood.Value = o is ItemInfo item ? item : null)
                        .Columns(
                            new GridViewColumn<ItemInfo>().Header("").Width(40)
                                .Template(
                                    _ => new Image().Size(28, 28).StretchMode(Stretch.Uniform),
                                    (v, r) => v.Source = r.ImageSource),
                            new GridViewColumn<ItemInfo>().Header("Id").Width(180)
                                .Text(r => r.Id),
                            new GridViewColumn<ItemInfo>().Header("Name").Width(220)
                                .Text(r => r.Name))),
            new GroupBox()
                .Row(2).Column(1)
                .Header("Add selected")
                .Content(
                    new StackPanel()
                        .Vertical().Spacing(8).Padding(4)
                        .Children(
                            new Label()
                                .BindText(selectedFood, f =>
                                    f is { } x
                                        ? $"Id:    {x.Id}\nName:  {x.Name}\nTier:  {x.Tier}\nPrio:  {x.Priority}\nType:  {x.Type}"
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
                                .OnCanClick(() => vm.IsConnected.Value && selectedFood.Value is not null)
                                .OnClick(() => vm.AddSelectedFood(selectedFood.Value, vm.AddCount.Value)),
                            new Button()
                                .Content("Quick x1")
                                .OnCanClick(() => vm.IsConnected.Value && selectedFood.Value is not null)
                                .OnClick(() => vm.AddSelectedFood(selectedFood.Value, 1)),
                            new Button()
                                .Content("Quick x10")
                                .OnCanClick(() => vm.IsConnected.Value && selectedFood.Value is not null)
                                .OnClick(() => vm.AddSelectedFood(selectedFood.Value, 10)),
                            new Button()
                                .Content("Quick x99")
                                .OnCanClick(() => vm.IsConnected.Value && selectedFood.Value is not null)
                                .OnClick(() => vm.AddSelectedFood(selectedFood.Value, 99)))));
}

// ---- Tab 2 — Current inventory ------------------------------------------
Element InventoryTab()
{
    return new Grid()
        .Columns("*,240")
        .Rows("*,Auto")
        .Spacing(8)
        .Children(
            new GroupBox()
                .Row(0).Column(0)
                .Padding(0, 0)
                .BorderThickness(0)
                .Header("Current foods (grouped)")
                .Content(
                    new GridView()
                        .Ref(out gridInv)
                        .ItemsSource(vm.CurrentInventory.ToList())
                        .Apply(g => g.SelectionChanged += o =>
                            selectedInv.Value = o is InventoryGroup grp ? grp : null)
                        .Columns(
                            new GridViewColumn<InventoryGroup>().Header("").Width(40)
                                .Template(
                                    _ => new Image().Size(28, 28).StretchMode(Stretch.Uniform),
                                    (v, r) => v.Source = r.Sample.ImageSource),
                            new GridViewColumn<InventoryGroup>().Header("Id").Width(140)
                                .Text(r => r.Sample.Id),
                            new GridViewColumn<InventoryGroup>().Header("Name").Width(180)
                                .Text(r => r.Sample.Name),
                            new GridViewColumn<InventoryGroup>().Header("Count").Width(64)
                                .Text(r => $"x{r.Count}"),
                            new GridViewColumn<InventoryGroup>().Header("Tier").Width(56)
                                .Text(r => r.Sample.Tier.ToString()))),
            new GroupBox()
                .Row(0).Column(1)
                .Header("Actions")
                .Content(
                    new StackPanel()
                        .Vertical().Spacing(8).Padding(4)
                        .Children(
                            new Label()
                                .BindText(selectedInv, g =>
                                    g is { } x
                                        ? $"Id:    {x.Sample.Id}\nName:  {x.Sample.Name}\nCount: {x.Count}\nTier:  {x.Sample.Tier}"
                                        : "(no selection)")
                                .FontFamily("Consolas"),
                            new Button()
                                .Content("Remove 1")
                                .OnCanClick(() => vm.IsConnected.Value && selectedInv.Value is not null)
                                .OnClick(() =>
                                {
                                    if (selectedInv.Value is { } g) vm.RemoveGroup(g, 1);
                                }),
                            new Button()
                                .Content("Remove all of this")
                                .OnCanClick(() => vm.IsConnected.Value && selectedInv.Value is not null)
                                .OnClick(() =>
                                {
                                    if (selectedInv.Value is { } g) vm.RemoveGroup(g, g.Count);
                                }))),
            new StackPanel()
                .Row(1).Column(0).ColumnSpan(2)
                .Horizontal().Spacing(8).Right()
                .Children(
                    new Button()
                        .Content("Refresh")
                        .Width(96)
                        .OnCanClick(() => vm.IsConnected.Value)
                        .OnClick(vm.RefreshInventoryOnly),
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
}

// ---- Tab 3 — Capacity ---------------------------------------------------
var capacityInput = new ObservableValue<int>(99);

Element CapacityTab()
{
    return new StackPanel()
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
                        .OnClick(() =>
                        {
                            capacityInput.Value = 99;
                            vm.SetCountMax(99);
                        }),
                    new Button()
                        .Content("Apply 999")
                        .Width(120)
                        .OnCanClick(() => vm.IsConnected.Value)
                        .OnClick(() =>
                        {
                            capacityInput.Value = 999;
                            vm.SetCountMax(999);
                        })));
}

// ---- compose window -----------------------------------------------------
Application.Create()
    .UseAccent(Color.FromHex("#5D5369"))
    .Run(new Window()
        .Ref(out window)
        .Resizable(860, 640)
        .Title("Infinite Pudding!  —  Girls Made Pudding trainer")
        .Padding(0)
        .OnLoaded(() =>
        {
            // Wire the VM's toast channel now that `window` is live. The VM
            // uses this for terminal outcomes (Added/Removed/failed) while
            // Status stays the progress-narration channel.
            vm.Toast = msg => window.ShowToast(msg);
        })
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
                            new TabItem().Header("Add food").Content(AddFoodTab()),
                            new TabItem().Header("Current inventory").Content(InventoryTab()),
                            new TabItem().Header("Capacity").Content(CapacityTab()))
                )));

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
        var rounded = (int)Math.Round(d.Value);
        if (src.Value != rounded) src.Value = rounded;
    });
    src.Subscribe(() =>
    {
        if ((int)Math.Round(d.Value) != src.Value) d.Value = src.Value;
    });
    return d;
}