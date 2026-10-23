using System.Diagnostics;
using System.Text.Json;

namespace WinSight.MediaProcess.Tests;

internal sealed record OwnedIdentity(int Pid, long CreationUtcTicks, string Role);

internal static class OwnedProcesses
{
    internal static string ObservationDirectory(string source) => Path.Combine(Path.GetDirectoryName(source)!, "observations");

    internal static void Record(string source, string role)
    {
        using var process = Process.GetCurrentProcess();
        var directory = ObservationDirectory(source);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(source) + "." + role + ".json");
        var temporary = destination + "." + process.Id + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new OwnedIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, role)));
        File.Move(temporary, destination, overwrite: true); // Publish only a closed, complete identity; never read a writer's partial JSON.
    }

    internal static OwnedIdentity[] Read(string source) => Directory.Exists(ObservationDirectory(source))
        ? Directory.GetFiles(ObservationDirectory(source), Path.GetFileName(source) + ".*.json")
            .Select(p =>
            {
                using var stream = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                return JsonSerializer.Deserialize<OwnedIdentity>(stream) ?? throw new InvalidDataException("Invalid owned-process identity.");
            }).ToArray() : [];

    internal static bool Alive(OwnedIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return process.StartTime.ToUniversalTime().Ticks == identity.CreationUtcTicks && !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    internal static void KillRecorded(OwnedIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.StartTime.ToUniversalTime().Ticks != identity.CreationUtcTicks || process.HasExited) return;
            // Only the individually recorded test process, never a name-based or unverified tree kill.
            process.Kill();
            if (!process.WaitForExit(5000)) throw new IOException($"Owned process did not exit: {identity}");
        }
        catch (ArgumentException) { }
    }

    internal static async Task WaitForRoles(string source, params string[] roles)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!roles.All(role => Read(source).Any(p => p.Role == role)))
            await Task.Delay(20, deadline.Token);
    }

    internal static async Task AssertReaped(string source, params string[] roles)
    {
        await WaitForRoles(source, roles);
        var identities = Read(source);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (identities.Any(Alive)) await Task.Delay(20, deadline.Token);
        Check.That(identities.All(p => !Alive(p)), "Main and all recorded descendants must be gone before completion.");
    }

    internal static void EmergencyCleanup(string source)
    {
        foreach (var identity in Read(source)) KillRecorded(identity);
    }
}
