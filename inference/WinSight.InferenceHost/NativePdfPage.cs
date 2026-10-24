using System.Text.Json;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinSight.InferenceHost;

internal sealed record NativePdfPageResult(int Protocol, string InputPath, int PageNumber, uint PageCount,
    uint PixelWidth, uint PixelHeight, string RenderPolicy, string Artifact);

internal static partial class NativeMediaCommand
{
    private const string PdfRenderPolicy = "winrt-pdf-dip-normalized-v1";
    internal static async Task<NativePdfPageResult> RenderPdfPageRequestAsync(string requestPath, CancellationToken token = default)
    {
        using var requestLease = OpenPinnedFile(ValidateLocalPath(requestPath, GetDriveType), MaxRequestBytes);
        var bytes = new byte[checked((int)requestLease.Length)];
        await requestLease.ReadExactlyAsync(bytes, token);
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("PDF page request must be an object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in json.RootElement.EnumerateObject())
            if (property.Name is not ("protocol" or "inputPath" or "outputDirectory" or "pageNumber" or "maxEdge") || !fields.TryAdd(property.Name, property.Value))
                throw new JsonException("Unknown/duplicate PDF page request member.");
        if (fields.Count != 5 || !fields["protocol"].TryGetInt32(out var protocol) || protocol != 1
            || !fields["pageNumber"].TryGetInt32(out var number) || number is < 1 or > 5000
            || !fields["maxEdge"].TryGetInt32(out var edge) || edge is < 256 or > 2600)
            throw new JsonException("Invalid PDF page protocol/number/edge.");
        if (fields["inputPath"].ValueKind != JsonValueKind.String || fields["outputDirectory"].ValueKind != JsonValueKind.String)
            throw new JsonException("PDF page paths must be strings.");
        var sourcePath = ValidateLocalPath(fields["inputPath"].GetString()!, GetDriveType);
        var outputPath = ValidateLocalPath(fields["outputDirectory"].GetString()!, GetDriveType);
        ValidateAncestors(outputPath, includeLeaf: false);
        if (Path.Exists(outputPath)) throw new IOException("PDF output directory must be new.");
        using var sourcePin = OpenPinnedFile(sourcePath, MaxInputBytes);
        var source = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(token);
        var document = await PdfDocument.LoadFromFileAsync(source).AsTask(token);
        if (number > document.PageCount) throw new ArgumentOutOfRangeException(nameof(number), "PDF page is outside the source.");
        using var page = document.GetPage((uint)(number - 1));
        var size = page.Size;
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
            throw new InvalidDataException("PDF page has invalid display dimensions.");
        var scale = edge / Math.Max(size.Width, size.Height);
        var width = Math.Max(1u, (uint)Math.Round(size.Width * scale));
        var height = Math.Max(1u, (uint)Math.Round(size.Height * scale));
        using var measure = new InMemoryRandomAccessStream();
        uint measureWidth = Math.Max(1u, (uint)Math.Round(size.Width * 256 / Math.Max(size.Width, size.Height)));
        uint measureHeight = Math.Max(1u, (uint)Math.Round(size.Height * 256 / Math.Max(size.Width, size.Height)));
        await page.RenderToStreamAsync(measure, new PdfPageRenderOptions
        { DestinationWidth = measureWidth, DestinationHeight = measureHeight, IsIgnoringHighContrast = true }).AsTask(token);
        var measured = await BitmapDecoder.CreateAsync(measure).AsTask(token);
        ValidatePdfRaster(measure, measured);
        var ratio = Math.Max((double)measured.PixelWidth / measureWidth, (double)measured.PixelHeight / measureHeight);
        if (!double.IsFinite(ratio) || ratio <= 0) throw new InvalidDataException("PDF raster scale is invalid.");
        using var rendered = new InMemoryRandomAccessStream();
        // Render destinations are DIPs. A small first render measures the host scale; canonical pixels are encoded below.
        await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions
        {
            DestinationWidth = Math.Max(1u, (uint)Math.Round(width / ratio)),
            DestinationHeight = Math.Max(1u, (uint)Math.Round(height / ratio)), IsIgnoringHighContrast = true
        }).AsTask(token);
        var decoder = await BitmapDecoder.CreateAsync(rendered).AsTask(token);
        ValidatePdfRaster(rendered, decoder);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height }, ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(token);
        var folder = await CreateOutputAsync(outputPath, token);
        var output = await folder.CreateFileAsync("page.png", CreationCollisionOption.FailIfExists).AsTask(token);
        using (var stream = await output.OpenAsync(FileAccessMode.ReadWrite).AsTask(token))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(token);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(token);
        }
        token.ThrowIfCancellationRequested();
        return new(1, sourcePath, number, document.PageCount, width, height, PdfRenderPolicy, "page.png");
    }

    private static void ValidatePdfRaster(IRandomAccessStream stream, BitmapDecoder decoder)
    {
        if (stream.Size > 64UL * 1024 * 1024 || decoder.PixelWidth == 0 || decoder.PixelHeight == 0
            || decoder.PixelWidth > 8192 || decoder.PixelHeight > 8192 || (ulong)decoder.PixelWidth * decoder.PixelHeight > 16_000_000)
            throw new InvalidDataException("Native PDF raster exceeds compressed/pixel bounds.");
    }
}
