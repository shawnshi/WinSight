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
                ? "\n\n💡 提示: 匹配文件名/元数据。按 [Space] 空格键或点击【页图校准】可调出独立高清光栅页图与 OCR 识别层。"
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
                return $"Page {(int)Location.Value}";
            }
            if (string.Equals(Kind, "code", StringComparison.OrdinalIgnoreCase))
            {
                return $"Line {(int)Location.Value}";
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
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        }
    }

    public Microsoft.UI.Xaml.Media.SolidColorBrush IconBackgroundBrush
    {
        get
        {
            var fg = IconForegroundBrush.Color;
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(28, fg.R, fg.G, fg.B));
        }
    }

    public SearchItemViewModel(SearchHit hit)
    {
        Hit = hit;
        FileName = System.IO.Path.GetFileName(hit.Path);
        DirectoryPath = System.IO.Path.GetDirectoryName(hit.Path) ?? string.Empty;
    }
}
