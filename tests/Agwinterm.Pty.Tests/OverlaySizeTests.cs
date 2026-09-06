using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// session.overlay's <c>size-percent</c> is VALIDATED, not clamped (P2, "stop lying to the caller").
///
/// Before this, <c>0</c>, <c>-5</c>, <c>150</c> and <c>sixty</c> all produced a full-screen overlay
/// with <c>ok:true</c>: the CLI dropped an unparseable value, <c>GetInt</c> defaulted a non-number to
/// 0, and the host clamped the rest. Three coercions, every one silent. Now: absent keeps its meaning
/// (the full content region), 1..100 is honoured as asked, and anything else is refused with the
/// value and the range named — and, like #213's control-byte refusal, a refusal leaves the world
/// untouched. Every refusal here is asserted twice: the reply, and <c>tree</c> afterwards.
/// </summary>
public class OverlaySizeTests
{
    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    /// <summary>Raw args JSON, so a float or a JSON string can be sent exactly as a caller would.</summary>
    private static JsonElement Overlay(ControlServer server, string argsJson)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.overlay\",\"args\":" + argsJson + "}")).RootElement;

    private static JsonElement Open(ControlServer server, string sizeJson)
        => Overlay(server, "{\"action\":\"open\",\"command\":\"cmd\",\"size-percent\":" + sizeJson + "}");

    private static JsonElement Resize(ControlServer server, string sizeJson)
        => Overlay(server, "{\"action\":\"resize\",\"size-percent\":" + sizeJson + "}");

    private static bool Ok(JsonElement r) => r.GetProperty("ok").GetBoolean();
    private static string Error(JsonElement r) => r.GetProperty("error").GetString() ?? "";
    private static string Result(JsonElement r) => r.GetProperty("result").GetString() ?? "";

    /// <summary>The session node from <c>tree</c> — the read-back a caller has, as opposed to the reply.</summary>
    private static JsonElement TreeSession(ControlServer server)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"tree\"}")).RootElement
            .GetProperty("result").GetProperty("workspaces")[0].GetProperty("sessions")[0];

    private static bool TreeOverlay(ControlServer server)
        => TreeSession(server).TryGetProperty("overlay", out var v) && v.GetBoolean();

    private static int? TreeOverlaySize(ControlServer server)
        => TreeSession(server).TryGetProperty("overlaySize", out var v) ? v.GetInt32() : null;

    // ---- refusals on open: the reply, and then the tree ----

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("101")]
    [InlineData("\"sixty\"")]   // a non-number
    [InlineData("60.5")]        // a JSON float
    [InlineData("\"60\"")]      // the JSON string "60" — a number in quotes is not a number
    public void Open_RefusesOutOfRangeOrNonNumber_AndOpensNothing(string sizeJson)
    {
        var (server, host) = New();
        var r = Open(server, sizeJson);
        Assert.False(Ok(r));
        Assert.False(host.ActiveSess!.Overlay);
        Assert.False(TreeOverlay(server));   // tree spells "no overlay" by omitting the flag
        Assert.Null(TreeOverlaySize(server));
    }

    [Fact]
    public void Refusal_NamesTheValue_TheRange_AndHowToAskForFull()
    {
        var (server, _) = New();
        string err = Error(Open(server, "150"));
        Assert.Contains("150", err);
        Assert.Contains("1..100", err);
        Assert.Contains("omit", err);   // `--size-percent 0` meant "full"; omitting the flag is how to say that
        Assert.Contains("full content region", err);

        // The string form is quoted back so the caller sees it was a STRING that was refused.
        Assert.Contains("\"sixty\"", Error(Open(server, "\"sixty\"")));
    }

    // ---- refusals on resize: the open overlay must keep its size ----

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("101")]
    [InlineData("\"sixty\"")]
    [InlineData("60.5")]
    [InlineData("\"60\"")]
    public void Resize_RefusesOutOfRangeOrNonNumber_AndDoesNotResize(string sizeJson)
    {
        var (server, _) = New();
        Assert.True(Ok(Open(server, "30")));
        Assert.Equal(30, TreeOverlaySize(server));

        var r = Resize(server, sizeJson);
        Assert.False(Ok(r));
        Assert.Contains("1..100", Error(r));
        Assert.Equal(30, TreeOverlaySize(server));   // through tree, not the reply: the world did not move
    }

    // ---- the edges are honoured, and reported as asked ----

    [Fact]
    public void Open_AcceptsOneAndOneHundred()
    {
        var (server, _) = New();
        Assert.True(Ok(Open(server, "1")));
        Assert.Equal(1, TreeOverlaySize(server));

        var (server2, _) = New();
        Assert.True(Ok(Open(server2, "100")));
        Assert.Equal(100, TreeOverlaySize(server2));
    }

    [Fact]
    public void Open_RepliesWithTheOverlayId_ShapeUnchanged()
    {
        var (server, host) = New();
        var r = Open(server, "40");
        Assert.True(Ok(r));
        Assert.Equal(host.ActiveSess!.Id, Result(r));   // the fake answers the session id (the app answers the overlay pane id — not mirrored, see FakeSessionHost.SessionOverlay); this pins the fake's shape only
    }

    [Fact]
    public void Resize_RepliesWithTheSizeAsked_NotACoercedOne()
    {
        var (server, _) = New();
        Assert.True(Ok(Open(server, "30")));
        Assert.Equal("resized 100%", Result(Resize(server, "100")));
        Assert.Equal(100, TreeOverlaySize(server));
        Assert.Equal("resized 1%", Result(Resize(server, "1")));
        Assert.Equal(1, TreeOverlaySize(server));
    }

    // ---- absent keeps today's meaning: the full content region, and tree stays lean ----

    [Fact]
    public void Open_WithoutSizePercent_IsFullRegion_AndTreeOmitsOverlaySize()
    {
        var (server, host) = New();
        var r = Overlay(server, "{\"action\":\"open\",\"command\":\"cmd\"}");
        Assert.True(Ok(r));
        Assert.True(host.ActiveSess!.Overlay);
        Assert.Equal(0, host.ActiveSess.OverlaySize);
        Assert.True(TreeOverlay(server));
        Assert.Null(TreeOverlaySize(server));   // 0 = full region is spelled by ABSENCE, on the way in and on the way out
    }

    /// <summary>A resize with no overlay open is REFUSED and says "no overlay" — the size question is
    /// only asked of a valid size, and a valid or absent size on a session with nothing to resize must
    /// not turn into a complaint about the size. It was ok:true "no overlay" until revmux r1 of P2
    /// pointed out that a script branching on ok then proceeds as if the resize had happened.</summary>
    [Fact]
    public void Resize_WithNoOverlay_IsRefused_AndSaysNoOverlay()
    {
        var (server, _) = New();
        foreach (var r in new[] { Resize(server, "50"), Overlay(server, "{\"action\":\"resize\"}") })
        {
            Assert.False(r.GetProperty("ok").GetBoolean());
            Assert.Contains("no overlay", r.GetProperty("error").GetString());
        }
    }

    /// <summary>The other two overlay failures are refusals too: open with no command, open on a
    /// target that does not resolve. Neither opened anything, and the reply must not say otherwise.
    /// Close with nothing open stays ok — closing nothing leaves "no overlay open" true, and the
    /// conformance contract closes with nothing open and expects ok.</summary>
    [Fact]
    public void Open_WithNoCommandOrNoSession_IsRefused_CloseWithNothingOpen_IsNot()
    {
        var (server, _) = New();
        var noCommand = Overlay(server, "{\"action\":\"open\"}");
        Assert.False(noCommand.GetProperty("ok").GetBoolean());
        Assert.Contains("command", noCommand.GetProperty("error").GetString());
        var noSession = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.overlay\",\"target\":\"no-such-session\",\"args\":{\"action\":\"open\",\"command\":\"git diff\"}}")).RootElement;
        Assert.False(noSession.GetProperty("ok").GetBoolean());
        Assert.Contains("no session", noSession.GetProperty("error").GetString());
        var close = Overlay(server, "{\"action\":\"close\"}");
        Assert.True(close.GetProperty("ok").GetBoolean());
        Assert.Equal("no overlay", close.GetProperty("result").GetString());
    }

    /// <summary>The state close does NOT get to call ok: a named target that matches no session at
    /// all. A typo'd id, or a session that has exited — the overlay the caller meant may still be up,
    /// and "no overlay" would say it is gone. Resize on the same target says the same thing, not
    /// "open one first" on a session that does not exist. (revmux r2 of P2)</summary>
    [Fact]
    public void CloseOrResize_OnATargetThatMatchesNoSession_IsRefused()
    {
        var (server, _) = New();
        foreach (var action in new[] { "close", "resize" })
        {
            var r = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.overlay\",\"target\":\"no-such-session\",\"args\":{\"action\":\"" + action + "\",\"size-percent\":50}}")).RootElement;
            Assert.False(r.GetProperty("ok").GetBoolean());
            Assert.Contains("no session", r.GetProperty("error").GetString());
            Assert.DoesNotContain("open one first", r.GetProperty("error").GetString());
        }
    }

    /// <summary>The strict reader itself, at the unit: the three cases the verb distinguishes.</summary>
    [Fact]
    public void TryOverlaySize_DistinguishesAbsentValidAndInvalid()
    {
        static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

        Assert.True(ControlServer.TryOverlaySize(Args("{}"), out int absent, out var e0));
        Assert.Equal(0, absent); Assert.Null(e0);

        Assert.True(ControlServer.TryOverlaySize(Args("{\"size-percent\":42}"), out int valid, out var e1));
        Assert.Equal(42, valid); Assert.Null(e1);

        Assert.False(ControlServer.TryOverlaySize(Args("{\"size-percent\":0}"), out _, out var e2));
        Assert.NotNull(e2);
        Assert.False(ControlServer.TryOverlaySize(Args("{\"size-percent\":99999999999}"), out _, out var e3));   // beyond int, still "not in 1..100"
        Assert.Contains("1..100", e3);
    }
}
