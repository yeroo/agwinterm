using System.Diagnostics;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// Compatibility oracle for the RUST pty-host (native/agwinterm-ptyhost): the SAME
/// PtyHostClient that talks to the C# host drives the Rust binary through the same
/// scenarios — protocol identity proven by the client not knowing which host it got.
/// Skips when the binary isn't built (CI builds the cargo workspace).
/// </summary>
public class RustPtyHostTests : IDisposable
{
    private static readonly string? ExePath = Find();
    private readonly string _appId = "agwinterm-rusthost-" + Guid.NewGuid().ToString("N")[..8];
    private Process? _host;

    private static string? Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agwinterm.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string exe = Path.Combine(dir.FullName, "native", "target", "release", "agwinterm-ptyhost.exe");
        return File.Exists(exe) ? exe : null;
    }

    private PtyHostClient Start(string? exe = null, string extraArgs = "")
    {
        _host = Process.Start(new ProcessStartInfo(exe ?? ExePath!, $"--pipe {_appId}{extraArgs}") { UseShellExecute = false, CreateNoWindow = true });
        for (int i = 0; i < 50 && !PtyHostClient.IsRunning(_appId); i++) Thread.Sleep(100);
        return PtyHostClient.Connect(_appId);   // hello handshake — protocol version must match
    }

    public void Dispose()
    {
        try { if (_host is { HasExited: false }) _host.Kill(entireProcessTree: true); } catch { }
        _host?.Dispose();
    }

    private static bool WaitFor(Func<bool> cond, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    /// <summary>Read the attachment stream until <paramref name="marker"/> appears — pure read, no
    /// typing (for sessions whose output is driven by -Command rather than interactive input).</summary>
    private static string ReadUntil(Stream data, string marker, int timeoutMs)
    {
        var all = new System.Text.StringBuilder();
        var buf = new byte[16 * 1024];
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        Task<int>? pending = null;
        try
        {
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                pending ??= data.ReadAsync(buf, 0, buf.Length, cts.Token);
                if (!pending.Wait(250)) continue;
                int n = pending.Result; pending = null;
                if (n <= 0) break;
                all.Append(System.Text.Encoding.UTF8.GetString(buf, 0, n));
                if (all.ToString().Contains(marker, StringComparison.Ordinal)) break;
            }
        }
        finally
        {
            if (pending is { IsCompleted: false }) { cts.Cancel(); try { pending.Wait(5000); } catch (AggregateException) { } }
        }
        return all.ToString();
    }

    private static string TypeUntilEcho(Stream data, string line, string marker, int timeoutMs = 20000)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\r");
        var all = new System.Text.StringBuilder();
        var buf = new byte[16 * 1024];
        var sw = Stopwatch.StartNew();
        long nextTypeAt = 0;
        using var cts = new CancellationTokenSource();
        Task<int>? pending = null;
        try
        {
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (sw.ElapsedMilliseconds >= nextTypeAt)
                {
                    data.Write(bytes); data.Flush();
                    nextTypeAt = sw.ElapsedMilliseconds + 2500;
                }
                pending ??= data.ReadAsync(buf, 0, buf.Length, cts.Token);
                if (!pending.Wait(250)) continue;
                int n = pending.Result; pending = null;
                if (n <= 0) break;
                all.Append(System.Text.Encoding.UTF8.GetString(buf, 0, n));
                if (all.ToString().Contains(marker, StringComparison.Ordinal)) break;
            }
        }
        finally
        {
            if (pending is { IsCompleted: false }) { cts.Cancel(); try { pending.Wait(5000); } catch (AggregateException) { } }
        }
        return all.ToString();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Osc11Query_ReachesTheClient_OnlyOnTheBundledConpty(bool bundled)
    {
        if (ExePath is null) return;
        string exe = BundledHostCopy();
        try
        {
            // No flag keeps the inbox conhost: a UI that predates the flag predates the replies too.
            using var client = Start(exe, bundled ? " --conpty bundled" : "");
            string id = client.Create(Guid.NewGuid().ToString(), 100, 24, "powershell.exe",
                new[] { "-NoProfile", "-Command", "[Console]::Write([char]27 + ']11;?' + [char]7); Write-Host done-probing" }, verbatim: false);
            using var att = client.Attach(id);
            string output = ReadUntil(att.Data, "done-probing", 20000);
            Assert.Contains("done-probing", output);
            Assert.Equal(bundled, output.Contains("\x1b]11;?"));
            client.Kill(id);
        }
        finally { DeleteHostCopy(exe); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reattach_ShowsTheScreenPrintedWhileDetached(bool bundled)
    {
        if (ExePath is null) return;
        string exe = BundledHostCopy();
        try
        {
            // The inbox conhost repaints on the resize jiggle; on the bundled ConPTY the host sends its
            // own emulator's screen instead, since that ConPTY does not repaint (#339).
            using var client = Start(exe, bundled ? " --conpty bundled" : " --conpty inbox");
            string id = client.Create(Guid.NewGuid().ToString(), 100, 24, "powershell.exe", ConPtyConnectionTests.BottomRowProbeArgs, verbatim: false);
            Thread.Sleep(5000);
            // Twice: a reattach must not cost the screen anything the next one would miss (the bottom row).
            for (int i = 0; i < 2; i++)
            {
                using var att = client.Attach(id, repaint: true);
                Assert.Contains(ConPtyConnectionTests.BottomRowMarker, ReadUntil(att.Data, ConPtyConnectionTests.BottomRowMarker, 10000));
            }
            client.Kill(id);
        }
        finally { DeleteHostCopy(exe); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DetachedSession_OnTheBundledConpty_HasItsQueriesAnswered(bool withTheme)
    {
        if (ExePath is null) return;
        string exe = BundledHostCopy();
        try
        {
            // The bundled ConPTY forwards the child's queries; with no client attached, the host answers them:
            // the DSR always, the color with the theme from the pane's environment, else not at all.
            using var client = Start(exe, " --conpty bundled");
            string outFile = Path.Combine(Path.GetDirectoryName(exe)!, "dsr.txt");
            string id = client.Create(Guid.NewGuid().ToString(), 100, 24, "powershell.exe", ConPtyConnectionTests.DsrProbeArgs(outFile),
                env: ConPtyConnectionTests.ThemeEnv(withTheme), verbatim: false);
            Assert.Equal(ConPtyConnectionTests.ExpectedDsrProbe(withTheme), ConPtyConnectionTests.ReadDsrProbe(outFile));
            client.Kill(id);
        }
        finally { DeleteHostCopy(exe); }
    }

    /// <summary>A copy of the host exe with the shipped ConPTY beside it (conpty.dll, x64/OpenConsole.exe),
    /// which is where the host looks for it (#339).</summary>
    private static string BundledHostCopy()
    {
        string dir = Directory.CreateTempSubdirectory("agw-rusthost-conpty-").FullName;
        string exe = Path.Combine(dir, "agwinterm-ptyhost.exe");
        File.Copy(ExePath!, exe);
        File.Copy(Path.Combine(ConPtyConnectionTests.BundledDir, "conpty.dll"), Path.Combine(dir, "conpty.dll"));
        Directory.CreateDirectory(Path.Combine(dir, "x64"));
        File.Copy(Path.Combine(ConPtyConnectionTests.BundledDir, "x64", "OpenConsole.exe"), Path.Combine(dir, "x64", "OpenConsole.exe"));
        return exe;
    }

    private void DeleteHostCopy(string exe)
    {
        Dispose();   // the host holds its exe open
        try { Directory.Delete(Path.GetDirectoryName(exe)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void CreationTickets_ReconcileLostRepliesAndGuardReusedIds()
    {
        if (ExePath is null) return;
        using var client = Start();
        CreationProtocolAssertions.LostRepliesAndReusedId(_appId);
    }

    [Fact]
    public async Task CreationTickets_StartupSweepProtectsUnpublishedPane()
    {
        Assert.NotNull(ExePath);
        using var client = Start();
        await CreationProtocolAssertions.StartupSweepKeepsPendingPane(_appId);
    }

    [Fact]
    public void CreateAttachTypeKill_RoundTrips()
    {
        if (ExePath is null) return;
        using var client = Start();
        string id = client.Create(Guid.NewGuid().ToString(), 100, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        using var att = client.Attach(id);
        Assert.False(att.HasExited);
        Assert.True(att.ChildPid > 0);
        Assert.Contains("rust+host+works", TypeUntilEcho(att.Data, "echo rust+host+works", "rust+host+works"));
        client.Kill(id);
        Assert.Empty(client.List());
    }

    [Fact]
    public void DetachedSurvives_ReattachSeedsScrollback()
    {
        if (ExePath is null) return;
        using var client = Start();
        string id = client.Create(Guid.NewGuid().ToString(), 100, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        using (var first = client.Attach(id))
            Assert.Contains("before-detach", TypeUntilEcho(first.Data, "echo before-detach", "before-detach"));

        Assert.True(WaitFor(() =>
        {
            var i = client.List().Single();
            return !i.HasExited && !i.Attached;
        }), "detach must leave the session running, unattached");

        Thread.Sleep(300);   // let the echo land in the HOST emulator (its snapshot feeds reattach)
        using var second = client.Attach(id, repaint: true);
        client.Resize(id, 132, 40); // may arrive during the reconnect repaint's two-resize transaction
        bool inHistory = second.Scrollback.Any(l => l.Contains("before-detach"));
        var emu = new TerminalEmulator(second.Cols, second.Rows);
        emu.Feed(System.Text.Encoding.UTF8.GetBytes(second.Modes));   // modes replay parses cleanly
        Assert.True(inHistory || TypeUntilEcho(second.Data, "echo probe", "probe").Length > 0,
            "reattach must hand back a live stream");
        Assert.Contains("after-reattach", TypeUntilEcho(second.Data, "echo after-reattach", "after-reattach"));
        var resized = Assert.Single(client.List());
        Assert.Equal((132, 40), (resized.Cols, resized.Rows));
        client.Kill(id);
    }

    [Fact]
    public void Reattach_RustHostBlob_RestoresColorsInCSharp()
    {
        if (ExePath is null) return;
        using var client = Start();
        string gateName = @"Local\agwinterm-color-" + Guid.NewGuid().ToString("N");
        using var outputGate = new EventWaitHandle(false, EventResetMode.ManualReset, gateName);
        // Same create/attach barrier as ServerSessionTests (#258): synthetic output must start
        // after the host installed the sink, not race its scheduling. No interactive input echo.
        string script = $"$g=[Threading.EventWaitHandle]::OpenExisting('{gateName}'); if (-not $g.WaitOne(60000)) {{ exit 41 }}; $g.Dispose(); " +
            "$e=[char]27; Write-Host ($e+'[31mCOLORLINE'+$e+'[0m'); 1..14|%{'.'}; 'SCROLL-READY'; Start-Sleep 3600";
        string id = client.Create(Guid.NewGuid().ToString(), 60, 10, "powershell.exe",
                                  new[] { "-NoLogo", "-NoProfile", "-Command", script });
        using (var first = client.Attach(id))
        {
            Assert.True(WaitFor(() => client.List().Any(s => s.Id == id && s.Attached), 10000), "Rust host never attached the output sink");
            outputGate.Set();
            Assert.Contains("SCROLL-READY", ReadUntil(first.Data, "SCROLL-READY", 60000));
        }
        Thread.Sleep(300);   // small grace for the host emulator to ingest the final bytes

        using var second = client.Attach(id, repaint: true);
        // The Rust host must produce an attributed blob that the C# BufferPersist restores with
        // colour — cross-language proof the Rust serializer is byte-compatible with the C# one.
        Assert.NotNull(second.ScrollbackBlob);
        Assert.True(Agwinterm.Core.BufferPersist.TryParse(second.ScrollbackBlob!, out var pbuf));
        var emu = new TerminalEmulator(60, 10);
        Agwinterm.Core.BufferPersist.Restore(emu, pbuf);
        bool coloured = false;
        for (int h = 0; h < emu.HistoryCount && !coloured; h++)
        {
            if (!emu.DumpHistoryRow(h).Contains("COLORLINE")) continue;
            for (int c = 0; c < emu.Screen.Cols; c++)
                if (emu.GetHistoryCell(h, c).Rune == 'C' && emu.GetHistoryCell(h, c).FgSpec.Kind != ColorSpecKind.Default)
                    coloured = true;
        }
        Assert.True(coloured, "Rust-host reattach blob did not restore the scrollback colour in C#");
        client.Kill(id);
    }

    [Fact]
    public void ChildExit_TravelsViaList()
    {
        if (ExePath is null) return;
        using var client = Start();
        string id = client.Create(Guid.NewGuid().ToString(), 80, 24, "cmd.exe", new[] { "/q", "/c", "exit", "42" }, verbatim: true);
        Assert.True(WaitFor(() => client.List() is [{ HasExited: true, ExitCode: 42 }]),
            "exit code 42 must travel via list");
        client.Kill(id);
    }

    /// <summary>#246: the Rust host settles the pump before the exit watcher closes the data pipe
    /// (the client's exit signal), the same 50 ms window as TerminalSession — so everything the
    /// program wrote is on the stream before its EOF. Read to EOF, then look for the last line.</summary>
    [Fact]
    public void DataEof_ComesAfterTheLastOutput()
    {
        if (ExePath is null) return;
        using var client = Start();
        string id = client.Create(Guid.NewGuid().ToString(), 100, 24, "cmd.exe", new[] { "/q", "/c", "echo settle-marker-246 & exit 3" }, verbatim: true);
        using var att = client.Attach(id);
        string all = ReadUntil(att.Data, "\u0001never-appears", 15000);   // returns at EOF
        Assert.Contains("settle-marker-246", all);
        Assert.True(WaitFor(() => client.List().Any(i => i.Id == id && i.HasExited)), "the session must report exited after its EOF");
    }

    [Fact]
    public void Resize_And_DuplicateCreate()
    {
        if (ExePath is null) return;
        using var client = Start();
        string id = client.Create("fixed-id", 80, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        client.Resize(id, 132, 40);
        var info = client.List().Single();
        Assert.Equal((132, 40), (info.Cols, info.Rows));
        Assert.Throws<InvalidOperationException>(() => client.Create("fixed-id", 80, 24, "cmd.exe", new[] { "/q" }));
        client.Kill(id);
    }

    [Fact]
    public void Shutdown_KillsSessionsAndExits()
    {
        if (ExePath is null) return;
        using var client = Start();
        client.Create(Guid.NewGuid().ToString(), 80, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        client.Shutdown();
        Assert.True(_host!.WaitForExit(5000), "shutdown must exit the host process");
    }
}
