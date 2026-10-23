using System.Text.Json;

namespace WinSight.Infrastructure.Media;

internal static class NativeMediaProtocol
{
    internal sealed record Result(string Filename, string Modality, double? Duration);

    internal static Result Parse(string text, NativeMediaProcessRequest request)
    {
        text = text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\n') || text.Contains('\r'))
            throw new InvalidDataException("Expected exactly one N1 response line.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            Object(root, "protocol", "sourcePath", "kind", "sourceDurationSeconds", "artifacts");
            if (!root.GetProperty("protocol").TryGetInt32(out var protocol) || protocol != 1 ||
                Text(root, "kind") != request.Kind ||
                !string.Equals(Text(root, "sourcePath"), request.SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("N1 version, kind or source mismatch.");
            var duration = NumberOrNull(root.GetProperty("sourceDurationSeconds"));
            if (duration is not null && (duration <= 0 || request.StartSeconds >= duration ||
                request.EndSeconds is not null && request.EndSeconds > duration))
                throw new InvalidDataException("N1 duration does not contain the requested interval.");
            var artifacts = root.GetProperty("artifacts");
            if (artifacts.ValueKind != JsonValueKind.Array || artifacts.GetArrayLength() != 1)
                throw new InvalidDataException("N1 must return exactly one artifact.");
            var item = artifacts[0];
            Object(item, "relativePath", "modality", "startSeconds", "endSeconds", "sampleRate", "channels", "bitsPerSample", "width", "height");
            if (NumberOrNull(item.GetProperty("startSeconds")) != request.StartSeconds ||
                NumberOrNull(item.GetProperty("endSeconds")) != request.EndSeconds)
                throw new InvalidDataException("N1 artifact interval mismatch.");
            var filename = Text(item, "relativePath");
            if (filename.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(filename[0]) ||
                filename.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || filename.EndsWith('.') ||
                filename.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException("N1 artifact is not a relative single filename.");
            var stem = Path.GetFileNameWithoutExtension(filename).ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]))
                throw new InvalidDataException("N1 artifact uses a reserved device name.");
            var modality = Text(item, "modality");
            if (request.Kind == "audio")
            {
                if (modality != "audio" || !filename.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                    Integer(item, "sampleRate") != 16000 || Integer(item, "channels") != 1 || Integer(item, "bitsPerSample") != 16 ||
                    item.GetProperty("width").ValueKind != JsonValueKind.Null || item.GetProperty("height").ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("N1 audio metadata mismatch.");
            }
            else
            {
                var width = Integer(item, "width");
                var height = Integer(item, "height");
                if (modality != "image" || !filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    width < 1 || height < 1 || width > request.MaxEdge || height > request.MaxEdge ||
                    item.GetProperty("sampleRate").ValueKind != JsonValueKind.Null ||
                    item.GetProperty("channels").ValueKind != JsonValueKind.Null || item.GetProperty("bitsPerSample").ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("N1 frame metadata mismatch.");
            }
            return new Result(filename, modality, duration);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new InvalidDataException("Invalid N1 response schema.", ex);
        }
    }

    internal static NativeMediaInfo ParseInspect(string text, string sourcePath, string kind)
    {
        text = text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\n') || text.Contains('\r'))
            throw new InvalidDataException("Expected exactly one media metadata response line.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            Object(root, "protocol", "sourcePath", "kind", "durationSeconds", "hasAudio", "audioTrackCount", "width", "height");
            if (!root.GetProperty("protocol").TryGetInt32(out var protocol) || protocol != 1 ||
                Text(root, "kind") != kind ||
                !string.Equals(Text(root, "sourcePath"), sourcePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Media metadata version, kind or source mismatch.");
            var duration = NumberOrNull(root.GetProperty("durationSeconds"));
            if (duration is null or <= 0) throw new InvalidDataException("Media metadata duration must be finite and positive.");
            var hasAudio = root.GetProperty("hasAudio").GetBoolean();
            int? count = root.GetProperty("audioTrackCount").ValueKind == JsonValueKind.Null ? null : Integer(root, "audioTrackCount");
            var width = Integer(root, "width");
            var height = Integer(root, "height");
            if (kind == "audio" ? !hasAudio || count is not null || width != 0 || height != 0 :
                kind != "video" || count is null or < 0 || hasAudio != (count > 0) || width <= 0 || height <= 0)
                throw new InvalidDataException("Media metadata audio tracks or dimensions are inconsistent.");
            return new NativeMediaInfo(sourcePath, kind, duration.Value, hasAudio, count, width, height);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new InvalidDataException("Invalid media metadata response schema.", ex);
        }
    }

    internal static void ValidateArtifact(FileStream stream, NativeMediaProcessRequest request, string response,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        if (request.Kind == "audio") ValidateWave(stream, request.EndSeconds!.Value - request.StartSeconds);
        else
        {
            using var document = JsonDocument.Parse(response);
            var artifact = document.RootElement.GetProperty("artifacts")[0];
            NativeMediaPng.Validate(stream, Integer(artifact, "width"), Integer(artifact, "height"), request.MaxEdge, cancellationToken);
        }
        stream.Position = 0;
    }

    private static void ValidateWave(FileStream stream, double seconds)
    {
        if (stream.Length is < 44 or > 2 * 1024 * 1024) throw new InvalidDataException("N1 WAV artifact size is invalid.");
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x46464952 || reader.ReadUInt32() != stream.Length - 8 || reader.ReadUInt32() != 0x45564157)
            throw new InvalidDataException("N1 artifact is not a bounded RIFF WAVE.");
        var formatSeen = false;
        var dataSeen = false;
        long dataBytes = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            var start = stream.Position;
            if (length > stream.Length - start) throw new InvalidDataException("Truncated N1 WAV chunk.");
            if (id == 0x20746d66)
            {
                if (formatSeen || length < 16) throw new InvalidDataException("Invalid N1 WAV format chunk.");
                formatSeen = true;
                var format = reader.ReadUInt16();
                var channels = reader.ReadUInt16();
                var rate = reader.ReadUInt32();
                var byteRate = reader.ReadUInt32();
                var align = reader.ReadUInt16();
                var bits = reader.ReadUInt16();
                var pcm = format == 1;
                if (format == 0xfffe && length >= 40)
                {
                    var extension = reader.ReadUInt16();
                    var validBits = reader.ReadUInt16();
                    reader.ReadUInt32();
                    pcm = extension >= 22 && validBits == 16 &&
                        new Guid(reader.ReadBytes(16)) == new Guid("00000001-0000-0010-8000-00aa00389b71");
                }
                if (!pcm || channels != 1 || rate != 16000 || byteRate != 32000 || align != 2 || bits != 16)
                    throw new InvalidDataException("N1 WAV must be PCM16 mono 16kHz.");
            }
            if (id == 0x61746164)
            {
                if (dataSeen) throw new InvalidDataException("Duplicate N1 WAV data chunk.");
                dataSeen = true; dataBytes = length;
            }
            stream.Position = checked(start + length + (length & 1));
        }
        if (!formatSeen || !dataSeen || dataBytes == 0 || (dataBytes & 1) != 0 || stream.Position != stream.Length ||
            Math.Abs(dataBytes / 2d - seconds * 16000) > 1)
            throw new InvalidDataException("N1 WAV does not cover the requested interval within one sample.");
    }

    private static void Object(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("N1 object required.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate N1 field.");
        if (found.Count != names.Length) throw new InvalidDataException("Missing N1 response field.");
    }
    private static string Text(JsonElement item, string name) => item.GetProperty(name).GetString() ?? throw new InvalidDataException("N1 string required.");
    private static int Integer(JsonElement item, string name) => item.GetProperty(name).TryGetInt32(out var number) ? number : throw new InvalidDataException("N1 integer required.");
    private static double? NumberOrNull(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new InvalidDataException("N1 finite number required.");
        return number;
    }
}
