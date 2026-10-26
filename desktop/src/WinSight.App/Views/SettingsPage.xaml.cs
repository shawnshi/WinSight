using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    private readonly HotkeyService? hotkeyService;
    private readonly Action<string>? onThemeChanged;

    public SettingsPage(
        IDesktopBackend backend,
        AppSettings? settings = null,
        HotkeyService? hotkeyService = null,
        Action<string>? onThemeChanged = null)
    {
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
        await ViewModel.ProbeFoundryAsync();
    }
}
