using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;
using WinSight.Core.Diagnostics;
using WinSight.Core.Updates;

namespace WinSight.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    /// <summary>Manual update check against the configured feed; nothing is downloaded or installed.</summary>
    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateStatus.Text = "Checking...";
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
            UpdateStatus.Text = "The update check failed: " + error.Message;
        }
    }

    /// <summary>Builds the redacted support bundle off the UI thread and reveals it in Explorer.</summary>
    private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (dataDirectory is null) { DiagnosticsStatus.Text = "The data directory is unavailable."; return; }
        try
        {
            DiagnosticsStatus.Text = "Collecting logs...";
            var path = await Task.Run(() => DiagnosticsBundle.Create(dataDirectory));
            DiagnosticsStatus.Text = "Saved " + path + ". It holds logs, settings and a file listing with user names redacted; no documents or index content.";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("diagnostics export failed", error);
            DiagnosticsStatus.Text = "Export failed: " + error.Message;
        }
    }
    private readonly HotkeyService? hotkeyService;
    private readonly Action<string>? onThemeChanged;
    private readonly string? dataDirectory;

    public SettingsPage(
        IDesktopBackend backend,
        AppSettings? settings = null,
        HotkeyService? hotkeyService = null,
        Action<string>? onThemeChanged = null,
        string? dataDirectory = null)
    {
        this.dataDirectory = dataDirectory;
        ViewModel = new SettingsViewModel(backend, settings, hotkeyService);
        this.hotkeyService = hotkeyService;
        this.onThemeChanged = onThemeChanged;

        InitializeComponent();

        if (hotkeyService != null)
        {
            ViewModel.HasHotkeyConflict = hotkeyService.HasConflict;
            ViewModel.HotkeyConflictMessage = hotkeyService.ConflictMessage;
            hotkeyService.HotkeyConflict += OnHotkeyConflict;
        }

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Select matching combo items
        ThemeComboBox.SelectedIndex = ViewModel.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };

        TrayClickComboBox.SelectedIndex = ViewModel.TrayClick == "window" ? 1 : 0;
        CodeEditorComboBox.SelectedIndex = ViewModel.CodeEditor == "vscode" ? 1 : 0;
        AcceleratorComboBox.SelectedIndex = ViewModel.Accelerator switch
        {
            "discrete-gpu" => 1,
            "npu" => 2,
            "gpu" => 3,
            "cpu" => 4,
            _ => 0
        };
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

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is ComboBoxItem item)
        {
            string theme = item.Content.ToString() ?? "Default";
            ViewModel.Theme = theme;
            onThemeChanged?.Invoke(theme);
        }
    }

    private void OnTrayClickSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrayClickComboBox.SelectedItem is ComboBoxItem item)
        {
            ViewModel.TrayClick = item.Content.ToString() == "Main Window" ? "window" : "panel";
        }
    }

    private void OnCodeEditorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CodeEditorComboBox.SelectedItem is ComboBoxItem item)
        {
            ViewModel.CodeEditor = item.Content.ToString()?.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase) == true
                ? "vscode"
                : "default";
        }
    }

    private void OnAcceleratorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AcceleratorComboBox.SelectedItem is ComboBoxItem item)
        {
            // Every item carries an explicit Tag; display text is presentation only.
            ViewModel.Accelerator = (item.Tag as string) ?? "auto";
        }
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveAsync();
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private async void OnProbeFoundryClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ProbeFoundryAsync(); } catch (Exception ex) { AppLog.Error("OnProbeFoundryClick failed", ex); }
    }

    private async void OnInstallFoundryProvidersClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.InstallFoundryProvidersAsync(); } catch (Exception ex) { AppLog.Error("OnInstallFoundryProvidersClick failed", ex); }
    }

    private async void OnProbeNpuClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ProbeNpuAsync(); } catch (Exception ex) { AppLog.Error("OnProbeNpuClick failed", ex); }
    }

    private async void OnPreviewClientConfigClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.PreviewClientConfigAsync(); } catch (Exception ex) { AppLog.Error("OnPreviewClientConfigClick failed", ex); }
    }

    private async void OnApplyClientConfigClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ApplyClientConfigAsync(); } catch (Exception ex) { AppLog.Error("OnApplyClientConfigClick failed", ex); }
    }



    private async void OnDrainMaintenanceClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.DrainMaintenanceAsync(); } catch (Exception ex) { AppLog.Error("OnDrainMaintenanceClick failed", ex); }
    }

    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.RefreshModelsAsync(); } catch (Exception ex) { AppLog.Error("OnRefreshModelsClick failed", ex); }
    }

    private async void OnRefreshRebuildClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.RefreshRebuildStateAsync(); } catch (Exception ex) { AppLog.Error("OnRefreshRebuildClick failed", ex); }
    }

    private async void OnStartRebuildClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.StartRebuildAsync();
    }

    private async void OnInspectMigrationClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.InspectMigrationAsync();
    }

    private async void OnMigrateIndexClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.MigrateIndexAsync();
    }

    private async void OnMeasureVectorsClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshVectorScaleAsync();
    }
}
