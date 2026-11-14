using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }
    private readonly IDesktopBackend backend;
    private readonly string? dataDirectory;
    private AppSettings settings;
    private readonly HotkeyService? hotkeyService;
    private readonly IntPtr windowHandle;
    private readonly AppWindow appWindow;
    private readonly SearchLayoutStore? layoutStore;
    private readonly Dictionary<string, Page> cachedPages = new(StringComparer.Ordinal);
    private bool isExiting;

    public IntPtr WindowHandle => windowHandle;

    public MainWindow(IDesktopBackend backend, AppSettings? settings = null, HotkeyService? hotkeyService = null, string? dataDirectory = null)
    {
        this.backend = backend;
        this.dataDirectory = dataDirectory;
        this.settings = settings ?? new AppSettings();
        this.hotkeyService = hotkeyService;
        layoutStore = string.IsNullOrEmpty(dataDirectory) ? null : new SearchLayoutStore(SearchLayoutStore.FileIn(dataDirectory));
        ViewModel = new MainWindowViewModel(backend, this.settings, dataDirectory);

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinSight.ico"));

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = appWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }

        RestoreWindowPlacement();
        appWindow.Closing += OnAppWindowClosing;

        backend.SettingsSaved += saved =>
        {
            this.settings = saved;
            DispatcherQueue.TryEnqueue(() => ApplyTheme(saved.Theme));
        };

        ApplyTheme(this.settings.Theme);

        // Navigate to search page initially
        NavigateTo("search");

        _ = ViewModel.InitializeAsync();
    }

    private void RestoreWindowPlacement()
    {
        var saved = layoutStore?.MainWindowPlacement;
        if (saved is null)
        {
            appWindow.Resize(new SizeInt32(1280, 820));
            return;
        }

        try
        {
            int w = Math.Clamp((int)Math.Round(saved.Width), 900, 3840);
            int h = Math.Clamp((int)Math.Round(saved.Height), 620, 2160);
            appWindow.Resize(new SizeInt32(w, h));

            if (saved.X.HasValue && saved.Y.HasValue)
            {
                var pt = new PointInt32(saved.X.Value, saved.Y.Value);
                var area = DisplayArea.GetFromPoint(pt, DisplayAreaFallback.Primary);
                if (area != null && pt.X >= area.WorkArea.X - 40 && pt.Y >= area.WorkArea.Y - 20
                    && pt.X < area.WorkArea.X + area.WorkArea.Width - 100 && pt.Y < area.WorkArea.Y + area.WorkArea.Height - 100)
                {
                    appWindow.Move(pt);
                }
            }

            if (saved.Maximized && appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        catch { }
    }

    private void SaveWindowPlacement()
    {
        if (layoutStore is null) return;
        try
        {
            bool max = appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized;
            var size = appWindow.Size;
            var pos = appWindow.Position;
            if (size.Width >= 600 && size.Height >= 400)
            {
                layoutStore.MainWindowPlacement = max && layoutStore.MainWindowPlacement is { } prev
                    ? prev with { Maximized = true }
                    : new WindowPlacement(size.Width, size.Height, pos.X, pos.Y, max);
            }
        }
        catch { }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveWindowPlacement();
        if (!isExiting)
        {
            // Close button hides window to tray instead of quitting
            args.Cancel = true;
            appWindow.Hide();
        }
    }

    public void Quit()
    {
        SaveWindowPlacement();
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

    private void OnNavShortcutAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var tag = sender.Key switch
        {
            VirtualKey.Number1 => "search",
            VirtualKey.Number2 => "folders",
            VirtualKey.Number3 => "storage",
            VirtualKey.Number4 => "mcp",
            VirtualKey.Number5 => "settings",
            _ => null
        };
        if (tag is null) return;
        NavigateTo(tag);
        args.Handled = true;
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
        SyncNavSelection(tag);

        if (!cachedPages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "search" => new SearchPage(backend, settings, dataDirectory),
                "folders" => CreateFoldersPage(),
                "storage" => new StorageModelPage(backend, settings),
                "mcp" => new McpConsolePage(backend),
                "settings" => new SettingsPage(backend, settings, hotkeyService, ApplyTheme, dataDirectory, () => NavigateTo("mcp")),
                _ => null
            };
            if (page is not null)
                cachedPages[tag] = page;
        }

        if (page is SearchPage searchPage && initialQuery != null)
        {
            searchPage.SetInitialQuery(initialQuery, initialKind);
        }

        if (page is not null && !ReferenceEquals(ContentFrame.Content, page))
        {
            ContentFrame.Content = page;
        }
    }

    private FoldersPage CreateFoldersPage()
    {
        var foldersPage = new FoldersPage(backend, ViewModel.Settings, windowHandle);
        foldersPage.ViewModel.SettingsChanged += OnFolderSettingsChanged;
        return foldersPage;
    }

    private void SyncNavSelection(string tag)
    {
        if (NavView is null) return;
        var target = tag switch
        {
            "search" => NavSearch,
            "folders" => NavFolders,
            "storage" => NavStorage,
            "mcp" => NavMcp,
            "settings" => NavSettings,
            _ => null
        };
        if (target is not null && !ReferenceEquals(NavView.SelectedItem, target))
        {
            NavView.SelectedItem = target;
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

    public string FormatStats(int done, int total) => ViewModel.StatsSummary;
    public bool Invert(bool value) => !value;
}
