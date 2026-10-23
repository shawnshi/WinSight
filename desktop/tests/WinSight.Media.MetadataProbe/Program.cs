using System.Text.Json;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0]))
    throw new ArgumentException("Supply one new absolute task fixture directory.");
var root = args[0]; Directory.CreateDirectory(root);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = deadline.Token;
var wave = Path.Combine(root, "source.wav");
using (var writer = new BinaryWriter(File.Create(wave)))
{
    const int frames = 32000;
    writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8);
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000);
    writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
    writer.Write("data"u8); writer.Write(frames * 2);
    for (var i = 0; i < frames; i++) writer.Write((short)(3000 * Math.Sin(2 * Math.PI * 440 * i / 16000)));
}
var folder = await StorageFolder.GetFolderFromPathAsync(root).AsTask(token);
var source = await StorageFile.GetFileFromPathAsync(wave).AsTask(token);
var reports = new List<object>();
foreach (var withAudio in new[] { false, true })
{
    var composition = new MediaComposition();
    composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 64, 96, 128), TimeSpan.FromSeconds(2)));
    if (withAudio) composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(source).AsTask(token));
    var output = await folder.CreateFileAsync(withAudio ? "with-audio.mp4" : "silent.mp4", CreationCollisionOption.FailIfExists).AsTask(token);
    var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Qvga);
    profile.Video.Width = 256; profile.Video.Height = 144;
    if (!withAudio) profile.Audio = null;
    var rendered = await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile).AsTask(token);
    if (rendered != TranscodeFailureReason.None) throw new InvalidOperationException("Synthetic video rendering failed: " + rendered);
    var clip = await MediaClip.CreateFromFileAsync(output).AsTask(token);
    var embedded = clip.EmbeddedAudioTracks.Count;
    bool? backgroundAccepted = null; double? audioDuration = null; string? audioError = null;
    if (withAudio)
    {
        try
        {
            var track = await BackgroundAudioTrack.CreateFromFileAsync(output).AsTask(token);
            backgroundAccepted = true; audioDuration = track.OriginalDuration.TotalSeconds;
            var pcm = new MediaComposition(); pcm.BackgroundAudioTracks.Add(track);
            pcm.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 0, 0, 0), track.OriginalDuration));
            var pcmFile = await folder.CreateFileAsync("soundtrack.wav", CreationCollisionOption.FailIfExists).AsTask(token);
            var wavProfile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            wavProfile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);
            var converted = await pcm.RenderToFileAsync(pcmFile, MediaTrimmingPreference.Precise, wavProfile).AsTask(token);
            if (converted != TranscodeFailureReason.None) throw new InvalidOperationException("Soundtrack PCM render failed: " + converted);
        }
        catch (Exception error) { backgroundAccepted = false; audioError = error.GetType().Name + ": " + error.Message; }
    }
    bool? clipAudioOnlySuccess = null; string? clipAudioError = null;
    if (withAudio)
    {
        try
        {
            clip.TrimTimeFromStart = TimeSpan.Zero;
            clip.TrimTimeFromEnd = clip.OriginalDuration - TimeSpan.FromSeconds(2);
            var sound = new MediaComposition(); sound.Clips.Add(clip);
            var soundFile = await folder.CreateFileAsync("video-clip-audio.wav", CreationCollisionOption.FailIfExists).AsTask(token);
            var soundProfile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            soundProfile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);
            var soundResult = await sound.RenderToFileAsync(soundFile, MediaTrimmingPreference.Precise, soundProfile).AsTask(token);
            if (soundResult != TranscodeFailureReason.None) throw new InvalidOperationException("Video audio-only render failed: " + soundResult);
            clipAudioOnlySuccess = true;
        }
        catch (Exception error) { clipAudioOnlySuccess = false; clipAudioError = error.GetType().Name + ": " + error.Message; }
    }
    reports.Add(new { withAudio, embeddedTracks = embedded, videoDurationSeconds = clip.OriginalDuration.TotalSeconds,
        acceptsMp4AsBackgroundAudio = backgroundAccepted, audioDurationSeconds = audioDuration, audioError,
        clipAudioOnlySuccess, clipAudioError });
    if ((embedded > 0) != withAudio) throw new InvalidDataException("Native embedded-track presence does not match synthetic fixture.");
}
var json = JsonSerializer.Serialize(new { reports, scope = "synthetic native metadata only; no model or personal media" }, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(root, "metadata-probe.json"), json);
Console.WriteLine(json);
return 0;
