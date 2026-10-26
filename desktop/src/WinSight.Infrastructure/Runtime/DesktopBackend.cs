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
                // Deferred (default) keeps the first pass to text and structure: page rasterisation, OCR and
                // media sampling are the expensive stages and are left to a later pass.
                var defer = approved.DeferComplexParse;
                // The embedding profile includes the ORT version, so an ORT change makes every stored vector
                // belong to a different profile. Keep the old index as a backup and start a fresh one instead
                // of failing every file with a profile mismatch.
                var info = await model.GetInfoAsync(token).ConfigureAwait(false);
                repository = await EnsureGenerationAsync(repository, approved, info, mediaClient, defer, token).ConfigureAwait(false);
                var service = new IndexingService(repository, new ContentExtractor(mediaClient), model,
                    defer || mediaClient is null ? null : new NativeMediaSamplingService(mediaClient),
                    defer ? null : new NativeDocumentPages(mediaClient),
                    defer ? null : new NativePresentationPages(mediaClient),
                    allowDeferredStages: defer);
                if (!defer)
                {
                    await service.IndexAsync(approved, progress, token);
                }
                else
                {
                    // Manual and watcher passes share one durable queue so progress survives a restart.
                    var queue = new IncrementalIndexQueue(repository,
                        indexPaths: (roots, paths, code, ct) => service.IndexPathsAsync(roots, paths, code, progress, ct),
                        onError: error => backgroundError = "queue: " + error.Message);
                    await queue.InitializeAsync(token);
                    foreach (var root in approved.Roots)
                    {
                        await queue.ReconcileRootAsync(root, approved.Code, force: true, token, requireStabilization: false);
                    }
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var processed = await queue.ProcessReadyTasksAsync(400, token, batchSize: 16);
                        if (processed == 0) break;
                    }
                }
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

    /// <summary>
    /// Asks the isolated Windows ML host (ORT 1.27) which devices it can see. Read-only: the host registers
    /// local provider libraries only and never downloads.
    /// </summary>
    public async Task<WinMlProbe?> ProbeWinMlAsync(CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(applicationDirectory, "inference", "winml", "WinSight.WinMlHost.exe");
        if (!File.Exists(executable)) return new WinMlProbe("unavailable", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<WinMlDevice>(), "The Windows ML host is not part of this payload.");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("environment");
            using var process = System.Diagnostics.Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new WinMlProbe("unavailable", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<WinMlDevice>(),
                    "environment exited " + process.ExitCode + ": " + (await stderr.ConfigureAwait(false)).Trim());
            using var document = System.Text.Json.JsonDocument.Parse(text);
            var root = document.RootElement;
            string[] ReadStrings(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Array
                ? value.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length != 0).ToArray()
                : Array.Empty<string>();
            var devices = root.TryGetProperty("devices", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array
                ? list.EnumerateArray().Select(d => new WinMlDevice(
                    d.TryGetProperty("ep", out var ep) ? ep.GetString() ?? "" : "",
                    d.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
                    d.TryGetProperty("device_id", out var id) && id.TryGetInt32(out var parsedId) ? parsedId : 0,
                    d.TryGetProperty("vendor_id", out var vendor) && vendor.TryGetInt32(out var parsedVendor) ? parsedVendor : 0))
                  .Where(d => d.Ep.Length != 0).ToArray()
                : Array.Empty<WinMlDevice>();
            var ort = root.TryGetProperty("ort_version", out var version) ? version.GetString() ?? "unknown" : "unknown";
            return new WinMlProbe(ort, ReadStrings("libraries"), ReadStrings("available"), devices);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return new WinMlProbe("unavailable", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<WinMlDevice>(), error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>
    /// Runs the isolated Foundry Local host in probe mode: it only discovers providers and never downloads.
    /// A missing or failing host degrades to an explanatory error instead of failing the page.
    /// </summary>
    public async Task<FoundryProbe?> ProbeFoundryAsync(CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(applicationDirectory, "foundry", "WinSight.FoundryHost.exe");
        if (!File.Exists(executable)) return new FoundryProbe("unavailable", Array.Empty<string>(), Array.Empty<FoundryProvider>(), "Foundry host is not part of this payload.");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("probe");
            using var process = System.Diagnostics.Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0) return new FoundryProbe("unavailable", Array.Empty<string>(), Array.Empty<FoundryProvider>(), "probe exited " + process.ExitCode + ": " + (await stderr.ConfigureAwait(false)).Trim());
            using var document = System.Text.Json.JsonDocument.Parse(text);
            var root = document.RootElement;
            var sdk = root.TryGetProperty("sdk", out var sdkElement) ? sdkElement.GetString() ?? "unknown" : "unknown";
            var types = root.TryGetProperty("deviceTypes", out var typeElement) && typeElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? typeElement.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length != 0).ToArray()
                : Array.Empty<string>();
            var providers = root.TryGetProperty("providers", out var providerElement) && providerElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? providerElement.EnumerateArray()
                    .Select(p => new FoundryProvider(p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        p.TryGetProperty("registered", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True))
                    .Where(p => p.Name.Length != 0).ToArray()
                : Array.Empty<FoundryProvider>();
            return new FoundryProbe(sdk, types, providers);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return new FoundryProbe("unavailable", Array.Empty<string>(), Array.Empty<FoundryProvider>(), error.GetType().Name + ": " + error.Message);
        }
    }

    public async Task<IReadOnlyList<string>> GetFailureSummaryAsync(int limit = 5, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var repository = (await GetRepositoriesAsync(cancellationToken))[0];
        return repository is SqliteIndexRepository sqlite
            ? await sqlite.ReadFailureSummaryAsync(limit, cancellationToken).ConfigureAwait(false)
            : Array.Empty<string>();
    }

    /// <summary>
    /// Downloads and registers the Foundry Local execution providers that apply to this device. This is the
    /// only path that touches the network, it is bounded, and it reports which providers failed.
    /// </summary>
    public async Task<FoundryInstallResult?> InstallFoundryProvidersAsync(CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(applicationDirectory, "foundry", "WinSight.FoundryHost.exe");
        if (!File.Exists(executable)) return new FoundryInstallResult(false, "host-absent", Array.Empty<string>(), Array.Empty<string>(), "Foundry host is not part of this payload.");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("register");
            start.ArgumentList.Add("--allow-download");
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return new FoundryInstallResult(false, "start-failed", Array.Empty<string>(), Array.Empty<string>(), "Foundry host did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            var tail = (await stderr.ConfigureAwait(false)).Trim();
            var jsonStart = text.IndexOf('{', StringComparison.Ordinal);
            if (jsonStart < 0) return new FoundryInstallResult(false, "no-result", Array.Empty<string>(), Array.Empty<string>(), tail.Length == 0 ? "host produced no result" : tail);
            using var document = System.Text.Json.JsonDocument.Parse(text[jsonStart..]);
            var root = document.RootElement;
            string[] ReadArray(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Array
                ? value.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length != 0).ToArray()
                : Array.Empty<string>();
            var success = root.TryGetProperty("success", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True;
            var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "" : "";
            return new FoundryInstallResult(success, status, ReadArray("registered"), ReadArray("failed"), tail.Length == 0 ? null : tail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FoundryInstallResult(false, "timeout", Array.Empty<string>(), Array.Empty<string>(), "provider installation exceeded 10 minutes");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            return new FoundryInstallResult(false, "failed", Array.Empty<string>(), Array.Empty<string>(), error.GetType().Name + ": " + error.Message);
        }
    }

    public async Task<IndexingQueueStatus> GetIndexingQueueStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var coordinator = background;
        return coordinator is null
            ? new IndexingQueueStatus(0, 0, 0, 0, 0, 0, 0, 0, null, "background-disabled", Array.Empty<IndexBudgetState>())
            : await coordinator.GetIndexingQueueStatusAsync(cancellationToken).ConfigureAwait(false);
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
    /// <summary>
    /// The NPU path runs on the isolated Windows ML host (ORT 1.27), because vendor NPU plugins are built
    /// against that runtime. Every other preference keeps the default host (ORT 1.31). The choice is stable
    /// per accelerator setting: each runtime produces its own embedding profile, and switching retires the
    /// old index instead of mixing vectors.
    /// </summary>
    private IEmbeddingService GetEmbeddings() => embeddings ??= new InferenceHostClient(
        settings.InferenceExecutable ?? ResolveInferenceExecutable(),
        settings.ModelDirectory ?? Path.Combine(applicationDirectory, "models"),
        settings.ManifestPath ?? Path.Combine(applicationDirectory, "inference", "model-manifest.json"),
        settings.Accelerator);

    private string ResolveInferenceExecutable()
    {
        var defaultHost = Path.Combine(applicationDirectory, "inference", "WinSight.InferenceHost.exe");
        if (!string.Equals(settings.Accelerator, "npu", StringComparison.OrdinalIgnoreCase)) return defaultHost;
        var winMlHost = Path.Combine(applicationDirectory, "inference", "winml", "WinSight.WinMlHost.exe");
        return File.Exists(winMlHost) ? winMlHost : defaultHost;
    }

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

    // Memory pressure must free model, vector and preview caches, not merely halve concurrency.
    private async Task ReleaseCachesForMemoryPressureAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopping != 0 || disposed) return;
            await ReleaseResourcesAsync().ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }

    /// <summary>
    /// One rebuild window covers both a model change and a lexicon change: staging is built with the new
    /// profile and the segmented FTS together, validated, then swapped in atomically; the old database is kept.
    /// </summary>
    private async Task<IIndexRepository> EnsureGenerationAsync(IIndexRepository repository, IndexRequest request,
        EmbeddingInfo info, NativeMediaProcessClient? mediaClient, bool defer, CancellationToken cancellationToken)
    {
        var dictionary = NativeSegmenter.Fingerprint(Array.Empty<string>());
        if (!IndexRebuildCoordinator.NeedsRebuild(IndexDirectory, info.ProfileId, dictionary)) return repository;
        var generation = info.ProfileId[..Math.Min(12, info.ProfileId.Length)] + "-" + dictionary[..8] + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        backgroundError = "Rebuilding the index for the current model and lexicon (one window, old database kept).";
        await repository.DisposeAsync().ConfigureAwait(false);
        repositories.Clear();
        var outcome = await IndexRebuildCoordinator.RebuildAsync(IndexDirectory, generation, async stagingRoot =>
        {
            await using var staging = new SqliteIndexRepository(Path.Combine(stagingRoot, "index.sqlite"));
            await staging.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await staging.SetMetaAsync("profile_id", info.ProfileId, cancellationToken).ConfigureAwait(false);
            await staging.SetMetaAsync(IndexRebuildCoordinator.LexiconSchemaKey, "2", cancellationToken).ConfigureAwait(false);
            await staging.SetMetaAsync(IndexRebuildCoordinator.DictionaryKey, dictionary, cancellationToken).ConfigureAwait(false);
            await staging.SetMetaAsync(IndexRebuildCoordinator.GenerationKey, generation, cancellationToken).ConfigureAwait(false);
            var builder = new IndexingService(staging, new ContentExtractor(mediaClient), embeddings!,
                defer || mediaClient is null ? null : new NativeMediaSamplingService(mediaClient),
                defer ? null : new NativeDocumentPages(mediaClient),
                defer ? null : new NativePresentationPages(mediaClient),
                allowDeferredStages: defer);
            await builder.IndexAsync(request, null, cancellationToken).ConfigureAwait(false);
        }, async database =>
        {
            await using var probe = new SqliteIndexRepository(database);
            await probe.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var hits = await probe.KeywordSearchAsync("index", 3, cancellationToken).ConfigureAwait(false);
            return hits.Count == 0 ? Array.Empty<string>() : new[] { "keyword:" + hits.Count };
        }, cancellationToken).ConfigureAwait(false);
        repositories.Clear();
        var reopened = (await GetRepositoriesAsync(cancellationToken).ConfigureAwait(false))[0];
        backgroundError = outcome.Switched
            ? "Index rebuilt for the current model and lexicon; previous database kept as " + Path.GetFileName(outcome.PreviousDirectory ?? "none") + "."
            : "Index rebuild did not switch: " + (outcome.Detail ?? "unknown");
        return reopened;
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
