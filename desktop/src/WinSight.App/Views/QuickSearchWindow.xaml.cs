using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.App.Views;

public sealed partial class QuickSearchWindow : Window
{
    public QuickSearchViewModel ViewModel { get; }
    private readonly IntPtr windowHandle;
    private readonly AppWindow appWindow;
    private CancellationTokenSource? quickPreviewCts;
    private bool isExiting;

    public event Action<string, string?>? OpenInFullWindowRequested;

    public QuickSearchWindow(IDesktopBackend backend, AppSettings? settings = null, string? dataDir = null)
    {
        ViewModel = new QuickSearchViewModel(backend, dataDir);
        InitializeComponent();

        windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        appWindow = AppWindow.GetFromWindowId(windowId);

        // True borderless rounded Spotlight HUD via OverlappedPresenter + DWM rounded corners
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }
        NativeMethods.ApplyRoundedWindowCorners(windowHandle);

        appWindow.Resize(new SizeInt32(840, 540));
        CenterOnActiveMonitor();

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
        quickPreviewCts?.Cancel();
        quickPreviewCts?.Dispose();
        isExiting = true;
        Close();
    }

    private void CenterOnActiveMonitor()
    {
        DisplayArea? displayArea = null;
        if (NativeMethods.GetCursorPos(out var pt))
        {
            displayArea = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Primary);
        }
        displayArea ??= DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);

        if (displayArea != null)
        {
            var workArea = displayArea.WorkArea;
            int w = Math.Min(840, workArea.Width - 32);
            int h = Math.Min(540, workArea.Height - 32);
            appWindow.Resize(new SizeInt32(w, h));
            int x = workArea.X + (workArea.Width - w) / 2;
            int y = workArea.Y + (workArea.Height - h) / 3; // vertically biased to upper third
            appWindow.Move(new PointInt32(x, y));
        }
    }

    public void ShowAndFocus()
    {
        CenterOnActiveMonitor();
        appWindow.Show();
        Activate();
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    public void HidePanel()
    {
        quickPreviewCts?.Cancel();
        appWindow.Hide();
    }

    public void TogglePanel()
    {
        if (appWindow.IsVisible) HidePanel();
        else ShowAndFocus();
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
            bool shiftHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shiftHeld)
            {
                // Ctrl+Shift+C: copy excerpt text
                if (!string.IsNullOrEmpty(ViewModel.SelectedItem?.Excerpt))
                {
                    var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dataPackage.SetText(ViewModel.SelectedItem.Excerpt);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                    ViewModel.StatusText = "已复制命中摘要到剪贴板";
                }
            }
            else
            {
                // Ctrl+C: consistent with SearchPage — copy full file path
                _ = ViewModel.CopyCurrentAsync();
            }
            e.Handled = true;
        }
    }

    private async void HandleOpenAction()
    {
        bool ctrlHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shiftHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrlHeld || shiftHeld)
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

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Bindings.Update();
        SearchPage.ApplyQueryHighlights(QuickCanvasExcerptBlock, ViewModel.Query);
        SearchPage.ApplyQueryHighlights(QuickCodeExcerptBlock, ViewModel.Query);
        _ = UpdateQuickPreviewAsync(ViewModel.SelectedItem);
    }

    private async Task UpdateQuickPreviewAsync(SearchItemViewModel? selected)
    {
        quickPreviewCts?.Cancel();
        quickPreviewCts?.Dispose();
        quickPreviewCts = new CancellationTokenSource();
        var ct = quickPreviewCts.Token;

        if (QuickDocumentRasterImage != null)
        {
            QuickDocumentRasterImage.Source = null;
            QuickDocumentRasterImage.Visibility = Visibility.Collapsed;
        }
        if (QuickExcerptScroll != null) QuickExcerptScroll.Visibility = Visibility.Visible;
        if (QuickHeroImage != null) QuickHeroImage.Source = null;

        if (selected is null) return;

        if (selected.IsImageVisibility == Visibility.Visible && File.Exists(selected.Path))
        {
            try
            {
                var bmp = new BitmapImage { DecodePixelWidth = 640 };
                bmp.UriSource = new Uri(selected.Path);
                if (QuickHeroImage != null) QuickHeroImage.Source = bmp;
            }
            catch { }
            return;
        }

        if (selected.IsDocumentVisibility == Visibility.Visible && QuickDocumentRasterImage != null)
        {
            ISourcePreview? preview = null;
            try
            {
                await Task.Delay(140, ct);
                preview = await ViewModel.PreviewAsync(selected, ct);
                if (ct.IsCancellationRequested) return;
                if (preview.SourceCurrent && preview.RasterPath is { } rasterPath && File.Exists(rasterPath))
                {
                    var bitmap = new BitmapImage();
                    await using (var file = new FileStream(rasterPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        using IRandomAccessStream stream = file.AsRandomAccessStream();
                        await bitmap.SetSourceAsync(stream);
                    }
                    if (ct.IsCancellationRequested) return;
                    QuickDocumentRasterImage.Source = bitmap;
                    QuickDocumentRasterImage.Visibility = Visibility.Visible;
                    if (QuickExcerptScroll != null) QuickExcerptScroll.Visibility = Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                if (preview is not null)
                {
                    try { await preview.DisposeAsync(); } catch { }
                }
            }
        }
    }

    private async void OnQuickOpenHeroClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.OpenCurrentAsync();
        HidePanel();
    }

    private async void OnQuickRevealClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RevealCurrentAsync();
        HidePanel();
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectedToEmptyVisibility(object? item) => item == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectedToDetailVisibility(object? item) => item != null ? Visibility.Visible : Visibility.Collapsed;

    private void OnKindSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ViewModel is null) return;
        if (sender.SelectedItem?.Tag is string tag)
        {
            ViewModel.SelectedKind = tag == "all" ? null : tag;
        }
    }

    private void OnOpenInFullWindowClick(object sender, RoutedEventArgs e)
    {
        HidePanel();
        OpenInFullWindowRequested?.Invoke(ViewModel.Query, ViewModel.SelectedKind);
    }

    private void OnResultsContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not SearchItemViewModel item) return;
        if (args.InRecycleQueue) { item.CancelThumbnail(); return; }
        _ = item.EnsureThumbnailAsync();
    }
}
