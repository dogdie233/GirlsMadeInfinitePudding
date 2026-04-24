using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using GirlsMadeInfinitePudding.GameAbi;
using GirlsMadeInfinitePudding.ProcessMemory;

namespace GirlsMadeInfinitePudding;

/// <summary>
///     Bridges the raw <see cref="GameSession" /> (which deals in naked pointers)
///     and the MewUI-facing observable surface the UI binds against.  Keeps
///     two mutable <see cref="List{T}" /> buffers (<see cref="FoodBank" /> and
///     <see cref="CurrentInventory" />) which the UI hands to
///     <c>ItemsSource.Create</c> — they're edited in place, then
///     <see cref="InventoryVersion" /> / <see cref="FoodBankVersion" /> tick to
///     signal the UI it needs to re-measure its list boxes.
/// </summary>
public sealed class TrainerViewModel : IDisposable
{
    public TrainerViewModel()
    {
        FoodFilter.Subscribe(RecomputeFilter);
    }

    // ---- observable state ---------------------------------------------------
    public ObservableValue<string> Status { get; } = new("Not connected.");
    public ObservableValue<bool> IsConnected { get; } = new();
    public ObservableValue<string> InventorySummary { get; } = new("—");
    public ObservableValue<string> ConnectionInfo { get; } = new("(no game attached)");

    /// <summary>Versions tick whenever the in-place lists are mutated.</summary>
    public ObservableValue<int> InventoryVersion { get; } = new();

    public ObservableValue<int> FoodBankVersion { get; } = new();

    /// <summary>All food-type items (Type=Food).  Stable reference — mutated in place.</summary>
    public List<ItemInfo> FoodBank { get; } = [];

    /// <summary>Filtered view of <see cref="FoodBank" />.  Stable reference — mutated in place.</summary>
    public List<ItemInfo> FoodBankFiltered { get; } = [];

    /// <summary>Current inventory grouped by ID.  Stable reference — mutated in place.</summary>
    public List<InventoryGroup> CurrentInventory { get; } = [];

    public ObservableValue<string> FoodFilter { get; } = new("");
    public ObservableValue<int> AddCount { get; } = new(1);

    /// <summary>
    ///     Hook supplied by the UI layer so the VM can raise result toasts
    ///     ("Added x5 itemId", "Remove failed: ...") without depending on the
    ///     concrete <see cref="Aprillz.MewUI.Window" />.  Status.Value remains the
    ///     "in-progress" narration channel; Toast is for terminal outcomes.
    /// </summary>
    public Action<string>? Toast { get; set; }

    public GameSession? Session { get; private set; }

    public void Dispose()
    {
        Session?.Dispose();
    }

    // ---- exception plumbing -------------------------------------------------
    /// <summary>
    ///     Centralised failure handler.  Routes a thrown exception to the right
    ///     surface depending on what kind of failure it is:
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <see cref="RemoteInvocationException" /> where the game process is no
    ///                 longer alive → silent <see cref="Disconnect" /> with a brief toast
    ///                 ("Game process gone — disconnected.").  This handles the common
    ///                 "game crashed mid-operation" case without scaring the user with a
    ///                 ReadProcessMemory stack trace.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 Anything else → raise <paramref name="action" /> to the toast channel
    ///                 with the exception's own message.  Status gets a terse summary.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     Intended call pattern is <c>try { ... } catch (Exception ex) { HandleActionException(ex, "Remove"); }</c>.
    /// </summary>
    private void HandleActionException(Exception ex, string action)
    {
        var procDead = Session is not null &&
                       ex is RemoteInvocationException &&
                       !Session.Proc.IsProcessAlive();
        if (procDead)
        {
            // Silent-ish tear down.  We tick the observables back to the
            // "no game" state but don't surface the underlying RPM error —
            // that's noise; the real story is "game is gone".
            Session?.Dispose();
            Session = null;
            IsConnected.Value = false;
            ConnectionInfo.Value = "(no game attached)";
            InventorySummary.Value = "—";
            FoodBank.Clear();
            RecomputeFilter();
            CurrentInventory.Clear();
            FoodBankVersion.Value++;
            InventoryVersion.Value++;
            Status.Value = "Game process gone — disconnected.";
            Toast?.Invoke("Game process gone — disconnected.");
            return;
        }

        Status.Value = $"{action} failed.";
        Toast?.Invoke($"{action} failed: {ex.Message}");
    }

    // ---- lifecycle ----------------------------------------------------------
    /// <summary>
    ///     Connects to the game and pulls the item bank + current inventory on a
    ///     worker thread, marshalling observable writes back to the UI dispatcher.
    ///     The UI layer is responsible for surfacing the <see cref="IBusyIndicator" />
    ///     and passing its cancellation token so the user can abort mid-dump.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct, Action<string>? progress = null)
    {
        var dispatcher = Application.Current.Dispatcher;

        void Post(Action a)
        {
            if (dispatcher is null) a();
            else dispatcher.BeginInvoke(a);
        }

        try
        {
            Post(() => Status.Value = "Attaching to game process…");
            progress?.Invoke("Attaching to game process…");

            // Attach + session creation are genuinely blocking (PInvoke +
            // memory scans), so they run off-thread.
            var (session, pid, baseAddr) = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                Session?.Dispose();
                var proc = GameProcess.Attach();
                var s = new GameSession(proc);
                s.RefreshSingletons();
                return (s, proc.Pid, proc.GameAssemblyBase);
            }, ct);

            Session = session;
            Post(() =>
            {
                ConnectionInfo.Value = $"pid={pid}  GameAssembly @ 0x{(long)baseAddr:X}";
                IsConnected.Value = true;
                Status.Value = "Connected.  Dumping item bank…";
            });
            progress?.Invoke("Dumping item bank…");

            // Pull item bank + inventory off-thread too — this walks a big list.
            var (foods, groups, summary) = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var items = session.ListFoodBank()
                    .OrderBy(i => i.Tier)
                    .ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                ct.ThrowIfCancellationRequested();
                var g = session.ListCurrentInventory()
                    .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new InventoryGroup(x.First(), x.Count()))
                    .OrderBy(x => x.Sample.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var sum =
                    $"{session.CurrentFoodCount()} / {session.CurrentCountMax()}" +
                    $"   (key items: {session.CurrentKeyCount()})";
                return (items, g, sum);
            }, ct);

            Post(() =>
            {
                FoodBank.Clear();
                FoodBank.AddRange(foods);
                RecomputeFilter(); // already ticks FoodBankVersion
                CurrentInventory.Clear();
                CurrentInventory.AddRange(groups);
                InventoryVersion.Value++;
                InventorySummary.Value = summary;
                Status.Value = "Ready.";
            });
            Toast?.Invoke($"Connected — {foods.Count} item definition(s).");
        }
        catch (OperationCanceledException)
        {
            Post(() =>
            {
                Session?.Dispose();
                Session = null;
                IsConnected.Value = false;
                ConnectionInfo.Value = "(no game attached)";
                Status.Value = "Connect cancelled.";
            });
            Toast?.Invoke("Connect cancelled.");
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                IsConnected.Value = false;
                ConnectionInfo.Value = "(attach failed)";
                Status.Value = "Connect failed.";
            });
            Toast?.Invoke($"Connect failed: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        Session?.Dispose();
        Session = null;
        IsConnected.Value = false;
        ConnectionInfo.Value = "(no game attached)";
        InventorySummary.Value = "—";

        FoodBank.Clear();
        RecomputeFilter();
        CurrentInventory.Clear();
        FoodBankVersion.Value++;
        InventoryVersion.Value++;

        Status.Value = "Disconnected.";
        Toast?.Invoke("Disconnected.");
    }

    public void RefreshAll()
    {
        if (Session is null) return;
        try
        {
            var items = Session.ListFoodBank()
                .OrderBy(i => i.Tier)
                .ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            FoodBank.Clear();
            FoodBank.AddRange(items);
            RecomputeFilter();
            FoodBankVersion.Value++;

            RefreshInventoryOnly();
            Toast?.Invoke($"Refreshed — {items.Count} item(s).");
        }
        catch (Exception ex)
        {
            HandleActionException(ex, "Refresh");
        }
    }

    public void RefreshInventoryOnly()
    {
        if (Session is null) return;
        try
        {
            var groups = Session.ListCurrentInventory()
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => new InventoryGroup(g.First(), g.Count()))
                .OrderBy(g => g.Sample.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CurrentInventory.Clear();
            CurrentInventory.AddRange(groups);
            InventoryVersion.Value++;

            InventorySummary.Value =
                $"{Session.CurrentFoodCount()} / {Session.CurrentCountMax()}" +
                $"   (key items: {Session.CurrentKeyCount()})";
        }
        catch (Exception ex)
        {
            HandleActionException(ex, "Inventory refresh");
        }
    }

    // ---- actions ------------------------------------------------------------
    public void AddSelectedFood(ItemInfo? food, int count)
    {
        if (Session is null || food is null) return;
        try
        {
            Session.AddItem(food.Value.DataPtr, count);
            Toast?.Invoke($"Added x{count} {food.Value.Id}");
            RefreshInventoryOnly();
        }
        catch (Exception ex)
        {
            HandleActionException(ex, "Add");
        }
    }

    public void RemoveGroup(InventoryGroup g, int count)
    {
        if (Session is null) return;
        try
        {
            var n = Math.Min(count, g.Count);
            Session.RemoveItem(g.Sample.DataPtr, n);
            Toast?.Invoke($"Removed x{n} {g.Sample.Id}");
            RefreshInventoryOnly();
        }
        catch (Exception ex)
        {
            HandleActionException(ex, "Remove");
        }
    }

    public void ClearAllFood()
    {
        if (Session is null) return;
        try
        {
            Session.ClearFood();
            Toast?.Invoke("Cleared all food.");
            RefreshInventoryOnly();
        }
        catch (Exception ex)
        {
            HandleActionException(ex, "Clear");
        }
    }

    public void SetCountMax(int value)
    {
        if (Session is null) return;
        try
        {
            var clamped = Math.Clamp(value, 1, 9999);
            Session.SetBaseCountMax(clamped);
            RefreshInventoryOnly();
            Toast?.Invoke($"CountMax → {clamped}");
        }
        catch (Exception ex)
        {
            HandleActionException(ex, "SetCountMax");
        }
    }

    // -------------------------------------------------------------------------
    private void RecomputeFilter()
    {
        var q = FoodFilter.Value.Trim();
        FoodBankFiltered.Clear();
        if (q.Length == 0)
            FoodBankFiltered.AddRange(FoodBank);
        else
            FoodBankFiltered.AddRange(
                FoodBank.Where(i =>
                    i.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)));
        FoodBankVersion.Value++;
    }
}

/// <summary>A (food, quantity) row for the "current inventory" tab.</summary>
public readonly record struct InventoryGroup(ItemInfo Sample, int Count)
{
}