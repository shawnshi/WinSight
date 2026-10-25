using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSight.App.ViewModels;
using WinSight.App.Views;
using WinSight.Core;

namespace WinSight.Ui.Harness;

internal static class StatusRefreshCases
{
    private static SyntheticDesktopBackend Backend(string root) => new(root)
    {
        StatusHandler = _ => Task.FromResult(new EngineStatus(0, 0, 0, 0, 0, root, null, IndexingControlState.Running))
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("Status notification did not converge.");
            await Task.Delay(25);
        }
    }

    public static async Task RunHeadlessAsync(string root)
    {
        var passed = new List<string>();
        await using (var backend = Backend(root))
        {
            var settings = await backend.LoadSettingsAsync();
            settings.Folders.Clear();
            settings.CodeFolders.Clear();
            await backend.SaveSettingsAsync(settings);
            var main = new MainWindowViewModel(backend, settings);
            var folders = new FoldersViewModel(backend, settings);
            Task pending = Task.CompletedTask;
            folders.SettingsChanged += (_, _) => pending = main.InitializeAsync();
            await folders.InitializeAsync();
            await pending;
            Require(main.StatusMessage.StartsWith("No index folders configured.", StringComparison.Ordinal), "Initial no-root state is inaccurate.");
            passed.Add("loaded-settings-notification");
            var owned = Path.Combine(root, "status-owned-empty");
            Directory.CreateDirectory(owned);
            Require(await folders.AddFolderAsync(owned), "Owned folder save failed.");
            await pending;
            Require(main.Settings.Folders.Contains(owned) && main.StatusMessage.StartsWith("No files indexed yet.", StringComparison.Ordinal), "Saved settings did not refresh the global empty-index status.");
            Require(backend.IndexCallCount == 0, "Status refresh unexpectedly indexed a folder.");
            passed.Add("save-notification-refreshes-global-status");
            Require(await folders.RemoveFolderAsync(owned), "Owned folder removal failed.");
            await pending;
            Require(main.Settings.Folders.Count == 0 && main.StatusMessage.StartsWith("No index folders configured.", StringComparison.Ordinal), "Root removal left stale global status.");
            passed.Add("remove-notification-refreshes-global-status");
            main.StopOperations();
        }
        await using (var backend = Backend(root))
        {
            var older = new TaskCompletionSource<AppSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            var newer = new TaskCompletionSource<AppSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            backend.LoadSettingsHandler = _ => Interlocked.Increment(ref calls) == 1 ? older.Task : newer.Task;
            var main = new MainWindowViewModel(backend);
            var first = main.InitializeAsync();
            var second = main.InitializeAsync();
            var owned = Path.Combine(root, "newer-settings");
            newer.SetResult(new AppSettings { Folders = [owned] });
            await second;
            older.SetResult(new AppSettings());
            await first;
            Require(main.Settings.Folders.SequenceEqual(new[] { owned }), "Late old settings overwrote a newer committed read.");
            passed.Add("late-load-cannot-overwrite-newer-settings");
            main.StopOperations();
        }
        await using (var backend = Backend(root))
        {
            backend.LoadSettingsHandler = _ => Task.FromException<AppSettings>(new IOException("Owned fixture load failure"));
            var main = new MainWindowViewModel(backend);
            await main.InitializeAsync();
            Require(main.StatusMessage.StartsWith("Failed to load settings:", StringComparison.Ordinal) && backend.StatusCallCount == 0, "A failed settings read was normalized into a healthy status.");
            passed.Add("settings-error-not-overwritten-by-success");
            main.StopOperations();
        }
        await using (var backend = Backend(root))
        {
            var delayed = new TaskCompletionSource<AppSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.LoadSettingsHandler = _ => delayed.Task;
            var main = new MainWindowViewModel(backend);
            var load = main.InitializeAsync();
            main.StopOperations();
            delayed.SetResult(new AppSettings { Folders = [Path.Combine(root, "late-after-stop")] });
            await load;
            Require(main.Settings.Folders.Count == 0 && backend.StatusCallCount == 0, "A settled window accepted late settings after stop.");
            passed.Add("stop-rejects-late-settings-read");
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { status = "Passed", count = passed.Count, tests = passed }));
    }

    public static async Task RunUiAsync(string root, string evidence)
    {
        await using var backend = Backend(root);
        var settings = await backend.LoadSettingsAsync();
        settings.Folders.Clear();
        settings.CodeFolders.Clear();
        await backend.SaveSettingsAsync(settings);
        var window = new MainWindow(backend, settings, dataDirectory: root)
        {
            Title = "WinSight - owned status notification fixture"
        };
        try
        {
            window.Activate();
            window.NavigateTo("folders");
            var navigation = ((Grid)window.Content).Children.OfType<NavigationView>().Single();
            var page = (FoldersPage)((Frame)navigation.Content).Content;
            await WaitAsync(() => page.IsLoaded && page.ViewModel.IsInitialized
                && window.ViewModel.StatusMessage.StartsWith("No index folders configured.", StringComparison.Ordinal));
            var owned = Path.Combine(root, "status-ui-owned-empty");
            Directory.CreateDirectory(owned);
            Require(await page.ViewModel.AddFolderAsync(owned), "Production page ViewModel save failed.");
            await WaitAsync(() => window.ViewModel.Settings.Folders.Contains(owned)
                && window.ViewModel.StatusMessage.StartsWith("No files indexed yet.", StringComparison.Ordinal));
            Require(backend.IndexCallCount == 0, "Notification started indexing implicitly.");
            var result = new { status = "PassedProductionPageToMainWindowNotification", pid = Environment.ProcessId,
                ownerHwnd = window.WindowHandle.ToInt64(), saved = true, globalSettingsRefreshed = true,
                emptyIndexNotReportedAsSuccess = true, pickerInvoked = false, indexCalls = backend.IndexCallCount };
            await File.WriteAllTextAsync(Path.Combine(evidence, "status-ui-result.json"),
                System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            window.ViewModel.StopOperations();
            window.Quit();
        }
    }
}
