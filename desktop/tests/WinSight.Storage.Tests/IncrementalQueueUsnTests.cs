using System.IO;
using Microsoft.Win32.SafeHandles;
using WinSight.Core;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Storage;

namespace WinSight.Storage.Tests;

internal static class IncrementalQueueUsnTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        string Db(string name) => Path.Combine(root, name + ".sqlite");

        await test("S3-USN-01: usn_checkpoints table persists and reloads across repo reopen", async () =>
        {
            string dbPath = Db("s3-chk-persist");
            var recTime = DateTimeOffset.UtcNow;
            await using (var repo = new SqliteIndexRepository(dbPath))
            {
                await repo.InitializeAsync();
                await repo.SaveUsnCheckpointAsync("VOL_C", "01DC6998695D898F", "184525807968", recTime);

                var chk = await repo.GetUsnCheckpointAsync("VOL_C");
                if (chk == null) throw new Exception("Checkpoint not found after save");
                if (chk.Value.JournalId != "01DC6998695D898F") throw new Exception($"Mismatched journalId: {chk.Value.JournalId}");
                if (chk.Value.NextUsn != "184525807968") throw new Exception($"Mismatched nextUsn: {chk.Value.NextUsn}");

                // Update checkpoint
                await repo.SaveUsnCheckpointAsync("VOL_C", "01DC6998695D898F", "184530000000", recTime.AddMinutes(5));
                var updated = await repo.GetUsnCheckpointAsync("VOL_C");
                if (updated?.NextUsn != "184530000000") throw new Exception("Checkpoint update did not take effect");
            }

            // Reopen repo and check persistence
            await using (var repo2 = new SqliteIndexRepository(dbPath))
            {
                await repo2.InitializeAsync();
                var chk = await repo2.GetUsnCheckpointAsync("VOL_C");
                if (chk == null) throw new Exception("Checkpoint lost after repo reopen");
                if (chk.Value.NextUsn != "184530000000") throw new Exception("NextUsn wrong after reopen");
            }
        });

        await test("S3-USN-02: Real C: volume read-only USN query and increment reading", async () =>
        {
            var reader = new UsnJournalReader();
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Skipping C: volume test on non-Windows");
                return;
            }

            bool isNtfs = reader.IsNtfsVolume("C:\\", out var serial);
            if (!isNtfs)
            {
                Console.WriteLine("C: is not NTFS or inaccessible");
                return;
            }

            var (qOk, qInfo, qErr) = reader.QueryJournal("C:");
            if (!qOk || qInfo == null)
            {
                Console.WriteLine($"Real C: query returned {qErr}; verifying graceful degradation reporting");
                if (string.IsNullOrEmpty(qErr)) throw new Exception("QueryJournal failed without an error reason");
                return;
            }

            if (qInfo.JournalId == 0) throw new Exception("JournalId cannot be zero on active journal");
            if (qInfo.NextUsn <= 0) throw new Exception("NextUsn should be positive");

            // Read increments from lowest valid USN (bounded batch of up to 10 records)
            var (rOk, records, nextUsn, rErr) = reader.ReadIncrements("C:", qInfo.JournalId, qInfo.LowestValidUsn, maxRecords: 10);
            if (!rOk)
            {
                Console.WriteLine($"Real C: ReadIncrements returned error {rErr}");
            }
            else
            {
                if (records.Count > 10) throw new Exception($"Expected <= 10 records, got {records.Count}");
                if (nextUsn < qInfo.LowestValidUsn) throw new Exception("Returned nextUsn cannot regress behind start");
            }
        });

        await test("S3-USN-03: journalId mismatch degrades to directory reconcile with usn_unavailable", async () =>
        {
            string dbPath = Db("s3-jid-mismatch");
            string testDir = Path.Combine(root, "jid-mismatch-fixture");
            Directory.CreateDirectory(testDir);
            string file1 = Path.Combine(testDir, "alpha.txt");
            await File.WriteAllTextAsync(file1, "content-alpha");

            await using var repo = new SqliteIndexRepository(dbPath);
            await repo.InitializeAsync();

            // Set checkpoint with journalId AAAA
            await repo.SaveUsnCheckpointAsync("VOL_TEST", "AAAA111122223333", "1000", DateTimeOffset.UtcNow);

            // Mock reader returning journalId BBBB
            var mockReader = new MockUsnReader(
                isNtfs: true,
                volumeSerial: "VOL_TEST",
                queryResult: (true, new UsnJournalInfo(0xBBBB111122223333UL, 100, 2000), null),
                readResult: (true, Array.Empty<UsnChangeRecord>(), 2000, null)
            );

            await using var queue = new IncrementalIndexQueue(repo, usnReader: mockReader);
            await queue.InitializeAsync();

            bool reconciled = await queue.ReconcileRootAsync(testDir, code: false, force: true);
            if (!reconciled) throw new Exception("ReconcileRootAsync should have succeeded via directory fallback");

            if (queue.UsnState != "usn_unavailable(journal-id-mismatch)")
            {
                throw new Exception($"Expected usn_unavailable(journal-id-mismatch), got: {queue.UsnState}");
            }

            // Verify alpha.txt was enqueued as an Add task by directory reconcile fallback
            var task = await repo.GetTaskAsync(file1);
            if (task == null || task.Kind != "Add")
            {
                throw new Exception($"Fallback reconcile did not enqueue Add task for alpha.txt (task={task?.Kind})");
            }
        });

        await test("S3-USN-04: Access denied or handle failure degrades to directory reconcile without exception", async () =>
        {
            string dbPath = Db("s3-access-denied");
            string testDir = Path.Combine(root, "access-denied-fixture");
            Directory.CreateDirectory(testDir);
            string file2 = Path.Combine(testDir, "beta.txt");
            await File.WriteAllTextAsync(file2, "content-beta");

            await using var repo = new SqliteIndexRepository(dbPath);
            await repo.InitializeAsync();
            await repo.SaveUsnCheckpointAsync("VOL_TEST2", "1111222233334444", "1000", DateTimeOffset.UtcNow);

            var mockReader = new MockUsnReader(
                isNtfs: true,
                volumeSerial: "VOL_TEST2",
                queryResult: (false, null, "access-denied"),
                readResult: (false, Array.Empty<UsnChangeRecord>(), 1000, "access-denied")
            );

            await using var queue = new IncrementalIndexQueue(repo, usnReader: mockReader);
            await queue.InitializeAsync();

            bool reconciled = await queue.ReconcileRootAsync(testDir, code: false, force: true);
            if (!reconciled) throw new Exception("Reconcile should have completed via directory fallback");

            if (queue.UsnState != "usn_unavailable(access-denied)")
            {
                throw new Exception($"Expected usn_unavailable(access-denied), got: {queue.UsnState}");
            }

            var task = await repo.GetTaskAsync(file2);
            if (task == null) throw new Exception("Fallback reconcile did not discover beta.txt");
        });

        await test("S3-USN-05: USN increment records correctly mapped to Add, Update, and Delete tasks", async () =>
        {
            string dbPath = Db("s3-task-mapping");
            string testDir = Path.Combine(root, "task-mapping-fixture");
            Directory.CreateDirectory(testDir);

            string fileAdd = Path.Combine(testDir, "added.txt");
            string fileUpdate = Path.Combine(testDir, "updated.txt");
            string fileDelete = Path.Combine(testDir, "deleted.txt");

            await using var repo = new SqliteIndexRepository(dbPath);
            await repo.InitializeAsync();
            await repo.SaveUsnCheckpointAsync("VOL_TEST3", "01DC000000000001", "1000", DateTimeOffset.UtcNow);

            var records = new List<UsnChangeRecord>
            {
                new(1010, 101, 10, UsnJournalReader.USN_REASON_RENAME_NEW_NAME, "added.txt", DateTimeOffset.UtcNow),
                new(1020, 102, 10, UsnJournalReader.USN_REASON_DATA_OVERWRITE | UsnJournalReader.USN_REASON_CLOSE, "updated.txt", DateTimeOffset.UtcNow),
                new(1030, 103, 10, UsnJournalReader.USN_REASON_FILE_DELETE | UsnJournalReader.USN_REASON_CLOSE, "deleted.txt", DateTimeOffset.UtcNow)
            };

            var pathsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["added.txt"] = fileAdd,
                ["updated.txt"] = fileUpdate,
                ["deleted.txt"] = fileDelete
            };

            var mockReader = new MockUsnReader(
                isNtfs: true,
                volumeSerial: "VOL_TEST3",
                queryResult: (true, new UsnJournalInfo(0x01DC000000000001UL, 500, 2000), null),
                readResult: (true, records, 1040, null),
                pathResolver: (vol, frn, pfrn, fn) => pathsMap.GetValueOrDefault(fn)
            );

            await using var queue = new IncrementalIndexQueue(repo, usnReader: mockReader);
            await queue.InitializeAsync();

            bool caughtUp = await queue.ReconcileRootAsync(testDir, code: false, force: true);
            if (!caughtUp) throw new Exception("USN catch up should have succeeded");

            if (queue.UsnState != "usn_caught_up(3 records)")
            {
                throw new Exception($"Expected usn_caught_up(3 records), got {queue.UsnState}");
            }

            var taskAdd = await repo.GetTaskAsync(fileAdd);
            if (taskAdd == null || taskAdd.Kind != "Add")
            {
                throw new Exception($"Expected Add task for added.txt, got {taskAdd?.Kind}");
            }

            var taskUpdate = await repo.GetTaskAsync(fileUpdate);
            if (taskUpdate == null || taskUpdate.Kind != "Update")
            {
                throw new Exception($"Expected Update task for updated.txt, got {taskUpdate?.Kind}");
            }

            var taskDelete = await repo.GetTaskAsync(fileDelete);
            if (taskDelete == null || taskDelete.Kind != "Delete")
            {
                throw new Exception($"Expected Delete task for deleted.txt, got {taskDelete?.Kind}");
            }

            // Verify checkpoint was advanced to 1040
            var chk = await repo.GetUsnCheckpointAsync("VOL_TEST3");
            if (chk?.NextUsn != "1040")
            {
                throw new Exception($"Expected checkpoint advanced to 1040, got {chk?.NextUsn}");
            }
        });

        await test("S3-USN-06: Non-NTFS volume degrades to usn_unavailable(not-ntfs) and runs directory reconcile", async () =>
        {
            string dbPath = Db("s3-not-ntfs");
            string testDir = Path.Combine(root, "not-ntfs-fixture");
            Directory.CreateDirectory(testDir);
            string file = Path.Combine(testDir, "gamma.txt");
            await File.WriteAllTextAsync(file, "gamma-data");

            await using var repo = new SqliteIndexRepository(dbPath);
            await repo.InitializeAsync();

            var mockReader = new MockUsnReader(
                isNtfs: false,
                volumeSerial: "FAT32_VOL",
                queryResult: (false, null, "not-supported"),
                readResult: (false, Array.Empty<UsnChangeRecord>(), 0, "not-supported")
            );

            await using var queue = new IncrementalIndexQueue(repo, usnReader: mockReader);
            await queue.InitializeAsync();

            bool reconciled = await queue.ReconcileRootAsync(testDir, code: false, force: true);
            if (!reconciled) throw new Exception("Reconcile should have completed via directory fallback");

            if (queue.UsnState != "usn_unavailable(not-ntfs)")
            {
                throw new Exception($"Expected usn_unavailable(not-ntfs), got: {queue.UsnState}");
            }

            var task = await repo.GetTaskAsync(file);
            if (task == null) throw new Exception("Fallback directory reconcile missed gamma.txt");
        });

        await test("S3-USN-07: Journal delete in progress degrades to usn_unavailable(journal-delete-in-progress)", async () =>
        {
            string dbPath = Db("s3-del-prog");
            string testDir = Path.Combine(root, "del-prog-fixture");
            Directory.CreateDirectory(testDir);

            await using var repo = new SqliteIndexRepository(dbPath);
            await repo.InitializeAsync();
            await repo.SaveUsnCheckpointAsync("VOL_DEL", "1234", "1000", DateTimeOffset.UtcNow);

            var mockReader = new MockUsnReader(
                isNtfs: true,
                volumeSerial: "VOL_DEL",
                queryResult: (false, null, "journal-delete-in-progress"),
                readResult: (false, Array.Empty<UsnChangeRecord>(), 1000, "journal-delete-in-progress")
            );

            await using var queue = new IncrementalIndexQueue(repo, usnReader: mockReader);
            await queue.InitializeAsync();

            await queue.ReconcileRootAsync(testDir, code: false, force: true);
            if (queue.UsnState != "usn_unavailable(journal-delete-in-progress)")
            {
                throw new Exception($"Expected usn_unavailable(journal-delete-in-progress), got {queue.UsnState}");
            }
        });
    }

    private sealed class MockUsnReader : IUsnJournalReader
    {
        private readonly bool isNtfs;
        private readonly string volumeSerial;
        private readonly (bool Success, UsnJournalInfo? Info, string? ErrorReason) queryResult;
        private readonly (bool Success, IReadOnlyList<UsnChangeRecord> Records, long NextUsn, string? ErrorReason) readResult;
        private readonly Func<string, ulong, ulong, string, string?>? pathResolver;

        public MockUsnReader(
            bool isNtfs,
            string volumeSerial,
            (bool Success, UsnJournalInfo? Info, string? ErrorReason) queryResult,
            (bool Success, IReadOnlyList<UsnChangeRecord> Records, long NextUsn, string? ErrorReason) readResult,
            Func<string, ulong, ulong, string, string?>? pathResolver = null)
        {
            this.isNtfs = isNtfs;
            this.volumeSerial = volumeSerial;
            this.queryResult = queryResult;
            this.readResult = readResult;
            this.pathResolver = pathResolver;
        }

        public bool IsNtfsVolume(string volumePath, out string serial)
        {
            serial = volumeSerial;
            return isNtfs;
        }

        public (bool Success, UsnJournalInfo? Info, string? ErrorReason) QueryJournal(string volumePath) => queryResult;

        public (bool Success, IReadOnlyList<UsnChangeRecord> Records, long NextUsn, string? ErrorReason) ReadIncrements(
            string volumePath, ulong journalId, long startUsn, int maxRecords = 5000) => readResult;

        public string? ResolvePath(string volumePath, ulong fileReferenceNumber, ulong parentFileReferenceNumber, string fileName)
        {
            if (pathResolver != null) return pathResolver(volumePath, fileReferenceNumber, parentFileReferenceNumber, fileName);
            return Path.Combine(volumePath, fileName);
        }
    }
}
