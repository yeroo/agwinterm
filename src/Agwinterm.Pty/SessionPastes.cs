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
    /// <summary>The payload was handed to the pane's input without a synchronous error. Not proof
    /// that a program read it: the pipe takes bytes without an acknowledgement, and a child's exit
    /// is observed asynchronously (<see cref="ISession.HasExited"/>; how late is per backend — see
    /// <see cref="ExitedPane"/>), so a write before the observation may be accepted for text no
    /// program reads. The refusals below cover what the host KNOWS at the time of the call.</summary>
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
    /// <summary>Known input closure is refused before clipboard access, even while output still
    /// drains or a disconnected host's child-exit status remains unknown. This is not an exit claim.</summary>
    public const string InputUnavailable = "the pane's input is closed";
    /// <summary>The refusal for a pane whose process has exited (a single-pane session keeps the
    /// exited surface on screen): its input may still take bytes, but no program reads them.
    /// Answered before the clipboard is read, once the host has OBSERVED the exit: the guard is
    /// <see cref="ISession.HasExited"/>, which lags the exit by an amount that depends on the
    /// backend (<see cref="ISession.Exited"/> describes both). In-process,
    /// <see cref="TerminalSession"/> sets it after its settle window (quiet 50 ms windows, at most
    /// ten) on the paths that settle, and at once on a start that failed; on <c>server</c> /
    /// <c>server-rust</c>, for a child that exited, the host settles on its own session, then the
    /// client sees the data pipe close and completes a <c>list</c> round trip before
    /// <see cref="ServerSession.HasExited"/> is set — no figure here bounds that; a start that
    /// failed sets it from the caught failure, and a host that died is inferred after the pipe's
    /// EOF and a <c>list</c> that failed. A call before the observation is answered by the other
    /// rules (a write may be accepted, or refused for another reason), and nothing promises that a
    /// retry gets this refusal. The enduring state (an exited pane on screen) is refused always;
    /// only the transition is not, and nothing here proves child consumption.</summary>
    public const string ExitedPane = "the pane's process has exited";
    /// <summary>The refusal for a write that threw (a broken pipe, a session that never started):
    /// what the exception said, after "paste failed: ". Unlike <see cref="ReadOnlyPane"/> and
    /// <see cref="ExitedPane"/> it comes AFTER the payload was picked — the clipboard may have been
    /// read — and whether any of the payload reached the pane is not known (a stream write can fail
    /// part-way), so a caller must not retry blindly.</summary>
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
