using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace WinSight.App.Services;

public sealed record ActionResult(bool Success, string? Message);

public static class FileActionService
{
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
                    var arg0 = plan.Arguments.Count > 0 ? plan.Arguments[0] : "";
                    if (arg0.StartsWith("/select,\"", StringComparison.OrdinalIgnoreCase) && arg0.EndsWith('"'))
                    {
                        var rawTarget = arg0[9..^1];
                        if (NativeMethods.RevealInExplorerNative(rawTarget))
                            return new ActionResult(true, null);
                    }
                    var revealPsi = new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = arg0,
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
