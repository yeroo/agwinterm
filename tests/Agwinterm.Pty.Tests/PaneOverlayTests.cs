using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// Pane-scoped overlays (P5): everything the fake host can prove about the contract, sectioned per
/// Overview item of the plan. The rule is the one on <see cref="ISessionHost.SessionOverlay"/>; the
/// words and every refusal are <see cref="OverlayPanes"/>'. Each refusal is asserted twice: the
/// reply, and the tree afterwards (nothing opened, nothing moved) — the world untouched.
/// </summary>
public class PaneOverlayTests
{
    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    /// <summary>Raw args JSON, so a non-string pane or a float size can be sent exactly as a caller would.</summary>
    private static JsonElement Overlay(ControlServer server, string argsJson, string? target = null)
    {
        var sb = new System.Text.StringBuilder("{\"cmd\":\"session.overlay\"");
        if (target is not null) sb.Append(",\"target\":").Append(JsonSerializer.Serialize(target));
        sb.Append(",\"args\":").Append(argsJson).Append('}');
        return JsonDocument.Parse(server.Dispatch(sb.ToString())).RootElement;
    }

    private static JsonElement Open(ControlServer server, string pane, string? target = null, string command = "cmd", string extra = "")
        => Overlay(server, "{\"action\":\"open\",\"command\":" + JsonSerializer.Serialize(command) + ",\"pane\":" + JsonSerializer.Serialize(pane) + extra + "}", target);

    private static JsonElement Act(ControlServer server, string action, string? pane, string extra = "", string? target = null)
        => Overlay(server, "{\"action\":\"" + action + "\"" + (pane is null ? "" : ",\"pane\":" + JsonSerializer.Serialize(pane)) + extra + "}", target);

    private static JsonElement Dispatch(ControlServer server, string cmd, string? target = null, string? argsJson = null)
    {
        var sb = new System.Text.StringBuilder("{\"cmd\":").Append(JsonSerializer.Serialize(cmd));
        if (target is not null) sb.Append(",\"target\":").Append(JsonSerializer.Serialize(target));
        if (argsJson is not null) sb.Append(",\"args\":").Append(argsJson);
        return JsonDocument.Parse(server.Dispatch(sb.Append('}').ToString())).RootElement;
    }

    private static bool Ok(JsonElement r) => r.GetProperty("ok").GetBoolean();
    private static string Error(JsonElement r) => r.GetProperty("error").GetString() ?? "";
    private static string Result(JsonElement r)
    {
        Assert.True(Ok(r), r.ToString());
        var res = r.GetProperty("result");
        Assert.Equal(JsonValueKind.String, res.ValueKind);
        return res.GetString()!;
    }
    /// <summary><c>copy</c> / <c>text</c> answer an OBJECT with <c>text</c> (agterm's result.text).</summary>
    private static string Text(JsonElement r)
    {
        Assert.True(Ok(r), r.ToString());
        var res = r.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, res.ValueKind);
        return res.GetProperty(OverlayPanes.TextKey).GetString()!;
    }

    private static JsonElement TreeSession(ControlServer server)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"tree\"}")).RootElement
            .GetProperty("result").GetProperty("workspaces")[0].GetProperty("sessions")[0];

    /// <summary>The tree's <c>paneOverlays</c>, or null when the key is absent (= none).</summary>
    private static string[]? PaneOverlays(ControlServer server)
        => TreeSession(server).TryGetProperty(OverlayPanes.TreeKey, out var v)
            ? v.EnumerateArray().Select(e => e.GetString()!).ToArray() : null;

    private static bool TreeOverlay(ControlServer server)
        => TreeSession(server).TryGetProperty("overlay", out var v) && v.GetBoolean();

    private static string[] PaneIds(ControlServer server)
        => TreeSession(server).GetProperty("paneIds").EnumerateArray().Select(e => e.GetString()!).ToArray();

    /// <summary>Split the active session; returns (left pane id, right pane id).</summary>
    private static (string left, string right) Split(ControlServer server)
    {
        Assert.True(Ok(Dispatch(server, "session.split", argsJson: "{\"op\":\"on\"}")));
        var ids = PaneIds(server);
        return (ids[0], ids[1]);
    }

    private static void Write(ControlServer server, string target, string text)
        => Assert.True(Ok(Dispatch(server, "session.write", target, "{\"text\":" + JsonSerializer.Serialize(text) + "}")));

    private static string SessionText(ControlServer server, string? target, string argsJson = "{}")
        => Result(Dispatch(server, "session.text", target, argsJson));

    // ---- section 1: the words ----

    [Fact]
    public void TryParse_AbsentIsSessionWide_LeftIsZero_RightIsOne()
    {
        Assert.True(OverlayPanes.TryParse(null, out int absent, out var e0));
        Assert.Equal(OverlayPanes.SessionWide, absent); Assert.Null(e0);
        Assert.True(OverlayPanes.TryParse("left", out int left, out _)); Assert.Equal(0, left);
        Assert.True(OverlayPanes.TryParse("right", out int right, out _)); Assert.Equal(1, right);
        Assert.Equal("left", OverlayPanes.Word(0)); Assert.Equal("right", OverlayPanes.Word(1));
    }

    [Theory]
    [InlineData("Left")]     // the wire spelling is case-sensitive
    [InlineData("top")]      // agterm's other axis word: left IS the top pane on a horizontal split
    [InlineData("primary")]  // session focus's word, not this flag's
    [InlineData("s1")]       // a pane id: refused, as #213 refuses one on --target
    [InlineData("")]
    public void TryParse_RefusesAnyOtherWord_NamingBothWordsAndTheAbsentForm(string raw)
    {
        Assert.False(OverlayPanes.TryParse(raw, out int index, out var refusal));
        Assert.Equal(OverlayPanes.SessionWide, index);
        Assert.Contains($"'{raw}'", refusal);
        Assert.Contains("left", refusal); Assert.Contains("right", refusal);
        Assert.Contains("omit --pane", refusal);
    }

    [Theory]
    [InlineData("\"Left\"")]
    [InlineData("\"p1\"")]
    [InlineData("\"\"")]
    [InlineData("1")]          // a non-string: quoted back as its raw JSON
    [InlineData("true")]
    public void Open_WithABadPaneWord_IsRefusedBeforeAnything_AndOpensNothing(string paneJson)
    {
        var (server, host) = New();
        Split(server);
        var r = Overlay(server, "{\"action\":\"open\",\"command\":\"cmd\",\"pane\":" + paneJson + "}");
        Assert.False(Ok(r));
        Assert.Contains("left", Error(r)); Assert.Contains("right", Error(r));
        Assert.Contains(paneJson.Trim('"').Length == 0 ? "''" : paneJson.Trim('"'), Error(r));
        Assert.Null(PaneOverlays(server));
        Assert.False(TreeOverlay(server));
        Assert.Empty(host.ActiveSess!.CoverPanes);
    }

    // ---- section 2: open --pane on a split ----

    [Fact]
    public void Open_PaneRight_RepliesThePaneOverlayId_AndTreeReadsItBack()
    {
        var (server, host) = New();
        var (_, right) = Split(server);
        string id = Result(Open(server, "right"));
        Assert.StartsWith(right + ":overlay:", id);       // the owner is readable off the id
        Assert.Equal(new[] { "right" }, PaneOverlays(server));
        Assert.False(TreeOverlay(server));                 // independent of the session-wide flag
        Assert.NotNull(host.Resolve(id));                  // --target <overlay id> reaches the overlay
    }

    [Fact]
    public void Open_BothPanes_Independently_TreeListsBothInPaneOrder()
    {
        var (server, host) = New();
        var (left, right) = Split(server);
        string rightId = Result(Open(server, "right"));
        Assert.Equal(new[] { "right" }, PaneOverlays(server));
        string leftId = Result(Open(server, "left"));
        Assert.Equal(new[] { "left", "right" }, PaneOverlays(server));
        Assert.StartsWith(left + ":overlay:", leftId);
        Assert.StartsWith(right + ":overlay:", rightId);
        Assert.NotSame(host.Resolve(leftId), host.Resolve(rightId));
        // Closing one leaves the other.
        Assert.Equal("closed", Result(Act(server, "close", "right")));
        Assert.Equal(new[] { "left" }, PaneOverlays(server));
        Assert.NotNull(host.Resolve(leftId));
        Assert.Null(host.Resolve(rightId));
    }

    [Fact]
    public void Open_OnAHeldSlot_IsRefused_AndTheFirstIsStillThere()
    {
        var (server, host) = New();
        Split(server);
        string first = Result(Open(server, "right"));
        var r = Open(server, "right", command: "other");
        Assert.False(Ok(r));
        Assert.StartsWith(OverlayPanes.AlreadyOpen, Error(r));
        Assert.Contains("close --pane right", Error(r));
        Assert.Equal(new[] { "right" }, PaneOverlays(server));
        Assert.Same(host.Resolve(first), host.ActiveSess!.CoverPanes.Single().Pane);   // the same term, not a replacement
    }

    // ---- section 3: a single pane ----

    [Fact]
    public void SinglePane_PaneLeft_IsOk_PaneRight_IsPaneNotVisible()
    {
        var (server, host) = New();
        var r = Open(server, "right");
        Assert.False(Ok(r));
        Assert.StartsWith(OverlayPanes.NotVisible + ":", Error(r));
        Assert.Contains("one pane", Error(r)); Assert.Contains("--pane left", Error(r));
        Assert.Null(PaneOverlays(server));
        Assert.Empty(host.ActiveSess!.CoverPanes);

        string id = Result(Open(server, "left"));
        Assert.StartsWith("s1:overlay:", id);
        Assert.Equal(new[] { "left" }, PaneOverlays(server));   // stands alone: no paneCount, no paneIds
        Assert.False(TreeSession(server).TryGetProperty("paneCount", out _));
    }

    // ---- section 4: always full-pane ----

    [Theory]
    [InlineData("40")]     // a valid size: still refused — the flag cannot mean anything with a pane
    [InlineData("150")]    // an invalid one: the combination is refused, not the value
    [InlineData("\"x\"")]
    public void Open_PaneWithSizePercent_IsRefusedAtTheServer_AndOpensNothing(string sizeJson)
    {
        var (server, host) = New();
        Split(server);
        var r = Open(server, "left", extra: ",\"size-percent\":" + sizeJson);
        Assert.False(Ok(r));
        Assert.Equal(OverlayPanes.SizeWithPaneRefusal, Error(r));
        Assert.Null(PaneOverlays(server));
        Assert.Empty(host.ActiveSess!.CoverPanes);
    }

    [Fact]
    public void Resize_WithPane_IsRefused_AndTheSessionWideSizeIsUntouched()
    {
        var (server, host) = New();
        Split(server);
        Assert.True(Ok(Overlay(server, "{\"action\":\"open\",\"command\":\"cmd\",\"size-percent\":30}")));
        Result(Open(server, "left"));
        // A resize always carries a size: the refusal still names the verb typed, not the size beside it.
        var r = Act(server, "resize", "left", extra: ",\"size-percent\":40");
        Assert.False(Ok(r));
        Assert.Equal(OverlayPanes.ResizeWithPaneRefusal, Error(r));
        Assert.Equal(OverlayPanes.ResizeWithPaneRefusal, Error(Act(server, "resize", "left")));
        Assert.Equal(30, host.ActiveSess!.OverlaySize);
        Assert.Equal(30, TreeSession(server).GetProperty("overlaySize").GetInt32());
    }

    [Fact]
    public void Host_RefusesSizeAndResizeWithPane_Too()
    {
        // Both ends: a raw caller of the host (the fake's tripwire, the app's task-4 guard) gets the
        // same words the server gives.
        var (_, host) = New();
        host.Split(null, "on", null);
        Assert.Equal(ISessionHost.RefusePrefix + OverlayPanes.SizeWithPaneRefusal,
            host.SessionOverlay(null, "open", "cmd", 40, false, false, "left", OverlayTextArgs.Screen));
        Assert.Equal(ISessionHost.RefusePrefix + OverlayPanes.ResizeWithPaneRefusal,
            host.SessionOverlay(null, "resize", null, 40, false, false, "left", OverlayTextArgs.Screen));
        Assert.Empty(host.ActiveSess!.CoverPanes);
    }

    // ---- section 5: close ----

    [Fact]
    public void Close_PaneWithNothingOpen_IsOkNoOverlay_TheSessionWideShape()
    {
        var (server, _) = New();
        Split(server);
        var r = Act(server, "close", "left");
        Assert.Equal("no overlay", Result(r));
        Assert.Equal("no overlay", Result(Act(server, "close", "right")));
        Assert.Equal("no overlay", Result(Act(server, "close", null)));   // unchanged
    }

    [Fact]
    public void Close_Pane_DropsTheSlot_AndTheIdResolvesNowhere()
    {
        var (server, host) = New();
        Split(server);
        string id = Result(Open(server, "left"));
        Assert.Equal("closed", Result(Act(server, "close", "left")));
        Assert.Null(PaneOverlays(server));
        Assert.Null(host.Resolve(id));
        Assert.Equal("no session", Error(Dispatch(server, "session.text", id)));
        Assert.Equal("no overlay", Result(Act(server, "close", "left")));   // idempotent
    }

    [Fact]
    public void Close_OnATargetThatMatchesNoSession_IsRefused_BareCloseIsNot()
    {
        var (server, _) = New();
        var r = Act(server, "close", "left", target: "no-such-session");
        Assert.False(Ok(r));
        Assert.Contains("no session", Error(r));
        Assert.Equal("no overlay", Result(Act(server, "close", "left", target: "active")));
    }

    // ---- section 6: result --pane, three states ----

    [Fact]
    public void Result_Pane_NoResult_ThenStillRunning_ThenExitN()
    {
        var (server, host) = New();
        Split(server);
        var r = Act(server, "result", "right");
        Assert.False(Ok(r)); Assert.Equal(OverlayPanes.NoResult, Error(r));

        Result(Open(server, "right"));
        r = Act(server, "result", "right");
        Assert.False(Ok(r)); Assert.Equal(OverlayPanes.StillRunning, Error(r));

        host.ExitPaneOverlay(host.ActiveSess!, 1, 3);
        Assert.Equal("exit 3", Result(Act(server, "result", "right")));
        Assert.Null(PaneOverlays(server));                     // without --wait the exit closes the slot
        // The other slot is its own: nothing ran there.
        r = Act(server, "result", "left");
        Assert.False(Ok(r)); Assert.Equal(OverlayPanes.NoResult, Error(r));
        // A new open resets the slot's result, as the session-wide open resets the window-wide one.
        Result(Open(server, "right"));
        Assert.Equal(OverlayPanes.StillRunning, Error(Act(server, "result", "right")));
        Assert.Equal("closed", Result(Act(server, "close", "right")));
        Assert.Equal(OverlayPanes.NoResult, Error(Act(server, "result", "right")));
    }

    [Fact]
    public void Result_Pane_WithWait_AnswersTheExitWhileTheTermIsStillUp()
    {
        var (server, host) = New();
        Split(server);
        string id = Result(Open(server, "right", extra: ",\"wait\":true"));
        host.ExitPaneOverlay(host.ActiveSess!, 1, 0);
        Assert.Equal("exit 0", Result(Act(server, "result", "right")));
        Assert.Equal(new[] { "right" }, PaneOverlays(server));   // the banner: still up until a key or a close
        Assert.NotNull(host.Resolve(id));
        Assert.Equal("closed", Result(Act(server, "close", "right")));
        Assert.Null(PaneOverlays(server));
    }

    [Fact]
    public void Result_SessionWide_IsUnchanged_WindowWideAndOk()
    {
        // The recorded divergence: the session-wide arm keeps answering the fake's shape (no arm —
        // the "needs a command" refusal here; the app's window-wide last exit), untouched by P5.
        var (server, _) = New();
        Split(server);
        Result(Open(server, "left"));
        var r = Act(server, "result", null);
        Assert.False(Ok(r));
        Assert.Contains("command", Error(r));
    }

    // ---- section 7: copy and text ----

    [Fact]
    public void Copy_And_Text_WithNothingOpen_AreRefused_NamingTheSlot()
    {
        var (server, _) = New();
        Split(server);
        foreach (var action in new[] { "copy", "text" })
        {
            var r = Act(server, action, "left");
            Assert.False(Ok(r));
            Assert.Equal(OverlayPanes.NoOverlayRefusal(0), Error(r));
            Assert.StartsWith(OverlayPanes.NoOverlay + ":", Error(r));
            Assert.Contains("--pane left", Error(r));
            // the session-wide slot: the bare phrase
            r = Act(server, action, null);
            Assert.False(Ok(r));
            Assert.Equal(OverlayPanes.NoOverlay, Error(r));
        }
    }

    [Fact]
    public void Text_ReadsTheOverlaysBuffer_NotThePaneUnderneath()
    {
        var (server, _) = New();
        var (left, right) = Split(server);
        string id = Result(Open(server, "right"));
        Write(server, id, "MARKER-IN-OVERLAY\r\n");
        Write(server, right, "shell under it\r\n");
        Write(server, left, "the other shell\r\n");

        Assert.Contains("MARKER-IN-OVERLAY", Text(Act(server, "text", "right")));
        Assert.DoesNotContain("shell under it", Text(Act(server, "text", "right")));
        // --target <pane id> reaches the shell underneath; --target <overlay id> the overlay.
        Assert.Contains("shell under it", SessionText(server, right));
        Assert.DoesNotContain("MARKER-IN-OVERLAY", SessionText(server, right));
        Assert.Contains("the other shell", SessionText(server, left));
        Assert.Contains("MARKER-IN-OVERLAY", SessionText(server, id));
        // `session copy` keeps reading the pane underneath (nothing selected there).
        Assert.Equal("", Result(Dispatch(server, "session.copy", right)));
    }

    [Fact]
    public void Copy_NoSelection_ThenSelectionAllOnTheOverlay_ReturnsItsText()
    {
        var (server, host) = New();
        Split(server);
        string id = Result(Open(server, "right"));
        Write(server, id, "select me\r\n");
        var r = Act(server, "copy", "right");
        Assert.False(Ok(r)); Assert.Equal(OverlayPanes.NoSelection, Error(r));

        Assert.Equal("selected", Result(Dispatch(server, "selection.all", id)));
        Assert.Contains("select me", Text(Act(server, "copy", "right")));
        Assert.Equal("", Result(Dispatch(server, "session.copy", "active")));   // the pane underneath: untouched
        Assert.Equal(OverlayPanes.NoOverlayRefusal(0), Error(Act(server, "copy", "left")));   // the other slot: empty, not "no selection"
        // Clearing the overlay's selection puts copy back to its refusal.
        Assert.True(Ok(Dispatch(server, "selection.clear", id)));
        Assert.Equal(OverlayPanes.NoSelection, Error(Act(server, "copy", "right")));
        Assert.Empty(host.ActiveSess!.Selections);
    }

    [Fact]
    public void Text_All_ReadsScreenAndHistory_Lines_TheLastN_ThePair_IsRefused_OnBothVerbs()
    {
        var (server, host) = New();
        Split(server);
        string id = Result(Open(server, "right"));
        var term = host.Resolve(id)!;
        for (int i = 1; i <= 30; i++) term.Inject(System.Text.Encoding.UTF8.GetBytes($"L{i}\r\n"));   // 24 rows: L1..L6 scroll into history

        string screen = Text(Act(server, "text", "right"));
        Assert.DoesNotContain("L1\n", screen + "\n"); Assert.Contains("L30", screen);
        string all = Text(Act(server, "text", "right", ",\"all\":true"));
        Assert.StartsWith("L1\n", all); Assert.Contains("L30", all);
        string last3 = Text(Act(server, "text", "right", ",\"lines\":3"));   // the last 3 rows: L29, L30 and the blank row after them (trimmed)
        Assert.Contains("L29", last3); Assert.Contains("L30", last3); Assert.DoesNotContain("L28", last3);

        foreach (var pair in new[] { ",\"all\":true,\"lines\":3", ",\"all\":true,\"lines\":0" })
        {
            var r = Act(server, "text", "right", pair);
            Assert.False(Ok(r)); Assert.Equal(OverlayPanes.AllWithLines, Error(r));
            var t = Dispatch(server, "session.text", id, "{" + pair[1..] + "}");
            Assert.False(Ok(t)); Assert.Equal(OverlayPanes.AllWithLines, Error(t));
        }
        // session text --all: the same reader, on the pane under the overlay too.
        Assert.StartsWith("L1\n", SessionText(server, id, "{\"all\":true}"));
    }

    /// <summary>The pin task 4 asks for: no target / "active" is the focused pane's SURFACE — the
    /// overlay while that pane's slot is open (the rule a cover follows today), the pane's own id the
    /// shell underneath, and the pane again once the slot closes.</summary>
    [Fact]
    public void SessionText_NoTarget_ReadsTheFocusedPanesOverlay_ItsPaneId_TheShellUnderneath()
    {
        var (server, _) = New();
        var (left, right) = Split(server);   // the split focuses the new pane: right
        Assert.Equal(1, TreeSession(server).GetProperty("focusedPane").GetInt32());
        Write(server, right, "shell under it\r\n");
        Assert.Contains("shell under it", SessionText(server, null));

        string id = Result(Open(server, "right"));
        Write(server, id, "MARKER-IN-OVERLAY\r\n");
        foreach (var active in new string?[] { null, "active" })
        {
            Assert.Contains("MARKER-IN-OVERLAY", SessionText(server, active));
            Assert.DoesNotContain("shell under it", SessionText(server, active));
        }
        Assert.Contains("shell under it", SessionText(server, right));          // the pane id: the shell underneath
        Assert.DoesNotContain("MARKER-IN-OVERLAY", SessionText(server, right));
        Assert.DoesNotContain("MARKER-IN-OVERLAY", SessionText(server, left));

        // The OTHER pane's overlay is not the focused surface.
        string leftId = Result(Open(server, "left"));
        Write(server, leftId, "LEFT-OVERLAY\r\n");
        Assert.DoesNotContain("LEFT-OVERLAY", SessionText(server, null));
        Assert.Contains("MARKER-IN-OVERLAY", SessionText(server, null));

        // Closed: the pane is the surface again.
        Assert.Equal("closed", Result(Act(server, "close", "right")));
        Assert.Contains("shell under it", SessionText(server, null));
        Assert.DoesNotContain("MARKER-IN-OVERLAY", SessionText(server, null));
    }

    [Fact]
    public void Copy_And_Text_OnTheSessionWideSlot_HaveNoTermInTheFake_AndSayNotRealized()
    {
        var (server, _) = New();
        Assert.True(Ok(Overlay(server, "{\"action\":\"open\",\"command\":\"cmd\"}")));
        foreach (var action in new[] { "copy", "text" })
        {
            var r = Act(server, action, null);
            Assert.False(Ok(r));
            Assert.StartsWith(OverlayPanes.NotRealized + ":", Error(r));
        }
    }

    // ---- section 8: the two slot kinds coexist ----

    [Fact]
    public void SessionWide_And_Pane_Coexist_InTheTree_AndCloseIndependently()
    {
        var (server, host) = New();
        Split(server);
        string paneId = Result(Open(server, "left"));
        Assert.True(Ok(Overlay(server, "{\"action\":\"open\",\"command\":\"cmd\",\"size-percent\":40}")));
        var s = TreeSession(server);
        Assert.True(s.GetProperty("overlay").GetBoolean());
        Assert.Equal(40, s.GetProperty("overlaySize").GetInt32());
        Assert.Equal(new[] { "left" }, PaneOverlays(server));

        Assert.Equal("closed", Result(Act(server, "close", null)));    // the session-wide one
        Assert.False(TreeOverlay(server));
        Assert.Equal(new[] { "left" }, PaneOverlays(server));          // the pane one is still running
        Assert.NotNull(host.Resolve(paneId));
        Assert.Equal("closed", Result(Act(server, "close", "left")));
        Assert.Null(PaneOverlays(server));
    }

    // ---- section 9: the target and the pane must agree ----

    [Fact]
    public void Open_TargetNamingTheOtherSide_IsRefused_AndOpensNothing()
    {
        var (server, host) = New();
        var (left, right) = Split(server);
        var r = Open(server, "left", target: right);
        Assert.False(Ok(r));
        Assert.Equal(OverlayPanes.Disagree(right, 1, 0), Error(r));
        Assert.Contains("is the right pane; --pane left names the other one", Error(r));
        Assert.Null(PaneOverlays(server));
        Assert.Empty(host.ActiveSess!.CoverPanes);
        // The session id, a name, the same side, and a prefix of the same side all agree.
        Assert.StartsWith(right + ":overlay:", Result(Open(server, "right", target: "s1")));
        Assert.Equal("closed", Result(Act(server, "close", "right", target: "session 1")));
        Assert.StartsWith(right + ":overlay:", Result(Open(server, "right", target: right)));
        Assert.Equal("closed", Result(Act(server, "close", "right", target: right[..4])));
        Assert.StartsWith(left + ":overlay:", Result(Open(server, "left", target: left)));
    }

    // ---- the overlay's own id on --target (revmux r1 Major) ----

    /// <summary>The skill's <c>--wait</c> recipe closes with <c>--target &lt;overlay id&gt;</c> and no
    /// <c>--pane</c>: that id names ITS slot, not the session-wide one. Before the fix the session-wide
    /// arm answered "no overlay" (or closed the session-wide overlay) while the pane overlay ran on.</summary>
    [Fact]
    public void OverlayIdTarget_WithoutPane_NamesThatSlot_ForCloseTextResult_TheSessionWideOneUntouched()
    {
        var (server, host) = New();
        var (left, right) = Split(server);
        Assert.True(Ok(Overlay(server, "{\"action\":\"open\",\"command\":\"cmd\",\"size-percent\":25}")));
        string id = Result(Open(server, "right"));
        Write(server, id, "MARKER-IN-OVERLAY\r\n");

        // text / result / open on the overlay's id, the pane word omitted: the right slot.
        Assert.Contains("MARKER-IN-OVERLAY", Text(Act(server, "text", null, target: id)));
        Assert.Equal(OverlayPanes.StillRunning, Error(Act(server, "result", null, target: id)));
        Assert.StartsWith(OverlayPanes.AlreadyOpen, Error(Overlay(server, "{\"action\":\"open\",\"command\":\"cmd\"}", id)));
        Assert.Equal(new[] { "right" }, PaneOverlays(server));
        // A prefix of the overlay id, as any id on --target.
        Assert.Contains("MARKER-IN-OVERLAY", Text(Act(server, "text", null, target: id[..(right.Length + 12)])));

        Assert.Equal("closed", Result(Act(server, "close", null, target: id)));
        Assert.Null(PaneOverlays(server));
        Assert.Null(host.Resolve(id));
        Assert.True(TreeOverlay(server));                                  // the session-wide slot is still up
        Assert.Equal(25, TreeSession(server).GetProperty("overlaySize").GetInt32());
        // Closed, the id resolves nowhere: the session-wide arm again, as for any unknown target.
        Assert.False(Ok(Act(server, "close", null, target: id)));
        // The slot's result survives the close under its pane word, and --wait keeps the id alive.
        string id2 = Result(Open(server, "right", extra: ",\"wait\":true"));
        host.ExitPaneOverlay(host.ActiveSess!, 1, 4);
        Assert.Equal("exit 4", Result(Act(server, "result", null, target: id2)));
        Assert.Equal("closed", Result(Act(server, "close", null, target: id2)));
        Assert.Equal("exit 4", Result(Act(server, "result", "right")));
    }

    [Fact]
    public void OverlayIdTarget_WithPane_SameSideAgrees_OtherSideIsRefused_NamingTheOverlay()
    {
        var (server, host) = New();
        var (left, right) = Split(server);
        string rightId = Result(Open(server, "right"));
        string leftId = Result(Open(server, "left"));
        Write(server, rightId, "RIGHT-OVL\r\n");
        Write(server, leftId, "LEFT-OVL\r\n");

        // Naming the other side's overlay is naming two panes: refused for every verb, nothing moves.
        foreach (var action in new[] { "close", "text", "result", "copy" })
        {
            var r = Act(server, action, "left", target: rightId);
            Assert.False(Ok(r));
            Assert.Equal(OverlayPanes.Disagree(rightId, 1, 0, overlay: true), Error(r));
            Assert.Contains("is the right pane's overlay; --pane left names the other one", Error(r));
        }
        var o = Open(server, "left", target: rightId);
        Assert.False(Ok(o)); Assert.Equal(OverlayPanes.Disagree(rightId, 1, 0, overlay: true), Error(o));
        Assert.Equal(new[] { "left", "right" }, PaneOverlays(server));
        Assert.NotNull(host.Resolve(rightId)); Assert.NotNull(host.Resolve(leftId));
        // The same side agrees, and reads that overlay — not the other one.
        Assert.Contains("RIGHT-OVL", Text(Act(server, "text", "right", target: rightId)));
        Assert.DoesNotContain("LEFT-OVL", Text(Act(server, "text", "right", target: rightId)));
        Assert.Equal("closed", Result(Act(server, "close", "right", target: rightId)));
        Assert.Equal(new[] { "left" }, PaneOverlays(server));
        Assert.NotNull(host.Resolve(leftId));
        // Before the fix: `close --pane left --target <right overlay id>` closed the LEFT one.
        Assert.Equal("closed", Result(Act(server, "close", "left", target: leftId)));
        Assert.Null(PaneOverlays(server));
    }

    [Fact]
    public void OverlayIdTarget_ResizeAndSizePercent_AreRefused_NamingTheOverlay()
    {
        var (server, host) = New();
        var (_, right) = Split(server);
        Assert.True(Ok(Overlay(server, "{\"action\":\"open\",\"command\":\"cmd\",\"size-percent\":25}")));
        string id = Result(Open(server, "right"));
        var r = Overlay(server, "{\"action\":\"resize\",\"size-percent\":40}", id);
        Assert.False(Ok(r)); Assert.Equal(OverlayPanes.OverlayIdResizeRefusal(id, 1), Error(r));
        Assert.EndsWith("Nothing resized.", Error(r));
        r = Overlay(server, "{\"action\":\"open\",\"command\":\"cmd\",\"size-percent\":40}", id);
        Assert.False(Ok(r)); Assert.Equal(OverlayPanes.OverlayIdSizeRefusal(id, 1), Error(r));
        Assert.EndsWith("Nothing opened.", Error(r));
        Assert.Equal(25, TreeSession(server).GetProperty("overlaySize").GetInt32());   // the session-wide size untouched
        Assert.Equal(new[] { "right" }, PaneOverlays(server));
        Assert.NotNull(host.Resolve(id));
        // The host too (the fake's tripwire for the app's arm).
        Assert.Equal(ISessionHost.RefusePrefix + OverlayPanes.OverlayIdResizeRefusal(id, 1),
            host.SessionOverlay(id, "resize", null, 40, false, false, null, OverlayTextArgs.Screen));
        Assert.Equal(ISessionHost.RefusePrefix + OverlayPanes.OverlayIdSizeRefusal(id, 1),
            host.SessionOverlay(id, "open", "cmd", 40, false, false, null, OverlayTextArgs.Screen));
    }

    /// <summary>The fake checks a size with a pane BEFORE the pane count, as the app does: on a
    /// single-pane session <c>open --pane right --size-percent 40</c> is the size refusal at both ends
    /// (revmux r1: the fake answered "pane not visible" here and the size refusal in the app).</summary>
    [Fact]
    public void SinglePane_SizeWithPaneRight_IsTheSizeRefusal_AtBothEnds()
    {
        var (server, host) = New();
        Assert.Equal(OverlayPanes.SizeWithPaneRefusal, Error(Open(server, "right", extra: ",\"size-percent\":40")));
        Assert.Equal(ISessionHost.RefusePrefix + OverlayPanes.SizeWithPaneRefusal,
            host.SessionOverlay(null, "open", "cmd", 40, false, false, "right", OverlayTextArgs.Screen));
        Assert.Equal(ISessionHost.RefusePrefix + OverlayPanes.ResizeWithPaneRefusal,
            host.SessionOverlay(null, "resize", null, 40, false, false, "right", OverlayTextArgs.Screen));
        Assert.Null(PaneOverlays(server));
        Assert.Empty(host.ActiveSess!.CoverPanes);
    }

    // ---- section 10: the slot moves with its pane, and dies with it ----

    [Fact]
    public void Swap_MovesThePaneSlot_AndKeepsTheSessionWideOne()
    {
        var (server, host) = New();
        var (left, right) = Split(server);
        string id = Result(Open(server, "right"));
        var term = host.Resolve(id);
        Assert.True(Ok(Overlay(server, "{\"action\":\"open\",\"command\":\"cmd\",\"size-percent\":25}")));

        Assert.True(Ok(Dispatch(server, "session.swap")));
        Assert.Equal(new[] { right, left }, PaneIds(server));
        Assert.Equal(new[] { "left" }, PaneOverlays(server));         // the slot went with its pane to the other side
        Assert.Same(term, host.Resolve(id));                            // the same term, the same id
        Assert.True(TreeOverlay(server));
        Assert.Equal(25, TreeSession(server).GetProperty("overlaySize").GetInt32());
        // It is now the LEFT slot for every verb.
        Assert.StartsWith(OverlayPanes.AlreadyOpen, Error(Open(server, "left")));
        Assert.Equal("no overlay", Result(Act(server, "close", "right")));
        Assert.Equal("closed", Result(Act(server, "close", "left")));
        Assert.Null(host.Resolve(id));
    }

    [Fact]
    public void SplitClose_OfThePaneWithTheOverlay_DropsIt_TheSurvivorKeepsItsOwn()
    {
        var (server, host) = New();
        var (left, right) = Split(server);
        string leftId = Result(Open(server, "left"));
        string rightId = Result(Open(server, "right"));
        Assert.Equal(new[] { "left", "right" }, PaneOverlays(server));

        Assert.Equal(left, Result(Dispatch(server, "session.split.close", right)));
        Assert.Equal(new[] { "left" }, PaneOverlays(server));
        Assert.Null(host.Resolve(rightId));                             // gone with its pane
        Assert.NotNull(host.Resolve(leftId));                           // the survivor's, intact
        Assert.Single(host.ActiveSess!.CoverPanes);
        Assert.StartsWith(OverlayPanes.NotVisible + ":", Error(Act(server, "result", "right")));   // one pane now: the slot is not a thing
    }

    [Fact]
    public void SplitClose_OfTheOtherPane_TheSlotSurvivesAndBecomesLeft()
    {
        var (server, host) = New();
        var (left, right) = Split(server);
        string id = Result(Open(server, "right"));
        Assert.Equal(right, Result(Dispatch(server, "session.split.close", left)));
        Assert.Equal(new[] { "left" }, PaneOverlays(server));          // pane 1 became pane 0: the slot is "left" now
        Assert.NotNull(host.Resolve(id));
        Assert.Contains(id, host.ActiveSess!.CoverPanes.Select(c => c.Id));
    }

    [Fact]
    public void SplitOff_DropsTheSplitPanesOverlay_AndKeepsPaneZeros()
    {
        var (server, host) = New();
        Split(server);
        string leftId = Result(Open(server, "left"));
        string rightId = Result(Open(server, "right"));
        Assert.True(Ok(Dispatch(server, "session.split", argsJson: "{\"op\":\"off\"}")));
        Assert.Equal(new[] { "left" }, PaneOverlays(server));
        Assert.Null(host.Resolve(rightId));
        Assert.NotNull(host.Resolve(leftId));
    }

    // ---- section 11: a host with no panes ----

    [Fact]
    public void SingleSessionHost_RefusesEverythingWithAPane()
    {
        var host = new SingleSessionHost(new TerminalSession(80, 24));
        foreach (var action in new[] { "open", "close", "result", "copy", "text" })
        {
            string r = host.SessionOverlay(null, action, "cmd", 0, false, false, "left", OverlayTextArgs.Screen);
            Assert.StartsWith(ISessionHost.RefusePrefix + OverlayPanes.NotVisible, r);
        }
        Assert.Equal("no overlay", host.SessionOverlay(null, "close", null, 0, false, false, null, OverlayTextArgs.Screen));   // unchanged
        Assert.StartsWith(ISessionHost.RefusePrefix + OverlayPanes.NoOverlay, host.SessionOverlay(null, "text", null, 0, false, false, null, OverlayTextArgs.Screen));
    }

    // ---- section 12: the reply shapes ----

    [Fact]
    public void TextReply_IsAnObjectWithText()
    {
        Assert.Equal("{\"text\":\"a\\nb\"}", OverlayPanes.TextReply("a\nb"));
        Assert.Equal("{\"text\":\"\"}", OverlayPanes.TextReply(""));
    }

    [Fact]
    public void EveryRefusal_StartsWithAgtermsPhrase()
    {
        Assert.StartsWith("pane not visible: ", OverlayPanes.NotVisibleRefusal("s1"));
        Assert.StartsWith("pane overlay already open: ", OverlayPanes.AlreadyOpenRefusal(1));
        Assert.StartsWith("no overlay: ", OverlayPanes.NoOverlayRefusal(0));
        Assert.Equal("no overlay", OverlayPanes.NoOverlayRefusal(OverlayPanes.SessionWide));
        Assert.StartsWith("overlay not realized: ", OverlayPanes.NotRealizedRefusal("x"));
        Assert.Equal("no selection", OverlayPanes.NoSelection);
        Assert.StartsWith("failed to read surface buffer: ", OverlayPanes.ReadFailedRefusal("boom"));
        Assert.Equal("overlay still running", OverlayPanes.StillRunning);
        Assert.Equal("no overlay result", OverlayPanes.NoResult);
    }
}
