using System.Drawing;
using System.IO;
using System.Windows;
using ObsBatteryGuard.Core;
using Forms = System.Windows.Forms;

namespace ObsBatteryGuard.App;

internal static class TrayIconChecks
{
    internal static void Run(string outputDirectory)
    {
        var names = new[] { "app", "tray-idle", "tray-recording", "tray-paused", "tray-warning", "tray-offline", "tray-busy" };
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        using var sheet = new Bitmap(700, 280);
        using var graphics = Graphics.FromImage(sheet);
        graphics.Clear(Color.White);
        using var dark = new SolidBrush(Color.FromArgb(15, 23, 42));
        graphics.FillRectangle(dark, 0, 140, 700, 140);
        for (var index = 0; index < names.Length; index++)
        {
            var uri = new Uri($"pack://application:,,,/Assets/{names[index]}.ico");
            using var stream = Application.GetResourceStream(uri)!.Stream;
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
            if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != sizes.Length)
                throw new Exception("ICO 头或帧数不正确：" + names[index]);
            for (var frame = 0; frame < sizes.Length; frame++)
            {
                var width = reader.ReadByte(); var height = reader.ReadByte();
                if ((width == 0 ? 256 : width) != sizes[frame] || width != height) throw new Exception("ICO 帧尺寸错误");
                reader.ReadBytes(6);
                var length = reader.ReadUInt32(); var offset = reader.ReadUInt32();
                var directoryPosition = stream.Position;
                stream.Position = offset;
                using var png = new MemoryStream(reader.ReadBytes(checked((int)length)));
                using var bitmap = new Bitmap(png);
                if (bitmap.Width != sizes[frame] || bitmap.Height != sizes[frame] || bitmap.GetPixel(0, 0).A != 0)
                    throw new Exception("ICO 内嵌图像尺寸或透明背景错误");
                stream.Position = directoryPosition;
            }
            foreach (var size in sizes)
            {
                using var resource = Application.GetResourceStream(uri)!.Stream;
                using var icon = new Icon(resource, size, size);
                // System.Drawing.Icon may select 128 for the ICO directory's 0 (=256) entry;
                // the actual 256px PNG was validated above. Tray sizes must match exactly.
                if (size < 256 && (icon.Width != size || icon.Height != size)) throw new Exception("ICO 帧不能按尺寸加载");
                if (size is not (16 or 32 or 64)) continue;
                var y = size == 16 ? 7 : size == 32 ? 29 : 65;
                // Draw exact native sizes on light and dark backgrounds; no stretched preview.
                graphics.DrawIcon(icon, new Rectangle(index * 100 + (100 - size) / 2, y, size, size));
                graphics.DrawIcon(icon, new Rectangle(index * 100 + (100 - size) / 2, y + 140, size, size));
            }
        }
        sheet.Save(Path.Combine(outputDirectory, "tray-icons.png"), System.Drawing.Imaging.ImageFormat.Png);

        int open = 0, minimize = 0, logs = 0, exit = 0;
        // Hidden NotifyIcon: exercise its resources, menu routing and disposal without touching the real guardian.
        var tray = new DesktopTrayIcon(() => open++, () => minimize++, () => logs++, () => exit++, visible: false);
        try
        {
            foreach (var state in Enum.GetValues<TrayIndicator>())
            {
                var presentation = new TrayStatusPresentation(state, state.ToString(), "测试状态", "测试详情");
                tray.Update(presentation);
                var firstIcon = tray.Notification.Icon;
                tray.Update(presentation);
                if (!ReferenceEquals(firstIcon, tray.Notification.Icon) || tray.Notification.Text != "测试状态")
                    throw new Exception("重复刷新重新分配图标或未更新提示");
            }
            foreach (var caption in new[] { "打开主界面", "收起到托盘（继续运行）", "查看运行日志", "退出软件…" })
                tray.Menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == caption).PerformClick();
            if (open != 1 || minimize != 1 || logs != 1 || exit != 1 || tray.Notification.Visible)
                throw new Exception("托盘菜单路由错误或离线测试意外显示了托盘");
        }
        finally { tray.Dispose(); tray.Dispose(); }
        if (tray.Notification.Visible) throw new Exception("释放后仍保留托盘图标");
        File.WriteAllText(Path.Combine(outputDirectory, "tray-checks.txt"),
            "PASS: 7 icons x 9 sizes; native-size light/dark preview; all 6 status resources; tooltip updates; unchanged icon reuse; open/minimize/logs/exit menu callbacks; idempotent disposal. Hidden tray only; no OBS, startup registration, real shutdown, or production guardian commands. Actual Windows tray visibility and notification suppression depend on shell settings.");
    }
}
