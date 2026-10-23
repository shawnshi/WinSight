using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace WinSight.MediaIndexing.Tests;

internal static class GapPolicyCases
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private delegate ulong HashPixels(ReadOnlySpan<byte> pixels, int width, int height, int channels);
    private static object Invoke(Type type, string method, params object[] args) => ActualApi.Unwrap(() => type.GetMethod(method, Static)!.Invoke(null, args))!;

    internal static async Task Run(Run run, ActualApi api)
    {
        var policy = api.Type("Media.NativeMediaSamplingPolicy");
        await run.Case("gap/policy/exact-double-rms", dir =>
        {
            double[] values = [Math.BitDecrement(.003), .003, Math.BitIncrement(.003)];
            bool[] actual = values.Select(v => (bool)Invoke(policy, "Audible", v)).ToArray();
            Check.That(actual.SequenceEqual(new[] { false, false, true }), "RMS must be strictly greater than .003, including adjacent doubles");
            File.WriteAllText(Path.Combine(dir, "threshold.json"), JsonSerializer.Serialize(new { values, actual }));
            return Task.CompletedTask;
        });
        await run.Case("gap/policy/lazy-5001-audio-plan", dir =>
        {
            // A huge valid duration makes eager materialization impractical; only 5001 policy entries are requested.
            double duration = TimeSpan.MaxValue.TotalSeconds;
            var windows = ((IEnumerable)Invoke(policy, "AudioWindows", duration)).Cast<object>();
            var first = windows.Take(5001).Select(w => (ActualApi.Get<double>(w, "StartSeconds"), ActualApi.Get<double>(w, "EndSeconds"))).ToArray();
            Check.That(first.Length == 5001, "Default5000 needs a valid next unattempted window, not fabricated EOF");
            for (int i = 0; i < first.Length; i++) Check.That(first[i] == (i * 25d, i * 25d + 30), "Lazy plan source-time window " + i);
            File.WriteAllText(Path.Combine(dir, "lazy-plan.json"), JsonSerializer.Serialize(new { duration, enumerated = first.Length, first = first[0].Item1, lastStart = first[^1].Item1, lastEnd = first[^1].Item2, physicalDecodeProcesses = 0 }));
            return Task.CompletedTask;
        });
        await run.Case("gap/policy/frame-cap-and-finite-duration-bounds", async dir =>
        {
            var plans = new List<object>();
            foreach (double duration in new[] { 1 / 16000d, .2, 3d, 3600d, 10000d, TimeSpan.MaxValue.TotalSeconds })
            {
                Invoke(policy, "ValidateDuration", duration);
                double[] actual = ((IEnumerable)Invoke(policy, "FrameTimes", duration)).Cast<double>().ToArray();
                Check.That(actual.SequenceEqual(Oracle.Video(duration)) && actual.Length <= 1200, "Frame grid/cap differs from frozen policy");
                Check.That(actual.All(t => double.IsFinite(t) && t >= 0 && t < duration), "Invalid frame locator");
                if (duration >= 3600) Check.That(actual.Length == 1200, "Long video must hit exactly1200 policy points");
                plans.Add(new { duration, count = actual.Length, first = actual[0], last = actual[^1] });
            }
            foreach (double duration in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity, Math.BitIncrement(TimeSpan.MaxValue.TotalSeconds) })
            {
                await Check.Throws(() => { Invoke(policy, "ValidateDuration", duration); return Task.CompletedTask; }, e => e.GetType() == typeof(InvalidDataException));
                foreach (string method in new[] { "AudioWindows", "FrameTimes" })
                    await Check.Throws(() => { var iterator = ((IEnumerable)Invoke(policy, method, duration)).GetEnumerator(); try { iterator.MoveNext(); } finally { (iterator as IDisposable)?.Dispose(); } return Task.CompletedTask; }, e => e.GetType() == typeof(InvalidDataException));
            }
            File.WriteAllText(Path.Combine(dir, "plans.json"), JsonSerializer.Serialize(plans));
        });
        await run.Case("gap/policy/small-RGB-integer-means-bit-order-and-PNG", async dir =>
        {
            var hash = policy.GetMethod("AverageHash", Static)!.CreateDelegate<HashPixels>();
            var vectors = new List<(int W, int H, byte[] Rgb, ulong? Expected)>
            {
                (1, 1, [255, 0, 0], 0), (1, 1, [0, 255, 0], 0), (1, 1, [0, 0, 255], 0),
                (2, 1, [255, 0, 0, 0, 255, 0], 0xf0f0f0f0f0f0f0f0UL),
                (2, 1, [0, 255, 0, 255, 0, 0], 0x0f0f0f0f0f0f0f0fUL),
                (2, 1, [1, 1, 1, 2, 2, 2], 0xf0f0f0f0f0f0f0f0UL),
                // Both integer grayscale values are zero; floating-point luma would invent a difference.
                (2, 1, [0, 1, 0, 0, 0, 1], 0),
                (8, 8, Png.Scene(8, 8, 0), 0x00000000ffffffffUL)
            };
            foreach (var size in new[] { (2, 1), (3, 2), (8, 8), (17, 19) })
            {
                byte[] rgb = Enumerable.Range(0, size.Item1 * size.Item2 * 3).Select(i => (byte)((i * 73 + i / 3 * 19 + 11) % 256)).ToArray();
                vectors.Add((size.Item1, size.Item2, rgb, null));
            }
            var receipts = new List<object>(); int ordinal = 0;
            foreach (var v in vectors)
            {
                ulong expected = v.Expected ?? Oracle.Hash(v.W, v.H, v.Rgb);
                Check.That(Oracle.Hash(v.W, v.H, v.Rgb) == expected, "Independent RGB oracle disagrees with known vector");
                Check.That(hash(v.Rgb, v.W, v.H, 3) == expected, "Production packed RGB integer hash");
                byte[] rgba = v.Rgb.Chunk(3).SelectMany((p, i) => p.Concat(new[] { (byte)(i * 17 % 256) })).ToArray();
                Check.That(hash(rgba, v.W, v.H, 4) == expected, "RGBA alpha must not affect RGB hash");
                for (int filter = 0; filter <= 4; filter++)
                {
                    string file = Path.Combine(dir, $"rgb-{ordinal}-{filter}.png"); Png.Write(file, v.W, v.H, v.Rgb, filter);
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Check.That((ulong)Invoke(api.Type("Media.NativeMediaFrameHash"), "Read", stream, CancellationToken.None) == expected, "Actual PNG reader differs from independent RGB oracle");
                }
                receipts.Add(new { width = v.W, height = v.H, expected = expected.ToString("X16"), channels = new[] { 3, 4 }, filters = 5 }); ordinal++;
            }
            foreach (var bad in new[] { (0, 1, 3, 0), (1, 0, 3, 0), (1, 1, 1, 1), (2, 1, 3, 3) })
                await Check.Throws(() => { hash(new byte[bad.Item4], bad.Item1, bad.Item2, bad.Item3); return Task.CompletedTask; }, e => e.GetType() == typeof(ArgumentException));
            File.WriteAllText(Path.Combine(dir, "hash-vectors.json"), JsonSerializer.Serialize(receipts));
        });
    }
}
