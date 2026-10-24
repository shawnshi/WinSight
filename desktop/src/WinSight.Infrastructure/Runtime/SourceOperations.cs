using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Media;

namespace WinSight.Infrastructure.Runtime;

public sealed partial class DesktopBackend
{
    private readonly SemaphoreSlim sourceOperationsGate=new(1,1);
    private CancellationTokenSource sourceOperationsStop=new();
    private readonly ConcurrentDictionary<Guid,PreviewSnapshot> sourcePreviews=new();

    private async Task<(IndexedFile File,SourceAnchor? Anchor,string Text,AppSettings Settings)> SourceSnapshotAsync(SearchHit hit,bool code,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(hit);MediaTaskFiles.LocalPath(hit.Path);
        await lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed||stopping!=0,this);
            var fresh=await settingsStore.LoadAsync(token);
            string freshIndex=fresh.IndexDirectory??Path.Combine(dataDirectory,"index");
            if(!PathsEqual(freshIndex,IndexDirectory))throw new InvalidOperationException("Index configuration changed; reload settings before preview.");
            bool allowed=!IsUnder(hit.Path,dataDirectory)&&!IsUnder(hit.Path,freshIndex)
                &&(code?fresh.CodeFolders:fresh.Folders).Any(root=>IsUnder(hit.Path,root)&&!(code?fresh.CodeSkipped:fresh.Skipped).Any(skip=>IsUnder(skip,root)&&!PathsEqual(skip,root)&&IsUnder(hit.Path,skip)));
            string extension=FilePolicy.Extension(hit.Path);
            if(!allowed||!code&&fresh.SkipTypes.Any(skip=>skip.TrimStart('.').Equals(extension,StringComparison.OrdinalIgnoreCase)||skip.Equals(hit.Kind,StringComparison.OrdinalIgnoreCase)))
                throw new UnauthorizedAccessException("Source is outside the current authorized selection or excluded.");
            var repository=(await GetRepositoriesAsync(token))[code?1:0];
            var file=await repository.GetFileAsync(hit.Path,token)??throw new InvalidOperationException("Source result is no longer indexed.");
            if(file.Id!=hit.FileId)throw new InvalidOperationException("Source result is stale; search again.");
            SourceAnchor? anchor=null;IReadOnlyList<IndexSegment> units=[];
            if(hit.Source is not null)
            {
                var match=file.Segments.FirstOrDefault(s=>s.Source?.Location==hit.Source.Location&&s.Source.Channel==hit.Source.Channel);
                if(match?.Source is null)throw new InvalidOperationException("Source location is no longer present in the index.");
                units=file.Segments.Where(s=>s.Source?.Location==match.Source.Location).ToArray();
                var regions=units.SelectMany(s=>s.Source?.Regions??[]).Distinct().OrderBy(r=>r.Line).Take(96).ToArray();
                anchor=match.Source with{Regions=regions};
            }
            else units=file.Segments.Where(s=>s.Kind!="name").Take(3).ToArray();
            string text=string.Join("\n\n",units.Where(s=>!string.IsNullOrEmpty(s.Excerpt)).Select(s=>s.Excerpt));
            if(text.Length>12000)text=ContentExtractor.Prefix(text,12000);
            return(file,anchor,text,fresh);
        }
        finally{lifecycle.Release();}
    }
    private static async Task<bool> CurrentSourceAsync(IndexedFile file,SafeFile pin,CancellationToken token)
    {
        if(file.Info is null)return false;using var info=JsonDocument.Parse(file.Info);
        if(!info.RootElement.TryGetProperty("sourceSha256V1",out var value)||value.ValueKind!=JsonValueKind.String||value.GetString() is not {Length:64} expected||!expected.All(Uri.IsHexDigit))return false;
        pin.Stream!.Position=0;var actual=await SHA256.HashDataAsync(pin.Stream,token);pin.Stream.Position=0;
        return CryptographicOperations.FixedTimeEquals(actual,Convert.FromHexString(expected));
    }
    public async Task<ISourcePreview> PreviewSourceAsync(SearchHit hit,bool code=false,CancellationToken cancellationToken=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetimeStop.Token,sourceOperationsStop.Token);var token=linked.Token;
        var source=await SourceSnapshotAsync(hit,code,token);
        await sourceOperationsGate.WaitAsync(token);SafeFile? pin=null;IAsyncDisposable? artifact=null;
        try
        {
            token.ThrowIfCancellationRequested();pin=SafeFile.Open(source.File.Path);
            bool current=await CurrentSourceAsync(source.File,pin,token);string? raster=null;int width=0,height=0;string notice="Indexed text preview.";
            if(!current)notice="Source fingerprint is missing or the file changed. Raster, OCR boxes and precise navigation are disabled; re-index this file.";
            else
            {
                string workspace=Path.Combine(dataDirectory,"source-preview");Directory.CreateDirectory(workspace);
                await using var client=new NativeMediaProcessClient(new(){HostExecutable=source.Settings.InferenceExecutable??Path.Combine(applicationDirectory,"inference","WinSight.InferenceHost.exe"),WorkspaceRoot=workspace});
                var location=source.Anchor?.Location;var extension=FilePolicy.Extension(source.File.Path);
                try
                {
                    if(extension=="pdf")
                    {
                        var page=await client.RenderPdfPageAsync(new(source.File.Path,location?.Page??1),token);artifact=page;raster=page.ArtifactPath;width=page.PixelWidth;height=page.PixelHeight;notice="Current PDF page; boxes are indexed OCR text lines, not word/character coordinates.";
                    }
                    else if(extension=="pptx")
                    {
                        var page=await client.RenderPresentationPageAsync(new(source.File.Path,location?.Page??1),token);artifact=page;
                        if(page.State=="deferred")notice=$"PowerPoint preview deferred ({page.Reason}); no existing instance was attached.";
                        else if(page.Slides.SingleOrDefault() is {} slide){raster=slide.ArtifactPath;width=slide.PixelWidth;height=slide.PixelHeight;notice="Current PowerPoint slide. Keep PowerPoint closed until the preview completes.";}
                        else notice="The requested slide was not rendered.";
                    }
                    else if(source.File.Kind is "image" or "screenshot")
                    {
                        var image=await client.RenderRasterPreviewAsync(new(source.File.Path),token);artifact=image;raster=image.ArtifactPath;width=image.PixelWidth;height=image.PixelHeight;notice="EXIF-normalized current image; indexed OCR line coordinates share the same image frame.";
                    }
                    else notice="Indexed paragraph / sheet context. Use read-only precise open for supported Word/Excel locations.";
                }
                catch(OperationCanceledException){throw;}
                catch(Exception e)when(e is IOException or InvalidDataException or NotSupportedException or ArgumentException)
                {notice=$"Raster preview unavailable ({e.GetType().Name}); indexed text remains visible.";}
            }
            token.ThrowIfCancellationRequested();
            bool navigate=current&&source.Anchor?.Location is {} target&&NativeOfficeNavigationRequest.SupportsLocation(source.File.Path,target);
            if(navigate&&source.Anchor?.Location.Type=="slide")notice+=" Precise open requires PowerPoint to be idle; do not open PowerPoint during its exclusive interval.";
            var snapshot=new PreviewSnapshot(source.Text,current?source.Anchor:source.Anchor is null?null:source.Anchor with{Regions=[]},raster,width,height,notice,current,navigate,pin,artifact,sourcePreviews);
            sourcePreviews[snapshot.Id]=snapshot;pin=null;artifact=null;return snapshot;
        }
        finally{try{if(artifact is not null)await artifact.DisposeAsync();}finally{pin?.Dispose();sourceOperationsGate.Release();}}
    }
    public async Task<SourceNavigationResult> NavigateSourceAsync(SearchHit hit,bool code=false,CancellationToken cancellationToken=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetimeStop.Token,sourceOperationsStop.Token);var token=linked.Token;
        var snapshot=await SourceSnapshotAsync(hit,code,token);
        var location=snapshot.Anchor?.Location??throw new NotSupportedException("No exact indexed location exists for this result.");
        await sourceOperationsGate.WaitAsync(token);
        try
        {
            using var pin=SafeFile.Open(snapshot.File.Path);
            if(!await CurrentSourceAsync(snapshot.File,pin,token))throw new InvalidOperationException("Source changed or has no fingerprint; re-index before precise open.");
            using var metadata=JsonDocument.Parse(snapshot.File.Info!);string hash=metadata.RootElement.GetProperty("sourceSha256V1").GetString()!;
            var request=new NativeOfficeNavigationRequest(snapshot.File.Path,hash,location);request.Validate();
            string workspace=Path.Combine(dataDirectory,"source-navigation");Directory.CreateDirectory(workspace);
            await using var client=new NativeMediaProcessClient(new(){HostExecutable=snapshot.Settings.InferenceExecutable??Path.Combine(applicationDirectory,"inference","WinSight.InferenceHost.exe"),WorkspaceRoot=workspace});
            var result=await client.NavigateOfficeAsync(request,token);
            return new(true,"Opened a new read-only Office view at "+result.Target+". The window now belongs to you."+(result.CleanupWarning is null?"":" "+result.CleanupWarning),result.ProcessId);
        }
        finally{sourceOperationsGate.Release();}
    }

    private async Task StopSourceOperationsAsync()
    {
        sourceOperationsStop.Cancel();await sourceOperationsGate.WaitAsync();
        try{foreach(var preview in sourcePreviews.Values)await preview.DisposeAsync();sourceOperationsStop.Dispose();sourceOperationsStop=new();}
        finally{sourceOperationsGate.Release();}
    }
    private sealed class PreviewSnapshot(string excerpt,SourceAnchor? source,string? raster,int width,int height,string notice,bool current,bool navigate,SafeFile pin,IAsyncDisposable? artifact,ConcurrentDictionary<Guid,PreviewSnapshot> owners):ISourcePreview
    {
        internal Guid Id{get;}=Guid.NewGuid();private readonly object gate=new();private Task? disposal;
        public string Excerpt=>excerpt;public SourceAnchor? Source=>source;public string? RasterPath=>raster;public int PixelWidth=>width;public int PixelHeight=>height;public string Notice=>notice;public bool SourceCurrent=>current;public bool CanNavigate=>navigate;
        public ValueTask DisposeAsync(){lock(gate){disposal??=DisposeCore();return new(disposal);}}
        private async Task DisposeCore(){try{if(artifact is not null)await artifact.DisposeAsync();}finally{pin.Dispose();owners.TryRemove(Id,out _);}}
    }
}
