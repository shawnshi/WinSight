using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WinSight.Core;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Search;

namespace WinSight.Infrastructure.Storage;

/// <summary>Schema5 reader; only newly created, explicitly marked native indexes are writable.</summary>
public sealed class SqliteIndexRepository : IIndexRepository, IUsnCheckpointRepository
{
    public const string NativeFormat = "winsight-native-schema5-fp16-v1";
    private const string MarkerKey = "native_format";
    private static readonly JsonSerializerOptions MetadataJson = new() { PropertyNameCaseInsensitive = true };
    private const string Tokenizer = "porter unicode61 remove_diacritics 2 categories 'L* N* Co M*'";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Action<IndexInitializationStage>? initializationCheckpoint;
    private SqliteConnection? connection;
    private (long Version, IReadOnlyList<IndexedFile> Files)? vectorCache;
    private bool writable;
    private bool disposed;
    public string DatabasePath { get; }

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
        if (connection != null) return;
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(DatabasePath)) CreateAndPublishDatabase(ct);
        var db = Open(DatabasePath, SqliteOpenMode.ReadOnly);
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
                Execute(db, null, "UPDATE index_tasks SET state='Pending' WHERE state IN ('Running', 'Stabilizing')");
            }
            ct.ThrowIfCancellationRequested();
            connection = db;
        }
        catch { db.Dispose(); throw; }
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
                Execute(staged, null, "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
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
            DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            db.Open();
            // Query-only collation: Windows ordinal path casing without changing the on-disk tokenizer or indexes.
            db.CreateCollation("WINSIGHT_PATH", StringComparer.OrdinalIgnoreCase.Compare);
            string? lastLiteralJson = null;
            string[] literals = [];
            db.CreateFunction<string?, string?, string, long>("WINSIGHT_LITERAL_MASK", (text, name, json) =>
            {
                if (lastLiteralJson != json) { literals = JsonSerializer.Deserialize<string[]>(json) ?? throw new InvalidDataException("Missing literal query."); lastLiteralJson = json; }
                return DocumentSearchQuery.LiteralMask(text, name, literals);
            }, isDeterministic: true);
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private static void ValidateSchema(SqliteConnection db)
    {
        // Additive table for existing databases: writing keeps working, and NeedsRebuild() forces a coordinated
        // generation switch because lexicon_schema is still absent. No silent migration of old rows.
        // A read-only open (legacy fixtures, inspection) must remain byte-identical, so the create is skipped there.
        try
        {
            Execute(db, null, "CREATE VIRTUAL TABLE IF NOT EXISTS fts_seg USING fts5(tokens, tokenize=\"unicode61 remove_diacritics 2\")");
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 8)
        {
            // read-only database: nothing to add, validation continues against what is there
        }
        if (Convert.ToInt64(Scalar(db, null, "PRAGMA user_version")) != 5)
            throw new InvalidDataException("Only schema5 is supported; no automatic migration or clearing is permitted.");
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

    private T Read<T>(CancellationToken ct, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        InitializeCore(ct);
        using var tx = connection!.BeginTransaction(deferred: true);
        var result = action(connection, tx);
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        return result;
    }

    private void Write(CancellationToken ct, Action<SqliteConnection, SqliteTransaction> action)
    {
        InitializeCore(ct);
        if (!writable) throw new InvalidOperationException("Legacy schema5 index is read-only. Reindex into a new native scratch database; never overwrite the original.");
        using var tx = connection!.BeginTransaction(deferred: false);
        if (!Equals(Scalar(connection, tx, "SELECT value FROM meta WHERE key=$key", ("$key", MarkerKey)), NativeFormat))
            throw new InvalidDataException("Native format marker changed; refusing writes.");
        action(connection, tx);
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        vectorCache = null;
    }

    public Task<IReadOnlyList<IndexedFile>> ReadFilesAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => Read(cancellationToken, (db, tx) => ReadFiles(db, tx, true, cancellationToken)), cancellationToken);

    public Task<IndexedFile?> GetFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        return RunAsync(() => Read(cancellationToken, (db, tx) => ReadFiles(db, tx, true, cancellationToken, path).SingleOrDefault()), cancellationToken);
    }

    private static List<IndexedFile> ReadHeaders(SqliteConnection db, SqliteTransaction tx, CancellationToken ct,
        string? path = null, IReadOnlyCollection<long>? ids = null)
    {
        var files = new List<IndexedFile>();
        string scope = path != null ? " WHERE path=$path" : ids != null ? " WHERE id IN (SELECT value FROM json_each($ids))" : "";
        using var cmd = Command(db, tx, "SELECT id,path,kind,size,mtime,state,detail,info FROM files" + scope + " ORDER BY id",
            ("$path", path), ("$ids", ids == null ? null : JsonSerializer.Serialize(ids)));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            files.Add(new IndexedFile(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetDouble(4), reader.GetString(5), Text(reader, 6), Text(reader, 7), new List<IndexSegment>()));
        }
        return files;
    }

    private static IReadOnlyList<IndexedFile> ReadFiles(SqliteConnection db, SqliteTransaction tx, bool vectors, CancellationToken ct, string? path = null, bool vectorSegmentsOnly = false)
    {
        var files = ReadHeaders(db, tx, ct, path);
        var byId = files.ToDictionary(f => f.Id);
        var anchors = files.ToDictionary(f => f.Id, f => ReadAnchors(f.Info));
        string scope = path == null ? "" : " WHERE f.path=$path";
        if (vectorSegmentsOnly) scope += path == null ? " WHERE s.vec IS NOT NULL" : " AND s.vec IS NOT NULL";
        using var segments = Command(db, tx, "SELECT s.file,s.kind,s.modality,s.loc,s.loc_end,s.excerpt," + (vectors ? "s.vec" : "NULL") +
            ",s.id FROM segments s JOIN files f ON f.id=s.file" + scope + " ORDER BY s.id", ("$path", path));
        using var rows = segments.ExecuteReader();
        while (rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            var fileId = rows.GetInt64(0);
            ((List<IndexSegment>)byId[fileId].Segments).Add(Segment(rows, 1, vectors) with { Source = anchors[fileId].GetValueOrDefault(rows.GetInt64(7)) });
        }
        return files;
    }

    public Task UpsertFileAsync(IndexedFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(file.Segments);
        // Own a snapshot of mutable input arrays before crossing the async boundary.
        file = file with { Segments = file.Segments.Select(s => s with { Vector = s.Vector?.ToArray(),
            Source = s.Source is { } source ? source with { Fields = source.Fields?.ToArray(), Regions = source.Regions?.ToArray() }
                : s.Excerpt is { } text ? new(new(file.Kind is "image" or "screenshot" ? "image" : "text"), s.Modality == "image" ? "page-visual" : file.Kind is "image" or "screenshot" ? "ocr" : "body", DocumentSearchQuery.FieldsFromText(text)) : null }).ToArray() };
        ValidateFile(file);
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) => UpsertFileInternal(db, tx, file, cancellationToken));
            return 0;
        }, cancellationToken);
    }

    private static void UpsertFileInternal(SqliteConnection db, SqliteTransaction tx, IndexedFile file, CancellationToken ct)
    {
        if (file.Segments.Any(s => s.Vector != null) && string.IsNullOrWhiteSpace(Scalar(db, tx, "SELECT value FROM meta WHERE key='profile_id'") as string))
            throw new InvalidDataException("Set profile_id before storing any vectors.");
        var existing = Scalar(db, tx, "SELECT id FROM files WHERE path=$path", ("$path", file.Path));
        if (file.Id != 0 && (existing == null || Convert.ToInt64(existing) != file.Id)) throw new InvalidDataException("File ID does not belong to this path.");
        Execute(db, tx, "INSERT INTO files(path,kind,size,mtime,inode,device,state,detail,info,indexed_at) VALUES($path,$kind,$size,$mtime,0,0,$state,$detail,$info,$now) " +
            "ON CONFLICT(path) DO UPDATE SET kind=excluded.kind,size=excluded.size,mtime=excluded.mtime,state=excluded.state,detail=excluded.detail,info=excluded.info,indexed_at=excluded.indexed_at",
            ("$path", file.Path), ("$kind", file.Kind), ("$size", file.Size), ("$mtime", file.ModifiedTime), ("$state", file.State), ("$detail", file.Detail), ("$info", file.Info), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
        long id = Convert.ToInt64(Scalar(db, tx, "SELECT id FROM files WHERE path=$path", ("$path", file.Path)));
        Execute(db, tx, "DELETE FROM fts WHERE rowid IN (SELECT id FROM segments WHERE file=$id); DELETE FROM fts_seg WHERE rowid IN (SELECT id FROM segments WHERE file=$id); DELETE FROM segments WHERE file=$id;", ("$id", id));
        string slash = file.Path.Replace('\\', '/');
        int last = slash.LastIndexOf('/');
        AddSegment(db, tx, id, new IndexSegment("name", "none", null, null, null), last < 0 ? slash : slash[(last + 1)..], last < 0 ? "" : slash[..last].Replace('/', ' '));
        var anchors = new Dictionary<long, SourceAnchor>();
        foreach (var segment in file.Segments.Where(s => s.Kind != "name"))
        {
            ct.ThrowIfCancellationRequested();
            var segmentId = AddSegment(db, tx, id, segment, "", segment.Excerpt);
            if (segment.Source is { } source) anchors.Add(segmentId, source);
        }
        if (anchors.Count != 0 || file.Info?.Contains("_sourceAnchorsV1", StringComparison.Ordinal) == true)
        {
            var metadata = file.Info is null ? new JsonObject() : JsonNode.Parse(file.Info)!.AsObject();
            metadata.Remove("_sourceAnchorsV1");
            if (anchors.Count != 0) metadata.Add("_sourceAnchorsV1", JsonSerializer.SerializeToNode(anchors));
            Execute(db, tx, "UPDATE files SET info=$info WHERE id=$id", ("$info", metadata.ToJsonString()), ("$id", id));
        }
    }

    private static long AddSegment(SqliteConnection db, SqliteTransaction tx, long id, IndexSegment segment, string name, string? body)
    {
        Execute(db, tx, "INSERT INTO segments(file,kind,modality,loc,loc_end,excerpt,vec) VALUES($file,$kind,$modality,$loc,$end,$excerpt,$vec)",
            ("$file", id), ("$kind", segment.Kind), ("$modality", segment.Vector == null ? "none" : segment.Modality), ("$loc", segment.Location),
            ("$end", segment.LocationEnd), ("$excerpt", segment.Excerpt), ("$vec", segment.Vector == null ? null : Encode(segment.Vector)));
        long segmentId = Convert.ToInt64(Scalar(db, tx, "SELECT last_insert_rowid()"));
        if (name.Length == 0 && string.IsNullOrEmpty(body)) return segmentId;
        string normalized = SearchText.Normalize(body ?? "");
        Execute(db, tx, "INSERT INTO fts(rowid,name,body,original) VALUES($id,$name,$body,$original)", ("$id", segmentId),
            ("$name", SearchText.Normalize(name)), ("$body", normalized), ("$original", normalized == body ? null : body));
        // Segmented FTS: the same rules are used at query time, so Chinese terms are real FTS tokens rather
        // than a substring heuristic. A missing segmenter aborts the file instead of leaving half a row.
        var tokens = SearchText.SegmentedTokens(name + " " + (body ?? ""));
        Execute(db, tx, "INSERT INTO fts_seg(rowid,tokens) VALUES($id,$tokens)", ("$id", segmentId), ("$tokens", tokens));
        return segmentId;
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        return RunAsync(() =>
        {
            Write(cancellationToken, (db, tx) => DeleteFileInternal(db, tx, path));
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
                Execute(db, tx, "UPDATE index_tasks SET state='Pending' WHERE state IN ('Running', 'Stabilizing')");
            });
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
            });
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
                foreach (var task in tasks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
                }
            });
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
            });
            return 0;
        }, cancellationToken);
    }

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
        return RunAsync(() => Read(cancellationToken, (db, tx) =>
        {
            var list = new List<IndexTask>();
            var now = DateTimeOffset.UtcNow.ToString("O");
            using var cmd = Command(db, tx, """
                SELECT path, root, code, kind, tombstone, state, attempts, due_utc, seen_utc, reason
                FROM index_tasks
                WHERE due_utc <= $now AND state IN ('Pending', 'Stabilizing', 'Ready')
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
        }), cancellationToken);
    }

    public Task<IndexTask?> GetTaskAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        return RunAsync(() => Read(cancellationToken, (db, tx) =>
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
        }), cancellationToken);
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
            });
            return 0;
        }, cancellationToken);
    }

    public Task AckTaskWithUpsertAsync(IndexedFile file, string taskPath, string? tombstone = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(file.Segments);
        ValidatePath(taskPath);
        file = file with { Segments = file.Segments.Select(s => s with { Vector = s.Vector?.ToArray(),
            Source = s.Source is { } source ? source with { Fields = source.Fields?.ToArray(), Regions = source.Regions?.ToArray() }
                : s.Excerpt is { } text ? new(new(file.Kind is "image" or "screenshot" ? "image" : "text"), s.Modality == "image" ? "page-visual" : file.Kind is "image" or "screenshot" ? "ocr" : "body", DocumentSearchQuery.FieldsFromText(text)) : null }).ToArray() };
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
            });
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
            });
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
            });
            return trimmed;
        }, cancellationToken);
    }

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
            });
            return 0;
        }, cancellationToken);
    }

    public Task<(string JournalId, string NextUsn, DateTimeOffset ReconcileUtc)?> GetUsnCheckpointAsync(string volumeSerial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeSerial);
        return RunAsync(() =>
        {
            return Read(cancellationToken, (db, tx) =>
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
        return RunAsync(() => Read(cancellationToken, (db, tx) => Scalar(db, tx, "SELECT value FROM meta WHERE key=$key", ("$key", key)) as string), cancellationToken);
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
                    if (profile != value && Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM segments WHERE vec IS NOT NULL")) > 0)
                        throw new InvalidOperationException("Cannot change profile while vectors exist; create a separate new index.");
                    if (infoJson != null && JsonSerializer.Deserialize<EmbeddingInfo>(infoJson, MetadataJson)?.ProfileId != value)
                        throw new InvalidDataException("Profile conflicts with embedding_info.");
                }
                if (key == "embedding_info" && profile != null && JsonSerializer.Deserialize<EmbeddingInfo>(value, MetadataJson)?.ProfileId != profile)
                    throw new InvalidDataException("embedding_info conflicts with profile_id.");
                Execute(db, tx, "INSERT INTO meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value));
            });
            return 0;
        }, cancellationToken);
    }

    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) => RunAsync(() => Read(cancellationToken, (db, tx) =>
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
    }), cancellationToken);

    public Task<IReadOnlyList<KeywordMatch>> KeywordSearchAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        ValidateQuery(query, limit);
        return RunAsync(() => Read(cancellationToken, (db, tx) => (IReadOnlyList<KeywordMatch>)FindWords(db, tx, query, null, null, limit, cancellationToken, fileResults: true)
            .Select(w => new KeywordMatch(w.FileId, w.Rank)).ToArray()), cancellationToken);
    }

    internal Task<SearchSnapshot> ReadSearchSnapshotAsync(string query, string? kind, string? root, bool vectors, bool words, int limit, CancellationToken ct,
        Func<string, bool>? authorize = null, Func<string, string?, long, bool>? matchesSource = null,
        bool allHeaders = false) => RunAsync(() => Read(ct, (db, tx) =>
    {
        long before = Convert.ToInt64(Scalar(db, tx, "PRAGMA data_version"));
        var profile = Scalar(db, tx, "SELECT value FROM meta WHERE key='profile_id'") as string;
        long version = Convert.ToInt64(Scalar(db, tx, "PRAGMA data_version"));
        var matches = words ? FindWords(db, tx, query, kind, root, Math.Max(100, limit), ct, authorize, matchesSource) : [];
        IReadOnlyList<IndexedFile> files;
        if (vectors)
        {
            // The profile read pins the WAL snapshot. Never label an older snapshot with a concurrent writer's newer version.
            if (before == version && vectorCache is { } cached && cached.Version == version) files = cached.Files;
            else
            {
                files = ReadFiles(db, tx, true, ct, vectorSegmentsOnly: true);
                vectorCache = before == version && Convert.ToInt64(Scalar(db, tx, "PRAGMA data_version")) == version ? (version, files) : null;
            }
        }
        else files = ReadHeaders(db, tx, ct, ids: allHeaders ? null : matches.Select(w => w.FileId).ToArray());
        return new SearchSnapshot(files, matches, profile);
    }), ct);

    private static IReadOnlyList<WordEvidence> FindWords(SqliteConnection db, SqliteTransaction tx, string query, string? kind, string? root, int limit, CancellationToken ct,
        Func<string, bool>? authorize = null, Func<string, string?, long, bool>? matchesSource = null, bool fileResults = false)
    {
        bool code = query.StartsWith("code:", StringComparison.OrdinalIgnoreCase);
        if (code) query = query[5..].Trim();
        var terms = SearchText.Terms(query);
        var identifier = code ? SearchText.Identifier(query) : null;
        var literals = DocumentSearchQuery.LiteralTokens(query);
        var literalJson = JsonSerializer.Serialize(literals);
        long requiredMask = (1L << literals.Length) - 1;
        var patterns = identifier != null ? new[] { SearchText.Quote(string.Join(' ', identifier)) } : terms.Select(t => SearchText.Quote(t) + (SearchText.Inflects(t) ? "*" : "")).ToArray();
        for (int i = 0; literals.Length == 0 && identifier == null && i < terms.Length; i++)
            if (terms[i].EnumerateRunes().Count() >= 3 && !patterns[i].EndsWith('*') &&
                Scalar(db, tx, "SELECT 1 FROM fts WHERE fts MATCH $q LIMIT 1", ("$q", patterns[i])) == null) patterns[i] += "*";
        if (patterns.Length == 0) return [];
        string scope = " AND ($kind IS NULL OR f.kind=$kind) AND ($root IS NULL OR replace(f.path,char(92),'/')=$root COLLATE WINSIGHT_PATH OR substr(replace(f.path,char(92),'/'),1,length($prefix))=$prefix COLLATE WINSIGHT_PATH)";
        string maskSql = "CASE WHEN $literals='[]' THEN 0 ELSE WINSIGHT_LITERAL_MASK(coalesce(fts.original,fts.body),f.path,$literals) END";
        string? normalizedRoot = root?.Replace('\\', '/').TrimEnd('/');
        var candidates = new Dictionary<(long File, string Unit), (long Segment, double Rank, bool Fts, long Mask)>();
        var segmentUnits = new Dictionary<long, (long File, string Unit)>();
        var sources = new Dictionary<long, IReadOnlyDictionary<long, SourceAnchor>>();
        void Collect(SqliteDataReader rows, bool fts)
        {
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                long file = rows.GetInt64(0), segment = rows.GetInt64(1), mask = rows.GetInt64(8);
                var path = rows.GetString(3);
                if (authorize != null && !authorize(path) || matchesSource != null && !matchesSource(path, Text(rows, 4), segment)) continue;
                if (!sources.TryGetValue(file, out var anchors)) sources[file] = anchors = ReadAnchors(Text(rows, 4));
                var source = anchors.GetValueOrDefault(segment);
                var key = (file, SearchText.SourceUnit(new(rows.GetString(5), "none", Number(rows, 6), Number(rows, 7), null, Source: source)));
                segmentUnits[segment] = key;
                var rank = rows.GetDouble(2);
                if (candidates.TryGetValue(key, out var old))
                {
                    bool better = fts && (!old.Fts || rank < old.Rank);
                    candidates[key] = (better ? segment : old.Segment, better ? rank : old.Rank, old.Fts || fts, old.Mask | mask);
                }
                else candidates.Add(key, (segment, rank, fts, mask));
            }
        }
        using (var cmd = Command(db, tx, "SELECT s.file,s.id,bm25(fts,3.0,1.0),f.path,f.info,s.kind,s.loc,s.loc_end," + maskSql + " FROM fts " +
            "JOIN segments s ON s.id=fts.rowid JOIN files f ON f.id=s.file WHERE fts MATCH $q" + scope + " ORDER BY bm25(fts,3.0,1.0),s.id",
            ("$literals", literalJson), ("$q", string.Join(" OR ", patterns)), ("$kind", kind), ("$root", normalizedRoot), ("$prefix", normalizedRoot == null ? null : normalizedRoot + "/")))
        using (var rows = cmd.ExecuteReader()) Collect(rows, true);
        var cjk = identifier == null ? terms.Where(SearchText.ContainsCjk).ToArray() : [];
        if (cjk.Length > 0)
        {
            Execute(db, tx, "CREATE VIRTUAL TABLE IF NOT EXISTS temp.winsight_terms USING fts5vocab(main,fts,instance)");
            using var cmd = Command(db, tx, "SELECT DISTINCT s.file,s.id,0.0,f.path,f.info,s.kind,s.loc,s.loc_end," + maskSql + " FROM temp.winsight_terms v " +
                "JOIN segments s ON s.id=v.doc JOIN fts ON fts.rowid=s.id JOIN files f ON f.id=s.file WHERE v.col IN ('name','body') AND EXISTS (SELECT 1 FROM json_each($cjk) q WHERE instr(v.term,q.value)>0)" + scope + " ORDER BY s.id",
                ("$literals", literalJson), ("$cjk", JsonSerializer.Serialize(cjk)), ("$kind", kind), ("$root", normalizedRoot), ("$prefix", normalizedRoot == null ? null : normalizedRoot + "/"));
            using var rows = cmd.ExecuteReader(); Collect(rows, false);
        }
        var shares = new Dictionary<(long File, string Unit), double>();
        for (int i = 0; i < patterns.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var matched = new HashSet<(long File, string Unit)>();
            void Cover(SqliteDataReader rows)
            {
                while (rows.Read()) { ct.ThrowIfCancellationRequested(); if (segmentUnits.TryGetValue(rows.GetInt64(0), out var key)) matched.Add(key); }
            }
            using (var cmd = Command(db, tx, "SELECT DISTINCT s.id FROM fts JOIN segments s ON s.id=fts.rowid JOIN files f ON f.id=s.file WHERE fts MATCH $q" + scope,
                ("$q", patterns[i]), ("$kind", kind), ("$root", normalizedRoot), ("$prefix", normalizedRoot == null ? null : normalizedRoot + "/")))
            using (var rows = cmd.ExecuteReader()) Cover(rows);
            if (identifier == null && SearchText.ContainsCjk(terms[i]))
            {
                using var cmd = Command(db, tx, "SELECT DISTINCT s.id FROM temp.winsight_terms v JOIN segments s ON s.id=v.doc JOIN files f ON f.id=s.file " +
                    "WHERE v.col IN ('name','body') AND instr(v.term,$term)>0" + scope,
                    ("$term", terms[i]), ("$kind", kind), ("$root", normalizedRoot), ("$prefix", normalizedRoot == null ? null : normalizedRoot + "/"));
                using var rows = cmd.ExecuteReader(); Cover(rows);
            }
            foreach (var key in matched) shares[key] = shares.GetValueOrDefault(key) + 1.0 / patterns.Length;
        }
        var ordered = candidates.Where(c => c.Value.Mask == requiredMask).OrderByDescending(c => shares.GetValueOrDefault(c.Key))
            .ThenBy(c => c.Value.Rank).ThenBy(c => c.Key.File).ThenBy(c => c.Key.Unit, StringComparer.Ordinal);
        var selected = (fileResults ? ordered.GroupBy(c => c.Key.File).Select(g => g.First()) : ordered).Take(limit).ToArray();
        var selectedBySegment = selected.ToDictionary(c => c.Value.Segment);
        var evidence = new Dictionary<long, WordEvidence>();
        var ordinary = selected.Where(c => c.Value.Fts).Select(c => c.Value.Segment).ToArray();
        if (ordinary.Length > 0)
        {
            using var cmd = Command(db, tx, "SELECT s.id,s.file,bm25(fts,3.0,1.0),s.kind,s.modality,s.loc,s.loc_end,s.excerpt,snippet(fts,1,'«','»','…',12) " +
                "FROM fts JOIN segments s ON s.id=fts.rowid WHERE fts MATCH $q AND s.id IN (SELECT value FROM json_each($ids))",
                ("$q", string.Join(" OR ", patterns)), ("$ids", JsonSerializer.Serialize(ordinary)));
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                long id = rows.GetInt64(0), file = rows.GetInt64(1);
                var selectedRow = selectedBySegment[id];
                var segment = Segment(rows, 3, false) with { Source = sources[file].GetValueOrDefault(id) };
                evidence.Add(id, new(file, rows.GetDouble(2), shares.GetValueOrDefault(selectedRow.Key), segment, Text(rows, 8), true));
            }
        }
        var cjkIds = selected.Where(c => !c.Value.Fts).Select(c => c.Value.Segment).ToArray();
        if (cjkIds.Length > 0)
        {
            // Segmented FTS gives Chinese units a real bm25 score instead of a substring share. When the
            // segmenter is unavailable the channel is skipped and the caller is told lexical is degraded.
            var segmented = new Dictionary<long, (double Rank, string? Snippet)>();
            var segmentPatterns = SegmentedQueryPatterns(query);
            if (segmentPatterns.Length != 0)
            {
                using var segCmd = Command(db, tx, "SELECT s.id,bm25(fts_seg,1.0),snippet(fts_seg,0,'«','»','…',12) FROM fts_seg JOIN segments s ON s.id=fts_seg.rowid " +
                    "WHERE fts_seg MATCH $q AND s.id IN (SELECT value FROM json_each($ids))",
                    ("$q", string.Join(" OR ", segmentPatterns)), ("$ids", JsonSerializer.Serialize(cjkIds)));
                using var segRows = segCmd.ExecuteReader();
                while (segRows.Read()) segmented[segRows.GetInt64(0)] = (segRows.GetDouble(1), Text(segRows, 2));
            }
            using var cmd = Command(db, tx, "SELECT id,file,kind,modality,loc,loc_end,excerpt FROM segments WHERE id IN (SELECT value FROM json_each($ids))", ("$ids", JsonSerializer.Serialize(cjkIds)));
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                long id = rows.GetInt64(0), file = rows.GetInt64(1);
                var selectedRow = selectedBySegment[id];
                var segment = Segment(rows, 2, false) with { Source = sources[file].GetValueOrDefault(id) };
                var ranked = segmented.TryGetValue(id, out var hit);
                evidence.Add(id, new(file, ranked ? hit.Rank : 0, shares.GetValueOrDefault(selectedRow.Key), segment,
                    ranked ? hit.Snippet : null, ranked));
            }
        }
        return selected.Select(c => evidence.TryGetValue(c.Value.Segment, out var word) ? word
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

    internal static IReadOnlyDictionary<long, SourceAnchor> ReadAnchors(string? info)
    {
        if (info is null) return new Dictionary<long, SourceAnchor>();
        using var document = JsonDocument.Parse(info);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("File info must be an object.");
        if (!document.RootElement.TryGetProperty("_sourceAnchorsV1", out var value)) return new Dictionary<long, SourceAnchor>();
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Malformed source-anchor metadata.");
        var result = new Dictionary<long, SourceAnchor>();
        foreach (var property in value.EnumerateObject())
        {
            if (result.Count >= 5000 || !long.TryParse(property.Name, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0 || result.ContainsKey(id))
                throw new InvalidDataException("Invalid source-anchor key/count.");
            var source = property.Value.Deserialize<SourceAnchor>(MetadataJson) ?? throw new InvalidDataException("Missing source anchor.");
            ValidateAnchor(source);
            result.Add(id, source);
        }
        return result;
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

    private static byte[] Encode(float[] vector)
    {
        VectorContract.Validate(vector);
        var bytes = new byte[VectorContract.Dimension * 2];
        for (int i = 0; i < vector.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), BitConverter.HalfToUInt16Bits((Half)vector[i]));
        return bytes;
    }

    private static float[] Decode(byte[] bytes)
    {
        if (bytes.Length != VectorContract.Dimension * 2) throw new InvalidDataException("Vector blob must contain 768 little-endian float16 values.");
        var vector = new float[VectorContract.Dimension];
        for (int i = 0; i < vector.Length; i++) vector[i] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2)));
        VectorContract.Validate(vector);
        return vector;
    }

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

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; disposed = true; vectorCache = null; connection?.Dispose(); connection = null; }
        finally { gate.Release(); }
    }


    /// <summary>Query-side tokens for the segmented FTS channel; empty when the segmenter is unusable.</summary>
    private static string[] SegmentedQueryPatterns(string query)
    {
        try
        {
            var tokens = NativeSegmenter.Segment(query)
                .Where(token => token.Length != 0 && token != "AND" && token != "OR" && token != "NOT")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(token => "\"" + token.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
                .ToArray();
            return tokens;
        }
        catch (SegmenterUnavailableException)
        {
            return Array.Empty<string>();
        }
    }

    private static readonly string Schema = $$"""
        CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE files(id INTEGER PRIMARY KEY,path TEXT NOT NULL UNIQUE,kind TEXT NOT NULL,size INTEGER NOT NULL,mtime REAL NOT NULL,
            inode INTEGER NOT NULL,device INTEGER NOT NULL,state TEXT NOT NULL DEFAULT 'pending',detail TEXT,info TEXT,indexed_at REAL);
        CREATE INDEX files_inode ON files(device,inode);
        CREATE INDEX files_state ON files(state,kind);
        CREATE TABLE segments(id INTEGER PRIMARY KEY,file INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,kind TEXT NOT NULL,
            modality TEXT NOT NULL,loc REAL,loc_end REAL,excerpt TEXT,vec BLOB);
        CREATE INDEX segments_file ON segments(file);
        CREATE VIRTUAL TABLE fts USING fts5(name,body,original UNINDEXED,tokenize="{{Tokenizer}}");
        CREATE VIRTUAL TABLE fts_seg USING fts5(tokens,tokenize="unicode61 remove_diacritics 2");
        CREATE TABLE IF NOT EXISTS index_tasks (path TEXT PRIMARY KEY,root TEXT NOT NULL,code INTEGER NOT NULL,kind TEXT NOT NULL,tombstone TEXT,state TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,due_utc TEXT NOT NULL,seen_utc TEXT NOT NULL,reason TEXT);
        CREATE INDEX IF NOT EXISTS ix_index_tasks_due ON index_tasks(state, due_utc);
        CREATE TABLE IF NOT EXISTS usn_checkpoints (volume_serial TEXT PRIMARY KEY, journal_id TEXT, next_usn TEXT, reconcile_utc TEXT);
        INSERT OR IGNORE INTO meta(key,value) VALUES('lexicon_schema','2');
        INSERT OR IGNORE INTO meta(key,value) VALUES('dictionary_fingerprint','');
        PRAGMA user_version=5;
        """;
}

internal enum IndexInitializationStage { SchemaCreated, BeforePublish }
internal sealed record WordEvidence(long FileId, double Rank, double Coverage, IndexSegment Segment, string? Snippet, bool ExactLiteralsVerified = false);
internal sealed record SearchSnapshot(IReadOnlyList<IndexedFile> Files, IReadOnlyList<WordEvidence> Words, string? Profile);
