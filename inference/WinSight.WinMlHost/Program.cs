using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using WinSight.InferenceHost;

// NPU execution host: Windows ML ships its own ONNX Runtime (1.27.x) and the vendor NPU plugins are built
// against it, so this process stays separate from the default host on 1.31. Protocol is identical.
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static int Main(string[] args)
    {
        try
        {
            // The shared provider manager re-launches this executable for isolated qualification.
            if (args is ["--qualify-probe", var qualifyRequestPath, var qualifyResultPath])
            {
                return EpQualificationChild.Run(qualifyRequestPath, qualifyResultPath);
            }
            if (args is ["environment"])
            {
                var catalog = ExecutionProviderCatalog.GetDefault()
                    ?? throw new InvalidOperationException("ExecutionProviderCatalog.GetDefault returned null.");
                var registered = catalog.RegisterCertifiedAsync().AsTask().GetAwaiter().GetResult();
                var registrations = new List<string>();
                foreach (var provider in catalog.FindAllProviders())
                {
                    if (provider.ReadyState != ExecutionProviderReadyState.Ready) continue;
                    if (string.IsNullOrWhiteSpace(provider.LibraryPath)) continue;
                    try
                    {
                        OrtEnv.Instance().RegisterExecutionProviderLibrary(provider.Name, provider.LibraryPath);
                        registrations.Add(provider.Name + "=" + provider.LibraryPath);
                    }
                    catch (Exception error) when (error.Message.Contains("already registered", StringComparison.OrdinalIgnoreCase))
                    {
                        registrations.Add(provider.Name + "=already-registered");
                    }
                }

                var environment = OrtEnv.Instance();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    phase = "P0",
                    ok = true,
                    ort_version = environment.GetVersionString(),
                    catalog_registered = registered.Count,
                    libraries = registrations,
                    available = environment.GetAvailableProviders(),
                    devices = environment.GetEpDevices().Select(d => new
                    {
                        ep = d.EpName,
                        type = d.HardwareDevice.Type.ToString(),
                        device_id = d.HardwareDevice.DeviceId,
                        vendor_id = d.HardwareDevice.VendorId
                    }),
                    provider_downloads_allowed = false
                }, JsonOptions));
                return 0;
            }

            string? modelRoot = null, manifestPath = null;
            var device = "auto";
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--stdio": break;
                    case "--models" when index + 1 < args.Length: modelRoot = args[++index]; break;
                    case "--manifest" when index + 1 < args.Length: manifestPath = args[++index]; break;
                    case "--device" when index + 1 < args.Length: device = args[++index]; break;
                    default: throw new ArgumentException($"Unknown or incomplete host option: {args[index]}");
                }
            }
            if (modelRoot is null || manifestPath is null)
                throw new ArgumentException("Usage: --stdio --models DIR --manifest FILE [--device auto|npu|gpu|cpu] | environment");
            if (device is not ("auto" or "npu" or "gpu" or "dml" or "cpu"))
                throw new ArgumentException("device must be auto, npu, gpu or cpu");
            return StdioServer.Run(modelRoot, manifestPath, false, device);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("host-error:" + error.GetType().Name + ":" + error.Message);
            return 1;
        }
    }
}
