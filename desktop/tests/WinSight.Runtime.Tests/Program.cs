using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Runtime;
using WinSight.Infrastructure.Storage;

var root = Path.Combine(Path.GetTempPath(), "WinSight.Native.Runtime.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<object>();
var failed = 0;
try
{
    await Test("CONFIG-ROUNDTRIP", async () =>
    {
        var store = new JsonSettingsStore(Path.Combine(root, "roundtrip", "settings.json"));
        var settings = await store.LoadAsync();
        Check(!File.Exists(store.SettingsPath), "Reading defaults must not save or import legacy settings.");
        Check(settings.IndexHours.Start == 60 && settings.IndexHours.End == 420, "Hours must retain minute units.");
        Check(settings.Folders.Count == 0 && !settings.OpenAtLogin, "No default directory or autostart authorization.");
        settings.Folders.Add(Path.Combine(root, "authorized"));
        settings.Theme = "Dark";
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        Check(loaded.Theme == "Dark" && loaded.Folders.SequenceEqual(settings.Folders), "Saved settings must reload.");
        settings.Theme = "Light";
        await store.SaveAsync(settings);
        Check(File.Exists(store.SettingsPath + ".previous"), "Atomic replacement must preserve a recovery copy.");
        Check((await store.LoadAsync()).Theme == "Light", "Replacement must take effect.");
    });
    await Test("CONFIG-MALFORMED-NOT-DEFAULT", async () =>
    {
        var path = Path.Combine(root, "malformed.json");
        await File.WriteAllTextAsync(path, "{bad");
        await Throws<JsonException>(() => new JsonSettingsStore(path).LoadAsync());
        Check(await File.ReadAllTextAsync(path) == "{bad", "Bad input must not be overwritten.");
    });
    await Test("CONFIG-DUPLICATE", async () =>
    {
        var path = Path.Combine(root, "duplicate.json");
        await File.WriteAllTextAsync(path, "{\"version\":1,\"version\":1}");
        await Throws<InvalidDataException>(() => new JsonSettingsStore(path).LoadAsync());
    });
    await Test("CONFIG-NEWER-VERSION", async () =>
    {
        var path = Path.Combine(root, "newer.json");
        await File.WriteAllTextAsync(path, "{\"version\":99}");
        await Throws<InvalidDataException>(() => new JsonSettingsStore(path).LoadAsync());
    });
    await Test("CONFIG-INVALID-SAVE-PRESERVES-OLD", async () =>
    {
        var store = new JsonSettingsStore(Path.Combine(root, "preserve.json"));
        await store.SaveAsync(new AppSettings());
        var before = await File.ReadAllBytesAsync(store.SettingsPath);
        await Throws<InvalidDataException>(() => store.SaveAsync(new AppSettings { Folders = ["relative"] }));
        var after = await File.ReadAllBytesAsync(store.SettingsPath);
        Check(before.SequenceEqual(after), "Invalid save changed prior data.");
        await Throws<InvalidDataException>(() => store.SaveAsync(new AppSettings { IndexHours = new() { Start = 1440 } }));
    });
    await Test("CONFIG-SIZE-BOUND", async () =>
    {
        var path = Path.Combine(root, "huge.json");
        await File.WriteAllBytesAsync(path, new byte[1024 * 1024 + 1]);
        await Throws<InvalidDataException>(() => new JsonSettingsStore(path).LoadAsync());
    });
    await Test("CONFIG-CANCELLATION", async () =>
    {
        var store = new JsonSettingsStore(Path.Combine(root, "cancel.json"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Throws<OperationCanceledException>(() => store.SaveAsync(new AppSettings(), canceled.Token));
        Check(!File.Exists(store.SettingsPath), "Canceled save committed.");
    });
    await Test("RUNTIME-NO-ROOT-NO-MODEL", async () =>
    {
        await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(Path.Combine(root, "runtime")));
        var response = await backend.SearchAsync(new SearchRequest("test"));
        Check(response.Hits.Count == 0 && response.Notice is not null && !response.SemanticAvailable,
            "Unconfigured app must not load a model or discover personal roots.");
        await Throws<UnauthorizedAccessException>(() => backend.IndexAsync(new IndexRequest([root])));
    });
    await Test("RUNTIME-DISPOSE", async () =>
    {
        var backend = await DesktopBackend.CreateAsync(new DesktopOptions(Path.Combine(root, "dispose")));
        await backend.DisposeAsync();
        await backend.DisposeAsync();
        await Throws<ObjectDisposedException>(() => backend.SearchAsync(new SearchRequest("test")));
    });
    await Test("SCHEDULE-MINUTES-CROSS-MIDNIGHT", () =>
    {
        var night = new IndexHours { Start = 22 * 60, End = 6 * 60 };
        Check(BackgroundIndexCoordinator.IsScheduledTime(night, 23 * 60), "Late-night interval omitted.");
        Check(BackgroundIndexCoordinator.IsScheduledTime(night, 0), "Midnight interval omitted.");
        Check(!BackgroundIndexCoordinator.IsScheduledTime(night, 6 * 60), "End of interval must be exclusive.");
        Check(!BackgroundIndexCoordinator.IsScheduledTime(night, 12 * 60), "Daytime included in night schedule.");
        Check(BackgroundIndexCoordinator.IsScheduledTime(new IndexHours { Start = 60, End = 60 }, 0), "Equal bounds mean all day.");
        return Task.CompletedTask;
    });
    await Test("WATCHER-REAL-SYNTHETIC-FILE-EVENT", async () =>
    {
        var watched = Path.Combine(root, "watched");
        Directory.CreateDirectory(watched);
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        await using var watcher = new BackgroundIndexCoordinator(new AppSettings { Folders = [watched], ProcessorOnly = false },
            (request, token) =>
            {
                Check(request.Roots.SequenceEqual(new[] { watched }), "Watcher widened roots.");
                if (Interlocked.Increment(ref count) == 1) initial.TrySetResult();
                else changed.TrySetResult();
                return Task.CompletedTask;
            }, errors.Enqueue, debounce: TimeSpan.FromMilliseconds(40));
        await initial.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(Path.Combine(watched, "synthetic.txt"), "synthetic watcher event");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(errors.IsEmpty, "Synthetic watcher reported a failure.");
    });
    await QueueWatcherCases.RunAsync(root, Test);
    await Test("WATCHER-DISPOSE-CANCELS-INFLIGHT", async () =>
    {
        var watched = Path.Combine(root, "watch-dispose");
        Directory.CreateDirectory(watched);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BackgroundIndexCoordinator(new AppSettings { Folders = [watched], ProcessorOnly = false },
            async (request, token) => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); },
            error => throw new InvalidOperationException("Unexpected watcher error.", error),
            debounce: TimeSpan.FromMilliseconds(1));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await watcher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await watcher.DisposeAsync(); }
    });
    await Test("RUNTIME-NAME-LONGTEXT-CODE-ROOTS", async () =>
    {
        var data = Path.Combine(root, "e2e-data");
        var documents = Path.Combine(root, "e2e-documents");
        var code = Path.Combine(root, "e2e-code");
        Directory.CreateDirectory(documents);
        Directory.CreateDirectory(code);
        var document = Path.Combine(documents, "uniquefilenamemarker.txt");
        var source = Path.Combine(code, "source.cs");
        var text = new string('x', 700) + " zephyrfinaltoken";
        await File.WriteAllTextAsync(document, text);
        await File.WriteAllTextAsync(source, "beacon");
        await using (var repository = new SqliteIndexRepository(Path.Combine(data, "index", "index.sqlite")))
        {
            await repository.InitializeAsync();
            await repository.UpsertFileAsync(new IndexedFile(0, document, "doc", text.Length, 1, "done", null, null,
                [new IndexSegment("chunk", "none", null, null, text)]));
        }
        await using (var repository = new SqliteIndexRepository(Path.Combine(data, "index", "code.sqlite")))
        {
            await repository.InitializeAsync();
            await repository.UpsertFileAsync(new IndexedFile(0, source, "code", 6, 1, "done", null, null,
                [new IndexSegment("lines", "none", 1, 1, "beacon")]));
        }
        await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data));
        var settings = await backend.LoadSettingsAsync();
        settings.Folders = [documents]; settings.CodeFolders = [code]; settings.SetUp = true;
        await backend.SaveSettingsAsync(settings);
        var named = await backend.SearchAsync(new SearchRequest("uniquefilenamemarker", Mode: SearchMode.Keyword));
        Check(named.Hits.Any(hit => hit.Path == document), "Repository-owned filename index was lost.");
        var longText = await backend.SearchAsync(new SearchRequest("zephyrfinaltoken", Mode: SearchMode.Keyword));
        Check(longText.Hits.Any(hit => hit.Path == document), "Full chunk body was truncated before FTS storage.");
        var codeHits = await backend.SearchAsync(new SearchRequest("code: beacon", Mode: SearchMode.Keyword));
        Check(codeHits.Hits.Any(hit => hit.Path == source), "code: request was not routed to code.sqlite.");
        var ordinary = await backend.SearchAsync(new SearchRequest("beacon", Mode: SearchMode.Keyword));
        Check(ordinary.Hits.Count == 0, "Code index leaked into ordinary document search.");
        await Throws<UnauthorizedAccessException>(() => backend.SearchAsync(new SearchRequest("test", Mode: SearchMode.Keyword,
            Root: documents + "-sibling")));
        settings.Folders.Clear(); settings.CodeFolders.Clear();
        await backend.SaveSettingsAsync(settings);
        var revoked = await backend.SearchAsync(new SearchRequest("zephyrfinaltoken", Mode: SearchMode.Keyword));
        Check(revoked.Hits.Count == 0, "Revoked root remained searchable.");
    });
    await ReviewRegressions.RunAsync(Test, root);
    await SettingsRevisionRegressions.RunAsync(Test, root);
    await WsRegressions.RunAsync(Test, root, args.FirstOrDefault());
    await Test("VECTOR-CONTRACT", () =>
    {
        var valid = new float[768]; valid[0] = 1;
        VectorContract.Validate(valid);
        ExpectInvalid(new float[767]);
        ExpectInvalid(new float[768]);
        valid[0] = float.NaN; ExpectInvalid(valid);
        valid[0] = 5; ExpectInvalid(valid);
        return Task.CompletedTask;
    });
}
finally
{
    Console.WriteLine(JsonSerializer.Serialize(new { suite = "runtime", passed = results.Count - failed,
        failed, assertions = results }, new JsonSerializerOptions { WriteIndented = true }));
    Directory.Delete(root, recursive: true);
}
return failed == 0 ? 0 : 1;

async Task Test(string id, Func<Task> action)
{
    try { await action(); results.Add(new { id, passed = true }); }
    catch (Exception error)
    {
        failed++;
        results.Add(new { id, passed = false, error = error.GetType().Name + ": " + error.Message });
    }
}
static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
static void ExpectInvalid(float[] vector)
{
    try { VectorContract.Validate(vector); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Invalid vector accepted.");
}
