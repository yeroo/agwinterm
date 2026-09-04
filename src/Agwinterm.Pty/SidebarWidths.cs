namespace Agwinterm.Pty;

/// <summary>
/// The sidebar width's default and its supported range, in device-independent pixels (DIP: the
/// unit the chrome lays itself out in; the Win32 host scales to device pixels per monitor). Kept
/// here, not in the Win32 assembly, so the control server can refuse an out-of-range
/// <c>sidebar width</c> before the host is reached and the fake host can exercise that refusal —
/// the same placement <c>TryOverlaySize</c> has for <c>--size-percent</c>.
///
/// The range is derived from what the chrome can actually draw, not from a round number:
///
/// <list type="bullet">
/// <item><b>Min 120.</b> A session row draws its name from x = 26 (40 for an elevated session) to
/// 22 DIP short of the right edge, where the status dot sits; a workspace row keeps 56 DIP on the
/// right for its count and "+" button; the footer puts two 34-DIP buttons at the left and one at the
/// right with 10 DIP of margin, 112 in all. Below about 112 the footer buttons overlap each other,
/// and at 120 a name still gets ~72 DIP, eight or nine characters of the sidebar font — the least
/// that still identifies a session. Narrower is a strip of dots and the tree cannot render a name
/// at all.</item>
/// <item><b>Max 600.</b> The content region has to stay usable beside the tree. A 1280-DIP window
/// (the narrowest common laptop at 100 %) keeps ~680 DIP of content at 600, about 80 columns of the
/// default cell — the width every shell and TUI assumes. Wider than that and an 80-column program no
/// longer fits at any common window size, so the width stops describing a sidebar and starts
/// describing a hidden terminal.</item>
/// </list>
///
/// A request outside the range is <b>refused</b>, not clamped (Technical Details, P2): a reported
/// clamp still answers <c>ok:true</c> to a script that checks nothing else.
/// </summary>
public static class SidebarWidths
{
    /// <summary>agterm's default, and the width the app has always used.</summary>
    public const int Default = 220;
    public const int Min = 120;
    public const int Max = 600;

    public static bool InRange(long width) => width >= Min && width <= Max;

    /// <summary>The refusal for an out-of-range or non-integer request: names the value and the
    /// range, and says how to read the current width — the caller who wrote <c>sidebar width 0</c>
    /// meaning "hide it" is told that <c>sidebar hide</c> is how to say that.</summary>
    public static string Refusal(string rawValue) =>
        $"sidebar width {rawValue} is not a whole number in {Min}..{Max} (device-independent pixels); " +
        "`sidebar width` with no value reads the current width, `sidebar hide` hides it. Nothing changed.";
}
