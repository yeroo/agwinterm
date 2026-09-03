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

    /// <summary>Epoch seconds of the last status write on the pane that won <see cref="Winner"/>.
    /// Where several panes tie at the winning severity the most recent wins: a session is as fresh
    /// as its freshest contributor to the status being shown. Empty set = 0.</summary>
    public static long WinnerChangedAt(IEnumerable<ISession> panes)
    {
        var list = panes as IReadOnlyCollection<ISession> ?? panes.ToList();
        int win = Severity(Winner(list));
        long at = 0;
        foreach (var p in list) if (Severity(p.Status) == win && p.StatusChangedAt > at) at = p.StatusChangedAt;
        return at;
    }
}
