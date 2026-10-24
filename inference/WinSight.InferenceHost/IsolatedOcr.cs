using System.Text.Json;
using Windows.Graphics.Imaging;
using WinSight.Infrastructure.Media;
using WinSight.Shared;

namespace WinSight.InferenceHost;

internal sealed record OcrRegionRect(int Line, double Left, double Top, double Width, double Height, int Start, int Length);
internal sealed record OcrWorkerResult(string Text, bool Truncated, IReadOnlyList<OcrRegionRect> Regions, bool RegionsTruncated);

internal static partial class NativeMediaCommand
{
    private static async Task<OcrWorkerResult> RunIsolatedOcrAsync(SoftwareBitmap pixels, CancellationToken cancellationToken)
    {
        // This inner budget includes PNG preparation. The outer request's shared budget is not reset.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Dense bounded images previously had a 30s caller budget; leave time for outer drain.
        // The caller's earlier cancellation/deadline always wins (normal image tests use 10s).
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("winsight-ocr-").FullName;
        WindowsNativeMediaProcess? worker = null;
        FileStream? executablePin = null;
        bool retained = false;
        try
        {
            var imagePath = Path.Combine(directory, "image.png");
            await using (var output = new FileStream(imagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using var random = output.AsRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, random).AsTask(token);
                encoder.SetSoftwareBitmap(pixels);
                await encoder.FlushAsync().AsTask(token);
                await output.FlushAsync(token);
            }
            var requestPath = Path.Combine(directory, "request.json");
            await File.WriteAllBytesAsync(requestPath,
                JsonSerializer.SerializeToUtf8Bytes(new { protocol = 2, imagePath }), token);
            var executable = ValidateLocalPath(Path.Combine(AppContext.BaseDirectory, "ocr", "WinSight.OcrHost.exe"), GetDriveType);
            executablePin = OpenPinnedFile(executable, 8 * 1024 * 1024);
            worker = WindowsNativeMediaProcess.Start(executable, requestPath, directory, TimeSpan.FromSeconds(2),
                false, NativeMediaOperation.InspectOcr);
            var reply = await worker.RunAsync(128 * 1024, TimeSpan.FromSeconds(2), token);
            if (reply.ExitCode != 0)
                throw new InvalidDataException($"OCR worker failed ({reply.ExitCode}): {reply.Error}");
            return ParseOcrWorkerReply(reply.Output);
        }
        catch (Exception error)
        {
            if (error is MediaTerminationException || worker is { Settled: false })
            {
                // Start can throw before assigning worker, while retaining its actual job/process.
                // Unknown physical settlement still requires the executable pin and task material.
                MediaRetention.Keep(worker, executablePin!, directory);
                retained = true;
            }
            throw;
        }
        finally
        {
            if (!retained)
            {
                worker?.Dispose();
                executablePin?.Dispose();
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    internal static OcrWorkerResult ParseOcrWorkerReply(string output)
    {
        using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OCR reply must be an object.");
        var required = new HashSet<string>(["protocol", "engine", "engineVersion", "text", "truncated", "languages", "modelProfile", "regions", "regionsTruncated"], StringComparer.Ordinal);
        foreach (var entry in root.EnumerateObject())
            if (!required.Remove(entry.Name)) throw new InvalidDataException("Unknown/duplicate OCR reply member.");
        if (required.Count != 0 || root.GetProperty("protocol").GetInt32() != 2
            || root.GetProperty("engine").GetString() != OcrIdentity.Engine
            || root.GetProperty("engineVersion").GetString() != OcrIdentity.EngineVersion
            || root.GetProperty("modelProfile").GetString() != OcrIdentity.ModelProfile)
            throw new InvalidDataException("OCR engine/model identity mismatch.");
        var languages = root.GetProperty("languages");
        if (languages.ValueKind != JsonValueKind.Array || languages.GetArrayLength() != 2
            || languages[0].GetString() != "zh-Hans-CN" || languages[1].GetString() != "en-US")
            throw new InvalidDataException("OCR language profile mismatch.");
        var text = root.GetProperty("text").GetString() ?? throw new InvalidDataException("Null OCR text.");
        if (text.Length > 8000 || text.Contains('\0')) throw new InvalidDataException("OCR text exceeds its bound.");
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i == text.Length || !char.IsLowSurrogate(text[i])) throw new InvalidDataException("Invalid OCR surrogate pair.");
            }
            else if (char.IsLowSurrogate(text[i])) throw new InvalidDataException("Invalid OCR surrogate pair.");
        }
        var regions = new List<OcrRegionRect>();
        var geometry = root.GetProperty("regions");
        if (geometry.ValueKind != JsonValueKind.Array || geometry.GetArrayLength() > 96) throw new InvalidDataException("OCR region bound.");
        int lastLine = -1, lastEnd = 0;
        foreach (var region in geometry.EnumerateArray())
        {
            var keys = new HashSet<string>(["line", "left", "top", "width", "height", "start", "length"], StringComparer.Ordinal);
            if (region.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid OCR region.");
            foreach (var property in region.EnumerateObject()) if (!keys.Remove(property.Name)) throw new InvalidDataException("Unknown/duplicate OCR region field.");
            if (keys.Count != 0) throw new InvalidDataException("Incomplete OCR region.");
            int line = region.GetProperty("line").GetInt32(), start = region.GetProperty("start").GetInt32(), length = region.GetProperty("length").GetInt32();
            if (start < lastEnd || length <= 0 || start > text.Length - length || char.IsLowSurrogate(text[start]) || char.IsHighSurrogate(text[start + length - 1]))
                throw new InvalidDataException("OCR text region range invalid.");
            double left = region.GetProperty("left").GetDouble(), top = region.GetProperty("top").GetDouble(), width = region.GetProperty("width").GetDouble(), height = region.GetProperty("height").GetDouble();
            if (line <= lastLine || line > 5000 || !double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height)
                || left < 0 || top < 0 || width <= 0 || height <= 0 || left + width > 1 + 1e-9 || top + height > 1 + 1e-9)
                throw new InvalidDataException("OCR region coordinates/order invalid.");
            regions.Add(new(line, left, top, width, height, start, length)); lastLine = line; lastEnd = start + length;
        }
        return new(text, root.GetProperty("truncated").GetBoolean(), regions, root.GetProperty("regionsTruncated").GetBoolean());
    }
}
