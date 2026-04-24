using System.Text;

using GirlsMadeInfinitePudding.GameAbi;
using GirlsMadeInfinitePudding.ProcessMemory;

namespace GirlsMadeInfinitePudding;

/// <summary>
///     Opt-in smoke test for the data layer.  Invoke with
///     <code>dotnet run -- --smoketest</code>
///     after launching the game.  Useful while the UI is still being built.
/// </summary>
public static class SmokeTest
{
    public static int Run(string[]? _)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            using var proc = GameProcess.Attach();
            using var session = new GameSession(proc);
            Console.WriteLine(
                $"Attached pid={proc.Pid}  GameAssembly @ 0x{(long)proc.GameAssemblyBase:X} " +
                $"size=0x{proc.GameAssemblySize:X}");

            session.RefreshSingletons();
            Console.WriteLine($"Inventory      = 0x{(long)session.Inventory:X}");
            Console.WriteLine($"ItemBankHolder = 0x{(long)session.ItemBankHolder:X}");
            Console.WriteLine($"StoryManager   = 0x{(long)session.StoryManager:X}");

            Console.WriteLine("\n-- ItemBank dump (foods only) --");
            foreach (var it in session.ListFoodBank())
                Console.WriteLine($"  [{it.Type}] {it.Id,-24} tier={it.Tier} prio={it.Priority}");

            Console.WriteLine(
                $"\n-- Current inventory ({session.CurrentFoodCount()}/{session.CurrentCountMax()}) --");
            foreach (var g in session.ListCurrentInventory().GroupBy(i => i.Id))
                Console.WriteLine($"  x{g.Count(),-3} {g.Key}");

            Console.WriteLine($"\n-- KeyItems ({session.CurrentKeyCount()}) --");
            foreach (var it in session.ListCurrentKeyItems())
                Console.WriteLine($"  {it.Id}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[!] {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}