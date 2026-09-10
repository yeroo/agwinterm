# Launching a session command

Both terminals interpret `session new --command` as Windows PowerShell code:

```powershell
agwintermctl session new --name build --command "Write-Output 'starting'; npm test"
```

The command runs after the PowerShell profile loads. When it finishes, the pane has
an interactive PowerShell prompt. `--wait` is accepted and leaves that same prompt
open; it no longer selects `cmd.exe` or a press-any-key wrapper. An explicit
PowerShell `exit` ends the interpreter. A standalone exited pane retains its output.

To launch an executable and its arguments with no added shell, use:

```powershell
agwintermctl session new --name tool --command-mode direct --command 'tool.exe "a b"'
```

Direct mode uses Windows command-line quoting, including escaped quotes, empty
arguments, and trailing backslashes. PowerShell cmdlets, variables, pipelines and
semicolons are not interpreted. Run a shell executable explicitly when you need one.
`--wait` with direct mode is refused: use the default PowerShell mode for an
interactive prompt after completion. An exited standalone direct pane retains output
but has no interpreter accepting further input. Existing split-pane exit rules apply.

The modes are exactly `powershell` (the default) and `direct`. A mode or `--wait`
requires a nonempty command. A command cannot be combined with `--profile`.
Invalid launches are refused before creating a workspace or session. To match Lite's
host protocol, each launch is limited to a 259-byte UTF-8 executable name, 16
arguments, and 2047 UTF-8 bytes per argument. The PowerShell code is one argument.
Use a helper script for longer startup sequences.

For a portable `workbench.cmd`, keep startup logic in a `.ps1` beside the batch file:

```cmd
agwintermctl session new --name Claude --cwd "%~dp0." --command "powershell.exe -NoLogo -NoExit -File .\workbench-pane.ps1 -Agent claude"
```

The trailing dot in `%~dp0.` avoids a trailing-backslash quoting trap. This explicit
PowerShell recipe also works with the already-released 0.18.0 applications, whose
implicit command defaults differ. For a helper filename containing spaces, quote
it as PowerShell code (single quotes inside the command string).

Migration from agwinterm 0.18.0: add `--command-mode direct` to callers that depend
on direct process lifetime or argv parsing. Use a matching updated `agwintermctl`
and terminal; older CLIs do not send the new option. Lite's ordinary PowerShell
command behavior remains the same. These rules apply to `session new`; overlays,
shell profiles, custom commands and restore replay retain their separate contracts.
