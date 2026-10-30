using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class WorkspaceDefaultDirTests
{
    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    private static JsonElement Dispatch(ControlServer server, string json)
        => JsonDocument.Parse(server.Dispatch(json)).RootElement;

    private static bool Ok(JsonElement r) => r.GetProperty("ok").GetBoolean();

    [Fact]
    public void Set_RepliesAndTreeCarriesDefaultDir()
    {
        var (server, host) = New();
        string dir = Environment.CurrentDirectory;
        var r = Dispatch(server, "{\"cmd\":\"workspace.default-dir\",\"args\":{\"defaultDir\":" + JsonSerializer.Serialize(dir) + "}}");
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(dir, r.GetProperty("result").GetProperty("defaultDir").GetString());
        var tree = Dispatch(server, "{\"cmd\":\"tree\"}");
        Assert.Equal(dir, tree.GetProperty("result").GetProperty("workspaces")[0].GetProperty("defaultDir").GetString());
        Assert.Equal(dir, host.ActiveWs.DefaultDir);
    }

    [Fact]
    public void Clear_RemovesDefaultDir()
    {
        var (server, host) = New();
        host.ActiveWs.DefaultDir = Environment.CurrentDirectory;
        var r = Dispatch(server, "{\"cmd\":\"workspace.default-dir\",\"args\":{\"clear\":true}}");
        Assert.True(Ok(r));
        Assert.Null(r.GetProperty("result").GetProperty("defaultDir").GetString());
        Assert.Null(host.ActiveWs.DefaultDir);
    }

    [Fact]
    public void MissingDirectory_IsRefused()
    {
        var (server, host) = New();
        string? before = host.ActiveWs.DefaultDir;
        var r = Dispatch(server, "{\"cmd\":\"workspace.default-dir\",\"args\":{\"defaultDir\":\"Z:\\\\no-such-agwinterm-dir\"}}");
        Assert.False(Ok(r));
        Assert.Equal(before, host.ActiveWs.DefaultDir);
    }

    [Fact]
    public void SessionNew_WithoutCwd_UsesWorkspaceDefault()
    {
        var (server, host) = New();
        string dir = Environment.CurrentDirectory;
        host.ActiveWs.DefaultDir = dir;
        Dispatch(server, "{\"cmd\":\"session.new\",\"args\":{\"name\":\"t\"}}");
        Assert.Equal(dir, host.LastNewSessionCwd);
    }

    [Fact]
    public void WorkspaceNew_WithDefaultDir_SetsField()
    {
        var (server, host) = New();
        string dir = Environment.CurrentDirectory;
        var r = Dispatch(server, "{\"cmd\":\"workspace.new\",\"args\":{\"name\":\"proj\",\"default-dir\":" + JsonSerializer.Serialize(dir) + "}}");
        Assert.True(Ok(r));
        string wid = r.GetProperty("result").GetString()!;
        var ws = host.Workspaces.First(w => w.Id == wid);
        Assert.Equal(WorkspaceDefaultDirs.NormalizeSet(dir), ws.DefaultDir);
    }

    [Fact]
    public void ClearAndPath_Refused()
    {
        var (server, host) = New();
        host.ActiveWs.DefaultDir = Environment.CurrentDirectory;
        var r = Dispatch(server, "{\"cmd\":\"workspace.default-dir\",\"args\":{\"clear\":true,\"defaultDir\":\"C:\\\\Windows\"}}");
        Assert.False(Ok(r));
        Assert.Contains(WorkspaceDefaultDirs.TextAndClear, r.GetProperty("error").GetString());
        Assert.Equal(WorkspaceDefaultDirs.NormalizeSet(Environment.CurrentDirectory), host.ActiveWs.DefaultDir);
    }

    [Fact]
    public void BlankPath_Refused()
    {
        var r = Dispatch(New().server, "{\"cmd\":\"workspace.default-dir\",\"args\":{\"defaultDir\":\"   \"}}");
        Assert.False(Ok(r));
        Assert.Contains(WorkspaceDefaultDirs.Blank, r.GetProperty("error").GetString());
    }

    [Fact]
    public void SessionNew_ExplicitCwd_OverridesWorkspaceDefault()
    {
        var (server, host) = New();
        string defaultDir = Environment.CurrentDirectory;
        string explicitDir = Path.GetTempPath();
        host.ActiveWs.DefaultDir = defaultDir;
        Dispatch(server, "{\"cmd\":\"session.new\",\"args\":{\"name\":\"t\",\"cwd\":" + JsonSerializer.Serialize(explicitDir) + "}}");
        Assert.Equal(explicitDir, host.LastNewSessionCwd);
    }

    [Fact]
    public void SessionNew_InOtherWorkspace_UsesThatWorkspaceDefault()
    {
        var (server, host) = New();
        string dir = Environment.CurrentDirectory;
        var wsReply = Dispatch(server, "{\"cmd\":\"workspace.new\",\"args\":{\"name\":\"other\",\"default-dir\":" + JsonSerializer.Serialize(dir) + "}}");
        string wid = wsReply.GetProperty("result").GetString()!;
        host.ActiveWs.DefaultDir = null;
        Dispatch(server, "{\"cmd\":\"session.new\",\"args\":{\"name\":\"t\",\"workspace\":" + JsonSerializer.Serialize(wid) + "}}");
        Assert.Equal(WorkspaceDefaultDirs.NormalizeSet(dir), host.LastNewSessionCwd);
    }

    [Fact]
    public void WorkspaceNew_InvalidDefaultDir_RefusesWithoutCreatingWorkspace()
    {
        var (server, host) = New();
        int before = host.Workspaces.Count;
        var r = Dispatch(server, "{\"cmd\":\"workspace.new\",\"args\":{\"name\":\"bad\",\"default-dir\":\"Z:\\\\no-such-agwinterm-ws-dir\"}}");
        Assert.False(Ok(r));
        Assert.Equal(before, host.Workspaces.Count);
    }

    [Fact]
    public void Tree_OmitsDefaultDir_WhenUnset()
    {
        var (server, host) = New();
        host.ActiveWs.DefaultDir = Environment.CurrentDirectory;
        Dispatch(server, "{\"cmd\":\"workspace.default-dir\",\"args\":{\"clear\":true}}");
        var tree = Dispatch(server, "{\"cmd\":\"tree\"}");
        var ws = tree.GetProperty("result").GetProperty("workspaces")[0];
        Assert.False(ws.TryGetProperty("defaultDir", out _));
    }
}

public class WorkspaceDefaultDirsValidationTests
{
    [Fact]
    public void ValidateSet_AcceptsExistingDirectory()
    {
        Assert.Null(WorkspaceDefaultDirs.ValidateSet(Environment.CurrentDirectory));
    }

    [Fact]
    public void ValidateSet_RefusesMissingDirectory()
    {
        Assert.Equal(WorkspaceDefaultDirs.NotDirectory, WorkspaceDefaultDirs.ValidateSet(@"Z:\agwinterm-no-such-dir"));
    }

    [Fact]
    public void NormalizeSet_ResolvesRelativePath()
    {
        string full = WorkspaceDefaultDirs.NormalizeSet(".");
        Assert.True(Directory.Exists(full));
        Assert.True(Path.IsPathRooted(full));
    }
}
