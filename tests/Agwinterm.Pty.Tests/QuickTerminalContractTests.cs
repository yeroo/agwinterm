using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class QuickTerminalContractTests
{
    [Theory]
    [InlineData("session.new")][InlineData("workspace.new")][InlineData("session.split")]
    [InlineData("restore.capture")][InlineData("dashboard")][InlineData("command.run")]
    public void AuxiliaryHostRefusesTreeMutations(string cmd)
    {
        var host = new FakeSessionHost { IsQuickSurface = true };
        string before = JsonSerializer.Serialize(host.Tree());
        var server = new ControlServer(host);
        using var reply = JsonDocument.Parse(server.Dispatch(JsonSerializer.Serialize(new { cmd })));
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("no workspace/session tree", reply.RootElement.GetProperty("error").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(host.Tree()));
    }

    [Theory]
    [InlineData("on", true)][InlineData("off", true)][InlineData("toggle", true)]
    [InlineData("bogus", false)][InlineData("", false)]
    public void QuickOperationIsValidated(string op, bool ok)
    {
        var server = new ControlServer(new FakeSessionHost());
        using var reply = JsonDocument.Parse(server.Dispatch(JsonSerializer.Serialize(new { cmd = "quick", args = new { op } })));
        Assert.Equal(ok, reply.RootElement.GetProperty("ok").GetBoolean());
    }
}
