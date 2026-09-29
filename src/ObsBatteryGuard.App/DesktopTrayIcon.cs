using System.Drawing;
using System.Windows;
using ObsBatteryGuard.Core;
using Forms = System.Windows.Forms;

namespace ObsBatteryGuard.App;

/// <summary>The UI owns the tray icon; it never starts a second guardian or changes recording state.</summary>
internal sealed class DesktopTrayIcon : IDisposable
{
    private readonly Dictionary<TrayIndicator, Icon> _icons = new();
    private readonly Forms.ToolStripMenuItem _status = new() { Enabled = false };
    private readonly Action _open;
    private TrayStatusPresentation? _last;
    private bool _disposed;
    private bool _announced;
    internal Forms.NotifyIcon Notification { get; }
    internal Forms.ContextMenuStrip Menu { get; } = new();

    internal DesktopTrayIcon(Action open, Action minimize, Action logs, Action exit, bool visible = true)
    {
        _open = open;
        Notification = new Forms.NotifyIcon();
        try
        {
            foreach (var state in Enum.GetValues<TrayIndicator>())
            {
                var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/tray-{state.ToString().ToLowerInvariant()}.ico"))
                    ?? throw new InvalidOperationException("托盘图标资源缺失：" + state);
                using var stream = resource.Stream;
                using var loaded = new Icon(stream, SystemInformationIconSize());
                _icons[state] = (Icon)loaded.Clone();
            }
            Menu.ShowImageMargin = false;
            Menu.Items.Add("OBS 电池安全录制").Enabled = false;
            Menu.Items.Add(_status);
            Menu.Items.Add(new Forms.ToolStripSeparator());
            AddMenu("打开主界面", open);
            AddMenu("收起到托盘（继续运行）", minimize);
            AddMenu("查看运行日志", logs);
            Menu.Items.Add(new Forms.ToolStripSeparator());
            AddMenu("退出软件…", exit);
            Notification.ContextMenuStrip = Menu;
            Notification.MouseClick += OnMouseClick;
            Notification.DoubleClick += OnOpen;
            Notification.BalloonTipClicked += OnOpen;
            Update(new(TrayIndicator.Busy, "软件已启动，正在连接后台", "OBS 电池安全录制\n正在连接后台，请稍候", "尚未确认保护状态"));
            Notification.Visible = visible;
        }
        catch { Dispose(); throw; }
    }

    private static System.Drawing.Size SystemInformationIconSize() => Forms.SystemInformation.SmallIconSize;

    private void AddMenu(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => { if (!_disposed) action(); };
        Menu.Items.Add(item);
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) OnOpen(sender, e); }
    private void OnOpen(object? sender, EventArgs e) { if (!_disposed) _open(); }

    internal void Update(TrayStatusPresentation state)
    {
        if (_disposed || state == _last) return;
        if (_last?.Indicator != state.Indicator) Notification.Icon = _icons[state.Indicator];
        Notification.Text = state.Tooltip.Length <= 63 ? state.Tooltip : state.Tooltip[..63];
        _status.Text = state.Summary;
        _status.ToolTipText = state.Detail;
        _last = state;
    }

    internal void AnnounceStartup()
    {
        if (_disposed || _announced) return;
        _announced = true;
        Notification.ShowBalloonTip(6000, "OBS 电池安全录制已启动",
            (_last?.Summary ?? "正在读取状态") + "。\n点击 × 收起到托盘并继续运行；单击图标打开，右键菜单退出。",
            Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Shell failures must not block guardian shutdown, and one failed cleanup must not skip the rest.
        try { Notification.Visible = false; } catch { }
        Notification.MouseClick -= OnMouseClick;
        Notification.DoubleClick -= OnOpen;
        Notification.BalloonTipClicked -= OnOpen;
        try { Notification.Dispose(); } catch { }
        try { Menu.Dispose(); } catch { }
        foreach (var icon in _icons.Values) { try { icon.Dispose(); } catch { } }
        _icons.Clear();
    }
}
