using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>
/// One real pane's result from <c>restore.capture</c>: the pane, the session that owns it, and what
/// was captured into its restore slot — the foreground command the shell was running, or
/// <b>null</b> when the shell had no non-denylisted child. Null is the honest answer for "nothing
/// running" and is DISTINCT from a failed process query, which is a refusal
/// (<see cref="RestoreCaptureReply.QueryFailed"/>) and captures nothing for anyone.
/// </summary>
public sealed record CapturedPane(string PaneId, string SessionId, string? Captured);

/// <summary>
/// What a host returns from <see cref="ISessionHost.RestoreCapture"/>: the panes whose slots were
/// written, in tree order, and the value of the <c>restore-commands</c> toggle
/// (<see cref="ReplayOnRestore"/>) — the one thing the caller cannot otherwise learn: whether the
/// slot it just filled will be typed back at the next restart. <see cref="Refusal"/> non-null means
/// nothing was captured and nothing was saved; the server turns it into ok:false.
/// </summary>
public sealed record RestoreCaptureResult(IReadOnlyList<CapturedPane> Panes, bool ReplayOnRestore, string? Refusal = null)
{
    public static RestoreCaptureResult Refuse(string refusal) => new(Array.Empty<CapturedPane>(), false, refusal);
}

/// <summary>
/// The reply shape and the refusal wording for <c>restore.capture</c>, built here so the app host,
/// the fake host and the server share one spelling. The reply is
/// <c>{"captured":n,"replayOnRestore":bool,"panes":[{"pane","session","captured":string|null}]}</c>:
/// <c>captured</c> counts the panes with a non-null capture, <c>panes</c> lists every real pane the
/// verb reached (all of them, or the one <c>--target</c> named), and <c>capturedCommands</c> on
/// <c>tree</c> is the read-back.
///
/// <b>This shape is ours, not agterm's.</b> agterm's parity entry for the verb is one sentence and
/// states no reply; per-pane objects with null for "nothing running" match <c>session.restore</c>'s
/// reply and the tree's <c>restoreCommands</c>. The conformance step in the sibling contract PR fixes
/// it as the family's answer.
/// </summary>
public static class RestoreCaptureReply
{
    /// <summary>The tree key the slot is read back under, keyed by pane id like <c>restoreCommands</c>.</summary>
    public const string TreeKey = "capturedCommands";

    public static string Build(RestoreCaptureResult result)
    {
        var sb = new StringBuilder("{\"captured\":").Append(result.Panes.Count(p => p.Captured is not null))
            .Append(",\"replayOnRestore\":").Append(result.ReplayOnRestore ? "true" : "false")
            .Append(",\"panes\":[");
        for (int i = 0; i < result.Panes.Count; i++)
        {
            var p = result.Panes[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"pane\":").Append(JsonSerializer.Serialize(p.PaneId))
              .Append(",\"session\":").Append(JsonSerializer.Serialize(p.SessionId))
              .Append(",\"captured\":").Append(p.Captured is null ? "null" : JsonSerializer.Serialize(p.Captured))
              .Append('}');
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>A target that names no pane and no session — the verb's own wording, not the flat
    /// "no session", so a script sees which verb refused and that nothing was written.</summary>
    public static string UnknownTarget(string target) =>
        $"restore capture: no pane or session matches '{target}'. Nothing captured, nothing saved.";

    /// <summary>A target that is present but EMPTY. Omitting the target is the documented "every real
    /// pane"; an empty one is a caller that meant to name something and built the request wrong, and
    /// broadening it to every pane would clear the checkpoint of every idle pane in the window on a
    /// typo (revmux r1). Refused in ControlServer, before the host is asked.</summary>
    public const string EmptyTarget =
        "restore capture: the target is empty. Omit --target to capture every real pane, or name one pane or session. Nothing captured, nothing saved.";

    /// <summary>A scratch / overlay / quick cover: it has no restore slot (covers are never in the saved
    /// tree), so there is nothing to capture into — the refusal <c>session.restore</c> gives a pin there.</summary>
    public static string CoverPane(string paneId) =>
        $"restore capture: '{paneId}' is a scratch/overlay/quick pane, which is never restored, so it has no restore slot to capture into. Nothing captured, nothing saved.";

    /// <summary>The process query (one CIM snapshot for every pane) failed or timed out. Refused rather
    /// than reported as "nothing running" everywhere: an empty answer from a dead query would write
    /// null into every slot and look exactly like a quiet desk.</summary>
    public const string QueryFailed =
        "restore capture: the process query failed or timed out, so what each shell is running is unknown. Nothing captured, nothing saved.";
}
