using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WinSight.MediaMetadata.Tests;

internal static class Verify
{
    internal static void That(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
    internal static async Task<Exception> Throws(Func<Task> action, Func<Exception, bool>? predicate = null)
    {
        try { await action(); }
        catch (Exception e) { if (predicate is null || predicate(e)) return e; throw new InvalidDataException("Wrong failure type: " + e, e); }
        throw new InvalidDataException("Expected failure, operation succeeded.");
    }
}

internal sealed record WaveData(int Rate, short Channels, short Bits, short[] Samples)
{
    internal int Frames => Samples.Length / Channels;
    internal double Seconds => Frames / (double)Rate;
}
internal static class Wave
{
    internal static void Write(string path, double seconds, double origin = 0)
    {
        var count = checked((int)Math.Round(seconds * 16000));
        using var w = new BinaryWriter(File.Create(path));
        // Odd-size ancillary chunk catches readers that assume a 44-byte header.
        w.Write("RIFF"u8); w.Write(48 + count * 2); w.Write("WAVEJUNK"u8); w.Write(3); w.Write(new byte[] { 1, 2, 3, 0 });
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(count * 2);
        for (int i = 0; i < count; i++)
        {
            double t = origin + i / 16000d;
            double hz = 280 + 180 * Math.Floor(t * 2);
            w.Write((short)(7000 * Math.Sin(2 * Math.PI * hz * t)));
        }
    }
    internal static WaveData Read(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var r = new BinaryReader(s);
        Verify.That(r.ReadUInt32() == 0x46464952 && r.ReadUInt32() == s.Length - 8 && r.ReadUInt32() == 0x45564157, "RIFF length/type invalid");
        int rate = 0; short channels = 0, bits = 0; bool fmt = false; short[]? samples = null;
        while (s.Position + 8 <= s.Length)
        {
            uint id = r.ReadUInt32(), n = r.ReadUInt32(); long start = s.Position;
            Verify.That(n <= s.Length - start, "Truncated WAV chunk");
            if (id == 0x20746d66)
            {
                Verify.That(!fmt && n >= 16, "Invalid/duplicate WAV format"); fmt = true;
                ushort tag = r.ReadUInt16(); channels = r.ReadInt16(); rate = r.ReadInt32(); int bytes = r.ReadInt32(); short align = r.ReadInt16(); bits = r.ReadInt16();
                if (tag == 0xfffe && n >= 40)
                {
                    ushort extra = r.ReadUInt16(), valid = r.ReadUInt16(); r.ReadUInt32();
                    Verify.That(extra >= 22 && valid == 16 && new Guid(r.ReadBytes(16)) == new Guid("00000001-0000-0010-8000-00aa00389b71"), "Not extensible PCM16");
                }
                else Verify.That(tag == 1, "Not PCM");
                Verify.That(channels == 1 && rate == 16000 && bits == 16 && align == 2 && bytes == 32000, "Expected PCM16 mono16k");
            }
            if (id == 0x61746164)
            {
                Verify.That(samples is null && n > 0 && n % 2 == 0, "Invalid/duplicate data");
                samples = new short[checked((int)n / 2)]; for (int i = 0; i < samples.Length; i++) samples[i] = r.ReadInt16();
            }
            s.Position = checked(start + n + (n & 1));
        }
        Verify.That(fmt && samples is not null && s.Position == s.Length, "Missing/truncated WAV chunks");
        return new(rate, channels, bits, samples!);
    }
    internal static double Power(short[] samples, int start, int count, double hz)
    {
        double real = 0, imag = 0;
        for (int i = 0; i < count; i++) { double angle = 2 * Math.PI * hz * i / 16000; real += samples[start + i] * Math.Cos(angle); imag += samples[start + i] * Math.Sin(angle); }
        return real * real + imag * imag;
    }
    internal static void Markers(WaveData data, double origin)
    {
        // 80ms windows, 90ms from AAC/video-trim boundaries (1024/48000 + 1/30 < 55ms).
        for (double local = .12; local + .08 < data.Seconds - .09; local += .18)
        {
            double t = origin + local + .04;
            if (Math.Abs(t * 2 - Math.Round(t * 2)) < .18) continue;
            int block = (int)Math.Floor(t * 2), start = (int)(local * 16000), count = 1280;
            double expected = Power(data.Samples, start, count, 280 + 180 * block);
            for (int other = 0; other < 8; other++) if (other != block)
                Verify.That(expected > 5 * Power(data.Samples, start, count, 280 + 180 * other), "Wrong time marker (zero-start/repeated/shifted audio)");
            Verify.That(expected > 1e9, "Zero/padded marker window");
        }
    }
    internal static void SamePhysicalTimeline(string actual, string reference)
    {
        var a = Read(actual); var b = Read(reference);
        Verify.That(Math.Abs(a.Frames - b.Frames) <= 1, $"Physical frame count differs from native timeline: {a.Frames} vs {b.Frames}");
        int n = Math.Min(a.Frames, b.Frames); double ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < n; i++) { ab += (double)a.Samples[i] * b.Samples[i]; aa += (double)a.Samples[i] * a.Samples[i]; bb += (double)b.Samples[i] * b.Samples[i]; }
        Verify.That(aa > 1e8 && bb > 1e8 && ab / Math.Sqrt(aa * bb) > .98, "Audio differs from native timeline; no padding/repetition allowed");
        // Local windows prevent a good middle from hiding a padded/truncated audible EOF tail.
        for (int start = 0; start < n; start += 160)
        {
            int end = Math.Min(start + 160, n); double localAB = 0, localAA = 0, localBB = 0;
            for (int i = start; i < end; i++) { localAB += (double)a.Samples[i] * b.Samples[i]; localAA += (double)a.Samples[i] * a.Samples[i]; localBB += (double)b.Samples[i] * b.Samples[i]; }
            if (localBB > 100d * (end - start))
                Verify.That(localAA > .9 * localBB && localAA < 1.1 * localBB && localAB / Math.Sqrt(localAA * localBB) > .98, "Local native timeline/EOF content lost at frame " + start);
            else Verify.That(localAA <= 400d * (end - start), "Invented audio beyond native audible timeline");
        }
    }
}

internal sealed record Identity(int Pid, long CreationUtcTicks, string Role);
internal static class Owned
{
    internal static string DirectoryFor(string source) => Path.Combine(Path.GetDirectoryName(source)!, "p");
    internal static void Record(string source, string role)
    {
        using var p = Process.GetCurrentProcess(); string dir = DirectoryFor(source); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, role + ".json"), tmp = path + "." + p.Id + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Identity(p.Id, p.StartTime.ToUniversalTime().Ticks, role)));
        File.Move(tmp, path, true);
    }
    internal static Identity[] Read(string source) => !Directory.Exists(DirectoryFor(source)) ? [] : Directory.GetFiles(DirectoryFor(source), "*.json").Select(path =>
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonSerializer.Deserialize<Identity>(s) ?? throw new InvalidDataException("Invalid PID receipt");
    }).ToArray();
    internal static bool Alive(Identity i)
    {
        try { using var p = Process.GetProcessById(i.Pid); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == i.CreationUtcTicks; }
        catch (ArgumentException) { return false; }
    }
    internal static void Stop(Identity i)
    {
        try { using var p = Process.GetProcessById(i.Pid); if (p.StartTime.ToUniversalTime().Ticks != i.CreationUtcTicks || p.HasExited) return; p.Kill(); Verify.That(p.WaitForExit(4000), "Owned PID termination unconfirmed"); }
        catch (ArgumentException) { }
    }
    internal static async Task Wait(string source, params string[] roles)
    {
        using var timeout = new CancellationTokenSource(6000);
        while (!roles.All(role => Read(source).Any(i => i.Role == role))) await Task.Delay(15, timeout.Token);
    }
    internal static void Gone(string source, params string[] roles)
    {
        var all = Read(source); Verify.That(roles.All(role => all.Any(i => i.Role == role)) && all.All(i => !Alive(i)), "Worker/descendants not confirmed gone");
    }
}

internal sealed record CommandResult(int Code, string Output, string Error);
internal static class Child
{
    internal static async Task<CommandResult> Run(string exe, IEnumerable<string> args, string receiptPath, int milliseconds = 12000)
    {
        var si = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) si.ArgumentList.Add(arg);
        using var p = Process.Start(si) ?? throw new IOException("Cannot start task child");
        var id = new Identity(p.Id, p.StartTime.ToUniversalTime().Ticks, "cli");
        File.WriteAllText(receiptPath + ".pid.json", JsonSerializer.Serialize(id));
        var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(milliseconds);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { Owned.Stop(id); throw new TimeoutException("Task CLI deadline (not a lifecycle pass)"); }
        var result = new CommandResult(p.ExitCode, await stdout.WaitAsync(TimeSpan.FromSeconds(2)), await stderr.WaitAsync(TimeSpan.FromSeconds(2)));
        File.WriteAllText(receiptPath + ".json", JsonSerializer.Serialize(result));
        return result;
    }
}
