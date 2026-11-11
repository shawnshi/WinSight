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
    private readonly SearchLayoutStore layout;
    private const double CompactWidth = 700;
    private bool compact, showingDetail;

    /// <summary>Hosts that drive the page headlessly (tests) turn the one-time tip off.</summary>
    internal bool FirstRunTipEnabled { get; set; } = true;

    public SearchPage(IDesktopBackend backend, AppSettings? settings = null, string? dataDirectory = null)
    {
        var stateDirectory = dataDirectory ?? CommandLineParser.DefaultDataDirectory;
        layout = new SearchLayoutStore(SearchLayoutStore.FileIn(stateDirectory));
        ViewModel = new QuickSearchViewModel(backend, history: new SearchHistoryStore(SearchHistoryStore.FileIn(stateDirectory)));
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickSearchViewModel.IsLoading) && !ViewModel.IsLoading)
            DispatcherQueue?.TryEnqueue(AnnounceStatus);
        if (e.PropertyName is nameof(QuickSearchViewModel.SelectedItem) or nameof(QuickSearchViewModel.SelectedIndex))
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                Bindings.Update();
                UpdateHeroStage(ViewModel.SelectedItem);
            });
        }
    }

    /// <summary>Tells Narrator how a finished search ended; without it a screen-reader user hears nothing after typing.</summary>
    private void AnnounceStatus()
    {
        if (StatusLine is null || string.IsNullOrEmpty(ViewModel.StatusText)) return;
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(StatusLine)
            ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(StatusLine);
        peer?.RaiseNotificationEvent(
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionCompleted,
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent,
            ViewModel.StatusText, "WinSightSearchStatus");
    }

    private void OnResultsContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not SearchItemViewModel item) return;
        if (args.InRecycleQueue) { item.CancelThumbnail(); return; }
        _ = item.EnsureThumbnailAsync();
    }

    private void OnFocusSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FocusSearchBox();
    }

    internal void FocusSearchBox()
    {
        SearchBox.Focus(FocusState.Keyboard);
        InnerSearchTextBox()?.SelectAll();
    }

    private async void OnCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.SelectedItem is { } item) await ViewModel.CopyHitAsync(item);
    }

    private void OnRevealAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.SelectedItem is { } item) _ = ViewModel.RevealHitAsync(item);
    }

    private void OnResultsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Bindings.Update();
        UpdateHeroStage(ViewModel.SelectedItem);
    }

    private static readonly Windows.UI.ViewManagement.UISettings SystemUi = new();

    /// <summary>Short deceleration fade so a new selection settles in instead of snapping; skipped when Windows animations are off.</summary>
    private void AnimateStageIn()
    {
        try
        {
            if (!SystemUi.AnimationsEnabled || DetailScroll is null) return;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(DetailScroll);
            var compositor = visual.Compositor;
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0.15f);
            fade.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0f, 0f), new System.Numerics.Vector2(0f, 1f)));
            fade.Duration = TimeSpan.FromMilliseconds(180);
            visual.StartAnimation("Opacity", fade);
        }
        catch (Exception error) { AppLog.Warn("stage animation skipped", error); }
    }

    private async void UpdateHeroStage(SearchItemViewModel? selected)
    {
        AnimateStageIn();
        heroPreviewCts?.Cancel();
        heroPreviewCts?.Dispose();
        heroPreviewCts = new CancellationTokenSource();
        var ct = heroPreviewCts.Token;

        ApplyQueryHighlights(CanvasExcerptBlock, ViewModel.Query);
        ApplyQueryHighlights(CodeExcerptBlock, ViewModel.Query);

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
        if (previewSessionOpen) return; // stage is hidden behind the drawer; reload happens on close

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
            ISourcePreview? preview = null;
            try
            {
                // Rendering a page spawns a media process and holds the shared preview gate, so wait for the selection to settle
                // instead of rendering every row an arrow-key scroll passes over.
                await Task.Delay(250, ct);
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
            finally
            {
                // The preview holds a pin on the source file and a rendered artifact; the decoded bitmap no longer needs either.
                if (preview is not null)
                {
                    try { await preview.DisposeAsync(); }
                    catch (Exception ex) { AppLog.Warn("Hero preview lease release failed", ex); }
                }
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
        // High-contrast themes replace the brand amber with the system selection pair.
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
            && Application.Current.Resources.TryGetValue("SystemColorHighlightColorBrush", out var hcBack) && hcBack is Microsoft.UI.Xaml.Media.Brush hcBackBrush
            && Application.Current.Resources.TryGetValue("SystemColorHighlightTextColorBrush", out var hcFore) && hcFore is Microsoft.UI.Xaml.Media.Brush hcForeBrush)
        {
            highlighter.Background = hcBackBrush;
            highlighter.Foreground = hcForeBrush;
        }

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

    private SearchItemViewModel? CommandTarget(ExecuteRequestedEventArgs args) =>
        args.Parameter as SearchItemViewModel ?? ViewModel.SelectedItem;

    private void OnOpenCommand(XamlUICommand sender, ExecuteRequestedEventArgs args)
    {
        if (CommandTarget(args) is { } item) _ = ViewModel.OpenHitAsync(item);
    }

    private void OnRevealCommand(XamlUICommand sender, ExecuteRequestedEventArgs args)
    {
        if (CommandTarget(args) is { } item) _ = ViewModel.RevealHitAsync(item);
    }

    private async void OnPreviewCommand(XamlUICommand sender, ExecuteRequestedEventArgs args)
    {
        if (CommandTarget(args) is { } item) await ShowPreviewAsync(item, keyboard: false);
    }

    private async void OnCopyPathCommand(XamlUICommand sender, ExecuteRequestedEventArgs args)
    {
        if (CommandTarget(args) is { } item) await ViewModel.CopyHitAsync(item);
    }

    private void OnCopyExcerptCommand(XamlUICommand sender, ExecuteRequestedEventArgs args)
    {
        if (CommandTarget(args) is not { } item) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(!string.IsNullOrWhiteSpace(item.Excerpt) ? item.Excerpt : item.Path);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    public void SetInitialQuery(string query, string? kind = null)
    {
        if (kind is not null)
        {
            SelectKindBar(kind.ToLowerInvariant() switch
            {
                "doc" or "document" => "doc",
                "media" => "video",
                var other => other
            });
        }
        ViewModel.Query = query;
    }

    private void SelectKindBar(string tag)
    {
        var item = KindSelector.Items.OfType<SelectorBarItem>().FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.Ordinal))
            ?? KindSelector.Items.OfType<SelectorBarItem>().First();
        KindSelector.SelectedItem = item;
    }

    private void OnKindSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag)
            ViewModel.SelectedKind = tag == "all" ? null : tag;
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            sender.ItemsSource = ViewModel.History.Suggest(sender.Text, 6);
        else
            sender.IsSuggestionListOpen = false; // a programmatic or chosen text change must not leave a stale dropdown owning the arrow keys
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string text) sender.Text = text;
    }

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string chosen) sender.Text = chosen;
        ViewModel.RecordCurrentQuery();
    }

    /// <summary>An empty, focused search box offers recent queries so repeat searches are two keystrokes.</summary>
    private void OnSearchBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(SearchBox.Text) || ViewModel.History.Items.Count == 0) return;
        SearchBox.ItemsSource = ViewModel.History.Suggest(string.Empty, 6);
        SearchBox.IsSuggestionListOpen = true;
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        ViewModel.History.Clear();
        SearchBox.ItemsSource = null;
    }

    public Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private TextBox? InnerSearchTextBox()
    {
        static TextBox? Find(DependencyObject root)
        {
            for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is TextBox box) return box;
                if (Find(child) is { } found) return found;
            }
            return null;
        }
        return Find(SearchBox);
    }

    private async void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // While the suggestion dropdown is open, arrows and Enter belong to it.
        if (SearchBox.IsSuggestionListOpen && e.Key is VirtualKey.Down or VirtualKey.Up or VirtualKey.Enter) return;
        if (e.Key == VirtualKey.Down && ViewModel.Hits.Count > 0)
        {
            e.Handled = true;
            ViewModel.SelectedIndex = 0;
            try { await FocusSelectedResultAsync(); }
            catch (Exception error) { ShowPreviewError(UiText.FocusFailed(error.Message)); }
        }
        else if (e.Key == VirtualKey.Enter)
        {
            ViewModel.RecordCurrentQuery();
            _ = ViewModel.OpenCurrentAsync();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && !string.IsNullOrEmpty(SearchBox.Text) && !previewSessionOpen)
        {
            ViewModel.Query = string.Empty;
            e.Handled = true;
        }
    }

    private void OnResultItemClick(object sender, ItemClickEventArgs e)
    {
        if (compact) { showingDetail = true; UpdateColumns(); }
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

    // ---- adaptive layout: draggable list width, two-level navigation when narrow ----

    private void OnResultsCardSizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns();

    /// <summary>The shortcut legend is a convenience; below this width it would clip, so it steps aside.</summary>
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var show = e.NewSize.Width >= 820;
        FooterHintsHost.MaxWidth = show ? double.PositiveInfinity : 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(FooterHintsHost,
            show ? Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content : Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    private void UpdateColumns()
    {
        if (ResultsCard is null || ResultsCard.ActualWidth <= 0) return;
        var width = ResultsCard.ActualWidth;
        compact = width < CompactWidth;
        if (compact)
        {
            ListColumn.MinWidth = 0;
            SplitterColumn.Width = new GridLength(0);
            ListSplitter.Visibility = Visibility.Collapsed;
            ResultsListView.Visibility = showingDetail ? Visibility.Collapsed : Visibility.Visible;
            DetailScroll.Visibility = showingDetail ? Visibility.Visible : Visibility.Collapsed;
            BackToListButton.Visibility = showingDetail ? Visibility.Visible : Visibility.Collapsed;
            ListColumn.Width = showingDetail ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            StageColumn.Width = showingDetail ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
        else
        {
            showingDetail = false;
            ListColumn.MinWidth = SearchLayoutStore.MinListWidth;
            SplitterColumn.Width = new GridLength(9);
            ListSplitter.Visibility = Visibility.Visible;
            ResultsListView.Visibility = Visibility.Visible;
            DetailScroll.Visibility = Visibility.Visible;
            BackToListButton.Visibility = Visibility.Collapsed;
            if (layout.ListWidth is { } saved)
            {
                ListColumn.Width = new GridLength(SearchLayoutStore.ClampListWidth(saved, width));
                StageColumn.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                ListColumn.Width = new GridLength(3.8, GridUnitType.Star);
                StageColumn.Width = new GridLength(6.2, GridUnitType.Star);
            }
        }
        // Icon-only actions when the stage is narrow; labels once there is room for them.
        var stage = compact ? width : width - Math.Max(ListColumn.ActualWidth, SearchLayoutStore.MinListWidth);
        if (ActionBar is not null)
            ActionBar.DefaultLabelPosition = stage >= 560 ? CommandBarDefaultLabelPosition.Right : CommandBarDefaultLabelPosition.Collapsed;
    }

    private void OnSplitterDragged(object? sender, double delta)
    {
        if (compact) return;
        var basis = ListColumn.Width.IsAbsolute ? ListColumn.Width.Value : ListColumn.ActualWidth;
        ListColumn.Width = new GridLength(SearchLayoutStore.ClampListWidth(basis + delta, ResultsCard.ActualWidth));
        StageColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void OnSplitterDragCompleted(object? sender, EventArgs e)
    {
        if (!compact && ListColumn.Width.IsAbsolute) layout.ListWidth = ListColumn.Width.Value;
        UpdateColumns();
    }

    private void OnSplitterReset(object? sender, EventArgs e)
    {
        layout.ListWidth = null;
        UpdateColumns();
    }

    private async void OnBackToListClick(object sender, RoutedEventArgs e) => await ReturnToListAsync();

    private async Task ReturnToListAsync()
    {
        showingDetail = false;
        UpdateColumns();
        await FocusSelectedResultAsync();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        UpdateColumns();
        if (!FirstRunTipEnabled || layout.TipSeen) return;
        await Task.Delay(900); // let the window settle and the user see the page first
        if (IsLoaded && !layout.TipSeen) FirstRunTip.IsOpen = true;
    }

    private void OnFirstRunTipClosed(TeachingTip sender, TeachingTipClosedEventArgs args) => layout.TipSeen = true;

    private void OnGroupToggleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SearchItemViewModel item) ViewModel.ToggleGroup(item);
    }

    private async void OnResultsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || HasKeyModifier() || IsInteractiveChild(e.OriginalSource as DependencyObject)) return;
        if (e.Key == VirtualKey.Right && ViewModel.SelectedItem is { IsSecondary: false, GroupExpanded: false, MoreCount: > 0 } closed)
        {
            e.Handled = true;
            ViewModel.ToggleGroup(closed);
            await FocusSelectedResultAsync();
            return;
        }
        if (e.Key == VirtualKey.Left && ViewModel.SelectedItem is { } open && (open.IsSecondary || open.GroupExpanded))
        {
            e.Handled = true;
            ViewModel.CollapseGroupOf(open);
            await FocusSelectedResultAsync();
            return;
        }
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
        if (e.Key == VirtualKey.Escape && compact && showingDetail && !previewSessionOpen && !HasKeyModifier())
        {
            e.Handled = true;
            await ReturnToListAsync();
            return;
        }
        if (e.Key != VirtualKey.Escape || !previewSessionOpen || HasKeyModifier()) return;
        e.Handled = true;
        await ClosePreviewAsync(restoreFocus: true);
    }

    private async void OnPreviewItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchItemViewModel selected } button)
            await ShowPreviewAsync(selected, button.FocusState == FocusState.Keyboard);
    }

    /// <summary>Lets the stage page image fly into the drawer so opening the preview reads as one object growing, not a swap.</summary>
    private void PrepareRasterHandoff()
    {
        try
        {
            if (!SystemUi.AnimationsEnabled || DocumentRasterImage is not { Visibility: Visibility.Visible, Source: not null }) return;
            Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("pageRaster", DocumentRasterImage);
        }
        catch (Exception error) { AppLog.Warn("raster handoff skipped", error); }
    }

    private async Task ShowPreviewAsync(SearchItemViewModel selected, bool keyboard)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is Control focused && !IsInsidePreview(focused))
            previewReturnFocus = new WeakReference<Control>(focused);
        previewSessionOpen = true;
        UpdatePreviewLayout();
        PrepareRasterHandoff();
        PreviewHost.IsPaneOpen = true;
        HeroStage.Visibility = Visibility.Collapsed; // the drawer shows the same page, so the stage steps aside instead of repeating it
        UpdateLayout();
        if (keyboard) SourcePreview.FocusCloseButton();
        try { await SourcePreview.ShowAsync(ViewModel, selected); }
        catch (Exception error) { ShowPreviewError(UiText.PreviewFailed(error.Message)); }
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
        catch (Exception error) { ShowPreviewError(UiText.PreviewCleanupFailed(error.Message)); }
        finally
        {
            PreviewHost.IsPaneOpen = false;
            HeroStage.Visibility = Visibility.Visible;
            UpdateHeroStage(ViewModel.SelectedItem);
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

    public Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public string NoResultsTitle(string query) =>
        string.IsNullOrWhiteSpace(query) ? "没有结果" : $"没有找到与“{query.Trim()}”相关的结果";

    private void OnExampleChipClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string text) SetInitialQuery(text);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnClearFilterClick(object sender, RoutedEventArgs e)
    {
        SelectKindBar("all");
        ViewModel.SelectedKind = null;
        ViewModel.TriggerSearch();
    }

    private void OnRetrySearchClick(object sender, RoutedEventArgs e) => ViewModel.TriggerSearch();

    public Visibility GetResultsVisibility(int hitCount) =>
        hitCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetSelectedVisibility(SearchItemViewModel? item) =>
        item is not null ? Visibility.Visible : Visibility.Collapsed;
}
