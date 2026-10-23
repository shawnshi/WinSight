using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WinSight.InferenceHost;

internal static class TextProbe
{
    private const string Revision = "daa72c51243991dfcaf9f9137d2c573d8f7790c0";
    private sealed record Sample(string Name, [property: JsonPropertyName("input_ids")] long[] InputIds);
    private sealed record Fixture(string Repository, string Revision,
        [property: JsonPropertyName("tokenizer_sha256")] string TokenizerSha256, Sample[] Items);

    internal static object Run(string modelRoot, string fixturePath, string reportDirectory)
    {
        if (new FileInfo(fixturePath).Length > 1024 * 1024)
            throw new ArgumentException("P0 fixture exceeds 1 MiB");
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(fixturePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("Missing token fixture");
        if (fixture.Repository != "onnx-community/embeddinggemma-2-ONNX" || fixture.Revision != Revision)
            throw new ArgumentException("Token fixture is not from the pinned P0 model");
        if (fixture.Items is not { Length: > 0 and <= 8 })
            throw new ArgumentException("P0 requires 1 to 8 synthetic token samples");
        foreach (var sample in fixture.Items)
        {
            if (string.IsNullOrWhiteSpace(sample.Name) || sample.InputIds is not { Length: > 0 and <= 8192 }
                || sample.InputIds.Any(token => token < 0 || token >= 262144))
                throw new ArgumentException("Invalid sample name, token count or vocabulary ID");
        }
        var tokenizerPath = Path.Combine(modelRoot, "tokenizer.json");
        var tokenizerHash = Digest(tokenizerPath);
        if (!string.Equals(tokenizerHash, fixture.TokenizerSha256, StringComparison.Ordinal))
            throw new ArgumentException("Fixture tokenizer does not match model tokenizer");

        var reportRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reportDirectory));
        Directory.CreateDirectory(reportRoot);
        using var options = ModelProbe.CpuOptions();
        // EnableProfiling immediately applies the current prefix to native ORT: set the prefix first.
        options.ProfileOutputPathPrefix = Path.Combine(reportRoot, "text-profile");
        options.EnableProfiling = true;
        var graphPath = Path.Combine(modelRoot, "onnx", "model_q4.onnx");
        using var session = new InferenceSession(graphPath, options);
        var vectors = new List<object>();
        foreach (var sample in fixture.Items)
        {
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(sample.InputIds, new[] { 1, sample.InputIds.Length })),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, sample.InputIds.Length).ToArray(), new[] { 1, sample.InputIds.Length })),
                NamedOnnxValue.CreateFromTensor("image_features", new DenseTensor<float>(Array.Empty<float>(), new[] { 0, 512 })),
                NamedOnnxValue.CreateFromTensor("video_features", new DenseTensor<float>(Array.Empty<float>(), new[] { 0, 512 })),
                NamedOnnxValue.CreateFromTensor("audio_features", new DenseTensor<float>(Array.Empty<float>(), new[] { 0, 512 }))
            };
            var clock = Stopwatch.StartNew();
            using var outputs = session.Run(inputs, new[] { "sentence_embedding" });
            var tensor = outputs.Single().AsTensor<float>();
            if (!tensor.Dimensions.SequenceEqual(new[] { 1, 768 }))
                throw new InvalidDataException("Unexpected sentence_embedding shape");
            var vector = tensor.ToArray();
            if (!vector.All(float.IsFinite)) throw new InvalidDataException("Non-finite embedding");
            var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
            if (Math.Abs(norm - 1) > 0.001)
                throw new InvalidDataException($"Graph output is not normalized: {norm}");
            vectors.Add(new { name = sample.Name, tokens = sample.InputIds.Length,
                inferenceMs = clock.Elapsed.TotalMilliseconds, dimension = vector.Length, norm, vector });
        }
        var profilePath = Path.GetFullPath(session.EndProfiling());
        if (!string.Equals(Path.GetDirectoryName(profilePath), reportRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Profiling output escaped the requested report directory");
        using var profile = JsonDocument.Parse(File.ReadAllText(profilePath));
        var providers = profile.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("args", out var args) && args.TryGetProperty("provider", out _))
            .Select(item => item.GetProperty("args").GetProperty("provider").GetString())
            .Distinct().ToArray();
        if (providers.Length == 0 || providers.Any(provider => provider != "CPUExecutionProvider"))
            throw new InvalidDataException("CPU graph-execution evidence is missing or unexpected");
        return new
        {
            phase = "P0", ok = true, mode = "synthetic-text-token-inference",
            revision = Revision, tokenizerSha256 = tokenizerHash, graphSha256 = Digest(graphPath),
            externalDataSha256 = Digest(graphPath + "_data"), runtime = OrtEnv.Instance().GetVersionString(),
            nodeExecutionProviders = providers, profilePath, samples = vectors,
            productionTokenizerAccepted = false, productionConnected = false,
            audioInferenceVerified = false, visionInferenceVerified = false
        };
    }

    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
