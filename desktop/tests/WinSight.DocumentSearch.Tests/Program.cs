using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;
using WinSight.Infrastructure.Indexing;
using Microsoft.Data.Sqlite;

if(args.Length==4&&args[0]=="--powerpoint-navigation-cases")
{
    Environment.ExitCode=await PowerPointNavigationCases.RunAsync(args[1],args[2],args[3]);return;
}
if(args.Length==4&&args[0]=="--source-office-policy")
{
    Environment.ExitCode=await OfficePolicyCases.RunAsync(args[1],args[2],args[3]);return;
}
if(args.Length==4&&args[0]=="--source-preview-backend")
{
    Environment.ExitCode=await SourcePreviewCases.RunAsync(args[1],args[2],args[3]);return;
}
if(args.Length==4&&args[0]=="--source-navigation")
{
    Environment.ExitCode=await SourceOperationCases.NavigateAsync(args[1],args[2],args[3]);return;
}
if (args.Length == 4 && args[0] == "--powerpoint-index-cases")
{
    Environment.ExitCode = await PowerPointIndexCases.RunAsync(args[1], args[2], args[3]);
    return;
}
if (args.Length == 5 && args[0] == "--powerpoint-cases")
{
    Environment.ExitCode = await PowerPointCases.RunAsync(args[1], args[2], args[3], args[4]);
    return;
}
if (args.Length == 4 && args[0] == "--powerpoint-cases")
{
    Environment.ExitCode = await PowerPointCases.RunAsync(args[1], args[2], args[3]);
    return;
}
if (args.Length == 3 && args[0] == "--native-page-cases")
{
    Environment.ExitCode = await PageCases.RunAsync(args[1], args[2]);
    return;
}
if (args.Length == 2 && args[0] == "--review-cases")
{
    Environment.ExitCode = await ReviewCases.RunAsync(args[1]);
    return;
}
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Use a NEW absolute task directory.");
var root = args[0];
if (Directory.Exists(root)) throw new ArgumentException("Task directory must be new.");
Directory.CreateDirectory(root);
var checks = new List<object>();
var extractor = new ContentExtractor();
void Check(string name, bool pass)
{
    checks.Add(new { name, pass });
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}");
    if (!pass) throw new InvalidOperationException(name);
}
async Task Reject(string name, string path, params Type[] expected)
{
    try { await extractor.ExtractAsync(path, false); }
    catch (Exception error) when (expected.Contains(error.GetType())) { Check(name, true); return; }
    Check(name, false);
}
string Package(string name, Dictionary<string, string> parts)
{
    var path = Path.Combine(root, name);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (part, xml) in parts)
    {
        using var stream = zip.CreateEntry(part).Open();
        stream.Write(Encoding.UTF8.GetBytes(xml));
    }
    return path;
}
const string rels = "http://schemas.openxmlformats.org/package/2006/relationships";
const string office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
const string wordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
const string drawNs = "http://schemas.openxmlformats.org/drawingml/2006/main";
const string pptNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
const string sheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
string Types(params (string Path, string Type)[] parts) => "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" + string.Concat(parts.Select(p => $"<Override PartName=\"/{p.Path}\" ContentType=\"{p.Type}\"/>")) + "</Types>";
string RootRels(string target) => $"<Relationships xmlns=\"{rels}\"><Relationship Id=\"r1\" Type=\"{office}/officeDocument\" Target=\"{target}\"/></Relationships>";
string Wp(string text) => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";
string PShape(string text, string placeholder = "body") => $"<p:sp><p:nvSpPr><p:cNvPr id=\"1\" name=\"fixture\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"{placeholder}\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp>";
var word = Package("consulting.docx", new()
{
    ["[Content_Types].xml"] = Types(("word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"), ("word/header1.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"), ("word/comments.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml")),
    ["_rels/.rels"] = RootRels("word/document.xml"),
    ["word/document.xml"] = $"<w:document xmlns:w=\"{wordNs}\" xmlns:r=\"{office}\"><w:body><w:p><w:r><w:t>医</w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>疗改革 PRJ-2026-001</w:t></w:r></w:p><w:tbl><w:tr><w:tc>{Wp("项目编号")}</w:tc><w:tc>{Wp("金额")}</w:tc></w:tr><w:tr><w:tc>{Wp("PRJ-2026-001")}</w:tc><w:tc>{Wp("128,000.50")}</w:tc></w:tr></w:tbl>{Wp("版本 v2.5.1")}</w:body></w:document>",
    ["word/_rels/document.xml.rels"] = $"<Relationships xmlns=\"{rels}\"><Relationship Id=\"h1\" Type=\"{office}/header\" Target=\"header1.xml\"/><Relationship Id=\"c1\" Type=\"{office}/comments\" Target=\"comments.xml\"/></Relationships>",
    ["word/header1.xml"] = $"<w:hdr xmlns:w=\"{wordNs}\">{Wp("合成页眉字段")}</w:hdr>",
    ["word/comments.xml"] = $"<w:comments xmlns:w=\"{wordNs}\"><w:comment w:id=\"0\" w:author=\"fixture\">{Wp("合成批注字段")}</w:comment></w:comments>"
});
var doc = await extractor.ExtractAsync(word, false);
Check("DOCX formatting runs preserve Chinese phrase", doc.Segments.Any(s => s.Excerpt!.Contains("医疗改革")));
Check("DOCX paragraph/row anchors", doc.Segments.All(s => s.Source?.Location.Type == "paragraph") && doc.Segments.Any(s => s.Source?.Location.Paragraph == 4 && s.Source.Fields!.Any(f => f.Name == "amount" && f.Value == "128,000.50")));
Check("DOCX private optional parts default off", !doc.Segments.Any(s => s.Excerpt!.Contains("合成页眉") || s.Excerpt.Contains("合成批注")));
var fullWord = await extractor.ExtractAsync(word, false, new DocumentExtractionOptions(true, true));
Check("DOCX configured comments/header opt-in", fullWord.Segments.Any(s => s.Source?.Location.Part?.StartsWith("comment:") == true) && fullWord.Segments.Any(s => s.Source?.Location.Part == "/word/header1.xml"));
Check("DOCX options revision changes", fullWord.Info != doc.Info);
var ppt = Package("slides.pptx", new()
{
    ["[Content_Types].xml"] = Types(("ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"), ("ppt/slides/slide1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"), ("ppt/slides/slide2.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"), ("ppt/notesSlides/notesSlide1.xml", "application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml")),
    ["_rels/.rels"] = RootRels("ppt/presentation.xml"),
    ["ppt/presentation.xml"] = $"<p:presentation xmlns:p=\"{pptNs}\" xmlns:r=\"{office}\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"r2\"/><p:sldId id=\"257\" r:id=\"r1\"/></p:sldIdLst></p:presentation>",
    ["ppt/_rels/presentation.xml.rels"] = $"<Relationships xmlns=\"{rels}\"><Relationship Id=\"r1\" Type=\"{office}/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"r2\" Type=\"{office}/slide\" Target=\"slides/slide2.xml\"/></Relationships>",
    ["ppt/slides/slide2.xml"] = $"<p:sld xmlns:p=\"{pptNs}\" xmlns:a=\"{drawNs}\"><p:cSld><p:spTree>{PShape("采购方案第一页", "title")}</p:spTree></p:cSld></p:sld>",
    ["ppt/slides/slide1.xml"] = $"<p:sld xmlns:p=\"{pptNs}\" xmlns:a=\"{drawNs}\"><p:cSld><p:spTree>{PShape("合同第二页")}</p:spTree></p:cSld></p:sld>",
    ["ppt/slides/_rels/slide2.xml.rels"] = $"<Relationships xmlns=\"{rels}\"><Relationship Id=\"n1\" Type=\"{office}/notesSlide\" Target=\"../notesSlides/notesSlide1.xml\"/></Relationships>",
    ["ppt/notesSlides/notesSlide1.xml"] = $"<p:notes xmlns:p=\"{pptNs}\" xmlns:a=\"{drawNs}\"><p:cSld><p:spTree>{PShape("演讲备注 PRJ-2026-001")}{PShape("999", "sldNum")}</p:spTree></p:cSld></p:notes>"
});
var slides = await extractor.ExtractAsync(ppt, false);
Check("PPTX slide order follows relationship list", slides.Segments.Any(s => s.Source?.Location.Page == 1 && s.Excerpt!.Contains("采购方案")) && slides.Segments.Any(s => s.Source?.Location.Page == 2 && s.Excerpt!.Contains("合同")));
Check("PPTX notes channel/slide anchor", slides.Segments.Any(s => s.Source?.Channel == "notes" && s.Source.Location.Page == 1 && s.Excerpt!.Contains("演讲备注")));
Check("PPTX slide-number placeholders excluded", !slides.Segments.Any(s => s.Excerpt!.Contains("999")));
Check("PPTX text is not called pagevisual", !slides.Segments.Any(s => s.Modality == "image") && slides.Info!.Contains("not-rendered"));
var xlsx = Package("projects.xlsx", new()
{
    ["[Content_Types].xml"] = Types(("xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"), ("xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"), ("xl/sharedStrings.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"), ("xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")),
    ["_rels/.rels"] = RootRels("xl/workbook.xml"),
    ["xl/workbook.xml"] = $"<workbook xmlns=\"{sheetNs}\" xmlns:r=\"{office}\"><workbookPr date1904=\"1\"/><sheets><sheet name=\"项目清单\" sheetId=\"1\" r:id=\"s1\"/></sheets></workbook>",
    ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"{rels}\"><Relationship Id=\"s1\" Type=\"{office}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"ss\" Type=\"{office}/sharedStrings\" Target=\"sharedStrings.xml\"/><Relationship Id=\"st\" Type=\"{office}/styles\" Target=\"styles.xml\"/></Relationships>",
    ["xl/sharedStrings.xml"] = $"<sst xmlns=\"{sheetNs}\"><si><t>项目编号</t></si><si><t>金额</t></si><si><t>日期</t></si><si><t>产品版本</t></si><si><t>型号</t></si><si><t>PRJ-2026-001</t></si></sst>",
    ["xl/styles.xml"] = $"<styleSheet xmlns=\"{sheetNs}\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"000000\"/></numFmts><cellXfs count=\"3\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/><xf numFmtId=\"164\"/></cellXfs></styleSheet>",
    ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{sheetNs}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c><c r=\"D1\" t=\"s\"><v>3</v></c><c r=\"E1\" t=\"s\"><v>4</v></c></row><row r=\"2\"><c r=\"A2\" t=\"s\"><v>5</v></c><c r=\"B2\"><v>128000.50</v></c><c r=\"C2\" s=\"1\"><v>0</v></c><c r=\"D2\" t=\"inlineStr\"><is><t>v2.5.1</t></is></c><c r=\"E2\" t=\"str\"><f>HYPERLINK(&quot;https://invalid.example/&quot;)</f><v>MODEL-11</v></c></row><row r=\"3\"><c r=\"A3\" s=\"2\"><v>123</v></c><c r=\"B3\"><f>SUM(1,2)</f></c></row><row r=\"4\" hidden=\"1\"><c r=\"A4\" t=\"inlineStr\"><is><t>HIDDEN-FIXTURE</t></is></c></row></sheetData></worksheet>"
});
var rows = await extractor.ExtractAsync(xlsx, false);
Check("XLSX sheet/range anchor and headers", rows.Segments.Any(s => s.Source?.Location.Sheet == "项目清单" && s.Source.Location.Range == "A2:E2" && s.Excerpt!.Contains("金额: 128000.50")));
Check("XLSX date1904 and zero-padded code", rows.Segments.Any(s => s.Excerpt!.Contains("1904-01-01")) && rows.Segments.Any(s => s.Excerpt!.Contains("000123")));
Check("XLSX structured fields remain row-local", rows.Segments.Any(s => s.Source?.Location.Range == "A2:E2" && s.Source.Fields!.Any(f => f.Name == "project" && f.Value == "PRJ-2026-001")));
Check("XLSX formula caches explicitly partial", rows.State == "partial" && rows.Detail!.Contains("not recalculated") && rows.Detail.Contains("no cached value"));
Check("XLSX no formula evaluation or hidden-row indexing", !rows.Segments.Any(s => s.Excerpt!.Contains("SUM(") || s.Excerpt.Contains("HIDDEN-FIXTURE")) && rows.Segments.Any(s => s.Excerpt!.Contains("MODEL-11")));
var malicious = Package("dtd.docx", new() { ["[Content_Types].xml"] = "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///unreachable-fixture'>]><x>&e;</x>" });
await Reject("Office DTD is prohibited", malicious, typeof(System.Xml.XmlException));
var invalid = Package("bad.xlsx", new() { ["[Content_Types].xml"] = "<Types" });
await Reject("Malformed XML is failure not empty", invalid, typeof(System.Xml.XmlException));
var tokenSource = new CancellationTokenSource(); tokenSource.Cancel();
try { await extractor.ExtractAsync(word, false, tokenSource.Token); Check("Cancellation propagates", false); }
catch (OperationCanceledException) { Check("Cancellation propagates", true); }
await using var repository = new SqliteIndexRepository(Path.Combine(root, "index.sqlite"));
await repository.InitializeAsync();
await repository.UpsertFileAsync(new(0, word, "doc", 100, 1, "done", null, doc.Info, doc.Segments));
await repository.UpsertFileAsync(new(0, ppt, "doc", 100, 1, "done", null, slides.Info, slides.Segments));
await repository.UpsertFileAsync(new(0, xlsx, "doc", 100, 1, "partial", rows.Detail, rows.Info, rows.Segments));
var roundtrip = await repository.GetFileAsync(xlsx);
Check("Source anchors and row fields survive actual SQLite", roundtrip!.Segments.Any(s => s.Source?.Location.Range == "A2:E2" && s.Source.Fields!.Any(f => f.Name == "amount" && f.Value == "128000.50")));
Check("Schema5 native marker unchanged", await repository.GetMetaAsync("native_format") == SqliteIndexRepository.NativeFormat);
var search = new SearchService(repository);
var chinese = await search.SearchAsync(new("醫療", Mode: SearchMode.Keyword));
Check("Traditional query retrieves simplified original paragraph", chinese.Hits.Any(h => h.Path == word && h.Excerpt!.Contains("医疗改革") && h.Source?.Location.Paragraph == 1));
var exact = await search.SearchAsync(new("PRJ-2026-001", Mode: SearchMode.Keyword));
Check("Literal identifier survives punctuation", exact.Hits.Any(h => h.Path == word) && exact.Hits.Any(h => h.Path == xlsx));
var field = await search.SearchAsync(new("项目:PRJ-2026-001 金额:128000.5", Mode: SearchMode.Keyword));
Check("Field-only amount equality uses decimal values", field.Hits.Any(h => h.Path == word && h.Source?.Location.Paragraph == 4) && field.Hits.Any(h => h.Path == xlsx && h.Source?.Location.Range == "A2:E2"));
var cells = await search.SearchAsync(new("sheet:项目清单 cell:B2 amount:128000.50", Mode: SearchMode.Keyword));
Check("Sheet and cell filters locate actual row", cells.Hits.Count == 1 && cells.Hits[0].Source?.Location.Range == "A2:E2");
await repository.UpsertFileAsync(new(0, Path.Combine(root, "traditional.txt"), "doc", 10, 1, "done", null, null,
    [new("chunk", "text", null, null, "醫療數位轉型 ＭＯＤＥＬ－１１")]));
var width = await search.SearchAsync(new("MODEL-11", Mode: SearchMode.Keyword));
Check("Fullwidth identifier and original excerpt coexist", width.Hits.Any(h => h.Excerpt!.Contains("ＭＯＤＥＬ－１１")));
Check("Chinese simplified query retains traditional excerpt", (await search.SearchAsync(new("医疗", Mode: SearchMode.Keyword))).Hits.Any(h => h.Excerpt!.Contains("醫療")));
for (int i = 0; i < 110; i++)
    await repository.UpsertFileAsync(new(0, Path.Combine(root, $"neighbor-{i}.txt"), "doc", 1, 1, "done", null, null,
        [new("chunk", "text", null, null, $"项目编号 PRJ-2026-001-A 邻近编号{i}")]));
var literalOnly = await search.SearchAsync(new("PRJ-2026-001", Limit: 1, Mode: SearchMode.Keyword));
Check("Exact identifier filter precedes candidate limit", literalOnly.Hits.Count == 1 && !literalOnly.Hits.Any(h => h.Path.Contains("neighbor-")));
Check("Identifier suffix does not match shorter ID", !(await search.SearchAsync(new("PRJ-2026-001-A9", Mode: SearchMode.Keyword))).Hits.Any());
var cross = Path.Combine(root, "cross-row.xlsx");
await repository.UpsertFileAsync(new(0, cross, "doc", 1, 1, "done", null, null,
    [new("cells", "text", 1, 1, "项目编号 PRJ-X 金额 100", Source: new(new("sheet", Sheet: "S", Range: "A1:B1"), "table", [new("project","PRJ-X"),new("amount","100")])),
     new("cells", "text", 2, 2, "项目编号 PRJ-Y 金额 200", Source: new(new("sheet", Sheet: "S", Range: "A2:B2"), "table", [new("project","PRJ-Y"),new("amount","200")]))]));
Check("Fields do not combine across spreadsheet rows", !(await search.SearchAsync(new("project:PRJ-X amount:200", Mode: SearchMode.Keyword))).Hits.Any());
var importRoot = Path.Combine(root, "import");
var nested = Path.Combine(importRoot, "政策", "2026", "华东");
Directory.CreateDirectory(nested);
var indexed = Path.Combine(nested, "项目.docx"); File.Copy(word, indexed);
await using var indexRepository = new SqliteIndexRepository(Path.Combine(root, "import.sqlite"));
await using var fake = new SyntheticEmbeddings();
var indexer = new IndexingService(indexRepository, extractor, fake);
await indexer.IndexAsync(new([importRoot]));
var indexedFirst = await indexRepository.GetFileAsync(indexed);
Check("Indexing embeds source units independently", indexedFirst!.State == "done" && indexedFirst.Segments.Count(s => s.Vector != null) == doc.Segments.Count);
var folderSearch = await new SearchService(indexRepository).SearchAsync(new("folder1:政策 folder2:2026 folder3:华东 project:PRJ-2026-001", Mode: SearchMode.Keyword));
Check("Three directory levels are relative to indexed root", folderSearch.Hits.Any(h => h.Path == indexed));
await indexer.IndexAsync(new([importRoot], Documents: new(true, true)));
Check("Changed DOCX options re-extract unchanged file", (await indexRepository.GetFileAsync(indexed))!.Segments.Any(s => s.Source?.Location.Part?.StartsWith("comment:") == true));
await using var semanticRepository = new SqliteIndexRepository(Path.Combine(root, "fusion.sqlite"));
await semanticRepository.SetMetaAsync("embedding_dimension", "768");
await semanticRepository.SetMetaAsync("profile_id", "synthetic-doc-profile");
float[] Vec(float cosine) { var v = new float[768]; v[0] = cosine; v[1] = MathF.Sqrt(1 - cosine * cosine); return v; }
var fusionPath = Path.Combine(root, "fusion.pdf");
var fusionSegments = new IndexSegment[]
{
    new("page","text",1,null,"body negative",Vec(-.9f),new(new("pdf-page",Page:1),"body")),
    new("page","image",1,null,null,Vec(.9f),new(new("pdf-page",Page:1),"page-visual")),
    new("chunk","text",1,null,"OCR positive",Vec(.95f),new(new("pdf-page",Page:1),"ocr")),
    new("page","text",2,null,"body positive",Vec(.8f),new(new("pdf-page",Page:2),"body"))
};
await semanticRepository.UpsertFileAsync(new(0,fusionPath,"pdf",1,1,"done",null,null,fusionSegments));
var fusionService = new SearchService(semanticRepository, fake);
var fusion = await fusionService.SearchAsync(new("consulting visual", Mode: SearchMode.Semantic));
Check("Separate body/OCR/pagevisual recalls fuse on page", fusion.Hits.Count == 2 && fusion.Hits[0].Source?.Location.Page == 1
    && fusion.Hits[0].Channels!.Contains("semantic:ocr") && fusion.Hits[0].Channels!.Contains("semantic:page-visual"));
double beforeScore = fusion.Hits[0].Score;
await semanticRepository.UpsertFileAsync(new(0,fusionPath,"pdf",1,1,"done",null,null,fusionSegments.Concat(Enumerable.Repeat(fusionSegments[1],20)).ToArray()));
var duplicateFusion = await fusionService.SearchAsync(new("consulting visual", Mode: SearchMode.Semantic));
Check("Duplicate chunks do not inflate a source-unit vote", Math.Abs(duplicateFusion.Hits[0].Score - beforeScore) < 1e-9);
Check("Vector contradiction is not averaged away", duplicateFusion.Hits[0].Channels!.Contains("semantic:page-visual"));
await repository.UpsertFileAsync(new(0, Path.Combine(root,"many-ids.txt"),"doc",1,1,"done",null,null,
    [new("chunk","text",null,null,string.Join(" ",Enumerable.Range(0,70).Select(i=>$"ITEM-{i}")))]));
Check("Exact IDs beyond bounded metadata still retrieve", (await search.SearchAsync(new("ITEM-69",Mode:SearchMode.Keyword))).Hits.Any(h=>h.Path.EndsWith("many-ids.txt",StringComparison.Ordinal)));
await repository.UpsertFileAsync(new(0,Path.Combine(root,"PRJ-2030-009.txt"),"doc",1,1,"done",null,null,[]));
Check("Identifier in filename ignores only extension", (await search.SearchAsync(new("PRJ-2030-009",Mode:SearchMode.Keyword))).Hits.Any(h=>h.Path.EndsWith("PRJ-2030-009.txt",StringComparison.Ordinal)));
await repository.UpsertFileAsync(new(0,Path.Combine(root,"mixed.txt"),"doc",1,1,"done",null,null,[new("chunk","text",null,null,"WiNEX医院建设 医保支付")]));
Check("Mixed Latin/Chinese without spaces", (await search.SearchAsync(new("WiNEX医院",Mode:SearchMode.Keyword))).Hits.Any(h=>h.Path.EndsWith("mixed.txt",StringComparison.Ordinal)));
Check("Chinese bigram recall", (await search.SearchAsync(new("医院建设",Mode:SearchMode.Keyword))).Hits.Any(h=>h.Path.EndsWith("mixed.txt",StringComparison.Ordinal)));
var latePath=Path.Combine(root,"late-fields.xlsx");
await repository.UpsertFileAsync(new(0,latePath,"doc",1,1,"done",null,null,Enumerable.Range(1,180).Select(i=>
    new IndexSegment("cells","text",i,i,$"金额 {i}",Source:new(new("sheet",Sheet:"L",Range:$"A{i}:B{i}"),"table",[new("amount",i.ToString())]))).ToArray()));
Check("Source field eligibility precedes per-channel cap", (await search.SearchAsync(new("sheet:L amount:180",Limit:1,Mode:SearchMode.Keyword))).Hits.Single().Source?.Location.Range=="A180:B180");
foreach(var badQuery in new[]{"amount:12,34","date:2026-02-30","cell:XFE1","sheet:\"unterminated"})
{
    try {await search.SearchAsync(new(badQuery,Mode:SearchMode.Keyword));Check("Invalid filter rejects "+badQuery,false);}
    catch(ArgumentException){Check("Invalid filter rejects "+badQuery,true);}
}
await using(var sql=new SqliteConnection($"Data Source={repository.DatabasePath};Pooling=false"))
{
    await sql.OpenAsync();
    using var command=sql.CreateCommand();command.CommandText="PRAGMA user_version";
    Check("No schema migration",Convert.ToInt64(await command.ExecuteScalarAsync())==5);
    command.CommandText="SELECT info FROM files WHERE path=$path";command.Parameters.AddWithValue("$path",word);var originalInfo=(string)(await command.ExecuteScalarAsync())!;
    command.CommandText="UPDATE files SET info=$info WHERE path=$path";command.Parameters.AddWithValue("$info","{\"_sourceAnchorsV1\":[]}");await command.ExecuteNonQueryAsync();
    try{await repository.GetFileAsync(word);Check("Malformed source metadata is not empty",false);}catch(InvalidDataException){Check("Malformed source metadata is not empty",true);}
    command.Parameters["$info"].Value=originalInfo;await command.ExecuteNonQueryAsync();
}
Check("Source map intact after malformed-data rejection",(await repository.GetFileAsync(word))!.Segments.Any(s=>s.Source?.Location.Paragraph==4));
var duplicate=Path.Combine(root,"duplicate.docx");
using(var zip=ZipFile.Open(duplicate,ZipArchiveMode.Create))
    foreach(var _ in new[]{0,1}){using var z=zip.CreateEntry("[Content_Types].xml").Open();z.Write(Encoding.UTF8.GetBytes("<x/>"));}
await Reject("Duplicate ZIP parts rejected",duplicate,typeof(InvalidDataException));
var traversal=Package("traversal.docx",new(){["../outside.xml"]="<x/>"});
await Reject("Traversal parts rejected without extraction",traversal,typeof(InvalidDataException));
var result = new { scope = "synthetic Office + real SQLite/index/search; fake vectors only; no OCR/model/Office process execution", passed = checks.Count, failed = 0, checks };
await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"DocumentSearch: {checks.Count}/{checks.Count}");

sealed class SyntheticEmbeddings : IEmbeddingService
{
    public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new EmbeddingInfo("synthetic-doc-profile",768,"synthetic","fixture","fixture","cpu","synthetic",["text"]));
    public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default)
    {
        var values = inputs.Select(_ => { var v = new float[768]; v[0] = 1; return new EmbeddingResult(v); }).ToArray();
        return Task.FromResult<IReadOnlyList<EmbeddingResult>>(values);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
