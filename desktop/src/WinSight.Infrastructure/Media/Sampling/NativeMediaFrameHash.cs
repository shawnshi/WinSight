using System.Buffers.Binary;
using System.IO.Compression;

namespace WinSight.Infrastructure.Media;

internal static class NativeMediaFrameHash
{
    internal static ulong Read(FileStream stream, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[29];
        stream.Position = 0;
        stream.ReadExactly(header);
        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[16..20]));
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[20..24]));
        stream.Position = 0;
        // Reuse N2's strict CRC/zlib/extent validation, without altering the protected decoder.
        NativeMediaPng.Validate(stream, width, height, NativeMediaSamplingPolicy.MaxEdge, token);
        stream.Position = 0;
        var png = new byte[checked((int)stream.Length)];
        stream.ReadExactly(png);
        var channels = png[25] == 2 ? 3 : 4;
        var interlace = png[28];
        using var compressed = new MemoryStream();
        for (var offset = 8; offset < png.Length;)
        {
            token.ThrowIfCancellationRequested();
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            if (png.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8)) compressed.Write(png.AsSpan(offset + 8, length));
            offset += length + 12;
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var pixels = new byte[checked(width * height * channels)];
        ReadOnlySpan<int> xs = [0, 4, 0, 2, 0, 1, 0], ys = [0, 0, 4, 0, 2, 0, 1];
        ReadOnlySpan<int> dxs = [8, 8, 4, 4, 2, 2, 1], dys = [8, 8, 8, 4, 4, 2, 2];
        for (var pass = 0; pass < (interlace == 0 ? 1 : 7); pass++)
        {
            var x0 = interlace == 0 ? 0 : xs[pass];
            var y0 = interlace == 0 ? 0 : ys[pass];
            var dx = interlace == 0 ? 1 : dxs[pass];
            var dy = interlace == 0 ? 1 : dys[pass];
            var w = (width - x0 + dx - 1) / dx;
            var h = (height - y0 + dy - 1) / dy;
            if (w <= 0 || h <= 0) continue;
            var previous = new byte[w * channels];
            var row = new byte[previous.Length];
            for (var y = 0; y < h; y++)
            {
                token.ThrowIfCancellationRequested();
                var filter = zlib.ReadByte();
                zlib.ReadExactly(row);
                Unfilter(row, previous, channels, filter);
                for (var x = 0; x < w; x++)
                    row.AsSpan(x * channels, channels).CopyTo(pixels.AsSpan(((y0 + y * dy) * width + x0 + x * dx) * channels, channels));
                (previous, row) = (row, previous);
            }
        }
        if (zlib.ReadByte() != -1) throw new InvalidDataException("PNG contains excess decoded data.");
        return NativeMediaSamplingPolicy.AverageHash(pixels, width, height, channels);
    }

    internal static void Unfilter(Span<byte> row, ReadOnlySpan<byte> previous, int channels, int filter)
    {
        if (filter is < 0 or > 4 || previous.Length != row.Length || channels is not (3 or 4))
            throw new InvalidDataException("Invalid PNG filter parameters.");
        for (var i = 0; i < row.Length; i++)
        {
            var a = i < channels ? 0 : row[i - channels];
            var b = previous[i];
            var c = i < channels ? 0 : previous[i - channels];
            var predictor = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => 0 };
            row[i] = unchecked((byte)(row[i] + predictor));
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
