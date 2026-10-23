using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinSight.MediaBackend.Tests;

try
{
    var config = Evidence.Read<HostConfig>(Path.Combine(AppContext.BaseDirectory, "composite.json"));
    Evidence.Under(config.Evidence, config.Root);
    if (args is ["--owned-leaf"])
    {
        Evidence.Pid(config, Evidence.Current(), "leaf"); await Task.Delay(Timeout.Infinite); return 0;
    }
    Evidence.Assert(args is ["--stdio", "--models", _, "--manifest", _, "--device", "cpu"]
        || args.Length == 2 && args[0] is "inspect-media" or "extract-media" or "extract-soundtrack", "Closed composite CLI required");
    Evidence.Pid(config, Evidence.Current(), args[0] == "--stdio" ? "model" : "helper");
    Evidence.Event(config, new { phase = "start", executable = Environment.ProcessPath, args, identity = Evidence.Current() });
    if (args[0] == "--stdio") return await Stdio(config, args);
    return await Media(config, args);
}
catch (Exception e) { Console.Error.WriteLine(e); return 65; }

static void Emit(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
static object Info() => new {
    model = "onnx-community/embeddinggemma-2-ONNX", revision = "daa72c51243991dfcaf9f9137d2c573d8f7790c0",
    runtime = "windows-ml-ort-1.24.3", dtype = "q4", dim = 768, text_only = false, profile_id = new string('a',64),
    validation = "synthetic-model-free-composite", requested_provider = "CPUExecutionProvider", provider_downloads_allowed = false,
    sessions_loaded = new { text = false, image = false, audio = false }
};
static object Caps(HostConfig c) => new { text = true, image = true, audio = c.Mode != "no-audio" };
static async Task<int> Stdio(HostConfig c, string[] args)
{
    string models = Evidence.Under(args[2], c.Root), manifest = Evidence.Under(args[4], c.Root);
    // These small test-only assets are never presented to the real ModelManifest/ONNX loader.
    Evidence.Assert(File.Exists(Path.Combine(models, "synthetic-weights.placeholder")), "Synthetic placeholder required");
    using (var doc = JsonDocument.Parse(File.ReadAllBytes(manifest))) Evidence.Assert(doc.RootElement.GetProperty("syntheticOnly").GetBoolean(), "Test manifest required");
    Emit(new { ready = true, protocol = 1, info = Info(), capabilities = Caps(c) });
    int mediaCount = 0;
    while (await Console.In.ReadLineAsync() is { } line)
    {
        Evidence.Assert(Encoding.UTF8.GetByteCount(line) <= 1024 * 1024, "Bounded JSONL");
        using var doc = JsonDocument.Parse(line); var r = doc.RootElement; long id = r.GetProperty("id").GetInt64(); string op = r.GetProperty("op").GetString()!;
        if (op == "quit") { Emit(new { id, ok = true }); return 0; }
        if (op == "info") { Emit(new { id, ok = true, protocol = 1, info = Info(), capabilities = Caps(c) }); continue; }
        Evidence.Assert(op is "audio" or "image" or "text", "Closed embedding operation");
        string[] inputs = r.GetProperty("inputs").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Evidence.Assert(inputs.Length is > 0 and <= 32 && r.GetProperty("budget").GetInt32() is >= 1 and <= 1120, "Real batching contract");
        foreach (string input in inputs) if (op != "text") { Evidence.Under(input, c.Root); Evidence.Assert(File.Exists(input), "Consumer lease missing before embedding"); }
        Evidence.Event(c, new { phase = "embedding", op, inputs, pid = Environment.ProcessId });
        if (op != "text") mediaCount++;
        if (c.Mode == "hold-embedding" && op != "text")
        {
            using var leaf = await Leaf(c);
            Evidence.Write(Path.Combine(c.Evidence, "embedding-held.json"), new { input = inputs[0], identity = Evidence.Current(), leaf = new Identity(leaf.Id, leaf.StartTime.ToUniversalTime().Ticks) });
            await Task.Delay(Timeout.Infinite);
        }
        if (c.Mode is "dependency" or "dependency-retain" && op != "text")
        { Emit(new { id, ok = false, error = "SYNTHETIC_UNTRUSTED " + inputs[0] + " media-tasks RAW_CANARY" }); continue; }
        bool itemError = c.Mode == "item-first" && op != "text" || c.Mode == "item-later" && mediaCount > 1;
        byte[] bytes = new byte[inputs.Length * 768 * 4];
        if (!itemError) for (int i = 0; i < inputs.Length; i++) { BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 768 * 4), .6f); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 768 * 4 + 4), .8f); }
        Emit(new { id, ok = !itemError, dim = 768, count = inputs.Length, vectors = Convert.ToBase64String(bytes),
            errors = inputs.Select(p => itemError ? "SYNTHETIC_UNTRUSTED " + p + " RAW_CANARY" : null).ToArray(), performance_profile_id = (string?)null });
    }
    return 0;
}
static async Task<string> ReadBounded(Stream stream, int limit, CancellationToken token)
{
    using var bytes = new MemoryStream(limit);
    var buffer = new byte[4096];
    while (true)
    {
        int count = await stream.ReadAsync(buffer.AsMemory(), token);
        if (count == 0) break;
        if (count > limit - bytes.Length) throw new InvalidDataException("Owned decoder output exceeds 65536 bytes.");
        bytes.Write(buffer, 0, count);
    }
    return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
}

static async Task<Process> Leaf(HostConfig c)
{
    // Deliberate owned descendant: inherits the production client's Job. Never substitutes for production launch.
    var s = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }; s.ArgumentList.Add("--owned-leaf");
    var leaf = Process.Start(s) ?? throw new IOException("Leaf launch failed");
    Evidence.Pid(c, new(leaf.Id, leaf.StartTime.ToUniversalTime().Ticks), "leaf-owner");
    string receipt = Path.Combine(c.Evidence, leaf.Id + ".leaf.pid.json");
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!File.Exists(receipt)) { stop.Token.ThrowIfCancellationRequested(); await Task.Delay(10, stop.Token); }
    return leaf;
}
static async Task<int> Media(HostConfig c, string[] args)
{
    string requestPath = Evidence.Under(args[1], c.Root); byte[] bytes = File.ReadAllBytes(requestPath);
    Evidence.Assert(bytes.Length <= 65536, "Native request bound"); using var doc = JsonDocument.Parse(new UTF8Encoding(false,true).GetString(bytes)); var r = doc.RootElement;
    bool inspect = args[0] == "inspect-media"; string kind = r.GetProperty("kind").GetString()!;
    string[] expected = inspect ? ["inputPath", "kind"] : kind == "frame" ? ["inputPath", "outputDirectory", "kind", "startSeconds", "maxEdge"] : ["inputPath", "outputDirectory", "kind", "startSeconds", "endSeconds"];
    // MediaTaskFiles omits null maxEdge/endSeconds; audio is allowed a bounded nonnull maxEdge too.
    var fields = r.EnumerateObject().Select(x => x.Name).ToArray();
    Evidence.Assert(fields.Distinct().Count() == fields.Length && fields.Except(expected.Append("maxEdge")).Count() == 0 && expected.All(fields.Contains), "Real native request shape");
    string source = Evidence.Under(r.GetProperty("inputPath").GetString()!, c.Root);
    Evidence.Assert(File.Exists(source) && kind is "audio" or "video" or "frame", "Task media source required");
    var fixture = Evidence.Read<MediaFixture>(source + ".fixture.synthetic");
    Evidence.Event(c, new { phase = "media", operation = args[0], requestPath, request = r.Clone(), identity = Evidence.Current() });
    if (c.NativeHost is not null)
    {
        // The wrapper already runs in N2's Job; this exact approved decoder inherits that Job.
        var s = new ProcessStartInfo(c.NativeHost) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) s.ArgumentList.Add(a);
        using var p = Process.Start(s) ?? throw new IOException("Approved decoder launch failed"); Evidence.Pid(c, new(p.Id,p.StartTime.ToUniversalTime().Ticks), "native");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = ReadBounded(p.StandardOutput.BaseStream, 65536, timeout.Token);
        var error = ReadBounded(p.StandardError.BaseStream, 65536, timeout.Token);
        var exited = p.WaitForExitAsync(timeout.Token);
        try
        {
            // Observe a failed reader immediately: waiting only for exit can deadlock a full pipe.
            var pending = new HashSet<Task> { output, error, exited };
            while (pending.Count != 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(timeout.Token);
                await completed; pending.Remove(completed);
            }
            Console.Write(await output); Console.Error.Write(await error); return p.ExitCode;
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            try
            {
                timeout.Cancel();
                if (!p.HasExited) p.Kill(entireProcessTree: true); // This exact owned decoder and its descendants only.
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception cleanup) { failures.Add(cleanup); }
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception drain) when (ReferenceEquals(drain, failure) || drain is OperationCanceledException) { }
            catch (Exception drain) { failures.Add(drain); }
            throw new AggregateException("Owned decoder forwarding failed; bounded exit/drain attempted.", failures);
        }
    }
    if (inspect)
    {
        Emit(new { protocol = 1, sourcePath = source, kind, durationSeconds = fixture.Duration, hasAudio = kind == "audio" || fixture.HasAudio,
            audioTrackCount = kind == "video" ? fixture.HasAudio ? (int?)1 : 0 : null, width = kind == "video" ? 8 : 0, height = kind == "video" ? 8 : 0 }); return 0;
    }
    string outputDir = Evidence.Under(r.GetProperty("outputDirectory").GetString()!, c.Root);
    double start = r.GetProperty("startSeconds").GetDouble(); double? end = kind == "frame" ? null : r.GetProperty("endSeconds").GetDouble();
    Evidence.Assert(!Directory.Exists(outputDir) && start >= 0 && start < fixture.Duration && (end is null || end > start && end <= fixture.Duration && end - start <= 30), "Bounded source interval/new output");
    if (fixture.Mode is "hold-helper" or "retain-helper")
    {
        if (fixture.Mode == "retain-helper") { Directory.CreateDirectory(outputDir); File.WriteAllText(Path.Combine(outputDir,"unknown.synthetic"),"Deliberate owned unknown file: never remove to make pause pass"); }
        using var leaf = await Leaf(c);
        Evidence.Write(Path.Combine(c.Evidence,"helper-held.json"), new { source, requestPath, outputDir, identity = Evidence.Current(), leaf = new Identity(leaf.Id,leaf.StartTime.ToUniversalTime().Ticks) });
        await Task.Delay(Timeout.Infinite);
    }
    if (fixture.Mode == "decode-first" || fixture.Mode == "decode-later" && start > 0 || fixture.Mode == "soundtrack-fail" && args[0] == "extract-soundtrack")
    { Console.Error.WriteLine("SYNTHETIC_UNTRUSTED " + requestPath + " " + outputDir + " RAW_CANARY"); return 27; }
    Directory.CreateDirectory(outputDir); bool audio = kind == "audio"; string name = audio ? "audio.wav" : "frame.png";
    if (audio) MediaBytes.Wave(Path.Combine(outputDir,name), end!.Value-start,start); else MediaBytes.Png(Path.Combine(outputDir,name), start >= 3);
    if (c.Mode == "dependency-retain") File.WriteAllText(Path.Combine(outputDir,"unknown.synthetic"),"Task-only retention oracle; do not delete");
    Emit(new { protocol = 1, sourcePath = source, kind, sourceDurationSeconds = fixture.Duration,
        artifacts = new[] { new { relativePath = name, modality = audio ? "audio" : "image", startSeconds = start, endSeconds = end,
            sampleRate = audio ? (int?)16000 : null, channels = audio ? (int?)1 : null, bitsPerSample = audio ? (int?)16 : null,
            width = audio ? null : (int?)8, height = audio ? null : (int?)8 } } }); return 0;
}
