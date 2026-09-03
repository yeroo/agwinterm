using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// Two control-API promises an agent has to be able to rely on.
///
/// session.type REFUSES control bytes. agterm hardened this in v0.25.0 after a NUL truncated an
/// injection and the call still answered ok — the shortened line kept its Return and ran. Stripping
/// would produce that same shortened line silently, so the answer is a refusal.
///
/// session.text reaches into scrollback with `lines`. Without it the caller sees the visible screen
/// only, which is never where a launch banner or a pre-TUI error still lives.
/// </summary>
public class ControlServerTypeTextTests
{
    private static (ControlServer server, TerminalSession session) New(int rows = 6)
    {
        var session = new TerminalSession(40, rows);
        return (new ControlServer(session), session);
    }

    private static string Type(ControlServer server, string text)
        => server.Dispatch("{\"cmd\":\"session.type\",\"args\":{\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}}");

    /// <summary>The guard must not reject legitimate typing. These assert the REFUSAL does not fire —
    /// not that the write succeeded, since an unstarted session has no pty to write to.</summary>
    [Fact]
    public void Type_AcceptsOrdinaryTextAndNewlines()
    {
        var (server, _) = New();
        Assert.DoesNotContain("refuses", Type(server, "npm test\n"));
        Assert.DoesNotContain("refuses", Type(server, "git status\r\n"));
        Assert.DoesNotContain("refuses", Type(server, "cd src\tsrc\t"));   // TAB is completion, i.e. typing
    }

    [Fact]
    public void Type_RefusesNul_AndSaysWhere()
    {
        var (server, _) = New();
        string resp = Type(server, "rm -rf /tmp/safe\0 --force");
        Assert.Contains("\"ok\":false", resp);
        Assert.Contains("0x00", resp);            // says WHICH byte (JSON-escaped "+" is why this is hex)
        Assert.Contains("allow-control", resp);   // and names the escape hatch that actually works
    }

    [Fact]
    public void Type_RefusesEscapeAndInterrupt()
    {
        var (server, _) = New();
        Assert.Contains("\"ok\":false", Type(server, "ls" + (char)0x1b + "[A"));   // an escape sequence smuggled into typing
        Assert.Contains("\"ok\":false", Type(server, "wait" + (char)0x03));       // a lone interrupt byte
        Assert.Contains("\"ok\":false", Type(server, "back" + (char)0x7f));       // DEL
    }

    [Fact]
    public void Type_RefusalWritesNothing()
    {
        // The whole point: a refused call must not deliver the truncated prefix, which is what made
        // the original defect dangerous — the shortened command still got its Return.
        var (server, session) = New();
        string before = session.Emulator.DumpRow(0);
        Assert.Contains("\"ok\":false", Type(server, "echo hi\0\r"));
        Assert.Equal(before, session.Emulator.DumpRow(0));
    }

    /// <summary>The escape hatch has to exist, and has to be the one the refusal names. The first
    /// version of that message sent callers to session.write, which injects into the emulator and
    /// never reaches the shell — so a caller with a legitimate control byte had nowhere to go.</summary>
    [Fact]
    public void Type_AllowControl_SendsItAnyway()
    {
        var (server, _) = New();
        string resp = server.Dispatch(
            "{\"cmd\":\"session.type\",\"args\":{\"allow-control\":true,\"text\":\"ls\u001b[A\"}}");
        Assert.DoesNotContain("refuses", resp);
    }

    [Fact]
    public void Type_Refusal_NamesTheEscapeHatch_NotSessionWrite()
    {
        var (server, _) = New();
        string resp = Type(server, "oops" + (char)0x1b);
        Assert.Contains("allow-control", resp);
        Assert.DoesNotContain("session.write", resp);
    }

    [Fact]
    public void Text_WithoutLines_IsTheVisibleScreenOnly()
    {
        var (server, session) = New(rows: 4);
        for (int i = 1; i <= 10; i++) session.Emulator.Feed(System.Text.Encoding.UTF8.GetBytes($"L{i}\r\n"));
        string resp = server.Dispatch("{\"cmd\":\"session.text\"}");
        Assert.Contains("\"ok\":true", resp);
        Assert.DoesNotContain("L1\\n", resp);      // scrolled off; unreachable without `lines`
        Assert.Contains("L10", resp);
    }

    [Fact]
    public void Text_WithLines_ReachesScrollback()
    {
        var (server, session) = New(rows: 4);
        for (int i = 1; i <= 10; i++) session.Emulator.Feed(System.Text.Encoding.UTF8.GetBytes($"L{i}\r\n"));
        string resp = server.Dispatch("{\"cmd\":\"session.text\",\"args\":{\"lines\":12}}");
        Assert.Contains("\"ok\":true", resp);
        Assert.Contains("L1", resp);               // the line the screen alone cannot show
        Assert.Contains("L10", resp);
    }

    [Fact]
    public void Text_LinesLargerThanTheBuffer_IsNotAnError()
    {
        var (server, session) = New(rows: 4);
        session.Emulator.Feed(System.Text.Encoding.UTF8.GetBytes("only\r\n"));
        string resp = server.Dispatch("{\"cmd\":\"session.text\",\"args\":{\"lines\":9999}}");
        Assert.Contains("\"ok\":true", resp);
        Assert.Contains("only", resp);
    }
}
