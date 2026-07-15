namespace Agwinterm.Core;

/// <summary>
/// The host-action seam (libghostty's "apprt" boundary): everything the emulator needs the embedding
/// application to DO — as opposed to grid state it exposes — lands here as one explicit interface.
/// Adding an escape sequence with host-visible effects means adding a member here, which forces every
/// embedder (and test fake) to decide what to do with it, instead of the sequence being silently
/// dropped (how OSC 52 clipboard writes went missing for months).
///
/// All methods are called on the feed/pump thread, typically while the session lock is held —
/// implementations must be cheap and marshal real work to their own thread.
/// </summary>
public interface IHostActions
{
    /// <summary>Desktop notification requested via OSC 9 (body only) or OSC 777 (title + body).</summary>
    void Notify(string title, string body);

    /// <summary>Taskbar progress via OSC 9;4 (ConEmu/Windows Terminal convention):
    /// state 0 clear | 1 normal | 2 error | 3 indeterminate | 4 paused; value 0–100 for 1/2/4.</summary>
    void Progress(int state, int value);

    /// <summary>The program wrote the clipboard via OSC 52 (payload already base64-decoded).
    /// Write-only by design: the emulator never asks the host to read the clipboard back.</summary>
    void ClipboardWrite(string text);

    /// <summary>Terminal response bytes that must be written back to the PTY as if typed
    /// (e.g. the kitty keyboard-protocol flags query reply).</summary>
    void Respond(string reply);

    /// <summary>A syntactically valid escape sequence the emulator has no handler for. A debug tap:
    /// hosts typically log it under an env flag (or no-op) — never user-visible. This is what turns
    /// a protocol gap into one grep instead of a stack of symptom reports.</summary>
    void Unhandled(string kind, string detail);
}
