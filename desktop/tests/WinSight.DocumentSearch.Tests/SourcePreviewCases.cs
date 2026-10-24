using System.Security.Cryptography;
using System.Text.Json;
using WinSight.Core;
using WinSight.App.Services;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Media;
using WinSight.Infrastructure.Runtime;
using WinSight.Infrastructure.Storage;

internal static class SourcePreviewCases
{
    internal static async Task<int> RunAsync(string host,string fixtures,string root)
    {
        if(Directory.Exists(root))throw new IOException("Fresh headless source-operation test directory required.");Directory.CreateDirectory(root);
        var checks=new List<object>();int failed=0;
        async Task Test(string name,Func<Task> body){try{await body();checks.Add(new{name,ok=true});}catch(Exception error){failed++;checks.Add(new{name,ok=false,error=error.ToString()});}}
        void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        async Task Deny(Func<Task> action){try{await action();}catch(Exception e)when(e is UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException){return;}throw new InvalidOperationException("Expected denial did not occur.");}
        string sources=Path.Combine(root,"sources"),data=Path.Combine(root,"data");Directory.CreateDirectory(sources);Directory.CreateDirectory(data);
        string image=Path.Combine(sources,"owned.png"),pdf=Path.Combine(sources,"owned.pdf"),ppt=Path.Combine(sources,"owned.pptx"),word=Path.Combine(sources,"owned.docx"),legacy=Path.Combine(sources,"legacy.png");
        File.Copy(Path.Combine(fixtures,".dev/powerpoint-pages-01/probe-05/slide-0001.png"),image);File.Copy(image,legacy);
        File.Copy(Path.Combine(fixtures,".dev/document-pages-01/native-pipeline-04/sources/synthetic.pdf"),pdf);
        File.Copy(Path.Combine(fixtures,".dev/powerpoint-pages-01/production-05/synthetic.pptx"),ppt);
        File.Copy(Path.Combine(fixtures,".dev/source-navigation-01/office-probe-01/own.docx"),word);
        var anchors=new Dictionary<string,SourceAnchor>{[image]=new(new("image"),"ocr",Regions:[new(0,.1,.2,.25,.1,"Indexed line")]),[pdf]=new(new("pdf-page",Page:1),"ocr",Regions:[new(0,.1,.2,.25,.1,"Indexed line")]),[ppt]=new(new("slide",Page:2),"ocr",Regions:[new(0,.1,.2,.25,.1,"Indexed line")]),[word]=new(new("paragraph",Paragraph:2,Part:"body")),[legacy]=new(new("image"),"ocr",Regions:[new(0,.1,.2,.25,.1,"Legacy line")])};
        var settingsStore=new JsonSettingsStore(Path.Combine(data,"settings.json"));await settingsStore.SaveAsync(new AppSettings{Folders=[sources],InferenceExecutable=host,BackgroundIndexingEnabled=false});
        var hits=new Dictionary<string,SearchHit>();
        await using(var repository=new SqliteIndexRepository(Path.Combine(data,"index/index.sqlite")))
        {
            foreach(var (path,anchor) in anchors)
            {
                string kind=Path.GetExtension(path) switch{".pdf"=>"pdf",".png"=>"image",_=>"doc"};
                string metadata=path==legacy?"{}":JsonSerializer.Serialize(new{sourceSha256V1=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()});
                var file=new FileInfo(path);await repository.UpsertFileAsync(new(0,path,kind,file.Length,(file.LastWriteTimeUtc-DateTime.UnixEpoch).TotalSeconds,"done",null,metadata,[new("page","text",anchor.Location.Page,anchor.Location.Page,"Indexed source context",null,anchor)]));
                var row=(await repository.GetFileAsync(path))!;hits[path]=new(row.Id,path,kind,1,"keyword","Indexed source context",anchor.Location.Page,anchor.Location.Page,"page",anchor);
            }
        }
        await using var backend=await DesktopBackend.CreateAsync(new(data,EnableBackgroundIndexing:false));
        await Test("image_current_raster_geometry_and_source_pin",async()=>
        {
            var p=await backend.PreviewSourceAsync(hits[image]);try{Require(p.SourceCurrent&&p.RasterPath is not null&&p.PixelWidth==1600&&p.PixelHeight==900&&p.Source?.Regions?.Count==1,"Image preview mismatch");Require(!p.CanNavigate,"Image cannot claim exact Office navigation");try{using var writer=new FileStream(image,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);throw new InvalidOperationException("Source not pinned while displayed");}catch(IOException){ }}finally{await p.DisposeAsync();}Require(!Directory.EnumerateFileSystemEntries(Path.Combine(data,"source-preview")).Any(),"Raster task leaked");
        });
        await Test("pdf_page_current_raster_and_lease",async()=>{await using var p=await backend.PreviewSourceAsync(hits[pdf]);Require(p.SourceCurrent&&p.RasterPath is not null&&p.Source?.Location.Page==1&&p.PixelWidth>0,"PDF preview mismatch");});
        await Test("ppt_only_requested_hidden_slide_current_raster",async()=>{await using var p=await backend.PreviewSourceAsync(hits[ppt]);Require(p.SourceCurrent&&p.RasterPath is not null&&p.Source?.Location.Page==2&&p.PixelWidth>0&&p.CanNavigate,"PPT requested-slide preview or typed exact-open capability mismatch");});
        await Test("word_body_paragraph_can_navigate_no_fake_raster",async()=>{await using var p=await backend.PreviewSourceAsync(hits[word]);Require(p.SourceCurrent&&p.RasterPath is null&&p.CanNavigate&&p.Source?.Location.Paragraph==2,"Word preview mismatch");});
        await Test("old_index_without_fingerprint_text_only",async()=>{await using var p=await backend.PreviewSourceAsync(hits[legacy]);Require(!p.SourceCurrent&&p.RasterPath is null&&!p.CanNavigate&&p.Source?.Regions?.Count==0,"Legacy source policy must remove boxes, raster and navigation");});
        await Test("changed_bytes_same_length_timestamp_disable_raster_and_navigation",async()=>{var f=new FileInfo(image);var stamp=f.LastWriteTimeUtc;var bytes=File.ReadAllBytes(image);bytes[^1]^=1;File.WriteAllBytes(image,bytes);File.SetLastWriteTimeUtc(image,stamp);await using var p=await backend.PreviewSourceAsync(hits[image]);Require(!p.SourceCurrent&&p.RasterPath is null&&!p.CanNavigate,"Fingerprint did not catch same-metadata drift");});
        await Test("forged_file_id_denied",()=>Deny(async()=>{await using var p=await backend.PreviewSourceAsync(hits[pdf] with{FileId=999999});}));
        await Test("forged_page_denied",()=>Deny(async()=>{await using var p=await backend.PreviewSourceAsync(hits[pdf] with{Source=new(new("pdf-page",Page:99),"ocr")});}));
        await Test("forged_channel_denied",()=>Deny(async()=>{await using var p=await backend.PreviewSourceAsync(hits[pdf] with{Source=new(new("pdf-page",Page:1),"notes")});}));
        await Test("doc_code_index_boundary_denied",()=>Deny(async()=>{await using var p=await backend.PreviewSourceAsync(hits[pdf],code:true);}));
        await Test("caller_canceled_before_launch",async()=>{using var cancel=new CancellationTokenSource();cancel.Cancel();try{await using var p=await backend.PreviewSourceAsync(hits[pdf],cancellationToken:cancel.Token);throw new InvalidOperationException("Canceled request ran");}catch(OperationCanceledException){ }});
        await Test("ppt_current_slide_can_navigate_without_triggering_handoff",async()=>{await using var p=await backend.PreviewSourceAsync(hits[ppt]);Require(p.SourceCurrent&&p.CanNavigate&&p.Source?.Location.Page==2,"Typed current PPT navigation capability mismatch");});
        await Test("current_authorization_removed_rechecked",async()=>{var config=await backend.LoadSettingsAsync();config.Folders=[];await backend.SaveSettingsAsync(config);await Deny(async()=>{await using var p=await backend.PreviewSourceAsync(hits[pdf]);});await Deny(async()=>{await backend.NavigateSourceAsync(hits[word]);});});
        await Test("headless_normalized_geometry",()=>{var box=PreviewGeometry.Project(new(0,.1,.2,.25,.1),1600,900);Require(box==new PreviewRectangle(160,180,400,90),"Projection mismatch");return Task.CompletedTask;});
        await Test("invalid_geometry_rejected",()=>{foreach(var region in new[]{new OcrTextRegion(0,double.NaN,0,.1,.1),new(0,0,0,2,.1),new(0,-.1,0,.1,.1)}){bool rejected=false;try{PreviewGeometry.Project(region,1600,900);}catch(ArgumentException){rejected=true;}Require(rejected,"Invalid normalized geometry accepted");}return Task.CompletedTask;});
        await Test("office_request_wrong_type_or_range_rejected_before_launch",async()=>{string workspace=Path.Combine(root,"invalid-office");Directory.CreateDirectory(workspace);await using var client=new NativeMediaProcessClient(new(){HostExecutable=host,WorkspaceRoot=workspace});string hash=new('a',64);await Deny(async()=>{await client.NavigateOfficeAsync(new(word,hash,new("slide",Page:1)));});await Deny(async()=>{await client.NavigateOfficeAsync(new(Path.ChangeExtension(word,"xlsx"),hash,new("sheet",Sheet:"Case",Range:"XFE1")));});Require(!Directory.EnumerateFileSystemEntries(workspace).Any(),"Invalid request created task");});
        await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{success=failed==0,failed,checks,scope="Headless actual production DesktopBackend+SQLite+native WIC/PDF/singlePPT page. No WinUI process or replacement GUI acceptance, no embeddings/user data; one explicitly requested syntheticPPT render underidleexclusivecondition; ExactPPTnavigation behavior is validated separately; this suite only checks its typed current-source capability."},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"Headless source operations {checks.Count-failed}/{checks.Count}");return failed==0?0:1;
    }
}
