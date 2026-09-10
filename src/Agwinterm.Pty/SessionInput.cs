namespace Agwinterm.Pty;

/// <summary>Best-effort input for human broadcasts and terminal protocol replies. False means
/// unavailable or a possibly partial failed write, never proof that a child read the input.</summary>
public static class SessionInput
{
    public static bool TryWrite(ISession session, ReadOnlySpan<byte> bytes)
    {
        if (session.InputClosed) return false;
        try { session.NotifyActivity(); session.Write(bytes); return true; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { return false; }
    }

    public static bool Broadcast(IEnumerable<(ISession Session, bool ReadOnly)> panes, ReadOnlySpan<byte> bytes)
    {
        bool allAccepted = true;
        foreach (var pane in panes)
            if (pane.ReadOnly || !TryWrite(pane.Session, bytes)) allAccepted = false;
        return allAccepted;
    }
}
