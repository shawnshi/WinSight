using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;

namespace WinSight.InferenceHost;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        try
        {
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
                var providerInstall = true;
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
                        case "--provider-install" when i + 1 < args.Length:
                            var installArg = args[++i];
                            if (!bool.TryParse(installArg, out providerInstall))
                                throw new ArgumentException("provider-install must be true or false");
                            break;
                        default: throw new ArgumentException($"Unknown or incomplete host option: {args[i]}");
                    }
                }
                if (hostModelRoot is null || manifestPath is null)
                    throw new ArgumentException("Usage: --stdio --models DIR --manifest FILE [--text-only] [--device auto|discrete-gpu|npu|gpu|cpu] [--provider-install true|false]");
                if (device is not ("auto" or "discrete-gpu" or "npu" or "gpu" or "cpu"))
                    throw new ArgumentException("device must be auto, discrete-gpu, npu, gpu or cpu");
                return StdioServer.Run(hostModelRoot, manifestPath, textOnly, device, providerInstall);
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
            if (args is not ["environment"])
                throw new ArgumentException("Usage: --stdio --models DIR --manifest FILE [--text-only] [--device auto|discrete-gpu|npu|gpu|cpu] [--provider-install true|false] | environment | inspect <model-dir> | embed-tokens <model-dir> <fixture.json> <report-dir> | extract-media ABSOLUTE_REQUEST_JSON | inspect-media ABSOLUTE_REQUEST_JSON | inspect-image ABSOLUTE_REQUEST_JSON | extract-soundtrack ABSOLUTE_REQUEST_JSON | render-pdf-page ABSOLUTE_REQUEST_JSON");

            var catalog = ExecutionProviderCatalog.GetDefault()
                ?? throw new InvalidOperationException("Windows ML execution-provider catalog is unavailable");
            // Discovery only: never EnsureReady/EnsureAndRegister (those may download providers).
            var providers = catalog.FindAllProviders().Select(provider => new
            {
                name = provider.Name,
                readyState = provider.ReadyState.ToString()
            }).ToArray();
            var environment = OrtEnv.Instance();
            var loaded = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Where(module => string.Equals(module.ModuleName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
                .Select(module => module.FileName).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "P0", ok = true,
                windowsMlCatalog = catalog.GetType().FullName,
                ortVersion = environment.GetVersionString(),
                availableProviders = environment.GetAvailableProviders(),
                discoveredProviders = providers,
                loadedOrtModules = loaded,
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
