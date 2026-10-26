using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }
    private readonly IDesktopBackend backend;
    private readonly AppSettings settings;
    private readonly HotkeyService? hotkeyService;
    private readonly IntPtr windowHandle;
    private readonly AppWindow appWindow;
    private bool isExiting;

    public IntPtr WindowHandle => windowHandle;

    public MainWindow(IDesktopBackend backend, AppSettings? settings = null, HotkeyService? hotkeyService = null, string? dataDirectory = null)
    {
        this.backend = backend;
        this.settings = settings ?? new AppSettings();
        this.hotkeyService = hotkeyService;
        ViewModel = new MainWindowViewModel(backend, this.settings, dataDirectory);

        InitializeComponent();

        windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinSight.ico"));

        appWindow.Closing += OnAppWindowClosing;

        ApplyTheme(this.settings.Theme);

        // Navigate to search page initially
        NavigateTo("search");

        _ = ViewModel.InitializeAsync();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!isExiting)
        {
            // Close button hides window to tray instead of quitting
            args.Cancel = true;
            appWindow.Hide();
        }
    }

    public void Quit()
    {
        isExiting = true;
        Close();
    }

    public void ShowAndActivate()
    {
        appWindow.Show();
        Activate();
    }

    public void ApplyTheme(string theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    public void NavigateTo(string tag, string? initialQuery = null, string? initialKind = null)
    {
        switch (tag)
        {
            case "search":
                var searchPage = new SearchPage(backend, settings);
                if (initialQuery != null)
                {
                    searchPage.SetInitialQuery(initialQuery, initialKind);
                }
                ContentFrame.Content = searchPage;
                break;
            case "folders":
                var foldersPage = new FoldersPage(backend, ViewModel.Settings, windowHandle);
                foldersPage.ViewModel.SettingsChanged += OnFolderSettingsChanged;
                ContentFrame.Content = foldersPage;
                break;
            case "storage":
                ContentFrame.Content = new StorageModelPage(backend, settings);
                break;
            case "mcp":
                ContentFrame.Content = new McpConsolePage(backend);
                break;
            case "settings":
                ContentFrame.Content = new SettingsPage(backend, settings, hotkeyService, ApplyTheme);
                break;
        }
    }

    private void OnFolderSettingsChanged(object? sender, EventArgs e) => _ = ViewModel.InitializeAsync();

    private async void OnPauseIndexingClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleIndexingPauseAsync();
    }

    private async void OnIndexNowClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.TriggerIndexNowAsync();
    }

    public string FormatStats(int done, int total) => $"Indexed: {done} / {total}";
    public bool Invert(bool value) => !value;
}
