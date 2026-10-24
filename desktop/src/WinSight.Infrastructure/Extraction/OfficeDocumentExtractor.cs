using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WinSight.Core;
using WinSight.Infrastructure.Search;
using W = DocumentFormat.OpenXml.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;

namespace WinSight.Infrastructure.Extraction;

internal static class OfficeDocumentExtractor
{
    internal const string Revision = "office-source-units-v1";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    internal static string Identity(DocumentExtractionOptions options) => $"{Revision};comments={options.IncludeWordComments};headers={options.IncludeWordHeadersFooters}";

    internal static ExtractionResult Read(Stream stream, string extension, string title,
        DocumentExtractionOptions options, CancellationToken token)
    {
        GuardPackage(stream, token);
        var output = new Collector(token);
        int? sourceUnits = null;
        var settings = new OpenSettings { MaxCharactersInPart = 8L << 20 };
        switch (extension)
        {
            case "docx":
                using (var doc = WordprocessingDocument.Open(stream, false, settings)) ReadWord(doc, options, output);
                break;
            case "pptx":
                using (var doc = PresentationDocument.Open(stream, false, settings)) sourceUnits = ReadSlides(doc, output);
                break;
            case "xlsx":
                using (var doc = SpreadsheetDocument.Open(stream, false, settings)) ReadSheets(doc, output);
                break;
            default: throw new ArgumentException("Unsupported Office package.", nameof(extension));
        }
        if (output.Secret) return new([], "skipped", "Content holds a private key or NUL characters.");
        return new(output.Segments, output.Partial ? "partial" : output.Segments.Count == 0 ? "skipped" : "done",
            output.Partial ? string.Join(" ", output.Problems.Order()) : output.Segments.Count == 0 ? "No searchable Office text." : null,
            JsonSerializer.Serialize(new { title, sourceRevision = Identity(options), segments = output.Segments.Count, sourceUnits, nativeVisualEligible = extension == "pptx",
                visualCoverage = extension == "pptx" ? "not-rendered; text/tables/notes only" : "not-applicable" }));
    }

    private static void GuardPackage(Stream stream, CancellationToken token)
    {
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (zip.Entries.Count > 10000) throw new InvalidDataException("Office package exceeds 10000 entries.");
            long expanded = 0, nodes = 0;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                expanded = checked(expanded + entry.Length);
                if (expanded > 64L << 20 || !names.Add(entry.FullName) || entry.FullName.Length > 1024
                    || entry.FullName.Split('/').Contains("..") || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/'))
                    throw new InvalidDataException("Office package has ambiguous entries or exceeds 64 MiB expanded.");
                if (entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Macro-bearing packages are not supported.");
                if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    && !entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Length > 8L << 20) throw new InvalidDataException("Office XML part exceeds 8 MiB.");
                using var xml = entry.Open();
                using var reader = XmlReader.Create(xml, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8L << 20 });
                while (reader.Read())
                {
                    if (++nodes > 1_000_000 || reader.Depth > 128) throw new InvalidDataException("Office XML node/depth limit exceeded.");
                    if (nodes % 1024 == 0) token.ThrowIfCancellationRequested();
                }
            }
        }
        stream.Position = 0;
    }

    private static void ReadWord(WordprocessingDocument document, DocumentExtractionOptions options, Collector output)
    {
        var main = document.MainDocumentPart ?? throw new InvalidDataException("DOCX has no main document part.");
        var body = main.Document?.Body ?? throw new InvalidDataException("DOCX has no body.");
        WordPart(body, "body", output);
        if (options.IncludeWordHeadersFooters)
        {
            foreach (var part in main.HeaderParts.OrderBy(p => p.Uri.ToString(), StringComparer.Ordinal)) WordPart(part.Header ?? throw new InvalidDataException("Missing DOCX header."), part.Uri.ToString(), output);
            foreach (var part in main.FooterParts.OrderBy(p => p.Uri.ToString(), StringComparer.Ordinal)) WordPart(part.Footer ?? throw new InvalidDataException("Missing DOCX footer."), part.Uri.ToString(), output);
        }
        if (options.IncludeWordComments && main.WordprocessingCommentsPart?.Comments is { } comments)
            foreach (var comment in comments.Elements<W.Comment>())
                WordPart(comment, "comment:" + (comment.Id?.Value ?? throw new InvalidDataException("DOCX comment has no id.")), output);
    }

    private static void WordPart(OpenXmlElement root, string part, Collector output)
    {
        int number = 0;
        var emittedRows = new HashSet<W.TableRow>();
        foreach (var paragraph in root.Descendants<W.Paragraph>())
        {
            output.Check();
            if (output.Full) break;
            number++;
            if (paragraph.Ancestors().Any(a => a.LocalName is "del" or "moveFrom")) continue;
            var row = paragraph.Ancestors<W.TableRow>().FirstOrDefault();
            string text;
            IReadOnlyList<SearchField>? fields = null;
            if (row is not null)
            {
                if (!emittedRows.Add(row)) continue;
                var table = row.Ancestors<W.Table>().First();
                var headers = table.Elements<W.TableRow>().FirstOrDefault()?.Elements<W.TableCell>().Select(WordText).ToArray() ?? [];
                var values = row.Elements<W.TableCell>().Select(WordText).ToArray();
                (text, fields) = RowContext(headers, values);
            }
            else text = WordText(paragraph);
            output.Add(text, new(new("paragraph", Paragraph: number, Part: part), "body", fields), "chunk", number);
        }
    }

    private static string WordText(OpenXmlElement node) => node is W.Paragraph
        ? string.Concat(node.Descendants().Where(t => t is W.Text || t.LocalName is "tab" or "br" or "cr")
            .Where(t => !t.Ancestors().Any(a => a.LocalName is "del" or "moveFrom"))
            .Select(t => t is W.Text text ? text.Text : t.LocalName == "tab" ? "\t" : "\n")).Trim()
        : string.Join("\n", node.Descendants<W.Paragraph>().Select(WordText));

    private static int ReadSlides(PresentationDocument document, Collector output)
    {
        var presentation = document.PresentationPart ?? throw new InvalidDataException("PPTX has no presentation part.");
        var slides = presentation.Presentation?.SlideIdList ?? throw new InvalidDataException("PPTX has no slide list.");
        int number = 0;
        foreach (var slideId in slides.Elements<P.SlideId>())
        {
            output.Check();
            if (output.Full) break;
            number++;
            if (slideId.RelationshipId?.Value is not { } id || presentation.GetPartById(id) is not SlidePart slide)
                throw new InvalidDataException("PPTX slide relationship is invalid.");
            var location = new SourceLocation("slide", Page: number);
            var slideRoot = slide.Slide ?? throw new InvalidDataException("PPTX slide has no XML root.");
            foreach (var shape in slideRoot.Descendants<P.Shape>())
            {
                var text = DrawingText(shape);
                var title = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<P.PlaceholderShape>()?.Type?.Value;
                if (title == P.PlaceholderValues.Title || title == P.PlaceholderValues.CenteredTitle) text = "Title: " + text;
                output.Add(text, new(location), "page", number);
            }
            foreach (var table in slideRoot.Descendants<A.Table>())
            {
                var headers = table.Elements<A.TableRow>().FirstOrDefault()?.Elements<A.TableCell>().Select(DrawingText).ToArray() ?? [];
                foreach (var row in table.Elements<A.TableRow>())
                {
                    var (text, fields) = RowContext(headers, row.Elements<A.TableCell>().Select(DrawingText).ToArray());
                    output.Add(text, new(location, "body", fields), "page", number);
                }
            }
            if (slide.NotesSlidePart?.NotesSlide is { } notes)
                foreach (var shape in notes.Descendants<P.Shape>())
                {
                    var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<P.PlaceholderShape>()?.Type?.Value;
                    if (placeholder == P.PlaceholderValues.SlideNumber || placeholder == P.PlaceholderValues.Header || placeholder == P.PlaceholderValues.Footer || placeholder == P.PlaceholderValues.DateAndTime || placeholder == P.PlaceholderValues.SlideImage) continue;
                    output.Add(DrawingText(shape), new(location, "notes"), "page", number);
                }
        }
        return slides.Elements<P.SlideId>().Count();
    }

    private static string DrawingText(OpenXmlElement node) => string.Join("\n", node.Descendants<A.Paragraph>()
        .Select(p => string.Concat(p.Descendants<A.Text>().Select(t => t.Text)))).Trim();

    private static void ReadSheets(SpreadsheetDocument document, Collector output)
    {
        var book = document.WorkbookPart ?? throw new InvalidDataException("XLSX has no workbook part.");
        var strings = book.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
        if (strings.Length > 200000) throw new InvalidDataException("XLSX shared string count exceeds limit.");
        var workbook = book.Workbook ?? throw new InvalidDataException("XLSX has no workbook XML.");
        bool date1904 = workbook.WorkbookProperties?.Date1904?.Value == true;
        foreach (var sheet in workbook.Sheets?.Elements<S.Sheet>() ?? [])
        {
            output.Check();
            if (output.Full) break;
            if (sheet.State?.Value == S.SheetStateValues.Hidden || sheet.State?.Value == S.SheetStateValues.VeryHidden)
            { output.Omit("Hidden worksheets were not indexed."); continue; }
            if (sheet.Id?.Value is not { } id || book.GetPartById(id) is not WorksheetPart worksheet)
                throw new InvalidDataException("XLSX worksheet relationship is invalid.");
            var name = sheet.Name?.Value ?? throw new InvalidDataException("XLSX sheet has no name.");
            Dictionary<int, string> headers = [];
            int inferredRow = 0;
            var sheetRoot = worksheet.Worksheet ?? throw new InvalidDataException("XLSX worksheet has no XML root.");
            foreach (var row in sheetRoot.Descendants<S.Row>())
            {
                output.Check();
                if (output.Full) break;
                var rowIndex = checked((int)(row.RowIndex?.Value ?? (uint)(inferredRow + 1)));
                if (rowIndex <= inferredRow) throw new InvalidDataException("XLSX rows must have unique increasing indices.");
                inferredRow = rowIndex;
                if (rowIndex is < 1 or > 1048576) throw new InvalidDataException("Invalid XLSX row index.");
                if (row.Hidden?.Value == true) { output.Omit("Hidden rows were not indexed."); continue; }
                var cells = new SortedDictionary<int, string>();
                int column = 0;
                var seenColumns = new HashSet<int>();
                foreach (var cell in row.Elements<S.Cell>())
                {
                    if (cell.CellReference?.Value is { } reference)
                    {
                        var match = Regex.Match(reference, "^([A-Z]{1,3})([1-9][0-9]{0,6})$", RegexOptions.CultureInvariant, Timeout);
                        if (!match.Success || int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) != rowIndex)
                            throw new InvalidDataException("XLSX cell reference disagrees with its row.");
                        column = ColumnNumber(match.Groups[1].Value);
                    }
                    else column++;
                    if (column > 16384 || !seenColumns.Add(column)) throw new InvalidDataException("Invalid/duplicate XLSX column.");
                    var value = CellText(cell, book, strings, date1904, output);
                    if (value.Length != 0) cells.Add(column, value);
                }
                if (cells.Count == 0) continue;
                if (headers.Count == 0 && cells.Values.Any(value => DocumentSearchQuery.HeaderField(value) is not null))
                    headers = new(cells);
                var labels = cells.Keys.Select(c => headers.GetValueOrDefault(c, ColumnName(c))).ToArray();
                var (text, fields) = RowContext(labels, cells.Values.ToArray());
                var range = $"{ColumnName(cells.Keys.First())}{rowIndex}:{ColumnName(cells.Keys.Last())}{rowIndex}";
                output.Add(text, new(new("sheet", Sheet: name, Range: range), "table", fields), "cells", rowIndex);
            }
            if (headers.Count == 0) output.Omit("Worksheet header context could not be established; columns use original cell letters.");
        }
    }

    private static string CellText(S.Cell cell, WorkbookPart book, string[] strings, bool date1904, Collector output)
    {
        var raw = cell.CellValue?.Text ?? "";
        if (cell.CellFormula is not null && cell.CellValue is null)
        { output.Omit("Formula has no cached value; formulas were not evaluated."); return ""; }
        if (cell.CellFormula is not null) output.Omit("Formula values are saved caches, not recalculated.");
        if (cell.DataType?.Value == S.CellValues.SharedString)
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= strings.Length)
                throw new InvalidDataException("Invalid XLSX shared-string index.");
            return strings[index];
        }
        if (cell.DataType?.Value == S.CellValues.InlineString) return cell.InlineString?.InnerText ?? "";
        if (cell.DataType?.Value == S.CellValues.Boolean) return raw switch { "0" => "false", "1" => "true", _ => throw new InvalidDataException("Invalid XLSX Boolean.") };
        if (cell.DataType?.Value is { } type && type != S.CellValues.Number) return raw;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return raw;
        var style = cell.StyleIndex?.Value ?? 0;
        var format = book.WorkbookStylesPart?.Stylesheet?.CellFormats?.Elements<S.CellFormat>().ElementAtOrDefault(checked((int)style));
        uint formatId = format?.NumberFormatId?.Value ?? 0;
        var custom = book.WorkbookStylesPart?.Stylesheet?.NumberingFormats?.Elements<S.NumberingFormat>()
            .FirstOrDefault(f => f.NumberFormatId?.Value == formatId)?.FormatCode?.Value ?? "";
        if (Regex.IsMatch(custom, "^0{2,20}$", RegexOptions.CultureInvariant, Timeout) && number >= 0 && number == Math.Truncate(number))
            return number.ToString(custom, CultureInfo.InvariantCulture);
        var unquoted = Regex.Replace(custom, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "", RegexOptions.CultureInvariant, Timeout);
        bool calendar = formatId is >= 14 and <= 22 || Regex.IsMatch(unquoted, "(?i)y{2,4}|d{2,4}", RegexOptions.CultureInvariant, Timeout);
        if (!calendar) return raw;
        if (!date1904 && number >= 60 && number < 61)
        { output.Omit("Excel serial60 is a nonexistent calendar date; raw cache retained."); return raw; }
        try { return DateTime.FromOADate(number + (date1904 ? 1462 : number is >= 1 and < 60 ? 1 : 0)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        catch (ArgumentException) { output.Omit("Invalid Excel date cache retained as raw value."); return raw; }
    }

    private static int ColumnNumber(string value) => value.Aggregate(0, (current, c) => checked(current * 26 + c - 'A' + 1));
    private static string ColumnName(int value)
    {
        var result = "";
        while (value > 0) { value--; result = (char)('A' + value % 26) + result; value /= 26; }
        return result;
    }

    private static (string, IReadOnlyList<SearchField>) RowContext(string[] labels, string[] values)
    {
        var fields = new List<SearchField>();
        var text = new List<string>();
        for (int i = 0; i < values.Length; i++)
        {
            var label = i < labels.Length ? labels[i] : "";
            text.Add($"{label}: {values[i]}");
            {
                var name = DocumentSearchQuery.HeaderField(label);
                if (name is not null && values[i].Length <= 256) fields.Add(new(name, values[i]));
            }
        }
        return (string.Join(" | ", text), fields);
    }

    private sealed class Collector(CancellationToken token)
    {
        internal List<IndexSegment> Segments { get; } = [];
        internal HashSet<string> Problems { get; } = new(StringComparer.Ordinal);
        internal bool Partial => Problems.Count != 0;
        internal bool Secret { get; private set; }
        internal bool Full
        {
            get
            {
                var reached = Segments.Count >= ContentExtractor.MaxSegments || characters >= ContentExtractor.MaxCharacters;
                if (reached) Omit("Office source-unit/character limit reached.");
                return reached;
            }
        }
        private int characters;
        internal void Check() => token.ThrowIfCancellationRequested();
        internal void Omit(string reason) => Problems.Add(reason);
        internal void Add(string text, SourceAnchor source, string kind, int location)
        {
            Check();
            if (text.Contains("PRIVATE KEY-----", StringComparison.Ordinal) || text.Contains('\0')) { Secret = true; return; }
            if (string.IsNullOrWhiteSpace(text)) return;
            var fields = (source.Fields ?? []).Concat(DocumentSearchQuery.FieldsFromText(text)).Distinct().ToArray();
            if (fields.Length > 64) Omit("Structured fields exceeded64 per source unit; full text remains searchable.");
            source = source with { Fields = fields.Take(64).ToArray() };
            if (Full) { Omit("Office source-unit/character limit reached."); return; }
            for (int offset = 0; offset < text.Length;)
            {
                if (Full) { Omit("Office source-unit/character limit reached."); break; }
                int length = Math.Min(Math.Min(1800, text.Length - offset), ContentExtractor.MaxCharacters - characters);
                if (length > 0 && char.IsHighSurrogate(text[offset + length - 1])) length--;
                if (length <= 0) { Omit("Office character limit reached."); break; }
                var excerpt = text.Substring(offset, length);
                Segments.Add(new(kind, "text", location, location, excerpt, Source: source));
                characters += length;
                if (offset + length >= text.Length) break;
                offset += Math.Max(1, length - 200);
                if (offset < text.Length && char.IsLowSurrogate(text[offset])) offset++;
            }
        }
    }
}
