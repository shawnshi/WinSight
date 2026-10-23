namespace WinSight.Infrastructure.Configuration;

public sealed class SettingsConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException($"Settings changed since loading (expected revision {expectedRevision}, current {actualRevision}); reload before saving.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}
