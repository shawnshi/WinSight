using System.Text.Json;
using System.Text.RegularExpressions;
using WinSight.Core;
using WinSight.Infrastructure.Media;

namespace WinSight.Infrastructure.Extraction;

internal static class NativeImageExtraction
{
    internal const string Revision = "native-image-exif-ocr-v1";

    internal static ExtractionResult Create(NativeImageInspection image, string title)
    {
        var metadata = image.Exif.Count == 0 ? "" : "Image metadata: " + string.Join("; ", image.Exif.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));
        if (image.OcrText.Contains("PRIVATE KEY-----", StringComparison.Ordinal) || metadata.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            return new([], "skipped", "Image text/metadata contains private-key material; content was not indexed.");
        var segments = new List<IndexSegment> { new("image", "image", null, null, null) };
        if (metadata.Length > 0) AddText(segments, metadata);
        if (image.OcrText.Length > 0)
        {
            // Windows OCR separates Chinese glyphs with spaces; compact only adjacent Han glyphs,
            // never Latin words/numbers or line boundaries, so phrase keyword search remains usable.
            var text = Regex.Replace(image.OcrText, @"(?<=[\u3400-\u9fff\uf900-\ufaff])[ \t]+(?=[\u3400-\u9fff\uf900-\ufaff])", "",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            AddText(segments, text);
        }
        var info = JsonSerializer.Serialize(new
        {
            title, native_image_revision = Revision, width = image.Width, height = image.Height,
            orientation = image.Orientation, frame_count = image.FrameCount, exif = image.Exif,
            ocr_languages = image.OcrLanguages, ocr_width = image.OcrWidth, ocr_height = image.OcrHeight,
            ocr_characters = image.OcrText.Length, segments = segments.Count
        });
        return new(segments, image.State, image.Detail, info);
    }

    internal static bool IsCurrent(string? info)
    {
        if (info == null) return false;
        try
        {
            using var document = JsonDocument.Parse(info);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("native_image_revision", out var revision)
                && revision.ValueKind == JsonValueKind.String && revision.GetString() == Revision;
        }
        catch (JsonException) { return false; }
    }

    private static void AddText(List<IndexSegment> segments, string text)
    {
        for (int start = 0; start < text.Length;)
        {
            int end = Math.Min(start + 1800, text.Length);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
            segments.Add(new IndexSegment("chunk", "text", null, null, text[start..end]));
            if (end == text.Length) break;
            start = Math.Max(start + 1, end - 200);
            if (char.IsLowSurrogate(text[start])) start++;
        }
    }
}
