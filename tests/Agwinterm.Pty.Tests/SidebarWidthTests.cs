using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// sidebar.width, AND A SIDEBAR OP THAT REFUSES WHAT IT CANNOT DO (P2, "stop lying to the caller").
///
/// The reply to a set is the width actually in effect, as an object — not the word "sidebar" — so a
/// caller compares what it asked for with what it got. Out of range is refused with the range named
/// and the width does not move; a set while the sidebar is hidden is remembered, not applied, and
/// the reply says so. One level up, the <c>sidebar</c> verb used to answer ok:true for ANY op
/// (the host's switch fell through), which is how the conformance file's <c>sidebar on</c> step sat
/// green while doing nothing: unknown ops are now refused and on/off are real aliases of show/hide.
/// As with #213's refusals, every refusal is asserted twice: the reply, and that nothing changed.
/// </summary>
public class SidebarWidthTests
{
    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    /// <summary>Dispatch <c>sidebar</c> with a raw JSON args object, so the width can be a float or a string.</summary>
    private static JsonElement Sidebar(ControlServer server, string argsJson)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"sidebar\",\"args\":" + argsJson + "}")).RootElement;

    private static JsonElement Width(ControlServer server, string? widthJson = null)
        => Sidebar(server, widthJson is null ? "{\"op\":\"width\"}" : "{\"op\":\"width\",\"width\":" + widthJson + "}");

    private static JsonElement Op(ControlServer server, string op) => Sidebar(server, "{\"op\":" + JsonSerializer.Serialize(op) + "}");

    private static bool Ok(JsonElement r) => r.GetProperty("ok").GetBoolean();
    private static string Error(JsonElement r) => r.GetProperty("error").GetString() ?? "";
    private static JsonElement Result(JsonElement r) => r.GetProperty("result");
    private static string State(ControlServer server) => Result(Op(server, "state")).GetString() ?? "";
    private static bool WindowSidebarVisible(ControlServer server)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"window.state\"}")).RootElement
            .GetProperty("result").GetProperty("sidebarVisible").GetBoolean();

    // ---- a set is reported back, as an object carrying the width in effect ----

    [Fact]
    public void Set_RepliesWithTheWidthInEffect_AndTheHostHasIt()
    {
        var (server, host) = New();
        var r = Width(server, "300");
        Assert.True(Ok(r));
        Assert.Equal(JsonValueKind.Object, Result(r).ValueKind);   // OkRaw: an object, not "sidebar"
        Assert.Equal(300, Result(r).GetProperty("width").GetInt32());
        Assert.True(Result(r).GetProperty("visible").GetBoolean());
        Assert.True(Result(r).GetProperty("applied").GetBoolean());
        Assert.False(Result(r).TryGetProperty("note", out _));     // nothing to explain: it was applied
        Assert.Equal(300, host.SidebarW);
    }

    [Fact]
    public void Read_NoArgument_ReportsTheCurrentWidth_AndIsNotASet()
    {
        var (server, host) = New();
        var r = Width(server);
        Assert.True(Ok(r));
        Assert.Equal(SidebarWidths.Default, Result(r).GetProperty("width").GetInt32());
        Assert.True(Result(r).GetProperty("visible").GetBoolean());
        Assert.False(Result(r).TryGetProperty("applied", out _));   // a read applied nothing, and does not claim to
        Assert.Equal(SidebarWidths.Default, host.SidebarW);

        Assert.True(Ok(Width(server, "333")));
        Assert.Equal(333, Result(Width(server)).GetProperty("width").GetInt32());
    }

    [Theory]
    [InlineData(SidebarWidths.Min)]
    [InlineData(SidebarWidths.Max)]
    public void TheRangeEnds_AreAccepted(int width)
    {
        var (server, host) = New();
        var r = Width(server, width.ToString());
        Assert.True(Ok(r));
        Assert.Equal(width, Result(r).GetProperty("width").GetInt32());
        Assert.Equal(width, host.SidebarW);
    }

    // ---- refusals: the reply, and then that the width did not move ----

    [Theory]
    [InlineData("0")]          // "0 = hide" is a guess the API does not make: sidebar hide is how to say that
    [InlineData("-5")]
    [InlineData("119")]        // one below Min
    [InlineData("601")]        // one above Max
    [InlineData("100000")]
    public void OutOfRange_IsRefused_NamesTheValueAndRange_AndTheWidthDoesNotMove(string widthJson)
    {
        var (server, host) = New();
        Assert.True(Ok(Width(server, "300")));   // start away from the default, so "did not move" is not "is the default"
        string stateBefore = State(server);

        var r = Width(server, widthJson);
        Assert.False(Ok(r));
        string err = Error(r);
        Assert.Contains(widthJson, err);
        Assert.Contains($"{SidebarWidths.Min}..{SidebarWidths.Max}", err);
        Assert.Contains("sidebar hide", err);      // the escape hatch for the caller who meant "none"
        Assert.Contains("Nothing changed", err);

        Assert.Equal(300, host.SidebarW);
        Assert.Equal(300, Result(Width(server)).GetProperty("width").GetInt32());
        Assert.Equal(stateBefore, State(server));
    }

    [Theory]
    [InlineData("\"300\"")]    // the JSON string "300": not coerced
    [InlineData("\"wide\"")]
    [InlineData("300.5")]      // a float
    [InlineData("true")]
    [InlineData("null")]
    public void NonInteger_IsRefused_AndTheWidthDoesNotMove(string widthJson)
    {
        var (server, host) = New();
        var r = Width(server, widthJson);
        Assert.False(Ok(r));
        Assert.Contains($"{SidebarWidths.Min}..{SidebarWidths.Max}", Error(r));
        Assert.Equal(SidebarWidths.Default, host.SidebarW);
    }

    // ---- sidebar state carries the width ----

    [Fact]
    public void State_CarriesVisibilityModeAndWidth()
    {
        var (server, _) = New();
        Assert.Equal($"visible tree {SidebarWidths.Default}", State(server));
        Assert.True(Ok(Width(server, "300")));
        Assert.Equal("visible tree 300", State(server));
        Assert.True(Ok(Op(server, "hide")));
        Assert.Equal("hidden tree 300", State(server));   // "hidden" says it is not on screen; 300 is what show will use
    }

    // ---- a set while hidden is remembered, not applied, and the reply says so ----

    [Fact]
    public void SetWhileHidden_IsRemembered_ReportedAsNotApplied_AndAppliedOnShow()
    {
        var (server, host) = New();
        Assert.True(Ok(Op(server, "hide")));
        var r = Width(server, "400");
        Assert.True(Ok(r));
        Assert.Equal(400, Result(r).GetProperty("width").GetInt32());
        Assert.False(Result(r).GetProperty("visible").GetBoolean());
        Assert.False(Result(r).GetProperty("applied").GetBoolean());
        Assert.Contains("hidden", Result(r).GetProperty("note").GetString());
        Assert.Contains("sidebar show", Result(r).GetProperty("note").GetString());
        Assert.Equal(400, host.SidebarW);   // remembered

        Assert.True(Ok(Op(server, "show")));
        var shown = Width(server);
        Assert.Equal(400, Result(shown).GetProperty("width").GetInt32());
        Assert.True(Result(shown).GetProperty("visible").GetBoolean());
    }

    [Fact]
    public void ReadWhileHidden_SaysHidden()
    {
        var (server, _) = New();
        Assert.True(Ok(Op(server, "hide")));
        var r = Width(server);
        Assert.True(Ok(r));
        Assert.False(Result(r).GetProperty("visible").GetBoolean());
        Assert.False(Result(r).TryGetProperty("applied", out _));
    }

    // ---- the verb one level up: an unknown op is refused, on/off are show/hide ----

    [Theory]
    [InlineData("sideways")]
    [InlineData("mode:sideways")]
    [InlineData("width:300")]
    [InlineData("")]
    public void UnknownOp_IsRefused_AndNothingChanged(string op)
    {
        var (server, host) = New();
        string before = State(server);
        var r = Op(server, op);
        Assert.False(Ok(r));
        Assert.Contains($"'{op}'", Error(r));
        Assert.Contains("show|hide|toggle", Error(r));   // the list of what it can do
        Assert.Contains("Nothing changed", Error(r));
        Assert.True(host.SidebarVisible);
        Assert.Equal(before, State(server));
    }

    [Fact]
    public void KnownOps_StillAnswerOk()
    {
        var (server, _) = New();
        foreach (var op in new[] { "show", "hide", "toggle", "expand", "collapse", "mode:tree", "mode:flagged", "mode:toggle" })
            Assert.True(Ok(Op(server, op)), op);
    }

    [Fact]
    public void OnAndOff_BehaveAsShowAndHide()
    {
        var (server, host) = New();
        var off = Op(server, "off");
        Assert.True(Ok(off));
        Assert.Equal("sidebar", Result(off).GetString());   // the conformance step's `result: string` still holds
        Assert.False(host.SidebarVisible);
        Assert.False(WindowSidebarVisible(server));

        var on = Op(server, "on");
        Assert.True(Ok(on));
        Assert.True(host.SidebarVisible);
        Assert.True(WindowSidebarVisible(server));
    }

    // ---- the strict reader on its own ----

    [Fact]
    public void TrySidebarWidth_AbsentIsARead_NotZero()
    {
        var args = JsonDocument.Parse("{\"op\":\"width\"}").RootElement;
        Assert.True(ControlServer.TrySidebarWidth(args, out int? width, out string? error));
        Assert.Null(width);
        Assert.Null(error);
    }

    [Fact]
    public void TrySidebarWidth_NoArgsObjectAtAll_IsARead()
    {
        var args = JsonDocument.Parse("null").RootElement;
        Assert.True(ControlServer.TrySidebarWidth(args, out int? width, out _));
        Assert.Null(width);
    }
}
