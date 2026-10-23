using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

if (args.Length != 3 || !int.TryParse(args[0], out var pid))
    throw new ArgumentException("Supply task App PID, exact deployed executable, and task report path.");
var boundary = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".dev/native-migration/deployment-checks")) + Path.DirectorySeparatorChar;
var expectedExe = Path.GetFullPath(args[1]);
var reportFile = Path.GetFullPath(args[2]);
if (!expectedExe.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
    || !reportFile.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
    throw new UnauthorizedAccessException("Probe inputs must remain in the deployment-check task directory.");
var checks = new List<object>();
var phase = "owned-process";
var exitCode = 0;
string? error = null;
IntPtr window = IntPtr.Zero;
System.Windows.Rect originalBounds = default;
using var process = Process.GetProcessById(pid);
try
{
    if (!string.Equals(Path.GetFullPath(process.MainModule!.FileName), expectedExe, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("PID does not identify the explicitly deployed task App.");
    phase = "visible-main-window";
    Wait(() => { process.Refresh(); window = process.MainWindowHandle; return window != IntPtr.Zero && Native.IsWindowVisible(window); });
    var root = AutomationElement.FromHandle(window);
    if (root.Current.ProcessId != pid || root.Current.IsOffscreen) throw new InvalidOperationException("Window ownership/visibility not confirmed.");
    originalBounds = root.Current.BoundingRectangle;
    checks.Add(new { name = "visible-owned-window", passed = true, width = originalBounds.Width, height = originalBounds.Height });
    phase = "find-pause-button";
    AutomationElement? button = null;
    Wait(() => { button = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "PauseIndexingButton")); return button is not null && button.Current.IsEnabled && button.Current.Name == "Pause indexing"; });
    if (button!.Current.ProcessId != pid) throw new UnauthorizedAccessException("Button provider belongs to another process.");
    phase = "invoke-visible-pause-button";
    Invoke(button);
    Wait(() => button.Current.IsEnabled && button.Current.Name == "Resume indexing" && VisibleText(root, "Indexing paused"));
    checks.Add(new { name = "actual-pause-button-and-visible-state", passed = true });
    phase = "invoke-visible-resume-button";
    Invoke(button);
    Wait(() => button.Current.IsEnabled && button.Current.Name == "Pause indexing" && VisibleText(root, "Indexing allowed"));
    checks.Add(new { name = "actual-resume-button-and-visible-state", passed = true });
    foreach (var size in new[] { (Width: 800, Height: 600), (Width: 1100, Height: 760) })
    {
        phase = $"viewport-{size.Width}x{size.Height}";
        if (!Native.SetWindowPos(window, IntPtr.Zero, 0, 0, size.Width, size.Height, 0x0002 | 0x0004 | 0x0010))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Wait(() =>
        {
            var outer = root.Current.BoundingRectangle;
            var control = button.Current.BoundingRectangle;
            return Math.Abs(outer.Width - size.Width) <= 4 && Math.Abs(outer.Height - size.Height) <= 4
                && !button.Current.IsOffscreen && control.Width > 0 && control.Height > 0
                && control.Left >= outer.Left && control.Top >= outer.Top && control.Right <= outer.Right && control.Bottom <= outer.Bottom;
        });
        checks.Add(new { name = phase, passed = true, pauseButtonInsideWindow = true });
    }
}
catch (Exception exception)
{
    exitCode = 1;
    error = exception.GetType().Name + ": " + exception.Message;
}
finally
{
    if (window != IntPtr.Zero && originalBounds.Width > 0)
        Native.SetWindowPos(window, IntPtr.Zero, 0, 0, (int)originalBounds.Width, (int)originalBounds.Height, 0x0002 | 0x0004 | 0x0010);
}
var report = new
{
    passed = exitCode == 0, pid, target = expectedExe, phase, error, checks,
    scope = "Known PID and its window subtree only; no desktop traversal, screenshots, user files or global input.",
    limits = "Not independent approval, screen-reader/keyboard/theme/DPI certification or native tray popup selection.",
    targetLeftRunning = !process.HasExited
};
Directory.CreateDirectory(Path.GetDirectoryName(reportFile)!);
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(reportFile, json);
Console.WriteLine(json);
return exitCode;

static bool VisibleText(AutomationElement root, string text)
{
    var found = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, text));
    return found is not null && !found.Current.IsOffscreen;
}
static void Invoke(AutomationElement element)
{
    if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) || pattern is not InvokePattern invoke)
        throw new InvalidOperationException("Pause button does not expose InvokePattern.");
    invoke.Invoke();
}
static void Wait(Func<bool> condition)
{
    var clock = Stopwatch.StartNew();
    while (!condition())
    {
        if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Task-window state did not converge within 12 seconds.");
        Thread.Sleep(75);
    }
}
internal static class Native
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
