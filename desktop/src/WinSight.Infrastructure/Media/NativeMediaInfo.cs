namespace WinSight.Infrastructure.Media;

public sealed record NativeMediaInfo(string SourcePath, string Kind, double DurationSeconds,
    bool HasAudio, int? AudioTrackCount, int Width, int Height);
