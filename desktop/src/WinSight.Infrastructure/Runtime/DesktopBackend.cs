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

    private DesktopBackend(DesktopOptions options, JsonSettingsStore store, AppSettings settings)
    {
        dataDirectory = Path.GetFullPath(options.DataDirectory);
        applicationDirectory = Path.GetFullPath(options.ApplicationDirectory ?? AppContext.BaseDirectory);
        settingsStore = store;
        enableBackgroundIndexing = options.EnableBackgroundIndexing;
        isOnAcPower = options.IsOnAcPower ?? PowerSource.IsOnAcPower;
        this.settings = settings;
    }

    public static async Task<DesktopBackend> CreateAsync(DesktopOptions options, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(options.DataDirectory)) throw new ArgumentException("Data directory must be absolute.");
        var store = new JsonSettingsStore(Path.Combine(options.DataDirectory, "settings.json"));
        var settings = await store.LoadAsync(cancellationToken);
        var backend = new DesktopBackend(options, store, settings);
        backend.ConfigureWatching();
        return backend;
    }

    public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        return settingsStore.LoadAsync(cancellationToken);
    }

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
                if (indexing.HasWork) throw new InvalidOperationException("Stop indexing before changing settings.");
                await settingsStore.SaveAsync(updated, cancellationToken);
                settings = await settingsStore.LoadAsync(cancellationToken);
                await ReleaseResourcesAsync();
            }
            finally { lifecycle.Release(); }
        }
        finally
        {
            if (stopping == 0) ConfigureWatching();
            reconfiguration.Release();
        }
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Query);
        if (request.Limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(request), "Limit must be in 1..200.");
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeStop.Token);
        cancellationToken = linked.Token;
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var codeQuery = request.Kind == "code" || request.Query.TrimStart().StartsWith("code:", StringComparison.OrdinalIgnoreCase);
            var roots = (codeQuery ? settings.CodeFolders : settings.Folders).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (request.Root is not null && !IsAuthorizedResult(request.Root, codeQuery))
                throw new UnauthorizedAccessException("Search root is outside the authorized selection or is an application data directory.");
            if (roots.Length == 0) return new SearchResponse([], TimeSpan.Zero, false, "Choose an authorized folder before searching.");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var stores = await GetRepositoriesAsync(cancellationToken);
            var service = new SearchService(stores[codeQuery ? 1 : 0], request.Mode == SearchMode.Keyword ? null : GetEmbeddings(),
                path => IsAuthorizedResult(path, codeQuery));
            var response = await service.SearchAsync(request, cancellationToken);
            return response with { Elapsed = clock.Elapsed };
        }
        finally { lifecycle.Release(); }
    }

    public async Task IndexAsync(IndexRequest request, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeStop.Token);
        cancellationToken = linked.Token;
        await indexing.RunAsync(async token =>
        {
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
                var service = new IndexingService(repository, new ContentExtractor(mediaClient), model,
                    mediaClient is null ? null : new NativeMediaSamplingService(mediaClient), new NativeDocumentPages(mediaClient), new NativePresentationPages(mediaClient));
                await service.IndexAsync(approved, progress, token);
            }
            catch (Exception error)
            {
                indexingFailure = error;
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
                IndexDirectory, "Native migration preview; legacy indexes are not imported automatically."
                    + (enableBackgroundIndexing ? "" : " Background coordination disabled for this host.")
                    + (backgroundError is null && background?.Failure is null ? ""
                        : " Background indexing failed: " + (backgroundError ?? background!.Failure!.Message))
                    + (indexing.Failure is { } failure ? " " + failure : ""), indexing.State);
        }
        finally { lifecycle.Release(); }
    }

    public async Task<EmbeddingInfo> GetEmbeddingInfoAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeStop.Token);
        cancellationToken = linked.Token;
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await GetEmbeddings().GetInfoAsync(cancellationToken);
        }
        finally { lifecycle.Release(); }
    }

    private string IndexDirectory => settings.IndexDirectory ?? Path.Combine(dataDirectory, "index");
    private IEmbeddingService GetEmbeddings() => embeddings ??= new InferenceHostClient(
        settings.InferenceExecutable ?? Path.Combine(applicationDirectory, "inference", "WinSight.InferenceHost.exe"),
        settings.ModelDirectory ?? Path.Combine(applicationDirectory, "models"),
        settings.ManifestPath ?? Path.Combine(applicationDirectory, "inference", "model-manifest.json"),
        settings.Accelerator,
        settings.InstallExecutionProviders);

    private async Task<IReadOnlyList<IIndexRepository>> GetRepositoriesAsync(CancellationToken cancellationToken)
    {
        if (repositories.Count != 0) return repositories;
        Directory.CreateDirectory(IndexDirectory);
        var created = new List<IIndexRepository>();
        try
        {
            foreach (var file in new[] { "index.sqlite", "code.sqlite" })
            {
                var repository = new SqliteIndexRepository(Path.Combine(IndexDirectory, file));
                created.Add(repository);
                await repository.InitializeAsync(cancellationToken);
            }
            repositories.AddRange(created);
            return repositories;
        }
        catch
        {
            foreach (var repository in created) await repository.DisposeAsync();
            throw;
        }
    }

    private bool IsAuthorizedResult(string path, bool code)
    {
        if (IsUnder(path, dataDirectory) || IsUnder(path, IndexDirectory)) return false;
        var roots = code ? settings.CodeFolders : settings.Folders;
        var skipped = code ? settings.CodeSkipped : settings.Skipped;
        // Skips belong to each chosen root; a separately chosen nested root overrides an ancestor's skip.
        return roots.Any(root => IsUnder(path, root) && !skipped.Any(skip =>
            IsUnder(skip, root) && !PathsEqual(skip, root) && IsUnder(path, skip)));
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnder(string path, string root)
    {
        var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return target.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
            isOnAcPower: isOnAcPower);
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
