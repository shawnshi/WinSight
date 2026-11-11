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

    public QuickSearchViewModel(IDesktopBackend backend, string? dataDir = null, TimeSpan? searchDebounce = null)
    {
        this.backend = backend;
        this.dataDir = dataDir;
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

    private async Task ExecuteSearchAsync(string q, string? kind, long seq, CancellationToken ct)
    {
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

            // Phase 1: lexical index only. It never touches the embedding host, so the first rows appear in tens of milliseconds
            // even while the semantic pass is queued behind indexing or a cold model start.
            SearchResponse? fast = null;
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
                StatusText = UiText.KeywordPhase(fast.Hits.Count);
            }
            else
            {
                if (fast is not null) firstMs = fast.Elapsed.TotalMilliseconds;
                // Never leave the previous query's rows on screen while a different query is still being resolved.
                if (Hits.Count > 0) ApplyHits(Array.Empty<SearchHit>(), preserveSelection: false);
                ViewState = SearchViewState.Searching;
                StatusText = UiText.KeywordPhase(0);
            }

            // Phase 2: full hybrid ranking replaces the provisional list; the user's selection survives when the row is still present.
            SearchResponse full;
            try
            {
                full = await backend.SearchAsync(new SearchRequest(q, Limit: 60, Mode: SearchMode.Hybrid, Kind: kind, MinScore: 0.25), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && keywordShown)
            {
                if (IsStale(seq, ct)) return;
                SetNotice(UiText.SemanticUnavailable);
                StatusText = UiText.Completed(Hits.Count, firstMs, firstMs, keywordOnly: true);
                AppLog.Warn("hybrid search failed; keeping keyword results", ex);
                return;
            }
            if (IsStale(seq, ct)) return;

            ApplyHits(full.Hits, preserveSelection: keywordShown);
            SetNotice(full.Notice);
            StatusText = full.Hits.Count == 0
                ? UiText.NoResult
                : UiText.Completed(full.Hits.Count, firstMs, full.Elapsed.TotalMilliseconds, keywordOnly: !full.SemanticAvailable && full.Hits.All(h => h.MatchType == "keyword"));
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

    private bool IsStale(long seq, CancellationToken ct) => ct.IsCancellationRequested || Interlocked.Read(ref querySequence) != seq;

    private void SetNotice(string? notice)
    {
        var text = UiText.LocalizeNotice(notice);
        HasNotice = !string.IsNullOrEmpty(text);
        NoticeText = text;
    }

    internal static string HitKey(SearchHit hit) =>
        $"{hit.Path}|{hit.Source?.Location.Page}|{hit.Source?.Location.Paragraph}|{hit.Source?.Location.Sheet}|{hit.Source?.Location.Range}|{hit.Location}|{hit.SegmentKind}";

    /// <summary>Swaps in a new hit list without churn: an identical list keeps its row objects, a changed one keeps the selection by identity.</summary>
    private void ApplyHits(IReadOnlyList<SearchHit> hits, bool preserveSelection)
    {
        var incoming = hits.Select(HitKey).ToArray();
        if (incoming.Length == Hits.Count && incoming.SequenceEqual(Hits.Select(h => HitKey(h.Hit)), StringComparer.Ordinal))
        {
            if (Hits.Count > 0 && selectedIndex < 0) SelectedIndex = 0;
            OnPropertyChanged(nameof(IsRefining));
            return;
        }

        var keep = preserveSelection && SelectedItem is { } current ? HitKey(current.Hit) : null;
        selectedIndex = -1;
        Hits.Clear();
        foreach (var hit in hits) Hits.Add(new SearchItemViewModel(hit));

        var index = keep is null ? -1 : Array.IndexOf(incoming, keep);
        SelectedIndex = index >= 0 ? index : Hits.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(IsRefining));
        ViewState = Hits.Count == 0 ? SearchViewState.Searching : SearchViewState.Results;
    }

    public async Task<bool> OpenHitAsync(SearchItemViewModel? item)
    {
        if (item is null) return false;

        // NM-UI-06: Asynchronously load latest settings before action
        var settings = await backend.LoadSettingsAsync();
        var plan = FileActionPlanner.PlanOpen(item.Path, item.Kind, item.Location, settings, dataDir);

        if (!plan.IsAuthorized)
        {
            // Revoked scope: remove outdated hit from results list
            Hits.Remove(item);
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
            Hits.Remove(item);
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
            Hits.Remove(item);
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
