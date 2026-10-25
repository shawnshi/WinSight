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
        await test("S1-Q6 settling waits for quiet, then requires a stable second sample", async () =>
        {
            var sandbox = Sandbox(root);
            await using var repo = new SqliteIndexRepository(Path.Combine(sandbox, "q6.sqlite"));
            await repo.InitializeAsync();
            await using var queue = new IncrementalIndexQueue(repo, quietPeriod: TimeSpan.FromMilliseconds(400), maxWait: TimeSpan.FromSeconds(5));
            await queue.InitializeAsync();
            var path = Path.Combine(sandbox, "settling.txt");
            await File.WriteAllTextAsync(path, "settled");
            await queue.EnqueueEventAsync(path, sandbox, false, WatcherChangeTypes.Created);

            var task = await repo.GetTaskAsync(path);
            Require(task is not null && task.State == "Stabilizing", "A fresh event must start in Stabilizing.");
            Require(!await queue.CheckStabilizationAsync(task!), "A task inside its quiet window must not be released early.");

            await Task.Delay(450);
            task = await repo.GetTaskAsync(path);
            Require(await queue.CheckStabilizationAsync(task!), "A quiet, stable file must be released.");
            var ready = await repo.GetTaskAsync(path);
            Require(ready!.State == "Ready" && ready.Reason is null, "Released task must be Ready without a caveat.");
        });

        await test("S1-Q7 continuous writes are still released at the maximum wait", async () =>
        {
            var sandbox = Sandbox(root);
            await using var repo = new SqliteIndexRepository(Path.Combine(sandbox, "q7.sqlite"));
            await repo.InitializeAsync();
            await using var queue = new IncrementalIndexQueue(repo, quietPeriod: TimeSpan.FromMilliseconds(200), maxWait: TimeSpan.FromMilliseconds(700));
            await queue.InitializeAsync();
            var path = Path.Combine(sandbox, "growing.log");
            await File.WriteAllTextAsync(path, "0");
            await queue.EnqueueEventAsync(path, sandbox, false, WatcherChangeTypes.Created);

            // A real writer keeps touching the file during the whole sampling window, otherwise the
            // stability gate would legitimately release the task instead of running into the max wait.
            using var writing = new CancellationTokenSource();
            var writer = Task.Run(async () =>
            {
                while (!writing.IsCancellationRequested)
                {
                    try { await File.AppendAllTextAsync(path, new string('x', 64), writing.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (IOException) { }
                    await Task.Delay(40, CancellationToken.None);
                }
            });

            var deadline = DateTime.UtcNow.AddSeconds(4);
            var released = false;
            try
            {
                while (DateTime.UtcNow < deadline && !released)
                {
                    var task = await repo.GetTaskAsync(path);
                    if (task is null) break;
                    released = await queue.CheckStabilizationAsync(task);
                }
            }
            finally
            {
                await writing.CancelAsync();
                await writer;
            }
            Require(released, "Continuous writes must not defer past the maximum wait.");
            var settled = await repo.GetTaskAsync(path);
            Require(settled!.State == "Ready" && settled.Reason == "still-changing", "Maximum-wait release must be marked still-changing, saw state=" + settled.State + " reason=" + (settled.Reason ?? "<null>") + " attempts=" + settled.Attempts);
        });

        await test("S1-Q8 an exclusively held file backs off and is released after the handle closes", async () =>
        {
            var sandbox = Sandbox(root);
            await using var repo = new SqliteIndexRepository(Path.Combine(sandbox, "q8.sqlite"));
            await repo.InitializeAsync();
            await using var queue = new IncrementalIndexQueue(repo, quietPeriod: TimeSpan.FromMilliseconds(50), maxWait: TimeSpan.FromSeconds(5));
            await queue.InitializeAsync();
            var path = Path.Combine(sandbox, "locked.txt");
            await File.WriteAllTextAsync(path, "locked");
            await queue.EnqueueEventAsync(path, sandbox, false, WatcherChangeTypes.Created);
            await Task.Delay(80);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var task = await repo.GetTaskAsync(path);
                Require(!await queue.CheckStabilizationAsync(task!), "A held file must not be released.");
                var waiting = await repo.GetTaskAsync(path);
                Require(waiting!.State == "Stabilizing" && waiting.Reason == "locked" && waiting.Attempts == 1, "A sharing violation must record one locked attempt and back off.");
                Require(waiting.DueUtc > DateTimeOffset.UtcNow, "The retry must be scheduled in the future.");
            }

            await Task.Delay(600);
            var retry = await repo.GetTaskAsync(path);
            Require(await queue.CheckStabilizationAsync(retry!), "After the handle closes the task must be released.");
            Require((await repo.GetTaskAsync(path))!.State == "Ready", "Released task must be Ready.");
        });

        await test("S1-Q9 exceeding the retry budget defers the task instead of spinning", async () =>
        {
            var sandbox = Sandbox(root);
            await using var repo = new SqliteIndexRepository(Path.Combine(sandbox, "q9.sqlite"));
            await repo.InitializeAsync();
            await using var queue = new IncrementalIndexQueue(repo, quietPeriod: TimeSpan.FromMilliseconds(50), maxWait: TimeSpan.FromSeconds(5));
            await queue.InitializeAsync();
            var path = Path.Combine(sandbox, "stuck.txt");
            await File.WriteAllTextAsync(path, "stuck");
            await repo.EnqueueIndexTaskAsync(new IndexTask(FilePolicy.Normalize(path), sandbox, false, "Update", null, "Stabilizing", 5, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(-1)));

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var task = await repo.GetTaskAsync(path);
                Require(!await queue.CheckStabilizationAsync(task!), "A sixth violation must not be retried again.");
            }
            var deferred = await repo.GetTaskAsync(path);
            Require(deferred!.State == "Deferred" && deferred.Reason == "locked" && deferred.Attempts == 6, "The budget must end in Deferred(locked).");
        });
    }


    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
