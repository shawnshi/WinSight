using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using WinSight.NativeMedia.Tests;

if (args is not ["--artifact-root", var root] || !Path.IsPathFullyQualified(root))
{
    Console.Error.WriteLine("Usage: --artifact-root NEW_ABSOLUTE_TASK_DIRECTORY");
    return 2;
}
await SyntheticMedia.CreateAsync(root);
var observations = new List<object>();
foreach (var name in new[] { "landscape.mp4", "portrait.mp4" })
{
    var source = await StorageFile.GetFileFromPathAsync(Path.Combine(root, name));
    var properties = await source.Properties.GetVideoPropertiesAsync();
    var composition = new MediaComposition();
    composition.Clips.Add(await MediaClip.CreateFromFileAsync(source));
    using var image = await composition.GetThumbnailAsync(TimeSpan.FromSeconds(1.25), (int)properties.Width, (int)properties.Height, VideoFramePrecision.NearestFrame);
    var decoder = await BitmapDecoder.CreateAsync(image);
    var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
    var offset = checked((int)(((decoder.PixelHeight / 2) * decoder.PixelWidth + decoder.PixelWidth / 2) * 4));
    var expected = SyntheticMedia.Colors[1];
    if (Math.Abs(pixels[offset] - expected.B) >= 35 || Math.Abs(pixels[offset + 1] - expected.G) >= 35 || Math.Abs(pixels[offset + 2] - expected.R) >= 35)
        throw new InvalidDataException("Synthetic video second-scene color marker not physically present.");
    observations.Add(new { name, sourceWidth = properties.Width, sourceHeight = properties.Height, durationSeconds = properties.Duration.TotalSeconds,
        thumbnailWidth = decoder.PixelWidth, thumbnailHeight = decoder.PixelHeight, startSeconds = 1.25,
        centerBgra = pixels.Skip(offset).Take(4).ToArray(), sha256 = SyntheticMedia.Hash(source.Path) });
}
var wavePath = Path.Combine(root, "levels-4s.wav");
using (var reader = new BinaryReader(File.OpenRead(wavePath)))
{
    if (reader.BaseStream.Length != 768044) throw new InvalidDataException("Synthetic source WAV byte count.");
    for (var second = 0; second < 4; second++)
    {
        reader.BaseStream.Position = 44 + (second * 48000L + 24000) * 4;
        if (reader.ReadInt16() != SyntheticMedia.Levels[second] || reader.ReadInt16() != SyntheticMedia.Levels[second])
            throw new InvalidDataException("Synthetic source WAV second marker mismatch.");
    }
}
observations.Add(new { name = "levels-4s.wav", sourceRate = 48000, sourceChannels = 2, sourceBits = 16, frames = 192000,
    durationSeconds = 4, secondLevels = SyntheticMedia.Levels, sha256 = SyntheticMedia.Hash(wavePath) });
var report = JsonSerializer.Serialize(new { fixtureValidationOnly = true, observations }, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(root, "fixture-validation.json"), report);
Console.WriteLine(report);
return 0;
