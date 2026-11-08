using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using WinSight.Core;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;

namespace WinSight.Bridge;

public sealed class ReadOnlyIndexReader : IDisposable
{
    private readonly string databasePath;
    private readonly AllowedFileRegistry registry;
    private readonly McpBridgeStateMachine stateMachine;

    public string DatabasePath => databasePath;

    public ReadOnlyIndexReader(string databasePath, AllowedFileRegistry registry, McpBridgeStateMachine stateMachine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
    }

    private SqliteConnection OpenReadOnlyConnection()
    {
        if (!File.Exists(databasePath))
        {
            throw new McpException(McpErrors.IndexIncompatible, $"Index database '{Path.GetFileName(databasePath)}' was not found.");
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            connection.CreateCollation("WINSIGHT_PATH", StringComparer.OrdinalIgnoreCase.Compare);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA query_only=ON; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-32768; PRAGMA mmap_size=268435456;";
            cmd.ExecuteNonQuery();
            return connection;
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            if (ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */)
            {
                throw new McpException(McpErrors.Busy, $"Index database is busy or locked: {ex.Message}", ex);
            }
            throw new McpException(McpErrors.IndexIncompatible, $"Index database error: {ex.Message}", ex);
        }
    }

    private static string[] SegmentedTokens(string text)
    {
        if (!NativeSegmenter.IsAvailable) return [];
        return NativeSegmenter.SegmentQuery(text)
            .Where(t => !string.IsNullOrWhiteSpace(t) && t != "AND" && t != "OR" && t != "NOT")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
            .ToArray();
    }

    /// <summary>Segmented FTS expression for the query; empty when the segmenter is unavailable.</summary>
    private static string SegmentedPattern(string text) => string.Join(" OR ", SegmentedTokens(text));

    private static string SegmentedAndPattern(string text)
    {
        var tokens = SegmentedTokens(text);
        return tokens.Length > 1 ? string.Join(" AND ", tokens) : "";
    }

    public Task<IndexStatusResult> GetIndexStatusAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var connection = OpenReadOnlyConnection();

            string? nativeFormat = null;
            string? generationId = null;
            string? lexiconSchema = null;

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT key, value FROM meta WHERE key IN ('native_format', 'generation_id', 'lexicon_schema')";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader.GetString(0);
                    string val = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (key == "native_format") nativeFormat = val;
                    else if (key == "generation_id") generationId = val;
                    else if (key == "lexicon_schema") lexiconSchema = val;
                }
            }

            int fileCount = 0;
            var statusDistribution = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT state, count(*) FROM files GROUP BY state";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string st = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                    int count = reader.GetInt32(1);
                    statusDistribution[st] = count;
                    fileCount += count;
                }
            }

            int chunkCount = 0;
            if (fileCount > 500)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM fts_docsize";
                chunkCount = Convert.ToInt32(cmd.ExecuteScalar());
            }
            if (chunkCount == 0 && fileCount > 0)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM segments";
                chunkCount = Convert.ToInt32(cmd.ExecuteScalar());
            }

            var modalities = new List<string>();
            if (fileCount > 500)
            {
                var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT kind FROM files WHERE state IN ('done', 'partial')";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0)) kinds.Add(reader.GetString(0));
                }
                if (kinds.Overlaps(["image", "screenshot"])) modalities.Add("image");
                if (chunkCount > 0) modalities.Add("none");
                if (kinds.Overlaps(["doc", "pdf", "code"])) modalities.Add("text");
                if (kinds.Contains("audio")) modalities.Add("audio");
                modalities.Sort(StringComparer.Ordinal);
            }
            else
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT modality FROM segments WHERE modality IS NOT NULL AND modality != '' ORDER BY modality";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    modalities.Add(reader.GetString(0));
                }
            }

            int monitoredDirectoryCount = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT count(DISTINCT substr(path, 1, instr(path, '\\'))) FROM files WHERE instr(path, '\\') > 0";
                var res = cmd.ExecuteScalar();
                monitoredDirectoryCount = res != null && res != DBNull.Value ? Convert.ToInt32(res) : 1;
                if (monitoredDirectoryCount == 0 && fileCount > 0) monitoredDirectoryCount = 1;
            }

            bool lexicalDegraded = !NativeSegmenter.IsAvailable || lexiconSchema != IndexRebuildCoordinator.LexiconSchema;

            return new IndexStatusResult(
                Modalities: modalities,
                FileCount: fileCount,
                ChunkCount: chunkCount,
                StatusDistribution: statusDistribution,
                LexicalDegraded: lexicalDegraded,
                RebuildGeneration: generationId ?? "initial",
                MonitoredDirectoryCount: monitoredDirectoryCount,
                Http: "disabled",
                State: stateMachine.CurrentState.ToString()
            );
        }, ct);
    }

    /// <summary>Weight of one rank in the cross-source reciprocal-rank fusion (same constant family the application uses).</summary>
    private const double RankFusionOffset = 60;
    /// <summary>Upper bound on characters scanned while locating a window and counting lines in one read.</summary>
    private const long MaxScanCharacters = 32L << 20;
    /// <summary>A single line is cut here so a minified or binary-looking file cannot allocate an unbounded string.</summary>
    private const int MaxLineCharacters = 16 * 1024;

    private sealed record LexicalRow(string Path, double Rank, FileHit Hit, double Mtime, long Size);

    /// <summary>Escapes a directory for a LIKE pattern: '%' and '_' are legal in Windows paths, '\' is the separator, so '^' escapes.</summary>
    private static string EscapeLike(string value) =>
        value.Replace("^", "^^", StringComparison.Ordinal).Replace("%", "^%", StringComparison.Ordinal).Replace("_", "^_", StringComparison.Ordinal);

    public Task<IReadOnlyList<FileHit>> SearchFilesAsync(
        string query,
        string? mode = null,
        string? root = null,
        int topK = 8,
        CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<FileHit>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(query))
            {
                throw new McpException(McpErrors.ValidationError, "Query cannot be empty or whitespace.");
            }
            if (query.Length > 4096)
            {
                throw new McpException(McpErrors.ValidationError, $"Query length ({query.Length}) exceeds maximum 4096 characters.");
            }
            if (topK < 1 || topK > 20)
            {
                throw new McpException(McpErrors.ValidationError, $"topK ({topK}) must be between 1 and 20.");
            }
            if (mode != null && mode != "keyword" && mode != "hybrid")
            {
                throw new McpException(McpErrors.ValidationError, $"mode '{mode}' is invalid; must be 'keyword' or 'hybrid'.");
            }

            using var connection = OpenReadOnlyConnection();

            // Build FTS query
            string trimmed = query.Trim();
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string ftsPattern = string.Join(" ", parts.Select(p => $"\"{p.Replace("\"", "\"\"")}\""));

            // Two lexical sources, same rules as the application: the literal index and the segmented index.
            // Querying only the literal one silently loses every Chinese phrase it cannot serve.
            var segmented = SegmentedPattern(trimmed);
            var segmentedAnd = SegmentedAndPattern(trimmed);
            var sourceList = new List<(string table, string pattern)> { ("fts", ftsPattern) };
            if (segmentedAnd.Length != 0) sourceList.Add(("fts_seg", segmentedAnd));
            if (segmented.Length != 0) sourceList.Add(("fts_seg", segmented));
            var sources = sourceList.ToArray();
            var ranked = new List<List<LexicalRow>>();
            foreach (var (table, pattern) in sources)
            {
                var sql = new StringBuilder();
                if (string.IsNullOrWhiteSpace(root))
                {
                    int cteLimit = Math.Max(150, topK * 25);
                    sql.Append($@"
                        WITH top_matches AS (
                            SELECT rowid AS seg_id, bm25({table}) AS score
                            FROM {table}
                            WHERE {table} MATCH $ftsPattern
                            ORDER BY bm25({table})
                            LIMIT {cteLimit}
                        )
                        SELECT s.id, s.file, m.score, s.kind AS seg_kind, s.modality,
                               s.loc, s.loc_end, substr(s.excerpt, 1, 220), f.path, f.kind AS file_kind, f.mtime, f.size
                        FROM top_matches m
                        JOIN segments s ON s.id = m.seg_id
                        JOIN files f ON f.id = s.file
                        ORDER BY m.score LIMIT $scanLimit");
                }
                else
                {
                    sql.Append($@"
                        SELECT s.id, s.file, bm25({table}) AS score, s.kind AS seg_kind, s.modality,
                               s.loc, s.loc_end, substr(s.excerpt, 1, 220), f.path, f.kind AS file_kind, f.mtime, f.size
                        FROM {table}
                        JOIN segments s ON s.id = {table}.rowid
                        JOIN files f ON f.id = s.file
                        WHERE {table} MATCH $ftsPattern
                          AND (f.path LIKE $rootPattern ESCAPE '^' OR f.path LIKE $rootPatternAlt ESCAPE '^')
                        ORDER BY bm25({table}) LIMIT $scanLimit");
                }

                var rows = new List<LexicalRow>();
                var seenInSource = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = sql.ToString();
                    // Bind the pattern of the current source: the segmented iteration must not receive the
                    // literal phrase, or the segmented index is queried with the wrong expression.
                    cmd.Parameters.AddWithValue("$ftsPattern", pattern);
                    // Scan up to topK * 10 segments to allow file-level deduplication
                    cmd.Parameters.AddWithValue("$scanLimit", Math.Max(50, topK * 10));

                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        string norm = root.TrimEnd('\\', '/');
                        cmd.Parameters.AddWithValue("$rootPattern", EscapeLike(norm.Replace('/', '\\')) + "\\%");
                        cmd.Parameters.AddWithValue("$rootPatternAlt", EscapeLike(norm.Replace('\\', '/')) + "/%");
                    }

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested();

                        string path = reader.GetString(8);
                        if (!seenInSource.Add(path)) continue;

                        double bm25 = reader.GetDouble(2);
                        string fileKind = reader.IsDBNull(9) ? "file" : reader.GetString(9);
                        double mtime = reader.IsDBNull(10) ? 0 : reader.GetDouble(10);
                        long size = reader.IsDBNull(11) ? 0 : reader.GetInt64(11);

                        string excerpt = reader.IsDBNull(7) ? "" : reader.GetString(7);
                        if (excerpt.Length > 200) excerpt = excerpt[..200];

                        int? page = null;
                        if (!reader.IsDBNull(5))
                        {
                            page = (int)Math.Round(reader.GetDouble(5));
                        }

                        int? paragraph = null;
                        if (!reader.IsDBNull(6))
                        {
                            double pEnd = reader.GetDouble(6);
                            if (reader.IsDBNull(5) || Math.Abs(pEnd - reader.GetDouble(5)) > 0.001)
                            {
                                paragraph = (int)Math.Round(pEnd);
                            }
                        }

                        string version = FormattableString.Invariant($"{mtime:F6}:{size}");
                        var hit = new FileHit(
                            Path: path,
                            Kind: fileKind,
                            Score: 0, // replaced by the fused score below
                            MatchType: "keyword",
                            Excerpt: excerpt,
                            Page: page,
                            Paragraph: paragraph,
                            Version: version
                        );
                        // bm25() is negative and only comparable inside one FTS table; keep the magnitude as a tie-breaker.
                        rows.Add(new LexicalRow(path, Math.Abs(bm25), hit, mtime, size));
                    }
                }
                catch (SqliteException ex)
                {
                    if (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6)
                    {
                        throw new McpException(McpErrors.Busy, $"Index database busy: {ex.Message}", ex);
                    }
                    // An expression one source cannot parse must not discard what the other source found.
                    if (ex.Message.Contains("syntax error", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("fts5:", StringComparison.OrdinalIgnoreCase)) continue;
                    throw new McpException(McpErrors.IndexIncompatible, $"Index search error: {ex.Message}", ex);
                }
                ranked.Add(rows);
            }

            // Reciprocal-rank fusion across the sources: bm25 values from different FTS tables are not comparable.
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var fused = new Dictionary<string, (LexicalRow Row, double Score)>(comparer);
            foreach (var rows in ranked)
            {
                for (var index = 0; index < rows.Count; index++)
                {
                    var contribution = 1.0 / (RankFusionOffset + index + 1);
                    fused[rows[index].Path] = fused.TryGetValue(rows[index].Path, out var existing)
                        ? (existing.Row.Rank >= rows[index].Rank ? existing.Row : rows[index], existing.Score + contribution)
                        : (rows[index], contribution);
                }
            }
            double best = ranked.Count / (RankFusionOffset + 1);
            var results = new List<FileHit>(topK);
            foreach (var (row, score) in fused.Values
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Row.Rank)
                .ThenBy(item => item.Row.Path, StringComparer.Ordinal)
                .Take(topK))
            {
                results.Add(row.Hit with { Score = best > 0 ? Math.Round(score / best, 4) : 0 });
                registry.Register(row.Path, row.Hit.Version, row.Mtime, row.Size);
            }
            return results;
        }, ct);
    }

    public async Task<FileContextResult> ReadFileContextAsync(
        string path,
        int? startLine = null,
        int? lineCount = null,
        string? expectedVersion = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException(McpErrors.ValidationError, "path parameter is required.");
        }

        // 1. Must be registered by search_files in this session
        if (!registry.IsAllowed(path, out var allowedInfo))
        {
            throw new McpException(McpErrors.ExcludedFile, $"File '{path}' is not in the referenced search set for this session.");
        }

        // 2. Must exist in SQLite index
        long dbFileId = 0;
        double dbMtime = 0;
        long dbSize = 0;
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var connection = OpenReadOnlyConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, mtime, size FROM files WHERE path = $path";
            cmd.Parameters.AddWithValue("$path", path);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                throw new McpException(McpErrors.ExcludedFile, $"File '{path}' is not recorded in the index database.");
            }
            dbFileId = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
            dbMtime = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
            dbSize = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
        }, ct).ConfigureAwait(false);

        // 3. Expected version check
        string currentVersion = FormattableString.Invariant($"{dbMtime:F6}:{dbSize}");
        if (expectedVersion != null)
        {
            if (expectedVersion != allowedInfo!.Version && expectedVersion != currentVersion)
            {
                throw new McpException(McpErrors.StaleReference, $"File reference is stale. Expected version '{expectedVersion}', current '{currentVersion}'.");
            }
        }

        // 4. Validate startLine and lineCount
        int start = startLine ?? 1;
        int count = lineCount ?? 50;

        if (start < 1)
        {
            throw new McpException(McpErrors.ValidationError, $"startLine ({start}) must be >= 1.");
        }
        if (count < 1 || count > 200)
        {
            throw new McpException(McpErrors.ValidationError, $"lineCount ({count}) must be between 1 and 200.");
        }

        // 5. Read file safely from disk
        if (!File.Exists(path))
        {
            throw new McpException(McpErrors.StaleReference, $"File '{path}' no longer exists on disk.");
        }

        const int maxByteLimit = 64 * 1024; // 64 KiB
        var lines = new List<string>();
        long currentLine = 0;
        int linesRead = 0;
        int accumulatedBytes = 0;
        bool truncated = false;
        bool totalExact = true;
        bool indexStale;

        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        bool isDocumentContainer = ext is "pdf" or "docx" or "pptx" or "xlsx" or "doc" or "xls" or "ppt" or "odt";

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // The content must be what the index described; report (do not hide) a file that changed after indexing.
            var diskVersion = FormattableString.Invariant($"{ToUnixSeconds(File.GetLastWriteTimeUtc(path)):F6}:{stream.Length}");
            indexStale = !VersionsMatch(diskVersion, currentVersion);
            if (isDocumentContainer && !indexStale && dbFileId > 0)
            {
                return await ReadIndexedDocumentSegmentsAsync(path, dbFileId, start, count, currentVersion, indexStale, maxByteLimit, ct).ConfigureAwait(false);
            }
            await EnsureReadableTextAsync(stream, ct).ConfigureAwait(false);
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[8192];
            var line = new StringBuilder();
            var lineCut = false;
            long scanned = 0;
            var previousWasCarriageReturn = false;
            var done = false;
            var lineHasContent = false;

            void EndLine()
            {
                currentLine++;
                lineHasContent = false;
                var text = line.ToString();
                line.Clear();
                var cut = lineCut;
                lineCut = false;
                if (currentLine < start || linesRead >= count) return;
                int lineBytes = Encoding.UTF8.GetByteCount(text) + 1;
                if (accumulatedBytes + lineBytes > maxByteLimit)
                {
                    truncated = true;
                    int remaining = maxByteLimit - accumulatedBytes;
                    if (remaining > 0)
                    {
                        var bytes = Encoding.UTF8.GetBytes(text);
                        lines.Add(Encoding.UTF8.GetString(bytes, 0, Math.Min(remaining, bytes.Length)));
                    }
                    linesRead++;
                    return;
                }
                if (cut) truncated = true;
                lines.Add(text);
                accumulatedBytes += lineBytes;
                linesRead++;
            }

            while (!done)
            {
                ct.ThrowIfCancellationRequested();
                var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0) break;
                scanned += read;
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (previousWasCarriageReturn && c == '\n') { previousWasCarriageReturn = false; continue; }
                    previousWasCarriageReturn = c == '\r';
                    if (c is '\r' or '\n') { EndLine(); continue; }
                    lineHasContent = true;
                    // Only characters of lines inside the requested window are kept; the rest are merely counted.
                    if (currentLine + 1 >= start && linesRead < count)
                    {
                        if (line.Length < MaxLineCharacters) line.Append(c);
                        else lineCut = true;
                    }
                }
                if (scanned > MaxScanCharacters)
                {
                    totalExact = false;
                    done = true;
                }
                else if (linesRead >= count && scanned > MaxScanCharacters / 4)
                {
                    // The window is complete; counting the rest of a very large file is not worth unbounded I/O.
                    totalExact = false;
                    done = true;
                }
            }
            if (!done && lineHasContent) EndLine();
        }

        if (currentLine < start && !totalExact)
            throw new McpException(McpErrors.ValidationError, $"startLine ({start}) lies beyond the {MaxScanCharacters >> 20} MiB this read will scan.");

        return new FileContextResult(
            Path: path,
            StartLine: start,
            LineCount: count,
            LinesRead: linesRead,
            TotalLines: (int)Math.Min(currentLine, int.MaxValue),
            Version: currentVersion,
            Content: string.Join("\n", lines),
            Truncated: truncated,
            TotalLinesExact: totalExact,
            IndexStale: indexStale
        );
    }

    private Task<FileContextResult> ReadIndexedDocumentSegmentsAsync(string path, long fileId, int start, int count,
        string currentVersion, bool indexStale, int maxByteLimit, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var connection = OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT kind, loc, excerpt FROM segments WHERE file = $fileId AND kind != 'name' AND excerpt IS NOT NULL ORDER BY id";
        cmd.Parameters.AddWithValue("$fileId", fileId);
        using var reader = cmd.ExecuteReader();
        var allLines = new List<string>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var segKind = reader.IsDBNull(0) ? "chunk" : reader.GetString(0);
            double? loc = reader.IsDBNull(1) ? null : reader.GetDouble(1);
            var excerpt = reader.IsDBNull(2) ? "" : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(excerpt)) continue;
            if (loc.HasValue && segKind == "page") allLines.Add($"--- [Page {(int)loc.Value}] ---");
            foreach (var rawLine in excerpt.Replace("\r\n", "\n").Split('\n'))
            {
                allLines.Add(rawLine.Length > MaxLineCharacters ? rawLine[..MaxLineCharacters] : rawLine);
            }
        }
        var lines = new List<string>();
        int accumulatedBytes = 0;
        bool truncated = false;
        for (int i = start - 1; i < allLines.Count && lines.Count < count; i++)
        {
            var text = allLines[i];
            int lineBytes = Encoding.UTF8.GetByteCount(text) + 1;
            if (accumulatedBytes + lineBytes > maxByteLimit)
            {
                truncated = true;
                int remaining = maxByteLimit - accumulatedBytes;
                if (remaining > 0)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    lines.Add(Encoding.UTF8.GetString(bytes, 0, Math.Min(remaining, bytes.Length)));
                }
                break;
            }
            lines.Add(text);
            accumulatedBytes += lineBytes;
        }
        return new FileContextResult(
            Path: path,
            StartLine: start,
            LineCount: count,
            LinesRead: lines.Count,
            TotalLines: allLines.Count,
            Version: currentVersion,
            Content: string.Join("\n", lines),
            Truncated: truncated,
            TotalLinesExact: true,
            IndexStale: indexStale
        );
    }, ct);

    private static double ToUnixSeconds(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>Index versions are "mtime:size"; compare size exactly and mtime within filesystem timestamp granularity.</summary>
    private static bool VersionsMatch(string disk, string indexed)
    {
        var left = disk.Split(':');
        var right = indexed.Split(':');
        if (left.Length != 2 || right.Length != 2 || left[1] != right[1]) return false;
        return double.TryParse(left[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(right[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            && Math.Abs(a - b) < 2.0;
    }

    /// <summary>Refuses binary content: MCP clients receive text, not the bytes of a PDF, archive or database.</summary>
    private static async Task EnsureReadableTextAsync(FileStream stream, CancellationToken ct)
    {
        var sample = new byte[Math.Min(stream.Length, 8192)];
        await stream.ReadExactlyAsync(sample, ct).ConfigureAwait(false);
        var utf16 = sample.Length >= 2 && ((sample[0] == 0xFF && sample[1] == 0xFE) || (sample[0] == 0xFE && sample[1] == 0xFF));
        if (!utf16 && Array.IndexOf(sample, (byte)0) >= 0)
            throw new McpException(McpErrors.ExcludedFile, "The file is binary (for example a PDF, Office package or image); only its indexed excerpts are available through search_files.");
    }

    public void Dispose()
    {
        // No persistent open connection held; each query opens and disposes its own connection.
    }
}
