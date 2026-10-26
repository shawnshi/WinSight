using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinSight.Infrastructure.Configuration;

/// <summary>Result of inspecting a client configuration before any write happens.</summary>
public sealed record ClientConfigPlan(string Path, bool FileExists, bool AlreadyConnected, bool Blocked, string Summary,
    IReadOnlyList<string> Changes, string? BlockReason = null, string? BackupPath = null);

/// <summary>
/// Prepares and applies an MCP entry in a user-chosen client configuration. Nothing is scanned automatically:
/// the caller names the file. Writes are backed up, atomic, and never clobber a different existing entry. The
/// entry carries only the bridge path and --stdio; no token is ever written.
/// </summary>
public sealed class ClientConfigAssistant
{
    public const string ServerKey = "winsight";

    public async Task<ClientConfigPlan> PreviewAsync(string configPath, string bridgePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgePath);
        var full = System.IO.Path.GetFullPath(configPath);
        var bridge = System.IO.Path.GetFullPath(bridgePath);
        if (!File.Exists(bridge)) return Blocked(full, false, "The bridge executable does not exist at " + bridge + ".");
        if (!File.Exists(full)) return new ClientConfigPlan(full, false, false, false, "A new configuration file will be created.",
            new[] { "add mcpServers." + ServerKey + " -> " + bridge + " --stdio" });

        string text;
        try
        {
            text = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException error)
        {
            return Blocked(full, true, "The file cannot be read: " + error.Message);
        }
        if (string.IsNullOrWhiteSpace(text)) return new ClientConfigPlan(full, true, false, false, "The file is empty; one entry will be added.",
            new[] { "add mcpServers." + ServerKey + " -> " + bridge + " --stdio" });

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException error)
        {
            return Blocked(full, true, "The file is not valid JSON, so it will not be touched: " + error.Message);
        }
        if (root is not JsonObject document)
            return Blocked(full, true, "The configuration root is not an object; refusing to rewrite it.");

        var servers = document["mcpServers"] as JsonObject;
        var existing = servers?[ServerKey] as JsonObject;
        if (existing is null)
            return new ClientConfigPlan(full, true, false, false, "One entry will be added; existing entries are preserved.",
                new[] { "add mcpServers." + ServerKey + " -> " + bridge + " --stdio" });

        var command = existing["command"]?.GetValue<string>();
        var args = existing["args"] as JsonArray;
        var sameCommand = string.Equals(command, bridge, StringComparison.OrdinalIgnoreCase);
        var sameArgs = args is { Count: 1 } && string.Equals(args[0]?.GetValue<string>(), "--stdio", StringComparison.Ordinal);
        if (sameCommand && sameArgs)
            return new ClientConfigPlan(full, true, true, false, "Already connected with this bridge; nothing to change.", Array.Empty<string>());
        return Blocked(full, true, "mcpServers." + ServerKey + " already points somewhere else (" + (command ?? "no command") + "); it will not be overwritten without an explicit decision.");
    }

    public async Task<ClientConfigPlan> ApplyAsync(string configPath, string bridgePath, bool replaceExisting = false, CancellationToken cancellationToken = default)
    {
        var plan = await PreviewAsync(configPath, bridgePath, cancellationToken).ConfigureAwait(false);
        if (plan.Blocked || plan.AlreadyConnected) return plan;
        var full = plan.Path;
        var bridge = System.IO.Path.GetFullPath(bridgePath);

        JsonObject document;
        if (!File.Exists(full))
        {
            document = new JsonObject();
        }
        else
        {
            var text = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
            document = string.IsNullOrWhiteSpace(text) ? new JsonObject() : (JsonObject)JsonNode.Parse(text)!;
        }
        if (document["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            document["mcpServers"] = servers;
        }
        if (servers[ServerKey] is JsonObject present && !replaceExisting)
            return Blocked(full, true, "mcpServers." + ServerKey + " already exists with different content; pass replaceExisting to overwrite.");
        servers[ServerKey] = new JsonObject
        {
            ["command"] = bridge,
            ["args"] = new JsonArray("--stdio"),
        };

        string? backup = null;
        if (File.Exists(full))
        {
            backup = full + ".winsight-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Copy(full, backup, overwrite: false);
        }
        var temporary = full + ".winsight-tmp-" + Guid.NewGuid().ToString("N");
        var payload = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporary, payload, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, full, overwrite: true);
        return new ClientConfigPlan(full, true, true, false, "Entry written; no token was stored.", Array.Empty<string>(), null, backup);
    }

    private static ClientConfigPlan Blocked(string path, bool exists, string reason) =>
        new(path, exists, false, true, "Nothing will be written.", Array.Empty<string>(), reason);
}
