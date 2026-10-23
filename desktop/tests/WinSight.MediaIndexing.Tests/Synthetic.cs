using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace WinSight.MediaIndexing.Tests;

internal static class Check
{
    internal static void That(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
    internal static async Task<Exception> Throws(Func<Task> action, Func<Exception, bool> accepts)
    {
        try { await action(); }
        catch (Exception e) { if (accepts(e)) return e; throw new InvalidDataException("Wrong exception type: " + e.GetType().Name, e); }
        throw new InvalidDataException("Expected rejection, operation succeeded");
    }
    internal static bool DataOrIo(Exception e) => e is InvalidDataException or IOException;
}

// This is an independent test oracle, never a replacement production sampler.
internal static class Oracle
{
    internal static (double Start, double End)[] Audio(double duration)
    {
        long samples = (long)Math.Floor(duration * 16000), offset = 0;
        var result = new List<(double, double)>();
        while (samples - offset >= 480000) { result.Add((offset / 16000d, (offset + 480000) / 16000d)); offset += 400000; }
        long fresh = samples - offset - (result.Count == 0 ? 0 : 80000);
        if (fresh >= 32000) result.Add((offset / 16000d, samples / 16000d));
        return result.ToArray();
    }
    internal static double[] Video(double duration) => Enumerable.Range(0, 1200)
        .Select(i => Math.Min(.5, duration / 2) + i * Math.Max(3, duration / 1200)).Where(t => t < duration).ToArray();
    internal static double Rms(IEnumerable<short> values)
    {
        var a = values.ToArray(); return a.Length == 0 ? 0 : Math.Sqrt(a.Sum(v => Math.Pow(v / 32768d, 2)) / a.Length);
    }
    internal static ulong Hash(int width, int height, byte[] rgb)
    {
        int[] cells = new int[64];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            int left = x * width / 8, top = y * height / 8;
            int right = Math.Max(left + 1, (x + 1) * width / 8), bottom = Math.Max(top + 1, (y + 1) * height / 8);
            int sum = 0, n = 0;
            for (int j = top; j < bottom; j++) for (int i = left; i < right; i++)
            { int p = (j * width + i) * 3; sum += (299 * rgb[p] + 587 * rgb[p + 1] + 114 * rgb[p + 2]) / 1000; n++; }
            cells[y * 8 + x] = sum / n;
        }
        int average = cells.Sum() / 64; ulong hash = 0;
        for (int i = 0; i < 64; i++) if (cells[i] > average) hash |= 1UL << i;
        return hash;
    }
}

internal sealed record Scenario(double Duration, string Mode = "tone", bool HasAudio = true, int Width = 8, int Height = 8, bool Audit = false);
internal sealed record PidReceipt(int Pid, long CreationUtcTicks);
internal static class Owned
{
    internal static void Record(string source)
    {
        PidReceipt id;
        using (var p = Process.GetCurrentProcess()) id = new(p.Id, p.StartTime.ToUniversalTime().Ticks);
        string dir = Path.Combine(Path.GetDirectoryName(source)!, "p"); Directory.CreateDirectory(dir);
        Atomic(Path.Combine(dir, id.Pid + ".json"), id);
    }
    internal static void Atomic(string path, object value)
    {
        string tmp = path + ".tmp";
        using (var writer = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(writer, value); writer.Flush(flushToDisk: true); }
        File.Move(tmp, path);
    }
    internal static void Gone(string root)
    {
        foreach (var path in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Where(p => Path.GetFileName(Path.GetDirectoryName(p)) == "p"))
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var id = JsonSerializer.Deserialize<PidReceipt>(s) ?? throw new InvalidDataException("Bad PID receipt");
            try { using var p = Process.GetProcessById(id.Pid); Check.That(p.HasExited || p.StartTime.ToUniversalTime().Ticks != id.CreationUtcTicks, "Owned worker still running: " + id.Pid); }
            catch (ArgumentException) { }
        }
    }
}
internal static class Wave
{
    internal static void Write(string path, double seconds, double origin = 0, string mode = "tone")
    {
        int count = checked((int)Math.Round(seconds * 16000));
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(48 + count * 2); w.Write("WAVEJUNK"u8); w.Write(3); w.Write(new byte[] { 1, 2, 3, 0 });
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(count * 2);
        for (int i = 0; i < count; i++)
        {
            double t = origin + i / 16000d;
            short v = mode switch { "silent" => 0, "r98" => 98, "r99" => 99, _ => (short)(7000 * Math.Sin(2 * Math.PI * (280 + 180 * Math.Floor(t * 2)) * t)) };
            w.Write(v);
        }
    }
    internal static short[] Read(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); using var r = new BinaryReader(s);
        Check.That(r.ReadUInt32() == 0x46464952 && r.ReadUInt32() == s.Length - 8 && r.ReadUInt32() == 0x45564157, "RIFF signature/size");
        bool format = false; short[]? data = null;
        while (s.Position + 8 <= s.Length)
        {
            uint id = r.ReadUInt32(), n = r.ReadUInt32(); long end = s.Position + n;
            Check.That(end <= s.Length, "RIFF truncated chunk");
            if (id == 0x20746d66)
            {
                Check.That(!format && n >= 16, "RIFF duplicate/bad format"); format = true;
                ushort tag = r.ReadUInt16(); short ch = r.ReadInt16(); int rate = r.ReadInt32(), bytes = r.ReadInt32(); short align = r.ReadInt16(), bits = r.ReadInt16();
                if (tag == 0xfffe && n >= 40)
                { Check.That(r.ReadUInt16() >= 22 && r.ReadUInt16() == 16, "RIFF extensible format"); r.ReadUInt32(); Check.That(new Guid(r.ReadBytes(16)) == new Guid("00000001-0000-0010-8000-00aa00389b71"), "RIFF non-PCM subformat"); }
                else Check.That(tag == 1, "RIFF non PCM");
                Check.That(ch == 1 && rate == 16000 && bytes == 32000 && align == 2 && bits == 16, "RIFF must be PCM16 mono16k");
            }
            if (id == 0x61746164)
            {
                Check.That(data is null && n % 2 == 0, "RIFF bad data"); data = new short[n / 2]; for (int i = 0; i < data.Length; i++) data[i] = r.ReadInt16();
            }
            s.Position = end + (n & 1);
        }
        Check.That(format && data is not null && s.Position == s.Length, "RIFF missing chunks/padding"); return data!;
    }
    internal static void Markers(short[] data, double origin)
    {
        for (double local = .12; local + .08 < data.Length / 16000d - .09; local += .18)
        {
            double t = origin + local + .04; if (Math.Abs(t * 2 - Math.Round(t * 2)) < .18) continue;
            int block = (int)Math.Floor(t * 2), start = (int)(local * 16000), n = 1280;
            double Power(int b)
            {
                double re = 0, im = 0;
                for (int i = 0; i < n; i++) { double a = 2 * Math.PI * (280 + 180 * b) * i / 16000; re += data[start + i] * Math.Cos(a); im += data[start + i] * Math.Sin(a); }
                return re * re + im * im;
            }
            double expected = Power(block); Check.That(expected > 1e9, "Zero/padded temporal audio marker");
            foreach (int b in new[] { block - 1, block + 1, 0 }.Distinct().Where(b => b >= 0 && b != block)) Check.That(expected > 5 * Power(b), "Wrong source-time audio marker");
        }
    }
}
internal static class Png
{
    internal static byte[] Scene(int width, int height, int scene)
    {
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            // A/B/C: d(A,B)=4, d(B,C)=4, d(A,C)=8. Dropped B must not replace last-kept A.
            bool white = scene switch { 0 => i < 32, 1 => i < 28, _ => i < 24 };
            rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = white ? (byte)255 : (byte)0;
        }
        return rgb;
    }
    internal static void Write(string path, int width, int height, byte[] rgb, int filter = 0)
    {
        using var file = File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 2;
        Chunk(file, "IHDR", header); int stride = width * 3; using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte((byte)filter);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= 3 ? rgb[y * stride + i - 3] : 0, b = y > 0 ? rgb[(y - 1) * stride + i] : 0, c = y > 0 && i >= 3 ? rgb[(y - 1) * stride + i - 3] : 0;
                int predict = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => throw new InvalidDataException("Bad filter") };
                raw.WriteByte(unchecked((byte)(rgb[y * stride + i] - predict)));
            }
        }
        using var compressed = new MemoryStream(); using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) z.Write(raw.ToArray());
        Chunk(file, "IDAT", compressed.ToArray()); Chunk(file, "IEND", []);
    }
    private static int Paeth(int a, int b, int c)
    { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
    private static void Chunk(Stream s, string name, byte[] payload)
    {
        Span<byte> n = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(n, payload.Length); s.Write(n); byte[] type = Encoding.ASCII.GetBytes(name); s.Write(type); s.Write(payload);
        uint crc = uint.MaxValue;
        foreach (byte b in type.Concat(payload)) { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320); }
        BinaryPrimitives.WriteUInt32BigEndian(n, ~crc); s.Write(n);
    }
}
