using System.Windows;

namespace ObsBatteryGuard.App;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--tray-check")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunTrayCheckAsync(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--owner-check")
        {
            _ = RunOwnerCheckAsync(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--ui-preview")
        {
            var preview = ObsBatteryGuard.App.MainWindow.CreateUiPreview(e.Args[1]);
            MainWindow = preview;
            preview.Show();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--ui-check")
        {
            try { ObsBatteryGuard.App.MainWindow.RunUiChecks(e.Args[1]); Shutdown(0); }
            catch (Exception ex)
            {
                System.IO.Directory.CreateDirectory(e.Args[1]);
                System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "ui-failure.txt"), ex.ToString());
                Shutdown(1);
            }
            return;
        }
        base.OnStartup(e);
        _instance = new Mutex(false, @"Local\ObsBatteryGuard.App.v1", out var firstInstance);
        if (!firstInstance || HasOtherInterface())
        {
            MessageBox.Show("软件已经在运行，请从任务栏或右下角托盘打开现有界面。\n如要升级，请先退出旧界面和守护，再打开新版。", "已有运行实例", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        MainWindow = new ObsBatteryGuard.App.MainWindow();
        MainWindow.Show();
    }

    private async Task RunTrayCheckAsync(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            await ObsBatteryGuard.App.MainWindow.RunTrayLifecycleChecksAsync(directory);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "tray-lifecycle-failure.txt"), ex.ToString());
            Shutdown(1);
        }
    }

    private async Task RunOwnerCheckAsync(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var pipe = "OBG.UiOwner." + Guid.NewGuid().ToString("N");
            using var helper = ObsBatteryGuard.Core.OwnedProcess.Start(System.IO.Path.Combine(AppContext.BaseDirectory, "ObsBatteryGuard.Guard.exe"),
                $"--owned-exit-test \"{System.IO.Path.GetFullPath(directory)}\" {pipe} --owner-process {self.Id} --owner-start {self.StartTime.ToUniversalTime().Ticks}");
            var response = await ObsBatteryGuard.Core.GuardianIpcClient.SendAsync("get_status", 8000, pipeName: pipe);
            if (!response.Success || response.Status?.GuardianProcessId != helper.Process.Id) throw new Exception("实际 GUI 所属进程身份未通过验证");
            response = await ObsBatteryGuard.Core.GuardianIpcClient.SendAsync("exit_application_confirmed", 3000, pipeName: pipe,
                arguments: new() { ["processId"] = helper.Process.Id.ToString() });
            await helper.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (!response.Success || helper.Process.ExitCode != 0) throw new Exception("所属守护未正常退出");
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "owner-check.txt"), "PASS real-ui-owner-identity-job-bound-guardian-graceful-exit (isolated config/IPC; no OBS/startup/power)");
            Shutdown(0);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "owner-check-failure.txt"), ex.ToString());
            Shutdown(1);
        }
    }

    private static bool HasOtherInterface()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var candidates = System.Diagnostics.Process.GetProcessesByName("OBS电池安全录制");
        try
        {
            foreach (var process in candidates)
            {
                if (process.Id == current.Id) continue;
                try { if (!process.HasExited && process.SessionId == current.SessionId) return true; }
                catch (InvalidOperationException) { }
            }
            return false;
        }
        finally { foreach (var process in candidates) process.Dispose(); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as ObsBatteryGuard.App.MainWindow)?.DisposeOwnedGuardian();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
