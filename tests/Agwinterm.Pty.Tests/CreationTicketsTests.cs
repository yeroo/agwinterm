using Xunit;

namespace Agwinterm.Pty.Tests;

public class CreationTicketsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void LostPreparationReplyCannotCreateAndMetadataExpires()
    {
        var ledger = new CreationTickets<object>(1, TimeSpan.FromSeconds(1));
        lock (ledger.Gate)
        {
            var lost = Assert.IsType<CreationTickets<object>.Entry>(ledger.Prepare("pane", Now));
            Assert.Null(ledger.Prepare("other", Now));
            Assert.Null(lost.Value);
            Assert.Null(ledger.Find("pane", lost.Ticket, Now.AddSeconds(1)));
            Assert.False(ledger.Begin(lost));
            Assert.NotNull(ledger.Prepare("other", Now.AddSeconds(1)));
        }
    }

    [Fact]
    public void LostCreateReplyCanBeQueriedButNeverSpawnsTwice()
    {
        var ledger = new CreationTickets<object>(); var child = new object();
        lock (ledger.Gate)
        {
            var e = ledger.Prepare("pane", Now)!;
            Assert.True(ledger.Begin(e)); Assert.False(ledger.Begin(e));
            Assert.Equal(CreationTickets<object>.Phase.Creating, ledger.Find("pane", e.Ticket, Now)!.State);
            Assert.True(ledger.Complete(e, child)); Assert.False(ledger.Begin(e));
            Assert.Same(child, ledger.Find("pane", e.Ticket, Now.AddDays(1))!.Value);
            Assert.Null(ledger.Find("different-pane", e.Ticket, Now));
            Assert.Null(ledger.Find("pane", "unknown", Now));
        }
    }

    [Fact]
    public void CancellationBeforeCreateRevokesSpawnAuthority()
    {
        var ledger = new CreationTickets<object>();
        lock (ledger.Gate)
        {
            var e = ledger.Prepare("pane", Now)!;
            Assert.Null(ledger.Cancel(e)); Assert.False(ledger.Begin(e));
            Assert.Null(ledger.Find("pane", e.Ticket, Now));
        }
    }

    [Fact]
    public void CancellationDuringSpawnRemainsPendingUntilExactChildIsDisposed()
    {
        var ledger = new CreationTickets<object>(); var child = new object();
        lock (ledger.Gate)
        {
            var e = ledger.Prepare("pane", Now)!; Assert.True(ledger.Begin(e));
            Assert.Null(ledger.Cancel(e)); Assert.Null(ledger.Cancel(e));
            Assert.False(ledger.Complete(e, child));
            Assert.Equal(CreationTickets<object>.Phase.Cancelling, ledger.Find("pane", e.Ticket, Now)!.State);
            Assert.Null(ledger.Cancel(e)); // creator, not the cancelling caller, owns disposal
            var reuse = ledger.Prepare("pane", Now)!;
            Assert.False(ledger.Begin(reuse)); // pending cleanup still excludes reuse
            ledger.Cleaned(e);
            Assert.Null(ledger.Find("pane", e.Ticket, Now));
            Assert.False(ledger.Begin(reuse)); // the conflicting attempt was refused permanently
            Assert.True(ledger.Begin(ledger.Prepare("pane", Now)!));
        }
    }

    [Fact]
    public void OldCancellationCannotReachAReusedPaneId()
    {
        var ledger = new CreationTickets<object>(); var oldChild = new object(); var newChild = new object();
        lock (ledger.Gate)
        {
            var old = ledger.Prepare("pane", Now)!; Assert.True(ledger.Begin(old));
            Assert.True(ledger.Complete(old, oldChild));
            Assert.Same(oldChild, ledger.Cancel(old)); Assert.Null(ledger.Cancel(old));
            ledger.Cleaned(old);
            var next = ledger.Prepare("PANE", Now)!; Assert.True(ledger.Begin(next));
            Assert.True(ledger.Complete(next, newChild));
            Assert.NotEqual(old.Ticket, next.Ticket);
            Assert.Null(ledger.Find("pane", old.Ticket, Now));
            Assert.Null(ledger.Cancel(old)); ledger.Cleaned(old);
            Assert.Same(newChild, ledger.Find("pane", next.Ticket, Now)!.Value);
        }
    }

    [Fact]
    public void FailedSpawnCanReleaseOnlyItsOwnReservation()
    {
        var ledger = new CreationTickets<object>();
        lock (ledger.Gate)
        {
            var e = ledger.Prepare("pane", Now)!; Assert.True(ledger.Begin(e));
            ledger.Cleaned(e); Assert.False(ledger.Begin(e));
            var next = ledger.Prepare("pane", Now)!;
            ledger.Cleaned(e); Assert.True(ledger.Begin(next));
        }
    }

    [Fact]
    public void ShutdownClaimsLiveChildrenAndLeavesCreatingCleanupToCreator()
    {
        var ledger = new CreationTickets<object>(); var child = new object();
        lock (ledger.Gate)
        {
            var prepared = ledger.Prepare("prepared", Now)!;
            var pending = ledger.Prepare("pending", Now)!; Assert.True(ledger.Begin(pending));
            var live = ledger.Prepare("live", Now)!; Assert.True(ledger.Begin(live));
            Assert.True(ledger.Complete(live, child));
            var cleanup = Assert.Single(ledger.Stop()); Assert.Same(child, cleanup.Value);
            Assert.False(ledger.Drained.IsCompleted);
            Assert.Same(live, cleanup.Attempt); Assert.Empty(ledger.Stop());
            Assert.False(ledger.Begin(prepared)); Assert.Null(ledger.Prepare("new", Now));
            Assert.False(ledger.Complete(pending, new object()));
            Assert.Equal(CreationTickets<object>.Phase.Cancelling, pending.State);
            ledger.Cleaned(pending);
            Assert.False(ledger.Drained.IsCompleted);
            ledger.Cleaned(live);
            Assert.True(ledger.Drained.IsCompletedSuccessfully);
        }
    }

    [Fact]
    public void ShutdownWithOnlyUnissuedPreparationsDrainsImmediately()
    {
        var ledger = new CreationTickets<object>();
        lock (ledger.Gate)
        {
            ledger.Prepare("never spawned", Now);
            Assert.False(ledger.Drained.IsCompleted);
            Assert.Empty(ledger.Stop());
            Assert.True(ledger.Drained.IsCompletedSuccessfully);
        }
    }

    [Fact]
    public void CallersMustCoordinateWithTheSessionMapGate()
    {
        var ledger = new CreationTickets<object>();
        Assert.Throws<InvalidOperationException>(() => ledger.Prepare("pane", Now));
    }
}
