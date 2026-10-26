using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;

namespace WinSight.Infrastructure.Indexing;

public sealed class IncrementalIndexQueue : IAsyncDisposable
{
    private readonly IIndexRepository repository;
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<string>, bool, CancellationToken, Task>? indexPaths;
    private readonly Action<Exception>? onError;
    private readonly TimeSpan quietPeriod;
    private readonly TimeSpan maxWait;
    private readonly TimeSpan reconcileInterval;
    private readonly int maxQueueTasks;
    private readonly IUsnJournalReader usnReader;

    private long watcherOverflows;
    private int overflowDropped;
    private DateTimeOffset? lastReconcileUtc;

    private readonly ConcurrentDictionary<string, (string Path, DateTimeOffset Time, string? FileId)> pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> dirtyRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> recentMoves = new(StringComparer.OrdinalIgnoreCase);

    public long WatcherOverflows => Interlocked.Read(ref watcherOverflows);
    public int DroppedCount => Volatile.Read(ref overflowDropped);
    public DateTimeOffset? LastReconcileUtc => lastReconcileUtc;
    public string UsnState { get; private set; } = "not-attempted";
    // Optional budget signal: the coordinator sets this so queue-side work can honour battery state.
    public Func<bool>? IsBattery { get; set; }

    public IncrementalIndexQueue(
        IIndexRepository repository,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, bool, CancellationToken, Task>? indexPaths = null,
        Action<Exception>? onError = null,
        TimeSpan? quietPeriod = null,
        TimeSpan? maxWait = null,
        TimeSpan? reconcileInterval = null,
        int maxQueueTasks = 50000,
        IUsnJournalReader? usnReader = null)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.indexPaths = indexPaths;
        this.onError = onError;
        this.quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(1.5);
        this.maxWait = maxWait ?? TimeSpan.FromSeconds(10);
        this.reconcileInterval = reconcileInterval ?? TimeSpan.FromHours(6);
        this.maxQueueTasks = maxQueueTasks;
        this.usnReader = usnReader ?? new UsnJournalReader();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await repository.ResetHangingTasksAsync(cancellationToken).ConfigureAwait(false);
    }

    public static bool IsTemporaryFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var name = Path.GetFileName(path);
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public async Task EnqueueEventAsync(string path, string root, bool code, WatcherChangeTypes changeType, string? oldPath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsTemporaryFile(path) && (oldPath == null || IsTemporaryFile(oldPath))) return;

        var now = DateTimeOffset.UtcNow;
        var normPath = FilePolicy.Normalize(path);

        if (changeType == WatcherChangeTypes.Deleted)
        {
            var fileId = FileIdentityResolver.GetFileId(normPath);
            pendingDeletes[normPath] = (normPath, now, fileId);
            var task = new IndexTask(normPath, root, code, "Delete", null, "Stabilizing", 0, now + TimeSpan.FromSeconds(2), now);
            await EnqueueWithCoalescingAsync(task, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (changeType == WatcherChangeTypes.Renamed && oldPath != null)
        {
            var normOld = FilePolicy.Normalize(oldPath);
            var moveKey = normOld + "->" + normPath;
            if (recentMoves.TryGetValue(moveKey, out var moveTime) && (now - moveTime).TotalSeconds < 2)
            {
                return;
            }
            recentMoves[moveKey] = now;

            if (File.Exists(normPath))
            {
                var task = new IndexTask(normPath, root, code, "Move", normOld, "Stabilizing", 0, now + quietPeriod, now);
                await EnqueueWithCoalescingAsync(task, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var delTask = new IndexTask(normOld, root, code, "Delete", null, "Stabilizing", 0, now + quietPeriod, now);
                await EnqueueWithCoalescingAsync(delTask, cancellationToken).ConfigureAwait(false);
                var addTask = new IndexTask(normPath, root, code, "Add", null, "Stabilizing", 0, now + quietPeriod, now);
                await EnqueueWithCoalescingAsync(addTask, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        string kind = changeType == WatcherChangeTypes.Created ? "Add" : "Update";
        string? pairedTombstone = null;
        if (changeType == WatcherChangeTypes.Created)
        {
            var targetId = FileIdentityResolver.GetFileId(normPath);
            if (targetId != null)
            {
                foreach (var pair in pendingDeletes)
                {
                    if ((now - pair.Value.Time).TotalSeconds <= 2.0 && string.Equals(pair.Value.FileId, targetId, StringComparison.Ordinal))
                    {
                        pairedTombstone = pair.Key;
                        kind = "Move";
                        pendingDeletes.TryRemove(pair.Key, out _);
                        break;
                    }
                }
            }
        }

        var enqueueTask = new IndexTask(normPath, root, code, kind, pairedTombstone, "Stabilizing", 0, now + quietPeriod, now);
        await EnqueueWithCoalescingAsync(enqueueTask, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnqueueWithCoalescingAsync(IndexTask task, CancellationToken cancellationToken)
    {
        var existing = await repository.GetTaskAsync(task.Path, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        IndexTask finalTask;

        if (existing == null)
        {
            finalTask = task;
        }
        else
        {
            var seenUtc = existing.SeenUtc;
            var kind = task.Kind;
            var tombstone = task.Tombstone ?? existing.Tombstone;

            if (existing.Kind == "Add" && task.Kind == "Update") kind = "Add";
            else if (existing.Kind == "Move" && task.Kind == "Update") kind = "Move";
            else if (existing.Kind == "Add" && task.Kind == "Delete") kind = "Delete";

            var dueUtc = now + quietPeriod;
            var maxDue = seenUtc + maxWait;
            if (dueUtc > maxDue) dueUtc = maxDue;

            finalTask = new IndexTask(task.Path, task.Root, task.Code, kind, tombstone, "Stabilizing", 0, dueUtc, seenUtc, null);
        }

        await repository.EnqueueIndexTaskAsync(finalTask, cancellationToken).ConfigureAwait(false);

        var dropped = await repository.TrimTasksAsync(maxQueueTasks, cancellationToken).ConfigureAwait(false);
        if (dropped > 0)
        {
            Interlocked.Add(ref overflowDropped, dropped);
        }
    }

    public void OnWatcherError(string root, Exception error)
    {
        Interlocked.Increment(ref watcherOverflows);
        dirtyRoots[root] = true;
        onError?.Invoke(error);
    }

    public async Task<bool> CheckStabilizationAsync(IndexTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;

        if (now - task.SeenUtc >= maxWait)
        {
            await repository.UpdateTaskStateAsync(task.Path, "Ready", task.Attempts, now, "still-changing", cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (now < task.DueUtc) return false;

        if (task.Kind == "Delete")
        {
            await repository.UpdateTaskStateAsync(task.Path, "Ready", task.Attempts, now, null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!File.Exists(task.Path))
        {
            await repository.UpdateTaskStateAsync(task.Path, "Ready", task.Attempts, now, null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        long len1;
        DateTime time1;
        try
        {
            var fi1 = new FileInfo(task.Path);
            len1 = fi1.Length;
            time1 = fi1.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await HandleLockOrErrorAsync(task, ex, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(300, cancellationToken).ConfigureAwait(false);

        long len2;
        DateTime time2;
        try
        {
            var fi2 = new FileInfo(task.Path);
            len2 = fi2.Length;
            time2 = fi2.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await HandleLockOrErrorAsync(task, ex, cancellationToken).ConfigureAwait(false);
        }

        if (len1 != len2 || time1 != time2)
        {
            var newDue = DateTimeOffset.UtcNow + quietPeriod;
            if (newDue > task.SeenUtc + maxWait) newDue = task.SeenUtc + maxWait;
            await repository.UpdateTaskStateAsync(task.Path, "Stabilizing", task.Attempts, newDue, "sampling-mismatch", cancellationToken).ConfigureAwait(false);
            return false;
        }

        try
        {
            using (var stream = new FileStream(task.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
            }
            await repository.UpdateTaskStateAsync(task.Path, "Ready", 0, DateTimeOffset.UtcNow, null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IOException ex) when (IsSharingOrLockViolation(ex))
        {
            var attempts = task.Attempts + 1;
            if (attempts > 5)
            {
                await repository.UpdateTaskStateAsync(task.Path, "Deferred", attempts, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10), "locked", cancellationToken).ConfigureAwait(false);
                return false;
            }
            else
            {
                double backoffSec = 0.5 * Math.Pow(2, attempts - 1);
                var due = DateTimeOffset.UtcNow.AddSeconds(backoffSec);
                await repository.UpdateTaskStateAsync(task.Path, "Stabilizing", attempts, due, "locked", cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        catch (Exception ex)
        {
            var attempts = task.Attempts + 1;
            if (attempts >= 3)
            {
                await repository.UpdateTaskStateAsync(task.Path, "Deferred", attempts, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5), ex.Message, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await repository.UpdateTaskStateAsync(task.Path, "Failed", attempts, DateTimeOffset.UtcNow.AddSeconds(5), ex.Message, cancellationToken).ConfigureAwait(false);
            }
            return false;
        }
    }

    private async Task<bool> HandleLockOrErrorAsync(IndexTask task, Exception ex, CancellationToken cancellationToken)
    {
        if (ex is IOException ioEx && IsSharingOrLockViolation(ioEx))
        {
            var attempts = task.Attempts + 1;
            if (attempts > 5)
            {
                await repository.UpdateTaskStateAsync(task.Path, "Deferred", attempts, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10), "locked", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                double backoffSec = 0.5 * Math.Pow(2, attempts - 1);
                var due = DateTimeOffset.UtcNow.AddSeconds(backoffSec);
                await repository.UpdateTaskStateAsync(task.Path, "Stabilizing", attempts, due, "locked", cancellationToken).ConfigureAwait(false);
            }
            return false;
        }

        var failedAttempts = task.Attempts + 1;
        if (failedAttempts >= 3)
        {
            await repository.UpdateTaskStateAsync(task.Path, "Deferred", failedAttempts, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5), ex.Message, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await repository.UpdateTaskStateAsync(task.Path, "Failed", failedAttempts, DateTimeOffset.UtcNow.AddSeconds(5), ex.Message, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    public static bool IsSharingOrLockViolation(IOException ex)
    {
        int hr = ex.HResult & 0xFFFF;
        return hr is 32 or 33;
    }

    // Reconciliation enumerates the whole root, so it is gated: an explicit caller (startup, overflow,
    // resume) forces it, while ordinary file events must not pay for a full diff on every save.
    public async Task<bool> ReconcileRootAsync(string root, bool code, bool force = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(root)) return false;
        var dirty = dirtyRoots.ContainsKey(root);
        var due = lastReconcileUtc is null || DateTimeOffset.UtcNow - lastReconcileUtc.Value >= reconcileInterval;
        if (!force && !dirty && !due) return false;

        // Try USN catch-up first if on NTFS and checkpoint is valid
        if (await TryUsnCatchUpAsync(root, code, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var existingFiles = await repository.ReadFilesAsync(cancellationToken).ConfigureAwait(false);
        var normRoot = FilePolicy.Normalize(root);
        var indexMap = existingFiles
            .Where(f => FilePolicy.Under(f.Path, normRoot))
            .ToDictionary(f => FilePolicy.Normalize(f.Path), StringComparer.OrdinalIgnoreCase);

        var diskPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<IndexTask>();
        var rootDir = new DirectoryInfo(normRoot);
        async Task FlushAsync(bool force)
        {
            if (pending.Count == 0 || (!force && pending.Count < 500)) return;
            await repository.EnqueueIndexTasksAsync(pending.ToArray(), cancellationToken).ConfigureAwait(false);
            pending.Clear();
        }

        foreach (var file in EnumerateFilesSafe(rootDir, code))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var norm = FilePolicy.Normalize(file.FullName);
            diskPaths.Add(norm);

            double mtime = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0;
            if (indexMap.TryGetValue(norm, out var indexed))
            {
                indexMap.Remove(norm);
                if (indexed.Size == file.Length && Math.Abs(indexed.ModifiedTime - mtime) < 0.001)
                {
                    continue;
                }
                pending.Add(new IndexTask(norm, normRoot, code, "Update", null, "Pending", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "reconcile_backfill"));
                await FlushAsync(false).ConfigureAwait(false);
            }
            else
            {
                pending.Add(new IndexTask(norm, normRoot, code, "Add", null, "Pending", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "reconcile_backfill"));
                await FlushAsync(false).ConfigureAwait(false);
            }
        }

        foreach (var missing in indexMap.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delTask = new IndexTask(missing.Path, normRoot, code, "Delete", null, "Pending", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "reconcile_backfill");
            await EnqueueWithCoalescingAsync(delTask, cancellationToken).ConfigureAwait(false);
        }

        await TryRecordUsnCheckpointAsync(normRoot, cancellationToken).ConfigureAwait(false);

        await FlushAsync(true).ConfigureAwait(false);
        dirtyRoots.TryRemove(root, out _);
        lastReconcileUtc = DateTimeOffset.UtcNow;
        return true;
    }

    private async Task<bool> TryUsnCatchUpAsync(string root, bool code, CancellationToken cancellationToken)
    {
        var normRoot = FilePolicy.Normalize(root);
        if (!usnReader.IsNtfsVolume(normRoot, out var volumeSerial) || string.IsNullOrEmpty(volumeSerial))
        {
            UsnState = "usn_unavailable(not-ntfs)";
            return false;
        }

        if (repository is not IUsnCheckpointRepository checkpointRepo)
        {
            UsnState = "usn_unavailable(no-checkpoint-store)";
            return false;
        }

        (string JournalId, string NextUsn, DateTimeOffset ReconcileUtc)? checkpoint;
        try
        {
            checkpoint = await checkpointRepo.GetUsnCheckpointAsync(volumeSerial, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            UsnState = $"usn_unavailable(checkpoint-read-failed: {ex.Message})";
            return false;
        }

        if (checkpoint == null)
        {
            UsnState = "usn_unavailable(missing-checkpoint)";
            return false;
        }

        var (queryOk, queryInfo, queryErr) = usnReader.QueryJournal(normRoot);
        if (!queryOk || queryInfo == null)
        {
            UsnState = $"usn_unavailable({queryErr ?? "query-failed"})";
            return false;
        }

        string currentJournalIdHex = queryInfo.JournalId.ToString("X16");
        if (!string.Equals(checkpoint.Value.JournalId, currentJournalIdHex, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(checkpoint.Value.JournalId, queryInfo.JournalId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            UsnState = "usn_unavailable(journal-id-mismatch)";
            return false;
        }

        if (!long.TryParse(checkpoint.Value.NextUsn, out var startUsn))
        {
            UsnState = "usn_unavailable(corrupted-checkpoint-usn)";
            return false;
        }

        if (startUsn < queryInfo.LowestValidUsn)
        {
            UsnState = "usn_unavailable(journal-truncated)";
            return false;
        }

        var (readOk, records, nextUsn, readErr) = usnReader.ReadIncrements(normRoot, queryInfo.JournalId, startUsn);
        if (!readOk)
        {
            UsnState = $"usn_unavailable({readErr ?? "read-failed"})";
            return false;
        }

        int matchedCount = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolvedPath = usnReader.ResolvePath(normRoot, record.FileReferenceNumber, record.ParentFileReferenceNumber, record.FileName);
            if (resolvedPath == null) continue;

            var normPath = FilePolicy.Normalize(resolvedPath);
            if (!FilePolicy.Under(normPath, normRoot)) continue;
            if (IsTemporaryFile(normPath)) continue;
            if (FilePolicy.Kind(normPath, code) == null) continue;

            string kind;
            if ((record.Reason & UsnJournalReader.USN_REASON_FILE_DELETE) != 0)
            {
                kind = "Delete";
            }
            else if ((record.Reason & UsnJournalReader.USN_REASON_RENAME_NEW_NAME) != 0)
            {
                kind = "Add";
            }
            else
            {
                kind = "Update";
            }

            var task = new IndexTask(normPath, normRoot, code, kind, null, "Pending", 0, now, now, "usn_catch_up");
            await EnqueueWithCoalescingAsync(task, cancellationToken).ConfigureAwait(false);
            matchedCount++;
        }

        try
        {
            await checkpointRepo.SaveUsnCheckpointAsync(volumeSerial, currentJournalIdHex, nextUsn.ToString(), now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }

        dirtyRoots.TryRemove(root, out _);
        lastReconcileUtc = now;
        UsnState = $"usn_caught_up({matchedCount} records)";
        return true;
    }

    private async Task TryRecordUsnCheckpointAsync(string normRoot, CancellationToken cancellationToken)
    {
        try
        {
            if (repository is IUsnCheckpointRepository checkpointRepo &&
                usnReader.IsNtfsVolume(normRoot, out var volumeSerial) &&
                !string.IsNullOrEmpty(volumeSerial))
            {
                var (queryOk, queryInfo, _) = usnReader.QueryJournal(normRoot);
                if (queryOk && queryInfo != null)
                {
                    await checkpointRepo.SaveUsnCheckpointAsync(
                        volumeSerial,
                        queryInfo.JournalId.ToString("X16"),
                        queryInfo.NextUsn.ToString(),
                        DateTimeOffset.UtcNow,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
    }

    private static IEnumerable<FileInfo> EnumerateFilesSafe(DirectoryInfo dir, bool code)
    {
        IEnumerable<FileInfo> files;
        try { files = dir.EnumerateFiles(); }
        catch { yield break; }

        foreach (var fi in files)
        {
            if (IsTemporaryFile(fi.FullName)) continue;
            if (FilePolicy.Kind(fi.FullName, code) != null)
                yield return fi;
        }

        IEnumerable<DirectoryInfo> subDirs;
        try { subDirs = dir.EnumerateDirectories(); }
        catch { yield break; }

        foreach (var sub in subDirs)
        {
            if (FilePolicy.SkipFolder(sub.FullName, code)) continue;
            foreach (var child in EnumerateFilesSafe(sub, code))
                yield return child;
        }
    }

    public async Task<int> ProcessReadyTasksAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dueTasks = await repository.ReadDueTasksAsync(limit, cancellationToken).ConfigureAwait(false);
        if (IsBattery?.Invoke() == true && dueTasks.Count > 1)
        {
            dueTasks = PrioritizeRecentSmallFiles(dueTasks);
        }
        var processed = 0;

        foreach (var task in dueTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool ready = task.State == "Ready";
            if (!ready)
            {
                ready = await CheckStabilizationAsync(task, cancellationToken).ConfigureAwait(false);
            }

            if (!ready) continue;

            await repository.UpdateTaskStateAsync(task.Path, "Running", task.Attempts, DateTimeOffset.UtcNow, task.Reason, cancellationToken).ConfigureAwait(false);

            try
            {
                if (task.Kind == "Delete")
                {
                    await repository.AckTaskWithDeleteAsync(task.Path, task.Path, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (indexPaths != null)
                    {
                        await indexPaths([task.Root], [task.Path], task.Code, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var file = await repository.GetFileAsync(task.Path, cancellationToken).ConfigureAwait(false);
                        if (file != null)
                        {
                            await repository.AckTaskWithUpsertAsync(file, task.Path, task.Tombstone, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await repository.AckTaskWithDeleteAsync(task.Path, task.Path, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                processed++;
            }
            catch (IndexingPausedException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var attempts = task.Attempts + 1;
                if (attempts >= 3)
                {
                    await repository.UpdateTaskStateAsync(task.Path, "Deferred", attempts, DateTimeOffset.UtcNow.AddMinutes(5), ex.Message, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await repository.UpdateTaskStateAsync(task.Path, "Failed", attempts, DateTimeOffset.UtcNow.AddSeconds(5), ex.Message, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return processed;
    }

    public Task<IReadOnlyDictionary<string, int>> GetTaskCountsAsync(CancellationToken cancellationToken = default) =>
        repository.GetTaskCountsAsync(cancellationToken);

    public async Task<IndexingQueueStatus> GetIndexingQueueStatusAsync(
        IReadOnlyList<IndexBudgetState>? budgets = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var counts = await repository.GetTaskCountsAsync(cancellationToken).ConfigureAwait(false);
        counts.TryGetValue("Pending", out var pending);
        counts.TryGetValue("Stabilizing", out var stabilizing);
        counts.TryGetValue("Ready", out var ready);
        counts.TryGetValue("Running", out var running);
        counts.TryGetValue("Failed", out var failed);
        counts.TryGetValue("Deferred", out var deferred);

        return new IndexingQueueStatus(
            Pending: pending,
            Stabilizing: stabilizing,
            Ready: ready,
            Running: running,
            Failed: failed,
            Deferred: deferred,
            Dropped: DroppedCount,
            WatcherOverflows: WatcherOverflows,
            LastReconcileUtc: LastReconcileUtc,
            UsnState: UsnState,
            Budgets: budgets ?? Array.Empty<IndexBudgetState>());
    }

    public static IReadOnlyList<IndexTask> PrioritizeRecentSmallFiles(IEnumerable<IndexTask> tasks)
    {
        return tasks
            .OrderByDescending(t => GetLastWriteTimeSafe(t.Path))
            .ThenBy(t => GetFileSizeSafe(t.Path))
            .ToList();
    }

    private static DateTime GetLastWriteTimeSafe(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private static long GetFileSizeSafe(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : long.MaxValue; }
        catch { return long.MaxValue; }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Kept local to the indexing layer: this type must not depend on the runtime coordinator, which the
    // indexing test project does not compile.
    private static bool IsUnder(string path, string root)
    {
        var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return target.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

}

internal static class FileIdentityResolver
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FILE_ID_INFO information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    public static string? GetFileId(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var handle = file.SafeFileHandle;
            if (GetFileInformationByHandleEx(handle, 18 /* FileIdInfo */, out var idInfo, (uint)Marshal.SizeOf<FILE_ID_INFO>()))
            {
                return $"{idInfo.VolumeSerialNumber:X16}:{idInfo.FileIdHigh:X16}{idInfo.FileIdLow:X16}";
            }
            if (GetFileInformationByHandle(handle, out var hInfo))
            {
                return $"{hInfo.VolumeSerial:X8}:{hInfo.FileIndexHigh:X8}:{hInfo.FileIndexLow:X8}";
            }
        }
        catch { }
        return null;
    }
}
