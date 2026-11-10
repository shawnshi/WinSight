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

private readonly IndexingController indexing = new();

private readonly SemaphoreSlim reconfiguration = new(1, 1);

private readonly CancellationTokenSource lifetimeStop = new();

private BackgroundIndexCoordinator? background;

private string? backgroundError;

private int stopping;

private readonly string applicationDirectory;

private readonly string dataDirectory;

private readonly bool enableBackgroundIndexing;

private readonly Func<bool> isOnAcPower;

private readonly List<IIndexRepository> repositories = [];

private IEmbeddingService? embeddings;

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
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopping != 0 || disposed) return;
            try { await StopSourceOperationsAsync(); } catch { }
            foreach (var store in repositories)
            {
                if (store is SqliteIndexRepository sqlite)
                {
                    sqlite.TrimMemory();
                }
            }
        }
        finally { lifecycle.Release(); }
    }

private async Task ReleaseResourcesAsync()
    {
        var failures = new List<Exception>();
        try { await StopSourceOperationsAsync(); }
        catch(Exception error) { failures.Add(error); }
        foreach (var store in repositories)
        {
            try { await store.DisposeAsync(); }
            catch (Exception error) { failures.Add(error); }
        }
        repositories.Clear();
        try { if (embeddings is not null) await embeddings.DisposeAsync(); }
        catch (Exception error) { failures.Add(error); }
        finally { embeddings = null; }
        if (failures.Count != 0) throw new AggregateException("Backend resource cleanup failed.", failures);
    }

private void ConfigureWatching()
    {
        if (!enableBackgroundIndexing || background is not null || !settings.BackgroundIndexingEnabled || !settings.SetUp) return;
        background = new BackgroundIndexCoordinator(settings,
            async (request, token) =>
            {
                await IndexAsync(request, cancellationToken: token);
                backgroundError = null;
            },
            error => backgroundError = error.GetType().Name + ": " + error.Message,
            ignoredRoots: [dataDirectory, IndexDirectory],
            indexingAllowed: () => indexing.State == IndexingControlState.Running && stopping == 0,
            isOnAcPower: isOnAcPower,
            releaseCaches: ReleaseCachesForMemoryPressureAsync);
    }

private async Task StopWatchingAsync()
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

private async Task RetireLifetimeAsync(Task cancelled)
    {
        try { await cancelled; }
        catch (Exception error) { backgroundError = "Shutdown cancellation failed: " + error.Message; }
        finally { lifetimeStop.Dispose(); }
    }
}
