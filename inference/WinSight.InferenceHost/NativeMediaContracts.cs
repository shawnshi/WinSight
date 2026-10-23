namespace WinSight.InferenceHost;

internal sealed record MediaDecodeRequest(string InputPath, string OutputDirectory, string Kind,
    double StartSeconds, double? EndSeconds = null, int MaxEdge = 512);

internal sealed record MediaDecodeArtifact(string RelativePath, string Modality,
    double StartSeconds, double? EndSeconds, int? SampleRate = null, int? Channels = null,
    int? BitsPerSample = null, int? Width = null, int? Height = null);

internal sealed record MediaDecodeResult(int Protocol, string SourcePath, string Kind,
    double? SourceDurationSeconds, IReadOnlyList<MediaDecodeArtifact> Artifacts);

// Additive operation-specific metadata protocol; existing decode/stdin schemas remain unchanged.
internal sealed record MediaInspectRequest(string InputPath, string Kind);
internal sealed record MediaInspectResult(int Protocol, string SourcePath, string Kind, double DurationSeconds,
    bool HasAudio, int? AudioTrackCount, int Width, int Height);
