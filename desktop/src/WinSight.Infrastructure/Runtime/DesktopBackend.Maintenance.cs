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
private volatile string rebuildStatus = "idle";

private int rebuildRunning;

/// <summary>
    /// Lists everything that must be idle before an export: an indexing pass, a generation rebuild, or
    /// maintenance requests the app has not finished. Each entry is a reason a user can act on.
    /// </summary>
    public async Task<IReadOnlyList<string>> DescribeMigrationBlockersAsync(CancellationToken cancellationToken = default)
    {
        var blockers = await MigrationHostileWorkAsync(cancellationToken).ConfigureAwait(false);
        return blockers.Count == 0 ? Array.Empty<string>() : blockers;
    }

private async Task<List<string>> MigrationHostileWorkAsync(CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        if (indexing.HasWork) blockers.Add("indexing is running");
        if (Volatile.Read(ref rebuildRunning) != 0) blockers.Add("a generation rebuild is running");
        var store = JobStore;
        if (Directory.Exists(store.Inbox) && Directory.GetFiles(store.Inbox, "*.json").Length != 0) blockers.Add("maintenance requests are waiting to be drained");
        if (Directory.Exists(store.Claimed) && Directory.GetFiles(store.Claimed, "*.json").Length != 0) blockers.Add("a maintenance request is in progress");
        await Task.CompletedTask.ConfigureAwait(false);
        return blockers;
    }

/// <summary>Explains what a migration would do, without exporting or touching either directory.</summary>
    public Task<IndexMigrationResult?> InspectMigrationAsync(string targetDirectory, CancellationToken cancellationToken = default)
    {
        var outcome = new IndexMigrationService().Inspect(IndexDirectory, targetDirectory);
        return Task.FromResult<IndexMigrationResult?>(new IndexMigrationResult(outcome.Phase.ToString(), outcome.Source, outcome.Target, outcome.Detail));
    }

/// <summary>
    /// Exports the index to another directory and points the app at it. The old directory is kept untouched as
    /// the recovery point; the caller decides when to remove it.
    /// </summary>
    public async Task<IndexMigrationResult?> MigrateIndexAsync(string targetDirectory, CancellationToken cancellationToken = default)
    {
        // The export reads the live database; running it while a pass is writing would copy a moving target.
        var blockers = await MigrationHostileWorkAsync(cancellationToken).ConfigureAwait(false);
        if (blockers.Count != 0)
            return new IndexMigrationResult("Refused", IndexDirectory, targetDirectory, "Blocked: " + string.Join("; ", blockers) + ".");
        var outcome = await new IndexMigrationService().MigrateAsync(IndexDirectory, targetDirectory, cancellationToken).ConfigureAwait(false);
        if (outcome.Phase != MigrationPhase.Switched)
            return new IndexMigrationResult(outcome.Phase.ToString(), outcome.Source, outcome.Target, outcome.Detail);
        try
        {
            var current = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            current.IndexDirectory = outcome.Target;
            await SaveSettingsAsync(current, cancellationToken).ConfigureAwait(false);
            return new IndexMigrationResult(outcome.Phase.ToString(), outcome.Source, outcome.Target,
                outcome.Detail + " The app now uses the new location; the previous directory can be removed once verified.");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new IndexMigrationResult("ExportedNotSwitched", outcome.Source, outcome.Target,
                "The export succeeded but the settings pointer could not change: " + error.Message);
        }
    }

/// <summary>True when the live generation no longer matches the model or the lexicon.</summary>
    public async Task<bool> RebuildNeededAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        var info = await GetEmbeddings().GetInfoAsync(cancellationToken).ConfigureAwait(false);
        var dictionary = NativeSegmenter.Fingerprint(Array.Empty<string>());
        return IndexRebuildCoordinator.NeedsRebuild(IndexDirectory, info.ProfileId, dictionary);
    }

public string RebuildStatus => rebuildStatus;

/// <summary>
    /// Starts a generation rebuild in the background and returns immediately. The request covers every
    /// authorized root because a generation is the whole corpus; progress is visible through RebuildStatus.
    /// </summary>
    public async Task<string> StartRebuildAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        if (Interlocked.CompareExchange(ref rebuildRunning, 1, 0) != 0) return "already running";
        bool handedOff = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeStop.Token);
            await GetEmbeddings().GetInfoAsync(linked.Token).ConfigureAwait(false);
            string[] roots;
            await lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed || stopping != 0, this);
                roots = (settings.Folders ?? new List<string>()).Where(root => IsAuthorizedResult(root, false)).ToArray();
            }
            finally { lifecycle.Release(); }
            if (roots.Length == 0) return "no authorized folders";
            rebuildStatus = "running: staging " + roots.Length + " root(s)";
            handedOff = true;
            _ = Task.Run(async () =>
            {
                var stopToken = lifetimeStop.Token;
                bool acquiredReconfig = false;
                try
                {
                    await reconfiguration.WaitAsync(stopToken).ConfigureAwait(false);
                    acquiredReconfig = true;
                    await StopWatchingAsync().ConfigureAwait(false);
                    using var preemption = SuppressAndCancelBackfill();
                    await indexing.RunAsync(async workToken =>
                    {
                        using var runLock = AcquireRunLock();
                        if (runLock is null)
                        {
                            rebuildStatus = "failed: another indexing run is active";
                            return;
                        }
                        await backfillGate.WaitAsync(workToken).ConfigureAwait(false);
                        try
                        {
                            string[] currentRoots;
                            IEmbeddingService currentService;
                            await lifecycle.WaitAsync(workToken).ConfigureAwait(false);
                            try
                            {
                                ObjectDisposedException.ThrowIf(disposed || stopping != 0, this);
                                currentRoots = (settings.Folders ?? new List<string>()).Where(root => IsAuthorizedResult(root, false)).ToArray();
                                currentService = GetEmbeddings();
                            }
                            finally { lifecycle.Release(); }
                            if (currentRoots.Length == 0)
                            {
                                rebuildStatus = "failed: no authorized folders";
                                return;
                            }
                            var currentInfo = await currentService.GetInfoAsync(workToken).ConfigureAwait(false);
                            var dictionary = NativeSegmenter.Fingerprint(Array.Empty<string>());
                            var generation = currentInfo.ProfileId[..Math.Min(12, currentInfo.ProfileId.Length)] + "-" + dictionary[..8] + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                            var outcome = await IndexRebuildCoordinator.RebuildAsync(IndexDirectory, generation, async stagingRoot =>
                            {
                                await using var staging = new SqliteIndexRepository(Path.Combine(stagingRoot, "index.sqlite"));
                                await staging.InitializeAsync(workToken).ConfigureAwait(false);
                                await staging.SetMetaAsync("profile_id", currentInfo.ProfileId, workToken).ConfigureAwait(false);
                                await staging.SetMetaAsync(IndexRebuildCoordinator.LexiconSchemaKey, IndexRebuildCoordinator.LexiconSchema, workToken).ConfigureAwait(false);
                                await staging.SetMetaAsync(IndexRebuildCoordinator.DictionaryKey, dictionary, workToken).ConfigureAwait(false);
                                await staging.SetMetaAsync(IndexRebuildCoordinator.GenerationKey, generation, workToken).ConfigureAwait(false);
                                await using var mediaClient = GetMediaClient();
                                var builder = new IndexingService(staging, new ContentExtractor(mediaClient), currentService,
                                    null, null, null, allowDeferredStages: true);
                                await builder.IndexAsync(new IndexRequest(currentRoots), null, workToken).ConfigureAwait(false);
                            }, async database =>
                            {
                                await using var probe = new SqliteIndexRepository(database);
                                await probe.InitializeAsync(workToken).ConfigureAwait(false);
                                var files = await probe.ReadFileHeadersAsync(workToken).ConfigureAwait(false);
                                return files.Count == 0 ? Array.Empty<string>() : new[] { "files:" + files.Count };
                            }, workToken, swapGuard: performSwitch => ExecuteProtectedRebuildSwapAsync(performSwitch, workToken)).ConfigureAwait(false);

                            rebuildStatus = outcome.Switched
                                ? "done: rebuilt with " + currentRoots.Length + " root(s); previous generation kept as " + Path.GetFileName(outcome.PreviousDirectory ?? "none")
                                : "failed: " + (outcome.Detail ?? "unknown");
                        }
                        finally { backfillGate.Release(); }
                    }, stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    rebuildStatus = stopToken.IsCancellationRequested
                        ? "cancelled: shutdown in progress"
                        : "cancelled: indexing paused";
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or ObjectDisposedException)
                {
                    rebuildStatus = "failed: " + error.GetType().Name + ": " + error.Message;
                }
                finally
                {
                    Interlocked.Exchange(ref rebuildRunning, 0);
                    if (stopping == 0) ConfigureWatching();
                    if (acquiredReconfig) reconfiguration.Release();
                }
            }, CancellationToken.None);
            return "started";
        }
        finally
        {
            if (!handedOff) Interlocked.Exchange(ref rebuildRunning, 0);
        }
    }

private MaintenanceJobStore JobStore => new(dataDirectory);

/// <summary>Returns the grant the user created, or null when maintenance is not authorized.</summary>
    public Task<string?> GetMaintenanceGrantAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(MaintenanceJobStore.ReadGrant(dataDirectory));

/// <summary>Creates the grant explicitly; only the user's click produces this file.</summary>
    public async Task<string?> GrantMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        var grantId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var payload = JsonSerializer.Serialize(new { grantId, createdUtc = DateTime.UtcNow.ToString("o"), scope = "index_directory" });
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "maintenance-grant.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, payload, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        return grantId;
    }

public Task RevokeMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(dataDirectory, "maintenance-grant.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

/// <summary>
    /// Claims maintenance requests left by the bridge and records an outcome for each. A request runs only when
    /// the grant still matches and the path is inside an authorized root; everything else is recorded as refused
    /// with its reason instead of being dropped silently.
    /// </summary>
    public async Task<IReadOnlyList<string>> DrainMaintenanceJobsAsync(CancellationToken cancellationToken = default)
    {
        var outcomes = new List<string>();
        while (JobStore.ClaimNext() is { } job)
        {
            // The grant is the user's standing authorization; a request made under an older or revoked grant is refused.
            if (MaintenanceJobStore.ReadGrant(dataDirectory) is not { } grant || !string.Equals(grant, job.GrantId, StringComparison.Ordinal))
            {
                await JobStore.CompleteAsync(job, false, "refused: the maintenance grant was revoked or does not match this request", cancellationToken).ConfigureAwait(false);
                outcomes.Add(job.Id + ": refused (no matching maintenance grant)");
                continue;
            }
            if (!IsAuthorizedResult(job.Root, false))
            {
                await JobStore.CompleteAsync(job, false, "refused: the path is outside the authorized folders", cancellationToken).ConfigureAwait(false);
                outcomes.Add(job.Id + ": refused (outside authorized folders)");
                continue;
            }
            // Accepted means it really runs: the request goes through the same manual indexing path the UI uses.
            try
            {
                if (await RebuildNeededAsync(cancellationToken).ConfigureAwait(false))
                {
                    await JobStore.CompleteAsync(job, false,
                        "needs_rebuild: the index generation no longer matches the model or lexicon; rebuild it from Settings, then retry",
                        cancellationToken).ConfigureAwait(false);
                    outcomes.Add(job.Id + ": needs_rebuild");
                    continue;
                }
                var before = await CountIndexedFilesAsync(job.Root, cancellationToken).ConfigureAwait(false);
                await IndexCoreAsync(new IndexRequest([job.Root]), null, allowRebuild: false, cancellationToken).ConfigureAwait(false);
                var after = await CountIndexedFilesAsync(job.Root, cancellationToken).ConfigureAwait(false);
                var added = after - before;
                var detail = "accepted: " + after + " file(s) indexed under the request, " + added + " new";
                await JobStore.CompleteAsync(job, true, detail, cancellationToken).ConfigureAwait(false);
                outcomes.Add(job.Id + ": " + detail);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException)
            {
                await JobStore.CompleteAsync(job, false, "accepted but indexing failed: " + error.Message, cancellationToken).ConfigureAwait(false);
                outcomes.Add(job.Id + ": accepted, indexing failed (" + error.GetType().Name + ")");
            }
        }
        return outcomes;
    }

/// <summary>
    /// Processes bridge requests without a click, but only while the user's maintenance grant exists, indexing is not paused
    /// and nothing else is running. Without a grant the loop does nothing, so an agent cannot start indexing on its own.
    /// </summary>
    private void StartMaintenanceLoop()
    {
        maintenanceLoop = Task.Run(async () =>
        {
            var nextIdleBackfillRetryUtc = DateTimeOffset.MinValue;
            try
            {
                using var timer = new PeriodicTimer(maintenancePoll);
                while (await timer.WaitForNextTickAsync(lifetimeStop.Token).ConfigureAwait(false))
                {
                    if (stopping != 0 || indexing.State != IndexingControlState.Running || indexing.HasWork) continue;

                    // Idle page reclamation: reclaim unused pages from deleted/reindexed files
                    try
                    {
                        var repos = await GetRepositoriesAsync(lifetimeStop.Token).ConfigureAwait(false);
                        foreach (var repo in repos)
                        {
                            if (repo is SqliteIndexRepository sqliteRepo)
                            {
                                var reclaimed = await sqliteRepo.IncrementalVacuumAsync(500, lifetimeStop.Token).ConfigureAwait(false);
                                if (reclaimed > 0) AppLog.Info($"Idle maintenance reclaimed {reclaimed} database pages.");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }

                    if (DateTimeOffset.UtcNow >= nextIdleBackfillRetryUtc)
                    {
                        try
                        {
                            await RunIdleBackfillTickAsync(128, lifetimeStop.Token).ConfigureAwait(false);
                        }
                        catch (IndexingPausedException)
                        {
                            // Indexing was paused concurrently; yield without error backoff.
                        }
                        catch (OperationCanceledException) when (stopping == 0 && !lifetimeStop.IsCancellationRequested)
                        {
                            // Preempted by user pause, interactive query, or generation rebuild; keep maintenance loop alive.
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            nextIdleBackfillRetryUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                            AppLog.Warn("Idle vector backfill tick failed; backing off 5 minutes.", ex);
                        }
                    }

                    if (MaintenanceJobStore.ReadGrant(dataDirectory) is null) continue;
                    var inbox = JobStore.Inbox;
                    if (!Directory.Exists(inbox) || Directory.GetFiles(inbox, "*.json").Length == 0) continue;
                    var outcomes = await DrainMaintenanceJobsAsync(lifetimeStop.Token).ConfigureAwait(false);
                    AppLog.Info("maintenance requests processed: " + string.Join("; ", outcomes));
                }
            }
            catch (OperationCanceledException) when (stopping != 0) { }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.Data.Common.DbException)
            {
                AppLog.Error("maintenance loop stopped", error);
            }
        });
    }

    internal async Task<int> RunIdleBackfillTickAsync(int maxSegments = 128, CancellationToken cancellationToken = default)
    {
        if (stopping != 0 || indexing.State != IndexingControlState.Running || indexing.HasWork
            || Volatile.Read(ref rebuildRunning) != 0 || backfillGate.CurrentCount == 0)
            return 0;
        var nowLocal = DateTime.Now;
        if (settings.IndexHoursOn && !BackgroundIndexCoordinator.IsScheduledTime(settings.IndexHours, nowLocal.Hour * 60 + nowLocal.Minute))
            return 0;
        if (background is not { } coord) return 0;

        var budgets = coord.BudgetController.ComputeBudgets();
        if (budgets.FirstOrDefault(b => b.Class == "Backfill") is not { State: "active", InferenceConcurrency: > 0 })
            return 0;

        var qStatus = await coord.GetIndexingQueueStatusAsync(cancellationToken).ConfigureAwait(false);
        if (qStatus.Pending != 0 || qStatus.Stabilizing != 0 || qStatus.Ready != 0 || qStatus.Running != 0)
            return 0;

        var stores = await GetRepositoriesAsync(cancellationToken).ConfigureAwait(false);
        bool hasPending = false;
        foreach (var store in stores)
        {
            if ((await store.ReadPendingEmbeddingSegmentsAsync(1, 0, cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                hasPending = true;
                break;
            }
        }
        if (!hasPending) return 0;

        return await BackfillMissingEmbeddingsAsync(maxSegments, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs in-place embedding backfill for legacy text-only segments without re-parsing files from disk.
    /// Uses the active inference accelerator (DirectML GPU or NPU) to generate embeddings at maximum speed.
    /// </summary>
    public async Task<int> BackfillMissingEmbeddingsAsync(
        int maxSegments = 0,
        IProgress<(int Processed, int Remaining)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSegments);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeStop.Token);
        const int batchSize = 32;
        int totalProcessed = 0;

        await indexing.RunConcurrentAsync(async token =>
        {
            await backfillGate.WaitAsync(token).ConfigureAwait(false);
            CancellationTokenSource? backfillRunCts = null;
            try
            {
                backfillRunCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (backfillCancelSync)
                {
                    if (backfillPreemptions > 0)
                        backfillRunCts.Cancel();
                    activeBackfillCts = backfillRunCts;
                }
                var runToken = backfillRunCts.Token;
                runToken.ThrowIfCancellationRequested();

                using var runLock = AcquireRunLock()
                    ?? throw new IOException("Another WinSight process is currently indexing this data directory.");
                IIndexRepository[] stores;
                IEmbeddingService service;
                await lifecycle.WaitAsync(runToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(disposed || stopping != 0, this);
                    stores = (await GetRepositoriesAsync(runToken).ConfigureAwait(false)).ToArray();
                    service = GetEmbeddings();
                }
                finally { lifecycle.Release(); }

                var info = service is IHostEmbeddingService client
                    ? await client.GetOrStartInfoAsync(interactive: false, runToken).ConfigureAwait(false)
                    : await service.GetInfoAsync(runToken).ConfigureAwait(false);
                if (info == null || !info.Capabilities.Contains("text", StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Embedding service is not available or does not support text.");
                progress?.Report((0, -1));

                foreach (var repo in stores)
                {
                    var currentProfile = await repo.GetMetaAsync("profile_id", runToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(currentProfile))
                    {
                        await repo.SetMetaAsync("profile_id", info.ProfileId, runToken).ConfigureAwait(false);
                    }
                    else if (!FilePolicy.IsCompatibleProfile(currentProfile, info.ProfileId))
                    {
                        throw new InvalidOperationException($"Active embedding profile '{info.ProfileId}' is incompatible with index profile '{currentProfile}'. Rebuild the index before backfilling vectors.");
                    }
                }

                int batchesRun = 0;
                int failedItems = 0;
                string? firstFailure = null;
                foreach (var repo in stores)
                {
                    long afterId = 0;
                    while (!runToken.IsCancellationRequested)
                    {
                        if (maxSegments > 0 && totalProcessed >= maxSegments) break;
                        int defaultBatch = maxSegments == 0 && batchesRun == 0 ? 64 : 256;
                        int remainingBudget = maxSegments > 0 ? maxSegments - totalProcessed : defaultBatch;
                        int queryLimit = Math.Min(remainingBudget, defaultBatch);
                        var segments = await repo.ReadPendingEmbeddingSegmentsAsync(queryLimit, afterId, runToken).ConfigureAwait(false);
                        if (segments.Count == 0) break;
                        afterId = segments[^1].Id;
                        if (batchesRun == 0)
                        {
                            progress?.Report((totalProcessed, segments.Count));
                        }
                        batchesRun++;

                        foreach (var chunk in segments.Chunk(batchSize))
                        {
                            runToken.ThrowIfCancellationRequested();
                            var inputs = chunk.Select(s => new EmbeddingInput(
                                "text",
                                FilePolicy.FormatTextEmbeddingPrompt(s.Title, s.Excerpt, s.Kind, maxNonPageChars: FilePolicy.DefaultMaxNonPageEmbeddingChars)
                            )).ToArray();

                            IReadOnlyList<EmbeddingResult> results;
                            const int maxPreemptRetries = 3;
                            int preemptRetries = 0;
                            while (true)
                            {
                                runToken.ThrowIfCancellationRequested();
                                var preemptToken = preemptRetries < maxPreemptRetries
                                    ? (background?.BudgetController.BackfillInferenceToken ?? CancellationToken.None)
                                    : CancellationToken.None;
                                try
                                {
                                    if (service is InferenceHostClient hostClient)
                                    {
                                        results = await hostClient.EmbedAsync(inputs, runToken, preemptToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        using var embedCts = CancellationTokenSource.CreateLinkedTokenSource(runToken, preemptToken);
                                        results = await service.EmbedAsync(inputs, embedCts.Token).ConfigureAwait(false);
                                    }
                                    break;
                                }
                                catch (OperationCanceledException) when (!runToken.IsCancellationRequested && preemptToken.IsCancellationRequested)
                                {
                                    preemptRetries++;
                                    AppLog.Info("Backfill chunk yielded to interactive search query; resuming after brief backoff.");
                                    await Task.Delay(100, runToken).ConfigureAwait(false);
                                }
                            }
                            if (results.Count != chunk.Length)
                                throw new InvalidDataException($"Embedding service returned {results.Count} results for {chunk.Length} inputs.");

                            var updates = new List<SegmentVectorUpdate>();
                            for (int i = 0; i < chunk.Length; i++)
                            {
                                if (results[i].Error is null && results[i].Vector is { } vec)
                                {
                                    updates.Add(new SegmentVectorUpdate(chunk[i].Id, chunk[i].FileId, vec));
                                }
                                else
                                {
                                    failedItems++;
                                    firstFailure ??= results[i].Error ?? "Embedding vector is missing.";
                                    AppLog.Warn($"Vector backfill failed for segment {chunk[i].Id}: {results[i].Error ?? "empty vector"}");
                                }
                            }

                            if (updates.Count > 0)
                            {
                                int saved = await repo.SaveBackfilledVectorsAsync(updates, runToken).ConfigureAwait(false);
                                if (saved > 0)
                                {
                                    totalProcessed += saved;
                                    progress?.Report((totalProcessed, segments.Count));
                                }
                            }
                            else if (failedItems > 0)
                            {
                                throw new InvalidOperationException($"Embedding backfill failed ({failedItems} segment(s)): {firstFailure}");
                            }
                        }
                    }
                }

                runToken.ThrowIfCancellationRequested();

                if (failedItems > 0)
                {
                    throw new InvalidOperationException($"Vector backfill finished with {failedItems} failed segment(s): {firstFailure}");
                }
            }
            finally
            {
                lock (backfillCancelSync)
                {
                    if (ReferenceEquals(activeBackfillCts, backfillRunCts))
                        activeBackfillCts = null;
                }
                backfillRunCts?.Dispose();
                backfillGate.Release();
            }
        }, linked.Token).ConfigureAwait(false);

        return totalProcessed;
    }

    public async Task<StorageMetrics?> GetStorageMetricsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        var dbPath = Path.Combine(IndexDirectory, "index.sqlite");
        long dbBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
        var repo = (await GetRepositoriesAsync(cancellationToken).ConfigureAwait(false))[0];
        long totalSegs = 0, embeddedSegs = 0;
        if (repo is SqliteIndexRepository sqliteRepo)
        {
            (totalSegs, embeddedSegs) = await sqliteRepo.GetSegmentVectorCountsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            embeddedSegs = await repo.CountVectorsAsync(cancellationToken).ConfigureAwait(false);
            totalSegs = embeddedSegs;
        }

        string cacheDir = Path.Combine(dataDirectory, "source-preview", "cache");
        long cacheBytes = 0;
        int cacheFiles = 0;
        if (Directory.Exists(cacheDir))
        {
            foreach (var f in new DirectoryInfo(cacheDir).EnumerateFiles("*.png"))
            {
                cacheFiles++;
                cacheBytes += f.Length;
            }
        }
        return new StorageMetrics(dbBytes, totalSegs, embeddedSegs, cacheBytes, cacheFiles);
    }

    public Task<int> RunVectorBackfillAsync(int maxSegments = 0, IProgress<(int Processed, int Remaining)>? progress = null, CancellationToken cancellationToken = default) =>
        BackfillMissingEmbeddingsAsync(maxSegments, progress, cancellationToken);

    public Task<int> ClearPreviewCacheAsync(CancellationToken cancellationToken = default)
    {
        RasterLruCache.Clear();
        string cacheDir = Path.Combine(dataDirectory, "source-preview", "cache");
        int deleted = 0;
        if (Directory.Exists(cacheDir))
        {
            foreach (var file in Directory.EnumerateFiles(cacheDir, "*.png"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Delete(file); deleted++; } catch { }
            }
        }
        return Task.FromResult(deleted);
    }

    public async Task<int> CompactDatabaseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        var repos = await GetRepositoriesAsync(cancellationToken).ConfigureAwait(false);
        int reclaimed = 0;
        foreach (var repo in repos)
        {
            if (repo is SqliteIndexRepository sqliteRepo)
                reclaimed += await sqliteRepo.CompactStorageAsync(50000, cancellationToken).ConfigureAwait(false);
        }
        return reclaimed;
    }

    public async Task<int> CountFolderFilesAsync(string root, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root)) return 0;
        return await CountIndexedFilesAsync(root, cancellationToken).ConfigureAwait(false);
    }
}

