using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using WinSight.Core;

namespace WinSight.App.ViewModels;

public sealed class SearchItemViewModel : INotifyPropertyChanged
{
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();
    private static readonly SemaphoreSlim ThumbnailGate = new(3);
    private ImageSource? thumbnail;
    private bool thumbnailRequested;
    private CancellationTokenSource? thumbnailCts;

    public event PropertyChangedEventHandler? PropertyChanged;


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
    public string ScorePercentage => Score >= 0 ? $"{(int)Math.Round(Math.Clamp(Score, 0, 1) * 100)}%" : string.Empty;

    public string CompactBadgeText
    {
        get
        {
            if (Hit.Source is { } source)
            {
                var loc = source.Location;
                if (loc.Type == "pdf-page" && loc.Page.HasValue) return $"p. {loc.Page.Value}";
                if (loc.Type == "slide" && loc.Page.HasValue) return $"p. {loc.Page.Value}";
                if (loc.Type == "sheet" && !string.IsNullOrEmpty(loc.Range)) return loc.Range;
                if (loc.Type == "paragraph" && loc.Paragraph.HasValue) return $"¶ {loc.Paragraph.Value}";
            }
            if (Location is null) return string.Empty;
            if (string.Equals(Kind, "audio", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Kind, "video", StringComparison.OrdinalIgnoreCase))
            {
                var ts = TimeSpan.FromSeconds(Location.Value);
                return ts.Hours > 0 ? $"{ts:h\\:mm\\:ss}" : $"{ts:m\\:ss}";
            }
            if (string.Equals(Kind, "code", StringComparison.OrdinalIgnoreCase))
            {
                return LocationEnd is > 0 && LocationEnd != Location
                    ? $"L{(int)Location.Value}–{(int)LocationEnd.Value}"
                    : $"L{(int)Location.Value}";
            }
            if (string.Equals(Kind, "pdf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Kind, "document", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Kind, "doc", StringComparison.OrdinalIgnoreCase))
            {
                return $"p. {(int)Location.Value}";
            }
            return $"{Location.Value:F0}";
        }
    }

    public Visibility HasLocationBadge =>
        string.IsNullOrWhiteSpace(CompactBadgeText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Last two folder names, so same-named files from different projects stay distinguishable in a narrow row.</summary>
    public string ParentFolderLabel
    {
        get
        {
            if (string.IsNullOrEmpty(DirectoryPath)) return string.Empty;
            var parts = DirectoryPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => DirectoryPath,
                1 => parts[0],
                _ => parts[^2] + " › " + parts[^1]
            };
        }
    }

    /// <summary>Type, modified date and size on one short line (date only, so it is not clipped in the list).</summary>
    public string ListMetaCompact
    {
        get
        {
            var ext = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
            if (string.IsNullOrEmpty(ext)) ext = Kind.ToUpperInvariant();
            var parts = new List<string> { ext };
            if (Hit.ModifiedTime is > 0)
                parts.Add(DateTimeOffset.FromUnixTimeMilliseconds((long)(Hit.ModifiedTime.Value * 1000)).LocalDateTime.ToString("yyyy-MM-dd"));
            if (Hit.FileSize is { } bytes)
                parts.Add(bytes switch
                {
                    >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
                    >= 1024 => $"{bytes / 1024.0:F0} KB",
                    _ => $"{bytes} B"
                });
            return string.Join(" · ", parts);
        }
    }

    public string ListAutomationName =>
        (IsSecondary ? $"{FileName} 的另一处命中，{MatchRationale}" : $"{FileName}，{ParentFolderLabel}，{MatchRationale}")
        + (ScorePercentage.Length > 0 ? $"，相关度 {ScorePercentage}" : "")
        + (GroupToggleVisibility == Visibility.Visible ? (groupExpanded ? "，已展开其他命中，按左方向键收起" : $"，另有 {MoreHits.Count} 处命中，按右方向键展开") : "");

    public ImageSource? ThumbnailSource
    {
        get => thumbnail;
        private set
        {
            thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeVisibility)));
        }
    }

    public Visibility ThumbnailVisibility => thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BadgeVisibility => thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Loads the shell thumbnail for a realized row. It is skipped for cloud placeholders (reading one would download the file)
    /// and for formats that only have a generic icon, where the colored type badge is the better signal.
    /// </summary>
    public async Task EnsureThumbnailAsync()
    {
        if (thumbnailRequested || IsSecondary) return;
        thumbnailRequested = true;
        var cts = thumbnailCts = new CancellationTokenSource();
        var token = cts.Token;
        try
        {
            await ThumbnailGate.WaitAsync(token);
            InMemoryRandomAccessStream? data;
            try
            {
                data = await Task.Run(async () =>
                {
                    var attributes = System.IO.File.GetAttributes(Path);
                    const int cloud = 0x1000 | 0x40000 | 0x400000; // FILE_ATTRIBUTE_OFFLINE | RECALL_ON_OPEN | RECALL_ON_DATA_ACCESS
                    if (((int)attributes & cloud) != 0) return null;
                    var file = await StorageFile.GetFileFromPathAsync(Path);
                    using var shell = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 96, ThumbnailOptions.UseCurrentScale);
                    if (shell is null || shell.Type != ThumbnailType.Image || shell.Size == 0) return null;
                    var copy = new InMemoryRandomAccessStream();
                    await RandomAccessStream.CopyAsync(shell, copy);
                    copy.Seek(0);
                    return copy;
                }, token);
            }
            finally { ThumbnailGate.Release(); }

            if (data is null || token.IsCancellationRequested) { data?.Dispose(); return; }
            var bitmap = new BitmapImage { DecodePixelWidth = 96 };
            using (data) await bitmap.SetSourceAsync(data);
            if (!token.IsCancellationRequested) ThumbnailSource = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* a missing or unreadable file simply keeps the type badge */ }
    }

    /// <summary>Called when the row container is recycled so scrolled-past rows stop queuing shell work.</summary>
    public void CancelThumbnail()
    {
        if (thumbnail is not null) return;
        thumbnailCts?.Cancel();
        thumbnailRequested = false;
    }

    public string FriendlyDirectoryPath
    {
        get
        {
            if (string.IsNullOrEmpty(DirectoryPath)) return "";
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var normalized = DirectoryPath.Replace('\\', '/');
            if (!string.IsNullOrEmpty(userProfile))
            {
                var normProfile = userProfile.Replace('\\', '/');
                if (normalized.StartsWith(normProfile, StringComparison.OrdinalIgnoreCase))
                    return "~" + normalized[normProfile.Length..];
            }
            if (normalized.StartsWith("D:/OneDrive/", StringComparison.OrdinalIgnoreCase))
                return "~/OneDrive/" + normalized["D:/OneDrive/".Length..];
            return normalized;
        }
    }

    public string SubtitleText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Excerpt))
            {
                var single = Excerpt.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
                return single.Length > 90 ? "..." + single[..87] + "..." : single;
            }
            return FriendlyDirectoryPath;
        }
    }

    public string ListItemMetadataLine
    {
        get
        {
            var ext = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
            if (string.IsNullOrEmpty(ext)) ext = Kind.ToUpperInvariant();
            long? bytes = Hit.FileSize;
            DateTime? mtime = Hit.ModifiedTime is > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(Hit.ModifiedTime.Value * 1000)).LocalDateTime
                : null;
            if (bytes is null || mtime is null)
            {
                try
                {
                    var fi = new System.IO.FileInfo(Path);
                    if (fi.Exists)
                    {
                        bytes ??= fi.Length;
                        mtime ??= fi.LastWriteTime;
                    }
                }
                catch { }
            }
            if (bytes is not null && mtime is not null)
            {
                var date = mtime.Value.ToString("yyyy-MM-dd HH:mm");
                string size = bytes.Value switch
                {
                    >= 1024 * 1024 => $"{bytes.Value / (1024.0 * 1024.0):F1} MB",
                    >= 1024 => $"{bytes.Value / 1024.0:F0} KB",
                    _ => $"{bytes.Value} B"
                };
                return $"{ext} · {date} · {size}";
            }
            return ext;
        }
    }

    public string FileMetadataLine
    {
        get
        {
            var baseMeta = ListItemMetadataLine;
            var loc = LocationDisplay;
            return string.IsNullOrEmpty(loc) ? baseMeta : $"{baseMeta} · {loc}";
        }
    }

    public string PrimaryOpenLabel
    {
        get
        {
            if (Hit.Source is { } source)
            {
                var loc = source.Location;
                if (loc.Type == "pdf-page" && loc.Page.HasValue) return $"打开第 {loc.Page.Value} 页";
                if (loc.Type == "slide" && loc.Page.HasValue) return $"打开幻灯片 {loc.Page.Value}";
            }
            if (string.Equals(Kind, "code", StringComparison.OrdinalIgnoreCase) && Location is not null)
                return $"打开第 {(int)Location.Value} 行";
            if ((string.Equals(Kind, "video", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Kind, "audio", StringComparison.OrdinalIgnoreCase)) && Location is not null)
            {
                var ts = TimeSpan.FromSeconds(Location.Value);
                string timeStr = ts.Hours > 0 ? $"{ts:h\\:mm\\:ss}" : $"{ts:m\\:ss}";
                return $"从 {timeStr} 播放";
            }
            if (string.Equals(Kind, "pdf", StringComparison.OrdinalIgnoreCase) && Location is not null)
                return $"打开第 {(int)Location.Value} 页";
            return "打开文件";
        }
    }

    public string CanvasExcerptText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Excerpt)) return Excerpt.Trim();

            var previewHint = string.Equals(Kind, "pdf", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(Kind, "doc", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(Kind, "presentation", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(Kind, "sheet", StringComparison.OrdinalIgnoreCase)
                ? "\n\n提示：本次命中来自文件名或元数据。按空格键可打开页面大图与 OCR 文字行。"
                : "";

            return $"[{FileName}]\n\n• 匹配依据: {MatchRationale}\n• 文件属性: {ListItemMetadataLine}\n• 所在目录: {DirectoryPath}{previewHint}";
        }
    }

    public Visibility IsImageVisibility =>
        (string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Kind, "screenshot", StringComparison.OrdinalIgnoreCase))
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsCodeVisibility =>
        string.Equals(Kind, "code", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsDocumentVisibility =>
        !string.Equals(Kind, "code", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Kind, "screenshot", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;

    public string OpenActionVerb
    {
        get
        {
            if (Hit.Source is { } source)
            {
                var loc = source.Location;
                if (loc.Type == "pdf-page" && loc.Page.HasValue) return $"在阅读器中打开第 {loc.Page.Value} 页";
                if (loc.Type == "slide" && loc.Page.HasValue) return $"在演示中定位第 {loc.Page.Value} 张幻灯片";
            }
            if (string.Equals(Kind, "code", StringComparison.OrdinalIgnoreCase) && Location is not null)
                return $"在代码编辑器中打开第 {(int)Location.Value} 行";
            if ((string.Equals(Kind, "video", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Kind, "audio", StringComparison.OrdinalIgnoreCase)) && Location is not null)
            {
                var ts = TimeSpan.FromSeconds(Location.Value);
                string timeStr = ts.Hours > 0 ? $"{ts:hh\\:mm\\:ss}" : $"{ts:mm\\:ss}";
                return $"从 {timeStr} 处开始播放";
            }
            if (string.Equals(Kind, "pdf", StringComparison.OrdinalIgnoreCase) && Location is not null)
                return $"打开至第 {(int)Location.Value} 页";
            return "打开此文件";
        }
    }

    public string MatchRationale
    {
        get
        {
            if (Hit.Source is { } source)
            {
                var loc = source.Location;
                if (loc.Type == "pdf-page" && loc.Page.HasValue) return $"第 {loc.Page.Value} 页内容匹配";
                if (loc.Type == "slide" && loc.Page.HasValue) return $"幻灯片 {loc.Page.Value} 匹配" + (source.Channel == "notes" ? " (演讲备注)" : "");
                if (loc.Type == "sheet") return $"工作表 '{loc.Sheet}' 单元格匹配";
                if (loc.Type == "paragraph") return $"段落 {loc.Paragraph} 正文匹配";
            }
            if (string.Equals(Kind, "code", StringComparison.OrdinalIgnoreCase))
            {
                if (Location is not null) return $"第 {(int)Location.Value} 行代码定义/引用";
                return "代码全文检索匹配";
            }
            if (string.Equals(Kind, "video", StringComparison.OrdinalIgnoreCase))
            {
                if (Location is not null)
                {
                    var ts = TimeSpan.FromSeconds(Location.Value);
                    string t = ts.Hours > 0 ? $"{ts:hh\\:mm\\:ss}" : $"{ts:mm\\:ss}";
                    return $"匹配视频画面于 {t}";
                }
                return "视频内容检索匹配";
            }
            if (string.Equals(Kind, "audio", StringComparison.OrdinalIgnoreCase))
            {
                if (Location is not null)
                {
                    var ts = TimeSpan.FromSeconds(Location.Value);
                    string t = ts.Hours > 0 ? $"{ts:hh\\:mm\\:ss}" : $"{ts:mm\\:ss}";
                    return $"匹配语音片段于 {t}";
                }
                return "音频内容检索匹配";
            }
            if (string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Kind, "screenshot", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(MatchType, "ocr", StringComparison.OrdinalIgnoreCase))
                    return "图片文字 (OCR) 识别命中";
                return "视觉图像多模态语义匹配";
            }
            if (Location is not null) return $"第 {(int)Location.Value} 页匹配";
            if (string.Equals(MatchType, "exact", StringComparison.OrdinalIgnoreCase))
                return "包含完整精确关键词/单号";
            if (string.Equals(MatchType, "keyword", StringComparison.OrdinalIgnoreCase))
                return "中文/关键词倒排索引命中";
            if (string.Equals(MatchType, "semantic", StringComparison.OrdinalIgnoreCase))
                return "多模态语义向量理解匹配";
            return "相关上下文检索命中";
        }
    }

    public string RationaleIconGlyph => Kind.ToLowerInvariant() switch
    {
        "code" => "\uE943",
        "video" => "\uE714",
        "audio" => "\uE8D6",
        "picture" or "image" or "screenshot" => "\uEB9F",
        "pdf" or "document" or "doc" => "\uE8A5",
        _ => MatchType switch
        {
            "exact" => "\uE734",
            "semantic" => "\uE946",
            _ => "\uE8EC"
        }
    };
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
                return $"第 {(int)Location.Value} 页";
            }
            if (string.Equals(Kind, "code", StringComparison.OrdinalIgnoreCase))
            {
                return $"第 {(int)Location.Value} 行";
            }
            return $"{Location.Value:F0}";
        }
    }

    public string IconGlyph
    {
        get
        {
            var ext = System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "pdf" => "\uEA90",
                "xlsx" or "xls" or "csv" or "tsv" => "\uE80A",
                "pptx" or "ppt" => "\uE786",
                "docx" or "doc" or "rtf" or "odt" => "\uE8A5",
                _ => Kind.ToLowerInvariant() switch
                {
                    "pdf" => "\uEA90",
                    "picture" or "image" or "screenshot" => "\uEB9F",
                    "video" => "\uE714",
                    "audio" => "\uE8D6",
                    "code" => "\uE943",
                    "archive" => "\uF012",
                    _ => "\uE8A5"
                }
            };
        }
    }

    public Microsoft.UI.Xaml.Media.SolidColorBrush IconForegroundBrush
    {
        get
        {
            var ext = System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();
            var color = ext switch
            {
                "pdf" => Windows.UI.Color.FromArgb(255, 217, 48, 37),                         // PDF Crimson
                "xlsx" or "xls" or "csv" or "tsv" => Windows.UI.Color.FromArgb(255, 24, 128, 56), // Excel Emerald
                "pptx" or "ppt" => Windows.UI.Color.FromArgb(255, 227, 116, 0),               // PPT Amber Orange
                "docx" or "doc" or "rtf" or "odt" => Windows.UI.Color.FromArgb(255, 26, 115, 232), // Word Cobalt Blue
                _ => Kind.ToLowerInvariant() switch
                {
                    "code" => Windows.UI.Color.FromArgb(255, 124, 58, 237),                   // Code Violet
                    "picture" or "image" or "screenshot" => Windows.UI.Color.FromArgb(255, 0, 137, 123), // Image Teal
                    "video" or "audio" => Windows.UI.Color.FromArgb(255, 219, 39, 119),       // Media Rose
                    _ => Windows.UI.Color.FromArgb(255, 26, 115, 232)
                }
            };
            // High-contrast themes own every color: use the system foreground instead of the brand palette.
            if (Accessibility.HighContrast)
                color = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        }
    }

    public Microsoft.UI.Xaml.Media.SolidColorBrush IconBackgroundBrush
    {
        get
        {
            if (Accessibility.HighContrast) return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            var fg = IconForegroundBrush.Color;
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(28, fg.R, fg.G, fg.B));
        }
    }

    /// <summary>Further hits inside the same file, shown on demand under this row.</summary>
    public IReadOnlyList<SearchHit> MoreHits { get; }

    /// <summary>True for an extra hit listed under its file row.</summary>
    public bool IsSecondary { get; }

    private bool groupExpanded;
    public bool GroupExpanded
    {
        get => groupExpanded;
        set
        {
            if (groupExpanded == value) return;
            groupExpanded = value;
            foreach (var name in new[] { nameof(GroupExpanded), nameof(GroupToggleText), nameof(GroupToggleGlyph), nameof(ListAutomationName) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public int MoreCount => MoreHits.Count;
    public Visibility GroupToggleVisibility => !IsSecondary && MoreHits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string GroupToggleText => groupExpanded ? "收起其他命中" : $"另有 {MoreHits.Count} 处命中";
    public string GroupToggleGlyph => groupExpanded ? "\uE70E" : "\uE70D";

    public Visibility PrimaryRowVisibility => IsSecondary ? Visibility.Collapsed : Visibility.Visible;
    public Thickness TextIndent => IsSecondary ? new Thickness(58, 0, 0, 0) : new Thickness(0);
    public string RowTitle => IsSecondary ? MatchRationale : FileName;

    public SearchItemViewModel(SearchHit hit, IReadOnlyList<SearchHit>? moreHits = null, bool isSecondary = false)
    {
        Hit = hit;
        MoreHits = moreHits ?? Array.Empty<SearchHit>();
        IsSecondary = isSecondary;
        FileName = System.IO.Path.GetFileName(hit.Path);
        DirectoryPath = System.IO.Path.GetDirectoryName(hit.Path) ?? string.Empty;
    }
}
