using System.Diagnostics;
using System.Net;

namespace ObsBatteryGuard.Core;

public interface IObsProcessLauncher
{
    bool IsRunning { get; }
    ObsRequestResult EnsureStarted(string configuredPath);
}

public sealed class ObsProcessLauncher : IObsProcessLauncher
{
    public bool IsRunning => ObsProfileManager.IsObsRunning();

    public static bool IsLocalHost(string host) =>
        host.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim().Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    public ObsRequestResult EnsureStarted(string configuredPath)
    {
        try
        {
            if (IsRunning) return new(true, "OBS 已运行，无需重复启动");
            var search = ObsInstallationLocator.FindBest(configuredPath);
            if (!search.Found) return new(false, "未找到 OBS，请到“参数设置 → OBS 连接”自动查找或选择 obs64.exe，并保存设置。");
            var name = Path.GetFileName(search.Path);
            if (!name.Equals("obs64.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("obs32.exe", StringComparison.OrdinalIgnoreCase))
                return new(false, "OBS 程序路径不是 obs64.exe / obs32.exe，请重新选择并保存。");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = search.Path,
                WorkingDirectory = Path.GetDirectoryName(search.Path),
                UseShellExecute = true
            });
            return process is null ? new(false, "Windows 未能启动 OBS，请检查程序路径和权限。") : new(true, "已启动 OBS，正在等待 WebSocket 服务就绪");
        }
        catch (Exception ex) { return new(false, "启动 OBS 失败：" + ex.Message); }
    }
}
