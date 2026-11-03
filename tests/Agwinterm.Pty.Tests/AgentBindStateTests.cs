using Agwinterm.Pty;
using static Agwinterm.Pty.AgentBindState;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// The orderings a pane's resume binding can meet (#368): a session-end report accepted on a pipe thread whose
/// UI step runs late, a SessionStart binding still resolving, the writes of restart / adopt / update, and the
/// restart's own Ctrl+C. Each test is one fixed interleaving of the calls the host makes from its threads.
/// </summary>
public class AgentBindStateTests
{
    private const string X = "36145f11-e132-4691-ab98-ae5c4445c46b";
    private const string Y = "01a0ce4b-7125-7dd1-b5a1-a99d8f14402c";
    private static string Line(string id) => $"claude --resume {id}";

    // ---- A: an accepted report acts only on what existed when it was accepted

    [Fact]
    public void AReportClearsTheLineItWasAcceptedFor()
    {
        var s = new AgentBindState();
        var t = s.AcceptEnded("claude", X, now: 0);
        Assert.Equal(UnbindResult.Cleared, s.TryUnbind(t, Line(X)));
    }

    [Fact]
    public void ALateReportLeavesALineAnotherWriterPutThereMeanwhile()
    {
        // restart / adopt / update / the legacy bind write the line after the report was accepted
        var s = new AgentBindState();
        var t = s.AcceptEnded("claude", X, 0);
        s.Supersede();
        Assert.Equal(UnbindResult.Stale, s.TryUnbind(t, Line(X) + " --dangerously-skip-permissions"));
    }

    [Fact]
    public void ALateReportLeavesTheBindingOfAUserSameIdResume()
    {
        // `claude --resume X` started after the end of X was accepted, and its binding landed before the UI step
        var s = new AgentBindState();
        var t = s.AcceptEnded("claude", X, 0);
        var p = s.Begin("claude", X);
        Assert.Equal(AgentResume.BindLanding.Store, s.Land(p));
        Assert.Equal(UnbindResult.Stale, s.TryUnbind(t, Line(X)));
    }

    [Fact]
    public void ALateReportBeforeThatBindingLandsClearsOnlyTheOldLine()
    {
        var s = new AgentBindState();
        var t = s.AcceptEnded("claude", X, 0);
        var p = s.Begin("claude", X);                                   // not pending when the report was accepted: not marked
        Assert.Equal(UnbindResult.Cleared, s.TryUnbind(t, Line(X)));
        Assert.Equal(AgentResume.BindLanding.Store, s.Land(p));         // and it stores
    }

    [Fact]
    public void AnUnrelatedStartMeanwhileDoesNotStopTheClear()
    {
        var s = new AgentBindState();
        var t = s.AcceptEnded("claude", X, 0);
        s.Begin("claude", Y);   // a nested run starting: it writes no line yet
        Assert.Equal(UnbindResult.Cleared, s.TryUnbind(t, Line(X)));
    }

    [Fact]
    public void AReportForAnotherSessionOrAnUnboundLineChangesNothing()
    {
        var s = new AgentBindState();
        Assert.Equal(UnbindResult.NotThisSession, s.TryUnbind(s.AcceptEnded("claude", X, 0), Line(Y)));
        Assert.Equal(UnbindResult.NotThisSession, s.TryUnbind(s.AcceptEnded("claude", X, 0), "claude"));
        Assert.Equal(UnbindResult.NotThisSession, s.TryUnbind(s.AcceptEnded("claude", X, 0), null));
        Assert.Equal(UnbindResult.NotThisSession, s.TryUnbind(s.AcceptEnded("codex", X, 0), Line(X)));
    }

    [Fact]
    public void AReportAcceptedBetweenALegacyBindsDispatchAndItsWriteDoesNotClearTheWrittenLine()
    {
        var s = new AgentBindState();
        s.Supersede();                                         // pipe thread: session.bind accepted, write queued
        var t = s.AcceptEnded("claude", X, 0);                 // a report accepted before the queued assignment runs
        s.Advance();                                           // UI thread: the assignment, advancing with it
        Assert.Equal(UnbindResult.Stale, s.TryUnbind(t, Line(X)));
    }

    [Fact]
    public void AdvanceLeavesAPendingBindingThatArrivedAfterTheWriteWasRequested()
    {
        var s = new AgentBindState();
        s.Supersede();
        var p = s.Begin("claude", X);                          // a SessionStart arrives before the queued assignment
        s.Advance();
        Assert.Equal(AgentResume.BindLanding.Store, s.Land(p));
    }

    [Fact]
    public void AClearIsAWriteOfTheLineToo()
    {
        var s = new AgentBindState();
        var first = s.AcceptEnded("claude", X, 0);
        var second = s.AcceptEnded("claude", X, 0);
        Assert.Equal(UnbindResult.Cleared, s.TryUnbind(first, Line(X)));
        Assert.Equal(UnbindResult.Stale, s.TryUnbind(second, Line(X)));   // the second report finds a line of a newer generation
    }

    // ---- B: the pending binding is marked and the line cleared independently

    [Fact]
    public void AReportMarksThePendingBindingOfItsSessionAndClearsTheLineIndependently()
    {
        var s = new AgentBindState();
        var p = s.Begin("claude", X);
        var t = s.AcceptEnded("claude", X, 0);
        Assert.True(t.MarkedPending);
        Assert.Equal(UnbindResult.Cleared, s.TryUnbind(t, Line(X)));       // the line resumed X: cleared
        Assert.Equal(AgentResume.BindLanding.Unbind, s.Land(p));           // and the binding does not bring it back
    }

    [Fact]
    public void AMarkedBindingNeverStoresWhicheverFinishesFirst()
    {
        var s = new AgentBindState();
        var p = s.Begin("claude", X);
        var t = s.AcceptEnded("claude", X, 0);
        Assert.Equal(AgentResume.BindLanding.Unbind, s.Land(p));            // the binding completes before the UI step
        Assert.Equal(UnbindResult.Stale, s.TryUnbind(t, null));             // which finds the line already cleared
    }

    [Fact]
    public void AClearAfterAnOlderSessionsLineStillUnbindsOnceTheWalkAcceptsTheMarkedBinding()
    {
        // /clear, then a quick /exit: the line is still the old session's
        var s = new AgentBindState();
        var p = s.Begin("claude", X);
        var t = s.AcceptEnded("claude", X, 0);
        Assert.Equal(UnbindResult.NotThisSession, s.TryUnbind(t, Line(Y)));
        Assert.Equal(AgentResume.BindLanding.Unbind, s.Land(p));
    }

    [Fact]
    public void AMarkedBindingTheWalkRefusesLeavesTheLine()
    {
        // a nested run that started and ended: nothing is stored, nothing but its own session's line is cleared
        var s = new AgentBindState();
        var p = s.Begin("claude", Y);
        var t = s.AcceptEnded("claude", Y, 0);
        Assert.Equal(UnbindResult.NotThisSession, s.TryUnbind(t, Line(X)));   // the parent's line
        s.Abandon(p);
        Assert.Equal(0, s.Unbinds);
    }

    [Fact]
    public void AReportForAnotherSessionOrAgentDoesNotMarkThePendingBinding()
    {
        var s = new AgentBindState();
        var p = s.Begin("claude", X);
        Assert.False(s.AcceptEnded("claude", Y, 0).MarkedPending);
        Assert.False(s.AcceptEnded("codex", X, 0).MarkedPending);
        Assert.Equal(AgentResume.BindLanding.Store, s.Land(p));
    }

    [Fact]
    public void ABindingSupersededByAnyWriterLandsAsSuperseded()
    {
        var s = new AgentBindState();
        var p = s.Begin("claude", X);
        s.Supersede();
        Assert.Equal(AgentResume.BindLanding.Superseded, s.Land(p));
        var older = s.Begin("claude", X);
        s.Begin("claude", Y);                                               // a later binding replaces it
        Assert.Equal(AgentResume.BindLanding.Superseded, s.Land(older));
        var q = s.Begin("claude", X);
        s.Abandon(older);                                                   // another binding's refusal leaves this one alone
        Assert.Equal(AgentResume.BindLanding.Store, s.Land(q));
    }

    [Fact]
    public void AMarkedBindingThatIsSupersededDoesNotUnbindTheNewerLine()
    {
        var s = new AgentBindState();
        var p = s.Begin("claude", X);
        s.AcceptEnded("claude", X, 0);
        s.Supersede();   // e.g. a restart wrote its line
        Assert.Equal(AgentResume.BindLanding.Superseded, s.Land(p));
        Assert.Equal(0, s.Unbinds);
    }

    // ---- C: the restart token

    [Fact]
    public void TheRestartsOwnReportIsRefusedOnceAndLeavesTheLine()
    {
        var s = new AgentBindState();
        s.ArmRestart(X, now: 1000, lifetime: 60_000);
        var t = s.AcceptEnded("claude", X, 2000);
        Assert.True(t.Restart);
        Assert.Equal(UnbindResult.Stale, s.TryUnbind(t, Line(X)));
        Assert.False(s.AcceptEnded("claude", X, 2001).Restart);              // single use
    }

    [Fact]
    public void TheReplacementsStartSpendsTheTokenSoItsOwnExitUnbinds()
    {
        var s = new AgentBindState();
        int restart = s.ArmRestart(X, 0, 60_000);
        s.RelaunchTyped(restart);
        s.Begin("claude", X);                                                // the replacement's SessionStart, same id
        var t = s.AcceptEnded("claude", X, 5000);
        Assert.False(t.Restart);
        Assert.Equal(UnbindResult.Cleared, s.TryUnbind(t, Line(X)));
    }

    [Fact]
    public void ALateRelaunchOfAnEarlierRestartDoesNotMarkANewerToken()
    {
        var s = new AgentBindState();
        int first = s.ArmRestart(X, 0, 60_000);
        int second = s.ArmRestart(X, 10, 60_000);              // overlapping restarts of the same pane
        Assert.NotEqual(first, second);
        s.RelaunchTyped(first);                                // the earlier one finishes late
        s.Begin("claude", X);                                  // not the newer restart's replacement
        Assert.True(s.AcceptEnded("claude", X, 100).Restart);

        int third = s.ArmRestart(X, 200, 60_000);
        s.RelaunchTyped(third);
        s.Begin("claude", X);
        Assert.False(s.AcceptEnded("claude", X, 300).Restart);
    }

    [Fact]
    public void AStartOfAnotherSessionDoesNotSpendTheToken()
    {
        var s = new AgentBindState();
        int restart = s.ArmRestart(X, 0, 60_000);
        s.RelaunchTyped(restart);
        s.Begin("claude", Y);                                                // a nested run
        Assert.True(s.AcceptEnded("claude", X, 5000).Restart);
    }

    [Fact]
    public void AStartBeforeTheRelaunchWasTypedDoesNotSpendTheToken()
    {
        var s = new AgentBindState();
        s.ArmRestart(X, 0, 60_000);
        s.Begin("claude", X);                                                // the dying Claude's own, still resolving
        Assert.True(s.AcceptEnded("claude", X, 5000).Restart);
    }

    [Fact]
    public void ATokenForAnUnknownSessionTakesOneReportOfAnyAndAnyStartSpendsIt()
    {
        var s = new AgentBindState();
        s.ArmRestart(null, 0, 60_000);
        Assert.True(s.AcceptEnded("claude", "whatever", 10).Restart);
        Assert.False(s.AcceptEnded("claude", "whatever", 11).Restart);

        int restart = s.ArmRestart(null, 0, 60_000);
        s.RelaunchTyped(restart);
        s.Begin("claude", Y);
        Assert.False(s.AcceptEnded("claude", X, 10).Restart);
    }

    [Fact]
    public void AReportForAnotherSessionDoesNotConsumeTheToken()
    {
        var s = new AgentBindState();
        s.ArmRestart(X, 0, 60_000);
        Assert.False(s.AcceptEnded("claude", Y, 100).Restart);
        Assert.True(s.AcceptEnded("claude", X, 200).Restart);
    }

    [Fact]
    public void TheTokenExpires()
    {
        var s = new AgentBindState();
        s.ArmRestart(X, 1000, 60_000);
        Assert.False(s.AcceptEnded("claude", X, 61_000).Restart);
        s.ArmRestart(X, 61_000, 60_000);
        Assert.True(s.AcceptEnded("claude", X, 120_999).Restart);
    }

    // ---- the restore capture fence

    [Fact]
    public void ACaptureStartedBeforeAnUnbindMustNotWriteAfterIt()
    {
        var s = new AgentBindState();
        int started = s.Unbinds;
        Assert.Equal(UnbindResult.Cleared, s.TryUnbind(s.AcceptEnded("claude", X, 0), Line(X)));
        Assert.True(s.UnbindsSince(started));
        Assert.False(s.UnbindsSince(s.Unbinds));                              // a capture taken after is fine
    }

    [Fact]
    public void AnUnbindByAMarkedBindingFencesCapturesToo()
    {
        var s = new AgentBindState();
        var p = s.Begin("claude", X);
        s.AcceptEnded("claude", X, 0);
        int started = s.Unbinds;
        s.Land(p);
        Assert.True(s.UnbindsSince(started));
    }

    [Fact]
    public void StoresAndRefusedReportsDoNotFenceCaptures()
    {
        var s = new AgentBindState();
        int started = s.Unbinds;
        s.Land(s.Begin("claude", X));
        s.TryUnbind(s.AcceptEnded("claude", Y, 0), Line(X));
        s.Supersede();
        Assert.False(s.UnbindsSince(started));
    }
}
