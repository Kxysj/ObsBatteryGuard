using Microsoft.Win32;
using System.Diagnostics;

namespace ObsBatteryGuard.Core;

public sealed record ObsInstallationSearchResult(bool Found, string Path, string Source, IReadOnlyList<string> CheckedLocations);

public static class ObsInstallationLocator
{
    public static ObsInstallationSearchResult FindBest(string? preferredPath = null)
    {
        var candidates = new List<(string Path, string Source)>();
        var checkedLocations = new List<string>();

        AddCandidate(candidates, preferredPath, "当前设置");

        try
        {
            foreach (var process in Process.GetProcessesByName("obs64"))
            {
                try { AddCandidate(candidates, process.MainModule?.FileName, "正在运行的 OBS"); }
                catch { }
                finally { process.Dispose(); }
            }
        }
        catch { }

        AddRegistryCandidates(candidates, RegistryHive.CurrentUser, RegistryView.Default);
        AddRegistryCandidates(candidates, RegistryHive.LocalMachine, RegistryView.Registry64);
        AddRegistryCandidates(candidates, RegistryHive.LocalMachine, RegistryView.Registry32);

        var commonRoots = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => drive.RootDirectory.FullName)
            .ToArray();
        foreach (var root in commonRoots)
        {
            AddCandidate(candidates, Path.Combine(root, "Program Files", "obs-studio", "bin", "64bit", "obs64.exe"), "常见安装位置");
            AddCandidate(candidates, Path.Combine(root, "Program Files", "OBS Studio", "obs-studio", "bin", "64bit", "obs64.exe"), "常见安装位置");
            AddCandidate(candidates, Path.Combine(root, "OBS Studio", "bin", "64bit", "obs64.exe"), "常见安装位置");
            AddCandidate(candidates, Path.Combine(root, "obs-studio", "bin", "64bit", "obs64.exe"), "常见安装位置");
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddCandidate(candidates, Path.Combine(directory.Trim('"'), "obs64.exe"), "PATH 环境变量");

        foreach (var candidate in candidates.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            checkedLocations.Add(candidate.Path);
            if (File.Exists(candidate.Path))
                return new ObsInstallationSearchResult(true, Path.GetFullPath(candidate.Path), candidate.Source, checkedLocations);
        }
        return new ObsInstallationSearchResult(false, string.Empty, "未找到", checkedLocations);
    }

    private static void AddRegistryCandidates(List<(string Path, string Source)> candidates, RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var appPath = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\obs64.exe");
            AddCandidate(candidates, NormalizeExecutablePath(appPath?.GetValue(null)?.ToString()), "Windows 应用注册表");

            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) return;
            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var subKey = uninstall.OpenSubKey(subKeyName);
                var displayName = subKey?.GetValue("DisplayName")?.ToString() ?? string.Empty;
                if (!displayName.Contains("OBS Studio", StringComparison.OrdinalIgnoreCase)) continue;
                AddCandidate(candidates, NormalizeExecutablePath(subKey?.GetValue("DisplayIcon")?.ToString()), "OBS 安装注册表");
                var installLocation = subKey?.GetValue("InstallLocation")?.ToString();
                if (!string.IsNullOrWhiteSpace(installLocation))
                {
                    AddCandidate(candidates, Path.Combine(installLocation, "bin", "64bit", "obs64.exe"), "OBS 安装注册表");
                    AddCandidate(candidates, Path.Combine(installLocation, "obs-studio", "bin", "64bit", "obs64.exe"), "OBS 安装注册表");
                }
            }
        }
        catch { }
    }

    private static string? NormalizeExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = Environment.ExpandEnvironmentVariables(value.Trim());
        var exeIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0) value = value[..(exeIndex + 4)];
        return value.Trim().Trim('"');
    }

    private static void AddCandidate(List<(string Path, string Source)> candidates, string? path, string source)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { candidates.Add((Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')), source)); }
        catch { }
    }
}
