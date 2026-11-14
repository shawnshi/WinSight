namespace WinSight.Core;

public enum SearchMode { Hybrid, Keyword, Semantic }
public enum IndexingControlState { Running, Pausing, Paused }
public sealed class IndexingPausedException() : InvalidOperationException("Indexing is paused or pausing.");
public sealed record SearchRequest(string Query, int Limit = 200, SearchMode Mode = SearchMode.Hybrid,
    string? Kind = null, string? Root = null, IReadOnlyList<SearchField>? Filters = null,
    double MinScore = 0.0);
public sealed record SearchHit(long FileId, string Path, string Kind, double Score, string MatchType,
    string? Excerpt = null, double? Location = null, double? LocationEnd = null, string? SegmentKind = null,
    SourceAnchor? Source = null, IReadOnlyList<string>? Channels = null, long? FileSize = null, double? ModifiedTime = null)
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
    /// <summary>Reads only the file metadata (path, kind, size, mtime, state, detail, info) without materialising segments or vectors.</summary>
    Task<IReadOnlyList<IndexedFile>> ReadFileHeadersAsync(CancellationToken cancellationToken = default) => ReadFilesAsync(cancellationToken);
    Task<IReadOnlyList<IndexedFile>> ReadFileHeadersByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => ReadFileHeadersAsync(cancellationToken);
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
    Task<int> ResetFailedFilesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    Task<IReadOnlyDictionary<string, int>> GetTaskCountsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
    /// <summary>Counts indexed files at or below <paramref name="root"/> without materialising segments or vectors.</summary>
    async Task<int> CountFilesUnderAsync(string root, CancellationToken cancellationToken = default)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = normalized + Path.DirectorySeparatorChar;
        var files = await ReadFilesAsync(cancellationToken).ConfigureAwait(false);
        return files.Count(file => string.Equals(file.Path, normalized, StringComparison.OrdinalIgnoreCase)
            || file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>Counts stored segment vectors without loading them.</summary>
    async Task<long> CountVectorsAsync(CancellationToken cancellationToken = default)
    {
        var files = await ReadFilesAsync(cancellationToken).ConfigureAwait(false);
        return files.Sum(file => (long)file.Segments.Count(segment => segment.Vector is not null));
    }
    Task<IReadOnlyList<PendingEmbeddingSegment>> ReadPendingEmbeddingSegmentsAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PendingEmbeddingSegment>>([]);
    Task<int> SaveBackfilledVectorsAsync(IReadOnlyList<SegmentVectorUpdate> updates, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
public sealed record PendingEmbeddingSegment(long Id, long FileId, string Path, string Title, string Excerpt, string Kind);
public sealed record SegmentVectorUpdate(long Id, long FileId, float[] Vector);
public interface IEmbeddingService : IAsyncDisposable
{
    Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Latency-sensitive embedding for a user query. Implementations may let it overtake queued bulk work and must not
    /// tear down shared state when the caller stops waiting. The default simply delegates to <see cref="EmbedAsync"/>.
    /// </summary>
    Task<IReadOnlyList<EmbeddingResult>> EmbedInteractiveAsync(IReadOnlyList<EmbeddingInput> inputs,
        CancellationToken cancellationToken = default) => EmbedAsync(inputs, cancellationToken);
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
    public bool EnableMcpServer { get; set; } = false;
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
    int PageNumber => 1;
    int? TotalPages => null;
}
public sealed record SourceNavigationResult(bool Exact, string Message, int? ProcessId = null);
public sealed record StorageMetrics(long DatabaseBytes, long TotalSegments, long EmbeddedSegments, long PreviewCacheBytes, int PreviewCacheFiles);

/// <summary>Reads and writes persisted settings.</summary>
public interface ISettingsBackend
{
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    event Action<AppSettings>? SettingsSaved { add { } remove { } }
}

/// <summary>Queries, source preview and the model identity queries depend on.</summary>
public interface ISearchBackend
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
    Task<EmbeddingInfo> GetEmbeddingInfoAsync(CancellationToken cancellationToken = default);
    Task<ISourcePreview> PreviewSourceAsync(SearchHit hit, bool code = false, CancellationToken cancellationToken = default) =>
        Task.FromException<ISourcePreview>(new NotSupportedException("Source preview is unavailable in this backend."));
    Task<ISourcePreview> PreviewSourcePageAsync(SearchHit hit, int pageNumber, bool code = false, CancellationToken cancellationToken = default) =>
        PreviewSourceAsync(hit, code, cancellationToken);
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
    Task<int> ResetFailedFilesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    Task SetIndexingPausedAsync(bool paused, CancellationToken cancellationToken = default);
    Task<string> DescribeVectorScaleAsync(CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    Task<StorageMetrics?> GetStorageMetricsAsync(CancellationToken cancellationToken = default) => Task.FromResult<StorageMetrics?>(null);
    Task<int> RunVectorBackfillAsync(int maxSegments = 0, IProgress<(int Processed, int Remaining)>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(0);
    Task<int> ClearPreviewCacheAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    Task<int> CompactDatabaseAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    Task<int> CountFolderFilesAsync(string root, CancellationToken cancellationToken = default) => Task.FromResult(-1);
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
    Task<string> GetMcpClientConfigSnippetAsync(string transport = "stdio", int httpPort = 0, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
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
    public const int Float16ByteCount = Dimension * sizeof(short); // 1536
    public const int Int8ByteCount = Dimension + sizeof(float);    // 772 (4-byte float scale + 768 sbytes)

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

    /// <summary>Symmetric scalar quantization from L2-normalized float32 into int8 (4-byte scale + 768 sbytes, 772 bytes total).</summary>
    public static byte[] QuantizeInt8(ReadOnlySpan<float> vector)
    {
        Validate(vector);
        var bytes = new byte[Int8ByteCount];
        float maxVal = 0;
        for (int i = 0; i < Dimension; i++)
        {
            float abs = Math.Abs(vector[i]);
            if (abs > maxVal) maxVal = abs;
        }
        float scale = maxVal <= 1e-12f ? 1.0f / 127.0f : maxVal / 127.0f;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), scale);
        float invScale = 1.0f / scale;
        for (int i = 0; i < Dimension; i++)
        {
            bytes[4 + i] = (byte)(sbyte)Math.Clamp((int)Math.Round(vector[i] * invScale), -127, 127);
        }
        return bytes;
    }

    /// <summary>Dequantizes a 772-byte int8 vector blob back to normalized float32 with guaranteed L2 contract compliance.</summary>
    public static float[] DequantizeInt8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Int8ByteCount)
            throw new InvalidDataException($"Int8 vector blob must be {Int8ByteCount} bytes (4-byte scale + 768 bytes).");
        float scale = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[..4]);
        if (!float.IsFinite(scale) || scale <= 0)
            throw new InvalidDataException("Invalid vector quantization scale.");
        var vector = new float[Dimension];
        double normSq = 0;
        for (int i = 0; i < Dimension; i++)
        {
            sbyte q = (sbyte)bytes[4 + i];
            float val = q * scale;
            vector[i] = val;
            normSq += (double)val * val;
        }
        if (normSq > 1e-12)
        {
            float invNorm = (float)(1.0 / Math.Sqrt(normSq));
            for (int i = 0; i < Dimension; i++) vector[i] *= invNorm;
        }
        Validate(vector);
        return vector;
    }
}
