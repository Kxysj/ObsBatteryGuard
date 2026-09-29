using ObsBatteryGuard.Core;

internal static class ObsLaunchRegression
{
    public static async Task RunAsync(string directory)
    {
        if (!ObsProcessLauncher.IsLocalHost("127.0.0.1") || !ObsProcessLauncher.IsLocalHost("[::1]") ||
            !ObsProcessLauncher.IsLocalHost("localhost") || ObsProcessLauncher.IsLocalHost("192.0.2.1"))
            throw new Exception("本机地址判断错误");
        await RunCase("manual-confirmed", false, false, true, 3, true, 1);
        await RunCase("auto-launch", false, true, false, 2, true, 1);
        await RunCase("already-running", true, false, false, 1, true, 0);
        await RunCase("no-launch-permission", false, false, false, 2, false, 0);
        await RunCase("launch-failure", false, false, true, 1, false, 1, failLaunch: true);
        await RunCase("remote-never-launch", false, true, true, 1, true, 0, host: "192.0.2.1");

        async Task RunCase(string name, bool running, bool autoLaunch, bool confirmed, int readyAfter, bool success, int launches,
            bool failLaunch = false, string host = "127.0.0.1")
        {
            var store = new SettingsStore(Path.Combine(directory, "launch-" + name));
            store.Save(new AppSettings { AutoConnectObs = false, AutoLaunchObs = autoLaunch, ObsHost = host,
                AutoStartGuardianWithWindows = false, AutoStartRecordingWhenObsConnects = false,
                BatteryProtectionEnabled = false, AfterStopAction = PostRecordingAction.None, EmergencyAlwaysShutdown = false });
            using var logger = new DurableLogger(store.LogDirectory);
            var launcher = new FakeLauncher { IsRunning = running, Fail = failLaunch };
            var obs = new FakeObs(readyAfter);
            await using var engine = new GuardianEngine(store, logger, obs, obsLauncher: launcher);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var result = await engine.HandleCommandAsync(new IpcRequest { Command = confirmed ? "connect_obs_launch" : "connect_obs" }, limit.Token);
            if (result.Success != success || launcher.Launches != launches || obs.StartCalls != 0)
                throw new Exception("OBS 启动流程不符合预期：" + name);
            if (failLaunch && (obs.Attempts != 0 || !result.Message.Contains("模拟启动失败")))
                throw new Exception("启动失败未立即给出原因");
            if (success && confirmed && host == "127.0.0.1")
            {
                await engine.HandleCommandAsync(new IpcRequest { Command = "connect_obs_launch" }, limit.Token);
                if (launcher.Launches != launches) throw new Exception("重复打开 OBS");
            }
            Console.WriteLine("PASS obs-launch-" + name);
        }
    }

    private sealed class FakeLauncher : IObsProcessLauncher
    {
        public bool IsRunning { get; set; }
        public bool Fail { get; init; }
        public int Launches { get; private set; }
        public ObsRequestResult EnsureStarted(string path)
        {
            Launches++;
            // Deliberately keep IsRunning false: process discovery may lag after launch.
            return new(!Fail, Fail ? "模拟启动失败" : "模拟启动成功");
        }
    }

    private sealed class FakeObs(int readyAfter) : IObsClient
    {
        public bool IsConnected { get; private set; }
        public int Attempts { get; private set; }
        public int StartCalls { get; private set; }
        public string RecordDirectory => "";
        public Task<ObsRequestResult> ConnectAsync(string host, int port, string password, int timeoutSeconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            IsConnected = ++Attempts >= readyAfter;
            return Task.FromResult(new ObsRequestResult(IsConnected, IsConnected ? "模拟连接成功" : "模拟服务未就绪"));
        }
        public Task<ObsSnapshot> GetSnapshotAsync(CancellationToken token) => Task.FromResult(new ObsSnapshot(IsConnected, false, false, TimeSpan.Zero, 0, "", "31", "test", ""));
        public Task<ObsRequestResult> StartRecordingAsync(CancellationToken token) { StartCalls++; return Task.FromResult(new ObsRequestResult(false, "禁止真实录制")); }
        public Task<ObsRequestResult> StopRecordingAsync(CancellationToken token) => Task.FromResult(new ObsRequestResult(false, "禁止真实录制"));
        public Task<ObsRequestResult> SplitRecordingAsync(CancellationToken token) => Task.FromResult(new ObsRequestResult(false, "禁止真实录制"));
        public Task<string> GetRecordDirectoryAsync(CancellationToken token) => Task.FromResult("");
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
