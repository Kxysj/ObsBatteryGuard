namespace ObsBatteryGuard.Core;

public sealed record PendingPowerAction(PostRecordingAction Action, DateTimeOffset DueAt, bool Force, bool Emergency);

/// <summary>Owns only this guardian's countdown; never cancels another application's Windows shutdown.</summary>
public sealed class PowerCountdown
{
    private readonly object _sync = new();
    private PendingPowerAction? _pending;
    private bool _committed;
    public PendingPowerAction? Pending { get { lock (_sync) return _pending; } }
    public bool Committed { get { lock (_sync) return _committed; } }

    public PendingPowerAction? Schedule(PostRecordingAction action, DateTimeOffset dueAt, bool force, bool emergency)
    {
        lock (_sync)
        {
            if (_committed || action == PostRecordingAction.None) return _pending;
            if (_pending is { Emergency: true } && !emergency) return _pending;
            if (emergency && _pending is { } existing && existing.DueAt < dueAt) dueAt = existing.DueAt;
            return _pending = new(action, dueAt, force, emergency);
        }
    }

    public PendingPowerAction? TryCommit(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_pending is null || _pending.DueAt > now || _committed) return null;
            var result = _pending;
            _pending = null;
            _committed = true;
            return result;
        }
    }

    public bool Cancel()
    {
        lock (_sync)
        {
            if (_committed) return false;
            _pending = null;
            return true;
        }
    }
    public void ReportExecutionFailure() { lock (_sync) _committed = false; }
}
