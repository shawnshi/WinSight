using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;

internal static class ReviewCases
{
    private const string R = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string O = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    internal static async Task<int> RunAsync(string root)
    {
        if (!Path.IsPathFullyQualified(root) || Directory.Exists(root)) throw new ArgumentException("Use a NEW absolute review-case directory.");
        Directory.CreateDirectory(root);
        var results = new List<object>(); int passed = 0, failed = 0;
        async Task Case(string id, Func<string, Task> run)
        {
            var path = Path.Combine(root, id); Directory.CreateDirectory(path);
            try { await run(path); passed++; results.Add(new { id, pass = true, error = (string?)null }); Console.WriteLine("PASS " + id); }
            catch (Exception error) { failed++; results.Add(new { id, pass = false, error = error.ToString() }); Console.WriteLine("FAIL " + id + ": " + error.Message); }
        }
        static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static async Task Store(SqliteIndexRepository repository, string path, ExtractionResult result, string kind = "doc") =>
            await repository.UpsertFileAsync(new(0, path, kind, 1, 1, result.State, result.Detail, result.Info, result.Segments));
        await Case("DS01-R01-slides", async folder =>
        {
            var slides = Slides(folder, "alpha bravo", "alpha bravo");
            var extraction = await new ContentExtractor().ExtractAsync(slides, false);
            await using var db = new SqliteIndexRepository(Path.Combine(folder, "index.sqlite")); await Store(db, slides, extraction);
            var found = await new SearchService(db).SearchAsync(new("alpha bravo", Mode: SearchMode.Keyword));
            Need(found.Hits.Count == 2 && found.Hits.Select(h => h.Source?.Location.Page).Order().SequenceEqual(new int?[] { 1, 2 }), "Both actual matching slides must survive keyword recall.");
        });
        await Case("DS01-R01-unit-coverage", async folder =>
        {
            await using var db = new SqliteIndexRepository(Path.Combine(folder, "index.sqlite"));
            await db.UpsertFileAsync(new(0, Path.Combine(folder, "pages.pdf"), "pdf", 1, 1, "done", null, null,
                new[] { "alpha", "bravo", "charlie" }.Select((text, i) => new IndexSegment("page", "text", i + 1, null, text, Source: new(new("pdf-page", Page: i + 1)))).ToArray()));
            Need((await new SearchService(db).SearchAsync(new("alpha bravo charlie", Mode: SearchMode.Keyword))).Hits.Count == 0, "Terms on unrelated pages cannot assemble full source coverage.");
        });
        await Case("DS01-R01-chunk-dedup", async folder =>
        {
            await using var db = new SqliteIndexRepository(Path.Combine(folder, "index.sqlite"));
            var chunks = Enumerable.Range(0, 150).Select(_ => new IndexSegment("page", "text", 1, null, "alpha bravo", Source: new(new("pdf-page", Page: 1))))
                .Append(new("page", "text", 2, null, "alpha bravo", Source: new(new("pdf-page", Page: 2)))).ToArray();
            await db.UpsertFileAsync(new(0, Path.Combine(folder, "pages.pdf"), "pdf", 1, 1, "done", null, null, chunks));
            Need((await new SearchService(db).SearchAsync(new("alpha bravo", Limit: 2, Mode: SearchMode.Keyword))).Hits.Select(h => h.Source?.Location.Page).Distinct().Count() == 2, "Repeated chunks must not starve another keyword unit.");
        });
        foreach (var header in new[] { "项目编号", "項目編號", "Ｐｒｏｊｅｃｔ　ＩＤ" })
            await Case("DS01-R02-" + (header == "项目编号" ? "simple" : header == "項目編號" ? "traditional" : "english-width"), async folder =>
            {
                var path = Sheet(folder, [header, "说明"], ["PRJ-2026-001", "alpha contract"]);
                var extracted = await new ContentExtractor().ExtractAsync(path, false);
                await using var db = new SqliteIndexRepository(Path.Combine(folder, "index.sqlite")); await Store(db, path, extracted);
                var hit = (await new SearchService(db).SearchAsync(new("project:PRJ-2026-001", Mode: SearchMode.Keyword))).Hits.Single();
                Need(hit.Source?.Location.Range == "A2:B2", "Typed project field must be row-local.");
                Need(extracted.Segments.Any(s => s.Source?.Location.Range == "A2:B2" && s.Excerpt!.Contains(header, StringComparison.Ordinal)), "Original header label must remain in context.");
            });
        await Case("DS01-R03-field-chunk-dedup", async folder =>
        {
            var path = Sheet(folder, ["项目编号", "金额", "说明"],
                ["PRJ-2026-001", "100", string.Concat(Enumerable.Repeat("long narrative ", 15000))], ["PRJ-2026-001", "200", "second eligible row"]);
            var extracted = await new ContentExtractor().ExtractAsync(path, false);
            Need(extracted.Segments.Count > 100, "Fixture must cross the old pre-dedup cap.");
            await using var db = new SqliteIndexRepository(Path.Combine(folder, "index.sqlite")); await Store(db, path, extracted);
            var hits = (await new SearchService(db).SearchAsync(new("project:PRJ-2026-001", Limit: 2, Mode: SearchMode.Keyword))).Hits;
            Need(hits.Count == 2 && hits.Select(h => h.Source?.Location.Range).Distinct().Count() == 2, "Field recall budget must count distinct source rows, not chunk anchors.");
        });
        await Case("DS01-R04-text-pdf", async folder =>
        {
            var path = Pdf(folder, "project PRJ-2026-001 amount 100 alpha " + string.Concat(Enumerable.Repeat("background ", 25)),
                "project PRJ-2026-002 amount 200 alpha " + string.Concat(Enumerable.Repeat("background ", 25)));
            var extracted = await new ContentExtractor().ExtractAsync(path, false);
            await using var db = new SqliteIndexRepository(Path.Combine(folder, "index.sqlite")); await Store(db, path, extracted, "pdf");
            var service = new SearchService(db);
            var fields = await service.SearchAsync(new("project:PRJ-2026-001 amount:100", Mode: SearchMode.Keyword));
            Need(fields.Hits.Count == 1 && fields.Hits[0].Source?.Location.Page == 1, "Extractable PDF needs page-local structured fields.");
            var mixed = await service.SearchAsync(new("alpha project:PRJ-2026-002 amount:200", Mode: SearchMode.Keyword));
            Need(mixed.Hits.Count == 1 && mixed.Hits[0].Source?.Location.Page == 2, "Keyword+field PDF filters must locate the correct page.");
            Need((await service.SearchAsync(new("project:PRJ-2026-001 amount:200", Mode: SearchMode.Keyword))).Hits.Count == 0, "PDF fields must not combine across pages.");
        });
        await Case("DS01-R05-numeric-body", async folder =>
        {
            await using var db = new SqliteIndexRepository(Path.Combine(folder, "index.sqlite"));
            await db.UpsertFileAsync(new(0, Path.Combine(folder, "long.txt"), "doc", 1, 1, "done", null, null, [new("chunk", "text", null, null, "1234567 0012345")]));
            var service = new SearchService(db);
            foreach (var token in new[] { "123456", "001234" }) Need((await service.SearchAsync(new(token, Mode: SearchMode.Keyword))).Hits.Count == 0, "Numeric prefix must not match: " + token);
            var exact = Path.Combine(folder, "exact.txt");
            await db.UpsertFileAsync(new(0, exact, "doc", 1, 1, "done", null, null, [new("chunk", "text", null, null, "123456 001234")]));
            foreach (var token in new[] { "123456", "001234" }) Need((await service.SearchAsync(new(token, Mode: SearchMode.Keyword))).Hits.Single().Path == exact, "Exact numeric token must retrieve original zeros.");
        });
        await Case("DS01-R05-numeric-path", async folder =>
        {
            await using var db = new SqliteIndexRepository(Path.Combine(folder, "index.sqlite"));
            foreach (var path in new[] { Path.Combine(folder, "1234567.txt"), Path.Combine(folder, "0012345", "report.txt") })
                await db.UpsertFileAsync(new(0, path, "doc", 1, 1, "done", null, null, []));
            var service = new SearchService(db);
            Need((await service.SearchAsync(new("123456", Mode: SearchMode.Keyword))).Hits.Count == 0, "Filename recall must not admit numeric prefixes.");
            Need((await service.SearchAsync(new("001234", Mode: SearchMode.Keyword))).Hits.Count == 0, "Folder recall must not admit numeric prefixes.");
            var exact = Path.Combine(folder, "001234", "report.txt");
            await db.UpsertFileAsync(new(0, exact, "doc", 1, 1, "done", null, null, []));
            Need((await service.SearchAsync(new("001234", Mode: SearchMode.Keyword))).Hits.Single().Path == exact, "Exact numeric folder tokens must remain searchable.");
        });
        await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed, failed, results,
            scope = "Actual Office/PdfPig extraction and SQLite keyword/field recall; no OCR, model, GUI or Office process." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Review cases {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static string Xml(string value) => System.Security.SecurityElement.Escape(value)!;
    private static string Types(params (string Part, string Type)[] parts) => "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" + string.Concat(parts.Select(p => $"<Override PartName=\"/{p.Part}\" ContentType=\"{p.Type}\"/>")) + "</Types>";
    private static string Package(string folder, string name, Dictionary<string, string> parts)
    {
        var path = Path.Combine(folder, name); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var part in parts) { using var stream = zip.CreateEntry(part.Key).Open(); stream.Write(Encoding.UTF8.GetBytes(part.Value)); }
        return path;
    }
    private static string Sheet(string folder, params string[][] rows) => Package(folder, "fixture.xlsx", new()
    {
        ["[Content_Types].xml"] = Types(("xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"), ("xl/worksheets/sheet.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
        ["_rels/.rels"] = $"<Relationships xmlns=\"{R}\"><Relationship Id=\"r\" Type=\"{O}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
        ["xl/workbook.xml"] = $"<workbook xmlns=\"{S}\" xmlns:r=\"{O}\"><sheets><sheet name=\"Projects\" sheetId=\"1\" r:id=\"s\"/></sheets></workbook>",
        ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"{R}\"><Relationship Id=\"s\" Type=\"{O}/worksheet\" Target=\"worksheets/sheet.xml\"/></Relationships>",
        ["xl/worksheets/sheet.xml"] = $"<worksheet xmlns=\"{S}\"><sheetData>" + string.Concat(rows.Select((row, r) => $"<row r=\"{r + 1}\">" + string.Concat(row.Select((value, c) => $"<c r=\"{(char)('A' + c)}{r + 1}\" t=\"inlineStr\"><is><t>{Xml(value)}</t></is></c>")) + "</row>")) + "</sheetData></worksheet>"
    });
    private static string Slides(string folder, params string[] text)
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = Types(new[] { ("ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml") }.Concat(text.Select((_, i) => ($"ppt/slides/{i + 1}.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"))).ToArray()),
            ["_rels/.rels"] = $"<Relationships xmlns=\"{R}\"><Relationship Id=\"r\" Type=\"{O}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>",
            ["ppt/presentation.xml"] = $"<p:presentation xmlns:p=\"{P}\" xmlns:r=\"{O}\"><p:sldIdLst>" + string.Concat(text.Select((_, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"r{i}\"/>")) + "</p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = $"<Relationships xmlns=\"{R}\">" + string.Concat(text.Select((_, i) => $"<Relationship Id=\"r{i}\" Type=\"{O}/slide\" Target=\"slides/{i + 1}.xml\"/>")) + "</Relationships>"
        };
        for (int i = 0; i < text.Length; i++) parts[$"ppt/slides/{i + 1}.xml"] = $"<p:sld xmlns:p=\"{P}\" xmlns:a=\"{A}\"><p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id=\"1\" name=\"fixture\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{Xml(text[i])}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>";
        return Package(folder, "fixture.pptx", parts);
    }
    private static string Pdf(string folder, params string[] text)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", $"<< /Type /Pages /Kids [{string.Join(' ', text.Select((_, i) => $"{4 + i * 2} 0 R"))}] /Count {text.Length} >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        for (int i = 0; i < text.Length; i++)
        {
            var content = $"BT /F1 10 Tf 20 700 Td ({text[i].Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")}) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10000 800] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Count; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(folder, "fixture.pdf"); File.WriteAllText(path, pdf.ToString(), Encoding.ASCII); return path;
    }
}
