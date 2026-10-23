using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinSight.MediaIndexing.Tests;

internal static class RedactionCases
{
    private const string Canary = "UNTRUSTED_ECHO";
    internal static async Task Run(Run run, ActualApi api, string fake)
    {
        foreach (int failAt in new[] { 1, 2 })
        foreach (bool encodedOnly in new[] { false, true })
            await run.Case($"redaction/item/{failAt}/{(encodedOnly ? "encoded" : "raw")}", dir => Media(api, fake, dir, new(failAt == 1 ? 2 : 32), async (h, service, path, workspace) =>
            {
                string? echo = null;
                h.ItemError = (input, call) => call == failAt ? echo = Echo(ActualApi.Get<string>(input, "Value"), workspace, encodedOnly) : null;
                var progress = await Index(api, service, h.Request(Path.GetDirectoryName(path)!));
                object row = h.Row(path);
                VerifyRow(row, path, failAt == 1 ? "failed" : "partial", failAt - 1);
                Check.That(h.EmbeddingCalls == failAt && h.ExtractCalls == 0 && echo is not null, "Must exercise actual media embedding callback, not extractor fallback");
                Check.That(Regex.IsMatch(ActualApi.Get<string>(row, "Detail"), @"\b1\b") && Has(ActualApi.Get<string>(row, "Detail"), "embedding"), "Item diagnostic must retain one embedding failure count/stage");
                VerifyProgress(progress, failAt == 1 ? "failed" : "partial", true);
                Settled(h, path, workspace, dir);
                Receipt(dir, row, path, workspace, h, echo, null, progress);
                Closed(row, workspace, h.Inputs.Select(i => ActualApi.Get<string>(i, "Value")));
            }));

        foreach (int failAt in new[] { 1, 2 })
            await run.Case($"redaction/dependency/{failAt}", dir => Media(api, fake, dir, new(failAt == 1 ? 2 : 32), async (h, service, path, workspace) =>
            {
                IOException? original = null;
                h.DuringEmbedding = (input, _) =>
                {
                    SamplerCases.Blocked(path);
                    Check.That(h.Inputs.Take(h.Inputs.Count - 1).All(i => !File.Exists(ActualApi.Get<string>(i, "Value"))), "Previous completed lease still exists");
                    if (h.EmbeddingCalls == failAt) { original = new IOException(Echo(ActualApi.Get<string>(input, "Value"), workspace, false)); throw original; }
                    return Task.CompletedTask;
                };
                var progress = new List<object>();
                Exception failure = await Check.Throws(async () => await Index(api, service, h.Request(Path.GetDirectoryName(path)!), progress), IsDependency);
                Check.That(original is not null && Tree(failure).Any(e => ReferenceEquals(e, original)), "Dependency IOException/inner identity lost");
                // Existing dependency semantics fail the row and propagate, even after a successful sample.
                object row = h.Row(path); VerifyRow(row, path, "failed", 0);
                Check.That(h.EmbeddingCalls == failAt && h.Writes.Count(w => w == "file:failed") == 1, "Dependency terminal accounting");
                VerifyProgress(progress, "failed", false);
                Settled(h, path, workspace, dir);
                Receipt(dir, row, path, workspace, h, original!.Message, failure, progress);
                Closed(row, workspace, h.Inputs.Select(i => ActualApi.Get<string>(i, "Value")));
                Check.That(!failure.Message.Contains(Canary, StringComparison.Ordinal), "Top-level media dependency message copied freeform input");
            }));

        foreach (bool afterValid in new[] { false, true })
            await run.Case($"redaction/native-decode/{(afterValid ? "after-valid" : "before-yield")}", dir => Media(api, fake, dir, new(afterValid ? 32 : 2, afterValid ? "echo-decode-later" : "echo-decode-first"), async (h, service, path, workspace) =>
            {
                var progress = await Index(api, service, h.Request(Path.GetDirectoryName(path)!));
                object row = h.Row(path); VerifyRow(row, path, afterValid ? "partial" : "failed", afterValid ? 1 : 0);
                Check.That(h.EmbeddingCalls == (afterValid ? 1 : 0), "Decode did not fail at required position");
                using var echo = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "e", Path.GetFileName(path) + ".echo.json")));
                string request = echo.RootElement.GetProperty("requestPath").GetString()!;
                Check.That(Path.GetFullPath(request).StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Worker did not echo its actual owned request");
                VerifyProgress(progress, afterValid ? "partial" : "failed", true);
                Settled(h, path, workspace, dir);
                Receipt(dir, row, path, workspace, h, request, null, progress);
                Closed(row, workspace, h.Inputs.Select(i => ActualApi.Get<string>(i, "Value")).Append(request).Append(echo.RootElement.GetProperty("outputDirectory").GetString()!));
            }));

        foreach (bool combined in new[] { false, true })
            await run.Case($"redaction/cleanup/{(combined ? "with-embedding-dependency" : "after-valid")}", dir => Media(api, fake, dir, new(2, "unknown-owned-task-file"), async (h, service, path, workspace) =>
            {
                IOException? original = null;
                h.DuringEmbedding = (input, _) =>
                {
                    string artifact = ActualApi.Get<string>(input, "Value"); SamplerCases.Blocked(path);
                    Check.That(File.Exists(Path.Combine(Path.GetDirectoryName(artifact)!, "unknown.synthetic")), "Unknown task-file injection missing");
                    if (combined) { original = new IOException(Echo(artifact, workspace, false)); throw original; }
                    return Task.CompletedTask;
                };
                var progress = new List<object>(); Exception? failure = null;
                if (combined) failure = await Check.Throws(async () => await Index(api, service, h.Request(Path.GetDirectoryName(path)!), progress), IsDependency);
                else await Index(api, service, h.Request(Path.GetDirectoryName(path)!), progress);
                object row = h.Row(path); VerifyRow(row, path, combined ? "failed" : "partial", combined ? 0 : 1, 2);
                Check.That(h.Inputs.Count == 1, "Cleanup fixture must consume one genuine artifact");
                string artifactPath = ActualApi.Get<string>(h.Inputs[0], "Value"), output = Path.GetDirectoryName(artifactPath)!;
                Check.That(!File.Exists(artifactPath) && File.Exists(Path.Combine(output, "unknown.synthetic")) && Directory.Exists(output), "Cleanup must retain unknown content, not claim fully cleaned success");
                SamplerCases.Blocked(path); Owned.Gone(dir);
                if (combined)
                {
                    Check.That(Tree(failure!).Any(e => ReferenceEquals(e, original)) && Tree(failure!).Any(e => e is AggregateException)
                        && Tree(failure!).Any(e => e is IOException && e.Message.Contains("cleanup", StringComparison.OrdinalIgnoreCase)), "Combined dependency + retention IOException aggregation lost");
                }
                VerifyProgress(progress, combined ? "failed" : "partial", !combined);
                Receipt(dir, row, path, workspace, h, original?.Message, failure, progress);
                Closed(row, workspace, [artifactPath]);
            }, retained: true));

        foreach (bool publicThree in new[] { true, false })
        foreach (bool dependency in new[] { false, true })
            await run.Case($"redaction/legacy-doc/{(publicThree ? "public-three" : "internal-four")}/{(dependency ? "dependency" : "item")}", async dir =>
            {
                string root = Path.Combine(dir, "input"); Directory.CreateDirectory(root);
                string path = Path.Combine(root, "synthetic.txt"); File.WriteAllText(path, "Task-only document prose.");
                string message = "Legacy error EXACT " + path + " " + JsonSerializer.Serialize(path);
                var h = new ConsumerHarness(api); IOException? original = null;
                if (dependency) h.DuringEmbedding = (_, _) => { original = new IOException(message); throw original; };
                else h.ItemError = (_, _) => message;
                object? client = publicThree ? null : api.Client(fake, Path.Combine(dir, "w"));
                try
                {
                    object service = h.Consumer(client is null ? null : api.Sampler(client));
                    Exception? failure = null;
                    if (dependency) failure = await Check.Throws(() => ConsumerHarness.Index(service, h.Request(root)), IsDependency);
                    else await ConsumerHarness.Index(service, h.Request(root));
                    object row = h.Row(path);
                    // The legacy fake extractor deliberately supplies opaque Info containing a spoofed revision.
                    // Item failure preserves that Info; dependency failure persists the original pending row with null Info.
                    ConsumerHarness.State(row, "failed", !dependency);
                    string? expectedInfo = dependency ? null : JsonSerializer.Serialize(new { artifactPath = (string?)null, native_media_revision = "native-media-sampling-v1" });
                    Check.That(ActualApi.Get<string?>(row, "Info") == expectedInfo, "Legacy opaque extractor metadata semantics changed");
                    string expected = dependency ? "Embedding service failed: IOException: " + message : "1 embedding errors: " + message;
                    Check.That(ActualApi.Get<string>(row, "Detail") == expected && ActualApi.Get<string>(row, "Path") == path, "Legacy exact message/path semantics changed");
                    Check.That(h.ExtractCalls == 1 && h.EmbeddingCalls == 1, "Legacy document not actually consumed");
                    if (dependency) Check.That(failure!.Message == expected && ReferenceEquals(failure.InnerException, original), "Legacy dependency wrapper/inner changed");
                    Owned.Atomic(Path.Combine(dir, "legacy.json"), new { path, message, expected, dependency, publicThree });
                }
                finally { if (client is not null) await ActualApi.Dispose(client); }
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Owned.Gone(dir);
            });
    }

    private static async Task Media(ActualApi api, string fake, string dir, Scenario scenario, Func<ConsumerHarness, object, string, string, Task> body, bool retained = false)
    {
        string root = Path.Combine(dir, "input"); Directory.CreateDirectory(root);
        string path = SamplerCases.Source(root, "audio", scenario), workspace = Path.Combine(dir, "w");
        var client = api.Client(fake, workspace); var h = new ConsumerHarness(api);
        try { await body(h, h.Consumer(api.Sampler(client)), path, workspace); }
        finally { await ActualApi.Dispose(client); Owned.Gone(dir); }
        if (!retained) Check.That(Directory.GetFileSystemEntries(workspace).Length == 0, "Unexpected retained media task");
    }
    private static async Task<List<object>> Index(ActualApi api, object service, object request, List<object>? progress = null)
    {
        progress ??= []; var sink = progress;
        var proxy = TestProxy.Create(typeof(IProgress<>).MakeGenericType(api.Type("IndexProgress")), (_, args) => { sink.Add(args[0]!); return null; });
        await ActualApi.Await(ActualApi.Call(service, "IndexAsync", request, proxy, CancellationToken.None)!);
        return progress;
    }
    private static void VerifyProgress(List<object> progress, string state, bool finished)
    {
        var terminal = progress.Single(p => ActualApi.Get<string>(p, "Phase") == state);
        Check.That(ActualApi.Get<int>(terminal, "Completed") == 1 && ActualApi.Get<int>(terminal, "Failed") == (state == "failed" ? 1 : 0)
            && ActualApi.Get<int>(terminal, "Partial") == (state == "partial" ? 1 : 0), "Truthful terminal progress counts");
        Check.That(progress.Any(p => ActualApi.Get<string>(p, "Phase") == "finished") == finished, "Dependency completion signal changed");
    }
    private static void VerifyRow(object row, string source, string state, int count, double expectedEnd = 30)
    {
        ConsumerHarness.State(row, state, false);
        Check.That(ActualApi.Get<string>(row, "Path") == source, "Legitimate original source mapping must remain exact");
        var segments = Segments(row); Check.That(segments.Length == count, "Valid vector retention/count changed");
        for (int i = 0; i < segments.Length; i++)
        {
            float[] vector = ActualApi.Get<float[]>(segments[i], "Vector");
            Check.That(vector.Length == 768 && vector[0] == 1 && vector.Skip(1).All(v => v == 0), "Previously valid vector altered");
            Check.That(ActualApi.Get<double>(segments[i], "Location") == 0 && ActualApi.Get<double>(segments[i], "LocationEnd") == expectedEnd, "Original source interval altered");
            Check.That(ActualApi.Get(segments[i], "Excerpt") is null, "Media excerpt must remain null");
        }
        string detail = ActualApi.Get<string?>(row, "Detail") ?? "";
        Check.That(!string.IsNullOrWhiteSpace(detail) && Regex.IsMatch(detail, "embedding|native|media|decode|protocol|cleanup|IOException", RegexOptions.IgnoreCase)
            && Regex.IsMatch(detail, "error|fail|invalid|exception|retai", RegexOptions.IgnoreCase), "Failure needs meaningful category/stage, not empty redaction");
    }
    private static object[] Segments(object row) => ((System.Collections.IEnumerable)ActualApi.Get(row, "Segments")!).Cast<object>().ToArray();
    private static bool Has(string value, string part) => value.Contains(part, StringComparison.OrdinalIgnoreCase);
    private static bool IsDependency(Exception e) => e.GetType().Name == "EmbeddingDependencyException";
    private static IEnumerable<Exception> Tree(Exception e)
    {
        yield return e;
        IEnumerable<Exception> children = e is AggregateException a ? a.InnerExceptions : e.InnerException is Exception one ? new[] { one } : Array.Empty<Exception>();
        foreach (var inner in children)
            foreach (var nested in Tree(inner)) yield return nested;
    }
    private static string[] Forms(string path)
    {
        string forward = path.Replace('\\', '/'), back = path.Replace('/', '\\');
        return [path, forward, back, path.ToUpperInvariant(), forward.ToUpperInvariant(), back.Replace("\\", "\\\\", StringComparison.Ordinal),
            JsonSerializer.Serialize(path)[1..^1], JsonSerializer.Serialize(forward)[1..^1], Uri.EscapeDataString(path), new Uri(path).AbsoluteUri];
    }
    private static string Echo(string actualInput, string workspace, bool encodedOnly)
    {
        string output = Path.GetDirectoryName(actualInput)!, task = Path.GetDirectoryName(output)!;
        var paths = new[] { actualInput, output, task, workspace };
        return Canary + " cannot read " + string.Join(" | ", paths.SelectMany(p => encodedOnly ? Forms(p).Skip(3) : Forms(p)));
    }
    private static void Closed(object row, string workspace, IEnumerable<string> artifacts)
    {
        string[] values = new[] { ActualApi.Get<string?>(row, "Detail"), ActualApi.Get<string?>(row, "Info") }
            .Concat(Segments(row).Select(s => ActualApi.Get<string?>(s, "Excerpt"))).Where(s => s is not null).Cast<string>().ToArray();
        var paths = artifacts.SelectMany(p => new[] { p, Path.GetDirectoryName(p)!, Path.GetDirectoryName(Path.GetDirectoryName(p)!)! }).Append(workspace).Distinct();
        foreach (string value in values)
        {
            Check.That(!value.Contains(Canary, StringComparison.OrdinalIgnoreCase), "NM-N2B1R-01 persisted untrusted freeform callback/worker message");
            foreach (string path in paths)
            foreach (string form in Forms(path))
                Check.That(!value.Contains(form, StringComparison.OrdinalIgnoreCase), "NM-N2B1R-01 persisted temporary path form: " + form);
        }
    }
    private static void Settled(ConsumerHarness h, string source, string workspace, string dir)
    {
        Check.That(h.Inputs.All(i => !File.Exists(ActualApi.Get<string>(i, "Value"))) && Directory.GetFileSystemEntries(workspace).Length == 0, "Completed leases/task files not cleaned");
        using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Owned.Gone(dir);
    }
    private static void Receipt(string dir, object row, string source, string workspace, ConsumerHarness h, string? echo, Exception? error, List<object> progress)
    {
        // Retention intentionally pins the case ancestor; evidence publication must not rename inside it.
        string evidence = Path.Combine(dir, "e"); Directory.CreateDirectory(evidence);
        Owned.Atomic(Path.Combine(evidence, "redaction-repro.json"), new
        {
            source, sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))), workspace,
            inputs = h.Inputs.Select(i => ActualApi.Get<string>(i, "Value")).ToArray(), echo,
            state = ActualApi.Get<string>(row, "State"), storedDetail = ActualApi.Get<string?>(row, "Detail"), storedInfo = ActualApi.Get<string?>(row, "Info"),
            segments = Segments(row).Select(s => new { excerpt = ActualApi.Get<string?>(s, "Excerpt"), start = ActualApi.Get<double>(s, "Location"), end = ActualApi.Get<double>(s, "LocationEnd") }).ToArray(),
            exception = error?.ToString(), h.EmbeddingCalls, progress = progress.Select(p => new { phase = ActualApi.Get<string>(p, "Phase"), completed = ActualApi.Get<int>(p, "Completed"), failed = ActualApi.Get<int>(p, "Failed"), partial = ActualApi.Get<int>(p, "Partial") }).ToArray(),
            requests = SamplerCases.Requests(Path.GetDirectoryName(source)!), retained = Directory.Exists(workspace) && Directory.GetFileSystemEntries(workspace).Length > 0
        });
    }
}
