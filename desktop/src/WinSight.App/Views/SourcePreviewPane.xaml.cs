using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;
using Windows.Storage.Streams;

namespace WinSight.App.Views;
public sealed partial class SourcePreviewPane : UserControl
{
    private CancellationTokenSource? request;
    private ISourcePreview? preview;
    private QuickSearchViewModel? viewModel;
    private SearchItemViewModel? item;
    private long generation;
    private readonly List<Rectangle> rectangles=[];
    public event EventHandler? CloseRequested;
    public SourcePreviewPane(){InitializeComponent();}
    public async Task ShowAsync(QuickSearchViewModel model,SearchItemViewModel selected)
    {
        request?.Cancel();request?.Dispose();request=new();var token=request.Token;long epoch=++generation;
        viewModel=model;item=selected;var old=preview;preview=null;
        SourceTitle.Text=selected.FileName;SourceLocation.Text=selected.LocationDisplay;Excerpt.Text="";Notice.Text="Loading current source...";
        ResetRaster();
        LoadingRing.Visibility=Visibility.Visible;LoadingRing.IsActive=true;
        ISourcePreview? loaded=null;
        try
        {
            if(old is not null)await old.DisposeAsync();
            loaded=await model.PreviewAsync(selected,token);
            if(epoch!=generation)return;
            if(loaded.SourceCurrent&&loaded.RasterPath is {} path)
            {
                var bitmap=new BitmapImage();
                // Do not cancel SetSourceAsync: a canceled wait is not proof the native decoder stopped using its stream.
                await using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                {using IRandomAccessStream stream=file.AsRandomAccessStream();await bitmap.SetSourceAsync(stream);}
                if(epoch!=generation)return;
                PixelFrame.Width=loaded.PixelWidth;PixelFrame.Height=loaded.PixelHeight;Overlay.Width=loaded.PixelWidth;Overlay.Height=loaded.PixelHeight;
                RasterImage.Source=bitmap;RasterArea.Visibility=Visibility.Visible;
            }
            DrawRegions(loaded);
            Notice.Text=loaded.Notice;Excerpt.Text=loaded.Excerpt;ExactOpen.IsEnabled=loaded.SourceCurrent&&loaded.CanNavigate;
            preview=loaded;loaded=null;
            UpdateLayout();FitRasterToViewport();ShowRasterInViewport();
        }
        catch(OperationCanceledException){if(epoch==generation){ResetRaster();Notice.Text="Preview canceled.";}}
        catch(Exception e){if(epoch==generation){ResetRaster();Notice.Text="Preview unavailable: "+e.Message;}}
        finally
        {
            try{if(loaded is not null)await loaded.DisposeAsync();}
            finally{if(epoch==generation){LoadingRing.IsActive=false;LoadingRing.Visibility=Visibility.Collapsed;}}
        }
    }
    private void DrawRegions(ISourcePreview value)
    {
        Overlay.Children.Clear();rectangles.Clear();OcrLines.Items.Clear();
        var regions=value.SourceCurrent&&value.RasterPath is not null?value.Source?.Regions??[]:[];
        foreach(var region in regions)
        {
            var box=PreviewGeometry.Project(region,value.PixelWidth,value.PixelHeight);
            var rect=new Rectangle{Width=box.Width,Height=box.Height,Stroke=new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),StrokeThickness=4,Fill=new SolidColorBrush(Windows.UI.Color.FromArgb(24,0,100,220))};
            Canvas.SetLeft(rect,box.X);Canvas.SetTop(rect,box.Y);Overlay.Children.Add(rect);rectangles.Add(rect);
            OcrLines.Items.Add(new ListBoxItem{Content=$"{region.Line+1}. {region.Text??"Indexed OCR line"}",Tag=region.Line});
        }
        OcrLines.Visibility=regions.Count==0?Visibility.Collapsed:Visibility.Visible;
        BoxStatus.Text=regions.Count==0?"No indexed OCR line boxes for this source unit.":$"{regions.Count} indexed text-line boxes. Select a line to highlight its rectangle.";
        Overlay.Visibility=ShowBoxes.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
    }
    private void OnLineSelected(object sender,SelectionChangedEventArgs e)
    {for(int n=0;n<rectangles.Count;n++)rectangles[n].StrokeThickness=n==OcrLines.SelectedIndex?9:4;}
    private void OnBoxesChanged(object sender,RoutedEventArgs e){if(Overlay is not null)Overlay.Visibility=ShowBoxes.IsChecked==true?Visibility.Visible:Visibility.Collapsed;}
    private async void OnExactOpen(object sender,RoutedEventArgs e)
    {
        if(viewModel is null||item is null||preview?.SourceCurrent!=true||preview.CanNavigate!=true)return;ExactOpen.IsEnabled=false;long epoch=generation;
        try{var result=await viewModel.NavigateAsync(item,request?.Token??default);if(epoch==generation)Notice.Text=result.Message;}
        catch(Exception error){if(epoch==generation)Notice.Text="Precise open failed: "+error.Message;}
        finally{if(epoch==generation)ExactOpen.IsEnabled=preview?.SourceCurrent==true&&preview.CanNavigate;}
    }
    private async void OnOpenFile(object sender,RoutedEventArgs e){if(viewModel is not null&&item is not null)await viewModel.OpenHitAsync(item);}
    private async void OnClose(object sender,RoutedEventArgs e)
    {
        try{await ClearAsync();CloseRequested?.Invoke(this,EventArgs.Empty);}
        catch(Exception error){Notice.Text="Preview cleanup failed: "+error.Message;}
    }
    public async Task ClearAsync()
    {
        ++generation;request?.Cancel();request?.Dispose();request=null;var old=preview;preview=null;
        ResetRaster();LoadingRing.IsActive=false;LoadingRing.Visibility=Visibility.Collapsed;
        viewModel=null;item=null;
        if(old is not null)await old.DisposeAsync();
    }
    private void ResetRaster()
    {
        ExactOpen.IsEnabled=false;RasterImage.Source=null;RasterArea.Visibility=Visibility.Collapsed;
        Overlay.Children.Clear();rectangles.Clear();OcrLines.Items.Clear();OcrLines.Visibility=Visibility.Collapsed;BoxStatus.Text="";
    }
    private void OnViewportSizeChanged(object sender,SizeChangedEventArgs e)=>FitRasterToViewport();
    private void FitRasterToViewport()
    {
        if(RasterArea is null||PreviewScroll is null)return;
        if(PreviewScroll.ActualHeight>0)
        {
            double height=Math.Clamp(PreviewScroll.ActualHeight-16,96,240);
            if(Math.Abs(RasterArea.Height-height)>.5)RasterArea.Height=height;
        }
    }
    internal void FocusCloseButton()=>ClosePreview.Focus(FocusState.Keyboard);
    internal void ShowRasterInViewport()
    {
        if(RasterArea.Visibility!=Visibility.Visible)return;
        UpdateLayout();
        double offset=PreviewScroll.VerticalOffset+RasterArea.TransformToVisual(PreviewScroll).TransformPoint(new Windows.Foundation.Point()).Y;
        PreviewScroll.ChangeView(null,Math.Clamp(offset,0,PreviewScroll.ScrollableHeight),null,true);
    }
    internal FrameworkElement ScrollViewport=>PreviewScroll;
    internal bool HasActivePreview=>preview is not null;
    internal int OverlayCount=>rectangles.Count;
    internal IReadOnlyList<Rectangle> OverlayRectangles=>rectangles;
    internal FrameworkElement RasterFrame=>PixelFrame;
}
