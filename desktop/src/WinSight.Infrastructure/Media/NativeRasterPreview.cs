using System.Text.Json;

namespace WinSight.Infrastructure.Media;
public sealed record NativeRasterPreviewRequest(string SourcePath,int MaxEdge=1600)
{
    internal void Validate(){if(MaxEdge is <256 or >1600)throw new ArgumentOutOfRangeException(nameof(MaxEdge));}
}
public sealed class NativeRasterPreviewLease : IAsyncDisposable
{
    private readonly MediaTaskFiles files;private readonly object gate=new();private Task? disposal;
    public string ArtifactPath=>files.ArtifactPath;
    public int PixelWidth{get;}public int PixelHeight{get;}
    private NativeRasterPreviewLease(MediaTaskFiles files,int width,int height)=>(this.files,PixelWidth,PixelHeight)=(files,width,height);
    internal static NativeRasterPreviewLease Parse(string payload,NativeRasterPreviewRequest request,MediaTaskFiles files,CancellationToken token)
    {
        using var doc=JsonDocument.Parse(payload,new JsonDocumentOptions{MaxDepth=8});var root=doc.RootElement;if(root.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Invalid raster preview result.");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in root.EnumerateObject())if(p.Name is not ("protocol" or "inputPath" or "pixelWidth" or "pixelHeight" or "renderPolicy" or "artifact")||!names.Add(p.Name))throw new InvalidDataException("Unknown/duplicate raster result member.");
        if(names.Count!=6||root.GetProperty("protocol").GetInt32()!=1||root.GetProperty("renderPolicy").GetString()!="wic-exif-rgb-preview-v1"||root.GetProperty("artifact").GetString()!="page.png"
            ||!string.Equals(root.GetProperty("inputPath").GetString(),request.SourcePath,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Raster preview identity mismatch.");
        int width=root.GetProperty("pixelWidth").GetInt32(),height=root.GetProperty("pixelHeight").GetInt32();if(width<1||height<1||width>request.MaxEdge||height>request.MaxEdge)throw new InvalidDataException("Raster preview pixel bounds invalid.");
        files.PinArtifact("page.png");NativeMediaPng.ValidateDocumentPage(files.ArtifactStream,width,height,request.MaxEdge,token);files.ArtifactStream.Position=0;return new(files,width,height);
    }
    public ValueTask DisposeAsync(){lock(gate){disposal??=Task.Run(files.Cleanup);return new(disposal);}}
}
