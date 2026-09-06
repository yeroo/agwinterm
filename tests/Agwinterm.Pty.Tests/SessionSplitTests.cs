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
///
/// Section 2. The axis, in agterm's words: <c>vertical</c> = left/right panes (the default of a session
/// never split), <c>horizontal</c> = top/bottom. Per session, set by <c>on</c> / a splitting toggle,
/// re-oriented LIVE by <c>on --axis</c> on an already-split session (the reply stays the existing
/// split pane's id), ignored by <c>off</c> and kept across it. The tree carries <c>axis</c> inside the
/// split block — always while split, never for one pane. Every other spelling is refused with the
/// pane count unchanged. <c>session focus</c> takes agterm's seven words and refuses a direction
/// that does not exist on the session's axis; <c>session resize</c> refuses the other axis's grow
/// flags with the divider unmoved.
///
/// Section 3. <c>session split close</c> closes ONE pane — EITHER side — and replies with the survivor's
/// id. The tracker had filed this as "naming, not behaviour" because <c>off</c> already destroys the
/// split shell; it was behaviour: <c>off</c> hard-codes pane 0 as the survivor, so no verb could close
/// pane 0 of two. The survivor becomes pane 0 whichever slot it was in, keeps its id and stays
/// resolvable; a session NAME or no target means the focused pane; the session id means the pane that
/// carries it (the resolver's exact-pane-first order); a one-pane session is refused naming
/// <c>session close</c>; a cover is refused; every refusal closes nothing.
///
/// Section 4. <c>session swap</c> exchanges the two panes: order reversed, focus follows the pane, axis
/// kept, ratio SEQUENCE kept (the left/top box keeps its size; the contents change places), and EVERY
/// ID kept — a swap moves panes, never ids, so the session id keeps naming the shell it named, now on
/// the other side (agterm moves the session's identity instead, because its panes are addressed by
/// role; ours are addressed by id, and a handle must not lie). Everything else — context, flag,
/// overlay, notifications, the status aggregate and its age — stays where it was; per-pane state (a
/// restore pin, a captured slot) travels with its pane. The reply is the tree's split block after the
/// swap, as an object. A one-pane session, a cover and an unknown target are refused with nothing
/// moved. A swapped session's save/load keeps its ids in their new order with no duplicates.
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
    public void UnknownOp_IsRefusedOnTheWire_AndNothingIsSplitOrCollapsed()
    {
        // The host treats an unknown op as toggle, so a raw client sending "Close" or "clos" against
        // a split session would collapse it — pane 1's shell gone — with ok:true (revmux r2/r3 of P4).
        // The CLI lowercases and validates; the SERVER must too, because the CLI is not the only client.
        var (server, host) = New();
        var single = Op(server, "Close");
        Assert.False(Ok(single));
        Assert.Contains("unknown op", Error(single));
        Assert.Contains("Nothing was split or closed", Error(single));
        Assert.Equal(1, PaneCount(server));                                   // a single pane stayed single

        string split = Id(Op(server, "on"));
        foreach (var bad in new[] { "Close", "clos", "close", "ON", "" })
        {
            var r = Op(server, bad);
            Assert.False(Ok(r), bad);
            Assert.Contains("unknown op", Error(r));
            Assert.Equal(2, PaneCount(server));                               // the split stood
            Assert.Equal(split, PaneIds(server)[1]);                          // with the same pane
        }
        var nonString = Split(server, null, "{\"op\":1}");
        Assert.False(Ok(nonString));
        Assert.Contains("unknown op", Error(nonString));
        Assert.Equal(2, PaneCount(server));
    }

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

    // ---- section 2: the axis ----

    private static string? TreeAxis(ControlServer server, int index = 0)
        => TreeSession(server, index).TryGetProperty("axis", out var a) ? a.GetString() : null;

    private static JsonElement Focus(ControlServer server, string dir)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.focus\",\"args\":{\"dir\":" + JsonSerializer.Serialize(dir) + "}}")).RootElement;

    private static JsonElement Resize(ControlServer server, string argsJson)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.resize\",\"args\":" + argsJson + "}")).RootElement;

    [Theory]
    [InlineData("vertical")]
    [InlineData("horizontal")]
    public void Axis_EitherWord_Splits_AndTheTreeReportsIt(string axis)
    {
        var (server, host) = New();
        string id = Id(Op(server, "on", axis: axis));
        Assert.Equal(2, PaneCount(server));
        Assert.Equal(PaneIds(server)[1], id);
        Assert.Equal(axis, TreeAxis(server));
        Assert.Equal(axis, host.ActiveSess!.Axis);
    }

    [Theory]
    [InlineData("h")]
    [InlineData("V")]
    [InlineData("diagonal")]
    [InlineData("")]
    public void Axis_OtherSpellings_AreRefused_Reported_AndNothingSplit(string axis)
    {
        var (server, host) = New();
        var r = Op(server, "on", axis: axis);
        Assert.False(Ok(r));
        Assert.Contains("'" + axis + "'", Error(r));
        Assert.Contains("vertical", Error(r));
        Assert.Contains("horizontal", Error(r));
        Assert.Equal(1, PaneCount(server));                       // paneCount unchanged
        Assert.Single(host.ActiveSess!.PaneIds);
        Assert.Equal(SplitAxes.Vertical, host.ActiveSess!.Axis);  // and the orientation untouched
        Assert.Null(TreeAxis(server));
    }

    [Fact]
    public void Axis_OnAnAlreadySplitSession_ReorientsLive_AndStillAnswersTheSplitPaneId()
    {
        var (server, host) = New();
        string id = Id(Op(server, "on"));                          // vertical, the default
        Assert.Equal(SplitAxes.Vertical, TreeAxis(server));
        var ratios = TreeSession(server).GetProperty("splitRatios").GetRawText();

        string again = Id(Op(server, "on", axis: SplitAxes.Horizontal));

        Assert.Equal(id, again);                                  // the existing split pane, not a new one
        Assert.Equal(2, PaneCount(server));
        Assert.Equal(SplitAxes.Horizontal, TreeAxis(server));      // re-oriented
        Assert.Equal(ratios, TreeSession(server).GetProperty("splitRatios").GetRawText());   // the ratio sequence kept
        Assert.Equal(2, host.ActiveSess!.PaneIds.Count);

        Assert.Equal(id, Id(Op(server, "on", axis: SplitAxes.Vertical)));   // and back
        Assert.Equal(SplitAxes.Vertical, TreeAxis(server));
    }

    [Fact]
    public void Axis_IsInTheTree_OnlyWhileSplit()
    {
        var (server, _) = New();
        Assert.Null(TreeAxis(server));                            // one pane: no axis key
        Id(Op(server, "on", axis: SplitAxes.Horizontal));
        Assert.Equal(SplitAxes.Horizontal, TreeAxis(server));      // split: always present
        Id(Op(server, "off"));
        Assert.Null(TreeAxis(server));                            // back to one pane: gone again
    }

    [Fact]
    public void Axis_SurvivesOff_SoTheNextOnWithoutOneKeepsIt()
    {
        var (server, host) = New();
        Id(Op(server, "on", axis: SplitAxes.Horizontal));
        Id(Op(server, "off"));                                     // off ignores and keeps the axis
        Assert.Equal(SplitAxes.Horizontal, host.ActiveSess!.Axis);
        Id(Op(server, "toggle"));                                  // toggle without an axis keeps the session's
        Assert.Equal(SplitAxes.Horizontal, TreeAxis(server));
    }

    [Fact]
    public void Axis_OffWithAnAxis_IgnoresIt()
    {
        var (server, host) = New();
        Id(Op(server, "on"));
        string survivor = Id(Op(server, "off", axis: SplitAxes.Horizontal));
        Assert.Equal(host.ActiveSess!.Id, survivor);
        Assert.Equal(1, PaneCount(server));
        Assert.Equal(SplitAxes.Vertical, host.ActiveSess!.Axis);   // off did not set it
    }

    // ---- section 2: session focus against the axis ----

    [Fact]
    public void Focus_TopOnAVerticalSplit_IsRefused_NamingTheAxis_AndFocusUnmoved()
    {
        var (server, host) = New();
        Id(Op(server, "on"));                                      // vertical; the new pane (1) is focused
        var r = Focus(server, "top");
        Assert.False(Ok(r));
        Assert.Contains("vertical", Error(r));
        Assert.Contains("'top'", Error(r));
        Assert.Equal(1, host.ActiveSess!.FocusedPane);

        var r2 = Focus(server, "bottom");
        Assert.False(Ok(r2));
        Assert.Equal(1, host.ActiveSess!.FocusedPane);
    }

    [Fact]
    public void Focus_LeftOnAHorizontalSplit_IsRefused_NamingTheAxis()
    {
        var (server, host) = New();
        Id(Op(server, "on", axis: SplitAxes.Horizontal));
        var r = Focus(server, "left");
        Assert.False(Ok(r));
        Assert.Contains("horizontal", Error(r));
        Assert.Contains("'left'", Error(r));
        Assert.Equal(1, host.ActiveSess!.FocusedPane);
        Assert.True(Ok(Focus(server, "top")));                     // the words of this axis work
        Assert.Equal(0, host.ActiveSess!.FocusedPane);
        Assert.True(Ok(Focus(server, "bottom")));
        Assert.Equal(1, host.ActiveSess!.FocusedPane);
    }

    [Fact]
    public void Focus_PrimarySplitOther_WorkOnEitherAxis()
    {
        foreach (var axis in new[] { SplitAxes.Vertical, SplitAxes.Horizontal })
        {
            var (server, host) = New();
            Id(Op(server, "on", axis: axis));
            Assert.True(Ok(Focus(server, "primary"))); Assert.Equal(0, host.ActiveSess!.FocusedPane);
            Assert.True(Ok(Focus(server, "split")));   Assert.Equal(1, host.ActiveSess!.FocusedPane);
            Assert.True(Ok(Focus(server, "other")));   Assert.Equal(0, host.ActiveSess!.FocusedPane);
            Assert.True(Ok(Focus(server, "other")));   Assert.Equal(1, host.ActiveSess!.FocusedPane);
        }
    }

    [Fact]
    public void Focus_LeftRight_WorkOnAVerticalSplit_AndAnUnknownWordIsRefused()
    {
        var (server, host) = New();
        Id(Op(server, "on"));
        Assert.True(Ok(Focus(server, "left")));  Assert.Equal(0, host.ActiveSess!.FocusedPane);
        Assert.True(Ok(Focus(server, "right"))); Assert.Equal(1, host.ActiveSess!.FocusedPane);
        var r = Focus(server, "sideways");
        Assert.False(Ok(r));
        Assert.Contains("'sideways'", Error(r));
        Assert.Equal(1, host.ActiveSess!.FocusedPane);
    }

    [Fact]
    public void Focus_OnASinglePane_IsRefused()
    {
        var (server, host) = New();
        var r = Focus(server, "other");
        Assert.False(Ok(r));
        Assert.Contains("not split", Error(r));
        Assert.Equal(0, host.ActiveSess!.FocusedPane);
    }

    [Fact]
    public void Focus_DefaultDirection_IsOther_WhichWorksOnEitherAxis()
    {
        var (server, host) = New();
        Id(Op(server, "on", axis: SplitAxes.Horizontal));
        var r = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.focus\",\"args\":{}}")).RootElement;
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(0, host.ActiveSess!.FocusedPane);
    }

    // ---- section 2: session resize against the axis ----

    [Fact]
    public void Resize_GrowLeftOnAHorizontalSplit_IsRefused_AndTheDividerDoesNotMove()
    {
        var (server, host) = New();
        Id(Op(server, "on", axis: SplitAxes.Horizontal));
        var before = host.ActiveSess!.Ratios.ToList();

        var r = Resize(server, "{\"grow-left\":3}");
        Assert.False(Ok(r));
        Assert.Contains("horizontal", Error(r));
        Assert.Contains("grow-top", Error(r));
        Assert.Equal(before, host.ActiveSess!.Ratios);

        Assert.False(Ok(Resize(server, "{\"grow-right\":3}")));
        Assert.Equal(before, host.ActiveSess!.Ratios);

        Assert.True(Ok(Resize(server, "{\"grow-bottom\":3}")));   // this axis's flag moves it
        Assert.True(host.ActiveSess!.Ratios[0] > before[0]);
    }

    [Fact]
    public void Resize_GrowTopOnAVerticalSplit_IsRefused_AndGrowLeftWorks()
    {
        var (server, host) = New();
        Id(Op(server, "on"));
        var before = host.ActiveSess!.Ratios.ToList();

        var r = Resize(server, "{\"grow-top\":2}");
        Assert.False(Ok(r));
        Assert.Contains("vertical", Error(r));
        Assert.Contains("grow-left", Error(r));
        Assert.Equal(before, host.ActiveSess!.Ratios);

        Assert.True(Ok(Resize(server, "{\"grow-left\":2}")));
        Assert.True(host.ActiveSess!.Ratios[0] < before[0]);      // the divider moved left: the first pane shrank
    }

    [Fact]
    public void Resize_OnASinglePane_IsRefused()
    {
        var (server, _) = New();
        var r = Resize(server, "{\"ratio\":0.3}");
        Assert.False(Ok(r));
        Assert.Contains("not split", Error(r));
    }

    [Fact]
    public void SplitAxes_TryFocusIndex_And_TryGrow_FollowTheAxis()
    {
        Assert.True(SplitAxes.TryFocusIndex("left", SplitAxes.Vertical, 1, out int i, out _)); Assert.Equal(0, i);
        Assert.True(SplitAxes.TryFocusIndex("bottom", SplitAxes.Horizontal, 0, out i, out _)); Assert.Equal(1, i);
        Assert.True(SplitAxes.TryFocusIndex("other", SplitAxes.Vertical, 1, out i, out _)); Assert.Equal(0, i);
        Assert.False(SplitAxes.TryFocusIndex("top", SplitAxes.Vertical, 0, out _, out var r1)); Assert.Contains("vertical", r1);
        Assert.False(SplitAxes.TryFocusIndex("right", SplitAxes.Horizontal, 0, out _, out var r2)); Assert.Contains("horizontal", r2);
        Assert.False(SplitAxes.TryFocusIndex(null, SplitAxes.Vertical, 0, out _, out var r3)); Assert.NotNull(r3);

        Assert.True(SplitAxes.TryGrow(SplitAxes.Vertical, 2, 5, 0, 0, out int s, out _)); Assert.Equal(3, s);
        Assert.True(SplitAxes.TryGrow(SplitAxes.Horizontal, 0, 0, 4, 1, out s, out _)); Assert.Equal(-3, s);
        Assert.False(SplitAxes.TryGrow(SplitAxes.Horizontal, 1, 0, 0, 0, out _, out var g1)); Assert.Contains("horizontal", g1);
        Assert.False(SplitAxes.TryGrow(SplitAxes.Vertical, 0, 0, 0, 1, out _, out var g2)); Assert.Contains("vertical", g2);
    }

    // ---- section 3: session split close ----

    private static JsonElement Close(ControlServer server, string? target)
        => JsonDocument.Parse(server.Dispatch(target is null
            ? "{\"cmd\":\"session.split.close\"}"
            : "{\"cmd\":\"session.split.close\",\"target\":" + JsonSerializer.Serialize(target) + "}")).RootElement;

    /// <summary>Whether <c>session.text</c> — a content verb, through the host's Resolve — still reaches a target.</summary>
    private static bool Resolves(ControlServer server, string target)
        => Ok(JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.text\",\"target\":" + JsonSerializer.Serialize(target) + "}")).RootElement);

    [Fact]
    public void Close_PaneZero_ByTheSessionIdItCarries_PaneOneSurvivesAsSlotZero_AndStaysResolvable()
    {
        var (server, host) = New();
        string splitId = Id(Op(server, "on", axis: SplitAxes.Horizontal));
        var sess = host.ActiveSess!;
        var survivor = sess.Panes[1];
        server.Dispatch("{\"cmd\":\"session.restore\",\"target\":\"s1\",\"args\":{\"command\":\"gone-with-pane-0\"}}");
        Assert.True(sess.RestorePins.ContainsKey("s1"));

        string reply = Id(Close(server, "s1"));   // pane 0 carries the session id: exact pane wins, so THIS closes pane 0

        Assert.Equal(splitId, reply);                          // the reply names the survivor
        Assert.Equal(1, PaneCount(server));
        Assert.Equal(new[] { splitId }, sess.PaneIds);          // the old pane 1 is now slot 0, under its old id
        Assert.Same(survivor, sess.Panes[0]);                  // the same shell, not a re-minted one
        Assert.Equal(0, sess.FocusedPane);
        Assert.Equal(new[] { 1.0 }, sess.Ratios);
        Assert.Equal(SplitAxes.Horizontal, sess.Axis);         // the axis is kept for the next split
        Assert.Null(TreeAxis(server));                         // but a single pane shows no split block
        Assert.False(sess.RestorePins.ContainsKey("s1"));      // pane 0's per-pane state went with it
        Assert.True(Resolves(server, splitId));                // the survivor is addressable by its old id
        Assert.Same(survivor, host.Resolve(splitId));
        Assert.True(Resolves(server, "s1"));                   // and the session id now reaches it as the focused pane
        Assert.Same(survivor, host.Resolve("s1"));
        Assert.Equal("s1", TreeSession(server).GetProperty("id").GetString());   // the session's own id never moved
    }

    [Fact]
    public void Close_PaneOne_ByItsId_PaneZeroSurvives()
    {
        var (server, host) = New();
        string splitId = Id(Op(server, "on"));
        var sess = host.ActiveSess!;
        var primary = sess.Panes[0];

        string reply = Id(Close(server, splitId));

        Assert.Equal("s1", reply);
        Assert.Equal(1, PaneCount(server));
        Assert.Equal(new[] { "s1" }, sess.PaneIds);
        Assert.Same(primary, sess.Panes[0]);
        Assert.Equal(0, sess.FocusedPane);
        Assert.False(Resolves(server, splitId));               // the closed pane is gone from the resolver
    }

    [Fact]
    public void Close_NoTarget_ClosesTheFocusedPane_WhicheverSideItIs()
    {
        var (server, host) = New();
        string splitId = Id(Op(server, "on"));
        Assert.Equal(1, host.ActiveSess!.FocusedPane);         // a split focuses the new pane

        Assert.Equal("s1", Id(Close(server, null)));           // so no target closes pane 1 and pane 0 survives

        splitId = Id(Op(server, "on"));
        Assert.True(Ok(Focus(server, "primary")));             // now focus pane 0
        Assert.Equal(splitId, Id(Close(server, "active")));    // "active" is the same target: pane 0 goes, pane 1 survives
        Assert.Equal(new[] { splitId }, host.ActiveSess!.PaneIds);
    }

    [Fact]
    public void Close_BySessionName_ClosesTheFocusedPane_ButBySessionId_ThePaneCarryingIt()
    {
        var (server, host) = New();
        string splitId = Id(Op(server, "on"));
        Assert.Equal(1, host.ActiveSess!.FocusedPane);
        Assert.Equal("s1", Id(Close(server, "session 1")));   // a NAME is a session: its focused pane (1) goes

        splitId = Id(Op(server, "on"));
        Assert.Equal(1, host.ActiveSess!.FocusedPane);
        Assert.Equal(splitId, Id(Close(server, "s1")));        // the id is a PANE first: pane 0 goes although pane 1 is focused
    }

    [Fact]
    public void Close_OnASinglePane_IsRefused_NamingSessionClose_AndNothingChanges()
    {
        var (server, host) = New();
        var r = Close(server, null);
        Assert.False(Ok(r));
        Assert.Contains("session close", Error(r));
        Assert.Contains("'s1'", Error(r));
        Assert.Equal(1, PaneCount(server));
        Assert.Single(host.ActiveSess!.PaneIds);
        Assert.Equal("s1", TreeSession(server).GetProperty("id").GetString());   // the session still exists

        var byId = Close(server, "s1");
        Assert.False(Ok(byId));
        Assert.Contains("session close", Error(byId));
        Assert.Equal(1, PaneCount(server));
    }

    [Fact]
    public void Close_ACover_IsRefused_AndTheSplitStands()
    {
        var (server, host) = New();
        Id(Op(server, "on"));
        string coverId = host.ActiveSess!.AddCoverPane();

        var r = Close(server, coverId);

        Assert.False(Ok(r));
        Assert.Contains("'" + coverId + "'", Error(r));
        Assert.Contains("scratch/overlay/quick", Error(r));
        Assert.Equal(2, PaneCount(server));
        Assert.Single(host.ActiveSess!.CoverPanes);            // the cover was not closed either
    }

    [Fact]
    public void Close_UnknownTarget_IsRefused_AndNothingCloses()
    {
        var (server, host) = New();
        Id(Op(server, "on"));
        var r = Close(server, "ghost");
        Assert.False(Ok(r));
        Assert.Contains("'ghost'", Error(r));
        Assert.Equal(2, PaneCount(server));
        Assert.Equal(2, host.ActiveSess!.PaneIds.Count);
    }

    [Fact]
    public void Close_HonoursTarget_OnANonActiveSession_AndLeavesTheActiveOneAlone()
    {
        var (server, host) = New();
        string other = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.new\",\"args\":{\"name\":\"other\"}}"))
            .RootElement.GetProperty("result").GetString()!;
        string otherSplit = Id(Op(server, "on", target: other));
        server.Dispatch("{\"cmd\":\"session.select\",\"target\":\"s1\"}");
        Id(Op(server, "on"));                                  // the active session (s1) is split too
        Assert.Equal("s1", host.ActiveSess!.Id);

        string reply = Id(Close(server, otherSplit));           // close the OTHER session's pane 1 by id

        Assert.Equal(other, reply);
        Assert.Equal("s1", host.ActiveSess!.Id);               // focus did not move
        Assert.Equal(2, PaneCount(server, 0));                 // s1 keeps both panes
        Assert.Equal(1, PaneCount(server, 1));                 // other is single
    }

    [Fact]
    public void Close_ThenSplitAgain_MintsAFreshPane_AndTheSurvivorStaysSlotZero()
    {
        var (server, host) = New();
        string first = Id(Op(server, "on"));
        Assert.Equal(first, Id(Close(server, "s1")));           // pane 0 closed; the split pane survives
        string second = Id(Op(server, "on"));
        Assert.NotEqual(first, second);
        Assert.Equal(new[] { first, second }, host.ActiveSess!.PaneIds);   // the survivor is primary now; the new pane is the split
        Assert.Equal(2, PaneCount(server));
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

    // ---- section 4: session swap ----

    private static JsonElement Swap(ControlServer server, string? target)
        => JsonDocument.Parse(server.Dispatch(target is null
            ? "{\"cmd\":\"session.swap\"}"
            : "{\"cmd\":\"session.swap\",\"target\":" + JsonSerializer.Serialize(target) + "}")).RootElement;

    /// <summary>The reply, asserted to be the OBJECT the sibling contract PR will pin — the four keys
    /// <c>session</c>, <c>paneIds</c>, <c>focusedPane</c>, <c>axis</c>, in that order and nothing else —
    /// then returned.</summary>
    private static JsonElement Swapped(JsonElement r)
    {
        Assert.True(Ok(r), r.ToString());
        var res = r.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, res.ValueKind);
        Assert.Equal(new[] { "session", "paneIds", "focusedPane", "axis" }, res.EnumerateObject().Select(p => p.Name).ToArray());
        return res;
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static double[] Ratios(ControlServer server, int index = 0)
        => TreeSession(server, index).GetProperty("splitRatios").EnumerateArray().Select(e => e.GetDouble()).ToArray();

    [Fact]
    public void Swap_ReversesTheOrder_KeepsTheRatioSequence_AndRepliesWithTheTreeAfter()
    {
        var (server, host) = New();
        string splitId = Id(Op(server, "on", axis: SplitAxes.Horizontal));
        var sess = host.ActiveSess!;
        var primary = sess.Panes[0]; var split = sess.Panes[1];
        Assert.True(Ok(Resize(server, "{\"ratio\":0.7}")));
        Assert.Equal(new[] { 0.7, 0.3 }, Ratios(server));      // 70/30: the top box is the big one
        Assert.Equal(1, sess.FocusedPane);

        var reply = Swapped(Swap(server, null));

        Assert.Equal("s1", reply.GetProperty("session").GetString());
        Assert.Equal(new[] { splitId, "s1" }, Strings(reply.GetProperty("paneIds")));
        Assert.Equal(0, reply.GetProperty("focusedPane").GetInt32());   // the focused shell (the split one) is slot 0 now
        Assert.Equal(SplitAxes.Horizontal, reply.GetProperty("axis").GetString());
        // The reply IS the tree's split block after the swap.
        Assert.Equal(new[] { splitId, "s1" }, PaneIds(server));
        Assert.Equal(0, TreeSession(server).GetProperty("focusedPane").GetInt32());
        Assert.Equal(SplitAxes.Horizontal, TreeAxis(server));
        Assert.Equal(new[] { 0.7, 0.3 }, Ratios(server));      // the SEQUENCE is kept: the top box is still the big one
        Assert.Same(split, sess.Panes[0]); Assert.Same(primary, sess.Panes[1]);   // the shells moved; nothing was re-minted
        Assert.Equal(2, PaneCount(server));
    }

    [Fact]
    public void Swap_KeepsEveryId_AndEachStillReachesTheSameShell()
    {
        var (server, host) = New();
        string splitId = Id(Op(server, "on"));
        var sess = host.ActiveSess!;
        var bySessionId = host.Resolve("s1"); var bySplitId = host.Resolve(splitId);
        Assert.Same(sess.Panes[0], bySessionId); Assert.Same(sess.Panes[1], bySplitId);

        Swapped(Swap(server, null));

        Assert.Same(bySessionId, host.Resolve("s1"));          // the session id names the shell it always named — now in slot 1
        Assert.Same(sess.Panes[1], host.Resolve("s1"));
        Assert.Same(bySplitId, host.Resolve(splitId));
        Assert.Same(sess.Panes[0], host.Resolve(splitId));
        Assert.True(Resolves(server, "s1")); Assert.True(Resolves(server, splitId));
        Assert.Equal(new[] { splitId, "s1" }, sess.PaneIds);
        Assert.Equal("s1", TreeSession(server).GetProperty("id").GetString());   // the session's own id never moved
    }

    [Fact]
    public void Swap_FocusFollowsThePane()
    {
        var (server, host) = New();
        Id(Op(server, "on"));
        Assert.True(Ok(Focus(server, "primary")));             // focus pane 0, the session-id shell
        var focused = host.Resolve("active");
        Assert.Equal(0, host.ActiveSess!.FocusedPane);

        var reply = Swapped(Swap(server, null));

        Assert.Equal(1, reply.GetProperty("focusedPane").GetInt32());
        Assert.Same(focused, host.Resolve("active"));          // the same shell has the focus, on the other side
        Assert.Equal(1, TreeSession(server).GetProperty("focusedPane").GetInt32());
    }

    [Fact]
    public void Swap_LeavesEverythingElseWhereItWas_AndPerPaneStateTravelsWithItsPane()
    {
        var (server, host) = New();
        string splitId = Id(Op(server, "on"));
        var sess = host.ActiveSess!;
        // Session-wide state, set the way a caller sets it or the way the app keeps it.
        Assert.True(Ok(JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.context\",\"target\":\"s1\",\"args\":{\"context\":\"P4 swap fixture\"}}")).RootElement));
        sess.Flagged = true; sess.Overlay = true; sess.OverlaySize = 40; sess.Notifications = 3;
        sess.Panes[1].SetStatus(Agwinterm.Core.AgentStatus.Blocked);   // the split pane wins the aggregate
        // Per-pane state on the split pane: a pin and a captured slot, keyed by its id.
        Assert.True(Ok(JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.restore\",\"target\":" + JsonSerializer.Serialize(splitId) + ",\"args\":{\"command\":\"cargo watch\"}}")).RootElement));
        sess.Captured[splitId] = "ping -n 300 127.0.0.1";
        var before = TreeSession(server);
        Assert.Equal("blocked", before.GetProperty("status").GetString());
        long changedAt = before.GetProperty("statusChangedAt").GetInt64();
        Assert.NotEqual(0, changedAt);

        Swapped(Swap(server, null));

        var after = TreeSession(server);
        foreach (var key in new[] { "id", "name", "active", "status", "statusChangedAt", "overlay", "flagged", "notifications", "overlaySize", "context", "axis", "splitRatios", "paneCount" })
            Assert.Equal(before.GetProperty(key).GetRawText(), after.GetProperty(key).GetRawText());
        Assert.Equal(changedAt, after.GetProperty("statusChangedAt").GetInt64());
        Assert.Equal(new[] { splitId, "s1" }, Strings(after.GetProperty("paneIds")));
        // The pin and the slot are still on the split pane, read back under ITS id whichever slot it is in.
        Assert.Equal("cargo watch", after.GetProperty("restoreCommands").GetProperty(splitId).GetString());
        Assert.Equal("ping -n 300 127.0.0.1", after.GetProperty("capturedCommands").GetProperty(splitId).GetString());
        Assert.False(after.GetProperty("restoreCommands").TryGetProperty("s1", out _));
        Assert.Equal("P4 swap fixture", sess.Context);
        Assert.Empty(sess.CoverPanes);                          // no cover was made or moved
    }

    [Fact]
    public void Swap_Twice_IsTheIdentity()
    {
        var (server, host) = New();
        Id(Op(server, "on"));
        Assert.True(Ok(Resize(server, "{\"ratio\":0.65}")));
        var sess = host.ActiveSess!;
        var panes = sess.Panes.ToArray();
        var before = TreeSession(server).GetRawText();

        Swapped(Swap(server, null));
        Assert.NotEqual(before, TreeSession(server).GetRawText());
        Swapped(Swap(server, null));

        Assert.Equal(before, TreeSession(server).GetRawText());   // ids, order, ratios, focus: all as they were
        Assert.Equal(panes, sess.Panes);
    }

    [Fact]
    public void Swap_ThenSplitOff_KeepsSlotZero_TheFormerSplitShell_AndNamesItInTheReply()
    {
        // Correct per the rules, and worth pinning: after a swap the session-id shell is pane 1, and
        // `off` keeps pane 0 — so the survivor is the former split shell, the session id's pane is
        // destroyed, the session id then resolves to the survivor (the exact-session arm).
        var (server, host) = New();
        string splitId = Id(Op(server, "on"));
        var sess = host.ActiveSess!;
        var split = sess.Panes[1];
        Swapped(Swap(server, null));

        Assert.Equal(splitId, Id(Op(server, "off")));

        Assert.Equal(new[] { splitId }, sess.PaneIds);
        Assert.Same(split, sess.Panes[0]);
        Assert.Equal(1, PaneCount(server));
        Assert.Same(split, host.Resolve("s1"));
        Assert.Equal("s1", TreeSession(server).GetProperty("id").GetString());
    }

    [Fact]
    public void Swap_OnASinglePane_IsRefused_AndNothingChanges()
    {
        var (server, host) = New();
        var before = TreeSession(server).GetRawText();
        foreach (var target in new string?[] { null, "active", "s1", "session 1" })
        {
            var r = Swap(server, target);
            Assert.False(Ok(r), target ?? "<null>");
            Assert.Contains("'s1'", Error(r));
            Assert.Contains("session split on", Error(r));
            Assert.Contains("Nothing moved", Error(r));
        }
        Assert.Equal(before, TreeSession(server).GetRawText());
        Assert.Equal(new[] { "s1" }, host.ActiveSess!.PaneIds);
    }

    [Fact]
    public void Swap_ACover_IsRefused_AndTheSplitStands()
    {
        var (server, host) = New();
        string splitId = Id(Op(server, "on"));
        string coverId = host.ActiveSess!.AddCoverPane();
        var before = TreeSession(server).GetRawText();

        var r = Swap(server, coverId);

        Assert.False(Ok(r));
        Assert.Contains("'" + coverId + "'", Error(r));
        Assert.Contains("scratch/overlay/quick", Error(r));
        Assert.Equal(before, TreeSession(server).GetRawText());
        Assert.Equal(new[] { "s1", splitId }, host.ActiveSess!.PaneIds);
    }

    [Fact]
    public void Swap_UnknownTarget_IsRefused_AndNothingMoves()
    {
        var (server, host) = New();
        Id(Op(server, "on"));
        var before = TreeSession(server).GetRawText();
        var r = Swap(server, "ghost");
        Assert.False(Ok(r));
        Assert.Contains("'ghost'", Error(r));
        Assert.Equal(before, TreeSession(server).GetRawText());
    }

    [Fact]
    public void Swap_HonoursTarget_ByEitherPaneOrTheSessionId_OnANonActiveSession()
    {
        var (server, host) = New();
        string other = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.new\",\"args\":{\"name\":\"other\"}}"))
            .RootElement.GetProperty("result").GetString()!;
        string otherSplit = Id(Op(server, "on", target: other));
        server.Dispatch("{\"cmd\":\"session.select\",\"target\":\"s1\"}");
        string activeSplit = Id(Op(server, "on"));
        Assert.Equal("s1", host.ActiveSess!.Id);
        var activeBefore = TreeSession(server, 0).GetRawText();

        var bySplit = Swapped(Swap(server, otherSplit));       // the OTHER session's pane 1 id names that session
        Assert.Equal(other, bySplit.GetProperty("session").GetString());
        Assert.Equal(new[] { otherSplit, other }, Strings(bySplit.GetProperty("paneIds")));
        var bySession = Swapped(Swap(server, other));          // its session id — now carried by its pane 1 — names it too
        Assert.Equal(new[] { other, otherSplit }, Strings(bySession.GetProperty("paneIds")));
        var byName = Swapped(Swap(server, "other"));           // and its name
        Assert.Equal(new[] { otherSplit, other }, Strings(byName.GetProperty("paneIds")));

        Assert.Equal("s1", host.ActiveSess!.Id);               // focus did not move
        Assert.Equal(activeBefore, TreeSession(server, 0).GetRawText());   // the active session was not touched
        Assert.Equal(new[] { "s1", activeSplit }, PaneIds(server, 0));
    }

    [Fact]
    public void Swap_ThenSaveAndLoad_KeepsTheIdsInTheirNewOrder_WithNoDuplicate()
    {
        // The app's restore path is Win32 (restore-roundtrip.ps1's swap-killed cell pins it); this pins
        // the fake and the FORMAT: a session whose pane 0 does not carry the session id serializes and
        // deserializes with both ids verbatim, and the session id sits on exactly one pane.
        var (server, host) = New();
        string splitId = Id(Op(server, "on", axis: SplitAxes.Horizontal));
        Assert.True(Ok(Resize(server, "{\"ratio\":0.7}")));
        var sess = host.ActiveSess!;
        Swapped(Swap(server, null));

        var st = new AppState
        {
            Workspaces = { new WorkspaceState { Id = "w1", Name = "workspace 1", Sessions = { new SessionState
            {
                Id = sess.Id, Name = sess.Name, Active = sess.FocusedPane,
                Panes = sess.PaneIds.Select((id, i) => new PaneState { Id = id, Ratio = (float)sess.Ratios[i] }).ToList(),
                Axis = RestoreState.StoreAxis(sess.PaneCount, sess.Axis),
            } } } },
        };
        string json = RestoreState.Serialize(st);
        Assert.True(RestoreState.TryDeserialize(json, out var loaded));
        var s = loaded!.Workspaces[0].Sessions[0];
        Assert.Equal("s1", s.Id);
        Assert.Equal(new[] { splitId, "s1" }, s.Panes.Select(p => p.Id));   // the saved order, the saved ids
        Assert.Distinct(s.Panes.Select(p => p.Id));
        Assert.Single(s.Panes, p => p.Id == s.Id);                            // exactly one pane carries the session id — slot 1
        Assert.Equal(new[] { 0.7f, 0.3f }, s.Panes.Select(p => p.Ratio));
        Assert.Equal(0, s.Active);
        Assert.Equal(SplitAxes.Horizontal, RestoreState.LoadAxis(s.Axis));
        Assert.Equal(json, RestoreState.Serialize(loaded));
    }

    [Fact]
    public void SwapReply_Build_SpellsTheFourKeys()
    {
        string raw = SwapReply.Build("s1", new[] { "p2", "s1" }, 1, SplitAxes.Vertical);
        var o = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(new[] { "session", "paneIds", "focusedPane", "axis" }, o.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("s1", o.GetProperty("session").GetString());
        Assert.Equal(new[] { "p2", "s1" }, Strings(o.GetProperty("paneIds")));
        Assert.Equal(1, o.GetProperty("focusedPane").GetInt32());
        Assert.Equal("vertical", o.GetProperty("axis").GetString());
        Assert.Equal(raw, SwapReply.Build(new SwapResult("s1", new[] { "p2", "s1" }, 1, SplitAxes.Vertical)));
    }
}
