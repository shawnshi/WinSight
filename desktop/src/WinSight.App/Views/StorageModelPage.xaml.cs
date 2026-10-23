using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.App.Views;

public sealed partial class StorageModelPage : Page
{
    public StorageModelViewModel ViewModel { get; }

    public StorageModelPage(IDesktopBackend backend, AppSettings? settings = null)
    {
        ViewModel = new StorageModelViewModel(backend);
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshStatusAsync();
    }

    private async void OnInspectModelClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.InspectModelAsync();
    }

    public string FormatTotal(int count) => $"Total: {count}";
    public string FormatDone(int count) => $"Indexed: {count}";
    public string FormatPending(int count) => $"Pending: {count}";
    public string FormatFailed(int count) => $"Failed: {count}";
    public bool Invert(bool value) => !value;
    public bool HasError(string? error) => !string.IsNullOrEmpty(error);
    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
