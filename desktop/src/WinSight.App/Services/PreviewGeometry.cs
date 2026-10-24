using WinSight.Core;

namespace WinSight.App.Services;
public readonly record struct PreviewRectangle(double X,double Y,double Width,double Height);
public static class PreviewGeometry
{
    public static PreviewRectangle Project(OcrTextRegion region,int width,int height)
    {
        if(width<1||height<1||!double.IsFinite(region.Left)||!double.IsFinite(region.Top)||!double.IsFinite(region.Width)||!double.IsFinite(region.Height)
            ||region.Left<0||region.Top<0||region.Width<=0||region.Height<=0||region.Left+region.Width>1.000001||region.Top+region.Height>1.000001)
            throw new ArgumentException("Invalid normalized OCR rectangle.");
        return new(region.Left*width,region.Top*height,region.Width*width,region.Height*height);
    }
}
