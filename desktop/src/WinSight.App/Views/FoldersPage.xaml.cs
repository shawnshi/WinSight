using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.App.Views;

public sealed partial class FoldersPage : Page
{
    public FoldersViewModel ViewModel { get; }
    private readonly IntPtr windowHandle;
    private bool pickerActive;

    public FoldersPage(ISettingsBackend backend, AppSettings? settings = null, IntPtr hwnd = default)
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

    private void OnScopeSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ViewModel is null) return;
        ViewModel.IsCodeScope = sender.SelectedItem?.Tag as string == "code";
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (pickerActive || !ViewModel.IsInitialized) return;
        pickerActive = true;
        AddFolderButton.IsEnabled = false;
        try
        {
            if (windowHandle == IntPtr.Zero)
                throw new InvalidOperationException("需要有效的主窗口句柄。");
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
            ViewModel.StatusMessage = $"选择目录失败：{ex.Message}";
        }
        finally
        {
            pickerActive = false;
            AddFolderButton.IsEnabled = true;
        }
    }

    private async void OnBrowseSkippedClick(object sender, RoutedEventArgs e)
    {
        if (pickerActive || !ViewModel.IsInitialized) return;
        pickerActive = true;
        try
        {
            if (windowHandle == IntPtr.Zero) return;
            var picker = new FolderPicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle))
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && IsLoaded)
            {
                await ViewModel.AddSkippedAsync(folder.Path);
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"浏览跳过目录失败：{ex.Message}";
        }
        finally
        {
            pickerActive = false;
        }
    }

    private void OnFoldersDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = ViewModel.IsCodeScope ? "授权为代码仓库" : "添加到索引目录";
        }
    }

    private async void OnFoldersDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items.OfType<StorageFolder>())
            {
                await ViewModel.AddFolderAsync(item.Path);
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"拖放添加目录失败：{ex.Message}";
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string path && Directory.Exists(path))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
            catch { }
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

    private async void OnPresetExtClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string ext)
        {
            await ViewModel.AddSkipTypeAsync(ext);
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

    public Visibility BoolToVisibility(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    public bool HasStatus(string? msg) => !string.IsNullOrWhiteSpace(msg);
}
