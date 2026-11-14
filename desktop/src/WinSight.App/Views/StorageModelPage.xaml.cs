using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.App.Views;

public sealed partial class StorageModelPage : Page
{
    public StorageModelViewModel ViewModel { get; }
    private readonly DispatcherTimer refreshTimer;

    public StorageModelPage(IDesktopBackend backend, AppSettings? settings = null)
    {
        ViewModel = new StorageModelViewModel(backend);
        InitializeComponent();
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        refreshTimer.Tick += async (_, _) =>
        {
            await ViewModel.RefreshStatusAsync();
            // Back off polling interval when queue is idle to save CPU/SQLite wakeups
            refreshTimer.Interval = ViewModel.PendingCount > 0 || ViewModel.IsBackfilling
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromSeconds(10);
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        refreshTimer.Start();
        await ViewModel.RefreshStatusAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        refreshTimer.Stop();
    }

    private async void OnInspectModelClick(object sender, RoutedEventArgs e) => await ViewModel.InspectModelAsync();
    private async void OnRefreshQueueClick(object sender, RoutedEventArgs e) => await ViewModel.RefreshStatusAsync();
    private async void OnRetryFailedClick(object sender, RoutedEventArgs e) => await ViewModel.RetryFailedAsync();
    private async void OnStartBackfillClick(object sender, RoutedEventArgs e) => await ViewModel.StartBackfillAsync();
    private void OnStopBackfillClick(object sender, RoutedEventArgs e) => ViewModel.StopBackfill();
    private async void OnClearCacheClick(object sender, RoutedEventArgs e) => await ViewModel.ClearPreviewCacheAsync();
    private async void OnCompactDbClick(object sender, RoutedEventArgs e) => await ViewModel.CompactDatabaseAsync();

    public string FormatTotal(int count) => $"总计：{count}";
    public string FormatDone(int count) => $"已索引：{count}";
    public string FormatPending(int count) => $"待处理：{count}";
    public string FormatFailed(int count) => $"失败：{count}";
    public double ComputePercent(int done, int total) => total > 0 ? Math.Clamp((double)done * 100 / total, 0, 100) : 0;
    public bool Invert(bool value) => !value;
    public bool HasError(string? error) => !string.IsNullOrEmpty(error);
    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
