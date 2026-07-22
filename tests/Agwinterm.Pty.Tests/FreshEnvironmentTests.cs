using Agwinterm.Pty;
using Microsoft.Win32;

namespace Agwinterm.Pty.Tests;

/// <summary>fresh-env: new sessions get an environment rebuilt from the registry at spawn, so
/// software installed while the app (or the pty-host) runs is visible without a restart.</summary>
public class FreshEnvironmentTests
{
    [Fact]
    public void TryBuild_ProducesACredibleUserEnvironment()
    {
        var env = FreshEnvironment.TryBuild();
        Assert.NotNull(env);
        Assert.True(env!.ContainsKey("SystemRoot"));
        Assert.True(env.ContainsKey("USERPROFILE"));
        Assert.False(string.IsNullOrEmpty(env["Path"]));
        Assert.True(env.ContainsKey("PATH"));                        // case-insensitive lookups
        Assert.DoesNotContain(env.Keys, k => k.StartsWith('='));     // no "=C:=..." pseudo-vars
    }

    /// <summary>The core claim: a registry env change (== an installer's work) is visible in the
    /// NEXT build without this process restarting — and gone again after the uninstall.</summary>
    [Fact]
    public void TryBuild_SeesRegistryChangesImmediately()
    {
        string name = "AGWTEST_FRESH_" + Guid.NewGuid().ToString("N")[..8];
        using var hkcu = Registry.CurrentUser.OpenSubKey("Environment", writable: true)!;
        try
        {
            Assert.False(FreshEnvironment.TryBuild()!.ContainsKey(name));
            hkcu.SetValue(name, "installed-just-now", RegistryValueKind.String);
            Assert.Equal("installed-just-now", FreshEnvironment.TryBuild()![name]);
            // NOTE: the var is deliberately NOT in this process's env — registry only.
            Assert.Null(Environment.GetEnvironmentVariable(name));
        }
        finally { try { hkcu.DeleteValue(name); } catch { } }
        Assert.False(FreshEnvironment.TryBuild()!.ContainsKey(name));
    }
}

/// <summary>End-to-end: the child shell actually SEES the fresh environment — in-process, over
/// the pty-host wire, and NOT when fresh-env is off.</summary>
public class FreshEnvironmentE2ETests : IDisposable
{
    private readonly string _name = "AGWTEST_FRESH_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _value = "fresh-proof-" + Guid.NewGuid().ToString("N")[..8];
    private readonly RegistryKey _hkcu;

    public FreshEnvironmentE2ETests()
    {
        _hkcu = Registry.CurrentUser.OpenSubKey("Environment", writable: true)!;
        _hkcu.SetValue(_name, _value, RegistryValueKind.String);     // "the installer ran"
    }

    public void Dispose()
    {
        try { _hkcu.DeleteValue(_name); } catch { }
        _hkcu.Dispose();
    }

    private static bool WaitFor(Func<bool> cond, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    private static string GridText(ISession s)
    {
        lock (s.SyncRoot) return string.Join("\n", s.Emulator.DumpBuffer());
    }

    /// <summary>Type-until-echoed (see ServerSessionTests.TypeLine): input during conhost init can
    /// be silently discarded, so retype until the marker shows; duplicates are harmless.</summary>
    private static void TypeLine(ISession s, string line, string marker)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\r");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            s.Write(bytes);
            if (WaitFor(() => GridText(s).Contains(marker), 2500)) return;
        }
        Assert.Fail($"typed line never echoed: {line}\ngrid:\n" + GridText(s));
    }

    [Fact]
    public async Task InProcess_ChildSeesTheRegistryVar_WithoutAppRestart()
    {
        using var s = new TerminalSession(100, 24);
        // extraEnv mirrors what CreatePane always passes; the var itself comes from the registry.
        await s.StartAsync("cmd.exe", new[] { "/q" }, verbatimCommandLine: true,
            extraEnv: new Dictionary<string, string> { ["AGWINTERM"] = "1" });
        // cmd expands %name% in its OUTPUT only if the var is in the child's env — the input echo
        // shows the literal %name%, so seeing _value proves expansion (and thus the fresh env).
        TypeLine(s, $"echo %{_name}%", _value);
    }

    [Fact]
    public async Task InProcess_FreshEnvOff_ChildInheritsTheStaleSnapshot()
    {
        using var s = new TerminalSession(100, 24);
        await s.StartAsync("cmd.exe", new[] { "/q" }, verbatimCommandLine: true, freshEnv: false);
        TypeLine(s, $"echo %{_name}%", $"%{_name}%");                // at least the input echo
        TypeLine(s, "echo sentinel+done", "sentinel+done");          // ...and the shell is responsive
        Assert.DoesNotContain(_value, GridText(s));                  // the var was NOT expanded
    }

    [Fact]
    public async Task PtyHost_FreshEnvCrossesTheWire()
    {
        string appId = "agwinterm-test-" + Guid.NewGuid().ToString("N")[..8];
        using var server = new PtyHostServer(appId);
        using var backend = new ServerSessionBackend(appId, exePath: null);
        using var s = backend.Create(Guid.NewGuid().ToString(), 100, 24);
        await s.StartAsync("cmd.exe", new[] { "/q" }, verbatimCommandLine: true);
        TypeLine(s, $"echo %{_name}%", _value);                      // host-side spawn rebuilt the env
    }
}
