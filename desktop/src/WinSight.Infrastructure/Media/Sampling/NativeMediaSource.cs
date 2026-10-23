using WinSight.Infrastructure.Extraction;

namespace WinSight.Infrastructure.Media;

internal sealed class NativeMediaSource : IDisposable
{
    private readonly object gate = new();
    private SafeFile? pin;
    internal string Path { get; }
    internal string Kind { get; }

    private NativeMediaSource(string path, string kind, SafeFile pin)
    {
        Path = path;
        Kind = kind;
        this.pin = pin;
    }

    internal static NativeMediaSource Acquire(string sourcePath, string kind)
    {
        // LocalPath must reject network/device paths before any source I/O.
        var path = MediaTaskFiles.LocalPath(sourcePath);
        if (kind is not ("audio" or "video") || FilePolicy.Kind(path, code: false) != kind)
            throw new ArgumentException("Source kind must match a non-code audio/video file.", nameof(kind));
        var pin = SafeFile.Open(path);
        try
        {
            if (pin.Stream!.Length > ContentExtractor.MaxBytes)
                throw new InvalidDataException("Media source exceeds the 32 MiB extraction limit.");
            return new(path, kind, pin);
        }
        catch { pin.Dispose(); throw; }
    }

    internal NativeMediaSource Duplicate()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(pin is null, this);
            var copy = Acquire(Path, Kind);
            if (copy.pin!.Identity == pin.Identity && copy.pin.Stream!.Length == pin.Stream!.Length) return copy;
            copy.Dispose();
            throw new IOException("Media source identity changed before session acquisition.");
        }
    }

    public void Dispose()
    {
        lock (gate) { pin?.Dispose(); pin = null; }
    }
}

internal sealed class NativeMediaSamplingService
{
    internal const string Revision = "native-media-sampling-v1";
    private readonly NativeMediaProcessClient client;
    private readonly int maximumOperations;

    internal NativeMediaSamplingService(NativeMediaProcessClient client, int maximumOperations = ContentExtractor.MaxSegments)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (maximumOperations is < 1 or > ContentExtractor.MaxSegments)
            throw new ArgumentOutOfRangeException(nameof(maximumOperations));
        this.client = client;
        this.maximumOperations = maximumOperations;
    }

    internal async Task<NativeMediaSamplingSession> OpenAsync(NativeMediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var pin = source.Duplicate();
        try
        {
            var metadata = await client.InspectAsync(pin.Path, pin.Kind, cancellationToken).ConfigureAwait(false);
            if (metadata.SourcePath != pin.Path || metadata.Kind != pin.Kind)
                throw new InvalidDataException("Metadata does not identify the pinned source.");
            return new(client, pin, metadata, maximumOperations);
        }
        catch { pin.Dispose(); throw; }
    }
}
