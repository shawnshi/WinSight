using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.App.Views;

public sealed partial class SearchPage : Page
{
    public QuickSearchViewModel ViewModel { get; }

    public SearchPage(IDesktopBackend backend, AppSettings? settings = null)
    {
        ViewModel = new QuickSearchViewModel(backend);
        InitializeComponent();
    }

    public void SetInitialQuery(string query, string? kind = null)
    {
        if (kind is not null)
        {
            KindFilterCombo.SelectedIndex = kind.ToLowerInvariant() switch
            {
                "doc" or "document" => 1,
                "pdf" => 2,
                "image" => 3,
                "screenshot" => 4,
                "audio" => 5,
                "video" or "media" => 6,
                "code" => 7,
                _ => 0
            };
        }
        ViewModel.Query = query;
    }

    private void OnKindFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KindFilterCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            ViewModel.SelectedKind = tag == "all" ? null : tag;
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && ViewModel.Hits.Count > 0)
        {
            ResultsListView.Focus(FocusState.Programmatic);
            ViewModel.SelectedIndex = 0;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            _ = ViewModel.OpenCurrentAsync();
            e.Handled = true;
        }
    }

    private void OnResultItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchItemViewModel item)
        {
            _ = ViewModel.OpenHitAsync(item);
        }
    }

    private void OnOpenItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SearchItemViewModel item)
        {
            _ = ViewModel.OpenHitAsync(item);
        }
    }

    private void OnRevealItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SearchItemViewModel item)
        {
            _ = ViewModel.RevealHitAsync(item);
        }
    }

    private async void OnCopyItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SearchItemViewModel item)
        {
            await ViewModel.CopyHitAsync(item);
        }
    }

    public Visibility GetEmptyStateVisibility(int hitCount, bool isLoading) =>
        hitCount == 0 && !isLoading ? Visibility.Visible : Visibility.Collapsed;
}
