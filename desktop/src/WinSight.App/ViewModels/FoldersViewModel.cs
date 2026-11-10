using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinSight.App.Services;
using WinSight.Core;

namespace WinSight.App.ViewModels;

public sealed class FoldersViewModel : INotifyPropertyChanged
{
    private readonly ISettingsBackend backend;
    private AppSettings settings;
    private bool backgroundIndexingEnabled;
    private string statusMessage = string.Empty;

    public ObservableCollection<string> Folders { get; } = [];
    public ObservableCollection<string> Skipped { get; } = [];
    public ObservableCollection<string> SkipTypes { get; } = [];

    private bool isInitialized;
    private long loadVersion;
    public bool IsInitialized
    {
        get => isInitialized;
        private set => SetProperty(ref isInitialized, value);
    }
    public bool IsSetUp => settings.SetUp;
    public bool IsMonitoringActive => settings.SetUp && settings.BackgroundIndexingEnabled && (settings.Folders.Count > 0 || settings.CodeFolders.Count > 0);

    public string MonitoringStatusText
    {
        get
        {
            if (IsMonitoringActive)
                return "运行中：已授权目录受保护监听，文件保存后自动增量更新。";
            if (backgroundIndexingEnabled)
                return (Folders.Count > 0 || settings.CodeFolders.Count > 0)
                    ? "运行中：后台监听已启用，文件变动自动增量更新。"
                    : "等待配置：请至少添加一个搜索目录以启动实时监听。";
            return "未启用：后台监听处于关闭状态。可随时手动点击‘全部重新索引’。";
        }
    }

    public bool BackgroundIndexingEnabled
    {
        get => backgroundIndexingEnabled;
        set
        {
            if (SetProperty(ref backgroundIndexingEnabled, value))
            {
                OnPropertyChanged(nameof(MonitoringStatusText));
                OnPropertyChanged(nameof(IsMonitoringActive));
                _ = SaveAsync();
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        set => SetProperty(ref statusMessage, value);
    }

    public FoldersViewModel(ISettingsBackend backend, AppSettings? initialSettings = null)
    {
        this.backend = backend;
        this.settings = initialSettings ?? new AppSettings();
        LoadFromSettings(this.settings);
    }

    public async Task InitializeAsync()
    {
        var version = ++loadVersion;
        IsInitialized = false;
        try
        {
            var latest = await backend.LoadSettingsAsync();
            if (version != loadVersion) return;
            settings = latest;
            LoadFromSettings(settings);
            IsInitialized = true;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            if (version != loadVersion) return;
            IsInitialized = false;
            StatusMessage = $"Failed to load settings: {ex.Message}";
        }
    }

    private void LoadFromSettings(AppSettings s)
    {
        Folders.Clear();
        foreach (var f in s.Folders) Folders.Add(f);

        Skipped.Clear();
        foreach (var sk in s.Skipped) Skipped.Add(sk);

        SkipTypes.Clear();
        foreach (var st in s.SkipTypes) SkipTypes.Add(st);

        backgroundIndexingEnabled = s.BackgroundIndexingEnabled;
        OnPropertyChanged(nameof(BackgroundIndexingEnabled));
        OnPropertyChanged(nameof(IsSetUp));
        OnPropertyChanged(nameof(IsMonitoringActive));
        OnPropertyChanged(nameof(MonitoringStatusText));
    }

    public async Task<bool> ConfirmFoldersAndEnableMonitoringAsync()
    {
        if (Folders.Count == 0 && settings.CodeFolders.Count == 0)
        {
            StatusMessage = "无法启用后台监听：请至少添加一个搜索目录。";
            return false;
        }

        try
        {
            var latest = await backend.LoadSettingsAsync();
            latest.Folders = [.. Folders];
            latest.Skipped = [.. Skipped];
            latest.SkipTypes = [.. SkipTypes];
            latest.SetUp = true;
            latest.BackgroundIndexingEnabled = true;

            await backend.SaveSettingsAsync(latest);
            settings = latest;
            LoadFromSettings(settings);
            OnPropertyChanged(nameof(BackgroundIndexingEnabled));
            OnPropertyChanged(nameof(IsSetUp));
            OnPropertyChanged(nameof(IsMonitoringActive));
            OnPropertyChanged(nameof(MonitoringStatusText));
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage = "目录已确认，后台监听增量索引已激活。";
            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("revision conflict", StringComparison.OrdinalIgnoreCase))
        {
            await ReloadAfterSaveFailureAsync($"Revision conflict: {ex.Message}");
            return false;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Stop indexing before changing settings", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "正在全量索引中，已为您保持监听就绪。";
            return false;
        }
        catch (Exception ex)
        {
            await ReloadAfterSaveFailureAsync($"Confirmation error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> AddFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        if (Folders.Contains(path, StringComparer.OrdinalIgnoreCase)) return false;

        Folders.Add(path);
        return await SaveAsync();
    }

    public async Task<bool> RemoveFolderAsync(string path)
    {
        if (!Folders.Remove(path)) return false;

        // NM-UI-09: Clean up skipped items using boundary separator check (prevents deleting C:\docs2 when removing C:\docs)
        var toRemove = Skipped.Where(sk => FileActionPlanner.IsSameOrChildOf(sk, path)).ToList();
        foreach (var item in toRemove) Skipped.Remove(item);

        return await SaveAsync();
    }

    public async Task<bool> AddSkippedAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        if (Skipped.Contains(path, StringComparer.OrdinalIgnoreCase)) return false;

        Skipped.Add(path);
        return await SaveAsync();
    }

    public async Task<bool> RemoveSkippedAsync(string path)
    {
        if (!Skipped.Remove(path)) return false;
        return await SaveAsync();
    }

    public async Task<bool> AddSkipTypeAsync(string ext)
    {
        var normalized = ext.Trim();
        if (!normalized.StartsWith('.')) normalized = "." + normalized;
        if (SkipTypes.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return false;

        SkipTypes.Add(normalized);
        return await SaveAsync();
    }

    public async Task<bool> RemoveSkipTypeAsync(string ext)
    {
        if (!SkipTypes.Remove(ext)) return false;
        return await SaveAsync();
    }

    public async Task<bool> SaveAsync()
    {
        try
        {
            var latest = await backend.LoadSettingsAsync();
            latest.Folders = [.. Folders];
            latest.Skipped = [.. Skipped];
            latest.SkipTypes = [.. SkipTypes];
            latest.BackgroundIndexingEnabled = backgroundIndexingEnabled;
            if (backgroundIndexingEnabled && (Folders.Count > 0 || latest.CodeFolders.Count > 0))
            {
                latest.SetUp = true;
            }

            await backend.SaveSettingsAsync(latest);
            settings = latest;
            OnPropertyChanged(nameof(IsSetUp));
            OnPropertyChanged(nameof(IsMonitoringActive));
            OnPropertyChanged(nameof(MonitoringStatusText));
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage = "Settings saved.";
            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("revision conflict", StringComparison.OrdinalIgnoreCase))
        {
            await ReloadAfterSaveFailureAsync($"Revision conflict: {ex.Message}");
            return false;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Stop indexing before changing settings", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "正在全量索引中，请先暂停索引或稍后再变更目录。";
            return false;
        }
        catch (Exception ex)
        {
            await ReloadAfterSaveFailureAsync($"Save error: {ex.Message}");
            return false;
        }
    }

    private async Task ReloadAfterSaveFailureAsync(string message)
    {
        await InitializeAsync();
        StatusMessage = IsInitialized ? message : $"{message} {StatusMessage}";
    }

    public event EventHandler? SettingsChanged;
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
