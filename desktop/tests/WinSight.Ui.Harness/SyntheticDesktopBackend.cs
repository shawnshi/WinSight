using WinSight.Core;

namespace WinSight.Ui.Harness;

public sealed class SyntheticDesktopBackend : IDesktopBackend
{
    private readonly string testDataDir;
    private AppSettings settings;
    private EngineStatus status;
    private CancellationTokenSource indexCancellation = new();
    private Task? activeIndex;

    public List<bool> RecordedPauseRequests { get; } = [];
    public int StatusCallCount { get; private set; }
    public IndexingControlState IndexingState => status.IndexingState;
    public Func<bool, CancellationToken, Task>? PauseControlHandler { get; set; }
    public Func<CancellationToken, Task<EngineStatus>>? StatusHandler { get; set; }

    public async Task SetIndexingPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordedPauseRequests.Add(paused);
        if (paused)
        {
            status = status with { IndexingState = IndexingControlState.Pausing };
            indexCancellation.Cancel();
            if (activeIndex is not null)
            {
                try { await activeIndex.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
        }
        if (PauseControlHandler is not null) await PauseControlHandler(paused, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!paused)
        {
            indexCancellation.Dispose();
            indexCancellation = new CancellationTokenSource();
        }
        status = status with { IndexingState = paused ? IndexingControlState.Paused : IndexingControlState.Running };
    }

    public bool WasEmbeddingInfoCalled { get; private set; }
    public int SearchCallCount { get; private set; }
    public int IndexCallCount { get; private set; }
    public int SaveSettingsCount { get; private set; }
    public List<IndexRequest> RecordedIndexRequests { get; } = [];

    public SyntheticDesktopBackend(string dataDir)
    {
        testDataDir = Path.GetFullPath(dataDir);
        settings = new AppSettings
        {
            Version = 1,
            Revision = 0,
            Folders = [Path.Combine(testDataDir, "Indexed")],
            Skipped = [Path.Combine(testDataDir, "Indexed", "node_modules")],
            SkipTypes = [".tmp", ".log"],
            Hotkey = "Control+Shift+Space",
            BackgroundIndexingEnabled = false,
            IndexHours = new IndexHours { Start = 60, End = 420 },
            Theme = "Default"
        };

        status = new EngineStatus(
            Files: 42,
            Done: 40,
            Pending: 2,
            Partial: 0,
            Failed: 0,
            IndexPath: Path.Combine(testDataDir, "index.sqlite"),
            Detail: "Synthetic preview engine online");
    }

    private static AppSettings DeepClone(AppSettings source)
    {
        return new AppSettings
        {
            Version = source.Version,
            Revision = source.Revision,
            Folders = [.. source.Folders],
            Skipped = [.. source.Skipped],
            SkipTypes = [.. source.SkipTypes],
            CodeFolders = [.. source.CodeFolders],
            CodeSkipped = [.. source.CodeSkipped],
            CodeEditor = source.CodeEditor,
            Hotkey = source.Hotkey,
            SetUp = source.SetUp,
            BackgroundIndexingEnabled = source.BackgroundIndexingEnabled,
            CenterPanel = source.CenterPanel,
            IndexHoursOn = source.IndexHoursOn,
            IndexHours = new IndexHours { Start = source.IndexHours.Start, End = source.IndexHours.End },
            ProcessorOnly = source.ProcessorOnly,
            UpdateChecksOff = source.UpdateChecksOff,
            TrayClick = source.TrayClick,
            OpenAtLogin = source.OpenAtLogin,
            Theme = source.Theme,
            IndexDirectory = source.IndexDirectory,
            ModelDirectory = source.ModelDirectory,
            InferenceExecutable = source.InferenceExecutable,
            ManifestPath = source.ManifestPath,
            Window = source.Window is not null
                ? new WindowPlacement(source.Window.Width, source.Window.Height, source.Window.X, source.Window.Y, source.Window.Maximized)
                : null
        };
    }

    public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(DeepClone(settings));
    }

    public Task SaveSettingsAsync(AppSettings updated, CancellationToken cancellationToken = default)
    {
        SaveSettingsCount++;

        // NM-UI-06: Real CAS (Compare-And-Swap) revision validation
        if (updated.Revision != settings.Revision)
        {
            throw new InvalidOperationException(
                $"Settings revision conflict: expected revision {settings.Revision}, but got {updated.Revision}. Settings were modified by another operation.");
        }

        updated.Revision = settings.Revision + 1;
        settings = DeepClone(updated);
        return Task.CompletedTask;
    }

    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        SearchCallCount++;

        var hits = new List<SearchHit>();
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var q = request.Query.ToLowerInvariant();
            if (request.Kind is null or "doc" or "pdf")
            {
                hits.Add(new SearchHit(
                    FileId: 1,
                    Path: Path.Combine(testDataDir, "Indexed", "Clinical_Notes.pdf"),
                    Kind: "pdf",
                    Score: 0.94,
                    MatchType: "hybrid",
                    Excerpt: $"Matched query '{request.Query}' in clinical findings summary section.",
                    Location: 3,
                    LocationEnd: 3,
                    SegmentKind: "page"));
            }

            if (request.Kind is null or "video" or "audio")
            {
                hits.Add(new SearchHit(
                    FileId: 2,
                    Path: Path.Combine(testDataDir, "Indexed", "Hospital_Walkthrough.mp4"),
                    Kind: "video",
                    Score: 0.88,
                    MatchType: "semantic",
                    Excerpt: $"Transcript snippet discussing {request.Query} at facility entrance.",
                    Location: 75.5,
                    LocationEnd: 82.0,
                    SegmentKind: "transcription"));
            }

            if (request.Kind is null or "code")
            {
                hits.Add(new SearchHit(
                    FileId: 3,
                    Path: Path.Combine(testDataDir, "Indexed", "PatientRegistry.cs"),
                    Kind: "code",
                    Score: 0.82,
                    MatchType: "keyword",
                    Excerpt: $"public sealed class PatientRecord // contains reference to {request.Query}",
                    Location: 42,
                    LocationEnd: 45,
                    SegmentKind: "symbol"));
            }
        }

        var response = new SearchResponse(
            Hits: hits,
            Elapsed: TimeSpan.FromMilliseconds(18.5),
            SemanticAvailable: true,
            Notice: null);

        return Task.FromResult(response);
    }

    public Task IndexAsync(IndexRequest request, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IndexingState != IndexingControlState.Running) throw new IndexingPausedException();
        return activeIndex = IndexCoreAsync(request, progress, cancellationToken);
    }

    private async Task IndexCoreAsync(IndexRequest request, IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, indexCancellation.Token);
        cancellationToken = linked.Token;
        IndexCallCount++;
        RecordedIndexRequests.Add(request);
        progress?.Report(new IndexProgress("Discovering", null, 0, 0, 0, "Scanning configured roots"));
        await Task.Delay(20, cancellationToken);
        progress?.Report(new IndexProgress("Extracting", Path.Combine(testDataDir, "Indexed", "Clinical_Notes.pdf"), 20, 0, 0, "Processing documents"));
        await Task.Delay(20, cancellationToken);
        progress?.Report(new IndexProgress("Embedding", null, 42, 0, 0, "Generating 768d vectors"));

        status = status with { Done = 42, Pending = 0, Detail = "Synthetic indexing completed" };
    }

    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatusCallCount++;
        return StatusHandler?.Invoke(cancellationToken) ?? Task.FromResult(status);
    }

    public Task<EmbeddingInfo> GetEmbeddingInfoAsync(CancellationToken cancellationToken = default)
    {
        WasEmbeddingInfoCalled = true;
        return Task.FromResult(new EmbeddingInfo(
            ProfileId: "synthetic-nomic-v1",
            Dimension: 768,
            Runtime: "WinSight.InferenceHost.Synthetic",
            Model: "nomic-embed-text-v1.5",
            Revision: "preview-synthetic-rev",
            RequestedProvider: "CPU (Synthetic)",
            Validation: "Validated",
            Capabilities: ["text", "document", "code"]));
    }

    public ValueTask DisposeAsync()
    {
        indexCancellation.Cancel();
        indexCancellation.Dispose();
        return ValueTask.CompletedTask;
    }
}
