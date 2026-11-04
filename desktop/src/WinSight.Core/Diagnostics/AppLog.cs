using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WinSight.Core.Diagnostics;

/// <summary>
/// Process-wide rolling file log under <c>&lt;data&gt;\logs</c>. The GUI process has no console, so anything written to
/// stderr is lost; this is the only durable record of what the application did. Every method is safe to call before
/// <see cref="Initialize"/> (it is then a no-op) and never throws: logging must not be able to fail the application.
/// </summary>
public static class AppLog
{
    internal const long MaxFileBytes = 2L * 1024 * 1024;
    internal const int KeepRolledFiles = 8;
    internal const int KeepCrashReports = 10;
    private const int MaxMessageChars = 8 * 1024;
    private static readonly object Gate = new();
    private static string? directory;
    private static string component = "app";
    private static int handlersInstalled;
    private static int crashSequence;

    public static string? Directory => Volatile.Read(ref directory);

    /// <summary>Creates the log directory and starts logging. <paramref name="componentName"/> separates app, cli and bridge files.</summary>
    public static void Initialize(string dataDirectory, string componentName = "app")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory)) return;
            var logs = Path.Combine(dataDirectory, "logs");
            System.IO.Directory.CreateDirectory(logs);
            lock (Gate) { directory = logs; component = Sanitize(componentName); }
            Info($"log started; version={AppVersion()}; pid={Environment.ProcessId}; os={RuntimeInformation.OSDescription}; runtime={RuntimeInformation.FrameworkDescription}");
        }
        catch (Exception error) when (IsLoggingFailure(error)) { lock (Gate) directory = null; }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? error = null) => Write("WARN", message, error);
    public static void Error(string message, Exception? error = null) => Write("ERROR", message, error);

    /// <summary>Records unhandled .NET and unobserved task exceptions; fatal ones also leave a crash report.</summary>
    public static void InstallGlobalHandlers()
    {
        if (Interlocked.Exchange(ref handlersInstalled, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var error = args.ExceptionObject as Exception ?? new Exception(Convert.ToString(args.ExceptionObject) ?? "unknown");
            WriteCrashReport(error, "AppDomain.UnhandledException (terminating=" + args.IsTerminating + ")");
        };
        TaskScheduler.UnobservedTaskException += (_, args) => Error("Unobserved task exception", args.Exception);
    }

    /// <summary>Writes the exception to the log and to a stand-alone <c>crash-*.txt</c> file; returns its path when written.</summary>
    public static string? WriteCrashReport(Exception error, string source)
    {
        Error("FATAL " + source, error);
        try
        {
            var logs = Directory;
            if (logs is null) return null;
            // The sequence keeps two crashes in the same millisecond from overwriting each other.
            var path = Path.Combine(logs, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Interlocked.Increment(ref crashSequence):D3}.txt");
            var text = new StringBuilder()
                .AppendLine("WinSight crash report")
                .AppendLine("time (UTC): " + DateTime.UtcNow.ToString("O"))
                .AppendLine("source: " + source)
                .AppendLine("version: " + AppVersion())
                .AppendLine("os: " + RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture)
                .AppendLine("runtime: " + RuntimeInformation.FrameworkDescription)
                .AppendLine("process uptime: " + (DateTime.Now - Process.GetCurrentProcess().StartTime))
                .AppendLine()
                .AppendLine(error.ToString())
                .ToString();
            File.WriteAllText(path, text, new UTF8Encoding(false));
            Prune(logs, "crash-*.txt", KeepCrashReports);
            return path;
        }
        catch (Exception failure) when (IsLoggingFailure(failure)) { return null; }
    }

    internal static string AppVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    internal static void ResetForTests() { lock (Gate) { directory = null; component = "app"; } }

    private static void Write(string level, string message, Exception? error)
    {
        try
        {
            string? logs; string name;
            lock (Gate) { logs = directory; name = component; }
            if (logs is null) return;
            var text = message.Length > MaxMessageChars ? message[..MaxMessageChars] + " ...[truncated]" : message;
            var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{level}] [{Environment.ProcessId}:{Environment.CurrentManagedThreadId}] {text.ReplaceLineEndings(" | ")}";
            if (error is not null) line += Environment.NewLine + error;
            lock (Gate)
            {
                var file = Path.Combine(logs, $"winsight-{name}.log");
                if (File.Exists(file) && new FileInfo(file).Length >= MaxFileBytes) Roll(logs, name, file);
                File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception failure) when (IsLoggingFailure(failure)) { /* a log failure is never an application failure */ }
    }

    private static void Roll(string logs, string name, string file)
    {
        var rolled = Path.Combine(logs, $"winsight-{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.log");
        File.Move(file, rolled);
        Prune(logs, $"winsight-{name}-*.log", KeepRolledFiles);
    }

    private static void Prune(string logs, string pattern, int keep)
    {
        foreach (var stale in new DirectoryInfo(logs).GetFiles(pattern).OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(keep))
            try { stale.Delete(); } catch (Exception error) when (IsLoggingFailure(error)) { }
    }

    private static string Sanitize(string value) =>
        new string(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray()) is { Length: > 0 } clean ? clean : "app";

    private static bool IsLoggingFailure(Exception error) => error is IOException or UnauthorizedAccessException or InvalidOperationException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
