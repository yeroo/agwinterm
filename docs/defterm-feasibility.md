# T2-13 — Default Terminal Application delegation ("defterm"): feasibility & plan

**Goal:** let agwinterm register as the Windows 11 *Default Terminal Application*, so any console
program launched anywhere on the system (double-clicking a `.bat`, `cmd` from Win+R, a build tool
spawning `python`) opens inside agwinterm instead of conhost.

**Verdict:** achievable in principle (it's a documented, open-source contract — Windows Terminal does
exactly this), but it is a genuine **multi-day, higher-risk** effort with **system-wide, hard-to-roll-back
testing**. Recommend doing it as a dedicated, staged effort with the ability to test on a throwaway/VM.

## How Windows' default-terminal handoff works

1. **Delegation registry.** `HKCU\Console\%%Startup` holds two CLSIDs: `DelegationConsole` and
   `DelegationTerminal`. Default/empty ⇒ classic conhost. WT sets these to *its* CLSIDs.
2. **Console side (`DelegationConsole`).** This is an **OpenConsole.exe** COM server — Microsoft's
   conhost, shipped *inside* the terminal package. When a console app starts, the OS launches this
   OpenConsole in "handoff" mode instead of the in-box conhost.
3. **Terminal side (`DelegationTerminal`).** OpenConsole `CoCreateInstance`s this CLSID and calls
   **`ITerminalHandoff(2/3)::EstablishPtyHandoff`**, passing the **in/out VT pipe handles**, a **signal
   pipe** (resize/reparent), and the **client `PROCESS_INFORMATION`**. The terminal creates a
   window/tab bound to those handles.

The interfaces live in WT's `ITerminalHandoff.idl` — **not** in the Windows SDK; we declare them from
the IDL ourselves.

## What agwinterm needs (staged)

**Stage A — Attach to an external PTY (safe, testable in isolation, no system changes).**
Add `TerminalSession.AttachAsync(SafeFileHandle input, SafeFileHandle output, SafeFileHandle signal,
int clientPid)` that wraps the given handles as streams and runs the existing `InteractivePumpAsync` +
write path — *exactly* what `DeElevatedPty` already does with FileStream-over-SafeFileHandle. Resize
writes to the signal pipe (the ConPTY resize packet) instead of `ResizePseudoConsole`.
- **Testable now:** create a pseudoconsole in a helper, hand its client-side handles to `AttachAsync`,
  confirm agwinterm renders/echoes. No registry, no COM, fully reversible.

**Stage B — Out-of-proc COM server implementing `ITerminalHandoff3`.**
- Declare the IDL interfaces in C# (ComWrappers / `[GeneratedComInterface]` in .NET 8+).
- Register a `LocalServer32` CLSID (agwinterm's own GUID). On `EstablishPtyHandoff`, marshal the
  handles and call Stage A's `AttachAsync` on the UI thread (new tab, or route to a running instance).
- **Risk:** .NET out-of-proc COM activation + handle marshalling across the process boundary is fiddly
  and sparsely documented for this contract; expect iteration.

**Stage C — "Set as default terminal" + packaging.**
- Ship an **OpenConsole.exe** as the `DelegationConsole` (WT bundles Microsoft's; licensing/version
  pinning to sort out), or investigate reusing the in-box one.
- An in-app / installer action that writes `DelegationConsole` + `DelegationTerminal` to
  `HKCU\Console\%%Startup`, plus a "restore default (conhost)" undo.
- **Risk:** this changes *every* console launch on the machine. A bug means broken terminals system-wide
  until reverted — must be test on a VM or with a one-click revert wired up first.

## Open questions to resolve during implementation
- Exact `ITerminalHandoff3` vtable/signature (pull from the current WT `ITerminalHandoff.idl`).
- Whether we must bundle OpenConsole.exe or can delegate the console side to an existing one.
- Single-instance routing: does a handoff open a tab in the running agwinterm, or a fresh window?
- Elevated console apps (a handoff from a High-IL client) — integrity interplay with our windows.

## Recommendation
Green-light Stage A now (it's safe, reusable, and de-risks the architecture). Treat B+C as a dedicated
follow-up with VM testing. Do **not** flip the system default terminal until Stage C has a verified
one-click revert.
