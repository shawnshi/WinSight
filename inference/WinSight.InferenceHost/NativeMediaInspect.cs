using System.Text.Json;
using Windows.Media.Editing;
using Windows.Storage;

namespace WinSight.InferenceHost;

internal static partial class NativeMediaCommand
{
    internal static async Task<MediaInspectResult> RunInspectRequestFileAsync(string requestPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ValidateLocalPath(requestPath, GetDriveType);
        using var input = OpenPinnedFile(path, MaxRequestBytes);
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The media inspect request must be an object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (property.Name is not ("inputPath" or "kind") || !fields.TryAdd(property.Name, property.Value))
                throw new JsonException($"Unknown or duplicate media inspect request member: {property.Name}");
        }
        foreach (var name in new[] { "inputPath", "kind" })
            if (!fields.ContainsKey(name)) throw new JsonException($"Missing media inspect request member: {name}");
        string RequiredString(string name) => fields[name].ValueKind == JsonValueKind.String
            ? fields[name].GetString()! : throw new JsonException($"{name} must be a string.");
        var request = new MediaInspectRequest(RequiredString("inputPath"), RequiredString("kind"));
        if (request.Kind is not ("audio" or "video")) throw new ArgumentException("kind must be audio or video.");
        var sourcePath = ValidateLocalPath(request.InputPath, GetDriveType);
        using var sourceLease = OpenPinnedFile(sourcePath, MaxInputBytes);
        var source = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(cancellationToken);
        if (request.Kind == "audio")
        {
            var track = await BackgroundAudioTrack.CreateFromFileAsync(source).AsTask(cancellationToken);
            var duration = RequireInspectDuration(track.OriginalDuration);
            cancellationToken.ThrowIfCancellationRequested();
            return new MediaInspectResult(1, source.Path, request.Kind, duration, true, null, 0, 0);
        }
        var clip = await MediaClip.CreateFromFileAsync(source).AsTask(cancellationToken);
        var videoDuration = RequireInspectDuration(clip.OriginalDuration);
        var video = clip.GetVideoEncodingProperties();
        if (video.Width == 0 || video.Height == 0 || video.Width > int.MaxValue || video.Height > int.MaxValue)
            throw new InvalidDataException("Native video source has no usable video dimensions.");
        var audioTracks = clip.EmbeddedAudioTracks.Count;
        cancellationToken.ThrowIfCancellationRequested();
        return new MediaInspectResult(1, source.Path, request.Kind, videoDuration, audioTracks > 0, audioTracks,
            checked((int)video.Width), checked((int)video.Height));
    }

    private static double RequireInspectDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || !double.IsFinite(duration.TotalSeconds))
            throw new InvalidDataException("Native media source has no usable timeline duration.");
        return duration.TotalSeconds;
    }
}
