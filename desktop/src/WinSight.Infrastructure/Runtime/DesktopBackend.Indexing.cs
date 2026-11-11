using System.Diagnostics;
using System.Text.Json;
using WinSight.Core;
using WinSight.Core.Diagnostics;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Inference;
using WinSight.Infrastructure.Media;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;

namespace WinSight.Infrastructure.Runtime;

public sealed partial class DesktopBackend
{
public Task IndexAsync(IndexRequest request, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        IndexCoreAsync(request, progress, allowRebuild: true, cancellationToken);

/// <summary>
    /// Runs indexing without ever starting a generation rebuild. Maintenance requests use this: a rebuild can
    /// take an hour, so it must never be a side effect of a client asking for one folder.
    /// </summary>
    private async Task IndexCoreAsync(IndexRequest request, IProgress<IndexProgress>? progress, bool allowRebuild,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeStop.Token);
        cancellationToken = linked.Token;
        await indexing.RunAsync(async token =>
        {
            // One indexing run per data directory across processes (app, CLI, maintenance); SQLite alone only serializes commits.
            using var runLock = IndexRunLock.TryAcquire(dataDirectory)
                ?? throw new InvalidOperationException("Another WinSight process is indexing this data directory (the app, the CLI or a maintenance request). Wait for it to finish, or pause it first.");
            IIndexRepository repository;
            IEmbeddingService model;
            string mediaHost;
            IndexRequest approved;
            await lifecycle.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (request.Roots.Count == 0 || request.Roots.Any(root => !IsAuthorizedResult(root, request.Code)))
                    throw new UnauthorizedAccessException("Indexing requires authorized roots outside application data and index directories.");
                token.ThrowIfCancellationRequested();
                var stores = await GetRepositoriesAsync(token);
                repository = stores[request.Code ? 1 : 0];
                model = GetEmbeddings();
                mediaHost = settings.InferenceExecutable ?? Path.Combine(applicationDirectory, "inference", "WinSight.InferenceHost.exe");
                approved = request with
                {
                    GeneratePageImages = request.GeneratePageImages || settings.GeneratePageImages,
                    Excluded = (request.Excluded ?? []).Concat(request.Code ? settings.CodeSkipped : settings.Skipped)
                        .Append(dataDirectory).Append(IndexDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    SkipTypes = (request.SkipTypes ?? []).Concat(settings.SkipTypes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Documents = request.Documents ?? new DocumentExtractionOptions(settings.IncludeWordComments, settings.IncludeWordHeadersFooters)
                };
            }
            finally { lifecycle.Release(); }
            NativeMediaProcessClient? mediaClient = null;
            Exception? indexingFailure = null;
            try
            {
                if (!approved.Code)
                {
                    if (File.Exists(mediaHost))
                    {
                        var workspace = MediaTaskFiles.LocalPath(Path.Combine(dataDirectory, "media-tasks"));
                        using var dataPin = SafeFile.Open(dataDirectory, directory: true);
                        Directory.CreateDirectory(workspace);
                        using var workspacePin = SafeFile.Open(workspace, directory: true);
                        mediaClient = new NativeMediaProcessClient(new NativeMediaProcessOptions
                        {
                            HostExecutable = mediaHost,
                            WorkspaceRoot = workspace
                        });
                    }
                    else
                    {
                        AppLog.Warn($"Inference host not found at '{mediaHost}'; media/image processing will degrade gracefully.");
                    }
                }
                // Deferred (default) keeps the first pass to text and structure: page rasterisation, OCR and
                // media sampling are the expensive stages and are left to a later pass.
                var defer = approved.DeferComplexParse;
                // The embedding profile includes the ORT version, so an ORT change makes every stored vector
                // belong to a different profile. Keep the old index as a backup and start a fresh one instead
                // of failing every file with a profile mismatch.
                EmbeddingInfo? info = null;
                try
                {
                    info = await model.GetInfoAsync(token).ConfigureAwait(false);
                }
                catch (Exception modelError) when (modelError is IOException or InvalidDataException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    AppLog.Warn("Embedding model host unavailable during indexing; falling back to lexical-only indexing", modelError);
                    backgroundError = "Embedding model unavailable; lexical/keyword indexing only: " + modelError.Message;
                }

                repository = allowRebuild && info is not null
                    ? await EnsureGenerationAsync(repository, approved, info, mediaClient, defer, progress, token).ConfigureAwait(false)
                    : repository;
                // Page rasterisation is the expensive stage. It now also needs an explicit opt-in,
                // because it is what made a corpus pass take days rather than minutes.
                var pages = approved.GeneratePageImages && mediaClient is not null;
                var service = new IndexingService(repository, new ContentExtractor(mediaClient), info is null ? null : model,
                    defer || mediaClient is null ? null : new NativeMediaSamplingService(mediaClient),
                    pages ? new NativeDocumentPages(mediaClient!) : null,
                    pages ? new NativePresentationPages(mediaClient!) : null,
                    allowDeferredStages: !pages);
                if (!defer)
                {
                    await service.IndexAsync(approved, progress, token);
                }
                else
                {
                    int totalCompleted = 0, totalFailed = 0, totalPartial = 0;
                    IProgress<IndexProgress>? batchProgress = progress is null ? null : new Progress<IndexProgress>(p =>
                    {
                        if (p.Phase == "finished") return;
                        if (p.Phase is "done" or "failed" or "partial" or "skipped" or "deleted")
                        {
                            Interlocked.Increment(ref totalCompleted);
                            if (p.Phase == "failed") Interlocked.Increment(ref totalFailed);
                            if (p.Phase == "partial") Interlocked.Increment(ref totalPartial);
                        }
                        progress.Report(new IndexProgress(p.Phase, p.Path, Volatile.Read(ref totalCompleted), Volatile.Read(ref totalFailed), Volatile.Read(ref totalPartial), p.Detail));
                    });

                    // Manual and watcher passes share one durable queue so progress survives a restart.
                    var queue = new IncrementalIndexQueue(repository,
                        indexPaths: (roots, paths, code, ct) => service.IndexPathsAsync(roots, paths, code, batchProgress, ct),
                        onError: error => {
                            if (error is not ObjectDisposedException)
                            {
                                backgroundError = "queue: " + error.Message;
                            }
                            AppLog.Warn("index queue error", error);
                        });
                    await queue.InitializeAsync(token);

                    var initialCounts = await queue.GetTaskCountsAsync(token);
                    bool reconciled = false;
                    if (initialCounts.GetValueOrDefault("Ready") <= 500)
                    {
                        foreach (var root in approved.Roots)
                        {
                            await queue.ReconcileRootAsync(root, approved.Code, force: true, token, requireStabilization: false, upgradePageImages: pages);
                        }
                        reconciled = true;
                    }

                    // A USN catch-up queues new files as Stabilizing with a due time a moment ahead. Ending the run when nothing is ready
                    // yet would leave them waiting for the next signal, so wait (bounded) for them to settle and process them too.
                    var settling = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var processed = await queue.ProcessReadyTasksAsync(400, token, batchSize: 16);
                        if (processed != 0)
                        {
                            backgroundError = null;
                            continue;
                        }
                        if (!reconciled)
                        {
                            foreach (var root in approved.Roots)
                            {
                                await queue.ReconcileRootAsync(root, approved.Code, force: true, token, requireStabilization: false, upgradePageImages: pages);
                            }
                            reconciled = true;
                            continue;
                        }
                        var counts = await queue.GetTaskCountsAsync(token);
                        if (counts.GetValueOrDefault("Stabilizing") == 0 || settling.Elapsed > TimeSpan.FromSeconds(15)) break;
                        await Task.Delay(300, token);
                    }
                    progress?.Report(new IndexProgress("finished", null, Volatile.Read(ref totalCompleted), Volatile.Read(ref totalFailed), Volatile.Read(ref totalPartial)));
                }
            }
            catch (Exception error)
            {
                indexingFailure = error;
                if (error is not OperationCanceledException) AppLog.Error("indexing run failed", error);
                throw;
            }
            finally
            {
                // The controller must not observe idle until this run's native work has settled.
                if (mediaClient is not null)
                {
                    try { await mediaClient.DisposeAsync(); }
                    catch (Exception cleanup)
                    {
                        // Report before callback exit: idle is logical, not proof that native resources settled.
                        indexing.ReportUnsettled(cleanup);
                        if (indexingFailure is not null)
                            throw new AggregateException("Indexing and native media shutdown failed.", indexingFailure, cleanup);
                        throw;
                    }
                }
            }
        }, cancellationToken);
    }

public async Task SetIndexingPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        var changed = await indexing.SetPausedAsync(paused, cancellationToken);
        if (changed && !paused && stopping == 0) background?.RequestReconciliation();
    }

    private async Task IndexPathsBackgroundAsync(IReadOnlyList<string> roots, IReadOnlyList<string> paths, bool code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stores = await GetRepositoriesAsync(cancellationToken).ConfigureAwait(false);
        var repository = stores[code ? 1 : 0];
        var model = GetEmbeddings();
        var service = new IndexingService(repository, new ContentExtractor(), model);
        await service.IndexPathsAsync(roots, paths, code, null, cancellationToken).ConfigureAwait(false);
    }

public async Task<IReadOnlyList<string>> GetFailureSummaryAsync(int limit = 5, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var repository = (await GetRepositoriesAsync(cancellationToken))[0];
        return repository is SqliteIndexRepository sqlite
            ? await sqlite.ReadFailureSummaryAsync(limit, cancellationToken).ConfigureAwait(false)
            : Array.Empty<string>();
    }

public async Task<IndexingQueueStatus> GetIndexingQueueStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var coordinator = background;
        if (coordinator != null)
            return await coordinator.GetIndexingQueueStatusAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var stores = await GetRepositoriesAsync(cancellationToken).ConfigureAwait(false);
            if (stores.Count > 0)
            {
                var counts = await stores[0].GetTaskCountsAsync(cancellationToken).ConfigureAwait(false);
                return new IndexingQueueStatus(
                    Pending: counts.GetValueOrDefault("Pending"),
                    Stabilizing: counts.GetValueOrDefault("Stabilizing"),
                    Ready: counts.GetValueOrDefault("Ready"),
                    Running: counts.GetValueOrDefault("Running"),
                    Failed: counts.GetValueOrDefault("Failed"),
                    Deferred: counts.GetValueOrDefault("Deferred"),
                    Dropped: 0,
                    WatcherOverflows: 0,
                    LastReconcileUtc: null,
                    UsnState: settings.BackgroundIndexingEnabled ? "starting" : "background-disabled",
                    Budgets: Array.Empty<IndexBudgetState>());
            }
        }
        catch { }

        return new IndexingQueueStatus(0, 0, 0, 0, 0, 0, 0, 0, null, settings.BackgroundIndexingEnabled ? "starting" : "background-disabled", Array.Empty<IndexBudgetState>());
    }

public async Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var statuses = new List<EngineStatus>();
            foreach (var store in await GetRepositoriesAsync(cancellationToken))
                statuses.Add(await store.GetStatusAsync(cancellationToken));
            return new EngineStatus(statuses.Sum(s => s.Files), statuses.Sum(s => s.Done),
                statuses.Sum(s => s.Pending), statuses.Sum(s => s.Partial), statuses.Sum(s => s.Failed),
                IndexDirectory, "索引数据库已就绪。"
                    + (enableBackgroundIndexing ? "" : " 后台增量监听未启用。")
                    + (background?.Failure is null ? "" : " 后台监听异常: " + background.Failure.Message)
                    + (backgroundError is null || (backgroundError.Contains("ObjectDisposedException") && indexing.State == IndexingControlState.Running) ? "" : " " + backgroundError)
                    + (indexing.Failure is { } failure ? " 索引异常: " + failure : ""), indexing.State);
        }
        finally { lifecycle.Release(); }
    }

public async Task<int> ResetFailedFilesAsync(CancellationToken cancellationToken = default)
{
await lifecycle.WaitAsync(cancellationToken);
try
{
ObjectDisposedException.ThrowIf(disposed, this);
var stores = await GetRepositoriesAsync(cancellationToken);
var reset = 0;
foreach (var store in stores)
{
reset += await store.ResetFailedFilesAsync(cancellationToken);
}
return reset;
}
finally { lifecycle.Release(); }
}

/// <summary>
/// Reports the vector corpus size. The measured warm semantic latency grew from ~32 ms at 20k segments to
    /// ~155 ms at 100k, so this warns before the path approaches the 500 ms budget.
    /// </summary>
    public async Task<string> DescribeVectorScaleAsync(CancellationToken cancellationToken = default)
    {
        var repository = (await GetRepositoriesAsync(cancellationToken).ConfigureAwait(false))[0];
        var vectors = await repository.CountVectorsAsync(cancellationToken).ConfigureAwait(false);
        var verdict = vectors switch
        {
            >= 80_000 => "approaching the measured ceiling: plan sqlite-vec/ANN",
            >= 40_000 => "within budget but worth watching",
            _ => "comfortably within the BLOB + cosine path",
        };
        return vectors + " vector segment(s); " + verdict + ".";
    }

/// <summary>
    /// One rebuild window covers both a model change and a lexicon change: staging is built with the new
    /// profile and the segmented FTS together, validated, then swapped in atomically; the old database is kept.
    /// </summary>
    private async Task<IIndexRepository> EnsureGenerationAsync(IIndexRepository repository, IndexRequest request,
        EmbeddingInfo info, NativeMediaProcessClient? mediaClient, bool defer, IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        var dictionary = NativeSegmenter.Fingerprint(Array.Empty<string>());
        if (!IndexRebuildCoordinator.NeedsRebuild(IndexDirectory, info.ProfileId, dictionary)) return repository;
        // A generation covers the whole corpus by definition. A single-root request (for example a maintenance
        // job) must not narrow the new generation to its own folder, or the live index silently shrinks.
        var rebuildRoots = (settings.Folders ?? new List<string>())
            .Where(root => IsAuthorizedResult(root, false))
            .ToArray();
        if (rebuildRoots.Length != 0) request = request with { Roots = rebuildRoots };
        var generation = info.ProfileId[..Math.Min(12, info.ProfileId.Length)] + "-" + dictionary[..8] + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        backgroundError = "正在为新模型与分词词典重建索引（后台运行中，完成后将自动替换旧库）...";
        await repository.DisposeAsync().ConfigureAwait(false);
        repositories.Clear();
        var outcome = await IndexRebuildCoordinator.RebuildAsync(IndexDirectory, generation, async stagingRoot =>
        {
            await using var staging = new SqliteIndexRepository(Path.Combine(stagingRoot, "index.sqlite"));
            await staging.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await staging.SetMetaAsync("profile_id", info.ProfileId, cancellationToken).ConfigureAwait(false);
            await staging.SetMetaAsync(IndexRebuildCoordinator.LexiconSchemaKey, IndexRebuildCoordinator.LexiconSchema, cancellationToken).ConfigureAwait(false);
            await staging.SetMetaAsync(IndexRebuildCoordinator.DictionaryKey, dictionary, cancellationToken).ConfigureAwait(false);
            await staging.SetMetaAsync(IndexRebuildCoordinator.GenerationKey, generation, cancellationToken).ConfigureAwait(false);
            var builder = new IndexingService(staging, new ContentExtractor(mediaClient), embeddings!,
                defer || mediaClient is null ? null : new NativeMediaSamplingService(mediaClient),
                defer ? null : new NativeDocumentPages(mediaClient),
                defer ? null : new NativePresentationPages(mediaClient),
                allowDeferredStages: defer);
            await builder.IndexAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }, async database =>
        {
            // The probe must come from the staged data itself: a hardcoded word would fail whenever the
            // corpus simply does not contain it, blocking a perfectly good rebuild.
            await using var probe = new SqliteIndexRepository(database);
            await probe.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var files = await probe.ReadFileHeadersAsync(cancellationToken).ConfigureAwait(false);
            var sample = files.FirstOrDefault(file => file.State == "done");
            if (sample is null) return Array.Empty<string>();
            var term = Path.GetFileNameWithoutExtension(sample.Path);
            if (term.Length < 2) return new[] { "files:" + files.Count };
            var hits = await probe.KeywordSearchAsync(term, 3, cancellationToken).ConfigureAwait(false);
            return hits.Count == 0 ? new[] { "files:" + files.Count } : new[] { "keyword:" + hits.Count };
        }, cancellationToken).ConfigureAwait(false);
        repositories.Clear();
        var reopened = (await GetRepositoriesAsync(cancellationToken).ConfigureAwait(false))[0];
        backgroundError = outcome.Switched
            ? "索引已针对新模型重建完成并切换。"
            : "索引重建未切换: " + (outcome.Detail ?? "未知原因");
        return reopened;
    }
}
