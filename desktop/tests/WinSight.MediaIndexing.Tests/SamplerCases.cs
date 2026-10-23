using System.Text.Json;

namespace WinSight.MediaIndexing.Tests;

internal static class SamplerCases
{
    internal static string Source(string dir, string kind, Scenario scenario)
    {
        string path = Path.Combine(dir, kind == "audio" ? "s.wav" : "s.mp4");
        File.WriteAllBytes(path, [1, 2, 3, 4]); File.WriteAllText(path + ".fixture.json", JsonSerializer.Serialize(scenario)); return path;
    }
    internal static async Task With(ActualApi api, string host, string dir, string path, string kind, int budget, Func<object, object, object, Task> body)
    {
        var client = api.Client(host, Path.Combine(dir, "w")); using var source = (IDisposable)api.Source(path, kind);
        try { var sampler = api.Sampler(client, budget); var session = await api.Open(sampler, source); try { await body(client, source, session); } finally { await ActualApi.Dispose(session); } }
        finally { await ActualApi.Dispose(client); }
        Owned.Gone(dir);
        Check.That(Directory.GetFileSystemEntries(Path.Combine(dir, "w")).Length == 0, "Settled sampling retained task artifacts");
    }
    private static async Task<List<(double Start, double End, string Modality)>> Read(object session, bool image, bool audio, bool markers = true)
    {
        var rows = new List<(double, double, string)>();
        await foreach (var sample in ActualApi.Samples(session, image, audio))
        {
            try
            {
                var segment = ActualApi.Get(sample, "Segment")!; var input = ActualApi.Get(sample, "Input")!;
                string operation = ActualApi.Get<string>(input, "Operation"), path = ActualApi.Get<string>(input, "Value");
                double start = ActualApi.Get<double>(segment, "Location"), end = ActualApi.Get<double>(segment, "LocationEnd");
                Check.That(start >= 0 && end > start && ActualApi.Get(segment, "Excerpt") is null && ActualApi.Get(segment, "Vector") is null, "Unsafe/unbounded sample segment");
                Check.That(operation == ActualApi.Get<string>(segment, "Modality") && ActualApi.Get<string>(segment, "Kind") == (operation == "audio" ? "audio" : "frame"), "Sample typed kind/modality");
                Check.That(Path.IsPathFullyQualified(path) && File.Exists(path), "Returned lease is absent");
                if (operation == "image") Check.That(ActualApi.Get<int>(input, "Budget") == 70, "Frame embedding budget 70");
                if (operation == "audio") { var pcm = Wave.Read(path); Check.That(Math.Abs(pcm.Length - (end - start) * 16000) <= 1, "Requested PCM interval lost"); if (markers) Wave.Markers(pcm, start); }
                rows.Add((start, end, operation));
            }
            finally { await ActualApi.Dispose(sample); }
        }
        return rows;
    }
    internal static async Task Run(Run run, ActualApi api, string fake)
    {
        int failures = run.Failures;
        await run.Case("actual/seam", _ => { api.AssertShape(); return Task.CompletedTask; });
        if (run.Failures != failures) return;
        foreach (string rejection in new[] { "wrong-kind", "code", "oversize" })
            await run.Case("actual/source/admission/" + rejection, async dir =>
            {
                string path = Path.Combine(dir, rejection == "code" ? "s.cs" : "s.wav");
                using (var file = File.Create(path)) file.SetLength(rejection == "oversize" ? 32L * 1024 * 1024 + 1 : 4);
                await Check.Throws(() => { using var source = (IDisposable)api.Source(path, rejection == "wrong-kind" ? "video" : "audio"); return Task.CompletedTask; }, e => e is ArgumentException or InvalidDataException or IOException or NotSupportedException);
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            });
        foreach (double seconds in new[] { 2 - 1 / 16000d, 2, 30, 31, 32, 55, 57 })
            await run.Case("actual/audio/windows/" + seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture), async dir =>
            {
                string path = Source(dir, "audio", new(seconds));
                await With(api, fake, dir, path, "audio", 5000, async (_, _, session) =>
                {
                    var actual = await Read(session, false, true); var expected = Oracle.Audio(seconds);
                    Check.That(actual.Select(v => (v.Start, v.End)).SequenceEqual(expected), "Audio windows differ from frozen source oracle");
                    Check.That(ActualApi.Get<bool>(session, "CoverageComplete"), "Natural EOF must be complete");
                });
            });
        foreach (string mode in new[] { "r98", "r99", "silent" })
            await run.Case("actual/audio/rms/" + mode, async dir =>
            {
                string path = Source(dir, "audio", new(2, mode));
                await With(api, fake, dir, path, "audio", 5000, async (_, _, session) =>
                { var actual = await Read(session, false, true, false); Check.That(actual.Count == (mode == "r99" ? 1 : 0), "Strict RMS > .003"); Check.That(ActualApi.Get<bool>(session, "CoverageComplete"), "Silent EOF is complete"); });
            });
        foreach (var test in new (double Duration, string Mode, bool Complete, int Retained)[] { (55d, "tone", true, 2), (82d, "tone", false, 2), (82d, "silent", false, 0) })
            await run.Case($"actual/audio/budget/{test.Duration}/{test.Mode}", async dir =>
            {
                string path = Source(dir, "audio", new(test.Duration, test.Mode));
                await With(api, fake, dir, path, "audio", 2, async (_, _, session) =>
                {
                    var samples = await Read(session, false, true, test.Mode == "tone"); Check.That(samples.Count == test.Retained, "Retained differs from attempts");
                    Check.That(ActualApi.Get<bool>(session, "CoverageComplete") == test.Complete, "Exact EOF vs budget cutoff");
                    if (!test.Complete) Check.That(!string.IsNullOrWhiteSpace(ActualApi.Get<string?>(session, "Detail")), "Budget partial needs reason");
                    Check.That(Requests(dir).Count(r => r.GetProperty("operation").GetString() != "inspect-media") == 2, "Silent attempts must consume budget");
                });
            });
        foreach (int budget in new[] { 0, 5001 })
            await run.Case("actual/budget/reject/" + budget, async dir =>
            {
                var c = api.Client(fake, Path.Combine(dir, "w"));
                try { await Check.Throws(() => { api.Sampler(c, budget); return Task.CompletedTask; }, e => e is ArgumentOutOfRangeException); }
                finally { await ActualApi.Dispose(c); }
            });
        foreach (string mode in new[] { "filter0", "filter1", "filter2", "filter3", "filter4", "duplicate" })
            await run.Case("actual/video/hash/" + mode, async dir =>
            {
                string path = Source(dir, "video", new(10, mode, false));
                await With(api, fake, dir, path, "video", 5000, async (_, _, session) =>
                {
                    var samples = await Read(session, true, false); var expected = mode == "duplicate" ? new[] { (.5, 10d) } : new[] { (.5, 6.5), (6.5, 10d) };
                    Check.That(samples.Select(v => (v.Start, v.End)).SequenceEqual(expected), "<=4 hash boundary/last-kept comparison/run end");
                    Check.That(ActualApi.Get<bool>(session, "CoverageComplete"), "No-audio video should complete");
                    var requests = Requests(dir).Where(r => r.GetProperty("operation").GetString() == "extract-media").ToArray();
                    Check.That(requests.Length == 4 && requests.All(r => r.GetProperty("request").GetProperty("maxEdge").GetInt32() == 1024), "Video count/maxEdge");
                    Check.That(requests.Select(r => r.GetProperty("request").GetProperty("startSeconds").GetDouble()).SequenceEqual(Oracle.Video(10)), "Video plan first/step");
                });
            });
        await run.Case("actual/video/duplicate-attempt-budget", async dir =>
        {
            string path = Source(dir, "video", new(20, "duplicate", false));
            await With(api, fake, dir, path, "video", 3, async (_, _, session) =>
            {
                var samples = await Read(session, true, false); Check.That(samples.Count == 1 && !ActualApi.Get<bool>(session, "CoverageComplete"), "Duplicate attempts count; budget must remain partial");
                Check.That(Requests(dir).Count(r => r.GetProperty("operation").GetString() == "extract-media") == 3, "No hidden extra decoding");
            });
        });
        foreach (var test in new (bool Image, bool Audio)[] { (true, false), (false, true), (false, false), (true, true) })
            await run.Case($"actual/video/capabilities/{test.Image}/{test.Audio}", async dir =>
            {
                string path = Source(dir, "video", new(10));
                await With(api, fake, dir, path, "video", 5000, async (_, _, session) =>
                {
                    var samples = await Read(session, test.Image, test.Audio);
                    Check.That(samples.Any(s => s.Modality == "audio") == test.Audio && samples.Any(s => s.Modality == "image") == test.Image, "Capability selection");
                    Check.That(ActualApi.Get<bool>(session, "CoverageComplete") == (test.Image && test.Audio), "Omitted existing modality cannot complete");
                    var requests = Requests(dir); Check.That(test.Image || !requests.Any(r => r.GetProperty("request").GetProperty("kind").GetString() == "frame"), "No image decode without capability");
                    Check.That(test.Audio || !requests.Any(r => r.GetProperty("operation").GetString() == "extract-soundtrack"), "No soundtrack decode without capability");
                });
            });
        await run.Case("actual/session/independent-pins-and-lease", async dir =>
        {
            string path = Source(dir, "audio", new(32)); var client = api.Client(fake, Path.Combine(dir, "w"));
            var source = (IDisposable)api.Source(path, "audio"); object? session = null; object? sample = null;
            try
            {
                Blocked(path); BlockedDirectory(dir);
                session = await api.Open(api.Sampler(client), source); source.Dispose();
                await using var iterator = ActualApi.Samples(session, false, true).GetAsyncEnumerator();
                Check.That(await iterator.MoveNextAsync(), "Expected retained sample"); sample = iterator.Current;
                string artifact = ActualApi.Get<string>(ActualApi.Get(sample, "Input")!, "Value");
                Blocked(path); BlockedDirectory(dir);
                await ActualApi.Dispose(session); session = null;
                Check.That(Wave.Read(artifact).Length == 30 * 16000, "Session disposed sample lease prematurely or made it unreadable"); Blocked(path); BlockedDirectory(dir);
                await ActualApi.Await(ActualApi.Call(client, "InspectAsync", path, "audio", CancellationToken.None)!);
                await ActualApi.Dispose(client);
                Check.That(Wave.Read(artifact).Length == 30 * 16000, "Client disposed sample lease prematurely or made it unreadable"); Blocked(path);
                await ActualApi.Dispose(sample); sample = null;
                Check.That(!File.Exists(artifact), "Released sample artifact persists");
            }
            finally { if (sample is not null) await ActualApi.Dispose(sample); if (session is not null) await ActualApi.Dispose(session); source.Dispose(); await ActualApi.Dispose(client); }
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            string moved = dir + "-moved"; Directory.Move(dir, moved); Directory.Move(moved, dir); Owned.Gone(dir);
        });
        await run.Case("actual/session/no-reentry", async dir =>
        {
            string path = Source(dir, "audio", new(2));
            await With(api, fake, dir, path, "audio", 5000, async (_, _, session) =>
            {
                await Read(session, false, true);
                await Check.Throws(async () => { await foreach (var sample in ActualApi.Samples(session, false, true)) await ActualApi.Dispose(sample); }, e => e is InvalidOperationException or ObjectDisposedException);
            });
        });
        await run.Case("actual/video/combined-attempt-budget", async dir =>
        {
            string path = Source(dir, "video", new(32));
            await With(api, fake, dir, path, "video", 3, async (_, _, session) =>
            {
                await Read(session, true, true); Check.That(!ActualApi.Get<bool>(session, "CoverageComplete"), "Cross-modality budget must be partial");
                Check.That(Requests(dir).Count(r => r.GetProperty("operation").GetString() != "inspect-media") == 3, "Image/audio attempts must share one TOTAL budget");
            });
        });
        await run.Case("actual/session/unread-not-complete", async dir =>
        {
            string path = Source(dir, "audio", new(32));
            await With(api, fake, dir, path, "audio", 5000, (_, _, session) => { Check.That(!ActualApi.Get<bool>(session, "CoverageComplete"), "Unenumerated session cannot complete"); return Task.CompletedTask; });
        });
        foreach (string mode in new[] { "nullmetadata", "extrametadata" })
            await run.Case("actual/metadata/reject/" + mode, async dir =>
            {
                string path = Source(dir, "audio", new(2, mode)); var client = api.Client(fake, Path.Combine(dir, "w")); using var source = (IDisposable)api.Source(path, "audio");
                try { await Check.Throws(async () => { await using var session = (IAsyncDisposable)await api.Open(api.Sampler(client), source); }, Check.DataOrIo); }
                finally { await ActualApi.Dispose(client); } Owned.Gone(dir);
            });
        await run.Case("actual/cancel/owned-process", async dir =>
        {
            string path = Source(dir, "audio", new(32, "hang")); var client = api.Client(fake, Path.Combine(dir, "w")); using var source = (IDisposable)api.Source(path, "audio");
            try
            {
                await using var session = (IAsyncDisposable)await api.Open(api.Sampler(client), source); using var cancel = new CancellationTokenSource();
                await using var iterator = ActualApi.Samples(session, false, true, cancel.Token).GetAsyncEnumerator(); var move = iterator.MoveNextAsync().AsTask();
                await WaitRequest(dir, "extract-media"); cancel.Cancel();
                await Check.Throws(async () => { await move.WaitAsync(TimeSpan.FromSeconds(8)); }, e => e is OperationCanceledException);
                Check.That(!ActualApi.Get<bool>(session, "CoverageComplete"), "Cancel cannot cover EOF");
            }
            finally { await ActualApi.Dispose(client); } Owned.Gone(dir);
        });
    }
    internal static JsonElement[] Requests(string dir)
    {
        string path = Path.Combine(dir, "q"); if (!Directory.Exists(path)) return [];
        return Directory.GetFiles(path, "*.json").OrderBy(File.GetLastWriteTimeUtc).Select(file =>
        {
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var doc = JsonDocument.Parse(s); return doc.RootElement.Clone();
        }).ToArray();
    }
    private static async Task WaitRequest(string dir, string op)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!Requests(dir).Any(r => r.GetProperty("operation").GetString() == op)) await Task.Delay(10, deadline.Token);
    }
    internal static void Blocked(string path)
    {
        try { using var write = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete); }
        catch (IOException) { return; }
        throw new InvalidDataException("Source not write-denied across ownership await");
    }
    internal static void BlockedDirectory(string dir)
    {
        try { Directory.Move(dir, dir + "-forbidden"); }
        catch (IOException) { return; }
        Directory.Move(dir + "-forbidden", dir); throw new InvalidDataException("Source ancestor rename not denied");
    }
}
