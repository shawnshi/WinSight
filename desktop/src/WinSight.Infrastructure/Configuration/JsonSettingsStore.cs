using System.Text.Json;
using WinSight.Core;

namespace WinSight.Infrastructure.Configuration;

public sealed class JsonSettingsStore(string settingsPath) : ISettingsStore
{
    private const int MaxBytes = 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 16
    };
    public string SettingsPath { get; } = Path.GetFullPath(settingsPath);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            var buffer = new byte[MaxBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (read == 0) break;
                length += read;
            }
            if (length > MaxBytes) throw new InvalidDataException("Settings exceed the 1 MiB limit.");
            var bytes = buffer.AsMemory(0, length);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            var settings = JsonSerializer.Deserialize<AppSettings>(bytes.Span, Json)
                ?? throw new InvalidDataException("Settings must not be null.");
            Validate(settings);
            return settings;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Settings exceed the 1 MiB limit.");
        var snapshot = JsonSerializer.Deserialize<AppSettings>(bytes, Json)!;
        Validate(snapshot);
        await gate.WaitAsync(cancellationToken);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            // A separate file serializes compare-and-replace across cooperating CLI/App store instances.
            using var writeLease = new FileStream(SettingsPath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var committed = await new JsonSettingsStore(SettingsPath).LoadAsync(cancellationToken);
            if (snapshot.Revision != committed.Revision)
                throw new SettingsConflictException(snapshot.Revision, committed.Revision);
            snapshot.Revision = checked(committed.Revision + 1);
            Validate(snapshot);
            bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
            if (bytes.Length > MaxBytes) throw new InvalidDataException("Settings exceed the 1 MiB limit.");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, SettingsPath + ".previous");
            else File.Move(temporary, SettingsPath);
            settings.Revision = snapshot.Revision;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }

    public static void Validate(AppSettings settings)
    {
        if (settings.Version != 1) throw new InvalidDataException($"Unsupported settings version {settings.Version}.");
        if (settings.Revision is < 0 or long.MaxValue) throw new InvalidDataException("Invalid settings revision.");
        if (settings.Folders is null || settings.Skipped is null || settings.SkipTypes is null
            || settings.CodeFolders is null || settings.CodeSkipped is null || settings.IndexHours is null)
            throw new InvalidDataException("Settings collections and index hours must not be null.");
        if (settings.Theme is not ("Default" or "Light" or "Dark"))
            throw new InvalidDataException("Theme must be Default, Light or Dark.");
        if (settings.IndexHours.Start is < 0 or >= 1440 || settings.IndexHours.End is < 0 or >= 1440)
            throw new InvalidDataException("Index hours are minutes since midnight in 0..1439.");
        foreach (var path in settings.Folders.Concat(settings.CodeFolders).Concat(settings.Skipped).Concat(settings.CodeSkipped))
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("Folder settings require absolute paths.");
            if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Device paths are not authorized folder roots.");
        }
        foreach (var path in new[] { settings.IndexDirectory, settings.ModelDirectory, settings.InferenceExecutable, settings.ManifestPath })
            if (path is not null && (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                || path.StartsWith(@"\\.\", StringComparison.Ordinal)
                || path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Storage, model and executable paths must be absolute when supplied.");
        if (string.IsNullOrWhiteSpace(settings.Hotkey)) throw new InvalidDataException("Hotkey must not be empty.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate settings field: {property.Name}");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
