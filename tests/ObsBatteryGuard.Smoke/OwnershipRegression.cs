using System.Diagnostics;
using System.Text.Json;
using ObsBatteryGuard.Core;

internal static class OwnershipRegression
{
    private static string TestExe => Path.Combine(AppContext.BaseDirectory, "ObsBatteryGuard.Smoke.exe");
    private static Process Spawn(params string[] args)
    {
        var start = new ProcessStartInfo(TestExe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new Exception("无法启动隔离子进程");
    }
    public static async Task OwnerAsync(string guard, string directory)
    {
        var pipe = "OBG.Owner." + Guid.NewGuid().ToString("N");
        using var helper = OwnedProcess.Start(guard, $"--exit-test \"{Path.Combine(directory, "guard")}\" {pipe}");
        using var controller = OwnedProcess.Start(TestExe, $"--ownership-controller \"{directory}\"");
        File.WriteAllText(Path.Combine(directory, "owner-ready.json"), JsonSerializer.Serialize(new[] { helper.Process.Id, controller.Process.Id }));
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!File.Exists(Path.Combine(directory, "close-owner")) && DateTime.UtcNow < deadline) await Task.Delay(50);
    }
    public static async Task ControllerAsync(string directory)
    {
        // This descendant stands in for an independently running OBS. Never launch real OBS.
        using var obsProcess = Spawn("--ownership-obs", directory);
        File.WriteAllText(Path.Combine(directory, "obs-pid"), obsProcess.Id.ToString());
        var store = new SettingsStore(Path.Combine(directory, "controller"));
        store.Save(new AppSettings { AutoConnectObs = false, AutoLaunchObs = false, AutoStartGuardianWithWindows = false,
            BatteryProtectionEnabled = false, EmergencyAlwaysShutdown = false, AfterStopAction = PostRecordingAction.None,
            EnableAutomaticSplit = false, RequireMkv = false, PreventSystemSleepWhileRecording = false });
        using var logger = new DurableLogger(store.LogDirectory);
        await using var engine = new GuardianEngine(store, logger, new TraceObs(directory));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                await engine.HandleCommandAsync(new() { Command = "split_record" }, timeout.Token);
                await Task.Delay(150, timeout.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public static async Task ObsAsync(string directory)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(Path.Combine(directory, "stop-obs")) && DateTime.UtcNow < deadline) await Task.Delay(50);
    }
    public static async Task RunAsync(string guard)
    {
        using var current = Process.GetCurrentProcess();
        var args = new[] { "--background", "--owner-process", current.Id.ToString(), "--owner-start", current.StartTime.ToUniversalTime().Ticks.ToString() };
        using var valid = GuardianOwner.Open(args, Environment.ProcessPath!);
        if (valid is null || GuardianOwner.Open(["--background"], Environment.ProcessPath!) is not null ||
            GuardianOwner.Open(args, Path.Combine(Path.GetTempPath(), "wrong.exe")) is not null) throw new Exception("守护所属界面校验错误");
        args[4] = "0";
        if (GuardianOwner.Open(args, Environment.ProcessPath!) is not null) throw new Exception("未校验 PID 复用");
        Console.WriteLine("PASS ownership-owner-required-and-start-time-identity");

        var root = Path.Combine(Path.GetTempPath(), "OBG-Ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = Path.Combine(root, "basic", "profiles", "test");
            Directory.CreateDirectory(profile);
            var iniPath = Path.Combine(profile, "basic.ini");
            const string ini = "[General]\nName=测试\n[Output]\nMode=Advanced\n[AdvOut]\nRecSplitFile=true\nRecSplitFileType=Time\nRecSplitFileTime=1\n";
            File.WriteAllText(iniPath, ini);
            var report = ObsProfileManager.InspectNativeSplit("测试", root);
            if (!report.Contains("独立于本软件") || File.ReadAllText(iniPath) != ini) throw new Exception("原生分割诊断不准确或改写了 OBS 设置");
            File.WriteAllText(iniPath, ini.Replace("Mode=Advanced", "Mode=Simple"));
            if (ObsProfileManager.InspectNativeSplit("测试", root).Contains("已在磁盘配置启用")) throw new Exception("误把非当前输出模式认定为开启分割");
            Console.WriteLine("PASS native-split-readonly-diagnostic-no-settings-changes");
            foreach (var force in new[] { false, true })
            {
                var directory = Path.Combine(root, force ? "killed" : "normal");
                Directory.CreateDirectory(directory);
                using var owner = Spawn("--ownership-owner", Path.GetFullPath(guard), directory);
                Process? helper = null, controller = null, obs = null;
                try
                {
                    await Until(() => File.Exists(Path.Combine(directory, "owner-ready.json")) && File.Exists(Path.Combine(directory, "obs-pid")) &&
                        File.Exists(Path.Combine(directory, "splits.log")), 10000);
                    var children = JsonSerializer.Deserialize<int[]>(File.ReadAllText(Path.Combine(directory, "owner-ready.json")))!;
                    helper = Process.GetProcessById(children[0]); controller = Process.GetProcessById(children[1]);
                    obs = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(directory, "obs-pid"))));
                    if (force) owner.Kill(); // Exact test process returned by Spawn; never kill any production process.
                    else File.WriteAllText(Path.Combine(directory, "close-owner"), "close");
                    await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.WhenAll(helper.WaitForExitAsync(), controller.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(3));
                    var trace = File.ReadAllText(Path.Combine(directory, "splits.log"));
                    await Task.Delay(700);
                    if (trace != File.ReadAllText(Path.Combine(directory, "splits.log")) || obs.HasExited)
                        throw new Exception("界面退出后仍有分割指令，或误杀了独立 OBS 替身");
                    Console.WriteLine(force ? "PASS ownership-ui-killed-no-guardian-no-split-obs-survives" : "PASS ownership-ui-close-no-guardian-no-split-obs-survives");
                }
                finally
                {
                    File.WriteAllText(Path.Combine(directory, "close-owner"), "close");
                    File.WriteAllText(Path.Combine(directory, "stop-obs"), "stop");
                    await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    if (obs is not null) await obs.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    helper?.Dispose(); controller?.Dispose(); obs?.Dispose();
                }
            }
            var legacyDirectory = Path.Combine(root, "legacy");
            Directory.CreateDirectory(legacyDirectory);
            using (var legacy = Spawn("--ownership-obs", legacyDirectory))
            {
                try
                {
                    try
                    {
                        using var refused = OwnedProcess.Attach(legacy.Id, 0, TestExe);
                        throw new Exception("接管时未校验启动时间");
                    }
                    catch (InvalidOperationException) { }
                    if (legacy.HasExited) throw new Exception("拒绝接管却结束了不匹配进程");
                    using (var adopted = OwnedProcess.Attach(legacy.Id, legacy.StartTime.ToUniversalTime().Ticks, TestExe)) { }
                    await legacy.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    Console.WriteLine("PASS ownership-adopted-legacy-helper-exits-with-owner");
                }
                finally
                {
                    File.WriteAllText(Path.Combine(legacyDirectory, "stop-obs"), "stop");
                    await legacy.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            using var orphan = Process.Start(new ProcessStartInfo(Path.GetFullPath(guard), "--background") { UseShellExecute = false, CreateNoWindow = true })!;
            await orphan.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (orphan.ExitCode != 3) throw new Exception("孤立守护未被拒绝");
            Console.WriteLine("PASS ownership-orphan-background-start-refused");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task Until(Func<bool> condition, int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("隔离进程启动超时"); await Task.Delay(50); }
    }
    private sealed class TraceObs(string directory) : IObsClient
    {
        public bool IsConnected => true;
        public string RecordDirectory => directory;
        public Task<ObsRequestResult> ConnectAsync(string h, int p, string password, int seconds, CancellationToken t) => Task.FromResult(new ObsRequestResult(true, "fake"));
        public Task<ObsSnapshot> GetSnapshotAsync(CancellationToken t) => Task.FromResult(new ObsSnapshot(true, true, false, TimeSpan.Zero, 0, "", "test", "", ""));
        public Task<ObsRequestResult> StartRecordingAsync(CancellationToken t) => throw new Exception("不应调用开始");
        public Task<ObsRequestResult> StopRecordingAsync(CancellationToken t) => throw new Exception("不应调用停止");
        public Task<ObsRequestResult> SplitRecordingAsync(CancellationToken t)
        { t.ThrowIfCancellationRequested(); File.AppendAllText(Path.Combine(directory, "splits.log"), "split\n"); return Task.FromResult(new ObsRequestResult(true, "fake")); }
        public Task<string> GetRecordDirectoryAsync(CancellationToken t) => Task.FromResult(directory);
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
