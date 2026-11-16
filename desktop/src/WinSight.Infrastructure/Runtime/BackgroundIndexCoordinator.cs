using System.Collections.Concurrent;
using System.Threading.Channels;
using WinSight.Core;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Search;

namespace WinSight.Infrastructure.Runtime;

public sealed class BackgroundIndexCoordinator : IAsyncDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly Task timer;
    private readonly AppSettings settings;
    private readonly Func<IndexRequest, CancellationToken, Task> index;
    private readonly Action<Exception> onError;
    private readonly TimeSpan debounce;
    private readonly IReadOnlyList<string> ignoredRoots;
    private readonly Func<bool> indexingAllowed;
    private readonly Func<bool> isOnAcPower;
    private readonly TimeSpan policyPoll;
    private readonly object policyGate = new();
    private readonly object delayedTimerGate = new();
    private readonly SemaphoreSlim queueWorkGate = new(1, 1);
    private readonly ConcurrentDictionary<long, Task> inflightOps = new();
    private long nextInflightOpId;
    private readonly IncrementalIndexQueue? queue;
    private readonly IncrementalIndexQueue? codeQueue;
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<string>, bool, CancellationToken, Task>? indexPaths;
    private readonly HostSignals hostSignals;
    private readonly ResourceBudgetController budgetController;
    private CancellationTokenSource? activePolicyStop;
    private Task? activePolicyCancellation;
    private Timer? delayedSignalTimer;
    private long delayedSignalDueTick = long.MaxValue;
    private int disposed;
    private bool queueReconciled;
    private bool codeQueueReconciled;
    private readonly Func<CancellationToken, Task>? releaseCaches;
    private async Task ReleaseCachesAsync()
    {
        try
        {
            if (releaseCaches is not null) await releaseCaches(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { onError(error); }
    }

    public Task Completion => worker;
    public Exception? Failure => worker.Exception?.GetBaseException();
    public IncrementalIndexQueue? Queue => queue;
    public IncrementalIndexQueue? CodeQueue => codeQueue;
    public long WatcherOverflows => (queue?.WatcherOverflows ?? 0) + (codeQueue?.WatcherOverflows ?? 0);
    public ResourceBudgetController BudgetController => budgetController;

    public BackgroundIndexCoordinator(AppSettings settings, Func<IndexRequest, CancellationToken, Task> index,
        Action<Exception> onError, TimeSpan? debounce = null, TimeSpan? reconcile = null,
        IReadOnlyList<string>? ignoredRoots = null, Func<bool>? indexingAllowed = null,
        Func<bool>? isOnAcPower = null, TimeSpan? policyPoll = null,
        IIndexRepository? repository = null,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, bool, CancellationToken, Task>? indexPaths = null,
        IncrementalIndexQueue? queue = null,
        HostSignals? hostSignals = null,
        ResourceBudgetController? budgetController = null,
        Func<CancellationToken, Task>? releaseCaches = null,
        IIndexRepository? codeRepository = null)
    {
        this.settings = settings;
        this.index = index;
        this.onError = onError;
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        this.ignoredRoots = ignoredRoots?.ToArray() ?? [];
        this.indexingAllowed = indexingAllowed ?? (() => true);
        this.isOnAcPower = isOnAcPower ?? PowerSource.IsOnAcPower;
        this.policyPoll = policyPoll ?? TimeSpan.FromSeconds(30);
        this.releaseCaches = releaseCaches;
        this.indexPaths = indexPaths;
        this.queue = queue ?? (repository != null ? new IncrementalIndexQueue(repository, indexPaths, onError, reconcileInterval: reconcile) : null);
        this.codeQueue = codeRepository != null ? new IncrementalIndexQueue(codeRepository, indexPaths, onError, reconcileInterval: reconcile) : null;
        if (hostSignals != null)
        {
            this.hostSignals = hostSignals;
        }
        else if (isOnAcPower != null)
        {
            this.hostSignals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(isOnAcPower(), false, 100),
                onError: onError);
        }
        else
        {
            this.hostSignals = new HostSignals(onError: onError);
        }

        this.budgetController = budgetController ?? new ResourceBudgetController(this.hostSignals,
            onMemoryPressure: releaseCaches is null ? null : () => _ = ReleaseCachesAsync());
        if (this.queue != null)
        {
            this.queue.IsBattery = () => !this.hostSignals.GetPowerInfo().IsAcPower;
        }
        if (this.codeQueue != null)
        {
            this.codeQueue.IsBattery = () => !this.hostSignals.GetPowerInfo().IsAcPower;
        }
        if (this.policyPoll <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(policyPoll));
        foreach (var root in settings.Folders.Concat(settings.CodeFolders).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Configured folder is unavailable: {root}");
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException($"Reparse-point root is not watched: {root}");
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 16 * 1024
                };
                watcher.Changed += Changed;
                watcher.Created += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += Changed;
                watcher.Error += (_, eventArgs) =>
                {
                    var ex = eventArgs.GetException();
                    onError(ex);
                    if (this.queue != null)
                    {
                        foreach (var f in settings.Folders)
                        {
                            if (DesktopBackend.IsUnder(root, f) || DesktopBackend.IsUnder(f, root))
                                this.queue.OnWatcherError(f, ex);
                        }
                    }
                    if (this.codeQueue != null)
                    {
                        foreach (var f in settings.CodeFolders)
                        {
                            if (DesktopBackend.IsUnder(root, f) || DesktopBackend.IsUnder(f, root))
                                this.codeQueue.OnWatcherError(f, ex);
                        }
                    }
                    Signal();
                };
                watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                onError(error);
            }
        }
        worker = RunAsync();
        timer = TickAsync(reconcile ?? TimeSpan.FromMinutes(15));
        Signal();
    }

    private (bool IsCode, string Root)? MatchScope(string fullPath)
    {
        (bool IsCode, string Root, int Length)? best = null;
        foreach (var f in settings.Folders)
        {
            if (DesktopBackend.IsUnder(fullPath, f) && (best is null || f.Length > best.Value.Length))
                best = (false, f, f.Length);
        }
        foreach (var f in settings.CodeFolders)
        {
            if (DesktopBackend.IsUnder(fullPath, f) && (best is null || f.Length > best.Value.Length || (f.Length == best.Value.Length && !best.Value.IsCode)))
                best = (true, f, f.Length);
        }
        return best is { } match ? (match.IsCode, match.Root) : null;
    }

    private void TrackInflight(Func<Task> start)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        long id = Interlocked.Increment(ref nextInflightOpId);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inflightOps[id] = done.Task;
        _ = RunTrackedAsync(id, done, start);
    }

    private async Task RunTrackedAsync(long id, TaskCompletionSource done, Func<Task> start)
    {
        try
        {
            await start().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            onError(error);
        }
        finally
        {
            inflightOps.TryRemove(id, out _);
            done.TrySetResult();
        }
    }

    private async Task EnqueueEventSafelyAsync(IncrementalIndexQueue targetQueue, string path, string root, bool isCode, WatcherChangeTypes changeType, string? oldPath)
    {
        try
        {
            await targetQueue.EnqueueEventAsync(path, root, isCode, changeType, oldPath, stop.Token).ConfigureAwait(false);
            Signal();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            onError(error);
        }
    }

    private void Changed(object sender, FileSystemEventArgs args)
    {
        var ignored = ignoredRoots.Any(root => DesktopBackend.IsUnder(args.FullPath, root));
        if (ignored)
        {
            if (args is RenamedEventArgs renIgnored && !ignoredRoots.Any(root => DesktopBackend.IsUnder(renIgnored.OldFullPath, root)))
            {
                if (MatchScope(renIgnored.OldFullPath) is { } oldScope)
                {
                    var oldQueue = oldScope.IsCode ? codeQueue : queue;
                    if (oldQueue != null)
                        TrackInflight(() => EnqueueEventSafelyAsync(oldQueue, renIgnored.OldFullPath, oldScope.Root, oldScope.IsCode, WatcherChangeTypes.Deleted, null));
                    else
                        Signal();
                }
                else
                {
                    Signal();
                }
            }
            return;
        }

        var newScope = MatchScope(args.FullPath);
        if (args is RenamedEventArgs ren)
        {
            var oldScope = MatchScope(ren.OldFullPath);
            if (oldScope is { } o && newScope is { } n && o.IsCode != n.IsCode)
            {
                var oldQueue = o.IsCode ? codeQueue : queue;
                if (oldQueue != null) TrackInflight(() => EnqueueEventSafelyAsync(oldQueue, ren.OldFullPath, o.Root, o.IsCode, WatcherChangeTypes.Deleted, null));
                var newQueue = n.IsCode ? codeQueue : queue;
                if (newQueue != null) TrackInflight(() => EnqueueEventSafelyAsync(newQueue, args.FullPath, n.Root, n.IsCode, WatcherChangeTypes.Created, null));
                return;
            }
        }

        bool isCode = newScope?.IsCode ?? false;
        var targetQueue = isCode ? codeQueue : queue;
        if (targetQueue != null)
        {
            var root = newScope?.Root ?? Path.GetDirectoryName(args.FullPath) ?? args.FullPath;
            TrackInflight(() => EnqueueEventSafelyAsync(targetQueue, args.FullPath, root, isCode, args.ChangeType, (args as RenamedEventArgs)?.OldFullPath));
        }
        else
        {
            Signal();
        }
    }
    private void Signal() => signals.Writer.TryWrite(true);

    private void ScheduleDelayedSignal(TimeSpan delay)
    {
        if (stop.IsCancellationRequested || Volatile.Read(ref disposed) != 0) return;
        long target = Environment.TickCount64 + Math.Max(10L, (long)delay.TotalMilliseconds);
        lock (delayedTimerGate)
        {
            if (stop.IsCancellationRequested || Volatile.Read(ref disposed) != 0) return;
            if (target >= delayedSignalDueTick) return;
            delayedSignalDueTick = target;
            delayedSignalTimer ??= new Timer(_ =>
            {
                lock (delayedTimerGate) { delayedSignalDueTick = long.MaxValue; }
                if (!stop.IsCancellationRequested && Volatile.Read(ref disposed) == 0) Signal();
            }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            delayedSignalTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void RequestReconciliation() => Signal();

    public void PreemptForInteractiveQuery() => budgetController.PreemptForInteractiveQuery();

    public async Task HandleResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var root in settings.Folders.Concat(settings.CodeFolders).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                onError(new DirectoryNotFoundException($"Configured folder is unavailable: {root}"));
            }
        }

        await queueWorkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var code in new[] { false, true })
            {
                var targetQueue = code ? codeQueue : queue;
                if (targetQueue == null) continue;
                var roots = code ? settings.CodeFolders : settings.Folders;
                var existingRoots = roots.Where(Directory.Exists).ToArray();
                if (existingRoots.Length > 0)
                {
                    await targetQueue.ReconcileRootsAsync(existingRoots, code, force: true, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            queueWorkGate.Release();
        }

        budgetController.ComputeBudgets();
        Signal();
    }

    public async Task<IndexingQueueStatus> GetIndexingQueueStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budgets = budgetController.ComputeBudgets();
        if (queue != null || codeQueue != null)
        {
            var docStatus = queue != null ? await queue.GetIndexingQueueStatusAsync(budgets, cancellationToken).ConfigureAwait(false) : null;
            var codeStatus = codeQueue != null ? await codeQueue.GetIndexingQueueStatusAsync(budgets, cancellationToken).ConfigureAwait(false) : null;
            if (docStatus != null && codeStatus == null) return docStatus;
            if (codeStatus != null && docStatus == null) return codeStatus;
            if (docStatus != null && codeStatus != null)
            {
                DateTimeOffset? latestReconcile = docStatus.LastReconcileUtc is null ? codeStatus.LastReconcileUtc
                    : codeStatus.LastReconcileUtc is null ? docStatus.LastReconcileUtc
                    : docStatus.LastReconcileUtc >= codeStatus.LastReconcileUtc ? docStatus.LastReconcileUtc : codeStatus.LastReconcileUtc;
                string usnState = settings.Folders.Count > 0 ? docStatus.UsnState : codeStatus.UsnState;
                return new IndexingQueueStatus(
                    Pending: docStatus.Pending + codeStatus.Pending,
                    Stabilizing: docStatus.Stabilizing + codeStatus.Stabilizing,
                    Ready: docStatus.Ready + codeStatus.Ready,
                    Running: docStatus.Running + codeStatus.Running,
                    Failed: docStatus.Failed + codeStatus.Failed,
                    Deferred: docStatus.Deferred + codeStatus.Deferred,
                    Dropped: docStatus.Dropped + codeStatus.Dropped,
                    WatcherOverflows: docStatus.WatcherOverflows + codeStatus.WatcherOverflows,
                    LastReconcileUtc: latestReconcile,
                    UsnState: usnState,
                    Budgets: budgets);
            }
        }

        return new IndexingQueueStatus(
            Pending: 0,
            Stabilizing: 0,
            Ready: 0,
            Running: 0,
            Failed: 0,
            Deferred: 0,
            Dropped: 0,
            WatcherOverflows: 0,
            LastReconcileUtc: null,
            UsnState: "starting",
            Budgets: budgets);
    }

    public static bool IsScheduledTime(IndexHours hours, int minute)
    {
        if (hours.Start is < 0 or >= 1440 || hours.End is < 0 or >= 1440 || minute is < 0 or >= 1440)
            throw new ArgumentOutOfRangeException(nameof(hours));
        return hours.Start == hours.End || (hours.Start < hours.End
            ? minute >= hours.Start && minute < hours.End
            : minute >= hours.Start || minute < hours.End);
    }

    private bool AllowedNow()
    {
        var now = DateTime.Now;
        if (settings.IndexHoursOn && !IsScheduledTime(settings.IndexHours, now.Hour * 60 + now.Minute)) return false;
        if (!settings.ProcessorOnly) return true;
        try { return isOnAcPower(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            onError(error);
            return false;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await signals.Reader.WaitToReadAsync(stop.Token))
            {
                while (signals.Reader.TryRead(out _)) { }
                await Task.Delay(debounce, stop.Token);
                while (signals.Reader.TryRead(out _)) { }
                if (!AllowedNow() || !indexingAllowed()) continue;

                foreach (var code in new[] { false, true })
                {
                    var roots = code ? settings.CodeFolders : settings.Folders;
                    if (roots.Count == 0) continue;
                    var activeQueue = code ? codeQueue : queue;
                    using var policyStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    lock (policyGate) { activePolicyStop = policyStop; activePolicyCancellation = null; }
                    try
                    {
                        if (!AllowedNow() || !indexingAllowed()) break;
                        if (activeQueue != null)
                        {
                            bool shouldContinue = false;
                            await queueWorkGate.WaitAsync(policyStop.Token).ConfigureAwait(false);
                            try
                            {
                                // Reconcile only on first pass, watcher overflow, or periodic interval.
                                var force = code ? !codeQueueReconciled : !queueReconciled;
                                if (AllowedNow() && indexingAllowed())
                                {
                                    await activeQueue.ReconcileRootsAsync(roots, code, force, policyStop.Token).ConfigureAwait(false);
                                }
                                if (AllowedNow() && indexingAllowed() && !policyStop.IsCancellationRequested)
                                {
                                    if (code) codeQueueReconciled = true;
                                    else queueReconciled = true;
                                }

                                if (indexPaths != null)
                                {
                                    var processed = await activeQueue.ProcessReadyTasksAsync(50, policyStop.Token, batchSize: 16).ConfigureAwait(false);
                                    var remaining = await activeQueue.GetTaskCountsAsync(policyStop.Token).ConfigureAwait(false);
                                    if (processed > 0 && remaining.GetValueOrDefault("Ready") > 0)
                                    {
                                        Signal();
                                    }
                                    else if (remaining.GetValueOrDefault("Pending") + remaining.GetValueOrDefault("Stabilizing") + remaining.GetValueOrDefault("Ready") > 0)
                                    {
                                        // Tasks inside their quiet window or waiting on stabilization must be re-checked after a short delay, never triggering a full corpus walk.
                                        ScheduleDelayedSignal(TimeSpan.FromMilliseconds(Math.Clamp(debounce.TotalMilliseconds, 100, 450)));
                                    }
                                    else if (await activeQueue.CountRetryableFailedTasksAsync(policyStop.Token).ConfigureAwait(false) > 0)
                                    {
                                        ScheduleDelayedSignal(TimeSpan.FromSeconds(5));
                                    }
                                    shouldContinue = true;
                                }
                            }
                            finally
                            {
                                queueWorkGate.Release();
                            }
                            if (shouldContinue) continue;
                        }

                        await index(new IndexRequest(roots.ToArray(), Code: code), policyStop.Token);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) when (policyStop.IsCancellationRequested) { break; }
                    catch (IndexingInterruptedException) { break; }
                    catch (IndexingPausedException) { break; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException
                        or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException
                        or System.Data.Common.DbException)
                    {
                        onError(error);
                    }
                    finally
                    {
                        Task? cancellation;
                        lock (policyGate)
                        {
                            activePolicyStop = null;
                            cancellation = activePolicyCancellation;
                            activePolicyCancellation = null;
                        }
                        if (cancellation != null) await cancellation;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task TickAsync(TimeSpan reconcile)
    {
        using var clock = new PeriodicTimer(policyPoll);
        var nextReconcile = DateTimeOffset.UtcNow + reconcile;
        var wasAllowed = AllowedNow();
        var lastTickUtc = DateTimeOffset.UtcNow;
        try
        {
            while (await clock.WaitForNextTickAsync(stop.Token))
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var gap = nowUtc - (lastTickUtc + policyPoll);
                lastTickUtc = nowUtc;
                if (gap > TimeSpan.FromSeconds(60))
                {
                    TrackInflight(() => RunResumeSafelyAsync(stop.Token));
                }

                var allowed = AllowedNow();
                if (!allowed)
                {
                    Task? cancellation;
                    lock (policyGate)
                    {
                        if (activePolicyStop != null) activePolicyCancellation ??= activePolicyStop.CancelAsync();
                        cancellation = activePolicyCancellation;
                    }
                    if (cancellation != null) await cancellation;
                }
                if ((!wasAllowed && allowed) || DateTimeOffset.UtcNow >= nextReconcile)
                {
                    Signal();
                    nextReconcile = DateTimeOffset.UtcNow + reconcile;
                }
                wasAllowed = allowed;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task RunResumeSafelyAsync(CancellationToken token)
    {
        try { await HandleResumeAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { onError(error); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lock (delayedTimerGate)
        {
            delayedSignalDueTick = long.MaxValue;
            delayedSignalTimer?.Dispose();
            delayedSignalTimer = null;
        }
        var failures = new List<Exception>();
        foreach (var watcher in watchers)
        {
            try { watcher.Dispose(); }
            catch (Exception error) { failures.Add(error); }
        }
        var cancellation = stop.CancelAsync();
        signals.Writer.TryComplete();
        async Task SettleAllAsync()
        {
            await Task.WhenAll(worker, timer, cancellation).ConfigureAwait(false);
            while (!inflightOps.IsEmpty)
            {
                await Task.WhenAll(inflightOps.Values.ToArray()).ConfigureAwait(false);
            }
        }
        var settled = SettleAllAsync();
        try { await settled.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (settled.IsCompleted)
            {
                stop.Dispose();
                queueWorkGate.Dispose();
            }
            else _ = RetireAsync(settled);
        }
        if (failures.Count != 0) throw new AggregateException("Background shutdown failed.", failures);
    }

    private async Task RetireAsync(Task settled)
    {
        try { await settled; }
        catch (Exception error) { onError(error); }
        finally
        {
            stop.Dispose();
            queueWorkGate.Dispose();
        }
    }
}
