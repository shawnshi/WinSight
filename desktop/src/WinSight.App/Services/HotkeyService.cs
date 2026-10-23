using System.Runtime.InteropServices;

namespace WinSight.App.Services;

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x9001;
    private const string WindowClassName = "WinSight_Hotkey_MsgWnd_Class";

    private readonly IntPtr hWnd;
    private readonly NativeMethods.WndProc wndProcDelegate;
    private readonly IntPtr hInstance;
    private string currentHotkeyText;
    private uint currentModifiers;
    private uint currentVk;
    private bool isHeld;
    private bool disposed;
    private readonly bool ownsNativeWindow;
    private readonly Func<int, uint, uint, bool> register;
    private readonly Func<int, bool> unregister;
    private readonly Func<int> errorCode;
    private int activeId = HotkeyId;
    private bool registered;
    private long bindingRevision;
    internal long BindingRevision => Interlocked.Read(ref bindingRevision);

    public bool HasConflict { get; private set; }
    public string? ConflictMessage { get; private set; }
    public string CurrentHotkey => currentHotkeyText;

    public event Action? HotkeyPressed;
    public event Action<string>? HotkeyConflict;

    public HotkeyService(string initialHotkey = "Control+Shift+Space")
    {
        currentHotkeyText = initialHotkey;
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
        hWnd = NativeMethods.CreateWindowEx(0, WindowClassName, "WinSight_Hotkey_MsgWnd", 0,
            0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hWnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot create hotkey message window.");
        ownsNativeWindow = true;
        register = (id, modifiers, key) => NativeMethods.RegisterHotKey(hWnd, id, modifiers, key);
        unregister = id => NativeMethods.UnregisterHotKey(hWnd, id);
        errorCode = Marshal.GetLastWin32Error;
        ApplyHotkey(initialHotkey);
    }

    internal HotkeyService(string initialHotkey, Func<int, uint, uint, bool> register,
        Func<int, bool> unregister, Func<int> errorCode)
    {
        currentHotkeyText = initialHotkey;
        wndProcDelegate = CustomWndProc;
        this.register = register ?? throw new ArgumentNullException(nameof(register));
        this.unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));
        this.errorCode = errorCode ?? throw new ArgumentNullException(nameof(errorCode));
        ApplyHotkey(initialHotkey);
    }

    public static (uint Modifiers, uint Vk) ParseHotkey(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) throw new ArgumentException("Hotkey must not be empty.", nameof(hotkey));
        uint modifiers = 0;
        uint vk = 0;
        foreach (var token in hotkey.Split('+', StringSplitOptions.TrimEntries))
        {
            var part = token.ToLowerInvariant();
            uint modifier = part switch
            {
                "control" or "ctrl" => NativeMethods.MOD_CONTROL,
                "shift" => NativeMethods.MOD_SHIFT,
                "alt" => NativeMethods.MOD_ALT,
                "win" or "windows" => NativeMethods.MOD_WIN,
                _ => 0
            };
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0) throw new ArgumentException("Hotkey contains a duplicate modifier.", nameof(hotkey));
                modifiers |= modifier;
                continue;
            }
            uint key = part switch
            {
                "space" => 0x20,
                "enter" or "return" => 0x0d,
                "esc" or "escape" => 0x1b,
                "tab" => 0x09,
                _ => 0
            };
            if (key == 0 && part.Length == 1 && (part[0] is >= 'a' and <= 'z' or >= '0' and <= '9'))
                key = char.ToUpperInvariant(part[0]);
            if (key == 0 && part.StartsWith('f') && int.TryParse(part[1..], out var number) && number is >= 1 and <= 24)
                key = (uint)(0x6f + number);
            if (key == 0 || vk != 0) throw new ArgumentException("Hotkey requires exactly one supported key.", nameof(hotkey));
            vk = key;
        }
        if (modifiers == 0 || vk == 0) throw new ArgumentException("Hotkey requires a modifier and one supported key.", nameof(hotkey));
        return (modifiers, vk);
    }

    public bool ApplyHotkey(string hotkeyText)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        uint modifiers, key;
        try { (modifiers, key) = ParseHotkey(hotkeyText); }
        catch (ArgumentException error) { return Fail(error.Message); }
        if (isHeld)
        {
            currentHotkeyText = hotkeyText;
            currentModifiers = modifiers;
            currentVk = key;
            Interlocked.Increment(ref bindingRevision);
            HasConflict = false;
            ConflictMessage = null;
            return true;
        }
        if (registered && modifiers == currentModifiers && key == currentVk)
        {
            currentHotkeyText = hotkeyText;
            Interlocked.Increment(ref bindingRevision);
            HasConflict = false;
            ConflictMessage = null;
            return true;
        }
        // WS-05: register on the spare ID before releasing the working binding.
        int replacement = registered ? activeId == HotkeyId ? HotkeyId + 1 : HotkeyId : activeId;
        if (!register(replacement, modifiers | NativeMethods.MOD_NOREPEAT, key))
        {
            int error = errorCode();
            return Fail(error == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"Hotkey '{hotkeyText}' is already in use; the previous binding was preserved."
                : $"Cannot register hotkey '{hotkeyText}' (Win32 error: {error}); the previous binding was preserved.");
        }
        if (registered && !unregister(activeId))
        {
            int error = errorCode();
            unregister(replacement);
            return Fail($"Cannot release the previous hotkey (Win32 error: {error}).");
        }
        activeId = replacement;
        registered = true;
        currentHotkeyText = hotkeyText;
        currentModifiers = modifiers;
        currentVk = key;
        Interlocked.Increment(ref bindingRevision);
        HasConflict = false;
        ConflictMessage = null;
        return true;
    }

    private bool Fail(string message)
    {
        HasConflict = true;
        ConflictMessage = message;
        HotkeyConflict?.Invoke(message);
        return false;
    }

    public void HoldHotkey(bool hold)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (hold == isHeld) return;
        if (hold)
        {
            if (registered && !unregister(activeId))
            {
                Fail($"Cannot suspend the hotkey (Win32 error: {errorCode()}).");
                return;
            }
            registered = false;
            isHeld = true;
        }
        else
        {
            isHeld = false;
            ApplyHotkey(currentHotkeyText);
        }
    }

    private IntPtr CustomWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && registered && wParam.ToInt32() == activeId)
        {
            HotkeyPressed?.Invoke();
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (registered) unregister(activeId);
        registered = false;
        if (ownsNativeWindow)
        {
            NativeMethods.DestroyWindow(hWnd);
            NativeMethods.UnregisterClass(WindowClassName, hInstance);
        }
    }
}
