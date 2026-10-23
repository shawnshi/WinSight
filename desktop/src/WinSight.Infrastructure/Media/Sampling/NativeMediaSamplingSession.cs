using WinSight.Core;

namespace WinSight.Infrastructure.Media;

internal sealed class NativeMediaSample : IAsyncDisposable
{
    private readonly NativeMediaArtifactLease lease;
    internal IndexSegment Segment { get; }
    internal EmbeddingInput Input { get; }

    internal NativeMediaSample(NativeMediaArtifactLease lease, double end)
    {
        this.lease = lease;
        var image = lease.Modality == "image";
        Segment = new(image ? "frame" : "audio", lease.Modality, lease.StartSeconds, end, null);
        Input = image ? new("image", lease.ArtifactPath, NativeMediaSamplingPolicy.ImageBudget) : new("audio", lease.ArtifactPath);
    }

    public ValueTask DisposeAsync() => lease.DisposeAsync();
}

internal sealed class NativeMediaSamplingSession : IAsyncDisposable
{
    private readonly NativeMediaProcessClient client;
    private readonly NativeMediaSource source;
    private readonly int maximumOperations;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly object lifetime = new();
    private Task? disposal;
    private volatile bool closing;
    private int readClaimed;
    private int operations;
    private bool failed, framesFinished, audioFinished, omitted, budgetExhausted;
    private string? omissionDetail;
    private string? detail = "Sampling has not reached natural EOF.";
    private IEnumerator<double>? frameTimes;
    private IEnumerator<NativeMediaSamplingPolicy.AudioWindow>? audioWindows;
    private NativeMediaArtifactLease? pendingFrame;
    private ulong pendingHash;

    internal NativeMediaInfo Metadata { get; }
    internal bool CoverageComplete { get; private set; }
    internal string? Detail => detail is null ? null : $"{detail} Decode attempts: {operations}/{maximumOperations}.";

    internal NativeMediaSamplingSession(NativeMediaProcessClient client, NativeMediaSource source, NativeMediaInfo metadata, int maximumOperations)
    {
        NativeMediaSamplingPolicy.ValidateDuration(metadata.DurationSeconds);
        this.client = client;
        this.source = source;
        Metadata = metadata;
        this.maximumOperations = maximumOperations;
    }

    internal IAsyncEnumerable<NativeMediaSample> ReadAsync(bool includeImage, bool includeAudio, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(closing, this);
        if (Interlocked.Exchange(ref readClaimed, 1) != 0)
            throw new InvalidOperationException("A sampling session supports only one enumeration.");
        var hasFrames = Metadata.Kind == "video";
        omitted = (hasFrames && !includeImage) || (Metadata.HasAudio && !includeAudio);
        if (omitted)
        {
            omissionDetail = $"Existing modality omitted: {(hasFrames && !includeImage ? "image " : "")}{(Metadata.HasAudio && !includeAudio ? "audio" : "")}.";
            detail = omissionDetail;
        }
        framesFinished = !hasFrames || !includeImage;
        audioFinished = !Metadata.HasAudio || !includeAudio;
        if (!framesFinished) frameTimes = NativeMediaSamplingPolicy.FrameTimes(Metadata.DurationSeconds).GetEnumerator();
        if (!audioFinished) audioWindows = NativeMediaSamplingPolicy.AudioWindows(Metadata.DurationSeconds).GetEnumerator();
        return new Samples(this, cancellationToken);
    }

    private sealed class Samples(NativeMediaSamplingSession session, CancellationToken readToken) : IAsyncEnumerable<NativeMediaSample>
    {
        private int claimed;
        public IAsyncEnumerator<NativeMediaSample> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0)
                throw new InvalidOperationException("Repeated sampling enumeration is not allowed.");
            return new Enumerator(session, readToken, cancellationToken);
        }
    }

    private sealed class Enumerator : IAsyncEnumerator<NativeMediaSample>
    {
        private readonly NativeMediaSamplingSession session;
        private readonly CancellationTokenSource cancellation;
        private int moving;
        private bool disposed;
        private NativeMediaSample? current;
        internal Enumerator(NativeMediaSamplingSession session, CancellationToken readToken, CancellationToken enumerationToken)
        {
            this.session = session;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.shutdown.Token, readToken, enumerationToken);
        }
        public NativeMediaSample Current => current ?? throw new InvalidOperationException("No current media sample.");
        public async ValueTask<bool> MoveNextAsync()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Interlocked.Exchange(ref moving, 1) != 0)
                throw new InvalidOperationException("Concurrent sampling MoveNext is not allowed.");
            try
            {
                current = await session.NextAsync(cancellation.Token).ConfigureAwait(false);
                return current is not null;
            }
            finally { Volatile.Write(ref moving, 0); }
        }
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            // Current was transferred to the consumer; never revoke its independent artifact lease.
            try { await session.DisposeAsync().ConfigureAwait(false); }
            finally { cancellation.Dispose(); }
        }
    }

    private async Task<NativeMediaSample?> NextAsync(CancellationToken token)
    {
        // Admit cancellation under the gate too, so pre-cancelled reads release owned lookahead.
        await operation.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closing, this);
            token.ThrowIfCancellationRequested();
            if (failed) throw new InvalidOperationException("A failed sampling session cannot resume.");
            while (!framesFinished)
            {
                if (!frameTimes!.MoveNext())
                {
                    framesFinished = true;
                    return TakeFrame(Metadata.DurationSeconds);
                }
                var start = frameTimes.Current;
                if (operations == maximumOperations)
                {
                    ExhaustBudget();
                    return TakeFrame(start);
                }
                operations++;
                var lease = await client.ExtractAsync(new(source.Path, "frame", start, MaxEdge: NativeMediaSamplingPolicy.MaxEdge), token).ConfigureAwait(false);
                try
                {
                    ulong hash;
                    using (var stream = OpenArtifact(lease)) hash = NativeMediaFrameHash.Read(stream, token);
                    if (pendingFrame is not null && NativeMediaSamplingPolicy.Duplicate(pendingHash, hash)) continue;
                    var sample = TakeFrame(start);
                    pendingFrame = lease;
                    pendingHash = hash;
                    lease = null;
                    if (sample is not null) return sample;
                }
                finally { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); }
            }
            while (!audioFinished)
            {
                if (!audioWindows!.MoveNext()) { audioFinished = true; break; }
                if (operations == maximumOperations) { ExhaustBudget(); break; }
                var window = audioWindows.Current;
                operations++;
                var request = new NativeMediaProcessRequest(source.Path, "audio", window.StartSeconds, window.EndSeconds);
                var lease = Metadata.Kind == "video"
                    ? await client.ExtractSoundtrackAsync(request, token).ConfigureAwait(false)
                    : await client.ExtractAsync(request, token).ConfigureAwait(false);
                try
                {
                    double rms;
                    using (var stream = OpenArtifact(lease)) rms = NativeMediaPcm.Rms(stream, token);
                    if (!NativeMediaSamplingPolicy.Audible(rms)) continue;
                    var sample = new NativeMediaSample(lease, window.EndSeconds);
                    lease = null;
                    return sample;
                }
                finally { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); }
            }
            token.ThrowIfCancellationRequested();
            if (!omitted && !budgetExhausted)
            {
                CoverageComplete = true;
                detail = null;
            }
            return null;
        }
        catch (Exception ex)
        {
            failed = true;
            CoverageComplete = false;
            detail = ex is OperationCanceledException ? "Sampling was cancelled before natural EOF." : $"Sampling failed ({ex.GetType().Name}); coverage is incomplete.";
            try { await ReleasePendingAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Sampling and artifact cleanup both failed.", ex, cleanup); }
            throw;
        }
        finally { operation.Release(); }
    }

    private const string BudgetDetail = "Decode attempt budget exhausted before all required media reached EOF.";
    private void ExhaustBudget()
    {
        framesFinished = audioFinished = true;
        budgetExhausted = true;
        detail = omissionDetail is null ? BudgetDetail : $"{BudgetDetail} {omissionDetail}";
    }

    private NativeMediaSample? TakeFrame(double end)
    {
        if (pendingFrame is null) return null;
        var sample = new NativeMediaSample(pendingFrame, end);
        pendingFrame = null;
        return sample;
    }

    private static FileStream OpenArtifact(NativeMediaArtifactLease lease) =>
        new(lease.ArtifactPath, FileMode.Open, FileAccess.Read, FileShare.Read);

    private async ValueTask ReleasePendingAsync()
    {
        var lease = pendingFrame;
        pendingFrame = null;
        if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetime)
        {
            closing = true;
            disposal ??= DisposeCoreAsync();
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        shutdown.Cancel();
        await operation.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReleasePendingAsync().ConfigureAwait(false);
        }
        finally
        {
            frameTimes?.Dispose();
            audioWindows?.Dispose();
            source.Dispose();
            operation.Release();
            shutdown.Dispose();
        }
    }
}
