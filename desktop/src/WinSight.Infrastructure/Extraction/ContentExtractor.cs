using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinSight.Core;
using WinSight.Infrastructure.Media;
using WinSight.Infrastructure.Search;
using UglyToad.PdfPig;

namespace WinSight.Infrastructure.Extraction;

public sealed class ContentExtractor : IContentExtractor
{
    private readonly NativeMediaProcessClient? nativeMedia;
    public ContentExtractor() { }
    internal ContentExtractor(NativeMediaProcessClient? nativeMedia) => this.nativeMedia = nativeMedia;
    internal const int MaxCharacters = 9_000_000;
    internal const int MaxSegments = 5000;
    internal const long MaxBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public bool CanExtract(string path, bool code) => FilePolicy.Kind(path, code) is not null;

    public Task<ExtractionResult> ExtractAsync(string path, bool code, CancellationToken cancellationToken = default) =>
        ExtractAsync(path, code, new DocumentExtractionOptions(), cancellationToken);

    public async Task<ExtractionResult> ExtractAsync(string path, bool code, DocumentExtractionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = FilePolicy.Normalize(path);
        var kind = FilePolicy.Kind(path, code);
        if (kind is null) return new([], "skipped", "Unsupported, generated, data or secret file type.");
        using var file = SafeFile.Open(path);
        var stream = file.Stream!;
        var limit = code ? FilePolicy.Extension(path) == "ipynb" ? 20L << 20 : 1L << 20 : MaxBytes;
        if (stream.Length > limit) return new([], "skipped", $"File exceeds extraction byte limit ({limit}).");
        if (kind is "audio" or "video")
            return new([], "skipped", "Unsupported: native audio windows/video frame extraction is not implemented; no whole-file or filename-only semantic fallback.");
        if (kind is "image" or "screenshot")
        {
            if (nativeMedia is null)
                return new([new("image", "image", null, null, null)], "partial", "Native image inspection is not configured; EXIF/OCR is unavailable.");
            var image = await nativeMedia.InspectImageAsync(path, cancellationToken).ConfigureAwait(false);
            return NativeImageExtraction.Create(image, Title(path));
        }
        if (kind == "pdf") return ReadPdf(stream, path, cancellationToken);
        var ext = FilePolicy.Extension(path);
        if (code && ext is "ts" or "mts")
        {
            var header = new byte[600];
            var length = await stream.ReadAsync(header, cancellationToken);
            stream.Position = 0;
            if (length >= 400 && (header[0] == 0x47 && header[188] == 0x47 && header[376] == 0x47
                || header[4] == 0x47 && header[196] == 0x47 && header[388] == 0x47))
                return new([], "skipped", "MPEG transport stream, not TypeScript source.");
        }
        if (code && ext == "ipynb") return await ReadNotebookAsync(stream, path, cancellationToken);
        if (!code && ext is "doc" or "odt" or "rtf")
            return new([], "skipped", $"Unsupported legacy/rich document type: .{ext}; not treated as plain text.");
        if (!code && ext is "docx" or "pptx" or "xlsx")
            return OfficeDocumentExtractor.Read(stream, ext, Path.GetFileNameWithoutExtension(path), options, cancellationToken);
        var (text, truncated) = await ReadTextAsync(stream, cancellationToken);
        if (!code && ext is "html" or "htm") text = HtmlText(text);
        if (text.Contains("PRIVATE KEY-----", StringComparison.Ordinal)) return new([], "skipped", "Content holds a private key.");
        if (text.Contains('\0')) return new([], "skipped", "Not text (NUL characters).");
        if (code)
        {
            var reason = CodeRejection(text, FilePolicy.Sources.Contains(ext));
            if (reason is not null) return new([], "skipped", reason);
            var (segments, cut) = CodeLines(text, cancellationToken);
            return Result(segments, truncated || cut, "Code input or line/segment limit reached.", Title(path));
        }
        var (chunks, omitted) = Chunks(text, cancellationToken);
        return Result(chunks, truncated || omitted, "Document text/chunk limit reached.", Path.GetFileNameWithoutExtension(path));
    }

    private static ExtractionResult Result(List<IndexSegment> segments, bool partial, string detail, string title) =>
        new(segments, partial ? "partial" : segments.Count == 0 ? "skipped" : "done",
            partial ? detail : segments.Count == 0 ? "No searchable text." : null,
            JsonSerializer.Serialize(new { title, segments = segments.Count }));

    private static async Task<(string Text, bool Truncated)> ReadTextAsync(Stream stream, CancellationToken token)
    {
        // Match the portable UTF-8/Latin-1 fallback, but honor UTF-16 BOMs rather than reading their NULs as binary.
        try { return await ReadDecodedAsync(stream, new UTF8Encoding(false, true), token); }
        catch (DecoderFallbackException)
        {
            stream.Position = 0;
            return await ReadDecodedAsync(stream, Encoding.Latin1, token);
        }
    }
    private static async Task<(string Text, bool Truncated)> ReadDecodedAsync(Stream stream, Encoding encoding, CancellationToken token)
    {
        using var reader = new StreamReader(stream, encoding, true, 65536, true);
        var output = new StringBuilder();
        var buffer = new char[16384];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxCharacters + 1 - output.Length)), token);
            if (count == 0) return (output.ToString(), false);
            output.Append(buffer, 0, count);
            if (output.Length > MaxCharacters) return (Prefix(output.ToString(), MaxCharacters), true);
        }
    }

    private static ExtractionResult ReadPdf(Stream stream, string path, CancellationToken token)
    {
        using var pdf = PdfDocument.Open(stream);
        var segments = new List<IndexSegment>();
        var partial = pdf.NumberOfPages > MaxSegments;
        var textLimited = partial;
        var chars = 0;
        var noText = 0;
        var rasterPages = new List<int>();
        for (var i = 1; i <= Math.Min(pdf.NumberOfPages, MaxSegments); i++)
        {
            token.ThrowIfCancellationRequested();
            var page = pdf.GetPage(i);
            if (page.NumberOfImages > 0) rasterPages.Add(i);
            var text = string.Join(" ", page.GetWords().Select(w => w.Text)).Trim();
            // PDF rendering/OCR are not available through PdfPig; scans must not look successfully indexed.
            if (text.Length < 200) { noText++; partial = true; }
            if (text.Length == 0) continue;
            var remaining = Math.Min(65536, MaxCharacters - chars);
            if (text.Length > remaining) { text = Prefix(text, remaining); partial = true; textLimited = true; }
            if (text.Length > 0) segments.Add(new("page", "text", i, null, text, Source: new(new("pdf-page", Page: i), Fields: DocumentSearchQuery.FieldsFromText(text))));
            chars += text.Length;
            if (chars >= MaxCharacters) { textLimited |= i < pdf.NumberOfPages; partial |= textLimited; break; }
        }
        return new(segments, partial ? "partial" : segments.Count == 0 ? "skipped" : "done",
            partial ? $"Text-only PDF: {noText} short/scan pages need rendering/OCR; limits {MaxSegments} pages/{MaxCharacters} characters. Text order/layout may differ from PDFium." : null,
            JsonSerializer.Serialize(new { title = string.IsNullOrWhiteSpace(pdf.Information.Title) ? Path.GetFileNameWithoutExtension(path) : pdf.Information.Title, pages = pdf.NumberOfPages, short_pages = noText, text_limits_reached = textLimited, raster_pages = rasterPages }));
    }

    private static async Task<ExtractionResult> ReadNotebookAsync(Stream stream, string path, CancellationToken token)
    {
        using var notebook = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 64 }, token);
        var root = notebook.RootElement;
        JsonElement cells;
        if (!root.TryGetProperty("cells", out cells))
        {
            if (!root.TryGetProperty("worksheets", out var sheets) || sheets.ValueKind != JsonValueKind.Array || sheets.GetArrayLength() == 0 || !sheets[0].TryGetProperty("cells", out cells))
                throw new InvalidDataException("Not a supported Jupyter notebook.");
        }
        if (cells.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Notebook cells must be an array.");
        var segments = new List<IndexSegment>();
        var characters = 0;
        var number = 0;
        var partial = false;
        foreach (var cell in cells.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            number++;
            if (!cell.TryGetProperty("source", out var source) && !cell.TryGetProperty("input", out source)) continue;
            var text = source.ValueKind switch
            {
                JsonValueKind.String => source.GetString()!,
                JsonValueKind.Array => string.Concat(source.EnumerateArray().Select(s => s.GetString())),
                _ => throw new InvalidDataException("Notebook cell source must be a string or strings.")
            };
            characters += text.Length;
            if (characters > 1 << 20) return new([], "skipped", "Notebook source exceeds 1 MiB characters.");
            var rejected = CodeRejection(text, true);
            if (rejected is not null) return new([], "skipped", rejected);
            var (pieces, cut) = CodeLines(text, token);
            partial |= cut;
            foreach (var piece in pieces)
            {
                if (segments.Count >= MaxSegments) { partial = true; break; }
                segments.Add(piece with { Kind = "cells", Location = number, LocationEnd = number });
            }
            if (segments.Count >= MaxSegments) { partial = true; break; }
        }
        return Result(segments, partial, "Notebook line/cell limit reached.", Title(path));
    }

    private static string? CodeRejection(string text, bool source)
    {
        if (text.Contains("PRIVATE KEY-----", StringComparison.Ordinal)) return "Content holds a private key.";
        if (text.Contains('\0')) return "Not text (NUL characters).";
        if (!source) return null;
        var top = Prefix(text, 1000);
        if (new[] { "DO NOT EDIT", "@generated", "Code generated by", "Autogenerated by", "Generated by the protocol" }.Any(top.Contains)) return "Generated code.";
        var lines = text.Split('\n');
        return text.Length / Math.Max(1, lines.Length) > 300 && lines.Any(l => l.Length > 3000) ? "Minified code." : null;
    }

    private static (List<IndexSegment>, bool) CodeLines(string text, CancellationToken token)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var segments = new List<IndexSegment>();
        var partial = false;
        var start = 0;
        while (start < lines.Length && segments.Count < MaxSegments)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[start])) { start++; continue; }
            var end = start + 1;
            var length = lines[start].Length;
            while (end < lines.Length && length + 1 + lines[end].Length <= 1800) { length += 1 + lines[end].Length; end++; }
            var natural = end >= lines.Length;
            if (!natural)
            {
                var offset = length;
                for (var cut = end; cut > start + 1 && offset >= 720; cut--)
                {
                    if (string.IsNullOrWhiteSpace(lines[cut - 1]) || (cut < lines.Length && StartsBlock(lines[cut]))) { end = cut; natural = true; break; }
                    offset -= lines[cut - 1].Length + 1;
                }
            }
            var last = end;
            while (last > start + 1 && string.IsNullOrWhiteSpace(lines[last - 1])) last--;
            var body = string.Join("\n", lines[start..last]);
            if (body.Length > 4000) { body = Prefix(body, 4000); partial = true; }
            segments.Add(new("lines", "text", start + 1, last, body));
            start = natural ? end : Math.Max(end - 2, start + 1);
        }
        return (segments, partial || start < lines.Length);
    }
    private static bool StartsBlock(string line) => line.Length > 0 && !char.IsWhiteSpace(line[0]) && !"})]".Contains(line[0])
        && !new[] { "end", "fi", "done", "esac", "@end", "#endif", "end;", "end)" }.Contains(line.Trim());

    internal static (List<IndexSegment>, bool) Chunks(string text, CancellationToken token)
    {
        text = Regex.Replace(text, "[ \\t\u00A0]+", " ", RegexOptions.None, RegexTimeout).Trim();
        text = Regex.Replace(text, @"\n\s*\n\s*\n+", "\n\n", RegexOptions.None, RegexTimeout);
        var result = new List<IndexSegment>();
        var start = 0;
        while (start < text.Length && result.Count < MaxSegments)
        {
            token.ThrowIfCancellationRequested();
            var end = Math.Min(text.Length, start + 1800);
            if (end < text.Length)
            {
                for (var i = end - 1; i > start + 900; i--) if (char.IsWhiteSpace(text[i])) { end = i; break; }
                if (char.IsHighSurrogate(text[end - 1])) end--;
            }
            var body = text[start..end].Trim();
            if (body.Length > 0) result.Add(new("chunk", "text", result.Count + 1, null, body));
            if (end == text.Length) return (result, false);
            var next = Math.Max(start + 1, end - 200);
            while (next < end && !char.IsWhiteSpace(text[next])) next++;
            start = next;
        }
        return (result, start < text.Length);
    }

    private static string HtmlText(string html)
    {
        foreach (var pattern in new[] { @"<script\b.*?</script\s*>", @"<style\b.*?</style\s*>", @"<!--.*?-->", @"<head\b.*?</head\s*>" })
            html = Regex.Replace(html, pattern, " ", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        html = Regex.Replace(html, @"<(br|/p|/div|/li|/h[1-6]|/tr|/section|/article)\b[^>]*>", "\n", RegexOptions.IgnoreCase, RegexTimeout);
        return WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ", RegexOptions.None, RegexTimeout));
    }
    internal static string Prefix(string text, int length)
    {
        if (text.Length <= length) return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
    internal static string Title(string path)
    {
        // Only inspect ancestors' marker existence; never read repository contents or credentials for a title.
        var folder = Path.GetDirectoryName(path);
        while (folder is not null)
        {
            if (FilePolicy.ProjectMarkers.Any(marker => File.Exists(Path.Combine(folder, marker)) || Directory.Exists(Path.Combine(folder, marker))))
                return Path.GetFileName(folder) + "/" + Path.GetRelativePath(folder, path).Replace('\\', '/');
            folder = Path.GetDirectoryName(folder);
        }
        return Path.GetFileName(path);
    }
}
