using System.Collections.Concurrent;

namespace WinSight.Bridge;

public sealed record AllowedFileInfo(string Path, string Version, double ModifiedTime, long Size);

public sealed class AllowedFileRegistry
{
    private readonly ConcurrentDictionary<string, AllowedFileInfo> allowedFiles =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public void Register(string path, string version, double mtime, long size)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        allowedFiles[path] = new AllowedFileInfo(path, version, mtime, size);
    }

    public bool IsAllowed(string path, out AllowedFileInfo? info)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            info = null;
            return false;
        }

        return allowedFiles.TryGetValue(path, out info);
    }

    public void Clear()
    {
        allowedFiles.Clear();
    }
}
