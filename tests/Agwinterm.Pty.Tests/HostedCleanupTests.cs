using Xunit;

namespace Agwinterm.Pty.Tests;

public class HostedCleanupTests
{
    [Theory]
    [InlineData("exited", true)] [InlineData("killed", true)] [InlineData("kill-raced-exit", true)]
    [InlineData("still-live", false)] [InlineData("wait-failed", false)] [InlineData("dispose-failed", false)]
    public void OnlyObservedExitAndSuccessfulDisposalCompleteCleanup(string state, bool expected)
    {
        var events = new List<string>();
        bool result = HostedCleanup.TryComplete(timeout =>
        {
            events.Add("wait:" + timeout);
            if (state == "wait-failed") throw new IOException();
            return state == "exited" || timeout > 0 && state != "still-live";
        }, () => { events.Add("kill"); if (state == "kill-raced-exit") throw new IOException(); },
        () => { events.Add("dispose"); if (state == "dispose-failed") throw new IOException(); });
        Assert.Equal(expected, result);
        Assert.Equal("wait:0", events[0]);
        if (state == "exited") Assert.Equal(new[] { "wait:0", "dispose" }, events);
        else if (state == "wait-failed") Assert.Single(events);
        else
        {
            Assert.Equal(new[] { "wait:0", "kill", "wait:5000" }, events.Take(3));
            Assert.Equal(state != "still-live", events.Contains("dispose"));
        }
    }
}
