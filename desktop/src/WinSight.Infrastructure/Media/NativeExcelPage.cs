using System.Text.Json;

namespace WinSight.Infrastructure.Media;

public sealed record NativeExcelPageRequest(string SourcePath, int PageNumber = 1, int MaxEdge = 2400, string? SheetName = null)
{
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourcePath);
        if (PageNumber is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(PageNumber));
        if (MaxEdge is < 256 or > 2600) throw new ArgumentOutOfRangeException(nameof(MaxEdge));
    }
}

public sealed class NativeExcelPageLease : IAsyncDisposable
{
    private readonly MediaTaskFiles files;
    private readonly object gate = new();
    private Task? disposal;

    public string ArtifactPath => files.ArtifactPath;
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public int PageCount { get; }

    private NativeExcelPageLease(MediaTaskFiles files, int width, int height, int pageCount) =>
        (this.files, PixelWidth, PixelHeight, PageCount) = (files, width, height, pageCount);

    internal static NativeExcelPageLease Parse(string payload, NativeExcelPageRequest request, MediaTaskFiles files, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Excel page result.");
        int width = root.GetProperty("pixelWidth").GetInt32();
        int height = root.GetProperty("pixelHeight").GetInt32();
        int pageCount = root.TryGetProperty("pageCount", out var pc) ? pc.GetInt32() : 1;
        files.PinArtifact("page.png");
        NativeMediaPng.ValidateDocumentPage(files.ArtifactStream, width, height, request.MaxEdge, token);
        files.ArtifactStream.Position = 0;
        return new(files, width, height, pageCount);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposal ??= Task.Run(files.Cleanup);
            return new(disposal);
        }
    }
}
