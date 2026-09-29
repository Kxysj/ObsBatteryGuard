using System.Diagnostics;

namespace ObsBatteryGuard.Core;

public static class GuardianOwner
{
    public static Process? Open(string[] args, string expectedUiPath)
    {
        if (args.Length != 5 || args[0] != "--background" || args[1] != "--owner-process" || args[3] != "--owner-start" ||
            !int.TryParse(args[2], out var id) || !long.TryParse(args[4], out var start)) return null;
        Process? owner = null;
        try
        {
            owner = Process.GetProcessById(id);
            using var current = Process.GetCurrentProcess();
            if (owner.HasExited || owner.SessionId != current.SessionId || owner.StartTime.ToUniversalTime().Ticks != start ||
                !string.Equals(owner.MainModule?.FileName, Path.GetFullPath(expectedUiPath), StringComparison.OrdinalIgnoreCase))
            { owner.Dispose(); return null; }
            return owner;
        }
        catch { owner?.Dispose(); return null; }
    }
}
