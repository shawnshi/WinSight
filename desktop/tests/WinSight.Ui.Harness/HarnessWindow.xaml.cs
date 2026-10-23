using System.Text.Json;
using Microsoft.UI.Xaml;

namespace WinSight.Ui.Harness;

public sealed partial class HarnessWindow : Window
{
    private readonly string tempDir;
    private readonly bool isAutomated;

    public HarnessWindow(string testDataDir, bool automated = false)
    {
        tempDir = testDataDir;
        isAutomated = automated;
        InitializeComponent();

        Activated += OnWindowActivated;
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnWindowActivated;
        if (isAutomated)
        {
            await ExecuteTestsAsync();
            // Automatically close after tests if automated
            await Task.Delay(200);
            Close();
        }
    }

    private async void OnRunTestsClick(object sender, RoutedEventArgs e)
    {
        await ExecuteTestsAsync();
    }

    private async Task ExecuteTestsAsync()
    {
        LogTextBox.Text = string.Empty;
        SummaryTextBlock.Text = "Running tests...";

        void Log(string line)
        {
            LogTextBox.Text += line + Environment.NewLine;
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
        }

        var report = await HarnessRunner.RunAllTestsAsync(tempDir, line => DispatcherQueue.TryEnqueue(() => Log(line)));

        SummaryTextBlock.Text = report.AllPassed
            ? $"All {report.TotalCount} tests PASSED ({report.PassedCount}/{report.TotalCount})"
            : $"Tests FAILED: {report.FailedCount}/{report.TotalCount} failures";

        SummaryTextBlock.Foreground = report.AllPassed
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGreen)
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed);

        // Save JSON report
        try
        {
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", ".dev", "native-migration"));
            if (Directory.Exists(outDir))
            {
                File.WriteAllText(Path.Combine(outDir, "ui-harness-report.json"), json);
            }
        }
        catch
        {
            // Fallback
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
