namespace WinSight.Infrastructure.Media;

public sealed record NativeMediaProcessRequest(string SourcePath, string Kind, double StartSeconds,
    double? EndSeconds = null, int MaxEdge = 512);

public sealed class NativeMediaProcessOptions
{
    public required string HostExecutable { get; init; }
    public required string WorkspaceRoot { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan TerminationTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxOutputBytes { get; init; } = 65536;
    internal bool ForceUnconfirmedTerminationForTesting { get; init; }
}
