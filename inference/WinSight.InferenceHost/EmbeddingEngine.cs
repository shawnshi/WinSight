using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WinSight.InferenceHost;

// One owner, one request at a time. Media preprocessing remains outside this graph boundary.
internal sealed class EmbeddingEngine : IDisposable
{
    internal const int Dimension = 768;
    internal const int MaxTokens = 8192;
    private readonly ModelManifest models;
    private readonly NativeTokenizer tokenizer;
    private readonly ExecutionProviderManager providers;
    private bool disposed;
    internal string Profile { get; }
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
        if (ids.Any(id => id is 258880 or 258881 or 258884))
            throw new ArgumentException("Media placeholder tokens are not valid text-only input");

        if (models.IsRyzenAI)
        {
            return EmbedTextRyzenAI(ids);
        }
        if (models.IsIntelOpenVINO)
        {
            return EmbedTextIntelOpenVINO(ids);
        }

        return Embed(ids, [], "text");
    }

    private float[] EmbedTextRyzenAI(long[] ids)
    {
        const int seqLen = 256;
        var paddedIds = new long[seqLen];
        var mask = new long[seqLen];
        int count = Math.Min(ids.Length, seqLen);
        for (int i = 0; i < count; i++)
        {
            paddedIds[i] = ids[i];
            mask[i] = 1;
        }

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(paddedIds, new[] { 1, seqLen })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, seqLen }))
        };

        var result = providers.Run("model_text_s256.onnx", inputs, "sentence_embedding");
        return Normalize(result.Values);
    }

    private float[] EmbedTextIntelOpenVINO(long[] ids)
    {
        bool useQ32 = ids.Length <= 32 && File.Exists(models.Graph("model_text_q32.onnx"));
        int seqLen = useQ32 ? 32 : 256;
        string graphName = useQ32 ? "model_text_q32.onnx" : "model_text_d256.onnx";

        var paddedIds = new long[seqLen];
        var mask = new long[seqLen];
        int count = Math.Min(ids.Length, seqLen);
        for (int i = 0; i < count; i++)
        {
            paddedIds[i] = ids[i];
            mask[i] = 1;
        }

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(paddedIds, new[] { 1, seqLen })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, seqLen }))
        };

        var result = providers.Run(graphName, inputs, "sentence_embedding");
        return Normalize(result.Values);
    }

    private static float[] Normalize(float[] vec)
    {
        double sum = 0;
        for (int i = 0; i < vec.Length; i++) sum += (double)vec[i] * vec[i];
        float inv = (float)(1.0 / Math.Max(1e-12, Math.Sqrt(sum)));
        var res = new float[vec.Length];
        for (int i = 0; i < vec.Length; i++) res[i] = vec[i] * inv;
        return res;
    }

    internal float[] EncodeVision(float[] pixels, long[] positions, int patchCount)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (models.IsModernPlatform)
        {
            var staticPixels = new float[1260 * 768];
            Array.Copy(pixels, staticPixels, Math.Min(pixels.Length, staticPixels.Length));
            var staticPos = new long[1260 * 2];
            Array.Fill(staticPos, -1);
            Array.Copy(positions, staticPos, Math.Min(positions.Length, staticPos.Length));

            var npuInputs = new[] {
                NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(staticPixels, new[] { 1, 1260, 768 })),
                NamedOnnxValue.CreateFromTensor("pixel_position_ids", new DenseTensor<long>(staticPos, new[] { 1, 1260, 2 }))
            };
            var res = providers.Run("vision_encoder_static_140.onnx", npuInputs, "image_features");
            return res.Values;
        }

        if (patchCount is <= 0 or > 10080 || patchCount % 9 != 0 || pixels.Length != patchCount * 768 || positions.Length != patchCount * 2)
            throw new ArgumentException("Vision requires 1..10080 patches, divisible by 9, with 768 pixels and 2 positions per patch");
        if (!pixels.All(float.IsFinite) || positions.Any(position => position is < -1 or >= 10240))
            throw new ArgumentException("Invalid vision pixels or positions");
        var inputs = new[] {
            NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(pixels, new[] { 1, patchCount, 768 })),
            NamedOnnxValue.CreateFromTensor("pixel_position_ids", new DenseTensor<long>(positions, new[] { 1, patchCount, 2 }))
        };
        var validPatches = positions.Where((value, index) => index % 2 == 0 && value >= 0).Count();
        if (validPatches <= 0 || validPatches % 9 != 0) throw new ArgumentException("Invalid unpadded vision patch count");
        return Encode("vision_encoder_q4.onnx", inputs, validPatches / 9);
    }

    internal float[] EncodeAudio(float[] features, bool[] mask, int frameCount)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (models.IsModernPlatform)
        {
            var staticFeats = new float[999 * 128];
            Array.Copy(features, staticFeats, Math.Min(features.Length, staticFeats.Length));
            var staticMask = new bool[999];
            Array.Fill(staticMask, true);
            Array.Copy(mask, staticMask, Math.Min(mask.Length, staticMask.Length));

            var cpuInputs = new[] {
                NamedOnnxValue.CreateFromTensor("input_features", new DenseTensor<float>(staticFeats, new[] { 1, 999, 128 })),
                NamedOnnxValue.CreateFromTensor("input_features_mask", new DenseTensor<bool>(staticMask, new[] { 1, 999 }))
            };
            var res = providers.Run("audio_encoder_10s.onnx", cpuInputs, "audio_features");
            return res.Values;
        }

        if (frameCount is <= 0 or > 3000 || features.Length != frameCount * 128 || mask.Length != frameCount || !features.All(float.IsFinite))
            throw new ArgumentException("Audio requires 1..3000 frames with 128 features and one mask per frame");
        if (!mask.Any(value => value)) throw new ArgumentException("Audio mask is empty");
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
        var expectedRows = Enumerable.Range(0, (frameCount + 3) / 4).Count(i => mask[i * 4]);
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
        if (!rows.All(float.IsFinite)) throw new InvalidDataException("Non-finite media features");
        return rows;
    }

    internal float[] EmbedFeatures(float[] rows, string kind)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (kind is not ("image" or "video" or "audio")) throw new ArgumentException("kind must be image, video or audio");
        if (rows.Length == 0 || rows.Length % 512 != 0 || rows.Length / 512 > MaxTokens - 4 || !rows.All(float.IsFinite))
            throw new ArgumentException("Expected nonempty finite feature rows with 512 columns");

        if (models.IsModernPlatform)
        {
            if (kind is "image" or "video")
            {
                const int seqLen = 144;
                int nValid = Math.Clamp(rows.Length / 512, 1, 140);
                var seqIds = new List<long> { 2, 255999 };
                for (int i = 0; i < nValid; i++) seqIds.Add(258880);
                seqIds.Add(258882); seqIds.Add(1);
                while (seqIds.Count < seqLen) seqIds.Add(0);

                var mask = new long[seqLen];
                for (int i = 0; i < nValid + 4 && i < seqLen; i++) mask[i] = 1;

                var staticFeats = new float[140 * 512];
                Array.Copy(rows, staticFeats, Math.Min(rows.Length, staticFeats.Length));

                var inputs = new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(seqIds.ToArray(), new[] { 1, seqLen })),
                    NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, seqLen })),
                    NamedOnnxValue.CreateFromTensor("image_features", new DenseTensor<float>(staticFeats, new[] { 140, 512 }))
                };

                string graphName = models.IsRyzenAI ? "model_image_s144.onnx" : "model_image_140.onnx";
                var result = providers.Run(graphName, inputs, "sentence_embedding");
                return Normalize(result.Values);
            }
            else
            {
                const int seqLen = 256;
                var seqIds = new List<long> { 2, 258881 };
                while (seqIds.Count < seqLen) seqIds.Add(0);
                var mask = new long[seqLen];
                for (int i = 0; i < 2; i++) mask[i] = 1;

                var inputs = new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(seqIds.ToArray(), new[] { 1, seqLen })),
                    NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, seqLen })),
                    NamedOnnxValue.CreateFromTensor("audio_features", new DenseTensor<float>(rows, new[] { rows.Length / 512, 512 }))
                };

                var result = providers.Run("model_audio_10s.onnx", inputs, "sentence_embedding");
                return Normalize(result.Values);
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
        foreach (var (name, token) in new[] { ("image", 258880L), ("audio", 258881L), ("video", 258884L) })
            if (ids.Count(id => id == token) != (kind == name ? rows.Length / 512 : 0))
                throw new InvalidDataException("Media token/feature count mismatch");
        var inputs = new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, new[] { 1, ids.Length })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), new[] { 1, ids.Length }))
        };
        foreach (var name in new[] { "image", "video", "audio" })
        {
            var features = kind == name ? rows : Array.Empty<float>();
            inputs.Add(NamedOnnxValue.CreateFromTensor(name + "_features", new DenseTensor<float>(features, new[] { features.Length / 512, 512 })));
        }
        var result = providers.Run("model_q4.onnx", inputs.ToArray(), "sentence_embedding");
        if (!result.Shape.SequenceEqual(new[] { 1, Dimension })) throw new InvalidDataException("Expected [1,768] sentence_embedding");
        var vector = result.Values;
        var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
        if (!vector.All(float.IsFinite) || Math.Abs(norm - 1) > .001) throw new InvalidDataException("Embedding must be finite and L2-normalized");
        return vector;
    }

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
