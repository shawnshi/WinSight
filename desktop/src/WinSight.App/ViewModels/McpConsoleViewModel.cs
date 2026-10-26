using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinSight.Core;

namespace WinSight.App.ViewModels;

/// <summary>
/// Read-only view of the MCP bridge: how many maintenance requests are waiting, what recently happened, and
/// which transports exist. Draining is the one action, and it goes through the same backend call as Settings.
/// </summary>
public sealed class McpConsoleViewModel : INotifyPropertyChanged
{
    private readonly IDesktopBackend backend;
    private string summary = "Loading the MCP bridge state…";
    private IReadOnlyList<string> recent = Array.Empty<string>();
    private IReadOnlyList<string> notes = Array.Empty<string>();
    private string actionResult = "";
    private bool busy;

    public McpConsoleViewModel(IDesktopBackend backend) => this.backend = backend;

    public string Summary => summary;
    public IReadOnlyList<string> Recent => recent;
    public IReadOnlyList<string> Notes => notes;
    public string ActionResult => actionResult;
    public bool CanDrain => !busy;

    public async Task RefreshAsync()
    {
        try
        {
            var status = await backend.GetMcpConsoleStatusAsync();
            if (status is null) { summary = "The MCP bridge state is unavailable in this build."; recent = Array.Empty<string>(); notes = Array.Empty<string>(); }
            else
            {
                summary = $"Pending {status.Pending} · claimed {status.Claimed} · completed {status.Completed} · HTTP {status.HttpState}";
                recent = status.Recent.Count == 0 ? new[] { "No completed requests yet." } : status.Recent;
                notes = status.Notes;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            summary = "Reading the MCP state failed: " + error.Message;
        }
        Raise(nameof(Summary)); Raise(nameof(Recent)); Raise(nameof(Notes));
    }

    public async Task DrainAsync()
    {
        if (busy) return;
        busy = true; Raise(nameof(CanDrain));
        try
        {
            var outcomes = await backend.DrainMaintenanceJobsAsync();
            actionResult = outcomes.Count == 0 ? "No pending requests." : string.Join("; ", outcomes);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            actionResult = "Drain failed: " + error.Message;
        }
        finally { busy = false; Raise(nameof(CanDrain)); Raise(nameof(ActionResult)); }
        await RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
