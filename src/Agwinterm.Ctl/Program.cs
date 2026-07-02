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
//   agwintermctl session status <idle|active|blocked|completed> [--target ID]
//   agwintermctl session type <text...> [--target ID]
//   agwintermctl session write <text...> [--target ID]
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
                target = null; // new isn't targeted
                break;
            case "select":
            case "close":
                target = rest.Count > 0 ? rest[0] : (Opt("target") ?? "active");
                break;
            case "status":
                if (rest.Count == 0) { Console.Error.WriteLine("session status needs a state"); return 2; }
                cargs["status"] = rest[0];
                break;
            case "type":
            case "write":
                // --select <text> (agterm parity): text may come via --select instead of positionals.
                cargs["text"] = rest.Count > 0 ? string.Join(' ', rest) : (Opt("select") ?? "");
                break;
            case "text": break; // dump the buffer; target only
            case "copy": break;  // return the target's selection text; target only
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
            case "scratch": cargs["op"] = rest.Count > 0 ? rest[0] : "toggle"; break; // on|off|toggle; per-session extra shell
            case "focus": cargs["dir"] = rest.Count > 0 ? rest[0] : "right"; break;
            case "resize":
                if (double.TryParse(Opt("split-ratio"), System.Globalization.CultureInfo.InvariantCulture, out var sr)) cargs["ratio"] = sr;
                if (int.TryParse(Opt("grow-left"), out var gl)) cargs["grow-left"] = gl;
                if (int.TryParse(Opt("grow-right"), out var gr)) cargs["grow-right"] = gr;
                break;
            default:
                Console.Error.WriteLine($"unknown session command '{sub}'"); return 2;
        }
        break;
    case "theme" when sub == "list": cmd = "theme.list"; break;
    case "theme" when sub == "set":
        cmd = "theme.set";
        if (rest.Count == 0) { Console.Error.WriteLine("theme set needs a name"); return 2; }
        cargs["name"] = string.Join(' ', rest);
        break;
    case "keymap" when sub == "reload": cmd = "keymap.reload"; break;
    case "restore" when sub == "clear": cmd = "restore.clear"; break;
    case "sidebar":
        cmd = "sidebar";
        cargs["op"] = sub.Length > 0 ? sub : "toggle";
        break;
    case "quick":
        cmd = "quick";
        cargs["op"] = sub.Length > 0 ? sub : "toggle"; // on|off|toggle; the window's throwaway shell
        break;
    case "font":
        cmd = "font";
        target = DefaultTarget();
        if (sub is not ("inc" or "dec" or "reset")) { Console.Error.WriteLine("usage: agwintermctl font inc|dec|reset [--target ID]"); return 2; }
        cargs["op"] = sub;
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
    default:
        Console.Error.WriteLine($"unknown command '{area} {sub}'"); return 2;
}

var req = new Dictionary<string, object?> { ["cmd"] = cmd };
if (!string.IsNullOrEmpty(target)) req["target"] = target;
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
