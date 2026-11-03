using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// session.link — the one http(s) URL a session is about (a tracker issue, a pull request). Set,
/// replaced and cleared over the API, read back in <c>tree</c> as <c>link</c>, opened by
/// <c>session.link.open</c>, and settable on <c>session.new</c>. Only absolute http/https URLs are
/// accepted: the link is handed to the shell. Every refusal is asserted twice: the reply, and that
/// the old value stands.
/// </summary>
public class SessionLinkTests
{
    private const string Jira = "https://jira.example.com/browse/PROJ-1234";
    private const string Pr = "https://github.com/acme/repo/pull/366";

    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    private static JsonElement Send(ControlServer server, string cmd, string? target, string argsJson)
    {
        var sb = new System.Text.StringBuilder("{\"cmd\":").Append(JsonSerializer.Serialize(cmd));
        if (target is not null) sb.Append(",\"target\":").Append(JsonSerializer.Serialize(target));
        sb.Append(",\"args\":").Append(argsJson).Append('}');
        return JsonDocument.Parse(server.Dispatch(sb.ToString())).RootElement;
    }

    private static JsonElement Set(ControlServer server, string url, string? target = null)
        => Send(server, "session.link", target, "{\"link\":" + JsonSerializer.Serialize(url) + "}");

    private static bool Ok(JsonElement r) => r.GetProperty("ok").GetBoolean();
    private static string Error(JsonElement r) => r.GetProperty("error").GetString() ?? "";
    private static JsonElement Result(JsonElement r) => r.GetProperty("result");

    private static string? TreeLink(ControlServer server, int index = 0)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"tree\"}")).RootElement
            .GetProperty("result").GetProperty("workspaces")[0].GetProperty("sessions")[index]
            .TryGetProperty("link", out var v) ? v.GetString() : null;

    [Fact]
    public void Set_RepliesWithSessionAndUrl_AndTheTreeCarriesIt()
    {
        var (server, host) = New();
        Assert.Null(TreeLink(server));
        var r = Set(server, "  " + Jira + "  ");   // padding is trimmed
        Assert.True(Ok(r), r.ToString());
        Assert.Equal("s1", Result(r).GetProperty("session").GetString());
        Assert.Equal(Jira, Result(r).GetProperty("link").GetString());
        Assert.Equal(Jira, host.ActiveSess!.Link);
        Assert.Equal(Jira, TreeLink(server));
    }

    [Fact]
    public void Set_Twice_Replaces_AndClearRemovesTheKey()
    {
        var (server, host) = New();
        Set(server, Jira);
        Assert.Equal(Pr, Result(Set(server, Pr)).GetProperty("link").GetString());
        Assert.Equal(Pr, TreeLink(server));
        var r = Send(server, "session.link", null, "{\"clear\":true}");
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(JsonValueKind.Null, Result(r).GetProperty("link").ValueKind);
        Assert.Null(host.ActiveSess!.Link);
        Assert.Null(TreeLink(server));
    }

    [Theory]
    [InlineData("", "blank")]
    [InlineData("   ", "blank")]
    [InlineData("file:///C:/Windows/System32/calc.exe", "http")]
    [InlineData("javascript:alert(1)", "http")]
    [InlineData("PROJ-1234", "http")]
    [InlineData("www.example.com/x", "http")]
    [InlineData("https://example.com/a b", "whitespace U+0020 at offset 21")]
    [InlineData("https://example.com/a\tb", "control character U+0009 at offset 21")]
    [InlineData("https://example.com/\n", "control character U+000A at offset 20")]
    public void BadUrls_AreRefused_AndTheOldLinkStands(string url, string expected)
    {
        var (server, host) = New();
        Set(server, Jira);
        var r = Set(server, url);
        Assert.False(Ok(r));
        Assert.Contains(expected, Error(r));
        Assert.Contains("Nothing changed", Error(r));
        Assert.Equal(Jira, host.ActiveSess!.Link);
    }

    [Fact]
    public void OverTheCeiling_IsRefused()
    {
        var (server, host) = New();
        string url = "https://example.com/" + new string('a', SessionLinks.MaxLength);
        var r = Set(server, url);
        Assert.False(Ok(r));
        Assert.Contains("ceiling of " + SessionLinks.MaxLength, Error(r));
        Assert.Null(host.ActiveSess!.Link);
    }

    [Fact]
    public void AtTheCeiling_IsAccepted()
    {
        var (server, host) = New();
        string url = "https://example.com/" + new string('a', SessionLinks.MaxLength - "https://example.com/".Length);
        Assert.Equal(SessionLinks.MaxLength, url.Length);
        Assert.True(Ok(Set(server, url)));
        Assert.Equal(url, host.ActiveSess!.Link);
    }

    [Fact]
    public void UrlBesideClear_IsRefused()
    {
        var (server, host) = New();
        Set(server, Jira);
        var r = Send(server, "session.link", null, "{\"link\":" + JsonSerializer.Serialize(Pr) + ",\"clear\":true}");
        Assert.False(Ok(r));
        Assert.Equal(SessionLinks.TextAndClear, Error(r));
        Assert.Equal(Jira, host.ActiveSess!.Link);
    }

    [Fact]
    public void Set_OnANamedTarget_LandsOnThatSession()
    {
        var (server, host) = New();
        server.Dispatch("{\"cmd\":\"session.new\",\"args\":{\"name\":\"second\",\"no-select\":true}}");
        var r = Set(server, Pr, target: "second");
        Assert.True(Ok(r), r.ToString());
        Assert.Null(host.ActiveSess!.Link);
        Assert.Equal(Pr, TreeLink(server, 1));
    }

    [Fact]
    public void UnknownTarget_IsSessionNotFound()
    {
        var (server, _) = New();
        var r = Set(server, Jira, target: "no-such-session");
        Assert.False(Ok(r));
        Assert.Equal(SessionLinks.NoSession, Error(r));
    }

    [Fact]
    public void Open_OpensTheLink_AndIsRefusedWithoutOne()
    {
        var (server, host) = New();
        var none = Send(server, "session.link.open", null, "{}");
        Assert.False(Ok(none));
        Assert.Equal(SessionLinks.NoLink, Error(none));
        Assert.Empty(host.ActiveSess!.Opened);

        Set(server, Pr);
        var r = Send(server, "session.link.open", null, "{}");
        Assert.True(Ok(r), r.ToString());
        Assert.True(Result(r).GetProperty("opened").GetBoolean());
        Assert.Equal(Pr, Result(r).GetProperty("link").GetString());
        Assert.Equal(new[] { Pr }, host.ActiveSess!.Opened);
    }

    [Fact]
    public void SessionNew_WithLink_SetsIt_AndABadLinkCreatesNothing()
    {
        var (server, host) = New();
        var bad = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.new\",\"args\":{\"name\":\"x\",\"link\":\"ftp://example.com\"}}")).RootElement;
        Assert.False(Ok(bad));
        Assert.Contains("Nothing created", Error(bad));
        Assert.Single(host.Workspaces[0].Sessions);

        var r = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.new\",\"args\":{\"name\":\"review\",\"no-select\":true,\"link\":" + JsonSerializer.Serialize(Pr) + "}}")).RootElement;
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(Pr, host.Workspaces[0].Sessions[1].Link);
        Assert.Equal(Pr, TreeLink(server, 1));
    }

    [Theory]
    [InlineData("https://github.com/acme/repo/pull/1", true)]
    [InlineData("http://jira.local:8080/browse/X-1?focusedId=2#c", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    [InlineData("https://", false)]
    [InlineData("mailto:a@b.c", false)]
    [InlineData("  https://example.com/x  ", true)]   // spaces around a pasted URL are trimmed
    [InlineData("https://example.com/\n", false)]     // a trailing control character is refused, not trimmed
    [InlineData("https://example.com/\t", false)]
    [InlineData(null, false)]
    public void IsValid(string? url, bool valid) => Assert.Equal(valid, SessionLinks.IsValid(url));
}
