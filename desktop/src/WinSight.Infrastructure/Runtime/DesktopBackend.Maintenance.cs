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

private volatile bool rebuildRunning;

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
        if (rebuildRunning) blockers.Add("a generation rebuild is running");
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
        if (rebuildRunning) return "already running";
        var info = await GetEmbeddings().GetInfoAsync(cancellationToken).ConfigureAwait(false);
        var roots = (settings.Folders ?? new List<string>()).Where(root => IsAuthorizedResult(root, false)).ToArray();
        if (roots.Length == 0) return "no authorized folders";
        rebuildRunning = true;
        rebuildStatus = "running: staging " + roots.Length + " root(s)";
        _ = Task.Run(async () =>
        {
            try
            {
                var dictionary = NativeSegmenter.Fingerprint(Array.Empty<string>());
                var generation = info.ProfileId[..Math.Min(12, info.ProfileId.Length)] + "-" + dictionary[..8] + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var repository = (await GetRepositoriesAsync(CancellationToken.None).ConfigureAwait(false))[0];
                await repository.DisposeAsync().ConfigureAwait(false);
                repositories.Clear();
                var outcome = await IndexRebuildCoordinator.RebuildAsync(IndexDirectory, generation, async stagingRoot =>
                {
                    await using var staging = new SqliteIndexRepository(Path.Combine(stagingRoot, "index.sqlite"));
                    await staging.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                    await staging.SetMetaAsync("profile_id", info.ProfileId, CancellationToken.None).ConfigureAwait(false);
                    await staging.SetMetaAsync(IndexRebuildCoordinator.LexiconSchemaKey, IndexRebuildCoordinator.LexiconSchema, CancellationToken.None).ConfigureAwait(false);
                    await staging.SetMetaAsync(IndexRebuildCoordinator.DictionaryKey, dictionary, CancellationToken.None).ConfigureAwait(false);
                    await staging.SetMetaAsync(IndexRebuildCoordinator.GenerationKey, generation, CancellationToken.None).ConfigureAwait(false);
                    var builder = new IndexingService(staging, new ContentExtractor(GetMediaClient()), embeddings!,
                        null, null, null, allowDeferredStages: true);
                    await builder.IndexAsync(new IndexRequest(roots), null, CancellationToken.None).ConfigureAwait(false);
                }, async database =>
                {
                    await using var probe = new SqliteIndexRepository(database);
                    await probe.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                    var files = await probe.ReadFilesAsync(CancellationToken.None).ConfigureAwait(false);
                    return files.Count == 0 ? Array.Empty<string>() : new[] { "files:" + files.Count };
                }, CancellationToken.None).ConfigureAwait(false);
                repositories.Clear();
                rebuildStatus = outcome.Switched
                    ? "done: rebuilt with " + roots.Length + " root(s); previous generation kept as " + Path.GetFileName(outcome.PreviousDirectory ?? "none")
                    : "failed: " + (outcome.Detail ?? "unknown");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException)
            {
                rebuildStatus = "failed: " + error.GetType().Name + ": " + error.Message;
            }
            finally
            {
                rebuildRunning = false;
                repositories.Clear();
            }
        }, CancellationToken.None);
        return "started";
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
            try
            {
                using var timer = new PeriodicTimer(maintenancePoll);
                while (await timer.WaitForNextTickAsync(lifetimeStop.Token).ConfigureAwait(false))
                {
                    if (stopping != 0 || indexing.State != IndexingControlState.Running || indexing.HasWork) continue;
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
}
