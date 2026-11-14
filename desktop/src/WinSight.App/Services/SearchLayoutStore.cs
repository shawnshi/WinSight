using System.Text.Json;
using WinSight.Core.Diagnostics;

namespace WinSight.App.Services;

/// <summary>
/// Small per-user UI state for the search page: the width the user dragged the result list to and whether the
/// first-run tip was shown. Kept out of AppSettings on purpose; it is presentation state, not configuration.
/// A null path keeps everything in memory (tests).
/// </summary>
public sealed class SearchLayoutStore
{
    public const double MinListWidth = 260;

    private sealed class State
    {
        public double? ListWidth { get; set; }
        public bool TipSeen { get; set; }
        public WinSight.Core.WindowPlacement? MainWindow { get; set; }
    }

    private readonly string? path;
    private State state = new();

    public SearchLayoutStore(string? path = null)
    {
        this.path = path;
        if (path is null || !File.Exists(path)) return;
        try { state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("search layout state unreadable; using defaults", error);
        }
    }

    public static string FileIn(string dataDirectory) => Path.Combine(dataDirectory, "search-layout.json");

    public double? ListWidth
    {
        get => state.ListWidth is { } w && double.IsFinite(w) && w >= MinListWidth ? w : null;
        set { state.ListWidth = value; Save(); }
    }

    public bool TipSeen
    {
        get => state.TipSeen;
        set { state.TipSeen = value; Save(); }
    }

    public WinSight.Core.WindowPlacement? MainWindowPlacement
    {
        get => state.MainWindow;
        set { state.MainWindow = value; Save(); }
    }

    /// <summary>Keeps the list wide enough to read and leaves the stage at least <paramref name="minStage"/> pixels.</summary>
    public static double ClampListWidth(double requested, double container, double minStage = 360) =>
        Math.Max(MinListWidth, Math.Min(requested, container - minStage));

    private void Save()
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("search layout state could not be saved", error);
        }
    }
}
