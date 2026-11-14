using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinSight.InferenceHost;

internal sealed record ExcelPageResult(int Protocol, string InputPath, int PageNumber, int PageCount,
    int PixelWidth, int PixelHeight, string RenderPolicy, string Artifact);

internal static partial class NativeMediaCommand
{
    private const string ExcelRenderPolicy = "excel-export-fixed-v1";

    internal static async Task<ExcelPageResult> RenderExcelPageRequestAsync(string requestPath, CancellationToken token = default)
    {
        using var requestLease = OpenPinnedFile(ValidateLocalPath(requestPath, GetDriveType), MaxRequestBytes);
        var bytes = new byte[checked((int)requestLease.Length)];
        await requestLease.ReadExactlyAsync(bytes, token);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Excel page request must be an object.");
        int pageNumber = root.GetProperty("pageNumber").GetInt32();
        int edge = root.GetProperty("maxEdge").GetInt32();
        string? sheetName = root.TryGetProperty("sheetName", out var sheetProp) && sheetProp.ValueKind == JsonValueKind.String
            ? sheetProp.GetString()
            : null;
        var input = ValidateLocalPath(root.GetProperty("inputPath").GetString()!, GetDriveType);
        var output = ValidateLocalPath(root.GetProperty("outputDirectory").GetString()!, GetDriveType);
        ValidateAncestors(output, includeLeaf: false);
        if (Path.Exists(output)) throw new IOException("Excel output directory must be new.");
        Directory.CreateDirectory(output);

        string tempPdf = Path.Combine(output, "slice.pdf");

        // Run Excel COM automation strictly in a dedicated STA thread to respect Windows COM requirements
        await RunExcelExportStaAsync(input, tempPdf, pageNumber, sheetName);

        if (!File.Exists(tempPdf)) throw new FileNotFoundException("Excel export failed to produce single-page PDF slice.");

        try
        {
            var sourceStorage = await StorageFile.GetFileFromPathAsync(tempPdf).AsTask(token);
            var pdfDoc = await PdfDocument.LoadFromFileAsync(sourceStorage).AsTask(token);
            if (pdfDoc.PageCount == 0) throw new InvalidDataException("Exported Excel PDF slice is empty.");

            using var pdfPage = pdfDoc.GetPage(0);
            var (width, height) = await RenderPdfPageToOutputAsync(pdfPage, edge, output, token, createDirectory: false);

            return new ExcelPageResult(1, input, pageNumber, (int)pdfDoc.PageCount, (int)width, (int)height, ExcelRenderPolicy, "page.png");
        }
        finally
        {
            try { File.Delete(tempPdf); } catch { }
        }
    }

    private static Task RunExcelExportStaAsync(string input, string tempPdf, int pageNumber, string? sheetName)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var excelType = Type.GetTypeFromProgID("Excel.Application");
                if (excelType is null) throw new NotSupportedException("Microsoft Excel is not installed.");

                string dir = Path.GetDirectoryName(tempPdf)!;
                var copy = Path.Combine(dir, "input" + Path.GetExtension(input));
                using (var sourceStream = OpenPinnedFile(input, MaxPreviewInputBytes))
                using (var copyStream = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    sourceStream.CopyTo(copyStream);

                var com = new List<object>();
                try
                {
                    var activated = Activator.CreateInstance(excelType)!;
                    com.Add(activated);
                    var app = new OfficeObject(activated, com);
                    app.Set("Visible", false);
                    try { app.Set("DisplayAlerts", false); } catch { }
                    try { app.Set("ScreenUpdating", false); } catch { }
                    try { app.Set("EnableEvents", false); } catch { }
                    try { app.Set("AutomationSecurity", 3); } catch { }

                    var workbooks = app.Object("Workbooks");
                    // Workbooks.Open(FileName, UpdateLinks: 0, ReadOnly: true)
                    var book = workbooks.Object("Open", true, copy, 0, true);

                    OfficeObject target = book;
                    if (!string.IsNullOrWhiteSpace(sheetName))
                    {
                        try
                        {
                            var sheets = book.Object("Worksheets");
                            target = sheets.Object("Item", false, sheetName);
                        }
                        catch
                        {
                            target = book;
                        }
                    }

                    int safePage = Math.Max(1, pageNumber);
                    try
                    {
                        // ExportAsFixedFormat(Type, Filename, Quality, IncludeDocProperties, IgnorePrintAreas, From, To, OpenAfterPublish)
                        // xlTypePDF = 0, xlQualityStandard = 0
                        target.Call("ExportAsFixedFormat", 0, tempPdf, 0, true, true, safePage, safePage, false);
                    }
                    catch
                    {
                        // Fall back to page 1 if requested page exceeds printed pages or sheet is empty
                        if (safePage > 1)
                        {
                            try { target.Call("ExportAsFixedFormat", 0, tempPdf, 0, true, true, 1, 1, false); }
                            catch { book.Call("ExportAsFixedFormat", 0, tempPdf, 0, true, true, 1, 1, false); }
                        }
                        else if (!ReferenceEquals(target, book))
                        {
                            book.Call("ExportAsFixedFormat", 0, tempPdf, 0, true, true, 1, 1, false);
                        }
                        else throw;
                    }

                    book.Call("Close", false);
                    try { app.Call("Quit"); } catch { }
                }
                finally
                {
                    for (int n = com.Count - 1; n >= 0; n--)
                        if (Marshal.IsComObject(com[n])) Marshal.FinalReleaseComObject(com[n]);
                    com.Clear();
                    try { File.Delete(copy); } catch { }
                }

                done.SetResult();
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
