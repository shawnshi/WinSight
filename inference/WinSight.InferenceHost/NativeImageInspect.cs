using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinSight.Shared;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace WinSight.InferenceHost;

internal sealed record ImageInspectResult(int Protocol, string SourcePath, int Width, int Height, int Orientation,
    int FrameCount, IReadOnlyDictionary<string, string> Exif, string OcrText, IReadOnlyList<string> OcrLanguages,
    int OcrWidth, int OcrHeight, string State, string? Detail,
    string OcrEngine, string OcrEngineVersion, string OcrModelProfile,
    IReadOnlyList<OcrRegionRect> OcrRegions, bool OcrRegionsTruncated);

internal static partial class NativeMediaCommand
{
    private const int MaxImagePixels = 40_000_000;
    private const int MaxOcrEdge = 2600;
    private static readonly IReadOnlyDictionary<string, string> ImageExifKeys = new Dictionary<string, string>
    {
        ["System.Photo.CameraManufacturer"] = "cameraManufacturer",
        ["System.Photo.CameraModel"] = "cameraModel",
        ["System.Photo.DateTaken"] = "dateTaken",
        ["System.Photo.ExposureTime"] = "exposureTime",
        ["System.Photo.FNumber"] = "fNumber",
        ["System.Photo.ISOSpeed"] = "isoSpeed",
        ["System.Photo.FocalLength"] = "focalLength",
        ["System.Title"] = "imageDescription",
        ["System.ApplicationName"] = "software"
    };

    internal static async Task<ImageInspectResult> RunImageInspectRequestFileAsync(string requestPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ValidateLocalPath(requestPath, GetDriveType);
        using var input = OpenPinnedFile(path, MaxRequestBytes);
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Image request must be an object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (property.Name is not ("inputPath" or "kind") || !fields.TryAdd(property.Name, property.Value))
                throw new JsonException("Unknown or duplicate image request member: " + property.Name);
        if (fields.Count != 2 || !fields.TryGetValue("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "image"
            || !fields.TryGetValue("inputPath", out var sourceValue) || sourceValue.ValueKind != JsonValueKind.String)
            throw new JsonException("Image request requires inputPath and kind=image.");
        var sourcePath = ValidateLocalPath(sourceValue.GetString()!, GetDriveType);
        using var sourceLease = OpenPinnedFile(sourcePath, MaxInputBytes);
        var file = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(cancellationToken);
        using var stream = await file.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        uint width = decoder.PixelWidth, height = decoder.PixelHeight;
        if (width == 0 || height == 0 || width > 32768 || height > 32768 || (long)width * height > MaxImagePixels)
            throw new InvalidDataException("Image dimensions exceed the bounded 40 MP / 32768 edge contract.");
        var warnings = new List<string>();
        if (decoder.FrameCount != 1) warnings.Add("Only the first image frame is inspected; multi-frame coverage is incomplete.");
        var exif = new Dictionary<string, string>(StringComparer.Ordinal);
        var orientation = 1;
        try
        {
            var properties = await file.Properties.GetImagePropertiesAsync().AsTask(cancellationToken);
            var rawOrientation = (int)properties.Orientation;
            if (rawOrientation is >= 1 and <= 8) orientation = rawOrientation;
            else if (rawOrientation != 0) warnings.Add("EXIF orientation is outside 1..8.");
            // Deliberately do not query GPS, serial numbers, people tags or arbitrary metadata/XMP.
            var values = await properties.RetrievePropertiesAsync(ImageExifKeys.Keys).AsTask(cancellationToken);
            foreach (var key in ImageExifKeys)
                if (values.TryGetValue(key.Key, out var value) && FormatImageProperty(value) is { Length: > 0 } text)
                    exif[key.Value] = text;
        }
        catch (Exception error) when (error is COMException or IOException or ArgumentException)
        {
            warnings.Add("EXIF inspection failed: " + error.GetType().Name + ": " + error.Message);
        }
        var edge = MaxOcrEdge;
        double scale = Math.Min(1, (double)edge / Math.Max(width, height));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(width * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(height * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
        var ocr = await RunIsolatedOcrAsync(pixels, cancellationToken);
        if (ocr.Truncated) warnings.Add("OCR text exceeded the 8000-character bound.");
        cancellationToken.ThrowIfCancellationRequested();
        if (ocr.RegionsTruncated) warnings.Add("OCR text-box coverage reached its bound.");
        var result = new ImageInspectResult(3, file.Path, checked((int)width), checked((int)height), orientation,
            checked((int)decoder.FrameCount), exif, ocr.Text, ["zh-Hans-CN", "en-US"], pixels.PixelWidth, pixels.PixelHeight,
            warnings.Count == 0 ? "done" : "partial", warnings.Count == 0 ? null : string.Join(" ", warnings),
            OcrIdentity.Engine, OcrIdentity.EngineVersion, OcrIdentity.ModelProfile, ocr.Regions, ocr.RegionsTruncated);
        while (JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).Length > 64 * 1024)
        {
            if (result.OcrRegions.Count == 0) throw new InvalidDataException("Bounded image reply exceeds64 KiB.");
            result = result with { OcrRegions = result.OcrRegions.Take(result.OcrRegions.Count - 1).ToArray(), OcrRegionsTruncated = true,
                State = "partial", Detail = "OCR text-box response reached the64 KiB output bound." };
        }
        return result;
    }

    private static string? FormatImageProperty(object? value)
    {
        string? text = value switch
        {
            null => null,
            string s => s.Trim(),
            DateTimeOffset time when time.Year >= 1900 => time.ToString("O", CultureInfo.InvariantCulture),
            double number when double.IsFinite(number) => number.ToString("G", CultureInfo.InvariantCulture),
            float number when float.IsFinite(number) => number.ToString("G", CultureInfo.InvariantCulture),
            ushort or uint or ulong or short or int or long => Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => null
        };
        if (text is null) return null;
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        if (text.Length <= 256) return text;
        int end = char.IsHighSurrogate(text[255]) ? 255 : 256;
        return text[..end];
    }
}
