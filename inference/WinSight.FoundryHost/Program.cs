using System.Text.Json;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Isolated Foundry Local backend: this process owns ORT 1.31.0 and the Foundry runtime so the main
// inference host keeps the Windows ML ORT 1.24.6 runtime untouched.
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
if (args.Length == 0 || args[0] is "help" or "--help")
{
    Console.WriteLine("WinSight Foundry Local host (isolated model backend)\n"
        + "  probe                      discover execution providers without downloading anything\n"
        + "  register --allow-download [EP ...]   download and register execution providers\n"
        + "  models                     list cached and loaded models (no download)\n");
    return 0;
}

var configuration = new Configuration
{
    AppName = "WinSight FoundryHost",
    AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSight", "FoundryLocal")
};
await FoundryLocalManager.CreateAsync(configuration, NullLogger.Instance, null);
var manager = FoundryLocalManager.Instance;
try
{
    switch (args[0])
    {
        case "probe":
        {
            var eps = manager.DiscoverEps()
                .Select(ep => new { name = ep.Name, registered = ep.IsRegistered })
                .OrderBy(ep => ep.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                protocol = 1,
                sdk = typeof(FoundryLocalManager).Assembly.GetName().Version?.ToString(),
                deviceTypes = Enum.GetNames<DeviceType>(),
                providers = eps,
                downloads = "not-allowed"
            }, json));
            return 0;
        }
        case "register":
        {
            var allow = args.Contains("--allow-download", StringComparer.OrdinalIgnoreCase);
            if (!allow)
            {
                Console.Error.WriteLine("register requires --allow-download: this step fetches provider packages from the network.");
                return 2;
            }
            var names = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
            var result = names.Length == 0
                ? await manager.DownloadAndRegisterEpsAsync((Action<string, double>?)null, null)
                : await manager.DownloadAndRegisterEpsAsync(names, (Action<string, double>?)null, null);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                protocol = 1,
                success = result.Success,
                status = result.Status,
                registered = result.RegisteredEps,
                failed = result.FailedEps
            }, json));
            return result.Success ? 0 : 1;
        }
        default:
            Console.Error.WriteLine("unknown command: " + args[0]);
            return 2;
    }
}
finally
{
    // 1.2.x exposes no explicit shutdown; the process exit releases the runtime.
}
