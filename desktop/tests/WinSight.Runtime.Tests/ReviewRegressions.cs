using System.Data.Common;
using System.Reflection;
using WinSight.Core;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Runtime;
using WinSight.Infrastructure.Storage;

internal static class ReviewRegressions
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, string root)
    {
        await test("NM-BR-01-HARD-EXCLUSIONS", async () =>
        {
            var parent = Path.Combine(root, "hard-exclusions");
            var data = Path.Combine(parent, "native-data");
            var index = Path.Combine(parent, "native-index");
            var fixture = Path.Combine(data, "nested", "synthetic.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
            await File.WriteAllTextAsync(fixture, "hardguardmarker");
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [parent], IndexDirectory = index, SetUp = true
            });
            await SeedAsync(Path.Combine(index, "index.sqlite"), fixture, "hardguardmarker");
            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: false));
            await Throws<UnauthorizedAccessException>(() => backend.IndexAsync(new IndexRequest([data])));
            await Throws<UnauthorizedAccessException>(() => backend.IndexAsync(new IndexRequest([Path.GetDirectoryName(fixture)!])));
            await Throws<UnauthorizedAccessException>(() => backend.IndexAsync(new IndexRequest([parent, index])));
            await Throws<UnauthorizedAccessException>(() => backend.SearchAsync(new SearchRequest("hardguardmarker",
                Mode: SearchMode.Keyword, Root: data)));
            var response = await backend.SearchAsync(new SearchRequest("hardguardmarker", Mode: SearchMode.Keyword));
            Check(response.Hits.Count == 0, "Previously stored application-data content remained searchable.");
            Check(Field(backend, "embeddings") is null, "Rejected hard-excluded roots started a model client.");
        });
        await test("NM-BR-02-ACTIVE-DOMAIN-AND-NESTED-CHOICE", async () =>
        {
            var parent = Path.Combine(root, "domain-policy");
            var data = Path.Combine(parent, "appdata");
            var documents = Path.Combine(parent, "documents");
            var skipped = Path.Combine(documents, "skipped");
            var chosen = Path.Combine(skipped, "chosen");
            var privateFile = Path.Combine(skipped, "private.txt");
            var chosenFile = Path.Combine(chosen, "allowed.txt");
            Directory.CreateDirectory(chosen);
            await File.WriteAllTextAsync(privateFile, "domainprivacy");
            await File.WriteAllTextAsync(chosenFile, "nestedallowed");
            var settings = new AppSettings { Folders = [documents], Skipped = [skipped], CodeFolders = [skipped], SetUp = true };
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(settings);
            await SeedAsync(Path.Combine(data, "index", "index.sqlite"), privateFile, "domainprivacy");
            await SeedAsync(Path.Combine(data, "index", "index.sqlite"), chosenFile, "nestedallowed");
            await SeedAsync(Path.Combine(data, "index", "code.sqlite"), privateFile, "domainprivacy", code: true);
            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: false));
            Check((await backend.SearchAsync(new SearchRequest("domainprivacy", Mode: SearchMode.Keyword))).Hits.Count == 0,
                "Code authorization overrode a document exclusion.");
            Check((await backend.SearchAsync(new SearchRequest("code: domainprivacy", Mode: SearchMode.Keyword))).Hits.Any(hit => hit.Path == privateFile),
                "Document skips incorrectly denied the independently authorized code domain.");
            await Throws<UnauthorizedAccessException>(() => backend.IndexAsync(new IndexRequest([skipped])));
            settings.Folders.Add(chosen);
            await backend.SaveSettingsAsync(settings);
            var nested = await backend.SearchAsync(new SearchRequest("nestedallowed", Mode: SearchMode.Keyword, Root: chosen));
            Check(nested.Hits.Any(hit => hit.Path == chosenFile), "A configured nested root did not override its ancestor skip.");
        });
        await test("NM-BR-05-DATABASE-FAULT-KEEPS-WATCHER-ALIVE", async () =>
        {
            var watched = Path.Combine(root, "watcher-db-fault");
            Directory.CreateDirectory(watched);
            var errorObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            await using var watcher = new BackgroundIndexCoordinator(new AppSettings { Folders = [watched], ProcessorOnly = false },
                (request, token) =>
                {
                    if (Interlocked.Increment(ref calls) == 1) throw new SyntheticDbException("synthetic database lock");
                    recovered.TrySetResult();
                    return Task.CompletedTask;
                }, error =>
                {
                    Check(error is DbException, "Database fault was not reported truthfully.");
                    errorObserved.TrySetResult();
                }, debounce: TimeSpan.FromMilliseconds(1));
            await errorObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(Path.Combine(watched, "changed.txt"), "synthetic");
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(watcher.Failure is null && !watcher.Completion.IsCompleted, "Database error permanently killed the worker.");
        });
        await test("NM-BR-05-FAULTED-WATCHER-STILL-RELEASES-ALL-RESOURCES", async () =>
        {
            var watched = Path.Combine(root, "faulted-watch-cleanup");
            Directory.CreateDirectory(watched);
            var backend = await DesktopBackend.CreateAsync(new DesktopOptions(Path.Combine(root, "cleanup-data"),
                EnableBackgroundIndexing: false));
            var watcher = new BackgroundIndexCoordinator(new AppSettings { Folders = [watched], ProcessorOnly = false },
                (request, token) => throw new FormatException("synthetic unexpected worker fault"), _ => { },
                debounce: TimeSpan.FromMilliseconds(1));
            await Throws<FormatException>(() => watcher.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            var first = new TrackingRepository();
            var second = new TrackingRepository { FailOnDispose = true };
            var model = new TrackingEmbeddings();
            ((List<IIndexRepository>)Field(backend, "repositories")!).AddRange([first, second]);
            SetField(backend, "embeddings", model);
            SetField(backend, "background", watcher);
            await Throws<AggregateException>(() => backend.DisposeAsync().AsTask());
            Check(first.Disposed && second.Disposed && model.Disposed, "Watcher/store failures skipped another resource's cleanup.");
            Check(Field(backend, "background") is null && Field(backend, "embeddings") is null,
                "Faulted resources remained attached to the backend.");
            await backend.DisposeAsync();
        });
        await test("NM-BR-06-READ-HOST-CANNOT-AUTOSTART-INDEXING", async () =>
        {
            var data = Path.Combine(root, "read-host");
            var watched = Path.Combine(root, "read-host-root");
            Directory.CreateDirectory(watched);
            await File.WriteAllTextAsync(Path.Combine(watched, "synthetic.txt"), "synthetic");
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(new AppSettings
            {
                Folders = [watched], SetUp = true, BackgroundIndexingEnabled = true
            });
            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: false));
            await backend.GetStatusAsync();
            await backend.SearchAsync(new SearchRequest("synthetic", Mode: SearchMode.Keyword));
            Check(Field(backend, "background") is null && Field(backend, "embeddings") is null,
                "Read-oriented host constructed background coordination or a model client.");
            var settings = await backend.LoadSettingsAsync();
            await backend.SaveSettingsAsync(settings);
            Check(Field(backend, "background") is null, "Saving settings re-enabled background indexing in a read host.");
        });
    }

    private static async Task SeedAsync(string database, string path, string text, bool code = false)
    {
        await using var repository = new SqliteIndexRepository(database);
        await repository.InitializeAsync();
        await repository.UpsertFileAsync(new IndexedFile(0, path, code ? "code" : "doc", text.Length, 1, "done", null, null,
            [new IndexSegment(code ? "lines" : "chunk", "none", code ? 1 : null, code ? 1 : null, text)]));
    }
    private static object? Field(object target, string name) =>
        typeof(DesktopBackend).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    private static void SetField(object target, string name, object value) =>
        typeof(DesktopBackend).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }
    private static async Task Throws<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class SyntheticDbException(string message) : DbException(message);
    private sealed class TrackingEmbeddings : IEmbeddingService
    {
        public bool Disposed { get; private set; }
        public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class TrackingRepository : IIndexRepository
    {
        public string DatabasePath => "test-only-unused";
        public bool Disposed { get; private set; }
        public bool FailOnDispose { get; init; }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexedFile>> ReadFilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IndexedFile?> GetFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertFileAsync(IndexedFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KeywordMatch>> KeywordSearchAsync(string query, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (FailOnDispose) throw new IOException("synthetic store cleanup fault");
            return ValueTask.CompletedTask;
        }
    }
}
