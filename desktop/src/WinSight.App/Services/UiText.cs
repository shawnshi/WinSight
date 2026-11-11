namespace WinSight.App.Services;

/// <summary>
/// Single source of user-visible strings for the search surfaces. Keeping them in one table makes the
/// language audit mechanical (nothing user-facing may be hard-coded elsewhere) and is the seam for a later .resw move.
/// </summary>
internal static class UiText
{
    // Search status line
    public const string Ready = "就绪";
    public const string Searching = "正在搜索…";
    public const string NoResult = "未找到相关结果";
    public const string SearchFailed = "搜索失败";
    public const string SemanticUnavailable = "语义检索暂不可用，已显示关键词结果。";
    public const string ActionBlocked = "操作已被授权规则拦截";
    public const string ActionFailed = "操作失败";

    public static string KeywordPhase(int count) => count == 0
        ? "关键词无结果，正在进行语义检索…"
        : $"已找到 {count} 条关键词结果，正在补充语义结果…";

    public static string Completed(int count, double firstMs, double totalMs, bool keywordOnly)
    {
        var text = $"共 {count} 条结果 · 耗时 {totalMs:F0} ms";
        if (firstMs > 0 && totalMs - firstMs > 400) text += $"（首批 {firstMs:F0} ms）";
        return keywordOnly ? text + " · 仅关键词" : text;
    }

    public static string SearchError(string message) => $"搜索出错：{message}";
    public static string NotAuthorized(string? reason) => $"文件已不在授权范围内：{reason}";

    // Preview pane
    public const string PreviewLoading = "正在加载当前来源…";
    public const string PreviewCanceled = "预览已取消。";
    public static string PreviewUnavailable(string message) => $"预览不可用：{message}";
    public static string PreciseOpenFailed(string message) => $"精确定位失败：{message}";
    public static string PreviewCleanupFailed(string message) => $"预览清理失败：{message}";
    public static string PreviewFailed(string message) => $"预览失败：{message}";
    public static string FocusFailed(string message) => $"焦点切换失败：{message}";
    public const string OcrLineFallback = "已索引的文字行";
    public const string NoOcrBoxes = "此来源单元没有已索引的文字行位置。";

    // Backend notices that reach the UI verbatim
    public static string? LocalizeNotice(string? notice) => notice switch
    {
        null or "" => notice,
        _ when notice.StartsWith("Choose an authorized folder", StringComparison.OrdinalIgnoreCase)
            => "请先在“目录管理”中选择要索引的文件夹。",
        _ => notice
    };

    // Indexing control (main window status bar)
    public const string IndexingAllowed = "索引已开启";
    public const string IndexingPausing = "正在暂停索引…";
    public const string IndexingPaused = "索引已暂停";
    public const string IndexingUnknown = "索引状态未知";
    public const string PauseIndexing = "暂停索引";
    public const string ResumeIndexing = "恢复索引";
    public const string RetryIndexingStatus = "重试读取索引状态";
}
