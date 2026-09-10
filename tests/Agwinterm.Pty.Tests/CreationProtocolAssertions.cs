using System.Diagnostics;
using System.IO.Pipes;
using Agwinterm.Pty.Proto;
using Google.Protobuf;
using Xunit;

namespace Agwinterm.Pty.Tests;

// Real-pipe/real-child oracle, shared by BOTH hosts. Requires the integration-suite token locally.
internal static class CreationProtocolAssertions
{
    internal static async Task StartupSweepKeepsPendingPane(string appId)
    {
        using var backend = new ServerSessionBackend(appId, null);
        // The live config path resolves a new backend even when reapplying the same value.
        // The original instance still owns the delayed sweep; the replacement owns this pane.
        using var replacement = (ServerSessionBackend)SessionBackends.Resolve("server", appId, null);
        using var pendingPane = replacement.Create("not-yet-published", 80, 24);
        using var client = PtyHostClient.Connect(appId);
        string orphan = client.PrepareCreate("unclaimed-orphan");
        try
        {
            await pendingPane.StartAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep 120"], freshEnv: false);
            Assert.False(pendingPane.HasExited);
            client.Create("unclaimed-orphan", 80, 24, "powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep 120"], freshEnv: false, creationTicket: orphan);
            var before = client.List(); Assert.Equal(2, before.Count);
            using var orphanChild = Process.GetProcessById(before.Single(x => x.Id == "unclaimed-orphan").ChildPid!.Value);
            _ = orphanChild.SafeHandle;
            // The backend handle exists, but no workspace/sidebar/Pane collection publishes it.
            backend.ReapUnclaimedStartupSessions();
            Assert.Equal("not-yet-published", Assert.Single(client.List()).Id);
            Assert.False(pendingPane.HasExited);
            Assert.True(orphanChild.WaitForExit(5000));
            replacement.ReapUnclaimedStartupSessions(); // shared one-shot, not a new claim epoch
            Assert.Equal("not-yet-published", Assert.Single(client.List()).Id);
        }
        finally
        {
            Assert.True(SpinWait.SpinUntil(() => client.CancelCreate("unclaimed-orphan", orphan) == CreationPhase.CreationUnknown, 15000));
            pendingPane.Dispose();
            Assert.True(SpinWait.SpinUntil(() => client.List().Count == 0, 15000));
        }
    }

    private static void DropReply(string appId, Request request)
    {
        using var pipe = new NamedPipeClientStream(".", PtyHostServer.ControlPipeName(appId), PipeDirection.InOut);
        pipe.Connect(5000);
        byte[] payload = request.ToByteArray();
        pipe.Write(BitConverter.GetBytes(payload.Length)); pipe.Write(payload); pipe.Flush();
        // Deliberately close after sending, before reading any response. The host may already
        // have created the child; the next connection must reconcile, never infer non-execution.
    }

    internal static void LostRepliesAndReusedId(string appId)
    {
        using var client = PtyHostClient.Connect(appId);
        Assert.True(client.CreationRevision >= 1);
        const string id = "creation-fault-fixture";
        DropReply(appId, new Request { PrepareCreate = new PrepareCreate { Id = "lost-preparation" } });
        Assert.Empty(client.List()); // preparation alone never creates a child
        Assert.Throws<InvalidOperationException>(() => client.Create("lost-preparation", 80, 24,
            "cmd.exe", [], creationTicket: new string('0', 32)));

        var ticket = client.PrepareCreate(id);
        var create = new Create { Id = id, CreationTicket = ticket, Cols = 80, Rows = 24,
            App = "powershell.exe", FreshEnvOff = true };
        create.Args.Add(new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120" });
        string? replacement = null;
        try
        {
            DropReply(appId, new Request { Create = create });
            Assert.True(SpinWait.SpinUntil(() => client.QueryCreate(id, ticket) == CreationPhase.CreationLive, 15000),
                "lost Create reply must leave an exact queryable live attempt");
            var first = Assert.Single(client.List()); Assert.Equal(ticket, first.CreationTicket);
            using var child = Process.GetProcessById(first.ChildPid!.Value);
            _ = child.SafeHandle; // pin THIS process while it is known live, before cancellation
            Assert.False(child.HasExited);
            Assert.Equal(id, client.Create(id, 80, 24, "powershell.exe", create.Args.ToArray(), freshEnv: false, creationTicket: ticket));
            Assert.Equal(first.ChildPid, Assert.Single(client.List()).ChildPid); // no duplicate spawn
            using (var attachment = client.Attach(id, creationTicket: ticket))
                Assert.Equal(ticket, attachment.CreationTicket);
            Assert.True(SpinWait.SpinUntil(() => !Assert.Single(client.List()).Attached, 5000));
            Assert.Equal(CreationPhase.CreationUnknown, client.CancelCreate(id, ticket));
            Assert.True(child.WaitForExit(5000), "Unknown must mean the exact original child is gone");
            Assert.Empty(client.List());

            replacement = client.PrepareCreate(id);
            client.Create(id, 80, 24, "powershell.exe", create.Args.ToArray(), freshEnv: false, creationTicket: replacement);
            var second = Assert.Single(client.List()); Assert.Equal(replacement, second.CreationTicket);
            Assert.NotEqual(ticket, replacement);
            Assert.Equal(CreationPhase.CreationUnknown, client.CancelCreate(id, ticket));
            Assert.Throws<InvalidOperationException>(() => client.Kill(id, ticket));
            Assert.Throws<InvalidOperationException>(() => client.Detach(id, ticket));
            Assert.Throws<InvalidOperationException>(() => client.Resize(id, 90, 30, ticket));
            Assert.Throws<InvalidOperationException>(() => client.Attach(id, creationTicket: ticket));
            Assert.Equal(second.ChildPid, Assert.Single(client.List()).ChildPid);
            Assert.False(Assert.Single(client.List()).HasExited);
        }
        finally
        {
            using var cleanup = PtyHostClient.Connect(appId);
            foreach (var owned in new[] { ticket, replacement }.OfType<string>())
                Assert.True(SpinWait.SpinUntil(() => cleanup.CancelCreate(id, owned) == CreationPhase.CreationUnknown, 15000),
                    "exact creation cleanup did not complete");
        }
    }
}
