using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>
/// The two words <c>session overlay --pane</c> accepts, in agterm's vocabulary, and every refusal a
/// pane-scoped overlay can answer (P5). The rule itself — three slots per session, <c>left</c> =
/// pane 0 and <c>right</c> = pane 1 whatever the axis, the pane overlay as the pane's SURFACE while
/// it is open — is stated ONCE, on <see cref="ISessionHost.SessionOverlay"/>; this file holds the
/// words and the spellings, not a paraphrase of it.
///
/// Kept in this assembly, beside <see cref="SplitAxes"/> and <see cref="SwapReply"/>, so the control
/// server refuses a bad word before the host is reached, the fake host exercises the same refusals
/// the app gives, and no refusal string is typed twice (the #248 lesson: a refusal names only what
/// its guard SAW, and a second spelling of it drifts). Every refusal that has an agterm phrase
/// starts with that phrase VERBATIM, then a colon, then what our guard saw.
///
/// The words are case-sensitive: they are the wire spelling, and <c>Left</c> or <c>top</c> is a
/// caller guessing — a guess accepted here would be a slot the tree read back in a spelling the
/// caller never sent. A pane ID is refused on <c>--pane</c> for the reason #213 refuses it on
/// <c>--target</c>: two vocabularies for one thing, and a reply that could silently widen.
/// </summary>
public static class OverlayPanes
{
    /// <summary>Pane 0 — whatever the axis (on a horizontal split, the TOP pane).</summary>
    public const string Left = "left";
    /// <summary>Pane 1 — whatever the axis (on a horizontal split, the BOTTOM pane).</summary>
    public const string Right = "right";

    /// <summary>The arg key <c>session.overlay</c> reads the word from.</summary>
    public const string Key = "pane";
    /// <summary>The tree key the open pane slots are read back under: <c>"paneOverlays":["left"]</c>,
    /// <c>["right"]</c> or <c>["left","right"]</c>, OMITTED when no pane slot is open (absence =
    /// none, like <c>overlay</c>), independent of <c>overlay</c> (the session-wide slot).</summary>
    public const string TreeKey = "paneOverlays";
    /// <summary>The key <c>copy</c> and <c>text</c> answer under: <c>{"text":…}</c> (agterm's
    /// <c>result.text</c>).</summary>
    public const string TextKey = "text";

    /// <summary>The index <see cref="TryParse"/> answers for the flag omitted: the session-wide slot.</summary>
    public const int SessionWide = -1;

    /// <summary>
    /// Parse a <c>--pane</c> word. <paramref name="raw"/> null means "not given": <paramref name="index"/>
    /// is <see cref="SessionWide"/> and the call succeeds — the session-wide slot, today's behaviour
    /// byte for byte. Anything else must be exactly <see cref="Left"/> (0) or <see cref="Right"/> (1);
    /// otherwise false, <paramref name="refusal"/> names the value, both words and the absent form,
    /// and the caller must open, close or read nothing.
    /// </summary>
    public static bool TryParse(string? raw, out int index, out string? refusal)
    {
        index = SessionWide; refusal = null;
        if (raw is null) return true;
        if (raw == Left) { index = 0; return true; }
        if (raw == Right) { index = 1; return true; }
        refusal = Refusal(raw);
        return false;
    }

    /// <summary>The word for a pane index, the inverse of <see cref="TryParse"/> — what the tree
    /// spells slot <paramref name="index"/> as.</summary>
    public static string Word(int index) => index switch
    {
        0 => Left,
        1 => Right,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "a session has at most two panes; only 0 and 1 have a word"),
    };

    /// <summary>The refusal for a word that is neither pane. A string is QUOTED; a non-string (a
    /// number, an object — the server's case) arrives as its raw JSON, its own delimiter, as
    /// <see cref="SplitAxes.OpRefusal"/> does it.</summary>
    public static string Refusal(string raw, bool quoted = true) =>
        $"--pane {(quoted ? $"'{raw}'" : raw)} is not one of {Left} (pane 0) or {Right} (pane 1); omit --pane for the session-wide overlay. Nothing opened, closed or read.";

    // ---- agterm's phrases, verbatim as the head of each refusal ----

    /// <summary>agterm: the pane the slot names is not on screen. We never hide a pane, so the one
    /// case is <c>--pane right</c> on a session with one pane.</summary>
    public const string NotVisible = "pane not visible";
    public static string NotVisibleRefusal(string sessionId) =>
        $"{NotVisible}: session {sessionId} has one pane; pass --pane {Left} or omit --pane";
    /// <summary>The same phrase for a host with no panes at all (<see cref="SingleSessionHost"/>).</summary>
    public const string NotVisibleNoPanes = NotVisible + ": this host has no panes to scope an overlay to; omit --pane";

    /// <summary>agterm: <c>open --pane X</c> while X holds one. NO silent replace — the session-wide
    /// slot replaces (today, unchanged); the pane slot refuses, agterm's rule.</summary>
    public const string AlreadyOpen = "pane overlay already open";
    public static string AlreadyOpenRefusal(int index) =>
        $"{AlreadyOpen}: close it first (session overlay close --pane {Word(index)}), or read it (result / copy / text)";

    /// <summary>agterm: <c>copy</c> / <c>text</c> with nothing in the named slot. The pane arm
    /// names which slot; the session-wide one is the bare phrase (the shape <c>close</c> answers
    /// ok with today).</summary>
    public const string NoOverlay = "no overlay";
    public static string NoOverlayRefusal(int index) =>
        index == SessionWide ? NoOverlay : $"{NoOverlay}: --pane {Word(index)} names which slot, and nothing is open in it";

    /// <summary>agterm: <c>copy</c> / <c>text</c> between <c>open</c> and the terminal being up.
    /// Probably unreachable here (CreatePane builds the emulator synchronously); if it is, this is
    /// still the contract for a pane whose surface has no emulator yet — not fabricated to match.</summary>
    public const string NotRealized = "overlay not realized";
    public static string NotRealizedRefusal(string overlayId) =>
        $"{NotRealized}: {overlayId} has no terminal to read yet";

    /// <summary>agterm: <c>copy</c> with nothing selected inside the overlay.</summary>
    public const string NoSelection = "no selection";

    /// <summary>agterm: <c>text</c> when the emulator read throws; the exception message follows.</summary>
    public const string ReadFailed = "failed to read surface buffer";
    public static string ReadFailedRefusal(string message) => $"{ReadFailed}: {message}";

    /// <summary>agterm: <c>result --pane X</c> while X's program is still running.</summary>
    public const string StillRunning = "overlay still running";

    /// <summary>agterm: <c>result --pane X</c> when nothing has run in X since the window opened —
    /// also the value a pane slot's last result starts as.</summary>
    public const string NoResult = "no overlay result";

    // ---- ours: the usage errors, the same words at the CLI and the server ----

    /// <summary>A pane overlay is always full-pane (agterm has no floating pane overlay). The CLI
    /// appends "Nothing sent."; the server "Nothing opened." — the sentence is the one spelling.</summary>
    public const string SizeWithPane = "--pane and --size-percent cannot be combined: a pane overlay is always full-pane";
    public const string SizeWithPaneRefusal = SizeWithPane + ". Nothing opened.";

    /// <summary><c>resize --pane</c>: nothing to resize to.</summary>
    public const string ResizeWithPane = "resize --pane: a pane overlay is always full-pane and cannot be resized; omit --pane to resize the session-wide overlay";
    public const string ResizeWithPaneRefusal = ResizeWithPane + ". Nothing resized.";

    /// <summary>The agreement check: <c>--target</c> may be the session id, either pane id, or a pane
    /// overlay's own id, but one that names the OTHER side than <c>--pane</c> is refused — the caller
    /// named two panes. <paramref name="overlay"/>: the target was the overlay's id, not the pane's.</summary>
    public static string Disagree(string target, int targetIndex, int paneIndex, bool overlay = false) =>
        $"'{target}' is the {Word(targetIndex)} pane{(overlay ? "'s overlay" : "")}; --pane {Word(paneIndex)} names the other one. Nothing opened.";

    /// <summary>A pane overlay's own id on <c>--target</c> with <c>--pane</c> omitted names its slot (the
    /// rule: the id reaches the overlay from anywhere), so the two usage refusals for that slot name
    /// the id the guard saw, not a <c>--pane</c> the caller never passed — as does
    /// <see cref="OverlayIdGoneRefusal"/>, for an id that stopped resolving between the pipe thread's
    /// inference and the UI hop (its overlay closed, or its pane went, in flight).</summary>
    public static string OverlayIdGoneRefusal(string target) =>
        $"'{target}' no longer names an open pane overlay: it closed, or its pane went, while this call was in flight; read tree and retry. Nothing done.";

    public static string OverlayIdResizeRefusal(string target, int index) =>
        $"'{target}' is the {Word(index)} pane's overlay, which is always full-pane and cannot be resized; pass the session id to resize the session-wide overlay. Nothing resized.";
    public static string OverlayIdSizeRefusal(string target, int index) =>
        $"'{target}' is the {Word(index)} pane's overlay, and a pane overlay is always full-pane: --size-percent does not apply. Nothing opened.";

    /// <summary><c>--lines</c> on <c>session text</c> and <c>session overlay text</c> is a whole number of
    /// lines, 0 or more; anything else is refused at the CLI (nothing sent) and at the server alike —
    /// it used to be DROPPED, so <c>--lines 5O</c> read the screen and reported success. A string is
    /// QUOTED; a non-string arrives as its raw JSON, as <see cref="Refusal"/> does it.</summary>
    public static string LinesRefusal(string raw, bool quoted = true) =>
        $"--lines needs a whole number of lines (0 = the visible screen), not {(quoted ? $"'{raw}'" : raw)}. Nothing read.";

    /// <summary><c>text</c>'s <c>--all</c> and <c>--lines</c> are exclusive, on <c>session text</c>
    /// and <c>session overlay text</c> alike (one reader, two verbs).</summary>
    public const string AllWithLines = "--all and --lines cannot be combined: --all reads the whole buffer (screen + scrollback), --lines N the last N lines; pass one. Nothing read.";

    /// <summary>The reply object for <c>copy</c> and <c>text</c>: <c>{"text":…}</c>.</summary>
    public static string TextReply(string text) =>
        "{\"" + TextKey + "\":" + JsonSerializer.Serialize(text) + "}";
}

/// <summary>What <c>text</c> reads: <see cref="All"/> = the whole buffer (screen + scrollback);
/// else <see cref="Lines"/> = the last N lines ending at the bottom of the screen (0 = the visible
/// screen, today's meaning). The two are exclusive; the server refuses the pair with
/// <see cref="OverlayPanes.AllWithLines"/> before a host sees it. Decided once, here, for
/// <see cref="ISessionHost.SessionOverlay"/> and <c>session.text</c> alike.</summary>
public sealed record OverlayTextArgs(bool All, int Lines)
{
    /// <summary>The visible screen — the default of both verbs.</summary>
    public static readonly OverlayTextArgs Screen = new(false, 0);
}
