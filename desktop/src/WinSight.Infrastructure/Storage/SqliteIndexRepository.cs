using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Search;

namespace WinSight.Infrastructure.Storage;

/// <summary>Schema5 reader; only newly created, explicitly marked native indexes are writable.</summary>
public sealed class SqliteIndexRepository : IIndexRepository, IUsnCheckpointRepository
{
    public const string NativeFormat = "winsight-native-schema5-fp16-v1";
    private const string MarkerKey = "native_format";
    private static readonly JsonSerializerOptions MetadataJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions CompactAnchorJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static readonly ulong FileUnitHash = CompactVectorIndex.ComputeUnitHash("file");
    private const string Tokenizer = "porter unicode61 remove_diacritics 2 categories 'L* N* Co M*'";
    private const string EmbeddableSegmentPredicate = "kind IN ('chunk', 'page', 'cells', 'lines') AND length(trim(coalesce(excerpt,''))) > 0";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly SemaphoreSlim auxReadGate = new(1, 1);
    private readonly object vectorCacheSync = new();
    private readonly Action<IndexInitializationStage>? initializationCheckpoint;
    private SqliteConnection? connection;
    private SqliteConnection? readConnection;
    private SqliteConnection? auxReadConnection;
    private (long ReadVersion, long ConnVersion, CompactVectorIndex Index)? vectorCache;
    private (long ReadVersion, long ConnVersion, long LexicalSeq, string Query, string? Kind, string? Root, int WordLimit, bool NeedsSourceAnchors, Func<string, bool>? Authorize, IReadOnlyList<WordEvidence> Matches, string? FilterSignature)? lastWordEvidence;
    private readonly HashSet<string> dirtyVectorPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> dirtyVectorSeqs = new(StringComparer.Ordinal);
    private long vectorWriteSeq;
    private long lexicalWriteSeq;
    private long vectorCacheSeq;
    private int inflightVectorWrites;
    private bool writable;
    private bool disposed;
    public string DatabasePath { get; }
    public bool UseInt8Quantization { get; set; }

    public SqliteIndexRepository(string databasePath) : this(databasePath, null) { }

    internal SqliteIndexRepository(string databasePath, Action<IndexInitializationStage>? initializationCheckpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        this.initializationCheckpoint = initializationCheckpoint;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => RunAsync(() => { InitializeCore(cancellationToken); return 0; }, cancellationToken);

    private void InitializeCore(CancellationToken ct)
    {
        if (Volatile.Read(ref connection) != null) return;
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(DatabasePath)) CreateAndPublishDatabase(ct);
        var db = Open(DatabasePath, SqliteOpenMode.ReadOnly);
        SqliteConnection? readDb = null;
        SqliteConnection? auxReadDb = null;
        try
        {
            ValidateSchema(db);
            string? marker = Scalar(db, null, "SELECT value FROM meta WHERE key=$key", ("$key", MarkerKey)) as string;
            if (marker != null && marker != NativeFormat) throw new InvalidDataException("Unsupported native index format.");
            writable = marker == NativeFormat;
            if (writable)
            {
                db.Dispose();
                db = Open(DatabasePath, SqliteOpenMode.ReadWrite);
                ValidateSchema(db);
                if (!Equals(Scalar(db, null, "SELECT value FROM meta WHERE key=$key", ("$key", MarkerKey)), NativeFormat))
                    throw new InvalidDataException("Index format changed while opening.");
                Execute(db, null, "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
                Execute(db, null, """
                    CREATE TABLE IF NOT EXISTS index_tasks (
                        path TEXT PRIMARY KEY,
                        root TEXT NOT NULL,
                        code INTEGER NOT NULL,
                        kind TEXT NOT NULL,
                        tombstone TEXT,
                        state TEXT NOT NULL,
                        attempts INTEGER NOT NULL DEFAULT 0,
                        due_utc TEXT NOT NULL,
                        seen_utc TEXT NOT NULL,
                        reason TEXT
                    );
                    CREATE INDEX IF NOT EXISTS ix_index_tasks_due ON index_tasks(state, due_utc);
                    CREATE TABLE IF NOT EXISTS usn_checkpoints (
                        volume_serial TEXT PRIMARY KEY,
                        journal_id TEXT,
                        next_usn TEXT,
                        reconcile_utc TEXT
                    );
                    """);
                Execute(db, null, """
                    UPDATE index_tasks SET
                        state = CASE WHEN attempts + 1 >= 4 THEN 'Failed' ELSE 'Pending' END,
                        attempts = CASE WHEN attempts + 1 >= 4 THEN 8 ELSE attempts + 1 END
                    WHERE state = 'Running';
                    UPDATE index_tasks SET state = 'Pending' WHERE state = 'Stabilizing';
                    """);
            }
            ct.ThrowIfCancellationRequested();
            readDb = Open(DatabasePath, SqliteOpenMode.ReadOnly);
            auxReadDb = Open(DatabasePath, SqliteOpenMode.ReadOnly);
            readConnection = readDb;
            auxReadConnection = auxReadDb;
            Volatile.Write(ref connection, db);
        }
        catch
        {
            Volatile.Write(ref connection, null);
            readConnection = null;
            auxReadConnection = null;
            auxReadDb?.Dispose();
            readDb?.Dispose();
            db.Dispose();
            throw;
        }
    }

    private void CreateAndPublishDatabase(CancellationToken ct)
    {
        RefuseOrphanSidecars();
        string directory = Path.GetDirectoryName(DatabasePath)!;
        Directory.CreateDirectory(directory);
        string stagingPath = Path.Combine(directory, $".{Path.GetFileName(DatabasePath)}.{Guid.NewGuid():N}.initializing");
        // NM-BR-09: never expose the reserved empty file as the index. CreateNew owns only this private pathname.
        using (var reservation = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try
        {
            using (var staged = Open(stagingPath, SqliteOpenMode.ReadWrite))
            {
                Execute(staged, null, "PRAGMA page_size=8192; PRAGMA auto_vacuum=INCREMENTAL; PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
                using (var tx = staged.BeginTransaction(deferred: false))
                {
                    Execute(staged, tx, Schema);
                    initializationCheckpoint?.Invoke(IndexInitializationStage.SchemaCreated);
                    ct.ThrowIfCancellationRequested();
                    Execute(staged, tx, "INSERT INTO meta(key,value) VALUES($key,$value)", ("$key", MarkerKey), ("$value", NativeFormat));
                    ct.ThrowIfCancellationRequested();
                    tx.Commit();
                }
                ValidateSchema(staged);
                if (Convert.ToInt64(Scalar(staged, null, "PRAGMA wal_checkpoint(TRUNCATE)")) != 0)
                    throw new IOException("Private database checkpoint did not complete; index was not published.");
            }
            if (File.Exists(stagingPath + "-wal") || File.Exists(stagingPath + "-shm") || File.Exists(stagingPath + "-journal"))
                throw new IOException("Private database still has sidecars after closing; index was not published.");
            initializationCheckpoint?.Invoke(IndexInitializationStage.BeforePublish);
            ct.ThrowIfCancellationRequested();
            RefuseOrphanSidecars();
            try { File.Move(stagingPath, DatabasePath, overwrite: false); }
            catch (IOException) when (File.Exists(DatabasePath))
            {
                // A concurrent publisher won. InitializeCore validates that file read-only; never replace/delete it.
            }
        }
        finally
        {
            // Only paths reserved by this initializer, never DatabasePath or any target sidecars.
            File.Delete(stagingPath + "-journal");
            File.Delete(stagingPath + "-wal");
            File.Delete(stagingPath + "-shm");
            File.Delete(stagingPath);
        }
    }

    private void RefuseOrphanSidecars()
    {
        if (!File.Exists(DatabasePath) && (File.Exists(DatabasePath + "-wal") || File.Exists(DatabasePath + "-shm") || File.Exists(DatabasePath + "-journal")))
            throw new InvalidDataException("Orphan SQLite sidecars exist; refusing to create an index over them.");
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 30
        }.ToString());
        try
        {
            db.Open();
            try
            {
                if (mode != SqliteOpenMode.ReadOnly)
                    Execute(db, null, "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536; PRAGMA mmap_size=268435456;");
                else
                    Execute(db, null, "PRAGMA busy_timeout=5000; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-32768; PRAGMA mmap_size=268435456;");
            }
            catch (SqliteException) { }
            // Query-only collation: Windows ordinal path casing without changing the on-disk tokenizer or indexes.
            db.CreateCollation("WINSIGHT_PATH", StringComparer.OrdinalIgnoreCase.Compare);
            string? lastLiteralJson = null;
            string[] literals = [];
            string? lastMaskPath = null;
            long lastPathMask = 0;
            db.CreateFunction<string?, string?, string, long>("WINSIGHT_LITERAL_MASK", (text, name, json) =>
            {
                if (!string.Equals(lastLiteralJson, json, StringComparison.Ordinal))
                {
                    literals = JsonSerializer.Deserialize<string[]>(json) ?? throw new InvalidDataException("Missing literal query.");
                    lastLiteralJson = json;
                    lastMaskPath = null;
                    lastPathMask = 0;
                }
                string pathKey = name ?? "";
                if (!string.Equals(lastMaskPath, pathKey, StringComparison.Ordinal))
                {
                    lastMaskPath = pathKey;
                    lastPathMask = DocumentSearchQuery.PathLiteralMask(pathKey, literals);
                }
                return DocumentSearchQuery.LiteralMask(text, null, literals, lastPathMask);
            }, isDeterministic: true);
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private static void ValidateSchema(SqliteConnection db)
    {
        if (Convert.ToInt64(Scalar(db, null, "PRAGMA user_version")) != 5)
            throw new InvalidDataException("Only schema5 is supported; no automatic migration or clearing is permitted.");
        // Additive table for existing databases: writing keeps working, and NeedsRebuild() forces a coordinated
        // generation switch because lexicon_schema is still absent. No silent migration of old rows.
        // A read-only open (legacy fixtures, inspection) must remain byte-identical, so the create is skipped there.
        try
        {
            Execute(db, null, "CREATE VIRTUAL TABLE IF NOT EXISTS fts_seg USING fts5(tokens, content='', contentless_delete=1, tokenize=\"unicode61 remove_diacritics 2\")");
            Execute(db, null, "CREATE INDEX IF NOT EXISTS ix_segments_meta ON segments(id, file, kind, loc, loc_end)");
            Execute(db, null, "CREATE INDEX IF NOT EXISTS ix_files_meta ON files(id, kind, path)");
            Execute(db, null, "CREATE INDEX IF NOT EXISTS ix_files_header ON files(id, path, kind, size, mtime, state, detail)");
            Execute(db, null, "CREATE INDEX IF NOT EXISTS ix_segments_vec ON segments(file, id, kind, modality, loc, loc_end) WHERE vec IS NOT NULL");
            Execute(db, null, $"CREATE INDEX IF NOT EXISTS ix_segments_embeddable ON segments(id) WHERE {EmbeddableSegmentPredicate}");
            Execute(db, null, $"CREATE INDEX IF NOT EXISTS ix_segments_pending_vec ON segments(id, file, kind) WHERE vec IS NULL AND {EmbeddableSegmentPredicate}");
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 8)
        {
            // read-only database: nothing to add, validation continues against what is there
        }
        var columns = new Dictionary<string, string[]>
        {
            ["meta"] = ["key", "value"],
            ["files"] = ["id", "path", "kind", "size", "mtime", "inode", "device", "state", "detail", "info", "indexed_at"],
            ["segments"] = ["id", "file", "kind", "modality", "loc", "loc_end", "excerpt", "vec"],
            ["fts"] = ["name", "body", "original"]
        };
        var types = new Dictionary<string, string[]>
        {
            ["meta"] = ["TEXT", "TEXT"],
            ["files"] = ["INTEGER", "TEXT", "TEXT", "INTEGER", "REAL", "INTEGER", "INTEGER", "TEXT", "TEXT", "TEXT", "REAL"],
            ["segments"] = ["INTEGER", "INTEGER", "TEXT", "TEXT", "REAL", "REAL", "TEXT", "BLOB"]
        };
        foreach (var entry in columns)
        {
            using var cmd = Command(db, null, $"PRAGMA table_info({entry.Key})");
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
            {
                int index = names.Count;
                names.Add(reader.GetString(1));
                if (entry.Key == "fts") continue;
                if (index >= types[entry.Key].Length || !reader.GetString(2).Equals(types[entry.Key][index], StringComparison.OrdinalIgnoreCase) ||
                    reader.GetInt32(5) != (index == 0 ? 1 : 0)) throw new InvalidDataException($"Unsupported {entry.Key} column types/primary key.");
                bool required = entry.Key switch { "meta" => index == 1, "files" => index is >= 1 and <= 7, _ => index is >= 1 and <= 3 };
                if ((reader.GetInt32(3) != 0) != required) throw new InvalidDataException($"Unsupported {entry.Key} nullability.");
            }
            if (!names.SequenceEqual(entry.Value)) throw new InvalidDataException($"Unsupported {entry.Key} columns.");
        }
        if (Convert.ToInt64(Scalar(db, null, "SELECT count(*) FROM pragma_foreign_key_list('segments') WHERE [table]='files' AND [from]='file' AND [to]='id' AND on_delete='CASCADE'")) != 1 ||
            Convert.ToInt64(Scalar(db, null, "SELECT count(*) FROM pragma_foreign_key_list('segments')")) != 1)
            throw new InvalidDataException("Unsupported segments foreign key.");
        if (Convert.ToInt64(Scalar(db, null, "SELECT count(*) FROM pragma_index_list('files') i WHERE i.[unique]=1 AND (SELECT count(*) FROM pragma_index_info(i.name))=1 AND (SELECT name FROM pragma_index_info(i.name))='path'")) < 1)
            throw new InvalidDataException("Missing unique file path constraint.");
        string? fts = Scalar(db, null, "SELECT sql FROM sqlite_master WHERE name='fts' AND type='table'") as string;
        if (fts == null || !fts.Contains("fts5", StringComparison.OrdinalIgnoreCase) || !fts.Contains(Tokenizer, StringComparison.Ordinal) ||
            !fts.Contains("original UNINDEXED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported FTS layout/tokenizer.");
        string? segmented = Scalar(db, null, "SELECT sql FROM sqlite_master WHERE name='fts_seg' AND type='table'") as string;
        if (segmented != null && !segmented.Contains("fts5", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported segmented FTS layout.");
        using var objects = Command(db, null, "SELECT name,type FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'");
        using var rows = objects.ExecuteReader();
        var allowedTables = new HashSet<string>(["meta", "files", "segments", "fts", "fts_data", "fts_idx", "fts_content", "fts_docsize", "fts_config", "index_tasks", "usn_checkpoints",
            "fts_seg", "fts_seg_data", "fts_seg_idx", "fts_seg_content", "fts_seg_docsize", "fts_seg_config"], StringComparer.Ordinal);
        while (rows.Read())
            if (rows.GetString(1) is "trigger" or "view" || (rows.GetString(1) == "table" && !allowedTables.Contains(rows.GetString(0))))
                throw new InvalidDataException("Unknown database objects; refusing an unsafe index.");
    }

    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() => { ct.ThrowIfCancellationRequested(); return action(); }, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<T> RunReadAsync<T>(Func<T> action, CancellationToken ct)
    {
        if (Volatile.Read(ref connection) is null)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { ObjectDisposedException.ThrowIf(disposed, this); InitializeCore(ct); }
            finally { gate.Release(); }
        }
        await readGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() => { ct.ThrowIfCancellationRequested(); return action(); }, ct).ConfigureAwait(false);
        }
        finally { readGate.Release(); }
    }

    private T Read<T>(CancellationToken ct, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        InitializeCore(ct);
        using var tx = connection!.BeginTransaction(deferred: true);
        var result = action(connection, tx);
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        return result;
    }

    private T ReadOnReader<T>(CancellationToken ct, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        var db = readConnection ?? connection!;
        using var tx = db.BeginTransaction(deferred: true);
        var result = action(db, tx);
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        return result;
    }

    private async Task<T> RunAuxReadAsync<T>(Func<SqliteConnection, SqliteTransaction, T> action, CancellationToken ct)
    {
        if (Volatile.Read(ref connection) is null)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { ObjectDisposedException.ThrowIf(disposed, this); InitializeCore(ct); }
            finally { gate.Release(); }
        }
        if (auxReadConnection is null)
            return await RunReadAsync(() => ReadOnReader(ct, action), ct).ConfigureAwait(false);
        await auxReadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var db = auxReadConnection;
                using var tx = db.BeginTransaction(deferred: true);
                var result = action(db, tx);
                ct.ThrowIfCancellationRequested();
                tx.Commit();
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally { auxReadGate.Release(); }
    }

    /// <summary>What a write does to the cached decoded vectors: nothing, specific files, or everything.</summary>
    private enum VectorEffect { All, None, Files }

    private void Write(CancellationToken ct, Action<SqliteConnection, SqliteTransaction> action, VectorEffect effect = VectorEffect.All, params string?[] touchedPaths)
        => WriteCore(ct, (db, tx) => { action(db, tx); return touchedPaths; }, effect, invalidatesLexical: true);

    private void Write(CancellationToken ct, Func<SqliteConnection, SqliteTransaction, IReadOnlyCollection<string?>> action, VectorEffect effect, bool invalidatesLexical = true)
        => WriteCore(ct, action, effect, invalidatesLexical);

    private void WriteCore(CancellationToken ct, Func<SqliteConnection, SqliteTransaction, IReadOnlyCollection<string?>> action, VectorEffect effect, bool invalidatesLexical = true)
    {
        InitializeCore(ct);
        if (!writable) throw new InvalidOperationException("Legacy schema5 index is read-only. Reindex into a new native scratch database; never overwrite the original.");
        using var tx = connection!.BeginTransaction(deferred: false);
        if (!Equals(Scalar(connection, tx, "SELECT value FROM meta WHERE key=$key", ("$key", MarkerKey)), NativeFormat))
            throw new InvalidDataException("Native format marker changed; refusing writes.");
        var touchedPaths = action(connection, tx);
        ct.ThrowIfCancellationRequested();
        // Gate concurrent WAL readers against any vector/header commit window (R2-BLK-01):
        // mark write-in-flight and advance vectorWriteSeq BEFORE tx.Commit(), then finalize dirty state AFTER Commit.
        if (effect != VectorEffect.None)
        {
            lock (vectorCacheSync)
            {
                inflightVectorWrites++;
                vectorWriteSeq++;
                if (invalidatesLexical)
                {
                    lexicalWriteSeq++;
                    lastWordEvidence = null;
                }
            }
        }
        bool committed = false;
        try
        {
            tx.Commit();
            committed = true;
        }
        finally
        {
            if (effect != VectorEffect.None)
            {
                lock (vectorCacheSync)
                {
                    inflightVectorWrites--;
                    vectorWriteSeq++;
                    if (invalidatesLexical)
                    {
                        lexicalWriteSeq++;
                        lastWordEvidence = null;
                    }
                    if (!committed || effect == VectorEffect.All)
                    {
                        vectorCache = null;
                        vectorCacheSeq = vectorWriteSeq;
                        dirtyVectorPaths.Clear();
                        dirtyVectorSeqs.Clear();
                    }
                    else if (effect == VectorEffect.Files && vectorCache is not null)
                    {
                        foreach (var path in touchedPaths)
                        {
                            if (!string.IsNullOrEmpty(path))
                            {
                                dirtyVectorPaths.Add(path);
                                dirtyVectorSeqs[path] = vectorWriteSeq;
                            }
                        }
                    }
                }
            }
        }
    }

    public Task<IReadOnlyList<IndexedFile>> ReadFilesAsync(CancellationToken cancellationToken = default) =>
        RunReadAsync(() => ReadOnReader(cancellationToken, (db, tx) => ReadFiles(db, tx, true, cancellationToken)), cancellationToken);

    public Task<IReadOnlyList<IndexedFile>> ReadFileHeadersAsync(CancellationToken cancellationToken = default) =>
        RunReadAsync(() => ReadOnReader(cancellationToken, (db, tx) => (IReadOnlyList<IndexedFile>)ReadHeaders(db, tx, cancellationToken, includeInfo: false)), cancellationToken);

    public Task<IReadOnlyList<IndexedFile>> ReadFileHeadersByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) return Task.FromResult<IReadOnlyList<IndexedFile>>([]);
        return RunReadAsync(() => ReadOnReader(cancellationToken, (db, tx) => (IReadOnlyList<IndexedFile>)ReadHeaders(db, tx, cancellationToken, includeInfo: true, paths: paths)), cancellationToken);
    }

    public Task<IndexedFile?> GetFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        return RunReadAsync(() => ReadOnReader(cancellationToken, (db, tx) => ReadFiles(db, tx, true, cancellationToken, path).SingleOrDefault()), cancellationToken);
    }

    internal Task<IndexedFile?> GetFileWithoutVectorsAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        return RunReadAsync(() => ReadOnReader(cancellationToken, (db, tx) => ReadFiles(db, tx, false, cancellationToken, path).SingleOrDefault()), cancellationToken);
    }

    private static List<IndexedFile> ReadHeadersWithRootInfo(SqliteConnection db, SqliteTransaction tx, CancellationToken ct, IReadOnlyCollection<string>? paths = null)
    {
        var files = new List<IndexedFile>();
        var rootJsonCache = new Dictionary<string, string>(StringComparer.Ordinal);
        string scope = paths != null ? " WHERE path IN (SELECT value FROM json_each($paths))" : "";
        using var cmd = Command(db, tx, "SELECT id,path,kind,size,mtime,state,detail,json_extract(info,'$.indexRoot') FROM files" + scope + " ORDER BY id",
            ("$paths", paths == null ? null : JsonSerializer.Serialize(paths)));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            string? rootVal = Text(reader, 7);
            string? tinyInfo = null;
            if (!string.IsNullOrEmpty(rootVal))
            {
                if (!rootJsonCache.TryGetValue(rootVal, out tinyInfo))
                {
                    tinyInfo = JsonSerializer.Serialize(new { indexRoot = rootVal });
                    rootJsonCache[rootVal] = tinyInfo;
                }
            }
            files.Add(new IndexedFile(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetDouble(4), reader.GetString(5), Text(reader, 6), tinyInfo, Array.Empty<IndexSegment>()));
        }
        return files;
    }

    private static List<IndexedFile> ReadHeaders(SqliteConnection db, SqliteTransaction tx, CancellationToken ct,
        string? path = null, IReadOnlyCollection<long>? ids = null, bool includeInfo = true, IReadOnlyCollection<string>? paths = null)
    {
        var files = new List<IndexedFile>();
        string scope = path != null ? " WHERE path=$path"
            : ids != null ? " WHERE id IN (SELECT value FROM json_each($ids))"
            : paths != null ? " WHERE path IN (SELECT value FROM json_each($paths))"
            : "";
        string infoCol = includeInfo ? "info" : "NULL";
        using var cmd = Command(db, tx, "SELECT id,path,kind,size,mtime,state,detail," + infoCol + " FROM files" + scope + " ORDER BY id",
            ("$path", path),
            ("$ids", ids == null ? null : JsonSerializer.Serialize(ids)),
            ("$paths", paths == null ? null : JsonSerializer.Serialize(paths)));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            files.Add(new IndexedFile(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetDouble(4), reader.GetString(5), Text(reader, 6), Text(reader, 7), new List<IndexSegment>()));
        }
        if (paths != null && files.Count < paths.Count)
        {
            var matched = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var unmatched = paths.Where(p => !matched.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (unmatched.Length > 0)
            {
                using var ciCmd = Command(db, tx,
                    "SELECT id,path,kind,size,mtime,state,detail," + infoCol + " FROM files WHERE path COLLATE WINSIGHT_PATH IN (SELECT value FROM json_each($unmatched)) ORDER BY id",
                    ("$unmatched", JsonSerializer.Serialize(unmatched)));
                using var ciReader = ciCmd.ExecuteReader();
                while (ciReader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var row = new IndexedFile(ciReader.GetInt64(0), ciReader.GetString(1), ciReader.GetString(2), ciReader.GetInt64(3), ciReader.GetDouble(4), ciReader.GetString(5), Text(ciReader, 6), Text(ciReader, 7), new List<IndexSegment>());
                    if (matched.Add(row.Path))
                        files.Add(row);
                }
            }
        }
        return files;
    }

    private static IReadOnlyList<IndexedFile> ReadFiles(SqliteConnection db, SqliteTransaction tx, bool vectors, CancellationToken ct,
        string? path = null, bool vectorSegmentsOnly = false, IReadOnlyCollection<string>? paths = null)
    {
        var files = ReadHeaders(db, tx, ct, path, paths: paths);
        if (files.Count == 0) return files;
        var byId = files.ToDictionary(f => f.Id);
        var anchors = new Dictionary<long, IReadOnlyDictionary<long, SourceAnchor>>();
        string join = path != null || paths != null ? " JOIN files f ON f.id=s.file" : "";
        string scope = path != null ? " WHERE f.path=$path"
            : paths != null ? " WHERE f.path IN (SELECT value FROM json_each($paths))"
            : "";
        if (vectorSegmentsOnly) scope += scope.Length == 0 ? " WHERE s.vec IS NOT NULL" : " AND s.vec IS NOT NULL";
        using var segments = Command(db, tx, "SELECT s.file,s.kind,s.modality,s.loc,s.loc_end,s.excerpt," + (vectors ? "s.vec" : "NULL") +
            ",s.id FROM segments s" + join + scope + " ORDER BY s.id",
            ("$path", path),
            ("$paths", paths == null ? null : JsonSerializer.Serialize(paths)));
        using var rows = segments.ExecuteReader();
        while (rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            var fileId = rows.GetInt64(0);
            if (!byId.TryGetValue(fileId, out var file)) continue;
            if (!anchors.TryGetValue(fileId, out var fileAnchors))
                anchors[fileId] = fileAnchors = ReadAnchors(file.Info);
            ((List<IndexSegment>)file.Segments).Add(Segment(rows, 1, vectors) with { Source = fileAnchors.GetValueOrDefault(rows.GetInt64(7)) });
        }
        return files;
    }

    public Task UpsertFileAsync(IndexedFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(file.Segments);
        // Own a snapshot of mutable input arrays before crossing the async boundary.
        file = file with { Segments = file.Segments.Select(s => s with { Vector = s.Vector?.ToArray(),
            Source = PrepareSegmentAnchor(file.Kind, s) }).ToArray() };
        ValidateFile(file);
        bool isPendingMarkerOnly = file.State == "pending" && file.Detail is null && file.Info is null && file.Segments.Count == 0;
        return RunAsync(() =>
        {
            if (isPendingMarkerOnly)
                Write(cancellationToken, (db, tx) => { UpsertFileInternal(db, tx, file, cancellationToken); return Array.Empty<string?>(); }, VectorEffect.None, invalidatesLexical: false);
            else
                Write(cancellationToken, (db, tx) => UpsertFileInternal(db, tx, file, cancellationToken), VectorEffect.Files, file.Path);
            return 0;
        }, cancellationToken);
    }

    private static SourceAnchor NormalizeAnchorForStorage(SourceAnchor source) =>
        source with
        {
            Fields = source.Fields is { Count: > 0 } ? source.Fields.ToArray() : null,
            Regions = source.Regions is { Count: > 0 } ? source.Regions.ToArray() : null
        };

    private static SourceAnchor? PrepareSegmentAnchor(string fileKind, IndexSegment s)
    {
        if (s.Source is { } source) return NormalizeAnchorForStorage(source);
        if (s.Excerpt is not { } text) return null;
        var fields = DocumentSearchQuery.FieldsFromText(text);
        return new(
            new(fileKind is "image" or "screenshot" ? "image" : "text"),
            s.Modality == "image" ? "page-visual" : fileKind is "image" or "screenshot" ? "ocr" : "body",
            fields.Count > 0 ? fields : null);
    }

    private static void UpsertFileInternal(SqliteConnection db, SqliteTransaction tx, IndexedFile file, CancellationToken ct)
    {
        if (file.Segments.Any(s => s.Vector != null) && string.IsNullOrWhiteSpace(Scalar(db, tx, "SELECT value FROM meta WHERE key='profile_id'") as string))
            throw new InvalidDataException("Set profile_id before storing any vectors.");
        var existing = Scalar(db, tx, "SELECT id FROM files WHERE path=$path", ("$path", file.Path));
        if (file.Id != 0 && (existing == null || Convert.ToInt64(existing) != file.Id)) throw new InvalidDataException("File ID does not belong to this path.");
        if (file.State == "pending" && file.Detail is null && file.Info is null && file.Segments.Count == 0)
        {
            Execute(db, tx, "INSERT INTO files(path,kind,size,mtime,inode,device,state,detail,info,indexed_at) VALUES($path,$kind,$size,$mtime,0,0,$state,$detail,$info,$now) " +
                "ON CONFLICT(path) DO UPDATE SET kind=excluded.kind,size=excluded.size,mtime=excluded.mtime,state=excluded.state,detail=excluded.detail,info=coalesce(excluded.info,files.info),indexed_at=excluded.indexed_at",
                ("$path", file.Path), ("$kind", file.Kind), ("$size", file.Size), ("$mtime", file.ModifiedTime), ("$state", file.State), ("$detail", file.Detail), ("$info", file.Info), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
            return;
        }
        Execute(db, tx, "INSERT INTO files(path,kind,size,mtime,inode,device,state,detail,info,indexed_at) VALUES($path,$kind,$size,$mtime,0,0,$state,$detail,$info,$now) " +
            "ON CONFLICT(path) DO UPDATE SET kind=excluded.kind,size=excluded.size,mtime=excluded.mtime,state=excluded.state,detail=excluded.detail,info=excluded.info,indexed_at=excluded.indexed_at",
            ("$path", file.Path), ("$kind", file.Kind), ("$size", file.Size), ("$mtime", file.ModifiedTime), ("$state", file.State), ("$detail", file.Detail), ("$info", file.Info), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
        long id = Convert.ToInt64(Scalar(db, tx, "SELECT id FROM files WHERE path=$path", ("$path", file.Path)));
        Execute(db, tx, "DELETE FROM fts WHERE rowid IN (SELECT id FROM segments WHERE file=$id); DELETE FROM fts_seg WHERE rowid IN (SELECT id FROM segments WHERE file=$id); DELETE FROM segments WHERE file=$id;", ("$id", id));

        using var segCmd = db.CreateCommand();
        segCmd.Transaction = tx;
        segCmd.CommandText = "INSERT INTO segments(file,kind,modality,loc,loc_end,excerpt,vec) VALUES($file,$kind,$modality,$loc,$end,$excerpt,$vec)";
        var pFile = segCmd.Parameters.Add("$file", SqliteType.Integer);
        var pKind = segCmd.Parameters.Add("$kind", SqliteType.Text);
        var pModality = segCmd.Parameters.Add("$modality", SqliteType.Text);
        var pLoc = segCmd.Parameters.Add("$loc", SqliteType.Real);
        var pEnd = segCmd.Parameters.Add("$end", SqliteType.Real);
        var pExcerpt = segCmd.Parameters.Add("$excerpt", SqliteType.Text);
        var pVec = segCmd.Parameters.Add("$vec", SqliteType.Blob);
        segCmd.Prepare();

        using var rowIdCmd = db.CreateCommand();
        rowIdCmd.Transaction = tx;
        rowIdCmd.CommandText = "SELECT last_insert_rowid()";
        rowIdCmd.Prepare();

        using var ftsCmd = db.CreateCommand();
        ftsCmd.Transaction = tx;
        ftsCmd.CommandText = "INSERT INTO fts(rowid,name,body,original) VALUES($id,$name,$body,NULL)";
        var pFtsId = ftsCmd.Parameters.Add("$id", SqliteType.Integer);
        var pFtsName = ftsCmd.Parameters.Add("$name", SqliteType.Text);
        var pFtsBody = ftsCmd.Parameters.Add("$body", SqliteType.Text);
        ftsCmd.Prepare();

        using var ftsSegCmd = db.CreateCommand();
        ftsSegCmd.Transaction = tx;
        ftsSegCmd.CommandText = "INSERT INTO fts_seg(rowid,tokens) VALUES($id,$tokens)";
        var pSegId = ftsSegCmd.Parameters.Add("$id", SqliteType.Integer);
        var pSegTokens = ftsSegCmd.Parameters.Add("$tokens", SqliteType.Text);
        ftsSegCmd.Prepare();

        long InsertSegment(IndexSegment segment, string name, string? body, bool int8Quantized = false)
        {
            pFile.Value = id;
            pKind.Value = segment.Kind;
            pModality.Value = segment.Vector == null ? "none" : segment.Modality;
            pLoc.Value = (object?)segment.Location ?? DBNull.Value;
            pEnd.Value = (object?)segment.LocationEnd ?? DBNull.Value;
            pExcerpt.Value = (object?)segment.Excerpt ?? DBNull.Value;
            pVec.Value = segment.Vector == null ? DBNull.Value : Encode(segment.Vector, int8Quantized);
            segCmd.ExecuteNonQuery();
            long segmentId = Convert.ToInt64(rowIdCmd.ExecuteScalar());
            if (name.Length == 0 && string.IsNullOrEmpty(body)) return segmentId;
            pFtsId.Value = segmentId;
            pFtsName.Value = SearchText.Normalize(name);
            pFtsBody.Value = SearchText.Normalize(body ?? "");
            ftsCmd.ExecuteNonQuery();
            // Segmented FTS: the same rules are used at query time, so Chinese terms are real FTS tokens rather
            // than a substring heuristic. A missing segmenter aborts the file instead of leaving half a row.
            pSegId.Value = segmentId;
            pSegTokens.Value = SearchText.SegmentedTokens(name + " " + (body ?? ""));
            ftsSegCmd.ExecuteNonQuery();
            return segmentId;
        }

        string slash = file.Path.Replace('\\', '/');
        int last = slash.LastIndexOf('/');
        InsertSegment(new IndexSegment("name", "none", null, null, null), last < 0 ? slash : slash[(last + 1)..], last < 0 ? "" : slash[..last].Replace('/', ' '));
        var anchors = new Dictionary<long, SourceAnchor>();
        bool int8 = string.Equals(Scalar(db, tx, "SELECT value FROM meta WHERE key='vector_encoding'") as string, "int8", StringComparison.OrdinalIgnoreCase);
        foreach (var segment in file.Segments.Where(s => s.Kind != "name"))
        {
            ct.ThrowIfCancellationRequested();
            var segmentId = InsertSegment(segment, "", segment.Excerpt, int8);
            if (segment.Source is { } source) anchors.Add(segmentId, source);
        }
        if (anchors.Count != 0 || file.Info?.Contains("_sourceAnchorsV1", StringComparison.Ordinal) == true)
        {
            var metadata = file.Info is null ? new JsonObject() : JsonNode.Parse(file.Info)!.AsObject();
            metadata.Remove("_sourceAnchorsV1");
            if (anchors.Count != 0) metadata.Add("_sourceAnchorsV1", JsonSerializer.SerializeToNode(anchors, CompactAnchorJson));
            Execute(db, tx, "UPDATE files SET info=$info WHERE id=$id", ("$info", metadata.ToJsonString()), ("$id", id));
        }
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) => DeleteFileInternal(db, tx, path), VectorEffect.Files, path);
            return 0;
        }, cancellationToken);
    }

    private static void DeleteFileInternal(SqliteConnection db, SqliteTransaction tx, string path)
    {
        Execute(db, tx, "DELETE FROM fts WHERE rowid IN (SELECT s.id FROM segments s JOIN files f ON f.id=s.file WHERE f.path=$path); DELETE FROM fts_seg WHERE rowid IN (SELECT s.id FROM segments s JOIN files f ON f.id=s.file WHERE f.path=$path); DELETE FROM files WHERE path=$path;", ("$path", path));
    }

    public Task ResetHangingTasksAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) =>
            {
                Execute(db, tx, """
                    UPDATE index_tasks SET
                        state = CASE WHEN attempts + 1 >= 4 THEN 'Failed' ELSE 'Pending' END,
                        attempts = CASE WHEN attempts + 1 >= 4 THEN 8 ELSE attempts + 1 END
                    WHERE state = 'Running';
                    UPDATE index_tasks SET state = 'Pending' WHERE state = 'Stabilizing';
                    """);
            }, VectorEffect.None);
            return 0;
        }, cancellationToken);

    public Task EnqueueIndexTaskAsync(IndexTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidatePath(task.Path);
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) =>
            {
                Execute(db, tx, """
                    INSERT INTO index_tasks(path, root, code, kind, tombstone, state, attempts, due_utc, seen_utc, reason)
                    VALUES($path, $root, $code, $kind, $tombstone, $state, $attempts, $due, $seen, $reason)
                    ON CONFLICT(path) DO UPDATE SET
                        root=excluded.root,
                        code=excluded.code,
                        kind=excluded.kind,
                        tombstone=coalesce(excluded.tombstone, index_tasks.tombstone),
                        state=excluded.state,
                        attempts=excluded.attempts,
                        due_utc=excluded.due_utc,
                        seen_utc=index_tasks.seen_utc,
                        reason=excluded.reason
                    """,
                    ("$path", task.Path),
                    ("$root", task.Root),
                    ("$code", task.Code ? 1 : 0),
                    ("$kind", task.Kind),
                    ("$tombstone", task.Tombstone),
                    ("$state", task.State),
                    ("$attempts", task.Attempts),
                    ("$due", task.DueUtc.ToString("O")),
                    ("$seen", task.SeenUtc.ToString("O")),
                    ("$reason", task.Reason));
            }, VectorEffect.None);
            return 0;
        }, cancellationToken);
    }

    /// <summary>Inserts or coalesces many tasks in one transaction; used by reconciliation.</summary>
    public Task EnqueueIndexTasksAsync(IReadOnlyList<IndexTask> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0) return Task.CompletedTask;
        foreach (var task in tasks) { ArgumentNullException.ThrowIfNull(task); ValidatePath(task.Path); }
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) =>
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO index_tasks(path, root, code, kind, tombstone, state, attempts, due_utc, seen_utc, reason)
                    VALUES($path, $root, $code, $kind, $tombstone, $state, $attempts, $due, $seen, $reason)
                    ON CONFLICT(path) DO UPDATE SET
                        root=excluded.root,
                        code=excluded.code,
                        kind=excluded.kind,
                        tombstone=coalesce(excluded.tombstone, index_tasks.tombstone),
                        state=CASE WHEN index_tasks.state = 'Failed' AND index_tasks.attempts >= 8 AND excluded.reason = 'reconcile_backfill' THEN index_tasks.state ELSE excluded.state END,
                        attempts=CASE WHEN index_tasks.state = 'Failed' AND index_tasks.attempts >= 8 AND excluded.reason = 'reconcile_backfill' THEN index_tasks.attempts ELSE excluded.attempts END,
                        due_utc=CASE WHEN index_tasks.state = 'Failed' AND index_tasks.attempts >= 8 AND excluded.reason = 'reconcile_backfill' THEN index_tasks.due_utc ELSE excluded.due_utc END,
                        seen_utc=CASE WHEN excluded.seen_utc < index_tasks.seen_utc THEN excluded.seen_utc ELSE index_tasks.seen_utc END,
                        reason=CASE WHEN index_tasks.state = 'Failed' AND index_tasks.attempts >= 8 AND excluded.reason = 'reconcile_backfill' THEN index_tasks.reason ELSE excluded.reason END
                    """;
                var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
                var pRoot = cmd.Parameters.Add("$root", SqliteType.Text);
                var pCode = cmd.Parameters.Add("$code", SqliteType.Integer);
                var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
                var pTombstone = cmd.Parameters.Add("$tombstone", SqliteType.Text);
                var pState = cmd.Parameters.Add("$state", SqliteType.Text);
                var pAttempts = cmd.Parameters.Add("$attempts", SqliteType.Integer);
                var pDue = cmd.Parameters.Add("$due", SqliteType.Text);
                var pSeen = cmd.Parameters.Add("$seen", SqliteType.Text);
                var pReason = cmd.Parameters.Add("$reason", SqliteType.Text);
                cmd.Prepare();

                foreach (var task in tasks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pPath.Value = task.Path;
                    pRoot.Value = task.Root;
                    pCode.Value = task.Code ? 1 : 0;
                    pKind.Value = task.Kind;
                    pTombstone.Value = (object?)task.Tombstone ?? DBNull.Value;
                    pState.Value = task.State;
                    pAttempts.Value = task.Attempts;
                    pDue.Value = task.DueUtc.ToString("O");
                    pSeen.Value = task.SeenUtc.ToString("O");
                    pReason.Value = (object?)task.Reason ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }, VectorEffect.None);
            return 0;
        }, cancellationToken);
    }

    public Task AckTaskAsync(string taskPath, CancellationToken cancellationToken = default)
    {
        ValidatePath(taskPath);
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) =>
            {
                Execute(db, tx, "DELETE FROM index_tasks WHERE path=$path", ("$path", taskPath));
            }, VectorEffect.None);
            return 0;
        }, cancellationToken);
    }

    public Task AckTasksAsync(IReadOnlyList<string> taskPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskPaths);
        if (taskPaths.Count == 0) return Task.CompletedTask;
        foreach (var taskPath in taskPaths) ValidatePath(taskPath);
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) =>
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM index_tasks WHERE path=$path";
                var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
                cmd.Prepare();
                foreach (var taskPath in taskPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pPath.Value = taskPath;
                    cmd.ExecuteNonQuery();
                }
                return Array.Empty<string?>();
            }, VectorEffect.None, invalidatesLexical: false);
            return 0;
        }, cancellationToken);
    }

    public Task<int> CountFilesUnderAsync(string root, CancellationToken cancellationToken = default)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = normalized + Path.DirectorySeparatorChar;
        return RunAsync(() => Read(cancellationToken, (db, tx) => Convert.ToInt32(Scalar(db, tx,
            "SELECT COUNT(*) FROM files WHERE path = $root COLLATE NOCASE OR substr(path, 1, $length) = $prefix COLLATE NOCASE",
            ("$root", normalized), ("$length", prefix.Length), ("$prefix", prefix)) ?? 0)), cancellationToken);
    }

    public Task<long> CountVectorsAsync(CancellationToken cancellationToken = default) =>
        RunAuxReadAsync((db, tx) => Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM segments WHERE vec IS NOT NULL") ?? 0L), cancellationToken);

    internal Task<(long TotalSegments, long EmbeddedSegments)> GetSegmentVectorCountsAsync(CancellationToken cancellationToken = default) =>
        RunAuxReadAsync((db, tx) =>
        {
            long total = Convert.ToInt64(Scalar(db, tx, $"SELECT COUNT(*) FROM segments WHERE {EmbeddableSegmentPredicate}") ?? 0L);
            long embedded = Convert.ToInt64(Scalar(db, tx, $"SELECT COUNT(*) FROM segments WHERE vec IS NOT NULL AND {EmbeddableSegmentPredicate}") ?? 0L);
            return (total, embedded);
        }, cancellationToken);

    /// <summary>Aggregated failure reasons (no paths) so the UI can explain why files did not index.</summary>
    public Task<IReadOnlyList<string>> ReadFailureSummaryAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit < 1) limit = 1;
        return RunAsync(() => Read(cancellationToken, (db, tx) =>
        {
            using var cmd = Command(db, tx, "SELECT COALESCE(detail,'failed'), COUNT(*) FROM files WHERE state IN ('failed','partial') GROUP BY COALESCE(detail,'failed') ORDER BY COUNT(*) DESC LIMIT $limit", ("$limit", limit));
            using var reader = cmd.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var text = reader.IsDBNull(0) ? "failed" : reader.GetString(0);
                rows.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{reader.GetInt64(1)}x {text}"));
            }
            return (IReadOnlyList<string>)rows;
        }), cancellationToken);
    }

    public Task<IReadOnlyList<IndexTask>> ReadDueTasksAsync(int limit, CancellationToken cancellationToken = default)
    {
        return RunAuxReadAsync((db, tx) =>
        {
            var list = new List<IndexTask>();
            var now = DateTimeOffset.UtcNow.ToString("O");
            using var cmd = Command(db, tx, """
                SELECT path, root, code, kind, tombstone, state, attempts, due_utc, seen_utc, reason
                FROM index_tasks
                WHERE due_utc <= $now AND (state IN ('Pending', 'Stabilizing', 'Ready') OR (state IN ('Failed', 'Deferred') AND attempts < 8))
                ORDER BY due_utc ASC
                LIMIT $limit
                """, ("$now", now), ("$limit", limit));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                list.Add(new IndexTask(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2) != 0,
                    reader.GetString(3),
                    Text(reader, 4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    DateTimeOffset.Parse(reader.GetString(7)),
                    DateTimeOffset.Parse(reader.GetString(8)),
                    Text(reader, 9)));
            }
            return (IReadOnlyList<IndexTask>)list;
        }, cancellationToken);
    }

    public Task<IndexTask?> GetTaskAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        return RunAuxReadAsync((db, tx) =>
        {
            using var cmd = Command(db, tx, """
                SELECT path, root, code, kind, tombstone, state, attempts, due_utc, seen_utc, reason
                FROM index_tasks WHERE path=$path
                """, ("$path", path));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new IndexTask(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2) != 0,
                reader.GetString(3),
                Text(reader, 4),
                reader.GetString(5),
                reader.GetInt32(6),
                DateTimeOffset.Parse(reader.GetString(7)),
                DateTimeOffset.Parse(reader.GetString(8)),
                Text(reader, 9));
        }, cancellationToken);
    }

    public Task UpdateTaskStateAsync(string path, string state, int attempts, DateTimeOffset dueUtc, string? reason = null, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) =>
            {
                Execute(db, tx, "UPDATE index_tasks SET state=$state, attempts=$attempts, due_utc=$due, reason=$reason WHERE path=$path",
                    ("$state", state), ("$attempts", attempts), ("$due", dueUtc.ToString("O")), ("$reason", reason), ("$path", path));
            }, VectorEffect.None);
            return 0;
        }, cancellationToken);
    }

    public Task UpdateTasksStateAsync(IReadOnlyList<IndexTask> tasks, string state, DateTimeOffset dueUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0) return Task.CompletedTask;
        foreach (var task in tasks) { ArgumentNullException.ThrowIfNull(task); ValidatePath(task.Path); }
        return RunAsync(() =>
        {
            var dueStr = dueUtc.ToString("O");
            Write(cancellationToken, (db, tx) =>
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE index_tasks SET state=$state, attempts=$attempts, due_utc=$due, reason=$reason WHERE path=$path";
                var pState = cmd.Parameters.Add("$state", SqliteType.Text);
                var pAttempts = cmd.Parameters.Add("$attempts", SqliteType.Integer);
                var pDue = cmd.Parameters.Add("$due", SqliteType.Text);
                var pReason = cmd.Parameters.Add("$reason", SqliteType.Text);
                var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
                cmd.Prepare();
                foreach (var task in tasks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pState.Value = state;
                    pAttempts.Value = task.Attempts;
                    pDue.Value = dueStr;
                    pReason.Value = (object?)task.Reason ?? DBNull.Value;
                    pPath.Value = task.Path;
                    cmd.ExecuteNonQuery();
                }
                return Array.Empty<string?>();
            }, VectorEffect.None, invalidatesLexical: false);
            return 0;
        }, cancellationToken);
    }

    public Task AckTaskWithUpsertAsync(IndexedFile file, string taskPath, string? tombstone = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(file.Segments);
        ValidatePath(taskPath);
        file = file with { Segments = file.Segments.Select(s => s with { Vector = s.Vector?.ToArray(),
            Source = PrepareSegmentAnchor(file.Kind, s) }).ToArray() };
        ValidateFile(file);
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) =>
            {
                if (!string.IsNullOrEmpty(tombstone) && !string.Equals(tombstone, file.Path, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteFileInternal(db, tx, tombstone);
                    Execute(db, tx, "DELETE FROM index_tasks WHERE path=$tombstone", ("$tombstone", tombstone));
                }
                UpsertFileInternal(db, tx, file, cancellationToken);
                Execute(db, tx, "DELETE FROM index_tasks WHERE path=$taskPath", ("$taskPath", taskPath));
            }, VectorEffect.Files, file.Path, tombstone);
            return 0;
        }, cancellationToken);
    }

    public Task AckTaskWithDeleteAsync(string filePath, string taskPath, CancellationToken cancellationToken = default)
    {
        ValidatePath(filePath);
        ValidatePath(taskPath);
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) =>
            {
                DeleteFileInternal(db, tx, filePath);
                Execute(db, tx, "DELETE FROM index_tasks WHERE path=$taskPath", ("$taskPath", taskPath));
            }, VectorEffect.Files, filePath);
            return 0;
        }, cancellationToken);
    }

    public Task<int> TrimTasksAsync(int maxTasks = 50000, CancellationToken cancellationToken = default)
    {
        return RunAsync(() =>
        {
            var trimmed = 0;
            Write(cancellationToken, (db, tx) =>
            {
                var total = Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM index_tasks"));
                if (total <= maxTasks) return;
                var excess = (int)(total - maxTasks);

                // 1. Backfill first
                var backfillDeleted = Execute(db, tx, """
                    DELETE FROM index_tasks WHERE path IN (
                        SELECT path FROM index_tasks
                        WHERE reason LIKE '%backfill%' OR reason LIKE '%reconcile%' OR kind = 'Reconcile'
                        ORDER BY seen_utc ASC LIMIT $limit
                    )
                    """, ("$limit", excess));
                trimmed += backfillDeleted;
                excess -= backfillDeleted;
                if (excess <= 0) return;

                // 2. Oldest Deferred next
                var deferredDeleted = Execute(db, tx, """
                    DELETE FROM index_tasks WHERE path IN (
                        SELECT path FROM index_tasks
                        WHERE state = 'Deferred'
                        ORDER BY seen_utc ASC LIMIT $limit
                    )
                    """, ("$limit", excess));
                trimmed += deferredDeleted;
                excess -= deferredDeleted;
                if (excess <= 0) return;

                // 3. Oldest remaining
                var oldestDeleted = Execute(db, tx, """
                    DELETE FROM index_tasks WHERE path IN (
                        SELECT path FROM index_tasks
                        ORDER BY seen_utc ASC LIMIT $limit
                    )
                    """, ("$limit", excess));
                trimmed += oldestDeleted;
            }, VectorEffect.None);
            return trimmed;
        }, cancellationToken);
    }

    public Task<int> CountRetryableFailedTasksAsync(CancellationToken cancellationToken = default) =>
        RunAuxReadAsync((db, tx) =>
            Convert.ToInt32(Scalar(db, tx, "SELECT COUNT(*) FROM index_tasks WHERE state IN ('Failed', 'Deferred') AND attempts < 8") ?? 0), cancellationToken);

    public Task<IReadOnlyDictionary<string, int>> GetTaskCountsAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(() => Read(cancellationToken, (db, tx) =>
        {
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var cmd = Command(db, tx, "SELECT state, count(*) FROM index_tasks GROUP BY state");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                dict[reader.GetString(0)] = reader.GetInt32(1);
            }
            return (IReadOnlyDictionary<string, int>)dict;
        }), cancellationToken);
    }

    public Task SaveUsnCheckpointAsync(string volumeSerial, string journalId, string nextUsn, DateTimeOffset reconcileUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeSerial);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextUsn);
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) =>
            {
                Execute(db, tx, """
                    INSERT INTO usn_checkpoints(volume_serial, journal_id, next_usn, reconcile_utc)
                    VALUES($serial, $jid, $next, $rec)
                    ON CONFLICT(volume_serial) DO UPDATE SET
                        journal_id=excluded.journal_id,
                        next_usn=excluded.next_usn,
                        reconcile_utc=excluded.reconcile_utc
                    """,
                    ("$serial", volumeSerial),
                    ("$jid", journalId),
                    ("$next", nextUsn),
                    ("$rec", reconcileUtc.ToString("o")));
            }, VectorEffect.None);
            return 0;
        }, cancellationToken);
    }

    public Task<(string JournalId, string NextUsn, DateTimeOffset ReconcileUtc)?> GetUsnCheckpointAsync(string volumeSerial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeSerial);
        return RunReadAsync(() =>
        {
            return ReadOnReader(cancellationToken, (db, tx) =>
            {
                using var cmd = Command(db, tx, "SELECT journal_id, next_usn, reconcile_utc FROM usn_checkpoints WHERE volume_serial=$serial", ("$serial", volumeSerial));
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    string jid = reader.GetString(0);
                    string next = reader.GetString(1);
                    DateTimeOffset rec = DateTimeOffset.Parse(reader.GetString(2));
                    return ((string JournalId, string NextUsn, DateTimeOffset ReconcileUtc)?)(jid, next, rec);
                }
                return null;
            });
        }, cancellationToken);
    }

    public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return RunReadAsync(() => ReadOnReader(cancellationToken, (db, tx) => Scalar(db, tx, "SELECT value FROM meta WHERE key=$key", ("$key", key)) as string), cancellationToken);
    }

    public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key == MarkerKey) throw new InvalidOperationException("Native format marker is repository-owned.");
        if (key == "profile_id" && string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Profile must not be empty.");
        if (key == "embedding_dimension" && value != "768") throw new InvalidDataException("Dimension must be 768.");
        if (key == "embedding_info")
        {
            var info = JsonSerializer.Deserialize<EmbeddingInfo>(value, MetadataJson) ?? throw new InvalidDataException("Missing embedding info.");
            if (info.Dimension != VectorContract.Dimension || string.IsNullOrWhiteSpace(info.ProfileId)) throw new InvalidDataException("Invalid embedding info.");
        }
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) =>
            {
                string? profile = Scalar(db, tx, "SELECT value FROM meta WHERE key='profile_id'") as string;
                string? infoJson = Scalar(db, tx, "SELECT value FROM meta WHERE key='embedding_info'") as string;
                if (key == "profile_id")
                {
                    if (profile != null && profile != value && !FilePolicy.IsCompatibleProfile(value, profile) && Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM segments WHERE vec IS NOT NULL")) > 0)
                        throw new InvalidOperationException("Cannot change profile while vectors exist; create a separate new index.");
                    if (infoJson != null)
                    {
                        var existing = JsonSerializer.Deserialize<EmbeddingInfo>(infoJson, MetadataJson);
                        if (existing != null && existing.ProfileId != value && !FilePolicy.IsCompatibleProfile(value, existing.ProfileId))
                            throw new InvalidDataException("Profile conflicts with embedding_info.");
                    }
                }
                if (key == "embedding_info" && profile != null)
                {
                    var incoming = JsonSerializer.Deserialize<EmbeddingInfo>(value, MetadataJson);
                    if (incoming != null && incoming.ProfileId != profile && !FilePolicy.IsCompatibleProfile(incoming.ProfileId, profile))
                        throw new InvalidDataException("embedding_info conflicts with profile_id.");
                }
                Execute(db, tx, "INSERT INTO meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value));
            }, VectorEffect.All);
            return 0;
        }, cancellationToken);
    }

    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) => RunAuxReadAsync((db, tx) =>
    {
        int total = 0, done = 0, pending = 0, partial = 0, failed = 0;
        using var cmd = Command(db, tx, "SELECT state,count(*) FROM files GROUP BY state");
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = rows.GetInt32(1); total += count;
            switch (rows.GetString(0)) { case "done": done += count; break; case "pending": pending += count; break; case "partial": partial += count; break; case "failed": failed += count; break; }
        }
        return new EngineStatus(total, done, pending, partial, failed, DatabasePath, writable ? null : "Legacy schema5: read-only; native reindex required for writes.");
    }, cancellationToken);

    public Task<int> ResetFailedFilesAsync(CancellationToken ct = default) => RunAsync(() =>
    {
        var count = 0;
        var isCode = string.Equals(Path.GetFileName(DatabasePath), "code.sqlite", StringComparison.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow.ToString("O");
        const string recoverablePredicate = """
            state = 'failed'
            OR (state = 'skipped' AND detail LIKE '%because the isolated reader host is unavailable%')
            OR (state = 'partial' AND (
                detail LIKE '%Vision feature count differs%'
                OR detail LIKE '%Native image inspection is not configured%'
                OR detail LIKE '%visual embedding failed%'
                OR detail LIKE '%Semantic extraction unavailable%'
                OR detail LIKE '%no embedding service%'
            ))
            """;
        Write(ct, (db, tx) =>
        {
            Execute(db, tx, $"""
                INSERT INTO index_tasks(path, root, code, kind, tombstone, state, attempts, due_utc, seen_utc, reason)
                SELECT path, coalesce(json_extract(info, '$.indexRoot'), path), $code, 'Update', NULL, 'Ready', 0, $now, $now, 'retry_failed'
                FROM files
                WHERE {recoverablePredicate}
                ON CONFLICT(path) DO UPDATE SET state='Ready', attempts=0, due_utc=$now, reason='retry_failed'
                """, ("$code", isCode ? 1 : 0), ("$now", now));

            using var cmd = Command(db, tx, $"UPDATE files SET state = 'pending', detail = NULL WHERE {recoverablePredicate}");
            int filesReset = cmd.ExecuteNonQuery();
            using var resetTasksCmd = Command(db, tx, "UPDATE index_tasks SET state = 'Ready', attempts = 0, due_utc = $now, reason = NULL WHERE state IN ('Failed', 'Deferred') AND reason IS NOT 'retry_failed'", ("$now", now));
            int tasksReset = resetTasksCmd.ExecuteNonQuery();
            count = filesReset + tasksReset;
        }, VectorEffect.All, []);
        return count;
    }, ct);

    public Task<IReadOnlyList<KeywordMatch>> KeywordSearchAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        ValidateQuery(query, limit);
        return RunReadAsync(() => ReadOnReader(cancellationToken, (db, tx) => (IReadOnlyList<KeywordMatch>)FindWords(db, tx, query, null, null, limit, cancellationToken, fileResults: true)
            .Select(w => new KeywordMatch(w.FileId, w.Rank)).ToArray()), cancellationToken);
    }

    /// <summary>Releases in-memory decoded vectors and returns unneeded SQLite cache pages under system memory pressure.</summary>
    public void TrimMemory()
    {
        lock (vectorCacheSync)
        {
            vectorWriteSeq++;
            lexicalWriteSeq++;
            vectorCache = null;
            vectorCacheSeq = vectorWriteSeq;
            lastWordEvidence = null;
            dirtyVectorPaths.Clear();
            dirtyVectorSeqs.Clear();
        }
        if (gate.Wait(200))
        {
            try
            {
                if (connection != null && !disposed)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "PRAGMA shrink_memory;";
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
            finally { gate.Release(); }
        }
        if (readGate.Wait(200))
        {
            try
            {
                if (readConnection != null && !disposed)
                {
                    using var cmd = readConnection.CreateCommand();
                    cmd.CommandText = "PRAGMA shrink_memory;";
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
            finally { readGate.Release(); }
        }
        if (auxReadGate.Wait(200))
        {
            try
            {
                if (auxReadConnection != null && !disposed)
                {
                    using var cmd = auxReadConnection.CreateCommand();
                    cmd.CommandText = "PRAGMA shrink_memory;";
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
            finally { auxReadGate.Release(); }
        }
    }

    private static string ComputeUnitString(SourceAnchor? anchor, string kind, double? loc, double? locEnd)
    {
        if (anchor is not null)
        {
            var l = anchor.Location;
            return l.Type is "text" or "image"
                ? "file"
                : $"{l.Type}:{l.Page}:{l.Paragraph}:{l.Sheet}:{l.Range}:{l.Part}";
        }
        return kind is "name" or "chunk"
            ? "file"
            : $"{kind}:{loc}:{locEnd}";
    }

    private static CompactVectorIndex BuildCompactVectorIndex(SqliteConnection db, SqliteTransaction tx, CancellationToken ct, IReadOnlyCollection<string>? onlyPaths = null)
    {
        var headerList = ReadHeaders(db, tx, ct, includeInfo: false, paths: onlyPaths);
        var headerDict = new Dictionary<long, IndexedFile>(headerList.Count);
        foreach (var h in headerList) headerDict[h.Id] = h;
        var index = new CompactVectorIndex(headerDict);
        AppendCompactVectors(db, tx, index, ct, onlyPaths);
        return index;
    }

    private static bool TryParsePositiveInt64Utf8(ReadOnlySpan<byte> utf8, out long value)
    {
        value = 0;
        if (utf8.IsEmpty) return false;
        foreach (byte b in utf8)
        {
            uint d = (uint)(b - (byte)'0');
            if (d > 9 || value > (long.MaxValue - d) / 10) { value = 0; return false; }
            value = value * 10 + d;
        }
        return value > 0;
    }

    private static string ReadChannelString(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("body"u8)) return "body";
        if (reader.ValueTextEquals("table"u8)) return "table";
        if (reader.ValueTextEquals("notes"u8)) return "notes";
        if (reader.ValueTextEquals("ocr"u8)) return "ocr";
        if (reader.ValueTextEquals("page-visual"u8)) return "page-visual";
        if (reader.ValueTextEquals("metadata"u8)) return "metadata";
        return reader.GetString()!;
    }

    private static string ReadLocTypeString(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("text"u8)) return "text";
        if (reader.ValueTextEquals("image"u8)) return "image";
        if (reader.ValueTextEquals("paragraph"u8)) return "paragraph";
        if (reader.ValueTextEquals("pdf-page"u8)) return "pdf-page";
        if (reader.ValueTextEquals("slide"u8)) return "slide";
        if (reader.ValueTextEquals("sheet"u8)) return "sheet";
        return reader.GetString() ?? "";
    }

    private static bool TryParseAnchorUnitFromReader(ref Utf8JsonReader reader, out string channel, out ulong unitHash)
    {
        channel = "body";
        unitHash = 0;
        if (reader.TokenType != JsonTokenType.StartObject) return false;
        bool hasLocation = false;
        string type = "";
        int? page = null, para = null;
        string? sheet = null, range = null, part = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) return false;
            bool isChannel = reader.ValueTextEquals("Channel"u8) || reader.ValueTextEquals("channel"u8);
            bool isLocation = !isChannel && (reader.ValueTextEquals("Location"u8) || reader.ValueTextEquals("location"u8));
            if (!reader.Read()) return false;
            if (isChannel)
            {
                if (reader.TokenType == JsonTokenType.String)
                    channel = ReadChannelString(ref reader);
                else
                    reader.Skip();
            }
            else if (isLocation && reader.TokenType == JsonTokenType.StartObject)
            {
                hasLocation = true;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) return false;
                    int fieldKind =
                        reader.ValueTextEquals("Type"u8) || reader.ValueTextEquals("type"u8) ? 1 :
                        reader.ValueTextEquals("Page"u8) || reader.ValueTextEquals("page"u8) ? 2 :
                        reader.ValueTextEquals("Paragraph"u8) || reader.ValueTextEquals("paragraph"u8) ? 3 :
                        reader.ValueTextEquals("Sheet"u8) || reader.ValueTextEquals("sheet"u8) ? 4 :
                        reader.ValueTextEquals("Range"u8) || reader.ValueTextEquals("range"u8) ? 5 :
                        reader.ValueTextEquals("Part"u8) || reader.ValueTextEquals("part"u8) ? 6 : 0;
                    if (!reader.Read()) return false;
                    switch (fieldKind)
                    {
                        case 1 when reader.TokenType == JsonTokenType.String:
                            type = ReadLocTypeString(ref reader);
                            break;
                        case 2 when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int pv):
                            page = pv;
                            break;
                        case 3 when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int prv):
                            para = prv;
                            break;
                        case 4 when reader.TokenType == JsonTokenType.String:
                            sheet = reader.GetString();
                            break;
                        case 5 when reader.TokenType == JsonTokenType.String:
                            range = reader.GetString();
                            break;
                        case 6 when reader.TokenType == JsonTokenType.String:
                            part = reader.GetString();
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }
                if (reader.TokenType != JsonTokenType.EndObject) return false;
            }
            else
            {
                reader.Skip();
            }
        }
        if (reader.TokenType != JsonTokenType.EndObject) return false;
        if (!hasLocation || type is "text" or "image")
        {
            unitHash = FileUnitHash;
        }
        else
        {
            Span<char> buf = stackalloc char[128];
            unitHash = buf.TryWrite(System.Globalization.CultureInfo.InvariantCulture, $"{type}:{page}:{para}:{sheet}:{range}:{part}", out int written)
                ? CompactVectorIndex.ComputeUnitHash(buf[..written])
                : CompactVectorIndex.ComputeUnitHash($"{type}:{page}:{para}:{sheet}:{range}:{part}");
        }
        return true;
    }

    private static bool TryApplyAnchorUnitsFast(string info, CompactVectorIndex index, Dictionary<long, int> segToGlobal)
    {
        int byteCount = info.Length <= 32768 ? Encoding.UTF8.GetMaxByteCount(info.Length) : Encoding.UTF8.GetByteCount(info);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            int written = Encoding.UTF8.GetBytes(info, rented);
            var reader = new Utf8JsonReader(rented.AsSpan(0, written));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            bool sawAnchors = false;
            List<(int GlobalIdx, ulong UnitHash, string Channel)>? pendingUpdates = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) return false;
                if (reader.ValueTextEquals("_sourceAnchorsV1"u8))
                {
                    if (sawAnchors) return false;
                    sawAnchors = true;
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName) return false;
                        if (reader.ValueIsEscaped) return false;
                        bool validId = TryParsePositiveInt64Utf8(reader.ValueSpan, out long segId);
                        if (!reader.Read()) return false;
                        if (validId && reader.TokenType == JsonTokenType.StartObject && segToGlobal.TryGetValue(segId, out int globalIdx))
                        {
                            if (!TryParseAnchorUnitFromReader(ref reader, out string channel, out ulong unitHash))
                                return false;
                            pendingUpdates ??= new List<(int, ulong, string)>();
                            pendingUpdates.Add((globalIdx, unitHash, channel));
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                    if (reader.TokenType != JsonTokenType.EndObject) return false;
                }
                else
                {
                    if (!reader.Read()) return false;
                    reader.Skip();
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return false;
            if (pendingUpdates is not null)
            {
                foreach (var u in pendingUpdates)
                    index.UpdateAnchorMeta(u.GlobalIdx, u.UnitHash, index.InternChannel(u.Channel));
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static Dictionary<long, (string Channel, ulong UnitHash)> ReadAnchorUnitsOnly(string info)
    {
        var map = new Dictionary<long, (string Channel, ulong UnitHash)>();
        using var doc = JsonDocument.Parse(info);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
        if (!doc.RootElement.TryGetProperty("_sourceAnchorsV1", out var anchors) || anchors.ValueKind != JsonValueKind.Object) return map;
        foreach (var prop in anchors.EnumerateObject())
        {
            if (!long.TryParse(prop.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long segId) || segId <= 0)
                continue;
            var el = prop.Value;
            if (el.ValueKind != JsonValueKind.Object) continue;
            string channel = Prop(el, "Channel", "channel", out var chEl) && chEl.ValueKind == JsonValueKind.String ? chEl.GetString()! : "body";
            ulong unitHash;
            if (Prop(el, "Location", "location", out var locEl) && locEl.ValueKind == JsonValueKind.Object)
            {
                string type = Prop(locEl, "Type", "type", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString()! : "";
                if (type is "text" or "image")
                {
                    unitHash = CompactVectorIndex.ComputeUnitHash("file");
                }
                else
                {
                    int? page = Prop(locEl, "Page", "page", out var pEl) && pEl.ValueKind == JsonValueKind.Number && pEl.TryGetInt32(out var pv) ? pv : null;
                    int? para = Prop(locEl, "Paragraph", "paragraph", out var prEl) && prEl.ValueKind == JsonValueKind.Number && prEl.TryGetInt32(out var prv) ? prv : null;
                    string? sheet = Prop(locEl, "Sheet", "sheet", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
                    string? range = Prop(locEl, "Range", "range", out var rEl) && rEl.ValueKind == JsonValueKind.String ? rEl.GetString() : null;
                    string? part = Prop(locEl, "Part", "part", out var ptEl) && ptEl.ValueKind == JsonValueKind.String ? ptEl.GetString() : null;
                    unitHash = CompactVectorIndex.ComputeUnitHash($"{type}:{page}:{para}:{sheet}:{range}:{part}");
                }
            }
            else
            {
                unitHash = CompactVectorIndex.ComputeUnitHash("file");
            }
            map[segId] = (channel, unitHash);
        }
        return map;
    }

    private static void AppendCompactVectors(SqliteConnection db, SqliteTransaction tx, CompactVectorIndex index, CancellationToken ct, IReadOnlyCollection<string>? paths = null)
    {
        string? pathsJson = paths == null ? null : JsonSerializer.Serialize(paths);
        string sql = paths is null
            ? "SELECT id, file, kind, modality, loc, loc_end, vec FROM segments WHERE vec IS NOT NULL"
            : "SELECT s.id, s.file, s.kind, s.modality, s.loc, s.loc_end, s.vec FROM segments s JOIN files f ON f.id = s.file WHERE s.vec IS NOT NULL AND f.path IN (SELECT value FROM json_each($paths))";
        var segToGlobal = new Dictionary<long, int>();
        var vecFiles = new HashSet<long>();
        using (var cmd = Command(db, tx, sql, ("$paths", pathsJson)))
        using (var rows = cmd.ExecuteReader())
        {
            float[] scratch = new float[CompactVectorIndex.Dimension];
            sbyte[] quantized = new sbyte[CompactVectorIndex.Dimension];
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                long segId = rows.GetInt64(0);
                long fileId = rows.GetInt64(1);
                string kind = rows.GetString(2);
                string modality = rows.GetString(3);
                double? loc = Number(rows, 4);
                double? locEnd = Number(rows, 5);
                byte[] blob = (byte[])rows.GetValue(6);
                CompactVectorIndex.QuantizeFromBlob(blob, scratch, quantized, out float scale);
                string fallbackChannel = modality == "image" ? "page-visual" : modality;
                ulong fallbackUnitHash = kind is "name" or "chunk"
                    ? FileUnitHash
                    : CompactVectorIndex.ComputeUnitHash($"{kind}:{loc}:{locEnd}");
                byte channelId = index.InternChannel(fallbackChannel);
                int globalIdx = index.Count;
                index.Add(segId, fileId, fallbackUnitHash, channelId, quantized, scale);
                segToGlobal[segId] = globalIdx;
                vecFiles.Add(fileId);
            }
        }
        if (segToGlobal.Count == 0) return;

        string infoSql = paths is null
            ? "SELECT id, info FROM files WHERE info IS NOT NULL AND instr(info, '_sourceAnchorsV1') > 0 AND id IN (SELECT DISTINCT file FROM segments WHERE vec IS NOT NULL)"
            : "SELECT id, info FROM files WHERE info IS NOT NULL AND instr(info, '_sourceAnchorsV1') > 0 AND path IN (SELECT value FROM json_each($paths))";
        using var infoCmd = Command(db, tx, infoSql, ("$paths", pathsJson));
        using var infoRows = infoCmd.ExecuteReader();
        while (infoRows.Read())
        {
            ct.ThrowIfCancellationRequested();
            long fileId = infoRows.GetInt64(0);
            if (!vecFiles.Contains(fileId)) continue;
            string info = infoRows.GetString(1);
            if (!TryApplyAnchorUnitsFast(info, index, segToGlobal))
            {
                foreach (var kv in ReadAnchorUnitsOnly(info))
                {
                    if (segToGlobal.TryGetValue(kv.Key, out int globalIdx))
                        index.UpdateAnchorMeta(globalIdx, kv.Value.UnitHash, index.InternChannel(kv.Value.Channel));
                }
            }
        }
    }

    private static CompactVectorIndex PatchCompactVectorIndex(SqliteConnection db, SqliteTransaction tx, CompactVectorIndex cached, IReadOnlyCollection<string> dirty, CancellationToken ct)
    {
        if (dirty.Count == 0) return cached;
        var working = cached.CloneForMutation();
        foreach (var path in dirty) working.MarkFileRemoved(path);
        var refreshedHeaders = ReadHeaders(db, tx, ct, includeInfo: false, paths: dirty);
        foreach (var h in refreshedHeaders) working.UpsertHeader(h);
        if (refreshedHeaders.Count > 0)
            AppendCompactVectors(db, tx, working, ct, dirty);
        return working.NeedsCompaction ? working.Compact() : working;
    }

    private bool IsVectorSeqUnchanged(long seqBefore)
    {
        lock (vectorCacheSync)
        {
            return inflightVectorWrites == 0 && vectorWriteSeq == seqBefore && vectorCache is not null;
        }
    }

    private bool IsLexicalSeqUnchanged(long lexSeqBefore)
    {
        lock (vectorCacheSync)
        {
            return lexicalWriteSeq == lexSeqBefore && lastWordEvidence is not null;
        }
    }

    private long TryGetConnDataVersion()
    {
        if (Volatile.Read(ref connection) is null) return -1;
        if (!gate.Wait(50)) return -2;
        try
        {
            var conn = Volatile.Read(ref connection);
            if (disposed || conn is null) return -1;
            return Convert.ToInt64(Scalar(conn, null, "PRAGMA data_version"));
        }
        catch { return -2; }
        finally { gate.Release(); }
    }

    internal Task<CompactSearchSnapshot> ReadCompactSearchSnapshotAsync(string query, string? kind, string? root, bool vectors, bool words, int limit, CancellationToken ct,
        Func<string, bool>? authorize = null, Func<string, string?, long, bool>? matchesSource = null,
        bool allHeaders = false, bool includeHeaderInfo = false, bool needsSourceAnchors = true, string? filterSignature = null) => RunReadAsync(() => ReadOnReader(ct, (db, tx) =>
    {
        // Capture cache & write-sequence state BEFORE opening the WAL read snapshot on the first SQL query,
        // guaranteeing the SQLite snapshot is at least as new as seqBefore.
        (long ReadVersion, long ConnVersion, CompactVectorIndex Index)? cachedSnapshot = null;
        (long ReadVersion, long ConnVersion, long LexicalSeq, string Query, string? Kind, string? Root, int WordLimit, bool NeedsSourceAnchors, Func<string, bool>? Authorize, IReadOnlyList<WordEvidence> Matches, string? FilterSignature)? cachedWord = null;
        string[] dirtySnapshot = [];
        long seqBefore = 0;
        long lexSeqBefore = 0;
        int dirtyCountSnapshot = 0;
        bool writeInFlightAtStart;
        lock (vectorCacheSync)
        {
            seqBefore = vectorWriteSeq;
            lexSeqBefore = lexicalWriteSeq;
            writeInFlightAtStart = inflightVectorWrites > 0;
            if (!writeInFlightAtStart)
            {
                cachedSnapshot = vectorCache;
                cachedWord = lastWordEvidence;
                dirtyCountSnapshot = dirtyVectorPaths.Count;
                if (vectors)
                {
                    dirtySnapshot = dirtyVectorPaths.ToArray();
                }
            }
        }

        long connVersionBefore = (vectors || allHeaders) ? TryGetConnDataVersion() : -1;
        long before = Convert.ToInt64(Scalar(db, tx, "PRAGMA data_version"));
        var profile = Scalar(db, tx, "SELECT value FROM meta WHERE key='profile_id'") as string;
        long version = Convert.ToInt64(Scalar(db, tx, "PRAGMA data_version"));
        long connVersionAtPin = (vectors || allHeaders) ? TryGetConnDataVersion() : -1;
        long pinnedConnVersion = connVersionBefore >= 0 && connVersionAtPin == connVersionBefore ? connVersionBefore : -1;
        int wordLimit = Math.Max(100, limit);
        bool canCacheWords = matchesSource is null || filterSignature is not null;
        IReadOnlyList<WordEvidence> matches;
        if (!words)
        {
            matches = [];
        }
        else if (canCacheWords && cachedWord is { } w
            && w.WordLimit == wordLimit
            && w.NeedsSourceAnchors == needsSourceAnchors
            && string.Equals(w.FilterSignature, filterSignature, StringComparison.Ordinal)
            && ReferenceEquals(w.Authorize, authorize)
            && string.Equals(w.Query, query, StringComparison.Ordinal)
            && string.Equals(w.Kind, kind, StringComparison.Ordinal)
            && string.Equals(w.Root, root, StringComparison.Ordinal)
            && ((before == version && w.ReadVersion == version)
                || (!writeInFlightAtStart && w.ConnVersion >= 0 && pinnedConnVersion >= 0 && pinnedConnVersion == w.ConnVersion
                    && w.LexicalSeq == lexSeqBefore && IsLexicalSeqUnchanged(lexSeqBefore))))
        {
            matches = w.Matches;
        }
        else
        {
            matches = FindWords(db, tx, query, kind, root, wordLimit, ct, authorize, matchesSource, needsSourceAnchors: needsSourceAnchors);
            if (canCacheWords && before == version && !writeInFlightAtStart)
            {
                lock (vectorCacheSync)
                {
                    if (inflightVectorWrites == 0 && lexicalWriteSeq == lexSeqBefore)
                    {
                        lastWordEvidence = (version, pinnedConnVersion, lexSeqBefore, query, kind, root, wordLimit, needsSourceAnchors, authorize, matches, filterSignature);
                    }
                }
            }
        }
        CompactVectorIndex? compactIndex = null;
        IReadOnlyDictionary<long, IndexedFile> headers;
        if (vectors)
        {
            long connVersion = pinnedConnVersion;
            if (cachedSnapshot is { } cached && cached.ReadVersion == version && dirtySnapshot.Length == 0)
            {
                compactIndex = cached.Index;
                if (connVersion < 0 && cached.ConnVersion >= 0 && TryGetConnDataVersion() == cached.ConnVersion)
                    connVersion = cached.ConnVersion;
            }
            else if (cachedSnapshot is { } cachedDirty && dirtySnapshot.Length != 0
                && cachedDirty.ConnVersion >= 0 && pinnedConnVersion >= 0 && pinnedConnVersion == cachedDirty.ConnVersion)
            {
                compactIndex = PatchCompactVectorIndex(db, tx, cachedDirty.Index, dirtySnapshot, ct);
            }
            else if (cachedSnapshot is { } cachedCheck && dirtySnapshot.Length == 0
                && cachedCheck.ConnVersion >= 0 && pinnedConnVersion >= 0 && pinnedConnVersion == cachedCheck.ConnVersion
                && IsVectorSeqUnchanged(seqBefore))
            {
                // Local VectorEffect.None writes (index_tasks / usn_checkpoints) bump readConnection's data_version
                // without changing connection's external data_version; skip full vector rebuild only when vectorWriteSeq did not advance.
                compactIndex = cachedCheck.Index;
            }
            else
            {
                compactIndex = BuildCompactVectorIndex(db, tx, ct);
            }

            if (!writeInFlightAtStart)
            {
                lock (vectorCacheSync)
                {
                    if (inflightVectorWrites == 0 && vectorWriteSeq == seqBefore)
                    {
                        vectorCache = (version, connVersion, compactIndex);
                        vectorCacheSeq = seqBefore;
                        dirtyVectorPaths.Clear();
                        dirtyVectorSeqs.Clear();
                    }
                    else if (vectorCache is not null && seqBefore >= vectorCacheSeq)
                    {
                        foreach (var p in dirtySnapshot)
                        {
                            if (dirtyVectorSeqs.TryGetValue(p, out var pSeq) && pSeq <= seqBefore)
                            {
                                dirtyVectorPaths.Remove(p);
                                dirtyVectorSeqs.Remove(p);
                            }
                        }
                        // Invalidate read-version pin while preserving snapshot-pinned connVersion so external writes stay visible.
                        vectorCache = (-1, connVersion, compactIndex);
                        vectorCacheSeq = seqBefore;
                    }
                }
            }
            if (!includeHeaderInfo)
            {
                headers = compactIndex.Files;
            }
            else
            {
                var infoList = ReadHeadersWithRootInfo(db, tx, ct);
                var infoDict = new Dictionary<long, IndexedFile>(infoList.Count);
                foreach (var f in infoList) infoDict[f.Id] = f;
                headers = infoDict;
            }
        }
        else if (allHeaders && !includeHeaderInfo && matchesSource == null && cachedSnapshot is { } vCached && dirtyCountSnapshot == 0
            && ((before == version && vCached.ReadVersion == version)
                || (!writeInFlightAtStart && vCached.ConnVersion >= 0 && pinnedConnVersion >= 0 && pinnedConnVersion == vCached.ConnVersion && IsVectorSeqUnchanged(seqBefore))))
        {
            headers = vCached.Index.Files;
        }
        else
        {
            var list = includeHeaderInfo && !needsSourceAnchors
                ? ReadHeadersWithRootInfo(db, tx, ct)
                : ReadHeaders(db, tx, ct, ids: allHeaders ? null : matches.Select(w => w.FileId).ToArray(), includeInfo: includeHeaderInfo || (matchesSource != null && needsSourceAnchors));
            var dict = new Dictionary<long, IndexedFile>(list.Count);
            foreach (var f in list) dict[f.Id] = f;
            headers = dict;
        }
        return new CompactSearchSnapshot(headers, compactIndex, matches, profile);
    }), ct);

    internal Task<Dictionary<long, (IndexedFile File, IndexSegment Segment)>> HydrateSemanticHitsAsync(IReadOnlyList<long> segmentIds, CancellationToken ct)
    {
        if (segmentIds.Count == 0) return Task.FromResult(new Dictionary<long, (IndexedFile, IndexSegment)>());
        return RunReadAsync(() => ReadOnReader(ct, (db, tx) =>
        {
            var idsJson = JsonSerializer.Serialize(segmentIds);
            var rowsData = new List<(long SegId, long FileId, string Kind, string Modality, double? Loc, double? LocEnd, string? Excerpt, string Path, string FKind, long Size, double MTime, string State, string? Detail, string? Info)>(segmentIds.Count);
            var idsByFile = new Dictionary<long, HashSet<long>>();
            using var cmd = Command(db, tx,
                "SELECT s.id, s.file, s.kind, s.modality, s.loc, s.loc_end, s.excerpt, f.path, f.kind, f.size, f.mtime, f.state, f.detail, f.info " +
                "FROM segments s JOIN files f ON f.id = s.file WHERE s.id IN (SELECT value FROM json_each($ids))",
                ("$ids", idsJson));
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                long segId = rows.GetInt64(0);
                long fileId = rows.GetInt64(1);
                rowsData.Add((segId, fileId, rows.GetString(2), rows.GetString(3), Number(rows, 4), Number(rows, 5), Text(rows, 6),
                    rows.GetString(7), rows.GetString(8), rows.GetInt64(9), rows.GetDouble(10), rows.GetString(11), Text(rows, 12), Text(rows, 13)));
                if (!idsByFile.TryGetValue(fileId, out var set))
                    idsByFile[fileId] = set = [];
                set.Add(segId);
            }
            var result = new Dictionary<long, (IndexedFile, IndexSegment)>(rowsData.Count);
            var fileCache = new Dictionary<long, (IndexedFile File, IReadOnlyDictionary<long, SourceAnchor> Anchors)>(idsByFile.Count);
            foreach (var r in rowsData)
            {
                if (!fileCache.TryGetValue(r.FileId, out var fc))
                {
                    var f = new IndexedFile(r.FileId, r.Path, r.FKind, r.Size, r.MTime, r.State, r.Detail, r.Info, Array.Empty<IndexSegment>());
                    fc = (f, ReadAnchors(r.Info, idsByFile[r.FileId]));
                    fileCache[r.FileId] = fc;
                }
                var seg = new IndexSegment(r.Kind, r.Modality, r.Loc, r.LocEnd, r.Excerpt, Source: fc.Anchors.GetValueOrDefault(r.SegId));
                result[r.SegId] = (fc.File, seg);
            }
            return result;
        }), ct);
    }

    internal Task<HashSet<long>> FilterVectorSegmentsAsync(IReadOnlyDictionary<long, IndexedFile> scopedFiles, DocumentSearchQuery plan, CancellationToken ct)
    {
        if (scopedFiles.Count == 0) return Task.FromResult(new HashSet<long>());
        return RunReadAsync(() => ReadOnReader(ct, (db, tx) =>
        {
            var allowed = new HashSet<long>();
            bool hasNonPathFilter = plan.Filters.Any(f => f.Name is not ("name" or "folder" or "folder1" or "folder2" or "folder3"));
            Dictionary<long, IndexedFile> pathEligibleFiles;
            if (!hasNonPathFilter && plan.Filters.Count > 0)
            {
                pathEligibleFiles = new Dictionary<long, IndexedFile>(scopedFiles.Count);
                foreach (var kv in scopedFiles)
                    if (plan.Matches(kv.Value.Path, kv.Value.Info, null, checkLiterals: false))
                        pathEligibleFiles[kv.Key] = kv.Value;
                if (pathEligibleFiles.Count == 0) return allowed;
            }
            else
            {
                pathEligibleFiles = scopedFiles as Dictionary<long, IndexedFile> ?? new Dictionary<long, IndexedFile>(scopedFiles);
            }

            Dictionary<long, IndexedFile> remainingFiles = pathEligibleFiles;
            if (!hasNonPathFilter && plan.Literals.Length > 0)
            {
                long fullMask = plan.Literals.Length >= 64 ? -1L : (1L << plan.Literals.Length) - 1;
                var pathMatchedFids = new List<long>();
                remainingFiles = new Dictionary<long, IndexedFile>();
                bool anyPartialPathMatch = false;
                foreach (var kv in pathEligibleFiles)
                {
                    long pMask = DocumentSearchQuery.PathLiteralMask(kv.Value.Path, plan.Literals);
                    if (pMask == fullMask)
                        pathMatchedFids.Add(kv.Key);
                    else
                    {
                        if (pMask != 0) anyPartialPathMatch = true;
                        remainingFiles[kv.Key] = kv.Value;
                    }
                }
                if (pathMatchedFids.Count > 0)
                {
                    bool hasVecIdx = Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='ix_segments_vec'")) == 1;
                    string vecHint = hasVecIdx ? " INDEXED BY ix_segments_vec" : "";
                    using var pmCmd = Command(db, tx,
                        $"SELECT s.id FROM segments s{vecHint} WHERE s.vec IS NOT NULL AND s.file IN (SELECT value FROM json_each($fids))",
                        ("$fids", JsonSerializer.Serialize(pathMatchedFids)));
                    using var pmRows = pmCmd.ExecuteReader();
                    while (pmRows.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        allowed.Add(pmRows.GetInt64(0));
                    }
                }
                if (remainingFiles.Count == 0) return allowed;

                var litSubTokens = SearchText.Words(string.Join(' ', plan.Literals));
                if (litSubTokens.Length > 0)
                {
                    string litOp = anyPartialPathMatch && plan.Literals.Length > 1 ? " OR " : " ";
                    string litQuery = string.Join(litOp, litSubTokens.Select(SearchText.Quote));
                    using var ftsLitCmd = Command(db, tx,
                        "SELECT s.id, s.file, s.excerpt FROM fts JOIN segments s ON s.id = fts.rowid WHERE fts MATCH $litQ AND s.vec IS NOT NULL",
                        ("$litQ", litQuery));
                    using var ftsLitRows = ftsLitCmd.ExecuteReader();
                    while (ftsLitRows.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        long segId = ftsLitRows.GetInt64(0);
                        long fid = ftsLitRows.GetInt64(1);
                        if (!remainingFiles.TryGetValue(fid, out var file)) continue;
                        string? excerpt = Text(ftsLitRows, 2);
                        if (DocumentSearchQuery.LiteralMask(excerpt, file.Path, plan.Literals) == fullMask)
                            allowed.Add(segId);
                    }
                    return allowed;
                }
            }

            var anchorsCache = new Dictionary<long, IReadOnlyDictionary<long, SourceAnchor>>();
            bool needAnchorInfo = hasNonPathFilter || plan.Literals.Length > 0;
            bool useTargetedFileScope = remainingFiles.Count < scopedFiles.Count || remainingFiles.Count <= 2048;
            if (needAnchorInfo)
            {
                using var infoCmd = useTargetedFileScope
                    ? Command(db, tx,
                        "SELECT id, info FROM files WHERE id IN (SELECT value FROM json_each($fids)) AND info IS NOT NULL AND instr(info, '_sourceAnchorsV1') > 0",
                        ("$fids", JsonSerializer.Serialize(remainingFiles.Keys)))
                    : Command(db, tx,
                        "SELECT id, info FROM files WHERE info IS NOT NULL AND instr(info, '_sourceAnchorsV1') > 0 AND id IN (SELECT DISTINCT file FROM segments WHERE vec IS NOT NULL)");
                using var infoRows = infoCmd.ExecuteReader();
                while (infoRows.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    long fid = infoRows.GetInt64(0);
                    if (!remainingFiles.ContainsKey(fid)) continue;
                    anchorsCache[fid] = ReadAnchors(infoRows.GetString(1));
                }
            }

            using var cmd = useTargetedFileScope
                ? Command(db, tx,
                    "SELECT id, file, excerpt FROM segments WHERE vec IS NOT NULL AND file IN (SELECT value FROM json_each($fids))",
                    ("$fids", JsonSerializer.Serialize(remainingFiles.Keys)))
                : Command(db, tx, "SELECT id, file, excerpt FROM segments WHERE vec IS NOT NULL");
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                long segId = rows.GetInt64(0);
                long fid = rows.GetInt64(1);
                if (!remainingFiles.TryGetValue(fid, out var file)) continue;
                string? excerpt = Text(rows, 2);
                SourceAnchor? source = anchorsCache.TryGetValue(fid, out var map) ? map.GetValueOrDefault(segId) : null;
                if (plan.Matches(file.Path, file.Info, source, excerpt))
                    allowed.Add(segId);
            }
            return allowed;
        }), ct);
    }

    internal Task<IReadOnlyList<IndexedFile>> ReadFilteredVectorFilesAsync(IReadOnlyList<long> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0) return Task.FromResult<IReadOnlyList<IndexedFile>>([]);
        return RunReadAsync(() => ReadOnReader(ct, (db, tx) =>
        {
            var idsJson = JsonSerializer.Serialize(fileIds);
            var list = ReadHeaders(db, tx, ct, ids: fileIds, includeInfo: true);
            if (list.Count == 0) return (IReadOnlyList<IndexedFile>)list;
            var byId = list.ToDictionary(f => f.Id);
            var anchors = new Dictionary<long, IReadOnlyDictionary<long, SourceAnchor>>();
            using var cmd = Command(db, tx,
                "SELECT s.file, s.kind, s.modality, s.loc, s.loc_end, s.excerpt, s.vec, s.id " +
                "FROM segments s WHERE s.vec IS NOT NULL AND s.file IN (SELECT value FROM json_each($ids)) ORDER BY s.id",
                ("$ids", idsJson));
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                long fileId = rows.GetInt64(0);
                if (!byId.TryGetValue(fileId, out var file)) continue;
                if (!anchors.TryGetValue(fileId, out var fileAnchors))
                    anchors[fileId] = fileAnchors = ReadAnchors(file.Info);
                ((List<IndexSegment>)file.Segments).Add(Segment(rows, 1, true) with { Source = fileAnchors.GetValueOrDefault(rows.GetInt64(7)) });
            }
            return (IReadOnlyList<IndexedFile>)list;
        }), ct);
    }

    private static IReadOnlyList<WordEvidence> FindWords(SqliteConnection db, SqliteTransaction tx, string query, string? kind, string? root, int limit, CancellationToken ct,
        Func<string, bool>? authorize = null, Func<string, string?, long, bool>? matchesSource = null, bool fileResults = false, bool needsSourceAnchors = true)
    {
        bool code = query.StartsWith("code:", StringComparison.OrdinalIgnoreCase);
        if (code) query = query[5..].Trim();
        var identifier = code ? SearchText.Identifier(query) : null;
        // Latin terms use the literal (porter) index; CJK tokens come from the shared segmenter and use the segmented
        // index. Neither path scans the vocabulary: both are plain FTS5 MATCH lookups, and both produce a BM25 score
        // BEFORE any truncation.
        (string[] Latin, string[] Cjk) lexical = identifier != null ? (Array.Empty<string>(), Array.Empty<string>()) : SearchText.LexicalTerms(query);
        var latinTerms = lexical.Latin;
        var literals = DocumentSearchQuery.LiteralTokens(query);
        var literalJson = JsonSerializer.Serialize(literals);
        long requiredMask = (1L << literals.Length) - 1;
        var patterns = identifier != null ? new[] { SearchText.Quote(string.Join(' ', identifier)) } : latinTerms.Select(t => SearchText.Quote(t) + (SearchText.Inflects(t) ? "*" : "")).ToArray();
        var segPatterns = lexical.Cjk.Select(SearchText.Quote).ToArray();
        for (int i = 0; literals.Length == 0 && identifier == null && i < latinTerms.Length; i++)
            if (latinTerms[i].EnumerateRunes().Count() >= 3 && !patterns[i].EndsWith('*') &&
                Scalar(db, tx, "SELECT 1 FROM fts WHERE fts MATCH $q LIMIT 1", ("$q", patterns[i])) == null) patterns[i] += "*";
        for (int i = 0; identifier == null && i < lexical.Cjk.Length; i++)
        {
            if (lexical.Cjk[i].EnumerateRunes().Count() >= 3 &&
                Scalar(db, tx, "SELECT 1 FROM fts_seg WHERE fts_seg MATCH $q LIMIT 1", ("$q", segPatterns[i])) == null)
            {
                var bigrams = SearchText.CjkBigrams(lexical.Cjk[i]);
                if (bigrams.Length >= 2)
                    segPatterns[i] = "(" + string.Join(" ", bigrams.Select(SearchText.Quote)) + ")";
            }
        }
        if (patterns.Length == 0 && segPatterns.Length == 0) return [];
        int termCount = patterns.Length + segPatterns.Length;
        string scope = " AND ($kind IS NULL OR f.kind=$kind) AND ($root IS NULL OR replace(f.path,char(92),'/')=$root COLLATE WINSIGHT_PATH OR substr(replace(f.path,char(92),'/'),1,length($prefix))=$prefix COLLATE WINSIGHT_PATH)";
        string maskSql = "CASE WHEN $literals='[]' THEN 0 ELSE WINSIGHT_LITERAL_MASK(coalesce(fts.body,fts.original),f.path,$literals) END";
        string? normalizedRoot = root?.Replace('\\', '/').TrimEnd('/');
        var candidateIndexByKey = new Dictionary<(long File, string Unit), int>();
        var candidateList = new List<(long File, string Unit, long Segment, double Rank, long Mask, ulong TermMask, int BestSegMatches, int BestLitMatches)>();
        var segmentInfo = new Dictionary<long, (int CandIdx, double Rank, long Mask, byte Flags, ulong TermMask)>();
        var sources = new Dictionary<long, IReadOnlyDictionary<long, SourceAnchor>>();
        bool needInfo = matchesSource != null;
        bool needAnchors = needInfo && needsSourceAnchors;
        bool needPath = authorize != null || matchesSource != null || literals.Length != 0;
        bool needFileJoin = needPath || needInfo || kind != null || normalizedRoot != null;
        string infoCol = needAnchors ? "f.info" : needInfo ? "json_extract(f.info,'$.indexRoot')" : "NULL";
        var rootJsonCache = needInfo && !needAnchors ? new Dictionary<string, string>(StringComparer.Ordinal) : null;
        bool hasCovering = !needInfo && Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='index' AND name IN ('ix_segments_meta','ix_files_meta')")) == 2;
        string segJoinHint = hasCovering ? " INDEXED BY ix_segments_meta" : "";
        string fileJoinHint = hasCovering ? " INDEXED BY ix_files_meta" : "";
        string fileJoinClause = needFileJoin ? " JOIN files f" + fileJoinHint + " ON f.id=s.file" : "";
        string whereScope = needFileJoin ? scope : "";
        string pathCol = needPath ? "f.path" : "''";
        string ftsMaskSql = literals.Length == 0 ? "0" : maskSql;
        void Collect(SqliteDataReader rows, bool literal, ulong knownTermMask)
        {
            byte litFlag = literal ? (byte)1 : (byte)2;
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                long segment = rows.GetInt64(1);
                long mask = literals.Length == 0 ? 0L : rows.GetInt64(8);
                double rawRank = rows.GetDouble(2);
                double rowRank = rawRank / (1.0 + Math.Abs(rawRank));
                ref var existingSeg = ref CollectionsMarshal.GetValueRefOrAddDefault(segmentInfo, segment, out bool segExists);
                if (segExists)
                {
                    existingSeg.Rank += rowRank;
                    existingSeg.Mask |= mask;
                    existingSeg.Flags |= litFlag;
                    existingSeg.TermMask |= knownTermMask;
                    continue;
                }
                long file = rows.GetInt64(0);
                var path = needPath ? rows.GetString(3) : "";
                string? rawInfo = needInfo ? Text(rows, 4) : null;
                string? rowInfo = rawInfo;
                if (rootJsonCache is not null && !string.IsNullOrEmpty(rawInfo))
                {
                    if (!rootJsonCache.TryGetValue(rawInfo, out rowInfo))
                    {
                        rowInfo = JsonSerializer.Serialize(new { indexRoot = rawInfo });
                        rootJsonCache[rawInfo] = rowInfo;
                    }
                }
                if (authorize != null && !authorize(path) || matchesSource != null && !matchesSource(path, rowInfo, segment))
                {
                    segmentInfo.Remove(segment);
                    continue;
                }
                SourceAnchor? source = null;
                if (needAnchors)
                {
                    if (!sources.TryGetValue(file, out var anchors)) sources[file] = anchors = ReadAnchors(rawInfo);
                    source = anchors.GetValueOrDefault(segment);
                }
                string unit;
                if (source is not null)
                {
                    var loc = source.Location;
                    unit = loc.Type is "text" or "image"
                        ? "file"
                        : $"{loc.Type}:{loc.Page}:{loc.Paragraph}:{loc.Sheet}:{loc.Range}:{loc.Part}";
                }
                else
                {
                    string segKind = rows.GetString(5);
                    unit = segKind is "name" or "chunk"
                        ? "file"
                        : $"{segKind}:{Number(rows, 6)}:{Number(rows, 7)}";
                }
                var key = (file, unit);
                ref int candIdx = ref CollectionsMarshal.GetValueRefOrAddDefault(candidateIndexByKey, key, out bool candExists);
                if (!candExists)
                {
                    candIdx = candidateList.Count;
                    candidateList.Add((file, unit, -1, 0, 0, 0UL, 0, 0));
                }
                // BM25 contributions are additive per term, so a query split across two indexes sums their scores.
                existingSeg = (candIdx, rowRank, mask, litFlag, knownTermMask);
            }
        }
        int candCap = Math.Clamp(limit * 20, 2000, 15000);
        bool usedAndFastPath = false;
        int EligibleCandidateCount()
        {
            if (literals.Length == 0) return candidateList.Count;
            var eligibleCands = new HashSet<int>();
            foreach (var (_, info) in segmentInfo)
                if (info.Mask == requiredMask) eligibleCands.Add(info.CandIdx);
            return eligibleCands.Count;
        }
        void ResetCollected()
        {
            segmentInfo.Clear();
            candidateList.Clear();
            candidateIndexByKey.Clear();
            sources.Clear();
        }
        if (patterns.Length > 1 && segPatterns.Length == 0 && patterns.Length <= 8)
        {
            using var andCmd = Command(db, tx, "SELECT s.file,s.id,bm25(fts,3.0,1.0)," + pathCol + "," + infoCol + ",s.kind,s.loc,s.loc_end," + ftsMaskSql + " FROM fts " +
                "JOIN segments s" + segJoinHint + " ON s.id=fts.rowid" + fileJoinClause + " WHERE fts MATCH $q" + whereScope + " ORDER BY bm25(fts,3.0,1.0) LIMIT $cap",
                ("$literals", literalJson), ("$q", string.Join(" ", patterns)), ("$kind", kind), ("$root", normalizedRoot), ("$prefix", normalizedRoot == null ? null : normalizedRoot + "/"), ("$cap", candCap));
            using var andRows = andCmd.ExecuteReader();
            ulong allMask = (1UL << patterns.Length) - 1;
            Collect(andRows, true, allMask);
            if (EligibleCandidateCount() >= limit) usedAndFastPath = true;
            else ResetCollected();
        }
        else if (segPatterns.Length > 1 && patterns.Length == 0 && segPatterns.Length <= 8)
        {
            string join = literals.Length == 0 ? "" : " JOIN fts ON fts.rowid=s.id";
            using var andCmd = Command(db, tx, "SELECT s.file,s.id,bm25(fts_seg)," + pathCol + "," + infoCol + ",s.kind,s.loc,s.loc_end," + ftsMaskSql + " FROM fts_seg " +
                "JOIN segments s" + segJoinHint + " ON s.id=fts_seg.rowid" + join + fileJoinClause + " WHERE fts_seg MATCH $q" + whereScope + " ORDER BY bm25(fts_seg) LIMIT $cap",
                ("$literals", literalJson), ("$q", string.Join(" ", segPatterns)), ("$kind", kind), ("$root", normalizedRoot), ("$prefix", normalizedRoot == null ? null : normalizedRoot + "/"), ("$cap", candCap));
            using var andRows = andCmd.ExecuteReader();
            ulong allMask = (1UL << segPatterns.Length) - 1;
            Collect(andRows, false, allMask);
            if (EligibleCandidateCount() >= limit) usedAndFastPath = true;
            else ResetCollected();
        }
        if (!usedAndFastPath && patterns.Length > 0)
        {
            using var cmd = Command(db, tx, "SELECT s.file,s.id,bm25(fts,3.0,1.0)," + pathCol + "," + infoCol + ",s.kind,s.loc,s.loc_end," + ftsMaskSql + " FROM fts " +
                "JOIN segments s" + segJoinHint + " ON s.id=fts.rowid" + fileJoinClause + " WHERE fts MATCH $q" + whereScope + " ORDER BY bm25(fts,3.0,1.0) LIMIT $cap",
                ("$literals", literalJson), ("$q", string.Join(" OR ", patterns)), ("$kind", kind), ("$root", normalizedRoot), ("$prefix", normalizedRoot == null ? null : normalizedRoot + "/"), ("$cap", candCap));
            using var rows = cmd.ExecuteReader();
            Collect(rows, true, patterns.Length == 1 ? 1UL : 0UL);
        }
        if (!usedAndFastPath && segPatterns.Length > 0)
        {
            // fts_seg holds jieba index-mode tokens with real term frequency; the literal-mask column needs the fts row.
            string join = literals.Length == 0 ? "" : " JOIN fts ON fts.rowid=s.id";
            using var cmd = Command(db, tx, "SELECT s.file,s.id,bm25(fts_seg)," + pathCol + "," + infoCol + ",s.kind,s.loc,s.loc_end," + ftsMaskSql + " FROM fts_seg " +
                "JOIN segments s" + segJoinHint + " ON s.id=fts_seg.rowid" + join + fileJoinClause + " WHERE fts_seg MATCH $q" + whereScope + " ORDER BY bm25(fts_seg) LIMIT $cap",
                ("$literals", literalJson), ("$q", string.Join(" OR ", segPatterns)), ("$kind", kind), ("$root", normalizedRoot), ("$prefix", normalizedRoot == null ? null : normalizedRoot + "/"), ("$cap", candCap));
            using var rows = cmd.ExecuteReader();
            ulong segKnownMask = segPatterns.Length == 1 ? (patterns.Length < 64 ? 1UL << patterns.Length : 1UL << 63) : 0UL;
            Collect(rows, false, segKnownMask);
        }
        if (segmentInfo.Count == 0) return [];
        if (!usedAndFastPath && (patterns.Length > 1 || segPatterns.Length > 1))
        {
            var candIdsJson = JsonSerializer.Serialize(segmentInfo.Keys);
            void Coverage(string table, string pattern, ulong termBit)
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = Command(db, tx, $"SELECT rowid FROM {table} WHERE {table} MATCH $q AND rowid IN (SELECT value FROM json_each($candIds))",
                    ("$q", pattern), ("$candIds", candIdsJson));
                using var rows = cmd.ExecuteReader();
                while (rows.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    ref var seg = ref CollectionsMarshal.GetValueRefOrNullRef(segmentInfo, rows.GetInt64(0));
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref seg))
                        seg.TermMask |= termBit;
                }
            }
            if (patterns.Length > 1)
                for (int i = 0; i < patterns.Length; i++)
                    Coverage("fts", patterns[i], i < 64 ? 1UL << i : 1UL << 63);
            if (segPatterns.Length > 1)
                for (int i = 0; i < segPatterns.Length; i++)
                {
                    int bitIdx = patterns.Length + i;
                    Coverage("fts_seg", segPatterns[i], bitIdx < 64 ? 1UL << bitIdx : 1UL << 63);
                }
        }
        var candArray = candidateList.ToArray();
        foreach (var (segment, info) in segmentInfo)
        {
            ref var cand = ref candArray[info.CandIdx];
            cand.TermMask |= info.TermMask;
            int segMatches = System.Numerics.BitOperations.PopCount(info.TermMask);
            int litMatches = literals.Length == 0 ? 0 : System.Numerics.BitOperations.PopCount((uint)info.Mask);
            double boost = segMatches > 1 ? 1.0 + 0.25 * (segMatches - 1) : 1.0;
            double effectiveRank = segMatches > 1 ? (info.Rank < 0 ? info.Rank * boost : info.Rank / boost) : info.Rank;
            if (cand.Segment < 0)
            {
                cand.Segment = segment;
                cand.Rank = effectiveRank;
                cand.BestSegMatches = segMatches;
                cand.BestLitMatches = litMatches;
                cand.Mask = info.Mask;
            }
            else
            {
                bool better = litMatches > cand.BestLitMatches
                    || (litMatches == cand.BestLitMatches && (segMatches > cand.BestSegMatches
                        || (segMatches == cand.BestSegMatches && (effectiveRank < cand.Rank || (effectiveRank == cand.Rank && segment < cand.Segment)))));
                if (better)
                {
                    cand.Segment = segment;
                    cand.Rank = effectiveRank;
                    cand.BestSegMatches = segMatches;
                    cand.BestLitMatches = litMatches;
                }
                cand.Mask |= info.Mask;
            }
        }
        int effectiveTermCount = Math.Min(termCount, 64);
        int CompareCandIdx(int aIdx, int bIdx)
        {
            ref readonly var a = ref candArray[aIdx];
            ref readonly var b = ref candArray[bIdx];
            int aMatches = effectiveTermCount == 1 ? 1 : System.Numerics.BitOperations.PopCount(a.TermMask);
            int bMatches = effectiveTermCount == 1 ? 1 : System.Numerics.BitOperations.PopCount(b.TermMask);
            int cmp = bMatches.CompareTo(aMatches);
            if (cmp != 0) return cmp;
            cmp = a.Rank.CompareTo(b.Rank);
            if (cmp != 0) return cmp;
            cmp = a.File.CompareTo(b.File);
            if (cmp != 0) return cmp;
            return StringComparer.Ordinal.Compare(a.Unit, b.Unit);
        }
        int[] selectedIndices;
        if (fileResults)
        {
            var bestByFile = new Dictionary<long, int>();
            for (int i = 0; i < candArray.Length; i++)
            {
                ref readonly var c = ref candArray[i];
                if (c.Segment < 0 || c.Mask != requiredMask) continue;
                if (!bestByFile.TryGetValue(c.File, out int prevIdx) || CompareCandIdx(i, prevIdx) < 0)
                    bestByFile[c.File] = i;
            }
            var fileSelected = new List<int>(bestByFile.Values);
            fileSelected.Sort(CompareCandIdx);
            if (fileSelected.Count > limit) fileSelected.RemoveRange(limit, fileSelected.Count - limit);
            selectedIndices = fileSelected.ToArray();
        }
        else
        {
            var eligible = new List<int>(candArray.Length);
            for (int i = 0; i < candArray.Length; i++)
            {
                ref readonly var c = ref candArray[i];
                if (c.Segment >= 0 && c.Mask == requiredMask) eligible.Add(i);
            }
            eligible.Sort(CompareCandIdx);
            if (eligible.Count > limit)
            {
                const int perFileSoftCap = 4;
                var primary = new List<int>(limit);
                var overflow = new List<int>();
                var fileCounts = new Dictionary<long, int>();
                foreach (int idx in eligible)
                {
                    long fileId = candArray[idx].File;
                    int count = fileCounts.GetValueOrDefault(fileId);
                    fileCounts[fileId] = count + 1;
                    if (count < perFileSoftCap)
                    {
                        primary.Add(idx);
                        if (primary.Count == limit) break;
                    }
                    else
                    {
                        overflow.Add(idx);
                    }
                }
                if (primary.Count < limit && overflow.Count > 0)
                {
                    int need = Math.Min(limit - primary.Count, overflow.Count);
                    for (int i = 0; i < need; i++) primary.Add(overflow[i]);
                }
                selectedIndices = primary.ToArray();
            }
            else
            {
                selectedIndices = eligible.ToArray();
            }
        }
        if (selectedIndices.Length == 0) return [];
        // Hydrate anchors only for the selected winning files and segments, rather than thousands of non-winning candidates.
        var selectedIdsByFile = new Dictionary<long, HashSet<long>>();
        var selectedBySegment = new Dictionary<long, int>(selectedIndices.Length);
        var selectedSegments = new long[selectedIndices.Length];
        for (int sIdx = 0; sIdx < selectedIndices.Length; sIdx++)
        {
            int candIdx = selectedIndices[sIdx];
            ref readonly var cand = ref candArray[candIdx];
            selectedSegments[sIdx] = cand.Segment;
            selectedBySegment[cand.Segment] = candIdx;
            if (sources.ContainsKey(cand.File)) continue;
            if (!selectedIdsByFile.TryGetValue(cand.File, out var set))
                selectedIdsByFile[cand.File] = set = [];
            set.Add(cand.Segment);
        }
        if (selectedIdsByFile.Count > 0)
        {
            var missingFileIdsJson = JsonSerializer.Serialize(selectedIdsByFile.Keys);
            using var infoCmd = Command(db, tx, "SELECT id, info FROM files WHERE id IN (SELECT value FROM json_each($fids))", ("$fids", missingFileIdsJson));
            using var infoRows = infoCmd.ExecuteReader();
            while (infoRows.Read())
            {
                ct.ThrowIfCancellationRequested();
                long fileId = infoRows.GetInt64(0);
                string? infoText = Text(infoRows, 1);
                sources[fileId] = ReadAnchors(infoText, selectedIdsByFile[fileId]);
            }
            foreach (var fileId in selectedIdsByFile.Keys)
            {
                if (!sources.ContainsKey(fileId))
                    sources[fileId] = new Dictionary<long, SourceAnchor>();
            }
        }
        var ids = JsonSerializer.Serialize(selectedSegments);
        var evidence = new Dictionary<long, WordEvidence>(selectedIndices.Length);
        using (var cmd = Command(db, tx, "SELECT id,file,kind,modality,loc,loc_end,excerpt FROM segments WHERE id IN (SELECT value FROM json_each($ids))", ("$ids", ids)))
        using (var rows = cmd.ExecuteReader())
        {
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                long id = rows.GetInt64(0), file = rows.GetInt64(1);
                int candIdx = selectedBySegment[id];
                ref readonly var cand = ref candArray[candIdx];
                var segEntry = segmentInfo[id];
                var segment = Segment(rows, 2, false) with { Source = sources[file].GetValueOrDefault(id) };
                int candMatches = effectiveTermCount == 1 ? 1 : System.Numerics.BitOperations.PopCount(cand.TermMask);
                double coverage = Math.Min(1.0, (double)candMatches / effectiveTermCount);
                evidence.Add(id, new(file, cand.Rank, coverage, segment, null,
                    ExactLiteralsVerified: (segEntry.Flags & 1) != 0, Segmented: (segEntry.Flags & 2) != 0));
            }
        }
        return selectedSegments.Select(segId => evidence.TryGetValue(segId, out var word) ? word
            : throw new InvalidDataException("Selected keyword source disappeared from its SQLite snapshot.")).ToArray();
    }

    internal static void ValidateQuery(string query, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length > 4096 || query.Contains('\0')) throw new ArgumentException("Query must be at most 4096 characters and contain no NUL.", nameof(query));
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0')) throw new ArgumentException("Path contains NUL.", nameof(path));
    }

    private static void ValidateFile(IndexedFile file)
    {
        ValidatePath(file.Path);
        if (file.Id < 0 || file.Size < 0 || !double.IsFinite(file.ModifiedTime)) throw new InvalidDataException("Invalid file metadata.");
        if (file.Kind is not ("doc" or "pdf" or "image" or "screenshot" or "video" or "audio" or "code")) throw new InvalidDataException("Unknown file kind.");
        if (file.State is not ("pending" or "done" or "partial" or "skipped" or "failed")) throw new InvalidDataException("Unknown file state.");
        if (file.Info != null) { using var info = JsonDocument.Parse(file.Info); if (info.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("File info must be a JSON object."); }
        ArgumentNullException.ThrowIfNull(file.Segments);
        foreach (var s in file.Segments)
        {
            if (s.Kind is not ("name" or "image" or "ocr" or "page" or "page_image" or "chunk" or "frame" or "audio" or "lines" or "cells")) throw new InvalidDataException("Unknown segment kind.");
            if (s.Kind == "name" && (s.Vector != null || s.Modality != "none")) throw new InvalidDataException("Name segments are keyword-only and repository-owned.");
            if (s.Modality is not ("none" or "text" or "image" or "audio")) throw new InvalidDataException("Unknown modality.");
            if (s.Location is { } loc && (!double.IsFinite(loc) || loc < 0) || s.LocationEnd is { } end && (!double.IsFinite(end) || end < 0 || end < s.Location)) throw new InvalidDataException("Invalid segment location.");
            if (s.Vector != null) { VectorContract.Validate(s.Vector); if (s.Modality == "none") throw new InvalidDataException("A vector must declare its modality."); }
            if (s.Source is { } source) ValidateAnchor(source);
        }
    }

    internal static IReadOnlyDictionary<long, SourceAnchor> ReadAnchors(string? info, IReadOnlySet<long>? onlyIds = null)
    {
        if (info is null) return new Dictionary<long, SourceAnchor>();
        if (onlyIds is { Count: > 0 } && info.Length > 2048 && TryParseTargetedAnchors(info, onlyIds, out var targeted))
            return targeted;
        using var document = JsonDocument.Parse(info);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("File info must be an object.");
        if (!document.RootElement.TryGetProperty("_sourceAnchorsV1", out var value)) return new Dictionary<long, SourceAnchor>();
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Malformed source-anchor metadata.");
        var result = new Dictionary<long, SourceAnchor>();
        var seenIds = onlyIds is null ? null : new HashSet<long>();
        int count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (++count > 5000 || !long.TryParse(property.Name, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0 || (seenIds is not null ? !seenIds.Add(id) : result.ContainsKey(id)))
                throw new InvalidDataException("Invalid source-anchor key/count.");
            if (onlyIds is not null && !onlyIds.Contains(id)) continue;
            var source = ParseAnchor(property.Value);
            ValidateAnchor(source);
            result.Add(id, source);
        }
        return result;
    }

    private static bool TryParseTargetedAnchors(string info, IReadOnlySet<long> onlyIds, out Dictionary<long, SourceAnchor> result)
    {
        result = new Dictionary<long, SourceAnchor>(onlyIds.Count);
        int byteCount = info.Length <= 32768 ? Encoding.UTF8.GetMaxByteCount(info.Length) : Encoding.UTF8.GetByteCount(info);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            int written = Encoding.UTF8.GetBytes(info, rented);
            var reader = new Utf8JsonReader(rented.AsSpan(0, written));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            bool sawAnchors = false;
            HashSet<long>? seenIds = null;
            int count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) return false;
                if (reader.ValueTextEquals("_sourceAnchorsV1"u8))
                {
                    if (sawAnchors) return false;
                    sawAnchors = true;
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                    seenIds = new HashSet<long>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName || reader.ValueIsEscaped) return false;
                        if (++count > 5000 || !TryParsePositiveInt64Utf8(reader.ValueSpan, out long id) || !seenIds.Add(id))
                            return false;
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                        if (onlyIds.Contains(id))
                        {
                            using var doc = JsonDocument.ParseValue(ref reader);
                            var source = ParseAnchor(doc.RootElement);
                            ValidateAnchor(source);
                            result.Add(id, source);
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                    if (reader.TokenType != JsonTokenType.EndObject) return false;
                }
                else
                {
                    if (!reader.Read()) return false;
                    reader.Skip();
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool Prop(JsonElement el, string pascal, string camel, out JsonElement val) =>
        el.TryGetProperty(pascal, out val) || el.TryGetProperty(camel, out val);

    private static SourceAnchor ParseAnchor(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Missing source anchor.");
        if (!Prop(el, "Location", "location", out var locEl) || locEl.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing source location.");
        var type = Prop(locEl, "Type", "type", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString()! : "";
        int? page = Prop(locEl, "Page", "page", out var pEl) && pEl.ValueKind == JsonValueKind.Number && pEl.TryGetInt32(out var pv) ? pv : null;
        int? para = Prop(locEl, "Paragraph", "paragraph", out var prEl) && prEl.ValueKind == JsonValueKind.Number && prEl.TryGetInt32(out var prv) ? prv : null;
        string? sheet = Prop(locEl, "Sheet", "sheet", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
        string? range = Prop(locEl, "Range", "range", out var rEl) && rEl.ValueKind == JsonValueKind.String ? rEl.GetString() : null;
        string? part = Prop(locEl, "Part", "part", out var ptEl) && ptEl.ValueKind == JsonValueKind.String ? ptEl.GetString() : null;
        var location = new SourceLocation(type, page, para, sheet, range, part);
        var channel = Prop(el, "Channel", "channel", out var chEl) && chEl.ValueKind == JsonValueKind.String ? chEl.GetString()! : "body";
        IReadOnlyList<SearchField>? fields = Array.Empty<SearchField>();
        if (Prop(el, "Fields", "fields", out var fEl) && fEl.ValueKind == JsonValueKind.Array)
        {
            var len = fEl.GetArrayLength();
            if (len > 0)
            {
                var arr = new SearchField[len];
                int idx = 0;
                foreach (var item in fEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid structured source fields.");
                    var fn = Prop(item, "Name", "name", out var fnEl) && fnEl.ValueKind == JsonValueKind.String ? fnEl.GetString()! : "";
                    var fv = Prop(item, "Value", "value", out var fvEl) && fvEl.ValueKind == JsonValueKind.String ? fvEl.GetString()! : null!;
                    arr[idx++] = new SearchField(fn, fv);
                }
                fields = arr;
            }
            else fields = Array.Empty<SearchField>();
        }
        IReadOnlyList<OcrTextRegion>? regions = null;
        if (Prop(el, "Regions", "regions", out var regEl) && regEl.ValueKind == JsonValueKind.Array)
        {
            var len = regEl.GetArrayLength();
            if (len > 0)
            {
                var arr = new OcrTextRegion[len];
                int idx = 0;
                foreach (var item in regEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid OCR source region.");
                    int line = Prop(item, "Line", "line", out var lEl) && lEl.ValueKind == JsonValueKind.Number && lEl.TryGetInt32(out var lv) ? lv : -1;
                    double left = Prop(item, "Left", "left", out var lfEl) && lfEl.ValueKind == JsonValueKind.Number && lfEl.TryGetDouble(out var lfv) ? lfv : double.NaN;
                    double top = Prop(item, "Top", "top", out var tpEl) && tpEl.ValueKind == JsonValueKind.Number && tpEl.TryGetDouble(out var tpv) ? tpv : double.NaN;
                    double width = Prop(item, "Width", "width", out var wEl) && wEl.ValueKind == JsonValueKind.Number && wEl.TryGetDouble(out var wv) ? wv : double.NaN;
                    double height = Prop(item, "Height", "height", out var hEl) && hEl.ValueKind == JsonValueKind.Number && hEl.TryGetDouble(out var hv) ? hv : double.NaN;
                    string? text = Prop(item, "Text", "text", out var txEl) && txEl.ValueKind == JsonValueKind.String ? txEl.GetString() : null;
                    arr[idx++] = new OcrTextRegion(line, left, top, width, height, text);
                }
                regions = arr;
            }
            else regions = Array.Empty<OcrTextRegion>();
        }
        return new SourceAnchor(location, channel, fields, regions);
    }

    private static void ValidateAnchor(SourceAnchor source)
    {
        var location = source.Location ?? throw new InvalidDataException("Missing source location.");
        if (source.Channel is not ("body" or "notes" or "table" or "ocr" or "page-visual" or "metadata"))
            throw new InvalidDataException("Unknown source channel.");
        if (location.Type is not ("paragraph" or "slide" or "sheet" or "pdf-page" or "image" or "text")
            || location.Page is < 1 or > 5000 || location.Paragraph is < 1
            || location.Sheet?.Length > 31 || location.Range?.Length > 32 || location.Part?.Length > 512
            || location.Type is "slide" or "pdf-page" && location.Page is null
            || location.Type == "paragraph" && location.Paragraph is null
            || location.Type == "sheet" && (string.IsNullOrWhiteSpace(location.Sheet) || string.IsNullOrWhiteSpace(location.Range)))
            throw new InvalidDataException("Invalid source locator.");
        if (source.Fields?.Count > 64 || source.Fields?.Any(f => f is null || string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > 64 || f.Value is null || f.Value.Length > 256 || f.Value.Contains('\0')) == true)
            throw new InvalidDataException("Invalid structured source fields.");
        int lastRegion = -1;
        if (source.Regions?.Count > 96 || source.Regions?.Sum(r => r?.Text?.Length ?? 0) > 8000) throw new InvalidDataException("OCR region count/text exceeds its bound.");
        foreach (var region in source.Regions ?? [])
        {
            if (region is null || region.Line <= lastRegion || region.Line > 5000 || !double.IsFinite(region.Left) || !double.IsFinite(region.Top)
                || !double.IsFinite(region.Width) || !double.IsFinite(region.Height) || region.Left < 0 || region.Top < 0
                || region.Text?.Contains('\0') == true || region.Width <= 0 || region.Height <= 0 || region.Left + region.Width > 1 + 1e-9 || region.Top + region.Height > 1 + 1e-9)
                throw new InvalidDataException("Invalid OCR source region.");
            lastRegion = region.Line;
        }
    }

    private static byte[] Encode(float[] vector, bool int8Quantized = false)
    {
        if (int8Quantized) return VectorContract.QuantizeInt8(vector);
        VectorContract.Validate(vector);
        var bytes = new byte[VectorContract.Dimension * 2];
        if (BitConverter.IsLittleEndian)
        {
            var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes.AsSpan());
            for (int i = 0; i < vector.Length; i++) dst[i] = (Half)vector[i];
        }
        else
        {
            for (int i = 0; i < vector.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), BitConverter.HalfToUInt16Bits((Half)vector[i]));
        }
        return bytes;
    }

    private static float[] Decode(byte[] bytes)
    {
        if (bytes.Length == VectorContract.Int8ByteCount)
        {
            return VectorContract.DequantizeInt8(bytes);
        }
        if (bytes.Length == VectorContract.Dimension)
        {
            // Fixed-scale 1/127f scalar fallback
            var raw = new float[VectorContract.Dimension];
            double normSq = 0;
            const float inv127 = 1.0f / 127.0f;
            for (int i = 0; i < VectorContract.Dimension; i++)
            {
                sbyte q = (sbyte)bytes[i];
                float val = q * inv127;
                raw[i] = val;
                normSq += (double)val * val;
            }
            if (normSq > 1e-12)
            {
                float invNorm = (float)(1.0 / Math.Sqrt(normSq));
                for (int i = 0; i < VectorContract.Dimension; i++) raw[i] *= invNorm;
            }
            VectorContract.Validate(raw);
            return raw;
        }
        if (bytes.Length == VectorContract.Dimension * 2)
        {
            var vector = new float[VectorContract.Dimension];
            if (BitConverter.IsLittleEndian)
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes.AsSpan());
                for (int i = 0; i < vector.Length; i++) vector[i] = (float)src[i];
            }
            else
            {
                for (int i = 0; i < vector.Length; i++) vector[i] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2)));
            }
            VectorContract.Validate(vector);
            return vector;
        }
        throw new InvalidDataException($"Vector blob must contain 768 float16 values ({VectorContract.Dimension * 2} bytes) or int8 quantized values ({VectorContract.Int8ByteCount} bytes).");
    }

    /// <summary>Converts FP16 vectors to Int8, migrates legacy contentful fts_seg to contentless, prunes redundant FTS/anchor payloads, optimizes FTS, and vacuums freed/fragmented pages.</summary>
    public Task<int> CompactStorageAsync(int maxPages = 50000, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            InitializeCore(cancellationToken);
            if (!writable || connection == null) return 0;
            long pagesBefore = Convert.ToInt64(Scalar(connection, null, "PRAGMA page_count"));
            long freelistBefore = Convert.ToInt64(Scalar(connection, null, "PRAGMA freelist_count"));
            bool migratedFtsSeg = false;
            Write(cancellationToken, (db, tx) =>
            {
                Execute(db, tx, "INSERT INTO meta(key,value) VALUES('vector_encoding','int8') ON CONFLICT(key) DO UPDATE SET value='int8'");

                var vecBatch = new List<(long Id, byte[] Int8)>(1000);
                long lastSegId = 0;
                using var vecSelectCmd = Command(db, tx, "SELECT id, vec FROM segments WHERE id > $lastId AND vec IS NOT NULL AND length(vec) = 1536 ORDER BY id LIMIT 1000", ("$lastId", 0L));
                var pLastSegId = vecSelectCmd.Parameters["$lastId"];
                using var vecUpdateCmd = Command(db, tx, "UPDATE segments SET vec = $vec WHERE id = $id", ("$vec", Array.Empty<byte>()), ("$id", 0L));
                var pUpdVec = vecUpdateCmd.Parameters["$vec"];
                var pUpdSegId = vecUpdateCmd.Parameters["$id"];
                vecUpdateCmd.Prepare();
                while (true)
                {
                    vecBatch.Clear();
                    pLastSegId.Value = lastSegId;
                    using (var r = vecSelectCmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            long segId = r.GetInt64(0);
                            byte[] blob = (byte[])r.GetValue(1);
                            vecBatch.Add((segId, VectorContract.QuantizeInt8(Decode(blob))));
                            lastSegId = segId;
                        }
                    }
                    if (vecBatch.Count == 0) break;
                    foreach (var (segId, qBlob) in vecBatch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pUpdVec.Value = qBlob;
                        pUpdSegId.Value = segId;
                        vecUpdateCmd.ExecuteNonQuery();
                    }
                }

                if (Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='fts_content'")) == 1)
                    Execute(db, tx, "UPDATE fts_content SET c2 = NULL WHERE c2 IS NOT NULL");

                if (Scalar(db, tx, "SELECT sql FROM sqlite_master WHERE type='table' AND name='fts_seg'") is string ftsSegSql &&
                    !ftsSegSql.Contains("content=''", StringComparison.OrdinalIgnoreCase) &&
                    Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='fts_seg_content'")) == 1)
                {
                    Execute(db, tx, "CREATE VIRTUAL TABLE fts_seg_new USING fts5(tokens,content='',contentless_delete=1,tokenize=\"unicode61 remove_diacritics 2\")");
                    Execute(db, tx, "INSERT INTO fts_seg_new(rowid, tokens) SELECT id, c0 FROM fts_seg_content WHERE c0 IS NOT NULL");
                    Execute(db, tx, "DROP TABLE fts_seg");
                    Execute(db, tx, "ALTER TABLE fts_seg_new RENAME TO fts_seg");
                    migratedFtsSeg = true;
                }

                var infoBatch = new List<(long Id, string Info)>(500);
                long lastFileId = 0;
                using var infoSelectCmd = Command(db, tx,
                    "SELECT id, info FROM files WHERE id > $lastId AND info IS NOT NULL AND instr(info, '_sourceAnchorsV1') > 0 AND (instr(info, '\"Page\":null') > 0 OR instr(info, '\"Paragraph\":null') > 0 OR instr(info, '\"Fields\":[]') > 0) ORDER BY id LIMIT 500",
                    ("$lastId", 0L));
                var pLastFileId = infoSelectCmd.Parameters["$lastId"];
                using var infoUpdateCmd = Command(db, tx, "UPDATE files SET info = $info WHERE id = $id", ("$info", ""), ("$id", 0L));
                var pUpdInfo = infoUpdateCmd.Parameters["$info"];
                var pUpdFileId = infoUpdateCmd.Parameters["$id"];
                infoUpdateCmd.Prepare();
                while (true)
                {
                    infoBatch.Clear();
                    pLastFileId.Value = lastFileId;
                    using (var r = infoSelectCmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            long fileId = r.GetInt64(0);
                            string infoText = r.GetString(1);
                            lastFileId = fileId;
                            var parsedAnchors = ReadAnchors(infoText);
                            var compactedAnchors = new Dictionary<long, SourceAnchor>(parsedAnchors.Count);
                            foreach (var kv in parsedAnchors)
                                compactedAnchors[kv.Key] = NormalizeAnchorForStorage(kv.Value);
                            var metadata = JsonNode.Parse(infoText)!.AsObject();
                            metadata.Remove("_sourceAnchorsV1");
                            if (compactedAnchors.Count != 0)
                                metadata["_sourceAnchorsV1"] = JsonSerializer.SerializeToNode(compactedAnchors, CompactAnchorJson);
                            infoBatch.Add((fileId, metadata.ToJsonString()));
                        }
                    }
                    if (infoBatch.Count == 0) break;
                    foreach (var (fileId, compactInfo) in infoBatch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pUpdInfo.Value = compactInfo;
                        pUpdFileId.Value = fileId;
                        infoUpdateCmd.ExecuteNonQuery();
                    }
                }

                Execute(db, tx, "INSERT INTO fts(fts) VALUES('optimize')");
                Execute(db, tx, "INSERT INTO fts_seg(fts_seg) VALUES('optimize')");
                return [];
            }, VectorEffect.All, invalidatesLexical: migratedFtsSeg);

            // Full VACUUM repacks partially-empty B-tree pages left behind by in-place vec/c2/info updates,
            // which PRAGMA incremental_vacuum alone cannot compact.
            Execute(connection, null, "VACUUM;");
            long pagesAfter = Convert.ToInt64(Scalar(connection, null, "PRAGMA page_count"));
            long freelistAfter = Convert.ToInt64(Scalar(connection, null, "PRAGMA freelist_count"));
            long reclaimedPages = Math.Max(0, pagesBefore - pagesAfter);
            if (reclaimedPages == 0 && freelistBefore > freelistAfter)
                reclaimedPages = freelistBefore - freelistAfter;
            return (int)Math.Min(int.MaxValue, reclaimedPages);
        }, cancellationToken);

    /// <summary>Reclaims unused database pages created by deleted segments or files during idle periods.</summary>
    public Task<int> IncrementalVacuumAsync(int maxPages = 500, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            if (!writable || connection == null) return 0;
            var autoVacuum = Convert.ToInt64(Scalar(connection, null, "PRAGMA auto_vacuum"));
            if (autoVacuum != 2) return 0; // 2 = INCREMENTAL
            var freelistCount = Convert.ToInt64(Scalar(connection, null, "PRAGMA freelist_count"));
            if (freelistCount <= 0) return 0;
            Execute(connection, null, $"PRAGMA incremental_vacuum({Math.Clamp(maxPages, 1, 5000)});");
            var after = Convert.ToInt64(Scalar(connection, null, "PRAGMA freelist_count"));
            return (int)(freelistCount - after);
        }, cancellationToken);

    private static IndexSegment Segment(SqliteDataReader row, int start, bool vector) => new(row.GetString(start), row.GetString(start + 1),
        Number(row, start + 2), Number(row, start + 3), Text(row, start + 4), vector && !row.IsDBNull(start + 5) ? Decode((byte[])row.GetValue(start + 5)) : null);
    private static string? Text(SqliteDataReader row, int n) => row.IsDBNull(n) ? null : row.GetString(n);
    private static double? Number(SqliteDataReader row, int n) => row.IsDBNull(n) ? null : row.GetDouble(n);
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object? Value)[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Key, arg.Value ?? DBNull.Value);
        return cmd;
    }
    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object? Value)[] args)
    { using var cmd = Command(db, tx, sql, args); return cmd.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object? Value)[] args)
    { using var cmd = Command(db, tx, sql, args); var value = cmd.ExecuteScalar(); return value is DBNull ? null : value; }

    public Task<IReadOnlyList<PendingEmbeddingSegment>> ReadPendingEmbeddingSegmentsAsync(int limit, CancellationToken cancellationToken = default) =>
        ReadPendingEmbeddingSegmentsAsync(limit, 0L, cancellationToken);

    private static readonly Regex TrailingEmbeddingErrorPattern =
        new(@"(?:^|\s+)\d+\s+embedding errors:\s.*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    internal static bool TryResolveVectorOnlyPartial(string? detail, bool allSegmentsEmbedded, out string? updatedDetail, out bool promoteToDone)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            updatedDetail = null;
            promoteToDone = true;
            return true;
        }
        string cleaned = detail
            .Replace(FilePolicy.SampledInlineBackfillDetail, "", StringComparison.Ordinal)
            .Replace(FilePolicy.SemanticUnavailableDetail, "", StringComparison.Ordinal);
        if (allSegmentsEmbedded)
        {
            cleaned = TrailingEmbeddingErrorPattern.Replace(cleaned, "");
        }
        cleaned = cleaned.Trim();
        if (cleaned.Length == 0)
        {
            updatedDetail = null;
            promoteToDone = true;
            return true;
        }
        promoteToDone = false;
        updatedDetail = cleaned;
        return !string.Equals(cleaned, detail, StringComparison.Ordinal);
    }

    public Task<IReadOnlyList<PendingEmbeddingSegment>> ReadPendingEmbeddingSegmentsAsync(int limit, long afterId, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return Task.FromResult<IReadOnlyList<PendingEmbeddingSegment>>([]);
        return RunAuxReadAsync((db, tx) =>
        {
            bool hasPendingIndex = Convert.ToInt64(Scalar(db, tx,
                "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='ix_segments_pending_vec'")) == 1;
            string segHint = hasPendingIndex ? " INDEXED BY ix_segments_pending_vec" : "";
            using var cmd = Command(db, tx, $"""
                SELECT s.id, s.file, f.path, s.excerpt, s.kind, f.kind, f.info
                FROM segments s{segHint}
                JOIN files f ON s.file = f.id
                WHERE s.id > $afterId
                  AND s.vec IS NULL
                  AND {EmbeddableSegmentPredicate.Replace("kind", "s.kind", StringComparison.Ordinal).Replace("excerpt", "s.excerpt", StringComparison.Ordinal)}
                ORDER BY s.id
                LIMIT $limit
                """, ("$afterId", afterId), ("$limit", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<PendingEmbeddingSegment>();
            while (r.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                long id = r.GetInt64(0);
                long fileId = r.GetInt64(1);
                string path = r.GetString(2);
                string excerpt = r.GetString(3);
                string kind = r.GetString(4);
                string fileKind = r.GetString(5);
                string? info = r.IsDBNull(6) ? null : r.GetString(6);
                string title = FilePolicy.ResolveEmbeddingTitle(path, fileKind == "code", info);
                list.Add(new PendingEmbeddingSegment(id, fileId, path, title, excerpt, kind));
            }
            return (IReadOnlyList<PendingEmbeddingSegment>)list;
        }, cancellationToken);
    }

    public Task<int> SaveBackfilledVectorsAsync(IReadOnlyList<SegmentVectorUpdate> updates, CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0) return Task.FromResult(0);
        return RunAsync(() =>
        {
            int actualUpdated = 0;
            Write(cancellationToken, (db, tx) =>
            {
                if (string.IsNullOrWhiteSpace(Scalar(db, tx, "SELECT value FROM meta WHERE key='profile_id'") as string))
                    throw new InvalidDataException("Set profile_id before storing any vectors.");
                var enc = Scalar(db, tx, "SELECT value FROM meta WHERE key='vector_encoding'") as string;
                if (enc is null)
                {
                    Execute(db, tx, "INSERT OR IGNORE INTO meta(key,value) VALUES('vector_encoding','int8')");
                    enc = "int8";
                }
                bool int8 = string.Equals(enc, "int8", StringComparison.OrdinalIgnoreCase);
                using var cmd = Command(db, tx,
                    $"UPDATE segments SET vec=$vec, modality='text' WHERE id=$id AND file=$file AND vec IS NULL AND {EmbeddableSegmentPredicate}",
                    ("$vec", (object)DBNull.Value), ("$id", 0L), ("$file", 0L));
                var vecParam = cmd.Parameters["$vec"];
                var idParam = cmd.Parameters["$id"];
                var fileParam = cmd.Parameters["$file"];

                var affectedFiles = new HashSet<long>();
                foreach (var u in updates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    VectorContract.Validate(u.Vector);
                    vecParam.Value = Encode(u.Vector, int8);
                    idParam.Value = u.Id;
                    fileParam.Value = u.FileId;
                    if (cmd.ExecuteNonQuery() > 0)
                    {
                        actualUpdated++;
                        affectedFiles.Add(u.FileId);
                    }
                }

                // Promote only files whose 'partial' status was caused solely by deferred/missing text embeddings.
                foreach (var fileId in affectedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long remaining = Convert.ToInt64(Scalar(db, tx,
                        $"SELECT COUNT(*) FROM segments WHERE file=$file AND vec IS NULL AND {EmbeddableSegmentPredicate}",
                        ("$file", fileId)));
                    if (remaining == 0)
                    {
                        (string State, string? Detail)? currentFile = null;
                        using (var fileCmd = Command(db, tx, "SELECT state, detail FROM files WHERE id=$file", ("$file", fileId)))
                        using (var fileRows = fileCmd.ExecuteReader())
                        {
                            if (fileRows.Read())
                                currentFile = (fileRows.GetString(0), fileRows.IsDBNull(1) ? null : fileRows.GetString(1));
                        }
                        if (currentFile is { State: "partial" } pf)
                        {
                            bool allSegmentsEmbedded = Convert.ToInt64(Scalar(db, tx,
                                "SELECT COUNT(*) FROM segments WHERE file=$file AND vec IS NULL AND kind <> 'name'",
                                ("$file", fileId))) == 0;
                            if (TryResolveVectorOnlyPartial(pf.Detail, allSegmentsEmbedded, out var updatedDetail, out var promoteToDone))
                            {
                                if (promoteToDone)
                                    Execute(db, tx, "UPDATE files SET state='done', detail=NULL WHERE id=$file AND state='partial'", ("$file", fileId));
                                else
                                    Execute(db, tx, "UPDATE files SET detail=$detail WHERE id=$file AND state='partial'", ("$file", fileId), ("$detail", (object?)updatedDetail ?? DBNull.Value));
                            }
                        }
                    }
                }

                var affectedPaths = new List<string?>(affectedFiles.Count);
                if (affectedFiles.Count > 0)
                {
                    using var pathCmd = Command(db, tx, "SELECT path FROM files WHERE id IN (SELECT value FROM json_each($ids))",
                        ("$ids", JsonSerializer.Serialize(affectedFiles)));
                    using var pathRows = pathCmd.ExecuteReader();
                    while (pathRows.Read())
                        affectedPaths.Add(pathRows.GetString(0));
                }
                return affectedPaths;
            }, VectorEffect.Files, invalidatesLexical: false);
            return actualUpdated;
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        await readGate.WaitAsync().ConfigureAwait(false);
        await auxReadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            lock (vectorCacheSync)
            {
                vectorCache = null;
                lastWordEvidence = null;
                dirtyVectorPaths.Clear();
                dirtyVectorSeqs.Clear();
            }
            auxReadConnection?.Dispose();
            auxReadConnection = null;
            readConnection?.Dispose();
            readConnection = null;
            connection?.Dispose();
            Volatile.Write(ref connection, null);
        }
        finally
        {
            auxReadGate.Release();
            readGate.Release();
            gate.Release();
        }
    }


    /// <summary>Query-side tokens for the segmented FTS channel; empty when the segmenter is unusable.</summary>

    private static readonly string Schema = $$"""
        CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE files(id INTEGER PRIMARY KEY,path TEXT NOT NULL UNIQUE,kind TEXT NOT NULL,size INTEGER NOT NULL,mtime REAL NOT NULL,
            inode INTEGER NOT NULL,device INTEGER NOT NULL,state TEXT NOT NULL DEFAULT 'pending',detail TEXT,info TEXT,indexed_at REAL);
        CREATE INDEX files_inode ON files(device,inode);
        CREATE INDEX files_state ON files(state,kind);
        CREATE INDEX ix_files_meta ON files(id, kind, path);
        CREATE INDEX ix_files_header ON files(id, path, kind, size, mtime, state, detail);
        CREATE TABLE segments(id INTEGER PRIMARY KEY,file INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,kind TEXT NOT NULL,
            modality TEXT NOT NULL,loc REAL,loc_end REAL,excerpt TEXT,vec BLOB);
        CREATE INDEX segments_file ON segments(file);
        CREATE INDEX ix_segments_meta ON segments(id, file, kind, loc, loc_end);
        CREATE INDEX ix_segments_vec ON segments(file, id, kind, modality, loc, loc_end) WHERE vec IS NOT NULL;
        CREATE INDEX ix_segments_embeddable ON segments(id) WHERE {{EmbeddableSegmentPredicate}};
        CREATE INDEX ix_segments_pending_vec ON segments(id, file, kind) WHERE vec IS NULL AND {{EmbeddableSegmentPredicate}};
        CREATE VIRTUAL TABLE fts USING fts5(name,body,original UNINDEXED,tokenize="{{Tokenizer}}");
        CREATE VIRTUAL TABLE fts_seg USING fts5(tokens,content='',contentless_delete=1,tokenize="unicode61 remove_diacritics 2");
        CREATE TABLE IF NOT EXISTS index_tasks (path TEXT PRIMARY KEY,root TEXT NOT NULL,code INTEGER NOT NULL,kind TEXT NOT NULL,tombstone TEXT,state TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,due_utc TEXT NOT NULL,seen_utc TEXT NOT NULL,reason TEXT);
        CREATE INDEX IF NOT EXISTS ix_index_tasks_due ON index_tasks(state, due_utc);
        CREATE TABLE IF NOT EXISTS usn_checkpoints (volume_serial TEXT PRIMARY KEY, journal_id TEXT, next_usn TEXT, reconcile_utc TEXT);
        INSERT OR IGNORE INTO meta(key,value) VALUES('lexicon_schema','3');
        INSERT OR IGNORE INTO meta(key,value) VALUES('dictionary_fingerprint','');
        PRAGMA user_version=5;
        """;
}

internal enum IndexInitializationStage { SchemaCreated, BeforePublish }
internal sealed record WordEvidence(long FileId, double Rank, double Coverage, IndexSegment Segment, string? Snippet, bool ExactLiteralsVerified = false, bool Segmented = false);
internal sealed record SearchSnapshot(IReadOnlyList<IndexedFile> Files, IReadOnlyList<WordEvidence> Words, string? Profile);
internal sealed record CompactSearchSnapshot(
    IReadOnlyDictionary<long, IndexedFile> Headers,
    CompactVectorIndex? Vectors,
    IReadOnlyList<WordEvidence> Words,
    string? Profile);
