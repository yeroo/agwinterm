using System.Text.Json;

namespace Agwinterm.Pty.Tests;

public class WorkspaceNavigationTests
{
    private static JsonElement Call(ControlServer server, string raw)
    { using var doc = JsonDocument.Parse(server.Dispatch(raw)); return doc.RootElement.Clone(); }

    [Theory]
    [InlineData("{}")][InlineData("{\"to\":\"up\"}")][InlineData("{\"to\":null}")]
    [InlineData("{\"to\":2}")][InlineData("{\"to\":\"\"}")]
    public void BadDirectionsRefuseWithoutMutation(string args)
    {
        var host = new FakeSessionHost(); host.NewWorkspace("second");
        string before = JsonSerializer.Serialize(host.Tree());
        var result = Call(new(host), "{\"cmd\":\"workspace.go\",\"args\":" + args + "}");
        Assert.False(result.GetProperty("ok").GetBoolean()); Assert.Equal(before, JsonSerializer.Serialize(host.Tree()));
    }

    [Theory]
    [InlineData("\"target\":2")][InlineData("\"target\":\"\"")]
    [InlineData("\"target\":false")][InlineData("\"window\":2")]
    [InlineData("\"window\":\"\"")][InlineData("\"window\":\"active\"")]
    public void MalformedOrUnroutableSelectorsRefuse(string selector)
    {
        var host = new FakeSessionHost(); host.NewWorkspace("second");
        string before = JsonSerializer.Serialize(host.Tree());
        Assert.False(Call(new(host), "{\"cmd\":\"workspace.go\",\"args\":{\"to\":\"next\"}," + selector + "}").GetProperty("ok").GetBoolean());
        Assert.Equal(before, JsonSerializer.Serialize(host.Tree()));
    }

    [Fact] public void ExplicitTargetAndQuickHostRefuse()
    {
        var host = new FakeSessionHost(); host.NewWorkspace("second");
        var server = new ControlServer(host);
        Assert.False(Call(server, "{\"cmd\":\"workspace.go\",\"target\":\"active\",\"args\":{\"to\":\"next\"}}").GetProperty("ok").GetBoolean());
        host.IsQuickSurface = true;
        Assert.False(Call(server, "{\"cmd\":\"workspace.go\",\"args\":{\"to\":\"next\"}}").GetProperty("ok").GetBoolean());
    }

    [Fact] public void EmptyDestinationIsCurrentWithoutErasingSelectedSession()
    {
        var host = new FakeSessionHost(); var old = host.ActiveSess; string next = host.NewWorkspace("empty");
        var server = new ControlServer(host);
        var response = Call(server, "{\"cmd\":\"workspace.go\",\"args\":{\"to\":\"next\"}}");
        Assert.True(response.GetProperty("ok").GetBoolean()); Assert.Equal(next, response.GetProperty("result").GetString());
        Assert.Equal(next, host.ActiveWs.Id); Assert.Same(old, host.ActiveSess);
        Assert.Equal("w1", Call(server, "{\"cmd\":\"workspace.go\",\"args\":{\"to\":\"previous\"}}").GetProperty("result").GetString());
    }

    [Fact] public void TreeCarriesCollapsedAndPerPaneShellHints()
    {
        var host = new FakeSessionHost(); host.ActiveWs.Collapsed = true;
        host.ActiveSess!.PaneCount = 2; host.ActiveSess.ForegroundShells = new() { null, "pwsh" };
        var tree = Call(new(host), "{\"cmd\":\"tree\"}").GetProperty("result").GetProperty("workspaces")[0];
        Assert.True(tree.GetProperty("collapsed").GetBoolean());
        var session = tree.GetProperty("sessions")[0];
        Assert.Equal(JsonValueKind.Null, session.GetProperty("foregroundShells")[0].ValueKind);
        Assert.False(session.TryGetProperty("foregroundShell", out _));
        Assert.Equal("pwsh", session.GetProperty("splitForegroundShell").GetString());
    }
}
