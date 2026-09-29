using ObsBatteryGuard.Core;

internal static class TrayStatusRegression
{
    public static void Run()
    {
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.FromHours(8));
        const string version = "1.0.test";
        GuardianStatus Healthy() => new()
        {
            GuardianVersion = version,
            Phase = GuardPhase.Idle,
            ProtectionEnabled = true,
            ObsUpdatedAt = now,
            WarningThreshold = 25,
            Battery = new(true, 80, false, false, false, 90, now, ""),
            Obs = new(true, false, false, TimeSpan.Zero, 0, "", "", "", "")
        };
        TrayStatusPresentation Show(GuardianStatus? status, string? offlineReason = null) =>
            TrayStatusPresentation.Create(status, version, offlineReason, now);

        var idle = Show(Healthy());
        Check(idle.Indicator == TrayIndicator.Idle && idle.Summary.Contains("未在录像"), "正常待机状态错误");
        var recording = Healthy();
        recording.Obs = recording.Obs with { IsRecording = true };
        Check(Show(recording).Indicator == TrayIndicator.Recording, "正在录像状态错误");
        recording.Obs = recording.Obs with { IsPaused = true };
        Check(Show(recording).Indicator == TrayIndicator.Paused, "暂停录像状态错误");
        recording.RecordingOperationBusy = true;
        Check(Show(recording).Indicator == TrayIndicator.Busy, "忙碌任务没有优先于暂停录像");
        recording.Phase = GuardPhase.Finalizing;
        Check(Show(recording).Summary == "正在保存录像", "保存录像状态错误");
        Console.WriteLine("PASS tray-idle-recording-paused-and-busy");

        Check(Show(null).Indicator == TrayIndicator.Offline && Show(null).Tooltip.Contains("保护状态未知"), "后台离线仍声称保护已启用");
        Check(Show(Healthy(), "test error").Indicator == TrayIndicator.Offline, "通信失败使用了旧在线状态");
        var disconnected = Healthy();
        disconnected.Obs = disconnected.Obs with { IsConnected = false };
        Check(Show(disconnected).Indicator == TrayIndicator.Offline, "OBS 离线状态错误");
        var stale = Healthy();
        stale.ObsUpdatedAt = now.AddSeconds(-3.01);
        Check(Show(stale).Indicator == TrayIndicator.Warning && Show(stale).Summary.Contains("未确认"), "过期录像状态仍显示正常");
        stale.ObsUpdatedAt = now.AddSeconds(-3);
        Check(Show(stale).Indicator == TrayIndicator.Idle, "3 秒边界误报过期");
        stale.ObsUpdatedAt = null;
        Check(Show(stale).Indicator == TrayIndicator.Warning, "无录像时间戳仍显示正常");
        var unknown = Healthy();
        unknown.Obs = unknown.Obs with { Error = "test" };
        Check(Show(unknown).Summary.Contains("未确认"), "未知录像状态仍显示正常");
        Console.WriteLine("PASS tray-offline-and-stale-recording-state");

        foreach (Action<GuardianStatus> alter in new Action<GuardianStatus>[]
        {
            status => status.GuardianVersion = "old",
            status => status.SettingsError = "test",
            status => status.LogWriteError = "test",
            status => status.LastError = "test",
            status => status.Phase = GuardPhase.Error,
            status => status.ProtectionEnabled = false,
            status => status.Battery = status.Battery with { Percent = 20 },
            status => status.Battery = status.Battery with { IsCritical = true },
            status => status.Battery = status.Battery with { IsAvailable = false },
            status => status.Battery = status.Battery with { Percent = -1 }
        })
        {
            var warning = Healthy();
            warning.Obs = warning.Obs with { IsRecording = true };
            warning.RecordingOperationBusy = true;
            alter(warning);
            Check(Show(warning).Indicator == TrayIndicator.Warning, "异常状态没有优先于录制或忙碌状态");
        }
        var disabled = Healthy();
        disabled.ProtectionEnabled = false;
        Check(Show(disabled).Tooltip.Contains("保护已关闭") && !Show(disabled).Detail.Contains("保护功能已启用"), "保护关闭时误导用户");
        var absent = Healthy();
        absent.Battery = absent.Battery with { IsAvailable = false, Percent = -1, IsOnAcPower = true };
        Check(Show(absent).Tooltip.Contains("外接电源") && Show(absent).Tooltip.Contains("电量未知") && !Show(absent).Detail.Contains("保护功能已启用"), "无电池时误报受保护");
        Console.WriteLine("PASS tray-warnings-version-errors-and-disabled-protection");

        var power = Healthy();
        power.ShutdownScheduled = true;
        power.GuardianVersion = "old";
        Check(Show(power).Summary == "电源操作倒计时", "倒计时未优先于版本异常");
        power.PowerActionCommitted = true;
        Check(Show(power).Summary == "电源操作已提交" && Show(power).Detail.Contains("不能保证撤回"), "已提交电源动作提示不准确");
        power.ApplicationExiting = true;
        Check(Show(power).Summary == "正在退出软件" && Show(power).Tooltip.Contains("保护已关闭"), "正在退出却显示继续保护");
        Console.WriteLine("PASS tray-power-and-exit-priority");

        var charging = Healthy();
        charging.Battery = charging.Battery with { IsOnAcPower = true, IsCharging = true, Percent = 20 };
        var chargingView = Show(charging);
        Check(chargingView.Indicator == TrayIndicator.Idle && chargingView.Tooltip.Contains("外接电源 20%·充电中"), "充电信息错误或接电时误报低电量");
        Check(idle.Tooltip.Contains("电池供电 80%"), "缺少电池供电百分比");
        foreach (var percentage in new[] { -1, 0, 20, 80, 100, 101 })
        foreach (var ac in new[] { true, false })
        foreach (var enabled in new[] { true, false })
        {
            var state = Healthy();
            state.ProtectionEnabled = enabled;
            state.Battery = state.Battery with { Percent = percentage, IsOnAcPower = ac, IsCharging = ac };
            var presentation = Show(state);
            Check(presentation.Tooltip.Length <= 63 && !presentation.Tooltip.Contains('\n'), "托盘提示长度超过 Windows 限制");
        }
        const string sensitive = @"C:\private\password=secret";
        var confidential = Healthy();
        confidential.LastError = sensitive;
        confidential.LogWriteError = sensitive;
        confidential.SettingsError = sensitive;
        confidential.GuardianVersion = sensitive;
        confidential.Obs = confidential.Obs with { OutputPath = sensitive, Error = sensitive };
        Check(!Show(confidential).Detail.Contains(sensitive) && !Show(confidential).Tooltip.Contains(sensitive) &&
              !Show(confidential, sensitive).Detail.Contains(sensitive), "托盘泄漏原始错误或敏感路径");
        Console.WriteLine("PASS tray-battery-tooltip-limit-and-no-sensitive-content");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
