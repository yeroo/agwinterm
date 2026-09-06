namespace Agwinterm.Pty;

/// <summary>
/// The two words <c>session split --axis</c> accepts, in agterm's vocabulary (agterm.com/commands):
/// <b><c>vertical</c> produces LEFT/RIGHT panes — the side-by-side layout, and the default of a
/// session that has never been split; <c>horizontal</c> produces TOP/BOTTOM panes.</b> The axis
/// names the ARRANGEMENT of the panes, never the divider between them (a vertical split has a
/// vertical hairline between two columns — the word describes the cut, not the line). Stated here
/// once; <c>ISessionHost.Split</c>, the skill file and the CLI usage header point at it.
///
/// Kept in this assembly, beside <see cref="SessionContexts"/> and <see cref="SidebarWidths"/>, so
/// the control server refuses a bad word before the host is reached, the fake host exercises the
/// same refusal the app gives, and the restore loader (<c>RestoreState.LoadAxis</c>) reads a value
/// off disk by the same two spellings. The words are case-sensitive: they are the wire spelling,
/// and <c>"Horizontal"</c> or <c>"h"</c> is a caller guessing — a guess that was accepted here would
/// be one the tree read back in a spelling the caller never sent.
/// </summary>
public static class SplitAxes
{
    /// <summary>The ops <c>session.split</c> accepts on the wire (<c>close</c> is its own verb,
    /// <c>session.split.close</c>). Exact, lowercase: the CLI lowercases what it was typed, a raw
    /// client sends what it means. Anything else is refused rather than treated as toggle.</summary>
    public static bool IsOp(string op) => op is "on" or "off" or "toggle";

    public static string OpRefusal(string raw) =>
        $"session split: unknown op {raw} — on, off or toggle (close is `session split close`); an unknown op is not a toggle, because a toggle on a split session closes a pane. Nothing was split or closed.";

    /// <summary>Left/right panes. The default.</summary>
    public const string Vertical = "vertical";
    /// <summary>Top/bottom panes.</summary>
    public const string Horizontal = "horizontal";

    /// <summary>The arg key the verb reads the word from, and the key the tree and the restore file
    /// carry it under — one spelling, so a caller reads back what it set under the name it set it.</summary>
    public const string Key = "axis";

    /// <summary>
    /// Parse an axis word. <paramref name="raw"/> null means "not given": <paramref name="axis"/> is
    /// null too and the call succeeds — the host keeps the session's current orientation (agterm:
    /// omitting the flag preserves it). Anything else must be exactly <see cref="Vertical"/> or
    /// <see cref="Horizontal"/>; otherwise false, <paramref name="refusal"/> names the value and both
    /// words, and the caller must split nothing.
    /// </summary>
    public static bool TryParse(string? raw, out string? axis, out string? refusal)
    {
        axis = null; refusal = null;
        if (raw is null) return true;
        if (raw == Vertical || raw == Horizontal) { axis = raw; return true; }
        refusal = Refusal(raw);
        return false;
    }

    /// <summary>The refusal wording for a word that is neither axis. Public so the server's
    /// non-string case (an <c>axis</c> that is a number or an object) can use the same sentence.</summary>
    public static string Refusal(string raw)
        => $"axis '{raw}' is not one of {Vertical} (left/right panes) or {Horizontal} (top/bottom panes); nothing was split";

    /// <summary>What a pane is called on each axis, for the refusals below: the two panes of a
    /// vertical split are left/right, of a horizontal split top/bottom.</summary>
    public static string PaneNames(string axis) => axis == Horizontal ? "top/bottom" : "left/right";

    /// <summary>
    /// The words <c>session focus</c> takes (agterm's list): <c>primary</c> = pane 0 and <c>split</c> =
    /// pane 1 on either axis; <c>left</c>/<c>right</c> = pane 0/1 on a VERTICAL split only;
    /// <c>top</c>/<c>bottom</c> = pane 0/1 on a HORIZONTAL split only; <c>other</c> = whichever pane is
    /// not focused. A direction that does not exist on the session's axis (<c>top</c> on a vertical
    /// split) is refused naming the axis — a request that cannot mean anything must not quietly land
    /// somewhere — and so is a word outside the list. <paramref name="focused"/> is the focused index
    /// today; on success <paramref name="index"/> is the pane to focus.
    /// </summary>
    public static bool TryFocusIndex(string? dir, string axis, int focused, out int index, out string? refusal)
    {
        index = focused; refusal = null;
        bool horizontal = axis == Horizontal;
        switch (dir)
        {
            case "primary": index = 0; return true;
            case "split": index = 1; return true;
            case "other": index = focused == 0 ? 1 : 0; return true;
            case "left": case "right":
                if (horizontal) { refusal = $"'{dir}' names no pane on a {Horizontal} split (top/bottom panes); use top, bottom, primary, split or other"; return false; }
                index = dir == "left" ? 0 : 1; return true;
            case "top": case "bottom":
                if (!horizontal) { refusal = $"'{dir}' names no pane on a {Vertical} split (left/right panes); use left, right, primary, split or other"; return false; }
                index = dir == "top" ? 0 : 1; return true;
            default:
                refusal = $"focus '{dir}' is not one of primary, split, left, right, top, bottom or other";
                return false;
        }
    }

    /// <summary>The refusal for <c>session focus</c> on a session with one pane: there is nothing to
    /// focus, and an ok:true for it would be the silent-success class.</summary>
    public const string NotSplit = "session is not split (one pane); nothing to focus";

    /// <summary>
    /// <c>session resize</c>'s grow flags against the session's axis. <c>--grow-left</c> /
    /// <c>--grow-right</c> move a VERTICAL split's divider (in columns); <c>--grow-top</c> /
    /// <c>--grow-bottom</c> move a HORIZONTAL one (in rows). Flags from the other axis are refused
    /// naming the axis — a request that cannot mean anything must not silently move the divider. On
    /// success <paramref name="shift"/> is how many cells the FIRST pane (left/top) grows by along the
    /// axis (negative = shrinks): the divider moves right/down by <c>grow-right</c>/<c>grow-bottom</c>
    /// and left/up by <c>grow-left</c>/<c>grow-top</c>, the pre-P4 meaning of the two vertical flags.
    /// </summary>
    public static bool TryGrow(string axis, int growLeft, int growRight, int growTop, int growBottom, out int shift, out string? refusal)
    {
        shift = 0; refusal = null;
        bool horizontal = axis == Horizontal;
        bool anyLr = growLeft != 0 || growRight != 0, anyTb = growTop != 0 || growBottom != 0;
        if (horizontal && anyLr)
        {
            refusal = $"--grow-left/--grow-right mean nothing on a {Horizontal} split (top/bottom panes); use --grow-top/--grow-bottom; the divider was not moved";
            return false;
        }
        if (!horizontal && anyTb)
        {
            refusal = $"--grow-top/--grow-bottom mean nothing on a {Vertical} split (left/right panes); use --grow-left/--grow-right; the divider was not moved";
            return false;
        }
        shift = horizontal ? growBottom - growTop : growRight - growLeft;
        return true;
    }

    /// <summary>The refusal for <c>session resize</c> on a session with one pane: no divider to move.</summary>
    public const string NoDivider = "session is not split (one pane); there is no divider to move";
}
