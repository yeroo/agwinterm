using System.Text;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>The ConPTY choice (#339) and the connection agwinterm creates itself for it.</summary>
public class ConPtyConnectionTests
{
    [Fact]
    public void Resolve_Inbox_WhenAskedOrWhenTheBundledFilesAreMissing()
    {
        string dir = Directory.CreateTempSubdirectory("agw-conpty-").FullName;
        try
        {
            var asked = ConPtyApi.Resolve(bundled: false, dir);
            Assert.False(asked.Bundled);
            Assert.Contains("conpty = inbox", asked.Description);

            var noDll = ConPtyApi.Resolve(bundled: true, dir);
            Assert.False(noDll.Bundled);
            Assert.Contains("conpty.dll", noDll.Description);

            // conpty.dll without OpenConsole.exe would quietly run the inbox conhost: say so instead.
            File.WriteAllBytes(Path.Combine(dir, "conpty.dll"), Array.Empty<byte>());
            var noOpenConsole = ConPtyApi.Resolve(bundled: true, dir);
            Assert.False(noOpenConsole.Bundled);
            Assert.Contains("OpenConsole.exe", noOpenConsole.Description);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    internal static readonly string BundledDir = Path.Combine(AppContext.BaseDirectory, "conpty");

    internal const string BottomRowMarker = "on-the-bottom-row";

    /// <summary>PowerShell arguments that write <see cref="BottomRowMarker"/> on row 24 of a 24-row
    /// session and stay alive, for the pty-host reattach tests.</summary>
    internal static readonly string[] BottomRowProbeArgs =
        { "-NoProfile", "-Command", "[Console]::Write([char]27 + '[24;1H" + BottomRowMarker + "'); Start-Sleep 60" };

    /// <summary>PowerShell arguments that send an OSC 11 color query and then a DSR (CSI 5 n), read the
    /// replies as keys and write them to <paramref name="outFile"/> with ESC spelled out. The pty-host
    /// tests run it while no client is attached: the host answers the DSR and, not knowing the theme,
    /// leaves the color query unanswered. They read the file rather than the screen.</summary>
    internal static string[] DsrProbeArgs(string outFile) => new[]
    {
        "-NoProfile", "-Command",
        "[Console]::Write([char]27 + ']11;?' + [char]7 + [char]27 + '[5n'); $s = ''; $t = [Diagnostics.Stopwatch]::StartNew(); " +
        "while ($t.ElapsedMilliseconds -lt 8000 -and -not $s.EndsWith('n')) { if ([Console]::KeyAvailable) { $s += [Console]::ReadKey($true).KeyChar } else { Start-Sleep -Milliseconds 20 } }; " +
        $"Set-Content -LiteralPath '{outFile}' -Value ('dsr-reply=' + $s.Replace([string][char]27, 'ESC'))",
    };

    /// <summary>The pane environment a UI sends with Create, with or without the theme a pty-host needs
    /// to answer color queries while detached.</summary>
    internal static Dictionary<string, string>? ThemeEnv(bool withTheme) =>
        withTheme ? new() { [Agwinterm.Core.ThemeColors.EnvVar] = "cccccc;123456" } : null;

    /// <summary>What the DSR probe records: the OSC 11 reply only when the host knows the theme.</summary>
    internal static string ExpectedDsrProbe(bool withTheme) =>
        withTheme ? "dsr-reply=ESC]11;rgb:1212/3434/5656ESC\\ESC[0n" : "dsr-reply=ESC[0n";

    /// <summary>Wait for the DSR probe's file and return its text ("" if it never appeared).</summary>
    internal static string ReadDsrProbe(string outFile, int timeoutMs = 20000)
    {
        for (var sw = System.Diagnostics.Stopwatch.StartNew(); sw.ElapsedMilliseconds < timeoutMs; Thread.Sleep(100))
            if (File.Exists(outFile)) try { return File.ReadAllText(outFile).Trim(); } catch (IOException) { }
        return "";
    }

    [Fact]
    public void Resolve_Bundled_LoadsTheShippedConpty()
    {
        var api = ConPtyApi.Resolve(bundled: true, BundledDir);
        Assert.True(api.Bundled, api.Description);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Osc11Query_ReachesTheTerminal_OnlyThroughTheBundledConpty(bool bundled)
    {
        var api = ConPtyApi.Resolve(bundled, BundledDir);
        using var conn = ConPtyConnection.Spawn(api,
            "powershell.exe -NoProfile -Command \"[Console]::Write([char]27 + ']11;?' + [char]7); Write-Host done-probing\"",
            Environment.CurrentDirectory, environment: null, 80, 24, deElevate: false);
        string output = ReadAsTerminal(conn, "done-probing");
        Assert.Contains("done-probing", output);   // the child ran: a missing query is not a missing child
        Assert.Equal(bundled, output.Contains("\x1b]11;?"));
    }

    /// <summary>Read a connection's output until <paramref name="marker"/> or exit, answering each DA1
    /// the way agwinterm does (OpenConsole asks at startup and waits for the reply). Queries are counted
    /// in the accumulated output, so one split across two reads is still answered.</summary>
    private static string ReadAsTerminal(ConPtyConnection conn, string marker)
    {
        var output = new StringBuilder();
        var reader = Task.Run(() =>
        {
            var buf = new byte[4096];
            int n, answered = 0;
            while ((n = conn.ReaderStream.Read(buf, 0, buf.Length)) > 0)
            {
                int asked;
                lock (output)
                {
                    output.Append(Encoding.UTF8.GetString(buf, 0, n));
                    asked = CountOf(output.ToString(), "\x1b[c");
                }
                for (; answered < asked; answered++)
                    conn.WriterStream.Write(Encoding.ASCII.GetBytes(Agwinterm.Core.TerminalEmulator.Da1Reply));
                conn.WriterStream.Flush();
            }
        });
        for (int i = 0; i < 750 && !Snapshot().Contains(marker); i++) Thread.Sleep(20);
        return Snapshot();

        string Snapshot() { lock (output) return output.ToString(); }
    }

    private static int CountOf(string text, string what)
    {
        int count = 0;
        for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // de-elevated panes get the pane environment too (COLORTERM, AGWINTERM_*)
    public void Spawn_RunsTheCommandWithTheGivenEnvironment(bool deElevate)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = e.Value as string ?? "";
        env["AGW_CONPTY_TEST"] = "from-the-block";

        using var conn = ConPtyConnection.Spawn(ConPtyApi.Resolve(bundled: false, AppContext.BaseDirectory),
            "cmd.exe /c echo value=%AGW_CONPTY_TEST%", Environment.CurrentDirectory, env, 80, 24, deElevate);

        var output = new StringBuilder();
        var buf = new byte[4096];
        var read = Task.Run(() =>
        {
            int n;
            while ((n = conn.ReaderStream.Read(buf, 0, buf.Length)) > 0)
                lock (output) output.Append(Encoding.UTF8.GetString(buf, 0, n));
        });
        Assert.True(conn.WaitForExit(15000), "the child should exit");
        for (int i = 0; i < 250 && !Snapshot().Contains("value=from-the-block"); i++) Thread.Sleep(20);
        Assert.Contains("value=from-the-block", Snapshot());
        Assert.Equal(0, conn.ExitCode);

        string Snapshot() { lock (output) return output.ToString(); }
    }
}
