using System.Text.Json;
using Agwinterm.Pty;
namespace Agwinterm.Pty.Tests;

public class SessionHudTests
{
    private static JsonElement Call(ControlServer server, string action, object? args = null, string target = "s1")
        => JsonDocument.Parse(server.Dispatch(JsonSerializer.Serialize(new { cmd = "session.hud." + action, target, args = args ?? new { } }))).RootElement;
    private static HudSpec Spec(string position = "center", int? width = null) => new("title", null, "none", null, null, width, position);
    private static JsonElement Tree(ControlServer server) => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"tree\"}"))
        .RootElement.GetProperty("result").GetProperty("workspaces")[0].GetProperty("sessions")[0];

    [Fact] public void OpenUpdateClose_ReadBackAndBackgroundLifetime()
    {
        var host = new FakeSessionHost(); var server = new ControlServer(host);
        Assert.False(Tree(server).TryGetProperty("hud", out _));
        Assert.True(Call(server, "open", new { message = "building", detail = "tests", spinner = "bar", color = "AaBBcc", position = "top" }).GetProperty("ok").GetBoolean());
        var hud = Tree(server).GetProperty("hud");
        Assert.Equal("top-center", hud.GetProperty("position").GetString());
        Assert.Equal("#aabbcc", hud.GetProperty("backgroundColor").GetString());
        Assert.Equal("bar", hud.GetProperty("spinner").GetString());
        Assert.True(Call(server, "update", new { message = "ready", color = "#000000" }).GetProperty("ok").GetBoolean());
        hud = Tree(server).GetProperty("hud");
        Assert.Equal("#aabbcc", hud.GetProperty("backgroundColor").GetString());
        Assert.Equal("center", hud.GetProperty("position").GetString());
        Assert.Equal("none", hud.GetProperty("spinner").GetString());
        Assert.Equal(JsonValueKind.Null, hud.GetProperty("detail").ValueKind);
        Assert.True(Call(server, "close").GetProperty("ok").GetBoolean());
        Assert.True(Call(server, "close").GetProperty("ok").GetBoolean());
        Assert.False(Tree(server).TryGetProperty("hud", out _));
        Assert.False(Call(server, "update", new { message = "not open" }).GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("{}")] [InlineData("{\"message\":\" \"}")]
    [InlineData("{\"message\":1}")] [InlineData("{\"message\":\"a\\nb\"}")]
    [InlineData("{\"message\":\"ok\",\"detail\":false}")]
    [InlineData("{\"message\":\"ok\",\"spinner\":true}")]
    [InlineData("{\"message\":\"ok\",\"spinner\":\"bad\"}")]
    [InlineData("{\"message\":\"ok\",\"position\":\"north\"}")]
    [InlineData("{\"message\":\"ok\",\"size-percent\":1.5}")]
    [InlineData("{\"message\":\"ok\",\"size-percent\":101}")]
    [InlineData("{\"message\":\"ok\",\"size-percent\":0}")]
    [InlineData("{\"message\":\"ok\",\"size-percent\":null}")]
    [InlineData("{\"message\":\"ok\",\"color\":\"#ffff\"}")]
    [InlineData("{\"message\":\"ok\",\"text-color\":\"bad\"}")]
    [InlineData("{\"message\":\"ok\",\"pane\":\"right\"}")]
    [InlineData("{\"message\":\"ok\",\"window\":\"silently-ignored\"}")]
    [InlineData("[]")]
    public void InvalidUpdatePreservesLiveHud(string raw)
    {
        var host = new FakeSessionHost(); var server = new ControlServer(host);
        Call(server, "open", new { message = "keep" });
        using var args = JsonDocument.Parse(raw);
        var result = Call(server, "update", args.RootElement);
        Assert.False(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Equal("keep", host.ActiveSess!.Hud!.Message);
    }

    [Fact] public void UnknownTargetAndProgramOverlayNeverMutate()
    {
        var host = new FakeSessionHost(); var server = new ControlServer(host);
        Assert.False(Call(server, "open", new { message = "x" }, "missing").GetProperty("ok").GetBoolean());
        host.ActiveSess!.Overlay = true;
        Assert.False(Call(server, "open", new { message = "x" }).GetProperty("ok").GetBoolean());
        Assert.True(Call(server, "close").GetProperty("ok").GetBoolean());
        Assert.True(host.ActiveSess.Overlay); Assert.Null(host.ActiveSess.Hud);
        Assert.False(Call(server, "close", new { message = "not ignored" }).GetProperty("ok").GetBoolean());
    }

    [Fact] public void UnicodeCeilingIsNormalizedScalarCount()
    {
        var server = new ControlServer(new FakeSessionHost());
        Assert.True(Call(server, "open", new { message = string.Concat(Enumerable.Repeat("e\u0301", 256)) }).GetProperty("ok").GetBoolean());
        Assert.True(Call(server, "open", new { message = string.Concat(Enumerable.Repeat("😀", 256)) }).GetProperty("ok").GetBoolean());
        Assert.False(Call(server, "open", new { message = new string('x', 257) }).GetProperty("ok").GetBoolean());
        Assert.False(Call(server, "open", new { message = "x", detail = new string('x', 257) }).GetProperty("ok").GetBoolean());
    }

    [Theory] [InlineData(1, 10)] [InlineData(50, 50)] [InlineData(100, 80)]
    public void WidthReadBackIsEffective(int requested, int effective)
    {
        using var args = JsonDocument.Parse("{\"message\":\"x\",\"size-percent\":" + requested + "}");
        Assert.True(SessionHuds.TryParse("open", args.RootElement, out var spec, out _));
        Assert.Equal(effective, spec!.SizePercent);
    }

    [Fact] public void AllNineAnchorsRespectMarginsAndStayBounded()
    {
        for (int i = 0; i < 9; ++i)
        foreach (float width in new[] { 0f, 1f, 25f, 800f })
        foreach (float height in new[] { 0f, 1f, 15f, 600f })
        {
            var b = SessionHuds.Place(10, 20, width, height, width * .25f, height * .2f, Spec(SessionHuds.Positions[i]));
            Assert.InRange(b.X, 10, 10 + width); Assert.InRange(b.Y, 20, 20 + height);
            Assert.True(b.X + b.Width <= 10 + width + .001f); Assert.True(b.Y + b.Height <= 20 + height + .001f);
            float expectedX = (i % 3) switch { 0 => .1f, 1 => .375f, _ => .65f };
            Assert.Equal(10 + width * expectedX, b.X, 3);
            var huge = SessionHuds.Place(0, 0, width, height, 9999, 9999, Spec(SessionHuds.Positions[i]));
            Assert.Equal(width * .8f, huge.Width, 3); Assert.Equal(height * .8f, huge.Height, 3);
        }
    }

    [Fact] public void EverySpinnerHasChangingFramesAndNoneIsStatic()
    {
        Assert.Equal("", SessionHuds.Frame("none", 2000));
        foreach (var spinner in SessionHuds.Spinners.Where(s => s != "none"))
            Assert.True(Enumerable.Range(0, 20).Select(i => SessionHuds.Frame(spinner, i * 100)).Distinct().Count() > 1);
    }

    [Theory]
    [InlineData("\"target\":false")] [InlineData("\"target\":[]")]
    [InlineData("\"window\":123")] [InlineData("\"window\":\"unavailable\"")]
    public void MalformedOrUnsupportedRoutingNeverFallsBackToActive(string routing)
    {
        var host = new FakeSessionHost(); var server = new ControlServer(host);
        var result = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.hud.open\",\"args\":{\"message\":\"wrong\"}," + routing + "}"));
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Null(host.ActiveSess!.Hud);
    }
}
