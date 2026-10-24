using System.Security.Cryptography;

namespace WinSight.Shared;

/// <summary>Linked source/data only: never loads RapidOCR/ORT into the Windows ML process.</summary>
internal static class OcrIdentity
{
    internal const string Engine = "RapidOcrNet";
    internal const string EngineVersion = "4.2.0-winsight.1";
    internal const string ManifestResource = "WinSight.OcrHost.model-manifest.json";
    internal static readonly string ModelProfile = ReadProfile();
    internal static string ExtractionRevision => $"native-image-exif-rapidocr-v3:{EngineVersion}:{ModelProfile}";

    private static string ReadProfile()
    {
        using var input = typeof(OcrIdentity).Assembly.GetManifestResourceStream(ManifestResource)
            ?? throw new InvalidOperationException("The pinned OCR model manifest is missing.");
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}
