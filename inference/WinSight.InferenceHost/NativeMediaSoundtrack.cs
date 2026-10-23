using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace WinSight.InferenceHost;

internal static partial class NativeMediaCommand
{
    internal static Task<MediaDecodeResult> RunSoundtrackRequestFileAsync(string requestPath,
        CancellationToken cancellationToken = default) =>
        RunDecodeRequestFileAsync(requestPath, GetDriveType, ExecuteSoundtrackAsync, cancellationToken);

    private static async Task<MediaDecodeResult> ExecuteSoundtrackAsync(MediaDecodeRequest request,
        Func<string, System.IO.DriveType> driveClassifier, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Kind != "audio") throw new ArgumentException("Soundtrack kind must be audio.");
        ValidateRequest(request);
        var sourcePath = ValidateLocalPath(request.InputPath, driveClassifier);
        var outputPath = ValidateLocalPath(request.OutputDirectory, driveClassifier);
        ValidateAncestors(outputPath, includeLeaf: false);
        if (Path.Exists(outputPath)) throw new IOException("Output directory must be new.");
        using var sourceLease = OpenPinnedFile(sourcePath, MaxInputBytes);
        var source = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(cancellationToken);
        var clip = await MediaClip.CreateFromFileAsync(source).AsTask(cancellationToken);
        var originalDuration = clip.OriginalDuration;
        RequireInspectDuration(originalDuration);
        var video = clip.GetVideoEncodingProperties();
        if (video.Width == 0 || video.Height == 0)
            throw new InvalidDataException("Soundtrack source must have valid video dimensions.");
        var audioTracks = clip.EmbeddedAudioTracks.Count;
        if (audioTracks == 0) throw new InvalidDataException("Video source has no embedded audio track.");
        if (audioTracks != 1) throw new NotSupportedException("Multiple embedded audio tracks require an explicit track-selection contract.");
        var startTime = TimeSpan.FromSeconds(request.StartSeconds);
        var end = request.EndSeconds!.Value;
        var endTime = TimeSpan.FromSeconds(end);
        if (endTime > originalDuration)
            throw new ArgumentException("Requested soundtrack interval exceeds the source duration.");
        clip.TrimTimeFromStart = startTime;
        clip.TrimTimeFromEnd = originalDuration - endTime;
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        try
        {
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            profile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);
            var folder = await CreateOutputAsync(outputPath, cancellationToken);
            var output = await folder.CreateFileAsync("audio.wav", CreationCollisionOption.FailIfExists).AsTask(cancellationToken);
            var failure = await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile).AsTask(cancellationToken);
            if (failure != TranscodeFailureReason.None)
                throw new InvalidOperationException($"Native soundtrack render failed: {failure}");
            cancellationToken.ThrowIfCancellationRequested();
            ValidateWave(output.Path, end - request.StartSeconds);
            return new MediaDecodeResult(1, source.Path, "audio", originalDuration.TotalSeconds,
                [new MediaDecodeArtifact("audio.wav", "audio", request.StartSeconds, end, 16000, 1, 16)]);
        }
        finally
        {
            composition.Clips.Clear();
        }
    }
}
