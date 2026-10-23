using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinSight.MediaIndexing.Tests;

if (args.Length != 2 || args[0] is not ("inspect-media" or "extract-media" or "extract-soundtrack") || !Path.IsPathFullyQualified(args[1])) return 64;
try
{
    byte[] bytes = File.ReadAllBytes(args[1]); Check.That(bytes.Length <= 65536, "Request over 64KiB");
    using var doc = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes)); var r = doc.RootElement;
    bool inspect = args[0] == "inspect-media";
    string kind = r.GetProperty("kind").GetString()!;
    // N1 requires audio endSeconds, but requires frame endSeconds to be OMITTED (not null).
    string[] fields = inspect ? ["inputPath", "kind"] : kind == "frame"
        ? ["inputPath", "outputDirectory", "kind", "startSeconds", "maxEdge"]
        : ["inputPath", "outputDirectory", "kind", "startSeconds", "endSeconds", "maxEdge"];
    var names = r.EnumerateObject().Select(p => p.Name).ToArray();
    Check.That(names.Length == fields.Length && names.Distinct(StringComparer.Ordinal).Count() == fields.Length && !names.Except(fields).Any(), "Exact request fields required");
    string source = r.GetProperty("inputPath").GetString()!;
    Check.That(Path.IsPathFullyQualified(source) && File.Exists(source) && kind is "audio" or "video" or "frame", "Bad source/kind");
    source = Path.GetFullPath(source);
    string scenarioPath = File.Exists(source + ".fixture.json") ? source + ".fixture.json" : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(source))!, "fixture.json");
    var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(scenarioPath)) ?? throw new InvalidDataException("Missing test-only scenario");
    Check.That(double.IsFinite(scenario.Duration) && scenario.Duration > 0, "Bad duration"); Owned.Record(source);
    string logDirectory = Path.Combine(Path.GetDirectoryName(source)!, "q"); Directory.CreateDirectory(logDirectory);
    string log = Path.Combine(logDirectory, Environment.ProcessId + ".json");
    Owned.Atomic(log, new { operation = args[0], request = r.Clone() });
    if (scenario.Mode == "hang" && !inspect) await Task.Delay(Timeout.Infinite);
    if (!inspect && (scenario.Mode == "echo-decode-first" || scenario.Mode == "echo-decode-later" && r.GetProperty("startSeconds").GetDouble() > 0))
    {
        string output = r.GetProperty("outputDirectory").GetString()!;
        // The source's immediate parent is pinned against rename; publish atomically in a fresh evidence child.
        string evidence = Path.Combine(Path.GetDirectoryName(source)!, "e"); Directory.CreateDirectory(evidence);
        Owned.Atomic(Path.Combine(evidence, Path.GetFileName(source) + ".echo.json"), new { requestPath = args[1], outputDirectory = output });
        Console.Error.WriteLine("UNTRUSTED_ECHO decode request=" + args[1] + " output=" + output + " task=" + Path.GetDirectoryName(args[1]) + " workspace=" + Path.GetDirectoryName(Path.GetDirectoryName(args[1])));
        return 27;
    }
    if (!inspect && scenario.Mode == "decodeerror" && r.GetProperty("startSeconds").GetDouble() > 0) { Console.Error.WriteLine("Synthetic decoder error after prior valid artifact"); return 27; }
    JsonObject response;
    if (inspect)
    {
        Check.That(kind is "audio" or "video", "Inspect kind");
        response = new() { ["protocol"] = 1, ["sourcePath"] = source, ["kind"] = kind, ["durationSeconds"] = scenario.Duration,
            ["hasAudio"] = kind == "audio" || scenario.HasAudio, ["audioTrackCount"] = kind == "audio" ? null : scenario.HasAudio ? 1 : 0,
            ["width"] = kind == "video" ? scenario.Width : 0, ["height"] = kind == "video" ? scenario.Height : 0 };
        if (scenario.Mode == "nullmetadata") response["hasAudio"] = null;
        if (scenario.Mode == "extrametadata") response["unexpected"] = 1;
        if (scenario.Audit) ArtifactAudit.Birth(source, args[1], null);
    }
    else
    {
        double start = r.GetProperty("startSeconds").GetDouble();
        double? end = kind == "frame" ? null : r.GetProperty("endSeconds").GetDouble();
        var edgeValue = r.GetProperty("maxEdge"); int? edge = edgeValue.ValueKind == JsonValueKind.Null ? null : edgeValue.GetInt32();
        string output = r.GetProperty("outputDirectory").GetString()!;
        Check.That(double.IsFinite(start) && start >= 0 && start < scenario.Duration && (end is null || double.IsFinite(end.Value) && end > start && end <= scenario.Duration), "Invalid requested interval");
        Check.That(Path.IsPathFullyQualified(output) && !Directory.Exists(output), "Output must be new task directory");
        Check.That(edge is null or >= 64 and <= 1024, "Invalid edge");
        Check.That(args[0] != "extract-soundtrack" || kind == "audio", "Soundtrack request kind");
        Directory.CreateDirectory(output); bool audio = kind == "audio";
        string name = audio ? "audio.wav" : "frame.png";
        if (audio) { Check.That(end is not null && end - start <= 30, "Audio must have bounded <=30s interval"); Wave.Write(Path.Combine(output, name), end!.Value - start, start, scenario.Mode); }
        else
        {
            Check.That(kind == "frame", "Frame kind required");
            int scene = scenario.Mode == "duplicate" ? 0 : Math.Min(2, (int)Math.Round((start - .5) / 3));
            Png.Write(Path.Combine(output, name), scenario.Width, scenario.Height, Png.Scene(scenario.Width, scenario.Height, scene), scenario.Mode.StartsWith("filter", StringComparison.Ordinal) ? scenario.Mode[^1] - '0' : 0);
        }
        if (scenario.Mode == "unknown-owned-task-file") File.WriteAllText(Path.Combine(output, "unknown.synthetic"), "Task-only unknown content must force retention, not broad deletion.");
        if (scenario.Audit) ArtifactAudit.Birth(source, args[1], Path.Combine(output, name));
        var artifact = new JsonObject { ["relativePath"] = name, ["modality"] = audio ? "audio" : "image", ["startSeconds"] = start, ["endSeconds"] = end,
            ["sampleRate"] = audio ? 16000 : null, ["channels"] = audio ? 1 : null, ["bitsPerSample"] = audio ? 16 : null,
            ["width"] = audio ? null : scenario.Width, ["height"] = audio ? null : scenario.Height };
        response = new() { ["protocol"] = 1, ["sourcePath"] = source, ["kind"] = kind, ["sourceDurationSeconds"] = scenario.Duration, ["artifacts"] = new JsonArray(artifact) };
    }
    Console.WriteLine(response.ToJsonString()); return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 65; }
