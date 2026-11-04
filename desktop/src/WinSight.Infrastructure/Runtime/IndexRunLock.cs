namespace WinSight.Infrastructure.Runtime;

/// <summary>
/// Cross-process guard for index writer runs. SQLite already serializes individual commits, but two whole indexing runs
/// (the app's background pass and a CLI <c>index</c>, or a maintenance request) would duplicate work and keep stalling
/// each other on the database lock. The lock is an exclusive file handle, so Windows releases it if the owner dies.
/// </summary>
public sealed class IndexRunLock : IDisposable
{
    internal const string FileName = "index-writer.lock";
    private FileStream? stream;

    private IndexRunLock(FileStream stream) => this.stream = stream;

    /// <summary>Returns the held lock, or null when another process (or run) owns it.</summary>
    public static IndexRunLock? TryAcquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        try
        {
            return new IndexRunLock(new FileStream(Path.Combine(dataDirectory, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1));
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33) { return null; }   // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
    }

    public void Dispose()
    {
        stream?.Dispose();
        stream = null;
    }
}
