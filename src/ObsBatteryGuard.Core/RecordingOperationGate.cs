namespace ObsBatteryGuard.Core;

/// <summary>Serializes recording mutations; a stop cancels existing work and blocks new starts.</summary>
public sealed class RecordingOperationGate : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _active;
    private int _stops;
    public bool IsBusy { get { lock (_sync) return _active is not null || _stops > 0; } }

    public Lease? TryEnter(CancellationToken token)
    {
        lock (_sync)
        {
            if (_stops > 0 || !_gate.Wait(0)) return null;
            _active = CancellationTokenSource.CreateLinkedTokenSource(token);
            return new Lease(this, _active, false);
        }
    }

    public async Task<Lease> EnterStopAsync(CancellationToken token)
    {
        CancellationTokenSource? active;
        lock (_sync)
        {
            _stops++;
            active = _active;
        }
        // Cancellation callbacks may resume engine code; never invoke them while holding our lock.
        try { active?.Cancel(); } catch (ObjectDisposedException) { }
        try { await _gate.WaitAsync(token); }
        catch { lock (_sync) _stops--; throw; }
        lock (_sync)
        {
            _active = CancellationTokenSource.CreateLinkedTokenSource(token);
            return new Lease(this, _active, true);
        }
    }

    public sealed class Lease : IDisposable
    {
        private RecordingOperationGate? _owner;
        private readonly CancellationTokenSource _source;
        private readonly bool _stop;
        internal Lease(RecordingOperationGate owner, CancellationTokenSource source, bool stop)
        { _owner = owner; _source = source; _stop = stop; }
        public CancellationToken Token => _source.Token;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._sync)
            {
                owner._active = null;
                if (_stop) owner._stops--;
                _source.Dispose();
                owner._gate.Release();
            }
        }
    }
    public void Dispose() => _gate.Dispose();
}
