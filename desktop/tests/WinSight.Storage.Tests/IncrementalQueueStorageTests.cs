using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WinSight.Core;
using WinSight.Infrastructure.Storage;

internal static class IncrementalQueueStorageTests
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static float[] Unit(int axis) { var vector = new float[768]; vector[axis] = 1; return vector; }
    private static IndexedFile SampleFile(string path, string text, float[]? vector = null) =>
        new(0, path, "doc", 10, 1, "done", null, null,
            [new("chunk", vector == null ? "none" : "text", 1, null, text, vector ?? Unit(0))]);

    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> Test)
    {
        string Db(string name) => Path.Combine(root, name + ".sqlite");

        await Test("S1-STORAGE-01: persistent queue survives repo reopen; Running and Stabilizing reset to Pending", async () =>
        {
            var path = Db("reopen-tasks");
            var now = DateTimeOffset.UtcNow;
            await using (var repo = new SqliteIndexRepository(path))
            {
                await repo.InitializeAsync();
                await repo.EnqueueIndexTaskAsync(new IndexTask("C:/tasks/p1.txt", "C:/tasks", false, "Add", null, "Pending", 0, now, now));
                await repo.EnqueueIndexTaskAsync(new IndexTask("C:/tasks/r1.txt", "C:/tasks", false, "Update", null, "Running", 1, now, now));
                await repo.EnqueueIndexTaskAsync(new IndexTask("C:/tasks/s1.txt", "C:/tasks", false, "Update", null, "Stabilizing", 2, now, now));
                await repo.EnqueueIndexTaskAsync(new IndexTask("C:/tasks/d1.txt", "C:/tasks", false, "Delete", null, "Deferred", 3, now.AddHours(1), now, "locked"));
                await repo.EnqueueIndexTaskAsync(new IndexTask("C:/tasks/ready1.txt", "C:/tasks", false, "Add", null, "Ready", 0, now, now));
            }

            // Reopen the exact same SQLite database
            await using (var repo2 = new SqliteIndexRepository(path))
            {
                await repo2.InitializeAsync();

                var tP = await repo2.GetTaskAsync("C:/tasks/p1.txt");
                var tR = await repo2.GetTaskAsync("C:/tasks/r1.txt");
                var tS = await repo2.GetTaskAsync("C:/tasks/s1.txt");
                var tD = await repo2.GetTaskAsync("C:/tasks/d1.txt");
                var tReady = await repo2.GetTaskAsync("C:/tasks/ready1.txt");

                Require(tP != null && tP.State == "Pending", "Pending task should remain Pending.");
                Require(tR != null && tR.State == "Pending", "Running task must be reset to Pending upon reopen.");
                Require(tS != null && tS.State == "Pending", "Stabilizing task must be reset to Pending upon reopen.");
                Require(tD != null && tD.State == "Deferred", "Deferred task should remain Deferred.");
                Require(tReady != null && tReady.State == "Ready", "Ready task should remain Ready.");

                var counts = await repo2.GetTaskCountsAsync();
                Require(counts.GetValueOrDefault("Pending") == 3, "Expected 3 Pending tasks after reopen reset.");
                Require(counts.GetValueOrDefault("Deferred") == 1, "Expected 1 Deferred task.");
                Require(counts.GetValueOrDefault("Ready") == 1, "Expected 1 Ready task.");
                Require(counts.GetValueOrDefault("Running") == 0, "No Running tasks should exist after reopen.");
                Require(counts.GetValueOrDefault("Stabilizing") == 0, "No Stabilizing tasks should exist after reopen.");
            }
        });

        await Test("S1-STORAGE-02: AckTaskWithUpsertAsync writes file and deletes task atomically with tombstone and is idempotent", async () =>
        {
            var path = Db("ack-upsert");
            var now = DateTimeOffset.UtcNow;
            await using var repo = new SqliteIndexRepository(path);
            await repo.InitializeAsync();
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");

            var taskFile = SampleFile("C:/work/active.txt", "active content", Unit(1));
            var tombstonePath = "C:/work/old.txt";
            var tombstoneFile = SampleFile(tombstonePath, "old content", Unit(2));

            // Setup: add tombstone file to repo, and enqueue tombstone task + active task
            await repo.UpsertFileAsync(tombstoneFile);
            await repo.EnqueueIndexTaskAsync(new IndexTask(tombstonePath, "C:/work", false, "Delete", null, "Pending", 0, now, now));
            await repo.EnqueueIndexTaskAsync(new IndexTask(taskFile.Path, "C:/work", false, "Move", tombstonePath, "Running", 0, now, now));

            Require(await repo.GetFileAsync(tombstonePath) != null, "Tombstone file should exist before ack.");
            Require(await repo.GetTaskAsync(tombstonePath) != null, "Tombstone task should exist before ack.");
            Require(await repo.GetTaskAsync(taskFile.Path) != null, "Active task should exist before ack.");

            // Atomic AckTaskWithUpsertAsync
            await repo.AckTaskWithUpsertAsync(taskFile, taskFile.Path, tombstonePath);

            // Assert: active file is inserted, active task is deleted, tombstone file is deleted, tombstone task is deleted
            var activeRecord = await repo.GetFileAsync(taskFile.Path);
            Require(activeRecord != null && activeRecord.Segments.Count == 2 && activeRecord.Segments[0].Kind == "name" && activeRecord.Segments[1].Kind == "chunk", "Active file must round-trip its repository-owned name segment plus the supplied chunk.");
            Require(await repo.GetTaskAsync(taskFile.Path) == null, "Active task must be deleted in same transaction.");
            Require(await repo.GetFileAsync(tombstonePath) == null, "Tombstone file must be deleted in same transaction.");
            Require(await repo.GetTaskAsync(tombstonePath) == null, "Tombstone task must be deleted in same transaction.");

            // Repeated ack: idempotent second call
            await repo.AckTaskWithUpsertAsync(taskFile, taskFile.Path, tombstonePath);
            var files = await repo.ReadFilesAsync();
            Require(files.Count == 1, "Idempotent ack must not create duplicate files.");
            Require(files[0].Segments.Count == 2 && files[0].Segments[1].Kind == "chunk", "Idempotent ack must not duplicate segments or vectors.");
            Require(await repo.GetTaskAsync(taskFile.Path) == null, "Task still deleted after second ack.");
        });

        await Test("S1-STORAGE-03: AckTaskWithDeleteAsync deletes file and task atomically and is idempotent", async () =>
        {
            var path = Db("ack-delete");
            var now = DateTimeOffset.UtcNow;
            await using var repo = new SqliteIndexRepository(path);
            await repo.InitializeAsync();
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");

            var delFile = SampleFile("C:/work/obsolete.txt", "to be deleted", Unit(3));
            await repo.UpsertFileAsync(delFile);
            await repo.EnqueueIndexTaskAsync(new IndexTask(delFile.Path, "C:/work", false, "Delete", null, "Running", 0, now, now));

            Require(await repo.GetFileAsync(delFile.Path) != null, "File must exist before delete ack.");
            Require(await repo.GetTaskAsync(delFile.Path) != null, "Task must exist before delete ack.");

            await repo.AckTaskWithDeleteAsync(delFile.Path, delFile.Path);

            Require(await repo.GetFileAsync(delFile.Path) == null, "File must be deleted after delete ack.");
            Require(await repo.GetTaskAsync(delFile.Path) == null, "Task must be deleted after delete ack.");

            // Idempotent second delete ack
            await repo.AckTaskWithDeleteAsync(delFile.Path, delFile.Path);
            Require(await repo.GetFileAsync(delFile.Path) == null, "File still deleted after second ack.");
            Require(await repo.GetTaskAsync(delFile.Path) == null, "Task still deleted after second ack.");
        });

        await Test("S1-STORAGE-04: TrimTasksAsync drops Backfill, then Deferred, then oldest remaining, avoiding negative/missed counts", async () =>
        {
            var path = Db("trim-tasks");
            var now = DateTimeOffset.UtcNow;
            await using var repo = new SqliteIndexRepository(path);
            await repo.InitializeAsync();

            // Insert 8 tasks with different priorities:
            // 2 Backfill tasks (seen at now-10m and now-9m)
            await repo.EnqueueIndexTaskAsync(new IndexTask("C:/trim/bf1.txt", "C:/trim", false, "Update", null, "Pending", 0, now, now.AddMinutes(-10), "reconcile_backfill"));
            await repo.EnqueueIndexTaskAsync(new IndexTask("C:/trim/bf2.txt", "C:/trim", false, "Add", null, "Pending", 0, now, now.AddMinutes(-9), "reconcile_backfill"));
            // 2 Deferred tasks (seen at now-8m and now-7m)
            await repo.EnqueueIndexTaskAsync(new IndexTask("C:/trim/def1.txt", "C:/trim", false, "Update", null, "Deferred", 6, now.AddHours(1), now.AddMinutes(-8), "locked"));
            await repo.EnqueueIndexTaskAsync(new IndexTask("C:/trim/def2.txt", "C:/trim", false, "Update", null, "Deferred", 6, now.AddHours(1), now.AddMinutes(-7), "locked"));
            // 4 normal tasks (seen at now-6m, now-5m, now-4m, now-3m)
            await repo.EnqueueIndexTaskAsync(new IndexTask("C:/trim/norm1.txt", "C:/trim", false, "Add", null, "Pending", 0, now, now.AddMinutes(-6)));
            await repo.EnqueueIndexTaskAsync(new IndexTask("C:/trim/norm2.txt", "C:/trim", false, "Update", null, "Pending", 0, now, now.AddMinutes(-5)));
            await repo.EnqueueIndexTaskAsync(new IndexTask("C:/trim/norm3.txt", "C:/trim", false, "Add", null, "Pending", 0, now, now.AddMinutes(-4)));
            await repo.EnqueueIndexTaskAsync(new IndexTask("C:/trim/norm4.txt", "C:/trim", false, "Update", null, "Pending", 0, now, now.AddMinutes(-3)));

            // Trim to max 7: should drop 1 task (the oldest Backfill task bf1)
            var trimmed1 = await repo.TrimTasksAsync(7);
            Require(trimmed1 == 1, $"Expected 1 trimmed, got {trimmed1}.");
            Require(await repo.GetTaskAsync("C:/trim/bf1.txt") == null, "Oldest backfill task bf1 should be trimmed first.");
            Require(await repo.GetTaskAsync("C:/trim/bf2.txt") != null, "bf2 should remain.");

            // Trim to max 5: should drop 2 more tasks (bf2, then oldest Deferred def1)
            var trimmed2 = await repo.TrimTasksAsync(5);
            Require(trimmed2 == 2, $"Expected 2 trimmed, got {trimmed2}.");
            Require(await repo.GetTaskAsync("C:/trim/bf2.txt") == null, "bf2 should be trimmed next.");
            Require(await repo.GetTaskAsync("C:/trim/def1.txt") == null, "def1 should be trimmed after backfill.");
            Require(await repo.GetTaskAsync("C:/trim/def2.txt") != null, "def2 should remain.");

            // Trim to max 3: should drop 2 more tasks (def2, then oldest remaining norm1)
            var trimmed3 = await repo.TrimTasksAsync(3);
            Require(trimmed3 == 2, $"Expected 2 trimmed, got {trimmed3}.");
            Require(await repo.GetTaskAsync("C:/trim/def2.txt") == null, "def2 should be trimmed.");
            Require(await repo.GetTaskAsync("C:/trim/norm1.txt") == null, "norm1 should be trimmed after deferred.");
            Require(await repo.GetTaskAsync("C:/trim/norm2.txt") != null, "norm2 should remain.");
            Require(await repo.GetTaskAsync("C:/trim/norm3.txt") != null, "norm3 should remain.");
            Require(await repo.GetTaskAsync("C:/trim/norm4.txt") != null, "norm4 should remain.");

            // Trim when total <= max: should drop 0
            var trimmed0 = await repo.TrimTasksAsync(10);
            Require(trimmed0 == 0, $"Expected 0 trimmed when total <= max, got {trimmed0}.");
        });
    }
}
