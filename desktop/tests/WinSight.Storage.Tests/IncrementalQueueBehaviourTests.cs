using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Storage;

// Queue-semantics tests that actually run. They use a real SQLite repository and real files in a sandbox
// directory, with shortened quiet/max-wait windows so the real code paths execute quickly.
internal static class IncrementalQueueBehaviourTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        static string Sandbox(string root)
        {
            // Short names keep the SQLite path well inside the legacy MAX_PATH limit of the fixture root.
            var path = Path.Combine(root, "s" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(path);
            return path;
        }

        await test("S1-Q1 temporary names never enter the queue", async () =>
        {
            var sandbox = Sandbox(root);
            await using var repo = new SqliteIndexRepository(Path.Combine(sandbox, "q1.sqlite"));
            await repo.InitializeAsync();
            await using var queue = new IncrementalIndexQueue(repo, quietPeriod: TimeSpan.FromMilliseconds(50), maxWait: TimeSpan.FromSeconds(2));
            await queue.InitializeAsync();

            foreach (var name in new[] { "draft.tmp", "part.part", "big.crdownload", "~$locked.docx" })
            {
                var path = Path.Combine(sandbox, name);
                await File.WriteAllTextAsync(path, "x");
                await queue.EnqueueEventAsync(path, sandbox, false, WatcherChangeTypes.Created);
                Require(await repo.GetTaskAsync(path) is null, name + " must not be queued.");
            }
            var real = Path.Combine(sandbox, "real.txt");
            await File.WriteAllTextAsync(real, "x");
            await queue.EnqueueEventAsync(real, sandbox, false, WatcherChangeTypes.Created);
            Require(await repo.GetTaskAsync(real) is not null, "Ordinary file must be queued.");
        });

        await test("S1-Q2 repeated events coalesce onto one task and keep the first sighting", async () =>
        {
            var sandbox = Sandbox(root);
            await using var repo = new SqliteIndexRepository(Path.Combine(sandbox, "q2.sqlite"));
            await repo.InitializeAsync();
            await using var queue = new IncrementalIndexQueue(repo, quietPeriod: TimeSpan.FromMilliseconds(50), maxWait: TimeSpan.FromSeconds(2));
            await queue.InitializeAsync();
            var path = Path.Combine(sandbox, "coalesce.txt");
            await File.WriteAllTextAsync(path, "one");
            await queue.EnqueueEventAsync(path, sandbox, false, WatcherChangeTypes.Created);
            var first = await repo.GetTaskAsync(path);
            await Task.Delay(120);
            await File.WriteAllTextAsync(path, "two");
            await queue.EnqueueEventAsync(path, sandbox, false, WatcherChangeTypes.Changed);
            await queue.EnqueueEventAsync(path, sandbox, false, WatcherChangeTypes.Changed);
            var coalesced = await repo.GetTaskAsync(path);
            Require(coalesced is not null, "Repeated events must leave one task for the path.");
            Require(coalesced!.SeenUtc == first!.SeenUtc, "Coalescing must keep the first sighting time.");
            Require(coalesced.Kind is "Update" or "Add", "Coalesced task must stay an add/update.");
            Require((await repo.GetTaskCountsAsync()).Values.Sum() == 1, "Exactly one task row may exist after coalescing.");
        });

        await test("S1-Q3 rename with a live target is a move carrying the old path", async () =>
        {
            var sandbox = Sandbox(root);
            await using var repo = new SqliteIndexRepository(Path.Combine(sandbox, "q3.sqlite"));
            await repo.InitializeAsync();
            await using var queue = new IncrementalIndexQueue(repo, quietPeriod: TimeSpan.FromMilliseconds(50), maxWait: TimeSpan.FromSeconds(2));
            await queue.InitializeAsync();
            var oldPath = Path.Combine(sandbox, "before.txt");
            var newPath = Path.Combine(sandbox, "after.txt");
            await File.WriteAllTextAsync(newPath, "moved");
            await queue.EnqueueEventAsync(newPath, sandbox, false, WatcherChangeTypes.Renamed, oldPath);
            var task = await repo.GetTaskAsync(newPath);
            Require(task is not null, "Rename must queue the destination.");
            Require(task!.Kind == "Move", "Rename with a live target must be a Move.");
            Require(string.Equals(task.Tombstone, FilePolicy.Normalize(oldPath), StringComparison.OrdinalIgnoreCase), "Move must carry the tombstone.");
        });

        await test("S1-Q4 rename without a target becomes delete plus add", async () =>
        {
            var sandbox = Sandbox(root);
            await using var repo = new SqliteIndexRepository(Path.Combine(sandbox, "q4.sqlite"));
            await repo.InitializeAsync();
            await using var queue = new IncrementalIndexQueue(repo, quietPeriod: TimeSpan.FromMilliseconds(50), maxWait: TimeSpan.FromSeconds(2));
            await queue.InitializeAsync();
            var oldPath = Path.Combine(sandbox, "gone.txt");
            var newPath = Path.Combine(sandbox, "never.txt");
            await queue.EnqueueEventAsync(newPath, sandbox, false, WatcherChangeTypes.Renamed, oldPath);
            var deletion = await repo.GetTaskAsync(oldPath);
            var addition = await repo.GetTaskAsync(newPath);
            Require(deletion is not null && deletion.Kind == "Delete", "Missing rename target must delete the old path.");
            Require(addition is not null && addition.Kind == "Add", "Missing rename target must add the new path.");
        });

        await test("S1-Q5 watcher overflow is counted and reconciled by diff, not by re-extract", async () =>
        {
            var sandbox = Sandbox(root);
            await using var repo = new SqliteIndexRepository(Path.Combine(sandbox, "q5.sqlite"));
            await repo.InitializeAsync();
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            var extracted = 0;
            await using var queue = new IncrementalIndexQueue(repo,
                indexPaths: (roots, paths, code, token) => { Interlocked.Add(ref extracted, paths.Count); return Task.CompletedTask; },
                quietPeriod: TimeSpan.FromMilliseconds(50), maxWait: TimeSpan.FromSeconds(2));
            await queue.InitializeAsync();

            // One file already indexed with matching size/mtime, one brand new file.
            var known = Path.Combine(sandbox, "known.txt");
            await File.WriteAllTextAsync(known, "known");
            var info = new FileInfo(known);
            await repo.UpsertFileAsync(new IndexedFile(0, FilePolicy.Normalize(known), "doc", info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0,
                "done", null, null, [new IndexSegment("chunk", "text", 1, null, "known", null)]));
            var fresh = Path.Combine(sandbox, "fresh.txt");
            await File.WriteAllTextAsync(fresh, "fresh");

            queue.OnWatcherError(sandbox, new InternalBufferOverflowException());
            Require(queue.WatcherOverflows == 1, "Overflow must be counted.");

            await queue.ReconcileRootAsync(sandbox, false);
            var freshTask = await repo.GetTaskAsync(fresh);
            var knownTask = await repo.GetTaskAsync(known);
            Require(freshTask is not null && freshTask.Reason == "reconcile_backfill", "New file must be queued by reconciliation.");
            Require(knownTask is null, "A file whose size and mtime already match the index must not be re-queued.");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
