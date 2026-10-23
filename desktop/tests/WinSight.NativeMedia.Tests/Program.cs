using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinSight.InferenceHost;
using WinSight.NativeMedia.Tests;
using FileAttributes = System.IO.FileAttributes;

if (args.Length is not (2 or 4) || args[0] != "--artifact-root" || !Path.IsPathFullyQualified(args[1]) ||
    (args.Length == 4 && (args[2] != "--inference-host" || !Path.IsPathFullyQualified(args[3]) || !File.Exists(args[3]))))
{
    Console.Error.WriteLine("Usage: --artifact-root NEW_ABSOLUTE_TASK_DIRECTORY [--inference-host ABSOLUTE_BUILT_HOST_EXE]");
    return 2;
}
var suite = new NativeMediaSuite(Path.GetFullPath(args[1]), args.Length == 4 ? args[3] : null);
return await suite.RunAsync();

internal sealed class NativeMediaSuite(string root, string? host)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly List<object> results = [];
    private int failures;
    private int sequence;
    private string Fixtures => Path.Combine(root, "fixtures");
    private string Wave => Path.Combine(Fixtures, "levels-4s.wav");
    private string NewOutput() => Path.Combine(root, "outputs", $"case-{++sequence:D3}");
    private MediaDecodeRequest Audio(string? output = null) => new(Wave, output ?? NewOutput(), "audio", 1, 2);

    internal async Task<int> RunAsync()
    {
        if (Directory.Exists(root) || File.Exists(root)) throw new ArgumentException("Artifact root already exists; no existing task is reused.");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "outputs")); // Parent owns the task parent; decoder must exclusively create each leaf.
        Directory.SetCurrentDirectory(root);
        await Case("fixtures-real-WinRT", () => SyntheticMedia.CreateAsync(Fixtures));
        if (failures != 0) return await FinishAsync();
        var originals = Directory.GetFiles(Fixtures).ToDictionary(path => path,
            path => (Hash: SyntheticMedia.Hash(path), Modified: File.GetLastWriteTimeUtc(path), Length: new FileInfo(path).Length));

        await Case("audio-normalize-and-seek-1-to-2", async () =>
        {
            var request = Audio();
            var result = await NativeMediaCommand.ExecuteAsync(request, CancellationToken.None);
            var artifact = ValidateResult(result, request, 4);
            Check(artifact.SampleRate == 16000 && artifact.Channels == 1 && artifact.BitsPerSample == 16, "Audio metadata format");
            Check(artifact.StartSeconds == 1 && artifact.EndSeconds == 2, "Original audio timestamps");
            ValidateWave(ArtifactPath(request, artifact));
        });
        foreach (var second in new[] { 0, 3 })
            await Case($"audio-source-boundary-{second}-to-{second + 1}", async () =>
            {
                var request = new MediaDecodeRequest(Wave, NewOutput(), "audio", second, second + 1);
                var result = await NativeMediaCommand.ExecuteAsync(request, CancellationToken.None);
                var artifact = ValidateResult(result, request, 4);
                Check(artifact.StartSeconds == second && artifact.EndSeconds == second + 1, "Boundary interval source timestamps");
                ValidateWave(ArtifactPath(request, artifact), second);
            });
        await Case("audio-EOF-marker-through-endpoint-and-mutations", async () =>
        {
            var request = new MediaDecodeRequest(Path.Combine(Fixtures, "eof-marker-4s.wav"), NewOutput(), "audio", 3, 4);
            var artifact = ValidateResult(await NativeMediaCommand.ExecuteAsync(request), request, 4);
            Check(artifact.StartSeconds == 3 && artifact.EndSeconds == 4 && artifact.SampleRate == 16000 && artifact.Channels == 1 && artifact.BitsPerSample == 16, "EOF marker interval/format metadata");
            EofTailOracle.ValidateWithNegativeControls(ArtifactPath(request, artifact), Path.Combine(root, "eof-linked-mutations"));
        });
        foreach (var (name, width, height) in new[] { ("landscape.mp4", 256, 144), ("portrait.mp4", 144, 256) })
            await Case($"frame-{name}-bounds-aspect-seek", async () =>
            {
                var request = new MediaDecodeRequest(Path.Combine(Fixtures, name), NewOutput(), "frame", 1.25, null, 64);
                var result = await NativeMediaCommand.ExecuteAsync(request, CancellationToken.None);
                var artifact = ValidateResult(result, request, 3);
                Check(artifact.StartSeconds == 1.25 && artifact.EndSeconds is null, "Original frame timestamp");
                await ValidateFrameAsync(ArtifactPath(request, artifact), artifact, width, height);
            });
        await Case("frame-max-edge-1024-accepted", async () =>
        {
            var request = new MediaDecodeRequest(Path.Combine(Fixtures, "landscape.mp4"), NewOutput(), "frame", 1.25, null, 1024);
            var artifact = ValidateResult(await NativeMediaCommand.ExecuteAsync(request, CancellationToken.None), request, 3);
            await ValidateFrameAsync(ArtifactPath(request, artifact), artifact, 256, 144, 1024);
        });
        await Case("pre-cancel-no-output", async () =>
        {
            var request = Audio();
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var error = await RejectAsync(() => NativeMediaCommand.ExecuteAsync(request, cancel.Token));
            Check(error is OperationCanceledException, "Pre-cancel must report cancellation");
            Check(!Directory.Exists(request.OutputDirectory) && !File.Exists(request.OutputDirectory), "Pre-cancel created output");
        });
        await Case("existing-output-sentinel-untouched", async () =>
        {
            var request = Audio(); Directory.CreateDirectory(request.OutputDirectory);
            var sentinel = Path.Combine(request.OutputDirectory, "sentinel.bin");
            await File.WriteAllBytesAsync(sentinel, [1, 7, 9, 0, 255]);
            var hash = SyntheticMedia.Hash(sentinel); var modified = File.GetLastWriteTimeUtc(sentinel);
            await RejectAsync(() => NativeMediaCommand.ExecuteAsync(request, CancellationToken.None));
            Check(Directory.GetFileSystemEntries(request.OutputDirectory).Length == 1 && SyntheticMedia.Hash(sentinel) == hash &&
                File.GetLastWriteTimeUtc(sentinel) == modified, "Existing output changed");
        });
        var invalid = new (string Name, Func<MediaDecodeRequest, MediaDecodeRequest> Change)[]
        {
            ("empty-input", r => r with { InputPath = "" }), ("null-input", r => r with { InputPath = null! }),
            ("relative-input", r => r with { InputPath = "levels-4s.wav" }),
            ("missing-input", r => r with { InputPath = Path.Combine(Fixtures, "absent.wav") }),
            ("input-is-directory", r => r with { InputPath = Fixtures }),
            ("empty-output", r => r with { OutputDirectory = "" }), ("null-output", r => r with { OutputDirectory = null! }),
            ("relative-output", r => r with { OutputDirectory = "relative-output" }),
            ("output-is-file", r => r with { OutputDirectory = Wave }),
            ("same-input-and-output", r => r with { OutputDirectory = r.InputPath }),
            ("unknown-kind", r => r with { Kind = "other" }), ("null-kind", r => r with { Kind = null! }),
            ("nan-start", r => r with { StartSeconds = double.NaN }), ("infinite-start", r => r with { StartSeconds = double.PositiveInfinity }),
            ("negative-start", r => r with { StartSeconds = -1 }), ("nan-end", r => r with { EndSeconds = double.NaN }),
            ("infinite-end", r => r with { EndSeconds = double.PositiveInfinity }), ("negative-end", r => r with { EndSeconds = -1 }),
            ("empty-interval", r => r with { EndSeconds = 1 }), ("reversed-interval", r => r with { EndSeconds = 0.5 }),
            ("over-30-seconds", r => r with { StartSeconds = 0, EndSeconds = 30.01 }),
            ("audio-missing-end", r => r with { EndSeconds = null }),
            ("frame-has-end", r => r with { Kind = "frame" }),
            ("frame-edge-too-small", r => r with { Kind = "frame", EndSeconds = null, MaxEdge = 63 }),
            ("frame-edge-too-large", r => r with { Kind = "frame", EndSeconds = null, MaxEdge = 1025 }),
            ("device-input", r => r with { InputPath = @"\\.\NUL" }),
            ("device-output", r => r with { OutputDirectory = @"\\.\NUL" }),
            ("extended-device-input", r => r with { InputPath = @"\\?\GLOBALROOT\Device\Null" }),
            ("reserved-device-basename", r => r with { InputPath = Path.Combine(Fixtures, "NUL.wav") }),
            ("alternate-data-stream", r => r with { InputPath = Wave + ":hidden" })
        };
        foreach (var (name, change) in invalid)
            await Case("reject-" + name, async () =>
            {
                var original = Audio(); var request = change(original);
                await RejectAsync(() => NativeMediaCommand.ExecuteAsync(request, CancellationToken.None));
                Check(!Directory.Exists(original.OutputDirectory), "Validation wrote output before rejection");
                Check(!Directory.Exists(Path.Combine(Environment.CurrentDirectory, "relative-output")), "Relative output escaped task root");
            });
        foreach (var (name, start, end) in new[] { ("past-end", 5d, 6d), ("straddles-end", 3.5, 4.5) })
            await Case("reject-audio-" + name, async () =>
                await RejectAsync(() => NativeMediaCommand.ExecuteAsync(Audio() with { StartSeconds = start, EndSeconds = end }, CancellationToken.None)));
        await Case("reject-frame-past-end", async () =>
            await RejectAsync(() => NativeMediaCommand.ExecuteAsync(new MediaDecodeRequest(Path.Combine(Fixtures, "landscape.mp4"), NewOutput(), "frame", 5), CancellationToken.None)));
        foreach (var name in new[] { "corrupt.wav", "unsupported.xyz" })
            await Case("reject-" + name, async () =>
            {
                var path = Path.Combine(Fixtures, name);
                await File.WriteAllTextAsync(path, "not a media container; synthetic negative fixture");
                await RejectAsync(() => NativeMediaCommand.ExecuteAsync(Audio() with { InputPath = path }, CancellationToken.None));
            });
        await Case("reject-corrupt-frame", async () =>
            await RejectAsync(() => NativeMediaCommand.ExecuteAsync(new MediaDecodeRequest(Path.Combine(Fixtures, "corrupt.wav"), NewOutput(), "frame", 0), CancellationToken.None)));
        await Case("reject-unsupported-wave-codec", async () =>
        {
            var path = Path.Combine(Fixtures, "unsupported-codec.wav");
            var bytes = await File.ReadAllBytesAsync(Wave);
            bytes[20] = 0xff; bytes[21] = 0x7f;
            await File.WriteAllBytesAsync(path, bytes);
            await RejectAsync(() => NativeMediaCommand.ExecuteAsync(Audio() with { InputPath = path }, CancellationToken.None));
        });
        await JsonCasesAsync();
        await DriveClassifierCases.RunAsync(root, Wave, Case);
        await ReparseCasesAsync();
        if (host is null) Note("cli", "NOT RUN: explicit actual inference-host exe was not supplied; integration gate remains pending.");
        else await CliCasesAsync();
        await Case("all-original-sources-unchanged", () =>
        {
            foreach (var (path, before) in originals)
                Check(SyntheticMedia.Hash(path) == before.Hash && File.GetLastWriteTimeUtc(path) == before.Modified && new FileInfo(path).Length == before.Length,
                    $"Original source changed: {Path.GetFileName(path)}");
            return Task.CompletedTask;
        });
        return await FinishAsync();
    }

    private async Task JsonCasesAsync()
    {
        var request = Audio();
        var good = JsonSerializer.SerializeToElement(request, Json);
        var members = good.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        var payloads = new List<(string Name, string Text)>
        {
            ("malformed", "{"), ("null-root", "null"), ("array-root", "[]"),
            ("oversize", JsonSerializer.Serialize(request, Json) + new string(' ', 65537)),
            ("unknown", JsonSerializer.Serialize(members.Concat(new[] { new KeyValuePair<string, object?>("surprise", true) }).ToDictionary(), Json))
        };
        foreach (var member in new[] { "inputPath", "outputDirectory", "kind", "startSeconds" })
        {
            payloads.Add(("missing-" + member, JsonSerializer.Serialize(members.Where(p => p.Key != member).ToDictionary(), Json)));
            var copy = new Dictionary<string, object?>(members) { [member] = null };
            payloads.Add(("null-" + member, JsonSerializer.Serialize(copy, Json)));
        }
        foreach (var member in new[] { "startSeconds", "endSeconds", "maxEdge" })
        {
            var copy = new Dictionary<string, object?>(members) { [member] = "not-a-number" };
            payloads.Add(("bad-" + member, JsonSerializer.Serialize(copy, Json)));
        }
        foreach (var (name, text) in payloads)
            await Case("json-reject-" + name, async () =>
            {
                var path = await WriteRequestAsync(name, text);
                await RejectAsync(() => NativeMediaCommand.RunRequestFileAsync(path, CancellationToken.None));
                Check(!Directory.Exists(request.OutputDirectory), "Invalid JSON created output");
            });
        await Case("json-relative-request-path", async () =>
            await RejectAsync(() => NativeMediaCommand.RunRequestFileAsync("relative-request.json", CancellationToken.None)));
        await Case("json-device-request-path", async () =>
            await RejectAsync(() => NativeMediaCommand.RunRequestFileAsync(@"\\.\NUL", CancellationToken.None)));
        await Case("json-frame-optional-defaults", async () =>
        {
            var frame = new MediaDecodeRequest(Path.Combine(Fixtures, "landscape.mp4"), NewOutput(), "frame", 1.25);
            var text = JsonSerializer.Serialize(new { frame.InputPath, frame.OutputDirectory, frame.Kind, frame.StartSeconds }, Json);
            var path = await WriteRequestAsync("frame-defaults", text);
            var artifact = ValidateResult(await NativeMediaCommand.RunRequestFileAsync(path, CancellationToken.None), frame, 3);
            await ValidateFrameAsync(ArtifactPath(frame, artifact), artifact, 256, 144, 512);
        });
        foreach (var (name, value) in new[] { ("null", "null"), ("string", "\"1\""), ("boolean", "true"), ("object", "{}"), ("array", "[]"), ("overflow", "1e400"), ("negative", "-1"), ("number", "1") })
            await Case("json-frame-reject-supplied-end-" + name, async () =>
            {
                var frame = new MediaDecodeRequest(Path.Combine(Fixtures, "landscape.mp4"), NewOutput(), "frame", 1.25, null, 64);
                var text = JsonSerializer.Serialize(new { frame.InputPath, frame.OutputDirectory, frame.Kind, frame.StartSeconds, frame.MaxEdge }, Json);
                text = text[..^1] + ",\"endSeconds\":" + value + "}";
                var path = await WriteRequestAsync("frame-end-" + name, text);
                await RejectAsync(() => NativeMediaCommand.RunRequestFileAsync(path));
                Check(!Directory.Exists(frame.OutputDirectory) && !File.Exists(frame.OutputDirectory), "Supplied invalid frame end created output");
            });
        await Case("json-precancel-no-output", async () =>
        {
            var path = await WriteRequestAsync("precancel", JsonSerializer.Serialize(request, Json));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var error = await RejectAsync(() => NativeMediaCommand.RunRequestFileAsync(path, cancel.Token));
            Check(error is OperationCanceledException && !Directory.Exists(request.OutputDirectory), "Request-file pre-cancel");
        });
        await Case("json-success-real-decode", async () =>
        {
            var path = await WriteRequestAsync("success", JsonSerializer.Serialize(request, Json));
            var artifact = ValidateResult(await NativeMediaCommand.RunRequestFileAsync(path, CancellationToken.None), request, 4);
            ValidateWave(ArtifactPath(request, artifact));
        });
    }

    private async Task ReparseCasesAsync()
    {
        var link = Path.Combine(root, "source-link.wav");
        try { File.CreateSymbolicLink(link, Wave); }
        catch (Exception error) when ((error is UnauthorizedAccessException || error is IOException) && (error.HResult & 0xffff) is 5 or 1314)
        {
            Note("reparse", $"NOT RUN: OS refused task-local symbolic link, {error.GetType().Name}, 0x{error.HResult:X8}; no elevation attempted.");
            return;
        }
        await Case("reject-reparse-input", async () =>
        {
            var request = Audio() with { InputPath = link };
            await RejectAsync(() => NativeMediaCommand.ExecuteAsync(request, CancellationToken.None));
            Check(!Directory.Exists(request.OutputDirectory), "Reparse input created output");
        });
        var sourceParent = Path.Combine(root, "source-parent-link"); Directory.CreateSymbolicLink(sourceParent, Fixtures);
        await Case("reject-reparse-input-ancestor", async () =>
        {
            var request = Audio() with { InputPath = Path.Combine(sourceParent, "levels-4s.wav") };
            await RejectAsync(() => NativeMediaCommand.ExecuteAsync(request, CancellationToken.None));
            Check(!Directory.Exists(request.OutputDirectory), "Reparse source ancestor created output");
        });
        var parent = Path.Combine(root, "output-link");
        var target = Path.Combine(root, "link-target"); Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(parent, target);
        await Case("reject-reparse-output-ancestor", async () =>
        {
            await RejectAsync(() => NativeMediaCommand.ExecuteAsync(Audio(Path.Combine(parent, "new-output")), CancellationToken.None));
            Check(Directory.GetFileSystemEntries(target).Length == 0, "Output escaped through reparse ancestor");
        });
        var requestTarget = await WriteRequestAsync("link-target", JsonSerializer.Serialize(Audio(), Json));
        var requestLink = Path.Combine(root, "request-link.json"); File.CreateSymbolicLink(requestLink, requestTarget);
        await Case("reject-reparse-request-file", async () =>
            await RejectAsync(() => NativeMediaCommand.RunRequestFileAsync(requestLink, CancellationToken.None)));
    }

    private async Task CliCasesAsync()
    {
        await Case("cli-model-free-audio-success", async () =>
        {
            var request = Audio(); var path = await WriteRequestAsync("cli-success", JsonSerializer.Serialize(request, Json));
            var (exit, stdout, stderr) = await RunHostAsync("success", ["extract-media", path]);
            Check(exit == 0, $"CLI success exit {exit}: {stderr}");
            Check(stdout.Trim().Split('\n').Length == 1, "CLI success must be one JSON line");
            using var document = JsonDocument.Parse(stdout);
            Check(document.RootElement.TryGetProperty("sourcePath", out _) && !document.RootElement.TryGetProperty("SourcePath", out _), "CLI camelCase");
            var result = JsonSerializer.Deserialize<MediaDecodeResult>(stdout, Json) ?? throw new InvalidDataException("CLI null result");
            ValidateWave(ArtifactPath(request, ValidateResult(result, request, 4)));
        });
        await Case("cli-EOF-marker-through-endpoint-and-mutations", async () =>
        {
            var request = new MediaDecodeRequest(Path.Combine(Fixtures, "eof-marker-4s.wav"), NewOutput(), "audio", 3, 4);
            var path = await WriteRequestAsync("cli-eof-marker", JsonSerializer.Serialize(request, Json));
            var (exit, stdout, stderr) = await RunHostAsync("eof-marker", ["extract-media", path]);
            Check(exit == 0 && stdout.Trim().Split('\n').Length == 1, $"EOF host failed: exit={exit}, stderr={stderr}");
            var result = JsonSerializer.Deserialize<MediaDecodeResult>(stdout, Json) ?? throw new InvalidDataException("CLI null EOF result");
            var artifact = ValidateResult(result, request, 4);
            Check(artifact.StartSeconds == 3 && artifact.EndSeconds == 4 && artifact.SampleRate == 16000 && artifact.Channels == 1 && artifact.BitsPerSample == 16, "CLI EOF interval/format metadata");
            EofTailOracle.ValidateWithNegativeControls(ArtifactPath(request, artifact), Path.Combine(root, "eof-cli-mutations"));
        });
        foreach (var suppliedNull in new[] { false, true })
            await Case(suppliedNull ? "cli-frame-null-end-rejected-before-output" : "cli-frame-omitted-end-valid", async () =>
            {
                var frame = new MediaDecodeRequest(Path.Combine(Fixtures, "landscape.mp4"), NewOutput(), "frame", 1.25, null, 64);
                var text = JsonSerializer.Serialize(new { frame.InputPath, frame.OutputDirectory, frame.Kind, frame.StartSeconds, frame.MaxEdge }, Json);
                if (suppliedNull) text = text[..^1] + ",\"endSeconds\":null}";
                var path = await WriteRequestAsync(suppliedNull ? "cli-frame-null" : "cli-frame-omitted", text);
                var (exit, stdout, stderr) = await RunHostAsync(suppliedNull ? "frame-null" : "frame-omitted", ["extract-media", path]);
                if (suppliedNull)
                    Check(exit != 0 && !string.IsNullOrWhiteSpace(stderr) && string.IsNullOrWhiteSpace(stdout) && !Directory.Exists(frame.OutputDirectory) && !File.Exists(frame.OutputDirectory), "Supplied null must fail before output, not produce a frame");
                else
                {
                    Check(exit == 0 && stdout.Trim().Split('\n').Length == 1, $"Omitted frame end failed: {stderr}");
                    var result = JsonSerializer.Deserialize<MediaDecodeResult>(stdout, Json) ?? throw new InvalidDataException("CLI null frame result");
                    var artifact = ValidateResult(result, frame, 3);
                    Check(artifact.StartSeconds == 1.25 && artifact.EndSeconds is null, "CLI omitted frame end metadata");
                    await ValidateFrameAsync(ArtifactPath(frame, artifact), artifact, 256, 144);
                }
            });
        await Case("cli-malformed-json-fails", async () =>
        {
            var path = await WriteRequestAsync("cli-malformed", "{");
            var (exit, stdout, stderr) = await RunHostAsync("malformed", ["extract-media", path]);
            Check(exit != 0 && !string.IsNullOrWhiteSpace(stderr) && string.IsNullOrWhiteSpace(stdout), "Malformed JSON CLI must fail, not emit success");
        });
        var legacy = new (string Name, string[] Arguments, string Error)[]
        {
            ("no-arguments", [], "Usage:"), ("incomplete-inspect", ["inspect"], "Usage:"),
            ("incomplete-embed", ["embed-tokens"], "Usage:"), ("stdio-missing-required", ["--stdio"], "Usage:"),
            ("stdio-unknown", ["--stdio", "--unknown"], "Unknown or incomplete host option:"),
            ("stdio-invalid-device", ["--stdio", "--models", Path.Combine(root, "no-models"), "--manifest", Path.Combine(root, "no-manifest.json"), "--device", "invalid"], "device must be auto or cpu")
        };
        foreach (var (name, arguments, error) in legacy)
            await Case("cli-legacy-" + name, async () =>
            {
                var (exit, stdout, stderr) = await RunHostAsync(name, arguments);
                Check(exit != 0 && stderr.Contains(error, StringComparison.Ordinal), "Legacy parsing/error changed");
                if (arguments.Contains("--stdio"))
                {
                    using var ready = JsonDocument.Parse(stdout);
                    Check(!ready.RootElement.GetProperty("ready").GetBoolean() && ready.RootElement.GetProperty("protocol").GetInt32() == 1, "Legacy stdio failure handshake changed");
                }
                else Check(string.IsNullOrWhiteSpace(stdout), "Legacy invalid command emitted success");
            });
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunHostAsync(string name, string[] arguments)
    {
        var info = new ProcessStartInfo(host!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start actual host");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000)) throw new TimeoutException("CLI process exit not confirmed; retain all task artifacts.");
            throw new TimeoutException("Actual CLI exceeded external process deadline and was killed; not native token cancellation.");
        }
        var output = await stdout; var error = await stderr;
        var logRoot = Path.Combine(root, "cli"); Directory.CreateDirectory(logRoot);
        await File.WriteAllTextAsync(Path.Combine(logRoot, name + ".stdout.log"), output);
        await File.WriteAllTextAsync(Path.Combine(logRoot, name + ".stderr.log"), error);
        await File.WriteAllTextAsync(Path.Combine(logRoot, name + ".exit.txt"), process.ExitCode.ToString());
        return (process.ExitCode, output, error);
    }

    private static MediaDecodeArtifact ValidateResult(MediaDecodeResult result, MediaDecodeRequest request, double duration)
    {
        Check(result.Protocol == 1 && result.SourcePath == request.InputPath && result.Kind == request.Kind, "Result identity/protocol");
        Check(result.SourceDurationSeconds is double seconds && Math.Abs(seconds - duration) <= 0.1, "Original source duration");
        Check(result.Artifacts.Count == 1, "Exactly one artifact required");
        var artifact = result.Artifacts[0];
        Check(artifact.Modality == (request.Kind == "frame" ? "image" : "audio"), "Artifact modality");
        var path = ArtifactPath(request, artifact);
        Check(File.Exists(path) && new FileInfo(path).Length > 0, "Artifact missing/empty");
        Check(Directory.GetFiles(request.OutputDirectory, "*", SearchOption.AllDirectories).Length == 1, "Unexpected extra output files");
        return artifact;
    }

    private static string ArtifactPath(MediaDecodeRequest request, MediaDecodeArtifact artifact)
    {
        Check(!string.IsNullOrWhiteSpace(artifact.RelativePath) && !Path.IsPathRooted(artifact.RelativePath), "Artifact must be a relative path");
        Check(!artifact.RelativePath.Contains(':') && !artifact.RelativePath.Split(['/', '\\']).Any(p => p is ".." or "."), "Artifact traversal/device path");
        var parent = Path.GetFullPath(request.OutputDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(request.OutputDirectory, artifact.RelativePath));
        Check(path.StartsWith(parent, StringComparison.OrdinalIgnoreCase), "Artifact escaped new output root");
        var current = new FileInfo(path) as FileSystemInfo;
        while (current is not null && current.FullName.Length >= parent.TrimEnd(Path.DirectorySeparatorChar).Length)
        {
            Check((current.Attributes & FileAttributes.ReparsePoint) == 0, "Artifact uses reparse path");
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        return path;
    }

    private static void ValidateWave(string path, int sourceSecond = 1)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        Check(Encoding.ASCII.GetString(reader.ReadBytes(4)) == "RIFF", "WAV RIFF header");
        var riffBytes = reader.ReadUInt32();
        Check(riffBytes + 8L == reader.BaseStream.Length && Encoding.ASCII.GetString(reader.ReadBytes(4)) == "WAVE", "WAV container length/type");
        bool format = false; byte[]? pcm = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4)); var size = reader.ReadUInt32(); var start = reader.BaseStream.Position;
            Check(size <= reader.BaseStream.Length - start, "Truncated WAV chunk");
            if (id == "fmt ")
            {
                Check(size >= 16, "WAV format chunk too small");
                var tag = reader.ReadUInt16(); var channels = reader.ReadUInt16(); var rate = reader.ReadUInt32();
                var bytesPerSecond = reader.ReadUInt32(); var align = reader.ReadUInt16(); var bits = reader.ReadUInt16();
                Check(channels == 1 && rate == 16000 && bits == 16 && align == 2 && bytesPerSecond == 32000, "Actual WAV is not PCM16/16k/mono");
                if (tag == 0xfffe)
                {
                    Check(size >= 40 && reader.ReadUInt16() >= 22 && reader.ReadUInt16() == 16, "Extensible PCM format");
                    _ = reader.ReadUInt32();
                    Check(new Guid(reader.ReadBytes(16)) == new Guid("00000001-0000-0010-8000-00aa00389b71"), "Extensible format is not PCM");
                }
                else Check(tag == 1, "WAV encoding is not PCM");
                format = true;
            }
            if (id == "data") { Check(pcm is null, "Multiple WAV data chunks"); pcm = reader.ReadBytes(checked((int)size)); }
            reader.BaseStream.Position = start + size + (size & 1);
        }
        Check(format && pcm is { Length: 32000 }, "Requested 1s must contain exactly 16000 physical frames, not shortened output");
        var samples = MemoryMarshal.Cast<byte, short>(pcm!);
        double sum = 0;
        for (var i = 800; i < 15200; i++) sum += samples[i];
        var expectedLevel = SyntheticMedia.Levels[sourceSecond];
        Check(Math.Abs(sum / 14400 - expectedLevel) < 200, "Wrong audio window: physical samples must match the requested source second");
        Check(samples[8000] > expectedLevel - 500 && samples[8000] < expectedLevel + 500, "Seek marker at middle of actual PCM");
        Console.WriteLine($"OBSERVED audio frames=16000 rate=16000 channels=1 bits=16 start={sourceSecond} end={sourceSecond + 1} level={sum / 14400}");
    }

    private static async Task ValidateFrameAsync(string path, MediaDecodeArtifact artifact, int sourceWidth, int sourceHeight, int maxEdge = 64)
    {
        using var stream = await (await StorageFile.GetFileFromPathAsync(path)).OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var width = decoder.PixelWidth; var height = decoder.PixelHeight;
        Check(width > 0 && height > 0 && width <= maxEdge && height <= maxEdge, "Actual image exceeds either max edge");
        Check(artifact.Width == width && artifact.Height == height, "Frame metadata differs from decoded pixels");
        Check(Math.Abs((long)width * sourceHeight - (long)height * sourceWidth) <= Math.Max(sourceWidth, sourceHeight), "Actual image aspect ratio distorted");
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var offset = checked((int)(((height / 2) * width + width / 2) * 4));
        var expected = SyntheticMedia.Colors[1];
        Check(Math.Abs(pixels[offset] - expected.B) < 35 && Math.Abs(pixels[offset + 1] - expected.G) < 35 && Math.Abs(pixels[offset + 2] - expected.R) < 35,
            "Wrong video window/color: must sample second scene at original 1.25 seconds");
        Console.WriteLine($"OBSERVED frame pixels={width}x{height} source={sourceWidth}x{sourceHeight} start=1.25 maxEdge={maxEdge}");
    }

    private async Task<string> WriteRequestAsync(string name, string json)
    {
        var directory = Path.Combine(root, "requests"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".json"); await File.WriteAllTextAsync(path, json); return path;
    }
    private static async Task<Exception> RejectAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or NotSupportedException or JsonException or OperationCanceledException or COMException or UnauthorizedAccessException or InvalidOperationException)
        { return error; }
        throw new InvalidOperationException("Expected explicit failure, but operation returned success.");
    }
    private async Task Case(string name, Func<Task> operation)
    {
        var watch = Stopwatch.StartNew();
        try { await operation(); results.Add(new { name, status = "passed", milliseconds = watch.ElapsedMilliseconds }); Console.WriteLine("PASS " + name); }
        catch (Exception error)
        {
            failures++; var id = "NM-" + name;
            results.Add(new { name, status = "failed", defect = id, type = error.GetType().FullName, error = error.Message, hresult = $"0x{error.HResult:X8}", stack = error.StackTrace });
            Console.Error.WriteLine($"FAIL {id}: {error}");
        }
    }
    private void Note(string name, string detail) { results.Add(new { name, status = "not-run", detail }); Console.WriteLine(detail); }
    private async Task<int> FinishAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { failures, cliRequested = host is not null, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Native media suite failures={failures}; CLI={(host is null ? "NOT RUN" : "requested")}; artifacts={root}");
        return failures == 0 ? 0 : 1;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
