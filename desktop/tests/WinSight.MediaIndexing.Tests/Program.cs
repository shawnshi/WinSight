using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace WinSight.MediaIndexing.Tests;

internal sealed class Run(string root)
{
    internal readonly List<object> Results = [];
    internal int Failures;
    internal int PendingCount;
    private int ordinal;
    internal async Task Case(string name, Func<string, Task> action)
    {
        if (Failures != 0) { Pending(name, "Not executed after first observed failure; no hidden retry/cascading pass"); return; }
        string dir = Path.Combine(root, "c" + (++ordinal).ToString("D3")); Directory.CreateDirectory(dir); var timer = Stopwatch.StartNew();
        try { await action(dir); Results.Add(new { name, status = "passed", seconds = timer.Elapsed.TotalSeconds, path = dir }); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Failures++; Results.Add(new { name, status = "failed", seconds = timer.Elapsed.TotalSeconds, path = dir, error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error); }
        Save();
    }
    internal void Pending(string name, string why) { PendingCount++; Results.Add(new { name, status = "pending", reason = why }); Console.WriteLine("PENDING " + name + ": " + why); Save(); }
    internal void Save() => File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { failures = Failures, pending = PendingCount, results = Results }, new JsonSerializerOptions { WriteIndented = true }));
}
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length % 2 != 0) return 64;
        var options = Enumerable.Range(0, args.Length / 2).ToDictionary(i => args[i * 2], i => args[i * 2 + 1], StringComparer.Ordinal);
        string root = options.GetValueOrDefault("--artifact-root") ?? throw new ArgumentException("Explicit artifact root required");
        Check.That(Path.IsPathFullyQualified(root) && !Directory.Exists(root), "Explicit NEW artifact root required"); Directory.CreateDirectory(root);
        var run = new Run(root);
        await FixtureCases(run, options["--fake-host"]);
        ActualApi? api = null;
        if (!options.TryGetValue("--actual-infrastructure-assembly", out var assembly)) run.Pending("candidate/infrastructure", "Explicit actual production Infrastructure DLL not supplied; no stub and no sampler/index pass claimed");
        else
        {
            await run.Case("actual/assembly-load", _ =>
            {
                Check.That(Path.IsPathFullyQualified(assembly) && File.Exists(assembly), "Actual Infrastructure DLL missing"); api = new ActualApi(assembly);
                File.WriteAllText(Path.Combine(root, "actual-assembly.json"), JsonSerializer.Serialize(new { path = assembly, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))) })); return Task.CompletedTask;
            });
            if (api is not null)
            {
                string[] missing = api.Missing();
                if (missing.Length > 0) { run.Pending("candidate/missing-component", string.Join(", ", missing)); api = null; }
                else { await SamplerCases.Run(run, api, options["--fake-host"]); await ConsumerCases.Run(run, api, options["--fake-host"]); }
            }
        }
        if (api is not null && options.TryGetValue("--actual-host", out var host)) await NativeCases.Run(run, api, host);
        else run.Pending("native-host/e2e", "Actual candidate Infrastructure and explicit read-only N2b-0 Host both required");
        if (api is not null)
        {
            await GapPolicyCases.Run(run, api);
            await GapSecurityCases.Run(run, api, options["--fake-host"]);
            // Intentional cleanup-retention keeps shared ancestor pins until process exit.
            // Run these cases last so their protected ancestors cannot invalidate earlier rename/release oracles.
            await RedactionCases.Run(run, api, options["--fake-host"]);
        }
        else
        {
            run.Pending("pure-policy/private-helpers", "Actual production helpers required for the executable GapPolicyCases");
            run.Pending("index/additional-security-regression", "Actual integrated consumer/client required for the executable GapSecurityCases");
        }
        run.Save(); Console.WriteLine($"SUMMARY failed={run.Failures} pending={run.PendingCount} (fixture passes are NOT production evidence)");
        return run.Failures > 0 ? 1 : options.ContainsKey("--require-integration") && run.PendingCount > 0 ? 3 : 0;
    }
    private static async Task FixtureCases(Run run, string fake)
    {
        await run.Case("fixture/audio-oracle-known-vectors", _ =>
        {
            (double D, (double, double)[] E)[] examples = [(2 - 1 / 16000d, Array.Empty<(double, double)>()), (2d, new[] { (0d, 2d) }), (30d, new[] { (0d, 30d) }), (31d, new[] { (0d, 30d) }), (32d, new[] { (0d, 30d), (25d, 32d) }), (55d, new[] { (0d, 30d), (25d, 55d) }), (57d, new[] { (0d, 30d), (25d, 55d), (50d, 57d) }) ];
            foreach (var x in examples) Check.That(Oracle.Audio(x.D).SequenceEqual(x.E), "Test oracle audio vector");
            Check.That(Oracle.Rms(new short[] { 98 }) < .003 && Oracle.Rms(new short[] { 99 }) > .003, "Test oracle RMS threshold");
            Check.That(!(.003 > .003) && Math.BitIncrement(.003) > .003, "Strict double threshold oracle"); return Task.CompletedTask;
        });
        await run.Case("fixture/video-oracle-known-vectors", _ =>
        {
            Check.That(Oracle.Video(.2).SequenceEqual(new[] { .1 }) && Oracle.Video(10).SequenceEqual(new[] { .5, 3.5, 6.5, 9.5 }), "Video first/step");
            Check.That(Oracle.Video(7200).Length == 1200 && Oracle.Video(7200)[^1] == 7194.5, "Video 1200 bound");
            ulong a = Oracle.Hash(8, 8, Png.Scene(8, 8, 0)), b = Oracle.Hash(8, 8, Png.Scene(8, 8, 1)), c = Oracle.Hash(8, 8, Png.Scene(8, 8, 2));
            Check.That(a == 0xffffffffUL && System.Numerics.BitOperations.PopCount(a ^ b) == 4 && System.Numerics.BitOperations.PopCount(b ^ c) == 4 && System.Numerics.BitOperations.PopCount(a ^ c) == 8, "Hash bit order/last-kept witness");
            Check.That(Oracle.Hash(1, 1, [255, 0, 0]) == 0 && Oracle.Hash(2, 1, [255, 0, 0, 0, 255, 0]) == 0xf0f0f0f0f0f0f0f0UL, "Small RGB integer grayscale vectors"); return Task.CompletedTask;
        });
        await run.Case("fixture/RIFF-non44-and-temporal-markers", dir =>
        {
            string path = Path.Combine(dir, "s.wav"); Wave.Write(path, 2, 25); var samples = Wave.Read(path); Check.That(samples.Length == 32000, "Non44 WAV physical frame count"); Wave.Markers(samples, 25);
            return Check.Throws(() => { Wave.Markers(samples, 0); return Task.CompletedTask; }, e => e is InvalidDataException);
        });
        await run.Case("fixture/PNG-all-filters-native-decode", async dir =>
        {
            var rgb = Enumerable.Range(0, 9 * 7 * 3).Select(i => (byte)(i * 73)).ToArray();
            for (int filter = 0; filter < 5; filter++)
            {
                string path = Path.Combine(dir, "f" + filter + ".png"); Png.Write(path, 9, 7, rgb, filter); var file = await StorageFile.GetFileFromPathAsync(path);
                using var s = await file.OpenReadAsync(); var decoder = await BitmapDecoder.CreateAsync(s); var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
                Check.That(decoder.PixelWidth == 9 && decoder.PixelHeight == 7 && Enumerable.Range(0, 63).All(i => pixels[i * 4] == rgb[i * 3] && pixels[i * 4 + 1] == rgb[i * 3 + 1] && pixels[i * 4 + 2] == rgb[i * 3 + 2]), "Fixture PNG filter disagrees with native decoder");
            }
        });
        await run.Case("fixture/fakeworker-requested-nonzero-interval", async dir =>
        {
            string source = SamplerCases.Source(dir, "audio", new(57)); string request = Path.Combine(dir, "r.json"), output = Path.Combine(dir, "o");
            File.WriteAllText(request, JsonSerializer.Serialize(new { inputPath = source, outputDirectory = output, kind = "audio", startSeconds = 25d, endSeconds = 32d, maxEdge = (int?)null }));
            var result = await Child(fake, ["extract-media", request], dir); Check.That(result.Code == 0, result.Error); using var doc = JsonDocument.Parse(result.Output);
            Check.That(doc.RootElement.GetProperty("sourceDurationSeconds").GetDouble() == 57 && doc.RootElement.GetProperty("artifacts")[0].GetProperty("startSeconds").GetDouble() == 25, "Fake returns requested interval/duration, not fixed4s");
            var samples = Wave.Read(Path.Combine(output, "audio.wav")); Check.That(samples.Length == 112000, "Requested 7sec PCM"); Wave.Markers(samples, 25); Owned.Gone(dir);
        });
        await run.Case("fixture/fakeworker-metadata-exact-null-fields", async dir =>
        {
            string source = SamplerCases.Source(dir, "audio", new(57)), request = Path.Combine(dir, "r.json");
            File.WriteAllText(request, JsonSerializer.Serialize(new { inputPath = source, kind = "audio" })); var result = await Child(fake, ["inspect-media", request], dir); Check.That(result.Code == 0, result.Error); using var d = JsonDocument.Parse(result.Output); var r = d.RootElement;
            Check.That(r.EnumerateObject().Count() == 8 && r.GetProperty("audioTrackCount").ValueKind == JsonValueKind.Null && r.GetProperty("width").GetInt32() == 0 && r.GetProperty("height").GetInt32() == 0 && Path.GetFullPath(r.GetProperty("sourcePath").GetString()!) == Path.GetFullPath(source), "Metadata exact fields/nulls/normalized source");
            File.WriteAllText(request, JsonSerializer.Serialize(new { inputPath = source, kind = "audio", extra = 1 })); result = await Child(fake, ["inspect-media", request], dir); Check.That(result.Code != 0, "Unknown request field accepted");
            File.WriteAllText(request, new string('x', 65537)); result = await Child(fake, ["inspect-media", request], dir); Check.That(result.Code != 0 && result.Error.Contains("64KiB", StringComparison.Ordinal), "64KiB request limit");
        });
    }
    internal sealed record CommandResult(int Code, string Output, string Error);
    internal static async Task<CommandResult> Child(string exe, string[] args, string root)
    {
        var si = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (string a in args) si.ArgumentList.Add(a);
        using var p = Process.Start(si) ?? throw new IOException("Could not launch owned test worker");
        var identity = new PidReceipt(p.Id, p.StartTime.ToUniversalTime().Ticks); File.WriteAllText(Path.Combine(root, "cli-" + p.Id + ".pid.json"), JsonSerializer.Serialize(identity));
        var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(7)); }
        catch (TimeoutException)
        {
            if (!p.HasExited && p.StartTime.ToUniversalTime().Ticks == identity.CreationUtcTicks) { p.Kill(); Check.That(p.WaitForExit(3000), "Owned worker exit unconfirmed"); }
            throw;
        }
        return new(p.ExitCode, await output.WaitAsync(TimeSpan.FromSeconds(2)), await error.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
