namespace WinSight.InferenceHost;

// Numerical contract: transformers.js 4.3.1 Gemma4/Gemma3n extractors and utils/audio.js,
// using the daa72c5 processor_config.json (no dither, preemphasis, normalization or input scale).
internal static class MediaAudio
{
    private const int Window = 320, Hop = 160, FftSize = 512, Bins = 257, Mels = 128;
    private static readonly double[] Hann = Enumerable.Range(0, Window)
        .Select(i => 0.5 - 0.5 * Math.Cos(i * (2 * Math.PI / Window))).ToArray();
    private static readonly float[] Filters = CreateFilters();

    internal static AudioInputs Extract(double[] waveform)
    {
        int paddedSamples = checked((waveform.Length + 127) / 128 * 128);
        // Reference uses unfold(321,160) to select frames, but a 320-sample FFT window.
        int frames = (paddedSamples + 160 - 321) / Hop + 1;
        var features = new float[checked(frames * Mels)];
        var mask = new bool[frames];
        var real = new double[FftSize];
        var imaginary = new double[FftSize];
        var magnitude = new float[Bins];
        for (int frame = 0; frame < frames; ++frame)
        {
            mask[frame] = frame * Hop + 160 < waveform.Length;
            if (!mask[frame]) continue;
            Array.Clear(real);
            Array.Clear(imaginary);
            for (int j = 0; j < Window; ++j)
            {
                int sample = frame * Hop + j - 160;
                if (sample >= 0 && sample < waveform.Length)
                    real[j] = waveform[sample] * Hann[j];
            }
            Transform(real, imaginary);
            for (int bin = 0; bin < Bins; ++bin)
            {
                // The reference stores power in float32 before taking the square root in float32.
                float power = (float)(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]);
                magnitude[bin] = (float)Math.Sqrt(power);
            }
            for (int mel = 0; mel < Mels; ++mel)
            {
                float sum = 0;
                for (int bin = 0; bin < Bins; ++bin)
                    sum += Filters[mel * Bins + bin] * magnitude[bin];
                float floored = (float)(sum + 0.001);
                features[frame * Mels + mel] = (float)Math.Log(floored);
            }
        }
        return new AudioInputs(features, mask, frames);
    }

    private static float[] CreateFilters()
    {
        var edges = new double[Mels + 2];
        double melMax = 2595 * Math.Log10(1 + 8000.0 / 700);
        double step = melMax / (Mels + 1);
        for (int i = 0; i < edges.Length; ++i)
            edges[i] = 700 * (Math.Pow(10, step * i / 2595) - 1);
        var filters = new float[Mels * Bins];
        for (int mel = 0; mel < Mels; ++mel)
            for (int bin = 0; bin < Bins; ++bin)
            {
                double hz = bin * (8000.0 / (Bins - 1));
                double down = (hz - edges[mel]) / (edges[mel + 1] - edges[mel]);
                double up = (edges[mel + 2] - hz) / (edges[mel + 2] - edges[mel + 1]);
                filters[mel * Bins + bin] = (float)Math.Max(0, Math.Min(down, up));
            }
        return filters;
    }

    private static void Transform(double[] real, double[] imaginary)
    {
        for (int i = 1, j = 0; i < FftSize; ++i)
        {
            int bit = FftSize >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (real[i], real[j]) = (real[j], real[i]);
        }
        for (int length = 2; length <= FftSize; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            double rootReal = Math.Cos(angle), rootImaginary = Math.Sin(angle);
            for (int offset = 0; offset < FftSize; offset += length)
            {
                double wr = 1, wi = 0;
                for (int j = 0; j < length / 2; ++j)
                {
                    int even = offset + j, odd = even + length / 2;
                    double tr = wr * real[odd] - wi * imaginary[odd];
                    double ti = wr * imaginary[odd] + wi * real[odd];
                    real[odd] = real[even] - tr;
                    imaginary[odd] = imaginary[even] - ti;
                    real[even] += tr;
                    imaginary[even] += ti;
                    (wr, wi) = (wr * rootReal - wi * rootImaginary, wr * rootImaginary + wi * rootReal);
                }
            }
        }
    }
}
