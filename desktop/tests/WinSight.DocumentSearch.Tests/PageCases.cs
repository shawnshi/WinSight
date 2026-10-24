using System.Globalization;
using System.Text;
using System.IO.Compression;
using Windows.Graphics.Imaging;
using Windows.Storage;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using WinSight.Core;
using WinSight.Infrastructure.Storage;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Media;

internal static class PageCases
{
    internal static async Task<int> RunAsync(string executable, string root)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(root) || Directory.Exists(root))
            throw new ArgumentException("Use an absolute Host path and NEW absolute page-case directory.");
        Directory.CreateDirectory(root);
        await using var repository = new SqliteIndexRepository(Path.Combine(root,"geometry.sqlite"));
        await repository.InitializeAsync();
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        await using var indexedRepository = new SqliteIndexRepository(Path.Combine(root,"paged-index.sqlite"));
        await indexedRepository.InitializeAsync();
        var sourceRoot=Directory.CreateDirectory(Path.Combine(root,"sources")).FullName;
        var pdf = MakePdf(sourceRoot); var before = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(pdf));
        var results = new List<object>();
        void Check(string name, bool ok)
        {
            results.Add(new { name, pass = ok }); Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) throw new InvalidOperationException(name);
        }
        async Task Reject(string name, Func<Task> call, Type errorType)
        {
            try { await call(); } catch (Exception error) when (errorType.IsAssignableFrom(error.GetType())) { Check(name, true); return; }
            Check(name, false);
        }
        var client = new NativeMediaProcessClient(new() { HostExecutable = executable, WorkspaceRoot = workspace, RequestTimeout = TimeSpan.FromSeconds(30) });
        var first = await client.RenderPdfPageAsync(new(pdf, 1));
        Check("PDF portrait canonical pixels and page count", first.PageCount == 2 && first.PageNumber == 1 && first.PixelHeight == 1600 && first.PixelWidth < 1600);
        Check("Native PDF raster policy", first.RenderPolicy == "winrt-pdf-dip-normalized-v1" && File.Exists(first.ArtifactPath));
        bool protectedWrite = false;
        try { using var write = new FileStream(pdf, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); } catch (IOException) { protectedWrite = true; }
        Check("Lease pins original PDF against writes", protectedWrite);
        var firstPath = first.ArtifactPath;
        await using (var second = await client.RenderPdfPageAsync(new(pdf, 2)))
            Check("PDF rotated page respects native display orientation", second.PageNumber == 2 && second.PixelWidth == 1600 && second.PixelHeight < 1600);
        Check("Other returned lease remains alive", File.Exists(firstPath));
        await Reject("PDF page zero rejected", async () => { await using var _ = await client.RenderPdfPageAsync(new(pdf, 0)); }, typeof(ArgumentOutOfRangeException));
        await Reject("PDF page beyond source is failure", async () => { await using var _ = await client.RenderPdfPageAsync(new(pdf, 3)); }, typeof(IOException));
        await Reject("PDF edge below bound rejected", async () => { await using var _ = await client.RenderPdfPageAsync(new(pdf, 1, 255)); }, typeof(ArgumentOutOfRangeException));
        await Reject("PDF edge above bound rejected", async () => { await using var _ = await client.RenderPdfPageAsync(new(pdf, 1, 2601)); }, typeof(ArgumentOutOfRangeException));
        await Reject("Original video frame edge contract not relaxed", async () => { await using var _ = await client.ExtractAsync(new(pdf, "frame", 0, null, 1600)); }, typeof(ArgumentException));
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            await Reject("Pre-cancel prevents a page operation", async () => { await using var _ = await client.RenderPdfPageAsync(new(pdf, 1), cancel.Token); }, typeof(OperationCanceledException));
        }
        var large = Path.Combine(root, "oversize.pdf");
        using (var file = new FileStream(large, FileMode.CreateNew, FileAccess.Write)) file.SetLength(33L * 1024 * 1024);
        await Reject("PDF source byte bound preserved", async () => { await using var _ = await client.RenderPdfPageAsync(new(large, 1)); }, typeof(IOException));
        var inspected = await client.InspectImageAsync(firstPath);
        Check("Actual OCR text and line regions on rendered PDF", inspected.OcrText.Contains("portrait",StringComparison.OrdinalIgnoreCase) && inspected.OcrRegions.Count>0);
        Check("OCR regions preserve their actual recognized line text",inspected.OcrRegions.All(r=>!string.IsNullOrWhiteSpace(r.Text)&&inspected.OcrText.Contains(r.Text,StringComparison.Ordinal)));
        Check("OCR boxes are bounded normalized display coordinates", inspected.OcrRegions.All(r=>r.Left>=0&&r.Top>=0&&r.Width>0&&r.Height>0&&r.Left+r.Width<=1+1e-9&&r.Top+r.Height<=1+1e-9));
        var reply=JsonSerializer.SerializeToNode(new{protocol=3,sourcePath=inspected.SourcePath,width=inspected.Width,height=inspected.Height,orientation=inspected.Orientation,frameCount=inspected.FrameCount,
            exif=inspected.Exif,ocrText=inspected.OcrText,ocrLanguages=inspected.OcrLanguages,ocrWidth=inspected.OcrWidth,ocrHeight=inspected.OcrHeight,state=inspected.State,detail=inspected.Detail,
            ocrEngine=inspected.OcrEngine,ocrEngineVersion=inspected.OcrEngineVersion,ocrModelProfile=inspected.OcrModelProfile,ocrRegions=inspected.OcrRegions.Select(r=>new{line=r.Line,start=inspected.OcrText.IndexOf(r.Text!,StringComparison.Ordinal),length=r.Text!.Length,left=r.Left,top=r.Top,width=r.Width,height=r.Height}),ocrRegionsTruncated=inspected.OcrRegionsTruncated})!.AsObject();
        var parse=typeof(NativeImageInspection).GetMethod("Parse",BindingFlags.NonPublic|BindingFlags.Static)!;
        foreach(var invalid in new[]{"old-protocol","region-outside","duplicate-line","truncated-done","unknown-region"})
        {
            var bad=reply.DeepClone().AsObject();
            if(invalid=="old-protocol")bad["protocol"]=2;
            if(invalid=="region-outside")bad["ocrRegions"]![0]!["width"]=2;
            if(invalid=="duplicate-line"){var copy=bad["ocrRegions"]![0]!.DeepClone();bad["ocrRegions"]!.AsArray().Add(copy);}
            if(invalid=="truncated-done"){bad["ocrRegionsTruncated"]=true;bad["state"]="done";bad["detail"]=null;}
            if(invalid=="unknown-region")bad["ocrRegions"]![0]!["extra"]=1;
            bool rejected=false;
            try{parse.Invoke(null,[bad.ToJsonString(),firstPath]);}
            catch(TargetInvocationException error)when(error.InnerException is InvalidDataException){rejected=true;}
            Check("Geometry protocol rejects "+invalid,rejected);
        }
        {
            await repository.UpsertFileAsync(new(0,pdf,"pdf",1,1,"done",null,null,
                [new("page","text",1,null,inspected.OcrText,Source:new(new("pdf-page",Page:1),"ocr",Regions:inspected.OcrRegions))]));
            Check("Geometry survives source-anchor SQLite roundtrip",(await repository.GetFileAsync(pdf))!.Segments.Any(s=>s.Source?.Regions?.SequenceEqual(inspected.OcrRegions)==true));
        }
        var scannedPdf = await MakeScannedPdfAsync(sourceRoot, firstPath);
        var layeredPdf = await MakeScannedPdfAsync(sourceRoot, firstPath, string.Concat(Enumerable.Repeat("existing searchable OCR layer ",15)));
        var layerText = await new ContentExtractor().ExtractAsync(layeredPdf,false);
        Check("Raster plus text-layer fixture has substantial extractable text",layerText.Segments.Sum(s=>s.Excerpt?.Length??0)>=200);
        var scanText = await new ContentExtractor().ExtractAsync(scannedPdf, false);
        Check("Raster-only PDF fixture has no extractable text", scanText.Segments.Count == 0 && scanText.State == "partial");
        await using var pageEmbeddings = new PageEmbeddings();
        var documentType=typeof(IndexingService).Assembly.GetType("WinSight.Infrastructure.Indexing.NativeDocumentPages")!;
        var documentService=Activator.CreateInstance(documentType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[client],null)!;
        var constructor=typeof(IndexingService).GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single(c=>c.GetParameters().Length==5);
        var indexer=(IndexingService)constructor.Invoke([indexedRepository,new ContentExtractor(),pageEmbeddings,null,documentService]);
        await indexer.IndexAsync(new([sourceRoot]));
        var indexed=(await indexedRepository.GetFileAsync(pdf))!;
        Check("Actual PDF pipeline keeps independent body visual OCR vectors",indexed.State=="done"
            &&indexed.Segments.Count(s=>s.Vector is not null&&s.Source?.Channel=="page-visual")==2
            &&indexed.Segments.Count(s=>s.Vector is not null&&s.Source?.Channel=="ocr")>=2
            &&indexed.Segments.Count(s=>s.Vector is not null&&s.Source?.Channel=="body")==2);
        var scannedIndex=(await indexedRepository.GetFileAsync(scannedPdf))!;
        Check("Raster-only PDF becomes actual visual plus located OCR",scannedIndex.State=="done" && scannedIndex.Segments.Any(s=>s.Source?.Channel=="page-visual"&&s.Vector is not null)
            &&scannedIndex.Segments.Any(s=>s.Source?.Channel=="ocr"&&s.Source.Regions?.Count>0&&s.Excerpt!.Contains("portrait",StringComparison.OrdinalIgnoreCase)));
        var layeredIndex=(await indexedRepository.GetFileAsync(layeredPdf))!;
        Check("Scan with an existing text layer still gets located visual OCR",layeredIndex.State=="done"&&layeredIndex.Segments.Any(s=>s.Source?.Channel=="body")
            &&layeredIndex.Segments.Any(s=>s.Source?.Channel=="ocr"&&s.Source.Regions?.Count>0));
        Check("Page geometry persists on actual indexed OCR units",indexed.Segments.Where(s=>s.Source?.Channel=="ocr").All(s=>s.Source!.Regions!.Count>0));
        Check("Temporary page artifact paths never persist",!indexed.Info!.Contains(workspace,StringComparison.OrdinalIgnoreCase)&&!indexed.Segments.Any(s=>s.Excerpt?.Contains("media-",StringComparison.OrdinalIgnoreCase)==true));
        int calls=pageEmbeddings.Calls;
        await indexer.IndexAsync(new([sourceRoot]));
        Check("Unchanged PDF native revision skips repeat processing",pageEmbeddings.Calls==calls);
        var located=await new SearchService(indexedRepository).SearchAsync(new("portrait",Mode:SearchMode.Keyword));
        Check("Indexed PDF keyword hit retains page and geometry",located.Hits.Any(h=>h.Source?.Location.Page==1&&h.Source.Regions?.Count>0));
        await client.DisposeAsync();
        Check("Client disposal does not consume returned page lease", File.Exists(firstPath));
        await first.DisposeAsync();
        Check("Page lease releases only task artifacts", !File.Exists(firstPath) && Directory.GetDirectories(workspace).Length == 0 && File.Exists(pdf));
        var after = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(pdf));
        Check("Original source bytes unchanged", before.SequenceEqual(after));
        using (var write = new FileStream(pdf, FileMode.Open, FileAccess.Write, FileShare.None)) Check("Original source pin released after lease", write.CanWrite);
        await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = results.Count, failed = 0,
            scope = "Actual Windows PDF render and RapidOCR text/regions, production Job/client/PNG/lease and synthetic SQLite geometry; no embedding model, real user index, GUI or Office operations", results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Native page cases {results.Count}/{results.Count}"); return 0;
    }
    private static async Task<string> MakeScannedPdfAsync(string folder,string png,string? hiddenText=null)
    {
        var file=await StorageFile.GetFileFromPathAsync(png);using var input=await file.OpenAsync(FileAccessMode.Read);
        var decoder=await BitmapDecoder.CreateAsync(input);
        var pixels=(await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8,BitmapAlphaMode.Ignore,new BitmapTransform(),ExifOrientationMode.IgnoreExifOrientation,ColorManagementMode.DoNotColorManage)).DetachPixelData();
        using var compressed=new MemoryStream();
        using(var zlib=new ZLibStream(compressed,CompressionLevel.Optimal,true))
        {
            for(int i=0;i<pixels.Length;i+=4)zlib.Write(pixels.AsSpan(i,3));
        }
        var image=compressed.ToArray();
        var content="q 612 0 0 792 0 0 cm /Im1 Do Q"+(hiddenText is null?"":$" BT 3 Tr /F1 12 Tf 30 500 Td ({hiddenText}) Tj ET");
        var objects=new byte[][]{
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 4 0 R >> /Font << /F1 6 0 R >> >> /Contents 5 0 R >>"),
            Encoding.ASCII.GetBytes($"<< /Type /XObject /Subtype /Image /Width {decoder.PixelWidth} /Height {decoder.PixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {image.Length} >>\\nstream\\n".Replace("\\n","\n")).Concat(image).Concat(Encoding.ASCII.GetBytes("\nendstream")).ToArray(),
            Encoding.ASCII.GetBytes($"<< /Length {content.Length} >>\nstream\n{content}\nendstream"),Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
        };
        using var output=new MemoryStream();
        void Write(string s)=>output.Write(Encoding.ASCII.GetBytes(s));
        Write("%PDF-1.4\n");var offsets=new List<long>{0};
        for(int i=0;i<objects.Length;i++){offsets.Add(output.Position);Write($"{i+1} 0 obj\n");output.Write(objects[i]);Write("\nendobj\n");}
        long xref=output.Position;Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach(long offset in offsets.Skip(1))Write(offset.ToString("D10",CultureInfo.InvariantCulture)+" 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path=Path.Combine(folder,hiddenText is null?"raster-only.pdf":"raster-with-text-layer.pdf");await File.WriteAllBytesAsync(path,output.ToArray());return path;
    }
    private sealed class PageEmbeddings : IEmbeddingService
    {
        internal int Calls;
        public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new EmbeddingInfo("synthetic-page-model",768,"synthetic","fixture","fixture","cpu","synthetic",["text","image"]));
        public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs,CancellationToken cancellationToken=default)
        {
            var output=new List<EmbeddingResult>();
            foreach(var input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();Calls++;
                if(input.Operation=="image"&&!File.Exists(input.Value))throw new IOException("Page PNG lease did not survive through embedding.");
                var vector=new float[768];vector[input.Operation=="image"?1:0]=1;output.Add(new(vector));
            }
            return Task.FromResult<IReadOnlyList<EmbeddingResult>>(output);
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static string MakePdf(string folder)
    {
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [4 0 R 6 0 R] /Count 2 >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>", Stream("portrait source page"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate 90 /Resources << /Font << /F1 3 0 R >> >> /Contents 7 0 R >>", Stream("rotated source page") };
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); var path = Path.Combine(folder, "synthetic.pdf"); File.WriteAllText(path, pdf.ToString(), Encoding.ASCII); return path;
    }
    private static string Stream(string text)
    {
        var content = $"BT /F1 24 Tf 60 650 Td ({text}) Tj ET";
        return $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream";
    }
}
