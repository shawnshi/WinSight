using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Media;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;

internal static class PowerPointIndexCases
{
    internal static async Task<int> RunAsync(string executable,string fixture,string root)
    {
        if(Directory.Exists(root))throw new IOException("New evidence directory required.");Directory.CreateDirectory(root);
        var checks=new List<object>();int failed=0;
        void Check(string name,bool ok){checks.Add(new{name,ok});Console.WriteLine($"{(ok?"PASS":"FAIL")} {name}");if(!ok)failed++;}
        var sourceRoot=Path.Combine(root,"sources");Directory.CreateDirectory(sourceRoot);
        var regular=Path.Combine(sourceRoot,"regular.pptx");File.Copy(fixture,regular);
        var raster=Path.Combine(sourceRoot,"raster.pptx");File.Copy(fixture,raster);MakeRaster(raster,Path.Combine(Path.GetDirectoryName(fixture)!,"slide-0001.png"));
        var unsafePath=Path.Combine(sourceRoot,"external.pptx");File.Copy(fixture,unsafePath);External(unsafePath);
        var extraction=await new ContentExtractor().ExtractAsync(raster,false);
        Check("Image-only PPTX has zero lexical text before native path",extraction.Segments.Count==0&&extraction.State=="skipped");
        await using var repo=new SqliteIndexRepository(Path.Combine(root,"index.sqlite"));await repo.InitializeAsync();
        var workspace=Path.Combine(root,"workspace");Directory.CreateDirectory(workspace);
        await using var client=new NativeMediaProcessClient(new(){HostExecutable=executable,WorkspaceRoot=workspace});await using var embeddings=new Vectors();
        var type=typeof(IndexingService).Assembly.GetType("WinSight.Infrastructure.Indexing.NativePresentationPages")!;
        var service=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[client],null)!;
        var ctor=typeof(IndexingService).GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single(c=>c.GetParameters().Length==6);
        var indexer=(IndexingService)ctor.Invoke([repo,new ContentExtractor(),embeddings,null,null,service]);
        Check("Office automation defaults off",!new IndexRequest([sourceRoot]).AllowPowerPointAutomation);
        await indexer.IndexAsync(new([sourceRoot]));
        var deferred=(await repo.GetFileAsync(regular))!;
        Check("Background-style request defers PowerPoint without image embeddings",deferred.State=="partial"&&deferred.Info!.Contains("manual-index-required",StringComparison.Ordinal)&&embeddings.ImageCalls==0);
        int backgroundCalls=embeddings.Calls;await indexer.IndexAsync(new([sourceRoot]));
        Check("Unchanged background deferral does not re-embed body",embeddings.Calls==backgroundCalls);
        await indexer.IndexAsync(new([sourceRoot],AllowPowerPointAutomation:true));
        var text=(await repo.GetFileAsync(regular))!;var image=(await repo.GetFileAsync(raster))!;var unsafeRow=(await repo.GetFileAsync(unsafePath))!;
        Check("Text PPTX complete native visual coverage",text.State=="done"&&text.Segments.Count(s=>s.Source?.Channel=="page-visual"&&s.Vector is not null)==2);
        Check("Image-only PPTX enters native path despite lexical skipped",image.State=="done"&&image.Segments.Count(s=>s.Source?.Channel=="page-visual")==2);
        Check("Actual RapidOCR text and line geometry on image-only slides",image.Segments.Any(s=>s.Source?.Channel=="ocr"&&s.Source.Regions?.Count>0&&s.Excerpt?.Contains("WinSight",StringComparison.OrdinalIgnoreCase)==true));
        Check("All content units use slide identity, not PDF page identity",text.Segments.Concat(image.Segments).Where(s=>s.Kind!="name").All(s=>s.Source?.Location.Type=="slide"));
        Check("Automatic filename units are not falsely labeled as slides",text.Segments.Concat(image.Segments).Where(s=>s.Kind=="name").All(s=>s.Source?.Location.Type!="slide"));
        Check("Hidden slide2 retains its ordinal and full visual vector",text.Segments.Any(s=>s.Source?.Location.Page==2&&s.Source.Channel=="page-visual"&&s.Vector?[1]==1));
        Check("Body OCR and image vectors independent, never averaged",text.Segments.Where(s=>s.Source?.Channel=="page-visual").All(s=>s.Vector?[1]==1&&s.Vector[0]==0)&&text.Segments.Any(s=>s.Source?.Channel=="body"&&s.Vector?[0]==1));
        Check("External-link PPTX keeps text, explicitly defers unsafe visuals",unsafeRow.State=="partial"&&unsafeRow.Segments.Any(s=>s.Source?.Channel=="body")&&!unsafeRow.Segments.Any(s=>s.Source?.Channel=="page-visual")&&unsafeRow.Info!.Contains("unsupported-content",StringComparison.Ordinal));
        Check("No temporary render paths persist",!text.Info!.Contains(workspace,StringComparison.OrdinalIgnoreCase)&&!image.Info!.Contains(workspace,StringComparison.OrdinalIgnoreCase));
        var hits=await new SearchService(repo).SearchAsync(new("hidden",Mode:SearchMode.Keyword));
        Check("Keyword hit keeps actual slide2 location",hits.Hits.Any(h=>h.Path==regular&&h.Source?.Location is {Type:"slide",Page:2}));
        int calls=embeddings.Calls;await indexer.IndexAsync(new([sourceRoot],AllowPowerPointAutomation:true));
        Check("Unchanged native revision and permanent-unsupported partial skip embeddings",embeddings.Calls==calls);
        Check("Task workspace completely released after indexing",!Directory.EnumerateFileSystemEntries(workspace).Any());
        Check("PowerPoint processes physically ended",System.Diagnostics.Process.GetProcessesByName("POWERPNT").Length==0);
        await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{passed=checks.Count-failed,failed,scope="Production PowerPoint→canonicalPNG→actualRapidOCR→independent synthetic768D embedding→SQLiteFP16→slide located search; ownregular/imageonly/external fixtures, not ranking quality/GUI/realuserindex/Release",checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PowerPoint indexing {checks.Count-failed}/{checks.Count}");return failed==0?0:1;
    }
    private static readonly XNamespace P="http://schemas.openxmlformats.org/presentationml/2006/main",A="http://schemas.openxmlformats.org/drawingml/2006/main",R="http://schemas.openxmlformats.org/officeDocument/2006/relationships",Rel="http://schemas.openxmlformats.org/package/2006/relationships",Ct="http://schemas.openxmlformats.org/package/2006/content-types";
    private static XDocument Read(ZipArchive z,string name){using var stream=z.GetEntry(name)!.Open();return XDocument.Load(stream);}
    private static void Put(ZipArchive z,string name,XDocument xml){z.GetEntry(name)?.Delete();using var stream=z.CreateEntry(name).Open();xml.Save(stream);}
    private static void External(string path)
    {
        using var z=ZipFile.Open(path,ZipArchiveMode.Update);var name="ppt/slides/_rels/slide1.xml.rels";var xml=Read(z,name);
        xml.Root!.Add(new XElement(Rel+"Relationship",new XAttribute("Id","rExternalFixture"),new XAttribute("Type",R.NamespaceName+"/hyperlink"),new XAttribute("TargetMode","External"),new XAttribute("Target","https://example.invalid/never-fetched")));Put(z,name,xml);
    }
    private static void MakeRaster(string path,string png)
    {
        using var z=ZipFile.Open(path,ZipArchiveMode.Update);
        for(int n=1;n<=2;n++)
        {
            string name=$"ppt/slides/slide{n}.xml";var xml=Read(z,name);foreach(var text in xml.Descendants(A+"t"))text.Value="";
            xml.Descendants(P+"spTree").Single().Add(new XElement(P+"pic",
                new XElement(P+"nvPicPr",new XElement(P+"cNvPr",new XAttribute("id",99),new XAttribute("name","Own raster fixture")),new XElement(P+"cNvPicPr"),new XElement(P+"nvPr")),
                new XElement(P+"blipFill",new XElement(A+"blip",new XAttribute(R+"embed","rOwnImage")),new XElement(A+"stretch",new XElement(A+"fillRect"))),
                new XElement(P+"spPr",new XElement(A+"xfrm",new XElement(A+"off",new XAttribute("x",0),new XAttribute("y",0)),new XElement(A+"ext",new XAttribute("cx",12192000),new XAttribute("cy",6858000))),new XElement(A+"prstGeom",new XAttribute("prst","rect"),new XElement(A+"avLst")))));Put(z,name,xml);
            string rel=$"ppt/slides/_rels/slide{n}.xml.rels";var links=Read(z,rel);links.Root!.Add(new XElement(Rel+"Relationship",new XAttribute("Id","rOwnImage"),new XAttribute("Type",R.NamespaceName+"/image"),new XAttribute("Target","../media/own.png")));Put(z,rel,links);
        }
        using(var input=File.OpenRead(png))using(var output=z.CreateEntry("ppt/media/own.png").Open())input.CopyTo(output);
        var types=Read(z,"[Content_Types].xml");if(!types.Root!.Elements(Ct+"Default").Any(e=>(string?)e.Attribute("Extension")=="png"))types.Root.Add(new XElement(Ct+"Default",new XAttribute("Extension","png"),new XAttribute("ContentType","image/png")));Put(z,"[Content_Types].xml",types);
    }
    private sealed class Vectors:IEmbeddingService
    {
        internal int Calls,ImageCalls;
        public Task<EmbeddingInfo> GetInfoAsync(CancellationToken token=default)=>Task.FromResult(new EmbeddingInfo("synthetic-presentation",768,"fixture","fixture","fixture","cpu","synthetic",["text","image"]));
        public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs,CancellationToken token=default)
        {
            IReadOnlyList<EmbeddingResult> values=inputs.Select(i=>{token.ThrowIfCancellationRequested();Calls++;if(i.Operation=="image")ImageCalls++;if(i.Operation=="image"&&!File.Exists(i.Value))throw new IOException("Batch lease was released early.");var v=new float[768];v[i.Operation=="image"?1:0]=1;return new EmbeddingResult(v);}).ToArray();return Task.FromResult(values);
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
