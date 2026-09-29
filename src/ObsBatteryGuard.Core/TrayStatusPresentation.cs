namespace ObsBatteryGuard.Core;

public enum TrayIndicator
{
    Idle,
    Recording,
    Paused,
    Warning,
    Offline,
    Busy
}

/// <summary>Pure presentation logic shared by the tray icon, its tooltip and its menu.</summary>
public sealed record TrayStatusPresentation(TrayIndicator Indicator, string Summary, string Tooltip, string Detail)
{
    public static TrayStatusPresentation Create(
        GuardianStatus? status,
        string expectedVersion,
        string? offlineReason = null,
        DateTimeOffset? now = null)
    {
        // Raw connection errors can include local paths or credentials. Keep tray text categorical.
        if (status is null || !string.IsNullOrWhiteSpace(offlineReason))
        {
            const string summary = "后台未连接";
            return Present(TrayIndicator.Offline, summary, "电量未知", "保护状态未知",
                "无法读取后台状态，不能确认录像和电池保护是否运行。\n请打开主界面检查连接；托盘图标存在不等于保护已生效。");
        }

        var currentTime = now ?? DateTimeOffset.Now;
        var batteryKnown = status.Battery.IsAvailable && status.Battery.Percent is >= 0 and <= 100;
        var batteryText = BatteryText(status.Battery, batteryKnown);
        var protectionText = !status.ProtectionEnabled || status.ApplicationExiting
            ? "保护已关闭"
            : batteryKnown ? "保护已启用" : "保护已启用·电量未知";
        var freshRecording = status.ObsUpdatedAt is { } updated && currentTime - updated <= TimeSpan.FromSeconds(3);
        var recordingKnown = status.Obs.IsRecordingStateKnown && freshRecording;
        var recordingText = !status.Obs.IsConnected ? "OBS 未连接，录像状态未知"
            : !recordingKnown ? "录像状态未确认，请打开主界面检查"
            : status.Obs.IsRecording ? status.Obs.IsPaused ? "录像已暂停" : "正在录像"
            : "未在录像";
        var warnings = new List<string>();

        if (status.ApplicationExiting)
            warnings.Add("软件正在退出，电池保护与自动任务将停止；不会自动关闭 OBS。");
        if (status.PowerActionCommitted)
            warnings.Add("电源操作已提交给 Windows，退出软件不能保证撤回。");
        else if (status.ShutdownScheduled)
            warnings.Add("电源操作正在倒计时，请打开主界面检查或取消。");
        if (!status.Obs.IsConnected)
            warnings.Add("OBS 未连接，无法控制录像。");
        else if (!status.Obs.IsRecordingStateKnown)
            warnings.Add("OBS 录像状态读取失败，不能确认是否正在录像。");
        else if (!freshRecording)
            warnings.Add("录像状态尚未更新或已超过 3 秒，不能将旧状态当作实时状态。");
        if (!string.IsNullOrWhiteSpace(expectedVersion) && !string.Equals(status.GuardianVersion, expectedVersion, StringComparison.Ordinal))
            warnings.Add("界面与后台版本不一致，请打开主界面处理。");
        if (!string.IsNullOrWhiteSpace(status.SettingsError))
            warnings.Add("配置存在错误，请打开主界面检查。");
        if (!string.IsNullOrWhiteSpace(status.LogWriteError))
            warnings.Add("日志写入异常，请打开主界面检查。");
        if (!string.IsNullOrWhiteSpace(status.LastError) || status.Phase == GuardPhase.Error)
            warnings.Add("后台报告运行异常，请打开主界面查看详情。");
        if (!status.ProtectionEnabled && !status.ApplicationExiting)
            warnings.Add("电池保护已关闭，不会按电量阈值自动停止录像。");
        if (!batteryKnown)
            warnings.Add("电池电量不可用，不能确认电量阈值保护条件。");
        var batteryLow = batteryKnown && !status.Battery.IsOnAcPower &&
                         (status.Battery.IsCritical || status.Battery.Percent <= status.WarningThreshold);
        if (batteryLow)
            warnings.Add("电池电量偏低，请打开主界面检查停止录像与电源操作计划。");
        if (status.Phase == GuardPhase.Warning && !batteryLow)
            warnings.Add("后台处于预警状态，请打开主界面检查。");

        TrayIndicator indicator;
        string summaryText;
        if (status.ApplicationExiting)
            (indicator, summaryText) = (TrayIndicator.Busy, "正在退出软件");
        else if (status.PowerActionCommitted)
            (indicator, summaryText) = (TrayIndicator.Warning, "电源操作已提交");
        else if (status.ShutdownScheduled)
            (indicator, summaryText) = (TrayIndicator.Warning, "电源操作倒计时");
        else if (!status.Obs.IsConnected)
            (indicator, summaryText) = (TrayIndicator.Offline, "OBS 未连接");
        else if (!recordingKnown)
            (indicator, summaryText) = (TrayIndicator.Warning, "录像状态未确认");
        else if (!string.IsNullOrWhiteSpace(expectedVersion) && !string.Equals(status.GuardianVersion, expectedVersion, StringComparison.Ordinal))
            (indicator, summaryText) = (TrayIndicator.Warning, "前后台版本不一致");
        else if (!string.IsNullOrWhiteSpace(status.SettingsError))
            (indicator, summaryText) = (TrayIndicator.Warning, "配置异常");
        else if (!string.IsNullOrWhiteSpace(status.LogWriteError))
            (indicator, summaryText) = (TrayIndicator.Warning, "日志写入异常");
        else if (!string.IsNullOrWhiteSpace(status.LastError) || status.Phase == GuardPhase.Error)
            (indicator, summaryText) = (TrayIndicator.Warning, "后台运行异常");
        else if (!status.ProtectionEnabled)
            (indicator, summaryText) = (TrayIndicator.Warning, "电池保护已关闭");
        else if (!batteryKnown)
            (indicator, summaryText) = (TrayIndicator.Warning, "电池电量不可用");
        else if (batteryLow || status.Phase == GuardPhase.Warning)
            (indicator, summaryText) = (TrayIndicator.Warning, batteryLow ? "电池电量偏低" : "后台预警");
        else if (status.RecordingOperationBusy || status.Phase is GuardPhase.Stopping or GuardPhase.Finalizing or GuardPhase.Starting)
            (indicator, summaryText) = (TrayIndicator.Busy, status.Phase switch
            {
                GuardPhase.Stopping => "正在停止录像",
                GuardPhase.Finalizing => "正在保存录像",
                GuardPhase.Starting => "正在启动软件",
                _ => "正在处理录像任务"
            });
        else if (status.Obs.IsRecording && status.Obs.IsPaused)
            (indicator, summaryText) = (TrayIndicator.Paused, "录像已暂停");
        else if (status.Obs.IsRecording)
            (indicator, summaryText) = (TrayIndicator.Recording, "正在录像");
        else
            (indicator, summaryText) = (TrayIndicator.Idle, "待机·未在录像");

        var detail = $"录像：{recordingText}\n";
        if (status.RecordingOperationBusy)
            detail += "录像任务：正在执行操作，请等待完成。\n";
        if (warnings.Count > 0)
            detail += "注意：\n" + string.Join("\n", warnings);
        else
            detail += "后台已连接；电池保护功能已启用，实际触发取决于配置与实时状态。";
        return Present(indicator, summaryText, batteryText, protectionText, detail);
    }

    private static string BatteryText(BatterySnapshot battery, bool known)
    {
        var power = battery.IsOnAcPower ? "外接电源" : known ? "电池供电" : "电源未知";
        var percent = known ? $"{battery.Percent}%" : "电量未知";
        return $"{power} {percent}" + (battery.IsCharging ? "·充电中" : string.Empty);
    }

    private static TrayStatusPresentation Present(TrayIndicator indicator, string summary, string battery, string protection, string detail)
    {
        var tooltip = $"OBS 电池安全录制｜{summary}｜{battery}｜{protection}";
        // NotifyIcon.Text must stay within the Windows tooltip limit on every supported runtime.
        if (tooltip.Length > 63) tooltip = tooltip[..62] + "…";
        return new(indicator, summary, tooltip,
            $"OBS 电池安全录制\n状态：{summary}\n电源：{battery}\n电池保护：{protection}\n{detail}\n双击图标打开主界面；右键查看菜单。");
    }
}
