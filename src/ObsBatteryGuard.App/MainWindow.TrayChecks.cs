using System.Diagnostics;
using System.IO;
using System.Windows;
using ObsBatteryGuard.Core;
using Forms = System.Windows.Forms;

namespace ObsBatteryGuard.App;

public partial class MainWindow
{
    private bool _trayLifecycleCheck;

    // Real WPF close/hide/restore events with an invisible window and hidden NotifyIcon.
    // Only an isolated test guardian and unique IPC pipe; no production config, OBS or power.
    internal static async Task RunTrayLifecycleChecksAsync(string directory)
    {
        var window = new MainWindow(new SettingsStore(Path.Combine(directory, "isolated-config")))
        {
            _previewMode = true, _trayLifecycleCheck = true,
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false
        };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        using var self = Process.GetCurrentProcess();
        var pipe = "OBG.TrayCheck." + Guid.NewGuid().ToString("N");
        try
        {
            window._ownedGuardian = OwnedProcess.Start(Path.Combine(AppContext.BaseDirectory, "ObsBatteryGuard.Guard.exe"),
                $"--owned-exit-test \"{Path.GetFullPath(directory)}\" {pipe} --owner-process {self.Id} --owner-start {self.StartTime.ToUniversalTime().Ticks}");
            using var helper = Process.GetProcessById(window._ownedGuardian.Process.Id);
            // Retain a native process handle before UI cleanup closes the ownership handle;
            // otherwise ExitCode can race with Windows releasing the exited process object.
            _ = helper.Handle;
            var response = await GuardianIpcClient.SendAsync("get_status", 8000, pipeName: pipe);
            if (!response.Success || response.Status?.GuardianProcessId != helper.Id) throw new Exception("隔离守护启动失败");
            var environment = new TrayCheckExitEnvironment(helper, pipe, () => window._exitRequested = true);
            var approve = false;
            Task? exitTask = null;
            DesktopTrayIcon CreateTray() => window.CreateTrayIcon(visible: false,
                exit: () => exitTask = window.RequestExitAsync(environment: environment, confirm: _ => Task.FromResult(approve)));
            window._tray = CreateTray();
            window.LoadSettingsIntoControls(new AppSettings());
            window.Show();
            // WPF disallows showing a maximized window when ShowActivated is false.
            // Match the production window's default for subsequent restore checks.
            window.ShowActivated = true;
            window.SelectSettingsGroup(1);
            window.SplitMinutesText.Text = "17";
            window._statusTimer.Interval = TimeSpan.FromMilliseconds(100);
            window._statusTimer.Start();
            var tray = window._tray;

            // Exercise the actual titlebar callback and WPF Closing event, not a policy mock.
            window.CloseButton_Click(window, new RoutedEventArgs());
            if (closed || window.IsVisible || !window._statusTimer.IsEnabled || window._ownedGuardian is null ||
                helper.HasExited || environment.Requests != 0 || !ReferenceEquals(tray, window._tray))
                throw new Exception("点击 × 未仅隐藏窗口，或错误停止了守护/计时器");
            response = await GuardianIpcClient.SendAsync("get_status", 2000, pipeName: pipe);
            if (!response.Success) throw new Exception("隐藏后守护未保持响应");
            Click("打开主界面");
            if (!window.IsVisible || !window._settingsDirty || window.SplitMinutesText.Text != "17" || window._selectedSettingsGroup != 1)
                throw new Exception("恢复窗口丢失了页面或未保存参数");

            // Repeated Loaded must not rerun production startup or reload unsaved controls.
            window._windowInitialized = true;
            window._previewMode = false;
            window.Window_Loaded(window, new RoutedEventArgs());
            window._previewMode = true;
            if (window.SplitMinutesText.Text != "17" || !window._settingsDirty) throw new Exception("重复加载丢失未保存设置");

            window.WindowState = WindowState.Maximized;
            window.Close();
            Click("打开主界面");
            if (!window.IsVisible || window.WindowState != WindowState.Maximized) throw new Exception("最大化状态未保留");
            window.WindowState = WindowState.Minimized;
            Click("收起到托盘（继续运行）");
            if (window.IsVisible) throw new Exception("托盘收起菜单未隐藏窗口");
            Click("打开主界面");
            if (window.WindowState != WindowState.Maximized) throw new Exception("最小化后未还原此前状态");

            window.Close();
            window.SafeTrayAction(_ => throw new InvalidOperationException("isolated tray failure"));
            if (!window.IsVisible || window._tray is not null || window.HideToTray() || helper.HasExited)
                throw new Exception("托盘故障使窗口不可达或停止守护");
            window._tray = CreateTray();
            window.Close();
            Click("退出软件…");
            await exitTask!;
            if (closed || environment.Requests != 0 || !window.IsVisible || !window._statusTimer.IsEnabled ||
                window._closeWorkflow || window._exitRequested || helper.HasExited || !((UIElement)window.Content).IsEnabled)
                throw new Exception("取消退出未保留运行状态");

            approve = true;
            window.Close();
            Click("退出软件…");
            await exitTask!;
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (!closed || window._tray is not null || window._ownedGuardian is not null || window._statusTimer.IsEnabled ||
                environment.Requests != 1 || helper.ExitCode != 0)
                throw new Exception("确认退出未释放托盘、计时器或所属守护");
            File.WriteAllText(Path.Combine(directory, "tray-lifecycle-checks.txt"),
                "PASS: actual X/Closing hides without stopping owned guardian or timer; hidden guardian IPC responsive; " +
                "tray open restores unsaved values/category; repeated Loaded guarded; maximized/minimized restoration; " +
                "tray hide menu; tray failure restores reachable UI; tray exit cancel preserves protection; " +
                "tray confirmed exit gracefully stops isolated guardian and disposes UI/tray/timer/job. " +
                "Invisible test window and hidden tray only; no production config/OBS/startup/power operations.");

            void Click(string caption) => window._tray!.Menu.Items.OfType<Forms.ToolStripMenuItem>()
                .Single(item => item.Text == caption).PerformClick();
        }
        finally
        {
            if (!closed) { window._allowClose = true; window.Close(); }
            window.DisposeOwnedGuardian();
        }
    }

    private sealed class TrayCheckExitEnvironment(Process helper, string pipe, Action confirmed) : IApplicationExitEnvironment
    {
        public int Requests { get; private set; }
        public IReadOnlyList<GuardProcessIdentity> FindGuardians() => helper.HasExited ? [] :
            [new(helper.Id, helper.MainModule!.FileName, helper.StartTime.ToUniversalTime().Ticks)];
        public Task<IpcResponse> ReadStatusAsync() => GuardianIpcClient.SendAsync("get_status", 2000, pipeName: pipe);
        public void OnExitConfirmed() => confirmed();
        public async Task RequestExitAsync(GuardProcessIdentity target)
        {
            if (target.Id != helper.Id) throw new Exception("隔离测试目标错误");
            Requests++;
            var response = await GuardianIpcClient.SendAsync("exit_application_confirmed", 2000, pipeName: pipe,
                arguments: new() { ["processId"] = helper.Id.ToString() });
            if (!response.Success) throw new Exception("隔离守护拒绝退出");
        }
        public async Task<bool> WaitForExitAsync(IReadOnlyList<GuardProcessIdentity> targets, TimeSpan timeout)
        {
            await helper.WaitForExitAsync().WaitAsync(timeout);
            return true;
        }
        public void ForceExit(GuardProcessIdentity target) => throw new Exception("隔离托盘测试不应强退");
    }
}
