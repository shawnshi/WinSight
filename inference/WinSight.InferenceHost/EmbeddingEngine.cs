using System.Buffers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WinSight.InferenceHost;

// One owner, one request at a time. Media preprocessing remains outside this graph boundary.
internal sealed class EmbeddingEngine : IDisposable
{
    internal const int Dimension = 768;
    internal const int MaxTokens = 8192;
    private static readonly DenseTensor<float> EmptyFeatureTensor = new(Array.Empty<float>(), new[] { 0, 512 });

    [ThreadStatic] private static long[]? tlsPaddedIds32;
    [ThreadStatic] private static long[]? tlsMask32;
    [ThreadStatic] private static long[]? tlsPaddedIds256;
    [ThreadStatic] private static long[]? tlsMask256;
    [ThreadStatic] private static float[]? tlsStaticVisionPixels;
    [ThreadStatic] private static long[]? tlsStaticVisionPos;
    [ThreadStatic] private static float[]? tlsStaticAudioFeats;
    [ThreadStatic] private static bool[]? tlsStaticAudioMask;
    [ThreadStatic] private static float[]? tlsStaticImageFeats;
    [ThreadStatic] private static long[]? tlsImageSeqIds;
    [ThreadStatic] private static long[]? tlsImageMask;

    private readonly ModelManifest models;
    private readonly NativeTokenizer tokenizer;
    private readonly ExecutionProviderManager providers;
    private readonly bool hasIntelQ32Graph;
    private bool disposed;
    internal string Profile { get; }
    internal bool IsModernPlatform => models.IsModernPlatform;
    internal string QuantizationProfile => models.IsRyzenAI ? "fp32-static" : models.IsIntelOpenVINO ? "int8-fp32" : "q4";
    internal bool TextLoaded => models.IsRyzenAI
        ? providers.Loaded("model_text_s256.onnx")
        : models.IsIntelOpenVINO
        ? (providers.Loaded("model_text_d256.onnx") || providers.Loaded("model_text_q32.onnx"))
        : providers.Loaded("model_q4.onnx");
    internal bool VisionLoaded => models.IsModernPlatform ? providers.Loaded("vision_encoder_static_140.onnx") : providers.Loaded("vision_encoder_q4.onnx");
    internal bool AudioLoaded => models.IsModernPlatform ? providers.Loaded("audio_encoder_10s.onnx") : providers.Loaded("audio_encoder_q4.onnx");
    internal string Preference { get; }
    internal bool ProviderDownloadsAllowed { get; }
    internal string RequestedProvider => FormatRequestedProvider(Preference);
    internal long ProviderEpoch => providers.Epoch;
    internal object ProviderStatus => providers.Status;
    internal string? PerformanceProfile => providers.PerformanceIdentity(Profile);
    internal string? PerformanceForBatch(long expectedEpoch) => providers.PerformanceIdentity(Profile, expectedEpoch);

    internal static string FormatRequestedProvider(string preference) => preference switch
    {
        "cpu" => "CPUExecutionProvider",
        "auto" => "device-auto-with-real-model-gates",
        "discrete-gpu" => "device-discrete-gpu-with-real-model-gates",
        "npu" => "device-npu-with-real-model-gates",
        "gpu" => "device-gpu-with-real-model-gates",
        _ => throw new ArgumentException($"Unknown device preference: {preference}")
    };

    internal EmbeddingEngine(string modelRoot, string mediaPreprocessorVersion = "unbound", string? manifestPath = null, string preference = "auto")
    {
        models = new ModelManifest(modelRoot, manifestPath);
        try
        {
            Profile = models.Profile(mediaPreprocessorVersion);
            tokenizer = NativeTokenizer.Load(models.TokenizerJson);
            Preference = preference;
            ProviderDownloadsAllowed = false;
            hasIntelQ32Graph = models.IsIntelOpenVINO && File.Exists(models.Graph("model_text_q32.onnx"));
            providers = new ExecutionProviderManager(models, preference);
        }
        catch
        {
            models.Dispose();
            throw;
        }
    }

    internal EmbeddingEngine(string modelRoot, string mediaPreprocessorVersion, string? manifestPath, bool automaticProviders)
        : this(modelRoot, mediaPreprocessorVersion, manifestPath, automaticProviders ? "auto" : "cpu")
    {
    }

    internal float[] EmbedText(string input)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Text must not be empty");
        if (input.Length > 65536 || System.Text.Encoding.UTF8.GetByteCount(input) > 65536)
            throw new ArgumentException("Text exceeds 64 KiB UTF8");
        var ids = tokenizer.Encode(input);
        if (ids.Length is <= 0 or > MaxTokens)
            throw new ArgumentException($"Token count must be 1..{MaxTokens}; truncation is not implicit");
        if (HasMediaPlaceholderToken(ids))
            throw new ArgumentException("Media placeholder tokens are not valid text-only input");

        if (models.IsRyzenAI)
        {
            return EmbedTextStaticWindow(ids, "model_text_s256.onnx", graph32: null);
        }
        if (models.IsIntelOpenVINO)
        {
            return EmbedTextStaticWindow(ids, "model_text_d256.onnx", hasIntelQ32Graph ? "model_text_q32.onnx" : null);
        }

        return Embed(ids, [], "text");
    }

    private float[] EmbedTextStaticWindow(long[] ids, string graph256, string? graph32)
    {
        if (ids.Length <= 32 && graph32 is not null)
        {
            const int seqLen = 32;
            var paddedIds = tlsPaddedIds32 ??= new long[seqLen];
            var mask = tlsMask32 ??= new long[seqLen];
            int count = ids.Length;
            Array.Copy(ids, paddedIds, count);
            Array.Clear(paddedIds, count, seqLen - count);
            Array.Fill(mask, 1L, 0, count);
            Array.Clear(mask, count, seqLen - count);
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(paddedIds, new[] { 1, seqLen })),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, seqLen }))
            };
            var result = providers.Run(graph32, inputs, "sentence_embedding");
            return NormalizeInPlace(result.Values);
        }

        const int windowSeqLen = 256;
        var winIds = tlsPaddedIds256 ??= new long[windowSeqLen];
        var winMask = tlsMask256 ??= new long[windowSeqLen];

        if (ids.Length <= windowSeqLen)
        {
            int count = ids.Length;
            Array.Copy(ids, winIds, count);
            Array.Clear(winIds, count, windowSeqLen - count);
            Array.Fill(winMask, 1L, 0, count);
            Array.Clear(winMask, count, windowSeqLen - count);
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(winIds, new[] { 1, windowSeqLen })),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(winMask, new[] { 1, windowSeqLen }))
            };
            var result = providers.Run(graph256, inputs, "sentence_embedding");
            return NormalizeInPlace(result.Values);
        }

        const int prefixKeep = 16;
        const int bodyCap = windowSeqLen - prefixKeep;
        const int stride = 192;
        var accum = new float[Dimension];
        var winInputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(winIds, new[] { 1, windowSeqLen })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(winMask, new[] { 1, windowSeqLen }))
        };

        // Window 0: ids[0..256]
        Array.Copy(ids, 0, winIds, 0, windowSeqLen);
        Array.Fill(winMask, 1L, 0, windowSeqLen);
        var firstVec = NormalizeInPlace(providers.Run(graph256, winInputs, "sentence_embedding").Values);
        for (int i = 0; i < Dimension; i++) accum[i] += firstVec[i] * windowSeqLen;

        // Overlapping sliding windows preserving prompt prefix so tail chunks retain full semantic context.
        for (int offset = stride; offset < ids.Length; offset += stride)
        {
            Array.Copy(ids, 0, winIds, 0, prefixKeep);
            int take = Math.Min(ids.Length - offset, bodyCap);
            Array.Copy(ids, offset, winIds, prefixKeep, take);
            int validCount = prefixKeep + take;
            if (validCount < windowSeqLen && ids[^1] == 1 && winIds[validCount - 1] != 1)
            {
                winIds[validCount] = 1;
                validCount++;
            }
            if (validCount < windowSeqLen) Array.Clear(winIds, validCount, windowSeqLen - validCount);
            Array.Fill(winMask, 1L, 0, validCount);
            if (validCount < windowSeqLen) Array.Clear(winMask, validCount, windowSeqLen - validCount);

            var winVec = NormalizeInPlace(providers.Run(graph256, winInputs, "sentence_embedding").Values);
            float weight = validCount;
            for (int i = 0; i < Dimension; i++) accum[i] += winVec[i] * weight;

            if (ids.Length - offset <= bodyCap) break;
        }

        return NormalizeInPlace(accum);
    }

    private static float[] NormalizeInPlace(float[] vec)
    {
        double sum = 0;
        for (int i = 0; i < vec.Length; i++) sum += (double)vec[i] * vec[i];
        float inv = (float)(1.0 / Math.Max(1e-12, Math.Sqrt(sum)));
        for (int i = 0; i < vec.Length; i++) vec[i] *= inv;
        return vec;
    }

    internal static int CountValidPatches(ReadOnlySpan<long> positions)
    {
        int count = 0;
        for (int i = 0; i < positions.Length; i += 2)
        {
            if (positions[i] >= 0) count++;
        }
        return count;
    }

    private static bool HasMediaPlaceholderToken(ReadOnlySpan<long> ids)
    {
        foreach (var id in ids)
        {
            if (id is 258880 or 258881 or 258884) return true;
        }
        return false;
    }

    private static bool AllFinite(ReadOnlySpan<float> values)
    {
        foreach (var v in values)
        {
            if (!float.IsFinite(v)) return false;
        }
        return true;
    }

    private static bool ValidPositionsRange(ReadOnlySpan<long> positions)
    {
        foreach (var pos in positions)
        {
            if (pos is < -1 or >= 10240) return false;
        }
        return true;
    }

    private static int CountToken(ReadOnlySpan<long> ids, long target)
    {
        int count = 0;
        foreach (var id in ids)
        {
            if (id == target) count++;
        }
        return count;
    }

    internal float[] EncodeVision(float[] pixels, long[] positions, int patchCount)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (models.IsModernPlatform)
        {
            var validCount = CountValidPatches(positions);
            if (validCount <= 0 || validCount % 9 != 0 || validCount > 1260)
                throw new ArgumentException("Static vision encoder requires 1..1260 valid patches divisible by 9");
            if (pixels.Length < validCount * 768 || positions.Length < validCount * 2)
                throw new ArgumentException("Vision pixel/position buffers are smaller than valid patch count");

            var staticPixels = tlsStaticVisionPixels ??= new float[1260 * 768];
            int copyPixels = Math.Min(pixels.Length, staticPixels.Length);
            Array.Copy(pixels, staticPixels, copyPixels);
            if (copyPixels < staticPixels.Length) Array.Clear(staticPixels, copyPixels, staticPixels.Length - copyPixels);

            var staticPos = tlsStaticVisionPos ??= new long[1260 * 2];
            Array.Fill(staticPos, -1L);
            Array.Copy(positions, staticPos, Math.Min(positions.Length, staticPos.Length));

            var npuInputs = new[] {
                NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(staticPixels, new[] { 1, 1260, 768 })),
                NamedOnnxValue.CreateFromTensor("pixel_position_ids", new DenseTensor<long>(staticPos, new[] { 1, 1260, 2 }))
            };
            var res = providers.Run("vision_encoder_static_140.onnx", npuInputs, "image_features");
            int tokenCount = validCount / 9;
            int expectedFloats = tokenCount * 512;
            if (res.Values.Length < expectedFloats)
                throw new InvalidDataException("Static vision encoder returned fewer features than valid tokens");
            return res.Values.Length == expectedFloats ? res.Values : res.Values[..expectedFloats];
        }

        if (patchCount is <= 0 or > 10080 || patchCount % 9 != 0 || pixels.Length != patchCount * 768 || positions.Length != patchCount * 2)
            throw new ArgumentException("Vision requires 1..10080 patches, divisible by 9, with 768 pixels and 2 positions per patch");
        if (!AllFinite(pixels) || !ValidPositionsRange(positions))
            throw new ArgumentException("Invalid vision pixels or positions");
        var inputs = new[] {
            NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(pixels, new[] { 1, patchCount, 768 })),
            NamedOnnxValue.CreateFromTensor("pixel_position_ids", new DenseTensor<long>(positions, new[] { 1, patchCount, 2 }))
        };
        var validPatches = CountValidPatches(positions);
        if (validPatches <= 0 || validPatches % 9 != 0) throw new ArgumentException("Invalid unpadded vision patch count");
        return Encode("vision_encoder_q4.onnx", inputs, validPatches / 9);
    }

    internal float[] EncodeAudio(float[] features, bool[] mask, int frameCount)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (models.IsModernPlatform)
        {
            var staticFeats = tlsStaticAudioFeats ??= new float[999 * 128];
            int copyFeats = Math.Min(features.Length, staticFeats.Length);
            Array.Copy(features, staticFeats, copyFeats);
            if (copyFeats < staticFeats.Length) Array.Clear(staticFeats, copyFeats, staticFeats.Length - copyFeats);

            var staticMask = tlsStaticAudioMask ??= new bool[999];
            Array.Fill(staticMask, true);
            Array.Copy(mask, staticMask, Math.Min(mask.Length, staticMask.Length));

            var cpuInputs = new[] {
                NamedOnnxValue.CreateFromTensor("input_features", new DenseTensor<float>(staticFeats, new[] { 1, 999, 128 })),
                NamedOnnxValue.CreateFromTensor("input_features_mask", new DenseTensor<bool>(staticMask, new[] { 1, 999 }))
            };
            var res = providers.Run("audio_encoder_10s.onnx", cpuInputs, "audio_features");
            return res.Values;
        }

        if (frameCount is <= 0 or > 3000 || features.Length != frameCount * 128 || mask.Length != frameCount || !AllFinite(features))
            throw new ArgumentException("Audio requires 1..3000 frames with 128 features and one mask per frame");
        bool anyValid = false;
        foreach (var valid in mask)
        {
            if (valid) { anyValid = true; break; }
        }
        if (!anyValid) throw new ArgumentException("Audio mask is empty");
        var foundPadding = false;
        foreach (var valid in mask)
        {
            if (!valid) foundPadding = true;
            else if (foundPadding) throw new ArgumentException("Audio mask must use contiguous right padding");
        }
        var inputs = new[] {
            NamedOnnxValue.CreateFromTensor("input_features", new DenseTensor<float>(features, new[] { 1, frameCount, 128 })),
            NamedOnnxValue.CreateFromTensor("input_features_mask", new DenseTensor<bool>(mask, new[] { 1, frameCount }))
        };
        int expectedRows = 0;
        int strideBlocks = (frameCount + 3) / 4;
        for (int i = 0; i < strideBlocks; i++)
        {
            if (mask[i * 4]) expectedRows++;
        }
        var rows = Encode("audio_encoder_q4.onnx", inputs, expectedRows);
        if (rows.Length != expectedRows * 512) throw new InvalidDataException("Audio encoder row count does not match stride-4 mask");
        return rows;
    }

    private float[] Encode(string graph, NamedOnnxValue[] inputs, int expectedRows)
    {
        var result = providers.Run(graph, inputs, expectedRows: expectedRows);
        if (result.Shape.Length != 2 || result.Shape[1] != 512 || result.Shape[0] <= 0)
            throw new InvalidDataException("Media encoder did not return [rows,512]");
        var rows = result.Values;
        if (!AllFinite(rows)) throw new InvalidDataException("Non-finite media features");
        return rows;
    }

    internal float[] EmbedFeatures(float[] rows, string kind)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (kind is not ("image" or "video" or "audio")) throw new ArgumentException("kind must be image, video or audio");
        if (rows.Length == 0 || rows.Length % 512 != 0 || rows.Length / 512 > MaxTokens - 4 || !AllFinite(rows))
            throw new ArgumentException("Expected nonempty finite feature rows with 512 columns");

        if (models.IsModernPlatform)
        {
            if (kind is "image" or "video")
            {
                const int seqLen = 144;
                int nValid = Math.Clamp(rows.Length / 512, 1, 140);
                var seqIds = tlsImageSeqIds ??= new long[seqLen];
                seqIds[0] = 2;
                seqIds[1] = 255999;
                for (int i = 0; i < nValid; i++) seqIds[2 + i] = 258880;
                seqIds[2 + nValid] = 258882;
                seqIds[3 + nValid] = 1;
                if (nValid + 4 < seqLen) Array.Clear(seqIds, nValid + 4, seqLen - (nValid + 4));

                var mask = tlsImageMask ??= new long[seqLen];
                int maskCount = Math.Min(nValid + 4, seqLen);
                Array.Fill(mask, 1L, 0, maskCount);
                if (maskCount < seqLen) Array.Clear(mask, maskCount, seqLen - maskCount);

                var staticFeats = tlsStaticImageFeats ??= new float[140 * 512];
                int copyFeats = Math.Min(rows.Length, staticFeats.Length);
                Array.Copy(rows, staticFeats, copyFeats);
                if (copyFeats < staticFeats.Length) Array.Clear(staticFeats, copyFeats, staticFeats.Length - copyFeats);

                var inputs = new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(seqIds, new[] { 1, seqLen })),
                    NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, seqLen })),
                    NamedOnnxValue.CreateFromTensor("image_features", new DenseTensor<float>(staticFeats, new[] { 140, 512 }))
                };

                string graphName = models.IsRyzenAI ? "model_image_s144.onnx" : "model_image_140.onnx";
                var result = providers.Run(graphName, inputs, "sentence_embedding");
                return NormalizeInPlace(result.Values);
            }
            else
            {
                const int seqLen = 256;
                var seqIds = tlsPaddedIds256 ??= new long[seqLen];
                seqIds[0] = 2;
                seqIds[1] = 258881;
                Array.Clear(seqIds, 2, seqLen - 2);
                var mask = tlsMask256 ??= new long[seqLen];
                mask[0] = 1;
                mask[1] = 1;
                Array.Clear(mask, 2, seqLen - 2);

                var inputs = new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(seqIds, new[] { 1, seqLen })),
                    NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, seqLen })),
                    NamedOnnxValue.CreateFromTensor("audio_features", new DenseTensor<float>(rows, new[] { rows.Length / 512, 512 }))
                };

                var result = providers.Run("model_audio_10s.onnx", inputs, "sentence_embedding");
                return NormalizeInPlace(result.Values);
            }
        }

        var marker = kind switch { "image" => "<|image|>", "video" => "<|video|>", _ => "<|audio|>" };
        // Pinned processor expands media-only input with begin/end markers, then applies tokenizer BOS/EOS.
        var prompt = (kind == "audio" ? "<|audio>" : "<|image>")
            + string.Concat(Enumerable.Repeat(marker, rows.Length / 512))
            + (kind == "audio" ? "<audio|>" : "<image|>");
        return Embed(tokenizer.Encode(prompt), rows, kind);
    }

    private float[] Embed(long[] ids, float[] rows, string kind)
    {
        if (ids.Length is <= 0 or > MaxTokens) throw new ArgumentException($"Token count must be 1..{MaxTokens}; truncation is not implicit");
        int expectedCount = rows.Length / 512;
        if (CountToken(ids, 258880L) != (kind == "image" ? expectedCount : 0)
            || CountToken(ids, 258881L) != (kind == "audio" ? expectedCount : 0)
            || CountToken(ids, 258884L) != (kind == "video" ? expectedCount : 0))
            throw new InvalidDataException("Media token/feature count mismatch");

        var maskArr = ArrayPool<long>.Shared.Rent(ids.Length);
        try
        {
            Array.Fill(maskArr, 1L, 0, ids.Length);
            var inputs = new NamedOnnxValue[] {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, new[] { 1, ids.Length })),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(maskArr.AsMemory(0, ids.Length), new[] { 1, ids.Length })),
                NamedOnnxValue.CreateFromTensor("image_features", kind == "image" ? new DenseTensor<float>(rows, new[] { expectedCount, 512 }) : EmptyFeatureTensor),
                NamedOnnxValue.CreateFromTensor("video_features", kind == "video" ? new DenseTensor<float>(rows, new[] { expectedCount, 512 }) : EmptyFeatureTensor),
                NamedOnnxValue.CreateFromTensor("audio_features", kind == "audio" ? new DenseTensor<float>(rows, new[] { expectedCount, 512 }) : EmptyFeatureTensor)
            };
            var result = providers.Run("model_q4.onnx", inputs, "sentence_embedding");
            if (!result.Shape.SequenceEqual(new[] { 1, Dimension })) throw new InvalidDataException("Expected [1,768] sentence_embedding");
            var vector = result.Values;
            double normSq = 0;
            for (int i = 0; i < vector.Length; i++)
            {
                if (!float.IsFinite(vector[i])) throw new InvalidDataException("Embedding must be finite and L2-normalized");
                normSq += (double)vector[i] * vector[i];
            }
            if (Math.Abs(Math.Sqrt(normSq) - 1) > .001) throw new InvalidDataException("Embedding must be finite and L2-normalized");
            return vector;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(maskArr);
        }
    }

    internal int TrimIdle(TimeSpan idleThreshold) => providers.TrimIdleSessions(idleThreshold);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // The background owner must finish before native sessions or model read locks are released.
        try { providers.Dispose(); }
        finally
        {
            try { tokenizer.Dispose(); }
            finally { models.Dispose(); }
        }
    }
}
