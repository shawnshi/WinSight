using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinSight.Core;
using WinSight.App.Services;

namespace WinSight.App.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly IDesktopBackend backend;
    private readonly HotkeyService? hotkeyService;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private AppSettings settings;

    private string hotkey = "Control+Shift+Space";
    private bool backgroundIndexingEnabled;
    private bool indexHoursOn;
    private int indexHoursStart = 60;
    private int indexHoursEnd = 420;
    private bool processorOnly = true;
    private string trayClick = "panel";
    private string theme = "Default";
    private string codeEditor = "default";
    private bool openAtLogin;
    private string accelerator = "auto";
    private bool installExecutionProviders = true;
    private bool hasHotkeyConflict;
    private string? hotkeyConflictMessage;
    private string statusMessage = string.Empty;

    private bool backgroundIndexingTouched;
    private long backgroundEditRevision;

    public string Hotkey
    {
        get => hotkey;
        set => SetProperty(ref hotkey, value);
    }

    public bool BackgroundIndexingEnabled
    {
        get => backgroundIndexingEnabled;
        set
        {
            if (SetProperty(ref backgroundIndexingEnabled, value))
            {
                backgroundIndexingTouched = true;
                backgroundEditRevision++;
            }
        }
    }

    public bool IndexHoursOn
    {
        get => indexHoursOn;
        set => SetProperty(ref indexHoursOn, value);
    }

    public int IndexHoursStart
    {
        get => indexHoursStart;
        set => SetProperty(ref indexHoursStart, Math.Clamp(value, 0, 1439));
    }

    public int IndexHoursEnd
    {
        get => indexHoursEnd;
        set => SetProperty(ref indexHoursEnd, Math.Clamp(value, 0, 1439));
    }

    public string IndexHoursStartFormatted => $"{IndexHoursStart / 60:D2}:{IndexHoursStart % 60:D2}";
    public string IndexHoursEndFormatted => $"{IndexHoursEnd / 60:D2}:{IndexHoursEnd % 60:D2}";

    public bool ProcessorOnly
    {
        get => processorOnly;
        set => SetProperty(ref processorOnly, value);
    }

    public string TrayClick
    {
        get => trayClick;
        set => SetProperty(ref trayClick, value);
    }

    public string Theme
    {
        get => theme;
        set => SetProperty(ref theme, value);
    }

    public string CodeEditor
    {
        get => codeEditor;
        set => SetProperty(ref codeEditor, value);
    }

    public bool OpenAtLogin
    {
        get => openAtLogin;
        set => SetProperty(ref openAtLogin, value);
    }

    public string Accelerator
    {
        get => accelerator;
        set => SetProperty(ref accelerator, value);
    }

    public bool InstallExecutionProviders
    {
        get => installExecutionProviders;
        set => SetProperty(ref installExecutionProviders, value);
    }

    public bool HasHotkeyConflict
    {
        get => hasHotkeyConflict;
        set => SetProperty(ref hasHotkeyConflict, value);
    }

    public string? HotkeyConflictMessage
    {
        get => hotkeyConflictMessage;
        set => SetProperty(ref hotkeyConflictMessage, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public SettingsViewModel(IDesktopBackend backend, AppSettings? initialSettings = null, HotkeyService? hotkeyService = null)
    {
        this.backend = backend;
        this.hotkeyService = hotkeyService;
        this.settings = initialSettings ?? new AppSettings();
        LoadFromSettings(this.settings);
    }

    public async Task InitializeAsync()
    {
        try
        {
            settings = await backend.LoadSettingsAsync();
            LoadFromSettings(settings);
            backgroundIndexingTouched = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load settings: {ex.Message}";
        }
    }

    private void LoadFromSettings(AppSettings s)
    {
        hotkey = s.Hotkey;
        backgroundIndexingEnabled = s.BackgroundIndexingEnabled;
        indexHoursOn = s.IndexHoursOn;
        indexHoursStart = s.IndexHours.Start;
        indexHoursEnd = s.IndexHours.End;
        processorOnly = s.ProcessorOnly;
        trayClick = s.TrayClick;
        theme = s.Theme;
        codeEditor = s.CodeEditor;
        openAtLogin = s.OpenAtLogin;
        accelerator = s.Accelerator;
        installExecutionProviders = s.InstallExecutionProviders;

        OnPropertyChanged(nameof(Hotkey));
        OnPropertyChanged(nameof(BackgroundIndexingEnabled));
        OnPropertyChanged(nameof(IndexHoursOn));
        OnPropertyChanged(nameof(IndexHoursStart));
        OnPropertyChanged(nameof(IndexHoursEnd));
        OnPropertyChanged(nameof(IndexHoursStartFormatted));
        OnPropertyChanged(nameof(IndexHoursEndFormatted));
        OnPropertyChanged(nameof(ProcessorOnly));
        OnPropertyChanged(nameof(TrayClick));
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(CodeEditor));
        OnPropertyChanged(nameof(OpenAtLogin));
        OnPropertyChanged(nameof(Accelerator));
        OnPropertyChanged(nameof(InstallExecutionProviders));
    }

    public async Task<bool> SaveAsync()
    {
        var input = new
        {
            Hotkey = hotkey, BackgroundIndexing = backgroundIndexingEnabled, BackgroundTouched = backgroundIndexingTouched,
            BackgroundRevision = backgroundEditRevision, HoursOn = indexHoursOn, HoursStart = indexHoursStart, HoursEnd = indexHoursEnd,
            ProcessorOnly = processorOnly, TrayClick = trayClick, Theme = theme, CodeEditor = codeEditor, OpenAtLogin = openAtLogin,
            Accelerator = accelerator, InstallExecutionProviders = installExecutionProviders
        };
        await saveGate.WaitAsync();
        string? previousHotkey = null;
        long appliedRevision = -1;
        bool bindingApplied = false;
        void ResetRequestedHotkey()
        {
            if (hotkeyService != null && hotkey == input.Hotkey) Hotkey = hotkeyService.CurrentHotkey;
        }
        void ReadHotkeyStatus()
        {
            if (hotkeyService == null) return;
            HasHotkeyConflict = hotkeyService.HasConflict;
            HotkeyConflictMessage = hotkeyService.ConflictMessage;
        }
        try
        {
            try { HotkeyService.ParseHotkey(input.Hotkey); }
            catch (ArgumentException error)
            {
                HasHotkeyConflict = true;
                HotkeyConflictMessage = error.Message;
                StatusMessage = "Save error: " + error.Message;
                ResetRequestedHotkey();
                return false;
            }
            // NM-UI-06: merge preferences into a fresh grant/revision snapshot, never the page's stale authorization.
            var latest = await backend.LoadSettingsAsync();
            if (hotkeyService != null)
            {
                previousHotkey = hotkeyService.CurrentHotkey;
                if (!hotkeyService.ApplyHotkey(input.Hotkey))
                {
                    ReadHotkeyStatus();
                    StatusMessage = hotkeyService.ConflictMessage ?? "Hotkey registration failed.";
                    ResetRequestedHotkey();
                    return false;
                }
                bindingApplied = true;
                appliedRevision = hotkeyService.BindingRevision;
                ReadHotkeyStatus();
            }
            if (latest.Folders.Count == 0 && latest.CodeFolders.Count == 0)
            {
                latest.BackgroundIndexingEnabled = false;
                latest.SetUp = false;
            }
            else if (input.BackgroundTouched) latest.BackgroundIndexingEnabled = input.BackgroundIndexing;
            latest.Hotkey = input.Hotkey;
            latest.IndexHoursOn = input.HoursOn;
            latest.IndexHours.Start = input.HoursStart;
            latest.IndexHours.End = input.HoursEnd;
            latest.ProcessorOnly = input.ProcessorOnly;
            latest.TrayClick = input.TrayClick;
            latest.Theme = input.Theme;
            latest.CodeEditor = input.CodeEditor;
            latest.OpenAtLogin = input.OpenAtLogin;
            latest.Accelerator = input.Accelerator;
            latest.InstallExecutionProviders = input.InstallExecutionProviders;
            await backend.SaveSettingsAsync(latest);
            settings = latest;
            if (backgroundEditRevision == input.BackgroundRevision) backgroundIndexingTouched = false;
            StatusMessage = "Settings successfully saved.";
            return true;
        }
        catch (Exception error)
        {
            StatusMessage = error is InvalidOperationException && error.Message.Contains("revision conflict", StringComparison.OrdinalIgnoreCase)
                ? "Revision conflict: " + error.Message : "Save error: " + error.Message;
            if (bindingApplied && hotkeyService != null && previousHotkey != null)
            {
                // WS-R03: a failed older save must not undo a newer binding from another owner.
                try
                {
                    if (hotkeyService.BindingRevision != appliedRevision) StatusMessage += " A newer hotkey binding was preserved.";
                    else if (!hotkeyService.ApplyHotkey(previousHotkey)) StatusMessage += " The previous hotkey could not be restored.";
                }
                catch (ObjectDisposedException) { StatusMessage += " The hotkey service closed before rollback."; }
                ReadHotkeyStatus();
            }
            ResetRequestedHotkey();
            return false;
        }
        finally { saveGate.Release(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
