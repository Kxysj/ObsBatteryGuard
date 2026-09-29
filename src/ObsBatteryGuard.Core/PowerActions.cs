using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ObsBatteryGuard.Core;

public sealed class SleepInhibitor : IDisposable
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState executionState);

    private readonly AutoResetEvent _changed = new(false);
    private readonly Thread _worker;
    private volatile bool _preventSleep;
    private volatile bool _keepDisplayAwake;
    private volatile bool _disposed;

    public SleepInhibitor()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "OBS Battery Guard - Sleep Inhibitor"
        };
        _worker.Start();
    }

    public void Set(bool preventSleep, bool keepDisplayAwake)
    {
        if (_preventSleep == preventSleep && _keepDisplayAwake == keepDisplayAwake) return;
        _preventSleep = preventSleep;
        _keepDisplayAwake = keepDisplayAwake;
        _changed.Set();
    }

    public void Clear()
    {
        Set(false, false);
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            var requested = ExecutionState.Continuous;
            if (_preventSleep) requested |= ExecutionState.SystemRequired;
            if (_keepDisplayAwake) requested |= ExecutionState.DisplayRequired;
            SetThreadExecutionState(requested);
            _changed.WaitOne(TimeSpan.FromSeconds(30));
        }
        SetThreadExecutionState(ExecutionState.Continuous);
    }

    public void Dispose()
    {
        _disposed = true;
        _changed.Set();
        _worker.Join(2000);
        _changed.Dispose();
    }
}

public static class PowerActions
{
    public static bool ScheduleShutdown(int delaySeconds, bool forceCloseApps, string comment, out string error)
    {
        var args = $"/s /t {Math.Max(0, delaySeconds)} /d p:0:0 /c \"{comment.Replace("\"", "'")}\"";
        if (forceCloseApps) args += " /f";
        return RunShutdown(args, out error);
    }

    public static bool Hibernate(bool forceCloseApps, out string error)
    {
        var args = forceCloseApps ? "/h /f" : "/h";
        return RunShutdown(args, out error);
    }

    public static bool AbortShutdown(out string error) => RunShutdown("/a", out error);

    private static bool RunShutdown(string arguments, out string error)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                error = "无法启动 Windows 关机程序";
                return false;
            }
            if (!process.WaitForExit(5000))
            {
                error = "Windows 电源命令未在 5 秒内返回，无法确认提交结果";
                return false;
            }
            error = process.StandardError.ReadToEnd().Trim();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

public sealed record StartupApplyResult(bool Success, string Method, string Message);

public static class WindowsShortcut
{
    public static void Create(string shortcutPath, string targetPath, string arguments, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath) ?? throw new InvalidOperationException("快捷方式目录无效"));
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows 快捷方式组件不可用");
        object? shellObject = null;
        object? shortcutObject = null;
        try
        {
            shellObject = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("无法创建 Windows 快捷方式组件");
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            shortcut.TargetPath = targetPath;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory;
            shortcut.WindowStyle = 7;
            shortcut.Description = description;
            shortcut.Save();
        }
        finally
        {
            if (shortcutObject is not null && Marshal.IsComObject(shortcutObject)) Marshal.FinalReleaseComObject(shortcutObject);
            if (shellObject is not null && Marshal.IsComObject(shellObject)) Marshal.FinalReleaseComObject(shellObject);
        }
    }
}

public static class StartupManager
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ObsBatteryGuard";
    private const string ShortcutName = "OBS电池安全录制守护.lnk";

    public static StartupApplyResult Apply(bool enabled, string executable, string arguments = "")
    {
        if (enabled && !File.Exists(executable))
            return new StartupApplyResult(false, "none", "程序不存在，无法设置开机启动");

        if (!enabled)
        {
            var errors = new List<string>();
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryPath, true);
                key?.DeleteValue(ValueName, false);
            }
            catch (Exception ex)
            {
                errors.Add("注册表启动项：" + FriendlyPermissionMessage(ex));
            }
            try
            {
                var shortcutPath = GetShortcutPath();
                if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
            }
            catch (Exception ex)
            {
                errors.Add("启动文件夹快捷方式：" + FriendlyPermissionMessage(ex));
            }
            return errors.Count == 0
                ? new StartupApplyResult(true, "disabled", "已关闭随 Windows 启动")
                : new StartupApplyResult(false, "partial", "配置已保存，但清理部分开机启动项失败：" + string.Join("；", errors));
        }

        Exception? registryError = null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryPath, true)
                ?? throw new UnauthorizedAccessException("无法打开当前用户的开机启动注册表项");
            key.SetValue(ValueName, $"\"{executable}\" {arguments}".TrimEnd());
            TryDeleteShortcut();
            return new StartupApplyResult(true, "registry", "已通过当前用户注册表设置随 Windows 启动");
        }
        catch (Exception ex)
        {
            registryError = ex;
        }

        try
        {
            CreateStartupShortcut(executable, arguments);
            return new StartupApplyResult(true, "startup-folder",
                "系统不允许写入开机启动注册表，已自动改用 Windows 启动文件夹快捷方式");
        }
        catch (Exception shortcutError)
        {
            return new StartupApplyResult(false, "none",
                "配置已保存，但无法设置随 Windows 启动。注册表：" + FriendlyPermissionMessage(registryError) +
                "；启动文件夹：" + FriendlyPermissionMessage(shortcutError));
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            if (key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value)) return true;
        }
        catch { }
        try { return File.Exists(GetShortcutPath()); }
        catch { return false; }
    }

    private static string GetShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    private static void CreateStartupShortcut(string executable, string arguments)
    {
        WindowsShortcut.Create(GetShortcutPath(), executable, arguments, "OBS 电池安全录制");
    }

    private static void TryDeleteShortcut()
    {
        try
        {
            var path = GetShortcutPath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static string FriendlyPermissionMessage(Exception? exception)
    {
        if (exception is UnauthorizedAccessException or System.Security.SecurityException)
            return "被 Windows 权限或安全策略拒绝";
        return exception?.Message ?? "未知错误";
    }
}
