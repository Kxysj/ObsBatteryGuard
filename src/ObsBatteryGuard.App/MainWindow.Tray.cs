using System.Windows;
using ObsBatteryGuard.Core;

namespace ObsBatteryGuard.App;

public partial class MainWindow
{
    private DesktopTrayIcon? _tray;
    private WindowState _lastRestoredState = WindowState.Normal;

    private void InitializeTray()
    {
        if (_previewMode || _tray is not null) return;
        try
        {
            _tray = CreateTrayIcon();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "系统托盘图标未能初始化：" + ex.Message + "\n软件界面将继续保留，请通过任务栏确认运行状态。",
                "托盘不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private DesktopTrayIcon CreateTrayIcon(bool visible = true, Func<Task>? exit = null) =>
        new(RestoreFromTray, () => HideToTray(),
            () => { RestoreFromTray(); if (!_closeWorkflow) LogsNav_Click(this, new RoutedEventArgs()); },
            async () => await (exit?.Invoke() ?? RequestExitAsync()), visible);

    private bool HideToTray()
    {
        if (_closeWorkflow || _allowClose) return false;
        if (_tray is null)
        {
            RestoreFromTray();
            return false;
        }
        // Hide only the window: timers, unsaved controls and guardian ownership stay alive.
        Hide();
        return true;
    }

    private void RestoreFromTray()
    {
        if (_allowClose || Dispatcher.HasShutdownStarted) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _lastRestoredState;
        Activate();
        Focus();
    }

    private void UpdateTray(GuardianStatus? status, string? offlineReason = null)
    {
        if (_closeWorkflow || _exitRequested || _allowClose) return;
        SafeTrayAction(tray => tray.Update(TrayStatusPresentation.Create(status, CurrentAppVersion, offlineReason)));
    }

    private void SafeTrayAction(Action<DesktopTrayIcon> action)
    {
        if (_tray is null) return;
        try { action(_tray); }
        catch (Exception ex)
        {
            DisposeTray();
            RestoreFromTray();
            SettingsNoticeText.Text = "托盘暂不可用，已保留窗口；点击 × 可确认退出软件：" + ex.Message;
        }
    }

    private void DisposeTray()
    {
        var tray = _tray;
        _tray = null;
        try { tray?.Dispose(); } catch { }
    }
}
