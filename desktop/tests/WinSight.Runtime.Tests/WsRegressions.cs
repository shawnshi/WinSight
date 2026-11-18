using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Inference;
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

        await test("PREVIEW-P0-P2-FASTPATH-AND-EDGE-DRIFT", async () =>
        {
            Require(new WinSight.Infrastructure.Media.NativePdfPageRequest("a.pdf", 1).MaxEdge == 1600, "NativePdfPageRequest default MaxEdge must be 1600");
            Require(new WinSight.Infrastructure.Media.NativeWordPageRequest("a.docx").MaxEdge == 1600, "NativeWordPageRequest default MaxEdge must be 1600");
            Require(new WinSight.Infrastructure.Media.NativeExcelPageRequest("a.xlsx").MaxEdge == 1600, "NativeExcelPageRequest default MaxEdge must be 1600");

            string pvRoot = Path.Combine(root, "preview-fastpath");
            string pvSrc = Path.Combine(pvRoot, "sources");
            string pvData = Path.Combine(pvRoot, "data");
            Directory.CreateDirectory(pvSrc);
            Directory.CreateDirectory(pvData);
            string mdPath = Path.Combine(pvSrc, "sample.md");
            string vidPath = Path.Combine(pvSrc, "clip.mp4");
            string midPath = Path.Combine(pvSrc, "mid.md");
            string pptPath = Path.Combine(pvSrc, "deck.ppt");
            string mdText = "Markdown preview body content " + new string('A', 9000) + "Z";
            await File.WriteAllTextAsync(mdPath, mdText);
            await File.WriteAllBytesAsync(vidPath, [0x00, 0x01, 0x02, 0xFF, 0xFE]);
            await File.WriteAllTextAsync(midPath, "Mid drift probe " + new string('M', 9500) + "Q");
            await File.WriteAllBytesAsync(pptPath, [0xD0, 0xCF, 0x11, 0xE0, 0x00, 0xFF]);
            await new JsonSettingsStore(Path.Combine(pvData, "settings.json"))
                .SaveAsync(new AppSettings { Folders = [pvSrc], BackgroundIndexingEnabled = false });

            IndexedFile mdRow, vidRow, midRow, pptRow;
            await using (var pvRepo = new SqliteIndexRepository(Path.Combine(pvData, "index", "index.sqlite")))
            {
                var mdInfo = new FileInfo(mdPath);
                var mdSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(mdPath))).ToLowerInvariant();
                await pvRepo.UpsertFileAsync(new(0, mdPath, "doc", mdInfo.Length, (mdInfo.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds,
                    "done", null, System.Text.Json.JsonSerializer.Serialize(new { sourceSha256V1 = mdSha }),
                    [new("chunk", "text", 1, null, "Markdown indexed excerpt", null)]));
                mdRow = (await pvRepo.GetFileAsync(mdPath))!;

                var vidInfo = new FileInfo(vidPath);
                var vidSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(vidPath))).ToLowerInvariant();
                await pvRepo.UpsertFileAsync(new(0, vidPath, "video", vidInfo.Length, (vidInfo.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds,
                    "done", null, System.Text.Json.JsonSerializer.Serialize(new { sourceSha256V1 = vidSha }),
                    [new("frame", "image", 5, 10, "video indexed transcript", null)]));
                vidRow = (await pvRepo.GetFileAsync(vidPath))!;

                var midInfo = new FileInfo(midPath);
                var midSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(midPath))).ToLowerInvariant();
                await pvRepo.UpsertFileAsync(new(0, midPath, "doc", midInfo.Length, (midInfo.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds,
                    "done", null, System.Text.Json.JsonSerializer.Serialize(new { sourceSha256V1 = midSha }),
                    [new("chunk", "text", 1, null, "Mid indexed excerpt", null)]));
                midRow = (await pvRepo.GetFileAsync(midPath))!;

                var pptInfo = new FileInfo(pptPath);
                var pptSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(pptPath))).ToLowerInvariant();
                await pvRepo.UpsertFileAsync(new(0, pptPath, "doc", pptInfo.Length, (pptInfo.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds,
                    "done", null, System.Text.Json.JsonSerializer.Serialize(new { sourceSha256V1 = pptSha }),
                    [new("chunk", "text", 1, null, "PPT indexed slide summary", null)]));
                pptRow = (await pvRepo.GetFileAsync(pptPath))!;
            }

            await using var pvBackend = await DesktopBackend.CreateAsync(new DesktopOptions(pvData, EnableBackgroundIndexing: false));
            var mdHit = new SearchHit(mdRow.Id, mdPath, "doc", 1.0, "keyword", "Markdown indexed excerpt", 1, null, "chunk", null, null, mdRow.Size, mdRow.ModifiedTime);
            await using (var mdPrev = await pvBackend.PreviewSourceAsync(mdHit))
            {
                Require(mdPrev.SourceCurrent && mdPrev.Excerpt.Contains("Markdown preview body content", StringComparison.Ordinal),
                    "Markdown fast preview must succeed");
            }
            var fi = new FileInfo(mdPath);
            var stamp = fi.LastWriteTimeUtc;
            var bytes = await File.ReadAllBytesAsync(mdPath);
            bytes[^1] ^= 1;
            await File.WriteAllBytesAsync(mdPath, bytes);
            File.SetLastWriteTimeUtc(mdPath, stamp);
            await using (var drifted = await pvBackend.PreviewSourceAsync(mdHit))
            {
                Require(!drifted.SourceCurrent, "Edge checksum must detect same-length/timestamp tail byte drift");
            }

            var vidHit = new SearchHit(vidRow.Id, vidPath, "video", 1.0, "keyword", "video indexed transcript", 5, 10, "frame", null, null, vidRow.Size, vidRow.ModifiedTime);
            await using (var vidPrev = await pvBackend.PreviewSourceAsync(vidHit))
            {
                Require(vidPrev.SourceCurrent && vidPrev.Excerpt == "video indexed transcript" && vidPrev.Notice.Contains("视频", StringComparison.Ordinal),
                    "Video preview must keep indexed transcript and avoid StreamReader binary read");
            }

            var midHit = new SearchHit(midRow.Id, midPath, "doc", 1.0, "keyword", "Mid indexed excerpt", 1, null, "chunk", null, null, midRow.Size, midRow.ModifiedTime);
            await using (var midPrev = await pvBackend.PreviewSourceAsync(midHit))
            {
                Require(midPrev.SourceCurrent, "Initial mid.md preview must verify current");
            }
            var midBytes = await File.ReadAllBytesAsync(midPath);
            midBytes[4500] ^= 1;
            await File.WriteAllBytesAsync(midPath, midBytes);
            File.SetLastWriteTimeUtc(midPath, DateTime.UtcNow.AddSeconds(2));
            await using (var midDrifted = await pvBackend.PreviewSourceAsync(midHit))
            {
                Require(!midDrifted.SourceCurrent, "Middle-byte edit with updated disk mtime must bypass fingerprint cache");
            }

            var pptHit = new SearchHit(pptRow.Id, pptPath, "doc", 1.0, "keyword", "PPT indexed slide summary", 1, null, "chunk", null, null, pptRow.Size, pptRow.ModifiedTime);
            await using (var pptPrev = await pvBackend.PreviewSourceAsync(pptHit))
            {
                Require(pptPrev.SourceCurrent && pptPrev.Excerpt == "PPT indexed slide summary",
                    "Legacy binary .ppt preview must keep indexed excerpt and skip StreamReader mojibake");
            }
        });

        await test("INDEX-P0-P2-UNCHANGED-SHORTCIRCUIT-AND-IDLE-BACKFILL", async () =>
        {
            string idxDir = Path.Combine(root, "idx-p0-p2-runtime");
            string srcDir = Path.Combine(idxDir, "src");
            Directory.CreateDirectory(srcDir);
            string docFile = FilePolicy.Normalize(Path.Combine(srcDir, "unchanged.txt"));
            await File.WriteAllTextAsync(docFile, "Repeated unchanged indexing corpus sentence.");

            await using (var repo = new SqliteIndexRepository(Path.Combine(idxDir, "idx.sqlite")))
            {
                await repo.InitializeAsync();
                var extractor = new CountingTestExtractor(new ContentExtractor());
                var embedder = new DeterministicTestEmbeddings("test-backfill-profile");
                var service = new IndexingService(repo, extractor, embedder);

                await service.IndexPathsAsync([srcDir], [docFile], code: false);
                Require(extractor.Calls == 1 && embedder.Calls == 1, $"Expected initial 1 extract & 1 embed call, got {extractor.Calls}/{embedder.Calls}");
                Require((await repo.GetFileAsync(docFile)) is { State: "done" }, "Initial IndexPathsAsync must reach done");

                var now = DateTimeOffset.UtcNow;
                await repo.EnqueueIndexTaskAsync(new IndexTask(docFile, srcDir, false, "Update", null, "Ready", 0, now, now));
                await service.IndexPathsAsync([srcDir], [docFile], code: false);
                Require(extractor.Calls == 1 && embedder.Calls == 1, $"Unchanged file must short-circuit without re-extract/re-embed, got {extractor.Calls}/{embedder.Calls}");
                Require(await repo.GetTaskAsync(docFile) is null, "Short-circuited file task must be acked");

                await service.IndexPathsAsync([srcDir], [docFile], code: false, force: true);
                Require(extractor.Calls == 2, $"force: true must bypass short-circuit (calls={extractor.Calls})");

                // INDX-R2-BLK-01 (NM-BR-08): differing-case IndexPathsAsync preserves stored ID & Path without duplicates
                string mixedFile = FilePolicy.Normalize(Path.Combine(srcDir, "MixedCaseTarget.txt"));
                string lowerMixedFile = FilePolicy.Normalize(Path.Combine(srcDir, "mixedcasetarget.txt"));
                await File.WriteAllTextAsync(mixedFile, "Mixed case unchanged corpus.");
                await service.IndexPathsAsync([srcDir], [mixedFile], code: false);
                var origMixed = await repo.GetFileAsync(mixedFile);
                Require(origMixed is { State: "done" }, "Initial MixedCaseTarget.txt must reach done");
                int callsBeforeCase = extractor.Calls;

                await service.IndexPathsAsync([srcDir], [lowerMixedFile], code: false);
                Require(extractor.Calls == callsBeforeCase, $"Case-differing unchanged file must hit short-circuit (calls={extractor.Calls} vs {callsBeforeCase})");
                var headersAfterCase = (await repo.ReadFileHeadersAsync()).Where(f => string.Equals(f.Path, mixedFile, StringComparison.OrdinalIgnoreCase)).ToArray();
                Require(headersAfterCase.Length == 1 && headersAfterCase[0].Id == origMixed!.Id && string.Equals(headersAfterCase[0].Path, origMixed.Path, StringComparison.Ordinal),
                    "Case-differing unchanged IndexPathsAsync must not insert duplicate row");

                await service.IndexPathsAsync([srcDir], [lowerMixedFile], code: false, force: true);
                Require(extractor.Calls == callsBeforeCase + 1, "force: true with differing case must re-extract");
                var afterForceMixed = await repo.GetFileAsync(mixedFile);
                var headersAfterForce = (await repo.ReadFileHeadersAsync()).Where(f => string.Equals(f.Path, mixedFile, StringComparison.OrdinalIgnoreCase)).ToArray();
                Require(headersAfterForce.Length == 1 && afterForceMixed is not null && afterForceMixed.Id == origMixed!.Id
                    && string.Equals(afterForceMixed.Path, origMixed.Path, StringComparison.Ordinal),
                    "Case-differing force update must preserve original Id and stored binary Path");
            }

            string dataDir = Path.Combine(idxDir, "backend-data");
            string watchDir = Path.Combine(idxDir, "backend-docs");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(watchDir);
            await new JsonSettingsStore(Path.Combine(dataDir, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [watchDir],
                SetUp = true,
                BackgroundIndexingEnabled = false,
                ProcessorOnly = false
            });

            string partialDoc = FilePolicy.Normalize(Path.Combine(watchDir, "partial.txt"));
            await File.WriteAllTextAsync(partialDoc, "Idle backfill segment content");
            await using (var seedRepo = new SqliteIndexRepository(Path.Combine(dataDir, "index", "index.sqlite")))
            {
                await seedRepo.InitializeAsync();
                await seedRepo.SetMetaAsync("profile_id", "test-backfill-profile");
                await seedRepo.UpsertFileAsync(new(0, partialDoc, "doc", 28, 1, "partial", FilePolicy.SemanticUnavailableDetail, null,
                    [new("chunk", "none", 1, null, "First idle backfill segment", null),
                     new("chunk", "none", 2, null, "Second idle backfill segment", null)]));
            }

            TimeSpan simIdle = TimeSpan.FromMinutes(1);
            var simSignals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(IsAcPower: true, IsBatterySaver: false, BatteryPercent: 100),
                idleTime: () => simIdle,
                memoryStatus: () => new SystemMemoryInfo(AvailablePhysicalBytes: 8UL * 1024 * 1024 * 1024, CommitPercent: 40.0));

            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(dataDir, EnableBackgroundIndexing: false) { IsOnAcPower = () => true });
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var stores = (IReadOnlyList<IIndexRepository>)await (Task<IReadOnlyList<IIndexRepository>>)typeof(DesktopBackend).GetMethod("GetRepositoriesAsync", flags)!.Invoke(backend, [CancellationToken.None])!;
            var coord = new BackgroundIndexCoordinator(
                new AppSettings { Folders = [watchDir], SetUp = true, BackgroundIndexingEnabled = false, ProcessorOnly = false },
                (_, _) => Task.CompletedTask,
                _ => { },
                repository: stores[0],
                indexingAllowed: () => false,
                hostSignals: simSignals);
            typeof(DesktopBackend).GetField("background", flags)!.SetValue(backend, coord);
            typeof(DesktopBackend).GetField("embeddings", flags)!.SetValue(backend, new DeterministicTestEmbeddings("test-backfill-profile"));

            int activeTick = await backend.RunIdleBackfillTickAsync(128);
            Require(activeTick == 0, $"Active user (<5min idle) must defer idle backfill, got {activeTick}");
            Require((await stores[0].GetFileAsync(partialDoc)) is { State: "partial" }, "Active user tick must keep file partial");

            simIdle = TimeSpan.FromMinutes(6);
            int scheduledBlockNow = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            int offStart = (scheduledBlockNow + 60) % 1440;
            int offEnd = (scheduledBlockNow + 120) % 1440;
            var offSettings = await backend.LoadSettingsAsync();
            offSettings.IndexHoursOn = true;
            offSettings.IndexHours = new IndexHours { Start = offStart, End = offEnd };
            await backend.SaveSettingsAsync(offSettings);
            typeof(DesktopBackend).GetField("embeddings", flags)!.SetValue(backend, new DeterministicTestEmbeddings("test-backfill-profile"));
            typeof(DesktopBackend).GetField("background", flags)!.SetValue(backend, coord);
            Require(await backend.RunIdleBackfillTickAsync(128) == 0, "RunIdleBackfillTickAsync must respect IndexHoursOn off-schedule gate");

            var onSettings = await backend.LoadSettingsAsync();
            onSettings.IndexHoursOn = false;
            await backend.SaveSettingsAsync(onSettings);
            typeof(DesktopBackend).GetField("embeddings", flags)!.SetValue(backend, new DeterministicTestEmbeddings("test-backfill-profile"));
            typeof(DesktopBackend).GetField("background", flags)!.SetValue(backend, coord);

            await backend.SetIndexingPausedAsync(true);
            Require(await backend.RunIdleBackfillTickAsync(128) == 0, "Paused backend must skip idle backfill without throwing");
            await backend.SetIndexingPausedAsync(false);

            int idleTick = await backend.RunIdleBackfillTickAsync(128);
            Require(idleTick == 2, $"AC + >=5min idle + empty queue must backfill 2 segments, got {idleTick}");
            Require((await stores[0].GetFileAsync(partialDoc)) is { State: "done" }, "Backfilled file must promote to done");

            // BLOCKING-2: StartMaintenanceLoop must survive Pause/OperationCanceledException during RunIdleBackfillTickAsync and complete backfill on resume
            string loopData = Path.Combine(idxDir, "loop-data");
            string loopWatch = Path.Combine(idxDir, "loop-docs");
            Directory.CreateDirectory(loopData);
            Directory.CreateDirectory(loopWatch);
            await new JsonSettingsStore(Path.Combine(loopData, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [loopWatch],
                SetUp = true,
                BackgroundIndexingEnabled = false,
                ProcessorOnly = false
            });
            string loopPartial = FilePolicy.Normalize(Path.Combine(loopWatch, "loop-partial.txt"));
            await File.WriteAllTextAsync(loopPartial, "Maintenance loop resilience content");
            await using (var loopSeedRepo = new SqliteIndexRepository(Path.Combine(loopData, "index", "index.sqlite")))
            {
                await loopSeedRepo.InitializeAsync();
                await loopSeedRepo.SetMetaAsync("profile_id", "test-backfill-profile");
                await loopSeedRepo.UpsertFileAsync(new(0, loopPartial, "doc", 34, 1, "partial", FilePolicy.SemanticUnavailableDetail, null,
                    [new("chunk", "none", 1, null, "Maintenance loop backfill segment", null)]));
            }

            var loopSignals = new HostSignals(
                powerStatus: () => new SystemPowerInfo(IsAcPower: true, IsBatterySaver: false, BatteryPercent: 100),
                idleTime: () => TimeSpan.FromMinutes(10),
                memoryStatus: () => new SystemMemoryInfo(AvailablePhysicalBytes: 8UL * 1024 * 1024 * 1024, CommitPercent: 40.0));
            var blockingEmbed = new BlockingFirstCallEmbeddings("test-backfill-profile");

            await using var loopBackend = await DesktopBackend.CreateAsync(new DesktopOptions(loopData, EnableBackgroundIndexing: true)
            {
                IsOnAcPower = () => true,
                MaintenancePollInterval = TimeSpan.FromMilliseconds(40)
            });
            var loopStores = (IReadOnlyList<IIndexRepository>)await (Task<IReadOnlyList<IIndexRepository>>)typeof(DesktopBackend).GetMethod("GetRepositoriesAsync", flags)!.Invoke(loopBackend, [CancellationToken.None])!;
            var loopCoord = new BackgroundIndexCoordinator(
                new AppSettings { Folders = [loopWatch], SetUp = true, BackgroundIndexingEnabled = false, ProcessorOnly = false },
                (_, _) => Task.CompletedTask,
                _ => { },
                repository: loopStores[0],
                indexingAllowed: () => false,
                hostSignals: loopSignals);
            typeof(DesktopBackend).GetField("embeddings", flags)!.SetValue(loopBackend, blockingEmbed);
            typeof(DesktopBackend).GetField("background", flags)!.SetValue(loopBackend, loopCoord);

            await blockingEmbed.FirstCallEntered.WaitAsync(TimeSpan.FromSeconds(5));
            await loopBackend.SetIndexingPausedAsync(true);
            await blockingEmbed.FirstCallCanceled.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(120);
            var loopTask = (Task)typeof(DesktopBackend).GetField("maintenanceLoop", flags)!.GetValue(loopBackend)!;
            Require(!loopTask.IsCompleted, $"maintenanceLoop must stay alive after pause/cancellation during idle backfill (status={loopTask.Status})");

            await loopBackend.SetIndexingPausedAsync(false);
            using (var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while ((await loopStores[0].GetFileAsync(loopPartial))?.State != "done")
                    await Task.Delay(25, waitCts.Token);
            }
            Require(!loopTask.IsCompleted, "maintenanceLoop must remain active after recovering from pause interruption");

            // INDX-R2-BLK-02: Pausing during active background batch (IndexingInterruptedException while outer token is uncancelled) resets tasks to Ready
            var loopController = (IndexingController)typeof(DesktopBackend).GetField("indexing", flags)!.GetValue(loopBackend)!;
            string batchPauseDoc = FilePolicy.Normalize(Path.Combine(loopWatch, "batch-pause.txt"));
            await File.WriteAllTextAsync(batchPauseDoc, "Pause interruption mid-batch check.");
            var batchNow = DateTimeOffset.UtcNow;
            await loopStores[0].EnqueueIndexTaskAsync(new IndexTask(batchPauseDoc, loopWatch, false, "Add", null, "Ready", 0, batchNow, batchNow));
            var pauseBatchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var pauseBatchQueue = new IncrementalIndexQueue(
                loopStores[0],
                indexPaths: (_, _, _, qTok) => loopController.RunAsync(async runTok =>
                {
                    pauseBatchEntered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, runTok);
                }, qTok)))
            {
                using var uncancelledOuter = new CancellationTokenSource();
                var batchTask = pauseBatchQueue.ProcessReadyTasksAsync(10, uncancelledOuter.Token, batchSize: 16);
                await pauseBatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await loopBackend.SetIndexingPausedAsync(true);
                bool caughtInterrupted = false;
                try { await batchTask; }
                catch (IndexingInterruptedException) { caughtInterrupted = true; }
                Require(caughtInterrupted && !uncancelledOuter.IsCancellationRequested,
                    "ProcessReadyTasksAsync must propagate IndexingInterruptedException when paused mid-batch");
                var pausedTaskState = await loopStores[0].GetTaskAsync(batchPauseDoc);
                Require(pausedTaskState is { State: "Ready", Attempts: 0 },
                    $"Paused mid-batch task must stay Ready with Attempts=0, got state={pausedTaskState?.State} attempts={pausedTaskState?.Attempts}");
                await loopStores[0].AckTaskAsync(batchPauseDoc);
            }
            await loopBackend.SetIndexingPausedAsync(false);
        });
        await test("VEC-P0-P2 consistent prompt cap, single-IPC warm SearchAsync, non-interactive info, and job interactive boost", async () =>
        {
            // 1. VEC-04: initial indexing default prompt cap == backfill prompt cap (4000 chars)
            var longExcerpt = new string('x', 5200);
            var defaultPrompt = FilePolicy.FormatTextEmbeddingPrompt("doc-title", longExcerpt, "chunk");
            var backfillPrompt = FilePolicy.FormatTextEmbeddingPrompt("doc-title", longExcerpt, "chunk", maxNonPageChars: FilePolicy.DefaultMaxNonPageEmbeddingChars);
            Require(defaultPrompt == backfillPrompt && defaultPrompt.Length == "title: doc-title | text: ".Length + 4000,
                $"Prompt formatting mismatch: default={defaultPrompt.Length}, backfill={backfillPrompt.Length}");

            // 2. PERF-02: warm SearchService reuses verified host identity -> exactly 1 "text" IPC and 0 redundant "info" IPCs
            var hostExe = !string.IsNullOrWhiteSpace(fakeHost)
                ? fakeHost
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../WinSight.Inference.Tests/FakeHost/bin/Debug/net11.0/FakeHost.exe"));
            if (!File.Exists(hostExe))
                hostExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../WinSight.Inference.Tests/FakeHost/bin/Release/net11.0/FakeHost.exe"));
            Require(File.Exists(hostExe), "FakeHost.exe missing for warm IPC test: " + hostExe);

            var ipcRoot = Path.Combine(root, "vec-p0-warm-ipc");
            var modelsDir = Path.Combine(ipcRoot, "models");
            Directory.CreateDirectory(modelsDir);
            var manifestPath = Path.Combine(modelsDir, "manifest with spaces &.json");
            await File.WriteAllTextAsync(manifestPath, "{}");

            await using var repo = new SqliteIndexRepository(Path.Combine(ipcRoot, "index.sqlite"));
            await repo.InitializeAsync();
            var vec = new float[VectorContract.Dimension];
            vec[0] = 0.6f; vec[1] = 0.8f;
            await repo.SetMetaAsync("profile_id", new string('a', 64));
            await repo.UpsertFileAsync(new IndexedFile(0, Path.Combine(ipcRoot, "alpha.txt"), "doc", 64, 1, "done", null, null,
                [new IndexSegment("chunk", "text", 1, null, "semantic retrieval anchor", vec)]));

            await using (var client = new InferenceHostClient(hostExe, modelsDir, manifestPath, "cpu", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)))
            {
                var info = await client.GetOrStartInfoAsync(interactive: false);
                Require(info.ProfileId == new string('a', 64) && !client.HasPendingInteractiveRequest,
                    "Non-interactive GetOrStartInfoAsync left pending interactive marker.");

                // Warm cache hit should not issue another IPC
                var cachedInfo = await client.GetOrStartInfoAsync(interactive: false);
                Require(ReferenceEquals(info, cachedInfo), "Expected cached EmbeddingInfo instance on warm host.");

                var auditPath = Path.Combine(modelsDir, "audit.jsonl");
                var beforeOps = File.ReadAllLines(auditPath)
                    .Select(l => JsonDocument.Parse(l).RootElement)
                    .Where(e => e.TryGetProperty("request", out _))
                    .Select(e => e.GetProperty("request").GetProperty("op").GetString()!)
                    .ToArray();
                Require(beforeOps.Length == 1 && beforeOps[0] == "info", $"Expected exactly 1 initial info IPC, got [{string.Join(',', beforeOps)}]");

                var search = new SearchService(repo, client);
                var resp = await search.SearchAsync(new SearchRequest("semantic retrieval anchor", Mode: SearchMode.Semantic));
                Require(resp.SemanticAvailable && resp.Hits.Count == 1, "Warm semantic search did not return expected hit.");

                var afterOps = File.ReadAllLines(auditPath)
                    .Select(l => JsonDocument.Parse(l).RootElement)
                    .Where(e => e.TryGetProperty("request", out _))
                    .Select(e => e.GetProperty("request").GetProperty("op").GetString()!)
                    .ToArray();
                Require(afterOps.SequenceEqual(new[] { "info", "text" }),
                    $"Warm SearchAsync issued unexpected IPCs: [{string.Join(',', afterOps)}]");
            }

            // 3. PERF-03: WindowsChildJob dynamically boosts interactive priority to Normal and restores BelowNormal
            if (OperatingSystem.IsWindows())
            {
                var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
                var psi = new ProcessStartInfo(ping) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                foreach (var a in new[] { "-n", "20", "127.0.0.1" }) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi)!;
                using var job = WindowsChildJob.Attach(proc, lowPriority: true);
                try
                {
                    Require(proc.PriorityClass == ProcessPriorityClass.BelowNormal, $"Expected BelowNormal initial priority, got {proc.PriorityClass}");
                    job.SetInteractiveBoost(true);
                    Require(proc.PriorityClass == ProcessPriorityClass.Normal, $"Expected Normal boosted priority, got {proc.PriorityClass}");
                    job.SetInteractiveBoost(false);
                    Require(proc.PriorityClass == ProcessPriorityClass.BelowNormal, $"Expected BelowNormal restored priority, got {proc.PriorityClass}");
                }
                finally
                {
                    try { if (!proc.HasExited) proc.Kill(true); } catch { }
                }
            }
        });
    }

    private sealed class BlockingFirstCallEmbeddings(string profileId) : IEmbeddingService
    {
        private int callCount;
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task FirstCallEntered => entered.Task;
        public Task FirstCallCanceled => canceled.Task;
        public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingInfo(profileId, 768, "test", "fake-model", "rev1", "cpu", "ok", ["text"]));
        public async Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult();
                    throw;
                }
            }
            return inputs.Select(_ =>
            {
                var vec = new float[768];
                vec[0] = 1f;
                return new EmbeddingResult(vec);
            }).ToArray();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingTestExtractor(IContentExtractor inner) : IContentExtractor
    {
        public int Calls { get; private set; }
        public bool CanExtract(string path, bool code) => inner.CanExtract(path, code);
        public Task<ExtractionResult> ExtractAsync(string path, bool code, CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.ExtractAsync(path, code, cancellationToken);
        }
    }

    private sealed class DeterministicTestEmbeddings(string profileId) : IEmbeddingService
    {
        public int Calls { get; private set; }
        public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingInfo(profileId, 768, "test", "fake-model", "rev1", "cpu", "ok", ["text"]));
        public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default)
        {
            Calls++;
            var results = inputs.Select((_, idx) =>
            {
                var vec = new float[768];
                vec[idx % 768] = 1f;
                return new EmbeddingResult(vec);
            }).ToArray();
            return Task.FromResult<IReadOnlyList<EmbeddingResult>>(results);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IndexedFile Row(string path, string kind, string text) => new(0, path, kind, 10, 1, "done", null, null, [new("chunk", "none", 1, null, text)]);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
