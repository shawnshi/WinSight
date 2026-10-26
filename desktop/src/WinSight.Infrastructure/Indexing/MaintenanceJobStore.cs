using System.Text.Json;

namespace WinSight.Infrastructure.Indexing;

/// <summary>A maintenance request accepted by the bridge. It is only a request: the desktop app performs the
/// work after checking its own authorization, so the bridge never writes the index itself.</summary>
public sealed record MaintenanceJobRequest(string Id, string Root, string GrantId, string RequestedUtc, string State);

/// <summary>
/// File-based hand-off between the MCP bridge and the desktop app. Jobs are written atomically, claimed by
/// moving them out of the inbox, and completed with a recorded outcome; nothing is deleted silently.
/// </summary>
public sealed class MaintenanceJobStore
{
    private readonly string jobsRoot;

    public MaintenanceJobStore(string dataDirectory) => jobsRoot = Path.Combine(dataDirectory, "bridge-jobs");

    public string Inbox => Path.Combine(jobsRoot, "inbox");
    public string Claimed => Path.Combine(jobsRoot, "claimed");
    public string Completed => Path.Combine(jobsRoot, "completed");

    /// <summary>Records a request. The bridge calls this only after the caller proved it holds the grant.</summary>
    public async Task<MaintenanceJobRequest> SubmitAsync(string root, string grantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        Directory.CreateDirectory(Inbox);
        var id = Guid.NewGuid().ToString("N");
        var request = new MaintenanceJobRequest(id, Path.GetFullPath(root), grantId, DateTime.UtcNow.ToString("o"), "pending");
        var target = Path.Combine(Inbox, id + ".json");
        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(request), new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, target, overwrite: false);
        return request;
    }

    /// <summary>Claims the oldest pending job; returns null when the inbox is empty.</summary>
    public MaintenanceJobRequest? ClaimNext()
    {
        if (!Directory.Exists(Inbox)) return null;
        var next = Directory.GetFiles(Inbox, "*.json").OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault();
        if (next is null) return null;
        Directory.CreateDirectory(Claimed);
        var claimed = Path.Combine(Claimed, Path.GetFileName(next));
        File.Move(next, claimed, overwrite: true);
        try
        {
            return JsonSerializer.Deserialize<MaintenanceJobRequest>(File.ReadAllText(claimed));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Records the outcome next to the claimed job so an operator can see why it did or did not run.</summary>
    public async Task CompleteAsync(MaintenanceJobRequest request, bool accepted, string detail, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Completed);
        var source = Path.Combine(Claimed, request.Id + ".json");
        if (File.Exists(source)) File.Move(source, Path.Combine(Completed, request.Id + ".json"), overwrite: true);
        var outcome = new { request.Id, request.Root, request.GrantId, accepted, detail, completedUtc = DateTime.UtcNow.ToString("o") };
        await File.WriteAllTextAsync(Path.Combine(Completed, request.Id + ".result.json"),
            JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the grant the user created explicitly; absent means maintenance is not authorized.</summary>
    public static string? ReadGrant(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "maintenance-grant.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("grantId", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
