using System.Collections.Concurrent;
using System.ComponentModel;
using WinSight.Core;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Runtime;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;

internal static class WsRegressions
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string root, string? fakeHost)
    {
        Console.WriteLine("WS-RUNTIME-ARTIFACTS " + root);
        await test("WS-04 combined roots exclude revoked/skipped candidates before the result limit and isolate code.sqlite", async () =>
        {
            var fixture = Path.Combine(root, "ws-scope");
            var data = Path.Combine(fixture, "data");
            var first = Path.Combine(fixture, "first"); var second = Path.Combine(fixture, "second");
            var skipped = Path.Combine(first, "skipped"); var outside = Path.Combine(fixture, "revoked");
            var code = Path.Combine(fixture, "code");
            foreach (var folder in new[] { first, second, skipped, outside, code }) Directory.CreateDirectory(folder);
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [first, second], Skipped = [skipped], CodeFolders = [code], BackgroundIndexingEnabled = false
            });
            await using (var store = new SqliteIndexRepository(Path.Combine(data, "index", "index.sqlite")))
            {
                for (int i = 0; i < 150; i++) await store.UpsertFileAsync(Row(Path.Combine(outside, $"needle-{i}.txt"), "doc", "needle"));
                await store.UpsertFileAsync(Row(Path.Combine(skipped, "needle.txt"), "doc", "needle"));
                await store.UpsertFileAsync(Row(Path.Combine(first, "a.txt"), "doc", "needle evidence"));
                await store.UpsertFileAsync(Row(Path.Combine(second, "b.txt"), "doc", "needle evidence"));
            }
            await using (var store = new SqliteIndexRepository(Path.Combine(data, "index", "code.sqlite")))
                await store.UpsertFileAsync(Row(Path.Combine(code, "c.cs"), "code", "needle identifier"));
            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: false));
            var both = await backend.SearchAsync(new("needle", Mode: SearchMode.Keyword));
            Require(both.Hits.Count == 2 && both.Hits.All(h => h.Path.StartsWith(first) || h.Path.StartsWith(second)), "Combined search lost a root or exposed revoked content.");
            Require((await backend.SearchAsync(new("needle", 1, SearchMode.Keyword))).Hits.Count == 1, "Unauthorized hits crowded out the requested result.");
            var narrowed = await backend.SearchAsync(new("needle", Mode: SearchMode.Keyword, Root: second));
            Require(narrowed.Hits.Count == 1 && narrowed.Hits[0].Path.StartsWith(second), "Explicit root selection changed.");
            var source = await backend.SearchAsync(new("code: needle", Mode: SearchMode.Keyword));
            Require(source.Hits.Count == 1 && source.Hits[0].Path.StartsWith(code), "Code search crossed the document store.");
        });
        await test("WS-06 battery defers real watcher/index/SQLite work; AC transitions reconcile dirty files", async () =>
        {
            var fixture = Path.Combine(root, "ws-power-chain");
            var watched = Path.Combine(fixture, "watched"); Directory.CreateDirectory(watched);
            var file = Path.Combine(watched, "notes.txt");
            await File.WriteAllTextAsync(file, "医院医保支付改革 firstneedle");
            await using var repository = new SqliteIndexRepository(Path.Combine(fixture, "index.sqlite"));
            int ac = 0, runs = 0, reads = 0;
            var errors = new ConcurrentQueue<Exception>();
            await using var watcher = new BackgroundIndexCoordinator(new AppSettings { Folders = [watched], ProcessorOnly = true },
                async (request, token) =>
                {
                    await new IndexingService(repository, new ContentExtractor()).IndexAsync(request, cancellationToken: token);
                    Interlocked.Increment(ref runs);
                }, errors.Enqueue, TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1),
                isOnAcPower: () => { Interlocked.Increment(ref reads); return Volatile.Read(ref ac) == 1; }, policyPoll: TimeSpan.FromMilliseconds(20));
            await Until(() => Volatile.Read(ref reads) > 2);
            Require(Volatile.Read(ref runs) == 0, "Automatic indexing ran on battery.");
            Volatile.Write(ref ac, 1);
            await Until(() => Volatile.Read(ref runs) >= 1);
            var search = new SearchService(repository);
            Require((await search.SearchAsync(new("医保", Mode: SearchMode.Keyword))).Hits.Count == 1, "Watcher -> extractor -> SQLite -> CJK search chain failed.");
            Volatile.Write(ref ac, 0);
            int previousReads = Volatile.Read(ref reads);
            await Until(() => Volatile.Read(ref reads) > previousReads + 2);
            int before = Volatile.Read(ref runs);
            await File.WriteAllTextAsync(file, "医院医保支付改革 batteryneedle");
            await Task.Delay(100);
            Require(Volatile.Read(ref runs) == before, "Battery file event started indexing.");
            Require((await search.SearchAsync(new("batteryneedle", Mode: SearchMode.Keyword))).Hits.Count == 0, "Blocked automatic update was committed.");
            Volatile.Write(ref ac, 1);
            await Until(() => Volatile.Read(ref runs) > before);
            Require((await search.SearchAsync(new("batteryneedle", Mode: SearchMode.Keyword))).Hits.Count == 1, "AC reconnect did not reconcile a previously drained watcher event.");
            Require(errors.IsEmpty && !watcher.Completion.IsFaulted, "Power transitions faulted the watcher.");
        });
        await test("WS-06 disconnect cancels and settles active automatic work before AC resume", async () =>
        {
            var watched = Path.Combine(root, "ws-active-power"); Directory.CreateDirectory(watched);
            int ac = 1, runs = 0, active = 0, maximum = 0;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new ConcurrentQueue<Exception>();
            await using var watcher = new BackgroundIndexCoordinator(new AppSettings { Folders = [watched], ProcessorOnly = true },
                async (_, token) =>
                {
                    maximum = Math.Max(maximum, Interlocked.Increment(ref active));
                    try
                    {
                        if (Interlocked.Increment(ref runs) == 1)
                        {
                            started.TrySetResult();
                            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.TrySetResult(); throw; }
                        }
                        else resumed.TrySetResult();
                    }
                    finally { Interlocked.Decrement(ref active); }
                }, errors.Enqueue, TimeSpan.FromMilliseconds(10), TimeSpan.FromHours(1),
                isOnAcPower: () => Volatile.Read(ref ac) == 1, policyPoll: TimeSpan.FromMilliseconds(20));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref ac, 0); await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => Volatile.Read(ref active) == 0);
            Volatile.Write(ref ac, 1); await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(maximum == 1 && errors.IsEmpty, "Cancellation overlapped a resumed run or was misreported as an indexing error.");
        });
        await test("WS-06 failed/unknown power fails closed and recovers; opting out never probes power", async () =>
        {
            var watched = Path.Combine(root, "ws-power-errors"); Directory.CreateDirectory(watched);
            int state = 0, runs = 0;
            var errors = new ConcurrentQueue<Exception>();
            await using (var watcher = new BackgroundIndexCoordinator(new AppSettings { Folders = [watched], ProcessorOnly = true },
                (_, _) => { Interlocked.Increment(ref runs); return Task.CompletedTask; }, errors.Enqueue,
                TimeSpan.FromMilliseconds(10), TimeSpan.FromHours(1),
                isOnAcPower: () => Volatile.Read(ref state) == 0 ? throw new Win32Exception(5, "synthetic unavailable power status") : Volatile.Read(ref state) == 2,
                policyPoll: TimeSpan.FromMilliseconds(20)))
            {
                await Until(() => !errors.IsEmpty);
                Require(runs == 0 && !watcher.Completion.IsFaulted, "Power read failure ran work or terminated coordination.");
                Volatile.Write(ref state, 1); await Task.Delay(80);
                Require(runs == 0, "Unknown/non-AC power ran automatic work.");
                Volatile.Write(ref state, 2); await Until(() => Volatile.Read(ref runs) > 0);
            }
            var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var optOut = new BackgroundIndexCoordinator(new AppSettings { Folders = [watched], ProcessorOnly = false },
                (_, _) => { indexed.TrySetResult(); return Task.CompletedTask; }, errors.Enqueue, TimeSpan.FromMilliseconds(10),
                isOnAcPower: () => throw new InvalidOperationException("Opt-out must not query the device."));
            await indexed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
        await test("WS-06 explicit backend indexing still works with AC-only enabled and an injected battery", async () =>
        {
            fakeHost ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../WinSight.Inference.Tests/FakeHost/bin/Debug/net11.0/FakeHost.exe"));
            if (!File.Exists(fakeHost)) throw new FileNotFoundException("Build/pass the task-owned inference fake host before runtime integration.", fakeHost);
            var fixture = Path.Combine(root, "ws-manual");
            var data = Path.Combine(fixture, "data"); var documents = Path.Combine(fixture, "documents");
            var models = Path.Combine(fixture, "success fake models");
            Directory.CreateDirectory(documents); Directory.CreateDirectory(models);
            await File.WriteAllTextAsync(Path.Combine(documents, "notes.txt"), "医院医保支付改革 manualneedle");
            var manifest = Path.Combine(models, "manifest with spaces &.json"); await File.WriteAllTextAsync(manifest, "{}");
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [documents], SetUp = true, BackgroundIndexingEnabled = true, ProcessorOnly = true,
                ModelDirectory = models, ManifestPath = manifest, InferenceExecutable = fakeHost
            });
            int reads = 0;
            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data)
            {
                IsOnAcPower = () => { Interlocked.Increment(ref reads); return false; }
            });
            await Until(() => Volatile.Read(ref reads) > 0);
            await Task.Delay(600);
            Require(!File.Exists(Path.Combine(models, "audit.jsonl")), "Automatic battery work started the inference host.");
            await backend.IndexAsync(new IndexRequest([documents]));
            var result = await backend.SearchAsync(new("医保", Mode: SearchMode.Keyword));
            Require(result.Hits.Count == 1 && File.Exists(Path.Combine(models, "audit.jsonl")), "Explicit backend index was blocked or failed to use the controlled host.");
        });
    }

    private static IndexedFile Row(string path, string kind, string text) => new(0, path, kind, 10, 1, "done", null, null, [new("chunk", "none", 1, null, text)]);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
