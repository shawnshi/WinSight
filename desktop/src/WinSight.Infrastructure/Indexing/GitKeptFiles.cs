using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WinSight.Infrastructure.Extraction;

namespace WinSight.Infrastructure.Indexing;

/// <summary>One bounded, read-only Git snapshot per repository in an indexing request.</summary>
internal sealed class GitKeptFiles
{
    internal const int MaxOutputBytes = 32 * 1024 * 1024;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, Repository> repositories = new(StringComparer.OrdinalIgnoreCase);
    internal sealed record Repository(string Root, HashSet<string> Kept)
    {
        internal bool Keeps(string path) => Kept.Contains(path);
    }

    internal static bool HasMarker(string folder, out bool directory)
    {
        directory = false;
        try
        {
            var attributes = File.GetAttributes(Path.Combine(folder, ".git"));
            if (FilePolicy.Unsafe(attributes)) throw new IOException("Unsafe Git metadata marker; code scope cannot be established.");
            directory = (attributes & FileAttributes.Directory) != 0;
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal async Task<Repository?> FindAsync(string folder, CancellationToken token)
    {
        var count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (++count > 256) throw new IOException("Git ancestor discovery exceeds 256 components.");
            if (HasMarker(folder, out _)) return await LoadAsync(folder, token);
            var parent = Path.GetDirectoryName(folder);
            if (parent is null || parent == folder) return null;
            folder = parent;
        }
    }

    internal async Task<Repository> LoadAsync(string root, CancellationToken token)
    {
        if (repositories.TryGetValue(root, out var cached)) return cached;
        var executable = FindExecutable() ?? throw new IOException("Git executable unavailable; repository code indexing is disabled (fail closed).");
        using var rootLease = SafeFile.Open(root, true);
        var marker = Path.Combine(root, ".git");
        if (!HasMarker(root, out var directory)) throw new IOException("Git repository marker disappeared.");
        using var markerLease = SafeFile.Open(marker, directory);
        var start = CreateStartInfo(executable, root);
        start.ArgumentList.Add("ls-files");
        foreach (var argument in new[] { "-z", "--cached", "--others", "--exclude-standard" }) start.ArgumentList.Add(argument);
        var bytes = await RunAsync(start, token);
        if (bytes.Length != 0 && bytes[^1] != 0) throw new IOException("Git kept-file output is not NUL-terminated.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException error) { throw new IOException("Git kept-file output is not valid UTF-8.", error); }
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = 0;
        foreach (var relative in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            if (++entries > 250000 || relative.Length > 32768 || Path.IsPathRooted(relative)
                || relative.Split('/').Any(part => part is "." or ".."))
                throw new IOException("Git kept-file output exceeds path/count bounds or contains an unsafe path.");
            var path = FilePolicy.Normalize(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!FilePolicy.Inside(path, root)) throw new IOException("Git kept-file path escaped repository scope.");
            kept.Add(path);
        }
        var result = new Repository(root, kept);
        repositories.Add(root, result);
        return result;
    }

    internal static string? FindExecutable()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var path = folder.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            var candidate = Path.Combine(path, "git.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string root)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // NM-BR-03 / frozen Crawler.swift GitFiles: local config must never launch a monitor, hook or pager.
        foreach (var argument in new[] { "--no-pager", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL", "-c", "core.untrackedCache=false", "-c", "protocol.allow=never", "-C", root })
            start.ArgumentList.Add(argument);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        start.Environment.Clear();
        if (systemRoot is not null) start.Environment["SystemRoot"] = systemRoot;
        start.Environment["PATH"] = Path.GetDirectoryName(executable)!;
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_SYSTEM"] = "NUL";
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_CONFIG_COUNT"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        return start;
    }

    internal static async Task<byte[]> RunAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("Git process did not start.");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { throw new IOException("Git process unavailable; repository code indexing is disabled (fail closed).", error); }
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(Timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var stdout = PumpAsync(process.StandardOutput.BaseStream, MaxOutputBytes, true, stop);
        var stderr = PumpAsync(process.StandardError.BaseStream, 65536, false, stop);
        var completion = Task.WhenAll(stdout, stderr, process.WaitForExitAsync(stop.Token));
        // Anonymous pipe reads may not abort until the child closes its handles; cancellation must reach kill/reap first.
        _ = completion.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await completion.WaitAsync(stop.Token);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException($"Git kept-file listing failed (exit {process.ExitCode}); no code scope was adopted.");
            return await stdout;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            if (stdout.IsFaulted || stderr.IsFaulted)
                throw new IOException("Git kept-file output exceeded its byte bound or could not be read.", (stdout.Exception ?? stderr.Exception)!.GetBaseException());
            if (timeout.IsCancellationRequested) throw new IOException("Git kept-file listing exceeded the 10-second deadline.", error);
            if (error is OperationCanceledException)
                throw new IOException("Git kept-file output exceeded its byte bound or could not be read.", error);
            throw;
        }
        finally
        {
            stop.Cancel();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(cleanup.Token); }
                catch (OperationCanceledException error) { throw new IOException("Git process could not be reaped within the cleanup deadline.", error); }
            }
        }
    }

    private static async Task<byte[]> PumpAsync(Stream stream, int limit, bool retain, CancellationTokenSource stop)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        var total = 0;
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, stop.Token);
                if (count == 0) return output.ToArray();
                total += count;
                if (total > limit) throw new IOException("Git output byte limit exceeded.");
                if (retain) output.Write(buffer, 0, count);
            }
        }
        catch { stop.Cancel(); throw; }
    }
}
