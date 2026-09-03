using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>
/// How a multi-pane session's single reported status is chosen, and how old that status is.
/// The rule lives here rather than in the UI so the control API and the sidebar cannot drift:
/// the tree's <c>status</c> and its <c>statusChangedAt</c> must describe the SAME pane, or the age
/// is an answer to a question nobody asked.
/// </summary>
public static class StatusAggregate
{
    /// <summary>Attention severity: Blocked &gt; Completed &gt; Active &gt; Idle.</summary>
    public static int Severity(AgentStatus a) => a switch
    {
        AgentStatus.Blocked => 3,
        AgentStatus.Completed => 2,
        AgentStatus.Active => 1,
        _ => 0,
    };

    /// <summary>The most attention-worthy status across the panes — a background pane's state must
    /// not be invisible. An empty set is Idle.</summary>
    public static AgentStatus Winner(IEnumerable<ISession> panes)
    {
        var best = AgentStatus.Idle;
        foreach (var p in panes) if (Severity(p.Status) > Severity(best)) best = p.Status;
        return best;
    }

    /// <summary><see cref="Winner"/> plus the epoch seconds of the last status write on the pane that
    /// won it, from ONE pass over the panes, reading each pane's status and stamp together. Where
    /// several panes tie at the winning severity the most recent stamp wins: a session is as fresh
    /// as its freshest contributor to the status being shown. Empty set = (Idle, 0).
    ///
    /// One pass, not two: statuses are written without a lock — a hook reply, a keystroke, the
    /// control API — so a second scan looking for "the severity that won" could find it gone and
    /// fall through to 0, the one value the stamp exists never to report (a consumer reads 0 as an
    /// agent silent since 1970). Every tree — the app's and the test host's — uses this form so its
    /// <c>status</c> and <c>statusChangedAt</c> come from the same reading of the same pane.</summary>
    public static (AgentStatus Status, long ChangedAt) WinnerAndChangedAt(IEnumerable<ISession> panes)
    {
        var best = AgentStatus.Idle;
        int win = -1;
        long at = 0;
        foreach (var p in panes)
        {
            var status = p.Status;
            long stamp = p.StatusChangedAt;
            int sev = Severity(status);
            if (sev > win) { win = sev; best = status; at = stamp; }
            else if (sev == win && stamp > at) at = stamp;
        }
        return (best, at);
    }
}
