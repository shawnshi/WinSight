using System.Collections.ObjectModel;
using System.Text.Json;
using WinSight.Core.Diagnostics;

namespace WinSight.App.Services;

/// <summary>
/// Most-recent-first list of committed queries, kept in the app data directory only (never uploaded).
/// A null path gives an in-memory store, which is what tests use so they never touch real user data.
/// </summary>
public sealed class SearchHistoryStore
{
    private readonly string? path;
    private readonly int capacity;
    private readonly object gate = new();

    public ObservableCollection<string> Items { get; } = [];

    public SearchHistoryStore(string? path = null, int capacity = 20)
    {
        this.path = path;
        this.capacity = Math.Max(1, capacity);
        Load();
    }

    public static string FileIn(string dataDirectory) => Path.Combine(dataDirectory, "search-history.json");

    public bool Add(string? query)
    {
        var text = (query ?? string.Empty).Trim();
        if (text.Length is < 2 or > 200) return false;
        lock (gate)
        {
            var existing = Items.FirstOrDefault(i => string.Equals(i, text, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var index = Items.IndexOf(existing);
                if (index == 0 && existing == text) return true;
                Items.RemoveAt(index);
            }
            Items.Insert(0, text);
            while (Items.Count > capacity) Items.RemoveAt(Items.Count - 1);
            Save();
            return true;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            Items.Clear();
            Save();
        }
    }

    /// <summary>History entries that contain the typed text, best (most recent) first.</summary>
    public IReadOnlyList<string> Suggest(string? typed, int limit = 8)
    {
        var text = (typed ?? string.Empty).Trim();
        lock (gate)
        {
            return Items
                .Where(i => text.Length == 0 || (i.Contains(text, StringComparison.OrdinalIgnoreCase) && !string.Equals(i, text, StringComparison.OrdinalIgnoreCase)))
                .Take(limit)
                .ToArray();
        }
    }

    private void Load()
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [];
            foreach (var item in loaded.Where(i => !string.IsNullOrWhiteSpace(i)).Take(capacity)) Items.Add(item);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("search history unreadable; starting empty", error);
        }
    }

    private void Save()
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Items.ToArray()));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("search history could not be saved", error);
        }
    }
}
