using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using ObsBatteryGuard.Core;

namespace ObsBatteryGuard.App;

public partial class MainWindow
{
    internal static MainWindow CreateUiPreview(string directory)
    {
        var window = new MainWindow(new SettingsStore(directory)) { _previewMode = true, Title = "OBS 电池安全录制 · 界面预览" };
        window.LoadSettingsIntoControls(new AppSettings());
        window.ShowPage(window.SettingsPage, window.SettingsNavButton);
        window.GuardianStateText.Text = "界面预览 · 未连接";
        window.SettingsNoticeText.Text = "预览模式：不会启动守护或修改实际配置。";
        return window;
    }

    // Offline harness: never shows a window or calls OBS, guardian IPC, startup or power actions.
    internal static void RunUiChecks(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        TrayIconChecks.Run(outputDirectory);
        var store = new SettingsStore(Path.Combine(outputDirectory, "isolated-config"));
        var window = new MainWindow(store) { _previewMode = true };
        if (window.AppVersionText.Text != $"v{CurrentAppVersion}")
            throw new Exception("左下角应只显示软件版本号");
        var defaults = new AppSettings { ObsPassword = "ui-test-only" };
        if (ChooseConnectCommand("127.0.0.1", false, false, () => false) is not null ||
            ChooseConnectCommand("127.0.0.1", false, false, () => true) != "connect_obs_launch" ||
            ChooseConnectCommand("127.0.0.1", false, true, () => throw new Exception("自动启动不应弹出确认")) != "connect_obs_launch" ||
            ChooseConnectCommand("127.0.0.1", true, false, () => throw new Exception("已运行不应询问启动")) != "connect_obs" ||
            ChooseConnectCommand("192.0.2.1", false, false, () => throw new Exception("远程地址不应启动本机 OBS")) != "connect_obs")
            throw new Exception("连接按钮确认策略错误");
        window.LoadSettingsIntoControls(defaults);
        if (window._controlGroups.Count != 45 || window._settingsGroups.Count != 5 ||
            window._controlGroups.Keys.Select(c => c.Name).Distinct().Count() != 45)
            throw new Exception("设置控件或分类数量错误");
        AssertSettingsEqual(defaults, window.ReadSettingsFromControls());
        store.Save(window.ReadSettingsFromControls());
        AssertSettingsEqual(defaults, store.Load());

        window.ShowPage(window.SettingsPage, window.SettingsNavButton);
        for (var group = 0; group < 5; group++)
        {
            window.SelectSettingsGroup(group);
            if (window._settingsDirty) throw new Exception("切换分类错误触发未保存状态");
            var toggle = (CheckBox)window._controlGroups.First(pair => pair.Value == group && pair.Key is CheckBox).Key;
            toggle.IsChecked = !toggle.IsChecked;
            if (!window._settingsDirty) throw new Exception("开关修改未触发未保存提示");
            window.LoadSettingsIntoControls(defaults);
        }
        window.SelectSettingsGroup(3);
        window.ObsPasswordBox.Password = "modified-ui-test";
        if (!window._settingsDirty) throw new Exception("密码修改未标记");
        window.SelectSettingsGroup(0);
        if (window.ReadSettingsFromControls().ObsPassword != "modified-ui-test") throw new Exception("切换分类丢失密码");
        window.LoadSettingsIntoControls(defaults);
        window.SelectSettingsGroup(4);
        window.RefreshIntervalText.Text = "750";
        if (!window._settingsDirty) throw new Exception("文本输入修改未标记");
        window.SelectSettingsGroup(0);
        if (window.ReadSettingsFromControls().StatusRefreshMilliseconds != 750) throw new Exception("切换分类丢失参数");
        window.LoadSettingsIntoControls(defaults);
        window.SelectSettingsGroup(2);
        window.AfterStopCombo.SelectedIndex = 0;
        if (!window._settingsDirty || window.ShutdownDelayText.IsEnabled) throw new Exception("下拉选项与关联禁用异常");
        window.LoadSettingsIntoControls(defaults);
        window.SelectSettingsGroup(0);
        window.SplitMinutesText.Text = "0";
        var rejected = false;
        try { window.ReadSettingsFromControls(); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected || window._selectedSettingsGroup != 1) throw new Exception("无效参数未拒绝或未跳转分类");
        window.LoadSettingsIntoControls(defaults);
        var chrome = WindowChrome.GetWindowChrome(window);
        if (chrome is null || chrome.GlassFrameThickness != new Thickness(0) ||
            chrome.NonClientFrameEdges != NonClientFrameEdges.None || window.ResizeMode != ResizeMode.CanResize ||
            !WindowChrome.GetIsHitTestVisibleInChrome(window.MaximizeWindowButton))
            throw new Exception("自定义窗口边框配置错误");

        window._latestStatus = new GuardianStatus
        {
            GuardianVersion = CurrentAppVersion, Phase = GuardPhase.Recording, PhaseText = "录像保护中",
            Battery = new(true, 68, false, false, false, 120, DateTimeOffset.Now, "使用电池 · 未充电"),
            Obs = new(true, true, false, TimeSpan.FromHours(25), 1250000000, @"D:\录像\测试片段.mkv", "31.1", "测试配置", "", @"D:\录像"),
            ObsUpdatedAt = DateTimeOffset.Now, ProtectionEnabled = true, WarningThreshold = 25, StopThreshold = 20,
            EmergencyThreshold = 10, NextSplitAt = DateTimeOffset.Now.AddMinutes(8), LastAction = "已确认 OBS 开始录像"
        };
        window.UpdateStatusUi(window._latestStatus);
        if (!window.RecordDurationText.Text.StartsWith("25:")) throw new Exception("超过 24 小时的录像时长错误");
        window._latestStatus.RecordingOperationBusy = true;
        window.UpdateStatusUi(window._latestStatus);
        if (window.SplitRecordButton.IsEnabled || window.StartRecordButton.IsEnabled) throw new Exception("忙碌状态未禁止冲突操作");
        window._latestStatus.RecordingOperationBusy = false;
        window.UpdateStatusUi(window._latestStatus);
        window._logContents = "2026-09-07 10:00:00 | 信息 | OBS 已连接\n    版本：31.1\n2026-09-07 10:01:00 | 警告 | 日志样例\n    详情：此为离线渲染，不是实际录制\n";
        window.ApplyLogFilter();
        var groups = new[] { "battery", "recording", "power", "connection", "startup" };
        foreach (var (width, height) in new[] { (800, 560), (900, 560), (1220, 800), (1600, 1000) })
        {
            window.ShowPage(window.SettingsPage, window.SettingsNavButton);
            for (var group = 0; group < groups.Length; group++)
            {
                window.SelectSettingsGroup(group);
                window.SettingsNoticeText.Text = "所有分类统一保存 · 切换分类保留未保存修改";
                Render($"settings-{groups[group]}-{width}", width, height);
                var footer = window.SaveSettingsButton.TransformToAncestor((Visual)window.Content)
                    .TransformBounds(new Rect(window.SaveSettingsButton.RenderSize));
                if (footer.Top < 0 || footer.Bottom > height || footer.Right > width || window.SettingsScrollViewer.ActualHeight < 140)
                    throw new Exception($"保存栏或表单视口不可用：{width}");
                foreach (var control in window._controlGroups.Where(pair => pair.Value == group).Select(pair => pair.Key))
                {
                    var bounds = control.TransformToAncestor(window.SettingsForm).TransformBounds(new Rect(control.RenderSize));
                    if (bounds.Left < -1 || bounds.Right > window.SettingsForm.ActualWidth + 1 || bounds.Height < 20)
                        throw new Exception($"设置控件横向越界或被压扁：{control.Name} / {width}");
                }
                window.SettingsScrollViewer.ScrollToBottom();
                Render($"settings-{groups[group]}-bottom-{width}", width, height);
            }
            foreach (var (page, button, name) in new[]
            {
                ((UIElement)window.DashboardPage, window.DashboardNavButton, "overview"),
                ((UIElement)window.LogsPage, window.LogsNavButton, "logs"),
                ((UIElement)window.DiagnosticsPage, window.DiagnosticsNavButton, "diagnostics")
            })
            {
                window.ShowPage(page, button);
                if (page is ScrollViewer scroll) scroll.ScrollToTop();
                Render($"{name}-{width}", width, height);
                if (name == "overview")
                {
                    var stopBounds = window.StopRecordButton.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(window.StopRecordButton.RenderSize));
                    if (stopBounds.Bottom > height || stopBounds.Right > width) throw new Exception("小窗口首屏无法操作停止录像");
                }
                if (page is ScrollViewer bottomScroll)
                {
                    bottomScroll.ScrollToBottom();
                    Render($"{name}-bottom-{width}", width, height);
                }
            }
        }
        var panel = new AdaptiveFormPanel();
        var a = new Border { Height = 50 }; var b = new Border { Height = 80 };
        panel.Children.Add(a); panel.Children.Add(b);
        panel.Measure(new Size(450, double.PositiveInfinity)); panel.Arrange(new Rect(0, 0, 450, panel.DesiredSize.Height));
        if (b.TranslatePoint(new Point(), panel).Y < 50) throw new Exception("窄宽度未切换单列");
        panel.Measure(new Size(1000, double.PositiveInfinity)); panel.Arrange(new Rect(0, 0, 1000, panel.DesiredSize.Height));
        if (b.TranslatePoint(new Point(), panel).Y != 0 || b.TranslatePoint(new Point(), panel).X < 500)
            throw new Exception("宽视口未切换双列");
        foreach (var size in new[] { 15, 18, 22 })
        {
            var password = new PasswordBox { Password = "test-password", PasswordChar = window.ObsPasswordBox.PasswordChar, Width = 360, FontSize = size,
                FontFamily = window.FontFamily, Style = (Style)window.FindResource(typeof(PasswordBox)) };
            password.ApplyTemplate();
            password.Measure(new Size(360, double.PositiveInfinity));
            password.Arrange(new Rect(0, 0, 360, password.DesiredSize.Height));
            password.UpdateLayout();
            var host = (ScrollViewer)password.Template.FindName("PART_ContentHost", password);
            var lineHeight = password.FontFamily.LineSpacing * size;
            if (host.Margin != new Thickness(0) || host.ActualHeight - password.Padding.Top - password.Padding.Bottom < lineHeight - 1)
                throw new Exception($"密码框字号 {size} 的文字区域被压缩");
            var bitmap = new RenderTargetBitmap(360, (int)Math.Ceiling(password.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(password);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(outputDirectory, $"password-font-{size}.png")); encoder.Save(file);
        }
        File.WriteAllText(Path.Combine(outputDirectory, "ui-checks.txt"),
            "PASS: 45 controls / 5 categories; settings + encrypted-password isolated roundtrip; unsaved-state routing; cross-category values; dependent controls; invalid-field navigation; WindowChrome configuration; adaptive one/two columns; 60 page renders at 800x560, 900x560, 1220x800, 1600x1000; footer and input bounds; first-screen stop action; 25-hour duration; busy buttons; manual-connect confirmation policy; password content area at font sizes 15/18/22 with 3 additional renders. Offline WPF render only, no native window or multi-DPI verification.");

        void Render(string name, int width, int height)
        {
            window.UpdateResponsiveLayout(width);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(outputDirectory, name + ".png"));
            encoder.Save(file);
        }
    }

    private static void AssertSettingsEqual(AppSettings expected, AppSettings actual)
    {
        foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.Name != nameof(AppSettings.ProtectedObsPassword)))
            if (!Equals(property.GetValue(expected), property.GetValue(actual)))
                throw new Exception($"设置往返丢失字段：{property.Name}");
    }
}
