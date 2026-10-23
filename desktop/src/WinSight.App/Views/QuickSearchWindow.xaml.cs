using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.App.Views;

public sealed partial class QuickSearchWindow : Window
{
    public QuickSearchViewModel ViewModel { get; }
    private readonly IntPtr windowHandle;
    private readonly AppWindow appWindow;
    private bool isExiting;

    public event Action<string, string?>? OpenInFullWindowRequested;

    public QuickSearchWindow(IDesktopBackend backend, AppSettings? settings = null, string? dataDir = null)
    {
        ViewModel = new QuickSearchViewModel(backend, dataDir);
        InitializeComponent();

        windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        appWindow = AppWindow.GetFromWindowId(windowId);

        // Size to 680x440 and center
        appWindow.Resize(new SizeInt32(680, 440));
        CenterOnScreen();

        // NM-UI-08: Intercept close (Alt+F4 / system close) to hide rather than destroy window
        appWindow.Closing += OnAppWindowClosing;

        if (settings != null)
        {
            ApplyTheme(settings.Theme);
        }

        Activated += OnWindowActivated;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!isExiting)
        {
            args.Cancel = true;
            appWindow.Hide();
        }
    }

    public void Quit()
    {
        isExiting = true;
        Close();
    }

    private void CenterOnScreen()
    {
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            var workArea = displayArea.WorkArea;
            int x = workArea.X + (workArea.Width - 680) / 2;
            int y = workArea.Y + (workArea.Height - 440) / 3; // slightly above center
            appWindow.Move(new PointInt32(x, y));
        }
    }

    public void ShowAndFocus()
    {
        CenterOnScreen();
        appWindow.Show();
        Activate();
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    public void HidePanel()
    {
        appWindow.Hide();
    }

    public void TogglePanel()
    {
        if (appWindow.IsVisible)
        {
            HidePanel();
        }
        else
        {
            ShowAndFocus();
        }
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

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Auto-hide when focus is lost
            HidePanel();
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HidePanel();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Down && ViewModel.Hits.Count > 0)
        {
            ResultsListView.Focus(FocusState.Programmatic);
            ViewModel.SelectedIndex = 0;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            HandleOpenAction();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.O && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            OnOpenInFullWindowClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnListViewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HidePanel();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up && ViewModel.SelectedIndex == 0)
        {
            SearchBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            HandleOpenAction();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.C && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            _ = ViewModel.CopyCurrentAsync();
            e.Handled = true;
        }
    }

    private async void HandleOpenAction()
    {
        bool shiftHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shiftHeld)
        {
            await ViewModel.RevealCurrentAsync();
        }
        else
        {
            await ViewModel.OpenCurrentAsync();
        }
        HidePanel();
    }

    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchItemViewModel item)
        {
            await ViewModel.OpenHitAsync(item);
            HidePanel();
        }
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            var defaultStyle = (Style)Application.Current.Resources["DefaultButtonStyle"];
            var accentStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

            FilterAllBtn.Style = tag == "all" ? accentStyle : defaultStyle;
            FilterDocBtn.Style = tag == "doc" ? accentStyle : defaultStyle;
            FilterPdfBtn.Style = tag == "pdf" ? accentStyle : defaultStyle;
            FilterImgBtn.Style = tag == "image" ? accentStyle : defaultStyle;
            FilterVideoBtn.Style = tag == "video" ? accentStyle : defaultStyle;
            FilterCodeBtn.Style = tag == "code" ? accentStyle : defaultStyle;

            ViewModel.SelectedKind = tag == "all" ? null : tag;
        }
    }

    private void OnOpenInFullWindowClick(object sender, RoutedEventArgs e)
    {
        HidePanel();
        OpenInFullWindowRequested?.Invoke(ViewModel.Query, ViewModel.SelectedKind);
    }
}
