using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// session.new REFUSES AN UNKNOWN WORKSPACE (P2, "stop lying to the caller", decision 1).
///
/// Before P2 an unknown <c>--workspace</c> id, or an unknown <c>--workspace-name</c> without
/// <c>--create-workspace</c>, silently placed the session in the ACTIVE workspace and answered
/// ok:true with an id — the caller believed it had put a session somewhere it had not. Now both are
/// refused with the value named, the escape hatch (<c>--create-workspace</c>) named, and <b>no
/// session created</b>: the host resolves the workspace before it mints an id, so there is no orphan
/// behind the ok:false. Two workspace flags together are refused, not ranked.
///
/// As with #213's refusals, every refusal is asserted twice: the reply, and that the tree did not
/// change. The wording asserted is <see cref="SessionNewWorkspaces"/>, which the app's host shares
/// with the fake, and the fake's FindWs resolves id / id-prefix / "active" and never a name — exactly
/// the app's — so a test here cannot pass against a fake that accepts what the app refuses.
/// </summary>
public class SessionNewWorkspaceTests
{
    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    private static JsonElement Dispatch(ControlServer server, string cmd, object? args = null)
    {
        var sb = new System.Text.StringBuilder("{\"cmd\":").Append(JsonSerializer.Serialize(cmd));
        if (args is not null) sb.Append(",\"args\":").Append(JsonSerializer.Serialize(args));
        sb.Append('}');
        return JsonDocument.Parse(server.Dispatch(sb.ToString())).RootElement;
    }

    private static JsonElement SessionNew(ControlServer server, Dictionary<string, object> args) => Dispatch(server, "session.new", args);

    private static bool Ok(JsonElement r) => r.GetProperty("ok").GetBoolean();
    private static string Error(JsonElement r) => r.GetProperty("error").GetString() ?? "";
    private static string Result(JsonElement r) => r.GetProperty("result").GetString() ?? "";

    /// <summary>The tree as a caller sees it, as one string — equal before and after means nothing changed.</summary>
    private static string TreeText(ControlServer server) => Dispatch(server, "tree").GetProperty("result").GetRawText();

    private static FakeSessionHost.Ws WorkspaceOf(FakeSessionHost host, string sessionId)
        => host.Workspaces.Single(w => w.Sessions.Any(s => s.Id == sessionId));

    private static int SessionCount(FakeSessionHost host) => host.Workspaces.Sum(w => w.Sessions.Count);

    /// <summary>A second workspace beside the fake's default one, so "placed in the other one" is observable.</summary>
    private static string SecondWorkspace(ControlServer server, string name = "build")
        => Result(Dispatch(server, "workspace.new", new { name }));

    // ---- the honoured cases: id, id prefix, name, name + create, nothing ----

    [Fact]
    public void KnownWorkspaceId_PlacesTheSessionThere()
    {
        var (server, host) = New();
        string wid = SecondWorkspace(server);
        var r = SessionNew(server, new() { ["name"] = "in-build", ["workspace"] = wid });
        Assert.True(Ok(r));
        string sid = Result(r);
        Assert.Equal(wid, WorkspaceOf(host, sid).Id);
        Assert.Single(host.Workspaces.First(w => w.Id == "w1").Sessions);   // the active workspace did not get it
        Assert.Equal(2, host.Workspaces.Count);                              // and nothing was created beside it
    }

    [Fact]
    public void WorkspaceIdPrefix_StillResolves()
    {
        var (server, host) = New();
        string wid = SecondWorkspace(server);
        Assert.True(wid.Length > 2);
        var r = SessionNew(server, new() { ["workspace"] = wid[..^1] });   // one character short of the full id
        Assert.True(Ok(r));
        Assert.Equal(wid, WorkspaceOf(host, Result(r)).Id);
    }

    [Fact]
    public void WorkspaceActive_IsTheActiveWorkspace_LikeEveryOtherWorkspaceVerb()
    {
        var (server, host) = New();
        string wid = SecondWorkspace(server);
        host.ActiveWs = host.Workspaces.First(w => w.Id == wid);
        var r = SessionNew(server, new() { ["workspace"] = "active" });
        Assert.True(Ok(r));
        Assert.Equal(wid, WorkspaceOf(host, Result(r)).Id);
    }

    [Fact]
    public void KnownWorkspaceName_PlacesTheSession_WithoutASecondWorkspace()
    {
        var (server, host) = New();
        string wid = SecondWorkspace(server, "CI");
        var r = SessionNew(server, new() { ["workspace-name"] = "ci" });   // case-insensitive, as the sidebar label is matched
        Assert.True(Ok(r));
        Assert.Equal(wid, WorkspaceOf(host, Result(r)).Id);
        Assert.Equal(2, host.Workspaces.Count);
        Assert.Single(host.Workspaces.Where(w => string.Equals(w.Name, "CI", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void KnownWorkspaceName_WithCreateWorkspace_Reuses_DoesNotDuplicate()
    {
        var (server, host) = New();
        string wid = SecondWorkspace(server, "CI");
        var r = SessionNew(server, new() { ["workspace-name"] = "CI", ["create-workspace"] = true });
        Assert.True(Ok(r));
        Assert.Equal(wid, WorkspaceOf(host, Result(r)).Id);
        Assert.Equal(2, host.Workspaces.Count);
    }

    [Fact]
    public void UnknownWorkspaceName_WithCreateWorkspace_CreatesBoth()
    {
        var (server, host) = New();
        var r = SessionNew(server, new() { ["name"] = "first", ["workspace-name"] = "fresh", ["create-workspace"] = true });
        Assert.True(Ok(r));
        var ws = WorkspaceOf(host, Result(r));
        Assert.Equal("fresh", ws.Name);
        Assert.NotEqual("w1", ws.Id);
        Assert.Equal(2, host.Workspaces.Count);
        Assert.Single(ws.Sessions);
        // and the caller can see it in the tree, in the new workspace, not the old one
        var tree = Dispatch(server, "tree").GetProperty("result").GetProperty("workspaces");
        var created = tree.EnumerateArray().Single(w => w.GetProperty("name").GetString() == "fresh");
        Assert.Equal("first", created.GetProperty("sessions")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void NoWorkspaceArgument_UsesTheActiveWorkspace()
    {
        var (server, host) = New();
        string wid = SecondWorkspace(server);
        host.ActiveWs = host.Workspaces.First(w => w.Id == wid);
        var r = SessionNew(server, new() { ["name"] = "here" });
        Assert.True(Ok(r));
        Assert.Equal(wid, WorkspaceOf(host, Result(r)).Id);
        Assert.Equal(2, host.Workspaces.Count);
    }

    // ---- the refusals: each asserted twice, the reply and the untouched tree ----

    [Fact]
    public void UnknownWorkspaceId_IsRefused_NamingTheValue()
    {
        var (server, _) = New();
        var r = SessionNew(server, new() { ["workspace"] = "no-such-workspace" });
        Assert.False(Ok(r));
        Assert.False(r.TryGetProperty("result", out _));   // no id at all, not an id beside an error
        Assert.Equal(SessionNewWorkspaces.UnknownId("no-such-workspace"), Error(r));
        Assert.Contains("'no-such-workspace'", Error(r));
        Assert.Contains("--create-workspace", Error(r));      // the escape hatch is named (#214)
        Assert.Contains("No session was created", Error(r));
    }

    [Fact]
    public void UnknownWorkspaceId_CreatesNothing_TreeUnchanged()
    {
        var (server, host) = New();
        SecondWorkspace(server);
        string before = TreeText(server);
        int sessions = SessionCount(host);
        var activeSess = host.ActiveSess; var activeWs = host.ActiveWs;

        Assert.False(Ok(SessionNew(server, new() { ["name"] = "orphan?", ["workspace"] = "no-such-workspace" })));

        Assert.Equal(before, TreeText(server));
        Assert.Equal(sessions, SessionCount(host));
        Assert.Same(activeSess, host.ActiveSess);
        Assert.Same(activeWs, host.ActiveWs);
        Assert.DoesNotContain(host.Workspaces.SelectMany(w => w.Sessions), s => s.Name == "orphan?");
    }

    [Fact]
    public void WorkspaceIdIsNotAName_ANameOnTheIdFlagIsRefused()
    {
        // The pre-P2 fake accepted a name here and the app did not; the fake now refuses as the app does.
        var (server, host) = New();
        SecondWorkspace(server, "CI");
        string before = TreeText(server);
        var r = SessionNew(server, new() { ["workspace"] = "CI" });
        Assert.False(Ok(r));
        Assert.Equal(SessionNewWorkspaces.UnknownId("CI"), Error(r));
        Assert.Equal(before, TreeText(server));
        Assert.Equal(2, host.Workspaces.Count);
    }

    [Fact]
    public void UnknownWorkspaceName_WithoutCreate_IsRefused_NamingTheFlag()
    {
        var (server, _) = New();
        var r = SessionNew(server, new() { ["workspace-name"] = "nowhere" });
        Assert.False(Ok(r));
        Assert.False(r.TryGetProperty("result", out _));
        Assert.Equal(SessionNewWorkspaces.UnknownName("nowhere"), Error(r));
        Assert.Contains("'nowhere'", Error(r));
        Assert.Contains("--create-workspace", Error(r));   // the caller almost always meant this
        Assert.Contains("No session was created", Error(r));
    }

    [Fact]
    public void UnknownWorkspaceName_WithoutCreate_CreatesNothing_TreeUnchanged()
    {
        var (server, host) = New();
        string before = TreeText(server);
        int sessions = SessionCount(host);

        Assert.False(Ok(SessionNew(server, new() { ["name"] = "orphan?", ["workspace-name"] = "nowhere" })));

        Assert.Equal(before, TreeText(server));
        Assert.Equal(sessions, SessionCount(host));
        Assert.Single(host.Workspaces);   // and no workspace was created without the flag
        Assert.DoesNotContain(host.Workspaces, w => w.Name == "nowhere");
    }

    [Fact]
    public void WorkspaceAndWorkspaceName_Together_AreRefused()
    {
        // Two answers to "where?" — refused, not ranked (before P2 --workspace silently won by being
        // tested first). Same rule as --stdin beside positional text on session type.
        var (server, host) = New();
        string wid = SecondWorkspace(server, "CI");
        string before = TreeText(server);
        var r = SessionNew(server, new() { ["workspace"] = wid, ["workspace-name"] = "CI" });
        Assert.False(Ok(r));
        Assert.Equal(SessionNewWorkspaces.TwoSources(wid, "CI"), Error(r));
        Assert.Contains("--workspace", Error(r));
        Assert.Contains("--workspace-name", Error(r));
        Assert.Equal(before, TreeText(server));   // even though BOTH resolve on their own
    }

    [Fact]
    public void WorkspaceAndWorkspaceName_Together_WithCreate_StillRefused_CreatesNoWorkspace()
    {
        var (server, host) = New();
        string wid = SecondWorkspace(server);
        Assert.False(Ok(SessionNew(server, new() { ["workspace"] = wid, ["workspace-name"] = "fresh", ["create-workspace"] = true })));
        Assert.Equal(2, host.Workspaces.Count);
        Assert.DoesNotContain(host.Workspaces, w => w.Name == "fresh");
    }

    [Fact]
    public void EmptyWorkspaceFlag_IsNotARefusal_ItIsTheActiveWorkspace()
    {
        // "" is how the CLI spells "not given" once it reaches JSON; it must not be refused as an unknown id "".
        var (server, host) = New();
        var r = SessionNew(server, new() { ["workspace"] = "", ["workspace-name"] = "" });
        Assert.True(Ok(r));
        Assert.Equal("w1", WorkspaceOf(host, Result(r)).Id);
    }
}
