using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RapidOcrNet;
using SkiaSharp;

namespace WinSight.OcrHost;

internal static class Program
{
    public static int Main(string[] args)
    {
        string phase = "request";
        try
        {
            if (args.Length != 2 || args[0] != "inspect-ocr")
                throw new InvalidDataException("Expected inspect-ocr ABSOLUTE_REQUEST_JSON.");
            using var request = LockedFile.Open(args[1]);
            byte[] bytes = ReadBounded(request.Stream, 64 * 1024);
            _ = new UTF8Encoding(false, true).GetString(bytes);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            int keys = 0;
            bool protocol = false;
            string? imagePath = null;
            foreach (var item in json.RootElement.EnumerateObject())
            {
                keys++;
                switch (item.Name)
                {
                    case "protocol" when !protocol && item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out int value) && value == 2:
                        protocol = true;
                        break;
                    case "imagePath" when imagePath is null && item.Value.ValueKind == JsonValueKind.String:
                        imagePath = item.Value.GetString();
                        break;
                    default: throw new InvalidDataException("Unknown, duplicate or invalid property.");
                }
            }
            if (!protocol || imagePath is null || keys != 2) throw new InvalidDataException();
            string fullImage = LockedFile.ValidatePath(imagePath);
            if (!string.Equals(Path.GetDirectoryName(request.Path), Path.GetDirectoryName(fullImage), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Image must be beside request.");
            phase = "image";
            using var image = LockedFile.Open(fullImage);
            if (image.Stream.Length is <= 0 or > 32 * 1024 * 1024) throw new InvalidDataException();
            using var managed = new SKManagedStream(image.Stream, false);
            using var codec = SKCodec.Create(managed) ?? throw new InvalidDataException("Invalid image.");
            var info = codec.Info;
            if (codec.EncodedFormat != SKEncodedImageFormat.Png || info.Width is <= 0 or > 2600 || info.Height is <= 0 or > 2600 || (long)info.Width * info.Height > 6_760_000)
                throw new InvalidDataException("Invalid PNG dimensions.");
            using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("Incomplete PNG.");
            phase = "models";
            using var models = PinnedModels.Open();
            phase = "engine";
            // Application runtime policy, not a change to the vendored recognition algorithm.
            // This bounded CPU setting is included in the pinned model/runtime profile.
            using var options = RapidOcr.GetDefaultSessionOptions(4);
            options.AppendExecutionProvider_CPU(0);
            using var engine = new RapidOcr();
            engine.InitModels(models.ModelSet, options);
            var result = engine.Detect(bitmap, RapidOcrOptions.PPOCRv6);
            string text = result.StrRes;
            bool truncated = text.Length > 8000;
            if (truncated)
            {
                int length = char.IsHighSurrogate(text[7999]) ? 7999 : 8000;
                text = text[..length];
            }
            var regions = new List<object>();
            bool regionsTruncated = result.TextBlocks.Length > 96;
            int textOffset = 0;
            for (int i = 0; i < Math.Min(result.TextBlocks.Length, 96); i++)
            {
                var block = result.TextBlocks[i];
                if (textOffset >= text.Length) { regionsTruncated = true; break; }
                int regionLength = Math.Min(block.Text.Length, text.Length - textOffset);
                if (!text.AsSpan(textOffset, regionLength).SequenceEqual(block.Text.AsSpan(0, regionLength))) throw new InvalidDataException("OCR text/block mapping mismatch.");
                int regionStart = textOffset;
                textOffset += block.Text.Length + Environment.NewLine.Length;
                var points = result.TextBlocks[i].BoxPoints;
                if (points.Length != 4) throw new InvalidDataException("OCR block polygon must have4 points.");
                int left = Math.Clamp(points.Min(p => p.X), 0, bitmap.Width), right = Math.Clamp(points.Max(p => p.X), 0, bitmap.Width);
                int top = Math.Clamp(points.Min(p => p.Y), 0, bitmap.Height), bottom = Math.Clamp(points.Max(p => p.Y), 0, bitmap.Height);
                if (right <= left || bottom <= top) { regionsTruncated = true; continue; }
                regions.Add(new { line = i, start = regionStart, length = regionLength, left = (double)left / bitmap.Width, top = (double)top / bitmap.Height,
                    width = (double)(right - left) / bitmap.Width, height = (double)(bottom - top) / bitmap.Height });
            }
            byte[] output = JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocol = 2, engine = "RapidOcrNet", engineVersion = "4.2.0-winsight.1",
                text, truncated, regions, regionsTruncated, languages = new[] { "zh-Hans-CN", "en-US" }, modelProfile = models.Profile
            });
            if (output.Length > 128 * 1024) throw new InvalidDataException("Output limit exceeded.");
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(output);
            return 0;
        }
        catch (Exception ex)
        {
            // Process boundary: never turn input/dependency errors into successful blank OCR.
            Console.Error.WriteLine($"ocr-{phase}-error:{ex.GetType().Name}");
            return 1;
        }
    }

    internal static byte[] ReadBounded(FileStream stream, int limit)
    {
        if (stream.Length is <= 0 || stream.Length > limit) throw new InvalidDataException("Input size limit.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}

internal sealed class PinnedModels : IDisposable
{
    private readonly List<LockedFile> files = [];
    public string Profile { get; private set; } = "";
    public RapidOcrModelSet ModelSet { get; private set; } = null!;

    public static PinnedModels Open()
    {
        var owner = new PinnedModels();
        try
        {
            using var embedded = typeof(Program).Assembly.GetManifestResourceStream("WinSight.OcrHost.model-manifest.json") ?? throw new InvalidDataException();
            using var buffer = new MemoryStream();
            embedded.CopyTo(buffer);
            byte[] expected = buffer.ToArray();
            owner.Profile = Convert.ToHexStringLower(SHA256.HashData(expected));
            var manifest = LockedFile.Open(Path.Combine(AppContext.BaseDirectory, "model-manifest.json"));
            owner.files.Add(manifest);
            if (!Program.ReadBounded(manifest.Stream, 64 * 1024).AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Manifest mismatch.");
            using var doc = JsonDocument.Parse(expected);
            foreach (var item in doc.RootElement.GetProperty("files").EnumerateArray())
            {
                string relative = item.GetProperty("path").GetString()!;
                var file = LockedFile.Open(Path.Combine(AppContext.BaseDirectory, relative));
                owner.files.Add(file);
                if (file.Stream.Length != item.GetProperty("bytes").GetInt64() ||
                    !string.Equals(Convert.ToHexStringLower(SHA256.HashData(file.Stream)), item.GetProperty("sha256").GetString(), StringComparison.Ordinal))
                    throw new InvalidDataException("Model hash mismatch.");
            }
            var preset = RapidOcrModelSet.PPOCRv6Small;
            owner.ModelSet = preset with
            {
                DetModelPath = Path.Combine(AppContext.BaseDirectory, preset.DetModelPath),
                ClsModelPath = Path.Combine(AppContext.BaseDirectory, preset.ClsModelPath),
                RecModelPath = Path.Combine(AppContext.BaseDirectory, preset.RecModelPath),
                KeysPath = Path.Combine(AppContext.BaseDirectory, preset.KeysPath)
            };
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    public void Dispose() { foreach (var file in files) file.Dispose(); }
}

internal sealed class LockedFile : IDisposable
{
    private readonly List<SafeFileHandle> directories;
    public FileStream Stream { get; }
    public string Path { get; }
    private LockedFile(string path, FileStream stream, List<SafeFileHandle> directories)
    { Path = path; Stream = stream; this.directories = directories; }

    public static string ValidatePath(string path)
    {
        if (path.Length is < 4 or > 1024 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/') ||
            path.AsSpan(2).Contains(':') || path.Contains('\0')) throw new InvalidDataException("Local absolute path required.");
        string full = System.IO.Path.GetFullPath(path);
        if (new DriveInfo(full[..3]).DriveType != DriveType.Fixed) throw new InvalidDataException("Fixed local drive required.");
        return full;
    }

    public static LockedFile Open(string path)
    {
        string full = ValidatePath(path);
        var pins = new List<SafeFileHandle>();
        SafeFileHandle? handle = null;
        try
        {
            string directory = System.IO.Path.GetDirectoryName(full)!;
            var ancestors = new Stack<string>();
            for (string? current = directory; current is not null; current = System.IO.Path.GetDirectoryName(current)) ancestors.Push(current);
            foreach (string ancestor in ancestors)
            {
                // Deny directory deletion/rename while checking and using the final file.
                var pin = OpenHandle(ancestor, 0, 3, 0x02200000);
                pins.Add(pin);
                ValidateHandle(pin, ancestor, true);
            }
            // FILE_SHARE_READ denies writers, replacement and deletion through inference.
            handle = OpenHandle(full, 0x80000000, 1, 0x08200000);
            ValidateHandle(handle, full, false);
            var stream = new FileStream(handle, FileAccess.Read);
            handle = null;
            return new LockedFile(full, stream, pins);
        }
        catch
        {
            handle?.Dispose();
            foreach (var pin in pins) pin.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenHandle(string path, uint access, uint share, uint flags)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }

    private static void ValidateHandle(SafeFileHandle handle, string expected, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory) throw new InvalidDataException("Reparse/wrong file type.");
        var final = new StringBuilder(2048);
        uint count = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
        if (count == 0 || count >= final.Capacity) throw new InvalidDataException("Unresolved file identity.");
        string actual = final.ToString();
        if (!actual.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            !string.Equals(actual[4..].TrimEnd('\\'), expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("File identity changed.");
    }

    public void Dispose() { Stream.Dispose(); foreach (var pin in directories) pin.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
