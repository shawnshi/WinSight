using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinSight.Infrastructure.Indexing;

public sealed record UsnCheckpoint(string VolumeSerial, string JournalId, string NextUsn, string ReconcileUtc);

public interface IUsnCheckpointRepository
{
    Task SaveUsnCheckpointAsync(string volumeSerial, string journalId, string nextUsn, DateTimeOffset reconcileUtc, CancellationToken cancellationToken = default);
    Task<(string JournalId, string NextUsn, DateTimeOffset ReconcileUtc)?> GetUsnCheckpointAsync(string volumeSerial, CancellationToken cancellationToken = default);
}

public sealed record UsnJournalInfo(ulong JournalId, long LowestValidUsn, long NextUsn);

public sealed record UsnChangeRecord(
    long Usn,
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    uint Reason,
    string FileName,
    DateTimeOffset Timestamp);

public interface IUsnJournalReader
{
    bool IsNtfsVolume(string volumePath, out string volumeSerial);
    (bool Success, UsnJournalInfo? Info, string? ErrorReason) QueryJournal(string volumePath);
    (bool Success, IReadOnlyList<UsnChangeRecord> Records, long NextUsn, string? ErrorReason) ReadIncrements(
        string volumePath,
        ulong journalId,
        long startUsn,
        int maxRecords = 5000);
    string? ResolvePath(string volumePath, ulong fileReferenceNumber, ulong parentFileReferenceNumber, string fileName);
}

public class UsnJournalReader : IUsnJournalReader
{
    public const uint USN_REASON_DATA_OVERWRITE = 0x00000001;
    public const uint USN_REASON_DATA_EXTEND = 0x00000002;
    public const uint USN_REASON_DATA_TRUNCATION = 0x00000004;
    public const uint USN_REASON_FILE_CREATE = 0x00000100;
    public const uint USN_REASON_FILE_DELETE = 0x00000200;
    public const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    public const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    public const uint USN_REASON_CLOSE = 0x80000000;

    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;

    [StructLayout(LayoutKind.Sequential)]
    public struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long LowestValidUsn;
        public long NextUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public uint Type;
        public ulong FileId;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle hVolumeHint,
        ref FILE_ID_DESCRIPTOR lpFileId,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwFlagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    private readonly Func<string, SafeFileHandle>? handleOpener;
    private readonly ConcurrentDictionary<ulong, string> parentPathCache = new();

    public UsnJournalReader(Func<string, SafeFileHandle>? handleOpener = null)
    {
        this.handleOpener = handleOpener;
    }

    public static string NormalizeVolumePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "C:";
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return "C:";
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string GetVolumeDevicePath(string volumePath)
    {
        var norm = NormalizeVolumePath(volumePath);
        return $@"\\.\{norm}";
    }

    private SafeFileHandle OpenVolume(string volumePath, out int win32Error)
    {
        win32Error = 0;
        if (handleOpener != null)
        {
            var custom = handleOpener(volumePath);
            if (custom.IsInvalid)
            {
                win32Error = Marshal.GetLastWin32Error();
                if (win32Error == 0) win32Error = ERROR_ACCESS_DENIED;
            }
            return custom;
        }

        if (!OperatingSystem.IsWindows())
        {
            win32Error = 1; // ERROR_INVALID_FUNCTION
            return new SafeFileHandle(IntPtr.Zero, true);
        }

        var devicePath = GetVolumeDevicePath(volumePath);
        // Minimum read-only rights: FILE_READ_DATA (1) | FILE_READ_ATTRIBUTES (0x80)
        uint access = 0x00000001 | 0x00000080;
        uint share = 1 | 2 | 4; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
        var handle = CreateFileW(devicePath, access, share, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            win32Error = Marshal.GetLastWin32Error();
        }
        return handle;
    }

    public virtual bool IsNtfsVolume(string volumePath, out string volumeSerial)
    {
        volumeSerial = string.Empty;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var root = Path.GetPathRoot(volumePath);
            if (string.IsNullOrEmpty(root)) root = "C:\\";
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                root += Path.DirectorySeparatorChar;

            var fsBuffer = new StringBuilder(64);
            if (GetVolumeInformationW(root, null, 0, out uint serial, out _, out _, fsBuffer, (uint)fsBuffer.Capacity))
            {
                volumeSerial = serial.ToString("X8");
                return string.Equals(fsBuffer.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
    }

    public virtual (bool Success, UsnJournalInfo? Info, string? ErrorReason) QueryJournal(string volumePath)
    {
        using var handle = OpenVolume(volumePath, out int openErr);
        if (handle.IsInvalid)
        {
            return (false, null, MapWin32Error(openErr));
        }

        int size = 1024;
        IntPtr pOut = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = DeviceIoControl(handle, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, pOut, (uint)size, out uint returned, IntPtr.Zero);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                return (false, null, MapWin32Error(err));
            }

            var data = Marshal.PtrToStructure<USN_JOURNAL_DATA_V0>(pOut);
            return (true, new UsnJournalInfo(data.UsnJournalID, data.LowestValidUsn, data.NextUsn), null);
        }
        finally
        {
            Marshal.FreeHGlobal(pOut);
        }
    }

    public virtual (bool Success, IReadOnlyList<UsnChangeRecord> Records, long NextUsn, string? ErrorReason) ReadIncrements(
        string volumePath,
        ulong journalId,
        long startUsn,
        int maxRecords = 5000)
    {
        using var handle = OpenVolume(volumePath, out int openErr);
        if (handle.IsInvalid)
        {
            return (false, Array.Empty<UsnChangeRecord>(), startUsn, MapWin32Error(openErr));
        }

        var results = new List<UsnChangeRecord>();
        long currentUsn = startUsn;
        int bufferSize = 65536;
        IntPtr pOut = Marshal.AllocHGlobal(bufferSize);
        int inputSize = Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>();
        IntPtr pIn = Marshal.AllocHGlobal(inputSize);

        try
        {
            while (results.Count < maxRecords)
            {
                var input = new READ_USN_JOURNAL_DATA_V0
                {
                    StartUsn = currentUsn,
                    ReasonMask = 0xFFFFFFFF,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = journalId
                };
                Marshal.StructureToPtr(input, pIn, false);

                bool ok = DeviceIoControl(handle, FSCTL_READ_USN_JOURNAL, pIn, (uint)inputSize, pOut, (uint)bufferSize, out uint bytesReturned, IntPtr.Zero);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    return (false, results, currentUsn, MapWin32Error(err));
                }

                if (bytesReturned < 8)
                {
                    break;
                }

                long nextUsn = Marshal.ReadInt64(pOut);
                if (bytesReturned == 8)
                {
                    currentUsn = nextUsn;
                    break;
                }

                int offset = 8;
                int batchCount = 0;

                while (offset + 60 <= bytesReturned && results.Count < maxRecords)
                {
                    uint recordLength = (uint)Marshal.ReadInt32(pOut + offset);
                    if (recordLength == 0 || offset + recordLength > bytesReturned)
                    {
                        break;
                    }

                    ushort major = (ushort)Marshal.ReadInt16(pOut + offset + 4);
                    if (major == 2)
                    {
                        ulong frn = (ulong)Marshal.ReadInt64(pOut + offset + 8);
                        ulong pfrn = (ulong)Marshal.ReadInt64(pOut + offset + 16);
                        long usn = Marshal.ReadInt64(pOut + offset + 24);
                        long timeStamp = Marshal.ReadInt64(pOut + offset + 32);
                        uint reason = (uint)Marshal.ReadInt32(pOut + offset + 40);
                        ushort fnLen = (ushort)Marshal.ReadInt16(pOut + offset + 56);
                        ushort fnOff = (ushort)Marshal.ReadInt16(pOut + offset + 58);

                        string fileName = string.Empty;
                        if (fnOff > 0 && fnLen > 0 && offset + fnOff + fnLen <= bytesReturned)
                        {
                            fileName = Marshal.PtrToStringUni(pOut + offset + fnOff, fnLen / 2) ?? string.Empty;
                        }

                        DateTimeOffset ts;
                        try { ts = DateTimeOffset.FromFileTime(timeStamp); }
                        catch { ts = DateTimeOffset.UtcNow; }

                        results.Add(new UsnChangeRecord(usn, frn, pfrn, reason, fileName, ts));
                        batchCount++;
                    }

                    offset += (int)recordLength;
                }

                if (nextUsn <= currentUsn || batchCount == 0)
                {
                    currentUsn = nextUsn;
                    break;
                }

                currentUsn = nextUsn;
            }

            return (true, results, currentUsn, null);
        }
        finally
        {
            Marshal.FreeHGlobal(pIn);
            Marshal.FreeHGlobal(pOut);
        }
    }

    public virtual string? ResolvePath(string volumePath, ulong fileReferenceNumber, ulong parentFileReferenceNumber, string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;

        if (parentPathCache.TryGetValue(parentFileReferenceNumber, out var cachedParent))
        {
            return Path.Combine(cachedParent, fileName);
        }

        if (!OperatingSystem.IsWindows()) return null;

        using var handle = OpenVolume(volumePath, out _);
        if (handle.IsInvalid) return null;

        var desc = new FILE_ID_DESCRIPTOR
        {
            dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
            Type = 0, // FileIdType
            FileId = parentFileReferenceNumber,
            FileIdHigh = 0
        };

        // Open directory with FILE_FLAG_BACKUP_SEMANTICS (0x02000000)
        using var hDir = OpenFileById(handle, ref desc, 0x00000080 /* FILE_READ_ATTRIBUTES */, 1 | 2 | 4, IntPtr.Zero, 0x02000000);
        if (!hDir.IsInvalid)
        {
            var sb = new StringBuilder(1024);
            uint len = GetFinalPathNameByHandleW(hDir, sb, 1024, 0);
            if (len > 0)
            {
                var dirPath = sb.ToString();
                if (dirPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    dirPath = @"\\" + dirPath.Substring(8);
                }
                else if (dirPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                {
                    dirPath = dirPath.Substring(4);
                }

                parentPathCache[parentFileReferenceNumber] = dirPath;
                return Path.Combine(dirPath, fileName);
            }
        }

        return null;
    }

    public static string MapWin32Error(int errorCode)
    {
        return errorCode switch
        {
            ERROR_ACCESS_DENIED => "access-denied",
            ERROR_JOURNAL_DELETE_IN_PROGRESS => "journal-delete-in-progress",
            ERROR_JOURNAL_NOT_ACTIVE => "journal-not-active",
            ERROR_INVALID_PARAMETER => "invalid-parameter",
            1 => "invalid-function",
            _ => $"win32-{errorCode}"
        };
    }
}
