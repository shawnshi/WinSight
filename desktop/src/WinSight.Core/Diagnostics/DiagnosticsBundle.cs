using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace WinSight.Core.Diagnostics;

/// <summary>
/// Builds a support bundle: logs, crash reports, settings and a data-directory listing. Document and index content is never
/// included. User-identifying path fragments (profile directory, account name, machine name) are replaced in every text entry.
/// </summary>
public static class DiagnosticsBundle
{
    private const long MaxEntryBytes = 8L * 1024 * 1024;
    private const long MaxBundleBytes = 24L * 1024 * 1024;

    public static string Create(string dataDirectory, string? destination = null, IReadOnlyDictionary<string, string>? extra = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("Data directory must be absolute.");
        destination ??= Path.Combine(dataDirectory, "diagnostics", $"winsight-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long total = 0;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Add(string name, string text)
                {
                    var bytes = Encoding.UTF8.GetBytes(Redact(text));
                    if (bytes.Length > MaxEntryBytes) bytes = bytes[^(int)MaxEntryBytes..];   // keep the newest part of an oversized log
                    if (total + bytes.Length > MaxBundleBytes) return;
                    total += bytes.Length;
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var output = entry.Open();
                    output.Write(bytes);
                }
                Add("README.txt", "WinSight diagnostics bundle. Contains logs, crash reports, settings and a file listing only; no document or index content. Paths are redacted.");
                Add("environment.txt", $"collected: {DateTime.UtcNow:O}{Environment.NewLine}version: {AppLog.AppVersion()}{Environment.NewLine}os: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}{Environment.NewLine}runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}{Environment.NewLine}processors: {Environment.ProcessorCount}{Environment.NewLine}");
                foreach (var name in new[] { "settings.json", "foundry-provision.json" })
                    if (ReadShared(Path.Combine(dataDirectory, name)) is { } text) Add(name, text);
                var logs = Path.Combine(dataDirectory, "logs");
                if (Directory.Exists(logs))
                    foreach (var file in new DirectoryInfo(logs).GetFiles().Where(f => f.Extension is ".log" or ".txt").OrderByDescending(f => f.LastWriteTimeUtc))
                        if (ReadShared(file.FullName) is { } text) Add("logs/" + file.Name, text);
                Add("data-directory.txt", Listing(dataDirectory));
                if (extra is not null) foreach (var (name, text) in extra) Add(Path.GetFileName(name), text);
            }
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Replaces the profile directory, account name and machine name with neutral tokens.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            foreach (var form in new[] { profile, profile.Replace('\\', '/'), profile.Replace("\\", "\\\\") })
                text = text.Replace(form, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        foreach (var (value, token) in new[] { (Environment.UserName, "<user>"), (Environment.MachineName, "<machine>") })
            if (value.Length >= 3) text = Regex.Replace(text, @"(?<![\p{L}\p{N}_])" + Regex.Escape(value) + @"(?![\p{L}\p{N}_])", token, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        return text;
    }

    private static string? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Listing(string dataDirectory)
    {
        var text = new StringBuilder();
        try
        {
            foreach (var entry in new DirectoryInfo(dataDirectory).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(200))
                text.AppendLine(entry is FileInfo file ? $"{file.Length,14:N0}  {file.LastWriteTimeUtc:u}  {file.Name}" : $"{"<dir>",14}  {entry.LastWriteTimeUtc:u}  {entry.Name}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { text.AppendLine("listing failed: " + error.GetType().Name); }
        return text.ToString();
    }
}
