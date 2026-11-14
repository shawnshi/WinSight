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
    public ObservableCollection<string> CodeFolders { get; } = [];
    public ObservableCollection<string> CodeSkipped { get; } = [];
    public ObservableCollection<string> SkipTypes { get; } = [];

    private bool isCodeScope;
    public bool IsCodeScope
    {
        get => isCodeScope;
        set
        {
            if (SetProperty(ref isCodeScope, value))
            {
                OnPropertyChanged(nameof(CurrentScopeTitle));
                OnPropertyChanged(nameof(CurrentScopeSubtitle));
                OnPropertyChanged(nameof(CurrentFolders));
                OnPropertyChanged(nameof(CurrentSkipped));
                OnPropertyChanged(nameof(ShowExtensionExclusions));
            }
        }
    }

    public string CurrentScopeTitle => isCodeScope ? "已授权代码仓库 (code: 隔离库)" : "已授权文档与媒体目录";
    public string CurrentScopeSubtitle => isCodeScope
        ? "独立存储于 code.sqlite，仅在输入 code: 前缀或代码筛选时参与检索。"
        : "WinSight 仅对下方显式授权的目录执行全文与多模态向量检索（支持拖入文件夹）。";
    public ObservableCollection<string> CurrentFolders => isCodeScope ? CodeFolders : Folders;
    public ObservableCollection<string> CurrentSkipped => isCodeScope ? CodeSkipped : Skipped;
    public bool ShowExtensionExclusions => !isCodeScope;

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
            StatusMessage = $"加载配置失败：{ex.Message}";
        }
    }

    private void LoadFromSettings(AppSettings s)
    {
        Folders.Clear();
        foreach (var f in s.Folders) Folders.Add(f);

        Skipped.Clear();
        foreach (var sk in s.Skipped) Skipped.Add(sk);

        CodeFolders.Clear();
        foreach (var cf in s.CodeFolders) CodeFolders.Add(cf);

        CodeSkipped.Clear();
        foreach (var cs in s.CodeSkipped) CodeSkipped.Add(cs);

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
        if (Folders.Count == 0 && CodeFolders.Count == 0)
        {
            StatusMessage = "无法启用后台监听：请至少添加一个搜索目录。";
            return false;
        }

        try
        {
            var latest = await backend.LoadSettingsAsync();
            latest.Folders = [.. Folders];
            latest.Skipped = [.. Skipped];
            latest.CodeFolders = [.. CodeFolders];
            latest.CodeSkipped = [.. CodeSkipped];
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
        var target = isCodeScope ? CodeFolders : Folders;
        if (target.Contains(path, StringComparer.OrdinalIgnoreCase)) return false;

        target.Add(path);
        return await SaveAsync();
    }

    public async Task<bool> RemoveFolderAsync(string path)
    {
        var target = isCodeScope ? CodeFolders : Folders;
        var skippedTarget = isCodeScope ? CodeSkipped : Skipped;
        if (!target.Remove(path)) return false;

        // NM-UI-09: Clean up skipped items using boundary separator check (prevents deleting C:\docs2 when removing C:\docs)
        var toRemove = skippedTarget.Where(sk => FileActionPlanner.IsSameOrChildOf(sk, path)).ToList();
        foreach (var item in toRemove) skippedTarget.Remove(item);

        return await SaveAsync();
    }

    public async Task<bool> AddSkippedAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        var skippedTarget = isCodeScope ? CodeSkipped : Skipped;
        if (skippedTarget.Contains(path, StringComparer.OrdinalIgnoreCase)) return false;

        skippedTarget.Add(path);
        return await SaveAsync();
    }

    public async Task<bool> RemoveSkippedAsync(string path)
    {
        var skippedTarget = isCodeScope ? CodeSkipped : Skipped;
        if (!skippedTarget.Remove(path)) return false;
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
            latest.CodeFolders = [.. CodeFolders];
            latest.CodeSkipped = [.. CodeSkipped];
            latest.SkipTypes = [.. SkipTypes];
            latest.BackgroundIndexingEnabled = backgroundIndexingEnabled;
            if (backgroundIndexingEnabled && (Folders.Count > 0 || CodeFolders.Count > 0))
            {
                latest.SetUp = true;
            }

            await backend.SaveSettingsAsync(latest);
            settings = latest;
            OnPropertyChanged(nameof(IsSetUp));
            OnPropertyChanged(nameof(IsMonitoringActive));
            OnPropertyChanged(nameof(MonitoringStatusText));
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage = "目录配置已自动保存。";
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
