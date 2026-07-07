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
        string? gotTitle = null, gotBody = null;
        t.Notified += (title, body) => { gotTitle = title; gotBody = body; };
        t.Feed(Encoding.UTF8.GetBytes("\x1b]9;build finished\x07"));
        Assert.Equal("", gotTitle);
        Assert.Equal("build finished", gotBody);
    }

    [Fact]
    public void Osc777_NotifyFiresNotifiedWithTitleAndBody()
    {
        var t = new TerminalEmulator(40, 3);
        string? gotTitle = null, gotBody = null;
        t.Notified += (title, body) => { gotTitle = title; gotBody = body; };
        t.Feed(Encoding.UTF8.GetBytes("\x1b]777;notify;npm;tests passed\x07"));
        Assert.Equal("npm", gotTitle);
        Assert.Equal("tests passed", gotBody);
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
        string? gotBody = null;
        t.Notified += (_, body) => gotBody = body;
        t.OscDispatch(9, "line1\r\nline2");
        Assert.Equal("line1line2", gotBody);
    }

    // OSC 9;4 (ConEmu/Windows Terminal progress) must fire Progress, NOT a notification toast.

    [Fact]
    public void Osc9_4_FiresProgressNotNotification()
    {
        var t = new TerminalEmulator(40, 3);
        int? st = null, val = null; bool notified = false;
        t.Progress += (s, v) => { st = s; val = v; };
        t.Notified += (_, _) => notified = true;
        t.OscDispatch(9, "4;1;62");
        Assert.Equal(1, st);
        Assert.Equal(62, val);
        Assert.False(notified);
    }

    [Fact]
    public void Osc9_4_ClearAndClamp()
    {
        var t = new TerminalEmulator(40, 3);
        var got = new List<(int, int)>();
        t.Progress += (s, v) => got.Add((s, v));
        t.OscDispatch(9, "4;1;250");
        t.OscDispatch(9, "4;0");
        Assert.Equal(new[] { (1, 100), (0, 0) }, got.ToArray());
    }

    [Fact]
    public void Osc9_PlainMessage_StillNotifies()
    {
        var t = new TerminalEmulator(40, 3);
        string? body = null;
        t.Notified += (_, b) => body = b;
        t.OscDispatch(9, "42 done");
        Assert.Equal("42 done", body);
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
}
