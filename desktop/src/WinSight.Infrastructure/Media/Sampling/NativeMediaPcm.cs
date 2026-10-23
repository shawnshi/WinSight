using System.Buffers.Binary;

namespace WinSight.Infrastructure.Media;

internal static class NativeMediaPcm
{
    internal static double Rms(Stream stream, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!stream.CanSeek || stream.Length is < 44 or > 2 * 1024 * 1024)
            throw new InvalidDataException("Expected bounded PCM RIFF WAVE.");
        stream.Position = 0;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x46464952 || reader.ReadUInt32() != stream.Length - 8 || reader.ReadUInt32() != 0x45564157)
            throw new InvalidDataException("Invalid RIFF WAVE header.");
        var formatSeen = false;
        long dataStart = -1;
        uint dataLength = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            token.ThrowIfCancellationRequested();
            var id = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            var start = stream.Position;
            var end = checked(start + length + (length & 1));
            if (end > stream.Length) throw new InvalidDataException("Truncated WAV chunk.");
            if (id == 0x20746d66)
            {
                if (formatSeen || length < 16) throw new InvalidDataException("Invalid WAV format chunk.");
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
                    pcm = extension >= 22 && extension <= length - 18 && validBits == 16 &&
                        new Guid(reader.ReadBytes(16)) == new Guid("00000001-0000-0010-8000-00aa00389b71");
                }
                if (!pcm || channels != 1 || rate != 16000 || byteRate != 32000 || align != 2 || bits != 16)
                    throw new InvalidDataException("WAV must be PCM16 mono 16kHz.");
            }
            else if (id == 0x61746164)
            {
                if (dataStart >= 0 || length == 0 || (length & 1) != 0)
                    throw new InvalidDataException("Invalid WAV data chunk.");
                dataStart = start;
                dataLength = length;
            }
            stream.Position = end;
        }
        if (!formatSeen || dataStart < 0 || stream.Position != stream.Length)
            throw new InvalidDataException("Missing WAV format/data or trailing partial chunk.");
        stream.Position = dataStart;
        Span<byte> bytes = stackalloc byte[8192];
        double squares = 0;
        for (long remaining = dataLength; remaining > 0;)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(remaining, bytes.Length);
            stream.ReadExactly(bytes[..count]);
            for (var i = 0; i < count; i += 2)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i, 2)) / 32768d;
                squares += sample * sample;
            }
            remaining -= count;
        }
        return Math.Sqrt(squares / (dataLength / 2));
    }
}
