using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;
using WinSight.Core.Diagnostics;
using WinSight.Core.Updates;

namespace WinSight.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    private readonly HotkeyService? hotkeyService;
    private readonly Action<string>? onThemeChanged;
    private readonly string? dataDirectory;
    private readonly Action? navigateToMcp;
    private bool isSyncingControls;
    private bool isRecordingHotkey;

    public SettingsPage(
        IDesktopBackend backend,
        AppSettings? settings = null,
        HotkeyService? hotkeyService = null,
        Action<string>? onThemeChanged = null,
        string? dataDirectory = null,
        Action? navigateToMcp = null)
    {
        this.dataDirectory = dataDirectory;
        this.navigateToMcp = navigateToMcp;
        ViewModel = new SettingsViewModel(backend, settings, hotkeyService);
        this.hotkeyService = hotkeyService;
        this.onThemeChanged = onThemeChanged;

        InitializeComponent();
        if (navigateToMcp is null && GoMcpConsoleButton is not null)
        {
            GoMcpConsoleButton.Visibility = Visibility.Collapsed;
        }

        if (hotkeyService != null)
        {
            ViewModel.HasHotkeyConflict = hotkeyService.HasConflict;
            ViewModel.HotkeyConflictMessage = hotkeyService.ConflictMessage;
            hotkeyService.HotkeyConflict += OnHotkeyConflict;
        }

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        isSyncingControls = true;
        try
        {
            try { await ViewModel.InitializeAsync(); } catch { }

            ThemeComboBox.SelectedIndex = ViewModel.Theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };

            TrayClickComboBox.SelectedIndex = ViewModel.TrayClick == "window" ? 1 : 0;
            CodeEditorComboBox.SelectedIndex = ViewModel.CodeEditor switch
            {
                "vscode" => 1,
                "cursor" => 2,
                "windsurf" => 3,
                "notepad++" => 4,
                "sublime" => 5,
                _ => 0
            };
            AcceleratorComboBox.SelectedIndex = ViewModel.Accelerator switch
            {
                "discrete-gpu" => 1,
                "npu" => 2,
                "gpu" => 3,
                "cpu" => 4,
                _ => 0
            };

            if (StartTimePicker is not null)
                StartTimePicker.Time = TimeSpan.FromMinutes(ViewModel.IndexHoursStart);
            if (EndTimePicker is not null)
                EndTimePicker.Time = TimeSpan.FromMinutes(ViewModel.IndexHoursEnd);
        }
        finally
        {
            isSyncingControls = false;
        }
    }

    private async void OnAutoSaveToggled(object sender, RoutedEventArgs e)
    {
        if (isSyncingControls) return;
        await ViewModel.SaveAsync();
    }

    private async void OnScheduleTimeChanged(object sender, TimePickerValueChangedEventArgs e)
    {
        if (isSyncingControls) return;
        if (ReferenceEquals(sender, StartTimePicker))
            ViewModel.IndexHoursStart = (int)e.NewTime.TotalMinutes;
        else if (ReferenceEquals(sender, EndTimePicker))
            ViewModel.IndexHoursEnd = (int)e.NewTime.TotalMinutes;
        await ViewModel.SaveAsync();
    }

    private void OnRecordHotkeyChecked(object sender, RoutedEventArgs e)
    {
        isRecordingHotkey = true;
        RecordHotkeyButton.Content = "请按下组合键…";
        HotkeyBox.Focus(FocusState.Programmatic);
    }

    private void OnRecordHotkeyUnchecked(object sender, RoutedEventArgs e)
    {
        isRecordingHotkey = false;
        RecordHotkeyButton.Content = "按下组合键录制";
    }

    private async void OnHotkeyBoxPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!isRecordingHotkey) return;
        e.Handled = true;

        var key = e.Key;
        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        if (key == VirtualKey.Escape)
        {
            RecordHotkeyButton.IsChecked = false;
            return;
        }

        var parts = new List<string>();
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl) parts.Add("Control");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (parts.Count == 0) return;

        parts.Add(key.ToString());
        var combo = string.Join("+", parts);
        ViewModel.Hotkey = combo;
        HotkeyBox.Text = combo;
        RecordHotkeyButton.IsChecked = false;
        await ViewModel.SaveAsync();
    }

    private void OnOpenMcpConsoleClick(object sender, RoutedEventArgs e) => navigateToMcp?.Invoke();

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateStatus.Text = "正在检查更新…";
            UpdateLink.Visibility = Visibility.Collapsed;
            var result = await UpdateChecker.CheckAsync(ViewModel.UpdateFeedUrl, ViewModel.AppVersion);
            UpdateStatus.Text = result.Message;
            if (result.UpdateAvailable && result.DownloadUrl is not null && Uri.TryCreate(result.DownloadUrl, UriKind.Absolute, out var link))
            {
                UpdateLink.NavigateUri = link;
                UpdateLink.Visibility = Visibility.Visible;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            AppLog.Warn("update check failed", error);
            UpdateStatus.Text = "检查更新失败：" + error.Message;
        }
    }

    private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (dataDirectory is null) { DiagnosticsStatus.Text = "数据目录不可用。"; return; }
        try
        {
            DiagnosticsStatus.Text = "正在收集脱敏诊断日志…";
            var path = await Task.Run(() => DiagnosticsBundle.Create(dataDirectory));
            DiagnosticsStatus.Text = "已保存至 " + path + "（仅包含日志、配置与脱敏路径，不含文档原文与索引内容）。";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("diagnostics export failed", error);
            DiagnosticsStatus.Text = "导出失败：" + error.Message;
        }
    }

    private void OnHotkeyConflict(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.HasHotkeyConflict = hotkeyService?.HasConflict ?? true;
            ViewModel.HotkeyConflictMessage = hotkeyService?.ConflictMessage ?? message;
        });
    }

    private async void OnApplyHotkeyClick(object sender, RoutedEventArgs e)
    {
        var key = HotkeyBox.Text?.Trim();
        if (string.IsNullOrEmpty(key)) return;
        ViewModel.Hotkey = key;
        await ViewModel.SaveAsync();
    }

    private async void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is ComboBoxItem item)
        {
            string theme = (item.Tag as string) ?? "Default";
            ViewModel.Theme = theme;
            onThemeChanged?.Invoke(theme);
            if (!isSyncingControls) await ViewModel.SaveAsync();
        }
    }

    private async void OnTrayClickSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrayClickComboBox.SelectedItem is ComboBoxItem item)
        {
            ViewModel.TrayClick = (item.Tag as string) ?? "panel";
            if (!isSyncingControls) await ViewModel.SaveAsync();
        }
    }

    private async void OnCodeEditorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CodeEditorComboBox.SelectedItem is ComboBoxItem item)
        {
            ViewModel.CodeEditor = (item.Tag as string) ?? "default";
            if (!isSyncingControls) await ViewModel.SaveAsync();
        }
    }

    private async void OnAcceleratorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AcceleratorComboBox.SelectedItem is ComboBoxItem item)
        {
            ViewModel.Accelerator = (item.Tag as string) ?? "auto";
            if (!isSyncingControls) await ViewModel.SaveAsync();
        }
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync();
    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private async void OnProbeNpuClick(object sender, RoutedEventArgs e) { try { await ViewModel.ProbeNpuAsync(); } catch (Exception ex) { AppLog.Error("OnProbeNpuClick failed", ex); } }
    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e) { try { await ViewModel.RefreshModelsAsync(); } catch (Exception ex) { AppLog.Error("OnRefreshModelsClick failed", ex); } }
    private async void OnRefreshRebuildClick(object sender, RoutedEventArgs e) { try { await ViewModel.RefreshRebuildStateAsync(); } catch (Exception ex) { AppLog.Error("OnRefreshRebuildClick failed", ex); } }
    private async void OnStartRebuildClick(object sender, RoutedEventArgs e) => await ViewModel.StartRebuildAsync();
    private async void OnInspectMigrationClick(object sender, RoutedEventArgs e) => await ViewModel.InspectMigrationAsync();
    private async void OnMigrateIndexClick(object sender, RoutedEventArgs e) => await ViewModel.MigrateIndexAsync();
    private async void OnMeasureVectorsClick(object sender, RoutedEventArgs e) => await ViewModel.RefreshVectorScaleAsync();
}
