using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class SessionBackendTests
{
    [Fact]
    public void InProcessBackend_CreatesWorkingSessions()
    {
        using ISession s = InProcessSessionBackend.Instance.Create("t", 20, 5);
        Assert.Equal(20, s.Cols);
        Assert.Equal(5, s.Rows);
        // The handle drives the emulator through the interface alone (what a Phase-2 replica must honor).
        s.Inject("hello"u8);
        Assert.Equal("hello", s.SnapshotRow(0).TrimEnd());
        s.Resize(30, 6);
        Assert.Equal(30, s.Cols);
        Assert.False(s.HasExited);   // never started — no phantom exit
    }

    [Fact]
    public void Resolve_PicksBackendByConfiguredValue()
    {
        Assert.Same(InProcessSessionBackend.Instance, SessionBackends.Resolve("in-process", "x", null));
        Assert.Same(InProcessSessionBackend.Instance, SessionBackends.Resolve(null, "x", null));
        Assert.IsType<ServerSessionBackend>(SessionBackends.Resolve("server", "x", null));
        Assert.IsType<ServerSessionBackend>(SessionBackends.Resolve("SERVER", "x", null));
    }

    [Fact]
    public void ServerBackend_WithNoHostAndNoExe_FailsAtCreate()
    {
        // Failure surfaces at Create — the one place the UI can catch it and fall back in-process.
        using var backend = new ServerSessionBackend("agwinterm-test-nohost-" + Guid.NewGuid().ToString("N")[..8], exePath: null);
        Assert.Throws<InvalidOperationException>(() => backend.Create("t3", 80, 24));
    }

    [Fact]
    public void ControlServer_AcceptsAnyISession()
    {
        // The single-session ControlServer ctor is part of the seam: it must take the interface.
        using ISession s = InProcessSessionBackend.Instance.Create("t2", 80, 24);
        using var server = new ControlServer(s, "agwinterm-test-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.NotNull(server);
    }
}
