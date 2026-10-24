using System.Buffers.Binary;
using System.IO.Compression;

namespace WinSight.Infrastructure.Media;

internal static class NativeMediaPng
{
    internal static void Validate(FileStream stream, int width, int height, int maxEdge, CancellationToken cancellationToken) =>
        ValidateCore(stream, width, height, maxEdge, 1024, cancellationToken);

    internal static void ValidateDocumentPage(FileStream stream, int width, int height, int maxEdge, CancellationToken cancellationToken) =>
        ValidateCore(stream, width, height, maxEdge, 2600, cancellationToken);

    private static void ValidateCore(FileStream stream, int width, int height, int maxEdge, int allowedEdge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxEdge < 64 || maxEdge > allowedEdge || width < 1 || height < 1 || width > maxEdge || height > maxEdge ||
            stream.Length < 33 || stream.Length > maxEdge * (long)maxEdge * 8 + 65536)
            throw new InvalidDataException("N1 PNG size is outside the bounded artifact contract.");
        var png = new byte[checked((int)stream.Length)];
        for (var start = 0; start < png.Length; start += 65536)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(png.AsSpan(start, Math.Min(65536, png.Length - start)));
        }
        if (!png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("N1 artifact is not a PNG.");
        using var compressed = new MemoryStream(png.Length);
        var channels = 0;
        var interlace = 0;
        var dataSeen = false;
        var dataClosed = false;
        var paletteSeen = false;
        var transparencySeen = false;
        var endSeen = false;
        for (var offset = 8; offset < png.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (png.Length - offset < 12) throw new InvalidDataException("Truncated N1 PNG chunk.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > png.Length - offset - 12) throw new InvalidDataException("Truncated N1 PNG chunk payload.");
            var type = png.AsSpan(offset + 4, 4);
            foreach (var value in type)
                if (value is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z'))
                    throw new InvalidDataException("Invalid N1 PNG chunk type.");
            if ((type[2] & 32) != 0) throw new InvalidDataException("Reserved N1 PNG chunk type bit.");
            var data = png.AsSpan(offset + 8, (int)length);
            if (Crc32(png.AsSpan(offset + 4, (int)length + 4), cancellationToken) !=
                BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + (int)length, 4)))
                throw new InvalidDataException("N1 PNG chunk CRC mismatch.");
            var id = BinaryPrimitives.ReadUInt32BigEndian(type);
            if (offset == 8 && id != 0x49484452) throw new InvalidDataException("N1 PNG must begin with IHDR.");
            if (dataSeen && id != 0x49444154) dataClosed = true;
            switch (id)
            {
                case 0x49484452: // IHDR
                    if (offset != 8 || data.Length != 13 ||
                        BinaryPrimitives.ReadUInt32BigEndian(data[..4]) != width ||
                        BinaryPrimitives.ReadUInt32BigEndian(data[4..8]) != height ||
                        data[8] != 8 || data[9] is not (2 or 6) || data[10] != 0 || data[11] != 0 || data[12] > 1)
                        throw new InvalidDataException("N1 PNG header does not match the frame response.");
                    channels = data[9] == 2 ? 3 : 4;
                    interlace = data[12];
                    break;
                case 0x504c5445: // PLTE is optional for truecolour, but must precede image data/tRNS.
                    if (paletteSeen || dataSeen || transparencySeen || data.Length is < 3 or > 768 || data.Length % 3 != 0)
                        throw new InvalidDataException("Invalid N1 PNG palette order or size.");
                    paletteSeen = true;
                    break;
                case 0x74524e53: // tRNS
                    if (transparencySeen || dataSeen || channels != 3 || data.Length != 6 ||
                        data[0] != 0 || data[2] != 0 || data[4] != 0)
                        throw new InvalidDataException("Invalid N1 PNG transparency.");
                    transparencySeen = true;
                    break;
                case 0x49444154: // IDAT
                    if (dataClosed) throw new InvalidDataException("N1 PNG image data must be consecutive.");
                    dataSeen = true;
                    compressed.Write(data);
                    break;
                case 0x49454e44: // IEND
                    if (!dataSeen || compressed.Length == 0 || data.Length != 0 || offset + 12 != png.Length)
                        throw new InvalidDataException("Invalid N1 PNG termination.");
                    endSeen = true;
                    break;
                default:
                    if ((type[0] & 32) == 0) throw new InvalidDataException("Unknown critical N1 PNG chunk.");
                    break;
            }
            offset += (int)length + 12;
        }
        if (!endSeen) throw new InvalidDataException("N1 PNG is missing IEND.");
        ValidateScanlines(compressed.GetBuffer(), checked((int)compressed.Length), width, height, channels, interlace, cancellationToken);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        var crc = uint.MaxValue;
        for (var i = 0; i < bytes.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            crc ^= bytes[i];
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1)));
        }
        return ~crc;
    }

    private static void ValidateScanlines(byte[] compressed, int length, int width, int height, int channels, int interlace,
        CancellationToken cancellationToken)
    {
        if (length < 6 || (compressed[0] & 15) != 8 || (compressed[0] >> 4) > 7 ||
            ((compressed[0] << 8) | compressed[1]) % 31 != 0 || (compressed[1] & 32) != 0)
            throw new InvalidDataException("Invalid N1 PNG zlib header.");
        using var input = new ExactZlibInput(compressed, length, cancellationToken);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
        Span<byte> row = stackalloc byte[checked(width * channels)];
        uint a = 1, b = 0;
        // Adam7 includes seven independently filtered passes; empty passes have no filter byte.
        ReadOnlySpan<int> x = [0, 4, 0, 2, 0, 1, 0], y = [0, 0, 4, 0, 2, 0, 1];
        ReadOnlySpan<int> dx = [8, 8, 4, 4, 2, 2, 1], dy = [8, 8, 8, 4, 4, 2, 2];
        for (var pass = 0; pass < (interlace == 0 ? 1 : 7); pass++)
        {
            var w = interlace == 0 ? width : (width - x[pass] + dx[pass] - 1) / dx[pass];
            var h = interlace == 0 ? height : (height - y[pass] + dy[pass] - 1) / dy[pass];
            if (w <= 0 || h <= 0) continue;
            for (var line = 0; line < h; line++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filter = zlib.ReadByte();
                if (filter is < 0 or > 4) throw new InvalidDataException("Invalid N1 PNG scanline filter or truncated image data.");
                a = (a + (uint)filter) % 65521; b = (b + a) % 65521;
                var pixels = row[..(w * channels)];
                try { zlib.ReadExactly(pixels); }
                catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated N1 PNG scanline.", ex); }
                foreach (var value in pixels) { a = (a + value) % 65521; b = (b + a) % 65521; }
            }
        }
        if (zlib.ReadByte() != -1 || input.BytesRead != length ||
            BinaryPrimitives.ReadUInt32BigEndian(compressed.AsSpan(length - 4, 4)) != (b << 16 | a))
            throw new InvalidDataException("N1 PNG image data has an invalid checksum, extent or zlib termination.");
    }

    // .NET inflate can read ahead or return EOF on truncated input. One-byte input plus a hard EOF
    // distinguishes an actual zlib end marker/checksum from truncation and trailing compressed bytes.
    private sealed class ExactZlibInput(byte[] bytes, int length, CancellationToken cancellationToken) : Stream
    {
        internal int BytesRead { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if ((BytesRead & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (BytesRead == length) throw new InvalidDataException("Truncated N1 PNG zlib stream.");
            buffer[0] = bytes[BytesRead++];
            return 1;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
