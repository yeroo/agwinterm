using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>
/// The rules for a session's <c>link</c> — the one URL a session is about (the tracker issue, the
/// pull request under review), set over <c>session link</c>, the sidebar's context menu or the File
/// menu, opened in the browser by <c>open_session_link</c> (Ctrl+Shift+L), the link glyph left of the
/// session's name, or <c>session link --open</c>. Carried in <c>tree --json</c> as <c>link</c> and
/// restored after a restart. Kept here, not in the Win32 assembly, for the reason
/// <see cref="SessionContexts"/> is: the server refuses a bad value before the host is reached, the
/// fake host exercises the refusal, and the restore loader applies the same rules to a value read
/// back from disk.
///
/// The value is ONE absolute <c>http</c> or <c>https</c> URL, tracker-agnostic (no key shorthand):
///
/// <list type="bullet">
/// <item><b>Only http and https.</b> The link is handed to the shell to open; a <c>file:</c>, a
/// <c>javascript:</c> or a bare path would launch whatever the shell associates with it.</item>
/// <item><b>No whitespace or control characters</b>, named with their offset. A URL has neither;
/// a pasted value with a newline in it is two things, not one link.</item>
/// <item><b>Blank is refused</b>; clearing is explicit (<c>--clear</c>), as for a context.</item>
/// <item><b>Over <see cref="MaxLength"/> is refused</b>, naming the ceiling.</item>
/// <item><b>Leading and trailing whitespace is trimmed</b> (<see cref="Normalize"/>).</item>
/// </list>
///
/// A refusal changes nothing: the old link stays and nothing is saved.
/// </summary>
public static class SessionLinks
{
    /// <summary>The ceiling, in UTF-16 code units: the practical URL length every browser accepts.</summary>
    public const int MaxLength = 2048;

    /// <summary>The JSON key the tree and the restore file carry the value under, and the arg key the
    /// verb reads it from.</summary>
    public const string Key = "link";

    /// <summary>Trim leading and trailing whitespace.</summary>
    public static string Normalize(string text) => text.Trim();

    /// <summary>The refusal text for a NORMALIZED <paramref name="link"/>, or null when it is acceptable.
    /// Checks, in order: blank; whitespace or a control character (with its offset); the length
    /// ceiling; an absolute http(s) URL with a host.</summary>
    public static string? Validate(string? link)
    {
        if (link is null || link.Length == 0 || string.IsNullOrWhiteSpace(link)) return Blank;
        if (CharRefusal(link) is { } bad) return bad;
        if (link.Length > MaxLength)
            return $"session link: {link.Length} characters is over the ceiling of {MaxLength}. Nothing changed.";
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            return NotHttp;
        return null;
    }

    /// <summary>True when <paramref name="link"/> is accepted by <see cref="TryNormalize"/>: the same
    /// answer the verb gives, so a trailing tab or newline is refused here too.</summary>
    public static bool IsValid(string? link) => link is not null && TryNormalize(link, out _, out _);

    // Control characters anywhere (a trailing tab or newline is refused, not trimmed away), whitespace
    // only between the first and the last non-space character: spaces around a pasted URL are trimmed.
    // Offsets index the string as given, so a caller can find the character in what they sent.
    private static string? CharRefusal(string text)
    {
        int first = 0, last = text.Length - 1;
        while (first <= last && text[first] == ' ') first++;
        while (last >= first && text[last] == ' ') last--;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x20 || (c >= 0x7F && c <= 0x9F))
                return $"session link: control character U+{(int)c:X4} at offset {i}; a link is one URL. Nothing changed.";
            if (i > first && i < last && char.IsWhiteSpace(c))
                return $"session link: whitespace U+{(int)c:X4} at offset {i}; a link is one URL (percent-encode a space as %20). Nothing changed.";
        }
        return null;
    }

    /// <summary>
    /// The character check on the text as given, then <see cref="Normalize"/>, then
    /// <see cref="Validate"/> on the result: <paramref name="text"/> is the normalized value exactly
    /// when <paramref name="refusal"/> is null. A null <paramref name="raw"/> is refused as blank.
    /// </summary>
    public static bool TryNormalize(string? raw, out string text, out string? refusal)
    {
        raw ??= "";
        text = Normalize(raw);
        refusal = CharRefusal(raw) ?? Validate(text);
        return refusal is null;
    }

    public const string Blank = "session link: the link is blank; a link is one http(s) URL, and `session link --clear` removes one. Nothing changed.";

    public const string NotHttp = "session link: not an absolute http:// or https:// URL; only web links are opened. Nothing changed.";

    public const string TextAndClear = "session link: a URL and --clear cannot be combined (one says what the link is, the other that there is none). Nothing changed.";

    /// <summary>The host's refusal when the target resolves to no session — the wording
    /// <see cref="SessionContexts.NoSession"/> uses for the same condition.</summary>
    public const string NoSession = SessionContexts.NoSession;

    /// <summary>The host's refusal for <c>--open</c> on a session without a link.</summary>
    public const string NoLink = "session has no link; set one with `session link <url>`";

    /// <summary>The success reply both hosts build: <c>{"session":"&lt;id&gt;","link":"&lt;url&gt;"}</c>,
    /// or <c>"link":null</c> after a clear, read back off the session after the write. <c>--open</c>
    /// answers with the same shape plus <c>"opened":true</c>.</summary>
    public static string Reply(string sessionId, string? link, bool opened = false) =>
        "{\"session\":" + JsonSerializer.Serialize(sessionId) + ",\"" + Key + "\":" +
        (link is null ? "null" : JsonSerializer.Serialize(link)) + (opened ? ",\"opened\":true" : "") + "}";
}
