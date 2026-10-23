using System.Text.Json;
using WinSight.InferenceHost;

namespace WinSight.NativeMedia.Tests;

internal static class DriveClassifierCases
{
    internal static async Task RunAsync(string root, string wave, Func<string, Func<Task>, Task> runCase)
    {
        var parent = Path.Combine(root, "drive-cases");
        Directory.CreateDirectory(parent);
        var localRoot = Path.GetPathRoot(root)!;
        var sequence = 0;
        foreach (var rejected in new[] { DriveType.Network, DriveType.Unknown, DriveType.NoRootDirectory })
            foreach (var role in new[] { "request", "source-direct", "output-direct", "source-json", "output-json" })
                await runCase($"drive-reject-{rejected}-{role}-before-io", async () =>
                {
                    var calls = new List<string>();
                    DriveType Classify(string driveRoot)
                    {
                        calls.Add(driveRoot);
                        if (driveRoot == @"Z:\") return rejected;
                        if (driveRoot.Equals(localRoot, StringComparison.OrdinalIgnoreCase)) return DriveType.Fixed;
                        throw new InvalidOperationException("Unexpected classifier argument: " + driveRoot);
                    }
                    var output = Path.Combine(parent, $"absent-output-{++sequence}");
                    // Z: is only a lexical input to the injected classifier. Never probe/map it.
                    // Bad output must win over absent local source metadata, not just fail eventually.
                    var request = new MediaDecodeRequest(
                        role.StartsWith("source") ? @"Z:\nonexistent-source.wav" : Path.Combine(parent, "nonexistent-source.wav"),
                        role.StartsWith("output") ? @"Z:\nonexistent-output" : output, "audio", 0, 1);
                    Func<Task> operation;
                    if (role == "request")
                        operation = () => NativeMediaCommand.RunRequestFileWithDriveClassifierAsync(@"Z:\nonexistent-request.json", Classify);
                    else if (role.EndsWith("json"))
                    {
                        var path = Path.Combine(parent, $"request-{sequence}.json");
                        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                        operation = () => NativeMediaCommand.RunRequestFileWithDriveClassifierAsync(path, Classify);
                    }
                    else operation = () => NativeMediaCommand.ExecuteWithDriveClassifierAsync(request, Classify);
                    Exception? rejection = null;
                    try { await operation(); }
                    catch (IOException error) when (error.GetType() == typeof(IOException) && error.Message ==
                        $"Media paths require a local drive (Fixed, Removable, CDRom or Ram); classified drive type: {rejected}.")
                    { rejection = error; }
                    var expectedRoots = role switch
                    {
                        "request" or "source-direct" => new[] { @"Z:\" },
                        "output-direct" or "source-json" => new[] { localRoot, @"Z:\" },
                        _ => new[] { localRoot, localRoot, @"Z:\" }
                    };
                    if (rejection is null || !calls.SequenceEqual(expectedRoots, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException("Expected the specific classified-drive guard rejection and call order before missing-file/open/create errors.");
                    if (Directory.Exists(output) || File.Exists(output)) throw new InvalidDataException("Rejected path created local output.");
                    Console.WriteLine($"OBSERVED drive={rejected} role={role} rejection={rejection.GetType().Name} roots={string.Join(',', calls)}; no Z: filesystem probe");
                });

        foreach (var allowed in new[] { DriveType.Fixed, DriveType.Removable, DriveType.CDRom, DriveType.Ram })
            await runCase($"drive-allow-{allowed}-real-local-io", async () =>
            {
                var calls = new List<string>();
                DriveType Classify(string driveRoot)
                {
                    if (!driveRoot.Equals(localRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Classifier must receive the drive root, not a media filename.");
                    calls.Add(driveRoot);
                    return allowed;
                }
                var request = new MediaDecodeRequest(wave, Path.Combine(parent, $"allowed-{allowed}"), "audio", 1, 2);
                var requestPath = Path.Combine(parent, $"allowed-{allowed}.json");
                await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                var result = await NativeMediaCommand.RunRequestFileWithDriveClassifierAsync(requestPath, Classify);
                if (calls.Count < 3 || result.SourcePath != wave || result.Artifacts.Count != 1)
                    throw new InvalidDataException("Allowed classification did not traverse request/source/output and real decode.");
                var artifact = result.Artifacts[0];
                if (Path.IsPathRooted(artifact.RelativePath) || artifact.RelativePath.Contains("..")) throw new InvalidDataException("Artifact escaped local output.");
                var bytes = await File.ReadAllBytesAsync(Path.Combine(request.OutputDirectory, artifact.RelativePath));
                if (bytes.Length < 32044 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)) throw new InvalidDataException("Allowed classification did not write real WAV.");
                Console.WriteLine($"OBSERVED allowedDrive={allowed} real request/read/render/write roots={string.Join(',', calls)}");
            });
    }
}
