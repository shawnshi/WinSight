using WinSight.App.Services;
using WinSight.App.Views;
using WinSight.Core;
using WinSight.Core.Diagnostics;

namespace WinSight.App;

/// <summary>
/// The <c>--smoke-test</c> check used by CI and the UI harness. It lives outside the composition root so the production
/// startup path stays free of test logic; it only runs when that flag is given.
/// </summary>
internal static class SmokeTest
{
    /// <summary>Returns the process exit code: 0 passed, 2 failed.</summary>
    public static async Task<int> RunAsync(IDesktopBackend backend, MainWindow mainWindow, TrayIconService tray)
    {
        try
        {
            await mainWindow.ViewModel.InitializeAsync();
            await mainWindow.ViewModel.ToggleIndexingPauseAsync();
            var paused = await backend.GetStatusAsync();
            if (paused.IndexingState != IndexingControlState.Paused || mainWindow.ViewModel.CanIndexNow
                || tray.PauseMenuText != "Resume indexing" || !tray.IsPauseMenuEnabled)
                throw new InvalidOperationException("Main-window/tray pause state did not converge to the real backend.");
            var searchRes = await backend.SearchAsync(new SearchRequest("smoke-test", Limit: 10, Mode: SearchMode.Keyword));
            await tray.ToggleIndexingPauseAsync();
            var status = await backend.GetStatusAsync();
            if (status.IndexingState != IndexingControlState.Running
                || mainWindow.ViewModel.IndexingState != IndexingControlState.Running
                || tray.PauseMenuText != "Pause indexing")
                throw new InvalidOperationException("Tray resume did not converge to the real main-window/backend state.");
            Console.WriteLine("[SMOKE-PAUSE] Main-window pause, keyword search while paused, and shared tray resume passed.");
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var modules = process.Modules.Cast<System.Diagnostics.ProcessModule>()
                .Where(module => module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)
                    || module.ModuleName.Equals("Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(module => module.ModuleName, module => module.FileName, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine("[SMOKE-RUNTIME] " + System.Text.Json.JsonSerializer.Serialize(new
            {
                coreLib = typeof(object).Assembly.Location,
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                modules
            }));
            Console.WriteLine($"[SMOKE-TEST] Status: {status.Files} files, {status.Done} indexed. Search hits: {searchRes.Hits.Count} in {searchRes.Elapsed.TotalMilliseconds:F1} ms.");
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("smoke test failed", ex);
            Console.Error.WriteLine($"[SMOKE-TEST-ERROR] {ex}");
            return 2;
        }
    }
}
