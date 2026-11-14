using System.Diagnostics;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Storage;

namespace WinSight.Infrastructure.Search;

/// <summary>Bounded independent source-unit recalls fused by reciprocal rank, not by averaging vectors.</summary>
public sealed class SearchService(IIndexRepository repository, IEmbeddingService? embeddings = null) : ISearchService
{
    private readonly IIndexRepository repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly Func<string, bool>? authorize;
    internal SearchService(IIndexRepository repository, IEmbeddingService? embeddings, Func<string, bool> authorize)
        : this(repository, embeddings) => this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        SqliteIndexRepository.ValidateQuery(request.Query, request.Limit);
        if (!Enum.IsDefined(request.Mode)) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Kind is not (null or "doc" or "pdf" or "image" or "screenshot" or "video" or "audio" or "code")) throw new ArgumentException("Unknown kind.");
        if (request.Root is not null && (string.IsNullOrWhiteSpace(request.Root) || request.Root.Contains('\0'))) throw new ArgumentException("Invalid root.");
        cancellationToken.ThrowIfCancellationRequested();
        SearchText.SetCurrentThreadQoS(highPerformance: true);
        var clock = Stopwatch.StartNew();
        bool code = string.Equals(Path.GetFileName(repository.DatabasePath), "code.sqlite", StringComparison.OrdinalIgnoreCase);
        var input = request.Query.Trim();
        if (input.StartsWith("code:", StringComparison.OrdinalIgnoreCase))
        {
            if (!code) throw new InvalidOperationException("code: requires the separate code.sqlite repository; no automatic routing/index creation.");
            input = input[5..].Trim();
        }
        var plan = new DocumentSearchQuery(input, request.Filters);
        if (plan.Text.Length == 0 && !plan.HasFilters) return new([], clock.Elapsed, false);
        bool wantSemantic = request.Mode != SearchMode.Keyword && plan.Text.Length != 0;
        var keywordQuery = code ? "code:" + plan.Text : plan.Text;
        var sourceCache = new Dictionary<string, IReadOnlyDictionary<long, SourceAnchor>>(StringComparer.Ordinal);
        bool MatchesSource(string path, string? info, long segmentId)
        {
            if (!plan.HasFilters) return true;
            if (!sourceCache.TryGetValue(path, out var anchors)) sourceCache[path] = anchors = SqliteIndexRepository.ReadAnchors(info);
            var anchor = anchors.GetValueOrDefault(segmentId);
            // Older schema5 metadata has no anchors. Exact excerpt checking still occurs after bounded hydration.
            return plan.Matches(path, info, anchor, checkLiterals: false);
        }
        bool needHeaderInfo = plan.Filters.Any(f => f.Name is "folder1" or "folder2" or "folder3") || (plan.Text.Length == 0 && plan.HasFilters);
        bool needsSourceAnchors = plan.Filters.Any(f => f.Name is not ("name" or "folder" or "folder1" or "folder2" or "folder3"));
        CompactSearchSnapshot? compactSnapshot = null;
        SearchSnapshot? genericSnapshot = null;
        IReadOnlyDictionary<long, IndexedFile> files;
        IReadOnlyList<WordEvidence> words;
        string? profile;
        if (repository is SqliteIndexRepository sqlite)
        {
            compactSnapshot = await sqlite.ReadCompactSearchSnapshotAsync(keywordQuery, request.Kind, request.Root, wantSemantic,
                request.Mode != SearchMode.Semantic && plan.Text.Length != 0, request.Limit, cancellationToken,
                authorize, plan.HasFilters ? MatchesSource : null, allHeaders: true, includeHeaderInfo: needHeaderInfo, needsSourceAnchors: needsSourceAnchors).ConfigureAwait(false);
            var scoped = new Dictionary<long, IndexedFile>(compactSnapshot.Headers.Count);
            foreach (var f in compactSnapshot.Headers.Values)
            {
                if (InScope(f, request) && (authorize is null || authorize(f.Path)))
                    scoped[f.Id] = f;
            }
            files = scoped;
            words = compactSnapshot.Words;
            profile = compactSnapshot.Profile;
        }
        else
        {
            genericSnapshot = await ReadGenericSnapshot(keywordQuery, request, cancellationToken).ConfigureAwait(false);
            files = genericSnapshot.Files.Where(f => InScope(f, request) && (authorize is null || authorize(f.Path))).ToDictionary(f => f.Id);
            words = genericSnapshot.Words;
            profile = genericSnapshot.Profile;
        }

        float[]? queryVector = null;
        string? notice = null;
        if (wantSemantic)
        {
            try
            {
                if (embeddings is null) throw new InvalidOperationException("No embedding service configured.");
                if (string.IsNullOrWhiteSpace(profile)) throw new InvalidDataException("Index has no profile_id; reindex separately.");
                ValidateProfile(await embeddings.GetInfoAsync(cancellationToken).ConfigureAwait(false), profile);
                bool hasVectors = compactSnapshot is not null
                    ? HasVectorsInScope(compactSnapshot.Vectors, files)
                    : files.Values.Any(f => f.Segments.Any(s => s.Vector is not null));
                if (!hasVectors) throw new InvalidOperationException("Index has no semantic vectors.");
                var encoded = await embeddings.EmbedInteractiveAsync([new("text", (code ? "task: code retrieval | query: " : "task: search result | query: ") + plan.Text)], cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (encoded.Count != 1 || encoded[0].Vector is null || encoded[0].Error is not null)
                    throw new InvalidDataException(encoded.Count == 1 ? "Query embedding failed: " + (encoded[0].Error ?? "missing vector") : "Query embedding result count is invalid.");
                queryVector = encoded[0].Vector;
                VectorContract.Validate(queryVector!);
                ValidateProfile(await embeddings.GetInfoAsync(cancellationToken).ConfigureAwait(false), profile);
            }
            catch (Exception error) when (request.Mode == SearchMode.Hybrid && error is InvalidDataException or IOException or InvalidOperationException or TimeoutException or NotSupportedException)
            {
                queryVector = null;
                notice = $"Semantic search unavailable; keyword results only. {error.GetType().Name}: {error.Message}";
            }
        }
        int channelLimit = Math.Clamp(request.Limit * 4, 100, 4000);
        var channels = new List<(string Name, double Weight, IReadOnlyList<Candidate> Rows)>();
        void Channel(string name, double weight, IEnumerable<Candidate> ordered)
        {
            var seen = new HashSet<(long, string)>();
            var rows = ordered.Where(c => seen.Add((c.File.Id, Unit(c.Segment)))).Take(channelLimit).ToArray();
            if (rows.Length != 0) channels.Add((name, weight, rows));
        }
        if (queryVector is not null)
        {
            if (compactSnapshot?.Vectors is { } compactVectors && repository is SqliteIndexRepository sqliteRepo)
            {
                IReadOnlyDictionary<long, IndexedFile> semanticFiles = files;
                HashSet<long>? allowedSegmentIds = null;
                if (plan.HasFilters && !plan.HasSegmentLevelFilters)
                {
                    var filtered = new Dictionary<long, IndexedFile>();
                    foreach (var kv in files)
                        if (plan.Matches(kv.Value.Path, kv.Value.Info, null))
                            filtered[kv.Key] = kv.Value;
                    semanticFiles = filtered;
                }
                else if (plan.HasSegmentLevelFilters)
                {
                    allowedSegmentIds = await sqliteRepo.FilterVectorSegmentsAsync(files, plan, cancellationToken).ConfigureAwait(false);
                }

                var compactGroups = SemanticCompact(compactVectors, semanticFiles, queryVector, channelLimit, cancellationToken, allowedSegmentIds);
                var winnerIds = compactGroups.SelectMany(g => g.Winners.Select(w => w.SegmentId)).Distinct().ToArray();
                var hydrated = await sqliteRepo.HydrateSemanticHitsAsync(winnerIds, cancellationToken).ConfigureAwait(false);
                foreach (var group in compactGroups)
                {
                    if (group.SmallSample) notice = Append(notice, "Small-sample semantic fallback: fewer than 5 calibration source units in a channel; cosine >= 0.5.");
                    Channel("semantic:" + group.Channel, 1, group.Winners
                        .Where(w => hydrated.ContainsKey(w.SegmentId))
                        .Select(w => { var h = hydrated[w.SegmentId]; return new Candidate(h.File, h.Segment); }));
                }
            }
            else
            {
                // Vectors read from SQLite were validated once when decoded; only foreign repositories need a per-query check.
                foreach (var group in Semantic(files.Values, queryVector, plan, cancellationToken, validateVectors: repository is not SqliteIndexRepository).GroupBy(s => s.Channel))
                {
                    if (group.Any(s => s.SmallSample)) notice = Append(notice, "Small-sample semantic fallback: fewer than 5 calibration source units in a channel; cosine >= 0.5.");
                    Channel("semantic:" + group.Key, 1, group.Where(s => s.Accepted).OrderByDescending(s => s.Rank)
                        .ThenBy(s => s.File.Id).Select(s => new Candidate(s.File, s.Segment)));
                }
            }
        }
        if (request.Mode != SearchMode.Semantic || plan.Text.Length == 0)
        {
            Channel("keyword", 2, words.Where(w => files.ContainsKey(w.FileId) && w.Coverage >= 0.5)
                .OrderByDescending(w => w.Coverage).ThenBy(w => w.Rank).ThenBy(w => w.FileId)
                .Where(w => plan.Matches(files[w.FileId].Path, files[w.FileId].Info, w.Segment.Source, w.Segment.Excerpt, checkLiterals: !w.ExactLiteralsVerified))
                .Select(w => new Candidate(files[w.FileId], w.Segment)));
            if (plan.Text.Length == 0)
            {
                var fieldRows = new List<Candidate>();
                var seenFields = new HashSet<(long, string)>();
                foreach (var file in files.Values.OrderBy(f => f.Id))
                {
                    if (fieldRows.Count >= channelLimit) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    var anchors = SqliteIndexRepository.ReadAnchors(file.Info);
                    var sources = anchors.Count != 0 ? anchors.Values : file.Segments.Where(s => s.Source is not null).Select(s => s.Source!);
                    foreach (var source in sources)
                    {
                        if (fieldRows.Count >= channelLimit) break;
                        if (!plan.Matches(file.Path, file.Info, source)) continue;
                        var segment = file.Segments.FirstOrDefault(s => s.Source?.Location == source.Location && s.Source.Channel == source.Channel)
                            ?? new IndexSegment("chunk", "none", source.Location.Page ?? source.Location.Paragraph, null, null, Source: source);
                        if (seenFields.Add((file.Id, Unit(segment)))) fieldRows.Add(new(file, segment));
                    }
                    if (anchors.Count == 0 && plan.Matches(file.Path, file.Info, null)) fieldRows.Add(new(file, new("name", "none", null, null, null)));
                }
                Channel("fields", 2, fieldRows);
            }
            else
            {
                var terms = SearchText.Terms(plan.Text);
                var normPlanText = string.IsNullOrWhiteSpace(plan.Text) ? "" : SearchText.Normalize(plan.Text);
                bool ContainsTerms(string value)
                {
                    if (terms.Length == 0 || string.IsNullOrEmpty(value)) return false;
                    var norm = SearchText.Normalize(value);
                    return terms.All(t => norm.Contains(t, StringComparison.OrdinalIgnoreCase))
                        || (normPlanText.Length > 0 && norm.Contains(normPlanText, StringComparison.OrdinalIgnoreCase));
                }
                var folderMatchCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                bool FolderMatches(string path)
                {
                    var dir = Path.GetDirectoryName(path) ?? "";
                    if (folderMatchCache.TryGetValue(dir, out var ok)) return ok;
                    ok = ContainsTerms(dir) && plan.FolderLiteralsMatch(path);
                    folderMatchCache[dir] = ok;
                    return ok;
                }
                var eligible = files.Values.OrderBy(f => f.Id).Where(f => plan.Matches(f.Path, f.Info, null)).ToArray();
                Channel("filename", 1.5, eligible.Where(f => ContainsTerms(Path.GetFileName(f.Path)) && plan.NameLiteralsMatch(f.Path)).Select(f => new Candidate(f, new("name", "none", null, null, Path.GetFileName(f.Path)))));
                Channel("folder", 0.7, eligible.Where(f => FolderMatches(f.Path)).Select(f => new Candidate(f, new("name", "none", null, null, null))));
            }
        }
        var fused = new Dictionary<(long File, string Unit), Fused>();
        double maximum = channels.Sum(c => c.Weight / 61);
        foreach (var channel in channels)
            for (int i = 0; i < channel.Rows.Count; i++)
            {
                var row = channel.Rows[i];
                var key = (row.File.Id, Unit(row.Segment));
                if (!fused.TryGetValue(key, out var hit)) fused.Add(key, hit = new(row));
                hit.Score += channel.Weight / (60 + i + 1);
                hit.Channels.Add(channel.Name);
                if (hit.Row.Segment.Kind == "name" && row.Segment.Kind != "name") hit.Row = row;
            }
        double topScore = fused.Values.Select(f => f.Score).DefaultIfEmpty(0).Max();
        // Dynamic relevance filter: when MinScore > 0, filter strictly by relative relevance: (f.Score / topScore) >= MinScore.
        var eligibleCandidates = fused.Values
            .Where(f => topScore == 0 || request.MinScore <= 0 || (f.Score / topScore) >= request.MinScore)
            .OrderByDescending(f => f.Score).ThenBy(f => f.Row.File.Id).ThenBy(f => Unit(f.Row.Segment), StringComparer.Ordinal);
        var hits = eligibleCandidates
            .Take(request.Limit).Select(f =>
            {
                var file = f.Row.File; var segment = f.Row.Segment;
                string match = f.Channels.Any(c => c.StartsWith("semantic:", StringComparison.Ordinal)) ? f.Channels.Count > 1 ? "hybrid" : "semantic" : "keyword";
                return new SearchHit(file.Id, file.Path, file.Kind, maximum == 0 ? 0 : f.Score / maximum, match,
                    segment.Excerpt, segment.Location, segment.LocationEnd, segment.Kind, segment.Source, f.Channels.ToArray(), file.Size, file.ModifiedTime);
            }).ToArray();
        if (request.Limit > channelLimit) notice = Append(notice, $"Recall capped at{channelLimit} source units per channel before fusion.");
        // The segmented lexical channel depends on a native library; when it is missing the response says so
        // instead of silently returning weaker results.
        var degraded = !SearchText.SegmentationAvailable;
        return new(hits, clock.Elapsed, queryVector is not null,
            degraded ? (notice is null ? "Chinese segmentation is unavailable; lexical results use the original text index only." : notice + " Chinese segmentation is unavailable; lexical results use the original text index only.") : notice,
            degraded);
    }

    private sealed record Candidate(IndexedFile File, IndexSegment Segment);
    private sealed class Fused(Candidate row)
    {
        internal Candidate Row = row;
        internal double Score;
        internal List<string> Channels { get; } = [];
    }
    private readonly record struct SemanticRow(IndexedFile File, IndexSegment Segment, string Channel, double Cosine,
        double Rank = 0, bool Accepted = false, bool SmallSample = false);
    private static string Unit(IndexSegment segment) => SearchText.SourceUnit(segment);
    private static string Append(string? notice, string message) => notice is null ? message : notice.Contains(message, StringComparison.Ordinal) ? notice : notice + " " + message;
    private static void ValidateProfile(EmbeddingInfo info, string profile)
    {
        if (info.Dimension != VectorContract.Dimension || !FilePolicy.IsCompatibleProfile(info.ProfileId, profile))
            throw new InvalidDataException("Query model profile/dimension does not match index profile_id.");
        if (!info.Capabilities.Contains("text", StringComparer.Ordinal)) throw new NotSupportedException("Embedding service does not support text queries.");
    }
    private static bool InScope(IndexedFile file, SearchRequest request)
    {
        if (request.Kind is not null && request.Kind != file.Kind) return false;
        if (request.Root is null) return true;
        var path = file.Path.Replace('\\', '/'); var root = request.Root.Replace('\\', '/').TrimEnd('/');
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }
    private async Task<SearchSnapshot> ReadGenericSnapshot(string query, SearchRequest request, CancellationToken ct)
    {
        var files = await repository.ReadFilesAsync(ct).ConfigureAwait(false);
        var matches = request.Mode == SearchMode.Semantic || string.IsNullOrWhiteSpace(query) ? [] : await repository.KeywordSearchAsync(query, 10000, ct).ConfigureAwait(false);
        var shares = new Dictionary<long, double>();
        var terms = SearchText.Terms(query.StartsWith("code:", StringComparison.Ordinal) ? query[5..] : query);
        foreach (var term in request.Mode == SearchMode.Semantic ? [] : terms)
            foreach (var id in (await repository.KeywordSearchAsync(term, 10000, ct).ConfigureAwait(false)).Select(m => m.FileId).Distinct())
                shares[id] = shares.GetValueOrDefault(id) + 1.0 / terms.Length;
        var byId = files.ToDictionary(f => f.Id);
        var words = matches.Where(m => byId.ContainsKey(m.FileId) && InScope(byId[m.FileId], request)).Select(m =>
        {
            var segment = byId[m.FileId].Segments.FirstOrDefault(s => s.Kind != "name") ?? new IndexSegment("name", "none", null, null, null);
            return new WordEvidence(m.FileId, m.Rank, shares.GetValueOrDefault(m.FileId), segment, segment.Excerpt);
        }).ToArray();
        return new(files, words, await repository.GetMetaAsync("profile_id", ct).ConfigureAwait(false));
    }
    // A channel is a population of source units (text units, page visuals, OCR units ...). With fewer than this many
    // units no statistic is meaningful, so the legacy absolute cosine floor applies.
    internal const int MinCalibratedUnits = 5;
    internal const double SmallSampleCosineFloor = 0.5;
    // Expected number of false positives per channel per query when the query matches nothing in the corpus.
    // Controlling the family-wise error instead of a fixed top percentile keeps the noise level independent of
    // corpus size (a fixed z>=2 admits ~2% of every channel, i.e. thousands of irrelevant units in a large index).
    internal const double FalsePositiveBudget = 1.0;
    internal const double MinimumSignificance = 1.0;

    /// <summary>z that an outlier must reach so that at most <see cref="FalsePositiveBudget"/> units pass by chance.</summary>
    internal static double SignificanceThreshold(int populationSize) =>
        populationSize < MinCalibratedUnits ? double.PositiveInfinity
            : Math.Max(MinimumSignificance, NormalQuantile(1 - Math.Min(0.5, FalsePositiveBudget / populationSize)));

    /// <summary>Standard normal quantile (Acklam's rational approximation, relative error about 1e-9).</summary>
    internal static double NormalQuantile(double p)
    {
        if (!(p > 0 && p < 1)) throw new ArgumentOutOfRangeException(nameof(p));
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        const double low = 0.02425;
        if (p < low)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p <= 1 - low)
        {
            double q = p - 0.5, r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }
        double u = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c[0] * u + c[1]) * u + c[2]) * u + c[3]) * u + c[4]) * u + c[5]) / ((((d[0] * u + d[1]) * u + d[2]) * u + d[3]) * u + 1);
    }

    private static IReadOnlyList<SemanticRow> Semantic(IEnumerable<IndexedFile> files, float[] query, DocumentSearchQuery plan, CancellationToken ct, bool validateVectors = true)
    {
        // Single-pass: directly aggregate the best vector per (file, unit, channel) to reduce memory allocations
        var best = new Dictionary<(long, string, string), SemanticRow>();
        foreach (var file in files)
            foreach (var segment in file.Segments)
            {
                ct.ThrowIfCancellationRequested();
                if (segment.Vector is not { } vector || !plan.Matches(file.Path, file.Info, segment.Source, segment.Excerpt)) continue;
                if (validateVectors) VectorContract.Validate(vector);
                double cosine = VectorMath.Dot(vector, query);
                var channel = segment.Source?.Channel ?? (segment.Modality == "image" ? "page-visual" : segment.Modality);
                var key = (file.Id, SearchText.SourceUnit(segment), channel);
                if (!best.TryGetValue(key, out var old) || cosine > old.Cosine)
                    best[key] = new(file, segment, channel, cosine);
            }
        var result = new List<SemanticRow>(best.Count);
        foreach (var channel in best.Values.GroupBy(r => r.Channel))
        {
            var group = channel.ToArray();
            bool small = group.Length < MinCalibratedUnits;
            double mean = 0;
            foreach (var row in group) mean += row.Cosine;
            mean /= group.Length;
            double variance = 0;
            foreach (var row in group) variance += (row.Cosine - mean) * (row.Cosine - mean);
            double deviation = Math.Sqrt(variance / group.Length);
            double threshold = SignificanceThreshold(group.Length);
            foreach (var row in group)
            {
                double rank = small ? row.Cosine : deviation > 1e-12 ? (row.Cosine - mean) / deviation : 0;
                result.Add(row with { Rank = rank, Accepted = small ? row.Cosine >= SmallSampleCosineFloor : rank >= threshold, SmallSample = small });
            }
        }
        return result;
    }

    private readonly record struct CompactWinner(long SegmentId, long FileId, double Rank);
    private sealed record CompactChannelGroup(string Channel, bool SmallSample, IReadOnlyList<CompactWinner> Winners);

    private static bool HasVectorsInScope(CompactVectorIndex? index, IReadOnlyDictionary<long, IndexedFile> files)
    {
        if (index is null || index.Count == 0 || files.Count == 0) return false;
        for (int c = 0; c < index.ChunkCount; c++)
        {
            var (_, metas, len) = index.GetChunk(c);
            for (int i = 0; i < len; i++)
                if (metas[i].FileId >= 0 && files.ContainsKey(metas[i].FileId))
                    return true;
        }
        return false;
    }

    private static List<CompactChannelGroup> SemanticCompact(CompactVectorIndex index, IReadOnlyDictionary<long, IndexedFile> files, float[] query, int channelLimit, CancellationToken ct, HashSet<long>? allowedSegmentIds = null)
    {
        var best = new Dictionary<(long FileId, ulong UnitHash, byte ChannelId), (long SegmentId, double Cosine)>();
        for (int c = 0; c < index.ChunkCount; c++)
        {
            var (vecs, metas, len) = index.GetChunk(c);
            for (int i = 0; i < len; i++)
            {
                if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
                ref readonly var m = ref metas[i];
                if (m.FileId < 0 || !files.ContainsKey(m.FileId)) continue;
                if (allowedSegmentIds is not null && !allowedSegmentIds.Contains(m.SegmentId)) continue;
                double cosine = CompactVectorIndex.DotInt8(vecs.AsSpan(i * CompactVectorIndex.Dimension, CompactVectorIndex.Dimension), m.Scale, query);
                var key = (m.FileId, m.UnitHash, m.ChannelId);
                if (!best.TryGetValue(key, out var old) || cosine > old.Cosine)
                    best[key] = (m.SegmentId, cosine);
            }
        }

        var byChannel = new Dictionary<byte, List<(long SegmentId, long FileId, double Cosine)>>();
        foreach (var kv in best)
        {
            if (!byChannel.TryGetValue(kv.Key.ChannelId, out var list))
                byChannel[kv.Key.ChannelId] = list = [];
            list.Add((kv.Value.SegmentId, kv.Key.FileId, kv.Value.Cosine));
        }

        var groups = new List<CompactChannelGroup>(byChannel.Count);
        foreach (var kv in byChannel)
        {
            var list = kv.Value;
            bool small = list.Count < MinCalibratedUnits;
            double mean = 0;
            foreach (var item in list) mean += item.Cosine;
            mean /= list.Count;
            double variance = 0;
            foreach (var item in list) variance += (item.Cosine - mean) * (item.Cosine - mean);
            double deviation = Math.Sqrt(variance / list.Count);
            double threshold = SignificanceThreshold(list.Count);

            var winners = new List<CompactWinner>();
            foreach (var item in list)
            {
                double rank = small ? item.Cosine : deviation > 1e-12 ? (item.Cosine - mean) / deviation : 0;
                bool accepted = small ? item.Cosine >= SmallSampleCosineFloor : rank >= threshold;
                if (accepted) winners.Add(new CompactWinner(item.SegmentId, item.FileId, rank));
            }
            if (winners.Count == 0) continue;
            winners.Sort((a, b) =>
            {
                int cmp = b.Rank.CompareTo(a.Rank);
                return cmp != 0 ? cmp : a.FileId.CompareTo(b.FileId);
            });
            if (winners.Count > channelLimit) winners.RemoveRange(channelLimit, winners.Count - channelLimit);
            string channelName = kv.Key < index.Channels.Count ? index.Channels[kv.Key] : "text";
            groups.Add(new CompactChannelGroup(channelName, small, winners));
        }
        return groups;
    }
}

/// <summary>SIMD dot product over embedding vectors; the query path scans every candidate vector.</summary>
internal static class VectorMath
{
    internal static double Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length) throw new ArgumentException("Vector lengths differ.");
        int i = 0;
        if (System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated && left.Length >= 32)
        {
            var sum0 = System.Runtime.Intrinsics.Vector512<float>.Zero;
            var sum1 = System.Runtime.Intrinsics.Vector512<float>.Zero;
            for (; i <= left.Length - 32; i += 32)
            {
                var l0 = System.Runtime.Intrinsics.Vector512.Create(left.Slice(i, 16));
                var r0 = System.Runtime.Intrinsics.Vector512.Create(right.Slice(i, 16));
                var l1 = System.Runtime.Intrinsics.Vector512.Create(left.Slice(i + 16, 16));
                var r1 = System.Runtime.Intrinsics.Vector512.Create(right.Slice(i + 16, 16));
                sum0 += l0 * r0;
                sum1 += l1 * r1;
            }
            double total512 = System.Runtime.Intrinsics.Vector512.Sum(sum0 + sum1);
            for (; i < left.Length; i++) total512 += (double)left[i] * right[i];
            return total512;
        }
        int width = System.Numerics.Vector<float>.Count;
        var sum = System.Numerics.Vector<float>.Zero;
        for (; i <= left.Length - width; i += width)
            sum += new System.Numerics.Vector<float>(left.Slice(i, width)) * new System.Numerics.Vector<float>(right.Slice(i, width));
        double total = System.Numerics.Vector.Sum(sum);
        for (; i < left.Length; i++) total += (double)left[i] * right[i];
        return total;
    }

    /// <summary>Dot product between a normalized float query vector and an int8 scalar-quantized vector blob.</summary>
    internal static double DotInt8(ReadOnlySpan<float> query, ReadOnlySpan<byte> quantized)
    {
        if (query.Length != VectorContract.Dimension || quantized.Length != VectorContract.Int8ByteCount)
            throw new ArgumentException("Vector dimensions mismatch for int8 dot product.");
        float scale = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(quantized[..4]);
        var qBytes = quantized[4..];
        double sum = 0;
        for (int i = 0; i < VectorContract.Dimension; i++)
        {
            sum += (double)query[i] * (sbyte)qBytes[i];
        }
        return sum * scale;
    }
}
