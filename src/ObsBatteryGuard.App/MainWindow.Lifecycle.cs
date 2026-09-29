using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ObsBatteryGuard.Core;

namespace ObsBatteryGuard.App;

public partial class MainWindow
{
    private bool _closeWorkflow;
    private bool _exitRequested;
    private bool _allowClose;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if ((_previewMode && !_trayLifecycleCheck) || _allowClose) return;
        e.Cancel = true;
        if (_closeWorkflow) return;
        if (HideToTray()) return;
        // No usable tray: keep the window reachable and offer the same confirmed exit.
        // Defer until Closing returns, since approved exit calls Close() again.
        Dispatcher.BeginInvoke(new Action(async () => await RequestExitAsync(trayUnavailable: true)));
    }

    private async Task RequestExitAsync(bool trayUnavailable = false,
        IApplicationExitEnvironment? environment = null, Func<string, Task<bool>>? confirm = null)
    {
        if (_closeWorkflow || _allowClose) return;
        RestoreFromTray();
        _closeWorkflow = true;
        SafeTrayAction(tray => tray.Update(new(TrayIndicator.Busy, "正在检查退出任务", "OBS 电池安全录制\n正在检查退出任务", "取消退出将保持原有运行状态")));
        _statusTimer.Stop();
        // Only exit confirmation remains interactive while checking and stopping the guardian.
        ((UIElement)Content).IsEnabled = false;
        try
        {
            var result = await ApplicationExit.RunAsync(environment ?? new WindowExitEnvironment(this), _settingsDirty,
                confirm ?? (message => Task.FromResult(MessageBox.Show(this,
                    (trayUnavailable ? "托盘不可用，无法收起窗口。\n\n" : "") + message, "退出软件", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)));
            if (result.Closed)
            {
                _allowClose = true;
                Close();
                return;
            }
            PhaseDetailText.Text = result.Message;
            if (!result.UserCancelled) MessageBox.Show(this, result.Message, "软件尚未完全退出", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            PhaseDetailText.Text = "退出失败：" + ex.Message;
            MessageBox.Show(this, PhaseDetailText.Text + "\n操作界面已保留，请检查后台是否仍在运行。", "退出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _closeWorkflow = false;
            if (!_allowClose)
            {
                _exitRequested = false;
                ((UIElement)Content).IsEnabled = true;
                await RefreshStatusAsync();
                _statusTimer.Start();
            }
        }
    }

    private sealed class WindowExitEnvironment(MainWindow owner) : IApplicationExitEnvironment
    {
        private static int CurrentSessionId()
        {
            using var current = Process.GetCurrentProcess();
            return current.SessionId;
        }
        public IReadOnlyList<GuardProcessIdentity> FindGuardians()
        {
            var identities = new List<GuardProcessIdentity>();
            var processes = Process.GetProcessesByName("ObsBatteryGuard.Guard");
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.HasExited || process.SessionId != CurrentSessionId()) continue;
                        var path = process.MainModule?.FileName ?? throw new InvalidOperationException("无法读取后台程序路径");
                        if (!IsOurGuardian(path)) throw new InvalidOperationException($"PID {process.Id} 的程序身份无法确认，未结束该进程。");
                        identities.Add(new(process.Id, Path.GetFullPath(path), process.StartTime.ToUniversalTime().Ticks));
                    }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            return identities;
        }

        private static bool IsOurGuardian(string path) =>
            Path.GetFileName(path).Equals("ObsBatteryGuard.Guard.exe", StringComparison.OrdinalIgnoreCase) &&
            FileVersionInfo.GetVersionInfo(path).ProductName == "OBS 电池安全录制";

        public Task<IpcResponse> ReadStatusAsync() => GuardianIpcClient.SendAsync("get_status", 1500);
        public void OnExitConfirmed()
        {
            owner._exitRequested = true;
            owner.SafeTrayAction(tray => tray.Update(new(TrayIndicator.Busy, "正在退出全部功能", "OBS 电池安全录制\n正在退出全部功能", "已开始退出，取消后续强退不会恢复已停止的任务")));
            owner.HeaderStatusText.Text = "正在退出整个软件…";
            owner.PhaseDetailText.Text = "正在取消后台任务并等待守护进程退出。";
        }
        public async Task RequestExitAsync(GuardProcessIdentity target) =>
            await GuardianIpcClient.SendAsync("exit_application_confirmed", 2000,
                arguments: new() { ["processId"] = target.Id.ToString() });

        public async Task<bool> WaitForExitAsync(IReadOnlyList<GuardProcessIdentity> targets, TimeSpan timeout)
        {
            var timer = Stopwatch.StartNew();
            while (targets.Any(IsAlive))
            {
                if (timer.Elapsed >= timeout) return false;
                await Task.Delay(100);
            }
            return true;
        }

        private static bool IsAlive(GuardProcessIdentity target)
        {
            try
            {
                using var process = Process.GetProcessById(target.Id);
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == target.StartTicks;
            }
            catch (ArgumentException) { return false; }
        }

        public void ForceExit(GuardProcessIdentity target)
        {
            if (!IsAlive(target)) return;
            using var process = Process.GetProcessById(target.Id);
            var path = process.MainModule?.FileName ?? "";
            if (process.SessionId != CurrentSessionId() ||
                process.StartTime.ToUniversalTime().Ticks != target.StartTicks ||
                !string.Equals(path, target.Path, StringComparison.OrdinalIgnoreCase) || !IsOurGuardian(path))
                throw new InvalidOperationException("后台进程身份发生变化，已放弃强制退出。");
            process.Kill(); // Never kill OBS, another application's process, or a child-process tree.
        }
    }
}
