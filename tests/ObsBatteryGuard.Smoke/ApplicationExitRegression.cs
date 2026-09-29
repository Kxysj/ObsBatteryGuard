using ObsBatteryGuard.Core;

internal static class ApplicationExitRegression
{
    public static async Task RunAsync(string directory)
    {
        var idle = new GuardianStatus { GuardianProcessId = 10, Obs = new(true, false, false, TimeSpan.Zero, 0, "", "test", "", ""), ObsUpdatedAt = DateTimeOffset.Now };
        var active = new GuardianStatus { GuardianProcessId = 10, Obs = idle.Obs with { IsRecording = true }, ObsUpdatedAt = DateTimeOffset.Now,
            RecordingOperationBusy = true, ShutdownScheduled = true, PowerActionCommitted = true };
        var prompt = ApplicationExit.BuildPrompt(active, true, true);
        if (!prompt.Contains("OBS 正在录像") || !prompt.Contains("分割") || !prompt.Contains("尚未保存") || !prompt.Contains("不能撤销"))
            throw new Exception("退出提醒遗漏任务或数据风险");
        if (!ApplicationExit.BuildPrompt(null, false, true).Contains("无法确认")) throw new Exception("未知后台未提醒");
        if (ApplicationExit.BuildPrompt(idle, false, true).Length > 60 || prompt.Length > 190 ||
            ApplicationExit.BuildPrompt(null, false, true).Length > 90)
            throw new Exception("退出提示不够简洁");
        var pending = ApplicationExit.BuildPrompt(new GuardianStatus { Obs = idle.Obs, ObsUpdatedAt = DateTimeOffset.Now, ShutdownScheduled = true }, false, true);
        if (!pending.Contains("倒计时将取消") || prompt.Contains("倒计时将取消") || !prompt.Contains("不会自动停录"))
            throw new Exception("退出对 OBS 或电源计划的影响提示错误");
        Console.WriteLine("PASS exit-prompt-recording-busy-power-unsaved-unknown");

        var cancel = new FakeExitEnvironment(active);
        var cancelled = await ApplicationExit.RunAsync(cancel, false, _ => Task.FromResult(false));
        if (cancelled.Closed || cancel.Confirmed || cancel.Requests != 0 || cancel.Forces != 0) throw new Exception("取消退出仍结束后台");
        Console.WriteLine("PASS exit-cancel-preserves-guardian");
        var normal = new FakeExitEnvironment(idle) { Graceful = true };
        if (!(await ApplicationExit.RunAsync(normal, false, _ => Task.FromResult(true))).Closed || normal.Requests != 1 || normal.Forces != 0)
            throw new Exception("正常退出流程错误");
        Console.WriteLine("PASS exit-graceful-waits-for-process");
        var legacy = new FakeExitEnvironment(active);
        var questions = 0;
        if (!(await ApplicationExit.RunAsync(legacy, false, _ => { questions++; return Task.FromResult(true); })).Closed || questions != 2 || legacy.Forces != 1)
            throw new Exception("旧后台强退未单独确认");
        Console.WriteLine("PASS exit-legacy-force-requires-second-confirmation");
        var decline = new FakeExitEnvironment(active);
        questions = 0;
        if ((await ApplicationExit.RunAsync(decline, false, _ => Task.FromResult(++questions == 1))).Closed || decline.Forces != 0)
            throw new Exception("拒绝强退后仍关闭界面或进程");
        var failure = new FakeExitEnvironment(active) { ForceFails = true };
        if ((await ApplicationExit.RunAsync(failure, false, _ => Task.FromResult(true))).Closed) throw new Exception("强退失败却误报退出");
        Console.WriteLine("PASS exit-declined-or-failed-force-keeps-ui");
        var wrong = new FakeExitEnvironment(new GuardianStatus { GuardianProcessId = 999 });
        if ((await ApplicationExit.RunAsync(wrong, false, _ => throw new Exception("错误进程不应进入确认"))).Closed || wrong.Requests != 0)
            throw new Exception("操作了错误会话的进程");
        Console.WriteLine("PASS exit-rejects-mismatched-process-identity");

        var store = new SettingsStore(Path.Combine(directory, "exit-engine"));
        store.Save(new AppSettings { AutoConnectObs = false, AutoLaunchObs = false, AutoStartGuardianWithWindows = false,
            BatteryProtectionEnabled = false, RequireMkv = false, EnableAutomaticSplit = false, StopRecordingWhenDiskLow = false,
            AllowStopStartSplitFallback = true, SplitFallbackDelayMilliseconds = 5000, AfterStopAction = PostRecordingAction.None,
            EmergencyAlwaysShutdown = false, FileFlushWaitSeconds = 0 });
        using var logger = new DurableLogger(store.LogDirectory);
        var obs = new FakeObs();
        await using (var engine = new GuardianEngine(store, logger, obs))
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var splitting = engine.HandleCommandAsync(new IpcRequest { Command = "split_record" }, lifetime.Token);
            await obs.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var exit = engine.RequestApplicationExit();
            try { await splitting; } catch (OperationCanceledException) { }
            var refused = await engine.HandleCommandAsync(new IpcRequest { Command = "start_record" }, lifetime.Token);
            if (!exit.Success || !engine.GetStatus().ApplicationExiting || refused.Success || obs.Starts != 0)
                throw new Exception("退出后继续启动分段或接受新操作");
        }
        Console.WriteLine("PASS exit-cancels-split-and-rejects-new-commands");

        // Real local pipe, fake OBS, and no real power APIs or startup registration.
        await using (var engine = new GuardianEngine(store, logger, new FakeObs()))
        {
            using var serverLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var pipe = "OBG.Exit." + Guid.NewGuid().ToString("N");
            var exited = false;
            var server = new GuardianIpcServer(engine, logger, pipe, () => { exited = true; serverLifetime.Cancel(); });
            var serving = server.RunAsync(serverLifetime.Token);
            var invalid = await GuardianIpcClient.SendAsync("exit_application_confirmed", 2000, pipeName: pipe, arguments: new() { ["processId"] = "-1" });
            if (invalid.Success || exited) throw new Exception("退出 IPC 未验证进程身份");
            var accepted = await GuardianIpcClient.SendAsync("exit_application_confirmed", 2000, pipeName: pipe, arguments: new() { ["processId"] = Environment.ProcessId.ToString() });
            await serving.WaitAsync(TimeSpan.FromSeconds(2));
            if (!accepted.Success || !exited) throw new Exception("正常退出协议或监听器关闭失败");
        }
        Console.WriteLine("PASS exit-ipc-identity-ack-and-listener-shutdown");
    }

    private sealed class FakeExitEnvironment(GuardianStatus status) : IApplicationExitEnvironment
    {
        private readonly GuardProcessIdentity target = new(10, @"D:\isolated-test\ObsBatteryGuard.Guard.exe", 100);
        public bool Graceful, ForceFails, Confirmed, Gone;
        public int Requests, Forces;
        public IReadOnlyList<GuardProcessIdentity> FindGuardians() => Gone ? [] : [target];
        public Task<IpcResponse> ReadStatusAsync() => Task.FromResult(IpcResponse.Ok("fake", status));
        public void OnExitConfirmed() => Confirmed = true;
        public Task RequestExitAsync(GuardProcessIdentity process)
        {
            if (!Confirmed || process != target) throw new Exception("未确认或目标错误");
            Requests++; Gone = Graceful; return Task.CompletedTask;
        }
        public Task<bool> WaitForExitAsync(IReadOnlyList<GuardProcessIdentity> targets, TimeSpan timeout) => Task.FromResult(Gone);
        public void ForceExit(GuardProcessIdentity process) { if (process != target) throw new Exception("错误强退目标"); Forces++; Gone = !ForceFails; }
    }

    private sealed class FakeObs : IObsClient
    {
        public bool IsConnected => true;
        public string RecordDirectory => Path.GetTempPath();
        public int Starts;
        private bool recording = true;
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ObsRequestResult> ConnectAsync(string h, int p, string password, int seconds, CancellationToken token) => Task.FromResult(new ObsRequestResult(true, "fake"));
        public Task<ObsSnapshot> GetSnapshotAsync(CancellationToken token) => Task.FromResult(new ObsSnapshot(true, recording, false, TimeSpan.Zero, 0, "", "test", "", ""));
        public Task<ObsRequestResult> StartRecordingAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Starts++; recording = true; return Task.FromResult(new ObsRequestResult(true, "fake")); }
        public Task<ObsRequestResult> StopRecordingAsync(CancellationToken token) { recording = false; Stopped.TrySetResult(); return Task.FromResult(new ObsRequestResult(true, "fake")); }
        public Task<ObsRequestResult> SplitRecordingAsync(CancellationToken token) => Task.FromResult(new ObsRequestResult(false, "fallback"));
        public Task<string> GetRecordDirectoryAsync(CancellationToken token) => Task.FromResult(RecordDirectory);
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
