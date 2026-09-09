using System.Diagnostics;
using System.Runtime.InteropServices;
using Agwinterm.Core;
using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    // Empty workspaces have no session to select. Keep their placement target separately
    // from the terminal still visible; the next explicit session selection clears it.
    private Workspace? _workspaceTarget;

    private Workspace? CurrentWorkspace()
    {
        lock (_workspaces)
            return _workspaceTarget is { } target && _workspaces.Contains(target) ? target
                : _active is { } active && _workspaces.Contains(active.Ws) ? active.Ws
                : _workspaces.FirstOrDefault();
    }

    private bool SelectWorkspaceCore(Workspace? ws)
    {
        if (ws is null) return false;
        lock (_workspaces) if (!_workspaces.Contains(ws)) return false;
        MruCommit();
        if (_focusedWorkspaceId is not null && _focusedWorkspaceId != ws.Id)
            _focusedWorkspaceId = null; // explicit selection must reveal its destination
        if (ws.Sessions.FirstOrDefault() is { } first) SetActive(first);
        else { _workspaceTarget = ws; RequestRedraw(); SaveState(); }
        EmitEvent("tree");
        return true;
    }

    public string WorkspaceGo(string direction) => InvokeOnUiQueued(() => WorkspaceGoCore(direction));

    private string WorkspaceGoCore(string direction)
    {
        if (!WorkspaceNavigation.TryDirection(direction, out int step))
            return ISessionHost.RefusePrefix + "workspace go requires next or prev";
        List<Workspace> visible;
        lock (_workspaces) visible = _sidebarMode == SidebarMode.Flagged ? new()
            : _workspaces.Where(w => _focusedWorkspaceId is null || w.Id == _focusedWorkspaceId).ToList();
        int index = WorkspaceNavigation.Destination(visible.Count, visible.IndexOf(CurrentWorkspace()!), step);
        if (index < 0) return ISessionHost.RefusePrefix + "no other visible workspace to navigate to";
        var destination = visible[index];
        return SelectWorkspaceCore(destination) ? destination.Id : ISessionHost.RefusePrefix + "workspace not found";
    }

    private void NavigateWorkspace(string direction)
    {
        string result = WorkspaceGoCore(direction);
        if (result.StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal))
            ShowToast(result[ISessionHost.RefusePrefix.Length..]);
        else if (CurrentWorkspace() is { Sessions.Count: 0 } empty)
            ShowToast(empty.Name + " — no sessions");
    }

    private void ToggleWorkspaceCollapse()
    {
        lock (_workspaces) if (CurrentWorkspace() is { } ws) ws.Expanded = !ws.Expanded;
        RequestRedraw(); SaveState(); EmitEvent("tree");
    }

    // One Toolhelp pass per tree read, no subprocess/CIM and never under _workspaces.
    // Windows has no Unix foreground process group: report only a recognized, live root
    // shell with no child processes. A builtin/loop may still be busy; this is not a prompt gate.
    private static Dictionary<Pane, string?> ReadForegroundShells(Pane[] panes)
    {
        var result = new Dictionary<Pane, string?>();
        IntPtr snapshot = CreateToolhelp32Snapshot(0x2, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return result;
            var names = new Dictionary<int, string>();
            var parents = new HashSet<int>();
            do
            {
                names[(int)entry.th32ProcessID] = entry.szExeFile;
                parents.Add((int)entry.th32ParentProcessID);
            } while (Process32NextW(snapshot, ref entry));
            if (Marshal.GetLastWin32Error() != 18 /* ERROR_NO_MORE_FILES */) return result;
            foreach (var pane in panes)
            {
                if (pane.S.HasExited || pane.S.ChildProcessId is not int pid || !names.TryGetValue(pid, out string? name)) continue;
                string? shell = ForegroundShellNames.Recognize(name, parents.Contains(pid), true);
                if (shell is null) continue;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited && !pane.S.HasExited && pane.S.ChildProcessId == pid &&
                        string.Equals(process.ProcessName, shell, StringComparison.OrdinalIgnoreCase)) result[pane] = shell;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        finally { CloseHandle(snapshot); }
        return result;
    }
}
