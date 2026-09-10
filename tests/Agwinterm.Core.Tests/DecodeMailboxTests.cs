using Xunit;

namespace Agwinterm.Core.Tests;

public class DecodeMailboxTests
{
    [Fact]
    public void DeletedOwnerRejectsLateDecodeWithoutRetainingPayload()
    {
        var mailbox = new DecodeMailbox<string, byte[]>();
        var ticket = mailbox.Begin("deleted")!;
        mailbox.Invalidate("deleted");
        Assert.False(mailbox.Complete("deleted", ticket, new byte[1024]));
        Assert.Empty(mailbox.Drain());
    }
    [Fact]
    public void ReplaceCannotAcceptOldCompletionOrEraseNewOne()
    {
        var mailbox = new DecodeMailbox<string, byte[]>();
        var old = mailbox.Begin("same-path")!;
        Assert.Null(mailbox.Begin("same-path"));
        mailbox.Invalidate("same-path");
        var fresh = mailbox.Begin("same-path")!;
        Assert.False(mailbox.Complete("same-path", old, [1]));
        Assert.True(mailbox.Complete("same-path", fresh, [2]));
        Assert.Equal(new byte[] { 2 }, Assert.Single(mailbox.Drain()).Value);
        Assert.Empty(mailbox.Drain());
    }
    [Fact]
    public void InvalidationDropsAlreadyPublishedResultsAndPreservesOtherOwners()
    {
        var mailbox = new DecodeMailbox<string, byte[]>();
        var deleted = mailbox.Begin("deleted")!; var live = mailbox.Begin("live")!;
        mailbox.Complete("deleted", deleted, [1]); mailbox.Complete("live", live, [2]);
        mailbox.Invalidate("deleted");
        Assert.Equal("live", Assert.Single(mailbox.Drain()).Key);
        var pending = mailbox.Begin("pending")!; var ready = mailbox.Begin("ready")!;
        mailbox.Complete("ready", ready, [3]); mailbox.Clear();
        Assert.False(mailbox.Complete("pending", pending, [4]));
        Assert.Empty(mailbox.Drain());
    }
}
