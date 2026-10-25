using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.App.Views;

public sealed partial class FoldersPage : Page
{
    public FoldersViewModel ViewModel { get; }
    private readonly IntPtr windowHandle;
    private bool pickerActive;

    public FoldersPage(IDesktopBackend backend, AppSettings? settings = null, IntPtr hwnd = default)
    {
        ViewModel = new FoldersViewModel(backend, settings);
        windowHandle = hwnd;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (pickerActive || !ViewModel.IsInitialized) return;
        pickerActive = true;
        AddFolderButton.IsEnabled = false;
        try
        {
            if (windowHandle == IntPtr.Zero)
                throw new InvalidOperationException("A valid owner window is required.");
            var picker = new FolderPicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle))
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && IsLoaded)
            {
                await ViewModel.AddFolderAsync(folder.Path);
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Folder picker error: {ex.Message}";
        }
        finally
        {
            pickerActive = false;
            AddFolderButton.IsEnabled = true;
        }
    }

    private async void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string path)
        {
            await ViewModel.RemoveFolderAsync(path);
        }
    }

    private async void OnAddSkippedClick(object sender, RoutedEventArgs e)
    {
        var text = NewSkippedBox.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            if (await ViewModel.AddSkippedAsync(text))
            {
                NewSkippedBox.Text = string.Empty;
            }
        }
    }

    private async void OnRemoveSkippedClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string path)
        {
            await ViewModel.RemoveSkippedAsync(path);
        }
    }

    private async void OnAddSkipTypeClick(object sender, RoutedEventArgs e)
    {
        var text = NewExtBox.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            if (await ViewModel.AddSkipTypeAsync(text))
            {
                NewExtBox.Text = string.Empty;
            }
        }
    }

    private async void OnConfirmFoldersClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConfirmFoldersAndEnableMonitoringAsync();
    }

    private async void OnRemoveSkipTypeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string ext)
        {
            await ViewModel.RemoveSkipTypeAsync(ext);
        }
    }
}
