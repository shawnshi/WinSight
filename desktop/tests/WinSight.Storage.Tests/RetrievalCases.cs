using System.Reflection;
using WinSight.Core;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;

/// <summary>Retrieval-quality regressions: semantic acceptance gate, CJK lexical path, term frequency and the vector cache.</summary>
internal static class RetrievalCases
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static float[] Cosine(double cosine)
    {
        var vector = new float[768];
        vector[0] = (float)cosine;
        vector[1] = (float)Math.Sqrt(1 - cosine * cosine);
        return vector;
    }

    private static IndexedFile File(string path, string text, float[]? vector = null) =>
        new(0, path, "doc", 10, 1, "done", null, null, [new("chunk", vector == null ? "none" : "text", 1, null, text, vector)]);

    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> Test)
    {
        string Db(string name) => Path.Combine(root, "retrieval-" + name + ".sqlite");

        await Test("RET-H3 significance threshold follows corpus size and stays reachable", () =>
        {
            Require(double.IsPositiveInfinity(SearchService.SignificanceThreshold(SearchService.MinCalibratedUnits - 1)), "below the minimum the statistic is undefined");
            Require(Math.Abs(SearchService.NormalQuantile(0.975) - 1.959964) < 1e-5, "quantile 0.975");
            Require(Math.Abs(SearchService.NormalQuantile(0.999) - 3.090232) < 1e-5, "quantile 0.999");
            Require(Math.Abs(SearchService.NormalQuantile(0.001) + 3.090232) < 1e-5, "quantile symmetry");
            double previous = 0;
            foreach (var n in new[] { 5, 6, 8, 10, 30, 100, 1000, 100000 })
            {
                double z = SearchService.SignificanceThreshold(n);
                Require(z >= previous, $"threshold must not shrink with corpus size (n={n})");
                // A population z-score can never exceed sqrt(n-1); the old fixed z>=2 was unreachable for n=5 outside a degenerate sample.
                Require(z <= Math.Sqrt(n - 1) + 1e-9, $"threshold must stay reachable (n={n}, z={z})");
                previous = z;
            }
            return Task.CompletedTask;
        });

        await Test("RET-H3 small channels no longer hit the z>=2 dead zone", async () =>
        {
            await using var repo = new SqliteIndexRepository(Db("dead-zone"));
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            double[] cosines = [0.5, 0.45, 0.1, 0.12, 0.08, 0.11];
            double mean = cosines.Average(), sd = Math.Sqrt(cosines.Average(c => (c - mean) * (c - mean)));
            Require(cosines.Take(2).All(c => (c - mean) / sd < 2), "fixture must sit in the old dead zone");
            for (int i = 0; i < cosines.Length; i++) await repo.UpsertFileAsync(File($"C:/dead/{i}.txt", "text", Cosine(cosines[i])));
            var hits = (await new SearchService(repo, new FakeEmbedding()).SearchAsync(new SearchRequest("topic", Mode: SearchMode.Semantic))).Hits;
            Require(hits.Count == 2 && hits.All(h => h.Path.EndsWith("/0.txt", StringComparison.Ordinal) || h.Path.EndsWith("/1.txt", StringComparison.Ordinal)), "both relevant units must pass, noise must not: " + hits.Count);
        });

        await Test("RET-H3 pure noise is not flooded with a fixed percentile of the corpus", async () =>
        {
            await using var repo = new SqliteIndexRepository(Db("noise"));
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            var random = new Random(20261028);
            var cosines = new double[400];
            for (int i = 0; i < cosines.Length; i++)
            {
                double u1 = 1 - random.NextDouble(), u2 = random.NextDouble();
                cosines[i] = Math.Clamp(0.3 + 0.05 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2), 0.01, 0.99);
                await repo.UpsertFileAsync(File($"C:/noise/{i}.txt", "text", Cosine(cosines[i])));
            }
            double mean = cosines.Average(), sd = Math.Sqrt(cosines.Average(c => (c - mean) * (c - mean)));
            int oldGate = cosines.Count(c => (c - mean) / sd >= 2);
            var hits = (await new SearchService(repo, new FakeEmbedding()).SearchAsync(new SearchRequest("nothing relevant", Limit: 400, Mode: SearchMode.Semantic))).Hits;
            Require(oldGate >= 8, "fixture should show the old gate flooding (" + oldGate + ")");
            Require(hits.Count <= 5 && hits.Count < oldGate, $"noise hits {hits.Count} vs old gate {oldGate}");
        });

        await Test("RET-H1/H2 Chinese keyword candidates are scored before truncation and keep term frequency", async () =>
        {
            await using var repo = new SqliteIndexRepository(Db("cjk-rank"));
            // Equal coverage for every document: the old path ordered the 100-candidate cut by file id, so the
            // best-scoring document (inserted last) never reached BM25.
            for (int i = 0; i < 150; i++)
                await repo.UpsertFileAsync(File($"C:/cjk/filler-{i:000}.txt", $"合同条款说明第{i}号附件及相关补充材料清单文本内容较长用于拉低文档频率"));
            await repo.UpsertFileAsync(File("C:/cjk/relevant.txt", "合同 合同 合同 评审"));
            var service = new SearchService(repo, new FakeEmbedding { ThrowIfCalled = true });
            var hits = (await service.SearchAsync(new SearchRequest("合同", Limit: 10, Mode: SearchMode.Keyword))).Hits;
            Require(hits.Count == 10, "hit count " + hits.Count);
            Require(hits[0].Path.EndsWith("relevant.txt", StringComparison.Ordinal), "highest term-frequency document must rank first, got " + hits[0].Path);
        });

        await Test("RET-H1 Chinese lexical recall: sub-words, normalization, misses, mixed scripts", async () =>
        {
            await using var repo = new SqliteIndexRepository(Db("cjk-recall"));
            await repo.UpsertFileAsync(File("C:/cjk2/a.txt", "医保结算报销比例调整通知"));
            await repo.UpsertFileAsync(File("C:/cjk2/b.txt", "contract 医保 appendix"));
            await repo.UpsertFileAsync(File("C:/cjk2/c.txt", "完全无关的另一份材料"));
            var service = new SearchService(repo, new FakeEmbedding { ThrowIfCalled = true });
            async Task<string[]> Find(string query) => (await service.SearchAsync(new SearchRequest(query, Mode: SearchMode.Keyword))).Hits
                .Select(h => Path.GetFileName(h.Path)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Require((await Find("报销")).SequenceEqual(["a.txt"]), "word inside a sentence must be found");
            Require((await Find("结算报销")).Contains("a.txt"), "multi-word query");
            Require((await Find("醫保結算")).Contains("a.txt"), "traditional query must reach simplified text through shared normalization");
            Require((await Find("医保")).SequenceEqual(["a.txt", "b.txt"]), "same word across Chinese and mixed-script text");
            var mixed = (await service.SearchAsync(new SearchRequest("医保 contract", Mode: SearchMode.Keyword))).Hits;
            Require(mixed.Count == 2 && mixed[0].Path.EndsWith("b.txt", StringComparison.Ordinal), "full coverage across both scripts must outrank half coverage: " + string.Join(",", mixed.Select(h => Path.GetFileName(h.Path))));
            Require((await Find("不存在的词汇")).Length == 0, "unrelated query must return nothing");
            Require((await Find("我的")).Length <= 3, "function words alone must not crash");
        });

        await Test("RET-M5 segmented index keeps duplicates and uses shared normalization", () =>
        {
            var tokens = SearchText.SegmentedTokens("医保 医保 合同").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Require(tokens.Count(t => t == "医保") == 2, "term frequency must survive: " + string.Join("|", tokens));
            Require(SearchText.SegmentedTokens("醫保結算").Contains("医保", StringComparison.Ordinal), "traditional text is simplified before segmentation");
            Require(NativeSegmenter.SegmentQuery("醫保").Contains("医保"), "query normalization matches the index");
            var lexical = SearchText.LexicalTerms("我的合同 contract");
            Require(lexical.Latin.SequenceEqual(["contract"]) && lexical.Cjk.Contains("合同") && !lexical.Cjk.Contains("的"), "function words are dropped from CJK terms");
            return Task.CompletedTask;
        });

        await Test("OPS-REBUILD-FRESH-INDEX-NOT-STALE-AND-NO-HANDLE-LEAK", async () =>
        {
            var directory = Path.Combine(root, "retrieval-fresh-index");
            Directory.CreateDirectory(directory);
            var database = Path.Combine(directory, "index.sqlite");
            await using (var repository = new SqliteIndexRepository(database)) await repository.InitializeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            // A brand-new index never stored a profile: there is nothing to invalidate, so no replacement generation is needed.
            Require(!IndexRebuildCoordinator.NeedsRebuild(directory, "any-profile", "any-dictionary"), "A fresh index must not ask for a rebuild.");
            // The check must not leave a pooled read connection holding the database file.
            using (new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            await using (var repository = new SqliteIndexRepository(database)) await repository.SetMetaAsync("profile_id", "profile-a");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Require(IndexRebuildCoordinator.NeedsRebuild(directory, "profile-b", "any-dictionary"), "A stored profile that differs must still ask for a rebuild.");
            using (new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        });

        await Test("OPS-SEARCH-HITS-COMPARE-BY-VALUE", async () =>
        {
            await using var repo = new SqliteIndexRepository(Db("hit-equality"));
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            await repo.UpsertFileAsync(File("C:/equal/a.txt", "alpha", Cosine(0.9)));
            var service = new SearchService(repo, new FakeEmbedding());
            var first = (await service.SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Semantic))).Hits;
            var second = (await service.SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Semantic))).Hits;
            Require(first.Count == 1 && first.SequenceEqual(second), "Identical searches over unchanged data must return equal hits.");
            Require(first[0].GetHashCode() == second[0].GetHashCode(), "Equal hits must hash alike.");
            Require(!first[0].Equals(first[0] with { Channels = ["other"] }), "A different channel list is a different hit.");
        });

        await Test("RET-M7 vector cache survives queue writes and patches only touched files", async () =>
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var cacheField = typeof(SqliteIndexRepository).GetField("vectorCache", flags)!;
            var dirtyField = typeof(SqliteIndexRepository).GetField("dirtyVectorPaths", flags)!;
            await using var repo = new SqliteIndexRepository(Db("cache-patch"));
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            for (int i = 0; i < 20; i++) await repo.UpsertFileAsync(File($"C:/cache/{i}.txt", "alpha", Cosine(i == 0 ? 0.9 : 0.1)));
            var service = new SearchService(repo, new FakeEmbedding());
            var request = new SearchRequest("alpha", Mode: SearchMode.Semantic);
            string[] Names(IReadOnlyList<SearchHit> hits) => hits.Select(h => Path.GetFileName(h.Path)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Require(Names((await service.SearchAsync(request)).Hits).SequenceEqual(["0.txt"]), "baseline hit");
            Require(cacheField.GetValue(repo) is not null, "cache built by the first search");

            var now = DateTimeOffset.UtcNow;
            await repo.EnqueueIndexTaskAsync(new IndexTask("C:/cache/queued.txt", "C:/cache", false, "Add", null, "Pending", 0, now, now));
            Require(cacheField.GetValue(repo) is not null, "queue bookkeeping must not drop the decoded vectors");

            await repo.UpsertFileAsync(File("C:/cache/5.txt", "alpha", Cosine(0.95)));
            Require(cacheField.GetValue(repo) is not null && ((HashSet<string>)dirtyField.GetValue(repo)!).Contains("C:/cache/5.txt"), "a file write dirties only that file");
            Require(Names((await service.SearchAsync(request)).Hits).SequenceEqual(["0.txt", "5.txt"]), "patched cache must see the new vector");
            Require(((HashSet<string>)dirtyField.GetValue(repo)!).Count == 0 && cacheField.GetValue(repo) is not null, "dirty set is consumed and the cache stays valid");

            await repo.DeleteFileAsync("C:/cache/0.txt");
            Require(Names((await service.SearchAsync(request)).Hits).SequenceEqual(["5.txt"]), "deleted file must leave the semantic channel");

        });

        await Test("RET-P1-COW-CONCURRENT concurrent search, upsert, and TrimMemory stay consistent", async () =>
        {
            await using var repo = new SqliteIndexRepository(Db("cow-concurrent"));
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            for (int i = 0; i < 20; i++)
                await repo.UpsertFileAsync(File($"C:/cow/{i}.txt", "alpha", Cosine(0.1)));

            var service = new SearchService(repo, new FakeEmbedding());
            var writer = Task.Run(async () =>
            {
                for (int iter = 0; iter < 12; iter++)
                {
                    await repo.UpsertFileAsync(File("C:/cow/target.txt", "alpha", Cosine(0.95)));
                    await repo.UpsertFileAsync(File("C:/cow/1.txt", "alpha", Cosine(iter % 2 == 0 ? 0.2 : 0.15)));
                    await repo.DeleteFileAsync("C:/cow/2.txt");
                    await repo.UpsertFileAsync(File("C:/cow/2.txt", "alpha", Cosine(0.1)));
                }
            });
            var reader = Task.Run(async () =>
            {
                var req = new SearchRequest("alpha", Mode: SearchMode.Semantic);
                for (int iter = 0; iter < 12; iter++)
                {
                    var response = await service.SearchAsync(req);
                    Require(response.Hits.Count >= 0, "search returned negative hit count");
                    if (iter % 3 == 0) repo.TrimMemory();
                }
            });

            await Task.WhenAll(writer, reader);

            var converged = (await service.SearchAsync(new SearchRequest("alpha", Mode: SearchMode.Semantic))).Hits;
            Require(converged.Any(h => h.Path == "C:/cow/target.txt"), "converged cache must include final upserted hit");
        });

        await Test("RET-CJK-MULTIWORD-RECALL multi-term Chinese query matches across separated clauses", async () =>
        {
            await using var repo = new SqliteIndexRepository(Db("cjk-multiword"));
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            await repo.UpsertFileAsync(File("C:/cjk/arch.txt", "系统采用分布式架构，并通过租约机制保障缓存层的一致性。"));
            var service = new SearchService(repo, new FakeEmbedding());
            var hits = (await service.SearchAsync(new SearchRequest("分布式缓存一致性", Mode: SearchMode.Keyword))).Hits;
            Require(hits.Count == 1 && hits[0].Path == "C:/cjk/arch.txt", $"expected CJK multi-word recall, got {hits.Count} hits");
        });

        await Test("RET-INT8-AVX2-EQUIVALENCE pre-quantized DotInt8 matches float DotInt8 across multi-chunk index", () =>
        {
            var files = new Dictionary<long, IndexedFile>();
            for (long id = 1; id <= 150; id++)
                files[id] = new(id, $"C:/simd/{id}.txt", "doc", 10, 1, "done", null, null, []);
            var index = new CompactVectorIndex(files);
            var rng = new Random(20261116);
            Span<float> scratch = stackalloc float[CompactVectorIndex.Dimension];
            Span<sbyte> quantized = stackalloc sbyte[CompactVectorIndex.Dimension];
            byte chId = index.InternChannel("body");

            for (int row = 0; row < 150; row++)
            {
                double normSq = 0;
                for (int d = 0; d < CompactVectorIndex.Dimension; d++)
                {
                    float val = (float)(rng.NextDouble() * 2.0 - 1.0);
                    scratch[d] = val;
                    normSq += (double)val * val;
                }
                float invNorm = (float)(1.0 / Math.Sqrt(normSq));
                float maxAbs = 0f;
                for (int d = 0; d < CompactVectorIndex.Dimension; d++)
                {
                    scratch[d] *= invNorm;
                    float a = MathF.Abs(scratch[d]);
                    if (a > maxAbs) maxAbs = a;
                }
                float scale = maxAbs / 127.0f;
                float invScale = 127.0f / maxAbs;
                for (int d = 0; d < CompactVectorIndex.Dimension; d++)
                    quantized[d] = (sbyte)Math.Clamp((int)MathF.Round(scratch[d] * invScale), -127, 127);
                index.Add(row + 1, (row % 150) + 1, (ulong)(row + 1), chId, quantized, scale);
            }

            Require(index.ChunkCount == 3, $"expected 3 chunks at ChunkCapacity=64 for 150 vectors, got {index.ChunkCount}");
            var query = new float[CompactVectorIndex.Dimension];
            double qNormSq = 0;
            for (int d = 0; d < CompactVectorIndex.Dimension; d++)
            {
                query[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
                qNormSq += (double)query[d] * query[d];
            }
            float qInvNorm = (float)(1.0 / Math.Sqrt(qNormSq));
            for (int d = 0; d < CompactVectorIndex.Dimension; d++) query[d] *= qInvNorm;

            Span<short> qInt16 = stackalloc short[CompactVectorIndex.Dimension];
            CompactVectorIndex.QuantizeQuery(query, qInt16, out float queryScale);
            for (int i = 0; i < 150; i++)
            {
                var span = index.GetVectorSpan(i);
                var meta = index.GetMeta(i);
                double expected = CompactVectorIndex.DotInt8(span, meta.Scale, query);
                double actual = CompactVectorIndex.DotInt8PreQuantized(span, meta.Scale, qInt16, queryScale);
                Require(Math.Abs(expected - actual) < 1e-4, $"AVX2 pre-quantized mismatch at {i}: expected={expected:F6}, actual={actual:F6}");
            }
            return Task.CompletedTask;
        });

        await Test("RET-R2-BLK01-NO-STALE-PROMOTION concurrent readers during warm-cache File writes never promote stale snapshot", async () =>
        {
            await using var repo = new SqliteIndexRepository(Db("r2-blk01"));
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            for (int i = 0; i < 30; i++)
                await repo.UpsertFileAsync(File($"C:/blk01/noise-{i}.txt", "noise", Cosine(0.05 + (i % 5) * 0.01)));

            var service = new SearchService(repo, new FakeEmbedding());
            var req = new SearchRequest("target", Mode: SearchMode.Semantic);
            var warm = await service.SearchAsync(req);
            Require(warm.Hits.Count == 0, "baseline noise corpus should yield 0 semantic hits");

            using var readerStop = new CancellationTokenSource();
            var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                while (!readerStop.IsCancellationRequested)
                {
                    try { await service.SearchAsync(req, readerStop.Token); }
                    catch (OperationCanceledException) when (readerStop.IsCancellationRequested) { break; }
                }
            })).ToArray();

            try
            {
                for (int round = 0; round < 40; round++)
                {
                    if (round > 0)
                        await repo.DeleteFileAsync($"C:/blk01/target-{round - 1}.txt");
                    string targetPath = $"C:/blk01/target-{round}.txt";
                    await repo.UpsertFileAsync(File(targetPath, "target", Cosine(0.95)));
                    var hits = (await service.SearchAsync(req)).Hits;
                    Require(hits.Any(h => h.Path == targetPath),
                        $"round {round}: post-upsert search missed {targetPath} (stale vectorCache promotion?); got=[{string.Join(",", hits.Select(h => Path.GetFileName(h.Path)))}]");
                }
            }
            finally
            {
                await readerStop.CancelAsync();
                await Task.WhenAll(readers);
            }

            var finalHits = (await service.SearchAsync(req)).Hits;
            Require(finalHits.Any(h => h.Path == "C:/blk01/target-39.txt"), "final search must include target-39.txt");
            Require(!finalHits.Any(h => h.Path == "C:/blk01/target-38.txt"), "final search must exclude deleted target-38.txt");
        });
    }
}
