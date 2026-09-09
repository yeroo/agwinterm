# Workspace navigation and small UI controls

`agwintermctl workspace go next|prev` (or `--to next|prev`, alias `previous`)
returns the destination workspace ID. It wraps in visible sidebar order, including
collapsed workspaces. Flagged mode and fewer than two visible workspaces refuse.
`--window ID|active` selects the library window; quick windows and `--target` refuse.

A destination with sessions selects its first session. An empty destination becomes
the current workspace without creating or closing a shell; the previously selected
terminal remains visible. `tree` and `window state` report that current workspace.
Subsequent `session new` uses it only when neither an explicit workspace nor a valid
caller pane supplies a workspace (the CLI sends its `AGWINTERM_SESSION_ID`). Selecting
a session clears this transient empty-workspace target. It is not restore state.

## Keymaps and sidebar

These actions appear in the action palette and can be bound without new default chords:
`next_workspace`, `previous_workspace`, `toggle_workspace_collapse`.
Collapse changes only current workspace expansion, even while the sidebar is hidden.
Quick terminals refuse these library actions.

```text
map f5 | f7 = next_workspace
map f8 = previous_workspace
map f9 = toggle_workspace_collapse
leader = f10
map leader a | b = next_workspace
```

Alternatives also work for `command:Label` targets. Defaults remain; later bindings win.
An invalid or empty alternative rejects the entire map line with a diagnostic.
`leader =` still takes one chord; pipes in command text remain shell syntax.
Fixed gestures such as F6 sidebar focus still precede keymap dispatch.

Dwell over a truncated workspace or session name to reveal a passive, wrapped tooltip
within the client area (maximum 600 DIP width, bounded by available height). It never
takes focus. Leaving, clicking, editing or invalidating that row dismisses it.

## Foreground shell hints

Tree sessions add `foregroundShells`, in pane order, with null for unknown entries.
Known primary/split entries also supply `foregroundShell` / `splitForegroundShell`.
Windows reports only a recognized, live root shell with no observed child process:
cmd, powershell, pwsh, bash, sh, zsh, fish or nu. Failed queries and exited shells are
unknown. This conservative snapshot is not Unix foreground-process-group detection:
a builtin or loop can be busy with no child. **Never treat it as proof of an idle
prompt or permission to send input.** No command arguments are exposed by these fields.

## Cursor settings

Existing Settings/config controls apply live: `cursor-style` accepts bar/beam/line,
block/box or underline/underscore; `cursor-blink` accepts true/false, on/off, yes/no,
1/0; `cursor-blink-ms` takes a positive 32-bit integer. Invalid API writes refuse
without changing the prior value. File parsing remains compatible. Terminal DECSCUSR
cursor overrides still take precedence. Shape changes do not resize terminal cells.

P17 mirrors this contract to lite. Shared conformance is unchanged until then.
