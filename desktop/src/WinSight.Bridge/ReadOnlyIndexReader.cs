using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using WinSight.Core;
using WinSight.Infrastructure.Search;

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
            throw new McpException(McpErrors.IndexIncompatible, $"Index database not found at '{databasePath}'.");
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
            cmd.CommandText = "PRAGMA query_only=ON; PRAGMA foreign_keys=ON;";
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

    /// <summary>Segmented FTS expression for the query; empty when the segmenter is unavailable.</summary>
    private static string SegmentedPattern(string text)
    {
        if (!NativeSegmenter.IsAvailable) return "";
        var tokens = NativeSegmenter.Segment(text)
            .Where(t => t.Length != 0 && t != "AND" && t != "OR" && t != "NOT")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
            .ToArray();
        return string.Join(" OR ", tokens);
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
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM files";
                fileCount = Convert.ToInt32(cmd.ExecuteScalar());
            }

            int chunkCount = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM segments";
                chunkCount = Convert.ToInt32(cmd.ExecuteScalar());
            }

            var modalities = new List<string>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT modality FROM segments WHERE modality IS NOT NULL AND modality != '' ORDER BY modality";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    modalities.Add(reader.GetString(0));
                }
            }

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

            bool lexicalDegraded = !NativeSegmenter.IsAvailable || lexiconSchema != "2";

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
            string ftsPattern;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                ftsPattern = $"\"{parts[0].Replace("\"", "\"\"")}\"";
            }
            else
            {
                ftsPattern = string.Join(" ", parts.Select(p => $"\"{p.Replace("\"", "\"\"")}\""));
            }

            // Two lexical sources, same rules as the application: the literal index and the segmented index.
            // Querying only the literal one silently loses every Chinese phrase it cannot serve.
            var segmented = SegmentedPattern(trimmed);
            var results = new List<FileHit>();
            var seenFiles = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            (string table, string pattern)[] sources = segmented.Length == 0
                ? new[] { ("fts", ftsPattern) }
                : new[] { ("fts", ftsPattern), ("fts_seg", segmented) };
            foreach (var (table, pattern) in sources)
            {
            var sql = new StringBuilder();
            sql.Append($@"
                SELECT s.id, s.file, bm25({table}) AS score, s.kind AS seg_kind, s.modality,
                       s.loc, s.loc_end, s.excerpt, f.path, f.kind AS file_kind, f.mtime, f.size
                FROM {table}
                JOIN segments s ON s.id = {table}.rowid
                JOIN files f ON f.id = s.file
                WHERE {table} MATCH $ftsPattern");

            if (!string.IsNullOrWhiteSpace(root))
            {
                sql.Append(" AND (f.path LIKE $rootPattern OR f.path LIKE $rootPatternAlt)");
            }

            sql.Append($" ORDER BY bm25({table}) LIMIT $scanLimit");

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
                    cmd.Parameters.AddWithValue("$rootPattern", norm.Replace('/', '\\') + "\\%");
                    cmd.Parameters.AddWithValue("$rootPatternAlt", norm.Replace('\\', '/') + "/%");
                }

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();

                    string path = reader.GetString(8);
                    if (!seenFiles.Add(path)) continue;

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

                    // Format normalized score (bm25 in SQLite is negative; lower is better)
                    double normScore = Math.Round(Math.Abs(bm25), 4);
                    string version = FormattableString.Invariant($"{mtime:F6}:{size}");

                    var hit = new FileHit(
                        Path: path,
                        Kind: fileKind,
                        Score: normScore,
                        MatchType: "keyword",
                        Excerpt: excerpt,
                        Page: page,
                        Paragraph: paragraph,
                        Version: version
                    );

                    results.Add(hit);
                    registry.Register(path, version, mtime, size);

                    if (results.Count >= topK) break;
                }
            }
            catch (SqliteException ex)
            {
                if (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6)
                {
                    throw new McpException(McpErrors.Busy, $"Index database busy: {ex.Message}", ex);
                }
                // FTS syntax error on odd input
                if (ex.Message.Contains("syntax error", StringComparison.OrdinalIgnoreCase))
                {
                    // Return empty list on unparseable FTS tokens
                    return Array.Empty<FileHit>();
                }
                throw new McpException(McpErrors.IndexIncompatible, $"Index search error: {ex.Message}", ex);
            }
            }   // lexical sources

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
        double dbMtime = 0;
        long dbSize = 0;
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var connection = OpenReadOnlyConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT mtime, size FROM files WHERE path = $path";
            cmd.Parameters.AddWithValue("$path", path);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                throw new McpException(McpErrors.ExcludedFile, $"File '{path}' is not recorded in the index database.");
            }
            dbMtime = reader.IsDBNull(0) ? 0 : reader.GetDouble(0);
            dbSize = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
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
        int currentLine = 0;
        int linesRead = 0;
        int accumulatedBytes = 0;
        bool truncated = false;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                currentLine++;
                if (currentLine < start) continue;
                if (linesRead >= count) continue; // keep counting totalLines

                int lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (accumulatedBytes + lineBytes > maxByteLimit)
                {
                    truncated = true;
                    int remaining = maxByteLimit - accumulatedBytes;
                    if (remaining > 0)
                    {
                        var bytes = Encoding.UTF8.GetBytes(line);
                        lines.Add(Encoding.UTF8.GetString(bytes, 0, Math.Min(remaining, bytes.Length)));
                    }
                    linesRead++;
                    break;
                }

                lines.Add(line);
                accumulatedBytes += lineBytes;
                linesRead++;
            }
        }

        return new FileContextResult(
            Path: path,
            StartLine: start,
            LineCount: count,
            LinesRead: linesRead,
            TotalLines: currentLine,
            Version: currentVersion,
            Content: string.Join("\n", lines),
            Truncated: truncated
        );
    }

    public void Dispose()
    {
        // No persistent open connection held; each query opens and disposes its own connection.
    }
}
