namespace Agwinterm.Core;

/// <summary>A flyout a menu row opens instead of running something.</summary>
public enum MenuFlyout { None, OpenWindow, OpenRecent }

/// <summary>
/// One row of a menu, as data. <paramref name="Id"/> is what the host maps to behaviour ("-" is a
/// separator); <paramref name="Label"/> is agterm's wording; <paramref name="Action"/> names the
/// keymap action whose EFFECTIVE chord the row shows, so a rebind in keymap.conf shows up here (null
/// = keyless, as in agterm); <paramref name="AltLabel"/> is the wording while the host reports the
/// row's state as on (Hide Sidebar / Show Sidebar); <paramref name="Term"/> is the enablement term
/// the host evaluates (null = always enabled) — see <see cref="MenuModel"/> for the vocabulary.
/// </summary>
public sealed record MenuItemDef(string Id, string Label, string? Action = null, string? AltLabel = null,
    string? Term = null, MenuFlyout Flyout = MenuFlyout.None)
{
    public bool IsSeparator => Id == "-";
    public static readonly MenuItemDef Separator = new("-", "-");
}

/// <summary>A top-level menu: its title, the letter that opens it (alone from the focused bar,
/// with Alt anywhere but the terminal), and its rows.</summary>
public sealed record MenuDef(string Title, char Mnemonic, IReadOnlyList<MenuItemDef> Items);

/// <summary>
/// The menu bar's four menus — agterm's File / View / Navigate / Help, in agterm's order and
/// wording (<c>agterm/agtermApp+Menus.swift</c>), with the Mac word swapped for the Windows word
/// (Finder → Explorer, ghostty.conf → agwinterm.conf) and agterm's items that have no agwinterm
/// counterpart left out rather than invented: Edit/Reload Hooks (agwinterm's hooks are installed
/// scripts, not a hooks file), Toggle Terminal Zoom (no zoom), Reset Live Sessions (zmx), and the
/// three focus-SET items (Add Workspace to Focus, Toggle Workspace Filter, Clear Focus — agwinterm's
/// workspace focus is one workspace, toggled by Focus Workspace). Host-free so the list is testable:
/// the Win32 layer maps ids to behaviour, terms to state, and actions to chords.
///
/// <para>Enablement terms, evaluated by the host when a menu opens: <c>session</c> (an active
/// session), <c>workspace</c> (a current workspace), <c>workspaces</c> (more than one workspace),
/// <c>windows</c> (more than one window in the library), <c>openwindows</c> (more than one window
/// OPEN — stepping goes between open windows), <c>closed</c> (something recently closed),
/// <c>tree</c> (the sidebar in tree mode), <c>flags</c> (flagged mode, or a flagged session to
/// show), <c>anyflag</c> (a flagged session), <c>split</c> (the active session has two panes),
/// <c>status</c> (the active session's agent status is not idle), <c>sessions</c> (at least two
/// sessions), <c>attention</c> (a session with a non-idle status).</para>
/// </summary>
public static class MenuModel
{
    public static IReadOnlyList<MenuDef> Menus { get; } = Array.AsReadOnly(new[]
    {
        new MenuDef("File", 'F', Array.AsReadOnly(new[]
        {
            new MenuItemDef("new_window", "New Window", "new_window"),
            new MenuItemDef("open_window", "Open Window", Flyout: MenuFlyout.OpenWindow),
            new MenuItemDef("rename_window", "Rename Window…"),
            new MenuItemDef("delete_window", "Delete Window", Term: "windows"),
            MenuItemDef.Separator,
            new MenuItemDef("new_workspace", "New Workspace", "new_workspace"),
            new MenuItemDef("rename_workspace", "Rename Workspace", Term: "workspace"),
            new MenuItemDef("delete_workspace", "Delete Workspace", "delete_workspace", Term: "workspaces"),
            MenuItemDef.Separator,
            new MenuItemDef("new_session", "New Session", "new_session"),
            new MenuItemDef("open_directory", "Open Directory…"),
            new MenuItemDef("open_recent", "Open Recent", Term: "closed", Flyout: MenuFlyout.OpenRecent),
            new MenuItemDef("reopen_recent", "Reopen Last Closed Item", "reopen_session", Term: "closed"),
            new MenuItemDef("rename_session", "Rename Session", "rename_session", Term: "session"),
            new MenuItemDef("duplicate_session", "Duplicate Session", "duplicate_session", Term: "session"),
            new MenuItemDef("reveal", "Reveal in Explorer", Term: "session"),
            new MenuItemDef("close_session", "Close Session", "close_pane", Term: "session"),
            new MenuItemDef("reopen_closed", "Reopen Closed Item", Term: "closed"),
            new MenuItemDef("clear_status", "Clear Status", Term: "status"),
            MenuItemDef.Separator,
            new MenuItemDef("edit_keymap", "Edit Keymap…"),
            new MenuItemDef("reload_keymap", "Reload Keymap", "reload_keymap"),
            new MenuItemDef("edit_config", "Edit agwinterm.conf…"),
            new MenuItemDef("reload_config", "Reload Config"),
        })),
        new MenuDef("View", 'V', Array.AsReadOnly(new[]
        {
            new MenuItemDef("increase_font", "Increase Font Size", "increase_font_size"),
            new MenuItemDef("decrease_font", "Decrease Font Size", "decrease_font_size"),
            new MenuItemDef("reset_font", "Actual Size", "reset_font_size"),
            new MenuItemDef("select_theme", "Select Theme…"),
            MenuItemDef.Separator,
            new MenuItemDef("toggle_sidebar", "Hide Sidebar", "toggle_sidebar", AltLabel: "Show Sidebar"),
            new MenuItemDef("expand_workspaces", "Expand Workspaces", Term: "tree"),
            new MenuItemDef("collapse_workspaces", "Collapse Workspaces", Term: "tree"),
            new MenuItemDef("toggle_workspace_collapse", "Collapse Workspace", "toggle_workspace_collapse", AltLabel: "Expand Workspace", Term: "workspace"),
            new MenuItemDef("toggle_flagged_view", "Show Flagged Sessions", "toggle_flagged_view", AltLabel: "Show All Sessions", Term: "flags"),
            new MenuItemDef("toggle_flag", "Flag Session", "toggle_flag", AltLabel: "Unflag Session", Term: "session"),
            new MenuItemDef("clear_flagged", "Clear Flagged", Term: "anyflag"),
            new MenuItemDef("focus_workspace", "Focus Workspace", "focus_workspace", AltLabel: "Unfocus Workspace", Term: "tree"),
            new MenuItemDef("toggle_split_v", "Toggle Vertical Split", "split_pane", Term: "session"),
            new MenuItemDef("toggle_split_h", "Toggle Horizontal Split", Term: "session"),
            new MenuItemDef("swap_panes", "Swap Panes", Term: "split"),
            new MenuItemDef("toggle_scratch", "Show Scratch", "toggle_scratch", AltLabel: "Hide Scratch", Term: "session"),
            new MenuItemDef("find", "Find…", "toggle_search", Term: "session"),
            new MenuItemDef("quick_terminal", "Quick Terminal", "quick_terminal"),
            MenuItemDef.Separator,
            new MenuItemDef("toggle_fullscreen", "Toggle Fullscreen", "toggle_fullscreen"),
        })),
        new MenuDef("Navigate", 'N', Array.AsReadOnly(new[]
        {
            new MenuItemDef("session_palette", "Go to Session", "session_palette"),
            new MenuItemDef("action_palette", "Command Palette", "action_palette"),
            new MenuItemDef("custom_palette", "Custom Commands", "custom_palette"),
            new MenuItemDef("attention_list", "Go to Attention…", "attention_list", Term: "attention"),
            new MenuItemDef("dashboard", "Dashboard", "dashboard"),
            MenuItemDef.Separator,
            new MenuItemDef("previous_session", "Previous Session", "previous_session", Term: "sessions"),
            new MenuItemDef("next_session", "Next Session", "next_session", Term: "sessions"),
            new MenuItemDef("previous_attention", "Previous Attention Session", "previous_attention", Term: "attention"),
            new MenuItemDef("next_attention", "Next Attention Session", "next_attention", Term: "attention"),
            new MenuItemDef("first_session", "First Session", Term: "sessions"),
            new MenuItemDef("last_session", "Last Session", Term: "sessions"),
            new MenuItemDef("previous_workspace", "Previous Workspace", "previous_workspace", Term: "workspaces"),
            new MenuItemDef("next_workspace", "Next Workspace", "next_workspace", Term: "workspaces"),
            new MenuItemDef("previous_window", "Previous Window", Term: "openwindows"),
            new MenuItemDef("next_window", "Next Window", Term: "openwindows"),
            MenuItemDef.Separator,
            new MenuItemDef("focus_left", "Focus Left Pane", "focus_left_pane", AltLabel: "Focus Top Pane", Term: "split"),
            new MenuItemDef("focus_right", "Focus Right Pane", "focus_right_pane", AltLabel: "Focus Bottom Pane", Term: "split"),
        })),
        new MenuDef("Help", 'H', Array.AsReadOnly(new[]
        {
            new MenuItemDef("docs", "Developer Documentation…"),
            MenuItemDef.Separator,
            new MenuItemDef("install_cli", "Install Command Line Tool…"),
            new MenuItemDef("install_hooks", "Install Agent Status Hooks…"),
            new MenuItemDef("install_skill", "Install Agent Skill…"),
            new MenuItemDef("install_shell", "Install Shell Integration…"),
            MenuItemDef.Separator,
            new MenuItemDef("check_updates", "Check for Updates…"),
            new MenuItemDef("about", "About agwinterm"),
        })),
    });

    /// <summary>The menu Alt+<paramref name="letter"/> opens (case-insensitive), or -1.</summary>
    public static int MenuForMnemonic(char letter)
    {
        char c = char.ToUpperInvariant(letter);
        for (int i = 0; i < Menus.Count; i++) if (Menus[i].Mnemonic == c) return i;
        return -1;
    }
}
