using System.Diagnostics;
using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;

if (await GitProcessFixture.TryRunAsync(args)) return;

var suite = new Suite();
await suite.RunAsync();

internal sealed partial class Suite
{
    private readonly string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
    private int passed;
    private string Folder(string name) { var p = Path.Combine(fixtures, name); Directory.CreateDirectory(p); return p; }
    private static string Put(string folder, string name, string value = "The searchable document describes reliable migration and indexing.")
    { var path = Path.Combine(folder, name); File.WriteAllText(path, value); return path; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private async Task Test(string name, Func<Task> test)
    { await test(); passed++; Console.WriteLine("PASS " + name); }
    private static async Task Throws<T>(Func<Task> test) where T : Exception
    { try { await test(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    internal async Task RunAsync()
    {
        Directory.CreateDirectory(fixtures);
        await Test("text/Markdown full text, prompts, metadata, incremental and Force", async () =>
        {
            var root = Folder("documents"); var text = Put(root, "notes.md", "# Heading\nThe needlekeyword is retained with full document text.");
            var repo = new FakeRepository(); var model = new FakeEmbedding(); var count = new CountingExtractor(new ContentExtractor());
            var index = new IndexingService(repo, count, model);
            await index.IndexAsync(new([root]));
            var row = repo.Files[text];
            Check(row.State == "done" && row.Segments.Single().Vector is not null, "Document should have valid vector.");
            Check(row.Segments.Single().Excerpt!.Contains("needlekeyword"), "Keyword text missing.");
            Check(model.Inputs.Single().Operation == "text" && model.Inputs.Single().Value.StartsWith("title: notes | text: "), "Document prompt mismatch.");
            Check(repo.Meta["profile_id"] == "synthetic-v1" && repo.Meta["embedding_info"].Contains("Synthetic"), "Model metadata missing.");
            Check(repo.Events.Contains("pending") && repo.Events.Contains("done"), "Pending transition missing.");
            await index.IndexAsync(new([root])); Check(count.Calls == 1, "Unchanged file was reextracted.");
            Put(root, "notes.md", "Modified content is deliberately longer than the old text to invalidate size and mtime.");
            await index.IndexAsync(new([root])); Check(count.Calls == 2, "Changed file not reextracted.");
            await index.IndexAsync(new([root], Force: true)); Check(count.Calls == 3, "Force did not reextract.");
        });
        await Test("exclusions, normalized extension filters, nested chosen-root override and sibling boundary", async () =>
        {
            var root = Folder("selection"); var excluded = Path.Combine(root, "leave"); Directory.CreateDirectory(excluded);
            var child = Path.Combine(excluded, "chosen"); Directory.CreateDirectory(child);
            var ignored = Put(excluded, "secret-not-key.txt"); var chosen = Put(child, "visible.txt"); var filtered = Put(root, "skip.MD"); var keep = Put(root, "keep.txt");
            var sibling = Folder("selection-sibling"); var outside = Put(sibling, "outside.txt");
            var repo = new FakeRepository(); var index = new IndexingService(repo, new ContentExtractor(), new FakeEmbedding());
            await index.IndexAsync(new([root, child], [excluded], ["*.mD, .PDF"]));
            Check(repo.Files.ContainsKey(chosen) && repo.Files.ContainsKey(keep), "Nested explicit root should override parent skip.");
            Check(!repo.Files.ContainsKey(ignored) && !repo.Files.ContainsKey(filtered) && !repo.Files.ContainsKey(outside), "Exclusion/type/boundary leaked.");
        });
        await Test("oversize skip, empty skip, no embedding downgrade to done", async () =>
        {
            var root = Folder("limits"); var big = Put(root, "big.txt", new string('a', 100)); var empty = Put(root, "empty.txt", "");
            var repo = new FakeRepository(); var count = new CountingExtractor(new ContentExtractor());
            await new IndexingService(repo, count).IndexAsync(new([root], MaxFileBytes: 50));
            Check(repo.Files[big].State == "skipped" && repo.Files[big].Detail!.Contains("50"), "Oversize should be explicit.");
            Check(repo.Files[empty].State == "skipped" && count.Calls == 1, "Empty or oversize extraction wrong.");
            Put(root, "short.txt", "Meaningful words.");
            await new IndexingService(repo, count).IndexAsync(new([root], MaxFileBytes: 50));
            Check(repo.Files[Path.Combine(root, "short.txt")].State == "partial", "No model should remain keyword-only partial.");
        });
        await Test("numeric-only keywords intentionally need no semantic vector", async () =>
        {
            var root = Folder("numbers"); var p = Put(root, "table.txt", "12345 67890\n11111 22222"); var repo = new FakeRepository(); var model = new FakeEmbedding();
            await new IndexingService(repo, new ContentExtractor(), model).IndexAsync(new([root]));
            Check(repo.Files[p].State == "done" && model.Inputs.Count == 0 && repo.Files[p].Segments[0].Excerpt!.Contains("12345"), "Numeric-only behavior mismatch.");
        });
        await Test("cancel after pending propagates, preserves pending and prevents deletion", async () =>
        {
            var root = Folder("cancel"); var old = Put(root, "old.txt"); var repo = new FakeRepository();
            await new IndexingService(repo, new ContentExtractor(), new FakeEmbedding()).IndexAsync(new([root]));
            File.Delete(old); var next = Put(root, "new.txt"); using var cancel = new CancellationTokenSource();
            var extractor = new StubExtractor((_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return new([], "done"); });
            await Throws<OperationCanceledException>(() => new IndexingService(repo, extractor, new FakeEmbedding()).IndexAsync(new([root]), cancellationToken: cancel.Token));
            Check(repo.Files[next].State == "pending" && repo.Files.ContainsKey(old), "Cancellation committed result or deletion.");
            var writes = repo.Writes; await Throws<OperationCanceledException>(() => new IndexingService(repo, extractor).IndexAsync(new([root]), cancellationToken: cancel.Token));
            Check(repo.Writes == writes, "Precancelled request wrote state.");
        });
        await Test("per-file extraction errors are failed; successful sibling still indexes", async () =>
        {
            var root = Folder("errors"); var bad = Put(root, "bad.txt"); var good = Put(root, "good.txt"); var repo = new FakeRepository();
            var real = new ContentExtractor(); var extractor = new StubExtractor((p, _) => p == bad ? throw new IOException("synthetic unreadable") : real.ExtractAsync(p, false).GetAwaiter().GetResult());
            await new IndexingService(repo, extractor, new FakeEmbedding()).IndexAsync(new([root]));
            Check(repo.Files[bad].State == "failed" && repo.Files[bad].Detail!.Contains("IOException: synthetic unreadable"), "Error masked as empty.");
            Check(repo.Files[good].State == "done", "Good file lost.");
        });
        await Test("failed/missing/nonfinite/wrong-dimension/zero/not-normalized vectors never done", async () =>
        {
            foreach (var mode in new[] { "error", "missing", "dimension", "nonfinite", "zero", "norm", "count" })
            {
                var root = Folder("vector-" + mode); var p = Put(root, "doc.txt"); var repo = new FakeRepository();
                await new IndexingService(repo, new ContentExtractor(), new FakeEmbedding { Mode = mode }).IndexAsync(new([root]));
                Check(repo.Files[p].State == "failed" && repo.Files[p].Detail is not null && repo.Files[p].Segments.All(s => s.Vector is null), "Invalid vector marked valid: " + mode);
            }
        });
        await Test("host exception stops run after one call, persists failure and prevents reconciliation", async () =>
        {
            var root = Folder("host-failed"); var old = Put(root, "old.txt"); var repo = new FakeRepository();
            await new IndexingService(repo, new ContentExtractor(), new FakeEmbedding()).IndexAsync(new([root]));
            File.Delete(old); Put(root, "one.txt"); Put(root, "two.txt"); var host = new FakeEmbedding { Mode = "throw" };
            await Throws<IOException>(() => new IndexingService(repo, new ContentExtractor(), host).IndexAsync(new([root])));
            Check(host.Inputs.Count == 1 && repo.Files.Values.Count(f => f.State == "failed") == 1 && repo.Files.ContainsKey(old), "Dead host was retried, failure lost or deletion ran.");
            Check(repo.Files.Values.Single(f => f.State == "failed").Detail!.Contains("Synthetic host dependency failure"), "Dependency cause lost.");
        });
        await Test("partial embedding batch retains success and error detail", async () =>
        {
            var root = Folder("mixed"); var p = Put(root, "doc.txt", string.Join(' ', Enumerable.Repeat("Words describe robust searchable information.", 140)));
            var repo = new FakeRepository(); var model = new FakeEmbedding { Mode = "mixed" };
            await new IndexingService(repo, new ContentExtractor(), model).IndexAsync(new([root]));
            Check(repo.Files[p].State == "partial" && repo.Files[p].Segments.Any(s => s.Vector is not null) && repo.Files[p].Segments.Any(s => s.Vector is null), "Partial batch incorrectly done or lost good vectors.");
            Check(model.MaxBatch <= 16, "Embedding batch bound exceeded.");
        });
        await Test("profile/fingerprint guard precedes all writes even Force", async () =>
        {
            var root = Folder("profile"); Put(root, "doc.txt"); var repo = new FakeRepository();
            await new IndexingService(repo, new ContentExtractor(), new FakeEmbedding()).IndexAsync(new([root]));
            var writes = repo.Writes; var snapshot = repo.Meta["embedding_info"];
            await Throws<InvalidDataException>(() => new IndexingService(repo, new ContentExtractor(), new FakeEmbedding { Profile = "other-model" }).IndexAsync(new([root], Force: true)));
            Check(writes == repo.Writes && snapshot == repo.Meta["embedding_info"], "Mismatch changed data or metadata.");
            await Throws<InvalidDataException>(() => new IndexingService(repo, new ContentExtractor(), new FakeEmbedding()).IndexAsync(new([root], Code: true, Force: true)));
            Check(writes == repo.Writes, "Code mode mismatch wrote state.");
        });
        await Test("missing root cannot reconcile; successful root can; unrequested root retained", async () =>
        {
            var unavailable = Folder("unavailable"); var up = Put(unavailable, "old.txt"); var okay = Folder("okay"); var op = Put(okay, "old.txt"); var other = Folder("other"); var untouched = Put(other, "old.txt");
            var repo = new FakeRepository(); var index = new IndexingService(repo, new ContentExtractor(), new FakeEmbedding());
            await index.IndexAsync(new([unavailable, okay, other]));
            Directory.Move(unavailable, unavailable + "-moved"); File.Delete(op); File.Delete(untouched);
            var events = new List<IndexProgress>(); await Throws<IncompleteIndexingException>(() => index.IndexAsync(new([unavailable, okay]), new ProgressSink(events.Add)));
            Check(repo.Files.ContainsKey(up) && repo.Files.ContainsKey(untouched) && !repo.Files.ContainsKey(op), "Reconciliation removed wrong root.");
            Check(events.Any(e => e.Phase == "crawl_failed") && events.Last().Detail is not null, "Enumeration failure hidden.");
        });
        await Test("replaced root identity cannot reconcile the old root", async () =>
        {
            var root = Folder("replaced-root"); var old = Put(root, "old.txt"); var repo = new FakeRepository();
            var index = new IndexingService(repo, new ContentExtractor(), new FakeEmbedding());
            await index.IndexAsync(new([root])); File.Delete(old); Put(root, "new.txt");
            var replaced = false; var events = new List<IndexProgress>();
            await Throws<IncompleteIndexingException>(() => index.IndexAsync(new([root]), new ProgressSink(e =>
            {
                events.Add(e);
                if (e.Phase == "pending" && !replaced)
                {
                    replaced = true; Directory.Move(root, root + "-original"); Directory.CreateDirectory(root);
                }
            })));
            Check(repo.Files.ContainsKey(old) && events.Any(e => e.Phase == "reconcile_failed"), "Replacement root was treated as successful enumeration of original root.");
        });
        await Test("depth limit disables deletion reconciliation for that root", async () =>
        {
            var root = Folder("depth"); var old = Put(root, "old.txt"); var repo = new FakeRepository();
            var index = new IndexingService(repo, new ContentExtractor(), new FakeEmbedding());
            await index.IndexAsync(new([root])); File.Delete(old);
            var deep = root; for (var i = 0; i < 130; i++) { deep = Path.Combine(deep, "d"); Directory.CreateDirectory(deep); }
            var events = new List<IndexProgress>(); await Throws<IncompleteIndexingException>(() => index.IndexAsync(new([root]), new ProgressSink(events.Add)));
            Check(repo.Files.ContainsKey(old) && events.Any(e => e.Phase == "crawl_failed" && e.Detail!.Contains("128")), "Bounded enumeration: retained=" + repo.Files.ContainsKey(old) + "; " + string.Join("; ", events.Where(e => e.Detail is not null).Select(e => e.Detail)));
        });
        await Test("junctions never escape; root junction rejected; path components pinned", async () =>
        {
            var root = Folder("junction-source"); var target = Folder("junction-target"); var p = Put(target, "outside.txt"); var link = Path.Combine(root, "escape");
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(arg);
            using (var process = Process.Start(psi)!) { var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Check(process.ExitCode == 0, "Synthetic junction creation failed: " + await stdout + await stderr); }
            var repo = new FakeRepository(); var index = new IndexingService(repo, new ContentExtractor(), new FakeEmbedding());
            await Throws<IncompleteIndexingException>(() => index.IndexAsync(new([root]))); Check(repo.Files.Count == 0, "Junction descendant was indexed.");
            var events = new List<IndexProgress>(); await Throws<IncompleteIndexingException>(() => index.IndexAsync(new([link]), new ProgressSink(events.Add)));
            Check(repo.Files.Count == 0 && events.Any(e => e.Phase == "crawl_failed"), "Root junction accepted.");
            await Throws<IOException>(() => new ContentExtractor().ExtractAsync(Path.Combine(link, "outside.txt"), false));
            Check(File.Exists(p), "Target changed.");
            var file = Put(root, "pinned.txt");
            using (SafeFile.Open(file))
            {
                try { File.Move(file, file + ".moved"); throw new Exception("Read lease allowed file replacement."); }
                catch (IOException) { }
            }
            File.Move(file, file + ".moved"); Check(File.Exists(file + ".moved"), "Lease leaked after dispose.");
        });
        await Test("code line/cell locators, generated/private key rejection and code-only types", async () =>
        {
            var root = Folder("code"); await InitGitAsync(root); var code = Put(root, "retry.cs", "class Retry\n{\n    public void Run() {}\n}\n"); Put(root, "data.json", "{\"not\":\"code\"}"); Put(root, "credentials.txt", "should never be read");
            var generated = Put(root, "generated.cs", "// DO NOT EDIT\nclass Generated {}\n"); var key = Put(root, "accidental.txt", "-----BEGIN PRIVATE KEY-----\nsynthetic no credential\n");
            var notebook = Put(root, "notes.ipynb", "{\"cells\":[{\"source\":[\"print('source')\\n\"],\"outputs\":[{\"text\":\"OUTPUT_NOT_INDEXED\"}]}],\"nbformat\":4}");
            var repo = new FakeRepository(); var model = new FakeEmbedding(); await new IndexingService(repo, new ContentExtractor(), model).IndexAsync(new([root], Code: true));
            Check(repo.Files[code].Segments[0] is { Kind: "lines", Location: 1, LocationEnd: 4 }, "Line locator mismatch.");
            Check(repo.Files[notebook].Segments[0] is { Kind: "cells", Location: 1, LocationEnd: 1 } && !repo.Files[notebook].Segments[0].Excerpt!.Contains("OUTPUT"), "Notebook outputs leaked.");
            Check(repo.Files[generated].State == "skipped" && repo.Files[key].State == "skipped", "Generated or private-key content accepted.");
            Check(!repo.Files.ContainsKey(Path.Combine(root, "data.json")) && !repo.Files.ContainsKey(Path.Combine(root, "credentials.txt")), "Data/credential file accepted.");
        });
        await Test("DOCX cached API extracts paragraphs/tabs/breaks, not zip bytes", async () =>
        {
            var root = Folder("word"); var path = Path.Combine(root, "synthetic.docx");
            using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(new Run(new Text("First paragraph"))), new Paragraph(new Run(new Text("Second"), new TabChar(), new Text("tab"), new Break(), new Text("line")))));
                main.Document.Save();
            }
            var result = await new ContentExtractor().ExtractAsync(path, false);
            Check(result.State == "done" && result.Segments.Count == 2 && result.Segments[0].Excerpt == "First paragraph"
                && result.Segments[1].Excerpt == "Second\ttab\nline" && result.Segments[0].Source?.Location.Paragraph == 1
                && result.Segments[1].Source?.Location.Paragraph == 2, "DOCX paragraph/control/source-unit extraction mismatch.");
            var repo = new FakeRepository(); await new IndexingService(repo, new ContentExtractor(), new FakeEmbedding()).IndexAsync(new([root]));
            Check(repo.Files[path].State == "done", "DOCX integration failed.");
        });
        await Test("PDF cached API keeps page locators; scans partial; corrupt failed", async () =>
        {
            var root = Folder("pdf"); var text = Path.Combine(root, "text.pdf"); var scan = Path.Combine(root, "scan.pdf"); var bad = Put(root, "corrupt.pdf", "not a PDF");
            MakePdf(text, string.Join(' ', Enumerable.Repeat("Searchable PDF migration text.", 12))); MakePdf(scan, "");
            var extractor = new ContentExtractor(); var result = await extractor.ExtractAsync(text, false);
            Check(result.State == "done" && result.Segments[0] is { Kind: "page", Location: 1 }, "PDF text/locator wrong.");
            Check((await extractor.ExtractAsync(scan, false)).State == "partial", "Scanned PDF faked success.");
            var repo = new FakeRepository(); await new IndexingService(repo, extractor, new FakeEmbedding()).IndexAsync(new([root]));
            Check(repo.Files[bad].State == "failed" && repo.Files[text].State == "done" && repo.Files[scan].State == "partial", "PDF states wrong.");
        });
        await Test("image host operation/budget; unsupported media explicit; media errors never done", async () =>
        {
            var root = Folder("media"); var image = Put(root, "Screenshot.png", "synthetic path only; fake decoder owns test"); var audio = Put(root, "sound.wav"); var video = Put(root, "movie.mp4");
            var repo = new FakeRepository(); var model = new FakeEmbedding(); await new IndexingService(repo, new ContentExtractor(), model).IndexAsync(new([root]));
            Check(model.Inputs.Single() == new EmbeddingInput("image", image, 280), "Image must use original media path/budget, not text prompt.");
            Check(repo.Files[image].State == "partial" && repo.Files[audio].State == "skipped" && repo.Files[video].State == "skipped", "Media gaps hidden.");
            await new IndexingService(repo, new ContentExtractor(), new FakeEmbedding { Mode = "error" }).IndexAsync(new([root], Force: true));
            Check(repo.Files[image].State == "failed", "Media error marked done.");
        });
        await Test("project/vendor/hidden folders respect default policies", async () =>
        {
            var root = Folder("projects"); Put(root, "package.json", "{}"); var ordinary = Put(root, "README.md"); var screenshot = Put(root, "Screenshot.png");
            var repo = new FakeRepository(); await new IndexingService(repo, new ContentExtractor(), new FakeEmbedding()).IndexAsync(new([root]));
            Check(!repo.Files.ContainsKey(ordinary) && repo.Files.ContainsKey(screenshot), "General index flooded by project assets.");
            var vendor = Path.Combine(root, "vendor"); Directory.CreateDirectory(vendor); Put(vendor, "foreign.cs", "class Foreign {}");
            var hidden = Path.Combine(root, ".hidden"); Directory.CreateDirectory(hidden); Put(hidden, "hidden.cs", "class Hidden {}");
            await InitGitAsync(root);
            var codeRepo = new FakeRepository(); await new IndexingService(codeRepo, new ContentExtractor(), new FakeEmbedding()).IndexAsync(new([root], Code: true));
            Check(codeRepo.Files.Count == 1 && codeRepo.Files.ContainsKey(ordinary), "Code default directory filters wrong.");
        });
        await Test("cloud/offline files are not opened for content", async () =>
        {
            var root = Folder("offline"); var path = Put(root, "placeholder.txt"); var attributes = File.GetAttributes(path);
            try
            {
                File.SetAttributes(path, attributes | FileAttributes.Offline);
                Check((File.GetAttributes(path) & FileAttributes.Offline) != 0, "Synthetic offline attribute not applied.");
                await Throws<IOException>(() => new ContentExtractor().ExtractAsync(path, false));
                var repo = new FakeRepository(); await new IndexingService(repo, new ContentExtractor(), new FakeEmbedding()).IndexAsync(new([root]));
                Check(repo.Files.Count == 0, "Offline placeholder was indexed.");
            }
            finally { File.SetAttributes(path, attributes); }
        });
        await Test("MPEG transport stream is not embedded as TypeScript", async () =>
        {
            var root = Folder("transport"); var path = Path.Combine(root, "clip.ts"); var bytes = Enumerable.Repeat((byte)'x', 600).ToArray();
            bytes[0] = bytes[188] = bytes[376] = 0x47; File.WriteAllBytes(path, bytes);
            var result = await new ContentExtractor().ExtractAsync(path, true);
            Check(result.State == "skipped" && result.Detail!.Contains("MPEG"), "Transport stream misclassified as source code.");
        });
        await Test("invalid/relative requests have no writes", async () =>
        {
            var repo = new FakeRepository(); var index = new IndexingService(repo, new ContentExtractor());
            var volume = Path.GetPathRoot(fixtures)!;
            Check(FilePolicy.Inside(Path.Combine(volume, "synthetic.txt"), volume) && !FilePolicy.Inside(fixtures + "-sibling", fixtures), "Drive-root/sibling path boundary mismatch.");
            await Throws<ArgumentException>(() => index.IndexAsync(new(["relative-root"])));
            await Throws<ArgumentOutOfRangeException>(() => index.IndexAsync(new([fixtures], MaxFileBytes: 0)));
            await index.IndexAsync(new([])); Check(repo.Writes == 0, "Invalid/empty roots wrote data.");
        });
        await ReviewRegressionsAsync();
        Console.WriteLine($"RESULT {passed} passed; 0 failed. Synthetic fixtures retained at {fixtures}");
    }
    private static void MakePdf(string path, string text)
    {
        var content = $"BT /F1 10 Tf 20 700 Td ({text.Replace("(", "\\(").Replace(")", "\\)")}) Tj ET";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10000 800] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream" };
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = pdf.Length; pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
    }
}

internal sealed class FakeRepository : IIndexRepository
{
    internal Dictionary<string, IndexedFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string> Meta { get; } = [];
    internal List<string> Events { get; } = [];
    internal int Writes { get; private set; }
    internal bool StrictPathIdentity { get; init; }
    internal Action<string>? BeforeDelete { get; set; }
    internal List<string> WritePaths { get; } = [];
    public string DatabasePath => "synthetic-memory";
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task InitializeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task<IReadOnlyList<IndexedFile>> ReadFilesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IndexedFile>>(Files.Values.ToArray());
    public Task<IndexedFile?> GetFileAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Files.GetValueOrDefault(path));
    public Task UpsertFileAsync(IndexedFile file, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (StrictPathIdentity && file.Id != 0 && !Files.Values.Any(f => f.Id == file.Id && string.Equals(f.Path, file.Path, StringComparison.Ordinal)))
            throw new InvalidDataException("File ID does not belong to this binary path.");
        Writes++; WritePaths.Add(file.Path); Events.Add(file.State);
        Files[file.Path] = file with { Id = Files.GetValueOrDefault(file.Path)?.Id ?? Files.Count + 1 };
        return Task.CompletedTask;
    }
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); BeforeDelete?.Invoke(path); Writes++; Files.Remove(path); return Task.CompletedTask; }
    public Task<IReadOnlyList<KeywordMatch>> KeywordSearchAsync(string query, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Meta.GetValueOrDefault(key));
    public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Writes++; Meta[key] = value; return Task.CompletedTask; }
    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
internal sealed class FakeEmbedding : IEmbeddingService
{
    internal string Mode { get; init; } = "valid";
    internal string Profile { get; init; } = "synthetic-v1";
    internal List<EmbeddingInput> Inputs { get; } = [];
    internal int MaxBatch { get; private set; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new EmbeddingInfo(Profile, 768, "Synthetic", "No model", "fixture", "CPU", "synthetic-only", ["text", "image", "audio"]));
    public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Inputs.AddRange(inputs); MaxBatch = Math.Max(MaxBatch, inputs.Count);
        if (Mode == "throw") throw new IOException("Synthetic host dependency failure.");
        if (Mode == "count") return Task.FromResult<IReadOnlyList<EmbeddingResult>>([]);
        return Task.FromResult<IReadOnlyList<EmbeddingResult>>(inputs.Select((_, index) =>
        {
            if (Mode == "error" || Mode == "mixed" && index % 2 == 1) return new EmbeddingResult(null, "synthetic item error");
            if (Mode == "missing") return new EmbeddingResult(null);
            var vector = new float[Mode == "dimension" ? 32 : 768];
            vector[0] = Mode switch { "nonfinite" => float.NaN, "zero" => 0, "norm" => 4, _ => 1 };
            return new EmbeddingResult(vector);
        }).ToArray());
    }
}
internal sealed class CountingExtractor(IContentExtractor inner) : IContentExtractor
{
    internal int Calls { get; private set; }
    internal List<string> Paths { get; } = [];
    public bool CanExtract(string path, bool code) => inner.CanExtract(path, code);
    public Task<ExtractionResult> ExtractAsync(string path, bool code, CancellationToken cancellationToken = default) { Calls++; Paths.Add(path); return inner.ExtractAsync(path, code, cancellationToken); }
}
internal sealed class StubExtractor(Func<string, CancellationToken, ExtractionResult> operation) : IContentExtractor
{
    public bool CanExtract(string path, bool code) => true;
    public Task<ExtractionResult> ExtractAsync(string path, bool code, CancellationToken cancellationToken = default) => Task.FromResult(operation(path, cancellationToken));
}
internal sealed class ProgressSink(Action<IndexProgress> report) : IProgress<IndexProgress>
{ public void Report(IndexProgress value) => report(value); }
