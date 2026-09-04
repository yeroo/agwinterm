using System.Text;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// `session type --stdin` (P2): the text is standard input, as bytes, so what the caller wrote is
/// what the shell receives. The argv path loses argument boundaries, a leading `--` and runs of
/// spaces silently; stdin loses nothing — and invalid UTF-8 is refused with the byte offset, because
/// the server would otherwise turn it into U+FFFD and answer ok.
/// </summary>
public class StdinTextTests
{
    private static StdinText.Outcome Read(string s) => StdinText.Read(new MemoryStream(Encoding.UTF8.GetBytes(s)));
    private static StdinText.Outcome Read(byte[] b) => StdinText.Read(new MemoryStream(b));

    [Fact]
    public void PlainString_RoundTrips()
    {
        var r = Read("npm test");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("npm test", r.Text);
    }

    [Fact]
    public void Quotes_Newlines_AndLeadingDashDash_Survive()
    {
        // Each of these is something argv cannot carry here: the splitter eats a leading "--", a
        // quote needs two shells' worth of escaping, and a newline mid-text is a second Enter.
        string text = "--force \"quoted\" 'single'\nsecond line";
        var r = Read(text);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(text, r.Text);
    }

    [Fact]
    public void ConsecutiveSpaces_Survive_UnlikeArgv()
    {
        var r = Read("a  b   c");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("a  b   c", r.Text);
        // The argv path: the CLI splits on argument boundaries and re-joins with a single space, so
        // the same text arrives as "a b c". This is the difference --stdin exists to close.
        string viaArgv = string.Join(' ', new[] { "a", "b", "c" });
        Assert.NotEqual(r.Text, viaArgv);
    }

    [Fact]
    public void ExactlyOneTrailingNewline_IsStripped()
    {
        Assert.Equal("ls", Read("ls\n").Text);
        Assert.Equal("ls", Read("ls\r\n").Text);          // PowerShell's pipe to a native process
        Assert.Equal("ls\n", Read("ls\n\n").Text);         // a caller who means Enter can still send it
        Assert.Equal("ls\r\n", Read("ls\r\n\r\n").Text);
        Assert.Equal("ls\r", Read("ls\r").Text);           // a bare CR is not a line ending here; left alone
    }

    [Fact]
    public void LoneHighByte_IsRefused_AndTheOffsetIsNamed()
    {
        var bytes = Encoding.UTF8.GetBytes("hello").Concat(new byte[] { 0x80 }).Concat(Encoding.UTF8.GetBytes(" world")).ToArray();
        var r = Read(bytes);
        Assert.False(r.Ok);
        Assert.Null(r.Text);
        Assert.Contains("offset 5", r.Error);
        Assert.Contains("0x80", r.Error);
    }

    [Fact]
    public void TruncatedMultiByteSequenceAtEnd_IsRefused()
    {
        // "€" is E2 82 AC; drop the last byte. Final-block decoding must not accept the fragment.
        var bytes = Encoding.UTF8.GetBytes("ab").Concat(new byte[] { 0xE2, 0x82 }).ToArray();
        var r = Read(bytes);
        Assert.False(r.Ok);
        Assert.Contains("offset 2", r.Error);   // where the broken sequence STARTS
    }

    [Fact]
    public void NonAsciiText_IsAccepted()
    {
        var r = Read("echo héllo € 日本");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("echo héllo € 日本", r.Text);
    }

    [Fact]
    public void LeadingBom_IsStripped_NotTyped()
    {
        // A BOM is an encoding signature (Notepad, PS5 redirection), not text; typed it is an
        // invisible zero-width character in front of the command.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("dir")).ToArray();
        var r = Read(bytes);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("dir", r.Text);
    }

    [Fact]
    public void BomInTheMiddle_IsKept()
    {
        // Only a LEADING BOM is a signature; U+FEFF elsewhere is content and stays content.
        var r = Read("a\uFEFFb");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("a\uFEFFb", r.Text);
    }

    [Fact]
    public void EmptyStream_IsEmptyText_NotAnError()
    {
        var r = Read(Array.Empty<byte>());
        Assert.True(r.Ok, r.Error);
        Assert.Equal("", r.Text);
        Assert.Equal("", Read("\n").Text);   // a lone newline is "nothing, plus the newline the shell added"
    }

    [Fact]
    public void ControlBytes_PassThroughToTheServer_WhereHandleTypeDecides()
    {
        // The reader is an encoding check, not a content check: a control byte is valid UTF-8 and
        // is delivered to session.type, whose #213 refusal (and --allow-control) then applies unchanged.
        var r = Read("wait\u0003");
        Assert.True(r.Ok, r.Error);
        var server = new ControlServer(new TerminalSession(40, 6));
        string refused = server.Dispatch("{\"cmd\":\"session.type\",\"args\":{\"text\":" + System.Text.Json.JsonSerializer.Serialize(r.Text) + "}}");
        Assert.Contains("\"ok\":false", refused);
        Assert.Contains("allow-control", refused);
        string allowed = server.Dispatch("{\"cmd\":\"session.type\",\"args\":{\"allow-control\":true,\"text\":" + System.Text.Json.JsonSerializer.Serialize(r.Text) + "}}");
        Assert.DoesNotContain("refuses", allowed);
    }
}
