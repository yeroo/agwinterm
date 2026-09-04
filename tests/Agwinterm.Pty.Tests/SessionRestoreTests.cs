using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// session.restore SAYS WHICH PANE RECEIVED THE PIN (P2, "stop lying to the caller").
///
/// Before this the reply was the constant "pinned", the host resolved the target through a path no
/// other verb used, and <c>tree</c> never emitted the <c>restoreCommands</c> the skill file promised —
/// so which pane in a split now carried the pin was unknowable from the caller's side, and a clear
/// was reported with the same word as a pin. Now the reply names the pane and its session, says
/// whether it pinned or cleared, and the tree reads the pin back keyed by pane id. As with #213's
/// refusals, every refusal is asserted twice: the reply, and that nothing was pinned.
/// </summary>
public class SessionRestoreTests
{
    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    private static JsonElement Restore(ControlServer server, string? target, string? command = "npm run dev")
    {
        var sb = new System.Text.StringBuilder("{\"cmd\":\"session.restore\"");
        if (target is not null) sb.Append(",\"target\":").Append(JsonSerializer.Serialize(target));
        if (command is not null) sb.Append(",\"args\":{\"command\":").Append(JsonSerializer.Serialize(command)).Append('}');
        sb.Append('}');
        return JsonDocument.Parse(server.Dispatch(sb.ToString())).RootElement;
    }

    private static bool Ok(JsonElement r) => r.GetProperty("ok").GetBoolean();
    private static string Error(JsonElement r) => r.GetProperty("error").GetString() ?? "";
    private static JsonElement Result(JsonElement r) => r.GetProperty("result");
    private static string Str(JsonElement r, string name) => Result(r).GetProperty(name).GetString() ?? "";

    /// <summary>The first session's node from <c>tree</c> — the read-back a caller has after the fact.</summary>
    private static JsonElement TreeSession(ControlServer server)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"tree\"}")).RootElement
            .GetProperty("result").GetProperty("workspaces")[0].GetProperty("sessions")[0];

    private static JsonElement? TreePins(ControlServer server)
        => TreeSession(server).TryGetProperty("restoreCommands", out var v) ? v : null;

    private static int AllPins(FakeSessionHost host)
        => host.Workspaces.SelectMany(w => w.Sessions).Sum(s => s.RestorePins.Count);

    // ---- the reply names the pane, and is an object, not a word ----

    [Fact]
    public void Pin_RepliesWithPaneSessionActionAndCommand()
    {
        var (server, host) = New();
        var r = Restore(server, "s1");
        Assert.True(Ok(r));
        Assert.Equal(JsonValueKind.Object, Result(r).ValueKind);   // OkRaw: an object, not a string of JSON
        Assert.Equal("pinned", Str(r, "action"));
        Assert.Equal("s1", Str(r, "pane"));
        Assert.Equal("s1", Str(r, "session"));
        Assert.Equal("npm run dev", Str(r, "command"));
        Assert.Equal("npm run dev", host.ActiveSess!.RestorePins["s1"]);
    }

    [Fact]
    public void SessionIdTarget_LandsOnPaneZero_PaneIdTarget_LandsOnThatPane()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.AddPane();
        string p1 = ses.PaneIds[1];
        ses.FocusedPane = 1;   // focus is on the second pane: an ID target must NOT follow it

        var byId = Restore(server, "s1", "one");
        Assert.Equal("s1", Str(byId, "pane"));     // the session id IS pane 0's id (control-API back-compat)
        Assert.Equal("s1", Str(byId, "session"));

        var byPane = Restore(server, p1, "two");
        Assert.Equal(p1, Str(byPane, "pane"));
        Assert.Equal("s1", Str(byPane, "session"));   // the reply says whose pane it is

        Assert.Equal("one", ses.RestorePins["s1"]);
        Assert.Equal("two", ses.RestorePins[p1]);
    }

    [Fact]
    public void PaneIdPrefix_LandsOnThatPane()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.AddPane();
        string p1 = ses.PaneIds[1];
        var r = Restore(server, p1[..4]);
        Assert.True(Ok(r));
        Assert.Equal(p1, Str(r, "pane"));
    }

    /// <summary>The one thing the shared resolver adds over the old pane-only one: a session NAME now
    /// reaches that session's focused pane, as it does for session.type — and the reply says which.</summary>
    [Fact]
    public void NameTarget_LandsOnFocusedPane_AndTheReplySaysWhich()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.AddPane();
        ses.FocusedPane = 1;
        var r = Restore(server, "session 1");
        Assert.True(Ok(r));
        Assert.Equal(ses.PaneIds[1], Str(r, "pane"));
        Assert.Equal("s1", Str(r, "session"));
    }

    // ---- clear is reported as a clear ----

    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    [InlineData("")]
    [InlineData("   ")]
    public void None_ReportsAClear_NotAPin(string clearWord)
    {
        var (server, host) = New();
        Assert.True(Ok(Restore(server, "s1")));
        var r = Restore(server, "s1", clearWord);
        Assert.True(Ok(r));
        Assert.Equal("cleared", Str(r, "action"));
        Assert.Equal("s1", Str(r, "pane"));
        Assert.False(Result(r).TryGetProperty("command", out _));   // a clear has no command to echo
        Assert.Empty(host.ActiveSess!.RestorePins);
    }

    [Fact]
    public void Clear_WithNoArgsAtAll_IsAClear()
    {
        var (server, host) = New();
        Assert.True(Ok(Restore(server, "s1")));
        var r = Restore(server, "s1", command: null);   // no args object at all
        Assert.Equal("cleared", Str(r, "action"));
        Assert.Empty(host.ActiveSess!.RestorePins);
    }

    // ---- refusals: the reply, and then that nothing was pinned ----

    /// <summary>A scratch / overlay / quick cover pane is a valid TARGET (every content verb reaches
    /// it) but never in the saved tree, so a pin on it would answer ok and vanish at the next restart
    /// — the one refusal in this batch that revmux r1 found asserted nowhere. Pinned nothing, and the
    /// error names the pane.</summary>
    [Fact]
    public void CoverPane_IsRefused_NamesThePane_AndPinsNothing()
    {
        var (server, host) = New();
        var sess = host.Workspaces[0].Sessions[0];
        string cover = sess.AddCoverPane();
        var r = Restore(server, cover);
        Assert.False(Ok(r));
        Assert.Contains(cover, Error(r));
        Assert.Contains("never restored", Error(r));
        Assert.Contains("Nothing pinned", Error(r));
        Assert.Equal(0, AllPins(host));
        Assert.Null(TreePins(server));
        // and the session itself is still pinnable: the refusal was about the pane, not the session
        Assert.True(Ok(Restore(server, sess.Id)));
        Assert.Equal(1, AllPins(host));
    }

    [Fact]
    public void UnknownTarget_IsRefused_AndPinsNothing()
    {
        var (server, host) = New();
        var r = Restore(server, "no-such-pane");
        Assert.False(Ok(r));
        Assert.Contains("no-such-pane", Error(r));
        Assert.Contains("Nothing pinned", Error(r));
        Assert.Equal(0, AllPins(host));
        Assert.Null(TreePins(server));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("active")]
    public void EmptyOrActiveTarget_IsRefused_SaysWhy_AndPinsNothing(string? target)
    {
        var (server, host) = New();
        var r = Restore(server, target);
        Assert.False(Ok(r));
        string err = Error(r);
        Assert.Contains("--target", err);             // how to fix it
        Assert.Contains("active", err);               // why there is no default: a pin outlives the active pane
        Assert.Contains("AGWINTERM_SESSION_ID", err); // where the pane id comes from inside a session
        Assert.Equal(0, AllPins(host));
        Assert.Null(TreePins(server));
    }

    [Fact]
    public void AmbiguousName_IsRefused_AndPinsNothing()
    {
        var (server, host) = New();
        host.NewSession("session 1", null, null);   // a second session with the same name
        var r = Restore(server, "session 1");
        Assert.False(Ok(r));
        Assert.Equal(0, AllPins(host));
    }

    // ---- the read-back: tree's restoreCommands, keyed by pane ----

    [Fact]
    public void Tree_ShowsThePin_KeyedByPane_AndDropsItWhenCleared()
    {
        var (server, _) = New();
        Assert.Null(TreePins(server));   // no pin: no field, like the other optional per-session fields

        Assert.True(Ok(Restore(server, "s1", "npm run dev")));
        var pins = TreePins(server);
        Assert.NotNull(pins);
        Assert.Equal(JsonValueKind.Object, pins!.Value.ValueKind);
        Assert.Equal("npm run dev", pins.Value.GetProperty("s1").GetString());

        Assert.True(Ok(Restore(server, "s1", "none")));
        Assert.Null(TreePins(server));   // cleared: the field is gone, not present-and-empty
    }

    [Fact]
    public void Tree_ListsOnlyPinnedPanes_APaneWithNoPinIsAbsent()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.AddPane();
        string p1 = ses.PaneIds[1];

        Assert.True(Ok(Restore(server, p1, "tail -f log")));
        var pins = TreePins(server)!.Value;
        Assert.Equal("tail -f log", pins.GetProperty(p1).GetString());
        Assert.False(pins.TryGetProperty("s1", out _));   // pane 0 has no pin: not listed, not "" either
        Assert.Single(pins.EnumerateObject());

        Assert.True(Ok(Restore(server, "s1", "npm test")));
        pins = TreePins(server)!.Value;
        Assert.Equal(2, pins.EnumerateObject().Count());
        Assert.Equal("npm test", pins.GetProperty("s1").GetString());
    }

    [Fact]
    public void Repin_ReplacesTheCommand_AndTheTreeShowsTheNewOne()
    {
        var (server, _) = New();
        Assert.True(Ok(Restore(server, "s1", "first")));
        Assert.True(Ok(Restore(server, "s1", "second")));
        Assert.Equal("second", TreePins(server)!.Value.GetProperty("s1").GetString());
    }
}
