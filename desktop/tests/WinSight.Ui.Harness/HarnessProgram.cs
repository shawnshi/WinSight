namespace WinSight.Ui.Harness;

public static class HarnessProgram
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--folder-selection", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("GUI-SELECT-01 stopped after three failed attempts; automated replay is disabled.");

        WinRT.ComWrappersSupport.InitializeComWrappers();

        string tempDir = Path.Combine(Path.GetTempPath(), "WinSight_Ui_Harness_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        if(args is ["--source-preview",var image,var output])
        {
            output=Path.GetFullPath(output);
            if(Directory.Exists(output))throw new IOException("Fresh GUI evidence directory required.");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output,"execution-prepared"),"Explicit synthetic preview run");
            SourcePreviewHarnessApp.Trace(output,"before Application.Start");
            try
            {
                global::Microsoft.UI.Xaml.Application.Start(initialization=>
                {
                    SourcePreviewHarnessApp.Trace(output,"initialization callback");
                    System.Threading.SynchronizationContext.SetSynchronizationContext(new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                    try{_=new SourcePreviewHarnessApp(image,output);SourcePreviewHarnessApp.Trace(output,"application constructed");}
                    catch(Exception error){SourcePreviewHarnessApp.Trace(output,"application constructor failed",error);throw;}
                });
            }
            catch(Exception error){SourcePreviewHarnessApp.Trace(output,"Application.Start failed",error);throw;}
            return;
        }

        bool pauseOnly = args.Any(a => string.Equals(a, "--pause-automated", StringComparison.OrdinalIgnoreCase));
        bool pauseConsole = args.Any(a => string.Equals(a, "--pause-console", StringComparison.OrdinalIgnoreCase));
        bool console = args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase));
        if (args.Contains("--status-refresh-console", StringComparer.OrdinalIgnoreCase))
        {
            StatusRefreshCases.RunHeadlessAsync(tempDir).GetAwaiter().GetResult();
            return;
        }

        bool guiRepairConsole = args.Any(a => string.Equals(a, "--gui-repair-console", StringComparison.OrdinalIgnoreCase));
        if (guiRepairConsole)
        {
            var report = GuiRepairHarness.RunAllTestsAsync(tempDir, Console.WriteLine, includeLiveWindow: false).GetAwaiter().GetResult();
            var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            Environment.Exit(report.AllPassed ? 0 : 1);
            return;
        }

        if (console || pauseConsole)
        {
            var report = pauseConsole
                ? PauseHarnessRunner.RunAsync(tempDir, render: false, Console.WriteLine).GetAwaiter().GetResult()
                : HarnessRunner.RunAllTestsAsync(tempDir, Console.WriteLine).GetAwaiter().GetResult();
            if (pauseConsole) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "p4-ui-pause-console-report.json"),
                System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            Environment.Exit(report.AllPassed ? 0 : 1);
            return;
        }

        bool statusRefresh = args.Contains("--status-refresh-ui", StringComparer.OrdinalIgnoreCase);
        bool guiRepairAutomated = statusRefresh || args.Any(a => string.Equals(a, "--gui-repair-automated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--verify-picker", StringComparison.OrdinalIgnoreCase));
        string? evidenceDir = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--evidence-dir", StringComparison.OrdinalIgnoreCase))
                evidenceDir = Path.GetFullPath(args[i + 1]);
        }

        bool automated = args.Any(a => string.Equals(a, "--automated", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));

        global::Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new HarnessApp(tempDir, automated || pauseOnly, pauseOnly, guiRepair: guiRepairAutomated, evidenceDir: evidenceDir, statusRefresh: statusRefresh);
        });
    }
}
