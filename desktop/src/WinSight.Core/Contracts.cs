namespace WinSight.Core;

public enum SearchMode { Hybrid, Keyword, Semantic }
public enum IndexingControlState { Running, Pausing, Paused }
public sealed class IndexingPausedException() : InvalidOperationException("Indexing is paused or pausing.");
public sealed record SearchRequest(string Query, int Limit = 50, SearchMode Mode = SearchMode.Hybrid,
    string? Kind = null, string? Root = null, IReadOnlyList<SearchField>? Filters = null);
public sealed record SearchHit(long FileId, string Path, string Kind, double Score, string MatchType,
    string? Excerpt = null, double? Location = null, double? LocationEnd = null, string? SegmentKind = null,
    SourceAnchor? Source = null, IReadOnlyList<string>? Channels = null)
{
    // Two searches over unchanged data must give equal hits; the default record equality would compare the channel list by reference.
    public bool Equals(SearchHit? other) => other is not null && FileId == other.FileId && Path == other.Path && Kind == other.Kind
        && Score.Equals(other.Score) && MatchType == other.MatchType && Excerpt == other.Excerpt && Location == other.Location
        && LocationEnd == other.LocationEnd && SegmentKind == other.SegmentKind && Equals(Source, other.Source)
        && (Channels is null ? other.Channels is null : other.Channels is not null && Channels.SequenceEqual(other.Channels));

    public override int GetHashCode() => HashCode.Combine(FileId, Path, Kind, Score, MatchType, Location, SegmentKind, Channels?.Count ?? -1);
}
public sealed record SearchResponse(IReadOnlyList<SearchHit> Hits, TimeSpan Elapsed,
    bool SemanticAvailable, string? Notice = null, bool LexicalDegraded = false);
public sealed record IndexSegment(string Kind, string Modality, double? Location, double? LocationEnd,
    string? Excerpt, float[]? Vector = null, SourceAnchor? Source = null);
public sealed record SearchField(string Name, string Value);
public sealed record SourceLocation(string Type, int? Page = null, int? Paragraph = null,
    string? Sheet = null, string? Range = null, string? Part = null);
public sealed record OcrTextRegion(int Line, double Left, double Top, double Width, double Height, string? Text = null);
public sealed record SourceAnchor(SourceLocation Location, string Channel = "body",
    IReadOnlyList<SearchField>? Fields = null, IReadOnlyList<OcrTextRegion>? Regions = null);
public sealed record DocumentExtractionOptions(bool IncludeWordComments = false, bool IncludeWordHeadersFooters = false);
public sealed record IndexedFile(long Id, string Path, string Kind, long Size, double ModifiedTime,
    string State, string? Detail, string? Info, IReadOnlyList<IndexSegment> Segments);
public sealed record KeywordMatch(long FileId, double Rank);
public sealed record IndexProgress(string Phase, string? Path, int Completed, int Failed, int Partial,
    string? Detail = null);
public sealed record IndexTask(string Path, string Root, bool Code, string Kind, string? Tombstone,
    string State, int Attempts, DateTimeOffset DueUtc, DateTimeOffset SeenUtc, string? Reason = null);
public sealed record IndexRequest(IReadOnlyList<string> Roots, IReadOnlyList<string>? Excluded = null,
    IReadOnlyList<string>? SkipTypes = null, bool Code = false, bool Force = false,
    long MaxFileBytes = 32 * 1024 * 1024, DocumentExtractionOptions? Documents = null, bool AllowPowerPointAutomation = false,
    bool DeferComplexParse = true, int MaxFileSeconds = 25, bool GeneratePageImages = false);
public sealed record EngineStatus(int Files, int Done, int Pending, int Partial, int Failed,
    string IndexPath, string? Detail = null, IndexingControlState IndexingState = IndexingControlState.Running);
public sealed record FoundryProbe(string Sdk, IReadOnlyList<string> DeviceTypes, IReadOnlyList<FoundryProvider> Providers, string? Error = null);
public sealed record FoundryProvider(string Name, bool Registered);
public sealed record McpConsoleStatus(int Pending, int Claimed, int Completed, IReadOnlyList<string> Recent,
    string HttpState, string BridgePath, IReadOnlyList<string> Notes);

public sealed record IndexMigrationResult(string Phase, string Source, string Target, string Detail);

public sealed record ModelInventory(string EmbeddingModel, string EmbeddingRevision, string EmbeddingProfile, string EmbeddingDevice,
    string OcrEngine, string OcrVersion, string OcrProfile, string OcrDevice, string Notes);

public sealed record ClientConfigPreview(bool FileExists, bool AlreadyConnected, bool Blocked, string Summary, string? BlockReason = null, string? BackupPath = null);

public sealed record WinMlProbe(string OrtVersion, IReadOnlyList<string> Libraries, IReadOnlyList<string> Available, IReadOnlyList<WinMlDevice> Devices, string? Error = null);
public sealed record WinMlDevice(string Ep, string Type, int DeviceId, int VendorId);

public sealed record FoundryInstallResult(bool Success, string Status, IReadOnlyList<string> Registered, IReadOnlyList<string> Failed, string? Error = null);

public sealed record IndexBudgetState(string Class, int ExtractConcurrency, int InferenceConcurrency, string State);
public sealed record IndexingQueueStatus(int Pending, int Stabilizing, int Ready, int Running, int Failed, int Deferred, long Dropped,
    long WatcherOverflows, DateTimeOffset? LastReconcileUtc, string UsnState, IReadOnlyList<IndexBudgetState> Budgets);
public sealed record EmbeddingInput(string Operation, string Value, int Budget = 280);
public sealed record EmbeddingResult(float[]? Vector, string? Error = null);
public sealed record EmbeddingInfo(string ProfileId, int Dimension, string Runtime, string Model,
    string Revision, string RequestedProvider, string Validation,
    IReadOnlyList<string> Capabilities, string BackendStatus = "");

public interface ISearchService
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
}
public interface IIndexService
{
    Task IndexAsync(IndexRequest request, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<IndexingQueueStatus> GetIndexingQueueStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new IndexingQueueStatus(0, 0, 0, 0, 0, 0, 0, 0, null, "not-implemented", Array.Empty<IndexBudgetState>()));
    Task<IReadOnlyList<string>> GetFailureSummaryAsync(int limit = 5, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
public interface IIndexRepository : IAsyncDisposable
{
    string DatabasePath { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IndexedFile>> ReadFilesAsync(CancellationToken cancellationToken = default);
    Task<IndexedFile?> GetFileAsync(string path, CancellationToken cancellationToken = default);
    Task UpsertFileAsync(IndexedFile file, CancellationToken cancellationToken = default);
    Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KeywordMatch>> KeywordSearchAsync(string query, int limit,
        CancellationToken cancellationToken = default);
    Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken = default);
    Task SetMetaAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task ResetHangingTasksAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task EnqueueIndexTaskAsync(IndexTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task AckTaskAsync(string taskPath, CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task EnqueueIndexTasksAsync(IReadOnlyList<IndexTask> tasks, CancellationToken cancellationToken = default) =>
        tasks.Count == 0 ? Task.CompletedTask
        : tasks.Aggregate(Task.CompletedTask, async (previous, task) =>
        {
            await previous.ConfigureAwait(false);
            await EnqueueIndexTaskAsync(task, cancellationToken).ConfigureAwait(false);
        });
    Task<IReadOnlyList<IndexTask>> ReadDueTasksAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IndexTask>>([]);
    Task<IndexTask?> GetTaskAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<IndexTask?>(null);
    Task UpdateTaskStateAsync(string path, string state, int attempts, DateTimeOffset dueUtc, string? reason = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task AckTaskWithUpsertAsync(IndexedFile file, string taskPath, string? tombstone = null, CancellationToken cancellationToken = default) => UpsertFileAsync(file, cancellationToken);
    Task AckTaskWithDeleteAsync(string filePath, string taskPath, CancellationToken cancellationToken = default) => DeleteFileAsync(filePath, cancellationToken);
    Task<int> TrimTasksAsync(int maxTasks = 50000, CancellationToken cancellationToken = default) => Task.FromResult(0);
    Task<IReadOnlyDictionary<string, int>> GetTaskCountsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
}
public interface IEmbeddingService : IAsyncDisposable
{
    Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs,
        CancellationToken cancellationToken = default);
}
public sealed record ExtractionResult(IReadOnlyList<IndexSegment> Segments, string State,
    string? Detail = null, string? Info = null);
public interface IContentExtractor
{
    bool CanExtract(string path, bool code);
    Task<ExtractionResult> ExtractAsync(string path, bool code, CancellationToken cancellationToken = default);
    Task<ExtractionResult> ExtractAsync(string path, bool code, DocumentExtractionOptions options,
        CancellationToken cancellationToken = default) => ExtractAsync(path, code, cancellationToken);
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public List<string> Folders { get; set; } = [];
    public List<string> Skipped { get; set; } = [];
    public List<string> SkipTypes { get; set; } = [];
    public List<string> CodeFolders { get; set; } = [];
    public List<string> CodeSkipped { get; set; } = [];
    public string CodeEditor { get; set; } = "default";
    public bool IncludeWordComments { get; set; }
    public bool IncludeWordHeadersFooters { get; set; }
    public string Hotkey { get; set; } = "Control+Shift+Space";
    public bool SetUp { get; set; }
    public bool BackgroundIndexingEnabled { get; set; }
    public bool CenterPanel { get; set; } = true;
    public bool IndexHoursOn { get; set; }
    public IndexHours IndexHours { get; set; } = new();
    public bool ProcessorOnly { get; set; } = true;
    /// <summary>Page rasterisation during indexing. Off by default: it dominates a full pass.</summary>
    public bool GeneratePageImages { get; set; }
    // Deferred parsing indexes text and structure first and leaves page vision, OCR and media parsing
    // to a later pass; turning this off makes every document pay the full parse cost immediately.
    public bool DeferComplexParse { get; set; } = true;
    public bool UpdateChecksOff { get; set; } = true;
    public string TrayClick { get; set; } = "panel";
    public bool OpenAtLogin { get; set; }
    // Optional HTTPS address of a small JSON feed for the manual "Check for updates" button; empty means the feature is off.
    public string? UpdateFeedUrl { get; set; }
    public string Theme { get; set; } = "Default";
    public string? IndexDirectory { get; set; }
    public string? ModelDirectory { get; set; }
    public string? InferenceExecutable { get; set; }
    public string? ManifestPath { get; set; }
    public WindowPlacement? Window { get; set; }
    public string Accelerator { get; set; } = "auto";
    // Downloads components over the network, so it is opt-in. Settings files written before this field existed stay off.
    public bool InstallExecutionProviders { get; set; } = false;
}
public sealed class IndexHours
{
    public int Start { get; set; } = 60;
    public int End { get; set; } = 420;
}
public sealed record WindowPlacement(double Width, double Height, int? X = null, int? Y = null,
    bool Maximized = false);
public interface ISettingsStore
{
    string SettingsPath { get; }
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ISourcePreview : IAsyncDisposable
{
    string Excerpt { get; }
    SourceAnchor? Source { get; }
    string? RasterPath { get; }
    int PixelWidth { get; }
    int PixelHeight { get; }
    string Notice { get; }
    bool SourceCurrent { get; }
    bool CanNavigate { get; }
}
public sealed record SourceNavigationResult(bool Exact, string Message, int? ProcessId = null);

/// <summary>Reads and writes persisted settings.</summary>
public interface ISettingsBackend
{
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Queries, source preview and the model identity queries depend on.</summary>
public interface ISearchBackend
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
    Task<EmbeddingInfo> GetEmbeddingInfoAsync(CancellationToken cancellationToken = default);
    Task<ISourcePreview> PreviewSourceAsync(SearchHit hit, bool code = false, CancellationToken cancellationToken = default) =>
        Task.FromException<ISourcePreview>(new NotSupportedException("Source preview is unavailable in this backend."));
    Task<SourceNavigationResult> NavigateSourceAsync(SearchHit hit, bool code = false, CancellationToken cancellationToken = default) =>
        Task.FromException<SourceNavigationResult>(new NotSupportedException("Precise source navigation is unavailable in this backend."));
}

/// <summary>Indexing runs, their status and pause control.</summary>
public interface IIndexingBackend
{
    Task IndexAsync(IndexRequest request, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IndexingQueueStatus> GetIndexingQueueStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new IndexingQueueStatus(0, 0, 0, 0, 0, 0, 0, 0, null, "not-implemented", Array.Empty<IndexBudgetState>()));
    Task<IReadOnlyList<string>> GetFailureSummaryAsync(int limit = 5, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
    Task SetIndexingPausedAsync(bool paused, CancellationToken cancellationToken = default);
    Task<string> DescribeVectorScaleAsync(CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
}

/// <summary>Inference providers, probes and the model inventory.</summary>
public interface IModelBackend
{
    Task<FoundryProbe?> ProbeFoundryAsync(CancellationToken cancellationToken = default) => Task.FromResult<FoundryProbe?>(null);
    Task<FoundryInstallResult?> InstallFoundryProvidersAsync(CancellationToken cancellationToken = default) => Task.FromResult<FoundryInstallResult?>(null);
    Task<WinMlProbe?> ProbeWinMlAsync(CancellationToken cancellationToken = default) => Task.FromResult<WinMlProbe?>(null);
    Task<ModelInventory?> GetModelInventoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<ModelInventory?>(null);
}

/// <summary>The local MCP bridge: client configuration, the maintenance grant and queued requests.</summary>
public interface IMcpBackend
{
    Task<ClientConfigPreview?> PreviewClientConfigAsync(string configPath, string transport = "stdio", int httpPort = 0, CancellationToken cancellationToken = default) => Task.FromResult<ClientConfigPreview?>(null);
    Task<ClientConfigPreview?> ApplyClientConfigAsync(string configPath, string transport = "stdio", int httpPort = 0, CancellationToken cancellationToken = default) => Task.FromResult<ClientConfigPreview?>(null);
    Task<McpConsoleStatus?> GetMcpConsoleStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult<McpConsoleStatus?>(null);
    Task<string?> GetMaintenanceGrantAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    Task<string?> GrantMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    Task RevokeMaintenanceAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<IReadOnlyList<string>> DrainMaintenanceJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Index location, generation rebuilds and their blockers.</summary>
public interface IMaintenanceBackend
{
    Task<IReadOnlyList<string>> DescribeMigrationBlockersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    Task<IndexMigrationResult?> InspectMigrationAsync(string targetDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IndexMigrationResult?>(null);
    Task<IndexMigrationResult?> MigrateIndexAsync(string targetDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IndexMigrationResult?>(null);
    Task<bool> RebuildNeededAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    Task<string> StartRebuildAsync(CancellationToken cancellationToken = default) => Task.FromResult("unsupported");
    string RebuildStatus => "idle";
}

/// <summary>
/// Everything the desktop shell can ask of the backend. Consumers should depend on the narrowest role interface they need;
/// this composite exists for the composition root and for fakes.
/// </summary>
public interface IDesktopBackend : ISettingsBackend, ISearchBackend, IIndexingBackend, IModelBackend, IMcpBackend, IMaintenanceBackend, IAsyncDisposable
{
}

public static class VectorContract
{
    public const int Dimension = 768;
    public static void Validate(ReadOnlySpan<float> vector)
    {
        if (vector.Length != Dimension) throw new InvalidDataException("Embedding dimension must be 768.");
        double norm = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value)) throw new InvalidDataException("Embedding contains a non-finite value.");
            norm += (double)value * value;
        }
        if (norm < 1e-12 || Math.Abs(Math.Sqrt(norm) - 1) > 0.025)
            throw new InvalidDataException("Embedding must be nonzero and L2-normalized.");
    }
}
