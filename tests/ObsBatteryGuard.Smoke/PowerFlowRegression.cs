using System.Collections.Concurrent;
using ObsBatteryGuard.Core;

internal static class PowerFlowRegression
{
    public static async Task RunAsync(string root)
    {
        var conflict = new PowerPolicySnapshot(true, false, 25, 20, 1, 2, 1, 60, "");
        var warnings = WindowsPowerPolicy.Evaluate(conflict, new AppSettings { StopBatteryPercent = 20, PreventSystemSleepWhileRecording = false });
        Check(warnings.Count(line => line.StartsWith("风险")) == 4, "系统电源策略冲突没有完整报告");
        var missing = WindowsPowerPolicy.Evaluate(new(false, false, null, null, null, null, null, null, "模拟不可读"), new());
        Check(missing.Single().StartsWith("待确认"), "不可读策略被当作安全");
        Console.WriteLine("PASS power-policy-conflicts-and-unknown-state");

        await using (var test = new Scenario(root, "normal"))
        {
            test.Start(); test.Percent = 15;
            await Until(() => test.Power.Calls.Count == 1);
            Check(test.Obs.Stops > 0 && !test.Obs.Recording, "未停止录像就提交普通电源动作");
            Check(test.Power.Calls.Single() == (PostRecordingAction.Shutdown, false), "普通关机强制选项不正确");
            await Task.Delay(1200);
            Check(test.Power.Calls.Count == 1, "电源动作重复提交");
            var cancel = await test.Command("cancel_shutdown");
            Check(!cancel.Success, "已提交后误报取消成功");
            Console.WriteLine("PASS engine-stop-before-power-single-submit-and-cancel-boundary");
        }

        await using (var test = new Scenario(root, "ac-cancel", s => { s.ShutdownDelaySeconds = 4; s.ResumeRecordingWhenAcReturns = true; }))
        {
            test.Start(); test.Percent = 15;
            await Until(() => test.Engine.GetStatus().ShutdownScheduled);
            test.OnAc = true;
            await Until(() => !test.Engine.GetStatus().ShutdownScheduled && test.Obs.Starts > 0);
            await Task.Delay(4200);
            Check(test.Power.Calls.IsEmpty && test.Obs.Recording, "来电取消/恢复录像失败，或已取消计划仍然执行");
            Console.WriteLine("PASS engine-ac-return-cancels-countdown-and-resumes");
        }

        await using (var test = new Scenario(root, "escalate", s => s.ShutdownDelaySeconds = 60))
        {
            test.Start(); test.Percent = 15;
            await Until(() => test.Engine.GetStatus().ShutdownScheduled);
            test.Percent = 8;
            await Until(() => test.Power.Calls.Count == 1);
            Check(test.Power.Calls.Single() == (PostRecordingAction.Shutdown, true), "紧急计划未缩短普通计划或未使用紧急强制选项");
            Console.WriteLine("PASS engine-emergency-escalates-normal-countdown");
        }

        await using (var test = new Scenario(root, "unknown-stop"))
        {
            test.Obs.Unknown = true; test.Start(); test.Percent = 15;
            await Until(() => test.Engine.GetStatus().LastError.Contains("普通关机已阻止"), 6000);
            Check(test.Power.Calls.IsEmpty, "未知录像状态仍提交普通关机");
            test.Percent = 8;
            await Until(() => test.Power.Calls.Count == 1, 6000);
            Check(test.Power.Calls.Single().Force, "停止无法确认时紧急兜底未执行");
            Console.WriteLine("PASS engine-unknown-stop-blocks-normal-but-retains-emergency-fallback");
        }

        await using (var test = new Scenario(root, "power-failure"))
        {
            test.Power.Succeed = false; test.Start(); test.Percent = 15;
            await Until(() => test.Engine.GetStatus().LastError.Contains("模拟电源提交失败"));
            Check(!test.Engine.GetStatus().ShutdownScheduled, "电源提交失败后仍显示待关机");
            Console.WriteLine("PASS engine-power-submit-failure-is-visible");
        }

        await using (var test = new Scenario(root, "exit-cancel", s => s.ShutdownDelaySeconds = 2))
        {
            test.Start(); test.Percent = 15;
            await Until(() => test.Engine.GetStatus().ShutdownScheduled);
            Check(test.Engine.RequestApplicationExit().Success, "未接受退出请求");
            var status = test.Engine.GetStatus();
            Check(status.ApplicationExiting && !status.ShutdownScheduled && !status.ProtectionEnabled && status.NextSplitAt is null,
                "退出后倒计时或保护仍显示启用");
            test.Percent = 8;
            await Task.Delay(2300);
            Check(test.Power.Calls.IsEmpty, "退出后仍执行倒计时或紧急电源动作");
            Console.WriteLine("PASS exit-cancels-pending-power-and-blocks-emergency");
        }

        await using (var test = new Scenario(root, "exit-committed"))
        {
            test.Start(); test.Percent = 15;
            await Until(() => test.Power.Calls.Count == 1);
            test.Engine.RequestApplicationExit();
            var status = test.Engine.GetStatus();
            Check(status.PowerActionCommitted && !status.ShutdownScheduled &&
                ApplicationExit.BuildPrompt(status, false, true).Contains("不能撤销"), "退出错误地宣称撤回已提交动作");
            await Task.Delay(1100);
            Check(test.Power.Calls.Count == 1, "退出后重复提交电源动作");
            Console.WriteLine("PASS exit-preserves-committed-power-warning");
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("模拟电源流程等待状态超时");
            await Task.Delay(25);
        }
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public readonly StubObs Obs = new();
        public readonly StubPower Power = new();
        public readonly GuardianEngine Engine;
        private readonly DurableLogger _logger;
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(20));
        private Task _loop = Task.CompletedTask;
        public volatile int Percent = 80;
        public volatile bool OnAc;
        public Scenario(string root, string name, Action<AppSettings>? configure = null)
        {
            var store = new SettingsStore(Path.Combine(root, "power-flow-" + name));
            var settings = new AppSettings
            {
                AutoConnectObs = false, AutoLaunchObs = false, AutoStartGuardianWithWindows = false,
                AutoStartRecordingWhenObsConnects = false, RequireMkv = false, EnableAutomaticSplit = false,
                StopRecordingWhenDiskLow = false, MinimumDiskFreeGb = 0.1, PreventSystemSleepWhileRecording = false,
                BatteryProtectionEnabled = true, BatteryPollSeconds = 1, ConsecutiveLowReadings = 1,
                StopBatteryPercent = 20, EmergencyBatteryPercent = 10, ObsStopTimeoutSeconds = 3,
                FileFlushWaitSeconds = 0, ShutdownDelaySeconds = 0, EmergencyShutdownDelaySeconds = 0,
                AfterStopAction = PostRecordingAction.Shutdown, EmergencyAlwaysShutdown = true,
                ForceCloseAppsOnShutdown = false, ForceCloseAppsOnEmergency = true
            };
            configure?.Invoke(settings); store.Save(settings);
            _logger = new DurableLogger(store.LogDirectory);
            // Every scenario injects an in-memory power controller. No Windows power command can be called.
            Engine = new GuardianEngine(store, _logger, Obs,
                () => new(true, Percent, OnAc, false, false, 60, DateTimeOffset.Now, "模拟电池"), Power);
        }
        public void Start() => _loop = Engine.RunAsync(_lifetime.Token);
        public Task<IpcResponse> Command(string command) => Engine.HandleCommandAsync(new() { Command = command }, _lifetime.Token);
        public async ValueTask DisposeAsync()
        { _lifetime.Cancel(); await _loop; await Engine.DisposeAsync(); _logger.Dispose(); _lifetime.Dispose(); }
    }

    private sealed class StubPower : IPowerController
    {
        public readonly ConcurrentQueue<(PostRecordingAction Action, bool Force)> Calls = new();
        public bool Succeed = true;
        public bool Execute(PostRecordingAction action, bool force, out string error)
        { Calls.Enqueue((action, force)); error = Succeed ? "" : "模拟电源提交失败"; return Succeed; }
    }
    private sealed class StubObs : IObsClient
    {
        public bool IsConnected => true;
        public string RecordDirectory => Path.GetTempPath();
        public volatile bool Recording = true;
        public volatile bool Unknown;
        public int Starts;
        public int Stops;
        public Task<ObsSnapshot> GetSnapshotAsync(CancellationToken token) => Task.FromResult(
            new ObsSnapshot(true, !Unknown && Recording, false, TimeSpan.Zero, 0, "", "test", "test", Unknown ? "模拟查询失败" : "", RecordDirectory));
        public Task<ObsRequestResult> ConnectAsync(string host, int port, string password, int timeoutSeconds, CancellationToken token) => Task.FromResult(new ObsRequestResult(true, "test"));
        public Task<ObsRequestResult> StartRecordingAsync(CancellationToken token)
        { Recording = true; Interlocked.Increment(ref Starts); return Task.FromResult(new ObsRequestResult(true, "test")); }
        public Task<ObsRequestResult> StopRecordingAsync(CancellationToken token)
        { if (!Unknown) Recording = false; Interlocked.Increment(ref Stops); return Task.FromResult(new ObsRequestResult(!Unknown, "test")); }
        public Task<ObsRequestResult> SplitRecordingAsync(CancellationToken token) => Task.FromResult(new ObsRequestResult(true, "test"));
        public Task<string> GetRecordDirectoryAsync(CancellationToken token) => Task.FromResult(RecordDirectory);
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
