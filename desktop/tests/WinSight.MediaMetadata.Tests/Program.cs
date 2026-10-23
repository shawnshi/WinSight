using System.Text.Json;
using WinSight.MediaMetadata.Tests;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 0; i < args.Length; i += 2)
{
    if (i + 1 == args.Length || !new[] { "--artifact-root", "--fake-host", "--actual-host", "--client-assembly", "--require-integration" }.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1]))
        throw new ArgumentException("Unknown/duplicate/missing test option");
}
string root = options["--artifact-root"], fake = options["--fake-host"];
if (!Path.IsPathFullyQualified(root) || Directory.Exists(root) || !Path.IsPathFullyQualified(fake) || !File.Exists(fake)) throw new ArgumentException("New absolute task root and explicit fake executable required");
Directory.CreateDirectory(root);
var run = new Run(root);
Synthetic? fixture = null;
await run.Case("fixtures/native-metadata-timeline", async dir => { fixture = await Fixtures.Create(dir); });
await run.Case("fake/exact-inspect-request", async dir =>
{
    string source = Path.Combine(dir, "valid.wav"), request = Path.Combine(dir, "i.json"); Wave.Write(source, 4);
    File.WriteAllText(request, JsonSerializer.Serialize(new { inputPath = source, kind = "audio" }));
    var result = await Child.Run(fake, ["inspect-media", request], Path.Combine(dir, "good"));
    Verify.That(result.Code == 0, result.Error); using var doc = JsonDocument.Parse(result.Output);
    Verify.That(doc.RootElement.GetProperty("audioTrackCount").ValueKind == JsonValueKind.Null, "Audio track count must remain unknown");
    File.WriteAllText(request, JsonSerializer.Serialize(new { inputPath = source, kind = "audio", outputDirectory = dir }));
    var invalid = await Child.Run(fake, ["inspect-media", request], Path.Combine(dir, "extra")); Verify.That(invalid.Code != 0, "Fake silently accepted extra inspect field");
});
await run.Case("fake/exact-N1-nullable-response", async dir =>
{
    string source = Path.Combine(dir, "valid.mp4"), request = Path.Combine(dir, "d.json"); File.WriteAllText(source, "synthetic fake input");
    File.WriteAllText(request, JsonSerializer.Serialize(new { inputPath = source, outputDirectory = Path.Combine(dir, "o"), kind = "audio", startSeconds = .75, endSeconds = 2.25, maxEdge = (int?)null }));
    var result = await Child.Run(fake, ["extract-soundtrack", request], Path.Combine(dir, "good")); Verify.That(result.Code == 0, result.Error);
    using var doc = JsonDocument.Parse(result.Output); var artifact = doc.RootElement.GetProperty("artifacts")[0];
    string[] fields = ["relativePath", "modality", "startSeconds", "endSeconds", "sampleRate", "channels", "bitsPerSample", "width", "height"];
    Verify.That(artifact.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(fields.Order()) && artifact.GetProperty("width").ValueKind == JsonValueKind.Null && artifact.GetProperty("height").ValueKind == JsonValueKind.Null, "Exact nullable N1 artifact fields");
    Wave.Markers(Wave.Read(Path.Combine(dir, "o", "audio.wav")), .75);
});
await run.Case("oracles/reject-zero-start-padding-truncation", async dir =>
{
    string good = Path.Combine(dir, "g.wav"), wrong = Path.Combine(dir, "w.wav"); Wave.Write(good, 1.5, .75); Wave.Write(wrong, 1.5, 0);
    Wave.Markers(Wave.Read(good), .75);
    await Verify.Throws(() => { Wave.Markers(Wave.Read(wrong), .75); return Task.CompletedTask; }, e => e is InvalidDataException);
    await Verify.Throws(() => { Wave.SamePhysicalTimeline(wrong, good); return Task.CompletedTask; }, e => e is InvalidDataException);
    Wave.Write(wrong, 1.51, .75);
    await Verify.Throws(() => { Wave.SamePhysicalTimeline(wrong, good); return Task.CompletedTask; }, e => e is InvalidDataException);
    var bytes = File.ReadAllBytes(good); File.WriteAllBytes(wrong, bytes[..^2]);
    await Verify.Throws(() => { Wave.Read(wrong); return Task.CompletedTask; }, e => e is InvalidDataException);
    // This fixture writer puts data last; retain valid RIFF length while silencing just 20ms of audible tail.
    Array.Clear(bytes, bytes.Length - 640, 640); File.WriteAllBytes(wrong, bytes);
    await Verify.Throws(() => { Wave.SamePhysicalTimeline(wrong, good); return Task.CompletedTask; }, e => e is InvalidDataException);
});
await run.Case("fixtures/task-junction-control", dir =>
{
    string target = Path.Combine(dir, "t"), link = Path.Combine(dir, "l"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "keep"), "task target");
    Junction.Create(link, target); Verify.That(File.ReadAllText(Path.Combine(link, "keep")) == "task target", "Junction control did not resolve");
    Directory.Delete(link); Verify.That(File.Exists(Path.Combine(target, "keep")), "Link cleanup deleted target"); return Task.CompletedTask;
});
if (options.TryGetValue("--actual-host", out var host))
{
    Verify.That(Path.IsPathFullyQualified(host) && File.Exists(host), "Explicit actual Host not found");
    if (fixture is not null) await HostCases.Run(run, host, fixture);
    else run.Pending("host/E2E", "Fixture generation failed; native Host cases cannot execute");
}
else run.Pending("host/E2E", "--actual-host omitted: actual inspect-media and extract-soundtrack NOT RUN (not an E2E pass)");
if (options.TryGetValue("--client-assembly", out var assembly))
{
    var api = ClientApi.TryLoad(assembly, out string? pending);
    if (api is null) run.Pending("client/public-API", pending!);
    else
    {
        await ClientCases.Run(run, api, fake);
        if (host is not null && fixture is not null) await ClientCases.Actual(run, api, host, fixture);
        else run.Pending("client/actual-E2E", "Actual Host or successful fixtures absent");
    }
}
else run.Pending("client/public-API", "--client-assembly omitted: no production API stub, restore or blind retry");
run.Pending("host/real-multitrack", "Cannot synthesize >1 embedded audio track with approved installed Windows composition path; no ffmpeg/tools/downloads. Explicitly permitted residual branch, not a passing test.", blocking: false);
run.Save();
Console.WriteLine(JsonSerializer.Serialize(new { passed = run.Passed, failed = run.Failed, pending = run.PendingCount, integration = run.BlockingPending == 0 && run.Failed == 0 ? "passed" : "not-complete" }));
return run.Failed > 0 ? 1 : options.GetValueOrDefault("--require-integration") == "true" && run.BlockingPending > 0 ? 2 : 0;

namespace WinSight.MediaMetadata.Tests
{
    internal sealed record Outcome(string Name, string Status, string Directory, string Evidence, bool Blocking = true);
    internal sealed class Run(string root)
    {
        private readonly List<Outcome> results = [];
        internal int Passed => results.Count(x => x.Status == "passed");
        internal int Failed => results.Count(x => x.Status == "failed");
        internal int PendingCount => results.Count(x => x.Status == "pending");
        internal int BlockingPending => results.Count(x => x.Status == "pending" && x.Blocking);
        internal async Task Case(string name, Func<string, Task> body)
        {
            string dir = Path.Combine(root, "c" + results.Count.ToString("D3")); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "name.txt"), name);
            try { await body(dir); results.Add(new(name, "passed", dir, "Assertions physically executed")); Console.WriteLine("PASS " + name); }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, "failure.txt"), e.ToString()); results.Add(new(name, "failed", dir, e.ToString())); Console.WriteLine("FAIL " + name + ": " + e.Message); }
            Save();
        }
        internal void Pending(string name, string reason, bool blocking = true) { results.Add(new(name, "pending", "", reason, blocking)); Console.WriteLine("PENDING " + name + ": " + reason); Save(); }
        internal void Save() => File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
