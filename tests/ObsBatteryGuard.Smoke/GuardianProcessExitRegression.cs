using System.Diagnostics;
using ObsBatteryGuard.Core;

internal static class GuardianProcessExitRegression
{
    public static async Task RunAsync(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("未找到待测守护程序", executable);
        var isolatedDirectory = Path.Combine(Path.GetTempPath(), "OBG-ExitProcess-" + Guid.NewGuid().ToString("N"));
        var pipe = "OBG.ExitProcess." + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--exit-test");
        start.ArgumentList.Add(isolatedDirectory);
        start.ArgumentList.Add(pipe);
        // The dedicated mode bypasses production IPC/mutex/config and disables all OBS and power actions.
        using var process = Process.Start(start) ?? throw new Exception("隔离守护进程启动失败");
        try
        {
            IpcResponse ready = IpcResponse.Fail("等待启动");
            var startup = Stopwatch.StartNew();
            while (startup.Elapsed < TimeSpan.FromSeconds(10) && !process.HasExited)
            {
                ready = await GuardianIpcClient.SendAsync("get_status", 500, pipeName: pipe);
                if (ready.Success) break;
                await Task.Delay(100);
            }
            if (!ready.Success || ready.Status?.GuardianProcessId != process.Id)
                throw new Exception("隔离后台未就绪或进程身份不匹配");
            var reply = await GuardianIpcClient.SendAsync("exit_application_confirmed", 3000, pipeName: pipe,
                arguments: new() { ["processId"] = process.Id.ToString() });
            if (!reply.Success) throw new Exception("隔离后台退出请求失败：" + reply.Message);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (process.ExitCode != 0) throw new Exception("隔离后台退出码异常：" + process.ExitCode);
            var logs = Directory.GetFiles(Path.Combine(isolatedDirectory, "logs"), "*.log");
            if (!logs.Any(path => File.ReadAllText(path).Contains("用户确认退出整个软件")))
                throw new Exception("退出前未写入持久日志");
            Console.WriteLine("PASS isolated-guardian-process-exit-and-durable-log " + FileVersionInfo.GetVersionInfo(executable).ProductVersion);
        }
        finally
        {
            // Even a failed test self-expires in 20 seconds; never kill any production process.
            if (!process.HasExited) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            if (Directory.Exists(isolatedDirectory)) Directory.Delete(isolatedDirectory, true);
        }
    }
}
