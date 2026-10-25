using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinSight.Core;

namespace WinSight.App.ViewModels;

public sealed class StorageModelViewModel : INotifyPropertyChanged
{
    private readonly IDesktopBackend backend;
    private EngineStatus? status;
    private EmbeddingInfo? embeddingInfo;
    private bool isInspecting;
    private bool hasEmbeddingInfo;
    private string? inspectionError;
    private string statusMessage = "Click 'Inspect Model' to probe embedding host.";

    private IndexingQueueStatus? queue;
    public string QueueSummary => queue is null
        ? "Queue status unavailable."
        : $"pending {queue.Pending}, settling {queue.Stabilizing}, ready {queue.Ready}, running {queue.Running}, failed {queue.Failed}, deferred {queue.Deferred}";
    public string QueueHealth => queue is null
        ? "—"
        : $"dropped {queue.Dropped}, watcher overflows {queue.WatcherOverflows}, last reconcile {(queue.LastReconcileUtc is { } at ? at.ToLocalTime().ToString("HH:mm:ss") : "never")}, USN {queue.UsnState}";
    public IReadOnlyList<string> BudgetRows => queue is null
        ? Array.Empty<string>()
        : queue.Budgets.Select(b => $"{b.Class}: extract {b.ExtractConcurrency}, inference {b.InferenceConcurrency} ({b.State})").ToArray();

    public string MigrationNotice =>
        $"Local preview. Active index: {DatabasePath}. Legacy indexes are not migrated.";

    public string DatabasePath => status?.IndexPath ?? "Isolated Preview Database";
    public int FilesCount => status?.Files ?? 0;
    public int DoneCount => status?.Done ?? 0;
    public int PendingCount => status?.Pending ?? 0;
    public int PartialCount => status?.Partial ?? 0;
    public int FailedCount => status?.Failed ?? 0;
    public string? StatusDetail => status?.Detail;

    public bool IsInspecting
    {
        get => isInspecting;
        private set => SetProperty(ref isInspecting, value);
    }

    public bool HasEmbeddingInfo
    {
        get => hasEmbeddingInfo;
        private set => SetProperty(ref hasEmbeddingInfo, value);
    }

    public string? InspectionError
    {
        get => inspectionError;
        private set => SetProperty(ref inspectionError, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string ProfileId => embeddingInfo?.ProfileId ?? "Not inspected";
    public int Dimension => embeddingInfo?.Dimension ?? 768;
    public string RuntimeHost => embeddingInfo?.Runtime ?? "WinSight.InferenceHost (CPU)";
    public string ModelName => embeddingInfo?.Model ?? "nomic-ai/nomic-embed-text-v1.5";
    public string Revision => embeddingInfo?.Revision ?? "N/A";
    public string RequestedProvider => embeddingInfo?.RequestedProvider ?? "CPU (Default)";
    public string ValidationStatus => embeddingInfo?.Validation ?? "Unprobed";
    public IReadOnlyList<string> Capabilities => embeddingInfo?.Capabilities ?? [];
    public string BackendStatusRaw => embeddingInfo?.BackendStatus ?? "";
    public string BackendStatusSummary => SummarizeBackendStatus(embeddingInfo?.BackendStatus);

    public StorageModelViewModel(IDesktopBackend backend)
    {
        this.backend = backend;
    }

    public async Task RefreshStatusAsync()
    {
        try
        {
            status = await backend.GetStatusAsync();
            OnPropertyChanged(nameof(DatabasePath));
            OnPropertyChanged(nameof(MigrationNotice));
            OnPropertyChanged(nameof(FilesCount));
            OnPropertyChanged(nameof(DoneCount));
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(PartialCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(StatusDetail));
            await RefreshQueueStatusAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Status check failed: {ex.Message}";
        }
    }

    // The queue status is a read-only projection of the live coordinator; it never mutates indexing.
    internal async Task RefreshQueueStatusAsync()
    {
        try
        {
            queue = await backend.GetIndexingQueueStatusAsync();
        }
        catch (Exception ex)
        {
            queue = null;
            StatusMessage = $"Queue status unavailable: {ex.Message}";
        }
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(QueueHealth));
        OnPropertyChanged(nameof(BudgetRows));
    }

    // Explicit user action: GetEmbeddingInfo starts the model host and hashes weights.
    // Must NOT be called automatically on page load.
    public async Task InspectModelAsync()
    {
        if (IsInspecting) return;

        IsInspecting = true;
        InspectionError = null;
        StatusMessage = "Starting inference host and checking model contract...";

        try
        {
            embeddingInfo = await backend.GetEmbeddingInfoAsync();
            HasEmbeddingInfo = true;
            StatusMessage = DescribeInspection(embeddingInfo);

            OnPropertyChanged(nameof(ProfileId));
            OnPropertyChanged(nameof(Dimension));
            OnPropertyChanged(nameof(RuntimeHost));
            OnPropertyChanged(nameof(ModelName));
            OnPropertyChanged(nameof(Revision));
            OnPropertyChanged(nameof(RequestedProvider));
            OnPropertyChanged(nameof(ValidationStatus));
            OnPropertyChanged(nameof(Capabilities));
            OnPropertyChanged(nameof(BackendStatusRaw));
            OnPropertyChanged(nameof(BackendStatusSummary));
        }
        catch (Exception ex)
        {
            HasEmbeddingInfo = false;
            InspectionError = ex.Message;
            StatusMessage = $"Inspection error: {ex.Message}";
        }
        finally
        {
            IsInspecting = false;
        }
    }

    public static string SummarizeBackendStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "未提供状态";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return Truncate(raw, 200);
            }

            var parts = new List<string>();

            if (root.TryGetProperty("epoch", out var epochProp) && epochProp.TryGetInt64(out var epoch))
            {
                parts.Add($"Epoch: {epoch}");
            }

            if (root.TryGetProperty("device", out var deviceProp) && deviceProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                parts.Add($"Device: {deviceProp.GetString()}");
            }

            if (root.TryGetProperty("providers", out var providersProp) && providersProp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var providerList = new List<string>();
                foreach (var item in providersProp.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var state = item.TryGetProperty("state", out var s) ? s.GetString() : null;
                        if (name != null)
                        {
                            providerList.Add(state != null ? $"{name} ({state})" : name);
                        }
                    }
                    else if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        providerList.Add(item.GetString()!);
                    }
                }
                if (providerList.Count > 0)
                {
                    parts.Add($"Providers: {string.Join(", ", providerList)}");
                }
            }

            if (root.TryGetProperty("graphs", out var graphsProp) && graphsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var graphList = new List<string>();
                foreach (var item in graphsProp.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var g = item.TryGetProperty("graph", out var gp) ? gp.GetString() : null;
                        var candidate = item.TryGetProperty("eligible_candidate", out var ec) ? ec.GetString() : null;
                        var last = item.TryGetProperty("last_execution", out var le) ? le.GetString() : null;
                        if (g != null)
                        {
                            graphList.Add($"{g}: {candidate ?? last ?? "CPU"}");
                        }
                    }
                }
                if (graphList.Count > 0)
                {
                    parts.Add($"Graphs: {string.Join("; ", graphList)}");
                }
            }

            if (parts.Count > 0)
            {
                return string.Join(" | ", parts);
            }

            var props = root.EnumerateObject().Take(4)
                .Select(p => $"{p.Name}: {Truncate(p.Value.ToString(), 40)}");
            var summary = string.Join(", ", props);
            return string.IsNullOrEmpty(summary) ? Truncate(raw, 200) : summary;
        }
        catch
        {
            return Truncate(raw, 200);
        }
    }

    // The message must describe what the host actually reported, not a preferred default.
    internal static string DescribeInspection(EmbeddingInfo? info)
    {
        if (info is null) return "Embedding host did not report status.";
        var provider = info.RequestedProvider;
        var preference = provider switch
        {
            "CPUExecutionProvider" => "cpu only",
            "device-auto-with-real-model-gates" => "auto: discrete GPU, then NPU, then integrated GPU, then CPU",
            "device-discrete-gpu-with-real-model-gates" => "discrete GPU only",
            "device-npu-with-real-model-gates" => "NPU only",
            "device-gpu-with-real-model-gates" => "any GPU",
            _ => provider
        };
        var graphs = SummarizeGraphRouting(info.BackendStatus);
        return graphs.Length == 0
            ? $"Embedding host online. Device policy: {preference}. Acceleration qualifies on the first real embedding run."
            : $"Embedding host online. Device policy: {preference}. Graph routing: {graphs}";
    }

    internal static string SummarizeGraphRouting(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return string.Empty;
            var rejected = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("providers", out var providers) && providers.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in providers.EnumerateArray())
                {
                    if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var state = item.TryGetProperty("state", out var s) ? s.GetString() : null;
                    if (name is null || state is null) continue;
                    var slash = name.IndexOf('/', StringComparison.Ordinal);
                    if (slash > 0 && state.StartsWith("rejected:", StringComparison.Ordinal))
                        rejected[name[..slash]] = state;
                }
            }
            if (!root.TryGetProperty("graphs", out var graphs) || graphs.ValueKind != System.Text.Json.JsonValueKind.Array) return string.Empty;
            var parts = new List<string>();
            foreach (var item in graphs.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var graph = item.TryGetProperty("graph", out var g) ? g.GetString() : null;
                if (graph is null) continue;
                var eligible = item.TryGetProperty("eligible_candidate", out var ec) ? ec.GetString() : null;
                var candidates = item.TryGetProperty("candidate_devices", out var cd) && cd.ValueKind == System.Text.Json.JsonValueKind.Array ? cd.GetArrayLength() : 0;
                if (eligible is not null && !eligible.StartsWith("CPUExecutionProvider", StringComparison.Ordinal))
                    parts.Add($"{graph}: {eligible}");
                else if (rejected.TryGetValue(graph, out var reason))
                    parts.Add($"{graph}: CPU, accelerator rejected ({reason})");
                else if (candidates > 0)
                    parts.Add($"{graph}: CPU, {candidates} candidate(s) await real-model qualification");
                else
                    parts.Add($"{graph}: CPU, no applicable device");
            }
            return string.Join("; ", parts);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "...";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
