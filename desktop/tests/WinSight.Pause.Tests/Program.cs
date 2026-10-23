using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Runtime;
using WinSight.Infrastructure.Storage;

if (args.Length != 2 || !File.Exists(args[0])) throw new ArgumentException("Pass the explicit fake-host executable and a task-owned artifact directory.");
var fakeHost = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
var results = new List<object>();
var failed = 0;

await Test("CONTROLLER-PRECANCEL-NO-STATE-CHANGE", async () =>
{
    var control = new IndexingController();
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await Throws<OperationCanceledException>(() => control.SetPausedAsync(true, cancelled.Token));
    await Throws<OperationCanceledException>(() => control.RunAsync(_ => throw new Exception("Must not enter"), cancelled.Token));
    Check(control.State == IndexingControlState.Running && !control.HasWork, "Precancel changed admission.");
    await control.StopAsync();
});

await Test("CONTROLLER-ACTIVE-QUEUED-EPOCH-AND-DUPLICATE-TRANSITIONS", async () =>
{
    var control = new IndexingController();
    var entered = Signal(); var release = Signal(); var queuedEntered = false;
    var active = control.RunAsync(async _ => { entered.SetResult(); await release.Task; }, default);
    await entered.Task;
    var queued = control.RunAsync(_ => { queuedEntered = true; return Task.CompletedTask; }, default);
    var pause = control.SetPausedAsync(true, default);
    try
    {
        Check(control.State == IndexingControlState.Pausing && !pause.IsCompleted, "Claimed Paused while work remains.");
        await Throws<OperationCanceledException>(() => queued);
        await Throws<IndexingPausedException>(() => control.RunAsync(_ => Task.CompletedTask, default));
        Check(!queuedEntered, "Queued work crossed its epoch.");
    }
    finally { release.TrySetResult(); }
    await Throws<OperationCanceledException>(() => active);
    await pause;
    Check(control.State == IndexingControlState.Paused && !control.HasWork, "Did not quiesce.");
    await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => control.SetPausedAsync(true, default)));
    await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => control.SetPausedAsync(false, default)));
    var starts = 0;
    await control.RunAsync(_ => { starts++; return Task.CompletedTask; }, default);
    Check(starts == 1 && !queuedEntered, "Resume replayed cancelled work.");
    await control.StopAsync();
});

await Test("CONTROLLER-MUTEX-AND-CALLER-QUEUE-CANCELLATION", async () =>
{
    var control = new IndexingController();
    var release = Signal(); var current = 0; var max = 0;
    var active = control.RunAsync(async _ => { current++; max = Math.Max(max, current); await release.Task; current--; }, default);
    using var cancel = new CancellationTokenSource();
    var queued = control.RunAsync(_ => throw new Exception("Cancelled queue entered"), cancel.Token);
    cancel.Cancel();
    await Throws<OperationCanceledException>(() => queued);
    var rest = Enumerable.Range(0, 10).Select(_ => control.RunAsync(async token =>
    { current++; max = Math.Max(max, current); await Task.Delay(1, token); current--; }, default)).ToArray();
    release.SetResult();
    await Task.WhenAll(rest.Append(active));
    Check(max == 1, "Indexing overlapped.");
    await control.StopAsync();
});

await Test("CONTROLLER-UNCOOPERATIVE-TIMEOUT-IS-VISIBLE-AND-FAILS-CLOSED", async () =>
{
    var control = new IndexingController(TimeSpan.FromMilliseconds(80));
    var release = Signal();
    var work = control.RunAsync(_ => release.Task, default);
    try
    {
        var clock = Stopwatch.StartNew();
        var pause = control.SetPausedAsync(true, default);
        var competing = Enumerable.Range(0, 24).Select(i => control.SetPausedAsync(i % 2 == 0, default)).ToArray();
        await Throws<TimeoutException>(() => pause);
        await Task.WhenAll(competing.Select(task => Throws<TimeoutException>(() => task)));
        Check(clock.Elapsed < TimeSpan.FromSeconds(1), "Queued transitions each restarted the timeout budget.");
        Check(control.State == IndexingControlState.Pausing && control.Failure is not null, "Timeout faked Paused or hid failure.");
        await Throws<IndexingPausedException>(() => control.RunAsync(_ => throw new Exception("Must not enter"), default));
        await Throws<TimeoutException>(() => control.SetPausedAsync(false, default));
        Check(control.State == IndexingControlState.Pausing, "Resume bypassed old work.");
    }
    finally { release.TrySetResult(); }
    await Throws<OperationCanceledException>(() => work);
    await control.SetPausedAsync(false, default);
    Check(control.State == IndexingControlState.Running && control.Failure is null, "Quiescent recovery failed.");
    await control.StopAsync();
});

await Test("CONTROLLER-CALLBACK-OUTSIDE-MONITOR-AND-RETIRE-AFTER-CANCEL", async () =>
{
    var control = new IndexingController(TimeSpan.FromMilliseconds(100));
    var callbackStarted = Signal(); var callbackRelease = Signal(); var callbackExited = Signal(); var workRelease = Signal();
    var work = control.RunAsync(async token =>
    {
        using var registration = token.Register(() =>
        {
            Task.Run(() => Check(control.State == IndexingControlState.Pausing, "Callback cannot inspect state.")).GetAwaiter().GetResult();
            callbackStarted.TrySetResult();
            callbackRelease.Task.GetAwaiter().GetResult();
            callbackExited.TrySetResult();
        });
        await workRelease.Task;
    }, default);
    var pause = control.SetPausedAsync(true, default);
    try
    {
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Throws<TimeoutException>(() => pause);
        Check(control.State == IndexingControlState.Pausing, "Cancellation callbacks still running but Paused published.");
    }
    finally { callbackRelease.TrySetResult(); workRelease.TrySetResult(); }
    await callbackExited.Task;
    await Throws<OperationCanceledException>(() => work);
    await control.SetPausedAsync(false, default);
    await control.RunAsync(_ => Task.CompletedTask, default);
    await control.StopAsync();
});

await Test("CONTROLLER-TRANSITION-CANCEL-AND-SHUTDOWN-WINS", async () =>
{
    var control = new IndexingController(); var release = Signal();
    var work = control.RunAsync(_ => release.Task, default);
    using var cancel = new CancellationTokenSource();
    var pause = control.SetPausedAsync(true, cancel.Token);
    cancel.Cancel();
    await Throws<OperationCanceledException>(() => pause);
    Check(control.State == IndexingControlState.Pausing, "Cancelled transition reopened admission.");
    var stop = control.StopAsync();
    var resume = control.SetPausedAsync(false, default);
    release.SetResult();
    await Throws<OperationCanceledException>(() => work);
    await stop;
    await Throws<ObjectDisposedException>(() => resume);
    await Throws<ObjectDisposedException>(() => control.RunAsync(_ => Task.CompletedTask, default));
    await control.StopAsync();
});

await Test("CONTROLLER-CALLBACK-FAULT-REMAINS-ERROR", async () =>
{
    var control = new IndexingController(); var release = Signal(); var callbackStarted = Signal();
    var work = control.RunAsync(async token =>
    {
        using var registration = token.Register(() => { callbackStarted.TrySetResult(); throw new IOException("synthetic callback fault"); });
        await release.Task;
    }, default);
    var pause = control.SetPausedAsync(true, default);
    await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    release.SetResult();
    await Throws<OperationCanceledException>(() => work);
    await Throws<AggregateException>(() => pause);
    Check(control.State == IndexingControlState.Pausing && control.Failure is not null, "Callback error hidden.");
    await Throws<AggregateException>(() => control.StopAsync());
});

await Test("WATCHER-INTENTIONAL-PAUSE-NOT-A-PERMANENT-FAULT", async () =>
{
    var documents = Path.Combine(root, "watcher"); Directory.CreateDirectory(documents);
    var control = new IndexingController(); var entered = Signal(); var resumed = Signal();
    var errors = new ConcurrentQueue<Exception>(); var calls = 0;
    await using var watcher = new BackgroundIndexCoordinator(new AppSettings { Folders = [documents], ProcessorOnly = false },
        (request, token) => control.RunAsync(async workToken =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, workToken); }
            else resumed.TrySetResult();
        }, token), errors.Enqueue, debounce: TimeSpan.FromMilliseconds(5), indexingAllowed: () => control.State == IndexingControlState.Running);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    await control.SetPausedAsync(true, default);
    for (var i = 0; i < 20; i++) watcher.RequestReconciliation();
    await Task.Delay(80);
    Check(errors.IsEmpty && !watcher.Completion.IsCompleted && calls == 1, "Pause faulted or spun watcher.");
    await control.SetPausedAsync(false, default); watcher.RequestReconciliation();
    await resumed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(errors.IsEmpty, "Resume produced watcher error.");
    await control.StopAsync();
});

await Test("BACKEND-REJECTED-REQUESTS-NO-WRITES-OR-MODEL-START-SEARCH-AND-SAVE", async () =>
{
    var fixture = await Fixture("rejection", "success", false);
    await using var backend = fixture.Backend;
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await Throws<OperationCanceledException>(() => backend.SetIndexingPausedAsync(true, cancel.Token));
    await backend.SetIndexingPausedAsync(true);
    await Throws<IndexingPausedException>(() => backend.IndexAsync(new IndexRequest([fixture.Documents])));
    Check(!Directory.Exists(Path.Combine(fixture.Data, "index")) && !File.Exists(fixture.Audit), "Rejected request created index/model.");
    var settings = await backend.LoadSettingsAsync();
    var revision = settings.Revision;
    settings.Theme = "Dark";
    await backend.SaveSettingsAsync(settings);
    Check(settings.Revision == revision + 1, "CAS revision changed incorrectly.");
    var stale = new JsonSettingsStore(Path.Combine(fixture.Data, "settings.json"));
    await Throws<SettingsConflictException>(() => stale.SaveAsync(new AppSettings { Revision = revision }));
    Check((await backend.GetStatusAsync()).IndexingState == IndexingControlState.Paused, "Settings save resumed work.");
    Check((await backend.SearchAsync(new SearchRequest("synthetic", Mode: SearchMode.Keyword))).Hits.Count == 0, "Empty index search failed.");
    var snapshot = await File.ReadAllBytesAsync(Path.Combine(fixture.Data, "settings.json"));
    await backend.SetIndexingPausedAsync(false);
    await backend.SetIndexingPausedAsync(false);
    await Task.Delay(700);
    Check(!File.Exists(fixture.Audit), "Resume enabled unauthorized background indexing.");
    await backend.IndexAsync(new IndexRequest([fixture.Documents]));
    Check((await backend.SearchAsync(new SearchRequest("synthetic", Mode: SearchMode.Keyword))).Hits.Count == 1, "Explicit resumed indexing/search failed.");
    Check(snapshot.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture.Data, "settings.json"))), "Pause/resume mutated saved permission/profile/settings.");
});

await Test("BACKEND-PENDING-AND-QUEUED-CANCEL-NO-FALSE-DONE-PROFILE-PRESERVED", async () =>
{
    var fixture = await Fixture("pending", "cancel", false);
    await using var backend = fixture.Backend;
    var settingsBytes = await File.ReadAllBytesAsync(Path.Combine(fixture.Data, "settings.json"));
    var active = backend.IndexAsync(new IndexRequest([fixture.Documents]));
    await WaitFor(() => File.Exists(fixture.Audit) && File.ReadAllText(fixture.Audit).Contains("\"op\":\"text\""));
    var queued = backend.IndexAsync(new IndexRequest([fixture.Documents], Force: true));
    await backend.SetIndexingPausedAsync(true);
    await Throws<OperationCanceledException>(() => active);
    await Throws<OperationCanceledException>(() => queued);
    var status = await backend.GetStatusAsync();
    Check(status.IndexingState == IndexingControlState.Paused && status.Pending == 1 && status.Done == 0 && status.Failed == 0, "Cancellation wrote a false done/failed state.");
    Check(!status.Detail!.Contains("failed:", StringComparison.OrdinalIgnoreCase), "Pause became indexing error.");
    var db = Path.Combine(fixture.Data, "index", "index.sqlite");
    await using var repository = new SqliteIndexRepository(db); await repository.InitializeAsync();
    var profile = await repository.GetMetaAsync("profile_id");
    var before = Snapshot(fixture.Data);
    var auditBefore = File.ReadAllText(fixture.Audit);
    await Throws<IndexingPausedException>(() => backend.IndexAsync(new IndexRequest([fixture.Documents])));
    await Throws<IndexingPausedException>(() => backend.IndexAsync(new IndexRequest([fixture.Documents], Force: true)));
    await Task.Delay(150);
    Check(before == Snapshot(fixture.Data) && auditBefore == File.ReadAllText(fixture.Audit), "Paused rejection wrote data or restarted model.");
    await backend.SearchAsync(new SearchRequest("synthetic", Mode: SearchMode.Keyword));
    await backend.SetIndexingPausedAsync(false);
    await backend.IndexAsync(new IndexRequest([fixture.Documents]));
    Check((await backend.GetStatusAsync()).Done == 1 && profile == await repository.GetMetaAsync("profile_id"), "Resume mixed profile or failed to finish pending row.");
    Check(settingsBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture.Data, "settings.json"))), "Transient controller changed saved BG authorization.");
    var runningSearch = await backend.SearchAsync(new SearchRequest("synthetic", Mode: SearchMode.Semantic));
    await backend.SetIndexingPausedAsync(true);
    var semantic = await backend.SearchAsync(new SearchRequest("synthetic", Mode: SearchMode.Semantic));
    Check(runningSearch.SemanticAvailable && semantic.SemanticAvailable && semantic.Notice == runningSearch.Notice && semantic.Hits.SequenceEqual(runningSearch.Hits),
        "Pause changed semantic search availability/results.");
});

await Test("BACKEND-AUTHORIZED-BG-RESUME-RECONCILES-AFTER-PAUSED-SAVE", async () =>
{
    var fixture = await Fixture("background", "cancel", true);
    await using var backend = fixture.Backend;
    await WaitFor(() => File.Exists(fixture.Audit) && File.ReadAllText(fixture.Audit).Contains("\"op\":\"text\""));
    await backend.SetIndexingPausedAsync(true);
    var before = await backend.LoadSettingsAsync();
    before.Theme = "Light";
    await backend.SaveSettingsAsync(before);
    var audit = File.ReadAllText(fixture.Audit);
    await File.WriteAllTextAsync(Path.Combine(fixture.Documents, "second.txt"), "synthetic second document");
    await Task.Delay(800);
    Check(audit == File.ReadAllText(fixture.Audit), "Paused save/watcher started model.");
    Check((await backend.GetStatusAsync()).IndexingState == IndexingControlState.Paused, "Paused save lost state.");
    var saved = await File.ReadAllBytesAsync(Path.Combine(fixture.Data, "settings.json"));
    await backend.SetIndexingPausedAsync(false);
    await WaitForAsync(async () => (await backend.GetStatusAsync()).Done == 2);
    Check((await backend.LoadSettingsAsync()).BackgroundIndexingEnabled && saved.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture.Data, "settings.json"))), "Resume changed BG authorization/revision.");
    var status = await backend.GetStatusAsync();
    Check(!status.Detail!.Contains("Background indexing failed"), "Intentional pause poisoned coordinator.");
    await Task.Delay(700);
    var settledAudit = File.ReadAllText(fixture.Audit);
    await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => backend.SetIndexingPausedAsync(false)));
    await Task.Delay(700);
    Check(settledAudit == File.ReadAllText(fixture.Audit), "Duplicate resume scheduled duplicate reconciliation.");
});

await Test("BACKEND-SHUTDOWN-CANCELS-ACTIVE-QUEUED-AND-EXITS-FAKE-HOST", async () =>
{
    var fixture = await Fixture("shutdown", "cancel", false);
    var backend = fixture.Backend;
    var active = backend.IndexAsync(new IndexRequest([fixture.Documents]));
    await WaitFor(() => File.Exists(fixture.Audit) && File.ReadAllText(fixture.Audit).Contains("\"op\":\"text\""));
    var queued = backend.IndexAsync(new IndexRequest([fixture.Documents]));
    var pause = backend.SetIndexingPausedAsync(true);
    await backend.DisposeAsync();
    await Throws<OperationCanceledException>(() => active);
    await Throws<OperationCanceledException>(() => queued);
    await pause;
    await Throws<ObjectDisposedException>(() => backend.SetIndexingPausedAsync(false));
    await backend.DisposeAsync();
    foreach (var line in File.ReadAllLines(fixture.Audit))
    {
        using var json = JsonDocument.Parse(line);
        if (!json.RootElement.TryGetProperty("started", out var pid)) continue;
        try { using var process = Process.GetProcessById(pid.GetInt32()); Check(process.HasExited, "Fake host leaked after shutdown."); }
        catch (ArgumentException) { }
    }
    foreach (var file in Directory.GetFiles(Path.Combine(fixture.Data, "index"), "*.sqlite"))
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
});

await Test("CONTROLLER-SHUTDOWN-UNCOOPERATIVE-BOUNDED-AND-CLOSED", async () =>
{
    var control = new IndexingController(TimeSpan.FromMilliseconds(80)); var release = Signal();
    var work = control.RunAsync(_ => release.Task, default);
    try
    {
        await Throws<TimeoutException>(() => control.StopAsync());
        Check(control.State == IndexingControlState.Pausing && control.HasWork, "Shutdown faked quiescence.");
        await Throws<ObjectDisposedException>(() => control.SetPausedAsync(false, default));
        await Throws<ObjectDisposedException>(() => control.RunAsync(_ => Task.CompletedTask, default));
    }
    finally { release.TrySetResult(); }
    await Throws<OperationCanceledException>(() => work);
    await control.StopAsync();
});

await Test("BACKEND-SHUTDOWN-RELEASES-ALL-RESOURCES-AFTER-CLEANUP-FAILURES", async () =>
{
    var fixture = await Fixture("cleanup", "success", false);
    var backend = fixture.Backend;
    var first = new CleanupRepository(true); var second = new CleanupRepository(false); var model = new CleanupModel();
    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    var repositories = (List<IIndexRepository>)typeof(DesktopBackend).GetField("repositories", flags)!.GetValue(backend)!;
    repositories.Add(first); repositories.Add(second);
    typeof(DesktopBackend).GetField("embeddings", flags)!.SetValue(backend, model);
    var watcher = new BackgroundIndexCoordinator(new AppSettings { Folders = [fixture.Documents], ProcessorOnly = false },
        (_, _) => Task.FromException(new IOException("synthetic index fault")),
        _ => throw new InvalidOperationException("synthetic error-sink fault"), debounce: TimeSpan.FromMilliseconds(1));
    await Throws<InvalidOperationException>(() => watcher.Completion);
    typeof(DesktopBackend).GetField("background", flags)!.SetValue(backend, watcher);
    await Throws<AggregateException>(() => backend.DisposeAsync().AsTask());
    Check(first.Disposed && second.Disposed && model.Disposed && repositories.Count == 0,
        "One cleanup error skipped later repositories/model.");
    await backend.DisposeAsync();
    await Throws<ObjectDisposedException>(() => backend.SearchAsync(new SearchRequest("synthetic")));
});

var report = JsonSerializer.Serialize(new { suite = "p4-pause", fakeHost, root, passed = results.Count - failed, failed, assertions = results }, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(report);
await File.WriteAllTextAsync(Path.Combine(root, "results.json"), report);
return failed == 0 ? 0 : 1;

async Task Test(string id, Func<Task> action)
{
    var watch = Stopwatch.StartNew();
    try { await action().WaitAsync(TimeSpan.FromSeconds(25)); results.Add(new { id, passed = true, elapsedMs = watch.ElapsedMilliseconds }); }
    catch (Exception error)
    {
        failed++; results.Add(new { id, passed = false, elapsedMs = watch.ElapsedMilliseconds, error = error.ToString() });
    }
}
async Task<(DesktopBackend Backend, string Data, string Documents, string Audit)> Fixture(string name, string mode, bool background)
{
    var directory = Path.Combine(root, name); var data = Path.Combine(directory, "data"); var documents = Path.Combine(directory, "documents");
    var models = Path.Combine(directory, mode + " fake models");
    Directory.CreateDirectory(documents); Directory.CreateDirectory(models);
    await File.WriteAllTextAsync(Path.Combine(documents, "synthetic.txt"), "synthetic document marker");
    var manifest = Path.Combine(models, "manifest with spaces &.json"); await File.WriteAllTextAsync(manifest, "{}");
    var store = new JsonSettingsStore(Path.Combine(data, "settings.json"));
    await store.SaveAsync(new AppSettings { Folders = [documents], ProcessorOnly = false, SetUp = true, BackgroundIndexingEnabled = background,
        ModelDirectory = models, ManifestPath = manifest, InferenceExecutable = fakeHost });
    return (await DesktopBackend.CreateAsync(new DesktopOptions(data)), data, documents, Path.Combine(models, "audit.jsonl"));
}
static string Snapshot(string directory) => string.Join("|", Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
    .Where(file => !file.EndsWith("-shm") && !file.EndsWith(".lock"))
    .Order(StringComparer.Ordinal).Select(file =>
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return file + ":" + Convert.ToHexString(SHA256.HashData(stream));
    }));
static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
static Task WaitFor(Func<bool> condition) => WaitForAsync(() => Task.FromResult(condition()));
static async Task WaitForAsync(Func<Task<bool>> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!await condition()) await Task.Delay(15, timeout.Token);
}

// Disposal-only fixtures: reflection injection remains in tests; production has no fake dependency options.
sealed class CleanupRepository(bool fail) : IIndexRepository
{
    public bool Disposed { get; private set; }
    public string DatabasePath => "synthetic";
    public ValueTask DisposeAsync() { Disposed = true; return fail ? ValueTask.FromException(new IOException("synthetic repository cleanup fault")) : ValueTask.CompletedTask; }
    public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<IndexedFile>> ReadFilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IndexedFile?> GetFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpsertFileAsync(IndexedFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<KeywordMatch>> KeywordSearchAsync(string query, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
sealed class CleanupModel : IEmbeddingService
{
    public bool Disposed { get; private set; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.FromException(new IOException("synthetic model cleanup fault")); }
    public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
