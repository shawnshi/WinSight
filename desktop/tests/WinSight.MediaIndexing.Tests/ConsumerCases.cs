using System.Reflection;
using System.Text.Json;

namespace WinSight.MediaIndexing.Tests;

internal sealed class ConsumerHarness
{
    internal const string Fingerprint = "native-extraction-v1|documents1800-overlap200-pdfpig0.1.16-imagehost";
    internal readonly Dictionary<string, string> Metadata = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, object> Files = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> Writes = [];
    internal readonly List<object> Inputs = [];
    internal int ExtractCalls;
    internal int EmbeddingCalls;
    internal string? EmbeddingFailure;
    internal Func<object, int, string?>? ItemError;
    internal string[] Capabilities = ["image", "audio", "text"];
    internal Func<object, CancellationToken, Task>? DuringEmbedding;
    internal string? SpoofPath;
    private readonly ActualApi api;
    internal ConsumerHarness(ActualApi api) { this.api = api; }
    private object Completed(MethodInfo method, object? value) => TestProxy.TaskOf(method.ReturnType.GetGenericArguments()[0], value);
    internal object Repository() => TestProxy.Create(api.Type("IIndexRepository"), (method, args) => method.Name switch
    {
        "get_DatabasePath" => "synthetic-in-memory-only",
        "InitializeAsync" => Task.CompletedTask,
        "ReadFilesAsync" => Completed(method, TestProxy.ArrayOf(api.Type("IndexedFile"), Files.Values)),
        "GetFileAsync" => Completed(method, Files.GetValueOrDefault(Path.GetFullPath((string)args[0]!))),
        "GetMetaAsync" => Completed(method, Metadata.GetValueOrDefault((string)args[0]!)),
        "UpsertFileAsync" => WriteFile(args[0]!),
        "SetMetaAsync" => SetMeta((string)args[0]!, (string)args[1]!),
        "DeleteFileAsync" => Delete((string)args[0]!),
        "DisposeAsync" => ValueTask.CompletedTask,
        _ => throw new NotSupportedException("Unexpected repository call " + method.Name)
    });
    private Task WriteFile(object file) { string path = ActualApi.Get<string>(file, "Path"); Writes.Add("file:" + ActualApi.Get<string>(file, "State")); Files[Path.GetFullPath(path)] = file; return Task.CompletedTask; }
    private Task SetMeta(string key, string value) { Writes.Add("meta:" + key); Metadata[key] = value; return Task.CompletedTask; }
    private Task Delete(string path) { Writes.Add("delete"); Files.Remove(Path.GetFullPath(path)); return Task.CompletedTask; }
    internal object Extractor() => TestProxy.Create(api.Type("IContentExtractor"), (method, _) =>
    {
        if (method.Name == "CanExtract") return true;
        if (method.Name != "ExtractAsync") throw new NotSupportedException(method.Name);
        ExtractCalls++;
        object segment = api.New("IndexSegment", SpoofPath is null ? "text" : "frame", SpoofPath is null ? "text" : "image", SpoofPath is null ? null : .5, SpoofPath is null ? null : 4d, SpoofPath ?? "synthetic prose only", null);
        var result = api.New("ExtractionResult", TestProxy.ArrayOf(api.Type("IndexSegment"), [segment]), "done", null, JsonSerializer.Serialize(new { artifactPath = SpoofPath, native_media_revision = "native-media-sampling-v1" }));
        return Completed(method, result);
    });
    internal object Embeddings() => TestProxy.Create(api.Type("IEmbeddingService"), (method, args) =>
    {
        if (method.Name == "DisposeAsync") return ValueTask.CompletedTask;
        if (method.Name == "GetInfoAsync") return Completed(method, api.New("EmbeddingInfo", "synthetic-profile", 768, "fake", "synthetic-only", "1", "cpu", "test", Capabilities));
        if (method.Name != "EmbedAsync") throw new NotSupportedException(method.Name);
        var values = ((System.Collections.IEnumerable)args[0]!).Cast<object>().ToArray(); var ct = (CancellationToken)args[1]!;
        return typeof(ConsumerHarness).GetMethod(nameof(TypedTask), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null, [Embed(values, ct)])!;
    });
    private static async Task<T> TypedTask<T>(Task<object> source) => (T)await source;
    private async Task<object> Embed(object[] values, CancellationToken ct)
    {
        EmbeddingCalls++; var results = new List<object>();
        foreach (object input in values)
        {
            Inputs.Add(input); string operation = ActualApi.Get<string>(input, "Operation"), path = ActualApi.Get<string>(input, "Value");
            Check.That(Capabilities.Contains(operation), "Consumer invoked unavailable capability");
            if (operation is "audio" or "image")
            {
                Check.That(File.Exists(path), "Lease gone before embedding await");
                if (operation == "audio") Check.That(Wave.Read(path).Length > 0, "Empty audio embedding input");
            }
            if (DuringEmbedding is not null) await DuringEmbedding(input, ct);
            ct.ThrowIfCancellationRequested();
            if (EmbeddingFailure == "dependency" && EmbeddingCalls >= 2) throw new IOException("Synthetic embedding dependency failure");
            float[] vector = new float[768]; vector[0] = 1;
            string? itemError = ItemError?.Invoke(input, EmbeddingCalls);
            bool error = itemError is not null || EmbeddingFailure == "item" && EmbeddingCalls >= 2;
            results.Add(api.New("EmbeddingResult", error ? null : vector, itemError ?? (error ? "Synthetic item error" : null)));
        }
        return TestProxy.ArrayOf(api.Type("EmbeddingResult"), results);
    }
    internal object Request(string root, string[]? excluded = null, bool force = false, bool code = false, string[]? skip = null) => api.New("IndexRequest", new[] { root }, excluded, skip, code, force, 32L * 1024 * 1024);
    internal object Consumer(object? sampler, bool noEmbeddings = false) => sampler is null ? api.New("Indexing.IndexingService", Repository(), Extractor(), noEmbeddings ? null : Embeddings()) : api.New("Indexing.IndexingService", Repository(), Extractor(), noEmbeddings ? null : Embeddings(), sampler);
    internal static async Task Index(object service, object request, CancellationToken ct = default) => await ActualApi.Await(ActualApi.Call(service, "IndexAsync", request, null, ct)!);
    internal object Row(string path) => Files[Path.GetFullPath(path)];
    internal void Seed(string path, string kind, string? info)
    {
        var f = new FileInfo(path); Metadata["indexing_fingerprint"] = Fingerprint; Metadata["profile_id"] = "synthetic-profile";
        Files[Path.GetFullPath(path)] = api.New("IndexedFile", 7L, path, kind, f.Length, (f.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds, "done", null, info, TestProxy.ArrayOf(api.Type("IndexSegment"), []));
    }
    internal static void State(object row, string expected, bool revision)
    {
        Check.That(ActualApi.Get<string>(row, "State") == expected, "Expected " + expected + ", actual " + ActualApi.Get<string>(row, "State"));
        string? info = ActualApi.Get<string?>(row, "Info"); bool marked = info is not null && JsonDocument.Parse(info).RootElement.TryGetProperty("native_media_revision", out var r) && r.GetString() == "native-media-sampling-v1";
        Check.That(marked == revision, "Revision must mark complete done/skipped only");
    }
}

internal static class ConsumerCases
{
    private static string Create(string dir, string kind, Scenario scenario)
    {
        string root = Path.Combine(dir, "input"); Directory.CreateDirectory(root);
        string path = Path.Combine(root, kind == "audio" ? "s.wav" : "s.mp4"); File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(dir, "fixture.json"), JsonSerializer.Serialize(scenario)); return path;
    }
    private static async Task With(ActualApi api, string fake, string dir, string kind, Scenario scenario, Func<ConsumerHarness, object, string, object, Task> test, int budget = 5000, bool noEmbeddings = false)
    {
        string path = Create(dir, kind, scenario); var client = api.Client(fake, Path.Combine(dir, "w")); var harness = new ConsumerHarness(api);
        try { var sampler = api.Sampler(client, budget); await test(harness, harness.Consumer(sampler, noEmbeddings), path, sampler); }
        finally { await ActualApi.Dispose(client); } Owned.Gone(dir);
    }
    internal static async Task Run(Run run, ActualApi api, string fake)
    {
        foreach (string kind in new[] { "audio", "video" })
            await run.Case("actual/index/full/" + kind, dir => With(api, fake, dir, kind, new(10), async (h, consumer, path, _) =>
            {
                await ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!)); var row = h.Row(path); ConsumerHarness.State(row, "done", true);
                var segments = ((System.Collections.IEnumerable)ActualApi.Get(row, "Segments")!).Cast<object>().ToArray();
                Check.That(segments.Length >= (kind == "video" ? 3 : 1), "Missing media modalities");
                foreach (var segment in segments)
                {
                    string modality = ActualApi.Get<string>(segment, "Modality"); var vector = ActualApi.Get<float[]>(segment, "Vector");
                    Check.That(vector.Length == 768 && vector[0] == 1 && vector.Skip(1).All(f => f == 0), "Fake embedding vector not preserved");
                    Check.That(ActualApi.Get(segment, "Excerpt") is null && ActualApi.Get<double>(segment, "LocationEnd") > ActualApi.Get<double>(segment, "Location"), "Media locators/excerpt");
                    Check.That(ActualApi.Get<string>(segment, "Kind") == (modality == "audio" ? "audio" : "frame"), "Indexed kind/modality");
                }
                Check.That(h.ExtractCalls == 0, "Candidate must not trust third-party extractor media artifacts");
                Check.That(h.Inputs.All(i => !File.Exists(ActualApi.Get<string>(i, "Value"))), "Consumed leases were not released after await");
            }));
        foreach (string missing in new[] { "audio", "image", "all" })
            await run.Case("actual/index/capability/" + missing, async dir =>
            {
                string path = Create(dir, "video", new(10)); var c = api.Client(fake, Path.Combine(dir, "w")); var h = new ConsumerHarness(api) { Capabilities = missing == "all" ? [] : missing == "audio" ? ["image"] : ["audio"] };
                try { await ConsumerHarness.Index(h.Consumer(api.Sampler(c)), h.Request(Path.GetDirectoryName(path)!)); ConsumerHarness.State(h.Row(path), "partial", false); }
                finally { await ActualApi.Dispose(c); } Owned.Gone(dir);
                var req = Requests(dir); Check.That(missing is not ("audio" or "all") || !req.Any(r => r.GetProperty("operation").GetString() == "extract-soundtrack"), "Unavailable audio decoded");
                Check.That(missing is not ("image" or "all") || !req.Any(r => r.GetProperty("request").GetProperty("kind").GetString() == "frame"), "Unavailable image decoded");
            });
        await run.Case("actual/index/no-embedding", dir => With(api, fake, dir, "audio", new(10), async (h, consumer, path, _) =>
        { await ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!)); ConsumerHarness.State(h.Row(path), "partial", false); Check.That(h.EmbeddingCalls == 0, "Fake filename embeddings"); }, noEmbeddings: true));
        await run.Case("actual/index/budget", dir => With(api, fake, dir, "audio", new(82), async (h, consumer, path, _) =>
        { await ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!)); ConsumerHarness.State(h.Row(path), "partial", false); }, 2));
        await run.Case("actual/index/decoder-after-valid", dir => With(api, fake, dir, "audio", new(32, "decodeerror"), async (h, consumer, path, _) =>
        { await ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!)); ConsumerHarness.State(h.Row(path), "partial", false); Check.That(h.Inputs.Count == 1, "Prior successful sample lost"); }));
        foreach (string failure in new[] { "item", "dependency" })
            await run.Case("actual/index/embedding/" + failure, dir => With(api, fake, dir, "audio", new(32), async (h, consumer, path, _) =>
            {
                h.EmbeddingFailure = failure;
                if (failure == "dependency") await Check.Throws(() => ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!)), e => e.GetType().Name == "EmbeddingDependencyException");
                else { await ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!)); ConsumerHarness.State(h.Row(path), "partial", false); }
            }));
        foreach (string revision in new[] { "absent", "old", "current" })
            await run.Case("actual/index/revision/" + revision, dir => With(api, fake, dir, "audio", new(2), async (h, consumer, path, _) =>
            {
                h.Seed(path, "audio", revision == "absent" ? null : JsonSerializer.Serialize(new { native_media_revision = revision == "current" ? "native-media-sampling-v1" : "old" }));
                await ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!));
                Check.That((h.EmbeddingCalls > 0) == (revision != "current"), "Only stale media done records must reprocess"); ConsumerHarness.State(h.Row(path), "done", true);
            }));
        foreach (string mismatch in new[] { "fingerprint", "profile" })
            foreach (bool force in new[] { false, true })
                await run.Case($"actual/index/guard/{mismatch}/{force}", dir => With(api, fake, dir, "audio", new(2), async (h, consumer, path, _) =>
                {
                    h.Metadata["indexing_fingerprint"] = mismatch == "fingerprint" ? "wrong" : ConsumerHarness.Fingerprint; h.Metadata["profile_id"] = mismatch == "profile" ? "wrong" : "synthetic-profile";
                    await Check.Throws(() => ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!, force: force)), e => e is InvalidDataException);
                    Check.That(h.Writes.Count == 0 && h.Inputs.Count == 0 && Requests(dir).Length == 0, "Mismatch must precede ANY writes/child/embedding even Force");
                }));
        foreach (string policy in new[] { "excluded", "skiptype", "code" })
            await run.Case("actual/index/policy/" + policy, dir => With(api, fake, dir, "audio", new(2), async (h, consumer, path, _) =>
            {
                await ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!, excluded: policy == "excluded" ? [path] : null, skip: policy == "skiptype" ? ["wav"] : null, code: policy == "code"));
                Check.That(Requests(dir).Length == 0 && h.Inputs.Count == 0, "Unauthorized media must not start decoder/embed");
            }));
        foreach (string restricted in new[] { "secret.wav", ".env.wav", "node_modules/s.wav", ".data/s.wav" })
            await run.Case("actual/index/policy/" + restricted, async dir =>
            {
                string root = Path.Combine(dir, "input"); Directory.CreateDirectory(root); string path = Path.Combine(root, restricted.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [1, 2, 3, 4]);
                File.WriteAllText(Path.Combine(dir, "fixture.json"), JsonSerializer.Serialize(new Scenario(2)));
                var client = api.Client(fake, Path.Combine(dir, "w")); var h = new ConsumerHarness(api);
                try { await ConsumerHarness.Index(h.Consumer(api.Sampler(client)), h.Request(root)); Check.That(h.Inputs.Count == 0 && !Directory.GetDirectories(root, "q", SearchOption.AllDirectories).Any(), "Secret/generated folder started a child"); }
                finally { await ActualApi.Dispose(client); } Owned.Gone(dir);
            });
        await run.Case("actual/index/root-scope", async dir =>
        {
            string allowed = Path.Combine(dir, "input"); Directory.CreateDirectory(allowed); string outside = Path.Combine(dir, "outside.wav"); File.WriteAllBytes(outside, [1]);
            var client = api.Client(fake, Path.Combine(dir, "w")); var h = new ConsumerHarness(api);
            try { await ConsumerHarness.Index(h.Consumer(api.Sampler(client)), h.Request(allowed)); Check.That(h.Files.Count == 0 && h.Inputs.Count == 0 && !Directory.Exists(Path.Combine(dir, "q")), "Root authority escaped to sibling source"); }
            finally { await ActualApi.Dispose(client); }
        });
        await run.Case("actual/index/doc-unchanged", async dir =>
        {
            string root = Path.Combine(dir, "input"); Directory.CreateDirectory(root); string path = Path.Combine(root, "s.txt"); File.WriteAllText(path, "synthetic unchanged prose");
            var client = api.Client(fake, Path.Combine(dir, "w")); var h = new ConsumerHarness(api); h.Seed(path, "doc", null);
            try { await ConsumerHarness.Index(h.Consumer(api.Sampler(client)), h.Request(root)); Check.That(h.ExtractCalls == 0 && h.Inputs.Count == 0 && !h.Writes.Any(w => w.StartsWith("file:", StringComparison.Ordinal)), "Native revision incorrectly invalidated unchanged doc"); }
            finally { await ActualApi.Dispose(client); }
        });
        foreach (string mode in new[] { "silent", "r98" })
            await run.Case("actual/index/skipped-complete/" + mode, dir => With(api, fake, dir, "audio", new(2, mode), async (h, consumer, path, _) =>
            { await ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!)); ConsumerHarness.State(h.Row(path), "skipped", true); Check.That(h.Inputs.Count == 0, "Silent sample embedded"); }));
        await run.Case("actual/index/cancel-controlled-await", dir => With(api, fake, dir, "audio", new(2), async (h, consumer, path, _) =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            h.DuringEmbedding = async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); };
            var index = ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!), cancel.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(6)); cancel.Cancel();
            await Check.Throws(async () => await index.WaitAsync(TimeSpan.FromSeconds(6)), e => e is OperationCanceledException);
            Check.That(!h.Files.Values.Any(r => ActualApi.Get<string>(r, "State") is "done" or "skipped"), "Cancel covered media as complete");
            Check.That(h.Inputs.All(i => !File.Exists(ActualApi.Get<string>(i, "Value"))), "Cancel leaked sample lease");
        }));
        await run.Case("actual/index/spoof-four-ctor", dir => With(api, fake, dir, "audio", new(2), async (h, consumer, path, _) =>
        {
            string forged = Path.Combine(dir, "forged.wav"); Wave.Write(forged, 2); h.SpoofPath = forged;
            await ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!)); ConsumerHarness.State(h.Row(path), "done", true);
            Check.That(h.ExtractCalls == 0 && h.Inputs.All(i => ActualApi.Get<string>(i, "Value") != forged), "Third-party Info/Excerpt selected a candidate artifact");
        }));
        await run.Case("actual/index/spoof-three-ctor", async dir =>
        {
            string root = Path.Combine(dir, "input"); Directory.CreateDirectory(root); string path = Path.Combine(root, "s.wav"), forged = Path.Combine(dir, "forged.wav"); Wave.Write(path, 2); Wave.Write(forged, 2);
            var h = new ConsumerHarness(api) { SpoofPath = forged }; await ConsumerHarness.Index(h.Consumer(null), h.Request(root));
            Check.That(h.Inputs.All(i => ActualApi.Get<string>(i, "Value") != forged), "Untrusted extractor Excerpt/Info drove external artifact input");
            Check.That(!h.Files.Values.Any(r => ActualApi.Get<string>(r, "State") == "done"), "Old public constructor must not activate native sampling");
        });
        await run.Case("actual/index/controlled-await-lifetime", dir => With(api, fake, dir, "audio", new(2), async (h, consumer, path, _) =>
        {
            var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.DuringEmbedding = async (input, ct) => { string value = ActualApi.Get<string>(input, "Value"); Check.That(Directory.GetFiles(Path.Combine(dir, "w"), "*", SearchOption.AllDirectories).Count(f => Path.GetExtension(f) is ".wav" or ".png") <= 2, "Sampling accumulated unconsumed artifacts"); entered.SetResult(value); await release.Task.WaitAsync(ct); Check.That(File.Exists(value), "Artifact gone during embedding await"); };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var index = ConsumerHarness.Index(consumer, h.Request(Path.GetDirectoryName(path)!), deadline.Token);
            try { string artifact = await entered.Task.WaitAsync(TimeSpan.FromSeconds(6)); Check.That(File.Exists(artifact), "Await fixture missing"); SamplerCases.Blocked(path); SamplerCases.BlockedDirectory(Path.GetDirectoryName(path)!); }
            finally { release.TrySetResult(); }
            await index; ConsumerHarness.State(h.Row(path), "done", true);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }));
    }
    private static JsonElement[] Requests(string dir) => Directory.Exists(Path.Combine(dir, "input")) ? SamplerCases.Requests(Path.Combine(dir, "input")) : [];
}
