using ObsBatteryGuard.Core;

if (args.Length == 3 && args[0] == "--ownership-owner") { await OwnershipRegression.OwnerAsync(args[1], args[2]); return 0; }
if (args.Length == 2 && args[0] == "--ownership-controller") { await OwnershipRegression.ControllerAsync(args[1]); return 0; }
if (args.Length == 2 && args[0] == "--ownership-obs") { await OwnershipRegression.ObsAsync(args[1]); return 0; }
if (args.Length == 2 && args[0] == "--test-ownership") { await OwnershipRegression.RunAsync(args[1]); return 0; }

if (args.Length == 2 && args[0] == "--test-guardian-exit")
{
    await GuardianProcessExitRegression.RunAsync(args[1]);
    return 0;
}

if (args.Length == 1 && args[0] == "--preflight")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    Console.WriteLine(await ReadOnlyPreflight.RunAsync(timeout.Token));
    return 0;
}

if (args.Any(arg => arg.Equals("--probe-obs-path", StringComparison.OrdinalIgnoreCase)))
{
    var result = ObsInstallationLocator.FindBest();
    Console.WriteLine($"obs_path_probe_found={result.Found}");
    Console.WriteLine("obs_path_probe_path=" + result.Path);
    Console.WriteLine("obs_path_probe_source=" + result.Source);
    Console.WriteLine("obs_path_probe_checked=" + result.CheckedLocations.Count);
    return result.Found ? 0 : 5;
}

if (args.Any(arg => arg.Equals("--probe-obs", StringComparison.OrdinalIgnoreCase)))
{
    var actualStore = new SettingsStore();
    var actualSettings = actualStore.Load();
    var probePassword = Environment.GetEnvironmentVariable("OBG_OBS_TEST_PASSWORD") ?? actualSettings.ObsPassword;
    await using var client = new ObsWebSocketClient();
    var connection = await client.ConnectAsync(
        actualSettings.ObsHost,
        actualSettings.ObsPort,
        probePassword,
        actualSettings.ObsConnectionTimeoutSeconds,
        CancellationToken.None);
    Console.WriteLine($"obs_probe_success={connection.Success}");
    Console.WriteLine("obs_probe_message=" + connection.Message);
    if (connection.Success)
    {
        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);
        Console.WriteLine($"obs_connected={snapshot.IsConnected} version={snapshot.Version} profile={snapshot.CurrentProfile} recording={snapshot.IsRecording}");
        var recordDirectory = await client.GetRecordDirectoryAsync(CancellationToken.None);
        Console.WriteLine("obs_record_directory=" + recordDirectory);
    }
    return connection.Success ? 0 : 3;
}

if (args.Any(arg => arg.Equals("--probe-guard", StringComparison.OrdinalIgnoreCase)))
{
    var response = await GuardianIpcClient.SendAsync("connect_obs", 12000);
    Console.WriteLine($"guard_probe_success={response.Success}");
    Console.WriteLine("guard_probe_message=" + response.Message);
    if (response.Status is { } status)
        Console.WriteLine($"guard_obs_connected={status.Obs.IsConnected} guard_phase={status.PhaseText} last_error={status.LastError}");
    return response.Success ? 0 : 4;
}

var testDirectory = Path.Combine(Path.GetTempPath(), "ObsBatteryGuardSmoke-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new SettingsStore(testDirectory);
    var settings = new AppSettings
    {
        WarningBatteryPercent = 30,
        StopBatteryPercent = 22,
        EmergencyBatteryPercent = 9,
        SplitMinutes = 12,
        SplitRestartTimeoutSeconds = 17,
        ObsStatusPollMilliseconds = 275,
        StatusRefreshMilliseconds = 450,
        MinimumDiskFreeGb = 3.5
    };
    settings.ObsPassword = "local-test-password";
    store.Save(settings);

    var loaded = store.Load();
    Assert(loaded.StopBatteryPercent == 22, "配置数值没有正确保存");
    Assert(loaded.SplitRestartTimeoutSeconds == 17, "兼容分割重启超时没有正确保存");
    Assert(loaded.ObsStatusPollMilliseconds == 275 && loaded.StatusRefreshMilliseconds == 450, "独立状态刷新参数没有正确保存");
    Assert(loaded.ObsPassword == "local-test-password", "OBS 密码 DPAPI 加密往返失败");
    Assert(!loaded.ProtectedObsPassword.Contains("local-test-password", StringComparison.Ordinal), "密码以明文保存");

    var legacyDirectory = Path.Combine(testDirectory, "legacy-settings");
    var legacyStore = new SettingsStore(legacyDirectory);
    File.WriteAllText(legacyStore.SettingsPath, "{\"SchemaVersion\":1,\"BatteryPollSeconds\":5,\"StatusRefreshMilliseconds\":1500}");
    var migrated = legacyStore.Load();
    Assert(migrated.SchemaVersion == 2 && migrated.ObsStatusPollMilliseconds == 300 && migrated.StatusRefreshMilliseconds == 500,
        "旧版配置没有迁移到快速状态同步参数");

    var legacyStructuredPath = Path.Combine(store.LogDirectory, $"guard-{DateTime.Now:yyyy-MM-dd}.jsonl");
    File.WriteAllText(legacyStructuredPath,
        $"{{\"timestamp\":\"{DateTimeOffset.Now:O}\",\"level\":\"WARN\",\"category\":\"recovery\",\"message\":\"旧版日志迁移测试\",\"data\":{{\"code\":42}}}}{Environment.NewLine}");
    string logPath;
    using (var logger = new DurableLogger(store.LogDirectory))
    {
        logger.Info("test", "冒烟测试日志");
        logPath = logger.CurrentLogPath;
        var liveLines = DurableLogger.ReadRecentHumanLines(logPath, 20);
        Assert(liveLines.Any(line => line.Contains("冒烟测试日志", StringComparison.Ordinal)), "守护写入期间读取易读日志失败");
        Assert(File.Exists(logger.StructuredLogPath), "结构化日志副本未生成");
    }
    var lines = DurableLogger.ReadRecentHumanLines(logPath, 10);
    Assert(lines.Any(line => line.Contains("冒烟测试日志", StringComparison.Ordinal)), "可靠日志写入或读取失败");
    Assert(lines.Any(line => line.Contains("旧版日志迁移测试", StringComparison.Ordinal)), "旧版 JSONL 没有转换到易读日志");

    var battery = BatteryMonitor.Read();
    Assert(battery.Timestamp > DateTimeOffset.Now.AddMinutes(-1), "电池状态时间戳异常");

    var shortcutPath = Path.Combine(testDirectory, "startup-test.lnk");
    WindowsShortcut.Create(shortcutPath, Environment.ProcessPath ?? throw new InvalidOperationException("无法获取测试程序路径"), "--shortcut-test", "OBS Battery Guard test");
    Assert(File.Exists(shortcutPath) && new FileInfo(shortcutPath).Length > 0, "Windows 启动快捷方式回退创建失败");

    loaded.AutoConnectObs = false;
    loaded.BatteryProtectionEnabled = false;
    loaded.AutoStartGuardianWithWindows = false;
    store.Save(loaded);
    using (var ipcLogger = new DurableLogger(store.LogDirectory))
    await using (var engine = new GuardianEngine(store, ipcLogger))
    {
        using var cancellation = new CancellationTokenSource();
        var testPipeName = "ObsBatteryGuard.Smoke." + Guid.NewGuid().ToString("N");
        var shutdownRequested = false;
        var server = new GuardianIpcServer(engine, ipcLogger, testPipeName, () => shutdownRequested = true);
        var engineTask = engine.RunAsync(cancellation.Token);
        var serverTask = server.RunAsync(cancellation.Token);
        await Task.Delay(150);
        var response = await GuardianIpcClient.SendAsync("get_status", 2000, default, testPipeName);
        Assert(response.Success && response.Status is not null, "后台守护 IPC 状态读取失败");
        Assert(!string.IsNullOrWhiteSpace(response.Status!.GuardianVersion), "后台守护没有返回版本号");
        var shutdownResponse = await GuardianIpcClient.SendAsync("shutdown_guard", 2000, default, testPipeName);
        await Task.Delay(50);
        Assert(!shutdownResponse.Success && !shutdownRequested, "后台不应被界面自动结束");
        cancellation.Cancel();
        await Task.WhenAll(engineTask, serverTask);
    }

    Console.WriteLine("PASS settings-roundtrip");
    Console.WriteLine("PASS password-dpapi");
    Console.WriteLine("PASS fast-status-settings-migration");
    Console.WriteLine("PASS durable-log");
    Console.WriteLine("PASS live-readable-log-and-migration");
    Console.WriteLine($"PASS battery-api available={battery.IsAvailable} percent={battery.Percent}");
    Console.WriteLine("PASS startup-shortcut-fallback");
    Console.WriteLine("PASS guardian-ipc");
    Console.WriteLine("PASS guardian-version-and-upgrade-protection");
    await SafetyRegression.RunAsync(testDirectory);
    await ExperienceRegression.RunAsync(testDirectory);
    await ObsProtocolRegression.RunAsync();
    await PowerFlowRegression.RunAsync(testDirectory);
    await ObsLaunchRegression.RunAsync(testDirectory);
    await ApplicationExitRegression.RunAsync(testDirectory);
    TrayStatusRegression.Run();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL " + ex);
    return 1;
}
finally
{
    try { if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, true); } catch { }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
