using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinSight.Core;

namespace WinSight.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IDesktopBackend backend;
    public string PreviewNotice { get; }
    private AppSettings settings;
    private EngineStatus? status;
    private bool isIndexingActive;
    private string statusMessage = "Ready";
    private CancellationTokenSource? activeIndexCts;
    private readonly CancellationTokenSource lifetime = new();
    private bool isPauseControlBusy;
    private bool pauseRequested;
    private bool statusKnown;
    private long statusReadVersion;
    private long settingsReadVersion;

    public IndexingControlState? IndexingState => statusKnown ? status?.IndexingState : null;
    public bool IsPauseControlBusy => isPauseControlBusy;
    public bool CanToggleIndexingPause => !isPauseControlBusy && !lifetime.IsCancellationRequested;
    public bool CanIndexNow => IndexingState == IndexingControlState.Running
        && !IsIndexingActive && CanToggleIndexingPause;
    public string IndexingStateText => IndexingState switch
    {
        IndexingControlState.Running => "Indexing allowed",
        IndexingControlState.Pausing => "Pausing indexing…",
        IndexingControlState.Paused => "Indexing paused",
        _ => "Indexing status unknown"
    };
    public string PauseActionText => isPauseControlBusy
        ? (pauseRequested ? "Pausing…" : "Resuming…")
        : IndexingState switch
        {
            IndexingControlState.Running => "Pause indexing",
            IndexingControlState.Pausing or IndexingControlState.Paused => "Resume indexing",
            _ => "Retry indexing status"
        };

    private void NotifyIndexingControl()
    {
        OnPropertyChanged(nameof(IndexingState));
        OnPropertyChanged(nameof(IndexingStateText));
        OnPropertyChanged(nameof(IsPauseControlBusy));
        OnPropertyChanged(nameof(CanToggleIndexingPause));
        OnPropertyChanged(nameof(CanIndexNow));
        OnPropertyChanged(nameof(PauseActionText));
    }

    public async Task ToggleIndexingPauseAsync(CancellationToken cancellationToken = default)
    {
        if (!CanToggleIndexingPause || cancellationToken.IsCancellationRequested) return;
        if (!statusKnown)
        {
            await RefreshStatusAsync();
            return;
        }

        pauseRequested = IndexingState == IndexingControlState.Running;
        isPauseControlBusy = true;
        // Ignore reads that began before this control operation.
        ++statusReadVersion;
        NotifyIndexingControl();
        StatusMessage = pauseRequested ? "Pause requested; waiting for indexing to stop…" : "Resuming indexing…";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        string? error = null;
        try
        {
            await backend.SetIndexingPausedAsync(pauseRequested, linked.Token);
        }
        catch (OperationCanceledException)
        {
            error = "Indexing control cancelled.";
        }
        catch (Exception ex)
        {
            error = $"Indexing control failed: {ex.Message}";
        }
        finally
        {
            // The caller's cancellation must not hide a pause already accepted by the engine.
            if (!lifetime.IsCancellationRequested)
            {
                var refreshError = await ReadStatusAsync();
                StatusMessage = error is not null
                    ? $"{error}{(refreshError is null ? "" : $" {refreshError}")}"
                    : refreshError ?? IndexingStateText;
            }
            isPauseControlBusy = false;
            NotifyIndexingControl();
        }
    }

    public void StopOperations()
    {
        lifetime.Cancel();
        activeIndexCts?.Cancel();
        ++statusReadVersion;
        NotifyIndexingControl();
    }

    public IDesktopBackend Backend => backend;
    public AppSettings Settings => settings;

    public int FilesCount => status?.Files ?? 0;
    public int DoneCount => status?.Done ?? 0;
    public int PendingCount => status?.Pending ?? 0;
    public int PartialCount => status?.Partial ?? 0;
    public int FailedCount => status?.Failed ?? 0;
    public string IndexPath => status?.IndexPath ?? "Default";
    public string? StatusDetail => status?.Detail;

    public bool IsIndexingActive
    {
        get => isIndexingActive;
        private set
        {
            if (SetProperty(ref isIndexingActive, value)) OnPropertyChanged(nameof(CanIndexNow));
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public MainWindowViewModel(IDesktopBackend backend, AppSettings? initialSettings = null, string? dataDirectory = null)
    {
        PreviewNotice = dataDirectory is null
            ? "Local preview; see Storage & Models for the active index path. Legacy indexes are not migrated."
            : $"Local preview. Data: {dataDirectory}. Legacy indexes are not migrated.";
        this.backend = backend;
        this.settings = initialSettings ?? new AppSettings();
    }

    public async Task InitializeAsync()
    {
        var version = ++settingsReadVersion;
        try
        {
            var latest = await backend.LoadSettingsAsync(lifetime.Token);
            if (version != settingsReadVersion || lifetime.IsCancellationRequested) return;
            settings = latest;
            OnPropertyChanged(nameof(Settings));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (version == settingsReadVersion && !lifetime.IsCancellationRequested)
                StatusMessage = $"Failed to load settings: {ex.Message}";
            return;
        }

        await RefreshStatusAsync();
    }

    public async Task RefreshStatusAsync()
    {
        var error = await ReadStatusAsync();
        if (lifetime.IsCancellationRequested || isPauseControlBusy) return;
        StatusMessage = error ?? (IndexingState != IndexingControlState.Running
            ? IndexingStateText
            : FilesCount == 0
                ? (settings.Folders.Count == 0 && settings.CodeFolders.Count == 0
                    ? "No index folders configured. Add a folder to begin."
                    : "No files indexed yet. Select Index Now to index configured folders.")
                    + (string.IsNullOrWhiteSpace(status?.Detail) ? string.Empty : " " + status.Detail)
                : status?.Detail ?? (PendingCount > 0
                ? $"Indexing in progress: {DoneCount} / {FilesCount} files completed..."
                : $"Index up to date: {DoneCount} files indexed."));
    }

    private async Task<string?> ReadStatusAsync()
    {
        var version = ++statusReadVersion;
        try
        {
            var current = await backend.GetStatusAsync(lifetime.Token);
            if (version != statusReadVersion || lifetime.IsCancellationRequested) return null;
            status = current;
            statusKnown = true;
            OnPropertyChanged(nameof(FilesCount));
            OnPropertyChanged(nameof(DoneCount));
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(PartialCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(IndexPath));
            OnPropertyChanged(nameof(StatusDetail));

            NotifyIndexingControl();
            return null;
        }
        catch (Exception ex)
        {
            if (version != statusReadVersion || lifetime.IsCancellationRequested) return null;
            statusKnown = false;
            NotifyIndexingControl();
            return $"Status error: {ex.Message}";
        }
    }

    public void CancelIndexing()
    {
        activeIndexCts?.Cancel();
    }

    public async Task TriggerIndexNowAsync(CancellationToken cancellationToken = default)
    {
        if (!CanIndexNow || cancellationToken.IsCancellationRequested) return;

        IsIndexingActive = true;
        StatusMessage = "Reading latest settings for indexing...";

        activeIndexCts?.Dispose();
        activeIndexCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var ct = activeIndexCts.Token;

        try
        {
            // NM-UI-04: Reload latest committed settings directly from backend
            settings = await backend.LoadSettingsAsync(ct);
            OnPropertyChanged(nameof(Settings));
            if (settings.Folders.Count == 0 && settings.CodeFolders.Count == 0)
            {
                StatusMessage = "No index folders configured. Add a folder to begin.";
                return;
            }

            var progress = new Progress<IndexProgress>(p =>
            {
                if (!lifetime.IsCancellationRequested && !isPauseControlBusy && IndexingState == IndexingControlState.Running)
                    StatusMessage = $"{p.Phase}: {p.Completed} completed, {p.Failed} failed{(p.Detail is not null ? $" ({p.Detail})" : "")}";
            });

            // NM-UI-05: Handle document folders and code folders separately.
            // Never pass Code=true on doc roots!
            if (settings.Folders.Count > 0)
            {
                StatusMessage = "Indexing documents; keep PowerPoint closed until this manual run finishes.";
                var docRequest = new IndexRequest(
                    Roots: settings.Folders,
                    Excluded: settings.Skipped,
                    SkipTypes: settings.SkipTypes,
                    Code: false,
                    AllowPowerPointAutomation: true);

                await backend.IndexAsync(docRequest, progress, ct);
            }

            if (settings.CodeFolders.Count > 0)
            {
                StatusMessage = "Indexing code roots...";
                var codeRequest = new IndexRequest(
                    Roots: settings.CodeFolders,
                    Excluded: settings.CodeSkipped,
                    SkipTypes: null,
                    Code: true);

                await backend.IndexAsync(codeRequest, progress, ct);
            }

            await RefreshStatusAsync();
            if (!lifetime.IsCancellationRequested && !isPauseControlBusy && IndexingState == IndexingControlState.Running)
                StatusMessage = "Indexing completed successfully.";
        }
        catch (OperationCanceledException)
        {
            await RefreshStatusAsync();
            if (!lifetime.IsCancellationRequested && !isPauseControlBusy && IndexingState == IndexingControlState.Running)
                StatusMessage = "Indexing was cancelled.";
        }
        catch (Exception ex)
        {
            await RefreshStatusAsync();
            if (!lifetime.IsCancellationRequested && !isPauseControlBusy)
                StatusMessage = $"Indexing failed: {ex.Message}";
        }
        finally
        {
            IsIndexingActive = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
