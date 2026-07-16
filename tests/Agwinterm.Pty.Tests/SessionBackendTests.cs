using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class SessionBackendTests
{
    [Fact]
    public void InProcessBackend_CreatesWorkingSessions()
    {
        using ISession s = InProcessSessionBackend.Instance.Create(20, 5);
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
    public void Resolve_ServerFallsBackToInProcessAndSaysSo()
    {
        // "server" is reserved for the pty-host (#105). Until Phase 2 ships it must fall back —
        // and report that it did, so the UI can tell the user instead of silently ignoring the key.
        Assert.Same(InProcessSessionBackend.Instance, SessionBackends.Resolve("in-process", out bool fb1));
        Assert.False(fb1);
        Assert.Same(InProcessSessionBackend.Instance, SessionBackends.Resolve("server", out bool fb2));
        Assert.True(fb2);
        Assert.Same(InProcessSessionBackend.Instance, SessionBackends.Resolve(null, out bool fb3));
        Assert.False(fb3);
    }

    [Fact]
    public void ControlServer_AcceptsAnyISession()
    {
        // The single-session ControlServer ctor is part of the seam: it must take the interface.
        using ISession s = InProcessSessionBackend.Instance.Create(80, 24);
        using var server = new ControlServer(s, "agwinterm-test-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.NotNull(server);
    }
}
