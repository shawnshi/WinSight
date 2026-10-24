using System.Text.Json;
using WinSight.Shared;

internal enum LaunchFault { StartUnconfirmed, StartConfirmedFailure, RunUnconfirmed, RunConfirmedFailure, Success }
internal static class LaunchState
{
    internal static LaunchFault Mode;
    internal static Exception Error = null!;
    internal static string? Directory;
    internal static FileStream? Pin;
    internal static WinSight.Infrastructure.Media.WindowsNativeMediaProcess? Worker;
    internal static void Reset(LaunchFault mode)
    {
        Mode = mode; Directory = null; Pin = null; Worker = null;
        Error = mode is LaunchFault.StartUnconfirmed or LaunchFault.RunUnconfirmed
            ? new WinSight.Infrastructure.Media.MediaTerminationException("Synthetic unconfirmed shutdown.", new IOException("synthetic"))
            : new IOException("Synthetic confirmed failure.");
    }
}

namespace WinSight.Infrastructure.Media
{
    internal enum NativeMediaOperation { InspectOcr }
    internal sealed class MediaTerminationException(string message, Exception inner) : IOException(message, inner);
    internal static class MediaRetention
    {
        internal static readonly List<object> Resources = [];
        internal static void Keep(params object?[] resources)
        {
            foreach (var resource in resources) if (resource is not null) Resources.Add(resource);
        }
    }
    internal sealed class WindowsNativeMediaProcess : IDisposable
    {
        internal bool Settled { get; private set; }
        internal bool Disposed { get; private set; }
        internal static WindowsNativeMediaProcess Start(string executable, string request, string directory,
            TimeSpan timeout, bool force, NativeMediaOperation operation)
        {
            LaunchState.Directory = directory;
            if (LaunchState.Mode is LaunchFault.StartUnconfirmed or LaunchFault.StartConfirmedFailure)
                throw LaunchState.Error; // Deliberately before the caller receives/assigns an owner.
            return LaunchState.Worker = new WindowsNativeMediaProcess
            { Settled = LaunchState.Mode != LaunchFault.RunUnconfirmed };
        }
        internal Task<(uint ExitCode, string Output, string Error)> RunAsync(int limit, TimeSpan timeout, CancellationToken token)
        {
            if (LaunchState.Mode != LaunchFault.Success)
                return Task.FromException<(uint ExitCode, string Output, string Error)>(LaunchState.Error);
            var json = JsonSerializer.Serialize(new { protocol = 2, engine = OcrIdentity.Engine,
                engineVersion = OcrIdentity.EngineVersion, text = "lifecycle fixture", truncated = false,
                languages = new[] { "zh-Hans-CN", "en-US" }, modelProfile = OcrIdentity.ModelProfile,
                regions = new[] { new { line = 0, start = 0, length = 17, left = 0.1, top = 0.1, width = 0.5, height = 0.2 } }, regionsTruncated = false });
            return Task.FromResult((0u, json, ""));
        }
        public void Dispose()
        {
            if (!Settled) throw new InvalidOperationException("Must not dispose an unconfirmed owner.");
            Disposed = true;
        }
    }
}

namespace WinSight.InferenceHost
{
    internal static partial class NativeMediaCommand
    {
        internal static Task<OcrWorkerResult> ExerciseOcrParentAsync(Windows.Graphics.Imaging.SoftwareBitmap pixels)
            => RunIsolatedOcrAsync(pixels, CancellationToken.None);
        private static DriveType GetDriveType(string root) => DriveType.Fixed;
        private static string ValidateLocalPath(string path, Func<string, DriveType> classifier) => Path.GetFullPath(path);
        private static FileStream OpenPinnedFile(string path, long maximum)
            => LaunchState.Pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
