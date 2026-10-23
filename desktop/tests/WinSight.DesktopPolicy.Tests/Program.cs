using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;

int passed = 0, failed = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
}
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

await Test("WS-05 strict hotkey grammar rejects fallback Space and accepts case-insensitive named keys", () =>
{
    foreach (var invalid in new[] { "", "garbage", "Ctrl", "Ctrl+garbage", "Space", "A", "Ctrl++A", "+Ctrl+A", "Ctrl+A+B", "Ctrl+Ctrl+A", "Ctrl+F25" })
    {
        bool rejected = false;
        try { HotkeyService.ParseHotkey(invalid); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Accepted invalid hotkey: " + invalid);
    }
    Check(HotkeyService.ParseHotkey("ctrl+alt+f12") == HotkeyService.ParseHotkey("CTRL+ALT+F12"), "Function key casing changed the chord.");
    Check(HotkeyService.ParseHotkey(" Control + Shift + Space ") == (6u, 32u), "Valid Space chord changed.");
    Check(HotkeyService.ParseHotkey("Win+Alt+0") == (9u, 48u), "Valid digit chord changed.");
    return Task.CompletedTask;
});
await Test("WS-05 conflict and invalid input leave the existing registration and text intact", () =>
{
    var registrar = new Registrar();
    using var service = new HotkeyService("Ctrl+Shift+A", registrar.Register, registrar.Unregister, () => 1409);
    var original = registrar.Bindings.Single();
    int calls = registrar.RegisterCalls;
    Check(!service.ApplyHotkey("garbage") && registrar.RegisterCalls == calls, "Invalid input reached native registration.");
    Check(registrar.Bindings.Single().Equals(original), "Invalid input removed the existing binding.");
    registrar.RejectKey = (uint)'B';
    Check(!service.ApplyHotkey("Ctrl+Shift+B"), "Injected conflict was hidden.");
    Check(service.CurrentHotkey == "Ctrl+Shift+A" && registrar.Bindings.Single().Equals(original), "Conflict removed the working binding or changed its text.");
    Check(service.HasConflict && service.ConflictMessage?.Contains("preserved") == true, "Conflict was not exposed.");
    registrar.RejectKey = null;
    Check(service.ApplyHotkey("Ctrl+Shift+C") && registrar.Bindings.Count == 1, "Successful replacement leaked the old registration.");
    Check(registrar.Bindings.Single().Value.Key == 'C' && registrar.SuccessfulReplacementsWereAtomic, "Old chord was released before the replacement existed.");
    return Task.CompletedTask;
});
await Test("WS-05 release failure rolls back the spare registration; hold/resume and dispose release owned IDs", () =>
{
    var registrar = new Registrar();
    var service = new HotkeyService("Ctrl+Alt+A", registrar.Register, registrar.Unregister, () => 5);
    int originalId = registrar.Bindings.Single().Key;
    registrar.RejectUnregister = originalId;
    Check(!service.ApplyHotkey("Ctrl+Alt+B"), "Injected old-binding release failure was hidden.");
    Check(registrar.Bindings.Count == 1 && registrar.Bindings.Single().Value.Key == 'A', "Replacement was not rolled back.");
    registrar.RejectUnregister = null;
    service.HoldHotkey(true);
    Check(registrar.Bindings.Count == 0, "Focused hotkey editor retained a global binding.");
    Check(service.ApplyHotkey("Ctrl+Alt+f24") && registrar.Bindings.Count == 0, "Held binding registered before resume.");
    service.HoldHotkey(false);
    Check(registrar.Bindings.Count == 1 && registrar.Bindings.Single().Value.Key == 0x87, "Held chord was not restored.");
    service.Dispose(); service.Dispose();
    Check(registrar.Bindings.Count == 0, "Dispose leaked an owned registration.");
    return Task.CompletedTask;
});
await Test("WS-05 invalid settings never persist; power preference survives the settings merge", async () =>
{
    var backend = new SettingsBackend();
    var vm = new SettingsViewModel(backend);
    await vm.InitializeAsync();
    vm.Hotkey = "garbage";
    Check(!await vm.SaveAsync() && backend.Saves == 0, "Settings accepted an invalid hotkey.");
    vm.Hotkey = "Ctrl+Alt+f12";
    vm.ProcessorOnly = true;
    Check(await vm.SaveAsync() && backend.Saves == 1 && backend.Settings.ProcessorOnly, "Valid desktop policies were not persisted.");
    Check(backend.Settings.Folders.SequenceEqual(new[] { "C:/authorized" }), "Preference save replaced authorization roots.");
});
await Test("WS-R02 ordinary Save validates registration before committing any preferences", async () =>
{
    var registrar = new Registrar();
    using var service = new HotkeyService("Ctrl+Shift+A", registrar.Register, registrar.Unregister, () => 1409);
    var backend = new SettingsBackend();
    backend.Settings.Hotkey = "Ctrl+Shift+A";
    var vm = new SettingsViewModel(backend, hotkeyService: service);
    await vm.InitializeAsync();
    registrar.RejectKey = (uint)'B';
    vm.Hotkey = "Ctrl+Shift+B"; vm.Theme = "Dark";
    Check(!await vm.SaveAsync() && backend.Saves == 0, "Ordinary Save persisted an unregistered/conflicting chord.");
    Check(backend.Settings.Hotkey == "Ctrl+Shift+A" && backend.Settings.Theme == "Default" && service.CurrentHotkey == "Ctrl+Shift+A",
        "Rejected ordinary Save changed committed preferences or the live binding.");
    registrar.RejectKey = null; vm.Hotkey = "Ctrl+Shift+C";
    Check(await vm.SaveAsync() && backend.Settings.Hotkey == service.CurrentHotkey && service.CurrentHotkey == "Ctrl+Shift+C",
        "Ordinary Save did not activate and persist the same chord.");
});
await Test("WS-R03 queued saves serialize; an older failure cannot undo the newer successful chord", async () =>
{
    var registrar = new Registrar();
    using var service = new HotkeyService("Ctrl+Shift+A", registrar.Register, registrar.Unregister, () => 1409);
    var backend = new SettingsBackend(); backend.Settings.Hotkey = "Ctrl+Shift+A";
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    backend.BeforeSave = async _ =>
    {
        if (backend.Saves == 1) { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5)); throw new IOException("synthetic save failure"); }
    };
    var vm = new SettingsViewModel(backend, hotkeyService: service); await vm.InitializeAsync();
    vm.Hotkey = "Ctrl+Shift+B"; var older = vm.SaveAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    vm.Hotkey = "Ctrl+Shift+C"; var newer = vm.SaveAsync();
    Check(backend.Saves == 1 && service.CurrentHotkey == "Ctrl+Shift+B", "A queued save bypassed serialization.");
    release.TrySetResult();
    Check(!await older && await newer, "Injected failure/newer success was not reported accurately.");
    Check(service.CurrentHotkey == "Ctrl+Shift+C" && backend.Settings.Hotkey == "Ctrl+Shift+C" &&
        vm.Hotkey == "Ctrl+Shift+C" && registrar.Bindings.Count == 1, "Older rollback overwrote the newer binding or its persisted value.");
});
await Test("WS-R03 rollback does not overwrite a newer binding owned outside the pending save", async () =>
{
    var registrar = new Registrar();
    using var service = new HotkeyService("Ctrl+Shift+A", registrar.Register, registrar.Unregister, () => 1409);
    var backend = new SettingsBackend(); backend.Settings.Hotkey = "Ctrl+Shift+A";
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    backend.BeforeSave = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5)); throw new IOException("synthetic late failure"); };
    var vm = new SettingsViewModel(backend, hotkeyService: service); await vm.InitializeAsync();
    vm.Hotkey = "Ctrl+Shift+B"; var pending = vm.SaveAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(service.ApplyHotkey("Ctrl+Shift+C"), "Newer owner could not update the binding.");
    release.TrySetResult();
    Check(!await pending && service.CurrentHotkey == "Ctrl+Shift+C" && registrar.Bindings.Count == 1 &&
        backend.Settings.Hotkey == "Ctrl+Shift+A" && vm.StatusMessage.Contains("newer"), "Failed save silently rolled back a newer owner.");
});
await Test("WS-R03 closing the registrar during a failed save is reported without an escaping rollback exception", async () =>
{
    var registrar = new Registrar();
    using var service = new HotkeyService("Ctrl+Shift+A", registrar.Register, registrar.Unregister, () => 1409);
    var backend = new SettingsBackend(); backend.Settings.Hotkey = "Ctrl+Shift+A";
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    backend.BeforeSave = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5)); throw new IOException("synthetic closed save"); };
    var vm = new SettingsViewModel(backend, hotkeyService: service); await vm.InitializeAsync();
    vm.Hotkey = "Ctrl+Shift+B"; var pending = vm.SaveAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); service.Dispose(); release.TrySetResult();
    Check(!await pending && vm.StatusMessage.Contains("closed") && registrar.Bindings.Count == 0, "Disposed rollback escaped or retained a binding.");
});
Console.WriteLine($"RESULT passed={passed} failed={failed}; controlled registrar; no system hotkey registration performed");
return failed == 0 ? 0 : 1;

sealed class Registrar
{
    internal Dictionary<int, (uint Modifiers, uint Key)> Bindings { get; } = [];
    internal uint? RejectKey;
    internal int? RejectUnregister;
    internal int RegisterCalls;
    internal bool SuccessfulReplacementsWereAtomic = true;
    internal bool Register(int id, uint modifiers, uint key)
    {
        RegisterCalls++;
        if (key == RejectKey) return false;
        if (Bindings.ContainsKey(id)) throw new InvalidOperationException("Replacement reused a live ID.");
        if (RegisterCalls > 1 && Bindings.Count == 0) SuccessfulReplacementsWereAtomic = false;
        Bindings.Add(id, (modifiers, key)); return true;
    }
    internal bool Unregister(int id) => id == RejectUnregister ? false : Bindings.Remove(id);
}

sealed class SettingsBackend : IDesktopBackend
{
    internal AppSettings Settings { get; private set; } = new() { Folders = ["C:/authorized"] };
    internal int Saves;
    internal Func<AppSettings, Task>? BeforeSave;
    private static AppSettings Clone(AppSettings settings) => System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
    public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Clone(Settings));
    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Saves);
        if (BeforeSave != null) await BeforeSave(settings);
        Settings = Clone(settings);
    }
    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task IndexAsync(IndexRequest request, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetIndexingPausedAsync(bool paused, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EmbeddingInfo> GetEmbeddingInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
