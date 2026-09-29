using System.Text;
using ObsBatteryGuard.Core;

internal static class ExperienceRegression
{
    public static Task RunAsync(string root)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var store = new SettingsStore(Path.Combine(root, "experience"));
        store.Save(new AppSettings { StopBatteryPercent = 18, ObsConfigDirectory = "D:\\portable-config" });
        File.WriteAllText(store.SettingsPath, "{broken");
        var recovered = store.Load();
        Check(recovered.StopBatteryPercent == 18 && store.LastLoadError.Length > 0 && File.ReadAllText(store.SettingsPath) == "{broken", "坏配置没有保留最近有效设置/原文件");
        var fresh = new SettingsStore(store.DataDirectory).Load();
        Check(!fresh.AutoConnectObs && !fresh.EmergencyAlwaysShutdown && fresh.AfterStopAction == PostRecordingAction.None, "首次读取坏配置启用了危险默认值");
        Console.WriteLine("PASS settings-read-failure-preserves-file-and-last-good");

        var config = Path.Combine(root, "profiles-test");
        var a = Path.Combine(config, "basic", "profiles", "a");
        var b = Path.Combine(config, "basic", "profiles", "b");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(config, "global.ini"), "[BasicWindow]\nProfile=old\nProfileDir=a\n");
        File.WriteAllText(Path.Combine(a, "basic.ini"), "[General]\nName=old\n[Output]\nMode=Simple\n[SimpleOutput]\nRecFormat2=mp4\n");
        File.WriteAllText(Path.Combine(b, "basic.ini"), "[General]\nName=current\n[Output]\nMode=Simple\n[SimpleOutput]\nRecFormat2=mkv\n");
        Check(ObsProfileManager.Inspect("current", config).IsMkv, "全局旧配置覆盖了明确指定的当前配置");
        Check(!ObsProfileManager.Inspect("missing", config).Found, "未找到配置时不应猜测其他配置");
        File.WriteAllText(Path.Combine(b, "basic.ini"), "[General]\nName=current\n[Output]\nMode=Advanced\n[AdvOut]\nRecType=FFmpeg\nRecFormat2=mkv\n");
        Check(!ObsProfileManager.Inspect("current", config).IsMkv, "自定义 FFmpeg 被错误验证为标准 MKV");
        Console.WriteLine("PASS explicit-profile-portable-root-and-ffmpeg-safety");

        var log = Path.Combine(root, "incremental.log");
        File.WriteAllText(log, "2026-09-07 12:00:00 | 信息 | 开始\n    第一条详情\n", new UTF8Encoding(false));
        var reader = new IncrementalLogReader();
        var first = reader.Read(log);
        Check(first == reader.Read(log), "未变更日志不稳定");
        File.AppendAllText(log, "2026-09-07 12:01:00 | 错误 | 失败\n    完整详情\n半行", new UTF8Encoding(false));
        var second = reader.Read(log);
        Check(second.Contains("完整详情") && !second.Contains("半行"), "部分行处理不正确");
        File.AppendAllText(log, "完成\n", new UTF8Encoding(false));
        Check(reader.Read(log).Contains("半行完成"), "部分 UTF-8 行没有正确恢复");
        var filtered = IncrementalLogReader.Filter(second, "失败", true);
        Check(filtered.Contains("完整详情") && !filtered.Contains("第一条详情"), "日志过滤丢失事件详情或混入其他事件");
        File.WriteAllText(log, "清空后新日志\n");
        Check(reader.Read(log).Contains("清空后新日志") && !reader.Read(log).Contains("第一条详情"), "日志截短后没有重置");
        File.WriteAllText(log, string.Concat(Enumerable.Repeat("中文长日志内容一二三四五六七八九十\n", 50000)), new UTF8Encoding(false));
        var tail = reader.Read(log, true);
        Check(tail.Split('\n').Length <= 1200 && !tail.Contains('\uFFFD'), "大日志没有有界读取或 UTF-8 边界损坏");
        Console.WriteLine("PASS incremental-log-tail-partial-lines-filter-and-truncation");
        var now = DateTimeOffset.Now;
        using (var logger = new DurableLogger(Path.Combine(root, "rotation"), () => now))
        {
            logger.Info("test", "跨日前");
            var oldPath = logger.CurrentLogPath;
            now = now.AddDays(1);
            logger.Info("test", "跨日后");
            Check(oldPath != logger.CurrentLogPath && File.ReadAllText(oldPath).Contains("跨日前") && new IncrementalLogReader().Read(logger.CurrentLogPath).Contains("跨日后"), "日志没有跨日切换");
        }
        Console.WriteLine("PASS daily-log-rollover-keeps-history");
        return Task.CompletedTask;
    }
}
