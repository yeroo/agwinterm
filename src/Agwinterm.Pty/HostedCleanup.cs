namespace Agwinterm.Pty;

internal static class HostedCleanup
{
    // One caller owns this cleanup. Exit observation uses the original connection's handle,
    // never a PID reopened after the child may have exited and its numeric PID been reused.
    internal static bool TryComplete(Func<int, bool> waitForExit, Action kill, Action dispose)
    {
        try
        {
            if (!waitForExit(0))
            {
                try { kill(); } catch { } // an exit racing Kill is valid only if the wait proves it
                if (!waitForExit(5000)) return false;
            }
            dispose();
            return true;
        }
        catch { return false; }
    }
}
