using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinSight.InferenceHost;

internal sealed record WordPageResult(int Protocol, string InputPath, int PageNumber, int PageCount,
    int PixelWidth, int PixelHeight, string RenderPolicy, string Artifact);

internal static partial class NativeMediaCommand
{
    private const string WordRenderPolicy = "word-export-fixed-v1";

    internal static async Task<WordPageResult> RenderWordPageRequestAsync(string requestPath, CancellationToken token = default)
    {
        using var requestLease = OpenPinnedFile(ValidateLocalPath(requestPath, GetDriveType), MaxRequestBytes);
        var bytes = new byte[checked((int)requestLease.Length)];
        await requestLease.ReadExactlyAsync(bytes, token);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Word page request must be an object.");
        int pageNumber = root.GetProperty("pageNumber").GetInt32();
        int edge = root.GetProperty("maxEdge").GetInt32();
        int? paragraphNumber = root.TryGetProperty("paragraphNumber", out var paraProp) && paraProp.ValueKind == JsonValueKind.Number
            ? paraProp.GetInt32()
            : null;
        var input = ValidateLocalPath(root.GetProperty("inputPath").GetString()!, GetDriveType);
        var output = ValidateLocalPath(root.GetProperty("outputDirectory").GetString()!, GetDriveType);
        ValidateAncestors(output, includeLeaf: false);
        if (Path.Exists(output)) throw new IOException("Word output directory must be new.");
        Directory.CreateDirectory(output);

        string tempPdf = Path.Combine(output, "slice.pdf");

        // Run Word COM automation strictly in a dedicated STA thread to respect Windows COM requirements
        var (resolvedPage, totalDocPages) = await RunWordExportStaAsync(input, tempPdf, pageNumber, paragraphNumber);

        if (!File.Exists(tempPdf)) throw new FileNotFoundException("Word export failed to produce single-page PDF slice.");

        try
        {
            var sourceStorage = await StorageFile.GetFileFromPathAsync(tempPdf).AsTask(token);
            var pdfDoc = await PdfDocument.LoadFromFileAsync(sourceStorage).AsTask(token);
            if (pdfDoc.PageCount == 0) throw new InvalidDataException("Exported Word PDF slice is empty.");

        using (var pdfPage = pdfDoc.GetPage(0))
        {
            var (width, height) = await RenderPdfPageToOutputAsync(pdfPage, edge, output, token, createDirectory: false);
            return new WordPageResult(1, input, resolvedPage, Math.Max(totalDocPages, (int)pdfDoc.PageCount), (int)width, (int)height, WordRenderPolicy, "page.png");
        }
        }
        finally
        {
            try { File.Delete(tempPdf); } catch { }
        }
    }

    private static Task<(int ResolvedPage, int TotalPages)> RunWordExportStaAsync(string input, string tempPdf, int pageNumber, int? paragraphNumber)
    {
        var done = new TaskCompletionSource<(int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var wordType = Type.GetTypeFromProgID("Word.Application");
                if (wordType is null) throw new NotSupportedException("Microsoft Word is not installed.");

                string dir = Path.GetDirectoryName(tempPdf)!;
                var copy = Path.Combine(dir, "input" + Path.GetExtension(input));
                using (var sourceStream = OpenPinnedFile(input, MaxPreviewInputBytes))
                using (var copyStream = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    sourceStream.CopyTo(copyStream);

                var com = new List<object>();
                int resolvedPage = Math.Max(1, pageNumber);
                int totalPages = 1;
                try
                {
                    var activated = Activator.CreateInstance(wordType)!;
                    com.Add(activated);
                    var app = new OfficeObject(activated, com);
                    app.Set("Visible", false);
                    try { app.Set("DisplayAlerts", 0); } catch { } // wdAlertsNone
                    try { app.Set("AutomationSecurity", 3); } catch { } // msoAutomationSecurityForceDisable

                    var docs = app.Object("Documents");
                    // Open(FileName, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, Visible: false)
                    var doc = docs.Object("Open", true, copy, false, true, false, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, false);

                    if (paragraphNumber is > 0)
                    {
                        try
                        {
                            var paras = doc.Object("Paragraphs");
                            int paraCount = paras.Get<int>("Count");
                            if (paraCount > 0)
                            {
                                int targetPara = Math.Clamp(paragraphNumber.Value, 1, paraCount);
                                var para = paras.Object("Item", true, targetPara);
                                var range = para.Object("Range");
                                // wdActiveEndPageNumber = 3
                                int p = Convert.ToInt32(range.Get<object>("Information", 3));
                                if (p >= 1) resolvedPage = p;
                            }
                        }
                        catch { /* keep requested pageNumber */ }
                    }

                    // ExportAsFixedFormat(OutputFileName, ExportFormat, OpenAfterExport, OptimizeFor, Range, From, To)
                    // wdExportFormatPDF = 17, wdExportOptimizeForOnScreen = 1, wdExportRangeFromTo = 3
                    try
                    {
                        doc.Call("ExportAsFixedFormat", tempPdf, 17, false, 1, 3, resolvedPage, resolvedPage);
                        totalPages = resolvedPage;
                    }
                    catch when (resolvedPage > 1)
                    {
                        resolvedPage = 1;
                        totalPages = 1;
                        doc.Call("ExportAsFixedFormat", tempPdf, 17, false, 1, 3, 1, 1);
                    }

                    doc.Call("Close", false); // SaveChanges: false
                    try { app.Call("Quit", false); } catch { }
                }
                finally
                {
                    for (int n = com.Count - 1; n >= 0; n--)
                        if (Marshal.IsComObject(com[n])) Marshal.FinalReleaseComObject(com[n]);
                    com.Clear();
                    try { File.Delete(copy); } catch { }
                }

                done.SetResult((resolvedPage, totalPages));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }
}
