using System.IO.Compression;
using System.Buffers.Binary;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Graphics.Imaging;

namespace WinSight.MediaProcess.Tests;

internal static class FixtureMedia
{
    internal static void Wave(string path, double seconds = 1)
    {
        var samples = checked((int)(seconds * 16000));
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000);
        writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(1000 * Math.Sin(2 * Math.PI * 440 * i / 16000)));
    }

    internal static void Png(string path)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 2);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 2);
        header[8] = 8; header[9] = 2;
        Chunk(file, "IHDR"u8, header);
        using var data = new MemoryStream();
        using (var zip = new ZLibStream(data, CompressionLevel.SmallestSize, true))
            zip.Write(new byte[] { 0, 192, 32, 32, 192, 32, 32, 0, 192, 32, 32, 192, 32, 32 });
        Chunk(file, "IDAT"u8, data.ToArray()); Chunk(file, "IEND"u8, []);
    }

    internal static readonly string[] PngFaultModes =
        ["pngheader33", "pngihdrcrc", "pngidatcrc", "pngnoidat", "pngnoiend", "pngtruncated", "pngtrailing",
         "pngdeflate", "pngzlibtruncated", "pngadler", "pngexcess"];

    internal static void CorruptPng(string path, string mode)
    {
        var valid = File.ReadAllBytes(path);
        var idatLength = BinaryPrimitives.ReadInt32BigEndian(valid.AsSpan(33, 4));
        byte[] mutated;
        switch (mode)
        {
            case "pngheader33": mutated = valid[..33]; break;
            case "pngihdrcrc": mutated = valid; mutated[29] ^= 1; break;
            case "pngidatcrc": mutated = valid; mutated[41 + idatLength] ^= 1; break;
            case "pngnoidat": mutated = [.. valid[..33], .. valid[^12..]]; break;
            case "pngnoiend": mutated = valid[..^12]; break;
            case "pngtruncated": mutated = valid[..^15]; break;
            case "pngtrailing": mutated = [.. valid, 0x42]; break;
            case "pngdeflate":
            case "pngzlibtruncated":
            case "pngadler":
            case "pngexcess":
                var payload = valid.AsSpan(41, idatLength).ToArray();
                if (mode == "pngdeflate") payload = [0x78, 0x9c, 0x07, 0, 0, 0, 0]; // Reserved DEFLATE block type, inside a legal PNG chunk.
                else if (mode == "pngzlibtruncated") payload = payload[..^2];
                else if (mode == "pngadler") payload[^1] ^= 1;
                else
                {
                    using var encoded = new MemoryStream(payload);
                    using var decoder = new ZLibStream(encoded, CompressionMode.Decompress);
                    using var pixels = new MemoryStream(); decoder.CopyTo(pixels); pixels.WriteByte(0);
                    using var compressed = new MemoryStream();
                    using (var encoder = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) encoder.Write(pixels.ToArray());
                    payload = compressed.ToArray();
                }
                using (var rebuilt = new MemoryStream())
                {
                    rebuilt.Write(valid.AsSpan(0, 33)); Chunk(rebuilt, "IDAT"u8, payload); Chunk(rebuilt, "IEND"u8, []);
                    mutated = rebuilt.ToArray();
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
        File.WriteAllBytes(path, mutated);
    }

    private static void Chunk(Stream stream, ReadOnlySpan<byte> kind, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length); stream.Write(word); stream.Write(kind); stream.Write(data);
        uint crc = uint.MaxValue;
        foreach (var b in kind.ToArray().Concat(data))
        {
            crc ^= b;
            for (var n = 0; n < 8; n++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); stream.Write(word);
    }

    internal static async Task Video(string path)
    {
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 192, 32, 32), TimeSpan.FromSeconds(2)));
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.FailIfExists);
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Qvga);
        profile.Video.Width = 256; profile.Video.Height = 144;
        profile.Video.FrameRate.Numerator = 30; profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1; profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Bitrate = 250000; profile.Audio = null;
        var result = await composition.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile);
        if (result != TranscodeFailureReason.None) throw new IOException($"Synthetic video render: {result}");
    }

    internal static void CheckWave(string path, double seconds)
    {
        var data = File.ReadAllBytes(path);
        Check.That(data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            data.AsSpan(8, 4).SequenceEqual("WAVE"u8) && BitConverter.ToUInt32(data, 4) == data.Length - 8, "Complete RIFF/WAVE signature");
        var position = 12;
        var format = false;
        var payload = false;
        while (position + 8 <= data.Length)
        {
            var length = checked((int)BitConverter.ToUInt32(data, position + 4));
            var start = position + 8;
            Check.That(length <= data.Length - start, "Complete WAV chunk");
            if (data.AsSpan(position, 4).SequenceEqual("fmt "u8))
            {
                Check.That(!format && length >= 16, "One complete format chunk");
                Check.That(BitConverter.ToInt16(data, start) == 1 && BitConverter.ToInt16(data, start + 2) == 1 &&
                    BitConverter.ToInt32(data, start + 4) == 16000 && BitConverter.ToInt32(data, start + 8) == 32000 &&
                    BitConverter.ToInt16(data, start + 12) == 2 && BitConverter.ToInt16(data, start + 14) == 16, "Actual PCM16 mono 16kHz");
                format = true;
            }
            if (data.AsSpan(position, 4).SequenceEqual("data"u8))
            {
                Check.That(!payload && length == (int)(seconds * 16000) * 2, "Full requested audio interval");
                payload = true;
            }
            position = checked(start + length + (length & 1));
            Check.That(position <= data.Length, "WAV chunk padding");
        }
        Check.That(format && payload && position == data.Length, "Complete WAV format and data; legal JUNK/LIST chunks permitted");
    }

    internal static async Task CheckPng(string path, uint maxEdge, bool exactFixture = false)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        Check.That(decoder.DecoderInformation.CodecId == BitmapDecoder.PngDecoderId && decoder.PixelWidth > 0 &&
            decoder.PixelHeight > 0 && decoder.PixelWidth <= maxEdge && decoder.PixelHeight <= maxEdge, "PNG dimensions/codec");
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        Check.That(Math.Abs(pixels[0] - 192) <= 8 && Math.Abs(pixels[1] - 32) <= 8 && Math.Abs(pixels[2] - 32) <= 8, "Decoded synthetic red pixel");
        if (exactFixture) Check.That(decoder.PixelWidth == 2 && decoder.PixelHeight == 2, "Fake PNG size");
    }
}

internal static class Check
{
    internal static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        var task = action();
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(12))) != task)
            throw new InvalidOperationException("External oracle deadline expired; this is not the production TimeoutException contract.");
        try { await task; }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}, got success.");
    }
}
