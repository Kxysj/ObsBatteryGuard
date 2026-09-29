using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ObsBatteryGuard.App;

public partial class MainWindow
{
    private readonly TextBox ObsHostText = new() { Name = "ObsHostText" };
    private readonly TextBox ObsPortText = new() { Name = "ObsPortText" };
    private readonly PasswordBox ObsPasswordBox = new() { Name = "ObsPasswordBox" };
    private readonly TextBox ObsPathText = new() { Name = "ObsPathText" };
    private readonly TextBox ObsConfigDirectoryText = new() { Name = "ObsConfigDirectoryText" };
    private readonly CheckBox AutoConnectObsCheck = new() { Name = "AutoConnectObsCheck" };
    private readonly CheckBox AutoLaunchObsCheck = new() { Name = "AutoLaunchObsCheck" };
    private readonly TextBox ConnTimeoutText = new() { Name = "ConnTimeoutText" };
    private readonly TextBox ReconnectText = new() { Name = "ReconnectText" };
    private readonly CheckBox BatteryProtectionCheck = new() { Name = "BatteryProtectionCheck" };
    private readonly TextBox WarningText = new() { Name = "WarningText" };
    private readonly TextBox StopText = new() { Name = "StopText" };
    private readonly TextBox EmergencyText = new() { Name = "EmergencyText" };
    private readonly TextBox MinStartText = new() { Name = "MinStartText" };
    private readonly TextBox PollText = new() { Name = "PollText" };
    private readonly TextBox ConsecutiveText = new() { Name = "ConsecutiveText" };
    private readonly CheckBox StopByTimeCheck = new() { Name = "StopByTimeCheck" };
    private readonly TextBox RemainingMinutesText = new() { Name = "RemainingMinutesText" };
    private readonly CheckBox UnknownFailSafeCheck = new() { Name = "UnknownFailSafeCheck" };
    private readonly TextBox UnknownTimeoutText = new() { Name = "UnknownTimeoutText" };
    private readonly CheckBox RequireMkvCheck = new() { Name = "RequireMkvCheck" };
    private readonly CheckBox EnableSplitCheck = new() { Name = "EnableSplitCheck" };
    private readonly TextBox SplitMinutesText = new() { Name = "SplitMinutesText" };
    private readonly TextBox SplitDelayText = new() { Name = "SplitDelayText" };
    private readonly TextBox SplitRestartTimeoutText = new() { Name = "SplitRestartTimeoutText" };
    private readonly CheckBox SplitFallbackCheck = new() { Name = "SplitFallbackCheck" };
    private readonly TextBox MinDiskText = new() { Name = "MinDiskText" };
    private readonly TextBox StopTimeoutText = new() { Name = "StopTimeoutText" };
    private readonly TextBox FlushWaitText = new() { Name = "FlushWaitText" };
    private readonly CheckBox StopOnDiskLowCheck = new() { Name = "StopOnDiskLowCheck" };
    private readonly CheckBox PreventSleepCheck = new() { Name = "PreventSleepCheck" };
    private readonly CheckBox KeepDisplayCheck = new() { Name = "KeepDisplayCheck" };
    private readonly ComboBox AfterStopCombo = new() { Name = "AfterStopCombo" };
    private readonly TextBox ShutdownDelayText = new() { Name = "ShutdownDelayText" };
    private readonly TextBox EmergencyDelayText = new() { Name = "EmergencyDelayText" };
    private readonly CheckBox EmergencyAlwaysShutdownCheck = new() { Name = "EmergencyAlwaysShutdownCheck" };
    private readonly CheckBox CancelAcCheck = new() { Name = "CancelAcCheck" };
    private readonly CheckBox ResumeAcCheck = new() { Name = "ResumeAcCheck" };
    private readonly CheckBox ForceShutdownCheck = new() { Name = "ForceShutdownCheck" };
    private readonly CheckBox ForceEmergencyCheck = new() { Name = "ForceEmergencyCheck" };
    private readonly CheckBox AutoStartGuardianCheck = new() { Name = "AutoStartGuardianCheck" };
    private readonly CheckBox AutoStartRecordingCheck = new() { Name = "AutoStartRecordingCheck" };
    private readonly TextBox BatteryLogIntervalText = new() { Name = "BatteryLogIntervalText" };
    private readonly TextBox ObsPollIntervalText = new() { Name = "ObsPollIntervalText" };
    private readonly TextBox RefreshIntervalText = new() { Name = "RefreshIntervalText" };
    private readonly List<StackPanel> _settingsGroups = new();
    private readonly List<Button> _settingsGroupButtons = new();
    private readonly Dictionary<Control, int> _controlGroups = new();
    private int _buildingSettingsGroup;
    private int _selectedSettingsGroup;

    private void BuildSettingsForms()
    {
        AfterStopCombo.Items.Add(new ComboBoxItem { Content = "仅停止录像，不关机" });
        AfterStopCombo.Items.Add(new ComboBoxItem { Content = "关闭 Windows" });
        AfterStopCombo.Items.Add(new ComboBoxItem { Content = "休眠" });
        var titles = new[] { "电池保护", "录像与分段", "停止与电源", "OBS 连接", "启动与日志" };
        for (var i = 0; i < titles.Length; i++)
        {
            var index = i;
            var button = new Button { Content = titles[i], Style = (Style)FindResource("SettingsTabStyle"), Margin = new Thickness(0, 0, 8, 6) };
            button.Click += (_, _) => SelectSettingsGroup(index);
            SettingsTabs.Children.Add(button);
            _settingsGroupButtons.Add(button);
            _settingsGroups.Add(new StackPanel());
        }

        _buildingSettingsGroup = 0;
        AddSection("电量阈值", "先预警，再停止；为文件保存和 Windows 电源动作留出余量。",
            Toggle(BatteryProtectionCheck, "启用电池保护", "使用电池时按下面的阈值执行保护。", full: true),
            Field(WarningText, "预警电量", "%", "建议 25%，只提醒，不直接停止。"),
            Field(StopText, "停止电量", "%", "建议 20%，连续确认后停止录像。"),
            Field(EmergencyText, "紧急电量", "%", "建议 10%，必须低于停止电量。"),
            Field(MinStartText, "允许开始的最低电量", "%", "建议 30%，避免刚启动就触发保护。"));
        AddSection("检测与异常处理", "电池检测独立于 OBS 状态刷新。",
            Field(PollText, "电池检测间隔", "秒", "默认 5 秒；间隔越短，响应越及时。"),
            Field(ConsecutiveText, "连续低电量确认", "次", "普通停止使用此次数；紧急保护不等待。"),
            Toggle(StopByTimeCheck, "按剩余时间停止", "使用 Windows 估算时间，精度取决于电池。"),
            Field(RemainingMinutesText, "剩余时间阈值", "分钟", "开启“按剩余时间停止”后生效。"),
            Toggle(UnknownFailSafeCheck, "电池状态未知时停止", "长时间读不到电池时启动保护。"),
            Field(UnknownTimeoutText, "状态未知超时", "秒", "建议保留足够时间应对临时读取失败。"));

        _buildingSettingsGroup = 1;
        AddSection("格式与自动分段", "优先使用 OBS 原生分割；兼容分割会有短暂录制间隔。",
            Toggle(RequireMkvCheck, "开始前验证 MKV", "降低异常断电损坏整段录像的风险。"),
            Toggle(EnableSplitCheck, "启用自动分割", "修改分割间隔并应用后重新计时。"),
            Field(SplitMinutesText, "每段录像时长", "分钟", "建议 10 分钟；支持 1–1440 分钟。"),
            Toggle(SplitFallbackCheck, "允许兼容分割", "原生分割失败时停止旧段并开始新段。"),
            Field(SplitDelayText, "兼容分割停录间隔", "毫秒", "较长间隔有利于收尾，但会遗漏更多画面。"),
            Field(SplitRestartTimeoutText, "新分段重启超时", "秒", "超时仍无法启动时明确报告失败。"));
        AddSection("磁盘空间", "开始录像会检查剩余空间；录制中也可持续保护。",
            Field(MinDiskText, "保留磁盘空间", "GB", "建议至少 5 GB。"),
            Toggle(StopOnDiskLowCheck, "空间不足时停止", "避免录像持续写入直至磁盘耗尽。"));

        _buildingSettingsGroup = 2;
        AddSection("停止与文件收尾", "发送停止指令后确认 OBS 状态，再等待文件写入。",
            Field(StopTimeoutText, "等待 OBS 停止", "秒", "超时或状态未知，不会误报保存成功。"),
            Field(FlushWaitText, "额外写盘等待", "秒", "默认 5 秒，不等于硬盘完整性验证。"));
        AddSection("停止后的动作", "修改设置不会改变已经开始的电源倒计时。",
            Field(AfterStopCombo, "普通停止后", "", "仅停止、关机或休眠。"),
            Field(ShutdownDelayText, "普通动作延时", "秒", "倒计时提交前可以取消。"),
            Toggle(EmergencyAlwaysShutdownCheck, "紧急电量始终关机", "可覆盖普通的“仅停止”或休眠设置。"),
            Field(EmergencyDelayText, "紧急关机延时", "秒", "紧急计划可缩短普通倒计时。"),
            Toggle(CancelAcCheck, "接通电源取消倒计时", "已经提交 Windows 的动作无法取消。"),
            Toggle(ResumeAcCheck, "来电后恢复录像", "仅对保护停止生效，不覆盖手动停止。"));
        AddSection("强制关闭应用", "可能导致其他应用的未保存内容丢失，请谨慎启用。",
            Toggle(ForceShutdownCheck, "普通关机强制关闭", "未启用时，其他应用可能阻止关机。"),
            Toggle(ForceEmergencyCheck, "紧急关机强制关闭", "尽快执行紧急兜底，但有数据丢失风险。"));

        _buildingSettingsGroup = 3;
        AddSection("WebSocket 连接", "在 OBS → 工具 → WebSocket 服务器设置中核对端口和密码。",
            Field(ObsHostText, "主机", "", "建议保持 127.0.0.1，仅连接本机。"),
            Field(ObsPortText, "端口", "", "OBS 默认端口为 4455。"),
            Field(ObsPasswordBox, "连接密码", "", "使用 Windows 当前用户加密保存。"),
            Toggle(AutoConnectObsCheck, "自动连接与断线重连", "软件运行期间生效；最小化不影响保护。"),
            Field(ConnTimeoutText, "连接 / 请求超时", "秒", "设置改变后在录制操作空闲时重连。"),
            Field(ReconnectText, "重连间隔", "秒", "连接失败后等待多久再尝试。"));
        AddSection("OBS 安装与配置", "程序路径用于启动 OBS；配置目录仅用于离线格式检查。",
            PathField(ObsPathText, "OBS 主程序", "请选择 obs64.exe；常见安装位置支持自动查找。", true),
            PathField(ObsConfigDirectoryText, "配置根目录（可选）", "普通安装留空；便携版填写包含 basic/profiles 的目录。", false),
            Toggle(AutoLaunchObsCheck, "OBS 未运行时自动启动", "需要有效的 OBS 主程序路径。", full: true));

        _buildingSettingsGroup = 4;
        AddSection("启动与节能", "点击 × 收起到托盘并继续运行；右键托盘图标可退出软件。",
            Toggle(AutoStartGuardianCheck, "软件随 Windows 启动", "登录后打开完整软件并显示托盘图标。"),
            Toggle(AutoStartRecordingCheck, "连接 OBS 后自动录像", "启用前请确认场景、格式和存储目录。"),
            Toggle(PreventSleepCheck, "录像时阻止自动睡眠", "不能保证阻止合盖或系统关键电量动作。"),
            Toggle(KeepDisplayCheck, "录像时保持屏幕点亮", "会增加耗电；无人值守通常无需开启。"));
        AddSection("刷新与日志", "三个间隔各自独立。过快刷新可能增加本机开销。",
            Field(ObsPollIntervalText, "OBS 状态同步", "毫秒", "默认 300 毫秒。"),
            Field(RefreshIntervalText, "界面刷新", "毫秒", "默认 500 毫秒。"),
            Field(BatteryLogIntervalText, "电池日志间隔", "秒", "关键事件会独立记录，不受此间隔限制。"));
        SelectSettingsGroup(0);
    }

    private void TrackSetting(Control control, string label)
    {
        _controlGroups[control] = _buildingSettingsGroup;
        AutomationProperties.SetName(control, label);
        RegisterName(control.Name, control);
    }

    private FrameworkElement Field(Control control, string label, string unit, string help)
    {
        TrackSetting(control, label);
        var panel = new StackPanel();
        panel.Children.Add(new Label { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Target = control, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 6),
            FontSize = 14, Foreground = (Brush)FindResource("TextBrush") });
        var input = new DockPanel();
        if (unit.Length > 0)
        {
            var suffix = new TextBlock { Text = unit, Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("MutedBrush") };
            DockPanel.SetDock(suffix, Dock.Right); input.Children.Add(suffix);
        }
        input.Children.Add(control);
        panel.Children.Add(input);
        panel.Children.Add(new TextBlock { Text = help, FontSize = 13, Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private FrameworkElement Toggle(CheckBox control, string label, string help, bool full = false)
    {
        TrackSetting(control, label);
        control.Style = (Style)FindResource("SwitchStyle");
        control.Margin = new Thickness(12, 0, 0, 0);
        var row = new Grid { MinHeight = 58, Tag = full ? "full" : null };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new Label { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Target = control, Padding = new Thickness(0), FontSize = 14 });
        copy.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 4, 0, 0) });
        row.Children.Add(copy);
        Grid.SetColumn(control, 1); row.Children.Add(control);
        return row;
    }

    private FrameworkElement PathField(TextBox control, string label, string help, bool withBrowse)
    {
        var panel = (StackPanel)Field(control, label, "", help);
        panel.Tag = "full";
        if (withBrowse)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var find = new Button { Content = "自动查找", Style = (Style)FindResource("SecondaryButtonStyle"), Height = 32 };
            find.Click += AutoFindObs_Click;
            var browse = new Button { Content = "选择程序…", Style = (Style)FindResource("NeutralButtonStyle"), Height = 32, Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += BrowseObs_Click;
            buttons.Children.Add(find); buttons.Children.Add(browse); panel.Children.Add(buttons);
        }
        return panel;
    }

    private void AddSection(string title, string description, params FrameworkElement[] fields)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = description, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 4, 0, 16) });
        AdaptiveFormPanel? cells = null;
        foreach (var field in fields)
        {
            if (Equals(field.Tag, "full"))
            {
                field.Margin = new Thickness(0, 0, 0, 16);
                body.Children.Add(field); cells = null;
            }
            else
            {
                if (cells is null) { cells = new AdaptiveFormPanel { Margin = new Thickness(0, 0, 0, 8) }; body.Children.Add(cells); }
                cells.Children.Add(field);
            }
        }
        _settingsGroups[_buildingSettingsGroup].Children.Add(new Border { Child = body, Background = Brushes.White,
            CornerRadius = new CornerRadius(10), BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1),
            Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 14) });
    }

    private void SelectSettingsGroup(int index)
    {
        _selectedSettingsGroup = index;
        SettingsForm.Children.Clear();
        SettingsForm.Children.Add(_settingsGroups[index]);
        for (var i = 0; i < _settingsGroupButtons.Count; i++) _settingsGroupButtons[i].Tag = i == index ? "Selected" : null;
        SettingsScrollViewer.ScrollToTop();
    }

    private void FocusInvalidSetting(Control control)
    {
        if (_controlGroups.TryGetValue(control, out var group)) SelectSettingsGroup(group);
        SettingsForm.UpdateLayout();
        control.BringIntoView();
        control.Focus();
        if (control is TextBox box) box.SelectAll();
    }
}
