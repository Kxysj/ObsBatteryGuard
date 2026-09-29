using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ObsBatteryGuard.Core;

public static class ReadOnlyPreflight
{
    public static async Task<string> RunAsync(CancellationToken token)
    {
        var report = new StringBuilder();
        report.AppendLine("# 第三阶段：本机只读预检");
        report.AppendLine($"\n时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine("\n本检查不启动 OBS/守护、不控制录像、不改配置或启动项、不执行电源动作。密码不写入报告。");
        report.AppendLine("\n验收状态：仅只读预检；即使所有查询成功，也不代表实际录制、断电保存或电源动作已验收。");
        var battery = BatteryMonitor.Read();
        report.AppendLine($"\n## 电池\n\n可读取电量：{battery.IsAvailable}；电量：{(battery.IsAvailable ? battery.Percent + "%" : "未知")}；外接电源：{battery.IsOnAcPower}；状态：{battery.Description}");
        if (!battery.IsAvailable) report.AppendLine("\n未满足实机电池验收条件：需在目标笔记本确认电池能被 Windows 正确读取。");
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ObsBatteryGuard", "settings.json");
        AppSettings? settings = null;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path, token), IpcProtocol.JsonOptions)
                ?? throw new JsonException("配置为空");
        }
        catch (Exception ex) { report.AppendLine("\n配置读取失败，未生成或覆盖原配置：" + ex.Message); }
        report.AppendLine("\n## 守护状态\n");
        var guardian = await GuardianIpcClient.SendAsync("get_status", 2500, token);
        if (guardian.Status is { } status)
        {
            report.AppendLine($"版本：{status.GuardianVersion}；状态：{status.PhaseText}；电源计划：{status.ShutdownScheduled}；录制操作忙碌：{status.RecordingOperationBusy}");
            report.AppendLine($"\n最新 OBS 有效状态：{status.ObsUpdatedAt?.ToString("O") ?? "旧版未提供或尚未确认"}；配置异常：{status.SettingsError}");
            var expected = typeof(ReadOnlyPreflight).Assembly.GetName().Version?.ToString(3);
            if (status.GuardianVersion != expected) report.AppendLine($"\n待处理：后台版本与待验收版本 {expected} 不一致，不能代表新版运行结果。不要在录制或倒计时期间强制替换。");
        }
        else report.AppendLine(guardian.Message);
        if (settings is null) return report.ToString();
        report.AppendLine("\n## OBS 自带文件分割（只读磁盘配置）\n");
        report.AppendLine(ObsProfileManager.InspectNativeSplit(configRoot: settings.ObsConfigDirectory));
        report.AppendLine("\n## 配置与 Windows 电源策略\n");
        report.AppendLine($"停止阈值 {settings.StopBatteryPercent}%；紧急阈值 {settings.EmergencyBatteryPercent}%；普通动作 {settings.AfterStopAction}；分割 {settings.SplitMinutes} 分钟；自动分割 {settings.EnableAutomaticSplit}。");
        foreach (var line in WindowsPowerPolicy.Evaluate(WindowsPowerPolicy.Read(), settings)) report.AppendLine("\n- " + line);
        report.AppendLine("\n## OBS 连接、格式和目录\n");
        if (settings.ObsHost is not ("127.0.0.1" or "localhost" or "::1"))
        { report.AppendLine("本预检仅允许本机回环地址，已跳过非回环目标。"); return report.ToString(); }
        await using var obs = new ObsWebSocketClient();
        var connection = await obs.ConnectAsync(settings.ObsHost, settings.ObsPort, settings.ObsPassword, Math.Clamp(settings.ObsConnectionTimeoutSeconds, 2, 10), token);
        report.AppendLine(connection.Message);
        if (!connection.Success) return report.ToString();
        var samples = new List<long>();
        ObsSnapshot? snapshot = null;
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            snapshot = await obs.GetSnapshotAsync(token);
            samples.Add(watch.ElapsedMilliseconds);
            if (!snapshot.IsRecordingStateKnown) break;
            await Task.Delay(200, token);
        }
        report.AppendLine($"\n只读状态请求耗时（毫秒）：{string.Join(", ", samples)}");
        if (snapshot is { } current)
            report.AppendLine($"\n录制状态已知：{current.IsRecordingStateKnown}；录制中：{current.IsRecording}；目录：{current.RecordDirectory}");
        report.AppendLine("\n" + (await obs.InspectFormatAsync(token)).Message);
        if (snapshot is { RecordDirectory.Length: > 0 })
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(snapshot.RecordDirectory))!);
                report.AppendLine($"\n目录存在：{Directory.Exists(snapshot.RecordDirectory)}；所在卷空闲：{drive.AvailableFreeSpace / 1024d / 1024 / 1024:F1} GB。尚未执行写入或录像文件验证。");
            }
            catch (Exception ex) { report.AppendLine("\n目录检查失败：" + ex.Message); }
        }
        return report.ToString();
    }
}
