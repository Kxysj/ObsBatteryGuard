using ObsBatteryGuard.Core;

namespace ObsBatteryGuard.Guard;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if ((args.Length == 3 && args[0] == "--exit-test") || (args.Length == 7 && args[0] == "--owned-exit-test"))
        {
            using var testOwner = args[0] == "--owned-exit-test"
                ? GuardianOwner.Open(["--background", .. args.Skip(3)], Path.Combine(AppContext.BaseDirectory, "OBS电池安全录制.exe")) : null;
            if (args[0] == "--owned-exit-test" && testOwner is null) return 3;
            var testStore = new SettingsStore(args[1]);
            testStore.Save(new AppSettings { AutoConnectObs = false, AutoLaunchObs = false, AutoStartGuardianWithWindows = false,
                AutoStartRecordingWhenObsConnects = false, BatteryProtectionEnabled = false, EmergencyAlwaysShutdown = false,
                AfterStopAction = PostRecordingAction.None });
            using var testLogger = new DurableLogger(testStore.LogDirectory);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var testEngine = new GuardianEngine(testStore, testLogger);
            var testIpc = new GuardianIpcServer(testEngine, testLogger, args[2], timeout.Cancel);
            await Task.WhenAll(testEngine.RunAsync(timeout.Token), testIpc.RunAsync(timeout.Token));
            return testEngine.GetStatus().ApplicationExiting ? 0 : 4;
        }
        if (args.Length == 2 && args[0] == "--preflight")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                var report = await ReadOnlyPreflight.RunAsync(timeout.Token);
                await File.WriteAllTextAsync(args[1], report, timeout.Token);
                return 0;
            }
            catch { return 1; }
        }
        if (args.Any(arg => arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
            return RunSelfTest();

        // Never run unattended from an obsolete --background startup entry or by double-clicking the helper.
        using var owner = GuardianOwner.Open(args, Path.Combine(AppContext.BaseDirectory, "OBS电池安全录制.exe"));
        if (owner is null) return 3;

        using var singleInstance = new Mutex(true, @"Local\ObsBatteryGuard.Guard.v1", out var createdNew);
        if (!createdNew) return 0;

        var store = new SettingsStore();
        using var logger = new DurableLogger(store.LogDirectory);
        logger.Info("lifecycle", "守护与操作界面绑定；界面退出后不再独立运行", new { ownerProcessId = owner.Id });
        var runMarker = Path.Combine(store.DataDirectory, "guardian.running");
        if (File.Exists(runMarker))
            logger.Warning("recovery", "检测到上一次后台守护可能异常结束；请检查最后一个录像分段");
        try
        {
            File.WriteAllText(runMarker, $"{DateTimeOffset.Now:O}|{Environment.ProcessId}");
        }
        catch { }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { File.Delete(runMarker); } catch { }
        };

        try
        {
            // Startup registration belongs to the full application, never this hidden helper.
            await using var engine = new GuardianEngine(store, logger);
            var ipc = new GuardianIpcServer(engine, logger, requestShutdown: cancellation.Cancel);
            async Task WatchOwnerAsync()
            {
                try { await owner.WaitForExitAsync(cancellation.Token); }
                catch (OperationCanceledException) { return; }
                engine.RequestApplicationExit();
                cancellation.Cancel();
            }
            await Task.WhenAll(engine.RunAsync(cancellation.Token), ipc.RunAsync(cancellation.Token), WatchOwnerAsync());
        }
        catch (Exception ex)
        {
            logger.Critical("lifecycle", "后台守护发生致命异常", new { ex.Message, ex.StackTrace });
        }
        finally
        {
            try { File.Delete(runMarker); } catch { }
        }
        return 0;
    }

    private static int RunSelfTest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ObsBatteryGuard.Guard.SelfTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(directory);
            var settings = new AppSettings { AutoStartGuardianWithWindows = false, AutoConnectObs = false, BatteryProtectionEnabled = false };
            settings.ObsPassword = "self-test";
            store.Save(settings);
            if (store.Load().ObsPassword != "self-test") return 2;
            using (var logger = new DurableLogger(store.LogDirectory)) logger.Info("self-test", "发布版守护程序自检通过");
            return 0;
        }
        catch
        {
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
        }
    }
}
