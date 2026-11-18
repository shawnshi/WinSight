using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Inference;
using WinSight.Infrastructure.Runtime;
using WinSight.Infrastructure.Storage;

internal static class ReviewRegressions
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, string root)
    {
        await test("NM-BR-01-HARD-EXCLUSIONS", async () =>
        {
            var parent = Path.Combine(root, "hard-exclusions");
            var data = Path.Combine(parent, "native-data");
            var index = Path.Combine(parent, "native-index");
            var fixture = Path.Combine(data, "nested", "synthetic.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
            await File.WriteAllTextAsync(fixture, "hardguardmarker");
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [parent], IndexDirectory = index, SetUp = true
            });
            await SeedAsync(Path.Combine(index, "index.sqlite"), fixture, "hardguardmarker");
            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: false));
            await Throws<UnauthorizedAccessException>(() => backend.IndexAsync(new IndexRequest([data])));
            await Throws<UnauthorizedAccessException>(() => backend.IndexAsync(new IndexRequest([Path.GetDirectoryName(fixture)!])));
            await Throws<UnauthorizedAccessException>(() => backend.IndexAsync(new IndexRequest([parent, index])));
            await Throws<UnauthorizedAccessException>(() => backend.SearchAsync(new SearchRequest("hardguardmarker",
                Mode: SearchMode.Keyword, Root: data)));
            var response = await backend.SearchAsync(new SearchRequest("hardguardmarker", Mode: SearchMode.Keyword));
            Check(response.Hits.Count == 0, "Previously stored application-data content remained searchable.");
            Check(Field(backend, "embeddings") is null, "Rejected hard-excluded roots started a model client.");
        });
        await test("NM-BR-02-ACTIVE-DOMAIN-AND-NESTED-CHOICE", async () =>
        {
            var parent = Path.Combine(root, "domain-policy");
            var data = Path.Combine(parent, "appdata");
            var documents = Path.Combine(parent, "documents");
            var skipped = Path.Combine(documents, "skipped");
            var chosen = Path.Combine(skipped, "chosen");
            var privateFile = Path.Combine(skipped, "private.txt");
            var chosenFile = Path.Combine(chosen, "allowed.txt");
            Directory.CreateDirectory(chosen);
            await File.WriteAllTextAsync(privateFile, "domainprivacy");
            await File.WriteAllTextAsync(chosenFile, "nestedallowed");
            var settings = new AppSettings { Folders = [documents], Skipped = [skipped], CodeFolders = [skipped], SetUp = true };
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(settings);
            await SeedAsync(Path.Combine(data, "index", "index.sqlite"), privateFile, "domainprivacy");
            await SeedAsync(Path.Combine(data, "index", "index.sqlite"), chosenFile, "nestedallowed");
            await SeedAsync(Path.Combine(data, "index", "code.sqlite"), privateFile, "domainprivacy", code: true);
            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: false));
            Check((await backend.SearchAsync(new SearchRequest("domainprivacy", Mode: SearchMode.Keyword))).Hits.Count == 0,
                "Code authorization overrode a document exclusion.");
            Check((await backend.SearchAsync(new SearchRequest("code: domainprivacy", Mode: SearchMode.Keyword))).Hits.Any(hit => hit.Path == privateFile),
                "Document skips incorrectly denied the independently authorized code domain.");
            await Throws<UnauthorizedAccessException>(() => backend.IndexAsync(new IndexRequest([skipped])));
            settings.Folders.Add(chosen);
            await backend.SaveSettingsAsync(settings);
            var nested = await backend.SearchAsync(new SearchRequest("nestedallowed", Mode: SearchMode.Keyword, Root: chosen));
            Check(nested.Hits.Any(hit => hit.Path == chosenFile), "A configured nested root did not override its ancestor skip.");
        });
        await test("NM-BR-05-DATABASE-FAULT-KEEPS-WATCHER-ALIVE", async () =>
        {
            var watched = Path.Combine(root, "watcher-db-fault");
            Directory.CreateDirectory(watched);
            var errorObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            await using var watcher = new BackgroundIndexCoordinator(new AppSettings { Folders = [watched], ProcessorOnly = false },
                (request, token) =>
                {
                    if (Interlocked.Increment(ref calls) == 1) throw new SyntheticDbException("synthetic database lock");
                    recovered.TrySetResult();
                    return Task.CompletedTask;
                }, error =>
                {
                    Check(error is DbException, "Database fault was not reported truthfully.");
                    errorObserved.TrySetResult();
                }, debounce: TimeSpan.FromMilliseconds(1));
            await errorObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(Path.Combine(watched, "changed.txt"), "synthetic");
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(watcher.Failure is null && !watcher.Completion.IsCompleted, "Database error permanently killed the worker.");
        });
        await test("NM-BR-05-FAULTED-WATCHER-STILL-RELEASES-ALL-RESOURCES", async () =>
        {
            var watched = Path.Combine(root, "faulted-watch-cleanup");
            Directory.CreateDirectory(watched);
            var backend = await DesktopBackend.CreateAsync(new DesktopOptions(Path.Combine(root, "cleanup-data"),
                EnableBackgroundIndexing: false));
            var watcher = new BackgroundIndexCoordinator(new AppSettings { Folders = [watched], ProcessorOnly = false },
                (request, token) => throw new FormatException("synthetic unexpected worker fault"), _ => { },
                debounce: TimeSpan.FromMilliseconds(1));
            await Throws<FormatException>(() => watcher.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            var first = new TrackingRepository();
            var second = new TrackingRepository { FailOnDispose = true };
            var model = new TrackingEmbeddings();
            ((List<IIndexRepository>)Field(backend, "repositories")!).AddRange([first, second]);
            SetField(backend, "embeddings", model);
            SetField(backend, "background", watcher);
            await Throws<AggregateException>(() => backend.DisposeAsync().AsTask());
            Check(first.Disposed && second.Disposed && model.Disposed, "Watcher/store failures skipped another resource's cleanup.");
            Check(Field(backend, "background") is null && Field(backend, "embeddings") is null,
                "Faulted resources remained attached to the backend.");
            await backend.DisposeAsync();
        });
        await test("NM-BR-06-READ-HOST-CANNOT-AUTOSTART-INDEXING", async () =>
        {
            var data = Path.Combine(root, "read-host");
            var watched = Path.Combine(root, "read-host-root");
            Directory.CreateDirectory(watched);
            await File.WriteAllTextAsync(Path.Combine(watched, "synthetic.txt"), "synthetic");
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [watched], SetUp = true, BackgroundIndexingEnabled = true
            });
            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: false));
            await backend.GetStatusAsync();
            await backend.SearchAsync(new SearchRequest("synthetic", Mode: SearchMode.Keyword));
            Check(Field(backend, "background") is null && Field(backend, "embeddings") is null,
                "Read-oriented host constructed background coordination or a model client.");
            var settings = await backend.LoadSettingsAsync();
            await backend.SaveSettingsAsync(settings);
            Check(Field(backend, "background") is null, "Saving settings re-enabled background indexing in a read host.");
        });
        await test("BACKFILL-CONCURRENT-WITH-ACTIVE-INDEXING", async () =>
        {
            var parent = Path.Combine(root, "backfill-concurrent");
            var data = Path.Combine(parent, "data");
            var documents = Path.Combine(parent, "docs");
            Directory.CreateDirectory(documents);
            var docPath = Path.Combine(documents, "pending.txt");
            await File.WriteAllTextAsync(docPath, "first chunk\nsecond chunk");
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [documents], SetUp = true, BackgroundIndexingEnabled = false
            });
            var dbPath = Path.Combine(data, "index", "index.sqlite");
            await using (var repo = new SqliteIndexRepository(dbPath))
            {
                await repo.InitializeAsync();
                await repo.UpsertFileAsync(new IndexedFile(0, docPath, "doc", 24, 1, "partial", null, null,
                [
                    new IndexSegment("chunk", "text", 1, 1, "first chunk"),
                    new IndexSegment("chunk", "text", 2, 2, "second chunk")
                ]));
            }

            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: false));
            var fakeEmbeddings = new FakeBackfillEmbeddings();
            SetField(backend, "embeddings", fakeEmbeddings);

            var controller = (IndexingController)Field(backend, "indexing")!;
            var acquireLock = typeof(DesktopBackend).GetMethod("AcquireRunLock", BindingFlags.NonPublic | BindingFlags.Instance)!;

            var indexingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseIndexing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var activeIndexing = controller.RunAsync(async token =>
            {
                using var runLock = (IDisposable?)acquireLock.Invoke(backend, null);
                Check(runLock is not null, "Initial active indexing failed to acquire run lock.");
                indexingEntered.SetResult();
                await releaseIndexing.Task.WaitAsync(token);
            }, CancellationToken.None);

            await indexingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(IndexRunLock.TryAcquire(data) is null, "External run lock was not held while indexing ran.");

            var reports = new List<(int Processed, int Remaining)>();
            var progress = new SyncProgress<(int Processed, int Remaining)>(p => { lock (reports) reports.Add(p); });

            var metricsTask = backend.GetStorageMetricsAsync();
            var backfillTask = backend.RunVectorBackfillAsync(0, progress);
            await Task.WhenAll(metricsTask, backfillTask).WaitAsync(TimeSpan.FromSeconds(5));
            int added = backfillTask.Result;
            Check(metricsTask.Result is not null, "GetStorageMetricsAsync returned null during concurrent backfill.");
            Check(added == 2, $"Expected 2 backfilled vectors while indexing held workGate, got {added}.");
            Check(!activeIndexing.IsCompleted, "Backfill waited for active indexing instead of running concurrently.");
            Check(IndexRunLock.TryAcquire(data) is null, "External run lock leaked after concurrent backfill ended.");
            lock (reports)
            {
                Check(reports.Contains((0, -1)) && reports.Any(r => r.Processed == 0 && r.Remaining == 2) && reports.Any(r => r.Processed == 2),
                    "Missing initial or completion progress reports: " + string.Join(",", reports));
            }

            // Pause must cancel both active indexing and any active concurrent participant cleanly.
            var concurrentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var concurrentBlocked = controller.RunConcurrentAsync(async token =>
            {
                using var runLock = (IDisposable?)acquireLock.Invoke(backend, null);
                Check(runLock is not null, "Concurrent participant failed to share run lock.");
                concurrentEntered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, CancellationToken.None);
            await concurrentEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await backend.SetIndexingPausedAsync(true);
            await Throws<OperationCanceledException>(() => activeIndexing);
            await Throws<OperationCanceledException>(() => concurrentBlocked);
            Check(controller.State == IndexingControlState.Paused && !controller.HasWork, "Controller remained busy after pause.");

            using (var external = IndexRunLock.TryAcquire(data))
            {
                Check(external is not null, "Cross-process lock was not released after all refcounted leases exited.");
            }
            await backend.SetIndexingPausedAsync(false);

            // BLK-04 + MED-02: Negative maxSegments rejected; embedding item error surfaces instead of silently returning 0.
            await Throws<ArgumentOutOfRangeException>(() => backend.RunVectorBackfillAsync(-1));
            var docPath2 = Path.Combine(documents, "err.txt");
            await File.WriteAllTextAsync(docPath2, "failing chunk");
            await using (var repo2 = new SqliteIndexRepository(dbPath))
            {
                await repo2.InitializeAsync();
                await repo2.UpsertFileAsync(new IndexedFile(0, docPath2, "doc", 13, 1, "partial",
                    "Semantic vectors sampled inline (64 chunks); remaining chunks queued for background vector backfill.", null,
                    [new IndexSegment("chunk", "text", 1, 1, "failing chunk")]));
            }
            fakeEmbeddings.FailMessage = "synthetic host failure";
            await Throws<InvalidOperationException>(() => backend.RunVectorBackfillAsync(0));
        });
        await test("R2-BLK-01-02-REBUILD-WITH-BACKGROUND-CODE-AND-BACKFILL-PREEMPTION", async () =>
        {
            var parent = Path.Combine(root, "r2-rebuild-bg");
            var data = Path.Combine(parent, "data");
            var docs = Path.Combine(parent, "docs");
            var code = Path.Combine(parent, "code");
            Directory.CreateDirectory(docs);
            Directory.CreateDirectory(code);
            var docFile = Path.Combine(docs, "guide.txt");
            var codeFile = Path.Combine(code, "worker.cs");
            await File.WriteAllTextAsync(docFile, "guide rebuild marker text");
            await File.WriteAllTextAsync(codeFile, "namespace Worker { class Run { } }");
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [docs], CodeFolders = [code], SetUp = true, BackgroundIndexingEnabled = true, ProcessorOnly = false
            });

            var indexDbPath = Path.Combine(data, "index", "index.sqlite");
            await using (var seed = new SqliteIndexRepository(indexDbPath))
            {
                await seed.InitializeAsync();
                await seed.SetMetaAsync("profile_id", "stale-profile-v0");
                await seed.UpsertFileAsync(new IndexedFile(0, docFile, "doc", 24, 1, "partial", null, null,
                    [new IndexSegment("chunk", "none", 1, null, "guide rebuild marker text")]));
            }

            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: true));
            var fake = new FakeBackfillEmbeddings();
            SetField(backend, "embeddings", fake);

            // R2-BLK-01: Rebuild triggered during a Code:true request must tear down & restore BackgroundIndexCoordinator without deadlock or disposed repos,
            // AND still write code.sqlite (stores[1]) rather than index.sqlite (stores[0]).
            await backend.IndexAsync(new IndexRequest([code], Code: true));
            var codeHits = await backend.SearchAsync(new SearchRequest("code: Worker", Mode: SearchMode.Keyword));
            Check(codeHits.Hits.Any(h => h.Path == codeFile), "Code request after generation rebuild did not index into code.sqlite.");
            var queueStatus = await backend.GetIndexingQueueStatusAsync();
            Check(queueStatus is not null, "Queue status unavailable after generation rebuild with background enabled.");

            var docStores = await ((Task<IReadOnlyList<IIndexRepository>>)typeof(DesktopBackend)
                .GetMethod("GetRepositoriesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(backend, [CancellationToken.None])!);
            Check(await docStores[0].GetMetaAsync("profile_id") == "fake-backfill-profile", "index.sqlite was not rebuilt to new profile.");

            // R2-BLK-02: Preemption by rebuild must cancel active backfill and propagate OperationCanceledException rather than returning 0/partial count.
            await docStores[0].UpsertFileAsync(new IndexedFile(0, docFile, "doc", 24, 1, "partial", null, null,
                [new IndexSegment("chunk", "none", 1, null, "pending vec after rebuild")]));

            var embedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.OnEmbed = async ct =>
            {
                embedEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            };
            var backfillTask = backend.RunVectorBackfillAsync(0);
            await embedEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var suppressMethod = typeof(DesktopBackend).GetMethod("SuppressAndCancelBackfill", BindingFlags.NonPublic | BindingFlags.Instance)!;
            using var preemption = (IDisposable)suppressMethod.Invoke(backend, null)!;
            await Throws<OperationCanceledException>(() => backfillTask);
        });
        await test("P0-P2-PREEMPT-SEARCH-BACKFILL-RETRY interactive SearchAsync preempts active Backfill which yields and completes", async () =>
        {
            var parent = Path.Combine(root, "p0-preempt-backfill");
            var data = Path.Combine(parent, "data");
            var docs = Path.Combine(parent, "docs");
            Directory.CreateDirectory(docs);
            var doc1 = Path.Combine(docs, "alpha.txt");
            await File.WriteAllTextAsync(doc1, "alpha interactive preemption test");
            var fi = new FileInfo(doc1);
            var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0;
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [docs], SetUp = true, BackgroundIndexingEnabled = true, ProcessorOnly = false
            });

            var dbPath = Path.Combine(data, "index", "index.sqlite");
            await using (var seed = new SqliteIndexRepository(dbPath))
            {
                await seed.InitializeAsync();
                await seed.SetMetaAsync("profile_id", "fake-backfill-profile");
                var segs = Enumerable.Range(1, 33).Select(i => new IndexSegment("chunk", "text", i, i, $"backfill chunk {i}")).ToArray();
                await seed.UpsertFileAsync(new IndexedFile(0, doc1, "doc", fi.Length, mtime, "partial", null, null, segs));
            }

            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: true) { IsOnAcPower = () => true });
            var waitUntilBackground = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < waitUntilBackground && Field(backend, "background") is null)
                await Task.Delay(25);
            Check(Field(backend, "background") is not null, "Background coordinator did not initialize.");

            var firstBatchBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int embedCalls = 0;
            var fake = new FakeBackfillEmbeddings();
            fake.OnEmbed = async ct =>
            {
                int call = Interlocked.Increment(ref embedCalls);
                if (call == 1)
                {
                    firstBatchBlocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
            };
            SetField(backend, "embeddings", fake);

            var backfillTask = backend.RunVectorBackfillAsync(0);
            await firstBatchBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var searchResp = await backend.SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Hybrid));
            Check(searchResp is not null, "Hybrid SearchAsync failed while backfill was preempted.");

            int backfilled = await backfillTask.WaitAsync(TimeSpan.FromSeconds(10));
            Check(backfilled == 33, $"Expected all 33 segments backfilled after yielding to SearchAsync, got {backfilled}.");
            Check(Volatile.Read(ref embedCalls) >= 3, $"Expected yielded chunk retry (>=3 embed calls: 1 preempted + 2 chunks), got {embedCalls}.");
        });
        await test("P0-P2-REAL-HOST-PREEMPTION-KEEPS-WARM-PID InferenceHostClient preemption keeps FakeHost PID warm without Reset()", async () =>
        {
            var fakeHost = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../WinSight.Inference.Tests/FakeHost/bin/Debug/net11.0/FakeHost.exe"));
            if (!File.Exists(fakeHost)) throw new FileNotFoundException("FakeHost.exe missing for runtime preemption test.", fakeHost);

            var parent = Path.Combine(root, "p0-real-host-preempt");
            var data = Path.Combine(parent, "data");
            var docs = Path.Combine(parent, "docs");
            var models = Path.Combine(parent, "delay fake models");
            Directory.CreateDirectory(docs);
            Directory.CreateDirectory(models);
            var manifest = Path.Combine(models, "manifest with spaces &.json");
            await File.WriteAllTextAsync(manifest, "{}");

            var doc1 = Path.Combine(docs, "warm-preempt.txt");
            await File.WriteAllTextAsync(doc1, "warm child host preemption test");
            var fi = new FileInfo(doc1);
            var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0;
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [docs], SetUp = true, BackgroundIndexingEnabled = true, ProcessorOnly = false,
                ModelDirectory = models, ManifestPath = manifest, InferenceExecutable = fakeHost
            });

            var fakeProfile = new string('a', 64);
            var dbPath = Path.Combine(data, "index", "index.sqlite");
            await using (var seed = new SqliteIndexRepository(dbPath))
            {
                await seed.InitializeAsync();
                await seed.SetMetaAsync("profile_id", fakeProfile);
                var segs = Enumerable.Range(1, 65).Select(i => new IndexSegment("chunk", "text", i, i, $"warm chunk {i}")).ToArray();
                await seed.UpsertFileAsync(new IndexedFile(0, doc1, "doc", fi.Length, mtime, "partial", null, null, segs));
            }

            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: true) { IsOnAcPower = () => true });
            var waitUntilBackground = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < waitUntilBackground && Field(backend, "background") is null)
                await Task.Delay(25);
            Check(Field(backend, "background") is not null, "Background coordinator did not initialize.");

            var chunkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new SyncProgress<(int Processed, int Remaining)>(p =>
            {
                if (p.Remaining > 0) chunkEntered.TrySetResult();
            });
            var backfillTask = backend.RunVectorBackfillAsync(0, progress);
            await chunkEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(30);

            // Directly test non-destructive queue preemption while host is warm + concurrent SearchAsync preemption.
            var client = (InferenceHostClient)typeof(DesktopBackend).GetMethod("GetEmbeddings", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(backend, null)!;
            var searchTask = backend.SearchAsync(new SearchRequest("warm", Mode: SearchMode.Hybrid));
            using var queuePreempt = new CancellationTokenSource();
            var queuedTask = client.EmbedAsync([new EmbeddingInput("text", "queued during search")], CancellationToken.None, queuePreempt.Token);
            await Task.Delay(15);
            queuePreempt.Cancel();
            await Throws<OperationCanceledException>(() => queuedTask);

            var searchResp = await searchTask.WaitAsync(TimeSpan.FromSeconds(10));
            Check(searchResp is not null, "Hybrid SearchAsync failed during real host backfill preemption.");
            int backfilled = await backfillTask.WaitAsync(TimeSpan.FromSeconds(15));
            Check(backfilled == 65, $"Expected 65 segments backfilled on warm host, got {backfilled}.");

            var auditLines = await File.ReadAllLinesAsync(Path.Combine(models, "audit.jsonl"));
            var startedPids = auditLines
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.TryGetProperty("started", out _))
                .Select(e => e.GetProperty("started").GetInt32())
                .ToArray();
            Check(startedPids.Length == 1, $"Preemption must NOT kill/restart InferenceHost child (expected 1 started PID, got {startedPids.Length}).");
        });
    }

    private static async Task SeedAsync(string database, string path, string text, bool code = false)
    {
        await using var repository = new SqliteIndexRepository(database);
        await repository.InitializeAsync();
        await repository.UpsertFileAsync(new IndexedFile(0, path, code ? "code" : "doc", text.Length, 1, "done", null, null,
            [new IndexSegment(code ? "lines" : "chunk", "none", code ? 1 : null, code ? 1 : null, text)]));
    }
    private static object? Field(object target, string name) =>
        typeof(DesktopBackend).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    private static void SetField(object target, string name, object value) =>
        typeof(DesktopBackend).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }
    private static async Task Throws<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    private sealed class FakeBackfillEmbeddings : IEmbeddingService
    {
        public string? FailMessage { get; set; }
        public Func<CancellationToken, Task>? OnEmbed { get; set; }
        public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingInfo("fake-backfill-profile", 768, "fake-runtime", "fake-model", "rev1", "cpu", "ok", ["text"]));
        public async Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default)
        {
            if (OnEmbed is not null) await OnEmbed(cancellationToken);
            var results = inputs.Select((_, idx) =>
            {
                if (FailMessage is not null)
                    return new EmbeddingResult(null, FailMessage);
                var vec = new float[768];
                vec[idx % 768] = 1f;
                return new EmbeddingResult(vec);
            }).ToArray();
            return results;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SyntheticDbException(string message) : DbException(message);
    private sealed class TrackingEmbeddings : IEmbeddingService
    {
        public bool Disposed { get; private set; }
        public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class TrackingRepository : IIndexRepository
    {
        public string DatabasePath => "test-only-unused";
        public bool Disposed { get; private set; }
        public bool FailOnDispose { get; init; }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexedFile>> ReadFilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IndexedFile?> GetFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertFileAsync(IndexedFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KeywordMatch>> KeywordSearchAsync(string query, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (FailOnDispose) throw new IOException("synthetic store cleanup fault");
            return ValueTask.CompletedTask;
        }
    }
}
