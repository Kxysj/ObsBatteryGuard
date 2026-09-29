using System.Diagnostics;
using System.Text;

namespace ObsBatteryGuard.Core;

public sealed record ObsFormatInfo(bool Found, string ProfileName, string ProfileDirectory, string OutputMode, string Format, bool IsMkv, string Message);

public static class ObsProfileManager
{
    public static string ConfigRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

    public static string InspectNativeSplit(string? preferredProfileName = null, string? configRoot = null)
    {
        try
        {
            var path = FindProfilePath(preferredProfileName, configRoot);
            if (path is null) return "OBS 自带分割：未找到明确的配置，需在 OBS 设置中检查。";
            var ini = IniDocument.Load(path);
            var mode = ini.Get("Output", "Mode") ?? "Simple";
            var enabled = ini.Get("AdvOut", "RecSplitFile");
            var type = ini.Get("AdvOut", "RecSplitFileType") ?? "未知";
            var time = ini.Get("AdvOut", "RecSplitFileTime") ?? "未知";
            var size = ini.Get("AdvOut", "RecSplitFileSize") ?? "未知";
            var explicitEnabled = string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) || enabled == "1";
            return $"OBS 自带分割（磁盘配置，运行中的设置可能尚未保存）：输出模式={mode}；开关={enabled ?? "未明确配置"}；类型={type}；时间={time} 分钟；大小={size} MB。\n" +
                (mode.Equals("Advanced", StringComparison.OrdinalIgnoreCase) && explicitEnabled
                ? "注意：OBS 自带分割已在磁盘配置启用，它独立于本软件，关闭本软件不会关闭 OBS 自带分割。"
                : "不能仅凭保存的分割时间认定分割已开启；如退出后仍分割，请核对 OBS 设置 → 输出 → 录像 → 自动文件分割。") +
                "本软件不写入这些分割参数，也不会擅自关闭你在 OBS 中设置的功能。";
        }
        catch (Exception ex) { return "OBS 自带分割检查失败：" + ex.Message; }
    }

    public static ObsFormatInfo Inspect(string? preferredProfileName = null, string? configRoot = null)
    {
        try
        {
            var profilePath = FindProfilePath(preferredProfileName, configRoot);
            if (profilePath is null)
                return new(false, string.Empty, string.Empty, string.Empty, string.Empty, false, "没有找到 OBS 配置文件");

            var ini = IniDocument.Load(profilePath);
            var name = ini.Get("General", "Name") ?? Path.GetFileName(Path.GetDirectoryName(profilePath)) ?? "未知";
            var mode = ini.Get("Output", "Mode") ?? "Simple";
            var section = mode.Equals("Advanced", StringComparison.OrdinalIgnoreCase) ? "AdvOut" : "SimpleOutput";
            if (section == "AdvOut" && string.Equals(ini.Get("AdvOut", "RecType"), "FFmpeg", StringComparison.OrdinalIgnoreCase))
                return new(true, name, Path.GetDirectoryName(profilePath)!, mode, "FFmpeg", false, "当前使用自定义 FFmpeg 输出，无法按标准录像配置验证 MKV；请在 OBS 中检查实际封装格式");
            var format = ini.Get(section, "RecFormat2") ?? ini.Get(section, "RecFormat") ?? "未知";
            var isMkv = format.Equals("mkv", StringComparison.OrdinalIgnoreCase);
            return new(true, name, Path.GetDirectoryName(profilePath) ?? string.Empty, mode, format, isMkv,
                isMkv ? "当前 OBS 配置为 MKV" : $"当前 OBS 配置为 {format}");
        }
        catch (Exception ex)
        {
            return new(false, string.Empty, string.Empty, string.Empty, string.Empty, false, ex.Message);
        }
    }

    public static ObsFormatInfo EnsureMkv(string? preferredProfileName = null, string? configRoot = null)
    {
        if (IsObsRunning())
            return new(false, preferredProfileName ?? string.Empty, string.Empty, string.Empty, string.Empty, false,
                "请先停止录像并完全退出 OBS，然后再执行一键设置 MKV");

        var inspected = Inspect(preferredProfileName, configRoot);
        if (inspected.Format == "FFmpeg") return inspected;
        var profilePath = FindProfilePath(preferredProfileName, configRoot);
        if (profilePath is null)
            return new(false, string.Empty, string.Empty, string.Empty, string.Empty, false, "没有找到 OBS 配置文件");

        try
        {
            var backup = profilePath + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(profilePath, backup, true);
            var ini = IniDocument.Load(profilePath);
            ini.Set("SimpleOutput", "RecFormat2", "mkv");
            ini.Set("AdvOut", "RecFormat2", "mkv");
            ini.Set("SimpleOutput", "RecFormat", "mkv");
            ini.Set("AdvOut", "RecFormat", "mkv");
            ini.Save(profilePath);
            var result = Inspect(preferredProfileName, configRoot);
            return result with { Message = $"已设置为 MKV；原配置已备份到 {backup}" };
        }
        catch (Exception ex)
        {
            return new(false, string.Empty, string.Empty, string.Empty, string.Empty, false, "设置 MKV 失败：" + ex.Message);
        }
    }

    public static bool IsObsRunning()
    {
        foreach (var name in new[] { "obs64", "obs32" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    private static string? FindProfilePath(string? preferredProfileName, string? configRoot)
    {
        var root = string.IsNullOrWhiteSpace(configRoot) ? ConfigRoot : Path.GetFullPath(configRoot);
        var explicitProfile = !string.IsNullOrWhiteSpace(preferredProfileName);
        var profilesRoot = Path.Combine(root, "basic", "profiles");
        if (!Directory.Exists(profilesRoot)) return null;

        var globalPath = Path.Combine(root, "global.ini");
        string? preferredDirectory = null;
        if (File.Exists(globalPath))
        {
            var global = IniDocument.Load(globalPath);
            preferredProfileName ??= global.Get("BasicWindow", "Profile");
            preferredDirectory = global.Get("BasicWindow", "ProfileDir");
        }

        if (!explicitProfile && !string.IsNullOrWhiteSpace(preferredDirectory))
        {
            var path = Path.GetFullPath(Path.Combine(profilesRoot, preferredDirectory, "basic.ini"));
            if (path.StartsWith(Path.GetFullPath(profilesRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return path;
        }

        foreach (var path in Directory.EnumerateFiles(profilesRoot, "basic.ini", SearchOption.AllDirectories))
        {
            if (string.IsNullOrWhiteSpace(preferredProfileName)) return null;
            try
            {
                var ini = IniDocument.Load(path);
                if (string.Equals(ini.Get("General", "Name"), preferredProfileName, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            catch { }
        }
        return null;
    }

    private sealed class IniDocument
    {
        private readonly List<string> _lines;

        private IniDocument(List<string> lines) => _lines = lines;

        public static IniDocument Load(string path) => new(File.ReadAllLines(path, Encoding.UTF8).ToList());

        public string? Get(string section, string key)
        {
            var current = string.Empty;
            foreach (var raw in _lines)
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    current = line[1..^1];
                    continue;
                }
                if (!current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                if (line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line[(separator + 1)..].Trim();
            }
            return null;
        }

        public void Set(string section, string key, string value)
        {
            var sectionStart = -1;
            var sectionEnd = _lines.Count;
            for (var i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i].Trim();
                if (!line.StartsWith('[') || !line.EndsWith(']')) continue;
                var foundSection = line[1..^1];
                if (sectionStart >= 0)
                {
                    sectionEnd = i;
                    break;
                }
                if (foundSection.Equals(section, StringComparison.OrdinalIgnoreCase)) sectionStart = i;
            }

            if (sectionStart < 0)
            {
                if (_lines.Count > 0 && !string.IsNullOrWhiteSpace(_lines[^1])) _lines.Add(string.Empty);
                _lines.Add($"[{section}]");
                _lines.Add($"{key}={value}");
                return;
            }

            for (var i = sectionStart + 1; i < sectionEnd; i++)
            {
                var separator = _lines[i].IndexOf('=');
                if (separator <= 0) continue;
                if (_lines[i][..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    _lines[i] = $"{key}={value}";
                    return;
                }
            }
            _lines.Insert(sectionEnd, $"{key}={value}");
        }

        public void Save(string path)
        {
            var temp = path + ".tmp";
            File.WriteAllLines(temp, _lines, new UTF8Encoding(false));
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough))
                stream.Flush(true);
            File.Move(temp, path, true);
        }
    }
}
