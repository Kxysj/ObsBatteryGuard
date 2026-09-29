using System.Text.Json.Serialization;

namespace ObsBatteryGuard.Core;

public enum PostRecordingAction
{
    None,
    Shutdown,
    Hibernate
}

public enum GuardPhase
{
    Starting,
    Idle,
    Recording,
    Warning,
    Stopping,
    Finalizing,
    ShutdownScheduled,
    Error
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;

    public string ObsHost { get; set; } = "127.0.0.1";
    public string ObsConfigDirectory { get; set; } = string.Empty;
    public int ObsPort { get; set; } = 4455;
    public string ProtectedObsPassword { get; set; } = string.Empty;
    public string ObsExecutablePath { get; set; } = string.Empty;
    public bool AutoConnectObs { get; set; } = true;
    public bool AutoLaunchObs { get; set; }
    public int ObsConnectionTimeoutSeconds { get; set; } = 6;
    public int ObsReconnectSeconds { get; set; } = 5;

    public bool BatteryProtectionEnabled { get; set; } = true;
    public int WarningBatteryPercent { get; set; } = 25;
    public int StopBatteryPercent { get; set; } = 20;
    public int EmergencyBatteryPercent { get; set; } = 10;
    public int MinimumStartBatteryPercent { get; set; } = 30;
    public int BatteryPollSeconds { get; set; } = 5;
    public int ConsecutiveLowReadings { get; set; } = 2;
    public bool StopByRemainingMinutesEnabled { get; set; }
    public int StopByRemainingMinutes { get; set; } = 15;
    public bool FailSafeWhenBatteryUnknown { get; set; }
    public int UnknownBatteryTimeoutSeconds { get; set; } = 120;

    public bool RequireMkv { get; set; } = true;
    public bool EnableAutomaticSplit { get; set; } = true;
    public int SplitMinutes { get; set; } = 10;
    public bool AllowStopStartSplitFallback { get; set; } = true;
    public int SplitFallbackDelayMilliseconds { get; set; } = 800;
    public int SplitRestartTimeoutSeconds { get; set; } = 20;
    public double MinimumDiskFreeGb { get; set; } = 5;
    public bool StopRecordingWhenDiskLow { get; set; } = true;
    public int ObsStopTimeoutSeconds { get; set; } = 20;
    public int FileFlushWaitSeconds { get; set; } = 5;
    public bool PreventSystemSleepWhileRecording { get; set; } = true;
    public bool KeepDisplayAwakeWhileRecording { get; set; }

    public PostRecordingAction AfterStopAction { get; set; } = PostRecordingAction.Shutdown;
    public int ShutdownDelaySeconds { get; set; } = 60;
    public int EmergencyShutdownDelaySeconds { get; set; } = 15;
    public bool CancelShutdownWhenAcReturns { get; set; } = true;
    public bool ResumeRecordingWhenAcReturns { get; set; }
    public bool ForceCloseAppsOnShutdown { get; set; }
    public bool ForceCloseAppsOnEmergency { get; set; } = true;
    public bool EmergencyAlwaysShutdown { get; set; } = true;

    public bool AutoStartGuardianWithWindows { get; set; } = true;
    public bool AutoStartRecordingWhenObsConnects { get; set; }
    public int BatteryLogIntervalSeconds { get; set; } = 60;
    public int ObsStatusPollMilliseconds { get; set; } = 300;
    public int StatusRefreshMilliseconds { get; set; } = 500;

    [JsonIgnore]
    public string ObsPassword
    {
        get => CredentialProtector.Unprotect(ProtectedObsPassword);
        set => ProtectedObsPassword = CredentialProtector.Protect(value ?? string.Empty);
    }

    public void Normalize()
    {
        ObsHost = string.IsNullOrWhiteSpace(ObsHost) ? "127.0.0.1" : ObsHost.Trim();
        ObsPort = Math.Clamp(ObsPort, 1, 65535);
        ObsConnectionTimeoutSeconds = Math.Clamp(ObsConnectionTimeoutSeconds, 2, 30);
        ObsReconnectSeconds = Math.Clamp(ObsReconnectSeconds, 1, 120);

        WarningBatteryPercent = Math.Clamp(WarningBatteryPercent, 5, 100);
        StopBatteryPercent = Math.Clamp(StopBatteryPercent, 3, WarningBatteryPercent);
        EmergencyBatteryPercent = Math.Clamp(EmergencyBatteryPercent, 1, Math.Max(1, StopBatteryPercent - 1));
        MinimumStartBatteryPercent = Math.Clamp(MinimumStartBatteryPercent, StopBatteryPercent, 100);
        BatteryPollSeconds = Math.Clamp(BatteryPollSeconds, 1, 60);
        ConsecutiveLowReadings = Math.Clamp(ConsecutiveLowReadings, 1, 10);
        StopByRemainingMinutes = Math.Clamp(StopByRemainingMinutes, 1, 240);
        UnknownBatteryTimeoutSeconds = Math.Clamp(UnknownBatteryTimeoutSeconds, 15, 3600);

        SplitMinutes = Math.Clamp(SplitMinutes, 1, 1440);
        SplitFallbackDelayMilliseconds = Math.Clamp(SplitFallbackDelayMilliseconds, 200, 5000);
        SplitRestartTimeoutSeconds = Math.Clamp(SplitRestartTimeoutSeconds, 5, 120);
        MinimumDiskFreeGb = double.IsFinite(MinimumDiskFreeGb) ? Math.Clamp(MinimumDiskFreeGb, 0.1, 1000) : 5;
        ObsStopTimeoutSeconds = Math.Clamp(ObsStopTimeoutSeconds, 3, 120);
        FileFlushWaitSeconds = Math.Clamp(FileFlushWaitSeconds, 0, 60);

        ShutdownDelaySeconds = Math.Clamp(ShutdownDelaySeconds, 0, 3600);
        EmergencyShutdownDelaySeconds = Math.Clamp(EmergencyShutdownDelaySeconds, 0, 120);
        BatteryLogIntervalSeconds = Math.Clamp(BatteryLogIntervalSeconds, 10, 3600);
        ObsStatusPollMilliseconds = Math.Clamp(ObsStatusPollMilliseconds, 100, 5000);
        StatusRefreshMilliseconds = Math.Clamp(StatusRefreshMilliseconds, 250, 10000);
    }
}

public sealed record BatterySnapshot(
    bool IsAvailable,
    int Percent,
    bool IsOnAcPower,
    bool IsCharging,
    bool IsCritical,
    int? RemainingMinutes,
    DateTimeOffset Timestamp,
    string Description);

public sealed record ObsSnapshot(
    bool IsConnected,
    bool IsRecording,
    bool IsPaused,
    TimeSpan Duration,
    long OutputBytes,
    string OutputPath,
    string Version,
    string CurrentProfile,
    string Error,
    string RecordDirectory = "")
{
    public string LastCompletedOutputPath { get; init; } = string.Empty;
    public bool IsRecordingStateKnown => IsConnected && string.IsNullOrWhiteSpace(Error);
    public bool IsConfirmedStopped => IsRecordingStateKnown && !IsRecording;
}

public sealed class GuardianStatus
{
    public int GuardianProcessId { get; set; }
    public bool ApplicationExiting { get; set; }
    public bool PowerActionCommitted { get; set; }
    public DateTimeOffset? ObsUpdatedAt { get; set; }
    public string SettingsError { get; set; } = string.Empty;
    public bool RecordingOperationBusy { get; set; }
    public string LogWriteError { get; set; } = string.Empty;
    public string GuardianVersion { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public GuardPhase Phase { get; set; } = GuardPhase.Starting;
    public string PhaseText { get; set; } = "正在启动后台守护";
    public BatterySnapshot Battery { get; set; } = new(false, -1, false, false, false, null, DateTimeOffset.Now, "等待读取");
    public ObsSnapshot Obs { get; set; } = new(false, false, false, TimeSpan.Zero, 0, string.Empty, string.Empty, string.Empty, string.Empty);
    public bool ProtectionEnabled { get; set; }
    public bool ShutdownScheduled { get; set; }
    public DateTimeOffset? ShutdownAt { get; set; }
    public DateTimeOffset? NextSplitAt { get; set; }
    public string LastAction { get; set; } = "无";
    public string LastError { get; set; } = string.Empty;
    public string LogFilePath { get; set; } = string.Empty;
    public string SettingsFilePath { get; set; } = string.Empty;
    public int WarningThreshold { get; set; }
    public int StopThreshold { get; set; }
    public int EmergencyThreshold { get; set; }
}

public sealed class IpcRequest
{
    public string Command { get; set; } = string.Empty;
    public Dictionary<string, string>? Arguments { get; set; }
}

public sealed class IpcResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public GuardianStatus? Status { get; set; }

    public static IpcResponse Ok(string message, GuardianStatus? status = null) => new() { Success = true, Message = message, Status = status };
    public static IpcResponse Fail(string message, GuardianStatus? status = null) => new() { Success = false, Message = message, Status = status };
}
