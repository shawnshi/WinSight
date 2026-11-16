using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Runtime;
using WinSight.Infrastructure.Storage;

internal static class QueueWatcherCases
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        await test("S1-WATCHER single save queues one file and does not re-walk the root", async () =>
        {
            var watched = Path.Combine(root, "queue-watch");
            Directory.CreateDirectory(watched);
            // Pre-existing, unchanged content: a full-root reconcile would re-queue it.
            for (var i = 0; i < 3; i++) await File.WriteAllTextAsync(Path.Combine(watched, $"old{i}.txt"), "old");
            await using var repo = new SqliteIndexRepository(Path.Combine(root, "queue-watch.sqlite"));
            await repo.InitializeAsync();
            var batches = new List<IReadOnlyList<string>>();
            await using var coordinator = new BackgroundIndexCoordinator(
                new AppSettings { Folders = [watched], ProcessorOnly = false },
                (request, token) => Task.CompletedTask,
                error => throw new InvalidOperationException("Unexpected watcher error.", error),
                debounce: TimeSpan.FromMilliseconds(40),
                repository: repo,
                indexPaths: (roots, paths, code, token) => { lock (batches) batches.Add(paths); return Task.CompletedTask; });

            // First pass reconciles (forced) so the pre-existing files are known to the queue.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && (await repo.GetTaskCountsAsync()).Values.Sum() == 0) await Task.Delay(50);

            var target = Path.Combine(watched, "saved.txt");
            await File.WriteAllTextAsync(target, "saved");
            while (DateTime.UtcNow < deadline && (await repo.GetTaskAsync(FilePolicy.Normalize(target))) is not null) await Task.Delay(50);
            while (DateTime.UtcNow < deadline && !batches.Any(b => b.Any(p => string.Equals(p, FilePolicy.Normalize(target), StringComparison.OrdinalIgnoreCase)))) await Task.Delay(50);

            IReadOnlyList<string>[] snapshot;
            lock (batches) snapshot = batches.ToArray();
            Require(snapshot.Length > 0, "The watcher path must reach the indexer.");
            Require(snapshot.All(b => b.Count < 4), "An ordinary save must not be indexed as a whole-root batch.");
            var first = (await repo.GetTaskCountsAsync()).Values.Sum();
            await Task.Delay(300);
            var second = (await repo.GetTaskCountsAsync()).Values.Sum();
            Require(second <= first, "Queued work must not grow while the stub indexer makes no progress.");
        });

        await test("S1-WATCHER-OVERFLOW forces a reconcile even after the first pass", async () =>
        {
            var watched = Path.Combine(root, "queue-overflow");
            Directory.CreateDirectory(watched);
            var known = Path.Combine(watched, "known.txt");
            await File.WriteAllTextAsync(known, "known");
            await using var repo = new SqliteIndexRepository(Path.Combine(root, "queue-overflow.sqlite"));
            await repo.InitializeAsync();
            await using var coordinator = new BackgroundIndexCoordinator(
                new AppSettings { Folders = [watched], ProcessorOnly = false },
                (request, token) => Task.CompletedTask,
                error => { },
                debounce: TimeSpan.FromMilliseconds(40),
                repository: repo,
                indexPaths: (roots, paths, code, token) => Task.CompletedTask);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && (await repo.GetTaskCountsAsync()).Values.Sum() == 0) await Task.Delay(50);

            // A file written while watching is missed on purpose; the overflow must recover it by diff.
            var missed = Path.Combine(watched, "missed.txt");
            await File.WriteAllTextAsync(missed, "missed while buffer overflowed");
            coordinator.Queue!.OnWatcherError(watched, new InternalBufferOverflowException());
            Require(coordinator.WatcherOverflows >= 1, "Overflow must be counted.");
            // The production watcher error handler signals a pass; reproduce that contract here.
            await File.WriteAllTextAsync(Path.Combine(watched, "trigger.txt"), "trigger");
            await Task.Delay(200);
            Require(await repo.GetTaskAsync(FilePolicy.Normalize(missed)) is not null, "Overflow must reconcile the missed file.");
        });

        await test("S1-QUEUE-BACKEND-LIVE-STATUS live DesktopBackend reports real queue status and non-null LastReconcileUtc", async () =>
        {
            var dataDir = Path.Combine(root, "backend-queue-status-data");
            var watchDir = Path.Combine(root, "backend-queue-status-watch");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(watchDir);
            await File.WriteAllTextAsync(Path.Combine(watchDir, "test.txt"), "hello world");

            var backend = await DesktopBackend.CreateAsync(new DesktopOptions(dataDir) { IsOnAcPower = () => true });
            var settings = await backend.LoadSettingsAsync();
            settings.Folders = [watchDir];
            settings.BackgroundIndexingEnabled = true;
            settings.SetUp = true;
            settings.ProcessorOnly = false;
            await backend.SaveSettingsAsync(settings);

            // Wait up to 5 seconds for background coordinator and initial reconcile
            var deadline = DateTime.UtcNow.AddSeconds(5);
            IndexingQueueStatus? st = null;
            while (DateTime.UtcNow < deadline)
            {
                st = await backend.GetIndexingQueueStatusAsync();
                if (st.LastReconcileUtc is not null) break;
                await Task.Delay(100);
            }

            Require(st != null && st.UsnState != null, "GetIndexingQueueStatusAsync returned null or null UsnState");
            Require(st!.UsnState != "not-implemented", "UsnState must not be not-implemented");
            var eng = await backend.GetStatusAsync();
            Require(st.LastReconcileUtc is not null, $"LastReconcileUtc must be populated after initial reconcile. Got: UsnState={st.UsnState}, Pending={st.Pending}, Detail={eng.Detail}");
            await backend.DisposeAsync();
        });

        await test("S1-P1-CODE-ISOLATION-AND-NO-FULLSCAN doc save wakes on delayed timer without full scan and code save routes strictly to codeQueue", async () =>
        {
            var docRoot = Path.Combine(root, "p1-doc-root");
            var codeRoot = Path.Combine(root, "p1-code-root");
            Directory.CreateDirectory(docRoot);
            Directory.CreateDirectory(codeRoot);

            await using var docRepo = new SqliteIndexRepository(Path.Combine(root, "p1-doc.sqlite"));
            await using var codeRepo = new SqliteIndexRepository(Path.Combine(root, "p1-code.sqlite"));
            await docRepo.InitializeAsync();
            await codeRepo.InitializeAsync();

            var fullScanCount = 0;
            var docBatches = new List<string>();
            var codeBatches = new List<string>();
            await using var coordinator = new BackgroundIndexCoordinator(
                new AppSettings { Folders = [docRoot], CodeFolders = [codeRoot], ProcessorOnly = false },
                (request, token) => { Interlocked.Increment(ref fullScanCount); return Task.CompletedTask; },
                error => throw new InvalidOperationException("Unexpected watcher error.", error),
                debounce: TimeSpan.FromMilliseconds(40),
                policyPoll: TimeSpan.FromSeconds(60),
                repository: docRepo,
                codeRepository: codeRepo,
                indexPaths: (roots, paths, code, token) =>
                {
                    var target = code ? codeBatches : docBatches;
                    lock (target) target.AddRange(paths);
                    return Task.CompletedTask;
                });

            // Wait for initial reconcile pass to finish before triggering live watcher saves.
            var initDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < initDeadline && (coordinator.Queue?.LastReconcileUtc is null || coordinator.CodeQueue?.LastReconcileUtc is null))
                await Task.Delay(50);
            Require(coordinator.Queue?.LastReconcileUtc is not null && coordinator.CodeQueue?.LastReconcileUtc is not null, "Both queues must complete initial reconcile.");

            var docFile = Path.Combine(docRoot, "note.txt");
            var codeFile = Path.Combine(codeRoot, "Program.cs");
            await File.WriteAllTextAsync(docFile, "hello doc");
            await File.WriteAllTextAsync(codeFile, "class Program {}");

            var normDoc = FilePolicy.Normalize(docFile);
            var normCode = FilePolicy.Normalize(codeFile);
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                bool gotDoc, gotCode;
                lock (docBatches) gotDoc = docBatches.Contains(normDoc, StringComparer.OrdinalIgnoreCase);
                lock (codeBatches) gotCode = codeBatches.Contains(normCode, StringComparer.OrdinalIgnoreCase);
                if (gotDoc && gotCode && await docRepo.GetTaskAsync(normDoc) is null && await codeRepo.GetTaskAsync(normCode) is null)
                    break;
                await Task.Delay(50);
            }

            bool docDelivered, codeDelivered;
            lock (docBatches) docDelivered = docBatches.Contains(normDoc, StringComparer.OrdinalIgnoreCase) && !docBatches.Contains(normCode, StringComparer.OrdinalIgnoreCase);
            lock (codeBatches) codeDelivered = codeBatches.Contains(normCode, StringComparer.OrdinalIgnoreCase) && !codeBatches.Contains(normDoc, StringComparer.OrdinalIgnoreCase);

            Require(docDelivered, "Document save must wake via delayed timer and deliver only to doc batches.");
            Require(codeDelivered, "Code save must route exclusively to codeQueue and deliver only to code batches.");
            Require(await docRepo.GetTaskAsync(normCode) is null, "docRepo index_tasks must not receive code files.");
            Require(await codeRepo.GetTaskAsync(normDoc) is null, "codeRepo index_tasks must not receive doc files.");
            Require(Volatile.Read(ref fullScanCount) == 0, $"Full-root fallback index delegate must not execute when active queues are present (count={fullScanCount}).");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
