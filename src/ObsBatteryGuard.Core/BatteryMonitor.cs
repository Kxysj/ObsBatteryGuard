using System.Runtime.InteropServices;

namespace ObsBatteryGuard.Core;

public static class BatteryMonitor
{
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

    public static BatterySnapshot Read()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return new BatterySnapshot(false, -1, false, false, false, null, DateTimeOffset.Now, "无法读取 Windows 电源状态");
        }

        var batteryMissing = (status.BatteryFlag & 128) != 0;
        var percentKnown = status.BatteryLifePercent != byte.MaxValue;
        var isAvailable = !batteryMissing && percentKnown;
        var isOnAc = status.AcLineStatus == 1;
        var isCharging = (status.BatteryFlag & 8) != 0;
        var isCritical = (status.BatteryFlag & 4) != 0;
        int? remaining = status.BatteryLifeTime == uint.MaxValue ? null : (int)Math.Ceiling(status.BatteryLifeTime / 60d);

        var description = batteryMissing
            ? "未检测到电池"
            : isOnAc
                ? isCharging ? "已接通电源，正在充电" : "已接通电源"
                : "正在使用电池";

        return new BatterySnapshot(
            isAvailable,
            percentKnown ? status.BatteryLifePercent : -1,
            isOnAc,
            isCharging,
            isCritical,
            remaining,
            DateTimeOffset.Now,
            description);
    }
}
