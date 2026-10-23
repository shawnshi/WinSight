namespace WinSight.InferenceHost;

internal sealed record VisionInputs(float[] Pixels, long[] Positions, int PatchCount);
internal sealed record AudioInputs(float[] Features, bool[] Mask, int FrameCount);

internal static class MediaPreprocessor
{
    // The version binds both tensor layout and the pinned reference's numerical rules.
    internal const string ProfileVersion = "embeddinggemma2-daa72c5-transformers4.3.1-v1";

    internal static VisionInputs LoadImage(string path, int softTokens = 280) =>
        MediaImage.Load(path, softTokens);

    /// <summary>One 16 kHz mono PCM16 RIFF/WAVE window, 161..480000 samples. Longer media must be windowed by the caller.</summary>
    internal static AudioInputs LoadAudio(string path) => MediaAudio.Extract(MediaWave.Read(path));
}
