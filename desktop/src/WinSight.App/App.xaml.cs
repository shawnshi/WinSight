using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinSight.App.Services;
using WinSight.App.Views;
using WinSight.Core;
using WinSight.Core.Diagnostics;
using WinSight.Infrastructure.Runtime;

namespace WinSight.App;

public partial class App : Application
{
    private readonly string customDataDir;
    private readonly bool isSmokeTest;
    private readonly bool isToggle;
    private readonly bool isAutostart;

    private IDesktopBackend? backend;
    private MainWindow? mainWindow;
    private QuickSearchWindow? quickSearchWindow;
    private HotkeyService? hotkeyService;
    private TrayIconService? trayIconService;
    private SingleInstanceService? singleInstanceService;
    private DispatcherQueue? dispatcherQueue;
    private int isQuitting;

    public App() : this(string.Empty, false, false, null) { }

    public App(string dataDir, bool smokeTest, bool toggle, SingleInstanceService? singleInstance = null, bool autostart = false)
    {
        customDataDir = dataDir;
        isSmokeTest = smokeTest;
        isToggle = toggle;
        isAutostart = autostart;
        singleInstanceService = singleInstance;
        // Log first: an unhandled UI exception ends the process, and without this it would leave no trace at all.
        UnhandledException += (_, args) => AppLog.WriteCrashReport(args.Exception, "WinUI.UnhandledException: " + args.Message);
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        // Enable subtle Fluent interaction audio feedback
        ElementSoundPlayer.State = ElementSoundPlayerState.On;

        try
        {
            // NM-UI-01: Pass validated absolute dataDirectory to DesktopRuntime factory
            backend = await DesktopRuntime.CreateAsync(customDataDir);

            var settings = await backend.LoadSettingsAsync();
            AppLog.Info($"started; autostart={isAutostart}; smoke={isSmokeTest}; data={customDataDir}");

            ApplyAutostart(settings.OpenAtLogin);
            if (backend is DesktopBackend concrete)
                concrete.SettingsSaved += saved =>
                {
                    ApplyAutostart(saved.OpenAtLogin);
                    _ = ProvisionExecutionProvidersAsync(saved.InstallExecutionProviders);
                };
            // Downloads components over the network, so it is gated by the user's setting and backs off after failures.
            _ = Task.Run(() => ProvisionExecutionProvidersAsync(settings.InstallExecutionProviders));

            hotkeyService = new HotkeyService(settings.Hotkey);
            mainWindow = new MainWindow(backend, settings, hotkeyService, customDataDir);
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
            else if (!isAutostart)
            {
                // A manual launch always shows the window; only the login item starts quietly in the tray.
                mainWindow.ShowAndActivate();
            }
        }
        catch (Exception ex)
        {
            AppLog.WriteCrashReport(ex, "launch failure");
            Console.Error.WriteLine($"[FATAL] WinSight application failed to launch: {ex}");
            if (isSmokeTest)
            {
                await QuitAppAsync(exitCode: 1);
                return;
            }
            // Without this a failed start (including a login-item start) is invisible: no window and no tray icon.
            FatalErrorDialog.Show("WinSight 无法启动 / WinSight could not start", ex.GetBaseException().Message);
            Environment.ExitCode = 1;
            Exit();
        }
    }

    /// <summary>
    /// Brings the login item in line with the setting. The command is rewritten on every start so a moved or upgraded
    /// installation does not leave a stale path behind.
    /// </summary>
    private void ApplyAutostart(bool enabled)
    {
        if (isSmokeTest || Environment.ProcessPath is not { } executable) return;
        new AutostartRegistration().Apply(enabled, AutostartRegistration.CommandFor(executable, customDataDir, CommandLineParser.DefaultDataDirectory));
    }

    /// <summary>Runs the network-using provisioning only when the user allowed it; failures back off and never block the UI.</summary>
    private async Task ProvisionExecutionProvidersAsync(bool allowed)
    {
        if (isSmokeTest || !allowed) return;
        try
        {
            var host = Path.Combine(AppContext.BaseDirectory, "foundry", "WinSight.FoundryHost.exe");
            if (!File.Exists(host)) return;
            var outcome = await new ExecutionProviderProvisioner(customDataDir, host).RunAsync(allowed);
            AppLog.Info($"execution-provider provisioning: {outcome.Status} - {outcome.Detail}");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Error("execution-provider provisioning crashed", error);
        }
    }

    private async Task RunSmokeTestAsync() => await QuitAppAsync(await SmokeTest.RunAsync(backend!, mainWindow!, trayIconService!));

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
                AppLog.Error("error disposing backend", ex);
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
