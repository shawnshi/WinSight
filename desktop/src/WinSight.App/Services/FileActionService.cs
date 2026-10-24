using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace WinSight.App.Services;

public sealed record ActionResult(bool Success, string? Message);

public static class FileActionService
{
    public static bool IsSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase)) return false;
        return File.Exists(path) || Directory.Exists(path);
    }

    public static async Task<ActionResult> ExecutePlanAsync(ActionPlan plan)
    {
        if (!plan.IsAuthorized)
            return new ActionResult(false, plan.DenialReason ?? "Action denied: file is not authorized.");

        try
        {
            switch (plan.ActionType)
            {
                case ActionPlanType.LaunchProcess:
                    var psi = new ProcessStartInfo
                    {
                        FileName = plan.Executable,
                        UseShellExecute = false
                    };
                    foreach (var arg in plan.Arguments)
                    {
                        psi.ArgumentList.Add(arg);
                    }
                    Process.Start(psi);
                    return new ActionResult(true, plan.NoticeMessage);

                case ActionPlanType.ShellExecute:
                    var shellPsi = new ProcessStartInfo
                    {
                        FileName = plan.Executable,
                        UseShellExecute = true
                    };
                    Process.Start(shellPsi);
                    return new ActionResult(true, plan.NoticeMessage);

                case ActionPlanType.RevealInExplorer:
                    var revealPsi = new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = plan.Arguments.Count > 0 ? plan.Arguments[0] : "",
                        UseShellExecute = false
                    };
                    Process.Start(revealPsi);
                    return new ActionResult(true, null);

                case ActionPlanType.CopyClipboard:
                    string path = plan.Arguments.Count > 0 ? plan.Arguments[0] : "";
                    if (!string.IsNullOrEmpty(path))
                    {
                        var dp = new DataPackage();
                        dp.RequestedOperation = DataPackageOperation.Copy;
                        dp.SetText(path);
                        if (File.Exists(path))
                        {
                            var file = await StorageFile.GetFileFromPathAsync(path);
                            dp.SetStorageItems(new Windows.Storage.IStorageItem[] { file });
                        }
                        Clipboard.SetContent(dp);
                    }
                    return new ActionResult(true, plan.NoticeMessage);

                default:
                    return new ActionResult(false, "Unknown action type.");
            }
        }
        catch (Exception ex)
        {
            return new ActionResult(false, $"Failed to execute action: {ex.Message}");
        }
    }
}
