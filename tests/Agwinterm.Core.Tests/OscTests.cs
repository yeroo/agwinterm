using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class OscTests
{
    private static TerminalEmulator Feed(string s)
    {
        var t = new TerminalEmulator(40, 3);
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    [Fact]
    public void Osc2_SetsTitle()
    {
        var t = Feed("\x1b]2;My Session\x07");
        Assert.Equal("My Session", t.Title);
    }

    [Fact]
    public void Osc0_SetsTitle()
    {
        var t = Feed("\x1b]0;Another\x07");
        Assert.Equal("Another", t.Title);
    }

    [Fact]
    public void Osc7_SetsCwd()
    {
        var t = Feed("\x1b]7;file:///c:/work/repo\x1b\\");
        Assert.Equal("file:///c:/work/repo", t.Cwd);
    }

    [Fact]
    public void OscDoesNotEmitPrintableCharacters()
    {
        var t = Feed("\x1b]0;hidden\x07visible");
        Assert.Equal("visible", t.DumpRow(0));
    }

    [Fact]
    public void Osc9_FiresNotifiedWithBody()
    {
        var t = new TerminalEmulator(40, 3);
        var host = new RecordingHost(); t.Host = host;
        t.Feed(Encoding.UTF8.GetBytes("\x1b]9;build finished\x07"));
        Assert.Equal(new[] { ("", "build finished") }, host.Notifications.ToArray());
    }

    [Fact]
    public void Osc777_NotifyFiresNotifiedWithTitleAndBody()
    {
        var t = new TerminalEmulator(40, 3);
        var host = new RecordingHost(); t.Host = host;
        t.Feed(Encoding.UTF8.GetBytes("\x1b]777;notify;npm;tests passed\x07"));
        Assert.Equal(new[] { ("npm", "tests passed") }, host.Notifications.ToArray());
    }

    [Fact]
    public void Osc9_DoesNotChangeTitleOrCwd()
    {
        var t = Feed("\x1b]9;hello\x07");
        Assert.Equal("", t.Title);
        Assert.Equal("", t.Cwd);
    }

    // Control characters embedded in OSC payloads are an injection vector; they must be stripped.

    [Fact]
    public void OscTitle_StripsControlCharacters()
    {
        var t = Feed("\x1b]2;bad\r\ntitle\twith\u0008controls\x07");
        Assert.Equal("badtitlewithcontrols", t.Title);
    }

    [Fact]
    public void OscCwd_StripsControlCharacters()
    {
        var t = Feed("\x1b]7;file:///c:/work\n/repo\x1b\\");
        Assert.Equal("file:///c:/work/repo", t.Cwd);
    }

    [Fact]
    public void OscTitle_StripsDelAndC1Controls()
    {
        var t = new TerminalEmulator(40, 3);
        t.OscDispatch(2, "a\u007Fb\u009Cc");
        Assert.Equal("abc", t.Title);
    }

    [Fact]
    public void OscNotification_StripsControlCharacters()
    {
        var t = new TerminalEmulator(40, 3);
        var host = new RecordingHost(); t.Host = host;
        t.OscDispatch(9, "line1\r\nline2");
        Assert.Equal("line1line2", host.Notifications.Single().Body);
    }

    // OSC 9;4 (ConEmu/Windows Terminal progress) must fire Progress, NOT a notification toast.

    [Fact]
    public void Osc9_4_FiresProgressNotNotification()
    {
        var t = new TerminalEmulator(40, 3);
        var host = new RecordingHost(); t.Host = host;
        t.OscDispatch(9, "4;1;62");
        Assert.Equal(new[] { (1, 62) }, host.ProgressReports.ToArray());
        Assert.Empty(host.Notifications);
    }

    [Fact]
    public void Osc9_4_ClearAndClamp()
    {
        var t = new TerminalEmulator(40, 3);
        var host = new RecordingHost(); t.Host = host;
        t.OscDispatch(9, "4;1;250");
        t.OscDispatch(9, "4;0");
        Assert.Equal(new[] { (1, 100), (0, 0) }, host.ProgressReports.ToArray());
    }

    [Fact]
    public void Osc9_PlainMessage_StillNotifies()
    {
        var t = new TerminalEmulator(40, 3);
        var host = new RecordingHost(); t.Host = host;
        t.OscDispatch(9, "42 done");
        Assert.Equal("42 done", host.Notifications.Single().Body);
    }

    // FTCS (OSC 133) shell-integration marks: prompt/output boundaries + exit codes.

    [Fact]
    public void Ftcs_RecordsPromptOutputAndExit()
    {
        var t = new TerminalEmulator(40, 6);
        t.Feed(Encoding.UTF8.GetBytes("\x1b]133;A\x07PS> dir\r\n"));
        t.Feed(Encoding.UTF8.GetBytes("\x1b]133;C\afile1\r\nfile2\r\n"));
        t.Feed(Encoding.UTF8.GetBytes("\x1b]133;D;0\x07\x1b]133;A\x07PS> "));
        Assert.Equal(2, t.Marks.Count);
        Assert.Equal(0, t.Marks[0].PromptLine);
        Assert.Equal(1, t.Marks[0].OutputLine);
        Assert.Equal(3, t.Marks[0].EndLine);
        Assert.Equal(0, t.Marks[0].ExitCode);
        Assert.Equal(3, t.Marks[1].PromptLine);
        Assert.Null(t.Marks[1].ExitCode);
    }

    [Fact]
    public void Ftcs_FailureExitCodeRecorded()
    {
        var t = new TerminalEmulator(40, 6);
        t.Feed(Encoding.UTF8.GetBytes("\x1b]133;A\x07PS> boom\r\nerr\r\n\x1b]133;D;1\x07"));
        Assert.Single(t.Marks);
        Assert.Equal(1, t.Marks[0].ExitCode);
    }

    // Regression: the parser must UTF-8-decode OSC payloads.

    [Fact]
    public void OscTitle_Utf8DecodesMultibyteText()
    {
        var t = Feed("\x1b]2;normal title \u2014 \u00FCn\u00EFcode ok\x07");
        Assert.Equal("normal title \u2014 \u00FCn\u00EFcode ok", t.Title);
    }

    // ---- OSC 52: program-initiated clipboard write (e.g. Claude Code's auto-copy-on-select) ----

    private static (TerminalEmulator t, List<string> writes) ClipboardHarness()
    {
        var t = new TerminalEmulator(40, 3);
        var host = new RecordingHost();
        t.Host = host;
        return (t, host.ClipboardWrites);
    }

    [Fact]
    public void Osc52_DecodesBase64AndFiresClipboardWrite()
    {
        var (t, writes) = ClipboardHarness();
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello clipboard"));
        t.Feed(Encoding.UTF8.GetBytes($"\x1b]52;c;{b64}\x07"));
        Assert.Equal(new[] { "hello clipboard" }, writes);
    }

    [Fact]
    public void Osc52_ToleratesUnpaddedBase64AndUtf8()
    {
        var (t, writes) = ClipboardHarness();
        // "\u00FCn\u00EFcode\u2122" encodes with padding; strip it to simulate emitters that drop '='.
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("\u00FCn\u00EFcode\u2122")).TrimEnd('=');
        t.Feed(Encoding.UTF8.GetBytes($"\x1b]52;c;{b64}\x1b\\"));
        Assert.Equal(new[] { "\u00FCn\u00EFcode\u2122" }, writes);
    }

    [Fact]
    public void Osc52_IgnoresQueryEmptyAndMalformed()
    {
        var (t, writes) = ClipboardHarness();
        t.Feed(Encoding.UTF8.GetBytes("\x1b]52;c;?\x07"));          // read-back query \u2014 never answered
        t.Feed(Encoding.UTF8.GetBytes("\x1b]52;c;\x07"));           // empty payload
        t.Feed(Encoding.UTF8.GetBytes("\x1b]52;c;!!notbase64!!\x07")); // malformed base64
        t.Feed(Encoding.UTF8.GetBytes("\x1b]52;justonefield\x07")); // no payload separator
        Assert.Empty(writes);
    }

    [Fact]
    public void Osc52_SelectionTargetIsIgnoredEverythingGoesToClipboard()
    {
        var (t, writes) = ClipboardHarness();
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("primary"));
        t.Feed(Encoding.UTF8.GetBytes($"\x1b]52;p;{b64}\x07"));     // "p" (primary) still lands
        Assert.Equal(new[] { "primary" }, writes);
    }

    // ---- The unhandled-sequence tap: protocol gaps must be observable, never silent ----

    [Fact]
    public void UnhandledOsc_ReportsThroughHostTap()
    {
        var t = new TerminalEmulator(40, 3);
        var host = new RecordingHost(); t.Host = host;
        t.Feed(Encoding.UTF8.GetBytes("\x1b]8;;https://example.com\x1b\\"));   // OSC 8 hyperlink (not implemented)
        var u = Assert.Single(host.Unhandleds);
        Assert.Equal("OSC", u.Kind);
        Assert.StartsWith("8;", u.Detail);
    }

    [Fact]
    public void UnhandledCsiEscAndMode_ReportThroughHostTap()
    {
        var t = new TerminalEmulator(40, 3);
        var host = new RecordingHost(); t.Host = host;
        t.Feed(Encoding.UTF8.GetBytes("\x1b[3 q"));       // DECSCUSR (not implemented; space intermediate dropped by parser)
        t.Feed(Encoding.UTF8.GetBytes("\x1b[?2026h"));    // synchronized output mode (not implemented)
        Assert.Contains(host.Unhandleds, u => u.Kind == "MODE" && u.Detail == "?2026 h");
        Assert.NotEmpty(host.Unhandleds);
    }

    [Fact]
    public void HandledSequences_DoNotTouchTheTap()
    {
        var t = new TerminalEmulator(40, 3);
        var host = new RecordingHost(); t.Host = host;
        t.Feed(Encoding.UTF8.GetBytes("hello\r\n\x1b[2J\x1b[H\x1b[31mred\x1b[0m\x1b]2;title\x07\x1b[?25l\x1b[?1049h\x1b[?1049l"));
        Assert.Empty(host.Unhandleds);
    }
}
