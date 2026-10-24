using System.Text.Json;
using Windows.Graphics.Imaging;
using WinSight.Infrastructure.Media;
using WinSight.InferenceHost;

internal static class ParentLaunchLifecycle
{
    internal static async Task<int> RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        if (File.Exists(output)) throw new ArgumentException("Use a NEW lifecycle report path.");
        var executable = Path.Combine(AppContext.BaseDirectory, "ocr", "WinSight.OcrHost.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        if (File.Exists(executable)) throw new InvalidOperationException("Refusing to replace an existing worker.");
        File.WriteAllText(executable, "SYNTHETIC NONEXECUTABLE START-FAULT FIXTURE");
        var results = new List<object>();
        int failures = 0;
        foreach (var mode in Enum.GetValues<LaunchFault>())
        {
            LaunchState.Reset(mode);
            Exception? observed = null;
            OcrWorkerResult? reply = null;
            using var pixels = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 2, 2, BitmapAlphaMode.Premultiplied);
            try { reply = await NativeMediaCommand.ExerciseOcrParentAsync(pixels); }
            catch (Exception error) { observed = error; }
            bool expectRetain = mode is LaunchFault.StartUnconfirmed or LaunchFault.RunUnconfirmed;
            bool pinOpen = LaunchState.Pin?.CanRead == true;
            bool directoryExists = Directory.Exists(LaunchState.Directory);
            bool protectedAgainstWrite = false;
            try { using var attempt = new FileStream(executable, FileMode.Open, FileAccess.Write, FileShare.None); }
            catch (IOException) { protectedAgainstWrite = true; }
            bool pinRetained = LaunchState.Pin is not null && MediaRetention.Resources.Contains(LaunchState.Pin);
            bool directoryRetained = LaunchState.Directory is not null && MediaRetention.Resources.Contains(LaunchState.Directory);
            bool correctError = mode == LaunchFault.Success
                ? observed is null && reply?.Text == "lifecycle fixture"
                : ReferenceEquals(observed, LaunchState.Error);
            bool workerDisposition = LaunchState.Worker is null || (expectRetain
                ? MediaRetention.Resources.Contains(LaunchState.Worker) && !LaunchState.Worker.Disposed
                : LaunchState.Worker.Disposed);
            bool pass = correctError && directoryExists == expectRetain && pinOpen == expectRetain
                && protectedAgainstWrite == expectRetain && pinRetained == expectRetain
                && directoryRetained == expectRetain && workerDisposition;
            if (!pass) failures++;
            results.Add(new { mode = mode.ToString(), pass, correctError, expectRetain,
                workerWasReturned = LaunchState.Worker is not null, directoryExists, pinOpen,
                protectedAgainstWrite, pinRetained, directoryRetained, workerDisposition });
            // No process was created: release only these known synthetic test resources after observation.
            LaunchState.Pin?.Dispose();
            MediaRetention.Resources.Clear();
            if (LaunchState.Directory is { } path && Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        File.Delete(executable);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { cases = results, passed = results.Count - failures,
            failed = failures, realPngPreparation = true, realFileShareLease = true, nativeProcessesCreated = 0,
            sourceUnderTest = "production IsolatedOcr.cs catch/finally; WIC PNG and FileShare real; process/path helpers stubbed",
            adjacentPathPolicyNotTested = true,
            noPhysicalUnsettledProcessInvented = true }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"OCR parent lifecycle: {results.Count - failures}/{results.Count}");
        return failures == 0 ? 0 : 1;
    }
}
