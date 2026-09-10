using Xunit;

namespace Agwinterm.Pty.Tests;

public class SessionStartFenceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedAttachReapsOnlyIfDetachHasNotAlreadyWon(bool detached)
    {
        var fence = new SessionStartFence(); Assert.False(fence.Created());
        if (detached) Assert.False(fence.Close(false));
        Assert.Equal(!detached, fence.AttachmentFailed());
        Assert.False(fence.Publish(() => throw new Exception("must not publish failed attach")));
        Assert.Equal(detached, fence.Close(true)); // explicit Dispose may strengthen detach later
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CloseBeforeCreateLeavesTheRightObligation(bool kill)
    {
        var fence = new SessionStartFence();
        Assert.False(fence.Close(kill)); // no hosted id exists yet
        Assert.Equal(kill, fence.Created()); // create just completed: close must reap it, detach must not
        Assert.False(fence.Created());
        Assert.False(fence.Publish(() => throw new Exception("must not start reader")));
    }

    [Fact]
    public void CloseAfterCreateClaimsKillExactlyOnce()
    {
        var fence = new SessionStartFence();
        Assert.False(fence.Created());
        Assert.True(fence.Publish(() => { }));
        Assert.True(fence.Close(true));
        Assert.False(fence.Close(true));
        Assert.False(fence.Publish(() => throw new Exception("must not republish")));
    }

    [Fact]
    public void ExplicitCloseCanStrengthenAnEarlierDetach()
    {
        var fence = new SessionStartFence();
        Assert.False(fence.Created());
        Assert.False(fence.Close(false));
        Assert.True(fence.Close(true));
        Assert.False(fence.Close(true));
    }

    [Fact]
    public async Task CloseDoesNotWaitForBlockedCreateAndCompletionCannotPublish()
    {
        var fence = new SessionStartFence();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int kills = 0, publications = 0;
        var create = Task.Run(async () =>
        {
            entered.SetResult(); await release.Task;
            if (fence.Created()) Interlocked.Increment(ref kills);
            fence.Publish(() => Interlocked.Increment(ref publications));
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.False(fence.Close(true)); }
        finally { release.TrySetResult(); }
        await create.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, kills); Assert.Equal(0, publications);
    }
}
