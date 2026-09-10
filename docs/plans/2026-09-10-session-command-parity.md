# Session command launch parity

User request: make `session new --command` behave the same in agwinterm and
agliteterm after the 0.18.0 releases. The reported workbench command begins with
PowerShell `Remove-Item`; Full attempts to execute that name, while Lite interprets it.

## Current behavior, verified against both released sources

- Full `Program.ControlHost.NewSession` forwards the command to `CreateSession`.
  `CreatePane` splits it as executable + arguments. The child exiting leaves an exited standalone pane. With `--wait`, it instead invokes `cmd /c <command> & echo. & pause`.
- Lite's `session.new` handler launches `powershell.exe -NoExit -Command <command>`.
  It leaves a PowerShell prompt after the command completes. It currently ignores
  `args.wait`. This is still true in Lite 0.18.0.
- Full also has a separate interactive PowerShell launch path for custom commands;
  overlays have their own launch and exit-status contracts in both products.
- Both products support explicit shell profiles. Lite uses the shared Full CLI,
  with a source revision pinned in its CI workflow.

## Accepted direction (user: "ok, continue")

The recommended shared default is PowerShell code, with an interactive prompt
after the command completes. This matches the reported workbench use case and
Lite's existing behavior. Explicit `exit` still exits that PowerShell process.

Provide an explicit direct-launch option (`--command-mode direct`) for consumers
that require executable + arguments and process-exit session lifetime. Default
`--command-mode powershell` must use identical parsing and lifetime behavior in both
products. Refuse invalid modes before creating a workspace or session.

`--wait` is accepted in PowerShell mode, retaining its interactive prompt, and
refused in direct mode. It never changes the interpreter. No new session wrapper
is added. Standalone exited panes retain output; split-pane exit rules are unchanged.

Implementation and acceptance are now in progress. The user accepted proceeding
with the recommended PowerShell default and explicit direct option on 2026-09-10.

## Acceptance

- Exercise a bare cmdlet, multiple statements, an ordinary executable, an explicit
  PowerShell helper script, paths with spaces, quoted and empty arguments, Unicode,
  and trailing backslashes through both actual launch paths.
- Verify command completion, explicit exit, launch failure, and the agreed `--wait`
  behavior. Test that direct mode does not interpret PowerShell metacharacters.
- Verify the chosen working directory and inherited pane/pipe identity.
- Test invalid mode, missing command, command/profile conflicts, and Lite's bounded
  host fields without creating partial workspace/session state.
- Migrate Full fixtures that depend on direct launching or immediate child exit
  explicitly; retain their original safety and lifetime assertions.
- Update shared CLI help, both shipped agent skills, current documentation, and
  cross-product acceptance coverage. Coordinate Lite's shared CLI pin after the
  Full candidate is available.
- Run Codex-only revmux and relevant isolated/local acceptance tests, using the
  canonical suite token for any GUI/host test. Require exact candidate CI before
  merging. No new release or installation is included in this follow-up.

## Ownership and recovery

Codex owns both new worktrees:

- `C:/Users/boris/source/agwinterm-command-parity`, branch
  `fix/session-command-parity`, base `a9cb53af9c79bf232ac780f8585ed31a182d3d67`.
- `C:/Users/boris/source/agliteterm-command-parity`, branch
  `fix/session-command-parity`, base `6264982426afcd1581461a225979d9217433e006`.

Existing dirty worktrees and release evidence remain untouched. Build and test receipts
live in the owned worktrees under `.revmux/`. Delivery follows the existing PR/CI gates.
