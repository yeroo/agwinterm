using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>An in-memory <see cref="ISessionHost"/> for testing the control-API JSON contract without the
/// Win32 UI: real workspace→session state + window flags, so tree/window.state/lifecycle verbs can be
/// asserted end-to-end. Peripheral verbs return sensible constants.</summary>
internal sealed class FakeSessionHost : ISessionHost
{
    internal sealed class Sess
    {
        public string Id = "", Name = "";
        public AgentStatus Status = AgentStatus.Idle;
        public bool Flagged, Overlay, ReadOnly;
        public string? AgentResume;
        public int Notifications, PaneCount = 1, FocusedPane, OverlaySize;
        public List<double> Ratios = new() { 1.0 };
    }
    internal sealed class Ws { public string Id = "", Name = ""; public List<Sess> Sessions = new(); }

    internal readonly List<Ws> Workspaces = new();
    internal Ws ActiveWs;
    internal Sess? ActiveSess;
    internal bool SidebarVisible = true, Fullscreen, Maximized, QuickVisible, Broadcast;
    internal readonly Dictionary<string, string> Config = new();
    private readonly TerminalSession _session = new(80, 24);
    private int _idSeq;

    public FakeSessionHost()
    {
        var w = new Ws { Id = "w1", Name = "workspace 1" };
        var s = new Sess { Id = "s1", Name = "session 1" };
        w.Sessions.Add(s);
        Workspaces.Add(w);
        ActiveWs = w; ActiveSess = s;
    }

    private Sess? Find(string? t) =>
        t is null or "active" ? ActiveSess
        : Workspaces.SelectMany(w => w.Sessions).FirstOrDefault(s => s.Id == t || s.Id.StartsWith(t));
    private Ws? FindWs(string? t) =>
        t is null or "active" ? ActiveWs
        : Workspaces.FirstOrDefault(w => w.Id == t || w.Id.StartsWith(t) || string.Equals(w.Name, t, StringComparison.OrdinalIgnoreCase));

    public TerminalSession? Resolve(string? target) => Find(target) is not null ? _session : null;

    public IReadOnlyList<WorkspaceSnapshot> Tree() => Workspaces.Select(w => new WorkspaceSnapshot(
        w.Id, w.Name, ReferenceEquals(w, ActiveWs),
        w.Sessions.Select(s => new SessionSnapshot(s.Id, s.Name, ReferenceEquals(s, ActiveSess), s.Status,
            s.Overlay, s.Notifications, s.Flagged, false, s.FocusedPane, s.PaneCount, false, s.OverlaySize, s.Ratios)).ToList())).ToList();

    public WindowStateSnapshot WindowState() =>
        new(SidebarVisible, Fullscreen, Maximized, QuickVisible, ActiveWs.Name, ActiveSess?.Name);

    public string NewSession(string? name, string? cwd, string? workspace, string? command = null,
        string? workspaceName = null, bool createWorkspace = false, string? profile = null)
    {
        var w = FindWs(workspace) ?? ActiveWs;
        var s = new Sess { Id = "s" + (++_idSeq + 100), Name = string.IsNullOrEmpty(name) ? $"session {w.Sessions.Count + 1}" : name };
        w.Sessions.Add(s); ActiveSess = s; ActiveWs = w;
        return s.Id;
    }

    public bool SelectSession(string target) { var s = Find(target); if (s is null) return false; ActiveSess = s; ActiveWs = Workspaces.First(w => w.Sessions.Contains(s)); return true; }
    public bool CloseSession(string target)
    {
        var s = Find(target); if (s is null) return false;
        var w = Workspaces.First(x => x.Sessions.Contains(s)); w.Sessions.Remove(s);
        if (ReferenceEquals(ActiveSess, s)) ActiveSess = Workspaces.SelectMany(x => x.Sessions).FirstOrDefault();
        return true;
    }
    public string NewWorkspace(string? name) { var w = new Ws { Id = "w" + (++_idSeq + 100), Name = string.IsNullOrEmpty(name) ? $"workspace {Workspaces.Count + 1}" : name }; Workspaces.Add(w); return w.Id; }
    public string ProfilesList() => "default";
    public string ProfilesReload() => "1 profile loaded";
    public bool SetFontSize(string? target, string op) => Find(target) is not null;
    public bool Dashboard(bool close, string? ids, int fontSize) => true;

    public void SessionGo(string dir) { }
    public bool SessionReorder(string? target, string dir) => Find(target) is not null;
    public bool SessionToWorkspace(string? target, string workspace) { var s = Find(target); var w = FindWs(workspace); if (s is null || w is null) return false; Workspaces.First(x => x.Sessions.Contains(s)).Sessions.Remove(s); w.Sessions.Add(s); return true; }
    public bool SessionRename(string? target, string name) { var s = Find(target); if (s is null || string.IsNullOrWhiteSpace(name)) return false; s.Name = name; return true; }
    public bool SessionSeen(string? target) { var s = Find(target); if (s is null) return false; s.Notifications = 0; return true; }
    public string SidebarState() => SidebarVisible ? "visible tree" : "hidden tree";
    public string BroadcastOp(string op) { Broadcast = op switch { "on" => true, "off" => false, "toggle" => !Broadcast, _ => Broadcast }; return Broadcast ? "on" : "off"; }
    public string ReadOnlyOp(string? target, string op) { var s = Find(target); if (s is null) return "off"; s.ReadOnly = op switch { "on" => true, "off" => false, "toggle" => !s.ReadOnly, _ => s.ReadOnly }; return s.ReadOnly ? "on" : "off"; }
    public string SessionOutput(string? target) => "";
    public bool WorkspaceRename(string? target, string name) { var w = FindWs(target); if (w is null || string.IsNullOrWhiteSpace(name)) return false; w.Name = name; return true; }
    public bool WorkspaceDelete(string? target) { var w = FindWs(target); if (w is null || Workspaces.Count <= 1) return false; Workspaces.Remove(w); if (ReferenceEquals(ActiveWs, w)) { ActiveWs = Workspaces[0]; ActiveSess = ActiveWs.Sessions.FirstOrDefault(); } return true; }
    public bool WorkspaceSelect(string? target) { var w = FindWs(target); if (w is null) return false; ActiveWs = w; ActiveSess = w.Sessions.FirstOrDefault(); return true; }
    public bool WorkspaceReorder(string? target, string dir) => FindWs(target) is not null;
    public void Split(string op) { if (ActiveSess is null) return; ActiveSess.PaneCount = op == "off" ? 1 : 2; ActiveSess.Ratios = op == "off" ? new() { 1.0 } : new() { 0.5, 0.5 }; }
    public void FocusPaneDir(string dir) { if (ActiveSess is { PaneCount: > 1 }) ActiveSess.FocusedPane ^= 1; }
    public void ResizeSplit(double? ratio, int growLeft, int growRight) { if (ActiveSess is { PaneCount: > 1 } && ratio is { } r) ActiveSess.Ratios = new() { r, 1 - r }; }
    public IReadOnlyList<string> ThemeList() => new[] { "dark", "light" };
    public bool ThemeSet(string name) => name is "dark" or "light";
    public string KeymapReload() => "reloaded";
    public string RestoreClear() => "cleared";
    public void SidebarOp(string op) { SidebarVisible = op switch { "show" => true, "hide" => false, "toggle" => !SidebarVisible, _ => SidebarVisible }; }
    public string ConfigSet(string key, string value) { Config[key] = value; return "set"; }
    public string ConfigGet(string key) => Config.TryGetValue(key, out var v) ? v : "";
    public string ConfigList() => string.Join("\n", Config.Select(kv => $"{kv.Key} = {kv.Value}"));
    public string SettingsOpen() => "opened";
    public string SessionCopy(string? target) => "";
    public string SelectionAll(string? target) => Find(target) is not null ? "selected" : "no session";
    public string SelectionCopy(string? target) => "";
    public string SelectionClear(string? target) => "cleared";
    public string SelectionFinalize(string? target) => "";
    public string SessionPaste(string? target, string? text) => Find(target) is not null ? "pasted" : "no session";
    public string SessionSearch(string? target, string? query, string? action) => "no matches";
    public bool SessionScratch(string? target, string op) => Find(target) is not null;
    public void Quick(string op) { QuickVisible = op switch { "on" => true, "off" => false, "toggle" => !QuickVisible, _ => QuickVisible }; }
    public string SessionOverlay(string? target, string action, string? command, int sizePercent, bool wait, bool block)
    {
        var s = Find(target); if (s is null) return "no session";
        switch (action)
        {
            case "close": s.Overlay = false; s.OverlaySize = 0; return "closed";
            case "resize": if (!s.Overlay) return "no overlay"; s.OverlaySize = Math.Clamp(sizePercent, 0, 100); return $"resized {s.OverlaySize}%";
            default: if (string.IsNullOrWhiteSpace(command)) return "no command"; s.Overlay = true; s.OverlaySize = Math.Clamp(sizePercent, 0, 100); return s.Id;
        }
    }
    public bool Notify(string? target, string? title, string body) { var s = Find(target); if (s is null) return false; s.Notifications++; return true; }
    public bool SessionFlag(string? target, string op) { if (op == "clear") { foreach (var s in Workspaces.SelectMany(w => w.Sessions)) s.Flagged = false; return true; } var x = Find(target); if (x is null) return false; x.Flagged = op switch { "on" => true, "off" => false, "toggle" => !x.Flagged, _ => x.Flagged }; return true; }
    public bool SessionBind(string? target, string agent) { var s = Find(target); if (s is null) return false; s.AgentResume = string.IsNullOrWhiteSpace(agent) || agent == "none" ? null : agent; return true; }
    public void WorkspaceFocus(string op) { }
    public string SessionBackground(string? target, string action, string? path, int opacity, string? mode) => Find(target) is not null ? "ok" : "no session";
    public string SessionSwitch(string op) => ActiveSess?.Name ?? "";
    public string CommandRun(string nameOrCommand, string? mode) => $"{mode ?? "new"}: {nameOrCommand}";
    public string CommandList() => "";
    public string CommandLeader(string op) => "idle";
    public string OmpSet(string nameOrPath, bool persist) => "ok";
}
