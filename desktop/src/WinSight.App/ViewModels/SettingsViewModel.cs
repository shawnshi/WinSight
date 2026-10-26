using System.ComponentModel;
using System.Reflection;
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

    // Read-only identity shown in the About card; the version is the assembly informational version.
    public string AppVersion
    {
        get
        {
            var assembly = typeof(SettingsViewModel).Assembly;
            var informational = assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational)
                ? assembly.GetName().Version?.ToString() ?? "unknown"
                : informational;
        }
    }

    public string RepositoryUrl => "https://github.com/shawnshi/WinSight";

    private string foundrySummary = "Not probed. Discovery never downloads anything.";
    private IReadOnlyList<string> foundryProviders = Array.Empty<string>();
    public string FoundrySummary => foundrySummary;
    public IReadOnlyList<string> FoundryProviders => foundryProviders;

    private bool foundryBusy;
    public bool FoundryBusy
    {
        get => foundryBusy;
        private set { if (SetProperty(ref foundryBusy, value)) OnPropertyChanged(nameof(CanInstallFoundryProviders)); }
    }
    public bool CanInstallFoundryProviders => !foundryBusy;

    /// <summary>
    /// Downloads and registers the providers Foundry reports as applicable but not registered. This is the
    /// only Foundry action that uses the network, and the result is shown verbatim including failures.
    /// </summary>
    public async Task InstallFoundryProvidersAsync()
    {
        if (foundryBusy) return;
        FoundryBusy = true;
        foundrySummary = "Installing the applicable Foundry Local providers… this can take a few minutes.";
        OnPropertyChanged(nameof(FoundrySummary));
        try
        {
            var result = await backend.InstallFoundryProvidersAsync();
            if (result is null)
            {
                foundrySummary = "This build has no Foundry Local backend to install providers into.";
            }
            else
            {
                var registered = result.Registered.Count == 0 ? "none" : string.Join(", ", result.Registered);
                var failed = result.Failed.Count == 0 ? "none" : string.Join(", ", result.Failed);
                foundrySummary = (result.Success ? "Providers installed. " : "Provider installation reported a problem. ")
                    + $"status: {result.Status}; registered: {registered}; failed: {failed}"
                    + (result.Error is { Length: > 0 } error ? "; detail: " + error : "");
            }
            OnPropertyChanged(nameof(FoundrySummary));
            await ProbeFoundryAsync();
        }
        finally
        {
            FoundryBusy = false;
        }
    }

    private string npuSummary = "Not probed. The Windows ML host registers local provider libraries only.";
    private IReadOnlyList<string> npuDevices = Array.Empty<string>();
    private string npuQualification = "Qualification runs when the NPU host actually loads a model.";
    private bool npuBusy;
    public string NpuSummary => npuSummary;
    public IReadOnlyList<string> NpuDevices => npuDevices;
    public string NpuQualification => npuQualification;
    public bool NpuBusy
    {
        get => npuBusy;
        private set { if (SetProperty(ref npuBusy, value)) OnPropertyChanged(nameof(CanProbeNpu)); }
    }
    public bool CanProbeNpu => !npuBusy;

    private string clientConfigPath = "";
    private string clientConfigSummary = "Point at a client configuration file, then preview before writing.";
    private bool clientConfigBusy;
    public string ClientConfigPath
    {
        get => clientConfigPath;
        set => SetProperty(ref clientConfigPath, value);
    }
    public string ClientConfigSummary => clientConfigSummary;

    private bool useHttpTransport;
    private string httpPortText = "18771";
    public bool UseHttpTransport
    {
        get => useHttpTransport;
        set
        {
            if (!SetProperty(ref useHttpTransport, value)) return;
            OnPropertyChanged(nameof(UseStdioTransport));
            OnPropertyChanged(nameof(ClientTransportDescription));
        }
    }
    public string HttpPortText
    {
        get => httpPortText;
        set => SetProperty(ref httpPortText, value);
    }
    /// <summary>Radio-button convenience: selecting stdio is the negation of the HTTP flag.</summary>
    public bool UseStdioTransport
    {
        get => !useHttpTransport;
        set { if (value) UseHttpTransport = false; }
    }

    public string ClientTransportDescription => useHttpTransport
        ? "HTTP entry: the client needs --http-port running and the WINSIGHT_MCP_TOKEN environment variable; the token is never written to the file."
        : "stdio entry: the client launches the bridge directly; no token is involved.";
    public bool ClientConfigBusy
    {
        get => clientConfigBusy;
        private set { if (SetProperty(ref clientConfigBusy, value)) OnPropertyChanged(nameof(CanConfigureClient)); }
    }
    public bool CanConfigureClient => !clientConfigBusy;

    private string modelsSummary = "Models are inspected when you refresh.";
    public string ModelsSummary => modelsSummary;

    /// <summary>Shows which models are loaded and on which device, without pretending a fallback is acceleration.</summary>
    public async Task RefreshModelsAsync()
    {
        try
        {
            var inventory = await backend.GetModelInventoryAsync();
            modelsSummary = inventory is null
                ? "The model inventory is unavailable in this build."
                : $"Embedding: {inventory.EmbeddingModel} @ {Shorten(inventory.EmbeddingRevision)} · profile {Shorten(inventory.EmbeddingProfile)} · device: {inventory.EmbeddingDevice}{Environment.NewLine}"
                  + $"OCR: {inventory.OcrEngine} {inventory.OcrVersion} · profile {Shorten(inventory.OcrProfile)} · device: {inventory.OcrDevice}";
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException)
        {
            modelsSummary = "Model inspection failed: " + error.Message;
        }
        OnPropertyChanged(nameof(ModelsSummary));
    }

    private static string Shorten(string value) => value.Length <= 12 ? value : value[..12] + "…";

    private string rebuildSummary = "Rebuild is only needed when the model or the lexicon changed.";
    public string RebuildSummary => rebuildSummary;

    public async Task RefreshRebuildStateAsync()
    {
        try
        {
            var needed = await backend.RebuildNeededAsync();
            rebuildSummary = needed ? "A rebuild is required before maintenance requests can index." : "The current index generation matches the model and lexicon.";
            if (backend.RebuildStatus != "idle") rebuildSummary += " Status: " + backend.RebuildStatus + ".";
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException)
        {
            rebuildSummary = "Rebuild state unavailable: " + error.Message;
        }
        OnPropertyChanged(nameof(RebuildSummary));
    }

    /// <summary>Starts a whole-corpus rebuild in the background; the window stays responsive.</summary>
    public async Task StartRebuildAsync()
    {
        try
        {
            var outcome = await backend.StartRebuildAsync();
            rebuildSummary = outcome == "started"
                ? "Rebuild started in the background; progress appears here."
                : "Rebuild not started: " + outcome;
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException)
        {
            rebuildSummary = "Rebuild failed to start: " + error.Message;
        }
        OnPropertyChanged(nameof(RebuildSummary));
    }

    private string maintenanceSummary = "Pending requests from MCP clients appear here; Drain hands accepted paths to the indexing queue.";
    private bool maintenanceBusy;
    public string MaintenanceSummary => maintenanceSummary;
    public bool CanManageMaintenance => !maintenanceBusy;

    /// <summary>Creates the grant an MCP client must present before its index_directory request is accepted.</summary>
    private async Task GrantMaintenanceAsync()
    {
        if (maintenanceBusy) return;
        maintenanceBusy = true;
        try
        {
            var id = await backend.GrantMaintenanceAsync();
            maintenanceSummary = id is null
                ? "This build cannot create a maintenance grant."
                : "Grant created. Give the client this identifier: " + id + ". Revoke it when you are done.";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            maintenanceSummary = "Grant failed: " + error.Message;
        }
        finally { maintenanceBusy = false; OnPropertyChanged(nameof(MaintenanceSummary)); OnPropertyChanged(nameof(CanManageMaintenance)); }
    }

    private async Task RevokeMaintenanceAsync()
    {
        if (maintenanceBusy) return;
        maintenanceBusy = true;
        try
        {
            await backend.RevokeMaintenanceAsync();
            maintenanceSummary = "Grant revoked; pending and future maintenance requests will be refused.";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            maintenanceSummary = "Revoke failed: " + error.Message;
        }
        finally { maintenanceBusy = false; OnPropertyChanged(nameof(MaintenanceSummary)); OnPropertyChanged(nameof(CanManageMaintenance)); }
    }

    /// <summary>Drains requests the bridge left behind and reports each outcome.</summary>
    public async Task DrainMaintenanceAsync()
    {
        if (maintenanceBusy) return;
        maintenanceBusy = true;
        try
        {
            var outcomes = await backend.DrainMaintenanceJobsAsync();
            maintenanceSummary = outcomes.Count == 0 ? "No pending maintenance requests." : string.Join("; ", outcomes);
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException)
        {
            maintenanceSummary = "Drain failed: " + error.Message;
        }
        finally { maintenanceBusy = false; OnPropertyChanged(nameof(MaintenanceSummary)); OnPropertyChanged(nameof(CanManageMaintenance)); }
    }

    /// <summary>Reads the named configuration and reports what would change. Nothing is written.</summary>
    public async Task PreviewClientConfigAsync()
    {
        if (clientConfigBusy) return;
        if (string.IsNullOrWhiteSpace(clientConfigPath)) { clientConfigSummary = "Enter the path of the client configuration file first."; OnPropertyChanged(nameof(ClientConfigSummary)); return; }
        ClientConfigBusy = true;
        try
        {
            var plan = await backend.PreviewClientConfigAsync(clientConfigPath.Trim(), useHttpTransport ? "http" : "stdio", ParsePort());
            clientConfigSummary = Describe(plan, "Preview");
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            clientConfigSummary = "Preview failed: " + error.Message;
        }
        finally { ClientConfigBusy = false; OnPropertyChanged(nameof(ClientConfigSummary)); }
    }

    /// <summary>Applies the entry after a backup; a conflicting entry is refused rather than overwritten.</summary>
    public async Task ApplyClientConfigAsync()
    {
        if (clientConfigBusy) return;
        if (string.IsNullOrWhiteSpace(clientConfigPath)) { clientConfigSummary = "Enter the path of the client configuration file first."; OnPropertyChanged(nameof(ClientConfigSummary)); return; }
        ClientConfigBusy = true;
        try
        {
            var plan = await backend.ApplyClientConfigAsync(clientConfigPath.Trim(), useHttpTransport ? "http" : "stdio", ParsePort());
            clientConfigSummary = Describe(plan, "Apply");
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            clientConfigSummary = "Apply failed: " + error.Message;
        }
        finally { ClientConfigBusy = false; OnPropertyChanged(nameof(ClientConfigSummary)); }
    }

    private int ParsePort() => int.TryParse(httpPortText, out var port) ? port : 0;

    private static string Describe(ClientConfigPreview? plan, string verb)
    {
        if (plan is null) return verb + ": the client configuration assistant is unavailable in this build.";
        var state = plan.Blocked ? "refused" : plan.AlreadyConnected ? "already connected" : "ready";
        var backup = plan.BackupPath is { Length: > 0 } path ? " Backup: " + System.IO.Path.GetFileName(path) + "." : "";
        var reason = plan.BlockReason is { Length: > 0 } detail ? " " + detail : "";
        return $"{verb}: {state}. {plan.Summary}{reason}{backup}";
    }

    /// <summary>Live view of the isolated Windows ML host: the devices it sees and the gate's own verdict text.</summary>
    public async Task ProbeNpuAsync()
    {
        if (npuBusy) return;
        NpuBusy = true;
        npuSummary = "Probing the Windows ML host…";
        OnPropertyChanged(nameof(NpuSummary));
        try
        {
            var probe = await backend.ProbeWinMlAsync();
            if (probe is null || probe.Error is { Length: > 0 })
            {
                npuSummary = "Windows ML host unavailable: " + (probe?.Error ?? "no backend");
                npuDevices = Array.Empty<string>();
            }
            else
            {
                var accelerated = probe.Devices.Count(d => !string.Equals(d.Type, "CPU", StringComparison.OrdinalIgnoreCase));
                npuSummary = $"ORT {probe.OrtVersion}; {probe.Devices.Count} device(s), {accelerated} accelerated; this host never downloads providers.";
                npuDevices = probe.Devices.Select(d => $"{d.Ep} · {d.Type} · device {d.DeviceId} · vendor {d.VendorId}").ToArray();
            }
            OnPropertyChanged(nameof(NpuSummary));
            OnPropertyChanged(nameof(NpuDevices));
            npuQualification = await DescribeNpuQualificationAsync();
            OnPropertyChanged(nameof(NpuQualification));
        }
        finally
        {
            NpuBusy = false;
        }
    }

    /// <summary>
    /// The gate's reasons are produced while a model is actually loaded, so this reports the last recorded
    /// status verbatim instead of inventing a verdict.
    /// </summary>
    private async Task<string> DescribeNpuQualificationAsync()
    {
        try
        {
            var info = await backend.GetEmbeddingInfoAsync();
            var status = info.BackendStatus;
            return string.IsNullOrWhiteSpace(status)
                ? "No qualification text has been recorded yet; it appears after the NPU host loads a model."
                : status;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException)
        {
            return "Qualification state unavailable: " + error.GetType().Name + ".";
        }
    }

    /// <summary>Runs the isolated Foundry host in probe mode; it only enumerates providers.</summary>
    public async Task ProbeFoundryAsync()
    {
        foundrySummary = "Probing the isolated Foundry Local host…";
        OnPropertyChanged(nameof(FoundrySummary));
        var probe = await backend.ProbeFoundryAsync();
        if (probe is null)
        {
            foundrySummary = "Foundry Local backend is unavailable in this build.";
            foundryProviders = Array.Empty<string>();
        }
        else if (probe.Error is { Length: > 0 } error)
        {
            foundrySummary = "Foundry Local unavailable: " + error;
            foundryProviders = Array.Empty<string>();
        }
        else
        {
            var registered = probe.Providers.Count(p => p.Registered);
            foundrySummary = $"Foundry Local SDK {probe.Sdk}; device types {string.Join('/', probe.DeviceTypes)}; "
                + $"{probe.Providers.Count} provider(s), {registered} registered.";
            foundryProviders = probe.Providers
                .Select(p => p.Name + (p.Registered ? " (registered)" : " (not registered)"))
                .ToArray();
        }
        OnPropertyChanged(nameof(FoundrySummary));
        OnPropertyChanged(nameof(FoundryProviders));
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
