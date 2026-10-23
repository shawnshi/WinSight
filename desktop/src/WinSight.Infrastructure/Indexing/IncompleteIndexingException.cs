namespace WinSight.Infrastructure.Indexing;

public sealed record IndexingFailure(string Root, string Path, string Phase, string Detail);

/// <summary>Successful file writes remain committed, but one or more root scans/deletion checks were incomplete.</summary>
public sealed class IncompleteIndexingException : IOException
{
    public IReadOnlyList<IndexingFailure> Failures { get; }
    public int Completed { get; }
    public int Failed { get; }
    public int Partial { get; }

    internal IncompleteIndexingException(IReadOnlyList<IndexingFailure> failures, int completed, int failed, int partial)
        : base($"Indexing incomplete: {failures.Count} root scan/reconciliation failure(s). " +
               string.Join("; ", failures.Take(3).Select(f => $"{f.Phase} [{f.Path}]: {f.Detail}")))
    {
        Failures = Array.AsReadOnly(failures.ToArray());
        Completed = completed;
        Failed = failed;
        Partial = partial;
    }
}
