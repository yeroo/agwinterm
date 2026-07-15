namespace Agwinterm.Core;

/// <summary>
/// Paste-protection classifier (the pure, testable half — enforcement lives at the host seam,
/// mirroring the Ghostty/Windows Terminal model). Decides whether pasting a given text into a
/// terminal warrants a user confirmation before any bytes reach the shell.
/// </summary>
public static class PastePolicy
{
    /// <summary>
    /// True when pasting <paramref name="text"/> warrants a confirmation:
    /// it contains a newline — a shell executes each completed line immediately, so a multi-line
    /// paste is effectively "run this script now" — or a non-whitespace C0 control character / DEL
    /// (escape-sequence injection, e.g. a smuggled ESC that could fake bracketed-paste terminators).
    /// When the application has bracketed paste on (<paramref name="bracketedPaste"/>), newlines are
    /// delivered as one quoted unit and are safe; control characters still warrant a warning there,
    /// because an embedded ESC can terminate the bracket early and inject.
    /// </summary>
    public static bool NeedsWarning(string text, bool bracketedPaste)
    {
        foreach (char c in text)
        {
            if (c is '\r' or '\n') { if (!bracketedPaste) return true; }
            else if (c < 0x20 && c != '\t') return true;   // C0 controls (ESC, BEL, …); tab is ordinary text
            else if (c == '\x7f') return true;             // DEL
        }
        return false;
    }

    /// <summary>Number of lines the text pastes as (for the "Paste N lines?" prompt):
    /// line breaks (\r\n, \r or \n) + 1.</summary>
    public static int LineCount(string text)
    {
        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r') { lines++; if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (c == '\n') lines++;
        }
        return lines;
    }
}
