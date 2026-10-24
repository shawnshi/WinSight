using Microsoft.UI.Xaml;
using WinSight.Core;

namespace WinSight.App.ViewModels;

public sealed class SearchItemViewModel
{
    public SearchHit Hit { get; }
    public string Path => Hit.Path;
    public string FileName { get; }
    public string DirectoryPath { get; }
    public string Kind => Hit.Kind;
    public double Score => Hit.Score;
    public string MatchType => Hit.MatchType;
    public string? Excerpt => Hit.Excerpt;
    public double? Location => Hit.Location;
    public double? LocationEnd => Hit.LocationEnd;
    public string? SegmentKind => Hit.SegmentKind;

    public Visibility ExcerptVisibility =>
        string.IsNullOrWhiteSpace(Excerpt) ? Visibility.Collapsed : Visibility.Visible;

    public string ScoreDisplay => $"排序分 {Score:0.000}";
    public string LocationDisplay
    {
        get
        {
            if (Hit.Source is { } source)
            {
                var value = source.Location;
                if (value.Type == "sheet") return $"'{value.Sheet?.Replace("'", "''", StringComparison.Ordinal)}'!{value.Range}";
                if (value.Type == "slide") return $"幻灯片 {value.Page}" + (source.Channel == "notes" ? " · 演讲者备注" : "");
                if (value.Type == "paragraph") return $"段落 {value.Paragraph}" + (value.Part == "body" ? "" : $" · {value.Part}");
                if (value.Type == "pdf-page") return $"第 {value.Page} 页";
            }
            if (Location is null) return string.Empty;
            if (string.Equals(Kind, "audio", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Kind, "video", StringComparison.OrdinalIgnoreCase))
            {
                var ts = TimeSpan.FromSeconds(Location.Value);
                return ts.Hours > 0 ? $"{ts:hh\\:mm\\:ss}" : $"{ts:mm\\:ss}";
            }
            if (string.Equals(Kind, "pdf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Kind, "document", StringComparison.OrdinalIgnoreCase))
            {
                return $"Page {(int)Location.Value}";
            }
            if (string.Equals(Kind, "code", StringComparison.OrdinalIgnoreCase))
            {
                return $"Line {(int)Location.Value}";
            }
            return $"{Location.Value:F0}";
        }
    }

    public string IconGlyph => Kind.ToLowerInvariant() switch
    {
        "pdf" or "document" or "doc" => "\uE8A5", // Document
        "picture" or "image" => "\uEB9F",         // Picture
        "video" => "\uE714",                      // Video
        "audio" => "\uE8D6",                      // Audio
        "code" => "\uE943",                       // Code
        "archive" => "\uF012",                    // Archive / Zip
        _ => "\uE8A5"                             // Default File
    };

    public SearchItemViewModel(SearchHit hit)
    {
        Hit = hit;
        FileName = System.IO.Path.GetFileName(hit.Path);
        DirectoryPath = System.IO.Path.GetDirectoryName(hit.Path) ?? string.Empty;
    }
}
