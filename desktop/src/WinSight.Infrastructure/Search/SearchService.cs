using System.Diagnostics;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Storage;

namespace WinSight.Infrastructure.Search;

/// <summary>FTS5 and modality-centered semantic search. Does not own the repository or embedding service.</summary>
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
        if (!Enum.IsDefined(request.Mode)) throw new ArgumentOutOfRangeException(nameof(request), "Unknown search mode.");
        if (request.Kind != null && request.Kind is not ("doc" or "pdf" or "image" or "screenshot" or "video" or "audio" or "code")) throw new ArgumentException("Unknown kind.", nameof(request));
        if (request.Root != null && (string.IsNullOrWhiteSpace(request.Root) || request.Root.Contains('\0'))) throw new ArgumentException("Invalid root.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        string text = request.Query.Trim();
        bool codePrefix = text.StartsWith("code:", StringComparison.OrdinalIgnoreCase);
        bool code = string.Equals(Path.GetFileName(repository.DatabasePath), "code.sqlite", StringComparison.OrdinalIgnoreCase);
        if (codePrefix)
        {
            if (!code) throw new InvalidOperationException("code: queries require the separate code.sqlite repository. No automatic index creation or routing is performed.");
            text = text[5..].Trim();
        }
        if (text.Length == 0) return new SearchResponse([], clock.Elapsed, false);
        string keywordQuery = code ? "code:" + text : text;
        bool wantSemantic = request.Mode != SearchMode.Keyword;
        SearchSnapshot snapshot = repository is SqliteIndexRepository sqlite
            ? await sqlite.ReadSearchSnapshotAsync(keywordQuery, request.Kind, request.Root, wantSemantic, request.Mode != SearchMode.Semantic, request.Limit, cancellationToken, authorize).ConfigureAwait(false)
            : await ReadGenericSnapshot(keywordQuery, request, cancellationToken).ConfigureAwait(false);
        var files = snapshot.Files.ToDictionary(f => f.Id);
        var words = snapshot.Words.ToDictionary(w => w.FileId);
        float[]? queryVector = null;
        string? notice = null;
        if (wantSemantic)
        {
            try
            {
                if (embeddings == null) throw new InvalidOperationException("No embedding service configured.");
                if (string.IsNullOrWhiteSpace(snapshot.Profile)) throw new InvalidDataException("Index has no profile_id; legacy vectors cannot be assumed compatible. Reindex separately.");
                var info = await embeddings.GetInfoAsync(cancellationToken).ConfigureAwait(false);
                ValidateProfile(info, snapshot.Profile);
                if (!snapshot.Files.Any(f => f.Segments.Any(s => s.Vector != null))) throw new InvalidOperationException("Index has no semantic vectors.");
                var encoded = await embeddings.EmbedAsync([new EmbeddingInput("text", (code ? "task: code retrieval | query: " : "task: search result | query: ") + text)], cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (encoded.Count != 1 || encoded[0].Vector == null || encoded[0].Error != null)
                    throw new InvalidDataException(encoded.Count == 1 ? "Query embedding failed: " + (encoded[0].Error ?? "missing vector") : "Query embedding returned an invalid result count.");
                queryVector = encoded[0].Vector;
                VectorContract.Validate(queryVector!);
                // A service can restart or switch providers during the awaited call; never mix profiles in flight.
                ValidateProfile(await embeddings.GetInfoAsync(cancellationToken).ConfigureAwait(false), snapshot.Profile);
            }
            catch (Exception error) when (request.Mode == SearchMode.Hybrid && error is InvalidDataException or IOException or InvalidOperationException or TimeoutException or NotSupportedException)
            {
                queryVector = null;
                notice = "Semantic search unavailable; keyword results only. " + error.GetType().Name + ": " + error.Message;
            }
        }
        var semantic = queryVector == null ? new Dictionary<long, SemanticEvidence>() : Score(snapshot.Files, queryVector, cancellationToken);
        if (semantic.Values.Any(s => s.SmallSample))
            notice = "Small-sample semantic fallback: fewer than 5 calibration segments in a modality; cosine >= 0.5.";
        var terms = SearchText.Terms(text);
        bool lookup = SearchText.IsLookup(terms) || (code && SearchText.Identifier(text) != null);
        double meaningWeight = lookup ? 0.5 : 1;
        double wordWeight = (code ? 1 : 2) * (lookup ? 2 : 1);
        var hints = SearchText.KindHints(text);
        var candidates = new HashSet<long>(semantic.Where(s => s.Value.Accepted).Select(s => s.Key));
        foreach (var word in words.Values) if (word.Coverage >= 0.5) candidates.Add(word.FileId);
        var ranked = new List<SearchHit>();
        foreach (long id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!files.TryGetValue(id, out var file) || !InScope(file, request) || authorize != null && !authorize(file.Path)) continue;
            semantic.TryGetValue(id, out var meaning);
            words.TryGetValue(id, out var word);
            double score = meaningWeight * Math.Max(meaning?.Rank ?? 0, 0) + wordWeight * Math.Pow(word?.Coverage ?? 0, 1.5) + (hints.Contains(file.Kind) ? 1 : 0);
            bool useMeaning = meaning != null && (meaning.Accepted || word == null);
            var segment = useMeaning ? meaning!.Segment : word?.Segment;
            if (segment == null) continue;
            string match = meaning?.Accepted == true ? word != null ? "hybrid" : "semantic" : "keyword";
            ranked.Add(new SearchHit(id, file.Path, file.Kind, score, match, useMeaning ? segment.Excerpt : word?.Snippet ?? segment.Excerpt,
                segment.Location, segment.LocationEnd, segment.Kind));
        }
        var hits = ranked.OrderByDescending(h => h.Score).ThenBy(h => h.FileId).Take(request.Limit).ToArray();
        return new SearchResponse(hits, clock.Elapsed, queryVector != null, notice);
    }

    private static void ValidateProfile(EmbeddingInfo info, string profile)
    {
        if (info.Dimension != VectorContract.Dimension || !string.Equals(info.ProfileId, profile, StringComparison.Ordinal))
            throw new InvalidDataException("Query model profile/dimension does not match index profile_id.");
        if (!info.Capabilities.Contains("text", StringComparer.Ordinal)) throw new NotSupportedException("Embedding service does not support text queries.");
    }

    private async Task<SearchSnapshot> ReadGenericSnapshot(string query, SearchRequest request, CancellationToken ct)
    {
        var files = await repository.ReadFilesAsync(ct).ConfigureAwait(false);
        var matches = request.Mode == SearchMode.Semantic ? [] : await repository.KeywordSearchAsync(query, 10000, ct).ConfigureAwait(false);
        var shares = new Dictionary<long, double>();
        var terms = SearchText.Terms(query.StartsWith("code:", StringComparison.Ordinal) ? query[5..] : query);
        foreach (var term in request.Mode == SearchMode.Semantic ? [] : terms)
        {
            var found = await repository.KeywordSearchAsync(term, 10000, ct).ConfigureAwait(false);
            foreach (var id in found.Select(m => m.FileId).Distinct()) shares[id] = shares.GetValueOrDefault(id) + 1.0 / terms.Length;
        }
        var byId = files.ToDictionary(f => f.Id);
        var words = matches.Where(m => byId.ContainsKey(m.FileId) && InScope(byId[m.FileId], request)).Select(m =>
        {
            var segment = byId[m.FileId].Segments.FirstOrDefault(s => s.Kind != "name") ?? new IndexSegment("name", "none", null, null, null);
            return new WordEvidence(m.FileId, m.Rank, shares.GetValueOrDefault(m.FileId), segment, segment.Excerpt);
        }).ToArray();
        return new SearchSnapshot(files, words, await repository.GetMetaAsync("profile_id", ct).ConfigureAwait(false));
    }

    private static bool InScope(IndexedFile file, SearchRequest request)
    {
        if (request.Kind != null && request.Kind != file.Kind) return false;
        if (request.Root == null) return true;
        string root = request.Root.Replace('\\', '/').TrimEnd('/');
        string path = file.Path.Replace('\\', '/');
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ScoredSegment(long FileId, IndexSegment Segment, double Cosine, bool SetsScale);
    private sealed record SemanticEvidence(double Rank, IndexSegment Segment, bool Accepted, bool SmallSample);

    private static Dictionary<long, SemanticEvidence> Score(IReadOnlyList<IndexedFile> files, float[] query, CancellationToken ct)
    {
        var rows = new List<ScoredSegment>();
        foreach (var file in files)
        {
            int pages = 0;
            if (file.Info != null)
            {
                using var info = JsonDocument.Parse(file.Info);
                if (info.RootElement.TryGetProperty("pages", out var p))
                {
                    if (p.ValueKind == JsonValueKind.Number) p.TryGetInt32(out pages);
                    else if (p.ValueKind == JsonValueKind.String) int.TryParse(p.GetString(), out pages);
                }
            }
            foreach (var segment in file.Segments)
            {
                ct.ThrowIfCancellationRequested();
                if (segment.Vector == null) continue;
                VectorContract.Validate(segment.Vector);
                if (segment.Modality is not ("text" or "image" or "audio")) throw new InvalidDataException("Vector has invalid modality.");
                double cosine = 0;
                for (int i = 0; i < query.Length; i++) cosine += (double)query[i] * segment.Vector[i];
                bool later = file.Kind == "pdf" ? segment.Kind != "chunk" && pages > 50 && segment.Location > 30 : segment.Kind == "chunk" && segment.Location > 40;
                rows.Add(new ScoredSegment(file.Id, segment, cosine, !later));
            }
        }
        var scale = rows.Where(r => r.SetsScale).ToArray();
        double all = scale.Length == 0 ? 0 : scale.Average(r => r.Cosine);
        var means = scale.GroupBy(r => r.Segment.Modality).ToDictionary(g => g.Key, g => g.Count() >= 20 ? g.Average(r => r.Cosine) : all);
        double Center(ScoredSegment row) => row.Cosine - means.GetValueOrDefault(row.Segment.Modality, all);
        double mean = scale.Length == 0 ? 0 : scale.Average(Center);
        double deviation = Math.Max(scale.Length == 0 ? 0 : Math.Sqrt(scale.Average(r => Math.Pow(Center(r) - mean, 2))), 0.02);
        var calibrationCounts = scale.GroupBy(r => r.Segment.Modality).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var best = new Dictionary<long, SemanticEvidence>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            bool small = calibrationCounts.GetValueOrDefault(row.Segment.Modality) < 5;
            double rank = small ? row.Cosine : (Center(row) - mean) / deviation;
            var evidence = new SemanticEvidence(rank, row.Segment, rank >= (small ? .5 : 2), small);
            if (!best.TryGetValue(row.FileId, out var previous) || evidence.Accepted && !previous.Accepted
                || evidence.Accepted == previous.Accepted && rank > previous.Rank) best[row.FileId] = evidence;
        }
        return best;
    }
}
