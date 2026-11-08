using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.App.Views;

public sealed partial class McpConsolePage : Page
{
    private readonly IMcpBackend backend;

    public McpConsolePage(IMcpBackend backend)
    {
        this.backend = backend;
        InitializeComponent();
        ViewModel = new McpConsoleViewModel(backend);
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    public McpConsoleViewModel ViewModel { get; }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void OnDrainClick(object sender, RoutedEventArgs e) => await ViewModel.DrainAsync();

    private void OnCopyConfigClick(object sender, RoutedEventArgs e) => ViewModel.CopyConfig();
}
