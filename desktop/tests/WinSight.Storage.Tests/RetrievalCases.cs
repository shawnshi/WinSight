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

        await Test("IR-P0-MINSCORE-HYBRID-SEMANTIC-AND-FILENAME-FUSION", async () =>
        {
            await using var repo = new SqliteIndexRepository(Db("ir-p0-hybrid"));
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            await repo.UpsertFileAsync(new(0, "C:/hybrid/target.docx", "doc", 100, 1, "done", null, null,
                [new("chunk", "text", 1, null, "target needle", Cosine(0.95), new(new("paragraph", Paragraph: 1, Part: "body"), "body"))]));
            await repo.UpsertFileAsync(File("C:/hybrid/concept.txt", "unrelated wording without query term", Cosine(0.92)));
            for (int i = 0; i < 10; i++)
                await repo.UpsertFileAsync(File($"C:/hybrid/bg-{i}.txt", "unrelated wording", Cosine(0.05)));

            // Multi-channel leader (keyword + filename + semantic) must not cause MinScore=0.25 to suppress semantic-only concept.txt,
            // and filename channel must merge into leader's paragraph unit rather than emitting a duplicate 'name' row.
            var service = new SearchService(repo, new FakeEmbedding());
            var hits = (await service.SearchAsync(new SearchRequest("target", Limit: 10, Mode: SearchMode.Hybrid, MinScore: 0.25))).Hits;
            var docxHits = hits.Where(h => h.Path == "C:/hybrid/target.docx").ToArray();
            Require(docxHits.Length == 1, $"expected fused single hit for target.docx, got {docxHits.Length}");
            var channels = docxHits[0].Channels ?? [];
            Require(docxHits[0].Source?.Location.Paragraph == 1 && channels.Contains("filename") && channels.Contains("keyword") && channels.Contains("semantic:body"),
                $"expected filename+keyword+semantic fusion on paragraph 1, got [{string.Join(",", channels)}]");
            Require(hits.Any(h => h.Path == "C:/hybrid/concept.txt" && h.MatchType == "semantic"),
                "MinScore=0.25 incorrectly dropped high-significance pure semantic hit");

            // Filename ranking prioritizes exact match > prefix > substring.
            await repo.UpsertFileAsync(File("C:/hybrid/long-prefix-alpha-tail.txt", "body"));
            await repo.UpsertFileAsync(File("C:/hybrid/alpha-note.txt", "body"));
            await repo.UpsertFileAsync(File("C:/hybrid/alpha.txt", "body"));
            var nameHits = (await new SearchService(repo, new FakeEmbedding { ThrowIfCalled = true })
                .SearchAsync(new SearchRequest("alpha", Limit: 5, Mode: SearchMode.Keyword))).Hits;
            Require(nameHits[0].Path == "C:/hybrid/alpha.txt" && nameHits[1].Path == "C:/hybrid/alpha-note.txt",
                $"expected exact>prefix filename rank order, got [{string.Join(",", nameHits.Select(h => Path.GetFileName(h.Path)))}]");
        });

        await Test("IR-P0-MULTIWORD-COOCCURRENCE-AND-DIVERSITY", async () =>
        {
            await using var repo = new SqliteIndexRepository(Db("ir-p0-diversity"));
            await repo.UpsertFileAsync(File("C:/div/coloc.txt", "alpha bravo"));
            await repo.UpsertFileAsync(new(0, "C:/div/split.txt", "doc", 10, 1, "done", null, null,
                [new("chunk", "none", 1, null, "alpha alpha alpha alpha"), new("chunk", "none", 2, null, "bravo")]));
            await repo.UpsertFileAsync(new(0, "C:/div/mixed.txt", "doc", 10, 1, "done", null, null,
                [new("chunk", "none", 1, null, "alpha alpha alpha alpha alpha"), new("chunk", "none", 2, null, "alpha bravo")]));
            await repo.UpsertFileAsync(new(0, "C:/div/huge.docx", "doc", 1000, 1, "done", null, null,
                Enumerable.Range(1, 60).Select(p => new IndexSegment("chunk", "none", p, null, "alpha bravo paragraph content",
                    Source: new(new("paragraph", Paragraph: p, Part: "body"), "body"))).ToArray()));
            for (int i = 0; i < 5; i++)
                await repo.UpsertFileAsync(File($"C:/div/extra-{i}.txt", "alpha bravo"));

            var service = new SearchService(repo, new FakeEmbedding { ThrowIfCalled = true });
            var limited = (await service.SearchAsync(new SearchRequest("alpha bravo", Limit: 10, Mode: SearchMode.Keyword))).Hits;
            int hugeCount = limited.Count(h => h.Path == "C:/div/huge.docx");
            int distinctFiles = limited.Select(h => h.Path).Distinct(StringComparer.Ordinal).Count();
            Require(hugeCount <= 4 && distinctFiles >= 6, $"per-file diversity failed: hugeCount={hugeCount}, distinctFiles={distinctFiles}");

            var all = (await service.SearchAsync(new SearchRequest("alpha bravo", Limit: 40, Mode: SearchMode.Keyword))).Hits;
            int colocIdx = Array.FindIndex(all.ToArray(), h => h.Path == "C:/div/coloc.txt");
            int splitIdx = Array.FindIndex(all.ToArray(), h => h.Path == "C:/div/split.txt");
            Require(colocIdx >= 0 && splitIdx > colocIdx, $"co-occurrence segment must outrank split chunks (coloc={colocIdx}, split={splitIdx})");
            var mixedHit = all.Single(h => h.Path == "C:/div/mixed.txt");
            Require(mixedHit.Excerpt == "alpha bravo", $"representative segment should pick full co-occurrence chunk, got '{mixedHit.Excerpt}'");
        });

        await Test("IR-P1-PARALLEL-VECTOR-SCAN-AND-WORD-CACHE", async () =>
        {
            const int totalVecs = 16384; // 256 chunks >= 256 parallel threshold
            const int fileCount = 200;
            var files = new Dictionary<long, IndexedFile>(fileCount);
            for (long fid = 1; fid <= fileCount; fid++)
                files[fid] = new(fid, $"C:/par/{fid}.txt", "doc", 10, 1, "done", null, null, []);
            var index = new CompactVectorIndex(files);
            var rng = new Random(20261117);
            Span<float> scratch = stackalloc float[CompactVectorIndex.Dimension];
            Span<sbyte> quantized = stackalloc sbyte[CompactVectorIndex.Dimension];
            byte bodyCh = index.InternChannel("body");
            byte imgCh = index.InternChannel("page-visual");

            for (int row = 0; row < totalVecs; row++)
            {
                double normSq = 0;
                for (int d = 0; d < CompactVectorIndex.Dimension; d++)
                {
                    float v = (float)(rng.NextDouble() * 2.0 - 1.0);
                    scratch[d] = v;
                    normSq += (double)v * v;
                }
                if (row < 10) scratch[0] += 3.0f;
                normSq = 0;
                for (int d = 0; d < CompactVectorIndex.Dimension; d++) normSq += (double)scratch[d] * scratch[d];
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
                long fid = (row % fileCount) + 1;
                ulong unitHash = (ulong)(row / 16 + 1);
                byte ch = (row & 1) == 0 ? bodyCh : imgCh;
                index.Add(row + 1, fid, unitHash, ch, quantized, scale);
            }
            index.MarkFileRemoved(10);
            index.MarkFileRemoved(20);
            Require(index.ChunkCount == 256, $"expected 256 chunks, got {index.ChunkCount}");

            var query = new float[CompactVectorIndex.Dimension];
            query[0] = 1.0f;
            _ = SearchService.SemanticCompact(index, files, query, 50, CancellationToken.None, allowParallel: false);
            _ = SearchService.SemanticCompact(index, files, query, 50, CancellationToken.None, allowParallel: true);

            var swSeq = System.Diagnostics.Stopwatch.StartNew();
            var seqGroups = SearchService.SemanticCompact(index, files, query, 50, CancellationToken.None, allowParallel: false);
            swSeq.Stop();
            var swPar = System.Diagnostics.Stopwatch.StartNew();
            var parGroups = SearchService.SemanticCompact(index, files, query, 50, CancellationToken.None, allowParallel: true);
            swPar.Stop();
            Console.WriteLine($"IR-VEC-SCAN-PERF vectors={totalVecs} chunks={index.ChunkCount} seqMs={swSeq.Elapsed.TotalMilliseconds:F3} parMs={swPar.Elapsed.TotalMilliseconds:F3}");

            Require(seqGroups.Count == parGroups.Count && seqGroups.Count > 0, "parallel/sequential channel counts differ");
            for (int g = 0; g < seqGroups.Count; g++)
            {
                Require(seqGroups[g].Channel == parGroups[g].Channel && seqGroups[g].Winners.Count == parGroups[g].Winners.Count,
                    $"group mismatch at {g}");
                for (int w = 0; w < seqGroups[g].Winners.Count; w++)
                {
                    var a = seqGroups[g].Winners[w];
                    var b = parGroups[g].Winners[w];
                    Require(a.SegmentId == b.SegmentId && a.FileId == b.FileId && Math.Abs(a.Rank - b.Rank) < 1e-12,
                        $"winner mismatch at {g}:{w}");
                }
            }

            string wordCacheDb = Db("ir-word-cache");
            await using var repo = new SqliteIndexRepository(wordCacheDb);
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            await repo.UpsertFileAsync(new(0, "C:/wcache/a.txt", "doc", 10, 1, "partial", "Semantic vectors pending", null,
                [new("chunk", "none", 1, null, "alpha needle", null)]));
            await repo.UpsertFileAsync(File("C:/wcache/b.txt", "unrelated", Cosine(0.1)));

            var searchSvc = new SearchService(repo, new FakeEmbedding());
            var kw = await searchSvc.SearchAsync(new SearchRequest("alpha", Limit: 10, Mode: SearchMode.Keyword));
            Require(kw.Hits.Count == 1, "baseline keyword hit");

            var wordField = typeof(SqliteIndexRepository).GetField("lastWordEvidence", BindingFlags.NonPublic | BindingFlags.Instance)!;
            static object? ExtractMatches(object? boxedTuple)
            {
                if (boxedTuple is null) return null;
                var rest = boxedTuple.GetType().GetField("Rest")?.GetValue(boxedTuple);
                return rest?.GetType().GetField("Item3")?.GetValue(rest);
            }
            var cachedBefore = wordField.GetValue(repo);
            Require(cachedBefore is not null, "lastWordEvidence must be populated after keyword search");
            var matchesBefore = ExtractMatches(cachedBefore);
            Require(matchesBefore is not null, "Matches tuple slot must be non-null");

            var now = DateTimeOffset.UtcNow;
            await repo.EnqueueIndexTaskAsync(new IndexTask("C:/wcache/q.txt", "C:/wcache", false, "Add", null, "Pending", 0, now, now));
            var pending = await repo.ReadPendingEmbeddingSegmentsAsync(10);
            Require(pending.Count == 1, "expected 1 pending embedding segment");
            int updated = await repo.SaveBackfilledVectorsAsync([new SegmentVectorUpdate(pending[0].Id, pending[0].FileId, Cosine(0.95))]);
            Require(updated == 1, "expected 1 backfilled segment");

            var cachedAfterBackfill = wordField.GetValue(repo);
            Require(cachedAfterBackfill is not null, "SaveBackfilledVectorsAsync must not invalidate lastWordEvidence");
            var matchesAfterBackfill = ExtractMatches(cachedAfterBackfill);
            Require(ReferenceEquals(matchesBefore, matchesAfterBackfill), "Matches cache instance must remain intact across backfill");

            var hyb = await searchSvc.SearchAsync(new SearchRequest("alpha", Limit: 10, Mode: SearchMode.Hybrid));
            Require(hyb.Hits.Count == 1 && hyb.SemanticAvailable, "hybrid search after backfill");
            var cachedAfterHybrid = wordField.GetValue(repo);
            var matchesAfterHybrid = ExtractMatches(cachedAfterHybrid);
            Require(ReferenceEquals(matchesBefore, matchesAfterHybrid), "Phase 2 hybrid search must reuse lastWordEvidence across vector backfill");

            await using (var external = new SqliteIndexRepository(wordCacheDb))
                await external.UpsertFileAsync(File("C:/wcache/c.txt", "alpha external", Cosine(0.8)));
            var afterExternal = await searchSvc.SearchAsync(new SearchRequest("alpha", Limit: 10, Mode: SearchMode.Keyword));
            Require(afterExternal.Hits.Count == 2, "external lexical write must invalidate lastWordEvidence cache");

            await using (var rawSql = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={wordCacheDb};Pooling=false"))
            {
                await rawSql.OpenAsync();
                static object? SqlScalar(Microsoft.Data.Sqlite.SqliteConnection db, string sql)
                {
                    using var c = db.CreateCommand();
                    c.CommandText = sql;
                    var v = c.ExecuteScalar();
                    return v is DBNull ? null : v;
                }
                Require(Convert.ToInt64(SqlScalar(rawSql, "SELECT count(*) FROM sqlite_master WHERE name='fts_seg_content'")) == 0,
                    "fts_seg must be contentless without fts_seg_content shadow table");
                Require(Convert.ToInt64(SqlScalar(rawSql, "SELECT count(*) FROM fts WHERE original IS NOT NULL")) == 0,
                    "fts.original must remain NULL to avoid duplicate text storage");
                var infoJson = SqlScalar(rawSql, "SELECT info FROM files WHERE path='C:/wcache/a.txt'") as string;
                Require(infoJson is not null && infoJson.Contains("\"_sourceAnchorsV1\"", StringComparison.Ordinal)
                    && !infoJson.Contains("\"Page\":null", StringComparison.Ordinal)
                    && !infoJson.Contains("\"Fields\":[]", StringComparison.Ordinal),
                    "Compact anchor JSON must omit null Page and empty Fields");
                Require(Convert.ToInt64(SqlScalar(rawSql, "SELECT length(vec) FROM segments WHERE file=(SELECT id FROM files WHERE path='C:/wcache/b.txt') AND vec IS NOT NULL")) == 1536,
                    "Pre-backfill vector should initially be FP16 (1536 bytes)");
                Require(Convert.ToInt64(SqlScalar(rawSql, "SELECT length(vec) FROM segments WHERE file=(SELECT id FROM files WHERE path='C:/wcache/a.txt') AND vec IS NOT NULL")) == 772,
                    "SaveBackfilledVectorsAsync must default vector_encoding to int8 (772 bytes)");
            }

            var aRoundtrip = await repo.GetFileAsync("C:/wcache/a.txt");
            Require(aRoundtrip?.Segments.Single(s => s.Kind != "name").Source?.Fields is not null,
                "ReadAnchors must round-trip omitted Fields as non-null empty array");

            await repo.CompactStorageAsync();
            await using (var rawSql = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={wordCacheDb};Pooling=false"))
            {
                await rawSql.OpenAsync();
                using var fp16Cmd = rawSql.CreateCommand();
                fp16Cmd.CommandText = "SELECT count(*) FROM segments WHERE vec IS NOT NULL AND length(vec) = 1536";
                Require(Convert.ToInt64(fp16Cmd.ExecuteScalar()) == 0, "CompactStorageAsync must convert all 1536-byte FP16 vectors to Int8");
                using var bLenCmd = rawSql.CreateCommand();
                bLenCmd.CommandText = "SELECT length(vec) FROM segments WHERE file=(SELECT id FROM files WHERE path='C:/wcache/b.txt') AND vec IS NOT NULL";
                Require(Convert.ToInt64(bLenCmd.ExecuteScalar()) == 772, "b.txt vector must be compacted to 772 bytes");
            }
            var bRoundtrip = await repo.GetFileAsync("C:/wcache/b.txt");
            Require(VectorMath.Dot(Cosine(0.1), bRoundtrip!.Segments.Single(s => s.Kind != "name").Vector!) > 0.9999,
                "Compacted Int8 vector must preserve >0.9999 similarity");

            await using var resetRepo = new SqliteIndexRepository(Db("ir-p2-compact-reset"));
            await resetRepo.UpsertFileAsync(new(0, "C:/reset/f1.jpg", "image", 10, 1, "partial",
                "1 embedding errors: InvalidDataException: Vision feature count differs from valid, unpadded patch count", "{\"indexRoot\":\"C:/reset\"}", []));
            await resetRepo.UpsertFileAsync(new(0, "C:/reset/f2.doc", "doc", 10, 1, "skipped",
                "Legacy binary Office format .doc is not extracted because the isolated reader host is unavailable; save it as .docx", "{\"indexRoot\":\"C:/reset\"}", []));
            await resetRepo.UpsertFileAsync(new(0, "C:/reset/f3.txt", "doc", 10, 1, "done",
                null, "{\"indexRoot\":\"C:/reset\"}", []));
            await resetRepo.EnqueueIndexTaskAsync(new IndexTask("C:/reset/standalone.pdf", "C:/reset", false, "Add", null, "Deferred", 3, now.AddMinutes(10), now, "synthetic-defer"));

            int resetCount = await resetRepo.ResetFailedFilesAsync();
            Require(resetCount == 3, $"Expected 3 reset items (f1 partial + f2 skipped + standalone task), got {resetCount}");
            Require((await resetRepo.GetFileAsync("C:/reset/f1.jpg")) is { State: "pending", Detail: null }, "f1 vision error should reset to pending");
            Require((await resetRepo.GetFileAsync("C:/reset/f2.doc")) is { State: "pending", Detail: null }, "f2 host-unavailable skip should reset to pending");
            Require((await resetRepo.GetFileAsync("C:/reset/f3.txt")) is { State: "done" }, "done file must stay untouched");
            foreach (var tp in new[] { "C:/reset/f1.jpg", "C:/reset/f2.doc", "C:/reset/standalone.pdf" })
            {
                var task = await resetRepo.GetTaskAsync(tp);
                Require(task is { State: "Ready", Attempts: 0 }, $"Task {tp} should be Ready with Attempts=0 after reset");
            }
            var dueTasks = await resetRepo.ReadDueTasksAsync(10);
            Require(dueTasks.Count == 3, $"Expected 3 due Ready tasks immediately after ResetFailedFilesAsync, got {dueTasks.Count}");
        });

        await Test("IR-P0-P2-QUERY-SHAPES-AND-LITERAL-FASTPATH", async () =>
        {
            await using var repo = new SqliteIndexRepository(Db("query-shapes"));
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            await repo.UpsertFileAsync(new(0, "C:/docs/2026/Q3 report.txt", "doc", 100, 1, "done",
                null, "{\"indexRoot\":\"C:/docs\"}",
                [new("chunk", "text", 0, null, "2026 Q3 quarterly budget summary", Cosine(0.8))]));
            await repo.UpsertFileAsync(new(0, "C:/docs/2026/split_path_excerpt.txt", "doc", 100, 1, "done",
                null, "{\"indexRoot\":\"C:/docs\"}",
                [new("chunk", "text", 0, null, "Q3 quarterly budget summary", Cosine(0.82))]));
            await repo.UpsertFileAsync(new(0, "C:/docs/quantum_plan.txt", "doc", 100, 1, "done",
                null, "{\"indexRoot\":\"C:/docs\"}",
                [new("chunk", "text", 0, null, "量子计算硬件路线图与架构设计", Cosine(0.75))]));
            await repo.UpsertFileAsync(new(0, "C:/docs/scattered.txt", "doc", 100, 1, "done",
                null, "{\"indexRoot\":\"C:/docs\"}",
                [new("chunk", "text", 0, null, "alpha " + new string('x', 120) + " beta", Cosine(0.1))]));
            await repo.UpsertFileAsync(new(0, "C:/docs/adjacent.txt", "doc", 100, 1, "done",
                null, "{\"indexRoot\":\"C:/docs\"}",
                [new("chunk", "text", 0, null, "alpha beta close match", Cosine(0.1))]));

            var svc = new SearchService(repo, new FakeEmbedding());

            // P1-2: Folder + filename combined literal match + P0-1 Hybrid literal fastpath
            var litRes = await svc.SearchAsync(new SearchRequest("2026 Q3", Mode: SearchMode.Hybrid));
            Require(litRes.Hits.Any(h => h.Path.EndsWith("/2026/Q3 report.txt", StringComparison.Ordinal) && (h.Channels?.Contains("filename") ?? false)),
                "Combined folder+filename literal should hit filename channel and pass Hybrid literal fastpath");

            // BLK-01: Split path+excerpt multi-literal query in Semantic mode must pass FilterVectorSegmentsAsync fastpath
            var splitSemRes = await svc.SearchAsync(new SearchRequest("2026 Q3", Mode: SearchMode.Semantic));
            Require(splitSemRes.Hits.Any(h => h.Path.EndsWith("/2026/split_path_excerpt.txt", StringComparison.Ordinal)),
                "Split path (2026) + excerpt (Q3) literal query must recall split_path_excerpt.txt in Semantic mode");

            // P0-2: Chinese natural-language sentence with filler stopwords must not be killed by Coverage
            var sentRes = await svc.SearchAsync(new SearchRequest("帮我找一下关于量子计算的相关方案", Mode: SearchMode.Keyword));
            Require(sentRes.Hits.Any(h => h.Path.EndsWith("/quantum_plan.txt", StringComparison.Ordinal)),
                "Chinese sentence with filler stopwords should recall quantum_plan.txt in Keyword mode");

            // P1-1: Multi-word proximity preference
            var proxRes = await svc.SearchAsync(new SearchRequest("alpha beta", Mode: SearchMode.Keyword));
            Require(proxRes.Hits.Count >= 2 && proxRes.Hits[0].Path.EndsWith("/adjacent.txt", StringComparison.Ordinal),
                "Adjacent multi-word match should outrank scattered match");

            // P2: Per-file max calibration prevents single multi-segment file from distorting Z-score
            await using var calRepo = new SqliteIndexRepository(Db("per-file-cal"));
            await calRepo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            var manyUnits = Enumerable.Range(0, 40)
                .Select(i => new IndexSegment("chunk", "text", i, null, $"unit {i}", Cosine(0.45),
                    Source: new SourceAnchor(new SourceLocation("pdf-page", Page: i + 1), "body", null, [])))
                .ToArray();
            await calRepo.UpsertFileAsync(new(0, "C:/cal/big.txt", "doc", 1000, 1, "done", null, "{\"indexRoot\":\"C:/cal\"}", manyUnits));
            for (int i = 0; i < 5; i++)
                await calRepo.UpsertFileAsync(File($"C:/cal/small_{i}.txt", "text", Cosine(0.1)));
            await calRepo.UpsertFileAsync(File("C:/cal/target.txt", "text", Cosine(0.85)));
            var calRes = await new SearchService(calRepo, new FakeEmbedding()).SearchAsync(new SearchRequest("topic", Mode: SearchMode.Semantic));
            Require(calRes.Hits.Any(h => h.Path.EndsWith("/target.txt", StringComparison.Ordinal)),
                "Per-file calibration should recall target.txt even when big.txt has 40 medium-cosine units");

            // Legacy contentful fts_seg migration + VACUUM inside CompactStorageAsync
            string legacyDb = Db("legacy-fts-seg-compact");
            await using var legacyRepo = new SqliteIndexRepository(legacyDb);
            await legacyRepo.UpsertFileAsync(new(0, "C:/legacy/doc.txt", "doc", 100, 1, "done", null, "{\"indexRoot\":\"C:/legacy\"}",
                [new("chunk", "text", 0, null, "医疗语义层架构", null)]));
            await legacyRepo.DisposeAsync();
            await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyDb};Pooling=false"))
            {
                await raw.OpenAsync();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = """
                    DROP TABLE fts_seg;
                    CREATE VIRTUAL TABLE fts_seg USING fts5(tokens,tokenize="unicode61 remove_diacritics 2");
                    INSERT INTO fts_seg(rowid, tokens) SELECT id, '医疗 语义 层 架构' FROM segments WHERE kind='chunk';
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            await using var reopenedLegacy = new SqliteIndexRepository(legacyDb);
            await reopenedLegacy.CompactStorageAsync();
            await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyDb};Pooling=false"))
            {
                await raw.OpenAsync();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='fts_seg_content'";
                Require(Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 0, "CompactStorageAsync must drop legacy fts_seg_content");
            }
            var legacySearch = await new SearchService(reopenedLegacy).SearchAsync(new SearchRequest("医疗 语义", Mode: SearchMode.Keyword));
            Require(legacySearch.Hits.Any(h => h.Path == "C:/legacy/doc.txt"), "Migrated contentless fts_seg must preserve CJK keyword search");

            // Preview P0-P2: vector-less file snapshot
            await reopenedLegacy.SetMetaAsync("profile_id", "synthetic-profile-v1");
            await reopenedLegacy.UpsertFileAsync(new(0, "C:/legacy/vec.txt", "doc", 50, 1, "done", null, "{\"indexRoot\":\"C:/legacy\"}",
                [new("chunk", "text", 1, null, "vectorless snapshot check", Cosine(0.9),
                    Source: new SourceAnchor(new SourceLocation("pdf-page", Page: 2), "ocr", null, []))]));
            var noVec = await reopenedLegacy.GetFileWithoutVectorsAsync("C:/legacy/vec.txt");
            Require(noVec is not null && noVec.Segments.Any(s => s.Kind != "name" && s.Vector is null && s.Source?.Location.Page == 2),
                "GetFileWithoutVectorsAsync must preserve segments/anchors without decoding vectors");
        });

        await Test("INDEX-P0-P2-QUEUE-ADMISSION-BATCH-STATES-AND-PENDING-PRESERVE", async () =>
        {
            string qSandbox = Path.Combine(root, "idx-p0-p2");
            string docs = Path.Combine(qSandbox, "docs");
            string excludedDir = Path.Combine(docs, "excluded");
            string skippedSub = Path.Combine(docs, "node_modules", "pkg");
            string dotGitSub = Path.Combine(docs, ".git");
            Directory.CreateDirectory(docs);
            Directory.CreateDirectory(excludedDir);
            Directory.CreateDirectory(skippedSub);
            Directory.CreateDirectory(dotGitSub);

            await using var repo = new SqliteIndexRepository(Db("idx-p0-p2"));
            await repo.InitializeAsync();
            await repo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            await using var queue = new WinSight.Infrastructure.Indexing.IncrementalIndexQueue(
                repo,
                maxQueueTasks: 2,
                quietPeriod: TimeSpan.FromMilliseconds(20),
                maxWait: TimeSpan.FromSeconds(2))
            {
                ExcludedRoots = [excludedDir],
                SkipExtensions = new HashSet<string>(["rtf"], StringComparer.OrdinalIgnoreCase)
            };
            await queue.InitializeAsync();

            string validTxt = Path.Combine(docs, "valid.txt");
            string validMd = Path.Combine(docs, "note.md");
            string dllPath = Path.Combine(docs, "build.dll");
            string pdbPath = Path.Combine(docs, "cache.pdb");
            string rtfPath = Path.Combine(docs, "skipme.rtf");
            string nmMd = Path.Combine(skippedSub, "index.md");
            string gitCfg = Path.Combine(dotGitSub, "config");
            string exTxt = Path.Combine(excludedDir, "ignored.txt");

            System.IO.File.WriteAllText(validTxt, "valid txt content");
            System.IO.File.WriteAllText(validMd, "valid md content");
            System.IO.File.WriteAllText(dllPath, "binary");
            System.IO.File.WriteAllText(pdbPath, "symbols");
            System.IO.File.WriteAllText(rtfPath, @"{\rtf1 skipped}");
            System.IO.File.WriteAllText(nmMd, "# pkg");
            System.IO.File.WriteAllText(gitCfg, "[core]");
            System.IO.File.WriteAllText(exTxt, "excluded");

            foreach (var bad in new[] { dllPath, pdbPath, rtfPath, nmMd, gitCfg, exTxt })
            {
                await queue.EnqueueEventAsync(bad, docs, false, WatcherChangeTypes.Created);
                Require(await repo.GetTaskAsync(bad) is null, $"Non-qualifying path must not enter queue: {bad}");
            }

            await queue.EnqueueEventAsync(validTxt, docs, false, WatcherChangeTypes.Created);
            await queue.EnqueueEventAsync(validMd, docs, false, WatcherChangeTypes.Created);
            Require(await repo.GetTaskAsync(validTxt) is not null && await repo.GetTaskAsync(validMd) is not null, "Valid files must enter queue");

            // Rename valid -> ignored extension must emit Delete for oldPath only
            await repo.AckTasksAsync([WinSight.Infrastructure.Extraction.FilePolicy.Normalize(validTxt), WinSight.Infrastructure.Extraction.FilePolicy.Normalize(validMd)]);
            await queue.EnqueueEventAsync(dllPath, docs, false, WatcherChangeTypes.Renamed, oldPath: validTxt);
            var renDelTask = await repo.GetTaskAsync(validTxt);
            Require(renDelTask is { Kind: "Delete" } && await repo.GetTaskAsync(dllPath) is null,
                "Rename from valid to ignored extension must enqueue Delete for oldPath only");
            await repo.AckTaskAsync(renDelTask!.Path);

            // Upsert validMd as vectorless partial with SemanticUnavailableDetail; ReconcileRootAsync must NOT re-queue it
            var mdInfo = new FileInfo(validMd);
            string normMd = WinSight.Infrastructure.Extraction.FilePolicy.Normalize(validMd);
            await repo.UpsertFileAsync(new(0, normMd, "doc", mdInfo.Length, new DateTimeOffset(mdInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0,
                "partial", WinSight.Infrastructure.Extraction.FilePolicy.SemanticUnavailableDetail, null,
                [new("chunk", "none", 1, null, "partial searchable text", null)]));

            for (int i = 0; i < 3; i++)
                System.IO.File.WriteAllText(Path.Combine(docs, $"extra{i}.txt"), $"extra {i}");

            await queue.ReconcileRootAsync(docs, code: false, force: true);
            foreach (var bad in new[] { dllPath, pdbPath, rtfPath, nmMd, gitCfg, exTxt, validMd })
                Require(await repo.GetTaskAsync(bad) is null, $"Reconcile must not enqueue skipped or text-partial path: {bad}");
            Require((await repo.GetTaskCountsAsync()).Values.Sum() <= 2 && queue.DroppedCount > 0,
                "Reconcile must enforce maxQueueTasks cap via TrimTasksAsync");

            // Non-destructive pre-extraction pending marker preserves segments, FTS matchability, and backfilled vectors
            string normKeep = WinSight.Infrastructure.Extraction.FilePolicy.Normalize(validTxt);
            await repo.UpsertFileAsync(new(0, normKeep, "doc", 17, 1, "done", null, "{\"indexRoot\":\"" + docs.Replace("\\", "\\\\") + "\"}",
                [new("chunk", "text", 1, null, "semantic alpha preserve", Cosine(0.95))]));
            await repo.UpsertFileAsync(new(0, normKeep, "doc", 17, 2, "pending", null, null, []));
            var preserved = await repo.GetFileAsync(normKeep);
            Require(preserved is { State: "pending" } && preserved.Segments.Any(s => s.Excerpt == "semantic alpha preserve" && s.Vector is not null),
                "Pre-extraction pending marker must preserve segments and vectors");
            var kwHits = await repo.KeywordSearchAsync("semantic alpha", 5);
            Require(kwHits.Any(h => h.FileId == preserved!.Id), "Pre-extraction pending marker must preserve FTS visibility");

            // Batch UpdateTasksStateAsync and AckTasksAsync
            var now = DateTimeOffset.UtcNow;
            var batchTasks = Enumerable.Range(0, 3)
                .Select(i => new IndexTask(WinSight.Infrastructure.Extraction.FilePolicy.Normalize(Path.Combine(docs, $"batch{i}.txt")), docs, false, "Add", null, "Ready", 0, now, now))
                .ToArray();
            await repo.AckTasksAsync((await repo.ReadDueTasksAsync(20)).Select(t => t.Path).ToArray());
            await repo.EnqueueIndexTasksAsync(batchTasks);
            await repo.UpdateTasksStateAsync(batchTasks, "Running", now);
            foreach (var bt in batchTasks)
                Require((await repo.GetTaskAsync(bt.Path)) is { State: "Running" }, "Batch UpdateTasksStateAsync must mark Running");
            await repo.AckTasksAsync(batchTasks.Select(t => t.Path).ToArray());
            Require((await repo.GetTaskCountsAsync()).Values.Sum() == 0, "Batch AckTasksAsync must delete all tasks");

            // BLOCKING-1: Nested chosen root inside excluded directory must survive ancestor reconcile (NM-BR-02)
            string chosenDir = Path.Combine(excludedDir, "chosen");
            Directory.CreateDirectory(chosenDir);
            string chosenFile = WinSight.Infrastructure.Extraction.FilePolicy.Normalize(Path.Combine(chosenDir, "allowed.txt"));
            System.IO.File.WriteAllText(chosenFile, "nested allowed content");
            var chosenFi = new FileInfo(chosenFile);
            await repo.UpsertFileAsync(new(0, chosenFile, "doc", chosenFi.Length,
                new DateTimeOffset(chosenFi.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0,
                "done", null, "{\"indexRoot\":\"" + chosenDir.Replace("\\", "\\\\") + "\"}",
                [new("chunk", "none", 1, null, "nested allowed content", null)]));

            await using var nestedQueue = new WinSight.Infrastructure.Indexing.IncrementalIndexQueue(repo, maxQueueTasks: 100)
            {
                AuthorizedRoots = [docs, chosenDir],
                ExcludedRoots = [excludedDir],
                SkipExtensions = new HashSet<string>(["rtf"], StringComparer.OrdinalIgnoreCase)
            };
            await nestedQueue.InitializeAsync();
            await nestedQueue.ReconcileRootsAsync([docs, chosenDir], code: false, force: true, requireStabilization: false);
            var chosenTask = await repo.GetTaskAsync(chosenFile);
            Require(chosenTask is null || chosenTask.Kind != "Delete",
                $"Nested chosen root file must not be marked for deletion on ancestor reconcile (task={chosenTask?.Kind})");
            Require(await repo.GetTaskAsync(exTxt) is null, "Non-overridden excluded file must still be skipped");

            // Case-only rename must emit Update preserving pre-existing casing (NM-BR-08)
            string upperCasePath = Path.Combine(chosenDir, "CaseRename.txt");
            System.IO.File.WriteAllText(upperCasePath, "case rename");
            string lowerCasePath = Path.Combine(chosenDir, "caserename.txt");
            await nestedQueue.EnqueueEventAsync(lowerCasePath, docs, false, WatcherChangeTypes.Renamed, oldPath: upperCasePath);
            var caseTask = await repo.GetTaskAsync(upperCasePath);
            Require(caseTask is { Kind: "Update", Tombstone: null }, "Case-only rename must enqueue Update preserving old path casing");
            await repo.AckTaskAsync(caseTask!.Path);

            // NON-BLOCKING-5: USN V2/V3 record buffer parsing & fail-closed on V3 128-bit FRN / V4+
            byte[] v2Name = System.Text.Encoding.Unicode.GetBytes("v2.txt");
            byte[] v3Name = System.Text.Encoding.Unicode.GetBytes("v3.txt");
            int v2Len = (60 + v2Name.Length + 7) & ~7;
            int v3Len = (76 + v3Name.Length + 7) & ~7;
            uint totalBytes = (uint)(8 + v2Len + v3Len);
            IntPtr usnBuf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)totalBytes);
            try
            {
                for (int i = 0; i < (int)totalBytes; i++) System.Runtime.InteropServices.Marshal.WriteByte(usnBuf, i, 0);
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, 0, 700L);
                // V2 at offset 8
                System.Runtime.InteropServices.Marshal.WriteInt32(usnBuf, 8 + 0, v2Len);
                System.Runtime.InteropServices.Marshal.WriteInt16(usnBuf, 8 + 4, 2);
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, 8 + 8, 1001L);
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, 8 + 16, 10L);
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, 8 + 24, 500L);
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, 8 + 32, DateTimeOffset.UtcNow.ToFileTime());
                System.Runtime.InteropServices.Marshal.WriteInt32(usnBuf, 8 + 40, unchecked((int)WinSight.Infrastructure.Indexing.UsnJournalReader.USN_REASON_FILE_CREATE));
                System.Runtime.InteropServices.Marshal.WriteInt32(usnBuf, 8 + 52, 0x20);
                System.Runtime.InteropServices.Marshal.WriteInt16(usnBuf, 8 + 56, (short)v2Name.Length);
                System.Runtime.InteropServices.Marshal.WriteInt16(usnBuf, 8 + 58, 60);
                System.Runtime.InteropServices.Marshal.Copy(v2Name, 0, IntPtr.Add(usnBuf, 8 + 60), v2Name.Length);
                // V3 at offset 8 + v2Len
                int off3 = 8 + v2Len;
                System.Runtime.InteropServices.Marshal.WriteInt32(usnBuf, off3 + 0, v3Len);
                System.Runtime.InteropServices.Marshal.WriteInt16(usnBuf, off3 + 4, 3);
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, off3 + 8, 2002L);
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, off3 + 16, 0L);
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, off3 + 24, 20L);
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, off3 + 32, 0L);
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, off3 + 40, 600L);
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, off3 + 48, DateTimeOffset.UtcNow.ToFileTime());
                System.Runtime.InteropServices.Marshal.WriteInt32(usnBuf, off3 + 56, unchecked((int)WinSight.Infrastructure.Indexing.UsnJournalReader.USN_REASON_DATA_EXTEND));
                System.Runtime.InteropServices.Marshal.WriteInt32(usnBuf, off3 + 68, 0x20);
                System.Runtime.InteropServices.Marshal.WriteInt16(usnBuf, off3 + 72, (short)v3Name.Length);
                System.Runtime.InteropServices.Marshal.WriteInt16(usnBuf, off3 + 74, 76);
                System.Runtime.InteropServices.Marshal.Copy(v3Name, 0, IntPtr.Add(usnBuf, off3 + 76), v3Name.Length);

                var parsedList = new List<WinSight.Infrastructure.Indexing.UsnChangeRecord>();
                var parseOk = WinSight.Infrastructure.Indexing.UsnJournalReader.ParseRecordBuffer(usnBuf, totalBytes, 400L, 5000, parsedList);
                Require(parseOk.Ok && parsedList.Count == 2 && parseOk.NextUsn == 700L
                    && parsedList[0].FileName == "v2.txt" && parsedList[0].FileReferenceNumber == 1001UL
                    && parsedList[1].FileName == "v3.txt" && parsedList[1].FileReferenceNumber == 2002UL && parsedList[1].Usn == 600L,
                    "USN ParseRecordBuffer must parse both V2 and 64-bit V3 records accurately");

                // V3 with non-zero high 64 bits must fail closed
                System.Runtime.InteropServices.Marshal.WriteInt64(usnBuf, off3 + 16, 1L);
                var parseHigh = WinSight.Infrastructure.Indexing.UsnJournalReader.ParseRecordBuffer(usnBuf, totalBytes, 400L, 5000, new List<WinSight.Infrastructure.Indexing.UsnChangeRecord>());
                Require(!parseHigh.Ok && parseHigh.ErrorReason == "unsupported-128bit-frn",
                    $"V3 non-zero frnHigh must fail closed, got ok={parseHigh.Ok} err={parseHigh.ErrorReason}");

                // V4 record must fail closed
                System.Runtime.InteropServices.Marshal.WriteInt32(usnBuf, 8 + 0, 80);
                System.Runtime.InteropServices.Marshal.WriteInt16(usnBuf, 8 + 4, 4);
                var parseV4 = WinSight.Infrastructure.Indexing.UsnJournalReader.ParseRecordBuffer(usnBuf, 88, 400L, 5000, new List<WinSight.Infrastructure.Indexing.UsnChangeRecord>());
                Require(!parseV4.Ok && parseV4.ErrorReason == "unsupported-record-v4",
                    $"USN major v4 must fail closed, got ok={parseV4.Ok} err={parseV4.ErrorReason}");
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(usnBuf);
            }

            // BLOCKING-3: Multi-page USN catch-up (>5000 records) succeeds and >40-page backlog fails closed to directory fallback
            await using var usnRepo = new SqliteIndexRepository(Db("idx-usn-paging"));
            await usnRepo.InitializeAsync();
            await usnRepo.SetMetaAsync("profile_id", "synthetic-profile-v1");
            await usnRepo.UpsertFileAsync(new(0, chosenFile, "doc", chosenFi.Length,
                new DateTimeOffset(chosenFi.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0,
                "done", null, null, [new("chunk", "none", 1, null, "nested allowed content", null)]));
            await usnRepo.SaveUsnCheckpointAsync("VOL_PAGING", "00000000000000AA", "1000", DateTimeOffset.UtcNow.AddMinutes(-1));

            var fillerRecord = new WinSight.Infrastructure.Indexing.UsnChangeRecord(1001, 9001, 1, WinSight.Infrastructure.Indexing.UsnJournalReader.USN_REASON_DATA_EXTEND, "filler.txt", DateTimeOffset.UtcNow);
            var targetRecord = new WinSight.Infrastructure.Indexing.UsnChangeRecord(6000, 9002, 2, WinSight.Infrastructure.Indexing.UsnJournalReader.USN_REASON_FILE_CREATE, "allowed.txt", DateTimeOffset.UtcNow);
            var page1 = Enumerable.Repeat(fillerRecord, 5000).ToArray();
            int multiCall = 0;
            var multiPageReader = new ScriptedUsnReader(
                "VOL_PAGING",
                new WinSight.Infrastructure.Indexing.UsnJournalInfo(0xAAUL, 100, 6001),
                (_, _, startUsn, max) =>
                {
                    multiCall++;
                    return startUsn == 1000
                        ? (true, (IReadOnlyList<WinSight.Infrastructure.Indexing.UsnChangeRecord>)page1, 6000L, (string?)null)
                        : (true, (IReadOnlyList<WinSight.Infrastructure.Indexing.UsnChangeRecord>)[targetRecord], 6001L, (string?)null);
                },
                frn => frn == 9002UL ? chosenFile : null);
            await using (var multiQueue = new WinSight.Infrastructure.Indexing.IncrementalIndexQueue(usnRepo, usnReader: multiPageReader)
            {
                AuthorizedRoots = [docs, chosenDir],
                ExcludedRoots = [excludedDir]
            })
            {
                await multiQueue.InitializeAsync();
                bool ok = await multiQueue.ReconcileRootsAsync([docs, chosenDir], code: false, force: true, requireStabilization: false);
                Require(ok && multiCall == 2 && multiQueue.UsnState == "usn_caught_up(1 records)",
                    $"Multi-page USN must catch up in 2 pages, got ok={ok} calls={multiCall} state={multiQueue.UsnState}");
                Require((await usnRepo.GetUsnCheckpointAsync("VOL_PAGING"))?.NextUsn == "6001", "Multi-page USN checkpoint must advance to 6001");
            }

            await usnRepo.SaveUsnCheckpointAsync("VOL_PAGING", "00000000000000AA", "1000", DateTimeOffset.UtcNow.AddMinutes(-1));
            int overflowCalls = 0;
            var backlogReader = new ScriptedUsnReader(
                "VOL_PAGING",
                new WinSight.Infrastructure.Indexing.UsnJournalInfo(0xAAUL, 100, 999999L),
                (_, _, startUsn, max) =>
                {
                    overflowCalls++;
                    return (true, (IReadOnlyList<WinSight.Infrastructure.Indexing.UsnChangeRecord>)page1, startUsn + 100L, (string?)null);
                },
                _ => null);
            await using (var overflowQueue = new WinSight.Infrastructure.Indexing.IncrementalIndexQueue(usnRepo, usnReader: backlogReader)
            {
                AuthorizedRoots = [docs, chosenDir],
                ExcludedRoots = [excludedDir]
            })
            {
                await overflowQueue.InitializeAsync();
                bool ok = await overflowQueue.ReconcileRootsAsync([docs, chosenDir], code: false, force: true, requireStabilization: false);
                Require(ok && overflowCalls == 40 && overflowQueue.UsnState == "usn_unavailable(backlog-exceeded)",
                    $"USN backlog >40 pages must degrade to backlog-exceeded, got ok={ok} calls={overflowCalls} state={overflowQueue.UsnState}");
                var afterFallbackTask = await usnRepo.GetTaskAsync(chosenFile);
                Require(afterFallbackTask is null || afterFallbackTask.Kind != "Delete",
                    "Fallback reconcile after backlog-exceeded must preserve nested chosen root files");
            }

            // INDX-R2-BLK-01 (NM-BR-08): ReadFileHeadersByPathsAsync & queue Delete preserve stored binary casing
            await using var caseRepo = new SqliteIndexRepository(Db("idx-r2-blk01"));
            await caseRepo.InitializeAsync();
            string mixedCasePath = WinSight.Infrastructure.Extraction.FilePolicy.Normalize(Path.Combine(docs, "MixedCaseFile.txt"));
            System.IO.File.WriteAllText(mixedCasePath, "mixed case binary identity content");
            var mixedFi = new FileInfo(mixedCasePath);
            await caseRepo.UpsertFileAsync(new(0, mixedCasePath, "doc", mixedFi.Length,
                new DateTimeOffset(mixedFi.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0,
                "done", null, "{\"indexRoot\":\"" + docs.Replace("\\", "\\\\") + "\"}",
                [new("chunk", "none", 1, null, "mixed case segment", null)]));
            var storedMixed = await caseRepo.GetFileAsync(mixedCasePath);
            Require(storedMixed is not null, "Seeded MixedCaseFile.txt must exist in repository");

            string lowerCaseQueryPath = WinSight.Infrastructure.Extraction.FilePolicy.Normalize(Path.Combine(docs, "mixedcasefile.txt"));
            var headerLookup = await caseRepo.ReadFileHeadersByPathsAsync([lowerCaseQueryPath]);
            Require(headerLookup.Count == 1
                && headerLookup[0].Id == storedMixed!.Id
                && string.Equals(headerLookup[0].Path, storedMixed.Path, StringComparison.Ordinal),
                $"ReadFileHeadersByPathsAsync must match OrdinalIgnoreCase while returning stored binary path/id (count={headerLookup.Count})");

            var delNow = DateTimeOffset.UtcNow;
            await caseRepo.EnqueueIndexTaskAsync(new IndexTask(lowerCaseQueryPath, docs, false, "Delete", null, "Ready", 0, delNow, delNow));
            await using (var delQueue = new WinSight.Infrastructure.Indexing.IncrementalIndexQueue(caseRepo))
            {
                await delQueue.InitializeAsync();
                int delCount = await delQueue.ProcessReadyTasksAsync(10);
                Require(delCount == 1, $"Expected 1 Delete task processed, got {delCount}");
            }
            Require(await caseRepo.GetFileAsync(mixedCasePath) is null, "Case-differing Delete task must remove stored MixedCaseFile row");
            Require(await caseRepo.GetTaskAsync(lowerCaseQueryPath) is null, "Processed Delete task must be acked");
        });
    }

    private sealed class ScriptedUsnReader(
        string volumeSerial,
        WinSight.Infrastructure.Indexing.UsnJournalInfo journalInfo,
        Func<string, ulong, long, int, (bool Success, IReadOnlyList<WinSight.Infrastructure.Indexing.UsnChangeRecord> Records, long NextUsn, string? ErrorReason)> onRead,
        Func<ulong, string?> resolveByFrn) : WinSight.Infrastructure.Indexing.IUsnJournalReader
    {
        public bool IsNtfsVolume(string volumePath, out string serial) { serial = volumeSerial; return true; }
        public (bool Success, WinSight.Infrastructure.Indexing.UsnJournalInfo? Info, string? ErrorReason) QueryJournal(string volumePath) => (true, journalInfo, null);
        public (bool Success, IReadOnlyList<WinSight.Infrastructure.Indexing.UsnChangeRecord> Records, long NextUsn, string? ErrorReason) ReadIncrements(
            string volumePath, ulong journalId, long startUsn, int maxRecords = 5000) => onRead(volumePath, journalId, startUsn, maxRecords);
        public string? ResolvePath(string volumePath, ulong fileReferenceNumber, ulong parentFileReferenceNumber, string fileName) => resolveByFrn(fileReferenceNumber);
    }
}
