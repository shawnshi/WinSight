using System.Diagnostics;
using System.Text.Json;
using WinSight.Core;
using WinSight.Core.Diagnostics;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Inference;
using WinSight.Infrastructure.Media;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;

using System.Text.Json.Nodes;

namespace WinSight.Infrastructure.Runtime;

public sealed partial class DesktopBackend
{
    /// <summary>
    /// Generates a ready-to-use MCP client configuration snippet (in standard mcpServers format)
    /// suitable for pasting directly into Claude Desktop, Cursor, Windsurf, Cline, etc.
    /// </summary>
    public Task<string> GetMcpClientConfigSnippetAsync(string transport = "stdio", int httpPort = 0, CancellationToken cancellationToken = default)
    {
        var bridge = Path.Combine(applicationDirectory, "bridge", "winsight-mcp.exe");
        if (!File.Exists(bridge))
        {
            var defaultInstall = @"C:\Program Files\WinSight\bridge\winsight-mcp.exe";
            if (File.Exists(defaultInstall)) bridge = defaultInstall;
        }

        var indexPath = Path.Combine(IndexDirectory, "index.sqlite");
        var isHttp = string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase);

        JsonObject serverObj;
        if (isHttp)
        {
            var port = httpPort is >= 1024 and <= 65535 ? httpPort : 8765;
            serverObj = new JsonObject
            {
                ["url"] = $"http://127.0.0.1:{port}/mcp/"
            };
        }
        else
        {
            serverObj = new JsonObject
            {
                ["command"] = bridge,
                ["args"] = new JsonArray { "--index", indexPath, "--stdio" }
            };
        }

        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["winsight"] = serverObj
            }
        };

        return Task.FromResult(root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Inspects a client configuration the user pointed at. Nothing is written and no client is scanned for.
    /// </summary>
    public async Task<ClientConfigPreview?> PreviewClientConfigAsync(string configPath, string transport = "stdio", int httpPort = 0, CancellationToken cancellationToken = default)
    {
        var bridge = Path.Combine(applicationDirectory, "bridge", "winsight-mcp.exe");
        var plan = await new Configuration.ClientConfigAssistant().PreviewAsync(configPath, bridge, Parse(transport), httpPort, cancellationToken).ConfigureAwait(false);
        return new ClientConfigPreview(plan.FileExists, plan.AlreadyConnected, plan.Blocked, plan.Summary, plan.BlockReason, plan.BackupPath);
    }

/// <summary>Writes the entry after a backup; refuses a conflicting existing entry and never stores a token.</summary>
    public async Task<ClientConfigPreview?> ApplyClientConfigAsync(string configPath, string transport = "stdio", int httpPort = 0, CancellationToken cancellationToken = default)
    {
        var bridge = Path.Combine(applicationDirectory, "bridge", "winsight-mcp.exe");
        var plan = await new Configuration.ClientConfigAssistant().ApplyAsync(configPath, bridge, replaceExisting: false, Parse(transport), httpPort, cancellationToken).ConfigureAwait(false);
        return new ClientConfigPreview(plan.FileExists, plan.AlreadyConnected, plan.Blocked, plan.Summary, plan.BlockReason, plan.BackupPath);
    }

/// <summary>
    /// Asks the inference host (Windows ML ORT 1.27) which devices it can see. Read-only: the host registers
    /// local provider libraries only and never downloads.
    /// </summary>
    public async Task<WinMlProbe?> ProbeWinMlAsync(CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(applicationDirectory, "inference", "WinSight.InferenceHost.exe");
        if (!File.Exists(executable))
            executable = Path.Combine(applicationDirectory, "inference", "winml", "WinSight.WinMlHost.exe");
        if (!File.Exists(executable)) return new WinMlProbe("unavailable", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<WinMlDevice>(), "The inference host is not part of this payload.");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("environment");
            using var process = System.Diagnostics.Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new WinMlProbe("unavailable", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<WinMlDevice>(),
                    "environment exited " + process.ExitCode + ": " + (await stderr.ConfigureAwait(false)).Trim());
            using var document = System.Text.Json.JsonDocument.Parse(text);
            var root = document.RootElement;
            string[] ReadStrings(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Array
                ? value.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length != 0).ToArray()
                : Array.Empty<string>();
            var devices = root.TryGetProperty("devices", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array
                ? list.EnumerateArray().Select(d => new WinMlDevice(
                    d.TryGetProperty("ep", out var ep) ? ep.GetString() ?? "" : "",
                    d.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
                    d.TryGetProperty("device_id", out var id) && id.TryGetInt32(out var parsedId) ? parsedId : 0,
                    d.TryGetProperty("vendor_id", out var vendor) && vendor.TryGetInt32(out var parsedVendor) ? parsedVendor : 0))
                  .Where(d => d.Ep.Length != 0).ToArray()
                : Array.Empty<WinMlDevice>();
            var ort = root.TryGetProperty("ort_version", out var version) ? version.GetString() ?? "unknown" : "unknown";
            return new WinMlProbe(ort, ReadStrings("libraries"), ReadStrings("available"), devices);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return new WinMlProbe("unavailable", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<WinMlDevice>(), error.GetType().Name + ": " + error.Message);
        }
    }

/// <summary>
    /// Runs the isolated Foundry Local host in probe mode: it only discovers providers and never downloads.
    /// A missing or failing host degrades to an explanatory error instead of failing the page.
    /// </summary>
    public async Task<FoundryProbe?> ProbeFoundryAsync(CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(applicationDirectory, "foundry", "WinSight.FoundryHost.exe");
        if (!File.Exists(executable)) return new FoundryProbe("unavailable", Array.Empty<string>(), Array.Empty<FoundryProvider>(), "Foundry host is not part of this payload.");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("probe");
            using var process = System.Diagnostics.Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0) return new FoundryProbe("unavailable", Array.Empty<string>(), Array.Empty<FoundryProvider>(), "probe exited " + process.ExitCode + ": " + (await stderr.ConfigureAwait(false)).Trim());
            using var document = System.Text.Json.JsonDocument.Parse(text);
            var root = document.RootElement;
            var sdk = root.TryGetProperty("sdk", out var sdkElement) ? sdkElement.GetString() ?? "unknown" : "unknown";
            var types = root.TryGetProperty("deviceTypes", out var typeElement) && typeElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? typeElement.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length != 0).ToArray()
                : Array.Empty<string>();
            var providers = root.TryGetProperty("providers", out var providerElement) && providerElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? providerElement.EnumerateArray()
                    .Select(p => new FoundryProvider(p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        p.TryGetProperty("registered", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True))
                    .Where(p => p.Name.Length != 0).ToArray()
                : Array.Empty<FoundryProvider>();
            return new FoundryProbe(sdk, types, providers);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return new FoundryProbe("unavailable", Array.Empty<string>(), Array.Empty<FoundryProvider>(), error.GetType().Name + ": " + error.Message);
        }
    }

/// <summary>
    /// Downloads and registers the Foundry Local execution providers that apply to this device. This is the
    /// only path that touches the network, it is bounded, and it reports which providers failed.
    /// </summary>
    public async Task<FoundryInstallResult?> InstallFoundryProvidersAsync(CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(applicationDirectory, "foundry", "WinSight.FoundryHost.exe");
        if (!File.Exists(executable)) return new FoundryInstallResult(false, "host-absent", Array.Empty<string>(), Array.Empty<string>(), "Foundry host is not part of this payload.");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("register");
            start.ArgumentList.Add("--allow-download");
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return new FoundryInstallResult(false, "start-failed", Array.Empty<string>(), Array.Empty<string>(), "Foundry host did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            var tail = (await stderr.ConfigureAwait(false)).Trim();
            var jsonStart = text.IndexOf('{', StringComparison.Ordinal);
            if (jsonStart < 0) return new FoundryInstallResult(false, "no-result", Array.Empty<string>(), Array.Empty<string>(), tail.Length == 0 ? "host produced no result" : tail);
            using var document = System.Text.Json.JsonDocument.Parse(text[jsonStart..]);
            var root = document.RootElement;
            string[] ReadArray(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Array
                ? value.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length != 0).ToArray()
                : Array.Empty<string>();
            var success = root.TryGetProperty("success", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True;
            var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "" : "";
            return new FoundryInstallResult(success, status, ReadArray("registered"), ReadArray("failed"), tail.Length == 0 ? null : tail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FoundryInstallResult(false, "timeout", Array.Empty<string>(), Array.Empty<string>(), "provider installation exceeded 10 minutes");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            return new FoundryInstallResult(false, "failed", Array.Empty<string>(), Array.Empty<string>(), error.GetType().Name + ": " + error.Message);
        }
    }

public async Task<EmbeddingInfo> GetEmbeddingInfoAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeStop.Token);
        cancellationToken = linked.Token;
        IEmbeddingService client;
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            client = GetEmbeddings();
        }
        finally { lifecycle.Release(); }
        // The host call may queue behind inference work or start the child; never hold the global lifecycle lock for it.
        return await client.GetInfoAsync(cancellationToken);
    }

/// <summary>
    /// Embeddings run on the unified inference host (Windows ML ORT 1.27), supporting CPU, DirectML (GPU)
    /// and NPU execution. The choice is stable per accelerator setting.
    /// </summary>
    private IEmbeddingService GetEmbeddings() => embeddings ??= new InferenceHostClient(
        settings.InferenceExecutable ?? ResolveInferenceExecutable(),
        settings.ModelDirectory ?? ResolveModelDirectory(),
        settings.ManifestPath ?? ResolveManifestPath(),
        settings.Accelerator);

    private string ResolveInferenceExecutable()
    {
        var defaultHost = Path.Combine(applicationDirectory, "inference", "WinSight.InferenceHost.exe");
        if (File.Exists(defaultHost)) return defaultHost;
        var winMlHost = Path.Combine(applicationDirectory, "inference", "winml", "WinSight.WinMlHost.exe");
        if (File.Exists(winMlHost)) return winMlHost;

        const string installedHost = @"C:\Program Files\WinSight\inference\WinSight.InferenceHost.exe";
        if (File.Exists(installedHost)) return installedHost;

        var devHost = Path.GetFullPath(Path.Combine(applicationDirectory, "..", "..", "..", "..", "..", "..", "inference", "WinSight.InferenceHost", "bin", "Debug", "net11.0-windows10.0.26100.0", "win-x64", "WinSight.InferenceHost.exe"));
        if (File.Exists(devHost)) return devHost;

        return defaultHost;
    }

    private string ResolveModelDirectory()
    {
        var defaultModels = Path.Combine(applicationDirectory, "models");
        if (Directory.Exists(defaultModels)) return defaultModels;
        const string installedModels = @"C:\Program Files\WinSight\models";
        if (Directory.Exists(installedModels)) return installedModels;
        var devModels = Path.GetFullPath(Path.Combine(applicationDirectory, "..", "..", "..", "..", "..", "..", "models"));
        if (Directory.Exists(devModels)) return devModels;
        return defaultModels;
    }

    private string ResolveManifestPath()
    {
        var defaultManifest = Path.Combine(applicationDirectory, "inference", "model-manifest.json");
        if (File.Exists(defaultManifest)) return defaultManifest;
        const string installedManifest = @"C:\Program Files\WinSight\inference\model-manifest.json";
        if (File.Exists(installedManifest)) return installedManifest;
        var devManifest = Path.GetFullPath(Path.Combine(applicationDirectory, "..", "..", "..", "..", "..", "..", "inference", "WinSight.InferenceHost", "model-manifest.json"));
        if (File.Exists(devManifest)) return devManifest;
        return defaultManifest;
    }

/// <summary>
    /// Reports which models are in use and where they run. Embedding identity comes from the inference host;
    /// the OCR identity is the pinned engine and manifest profile, and OCR always runs in its own process.
    /// </summary>
    public async Task<ModelInventory?> GetModelInventoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var host = settings.InferenceExecutable ?? ResolveInferenceExecutable();
            if (!File.Exists(host))
            {
                return new ModelInventory("unavailable", "unavailable", "unavailable", $"Inference host not found: {Path.GetFileName(host)}",
                    WinSight.Shared.OcrIdentity.Engine, WinSight.Shared.OcrIdentity.EngineVersion, WinSight.Shared.OcrIdentity.ModelProfile, "unavailable",
                    $"The inference host executable was not found at '{host}'. Please verify installation.");
            }
            var info = await GetEmbeddings().GetInfoAsync(cancellationToken).ConfigureAwait(false);
            var device = info.RequestedProvider switch
            {
                "CPUExecutionProvider" => "CPU",
                var other when other.StartsWith("device-", StringComparison.OrdinalIgnoreCase) => "auto (gated: " + other.Replace("-with-real-model-gates", "", StringComparison.Ordinal) + ")",
                _ => info.RequestedProvider
            };
            return new ModelInventory(info.Model, info.Revision, info.ProfileId, device + " via " + info.Runtime,
                WinSight.Shared.OcrIdentity.Engine, WinSight.Shared.OcrIdentity.EngineVersion, WinSight.Shared.OcrIdentity.ModelProfile, "CPU (isolated OCR host process)",
                "Embedding runs in the inference host; OCR runs in its own process so a parser crash cannot take search down.");
        }
        catch (Exception error)
        {
            AppLog.Warn("GetModelInventoryAsync failed", error);
            var detail = error.Message.Contains("未找到") || error.Message.Contains("Could not find a part of the path") || error.Message.Contains("FileNotFoundException")
                ? "离线嵌入模型未就绪: " + error.Message
                : error.Message;
            return new ModelInventory("unavailable", "unavailable", "unavailable", detail,
                WinSight.Shared.OcrIdentity.Engine, WinSight.Shared.OcrIdentity.EngineVersion, WinSight.Shared.OcrIdentity.ModelProfile, "CPU (isolated OCR host process)",
                "The embedding host did not answer; the OCR identity is still the pinned manifest.");
        }
    }

/// <summary>
    /// Snapshot for the MCP console: how many requests are waiting, what recently happened, and which
    /// transports are actually available. It reports facts only; no token or file path leaves this method.
    /// </summary>
    public Task<McpConsoleStatus?> GetMcpConsoleStatusAsync(CancellationToken cancellationToken = default)
    {
        var store = JobStore;
        int Count(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Count(name => !name.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase)) : 0;
        var recent = new List<string>();
        if (Directory.Exists(store.Completed))
        {
            foreach (var file in Directory.GetFiles(store.Completed, "*.result.json").OrderByDescending(name => name).Take(5))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    var root = document.RootElement;
                    var accepted = root.TryGetProperty("accepted", out var flag) && flag.GetBoolean();
                    var detail = root.TryGetProperty("detail", out var value) ? value.GetString() ?? "" : "";
                    recent.Add((accepted ? "accepted" : "refused") + ": " + detail);
                }
                catch (System.Text.Json.JsonException) { recent.Add("unreadable completed request"); }
            }
        }
        var bridge = Path.Combine(applicationDirectory, "bridge", "winsight-mcp.exe");
        var notes = new List<string>
        {
            File.Exists(bridge) ? "Bridge present: stdio transport available." : "Bridge is missing from this payload.",
            "Loopback HTTP is opt-in (bridge --http-port) and is not started by the app.",
            "Requests are only executed by this app; the bridge never writes the index.",
        };
        return Task.FromResult<McpConsoleStatus?>(new McpConsoleStatus(Count(store.Inbox), Count(store.Claimed), Count(store.Completed), recent, "not running", bridge, notes));
    }

private NativeMediaProcessClient? GetMediaClient()
    {
        var mediaHost = settings.InferenceExecutable ?? Path.Combine(applicationDirectory, "inference", "WinSight.InferenceHost.exe");
        if (!File.Exists(mediaHost)) return null;
        return new NativeMediaProcessClient(new NativeMediaProcessOptions
        {
            HostExecutable = mediaHost,
            WorkspaceRoot = Path.Combine(dataDirectory, "media-tasks")
        });
    }

private static ClientTransport Parse(string transport) =>
        string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase) ? ClientTransport.Http : ClientTransport.Stdio;
}
