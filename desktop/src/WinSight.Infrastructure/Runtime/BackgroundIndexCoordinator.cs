using System.Threading.Channels;
using WinSight.Core;
using WinSight.Infrastructure.Indexing;

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
    private readonly IncrementalIndexQueue? queue;
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<string>, bool, CancellationToken, Task>? indexPaths;
    private readonly HostSignals hostSignals;
    private readonly ResourceBudgetController budgetController;
    private CancellationTokenSource? activePolicyStop;
    private Task? activePolicyCancellation;
    private int disposed;
    private bool queueReconciled;
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
    public long WatcherOverflows => queue?.WatcherOverflows ?? 0;
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
        Func<CancellationToken, Task>? releaseCaches = null)
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
                    onError(eventArgs.GetException());
                    queue?.OnWatcherError(root, eventArgs.GetException());
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

    private void Changed(object sender, FileSystemEventArgs args)
    {
        var ignored = ignoredRoots.Any(root => DesktopBackend.IsUnder(args.FullPath, root));
        if (ignored && (args is not RenamedEventArgs renamed
            || ignoredRoots.Any(root => DesktopBackend.IsUnder(renamed.OldFullPath, root)))) return;

        if (queue != null)
        {
            var isCode = settings.CodeFolders.Any(f => DesktopBackend.IsUnder(args.FullPath, f));
            var root = (isCode ? settings.CodeFolders : settings.Folders).FirstOrDefault(f => DesktopBackend.IsUnder(args.FullPath, f))
                ?? Path.GetDirectoryName(args.FullPath) ?? args.FullPath;
            _ = queue.EnqueueEventAsync(args.FullPath, root, isCode, args.ChangeType, (args as RenamedEventArgs)?.OldFullPath);
        }
        Signal();
    }
    private void Signal() => signals.Writer.TryWrite(true);
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

        if (queue != null)
        {
            foreach (var code in new[] { false, true })
            {
                var roots = code ? settings.CodeFolders : settings.Folders;
                foreach (var root in roots)
                {
                    if (Directory.Exists(root))
                    {
                        await queue.ReconcileRootAsync(root, code, force: true, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        budgetController.ComputeBudgets();
        Signal();
    }

    public async Task<IndexingQueueStatus> GetIndexingQueueStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budgets = budgetController.ComputeBudgets();
        if (queue != null)
        {
            return await queue.GetIndexingQueueStatusAsync(budgets, cancellationToken).ConfigureAwait(false);
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
            UsnState: "not-implemented",
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

                if (queue != null)
                {
                    // The first pass reconciles every root (offline changes); later passes only reconcile a
                    // root after a watcher overflow, so ordinary saves stay on the per-file path.
                    var force = !queueReconciled;
                    foreach (var code in new[] { false, true })
                    {
                        var roots = code ? settings.CodeFolders : settings.Folders;
                        foreach (var root in roots)
                        {
                            if (!AllowedNow() || !indexingAllowed()) break;
                            await queue.ReconcileRootAsync(root, code, force, stop.Token).ConfigureAwait(false);
                        }
                    }
                    queueReconciled = true;

                    var processed = await queue.ProcessReadyTasksAsync(50, stop.Token).ConfigureAwait(false);
                    if (processed > 0 && indexPaths != null)
                    {
                        continue;
                    }
                }

                foreach (var code in new[] { false, true })
                {
                    var roots = code ? settings.CodeFolders : settings.Folders;
                    if (roots.Count == 0) continue;
                    using var policyStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    lock (policyGate) { activePolicyStop = policyStop; activePolicyCancellation = null; }
                    try
                    {
                        if (!AllowedNow() || !indexingAllowed()) break;
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
                    _ = HandleResumeAsync(stop.Token);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var failures = new List<Exception>();
        foreach (var watcher in watchers)
        {
            try { watcher.Dispose(); }
            catch (Exception error) { failures.Add(error); }
        }
        var cancellation = stop.CancelAsync();
        signals.Writer.TryComplete();
        var settled = Task.WhenAll(worker, timer, cancellation);
        try { await settled.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (settled.IsCompleted) stop.Dispose();
            else _ = RetireAsync(settled);
        }
        if (failures.Count != 0) throw new AggregateException("Background shutdown failed.", failures);
    }

    private async Task RetireAsync(Task settled)
    {
        try { await settled; }
        catch (Exception error) { onError(error); }
        finally { stop.Dispose(); }
    }
}
