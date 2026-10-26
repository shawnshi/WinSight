using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace WinSight.InferenceHost;

internal static class StdioServer
{
    private const int MaxLineBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static int Run(string modelRoot, string manifestPath, bool textOnly, string device = "auto")
    {
        var clock = Stopwatch.StartNew();
        EmbeddingEngine engine;
        try { engine = new EmbeddingEngine(modelRoot, MediaPreprocessor.ProfileVersion, manifestPath, device); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Host startup failed: {error.GetType().Name}: {error.Message}");
            Emit(new { ready = false, protocol = 1, error = error.Message });
            return 1;
        }
        using (engine)
        using (var input = new BufferedStream(Console.OpenStandardInput(), 65536))
        {
            var loadSeconds = clock.Elapsed.TotalSeconds;
            var capabilities = new { text = true, image = !textOnly, audio = !textOnly };
            Emit(new { ready = true, info = Info(engine, loadSeconds, textOnly), capabilities, protocol = 1 });
            while (true)
            {
                var (bytes, oversized) = ReadLine(input);
                if (bytes is null) return 0;
                object? id = null;
                try
                {
                    if (oversized) throw new ArgumentException("JSONL line exceeds 1 MiB");
                    using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 16 });
                    var request = document.RootElement;
                    if (request.ValueKind != JsonValueKind.Object) throw new ArgumentException("Request must be a JSON object");
                    if (request.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != request.EnumerateObject().Count())
                        throw new ArgumentException("Duplicate request fields");
                    if (request.TryGetProperty("id", out var requestId))
                    {
                        if (requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                            throw new ArgumentException("id must be a string, number or null");
                        id = requestId.Clone();
                    }
                    if (!request.TryGetProperty("op", out var operation) || operation.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("Missing string op");
                    switch (operation.GetString())
                    {
                        case "quit": Emit(new { id, ok = true }); return 0;
                        case "info": Emit(new { id, ok = true, info = Info(engine, loadSeconds, textOnly), capabilities, protocol = 1 }); break;
                        case "text": EmbedBatch(engine, request, id, "text"); break;
                        case "image" or "audio":
                            if (textOnly) throw new ArgumentException("Media operations are disabled in text-only mode");
                            EmbedBatch(engine, request, id, operation.GetString()!);
                            break;
                        default: throw new ArgumentException("Unknown operation");
                    }
                }
                catch (Exception error) when (error is JsonException or ArgumentException)
                {
                    Emit(new { id, ok = false, error = error.Message });
                }
            }
        }
    }

    internal static int Run(string modelRoot, string manifestPath, bool textOnly, bool automaticProviders) =>
        Run(modelRoot, manifestPath, textOnly, automaticProviders ? "auto" : "cpu");

    private static object Info(EmbeddingEngine engine, double loadSeconds, bool textOnly) => new
    {
        model = ModelManifest.Repository, revision = ModelManifest.Revision,
        runtime = "windows-ml-ort-" + OrtEnv.Instance().GetVersionString(), dtype = "q4", dim = EmbeddingEngine.Dimension,
        text_only = textOnly,
        graph_capabilities = new[] { "text", "vision_features", "audio_features" },
        media_preprocessor = MediaPreprocessor.ProfileVersion, validation = "unverified-source-integration", profile_id = engine.Profile,
        input_contract = "raw-text-v1: callers supply query/document task prompts; no implicit truncation",
        requested_provider = engine.RequestedProvider,
        provider_downloads_allowed = engine.ProviderDownloadsAllowed,
        backend_status = engine.ProviderStatus,
        performance_profile_id = engine.PerformanceProfile,
        // Sessions are lazy: startup time is not a measured model-load time.
        load_seconds = (double?)null, startup_seconds = loadSeconds,
        sessions_loaded = new { text = engine.TextLoaded, vision = engine.VisionLoaded, audio = engine.AudioLoaded }
    };

    private static void EmbedBatch(EmbeddingEngine engine, JsonElement request, object? id, string operation)
    {
        var budget = 280;
        if (request.TryGetProperty("budget", out var requestedBudget)
            && (requestedBudget.ValueKind != JsonValueKind.Number || !requestedBudget.TryGetInt32(out budget) || budget is < 1 or > 1120))
            throw new ArgumentException("budget must be an integer in 1..1120");
        if (!request.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() is < 1 or > 32)
            throw new ArgumentException("inputs must be an array with 1..32 items");
        var count = inputs.GetArrayLength();
        var epoch = engine.ProviderEpoch;
        var errors = new string?[count];
        var bytes = new byte[count * EmbeddingEngine.Dimension * sizeof(float)];
        var index = 0;
        foreach (var item in inputs.EnumerateArray())
        {
            try
            {
                if (item.ValueKind != JsonValueKind.String) throw new ArgumentException("Input must be a string");
                var vector = EmbedItem(engine, operation, item.GetString()!, budget);
                for (var column = 0; column < vector.Length; column++)
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((index * EmbeddingEngine.Dimension + column) * sizeof(float), sizeof(float)), vector[column]);
            }
            // WINML-STATIC-001: unsupported media formats are item errors, not host-fatal failures.
            catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException
                or NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or OnnxRuntimeException)
            {
                errors[index] = $"{error.GetType().Name}: {error.Message}";
                Console.Error.WriteLine($"Item {index} failed: {error.GetType().Name}");
            }
            index++;
        }
        // Failed or partial batches are never calibration evidence, even if their route stayed unchanged.
        var succeeded = errors.All(error => error is null);
        var performanceProfile = succeeded ? engine.PerformanceForBatch(epoch) : null;
        Emit(new { id, ok = succeeded, dim = EmbeddingEngine.Dimension, count,
            vectors = Convert.ToBase64String(bytes), errors, performance_profile_id = performanceProfile });
    }

    private static float[] EmbedItem(EmbeddingEngine engine, string operation, string input, int budget)
    {
        if (operation == "text") return engine.EmbedText(input);
        if (operation == "image")
        {
            var image = MediaPreprocessor.LoadImage(input, budget);
            var validPatches = image.Positions.Where((value, index) => index % 2 == 0 && value >= 0).Count();
            var features = engine.EncodeVision(image.Pixels, image.Positions, image.PatchCount);
            if (validPatches <= 0 || validPatches % 9 != 0 || features.Length != validPatches / 9 * 512)
                throw new InvalidDataException("Vision feature count differs from valid, unpadded patch count");
            return engine.EmbedFeatures(features, "image");
        }
        var audio = MediaPreprocessor.LoadAudio(input);
        var rows = engine.EncodeAudio(audio.Features, audio.Mask, audio.FrameCount);
        // Encoder rows are not sentence vectors; every modality passes through the shared text graph.
        return engine.EmbedFeatures(rows, "audio");
    }

    private static (byte[]? Bytes, bool Oversized) ReadLine(Stream input)
    {
        using var buffer = new MemoryStream();
        var oversized = false;
        while (true)
        {
            var value = input.ReadByte();
            if (value == -1 && buffer.Length == 0 && !oversized) return (null, false);
            if (value is -1 or 10) return (buffer.ToArray(), oversized);
            if (buffer.Length < MaxLineBytes) buffer.WriteByte((byte)value);
            else oversized = true; // Drain to newline before accepting another request.
        }
    }

    private static void Emit(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value));
        Console.Out.Flush();
    }
}
