using System.Diagnostics;
using WinSight.Core;
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
        SearchSnapshot snapshot = repository is SqliteIndexRepository sqlite
            ? await sqlite.ReadSearchSnapshotAsync(keywordQuery, request.Kind, request.Root, wantSemantic,
                request.Mode != SearchMode.Semantic && plan.Text.Length != 0, request.Limit, cancellationToken,
                authorize, MatchesSource, allHeaders: true).ConfigureAwait(false)
            : await ReadGenericSnapshot(keywordQuery, request, cancellationToken).ConfigureAwait(false);
        var files = snapshot.Files.Where(f => InScope(f, request) && (authorize is null || authorize(f.Path))).ToDictionary(f => f.Id);
        float[]? queryVector = null;
        string? notice = null;
        if (wantSemantic)
        {
            try
            {
                if (embeddings is null) throw new InvalidOperationException("No embedding service configured.");
                if (string.IsNullOrWhiteSpace(snapshot.Profile)) throw new InvalidDataException("Index has no profile_id; reindex separately.");
                ValidateProfile(await embeddings.GetInfoAsync(cancellationToken).ConfigureAwait(false), snapshot.Profile);
                if (!files.Values.Any(f => f.Segments.Any(s => s.Vector is not null))) throw new InvalidOperationException("Index has no semantic vectors.");
                var encoded = await embeddings.EmbedAsync([new("text", (code ? "task: code retrieval | query: " : "task: search result | query: ") + plan.Text)], cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (encoded.Count != 1 || encoded[0].Vector is null || encoded[0].Error is not null)
                    throw new InvalidDataException(encoded.Count == 1 ? "Query embedding failed: " + (encoded[0].Error ?? "missing vector") : "Query embedding result count is invalid.");
                queryVector = encoded[0].Vector;
                VectorContract.Validate(queryVector!);
                ValidateProfile(await embeddings.GetInfoAsync(cancellationToken).ConfigureAwait(false), snapshot.Profile);
            }
            catch (Exception error) when (request.Mode == SearchMode.Hybrid && error is InvalidDataException or IOException or InvalidOperationException or TimeoutException or NotSupportedException)
            {
                queryVector = null;
                notice = $"Semantic search unavailable; keyword results only. {error.GetType().Name}: {error.Message}";
            }
        }
        int channelLimit = Math.Clamp(request.Limit * 4, 100, 400);
        var channels = new List<(string Name, double Weight, IReadOnlyList<Candidate> Rows)>();
        void Channel(string name, double weight, IEnumerable<Candidate> ordered)
        {
            var seen = new HashSet<(long, string)>();
            var rows = ordered.Where(c => seen.Add((c.File.Id, Unit(c.Segment)))).Take(channelLimit).ToArray();
            if (rows.Length != 0) channels.Add((name, weight, rows));
        }
        if (queryVector is not null)
        {
            foreach (var group in Semantic(files.Values, queryVector, plan, cancellationToken).GroupBy(s => s.Channel))
            {
                if (group.Any(s => s.SmallSample)) notice = Append(notice, "Small-sample semantic fallback: fewer than 5 calibration source units in a channel; cosine >= 0.5.");
                Channel("semantic:" + group.Key, 1, group.Where(s => s.Accepted).OrderByDescending(s => s.Rank)
                    .ThenBy(s => s.File.Id).Select(s => new Candidate(s.File, s.Segment)));
            }
        }
        if (request.Mode != SearchMode.Semantic || plan.Text.Length == 0)
        {
            Channel("keyword", 2, snapshot.Words.Where(w => files.ContainsKey(w.FileId) && w.Coverage >= 0.5)
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
                bool ContainsTerms(string value) => terms.Length != 0 && terms.All(t => SearchText.Normalize(value).Contains(t, StringComparison.OrdinalIgnoreCase));
                var eligible = files.Values.OrderBy(f => f.Id).Where(f => plan.Matches(f.Path, f.Info, null));
                Channel("filename", 1.5, eligible.Where(f => ContainsTerms(Path.GetFileName(f.Path)) && plan.NameLiteralsMatch(f.Path)).Select(f => new Candidate(f, new("name", "none", null, null, Path.GetFileName(f.Path)))));
                Channel("folder", 0.7, eligible.Where(f => ContainsTerms(Path.GetDirectoryName(f.Path) ?? "") && plan.FolderLiteralsMatch(f.Path)).Select(f => new Candidate(f, new("name", "none", null, null, null))));
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
        var hits = fused.Values.OrderByDescending(f => f.Score).ThenBy(f => f.Row.File.Id).ThenBy(f => Unit(f.Row.Segment), StringComparer.Ordinal)
            .Take(request.Limit).Select(f =>
            {
                var file = f.Row.File; var segment = f.Row.Segment;
                string match = f.Channels.Any(c => c.StartsWith("semantic:", StringComparison.Ordinal)) ? f.Channels.Count > 1 ? "hybrid" : "semantic" : "keyword";
                return new SearchHit(file.Id, file.Path, file.Kind, maximum == 0 ? 0 : f.Score / maximum, match,
                    segment.Excerpt, segment.Location, segment.LocationEnd, segment.Kind, segment.Source, f.Channels.ToArray());
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
    private sealed record SemanticRow(IndexedFile File, IndexSegment Segment, string Channel, double Cosine,
        double Rank = 0, bool Accepted = false, bool SmallSample = false);
    private static string Unit(IndexSegment segment) => SearchText.SourceUnit(segment);
    private static string Append(string? notice, string message) => notice is null ? message : notice.Contains(message, StringComparison.Ordinal) ? notice : notice + " " + message;
    private static void ValidateProfile(EmbeddingInfo info, string profile)
    {
        if (info.Dimension != VectorContract.Dimension || info.ProfileId != profile) throw new InvalidDataException("Query model profile/dimension does not match index profile_id.");
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
    private static IReadOnlyList<SemanticRow> Semantic(IEnumerable<IndexedFile> files, float[] query, DocumentSearchQuery plan, CancellationToken ct)
    {
        var rows = new List<SemanticRow>();
        foreach (var file in files)
            foreach (var segment in file.Segments)
            {
                ct.ThrowIfCancellationRequested();
                if (segment.Vector is not { } vector || !plan.Matches(file.Path, file.Info, segment.Source, segment.Excerpt)) continue;
                VectorContract.Validate(vector);
                double cosine = 0;
                for (int i = 0; i < vector.Length; i++) cosine += (double)vector[i] * query[i];
                var channel = segment.Source?.Channel ?? (segment.Modality == "image" ? "page-visual" : segment.Modality);
                rows.Add(new(file, segment, channel, cosine));
            }
        rows = rows.GroupBy(r => (r.File.Id, Unit(r.Segment), r.Channel)).Select(g => g.MaxBy(r => r.Cosine)!).ToList();
        var result = new List<SemanticRow>();
        foreach (var channel in rows.GroupBy(r => r.Channel))
        {
            var group = channel.ToArray();
            bool small = group.Length < 5;
            double mean = group.Average(r => r.Cosine);
            double deviation = Math.Sqrt(group.Average(r => Math.Pow(r.Cosine - mean, 2)));
            foreach (var row in group)
            {
                double rank = small ? row.Cosine : deviation > 1e-12 ? (row.Cosine - mean) / deviation : 0;
                result.Add(row with { Rank = rank, Accepted = small ? row.Cosine >= 0.5 : rank >= 2, SmallSample = small });
            }
        }
        return result;
    }
}
