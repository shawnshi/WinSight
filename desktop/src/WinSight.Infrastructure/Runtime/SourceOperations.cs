using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Media;

namespace WinSight.Infrastructure.Runtime;

public sealed partial class DesktopBackend
{
    private readonly SemaphoreSlim sourceOperationsGate=new(1,1);
    private readonly object previewServerLock = new();
    private CancellationTokenSource sourceOperationsStop=new();
    private readonly ConcurrentDictionary<Guid,PreviewSnapshot> sourcePreviews=new();
    private PreviewServerClient? previewServer;
    private string? previewServerHost;
    private int staleMediaSwept;

    private PreviewServerClient GetPreviewServer(string hostExe, string workspace)
    {
        if (Interlocked.Exchange(ref staleMediaSwept, 1) == 0)
        {
            _ = Task.Run(() => SweepStaleMediaDirectories(dataDirectory));
        }
        lock (previewServerLock)
        {
            if (previewServer is not null && string.Equals(previewServerHost, hostExe, StringComparison.OrdinalIgnoreCase))
                return previewServer;
            if (previewServer is not null)
            {
                var old = previewServer;
                previewServer = null;
                _ = old.DisposeAsync();
            }
            previewServerHost = hostExe;
            previewServer = new PreviewServerClient(hostExe, workspace);
            return previewServer;
        }
    }

    private static void SweepStaleMediaDirectories(string dataDir)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var sub in new[] { "source-preview", "media-tasks" })
            {
                var root = Path.Combine(dataDir, sub);
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root, "media-*"))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                            Directory.Delete(dir, recursive: true);
                    }
                    catch { /* locked by active/unsettled process: skip */ }
                }
            }
        }
        catch { }
    }

    private static void PruneDiskCache(string cacheDir)
    {
        try
        {
            var files = new DirectoryInfo(cacheDir).GetFiles("*.png");
            const int maxFiles = 200;
            const long maxBytes = 150L * 1024 * 1024;
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (files.Length <= maxFiles && total <= maxBytes) return;
            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            int remaining = files.Length;
            foreach (var f in files)
            {
                if (remaining <= maxFiles && total <= maxBytes) break;
                try
                {
                    long len = f.Length;
                    f.Delete();
                    remaining--;
                    total -= len;
                }
                catch { }
            }
        }
        catch { }
    }

    private static string HashCacheKey(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static bool TryReadPngDimensions(string path, out int width, out int height)
    {
        width = 0; height = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> buf = stackalloc byte[24];
            if (fs.Read(buf) < 24) return false;
            if (buf[0] != 137 || buf[1] != 80 || buf[2] != 78 || buf[3] != 71) return false;
            width = BinaryPrimitives.ReadInt32BigEndian(buf[16..20]);
            height = BinaryPrimitives.ReadInt32BigEndian(buf[20..24]);
            return width > 0 && height > 0;
        }
        catch { return false; }
    }

    private async Task<(IndexedFile File,SourceAnchor? Anchor,string Text,AppSettings Settings)> SourceSnapshotAsync(SearchHit hit,bool code,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(hit);MediaTaskFiles.LocalPath(hit.Path);
        await lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed||stopping!=0,this);
            var fresh=await settingsStore.LoadAsync(token);
            string freshIndex=fresh.IndexDirectory??Path.Combine(dataDirectory,"index");
            if(!PathsEqual(freshIndex,IndexDirectory))throw new InvalidOperationException("Index configuration changed; reload settings before preview.");
            bool allowed=!IsUnder(hit.Path,dataDirectory)&&!IsUnder(hit.Path,freshIndex)
                &&(code?fresh.CodeFolders:fresh.Folders).Any(root=>IsUnder(hit.Path,root)&&!(code?fresh.CodeSkipped:fresh.Skipped).Any(skip=>IsUnder(skip,root)&&!PathsEqual(skip,root)&&IsUnder(hit.Path,skip)));
            string extension=FilePolicy.Extension(hit.Path);
            if(!allowed||!code&&fresh.SkipTypes.Any(skip=>skip.TrimStart('.').Equals(extension,StringComparison.OrdinalIgnoreCase)||skip.Equals(hit.Kind,StringComparison.OrdinalIgnoreCase)))
                throw new UnauthorizedAccessException("Source is outside the current authorized selection or excluded.");
            var repository=(await GetRepositoriesAsync(token))[code?1:0];
            var file=await repository.GetFileAsync(hit.Path,token)??throw new InvalidOperationException("Source result is no longer indexed.");
            if(file.Id!=hit.FileId)throw new InvalidOperationException("Source result is stale; search again.");
            SourceAnchor? anchor=null;IReadOnlyList<IndexSegment> units=[];
            if(hit.Source is not null)
            {
                var match=file.Segments.FirstOrDefault(s=>s.Source?.Location==hit.Source.Location&&s.Source.Channel==hit.Source.Channel);
                if(match?.Source is null)throw new InvalidOperationException("Source location is no longer present in the index.");
                units=file.Segments.Where(s=>s.Source?.Location==match.Source.Location).ToArray();
                var regions=units.SelectMany(s=>s.Source?.Regions??[]).Distinct().OrderBy(r=>r.Line).Take(96).ToArray();
                anchor=match.Source with{Regions=regions};
            }
            else units=file.Segments.Where(s=>s.Kind!="name").Take(3).ToArray();
            string text=string.Join("\n\n",units.Where(s=>!string.IsNullOrEmpty(s.Excerpt)).Select(s=>s.Excerpt));
            if(text.Length>12000)text=ContentExtractor.Prefix(text,12000);
            return(file,anchor,text,fresh);
        }
        finally{lifecycle.Release();}
    }
    private sealed record CachedRasterEntry(string RasterPath, int PixelWidth, int PixelHeight, string Notice, string SourceText, DateTime CachedAt, int PageNumber = 1, int? TotalPages = null);
    private static readonly ConcurrentDictionary<string, CachedRasterEntry> RasterLruCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, (long Size, double Mtime, bool Current)> VerifiedSourceFingerprints = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<bool> CurrentSourceAsync(IndexedFile file,SafeFile pin,CancellationToken token)
    {
        if(file.Info is null)return false;
        if(pin.Stream!.Length != file.Size) return false;
        if(VerifiedSourceFingerprints.TryGetValue(file.Path, out var cached) && cached.Size == file.Size && Math.Abs(cached.Mtime - file.ModifiedTime) < 0.001)
            return cached.Current;

        using var info=JsonDocument.Parse(file.Info);
        if(!info.RootElement.TryGetProperty("sourceSha256V1",out var value)||value.ValueKind!=JsonValueKind.String||value.GetString() is not {Length:64} expected||!expected.All(Uri.IsHexDigit))return false;
        pin.Stream.Position=0;var actual=await SHA256.HashDataAsync(pin.Stream,token);pin.Stream.Position=0;
        bool matched = CryptographicOperations.FixedTimeEquals(actual,Convert.FromHexString(expected));
        VerifiedSourceFingerprints[file.Path] = (file.Size, file.ModifiedTime, matched);
        return matched;
    }
    public Task<ISourcePreview> PreviewSourceAsync(SearchHit hit,bool code=false,CancellationToken cancellationToken=default) =>
        PreviewSourceCoreAsync(hit, null, code, cancellationToken);

    public Task<ISourcePreview> PreviewSourcePageAsync(SearchHit hit, int pageNumber, bool code = false, CancellationToken cancellationToken = default) =>
        PreviewSourceCoreAsync(hit, Math.Max(1, pageNumber), code, cancellationToken);

    private async Task<ISourcePreview> PreviewSourceCoreAsync(SearchHit hit, int? overridePage, bool code, CancellationToken cancellationToken)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetimeStop.Token,sourceOperationsStop.Token);var token=linked.Token;
        var source=await SourceSnapshotAsync(hit,code,token);
        SafeFile? pin=null;IAsyncDisposable? artifact=null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(source.File.Path))
            {
                var missingSnapshot = new PreviewSnapshot(source.Text, source.Anchor is null ? null : source.Anchor with { Regions = [] }, null, 0, 0,
                    "源文件已被移动、重命名或删除，等待索引刷新。", false, false, null, null, sourcePreviews, overridePage ?? 1, null);
                sourcePreviews[missingSnapshot.Id] = missingSnapshot;
                return missingSnapshot;
            }
            pin=SafeFile.Open(source.File.Path);
            bool current=await CurrentSourceAsync(source.File,pin,token);string? raster=null;int width=0,height=0;string notice="已就绪，索引文本预览。";
            string sourceText = source.Text;
            int resolvedPageNumber = overridePage ?? 1;
            int? totalPages = null;
            if(!current)notice="源文件指纹已变更或不匹配。光栅视图与精确导航已停用；请重新索引该文件。";
            else
            {
                string workspace=Path.Combine(dataDirectory,"source-preview");Directory.CreateDirectory(workspace);
                string hostExe = source.Settings.InferenceExecutable ?? ResolveInferenceExecutable();
                var client = GetPreviewServer(hostExe, workspace);
                var location=source.Anchor?.Location;var extension=FilePolicy.Extension(source.File.Path);
                int targetPage = overridePage ?? location?.Page ?? (extension is "docx" or "doc" ? 1 : (hit.Location.HasValue ? (int)Math.Round(hit.Location.Value) : 1));
                if (targetPage < 1) targetPage = 1;
                resolvedPageNumber = targetPage;
                int? targetParagraph = overridePage.HasValue
                    ? null
                    : (extension is "docx" or "doc" ? (location?.Paragraph ?? (hit.Location.HasValue ? Math.Max(1, (int)Math.Round(hit.Location.Value)) : null)) : null);
                string? targetSheet = extension is "xlsx" or "xls" ? location?.Sheet : null;
                string cacheKey = $"{source.File.Path}|{source.File.Size}|{source.File.ModifiedTime:F3}|{targetPage}|{targetParagraph?.ToString() ?? ""}|{targetSheet ?? ""}";
                string cacheDir = Path.Combine(workspace, "cache");
                string persistentRaster = Path.Combine(cacheDir, $"{HashCacheKey(cacheKey)}_{targetPage}.png");
                bool isFallbackThumbnail = false;
                if (RasterLruCache.TryGetValue(cacheKey, out var cachedRaster) && File.Exists(cachedRaster.RasterPath))
                {
                    raster = cachedRaster.RasterPath;
                    width = cachedRaster.PixelWidth;
                    height = cachedRaster.PixelHeight;
                    notice = cachedRaster.Notice;
                    sourceText = cachedRaster.SourceText;
                    resolvedPageNumber = cachedRaster.PageNumber;
                    totalPages = cachedRaster.TotalPages;
                }
                else if (File.Exists(persistentRaster) && TryReadPngDimensions(persistentRaster, out int diskW, out int diskH))
                {
                    raster = persistentRaster;
                    width = diskW;
                    height = diskH;
                    notice = $"已从本地缓存瞬时载入第 {targetPage} 页高清预览。";
                    try { File.SetLastWriteTimeUtc(persistentRaster, DateTime.UtcNow); } catch { }
                    RasterLruCache[cacheKey] = new(persistentRaster, diskW, diskH, notice, sourceText, DateTime.UtcNow, resolvedPageNumber, totalPages);
                }
                else
                {
                    try
                    {
                        if(extension=="pdf")
                        {
                            var page=await client.RenderPdfPageAsync(new(source.File.Path,targetPage,2400),token);artifact=page;raster=page.ArtifactPath;width=page.PixelWidth;height=page.PixelHeight;resolvedPageNumber=page.PageNumber;totalPages=page.PageCount;notice=$"当前 PDF 第 {page.PageNumber} / {page.PageCount} 页画面；文字框标记为 OCR 提取位置。";
                        }
                        else if(extension=="pptx")
                        {
                            try
                            {
                                var page=await client.RenderPresentationPageAsync(new(source.File.Path,targetPage),token);artifact=page;
                                if(page.State=="deferred")notice=$"演示文稿第 {targetPage} 页渲染已暂缓（{page.Reason}）；当前未附加 PowerPoint 自动化实例。";
                                else if(page.Slides.SingleOrDefault() is {} slide){raster=slide.ArtifactPath;width=slide.PixelWidth;height=slide.PixelHeight;resolvedPageNumber=slide.Number;totalPages=page.SlideCount;notice=$"当前演示文稿第 {slide.Number} / {page.SlideCount} 页实时渲染画面。";}
                                else notice="未成功渲染所请求的幻灯片页面。";
                            }
                            catch(Exception ex)when(ex is not OperationCanceledException)
                            {
                                notice=$"演示文稿幻灯片渲染不可用 ({ex.GetType().Name})。";
                            }
                            if(raster is null && await TryExtractOoxmlThumbnailAsync(source.File.Path,workspace,client,token) is {} thumb)
                            {
                                isFallbackThumbnail = true;
                                artifact=thumb;raster=thumb.ArtifactPath;width=thumb.PixelWidth;height=thumb.PixelHeight;
                                notice=$"已呈现演示文稿内置缩略图（因文件结构受限或 PowerPoint 未就绪，第 {targetPage} 页实时渲染已暂缓）。";
                            }
                        }
                        else if(extension is "docx" or "doc")
                        {
                            try
                            {
                                var wordPage = await client.RenderWordPageAsync(new(source.File.Path, targetPage, 2400, targetParagraph), token);
                                artifact = wordPage; raster = wordPage.ArtifactPath; width = wordPage.PixelWidth; height = wordPage.PixelHeight;
                                totalPages = wordPage.PageCount > 1 ? wordPage.PageCount : null;
                                notice = $"当前 Word 文档排版画面。";
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                if (await TryExtractOoxmlThumbnailAsync(source.File.Path, workspace, client, token) is { } thumb)
                                {
                                    isFallbackThumbnail = true;
                                    artifact = thumb; raster = thumb.ArtifactPath; width = thumb.PixelWidth; height = thumb.PixelHeight;
                                    notice = $"已呈现内置 {extension.ToUpperInvariant()} 文档缩略图。";
                                }
                                else notice = $"已定位 {extension.ToUpperInvariant()} 文档内容。可点击“定位到此位置”以只读方式打开。";
                            }
                        }
                        else if(extension is "xlsx" or "xls")
                        {
                            try
                            {
                                var excelPage = await client.RenderExcelPageAsync(new(source.File.Path, targetPage, 2400, targetSheet), token);
                                artifact = excelPage; raster = excelPage.ArtifactPath; width = excelPage.PixelWidth; height = excelPage.PixelHeight;
                                totalPages = excelPage.PageCount > 1 ? excelPage.PageCount : null;
                                notice = string.IsNullOrWhiteSpace(targetSheet)
                                    ? $"当前 Excel 工作表第 {targetPage} 页排版画面。"
                                    : $"当前 Excel 工作表「{targetSheet}」排版画面。";
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                if (await TryExtractOoxmlThumbnailAsync(source.File.Path, workspace, client, token) is { } thumb)
                                {
                                    isFallbackThumbnail = true;
                                    artifact = thumb; raster = thumb.ArtifactPath; width = thumb.PixelWidth; height = thumb.PixelHeight;
                                    notice = $"已呈现内置 {extension.ToUpperInvariant()} 文档缩略图。";
                                }
                                else notice = $"已定位 {extension.ToUpperInvariant()} 工作表内容。可点击“定位到此位置”以只读方式打开。";
                            }
                        }
                        else if(source.File.Kind is "image" or "screenshot")
                        {
                            var image=await client.RenderRasterPreviewAsync(new(source.File.Path),token);artifact=image;raster=image.ArtifactPath;width=image.PixelWidth;height=image.PixelHeight;notice="规范化当前图片；选框与索引 OCR 行坐标一致。";
                        }
                        else
                        {
                            // 针对纯文本、Markdown、代码文件等，直接从磁盘读取前部文本以呈现完整预览（达到 Peek 效果）
                            if (pin.Stream is not null && pin.Stream.CanRead)
                            {
                                try
                                {
                                    pin.Stream.Position = 0;
                                    using var textReader = new StreamReader(pin.Stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                                    var textBuf = new char[24576];
                                    int readChars = await textReader.ReadAsync(textBuf, token);
                                    pin.Stream.Position = 0;
                                    if (readChars > 0)
                                    {
                                        sourceText = new string(textBuf, 0, readChars);
                                        notice = $"文本 / Markdown 即时预览（已载入前 {readChars} 字符）；支持鼠标选中与复制。";
                                    }
                                }
                                catch { /* fallback to indexed excerpt */ }
                            }
                            if (string.IsNullOrEmpty(notice) || notice == "已就绪，索引文本预览。")
                                notice = "已定位索引上下文片段。点击“打开文件”查看完整内容。";
                        }
                    }
                    catch(OperationCanceledException){throw;}
                    catch(Exception e)when(e is IOException or InvalidDataException or NotSupportedException or ArgumentException)
                    {notice=$"光栅视图暂时不可用 ({e.GetType().Name})；已呈现正文内容。";}

                    // 缓存渲染成功的单页图片到持久缓存目录，使后续秒开无需重复调度 Office（绝不把降级缩略图误缓存为指定页）
                    if (!isFallbackThumbnail && !string.IsNullOrEmpty(raster) && File.Exists(raster))
                    {
                        try
                        {
                            Directory.CreateDirectory(cacheDir);
                            File.Copy(raster, persistentRaster, overwrite: true);
                            if (RasterLruCache.Count > 64)
                            {
                                var oldest = RasterLruCache.OrderBy(kv => kv.Value.CachedAt).FirstOrDefault();
                                if (oldest.Key is not null) RasterLruCache.TryRemove(oldest.Key, out _);
                            }
                            RasterLruCache[cacheKey] = new(persistentRaster, width, height, notice, sourceText, DateTime.UtcNow, resolvedPageNumber, totalPages);
                            PruneDiskCache(cacheDir);
                        }
                        catch { /* non-fatal cache persistence */ }
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            bool navigate=current&&source.Anchor?.Location is {} target&&NativeOfficeNavigationRequest.SupportsLocation(source.File.Path,target);
            if(navigate&&source.Anchor?.Location.Type=="slide")notice+=" 定位打开需要 PowerPoint 处于空闲状态。";
            var snapshot=new PreviewSnapshot(sourceText,current?source.Anchor:source.Anchor is null?null:source.Anchor with{Regions=[]},raster,width,height,notice,current,navigate,pin,artifact,sourcePreviews,resolvedPageNumber,totalPages);
            sourcePreviews[snapshot.Id]=snapshot;pin=null;artifact=null;return snapshot;
        }
        finally{try{if(artifact is not null)await artifact.DisposeAsync();}finally{pin?.Dispose();}}
    }
    public async Task<SourceNavigationResult> NavigateSourceAsync(SearchHit hit,bool code=false,CancellationToken cancellationToken=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetimeStop.Token,sourceOperationsStop.Token);var token=linked.Token;
        var snapshot=await SourceSnapshotAsync(hit,code,token);
        var location=snapshot.Anchor?.Location??throw new NotSupportedException("No exact indexed location exists for this result.");
        await sourceOperationsGate.WaitAsync(token);
        try
        {
            using var pin=SafeFile.Open(snapshot.File.Path);
            if(!await CurrentSourceAsync(snapshot.File,pin,token))throw new InvalidOperationException("Source changed or has no fingerprint; re-index before precise open.");
            using var metadata=JsonDocument.Parse(snapshot.File.Info!);string hash=metadata.RootElement.GetProperty("sourceSha256V1").GetString()!;
            var request=new NativeOfficeNavigationRequest(snapshot.File.Path,hash,location);request.Validate();
            string workspace=Path.Combine(dataDirectory,"source-navigation");Directory.CreateDirectory(workspace);
            await using var client=new NativeMediaProcessClient(new(){HostExecutable=snapshot.Settings.InferenceExecutable??Path.Combine(applicationDirectory,"inference","WinSight.InferenceHost.exe"),WorkspaceRoot=workspace});
            var result=await client.NavigateOfficeAsync(request,token);
            return new(true,"Opened a new read-only Office view at "+result.Target+". The window now belongs to you."+(result.CleanupWarning is null?"":" "+result.CleanupWarning),result.ProcessId);
        }
        finally{sourceOperationsGate.Release();}
    }

    private static async Task<NativeRasterPreviewLease?> TryExtractOoxmlThumbnailAsync(
        string filePath, string workspace, PreviewServerClient client, CancellationToken token)
    {
        try
        {
            string? extracted = null;
            using (var zip = ZipFile.OpenRead(filePath))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName, "docProps/thumbnail.jpeg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.FullName, "docProps/thumbnail.jpg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.FullName, "docProps/thumbnail.png", StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.Length <= 0 || entry.Length > 16L * 1024 * 1024) return null;
                var ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
                extracted = Path.Combine(workspace, $"ooxml-thumb-{Guid.NewGuid():N}{ext}");
                entry.ExtractToFile(extracted, overwrite: true);
            }
            if (extracted is not null && File.Exists(extracted))
            {
                try
                {
                    return await client.RenderRasterPreviewAsync(new(extracted), token);
                }
                finally
                {
                    try { File.Delete(extracted); } catch { }
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task StopSourceOperationsAsync()
    {
        sourceOperationsStop.Cancel();await sourceOperationsGate.WaitAsync();
        try
        {
            foreach(var preview in sourcePreviews.Values) await preview.DisposeAsync();
            if (previewServer is not null)
            {
                await previewServer.DisposeAsync();
                previewServer = null;
                previewServerHost = null;
            }
            sourceOperationsStop.Dispose();sourceOperationsStop=new();
        }
        finally{sourceOperationsGate.Release();}
    }
    private sealed class PreviewSnapshot(string excerpt,SourceAnchor? source,string? raster,int width,int height,string notice,bool current,bool navigate,SafeFile? pin,IAsyncDisposable? artifact,ConcurrentDictionary<Guid,PreviewSnapshot> owners,int pageNumber = 1,int? totalPages = null):ISourcePreview
    {
        internal Guid Id{get;}=Guid.NewGuid();private readonly object gate=new();private Task? disposal;
        public string Excerpt=>excerpt;public SourceAnchor? Source=>source;public string? RasterPath=>raster;public int PixelWidth=>width;public int PixelHeight=>height;public string Notice=>notice;public bool SourceCurrent=>current;public bool CanNavigate=>navigate;public int PageNumber=>pageNumber;public int? TotalPages=>totalPages;
        public ValueTask DisposeAsync(){lock(gate){disposal??=DisposeCore();return new(disposal);}}
        private async Task DisposeCore(){try{if(artifact is not null)await artifact.DisposeAsync();}finally{pin?.Dispose();owners.TryRemove(Id,out _);}}
    }
}
