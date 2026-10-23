using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinSight.MediaProcess.Tests;

internal static class PinAttacks
{
    internal static void WriteDenied(string path)
    {
        Denied(() => { using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); file.WriteByte(0); }, "write " + path);
    }

    internal static void MoveDenied(string path)
    {
        Denied(() => Directory.Move(path, path + ".moved"), "move " + path);
    }

    internal static void VerifyJunctionProbe(string taskDirectory)
    {
        var control = Path.Combine(taskDirectory, "unpinned-control");
        var target = Path.Combine(taskDirectory, "junction-target");
        Directory.CreateDirectory(control); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "target.keep"), "target");
        SetJunction(control, target);
        Check.That((File.GetAttributes(control) & FileAttributes.ReparsePoint) != 0 &&
            File.ReadAllText(Path.Combine(control, "target.keep")) == "target", "Unpinned empty control must accept junction; otherwise attack capability is unavailable.");
        Directory.Delete(control);
        Check.That(File.Exists(Path.Combine(target, "target.keep")), "Removing only control link preserves target.");
    }

    internal static void JunctionDenied(string directory, string taskTarget)
    {
        Check.That(Directory.Exists(directory) && !Directory.Exists(directory + ".before-junction"), "Attack preconditions.");
        // FSCTL rejects nonempty directories even without pins (Win32 145). Only a refused
        // replacement precondition proves protection; never count that unrelated error as a pass.
        try { Directory.Move(directory, directory + ".before-junction"); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        Directory.CreateDirectory(directory);
        SetJunction(directory, taskTarget);
        throw new InvalidOperationException("Pinned ancestor was replaced by a task-owned junction.");
    }

    internal static void CreateArtifactJunction(string artifact, string taskTarget)
    {
        Directory.CreateDirectory(artifact); Directory.CreateDirectory(taskTarget);
        SetJunction(artifact, taskTarget);
    }

    private static void Denied(Action action, string label)
    {
        try { action(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("OS did not deny pinned " + label);
    }

    // The target is always a new task-owned empty directory; no privilege elevation or shell mklink.
    private static void SetJunction(string directory, string target)
    {
        using var handle = CreateFile(directory, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Junction write handle denied.", new Win32Exception(Marshal.GetLastWin32Error()));
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xa0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes(checked((ushort)(buffer.Length - 8))).CopyTo(buffer, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x000900a4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Junction mutation denied.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int bytes, IntPtr overlapped);
}
