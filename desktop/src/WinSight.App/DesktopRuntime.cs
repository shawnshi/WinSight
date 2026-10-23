using WinSight.Core;
using WinSight.Infrastructure.Runtime;

namespace WinSight.App;

internal static class DesktopRuntime
{
    public static async Task<IDesktopBackend> CreateAsync(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("The validated data directory must be absolute.");
        return await DesktopBackend.CreateAsync(new DesktopOptions(dataDirectory));
    }
}
