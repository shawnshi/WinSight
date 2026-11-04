using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinSight.Core.Diagnostics;
using WinSight.Infrastructure.Inference;

namespace WinSight.Infrastructure.Runtime;

public enum ProvisionStatus { Disabled, AlreadyDone, Deferred, HostMissing, Succeeded, Failed }

public sealed record ProvisionOutcome(ProvisionStatus Status, string Detail);

/// <summary>
/// One-shot registration of the isolated Foundry Local execution providers. It downloads components from the network,
/// so it runs only when the user allowed it, is bounded (the child is killed on timeout and bound to the parent's job),
/// and backs off after failures instead of retrying for ten minutes on every launch.
/// </summary>
public sealed class ExecutionProviderProvisioner
{
    public delegate Task<(int ExitCode, string Output)> HostRunner(string hostPath, CancellationToken cancellationToken);

    internal const string StateFile = "foundry-provision.json";
    private const string LegacyMarker = "foundry-provision.done";
    private readonly string dataDirectory, hostPath;
    private readonly HostRunner runner;
    private readonly TimeSpan timeout;
    private readonly Func<DateTimeOffset> clock;
    private readonly string[] hostArguments;

    public ExecutionProviderProvisioner(string dataDirectory, string hostPath, HostRunner? runner = null, TimeSpan? timeout = null, Func<DateTimeOffset>? clock = null,
        IReadOnlyList<string>? hostArguments = null)
    {
        this.hostArguments = hostArguments?.ToArray() ?? ["register", "--allow-download"];
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("Data directory must be absolute.", nameof(dataDirectory));
        this.dataDirectory = dataDirectory;
        this.hostPath = hostPath;
        this.timeout = timeout ?? TimeSpan.FromMinutes(10);
        this.runner = runner ?? RunHostAsync;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Wait before the next attempt after <paramref name="failures"/> consecutive failures.</summary>
    public static TimeSpan Backoff(int failures) => failures switch { <= 0 => TimeSpan.Zero, 1 => TimeSpan.FromHours(1), 2 => TimeSpan.FromHours(6), _ => TimeSpan.FromHours(24) };

    public async Task<ProvisionOutcome> RunAsync(bool allowed, CancellationToken cancellationToken = default)
    {
        if (!allowed) return new(ProvisionStatus.Disabled, "Execution-provider installation is not allowed in settings; nothing was downloaded.");
        var state = Load();
        if (state.Done) return new(ProvisionStatus.AlreadyDone, "Already provisioned.");
        var wait = Backoff(state.Failures);
        if (state.LastAttemptUtc is { } last && clock() - last < wait)
            return new(ProvisionStatus.Deferred, $"Previous attempt failed {state.Failures} time(s); next attempt after {last + wait:O}.");
        if (!File.Exists(hostPath))
        {
            AppLog.Info("execution-provider provisioning skipped: host absent");
            return new(ProvisionStatus.HostMissing, "Foundry host is not part of this installation.");
        }
        AppLog.Info("execution-provider provisioning started");
        string detail; int exit;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try { (exit, detail) = await runner(hostPath, limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { exit = -1; detail = "timed out after " + timeout; }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            exit = -2; detail = error.GetType().Name + ": " + error.Message;
        }
        var succeeded = exit == 0;
        Save(new State(succeeded, succeeded ? 0 : state.Failures + 1, clock(), succeeded ? null : Truncate(detail)));
        if (succeeded) AppLog.Info("execution-provider provisioning succeeded"); else AppLog.Warn($"execution-provider provisioning failed (exit {exit}): {Truncate(detail)}");
        return succeeded ? new(ProvisionStatus.Succeeded, "Provisioned.") : new(ProvisionStatus.Failed, $"exit {exit}: {Truncate(detail)}");
    }

    private async Task<(int, string)> RunHostAsync(string host, CancellationToken token)
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in hostArguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Foundry host did not start.");
        // The job kills the whole tree if this process dies; the explicit kill below covers a timeout.
        using var job = OperatingSystem.IsWindows() ? WindowsChildJob.Attach(process) : null;
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }

    private sealed record State(bool Done, int Failures, DateTimeOffset? LastAttemptUtc, string? LastError);

    private State Load()
    {
        if (File.Exists(Path.Combine(dataDirectory, LegacyMarker))) return new(true, 0, null, null);
        try
        {
            var path = Path.Combine(dataDirectory, StateFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new(false, 0, null, null) : new(false, 0, null, null);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return new(false, 0, null, null); }
    }

    private void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, StateFile);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppLog.Warn("could not persist provisioning state", error); }
    }

    private static string Truncate(string value) => value.Length > 2000 ? value[..2000] + "..." : value;
}
