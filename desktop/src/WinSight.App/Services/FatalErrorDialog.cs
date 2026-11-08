using System.Runtime.InteropServices;
using WinSight.Core.Diagnostics;

namespace WinSight.App.Services;

/// <summary>
/// The GUI process has no console, so a failed start would otherwise be a silent exit: no window, no tray icon, nothing.
/// This shows one modal box with the reason and where the log is, then lets the process end.
/// </summary>
internal static class FatalErrorDialog
{
    private const uint MB_OK = 0x0, MB_ICONERROR = 0x10, MB_SETFOREGROUND = 0x10000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    internal static string Compose(string summary, string? logDirectory)
    {
        var text = summary.Length > 600 ? summary[..600] + "…" : summary;
        return logDirectory is null
            ? text
            : text + Environment.NewLine + Environment.NewLine + "详细信息已写入日志目录 / Details were written to:" + Environment.NewLine + logDirectory;
    }

    /// <summary>Shows the dialog; never throws, because it runs on the failure path.</summary>
    internal static void Show(string title, string summary)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || Environment.UserInteractive is false) return;
            _ = MessageBox(IntPtr.Zero, Compose(summary, AppLog.Directory), title, MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
    }
}
