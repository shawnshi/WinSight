using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinSight.App.Services;
using WinSight.Core;

namespace WinSight.App.ViewModels;

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
    private string statusText = "Ready";
    private CancellationTokenSource? activeCts;
    private long querySequence;

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
        set => SetProperty(ref selectedIndex, value);
    }

    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
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
        private set => SetProperty(ref statusText, value);
    }

    public SearchItemViewModel? SelectedItem =>
        selectedIndex >= 0 && selectedIndex < Hits.Count ? Hits[selectedIndex] : null;

    public QuickSearchViewModel(IDesktopBackend backend, string? dataDir = null)
    {
        this.backend = backend;
        this.dataDir = dataDir;
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
            Hits.Clear();
            SelectedIndex = -1;
            IsLoading = false;
            HasNotice = false;
            NoticeText = null;
            StatusText = "Ready";
            return;
        }

        IsLoading = true;
        StatusText = "Searching...";

        try
        {
            var req = new SearchRequest(
                Query: q,
                Limit: 50,
                Mode: SearchMode.Hybrid,
                Kind: kind);

            var response = await backend.SearchAsync(req, ct);

            // Stale result guard: check token and sequence
            if (ct.IsCancellationRequested || Interlocked.Read(ref querySequence) != seq)
            {
                return;
            }

            Hits.Clear();
            foreach (var hit in response.Hits)
            {
                Hits.Add(new SearchItemViewModel(hit));
            }

            SelectedIndex = Hits.Count > 0 ? 0 : -1;
            HasNotice = !string.IsNullOrEmpty(response.Notice);
            NoticeText = response.Notice;
            StatusText = $"Found {response.Hits.Count} item{(response.Hits.Count == 1 ? "" : "s")} in {response.Elapsed.TotalMilliseconds:F0} ms";
        }
        catch (OperationCanceledException)
        {
            // Normal when typing fast
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref querySequence) == seq)
            {
                Hits.Clear();
                SelectedIndex = -1;
                HasNotice = true;
                NoticeText = $"Search error: {ex.Message}";
                StatusText = "Error during search";
            }
        }
        finally
        {
            if (Interlocked.Read(ref querySequence) == seq)
            {
                IsLoading = false;
            }
        }
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
            NoticeText = $"File no longer authorized: {plan.DenialReason}";
            StatusText = "Action blocked by authorization rules";
            return false;
        }

        var result = await FileActionService.ExecutePlanAsync(plan);
        if (!result.Success)
        {
            HasNotice = true;
            NoticeText = result.Message;
            StatusText = "Action failed";
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
            NoticeText = $"File no longer authorized: {plan.DenialReason}";
            StatusText = "Action blocked by authorization rules";
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
            NoticeText = $"File no longer authorized: {plan.DenialReason}";
            StatusText = "Action blocked by authorization rules";
            return false;
        }

        var result = await FileActionService.ExecutePlanAsync(plan);
        if (!string.IsNullOrEmpty(result.Message))
        {
            StatusText = result.Message;
        }
        return result.Success;
    }

    public async Task<bool> OpenCurrentAsync() => await OpenHitAsync(SelectedItem);
    public async Task<bool> RevealCurrentAsync() => await RevealHitAsync(SelectedItem);
    public async Task<bool> CopyCurrentAsync() => await CopyHitAsync(SelectedItem);

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
