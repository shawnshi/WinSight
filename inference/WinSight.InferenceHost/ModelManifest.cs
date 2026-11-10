using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinSight.InferenceHost;

internal sealed class ModelManifest : IDisposable
{
    internal const string Repository = "onnx-community/embeddinggemma-2-ONNX";
    internal const string Revision = "daa72c51243991dfcaf9f9137d2c573d8f7790c0";
    private sealed record Asset(string Path, long Size, string Sha256);
    private sealed record Manifest(string Repository, string Revision, Asset[] Files);
    private readonly Manifest? manifest;
    private readonly List<FileStream> readLocks = [];
    internal string Root { get; }
    internal byte[] TokenizerJson { get; }
    internal bool IsRyzenAI { get; }
    internal string RyzenAIDir { get; } = "";
    internal string VaiConfigFile { get; } = "";
    internal string VaipCacheDir { get; } = "";

    internal ModelManifest(string root, string? manifestPath = null)
    {
        Root = Path.GetFullPath(root);

        // 优先探测 AMD Ryzen AI 专用静态图与硬件微码方案
        var ryzenDir = Path.Combine(Root, "RyzenAI");
        var textInRyzen = Path.Combine(ryzenDir, "model_text_s256.onnx");
        var textInOnnx = Path.Combine(Root, "onnx", "model_text_s256.onnx");

        if (File.Exists(textInRyzen) || File.Exists(textInOnnx))
        {
            IsRyzenAI = true;
            RyzenAIDir = File.Exists(textInRyzen) ? ryzenDir : Path.Combine(Root, "onnx");
            VaiConfigFile = Path.Combine(File.Exists(Path.Combine(ryzenDir, "vai_ep_config.json")) ? ryzenDir : Root, "vai_ep_config.json");
            VaipCacheDir = Path.Combine(Directory.Exists(Path.Combine(ryzenDir, "vaip_cache")) ? ryzenDir : Root, "vaip_cache");

            var requiredGraphs = new[]
            {
                "model_text_s256.onnx",
                "vision_encoder_static_140.onnx",
                "audio_encoder_10s.onnx",
                "model_image_s144.onnx",
                "model_audio_10s.onnx"
            };

            foreach (var graph in requiredGraphs)
            {
                var p = Path.Combine(RyzenAIDir, graph);
                if (!File.Exists(p))
                {
                    throw new FileNotFoundException($"Ryzen AI 静态计算图文件未找到: '{graph}'。请检查 '{RyzenAIDir}' 目录。", graph);
                }
            }

            var tokPath = Path.Combine(Root, "tokenizer.json");
            if (!File.Exists(tokPath))
                throw new FileNotFoundException($"分词器配置未找到: '{tokPath}'", tokPath);

            TokenizerJson = File.ReadAllBytes(tokPath);
            return;
        }

        // 回退到通用 Legacy Q4 模型清单校验
        using var stream = typeof(ModelManifest).Assembly.GetManifestResourceStream("WinSight.InferenceHost.model-manifest.json")
            ?? throw new InvalidDataException("Missing embedded model manifest");
        manifest = JsonSerializer.Deserialize<Manifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid embedded model manifest");
        if (manifest.Repository != Repository || manifest.Revision != Revision || manifest.Files.Length != 11)
            throw new InvalidDataException("Manifest is not the fixed, audio-inclusive model set");
        if (manifestPath is not null)
        {
            using var packagedFile = File.OpenRead(manifestPath);
            if (packagedFile.Length > 65536) throw new InvalidDataException("Model manifest exceeds 64 KiB");
            var packaged = JsonSerializer.Deserialize<Manifest>(packagedFile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Invalid packaged model manifest");
            if (packaged.Repository != Repository || packaged.Revision != Revision || packaged.Files is null
                || !packaged.Files.SequenceEqual(manifest.Files))
                throw new InvalidDataException("Packaged manifest differs from the pinned audio-inclusive asset set");
        }
        try
        {
            foreach (var asset in manifest.Files)
            {
                var path = Path.Combine(Root, asset.Path);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"EmbeddingGemma-2 模型权重文件 '{asset.Path}' 未找到。请将完整的 ONNX 权重文件复制至 '{Root}' 目录（包含 RyzenAI/ 静态模型或 onnx/model_q4.onnx 等）。", asset.Path);
                }
                // Windows read-sharing locks prevent replacement between hashing and lazy graph creation.
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                readLocks.Add(file);
                if (file.Length != asset.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(file)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Pinned model asset mismatch: {asset.Path} (expected {asset.Size} bytes, sha256 {asset.Sha256})");
            }
            TokenizerJson = File.ReadAllBytes(Path.Combine(Root, "tokenizer.json"));
            if (Hash(TokenizerJson) != manifest.Files.Single(asset => asset.Path == "tokenizer.json").Sha256)
                throw new InvalidDataException("Tokenizer changed during validation");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal string Graph(string name)
    {
        if (IsRyzenAI)
        {
            var p = Path.Combine(RyzenAIDir, name);
            if (File.Exists(p)) return p;
        }
        return Path.Combine(Root, "onnx", name);
    }

    internal string Profile(string mediaPreprocessorVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPreprocessorVersion);
        if (IsRyzenAI)
        {
            var ryzenDescriptor = new StringBuilder("winsight-embedding-profile-v3-ryzenai\n")
                .Append(Repository).Append('@').Append(Revision).Append('\n')
                .Append("ort=").Append(Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetVersionString()).Append('\n')
                .Append("provider=heterogeneous-npu-gpu-cpu;dtype=fp32-static;dim=768;features=512\n")
                .Append("tokenizer=").Append(NativeTokenizer.RuntimeIdentity).Append('\n')
                .Append("text=s256;image=s144;audio=10s\n")
                .Append("media=embedding-gemma2-placeholder-v1;preprocessor=").Append(mediaPreprocessorVersion).Append('\n');
            return Hash(Encoding.UTF8.GetBytes(ryzenDescriptor.ToString()));
        }

        // Deliberately excludes filesystem locations, timestamps and host identity.
        var descriptor = new StringBuilder("winsight-embedding-profile-v2\n")
            .Append(Repository).Append('@').Append(Revision).Append('\n')
            .Append("ort=").Append(Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetVersionString()).Append('\n')
            .Append("provider=qualified-per-graph-with-same-model-CPU-fallback;dtype=q4;dim=768;features=512\n")
            .Append("tokenizer=").Append(NativeTokenizer.RuntimeIdentity).Append('\n')
            .Append("text=raw-prompt-v1;maxTokens=8192\n")
            .Append("media=embedding-gemma2-placeholder-v1;preprocessor=").Append(mediaPreprocessorVersion).Append('\n');
        foreach (var asset in manifest!.Files.OrderBy(asset => asset.Path, StringComparer.Ordinal))
            descriptor.Append(asset.Path).Append('=').Append(asset.Sha256).Append('\n');
        return Hash(Encoding.UTF8.GetBytes(descriptor.ToString()));
    }

    public void Dispose()
    {
        foreach (var file in readLocks) file.Dispose();
        readLocks.Clear();
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
