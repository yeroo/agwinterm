using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>
/// What a host returns from <see cref="ISessionHost.Swap"/>: the session the swap landed on and its
/// pane order, focus and axis AFTER the swap, read back off the session inside the UI hop so the
/// reply describes a state that exists. <see cref="Refusal"/> non-null means nothing moved and nothing
/// was saved; the server turns it into ok:false. The <see cref="RestoreCaptureResult"/> shape.
/// </summary>
public sealed record SwapResult(string SessionId, IReadOnlyList<string> PaneIds, int FocusedPane, string Axis, string? Refusal = null)
{
    public static SwapResult Refuse(string refusal) => new("", Array.Empty<string>(), 0, SplitAxes.Vertical, refusal);
}

/// <summary>
/// <c>session swap</c> (P4): exchange the two panes of a split session. The reply shape and every
/// refusal wording live here, beside <see cref="SplitCloseReply"/> and <see cref="SplitAxes"/>, so the
/// app host, the fake host and the server share one spelling. The reply is
/// <c>{"session","paneIds":[…],"focusedPane","axis"}</c> — the split block of the session's tree node
/// after the swap, so a caller reads the new order without a second round trip.
///
/// <b>What a swap moves, and what it does not.</b> The pane ORDER is reversed and the FOCUS follows
/// the pane (the shell the user was typing into is still the one they are typing into, on the other
/// side). The axis is kept, and the ratio SEQUENCE is kept — the left/top box stays the size it was;
/// the contents change places (agterm: "axis and divider ratio remain fixed"; in our representation,
/// where each pane owns its share, that is the two panes exchanging their values). Every id is kept:
/// <b>a swap moves panes, never ids</b>. agterm's swap moves the session's public identity to the
/// other shell, because its panes are addressed by role (<c>primary|split</c>); ours are addressed by
/// id, and an agent holding a pane id must keep reaching the same shell after a swap. So the session
/// id keeps naming the pane it always named, which now sits on the other side — "the first pane shares
/// the session id" becomes "exactly one pane carries the session id, and a swap may put it on either
/// side". Nothing else moves: overlays and scratch are session-wide (until P5), the status aggregate
/// is order-independent, context, flag, MRU and broadcast are session-keyed, events carry pane ids.
///
/// Resolution: null / "" / "active" = the active session; else the content verbs' resolver (a
/// session id, either pane's id, a prefix of any of them, or a session name) — the session either
/// pane belongs to is the one swapped. A cover is refused (not a side of a split), an unknown
/// target is refused, a one-pane session is refused (there is nothing to exchange, and an ok:true
/// for it would be the silent-success class). Each refusal leaves the world untouched.
/// </summary>
public static class SwapReply
{
    public static string Build(SwapResult result) => Build(result.SessionId, result.PaneIds, result.FocusedPane, result.Axis);

    public static string Build(string sessionId, IReadOnlyList<string> paneIds, int focusedPane, string axis)
    {
        var sb = new StringBuilder("{\"session\":").Append(JsonSerializer.Serialize(sessionId)).Append(",\"paneIds\":[");
        for (int i = 0; i < paneIds.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(paneIds[i]));
        }
        return sb.Append("],\"focusedPane\":").Append(focusedPane.ToString(System.Globalization.CultureInfo.InvariantCulture))
                 .Append(",\"axis\":").Append(JsonSerializer.Serialize(axis)).Append('}').ToString();
    }

    /// <summary>A target that names no pane and no session.</summary>
    public static string UnknownTarget(string target) =>
        $"swap: no pane or session matches '{target}'. Nothing moved.";

    /// <summary>No target, and no active session to take it as.</summary>
    public const string NoActiveSession =
        "swap: there is no active session to swap the panes of. Nothing moved.";

    /// <summary>The session has one pane: there is nothing to exchange. <c>session split on</c> is the verb
    /// that makes a split.</summary>
    public static string SinglePane(string sessionId) =>
        $"swap: session '{sessionId}' has one pane, so there is nothing to exchange; `session split on` makes a split. Nothing moved.";

    /// <summary>A scratch / overlay / quick cover: not a side of a split (the wording <c>split close</c>
    /// and <c>restore.capture</c> use for the same targets).</summary>
    public static string CoverPane(string paneId) =>
        $"swap: '{paneId}' is a scratch/overlay/quick pane, not a side of a split. Nothing moved.";
}
