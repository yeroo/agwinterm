using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// agwintermctl — drive agwinterm's control API from the shell (agterm's agtermctl analog).
// Usage:
//   agwintermctl ping
//   agwintermctl version [--json]                  (the CLI that ran + the app serving the pipe)
//   agwintermctl tree [--json]
//   agwintermctl session new [--cwd DIR] [--name NAME] [--workspace ID|--workspace-name NAME [--create-workspace]] [--no-select]
//       (no workspace given = the workspace of the pane running this CLI; the active one only when there is none)
//                                                  (an unknown workspace is refused, never swapped for the active one)
//   agwintermctl session select <target>
//   agwintermctl session close [target]
//   agwintermctl session rename <new-name...> [--target ID]
//   agwintermctl session seen [--target ID]        (clear the unseen-notification badge)
//   agwintermctl sidebar state                      (read-back: "visible tree 220" = visibility, mode, width)
//   agwintermctl sidebar width [N]                  (read, or set, the sidebar width in DIP; replies {width,visible[,applied]}
//       with the width actually in effect; outside 120..600 is refused, not clamped; set while hidden = remembered)
//   agwintermctl sidebar show|hide|toggle|expand|collapse|mode <tree|flagged|toggle>   (on/off = show/hide; anything
//       else is refused rather than acknowledged)
//   agwintermctl session status <idle|active|blocked|completed> [--sound [name]] [--blink] [--auto-reset] [--target ID]
//   agwintermctl session metrics [<pane-id>] [--json] (live cell + pane pixel metrics)
//   agwintermctl session text [--lines N] [--target ID]   (N reaches into scrollback; default = screen)
//   agwintermctl session type <text...> [--allow-control] [--target ID]   (control bytes refused unless allowed)
//   agwintermctl session type --stdin [--allow-control] [--target ID]     (text = stdin, as bytes: how quotes,
//       newlines, a leading -- or runs of spaces are sent; invalid UTF-8 is refused, nothing sent; one
//       trailing newline is dropped. "quick type" is `session type --target quick:` — the quick pane's id)
//   agwintermctl session write <text...> [--target ID]                    (also takes --stdin)
//   agwintermctl session restore <command...>|none --target PANE          (pin a command re-run on every restart;
//       target mandatory; replies {action,pane,session}; read back in `tree --json` as restoreCommands)
//   agwintermctl session copy [--target ID]           (returns the selection text)
//   agwintermctl session paste <text...> [--target ID] (pastes text; clipboard if omitted)
//   agwintermctl selection all|copy|clear|finalize [--target ID]
//   agwintermctl surface cursor [--target ID]         (caret column of a pane, as a bare integer)
//   agwintermctl image show <path> [--row R] [--col C] [--id N] [--target ID]
//   agwintermctl image sixel <path> [--row R] [--col C] [--target ID]
//   agwintermctl image frameshm <Local\agwinterm-frame-NAME> [--slot N] [--seq N] [--width N]
//       [--height N] [--stride N] [--format N] [--id N] [--row R] [--col C] [--cols N] [--rows N]
//       [--sx N] [--sy N] [--sw N] [--sh N] [--target ID]
//   agwintermctl image frameshm --images '[{"name":"Local\\agwinterm-frame-x","slot":0,...}]'
//       (several entries applied as one all-or-nothing frame; see docs/specs/image-frameshm.md)
//   agwintermctl install hooks
// Target defaults to $AGWINTERM_SESSION_ID (the current session) when not given.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: agwintermctl <ping|version|tree|session|surface|image|install> ... (see README.md, \"Control it from anything\")");
    return 2;
}

// Split into positionals and --options.
var positionals = new List<string>();
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
bool jsonOut = false;
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "--json") { jsonOut = true; }
    else if (a.StartsWith("--"))
    {
        string key = a[2..];
        string val = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
        options[key] = val;
    }
    else positionals.Add(a);
}

string area = positionals.Count > 0 ? positionals[0].ToLowerInvariant() : "";
string sub = positionals.Count > 1 ? positionals[1].ToLowerInvariant() : "";
var rest = positionals.Skip(2).ToList();

string? Opt(string k) => options.TryGetValue(k, out var v) ? v : null;
string? DefaultTarget() => Opt("target") ?? Environment.GetEnvironmentVariable("AGWINTERM_SESSION_ID");

string cmd;
string? target = null;
var cargs = new Dictionary<string, object?>();

switch (area)
{
    case "ping": cmd = "ping"; break;
    case "version": cmd = "version"; break;   // handled locally, below: needs the resolved pipe name
    case "tree": cmd = "tree"; break;
    case "events": // agwintermctl events [--since CURSOR] [--limit N] — poll status/notification/session/tree events
        cmd = "events";
        if (Opt("since") is { } sinceV && int.TryParse(sinceV, out var sinceN)) cargs["since"] = sinceN;
        if (Opt("limit") is { } limV && int.TryParse(limV, out var limN)) cargs["limit"] = limN;
        target = null;
        break;
    case "install" when sub == "hooks": cmd = "install.hooks"; break;
    case "install" when sub == "skill": cmd = "install.skill"; break;
    case "install" when sub == "shell": cmd = "install.shell"; break;
    case "install" when sub == "cli": cmd = "install.cli"; if (options.ContainsKey("remove")) cargs["remove"] = true; break;
    case "omp" when sub == "list": cmd = "omp.list"; break;
    case "omp" when sub == "set":
        cmd = "omp.set";
        if (rest.Count == 0) { Console.Error.WriteLine("omp set needs a theme name"); return 2; }
        cargs["name"] = string.Join(' ', rest);
        if (options.ContainsKey("persist")) cargs["persist"] = true;
        break;
    case "workspace":
        target = Opt("target") ?? "active";
        switch (sub)
        {
            case "new":
                cmd = "workspace.new";
                if (Opt("name") is { } wsname) cargs["name"] = wsname;
                else if (rest.Count > 0) cargs["name"] = rest[0];
                target = null;
                break;
            case "rename":
                cmd = "workspace.rename";
                if (rest.Count == 0) { Console.Error.WriteLine("workspace rename needs a name"); return 2; }
                cargs["name"] = string.Join(' ', rest);
                break;
            case "delete": cmd = "workspace.delete"; break;
            case "select": cmd = "workspace.select"; break;
            case "collapse": cmd = "workspace.collapse"; if (rest.Count > 0) target = rest[0]; break;
            case "expand": cmd = "workspace.expand"; if (rest.Count > 0) target = rest[0]; break;
            case "focus":
                cmd = "workspace.focus";
                cargs["op"] = rest.Count > 0 ? rest[0] : "toggle"; // on|off|toggle (focuses the active workspace)
                target = null;
                break;
            case "move":
                cmd = "workspace.move";
                cargs["dir"] = Opt("to") ?? (rest.Count > 0 ? rest[0] : "down");
                break;
            default: Console.Error.WriteLine($"unknown workspace command '{sub}'"); return 2;
        }
        break;
    case "session":
        cmd = "session." + sub;
        target = DefaultTarget();
        switch (sub)
        {
            case "new":
                if (Opt("cwd") is { } cwd) cargs["cwd"] = cwd;
                if (Opt("name") is { } name) cargs["name"] = name;
                if (Opt("workspace") is { } wsp) cargs["workspace"] = wsp;
                if (Opt("command") is { } command) cargs["command"] = command;
                if (Opt("workspace-name") is { } wsn) cargs["workspace-name"] = wsn;
                if (options.ContainsKey("create-workspace")) cargs["create-workspace"] = true;
                if (Opt("profile") is { } prof) cargs["profile"] = prof;
                if (options.ContainsKey("no-select")) cargs["no-select"] = true;   // create in background, keep focus
                if (options.ContainsKey("wait")) cargs["wait"] = true;             // hold on "press any key" after --command exits
                // Who is asking: the pane this CLI runs in, the same AGWINTERM_SESSION_ID every other
                // verb defaults its target to. With no --workspace the session lands in THAT pane's
                // workspace, not in whatever the user last clicked. Sent as `caller`, not as the
                // target: session.new is targetless on the server, and a target would make an
                // unknown value "session not found" instead of the active-workspace fallback.
                if (Environment.GetEnvironmentVariable("AGWINTERM_SESSION_ID") is { Length: > 0 } callerPane) cargs["caller"] = callerPane;
                target = null; // new isn't targeted
                break;
            case "select":
            case "close":
            case "duplicate":
                target = rest.Count > 0 ? rest[0] : (Opt("target") ?? "active");
                break;
            case "rename": // session rename <new-name...> [--target ID]
                if (rest.Count == 0 && Opt("name") is null) { Console.Error.WriteLine("session rename needs a name"); return 2; }
                cargs["name"] = rest.Count > 0 ? string.Join(' ', rest) : Opt("name")!;
                break;
            // session metrics [<pane-id>] — cell size + pixel box of a pane, for sizing an
            // image.frameshm buffer. No args of its own; --json is the useful form.
            case "metrics":
                if (rest.Count > 0) target = rest[0];
                break;
            case "status":
                if (rest.Count == 0) { Console.Error.WriteLine("session status needs a state"); return 2; }
                cargs["status"] = rest[0];
                if (options.ContainsKey("blink")) cargs["blink"] = true;
                if (options.ContainsKey("auto-reset")) cargs["auto-reset"] = true;
                if (options.TryGetValue("sound", out var sndOpt)) cargs["sound"] = sndOpt; // "true" (default alert) or a name/.wav path
                break;
            case "type":
                // Deliberate control bytes (an escape sequence for a TUI, a lone ^C). Without it a
                // control byte is refused, because the usual reason one is there is that a caller
                // built the string wrong.
                if (options.ContainsKey("allow-control")) cargs["allow-control"] = true;
                goto case "write";
            case "write":
                // --stdin: the text is standard input, as bytes. Positionals are joined with one
                // space and the splitter eats a leading "--", so quotes, newlines, runs of spaces and
                // a "--flag" as text only survive this way. Two sources for one field is an
                // ambiguity we refuse rather than resolve: --stdin with positional text or --select
                // is an error. Invalid UTF-8 exits non-zero and sends NOTHING — the server's own
                // reader would have replaced the bad bytes with U+FFFD and answered ok.
                if (options.ContainsKey("stdin"))
                {
                    // The splitter hands "--stdin" the next bare word as its value, so a positional
                    // after it shows up as Opt("stdin") != "true" rather than in `rest`.
                    if (rest.Count > 0 || Opt("stdin") != "true" || Opt("select") is not null)
                    { Console.Error.WriteLine($"session {sub}: --stdin cannot be combined with positional text or --select (one source for the text, not two)"); return 2; }
                    var stdinText = Agwinterm.Pty.StdinText.Read(Console.OpenStandardInput());
                    if (!stdinText.Ok) { Console.Error.WriteLine($"session {sub} --stdin: {stdinText.Error}; nothing was sent"); return 2; }
                    cargs["text"] = stdinText.Text;
                    break;
                }
                // --select <text> (agterm parity): text may come via --select instead of positionals.
                cargs["text"] = rest.Count > 0 ? string.Join(' ', rest) : (Opt("select") ?? "");
                break;
            case "text": // dump the buffer; --lines N reaches back into scrollback (default: the visible screen)
                if (int.TryParse(Opt("lines"), out var textLines)) cargs["lines"] = textLines;
                break;
            case "copy": break;  // return the target's selection text; target only
            case "seen": break;  // clear the unseen-notification badge; target only
            case "output": break; // last completed command's output (FTCS marks); target only
            case "paste": // paste literal text (or the clipboard if none) into the target pane
                cargs["text"] = rest.Count > 0 ? string.Join(' ', rest) : (Opt("text") ?? "");
                break;
            case "go":
                if (rest.Count == 0) { Console.Error.WriteLine("session go needs a direction (next|prev|first|last|next-attention|prev-attention)"); return 2; }
                cargs["dir"] = rest[0]; target = null;
                break;
            case "move":
                if (rest.Count > 0 && Opt("to") is null) cargs["workspace"] = rest[0]; // relocate to workspace
                else cargs["dir"] = Opt("to") ?? "down";                                // reorder within workspace
                break;
            case "search": // session search "term" | --next | --prev | --close
                if (Opt("close") is not null) cargs["action"] = "close";
                else if (Opt("next") is not null) cargs["action"] = "next";
                else if (Opt("prev") is not null) cargs["action"] = "prev";
                else if (rest.Count > 0) cargs["query"] = string.Join(' ', rest);
                break;
            case "split": cargs["op"] = rest.Count > 0 ? rest[0] : "toggle"; break;
            case "readonly": cargs["op"] = rest.Count > 0 ? rest[0] : "toggle"; break; // on|off|toggle|state; block input to the pane
            case "scratch": cargs["op"] = rest.Count > 0 ? rest[0] : "toggle"; break; // on|off|toggle; per-session extra shell
            case "overlay": // overlay open <command> [--size-percent N] [--wait|--block] | overlay close | overlay resize --size-percent N | overlay result
                cargs["action"] = rest.Count > 0 ? rest[0] : "open";
                if (rest.Count > 1) cargs["command"] = string.Join(' ', rest.Skip(1));
                else if (Opt("command") is { } ovcmd) cargs["command"] = ovcmd;
                // An unparseable --size-percent is refused, not dropped: `--size-percent sixty` used to
                // open a FULL-SCREEN overlay and report success. The range (1..100) is the server's
                // call, so its refusal names the value and the way to ask for the full region.
                if (Opt("size-percent") is { } spText)
                {
                    if (!int.TryParse(spText, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var sp))
                    {
                        Console.Error.WriteLine($"--size-percent needs a whole number in 1..100, not '{spText}'; omit it for the full content region");
                        return 2;
                    }
                    cargs["size-percent"] = sp;
                }
                if (options.ContainsKey("wait")) cargs["wait"] = true;
                if (options.ContainsKey("block")) cargs["block"] = true;
                break;
            case "focus": cargs["dir"] = rest.Count > 0 ? rest[0] : "right"; break;
            case "flag": cargs["op"] = rest.Count > 0 ? rest[0] : "toggle"; break; // on|off|toggle|clear
            case "bind": cargs["agent"] = rest.Count > 0 ? rest[0] : "claude"; break; // bind a resumable agent (claude) | none to clear
            case "restore": cargs["command"] = rest.Count > 0 ? string.Join(' ', rest) : (Opt("command") ?? ""); break; // pin a per-pane restore command | none to clear
            case "background": // session background set <path> [--opacity N] [--mode fit|fill|center|tile] | background clear
                cargs["action"] = rest.Count > 0 ? rest[0] : "set";
                if (rest.Count > 1) cargs["path"] = string.Join(' ', rest.Skip(1));
                else if (Opt("path") is { } bgp) cargs["path"] = bgp;
                if (int.TryParse(Opt("opacity"), out var bop)) cargs["opacity"] = bop;
                if (Opt("mode") is { } bgm) cargs["mode"] = bgm;
                break;
            case "switch": cargs["op"] = rest.Count > 0 ? rest[0] : "advance"; target = null; break; // MRU walk: begin|advance|advance-back|commit|cancel
            case "resize":
                if (double.TryParse(Opt("split-ratio"), System.Globalization.CultureInfo.InvariantCulture, out var sr)) cargs["ratio"] = sr;
                if (int.TryParse(Opt("grow-left"), out var gl)) cargs["grow-left"] = gl;
                if (int.TryParse(Opt("grow-right"), out var gr)) cargs["grow-right"] = gr;
                break;
            default:
                Console.Error.WriteLine($"unknown session command '{sub}'"); return 2;
        }
        break;
    case "command":
        switch (sub)
        {
            case "run": // command run "<name-or-command...>" [--mode new|overlay|detached|send]
                cmd = "command.run";
                if (rest.Count > 0) cargs["name"] = string.Join(' ', rest);
                else if (Opt("command") is { } rawcmd) cargs["command"] = rawcmd;
                else { Console.Error.WriteLine("command run needs a name or command"); return 2; }
                if (Opt("mode") is { } cmode) cargs["mode"] = cmode;
                break;
            case "list": cmd = "command.list"; break;
            case "leader": // command leader state|begin|cancel|key:<chord>
                cmd = "command.leader";
                cargs["op"] = rest.Count > 0 ? rest[0] : "state";
                break;
            default: Console.Error.WriteLine($"unknown command '{area} {sub}'"); return 2;
        }
        break;
    case "claude" when sub == "adopt": cmd = "claude.adopt"; break; // bind existing claude convos to their panes
    case "claude" when sub == "yolo": cmd = "claude.yolo"; target = DefaultTarget(); break; // restart the target pane's claude in --dangerously-skip-permissions, resumed
    case "claude" when sub == "update": cmd = "claude.update"; break; // run `claude update` in an overlay, then restart live claude panes
    case "app" when sub == "update": cmd = "app.update"; break; // self-update agwinterm (download+verify latest release, restart, sessions restore)
    case "theme" when sub == "list": cmd = "theme.list"; break;
    case "theme" when sub == "set":
        cmd = "theme.set";
        if (rest.Count == 0) { Console.Error.WriteLine("theme set needs a name"); return 2; }
        cargs["name"] = string.Join(' ', rest);
        break;
    case "keymap" when sub == "reload": cmd = "keymap.reload"; break;
    case "profiles" when sub == "list": cmd = "profiles.list"; break;
    case "profiles" when sub == "reload": cmd = "profiles.reload"; break;
    case "restore" when sub == "clear": cmd = "restore.clear"; break;
    case "config" when sub == "set":
        cmd = "config.set";
        if (rest.Count < 1) { Console.Error.WriteLine("config set needs <key> <value>"); return 2; }
        cargs["key"] = rest[0];
        cargs["value"] = rest.Count > 1 ? string.Join(' ', rest.Skip(1)) : "";
        break;
    case "config" when sub == "get":
        cmd = "config.get";
        if (rest.Count < 1) { Console.Error.WriteLine("config get needs <key>"); return 2; }
        cargs["key"] = rest[0];
        break;
    case "config" when sub == "list": cmd = "config.list"; break;
    case "settings": cmd = "settings.open"; break;
    case "surface":
        // agwintermctl surface cursor [--target ID] — the caret column of a pane, as a bare integer.
        // A pane id selects that pane — and the session id IS pane 0's id, so it selects pane 0
        // regardless of focus; only a unique session NAME resolves to the focused pane.
        if (sub != "cursor")
        { Console.Error.WriteLine("usage: agwintermctl surface cursor [--target ID]"); return 2; }
        cmd = "surface.cursor";
        target = DefaultTarget();
        break;
    case "selection":
        // agwintermctl selection all|copy|clear|finalize [--target ID]
        if (sub is not ("all" or "copy" or "clear" or "finalize"))
        { Console.Error.WriteLine("usage: agwintermctl selection all|copy|clear|finalize [--target ID]"); return 2; }
        cmd = "selection." + sub;
        target = DefaultTarget();
        break;
    case "sidebar":
        cmd = "sidebar";
        // `sidebar width [N]` reads (no N) or sets the width; `sidebar mode tree|flagged|toggle`
        // switches the view mode; otherwise show|hide|toggle|expand|collapse (on/off = show/hide).
        if (sub == "width")
        {
            cargs["op"] = "width";
            if (rest.Count > 0)
            {
                // An unparseable width is refused here, not dropped into a read: `sidebar width wide`
                // answering with the current width would look like a successful set. The range is the
                // server's call (one refusal text, shared with the fake host's tests), so only the
                // "not a number at all" case is caught before the request is built.
                if (!int.TryParse(rest[0], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var sw))
                {
                    Console.Error.WriteLine(Agwinterm.Pty.SidebarWidths.Refusal($"'{rest[0]}'"));
                    return 2;
                }
                cargs["width"] = sw;
            }
        }
        else
            cargs["op"] = sub == "mode"
                ? "mode:" + (rest.Count > 0 ? rest[0] : "toggle")
                : (sub.Length > 0 ? sub : "toggle");
        break;
    case "quick":
        cmd = "quick";
        cargs["op"] = sub.Length > 0 ? sub : "toggle"; // on|off|toggle; the window's throwaway shell
        break;
    case "broadcast": // agwintermctl broadcast [on|off|toggle|state] — typing fans out to the whole workspace
        cmd = "broadcast";
        cargs["op"] = sub.Length > 0 ? sub : "toggle";
        break;
    case "notify": // agwintermctl notify <body...> [--title T] [--target ID]
        cmd = "notify";
        target = DefaultTarget();
        cargs["body"] = string.Join(' ', positionals.Skip(1));
        if (Opt("title") is { } ntitle) cargs["title"] = ntitle;
        break;
    case "font":
        cmd = "font";
        target = DefaultTarget();
        if (sub is not ("inc" or "dec" or "reset")) { Console.Error.WriteLine("usage: agwintermctl font inc|dec|reset [--target ID]"); return 2; }
        cargs["op"] = sub;
        break;
    case "dashboard":
        // agwintermctl dashboard [<id> ...] [--close] [--font-size N | --auto-size]
        cmd = "dashboard";
        var dashIds = positionals.Skip(1).ToList();   // session ids after "dashboard"
        if (dashIds.Count > 0) cargs["ids"] = string.Join(",", dashIds);
        if (options.ContainsKey("close")) cargs["close"] = true;
        if (Opt("font-size") is { } dfs && int.TryParse(dfs, out var dfsn)) cargs["font-size"] = dfsn;   // omit / --auto-size => auto
        break;
    case "window":
        // agwintermctl window new|list|select|close|delete|rename|resize|move|zoom [<window>] ...
        cmd = "window." + sub;
        switch (sub)
        {
            case "new":
                if (Opt("name") is { } wn) cargs["name"] = wn;
                else if (rest.Count > 0) cargs["name"] = string.Join(' ', rest);
                break;
            case "list": case "state": break;   // state = read-back of window UI flags
            case "select":
            case "close":
            case "delete":
            case "zoom":
                target = rest.Count > 0 ? rest[0] : (Opt("target") ?? "active");
                break;
            case "rename":
                if (rest.Count < 2) { Console.Error.WriteLine("window rename needs <window> <name>"); return 2; }
                target = rest[0]; cargs["name"] = string.Join(' ', rest.Skip(1));
                break;
            case "resize":
                target = rest.Count > 0 ? rest[0] : "active";
                if (int.TryParse(rest.Count > 1 ? rest[1] : Opt("w"), out var rw)) cargs["w"] = rw;
                if (int.TryParse(rest.Count > 2 ? rest[2] : Opt("h"), out var rh)) cargs["h"] = rh;
                break;
            case "move":
                target = rest.Count > 0 ? rest[0] : "active";
                if (int.TryParse(rest.Count > 1 ? rest[1] : Opt("x"), out var mx)) cargs["x"] = mx;
                if (int.TryParse(rest.Count > 2 ? rest[2] : Opt("y"), out var my)) cargs["y"] = my;
                break;
            default: Console.Error.WriteLine($"unknown window command '{sub}'"); return 2;
        }
        break;
    case "image" when sub == "show":
        cmd = "image.show";
        target = DefaultTarget();
        if (rest.Count == 0) { Console.Error.WriteLine("image show needs a path"); return 2; }
        cargs["path"] = System.IO.Path.GetFullPath(rest[0]);
        if (int.TryParse(Opt("row"), out var row)) cargs["row"] = row;
        if (int.TryParse(Opt("col"), out var col)) cargs["col"] = col;
        if (int.TryParse(Opt("id"), out var id)) cargs["id"] = id;
        break;
    case "image" when sub == "frameshm":
        cmd = "image.frameshm";
        target = DefaultTarget();
        if (!Agwinterm.Ctl.FrameShmCli.TryBuildArgs(rest, options, out var shmArgs, out var shmErr))
        { Console.Error.WriteLine("image frameshm: " + shmErr); return 2; }
        foreach (var kv in shmArgs) cargs[kv.Key] = kv.Value;
        break;
    case "image" when sub == "sixel":
        cmd = "image.sixel";
        target = DefaultTarget();
        if (rest.Count == 0) { Console.Error.WriteLine("image sixel needs a path"); return 2; }
        cargs["path"] = System.IO.Path.GetFullPath(rest[0]);
        if (int.TryParse(Opt("row"), out var srow)) cargs["row"] = srow;
        if (int.TryParse(Opt("col"), out var scol)) cargs["col"] = scol;
        break;
    default:
        Console.Error.WriteLine($"unknown command '{area} {sub}'"); return 2;
}

// install.skill is a self-contained file-write (writes SKILL.md to ~/.claude & ~/.codex); run it
// locally so it works with no running app / pipe (e.g. from the installer, incl. silent installs).
if (cmd == "install.skill")
{
    Console.WriteLine(Agwinterm.Pty.AgentSkill.Install());
    return 0;
}
// install.cli (PATH edit) and omp.list (file enumeration) are self-contained too — run locally.
if (cmd == "install.cli")
{
    Console.WriteLine(options.ContainsKey("remove")
        ? Agwinterm.Pty.CliInstaller.Uninstall() : Agwinterm.Pty.CliInstaller.Install());
    return 0;
}
if (cmd == "omp.list")
{
    foreach (var (name, _) in Agwinterm.Pty.OmpThemes.List()) Console.WriteLine(name);
    return 0;
}

var req = new Dictionary<string, object?> { ["cmd"] = cmd };
if (!string.IsNullOrEmpty(target)) req["target"] = target;
// --window <id|prefix|active> targets a specific window for content verbs (window.* use the positional target).
if (area != "window" && Opt("window") is { } winSel) req["window"] = winSel;
if (cargs.Count > 0) req["args"] = cargs;
string requestJson = JsonSerializer.Serialize(req);

// --pipe is an alias for --socket (matches the app's --pipe flag); AGWINTERM_PIPE is set inside
// every agwinterm session, so ctl run from within a session auto-targets that instance (dev or release).
string pipeName = options.TryGetValue("socket", out var s) ? s
    : options.TryGetValue("pipe", out var pp) ? pp
    : Environment.GetEnvironmentVariable("AGWINTERM_PIPE") ?? "agwinterm";

// `version` answers "which binary did I run, and which app did it reach". The app half is a ping,
// but the CLI half must survive a dead pipe — that is the case the command exists for — so it is
// rendered locally and exits 0 either way.
if (cmd == "version")
{
    var report = Agwinterm.Pty.VersionReport.Build(pipeName);
    Console.WriteLine(jsonOut ? Agwinterm.Pty.VersionReport.RenderJson(report)
                              : Agwinterm.Pty.VersionReport.RenderText(report));
    return 0;
}

try
{
    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
    pipe.Connect(3000);
    // leaveOpen so the reader/writer don't each try to close the same pipe (double-close throws).
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
    writer.WriteLine(requestJson);
    string? response = reader.ReadLine();
    if (response is null) { Console.Error.WriteLine("no response"); return 1; }

    if (jsonOut) { Console.WriteLine(response); }
    else
    {
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        bool ok = root.TryGetProperty("ok", out var o) && o.GetBoolean();
        if (ok)
        {
            if (root.TryGetProperty("result", out var res))
                Console.WriteLine(res.ValueKind == JsonValueKind.String ? res.GetString() : res.GetRawText());
            return 0;
        }
        Console.Error.WriteLine(root.TryGetProperty("error", out var err) ? err.GetString() : "error");
        return 1;
    }
    return 0;
}
catch (TimeoutException)
{
    Console.Error.WriteLine($"could not connect to agwinterm pipe '\\\\.\\pipe\\{pipeName}' (is agwinterm running?)");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}
