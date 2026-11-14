using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;
using WinSight.Core.Diagnostics;
using Windows.Storage.Streams;

namespace WinSight.App.Views;
public sealed partial class SourcePreviewPane : UserControl
{
    private CancellationTokenSource? request;
    private ISourcePreview? preview;
    private QuickSearchViewModel? viewModel;
    private SearchItemViewModel? item;
    private long generation;
    private int currentPage = 1;
    private int? totalPages;
    private int currentPixelWidth;
    private int currentPixelHeight;
    private readonly List<Rectangle> rectangles = [];
    public event EventHandler? CloseRequested;

    public SourcePreviewPane() { InitializeComponent(); }

    public Task ShowAsync(QuickSearchViewModel model, SearchItemViewModel selected) =>
        ShowPageCoreAsync(model, selected, overridePage: null);

    private async Task ShowPageCoreAsync(QuickSearchViewModel model, SearchItemViewModel selected, int? overridePage)
    {
        request?.Cancel(); request?.Dispose(); request = new(); var token = request.Token; long epoch = ++generation;
        viewModel = model; item = selected; var old = preview; preview = null;
        SourceTitle.Text = selected.FileName; SourceLocation.Text = selected.LocationDisplay;
        if (!overridePage.HasValue) Excerpt.Text = "";
        Notice.Text = UiText.PreviewLoading;
        ResetRaster();
        LoadingRing.Visibility = Visibility.Visible; LoadingRing.IsActive = true;
        ISourcePreview? loaded = null;
        try
        {
            if (old is not null) await old.DisposeAsync();
            loaded = overridePage.HasValue
                ? await model.PreviewPageAsync(selected, overridePage.Value, token)
                : await model.PreviewAsync(selected, token);
            if (epoch != generation) return;

            currentPage = loaded.PageNumber;
            totalPages = loaded.TotalPages;
            currentPixelWidth = loaded.PixelWidth;
            currentPixelHeight = loaded.PixelHeight;

            if (loaded.SourceCurrent && loaded.RasterPath is { } path)
            {
                var bitmap = new BitmapImage();
                await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using IRandomAccessStream stream = file.AsRandomAccessStream();
                    await bitmap.SetSourceAsync(stream);
                }
                if (epoch != generation) return;
                PixelFrame.Width = loaded.PixelWidth; PixelFrame.Height = loaded.PixelHeight;
                Overlay.Width = loaded.PixelWidth; Overlay.Height = loaded.PixelHeight;
                RasterImage.Source = bitmap;
                RasterArea.Visibility = Visibility.Visible;
                PreviewToolbar.Visibility = Visibility.Visible;
            }
            UpdatePageStepper();
            DrawRegions(loaded, showRegions: !overridePage.HasValue || overridePage.Value == (selected.Hit.Source?.Location.Page ?? 1));
            Notice.Text = loaded.Notice;
            if (!string.IsNullOrEmpty(loaded.Excerpt) || !overridePage.HasValue) Excerpt.Text = loaded.Excerpt;
            ExactOpen.IsEnabled = loaded.SourceCurrent && loaded.CanNavigate;
            preview = loaded; loaded = null;
            UpdateLayout();
            FitRasterToViewport();
            ApplyFitZoom();
            ShowRasterInViewport();
            PlayRasterHandoff();
        }
        catch (OperationCanceledException) { if (epoch == generation) { ResetRaster(); Notice.Text = UiText.PreviewCanceled; } }
        catch (Exception e) { if (epoch == generation) { ResetRaster(); Notice.Text = UiText.PreviewUnavailable(e.Message); } }
        finally
        {
            try { if (loaded is not null) await loaded.DisposeAsync(); }
            finally { if (epoch == generation) { LoadingRing.IsActive = false; LoadingRing.Visibility = Visibility.Collapsed; } }
        }
    }

    private void UpdatePageStepper()
    {
        if (totalPages is > 1)
        {
            PageStepperPanel.Visibility = Visibility.Visible;
            PageIndicatorText.Text = $"{currentPage} / {totalPages.Value}";
            PrevPageBtn.IsEnabled = currentPage > 1;
            NextPageBtn.IsEnabled = currentPage < totalPages.Value;
        }
        else
        {
            PageStepperPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnPrevPageClick(object sender, RoutedEventArgs e)
    {
        if (viewModel is not null && item is not null && currentPage > 1)
            await ShowPageCoreAsync(viewModel, item, currentPage - 1);
    }

    private async void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        if (viewModel is not null && item is not null && totalPages is { } max && currentPage < max)
            await ShowPageCoreAsync(viewModel, item, currentPage + 1);
    }

    private void ApplyFitZoom()
    {
        if (RasterZoomScroll is null || currentPixelWidth <= 0) return;
        double vp = RasterZoomScroll.ViewportWidth;
        if (vp <= 0) vp = RasterArea.ActualWidth - 8;
        if (vp > 40)
        {
            float factor = Math.Clamp((float)(vp / currentPixelWidth), RasterZoomScroll.MinZoomFactor, 1.5f);
            RasterZoomScroll.ChangeView(0, 0, factor, true);
        }
    }

    private void OnZoomFitClick(object sender, RoutedEventArgs e) => ApplyFitZoom();

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        if (RasterZoomScroll is null) return;
        float next = Math.Min(RasterZoomScroll.MaxZoomFactor, RasterZoomScroll.ZoomFactor * 1.25f);
        RasterZoomScroll.ChangeView(null, null, next);
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        if (RasterZoomScroll is null) return;
        float next = Math.Max(RasterZoomScroll.MinZoomFactor, RasterZoomScroll.ZoomFactor / 1.25f);
        RasterZoomScroll.ChangeView(null, null, next);
    }

    private void PlayRasterHandoff()
    {
        try { Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView().GetAnimation("pageRaster")?.TryStart(RasterImage); }
        catch (Exception error) { AppLog.Warn("raster handoff skipped", error); }
    }

    private void DrawRegions(ISourcePreview value, bool showRegions)
    {
        Overlay.Children.Clear(); rectangles.Clear(); OcrLines.Items.Clear();
        var regions = showRegions && value.SourceCurrent && value.RasterPath is not null ? value.Source?.Regions ?? [] : [];
        for (int i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            int lineIndex = i;
            var box = PreviewGeometry.Project(region, value.PixelWidth, value.PixelHeight);
            var rect = new Rectangle
            {
                Width = box.Width,
                Height = box.Height,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                StrokeThickness = 4,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 0, 100, 220))
            };
            if (!string.IsNullOrWhiteSpace(region.Text))
                ToolTipService.SetToolTip(rect, $"点击复制：{region.Text}");
            rect.Tapped += (_, _) =>
            {
                if (lineIndex < OcrLines.Items.Count) OcrLines.SelectedIndex = lineIndex;
                if (!string.IsNullOrWhiteSpace(region.Text))
                {
                    var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    pkg.SetText(region.Text);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                    BoxStatus.Text = $"已复制第 {region.Line + 1} 行文本到剪贴板";
                }
            };
            Canvas.SetLeft(rect, box.X); Canvas.SetTop(rect, box.Y);
            Overlay.Children.Add(rect); rectangles.Add(rect);
            OcrLines.Items.Add(new ListBoxItem { Content = $"{region.Line + 1}. {region.Text ?? UiText.OcrLineFallback}", Tag = region.Line });
        }
        OcrLines.Visibility = regions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BoxStatus.Text = regions.Count == 0 ? UiText.NoOcrBoxes : $"已索引 {regions.Count} 个文字行位置；点击图上蓝框可直接复制文字。";
        Overlay.Visibility = ShowBoxes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLineSelected(object sender, SelectionChangedEventArgs e)
    {
        var blue = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        var amber = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 242, 169, 0));
        for (int n = 0; n < rectangles.Count; n++)
        {
            bool sel = n == OcrLines.SelectedIndex;
            rectangles[n].StrokeThickness = sel ? 8 : 4;
            rectangles[n].Stroke = sel ? amber : blue;
        }
        if (OcrLines.SelectedIndex >= 0 && OcrLines.SelectedIndex < rectangles.Count && RasterZoomScroll is not null)
        {
            var r = rectangles[OcrLines.SelectedIndex];
            float z = RasterZoomScroll.ZoomFactor;
            double targetY = Math.Max(0, Canvas.GetTop(r) * z - RasterZoomScroll.ViewportHeight * 0.35);
            double targetX = Math.Max(0, Canvas.GetLeft(r) * z - RasterZoomScroll.ViewportWidth * 0.25);
            RasterZoomScroll.ChangeView(targetX, targetY, null);
        }
    }

    private void OnBoxesChanged(object sender, RoutedEventArgs e)
    {
        if (Overlay is not null) Overlay.Visibility = ShowBoxes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnExactOpen(object sender, RoutedEventArgs e)
    {
        if (viewModel is null || item is null || preview?.SourceCurrent != true || preview.CanNavigate != true) return;
        ExactOpen.IsEnabled = false; long epoch = generation;
        try { var result = await viewModel.NavigateAsync(item, request?.Token ?? default); if (epoch == generation) Notice.Text = result.Message; }
        catch (Exception error) { if (epoch == generation) Notice.Text = UiText.PreciseOpenFailed(error.Message); }
        finally { if (epoch == generation) ExactOpen.IsEnabled = preview?.SourceCurrent == true && preview.CanNavigate; }
    }

    private async void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (viewModel is not null && item is not null) await viewModel.OpenHitAsync(item);
    }

    private async void OnClose(object sender, RoutedEventArgs e)
    {
        try { await ClearAsync(); CloseRequested?.Invoke(this, EventArgs.Empty); }
        catch (Exception error) { Notice.Text = UiText.PreviewCleanupFailed(error.Message); }
    }

    public async Task ClearAsync()
    {
        ++generation; request?.Cancel(); request?.Dispose(); request = null; var old = preview; preview = null;
        ResetRaster(); LoadingRing.IsActive = false; LoadingRing.Visibility = Visibility.Collapsed;
        viewModel = null; item = null;
        if (old is not null) await old.DisposeAsync();
    }

    private void ResetRaster()
    {
        ExactOpen.IsEnabled = false; RasterImage.Source = null; RasterArea.Visibility = Visibility.Collapsed;
        PreviewToolbar.Visibility = Visibility.Collapsed; PageStepperPanel.Visibility = Visibility.Collapsed;
        Overlay.Children.Clear(); rectangles.Clear(); OcrLines.Items.Clear(); OcrLines.Visibility = Visibility.Collapsed; BoxStatus.Text = "";
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitRasterToViewport();
        if (RasterArea.Visibility == Visibility.Visible) ApplyFitZoom();
    }

    private void FitRasterToViewport()
    {
        if (RasterArea is null || PreviewScroll is null) return;
        if (PreviewScroll.ActualHeight > 0)
        {
            double height = Math.Clamp(PreviewScroll.ActualHeight * 0.72, 320, 760);
            if (Math.Abs(RasterArea.Height - height) > .5) RasterArea.Height = height;
        }
    }

    internal void FocusCloseButton() => ClosePreview.Focus(FocusState.Keyboard);

    internal void ShowRasterInViewport()
    {
        if (RasterArea.Visibility != Visibility.Visible) return;
        UpdateLayout();
        double offset = PreviewScroll.VerticalOffset + RasterArea.TransformToVisual(PreviewScroll).TransformPoint(new Windows.Foundation.Point()).Y;
        PreviewScroll.ChangeView(null, Math.Clamp(offset, 0, PreviewScroll.ScrollableHeight), null, true);
    }

    internal bool HasActivePreview => preview is not null;
    internal int OverlayCount => rectangles.Count;
    internal IReadOnlyList<Rectangle> OverlayRectangles => rectangles;
    internal FrameworkElement RasterFrame => PixelFrame;
}
