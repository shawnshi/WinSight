namespace WinSight.Infrastructure.Media;

public sealed record NativePdfPageRequest(string SourcePath, int PageNumber, int MaxEdge = 1600)
{
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourcePath);
        if (PageNumber is < 1 or > 5000 || MaxEdge is < 256 or > 2600)
            throw new ArgumentOutOfRangeException(nameof(PageNumber), "PDF page/edge outside bounded contract.");
    }
}
