using WinSight.Core;
using WinSight.Infrastructure.Runtime;

internal static class MemoryPressureCases
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        await test("S2-MEM caches are released when memory pressure appears", async () =>
        {
            var watched = Path.Combine(root, "pressure");
            Directory.CreateDirectory(watched);
            var released = 0;
            var signals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(true, false, 100),
                idleTime: () => TimeSpan.Zero,
                memoryStatus: () => new SystemMemoryInfo(512UL * 1024 * 1024, 92.0),
                onError: _ => { });
            await using var coordinator = new BackgroundIndexCoordinator(
                new AppSettings { Folders = [watched], ProcessorOnly = false },
                (_, _) => Task.CompletedTask,
                _ => { },
                debounce: TimeSpan.FromMilliseconds(20),
                hostSignals: signals,
                releaseCaches: _ => { Interlocked.Increment(ref released); return Task.CompletedTask; });
            var budgets = coordinator.BudgetController.ComputeBudgets();
            await Task.Delay(200);
            Require(Interlocked.CompareExchange(ref released, 0, 0) >= 1, "Memory pressure must trigger a cache release.");
            Require(budgets.Any(b => b.State.Contains(":memory")), "Budget rows must mark memory pressure.");
        });

        await test("S2-MEM no release without pressure", async () =>
        {
            var watched = Path.Combine(root, "no-pressure");
            Directory.CreateDirectory(watched);
            var released = 0;
            var signals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(true, false, 100),
                idleTime: () => TimeSpan.FromMinutes(30),
                memoryStatus: () => new SystemMemoryInfo(8UL * 1024 * 1024 * 1024, 40.0),
                onError: _ => { });
            await using var coordinator = new BackgroundIndexCoordinator(
                new AppSettings { Folders = [watched], ProcessorOnly = false },
                (_, _) => Task.CompletedTask,
                _ => { },
                debounce: TimeSpan.FromMilliseconds(20),
                hostSignals: signals,
                releaseCaches: _ => { Interlocked.Increment(ref released); return Task.CompletedTask; });
            coordinator.BudgetController.ComputeBudgets();
            await Task.Delay(150);
            Require(Interlocked.CompareExchange(ref released, 0, 0) == 0, "Caches must not be released without pressure.");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
