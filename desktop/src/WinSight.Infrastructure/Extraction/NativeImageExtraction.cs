using System.Text.Json;
using System.Text.RegularExpressions;
using WinSight.Core;
using WinSight.Shared;
using WinSight.Infrastructure.Media;
using WinSight.Infrastructure.Search;

namespace WinSight.Infrastructure.Extraction;

internal static class NativeImageExtraction
{
    internal static readonly string Revision = OcrIdentity.ExtractionRevision;

    internal static ExtractionResult Create(NativeImageInspection image, string title)
    {
        var metadata = image.Exif.Count == 0 ? "" : "Image metadata: " + string.Join("; ", image.Exif.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));
        if (image.OcrText.Contains("PRIVATE KEY-----", StringComparison.Ordinal) || metadata.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            return new([], "skipped", "Image text/metadata contains private-key material; content was not indexed.");
        var fields = DocumentSearchQuery.FieldsFromText(metadata + "\n" + image.OcrText);
        var source = new SourceAnchor(new("image"), "ocr", fields, image.OcrRegions);
        var segments = new List<IndexSegment> { new("image", "image", null, null, null, Source: source with { Channel = "page-visual" }) };
        if (metadata.Length > 0) AddText(segments, metadata, source with { Channel = "metadata" });
        if (image.OcrText.Length > 0)
        {
            // OCR may separate Chinese glyphs with spaces; compact only adjacent Han glyphs,
            // never Latin words/numbers or line boundaries, so phrase keyword search remains usable.
            var text = NormalizeOcrText(image.OcrText);
            AddText(segments, text, source);
        }
        var info = JsonSerializer.Serialize(new
        {
            title, native_image_revision = Revision, width = image.Width, height = image.Height,
            orientation = image.Orientation, frame_count = image.FrameCount, exif = image.Exif,
            ocr_engine = image.OcrEngine, ocr_engine_version = image.OcrEngineVersion, ocr_model_profile = image.OcrModelProfile,
            ocr_languages = image.OcrLanguages, ocr_width = image.OcrWidth, ocr_height = image.OcrHeight,
            ocr_characters = image.OcrText.Length, ocr_regions = image.OcrRegions.Count, ocr_regions_truncated = image.OcrRegionsTruncated, segments = segments.Count
        });
        return new(segments, image.State, image.Detail, info);
    }

    internal static string NormalizeOcrText(string text) => Regex.Replace(text,
        @"(?<=[\u3400-\u9fff\uf900-\ufaff])[ \t]+(?=[\u3400-\u9fff\uf900-\ufaff])", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static bool IsCurrent(string? info)
    {
        if (info == null) return false;
        try
        {
            using var document = JsonDocument.Parse(info);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("native_image_revision", out var revision)
                && revision.ValueKind == JsonValueKind.String && revision.GetString() == Revision
                && document.RootElement.TryGetProperty("ocr_engine", out var engine) && engine.GetString() == OcrIdentity.Engine
                && document.RootElement.TryGetProperty("ocr_engine_version", out var version) && version.GetString() == OcrIdentity.EngineVersion
                && document.RootElement.TryGetProperty("ocr_model_profile", out var profile) && profile.GetString() == OcrIdentity.ModelProfile;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }

    private static void AddText(List<IndexSegment> segments, string text, SourceAnchor source)
    {
        for (int start = 0; start < text.Length;)
        {
            int end = Math.Min(start + 1800, text.Length);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
            segments.Add(new IndexSegment("chunk", "text", null, null, text[start..end], Source: source));
            if (end == text.Length) break;
            start = Math.Max(start + 1, end - 200);
            if (char.IsLowSurrogate(text[start])) start++;
        }
    }
}
