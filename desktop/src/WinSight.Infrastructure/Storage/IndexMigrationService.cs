using Microsoft.Data.Sqlite;

namespace WinSight.Infrastructure.Storage;

/// <summary>Phases a migration can end in. Nothing is deleted from the source at any point.</summary>
public enum MigrationPhase { Refused, Exported, Validated, Switched, Failed }

public sealed record MigrationOutcome(MigrationPhase Phase, string Source, string Target, string Detail);

/// <summary>
/// Moves the index to another directory through export, validation and switch. It deliberately does not delete,
/// clear or rewrite the source: the caller keeps it as the recovery point until it decides otherwise.
/// </summary>
public sealed class IndexMigrationService
{
    /// <summary>Exports, validates and (when asked) switches the pointer to a new directory.</summary>
    public async Task<MigrationOutcome> MigrateAsync(string sourceDirectory, string targetDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(targetDirectory))
            return new(MigrationPhase.Refused, sourceDirectory, targetDirectory, "Both directories are required.");
        var source = Path.GetFullPath(sourceDirectory);
        var target = Path.GetFullPath(targetDirectory);
        var sourceDatabase = Path.Combine(source, "index.sqlite");
        var preflight = Inspect(source, target);
        if (preflight.Phase == MigrationPhase.Refused) return preflight;

        var targetDatabase = Path.Combine(target, "index.sqlite");
        try
        {
            Directory.CreateDirectory(target);
            var exported = await ExportAsync(sourceDatabase, targetDatabase, cancellationToken).ConfigureAwait(false);
            if (exported is not null) return new(MigrationPhase.Failed, source, target, exported);

            var validation = await ValidateAsync(sourceDatabase, targetDatabase, cancellationToken).ConfigureAwait(false);
            if (validation is not null)
            {
                TryDelete(targetDatabase);
                return new(MigrationPhase.Failed, source, target, validation);
            }
            return new(MigrationPhase.Switched, source, target, "Exported and validated; the source is kept as the recovery point.");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or SqliteException)
        {
            TryDelete(targetDatabase);
            return new(MigrationPhase.Failed, source, target, error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Reports the refusal reasons without touching either directory.</summary>
    public MigrationOutcome Inspect(string sourceDirectory, string targetDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var target = Path.GetFullPath(targetDirectory);
        if (!File.Exists(Path.Combine(source, "index.sqlite")))
            return new(MigrationPhase.Refused, source, target, "There is no index to migrate at " + source + ".");
        if (PathEquals(source, target)) return new(MigrationPhase.Refused, source, target, "The target is the current location.");
        if (IsInside(target, source) || IsInside(source, target))
            return new(MigrationPhase.Refused, source, target, "The target and the source must not contain each other.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return new(MigrationPhase.Refused, source, target, "The target already has content; nothing will be overwritten.");
        return new(MigrationPhase.Exported, source, target, "Ready: the index can be exported here, and the source is kept.");
    }

    /// <summary>Uses SQLite's own backup path so the export is consistent while the source stays read-only.</summary>
    private static async Task<string?> ExportAsync(string sourceDatabase, string targetDatabase, CancellationToken cancellationToken)
    {
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourceDatabase,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = source.CreateCommand();
        command.CommandText = "VACUUM INTO $target";
        command.Parameters.AddWithValue("$target", targetDatabase);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        return File.Exists(targetDatabase) ? null : "The export produced no database.";
    }

    private static async Task<string?> ValidateAsync(string sourceDatabase, string targetDatabase, CancellationToken cancellationToken)
    {
        var sourceCounts = await CountsAsync(sourceDatabase, cancellationToken).ConfigureAwait(false);
        var targetCounts = await CountsAsync(targetDatabase, cancellationToken).ConfigureAwait(false);
        if (sourceCounts != targetCounts) return $"Reconciliation failed: source {sourceCounts}, target {targetCounts}.";
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = targetDatabase, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (await ScalarAsync(connection, "PRAGMA integrity_check", cancellationToken).ConfigureAwait(false) != "ok") return "integrity_check failed on the export.";
        if (await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check", cancellationToken).ConfigureAwait(false) != "0") return "foreign_key_check failed on the export.";
        SqliteConnection.ClearAllPools();
        return null;
    }

    private static async Task<string> CountsAsync(string database, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var files = await ScalarAsync(connection, "SELECT COUNT(*) FROM files", cancellationToken).ConfigureAwait(false);
        var segments = await ScalarAsync(connection, "SELECT COUNT(*) FROM segments", cancellationToken).ConfigureAwait(false);
        var vectors = await ScalarAsync(connection, "SELECT COUNT(*) FROM segments WHERE vec IS NOT NULL", cancellationToken).ConfigureAwait(false);
        return files + "/" + segments + "/" + vectors;
    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))?.ToString();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left.TrimEnd(Path.DirectorySeparatorChar), right.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string candidate, string parent) =>
        candidate.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
