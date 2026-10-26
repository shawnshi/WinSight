using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Media;

namespace WinSight.Infrastructure.Indexing;

public sealed class IndexingService(IIndexRepository repository, IContentExtractor extractor, IEmbeddingService? embeddings = null) : IIndexService
{
    private readonly SemaphoreSlim runGate = new(1, 1);
    private readonly NativeMediaSamplingService? mediaSampling;
    private readonly NativeDocumentPages? documentPages;
    private readonly NativePresentationPages? presentationPages;

    internal IndexingService(IIndexRepository repository, IContentExtractor extractor, IEmbeddingService? embeddings,
        NativeMediaSamplingService? mediaSampling, NativeDocumentPages? documentPages = null) : this(repository,extractor,embeddings,mediaSampling,documentPages,null) { }

    internal IndexingService(IIndexRepository repository, IContentExtractor extractor, IEmbeddingService? embeddings,
        NativeMediaSamplingService? mediaSampling, NativeDocumentPages? documentPages, NativePresentationPages? presentationPages,
        bool allowDeferredStages = false) : this(repository, extractor, embeddings)
    {
        // A service with no media/page stages is the deferred text-and-structure pass; it is only valid when
        // the caller asks for it explicitly, so a genuinely misconfigured caller still fails.
        if (mediaSampling is null && documentPages is null && presentationPages is null && !allowDeferredStages)
            throw new ArgumentNullException(nameof(mediaSampling));
        this.mediaSampling = mediaSampling;
        this.documentPages = documentPages;
        this.presentationPages = presentationPages;
    }

    private const int MaxWalkEntries = 250000;
    private sealed class EmbeddingDependencyException(Exception cause, string? mediaSafeMessage = null)
        : IOException(mediaSafeMessage ?? "Embedding service failed: " + Describe(cause), cause);
    private sealed record Candidate(string Path, string Kind, long Size, double Mtime, string? Skip);
    private sealed record Crawl(string Root, string? Identity, int Entries, IReadOnlyList<IndexingFailure> Failures, HashSet<string> Seen, List<Candidate> Files, int UnsafeSkipped = 0)
    {
        internal bool Complete => Failures.Count == 0;
    }

    public async Task IndexAsync(IndexRequest request, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(request), "MaxFileBytes must be positive.");
        var roots = request.Roots.Select(FilePolicy.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length > 128) throw new ArgumentException("At most 128 roots can be indexed in a single request.", nameof(request));
        var exclusions = (request.Excluded ?? []).Select(FilePolicy.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var types = new HashSet<string>((request.SkipTypes ?? []).SelectMany(s => s.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Select(s => s.TrimStart('*', '.')), StringComparer.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
        if (roots.Length == 0) return;
        await runGate.WaitAsync(cancellationToken);
        try
        {
            await repository.InitializeAsync(cancellationToken);
            var existing = await repository.ReadFilesAsync(cancellationToken);
            var info = embeddings is null ? null : await embeddings.GetInfoAsync(cancellationToken);
            await GuardMetadataAsync(existing, info, request.Code, cancellationToken);
            var crawls = new List<Crawl>();
            var walkedEntries = 0;
            var git = request.Code ? new GitKeptFiles() : null;
            foreach (var root in roots)
            {
                var crawl = await WalkAsync(root, exclusions, types, request, git, progress, MaxWalkEntries - walkedEntries, cancellationToken);
                crawls.Add(crawl);
                walkedEntries += crawl.Entries;
            }
            var failures = crawls.SelectMany(c => c.Failures).ToList();
            var unsafeSkipped = crawls.Sum(c => c.UnsafeSkipped);
            if (unsafeSkipped > 0)
            {
                // Cloud placeholders and reparse entries are not indexed; say so instead of dropping them silently.
                progress?.Report(new IndexProgress("skipped", null, 0, 0, unsafeSkipped,
                    $"{unsafeSkipped} cloud/offline/reparse entries were skipped; keep them on this device to index them."));
            }
            var previous = existing.ToDictionary(f => FilePolicy.Normalize(f.Path), StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completed = 0;
            var failed = 0;
            var partial = 0;
            foreach (var item in crawls.SelectMany(c => c.Files.Select(file => (File: file, Root: c.Root))))
            {
                var file = item.File;
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(file.Path)) continue;
                previous.TryGetValue(file.Path, out var old);
                bool stablePolicyPartial = !request.Code && file.Kind == "pdf" && documentPages is not null
                    && old is { State: "partial" } && NativeDocumentPages.IsStablePolicyPartial(old.Info);
                bool reusablePresentation = !request.Code && FilePolicy.Extension(file.Path) == "pptx" && presentationPages is not null
                    && old is { State: "partial" } && NativePresentationPages.ReusePartial(old.Info, request.AllowPowerPointAutomation);
                if (!request.Force && file.Skip is null && old is not null && (old.State == "done" || stablePolicyPartial || reusablePresentation) && old.Kind == file.Kind && old.Size == file.Size && Math.Abs(old.ModifiedTime - file.Mtime) < 0.001
                    && (!IsNativeMedia(file, request.Code) || file.Size <= ContentExtractor.MaxBytes && HasCurrentMediaRevision(old.Info))
                    && (request.Code || file.Kind is not ("image" or "screenshot") || NativeImageExtraction.IsCurrent(old.Info))
                    && HasCurrentOfficeRevision(file.Path, request.Code, old.Info, request.Documents ?? new())
                    && (request.Code || HasCurrentLexicalRevision(old.Info))
                    && (request.Code || !NeedsSourceSnapshot(file.Path) || HasSourceSnapshot(old.Info))
                    && (request.Code || file.Kind != "pdf" || documentPages is null || NativeDocumentPages.IsCurrent(old.Info))
                    && (request.Code || FilePolicy.Extension(file.Path) != "pptx" || presentationPages is null || NativePresentationPages.IsCurrent(old.Info)))
                {
                    completed++;
                    if (stablePolicyPartial || reusablePresentation) partial++;
                    progress?.Report(new("unchanged", file.Path, completed, failed, partial));
                    continue;
                }
                // NM-BR-08: Windows matches case-insensitively; repository IDs are bound to their stored binary path.
                var row = new IndexedFile(old?.Id ?? 0, old?.Path ?? file.Path, file.Kind, file.Size, file.Mtime, "pending", null, null, []);
                await repository.UpsertFileAsync(row, cancellationToken);
                progress?.Report(new("pending", file.Path, completed, failed, partial));
                ExtractionResult result;
                if (IsNativeMedia(file, request.Code) && file.Size > ContentExtractor.MaxBytes)
                    result = new([], "partial", $"Native media coverage incomplete: source exceeds {ContentExtractor.MaxBytes} byte limit.");
                else if (file.Skip is not null) result = new([], "skipped", file.Skip);
                else
                {
                    try
                    {
                        if (request.AllowPowerPointAutomation && !request.Code && FilePolicy.Extension(file.Path) == "pptx" && presentationPages is not null)
                            progress?.Report(new("powerpoint-exclusive",file.Path,completed,failed,partial,"Keep PowerPoint closed during this manual indexing interval."));
                        // A single pathological document must not stall a corpus-wide pass. The budget only
                        // applies to the expensive parse, and the file is retried on a later pass.
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        var bounded = request.MaxFileSeconds > 0;
                        if (bounded) budget.CancelAfter(TimeSpan.FromSeconds(request.MaxFileSeconds));
                        try
                        {
                            result = await ProcessAsync(file, request.Code, info, request.Documents ?? new(), request.AllowPowerPointAutomation, budget.Token);
                        }
                        catch (OperationCanceledException) when (bounded && !cancellationToken.IsCancellationRequested)
                        {
                            result = new([], "partial", $"deferred: parsing exceeded the {request.MaxFileSeconds}s per-file budget; retried on a later pass.");
                        }
                    }
                    catch (EmbeddingDependencyException error)
                    {
                        await repository.UpsertFileAsync(row with { State = "failed", Detail = error.Message }, cancellationToken);
                        progress?.Report(new("failed", file.Path, completed + 1, failed + 1, partial, error.Message));
                        throw;
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                // A changed file is never committed with vectors computed from a different size/mtime snapshot.
                if (result.State is "done" or "partial" || IsNativeMedia(file, request.Code) && result.State == "skipped")
                {
                    try
                    {
                        var now = new FileInfo(file.Path);
                        if (!now.Exists || FilePolicy.Unsafe(now.Attributes) || now.Length != file.Size || Math.Abs(Mtime(now) - file.Mtime) >= 0.001)
                            result = new([], "pending", "File changed during extraction; retry on next indexing pass.");
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { result = new([], "failed", IsNativeMedia(file, request.Code) ? DescribeMediaFailure("snapshot validation", error) : Describe(error)); }
                }
                if (result.Info is not null)
                {
                    var metadata = JsonNode.Parse(result.Info)!.AsObject();
                    metadata["indexRoot"] = item.Root;
                    metadata["lexicalRevision"] = "chinese-width-exact-v1";
                    result = result with { Info = metadata.ToJsonString() };
                }
                await repository.UpsertFileAsync(row with { State = result.State, Detail = result.Detail, Info = result.Info, Segments = result.Segments }, cancellationToken);
                completed++;
                if (result.State == "failed") failed++;
                if (result.State == "partial") partial++;
                progress?.Report(new(result.State, file.Path, completed, failed, partial, result.Detail));
            }
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var old in existing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = FilePolicy.Normalize(old.Path);
                var responsible = crawls.Where(c => FilePolicy.Inside(path, c.Root)).ToArray();
                if (responsible.Length == 0 || responsible.Any(c => !c.Complete) || responsible.Any(c => c.Seen.Contains(path))) continue;
                // NM-BR-04: every covering root must be validated AND pinned until the deletion commits.
                var leases = new List<SafeFile>();
                try
                {
                    var valid = true;
                    foreach (var crawl in responsible)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var lease = TryOpenDirectory(crawl.Root, out var problem);
                        if (lease is not null) leases.Add(lease);
                        if (lease is null || lease.Identity != crawl.Identity)
                        {
                            valid = false;
                            var failure = new IndexingFailure(crawl.Root, old.Path, "reconcile_failed", problem ?? "Root identity changed since enumeration; existing rows retained.");
                            failures.Add(failure);
                            progress?.Report(new(failure.Phase, failure.Path, completed, failed, partial, failure.Detail));
                        }
                    }
                    if (!valid) continue;
                    await repository.DeleteFileAsync(old.Path, cancellationToken);
                    progress?.Report(new("deleted", old.Path, completed, failed, partial));
                }
                finally
                {
                    for (var i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (failures.Count != 0)
            {
                var incomplete = new IncompleteIndexingException(failures, completed, failed, partial);
                progress?.Report(new("incomplete", null, completed, failed, partial, incomplete.Message));
                throw incomplete;
            }
            progress?.Report(new("finished", null, completed, failed, partial));
        }
        finally { runGate.Release(); }
    }

    public async Task IndexPathsAsync(IReadOnlyList<string> roots, IReadOnlyList<string> paths, bool code,
        IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        if (paths.Count == 0) return;
        await runGate.WaitAsync(cancellationToken);
        try
        {
            await repository.InitializeAsync(cancellationToken);
            var existing = await repository.ReadFilesAsync(cancellationToken);
            var info = embeddings is null ? null : await embeddings.GetInfoAsync(cancellationToken);
            await GuardMetadataAsync(existing, info, code, cancellationToken);
            var previous = existing.ToDictionary(f => FilePolicy.Normalize(f.Path), StringComparer.OrdinalIgnoreCase);

            var completed = 0;
            var failed = 0;
            var partial = 0;

            foreach (var rawPath in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normPath = FilePolicy.Normalize(rawPath);
                var root = roots.FirstOrDefault(r => FilePolicy.Inside(normPath, r) || string.Equals(normPath, r, StringComparison.OrdinalIgnoreCase))
                    ?? Path.GetDirectoryName(normPath) ?? normPath;

                if (!File.Exists(normPath))
                {
                    if (previous.TryGetValue(normPath, out var old))
                    {
                        await repository.AckTaskWithDeleteAsync(old.Path, normPath, cancellationToken);
                        completed++;
                        progress?.Report(new("deleted", normPath, completed, failed, partial));
                    }
                    continue;
                }

                var kind = FilePolicy.Kind(normPath, code);
                if (kind is null) continue;

                var fi = new FileInfo(normPath);
                if (FilePolicy.Unsafe(fi.Attributes)) continue;

                var candidate = new Candidate(normPath, kind, fi.Length, Mtime(fi), null);
                previous.TryGetValue(normPath, out var oldFile);

                var row = new IndexedFile(oldFile?.Id ?? 0, oldFile?.Path ?? normPath, candidate.Kind, candidate.Size, candidate.Mtime, "pending", null, null, []);
                await repository.UpsertFileAsync(row, cancellationToken);
                progress?.Report(new("pending", normPath, completed, failed, partial));

                var result = await ProcessAsync(candidate, code, info, new DocumentExtractionOptions(), false, cancellationToken);

                if (result.State is "done" or "partial" || IsNativeMedia(candidate, code) && result.State == "skipped")
                {
                    try
                    {
                        var now = new FileInfo(candidate.Path);
                        if (!now.Exists || FilePolicy.Unsafe(now.Attributes) || now.Length != candidate.Size || Math.Abs(Mtime(now) - candidate.Mtime) >= 0.001)
                            result = new([], "pending", "File changed during extraction; retry on next indexing pass.");
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        result = new([], "failed", IsNativeMedia(candidate, code) ? DescribeMediaFailure("snapshot validation", error) : Describe(error));
                    }
                }

                if (result.Info is not null)
                {
                    var metadata = JsonNode.Parse(result.Info)!.AsObject();
                    metadata["indexRoot"] = root;
                    metadata["lexicalRevision"] = "chinese-width-exact-v1";
                    result = result with { Info = metadata.ToJsonString() };
                }

                var finalRow = row with { State = result.State, Detail = result.Detail, Info = result.Info, Segments = result.Segments };
                await repository.AckTaskWithUpsertAsync(finalRow, normPath, null, cancellationToken);
                completed++;
                if (result.State == "failed") failed++;
                if (result.State == "partial") partial++;
                progress?.Report(new(result.State, normPath, completed, failed, partial, result.Detail));
            }
            progress?.Report(new("finished", null, completed, failed, partial));
        }
        finally { runGate.Release(); }
    }

    private async Task GuardMetadataAsync(IReadOnlyList<IndexedFile> files, EmbeddingInfo? info, bool code, CancellationToken token)
    {
        var fingerprint = "native-extraction-v1|" + (code ? "code-lines1800-cells" : "documents1800-overlap200-pdfpig0.1.16-imagehost");
        var stored = await repository.GetMetaAsync("indexing_fingerprint", token);
        var profile = await repository.GetMetaAsync("profile_id", token);
        if (stored is not null && stored != fingerprint || stored is null && files.Count != 0)
            throw new InvalidDataException("Index extraction fingerprint differs or is missing. Rebuild in a separate empty index; Force does not reset metadata.");
        if (info is not null)
        {
            if (string.IsNullOrWhiteSpace(info.ProfileId) || info.Dimension != VectorContract.Dimension)
                throw new InvalidDataException("Embedding profile must be identified and have dimension 768.");
            if (profile is not null && profile != info.ProfileId || profile is null && files.Any(f => f.Segments.Any(s => s.Vector is not null)))
                throw new InvalidDataException("Index model profile differs or is missing. Rebuild in a separate empty index; Force never mixes vectors.");
        }
        // All guards precede all metadata writes. A crash between writes remains fail-closed on the next run.
        if (stored is null) await repository.SetMetaAsync("indexing_fingerprint", fingerprint, token);
        if (info is not null)
        {
            await repository.SetMetaAsync("profile_id", info.ProfileId, token);
            await repository.SetMetaAsync("embedding_info", JsonSerializer.Serialize(info), token);
        }
    }

    private static async Task<Crawl> WalkAsync(string root, string[] excluded, HashSet<string> types, IndexRequest request, GitKeptFiles? git, IProgress<IndexProgress>? progress, int remainingEntries, CancellationToken token)
    {
        var failures = new List<IndexingFailure>();
        var count = 0;
        string? identity = null;
        var files = new List<Candidate>();
        var skippedUnsafe = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skips = excluded.Where(p => FilePolicy.Inside(p, root)).ToArray();
        var queue = new Stack<(string Path, int Depth, bool Project, GitKeptFiles.Repository? Repository)>();
        queue.Push((root, 0, false, null));
        while (queue.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (item.Depth > 128) throw new IOException("Directory depth exceeds 128; reconciliation disabled for this root.");
                if (remainingEntries <= 0) throw new IOException($"Walk exceeds {MaxWalkEntries} entries; reconciliation disabled for this root.");
                using var lease = SafeFile.Open(item.Path, true);
                if (item.Depth == 0) identity = lease.Identity;
                var repositoryFiles = item.Repository;
                if (git is not null)
                {
                    if (item.Depth == 0) repositoryFiles = await git.FindAsync(item.Path, token);
                    else if (GitKeptFiles.HasMarker(item.Path, out var metadataDirectory))
                    {
                        if (!metadataDirectory) continue; // Frozen crawler excludes nested submodules/worktrees.
                        repositoryFiles = await git.LoadAsync(item.Path, token);
                    }
                }
                var project = item.Project || FilePolicy.ProjectMarkers.Any(m => File.Exists(Path.Combine(item.Path, m)) || Directory.Exists(Path.Combine(item.Path, m)));
                foreach (var entry in Directory.EnumerateFileSystemEntries(item.Path, "*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 }))
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > remainingEntries) throw new IOException($"Walk exceeds {MaxWalkEntries} entries; reconciliation disabled for this root.");
                    var path = FilePolicy.Normalize(entry);
                    if (!FilePolicy.Inside(path, root)) throw new IOException("Enumeration escaped authorized root.");
                    if (skips.Any(s => FilePolicy.Under(path, s))) continue;
                    var attributes = File.GetAttributes(path);
                    if (FilePolicy.Unsafe(attributes))
                    {
                        skippedUnsafe++;
                        seen.Add(path);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            var failure = new IndexingFailure(root, path, "crawl_failed", "Unsafe/reparse/cloud directory not enumerated; root reconciliation disabled.");
                            failures.Add(failure);
                            progress?.Report(new(failure.Phase, failure.Path, 0, 0, 0, failure.Detail));
                        }
                        continue;
                    }
                    if ((attributes & FileAttributes.Hidden) != 0 || Path.GetFileName(path).StartsWith('.')) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!FilePolicy.SkipFolder(path, request.Code)) queue.Push((path, item.Depth + 1, project, repositoryFiles));
                        continue;
                    }
                    if (types.Contains(FilePolicy.Extension(path))) continue;
                    var kind = FilePolicy.Kind(path, request.Code);
                    if (kind is null || (!request.Code && project && kind != "screenshot")) continue;
                    if (repositoryFiles is not null && !repositoryFiles.Keeps(path)) continue;
                    seen.Add(path);
                    var stat = new FileInfo(path);
                    var maximum = request.Code ? Math.Min(request.MaxFileBytes, FilePolicy.Extension(path) == "ipynb" ? 20L << 20 : 1L << 20) : request.MaxFileBytes;
                    var reason = stat.Length > maximum ? $"File exceeds MaxFileBytes/type limit ({maximum})." : null;
                    files.Add(new(path, kind, stat.Length, Mtime(stat), reason));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var failure = new IndexingFailure(root, item.Path, "crawl_failed", Describe(error));
                failures.Add(failure);
                progress?.Report(new(failure.Phase, failure.Path, 0, 0, 0, failure.Detail));
                if (count > remainingEntries) break;
            }
        }
        return new(root, identity, count, failures, seen, files, skippedUnsafe);
    }

    private static bool HasCurrentLexicalRevision(string? info)
    {
        if (info is null) return false;
        using var document = JsonDocument.Parse(info);
        return document.RootElement.TryGetProperty("lexicalRevision", out var value) && value.GetString() == "chinese-width-exact-v1";
    }

    private static bool HasCurrentOfficeRevision(string path, bool code, string? info, DocumentExtractionOptions options)
    {
        if (code || FilePolicy.Extension(path) is not ("docx" or "pptx" or "xlsx")) return true;
        if (info is null) return false;
        using var metadata = JsonDocument.Parse(info);
        return metadata.RootElement.TryGetProperty("sourceRevision", out var value) && value.GetString() == OfficeDocumentExtractor.Identity(options);
    }

    private static bool NeedsSourceSnapshot(string path) => FilePolicy.Extension(path) is "pdf" or "pptx" or "docx" or "xlsx" || FilePolicy.Kind(path,false) is "image" or "screenshot";
    private static bool HasSourceSnapshot(string? info)
    {
        if(info is null)return false;using var json=JsonDocument.Parse(info);
        return json.RootElement.TryGetProperty("sourceSha256V1",out var hash)&&hash.ValueKind==JsonValueKind.String&&hash.GetString() is {Length:64} text&&text.All(Uri.IsHexDigit);
    }
    private async Task<ExtractionResult> ProcessAsync(Candidate file, bool code, EmbeddingInfo? info, DocumentExtractionOptions options, bool allowPowerPointAutomation, CancellationToken token)
    {
        if(code || !NeedsSourceSnapshot(file.Path))return await ProcessContentAsync(file,code,info,options,allowPowerPointAutomation,token);
        try
        {
            using var source=SafeFile.Open(file.Path);
            var result=await ProcessContentAsync(file,code,info,options,allowPowerPointAutomation,token);
            if(result.State is not ("done" or "partial") || result.Info is null)return result;
            source.Stream!.Position=0;var hash=Convert.ToHexString(await SHA256.HashDataAsync(source.Stream,token)).ToLowerInvariant();
            var metadata=JsonNode.Parse(result.Info)!.AsObject();metadata["sourceSha256V1"]=hash;
            return result with {Info=metadata.ToJsonString()};
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
        catch(EmbeddingDependencyException){throw;}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {return new([],"failed","Source snapshot failed: "+e.GetType().Name);}
    }
    private async Task<ExtractionResult> ProcessContentAsync(Candidate file, bool code, EmbeddingInfo? info, DocumentExtractionOptions options, bool allowPowerPointAutomation, CancellationToken token)
    {
        try
        {
            using var lease = SafeFile.Open(file.Path);
            if (IsNativeMedia(file, code))
            {
                if (lease.Stream!.Length > ContentExtractor.MaxBytes)
                    return new([], "partial", $"Native media coverage incomplete: source exceeds {ContentExtractor.MaxBytes} byte limit.");
                return await ProcessNativeMediaAsync(file, info, token);
            }
            if (!extractor.CanExtract(file.Path, code)) return new([], "skipped", "Unsupported extractor type.");
            var result = await extractor.ExtractAsync(file.Path, code, options, token);
            token.ThrowIfCancellationRequested();
            if (result.State is not ("done" or "partial" or "skipped" or "failed")) throw new InvalidDataException("Extractor returned an invalid terminal state.");
            if (result.Segments.Count > ContentExtractor.MaxSegments || result.Segments.Sum(s => (long)(s.Excerpt?.Length ?? 0)) > ContentExtractor.MaxCharacters
                || result.Segments.Any(s => s.Excerpt?.Length > 65536)) throw new InvalidDataException("Extractor exceeded segment/text limits.");
            if (result.Segments.Any(s => s.Excerpt?.Contains("PRIVATE KEY-----", StringComparison.Ordinal) == true))
                return new([], "skipped", "Content holds private-key material.");
            var segments = result.Segments.Select(s => s with { Vector = null }).ToArray();
            if (!code && FilePolicy.Extension(file.Path) == "pptx" && presentationPages is not null && result.State == "skipped" && result.Info is not null)
            {
                using var metadata = JsonDocument.Parse(result.Info);
                if (metadata.RootElement.TryGetProperty("nativeVisualEligible", out var eligible) && eligible.ValueKind == JsonValueKind.True)
                    result = result with { State = "done", Detail = null };
            }
            if (result.State is "failed" or "skipped") return result with { Segments = [] };
            var errors = new List<string>();
            var jobs = new List<(int Index, EmbeddingInput Input)>();
            var unavailable = false;
            var title = code ? ContentExtractor.Title(file.Path) : Path.GetFileNameWithoutExtension(file.Path);
            if (result.Info is not null)
            {
                try
                {
                    using var metadata = JsonDocument.Parse(result.Info);
                    if (metadata.RootElement.ValueKind == JsonValueKind.Object && metadata.RootElement.TryGetProperty("title", out var value)
                        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) title = ContentExtractor.Prefix(value.GetString()!, 1024);
                }
                catch (JsonException) { /* Info is opaque for third-party extractors, not necessarily JSON. */ }
            }
            foreach (var pair in segments.Select((segment, index) => (segment, index)))
            {
                var segment = pair.segment;
                EmbeddingInput? input = segment.Modality switch
                {
                    "none" => null,
                    "text" when segment.Excerpt is null || !HasWords(segment.Excerpt) => null,
                    "text" => new("text", $"title: {title} | text: {(segment.Kind == "page" ? ContentExtractor.Prefix(segment.Excerpt!, 4000) : segment.Excerpt)}"),
                    "image" when segment.Kind == "image" => new("image", file.Path, file.Kind == "screenshot" ? 280 : 140),
                    _ => throw new NotSupportedException("Extractor modality has no safe media path/ownership contract.")
                };
                if (input is null) continue;
                if (embeddings is null || info is null || !info.Capabilities.Contains(input.Operation, StringComparer.OrdinalIgnoreCase))
                { unavailable = true; continue; }
                jobs.Add((pair.index, input));
            }
            var succeeded = 0;
            foreach (var group in jobs.GroupBy(j => (j.Input.Operation, j.Input.Budget)))
            foreach (var batch in group.Chunk(16))
            {
                token.ThrowIfCancellationRequested();
                IReadOnlyList<EmbeddingResult> vectors;
                try { vectors = await embeddings!.EmbedAsync(batch.Select(j => j.Input).ToArray(), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                { throw new EmbeddingDependencyException(error); }
                token.ThrowIfCancellationRequested();
                if (vectors.Count != batch.Length) throw new InvalidDataException("Embedding item count differs from request.");
                for (var i = 0; i < batch.Length; i++)
                {
                    var vector = vectors[i];
                    if (vector.Error is not null || vector.Vector is null)
                    { errors.Add(vector.Error ?? "Embedding is missing."); continue; }
                    try { VectorContract.Validate(vector.Vector); }
                    catch (InvalidDataException error) { errors.Add(error.Message); continue; }
                    segments[batch[i].Index] = segments[batch[i].Index] with { Vector = vector.Vector };
                    succeeded++;
                }
            }
            if (errors.Count != 0) return new(segments, succeeded == 0 ? "failed" : "partial", Join(result.Detail, $"{errors.Count} embedding errors: {string.Join("; ", errors.Distinct().Take(3))}"), result.Info);
            if (unavailable) return new(segments, "partial", Join(result.Detail, "Semantic extraction unavailable: no embedding service or required modality capability; keyword content retained."), result.Info);
            var encoded = result with { Segments = segments, State = segments.Length == 0 && result.State == "done" ? "skipped" : result.State };
            if (!code && file.Kind == "pdf" && documentPages is not null)
            {
                try { return await documentPages.AppendAsync(file.Path, title, encoded, embeddings, info, token); }
                catch (NativeDocumentEmbeddingException error) { throw new EmbeddingDependencyException(error, error.Message); }
            }
            if (!code && FilePolicy.Extension(file.Path) == "pptx" && presentationPages is not null)
            {
                try { return await presentationPages.AppendAsync(file.Path, title, encoded, embeddings, info, allowPowerPointAutomation, token); }
                catch (NativePresentationEmbeddingException error) { throw new EmbeddingDependencyException(error, error.Message); }
            }
            return encoded;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (EmbeddingDependencyException) { throw; }
        // This boundary records real per-file extraction errors, never converts them into empty success/no_data.
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { return new([], "failed", IsNativeMedia(file, code) ? DescribeMediaFailure("source access", error) : Describe(error)); }
    }

    private bool IsNativeMedia(Candidate file, bool code) => mediaSampling is not null && !code
        && file.Kind is "audio" or "video" && FilePolicy.Kind(file.Path, false) == file.Kind;

    private static bool HasCurrentMediaRevision(string? info)
    {
        if (info is null) return false;
        try
        {
            using var metadata = JsonDocument.Parse(info);
            return metadata.RootElement.ValueKind == JsonValueKind.Object
                && metadata.RootElement.TryGetProperty("native_media_revision", out var revision)
                && revision.ValueKind == JsonValueKind.String && revision.GetString() == NativeMediaSamplingService.Revision;
        }
        catch (JsonException) { return false; }
    }

    private async Task<ExtractionResult> ProcessNativeMediaAsync(Candidate file, EmbeddingInfo? info, CancellationToken token)
    {
        var segments = new List<IndexSegment>();
        var errors = new List<string>();
        EmbeddingDependencyException? embeddingFailure = null;
        var stage = "source acquisition";
        if (embeddings is null || info is null)
            return new([], "partial", "Native media coverage incomplete: embedding service/information unavailable.");
        var includeImage = info.Capabilities.Contains("image", StringComparer.OrdinalIgnoreCase);
        var includeAudio = info.Capabilities.Contains("audio", StringComparer.OrdinalIgnoreCase);
        if (file.Kind == "audio" && !includeAudio || file.Kind == "video" && !includeImage && !includeAudio)
            return new([], "partial", "Native media coverage incomplete: no usable embedding modality capability.");
        try
        {
            using var source = NativeMediaSource.Acquire(file.Path, file.Kind);
            stage = "metadata";
            await using var session = await mediaSampling!.OpenAsync(source, token);
            stage = "sampling/decode/protocol/cleanup";
            var metadata = session.Metadata;
            if (metadata.Kind != file.Kind || !double.IsFinite(metadata.DurationSeconds) || metadata.DurationSeconds <= 0)
                throw new InvalidDataException("Native sampler returned invalid source metadata.");
            var omitted = file.Kind == "video" && !includeImage || (file.Kind == "audio" || metadata.HasAudio) && !includeAudio;
            var count = 0;
            await foreach (var sample in session.ReadAsync(includeImage, includeAudio, token))
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > ContentExtractor.MaxSegments) throw new InvalidDataException("Native sampler exceeded segment limit.");
                    var segment = sample.Segment;
                    if ((segment.Kind, segment.Modality, sample.Input.Operation) is not (("frame", "image", "image") or ("audio", "audio", "audio"))
                        || segment.Location is not double start || segment.LocationEnd is not double end
                        || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > metadata.DurationSeconds)
                        throw new InvalidDataException("Native sampler returned an invalid modality or source interval.");
                    IReadOnlyList<EmbeddingResult> vectors;
                    try { vectors = await embeddings.EmbedAsync([sample.Input], token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                    { embeddingFailure = new EmbeddingDependencyException(error, DescribeMediaFailure(
                        FormattableString.Invariant($"embedding [{start:R}, {end:R})"), error)); throw embeddingFailure; }
                    token.ThrowIfCancellationRequested();
                    if (vectors.Count != 1)
                    { errors.Add(MediaEmbeddingError(start, end, "item-count")); continue; }
                    var vector = vectors[0];
                    if (vector.Error is not null || vector.Vector is null)
                    { errors.Add(MediaEmbeddingError(start, end, vector.Error is not null ? "item-error" : "missing-vector")); continue; }
                    try { VectorContract.Validate(vector.Vector); }
                    catch (InvalidDataException) { errors.Add(MediaEmbeddingError(start, end, "invalid-vector")); continue; }
                    // Only typed sampler leases supply paths; neither an artifact path nor untrusted extractor text is persisted.
                    segments.Add(segment with { Excerpt = null, Vector = vector.Vector });
                }
                finally { await sample.DisposeAsync(); }
            }
            token.ThrowIfCancellationRequested();
            if (errors.Count != 0)
                return new(segments, segments.Count == 0 ? "failed" : "partial",
                    Join(session.Detail, $"{errors.Count} embedding errors: {string.Join("; ", errors.Distinct().Take(3))}"));
            if (omitted || !session.CoverageComplete)
                return new(segments, "partial", Join(session.Detail, "Native media coverage incomplete: omitted modality or sampling limit."));
            var nativeInfo = JsonSerializer.Serialize(new
            {
                native_media_revision = NativeMediaSamplingService.Revision,
                duration = metadata.DurationSeconds,
                has_audio = metadata.HasAudio,
                segments = segments.Count
            });
            return new(segments, segments.Count == 0 ? "skipped" : "done", session.Detail, nativeInfo);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (EmbeddingDependencyException) { throw; }
        // Keep already embedded items when a later decode/protocol/cleanup operation fails, without claiming complete coverage.
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            token.ThrowIfCancellationRequested();
            if (embeddingFailure is not null)
                throw new EmbeddingDependencyException(new AggregateException(embeddingFailure, error),
                    embeddingFailure.Message + " " + DescribeMediaFailure("cleanup after embedding failure", error));
            return new(segments, segments.Count == 0 ? "failed" : "partial", DescribeMediaFailure(stage, error));
        }
    }

    // NM-N2B1R-01: external messages/type names can encode artifact or task paths in arbitrary forms.
    // Only trusted stages/categories and numeric diagnostics cross the media persistence boundary; causes stay transient.
    private static string DescribeMediaFailure(string stage, Exception error)
    {
        var category = error switch
        {
            InvalidDataException => "invalid-data",
            IOException => "io",
            UnauthorizedAccessException => "permission",
            JsonException => "protocol",
            TimeoutException => "timeout",
            NotSupportedException => "unsupported",
            ArgumentException => "argument",
            ObjectDisposedException => "disposed",
            InvalidOperationException => "invalid-state",
            AggregateException => "aggregate",
            OperationCanceledException => "cancelled",
            _ => "other"
        };
        return FormattableString.Invariant($"Native media {stage} failed (category: {category}, HResult: {error.HResult}).");
    }

    private static string MediaEmbeddingError(double start, double end, string category)
        => FormattableString.Invariant($"Native media embedding [{start:R}, {end:R}) failed (category: {category}).");

    private static SafeFile? TryOpenDirectory(string path, out string? problem)
    {
        problem = null;
        try { return SafeFile.Open(path, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problem = Describe(error); return null; }
    }
    private static double Mtime(FileInfo file) => (file.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;
    private static string Describe(Exception error) => $"{error.GetType().Name}: {ContentExtractor.Prefix(error.Message, 1024)}";
    private static string Join(string? first, string second) => first is null ? second : first + " " + second;
    private static bool HasWords(string text)
    {
        var letters = 0;
        var others = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            if (Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark) letters++;
            else others++;
        }
        return letters > 0 && letters * 4 >= others;
    }
}
