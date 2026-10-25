using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinSight.Core;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;
using WinSight.Storage.Tests;

var root = Path.Combine(AppContext.BaseDirectory, "synthetic", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0, failed = 0;
async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
}
string Db(string name) => Path.Combine(root, name + ".sqlite");

await Test("NM-BR-09 first schema cancellation leaves target absent and same instance can recover", async () =>
{
    string path = Db("first-cancel");
    using var cancellation = new CancellationTokenSource();
    bool first = true;
    await using var repo = new SqliteIndexRepository(path, stage =>
    {
        if (stage == IndexInitializationStage.SchemaCreated && first) { first = false; cancellation.Cancel(); }
    });
    await Throws<OperationCanceledException>(() => repo.InitializeAsync(cancellation.Token));
    Check(!System.IO.File.Exists(path), "Canceled first initialization stranded the target database.");
    CheckNoStaging(path);
    await repo.InitializeAsync();
    await repo.UpsertFileAsync(SampleFile("C:/recovered/one.txt", "recoveredneedle"));
    Check((await repo.KeywordSearchAsync("recoveredneedle", 10)).Count == 1);
});

await Test("NM-BR-09 schema failure rolls back private database and retry publishes a usable index", async () =>
{
    string path = Db("first-failure");
    bool first = true;
    await using var repo = new SqliteIndexRepository(path, stage =>
    {
        if (stage == IndexInitializationStage.SchemaCreated && first) { first = false; throw new IOException("synthetic schema creation failure"); }
    });
    await Throws<IOException>(() => repo.InitializeAsync());
    Check(!System.IO.File.Exists(path));
    CheckNoStaging(path);
    await repo.InitializeAsync();
    Check(await repo.GetMetaAsync("native_format") == SqliteIndexRepository.NativeFormat);
    await repo.UpsertFileAsync(SampleFile("C:/failure-recovery/one.txt", "recoveredfailure"));
    Check((await repo.GetStatusAsync()).Done == 1);
});

await Test("NM-BR-09 cancellation before publication has a closed checkpointed private DB, not a target", async () =>
{
    string path = Db("publish-cancel");
    using var cancellation = new CancellationTokenSource();
    bool first = true;
    await using var repo = new SqliteIndexRepository(path, stage =>
    {
        if (stage != IndexInitializationStage.BeforePublish || !first) return;
        first = false;
        Check(!System.IO.File.Exists(path));
        string staged = Directory.GetFiles(root, "." + Path.GetFileName(path) + ".*.initializing").Single();
        Check(!System.IO.File.Exists(staged + "-wal") && !System.IO.File.Exists(staged + "-shm"));
        using (var closedProof = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None)) Check(closedProof.Length > 0);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = staged, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            db.Open();
            Check(Convert.ToInt64(Scalar(db, "PRAGMA user_version")) == 5);
            Check((string?)Scalar(db, "SELECT value FROM meta WHERE key='native_format'") == SqliteIndexRepository.NativeFormat);
        }
        cancellation.Cancel();
    });
    await Throws<OperationCanceledException>(() => repo.InitializeAsync(cancellation.Token));
    Check(!System.IO.File.Exists(path));
    CheckNoStaging(path);
    await repo.InitializeAsync();
    Check((await repo.GetStatusAsync()).Files == 0);
});

await Test("NM-BR-09 concurrent initializers race no-overwrite publication and both use the winner", async () =>
{
    string path = Db("publish-race");
    using var ready = new Barrier(2);
    int publishers = 0;
    void BeforePublish(IndexInitializationStage stage)
    {
        if (stage != IndexInitializationStage.BeforePublish) return;
        Interlocked.Increment(ref publishers);
        Check(!System.IO.File.Exists(path), "Another initializer exposed the target before both private DBs were ready.");
        Check(ready.SignalAndWait(TimeSpan.FromSeconds(10)), "Concurrent initializer did not reach publication.");
    }
    await using var first = new SqliteIndexRepository(path, BeforePublish);
    await using var second = new SqliteIndexRepository(path, BeforePublish);
    await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
    Check(publishers == 2 && await first.GetMetaAsync("native_format") == SqliteIndexRepository.NativeFormat);
    CheckNoStaging(path);
    await Task.WhenAll(first.UpsertFileAsync(SampleFile("C:/race/one.txt", "raceneedle")), second.UpsertFileAsync(SampleFile("C:/race/two.txt", "raceneedle")));
    Check((await first.GetStatusAsync()).Done == 2 && (await second.KeywordSearchAsync("raceneedle", 10)).Count == 2);
    CheckNoStaging(path);
});

await Test("NM-BR-09 preexisting or publication-raced empty/unknown targets stay byte-identical", async () =>
{
    foreach (string kind in new[] { "preexisting-empty", "raced-empty", "raced-unknown" })
    {
        string path = Db(kind);
        byte[]? before = null;
        void CreateTarget()
        {
            if (kind == "raced-unknown") { using var db = Open(path); Sql(db, "CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('preserved')"); }
            else System.IO.File.WriteAllBytes(path, []);
            before = SHA256.HashData(System.IO.File.ReadAllBytes(path));
        }
        if (kind == "preexisting-empty") CreateTarget();
        await using var repo = new SqliteIndexRepository(path, stage => { if (stage == IndexInitializationStage.BeforePublish) CreateTarget(); });
        await Throws<InvalidDataException>(() => repo.InitializeAsync());
        Check(before != null && before.SequenceEqual(SHA256.HashData(System.IO.File.ReadAllBytes(path))), "Initializer replaced or altered an unknown target.");
        CheckNoStaging(path);
        await using var retry = new SqliteIndexRepository(path);
        await Throws<InvalidDataException>(() => retry.InitializeAsync());
        Check(before!.SequenceEqual(SHA256.HashData(System.IO.File.ReadAllBytes(path))));
    }
});

await Test("NM-BR-10 keyword and hybrid requests above 100 retain requested scoped candidates", async () =>
{
    await using var repo = new SqliteIndexRepository(Db("candidate-limit"));
    for (int i = 0; i < 220; i++) await repo.UpsertFileAsync(SampleFile($"C:/batch/{i:D3}.txt", "batchneedle"));
    await repo.UpsertFileAsync(SampleFile("C:/batch-sibling/else.txt", "batchneedle"));
    var model = new FakeEmbedding { ThrowIfCalled = true };
    var service = new SearchService(repo, model);
    var request = new SearchRequest("batchneedle", 200, SearchMode.Keyword, Root: "C:/batch");
    var result = await service.SearchAsync(request);
    Check(result.Hits.Count == 200, $"Requested 200 real matches but received {result.Hits.Count}.");
    Check(result.Hits.Select(h => h.FileId).Distinct().Count() == 200 && result.Hits.All(h => h.Path.StartsWith("C:/batch/", StringComparison.Ordinal)));
    Check((await service.SearchAsync(request with { Limit = 220 })).Hits.Count == 220);
    Check((await service.SearchAsync(request with { Limit = 50 })).Hits.Count == 50);
    Check(model.Calls == 0);
    var hybrid = await new SearchService(repo).SearchAsync(request with { Mode = SearchMode.Hybrid });
    Check(hybrid.Hits.Count == 200 && hybrid.Notice != null && !hybrid.SemanticAvailable);
});

await Test("new scratch schema5, real FTS tokenizer, Half bytes and immutable profile", async () =>
{
    string path = Db("new");
    await using var repo = new SqliteIndexRepository(path);
    await repo.InitializeAsync();
    Check(await repo.GetMetaAsync("native_format") == SqliteIndexRepository.NativeFormat);
    await Throws<InvalidDataException>(() => repo.UpsertFileAsync(SampleFile("C:/docs/early.txt", "early", Unit(0))));
    await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
    await repo.SetMetaAsync("embedding_info", JsonSerializer.Serialize(FakeEmbedding.Info("synthetic-profile-v1")));
    await repo.SetMetaAsync("embedding_info", JsonSerializer.Serialize(FakeEmbedding.Info("synthetic-profile-v1"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    await repo.UpsertFileAsync(SampleFile("C:/docs/one.txt", "retrieval running বাংলাদেশ", Unit(0)));
    var file = (await repo.ReadFilesAsync()).Single();
    Check(file.Segments.Count == 2 && file.Segments[0].Kind == "name");
    Check(file.Segments[1].Vector!.Length == 768 && file.Segments[1].Vector![0] == 1);
    await repo.UpsertFileAsync(file);
    var roundtrip = (await repo.GetFileAsync(file.Path))!;
    Check(roundtrip.Id == file.Id && roundtrip.Segments.Count == 2);
    using var db = Open(path);
    Check(Convert.ToInt64(Scalar(db, "PRAGMA user_version")) == 5);
    var bytes = (byte[])Scalar(db, "SELECT vec FROM segments WHERE vec IS NOT NULL")!;
    Check(bytes.Length == 1536 && BinaryPrimitives.ReadUInt16LittleEndian(bytes) == 0x3C00);
    Check(Convert.ToInt64(Scalar(db, "SELECT count(*) FROM fts WHERE fts MATCH 'run'")) == 1);
    Check((await repo.KeywordSearchAsync("বাংলাদেশ", 10)).Count == 1);
    await Throws<InvalidOperationException>(() => repo.SetMetaAsync("profile_id", "other"));
    await Throws<InvalidDataException>(() => repo.SetMetaAsync("embedding_info", JsonSerializer.Serialize(FakeEmbedding.Info("other"))));
    await Throws<InvalidOperationException>(() => repo.SetMetaAsync("native_format", SqliteIndexRepository.NativeFormat));
});

await Test("strict vector dimensions, NaN, infinity, zero, norm; no partial writes", async () =>
{
    await using var repo = new SqliteIndexRepository(Db("vectors"));
    await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
    var nan = Unit(0); nan[2] = float.NaN;
    var infinity = Unit(0); infinity[2] = float.PositiveInfinity;
    var badNorm = Unit(0); badNorm[0] = 2;
    foreach (var vector in new[] { new float[767], new float[768], nan, infinity, badNorm })
        await Throws<InvalidDataException>(() => repo.UpsertFileAsync(SampleFile("C:/invalid.txt", "not committed", vector)));
    Check((await repo.GetStatusAsync()).Files == 0);
});

await Test("independent Swift schema5 fixture reads and refuses all legacy writes", async () =>
{
    string path = Db("legacy");
    CreateLegacy(path);
    var before = SHA256.HashData(System.IO.File.ReadAllBytes(path));
    await using (var repo = new SqliteIndexRepository(path))
    {
        Check((await repo.ReadFilesAsync()).Single().Path == "/legacy/alpha.txt");
        Check((await repo.KeywordSearchAsync("alpha", 10)).Single().FileId == 41);
        await Throws<InvalidOperationException>(() => repo.UpsertFileAsync(SampleFile("/legacy/beta.txt", "beta")));
        await Throws<InvalidOperationException>(() => repo.DeleteFileAsync("/legacy/alpha.txt"));
        await Throws<InvalidOperationException>(() => repo.SetMetaAsync("profile_id", "synthetic-profile-v1"));
        Check((await repo.GetStatusAsync()).Detail!.Contains("read-only", StringComparison.Ordinal));
        var hybrid = await new SearchService(repo, new FakeEmbedding()).SearchAsync(new SearchRequest("alpha"));
        Check(!hybrid.SemanticAvailable && hybrid.Notice!.Contains("profile_id", StringComparison.Ordinal) && hybrid.Hits.Count == 1);
    }
    Check(before.SequenceEqual(SHA256.HashData(System.IO.File.ReadAllBytes(path))));
});

await Test("unknown, empty, older/newer, altered FTS, unknown objects never cleared or marked", async () =>
{
    foreach (string type in new[] { "empty", "unknown", "older", "newer", "tokenizer", "extra", "marker", "foreignkey", "unique", "type" })
    {
        string path = Db(type);
        if (type == "empty") System.IO.File.WriteAllBytes(path, []);
        else if (type == "unknown") { using var db = Open(path); Sql(db, "CREATE TABLE personal(value TEXT); INSERT INTO personal VALUES('untouched')"); }
        else
        {
            CreateLegacy(path);
            using var db = Open(path);
            Sql(db, type switch
            {
                "older" => "PRAGMA user_version=4", "newer" => "PRAGMA user_version=6",
                "tokenizer" => "DROP TABLE fts; CREATE VIRTUAL TABLE fts USING fts5(name,body,original UNINDEXED)",
                "marker" => "INSERT INTO meta VALUES('native_format','unknown-format')",
                "foreignkey" => "DROP TABLE segments; CREATE TABLE segments(id INTEGER PRIMARY KEY,file INTEGER NOT NULL,kind TEXT NOT NULL,modality TEXT NOT NULL,loc REAL,loc_end REAL,excerpt TEXT,vec BLOB)",
                "unique" => "DROP TABLE files; CREATE TABLE files(id INTEGER PRIMARY KEY,path TEXT NOT NULL,kind TEXT NOT NULL,size INTEGER NOT NULL,mtime REAL NOT NULL,inode INTEGER NOT NULL,device INTEGER NOT NULL,state TEXT NOT NULL DEFAULT 'pending',detail TEXT,info TEXT,indexed_at REAL)",
                "type" => "DROP TABLE segments; CREATE TABLE segments(id INTEGER PRIMARY KEY,file INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,kind TEXT NOT NULL,modality TEXT NOT NULL,loc REAL,loc_end REAL,excerpt TEXT,vec TEXT)",
                _ => "CREATE TABLE extra(value TEXT)"
            });
        }
        var before = SHA256.HashData(System.IO.File.ReadAllBytes(path));
        await using (var repo = new SqliteIndexRepository(path)) await Throws<InvalidDataException>(() => repo.InitializeAsync());
        Check(before.SequenceEqual(SHA256.HashData(System.IO.File.ReadAllBytes(path))), "Rejected database changed: " + type);
    }
});

await Test("late FTS/segment SQL failure rolls back file metadata, segments and FTS together", async () =>
{
    string path = Db("rollback");
    await using var repo = new SqliteIndexRepository(path);
    await repo.UpsertFileAsync(SampleFile("C:/docs/old.txt", "originalneedle"));
    long id = (await repo.GetFileAsync("C:/docs/old.txt"))!.Id;
    using var db = Open(path);
    Sql(db, "CREATE TRIGGER fail_page BEFORE INSERT ON segments WHEN NEW.kind='page' BEGIN SELECT RAISE(ABORT,'synthetic late failure'); END;");
    var replacement = SampleFile("C:/docs/old.txt", "replacementneedle") with { Size = 999, Segments = [new("chunk", "none", 1, null, "replacementneedle"), new("page", "none", 2, null, "failure")] };
    await Throws<SqliteException>(() => repo.UpsertFileAsync(replacement));
    var old = (await repo.GetFileAsync("C:/docs/old.txt"))!;
    Check(old.Id == id && old.Size == 10 && old.Segments.Count == 2 && old.Segments[1].Excerpt == "originalneedle");
    Check((await repo.KeywordSearchAsync("originalneedle", 10)).Count == 1);
    Check((await repo.KeywordSearchAsync("replacementneedle", 10)).Count == 0);
    Sql(db, "DROP TRIGGER fail_page");
    await repo.UpsertFileAsync(SampleFile("C:/docs/old.txt", "newneedle"));
    Check((await repo.GetFileAsync("C:/docs/old.txt"))!.Id == id);
    Check((await repo.KeywordSearchAsync("originalneedle", 10)).Count == 0);
    await repo.DeleteFileAsync("C:/docs/old.txt");
    Check((await repo.ReadFilesAsync()).Count == 0 && Convert.ToInt64(Scalar(db, "SELECT count(*) FROM fts")) == 0 && Convert.ToInt64(Scalar(db, "SELECT count(*) FROM segments")) == 0);
});

await Test("parameterized path/kind/query, exact sibling-root boundary, literal wildcard roots", async () =>
{
    await using var repo = new SqliteIndexRepository(Db("scope"));
    foreach (string path in new[] { "C:/scope%_'/inside.txt", "C:/scope%_'sibling/outside.txt", "C:/elsewhere/outside.txt" })
        await repo.UpsertFileAsync(SampleFile(path, "commonneedle"));
    var model = new FakeEmbedding { ThrowIfCalled = true };
    var service = new SearchService(repo, model);
    var found = await service.SearchAsync(new SearchRequest("commonneedle", 10, SearchMode.Keyword, Root: "c:\\scope%_'\\"));
    Check(found.Hits.Count == 1 && found.Hits[0].Path.EndsWith("inside.txt", StringComparison.Ordinal));
    Check((await service.SearchAsync(new SearchRequest("commonneedle", 10, SearchMode.Keyword, Root: "C:/scope' OR 1=1--"))).Hits.Count == 0);
    Check((await repo.GetFileAsync("' OR 1=1--")) == null);
    await repo.DeleteFileAsync("' OR 1=1--");
    Check((await repo.GetStatusAsync()).Files == 3);
    await repo.UpsertFileAsync(SampleFile("C:/Äccent/inside.txt", "unicodeneedle"));
    Check((await service.SearchAsync(new SearchRequest("unicodeneedle", Mode: SearchMode.Keyword, Root: "c:/äccent"))).Hits.Count == 1);
    await Throws<ArgumentException>(() => service.SearchAsync(new SearchRequest("commonneedle", Mode: SearchMode.Keyword, Kind: "doc' OR 1=1--")));
    await service.SearchAsync(new SearchRequest("\" ) OR * : - ( commonneedle", Mode: SearchMode.Keyword));
    Check(model.Calls == 0);
});

await Test("normalization, Porter stemming, prefix rescue, coverage gate and lookup weighting", async () =>
{
    await using var repo = new SqliteIndexRepository(Db("ranking"));
    await repo.UpsertFileAsync(SampleFile("C:/rank/full.txt", "invoice ZX99123 running Grok4 أَلْفَا ＡＢＣ"));
    await repo.UpsertFileAsync(SampleFile("C:/rank/part.txt", "invoice"));
    var service = new SearchService(repo, new FakeEmbedding { ThrowIfCalled = true });
    var lookup = (await service.SearchAsync(new SearchRequest("ZX99123", Mode: SearchMode.Keyword))).Hits.Single();
    Check(lookup.Score == 1 && lookup.Channels!.SequenceEqual(["keyword"]) && lookup.Excerpt!.Contains("ZX99123", StringComparison.Ordinal));
    Check((await service.SearchAsync(new SearchRequest("run", Mode: SearchMode.Keyword))).Hits.Single().Path.EndsWith("full.txt", StringComparison.Ordinal));
    Check((await service.SearchAsync(new SearchRequest("grok", Mode: SearchMode.Keyword))).Hits.Count == 1);
    Check((await service.SearchAsync(new SearchRequest("الفا", Mode: SearchMode.Keyword))).Hits.Count == 1);
    Check((await service.SearchAsync(new SearchRequest("abc", Mode: SearchMode.Keyword))).Hits.Count == 1);
    var coverage = await service.SearchAsync(new SearchRequest("invoice ZX99123 missingword", Mode: SearchMode.Keyword));
    Check(coverage.Hits.Count == 1 && coverage.Hits[0].Path.EndsWith("full.txt", StringComparison.Ordinal));
});

await Test("semantic ranking uses global z gate, document prompts, text operation, and strict profiles", async () =>
{
    await using var repo = new SqliteIndexRepository(Db("semantic"));
    await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
    for (int i = 0; i < 30; i++) await repo.UpsertFileAsync(SampleFile($"C:/semantic/{i}.txt", "unrelated", Unit(i == 0 ? 0 : 1)));
    var model = new FakeEmbedding();
    var service = new SearchService(repo, model);
    var result = await service.SearchAsync(new SearchRequest("desired meaning", Mode: SearchMode.Semantic));
    Check(result.SemanticAvailable && result.Hits.Count == 1 && result.Hits[0].Path.EndsWith("/0.txt", StringComparison.Ordinal) && result.Hits[0].MatchType == "semantic");
    Check(model.LastInput == new EmbeddingInput("text", "task: search result | query: desired meaning"));
    var target = (await repo.GetFileAsync("C:/semantic/0.txt"))!;
    await repo.UpsertFileAsync(target with { Segments = target.Segments.Concat(Enumerable.Range(41, 60).Select(i => new IndexSegment("chunk", "text", i, null, "later pages excluded from scale", Unit(1)))).ToArray() });
    var afterLater = await service.SearchAsync(new SearchRequest("desired meaning", Mode: SearchMode.Semantic));
    Check(Math.Abs(afterLater.Hits.Single().Score - result.Hits[0].Score) < 1e-10);
    model.Profile = "wrong-profile";
    await Throws<InvalidDataException>(() => service.SearchAsync(new SearchRequest("unrelated", Mode: SearchMode.Semantic)));
    var hybrid = await service.SearchAsync(new SearchRequest("unrelated"));
    Check(!hybrid.SemanticAvailable && hybrid.Notice!.Contains("profile", StringComparison.Ordinal) && hybrid.Hits.Count == 30);
});

await Test("modality means calibrate image/text distributions instead of ranking raw cosine", async () =>
{
    await using var repo = new SqliteIndexRepository(Db("modalities"));
    await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
    foreach (var modality in new[] { "text", "image" })
    for (int i = 0; i < 20; i++)
    {
        float cosine = modality == "text" ? i == 0 ? 0.5f : 0.1f : i == 0 ? 0.95f : 0.7f;
        var vector = new float[768]; vector[0] = cosine; vector[1] = MathF.Sqrt(1 - cosine * cosine);
        await repo.UpsertFileAsync(SampleFile($"C:/modalities/{modality}-{i}.txt", "background") with
        { Segments = [new("chunk", modality, 1, null, "background", vector)] });
    }
    var result = await new SearchService(repo, new FakeEmbedding()).SearchAsync(new SearchRequest("desired meaning", Mode: SearchMode.Semantic));
    Check(result.Hits.Count == 2 && result.Hits[0].Path.EndsWith("text-0.txt", StringComparison.Ordinal) && result.Hits[1].Path.EndsWith("image-0.txt", StringComparison.Ordinal));
});

await Test("semantic faults visible, hybrid degradation explicit, keyword never invokes model", async () =>
{
    await using var repo = new SqliteIndexRepository(Db("faults"));
    await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
    await repo.UpsertFileAsync(SampleFile("C:/fault/alpha.txt", "alpha", Unit(0)));
    var model = new FakeEmbedding { Error = "synthetic dependency failure" };
    var service = new SearchService(repo, model);
    await Throws<InvalidDataException>(() => service.SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Semantic)));
    var hybrid = await service.SearchAsync(new SearchRequest("alpha"));
    Check(hybrid.Hits.Count == 1 && !hybrid.SemanticAvailable && hybrid.Notice!.Contains("synthetic dependency failure", StringComparison.Ordinal));
    model.ThrowIfCalled = true; int calls = model.Calls;
    Check((await service.SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Keyword))).Hits.Count == 1 && model.Calls == calls);
    await Throws<InvalidOperationException>(() => new SearchService(repo).SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Semantic)));
    model.ThrowIfCalled = false; model.Error = null; model.Vector = new float[767];
    await Throws<InvalidDataException>(() => service.SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Semantic)));
    model.Vector = Unit(0); model.ChangeProfileAfterEmbedding = true;
    await Throws<InvalidDataException>(() => service.SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Semantic)));
});

await Test("cancellation before write/query and during embedding does not become empty success", async () =>
{
    await using var repo = new SqliteIndexRepository(Db("cancel"));
    await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
    await repo.UpsertFileAsync(SampleFile("C:/cancel/alpha.txt", "alpha", Unit(0)));
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Throws<OperationCanceledException>(() => repo.UpsertFileAsync(SampleFile("C:/cancel/beta.txt", "beta"), canceled.Token));
    await Throws<OperationCanceledException>(() => repo.DeleteFileAsync("C:/cancel/alpha.txt", canceled.Token));
    await Throws<OperationCanceledException>(() => repo.KeywordSearchAsync("alpha", 10, canceled.Token));
    await Throws<OperationCanceledException>(() => new SearchService(repo).SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Keyword), canceled.Token));
    Check((await repo.GetStatusAsync()).Files == 1);
    using var during = new CancellationTokenSource();
    var model = new FakeEmbedding { OnEmbed = () => during.Cancel() };
    await Throws<OperationCanceledException>(() => new SearchService(repo, model).SearchAsync(new SearchRequest("alpha"), during.Token));
});

await Test("canceled queued writer rolls back, parallel instances serialize complete transactions", async () =>
{
    string path = Db("parallel");
    await using var one = new SqliteIndexRepository(path);
    await one.InitializeAsync();
    await using var two = new SqliteIndexRepository(path);
    await two.InitializeAsync();
    using (var blocker = Open(path))
    using (var lockTx = blocker.BeginTransaction(deferred: false))
    {
        using var canceled = new CancellationTokenSource();
        var pending = one.UpsertFileAsync(SampleFile("C:/parallel/canceled.txt", "nevercommit"), canceled.Token);
        await Task.Delay(100); canceled.Cancel(); lockTx.Rollback();
        await Throws<OperationCanceledException>(() => pending);
    }
    Check((await one.GetStatusAsync()).Files == 0);
    await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? one : two).UpsertFileAsync(SampleFile($"C:/parallel/{i}.txt", "serializedneedle"))));
    Check((await one.GetStatusAsync()).Done == 20 && (await two.KeywordSearchAsync("serializedneedle", 100)).Count == 20);
});

await Test("corrupt vector blobs fail visibly; keyword-only remains independent of vectors", async () =>
{
    string path = Db("corrupt");
    await using var repo = new SqliteIndexRepository(path);
    await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
    await repo.UpsertFileAsync(SampleFile("C:/corrupt/alpha.txt", "alpha", Unit(0)));
    using var db = Open(path);
    Sql(db, "UPDATE segments SET vec=X'0000' WHERE vec IS NOT NULL");
    await Throws<InvalidDataException>(() => repo.ReadFilesAsync());
    await Throws<InvalidDataException>(() => new SearchService(repo, new FakeEmbedding()).SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Semantic)));
    Check((await new SearchService(repo, new FakeEmbedding { ThrowIfCalled = true }).SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Keyword))).Hits.Count == 1);
});

await Test("query validation, no keyword error swallowing, dispose ownership, code.sqlite isolation", async () =>
{
    var repo = new SqliteIndexRepository(Db("query"));
    await repo.UpsertFileAsync(SampleFile("C:/query/alpha.txt", "alpha"));
    var service = new SearchService(repo);
    Check((await service.SearchAsync(new SearchRequest("   ", Mode: SearchMode.Keyword))).Hits.Count == 0);
    await Throws<ArgumentException>(() => service.SearchAsync(new SearchRequest("bad\0query")));
    await Throws<ArgumentException>(() => service.SearchAsync(new SearchRequest(new string('x', 4097))));
    await Throws<ArgumentOutOfRangeException>(() => service.SearchAsync(new SearchRequest("alpha", 0)));
    await Throws<InvalidOperationException>(() => service.SearchAsync(new SearchRequest("code: alpha", Mode: SearchMode.Keyword)));
    using (var db = Open(repo.DatabasePath)) Sql(db, "DROP TABLE fts");
    await Throws<SqliteException>(() => service.SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Keyword)));
    await repo.DisposeAsync(); await repo.DisposeAsync();
    await Throws<ObjectDisposedException>(() => repo.GetStatusAsync());
    await using var code = new SqliteIndexRepository(Path.Combine(root, "code.sqlite"));
    await code.SetMetaAsync("profile_id", "synthetic-profile-v1");
    await code.UpsertFileAsync(SampleFile("C:/code/one.cs", "quantize_row_q8_0_ref", Unit(0)) with { Kind = "code", Segments = [new("lines", "text", 21, 40, "quantize_row_q8_0_ref", Unit(0))] });
    var model = new FakeEmbedding();
    var hit = (await new SearchService(code, model).SearchAsync(new SearchRequest("code: quantize_row_q8_0_ref", Mode: SearchMode.Keyword))).Hits.Single();
    Check(hit.Location == 21 && hit.SegmentKind == "lines" && model.Calls == 0);
    await new SearchService(code, model).SearchAsync(new SearchRequest("code: retry", Mode: SearchMode.Semantic));
    Check(model.LastInput!.Value == "task: code retrieval | query: retry");
});

await WsRegressions.RunAsync(root, Test);
await IncrementalQueueStorageTests.RunAsync(root, Test);
await IncrementalQueueBehaviourTests.RunAsync(root, Test);
await IncrementalQueueUsnTests.RunAsync(root, Test);
Console.WriteLine($"RESULT passed={passed} failed={failed} synthetic={root}");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
static void CheckNoStaging(string path) => Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".*.initializing*").Any(), "Initializer left private staging files behind.");
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static float[] Unit(int axis) { var vector = new float[768]; vector[axis] = 1; return vector; }
static IndexedFile SampleFile(string path, string text, float[]? vector = null) => new(0, path, "doc", 10, 1, "done", null, null,
    [new("chunk", vector == null ? "none" : "text", 1, null, text, vector)]);
static SqliteConnection Open(string path)
{
    var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString());
    db.Open(); return db;
}
static void Sql(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
static object? Scalar(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
static void CreateLegacy(string path)
{
    using var db = Open(path);
    // Independent fixture transcribed from the current Swift schema5 create() contract, not the C# schema string.
    Sql(db, """
        CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE files(id INTEGER PRIMARY KEY,path TEXT NOT NULL UNIQUE,kind TEXT NOT NULL,size INTEGER NOT NULL,mtime REAL NOT NULL,
          inode INTEGER NOT NULL,device INTEGER NOT NULL,state TEXT NOT NULL DEFAULT 'pending',detail TEXT,info TEXT,indexed_at REAL);
        CREATE INDEX files_inode ON files(device,inode);
        CREATE INDEX files_state ON files(state,kind);
        CREATE TABLE segments(id INTEGER PRIMARY KEY,file INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
          kind TEXT NOT NULL,modality TEXT NOT NULL,loc REAL,loc_end REAL,excerpt TEXT,vec BLOB);
        CREATE INDEX segments_file ON segments(file);
        CREATE VIRTUAL TABLE fts USING fts5(name,body,original UNINDEXED,tokenize="porter unicode61 remove_diacritics 2 categories 'L* N* Co M*'");
        PRAGMA user_version=5;
        INSERT INTO files(id,path,kind,size,mtime,inode,device,state) VALUES(41,'/legacy/alpha.txt','doc',20,1,44,55,'done');
        INSERT INTO segments(id,file,kind,modality,excerpt) VALUES(51,41,'chunk','none','alpha legacy');
        INSERT INTO fts(rowid,name,body) VALUES(51,'','alpha legacy');
        INSERT INTO meta VALUES('vector_source','swift-legacy-source');
        """);
}

sealed class FakeEmbedding : IEmbeddingService
{
    public string Profile { get; set; } = "synthetic-profile-v1";
    public string? Error { get; set; }
    public bool ThrowIfCalled { get; set; }
    public bool ChangeProfileAfterEmbedding { get; set; }
    public float[] Vector { get; set; } = Enumerable.Range(0, 768).Select(i => i == 0 ? 1f : 0f).ToArray();
    public int Calls { get; private set; }
    public EmbeddingInput? LastInput { get; private set; }
    public Action? OnEmbed { get; set; }
    public static EmbeddingInfo Info(string profile) => new(profile, 768, "fake", "synthetic-no-download", "test", "cpu", "validated", ["text"]);
    public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        Calls++; if (ThrowIfCalled) throw new Exception("Keyword mode started the model");
        cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Info(Profile));
    }
    public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default)
    {
        Calls++; if (ThrowIfCalled) throw new Exception("Keyword mode started the model");
        cancellationToken.ThrowIfCancellationRequested(); LastInput = inputs.Single(); OnEmbed?.Invoke();
        if (ChangeProfileAfterEmbedding) Profile = "changed-in-flight";
        return Task.FromResult<IReadOnlyList<EmbeddingResult>>([new(Vector, Error)]);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
