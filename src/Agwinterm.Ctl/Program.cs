using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// agwintermctl — drive agwinterm's control API from the shell (agterm's agtermctl analog).
// Usage:
//   agwintermctl ping
//   agwintermctl tree [--json]
//   agwintermctl session new [--cwd DIR] [--name NAME]
//   agwintermctl session select <target>
//   agwintermctl session close [target]
//   agwintermctl session rename <new-name...> [--target ID]
//   agwintermctl session seen [--target ID]        (clear the unseen-notification badge)
//   agwintermctl sidebar state                      (read-back: "visible tree" | "hidden flagged" | ...)
//   agwintermctl session status <idle|active|blocked|completed> [--sound [name]] [--blink] [--auto-reset] [--target ID]
//   agwintermctl session type <text...> [--target ID]
//   agwintermctl session write <text...> [--target ID]
//   agwintermctl session copy [--target ID]           (returns the selection text)
//   agwintermctl session paste <text...> [--target ID] (pastes text; clipboard if omitted)
//   agwintermctl selection all|copy|clear|finalize [--target ID]
//   agwintermctl image show <path> [--row R] [--col C] [--id N] [--target ID]
//   agwintermctl install hooks
// Target defaults to $AGWINTERM_SESSION_ID (the current session) when not given.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: agwintermctl <ping|tree|session|image|install> ... (see --help)");
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
    case "tree": cmd = "tree"; break;
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
                target = null; // new isn't targeted
                break;
            case "select":
            case "close":
                target = rest.Count > 0 ? rest[0] : (Opt("target") ?? "active");
                break;
            case "rename": // session rename <new-name...> [--target ID]
                if (rest.Count == 0 && Opt("name") is null) { Console.Error.WriteLine("session rename needs a name"); return 2; }
                cargs["name"] = rest.Count > 0 ? string.Join(' ', rest) : Opt("name")!;
                break;
            case "status":
                if (rest.Count == 0) { Console.Error.WriteLine("session status needs a state"); return 2; }
                cargs["status"] = rest[0];
                if (options.ContainsKey("blink")) cargs["blink"] = true;
                if (options.ContainsKey("auto-reset")) cargs["auto-reset"] = true;
                if (options.TryGetValue("sound", out var sndOpt)) cargs["sound"] = sndOpt; // "true" (default alert) or a name/.wav path
                break;
            case "type":
            case "write":
                // --select <text> (agterm parity): text may come via --select instead of positionals.
                cargs["text"] = rest.Count > 0 ? string.Join(' ', rest) : (Opt("select") ?? "");
                break;
            case "text": break; // dump the buffer; target only
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
                if (int.TryParse(Opt("size-percent"), out var sp)) cargs["size-percent"] = sp;
                if (options.ContainsKey("wait")) cargs["wait"] = true;
                if (options.ContainsKey("block")) cargs["block"] = true;
                break;
            case "focus": cargs["dir"] = rest.Count > 0 ? rest[0] : "right"; break;
            case "flag": cargs["op"] = rest.Count > 0 ? rest[0] : "toggle"; break; // on|off|toggle|clear
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
    case "selection":
        // agwintermctl selection all|copy|clear|finalize [--target ID]
        if (sub is not ("all" or "copy" or "clear" or "finalize"))
        { Console.Error.WriteLine("usage: agwintermctl selection all|copy|clear|finalize [--target ID]"); return 2; }
        cmd = "selection." + sub;
        target = DefaultTarget();
        break;
    case "sidebar":
        cmd = "sidebar";
        // `sidebar mode tree|flagged|toggle` switches the view mode; otherwise show|hide|toggle|expand|collapse.
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
    case "window":
        // agwintermctl window new|list|select|close|delete|rename|resize|move|zoom [<window>] ...
        cmd = "window." + sub;
        switch (sub)
        {
            case "new":
                if (Opt("name") is { } wn) cargs["name"] = wn;
                else if (rest.Count > 0) cargs["name"] = string.Join(' ', rest);
                break;
            case "list": break;
            case "select": case "close": case "delete": case "zoom":
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

string pipeName = options.TryGetValue("socket", out var s) ? s
    : Environment.GetEnvironmentVariable("AGWINTERM_PIPE") ?? "agwinterm";

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
