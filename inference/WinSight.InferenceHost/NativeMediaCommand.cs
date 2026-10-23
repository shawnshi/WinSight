using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.FileProperties;
using FileAttributes = System.IO.FileAttributes;

namespace WinSight.InferenceHost;

internal static partial class NativeMediaCommand
{
    private const long MaxInputBytes = 32L * 1024 * 1024;
    private const int MaxRequestBytes = 64 * 1024;

    internal static Task<MediaDecodeResult> RunRequestFileAsync(string requestPath,
        CancellationToken cancellationToken = default) =>
        RunRequestFileWithDriveClassifierAsync(requestPath, GetDriveType, cancellationToken);

    internal static Task<MediaDecodeResult> RunRequestFileWithDriveClassifierAsync(string requestPath,
        Func<string, System.IO.DriveType> driveClassifier, CancellationToken cancellationToken = default) =>
        RunDecodeRequestFileAsync(requestPath, driveClassifier, ExecuteWithDriveClassifierAsync, cancellationToken);

    private static async Task<MediaDecodeResult> RunDecodeRequestFileAsync(string requestPath,
        Func<string, System.IO.DriveType> driveClassifier,
        Func<MediaDecodeRequest, Func<string, System.IO.DriveType>, CancellationToken, Task<MediaDecodeResult>> execute,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(driveClassifier);
        var path = ValidateLocalPath(requestPath, driveClassifier);
        using var input = OpenPinnedFile(path, MaxRequestBytes);
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The media request must be an object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (property.Name is not ("inputPath" or "outputDirectory" or "kind" or "startSeconds" or "endSeconds" or "maxEdge") ||
                !fields.TryAdd(property.Name, property.Value))
                throw new JsonException($"Unknown or duplicate media request member: {property.Name}");
        }
        foreach (var name in new[] { "inputPath", "outputDirectory", "kind", "startSeconds" })
            if (!fields.ContainsKey(name)) throw new JsonException($"Missing media request member: {name}");
        string RequiredString(string name) => fields[name].ValueKind == JsonValueKind.String
            ? fields[name].GetString()! : throw new JsonException($"{name} must be a string.");
        double Number(string name) => fields[name].ValueKind == JsonValueKind.Number &&
            fields[name].TryGetDouble(out var value) && double.IsFinite(value)
            ? value : throw new JsonException($"{name} must be a finite number.");
        var end = fields.ContainsKey("endSeconds") ? (double?)Number("endSeconds") : null;
        var maxEdge = 512;
        if (fields.TryGetValue("maxEdge", out var edge) &&
            (edge.ValueKind != JsonValueKind.Number || !edge.TryGetInt32(out maxEdge)))
            throw new JsonException("maxEdge must be an integer.");
        return await execute(new MediaDecodeRequest(RequiredString("inputPath"), RequiredString("outputDirectory"),
            RequiredString("kind"), Number("startSeconds"), end, maxEdge), driveClassifier, cancellationToken);
    }

    internal static Task<MediaDecodeResult> ExecuteAsync(MediaDecodeRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteWithDriveClassifierAsync(request, GetDriveType, cancellationToken);

    internal static async Task<MediaDecodeResult> ExecuteWithDriveClassifierAsync(MediaDecodeRequest request,
        Func<string, System.IO.DriveType> driveClassifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(driveClassifier);
        ValidateRequest(request);
        // Classify both roots before either media path reaches filesystem metadata or native I/O (NM-N1R-01).
        var sourcePath = ValidateLocalPath(request.InputPath, driveClassifier);
        var outputPath = ValidateLocalPath(request.OutputDirectory, driveClassifier);
        ValidateAncestors(outputPath, includeLeaf: false);
        if (Path.Exists(outputPath)) throw new IOException("Output directory must be new.");
        using var sourceLease = OpenPinnedFile(sourcePath, MaxInputBytes);
        var source = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Kind == "audio")
            return await DecodeAudioAsync(request, source, outputPath, cancellationToken);
        return await DecodeFrameAsync(request, source, outputPath, cancellationToken);
    }

    private static void ValidateRequest(MediaDecodeRequest request)
    {
        if (request.Kind is not ("audio" or "frame")) throw new ArgumentException("kind must be audio or frame.");
        if (!double.IsFinite(request.StartSeconds) || request.StartSeconds < 0 ||
            request.StartSeconds >= TimeSpan.MaxValue.TotalSeconds)
            throw new ArgumentException("startSeconds must be a finite nonnegative source time.");
        if (request.MaxEdge is < 64 or > 1024) throw new ArgumentException("maxEdge must be between 64 and 1024.");
        if (request.Kind == "frame" && request.EndSeconds is not null)
            throw new ArgumentException("endSeconds is forbidden for a frame request.");
        if (request.Kind == "audio")
        {
            if (request.EndSeconds is not { } end || !double.IsFinite(end) || end <= request.StartSeconds ||
                end >= TimeSpan.MaxValue.TotalSeconds || end - request.StartSeconds > 30)
                throw new ArgumentException("Audio requires 0 <= startSeconds < endSeconds and an interval <= 30 seconds.");
            if (TimeSpan.FromSeconds(end) <= TimeSpan.FromSeconds(request.StartSeconds))
                throw new ArgumentException("The requested interval is below clock precision.");
        }
    }

    private static async Task<MediaDecodeResult> DecodeAudioAsync(MediaDecodeRequest request, StorageFile source,
        string outputPath, CancellationToken cancellationToken)
    {
        var track = await BackgroundAudioTrack.CreateFromFileAsync(source).AsTask(cancellationToken);
        var originalDuration = track.OriginalDuration;
        var startTime = TimeSpan.FromSeconds(request.StartSeconds);
        var end = request.EndSeconds!.Value;
        var endTime = TimeSpan.FromSeconds(end);
        if (originalDuration <= TimeSpan.Zero)
            throw new InvalidDataException("Native audio source has no usable timeline duration.");
        if (endTime > originalDuration)
            throw new ArgumentException("Requested audio interval exceeds the source duration.");
        track.TrimTimeFromStart = startTime;
        track.TrimTimeFromEnd = originalDuration - endTime;
        var composition = new MediaComposition();
        composition.BackgroundAudioTracks.Add(track);
        // NM-NATIVE-AUDIO-FULL-INTERVAL: precise timeline rendering drains native resampling at cropped/real EOF.
        // Background tracks alone have zero composition duration. This video-only clock contributes no audio to WAV.
        composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 0, 0, 0), endTime - startTime));
        try
        {
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            profile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);
            var folder = await CreateOutputAsync(outputPath, cancellationToken);
            var output = await folder.CreateFileAsync("audio.wav", CreationCollisionOption.FailIfExists).AsTask(cancellationToken);
            var failure = await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile).AsTask(cancellationToken);
            if (failure != TranscodeFailureReason.None)
                throw new InvalidOperationException($"Native audio render failed: {failure}");
            cancellationToken.ThrowIfCancellationRequested();
            ValidateWave(output.Path, end - request.StartSeconds);
            return new MediaDecodeResult(1, source.Path, request.Kind, originalDuration.TotalSeconds,
                [new MediaDecodeArtifact("audio.wav", "audio", request.StartSeconds, end, 16000, 1, 16)]);
        }
        finally
        {
            composition.BackgroundAudioTracks.Clear();
            composition.Clips.Clear();
        }
    }

    private static async Task<MediaDecodeResult> DecodeFrameAsync(MediaDecodeRequest request, StorageFile source,
        string outputPath, CancellationToken cancellationToken)
    {
        var clip = await MediaClip.CreateFromFileAsync(source).AsTask(cancellationToken);
        var duration = clip.OriginalDuration.TotalSeconds;
        if (duration <= 0 || request.StartSeconds >= duration)
            throw new ArgumentException("Requested frame must be inside the source video duration.");
        var video = clip.GetVideoEncodingProperties();
        if (video.Width == 0 || video.Height == 0)
            throw new InvalidDataException("Source has no video dimensions.");
        var properties = await source.Properties.GetVideoPropertiesAsync().AsTask(cancellationToken);
        var pixelAspect = video.PixelAspectRatio;
        var displayWidth = (double)video.Width * (pixelAspect.Denominator == 0 ? 1 : (double)pixelAspect.Numerator / pixelAspect.Denominator);
        var portrait = displayWidth < video.Height;
        if (properties.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270) portrait = !portrait;
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        try
        {
            using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.FromSeconds(request.StartSeconds),
                portrait ? 0 : request.MaxEdge, portrait ? request.MaxEdge : 0, VideoFramePrecision.NearestFrame).AsTask(cancellationToken);
            var decoder = await BitmapDecoder.CreateAsync(thumbnail).AsTask(cancellationToken);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0) throw new InvalidDataException("Native thumbnail is empty.");
            var scale = Math.Min(1d, (double)request.MaxEdge / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var width = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
            var height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = width, ScaledHeight = height }, ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
            var folder = await CreateOutputAsync(outputPath, cancellationToken);
            var output = await folder.CreateFileAsync("frame.png", CreationCollisionOption.FailIfExists).AsTask(cancellationToken);
            using (var stream = await output.OpenAsync(FileAccessMode.ReadWrite).AsTask(cancellationToken))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync().AsTask(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (bitmap.PixelWidth > request.MaxEdge || bitmap.PixelHeight > request.MaxEdge)
                throw new InvalidDataException("Native frame exceeds the requested size limit.");
            return new MediaDecodeResult(1, source.Path, request.Kind, duration,
                [new MediaDecodeArtifact("frame.png", "image", request.StartSeconds, null,
                    Width: bitmap.PixelWidth, Height: bitmap.PixelHeight)]);
        }
        finally
        {
            composition.Clips.Clear();
        }
    }

    private static void ValidateWave(string path, double requestedSeconds)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 12 || reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("Native output is not RIFF WAV.");
        reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Native output is not WAVE.");
        var validFormat = false;
        long dataBytes = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            var start = stream.Position;
            if (length > stream.Length - start) throw new InvalidDataException("Native WAV chunk is truncated.");
            if (id == 0x20746d66 && length >= 16)
            {
                var format = reader.ReadUInt16();
                var channels = reader.ReadUInt16();
                var sampleRate = reader.ReadUInt32();
                var byteRate = reader.ReadUInt32();
                var blockAlign = reader.ReadUInt16();
                var bits = reader.ReadUInt16();
                var pcm = format == 1;
                if (format == 0xfffe && length >= 40)
                {
                    var extensionSize = reader.ReadUInt16();
                    var validBits = reader.ReadUInt16();
                    reader.ReadUInt32();
                    pcm = extensionSize >= 22 && validBits == 16 &&
                        new Guid(reader.ReadBytes(16)) == new Guid("00000001-0000-0010-8000-00aa00389b71");
                }
                validFormat = pcm && channels == 1 && sampleRate == 16000 && byteRate == 32000 && blockAlign == 2 && bits == 16;
            }
            if (id == 0x61746164) dataBytes = checked(dataBytes + length);
            stream.Position = checked(start + length + (length & 1));
        }
        // A decoder may silently return a short clip at EOF even when source metadata is unavailable.
        if (!validFormat || dataBytes == 0 || (dataBytes & 1) != 0 ||
            Math.Abs(dataBytes / 2d - requestedSeconds * 16000) > 1)
            throw new InvalidDataException("Native WAV is not PCM16 mono 16kHz covering the full requested interval (within one sample).");
    }

    private static string ValidateLocalPath(string path, Func<string, System.IO.DriveType> driveClassifier)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path[2] is not ('\\' or '/'))
            throw new ArgumentException("Only absolute local drive paths are supported; device and UNC paths are forbidden.");
        var components = path[3..].Replace('/', '\\').Split('\\');
        foreach (var component in components)
        {
            var stem = component.Split('.')[0];
            if (component.Length == 0 || component.EndsWith(' ') || component.EndsWith('.') ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("Unsafe path component.");
        }
        var fullPath = Path.GetFullPath(path);
        var driveType = driveClassifier(fullPath[..3]);
        if (driveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram))
            throw new IOException($"Media paths require a local drive (Fixed, Removable, CDRom or Ram); classified drive type: {driveType}.");
        return fullPath;
    }

    private static void ValidateAncestors(string path, bool includeLeaf)
    {
        var current = includeLeaf ? path : Path.GetDirectoryName(path);
        while (current is not null)
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("Reparse points and devices are forbidden.");
            if (current != path && (attributes & FileAttributes.Directory) == 0)
                throw new IOException("A media path parent is not a directory.");
            current = Path.GetDirectoryName(current);
        }
    }

    private static FileStream OpenPinnedFile(string path, long maxBytes)
    {
        ValidateAncestors(path, includeLeaf: true);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("Input must be a regular file.");
        // OPEN_REPARSE_POINT checks the opened leaf itself rather than trusting only a preceding name lookup.
        var handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Cannot pin media input for read-only decoding.");
        }
        FileStream stream;
        try
        {
            if (GetFileType(handle) != 1 || (File.GetAttributes(handle) &
                (FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Directory)) != 0)
                throw new IOException("Input handle must be a regular non-reparse disk file.");
            stream = new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        try
        {
            ValidateAncestors(path, includeLeaf: true);
            if (stream.Length <= 0 || stream.Length > maxBytes) throw new IOException($"Input file must contain 1..{maxBytes} bytes.");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static async Task<StorageFolder> CreateOutputAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAncestors(path, includeLeaf: false);
        // CreateDirectoryW is exclusive; Directory.CreateDirectory would accept a competing writer's existing directory.
        if (!CreateDirectory(path, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create new media output directory.");
        ValidateAncestors(path, includeLeaf: true);
        // No recursive cleanup: cancellation of a WinRT await does not prove that its native writer has stopped.
        return await StorageFolder.GetFolderFromPathAsync(path).AsTask(cancellationToken);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern DriveType GetDriveType(string rootPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
}
