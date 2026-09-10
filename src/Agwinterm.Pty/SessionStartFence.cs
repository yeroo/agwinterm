namespace Agwinterm.Pty;

/// <summary>Serializes publication, not host I/O. A close before create completes leaves a kill
/// obligation; detach leaves the hosted child alive. The caller performs a claimed kill outside the gate.</summary>
internal sealed class SessionStartFence
{
    private readonly object _gate = new();
    private bool _closed, _killRequested, _created, _killClaimed;
    public bool IsClosed { get { lock (_gate) return _closed; } }
    public bool KillRequested { get { lock (_gate) return _killRequested; } }
    public bool Created() { lock (_gate) { _created = true; return ClaimKill(); } }
    public bool Close(bool kill)
    {
        lock (_gate) { _closed = true; _killRequested |= kill; return ClaimKill(); }
    }
    public bool AttachmentFailed()
    {
        lock (_gate)
        {
            // A failed attach is cleanup for a live create, not authority to undo app-quit detach.
            if (!_closed) { _closed = true; _killRequested = true; }
            return ClaimKill();
        }
    }
    private bool ClaimKill()
    {
        if (!_created || !_killRequested || _killClaimed) return false;
        _killClaimed = true; return true;
    }
    public bool Publish(Action publish)
    {
        lock (_gate) { if (_closed) return false; publish(); return true; }
    }
}
