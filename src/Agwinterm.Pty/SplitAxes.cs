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
}
