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
    internal bool TextLoaded => providers.Loaded("model_q4.onnx");
    internal bool VisionLoaded => providers.Loaded("vision_encoder_q4.onnx");
    internal bool AudioLoaded => providers.Loaded("audio_encoder_q4.onnx");
    internal string Preference { get; }
    internal bool ProviderDownloadsAllowed { get; }
    internal string RequestedProvider => FormatRequestedProvider(Preference);
    internal bool AutomaticProviders => Preference != "cpu";
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

    internal EmbeddingEngine(string modelRoot, string mediaPreprocessorVersion = "unbound", string? manifestPath = null, string preference = "auto", bool providerInstall = true)
    {
        models = new ModelManifest(modelRoot, manifestPath);
        try
        {
            Profile = models.Profile(mediaPreprocessorVersion);
            tokenizer = NativeTokenizer.Load(models.TokenizerJson);
            Preference = preference;
            ProviderDownloadsAllowed = providerInstall;
            providers = new ExecutionProviderManager(models, preference, providerInstall);
        }
        catch
        {
            models.Dispose();
            throw;
        }
    }

    internal EmbeddingEngine(string modelRoot, string mediaPreprocessorVersion, string? manifestPath, bool automaticProviders)
        : this(modelRoot, mediaPreprocessorVersion, manifestPath, automaticProviders ? "auto" : "cpu", automaticProviders)
    {
    }

    internal long[] Tokenize(string input, bool special = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return tokenizer.Encode(input, special);
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
        return Embed(ids, [], "text");
    }

    internal float[] EncodeVision(float[] pixels, long[] positions, int patchCount)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
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
