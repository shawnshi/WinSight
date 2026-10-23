using Microsoft.UI.Xaml;

namespace WinSight.Ui.Harness;

public partial class HarnessApp : Application
{
    private readonly string tempDir;
    private readonly bool isAutomated;
    private HarnessWindow? window;
    private readonly bool pauseOnly;

    public HarnessApp(string dataDir, bool automated, bool pauseOnly = false)
    {
        this.pauseOnly = pauseOnly;
        tempDir = dataDir;
        isAutomated = automated;
        InitializeComponent();
        if (pauseOnly)
        {
            UnhandledException += (_, args) => File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "p4-ui-unhandled.log"), args.Exception.ToString() + Environment.NewLine);
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        if (!pauseOnly)
        {
            window = new HarnessWindow(tempDir, isAutomated);
            window.Activate();
            return;
        }

        // Keep a host alive while the actual production window is opened and closed by the tests.
        var host = new Window { Title = "P4 pause synthetic test host", Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = "Running synthetic pause checks" } };
        host.Activate();
        try
        {
            var report = await PauseHarnessRunner.RunAsync(tempDir, render: true,
                line => File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "p4-ui-run.log"), line + Environment.NewLine));
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "p4-ui-pause-report.json"),
                System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = report.AllPassed ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "p4-ui-unhandled.log"), ex.ToString());
            Environment.ExitCode = 1;
        }
        finally
        {
            host.Close();
            Exit();
        }
    }
}
