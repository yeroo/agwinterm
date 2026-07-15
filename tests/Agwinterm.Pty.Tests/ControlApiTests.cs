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
        host.Split("on");   // 2 panes, 0.5/0.5
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
    public void Overlay_Resize_WithNoOverlay_ReturnsNoOverlay()
    {
        var (server, _) = New();
        Assert.Equal("no overlay", Result(Dispatch(server, "session.overlay", Overlay("resize", 50))));
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
}
