namespace WinSight.Infrastructure.Media;

/// <summary>Consumer-owned pins and artifact. Client shutdown never disposes a returned lease.</summary>
public sealed class NativeMediaArtifactLease : IAsyncDisposable
{
    private readonly MediaTaskFiles files;
    private readonly object gate = new();
    private Task? disposal;
    public string ArtifactPath { get; }
    public string Modality { get; }
    public double StartSeconds { get; }
    public double? EndSeconds { get; }
    public double? SourceDurationSeconds { get; }
    public string TaskDirectory { get; }

    internal NativeMediaArtifactLease(MediaTaskFiles files, NativeMediaProcessRequest request, NativeMediaProtocol.Result result)
    {
        this.files = files;
        ArtifactPath = files.ArtifactPath;
        TaskDirectory = files.TaskDirectory;
        Modality = result.Modality;
        StartSeconds = request.StartSeconds;
        EndSeconds = request.EndSeconds;
        SourceDurationSeconds = result.Duration;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            // Cache failures too: repeated Dispose is not an implicit cleanup retry against changed paths.
            disposal ??= Task.Run(files.Cleanup);
            return new ValueTask(disposal);
        }
    }
}
