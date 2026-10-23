using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinSight.MediaProcess.Tests;

internal sealed class TestRun
{
    internal string Root { get; }
    internal string FakeHost { get; }
    internal string? Host { get; }
    internal string? Filter { get; }
    private readonly List<object> results = [];
    internal int Failures { get; private set; }
    private int executed;
    internal int ExitCode => Failures == 0 && executed > 0 ? 0 : 1;

    internal TestRun(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2) options.Add(args[i], args[i + 1]);
        Root = options["--artifact-root"];
        FakeHost = options["--fake-host"];
        Host = options.GetValueOrDefault("--inference-host"); Filter = options.GetValueOrDefault("--case");
        if (!Path.IsPathFullyQualified(Root) || Directory.Exists(Root) || File.Exists(Root)) throw new ArgumentException("Explicit new task root required.");
        if (!Path.IsPathFullyQualified(FakeHost) || !File.Exists(FakeHost)) throw new ArgumentException("Explicit built fake executable required.");
        Directory.CreateDirectory(Root);
    }

    internal async Task Case(string name, Func<string, Task> body)
    {
        if (Filter is not null && !name.Contains(Filter, StringComparison.Ordinal)) return;
        executed++;
        // Logical names stay in receipts; short physical case roots keep fault probes below Win32 path limits.
        var directory = Path.Combine(Root, $"c{executed:D3}"); Directory.CreateDirectory(directory);
        var clock = Stopwatch.StartNew();
        try
        {
            await body(directory);
            results.Add(new { name, directory, status = "passed", milliseconds = clock.ElapsedMilliseconds });
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception error)
        {
            Failures++;
            results.Add(new { name, directory, status = "failed", milliseconds = clock.ElapsedMilliseconds, error = error.ToString() });
            Console.Error.WriteLine($"FAIL {name}: {error}");
        }
        finally
        {
            var sources = Directory.GetFiles(directory, "*.wav", SearchOption.AllDirectories);
            foreach (var source in sources) OwnedProcesses.EmergencyCleanup(source);
            Save();
        }
    }

    internal void NotRun(string name, string reason)
    {
        results.Add(new { name, status = "not-run", reason }); Console.WriteLine($"NOT-RUN {name}: {reason}"); Save();
    }

    private void Save() => File.WriteAllText(Path.Combine(Root, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

    internal static string Source(string directory, string mode = "ok")
    {
        var path = Path.Combine(directory, mode + "_synthetic.wav"); FixtureMedia.Wave(path, 4);
        File.WriteAllText(path + ".sha256", Hash(path));
        return path;
    }

    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static async Task<(int Code, byte[] Stdout, byte[] Stderr)> Invoke(string executable, string[] args, string evidence)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Test child creation failed.");
        var identity = new OwnedIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, "fixture-child");
        File.WriteAllText(evidence + ".identity.json", JsonSerializer.Serialize(identity));
        using var output = new MemoryStream(); using var error = new MemoryStream();
        var outTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errTask = process.StandardError.BaseStream.CopyToAsync(error);
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(2));
            return (process.ExitCode, output.ToArray(), error.ToArray());
        }
        finally
        {
            OwnedProcesses.KillRecorded(identity);
            File.WriteAllBytes(evidence + ".stdout.bin", output.ToArray());
            File.WriteAllBytes(evidence + ".stderr.bin", error.ToArray());
        }
    }
}
