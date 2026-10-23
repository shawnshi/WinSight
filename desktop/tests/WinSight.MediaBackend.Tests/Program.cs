using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Runtime;

namespace WinSight.MediaBackend.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options=Enumerable.Range(0,args.Length/2).ToDictionary(i=>args[i*2],i=>args[i*2+1],StringComparer.Ordinal);
        string root=Path.GetFullPath(options["--artifact-root"]),fake=Path.GetFullPath(options["--fake-host"]);
        Evidence.Assert(!Directory.Exists(root),"Explicit NEW task root required"); Directory.CreateDirectory(root);
        string? native=options.GetValueOrDefault("--actual-host"); var results=new List<object>(); int failed=0,missing=0,ordinal=0;
        Evidence.Write(Path.Combine(root,"loaded-assemblies.json"),new[]{typeof(DesktopBackend).Assembly,typeof(AppSettings).Assembly}.Select(a=>new {a.FullName,a.Location,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location)))}));
        async Task Case(string name,Func<BackendFixture,Task> body)
        {
            if(failed!=0){missing++;results.Add(new{name,status="not-run",reason="Stopped after first surfaced failure; parent integrated candidate required"});Save();return;}
            string dir=Path.Combine(root,"c"+(++ordinal).ToString("D2")); var watch=Stopwatch.StartNew();
            try { await using(var h=new BackendFixture(dir,fake)) await body(h); results.Add(new{name,status="passed",path=dir,seconds=watch.Elapsed.TotalSeconds});Console.WriteLine("PASS "+name); }
            catch(Exception e){failed++;results.Add(new{name,status="failed",path=dir,error=e.ToString(),seconds=watch.Elapsed.TotalSeconds});Console.WriteLine("FAIL "+name+": "+e);}
            Save();
        }
        void Save()=>File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{failed,notRun=missing,results},Evidence.Json));
        await Case("composite/real-stdio-client-profile",async h=>
        {
            await h.Open(); var info=await h.Backend.GetEmbeddingInfoAsync();
            Evidence.Assert(info.Dimension==768 && info.RequestedProvider=="CPUExecutionProvider" && info.Capabilities.SequenceEqual(new[]{"text","image","audio"}),"Actual stdio handshake mismatch");
            Evidence.Assert(h.MediaEvents().Length==0 && !Directory.Exists(Path.Combine(h.Data,"media-tasks")),"Info constructed media workspace/helper");
        });
        await Case("backend/init-status-search-no-media-IO",async h=>
        {
            h.Media(); await h.Open(); Evidence.Assert(h.Events().Length==0,"Init launched a child");
            await h.Backend.GetStatusAsync(); await h.Backend.SearchAsync(new SearchRequest("synthetic",Mode:SearchMode.Keyword));
            Evidence.Assert(h.Events().Length==0 && !Directory.Exists(Path.Combine(h.Data,"media-tasks")),"Status/keyword search launched or made media workspace");
            await Evidence.Throws(()=>h.Backend.SearchAsync(new SearchRequest("synthetic",Mode:SearchMode.Semantic)),e=>e is InvalidDataException && e.Message=="Index has no profile_id; legacy vectors cannot be assumed compatible. Reindex separately.");
            Evidence.Assert(h.MediaEvents().Length==0 && !Directory.Exists(Path.Combine(h.Data,"media-tasks")),"Rejected empty-index semantic search launched media helper");
        });
        await Case("backend/code-and-document-regression-no-cli",async h=>
        {
            h.Media(); string codeRoot=Path.Combine(h.Input,"code");Directory.CreateDirectory(codeRoot);
            string code=Path.Combine(codeRoot,"sample.cs"),doc=Path.Combine(h.Input,"sample.txt");
            // A nested task-only Git root avoids ancestor ignores without making the document root a code project.
            Directory.CreateDirectory(Path.Combine(codeRoot,".git","objects"));Directory.CreateDirectory(Path.Combine(codeRoot,".git","refs","heads"));
            File.WriteAllText(Path.Combine(codeRoot,".git","HEAD"),"ref: refs/heads/synthetic\n");
            File.WriteAllText(code,"public static class Synthetic { public static int Answer() => 42; }\n");
            File.WriteAllText(doc,"Synthetic orchard document contains a searchable sentence and no personal information."); await h.Open();
            await h.Index(code:true); Evidence.Assert(h.MediaEvents().Length==0 && !Directory.Exists(Path.Combine(h.Data,"media-tasks")),"Code creates workspace or launches media");
            Evidence.Assert((await h.Row(code,true))?.Segments.Any(s=>s.Vector?.Length==768)==true,"Code embedding regression");
            await h.Index(skip:["wav"]); Evidence.Assert(h.MediaEvents().Length==0,"Document-only run launched media");
            Evidence.Assert((await h.Row(doc))?.State=="done" && h.Embeddings().Any(e=>e.GetProperty("op").GetString()=="text"),"Legacy document path regression");
        });
        await Case("factory/audio32/default-public-backend",async h=>
        {
            string source=h.Media(); await h.Open(); await h.Index(); var row=await h.Row(source); BackendFixture.State(row,"done",true);
            var media=ContentSegments(row!);
            Evidence.Assert(media.Length==2 && media.Select(s=>(s.Location,s.LocationEnd)).SequenceEqual(new (double?,double?)[]{(0,30),(25,32)}),"Original source-time audio windows");
            Evidence.Assert(media.All(s=>s.Kind=="audio"&&s.Modality=="audio"&&s.Vector?.Length==768&&s.Excerpt is null),"768 native vectors, never whole-file/text fallback");
            FactoryReceipts(h); h.WorkspaceEmpty(); h.HelpersGone(); Evidence.Released(source,h.Input);
        });
        await Case("factory/video-soundtrack/original-locators",async h=>
        {
            string source=h.Media("video",new(4)); await h.Open(); await h.Index(); var row=await h.Row(source); BackendFixture.State(row,"done",true);
            var frames=row!.Segments.Where(s=>s.Kind=="frame").ToArray();
            Evidence.Assert(frames.Select(s=>(s.Location,s.LocationEnd)).SequenceEqual(new (double?,double?)[]{(.5,3.5),(3.5,4)}),"Video frame source locators");
            Evidence.Assert(row.Segments.Any(s=>s.Kind=="audio"&&s.Location==0&&s.LocationEnd==4&&s.Vector?.Length==768),"Video soundtrack vector missing");
            Evidence.Assert(JsonDocument.Parse(row.Info!).RootElement.GetProperty("has_audio").GetBoolean(),"Known hasAudio lost"); FactoryReceipts(h);h.WorkspaceEmpty();h.HelpersGone();
        });
        await Case("factory/application-directory-fallback-same-host",async h=>
        {
            string source=h.Media(fixture:new(2)); await h.Open(fallback:true); await h.Index();BackendFixture.State(await h.Row(source),"done",true);
            var starts=h.Events().Where(e=>e.GetProperty("phase").GetString()=="start");
            Evidence.Assert(starts.All(e=>Path.GetFileName(e.GetProperty("executable").GetString())=="WinSight.InferenceHost.exe"),"Model/media did not capture exact application fallback host");h.WorkspaceEmpty();
        });
        await Case("failure/missing-host-truthful-no-vectors",async h=>
        {
            string source=h.Media();await h.Open(hostEnabled:false);Exception? thrown=null;try{await h.Index();}catch(Exception e){thrown=e;}
            var row=await h.Row(source);Evidence.Assert(thrown is not null||row?.State is "failed" or "partial","Missing host silently succeeded");
            Evidence.Assert(row?.Segments.All(s=>s.Vector is null)!=false&&h.Events().Length==0,"Missing host produced vectors or fake fallback");
        });
        await Case("failure/missing-audio-capability-no-decode",async h=>
        {string source=h.Media();await h.Open(mode:"no-audio");await h.Index();BackendFixture.State(await h.Row(source),"partial",false);Evidence.Assert(h.MediaEvents().Length==0,"Unavailable modality launched helper");});
        foreach(var (mode,state) in new[]{("decode-first","failed"),("decode-later","partial")})
            await Case("failure/"+mode+"/P1",async h=>
            {string source=h.Media(fixture:new(32,Mode:mode));await h.Open();await h.Index();var row=await h.Row(source);BackendFixture.State(row,state,false);BackendFixture.Redacted(row,h);Evidence.Assert(ContentSegments(row!).Length==(state=="partial"?1:0),"Prior vector lost or fake vector added");h.WorkspaceEmpty();h.HelpersGone();await h.Backend.SetIndexingPausedAsync(true);Evidence.Assert((await h.Backend.GetStatusAsync()).IndexingState==IndexingControlState.Paused,"Ordinary decode error incorrectly poisons control");await h.Backend.SetIndexingPausedAsync(false);});
        foreach(var (mode,state) in new[]{("item-first","failed"),("item-later","partial")})
            await Case("failure/"+mode+"/P1",async h=>
            {string source=h.Media();await h.Open(mode:mode);await h.Index();var row=await h.Row(source);BackendFixture.State(row,state,false);BackendFixture.Redacted(row,h);Evidence.Assert(ContentSegments(row!).Length==(state=="partial"?1:0),"Item error vector retention mismatch");h.WorkspaceEmpty();});
        await Case("failure/embedding-dependency-original-cause",async h=>
        {
            string source=h.Media();await h.Open(mode:"dependency");var e=await Evidence.Throws(()=>h.Index(),x=>x.GetType().Name=="EmbeddingDependencyException");
            Evidence.Assert(e.InnerException is IOException&&e.InnerException.Message.Contains("SYNTHETIC_UNTRUSTED",StringComparison.Ordinal),"Transient dependency cause lost");
            var row=await h.Row(source);BackendFixture.State(row,"failed",false);BackendFixture.Redacted(row,h);h.WorkspaceEmpty();
        });
        await Case("failure/video-known-audio-decoder-not-silent-done",async h=>
        {
            string source=h.Media("video",new(4,Mode:"soundtrack-fail"));await h.Open();await h.Index();var row=await h.Row(source);BackendFixture.State(row,"partial",false);BackendFixture.Redacted(row,h);
            Evidence.Assert(row!.Segments.Any(s=>s.Modality=="image")&&!row.Segments.Any(s=>s.Modality=="audio"),"Video failure invented soundtrack vectors");
            Evidence.Assert(row.Info is null||!row.Info.Contains("\"has_audio\":false",StringComparison.Ordinal),"Decode failure falsified known hasAudio");h.WorkspaceEmpty();
        });
        await Case("boundary/capacity-skip-no-helper",async h=>
        {string source=h.Media();await h.Open();await h.Index(max:1);Evidence.Assert((await h.Row(source))?.State=="skipped"&&h.MediaEvents().Length==0,"Oversize source parsed/launched helper");});
        await Case("authorization/root-skip-data-index-never-parse",async h=>
        {
            await h.Open();string forbidden=Path.Combine(h.Root,"unauthorized");h.Media(directory:forbidden);h.Media(directory:h.Data,name:"private-data");
            await h.Backend.GetStatusAsync();string index=Path.Combine(h.Data,"index");h.Media(directory:index,name:"private-index");
            foreach(string dir in new[]{forbidden,h.Data,index})await Evidence.Throws(()=>h.Backend.IndexAsync(new IndexRequest([dir])),e=>e is UnauthorizedAccessException);
            string skipped=Path.Combine(h.Input,"excluded");h.Media(directory:skipped);h.Media(name:"skip-type");await h.Index(excluded:[skipped],skip:["wav"]);
            Evidence.Assert(h.MediaEvents().Length==0,"Unauthorized/skipped/data/index parsed media");
            var s=await h.Backend.LoadSettingsAsync();s.Folders.Add(h.Root);await h.Backend.SaveSettingsAsync(s);
            await h.Backend.IndexAsync(new IndexRequest([h.Root],Excluded:[h.App,h.Models,h.Audit,h.Input,forbidden],Force:true));
            Evidence.Assert(h.MediaEvents().Length==0,"Ancestor root bypassed data/index exclusion");
        });
        foreach(string hold in new[]{"helper","embedding"})
        {
            await Case("lifecycle/pause-"+hold+"/physical-tree-and-pins",h=>Interrupt(h,hold,"pause"));
            await Case("lifecycle/cancel-"+hold+"/physical-tree-and-pins",h=>Interrupt(h,hold,"cancel"));
            await Case("lifecycle/shutdown-"+hold+"/physical-tree-and-pins",h=>Interrupt(h,hold,"shutdown"));
        }
        await Case("settings/active-reject-CAS-next-host-quiescent",async h=>
        {
            string source=h.Media(fixture:new(32,Mode:"hold-helper"));await h.Open();Task work=h.Index();await h.Wait("helper-held.json",work);
            var s=await h.Backend.LoadSettingsAsync();long revision=s.Revision;s.InferenceExecutable=h.InstallHost("second");
            await Evidence.Throws(()=>h.Backend.SaveSettingsAsync(s),e=>e is InvalidOperationException&&e.Message=="Stop indexing before changing settings.");
            Evidence.Assert((await h.Backend.LoadSettingsAsync()).Revision==revision,"Rejected active settings committed");
            await h.Backend.SetIndexingPausedAsync(true);await Evidence.Throws(()=>work,e=>e is OperationCanceledException);h.HelpersGone();
            File.WriteAllText(source+".fixture.synthetic",JsonSerializer.Serialize(new MediaFixture(32)));await h.Backend.SaveSettingsAsync(s);
            var stale=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;stale.Revision=revision;
            await Evidence.Throws(()=>h.Backend.SaveSettingsAsync(stale),e=>e.GetType().Name=="SettingsConflictException");
            int old=h.Events().Length;await h.Backend.SetIndexingPausedAsync(false);await h.Index();BackendFixture.State(await h.Row(source),"done",true);
            var newStarts=h.Events().Where(e=>e.GetProperty("phase").GetString()=="start"&&e.GetProperty("executable").GetString()==s.InferenceExecutable).ToArray();
            Evidence.Assert(newStarts.Any(e=>e.GetProperty("args")[0].GetString()=="--stdio")&&newStarts.Any(e=>e.GetProperty("args")[0].GetString()=="inspect-media"),"Next run did not use changed model AND media host");
            Evidence.Assert(old>0,"Missing old host receipts");h.WorkspaceEmpty();
        });
        await Case("background/host-disabled-no-watch-no-resume-optin",async h=>
        {
            await h.Open(backgroundSetting:true,backgroundHost:false);await h.Backend.SetIndexingPausedAsync(true);await h.Backend.SetIndexingPausedAsync(false);h.Media();await Task.Delay(750);
            Evidence.Assert(h.Events().Length==0&&NoWatcher(h.Backend),"Disabled host created watcher/work on resume");
            var s=await h.Backend.LoadSettingsAsync();Evidence.Assert(s.BackgroundIndexingEnabled&&s.SetUp,"Host silently rewrote settings authorization");
        });
        await Case("background/setup-required-then-authorized-root-only",async h=>
        {
            string source=h.Media(fixture:new(2));await h.Open(setup:false,backgroundSetting:true,backgroundHost:true);await Task.Delay(650);
            Evidence.Assert(h.Events().Length==0&&NoWatcher(h.Backend),"Background ignored SetUp authorization");
            var s=await h.Backend.LoadSettingsAsync();s.SetUp=true;await h.Backend.SaveSettingsAsync(s);
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(7));while((await h.Row(source))?.State!="done")await Task.Delay(50,stop.Token);
            await h.Backend.SetIndexingPausedAsync(true);h.HelpersGone();FactoryReceipts(h);h.WorkspaceEmpty();
            string other=h.Media(directory:Path.Combine(h.Root,"not-selected"));await h.Backend.SetIndexingPausedAsync(false);await Task.Delay(750);await h.Backend.SetIndexingPausedAsync(true);
            Evidence.Assert(h.MediaEvents().All(e=>!e.GetProperty("request").GetProperty("inputPath").GetString()!.Equals(other,StringComparison.OrdinalIgnoreCase)),"Background read unselected root");
        });
        if(native is null){missing++;results.Add(new{name="native/approved-host-model-free-wav",status="not-run",reason="Explicit approved host not supplied; optional decoder evidence missing"});Save();}
        else await Case("native/approved-host-model-free-wav",async h=>
        {string source=h.Media();await h.Open(native:native);await h.Index();BackendFixture.State(await h.Row(source),"done",true);Evidence.Assert(Directory.GetFiles(h.Audit,"*.native.pid.json").Length>=3,"No actual native decoder child receipts");h.WorkspaceEmpty();h.HelpersGone();});
        await Case("injected-fence/public-backend-sticky-not-OS-failure",async h=>
        {
            string source=h.Media(fixture:new(32,Mode:"hold-helper"));await h.Open();Task work=h.Index();await h.Wait("helper-held.json",work);
            object controller=typeof(DesktopBackend).GetField("indexing",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(h.Backend)!;
            var report=controller.GetType().GetMethod("ReportUnsettled",BindingFlags.Instance|BindingFlags.NonPublic);
            Evidence.Assert(report is not null,"Integrated settlement guard ReportUnsettled missing; integration required");
            var cause=new IOException("INJECTED_STATE_FENCE_ONLY RAW_CANARY "+h.Data);report!.Invoke(controller,[cause]);
            await Evidence.Throws(()=>h.Backend.SetIndexingPausedAsync(true),e=>e is IOException or InvalidOperationException or AggregateException);
            await Evidence.Throws(()=>work,e=>e is OperationCanceledException);h.HelpersGone();h.WorkspaceEmpty();Evidence.Released(source,h.Input);
            var status=await h.Backend.GetStatusAsync();Evidence.Assert(status.IndexingState==IndexingControlState.Pausing && status.Detail?.Contains("RAW_CANARY",StringComparison.Ordinal)!=true,"Injected fence lost stickiness or leaked raw cause");
            await Evidence.Throws(()=>h.Backend.SetIndexingPausedAsync(false),e=>e is IOException or InvalidOperationException or AggregateException);
            await Evidence.Throws(()=>h.Index(),e=>e is IOException or InvalidOperationException);
            await Evidence.Throws(async()=>await h.Backend.SaveSettingsAsync(await h.Backend.LoadSettingsAsync()),e=>e is InvalidOperationException);
            var shutdown=await Evidence.Throws(()=>h.Backend.DisposeAsync().AsTask(),e=>e is AggregateException);
            Evidence.Write(Path.Combine(h.Audit,"injected-fence.json"),new{kind="INJECTED controller signal; NOT real NativeMediaProcessClient Dispose/OS failure",shutdown=shutdown.ToString()});
        });
        // Retained pins cover shared ancestors. Keep physical retention tests LAST, never weaken rename oracles/delete unknown files.
        await Case("retention/dependency-plus-sample-cleanup-both-causes",async h=>
        {
            string source=h.Media();await h.Open(mode:"dependency-retain");var e=await Evidence.Throws(()=>h.Index(),x=>x.GetType().Name=="EmbeddingDependencyException");
            string diagnostic=e.ToString();Evidence.Assert(diagnostic.Contains("AggregateException",StringComparison.Ordinal)&&diagnostic.Contains("SYNTHETIC_UNTRUSTED",StringComparison.Ordinal)&&diagnostic.Contains("cleanup",StringComparison.OrdinalIgnoreCase),"Dependency and actual lease cleanup causes not both preserved");
            BackendFixture.Redacted(await h.Row(source),h);Evidence.Assert(Directory.GetFiles(Path.Combine(h.Data,"media-tasks"),"unknown.synthetic",SearchOption.AllDirectories).Length>0,"Retention erased unknown owned file");h.HelpersGone();
        });
        await Case("retention/NM-N2B2-LIFECYCLE-01/pause-must-not-converge",async h=>
        {
            string source=h.Media(fixture:new(32,Mode:"retain-helper"));await h.Open();Task work=h.Index();await h.Wait("helper-held.json",work);
            Exception? pauseError=null;try{await h.Backend.SetIndexingPausedAsync(true);}catch(Exception e){pauseError=e;}
            var indexError=await Evidence.Throws(()=>work,e=>e is AggregateException or OperationCanceledException or IOException);
            var status=await h.Backend.GetStatusAsync();Evidence.Write(Path.Combine(h.Audit,"lifecycle-retention-observation.json"),new{pauseError=pauseError?.ToString(),workError=indexError.ToString(),state=status.IndexingState.ToString(),status.Detail});
            h.HelpersGone();Evidence.Assert(Directory.GetFiles(Path.Combine(h.Data,"media-tasks"),"unknown.synthetic",SearchOption.AllDirectories).Length>0,"Retention evidence disappeared");
            Evidence.Assert(pauseError is not null&&status.IndexingState!=IndexingControlState.Paused,"NM-N2B2-LIFECYCLE-01: Pause reported settled despite real native cleanup/Dispose retention failure");
            await Evidence.Throws(()=>h.Backend.SetIndexingPausedAsync(false),e=>e is IOException or InvalidOperationException or AggregateException);
            await Evidence.Throws(()=>h.Index(),e=>e is InvalidOperationException or IOException);
            Evidence.Blocked(source);
            await Evidence.Throws(()=>h.Backend.DisposeAsync().AsTask(),e=>e is AggregateException);
        });
        Save();Console.WriteLine($"SUMMARY failed={failed} not-run={missing}; fake embeddings are not real-model evidence");
        return failed!=0?1:missing!=0?3:0;
    }
    private static IndexSegment[] ContentSegments(IndexedFile row)
    {
        Evidence.Assert(row.Segments.Where(s=>s.Kind=="name").All(s=>s.Modality=="none"&&s.Vector is null),"Repository name segment must never have a vector");
        return row.Segments.Where(s=>s.Kind!="name").ToArray();
    }
    private static bool NoWatcher(DesktopBackend b)=>typeof(DesktopBackend).GetField("background",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(b) is null;
    private static void FactoryReceipts(BackendFixture h)
    {
        var events=h.MediaEvents();Evidence.Assert(events.Length>0,"Default backend factory never invoked native CLI");
        foreach(var e in events)Evidence.Assert(e.GetProperty("requestPath").GetString()!.StartsWith(Path.Combine(h.Data,"media-tasks")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Workspace escaped task data/media-tasks");
        var starts=h.Events().Where(e=>e.GetProperty("phase").GetString()=="start").ToArray();
        Evidence.Assert(starts.Select(e=>e.GetProperty("executable").GetString()).Distinct(StringComparer.OrdinalIgnoreCase).Count()==1,"Model/media host capture mismatch");
        Evidence.Assert(h.Embeddings().Where(e=>e.GetProperty("op").GetString() is "audio" or "image").All(e=>e.GetProperty("inputs").EnumerateArray().All(x=>x.GetString()!.Contains(Path.Combine("data","media-tasks"),StringComparison.OrdinalIgnoreCase))),"Whole-file media fallback or wrong sample path");
    }
    private static async Task Interrupt(BackendFixture h,string hold,string action)
    {
        string source=h.Media(fixture:new(32,Mode:hold=="helper"?"hold-helper":"ok"));await h.Open(mode:hold=="embedding"?"hold-embedding":"ok");
        using var cancel=new CancellationTokenSource();Task work=h.Index(token:cancel.Token);var receipt=await h.Wait(hold+"-held.json",work);
        Evidence.Blocked(source);if(hold=="embedding")Evidence.Blocked(receipt.GetProperty("input").GetString()!);
        if(action=="pause")
        {
            Task pause=h.Backend.SetIndexingPausedAsync(true);
            while(!pause.IsCompleted){var s=await h.Backend.GetStatusAsync();if(s.IndexingState==IndexingControlState.Paused)h.HelpersGone();await Task.Delay(5);}
            await pause;await Evidence.Throws(()=>work,e=>e is OperationCanceledException);h.HelpersGone();
            Evidence.Assert((await h.Backend.GetStatusAsync()).IndexingState==IndexingControlState.Paused,"Pause failed to settle");
            await h.Backend.SetIndexingPausedAsync(false);Evidence.Assert(NoWatcher(h.Backend),"Resume opted into background");
            Evidence.Assert(!(await h.Backend.LoadSettingsAsync()).BackgroundIndexingEnabled,"Resume rewrote background authorization");
        }
        else if(action=="cancel"){cancel.Cancel();await Evidence.Throws(()=>work,e=>e is OperationCanceledException);h.HelpersGone();}
        else {await h.Backend.DisposeAsync();await Evidence.Throws(()=>work,e=>e is OperationCanceledException);Evidence.Gone(h.Audit);}
        h.WorkspaceEmpty();Evidence.Released(source,h.Input);
        if(hold=="embedding")Evidence.Assert(!File.Exists(receipt.GetProperty("input").GetString()),"Transferred sample lease survived settled await/cancel");
    }
}
