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
        }
        catch (Exception ex)
        {
            StatusMessage = $"Status check failed: {ex.Message}";
        }
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
            StatusMessage = "Embedding host online and verified (CPU default).";

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
