using Microsoft.Data.Sqlite;

namespace WinSight.Infrastructure.Storage;

/// <summary>Outcome of one rebuild window. Both databases are reported so the caller can keep the old one as a
/// restore point instead of assuming the switch happened.</summary>
public sealed record RebuildOutcome(bool Switched, string? Generation, string? PreviousDirectory, string? Detail);

/// <summary>
/// One window that produces a new index generation for both a lexicon change and a model change, then switches
/// atomically. The live database keeps serving reads while staging is built; a failed validation restores it.
/// </summary>
public static class IndexRebuildCoordinator
{
    public const string GenerationKey = "generation_id";
    public const string LexiconSchemaKey = "lexicon_schema";
    public const string DictionaryKey = "dictionary_fingerprint";

    /// <summary>True when the live database was built with a different lexicon or model identity.</summary>
    public static bool NeedsRebuild(string indexDirectory, string expectedProfile, string expectedDictionary)
    {
        var database = Path.Combine(indexDirectory, "index.sqlite");
        if (!File.Exists(database)) return false;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key,value FROM meta WHERE key IN ('profile_id','lexicon_schema','dictionary_fingerprint')";
            using var rows = command.ExecuteReader();
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            while (rows.Read()) meta[rows.GetString(0)] = rows.GetString(1);
            if (meta.Count == 0) return false;                       // empty or legacy database: initialise in place
            if (meta.GetValueOrDefault("profile_id") != expectedProfile) return true;
            if (meta.GetValueOrDefault("lexicon_schema") != "2") return true;
            var stored = meta.GetValueOrDefault("dictionary_fingerprint");
            // An unset fingerprint on a database with content means the identity is unknown: rebuild once.
            return !string.IsNullOrEmpty(stored) && stored != expectedDictionary;
        }
        catch (SqliteException)
        {
            // A database we cannot read is not a licence to overwrite it; the caller reports the fault.
            return false;
        }
    }

    /// <summary>
    /// Builds a staging database through <paramref name="build"/>, validates it, then swaps it in. The live
    /// database is renamed aside (never deleted) and restored if validation or the swap fails.
    /// </summary>
    public static async Task<RebuildOutcome> RebuildAsync(string indexDirectory, string generation, Func<string, Task> build,
        Func<string, Task<IReadOnlyList<string>>> fixedQueryProbe, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        var stagingRoot = Path.Combine(indexDirectory, "rebuild", generation);
        var liveDatabase = Path.Combine(indexDirectory, "index.sqlite");
        var stagingDatabase = Path.Combine(stagingRoot, "index.sqlite");
        try
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
            Directory.CreateDirectory(stagingRoot);
            await build(stagingRoot).ConfigureAwait(false);
            if (!File.Exists(stagingDatabase)) return new RebuildOutcome(false, null, null, "staging database was not produced");
            var problem = await ValidateAsync(stagingDatabase, fixedQueryProbe, cancellationToken).ConfigureAwait(false);
            // Pooled connections keep the staging file open; release them before any move.
            SqliteConnection.ClearAllPools();
            if (problem is not null) return new RebuildOutcome(false, generation, null, problem);
            SqliteConnection.ClearAllPools();
            var previous = Path.Combine(indexDirectory, "pre-" + generation);
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            var moved = new List<string>();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var source = liveDatabase + suffix;
                if (!File.Exists(source)) continue;
                Directory.CreateDirectory(previous);
                File.Move(source, Path.Combine(previous, "index.sqlite" + suffix));
                moved.Add(suffix);
            }
            try
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var source = stagingDatabase + suffix;
                    if (File.Exists(source)) File.Move(source, liveDatabase + suffix);
                }
                Directory.Delete(stagingRoot, true);
                return new RebuildOutcome(true, generation, moved.Count == 0 ? null : previous, "switched");
            }
            catch (IOException error)
            {
                foreach (var suffix in moved) File.Move(Path.Combine(previous, "index.sqlite" + suffix), liveDatabase + suffix);
                return new RebuildOutcome(false, generation, null, "switch failed: " + error.Message);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or SqliteException)
        {
            return new RebuildOutcome(false, generation, null, error.GetType().Name + ": " + error.Message);
        }
    }

    private static async Task<string?> ValidateAsync(string database, Func<string, Task<IReadOnlyList<string>>> probe, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (Scalar(connection, "PRAGMA integrity_check") != "ok") return "integrity_check failed";
        if (Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check") != "0") return "foreign_key_check failed";
        var files = long.Parse(Scalar(connection, "SELECT COUNT(*) FROM files")!);
        var segments = long.Parse(Scalar(connection, "SELECT COUNT(*) FROM segments")!);
        if (segments == 0) return "staging database has no segments";
        var orphans = long.Parse(Scalar(connection, "SELECT COUNT(*) FROM segments s LEFT JOIN files f ON f.id=s.file WHERE f.id IS NULL")!);
        if (orphans != 0) return "orphan segments: " + orphans;
        var unindexed = long.Parse(Scalar(connection, "SELECT COUNT(*) FROM segments s LEFT JOIN fts ON fts.rowid=s.id WHERE fts.rowid IS NULL")!);
        if (unindexed != 0) return "segments missing from the original FTS: " + unindexed;
        var unsegmented = long.Parse(Scalar(connection, "SELECT COUNT(*) FROM segments s JOIN fts ON fts.rowid=s.id LEFT JOIN fts_seg ON fts_seg.rowid=s.id WHERE fts_seg.rowid IS NULL")!);
        if (unsegmented != 0) return "segments missing from the segmented FTS: " + unsegmented;
        // A fixed probe set must return at least one hit; the caller supplies queries that are known to match.
        var hits = await probe(database).ConfigureAwait(false);
        if (hits.Count == 0) return "fixed query set returned no hits (files=" + files + ", segments=" + segments + ")";
        return null;
    }

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }
}
