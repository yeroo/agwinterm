using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// P4 — A SESSION'S TWO PANES, AS THE CONTROL API SEES THEM. One file for the batch's four items,
/// sectioned: (1) <c>session split</c> replies with the pane id, and the degenerate ops stop lying;
/// (2) the axis; (3) <c>session split close</c>; (4) <c>session swap</c>.
///
/// Section 1. Before P4 <c>session split</c> answered the constant <c>"split"</c> from a
/// Post-and-return-true, so the caller could not address the shell it had just asked for, and
/// <c>on</c> when already split (or <c>off</c> when already single) said ok:true having done nothing —
/// the P2 defect class. Now the reply is a pane id read back off the session AFTER the op, inside the
/// same UI hop: <c>on</c>/<c>toggle</c>-on name the split pane (also when it already existed —
/// lite's rule, so "make sure this session is split" gets something addressable either way),
/// <c>off</c>/<c>toggle</c>-off name the survivor. Every reply is a bare string, because the shipped
/// conformance step on <c>split off</c> is a string type check. Every refusal splits nothing.
/// </summary>
public class SessionSplitTests
{
    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    /// <summary>Dispatch <c>session.split</c> with a raw JSON args object (so <c>axis</c> can be a
    /// non-string when a test needs one).</summary>
    private static JsonElement Split(ControlServer server, string? target, string argsJson)
    {
        var sb = new System.Text.StringBuilder("{\"cmd\":\"session.split\"");
        if (target is not null) sb.Append(",\"target\":").Append(JsonSerializer.Serialize(target));
        sb.Append(",\"args\":").Append(argsJson).Append('}');
        return JsonDocument.Parse(server.Dispatch(sb.ToString())).RootElement;
    }

    private static JsonElement Op(ControlServer server, string op, string? target = null, string? axis = null)
        => Split(server, target, axis is null
            ? "{\"op\":" + JsonSerializer.Serialize(op) + "}"
            : "{\"op\":" + JsonSerializer.Serialize(op) + ",\"axis\":" + JsonSerializer.Serialize(axis) + "}");

    private static bool Ok(JsonElement r) => r.GetProperty("ok").GetBoolean();
    private static string Error(JsonElement r) => r.GetProperty("error").GetString() ?? "";

    /// <summary>The reply, asserted to be the STRING the contract's type check expects, then returned.</summary>
    private static string Id(JsonElement r)
    {
        Assert.True(Ok(r), r.ToString());
        var res = r.GetProperty("result");
        Assert.Equal(JsonValueKind.String, res.ValueKind);
        return res.GetString()!;
    }

    private static JsonElement TreeSession(ControlServer server, int index = 0)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"tree\"}")).RootElement
            .GetProperty("result").GetProperty("workspaces")[0].GetProperty("sessions")[index];

    private static string[] PaneIds(ControlServer server, int index = 0)
    {
        var s = TreeSession(server, index);
        return s.TryGetProperty("paneIds", out var ids)
            ? ids.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : new[] { s.GetProperty("id").GetString()! };
    }

    private static int PaneCount(ControlServer server, int index = 0)
        => TreeSession(server, index).TryGetProperty("paneCount", out var n) ? n.GetInt32() : 1;

    // ---- section 1: the reply is the pane id ----

    [Fact]
    public void SplitOn_RepliesWithTheSplitPaneId_WhichTheTreeLists()
    {
        var (server, host) = New();
        string sessionId = host.ActiveSess!.Id;

        string id = Id(Op(server, "on"));

        var ids = PaneIds(server);
        Assert.Equal(2, PaneCount(server));
        Assert.Equal(2, ids.Length);
        Assert.Equal(sessionId, ids[0]);                 // pane 0 keeps the session id
        Assert.Equal(id, ids[1]);                        // the reply IS the new entry
        Assert.NotEqual(sessionId, id);                  // and not the session id dressed up
        Assert.NotEqual("split", id);                    // the pre-P4 constant
        Assert.Equal(1, TreeSession(server).GetProperty("focusedPane").GetInt32());   // the new pane takes focus, as the app does
    }

    [Fact]
    public void SplitOn_WhenAlreadySplit_RepliesWithTheSameId_AndChangesNothing()
    {
        var (server, host) = New();
        string first = Id(Op(server, "on"));
        var before = TreeSession(server).GetRawText();

        string again = Id(Op(server, "on"));

        Assert.Equal(first, again);
        Assert.Equal(2, PaneCount(server));
        Assert.Equal(2, host.ActiveSess!.PaneIds.Count);            // no third pane minted
        Assert.Equal(before, TreeSession(server).GetRawText());     // ratios, focus, ids: all as they were
    }

    [Fact]
    public void SplitOff_RepliesWithPaneZero_AlsoWhenAlreadySingle()
    {
        var (server, host) = New();
        string sessionId = host.ActiveSess!.Id;
        string split = Id(Op(server, "on"));

        string survivor = Id(Op(server, "off"));
        Assert.Equal(sessionId, survivor);               // pane 0 survives a collapse
        Assert.NotEqual(split, survivor);
        Assert.Equal(1, PaneCount(server));
        Assert.Equal(new[] { sessionId }, PaneIds(server));

        string again = Id(Op(server, "off"));            // off when already single: the only pane, nothing changed
        Assert.Equal(sessionId, again);
        Assert.Equal(1, PaneCount(server));
        Assert.Single(host.ActiveSess!.PaneIds);
    }

    [Fact]
    public void Toggle_RepliesWithWhateverItProduced()
    {
        var (server, host) = New();
        string sessionId = host.ActiveSess!.Id;

        string produced = Id(Op(server, "toggle"));      // single → split: the split pane
        Assert.Equal(2, PaneCount(server));
        Assert.Equal(PaneIds(server)[1], produced);
        Assert.NotEqual(sessionId, produced);

        string survivor = Id(Op(server, "toggle"));      // split → single: the survivor
        Assert.Equal(1, PaneCount(server));
        Assert.Equal(sessionId, survivor);
    }

    [Fact]
    public void DefaultOp_IsToggle_AndStillRepliesWithAnId()
    {
        var (server, _) = New();
        string id = Id(Split(server, null, "{}"));
        Assert.Equal(2, PaneCount(server));
        Assert.Equal(PaneIds(server)[1], id);
    }

    [Fact]
    public void SplitOff_DropsTheGonePanesPerPaneState_WithIt()
    {
        var (server, host) = New();
        string split = Id(Op(server, "on"));
        var s = host.ActiveSess!;
        s.RestorePins[split] = "claude --resume"; s.Captured[split] = "vim"; s.Foreground[split] = "vim";

        Id(Op(server, "off"));

        Assert.False(s.RestorePins.ContainsKey(split));
        Assert.False(s.Captured.ContainsKey(split));
        Assert.False(s.Foreground.ContainsKey(split));
    }

    // ---- section 1: targets (#230, kept and not weakened) ----

    [Fact]
    public void HonoursTarget_TheIdBelongsToTheTargetedSession_NotTheActiveOne()
    {
        var (server, host) = New();
        var original = host.ActiveSess!;
        string other = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.new\",\"args\":{\"name\":\"other\"}}"))
            .RootElement.GetProperty("result").GetString()!;
        Assert.Equal("other", host.ActiveSess!.Name);   // session.new made the new one active

        string id = Id(Op(server, "on", target: original.Id));

        Assert.Equal(2, original.PaneCount);                       // the targeted session split
        Assert.Equal(id, original.PaneIds[1]);                     // and the id is ITS split pane
        Assert.Equal(1, host.ActiveSess!.PaneCount);               // the active one was left alone
        Assert.Equal("other", host.ActiveSess!.Name);              // and focus did not move
        Assert.DoesNotContain(id, host.ActiveSess!.PaneIds);
        Assert.Equal(new[] { other }, host.ActiveSess!.PaneIds);
    }

    [Fact]
    public void ATargetOfThePaneId_LandsOnItsSession()
    {
        var (server, host) = New();
        string split = Id(Op(server, "on"));
        string survivor = Id(Op(server, "off", target: split));   // the split pane's id names its session
        Assert.Equal(host.ActiveSess!.Id, survivor);
        Assert.Equal(1, PaneCount(server));
    }

    [Fact]
    public void UnknownTarget_IsRefused_AndNoSessionChanged()
    {
        var (server, host) = New();
        server.Dispatch("{\"cmd\":\"session.new\",\"args\":{\"name\":\"other\"}}");
        var counts = host.Workspaces.SelectMany(w => w.Sessions).Select(s => s.PaneCount).ToArray();

        var r = Op(server, "on", target: "no-such-session");

        Assert.False(Ok(r));
        Assert.Contains("session not found", Error(r));
        Assert.Equal(counts, host.Workspaces.SelectMany(w => w.Sessions).Select(s => s.PaneCount).ToArray());   // nothing split as a fallback
        Assert.All(host.Workspaces.SelectMany(w => w.Sessions), s => Assert.Single(s.PaneIds));
    }

    // ---- section 1: the axis word is read strictly at the server (the words themselves are section 2) ----

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("{\"v\":\"vertical\"}")]
    [InlineData("null")]
    public void Axis_ThatIsNotAString_IsRefused_NotDefaulted(string axisJson)
    {
        var (server, host) = New();
        var r = Split(server, null, "{\"op\":\"on\",\"axis\":" + axisJson + "}");
        Assert.False(Ok(r));
        Assert.Contains("axis", Error(r));
        Assert.Contains("vertical", Error(r));
        Assert.Contains("horizontal", Error(r));
        Assert.Equal(1, host.ActiveSess!.PaneCount);
        Assert.Single(host.ActiveSess!.PaneIds);
    }

    [Fact]
    public void Axis_ThatIsNotOneOfTheTwoWords_IsRefused_AndNothingSplit()
    {
        var (server, host) = New();
        var r = Op(server, "on", axis: "diagonal");
        Assert.False(Ok(r));
        Assert.Contains("'diagonal'", Error(r));
        Assert.Contains("vertical", Error(r));
        Assert.Contains("horizontal", Error(r));
        Assert.Equal(1, host.ActiveSess!.PaneCount);
    }

    [Fact]
    public void Axis_Vertical_IsAccepted_AndTheReplyIsStillTheId()
    {
        var (server, _) = New();
        string id = Id(Op(server, "on", axis: SplitAxes.Vertical));
        Assert.Equal(PaneIds(server)[1], id);
    }

    [Fact]
    public void SplitAxes_TryParse_AcceptsExactlyTheTwoWords_AndNullMeansKeep()
    {
        Assert.True(SplitAxes.TryParse(null, out var none, out var r0)); Assert.Null(none); Assert.Null(r0);
        Assert.True(SplitAxes.TryParse("vertical", out var v, out _)); Assert.Equal(SplitAxes.Vertical, v);
        Assert.True(SplitAxes.TryParse("horizontal", out var h, out _)); Assert.Equal(SplitAxes.Horizontal, h);
        foreach (var bad in new[] { "", "h", "V", "Horizontal", "diagonal", " vertical" })
        {
            Assert.False(SplitAxes.TryParse(bad, out var a, out var refusal), bad);
            Assert.Null(a);
            Assert.Contains("'" + bad + "'", refusal);
            Assert.Contains("vertical", refusal);
            Assert.Contains("horizontal", refusal);
        }
    }
}
