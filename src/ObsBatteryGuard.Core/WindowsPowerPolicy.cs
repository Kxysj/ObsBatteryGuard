using System.Runtime.InteropServices;

namespace ObsBatteryGuard.Core;

public sealed record PowerPolicySnapshot(bool Readable, bool HibernateAvailable, uint? LowPercent, uint? CriticalPercent,
    uint? LowAction, uint? CriticalAction, uint? LidAction, uint? SleepSeconds, string Error);

public static class WindowsPowerPolicy
{
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern byte IsPwrHibernateAllowed();
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);

    public static PowerPolicySnapshot Read()
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            var result = PowerGetActiveScheme(IntPtr.Zero, out pointer);
            if (result != 0) return new(false, IsPwrHibernateAllowed() != 0, null, null, null, null, null, null, $"无法读取当前电源计划（{result}）");
            var scheme = Marshal.PtrToStructure<Guid>(pointer);
            uint? ReadValue(string group, string setting)
            {
                var subgroup = Guid.Parse(group); var settingGuid = Guid.Parse(setting);
                return PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingGuid, out var value) == 0 ? value : null;
            }
            const string battery = "e73a048d-bf27-4f12-9731-8b2076e8891f";
            return new(true, IsPwrHibernateAllowed() != 0,
                ReadValue(battery, "8183ba9a-e910-48da-8769-14ae6dc1170a"),
                ReadValue(battery, "9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469"),
                ReadValue(battery, "d8742dcb-3e6a-4b3c-b3fe-374623cdcf06"),
                ReadValue(battery, "637ea02f-bbcb-4015-8e2c-a1c7b9c0b546"),
                ReadValue("4f971e89-eebd-4455-a8de-9e59040e7347", "5ca83367-6e45-459f-a27b-476b1d01c936"),
                ReadValue("238c9fa8-0aad-41ed-83f4-97be242c8f20", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), string.Empty);
        }
        catch (Exception ex) { return new(false, false, null, null, null, null, null, null, ex.Message); }
        finally { if (pointer != IntPtr.Zero) LocalFree(pointer); }
    }

    public static IReadOnlyList<string> Evaluate(PowerPolicySnapshot policy, AppSettings settings)
    {
        var lines = new List<string>();
        if (!policy.Readable) { lines.Add("待确认：" + policy.Error); return lines; }
        lines.Add($"Windows 电池供电策略：低电量 {policy.LowPercent?.ToString() ?? "未知"}% / {ActionName(policy.LowAction)}；关键电量 {policy.CriticalPercent?.ToString() ?? "未知"}% / {ActionName(policy.CriticalAction)}");
        lines.Add($"合盖动作：{ActionName(policy.LidAction)}；空闲睡眠：{(policy.SleepSeconds is 0 ? "从不" : policy.SleepSeconds is { } seconds ? seconds + " 秒" : "未知")}；休眠可用：{policy.HibernateAvailable}");
        if (policy.LowAction is > 0 && policy.LowPercent >= settings.StopBatteryPercent || policy.CriticalAction is > 0 && policy.CriticalPercent >= settings.StopBatteryPercent)
            lines.Add("风险：Windows 低电量动作可能先于软件停止录像；请提高软件停止阈值并预留保存时间，不建议关闭系统最后的电池保护。");
        if (!policy.HibernateAvailable && (settings.AfterStopAction == PostRecordingAction.Hibernate || policy.LowAction == 2 || policy.CriticalAction == 2 || policy.LidAction == 2))
            lines.Add("风险：配置要求休眠，但本机休眠不可用。需要先验证或调整策略。");
        if (policy.LidAction is > 0) lines.Add("风险：合盖可能睡眠/休眠/关机；软件防睡眠不能保证阻止合盖动作，验收前保持屏幕打开。");
        if (policy.LidAction is null || policy.CriticalPercent is null || policy.CriticalAction is null)
            lines.Add("待确认：部分电源设置无法读取，不能据此宣称电源策略安全。");
        if (policy.SleepSeconds is > 0 && !settings.PreventSystemSleepWhileRecording)
            lines.Add("风险：电池供电有空闲睡眠计时，但软件录像防睡眠未启用。");
        return lines;
    }

    private static string ActionName(uint? action) => action switch { 0 => "不操作", 1 => "睡眠", 2 => "休眠", 3 => "关机", _ => "未知" };
}
