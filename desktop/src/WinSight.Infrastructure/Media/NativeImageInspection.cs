using System.Text.Json;
using WinSight.Shared;
using WinSight.Core;

namespace WinSight.Infrastructure.Media;

public sealed record NativeImageInspection(string SourcePath, int Width, int Height, int Orientation, int FrameCount,
    IReadOnlyDictionary<string, string> Exif, string OcrText, IReadOnlyList<string> OcrLanguages,
    int OcrWidth, int OcrHeight, string State, string? Detail)
{
    public string? OcrEngine { get; init; }
    public string? OcrEngineVersion { get; init; }
    public string? OcrModelProfile { get; init; }
    public IReadOnlyList<OcrTextRegion> OcrRegions { get; init; } = [];
    public bool OcrRegionsTruncated { get; init; }

    internal static NativeImageInspection Parse(string output, string expectedSource)
    {
        try
        {
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object, "Image response must be an object.");
            var required = new HashSet<string>(["protocol", "sourcePath", "width", "height", "orientation", "frameCount", "exif",
                "ocrText", "ocrLanguages", "ocrWidth", "ocrHeight", "state", "detail", "ocrEngine", "ocrEngineVersion", "ocrModelProfile", "ocrRegions", "ocrRegionsTruncated"], StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                Require(required.Remove(field.Name), "Unknown or duplicate image response field.");
            Require(required.Count == 0 && root.GetProperty("protocol").GetInt32() == 3, "Incomplete/unsupported image response protocol.");
            Require(String(root, "ocrEngine") == OcrIdentity.Engine && String(root, "ocrEngineVersion") == OcrIdentity.EngineVersion
                && String(root, "ocrModelProfile") == OcrIdentity.ModelProfile, "Image OCR engine/model identity mismatch.");
            string source = String(root, "sourcePath");
            Require(string.Equals(MediaTaskFiles.LocalPath(source), expectedSource, StringComparison.OrdinalIgnoreCase), "Image response source identity mismatch.");
            int width = root.GetProperty("width").GetInt32(), height = root.GetProperty("height").GetInt32();
            int orientation = root.GetProperty("orientation").GetInt32(), frames = root.GetProperty("frameCount").GetInt32();
            int ocrWidth = root.GetProperty("ocrWidth").GetInt32(), ocrHeight = root.GetProperty("ocrHeight").GetInt32();
            Require(width is > 0 and <= 32768 && height is > 0 and <= 32768 && (long)width * height <= 40_000_000,
                "Image dimensions exceed the response bound.");
            Require(orientation is >= 1 and <= 8 && frames > 0 && ocrWidth is > 0 and <= 2600 && ocrHeight is > 0 and <= 2600,
                "Invalid image orientation/frame/OCR dimensions.");
            var exifElement = root.GetProperty("exif");
            Require(exifElement.ValueKind == JsonValueKind.Object, "EXIF must be a bounded object.");
            var allowed = new HashSet<string>(["cameraManufacturer", "cameraModel", "dateTaken", "exposureTime", "fNumber", "isoSpeed", "focalLength", "imageDescription", "software"], StringComparer.Ordinal);
            var exif = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in exifElement.EnumerateObject())
            {
                Require(allowed.Contains(entry.Name) && !exif.ContainsKey(entry.Name) && entry.Value.ValueKind == JsonValueKind.String,
                    "Unknown, duplicate or non-string EXIF property.");
                string value = entry.Value.GetString()!;
                Require(value.Length <= 256 && !value.Any(char.IsControl), "EXIF string exceeds its safe bound.");
                exif.Add(entry.Name, value);
            }
            string text = String(root, "ocrText");
            Require(text.Length <= 8000 && !text.Contains('\0'), "OCR text exceeds its safe bound.");
            var languagesElement = root.GetProperty("ocrLanguages");
            Require(languagesElement.ValueKind == JsonValueKind.Array && languagesElement.GetArrayLength() <= 2, "OCR languages exceed their bound.");
            var languages = new List<string>();
            foreach (var language in languagesElement.EnumerateArray())
            {
                Require(language.ValueKind == JsonValueKind.String, "OCR language must be a string.");
                var tag = language.GetString()!;
                Require(tag.Length is > 0 and <= 32 && tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                    && !languages.Contains(tag, StringComparer.OrdinalIgnoreCase), "Invalid or duplicate OCR language.");
                languages.Add(tag);
            }
            Require(languages.SequenceEqual(new[] { "zh-Hans-CN", "en-US" }), "Image OCR language profile mismatch.");
            var state = String(root, "state");
            var detailElement = root.GetProperty("detail");
            Require(detailElement.ValueKind is JsonValueKind.Null or JsonValueKind.String, "Invalid image detail type.");
            var detail = detailElement.ValueKind == JsonValueKind.Null ? null : detailElement.GetString();
            Require(detail is null || detail.Length <= 4096, "Image detail exceeds its bound.");
            Require(state == "done" && languages.Count > 0 && frames == 1 && detail == null
                || state == "partial" && !string.IsNullOrWhiteSpace(detail), "Image completion/coverage contract is inconsistent.");
            var regions = new List<OcrTextRegion>();
            var geometry = root.GetProperty("ocrRegions");
            Require(geometry.ValueKind == JsonValueKind.Array && geometry.GetArrayLength() <= 96, "OCR region count invalid.");
            int lastLine = -1, lastEnd = 0;
            foreach (var region in geometry.EnumerateArray())
            {
                Require(region.ValueKind == JsonValueKind.Object, "OCR region must be an object.");
                var keys = new HashSet<string>(["line", "left", "top", "width", "height", "start", "length"], StringComparer.Ordinal);
                foreach (var property in region.EnumerateObject()) Require(keys.Remove(property.Name), "Unknown/duplicate OCR region member.");
                Require(keys.Count == 0, "OCR region incomplete.");
                int line = region.GetProperty("line").GetInt32(), start = region.GetProperty("start").GetInt32(), length = region.GetProperty("length").GetInt32();
                Require(start >= lastEnd && length > 0 && start <= text.Length - length && !char.IsLowSurrogate(text[start]) && !char.IsHighSurrogate(text[start + length - 1]), "OCR region text range invalid.");
                double left = region.GetProperty("left").GetDouble(), top = region.GetProperty("top").GetDouble(), w = region.GetProperty("width").GetDouble(), h = region.GetProperty("height").GetDouble();
                Require(line > lastLine && line <= 5000 && double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(w) && double.IsFinite(h)
                    && left >= 0 && top >= 0 && w > 0 && h > 0 && left + w <= 1 + 1e-9 && top + h <= 1 + 1e-9, "OCR region bounds/order invalid.");
                regions.Add(new(line, left, top, w, h, text.Substring(start, length))); lastLine = line; lastEnd = start + length;
            }
            bool geometryTruncated = root.GetProperty("ocrRegionsTruncated").GetBoolean();
            Require(!geometryTruncated || state == "partial", "Truncated geometry cannot claim complete coverage.");
            Require(string.IsNullOrWhiteSpace(text) || regions.Count > 0 || geometryTruncated, "Text has no located OCR regions.");
            return new(source, width, height, orientation, frames, exif, text, languages, ocrWidth, ocrHeight, state, detail)
            { OcrEngine = OcrIdentity.Engine, OcrEngineVersion = OcrIdentity.EngineVersion, OcrModelProfile = OcrIdentity.ModelProfile,
                OcrRegions = regions, OcrRegionsTruncated = geometryTruncated };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        {
            throw new InvalidDataException("Malformed native image response.", error);
        }
    }

    private static string String(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.String, "Image member must be a string: " + name);
        return value.GetString()!;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
