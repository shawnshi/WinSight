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
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        refreshTimer.Tick += async (_, _) => await ViewModel.RefreshStatusAsync();
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

    private async void OnInspectModelClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.InspectModelAsync();
    }

    public string FormatTotal(int count) => $"Total: {count}";
    public string FormatDone(int count) => $"Indexed: {count}";
    public string FormatPending(int count) => $"Pending: {count}";
    public string FormatFailed(int count) => $"Failed: {count}";
    public double ComputePercent(int done, int total) => total > 0 ? Math.Clamp((double)done * 100 / total, 0, 100) : 0;
    public bool Invert(bool value) => !value;
    public bool HasError(string? error) => !string.IsNullOrEmpty(error);
    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private async void OnRefreshQueueClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshQueueStatusAsync();
    }

    private async void OnRetryFailedClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RetryFailedAsync();
    }
}
