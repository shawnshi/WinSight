using System.Text.RegularExpressions;

namespace WinSight.App.Services;

/// <summary>
/// Pulls an inline <c>type:</c> operator out of the query so "预算 type:pdf" filters like the type bar does.
/// Only operators the backend can honor are interpreted; anything else stays part of the text.
/// </summary>
internal static partial class SearchOperators
{
    [GeneratedRegex(@"(?<![\w:])type:(?<value>[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeOperator();

    public static (string Query, string? Kind) Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return (query, null);
        // "code:" is a query-mode prefix owned by the backend, not a filter operator.
        if (query.TrimStart().StartsWith("code:", StringComparison.OrdinalIgnoreCase)) return (query, null);

        string? kind = null;
        var cleaned = TypeOperator().Replace(query, match =>
        {
            var mapped = MapKind(match.Groups["value"].Value);
            if (mapped is null) return match.Value;
            kind = mapped;
            return string.Empty;
        });
        return kind is null ? (query, null) : (Regex.Replace(cleaned, @"\s{2,}", " ").Trim(), kind);
    }

    public static string? MapKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "doc" or "docx" or "word" or "ppt" or "pptx" or "xls" or "xlsx" or "excel" or "文档" or "演示" or "表格" => "doc",
        "pdf" => "pdf",
        "image" or "img" or "图片" or "图像" => "image",
        "screenshot" or "截图" => "screenshot",
        "video" or "视频" => "video",
        "audio" or "音频" or "录音" => "audio",
        "code" or "代码" => "code",
        _ => null
    };
}
