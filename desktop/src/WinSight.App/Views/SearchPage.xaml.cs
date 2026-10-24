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
    private WeakReference<Control>? previewReturnFocus;
    private bool previewSessionOpen;
    private bool closingPreview;

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

    private async void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && ViewModel.Hits.Count > 0)
        {
            e.Handled = true;
            ViewModel.SelectedIndex = 0;
            try { await FocusSelectedResultAsync(); }
            catch (Exception error) { ShowPreviewError("Result focus failed: " + error.Message); }
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

    private async void OnResultsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || HasKeyModifier() || IsInteractiveChild(e.OriginalSource as DependencyObject)) return;
        if (e.Key == VirtualKey.Space && ViewModel.SelectedItem is { } selected)
        {
            e.Handled = true;
            await ShowPreviewAsync(selected, keyboard: true);
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ViewModel.OpenCurrentAsync();
        }
    }

    private static bool HasKeyModifier() => new[] { VirtualKey.Control, VirtualKey.Menu, VirtualKey.Shift, VirtualKey.LeftWindows, VirtualKey.RightWindows }
        .Any(key => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));

    private static bool IsInteractiveChild(DependencyObject? element)
    {
        while (element is not null && element is not ListViewItem && element is not ListView)
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or TextBox or PasswordBox or RichEditBox or ComboBox) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || !previewSessionOpen || HasKeyModifier()) return;
        e.Handled = true;
        await ClosePreviewAsync(restoreFocus: true);
    }

    private async void OnPreviewItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchItemViewModel selected } button)
            await ShowPreviewAsync(selected, button.FocusState == FocusState.Keyboard);
    }

    private async Task ShowPreviewAsync(SearchItemViewModel selected, bool keyboard)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is Control focused && !IsInsidePreview(focused))
            previewReturnFocus = new WeakReference<Control>(focused);
        previewSessionOpen = true;
        UpdatePreviewLayout();
        PreviewHost.IsPaneOpen = true;
        UpdateLayout();
        if (keyboard) SourcePreview.FocusCloseButton();
        try { await SourcePreview.ShowAsync(ViewModel, selected); }
        catch (Exception error) { ShowPreviewError("Preview failed: " + error.Message); }
    }

    private async Task ClosePreviewAsync(bool restoreFocus)
    {
        if (closingPreview || (!previewSessionOpen && !PreviewHost.IsPaneOpen)) return;
        closingPreview = true;
        previewSessionOpen = false;
        try
        {
            await SourcePreview.ClearAsync();
        }
        catch (Exception error) { ShowPreviewError("Preview cleanup failed: " + error.Message); }
        finally
        {
            PreviewHost.IsPaneOpen = false;
            closingPreview = false;
            var returnFocus = previewReturnFocus;
            previewReturnFocus = null;
            if (restoreFocus)
            {
                bool restored = returnFocus?.TryGetTarget(out var focused) == true && focused.XamlRoot == XamlRoot && focused.Focus(FocusState.Keyboard);
                if (!restored) await FocusSelectedResultAsync();
            }
        }
    }

    private bool IsInsidePreview(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, SourcePreview)) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async Task FocusSelectedResultAsync()
    {
        if (!IsLoaded || XamlRoot is not { } root) return;
        var selected = ViewModel.SelectedItem;
        var origin = FocusManager.GetFocusedElement(root);
        if (selected is not null) ResultsListView.ScrollIntoView(selected);
        // Allow the input event and container realization to settle before an explicit target focus.
        await Task.Delay(16);
        if (!IsLoaded || XamlRoot != root || previewSessionOpen || !ReferenceEquals(selected, ViewModel.SelectedItem)
            || !ReferenceEquals(origin, FocusManager.GetFocusedElement(root))) return;
        ResultsListView.UpdateLayout();
        if (selected is not null && ResultsListView.ContainerFromItem(selected) is Control { IsLoaded: true } container)
        {
            var result = await FocusManager.TryFocusAsync(container, FocusState.Keyboard);
            if (result.Succeeded) return;
        }
        if (ViewModel.Hits.Count > 0) await FocusManager.TryFocusAsync(ResultsListView, FocusState.Keyboard);
        else await FocusManager.TryFocusAsync(SearchBox, FocusState.Keyboard);
    }

    private void ShowPreviewError(string message)
    {
        SearchNotice.Message = message;
        SearchNotice.Severity = InfoBarSeverity.Warning;
        SearchNotice.IsOpen = true;
    }

    private void UpdatePreviewLayout()
    {
        if (PreviewHost is null || PreviewHost.ActualWidth <= 0) return;
        bool overlay = PreviewHost.ActualWidth < 820;
        PreviewHost.DisplayMode = overlay ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        SourcePreview.TabFocusNavigation = overlay ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Local;
        PreviewHost.OpenPaneLength = overlay ? PreviewHost.ActualWidth : Math.Clamp(PreviewHost.ActualWidth * .42, 340, 440);
    }

    private void OnPreviewHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewLayout();
    private async void OnPreviewCloseRequested(object? sender, EventArgs e) => await ClosePreviewAsync(restoreFocus: true);
    private async void OnPreviewPaneClosed(SplitView sender, object args) => await ClosePreviewAsync(restoreFocus: true);
    private async void OnPageUnloaded(object sender, RoutedEventArgs e) => await ClosePreviewAsync(restoreFocus: false);
    internal bool IsPreviewOpen => PreviewHost.IsPaneOpen;
    internal SourcePreviewPane PreviewControl => SourcePreview;

    public Visibility GetEmptyStateVisibility(int hitCount, bool isLoading) =>
        hitCount == 0 && !isLoading ? Visibility.Visible : Visibility.Collapsed;
}
