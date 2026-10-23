using System.Text.Json;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace WinSight.MediaIndexing.Tests;

internal static class NativeCases
{
    internal static async Task Run(Run run, ActualApi api, string host)
    {
        await run.Case("native/audio32/index-with-fake-embeddings", async dir =>
        {
            string root = Path.Combine(dir, "input"); Directory.CreateDirectory(root); string path = Path.Combine(root, "s.wav"); Wave.Write(path, 32);
            var c = api.Client(host, Path.Combine(dir, "w")); var h = new ConsumerHarness(api); var captured = new List<short[]>();
            h.DuringEmbedding = (input, _) => { captured.Add(Wave.Read(ActualApi.Get<string>(input, "Value"))); return Task.CompletedTask; };
            try
            {
                await ConsumerHarness.Index(h.Consumer(api.Sampler(c)), h.Request(root)); var row = h.Row(path); ConsumerHarness.State(row, "done", true);
                var segments = ((System.Collections.IEnumerable)ActualApi.Get(row, "Segments")!).Cast<object>().ToArray();
                Check.That(segments.Length == 2 && ActualApi.Get<double>(segments[1], "Location") == 25 && ActualApi.Get<double>(segments[1], "LocationEnd") == 32, "Native source-time audio locators");
                Check.That(captured.Count == 2, "Native audio missing samples"); Wave.Markers(captured[0], 0); Wave.Markers(captured[1], 25);
            }
            finally { await ActualApi.Dispose(c); }
        });
        await run.Case("native/video-scene-order-and-soundtrack-index", async dir =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)); var ct = timeout.Token;
            string root = Path.Combine(dir, "input"); Directory.CreateDirectory(root); string wave = Path.Combine(dir, "a.wav"); Wave.Write(wave, 10);
            var composition = new MediaComposition(); var expectedHashes = new List<ulong>();
            for (int scene = 0; scene < 3; scene++)
            {
                const int width = 256, height = 144; byte[] rgb = new byte[width * height * 3];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int cx = x * 8 / width, cy = y * 8 / height;
                    bool white = scene switch { 0 => cx < 4, 1 => cy < 4, _ => (cx + cy) % 2 == 0 };
                    int p = (y * width + x) * 3; rgb[p] = rgb[p + 1] = rgb[p + 2] = white ? (byte)240 : (byte)16;
                }
                expectedHashes.Add(Oracle.Hash(width, height, rgb)); string image = Path.Combine(dir, "i" + scene + ".png"); Png.Write(image, width, height, rgb);
                var file = await StorageFile.GetFileFromPathAsync(image).AsTask(ct); composition.Clips.Add(await MediaClip.CreateFromImageFileAsync(file, TimeSpan.FromSeconds(scene == 2 ? 4 : 3)).AsTask(ct));
            }
            composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(wave).AsTask(ct)).AsTask(ct));
            var folder = await StorageFolder.GetFolderFromPathAsync(root).AsTask(ct); var video = await folder.CreateFileAsync("s.mp4", CreationCollisionOption.FailIfExists).AsTask(ct);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Qvga); profile.Video.Width = 256; profile.Video.Height = 144;
            Check.That(await composition.RenderToFileAsync(video, MediaTrimmingPreference.Precise, profile).AsTask(ct) == TranscodeFailureReason.None, "Synthetic native render failure");
            File.WriteAllText(Path.Combine(dir, "native-fixture.json"), JsonSerializer.Serialize(new { path = video.Path, expectedHashes, durationSeconds = 10, decoderParity = "Unknown: AAC/Native trim and FFmpeg frame precision are not equivalent" }));
            var client = api.Client(host, Path.Combine(dir, "w")); var h = new ConsumerHarness(api); var captured = new List<(string Op, string Path)>();
            h.DuringEmbedding = async (input, token) =>
            {
                string op = ActualApi.Get<string>(input, "Operation"), path = ActualApi.Get<string>(input, "Value"); string copy = Path.Combine(dir, "kept" + captured.Count + (op == "audio" ? ".wav" : ".png"));
                File.Copy(path, copy); captured.Add((op, copy)); SamplerCases.Blocked(video.Path); SamplerCases.BlockedDirectory(root); await Task.CompletedTask; token.ThrowIfCancellationRequested();
            };
            try
            {
                await ConsumerHarness.Index(h.Consumer(api.Sampler(client)), h.Request(root)); var row = h.Row(video.Path); ConsumerHarness.State(row, "done", true);
                var segments = ((System.Collections.IEnumerable)ActualApi.Get(row, "Segments")!).Cast<object>().ToArray(); var frames = segments.Where(s => ActualApi.Get<string>(s, "Modality") == "image").ToArray();
                Check.That(frames.Length == 3 && frames.Select(s => ActualApi.Get<double>(s, "Location")).SequenceEqual(new[] { .5, 3.5, 6.5 }), "Native dedup scene temporal ordering");
                Check.That(frames.Select(s => ActualApi.Get<double>(s, "LocationEnd")).Zip(new[] { 3.5, 6.5, 10d }).All(p => Math.Abs(p.First - p.Second) < .1), "Native scene run ends");
                int i = 0;
                foreach (var frame in captured.Where(c => c.Op == "image"))
                {
                    var bitmap = await StorageFile.GetFileFromPathAsync(frame.Path).AsTask(ct); using var stream = await bitmap.OpenReadAsync().AsTask(ct); var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask(ct);
                    var data = (await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask(ct)).DetachPixelData();
                    byte[] rgb = new byte[data.Length / 4 * 3]; for (int j = 0; j < data.Length / 4; j++) { rgb[j * 3] = data[j * 4]; rgb[j * 3 + 1] = data[j * 4 + 1]; rgb[j * 3 + 2] = data[j * 4 + 2]; }
                    ulong hash = Oracle.Hash((int)decoder.PixelWidth, (int)decoder.PixelHeight, rgb); Check.That(System.Numerics.BitOperations.PopCount(hash ^ expectedHashes[i++]) <= 4, "Wrong native scene content/order");
                }
                Check.That(captured.Any(c => c.Op == "audio"), "Video soundtrack omitted"); foreach (var audio in captured.Where(c => c.Op == "audio")) Wave.Markers(Wave.Read(audio.Path), 0);
            }
            finally { await ActualApi.Dispose(client); }
            using (File.Open(video.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        });
    }
}
