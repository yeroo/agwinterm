namespace Agwinterm.Pty;

/// <summary>
/// <c>session split close</c> (P4): close ONE pane of a split session — either side — and reply with
/// the survivor's id (a bare string, like every <c>session split</c> reply). The refusal wordings
/// live here, beside <see cref="SplitAxes"/> and <see cref="RestoreCaptureReply"/>, so the fake host
/// exercises the sentences the app gives and a reader finds every reason the verb can say no in one
/// place. Each refusal leaves the world untouched: nothing closed, nothing saved.
///
/// Why the verb exists: agterm's <c>session split close</c> destroys a pane, and the tracker filed
/// ours as "naming, not behaviour" because <c>session split off</c> already destroys the split shell.
/// It was behaviour: <c>off</c> hard-codes pane 0 as the survivor, so no control verb could close
/// pane 0 of two — only Ctrl+Shift+W, on whichever pane the user had focused. This verb closes the
/// TARGETED pane, and the survivor becomes pane 0 with the full width or height and the focus.
///
/// Resolution is the content verbs' (<c>FindControlPane</c>: exact pane, exact session → its focused
/// pane, pane prefix, session prefix / name → its focused pane); no target or <c>active</c> is the
/// active session's focused pane, what Ctrl+Shift+W closes. A target of the session id lands on the
/// pane that carries it while one does (the exact-pane arm, the order <c>win32-control.ps1</c> pins)
/// — and this verb is one of the ways that pane goes away, after which the session id names the
/// FOCUSED pane like a name does (<c>win32-control.ps1</c> pins that too: after closing the carrier,
/// <c>session text</c> by the session id reaches the survivor). The rule in full is on
/// <see cref="ISessionHost.SplitClose"/>.
/// </summary>
public static class SplitCloseReply
{
    /// <summary>A target that names no pane and no session.</summary>
    public static string UnknownTarget(string target) =>
        $"split close: no pane or session matches '{target}'. Nothing closed.";

    /// <summary>No target, and no active session to take it as.</summary>
    public const string NoActiveSession =
        "split close: there is no active session to close a pane of. Nothing closed.";

    /// <summary>The session has one pane: there is no split to close. <c>session close</c> is the verb
    /// that closes a session, and a <c>split close</c> that quietly did that instead would be the
    /// silent-success class one verb over.</summary>
    public static string SinglePane(string sessionId) =>
        $"split close: session '{sessionId}' has one pane, so there is no split to close; `session close` closes the session. Nothing closed.";

    /// <summary>A scratch / overlay / quick cover: not a side of a split, and each has its own verb
    /// to dismiss it (the wording <c>restore.capture</c> uses for the same targets).</summary>
    public static string CoverPane(string paneId) =>
        $"split close: '{paneId}' is a scratch/overlay/quick pane, not a side of a split; `session scratch off`, `session overlay close` or `quick off` dismiss those. Nothing closed.";
}
