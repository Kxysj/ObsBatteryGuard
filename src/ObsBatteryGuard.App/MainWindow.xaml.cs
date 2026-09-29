using Microsoft.Win32;
using ObsBatteryGuard.Core;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ObsBatteryGuard.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _statusTimer = new();
    private AppSettings _settings = new();
    private GuardianStatus? _latestStatus;
    private string _guardianExecutable = string.Empty;
    private bool _refreshing;
    private readonly HashSet<Button> _pendingButtons = new();
    private readonly IncrementalLogReader _logReader = new();
    private bool _readingLogs;
    private string _logContents = string.Empty;
    private bool _loadingControls;
    private bool _settingsDirty;
    private string _preflightReport = string.Empty;
    private bool _previewMode;
    private bool _windowInitialized;
    private OwnedProcess? _ownedGuardian;
    private string _ownershipFailure = string.Empty;
    private readonly List<OwnedProcess> _adoptedGuardians = new();
    internal void DisposeOwnedGuardian()
    {
        _ownedGuardian?.Dispose(); _ownedGuardian = null;
        foreach (var guardian in _adoptedGuardians) guardian.Dispose();
        _adoptedGuardians.Clear();
    }

    public MainWindow() : this(new SettingsStore()) { }

    private MainWindow(SettingsStore store)
    {
        _store = store;
        InitializeComponent();
        BuildSettingsForms();
        AppVersionText.Text = $"v{CurrentAppVersion}";
        _statusTimer.Tick += StatusTimer_Tick;
        SettingsPage.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(SettingsText_Changed));
        SettingsPage.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(SettingsControl_Changed));
        SettingsPage.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(SettingsControl_Changed));
        SettingsPage.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new SelectionChangedEventHandler(SettingsSelection_Changed));
        ObsPasswordBox.PasswordChanged += SettingsControl_Changed;
        SizeChanged += (_, _) => UpdateResponsiveLayout(ActualWidth);
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _lastRestoredState = WindowState;
            MaximizeWindowButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            MaximizeWindowButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        };
        Closed += (_, _) => { _statusTimer.Stop(); DisposeTray(); DisposeOwnedGuardian(); };
        ConfigureParameterHelp();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_previewMode || _windowInitialized) return;
        _windowInitialized = true;
        InitializeTray();
        _settings = _store.Load();
        LoadSettingsIntoControls(_settings);
        if (string.IsNullOrWhiteSpace(_store.LastLoadError)) AutoLocateObsIfNeeded();
        else SettingsNoticeText.Text = _store.LastLoadError + "；请先检查配置文件，再决定是否保存新配置。";
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        _guardianExecutable = FindGuardianExecutable();
        ConfigPathText.Text = _store.SettingsPath;
        GuardPathText.Text = string.IsNullOrWhiteSpace(_guardianExecutable) ? "未找到后台守护程序" : _guardianExecutable;
        _statusTimer.Interval = TimeSpan.FromMilliseconds(_settings.StatusRefreshMilliseconds);
        if (string.IsNullOrWhiteSpace(_store.LastLoadError))
        {
            var startup = StartupManager.Apply(_settings.AutoStartGuardianWithWindows, Environment.ProcessPath ?? "", "");
            if (!startup.Success) SettingsNoticeText.Text = startup.Message;
        }
        await EnsureGuardianRunningAsync();
        if (_exitRequested || _closeWorkflow) return;
        await RefreshStatusAsync();
        if (_exitRequested || _closeWorkflow) return;
        _statusTimer.Start();
        SafeTrayAction(tray => tray.AnnounceStartup());
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void StatusTimer_Tick(object? sender, EventArgs e) => await RefreshStatusAsync();

    private async Task EnsureGuardianRunningAsync()
    {
        if (_exitRequested || _closeWorkflow) return;
        var response = await GuardianIpcClient.SendAsync("get_status", 700);
        if (_exitRequested || _closeWorkflow) return;
        if (response.Success && response.Status?.GuardianProcessId == _ownedGuardian?.Process.Id && IsCurrentGuardianVersion(response.Status?.GuardianVersion)) return;
        if (response.Success || HasSessionGuardianProcesses())
        {
            // Legacy helpers may be left by old --background startup entries. Verify identities
            // before adoption; do not silently let an unowned helper control OBS after this UI exits.
            try
            {
                var targets = new WindowExitEnvironment(this).FindGuardians();
                if (targets.Count == 0 || response.Status?.GuardianProcessId > 0 && !targets.Any(t => t.Id == response.Status.GuardianProcessId))
                    throw new InvalidOperationException("未找到与通信状态匹配的本会话守护");
                foreach (var target in targets)
                    _adoptedGuardians.Add(OwnedProcess.Attach(target.Id, target.StartTicks, target.Path));
            }
            catch (Exception ex)
            {
                _ownershipFailure = "无法绑定已有后台：" + ex.Message;
                SetGuardianOffline(_ownershipFailure);
                MessageBox.Show(this, "无法将已有后台绑定到本界面：" + ex.Message + "\n请右键托盘图标退出，按提示结束旧后台后重开；在此之前不要认为后台已经停止。",
                    "需要处理旧后台", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SetGuardianOffline(response.Success
                ? "已有后台已绑定本界面寿命。若版本不一致，请右键托盘图标退出，再重新打开新版。"
                : "现有后台暂未响应，已保留保护进程，请稍后重试。");
            return;
        }
        if (string.IsNullOrWhiteSpace(_guardianExecutable) || !File.Exists(_guardianExecutable))
        {
            SetGuardianOffline("找不到后台守护程序");
            return;
        }

        try
        {
            using var owner = Process.GetCurrentProcess();
            _ownedGuardian = OwnedProcess.Start(_guardianExecutable,
                $"--background --owner-process {owner.Id} --owner-start {owner.StartTime.ToUniversalTime().Ticks}");
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (_exitRequested || _closeWorkflow) return;
                await Task.Delay(250);
                response = await GuardianIpcClient.SendAsync("get_status", 600);
                if (response.Success && IsCurrentGuardianVersion(response.Status?.GuardianVersion))
                {
                    return;
                }
            }
            SetGuardianOffline("当前版本后台守护启动超时");
        }
        catch (Exception ex)
        {
            SetGuardianOffline("启动失败：" + ex.Message);
        }
    }

    private static bool HasSessionGuardianProcesses()
    {
        var processes = GetSessionGuardianProcesses();
        try { return processes.Length > 0; }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static Process[] GetSessionGuardianProcesses()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        return Process.GetProcessesByName("ObsBatteryGuard.Guard")
            .Where(process =>
            {
                try { return process.SessionId == sessionId && !process.HasExited; }
                catch { process.Dispose(); return false; }
            })
            .ToArray();
    }

    private static string CurrentAppVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? string.Empty;

    private static bool IsCurrentGuardianVersion(string? guardianVersion)
    {
        if (!Version.TryParse(guardianVersion, out var running) || !Version.TryParse(CurrentAppVersion, out var expected)) return false;
        return running.Major == expected.Major && running.Minor == expected.Minor && running.Build == expected.Build;
    }

    private async Task RefreshStatusAsync()
    {
        if (_previewMode || _closeWorkflow || _exitRequested) return;
        if (!string.IsNullOrEmpty(_ownershipFailure)) { SetGuardianOffline(_ownershipFailure); return; }
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var response = await GuardianIpcClient.SendAsync("get_status", 2500);
            if (!response.Success || response.Status is null)
            {
                SetGuardianOffline(response.Message);
                return;
            }
            _latestStatus = response.Status;
            UpdateStatusUi(response.Status);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateStatusUi(GuardianStatus status)
    {
        UpdateTray(status);
        GuardianDot.Fill = (Brush)FindResource(IsCurrentGuardianVersion(status.GuardianVersion) ? "SuccessBrush" : "WarningBrush");
        GuardianStateText.Text = string.IsNullOrWhiteSpace(status.GuardianVersion) ? "运行中（旧版）" : $"运行中 v{status.GuardianVersion}";
        HeaderStatusText.Text = status.PhaseText;
        PhaseText.Text = status.PhaseText;
        PhaseIconText.Text = status.Phase is GuardPhase.Error or GuardPhase.Warning || !status.Obs.IsRecordingStateKnown ? "!"
            : status.RecordingOperationBusy ? "…" : status.Obs.IsRecording ? "●" : "✓";
        PhaseDetailText.Text = !string.IsNullOrWhiteSpace(status.LogWriteError) ? status.LogWriteError
            : string.IsNullOrWhiteSpace(status.LastError) ? status.LastAction : status.LastError;
        if (!IsCurrentGuardianVersion(status.GuardianVersion))
            PhaseDetailText.Text = "界面与后台版本不一致；右键托盘图标选择“退出软件”，再打开新版即可切换。";
        if (!string.IsNullOrWhiteSpace(status.SettingsError)) PhaseDetailText.Text = status.SettingsError;
        LastActionText.Text = status.LastAction;

        var battery = status.Battery;
        BatteryPercentText.Text = battery.IsAvailable ? $"{battery.Percent}%" : "--%";
        BatteryStateText.Text = battery.Description;
        BatteryProgress.Value = battery.IsAvailable ? battery.Percent : 0;
        BatteryRemainingText.Text = battery.RemainingMinutes is { } minutes ? FormatMinutes(minutes) : "未知";
        BatteryProgress.Foreground = battery.IsAvailable && battery.Percent <= status.EmergencyThreshold
            ? (Brush)FindResource("DangerBrush")
            : battery.IsAvailable && battery.Percent <= status.WarningThreshold
                ? (Brush)FindResource("WarningBrush")
                : (Brush)FindResource("SuccessBrush");

        var obs = status.Obs;
        ObsStateText.Text = obs.IsConnected ? "已连接" : "未连接";
        ObsStateText.Foreground = obs.IsConnected ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("DangerBrush");
        var age = status.ObsUpdatedAt is { } updated ? $" · {Math.Max(0, (int)(DateTimeOffset.Now - updated).TotalSeconds)} 秒前确认" : " · 尚未确认";
        ObsVersionText.Text = (obs.IsConnected ? $"OBS {obs.Version} · {obs.CurrentProfile}" : (string.IsNullOrWhiteSpace(obs.Error) ? "等待连接" : obs.Error)) + age;
        ObsVersionText.ToolTip = ObsVersionText.Text;
        RecordStateText.Text = !obs.IsRecordingStateKnown ? "状态未知" : obs.IsRecording ? obs.IsPaused ? "已暂停" : "录像中" : "未录像";
        RecordStateText.Foreground = obs.IsRecording ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("TextBrush");
        RecordDurationText.Text = $"{(long)obs.Duration.TotalHours:00}:{obs.Duration.Minutes:00}:{obs.Duration.Seconds:00}  ·  {FormatBytes(obs.OutputBytes)}";
        OutputPathText.Text = !string.IsNullOrWhiteSpace(obs.OutputPath)
            ? "当前录像文件：" + obs.OutputPath
            : !string.IsNullOrWhiteSpace(obs.RecordDirectory)
                ? "录像保存目录：" + obs.RecordDirectory
                : "尚未从 OBS 获取录像保存目录";
        if (!string.IsNullOrWhiteSpace(obs.LastCompletedOutputPath)) OutputPathText.Text += "\n最近停止的文件：" + obs.LastCompletedOutputPath;
        OutputPathText.ToolTip = OutputPathText.Text;

        ProtectionText.Text = status.ProtectionEnabled ? "已启用" : "已关闭";
        ProtectionText.Foreground = status.ProtectionEnabled ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("DangerBrush");
        ThresholdText.Text = $"{status.StopThreshold}% 停止 · {status.EmergencyThreshold}% 紧急";
        NextSplitText.Text = status.NextSplitAt is { } splitAt ? FormatCountdown(splitAt) : "未安排";
        ShutdownText.Text = status.ShutdownScheduled && status.ShutdownAt is { } shutdownAt ? FormatCountdown(shutdownAt) : "未安排";
        CancelShutdownButton.IsEnabled = status.ShutdownScheduled;
        StartRecordButton.IsEnabled = obs.IsConfirmedStopped && !status.ShutdownScheduled && !status.RecordingOperationBusy;
        StopRecordButton.IsEnabled = obs.IsConnected && !obs.IsConfirmedStopped;
        SplitRecordButton.IsEnabled = obs.IsRecordingStateKnown && obs.IsRecording && !status.RecordingOperationBusy && !status.ShutdownScheduled;
        ConnectObsButton.IsEnabled = !status.RecordingOperationBusy;
        foreach (var pending in _pendingButtons) pending.IsEnabled = false;

        if (LogsPage.Visibility == Visibility.Visible && FollowLogsCheck.IsChecked == true && !LogTextBox.IsKeyboardFocusWithin)
            _ = RefreshLogViewAsync(status.LogFilePath);
    }

    private void SetGuardianOffline(string message)
    {
        _latestStatus = null;
        UpdateTray(null, message);
        GuardianDot.Fill = (Brush)FindResource("DangerBrush");
        GuardianStateText.Text = "未运行";
        HeaderStatusText.Text = "后台守护未连接";
        PhaseText.Text = "后台守护不可用";
        PhaseDetailText.Text = message;
        StartRecordButton.IsEnabled = false;
        StopRecordButton.IsEnabled = false;
        SplitRecordButton.IsEnabled = ConnectObsButton.IsEnabled = false;
        PhaseIconText.Text = "!";
    }

    private async Task<IpcResponse> ExecuteCommandAsync(string command, string progressText, bool showFailure = true, int timeoutMilliseconds = 30000)
    {
        if (_previewMode) return IpcResponse.Fail("界面预览模式，不执行 OBS 或电源命令");
        HeaderStatusText.Text = progressText;
        var response = await GuardianIpcClient.SendAsync(command, timeoutMilliseconds);
        if (response.Status is not null)
        {
            _latestStatus = response.Status;
            UpdateStatusUi(response.Status);
        }
        LastActionText.Text = response.Message;
        if (!response.Success && showFailure && !_closeWorkflow && !_exitRequested)
            MessageBox.Show(this, response.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        return response;
    }

    private async void StartRecord_Click(object sender, RoutedEventArgs e) =>
        await ExecuteButtonCommandAsync(StartRecordButton, "start_record", "正在开始录像…");

    private async void StopRecord_Click(object sender, RoutedEventArgs e) =>
        await ExecuteButtonCommandAsync(StopRecordButton, "stop_record", "正在停止并保存…",
            (_settings.ObsStopTimeoutSeconds + _settings.FileFlushWaitSeconds + 40) * 1000);

    private async void SplitRecord_Click(object sender, RoutedEventArgs e) =>
        await ExecuteButtonCommandAsync(
            (Button)sender,
            "split_record",
            "正在安全分割并确认新录像…",
            Math.Min(300000, (_settings.ObsStopTimeoutSeconds + _settings.SplitRestartTimeoutSeconds + 20) * 1000));

    private async void ConnectObs_Click(object sender, RoutedEventArgs e)
    {
        if (_previewMode || !_pendingButtons.Add(ConnectObsButton)) return;
        var originalContent = ConnectObsButton.Content;
        ConnectObsButton.IsEnabled = false;
        ConnectObsButton.Content = "正在检查…";
        try
        {
            // Only saved settings are effective. Asking here never enables persistent auto-start.
            var command = ChooseConnectCommand(_settings.ObsHost, ObsProfileManager.IsObsRunning(), _settings.AutoLaunchObs, () =>
                MessageBox.Show(this,
                    "检测到 OBS 尚未运行，是否现在打开并连接？\n\n这次启动不会修改“自动启动 OBS”设置。若已启用“连接 OBS 后自动录像”，连接成功会按该设置开始录像。",
                    "启动 OBS", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            if (command is null) { HeaderStatusText.Text = "已取消打开 OBS"; return; }
            ConnectObsButton.Content = command == "connect_obs_launch" ? "正在启动…" : "正在连接…";
            await ExecuteCommandAsync(command, command == "connect_obs_launch" ? "正在启动 OBS 并等待连接（最长 30 秒）…" : "正在连接 OBS…", timeoutMilliseconds: 45000);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "连接 OBS 失败：" + ex.Message, "连接未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _pendingButtons.Remove(ConnectObsButton);
            ConnectObsButton.Content = originalContent;
            ConnectObsButton.IsEnabled = true;
            if (_latestStatus is { } status) UpdateStatusUi(status);
        }
    }

    private static string? ChooseConnectCommand(string host, bool running, bool autoLaunch, Func<bool> confirmLaunch)
    {
        if (!ObsProcessLauncher.IsLocalHost(host) || running) return "connect_obs";
        return autoLaunch || confirmLaunch() ? "connect_obs_launch" : null;
    }
    private async void CancelShutdown_Click(object sender, RoutedEventArgs e) => await ExecuteCommandAsync("cancel_shutdown", "正在取消关机");

    private async Task ExecuteButtonCommandAsync(Button button, string command, string busyText, int timeoutMilliseconds = 30000)
    {
        if (!_pendingButtons.Add(button)) return;
        var originalContent = button.Content;
        button.IsEnabled = false;
        button.Content = busyText;
        try
        {
            await ExecuteCommandAsync(command, busyText, timeoutMilliseconds: timeoutMilliseconds);
            await RefreshStatusAsync();
        }
        finally
        {
            _pendingButtons.Remove(button);
            button.Content = originalContent;
            button.IsEnabled = true;
            if (_latestStatus is { } status) UpdateStatusUi(status);
        }
    }

    private async void OpenRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        var outputPath = _latestStatus?.Obs.OutputPath;
        var recordDirectory = _latestStatus?.Obs.RecordDirectory;
        if (string.IsNullOrWhiteSpace(outputPath) && string.IsNullOrWhiteSpace(recordDirectory))
        {
            var response = await ExecuteCommandAsync("refresh_record_directory", "正在读取 OBS 录像目录…", false, 10000);
            outputPath = response.Status?.Obs.OutputPath;
            recordDirectory = response.Status?.Obs.RecordDirectory;
        }
        var directory = !string.IsNullOrWhiteSpace(outputPath)
            ? Path.GetDirectoryName(outputPath)
            : recordDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show(this, "OBS 尚未提供录像保存目录，请先连接 OBS。", "没有录像目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenFolder(directory);
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { SettingsNoticeText.Text = "预览模式：仅测试界面，不保存任何实际配置。"; return; }
        if (!SaveSettingsButton.IsEnabled) return;
        SaveSettingsButton.IsEnabled = false;
        try
        {
            var settings = ReadSettingsFromControls();
            if (settings.WarningBatteryPercent < settings.StopBatteryPercent || settings.StopBatteryPercent <= settings.EmergencyBatteryPercent)
            {
                FocusInvalidSetting(StopText);
                throw new InvalidOperationException("阈值必须满足：预警电量 ≥ 停止电量 > 紧急电量。");
            }
            if (settings.MinimumStartBatteryPercent < settings.StopBatteryPercent)
            {
                FocusInvalidSetting(MinStartText);
                throw new InvalidOperationException("允许开始录像的最低电量不能低于停止录像电量。");
            }

            settings.Normalize();
            if (ReadDouble(MinDiskText, "最低磁盘空间") is < 0.1 or > 1000)
            {
                FocusInvalidSetting(MinDiskText);
                throw new InvalidOperationException("最低磁盘空间必须为 0.1–1000 GB。");
            }
            _store.Save(settings);
            _settings = settings;
            LoadSettingsIntoControls(settings);
            StartupApplyResult? startupResult = null;
            startupResult = StartupManager.Apply(settings.AutoStartGuardianWithWindows, Environment.ProcessPath ?? "", "");
            _statusTimer.Interval = TimeSpan.FromMilliseconds(settings.StatusRefreshMilliseconds);
            var response = await ExecuteCommandAsync("reload_settings", "正在应用配置", false);
            var message = response.Success ? response.Message : "已保存到磁盘，但后台未应用：" + response.Message;
            if (startupResult is { Success: false })
                message += Environment.NewLine + Environment.NewLine + startupResult.Message;
            else if (startupResult?.Method == "startup-folder")
                message += Environment.NewLine + Environment.NewLine + startupResult.Message;
            SettingsNoticeText.Text = message;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "参数有误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SaveSettingsButton.IsEnabled = true; }
    }

    private void LoadSettingsIntoControls(AppSettings s)
    {
        _loadingControls = true;
        ObsHostText.Text = s.ObsHost;
        ObsPortText.Text = s.ObsPort.ToString();
        ObsPasswordBox.Password = s.ObsPassword;
        ObsPathText.Text = s.ObsExecutablePath;
        ObsConfigDirectoryText.Text = s.ObsConfigDirectory;
        AutoConnectObsCheck.IsChecked = s.AutoConnectObs;
        AutoLaunchObsCheck.IsChecked = s.AutoLaunchObs;
        ConnTimeoutText.Text = s.ObsConnectionTimeoutSeconds.ToString();
        ReconnectText.Text = s.ObsReconnectSeconds.ToString();

        BatteryProtectionCheck.IsChecked = s.BatteryProtectionEnabled;
        WarningText.Text = s.WarningBatteryPercent.ToString();
        StopText.Text = s.StopBatteryPercent.ToString();
        EmergencyText.Text = s.EmergencyBatteryPercent.ToString();
        MinStartText.Text = s.MinimumStartBatteryPercent.ToString();
        PollText.Text = s.BatteryPollSeconds.ToString();
        ConsecutiveText.Text = s.ConsecutiveLowReadings.ToString();
        StopByTimeCheck.IsChecked = s.StopByRemainingMinutesEnabled;
        RemainingMinutesText.Text = s.StopByRemainingMinutes.ToString();
        UnknownFailSafeCheck.IsChecked = s.FailSafeWhenBatteryUnknown;
        UnknownTimeoutText.Text = s.UnknownBatteryTimeoutSeconds.ToString();

        RequireMkvCheck.IsChecked = s.RequireMkv;
        EnableSplitCheck.IsChecked = s.EnableAutomaticSplit;
        SplitMinutesText.Text = s.SplitMinutes.ToString();
        SplitFallbackCheck.IsChecked = s.AllowStopStartSplitFallback;
        SplitDelayText.Text = s.SplitFallbackDelayMilliseconds.ToString();
        SplitRestartTimeoutText.Text = s.SplitRestartTimeoutSeconds.ToString();
        MinDiskText.Text = s.MinimumDiskFreeGb.ToString("0.##", CultureInfo.CurrentCulture);
        StopOnDiskLowCheck.IsChecked = s.StopRecordingWhenDiskLow;
        StopTimeoutText.Text = s.ObsStopTimeoutSeconds.ToString();
        FlushWaitText.Text = s.FileFlushWaitSeconds.ToString();
        PreventSleepCheck.IsChecked = s.PreventSystemSleepWhileRecording;
        KeepDisplayCheck.IsChecked = s.KeepDisplayAwakeWhileRecording;

        AfterStopCombo.SelectedIndex = s.AfterStopAction switch { PostRecordingAction.None => 0, PostRecordingAction.Shutdown => 1, PostRecordingAction.Hibernate => 2, _ => 1 };
        ShutdownDelayText.Text = s.ShutdownDelaySeconds.ToString();
        EmergencyDelayText.Text = s.EmergencyShutdownDelaySeconds.ToString();
        EmergencyAlwaysShutdownCheck.IsChecked = s.EmergencyAlwaysShutdown;
        CancelAcCheck.IsChecked = s.CancelShutdownWhenAcReturns;
        ResumeAcCheck.IsChecked = s.ResumeRecordingWhenAcReturns;
        ForceShutdownCheck.IsChecked = s.ForceCloseAppsOnShutdown;
        ForceEmergencyCheck.IsChecked = s.ForceCloseAppsOnEmergency;

        AutoStartGuardianCheck.IsChecked = s.AutoStartGuardianWithWindows;
        AutoStartRecordingCheck.IsChecked = s.AutoStartRecordingWhenObsConnects;
        BatteryLogIntervalText.Text = s.BatteryLogIntervalSeconds.ToString();
        ObsPollIntervalText.Text = s.ObsStatusPollMilliseconds.ToString();
        RefreshIntervalText.Text = s.StatusRefreshMilliseconds.ToString();
        _loadingControls = false;
        _settingsDirty = false;
        UpdateDependentControls();
    }

    private AppSettings ReadSettingsFromControls()
    {
        var s = new AppSettings
        {
            ObsHost = ObsHostText.Text.Trim(),
            ObsPort = ReadInt(ObsPortText, "OBS 端口"),
            ObsExecutablePath = ObsPathText.Text.Trim(),
            ObsConfigDirectory = ObsConfigDirectoryText.Text.Trim(),
            AutoConnectObs = AutoConnectObsCheck.IsChecked == true,
            AutoLaunchObs = AutoLaunchObsCheck.IsChecked == true,
            ObsConnectionTimeoutSeconds = ReadInt(ConnTimeoutText, "连接超时"),
            ObsReconnectSeconds = ReadInt(ReconnectText, "重连间隔"),

            BatteryProtectionEnabled = BatteryProtectionCheck.IsChecked == true,
            WarningBatteryPercent = ReadInt(WarningText, "预警电量"),
            StopBatteryPercent = ReadInt(StopText, "停止电量"),
            EmergencyBatteryPercent = ReadInt(EmergencyText, "紧急电量"),
            MinimumStartBatteryPercent = ReadInt(MinStartText, "最低启动电量"),
            BatteryPollSeconds = ReadInt(PollText, "电池检测间隔"),
            ConsecutiveLowReadings = ReadInt(ConsecutiveText, "连续确认次数"),
            StopByRemainingMinutesEnabled = StopByTimeCheck.IsChecked == true,
            StopByRemainingMinutes = ReadInt(RemainingMinutesText, "剩余时间阈值"),
            FailSafeWhenBatteryUnknown = UnknownFailSafeCheck.IsChecked == true,
            UnknownBatteryTimeoutSeconds = ReadInt(UnknownTimeoutText, "电池未知超时"),

            RequireMkv = RequireMkvCheck.IsChecked == true,
            EnableAutomaticSplit = EnableSplitCheck.IsChecked == true,
            SplitMinutes = ReadInt(SplitMinutesText, "分割时间"),
            AllowStopStartSplitFallback = SplitFallbackCheck.IsChecked == true,
            SplitFallbackDelayMilliseconds = ReadInt(SplitDelayText, "兼容分割间隔"),
            SplitRestartTimeoutSeconds = ReadInt(SplitRestartTimeoutText, "兼容分割重启超时"),
            MinimumDiskFreeGb = ReadDouble(MinDiskText, "最低磁盘空间"),
            StopRecordingWhenDiskLow = StopOnDiskLowCheck.IsChecked == true,
            ObsStopTimeoutSeconds = ReadInt(StopTimeoutText, "OBS 停止超时"),
            FileFlushWaitSeconds = ReadInt(FlushWaitText, "落盘等待"),
            PreventSystemSleepWhileRecording = PreventSleepCheck.IsChecked == true,
            KeepDisplayAwakeWhileRecording = KeepDisplayCheck.IsChecked == true,

            AfterStopAction = AfterStopCombo.SelectedIndex switch { 0 => PostRecordingAction.None, 2 => PostRecordingAction.Hibernate, _ => PostRecordingAction.Shutdown },
            ShutdownDelaySeconds = ReadInt(ShutdownDelayText, "正常关机延时"),
            EmergencyShutdownDelaySeconds = ReadInt(EmergencyDelayText, "紧急关机延时"),
            EmergencyAlwaysShutdown = EmergencyAlwaysShutdownCheck.IsChecked == true,
            CancelShutdownWhenAcReturns = CancelAcCheck.IsChecked == true,
            ResumeRecordingWhenAcReturns = ResumeAcCheck.IsChecked == true,
            ForceCloseAppsOnShutdown = ForceShutdownCheck.IsChecked == true,
            ForceCloseAppsOnEmergency = ForceEmergencyCheck.IsChecked == true,

            AutoStartGuardianWithWindows = AutoStartGuardianCheck.IsChecked == true,
            AutoStartRecordingWhenObsConnects = AutoStartRecordingCheck.IsChecked == true,
            BatteryLogIntervalSeconds = ReadInt(BatteryLogIntervalText, "电池日志间隔"),
            ObsStatusPollMilliseconds = ReadInt(ObsPollIntervalText, "OBS 状态同步间隔"),
            StatusRefreshMilliseconds = ReadInt(RefreshIntervalText, "界面刷新间隔")
        };
        s.ObsPassword = ObsPasswordBox.Password;
        return s;
    }

    private int ReadInt(TextBox control, string name)
    {
        if (!int.TryParse(control.Text.Trim(), out var value)) { FocusInvalidSetting(control); throw new InvalidOperationException($"“{name}”必须是整数。"); }
        if (control.Tag is ValueTuple<int, int> range && (value < range.Item1 || value > range.Item2))
        {
            FocusInvalidSetting(control);
            throw new InvalidOperationException($"“{name}”范围为 {range.Item1}–{range.Item2}，请修改后保存。");
        }
        return value;
    }

    private void ConfigureParameterHelp()
    {
        void Range(TextBox box, int min, int max, string help)
        { box.Tag = (min, max); box.ToolTip = $"范围：{min}–{max}。{help}"; }
        Range(ObsPortText, 1, 65535, "OBS WebSocket 端口，默认 4455。应用后自动重连。");
        Range(ConnTimeoutText, 2, 30, "秒；单次连接/请求等待上限。");
        Range(ReconnectText, 1, 120, "秒；连接失败后的重试间隔。");
        Range(WarningText, 5, 100, "%；只预警，不直接停止。必须不低于停止阈值。");
        Range(StopText, 3, 100, "%；连续满足后停止。应高于 Windows 自身低电量休眠阈值。");
        Range(EmergencyText, 1, 99, "%；紧急兜底，必须低于停止阈值。");
        Range(MinStartText, 3, 100, "%；低于此值拒绝启动录像。必须不低于停止阈值。");
        Range(PollText, 1, 60, "秒；电池独立采样周期。默认 5 秒。");
        Range(ConsecutiveText, 1, 10, "次；普通低电量连续确认次数，紧急阈值不等待多次确认。");
        Range(RemainingMinutesText, 1, 240, "分钟；系统电池剩余时间估算可能不准确。");
        Range(UnknownTimeoutText, 15, 3600, "秒；持续无法读电池时触发保护。");
        Range(SplitMinutesText, 1, 1440, "分钟；更改并应用后从应用时重新计时。");
        Range(SplitDelayText, 200, 5000, "毫秒；兼容分割的停录间隔，会有画面缺口。");
        Range(SplitRestartTimeoutText, 5, 120, "秒；兼容分割启动新文件的最长重试窗口。");
        Range(StopTimeoutText, 3, 120, "秒；停止确认等待上限。");
        Range(FlushWaitText, 0, 60, "秒；停止后额外等待写入，不等于硬盘数据完整性验证。");
        Range(ShutdownDelayText, 0, 3600, "秒；修改只影响下一次计划，已有倒计时不变。");
        Range(EmergencyDelayText, 0, 120, "秒；紧急计划可缩短已有普通倒计时。");
        Range(BatteryLogIntervalText, 10, 3600, "秒；定期电池日志间隔，关键事件另外记录。");
        Range(ObsPollIntervalText, 100, 5000, "毫秒；OBS 状态查询间隔，不是电池检测周期。");
        Range(RefreshIntervalText, 250, 10000, "毫秒；界面读取后台状态的间隔。");
        MinDiskText.ToolTip = "范围：0.1–1000 GB。建议至少 5 GB，所有开始录像操作都会检查磁盘空间。";
    }

    private void UpdateResponsiveLayout(double width)
    {
        StatusCards.Columns = width < 1150 ? 2 : 4;
    }

    private void SettingsText_Changed(object sender, TextChangedEventArgs e) => MarkSettingsDirty();
    private void SettingsSelection_Changed(object sender, SelectionChangedEventArgs e) => MarkSettingsDirty();
    private void SettingsControl_Changed(object sender, RoutedEventArgs e) => MarkSettingsDirty();
    private void MarkSettingsDirty()
    {
        if (_loadingControls || (!IsLoaded && !_previewMode)) return;
        _settingsDirty = true;
        SettingsNoticeText.Text = "有未保存修改 · 切换分类不会丢失。点击“保存全部设置”应用。";
        UpdateDependentControls();
    }

    private void UpdateDependentControls()
    {
        RemainingMinutesText.IsEnabled = StopByTimeCheck.IsChecked == true;
        UnknownTimeoutText.IsEnabled = UnknownFailSafeCheck.IsChecked == true;
        SplitMinutesText.IsEnabled = EnableSplitCheck.IsChecked == true;
        SplitDelayText.IsEnabled = SplitRestartTimeoutText.IsEnabled = SplitFallbackCheck.IsChecked == true;
        ShutdownDelayText.IsEnabled = ForceShutdownCheck.IsEnabled = AfterStopCombo.SelectedIndex != 0;
        EmergencyDelayText.IsEnabled = ForceEmergencyCheck.IsEnabled = EmergencyAlwaysShutdownCheck.IsChecked == true;
    }

    private double ReadDouble(TextBox control, string name)
    {
        if (double.TryParse(control.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value)) return value;
        if (double.TryParse(control.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value)) return value;
        FocusInvalidSetting(control);
        throw new InvalidOperationException($"“{name}”必须是数字。");
    }

    private void BrowseObs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 OBS 主程序", Filter = "OBS 主程序 (obs64.exe)|obs64.exe|可执行文件 (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true) ObsPathText.Text = dialog.FileName;
    }

    private void AutoFindObs_Click(object sender, RoutedEventArgs e)
    {
        var result = ObsInstallationLocator.FindBest(ObsPathText.Text);
        if (result.Found)
        {
            ObsPathText.Text = result.Path;
            MessageBox.Show(this, $"已找到 OBS：\n{result.Path}\n\n来源：{result.Source}\n\n请点击“保存全部设置”使路径生效。", "自动查找完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(this, $"没有找到 obs64.exe。已检查 {result.CheckedLocations.Count} 个常见位置，请使用“选择程序”手动选择。", "未找到 OBS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AutoLocateObsIfNeeded()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ObsExecutablePath) && File.Exists(_settings.ObsExecutablePath)) return;
        var result = ObsInstallationLocator.FindBest();
        if (!result.Found) return;
        _settings.ObsExecutablePath = result.Path;
        ObsPathText.Text = result.Path;
        _store.Save(_settings);
    }

    private async void SelfCheck_Click(object sender, RoutedEventArgs e)
    {
        CheckResultText.Text = "正在检查配置与连接（不执行录制或电源操作）……";
        var result = await ExecuteCommandAsync("self_check", "正在进行系统自检", false);
        CheckResultText.Text = result.Message;
        CheckResultText.Foreground = result.Success ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("WarningBrush");
    }

    private async void Preflight_Click(object sender, RoutedEventArgs e)
    {
        if (_previewMode) return;
        if (!PreflightButton.IsEnabled) return;
        PreflightButton.IsEnabled = false;
        ExportPreflightButton.IsEnabled = false;
        CheckResultText.Text = "正在执行只读预检，不控制录像和电源……";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            _preflightReport = await Task.Run(() => ReadOnlyPreflight.RunAsync(timeout.Token));
            CheckResultText.Text = _preflightReport;
            CheckResultText.Foreground = (Brush)FindResource("TextBrush");
            ExportPreflightButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _preflightReport = string.Empty;
            CheckResultText.Text = "预检未完成：" + ex.Message;
        }
        finally { PreflightButton.IsEnabled = true; }
    }

    private async void ExportPreflight_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_preflightReport)) return;
        var dialog = new SaveFileDialog { FileName = $"OBS-实机预检-{DateTime.Now:yyyyMMdd-HHmmss}.md", Filter = "Markdown 报告 (*.md)|*.md", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, _preflightReport); CheckResultText.Text = "报告已导出：" + dialog.FileName + Environment.NewLine + _preflightReport; }
        catch (Exception ex) { CheckResultText.Text = "导出失败：" + ex.Message; }
    }

    private async void SafetyTest_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "演练会停止当前录像并等待文件写入。演练本身不安排关机，但真实低电量保护仍然有效，可能触发紧急关机。是否继续？", "确认安全演练", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        var result = await ExecuteCommandAsync("safety_test", "正在执行安全停止演练", false);
        CheckResultText.Text = result.Message;
    }

    private async void InspectMkv_Click(object sender, RoutedEventArgs e)
    {
        var result = await ExecuteCommandAsync("inspect_mkv", "正在检查 MKV 格式", false);
        FormatStatusText.Text = result.Message;
        FormatStatusText.Foreground = result.Success ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("WarningBrush");
    }

    private async void EnsureMkv_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "请先停止录像并完全退出 OBS。软件将备份当前配置，然后把简单输出和高级输出格式设置为 MKV。是否继续？", "设置 MKV", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        var result = await ExecuteCommandAsync("ensure_mkv", "正在设置 MKV", false);
        FormatStatusText.Text = result.Message;
        FormatStatusText.Foreground = result.Success ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("DangerBrush");
        if (!result.Success) MessageBox.Show(this, result.Message, "MKV 设置未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RefreshLogs_Click(object sender, RoutedEventArgs e)
    {
        var path = _latestStatus?.LogFilePath;
        if (!string.IsNullOrWhiteSpace(path)) _ = RefreshLogViewAsync(path, true);
    }

    private async Task RefreshLogViewAsync(string path, bool reset = false)
    {
        if (_readingLogs || string.IsNullOrWhiteSpace(path)) return;
        _readingLogs = true;
        try
        {
            LogPathText.Text = path;
            _logContents = await Task.Run(() => _logReader.Read(path, reset));
            ApplyLogFilter();
        }
        catch (Exception ex)
        {
            LogPathText.Text = "读取日志失败（保留当前内容）：" + ex.Message;
        }
        finally { _readingLogs = false; }
    }

    private void ApplyLogFilter()
    {
        if (LogTextBox is null || LogSearchText is null || ProblemLogsCheck is null) return;
        var content = IncrementalLogReader.Filter(_logContents, LogSearchText.Text, ProblemLogsCheck.IsChecked == true);
        if (LogTextBox.Text == content) return;
        var offset = LogTextBox.VerticalOffset;
        LogTextBox.Text = content;
        if (FollowLogsCheck.IsChecked == true && !LogTextBox.IsKeyboardFocusWithin) LogTextBox.ScrollToEnd();
        else LogTextBox.ScrollToVerticalOffset(offset);
    }
    private void LogSearch_Changed(object sender, TextChangedEventArgs e) => ApplyLogFilter();
    private void LogFilter_Changed(object sender, RoutedEventArgs e) => ApplyLogFilter();

    private async void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var source = _latestStatus?.LogFilePath;
        if (string.IsNullOrWhiteSpace(source)) return;
        var dialog = new SaveFileDialog { FileName = Path.GetFileName(source), Filter = "日志文件 (*.log)|*.log", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (Path.GetFullPath(source).Equals(Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)) throw new IOException("不能覆盖正在写入的源日志");
            await Task.Run(() =>
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
                var remaining = input.Length;
                var buffer = new byte[65536];
                while (remaining > 0)
                {
                    var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count == 0) break;
                    output.Write(buffer, 0, count); remaining -= count;
                }
            });
            LogPathText.Text = "已导出当前日志快照：" + dialog.FileName;
        }
        catch (Exception ex) { LogPathText.Text = "导出失败：" + ex.Message; }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_store.LogDirectory);

    private void OpenReadableLog_Click(object sender, RoutedEventArgs e)
    {
        var path = _latestStatus?.LogFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "易读日志文件尚未生成。", "没有日志", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
    }

    private void DashboardNav_Click(object sender, RoutedEventArgs e) => ShowPage(DashboardPage, DashboardNavButton);
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, SettingsNavButton);
    private void LogsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(LogsPage, LogsNavButton);
        if (_latestStatus is { } status) _ = RefreshLogViewAsync(status.LogFilePath);
    }
    private void DiagnosticsNav_Click(object sender, RoutedEventArgs e) => ShowPage(DiagnosticsPage, DiagnosticsNavButton);

    private void ShowPage(UIElement page, Button selectedButton)
    {
        DashboardPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        LogsPage.Visibility = Visibility.Collapsed;
        DiagnosticsPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;

        DashboardNavButton.Tag = null;
        SettingsNavButton.Tag = null;
        LogsNavButton.Tag = null;
        DiagnosticsNavButton.Tag = null;
        selectedButton.Tag = "Selected";
    }

    private static string FindGuardianExecutable()
    {
        var sibling = Path.Combine(AppContext.BaseDirectory, "ObsBatteryGuard.Guard.exe");
        if (File.Exists(sibling)) return sibling;
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ObsBatteryGuard.Guard", "bin", configuration, "net8.0-windows", "ObsBatteryGuard.Guard.exe"));
        return File.Exists(development) ? development : string.Empty;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = Math.Max(0, (double)bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    private static string FormatMinutes(int minutes) => minutes >= 60 ? $"{minutes / 60} 小时 {minutes % 60} 分" : $"{minutes} 分钟";

    private static string FormatCountdown(DateTimeOffset target)
    {
        var remaining = target - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "即将执行";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        return $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }
}
