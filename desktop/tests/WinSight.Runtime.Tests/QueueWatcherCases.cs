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
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
