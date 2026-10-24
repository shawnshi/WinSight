using System.Text.Json;
using System.Text.Json.Nodes;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Media;
using WinSight.Infrastructure.Search;
using WinSight.Shared;

namespace WinSight.Infrastructure.Indexing;

internal sealed class NativeDocumentEmbeddingException(Exception cause) : IOException("PDF page embedding dependency failed; run stopped.", cause);

internal sealed class NativeDocumentPages(NativeMediaProcessClient? client)
{
    internal const int MaxPages = 32;
    internal const int MaxEdge = 1600;
    internal static string Revision => $"pdf-page-visual-ocr-v1:{OcrIdentity.ExtractionRevision}:pages{MaxPages}:edge{MaxEdge}";
    internal static bool IsCurrent(string? info)
    {
        if (info is null) return false;
        using var doc = JsonDocument.Parse(info);
        return doc.RootElement.TryGetProperty("native_document_revision", out var value) && value.GetString() == Revision;
    }
    internal static bool IsStablePolicyPartial(string? info)
    {
        if (!IsCurrent(info)) return false;
        using var doc = JsonDocument.Parse(info!);
        var root = doc.RootElement;
        return root.TryGetProperty("native_document_policy_limited", out var value) && value.ValueKind == JsonValueKind.True
            && root.TryGetProperty("pages", out var pages) && pages.GetInt32() > MaxPages
            && root.TryGetProperty("native_document_visual_pages", out var visual) && visual.GetInt32() == MaxPages
            && root.TryGetProperty("native_document_coverage_complete", out var complete) && complete.ValueKind == JsonValueKind.False;
    }
    internal async Task<ExtractionResult> AppendAsync(string path, string title, ExtractionResult textResult,
        IEmbeddingService? embeddings, EmbeddingInfo? info, CancellationToken caller)
    {
        if (textResult.Info is null) throw new InvalidDataException("PDF text metadata is missing.");
        var metadata = JsonNode.Parse(textResult.Info)!.AsObject();
        int pages = metadata["pages"]?.GetValue<int>() ?? throw new InvalidDataException("PDF text page count is missing.");
        if (pages <= 0) throw new InvalidDataException("PDF page count is invalid.");
        if (textResult.Segments.Any(s => s.Excerpt?.Contains("PRIVATE KEY-----", StringComparison.Ordinal) == true))
            return new([], "skipped", "PDF text contains private-key material; content was not indexed.");
        var rasterPages = metadata["raster_pages"]?.AsArray().Select(p => p!.GetValue<int>()).ToHashSet() ?? [];
        bool textLimited = metadata["text_limits_reached"]?.GetValue<bool>() == true;
        var segments = textResult.Segments.ToList();
        var problems = new List<string>();
        bool transientFailure = false;
        int visualPages = 0, ocrPages = 0;
        if (client is null || embeddings is null || info is null || !info.Capabilities.Contains("image", StringComparer.OrdinalIgnoreCase))
            problems.Add("Native PDF visual/OCR coverage unavailable: helper or image embedding capability missing.");
        else
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            deadline.CancelAfter(TimeSpan.FromSeconds(180));
            var token = deadline.Token;
            for (int number = 1; number <= Math.Min(pages, MaxPages); number++)
            {
                if (segments.Count >= ContentExtractor.MaxSegments) { problems.Add("PDF combined segment bound reached."); break; }
                caller.ThrowIfCancellationRequested();
                string stage = "render";
                try
                {
                    await using var page = await client.RenderPdfPageAsync(new(path, number, MaxEdge), token);
                    if (page.PageCount != pages) throw new InvalidDataException("PDF parsers disagree on page count.");
                    var location = new SourceLocation("pdf-page", Page: number);
                    var body = textResult.Segments.Where(s => s.Source?.Location.Type == "pdf-page" && s.Source.Location.Page == number).ToArray();
                    string bodyText = string.Join("\n", body.Select(s => s.Excerpt));
                    var fields = body.SelectMany(s => s.Source?.Fields ?? []).Distinct().ToList();
                    IReadOnlyList<OcrTextRegion> regions = [];
                    string ocrText = "";
                    bool needOcr = bodyText.Length < 200 || rasterPages.Contains(number);
                    if (needOcr)
                    {
                        stage = "OCR";
                        var inspection = await client.InspectImageAsync(page.ArtifactPath, token);
                        regions = inspection.OcrRegions;
                        ocrText = NativeImageExtraction.NormalizeOcrText(inspection.OcrText);
                        if (inspection.State != "done") { problems.Add($"Page{number}: OCR coverage partial."); transientFailure = true; }
                        ocrPages++;
                        fields.AddRange(DocumentSearchQuery.FieldsFromText(ocrText));
                        if (ocrText.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
                            return new([], "skipped", "PDF OCR contains private-key material; content was not indexed.");
                    }
                    var pageFields = fields.Distinct().Take(64).ToArray();
                    if (fields.Distinct().Count() > 64) problems.Add($"Page{number}: structured field limit reached.");
                    for (int index = 0; index < textResult.Segments.Count; index++)
                        if (segments[index].Source?.Location.Type == "pdf-page" && segments[index].Source!.Location.Page == number)
                            segments[index] = segments[index] with { Source = segments[index].Source! with { Fields = pageFields, Regions = regions } };
                    var visual = new IndexSegment("page", "image", number, number, null,
                        Source: new(location, "page-visual", pageFields, regions));
                    stage = "embedding";
                    var visualVector = await EmbedAsync(new("image", page.ArtifactPath, 280), page, embeddings, token);
                    if (visualVector is not null) { segments.Add(visual with { Vector = visualVector }); visualPages++; }
                    else { problems.Add($"Page{number}: visual embedding failed."); transientFailure = true; }
                    if (ocrText.Length != 0)
                    {
                        var (chunks, cut) = ContentExtractor.Chunks(ocrText, token);
                        if (cut) problems.Add($"Page{number}: OCR chunk coverage partial.");
                        foreach (var chunk in chunks)
                        {
                            if (segments.Count >= ContentExtractor.MaxSegments || segments.Sum(s => (long)(s.Excerpt?.Length ?? 0)) + chunk.Excerpt!.Length > ContentExtractor.MaxCharacters)
                            { problems.Add("PDF combined text/segment bound reached."); break; }
                            float[]? vector = null;
                            if (info.Capabilities.Contains("text", StringComparer.OrdinalIgnoreCase))
                                vector = await EmbedAsync(new("text", $"title: {title} | text: {chunk.Excerpt}"), page, embeddings, token);
                            if (vector is null) { problems.Add($"Page{number}: OCR text embedding failed."); transientFailure = true; }
                            segments.Add(chunk with { Location = number, LocationEnd = number, Vector = vector,
                                Source = new(location, "ocr", pageFields, regions) });
                        }
                    }
                }
                catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                { problems.Add("PDF page coverage hit the shared180s budget."); transientFailure = true; break; }
                catch (NativeDocumentEmbeddingException) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or NotSupportedException)
                { problems.Add($"Page{number}: {stage} failed ({Category(error)})."); transientFailure = true; break; }
            }
        }
        if (pages > MaxPages) problems.Add($"PDF visual/OCR page coverage limited to first{MaxPages} pages; extractable text may cover later pages.");
        if (textLimited) problems.Add("PDF text extraction limit reached.");
        bool completeVisual = visualPages == pages;
        bool stablePolicy = pages > MaxPages && visualPages == MaxPages && !transientFailure && !textLimited && problems.Count == 1;
        metadata["native_document_revision"] = Revision;
        metadata["native_document_visual_pages"] = visualPages;
        metadata["native_document_ocr_pages"] = ocrPages;
        metadata["native_document_policy_limited"] = stablePolicy;
        metadata["native_document_coverage_complete"] = completeVisual && problems.Count == 0;
        return new(segments, completeVisual && problems.Count == 0 ? "done" : "partial", problems.Count == 0 ? null : string.Join(" ", problems.Distinct()), metadata.ToJsonString());
    }
    private static async Task<float[]?> EmbedAsync(EmbeddingInput input, NativePdfPageLease page, IEmbeddingService embeddings, CancellationToken token)
    {
        IReadOnlyList<EmbeddingResult> results;
        try { results = await embeddings.EmbedAsync([input], token); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // A dependency exception does not prove the consumer has released its image path.
            page.Retain();
            if (error is OperationCanceledException) throw;
            throw new NativeDocumentEmbeddingException(error);
        }
        token.ThrowIfCancellationRequested();
        if (results.Count != 1 || results[0].Error is not null || results[0].Vector is not { } vector) return null;
        try { VectorContract.Validate(vector); return vector; }
        catch (InvalidDataException) { return null; }
    }
    private static string Category(Exception error) => error switch
    { InvalidDataException => "invalid-data", IOException => "io", JsonException => "protocol", ArgumentException => "argument", NotSupportedException => "unsupported", _ => "other" };
}
