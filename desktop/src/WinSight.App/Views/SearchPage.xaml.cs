using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;
using WinSight.Core.Diagnostics;

namespace WinSight.App.Views;

public sealed partial class SearchPage : Page
{
    public QuickSearchViewModel ViewModel { get; }
    private WeakReference<Control>? previewReturnFocus;
    private bool previewSessionOpen;
    private bool closingPreview;
    private CancellationTokenSource? heroPreviewCts;

    public SearchPage(IDesktopBackend backend, AppSettings? settings = null)
    {
        ViewModel = new QuickSearchViewModel(backend);
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QuickSearchViewModel.SelectedItem) or nameof(QuickSearchViewModel.SelectedIndex))
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                Bindings.Update();
                UpdateHeroStage(ViewModel.SelectedItem);
            });
        }
    }

    private void OnResultsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Bindings.Update();
        UpdateHeroStage(ViewModel.SelectedItem);
    }

    private async void UpdateHeroStage(SearchItemViewModel? selected)
    {
        heroPreviewCts?.Cancel();
        heroPreviewCts?.Dispose();
        heroPreviewCts = new CancellationTokenSource();
        var ct = heroPreviewCts.Token;

        ApplyQueryHighlights(CanvasExcerptBlock, ViewModel.Query);
        ApplyQueryHighlights(CodeExcerptBlock, ViewModel.Query);
        ApplyQueryHighlights(MatchCalloutExcerptBlock, ViewModel.Query);

        if (DocumentRasterImage != null)
        {
            DocumentRasterImage.Source = null;
            DocumentRasterImage.Visibility = Visibility.Collapsed;
        }
        if (DocumentExcerptScroll != null)
        {
            DocumentExcerptScroll.Visibility = Visibility.Visible;
        }

        if (selected is null)
        {
            if (HeroPreviewImage != null) HeroPreviewImage.Source = null;
            return;
        }

        if (selected.IsImageVisibility == Visibility.Visible && File.Exists(selected.Path))
        {
            try
            {
                var bmp = new BitmapImage { DecodePixelWidth = 800 };
                bmp.UriSource = new Uri(selected.Path);
                if (HeroPreviewImage != null) HeroPreviewImage.Source = bmp;
                return;
            }
            catch { }
        }
        if (HeroPreviewImage != null) HeroPreviewImage.Source = null;

        // Auto-load high-resolution page raster preview for documents (PPTX slides, PDF pages)
        if (selected.IsDocumentVisibility == Visibility.Visible && DocumentRasterImage != null)
        {
            try
            {
                var preview = await ViewModel.PreviewAsync(selected, ct);
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

                    DocumentRasterImage.Source = bitmap;
                    DocumentRasterImage.Visibility = Visibility.Visible;
                    if (DocumentExcerptScroll != null) DocumentExcerptScroll.Visibility = Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLog.Warn("Auto hero preview load failed", ex);
            }
        }
    }

    private async void OnDocumentRasterImageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } selected)
        {
            await ShowPreviewAsync(selected, keyboard: false);
        }
    }

    internal static void ApplyQueryHighlights(TextBlock? block, string? rawQuery)
    {
        if (block is null) return;
        block.TextHighlighters.Clear();
        if (string.IsNullOrWhiteSpace(rawQuery) || string.IsNullOrEmpty(block.Text)) return;

        var q = rawQuery.Trim();
        if (q.StartsWith("code:", StringComparison.OrdinalIgnoreCase)) q = q[5..].Trim();
        if (string.IsNullOrWhiteSpace(q)) return;

        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = part.Trim('"', '\'', '，', '。', ',', ';');
            if (clean.Length >= 1) terms.Add(clean);
            // For CJK phrases >= 4 chars (e.g. "卫宁健康"), also highlight 2-char sub-terms ("卫宁", "健康")
            if (clean.Length >= 4 && clean.All(c => c >= 0x4E00 && c <= 0x9FFF))
            {
                for (int i = 0; i <= clean.Length - 2; i += 2)
                    terms.Add(clean.Substring(i, 2));
            }
        }
        if (terms.Count == 0) return;

        var text = block.Text;
        var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(100, 242, 169, 0))
        };

        foreach (var term in terms.OrderByDescending(t => t.Length))
        {
            int idx = 0;
            while (idx < text.Length)
            {
                int found = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                highlighter.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange { StartIndex = found, Length = term.Length });
                idx = found + Math.Max(1, term.Length);
                if (highlighter.Ranges.Count >= 64) break;
            }
            if (highlighter.Ranges.Count >= 64) break;
        }

        if (highlighter.Ranges.Count > 0)
            block.TextHighlighters.Add(highlighter);
    }

    private void OnResultsDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<SearchItemViewModel>().Where(i => System.IO.File.Exists(i.Path)).ToArray();
        if (items.Length == 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.Data.SetText(string.Join(Environment.NewLine, items.Select(i => i.Path)));
        e.Data.SetDataProvider(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var storageFiles = new List<Windows.Storage.IStorageItem>();
                foreach (var item in items)
                {
                    try { storageFiles.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(item.Path)); }
                    catch { }
                }
                if (storageFiles.Count > 0) request.SetData(storageFiles);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void OnContextOpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SearchItemViewModel item) _ = ViewModel.OpenHitAsync(item);
    }

    private void OnContextRevealClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SearchItemViewModel item) _ = ViewModel.RevealHitAsync(item);
    }

    private void OnContextCopyExcerptClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SearchItemViewModel item)
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(!string.IsNullOrWhiteSpace(item.Excerpt) ? item.Excerpt : item.Path);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        }
    }

    private async void OnContextCopyPathClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SearchItemViewModel item) await ViewModel.CopyHitAsync(item);
    }

    private async void OnContextPreviewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SearchItemViewModel item) await ShowPreviewAsync(item, keyboard: false);
    }

    public void SetInitialQuery(string query, string? kind = null)
    {
        if (kind is not null)
        {
            KindFilterCombo.SelectedIndex = kind.ToLowerInvariant() switch
            {
                "doc" or "document" => 1,
                "pdf" => 2,
                "image" => 3,
                "screenshot" => 4,
                "audio" => 5,
                "video" or "media" => 6,
                "code" => 7,
                _ => 0
            };
        }
        ViewModel.Query = query;
    }

    private void OnKindFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KindFilterCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            ViewModel.SelectedKind = tag == "all" ? null : tag;
        }
    }

    private async void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && ViewModel.Hits.Count > 0)
        {
            e.Handled = true;
            ViewModel.SelectedIndex = 0;
            try { await FocusSelectedResultAsync(); }
            catch (Exception error) { ShowPreviewError("Result focus failed: " + error.Message); }
        }
        else if (e.Key == VirtualKey.Enter)
        {
            _ = ViewModel.OpenCurrentAsync();
            e.Handled = true;
        }
    }

    private void OnResultItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchItemViewModel item)
        {
            var idx = ViewModel.Hits.IndexOf(item);
            if (idx >= 0) ViewModel.SelectedIndex = idx;
        }
    }

    private void OnResultsDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
        {
            _ = ViewModel.OpenHitAsync(item);
        }
    }

    private void OnOpenSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
        {
            _ = ViewModel.OpenHitAsync(item);
        }
    }

    private void OnRevealSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
        {
            _ = ViewModel.RevealHitAsync(item);
        }
    }

    private async void OnCopySelectedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
        {
            await ViewModel.CopyHitAsync(item);
        }
    }

    private async void OnPreviewSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } selected)
        {
            await ShowPreviewAsync(selected, keyboard: false);
        }
    }

    private void OnOpenItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SearchItemViewModel item)
        {
            _ = ViewModel.OpenHitAsync(item);
        }
    }

    private void OnRevealItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SearchItemViewModel item)
        {
            _ = ViewModel.RevealHitAsync(item);
        }
    }

    private async void OnCopyItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SearchItemViewModel item)
        {
            await ViewModel.CopyHitAsync(item);
        }
    }

    private async void OnResultsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || HasKeyModifier() || IsInteractiveChild(e.OriginalSource as DependencyObject)) return;
        if (e.Key == VirtualKey.Space && ViewModel.SelectedItem is { } selected)
        {
            e.Handled = true;
            await ShowPreviewAsync(selected, keyboard: true);
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ViewModel.OpenCurrentAsync();
        }
    }

    private static bool HasKeyModifier() => new[] { VirtualKey.Control, VirtualKey.Menu, VirtualKey.Shift, VirtualKey.LeftWindows, VirtualKey.RightWindows }
        .Any(key => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));

    private static bool IsInteractiveChild(DependencyObject? element)
    {
        while (element is not null && element is not ListViewItem && element is not ListView)
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or TextBox or PasswordBox or RichEditBox or ComboBox) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || !previewSessionOpen || HasKeyModifier()) return;
        e.Handled = true;
        await ClosePreviewAsync(restoreFocus: true);
    }

    private async void OnPreviewItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchItemViewModel selected } button)
            await ShowPreviewAsync(selected, button.FocusState == FocusState.Keyboard);
    }

    private async Task ShowPreviewAsync(SearchItemViewModel selected, bool keyboard)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is Control focused && !IsInsidePreview(focused))
            previewReturnFocus = new WeakReference<Control>(focused);
        previewSessionOpen = true;
        UpdatePreviewLayout();
        PreviewHost.IsPaneOpen = true;
        UpdateLayout();
        if (keyboard) SourcePreview.FocusCloseButton();
        try { await SourcePreview.ShowAsync(ViewModel, selected); }
        catch (Exception error) { ShowPreviewError("Preview failed: " + error.Message); }
    }

    private async Task ClosePreviewAsync(bool restoreFocus)
    {
        if (closingPreview || (!previewSessionOpen && !PreviewHost.IsPaneOpen)) return;
        closingPreview = true;
        previewSessionOpen = false;
        try
        {
            await SourcePreview.ClearAsync();
        }
        catch (Exception error) { ShowPreviewError("Preview cleanup failed: " + error.Message); }
        finally
        {
            PreviewHost.IsPaneOpen = false;
            closingPreview = false;
            var returnFocus = previewReturnFocus;
            previewReturnFocus = null;
            if (restoreFocus)
            {
                bool restored = returnFocus?.TryGetTarget(out var focused) == true && focused.XamlRoot == XamlRoot && focused.Focus(FocusState.Keyboard);
                if (!restored) await FocusSelectedResultAsync();
            }
        }
    }

    private bool IsInsidePreview(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, SourcePreview)) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async Task FocusSelectedResultAsync()
    {
        if (!IsLoaded || XamlRoot is not { } root) return;
        var selected = ViewModel.SelectedItem;
        var origin = FocusManager.GetFocusedElement(root);
        if (selected is not null) ResultsListView.ScrollIntoView(selected);
        // Allow the input event and container realization to settle before an explicit target focus.
        await Task.Delay(16);
        if (!IsLoaded || XamlRoot != root || previewSessionOpen || !ReferenceEquals(selected, ViewModel.SelectedItem)
            || !ReferenceEquals(origin, FocusManager.GetFocusedElement(root))) return;
        ResultsListView.UpdateLayout();
        if (selected is not null && ResultsListView.ContainerFromItem(selected) is Control { IsLoaded: true } container)
        {
            var result = await FocusManager.TryFocusAsync(container, FocusState.Keyboard);
            if (result.Succeeded) return;
        }
        if (ViewModel.Hits.Count > 0) await FocusManager.TryFocusAsync(ResultsListView, FocusState.Keyboard);
        else await FocusManager.TryFocusAsync(SearchBox, FocusState.Keyboard);
    }

    private void ShowPreviewError(string message)
    {
        SearchNotice.Message = message;
        SearchNotice.Severity = InfoBarSeverity.Warning;
        SearchNotice.IsOpen = true;
    }

    private void UpdatePreviewLayout()
    {
        if (PreviewHost is null || PreviewHost.ActualWidth <= 0) return;
        bool overlay = PreviewHost.ActualWidth < 820;
        PreviewHost.DisplayMode = overlay ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        SourcePreview.TabFocusNavigation = overlay ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Local;
        PreviewHost.OpenPaneLength = overlay ? PreviewHost.ActualWidth : Math.Clamp(PreviewHost.ActualWidth * .42, 340, 440);
    }

    private void OnPreviewHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewLayout();
    private async void OnPreviewCloseRequested(object? sender, EventArgs e) => await ClosePreviewAsync(restoreFocus: true);
    private async void OnPreviewPaneClosed(SplitView sender, object args) => await ClosePreviewAsync(restoreFocus: true);
    private async void OnPageUnloaded(object sender, RoutedEventArgs e) => await ClosePreviewAsync(restoreFocus: false);
    internal bool IsPreviewOpen => PreviewHost.IsPaneOpen;
    internal SourcePreviewPane PreviewControl => SourcePreview;

    public Visibility GetEmptyStateVisibility(int hitCount, bool isLoading) =>
        hitCount == 0 && !isLoading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetResultsVisibility(int hitCount) =>
        hitCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetSelectedVisibility(SearchItemViewModel? item) =>
        item is not null ? Visibility.Visible : Visibility.Collapsed;
}
