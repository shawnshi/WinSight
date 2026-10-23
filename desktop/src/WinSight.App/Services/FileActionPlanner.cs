using WinSight.Core;

namespace WinSight.App.Services;

public enum ActionPlanType
{
    None,
    ShellExecute,
    LaunchProcess,
    RevealInExplorer,
    CopyClipboard
}

public sealed record ActionPlan(
    bool IsAuthorized,
    string? DenialReason,
    ActionPlanType ActionType,
    string Executable,
    IReadOnlyList<string> Arguments,
    bool UseShellExecute,
    string? NoticeMessage);

public static class FileActionPlanner
{
    private static readonly HashSet<string> AllowedDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".md", ".markdown", ".rtf", ".doc", ".docx", ".xls", ".xlsx",
        ".ppt", ".pptx", ".odt", ".ods", ".odp", ".csv", ".tsv", ".json", ".xml",
        ".html", ".htm", ".log"
    };

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif", ".avif", ".heic"
    };

    private static readonly HashSet<string> AllowedMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".aac", ".mp4", ".mkv", ".avi", ".mov", ".webm"
    };

    private static readonly HashSet<string> BlockedExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".ps1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".scr", ".msi", ".reg", ".dll", ".sys", ".hta", ".lnk",
        ".url", ".website", ".appref-ms", ".pif", ".gadget", ".cpl", ".msc", ".jar"
    };

    public static bool IsSameOrChildOf(string candidate, string parent)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(parent))
            return false;

        string normCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normCandidate, normParent, StringComparison.OrdinalIgnoreCase))
            return true;

        string prefix = normParent + Path.DirectorySeparatorChar;
        return normCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAuthorizedByRoots(
        string path,
        IReadOnlyList<string> chosenRoots,
        IReadOnlyList<string> domainSkips)
    {
        foreach (var root in chosenRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                continue;

            if (IsSameOrChildOf(path, root))
            {
                // NM-UI-06: Root contains the file. A skip excludes the file under this root
                // ONLY if the skip is strictly located inside this chosen root.
                bool excludedInThisRoot = domainSkips.Any(skip =>
                    !string.IsNullOrWhiteSpace(skip) &&
                    IsSameOrChildOf(skip, root) &&
                    !IsSameOrChildOf(root, skip) &&
                    IsSameOrChildOf(path, skip));

                if (!excludedInThisRoot)
                {
                    // This chosen root authorizes the file (nested chosen roots override ancestor skips)
                    return true;
                }
            }
        }

        return false;
    }

    public static (bool Allowed, string? Reason) CheckAuthorization(
        string path,
        string kind,
        AppSettings settings,
        string? dataDir = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return (false, "Path must be non-empty and absolute.");

        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            return (false, "Device paths are not permitted.");

        // NM-UI-06: Data directory and internal index folder are hard-excluded and cannot be overridden
        if (!string.IsNullOrEmpty(dataDir) && IsSameOrChildOf(path, dataDir))
            return (false, "Access to internal preview data directory is prohibited.");

        if (!string.IsNullOrEmpty(settings.IndexDirectory) && IsSameOrChildOf(path, settings.IndexDirectory))
            return (false, "Access to internal index directory is prohibited.");

        bool isCode = string.Equals(kind, "code", StringComparison.OrdinalIgnoreCase);

        if (isCode)
        {
            // Code roots authorization evaluated per chosen root
            if (!IsAuthorizedByRoots(path, settings.CodeFolders, settings.CodeSkipped))
                return (false, "File is not under any authorized code folder, or is within an excluded subfolder.");

            return (true, null);
        }
        else
        {
            // Document / media roots authorization evaluated per chosen root
            if (!IsAuthorizedByRoots(path, settings.Folders, settings.Skipped))
                return (false, "File is not under any authorized document folder, or is within an excluded subfolder.");

            var ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext))
            {
                var normExt = ext.StartsWith('.') ? ext : "." + ext;
                bool isSkipType = settings.SkipTypes.Any(st =>
                {
                    var s = st.StartsWith('.') ? st : "." + st;
                    return string.Equals(normExt, s, StringComparison.OrdinalIgnoreCase);
                });
                if (isSkipType)
                    return (false, $"File extension '{ext}' is excluded by skip types setting.");
            }

            return (true, null);
        }
    }

    public static ActionPlan PlanOpen(
        string path,
        string kind,
        double? location,
        AppSettings settings,
        string? dataDir = null)
    {
        var (allowed, reason) = CheckAuthorization(path, kind, settings, dataDir);
        if (!allowed)
        {
            return new ActionPlan(
                IsAuthorized: false,
                DenialReason: reason,
                ActionType: ActionPlanType.None,
                Executable: string.Empty,
                Arguments: [],
                UseShellExecute: false,
                NoticeMessage: null);
        }

        var ext = Path.GetExtension(path);
        if (BlockedExecutableExtensions.Contains(ext))
        {
            return new ActionPlan(
                IsAuthorized: false,
                DenialReason: $"Direct execution of '{ext}' files is blocked for security.",
                ActionType: ActionPlanType.None,
                Executable: string.Empty,
                Arguments: [],
                UseShellExecute: false,
                NoticeMessage: null);
        }

        bool isCode = string.Equals(kind, "code", StringComparison.OrdinalIgnoreCase);

        if (isCode)
        {
            // NM-UI-07 & NM-UI-10: Code files use explicit text editor with ArgumentList
            int line = location.HasValue && location.Value >= 1 ? (int)location.Value : 1;
            string editor = settings.CodeEditor ?? "default";

            if (string.Equals(editor, "vscode", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(editor, "code", StringComparison.OrdinalIgnoreCase))
            {
                return new ActionPlan(
                    IsAuthorized: true,
                    DenialReason: null,
                    ActionType: ActionPlanType.LaunchProcess,
                    Executable: "code",
                    Arguments: ["-g", $"{path}:{line}"],
                    UseShellExecute: false,
                    NoticeMessage: $"Opening in VS Code at line {line}.");
            }
            if (string.Equals(editor, "cursor", StringComparison.OrdinalIgnoreCase))
            {
                return new ActionPlan(
                    IsAuthorized: true,
                    DenialReason: null,
                    ActionType: ActionPlanType.LaunchProcess,
                    Executable: "cursor",
                    Arguments: ["-g", $"{path}:{line}"],
                    UseShellExecute: false,
                    NoticeMessage: $"Opening in Cursor at line {line}.");
            }

            // Default system notepad / text editor
            return new ActionPlan(
                IsAuthorized: true,
                DenialReason: null,
                ActionType: ActionPlanType.LaunchProcess,
                Executable: "notepad.exe",
                Arguments: [path],
                UseShellExecute: false,
                NoticeMessage: line > 1
                    ? $"Opened in default editor. (Note: line {line} navigation not supported by notepad; line locator displayed for reference.)"
                    : "Opened in default text editor.");
        }

        // Document or Media file: Check allowlist
        bool isAllowedDoc = AllowedDocumentExtensions.Contains(ext);
        bool isAllowedImg = AllowedImageExtensions.Contains(ext);
        bool isAllowedMedia = AllowedMediaExtensions.Contains(ext);

        if (!isAllowedDoc && !isAllowedImg && !isAllowedMedia)
        {
            return new ActionPlan(
                IsAuthorized: false,
                DenialReason: $"File extension '{ext}' is not in the recognized document or media allowlist.",
                ActionType: ActionPlanType.None,
                Executable: string.Empty,
                Arguments: [],
                UseShellExecute: false,
                NoticeMessage: null);
        }

        // NM-UI-10: Location annotation for PDF / media
        string? notice = null;
        if (location.HasValue)
        {
            if (string.Equals(kind, "pdf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                notice = $"Opened PDF. (Note: automatic navigation to Page {(int)location.Value} not supported by default Windows viewer; page locator displayed for reference.)";
            }
            else if (string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase) || string.Equals(kind, "audio", StringComparison.OrdinalIgnoreCase))
            {
                var ts = TimeSpan.FromSeconds(location.Value);
                string timeStr = ts.Hours > 0 ? $"{ts:hh\\:mm\\:ss}" : $"{ts:mm\\:ss}";
                notice = $"Opened media. (Note: automatic seek to {timeStr} not supported by default Windows player; time locator displayed for reference.)";
            }
        }

        return new ActionPlan(
            IsAuthorized: true,
            DenialReason: null,
            ActionType: ActionPlanType.ShellExecute,
            Executable: path,
            Arguments: [],
            UseShellExecute: true,
            NoticeMessage: notice);
    }

    public static ActionPlan PlanReveal(
        string path,
        string kind,
        AppSettings settings,
        string? dataDir = null)
    {
        var (allowed, reason) = CheckAuthorization(path, kind, settings, dataDir);
        if (!allowed)
        {
            return new ActionPlan(
                IsAuthorized: false,
                DenialReason: reason,
                ActionType: ActionPlanType.None,
                Executable: string.Empty,
                Arguments: [],
                UseShellExecute: false,
                NoticeMessage: null);
        }

        return new ActionPlan(
            IsAuthorized: true,
            DenialReason: null,
            ActionType: ActionPlanType.RevealInExplorer,
            Executable: "explorer.exe",
            Arguments: [$"/select,\"{path}\""],
            UseShellExecute: false,
            NoticeMessage: null);
    }

    public static ActionPlan PlanCopy(
        string path,
        string kind,
        AppSettings settings,
        string? dataDir = null)
    {
        var (allowed, reason) = CheckAuthorization(path, kind, settings, dataDir);
        if (!allowed)
        {
            return new ActionPlan(
                IsAuthorized: false,
                DenialReason: reason,
                ActionType: ActionPlanType.None,
                Executable: string.Empty,
                Arguments: [],
                UseShellExecute: false,
                NoticeMessage: null);
        }

        return new ActionPlan(
            IsAuthorized: true,
            DenialReason: null,
            ActionType: ActionPlanType.CopyClipboard,
            Executable: string.Empty,
            Arguments: [path],
            UseShellExecute: false,
            NoticeMessage: "File path copied to clipboard.");
    }
}
