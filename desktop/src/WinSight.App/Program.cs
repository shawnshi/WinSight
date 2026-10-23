using WinSight.App.Services;

namespace WinSight.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // NM-UI-01: Unified strict CLI argument parsing
        if (!CommandLineParser.TryParse(args, out var dataDir, out var isSmokeTest, out var isToggle, out var error))
        {
            Console.Error.WriteLine($"[FATAL] Command line error: {error}");
            Environment.ExitCode = 2;
            return;
        }

        if (!SingleInstanceService.TryAcquire(dataDir, isToggle, out var singleInstance) || singleInstance is null)
        {
            // Another instance is running and was signaled; exit cleanly
            return;
        }

        global::Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(dataDir, isSmokeTest, isToggle, singleInstance);
        });
    }
}
