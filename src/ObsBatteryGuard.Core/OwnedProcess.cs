using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ObsBatteryGuard.Core;

/// <summary>OS-enforced UI lifetime. Only the explicitly assigned helper belongs to this job;
/// child applications such as OBS must be allowed to break away and remain running.</summary>
public sealed class OwnedProcess : IDisposable
{
    private readonly SafeFileHandle _job;
    public Process Process { get; }
    private OwnedProcess(SafeFileHandle job, Process process) { _job = job; Process = process; }

    private static SafeFileHandle CreateContainer()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "创建守护生命周期容器失败");
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = 0x2000 | 0x1000; // KILL_ON_JOB_CLOSE | SILENT_BREAKAWAY_OK
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "设置守护生命周期失败");
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    public static OwnedProcess Attach(int id, long startTicks, string expectedPath)
    {
        var job = CreateContainer();
        Process? process = null;
        try
        {
            process = Process.GetProcessById(id);
            using var current = Process.GetCurrentProcess();
            if (process.HasExited || process.SessionId != current.SessionId || process.StartTime.ToUniversalTime().Ticks != startTicks ||
                !string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("后台进程身份改变，未接管生命周期");
            if (!AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法接管已有后台的生命周期");
            return new(job, process);
        }
        catch { job.Dispose(); process?.Dispose(); throw; }
    }

    public static OwnedProcess Start(string executable, string arguments, string? directory = null)
    {
        executable = Path.GetFullPath(executable);
        var job = CreateContainer();
        PROCESS_INFORMATION created = default;
        Process? process = null;
        try
        {
            var startup = new STARTUPINFO { cb = (uint)Marshal.SizeOf<STARTUPINFO>() };
            // Suspended creation closes the Process.Start -> AssignToJobObject orphan race.
            if (!CreateProcess(executable, new StringBuilder($"\"{executable}\" {arguments}"), IntPtr.Zero, IntPtr.Zero,
                    false, 0x4 | 0x08000000, IntPtr.Zero, directory ?? Path.GetDirectoryName(executable), ref startup, out created))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "启动守护失败");
            if (!AssignProcessToJobObject(job, created.hProcess))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将守护绑定到界面，已取消启动");
            process = Process.GetProcessById((int)created.dwProcessId);
            if (ResumeThread(created.hThread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复守护进程");
            return new(job, process);
        }
        catch
        {
            // This handle only identifies the child created above; never terminate OBS or a process tree.
            if (created.hProcess != IntPtr.Zero) TerminateProcess(created.hProcess, 1);
            process?.Dispose(); job.Dispose(); throw;
        }
        finally
        {
            if (created.hThread != IntPtr.Zero) CloseHandle(created.hThread);
            if (created.hProcess != IntPtr.Zero) CloseHandle(created.hProcess);
        }
    }

    public void Dispose() { _job.Dispose(); Process.Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION
    { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref STARTUPINFO startup, out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
