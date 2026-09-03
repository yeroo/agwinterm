using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// `session.metrics` — the contract winterm-browser codes against (`pixel-core`'s
/// `ControlClient::pane_metrics`). It is the only way anything outside the process can learn a pane's
/// grid size, so the shape is pinned here field by field: the exact extent prevents the rounded
/// per-cell compatibility hint from accumulating into a resampled, mis-sized page.
/// </summary>
public class SessionMetricsTests
{
    private static (ControlServer server, FakeSessionHost host) New()
    {
        var host = new FakeSessionHost();
        return (new ControlServer(host), host);
    }

    private static JsonElement Dispatch(ControlServer server, string? target = null)
    {
        string req = target is null
            ? "{\"cmd\":\"session.metrics\"}"
            : "{\"cmd\":\"session.metrics\",\"target\":" + JsonSerializer.Serialize(target) + "}";
        return JsonDocument.Parse(server.Dispatch(req)).RootElement;
    }

    private static JsonElement Metrics(ControlServer server, string? target = null)
        => Dispatch(server, target).GetProperty("result");

    [Fact]
    public void ReportsTheHostsLiveCellMetrics()
    {
        var (server, host) = New();
        host.CellW = 9; host.CellH = 19; host.PaneW = 1188; host.PaneH = 703;

        var r = Metrics(server);

        Assert.Equal(9, r.GetProperty("cellWidth").GetInt32());
        Assert.Equal(19, r.GetProperty("cellHeight").GetInt32());
        Assert.Equal(1188, r.GetProperty("widthPx").GetInt32());
        Assert.Equal(703, r.GetProperty("heightPx").GetInt32());
        Assert.Equal(80, r.GetProperty("cols").GetInt32());
        Assert.Equal(24, r.GetProperty("rows").GetInt32());
    }

    // OkRaw, not Ok: `events` shipped as a STRING of JSON once and every caller had to parse twice.
    [Fact]
    public void ResultIsAnObjectNotAStringOfJson()
    {
        var (server, _) = New();
        var resp = Dispatch(server);
        Assert.True(resp.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Object, resp.GetProperty("result").ValueKind);
    }

    // camelCase, matching window.state's sidebarVisible — and exactly these six, so a rename or a
    // stray extra field is a failing test here rather than a silent contract break over there.
    [Fact]
    public void FieldNamesAreTheCamelCaseSetTheConsumerReads()
    {
        var (server, _) = New();
        var names = Metrics(server).EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "cols", "rows", "cellWidth", "cellHeight", "widthPx", "heightPx" }, names);
    }

    // The whole point of the verb: the consumer re-asks after a resize / font-size change, so the
    // answer must be measured per call. A constant, or a value cached at construction, fails here.
    [Fact]
    public void TracksAFontSizeChangeRatherThanBeingSampledOnce()
    {
        var (server, host) = New();
        host.CellW = 9; host.CellH = 19;
        Assert.Equal(9, Metrics(server).GetProperty("cellWidth").GetInt32());

        host.CellW = 14; host.CellH = 30;      // the user pressed Ctrl+= — the pane is unchanged, the grid is not
        host.PaneW = 1188; host.PaneH = 700;

        var after = Metrics(server);
        Assert.Equal(14, after.GetProperty("cellWidth").GetInt32());
        Assert.Equal(30, after.GetProperty("cellHeight").GetInt32());
    }

    [Fact]
    public void TracksAPaneResize()
    {
        var (server, host) = New();
        host.PaneW = 1188; host.PaneH = 703;
        Assert.Equal(1188, Metrics(server).GetProperty("widthPx").GetInt32());

        host.PaneW = 640; host.PaneH = 480;
        var after = Metrics(server);
        Assert.Equal(640, after.GetProperty("widthPx").GetInt32());
        Assert.Equal(480, after.GetProperty("heightPx").GetInt32());
    }

    [Fact]
    public void FractionalCellMetricsKeepTheExactRenderedGridExtent()
    {
        var m = PaneMetricsSnapshot.FromDipGrid(
            cols: 132, rows: 37, cellWidthDip: 7.4f, cellHeightDip: 15.2f, dpiScale: 1.25f);

        Assert.Equal(9, m.CellWidth);
        Assert.Equal(19, m.CellHeight);
        Assert.Equal(1221, m.WidthPx);  // round(132 * 7.4 * 1.25), not 132 * round(7.4 * 1.25)
        Assert.Equal(703, m.HeightPx);
    }

    [Fact]
    public void ATargetedPaneIsMeasuredRatherThanTheActiveOne()
    {
        var (server, host) = New();
        string second = host.NewSession("second", null, null);
        string secondPane = "p-" + second;
        host.MetricsBySession[second] = new PaneMetricsSnapshot(40, 12, 11, 22, 440, 264);
        Assert.True(host.SelectSession("s1"));
        host.CellW = 9; host.CellH = 19; host.PaneW = 720; host.PaneH = 456;

        foreach (string target in new[] { secondPane, secondPane[..^1] })
        {
            var r = Metrics(server, target);
            Assert.Equal(11, r.GetProperty("cellWidth").GetInt32());
            Assert.Equal(440, r.GetProperty("widthPx").GetInt32());
            Assert.Equal(40, r.GetProperty("cols").GetInt32());
        }
    }

    // A host with no UI to measure is not an error: the consumer treats a zero cell size as "no
    // metrics" and falls back to its override, where ok:false reads as a broken terminal. cols/rows
    // still come from the session, which every host knows.
    [Fact]
    public void AHostThatCannotMeasureAnswersZerosNotAnError()
    {
        var (server, host) = New();
        host.Measurable = false;

        var resp = Dispatch(server);
        Assert.True(resp.GetProperty("ok").GetBoolean());
        var r = resp.GetProperty("result");
        Assert.Equal(0, r.GetProperty("cellWidth").GetInt32());
        Assert.Equal(0, r.GetProperty("cellHeight").GetInt32());
        Assert.Equal(0, r.GetProperty("widthPx").GetInt32());
        Assert.Equal(80, r.GetProperty("cols").GetInt32());
        Assert.Equal(24, r.GetProperty("rows").GetInt32());
    }

    // A host that never implements the verb (the default interface member) still answers the shape.
    [Fact]
    public void TheDefaultHostImplementationStillAnswersTheShape()
    {
        var server = new ControlServer(new TerminalSession(100, 40));   // SingleSessionHost
        var r = Metrics(server);
        Assert.Equal(100, r.GetProperty("cols").GetInt32());
        Assert.Equal(40, r.GetProperty("rows").GetInt32());
        Assert.Equal(0, r.GetProperty("cellWidth").GetInt32());
    }

    [Fact]
    public void AnUnresolvableTargetIsAnError()
    {
        var (server, _) = New();
        var resp = Dispatch(server, "no-such-pane");
        Assert.False(resp.GetProperty("ok").GetBoolean());
        Assert.Contains("no session", resp.GetProperty("error").GetString());
    }

    // The capability probe the consumer latches on: it must NOT be "unknown command" any more.
    [Fact]
    public void TheVerbIsNoLongerAnUnknownCommand()
    {
        var (server, _) = New();
        Assert.DoesNotContain("unknown command", server.Dispatch("{\"cmd\":\"session.metrics\"}"));
        Assert.Contains("unknown command", server.Dispatch("{\"cmd\":\"session.metric\"}"));
    }
}
