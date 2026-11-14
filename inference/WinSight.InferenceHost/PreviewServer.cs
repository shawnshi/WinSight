using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Data.Pdf;
using Windows.Storage;

namespace WinSight.InferenceHost;

internal static class PreviewServer
{
    private static readonly JsonSerializerOptions CamelJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class PdfCacheState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal PdfDocument? Document;
        internal string? Path;
        internal long Size = -1;
        internal DateTime Mtime = DateTime.MinValue;
    }

    internal static int Run()
    {
        using var officeSta = new WarmOfficeStaWorker();
        var pdfCache = new PdfCacheState();
        var stdoutLock = new object();
        var inflight = new ConcurrentDictionary<long, Task>();
        long seq = 0;

        void Emit(object value)
        {
            var json = JsonSerializer.Serialize(value);
            lock (stdoutLock)
            {
                Console.WriteLine(json);
                Console.Out.Flush();
            }
        }

        Emit(new { ready = true, protocol = 1 });

        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement idClone = default;
            bool hasId = false;
            string op;
            string requestPath;
            try
            {
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idProp))
                {
                    idClone = idProp.Clone();
                    hasId = true;
                }
                op = root.GetProperty("op").GetString() ?? "";

                if (op == "quit")
                {
                    try { Task.WaitAll([.. inflight.Values], TimeSpan.FromSeconds(3)); } catch { }
                    officeSta.Shutdown();
                    Emit(new { id = hasId ? (object)idClone : null, ok = true });
                    return 0;
                }

                requestPath = root.GetProperty("requestPath").GetString()!;
            }
            catch (Exception ex)
            {
                Emit(new { id = hasId ? (object)idClone : null, ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
                continue;
            }

            long taskKey = Interlocked.Increment(ref seq);
            var task = Task.Run(async () =>
            {
                try
                {
                    object result = op switch
                    {
                        "render-pdf-page" => await RenderPdfWarmAsync(requestPath, pdfCache).ConfigureAwait(false),
                        "render-word-page" => await RenderWordWarmAsync(requestPath, officeSta).ConfigureAwait(false),
                        "render-excel-page" => await RenderExcelWarmAsync(requestPath, officeSta).ConfigureAwait(false),
                        "render-presentation-page" => await officeSta.RunAsync(() => RenderPresentationWarm(requestPath)).ConfigureAwait(false),
                        "render-raster-preview" => await NativeMediaCommand.RenderRasterPreviewAsync(requestPath).ConfigureAwait(false),
                        _ => throw new ArgumentException($"Unknown preview op: {op}")
                    };
                    var payload = JsonSerializer.Serialize(result, CamelJson);
                    Emit(new { id = hasId ? (object)idClone : null, ok = true, payload });
                }
                catch (Exception ex)
                {
                    Emit(new { id = hasId ? (object)idClone : null, ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
                }
                finally
                {
                    inflight.TryRemove(taskKey, out _);
                }
            });
            inflight[taskKey] = task;
        }

        try { Task.WaitAll([.. inflight.Values], TimeSpan.FromSeconds(2)); } catch { }
        officeSta.Shutdown();
        return 0;
    }

    private static async Task<NativePdfPageResult> RenderPdfWarmAsync(string requestPath, PdfCacheState cache)
    {
        var bytes = await File.ReadAllBytesAsync(requestPath).ConfigureAwait(false);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        int number = root.GetProperty("pageNumber").GetInt32();
        int edge = root.GetProperty("maxEdge").GetInt32();
        var sourcePath = NativeMediaCommand.ValidateLocalPathPublic(root.GetProperty("inputPath").GetString()!);
        var outputPath = NativeMediaCommand.ValidateLocalPathPublic(root.GetProperty("outputDirectory").GetString()!);
        if (Path.Exists(outputPath)) throw new IOException("PDF output directory must be new.");

        var fi = new FileInfo(sourcePath);
        if (!fi.Exists || fi.Length is <= 0 or > 256L * 1024 * 1024)
            throw new IOException("PDF source missing or exceeds 256 MiB.");

        await cache.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (cache.Document is null || !string.Equals(cache.Path, sourcePath, StringComparison.OrdinalIgnoreCase)
                || cache.Size != fi.Length || cache.Mtime != fi.LastWriteTimeUtc)
            {
                var storage = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask().ConfigureAwait(false);
                cache.Document = await PdfDocument.LoadFromFileAsync(storage).AsTask().ConfigureAwait(false);
                cache.Path = sourcePath;
                cache.Size = fi.Length;
                cache.Mtime = fi.LastWriteTimeUtc;
            }

            if (number < 1 || number > cache.Document.PageCount)
                throw new ArgumentOutOfRangeException(nameof(number), "PDF page is outside the source.");

            using var page = cache.Document.GetPage((uint)(number - 1));
            var (width, height) = await NativeMediaCommand.RenderPdfPageToOutputAsync(page, edge, outputPath, CancellationToken.None).ConfigureAwait(false);
            return new(1, sourcePath, number, cache.Document.PageCount, width, height, "winrt-pdf-dip-normalized-v1", "page.png");
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    private static async Task<WordPageResult> RenderWordWarmAsync(string requestPath, WarmOfficeStaWorker sta)
    {
        var bytes = await File.ReadAllBytesAsync(requestPath);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        int pageNumber = root.GetProperty("pageNumber").GetInt32();
        int edge = root.GetProperty("maxEdge").GetInt32();
        int? paragraphNumber = root.TryGetProperty("paragraphNumber", out var paraProp) && paraProp.ValueKind == JsonValueKind.Number
            ? paraProp.GetInt32()
            : null;
        var input = NativeMediaCommand.ValidateLocalPathPublic(root.GetProperty("inputPath").GetString()!);
        var output = NativeMediaCommand.ValidateLocalPathPublic(root.GetProperty("outputDirectory").GetString()!);
        if (Path.Exists(output)) throw new IOException("Word output directory must be new.");
        Directory.CreateDirectory(output);

        string tempPdf = Path.Combine(output, "slice.pdf");
        var (resolvedPage, totalPages) = await sta.RunAsync(() => sta.ExportWordSlice(input, tempPdf, pageNumber, paragraphNumber));

        if (!File.Exists(tempPdf)) throw new FileNotFoundException("Word export failed to produce single-page PDF slice.");
        try
        {
            var storage = await StorageFile.GetFileFromPathAsync(tempPdf);
            var pdfDoc = await PdfDocument.LoadFromFileAsync(storage);
            if (pdfDoc.PageCount == 0) throw new InvalidDataException("Exported Word PDF slice is empty.");
            using var pdfPage = pdfDoc.GetPage(0);
            var (width, height) = await NativeMediaCommand.RenderPdfPageToOutputAsync(pdfPage, edge, output, CancellationToken.None, createDirectory: false);
            return new WordPageResult(1, input, resolvedPage, Math.Max(totalPages, (int)pdfDoc.PageCount), (int)width, (int)height, "word-export-fixed-v1", "page.png");
        }
        finally
        {
            try { File.Delete(tempPdf); } catch { }
        }
    }

    private static async Task<ExcelPageResult> RenderExcelWarmAsync(string requestPath, WarmOfficeStaWorker sta)
    {
        var bytes = await File.ReadAllBytesAsync(requestPath);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        int pageNumber = root.GetProperty("pageNumber").GetInt32();
        int edge = root.GetProperty("maxEdge").GetInt32();
        string? sheetName = root.TryGetProperty("sheetName", out var sheetProp) && sheetProp.ValueKind == JsonValueKind.String
            ? sheetProp.GetString()
            : null;
        var input = NativeMediaCommand.ValidateLocalPathPublic(root.GetProperty("inputPath").GetString()!);
        var output = NativeMediaCommand.ValidateLocalPathPublic(root.GetProperty("outputDirectory").GetString()!);
        if (Path.Exists(output)) throw new IOException("Excel output directory must be new.");
        Directory.CreateDirectory(output);

        string tempPdf = Path.Combine(output, "slice.pdf");
        await sta.RunAsync(() => sta.ExportExcelSlice(input, tempPdf, pageNumber, sheetName));

        if (!File.Exists(tempPdf)) throw new FileNotFoundException("Excel export failed to produce single-page PDF slice.");
        try
        {
            var storage = await StorageFile.GetFileFromPathAsync(tempPdf);
            var pdfDoc = await PdfDocument.LoadFromFileAsync(storage);
            if (pdfDoc.PageCount == 0) throw new InvalidDataException("Exported Excel PDF slice is empty.");
            using var pdfPage = pdfDoc.GetPage(0);
            var (width, height) = await NativeMediaCommand.RenderPdfPageToOutputAsync(pdfPage, edge, output, CancellationToken.None, createDirectory: false);
            return new ExcelPageResult(1, input, pageNumber, (int)pdfDoc.PageCount, (int)width, (int)height, "excel-export-fixed-v1", "page.png");
        }
        finally
        {
            try { File.Delete(tempPdf); } catch { }
        }
    }

    private static object RenderPresentationWarm(string requestPath)
    {
        // Delegate to WarmOfficeStaWorker directly inside STA
        return WarmOfficeStaWorker.Current!.RenderPresentationSlide(requestPath);
    }

    private sealed class WarmOfficeStaWorker : IDisposable
    {
        [ThreadStatic]
        internal static WarmOfficeStaWorker? Current;

        private readonly BlockingCollection<(Action Run, TaskCompletionSource<object?> Done)> queue = new();
        private readonly Thread staThread;
        private readonly Timer idleTimer;
        private object? wordApp;
        private object? excelApp;
        private object? pptApp;
        private bool pptPublisherVerified;
        private string? pptExecutable;
        private DateTime lastActivity = DateTime.UtcNow;

        internal WarmOfficeStaWorker()
        {
            staThread = new Thread(RunLoop) { IsBackground = true };
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            idleTimer = new Timer(_ => CheckIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private void CheckIdle()
        {
            if ((DateTime.UtcNow - lastActivity) < TimeSpan.FromSeconds(120)) return;
            if (wordApp is null && excelApp is null && pptApp is null) return;
            try
            {
                RunAsync(() =>
                {
                    if ((DateTime.UtcNow - lastActivity) >= TimeSpan.FromSeconds(120))
                        ReleaseAllApps();
                }).GetAwaiter().GetResult();
            }
            catch { }
        }

        private void RunLoop()
        {
            Current = this;
            foreach (var item in queue.GetConsumingEnumerable())
            {
                lastActivity = DateTime.UtcNow;
                try
                {
                    item.Run();
                    item.Done.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    item.Done.TrySetException(ex);
                }
            }
            ReleaseAllApps();
        }

        internal Task RunAsync(Action action)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add((action, tcs));
            return tcs.Task;
        }

        internal Task<T> RunAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add((() =>
            {
                var res = func();
                tcs.TrySetResult(res);
            }, tcs));
            return tcs.Task.ContinueWith(t =>
            {
                if (t.IsFaulted) throw t.Exception!.InnerException!;
                return (T)t.Result!;
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        internal (int ResolvedPage, int TotalPages) ExportWordSlice(string input, string tempPdf, int pageNumber, int? paragraphNumber)
        {
            string dir = Path.GetDirectoryName(tempPdf)!;
            var copy = Path.Combine(dir, "input" + Path.GetExtension(input));
            using (var sourceStream = NativeMediaCommand.OpenPinnedFilePublic(input, 256L * 1024 * 1024))
            using (var copyStream = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                sourceStream.CopyTo(copyStream);

            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var appObj = EnsureWord();
                        var com = new List<object>();
                        try
                        {
                            var app = new NativeMediaCommand.OfficeObject(appObj, com);
                            var docs = app.Object("Documents");
                            var doc = docs.Object("Open", true, copy, false, true, false, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, false);
                            int resolvedPage = Math.Max(1, pageNumber);
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
                                        int p = Convert.ToInt32(range.Get<object>("Information", 3));
                                        if (p >= 1) resolvedPage = p;
                                    }
                                }
                                catch { }
                            }
                            int totalPages;
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
                            try { doc.Call("Close", false); } catch { }
                            return (resolvedPage, totalPages);
                        }
                        finally
                        {
                            for (int n = com.Count - 1; n >= 0; n--)
                                if (!ReferenceEquals(com[n], wordApp) && Marshal.IsComObject(com[n]))
                                    Marshal.FinalReleaseComObject(com[n]);
                        }
                    }
                    catch (COMException) when (attempt == 0)
                    {
                        ReleaseCom(ref wordApp, quit: false);
                    }
                }
                throw new IOException("Word automation failed.");
            }
            finally
            {
                try { File.Delete(copy); } catch { }
            }
        }

        internal void ExportExcelSlice(string input, string tempPdf, int pageNumber, string? sheetName)
        {
            string dir = Path.GetDirectoryName(tempPdf)!;
            var copy = Path.Combine(dir, "input" + Path.GetExtension(input));
            using (var sourceStream = NativeMediaCommand.OpenPinnedFilePublic(input, 256L * 1024 * 1024))
            using (var copyStream = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                sourceStream.CopyTo(copyStream);

            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var appObj = EnsureExcel();
                        var com = new List<object>();
                        try
                        {
                            var app = new NativeMediaCommand.OfficeObject(appObj, com);
                            var workbooks = app.Object("Workbooks");
                            var book = workbooks.Object("Open", true, copy, 0, true);
                            var target = book;
                            if (!string.IsNullOrWhiteSpace(sheetName))
                            {
                                try
                                {
                                    var sheets = book.Object("Worksheets");
                                    target = sheets.Object("Item", false, sheetName);
                                }
                                catch { target = book; }
                            }
                            int safePage = Math.Max(1, pageNumber);
                            try
                            {
                                target.Call("ExportAsFixedFormat", 0, tempPdf, 0, true, true, safePage, safePage, false);
                            }
                            catch
                            {
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
                            try { book.Call("Close", false); } catch { }
                            return;
                        }
                        finally
                        {
                            for (int n = com.Count - 1; n >= 0; n--)
                                if (!ReferenceEquals(com[n], excelApp) && Marshal.IsComObject(com[n]))
                                    Marshal.FinalReleaseComObject(com[n]);
                        }
                    }
                    catch (COMException) when (attempt == 0)
                    {
                        ReleaseCom(ref excelApp, quit: false);
                    }
                }
                throw new IOException("Excel automation failed.");
            }
            finally
            {
                try { File.Delete(copy); } catch { }
            }
        }

        internal object RenderPresentationSlide(string requestPath)
        {
            var bytes = File.ReadAllBytes(requestPath);
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            int first = root.GetProperty("pageNumber").GetInt32();
            int edge = root.GetProperty("maxEdge").GetInt32();
            var input = NativeMediaCommand.ValidateLocalPathPublic(root.GetProperty("inputPath").GetString()!);
            var output = NativeMediaCommand.ValidateLocalPathPublic(root.GetProperty("outputDirectory").GetString()!);
            if (!Path.GetExtension(input).Equals(".pptx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only PPTX is rendered.");

            using var source = NativeMediaCommand.OpenPinnedFilePublic(input, 256L * 1024 * 1024);
            string version = WinSight.Infrastructure.Media.PowerPointPolicy.Identity();
            object Deferred(string reason) => new { protocol = 1, inputPath = input, state = "deferred", reason, officeVersion = version, slideCount = 0, renderPolicy = "powerpoint-slide-export-png-v1", slides = Array.Empty<object>() };

            var executable = pptExecutable ??= WinSight.Infrastructure.Media.PowerPointPolicy.InstalledPath();
            if (executable is null || !File.Exists(executable)) return Deferred("powerpoint-missing");
            try { WinSight.Infrastructure.Media.PowerPointPolicy.ValidateInteractivePackage(source, CancellationToken.None); }
            catch (NotSupportedException) { return Deferred("unsupported-content"); }

            if (!pptPublisherVerified)
            {
                WinSight.Infrastructure.Media.PowerPointPolicy.VerifyPublisher(executable);
                pptPublisherVerified = true;
            }

            Directory.CreateDirectory(output);
            var copy = Path.Combine(output, "input.pptx");
            source.Position = 0;
            using (var writer = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(writer);

            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var appObj = EnsurePowerPoint();
                        var com = new List<object>();
                        try
                        {
                            var app = new NativeMediaCommand.OfficeObject(appObj, com);
                            var decks = app.Object("Presentations");
                            int security = app.Get<int>("AutomationSecurity");
                            NativeMediaCommand.OfficeObject deck;
                            try
                            {
                                app.Set("AutomationSecurity", 3);
                                deck = decks.Object("Open", true, copy, -1, -1, 0);
                            }
                            finally
                            {
                                try { app.Set("AutomationSecurity", security); } catch { }
                            }
                            try
                            {
                                var slides = deck.Object("Slides");
                                int count = slides.Get<int>("Count");
                                if (count is < 1 or > 5000) throw new InvalidDataException("Unsupported presentation slide count.");
                                int targetSlide = Math.Clamp(first, 1, count);
                                var setup = deck.Object("PageSetup");
                                double w = Convert.ToDouble(setup.Get<object>("SlideWidth"));
                                double h = Convert.ToDouble(setup.Get<object>("SlideHeight"));
                                double scale = edge / Math.Max(w, h);
                                int width = Math.Max(1, (int)Math.Round(w * scale));
                                int height = Math.Max(1, (int)Math.Round(h * scale));
                                var slide = slides.Object("Item", true, targetSlide);
                                var transition = slide.Object("SlideShowTransition");
                                bool hidden = transition.Get<int>("Hidden") == -1;
                                string path = Path.Combine(output, "page.png");
                                var raw = Path.Combine(output, "raw.png");
                                slide.Call("Export", raw, "PNG", width, height);
                                var ihdr = new byte[24];
                                using (var rawPin = NativeMediaCommand.OpenPinnedFilePublic(raw, 32L * 1024 * 1024))
                                {
                                    if (rawPin.Read(ihdr) < 24 || BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(16, 4)) != width || BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(20, 4)) != height)
                                    {
                                        rawPin.Dispose();
                                        NativeMediaCommand.CanonicalSlidePublic(raw, path, width, height);
                                        File.Delete(raw);
                                    }
                                    else
                                    {
                                        rawPin.Dispose();
                                        File.Move(raw, path);
                                    }
                                }
                                return new
                                {
                                    protocol = 1,
                                    inputPath = input,
                                    state = "done",
                                    reason = "",
                                    officeVersion = version,
                                    slideCount = count,
                                    renderPolicy = "powerpoint-slide-export-png-v1",
                                    slides = new[] { new { number = targetSlide, width, height, hidden, artifact = "page.png" } }
                                };
                            }
                            finally
                            {
                                try { deck.Call("Close"); } catch { }
                            }
                        }
                        finally
                        {
                            for (int n = com.Count - 1; n >= 0; n--)
                                if (!ReferenceEquals(com[n], pptApp) && Marshal.IsComObject(com[n]))
                                    Marshal.FinalReleaseComObject(com[n]);
                        }
                    }
                    catch (COMException) when (attempt == 0)
                    {
                        ReleaseCom(ref pptApp, quit: false);
                    }
                }
                throw new IOException("PowerPoint automation failed.");
            }
            finally
            {
                try { File.Delete(copy); } catch { }
            }
        }

        private object EnsureWord()
        {
            if (wordApp is not null) return wordApp;
            wordApp = CreateTrackedOfficeApp("Word.Application", "WINWORD", "Microsoft Word is not installed.");
            var app = new NativeMediaCommand.OfficeObject(wordApp, new List<object>());
            app.Set("Visible", false);
            try { app.Set("DisplayAlerts", 0); } catch { }
            try { app.Set("AutomationSecurity", 3); } catch { }
            return wordApp;
        }

        private object EnsureExcel()
        {
            if (excelApp is not null) return excelApp;
            excelApp = CreateTrackedOfficeApp("Excel.Application", "EXCEL", "Microsoft Excel is not installed.");
            var app = new NativeMediaCommand.OfficeObject(excelApp, new List<object>());
            app.Set("Visible", false);
            try { app.Set("DisplayAlerts", false); } catch { }
            try { app.Set("ScreenUpdating", false); } catch { }
            try { app.Set("EnableEvents", false); } catch { }
            try { app.Set("AutomationSecurity", 3); } catch { }
            return excelApp;
        }

        private object EnsurePowerPoint()
        {
            if (pptApp is not null) return pptApp;
            pptApp = CreateTrackedOfficeApp("PowerPoint.Application", "POWERPNT", "Microsoft PowerPoint is not installed.");
            return pptApp;
        }

        private IntPtr officeJobHandle;

        private object CreateTrackedOfficeApp(string progId, string procName, string missingMessage)
        {
            var type = Type.GetTypeFromProgID(progId) ?? throw new NotSupportedException(missingMessage);
            var beforePids = Process.GetProcessesByName(procName).Select(p => { int id = p.Id; p.Dispose(); return id; }).ToHashSet();
            var instance = Activator.CreateInstance(type)!;
            try
            {
                EnsureOfficeJob();
                if (officeJobHandle != IntPtr.Zero)
                {
                    foreach (var p in Process.GetProcessesByName(procName))
                    {
                        using (p)
                        {
                            if (!beforePids.Contains(p.Id))
                                AssignProcessToJobObject(officeJobHandle, p.Handle);
                        }
                    }
                }
            }
            catch { }
            return instance;
        }

        private void EnsureOfficeJob()
        {
            if (officeJobHandle != IntPtr.Zero) return;
            officeJobHandle = CreateJobObject(IntPtr.Zero, null);
            if (officeJobHandle == IntPtr.Zero) return;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(officeJobHandle, 9, ptr, (uint)size);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryLimit, PeakJobMemoryLimit;
        }

        private void ReleaseAllApps()
        {
            ReleaseCom(ref wordApp, quit: true, saveFalseArg: true);
            ReleaseCom(ref excelApp, quit: true, saveFalseArg: false);
            ReleaseCom(ref pptApp, quit: true, saveFalseArg: false);
        }

        private static void ReleaseCom(ref object? comObj, bool quit, bool saveFalseArg = false)
        {
            var obj = comObj;
            comObj = null;
            if (obj is null) return;
            try
            {
                if (quit)
                {
                    var app = new NativeMediaCommand.OfficeObject(obj, new List<object>());
                    if (saveFalseArg) app.Call("Quit", false);
                    else app.Call("Quit");
                }
            }
            catch { }
            try
            {
                if (Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
            }
            catch { }
        }

        internal void Shutdown()
        {
            idleTimer.Dispose();
            queue.CompleteAdding();
            staThread.Join(3000);
            if (officeJobHandle != IntPtr.Zero)
            {
                CloseHandle(officeJobHandle);
                officeJobHandle = IntPtr.Zero;
            }
        }

        public void Dispose() => Shutdown();
    }
}
