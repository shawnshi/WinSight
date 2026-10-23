using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinSight.App.Services;
using WinSight.App.Views;
using WinSight.Core;

namespace WinSight.App;

public partial class App : Application
{
    private readonly string customDataDir;
    private readonly bool isSmokeTest;
    private readonly bool isToggle;

    private IDesktopBackend? backend;
    private MainWindow? mainWindow;
    private QuickSearchWindow? quickSearchWindow;
    private HotkeyService? hotkeyService;
    private TrayIconService? trayIconService;
    private SingleInstanceService? singleInstanceService;
    private DispatcherQueue? dispatcherQueue;
    private int isQuitting;

    public App() : this(string.Empty, false, false, null) { }

    public App(string dataDir, bool smokeTest, bool toggle, SingleInstanceService? singleInstance = null)
    {
        customDataDir = dataDir;
        isSmokeTest = smokeTest;
        isToggle = toggle;
        singleInstanceService = singleInstance;
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        try
        {
            // NM-UI-01: Pass validated absolute dataDirectory to DesktopRuntime factory
            backend = await DesktopRuntime.CreateAsync(customDataDir);

            var settings = await backend.LoadSettingsAsync();

            hotkeyService = new HotkeyService(settings.Hotkey);
            mainWindow = new MainWindow(backend, settings, hotkeyService);
            trayIconService = new TrayIconService(mainWindow.ViewModel);
            quickSearchWindow = new QuickSearchWindow(backend, settings, customDataDir);

            // Wire single instance signals to window activation
            if (singleInstanceService != null)
            {
                singleInstanceService.ShowRequested += () =>
                {
                    dispatcherQueue?.TryEnqueue(() => mainWindow?.ShowAndActivate());
                };
                singleInstanceService.ToggleRequested += () =>
                {
                    dispatcherQueue?.TryEnqueue(() => quickSearchWindow?.TogglePanel());
                };
            }

            // Wire tray icon
            trayIconService.QuickSearchRequested += () => dispatcherQueue?.TryEnqueue(() => quickSearchWindow?.ShowAndFocus());
            trayIconService.MainWindowRequested += () => dispatcherQueue?.TryEnqueue(() => mainWindow?.ShowAndActivate());
            trayIconService.SettingsRequested += () => dispatcherQueue?.TryEnqueue(() =>
            {
                mainWindow?.ShowAndActivate();
                mainWindow?.NavigateTo("settings");
            });
            trayIconService.IndexNowRequested += () => dispatcherQueue?.TryEnqueue(() => _ = mainWindow?.ViewModel.TriggerIndexNowAsync());
            trayIconService.ExitRequested += () => dispatcherQueue?.TryEnqueue(QuitApp);
            trayIconService.LeftClicked += () => dispatcherQueue?.TryEnqueue(() =>
            {
                if (settings.TrayClick == "window") mainWindow?.ShowAndActivate();
                else quickSearchWindow?.TogglePanel();
            });

            // Wire hotkey
            hotkeyService.HotkeyPressed += () => dispatcherQueue?.TryEnqueue(() => quickSearchWindow?.TogglePanel());

            // Wire quick search to main window
            quickSearchWindow.OpenInFullWindowRequested += (q, kind) =>
            {
                mainWindow?.ShowAndActivate();
                mainWindow?.NavigateTo("search", q, kind);
            };

            if (isToggle)
            {
                quickSearchWindow.ShowAndFocus();
            }
            else if (isSmokeTest)
            {
                await RunSmokeTestAsync();
            }
            else if (!settings.OpenAtLogin)
            {
                mainWindow.ShowAndActivate();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] WinSight application failed to launch: {ex}");
            if (isSmokeTest)
            {
                await QuitAppAsync(exitCode: 1);
                return;
            }
            Environment.ExitCode = 1;
            Exit();
        }
    }

    private async Task RunSmokeTestAsync()
    {
        int exitCode = 0;
        try
        {
            await mainWindow!.ViewModel.InitializeAsync();
            await mainWindow.ViewModel.ToggleIndexingPauseAsync();
            var paused = await backend!.GetStatusAsync();
            if (paused.IndexingState != IndexingControlState.Paused || mainWindow.ViewModel.CanIndexNow
                || trayIconService!.PauseMenuText != "Resume indexing" || !trayIconService.IsPauseMenuEnabled)
                throw new InvalidOperationException("Main-window/tray pause state did not converge to the real backend.");
            var searchRes = await backend.SearchAsync(new SearchRequest("smoke-test", Limit: 10, Mode: SearchMode.Keyword));
            await trayIconService.ToggleIndexingPauseAsync();
            var status = await backend.GetStatusAsync();
            if (status.IndexingState != IndexingControlState.Running
                || mainWindow.ViewModel.IndexingState != IndexingControlState.Running
                || trayIconService.PauseMenuText != "Pause indexing")
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
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SMOKE-TEST-ERROR] {ex}");
            exitCode = 2;
        }
        finally
        {
            await QuitAppAsync(exitCode);
        }
    }

    public void QuitApp() => _ = QuitAppAsync();

    public async Task QuitAppAsync(int exitCode = 0)
    {
        if (Interlocked.Exchange(ref isQuitting, 1) != 0) return;

        Environment.ExitCode = exitCode;
        mainWindow?.ViewModel.StopOperations();

        singleInstanceService?.Dispose();
        singleInstanceService = null;

        hotkeyService?.Dispose();
        hotkeyService = null;

        trayIconService?.Dispose();
        trayIconService = null;

        if (backend != null)
        {
            try
            {
                await backend.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error disposing backend: {ex.Message}");
            }
            backend = null;
        }

        mainWindow?.Quit();
        mainWindow = null;

        // NM-UI-08: Explicit Quit on QuickSearchWindow
        quickSearchWindow?.Quit();
        quickSearchWindow = null;

        Exit();
    }
}
