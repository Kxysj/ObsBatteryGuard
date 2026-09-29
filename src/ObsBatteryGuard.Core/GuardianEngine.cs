using System.Diagnostics;
using System.Text.Json;

namespace ObsBatteryGuard.Core;

public sealed class GuardianEngine : IAsyncDisposable
{
    private readonly SettingsStore _store;
    private readonly DurableLogger _logger;
    private readonly IObsClient _obs;
    private readonly IObsProcessLauncher _obsLauncher;
    private DateTimeOffset _lastObsLaunchAt = DateTimeOffset.MinValue;
    private readonly Func<BatterySnapshot> _readBattery;
    private readonly IPowerController _power;
    private readonly SleepInhibitor _sleepInhibitor = new();
    private readonly object _statusLock = new();
    private readonly CancellationTokenSource _applicationExit = new();
    private bool _applicationExiting;
    private readonly object _settingsLock = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private volatile bool _reconnectPending;
    private string _lastObsStateError = string.Empty;
    private readonly RecordingOperationGate _recordingOperations = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _backgroundTasks = new();
    private AppSettings _settings;
    private GuardianStatus _status;
    private DateTimeOffset _nextConnectAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextBatteryPoll = DateTimeOffset.MinValue;
    private DateTimeOffset _nextBatteryLog = DateTimeOffset.MinValue;
    private DateTimeOffset _nextDiskCheck = DateTimeOffset.MinValue;
    private DateTimeOffset? _nextSplitAt;
    private DateTimeOffset? _batteryUnknownSince;
    private readonly PowerCountdown _powerCountdown = new();
    private int _lowReadings;
    private int _safetySequenceActive;
    private int _splitInProgress;
    private long _stopGeneration;
    private bool _warningRaised;
    private bool _protectionTriggered;
    private bool _emergencyTriggered;
    private bool _autoStartAttempted;
    private volatile bool _manualStopRequested;
    private bool _wasRecording;
    private bool _stoppedByProtection;
    private string _lastConnectionError = string.Empty;

    public GuardianEngine(SettingsStore store, DurableLogger logger, IObsClient? obs = null, Func<BatterySnapshot>? readBattery = null, IPowerController? power = null, IObsProcessLauncher? obsLauncher = null)
    {
        _obsLauncher = obsLauncher ?? new ObsProcessLauncher();
        _power = power ?? new WindowsPowerController();
        _obs = obs ?? new ObsWebSocketClient();
        _readBattery = readBattery ?? BatteryMonitor.Read;
        _store = store;
        _logger = logger;
        _settings = store.Load();
        _status = new GuardianStatus
        {
            GuardianVersion = typeof(GuardianEngine).Assembly.GetName().Version?.ToString(3) ?? string.Empty,
            ProtectionEnabled = _settings.BatteryProtectionEnabled,
            LogFilePath = logger.CurrentLogPath,
            SettingsFilePath = store.SettingsPath,
            WarningThreshold = _settings.WarningBatteryPercent,
            StopThreshold = _settings.StopBatteryPercent,
            EmergencyThreshold = _settings.EmergencyBatteryPercent
        };
    }

    public AppSettings GetSettings()
    {
        lock (_settingsLock)
        {
            return JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings)) ?? new AppSettings();
        }
    }

    public GuardianStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new GuardianStatus
            {
                GuardianProcessId = Environment.ProcessId,
                ApplicationExiting = _applicationExiting,
                PowerActionCommitted = _powerCountdown.Committed,
                GuardianVersion = _status.GuardianVersion,
                ObsUpdatedAt = _status.ObsUpdatedAt,
                SettingsError = _store.LastLoadError,
                RecordingOperationBusy = _recordingOperations.IsBusy || Volatile.Read(ref _safetySequenceActive) != 0 || _powerCountdown.Committed,
                LogWriteError = _logger.LastWriteError,
                Timestamp = _status.Timestamp,
                Phase = _status.Phase,
                PhaseText = _applicationExiting ? "用户确认退出，正在关闭全部功能" : _status.PhaseText,
                Battery = _status.Battery,
                Obs = _status.Obs,
                ProtectionEnabled = !_applicationExiting && _status.ProtectionEnabled,
                ShutdownScheduled = _status.ShutdownScheduled,
                ShutdownAt = _status.ShutdownAt,
                NextSplitAt = _applicationExiting ? null : _status.NextSplitAt,
                LastAction = _status.LastAction,
                LastError = _status.LastError,
                LogFilePath = _logger.CurrentLogPath,
                SettingsFilePath = _status.SettingsFilePath,
                WarningThreshold = _status.WarningThreshold,
                StopThreshold = _status.StopThreshold,
                EmergencyThreshold = _status.EmergencyThreshold
            };
        }
    }

    public void ReloadSettings()
    {
        var loaded = _store.Load();
        if (!string.IsNullOrWhiteSpace(_store.LastLoadError)) throw new InvalidOperationException(_store.LastLoadError);
        var previous = GetSettings();
        if (previous.ObsHost != loaded.ObsHost || previous.ObsPort != loaded.ObsPort || previous.ObsPassword != loaded.ObsPassword || previous.ObsConnectionTimeoutSeconds != loaded.ObsConnectionTimeoutSeconds)
            _reconnectPending = true;
        lock (_settingsLock) _settings = loaded;
        _nextBatteryPoll = DateTimeOffset.MinValue;
        _nextDiskCheck = DateTimeOffset.MinValue;
        lock (_statusLock)
        {
            if (previous.EnableAutomaticSplit != loaded.EnableAutomaticSplit || previous.SplitMinutes != loaded.SplitMinutes)
            {
                _nextSplitAt = loaded.EnableAutomaticSplit && _status.Obs.IsRecordingStateKnown && _status.Obs.IsRecording
                    ? DateTimeOffset.Now.AddMinutes(loaded.SplitMinutes) : null;
                _status.NextSplitAt = _nextSplitAt;
            }
            _status.ProtectionEnabled = loaded.BatteryProtectionEnabled;
            _status.WarningThreshold = loaded.WarningBatteryPercent;
            _status.StopThreshold = loaded.StopBatteryPercent;
            _status.EmergencyThreshold = loaded.EmergencyBatteryPercent;
            _status.LastAction = "配置已重新载入";
        }
        _logger.Info("settings", "配置已重新载入");
        LogEffectiveConfiguration(loaded);
    }

    public async Task RunAsync(CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _applicationExit.Token);
        token = lifetime.Token;
        _logger.Info("lifecycle", "后台守护已启动", new { processId = Environment.ProcessId, version = typeof(GuardianEngine).Assembly.GetName().Version?.ToString() });
        LogEffectiveConfiguration(GetSettings());
        SetPhase(GuardPhase.Idle, "后台守护运行正常");
        var batteryTask = Task.Run(() => RunBatteryLoopAsync(token), token);

        while (!token.IsCancellationRequested)
        {
            var settings = GetSettings();
            try
            {
                var battery = GetStatus().Battery;
                if (settings.AutoConnectObs || _reconnectPending)
                    await EnsureObsConnectedAsync(settings, token);

                var obsSnapshot = await PollObsAsync(token);
                UpdateObs(obsSnapshot);
                ApplySleepPolicy(obsSnapshot, settings);
                await HandleRecordingStateAsync(obsSnapshot, battery, settings, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetError("后台监控发生异常：" + ex.Message);
                _logger.Error("guardian", "后台监控循环异常", new { ex.Message, ex.StackTrace });
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(settings.ObsStatusPollMilliseconds), token); }
            catch (OperationCanceledException) { break; }
        }

        try { await batteryTask; await Task.WhenAll(_backgroundTasks.Keys); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        _sleepInhibitor.Clear();
        _logger.Info("lifecycle", "后台守护已停止");
    }

    private async Task RunBatteryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var settings = GetSettings();
                if (DateTimeOffset.Now >= _nextBatteryPoll)
                {
                    _nextBatteryPoll = DateTimeOffset.Now.AddSeconds(settings.BatteryPollSeconds);
                    var battery = _readBattery();
                    UpdateBattery(battery);
                    EvaluateBatteryProtection(battery, GetStatus().Obs, settings, token);
                    HandleAcReturn(battery, settings, token);
                    LogBatteryWhenDue(battery, settings);
                }
                ExecuteDuePowerAction();
                await Task.Delay(100, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                SetError("电池监测异常：" + ex.Message);
                _logger.Error("battery", "电池监测异常", new { ex.Message });
                try { await Task.Delay(500, token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private void TrackBackground(Task task)
    {
        _backgroundTasks.TryAdd(task, 0);
        _ = task.ContinueWith(completed =>
        {
            if (completed.Exception is { } error)
                _logger.Error("guardian", "后台操作异常", new { error.GetBaseException().Message });
            _backgroundTasks.TryRemove(completed, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task EnsureObsConnectedAsync(AppSettings settings, CancellationToken token, bool launchIfNeeded = false)
    {
        await _connectionLock.WaitAsync(token);
        try
        {
        token.ThrowIfCancellationRequested();
        settings = GetSettings();
        if (_reconnectPending && !_recordingOperations.IsBusy && Volatile.Read(ref _safetySequenceActive) == 0)
        {
            await _obs.DisconnectAsync();
            _reconnectPending = false;
            _nextConnectAttempt = DateTimeOffset.MinValue;
        }
        if (_obs.IsConnected || DateTimeOffset.Now < _nextConnectAttempt) return;
        _nextConnectAttempt = DateTimeOffset.Now.AddSeconds(settings.ObsReconnectSeconds);

        if ((settings.AutoLaunchObs || launchIfNeeded) && ObsProcessLauncher.IsLocalHost(settings.ObsHost) && !_obsLauncher.IsRunning &&
            DateTimeOffset.Now - _lastObsLaunchAt >= TimeSpan.FromSeconds(30))
        {
            var launch = _obsLauncher.EnsureStarted(settings.ObsExecutablePath);
            if (!launch.Success)
            {
                _lastConnectionError = launch.Message;
                SetLastError(launch.Message);
                _logger.Warning("obs", launch.Message);
                return;
            }
            _lastObsLaunchAt = DateTimeOffset.Now;
            SetLastAction(launch.Message);
            _logger.Info("obs", launch.Message);
        }

        var result = await _obs.ConnectAsync(settings.ObsHost, settings.ObsPort, settings.ObsPassword, settings.ObsConnectionTimeoutSeconds, token);
        if (result.Success)
        {
            if (!string.IsNullOrEmpty(_lastConnectionError))
                _logger.Info("obs", "OBS 已恢复连接");
            else
                _logger.Info("obs", result.Message);
            _lastConnectionError = string.Empty;
            _autoStartAttempted = false;
            SetLastAction(result.Message);
            if (!string.IsNullOrWhiteSpace(_obs.RecordDirectory))
                _logger.Info("obs", "已读取 OBS 录像目录", new { 录像目录 = _obs.RecordDirectory });
        }
        else if (!string.Equals(_lastConnectionError, result.Message, StringComparison.Ordinal))
        {
            _lastConnectionError = result.Message;
            _logger.Warning("obs", result.Message);
            SetLastError(result.Message);
        }
        }
        finally { _connectionLock.Release(); }
    }

    private async Task<ObsSnapshot> PollObsAsync(CancellationToken token)
    {
        if (!_obs.IsConnected)
            return new ObsSnapshot(false, false, false, TimeSpan.Zero, 0, string.Empty, string.Empty, string.Empty, _lastConnectionError);
        return await _obs.GetSnapshotAsync(token);
    }

    private async Task HandleRecordingStateAsync(ObsSnapshot obs, BatterySnapshot battery, AppSettings settings, CancellationToken token)
    {
        if (!obs.IsRecordingStateKnown) return;
        if (obs.IsRecording) _autoStartAttempted = true;
        if (obs.IsConnected && settings.AutoStartRecordingWhenObsConnects && !_autoStartAttempted && !_manualStopRequested && !_protectionTriggered && !obs.IsRecording)
        {
            _autoStartAttempted = true;
            if (CanStartForBattery(battery, settings, out _))
                await StartRecordingCommandAsync(token);
        }

        if (obs.IsRecording && !_wasRecording)
        {
            _logger.Info("recording", "检测到录像已开始", new { obs.OutputPath });
            _nextSplitAt = settings.EnableAutomaticSplit ? DateTimeOffset.Now.AddMinutes(settings.SplitMinutes) : null;
            SetLastAction("录像保护已启用");
        }
        else if (obs.IsConfirmedStopped && _wasRecording)
        {
            _logger.Info("recording", "检测到录像已停止");
            _nextSplitAt = null;
        }
        _wasRecording = obs.IsRecording;

        if (obs.IsRecording && settings.EnableAutomaticSplit && _nextSplitAt is { } due && DateTimeOffset.Now >= due && Interlocked.CompareExchange(ref _safetySequenceActive, 0, 0) == 0)
        {
            var splitResult = await SplitRecordingCoreAsync(settings, token);
            settings = GetSettings();
            _nextSplitAt = settings.EnableAutomaticSplit && GetStatus().Obs.IsRecording
                ? splitResult.Success
                    ? DateTimeOffset.Now.AddMinutes(settings.SplitMinutes)
                    : DateTimeOffset.Now.AddSeconds(30)
                : null;
            if (!splitResult.Success) SetLastError("自动分割失败：" + splitResult.Message);
        }

        if (obs.IsRecording && settings.StopRecordingWhenDiskLow && DateTimeOffset.Now >= _nextDiskCheck)
        {
            _nextDiskCheck = DateTimeOffset.Now.AddSeconds(Math.Max(5, settings.BatteryPollSeconds));
            var diskCheck = await CheckDiskSpaceAsync(obs, settings, token);
            if (!diskCheck.Success && !_protectionTriggered)
            {
                _protectionTriggered = true;
                _logger.Critical("storage", diskCheck.Message);
                TrackBackground(ExecuteSafetyStopAsync(diskCheck.Message, emergency: false, takePowerAction: false, dryRun: false, token));
            }
        }
        else if (!obs.IsRecording || !settings.StopRecordingWhenDiskLow)
        {
            _nextDiskCheck = DateTimeOffset.MinValue;
        }

        lock (_statusLock)
        {
            _status.NextSplitAt = _nextSplitAt;
            if (!_status.ShutdownScheduled && !_recordingOperations.IsBusy && _status.Phase != GuardPhase.Error && Interlocked.CompareExchange(ref _safetySequenceActive, 0, 0) == 0)
            {
                var warning = settings.BatteryProtectionEnabled && battery.IsAvailable && !battery.IsOnAcPower && battery.Percent <= settings.WarningBatteryPercent;
                _status.Phase = warning ? GuardPhase.Warning : obs.IsRecording ? GuardPhase.Recording : GuardPhase.Idle;
                _status.PhaseText = warning ? $"电量预警 {battery.Percent}%" : obs.IsRecording ? "录像保护中" : "OBS 已连接，等待录像";
            }
        }
    }

    private void EvaluateBatteryProtection(BatterySnapshot battery, ObsSnapshot obs, AppSettings settings, CancellationToken token)
    {
        if (!settings.BatteryProtectionEnabled || battery.IsOnAcPower)
        {
            _lowReadings = 0;
            _batteryUnknownSince = null;
            return;
        }

        if (!battery.IsAvailable)
        {
            _batteryUnknownSince ??= DateTimeOffset.Now;
            if (settings.FailSafeWhenBatteryUnknown && obs.IsRecording && !_protectionTriggered &&
                DateTimeOffset.Now - _batteryUnknownSince >= TimeSpan.FromSeconds(settings.UnknownBatteryTimeoutSeconds))
            {
                _protectionTriggered = true;
                TrackBackground(ExecuteSafetyStopAsync("电池状态持续无法读取", false, true, false, token));
            }
            return;
        }
        _batteryUnknownSince = null;

        if (battery.Percent <= settings.WarningBatteryPercent && !_warningRaised)
        {
            _warningRaised = true;
            _logger.Warning("battery", $"电量已降至预警阈值：{battery.Percent}%");
            SetPhase(GuardPhase.Warning, $"电量预警 {battery.Percent}%");
        }

        var lowByPercent = battery.Percent <= settings.StopBatteryPercent;
        var lowByTime = settings.StopByRemainingMinutesEnabled && battery.RemainingMinutes is { } minutes && minutes <= settings.StopByRemainingMinutes;
        _lowReadings = lowByPercent || lowByTime ? _lowReadings + 1 : 0;

        if (battery.Percent <= settings.EmergencyBatteryPercent && !_emergencyTriggered)
        {
            _emergencyTriggered = true;
            _protectionTriggered = true;
            _logger.Critical("battery", $"达到紧急电量：{battery.Percent}%");
            if (Interlocked.CompareExchange(ref _safetySequenceActive, 0, 0) == 1)
                ScheduleEmergencyShutdown(settings);
            else
                TrackBackground(ExecuteSafetyStopAsync($"达到紧急电量 {battery.Percent}%", true, true, false, token));
            return;
        }

        if (_lowReadings >= settings.ConsecutiveLowReadings && !_protectionTriggered)
        {
            _protectionTriggered = true;
            var reason = lowByPercent ? $"电量达到停止阈值：{battery.Percent}%" : $"预计剩余时间不足：{battery.RemainingMinutes} 分钟";
            TrackBackground(ExecuteSafetyStopAsync(reason, false, true, false, token));
        }
    }

    private void HandleAcReturn(BatterySnapshot battery, AppSettings settings, CancellationToken token)
    {
        if (!battery.IsOnAcPower) return;
        _warningRaised = false;
        _emergencyTriggered = false;
        _lowReadings = 0;

        bool scheduled;
        lock (_statusLock) scheduled = _status.ShutdownScheduled;
        if (scheduled && settings.CancelShutdownWhenAcReturns)
        {
            var cancelled = CancelShutdownInternal("检测到电源恢复，已取消关机");
            if (!cancelled) return;
        }
        if (!GetStatus().ShutdownScheduled && !_powerCountdown.Committed && settings.ResumeRecordingWhenAcReturns && _stoppedByProtection &&
            !_manualStopRequested && !_recordingOperations.IsBusy && Volatile.Read(ref _safetySequenceActive) == 0)
        {
            _stoppedByProtection = false;
            TrackBackground(StartRecordingCommandAsync(token));
        }
        if (!scheduled) _protectionTriggered = false;
    }

    private async Task ExecuteSafetyStopAsync(string reason, bool emergency, bool takePowerAction, bool dryRun, CancellationToken lifetimeToken)
    {
        if (Interlocked.Exchange(ref _safetySequenceActive, 1) == 1) return;
        Interlocked.Increment(ref _stopGeneration);
        try
        {
            using var operation = await _recordingOperations.EnterStopAsync(lifetimeToken);
            var settings = GetSettings();
            SetPhase(GuardPhase.Stopping, "正在安全停止录像");
            _logger.Critical("safety", "启动安全停止流程", new { reason, emergency, dryRun });

            if (!_obs.IsConnected)
            {
                _nextConnectAttempt = DateTimeOffset.MinValue;
                await EnsureObsConnectedAsync(settings, lifetimeToken);
            }
            var snapshot = await _obs.GetSnapshotAsync(lifetimeToken);
            if (!snapshot.IsConfirmedStopped)
            {
                var stopResult = await _obs.StopRecordingAsync(lifetimeToken);
                if (stopResult.Success)
                    _logger.Info("safety", "OBS 已接受停止录像指令");
                else
                    _logger.Error("safety", "OBS 停止指令失败，将继续执行兜底流程", new { stopResult.Message });

                var timeoutAt = DateTimeOffset.Now.AddSeconds(settings.ObsStopTimeoutSeconds);
                while (DateTimeOffset.Now < timeoutAt && !lifetimeToken.IsCancellationRequested)
                {
                    await Task.Delay(500, lifetimeToken);
                    if (!_obs.IsConnected) await EnsureObsConnectedAsync(settings, lifetimeToken);
                    snapshot = await _obs.GetSnapshotAsync(lifetimeToken);
                    UpdateObs(snapshot);
                    if (snapshot.IsConfirmedStopped) break;
                }
                if (!snapshot.IsConfirmedStopped)
                    _logger.Error("safety", "无法确认 OBS 已停止，不能确认录像保存完成");
                else
                    _logger.Info("safety", "已确认 OBS 录像停止", new { snapshot.OutputPath });
            }
            else
            {
                _logger.Info("safety", "当前没有正在进行的录像");
            }

            _stoppedByProtection = !dryRun;
            if (!snapshot.IsConfirmedStopped)
            {
                SetError("无法确认录像停止和保存；普通关机已阻止，紧急电量仍按兜底策略处理");
                if (!dryRun && takePowerAction && emergency) ScheduleEmergencyShutdown(settings);
                return;
            }
            SetPhase(GuardPhase.Finalizing, "正在等待录像文件写入磁盘");
            if (settings.FileFlushWaitSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(settings.FileFlushWaitSeconds), lifetimeToken);
            _logger.Info("safety", "录像文件落盘等待完成");

            if (dryRun || !takePowerAction)
            {
                SetPhase(GuardPhase.Idle, dryRun ? "安全演练完成（未关机）" : "已安全停止录像");
                SetLastAction(dryRun ? "安全演练完成，未执行关机" : reason);
                return;
            }

            if (emergency && settings.EmergencyAlwaysShutdown)
            {
                ScheduleEmergencyShutdown(settings);
                return;
            }

            if (settings.CancelShutdownWhenAcReturns && _readBattery().IsOnAcPower)
            {
                SetPhase(GuardPhase.Idle, "录像已停止；电源已恢复，不安排电源操作");
                return;
            }
            await ScheduleConfiguredPowerActionAsync(settings, lifetimeToken);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetError("安全停止流程异常：" + ex.Message);
            _logger.Critical("safety", "安全停止流程异常", new { ex.Message, ex.StackTrace });
        }
        finally
        {
            Interlocked.Exchange(ref _safetySequenceActive, 0);
        }
    }

    private Task ScheduleConfiguredPowerActionAsync(AppSettings settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (settings.AfterStopAction == PostRecordingAction.None)
        {
            if (!GetStatus().ShutdownScheduled) SetPhase(GuardPhase.Idle, "录像已停止，不执行电源操作");
            return Task.CompletedTask;
        }
        SchedulePower(settings.AfterStopAction, settings.ShutdownDelaySeconds, settings.ForceCloseAppsOnShutdown, false);
        return Task.CompletedTask;
    }

    private void ScheduleEmergencyShutdown(AppSettings settings)
    {
        if (!settings.EmergencyAlwaysShutdown) return;
        if (settings.CancelShutdownWhenAcReturns && _readBattery().IsOnAcPower) return;
        SchedulePower(PostRecordingAction.Shutdown, settings.EmergencyShutdownDelaySeconds, settings.ForceCloseAppsOnEmergency, true);
    }

    private void SchedulePower(PostRecordingAction action, int seconds, bool force, bool emergency)
    {
        lock (_statusLock)
        {
            if (_applicationExiting) return;
            var pending = _powerCountdown.Schedule(action, DateTimeOffset.Now.AddSeconds(seconds), force, emergency);
            if (pending is null) return;
            var text = pending.Emergency ? "紧急关机倒计时（守护程序管理）" : pending.Action == PostRecordingAction.Hibernate ? "休眠倒计时（守护程序管理）" : "关机倒计时（守护程序管理）";
            _status.ShutdownScheduled = true;
            _status.ShutdownAt = pending.DueAt;
            _status.Phase = GuardPhase.ShutdownScheduled;
            _status.PhaseText = text;
            _status.LastAction = text;
        }
        _logger.Critical("power", "电源倒计时已安排或升级", new { action, seconds, force, emergency });
    }

    private bool CancelShutdownInternal(string reason)
    {
        lock (_statusLock)
        {
            if (!_powerCountdown.Cancel()) return false;
            _status.ShutdownScheduled = false;
            _status.ShutdownAt = null;
            _status.Phase = GuardPhase.Idle;
            _status.PhaseText = reason;
            _status.LastAction = reason;
        }
        _logger.Info("power", reason);
        return true;
    }

    private void ExecuteDuePowerAction()
    {
        if (_powerCountdown.Pending is not { } candidate || candidate.DueAt > DateTimeOffset.Now) return;
        if (GetSettings().CancelShutdownWhenAcReturns && _readBattery().IsOnAcPower)
        {
            CancelShutdownInternal("电源操作提交前检测到来电，倒计时已取消");
            return;
        }
        PendingPowerAction? pending;
        lock (_statusLock)
        {
            if (_applicationExiting) return;
            pending = _powerCountdown.TryCommit(DateTimeOffset.Now);
            if (pending is null) return;
            _status.ShutdownScheduled = false;
            _status.ShutdownAt = null;
            _status.PhaseText = "正在向 Windows 提交电源操作，此阶段不能取消";
        }
        string error;
        // /t 0 avoids Windows implicitly enabling /f for a positive shutdown timeout.
        var success = _power.Execute(pending.Action, pending.Force, out error);
        if (!success)
        {
            _powerCountdown.ReportExecutionFailure();
            SetError("Windows 电源操作提交失败：" + error);
        }
        _logger.Critical("power", success ? "Windows 已接受电源操作" : "Windows 电源操作失败", new { pending.Action, pending.Force, error });
    }

    public async Task<IpcResponse> HandleCommandAsync(IpcRequest request, CancellationToken token)
    {
        var command = request.Command.Trim().ToLowerInvariant();
        if (command == "get_status") return IpcResponse.Ok("状态读取成功", GetStatus());
        if (_applicationExit.IsCancellationRequested) return IpcResponse.Fail("软件正在退出，不再接受操作", GetStatus());
        using var commandLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _applicationExit.Token);
        token = commandLifetime.Token;
        if (command == "stop_record") return await StopRecordingCommandAsync(token);
        if (command == "cancel_shutdown") return CancelShutdownCommand();

        var stopGeneration = Interlocked.Read(ref _stopGeneration);
        await _commandLock.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (command is "start_record" or "split_record" && stopGeneration != Interlocked.Read(ref _stopGeneration))
                return IpcResponse.Fail("等待期间收到停止请求，已取消先前排队的录制操作", GetStatus());
            return command switch
            {
                "reload_settings" => ReloadSettingsCommand(),
                "connect_obs" => await ConnectCommandAsync(token),
                "connect_obs_launch" => await ConnectCommandAsync(token, true),
                "refresh_record_directory" => await RefreshRecordDirectoryCommandAsync(token),
                "start_record" => await StartRecordingCommandAsync(token),
                "stop_record" => await StopRecordingCommandAsync(token),
                "split_record" => await SplitRecordingCommandAsync(token),
                "cancel_shutdown" => CancelShutdownCommand(),
                "safety_test" => SafetyTestCommand(token),
                "self_check" => await SelfCheckCommandAsync(token),
                "inspect_mkv" => await InspectMkvCommandAsync(token),
                "ensure_mkv" => EnsureMkvCommand(),
                _ => IpcResponse.Fail("未知命令：" + request.Command, GetStatus())
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private IpcResponse ReloadSettingsCommand()
    {
        ReloadSettings();
        return IpcResponse.Ok(_reconnectPending ? "配置已应用；连接参数将在当前录制操作结束后重连，既有电源倒计时保持不变"
            : "配置已应用；修改分割时间后从应用时重新计时，既有电源倒计时保持不变", GetStatus());
    }

    private async Task<IpcResponse> ConnectCommandAsync(CancellationToken token, bool launchIfNeeded = false)
    {
        var settings = GetSettings();
        var waitForStartup = !_obs.IsConnected && ObsProcessLauncher.IsLocalHost(settings.ObsHost) &&
            (settings.AutoLaunchObs || launchIfNeeded) &&
            (!_obsLauncher.IsRunning || DateTimeOffset.Now - _lastObsLaunchAt < TimeSpan.FromSeconds(30));
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (waitForStartup) startupTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                _nextConnectAttempt = DateTimeOffset.MinValue;
                await EnsureObsConnectedAsync(settings, startupTimeout.Token, launchIfNeeded);
                if (!_obs.IsConnected) startupTimeout.Token.ThrowIfCancellationRequested();
                if (_obs.IsConnected || !waitForStartup || DateTimeOffset.Now - _lastObsLaunchAt >= TimeSpan.FromSeconds(30)) break;
                await Task.Delay(1000, startupTimeout.Token);
            } while (true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            var message = "已等待 OBS 启动 30 秒，但尚未连接。请检查 OBS 是否弹出启动提示，并确认 WebSocket 已启用、端口和密码正确。";
            SetLastError(message);
            return IpcResponse.Fail(message, GetStatus());
        }
        if (_obs.IsConnected)
        {
            var snapshot = await RefreshObsStateAsync(null, token);
            SetPhase(snapshot.IsRecording ? GuardPhase.Recording : GuardPhase.Idle,
                snapshot.IsRecording ? "录像保护中" : "OBS 已连接，等待录像");
        }
        var status = GetStatus();
        return _obs.IsConnected ? IpcResponse.Ok("OBS 已连接", status) : IpcResponse.Fail(status.LastError, status);
    }

    private async Task<IpcResponse> RefreshRecordDirectoryCommandAsync(CancellationToken token)
    {
        var settings = GetSettings();
        _nextConnectAttempt = DateTimeOffset.MinValue;
        await EnsureObsConnectedAsync(settings, token);
        if (!_obs.IsConnected) return IpcResponse.Fail(GetStatus().LastError, GetStatus());

        var directory = await _obs.GetRecordDirectoryAsync(token);
        var snapshot = await _obs.GetSnapshotAsync(token);
        UpdateObs(snapshot);
        if (string.IsNullOrWhiteSpace(directory))
            return IpcResponse.Fail("OBS 已连接，但没有返回录像保存目录", GetStatus());

        _logger.Info("obs", "已主动刷新 OBS 录像目录", new { 录像目录 = directory });
        return IpcResponse.Ok("已读取录像目录：" + directory, GetStatus());
    }

    private async Task<IpcResponse> StartRecordingCommandAsync(CancellationToken token)
    {
        if (_reconnectPending) return IpcResponse.Fail("连接参数正在切换，请等待重连完成后开始录像", GetStatus());
        using var operation = _recordingOperations.TryEnter(token);
        if (operation is null || Volatile.Read(ref _safetySequenceActive) != 0 || GetStatus().ShutdownScheduled || _powerCountdown.Committed)
            return IpcResponse.Fail("录制操作或保护流程正在运行，不能开始录像", GetStatus());
        token = operation.Token;
        var battery = _readBattery();
        var settings = GetSettings();
        if (!CanStartForBattery(battery, settings, out var reason)) return IpcResponse.Fail(reason, GetStatus());
        if (!_obs.IsConnected)
        {
            var connected = await ConnectCommandAsync(token);
            if (!connected.Success) return connected;
        }
        if (settings.RequireMkv)
        {
            var info = await _obs.InspectFormatAsync(token);
            if (!info.Found || !info.IsMkv)
                return IpcResponse.Fail(info.Found ? $"已阻止开始录像：{info.Message}。请先设置为 MKV，或关闭“强制 MKV 检查”。" : "无法验证 OBS 录像格式；请检查配置或关闭“强制 MKV 检查”。", GetStatus());
        }
        var disk = await CheckDiskSpaceAsync(GetStatus().Obs, settings, token);
        if (!disk.Success) return IpcResponse.Fail(disk.Message, GetStatus());
        token.ThrowIfCancellationRequested();
        var result = await StartRecordingCoreAsync(token);
        return result.Success ? IpcResponse.Ok(result.Message, GetStatus()) : IpcResponse.Fail(result.Message, GetStatus());
    }

    private async Task<ObsRequestResult> StartRecordingCoreAsync(CancellationToken token)
    {
        var snapshot = await _obs.GetSnapshotAsync(token);
        if (snapshot.IsRecording) return new ObsRequestResult(true, "OBS 已在录像");
        token.ThrowIfCancellationRequested();
        var result = await _obs.StartRecordingAsync(token);
        if (result.Success)
        {
            _logger.Info("recording", "已发送开始录像指令");
            SetLastAction("开始录像");
            _protectionTriggered = false;
            _stoppedByProtection = false;
            _manualStopRequested = false;
            var refreshed = await RefreshObsStateAsync(true, token);
            if (!refreshed.IsRecordingStateKnown || !refreshed.IsRecording)
                return new ObsRequestResult(false, "开始指令已发送，但尚未确认正在录像；请检查当前状态，不要连续重复点击");
            SetLastError(string.Empty);
            var settings = GetSettings();
            _nextSplitAt = refreshed.IsRecording && settings.EnableAutomaticSplit
                ? DateTimeOffset.Now.AddMinutes(settings.SplitMinutes)
                : null;
            lock (_statusLock) _status.NextSplitAt = _nextSplitAt;
            SetPhase(refreshed.IsRecording ? GuardPhase.Recording : GuardPhase.Idle,
                refreshed.IsRecording ? "录像保护中" : "OBS 已接受开始指令，正在等待状态更新");
        }
        else
        {
            _logger.Error("recording", "开始录像失败", new { result.Message });
            SetLastError(result.Message);
        }
        return result with { Message = result.Success ? "开始录像成功" : result.Message };
    }

    private async Task<IpcResponse> StopRecordingCommandAsync(CancellationToken token)
    {
        Interlocked.Increment(ref _stopGeneration);
        _manualStopRequested = true;
        using var operation = await _recordingOperations.EnterStopAsync(token);
        _autoStartAttempted = true;
        var result = await StopRecordingCoreAsync(token);
        return result.Success ? IpcResponse.Ok(result.Message, GetStatus()) : IpcResponse.Fail(result.Message, GetStatus());
    }

    private async Task<ObsRequestResult> StopRecordingCoreAsync(CancellationToken token)
    {
        if (!_obs.IsConnected)
        {
            _nextConnectAttempt = DateTimeOffset.MinValue;
            await EnsureObsConnectedAsync(GetSettings(), token);
        }
        var snapshot = await _obs.GetSnapshotAsync(token);
        if (snapshot.IsConfirmedStopped) return new ObsRequestResult(true, "已确认当前没有正在进行的录像");
        SetPhase(GuardPhase.Stopping, "正在停止录像并等待 OBS 确认");
        var result = await _obs.StopRecordingAsync(token);
        if (result.Success)
        {
            _logger.Info("recording", "已手动停止录像");
            SetLastAction("停止录像");
            var settings = GetSettings();
            var refreshed = await WaitForRecordingStateAsync(false, TimeSpan.FromSeconds(settings.ObsStopTimeoutSeconds), token);
            if (!refreshed.IsConfirmedStopped)
            {
                SetError("停止确认超时或 OBS 状态未知，不能确认录像已保存");
                return new ObsRequestResult(false, GetStatus().LastError);
            }
            SetPhase(GuardPhase.Finalizing, "OBS 已停止，正在等待文件写入");
            await Task.Delay(TimeSpan.FromSeconds(settings.FileFlushWaitSeconds), token);
            SetLastError(string.Empty);
            _nextSplitAt = null;
            lock (_statusLock) _status.NextSplitAt = null;
            SetPhase(refreshed.IsRecording ? GuardPhase.Stopping : GuardPhase.Idle,
                refreshed.IsRecording ? "OBS 已接受停止指令，正在等待状态更新" : "录像已停止并保存");
        }
        else
        {
            _logger.Error("recording", "停止录像失败", new { result.Message });
            SetLastError(result.Message);
        }
        return result with { Message = result.Success ? "停止录像成功" : result.Message };
    }

    private async Task<IpcResponse> SplitRecordingCommandAsync(CancellationToken token)
    {
        var settings = GetSettings();
        var result = await SplitRecordingCoreAsync(settings, token);
        if (result.Success)
        {
            settings = GetSettings();
            _nextSplitAt = settings.EnableAutomaticSplit ? DateTimeOffset.Now.AddMinutes(settings.SplitMinutes) : null;
            lock (_statusLock) _status.NextSplitAt = _nextSplitAt;
        }
        return result.Success ? IpcResponse.Ok(result.Message, GetStatus()) : IpcResponse.Fail(result.Message, GetStatus());
    }

    private async Task<ObsRequestResult> SplitRecordingCoreAsync(AppSettings settings, CancellationToken token)
    {
        using var operation = _recordingOperations.TryEnter(token);
        if (operation is null) return new ObsRequestResult(false, "其他录制操作正在运行，分割已跳过");
        token = operation.Token;
        if (Interlocked.CompareExchange(ref _safetySequenceActive, 0, 0) != 0)
            return new ObsRequestResult(false, "安全停止流程正在运行，不能分割录像");
        if (Interlocked.CompareExchange(ref _splitInProgress, 1, 0) != 0)
            return new ObsRequestResult(false, "另一个录像分割任务正在运行");

        try
        {
            var snapshot = await _obs.GetSnapshotAsync(token);
            UpdateObs(snapshot);
            if (!snapshot.IsRecordingStateKnown || !snapshot.IsRecording) return new ObsRequestResult(false, "当前未确认正在录像，不能分割");

            token.ThrowIfCancellationRequested();
            SetPhase(GuardPhase.Recording, "正在分割录像文件");
            var nativeSplit = await _obs.SplitRecordingAsync(token);
            if (nativeSplit.Success)
            {
                _logger.Info("recording", "OBS 原生录像分割完成");
                SetLastAction("录像文件已分割");
                SetPhase(GuardPhase.Recording, "录像保护中");
                return nativeSplit with { Message = "录像文件已分割" };
            }
            if (!settings.AllowStopStartSplitFallback)
            {
                var message = "OBS 原生分割失败：" + nativeSplit.Message;
                _logger.Error("recording", message, new { nativeSplit.StatusCode });
                SetPhase(GuardPhase.Recording, "录像保护中");
                return nativeSplit with { Message = message };
            }

            _logger.Warning("recording", "OBS 原生分割不可用，开始兼容分割", new
            {
                OBS错误 = nativeSplit.Message,
                nativeSplit.StatusCode,
                停止后等待毫秒 = settings.SplitFallbackDelayMilliseconds,
                重启超时秒 = settings.SplitRestartTimeoutSeconds
            });

            var stop = await _obs.StopRecordingAsync(token);
            if (!stop.Success)
            {
                var message = "兼容分割无法停止旧文件：" + stop.Message;
                _logger.Error("recording", message, new { stop.StatusCode });
                SetPhase(GuardPhase.Recording, "录像保护中");
                return new ObsRequestResult(false, message, StatusCode: stop.StatusCode);
            }

            SetPhase(GuardPhase.Finalizing, "正在等待旧录像文件完成保存");
            var stopped = await WaitForRecordingStateAsync(false, TimeSpan.FromSeconds(settings.ObsStopTimeoutSeconds), token);
            if (!stopped.IsConfirmedStopped)
            {
                var message = $"OBS 在 {settings.ObsStopTimeoutSeconds} 秒内没有确认旧录像已停止；为避免重复指令，未启动新文件";
                _logger.Error("recording", message);
                SetLastError(message);
                return new ObsRequestResult(false, message);
            }
            if (!stopped.IsConnected)
                _logger.Warning("recording", "旧录像停止后 OBS WebSocket 已断开，将在重启录像时尝试重连");
            else
                _logger.Info("recording", "已确认旧录像停止并完成当前状态切换");

            await Task.Delay(settings.SplitFallbackDelayMilliseconds, token);
            SetPhase(GuardPhase.Recording, "正在启动新的录像分段");
            var restartDeadline = DateTimeOffset.Now.AddSeconds(settings.SplitRestartTimeoutSeconds);
            var restartAttempts = 0;
            var lastStart = new ObsRequestResult(false, "尚未发送开始录像请求");

            while (DateTimeOffset.Now < restartDeadline && !token.IsCancellationRequested)
            {
                restartAttempts++;
                if (!_obs.IsConnected)
                {
                    _nextConnectAttempt = DateTimeOffset.MinValue;
                    await EnsureObsConnectedAsync(settings, token);
                }

                if (_obs.IsConnected)
                {
                    token.ThrowIfCancellationRequested();
                    lastStart = await _obs.StartRecordingAsync(token);
                    var confirmationWindow = lastStart.Success ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(500);
                    var started = await WaitForRecordingStateAsync(true, confirmationWindow, token);
                    if (started.IsRecording)
                    {
                        _logger.Info("recording", "兼容分割完成，新录像已确认启动", new
                        {
                            重启尝试次数 = restartAttempts,
                            停止后等待毫秒 = settings.SplitFallbackDelayMilliseconds,
                            新录像文件 = started.OutputPath
                        });
                        SetLastAction("兼容分割完成，新录像已启动");
                        SetPhase(GuardPhase.Recording, "录像保护中");
                        return new ObsRequestResult(true, restartAttempts == 1
                            ? "兼容分割完成，新录像已启动"
                            : $"兼容分割完成，第 {restartAttempts} 次尝试后新录像已启动");
                    }
                }

                var remaining = restartDeadline - DateTimeOffset.Now;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500, remaining.TotalMilliseconds)), token);
            }

            var failure = $"旧文件已安全保存，但在 {settings.SplitRestartTimeoutSeconds} 秒内未能启动新录像。最后错误：{lastStart.Message}";
            _logger.Critical("recording", "兼容分割重启录像失败", new
            {
                重启尝试次数 = restartAttempts,
                最后错误 = lastStart.Message,
                lastStart.StatusCode
            });
            SetError(failure);
            return new ObsRequestResult(false, failure, StatusCode: lastStart.StatusCode);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new ObsRequestResult(false, "分割已被停止操作或保护流程取消，未继续重启录像");
        }
        finally
        {
            Interlocked.Exchange(ref _splitInProgress, 0);
        }
    }

    private IpcResponse CancelShutdownCommand()
    {
        if (_powerCountdown.Committed) return IpcResponse.Fail("电源操作已提交 Windows，无法再由本软件取消", GetStatus());
        if (!GetStatus().ShutdownScheduled) return IpcResponse.Ok("当前没有关机计划", GetStatus());
        if (!CancelShutdownInternal("已手动取消关机")) return IpcResponse.Fail("电源操作已提交，取消失败", GetStatus());
        return IpcResponse.Ok("关机已取消", GetStatus());
    }

    private IpcResponse SafetyTestCommand(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _safetySequenceActive, 0, 0) == 1)
            return IpcResponse.Fail("安全流程正在运行，请稍候", GetStatus());
        if (GetStatus().ShutdownScheduled)
            return IpcResponse.Fail("已有电源计划，不能开始演练；请先检查并取消倒计时", GetStatus());
        TrackBackground(ExecuteSafetyStopAsync("用户启动安全演练", false, false, true, token));
        return IpcResponse.Ok("演练已启动，会停止录像；演练本身不关机，真实低电量保护仍然有效", GetStatus());
    }

    private async Task<IpcResponse> SelfCheckCommandAsync(CancellationToken token)
    {
        var settings = GetSettings();
        var battery = _readBattery();
        var obs = await PollObsAsync(token);
        var format = await _obs.InspectFormatAsync(token);
        var disk = await CheckDiskSpaceAsync(obs, settings, token);
        var lines = new List<string>
        {
            battery.IsAvailable ? $"✓ 电池读取正常：{battery.Percent}%" : "✕ 无法读取电池状态",
            obs.IsConnected ? $"✓ OBS 已连接：{obs.Version}" : "✕ OBS 未连接",
            format.IsMkv ? $"✓ 录像格式为 MKV（{format.ProfileName}）" : $"! MKV 检查：{format.Message}",
            disk.Success ? "✓ 录像磁盘空间满足要求" : "✕ " + disk.Message,
            settings.EnableAutomaticSplit ? $"配置：自动分割每 {settings.SplitMinutes} 分钟（未实测分割能力）" : "! 自动分割已关闭",
            settings.BatteryProtectionEnabled ? $"✓ 电池保护：{settings.StopBatteryPercent}% 停止 / {settings.EmergencyBatteryPercent}% 紧急" : "✕ 电池保护已关闭",
            settings.AutoStartGuardianWithWindows ? "配置：随 Windows 启动已勾选（未验证启动项）" : "! 后台守护未设置自启动",
            "待人工检查：录像目录写入权限和实机录制/分割；此检查不等于无人值守验收"
        };
        var powerChecks = WindowsPowerPolicy.Evaluate(WindowsPowerPolicy.Read(), settings);
        lines.AddRange(powerChecks);
        var success = battery.IsAvailable && obs.IsRecordingStateKnown && (!settings.RequireMkv || format.IsMkv) && disk.Success && settings.BatteryProtectionEnabled
            && !powerChecks.Any(line => line.StartsWith("风险") || line.StartsWith("待确认"));
        _logger.Info("diagnostic", "已执行系统自检", new { success, lines });
        return success ? IpcResponse.Ok(string.Join(Environment.NewLine, lines), GetStatus()) : IpcResponse.Fail(string.Join(Environment.NewLine, lines), GetStatus());
    }

    private async Task<IpcResponse> InspectMkvCommandAsync(CancellationToken token)
    {
        var info = _obs.IsConnected ? await _obs.InspectFormatAsync(token)
            : ObsProfileManager.Inspect(GetStatus().Obs.CurrentProfile, GetSettings().ObsConfigDirectory);
        if (!_obs.IsConnected) info = info with { Message = "离线配置检查（不是运行状态）：" + info.Message };
        return info.IsMkv ? IpcResponse.Ok(info.Message, GetStatus()) : IpcResponse.Fail(info.Message, GetStatus());
    }

    private IpcResponse EnsureMkvCommand()
    {
        var info = ObsProfileManager.EnsureMkv(GetStatus().Obs.CurrentProfile, GetSettings().ObsConfigDirectory);
        if (info.IsMkv)
        {
            _logger.Info("configuration", info.Message);
            return IpcResponse.Ok(info.Message, GetStatus());
        }
        return IpcResponse.Fail(info.Message, GetStatus());
    }

    private async Task<(bool Success, string Message)> CheckDiskSpaceAsync(ObsSnapshot obs, AppSettings settings, CancellationToken token)
    {
        try
        {
            var directory = !string.IsNullOrWhiteSpace(obs.OutputPath)
                ? Path.GetDirectoryName(obs.OutputPath)
                : obs.RecordDirectory;
            if (string.IsNullOrWhiteSpace(directory)) directory = await _obs.GetRecordDirectoryAsync(token);
            if (string.IsNullOrWhiteSpace(directory)) return (false, "无法从 OBS 获取录像目录，不能确认磁盘剩余空间");
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrWhiteSpace(root)) return (true, "无法识别录像磁盘，跳过检查");
            var drive = new DriveInfo(root);
            var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            return freeGb >= settings.MinimumDiskFreeGb
                ? (true, $"磁盘剩余 {freeGb:F1} GB")
                : (false, $"录像磁盘空间不足：剩余 {freeGb:F1} GB，要求至少 {settings.MinimumDiskFreeGb:F1} GB");
        }
        catch (Exception ex)
        {
            return (false, "检查录像磁盘失败：" + ex.Message);
        }
    }

    private async Task<ObsSnapshot> RefreshObsStateAsync(bool? expectedRecordingState, CancellationToken token)
    {
        ObsSnapshot snapshot = new(false, false, false, TimeSpan.Zero, 0, string.Empty, string.Empty, string.Empty, "OBS 未连接");
        var attempts = expectedRecordingState.HasValue ? 12 : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            snapshot = await _obs.GetSnapshotAsync(token);
            UpdateObs(snapshot);
            if (!expectedRecordingState.HasValue || (snapshot.IsRecordingStateKnown && snapshot.IsRecording == expectedRecordingState.Value))
                return snapshot;
            if (attempt < attempts - 1) await Task.Delay(100, token);
        }
        return snapshot;
    }

    private async Task<ObsSnapshot> WaitForRecordingStateAsync(bool expectedRecordingState, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTimeOffset.Now + timeout;
        ObsSnapshot snapshot;
        do
        {
            if (!_obs.IsConnected) await EnsureObsConnectedAsync(GetSettings(), token);
            snapshot = await _obs.GetSnapshotAsync(token);
            UpdateObs(snapshot);
            if (snapshot.IsRecordingStateKnown && snapshot.IsRecording == expectedRecordingState)
                return snapshot;

            var remaining = deadline - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero) return snapshot;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(200, remaining.TotalMilliseconds)), token);
        }
        while (true);
    }

    private static bool CanStartForBattery(BatterySnapshot battery, AppSettings settings, out string reason)
    {
        if (settings.BatteryProtectionEnabled && settings.FailSafeWhenBatteryUnknown && !battery.IsAvailable && !battery.IsOnAcPower)
        {
            reason = "无法读取电池状态，已按电池异常保护设置阻止开始录像";
            return false;
        }
        if (settings.BatteryProtectionEnabled && battery.IsAvailable && !battery.IsOnAcPower && battery.Percent < settings.MinimumStartBatteryPercent)
        {
            reason = $"当前电量 {battery.Percent}%，低于允许开始录像的 {settings.MinimumStartBatteryPercent}%";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private void ApplySleepPolicy(ObsSnapshot obs, AppSettings settings)
    {
        if (obs.IsRecording)
            _sleepInhibitor.Set(settings.PreventSystemSleepWhileRecording, settings.KeepDisplayAwakeWhileRecording);
        else if (obs.IsConfirmedStopped)
            _sleepInhibitor.Clear();
    }

    private void LogBatteryWhenDue(BatterySnapshot battery, AppSettings settings)
    {
        if (DateTimeOffset.Now < _nextBatteryLog) return;
        _nextBatteryLog = DateTimeOffset.Now.AddSeconds(settings.BatteryLogIntervalSeconds);
        _logger.Info("battery", battery.Description, new { battery.Percent, battery.IsOnAcPower, battery.IsCharging, battery.RemainingMinutes });
    }

    private void LogEffectiveConfiguration(AppSettings settings)
    {
        _logger.Info("settings", "当前生效的无人值守保护配置", new
        {
            OBS地址 = $"ws://{settings.ObsHost}:{settings.ObsPort}",
            OBS已配置密码 = !string.IsNullOrEmpty(settings.ObsPassword),
            OBS程序 = settings.ObsExecutablePath,
            自动连接OBS = settings.AutoConnectObs,
            自动启动OBS = settings.AutoLaunchObs,
            电池保护 = settings.BatteryProtectionEnabled,
            预警电量百分比 = settings.WarningBatteryPercent,
            停止录像电量百分比 = settings.StopBatteryPercent,
            紧急关机电量百分比 = settings.EmergencyBatteryPercent,
            允许开始录像最低电量百分比 = settings.MinimumStartBatteryPercent,
            电池检测间隔秒 = settings.BatteryPollSeconds,
            OBS状态同步毫秒 = settings.ObsStatusPollMilliseconds,
            界面刷新毫秒 = settings.StatusRefreshMilliseconds,
            强制验证MKV = settings.RequireMkv,
            自动分割 = settings.EnableAutomaticSplit,
            分割间隔分钟 = settings.SplitMinutes,
            兼容分割停止后等待毫秒 = settings.SplitFallbackDelayMilliseconds,
            兼容分割重启超时秒 = settings.SplitRestartTimeoutSeconds,
            最低磁盘空间GB = settings.MinimumDiskFreeGb,
            OBS停止超时秒 = settings.ObsStopTimeoutSeconds,
            文件落盘等待秒 = settings.FileFlushWaitSeconds,
            录像时阻止睡眠 = settings.PreventSystemSleepWhileRecording,
            停止后动作 = settings.AfterStopAction switch
            {
                PostRecordingAction.Shutdown => "关闭 Windows",
                PostRecordingAction.Hibernate => "休眠",
                _ => "仅停止录像"
            },
            正常关机延时秒 = settings.ShutdownDelaySeconds,
            紧急关机延时秒 = settings.EmergencyShutdownDelaySeconds,
            接通电源取消关机 = settings.CancelShutdownWhenAcReturns,
            随Windows启动 = settings.AutoStartGuardianWithWindows,
            易读日志文件 = _logger.CurrentLogPath,
            结构化日志文件 = _logger.StructuredLogPath
        });
    }

    private void UpdateBattery(BatterySnapshot battery)
    {
        lock (_statusLock)
        {
            _status.Battery = battery;
            _status.Timestamp = DateTimeOffset.Now;
        }
    }

    private void UpdateObs(ObsSnapshot obs)
    {
        lock (_statusLock)
        {
            _status.Obs = obs;
            if (obs.IsRecordingStateKnown) _status.ObsUpdatedAt = DateTimeOffset.Now;
            _status.Timestamp = DateTimeOffset.Now;
            if (!string.IsNullOrWhiteSpace(obs.Error))
            {
                _lastObsStateError = obs.Error;
                _status.LastError = obs.Error;
            }
            else if (_status.LastError == _lastObsStateError)
            {
                _status.LastError = string.Empty;
                _lastObsStateError = string.Empty;
            }
        }
    }

    private void SetPhase(GuardPhase phase, string text)
    {
        lock (_statusLock)
        {
            _status.Phase = phase;
            _status.PhaseText = text;
            _status.Timestamp = DateTimeOffset.Now;
        }
    }

    private void SetLastAction(string text)
    {
        lock (_statusLock) _status.LastAction = text;
    }

    private void SetLastError(string text)
    {
        lock (_statusLock) _status.LastError = text;
    }

    private void SetError(string text)
    {
        lock (_statusLock)
        {
            _status.Phase = GuardPhase.Error;
            _status.PhaseText = text;
            _status.LastError = text;
        }
    }

    public IpcResponse RequestApplicationExit()
    {
        lock (_statusLock)
        {
            if (_applicationExiting) return IpcResponse.Ok("软件正在退出", GetStatus());
            _applicationExiting = true;
            if (_powerCountdown.Cancel())
            {
                _status.ShutdownScheduled = false;
                _status.ShutdownAt = null;
            }
            _status.ProtectionEnabled = false;
            _nextSplitAt = null;
            _status.NextSplitAt = null;
            _status.PhaseText = "用户确认退出，正在关闭全部功能";
        }
        // Cancel outside the status lock: continuations may update status while unwinding.
        try { _applicationExit.Cancel(); }
        catch (AggregateException ex) { _logger.Error("lifecycle", "退出时部分任务取消回调异常，将继续关闭后台", new { ex.Message }); }
        _logger.Critical("lifecycle", "用户确认退出整个软件；取消未提交电源计划和后台任务，OBS 本身保持运行");
        return IpcResponse.Ok("已接受退出请求，等待后台进程结束", GetStatus());
    }

    public async ValueTask DisposeAsync()
    {
        _powerCountdown.Cancel();
        _sleepInhibitor.Dispose();
        await _obs.DisposeAsync();
        _commandLock.Dispose();
        _connectionLock.Dispose();
        _recordingOperations.Dispose();
        _applicationExit.Dispose();
    }
}
