using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.IO.Compression;
using System.Text;
using WinSight.Infrastructure.Media;

internal static class PowerPointCases
{
    internal static async Task<int> RunAsync(string executable,string fixture,string root,string? busyFixture=null)
    {
        if(Directory.Exists(root))throw new IOException("Fresh task evidence required.");Directory.CreateDirectory(root);
        var outcomes=new List<object>();int passed=0,failed=0;
        void Check(string name,bool ok){outcomes.Add(new{name,ok});Console.WriteLine($"{(ok?"PASS":"FAIL")} {name}");if(ok)passed++;else failed++;}
        async Task Reject(string name,Func<Task> work){try{await work();Check(name,false);}catch(Exception e)when(e is ArgumentException or IOException or OperationCanceledException){Check(name,true);}}
        var workspace=Path.Combine(root,"workspace");Directory.CreateDirectory(workspace);
        var source=Path.Combine(root,"synthetic.pptx");File.Copy(fixture,source);var before=SHA256.HashData(File.ReadAllBytes(source));
        var client=new NativeMediaProcessClient(new(){HostExecutable=Path.GetFullPath(executable),WorkspaceRoot=workspace,RequestTimeout=TimeSpan.FromSeconds(60)});
        NativePresentationLease? batch=null;
        try
        {
            await Reject("Invalid maxSlide bound rejects before launch",async()=>{await using var _=await client.RenderPresentationAsync(new(source,33));});
            await Reject("Invalid edge rejects before launch",async()=>{await using var _=await client.RenderPresentationAsync(new(source,32,1601));});
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();await Reject("Pre-cancel rejects presentation work",async()=>{await using var _=await client.RenderPresentationAsync(new(source),cancel.Token);});}
            batch=await client.RenderPresentationAsync(new(source));
            Check("Real owned PowerPoint opens only copy and renders two slides",batch.State=="done"&&batch.SlideCount==2&&batch.Slides.Count==2);
            Check("Hidden slide retained with source ordinal",batch.Slides.Count==2&&batch.Slides[1].Number==2&&batch.Slides[1].Hidden);
            Check("Canonical full-slide dimensions",batch.Slides.All(s=>s.PixelWidth==1600&&s.PixelHeight==900));
            Check("PNG artifact paths not original source",batch.Slides.All(s=>Path.GetDirectoryName(s.ArtifactPath)!.StartsWith(workspace,StringComparison.OrdinalIgnoreCase)));
            Check("All owned PowerPoint processes physically ended",Process.GetProcessesByName("POWERPNT").Length==0);
            foreach(var slide in batch.Slides)
                await Reject($"Slide{slide.Number} immutable while leased",()=>{using var writer=new FileStream(slide.ArtifactPath,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);return Task.CompletedTask;});
            ProtocolCases(batch,source,Check);
            SafetyCases(fixture,Check);
            await using(var limited=await client.RenderPresentationAsync(new(source,1)))
                Check("Explicit slide cap is partial and correctly ordered",limited.State=="partial"&&limited.Reason=="slide-limit"&&limited.SlideCount==2&&limited.Slides.Count==1&&limited.Slides[0].Number==1);
            if(busyFixture is not null)
            {
                using var fake=Process.Start(new ProcessStartInfo(busyFixture){UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true})!;
                try
                {
                    var ready=await fake.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Check("Busy fixture readiness",ready=="READY");
                    await using var busy=await client.RenderPresentationAsync(new(source));
                    Check("Existing POWERPNT refuses COM activation and returns deferred",busy.State=="deferred"&&busy.Reason=="powerpoint-busy"&&busy.Slides.Count==0&&!fake.HasExited);
                }
                finally{if(!fake.HasExited)fake.Kill(entireProcessTree:true);if(!fake.WaitForExit(5000))throw new IOException("Busy fixture did not settle.");}
            }
            using(var cancel=new CancellationTokenSource())
            {
                var running=client.RenderPresentationAsync(new(source),cancel.Token);var watch=Stopwatch.StartNew();bool found=false;
                while(watch.Elapsed<TimeSpan.FromSeconds(10)&&!running.IsCompleted)
                {var processes=Process.GetProcessesByName("POWERPNT");try{found=processes.Length!=0;}finally{foreach(var p in processes)p.Dispose();}if(found)break;await Task.Delay(20);}
                Check("In-flight cancel reached an actual owned PowerPoint process",found);cancel.Cancel();
                await Reject("In-flight cancellation settles caller",async()=>{await using var _=await running;});
                Check("In-flight cancel physically ended PowerPoint tree",Process.GetProcessesByName("POWERPNT").Length==0);
            }
            await client.DisposeAsync();
            Check("Batch pins outlive client disposal",batch.Slides.All(s=>File.Exists(s.ArtifactPath)));
            Check("Original PPTX bytes unchanged",before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))));
        }
        finally{if(batch is not null)await batch.DisposeAsync();await client.DisposeAsync();}
        Check("Owned task workspace empty after batch disposal",!Directory.EnumerateFileSystemEntries(workspace).Any());
        await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{passed,failed,scope="Production Host/Job/PowerPoint readonlytaskcopy→normal+hiddenPNG→strictparentPNGvalidation/pins/cleanup; ownfixtureonly, no embeddings, GUI, actualuserindexes or Release",outcomes},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PowerPoint cases {passed}/{passed+failed}");return failed==0?0:1;
    }
    private static void ProtocolCases(NativePresentationLease batch,string source,Action<string,bool> check)
    {
        var parse=typeof(NativePresentationLease).GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic)!;
        var files=typeof(NativePresentationLease).GetField("files",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(batch);
        var original=new JsonObject{["protocol"]=1,["inputPath"]=source,["state"]="done",["reason"]="",["officeVersion"]=batch.OfficeVersion,["slideCount"]=batch.SlideCount,["renderPolicy"]="powerpoint-slide-export-png-v1",["slides"]=new JsonArray(batch.Slides.Select(s=>(JsonNode)new JsonObject{["number"]=s.Number,["width"]=s.PixelWidth,["height"]=s.PixelHeight,["hidden"]=s.Hidden,["artifact"]=Path.GetFileName(s.ArtifactPath)}).ToArray())};
        void Bad(string name,Action<JsonObject> mutate)
        {
            var value=original.DeepClone().AsObject();mutate(value);
            try{parse.Invoke(null,[value.ToJsonString(),new NativePresentationRequest(source),files,CancellationToken.None]);check(name,false);}
            catch(TargetInvocationException e)when(e.InnerException is InvalidDataException or IOException or JsonException or ArgumentException or InvalidOperationException){check(name,true);}
        }
        Bad("Protocol unknown member rejected",o=>o["surprise"]=true);
        Bad("Protocol old version rejected",o=>o["protocol"]=0);
        Bad("Protocol source mismatch rejected",o=>o["inputPath"]=source+".other");
        Bad("Protocol wrong canonical width rejected",o=>o["slides"]![0]!["width"]=1500);
        Bad("Protocol duplicate slide ordinal rejected",o=>o["slides"]![1]!["number"]=1);
        Bad("Protocol artifact traversal rejected",o=>o["slides"]![0]!["artifact"]="../slide.png");
        Bad("Protocol invalid hidden flag rejected",o=>o["slides"]![0]!["hidden"]="false");
        Bad("Protocol false complete coverage rejected",o=>o["slideCount"]=3);
        Bad("Protocol false slide-limit partial rejected",o=>{o["state"]="partial";o["reason"]="slide-limit";});
        Bad("Protocol deferred payload cannot contain rasters",o=>{o["state"]="deferred";o["reason"]="powerpoint-busy";});
        Bad("Protocol missing slides rejected",o=>o.Remove("slides"));
        var duplicate=original.ToJsonString();duplicate=duplicate.Insert(1,"\"protocol\":1,");
        try{parse.Invoke(null,[duplicate,new NativePresentationRequest(source),files,CancellationToken.None]);check("Protocol duplicate root key rejected",false);}
        catch(TargetInvocationException e)when(e.InnerException is InvalidDataException or IOException or JsonException){check("Protocol duplicate root key rejected",true);}
    }
    private static void SafetyCases(string fixture,Action<string,bool> check)
    {
        var method=typeof(NativeMediaProcessClient).Assembly.GetType("WinSight.Infrastructure.Media.PowerPointPolicy")!.GetMethod("ValidatePackage",BindingFlags.Static|BindingFlags.NonPublic)!;
        void Bad(string name,Action<ZipArchive> mutate)
        {
            using var memory=new MemoryStream();using(var input=File.OpenRead(fixture))input.CopyTo(memory);
            using(var zip=new ZipArchive(memory,ZipArchiveMode.Update,leaveOpen:true))mutate(zip);memory.Position=0;
            try{method.Invoke(null,[memory,CancellationToken.None]);check(name,false);}
            catch(TargetInvocationException e)when(e.InnerException is NotSupportedException or InvalidDataException or IOException or System.Xml.XmlException){check(name,true);}
        }
        static void Entry(ZipArchive zip,string name,string value){using var writer=new StreamWriter(zip.CreateEntry(name).Open(),Encoding.UTF8);writer.Write(value);}
        Bad("ActiveX parts rejected before Office",z=>Entry(z,"ppt/activeX/activeX1.xml","<x/>"));
        Bad("Embedded objects rejected before Office",z=>Entry(z,"ppt/embeddings/obj.bin","own fixture"));
        Bad("SVG visual restriction explicit",z=>Entry(z,"ppt/media/icon.svg","<svg/>"));
        Bad("Ambiguous package duplicate rejected",z=>Entry(z,"PPT/PRESENTATION.XML","<x/>"));
        Bad("Package traversal rejected",z=>Entry(z,"../escape.xml","<x/>"));
        Bad("DTD rejected before Office",z=>Entry(z,"ppt/extra.xml","<!DOCTYPE x [<!ENTITY test \"own\">]><x>&test;</x>"));
        Bad("External relationships rejected before Office",z=>Entry(z,"ppt/_rels/external.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"image\" TargetMode=\"External\" Target=\"https://example.invalid/never-fetch\"/></Relationships>"));
    }
}
