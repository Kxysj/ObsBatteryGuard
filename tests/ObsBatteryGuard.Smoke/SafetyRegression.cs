using System.IO.Pipes;
using ObsBatteryGuard.Core;

internal static class SafetyRegression
{
    public static async Task RunAsync(string directory)
    {
        using (var gate = new RecordingOperationGate())
        {
            var split = gate.TryEnter(default) ?? throw new Exception("无法创建分割操作");
            var stopTask = gate.EnterStopAsync(default);
            Check(split.Token.IsCancellationRequested, "停止没有取消分割");
            Check(gate.TryEnter(default) is null, "停止等待期间不应允许新录制操作");
            split.Dispose();
            using (await stopTask.WaitAsync(TimeSpan.FromSeconds(1)))
                Check(gate.TryEnter(default) is null, "停止期间不能重新开始");
            using var next = gate.TryEnter(default);
            Check(next is not null, "停止完成后没有释放操作锁");
        }
        Console.WriteLine("PASS stop-priority-and-split-cancellation");

        var unknown = new ObsSnapshot(false, false, false, TimeSpan.Zero, 0, "", "", "", "断连");
        Check(!unknown.IsConfirmedStopped, "断连不能判定为已停止");
        Check(!(unknown with { IsConnected = true }).IsConfirmedStopped, "查询失败不能判定为已停止");
        Check((unknown with { IsConnected = true, Error = "" }).IsConfirmedStopped, "有效停止状态无法识别");
        Console.WriteLine("PASS unknown-recording-state");

        var power = new PowerCountdown();
        var now = DateTimeOffset.UtcNow;
        power.Schedule(PostRecordingAction.Shutdown, now.AddSeconds(60), false, false);
        power.Schedule(PostRecordingAction.Shutdown, now.AddSeconds(15), true, true);
        Check(power.Pending is { Force: true, Emergency: true } && power.Pending.DueAt == now.AddSeconds(15), "紧急倒计时没有升级普通倒计时");
        power.Schedule(PostRecordingAction.Hibernate, now.AddSeconds(60), false, false);
        Check(power.Pending?.DueAt == now.AddSeconds(15), "普通流程覆盖了紧急倒计时");
        Check(power.Cancel() && power.TryCommit(now.AddSeconds(120)) is null, "取消后仍提交电源操作");
        power.Schedule(PostRecordingAction.Shutdown, now, false, false);
        Check(power.TryCommit(now) is { Force: false } && power.TryCommit(now) is null, "电源操作重复提交或强制选项改变");
        Check(!power.Cancel(), "已提交的操作不应显示取消成功");
        Console.WriteLine("PASS power-escalation-cancel-single-commit-and-force-policy");

        var blockedDirectory = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(blockedDirectory, "simulate inaccessible log directory");
        using (var logger = new DurableLogger(blockedDirectory))
        {
            for (var i = 0; i < 250; i++) logger.Critical("test", "日志失败后保护必须继续");
            Check(logger.LastWriteError.Length > 0 && logger.EmergencyLines.Count == 200, "日志故障未降级为有界内存记录");
        }
        using (var logger = new DurableLogger(Path.Combine(directory, "subscriber-log")))
        {
            logger.LineWritten += _ => throw new IOException("模拟订阅者异常");
            logger.Critical("test", "订阅者异常不能传播");
            Check(logger.LastWriteError.Length > 0, "订阅者故障未报告");
        }
        Console.WriteLine("PASS log-failure-isolation-and-bounded-buffer");

        var store = new SettingsStore(Path.Combine(directory, "safety"));
        store.Save(new AppSettings
        {
            AutoConnectObs = false, AutoLaunchObs = false, AutoStartGuardianWithWindows = false,
            BatteryProtectionEnabled = false, RequireMkv = false, StopRecordingWhenDiskLow = false,
            EnableAutomaticSplit = false, BatteryPollSeconds = 1, FileFlushWaitSeconds = 0,
            ObsStopTimeoutSeconds = 3, AfterStopAction = PostRecordingAction.None, EmergencyAlwaysShutdown = false
        });
        using var safeLogger = new DurableLogger(store.LogDirectory);
        var obs = new FakeObs();
        var batteryReads = 0;
        var batteryPercent = 80;
        await using var engine = new GuardianEngine(store, safeLogger, obs, () =>
        {
            Interlocked.Increment(ref batteryReads);
            return new BatterySnapshot(true, Volatile.Read(ref batteryPercent), false, false, false, 60, DateTimeOffset.Now, "测试电池");
        });
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loop = engine.RunAsync(lifetime.Token);
        var pipeName = "OBG.Safety." + Guid.NewGuid().ToString("N");
        var server = new GuardianIpcServer(engine, safeLogger, pipeName);
        var serving = server.RunAsync(lifetime.Token);
        try
        {
            // The fake OBS deliberately stalls; this never connects to the real OBS or calls Windows power APIs.
            obs.BlockSnapshots = true;
            await Task.Delay(2300, lifetime.Token);
            Check(Volatile.Read(ref batteryReads) >= 2, "OBS 阻塞时电池监测停止了");
            using var stalledClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stalledClient.ConnectAsync(lifetime.Token);
            var status = await GuardianIpcClient.SendAsync("get_status", 1500, lifetime.Token, pipeName);
            Check(status.Success, "未发送完整请求的客户端阻塞了状态查询");
            Console.WriteLine("PASS battery-loop-independent-of-obs-and-stalled-ipc");
            obs.BlockSnapshots = false;
            obs.ReleaseSnapshots.TrySetResult();

            obs.Recording = true;
            await Task.Delay(500, lifetime.Token);
            var changing = store.Load();
            changing.EnableAutomaticSplit = true;
            changing.SplitMinutes = 7;
            store.Save(changing); engine.ReloadSettings();
            Check(engine.GetStatus().NextSplitAt is { } splitAt && splitAt > DateTimeOffset.Now.AddMinutes(6), "录制中启用分割没有安排下一段");
            changing.SplitMinutes = 2;
            store.Save(changing); engine.ReloadSettings();
            Check(engine.GetStatus().NextSplitAt < DateTimeOffset.Now.AddMinutes(3), "修改分割间隔没有重新计时");
            changing.EnableAutomaticSplit = false;
            changing.ObsPort = 4456;
            store.Save(changing); engine.ReloadSettings();
            Check(engine.GetStatus().NextSplitAt is null, "禁用分割未清除倒计时");
            await engine.HandleCommandAsync(new IpcRequest { Command = "connect_obs" }, lifetime.Token);
            Check(obs.Disconnects > 0 && obs.LastPort == 4456, "连接参数改变后没有重连新端口");
            Console.WriteLine("PASS live-split-settings-and-connection-reload");

            obs.Recording = true;
            var split = engine.HandleCommandAsync(new IpcRequest { Command = "split_record" }, lifetime.Token);
            await obs.StopSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stop = engine.HandleCommandAsync(new IpcRequest { Command = "stop_record" }, lifetime.Token);
            var splitResult = await split.WaitAsync(TimeSpan.FromSeconds(3));
            var stopResult = await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!splitResult.Success && stopResult.Success && obs.StartCalls == 0, "手动停止后兼容分割仍重启录像");
            Console.WriteLine("PASS manual-stop-preempts-fallback-split");

            obs.Unknown = true;
            var unknownStop = await engine.HandleCommandAsync(new IpcRequest { Command = "stop_record" }, lifetime.Token);
            Check(!unknownStop.Success, "未知状态被手动停止误报为已保存");
            Console.WriteLine("PASS manual-stop-requires-confirmation");

            obs.Unknown = false;
            obs.Recording = true;
            obs.StopSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var protection = store.Load();
            protection.BatteryProtectionEnabled = true;
            protection.StopBatteryPercent = 25;
            protection.ConsecutiveLowReadings = 1;
            protection.SplitFallbackDelayMilliseconds = 5000;
            store.Save(protection);
            engine.ReloadSettings();
            var protectedSplit = engine.HandleCommandAsync(new IpcRequest { Command = "split_record" }, lifetime.Token);
            await obs.StopSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Volatile.Write(ref batteryPercent, 20);
            var cancelledSplit = await protectedSplit.WaitAsync(TimeSpan.FromSeconds(4));
            Check(!cancelledSplit.Success && obs.StartCalls == 0, "低电量没有抢占兼容分割");
            await Task.Delay(300, lifetime.Token);
            Check(!obs.Recording && !engine.GetStatus().ShutdownScheduled, "仅停止策略执行结果不正确");
            Console.WriteLine("PASS battery-protection-preempts-split-without-power-action");
        }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(loop, serving);
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class FakeObs : IObsClient
    {
        public bool IsConnected { get; private set; } = true;
        public int Disconnects;
        public int LastPort;
        public string RecordDirectory => Path.GetTempPath();
        public volatile bool BlockSnapshots;
        public volatile bool Recording;
        public volatile bool Unknown;
        public int StartCalls;
        public TaskCompletionSource ReleaseSnapshots { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopSeen { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ObsRequestResult> ConnectAsync(string host, int port, string password, int timeoutSeconds, CancellationToken token)
        { IsConnected = true; LastPort = port; return Task.FromResult(new ObsRequestResult(true, "fake")); }
        public async Task<ObsSnapshot> GetSnapshotAsync(CancellationToken token)
        {
            if (BlockSnapshots) await ReleaseSnapshots.Task.WaitAsync(token);
            return new(true, Recording, false, TimeSpan.Zero, 0, "", "test", "test", Unknown ? "模拟查询失败" : "", RecordDirectory);
        }
        public Task<ObsRequestResult> StartRecordingAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref StartCalls); Recording = true; return Task.FromResult(new ObsRequestResult(true, "started")); }
        public Task<ObsRequestResult> StopRecordingAsync(CancellationToken token)
        { Recording = false; StopSeen.TrySetResult(); return Task.FromResult(new ObsRequestResult(true, "stopped")); }
        public Task<ObsRequestResult> SplitRecordingAsync(CancellationToken token) => Task.FromResult(new ObsRequestResult(false, "native unsupported"));
        public Task<string> GetRecordDirectoryAsync(CancellationToken token) => Task.FromResult(RecordDirectory);
        public Task DisconnectAsync() { IsConnected = false; Disconnects++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
