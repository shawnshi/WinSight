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
    private readonly MainWindowViewModel? indexing;

    public string PauseMenuText => indexing?.PauseActionText ?? "Indexing status unavailable";
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
        NativeMethods.RegisterClassEx(ref wndClass);

        hWnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            "WinSight_Tray_MsgWnd",
            0,
            0, 0, 0, 0,
            new IntPtr(-3), // HWND_MESSAGE
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        InitializeTrayIcon();
        if (indexing is not null)
        {
            indexing.PropertyChanged += OnIndexingChanged;
            OnIndexingChanged(indexing, new PropertyChangedEventArgs(null));
        }
    }

    private void InitializeTrayIcon()
    {
        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = hWnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(32512)), // IDI_APPLICATION
            szTip = "WinSight (Native Preview)"
        };

        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref nid);
    }

    public void UpdateTooltip(string tooltip)
    {
        if (hWnd == IntPtr.Zero) return;
        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = hWnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_TIP,
            szTip = tooltip.Length > 120 ? tooltip[..120] : tooltip
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
    }
}
