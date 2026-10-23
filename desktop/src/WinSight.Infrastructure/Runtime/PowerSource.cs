using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinSight.Infrastructure.Runtime;

internal static class PowerSource
{
    internal static bool IsOnAcPower()
    {
        if (!GetSystemPowerStatus(out var status)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read system power status.");
        return status.AcLineStatus == 1;
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
}
