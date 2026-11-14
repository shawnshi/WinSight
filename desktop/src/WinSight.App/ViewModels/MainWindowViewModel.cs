using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinSight.App.Services;
using WinSight.Core;

namespace WinSight.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IDesktopBackend backend;
    public string PreviewNotice { get; }
    private AppSettings settings;
    private EngineStatus? status;
    private bool isIndexingActive;
    private string statusMessage = "就绪";
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
        IndexingControlState.Running => UiText.IndexingAllowed,
        IndexingControlState.Pausing => UiText.IndexingPausing,
        IndexingControlState.Paused => UiText.IndexingPaused,
        _ => UiText.IndexingUnknown
    };
    public string PauseActionText => isPauseControlBusy
        ? (pauseRequested ? "正在暂停…" : "正在恢复…")
        : IndexingState switch
        {
            IndexingControlState.Running => UiText.PauseIndexing,
            IndexingControlState.Pausing or IndexingControlState.Paused => UiText.ResumeIndexing,
            _ => UiText.RetryIndexingStatus
        };

    public Brush StatusDotBrush
    {
        get
        {
            var key = IndexingState switch
            {
                IndexingControlState.Running => "SystemFillColorSuccessBrush",
                IndexingControlState.Pausing => "SystemFillColorCautionBrush",
                IndexingControlState.Paused => "TextFillColorTertiaryBrush",
                _ => "SystemFillColorCriticalBrush"
            };
            if (Application.Current?.Resources.TryGetValue(key, out var res) == true && res is Brush b)
                return b;
            return new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
        }
    }

    private void NotifyIndexingControl()
    {
        OnPropertyChanged(nameof(IndexingState));
        OnPropertyChanged(nameof(IndexingStateText));
        OnPropertyChanged(nameof(StatusDotBrush));
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
        StatusMessage = pauseRequested ? "已请求暂停，正在等待索引停止…" : "正在恢复索引…";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        string? error = null;
        try
        {
            await backend.SetIndexingPausedAsync(pauseRequested, linked.Token);
        }
        catch (OperationCanceledException)
        {
            error = "索引控制操作已取消。";
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
    public string IndexPath => status?.IndexPath ?? "默认位置";
    public string? StatusDetail => status?.Detail;

    public string StatsSummary
    {
        get
        {
            if (status is null || status.Files == 0) return "暂无已索引文件";
            if (status.Pending > 0 || status.Failed > 0 || status.Partial > 0)
            {
                var parts = new List<string>();
                parts.Add($"{status.Done} 完成");
                if (status.Partial > 0) parts.Add($"{status.Partial} 部分");
                if (status.Pending > 0) parts.Add($"{status.Pending} 待处理");
                if (status.Failed > 0) parts.Add($"{status.Failed} 失败");
                return $"已入库: {string.Join(", ", parts)} / 共 {status.Files}";
            }
            return $"已入库: {status.Done} / {status.Files}";
        }
    }

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
                    ? "尚未配置索引文件夹，请先添加一个文件夹。"
                    : "尚未建立索引。请点击“立即索引”处理已配置的文件夹。")
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

    public async Task TriggerIndexNowAsync(CancellationToken cancellationToken = default)
    {
        if (!CanIndexNow || cancellationToken.IsCancellationRequested) return;

        IsIndexingActive = true;
        StatusMessage = "正在读取最新的索引设置…";

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
                StatusMessage = "尚未配置索引文件夹，请先添加一个文件夹。";
                return;
            }

            var lastRefresh = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<IndexProgress>(p =>
            {
                if (!lifetime.IsCancellationRequested && !isPauseControlBusy && IndexingState == IndexingControlState.Running)
                {
                    string phaseText = p.Phase switch
                    {
                        "pending" => "正在处理",
                        "done" => "已完成",
                        "failed" => "失败",
                        "skipped" => "已跳过",
                        "partial" => "部分完成",
                        "unchanged" => "未改动",
                        "finished" => "索引扫描结束",
                        _ => p.Phase
                    };
                    var fileName = !string.IsNullOrEmpty(p.Path) ? System.IO.Path.GetFileName(p.Path) : null;
                    StatusMessage = $"{phaseText}: 已处理 {p.Completed} 个{(p.Failed > 0 ? $", {p.Failed} 失败" : "")}{(fileName is not null ? $" — {fileName}" : "")}";
                    if (lastRefresh.ElapsedMilliseconds >= 1500)
                    {
                        lastRefresh.Restart();
                        _ = RefreshStatusAsync();
                    }
                }
            });

            // NM-UI-05: Handle document folders and code folders separately.
            // Never pass Code=true on doc roots!
            if (settings.Folders.Count > 0)
            {
                StatusMessage = "正在索引文档，建议保持 PowerPoint 处于空闲状态…";
                var docRequest = new IndexRequest(
                    Roots: settings.Folders,
                    Excluded: settings.Skipped,
                    SkipTypes: settings.SkipTypes,
                    Code: false,
                    AllowPowerPointAutomation: true,
                    GeneratePageImages: settings.GeneratePageImages);

                await backend.IndexAsync(docRequest, progress, ct);
            }

            if (settings.CodeFolders.Count > 0)
            {
                StatusMessage = "正在索引代码目录…";
                var codeRequest = new IndexRequest(
                    Roots: settings.CodeFolders,
                    Excluded: settings.CodeSkipped,
                    SkipTypes: null,
                    Code: true);

                await backend.IndexAsync(codeRequest, progress, ct);
            }

            await RefreshStatusAsync();
            if (!lifetime.IsCancellationRequested && !isPauseControlBusy && IndexingState == IndexingControlState.Running)
            {
                if (status is not null)
                {
                    if (status.Pending > 0 || status.Failed > 0)
                    {
                        var parts = new List<string>();
                        parts.Add($"{status.Done} 完成");
                        if (status.Partial > 0) parts.Add($"{status.Partial} 部分");
                        if (status.Pending > 0) parts.Add($"{status.Pending} 待处理");
                        if (status.Failed > 0) parts.Add($"{status.Failed} 失败");
                        StatusMessage = $"索引未完成：{string.Join("，", parts)} / 共 {status.Files}。";
                    }
                    else
                    {
                        StatusMessage = $"索引完成：共 {status.Files} 个文件全部入库。";
                    }
                }
                else
                {
                    StatusMessage = "索引完成。";
                }
            }
        }
        catch (OperationCanceledException)
        {
            await RefreshStatusAsync();
            if (!lifetime.IsCancellationRequested && !isPauseControlBusy && IndexingState == IndexingControlState.Running)
                StatusMessage = "索引已取消。";
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
