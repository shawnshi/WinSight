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
            try
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

                // Hold FileShare.Read locks across tokenizer and lazy-loaded platform graphs to prevent TOCTOU tampering.
                var tokStream = new FileStream(tokPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                readLocks.Add(tokStream);
                if (tokStream.Length <= 0)
                    throw new InvalidDataException($"Pinned tokenizer file is empty: '{tokPath}'.");
                TokenizerJson = ReadExactStream(tokStream);

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

                string[] modernGraphs = IsRyzenAI
                    ? ["model_text_s256.onnx", "model_image_s144.onnx", "model_audio_10s.onnx", "vision_encoder_static_140.onnx", "audio_encoder_10s.onnx"]
                    : ["model_text_d256.onnx", "model_text_q32.onnx", "model_image_140.onnx", "model_audio_10s.onnx", "vision_encoder_static_140.onnx", "audio_encoder_10s.onnx"];
                var lockedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(tokPath) };
                foreach (var graphName in modernGraphs)
                {
                    var graphPath = Graph(graphName);
                    foreach (var candidate in new[] { graphPath, graphPath + "_data", graphPath + ".data" })
                    {
                        var full = Path.GetFullPath(candidate);
                        if (!File.Exists(full) || !lockedPaths.Add(full)) continue;
                        var graphLock = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                        readLocks.Add(graphLock);
                        if (graphLock.Length <= 0)
                            throw new InvalidDataException($"Pinned modern platform model asset is empty: '{full}'.");
                    }
                }
                return;
            }
            catch
            {
                Dispose();
                throw;
            }
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
            var opened = new List<(Asset Asset, string FullPath, FileStream Stream)>(manifest.Files.Length);
            foreach (var asset in manifest.Files)
            {
                var path = Path.GetFullPath(Path.Combine(Root, asset.Path));
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"EmbeddingGemma-2 模型权重文件 '{asset.Path}' 未找到。请将完整的 ONNX 权重文件复制至 '{Root}' 目录（包含 RyzenAI/ 静态模型、IntelOpenVINO/ 静态模型或 onnx/model_q4.onnx 等）。", asset.Path);
                }
                // Windows read-sharing locks prevent replacement between verification and lazy graph creation.
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                readLocks.Add(file);
                if (file.Length != asset.Size)
                    throw new InvalidDataException($"Pinned model asset mismatch: {asset.Path} (expected {asset.Size} bytes, sha256 {asset.Sha256})");
                opened.Add((asset, path, file));
            }

            var fastStamp = ComputeFastStamp(opened);
            var stampPath = ResolveFastStampPath();
            bool fastStampMatched = false;
            try
            {
                if (File.Exists(stampPath))
                {
                    fastStampMatched = string.Equals(File.ReadAllText(stampPath).Trim(), fastStamp, StringComparison.Ordinal);
                }
            }
            catch { fastStampMatched = false; }

            if (!fastStampMatched)
            {
                foreach (var (asset, _, file) in opened)
                {
                    file.Position = 0;
                    if (!string.Equals(Convert.ToHexString(SHA256.HashData(file)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Pinned model asset mismatch: {asset.Path} (expected {asset.Size} bytes, sha256 {asset.Sha256})");
                }
                TryWriteFastStamp(stampPath, fastStamp);
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

    private static byte[] ReadExactStream(FileStream stream)
    {
        var bytes = new byte[checked((int)stream.Length)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new EndOfStreamException("Unexpected EOF reading locked model asset.");
            offset += read;
        }
        return bytes;
    }

    private static string ResolveFastStampPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Path.GetTempPath();
        return Path.Combine(localAppData, "WinSight", "cache", "model-manifest-verified-v1.stamp");
    }

    private static string ComputeFastStamp(List<(Asset Asset, string FullPath, FileStream Stream)> opened)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sampleBuf = new byte[16384];
        foreach (var (asset, fullPath, file) in opened.OrderBy(item => item.Asset.Path, StringComparer.Ordinal))
        {
            var ticks = File.GetLastWriteTimeUtc(fullPath).Ticks;
            hasher.AppendData(Encoding.UTF8.GetBytes($"{fullPath}\n{asset.Size}\n{asset.Sha256}\n{ticks}\n"));
            file.Position = 0;
            int headRead = file.Read(sampleBuf, 0, (int)Math.Min(sampleBuf.Length, file.Length));
            hasher.AppendData(sampleBuf.AsSpan(0, headRead));
            if (file.Length > sampleBuf.Length)
            {
                file.Position = file.Length - sampleBuf.Length;
                int tailRead = file.Read(sampleBuf, 0, sampleBuf.Length);
                hasher.AppendData(sampleBuf.AsSpan(0, tailRead));
            }
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static void TryWriteFastStamp(string stampPath, string fastStamp)
    {
        try
        {
            var dir = Path.GetDirectoryName(stampPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(stampPath, fastStamp);
        }
        catch { }
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
