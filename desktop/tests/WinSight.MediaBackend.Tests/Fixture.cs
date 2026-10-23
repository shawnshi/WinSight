using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace WinSight.MediaBackend.Tests;

internal sealed record HostConfig(string Root, string Evidence, string Mode = "ok", string? NativeHost = null);
internal sealed record MediaFixture(double Duration = 32, bool HasAudio = true, string Mode = "ok");
internal sealed record Identity(int Pid, long CreationUtcTicks);
internal static class Evidence
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static void Assert(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    internal static T Read<T>(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(s) ?? throw new InvalidDataException("Missing receipt: " + path);
    }
    internal static void Write(string path, object item)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var s = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(s, item, Json); s.Flush(true); }
        File.Move(tmp, path);
    }
    internal static Identity Current() { using var p = Process.GetCurrentProcess(); return new(p.Id, p.StartTime.ToUniversalTime().Ticks); }
    internal static void Event(HostConfig config, object item) => Write(Path.Combine(config.Evidence, Guid.NewGuid().ToString("N") + ".event.json"), item);
    internal static void Pid(HostConfig config, Identity id, string role) => Write(Path.Combine(config.Evidence, id.Pid + "." + role + ".pid.json"), id);
    internal static string Under(string path, string root)
    {
        Assert(Path.IsPathFullyQualified(path), "Absolute task path required");
        path = Path.GetFullPath(path); root = Path.GetFullPath(root);
        Assert(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Outside synthetic task root"); return path;
    }
    internal static bool Alive(Identity id)
    {
        try { using var p = Process.GetProcessById(id.Pid); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == id.CreationUtcTicks; }
        catch (ArgumentException) { return false; }
    }
    internal static void Gone(string evidence)
    { foreach (string file in Directory.GetFiles(evidence, "*.pid.json")) Assert(!Alive(Read<Identity>(file)), "Owned PID still alive: " + file); }
    internal static async Task<Exception> Throws(Func<Task> action, Func<Exception, bool> predicate)
    {
        try { await action(); } catch (Exception e) { Assert(predicate(e), "Wrong exception: " + e); return e; }
        throw new InvalidDataException("Expected rejection, operation succeeded");
    }
    internal static void Released(string file, string directory)
    {
        using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Move(file, file + ".moved"); File.Move(file + ".moved", file);
        Directory.Move(directory, directory + ".moved"); Directory.Move(directory + ".moved", directory);
    }
    internal static void Blocked(string file)
    {
        try { using var s = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return; }
        throw new InvalidDataException("Expected physical source/sample sharing pin");
    }
}
internal static class MediaBytes
{
    internal static void Wave(string path, double seconds, double origin = 0)
    {
        int n = checked((int)Math.Round(seconds * 16000));
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVEfmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++) w.Write((short)(7000 * Math.Sin(2 * Math.PI * 440 * (origin + i / 16000d))));
    }
    internal static void Png(string path, bool alternate)
    {
        using var file = File.Create(path); file.Write(new byte[] { 137,80,78,71,13,10,26,10 });
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, 8); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 8); header[8] = 8; header[9] = 2;
        Chunk(file, "IHDR", header); using var raw = new MemoryStream();
        for (int y = 0; y < 8; y++) { raw.WriteByte(0); for (int x = 0; x < 8; x++) { byte v = (alternate ? y < 4 : x < 4) ? (byte)240 : (byte)16; raw.Write([v,v,v]); } }
        using var compressed = new MemoryStream(); using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) z.Write(raw.ToArray());
        Chunk(file, "IDAT", compressed.ToArray()); Chunk(file, "IEND", []);
    }
    private static void Chunk(Stream s, string name, byte[] payload)
    {
        Span<byte> n = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(n, payload.Length); s.Write(n); byte[] type = Encoding.ASCII.GetBytes(name); s.Write(type); s.Write(payload);
        uint crc = uint.MaxValue; foreach (byte b in type.Concat(payload)) { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320); }
        BinaryPrimitives.WriteUInt32BigEndian(n, ~crc); s.Write(n);
    }
}
