using WinSight.Core;

namespace WinSight.Infrastructure.Runtime;

// A generation includes queued entrants, so none can survive pause and enter after resume.
internal sealed class IndexingController(TimeSpan? transitionTimeout = null)
{
    private readonly object sync = new();
    private readonly SemaphoreSlim transitions = new(1, 1);
    private readonly SemaphoreSlim workGate = new(1, 1);
    private readonly TimeSpan timeout = transitionTimeout ?? TimeSpan.FromSeconds(10);
    private CancellationTokenSource epoch = new();
    private Task cancellation = Task.CompletedTask;
    private TaskCompletionSource idle = Completed();
    private int participants;
    private bool stopping;
    private bool retiring;
    private IndexingControlState state;
    private string? failure;
    private Exception? unsettled;

    public IndexingControlState State { get { lock (sync) return state; } }
    public bool HasWork { get { lock (sync) return participants != 0 || unsettled is not null; } }
    public string? Failure { get { lock (sync) return failure; } }

    // NM-N2B2-LIFECYCLE-01: logical callback exit is not proof of native resource settlement.
    // Only the owning callback's failed resource shutdown reports this irreversible instance fence.
    internal void ReportUnsettled(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        lock (sync)
        {
            unsettled ??= cause;
            state = IndexingControlState.Pausing;
            failure = IndexingSettlementException.Diagnostic;
        }
    }

    private void ThrowIfUnsettled()
    {
        if (unsettled is not null) throw new IndexingSettlementException(unsettled);
    }

    public async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CancellationToken generation;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            ThrowIfUnsettled();
            if (state != IndexingControlState.Running) throw new IndexingPausedException();
            generation = epoch.Token;
            if (participants++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, generation);
            await workGate.WaitAsync(linked.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                lock (sync) ThrowIfUnsettled(); // Already queued entrants must not pass a later settlement failure.
                await work(linked.Token);
                linked.Token.ThrowIfCancellationRequested();
            }
            finally { workGate.Release(); }
        }
        catch (OperationCanceledException) when (generation.IsCancellationRequested)
        {
            throw new IndexingInterruptedException(generation);
        }
        finally
        {
            lock (sync) if (--participants == 0) idle.TrySetResult();
        }
    }

    public Task<bool> SetPausedAsync(bool paused, CancellationToken token) => TransitionAsync(paused, false, token);

    public Task StopAsync()
    {
        lock (sync) stopping = true;
        return TransitionAsync(true, true, CancellationToken.None);
    }

    private async Task<bool> TransitionAsync(bool paused, bool shutdown, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!await transitions.WaitAsync(timeout, token))
        {
            lock (sync) if (unsettled is null) failure = "Indexing control transition queue timed out; admission is unchanged.";
            throw new TimeoutException("Indexing control transition queue timed out.");
        }
        try
        {
            Task drained;
            bool cancel;
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(stopping && !shutdown, this);
                if (!shutdown && state == (paused ? IndexingControlState.Paused : IndexingControlState.Running)) return false;
                cancel = state == IndexingControlState.Running || (unsettled is not null && !epoch.IsCancellationRequested);
                state = IndexingControlState.Pausing;
                drained = idle.Task;
            }
            // Cancellation callbacks may reenter status/work APIs or block: never run under sync.
            if (cancel) cancellation = epoch.CancelAsync();
            var settled = Task.WhenAll(drained, cancellation);
            if (shutdown && !retiring)
            {
                retiring = true;
                _ = RetireAsync(settled, epoch);
            }
            try
            {
                var remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Indexing transition deadline elapsed.");
                await settled.WaitAsync(remaining, token);
            }
            catch (Exception error)
            {
                lock (sync) if (unsettled is null) failure = error is TimeoutException
                    ? "Indexing did not quiesce within the pause deadline; new work remains blocked."
                    : "Indexing transition failed: " + error.Message + "; new work remains blocked.";
                throw;
            }
            lock (sync)
            {
                ThrowIfUnsettled(); // Never publish Paused/Running or clear the sticky failure after logical idle.
                if (!paused && !stopping)
                {
                    epoch.Dispose();
                    epoch = new();
                    cancellation = Task.CompletedTask;
                    state = IndexingControlState.Running;
                }
                else
                {
                    state = IndexingControlState.Paused;
                }
                failure = null;
            }
            return true;
        }
        finally { transitions.Release(); }
    }

    private async Task RetireAsync(Task settled, CancellationTokenSource source)
    {
        try { await settled; }
        catch (Exception error) { lock (sync) if (unsettled is null) failure = "Shutdown cancellation failed: " + error.Message; }
        finally { source.Dispose(); }
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

internal sealed class IndexingInterruptedException(CancellationToken token) : OperationCanceledException(token);

internal sealed class IndexingSettlementException(Exception cause) : IOException(Diagnostic, cause)
{
    internal const string Diagnostic = "Indexing resources did not settle; admission remains blocked. Restart the process before retrying.";
}
