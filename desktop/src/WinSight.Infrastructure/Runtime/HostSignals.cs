using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinSight.Infrastructure.Runtime;

public sealed record SystemPowerInfo(bool IsAcPower, bool IsBatterySaver, int BatteryPercent);
public sealed record SystemMemoryInfo(ulong AvailablePhysicalBytes, double CommitPercent);

public sealed class HostSignals
{
    private readonly Func<SystemPowerInfo>? powerStatusDouble;
    private readonly Func<TimeSpan>? idleTimeDouble;
    private readonly Func<SystemMemoryInfo>? memoryStatusDouble;
    private readonly Action<Exception>? onError;

    public HostSignals(
        Func<SystemPowerInfo>? powerStatus = null,
        Func<TimeSpan>? idleTime = null,
        Func<SystemMemoryInfo>? memoryStatus = null,
        Action<Exception>? onError = null)
    {
        powerStatusDouble = powerStatus;
        idleTimeDouble = idleTime;
        memoryStatusDouble = memoryStatus;
        this.onError = onError;
    }

    public SystemPowerInfo GetPowerInfo()
    {
        if (powerStatusDouble != null)
        {
            try
            {
                return powerStatusDouble();
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                // Fail-closed: assume battery, low power, saver active
                return new SystemPowerInfo(IsAcPower: false, IsBatterySaver: true, BatteryPercent: 0);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return new SystemPowerInfo(IsAcPower: true, IsBatterySaver: false, BatteryPercent: 100);
        }

        try
        {
            if (!GetSystemPowerStatus(out var status))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read system power status.");
                onError?.Invoke(error);
                return new SystemPowerInfo(IsAcPower: false, IsBatterySaver: true, BatteryPercent: 0);
            }

            bool isAc = status.AcLineStatus == 1;
            bool isSaver = status.SystemStatusFlag == 1;
            int percent = status.BatteryLifePercent == 255 ? 100 : (int)status.BatteryLifePercent;
            return new SystemPowerInfo(isAc, isSaver, percent);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return new SystemPowerInfo(IsAcPower: false, IsBatterySaver: true, BatteryPercent: 0);
        }
    }

    public TimeSpan GetIdleTime()
    {
        if (idleTimeDouble != null)
        {
            try
            {
                return idleTimeDouble();
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                return TimeSpan.Zero; // Fail-closed: treat as user active (not idle)
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return TimeSpan.Zero;
        }

        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read last input info.");
                onError?.Invoke(error);
                return TimeSpan.Zero;
            }

            uint currentTick = (uint)Environment.TickCount;
            uint idleTicks = currentTick >= lii.dwTime ? currentTick - lii.dwTime : (uint.MaxValue - lii.dwTime + currentTick);
            return TimeSpan.FromMilliseconds(idleTicks);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return TimeSpan.Zero;
        }
    }

    public SystemMemoryInfo GetMemoryInfo()
    {
        if (memoryStatusDouble != null)
        {
            try
            {
                return memoryStatusDouble();
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                // Fail-closed: report memory pressure
                return new SystemMemoryInfo(AvailablePhysicalBytes: 0, CommitPercent: 100.0);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return new SystemMemoryInfo(AvailablePhysicalBytes: 16UL * 1024 * 1024 * 1024, CommitPercent: 30.0);
        }

        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read memory status.");
                onError?.Invoke(error);
                return new SystemMemoryInfo(AvailablePhysicalBytes: 0, CommitPercent: 100.0);
            }

            double commitPercent = 0.0;
            if (status.ullTotalPageFile > 0)
            {
                ulong committed = status.ullTotalPageFile - status.ullAvailPageFile;
                commitPercent = (double)committed / status.ullTotalPageFile * 100.0;
            }
            return new SystemMemoryInfo(status.ullAvailPhys, commitPercent);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return new SystemMemoryInfo(AvailablePhysicalBytes: 0, CommitPercent: 100.0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
