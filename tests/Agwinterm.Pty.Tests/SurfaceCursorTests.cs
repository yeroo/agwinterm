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
}
