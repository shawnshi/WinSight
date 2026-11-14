using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinSight.App.Services;
using WinSight.Core;
using WinSight.Core.Diagnostics;

namespace WinSight.App.ViewModels;

public enum SearchViewState { Idle, Searching, Results, NoResults, Error }

public sealed class QuickSearchViewModel : INotifyPropertyChanged
{
    private readonly IDesktopBackend backend;
    private readonly string? dataDir;
    private string query = string.Empty;
    private string? selectedKind;
    private SearchMode selectedMode = SearchMode.Hybrid;
    private int selectedIndex = -1;
    private bool isLoading;
    private bool hasNotice;
    private string? noticeText;
    private string statusText = UiText.Ready;
    private SearchViewState viewState = SearchViewState.Idle;
    private CancellationTokenSource? activeCts;
    private long querySequence;
    private readonly TimeSpan searchDebounce;

    /// <summary>Quiet period after the last keystroke before a search starts; keeps fast typing from issuing one query per character.</summary>
    public static readonly TimeSpan DefaultSearchDebounce = TimeSpan.FromMilliseconds(150);

    public ObservableCollection<SearchItemViewModel> Hits { get; } = [];

    public string Query
    {
        get => query;
        set
        {
            if (SetProperty(ref query, value))
            {
                TriggerSearch();
            }
        }
    }

    public string? SelectedKind
    {
        get => selectedKind;
        set
        {
            string? normalized = value?.ToLowerInvariant();
            if (normalized is "document") normalized = "doc";
            else if (normalized is "media") normalized = "video";
            else if (normalized is not (null or "doc" or "pdf" or "image" or "screenshot" or "video" or "audio" or "code"))
            {
                normalized = null;
            }

            if (SetProperty(ref selectedKind, normalized))
            {
                TriggerSearch();
            }
        }
    }

    public SearchMode SelectedMode
    {
        get => selectedMode;
        set
        {
            if (SetProperty(ref selectedMode, value))
            {
                TriggerSearch();
            }
        }
    }

    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (SetProperty(ref selectedIndex, value))
            {
                OnPropertyChanged(nameof(SelectedItem));
            }
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        private set { if (SetProperty(ref isLoading, value)) OnPropertyChanged(nameof(IsRefining)); }
    }

    public bool HasNotice
    {
        get => hasNotice;
        private set => SetProperty(ref hasNotice, value);
    }

    public string? NoticeText
    {
        get => noticeText;
        private set => SetProperty(ref noticeText, value);
    }

    public string StatusText
    {
        get => statusText;
        internal set => SetProperty(ref statusText, value);
    }

    public SearchViewState ViewState
    {
        get => viewState;
        private set
        {
            if (!SetProperty(ref viewState, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsNoResults));
            OnPropertyChanged(nameof(IsError));
        }
    }

    public bool IsIdle => viewState == SearchViewState.Idle;
    public bool IsNoResults => viewState == SearchViewState.NoResults;
    public bool IsError => viewState == SearchViewState.Error;

    /// <summary>True while results are already on screen and the slower semantic pass is still merging in.</summary>
    public bool IsRefining => isLoading && Hits.Count > 0;

    public SearchItemViewModel? SelectedItem =>
        selectedIndex >= 0 && selectedIndex < Hits.Count ? Hits[selectedIndex] : null;

    /// <summary>Committed queries, most recent first. In-memory unless the host passes a persistent store.</summary>
    public SearchHistoryStore History { get; }

    public QuickSearchViewModel(IDesktopBackend backend, string? dataDir = null, TimeSpan? searchDebounce = null, SearchHistoryStore? history = null)
    {
        this.backend = backend;
        this.dataDir = dataDir;
        History = history ?? new SearchHistoryStore();
        this.searchDebounce = searchDebounce is { } value && value >= TimeSpan.Zero ? value : DefaultSearchDebounce;
    }

    public void TriggerSearch()
    {
        activeCts?.Cancel();
        activeCts?.Dispose();
        activeCts = new CancellationTokenSource();
        var ct = activeCts.Token;

        long currentSeq = Interlocked.Increment(ref querySequence);
        _ = ExecuteSearchAsync(query, selectedKind, currentSeq, ct);
    }

    /// <summary>Remembers the current query once the user commits to it (Enter, or acting on a result).</summary>
    public void RecordCurrentQuery() => History.Add(query);

    private async Task ExecuteSearchAsync(string rawQuery, string? barKind, long seq, CancellationToken ct)
    {
        // An inline "type:" operator wins over the type bar for this query only; the bar itself is left alone.
        var (q, operatorKind) = SearchOperators.Parse(rawQuery);
        var kind = operatorKind ?? barKind;
        if (string.IsNullOrWhiteSpace(q))
        {
            selectedIndex = -1;
            Hits.Clear();
            OnPropertyChanged(nameof(SelectedIndex));
            OnPropertyChanged(nameof(SelectedItem));
            IsLoading = false;
            HasNotice = false;
            NoticeText = null;
            StatusText = UiText.Ready;
            ViewState = SearchViewState.Idle;
            return;
        }

        IsLoading = true;
        StatusText = UiText.Searching;
        if (Hits.Count == 0) ViewState = SearchViewState.Searching;

        var keywordShown = false;
        var firstMs = 0.0;
        try
        {
            if (searchDebounce > TimeSpan.Zero) await Task.Delay(searchDebounce, ct);

            // Phase 1: lexical index only (skipped in pure Semantic mode).
            SearchResponse? fast = null;
            if (selectedMode != SearchMode.Semantic)
            {
                try
                {
                    fast = await backend.SearchAsync(new SearchRequest(q, Limit: 60, Mode: SearchMode.Keyword, Kind: kind, MinScore: 0.25), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The hybrid pass below reports the real failure; a keyword fault alone must not blank the page.
                }
                if (IsStale(seq, ct)) return;

                if (fast is { Hits.Count: > 0 })
                {
                    firstMs = fast.Elapsed.TotalMilliseconds;
                    ApplyHits(fast.Hits, preserveSelection: false);
                    keywordShown = true;
                    SetNotice(fast.Notice);
                    StatusText = selectedMode == SearchMode.Keyword
                        ? UiText.Completed(fast.Hits.Count, FileCount(fast.Hits), firstMs, firstMs, keywordOnly: true)
                        : UiText.KeywordPhase(fast.Hits.Count);
                }
                else
                {
                    if (fast is not null) firstMs = fast.Elapsed.TotalMilliseconds;
                    // Never leave the previous query's rows on screen while a different query is still being resolved.
                    if (Hits.Count > 0) ApplyHits(Array.Empty<SearchHit>(), preserveSelection: false);
                    ViewState = SearchViewState.Searching;
                    StatusText = selectedMode == SearchMode.Keyword ? UiText.Searching : UiText.KeywordPhase(0);
                }

                if (selectedMode == SearchMode.Keyword)
                {
                    if (fast is { Hits.Count: 0 } or null)
                        StatusText = UiText.NoResult;
                    ViewState = Hits.Count == 0 ? SearchViewState.NoResults : SearchViewState.Results;
                    return;
                }
            }

            // Phase 2: hybrid or pure semantic ranking replaces the provisional list.
            SearchResponse full;
            try
            {
                full = await backend.SearchAsync(new SearchRequest(q, Limit: 60, Mode: selectedMode, Kind: kind, MinScore: 0.25), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && keywordShown)
            {
                if (IsStale(seq, ct)) return;
                SetNotice(UiText.SemanticUnavailable);
                StatusText = UiText.Completed(fast!.Hits.Count, FileCount(fast.Hits), firstMs, firstMs, keywordOnly: true);
                AppLog.Warn("hybrid search failed; keeping keyword results", ex);
                return;
            }
            if (IsStale(seq, ct)) return;

            ApplyHits(full.Hits, preserveSelection: keywordShown);
            SetNotice(full.Notice);
            StatusText = full.Hits.Count == 0
                ? UiText.NoResult
                : UiText.Completed(full.Hits.Count, FileCount(full.Hits), firstMs, full.Elapsed.TotalMilliseconds, keywordOnly: !full.SemanticAvailable && full.Hits.All(h => h.MatchType == "keyword"));
            ViewState = Hits.Count == 0 ? SearchViewState.NoResults : SearchViewState.Results;
        }
        catch (OperationCanceledException)
        {
            // Normal when typing fast
        }
        catch (Exception ex)
        {
            if (!IsStale(seq, ct))
            {
                selectedIndex = -1;
                Hits.Clear();
                OnPropertyChanged(nameof(SelectedIndex));
                OnPropertyChanged(nameof(SelectedItem));
                HasNotice = true;
                NoticeText = UiText.SearchError(ex.Message);
                StatusText = UiText.SearchFailed;
                ViewState = SearchViewState.Error;
            }
        }
        finally
        {
            if (Interlocked.Read(ref querySequence) == seq)
            {
                IsLoading = false;
                if (ViewState == SearchViewState.Searching)
                    ViewState = Hits.Count == 0 ? SearchViewState.NoResults : SearchViewState.Results;
            }
        }
    }

    private static int FileCount(IReadOnlyList<SearchHit> hits) => hits.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private bool IsStale(long seq, CancellationToken ct) => ct.IsCancellationRequested || Interlocked.Read(ref querySequence) != seq;

    private void SetNotice(string? notice)
    {
        var text = UiText.LocalizeNotice(notice);
        HasNotice = !string.IsNullOrEmpty(text);
        NoticeText = text;
    }

    internal static string HitKey(SearchHit hit) =>
        $"{hit.Path}|{hit.Source?.Location.Page}|{hit.Source?.Location.Paragraph}|{hit.Source?.Location.Sheet}|{hit.Source?.Location.Range}|{hit.Location}|{hit.SegmentKind}";

    private static string RowKey(SearchItemViewModel row) =>
        (row.IsSecondary ? "2|" : "1|") + HitKey(row.Hit) + "|" + row.MoreHits.Count;

    /// <summary>Files whose extra hits are currently shown. Survives refinement so a refresh never folds a group the user opened.</summary>
    private readonly HashSet<string> expandedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One row per file (its best hit); further hits of the same file hang off that row and appear only when expanded.</summary>
    private List<SearchItemViewModel> BuildRows(IReadOnlyList<SearchHit> hits)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<SearchHit>>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits)
        {
            if (!groups.TryGetValue(hit.Path, out var list))
            {
                groups[hit.Path] = list = [];
                order.Add(hit.Path);
            }
            list.Add(hit);
        }

        var rows = new List<SearchItemViewModel>(order.Count);
        foreach (var path in order)
        {
            var list = groups[path];
            var primary = new SearchItemViewModel(list[0], list.Skip(1).ToArray());
            rows.Add(primary);
            if (primary.MoreHits.Count > 0 && expandedFiles.Contains(path))
            {
                primary.GroupExpanded = true;
                rows.AddRange(primary.MoreHits.Select(more => new SearchItemViewModel(more, null, isSecondary: true)));
            }
        }
        return rows;
    }

    /// <summary>Swaps in a new hit list without churn: an identical list keeps its row objects, a changed one keeps the selection by identity.</summary>
    private void ApplyHits(IReadOnlyList<SearchHit> hits, bool preserveSelection)
    {
        var rows = BuildRows(hits);
        var incoming = rows.Select(RowKey).ToArray();
        if (incoming.Length == Hits.Count && incoming.SequenceEqual(Hits.Select(RowKey), StringComparer.Ordinal))
        {
            if (Hits.Count > 0 && selectedIndex < 0) SelectedIndex = 0;
            OnPropertyChanged(nameof(IsRefining));
            return;
        }

        var keep = preserveSelection && SelectedItem is { } current ? RowKey(current) : null;
        selectedIndex = -1;
        Hits.Clear();
        foreach (var row in rows) Hits.Add(row);

        var index = keep is null ? -1 : Array.IndexOf(incoming, keep);
        SelectedIndex = index >= 0 ? index : Hits.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(IsRefining));
        ViewState = Hits.Count == 0 ? SearchViewState.Searching : SearchViewState.Results;
    }

    /// <summary>Shows or hides the other hits of one file in place; the selection stays on the same row (or its file row when it folds away).</summary>
    public void ToggleGroup(SearchItemViewModel? primary)
    {
        if (primary is null || primary.IsSecondary || primary.MoreHits.Count == 0) return;
        var index = Hits.IndexOf(primary);
        if (index < 0) return;
        var selected = SelectedItem;
        var path = primary.Path;

        if (primary.GroupExpanded)
        {
            while (index + 1 < Hits.Count && Hits[index + 1].IsSecondary && string.Equals(Hits[index + 1].Path, path, StringComparison.OrdinalIgnoreCase))
                Hits.RemoveAt(index + 1);
            primary.GroupExpanded = false;
            expandedFiles.Remove(path);
            if (selected is { IsSecondary: true } && !Hits.Contains(selected)) selected = primary;
        }
        else
        {
            var at = index + 1;
            foreach (var more in primary.MoreHits) Hits.Insert(at++, new SearchItemViewModel(more, null, isSecondary: true));
            primary.GroupExpanded = true;
            expandedFiles.Add(path);
        }

        selectedIndex = -1; // force a change notification even when the index number is unchanged
        SelectedIndex = selected is null ? -1 : Hits.IndexOf(selected);
        OnPropertyChanged(nameof(SelectedItem));
    }

    /// <summary>Folds the group a secondary row belongs to and selects its file row (keyboard Left on a child).</summary>
    public void CollapseGroupOf(SearchItemViewModel? row)
    {
        if (row is null) return;
        var primary = row.IsSecondary
            ? Hits.FirstOrDefault(h => !h.IsSecondary && string.Equals(h.Path, row.Path, StringComparison.OrdinalIgnoreCase))
            : row;
        if (primary is { GroupExpanded: true }) ToggleGroup(primary);
    }

    /// <summary>Drops every row of a file, e.g. after its authorization was revoked.</summary>
    private void RemoveRows(SearchItemViewModel item)
    {
        for (var i = Hits.Count - 1; i >= 0; i--)
            if (string.Equals(Hits[i].Path, item.Path, StringComparison.OrdinalIgnoreCase)) Hits.RemoveAt(i);
        expandedFiles.Remove(item.Path);
    }

    public async Task<bool> OpenHitAsync(SearchItemViewModel? item)
    {
        if (item is null) return false;
        RecordCurrentQuery();

        // NM-UI-06: Asynchronously load latest settings before action
        var settings = await backend.LoadSettingsAsync();
        var plan = FileActionPlanner.PlanOpen(item.Path, item.Kind, item.Location, settings, dataDir);

        if (!plan.IsAuthorized)
        {
            // Revoked scope: remove outdated hit from results list
            RemoveRows(item);
            HasNotice = true;
            NoticeText = UiText.NotAuthorized(plan.DenialReason);
            StatusText = UiText.ActionBlocked;
            return false;
        }

        var result = await FileActionService.ExecutePlanAsync(plan);
        if (!result.Success)
        {
            HasNotice = true;
            NoticeText = result.Message;
            StatusText = UiText.ActionFailed;
            return false;
        }

        if (!string.IsNullOrEmpty(result.Message))
        {
            HasNotice = true;
            NoticeText = result.Message;
            StatusText = result.Message;
        }
        return true;
    }

    public async Task<bool> RevealHitAsync(SearchItemViewModel? item)
    {
        if (item is null) return false;

        var settings = await backend.LoadSettingsAsync();
        var plan = FileActionPlanner.PlanReveal(item.Path, item.Kind, settings, dataDir);

        if (!plan.IsAuthorized)
        {
            RemoveRows(item);
            HasNotice = true;
            NoticeText = UiText.NotAuthorized(plan.DenialReason);
            StatusText = UiText.ActionBlocked;
            return false;
        }

        var result = await FileActionService.ExecutePlanAsync(plan);
        return result.Success;
    }

    public async Task<bool> CopyHitAsync(SearchItemViewModel? item)
    {
        if (item is null) return false;

        var settings = await backend.LoadSettingsAsync();
        var plan = FileActionPlanner.PlanCopy(item.Path, item.Kind, settings, dataDir);

        if (!plan.IsAuthorized)
        {
            RemoveRows(item);
            HasNotice = true;
            NoticeText = UiText.NotAuthorized(plan.DenialReason);
            StatusText = UiText.ActionBlocked;
            return false;
        }

        var result = await FileActionService.ExecutePlanAsync(plan);
        if (!string.IsNullOrEmpty(result.Message))
        {
            StatusText = result.Message;
        }
        return result.Success;
    }

    public Task<ISourcePreview> PreviewAsync(SearchItemViewModel item,CancellationToken token=default) =>
        backend.PreviewSourceAsync(item.Hit,item.Kind.Equals("code",StringComparison.OrdinalIgnoreCase),token);
    public Task<ISourcePreview> PreviewPageAsync(SearchItemViewModel item, int pageNumber, CancellationToken token = default) =>
        backend.PreviewSourcePageAsync(item.Hit, pageNumber, item.Kind.Equals("code", StringComparison.OrdinalIgnoreCase), token);
    public Task<SourceNavigationResult> NavigateAsync(SearchItemViewModel item,CancellationToken token=default) =>
        backend.NavigateSourceAsync(item.Hit,item.Kind.Equals("code",StringComparison.OrdinalIgnoreCase),token);

    public async Task<bool> OpenCurrentAsync() => await OpenHitAsync(SelectedItem);
    public async Task<bool> RevealCurrentAsync() => await RevealHitAsync(SelectedItem);
    public async Task<bool> CopyCurrentAsync() => await CopyHitAsync(SelectedItem);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
