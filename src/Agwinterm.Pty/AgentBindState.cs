namespace Agwinterm.Pty;

/// <summary>
/// The concurrency half of a pane's agent resume binding (#316, #368), kept host-free so its orderings can be
/// tested: what the pane's stored line, the SessionStart binding still resolving and the session-end reports
/// may do to each other. The host holds one per pane and delegates; every method is one critical section on a
/// private lock, because the callers sit on different threads (a pipe thread per client, the background walk,
/// the UI thread) and the pieces below used to be published one at a time.
///
/// <para><b>Generation.</b> <see cref="Gen"/> is advanced by every write of the pane's resume line: a bound
/// line, an unbind, and the writes that are not a SessionStart binding's (<see cref="Supersede"/>: a restart,
/// adopt, the update flow, the legacy <c>session.bind</c>). An end report captures it when it is accepted
/// (<see cref="AcceptEnded"/>) and its UI step (<see cref="TryUnbind"/>) acts only if it is still that, so a
/// report that is accepted but runs late can never clear a line written after it.</para>
///
/// <para><b>Pending.</b> A SessionStart report is published as the pane's <see cref="PendingBind"/>
/// (<see cref="Begin"/>) before the slow process-tree walk says whether it is the pane's own agent. A later
/// binding, or a non-binding write, replaces it, and it then lands as superseded. An end report for its session
/// MARKS it, at acceptance and in the same critical section as the generation it captures; the line is cleared
/// independently, when it resumes that session. So whichever of the two finishes first, a marked binding is not
/// stored afterwards.</para>
/// </summary>
public sealed class AgentBindState
{
    /// <summary>A SessionStart binding between its report and its completion.</summary>
    public sealed class PendingBind
    {
        internal PendingBind(string agent, string session) { Agent = agent; Session = session; }
        public string Agent { get; }
        public string Session { get; }
        internal bool Ended;   // guarded by the state's lock
    }

    /// <summary>What was true when an end report was accepted. <paramref name="Restart"/>: it is the report of
    /// a restart the host drives itself, which changes nothing. <paramref name="MarkedPending"/>: it named the
    /// session of the binding then pending, which is now marked.</summary>
    public readonly record struct EndTicket(string Agent, string Session, int Gen, bool Restart, bool MarkedPending);

    public enum UnbindResult { Cleared, Stale, NotThisSession }

    private readonly object _gate = new();
    private int _gen;
    private int _unbinds;
    private PendingBind? _pending;
    // The restart token (see ArmRestart).
    private bool _armed, _relaunched;
    private int _restart;
    private string? _armedSession;
    private long _expiresAt;

    public int Gen { get { lock (_gate) return _gen; } }

    /// <summary>How many times the line was unbound. A <c>restore capture</c> that read the processes before an
    /// unbind must not write its result after it (<see cref="UnbindsSince"/>).</summary>
    public int Unbinds { get { lock (_gate) return _unbinds; } }

    public bool UnbindsSince(int unbinds) { lock (_gate) return _unbinds != unbinds; }

    /// <summary>The line is written by something other than a SessionStart binding: whatever was pending is
    /// superseded and older end reports no longer apply. Call it where the line is assigned; a writer that
    /// dispatches the assignment to another thread calls it there too, so the generation moves with the write
    /// and not only with the request (<see cref="Advance"/> when a newer pending binding must be left).</summary>
    public void Supersede()
    {
        lock (_gate) { _gen++; _pending = null; }
    }

    /// <summary>The line is being assigned: end reports accepted before this are stale. Leaves the pending
    /// binding, which may belong to a SessionStart that arrived after the write was requested.</summary>
    public void Advance()
    {
        lock (_gate) { _gen++; }
    }

    /// <summary>A SessionStart report was accepted, before its walk. Replaces the pending binding (which then
    /// lands as superseded). When it is the replacement agent of a restart the host just typed (<see
    /// cref="RelaunchTyped"/>) for the session being quit, or for any when none was named, the restart token is
    /// spent: the old agent's report, if any, is already in. A start of another session, a nested run's, leaves
    /// it.</summary>
    public PendingBind Begin(string agent, string session)
    {
        lock (_gate)
        {
            if (_armed && _relaunched && (_armedSession is null || _armedSession == session)) _armed = false;
            return _pending = new PendingBind(agent, session);
        }
    }

    /// <summary>The walk refused the binding (a nested run, an unreadable tree): nothing is stored and the
    /// line stays. Only the pane's own pending binding is dropped.</summary>
    public void Abandon(PendingBind pending)
    {
        lock (_gate) { if (ReferenceEquals(_pending, pending)) _pending = null; }
    }

    /// <summary>The walk accepted the binding. The result is where it lands (<see cref="AgentResume.LandBind"/>):
    /// stored, superseded, or, its session having been reported ended while it resolved, an unbind of the
    /// pane. A store or an unbind is a write of the line, so it advances the generation.</summary>
    public AgentResume.BindLanding Land(PendingBind pending)
    {
        lock (_gate)
        {
            bool superseded = !ReferenceEquals(_pending, pending);
            if (!superseded) _pending = null;
            var landing = AgentResume.LandBind(superseded, pending.Ended);
            if (landing != AgentResume.BindLanding.Superseded) _gen++;
            if (landing == AgentResume.BindLanding.Unbind) _unbinds++;
            return landing;
        }
    }

    /// <summary>An agent reported <paramref name="session"/> ended, on the pipe thread. Either it is the end of a
    /// restart the host drives (its token is spent), or it captures the generation and marks the pending binding
    /// when that is for the same session.</summary>
    public EndTicket AcceptEnded(string agent, string session, long now)
    {
        lock (_gate)
        {
            if (_armed && now >= _expiresAt) _armed = false;
            if (_armed && (_armedSession is null || _armedSession == session))
            {
                _armed = false;
                return new(agent, session, _gen, Restart: true, MarkedPending: false);
            }
            bool mark = _pending is { } p && p.Agent == agent && p.Session == session;
            if (mark) _pending!.Ended = true;
            return new(agent, session, _gen, Restart: false, MarkedPending: mark);
        }
    }

    /// <summary>The UI step of an accepted end report: whether to clear <paramref name="line"/>, the pane's
    /// line now. Only a line written no later than the report was accepted, and one that resumes the ended
    /// session, goes. The pending binding is not touched here; it was marked at acceptance.</summary>
    public UnbindResult TryUnbind(EndTicket ticket, string? line)
    {
        lock (_gate)
        {
            if (ticket.Restart || _gen != ticket.Gen) return UnbindResult.Stale;
            if (!AgentResume.ResumesSession(line, ticket.Agent, ticket.Session)) return UnbindResult.NotThisSession;
            _gen++;
            _unbinds++;
            return UnbindResult.Cleared;
        }
    }

    /// <summary>The host is about to end the agent of this pane with Ctrl+C and relaunch it (<c>QuitClaudeAndRelaunch</c>);
    /// the agent reports that as the user ending the session. Arms a token that refuses exactly one such report,
    /// for <paramref name="running"/> (the session being quit, captured by the caller before it replaced the
    /// line; null for any), until <paramref name="now"/> + <paramref name="lifetime"/>. The relaunch resumes the
    /// SAME id, so the token is single use and the relaunched agent's own exit unbinds as usual. Returns the
    /// restart's number, which its <see cref="RelaunchTyped"/> must give: restarts can overlap, and a late
    /// relaunch of an earlier one must not mark the token of a newer.</summary>
    public int ArmRestart(string? running, long now, long lifetime)
    {
        lock (_gate)
        {
            _armed = true; _relaunched = false; _armedSession = running; _expiresAt = now + lifetime;
            return ++_restart;
        }
    }

    /// <summary>The relaunch line of restart <paramref name="restart"/> was typed: from here on the replacement's
    /// SessionStart spends the token, if that restart's is still the one armed.</summary>
    public void RelaunchTyped(int restart)
    {
        lock (_gate) { if (_armed && restart == _restart) _relaunched = true; }
    }
}
