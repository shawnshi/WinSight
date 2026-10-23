using System.Diagnostics;
using System.Text.Json;
using WinSight.InferenceHost;
using WinSight.NativeMedia.Tests;

if (args.Length != 4 || args[0] != "--artifact-root" || args[2] != "--inference-host" ||
    !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[3]) || !File.Exists(args[3]))
    throw new ArgumentException("Tail probe requires a new absolute artifact root and actual inference host.");
var root = args[1];
if (Directory.Exists(root) || File.Exists(root)) throw new ArgumentException("Artifact root must be new.");
Directory.CreateDirectory(root);
Directory.SetCurrentDirectory(root);
var fixtures = Path.Combine(root, "fixtures");
await SyntheticMedia.CreateAsync(fixtures);
var source = Path.Combine(fixtures, "eof-marker-4s.wav");
var sourceHash = SyntheticMedia.Hash(source);
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
foreach (var route in new[] { "linked", "actual-host" })
{
    var request = new MediaDecodeRequest(source, Path.Combine(root, route), "audio", 3, 4);
    MediaDecodeResult result;
    if (route == "linked") result = await NativeMediaCommand.ExecuteAsync(request);
    else
    {
        var requestPath = Path.Combine(root, "request.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, json));
        var start = new ProcessStartInfo(args[3]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add("extract-media"); start.ArgumentList.Add(requestPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Host did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            using var confirmation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(confirmation.Token);
            throw new TimeoutException("Actual host deadline exceeded; tree exit confirmed, artifacts retained.");
        }
        var output = await stdout; var error = await stderr;
        await File.WriteAllTextAsync(Path.Combine(root, "host.stdout.log"), output);
        await File.WriteAllTextAsync(Path.Combine(root, "host.stderr.log"), error);
        await File.WriteAllTextAsync(Path.Combine(root, "host.exit.txt"), process.ExitCode.ToString());
        if (process.ExitCode != 0 || output.Trim().Split('\n').Length != 1) throw new InvalidDataException($"Actual host failed: exit={process.ExitCode}; {error}");
        result = JsonSerializer.Deserialize<MediaDecodeResult>(output, json) ?? throw new InvalidDataException("Null host result.");
    }
    if (result.Protocol != 1 || result.SourcePath != source || result.Kind != "audio" || result.SourceDurationSeconds is not double duration || Math.Abs(duration - 4) > 0.1 || result.Artifacts.Count != 1)
        throw new InvalidDataException("EOF source/result metadata mismatch.");
    var artifact = result.Artifacts[0];
    if (artifact.StartSeconds != 3 || artifact.EndSeconds != 4 || artifact.SampleRate != 16000 || artifact.Channels != 1 || artifact.BitsPerSample != 16 || artifact.Modality != "audio" ||
        Path.IsPathRooted(artifact.RelativePath) || artifact.RelativePath.Contains(".."))
        throw new InvalidDataException("EOF interval/artifact metadata mismatch.");
    EofTailOracle.ValidateWithNegativeControls(Path.Combine(request.OutputDirectory, artifact.RelativePath), Path.Combine(root, route + "-mutations"));
    Console.WriteLine($"PASS {route} EOF true-endpoint marker + zero/copy/repeat800 + zero/copy/repeat31 + short31");
}
if (SyntheticMedia.Hash(source) != sourceHash) throw new InvalidDataException("Source mutated.");
Console.WriteLine($"PASS EOF source unchanged sha256={sourceHash}");
return 0;
