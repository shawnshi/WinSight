using System.Text.Json;

namespace WinSight.InferenceHost;

internal sealed record RasterPreviewResult(int Protocol,string InputPath,int PixelWidth,int PixelHeight,string RenderPolicy,string Artifact);
internal static partial class NativeMediaCommand
{
    internal static async Task<RasterPreviewResult> RenderRasterPreviewAsync(string requestPath,CancellationToken token=default)
    {
        using var request=OpenPinnedFile(ValidateLocalPath(requestPath,GetDriveType),MaxRequestBytes);
        var bytes=new byte[checked((int)request.Length)];await request.ReadExactlyAsync(bytes,token);
        using var json=JsonDocument.Parse(bytes);var root=json.RootElement;if(root.ValueKind!=JsonValueKind.Object)throw new JsonException("Image preview request must be an object.");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in root.EnumerateObject())if(p.Name is not ("protocol" or "inputPath" or "outputDirectory" or "maxEdge")||!names.Add(p.Name))throw new JsonException("Unknown/duplicate raster preview member.");
        if(names.Count!=4||root.GetProperty("protocol").GetInt32()!=1)throw new JsonException("Invalid raster preview protocol.");
        int edge=root.GetProperty("maxEdge").GetInt32();if(edge is <256 or >1600)throw new ArgumentOutOfRangeException(nameof(edge));
        string path=ValidateLocalPath(root.GetProperty("inputPath").GetString()!,GetDriveType),output=ValidateLocalPath(root.GetProperty("outputDirectory").GetString()!,GetDriveType);
        using var pin=OpenPinnedFile(path,MaxInputBytes);var rgb=MediaWic.Read(path);token.ThrowIfCancellationRequested();
        double scale=Math.Min(1,edge/(double)Math.Max(rgb.Width,rgb.Height));int width=Math.Max(1,(int)Math.Round(rgb.Width*scale)),height=Math.Max(1,(int)Math.Round(rgb.Height*scale));
        var resized=MediaBicubic.Resize(rgb,width,height);await CreateOutputAsync(output,token);
        WriteCanonicalRgb(resized,Path.Combine(output,"page.png"),token);token.ThrowIfCancellationRequested();
        return new(1,path,width,height,"wic-exif-rgb-preview-v1","page.png");
    }
}
