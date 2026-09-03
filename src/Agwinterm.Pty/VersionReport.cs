using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>
/// `agwintermctl version` — which CLI binary ran, and which app is serving the pipe.
///
/// This machine routinely has several `agwintermctl.exe` (the install directory and one per source
/// build tree), none of them on PATH, and several instances an agent could be talking to. "Which
/// binary did I just run, and which app did it reach" is the question; the answer is two greppable
/// lines, `cli` and `app`.
///
/// The app half is a `ping` round trip, so nothing new is needed server-side. It is deliberately
/// allowed to fail: a diagnostic that only works when the thing being diagnosed is up is useless in
/// the one case it exists for, so a dead pipe still prints the CLI half and still exits 0.
/// </summary>
public static class VersionReport
{
    /// <summary>What `version` found. <see cref="App"/> is null when nothing answered the pipe.</summary>
    public sealed record Report(string CliVersion, string CliPath, string Pipe, string? App)
    {
        public bool AppAvailable => App is not null;
        /// <summary>The pipe as the OS names it, so the string can be pasted into other tools.</summary>
        public string PipePath => @"\\.\pipe\" + Pipe;
    }

    /// <summary>The CLI's own version — see <see cref="EntryAssemblyVersion"/>.</summary>
    public static string CliVersion() => EntryAssemblyVersion();

    /// <summary>The entry assembly's informational version, stamped by the release build via
    /// -p:Version ("1.0.0" in unstamped dev builds), without build metadata. The one formatting rule
    /// behind both lines `version` prints — the CLI's own, and the app's as `ping` reports it — so
    /// the two can never drift apart in the output whose purpose is comparing them.</summary>
    internal static string EntryAssemblyVersion()
    {
        string v = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "dev";
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    /// <summary>The resolved path of the executable that actually ran — the whole point of the CLI
    /// half, since several builds of the same name coexist here.</summary>
    public static string CliPath() => Environment.ProcessPath ?? "(unknown)";

    /// <summary>Ping <paramref name="pipeName"/>; returns the app's reply, or null if nothing answered
    /// within <paramref name="timeoutMs"/>. The budget covers the whole probe, not just the connect:
    /// a process that owns the name but never replies — a starved thread pool, a stranger behind
    /// <c>--pipe</c> — must not hang the one command that exists for the app being unhealthy.</summary>
    public static string? Probe(string pipeName, int timeoutMs = 3000)
    {
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            // Asynchronous so the read below can actually be cancelled: a synchronous pipe handle
            // ignores the token once the read has started.
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(timeoutMs);
            int remainingMs = timeoutMs - (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remainingMs <= 0) return null;
            using var cts = new CancellationTokenSource(remainingMs);
            // The write is bounded too, not just the read: the app's server pipes have no buffer of
            // their own, so an owner that accepted but never reads blocks even these 16 bytes.
            pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"ping\"}\n"), cts.Token).AsTask().GetAwaiter().GetResult();
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            string? response = reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
            if (response is null) return null;
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;
            return root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String
                ? res.GetString() : null;
        }
        catch { return null; }   // no app, wrong pipe, a reply we can't parse — all "unavailable"
    }

    public static Report Build(string pipeName, int timeoutMs = 3000)
        => new(CliVersion(), CliPath(), pipeName, Probe(pipeName, timeoutMs));

    /// <summary>Two lines, each tagged so `version | grep ^app` is a usable one-liner.</summary>
    public static string RenderText(Report r)
        => $"cli {r.CliVersion} {r.CliPath}\n" +
           $"app {(r.App ?? "unavailable")} (pipe {r.PipePath})";

    public static string RenderJson(Report r)
    {
        var obj = new Dictionary<string, object?>
        {
            ["cli"] = new Dictionary<string, object?> { ["version"] = r.CliVersion, ["path"] = r.CliPath },
            ["app"] = new Dictionary<string, object?>
            {
                ["available"] = r.AppAvailable,
                ["version"] = r.App,
                ["pipe"] = r.Pipe,
                ["pipePath"] = r.PipePath,
            },
        };
        return JsonSerializer.Serialize(obj);
    }
}
