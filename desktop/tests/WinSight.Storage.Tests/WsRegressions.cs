using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinSight.Core;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;

internal static class WsRegressions
{
    internal static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        await test("WS-01 duplicate segment ranks cannot starve another matching file", async () =>
        {
            await using var repo = new SqliteIndexRepository(Path.Combine(root, "ws01.sqlite"));
            var repeated = Enumerable.Range(1, 1100).Select(i => new IndexSegment("chunk", "none", i, null, "needle in a matching paragraph")).ToArray();
            await repo.UpsertFileAsync(File("C:/selected/alpha.txt", repeated));
            await repo.UpsertFileAsync(File("C:/selected/bravo.txt", [new("chunk", "none", 1, null, "needle in a matching paragraph")]));
            var result = await new SearchService(repo).SearchAsync(new("needle", 20, SearchMode.Keyword));
            Require(result.Hits.Count == 2, "Expected both files, not just the file owning the first 1000 segment matches.");
        });
        await test("WS-02 small calibration sets retrieve strong matches without accepting orthogonal vectors", async () =>
        {
            for (int count = 1; count <= 4; count++)
            {
                await using var repo = new SqliteIndexRepository(Path.Combine(root, $"ws02-{count}.sqlite"));
                await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
                for (int i = 0; i < count; i++)
                    await repo.UpsertFileAsync(File($"C:/small/file-{i}.txt", [new("chunk", "text", 1, null, "semantic evidence", Direction(i == 0 ? .95 : .1))]));
                var result = await new SearchService(repo, new FakeEmbedding()).SearchAsync(new("desired meaning", Mode: SearchMode.Semantic));
                Require(result.Hits.Count == 1 && result.Hits[0].Path.EndsWith("file-0.txt"), $"Small index with {count} segments lost its strong match.");
                Require(result.Notice?.Contains("small", StringComparison.OrdinalIgnoreCase) == true, "Small-sample policy must be disclosed.");
            }
            await using var unrelated = new SqliteIndexRepository(Path.Combine(root, "ws02-unrelated.sqlite"));
            await unrelated.SetMetaAsync("profile_id", "synthetic-profile-v1");
            await unrelated.UpsertFileAsync(File("C:/small/unrelated.txt", [new("chunk", "text", 1, null, "orthogonal evidence", Direction(0))]));
            Require((await new SearchService(unrelated, new FakeEmbedding()).SearchAsync(new("desired meaning", Mode: SearchMode.Semantic))).Hits.Count == 0,
                "Small-sample fallback must not turn orthogonal vectors into semantic matches.");
        });
        await test("WS-03 CJK substrings work in keyword/hybrid, including mixed queries and filenames", async () =>
        {
            await using var repo = new SqliteIndexRepository(Path.Combine(root, "ws03.sqlite"));
            await repo.UpsertFileAsync(File("C:/selected/改革纪要.txt", [new("chunk", "none", 1, null, "医院医保支付改革 evidence")]));
            await repo.UpsertFileAsync(File("C:/selected/医保规则.txt", [new("chunk", "none", 1, null, "ordinary evidence")]));
            var service = new SearchService(repo);
            foreach (var mode in new[] { SearchMode.Keyword, SearchMode.Hybrid })
            {
                var result = await service.SearchAsync(new("医保", Mode: mode));
                Require(result.Hits.Count == 2, "Chinese content substring or filename match was lost.");
            }
            var mixed = await service.SearchAsync(new("医保 evidence", Mode: SearchMode.Keyword, Root: "C:/selected"));
            Require(mixed.Hits.Any(h => h.Path.EndsWith("改革纪要.txt")), "Mixed CJK/Latin file coverage was lost.");
            Require((await service.SearchAsync(new("医保", Mode: SearchMode.Keyword, Root: "C:/selected-sibling"))).Hits.Count == 0,
                "CJK lookup escaped an exact root boundary.");
            Require((await service.SearchAsync(new("needle", Mode: SearchMode.Keyword))).Hits.Count == 0, "Unrelated Latin terms became matches.");
        });
        await test("WS-R01 full CJK/Latin matches outrank more than 100 partial-only candidates", async () =>
        {
            await using var repo = new SqliteIndexRepository(Path.Combine(root, "ws03-crowding.sqlite"));
            for (int i = 0; i < 140; i++)
                await repo.UpsertFileAsync(File($"C:/selected/partial-{i}.txt", [new("chunk", "none", 1, null, "evidence only")]));
            await repo.UpsertFileAsync(File("C:/selected/full.txt", [new("chunk", "none", 1, null, "医院医保支付改革 evidence")]));
            var result = await new SearchService(repo).SearchAsync(new("医保 evidence", 1, SearchMode.Keyword));
            Require(result.Hits.Count == 1 && result.Hits[0].Path.EndsWith("full.txt"), "Partial ordinary FTS hits crowded out the full CJK match.");
        });
        await test("WS-03 read-only schema5 CJK search does not migrate the persistent database", async () =>
        {
            var path = Path.Combine(root, "ws03-readonly.sqlite");
            await using (var repo = new SqliteIndexRepository(path))
                await repo.UpsertFileAsync(File("C:/legacy/notes.txt", [new("chunk", "none", 1, null, "医院医保支付改革")]));
            using (var db = Open(path)) Sql(db, "DELETE FROM meta WHERE key='native_format'; PRAGMA wal_checkpoint(TRUNCATE);");
            var before = SHA256.HashData(await System.IO.File.ReadAllBytesAsync(path));
            await using (var repo = new SqliteIndexRepository(path))
                Require((await new SearchService(repo).SearchAsync(new("医保", Mode: SearchMode.Keyword))).Hits.Count == 1, "Read-only index lost CJK lookup.");
            var after = SHA256.HashData(await System.IO.File.ReadAllBytesAsync(path));
            Require(before.SequenceEqual(after), "Query modified/migrated the persistent index.");
        });
        await test("WS-04 keyword reads stay candidate-bounded and warm semantic searches reuse their snapshot", async () =>
        {
            var path = Path.Combine(root, "ws04-performance.sqlite");
            await using var repo = new SqliteIndexRepository(path);
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            var text = "evidence " + new string('x', 12000);
            for (int i = 0; i < 600; i++)
                await repo.UpsertFileAsync(File($"C:/selected/{(i == 0 ? "needle" : "other")}-{i}.txt", [new("chunk", "text", 1, null, text, Direction(i == 0 ? .99 : 0))]));
            var service = new SearchService(repo, new FakeEmbedding());
            await service.SearchAsync(new("warming parser", Mode: SearchMode.Keyword));
            var bytes = GC.GetTotalAllocatedBytes(true);
            var keyword = await service.SearchAsync(new("needle", Mode: SearchMode.Keyword));
            var keywordBytes = GC.GetTotalAllocatedBytes(true) - bytes;
            bytes = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew();
            var cold = await service.SearchAsync(new("desired meaning", Mode: SearchMode.Semantic));
            var coldMs = watch.Elapsed.TotalMilliseconds;
            var coldBytes = GC.GetTotalAllocatedBytes(true) - bytes;
            var warmMs = new List<double>();
            bytes = GC.GetTotalAllocatedBytes(true);
            for (int i = 0; i < 5; i++)
            {
                watch.Restart();
                var result = await service.SearchAsync(new("desired meaning", Mode: SearchMode.Semantic));
                warmMs.Add(watch.Elapsed.TotalMilliseconds);
                Require(result.Hits.Select(h => h.Path).SequenceEqual(cold.Hits.Select(h => h.Path)), "Warm/cold semantic results drifted.");
            }
            var warmBytes = (GC.GetTotalAllocatedBytes(true) - bytes) / 5;
            var measurements = new { files = 600, textCharactersPerFile = text.Length, keywordBytes, coldBytes, warmBytes, coldMs, warmMs };
            await System.IO.File.WriteAllTextAsync(Path.Combine(root, "ws-performance.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("WS-PERF " + JsonSerializer.Serialize(measurements));
            Require(keyword.Hits.Count == 1, "Keyword candidate result drifted.");
            Require(keywordBytes < 4 * 1024 * 1024, "Keyword search materialized unrelated full-index text.");
            Require(warmBytes < coldBytes / 2, "Warm semantic query still allocates/reloads the full snapshot.");
        });
        await test("WS-04 local and external writes invalidate cached vectors; corruption stays visible", async () =>
        {
            var path = Path.Combine(root, "ws04-invalidation.sqlite");
            await using var repo = new SqliteIndexRepository(path);
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            var selected = "C:/selected/needle.txt";
            await repo.UpsertFileAsync(File(selected, [new("chunk", "text", 1, null, "first evidence", Direction(.99))]));
            for (int i = 0; i < 10; i++) await repo.UpsertFileAsync(File($"C:/selected/other-{i}.txt", [new("chunk", "text", 1, null, "unrelated", Direction(0))]));
            var service = new SearchService(repo, new FakeEmbedding());
            Require((await service.SearchAsync(new("desired meaning", Mode: SearchMode.Semantic))).Hits.Any(h => h.Path == selected), "Initial semantic fixture failed.");
            await repo.UpsertFileAsync(File(selected, [new("chunk", "text", 1, null, "local replacement", Direction(0))]));
            Require((await service.SearchAsync(new("desired meaning", Mode: SearchMode.Semantic))).Hits.All(h => h.Path != selected), "Local write served stale vectors.");
            await using (var writer = new SqliteIndexRepository(path))
                await writer.UpsertFileAsync(File(selected, [new("chunk", "text", 1, null, "external replacement", Direction(.99))]));
            var external = await service.SearchAsync(new("desired meaning", Mode: SearchMode.Semantic));
            Require(external.Hits.Any(h => h.Path == selected && h.Excerpt == "external replacement"), "External write served stale vectors or text.");
            using (var db = Open(path)) Sql(db, "UPDATE segments SET vec=X'0102' WHERE vec IS NOT NULL");
            bool corrupt = false;
            try { await service.SearchAsync(new("desired meaning", Mode: SearchMode.Semantic)); }
            catch (InvalidDataException) { corrupt = true; }
            Require(corrupt, "Corrupt external vector update was hidden by the cache.");
            Require((await service.SearchAsync(new("needle", Mode: SearchMode.Keyword))).Hits.Count == 1, "Keyword path decoded corrupt vectors.");
        });
    }

    private static IndexedFile File(string path, IReadOnlyList<IndexSegment> segments) => new(0, path, "doc", 10, 1, "done", null, null, segments);
    private static float[] Direction(double cosine)
    {
        var vector = new float[768]; vector[0] = (float)cosine; vector[1] = (float)Math.Sqrt(1 - cosine * cosine); return vector;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db;
    }
    private static void Sql(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
}
