using System.Text;

namespace WinSight.Infrastructure.Media;

public sealed class NativeMediaProcessClient : IAsyncDisposable
{
    private readonly string hostPath, workspacePath;
    private readonly TimeSpan requestTimeout, terminationTimeout;
    private readonly int maxOutputBytes;
    private readonly bool forceUnconfirmedTerminationForTesting;
    private readonly SemaphoreSlim worker = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private readonly HashSet<TaskCompletionSource> operations = [];
    private readonly List<Exception> settlementErrors = [];
    private bool disposed;
    private Task? disposal;

    public NativeMediaProcessClient(NativeMediaProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        hostPath = MediaTaskFiles.LocalPath(options.HostExecutable);
        workspacePath = MediaTaskFiles.LocalPath(options.WorkspaceRoot);
        ValidateTimeout(options.RequestTimeout, nameof(options.RequestTimeout));
        ValidateTimeout(options.TerminationTimeout, nameof(options.TerminationTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxOutputBytes);
        requestTimeout = options.RequestTimeout;
        terminationTimeout = options.TerminationTimeout;
        maxOutputBytes = options.MaxOutputBytes;
        forceUnconfirmedTerminationForTesting = options.ForceUnconfirmedTerminationForTesting;
    }

    public Task<NativeMediaArtifactLease> ExtractAsync(NativeMediaProcessRequest request, CancellationToken cancellationToken = default) =>
        ExtractAsync(request, NativeMediaOperation.Extract, cancellationToken);

    public Task<NativeMediaArtifactLease> ExtractSoundtrackAsync(NativeMediaProcessRequest request, CancellationToken cancellationToken = default) =>
        ExtractAsync(request, NativeMediaOperation.Soundtrack, cancellationToken);

    /// <summary>Returns a point-in-time snapshot; callers own source identity across later operations.</summary>
    public Task<NativeMediaInfo> InspectAsync(string sourcePath, string kind, CancellationToken cancellationToken = default) =>
        ExecuteAsync(NativeMediaOperation.Inspect, () =>
        {
            if (kind is not ("audio" or "video")) throw new ArgumentException("Inspect kind must be audio or video.", nameof(kind));
        }, () =>
        {
            sourcePath = MediaTaskFiles.LocalPath(sourcePath);
            return MediaTaskFiles.CreateInspect(hostPath, workspacePath, sourcePath, kind);
        }, (_, output, _) => NativeMediaProtocol.ParseInspect(output, sourcePath, kind), cancellationToken);

    public Task<NativeImageInspection> InspectImageAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        ExecuteAsync(NativeMediaOperation.InspectImage, () => ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath), () =>
        {
            sourcePath = MediaTaskFiles.LocalPath(sourcePath);
            return MediaTaskFiles.CreateInspect(hostPath, workspacePath, sourcePath, "image");
        }, (_, output, _) => NativeImageInspection.Parse(output, sourcePath), cancellationToken);

    private Task<NativeMediaArtifactLease> ExtractAsync(NativeMediaProcessRequest request, NativeMediaOperation operation,
        CancellationToken cancellationToken) => ExecuteAsync(operation, () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateRequest(request);
            if (operation == NativeMediaOperation.Soundtrack && request.Kind != "audio")
                throw new ArgumentException("Soundtrack kind must be audio.", nameof(request));
        }, () =>
        {
            request = request with { SourcePath = MediaTaskFiles.LocalPath(request.SourcePath) };
            return MediaTaskFiles.Create(hostPath, workspacePath, request);
        }, (files, output, token) =>
        {
            var parsed = NativeMediaProtocol.Parse(output, request);
            files.PinArtifact(parsed.Filename);
            NativeMediaProtocol.ValidateArtifact(files.ArtifactStream, request, output, token);
            return new NativeMediaArtifactLease(files, request, parsed);
        }, cancellationToken);

    private Task<T> ExecuteAsync<T>(NativeMediaOperation operation, Action validate, Func<MediaTaskFiles> prepare,
        Func<MediaTaskFiles, string, CancellationToken, T> consume, CancellationToken cancellationToken)
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (settlementErrors.Count != 0) throw new IOException("Native media client has retained unsettled resources.");
            cancellationToken.ThrowIfCancellationRequested();
            validate();
            if (operations.Count >= 16) throw new IOException("Native media admission limit (16) reached.");
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            operations.Add(completion);
        }
        return ExecuteCoreAsync(operation, prepare, consume, cancellationToken, completion);
    }

    private async Task<T> ExecuteCoreAsync<T>(NativeMediaOperation operation, Func<MediaTaskFiles> prepare,
        Func<MediaTaskFiles, string, CancellationToken, T> consume, CancellationToken callerToken, TaskCompletionSource completion)
    {
        using var deadline = new CancellationTokenSource(requestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token, shutdown.Token);
        MediaTaskFiles? files = null;
        WindowsNativeMediaProcess? process = null;
        var acquired = false;
        try
        {
            await worker.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            linked.Token.ThrowIfCancellationRequested();
            lock (gate)
                if (settlementErrors.Count != 0) throw new IOException("Native media client has retained unsettled resources.");
            files = prepare();
            linked.Token.ThrowIfCancellationRequested();
            process = WindowsNativeMediaProcess.Start(hostPath, files.RequestPath, files.TaskDirectory, terminationTimeout,
                forceUnconfirmedTerminationForTesting, operation);
            var result = await process.RunAsync(maxOutputBytes, terminationTimeout, linked.Token).ConfigureAwait(false);
            process.Dispose(); process = null;
            linked.Token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0)
                throw new IOException($"Native media worker exited with code {result.ExitCode}. stderr: {result.Error}");
            var value = consume(files, result.Output, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (operation is NativeMediaOperation.Inspect or NativeMediaOperation.InspectImage) files.Cleanup();
                files = null;
                return value;
            }
        }
        catch (Exception ex)
        {
            Exception failure = ex;
            if (ex is DecoderFallbackException) failure = new InvalidDataException("Native media stdio is not strict UTF-8.", ex);
            if (ex is UnauthorizedAccessException) failure = new IOException("Native media path access failed.", ex);
            if (ex is OperationCanceledException)
            {
                if (callerToken.IsCancellationRequested) failure = new OperationCanceledException("Media request canceled by caller.", ex, callerToken);
                else if (deadline.IsCancellationRequested) failure = new TimeoutException("Native media request deadline exceeded (including queue).", ex);
                else failure = new ObjectDisposedException(nameof(NativeMediaProcessClient), "Client shutdown canceled the media request.");
            }
            if (ex is MediaTerminationException || process is { Settled: false })
            {
                MediaRetention.Keep(process, files);
                var retained = new IOException($"Media termination unconfirmed; task and protections retained: {files?.TaskDirectory}", failure);
                lock (gate) settlementErrors.Add(retained);
                throw retained;
            }
            process?.Dispose(); process = null;
            if (files is not null)
            {
                try { files.Cleanup(); files = null; }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    var retained = new IOException("Media request failed and cleanup did not settle.", new AggregateException(failure, cleanup));
                    lock (gate) settlementErrors.Add(retained);
                    throw retained;
                }
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally
        {
            if (acquired) worker.Release();
            lock (gate)
            {
                operations.Remove(completion);
                completion.SetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new ValueTask(disposal);
            disposed = true;
            var pending = operations.Select(x => x.Task).ToArray();
            disposal = Task.Run(async () =>
            {
                shutdown.Cancel();
                await Task.WhenAll(pending).ConfigureAwait(false);
                shutdown.Dispose();
                worker.Dispose();
                lock (gate)
                    if (settlementErrors.Count != 0)
                        throw new IOException("Media client shutdown has retained unsettled resources.", new AggregateException(settlementErrors));
            });
            return new ValueTask(disposal);
        }
    }

    private static void ValidateRequest(NativeMediaProcessRequest request)
    {
        if (request.Kind is not ("audio" or "frame") || !double.IsFinite(request.StartSeconds) || request.StartSeconds < 0 ||
            request.StartSeconds > TimeSpan.MaxValue.TotalSeconds || request.MaxEdge is < 64 or > 1024 ||
            request.Kind == "frame" && request.EndSeconds is not null ||
            request.Kind == "audio" && (request.EndSeconds is not { } end || !double.IsFinite(end) ||
                end <= request.StartSeconds || end - request.StartSeconds > 30 || end > TimeSpan.MaxValue.TotalSeconds))
            throw new ArgumentException("Invalid N1 media kind, interval or size.", nameof(request));
    }

    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(name, "A finite positive timer interval is required.");
    }
}
