using System.Text.Json;
using System.Text.Json.Nodes;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Media;
using WinSight.Infrastructure.Search;
using WinSight.Shared;

namespace WinSight.Infrastructure.Indexing;

internal sealed class NativePresentationEmbeddingException(Exception cause) : IOException("Presentation embedding dependency failed; run stopped.",cause);
internal sealed class NativePresentationPages(NativeMediaProcessClient? client)
{
    internal static string Revision => $"{PowerPointPolicy.RenderPolicy}:{OcrIdentity.ExtractionRevision}:slides32:edge1600";
    internal static bool IsCurrent(string? info)
    {
        if(info is null)return false;using var json=JsonDocument.Parse(info);var root=json.RootElement;
        return root.TryGetProperty("native_presentation_revision",out var rev)&&rev.GetString()==Revision
            && root.TryGetProperty("native_presentation_office",out var version)&&version.GetString()==PowerPointPolicy.Identity();
    }
    internal static bool ReusePartial(string? info, bool allowAutomation)
    {
        if(!IsCurrent(info))return false;using var json=JsonDocument.Parse(info!);var root=json.RootElement;
        if(root.TryGetProperty("native_presentation_policy_limited",out var limited)&&limited.ValueKind==JsonValueKind.True)return true;
        if(!root.TryGetProperty("native_presentation_deferred",out var deferred))return false;
        return deferred.GetString() switch {"powerpoint-busy"=>PowerPointPolicy.Busy,"renderer-busy"=>PowerPointPolicy.Busy,"powerpoint-missing"=>PowerPointPolicy.Identity()=="missing","unsupported-content"=>true,"manual-index-required"=>!allowAutomation,_=>false};
    }
    internal async Task<ExtractionResult> AppendAsync(string path,string title,ExtractionResult text,IEmbeddingService? embeddings,EmbeddingInfo? info,bool allowAutomation,CancellationToken caller)
    {
        var metadata=JsonNode.Parse(text.Info??throw new InvalidDataException("PPTX source metadata missing."))!.AsObject();
        var segments=text.Segments.ToList();var problems=new List<string>();int visual=0,ocr=0;bool transient=false,stable=false;string deferred="";
        string version=PowerPointPolicy.Identity();
        if(!allowAutomation)
        {deferred="manual-index-required";problems.Add("PowerPoint whole-slide rendering requires manual indexing; background work retains text only.");}
        else if(client is null||embeddings is null||info is null||!info.Capabilities.Contains("image",StringComparer.OrdinalIgnoreCase))
            problems.Add("PowerPoint whole-slide coverage unavailable: helper or image capability missing; text retained.");
        else
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(caller);deadline.CancelAfter(TimeSpan.FromSeconds(180));var token=deadline.Token;
            NativePresentationLease? batch=null;
            try
            {
                batch=await client.RenderPresentationAsync(new(path),token);version=batch.OfficeVersion;
                if(batch.State=="deferred")
                {
                    deferred=batch.Reason;problems.Add($"PowerPoint visual coverage deferred ({deferred}); text retained. Do not open PowerPoint during a render interval.");
                }
                else
                {
                    if(metadata["sourceUnits"]?.GetValue<int>()!=batch.SlideCount)throw new InvalidDataException("PPTX parsers disagree on slide count.");
                    foreach(var slide in batch.Slides)
                    {
                        caller.ThrowIfCancellationRequested();token.ThrowIfCancellationRequested();
                        if(segments.Count>=ContentExtractor.MaxSegments){problems.Add("PPTX combined segment bound reached.");break;}
                        var location=new SourceLocation("slide",Page:slide.Number);
                        var body=text.Segments.Where(s=>s.Source?.Location==location&&s.Source.Channel!="notes").ToArray();
                        string bodyText=string.Join("\n",body.Select(s=>s.Excerpt));
                        var fields=body.SelectMany(s=>s.Source?.Fields??[]).Concat(DocumentSearchQuery.FieldsFromText(bodyText)).Distinct().ToList();
                        IReadOnlyList<OcrTextRegion> regions=[];string ocrText="";
                        if(bodyText.Length<200)
                        {
                            var inspection=await client.InspectImageAsync(slide.ArtifactPath,token);ocr++;regions=inspection.OcrRegions;ocrText=NativeImageExtraction.NormalizeOcrText(inspection.OcrText);
                            if(inspection.State!="done"){problems.Add($"Slide{slide.Number}: OCR coverage partial.");transient=true;}
                            if(ocrText.Contains("PRIVATE KEY-----",StringComparison.Ordinal))return new([],"skipped","PPTX OCR contains private-key material; content was not indexed.");
                            fields.AddRange(DocumentSearchQuery.FieldsFromText(ocrText));
                        }
                        var pageFields=fields.Distinct().Take(64).ToArray();if(fields.Distinct().Count()>64)problems.Add($"Slide{slide.Number}: field bound reached.");
                        for(int n=0;n<text.Segments.Count;n++)if(segments[n].Source?.Location==location&&segments[n].Source!.Channel!="notes")
                            segments[n]=segments[n] with {Source=segments[n].Source! with{Fields=pageFields,Regions=regions}};
                        var image=await Embed(new("image",slide.ArtifactPath,280),batch,embeddings,token);
                        if(image is null){problems.Add($"Slide{slide.Number}: visual embedding failed.");transient=true;}
                        else{segments.Add(new("page","image",slide.Number,slide.Number,null,image, Source: new(location,"page-visual",pageFields,regions)));visual++;}
                        var(chunks,cut)=ContentExtractor.Chunks(ocrText,token);if(cut)problems.Add($"Slide{slide.Number}: OCR chunks limited.");
                        foreach(var chunk in chunks)
                        {
                            if(segments.Count>=ContentExtractor.MaxSegments||segments.Sum(s=>(long)(s.Excerpt?.Length??0))+chunk.Excerpt!.Length>ContentExtractor.MaxCharacters){problems.Add("PPTX combined text/segment bound reached.");break;}
                            var vector=info.Capabilities.Contains("text",StringComparer.OrdinalIgnoreCase)?await Embed(new("text",$"title: {title} | text: {chunk.Excerpt}"),batch,embeddings,token):null;
                            if(vector is null){problems.Add($"Slide{slide.Number}: OCR text embedding failed.");transient=true;}
                            segments.Add(chunk with{Location=slide.Number,LocationEnd=slide.Number,Vector=vector,Source=new(location,"ocr",pageFields,regions)});
                        }
                    }
                    if(batch.State=="partial")problems.Add($"PowerPoint rendered coverage limited ({batch.Reason}); extractable text may cover later slides.");
                    stable=batch.State=="partial"&&batch.Reason=="slide-limit"&&visual==32&&!transient&&text.State!="partial"&&problems.Count==1;
                    metadata["native_presentation_slides"]=batch.SlideCount;
                }
            }
            catch(OperationCanceledException)when(caller.IsCancellationRequested){throw;}
            catch(OperationCanceledException)when(deadline.IsCancellationRequested){problems.Add("PPTX coverage reached the shared180s budget.");}
            catch(NativePresentationEmbeddingException){throw;}
            catch(Exception e)when(e is IOException or InvalidDataException or JsonException or ArgumentException or NotSupportedException){problems.Add($"PPTX visual/OCR stage failed ({e.GetType().Name}); text retained.");}
            finally{if(batch is not null)await batch.DisposeAsync();}
        }
        if(text.State=="partial")problems.Add(text.Detail??"PPTX extractable text coverage partial.");
        metadata["native_presentation_revision"]=Revision;metadata["native_presentation_office"]=version;
        metadata["native_presentation_visual_slides"]=visual;metadata["native_presentation_ocr_slides"]=ocr;
        metadata["native_presentation_policy_limited"]=stable;metadata["native_presentation_deferred"]=deferred;
        metadata["native_presentation_coverage_complete"]=problems.Count==0;
        metadata["visualCoverage"]=$"PowerPoint whole-slide PNG: {visual} slides; omitted/error coverage remains partial";
        return new(segments,problems.Count==0?"done":"partial",problems.Count==0?null:string.Join(" ",problems.Distinct()),metadata.ToJsonString());
    }
    private static async Task<float[]?> Embed(EmbeddingInput input,NativePresentationLease batch,IEmbeddingService embeddings,CancellationToken token)
    {
        IReadOnlyList<EmbeddingResult> result;
        try{result=await embeddings.EmbedAsync([input],token);}
        catch(Exception e)when(e is not OutOfMemoryException and not StackOverflowException)
        {batch.Retain();if(e is OperationCanceledException)throw;throw new NativePresentationEmbeddingException(e);}
        token.ThrowIfCancellationRequested();if(result.Count!=1||result[0].Error is not null||result[0].Vector is not {} vector)return null;
        try{VectorContract.Validate(vector);return vector;}catch(InvalidDataException){return null;}
    }
}
