using System.Text.Json;

namespace WinSight.Infrastructure.Media;

public sealed class NativePdfPageLease : IAsyncDisposable
{
    private readonly MediaTaskFiles files;
    private readonly object gate = new();
    private Task? disposal;
    private bool retained;
    public string ArtifactPath => files.ArtifactPath;
    public int PageNumber { get; }
    public int PageCount { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public string RenderPolicy => "winrt-pdf-dip-normalized-v1";
    private NativePdfPageLease(MediaTaskFiles files, int page, int pages, int width, int height) =>
        (this.files, PageNumber, PageCount, PixelWidth, PixelHeight) = (files, page, pages, width, height);

    internal static NativePdfPageLease Parse(string payload, NativePdfPageRequest request, MediaTaskFiles files, CancellationToken token)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("PDF page result must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("protocol" or "inputPath" or "pageNumber" or "pageCount" or "pixelWidth" or "pixelHeight" or "renderPolicy" or "artifact") || !names.Add(property.Name))
                throw new InvalidDataException("Unknown/duplicate PDF page result member.");
        if (names.Count != 8 || root.GetProperty("protocol").GetInt32() != 1
            || !string.Equals(root.GetProperty("inputPath").GetString(), request.SourcePath, StringComparison.OrdinalIgnoreCase)
            || root.GetProperty("pageNumber").GetInt32() != request.PageNumber
            || root.GetProperty("artifact").GetString() != "page.png" || root.GetProperty("renderPolicy").GetString() != "winrt-pdf-dip-normalized-v1")
            throw new InvalidDataException("PDF page result identity/policy mismatch.");
        int pages = root.GetProperty("pageCount").GetInt32(), width = root.GetProperty("pixelWidth").GetInt32(), height = root.GetProperty("pixelHeight").GetInt32();
        if (pages < request.PageNumber || pages > 1_000_000 || width < 1 || height < 1 || width > request.MaxEdge || height > request.MaxEdge || Math.Max(width,height) != request.MaxEdge)
            throw new InvalidDataException("Invalid PDF page count/canonical pixel bounds.");
        files.PinArtifact("page.png");
        NativeMediaPng.ValidateDocumentPage(files.ArtifactStream, width, height, request.MaxEdge, token);
        files.ArtifactStream.Position = 0;
        return new(files, request.PageNumber, pages, width, height);
    }
    internal void Retain()
    {
        lock (gate)
        {
            if (disposal is not null) throw new InvalidOperationException("Cannot retain a disposed PDF artifact.");
            retained = true; MediaRetention.Keep(this);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (retained) return ValueTask.CompletedTask;
            disposal ??= Task.Run(files.Cleanup);
            return new(disposal);
        }
    }
}
