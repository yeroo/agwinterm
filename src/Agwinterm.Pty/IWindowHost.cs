namespace Agwinterm.Pty;

/// <summary>A window's metadata for the control-API window list.</summary>
public sealed record WindowSnapshot(string Id, string Name, bool Open, bool Active);

/// <summary>
/// App-level window manager for the control API (multi-window). Resolves a window selector
/// (id | unique id-prefix | "active" | null = frontmost) to that window's <see cref="ISessionHost"/>,
/// and performs window-management ops. Window-scoped content verbs dispatch to the resolved host;
/// the window-management verbs (window.*) act on the library itself. One named pipe serves the app.
/// </summary>
public interface IWindowHost
{
    /// <summary>The session host for the target window (default/"active"/null = frontmost); null if the selector matches no open window.</summary>
    ISessionHost? ResolveWindow(string? selector);

    /// <summary>All windows in the library (open + closed), with open/active flags.</summary>
    IReadOnlyList<WindowSnapshot> Windows();

    /// <summary>Create a new (seeded) window; returns its id.</summary>
    string WindowNew(string? name);

    // Every window verb below applies its change on the UI thread through a posted action. False
    // means the selector named no window (the server replies "window not found"); a change the app
    // could not queue — the window is closing, or its message queue refused the wake-up — is a THROW,
    // which the server turns into ok:false with that reason, never the ack (#228 item 5).
    /// <summary>Raise/focus a window (restoring it if minimized). False if not found.</summary>
    bool WindowSelect(string? selector);
    /// <summary>Close a window (its entry + per-window file are kept, marked not-open). False if not found.</summary>
    bool WindowClose(string? selector);
    /// <summary>Delete a window (removes its entry + per-window file). Never deletes the last window. False if not found / last.</summary>
    bool WindowDelete(string? selector);
    /// <summary>Rename a window (its custom name shows in the title). False if not found / blank name.</summary>
    bool WindowRename(string? selector, string name);

    bool WindowResize(string? selector, int w, int h);
    bool WindowMove(string? selector, int x, int y);
    /// <summary>Toggle maximize for a window.</summary>
    bool WindowZoom(string? selector);
}
