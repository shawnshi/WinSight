using System.Diagnostics;
using System.Text.Json;
using WinSight.Core;
using WinSight.Core.Diagnostics;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Inference;
using WinSight.Infrastructure.Media;
using WinSight.Infrastructure.Search;
using WinSight.Infrastructure.Storage;

namespace WinSight.Infrastructure.Runtime;

public sealed partial class DesktopBackend
{
public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Query);
        if (request.Limit is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(request), "Limit must be in 1..2000.");
        ObjectDisposedException.ThrowIf(stopping != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeStop.Token);
        cancellationToken = linked.Token;
        SearchService service;
        Stopwatch clock;
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var codeQuery = request.Kind == "code" || request.Query.TrimStart().StartsWith("code:", StringComparison.OrdinalIgnoreCase);
            var roots = (codeQuery ? settings.CodeFolders : settings.Folders).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (request.Root is not null && !IsAuthorizedResult(request.Root, codeQuery))
                throw new UnauthorizedAccessException("Search root is outside the authorized selection or is an application data directory.");
            if (roots.Length == 0) return new SearchResponse([], TimeSpan.Zero, false, "Choose an authorized folder before searching.");
            clock = System.Diagnostics.Stopwatch.StartNew();
            var stores = await GetRepositoriesAsync(cancellationToken);
            service = new SearchService(stores[codeQuery ? 1 : 0], request.Mode == SearchMode.Keyword ? null : GetEmbeddings(),
                CreateAuthorizer(codeQuery));
        }
        finally { lifecycle.Release(); }
        var response = await service.SearchAsync(request, cancellationToken);
        return response with { Elapsed = clock.Elapsed };
    }

private string IndexDirectory => settings.IndexDirectory ?? Path.Combine(dataDirectory, "index");

private async Task<IReadOnlyList<IIndexRepository>> GetRepositoriesAsync(CancellationToken cancellationToken)
    {
        if (repositories.Count != 0) return repositories;
        Directory.CreateDirectory(IndexDirectory);
        var created = new List<IIndexRepository>();
        try
        {
            foreach (var file in new[] { "index.sqlite", "code.sqlite" })
            {
                var repository = new SqliteIndexRepository(Path.Combine(IndexDirectory, file));
                created.Add(repository);
                await repository.InitializeAsync(cancellationToken);
            }
            repositories.AddRange(created);
            return repositories;
        }
        catch
        {
            foreach (var repository in created) await repository.DisposeAsync();
            throw;
        }
    }

/// <summary>Counts indexed rows for one subtree so a completion message can state a real number.</summary>
    private async Task<int> CountIndexedFilesAsync(string root, CancellationToken cancellationToken)
    {
        var repository = (await GetRepositoriesAsync(cancellationToken).ConfigureAwait(false))[0];
        return await repository.CountFilesUnderAsync(root, cancellationToken).ConfigureAwait(false);
    }

/// <summary>
    /// Builds the per-query scope check once: roots, skips and the excluded data directories are normalised a single time,
    /// so each of the (possibly hundreds of thousands of) indexed paths costs only ordinal prefix comparisons.
    /// Stored paths are already canonical; anything that is not falls back to the full normalising check.
    /// </summary>
    internal Func<string, bool> CreateAuthorizer(bool code)
    {
        static string Canonical(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        static bool Within(string path, string root) =>
            path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || (path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] is '\\' or '/');
        var excluded = new[] { Canonical(dataDirectory), Canonical(IndexDirectory) };
        var skips = (code ? settings.CodeSkipped : settings.Skipped).Select(Canonical).ToArray();
        var scopes = (code ? settings.CodeFolders : settings.Folders).Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => (Root: root, Skips: skips.Where(skip => Within(skip, root) && !skip.Equals(root, StringComparison.OrdinalIgnoreCase)).ToArray()))
            .ToArray();
        return path =>
        {
            if (string.IsNullOrEmpty(path)) return false;
            // Mixed separators, dot segments and trailing separators are the only shapes GetFullPath would change.
            var plain = path.IndexOf('/') < 0 && !path.Contains("\\.", StringComparison.Ordinal) && !path.EndsWith('\\');
            var target = plain && Path.IsPathFullyQualified(path) ? path : Canonical(path);
            foreach (var blocked in excluded) if (Within(target, blocked)) return false;
            foreach (var (root, rootSkips) in scopes)
            {
                if (!Within(target, root)) continue;
                var skipped = false;
                foreach (var skip in rootSkips) if (Within(target, skip)) { skipped = true; break; }
                if (!skipped) return true;
            }
            return false;
        };
    }

internal bool IsAuthorizedResult(string path, bool code)
    {
        if (IsUnder(path, dataDirectory) || IsUnder(path, IndexDirectory)) return false;
        var roots = code ? settings.CodeFolders : settings.Folders;
        var skipped = code ? settings.CodeSkipped : settings.Skipped;
        // Skips belong to each chosen root; a separately chosen nested root overrides an ancestor's skip.
        return roots.Any(root => IsUnder(path, root) && !skipped.Any(skip =>
            IsUnder(skip, root) && !PathsEqual(skip, root) && IsUnder(path, skip)));
    }

private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

internal static bool IsUnder(string path, string root)
    {
        var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return target.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
