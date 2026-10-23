using WinSight.Core;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Runtime;

internal static class SettingsRevisionRegressions
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, string root)
    {
        await test("NM-UI-06-STALE-PREFERENCE-CANNOT-RESTORE-REVOKED-GRANTS", async () =>
        {
            var path = Path.Combine(root, "revision", "settings.json");
            var first = new JsonSettingsStore(path);
            var second = new JsonSettingsStore(path);
            var initial = new AppSettings { Folders = [Path.Combine(root, "authorized")], SetUp = true, BackgroundIndexingEnabled = true };
            await first.SaveAsync(initial);
            Check(initial.Revision == 1, "First committed revision was not reflected to its caller.");
            var oldPreferences = await first.LoadAsync();
            var revoke = await second.LoadAsync();
            revoke.Folders.Clear(); revoke.SetUp = false; revoke.BackgroundIndexingEnabled = false;
            await second.SaveAsync(revoke);
            var before = await File.ReadAllBytesAsync(path);
            oldPreferences.Theme = "Dark";
            await Throws<SettingsConflictException>(() => first.SaveAsync(oldPreferences));
            var after = await File.ReadAllBytesAsync(path);
            Check(before.SequenceEqual(after), "A stale preference save modified committed authorization.");
            var current = await second.LoadAsync();
            Check(current.Revision == 2 && current.Folders.Count == 0 && !current.SetUp && !current.BackgroundIndexingEnabled,
                "Revoked root/setup/background fields were restored.");
            Check(oldPreferences.Revision == 1, "A failed save advanced its caller revision.");
        });
        await test("NM-UI-06-SETTINGS-WRITER-LEASE-FAILS-CLOSED", async () =>
        {
            var path = Path.Combine(root, "writer-lease", "settings.json");
            var store = new JsonSettingsStore(path);
            var settings = new AppSettings();
            await store.SaveAsync(settings);
            var before = await File.ReadAllBytesAsync(path);
            using (var lease = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                settings.Theme = "Dark";
                await Throws<IOException>(() => new JsonSettingsStore(path).SaveAsync(settings));
            }
            var after = await File.ReadAllBytesAsync(path);
            Check(before.SequenceEqual(after) && settings.Revision == 1, "A writer bypassed another store's exclusive lease.");
            await store.SaveAsync(settings);
            Check((await store.LoadAsync()).Theme == "Dark" && settings.Revision == 2, "Failed lock acquisition prevented later valid save.");
        });
        await test("NM-UI-06-CONCURRENT-SAME-REVISION-HAS-ONE-WINNER", async () =>
        {
            var path = Path.Combine(root, "concurrent-preferences", "settings.json");
            var setup = new JsonSettingsStore(path);
            await setup.SaveAsync(new AppSettings());
            var first = new JsonSettingsStore(path); var second = new JsonSettingsStore(path);
            var a = await first.LoadAsync(); var b = await second.LoadAsync();
            a.Theme = "Dark"; b.Theme = "Light";
            var results = await Task.WhenAll(Attempt(first, a), Attempt(second, b));
            Check(results.Count(success => success) == 1, "Same-revision writers silently overwrote each other.");
            var current = await setup.LoadAsync();
            Check(current.Revision == 2 && current.Theme == (results[0] ? "Dark" : "Light"), "Committed settings do not match the winner.");
        });
        await test("NM-UI-06-ACTUAL-BACKEND-REJECTS-STALE-GRANT-SNAPSHOT", async () =>
        {
            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(Path.Combine(root, "backend-revision"),
                EnableBackgroundIndexing: false));
            var initial = await backend.LoadSettingsAsync();
            initial.Folders = [Path.Combine(root, "backend-documents")]; initial.SetUp = true; initial.BackgroundIndexingEnabled = true;
            await backend.SaveSettingsAsync(initial);
            var oldPreferences = await backend.LoadSettingsAsync();
            var revoke = await backend.LoadSettingsAsync();
            revoke.Folders.Clear(); revoke.BackgroundIndexingEnabled = false; revoke.SetUp = false;
            await backend.SaveSettingsAsync(revoke);
            oldPreferences.Theme = "Dark";
            await Throws<SettingsConflictException>(() => backend.SaveSettingsAsync(oldPreferences));
            var current = await backend.LoadSettingsAsync();
            Check(current.Folders.Count == 0 && !current.SetUp && !current.BackgroundIndexingEnabled,
                "Backend accepted an old preference object as a new grant.");
            Check((await backend.SearchAsync(new SearchRequest("synthetic", Mode: SearchMode.Keyword))).Hits.Count == 0,
                "Revoked roots remained searchable after rejecting a stale save.");
        });
    }

    private static async Task<bool> Attempt(JsonSettingsStore store, AppSettings settings)
    {
        try { await store.SaveAsync(settings); return true; }
        catch (SettingsConflictException) { return false; }
        catch (IOException) { return false; }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task Throws<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
