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

    // Control characters embedded in OSC payloads are an injection vector (the title is re-shown
    // in the title bar; the cwd is expanded into custom-command text). They must be stripped.

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
        t.OscDispatch(2, "a\u007Fb\u009Cc");   // DEL + C1 (ST)
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
        t.OscDispatch(9, "4;1;250");   // clamped to 100
        t.OscDispatch(9, "4;0");       // clear, no value
        Assert.Equal(new[] { (1, 100), (0, 0) }, got.ToArray());
    }

    [Fact]
    public void Osc9_PlainMessage_StillNotifies()
    {
        var t = new TerminalEmulator(40, 3);
        string? body = null;
        t.Notified += (_, b) => body = b;
        t.OscDispatch(9, "42 done");   // not "4;" -> a normal notification
        Assert.Equal("42 done", body);
    }

    // Regression: the parser must UTF-8-decode OSC payloads (byte-as-char accumulation
    // mojibakes multibyte text and lets the C1 stripper eat continuation bytes).

    [Fact]
    public void OscTitle_Utf8DecodesMultibyteText()
    {
        var t = Feed("\x1b]2;normal title \u2014 \u00FCn\u00EFcode ok\x07");
        Assert.Equal("normal title \u2014 \u00FCn\u00EFcode ok", t.Title);
    }
}
