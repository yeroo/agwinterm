using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>
/// The rules for a session's <c>context</c> — the free text an agent sets on a session over
/// <c>session context</c> to say what the pane is FOR, shown dimmed beside the name in the title
/// bar and the sidebar row, carried in <c>tree --json</c> as <c>context</c>, and restored after a
/// restart (P3). Kept here, not in the Win32 assembly, so the control server can refuse a bad value
/// before the host is reached, the fake host can exercise the refusal, and the restore loader can
/// apply the same rules to a value read back from disk — one class, one wording, the placement
/// <see cref="SidebarWidths"/> has for <c>sidebar width</c>.
///
/// The value is ONE LINE of printable text:
///
/// <list type="bullet">
/// <item><b>Control characters are refused</b> (anything below U+0020, and U+007F..U+009F), naming
/// the character and its offset the way <see cref="StdinText"/> names a bad byte. A newline in the
/// title bar is not a context, it is a rendering accident (#213's class); a tab or an escape
/// sequence likewise. Both surfaces are single rows of fixed height, so a multi-line value would be
/// silently truncated everywhere it is shown — the P2 defect class. Refusing keeps "what you set is
/// what is shown" true.</item>
/// <item><b>Blank is refused.</b> <c>session rename ""</c> is already refused as blank; a context
/// verb that treated blank as "clear" would be the one verb in the family where an empty argument
/// is a command. Clearing is explicit (<c>--clear</c>), and text beside <c>--clear</c> is refused
/// as two sources for one field (P2's <c>--stdin</c> rule).</item>
/// <item><b>Over <see cref="MaxLength"/> is refused</b>, naming the ceiling.</item>
/// <item><b>Leading and trailing whitespace is trimmed</b> (<see cref="Normalize"/>) — a shell that
/// pads its arguments should not produce a context that draws as a shifted suffix.</item>
/// </list>
///
/// A refusal changes nothing: the old context stays and nothing is saved.
/// </summary>
public static class SessionContexts
{
    /// <summary>
    /// The ceiling, in UTF-16 code units (<see cref="string.Length"/>). This is a DISPLAY budget,
    /// not a storage limit: the title bar draws the context as a dimmed suffix inside the title's
    /// own width budget (a second caption line is not viable — the title bar is 40/30/0 DIP tall by
    /// toolbar mode), and the sidebar row clips it to the name rect. The session palette is the
    /// only surface with room for a long form. Do not raise this without widening those surfaces.
    /// </summary>
    public const int MaxLength = 200;

    /// <summary>The JSON key the tree and the restore file carry the value under, and the arg key the
    /// verb reads it from. One spelling, so a caller reads back what it set under the name it set it.</summary>
    public const string Key = "context";

    /// <summary>Trim leading and trailing whitespace. Applied by the server before <see cref="Validate"/>
    /// and by the restore loader before it validates a value read from disk.</summary>
    public static string Normalize(string text) => text.Trim();

    /// <summary>
    /// The refusal text for <paramref name="context"/>, or null when it is acceptable. Checks, in
    /// order: null / blank; a control character (named with its offset in the string as given, so a
    /// caller can find it); the length ceiling. Call on the NORMALIZED text — a value with only
    /// whitespace around it is fine, a value that is only whitespace is blank.
    /// </summary>
    public static string? Validate(string? context)
    {
        if (context is null || context.Length == 0 || string.IsNullOrWhiteSpace(context)) return Blank;
        if (ControlRefusal(context) is { } control) return control;
        if (context.Length > MaxLength)
            return $"session context: {context.Length} characters is over the ceiling of {MaxLength}; the ceiling is a display budget (the title bar and the sidebar row draw the context beside the name). Nothing changed.";
        return null;
    }

    /// <summary>The control-character refusal for <paramref name="text"/>, or null. The offset indexes
    /// <paramref name="text"/> as given — <see cref="TryNormalize"/> runs this on the caller's string
    /// BEFORE trimming, so the offset in the refusal is one the caller can find in what they sent, the
    /// way <see cref="StdinText"/> numbers a bad byte in the bytes piped rather than in the bytes minus
    /// the BOM. (Trim would also silently eat a trailing NEL or tab, which are whitespace to .NET and
    /// control characters to a title bar; checking first refuses them instead.)</summary>
    private static string? ControlRefusal(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x20 || (c >= 0x7F && c <= 0x9F))
                return $"session context: control character U+{(int)c:X4} at offset {i}; a context is one line of printable text (no newline, tab or escape). Nothing changed.";
        }
        return null;
    }

    /// <summary>
    /// The control check on the text as given, then <see cref="Normalize"/>, then
    /// <see cref="Validate"/> on the result: <paramref name="text"/> is the normalized value exactly
    /// when <paramref name="refusal"/> is null. A null <paramref name="raw"/> (no text given at all)
    /// is refused as blank — with the hint that <c>--clear</c> is how to remove a context.
    /// </summary>
    public static bool TryNormalize(string? raw, out string text, out string? refusal)
    {
        raw ??= "";
        text = Normalize(raw);
        refusal = ControlRefusal(raw) ?? Validate(text);
        return refusal is null;
    }

    /// <summary>The blank refusal, with the way to clear named — a caller who sent "" meaning
    /// "remove it" is told the flag that says that.</summary>
    public const string Blank = "session context: the text is blank; a context is one line of printable text, and `session context --clear` removes one. Nothing changed.";

    /// <summary>Text and <c>--clear</c> together: two sources for one field, refused rather than
    /// ranked (the rule <c>session type --stdin</c> set in P2).</summary>
    public const string TextAndClear = "session context: text and --clear cannot be combined (one says what the context is, the other that there is none). Nothing changed.";

    /// <summary>The host's refusal when the target resolves to no session — the same "session not
    /// found" <c>session.rename</c> answers, so a script sees one wording for one condition. Both
    /// hosts return it (behind <see cref="ISessionHost.RefusePrefix"/>) so a test against the fake
    /// asserts the app's text.</summary>
    public const string NoSession = "session not found; nothing changed";

    /// <summary>
    /// The success reply, built by both hosts so the shape cannot drift between them:
    /// <c>{"session":"&lt;id&gt;","context":"&lt;text&gt;"}</c>, or <c>"context":null</c> after a
    /// clear. It names the session the value landed on (a target may have been a prefix, a name or
    /// a cover pane id) and the value IN EFFECT after the write — the host reads it back off the
    /// session inside the UI-thread hop rather than echoing the request.
    /// </summary>
    public static string Reply(string sessionId, string? context) =>
        "{\"session\":" + JsonSerializer.Serialize(sessionId) + ",\"" + Key + "\":" +
        (context is null ? "null" : JsonSerializer.Serialize(context)) + "}";
}
