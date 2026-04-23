using System.Diagnostics;
using Aprillz.MewUI;
using GirlsMadeInfinitePudding.GameAbi;
using GirlsMadeInfinitePudding.ProcessMemory;

namespace GirlsMadeInfinitePudding;

/// <summary>
/// Bridges the raw <see cref="GameSession"/> (which deals in naked pointers)
/// and the MewUI-facing observable surface the UI binds against.  Keeps
/// two mutable <see cref="List{T}"/> buffers (<see cref="FoodBank"/> and
/// <see cref="CurrentInventory"/>) which the UI hands to
/// <c>ItemsSource.Create</c> — they're edited in place, then
/// <see cref="InventoryVersion"/> / <see cref="FoodBankVersion"/> tick to
/// signal the UI it needs to re-measure its list boxes.
/// </summary>
public sealed class TrainerViewModel : IDisposable
{
    // ---- observable state ---------------------------------------------------
    public ObservableValue<string> Status           { get; } = new("Not connected.");
    public ObservableValue<bool>   IsConnected      { get; } = new(false);
    public ObservableValue<string> InventorySummary { get; } = new("—");
    public ObservableValue<string> ConnectionInfo   { get; } = new("(no game attached)");

    /// <summary>Versions tick whenever the in-place lists are mutated.</summary>
    public ObservableValue<int> InventoryVersion { get; } = new(0);
    public ObservableValue<int> FoodBankVersion  { get; } = new(0);

    /// <summary>All food-type items (Type=Food).  Stable reference — mutated in place.</summary>
    public List<ItemInfo> FoodBank { get; } = new();

    /// <summary>Filtered view of <see cref="FoodBank"/>.  Stable reference — mutated in place.</summary>
    public List<ItemInfo> FoodBankFiltered { get; } = new();

    /// <summary>Current inventory grouped by ID.  Stable reference — mutated in place.</summary>
    public List<InventoryGroup> CurrentInventory { get; } = new();

    public ObservableValue<string> FoodFilter { get; } = new("");
    public ObservableValue<int>    AddCount   { get; } = new(1);

    private GameSession? _session;

    public TrainerViewModel()
    {
        FoodFilter.Subscribe(RecomputeFilter);
    }

    public GameSession? Session => _session;

    // ---- lifecycle ----------------------------------------------------------
    public void Connect()
    {
        try
        {
            _session?.Dispose();
            var proc = GameProcess.Attach();
            _session = new GameSession(proc);
            _session.RefreshSingletons();
            ConnectionInfo.Value = $"pid={proc.Pid}  GameAssembly @ 0x{(long)proc.GameAssemblyBase:X}";
            IsConnected.Value    = true;
            Status.Value         = "Connected.  Dumping item bank…";
            RefreshAll();
            Status.Value         = "Ready.";
        }
        catch (Exception ex)
        {
            IsConnected.Value    = false;
            ConnectionInfo.Value = "(attach failed)";
            Status.Value         = $"Connect failed: {ex.Message}";
        }
    }

    public void Disconnect()
    {
        _session?.Dispose();
        _session = null;
        IsConnected.Value      = false;
        ConnectionInfo.Value   = "(no game attached)";
        InventorySummary.Value = "—";

        FoodBank.Clear();
        RecomputeFilter();
        CurrentInventory.Clear();
        FoodBankVersion.Value++;
        InventoryVersion.Value++;

        Status.Value = "Disconnected.";
    }

    public void RefreshAll()
    {
        if (_session is null) return;
        try
        {
            var items = _session.ListFoodBank()
                                .OrderBy(i => i.Tier)
                                .ThenBy (i => i.Id, StringComparer.OrdinalIgnoreCase)
                                .ToList();
            FoodBank.Clear();
            FoodBank.AddRange(items);
            RecomputeFilter();
            FoodBankVersion.Value++;

            RefreshInventoryOnly();
        }
        catch (Exception ex)
        {
            Status.Value = $"Refresh failed: {ex.Message}";
        }
    }

    public void RefreshInventoryOnly()
    {
        if (_session is null) return;
        try
        {
            var groups = _session.ListCurrentInventory()
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => new InventoryGroup(g.First(), g.Count()))
                .OrderBy(g => g.Sample.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CurrentInventory.Clear();
            CurrentInventory.AddRange(groups);
            InventoryVersion.Value++;

            InventorySummary.Value =
                $"{_session.CurrentFoodCount()} / {_session.CurrentCountMax()}" +
                $"   (key items: {_session.CurrentKeyCount()})";
        }
        catch (Exception ex)
        {
            Status.Value = $"Inventory refresh failed: {ex.Message}";
        }
    }

    // ---- actions ------------------------------------------------------------
    public void AddSelectedFood(ItemInfo? food, int count)
    {
        if (_session is null || food is null) return;
        try
        {
            _session.AddItem(food.Value.DataPtr, count, over: true);
            Status.Value = $"Added x{count} {food.Value.Id}";
            RefreshInventoryOnly();
        }
        catch (Exception ex) { Status.Value = $"Add failed: {ex.Message}"; }
    }

    public void RemoveGroup(InventoryGroup g, int count)
    {
        if (_session is null) return;
        try
        {
            var n = Math.Min(count, g.Count);
            _session.RemoveItem(g.Sample.DataPtr, n);
            Status.Value = $"Removed x{n} {g.Sample.Id}";
            RefreshInventoryOnly();
        }
        catch (Exception ex) { Status.Value = $"Remove failed: {ex.Message}"; }
    }

    public void ClearAllFood()
    {
        if (_session is null) return;
        try
        {
            _session.ClearFood();
            Status.Value = "Cleared all food.";
            RefreshInventoryOnly();
        }
        catch (Exception ex) { Status.Value = $"Clear failed: {ex.Message}"; }
    }

    public void SetCountMax(int value)
    {
        if (_session is null) return;
        try
        {
            _session.SetBaseCountMax(Math.Clamp(value, 1, 9999));
            RefreshInventoryOnly();
            Status.Value = $"CountMax → {value}";
        }
        catch (Exception ex) { Status.Value = $"SetCountMax failed: {ex.Message}"; }
    }

    // -------------------------------------------------------------------------
    private void RecomputeFilter()
    {
        var q = FoodFilter.Value?.Trim() ?? "";
        FoodBankFiltered.Clear();
        if (q.Length == 0)
            FoodBankFiltered.AddRange(FoodBank);
        else
            FoodBankFiltered.AddRange(
                FoodBank.Where(i => i.Id.Contains(q, StringComparison.OrdinalIgnoreCase)));
        FoodBankVersion.Value++;
    }

    public void Dispose() => _session?.Dispose();
}

/// <summary>A (food, quantity) row for the "current inventory" tab.</summary>
public readonly record struct InventoryGroup(ItemInfo Sample, int Count)
{
    public string DisplayText => $"x{Count,-4} {Sample.Id}";
}
