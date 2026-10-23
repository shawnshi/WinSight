using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinSight.Infrastructure.Extraction;

/// <summary>Pins each path component against replacement while a file or host media path is being read.</summary>
internal sealed class SafeFile : IDisposable
{
    private readonly List<SafeFileHandle> directories = [];
    internal FileStream? Stream { get; private set; }
    internal string Identity { get; private set; } = string.Empty;

    internal static SafeFile Open(string path, bool directory = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Safe native indexing requires Windows.");
        path = FilePolicy.Normalize(path);
        var lease = new SafeFile();
        try
        {
            var parents = new Stack<string>();
            var parent = directory ? path : Path.GetDirectoryName(path);
            while (parent is not null)
            {
                if (parents.Count >= 256) throw new IOException("Safe path exceeds 256 ancestor components.");
                parents.Push(parent);
                var next = Path.GetDirectoryName(parent);
                parent = next == parent ? null : next;
            }
            while (parents.TryPop(out var folder))
            {
                using var metadata = OpenHandle(folder, false);
                Validate(metadata, folder, true);
                var handle = OpenHandle(folder, false, directory: true);
                lease.directories.Add(handle);
                Validate(handle, folder, true);
            }
            if (!directory)
            {
                // Attribute-only no-follow open rejects cloud placeholders before requesting content access.
                using var metadata = OpenHandle(path, false);
                Validate(metadata, path, false);
                var handle = OpenHandle(path, true);
                try
                {
                    Validate(handle, path, false);
                    lease.Identity = ReadIdentity(handle);
                    lease.Stream = new FileStream(handle, FileAccess.Read, 65536, false);
                }
                catch { handle.Dispose(); throw; }
            }
            if (directory) lease.Identity = ReadIdentity(lease.directories[^1]);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static SafeFileHandle OpenHandle(string path, bool read, bool directory = false)
    {
        // NM-BR-04: READ_ATTRIBUTES alone is not a Windows sharing lease; LIST_DIRECTORY participates in delete sharing.
        var nativePath = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        var access = read ? 0x80000000u : directory ? 0x81u : 0x80u;
        var handle = CreateFileW(nativePath, access, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException($"Cannot open safe path ({error}): {path}", new Win32Exception(error));
    }

    private static void Validate(SafeFileHandle handle, string expected, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<AttributeTag>()))
            throw new IOException("Cannot inspect open file attributes.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (FilePolicy.Unsafe((FileAttributes)info.Attributes) || ((info.Attributes & 16) != 0) != directory)
            throw new IOException("Reparse points, cloud placeholders and non-regular files are not read.");
        var name = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 0);
        if (length == 0 || length >= name.Capacity) throw new IOException("Cannot resolve open file path.");
        var actual = name.ToString();
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Open file resolves outside its checked path.");
    }

    private static string ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("Cannot identify open file/root.", new Win32Exception(Marshal.GetLastWin32Error()));
        return $"{info.VolumeSerial:X8}:{info.FileIndexHigh:X8}:{info.FileIndexLow:X8}";
    }

    public void Dispose()
    {
        Stream?.Dispose();
        for (var i = directories.Count - 1; i >= 0; i--) directories[i].Dispose();
        directories.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder name, uint size, uint flags);
}
