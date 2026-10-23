using System.Threading.Channels;
using WinSight.Core;

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
    private CancellationTokenSource? activePolicyStop;
    private Task? activePolicyCancellation;
    private int disposed;

    public Task Completion => worker;
    public Exception? Failure => worker.Exception?.GetBaseException();

    public BackgroundIndexCoordinator(AppSettings settings, Func<IndexRequest, CancellationToken, Task> index,
        Action<Exception> onError, TimeSpan? debounce = null, TimeSpan? reconcile = null,
        IReadOnlyList<string>? ignoredRoots = null, Func<bool>? indexingAllowed = null,
        Func<bool>? isOnAcPower = null, TimeSpan? policyPoll = null)
    {
        this.settings = settings;
        this.index = index;
        this.onError = onError;
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        this.ignoredRoots = ignoredRoots?.ToArray() ?? [];
        this.indexingAllowed = indexingAllowed ?? (() => true);
        this.isOnAcPower = isOnAcPower ?? PowerSource.IsOnAcPower;
        this.policyPoll = policyPoll ?? TimeSpan.FromSeconds(30);
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
        Signal();
    }
    private void Signal() => signals.Writer.TryWrite(true);
    public void RequestReconciliation() => Signal();

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
        try
        {
            while (await clock.WaitForNextTickAsync(stop.Token))
            {
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
