using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// session.context — A SESSION'S ONE-LINE "WHAT IS THIS PANE FOR", SET OVER THE API (P3).
///
/// The reply is an object naming the session the value landed on and the value IN EFFECT
/// (<c>{session, context}</c>), and <c>tree</c> reads it back as <c>context</c> — P2 spent a round
/// learning that a write without a read-back is only honest in the instant of the call. The value
/// is one line: a control character (the #213 class — a newline in the title bar is a rendering
/// accident, not a context) is refused with its offset, blank is refused unless the caller says
/// <c>clear</c>, text beside <c>clear</c> is refused as two sources for one field, and there is a
/// display-budget ceiling (<see cref="SessionContexts.MaxLength"/>). As with #213's refusals, every
/// refusal is asserted twice: the reply, and that the old value stands.
///
/// Resolution is <c>session.rename</c>'s (the app's FindSesForTarget), and the last test pins the
/// #228 item 3 split: a scratch cover id lands on the session it covers for BOTH verbs — a CLI inside
/// a scratch pane inherits the cover's id, and "this session" is the one under it — while the
/// session-only verbs (close) refuse the same id, exactly as the app does.
/// </summary>
public class SessionContextTests
{
    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    /// <summary>Dispatch <c>session.context</c> with a raw JSON args object (so <c>clear</c> can be a
    /// bool and the text can carry anything).</summary>
    private static JsonElement Context(ControlServer server, string? target, string argsJson)
    {
        var sb = new System.Text.StringBuilder("{\"cmd\":\"session.context\"");
        if (target is not null) sb.Append(",\"target\":").Append(JsonSerializer.Serialize(target));
        sb.Append(",\"args\":").Append(argsJson).Append('}');
        return JsonDocument.Parse(server.Dispatch(sb.ToString())).RootElement;
    }

    private static JsonElement Set(ControlServer server, string text, string? target = null)
        => Context(server, target, "{\"context\":" + JsonSerializer.Serialize(text) + "}");

    private static JsonElement Clear(ControlServer server, string? target = null)
        => Context(server, target, "{\"clear\":true}");

    private static bool Ok(JsonElement r) => r.GetProperty("ok").GetBoolean();
    private static string Error(JsonElement r) => r.GetProperty("error").GetString() ?? "";
    private static JsonElement Result(JsonElement r) => r.GetProperty("result");

    /// <summary>The first session's node from <c>tree</c> — the read-back a caller has after the fact.</summary>
    private static JsonElement TreeSession(ControlServer server, int index = 0)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"tree\"}")).RootElement
            .GetProperty("result").GetProperty("workspaces")[0].GetProperty("sessions")[index];

    private static string? TreeContext(ControlServer server, int index = 0)
        => TreeSession(server, index).TryGetProperty("context", out var v) ? v.GetString() : null;

    // ---- set: the reply names the session and the value, and the tree carries it ----

    [Fact]
    public void Set_RepliesWithSessionAndText_AndTheTreeCarriesIt()
    {
        var (server, host) = New();
        Assert.Null(TreeContext(server));   // nothing before: the key is absent, not ""
        var r = Set(server, "builds the docs site");
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(JsonValueKind.Object, Result(r).ValueKind);   // OkRaw: an object, not a word
        Assert.Equal("s1", Result(r).GetProperty("session").GetString());
        Assert.Equal("builds the docs site", Result(r).GetProperty("context").GetString());
        Assert.Equal("builds the docs site", host.ActiveSess!.Context);
        Assert.Equal("builds the docs site", TreeContext(server));
    }

    [Fact]
    public void Set_OnANamedTarget_LandsOnThatSession_NotTheActiveOne()
    {
        var (server, host) = New();
        var r2 = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.new\",\"args\":{\"name\":\"second\",\"no-select\":true}}")).RootElement;
        string s2 = r2.GetProperty("result").GetString()!;
        Assert.Same(host.Workspaces[0].Sessions[0], host.ActiveSess);   // still the first

        var r = Set(server, "the other one", target: "second");   // by name
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(s2, Result(r).GetProperty("session").GetString());   // the reply names the session it LANDED on
        Assert.Null(host.ActiveSess!.Context);
        Assert.Equal("the other one", host.Workspaces[0].Sessions[1].Context);
        Assert.Null(TreeContext(server, 0));
        Assert.Equal("the other one", TreeContext(server, 1));
    }

    [Fact]
    public void Set_Twice_Replaces()
    {
        var (server, host) = New();
        Set(server, "first");
        var r = Set(server, "second");
        Assert.Equal("second", Result(r).GetProperty("context").GetString());
        Assert.Equal("second", host.ActiveSess!.Context);
        Assert.Equal("second", TreeContext(server));
    }

    // ---- clear: the reply says null, the tree omits the key ----

    [Fact]
    public void Clear_RepliesContextNull_AndTheTreeOmitsIt()
    {
        var (server, host) = New();
        Set(server, "temporary");
        var r = Clear(server);
        Assert.True(Ok(r), r.ToString());
        Assert.Equal("s1", Result(r).GetProperty("session").GetString());
        Assert.Equal(JsonValueKind.Null, Result(r).GetProperty("context").ValueKind);   // present and null: "there is none now"
        Assert.Null(host.ActiveSess!.Context);
        Assert.False(TreeSession(server).TryGetProperty("context", out _));   // absent, as the flags spell "no"
    }

    [Fact]
    public void Clear_WithNothingSet_IsOk_NotARefusal()
    {
        var (server, _) = New();
        var r = Clear(server);
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(JsonValueKind.Null, Result(r).GetProperty("context").ValueKind);
    }

    // ---- refusals: reported, and nothing changed ----

    [Fact]
    public void UnknownTarget_IsRefused_AndNoSessionChanged()
    {
        var (server, host) = New();
        Set(server, "kept");
        var r = Set(server, "lost", target: "no-such-session");
        Assert.False(Ok(r));
        Assert.Equal(SessionContexts.NoSession, Error(r));   // the wording rename's condition has
        Assert.Contains("session not found", Error(r));
        Assert.Equal("kept", host.ActiveSess!.Context);
        Assert.All(host.Workspaces.SelectMany(w => w.Sessions), s => Assert.NotEqual("lost", s.Context));
    }

    [Fact]
    public void Blank_IsRefused_AndTheOldValueStands()
    {
        var (server, host) = New();
        Set(server, "kept");
        foreach (var blank in new[] { "", "   " })
        {
            var r = Set(server, blank);
            Assert.False(Ok(r), blank);
            Assert.Contains("blank", Error(r));
            Assert.Contains("--clear", Error(r));   // the way to remove one is named
            Assert.Contains("Nothing changed", Error(r));
            Assert.Equal("kept", host.ActiveSess!.Context);
        }
        Assert.Equal("kept", TreeContext(server));
    }

    [Fact]
    public void WhitespaceOnlyControlCharacters_AreRefusedAsControl_NotSilentlyTrimmedToBlank()
    {
        // .NET's Trim eats a tab and a NEL as whitespace; to a title bar they are control characters.
        // The control check runs on the text as GIVEN, so a caller who sent one is told what they sent.
        var (server, host) = New();
        Set(server, "kept");
        foreach (var (text, offset) in new[] { ("\t\t", 0), ("trailing\u0085", 8), ("\nlead", 0) })
        {
            var r = Set(server, text);
            Assert.False(Ok(r), text);
            Assert.Contains("control character", Error(r));
            Assert.Contains("offset " + offset, Error(r));
            Assert.Equal("kept", host.ActiveSess!.Context);
        }
    }

    [Fact]
    public void NoTextAndNoClear_IsRefusedAsBlank()
    {
        var (server, host) = New();
        Set(server, "kept");
        var r = Context(server, null, "{}");
        Assert.False(Ok(r));
        Assert.Equal(SessionContexts.Blank, Error(r));
        Assert.Equal("kept", host.ActiveSess!.Context);
    }

    [Theory]
    [InlineData("line one\nline two", 8, "000A")]     // newline: the #213 class
    [InlineData("tab\there", 3, "0009")]              // tab
    [InlineData("\u001b[31mred", 0, "001B")]     // escape sequence
    [InlineData("del\u007fhere", 3, "007F")]      // DEL
    [InlineData("c1\u0085next", 2, "0085")]       // a C1 control (NEL)
    public void ControlCharacter_IsRefusedWithItsOffset_AndTheOldValueStands(string text, int offset, string codepoint)
    {
        var (server, host) = New();
        Set(server, "kept");
        var r = Set(server, text);
        Assert.False(Ok(r), text);
        Assert.Contains("control character", Error(r));
        Assert.Contains("U+" + codepoint, Error(r));
        Assert.Contains("offset " + offset, Error(r));
        Assert.Contains("Nothing changed", Error(r));
        Assert.Equal("kept", host.ActiveSess!.Context);
        Assert.Equal("kept", TreeContext(server));
    }

    [Fact]
    public void ControlCharacter_OffsetIndexesTheTextAsGiven_NotTheTrimmedText()
    {
        // Leading whitespace is trimmed on success, but a refusal must point at the caller's string:
        // the offset they can find with their editor is the one in what they sent.
        var (server, _) = New();
        var r = Set(server, "  ab\ncd");
        Assert.False(Ok(r));
        Assert.Contains("offset 4", Error(r));
    }

    [Fact]
    public void OverLength_IsRefusedNamingTheCeiling_AndExactlyTheCeilingIsAccepted()
    {
        var (server, host) = New();
        Set(server, "kept");
        string atCeiling = new string('x', SessionContexts.MaxLength);
        string over = new string('x', SessionContexts.MaxLength + 1);

        var refused = Set(server, over);
        Assert.False(Ok(refused));
        Assert.Contains((SessionContexts.MaxLength + 1).ToString(), Error(refused));
        Assert.Contains("ceiling of " + SessionContexts.MaxLength, Error(refused));
        Assert.Contains("Nothing changed", Error(refused));
        Assert.Equal("kept", host.ActiveSess!.Context);

        var accepted = Set(server, atCeiling);
        Assert.True(Ok(accepted), accepted.ToString());
        Assert.Equal(atCeiling, host.ActiveSess!.Context);
        Assert.Equal(atCeiling, TreeContext(server));
    }

    [Fact]
    public void TextAndClearTogether_IsRefused_AndNothingChanged()
    {
        var (server, host) = New();
        Set(server, "kept");
        var r = Context(server, null, "{\"context\":\"new\",\"clear\":true}");
        Assert.False(Ok(r));
        Assert.Equal(SessionContexts.TextAndClear, Error(r));
        Assert.Equal("kept", host.ActiveSess!.Context);   // neither cleared nor replaced
        Assert.Equal("kept", TreeContext(server));
    }

    [Fact]
    public void ClearFalse_WithText_IsASet()
    {
        // `clear:false` is "not clearing", not "clearing" — the flag's absence and false are the same.
        var (server, host) = New();
        var r = Context(server, null, "{\"context\":\"set anyway\",\"clear\":false}");
        Assert.True(Ok(r), r.ToString());
        Assert.Equal("set anyway", host.ActiveSess!.Context);
    }

    // ---- normalisation ----

    [Fact]
    public void Whitespace_IsTrimmed_AndTheReplyCarriesTheTrimmedValue()
    {
        var (server, host) = New();
        var r = Set(server, "   padded by a shell   ");
        Assert.True(Ok(r), r.ToString());
        Assert.Equal("padded by a shell", Result(r).GetProperty("context").GetString());   // the value IN EFFECT, not the request
        Assert.Equal("padded by a shell", host.ActiveSess!.Context);
        Assert.Equal("padded by a shell", TreeContext(server));
    }

    [Fact]
    public void InteriorSpaces_AndNonAscii_Survive()
    {
        var (server, host) = New();
        string text = "two  spaces · émoji 🚀 ok";
        var r = Set(server, text);
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(text, host.ActiveSess!.Context);
        Assert.Equal(text, TreeContext(server));
    }

    [Fact]
    public void Rename_DoesNotTouchTheContext_AndViceVersa()
    {
        var (server, host) = New();
        Set(server, "the context");
        JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.rename\",\"args\":{\"name\":\"renamed\"}}"));
        Assert.Equal("renamed", host.ActiveSess!.Name);
        Assert.Equal("the context", host.ActiveSess!.Context);
        Assert.Equal("the context", TreeContext(server));
        Assert.Equal("renamed", TreeSession(server).GetProperty("name").GetString());
    }

    // ---- the #228 item 3 resolver split: context resolves exactly as rename does ----

    /// <summary>A CLI launched inside a scratch pane inherits the cover's id as AGWINTERM_SESSION_ID.
    /// In the app, rename's resolver (FindSesForTarget) lands a cover id on the session it covers;
    /// session.context uses the same resolver and must land in the same place — and the session-only
    /// resolver behind close must NOT reach it, which is where the fake used to diverge from the app.</summary>
    [Fact]
    public void CoverPaneId_ResolvesForContextExactlyAsForRename_AndNotForTheSessionOnlyVerbs()
    {
        var (server, host) = New();
        var sess = host.Workspaces[0].Sessions[0];
        string cover = sess.AddCoverPane();

        var rename = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.rename\",\"target\":" + JsonSerializer.Serialize(cover) + ",\"args\":{\"name\":\"via cover\"}}")).RootElement;
        Assert.True(rename.GetProperty("ok").GetBoolean(), rename.ToString());
        Assert.Equal("via cover", sess.Name);   // rename on the cover id renamed the covering session

        var ctx = Set(server, "set via cover", target: cover);
        Assert.True(Ok(ctx), ctx.ToString());                                  // the same resolution: not refused
        Assert.Equal(sess.Id, Result(ctx).GetProperty("session").GetString()); // and the reply names the SESSION, not the cover
        Assert.Equal("set via cover", sess.Context);
        Assert.Equal("set via cover", TreeContext(server));

        // The session-only resolver (the app's Find: id, prefix, name — never a pane, never a cover)
        // refuses the same id, so `session.close <cover id>` cannot close the covering session.
        var close = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.close\",\"target\":" + JsonSerializer.Serialize(cover) + "}")).RootElement;
        Assert.False(close.GetProperty("ok").GetBoolean(), close.ToString());
        Assert.Single(host.Workspaces[0].Sessions);
        Assert.Equal("set via cover", sess.Context);
    }

    [Fact]
    public void SplitPaneId_ResolvesForContext_AndNotForTheSessionOnlyVerbs()
    {
        // The app's Find never matches a split pane's own id; FindSesForTarget does (exact pane).
        var (server, host) = New();
        var sess = host.Workspaces[0].Sessions[0];
        sess.AddPane();
        string p1 = sess.PaneIds[1];

        var ctx = Set(server, "by pane id", target: p1);
        Assert.True(Ok(ctx), ctx.ToString());
        Assert.Equal(sess.Id, Result(ctx).GetProperty("session").GetString());
        Assert.Equal("by pane id", sess.Context);

        var select = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.select\",\"target\":" + JsonSerializer.Serialize(p1) + "}")).RootElement;
        Assert.False(select.GetProperty("ok").GetBoolean(), select.ToString());
    }

    // ---- the rules class on its own, for the restore loader that will apply it to a file value ----

    [Fact]
    public void Rules_ValidateAndNormalize_StandAlone()
    {
        Assert.Null(SessionContexts.Validate("fine"));
        Assert.Equal(SessionContexts.Blank, SessionContexts.Validate(null));
        Assert.Equal(SessionContexts.Blank, SessionContexts.Validate(""));
        Assert.Equal(SessionContexts.Blank, SessionContexts.Validate("  "));
        Assert.Contains("U+000A at offset 1", SessionContexts.Validate("a\nb"));
        Assert.Contains("ceiling of 200", SessionContexts.Validate(new string('y', 201)));
        Assert.Null(SessionContexts.Validate(new string('y', 200)));
        Assert.Equal("x y", SessionContexts.Normalize(" x y \r\n"));
        Assert.True(SessionContexts.TryNormalize("  ok  ", out var text, out var refusal));
        Assert.Equal("ok", text); Assert.Null(refusal);
        Assert.False(SessionContexts.TryNormalize(null, out _, out refusal));
        Assert.Equal(SessionContexts.Blank, refusal);
        Assert.Equal("{\"session\":\"s1\",\"context\":\"t\"}", SessionContexts.Reply("s1", "t"));
        Assert.Equal("{\"session\":\"s1\",\"context\":null}", SessionContexts.Reply("s1", null));
    }
}
