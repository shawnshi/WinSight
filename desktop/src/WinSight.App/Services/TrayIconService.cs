using System.Runtime.InteropServices;
using System.ComponentModel;
using WinSight.App.ViewModels;

namespace WinSight.App.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint WM_TRAYICON = NativeMethods.WM_USER + 101;
    private const string WindowClassName = "WinSight_Tray_MsgWnd_Class";

    private const uint CMD_QUICKSEARCH = 1001;
    private const uint CMD_MAINWINDOW = 1002;
    private const uint CMD_SETTINGS = 1003;
    private const uint CMD_PAUSE = 1004;
    private const uint CMD_INDEXNOW = 1005;
    private const uint CMD_EXIT = 1006;

    private readonly IntPtr hWnd;
    private readonly NativeMethods.WndProc wndProcDelegate;
    private readonly IntPtr hInstance;
    private bool disposed;
    private IntPtr iconHandle;
    private string tooltip = "WinSight (Native Preview)";
    // Explorer broadcasts this after a restart or crash; every notification icon is gone and must be added again.
    private readonly uint taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private readonly MainWindowViewModel? indexing;

    public string PauseMenuText => indexing?.PauseActionText ?? "索引状态不可用";
    public bool IsPauseMenuEnabled => !disposed && indexing?.CanToggleIndexingPause == true;
    public bool IsIndexNowMenuEnabled => !disposed && indexing?.CanIndexNow == true;

    public Task ToggleIndexingPauseAsync(CancellationToken cancellationToken = default) =>
        IsPauseMenuEnabled ? indexing!.ToggleIndexingPauseAsync(cancellationToken) : Task.CompletedTask;

    private void OnIndexingChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!disposed && indexing is not null)
            UpdateTooltip($"WinSight: {indexing.IndexingStateText}. {indexing.StatusMessage}");
    }

    public event Action? QuickSearchRequested;
    public event Action? MainWindowRequested;
    public event Action? SettingsRequested;
    public event Action? IndexNowRequested;
    public event Action? ExitRequested;
    public event Action? LeftClicked;

    public TrayIconService(MainWindowViewModel? indexing = null)
    {
        this.indexing = indexing;
        hInstance = NativeMethods.GetModuleHandle(null);
        wndProcDelegate = CustomWndProc;

        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = wndProcDelegate,
            hInstance = hInstance,
            lpszClassName = WindowClassName
        };
        if (NativeMethods.RegisterClassEx(ref wndClass) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot register the WinSight tray window.");

        // A hidden top-level tool window, not a message-only window: message-only windows are excluded from
        // broadcasts, so they would never see TaskbarCreated.
        const uint WS_EX_TOOLWINDOW = 0x00000080;
        hWnd = NativeMethods.CreateWindowEx(
            WS_EX_TOOLWINDOW,
            WindowClassName,
            "WinSight_Tray_MsgWnd",
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (hWnd == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.UnregisterClass(WindowClassName, hInstance);
            throw new Win32Exception(error, "Cannot create the WinSight tray window.");
        }
        try
        {
            InitializeTrayIcon();
        }
        catch
        {
            if (iconHandle != IntPtr.Zero) NativeMethods.DestroyIcon(iconHandle);
            NativeMethods.DestroyWindow(hWnd);
            NativeMethods.UnregisterClass(WindowClassName, hInstance);
            throw;
        }
        if (indexing is not null)
        {
            indexing.PropertyChanged += OnIndexingChanged;
            OnIndexingChanged(indexing, new PropertyChangedEventArgs(null));
        }
    }

    private void InitializeTrayIcon()
    {
        const uint imageIcon = 1;
        const uint loadFromFile = 0x10;
        const uint defaultSize = 0x40;
        iconHandle = NativeMethods.LoadImage(IntPtr.Zero,
            Path.Combine(AppContext.BaseDirectory, "Assets", "WinSight.ico"), imageIcon, 0, 0, loadFromFile | defaultSize);
        if (iconHandle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the WinSight tray icon.");
        AddIcon();
    }

    private void AddIcon()
    {
        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = hWnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = iconHandle,
            szTip = tooltip
        };

        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref nid))
            throw new InvalidOperationException("Cannot add the WinSight tray icon.");
    }

    /// <summary>Re-adds the icon after the taskbar was recreated; true when the shell accepted it.</summary>
    internal bool RestoreIcon()
    {
        if (disposed || iconHandle == IntPtr.Zero) return false;
        try { AddIcon(); return true; }
        catch (InvalidOperationException error)
        {
            WinSight.Core.Diagnostics.AppLog.Warn("tray icon could not be restored after the taskbar was recreated", error);
            return false;
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        if (hWnd == IntPtr.Zero) return;
        this.tooltip = tooltip.Length > 120 ? tooltip[..120] : tooltip;
        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = hWnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_TIP,
            szTip = this.tooltip
        };
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref nid);
    }

    private void ShowContextMenu()
    {
        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        NativeMethods.AppendMenu(hMenu, 0, CMD_QUICKSEARCH, "Quick Search\tCtrl+Shift+Space");
        NativeMethods.AppendMenu(hMenu, 0, CMD_MAINWINDOW, "Open WinSight");
        NativeMethods.AppendMenu(hMenu, 0, CMD_SETTINGS, "Settings");
        NativeMethods.AppendMenu(hMenu, 0x0800, 0, "-"); // MF_SEPARATOR
        NativeMethods.AppendMenu(hMenu, IsPauseMenuEnabled ? 0u : 0x0001u, CMD_PAUSE, PauseMenuText);
        NativeMethods.AppendMenu(hMenu, IsIndexNowMenuEnabled ? 0u : 0x0001u, CMD_INDEXNOW, "Index Now");
        NativeMethods.AppendMenu(hMenu, 0x0800, 0, "-"); // MF_SEPARATOR
        NativeMethods.AppendMenu(hMenu, 0, CMD_EXIT, "Exit WinSight");

        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.SetForegroundWindow(hWnd);

        uint selected = NativeMethods.TrackPopupMenuEx(
            hMenu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
            pt.X, pt.Y,
            hWnd,
            IntPtr.Zero);

        NativeMethods.DestroyMenu(hMenu);

        switch (selected)
        {
            case CMD_QUICKSEARCH:
                QuickSearchRequested?.Invoke();
                break;
            case CMD_MAINWINDOW:
                MainWindowRequested?.Invoke();
                break;
            case CMD_SETTINGS:
                SettingsRequested?.Invoke();
                break;
            case CMD_PAUSE:
                _ = ToggleIndexingPauseAsync();
                break;
            case CMD_INDEXNOW:
                if (IsIndexNowMenuEnabled) IndexNowRequested?.Invoke();
                break;
            case CMD_EXIT:
                ExitRequested?.Invoke();
                break;
        }
    }

    private IntPtr CustomWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (taskbarCreatedMessage != 0 && msg == taskbarCreatedMessage)
        {
            RestoreIcon();
            return IntPtr.Zero;
        }
        if (msg == WM_TRAYICON)
        {
            uint mouseEvent = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseEvent == NativeMethods.WM_LBUTTONUP)
            {
                LeftClicked?.Invoke();
                return IntPtr.Zero;
            }
            if (mouseEvent == NativeMethods.WM_RBUTTONUP)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }
        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (indexing is not null) indexing.PropertyChanged -= OnIndexingChanged;

        if (hWnd != IntPtr.Zero)
        {
            var nid = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = hWnd,
                uID = TrayIconId
            };
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref nid);
            NativeMethods.DestroyWindow(hWnd);
        }
        NativeMethods.UnregisterClass(WindowClassName, hInstance);
        if (iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(iconHandle);
            iconHandle = IntPtr.Zero;
        }
    }
}
