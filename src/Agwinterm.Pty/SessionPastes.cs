namespace Agwinterm.Pty;

/// <summary>
/// The replies of <c>session paste</c> and the rule that picks its payload. Kept here, not in the
/// Win32 assembly, so the decision has a test that needs no clipboard, the fake host answers with
/// the same words, and the control server's test pins the read-only refusal as ok:false.
/// <para>Round 8 of #256: the host answered <see cref="Pasted"/> whatever happened — the paste
/// routine returns silently for a read-only pane (a toast) and for empty text, and the clipboard
/// read answers "" for a clipboard with no CF_UNICODETEXT (empty, an image, a file list) and when
/// it cannot be opened or read — so a script was told its text reached a shell when nothing was
/// sent: the class the branch exists to close (a paste that silently went nowhere is the worst of
/// it; the text was meant for a shell).</para>
/// </summary>
public static class SessionPastes
{
    /// <summary>The payload reached the pane.</summary>
    public const string Pasted = "pasted";
    /// <summary>Nothing was sent: the text was empty and so was what the clipboard gave. NOT proof
    /// that the clipboard is empty — a clipboard that could not be opened or read, or holds no text
    /// (an image, files), gives the same reply; it says only that no text was obtained and none sent.</summary>
    public const string Nothing = "nothing to paste";
    /// <summary>The refusal (after <see cref="ISessionHost.RefusePrefix"/>) for a pane whose input
    /// is blocked because it is read-only — set by <c>session readonly on</c> / <c>toggle</c>, the
    /// Toggle Read-Only Pane menu item or the <c>toggle_read_only</c> binding, and lifted by
    /// <c>session readonly off</c> / the same toggles. The interactive paste shows a toast; a script
    /// needs ok:false, not <see cref="Pasted"/> for text that was dropped. Answered before the
    /// clipboard is read.</summary>
    public const string ReadOnlyPane = "pane is read-only";
    /// <summary>The refusal for a pane whose process has exited (a single-pane session keeps the
    /// exited surface on screen): its input may still take bytes, but no program reads them.
    /// Answered before the clipboard is read.</summary>
    public const string ExitedPane = "the pane's process has exited";
    /// <summary>The refusal for a write that threw (a broken pipe, a session that never started):
    /// what the exception said, after "paste failed: ". Whether any of the payload reached the
    /// pane is not known.</summary>
    public static string Failed(string why) => "paste failed: " + why;

    /// <summary>The text to paste: <paramref name="text"/> when the caller gave any (whitespace and
    /// newlines included — they are input, never trimmed), else what <paramref name="clipboard"/>
    /// gives; "" when neither has any. The clipboard is read only when it is needed.</summary>
    public static string Payload(string? text, Func<string> clipboard)
        => string.IsNullOrEmpty(text) ? clipboard() ?? "" : text;

    /// <summary>The reply for a payload that was handed to the pane — <see cref="Nothing"/> when
    /// there was none to hand.</summary>
    public static string Reply(string payload) => payload.Length == 0 ? Nothing : Pasted;
}
