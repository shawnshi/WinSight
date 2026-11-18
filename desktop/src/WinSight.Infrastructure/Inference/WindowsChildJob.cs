using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WinSight.Infrastructure.Inference;

/// <summary>Windows closes this non-inheritable job on parent death, including forced termination.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsChildJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000u;
    private const uint JobObjectLimitPriorityClass = 0x20u;
    private const uint NormalPriorityClass = 0x20u;
    private const uint BelowNormalPriorityClass = 0x4000u;

    private readonly SafeFileHandle handle;
    private readonly Process process;
    private readonly bool managePriority;
    private readonly object gate = new();
    private bool isBoosted;
    private bool disposed;

    private WindowsChildJob(SafeFileHandle handle, Process process, bool managePriority)
    {
        this.handle = handle;
        this.process = process;
        this.managePriority = managePriority;
    }

    /// <param name="lowPriority">
    /// Run the child at BELOW_NORMAL. Embedding and OCR are long CPU-bound jobs; at normal priority they compete with the
    /// user's foreground applications. EcoQoS is deliberately not used: the same host answers interactive queries, and
    /// EcoQoS would push it onto efficiency cores on hybrid CPUs.
    /// </param>
    internal static WindowsChildJob Attach(Process process, bool lowPriority = false)
    {
        // Windows SDK jobapi2.h/winnt.h: ExtendedLimitInformation=9, KILL_ON_JOB_CLOSE=0x2000.
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            handle.Dispose();
            throw error;
        }
        try
        {
            // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000, JOB_OBJECT_LIMIT_PRIORITY_CLASS = 0x20, BELOW_NORMAL_PRIORITY_CLASS = 0x4000.
            var limits = new ExtendedLimits
            {
                Basic = new BasicLimits
                {
                    LimitFlags = lowPriority ? JobObjectLimitKillOnJobClose | JobObjectLimitPriorityClass : JobObjectLimitKillOnJobClose,
                    PriorityClass = lowPriority ? BelowNormalPriorityClass : 0u
                }
            };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot configure inference child job.");
            if (!AssignProcessToJobObject(handle, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot bind inference host lifetime to parent.");
            return new WindowsChildJob(handle, process, lowPriority);
        }
        catch { handle.Dispose(); throw; }
    }

    internal void SetInteractiveBoost(bool boost)
    {
        if (!managePriority) return;
        lock (gate)
        {
            if (disposed || isBoosted == boost || handle.IsInvalid || handle.IsClosed) return;
            var limits = new ExtendedLimits
            {
                Basic = new BasicLimits
                {
                    LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitPriorityClass,
                    PriorityClass = boost ? NormalPriorityClass : BelowNormalPriorityClass
                }
            };
            try
            {
                if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                    return;
                process.PriorityClass = boost ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;
                isBoosted = boost;
            }
            catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or Win32Exception or NotSupportedException) { }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        internal ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
