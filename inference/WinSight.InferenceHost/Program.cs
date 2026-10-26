using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace WinSight.InferenceHost;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--qualify-probe", var qualifyRequestPath, var qualifyResultPath])
            {
                return EpQualificationChild.Run(qualifyRequestPath, qualifyResultPath);
            }
            if (args.Length > 0 && args[0] == "extract-media")
            {
                if (args is not [_, var requestPath])
                    throw new ArgumentException("Usage: extract-media ABSOLUTE_REQUEST_JSON");
                var result = NativeMediaCommand.RunRequestFileAsync(requestPath).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            if (args.Length > 0 && args[0] == "render-pdf-page")
            {
                if (args is not [_, var requestPath]) throw new ArgumentException("Usage: render-pdf-page ABSOLUTE_REQUEST_JSON");
                var result = NativeMediaCommand.RenderPdfPageRequestAsync(requestPath).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            if (args.Length > 0 && args[0] is "render-presentation-page" or "render-raster-preview")
            {
                if(args is not [var command,var path])throw new ArgumentException("Use command ABSOLUTE_REQUEST_JSON");
                object result=command=="render-presentation-page"
                    ? NativeMediaCommand.RenderPresentationPageRequestAsync(path).GetAwaiter().GetResult()
                    : NativeMediaCommand.RenderRasterPreviewAsync(path).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));return 0;
            }
            if(args.Length>0&&args[0]=="navigate-office")
            {
                if(args is not [_,var requestPath])throw new ArgumentException("Use navigate-office ABSOLUTE_REQUEST_JSON");
                var result=NativeMediaCommand.NavigateOfficeAsync(requestPath).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));return 0;
            }
            if (args.Length > 0 && args[0] == "render-presentation")
            {
                if (args is not [_, var requestPath]) throw new ArgumentException("Usage: render-presentation ABSOLUTE_REQUEST_JSON");
                var result = NativeMediaCommand.RenderPresentationRequestAsync(requestPath).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            if (args.Length > 0 && args[0] == "inspect-image")
            {
                if (args is not [_, var requestPath])
                    throw new ArgumentException("Usage: inspect-image ABSOLUTE_REQUEST_JSON");
                var result = NativeMediaCommand.RunImageInspectRequestFileAsync(requestPath).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            if (args.Length > 0 && args[0] == "inspect-media")
            {
                if (args is not [_, var requestPath])
                    throw new ArgumentException("Usage: inspect-media ABSOLUTE_REQUEST_JSON");
                var result = NativeMediaCommand.RunInspectRequestFileAsync(requestPath).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            if (args.Length > 0 && args[0] == "extract-soundtrack")
            {
                if (args is not [_, var requestPath])
                    throw new ArgumentException("Usage: extract-soundtrack ABSOLUTE_REQUEST_JSON");
                var result = NativeMediaCommand.RunSoundtrackRequestFileAsync(requestPath).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            if (args.Contains("--stdio", StringComparer.Ordinal))
            {
                string? hostModelRoot = null, manifestPath = null;
                var device = "auto";
                var textOnly = false;
                for (var i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--stdio": break;
                        case "--text-only": textOnly = true; break;
                        case "--models" when i + 1 < args.Length: hostModelRoot = args[++i]; break;
                        case "--manifest" when i + 1 < args.Length: manifestPath = args[++i]; break;
                        case "--device" when i + 1 < args.Length: device = args[++i]; break;
                        default: throw new ArgumentException($"Unknown or incomplete host option: {args[i]}");
                    }
                }
                if (hostModelRoot is null || manifestPath is null)
                    throw new ArgumentException("Usage: --stdio --models DIR --manifest FILE [--text-only] [--device auto|discrete-gpu|npu|gpu|cpu]");
                if (device is not ("auto" or "discrete-gpu" or "npu" or "gpu" or "cpu"))
                    throw new ArgumentException("device must be auto, discrete-gpu, npu, gpu or cpu");
                return StdioServer.Run(hostModelRoot, manifestPath, textOnly, device);
            }
            if (args is ["embed-tokens", var textModelRoot, var fixturePath, var reportDirectory])
            {
                Console.WriteLine(JsonSerializer.Serialize(TextProbe.Run(textModelRoot, fixturePath, reportDirectory), JsonOptions));
                return 0;
            }
            if (args is ["inspect", var modelRoot])
            {
                Console.WriteLine(JsonSerializer.Serialize(ModelProbe.Inspect(modelRoot), JsonOptions));
                return 0;
            }
            if (args.Length != 0 && args[0] != "environment")
            {
                throw new ArgumentException("Usage: --stdio --models DIR --manifest FILE [--text-only] [--device auto|discrete-gpu|npu|gpu|cpu] | environment | inspect <model-dir> | embed-tokens <model-dir> <fixture.json> <report-dir> | extract-media ABSOLUTE_REQUEST_JSON | inspect-media ABSOLUTE_REQUEST_JSON | inspect-image ABSOLUTE_REQUEST_JSON | extract-soundtrack ABSOLUTE_REQUEST_JSON | render-pdf-page ABSOLUTE_REQUEST_JSON");
            }

            object? catalogInfo = null;
            string? registrationStatus = null;
            try
            {
                var catalog = Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog.GetDefault();
                if (catalog != null)
                {
                    try
                    {
                        var reg = catalog.RegisterCertifiedAsync().AsTask().GetAwaiter().GetResult();
                        registrationStatus = $"RegisterCertifiedAsync reported {reg.Count} provider(s): {string.Join(", ", reg.Select(p => p.Name))}";
                    }
                    catch (Exception ex)
                    {
                        registrationStatus = $"RegisterCertifiedAsync failed: {ex.GetType().Name}: {ex.Message}";
                    }

                    foreach (var provider in catalog.FindAllProviders())
                    {
                        if (provider.ReadyState != Microsoft.Windows.AI.MachineLearning.ExecutionProviderReadyState.Ready) continue;
                        if (string.IsNullOrWhiteSpace(provider.LibraryPath)) continue;
                        try { OrtEnv.Instance().RegisterExecutionProviderLibrary(provider.Name, provider.LibraryPath); }
                        catch (Exception ex) when (ex.Message.Contains("already registered", StringComparison.OrdinalIgnoreCase)) { registrationStatus += $"; {provider.Name} already registered"; }
                        catch (Exception ex) { registrationStatus += $"; register {provider.Name} failed: {ex.GetType().Name}: {ex.Message}"; }
                    }

                    var providers = catalog.FindAllProviders().Select(p => new
                    {
                        name = p.Name,
                        readyState = p.ReadyState.ToString(),
                        libraryPath = p.LibraryPath,
                        certification = p.Certification.ToString()
                    }).ToArray();
                    catalogInfo = new { available = true, registrationStatus, providers };
                }
                else
                {
                    catalogInfo = new { available = false, reason = "GetDefault returned null" };
                }
            }
            catch (Exception ex)
            {
                catalogInfo = new { available = false, error = ex.GetType().FullName, message = ex.Message };
            }

            var environment = OrtEnv.Instance();
            var availableProviders = environment.GetAvailableProviders();
            var epDevices = environment.GetEpDevices().Select(device => new
            {
                ep = device.EpName,
                deviceType = device.HardwareDevice.Type.ToString(),
                deviceId = device.HardwareDevice.DeviceId,
                vendorId = device.HardwareDevice.VendorId
            }).ToArray();
            var loaded = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Where(module => string.Equals(module.ModuleName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
                .Select(module => module.FileName).ToArray();

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "P0", ok = true,
                ortVersion = environment.GetVersionString(),
                availableProviders,
                epDevices,
                loadedOrtModules = loaded,
                catalog = catalogInfo,
                providerDownloadsAllowed = false,
                productionConnected = false
            }, JsonOptions));
            return 0;
        }
        catch (Exception error)
        {
            // CLI boundary: preserve the actual failure and exit nonzero; never pretend it was no data.
            if (!(args.Length > 0 && args[0] is "extract-media" or "inspect-media" or "extract-soundtrack") && args.Contains("--stdio", StringComparer.Ordinal) && error is ArgumentException)
                Console.WriteLine(JsonSerializer.Serialize(new { ready = false, protocol = 1, error = error.Message }));
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                ok = false, error = error.GetType().FullName, message = error.Message,
                hresult = $"0x{error.HResult:X8}"
            }, JsonOptions));
            return 1;
        }
    }
}
