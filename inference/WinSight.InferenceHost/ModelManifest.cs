using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinSight.InferenceHost;

internal enum PlatformKind
{
    LegacyQ4,
    RyzenAI,
    IntelOpenVINO
}

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
    internal PlatformKind Platform { get; }
    internal string PlatformDir { get; } = "";
    internal string OnnxDir { get; } = "";
    internal string ConfigPath { get; } = "";
    internal string VaiConfigFile { get; } = "";
    internal string VaipCacheDir { get; } = "";
    internal bool IsRyzenAI => Platform == PlatformKind.RyzenAI;
    internal bool IsIntelOpenVINO => Platform == PlatformKind.IntelOpenVINO;
    internal bool IsModernPlatform => IsRyzenAI || IsIntelOpenVINO;

    private static string DetectCpuVendor()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("VendorIdentifier") as string ?? "").ToUpperInvariant();
        }
        catch
        {
            return "";
        }
    }

    internal ModelManifest(string root, string? manifestPath = null)
    {
        Root = Path.GetFullPath(root);

        // 1. AMD 候选目录探测 (models/RyzenAI 或 Root 本身)
        var ryzenDir = Directory.Exists(Path.Combine(Root, "RyzenAI"))
            ? Path.Combine(Root, "RyzenAI")
            : Root;
        bool hasRyzenModels = (Directory.Exists(Path.Combine(ryzenDir, "onnx")) && File.Exists(Path.Combine(ryzenDir, "onnx", "model_text_s256.onnx")))
            || File.Exists(Path.Combine(ryzenDir, "model_text_s256.onnx"))
            || File.Exists(Path.Combine(ryzenDir, "vai_ep_config.json"));

        // 2. Intel 候选目录探测 (models/IntelOpenVINO/ov_bundle 或 models/IntelOpenVINO 或 Root 本身)
        var intelDir = Directory.Exists(Path.Combine(Root, "IntelOpenVINO", "ov_bundle"))
            ? Path.Combine(Root, "IntelOpenVINO", "ov_bundle")
            : Directory.Exists(Path.Combine(Root, "IntelOpenVINO"))
            ? Path.Combine(Root, "IntelOpenVINO")
            : Root;
        bool hasIntelModels = (Directory.Exists(Path.Combine(intelDir, "onnx")) && (File.Exists(Path.Combine(intelDir, "onnx", "model_text_d256.onnx")) || File.Exists(Path.Combine(intelDir, "onnx", "model_text_q32.onnx"))))
            || File.Exists(Path.Combine(intelDir, "model_text_d256.onnx"))
            || File.Exists(Path.Combine(intelDir, "model_text_q32.onnx"));

        // 3. 硬件自适应决策
        var vendor = DetectCpuVendor();
        bool isAmdCpu = vendor.Contains("AMD");
        bool isIntelCpu = vendor.Contains("INTEL");

        if (isAmdCpu && hasRyzenModels)
        {
            Platform = PlatformKind.RyzenAI;
            PlatformDir = ryzenDir;
        }
        else if (isIntelCpu && hasIntelModels)
        {
            Platform = PlatformKind.IntelOpenVINO;
            PlatformDir = intelDir;
        }
        else if (hasRyzenModels && !hasIntelModels)
        {
            Platform = PlatformKind.RyzenAI;
            PlatformDir = ryzenDir;
        }
        else if (hasIntelModels && !hasRyzenModels)
        {
            Platform = PlatformKind.IntelOpenVINO;
            PlatformDir = intelDir;
        }
        else if (hasRyzenModels)
        {
            Platform = PlatformKind.RyzenAI;
            PlatformDir = ryzenDir;
        }
        else if (hasIntelModels)
        {
            Platform = PlatformKind.IntelOpenVINO;
            PlatformDir = intelDir;
        }
        else
        {
            Platform = PlatformKind.LegacyQ4;
            PlatformDir = Root;
        }

        // 4. 解析选中平台的目录结构
        if (IsModernPlatform)
        {
            OnnxDir = Directory.Exists(Path.Combine(PlatformDir, "onnx"))
                ? Path.Combine(PlatformDir, "onnx")
                : Directory.Exists(Path.Combine(Root, "onnx"))
                ? Path.Combine(Root, "onnx")
                : PlatformDir;

            ConfigPath = File.Exists(Path.Combine(PlatformDir, "config.json"))
                ? Path.Combine(PlatformDir, "config.json")
                : Path.Combine(Root, "config.json");

            var tokPath = File.Exists(Path.Combine(PlatformDir, "tokenizer.json"))
                ? Path.Combine(PlatformDir, "tokenizer.json")
                : Path.Combine(Root, "tokenizer.json");

            if (!File.Exists(tokPath))
                throw new FileNotFoundException($"分词器配置文件未找到: '{tokPath}' (平台: {Platform}, 搜索目录: {PlatformDir})", tokPath);

            TokenizerJson = File.ReadAllBytes(tokPath);

            if (IsRyzenAI)
            {
                // Prefer the standard system ProgramData location (C:\ProgramData\RyzenAI\vaip_cache) which avoids spaces and driver parser issues
                const string systemProgramDataCache = @"C:\ProgramData\RyzenAI\vaip_cache";
                if (Directory.Exists(systemProgramDataCache) && File.Exists(Path.Combine(systemProgramDataCache, "eg2_vis_140", "eg2_vis_140.rai")))
                {
                    VaipCacheDir = systemProgramDataCache;
                    VaiConfigFile = File.Exists(Path.Combine(systemProgramDataCache, "vai_ep_config.json"))
                        ? Path.Combine(systemProgramDataCache, "vai_ep_config.json")
                        : (File.Exists(Path.Combine(PlatformDir, "vai_ep_config.json")) ? Path.Combine(PlatformDir, "vai_ep_config.json") : Path.Combine(Root, "vai_ep_config.json"));
                }
                else
                {
                    VaiConfigFile = Path.Combine(File.Exists(Path.Combine(PlatformDir, "vai_ep_config.json")) ? PlatformDir : Root, "vai_ep_config.json");
                    VaipCacheDir = Path.Combine(Directory.Exists(Path.Combine(PlatformDir, "vaip_cache")) ? PlatformDir : Root, "vaip_cache");
                }
            }
            return;
        }

        // 5. 回退到通用 Legacy Q4 模型清单校验
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
                        $"EmbeddingGemma-2 模型权重文件 '{asset.Path}' 未找到。请将完整的 ONNX 权重文件复制至 '{Root}' 目录（包含 RyzenAI/ 静态模型、IntelOpenVINO/ 静态模型或 onnx/model_q4.onnx 等）。", asset.Path);
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
        if (IsModernPlatform)
        {
            var p = Path.Combine(OnnxDir, name);
            if (File.Exists(p)) return p;

            p = Path.Combine(PlatformDir, name);
            if (File.Exists(p)) return p;

            p = Path.Combine(Root, "onnx", name);
            if (File.Exists(p)) return p;

            p = Path.Combine(Root, name);
            if (File.Exists(p)) return p;

            return Path.Combine(OnnxDir, name);
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
        if (IsIntelOpenVINO)
        {
            var intelDescriptor = new StringBuilder("winsight-embedding-profile-v3-intel\n")
                .Append(Repository).Append('@').Append(Revision).Append('\n')
                .Append("ort=").Append(Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetVersionString()).Append('\n')
                .Append("provider=heterogeneous-intel-directml-cpu;dtype=int8-fp32;dim=768;features=512\n")
                .Append("tokenizer=").Append(NativeTokenizer.RuntimeIdentity).Append('\n')
                .Append("text=q32-d256;image=s140;audio=10s\n")
                .Append("media=embedding-gemma2-placeholder-v1;preprocessor=").Append(mediaPreprocessorVersion).Append('\n');
            return Hash(Encoding.UTF8.GetBytes(intelDescriptor.ToString()));
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
