using System.Numerics;

namespace WinSight.Infrastructure.Media;

internal static class NativeMediaSamplingPolicy
{
    internal const int SampleRate = 16000;
    internal const int MaxFrames = 1200;
    internal const int MaxEdge = 1024;
    internal const int ImageBudget = 70;
    internal readonly record struct AudioWindow(long Start, long End)
    {
        internal double StartSeconds => Start / (double)SampleRate;
        internal double EndSeconds => End / (double)SampleRate;
    }

    internal static IEnumerable<AudioWindow> AudioWindows(double duration)
    {
        ValidateDuration(duration);
        var total = checked((long)Math.Floor(duration * SampleRate));
        const long window = 30L * SampleRate, hop = 25L * SampleRate, minimum = 2L * SampleRate;
        long start = 0;
        var fullWindow = false;
        while (total - start >= window)
        {
            yield return new(start, start + window);
            fullWindow = true;
            start = checked(start + hop);
        }
        var remaining = total - start;
        if (remaining - (fullWindow ? window - hop : 0) >= minimum)
            yield return new(start, total);
    }

    internal static IEnumerable<double> FrameTimes(double duration)
    {
        ValidateDuration(duration);
        var first = Math.Min(0.5, duration / 2);
        var step = Math.Max(3, duration / MaxFrames);
        for (var index = 0; index < MaxFrames; index++)
        {
            var time = first + index * step;
            if (time >= duration) yield break;
            yield return time;
        }
    }

    internal static bool Duplicate(ulong previousKept, ulong current) => BitOperations.PopCount(previousKept ^ current) <= 4;
    internal static bool Audible(double rms) => rms > 0.003;

    internal static void ValidateDuration(double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0 || duration > TimeSpan.MaxValue.TotalSeconds)
            throw new InvalidDataException("Media duration must be finite and positive.");
    }

    internal static ulong AverageHash(ReadOnlySpan<byte> pixels, int width, int height, int channels)
    {
        if (width < 1 || height < 1 || channels is not (3 or 4) || pixels.Length != checked(width * height * channels))
            throw new ArgumentException("Hash requires packed RGB or RGBA pixels.");
        Span<int> cells = stackalloc int[64];
        var total = 0;
        for (var cy = 0; cy < 8; cy++)
        for (var cx = 0; cx < 8; cx++)
        {
            var x0 = cx * width / 8;
            var x1 = Math.Max(x0 + 1, (cx + 1) * width / 8);
            var y0 = cy * height / 8;
            var y1 = Math.Max(y0 + 1, (cy + 1) * height / 8);
            long sum = 0;
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var offset = (y * width + x) * channels;
                sum += (299 * pixels[offset] + 587 * pixels[offset + 1] + 114 * pixels[offset + 2]) / 1000;
            }
            var mean = (int)(sum / ((x1 - x0) * (y1 - y0)));
            cells[cy * 8 + cx] = mean;
            total += mean;
        }
        var average = total / 64;
        ulong hash = 0;
        for (var i = 0; i < cells.Length; i++) if (cells[i] > average) hash |= 1UL << i;
        return hash;
    }
}
