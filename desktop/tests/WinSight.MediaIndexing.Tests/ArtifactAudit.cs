using System.Text.Json;

namespace WinSight.MediaIndexing.Tests;

internal sealed record ArtifactBirth(int Sequence, PidReceipt Worker, string Workspace, string Request, string? Created, string[] Live);
internal static class ArtifactAudit
{
    internal static ArtifactBirth[] Read(string sourceDirectory)
    {
        string dir = Path.Combine(sourceDirectory, "a");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).Select(p => JsonSerializer.Deserialize<ArtifactBirth>(File.ReadAllText(p)) ?? throw new InvalidDataException("Invalid artifact audit")).ToArray() : [];
    }
    internal static void Birth(string source, string request, string? created)
    {
        string sourceDirectory = Path.GetDirectoryName(source)!;
        string workspace = Path.GetDirectoryName(Path.GetDirectoryName(request))!;
        var prior = Read(sourceDirectory);
        Check.That(prior.All(p => p.Workspace == workspace), "Audit cannot mix workspaces");
        if (created is not null) Check.That(created == Path.Combine(Path.GetDirectoryName(request)!, "output", Path.GetFileName(created)) && Path.GetFileName(created) is "frame.png" or "audio.wav", "Unknown audit artifact");
        // Only trusted, previously recorded exact births are inspected, never an unrelated filesystem tree.
        var known = prior.Select(p => p.Created).Append(created).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Check.That(known.All(p => p.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "Audit escaped owned workspace");
        PidReceipt id;
        using (var process = System.Diagnostics.Process.GetCurrentProcess()) id = new(process.Id, process.StartTime.ToUniversalTime().Ticks);
        var receipt = new ArtifactBirth(prior.Length, id, workspace, request, created, known.Where(File.Exists).ToArray());
        string dir = Path.Combine(sourceDirectory, "a"); Directory.CreateDirectory(dir);
        Owned.Atomic(Path.Combine(dir, prior.Length.ToString("D4") + ".json"), receipt);
    }
}
