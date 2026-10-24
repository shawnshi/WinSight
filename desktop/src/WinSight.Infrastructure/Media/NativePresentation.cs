using System.Text.Json;

namespace WinSight.Infrastructure.Media;

public sealed record NativePresentationRequest(string SourcePath, int MaxSlides = 32, int MaxEdge = 1600)
{
    internal void Validate()
    {
        if(MaxSlides is <1 or >32 || MaxEdge is <256 or >1600)throw new ArgumentOutOfRangeException(nameof(MaxSlides));
        if(!Path.GetExtension(SourcePath).Equals(".pptx",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Only PPTX whole-slide rendering is supported.");
    }
}
public sealed record NativePresentationPageRequest(string SourcePath,int PageNumber,int MaxEdge=1600)
{
    internal void Validate(){new NativePresentationRequest(SourcePath,1,MaxEdge).Validate();if(PageNumber is <1 or >5000)throw new ArgumentOutOfRangeException(nameof(PageNumber));}
}
public sealed record NativePresentationSlide(int Number,int PixelWidth,int PixelHeight,bool Hidden,string ArtifactPath);

public sealed class NativePresentationLease : IAsyncDisposable
{
    private readonly MediaTaskFiles files;
    private readonly object gate=new();private Task? disposal;private bool retained;
    public string State { get; }
    public string Reason { get; }
    public string OfficeVersion { get; }
    public int SlideCount { get; }
    public IReadOnlyList<NativePresentationSlide> Slides { get; }
    private NativePresentationLease(MediaTaskFiles files,string state,string reason,string version,int count,List<NativePresentationSlide> slides) =>
        (this.files,State,Reason,OfficeVersion,SlideCount,Slides)=(files,state,reason,version,count,slides.AsReadOnly());
    internal static NativePresentationLease Parse(string payload,NativePresentationRequest request,MediaTaskFiles files,CancellationToken token)
        => ParseCore(payload,request,files,token,null);
    internal static NativePresentationLease ParsePage(string payload,NativePresentationPageRequest request,MediaTaskFiles files,CancellationToken token) =>
        ParseCore(payload,new(request.SourcePath,1,request.MaxEdge),files,token,request.PageNumber);
    private static NativePresentationLease ParseCore(string payload,NativePresentationRequest request,MediaTaskFiles files,CancellationToken token,int? page)
    {
        using var json=JsonDocument.Parse(payload,new JsonDocumentOptions{MaxDepth=8});var root=json.RootElement;
        Members(root,["protocol","inputPath","state","reason","officeVersion","slideCount","renderPolicy","slides"]);
        if(root.GetProperty("protocol").GetInt32()!=1 || root.GetProperty("renderPolicy").GetString()!=PowerPointPolicy.RenderPolicy
            || !string.Equals(root.GetProperty("inputPath").GetString(),request.SourcePath,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Presentation result identity mismatch.");
        string state=root.GetProperty("state").GetString()!,reason=root.GetProperty("reason").GetString()!,version=root.GetProperty("officeVersion").GetString()!;
        if(version is null || version.Length is <1 or >128 || version.Contains('\0') || state is not ("done" or "partial" or "deferred"))throw new InvalidDataException("Invalid presentation result state/version.");
        int count=root.GetProperty("slideCount").GetInt32();var list=root.GetProperty("slides");
        if(list.ValueKind!=JsonValueKind.Array || list.GetArrayLength()>request.MaxSlides)throw new InvalidDataException("Presentation slide bound exceeded.");
        if(state=="deferred")
        {
            if(reason is not ("renderer-busy" or "powerpoint-busy" or "powerpoint-missing" or "unsupported-content") || count!=0 || list.GetArrayLength()!=0)
                throw new InvalidDataException("Invalid presentation deferral.");
        }
        else if(count is <1 or >5000 || (state=="done" && (reason!="" || list.GetArrayLength()!=(page is null?count:1)))
            || (state=="partial" && reason is not ("slide-limit" or "png-byte-limit")))throw new InvalidDataException("Invalid presentation coverage.");
        if(state=="partial" && reason=="slide-limit" && (count<=request.MaxSlides || list.GetArrayLength()!=request.MaxSlides))throw new InvalidDataException("False slide-limit coverage.");
        var slides=new List<NativePresentationSlide>();long total=0;
        foreach(var item in list.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();Members(item,["number","width","height","hidden","artifact"]);
            int number=item.GetProperty("number").GetInt32(),w=item.GetProperty("width").GetInt32(),h=item.GetProperty("height").GetInt32();
            string name=page is null?$"slide-{number:0000}.png":"page.png";
            if(number!=(page??1)+slides.Count || number>count || w<1 || h<1 || w>request.MaxEdge || h>request.MaxEdge || Math.Max(w,h)!=request.MaxEdge
                || item.GetProperty("artifact").GetString()!=name || item.GetProperty("hidden").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Invalid ordered presentation slide/dimensions.");
            files.PinPresentationFile(name);var stream=files.PresentationStream(name);total=checked(total+stream.Length);
            if(total>64L*1024*1024)throw new InvalidDataException("Presentation accepted PNG aggregate exceeds64MiB.");
            NativeMediaPng.ValidateDocumentPage(stream,w,h,request.MaxEdge,token);stream.Position=0;
            slides.Add(new(number,w,h,item.GetProperty("hidden").GetBoolean(),Path.Combine(files.OutputDirectory,name)));
        }
        return new(files,state,reason,version,count,slides);
    }
    private static void Members(JsonElement value,string[] expected)
    {
        if(value.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Presentation member must be an object.");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in value.EnumerateObject())if(!expected.Contains(property.Name,StringComparer.Ordinal)||!seen.Add(property.Name))throw new InvalidDataException("Unknown/duplicate presentation member.");
        if(seen.Count!=expected.Length)throw new InvalidDataException("Missing presentation member.");
    }
    internal void Retain(){lock(gate){if(disposal is not null)throw new InvalidOperationException("Presentation already disposed.");retained=true;MediaRetention.Keep(this);}}
    public ValueTask DisposeAsync(){lock(gate){if(retained)return ValueTask.CompletedTask;disposal??=Task.Run(files.Cleanup);return new(disposal);}}
}
