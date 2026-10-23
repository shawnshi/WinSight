using System.Security.Cryptography;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace WinSight.NativeMedia.Tests;

internal static class SyntheticMedia
{
    internal static readonly short[] Levels = [1000, 7000, -5000, 13000];
    internal static readonly (byte R, byte G, byte B)[] Colors = [(192, 32, 32), (32, 176, 64), (32, 48, 192)];

    internal static async Task CreateAsync(string root)
    {
        if (!Path.IsPathFullyQualified(root) || Directory.Exists(root) || File.Exists(root))
            throw new ArgumentException("Fixture root must be an explicit new absolute task directory.");
        Directory.CreateDirectory(root);
        WriteWave(Path.Combine(root, "levels-4s.wav"));
        WriteWave(Path.Combine(root, "eof-marker-4s.wav"), marker: true);
        await WriteVideoAsync(root, "landscape.mp4", 256, 144);
        await WriteVideoAsync(root, "portrait.mp4", 144, 256);
    }

    private static void WriteWave(string path, bool marker = false)
    {
        const int rate = 48000, channels = 2, frames = rate * 4;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + frames * channels * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)channels); writer.Write(rate);
        writer.Write(rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(frames * channels * 2);
        for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
                writer.Write(marker ? (frame < rate * 3 ? (short)0 : EofTailOracle.Reference((frame - rate * 3) / (double)rate)) : Levels[frame / rate]);
    }

    private static async Task WriteVideoAsync(string root, string name, uint width, uint height)
    {
        var composition = new MediaComposition();
        foreach (var (r, g, b) in Colors)
            composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, r, g, b), TimeSpan.FromSeconds(1)));
        var folder = await StorageFolder.GetFolderFromPathAsync(root);
        var output = await folder.CreateFileAsync(name, CreationCollisionOption.FailIfExists);
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Qvga);
        profile.Video.Width = width;
        profile.Video.Height = height;
        profile.Video.FrameRate.Numerator = 30;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Bitrate = 250000;
        profile.Audio = null;
        var failure = await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile);
        if (failure != TranscodeFailureReason.None) throw new InvalidOperationException($"Synthetic render failed: {failure}");
        var properties = await output.Properties.GetVideoPropertiesAsync();
        if (properties.Width != width || properties.Height != height || Math.Abs(properties.Duration.TotalSeconds - 3) > 0.1)
            throw new InvalidDataException($"Synthetic source mismatch: {name}, {properties.Width}x{properties.Height}, {properties.Duration}");
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
