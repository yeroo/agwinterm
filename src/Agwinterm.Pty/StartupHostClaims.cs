namespace Agwinterm.Pty;

/// <summary>Creation/adoption and the one startup orphan sweep share this ownership decision.
/// Claims precede all host I/O and remain until the sweep ends, including panes not yet in a
/// workspace. A concurrent cleanup wins only before a claim; that ID then refuses creation
/// without waiting on host I/O or silently adopting a session being killed.</summary>
internal sealed class StartupHostClaims
{
    private readonly object _gate = new();
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reaping = new(StringComparer.OrdinalIgnoreCase);
    private bool _complete;

    public void Claim(string id)
    {
        lock (_gate)
        {
            if (_reaping.Contains(id)) throw new InvalidOperationException("Hosted session is undergoing startup orphan cleanup; creation/adoption refused");
            if (!_complete) _claimed.Add(id);
        }
    }

    public bool TryReap(string id, Action cleanup)
    {
        lock (_gate)
        {
            if (_complete || _claimed.Contains(id) || !_reaping.Add(id)) return false;
        }
        try { cleanup(); return true; }
        finally { lock (_gate) _reaping.Remove(id); }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_reaping.Count != 0) throw new InvalidOperationException("Orphan cleanup is still running");
            _complete = true;
            _claimed.Clear();
        }
    }
}
