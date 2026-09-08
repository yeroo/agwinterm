using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>End-to-end control-API contract tests: JSON command in → JSON response out, driving a realistic
/// in-memory <see cref="FakeSessionHost"/>. This is the regression net for the control surface that the
/// Win32 UI otherwise only exercises by hand. Covers the tree/window read-back, session &amp; workspace
/// lifecycle, config, overlay, toggles, and error handling.</summary>
public class ControlApiTests
{
    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    // `target` is a top-level field in the command envelope (not inside args); size-percent etc. are hyphenated
    // arg keys, so args that need them are passed as dictionaries.
    private static JsonElement Dispatch(ControlServer server, string cmd, object? args = null, string? target = null)
    {
        var sb = new System.Text.StringBuilder("{\"cmd\":").Append(JsonSerializer.Serialize(cmd));
        if (target is not null) sb.Append(",\"target\":").Append(JsonSerializer.Serialize(target));
        if (args is not null) sb.Append(",\"args\":").Append(JsonSerializer.Serialize(args));
        sb.Append('}');
        return JsonDocument.Parse(server.Dispatch(sb.ToString())).RootElement;
    }

    private static Dictionary<string, object> Overlay(string action, int sizePercent, string? command = null)
    {
        var d = new Dictionary<string, object> { ["action"] = action, ["size-percent"] = sizePercent };
        if (command is not null) d["command"] = command;
        return d;
    }

    private static bool Ok(JsonElement r) => r.TryGetProperty("ok", out var o) && o.GetBoolean();
    private static string Result(JsonElement r) => r.GetProperty("result").GetString() ?? "";
    // tree / window.state return raw JSON nested under "result".
    private static JsonElement Tree(ControlServer s) => Dispatch(s, "tree").GetProperty("result");
    private static JsonElement Sessions0(ControlServer s) => Tree(s).GetProperty("workspaces")[0].GetProperty("sessions")[0];
    private static JsonElement WinState(ControlServer s) => Dispatch(s, "window.state").GetProperty("result");

    // ---- tree read-back ----

    [Fact]
    public void Tree_ReportsWorkspacesAndSessions()
    {
        var (server, _) = New();
        var ws = Tree(server).GetProperty("workspaces");
        Assert.Equal(1, ws.GetArrayLength());
        Assert.Equal("workspace 1", ws[0].GetProperty("name").GetString());
        Assert.True(ws[0].GetProperty("active").GetBoolean());
        Assert.Equal("session 1", ws[0].GetProperty("sessions")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Tree_ExposesSplitReadBack_AfterSplit()
    {
        var (server, host) = New();
        host.Split(null, "on", null);   // 2 panes, 0.5/0.5
        host.FocusPaneDir("right");
        var s = Sessions0(server);
        Assert.Equal(2, s.GetProperty("paneCount").GetInt32());
        Assert.Equal(1, s.GetProperty("focusedPane").GetInt32());
        var ratios = s.GetProperty("splitRatios");
        Assert.Equal(2, ratios.GetArrayLength());
        Assert.Equal(0.5, ratios[0].GetDouble(), 3);
    }

    [Fact]
    public void Tree_OmitsSplitFields_ForSinglePane()
    {
        var (server, _) = New();
        var s = Sessions0(server);
        Assert.False(s.TryGetProperty("paneCount", out _));   // single-pane sessions stay lean
        Assert.False(s.TryGetProperty("splitRatios", out _));
        Assert.False(s.TryGetProperty("axis", out _));        // P4: the axis is a fact about a split, not about a session
    }

    [Fact]
    public void Tree_ExposesOverlaySize_WhenOverlayOpen()
    {
        var (server, _) = New();
        Dispatch(server, "session.overlay", Overlay("open", 40, "cmd"));
        var s = Sessions0(server);
        Assert.True(s.GetProperty("overlay").GetBoolean());
        Assert.Equal(40, s.GetProperty("overlaySize").GetInt32());
    }

    // ---- window.state ----

    [Fact]
    public void WindowState_ReportsFlagsAndActive()
    {
        var (server, _) = New();
        var r = WinState(server);
        Assert.True(r.GetProperty("sidebarVisible").GetBoolean());
        Assert.False(r.GetProperty("fullscreen").GetBoolean());
        Assert.False(r.GetProperty("maximized").GetBoolean());
        Assert.False(r.GetProperty("quickTerminalVisible").GetBoolean());
        Assert.Equal("workspace 1", r.GetProperty("activeWorkspace").GetString());
        Assert.Equal("session 1", r.GetProperty("activeSession").GetString());
    }

    [Fact]
    public void WindowState_SidebarVisible_FollowsSidebarOp()
    {
        var (server, _) = New();
        Dispatch(server, "sidebar", new { op = "hide" });
        Assert.False(WinState(server).GetProperty("sidebarVisible").GetBoolean());
        Dispatch(server, "sidebar", new { op = "show" });
        Assert.True(WinState(server).GetProperty("sidebarVisible").GetBoolean());
    }

    // ---- session lifecycle ----

    [Fact]
    public void NewSession_AppearsInTree_AndBecomesActive()
    {
        var (server, host) = New();
        var r = Dispatch(server, "session.new", new { name = "built" });
        Assert.True(Ok(r));
        Assert.Equal(2, host.ActiveWs.Sessions.Count);
        Assert.Equal("built", host.ActiveSess!.Name);
    }

    [Fact]
    public void SessionRename_ChangesName()
    {
        var (server, host) = New();
        Dispatch(server, "session.rename", new { name = "renamed" });
        Assert.Equal("renamed", host.ActiveSess!.Name);
    }

    [Fact]
    public void SessionFlag_TogglesFlag()
    {
        var (server, host) = New();
        Dispatch(server, "session.flag", new { op = "on" });
        Assert.True(host.ActiveSess!.Flagged);
        Dispatch(server, "session.flag", new { op = "toggle" });
        Assert.False(host.ActiveSess.Flagged);
    }

    [Fact]
    public void SessionBind_SetsAndClearsAgent()
    {
        var (server, host) = New();
        var r = Dispatch(server, "session.bind", new { agent = "claude" }, target: "s1");
        Assert.True(Ok(r));
        Assert.Equal("claude", host.ActiveSess!.AgentResume);
        Dispatch(server, "session.bind", new { agent = "none" }, target: "s1");
        Assert.Null(host.ActiveSess.AgentResume);
    }

    [Fact]
    public void SessionBind_UnknownTarget_Fails()
    {
        var (server, _) = New();
        var r = Dispatch(server, "session.bind", new { agent = "claude" }, target: "nope");
        Assert.False(Ok(r));
    }

    [Fact]
    public void ClaudeAdopt_BindsSessions()
    {
        var (server, host) = New();
        var r = Dispatch(server, "claude.adopt");
        Assert.True(Ok(r));
        Assert.Contains("adopted", Result(r));
        Assert.Equal("claude --resume x", host.ActiveSess!.AgentResume);
    }

    [Fact]
    public void ClaudeYolo_RestartsWithDangerousFlag()
    {
        var (server, host) = New();
        var r = Dispatch(server, "claude.yolo", target: "s1");
        Assert.True(Ok(r));
        Assert.Contains("YOLO", Result(r));
        Assert.Contains("--dangerously-skip-permissions", host.ActiveSess!.AgentResume);
    }

    [Fact]
    public void CloseSession_RemovesIt()
    {
        var (server, host) = New();
        string id = Dispatch(server, "session.new", new { name = "temp" }).GetProperty("result").GetString()!;
        Assert.Equal(2, host.ActiveWs.Sessions.Count);
        var r = Dispatch(server, "session.close", target: id);
        Assert.True(Ok(r));
        Assert.Single(host.ActiveWs.Sessions);
    }

    [Fact]
    public void SelectSession_SetsActive()
    {
        var (server, host) = New();
        string id = Dispatch(server, "session.new", new { name = "b" }).GetProperty("result").GetString()!;
        Dispatch(server, "session.select", target: "s1");
        Assert.Equal("session 1", host.ActiveSess!.Name);
        Dispatch(server, "session.select", target: id);
        Assert.Equal("b", host.ActiveSess.Name);
    }

    // ---- workspace lifecycle ----

    [Fact]
    public void NewWorkspace_ThenRename_ThenDelete()
    {
        var (server, host) = New();
        string wid = Dispatch(server, "workspace.new", new { name = "extra" }).GetProperty("result").GetString()!;
        Assert.Equal(2, host.Workspaces.Count);
        Dispatch(server, "workspace.rename", new { name = "renamed-ws" }, target: wid);
        Assert.Contains(host.Workspaces, w => w.Name == "renamed-ws");
        Dispatch(server, "workspace.delete", target: wid);
        Assert.Single(host.Workspaces);
    }

    [Fact]
    public void WorkspaceDelete_RefusesLastWorkspace()
    {
        var (server, host) = New();
        var r = Dispatch(server, "workspace.delete", target: "w1");
        Assert.False(Ok(r));            // can't delete the only workspace
        Assert.Single(host.Workspaces);
    }

    // ---- config ----

    [Fact]
    public void ConfigSet_ThenGet_RoundTrips()
    {
        var (server, _) = New();
        Dispatch(server, "config.set", new { key = "toolbar-mode", value = "hidden" });
        Assert.Equal("hidden", Result(Dispatch(server, "config.get", new { key = "toolbar-mode" })));
    }

    // ---- overlay ----

    [Fact]
    public void Overlay_Open_Resize_Close()
    {
        var (server, host) = New();
        Dispatch(server, "session.overlay", Overlay("open", 30, "cmd"));
        Assert.True(host.ActiveSess!.Overlay);
        Assert.Equal("resized 75%", Result(Dispatch(server, "session.overlay", Overlay("resize", 75))));
        Assert.Equal(75, host.ActiveSess.OverlaySize);
        Dispatch(server, "session.overlay", new { action = "close" });
        Assert.False(host.ActiveSess.Overlay);
    }

    [Fact]
    public void Overlay_Resize_WithNoOverlay_IsRefused()
    {
        // ok:false since P2 r1 - it answered ok:true "no overlay", and a script branching on ok
        // carried on as if the resize had happened. OverlaySizeTests pins the wording.
        var (server, _) = New();
        var r = Dispatch(server, "session.overlay", Overlay("resize", 50));
        Assert.False(Ok(r));
        Assert.Contains("no overlay", r.GetProperty("error").GetString());
    }

    [Fact]
    public void OmpSet_UnknownTheme_IsRefused()
    {
        // ok:false since #246 - it answered ok:true "oh-my-posh theme not found: X", and a script
        // branching on ok carried on with the old theme in place.
        var (server, _) = New();
        var r = Dispatch(server, "omp.set", new { name = "no-such-theme" });
        Assert.False(Ok(r));
        Assert.Equal(OmpThemes.NotFound("no-such-theme"), r.GetProperty("error").GetString());
        var ok = Dispatch(server, "omp.set", new { name = "fake-theme" });
        Assert.True(Ok(ok));
    }

    // ---- toggles ----

    [Fact]
    public void Broadcast_And_ReadOnly_And_Quick_Toggle()
    {
        var (server, host) = New();
        Assert.Equal("on", Result(Dispatch(server, "broadcast", new { op = "on" })));
        Assert.True(host.Broadcast);
        Assert.Equal("on", Result(Dispatch(server, "session.readonly", new { op = "on" })));
        Assert.True(host.ActiveSess!.ReadOnly);
        Dispatch(server, "quick", new { op = "on" });
        Assert.True(host.QuickVisible);
    }

    // ---- notifications ----

    [Fact]
    public void Notify_ThenSeen_ClearsBadge()
    {
        var (server, host) = New();
        Dispatch(server, "notify", new { title = "t", body = "b" });
        Assert.Equal(1, host.ActiveSess!.Notifications);
        Assert.Equal(1, Sessions0(server).GetProperty("notifications").GetInt32());
        Dispatch(server, "session.seen");
        Assert.Equal(0, host.ActiveSess.Notifications);
    }

    // ---- error handling ----

    [Fact]
    public void UnknownCommand_IsError()
    {
        var (server, _) = New();
        Assert.False(Ok(Dispatch(server, "does.not.exist")));
    }

    [Fact]
    public void InvalidJson_IsError()
    {
        var (server, _) = New();
        Assert.False(Ok(JsonDocument.Parse(server.Dispatch("}{ not json")).RootElement));
    }

    [Fact]
    public void CloseUnknownSession_IsError()
    {
        var (server, _) = New();
        Assert.False(Ok(Dispatch(server, "session.close", target: "nope-does-not-exist")));
    }
    // session.split used to ignore its target and always split the ACTIVE session. An agent
    // running in one pane that asked for a split while the user was looking at another session
    // split the user's session instead - and its "undo" then collapsed a pane of that session
    // (2026-09-03, a Claude session against a finished ralphex session). These pin the target.

    [Fact]
    public void SessionSplit_HonoursTarget_NotTheActiveSession()
    {
        var (server, host) = New();
        var original = host.ActiveSess!;
        string other = Dispatch(server, "session.new", new { name = "other" }).GetProperty("result").GetString()!;
        Assert.Equal("other", host.ActiveSess!.Name);      // session.new made the new one active

        var r = Dispatch(server, "session.split", new { op = "on" }, target: original.Id);
        Assert.True(Ok(r));
        Assert.Equal(2, original.PaneCount);                // the targeted session split
        Assert.Equal(1, host.ActiveSess!.PaneCount);        // the active one was left alone
        Assert.Equal("other", host.ActiveSess!.Name);       // and focus did not move
    }

    [Fact]
    public void SessionSplit_WithoutTarget_SplitsTheActiveSession()
    {
        var (server, host) = New();
        var r = Dispatch(server, "session.split", new { op = "on" });
        Assert.True(Ok(r));
        Assert.Equal(2, host.ActiveSess!.PaneCount);
        r = Dispatch(server, "session.split", new { op = "off" });
        Assert.True(Ok(r));
        Assert.Equal(1, host.ActiveSess!.PaneCount);
    }

    [Fact]
    public void SessionSplit_UnknownTarget_IsRefused()
    {
        var (server, host) = New();
        var r = Dispatch(server, "session.split", new { op = "on" }, target: "no-such-session");
        Assert.False(Ok(r));
        Assert.Contains("session not found", r.GetProperty("error").GetString());
        Assert.Equal(1, host.ActiveSess!.PaneCount);        // and nothing was split as a fallback
    }

    // ---- selection verbs and paste: a target with no pane is a refusal, not an ok:true string ----

    // The five verbs the P6 contract steps pin. Before this, ControlServer wrapped their host
    // replies in Ok(), so a target that resolved to nothing came back {ok:true,result:"no session"}
    // - a refusal every script reads as done. Same wording as session.rename / session.context.
    // session.copy answered ok:true "" — a missing pane and an empty selection were one reply; it now
    // refuses with the read verbs (session.text's "no session"), which is the one different wording.
    [Theory]
    [InlineData("selection.all", SessionContexts.NoSession)]
    [InlineData("selection.copy", SessionContexts.NoSession)]
    [InlineData("selection.clear", SessionContexts.NoSession)]
    [InlineData("selection.finalize", SessionContexts.NoSession)]
    [InlineData("session.paste", SessionContexts.NoSession)]
    [InlineData("session.copy", "no session")]
    public void SelectionVerbs_CopyAndPaste_NoPaneForTarget_IsRefused(string verb, string error)
    {
        var (server, host) = New();
        string active = host.ActiveSess!.Id;
        Write(server, active, "left alone\r\n");
        Assert.Equal("selected all", Result(Dispatch(server, "selection.all", target: active)));   // a selection that must SURVIVE the refusal
        var r = Dispatch(server, verb, verb == "session.paste" ? new { text = "x" } : null, target: "no-such-session-id");
        Assert.False(Ok(r));
        Assert.Equal(error, r.GetProperty("error").GetString());
        Assert.False(r.TryGetProperty("result", out _));
        Assert.Contains("left alone", Result(Dispatch(server, "session.copy", target: active)));   // nothing changed anywhere
    }

    // Round 8 of #256: session.paste reports what happened. A read-only pane is a REFUSAL (ok:false,
    // "pane is read-only"), made before any clipboard is read — the interactive paste shows a toast,
    // a script was told `pasted` for text that was dropped; an empty text with nothing on the
    // clipboard is ok:true "nothing to paste", never `pasted`. The fake has no clipboard, so its
    // omitted-text arm IS the empty clipboard; the payload rule itself is pinned in SessionPastesTests.
    [Fact]
    public void SessionPaste_ReadOnlyPane_IsRefused_AndAnEmptyPayload_SaysNothingToPaste()
    {
        var (server, host) = New();
        string active = host.ActiveSess!.Id;
        Assert.Equal("on", Result(Dispatch(server, "session.readonly", new { op = "on" }, target: active)));
        var refused = Dispatch(server, "session.paste", new { text = "dropped" }, target: active);
        Assert.False(Ok(refused));
        Assert.Equal(SessionPastes.ReadOnlyPane, refused.GetProperty("error").GetString());
        Assert.False(refused.TryGetProperty("result", out _));
        Assert.Equal("off", Result(Dispatch(server, "session.readonly", new { op = "off" }, target: active)));
        Assert.Equal(SessionPastes.Pasted, Result(Dispatch(server, "session.paste", new { text = "dropped" }, target: active)));
        var nothing = Dispatch(server, "session.paste", new { text = "" }, target: active);
        Assert.True(Ok(nothing));
        Assert.Equal(SessionPastes.Nothing, Result(nothing));
        Assert.Equal(SessionPastes.Nothing, Result(Dispatch(server, "session.paste", target: active)));   // no text at all
    }

    // The replies on a pane that HAS text but no selection yet (`selection all` makes one), on the
    // active session's id, with the product's defaults (copy-on-select OFF, so finalize says so and
    // never looks at the selection) — and session.copy reads back what `selection all` made, then
    // what `selection copy` (clears) and `selection finalize` (keeps) left of it.
    [Theory]
    [InlineData("selection.all", "selected all")]
    [InlineData("selection.copy", "no selection")]
    [InlineData("selection.clear", "cleared")]
    [InlineData("selection.finalize", "finalized (copy-on-select off)")]
    [InlineData("session.paste", "pasted")]
    [InlineData("session.copy", "")]
    public void SelectionVerbs_CopyAndPaste_OnTheActiveSession_AnswerOk(string verb, string reply)
    {
        var (server, host) = New();
        Write(server, host.ActiveSess!.Id, "some text\r\n");
        var r = Dispatch(server, verb, verb == "session.paste" ? new { text = "x" } : null, target: host.ActiveSess!.Id);
        Assert.True(Ok(r));
        Assert.Equal(reply, Result(r));
    }

    [Fact]
    public void SelectionAll_IsReadBackBySessionCopy_CopyClearsIt_FinalizeKeepsIt()
    {
        var (server, host) = New();
        string id = host.ActiveSess!.Id;
        // A blank 80x24 pane: SelectAll decides by geometry, so it IS a selection — 24 blank rows,
        // which SelectionText renders as 23 CRLFs and nothing else — and `selection copy` has
        // nothing in it worth the clipboard (CopySelection's whitespace arm) but clears it anyway.
        Assert.Equal("selected all", Result(Dispatch(server, "selection.all", target: id)));
        string blank = string.Concat(Enumerable.Repeat("\r\n", 23));
        Assert.Equal(blank, Result(Dispatch(server, "session.copy", target: id)));
        host.CopyOnSelect = true;                                                              // the copy is declined, the selection kept
        Assert.Equal("finalized (empty)", Result(Dispatch(server, "selection.finalize", target: id)));
        Assert.Equal(blank, Result(Dispatch(server, "session.copy", target: id)));
        host.CopyOnSelect = false;
        Assert.Equal("nothing to copy", Result(Dispatch(server, "selection.copy", target: id)));
        Assert.Equal("", Result(Dispatch(server, "session.copy", target: id)));
        Assert.Equal("no selection", Result(Dispatch(server, "selection.copy", target: id)));
        Write(server, id, "read me back\r\n");
        Assert.Equal("selected all", Result(Dispatch(server, "selection.all", target: id)));
        string sel = Result(Dispatch(server, "session.copy", target: id));
        Assert.StartsWith("read me back\r\n", sel);                                          // CRLF rows, as SelectionText joins them
        // finalize keeps the selection on every arm: off (the default) never looks at it, on copies it.
        Assert.Equal("finalized (copy-on-select off)", Result(Dispatch(server, "selection.finalize", target: id)));
        Assert.Equal(sel, Result(Dispatch(server, "session.copy", target: id)));
        host.CopyOnSelect = true;
        Assert.Equal("finalized (copied)", Result(Dispatch(server, "selection.finalize", target: id)));
        Assert.Equal(sel, Result(Dispatch(server, "session.copy", target: id)));
        Assert.Equal($"copied {sel.Length} chars", Result(Dispatch(server, "selection.copy", target: id)));
        Assert.Equal("", Result(Dispatch(server, "session.copy", target: id)));             // copy clears it (CopySelection(clear: true))
        Assert.Equal("no selection", Result(Dispatch(server, "selection.copy", target: id)));
        Assert.Equal("finalized (empty)", Result(Dispatch(server, "selection.finalize", target: id)));
    }

    // Decision 2 of the parity programme: on the alt screen `selection all` is the alt screen alone -
    // the history is the main screen's and stays reachable underneath (HistoryCount does not change
    // when the alt buffer is active), so a fake that walked it would hand back text neither product
    // does, and say `copied` where both say `nothing to copy`.
    [Fact]
    public void SelectionAll_OnTheAltScreen_IsTheAltScreenAlone()
    {
        var (server, host) = New();
        string id = host.ActiveSess!.Id;
        Write(server, id, "in history\r\n" + string.Concat(Enumerable.Repeat("filler\r\n", 30)));   // 24 rows: the first line scrolls into history
        Assert.Equal("selected all", Result(Dispatch(server, "selection.all", target: id)));
        Assert.StartsWith("in history\r\n", Result(Dispatch(server, "session.copy", target: id)));      // main screen: history first
        Write(server, id, "\u001b[?1049h");                                                              // enter the alt screen (blank)
        Assert.Equal("selected all", Result(Dispatch(server, "selection.all", target: id)));
        Assert.Equal(string.Concat(Enumerable.Repeat("\r\n", 23)), Result(Dispatch(server, "session.copy", target: id)));
        Assert.Equal("nothing to copy", Result(Dispatch(server, "selection.copy", target: id)));
        Write(server, id, "\u001b[?1049l");                                                              // back: the history is there again
        Assert.Equal("selected all", Result(Dispatch(server, "selection.all", target: id)));
        Assert.StartsWith("in history\r\n", Result(Dispatch(server, "session.copy", target: id)));
    }

    // The app drops a selection when the screen switches under it (ReconcileSel's first guard: an
    // index into one buffer names unrelated text in the other) — session copy answers "" and
    // selection copy answers "no selection" from then on, and coming BACK does not revive it. A fake that kept its
    // snapshot would hand the main screen's text out from under a full-screen TUI.
    [Fact]
    public void SelectionAll_ThenTheScreenSwitches_DropsTheSelection()
    {
        var (server, host) = New();
        string id = host.ActiveSess!.Id;
        Write(server, id, "main text");
        Assert.Equal("selected all", Result(Dispatch(server, "selection.all", target: id)));
        Assert.StartsWith("main text\r\n", Result(Dispatch(server, "session.copy", target: id)));
        Write(server, id, "\u001b[?1049h");                                                              // the TUI enters the alt screen
        Assert.Equal("", Result(Dispatch(server, "session.copy", target: id)));
        Assert.Equal("no selection", Result(Dispatch(server, "selection.copy", target: id)));
        Write(server, id, "\u001b[?1049l");                                                              // and leaves it: still gone
        Assert.Equal("", Result(Dispatch(server, "session.copy", target: id)));
        Assert.Equal("selected all", Result(Dispatch(server, "selection.all", target: id)));            // a new one works
        Assert.StartsWith("main text\r\n", Result(Dispatch(server, "session.copy", target: id)));
    }

    // SelectionText trims only SPACES from a row's end: a no-break space stays, so it counts in
    // `copied N chars` and, alone on a row, is something to copy - the whitespace arm draws its line
    // at CR, LF and U+0020, and a fake that trimmed every whitespace would answer it backwards.
    [Fact]
    public void SelectionAll_KeepsATrailingNoBreakSpace()
    {
        var (server, host) = New();
        string id = host.ActiveSess!.Id;
        Write(server, id, "\u00a0\u00a0");
        Assert.Equal("selected all", Result(Dispatch(server, "selection.all", target: id)));
        string sel = Result(Dispatch(server, "session.copy", target: id));
        Assert.StartsWith("\u00a0\u00a0\r\n", sel);
        Assert.Equal($"copied {sel.Length} chars", Result(Dispatch(server, "selection.copy", target: id)));
    }

    private static void Write(ControlServer server, string target, string text)
        => Assert.True(Ok(Dispatch(server, "session.write", new { text }, target: target)));
}
