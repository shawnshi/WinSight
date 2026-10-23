using System.Text;

namespace WinSight.NativeMedia.Tests;

internal static class EofTailOracle
{
    internal const int Frames = 16000;
    internal const int PointwiseTolerance = 1280;
    private const double RampSeconds = 0.004;

    // Analytic source reference, not output fitted: <=9.15e6 PCM units/sec slope.
    // Two 16k frames of timing error +128 units FIR/quantization budget <1280.
    // The 4ms smooth envelope avoids a discontinuity at the true source EOF.
    internal static short Reference(double secondsInLastSecond)
    {
        var t = secondsInLastSecond;
        var distance = Math.Min(t, 1 - t);
        var envelope = distance >= RampSeconds ? 1 : Math.Pow(Math.Sin(Math.PI * Math.Max(0, distance) / (2 * RampSeconds)), 2);
        return checked((short)Math.Round(envelope * (10000 + 2500 * Math.Sin(2 * Math.PI * 73 * t) + 1800 * Math.Sin(2 * Math.PI * 211 * t))));
    }

    internal static void Validate(string path)
    {
        var wave = Read(path);
        if (wave.Samples.Length != Frames) throw new InvalidDataException($"EOF requires {Frames} physical samples; actual={wave.Samples.Length}.");
        var maxError = 0;
        var maxIndex = 0;
        for (var i = 0; i < Frames; i++)
        {
            var error = Math.Abs(wave.Samples[i] - Reference(i / 16000.0));
            if (error > maxError) { maxError = error; maxIndex = i; }
            if (error > PointwiseTolerance)
                throw new InvalidDataException($"EOF marker mismatch sample={i}, expected={Reference(i / 16000.0)}, actual={wave.Samples[i]}, error={error}, fixedTolerance={PointwiseTolerance}.");
        }
        Console.WriteLine($"OBSERVED EOF samples=0..15999 checked=16000 maxAbsError={maxError} at={maxIndex} tolerance={PointwiseTolerance} last={wave.Samples[^1]} referenceLast={Reference(15999 / 16000.0)} sha256={SyntheticMedia.Hash(path)}");
    }

    internal static void ValidateWithNegativeControls(string actualArtifact, string mutationRoot)
    {
        if (Directory.Exists(mutationRoot) || File.Exists(mutationRoot)) throw new ArgumentException("Mutation root must be new.");
        Validate(actualArtifact);
        var originalHash = SyntheticMedia.Hash(actualArtifact);
        Directory.CreateDirectory(mutationRoot);
        var wave = Read(actualArtifact);
        foreach (var (name, tailFrames, mode) in new[]
        {
            ("zero-800", 800, "zero"), ("copy-800", 800, "copy"), ("repeat-800", 800, "repeat"),
            ("zero-31", 31, "zero"), ("copy-31", 31, "copy"), ("repeat-31", 31, "repeat"),
            ("short-31", 31, "short")
        })
        {
            var path = Path.Combine(mutationRoot, name + ".wav");
            var bytes = File.ReadAllBytes(actualArtifact);
            var start = Frames - tailFrames;
            if (mode == "short")
            {
                bytes = bytes[..(wave.DataOffset + start * 2)];
                BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
                BitConverter.GetBytes(start * 2).CopyTo(bytes, wave.DataOffset - 4);
            }
            else
                for (var i = start; i < Frames; i++)
                {
                    short sample = mode == "zero" ? (short)0 : mode == "repeat" ? wave.Samples[start - 1] : wave.Samples[i - tailFrames];
                    BitConverter.GetBytes(sample).CopyTo(bytes, wave.DataOffset + i * 2);
                }
            File.WriteAllBytes(path, bytes);
            try { Validate(path); }
            catch (InvalidDataException error)
            {
                Console.WriteLine($"REJECTED EOF mutation={name} sha256={SyntheticMedia.Hash(path)} reason={error.Message}");
                continue;
            }
            throw new InvalidDataException($"EOF checker accepted negative control {name}.");
        }
        if (SyntheticMedia.Hash(actualArtifact) != originalHash) throw new InvalidDataException("Mutation controls modified original artifact.");
    }

    private static (short[] Samples, int DataOffset) Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("RIFF"u8)) throw new InvalidDataException("Not RIFF.");
        var riffSize = reader.ReadUInt32();
        if (riffSize + 8L != reader.BaseStream.Length || !reader.ReadBytes(4).AsSpan().SequenceEqual("WAVE"u8)) throw new InvalidDataException("Invalid WAV length/type.");
        short[]? samples = null;
        int dataOffset = 0;
        bool format = false;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            var start = reader.BaseStream.Position;
            if (size > reader.BaseStream.Length - start) throw new InvalidDataException("Truncated WAV chunk.");
            if (id == "fmt ")
            {
                if (format || size < 16) throw new InvalidDataException("Invalid WAV format chunk.");
                var tag = reader.ReadUInt16();
                if (reader.ReadUInt16() != 1 || reader.ReadUInt32() != 16000 || reader.ReadUInt32() != 32000 || reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16)
                    throw new InvalidDataException("Expected PCM16/16k/mono.");
                if (tag == 0xfffe)
                {
                    if (size < 40 || reader.ReadUInt16() < 22 || reader.ReadUInt16() != 16) throw new InvalidDataException("Invalid extensible PCM.");
                    _ = reader.ReadUInt32();
                    if (new Guid(reader.ReadBytes(16)) != new Guid("00000001-0000-0010-8000-00aa00389b71")) throw new InvalidDataException("Not PCM subformat.");
                }
                else if (tag != 1) throw new InvalidDataException("Not PCM.");
                format = true;
            }
            if (id == "data")
            {
                if (samples is not null || size % 2 != 0) throw new InvalidDataException("Invalid/duplicate PCM chunk.");
                dataOffset = checked((int)start);
                samples = new short[checked((int)(size / 2))];
                for (var i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16();
            }
            reader.BaseStream.Position = start + size + (size & 1);
        }
        if (!format || samples is null) throw new InvalidDataException("Missing WAV format/data.");
        return (samples, dataOffset);
    }
}
