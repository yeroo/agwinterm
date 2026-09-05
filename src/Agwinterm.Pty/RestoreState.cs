using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agwinterm.Pty;

// ---- The per-window restore file: %LOCALAPPDATA%\<app-id>\windows\<window-id>.json ----
//
// FORMAT RULE (set by P3, inherited by every later change to this file — P4's split axis, P7):
//
//   * ADDITIVE KEYS ONLY, NO VERSION FIELD. These are plain POCOs round-tripped by System.Text.Json
//     with nothing but WriteIndented set. A key the reader does not know is ignored; a key the file
//     does not have takes the property's default. RestoreCommand, AgentResume, SidebarWidth and
//     (P3) Context were all added this way and none of them bumped anything — there is no version on
//     this file to bump (the window-library INDEX, windows.json, has one; the tree file does not).
//   * THE FAILURE MODE IS WRITE-BACK LOSS, NOT A CRASH. An older build (or a second window of a mixed
//     install) reading a newer file drops the keys it does not know on read and writes the file back
//     without them on its next save. JSON tolerance protects against the parse error; nothing
//     protects against the downgrade, and nothing here tries to — it is the exposure RestoreCommand
//     already has. A parse FAILURE (a truncated or hand-broken file) is the only "bad file" path:
//     the host renames it .bad and starts a default tree; an unknown key never reaches it.
//   * EVERY LOADED VALUE IS VALIDATED, NOT TRUSTED. SidebarWidth out of range → the default;
//     a Context that fails SessionContexts' rules → dropped (LoadContext), not displayed; an Axis that
//     is not one of SplitAxes' two words → vertical (LoadAxis).
//   * (P4) THE SPLIT AXIS: SessionState.Axis, the LAST key of a session, one of SplitAxes' words —
//     "horizontal" = top/bottom panes. WRITTEN ONLY WHEN THE SESSION IS SPLIT AND HORIZONTAL (StoreAxis):
//     a vertical or single-pane session writes NO key, so every file this build saves for a tree
//     without a horizontal split is byte-identical to what 0.17.12 saved. Absent, or any spelling but
//     the two wire words (case-sensitive), reads as vertical — what every file written before the key
//     meant. TWO PANES PER SESSION: the model is primary + split and the axis is per session; a file
//     with more panes restores the first two and the host logs the rest as dropped (the restore loop
//     being written for N was an accident, never a feature).
//   * THE FORMAT HAS A TEST THAT CAN SEE IT (tests/Agwinterm.Pty.Tests/RestoreStateTests.cs) — that is
//     why the POCOs live here rather than in the Win32 assembly: every format change is verified by a
//     round-trip beside it, the way BufferPersist has one, rather than by grepping a JSON file from
//     PowerShell.
//
// Property ORDER is the file's key order (System.Text.Json writes in declaration order). Keep it:
// the move out of Program.Services.cs was verified byte-for-byte against files the previous build
// wrote, and a reorder would turn every ordinary save into a spurious whole-file rewrite.

/// <summary>One pane of a session. <c>Command</c> is the captured foreground command (restore-commands;
/// <c>""</c> = nothing captured), <c>RestoreCommand</c> the explicit pin (independent of the toggle),
/// <c>AgentResume</c> the bound resumable agent. Cwd/FontSize are per pane since splits.</summary>
public sealed class PaneState { public string Id { get; set; } = ""; public string Cwd { get; set; } = ""; public float FontSize { get; set; } public float Ratio { get; set; } = 1f; public string Command { get; set; } = ""; public string? AgentResume { get; set; } public string? RestoreCommand { get; set; } public List<string>? Buffer { get; set; } public string? BufferBlob { get; set; } }

// Cwd/FontSize kept for backward-compat with pre-splits state.json (one pane per session).
public sealed class SessionState
{
    public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string? CustomName { get; set; }
    /// <summary>
    /// P3: the session's one-line context (<c>session context</c>), null = none. Written only when
    /// set — a null context serializes as NO key, which is exactly what a pre-P3 build writes for
    /// this field, so a tree without contexts still saves byte-for-byte what 0.17.11 saved and an
    /// old-shape file round-trips unchanged. Read back through <see cref="RestoreState.LoadContext"/>,
    /// never straight into the session: the rules are <see cref="SessionContexts"/>'.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; set; }
    public string? Profile { get; set; }
    public int Active { get; set; }
    public bool Flagged { get; set; }
    public List<PaneState> Panes { get; set; } = new(); public string Cwd { get; set; } = ""; public float FontSize { get; set; }
    // Wave F2: background watermark (BgFile = the copied file's name under backgrounds\; null = none).
    public string? BgFile { get; set; }
    public int BgOpacity { get; set; } = 15; public string BgMode { get; set; } = "fit";
    /// <summary>
    /// P4: the split's orientation — <see cref="SplitAxes.Horizontal"/> for top/bottom panes; null for
    /// left/right (the default) and for a single pane. Written only when the session is split AND
    /// horizontal (<c>WhenWritingNull</c>; <see cref="RestoreState.StoreAxis"/> is the one rule), so a
    /// vertical or single-pane session writes NO key and saves exactly the bytes a pre-P4 build saves —
    /// <c>PreP3File_RoundTripsByteForByte</c> stays green untouched. At the END of the class: property
    /// order is the file's key order. Read back through <see cref="RestoreState.LoadAxis"/>, never
    /// straight into the session: an unknown spelling is the default, not a layout.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Axis { get; set; }
}
public sealed class WorkspaceState { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public bool Expanded { get; set; } = true; public List<SessionState> Sessions { get; set; } = new(); }
public sealed class AppState
{
    public List<WorkspaceState> Workspaces { get; set; } = new();
    public string? ActiveId { get; set; }
    public float SidebarWidth { get; set; } = SidebarWidths.Default;
    public bool SidebarVisible { get; set; } = true;
    // Window geometry (restore rect; 0 width = unset). WindowMaximized reopens maximized.
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    // Wave D1: sidebar view mode ("tree"|"flagged") + focused workspace id (null = show all).
    public string SidebarMode { get; set; } = "tree";
    public string? FocusedWorkspaceId { get; set; }
    // Ctrl+Tab MRU session order (most recent first); restored on relaunch.
    public List<string> Mru { get; set; } = new();
}

/// <summary>The serializer for the restore file (and the one the host shares with the window-library
/// index): the POCOs above, indented, nothing else configured. See the format rule at the top of this file.</summary>
public static class RestoreState
{
    /// <summary>The one options object — <c>WriteIndented</c> and nothing else. Reads use it too (indentation
    /// is irrelevant on read; property matching stays case-sensitive, the default).</summary>
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The file's text for <paramref name="state"/>. UTF-8 without BOM when the host writes it.</summary>
    public static string Serialize(AppState state) => JsonSerializer.Serialize(state, Json);

    /// <summary>
    /// Parse a restore file. False (and null) when the JSON does not parse — the host's cue to rename
    /// the file <c>.bad</c> — and true for anything that parses, including an empty object, a file
    /// with keys this build does not know, and a file missing keys this build has (they take their
    /// defaults). Callers still validate what they read: see <see cref="LoadContext"/>.
    /// </summary>
    public static bool TryDeserialize(string json, out AppState? state)
    {
        try { state = JsonSerializer.Deserialize<AppState>(json, Json); return true; }
        catch { state = null; return false; }
    }

    /// <summary>
    /// The context to put on a restored session for a stored <see cref="SessionState.Context"/>: the
    /// normalized value when it passes <see cref="SessionContexts"/>' rules, else null — a value that
    /// fails them (a newline, a control byte, over the ceiling, blank) is DROPPED on load, not shown,
    /// the way an out-of-range SidebarWidth falls back to the default. A hand-edited file cannot put
    /// on the title bar what the verb would have refused.
    /// </summary>
    public static string? LoadContext(string? stored) =>
        stored is not null && SessionContexts.TryNormalize(stored, out var text, out _) ? text : null;

    /// <summary>
    /// The axis to put on a restored SPLIT session for a stored <see cref="SessionState.Axis"/>: the word
    /// itself when it is exactly one of <see cref="SplitAxes"/>' two (the wire spelling, case-sensitive —
    /// what <see cref="SplitAxes.TryParse"/> accepts from the verb), else <see cref="SplitAxes.Vertical"/>.
    /// Absent (a pre-P4 file, or a vertical split, which writes no key) is vertical: that is what every
    /// file written before the key meant. A hand-edited <c>"Horizontal"</c>, <c>"h"</c>, <c>""</c> or
    /// <c>"diagonal"</c> is DROPPED to the default the way an out-of-rules Context is — the file cannot
    /// lay out what the verb would have refused.
    /// </summary>
    public static string LoadAxis(string? stored) =>
        SplitAxes.TryParse(stored, out var axis, out _) && axis is not null ? axis : SplitAxes.Vertical;

    /// <summary>
    /// What a save puts in <see cref="SessionState.Axis"/> for a session with <paramref name="paneCount"/>
    /// panes laid out along <paramref name="axis"/>: <see cref="SplitAxes.Horizontal"/> only when the
    /// session is split AND horizontal, else null — no key. A single-pane session keeps its axis in
    /// memory for its next <c>split on</c> without <c>--axis</c>, but the FILE does not carry it: the
    /// key means "these two panes are stacked", and writing it for one pane would make every file with
    /// a once-horizontal session differ from what a pre-P4 build writes for the same tree.
    /// </summary>
    public static string? StoreAxis(int paneCount, string axis) =>
        paneCount > 1 && axis == SplitAxes.Horizontal ? SplitAxes.Horizontal : null;
}
