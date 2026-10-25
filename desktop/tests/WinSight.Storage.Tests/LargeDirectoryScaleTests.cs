using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Storage;

internal static class LargeDirectoryScaleTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        await test("S1-SCALE reconcile and enqueue stay bounded on a 20k-file directory", async () =>
        {
            var sandbox = Path.Combine(root, "s" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(sandbox);
            const int count = 20000;
            for (var i = 0; i < count; i++)
            {
                var dir = Path.Combine(sandbox, "d" + (i % 100).ToString("D3"));
                Directory.CreateDirectory(dir);
                await using var stream = File.Create(Path.Combine(dir, "f" + i.ToString("D5") + ".txt"));
            }

            await using var repo = new SqliteIndexRepository(Path.Combine(sandbox, "scale.sqlite"));
            await repo.InitializeAsync();
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            await using var queue = new IncrementalIndexQueue(repo, quietPeriod: TimeSpan.FromSeconds(1), maxWait: TimeSpan.FromSeconds(10));

            var cold = System.Diagnostics.Stopwatch.StartNew();
            await queue.ReconcileRootAsync(sandbox, false, force: true);
            cold.Stop();
            var afterCold = (await repo.GetTaskCountsAsync()).Values.Sum();

            var warm = System.Diagnostics.Stopwatch.StartNew();
            await queue.ReconcileRootAsync(sandbox, false, force: true);
            warm.Stop();
            var afterWarm = (await repo.GetTaskCountsAsync()).Values.Sum();

            var before = GC.GetTotalMemory(true);
            Interlocked.Increment(ref Noop);
            var managedMb = before / (1024.0 * 1024.0);
            Console.WriteLine($"SCALE cold={cold.Elapsed.TotalSeconds:F1}s warm={warm.Elapsed.TotalSeconds:F1}s tasksAfterCold={afterCold} tasksAfterWarm={afterWarm} managedMb={managedMb:F0}");
            Require(afterCold == count, $"Cold reconcile must queue every file (saw {afterCold}).");
            // A warm reconcile over an unchanged tree must not re-queue anything.
            Require(afterWarm == afterCold, $"Warm reconcile must be idempotent (cold={afterCold}, warm={afterWarm}).");
            Require(cold.Elapsed.TotalSeconds < 300, "Cold reconcile of 20k files exceeded the 5-minute bound.");
            Require(warm.Elapsed.TotalSeconds < 300, "Warm reconcile of 20k files exceeded the 5-minute bound.");
        });
    }

    private static int Noop;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
