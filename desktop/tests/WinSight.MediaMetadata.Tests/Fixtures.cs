using System.Security.Cryptography;
using System.Text.Json;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace WinSight.MediaMetadata.Tests;

internal sealed record NativeInfo(string SourcePath, string Kind, double DurationSeconds, bool HasAudio, int? AudioTrackCount, int Width, int Height);
internal sealed record Interval(double Start, double End, string Reference, int PhysicalFrames);
internal sealed record Synthetic(string WavePath, string SilentPath, string VideoPath, NativeInfo[] Metadata, Interval[] Intervals);
internal static class Fixtures
{
    internal static async Task<Synthetic> Create(string root)
    {
        Directory.CreateDirectory(root); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35)); var ct = deadline.Token;
        string wave = Path.Combine(root, "s.wav"); Wave.Write(wave, 4); var parsed = Wave.Read(wave);
        Verify.That(parsed.Frames == 64000, "Physical source WAV frame count"); Wave.Markers(parsed, 0);
        var folder = await StorageFolder.GetFolderFromPathAsync(root).AsTask(ct);
        var input = await StorageFile.GetFileFromPathAsync(wave).AsTask(ct);
        var audio = await BackgroundAudioTrack.CreateFromFileAsync(input).AsTask(ct);
        var infos = new List<NativeInfo> { new(wave, "audio", audio.OriginalDuration.TotalSeconds, true, null, 0, 0) };
        string silent = Path.Combine(root, "n.mp4"), video = Path.Combine(root, "v.mp4");
        foreach (bool hasAudio in new[] { false, true })
        {
            var composition = new MediaComposition();
            composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 40, 90, 130), TimeSpan.FromSeconds(4)));
            if (hasAudio) composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(input).AsTask(ct));
            var file = await folder.CreateFileAsync(hasAudio ? "v.mp4" : "n.mp4", CreationCollisionOption.FailIfExists).AsTask(ct);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Qvga); profile.Video.Width = 256; profile.Video.Height = 144;
            if (!hasAudio) profile.Audio = null;
            Verify.That(await composition.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile).AsTask(ct) == TranscodeFailureReason.None, "Synthetic MP4 render failed");
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask(ct); var dimensions = clip.GetVideoEncodingProperties(); int count = clip.EmbeddedAudioTracks.Count;
            Verify.That(count == (hasAudio ? 1 : 0) && dimensions.Width == 256 && dimensions.Height == 144 && clip.OriginalDuration.TotalSeconds > 0, "Native synthetic metadata disagrees");
            infos.Add(new(file.Path, "video", clip.OriginalDuration.TotalSeconds, count > 0, count, (int)dimensions.Width, (int)dimensions.Height));
        }
        var intervals = new List<Interval>(); double duration = infos[2].DurationSeconds;
        foreach (var range in new[] { (Start: .75, End: 2.25), (Start: duration - .75, End: duration) })
        {
            string name = "r" + intervals.Count + ".wav";
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.FailIfExists).AsTask(ct);
            var source = await StorageFile.GetFileFromPathAsync(video).AsTask(ct);
            var clip = await MediaClip.CreateFromFileAsync(source).AsTask(ct);
            clip.TrimTimeFromStart = TimeSpan.FromSeconds(range.Start); clip.TrimTimeFromEnd = clip.OriginalDuration - TimeSpan.FromSeconds(range.End);
            var composition = new MediaComposition(); composition.Clips.Add(clip);
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low); profile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);
            Verify.That(await composition.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile).AsTask(ct) == TranscodeFailureReason.None, "Native timeline reference failed");
            var samples = Wave.Read(file.Path); Wave.Markers(samples, range.Start);
            Verify.That(Math.Abs(samples.Frames - (range.End - range.Start) * 16000) <= 1, "Native timeline does not cover interval; preserve this failure, do not pad");
            intervals.Add(new(range.Start, range.End, file.Path, samples.Frames));
        }
        var result = new Synthetic(wave, silent, video, infos.ToArray(), intervals.ToArray());
        var evidence = new { result, hashes = Directory.GetFiles(root).Select(p => new { path = p, bytes = new FileInfo(p).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }).ToArray(),
            tolerance = "Metadata: 1us vs actual Native MediaClip/BackgroundAudioTrack. Audio: physical native timeline within 1 sample; correlation >0.98. Spectral marker windows exclude 90ms boundaries, exceeding AAC 1024/48000 + video 1/30 (<55ms); no inserted padding/repeats." };
        File.WriteAllText(Path.Combine(root, "physical.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }
}
