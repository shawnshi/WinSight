using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using WinSight.Infrastructure.Extraction;

namespace WinSight.Infrastructure.Media;

internal sealed class MediaTaskFiles
{
    private SafeFile? source, host, workspace, task, request, output, artifact;
    private string? requestIdentity, taskIdentity, outputIdentity, artifactIdentity;
    internal string TaskDirectory { get; private set; } = string.Empty;
    internal string RequestPath => Path.Combine(TaskDirectory, "request.json");
    internal string OutputDirectory => Path.Combine(TaskDirectory, "output");
    internal string ArtifactPath { get; private set; } = string.Empty;
    internal FileStream ArtifactStream => artifact!.Stream!;

    internal static MediaTaskFiles Create(string hostPath, string workspacePath, NativeMediaProcessRequest input) =>
        CreateOwned(hostPath, workspacePath, input.SourcePath, files =>
        {
            files.ArtifactPath = Path.Combine(files.OutputDirectory, input.Kind == "audio" ? "audio.wav" : "frame.png");
            return JsonSerializer.SerializeToUtf8Bytes(new RequestDocument(input.SourcePath,
                files.OutputDirectory, input.Kind, input.StartSeconds, input.EndSeconds, input.MaxEdge),
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
        });

    internal static MediaTaskFiles CreateInspect(string hostPath, string workspacePath, string sourcePath, string kind) =>
        CreateOwned(hostPath, workspacePath, sourcePath, _ =>
            JsonSerializer.SerializeToUtf8Bytes(new { inputPath = sourcePath, kind }));

    private static MediaTaskFiles CreateOwned(string hostPath, string workspacePath, string sourcePath,
        Func<MediaTaskFiles, byte[]> createPayload)
    {
        var files = new MediaTaskFiles();
        try
        {
            // Reclassify immediately before metadata I/O, not only at client construction.
            LocalPath(sourcePath);
            LocalPath(hostPath);
            LocalPath(workspacePath);
            files.source = SafeFile.Open(sourcePath);
            if (files.source.Stream!.Length is <= 0 or > 32L * 1024 * 1024)
                throw new IOException("Native media source must be nonempty and at most 32 MiB.");
            files.host = SafeFile.Open(hostPath);
            files.workspace = SafeFile.Open(workspacePath, directory: true);
            var path = Path.Combine(workspacePath, "media-" + Guid.NewGuid().ToString("N"));
            if (!CreateDirectoryW(path, IntPtr.Zero)) throw Error("Create exclusive media task directory");
            files.TaskDirectory = path;
            files.task = SafeFile.Open(path, directory: true);
            files.taskIdentity = files.task.Identity;
            var payload = createPayload(files);
            if (payload.Length > 65536) throw new ArgumentException("Native request exceeds 64 KiB.");
            using (var writer = new FileStream(files.RequestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                writer.Write(payload);
            files.request = SafeFile.Open(files.RequestPath);
            files.requestIdentity = files.request.Identity;
            return files;
        }
        catch (Exception ex)
        {
            try { files.Cleanup(); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                MediaRetention.Keep(files);
                throw new IOException($"Media preparation failed; cleanup retained task {files.TaskDirectory}.", new AggregateException(ex, cleanup));
            }
            throw;
        }
    }

    internal void PinArtifact(string filename)
    {
        if (output is not null || artifact is not null) throw new InvalidOperationException("Artifact already pinned.");
        output = SafeFile.Open(OutputDirectory, directory: true);
        outputIdentity = output.Identity;
        ArtifactPath = Path.Combine(OutputDirectory, filename);
        artifact = SafeFile.Open(ArtifactPath);
        artifactIdentity = artifact.Identity;
    }

    internal void Cleanup()
    {
        try
        {
            // On failed decodes only these N1-owned names may be reclaimed. Unknown content keeps the directory nonempty.
            if (TaskDirectory.Length != 0)
            {
                if (task is null) throw new IOException("Task directory was created but could not be pinned; retained without deletion.");
                if (ArtifactPath.Length != 0 && output is null && Exists(OutputDirectory))
                {
                    output = SafeFile.Open(OutputDirectory, directory: true);
                    outputIdentity = output.Identity;
                }
                if (output is not null)
                {
                    if (artifact is null && Exists(ArtifactPath))
                    {
                        artifact = SafeFile.Open(ArtifactPath);
                        artifactIdentity = artifact.Identity;
                    }
                    if (artifact is not null)
                    {
                        artifact.Dispose(); artifact = null;
                        DeleteOwned(ArtifactPath, artifactIdentity!, directory: false);
                        artifactIdentity = null;
                    }
                    output.Dispose(); output = null;
                    DeleteOwned(OutputDirectory, outputIdentity!, directory: true);
                    outputIdentity = null;
                }
                if (request is not null)
                {
                    request.Dispose(); request = null;
                    DeleteOwned(RequestPath, requestIdentity!, directory: false);
                    requestIdentity = null;
                }
                else if (Exists(RequestPath))
                    throw new IOException("Unidentified request file retained without deletion.");
                task.Dispose(); task = null;
                DeleteOwned(TaskDirectory, taskIdentity!, directory: true);
                taskIdentity = null;
            }
            workspace?.Dispose(); workspace = null;
            host?.Dispose(); host = null;
            source?.Dispose(); source = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MediaRetention.Keep(this);
            throw new IOException($"Media task cleanup failed; remaining protections retained: {TaskDirectory}", ex);
        }
    }

    internal static string LocalPath(string path) =>
        LocalPath(path, root => (DriveType)GetDriveTypeW(root));

    internal static string LocalPath(string path, Func<string, DriveType> classifyDrive)
    {
        ArgumentNullException.ThrowIfNull(classifyDrive);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native media processes require Windows.");
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] != '\\' || path.Contains('/') || path.AsSpan(2).Contains(':') ||
            path.Contains('"') || path.Contains('\0') || path.Split('\\').Skip(1).Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new ArgumentException("An absolute local drive path without aliases or device syntax is required.", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var type = classifyDrive(full[..3]);
        if (type is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram))
            throw new IOException($"Media paths require a local drive; classified drive type: {type}.");
        return full;
    }

    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void DeleteOwned(string path, string identity, bool directory)
    {
        // Delete by a validated no-follow HANDLE, not a pathname after releasing a leaf pin.
        // Other SafeFile pins keep every parent alive throughout this transition.
        using var handle = CreateFileW(@"\\?\" + path, 0x10080, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw Error("Open owned leaf for deletion");
        if (!GetFileInformationByHandle(handle, out var info)) throw Error("Identify cleanup leaf");
        var actualIdentity = $"{info.VolumeSerial:X8}:{info.FileIndexHigh:X8}:{info.FileIndexLow:X8}";
        if (actualIdentity != identity || FilePolicy.Unsafe((FileAttributes)info.Attributes) ||
            ((info.Attributes & 16) != 0) != directory)
            throw new IOException("Cleanup leaf identity or attributes changed; refusing deletion.");
        var name = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 0);
        if (length == 0 || length >= name.Capacity ||
            !string.Equals(name.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cleanup leaf resolved outside its owned path.");
        var disposition = new Disposition { Delete = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) throw Error("Delete owned file or empty directory");
    }

    private static IOException Error(string operation) => new(operation, new Win32Exception(Marshal.GetLastWin32Error()));
    private sealed record RequestDocument(string InputPath, string OutputDirectory, string Kind,
        double StartSeconds, double? EndSeconds, int MaxEdge);
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { internal byte Delete; }
    [StructLayout(LayoutKind.Sequential)] private struct Identity
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        internal uint VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Identity info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref Disposition disposition, uint size);
}
