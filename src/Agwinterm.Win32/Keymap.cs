using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>
/// Parses %LOCALAPPDATA%\agwinterm\keymap.conf into chord→action bindings and custom
/// commands. Our own simple format (inspired by agterm, not copied):
///   map &lt;chord&gt; = &lt;action&gt;          rebind a built-in action
///   map &lt;chord&gt; = command:&lt;Label&gt;  bind a chord to a custom command
///   command &lt;Label&gt; = &lt;text&gt;       run &lt;text&gt; (default: type it into the active session)
///   command [new|overlay|detached|send] &lt;Label&gt; = &lt;text&gt;   choose the run mode
///   leader = &lt;chord&gt;                 set the leader/prefix chord (tmux-style)
///   map leader &lt;chord&gt; = &lt;action|command:Label&gt;   bind a leader sequence
///   '#' starts a comment; blank lines ignored.
/// A canonical chord is "[ctrl+][alt+][shift+]&lt;key&gt;" where key ∈ a–z, 0–9, f1–f12,
/// tab, enter, escape, space, up, down, left, right.
/// The command &lt;text&gt; may contain {AGW_*} tokens (expanded from the active session) and the
/// launched process receives $AGW_* environment variables — see the agent skill for the list.
/// </summary>
internal static class Keymap
{
    /// <summary>Built-in action ids and their default chords (overridable by keymap.conf).</summary>
    public static readonly (string Chord, string Action)[] DefaultBindings =
    {
        ("ctrl+shift+t", "new_session"),
        ("ctrl+shift+n", "new_workspace"),
        ("ctrl+shift+w", "close_pane"),
        ("ctrl+d", "split_pane"),
        ("ctrl+alt+left", "focus_left_pane"),
        ("ctrl+alt+right", "focus_right_pane"),
        ("ctrl+tab", "next_session"),
        ("ctrl+shift+tab", "previous_session"),
        ("ctrl+p", "session_palette"),
        ("ctrl+shift+p", "action_palette"),
        ("ctrl+shift+i", "attention_list"),
        ("ctrl+shift+o", "custom_palette"),
        ("ctrl+alt+up", "previous_attention"),
        ("ctrl+alt+down", "next_attention"),
        ("f2", "rename_session"),
        ("ctrl+f", "toggle_search"),
        ("ctrl+j", "toggle_scratch"),
        ("ctrl+shift+f", "toggle_flag"),
        ("ctrl+shift+a", "select_all"),
        ("ctrl+alt+n", "new_window"),
        ("f11", "toggle_fullscreen"),
        ("ctrl+shift+up", "previous_prompt"),
        ("ctrl+shift+down", "next_prompt"),
    };

    public static readonly HashSet<string> ValidActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "new_session", "new_workspace", "close_session", "close_pane", "split_pane",
        "focus_left_pane", "focus_right_pane", "next_session", "previous_session",
        "toggle_sidebar", "rename_session", "delete_workspace", "session_palette", "action_palette",
        "attention_list", "custom_palette", "next_attention", "previous_attention", "reload_keymap",
        "toggle_search", "toggle_scratch", "quick_terminal", "close_cover", "toggle_fullscreen", "toggle_broadcast", "toggle_read_only",
        "previous_prompt", "next_prompt",
        "toggle_flag", "toggle_flagged_view", "focus_workspace",
        "select_all", "copy_selection", "paste",
        "new_window", "close_window", "switch_window",
    };

    public const string StarterText =
        """
        # agwinterm keymap (our own simple format)
        #
        #   map <chord> = <action>          rebind a built-in action
        #   map <chord> = command:<Label>   bind a chord to a custom command below
        #   command <Label> = <text>        run <text> (default: type it into the active session)
        #   command [new|overlay|detached] <Label> = <text>   choose the run mode
        #   leader = <chord>                set a leader/prefix chord (tmux-style)
        #   map leader <chord> = <action|command:Label>        bind a leader sequence
        #
        # chords: ctrl+ alt+ shift+ then a key — a-z, 0-9, f1-f12,
        #         tab enter escape space up down left right,
        #         comma period slash semicolon quote backtick minus equals
        #         lbracket rbracket backslash (US layout).  e.g. shift+comma, ctrl+shift+g
        #
        # run modes: send (default; types into the active session) | new (fresh session's
        #            shell runs it) | overlay (ephemeral pane over the session) | detached
        #            (independent OS process — open a URL, launch an external tool)
        #
        # tokens (expanded in <text>) + matching $AGW_* env vars given to the process:
        #   {AGW_SESSION} {AGW_SESSION_ID} {AGW_WORKSPACE} {AGW_CWD} {AGW_PANE_ID} {AGW_APP}
        #
        # actions: new_session new_workspace close_session next_session previous_session
        #          toggle_sidebar rename_session delete_workspace session_palette
        #          action_palette attention_list custom_palette next_attention
        #          previous_attention reload_keymap toggle_flag focus_workspace
        #          select_all copy_selection paste close_cover
        #
        # Examples (uncomment to use):
        # map escape = close_cover        # Esc hides the quick/scratch/overlay cover (falls through otherwise)
        # map ctrl+shift+g = command:Greet
        # command Greet = echo hello from {AGW_SESSION}
        # command [new] Log = echo running in {AGW_CWD}
        # command [detached] Docs = start https://example.com
        # leader = ctrl+k
        # map leader g = command:Greet
        """;

    /// <summary>A custom command: a label, the text to run, and how to run it.</summary>
    /// <param name="Mode">send | new | overlay | detached.</param>
    public sealed record CmdDef(string Label, string Text, string Mode);

    public static readonly HashSet<string> ValidModes = new(StringComparer.OrdinalIgnoreCase)
    { "send", "new", "overlay", "detached" };

    public sealed class Parsed
    {
        public readonly Dictionary<string, string> Bindings = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<CmdDef> Commands = new();
        public readonly List<string> Diagnostics = new();
        /// <summary>The leader/prefix chord (canonical), or null if none configured.</summary>
        public string? Leader;
        /// <summary>Second chord (canonical) → action id / "command:&lt;Label&gt;", pressed after the leader.</summary>
        public readonly Dictionary<string, string> LeaderBindings = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Parse keymap text; starts from the defaults, then applies map/command/leader lines.</summary>
    public static Parsed Parse(string text)
    {
        var p = new Parsed();
        foreach (var (c, a) in DefaultBindings) p.Bindings[c] = a; // defaults, overridable

        int lineNo = 0;
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("leader ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("leader=", StringComparison.OrdinalIgnoreCase))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) { p.Diagnostics.Add($"line {lineNo}: 'leader' needs '='"); continue; }
                string chordRaw = line[(eq + 1)..].Trim();
                string? chord = Canonicalize(chordRaw);
                if (chord is null) { p.Diagnostics.Add($"line {lineNo}: bad leader chord '{chordRaw}'"); continue; }
                p.Leader = chord;
            }
            else if (line.StartsWith("map ", StringComparison.OrdinalIgnoreCase))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) { p.Diagnostics.Add($"line {lineNo}: 'map' needs '='"); continue; }
                string chordRaw = line.Substring(3, eq - 3).Trim();
                string target = line[(eq + 1)..].Trim();

                // "map leader <chord> = ..." binds a leader sequence into LeaderBindings.
                bool isLeader = chordRaw.StartsWith("leader ", StringComparison.OrdinalIgnoreCase);
                if (isLeader) chordRaw = chordRaw["leader ".Length..].Trim();

                string? chord = Canonicalize(chordRaw);
                if (chord is null) { p.Diagnostics.Add($"line {lineNo}: bad chord '{chordRaw}'"); continue; }
                var into = isLeader ? p.LeaderBindings : p.Bindings;
                if (target.StartsWith("command:", StringComparison.OrdinalIgnoreCase))
                    into[chord] = "command:" + target["command:".Length..].Trim();
                else if (ValidActions.Contains(target))
                    into[chord] = target.ToLowerInvariant();
                else { p.Diagnostics.Add($"line {lineNo}: unknown action '{target}'"); }
            }
            else if (line.StartsWith("command ", StringComparison.OrdinalIgnoreCase))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) { p.Diagnostics.Add($"line {lineNo}: 'command' needs '='"); continue; }
                string head = line.Substring(7, eq - 7).Trim();  // between "command " and '='
                string cmdText = line[(eq + 1)..].Trim();

                // Optional "[mode]" prefix before the label: command [new] Build = ...
                string mode = "send";
                if (head.StartsWith("["))
                {
                    int rb = head.IndexOf(']');
                    if (rb < 0) { p.Diagnostics.Add($"line {lineNo}: command mode missing ']'"); continue; }
                    mode = head.Substring(1, rb - 1).Trim().ToLowerInvariant();
                    head = head[(rb + 1)..].Trim();
                    if (!ValidModes.Contains(mode)) { p.Diagnostics.Add($"line {lineNo}: unknown run mode '{mode}'"); mode = "send"; }
                }
                string label = head;
                if (label.Length == 0 || cmdText.Length == 0) { p.Diagnostics.Add($"line {lineNo}: command needs a label and text"); continue; }
                p.Commands.Add(new CmdDef(label, cmdText, mode));
            }
            else p.Diagnostics.Add($"line {lineNo}: expected 'map', 'command' or 'leader'");
        }
        return p;
    }

    /// <summary>Normalize a chord string to canonical "[ctrl+][alt+][shift+]key"; null if unparseable.</summary>
    public static string? Canonicalize(string s)
    {
        bool ctrl = false, alt = false, shift = false;
        string? key = null;
        foreach (var tokRaw in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (tokRaw.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; break;
                case "alt": case "option": alt = true; break;
                case "shift": shift = true; break;
                default:
                    if (key is not null) return null; // more than one key token
                    string nk = tokRaw.ToLowerInvariant() == "esc" ? "escape" : tokRaw.ToLowerInvariant();
                    if (!IsKey(nk)) return null;
                    key = nk;
                    break;
            }
        }
        if (key is null) return null;
        return (ctrl ? "ctrl+" : "") + (alt ? "alt+" : "") + (shift ? "shift+" : "") + key;
    }

    private static bool IsKey(string t)
    {
        if (t.Length == 1 && char.IsLetterOrDigit(t[0])) return true;
        if (t.Length is 2 or 3 && t[0] == 'f' && int.TryParse(t[1..], out int n) && n is >= 1 and <= 12) return true;
        return t is "tab" or "enter" or "escape" or "space" or "up" or "down" or "left" or "right"
            // OEM punctuation (US layout), so symbol chords like shift+comma ('<') bind too.
            or "comma" or "period" or "slash" or "semicolon" or "quote" or "backtick"
            or "minus" or "equals" or "lbracket" or "rbracket" or "backslash";
    }

    /// <summary>Build the canonical chord for a live keypress, or null if the key isn't bindable.</summary>
    public static string? ChordFor(int vk, bool ctrl, bool alt, bool shift)
    {
        string? key = KeyToken(vk);
        if (key is null) return null;
        return (ctrl ? "ctrl+" : "") + (alt ? "alt+" : "") + (shift ? "shift+" : "") + key;
    }

    private static string? KeyToken(int vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)('a' + (vk - 'A'))).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return "f" + (vk - 0x70 + 1);
        return vk switch
        {
            VK_TAB => "tab", VK_RETURN => "enter", VK_ESCAPE => "escape", VK_SPACE => "space",
            VK_UP => "up", VK_DOWN => "down", VK_LEFT => "left", VK_RIGHT => "right",
            // OEM punctuation VKs (US layout names)
            0xBA => "semicolon", 0xBB => "equals", 0xBC => "comma", 0xBD => "minus",
            0xBE => "period", 0xBF => "slash", 0xC0 => "backtick",
            0xDB => "lbracket", 0xDC => "backslash", 0xDD => "rbracket", 0xDE => "quote",
            _ => null,
        };
    }
}
