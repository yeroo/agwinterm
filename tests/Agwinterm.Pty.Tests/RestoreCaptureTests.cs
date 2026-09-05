using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// restore.capture — A DURABLE SLOT, ONE CAPTURE PATH, A VERB THAT REPORTS (P3).
///
/// Before this the foreground-command capture happened exactly once, in WM_DESTROY — which a crash,
/// a Stop-Process, a power loss or a missed update-quit never reaches — and every ordinary save wrote
/// "" into the slot because the captured command had no in-memory field. The verb captures NOW,
/// into a slot the saves read, and replies per pane with what it captured:
/// <c>{captured, replayOnRestore, panes:[{pane, session, captured|null}]}</c>; <c>tree</c> reads it
/// back as <c>capturedCommands</c>. Null is "the shell had no non-denylisted child" and is distinct
/// from a failed query, which is a refusal. As with #213's refusals, every refusal is asserted twice:
/// the reply, and that no pane's slot changed.
///
/// The fake's <c>Foreground</c> table stands in for the app's CIM process snapshot (the fake has no
/// processes); <c>Captured</c> is the slot. The app's threading (query on the pipe thread, writes in
/// one queued UI hop) cannot be exercised here — see Program.ControlHost.RestoreCapture.
/// </summary>
public class RestoreCaptureTests
{
    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    private static JsonElement Capture(ControlServer server, string? target = null)
    {
        var sb = new System.Text.StringBuilder("{\"cmd\":\"restore.capture\"");
        if (target is not null) sb.Append(",\"target\":").Append(JsonSerializer.Serialize(target));
        sb.Append('}');
        return JsonDocument.Parse(server.Dispatch(sb.ToString())).RootElement;
    }

    private static bool Ok(JsonElement r) => r.GetProperty("ok").GetBoolean();
    private static string Error(JsonElement r) => r.GetProperty("error").GetString() ?? "";
    private static JsonElement Result(JsonElement r) => r.GetProperty("result");
    private static int Captured(JsonElement r) => Result(r).GetProperty("captured").GetInt32();
    private static bool Replay(JsonElement r) => Result(r).GetProperty("replayOnRestore").GetBoolean();
    private static List<JsonElement> Panes(JsonElement r) => Result(r).GetProperty("panes").EnumerateArray().ToList();
    private static string? PaneCaptured(JsonElement pane) => pane.GetProperty("captured").ValueKind == JsonValueKind.Null ? null : pane.GetProperty("captured").GetString();

    /// <summary>A session's node from <c>tree</c> — the read-back a caller has after the fact.</summary>
    private static JsonElement TreeSession(ControlServer server, int index = 0)
        => JsonDocument.Parse(server.Dispatch("{\"cmd\":\"tree\"}")).RootElement
            .GetProperty("result").GetProperty("workspaces")[0].GetProperty("sessions")[index];

    private static JsonElement? TreeCaptured(ControlServer server, int index = 0)
        => TreeSession(server, index).TryGetProperty("capturedCommands", out var v) ? v : null;

    private static int AllSlots(FakeSessionHost host)
        => host.Workspaces.SelectMany(w => w.Sessions).Sum(s => s.Captured.Count);

    // ---- all panes: the reply shape, the count, and the slot ----

    [Fact]
    public void AllPanes_RepliesPerPane_CountsTheNonNull_AndFillsEverySlot()
    {
        var (server, host) = New();
        var s1 = host.ActiveSess!;
        string p1 = s1.AddPane().Let(_ => s1.PaneIds[1]);
        string s2 = host.NewSession("second", null, null, noSelect: true);
        var ses2 = host.Workspaces[0].Sessions[1];
        s1.Foreground["s1"] = "npm run dev";
        s1.Foreground[p1] = "cargo watch -x test";
        // ses2's pane runs nothing (absent from Foreground)

        var r = Capture(server);
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(JsonValueKind.Object, Result(r).ValueKind);   // OkRaw: an object, not a word
        Assert.Equal(2, Captured(r));                               // the non-null ones, not the pane count
        var panes = Panes(r);
        Assert.Equal(3, panes.Count);                               // every real pane, in tree order
        Assert.Equal("s1", panes[0].GetProperty("pane").GetString());
        Assert.Equal("s1", panes[0].GetProperty("session").GetString());
        Assert.Equal("npm run dev", PaneCaptured(panes[0]));
        Assert.Equal(p1, panes[1].GetProperty("pane").GetString());
        Assert.Equal("s1", panes[1].GetProperty("session").GetString());   // the reply says whose pane it is
        Assert.Equal("cargo watch -x test", PaneCaptured(panes[1]));
        Assert.Equal(s2, panes[2].GetProperty("pane").GetString());
        Assert.Equal(s2, panes[2].GetProperty("session").GetString());
        Assert.Null(PaneCaptured(panes[2]));                        // listed, with null: the pane was reached and had nothing

        Assert.Equal("npm run dev", s1.Captured["s1"]);
        Assert.Equal("cargo watch -x test", s1.Captured[p1]);
        Assert.Empty(ses2.Captured);
    }

    [Fact]
    public void NothingRunningAnywhere_IsOkWithZeroCaptured_AndEveryPaneListedAsNull()
    {
        var (server, host) = New();
        var r = Capture(server);
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(0, Captured(r));
        var panes = Panes(r);
        Assert.Single(panes);
        Assert.Null(PaneCaptured(panes[0]));
        Assert.Equal(0, AllSlots(host));
        Assert.Null(TreeCaptured(server));   // nothing captured: no field, like restoreCommands
    }

    // ---- one target ----

    [Fact]
    public void Target_PaneId_CapturesThatPaneOnly()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.AddPane();
        string p1 = ses.PaneIds[1];
        ses.Foreground["s1"] = "one";
        ses.Foreground[p1] = "two";

        var r = Capture(server, p1);
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(1, Captured(r));
        var panes = Panes(r);
        Assert.Single(panes);
        Assert.Equal(p1, panes[0].GetProperty("pane").GetString());
        Assert.Equal("two", PaneCaptured(panes[0]));
        Assert.Equal("two", ses.Captured[p1]);
        Assert.False(ses.Captured.ContainsKey("s1"));   // pane 0 was not reached: its slot is untouched
    }

    [Fact]
    public void Target_SessionId_LandsOnPaneZero_NotTheFocusedPane()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.AddPane();
        ses.FocusedPane = 1;   // an ID target must NOT follow focus: the session id IS pane 0's id
        ses.Foreground["s1"] = "zero";
        ses.Foreground[ses.PaneIds[1]] = "one";
        var r = Capture(server, "s1");
        Assert.Equal("s1", Panes(r)[0].GetProperty("pane").GetString());
        Assert.Equal("zero", ses.Captured["s1"]);
        Assert.False(ses.Captured.ContainsKey(ses.PaneIds[1]));
    }

    [Fact]
    public void Target_SessionName_LandsOnTheFocusedPane()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.AddPane();
        ses.FocusedPane = 1;
        ses.Foreground[ses.PaneIds[1]] = "one";
        var r = Capture(server, "session 1");
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(ses.PaneIds[1], Panes(r)[0].GetProperty("pane").GetString());
        Assert.Equal("one", PaneCaptured(Panes(r)[0]));
    }

    [Fact]
    public void Target_Active_IsTheActiveSessionsFocusedPane()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.AddPane();
        ses.FocusedPane = 1;
        ses.Foreground[ses.PaneIds[1]] = "one";
        var r = Capture(server, "active");
        Assert.True(Ok(r), r.ToString());
        Assert.Single(Panes(r));
        Assert.Equal(ses.PaneIds[1], Panes(r)[0].GetProperty("pane").GetString());
    }

    [Fact]
    public void Target_PaneIdPrefix_LandsOnThatPane()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.AddPane();
        string p1 = ses.PaneIds[1];
        var r = Capture(server, p1[..4]);
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(p1, Panes(r)[0].GetProperty("pane").GetString());
    }

    // ---- null is "nothing running", and it overrides an earlier checkpoint ----

    [Fact]
    public void PaneWithNothingRunning_ReportsNull_AndCountsZero()
    {
        var (server, host) = New();
        var r = Capture(server, "s1");
        Assert.True(Ok(r), r.ToString());
        Assert.Equal(0, Captured(r));
        Assert.Single(Panes(r));
        Assert.Equal(JsonValueKind.Null, Panes(r)[0].GetProperty("captured").ValueKind);   // null, not "" and not absent
        Assert.Equal(0, AllSlots(host));
    }

    [Fact]
    public void Recapture_WithNothingRunning_ClearsTheEarlierCheckpoint()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.Foreground["s1"] = "npm run dev";
        Assert.Equal(1, Captured(Capture(server)));
        Assert.Equal("npm run dev", TreeCaptured(server)!.Value.GetProperty("s1").GetString());

        ses.Foreground.Remove("s1");   // the command has since exited
        var r = Capture(server);
        Assert.Equal(0, Captured(r));
        Assert.Null(PaneCaptured(Panes(r)[0]));
        Assert.Equal(0, AllSlots(host));   // a fresh capture overrides, including to empty — the quit-time behaviour
        Assert.Null(TreeCaptured(server));
    }

    [Fact]
    public void Recapture_ReplacesTheCommand_AndTheTreeShowsTheNewOne()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.Foreground["s1"] = "first";
        Assert.True(Ok(Capture(server)));
        ses.Foreground["s1"] = "second";
        Assert.True(Ok(Capture(server)));
        Assert.Equal("second", TreeCaptured(server)!.Value.GetProperty("s1").GetString());
    }

    // ---- refusals: the reply, and then that no slot changed ----

    [Fact]
    public void UnknownTarget_IsRefused_NamesTheVerb_AndNoSlotChanges()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.AddPane();
        ses.Foreground["s1"] = "npm run dev";
        ses.Foreground[ses.PaneIds[1]] = "tail -f log";
        var r = Capture(server, "no-such-pane");
        Assert.False(Ok(r));
        Assert.StartsWith("restore capture:", Error(r));   // the verb's own refusal, not the flat "no session"
        Assert.Contains("no-such-pane", Error(r));
        Assert.Contains("Nothing captured", Error(r));
        Assert.Equal(0, AllSlots(host));   // nothing captured for ANY pane, not just the missing one
        Assert.Null(TreeCaptured(server));
    }

    [Fact]
    public void AmbiguousName_IsRefused_AndNoSlotChanges()
    {
        var (server, host) = New();
        host.NewSession("session 1", null, null);   // a second session with the same name
        host.Workspaces[0].Sessions[0].Foreground["s1"] = "npm run dev";
        var r = Capture(server, "session 1");
        Assert.False(Ok(r));
        Assert.Equal(0, AllSlots(host));
    }

    /// <summary>A scratch / overlay / quick cover is a valid TARGET for every content verb, but it is
    /// never in the saved tree, so it has no restore slot: refused with the pane named, and nothing
    /// captured — the refusal session.restore gives a pin there.</summary>
    [Fact]
    public void CoverPane_IsRefused_NamesThePane_AndNoSlotChanges()
    {
        var (server, host) = New();
        var ses = host.Workspaces[0].Sessions[0];
        string cover = ses.AddCoverPane();
        ses.Foreground["s1"] = "npm run dev";
        // the cover IS reachable by a content verb (the app's FindPaneBy order) — only capture refuses it
        var read = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.text\",\"target\":" + JsonSerializer.Serialize(cover) + "}")).RootElement;
        Assert.True(read.GetProperty("ok").GetBoolean(), read.ToString());
        var r = Capture(server, cover);
        Assert.False(Ok(r));
        Assert.Contains(cover, Error(r));
        Assert.Contains("never restored", Error(r));
        Assert.Contains("Nothing captured", Error(r));
        Assert.Equal(0, AllSlots(host));
        Assert.Null(TreeCaptured(server));
        // and the session itself is still capturable: the refusal was about the pane, not the session
        Assert.Equal(1, Captured(Capture(server, ses.Id)));
    }

    [Fact]
    public void FailedQuery_IsRefused_AndNoSlotChanges_EvenAnEarlierCheckpoint()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.Foreground["s1"] = "npm run dev";
        Assert.Equal(1, Captured(Capture(server)));

        host.CaptureFails = true;
        ses.Foreground.Remove("s1");   // if the failure were reported as "nothing running", this would clear the slot
        var r = Capture(server);
        Assert.False(Ok(r));
        Assert.Equal(RestoreCaptureReply.QueryFailed, Error(r));
        Assert.Contains("Nothing captured", Error(r));
        Assert.Equal("npm run dev", ses.Captured["s1"]);   // the earlier checkpoint stands
        Assert.Equal("npm run dev", TreeCaptured(server)!.Value.GetProperty("s1").GetString());
    }

    // ---- the read-back: tree's capturedCommands, keyed by pane ----

    [Fact]
    public void Tree_CarriesCapturedCommands_AfterACapture_AndNotBefore()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.AddPane();
        string p1 = ses.PaneIds[1];
        ses.Foreground[p1] = "tail -f log";
        Assert.Null(TreeCaptured(server));   // seeded but not captured: the slot is empty and the tree says so

        Assert.True(Ok(Capture(server)));
        var caps = TreeCaptured(server);
        Assert.NotNull(caps);
        Assert.Equal(JsonValueKind.Object, caps!.Value.ValueKind);
        Assert.Equal("tail -f log", caps.Value.GetProperty(p1).GetString());
        Assert.False(caps.Value.TryGetProperty("s1", out _));   // pane 0 captured nothing: absent, not "" and not null
        Assert.Single(caps.Value.EnumerateObject());
    }

    [Fact]
    public void Tree_KeepsRestoreCommandsAndCapturedCommandsApart()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.Foreground["s1"] = "npm run dev";
        Assert.True(Ok(JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.restore\",\"target\":\"s1\",\"args\":{\"command\":\"pinned one\"}}")).RootElement));
        Assert.True(Ok(Capture(server)));
        var node = TreeSession(server);
        Assert.Equal("pinned one", node.GetProperty("restoreCommands").GetProperty("s1").GetString());
        Assert.Equal("npm run dev", node.GetProperty("capturedCommands").GetProperty("s1").GetString());
    }

    // ---- replayOnRestore mirrors the toggle, and the toggle never gates the capture ----

    [Fact]
    public void ReplayOnRestore_MirrorsTheToggle_AndTheCaptureHappensEitherWay()
    {
        var (server, host) = New();
        var ses = host.ActiveSess!;
        ses.Foreground["s1"] = "npm run dev";

        var off = Capture(server);   // a default install: restore-commands is off
        Assert.True(Ok(off), off.ToString());
        Assert.False(Replay(off));
        Assert.Equal(1, Captured(off));                 // captured anyway — not the silent-success class
        Assert.Equal("npm run dev", ses.Captured["s1"]);

        Assert.True(Ok(JsonDocument.Parse(server.Dispatch("{\"cmd\":\"config.set\",\"args\":{\"key\":\"restore-commands\",\"value\":\"true\"}}")).RootElement));
        var on = Capture(server);
        Assert.True(Replay(on));
        Assert.Equal(1, Captured(on));
    }

    // ---- the reply builder itself (the shape the sibling conformance step will pin) ----

    [Fact]
    public void Reply_Shape_IsCapturedReplayAndPanes_WithNullSpelledAsNull()
    {
        string json = RestoreCaptureReply.Build(new RestoreCaptureResult(
            new[] { new CapturedPane("p0", "s0", "a \"quoted\" cmd"), new CapturedPane("p1", "s0", null) }, true));
        var r = JsonDocument.Parse(json).RootElement;
        Assert.Equal(1, r.GetProperty("captured").GetInt32());
        Assert.True(r.GetProperty("replayOnRestore").GetBoolean());
        Assert.Equal(2, r.GetProperty("panes").GetArrayLength());
        Assert.Equal("a \"quoted\" cmd", r.GetProperty("panes")[0].GetProperty("captured").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("panes")[1].GetProperty("captured").ValueKind);
        Assert.Equal("{\"captured\":0,\"replayOnRestore\":false,\"panes\":[]}",
            RestoreCaptureReply.Build(new RestoreCaptureResult(Array.Empty<CapturedPane>(), false)));
    }
}

internal static class LetExtensions
{
    /// <summary>Evaluate <paramref name="f"/> on <paramref name="x"/> — lets a seeded pane's id be read
    /// in the same expression that added it.</summary>
    public static TOut Let<TIn, TOut>(this TIn x, Func<TIn, TOut> f) => f(x);
}
