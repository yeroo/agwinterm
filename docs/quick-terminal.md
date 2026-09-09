# Quick terminal

P14 detaches the quick shell from the main window. There is one per app, shared
by all library windows. Ctrl+backtick, the Quick toolbar button, a `quick_terminal`
keymap action, and an optional global chord toggle the same panel. Human summons
dismiss when focus moves elsewhere; hiding keeps the shell and working directory.
Closing the shell discards it; the next summon starts a fresh shell in home.
Closing the last library window disposes the quick shell. It is never restored.

```text
quick-terminal-size = 70
quick-terminal-hotkey = ctrl+alt+backtick
```

Size is 40–90% of both usable monitor dimensions, centered on the monitor under
the pointer. It does not resize normal sessions. The Appearance settings slider
and `config set quick-terminal-size 60` apply live. Out-of-range live writes refuse;
hand-edited numeric values clamp when loading the config.

The global hotkey defaults to empty: no system chord is claimed until enabled.
It accepts Ctrl/Alt (optional Shift) with a letter, digit, F1–F11 or `backtick`.
Win, F12, unmodified and Shift-only chords are refused. Conflicts refuse without
changing the current binding or saved value; startup conflicts warn. Empty disables
and releases the binding. Holding the key does not repeatedly toggle the panel.

`agwintermctl quick on|off|toggle` controls visibility. API shows stay pinned and
do **not** take keyboard focus. A human summon requests focus through normal Windows
policy; the app does not override the OS's foreground restrictions.

```powershell
agwintermctl quick on
agwintermctl session type "echo hello`r" --window quick --target active
agwintermctl session text --window quick --target active
agwintermctl quick off
```

`--window quick` addresses the auxiliary host, not a library window. Its shell
inherits that selector and its unique `quick:...` pane id. Explicit quick pane ids
or the `quick:` prefix route content verbs to the same host from normal windows.
Text/input/selection/images/font/read-only and config verbs work there; workspace,
split, restore and normal-window UI mutations refuse rather than creating a hidden
session tree. `window.state.quickTerminalVisible` reports shared visibility from
any window. `window list` never includes the auxiliary host.

P17 will mirror this shipped contract to lite; the shared conformance floor is
unchanged until that mirror. No clipboard or registry policy is changed by P14.
