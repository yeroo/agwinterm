# Testing defterm (T2-13) safely

**Windows Sandbox is NOT available on this machine** (Windows 11 Home — Sandbox/Hyper-V are
Pro/Enterprise-only and can't be enabled on Home). So the plan is host testing with a **bulletproof,
one-command revert**, which is safe because the "default terminal application" is a *per-user*,
*no-admin*, instantly-reversible `HKCU\Console\%%Startup` setting.

## The safety net (built first, on purpose)

`tools/defterm-restore-conhost.ps1` resets the default terminal to "Let Windows decide" (= conhost).
Run it any time a defterm experiment misbehaves — it works from **Win+R** (`powershell -File <path>`)
or any already-open shell, needs no admin, and takes effect for the *next* console you open.

Baseline on this machine (captured before any changes):
`DelegationConsole = DelegationTerminal = {00000000-0000-0000-0000-000000000000}` (zero GUID).

## Why host testing is acceptable here

- The delegation lives in `HKCU` — no admin, no system-wide/registry-of-death risk.
- Worst case (broken handoff): newly-launched console apps fail to open **until you run the revert**.
  Existing windows keep working, and Win+R → the revert script fixes it immediately.
- Nothing is installed system-wide; no OpenConsole gets wired in until we explicitly do so in Stage C.

## Test loop (once Stage B/C exist)

1. `tools/defterm-restore-conhost.ps1` — confirm baseline (a plain `cmd` opens in conhost).
2. `tools/defterm-register.ps1` — register agwinterm as the default terminal (writes agwinterm's
   DelegationTerminal CLSID; registers the out-of-proc COM server).
3. Launch a console app (e.g. `cmd` from Win+R, double-click a `.bat`) → it should open **inside a new
   agwinterm session/window** via the handoff.
4. Type / run something → confirm I/O and resize work.
5. `tools/defterm-restore-conhost.ps1` — revert. Confirm console apps open in conhost again.

## Fuller isolation (optional, if desired)

For zero host risk, run the loop inside a free VM instead: **VirtualBox** or **VMware Workstation
Player** with a Windows 11 image (Microsoft ships time-limited eval VM images). Slower to set up, but
completely isolated. Not required — the host + revert loop above is safe for this per-user setting.
