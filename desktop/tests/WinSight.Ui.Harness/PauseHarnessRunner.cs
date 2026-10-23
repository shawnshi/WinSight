using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.App.Views;
using WinSight.Core;

namespace WinSight.Ui.Harness;

public static class PauseHarnessRunner
{
    public static async Task<TestSuiteReport> RunAsync(string tempDir, bool render, Action<string>? log = null)
    {
        var results = new List<TestResult>();
        async Task Check(string name, Func<Task> test)
        {
            log?.Invoke($"START {name}");
            var clock = Stopwatch.StartNew();
            try
            {
                await test();
                results.Add(new(name, true, "Assertions passed", clock.Elapsed));
            }
            catch (Exception ex)
            {
                results.Add(new(name, false, ex.ToString(), clock.Elapsed));
            }
            log?.Invoke($"{(results[^1].Passed ? "PASS" : "FAIL")} {name}: {results[^1].Details}");
        }

        await Check("P4_DelayedControlAndStatus_NoPrematurePaused_Duplicate_Search_NoSettingsSave", async () =>
        {
            await using var backend = new SyntheticDesktopBackend(tempDir);
            var vm = new MainWindowViewModel(backend);
            await vm.InitializeAsync();
            var before = await backend.LoadSettingsAsync();
            var control = Gate();
            var statusGate = Gate();
            backend.PauseControlHandler = (_, ct) => control.Task.WaitAsync(ct);
            backend.StatusHandler = async ct =>
            {
                await statusGate.Task.WaitAsync(ct);
                return Status(tempDir, backend.IndexingState);
            };
            var pause = vm.ToggleIndexingPauseAsync();
            Require(backend.RecordedPauseRequests.SequenceEqual([true]), "Missing actual pause API call");
            Require(backend.IndexingState == IndexingControlState.Pausing, "Synthetic state must be Pausing while draining");
            Require(vm.PauseActionText == "Pausing…" && !vm.CanToggleIndexingPause && !vm.CanIndexNow, "Busy controls");
            Require(vm.IndexingState != IndexingControlState.Paused && vm.IndexingStateText != "Indexing paused", "Premature paused state");
            await vm.ToggleIndexingPauseAsync();
            await vm.TriggerIndexNowAsync();
            Require(backend.RecordedPauseRequests.Count == 1 && backend.IndexCallCount == 0, "Duplicate control/index dispatch");
            control.SetResult();
            await Task.Yield();
            Require(vm.IsPauseControlBusy && vm.IndexingState != IndexingControlState.Paused, "Must wait for actual status completion");
            statusGate.SetResult();
            await pause.WaitAsync(TimeSpan.FromSeconds(5));
            Require(vm.IndexingState == IndexingControlState.Paused && vm.PauseActionText == "Resume indexing", "Paused state not confirmed");
            var search = new QuickSearchViewModel(backend);
            search.Query = "synthetic paused query";
            await Until(() => !search.IsLoading && search.Hits.Count > 0);
            Require(backend.SearchCallCount > 0 && search.Hits.Count > 0, "Paused search unusable");
            backend.PauseControlHandler = null;
            backend.StatusHandler = null;
            await vm.ToggleIndexingPauseAsync();
            Require(backend.RecordedPauseRequests.SequenceEqual([true, false]) && vm.CanIndexNow, "Missing actual resume API call");
            var after = await backend.LoadSettingsAsync();
            Require(backend.SaveSettingsCount == 0 && before.Revision == after.Revision
                && before.BackgroundIndexingEnabled == after.BackgroundIndexingEnabled, "Pause must not save background settings");
        });

        await Check("P4_PauseAndResumeFailure_RefreshActualPausingOrPaused", async () =>
        {
            await using var backend = new SyntheticDesktopBackend(tempDir);
            var vm = new MainWindowViewModel(backend);
            await vm.InitializeAsync();
            backend.PauseControlHandler = (_, _) => throw new IOException("synthetic drain timeout");
            var reads = backend.StatusCallCount;
            await vm.ToggleIndexingPauseAsync();
            Require(backend.StatusCallCount > reads && vm.IndexingState == IndexingControlState.Pausing, "Failure must refresh real Pausing");
            Require(vm.StatusMessage.Contains("synthetic drain timeout") && vm.IndexingStateText != "Indexing paused", "Failure hidden or false success");
            Require(vm.CanToggleIndexingPause && !vm.CanIndexNow, "Failure must permit recovery, not indexing");
            backend.PauseControlHandler = null;
            await vm.ToggleIndexingPauseAsync(); // Resume from Pausing.
            await vm.ToggleIndexingPauseAsync(); // Pause successfully.
            backend.PauseControlHandler = (_, _) => throw new IOException("synthetic resume failure");
            await vm.ToggleIndexingPauseAsync();
            Require(vm.IndexingState == IndexingControlState.Paused && vm.StatusMessage.Contains("synthetic resume failure"), "Failed resume falsely Running");
            backend.PauseControlHandler = null;
            await vm.ToggleIndexingPauseAsync();
            Require(vm.IndexingState == IndexingControlState.Running && backend.SaveSettingsCount == 0, "Resume retry recovery");
        });

        await Check("P4_Cancellation_PreCancelled_InFlight_Shutdown", async () =>
        {
            await using var backend = new SyntheticDesktopBackend(tempDir);
            var vm = new MainWindowViewModel(backend);
            await vm.InitializeAsync();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await vm.ToggleIndexingPauseAsync(cancelled.Token);
            Require(backend.RecordedPauseRequests.Count == 0, "Pre-cancel dispatch");
            backend.PauseControlHandler = (_, ct) => Task.Delay(Timeout.Infinite, ct);
            using var active = new CancellationTokenSource();
            var pause = vm.ToggleIndexingPauseAsync(active.Token);
            active.Cancel();
            await pause.WaitAsync(TimeSpan.FromSeconds(5));
            Require(vm.IndexingState == IndexingControlState.Pausing && vm.StatusMessage.Contains("cancelled") && vm.CanToggleIndexingPause,
                "Cancellation must refresh actual state and release busy gate");
            backend.PauseControlHandler = null;
            await vm.ToggleIndexingPauseAsync();
            backend.PauseControlHandler = (_, ct) => Task.Delay(Timeout.Infinite, ct);
            pause = vm.ToggleIndexingPauseAsync();
            vm.StopOperations();
            await pause.WaitAsync(TimeSpan.FromSeconds(5));
            var count = backend.RecordedPauseRequests.Count;
            await vm.ToggleIndexingPauseAsync();
            Require(!vm.CanToggleIndexingPause && !vm.CanIndexNow && backend.RecordedPauseRequests.Count == count, "Shutdown dispatched/resumed work");
        });

        await Check("P4_StatusFailure_UnknownRecovery_StaleReadCannotOverwrite", async () =>
        {
            await using var backend = new SyntheticDesktopBackend(tempDir);
            var vm = new MainWindowViewModel(backend);
            await vm.InitializeAsync();
            var stale = new TaskCompletionSource<EngineStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.StatusHandler = _ => stale.Task;
            var oldRead = vm.RefreshStatusAsync();
            backend.StatusHandler = null;
            await vm.ToggleIndexingPauseAsync();
            stale.SetResult(Status(tempDir, IndexingControlState.Running));
            await oldRead;
            Require(vm.IndexingState == IndexingControlState.Paused, "Stale Running status overwrote confirmed Paused");
            backend.StatusHandler = _ => throw new IOException("synthetic status unavailable");
            await vm.ToggleIndexingPauseAsync();
            Require(vm.IndexingState is null && vm.PauseActionText == "Retry indexing status" && !vm.CanIndexNow
                && vm.StatusMessage.Contains("synthetic status unavailable"), "Status failure must not claim confirmed state");
            var count = backend.RecordedPauseRequests.Count;
            backend.StatusHandler = null;
            await vm.ToggleIndexingPauseAsync();
            Require(vm.IndexingState == IndexingControlState.Running && backend.RecordedPauseRequests.Count == count,
                "Unknown-state retry must read status, not guess control direction");
        });

        await Check("P4_PausedSettingsSavePreservesControl_ManualIndexCancellation", async () =>
        {
            await using var backend = new SyntheticDesktopBackend(tempDir);
            var vm = new MainWindowViewModel(backend);
            await vm.InitializeAsync();
            var indexing = vm.TriggerIndexNowAsync();
            await vm.ToggleIndexingPauseAsync();
            await indexing.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!vm.IsIndexingActive && vm.IndexingState == IndexingControlState.Paused, "Manual indexing did not converge before Paused");
            var settings = new SettingsViewModel(backend);
            await settings.InitializeAsync();
            settings.Theme = "Dark";
            await settings.SaveAsync();
            await vm.RefreshStatusAsync();
            Require(vm.IndexingState == IndexingControlState.Paused && backend.RecordedPauseRequests.SequenceEqual([true]), "Settings save resumed engine");
            var count = backend.IndexCallCount;
            await vm.TriggerIndexNowAsync();
            Require(backend.IndexCallCount == count, "Paused manual index dispatched");
        });

        await Check("P4_SharedMainTrayService_StateAndFailures_NoIndependentToggle", async () =>
        {
            await using var backend = new SyntheticDesktopBackend(tempDir);
            await backend.SetIndexingPausedAsync(true);
            var vm = new MainWindowViewModel(backend);
            await vm.InitializeAsync();
            using var tray = new TrayIconService(vm);
            Require(vm.IndexingState == IndexingControlState.Paused && tray.PauseMenuText == vm.PauseActionText
                && tray.IsPauseMenuEnabled && !tray.IsIndexNowMenuEnabled, "Tray initial state not derived from actual backend");
            var control = Gate();
            backend.PauseControlHandler = (_, ct) => control.Task.WaitAsync(ct);
            var resume = tray.ToggleIndexingPauseAsync();
            Require(vm.PauseActionText == "Resuming…" && tray.PauseMenuText == vm.PauseActionText
                && !tray.IsPauseMenuEnabled && !tray.IsIndexNowMenuEnabled, "Tray/main pending state diverged");
            await tray.ToggleIndexingPauseAsync();
            await vm.ToggleIndexingPauseAsync();
            Require(backend.RecordedPauseRequests.SequenceEqual([true, false]), "Duplicate main/tray API calls");
            control.SetResult();
            await resume.WaitAsync(TimeSpan.FromSeconds(5));
            Require(vm.CanIndexNow && tray.IsIndexNowMenuEnabled && tray.PauseMenuText == vm.PauseActionText, "Tray/main resume mismatch");
            backend.PauseControlHandler = (_, _) => throw new IOException("synthetic tray pause failure");
            await tray.ToggleIndexingPauseAsync();
            Require(vm.IndexingState == IndexingControlState.Pausing && vm.StatusMessage.Contains("synthetic tray pause failure")
                && tray.PauseMenuText == "Resume indexing" && !tray.IsIndexNowMenuEnabled && tray.IsPauseMenuEnabled, "Tray failure not synchronized");
            Require(backend.SaveSettingsCount == 0, "Tray pause mutated settings");
            vm.StopOperations();
            Require(!tray.IsPauseMenuEnabled && !tray.IsIndexNowMenuEnabled, "Tray enabled after shutdown");
        });

        if (render)
        {
            await Check("P4_ActualMainButton_AutomationInvoke_TraySync_LightDark_1100x760_800x600", async () =>
            {
                await using var backend = new SyntheticDesktopBackend(tempDir);
                var window = new MainWindow(backend);
                using var tray = new TrayIconService(window.ViewModel);
                try
                {
                    window.ShowAndActivate();
                    var root = (FrameworkElement)window.Content;
                    var button = (Button)root.FindName("PauseIndexingButton");
                    var state = (TextBlock)root.FindName("IndexingStateLabel");
                    var index = (Button)root.FindName("IndexNowButton");
                    await Until(() => button.IsLoaded && button.IsEnabled);
                    Require(button.Focus(FocusState.Keyboard), "Pause button keyboard focus unavailable");
                    var peer = new ButtonAutomationPeer(button);
                    Require(peer.GetName() == "Pause indexing", "Accessible action name missing");
                    var control = Gate();
                    backend.PauseControlHandler = (_, ct) => control.Task.WaitAsync(ct);
                    ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                    await Until(() => window.ViewModel.IsPauseControlBusy);
                    Require((string)button.Content == "Pausing…" && !button.IsEnabled && !index.IsEnabled
                        && tray.PauseMenuText == "Pausing…" && !tray.IsPauseMenuEnabled, "Main/tray pending mismatch");
                    Require(state.Text != "Indexing paused", "Rendered premature Paused");
                    control.SetResult();
                    await Until(() => !window.ViewModel.IsPauseControlBusy);
                    Require(state.Text == "Indexing paused" && (string)button.Content == tray.PauseMenuText && !tray.IsIndexNowMenuEnabled, "Main/tray Paused mismatch");
                    foreach (var (width, height, theme) in new[] { (1100, 760, "Light"), (800, 600, "Dark") })
                    {
                        window.ApplyTheme(theme);
                        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(window.WindowHandle));
                        appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
                        await Task.Delay(100);
                        root.UpdateLayout();
                        var point = button.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
                        Require(button.ActualWidth > 0 && point.X >= 0 && point.X + button.ActualWidth <= root.ActualWidth + 1,
                            $"Pause control clipped at {width}x{height} {theme}");
                        Require(state.ActualWidth > 0 && state.Text == "Indexing paused", "State label missing");
                    }
                    backend.PauseControlHandler = null;
                    await tray.ToggleIndexingPauseAsync();
                    Require(state.Text == "Indexing allowed" && (string)button.Content == "Pause indexing" && index.IsEnabled
                        && tray.IsIndexNowMenuEnabled && backend.RecordedPauseRequests.SequenceEqual([true, false]), "Tray resume did not update actual main button");
                    Require(backend.SaveSettingsCount == 0, "Rendered pause saved settings");
                }
                finally
                {
                    window.ViewModel.StopOperations();
                    window.Quit();
                }
            });
        }

        return new("P4 UI Pause Synthetic", DateTime.UtcNow, results.All(r => r.Passed), results.Count,
            results.Count(r => r.Passed), results.Count(r => !r.Passed), results);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static EngineStatus Status(string dir, IndexingControlState state) => new(42, 40, 2, 0, 0, Path.Combine(dir, "index.sqlite"), IndexingState: state);
    private static void Require(bool assertion, string message)
    {
        if (!assertion) throw new InvalidOperationException(message);
    }
    private static async Task Until(Func<bool> assertion)
    {
        var timeout = Stopwatch.StartNew();
        while (!assertion())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("UI condition not reached within 5s");
            await Task.Delay(10);
        }
    }
}
