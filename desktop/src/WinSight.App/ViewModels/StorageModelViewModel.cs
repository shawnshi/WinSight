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
        "Migration Preview: Running on isolated preview pipeline (%LOCALAPPDATA%\\WinSight\\NativePreview). Legacy production pipeline unchanged.";

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
