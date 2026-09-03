using System.IO;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class ControlServerTests
{
    private static (ControlServer server, TerminalSession session) New()
    {
        var session = new TerminalSession(80, 24);
        return (new ControlServer(session), session);
    }

    [Fact]
    public void Ping_ReturnsOk()
    {
        var (server, _) = New();
        string resp = server.Dispatch("{\"cmd\":\"ping\"}");
        Assert.Contains("\"ok\":true", resp);
        Assert.Contains("agwinterm", resp);
    }

    [Fact]
    public void UnknownCommand_ReturnsError()
    {
        var (server, _) = New();
        string resp = server.Dispatch("{\"cmd\":\"bogus\"}");
        Assert.Contains("\"ok\":false", resp);
        Assert.Contains("unknown command", resp);
    }

    [Fact]
    public void InvalidJson_ReturnsError()
    {
        var (server, _) = New();
        string resp = server.Dispatch("not json");
        Assert.Contains("\"ok\":false", resp);
    }

    [Fact]
    public void SessionStatus_SetsAgentStatus()
    {
        var (server, session) = New();
        string resp = server.Dispatch("{\"cmd\":\"session.status\",\"args\":{\"status\":\"active\"}}");
        Assert.Contains("\"ok\":true", resp);
        Assert.Equal(AgentStatus.Active, session.Status);

        server.Dispatch("{\"cmd\":\"session.status\",\"args\":{\"status\":\"blocked\"}}");
        Assert.Equal(AgentStatus.Blocked, session.Status);

        server.Dispatch("{\"cmd\":\"session.status\",\"args\":{\"status\":\"idle\"}}");
        Assert.Equal(AgentStatus.Idle, session.Status);
    }

    [Fact]
    public void NotifyActivity_ClearsBlockedOrCompleted_NotActive()
    {
        var (_, session) = New();
        session.SetStatus(AgentStatus.Completed);
        session.NotifyActivity();
        Assert.Equal(AgentStatus.Idle, session.Status);

        session.SetStatus(AgentStatus.Active);
        session.NotifyActivity();
        Assert.Equal(AgentStatus.Active, session.Status); // agent still working; not cleared
    }

    [Fact]
    public void SessionWrite_InjectsTextIntoEmulator()
    {
        var (server, session) = New();
        string resp = server.Dispatch("{\"cmd\":\"session.write\",\"args\":{\"text\":\"hello\"}}");
        Assert.Contains("\"ok\":true", resp);
        Assert.Equal("hello", session.Emulator.DumpRow(0));
    }

    [Fact]
    public void ImageShow_MissingFile_ReturnsError()
    {
        var (server, _) = New();
        string resp = server.Dispatch("{\"cmd\":\"image.show\",\"args\":{\"path\":\"C:\\\\nope\\\\missing.png\"}}");
        Assert.Contains("\"ok\":false", resp);
        Assert.Contains("not found", resp);
    }

    [Fact]
    public void ImageShow_RealPng_PlacesImageAtPosition()
    {
        var (server, session) = New();

        // Minimal 1x1 PNG.
        byte[] png =
        {
            0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
            0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,0x08,0x02,0x00,0x00,0x00,0x90,0x77,0x53,
            0xDE,0x00,0x00,0x00,0x0C,0x49,0x44,0x41,0x54,0x08,0xD7,0x63,0xF8,0xCF,0xC0,0x00,
            0x00,0x00,0x03,0x01,0x01,0x00,0x18,0xDD,0x8D,0xB0,0x00,0x00,0x00,0x00,0x49,0x45,
            0x4E,0x44,0xAE,0x42,0x60,0x82,
        };
        string file = Path.Combine(Path.GetTempPath(), "agw_ctrl_test.png");
        File.WriteAllBytes(file, png);

        string req = "{\"cmd\":\"image.show\",\"args\":{\"path\":" +
                     System.Text.Json.JsonSerializer.Serialize(file) + ",\"row\":3,\"col\":5}}";
        string resp = server.Dispatch(req);

        Assert.Contains("\"ok\":true", resp);
        Assert.Single(session.Emulator.Placements);
        var p = session.Emulator.Placements[0];
        Assert.Equal(3, p.Row);
        Assert.Equal(5, p.Col);
        Assert.Single(session.Emulator.Images);
    }

    [Fact]
    public void ImageClear_RemovesPlacements()
    {
        var (server, session) = New();
        byte[] png =
        {
            0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
            0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,0x08,0x02,0x00,0x00,0x00,0x90,0x77,0x53,
            0xDE,0x00,0x00,0x00,0x0C,0x49,0x44,0x41,0x54,0x08,0xD7,0x63,0xF8,0xCF,0xC0,0x00,
            0x00,0x00,0x03,0x01,0x01,0x00,0x18,0xDD,0x8D,0xB0,0x00,0x00,0x00,0x00,0x49,0x45,
            0x4E,0x44,0xAE,0x42,0x60,0x82,
        };
        string file = Path.Combine(Path.GetTempPath(), "agw_ctrl_clear.png");
        File.WriteAllBytes(file, png);
        server.Dispatch("{\"cmd\":\"image.show\",\"args\":{\"path\":" + System.Text.Json.JsonSerializer.Serialize(file) + ",\"id\":1}}");
        Assert.Single(session.Emulator.Placements);

        string resp = server.Dispatch("{\"cmd\":\"image.clear\"}");
        Assert.Contains("\"ok\":true", resp);
        Assert.Empty(session.Emulator.Placements);
    }

    [Fact]
    public void ImageFrame_AtomicallyReplacesWithCellDims()
    {
        var (server, session) = New();
        byte[] png =
        {
            0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
            0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,0x08,0x02,0x00,0x00,0x00,0x90,0x77,0x53,
            0xDE,0x00,0x00,0x00,0x0C,0x49,0x44,0x41,0x54,0x08,0xD7,0x63,0xF8,0xCF,0xC0,0x00,
            0x00,0x00,0x03,0x01,0x01,0x00,0x18,0xDD,0x8D,0xB0,0x00,0x00,0x00,0x00,0x49,0x45,
            0x4E,0x44,0xAE,0x42,0x60,0x82,
        };
        string file = Path.Combine(Path.GetTempPath(), "agw_frame.png");
        File.WriteAllBytes(file, png);
        string pj = System.Text.Json.JsonSerializer.Serialize(file);

        // Seed a stale placement that the frame must clear.
        server.Dispatch("{\"cmd\":\"image.show\",\"args\":{\"path\":" + pj + ",\"id\":99}}");

        string req = "{\"cmd\":\"image.frame\",\"args\":{\"images\":[" +
            "{\"path\":" + pj + ",\"row\":1,\"col\":2,\"cols\":10,\"rows\":5,\"id\":1}," +
            "{\"path\":" + pj + ",\"row\":7,\"col\":0,\"cols\":4,\"rows\":3,\"id\":2}]}}";
        string resp = server.Dispatch(req);

        Assert.Contains("\"ok\":true", resp);
        Assert.Equal(2, session.Emulator.Placements.Count); // stale id=99 cleared, 2 new
        var p1 = session.Emulator.Placements[0];
        Assert.Equal(1, p1.Row);
        Assert.Equal(2, p1.Col);
        Assert.Equal(10, p1.Cols);
        Assert.Equal(5, p1.Rows);
    }

    // Phase 1 answers a cache hit from the dictionary alone; the only check that the emulator still
    // holds the cached image is in phase 2, which drops the entry and retries once. A child that
    // replaced the id (Kitty output, image.show) must therefore cost one re-read, not an error.
    [Fact]
    public void ImageFrame_ACacheHitRetransmitsAfterTheImageIdWasReplacedOutsideTheFrameVerb()
    {
        var (server, session) = New();
        string file = Path.Combine(Path.GetTempPath(), "agw_frame_replaced_" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(file, OnePixelPng);
        try
        {
            string pj = System.Text.Json.JsonSerializer.Serialize(file);
            string req = "{\"cmd\":\"image.frame\",\"args\":{\"images\":[" +
                "{\"path\":" + pj + ",\"row\":1,\"col\":2,\"cols\":10,\"rows\":5,\"id\":1}]}}";
            Assert.Contains("frame:1/1", server.Dispatch(req));

            session.MutateLocked(em => em.SetImageData(1, KittyFormat.Rgba, 1, 1, new byte[] { 1, 2, 3, 4 }));

            Assert.Contains("frame:1/1", server.Dispatch(req));
            Assert.Equal(OnePixelPng, session.Emulator.Images[1].Data);
            Assert.Single(session.Emulator.Placements);
        }
        finally { File.Delete(file); }
    }

    // A frame of two cached ids where only one went stale: the retry re-reads that one file, and
    // the live sibling keeps its cache hit instead of being re-read along with it.
    [Fact]
    public void ImageFrame_OnlyTheStaleIdIsReReadWhenASiblingIsStillLive()
    {
        var (server, session) = New();
        string file = Path.Combine(Path.GetTempPath(), "agw_frame_sibling_" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(file, OnePixelPng);
        try
        {
            string pj = System.Text.Json.JsonSerializer.Serialize(file);
            string req = "{\"cmd\":\"image.frame\",\"args\":{\"images\":[" +
                "{\"path\":" + pj + ",\"row\":1,\"col\":0,\"cols\":4,\"rows\":2,\"id\":1}," +
                "{\"path\":" + pj + ",\"row\":5,\"col\":0,\"cols\":4,\"rows\":2,\"id\":2}]}}";
            Assert.Contains("frame:2/2", server.Dispatch(req));
            Assert.Contains("frame:2/0", server.Dispatch(req));

            session.MutateLocked(em => em.SetImageData(2, KittyFormat.Rgba, 1, 1, new byte[] { 1, 2, 3, 4 }));

            Assert.Contains("frame:2/1", server.Dispatch(req));
            Assert.Equal(OnePixelPng, session.Emulator.Images[2].Data);
            Assert.Equal(2, session.Emulator.Placements.Count);
        }
        finally { File.Delete(file); }
    }

    private static readonly byte[] OnePixelPng =
    {
        0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
        0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,0x08,0x02,0x00,0x00,0x00,0x90,0x77,0x53,
        0xDE,0x00,0x00,0x00,0x0C,0x49,0x44,0x41,0x54,0x08,0xD7,0x63,0xF8,0xCF,0xC0,0x00,
        0x00,0x00,0x03,0x01,0x01,0x00,0x18,0xDD,0x8D,0xB0,0x00,0x00,0x00,0x00,0x49,0x45,
        0x4E,0x44,0xAE,0x42,0x60,0x82,
    };
}
