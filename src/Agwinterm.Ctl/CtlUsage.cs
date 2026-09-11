using System.Text;

namespace Agwinterm.Ctl;

/// <summary>
/// The CLI's own usage text and the two usage refusals that had no home.
///
/// <para>Split out of <c>Program.cs</c> (top-level statements, so not directly testable) for the
/// reason <see cref="FrameShmCli"/> was: the part that has to be right is a decision, and a
/// decision that cannot be asserted on drifts. <see cref="Long"/> used to be a 96-line COMMENT at
/// the top of <c>Program.cs</c> that nothing printed, while <c>Help.cs</c> told users to run
/// <c>agwintermctl --help</c> — a flag the CLI did not have. It is one string now, printed by
/// <c>--help</c>, so the promise is true and there is no second copy to fall behind.</para>
/// </summary>
public static class CtlUsage
{
    /// <summary>The one-line summary printed with no arguments at all.</summary>
    public const string Short = "usage: agwintermctl <ping|version|tree|session|surface|image|install> ... (see README.md, \"Control it from anything\")";

    /// <summary>Every verb and flag, the text <c>--help</c> prints. Moved here from the comment it
    /// used to be; keep it and the code in step, because this is now what users read.</summary>
    public const string Long = """
        agwintermctl — drive agwinterm's control API from the shell (agterm's agtermctl analog).
        Usage:
          agwintermctl ping
          agwintermctl version [--json]                  (the CLI that ran + the app serving the pipe)
          agwintermctl tree [--json]
          agwintermctl session new [--command "PowerShell code"] [--command-mode powershell|direct] [--cwd DIR] [--name NAME] [--workspace ID|--workspace-name NAME [--create-workspace]] [--no-select]
              (no workspace given = the workspace of the pane running this CLI; the active one only when there is none)
                                                         (an unknown workspace is refused, never swapped for the active one)
          agwintermctl session select <target>
          agwintermctl session close [target]
          agwintermctl session rename <new-name...> [--target ID]
      (names a SESSION; a pane id - including $AGWINTERM_SESSION_ID, which IS a pane id - names the
      session that pane belongs to. Replies {session,name}: the session it landed on and the name in
      effect. Blank is refused; so is a target that belongs to no session)
          agwintermctl session split [on|off|toggle] [--axis vertical|horizontal] [--target ID]
              (replies with a PANE ID: on/toggle-on = the split pane's, also when the session was already split;
              off/toggle-off = the survivor's. Default op = toggle. The axis names the ARRANGEMENT, agterm's words:
              vertical = left/right panes (the default of a session never split), horizontal = top/bottom panes.
              Omitted = keep the session's orientation; given on an already-split session = re-orient it live)
          agwintermctl session split close [--target ID]   (close ONE pane, EITHER side: a pane id = that pane; a session
              name or no target = the session's focused pane, what Ctrl+Shift+W closes; from a pane's own CLI, that pane.
              Replies with the SURVIVOR's id. A one-pane session is refused — `session close` closes a session)
          agwintermctl session swap [--target ID]           (exchange the two panes: order reversed, focus follows the pane,
              axis and ratio sequence kept — the left/top box keeps its size, the contents change places — and EVERY ID
              kept: a swap moves panes, never ids, so the session id keeps naming the shell it named, now on the other
              side. Target = a session, either of its panes, or nothing — from a pane's own CLI, that pane's
              session; otherwise the active one. Replies
              {session,paneIds,focusedPane,axis} — the tree's split block after the swap. A one-pane session is refused)
          agwintermctl session focus [primary|split|left|right|top|bottom|other]   (default other; left/right exist on a
              vertical split only, top/bottom on a horizontal one — the wrong pair is refused naming the axis)
          agwintermctl session resize [--split-ratio R] [--grow-left N|--grow-right N|--grow-top N|--grow-bottom N]
              (left/right move a vertical split's divider by N columns, top/bottom a horizontal one's by N rows;
              the other axis's flags are refused, and the divider does not move)
          agwintermctl session context <text...> [--target ID]   (one line of "what is this pane for", shown dimmed
              beside the name and read back in `tree --json` as context; survives a restart. Blank, a control
              character or more than 200 characters is refused; replies {session,context})
          agwintermctl session context --clear [--target ID]      (remove it; text beside --clear is refused)
          agwintermctl session context --stdin [--target ID]      (text = stdin, one trailing newline dropped; an
              embedded newline is then refused — the context is one line)
          agwintermctl session seen [--target ID]        (clear the unseen-notification badge)
          agwintermctl sidebar state                      (read-back: "visible tree 220" = visibility, mode, width)
          agwintermctl sidebar width [N]                  (read, or set, the sidebar width in DIP; replies {width,visible[,applied]}
              with the width actually in effect; outside 120..600 is refused, not clamped; set while hidden = remembered)
          agwintermctl sidebar show|hide|toggle|expand|collapse|mode <tree|flagged|toggle>   (on/off = show/hide; anything
              else is refused rather than acknowledged)
          agwintermctl session status <idle|active|blocked|completed> [--sound [name]] [--blink] [--auto-reset] [--target ID]
          agwintermctl session metrics [<pane-id>] [--json] (live cell + pane pixel metrics)
          agwintermctl session text [--all|--lines N] [--target ID]   (N reaches into scrollback; --all = the whole
              buffer, screen + scrollback; default = screen; --all with --lines is refused)
          agwintermctl session hud [open|update] <message...> [--detail TEXT] [--spinner|--spinner-style STYLE]
              [--position ANCHOR] [--size-percent N] [--background-color HEX] [--text-color HEX] [--target ID]
          agwintermctl session hud close [--target ID]   (see docs/session-hud.md)
          agwintermctl session overlay open <command...> [--pane left|right] [--wait|--block] [--size-percent N] [--target ID]
          agwintermctl session overlay close|result|copy [--pane left|right] [--target ID]
          agwintermctl session overlay text [--all|--lines N] [--pane left|right] [--target ID]
          agwintermctl session overlay resize --size-percent N [--target ID]   (the session-wide overlay only)
              (copy and text print result.text; --pane names a PANE slot, passed through as typed — the server validates
              the word; --size-percent beside --pane, and resize --pane, are refused here and nothing is sent: a pane
              overlay is always full-pane. The rule, quoted from ISessionHost.SessionOverlay — the skill quotes it too:
              A session has three overlay slots: one session-wide and one per pane. A session-wide overlay
              covers the whole session (every pane, and any pane overlay under it), as today. A pane overlay
              covers exactly one pane's box - always the full box, never floating - and the sibling pane stays
              visible and interactive. --pane left|right names the slot: left is pane 0 and right is pane 1
              whatever the axis (on a horizontal split left is the top pane), a non-split session accepts
              --pane left, and the flag omitted means the session-wide slot - today's behaviour, byte for
              byte. A pane overlay is that pane's surface while it is open: keys typed into the focused pane,
              the mouse inside the pane's box and --target active reach the overlay; --target with a pane id
              reaches the shell underneath (agterm: "session text reads the surface underneath"); --target
              with the overlay's id reaches the overlay from anywhere, and on session overlay itself names
              that overlay's slot - the same as passing its --pane word - for as long as the id resolves (an
              overlay that closed is reached by --pane only); with --pane naming the other side it is refused.
              The slot moves with its pane (a swap, a split close of the other pane) and dies with it (split
              close, split off, the shell exiting when that removes the pane - a single-pane session keeps an
              exited shell on screen, and its overlay with it - session close, the window closing).)
          agwintermctl session type <text...> [--allow-control] [--target ID]   (control bytes refused unless allowed)
          agwintermctl session type --stdin [--allow-control] [--target ID]     (text = stdin, as bytes: how quotes,
              newlines, a leading -- or runs of spaces are sent; invalid UTF-8 is refused, nothing sent; one
              trailing newline is dropped. "quick type" is `session type --target quick:` — the quick pane's id)
          agwintermctl session write <text...> [--target ID]                    (also takes --stdin)
          agwintermctl session restore <command...>|none --target PANE          (pin a command re-run on every restart;
              target mandatory; replies {action,pane,session}; read back in `tree --json` as restoreCommands)
          agwintermctl restore capture [--target ID]       (capture the foreground command of every real pane — or the one
              pane/session named — into its restore slot NOW, so a crash or Stop-Process keeps it; replies
              {captured,replayOnRestore,panes:[{pane,session,captured|null}]}; read back in `tree --json` as
              capturedCommands; replayOnRestore = the restore-commands toggle, which gates the replay, not the capture)
          agwintermctl restore clear                       (delete this window's saved session tree)
          agwintermctl session copy [--target ID]           (returns the selection text)
          agwintermctl session paste <text...> [--target ID] (pastes text; clipboard if omitted)
          agwintermctl selection all|copy|clear|finalize [--target ID]
          agwintermctl surface cursor [--target ID]         (caret column of a pane, as a bare integer)
          agwintermctl image show <path> [--row R] [--col C] [--id N] [--target ID]
          agwintermctl image sixel <path> [--row R] [--col C] [--target ID]
          agwintermctl image frameshm <Local\agwinterm-frame-NAME> [--slot N] [--seq N] [--width N]
              [--height N] [--stride N] [--format N] [--id N] [--row R] [--col C] [--cols N] [--rows N]
              [--sx N] [--sy N] [--sw N] [--sh N] [--target ID]
          agwintermctl image frameshm --images '[{"name":"Local\\agwinterm-frame-x","slot":0,...}]'
              (several entries applied as one all-or-nothing frame; see docs/specs/image-frameshm.md)
          agwintermctl install hooks
        Target defaults to $AGWINTERM_SESSION_ID (the current session) when not given.
        """;

    /// <summary>
    /// Is this invocation a request for help rather than a command?
    ///
    /// <para>Asked of the RAW argument array, before the splitter runs, and answered before anything
    /// is sent. The splitter turns every unknown <c>--flag</c> into a dictionary entry that most
    /// verbs never read (<c>Program.cs</c>, "Split into positionals and --options"), so
    /// <c>--help</c> used to be INTERPRETED: <c>session close --help</c> built a real
    /// <c>session.close</c> whose target fell back to <c>"active"</c> and closed the session the
    /// user was looking at, with exit 0. Four verbs had grown their own allow-lists against exactly
    /// this (<c>hud</c>, <c>split</c>, <c>swap</c>, <c>restore capture</c>); help is the one spelling
    /// every verb shares, so it is answered once, here, for all of them.</para>
    ///
    /// <para>Only the exact word <c>--help</c> counts. <c>-h</c>, <c>-?</c> and <c>/?</c> do NOT
    /// start with <c>--</c>, so the splitter files them as POSITIONALS: they are legitimate text for
    /// <c>session type</c> and a legitimate name for <c>session rename</c>, and swallowing them here
    /// would break a caller that means them literally. Text that must survive anything goes through
    /// <c>--stdin</c>, which reads the payload as bytes and never sees this array.</para>
    /// </summary>
    public static bool IsHelpRequest(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
            if (string.Equals(args[i], "--help", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>What <c>session text</c> accepts: its own two flags plus the global selectors.</summary>
    private static readonly HashSet<string> TextOptions =
        new(FrameShmCli.GlobalValuedOptions, StringComparer.OrdinalIgnoreCase) { "all", "lines" };

    /// <summary>
    /// Refuse an option <c>session text</c> does not read, instead of dropping it.
    ///
    /// <para><c>session text</c> read only <c>--all</c> and <c>--lines</c> and dropped the rest, so
    /// <c>session text --pane right</c> reported success while reading the LEFT pane — the caller
    /// got the wrong pane's buffer with exit 0 and no way to tell. <c>docs/agterm-parity.md</c>
    /// states the rule this breaks: an unsupported form is "refused rather than silently widened".
    /// <c>--pane</c> is real on <c>session overlay</c> only, which is where the mistake comes from,
    /// so its refusal names the two spellings that do work.</para>
    /// </summary>
    public static bool TryTextOptions(IEnumerable<string> keys, out string? refusal)
    {
        foreach (var key in keys)
        {
            if (TextOptions.Contains(key)) continue;
            refusal = string.Equals(key, OverlayPanesPaneKey, StringComparison.OrdinalIgnoreCase)
                ? "session text: --pane is a `session overlay` flag, not a `session text` one — it was DROPPED here, so this call would have read the focused pane and reported success. To read the other pane pass its id: `session text --target <pane-id>` (tree --json lists paneIds). For a pane's overlay use `session overlay text --pane left|right`. Nothing read."
                : $"session text: unknown option --{key} (it takes --all, --lines and --target). Nothing read.";
            return false;
        }
        refusal = null;
        return true;
    }

    /// <summary>The <c>--pane</c> spelling, from the verb that owns it.</summary>
    private const string OverlayPanesPaneKey = Agwinterm.Pty.OverlayPanes.Key;
}

/// <summary>
/// The encoding this CLI's own stdout speaks.
///
/// <para>The control pipe is UTF-8 both ways and the CLI decodes it correctly; then it printed with
/// a bare <c>Console.WriteLine</c>. On Windows .NET initialises <c>Console.OutputEncoding</c> from
/// <c>GetConsoleOutputCP()</c>, so every reply was RE-ENCODED into the host console's code page —
/// cp437 on a stock en-US console — for a captured pipe exactly as for a screen. A script reading
/// <c>session text</c> got box-drawing in cp437 and a silent <c>?</c> for anything cp437 has no
/// room for. Inside an agwinterm pane it looked fine only because the shell profile runs
/// <c>chcp 65001</c>.</para>
///
/// <para>So: a REDIRECTED stream gets UTF-8, and a console does not. Setting
/// <c>Console.OutputEncoding</c> would fix the pipe too, but it calls <c>SetConsoleOutputCP</c> on
/// the console this short-lived process shares with its parent shell — the code page would outlive
/// the call, like <c>chcp</c>. Replacing the writer changes nothing the parent can see. A human at a
/// cp437 console keeps the rendering they have; a script gets the bytes the pipe sent.</para>
/// </summary>
public static class CtlStdout
{
    /// <summary>UTF-8 with no BOM: a leading BOM would be the first thing every caller parsed.</summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A UTF-8 writer over <paramref name="stream"/> when it is redirected, else null —
    /// null meaning "leave <c>Console.Out</c> alone". Autoflushed: the process exits without
    /// disposing it.</summary>
    public static TextWriter? Utf8Writer(Stream stream, bool redirected) =>
        redirected ? new StreamWriter(stream, Utf8NoBom) { AutoFlush = true } : null;
}
