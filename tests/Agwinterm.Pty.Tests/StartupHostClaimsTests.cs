using Xunit;

namespace Agwinterm.Pty.Tests;

public class StartupHostClaimsTests
{
    [Fact]
    public void BackendReplacementSharesClaimsAndSweepEpochOnlyWithinItsHostNamespace()
    {
        string appId = "startup-claims-" + Guid.NewGuid().ToString("N");
        var original = StartupHostClaims.ForNamespace(appId);
        var replacement = StartupHostClaims.ForNamespace(appId.ToUpperInvariant());
        var otherHost = StartupHostClaims.ForNamespace(appId + "-rust");
        Assert.Same(original, replacement);
        Assert.NotSame(original, otherHost);
        replacement.Claim("new-pane");
        Assert.True(original.TryBeginSweep());
        Assert.False(replacement.TryBeginSweep());
        Assert.False(original.TryReap("new-pane", () => throw new Exception("replacement pane must survive")));
        Assert.True(otherHost.TryBeginSweep());
        Assert.True(otherHost.TryReap("new-pane", () => { }));
        original.Complete(); otherHost.Complete();
        Assert.Same(original, StartupHostClaims.ForNamespace(appId));
        Assert.False(StartupHostClaims.ForNamespace(appId).TryBeginSweep());
    }

    [Fact]
    public void PendingPublicationIsAlreadyClaimedAndComparisonIsCaseInsensitive()
    {
        var claims = new StartupHostClaims();
        claims.Claim("Pane");
        Assert.False(claims.TryReap("pane", () => throw new Exception("must not kill pending publication")));
    }

    [Fact]
    public async Task ClaimAfterHostListButBeforeReapWins()
    {
        var claims = new StartupHostClaims();
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = Task.Run(async () => { listed.SetResult(); await resume.Task; return claims.TryReap("pane", () => throw new Exception("stale claim snapshot")); });
        await listed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { claims.Claim("pane"); } finally { resume.TrySetResult(); }
        Assert.False(await sweep.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReapWinsBeforeClaimWithoutBlockingUnrelatedCreation(bool cleanupFails)
    {
        var claims = new StartupHostClaims();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var sweep = Task.Run(() => Record.Exception(() => claims.TryReap("pane", () => {
            entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            if (cleanupFails) throw new IOException("kill failed");
        })));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidOperationException>(() => claims.Claim("PANE"));
            claims.Claim("unrelated");
            Assert.False(claims.TryReap("pane", () => throw new Exception("duplicate cleanup")));
            Assert.Throws<InvalidOperationException>(claims.Complete);
        }
        finally { release.Set(); }
        var error = await sweep.WaitAsync(TimeSpan.FromSeconds(5));
        if (cleanupFails) Assert.IsType<IOException>(error); else Assert.Null(error);
        claims.Claim("pane"); // success or failure both relinquish the cleanup reservation
        Assert.False(claims.TryReap("pane", () => throw new Exception("claimed after reap")));
    }

    [Fact]
    public void CompletionDisablesFurtherSweepsAndStopsAccumulatingClaims()
    {
        var claims = new StartupHostClaims(); int killed = 0;
        Assert.True(claims.TryReap("orphan", () => killed++));
        claims.Complete(); claims.Claim("new");
        Assert.False(claims.TryReap("anything", () => killed++));
        Assert.Equal(1, killed);
    }
}
