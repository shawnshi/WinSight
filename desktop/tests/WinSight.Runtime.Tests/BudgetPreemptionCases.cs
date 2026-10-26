using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Runtime;
using WinSight.Infrastructure.Storage;

internal static class BudgetPreemptionCases
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        await test("S2-BUDGET-AC-IDLE AC with idle >= 5min grants backfill 2/1 and complex 1/0", () =>
        {
            var signals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(IsAcPower: true, IsBatterySaver: false, BatteryPercent: 100),
                idleTime: () => TimeSpan.FromMinutes(6),
                memoryStatus: () => new SystemMemoryInfo(AvailablePhysicalBytes: 8UL * 1024 * 1024 * 1024, CommitPercent: 40.0));

            var controller = new ResourceBudgetController(signals);
            var budgets = controller.ComputeBudgets();

            Require(budgets.Count == 4, "Expected 4 budget states.");
            var query = budgets.First(b => b.Class == "InteractiveQuery");
            Require(query.ExtractConcurrency == 1 && query.InferenceConcurrency == 1 && query.State == "active",
                "InteractiveQuery should be 1/1 active.");

            var newFile = budgets.First(b => b.Class == "NewFileIndex");
            Require(newFile.ExtractConcurrency == 1 && newFile.InferenceConcurrency == 1 && newFile.State == "active",
                "NewFileIndex should be 1/1 active.");

            var backfill = budgets.First(b => b.Class == "Backfill");
            Require(backfill.ExtractConcurrency == 2 && backfill.InferenceConcurrency == 1 && backfill.State == "active",
                "Backfill should be 2/1 active under AC + idle.");

            var complex = budgets.First(b => b.Class == "ComplexParse");
            Require(complex.ExtractConcurrency == 1 && complex.InferenceConcurrency == 0 && complex.State == "active",
                "ComplexParse should be 1/0 active under AC + idle.");

            return Task.CompletedTask;
        });

        await test("S2-BUDGET-AC-ACTIVE AC with active user (<5min idle) pauses backfill and complex parse", () =>
        {
            var signals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(IsAcPower: true, IsBatterySaver: false, BatteryPercent: 100),
                idleTime: () => TimeSpan.FromMinutes(1),
                memoryStatus: () => new SystemMemoryInfo(AvailablePhysicalBytes: 8UL * 1024 * 1024 * 1024, CommitPercent: 40.0));

            var controller = new ResourceBudgetController(signals);
            var budgets = controller.ComputeBudgets();

            var backfill = budgets.First(b => b.Class == "Backfill");
            Require(backfill.ExtractConcurrency == 0 && backfill.InferenceConcurrency == 0 && backfill.State == "paused:idle",
                "Backfill should be paused:idle when user is active.");

            var complex = budgets.First(b => b.Class == "ComplexParse");
            Require(complex.ExtractConcurrency == 0 && complex.InferenceConcurrency == 0 && complex.State == "paused:idle",
                "ComplexParse should be paused:idle when user is active.");

            return Task.CompletedTask;
        });

        await test("S2-BUDGET-BATTERY Battery stops backfill and complex parse with paused:battery", () =>
        {
            var signals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(IsAcPower: false, IsBatterySaver: false, BatteryPercent: 75),
                idleTime: () => TimeSpan.FromMinutes(10),
                memoryStatus: () => new SystemMemoryInfo(AvailablePhysicalBytes: 8UL * 1024 * 1024 * 1024, CommitPercent: 40.0));

            var controller = new ResourceBudgetController(signals);
            var budgets = controller.ComputeBudgets();

            var newFile = budgets.First(b => b.Class == "NewFileIndex");
            Require(newFile.ExtractConcurrency == 1 && newFile.InferenceConcurrency == 1 && newFile.State == "active",
                "NewFileIndex should remain 1/1 active on battery.");

            var backfill = budgets.First(b => b.Class == "Backfill");
            Require(backfill.ExtractConcurrency == 0 && backfill.InferenceConcurrency == 0 && backfill.State == "paused:battery",
                "Backfill should be paused:battery on battery power.");

            var complex = budgets.First(b => b.Class == "ComplexParse");
            Require(complex.ExtractConcurrency == 0 && complex.InferenceConcurrency == 0 && complex.State == "paused:battery",
                "ComplexParse should be paused:battery on battery power.");

            return Task.CompletedTask;
        });

        await test("S2-BUDGET-ENERGY-SAVER Saver or low battery (<20%) pauses with paused:power", () =>
        {
            // Case A: Saver active
            var saverSignals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(IsAcPower: false, IsBatterySaver: true, BatteryPercent: 50),
                idleTime: () => TimeSpan.FromMinutes(10),
                memoryStatus: () => new SystemMemoryInfo(AvailablePhysicalBytes: 8UL * 1024 * 1024 * 1024, CommitPercent: 40.0));

            var saverController = new ResourceBudgetController(saverSignals);
            var saverBudgets = saverController.ComputeBudgets();

            var backfillA = saverBudgets.First(b => b.Class == "Backfill");
            Require(backfillA.State == "paused:power", "Saver must yield paused:power for backfill.");
            var complexA = saverBudgets.First(b => b.Class == "ComplexParse");
            Require(complexA.State == "paused:power", "Saver must yield paused:power for complex parse.");

            // Case B: Low battery < 20%
            var lowBatterySignals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(IsAcPower: false, IsBatterySaver: false, BatteryPercent: 15),
                idleTime: () => TimeSpan.FromMinutes(10),
                memoryStatus: () => new SystemMemoryInfo(AvailablePhysicalBytes: 8UL * 1024 * 1024 * 1024, CommitPercent: 40.0));

            var lowBatteryController = new ResourceBudgetController(lowBatterySignals);
            var lowBatteryBudgets = lowBatteryController.ComputeBudgets();

            var backfillB = lowBatteryBudgets.First(b => b.Class == "Backfill");
            Require(backfillB.State == "paused:power", "Low battery < 20% must yield paused:power for backfill.");
            var complexB = lowBatteryBudgets.First(b => b.Class == "ComplexParse");
            Require(complexB.State == "paused:power", "Low battery < 20% must yield paused:power for complex parse.");

            return Task.CompletedTask;
        });

        await test("S2-BUDGET-MEMORY-PRESSURE Memory pressure halves non-interactive concurrency and appends :memory", () =>
        {
            var memoryPressureFired = false;
            var signals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(IsAcPower: true, IsBatterySaver: false, BatteryPercent: 100),
                idleTime: () => TimeSpan.FromMinutes(10),
                memoryStatus: () => new SystemMemoryInfo(AvailablePhysicalBytes: 500UL * 1024 * 1024, CommitPercent: 88.0));

            var controller = new ResourceBudgetController(signals, onMemoryPressure: () => { memoryPressureFired = true; });
            var budgets = controller.ComputeBudgets();

            Require(memoryPressureFired, "Memory pressure callback must be invoked.");

            var query = budgets.First(b => b.Class == "InteractiveQuery");
            Require(query.ExtractConcurrency == 1 && query.InferenceConcurrency == 1 && query.State == "active",
                "InteractiveQuery is interactive and must NOT be halved or tagged :memory.");

            var newFile = budgets.First(b => b.Class == "NewFileIndex");
            Require(newFile.ExtractConcurrency == 0 && newFile.InferenceConcurrency == 0 && newFile.State == "active:memory",
                "NewFileIndex 1/1 must halve to 0/0 and append :memory.");

            var backfill = budgets.First(b => b.Class == "Backfill");
            Require(backfill.ExtractConcurrency == 1 && backfill.InferenceConcurrency == 0 && backfill.State == "active:memory",
                "Backfill 2/1 must halve to 1/0 and append :memory.");

            var complex = budgets.First(b => b.Class == "ComplexParse");
            Require(complex.ExtractConcurrency == 0 && complex.InferenceConcurrency == 0 && complex.State == "active:memory",
                "ComplexParse 1/0 must halve to 0/0 and append :memory.");

            return Task.CompletedTask;
        });

        await test("S2-PREEMPTION-SEARCH-CANCELS-BACKFILL-INFERENCE Interactive query cancels inflight backfill inference token", () =>
        {
            var signals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(IsAcPower: true, IsBatterySaver: false, BatteryPercent: 100),
                idleTime: () => TimeSpan.FromMinutes(10),
                memoryStatus: () => new SystemMemoryInfo(AvailablePhysicalBytes: 8UL * 1024 * 1024 * 1024, CommitPercent: 40.0));

            var controller = new ResourceBudgetController(signals);
            var tokenBefore = controller.BackfillInferenceToken;
            Require(!tokenBefore.IsCancellationRequested, "Token should be live before preemption.");

            controller.PreemptForInteractiveQuery();

            Require(tokenBefore.IsCancellationRequested, "Inflight backfill token must be canceled by interactive query.");
            var tokenAfter = controller.BackfillInferenceToken;
            Require(!tokenAfter.IsCancellationRequested, "Fresh backfill token must be live after preemption.");

            return Task.CompletedTask;
        });

        await test("S2-BATTERY-PRIORITIZE-RECENT-SMALL-FILES Prioritizes recent small files on battery", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var dir = Path.Combine(root, "prio-test");
            Directory.CreateDirectory(dir);

            var oldLarge = Path.Combine(dir, "old-large.txt");
            var recentLarge = Path.Combine(dir, "recent-large.txt");
            var recentSmall = Path.Combine(dir, "recent-small.txt");

            File.WriteAllBytes(oldLarge, new byte[50000]);
            File.SetLastWriteTimeUtc(oldLarge, DateTime.UtcNow.AddHours(-2));

            File.WriteAllBytes(recentLarge, new byte[50000]);
            File.SetLastWriteTimeUtc(recentLarge, DateTime.UtcNow);

            File.WriteAllBytes(recentSmall, new byte[1000]);
            File.SetLastWriteTimeUtc(recentSmall, DateTime.UtcNow);

            var tasks = new List<IndexTask>
            {
                new IndexTask(oldLarge, dir, false, "Add", null, "Ready", 0, now, now),
                new IndexTask(recentLarge, dir, false, "Add", null, "Ready", 0, now, now),
                new IndexTask(recentSmall, dir, false, "Add", null, "Ready", 0, now, now)
            };

            var prioritized = IncrementalIndexQueue.PrioritizeRecentSmallFiles(tasks);

            Require(prioritized[0].Path == recentSmall, "Recent small file should come first.");
            Require(prioritized[1].Path == recentLarge, "Recent large file should come second.");
            Require(prioritized[2].Path == oldLarge, "Old large file should come last.");

            return Task.CompletedTask;
        });

        await test("S2-RESUME-FORCES-RECONCILE Resume forces root reconcile and updates LastReconcileUtc", async () =>
        {
            var watched = Path.Combine(root, "resume-watch");
            Directory.CreateDirectory(watched);
            var preFile = Path.Combine(watched, "pre.txt");
            await File.WriteAllTextAsync(preFile, "pre");

            await using var repo = new SqliteIndexRepository(Path.Combine(root, "resume-test.sqlite"));
            await repo.InitializeAsync();

            var signals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(IsAcPower: true, IsBatterySaver: false, BatteryPercent: 100),
                idleTime: () => TimeSpan.FromMinutes(10),
                memoryStatus: () => new SystemMemoryInfo(AvailablePhysicalBytes: 8UL * 1024 * 1024 * 1024, CommitPercent: 40.0));

            await using var coordinator = new BackgroundIndexCoordinator(
                new AppSettings { Folders = [watched], ProcessorOnly = false },
                (request, token) => Task.CompletedTask,
                error => { },
                debounce: TimeSpan.FromMilliseconds(40),
                repository: repo,
                hostSignals: signals);

            // Wait for initial reconcile
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && coordinator.Queue?.LastReconcileUtc is null)
            {
                await Task.Delay(50);
            }

            var firstReconcile = coordinator.Queue?.LastReconcileUtc;
            Require(firstReconcile is not null, "Queue should have completed initial reconcile.");

            await Task.Delay(100);
            var resumeFile = Path.Combine(watched, "missed-on-resume.txt");
            await File.WriteAllTextAsync(resumeFile, "missed");

            // Explicitly trigger resume
            await coordinator.HandleResumeAsync();

            var secondReconcile = coordinator.Queue?.LastReconcileUtc;
            Require(secondReconcile is not null && secondReconcile > firstReconcile,
                "Resume must force a new reconcile pass.");

            var task = await repo.GetTaskAsync(FilePolicy.Normalize(resumeFile));
            Require(task is not null, "Forced reconcile on resume must have enqueued the missed file.");
        });

        await test("S2-QUEUE-STATUS-REAL-COUNTS Returns accurate counts, overflows, dropped, and budgets", async () =>
        {
            var dir = Path.Combine(root, "status-test");
            Directory.CreateDirectory(dir);
            await using var repo = new SqliteIndexRepository(Path.Combine(root, "status-test.sqlite"));
            await repo.InitializeAsync();

            var now = DateTimeOffset.UtcNow;
            await repo.EnqueueIndexTaskAsync(new IndexTask(Path.Combine(dir, "p1.txt"), dir, false, "Add", null, "Pending", 0, now, now));
            await repo.EnqueueIndexTaskAsync(new IndexTask(Path.Combine(dir, "s1.txt"), dir, false, "Add", null, "Stabilizing", 0, now, now));
            await repo.EnqueueIndexTaskAsync(new IndexTask(Path.Combine(dir, "r1.txt"), dir, false, "Add", null, "Ready", 0, now, now));
            await repo.EnqueueIndexTaskAsync(new IndexTask(Path.Combine(dir, "r2.txt"), dir, false, "Add", null, "Ready", 0, now, now));
            await repo.EnqueueIndexTaskAsync(new IndexTask(Path.Combine(dir, "run1.txt"), dir, false, "Add", null, "Running", 0, now, now));
            await repo.EnqueueIndexTaskAsync(new IndexTask(Path.Combine(dir, "f1.txt"), dir, false, "Add", null, "Failed", 0, now, now));
            await repo.EnqueueIndexTaskAsync(new IndexTask(Path.Combine(dir, "d1.txt"), dir, false, "Add", null, "Deferred", 0, now, now));

            var signals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(IsAcPower: true, IsBatterySaver: false, BatteryPercent: 100),
                idleTime: () => TimeSpan.FromMinutes(10),
                memoryStatus: () => new SystemMemoryInfo(AvailablePhysicalBytes: 8UL * 1024 * 1024 * 1024, CommitPercent: 40.0));

            await using var coordinator = new BackgroundIndexCoordinator(
                new AppSettings { Folders = [dir], ProcessorOnly = false },
                (request, token) => Task.CompletedTask,
                error => { },
                repository: repo,
                hostSignals: signals);

            coordinator.Queue?.OnWatcherError(dir, new Exception("synthetic overflow"));

            var status = await coordinator.GetIndexingQueueStatusAsync();

            Require(status.Pending == 1, $"Expected Pending=1, got {status.Pending}");
            Require(status.Stabilizing == 1, $"Expected Stabilizing=1, got {status.Stabilizing}");
            Require(status.Ready == 2, $"Expected Ready=2, got {status.Ready}");
            Require(status.Running == 1, $"Expected Running=1, got {status.Running}");
            Require(status.Failed == 1, $"Expected Failed=1, got {status.Failed}");
            Require(status.Deferred == 1, $"Expected Deferred=1, got {status.Deferred}");
            Require(status.WatcherOverflows == 1, $"Expected WatcherOverflows=1, got {status.WatcherOverflows}");
            // The status must expose the queue's real USN state, not a placeholder.
            Require(!string.IsNullOrWhiteSpace(status.UsnState) && status.UsnState != "not-implemented",
                $"Expected a real USN state, got {status.UsnState}");
            Require(status.Budgets.Count == 4, $"Expected 4 budgets, got {status.Budgets.Count}");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
