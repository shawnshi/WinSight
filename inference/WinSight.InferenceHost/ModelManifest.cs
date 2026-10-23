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
    private readonly Manifest manifest;
    private readonly List<FileStream> readLocks = [];
    internal string Root { get; }
    internal byte[] TokenizerJson { get; }

    internal ModelManifest(string root, string? manifestPath = null)
    {
        Root = Path.GetFullPath(root);
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
                // Windows read-sharing locks prevent replacement between hashing and lazy graph creation.
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                readLocks.Add(file);
                if (file.Length != asset.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(file)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Pinned model asset mismatch: {asset.Path}");
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

    internal string Graph(string name) => Path.Combine(Root, "onnx", name);

    internal string Profile(string mediaPreprocessorVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPreprocessorVersion);
        // Deliberately excludes filesystem locations, timestamps and host identity.
        var descriptor = new StringBuilder("winsight-embedding-profile-v2\n")
            .Append(Repository).Append('@').Append(Revision).Append('\n')
            .Append("ort=").Append(Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetVersionString()).Append('\n')
            .Append("provider=qualified-per-graph-with-same-model-CPU-fallback;dtype=q4;dim=768;features=512\n")
            .Append("tokenizer=tokenizers-0.23.1;abi=1;special=true\n")
            .Append("text=raw-prompt-v1;maxTokens=8192\n")
            .Append("media=embedding-gemma2-placeholder-v1;preprocessor=").Append(mediaPreprocessorVersion).Append('\n');
        foreach (var asset in manifest.Files.OrderBy(asset => asset.Path, StringComparer.Ordinal))
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
