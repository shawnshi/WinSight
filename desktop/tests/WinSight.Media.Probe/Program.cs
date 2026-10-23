using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
{
    Console.Error.WriteLine("Supply one explicit, task-owned absolute fixture directory.");
    return 2;
}
var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
uint identityLength = 0;
var identityResult = Identity.GetCurrentPackageFullName(ref identityLength, IntPtr.Zero);
var packaged = identityResult is 0 or 122;
var results = new List<object>();
var failures = 0;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
var token = timeout.Token;
var sourceWav = Path.Combine(root, "synthetic-source.wav");
using (var output = new BinaryWriter(File.Create(sourceWav)))
{
    const int samples = 16000;
    output.Write("RIFF"u8); output.Write(36 + samples * 2); output.Write("WAVEfmt "u8);
    output.Write(16); output.Write((short)1); output.Write((short)1); output.Write(16000);
    output.Write(32000); output.Write((short)2); output.Write((short)16);
    output.Write("data"u8); output.Write(samples * 2);
    for (var i = 0; i < samples; i++) output.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 16000) * 1000));
}
await Probe("MediaTranscoder-WAV-to-mono16kPCM", async () =>
{
    var input = await StorageFile.GetFileFromPathAsync(sourceWav).AsTask(token);
    var destination = await (await StorageFolder.GetFolderFromPathAsync(root).AsTask(token))
        .CreateFileAsync("transcoded.wav", CreationCollisionOption.ReplaceExisting).AsTask(token);
    var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
    profile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);
    var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false };
    var prepared = await transcoder.PrepareFileTranscodeAsync(input, destination, profile).AsTask(token);
    if (!prepared.CanTranscode) throw new NotSupportedException("Prepare rejected profile: " + prepared.FailureReason);
    await prepared.TranscodeAsync().AsTask(token);
    return new { bytes = new FileInfo(destination.Path).Length, requestedSampleRate = 16000, requestedChannels = 1 };
});
var video = Path.Combine(root, "synthetic-solid-color.mp4");
await Probe("MediaComposition-synthetic-color-MP4", async () =>
{
    var composition = new MediaComposition();
    composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 32, 96, 128), TimeSpan.FromMilliseconds(500)));
    var destination = await (await StorageFolder.GetFolderFromPathAsync(root).AsTask(token))
        .CreateFileAsync(Path.GetFileName(video), CreationCollisionOption.ReplaceExisting).AsTask(token);
    var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Qvga);
    var outcome = await composition.RenderToFileAsync(destination, MediaTrimmingPreference.Precise, profile).AsTask(token);
    if (outcome != TranscodeFailureReason.None) throw new NotSupportedException("Render failed: " + outcome);
    return new { bytes = new FileInfo(video).Length, durationMilliseconds = 500 };
});
await Probe("MediaComposition-GetThumbnail-synthetic-video", async () =>
{
    if (!File.Exists(video) || new FileInfo(video).Length == 0) throw new InvalidOperationException("Synthetic video prerequisite unavailable.");
    var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(video).AsTask(token)).AsTask(token);
    var composition = new MediaComposition();
    composition.Clips.Add(clip);
    using var image = await composition.GetThumbnailAsync(TimeSpan.Zero, 64, 48, VideoFramePrecision.NearestFrame).AsTask(token);
    if (image.Size is 0 or > 2 * 1024 * 1024) throw new InvalidDataException("Thumbnail size is invalid or exceeds the probe bound.");
    using var reader = new DataReader(image.GetInputStreamAt(0));
    var loaded = await reader.LoadAsync((uint)image.Size).AsTask(token);
    if (loaded != image.Size) throw new InvalidDataException("Incomplete thumbnail read.");
    var bytes = new byte[checked((int)loaded)]; reader.ReadBytes(bytes);
    await File.WriteAllBytesAsync(Path.Combine(root, "synthetic-thumbnail.bin"), bytes, token);
    return new { bytes = bytes.Length, contentType = image.ContentType, width = 64, height = 48 };
});
var report = new
{
    probe = "unpackaged-native-media", processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
    hasPackageIdentity = packaged, packageIdentityQueryStatus = identityResult,
    ocr = new { attempted = false, supportedByDocumentedDesktopContractWithoutIdentity = false,
        reason = "Official Windows.Media.Ocr desktop support requires package identity; no personal image or OCR request was used." },
    failures, observations = results,
    limits = "Synthetic fixture capabilities only. Not arbitrary-codec support, hard native cancellation, numerical/model parity, screenshots or release approval."
};
var text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(root, "media-probe.json"), text);
Console.WriteLine(text);
return 0;

async Task Probe(string name, Func<Task<object>> operation)
{
    try { results.Add(new { name, available = true, observed = await operation() }); }
    catch (Exception error)
    {
        failures++;
        results.Add(new { name, available = false, error = error.GetType().Name, hresult = $"0x{error.HResult:X8}", detail = error.Message });
    }
}
internal static class Identity
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
