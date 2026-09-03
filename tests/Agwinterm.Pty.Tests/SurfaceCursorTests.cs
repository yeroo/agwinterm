using System.Text;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>Contract tests for <c>surface.cursor</c> — the caret column of a pane, as a BARE integer.
/// The shape is the contract agliteterm mirrors (P8) and the reason a script written against agterm's
/// cookbook works here unchanged, so it is pinned as tightly as the value itself.</summary>
public class SurfaceCursorTests
{
    private static JsonElement Cursor(ControlServer server, string? target = null)
    {
        string req = target is null
            ? "{\"cmd\":\"surface.cursor\"}"
            : "{\"cmd\":\"surface.cursor\",\"target\":" + JsonSerializer.Serialize(target) + "}";
        return JsonDocument.Parse(server.Dispatch(req)).RootElement;
    }

    private static void Feed(TerminalSession s, string text) => s.Inject(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void FreshSession_ReportsColumnZero_NotAnAbsentAnswer()
    {
        using var session = new TerminalSession(80, 24);
        using var server = new ControlServer(session);

        var r = Cursor(server);
        Assert.True(r.GetProperty("ok").GetBoolean());
        var result = r.GetProperty("result");
        Assert.Equal(JsonValueKind.Number, result.ValueKind);   // 0 is a real answer, not a missing one
        Assert.Equal(0, result.GetInt32());
    }

    [Fact]
    public void ReplyIsABareInteger_NotAnObject()
    {
        using var session = new TerminalSession(80, 24);
        using var server = new ControlServer(session);
        Feed(session, "hello");

        string raw = server.Dispatch("{\"cmd\":\"surface.cursor\"}");
        // The exact wire form P8 mirrors: {"ok":true,"result":5} — no {"col":5}, no "5".
        Assert.Equal("{\"ok\":true,\"result\":5}", raw);
    }

    [Fact]
    public void ColumnTracksTheEmulator()
    {
        using var session = new TerminalSession(80, 24);
        using var server = new ControlServer(session);

        Feed(session, "> ");
        Assert.Equal(2, Cursor(server).GetProperty("result").GetInt32());

        Feed(session, "draft");
        Assert.Equal(7, Cursor(server).GetProperty("result").GetInt32());

        // A carriage return puts the caret back at column 0 — the reply follows the emulator, not a counter.
        Feed(session, "\r");
        Assert.Equal(0, Cursor(server).GetProperty("result").GetInt32());
    }

    [Fact]
    public void MatchesTheSessionsOwnCursorSnapshot()
    {
        using var session = new TerminalSession(80, 24);
        using var server = new ControlServer(session);
        Feed(session, "\u001b[5;13H");   // CUP: row 5, col 13 (1-based)

        var (row, col) = session.SnapshotCursor();
        Assert.Equal(4, row);
        Assert.Equal(12, col);
        // Column only; the row is deliberately not reported.
        Assert.Equal(col, Cursor(server).GetProperty("result").GetInt32());
    }

    [Fact]
    public void AltScreen_IsNotSpecialCased()
    {
        using var session = new TerminalSession(80, 24);
        using var server = new ControlServer(session);
        Feed(session, "\u001b[?1049h");   // enter the alternate screen
        Feed(session, "abc");

        Assert.True(session.Emulator.IsAltScreen);
        Assert.Equal(3, Cursor(server).GetProperty("result").GetInt32());
    }

    [Fact]
    public void UnknownTarget_IsRefused()
    {
        using var server = new ControlServer(new FakeSessionHost());

        var r = Cursor(server, "no-such-pane");
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal("no session", r.GetProperty("error").GetString());
    }

    [Fact]
    public void KnownTarget_IsAnswered()
    {
        using var server = new ControlServer(new FakeSessionHost());

        var r = Cursor(server, "s1");
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Number, r.GetProperty("result").ValueKind);
    }

    // ---- targeting a split session: the asymmetry qa/control-read.md found ----

    [Fact]
    public void MultiPaneSession_ByNAME_ReportsTheFOCUSEDPane()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        var s = host.ActiveSess!;
        Feed((TerminalSession)s.Panes[0], "left");
        Feed((TerminalSession)s.AddPane(), "right-hand-side");
        s.FocusedPane = 1;

        // A cursor is a per-pane thing; focus is the only non-arbitrary answer for a whole session.
        Assert.Equal(15, Cursor(server, "session 1").GetProperty("result").GetInt32());
    }

    [Fact]
    public void MultiPaneSession_ByID_ReportsPaneZero_BecauseTheIdIsPaneZeros()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        var s = host.ActiveSess!;
        Feed((TerminalSession)s.Panes[0], "left");
        Feed((TerminalSession)s.AddPane(), "right-hand-side");
        s.FocusedPane = 1;

        // Not a bug and not focus-blindness: a session's id IS its first pane's id, so Resolve
        // matches it as a pane. session.text / session.type widen the same way, which is the
        // guarantee that matters — the pane you check is the pane you then type into.
        Assert.Equal(4, Cursor(server, "s1").GetProperty("result").GetInt32());
    }

    [Fact]
    public void APaneIdPrefix_ReachesThatPane()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        var s = host.ActiveSess!;
        Feed((TerminalSession)s.Panes[0], "left");
        Feed((TerminalSession)s.AddPane(), "right-hand-side");
        string prefix = s.PaneIds[1][..6];   // how an agent abbreviates the id the tree gave it

        Assert.Equal(15, Cursor(server, prefix).GetProperty("result").GetInt32());
        Assert.Equal(15, Cursor(server, s.PaneIds[1]).GetProperty("result").GetInt32());
    }

    [Fact]
    public void AnAmbiguousName_IsRefused_RatherThanGuessed()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        host.NewSession("session 1", null, null);   // a second session sharing the first one's name

        // Reading the wrong terminal's caret then typing into it is the failure this prevents.
        Assert.False(Cursor(server, "session 1").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task APaneWhoseChildHasEXITED_StillReportsItsCaret()
    {
        using var session = new TerminalSession(80, 24);
        using var server = new ControlServer(session);
        await session.StartAsync("cmd.exe", new[] { "/c", "exit" }, verbatimCommandLine: false);
        Assert.True(WaitFor(() => session.HasExited), "the child never exited");

        // A dead child does not un-address the pane: the grid is still there to be read, and a caller
        // deciding whether to type is exactly the one who must not get an error here instead of a number.
        var r = Cursor(server);
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Number, r.GetProperty("result").ValueKind);
    }

    private static bool WaitFor(Func<bool> cond, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    [Fact]
    public void ASessionRemovedFromTheTree_IsRefused_NotAnsweredFromAStaleHandle()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        Assert.True(Cursor(server, "s1").GetProperty("ok").GetBoolean());

        host.CloseSession("s1");

        var r = Cursor(server, "s1");
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal("no session", r.GetProperty("error").GetString());
    }
}
