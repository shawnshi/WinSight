using WinSight.Core.Diagnostics;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Inference;
using WinSight.Infrastructure.Media;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;

namespace WinSight.Infrastructure.Runtime;

public sealed record DesktopOptions(string DataDirectory, string? ApplicationDirectory = null,
    bool EnableBackgroundIndexing = true)
{
internal Func<bool>? IsOnAcPower { get; init; }

/// <summary>How often the app looks for maintenance requests when the user granted maintenance (default 20 s).</summary>
    internal TimeSpan? MaintenancePollInterval { get; init; }

}

public sealed partial class DesktopBackend : IDesktopBackend
{
    private readonly JsonSettingsStore settingsStore;

private readonly SemaphoreSlim lifecycle = new(1, 1);

private readonly SemaphoreSlim repositoryGate = new(1, 1);

private readonly object embeddingsSync = new();

private readonly IndexingController indexing = new();

private readonly SemaphoreSlim backfillGate = new(1, 1);

private readonly object backfillCancelSync = new();
private CancellationTokenSource? activeBackfillCts;
private int backfillPreemptions;

private readonly object runLockSync = new();
private IndexRunLock? activeRunLock;
private int activeRunLockCount;

private readonly SemaphoreSlim reconfiguration = new(1, 1);

private readonly CancellationTokenSource lifetimeStop = new();

private readonly SemaphoreSlim watchingLock = new(1, 1);
private BackgroundIndexCoordinator? background;

private string? backgroundError;

private int stopping;

private readonly string applicationDirectory;

private readonly string dataDirectory;

private readonly bool enableBackgroundIndexing;

private readonly Func<bool> isOnAcPower;

private readonly List<IIndexRepository> repositories = [];

private IEmbeddingService? embeddings;

private EmbeddingInfo? cachedEmbeddingInfo;

private IEmbeddingService? cachedEmbeddingInfoOwner;

private AppSettings settings;

private bool disposed;

private readonly TimeSpan maintenancePoll;

private Task? maintenanceLoop;

private DesktopBackend(DesktopOptions options, JsonSettingsStore store, AppSettings settings)
    {
        dataDirectory = Path.GetFullPath(options.DataDirectory);
        applicationDirectory = Path.GetFullPath(options.ApplicationDirectory ?? AppContext.BaseDirectory);
        settingsStore = store;
        enableBackgroundIndexing = options.EnableBackgroundIndexing;
        isOnAcPower = options.IsOnAcPower ?? PowerSource.IsOnAcPower;
        maintenancePoll = options.MaintenancePollInterval ?? TimeSpan.FromSeconds(20);
        this.settings = settings;
    }

public static async Task<DesktopBackend> CreateAsync(DesktopOptions options, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(options.DataDirectory)) throw new ArgumentException("Data directory must be absolute.");
        var store = new JsonSettingsStore(Path.Combine(options.DataDirectory, "settings.json"));
        var settings = await store.LoadAsync(cancellationToken);
        var backend = new DesktopBackend(options, store, settings);
        backend.ConfigureWatching();
        if (options.EnableBackgroundIndexing) backend.StartMaintenanceLoop();
        return backend;
    }

public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        return settingsStore.LoadAsync(cancellationToken);
    }

/// <summary>Raised after settings were persisted and applied; the app uses it for side effects such as the login item.</summary>
    public event Action<AppSettings>? SettingsSaved;

public async Task SaveSettingsAsync(AppSettings updated, CancellationToken cancellationToken = default)
    {
        JsonSettingsStore.Validate(updated);
        await reconfiguration.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(stopping != 0, this);
            await StopWatchingAsync();
            await lifecycle.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                bool structuralChange = !string.Equals(settings.IndexDirectory, updated.IndexDirectory, StringComparison.OrdinalIgnoreCase)
                    || !settings.Folders.SequenceEqual(updated.Folders, StringComparer.OrdinalIgnoreCase)
                    || !settings.CodeFolders.SequenceEqual(updated.CodeFolders, StringComparer.OrdinalIgnoreCase);

                if (structuralChange && indexing.HasWork)
                    throw new InvalidOperationException("Stop indexing before changing settings.");

                await settingsStore.SaveAsync(updated, cancellationToken);
                settings = await settingsStore.LoadAsync(cancellationToken);
                if (structuralChange)
                {
                    await ReleaseResourcesAsync();
                }
                AppLog.Info("settings saved");
                try { SettingsSaved?.Invoke(settings); }
                catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException) { AppLog.Warn("settings side effect failed", error); }
            }
            finally { lifecycle.Release(); }
        }
        finally
        {
            if (stopping == 0) ConfigureWatching();
            reconfiguration.Release();
        }
    }

// Memory pressure must free model, vector and preview caches, not merely halve concurrency.
    private async Task ReleaseCachesForMemoryPressureAsync(CancellationToken cancellationToken)
    {
        IIndexRepository[] openStores;
        IEmbeddingService? currentEmbeddings;
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopping != 0 || disposed) return;
            await repositoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { openStores = repositories.ToArray(); }
            finally { repositoryGate.Release(); }
            lock (embeddingsSync) { currentEmbeddings = embeddings; }
        }
        finally { lifecycle.Release(); }

        // Release heavy I/O and IPC caches outside lifecycle so interactive SearchAsync cannot be blocked behind TrimIdleSessionsAsync.
        try { await StopSourceOperationsAsync().ConfigureAwait(false); } catch { }
        foreach (var store in openStores)
        {
            if (store is SqliteIndexRepository sqlite)
            {
                try { sqlite.TrimMemory(); }
                catch (ObjectDisposedException) { }
            }
        }
        if (currentEmbeddings is InferenceHostClient hostClient)
        {
            try { await hostClient.TrimIdleSessionsAsync(cancellationToken).ConfigureAwait(false); } catch { }
        }
    }

private async Task DisposeRepositoriesCoreAsync()
    {
        var failures = new List<Exception>();
        try
        {
            foreach (var store in repositories)
            {
                try { await store.DisposeAsync(); }
                catch (Exception error) { failures.Add(error); }
            }
        }
        finally
        {
            repositories.Clear();
        }
        if (failures.Count != 0) throw new AggregateException("Repository cleanup failed.", failures);
    }

private async Task ReleaseResourcesAsync()
    {
        var failures = new List<Exception>();
        try { await StopSourceOperationsAsync(); }
        catch(Exception error) { failures.Add(error); }
        await repositoryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeRepositoriesCoreAsync();
        }
        catch (AggregateException error) { failures.AddRange(error.InnerExceptions); }
        catch (Exception error) { failures.Add(error); }
        finally { repositoryGate.Release(); }

        IEmbeddingService? currentEmbeddings;
        lock (embeddingsSync)
        {
            currentEmbeddings = embeddings;
            embeddings = null;
            cachedEmbeddingInfo = null;
            cachedEmbeddingInfoOwner = null;
        }
        try { if (currentEmbeddings is not null) await currentEmbeddings.DisposeAsync(); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Backend resource cleanup failed.", failures);
    }

private void ConfigureWatching()
    {
        if (!enableBackgroundIndexing || !settings.BackgroundIndexingEnabled || !settings.SetUp || stopping != 0) return;
        _ = StartWatchingAsync();
    }

private async Task StartWatchingAsync()
    {
        if (!enableBackgroundIndexing || !settings.BackgroundIndexingEnabled || !settings.SetUp || stopping != 0) return;
        await watchingLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (background != null || stopping != 0 || !settings.BackgroundIndexingEnabled || !settings.SetUp) return;
            var stores = await GetRepositoriesAsync(lifetimeStop.Token).ConfigureAwait(false);
            var repository = stores[0];
            var codeRepository = stores[1];
            await repository.ResetHangingTasksAsync(lifetimeStop.Token).ConfigureAwait(false);
            await codeRepository.ResetHangingTasksAsync(lifetimeStop.Token).ConfigureAwait(false);

            background = new BackgroundIndexCoordinator(
                settings,
                async (request, token) =>
                {
                    await IndexAsync(request, cancellationToken: token);
                    backgroundError = null;
                },
                error => {
                    if (error is not ObjectDisposedException)
                    {
                        backgroundError = error.GetType().Name + ": " + error.Message;
                    }
                },
                ignoredRoots: [dataDirectory, IndexDirectory],
                indexingAllowed: () => indexing.State == IndexingControlState.Running && stopping == 0,
                isOnAcPower: isOnAcPower,
                repository: repository,
                indexPaths: IndexPathsBackgroundAsync,
                releaseCaches: ReleaseCachesForMemoryPressureAsync,
                codeRepository: codeRepository);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            backgroundError = error.GetType().Name + ": " + error.Message;
            AppLog.Warn("Failed to start background index watching", error);
        }
        finally
        {
            watchingLock.Release();
        }
    }

private async Task StopWatchingAsync()
    {
        await watchingLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = background;
            background = null;
            if (previous is null) return;
            try { await previous.DisposeAsync(); }
            catch (Exception error)
            {
                backgroundError = error.GetType().Name + ": " + error.Message;
                throw;
            }
        }
        finally
        {
            watchingLock.Release();
        }
    }

public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        var failures = new List<Exception>();
        var stopped = indexing.StopAsync();
        var cancelled = lifetimeStop.CancelAsync();
        try { await stopped; }
        catch (Exception error) { failures.Add(error); }
        try { await cancelled.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) { failures.Add(error); }
        if (maintenanceLoop is not null)
        {
            try { await maintenanceLoop.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException) { failures.Add(error); }
        }
        await reconfiguration.WaitAsync();
        try
        {
            try { await StopWatchingAsync(); }
            catch (Exception error) { failures.Add(error); }
            await lifecycle.WaitAsync();
            try
            {
                disposed = true;
                try { await ReleaseResourcesAsync(); }
                catch (Exception error) { failures.Add(error); }
            }
            finally { lifecycle.Release(); }
            if (failures.Count != 0) throw new AggregateException("Backend shutdown completed with errors.", failures);
        }
        finally
        {
            reconfiguration.Release();
            if (cancelled.IsCompleted) lifetimeStop.Dispose();
            else _ = RetireLifetimeAsync(cancelled);
        }
    }

    private IDisposable? AcquireRunLock()
    {
        lock (runLockSync)
        {
            if (activeRunLockCount == 0)
            {
                activeRunLock = IndexRunLock.TryAcquire(dataDirectory);
                if (activeRunLock is null) return null;
            }
            activeRunLockCount++;
            return new RunLockLease(this);
        }
    }

    private void ReleaseRunLock()
    {
        lock (runLockSync)
        {
            if (activeRunLockCount <= 0) return;
            activeRunLockCount--;
            if (activeRunLockCount == 0)
            {
                activeRunLock?.Dispose();
                activeRunLock = null;
            }
        }
    }

    private sealed class RunLockLease(DesktopBackend owner) : IDisposable
    {
        private DesktopBackend? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseRunLock();
    }

    private IDisposable SuppressAndCancelBackfill()
    {
        CancellationTokenSource? cts;
        lock (backfillCancelSync)
        {
            backfillPreemptions++;
            cts = activeBackfillCts;
        }
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        return new PreemptionLease(this);
    }

    private void ReleaseBackfillPreemption()
    {
        lock (backfillCancelSync)
        {
            if (backfillPreemptions > 0) backfillPreemptions--;
        }
    }

    private sealed class PreemptionLease(DesktopBackend owner) : IDisposable
    {
        private DesktopBackend? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseBackfillPreemption();
    }

    private async Task<RebuildOutcome> ExecuteProtectedRebuildSwapAsync(Func<Task<RebuildOutcome>> performSwitch, CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed || stopping != 0, this);
            await repositoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DisposeRepositoriesCoreAsync().ConfigureAwait(false);
                return await performSwitch().ConfigureAwait(false);
            }
            finally { repositoryGate.Release(); }
        }
        finally { lifecycle.Release(); }
    }

private async Task RetireLifetimeAsync(Task cancelled)
    {
        try { await cancelled; }
        catch (Exception error) { backgroundError = "Shutdown cancellation failed: " + error.Message; }
        finally { lifetimeStop.Dispose(); }
    }
}
