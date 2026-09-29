namespace ObsBatteryGuard.Core;

public sealed record GuardProcessIdentity(int Id, string Path, long StartTicks);
public sealed record ApplicationExitResult(bool Closed, string Message, bool UserCancelled = false);

public interface IApplicationExitEnvironment
{
    IReadOnlyList<GuardProcessIdentity> FindGuardians();
    Task<IpcResponse> ReadStatusAsync();
    void OnExitConfirmed();
    Task RequestExitAsync(GuardProcessIdentity target);
    Task<bool> WaitForExitAsync(IReadOnlyList<GuardProcessIdentity> targets, TimeSpan timeout);
    void ForceExit(GuardProcessIdentity target);
}

/// <summary>Explicit, user-confirmed exit stops identified guardians; hiding to tray never calls this workflow.</summary>
public static class ApplicationExit
{
    public static string BuildPrompt(GuardianStatus? status, bool unsaved, bool hasGuardians)
    {
        var lines = new List<string>();
        if (unsaved) lines.Add("设置尚未保存，退出将放弃修改。");
        if (status is null) lines.Add(hasGuardians ? "后台未响应，无法确认录像状态。" : "无法确认 OBS 录像状态。");
        if (status is not null)
        {
            if (status.Obs.IsRecording) lines.Add("OBS 正在录像，退出后将失去保护。");
            if (!status.Obs.IsRecordingStateKnown || status.ObsUpdatedAt is null || DateTimeOffset.Now - status.ObsUpdatedAt > TimeSpan.FromSeconds(3))
                lines.Add("录像状态未确认，请在 OBS 中核对。");
            if (status.RecordingOperationBusy || status.Phase is GuardPhase.Stopping or GuardPhase.Finalizing)
                lines.Add("分割／保存正在处理，退出可能中断收尾。");
            if (status.PowerActionCommitted) lines.Add("电源动作已提交系统，不能撤销。");
            else if (status.ShutdownScheduled) lines.Add("本软件的关机／休眠倒计时将取消。");
        }
        return "退出软件并停止保护和自动分割？\nOBS 保持运行，不会自动停录。" +
            (lines.Count == 0 ? "" : "\n\n" + string.Join(Environment.NewLine, lines));
    }

    public static async Task<ApplicationExitResult> RunAsync(IApplicationExitEnvironment environment, bool unsaved, Func<string, Task<bool>> confirm)
    {
        var targets = environment.FindGuardians();
        var response = await environment.ReadStatusAsync();
        var status = response.Success ? response.Status : null;
        if (status?.GuardianProcessId > 0 && !targets.Any(p => p.Id == status.GuardianProcessId))
            return new(false, "响应的后台不属于当前可识别的会话，未执行退出。请检查是否打开了其他会话的软件。");
        if (!await confirm(BuildPrompt(status, unsaved, targets.Count > 0))) return new(false, "已取消退出，后台运行状态保持不变。", UserCancelled: true);
        environment.OnExitConfirmed();
        targets = environment.FindGuardians();
        foreach (var target in targets) await environment.RequestExitAsync(target);
        if (!await environment.WaitForExitAsync(targets, TimeSpan.FromSeconds(8)))
        {
            var remaining = environment.FindGuardians();
            if (remaining.Count > 0)
            {
                var detail = string.Join("\n", remaining.Select(p => $"PID {p.Id} · {p.Path}"));
                if (!await confirm("后台未能退出，是否强制结束？\n可能中断文件收尾或日志写入；OBS 不会关闭。\n已提交系统的电源动作不能撤销。\n\n" + detail))
                    return new(false, "后台尚未完全退出。已保留操作界面，请检查后台状态。");
                foreach (var target in remaining) environment.ForceExit(target);
                if (!await environment.WaitForExitAsync(remaining, TimeSpan.FromSeconds(3)))
                    return new(false, "仍有后台未退出；请检查进程权限。操作界面已保留。");
            }
        }
        return environment.FindGuardians().Count == 0
            ? new(true, "界面及后台守护已退出。")
            : new(false, "检测到后台再次启动，未关闭操作界面。请检查其他软件窗口或启动任务。");
    }
}
