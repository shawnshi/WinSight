using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace WinSight.InferenceHost;

internal static class ModelProbe
{
    internal static int ResolveThreadCount()
    {
        var threadEnv = Environment.GetEnvironmentVariable("WINSIGHT_INFERENCE_THREADS");
        if (int.TryParse(threadEnv, out var customThreads) && customThreads > 0)
            return Math.Clamp(customThreads, 1, Environment.ProcessorCount);

        // Cap background embedding threads to avoid thermal throttling and saturating all host cores.
        // INT4 quantized models see diminishing returns beyond 4 threads.
        return Environment.ProcessorCount <= 4
            ? Math.Max(1, Environment.ProcessorCount / 2)
            : Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
    }

    internal static SessionOptions CpuOptions()
    {
        var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = ResolveThreadCount(),
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        return options;
    }

    internal static object Inspect(string modelRoot)
    {
        var graphs = new[] { "model_q4.onnx", "vision_encoder_q4.onnx", "audio_encoder_q4.onnx" };
        // Require all modalities before loading any session; a missing audio model is not partial success.
        foreach (var graph in graphs)
        {
            var path = Path.Combine(Path.GetFullPath(modelRoot), "onnx", graph);
            if (!File.Exists(path) || !File.Exists(path + "_data"))
                throw new FileNotFoundException($"Missing graph or external data pair: {graph}");
        }
        var reports = new List<object>();
        foreach (var graph in graphs)
        {
            var path = Path.Combine(Path.GetFullPath(modelRoot), "onnx", graph);
            using var options = CpuOptions();
            var clock = Stopwatch.StartNew();
            using var session = new InferenceSession(path, options);
            reports.Add(new
            {
                graph, sessionCreated = true, sessionLoadMs = clock.Elapsed.TotalMilliseconds,
                inputs = Describe(session.InputMetadata), outputs = Describe(session.OutputMetadata)
            });
        }
        return new
        {
            phase = "P0", ok = true, mode = "session-inspection",
            ortVersion = OrtEnv.Instance().GetVersionString(),
            requestedProvider = "CPUExecutionProvider", providerDownloadsAllowed = false,
            modalities = new[] { "text", "vision", "audio" },
            graphs = reports, note = "Session creation is not end-to-end multimodal inference."
        };
    }

    private static object[] Describe(IReadOnlyDictionary<string, NodeMetadata> nodes) =>
        nodes.Select(pair => (object)new
        {
            name = pair.Key, elementType = pair.Value.ElementType.Name,
            dimensions = pair.Value.Dimensions
        }).ToArray();
}
