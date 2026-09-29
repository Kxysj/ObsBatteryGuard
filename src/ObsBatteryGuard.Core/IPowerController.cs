namespace ObsBatteryGuard.Core;

public interface IPowerController
{
    bool Execute(PostRecordingAction action, bool force, out string error);
}

public sealed class WindowsPowerController : IPowerController
{
    public bool Execute(PostRecordingAction action, bool force, out string error)
    {
        if (action == PostRecordingAction.Shutdown)
            return PowerActions.ScheduleShutdown(0, force, "OBS 电池保护：电源倒计时到期", out error);
        if (action == PostRecordingAction.Hibernate)
        {
            if (!WindowsPowerPolicy.Read().HibernateAvailable)
            { error = "本机休眠未启用或不可用，未提交休眠命令"; return false; }
            return PowerActions.Hibernate(force, out error);
        }
        error = "没有可执行的电源动作";
        return false;
    }
}
