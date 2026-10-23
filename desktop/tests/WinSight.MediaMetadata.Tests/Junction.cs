using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinSight.MediaMetadata.Tests;

internal static class Junction
{
    internal static void Create(string link, string target)
    {
        Verify.That(!Directory.Exists(link) && Directory.Exists(target), "Junction must use a new link and task-owned target");
        Directory.CreateDirectory(link);
        using var handle = CreateFile(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Task junction handle failed", new Win32Exception(Marshal.GetLastWin32Error()));
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target); var print = Encoding.Unicode.GetBytes(target);
        var data = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xa0000003u).CopyTo(data, 0); BitConverter.GetBytes(checked((ushort)(data.Length - 8))).CopyTo(data, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(data, 10); BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(data, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(data, 14); substitute.CopyTo(data, 16); print.CopyTo(data, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x900a4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new IOException("Task junction failed", new Win32Exception(Marshal.GetLastWin32Error()));
        Verify.That((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Junction control unavailable (not a passing rejection)");
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int length, IntPtr output, int outputLength, out int bytes, IntPtr overlapped);
}
