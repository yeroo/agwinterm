# agliteterm: splitting lite into its own product

## Overview

agwinterm-lite becomes **agliteterm** — its own product, in its own repository, with its own name,
version line, release cadence, issue tracker and package identities. It keeps agwinterm ecosystem
compatibility as a **promise**: `agwintermctl`, the agent skill and the status hooks go on working
against it unchanged.

This is a product decision (Boris, 2026-08-17), not a technical one. The engineering exists to serve
it, and the plan is written that way: the product outcome is fixed, and the tasks below are the cost
of getting there safely.

Two consequences follow directly from the three choices, and they are the real work:

1. **The C ABI becomes a cross-repo contract.** Today `lite/src/main.cpp` links `agwinterm_core.dll`
   and spawns `agwinterm-ptyhost.exe`, both built from `native/` in the same tree, so an ABI change
   lands atomically across Rust, C# and C++. Split, that stops being possible — and the ABI is
   still moving (`kRequiredAbi = 15`, up from 8 in July). The saving grace is that lite already
   *checks*: `agwcore_abi_version()` is called before anything else and `fatal()`s on mismatch, so a
   mismatch is a loud refusal, not the memory corruption an unchecked `FfiEmuInfo` write would be.
   The contract must be **versioned and published**, not just checked.
2. **Existing installs must be handed over, or they are stranded.** Every lite install polls
   `https://api.github.com/repos/yeroo/agwinterm/releases/latest` for an `agwinterm-lite-setup-*`
   asset (`lite/src/main.cpp` ~3295). The moment agwinterm stops publishing that asset, those
   installs stop updating **silently** — no error, no notice, no path to agliteterm. The handover
   release is not optional and must ship *before* the name change.

## Identity — the single source of truth (Task 1, settled 2026-08-17)

Every later task renames against THIS table and nothing else. All three names were checked free on
2026-08-17: `yeroo/agliteterm` does not exist, `microsoft/winget-pkgs` has zero `agliteterm`
manifests (yeroo publishes only `agwinterm`), and `choco search agliteterm` returns nothing.

| surface | today | agliteterm |
|---|---|---|
| product name | agwinterm lite | **agliteterm** |
| executable | `agwinterm-lite.exe` | `agliteterm.exe` |
| installer AppId | `{B8F4A2D3-6C1E-4F7B-9D32-4A1E8B5C9F26}` | `{E0ACBA4E-AAD3-4689-9234-66D3CD207A6A}` |
| setup filename | `agwinterm-lite-setup-<ver>.exe` | `agliteterm-setup-<ver>.exe` |
| install dir | `%LOCALAPPDATA%\Programs\agwinterm-lite` | `%LOCALAPPDATA%\Programs\agliteterm` |
| `kAppId` / control pipe | `agwinterm-lite` | `agliteterm` (+ old name as alias, Task 3) |
| pty-host pipe | `agwinterm-lite-ptyhost` | `agliteterm-ptyhost` |
| settings key | `HKCU\Software\agwinterm-lite` | `HKCU\Software\agliteterm` |
| instances key | `HKCU\Software\agwinterm-lite\Instances` | `HKCU\Software\agliteterm\Instances` |
| state dir | `%LOCALAPPDATA%\agwinterm-lite` | `%LOCALAPPDATA%\agliteterm` |
| state files | `sessions.tsv`, `sessions-<inst>.tsv`, `.bak` | names unchanged, new directory |
| log files | `lite.log`, `lite-<inst>.log` | `agliteterm.log`, `agliteterm-<inst>.log` |
| update feed | `api.github.com/repos/yeroo/agwinterm/releases/latest` | `.../repos/yeroo/agliteterm/releases/latest` |
| update asset prefix | `agwinterm-lite-setup-` | `agliteterm-setup-` |
| winget id | *(none — `yeroo.agwinterm` is the full app)* | `yeroo.agliteterm` |
| chocolatey id | *(none — `agwinterm` is the full app)* | `agliteterm` |
| repository | `yeroo/agwinterm` (`lite/`) | `yeroo/agliteterm` |
| version line | shared; both `.iss` bump together | **continues from 0.17.x**, independently |

**Deliberate exceptions to the rename** — these are the compatibility promise, and changing them
would break the agent tooling the product is being kept compatible with:

- `AGWINTERM`, `AGWINTERM_ENABLED`, `AGWINTERM_PIPE`, `AGWINTERM_SESSION_ID`, `AGWINTERM_PANE_ID`
  (main.cpp:1399-1404) keep their names. The agent skill, the status hooks and `agwintermctl` all
  read them; renaming would break every integration on day one.
- `TERM_PROGRAM` (main.cpp:1405) **does** change, `agwinterm-lite` -> `agliteterm`: it names the
  terminal, and a renamed product should say what it is. Anything detecting the old string sees a
  new terminal rather than a broken one. ⚠️ Flagged because prompt engines and scripts sometimes
  branch on it — the READMEs must mention it.

**Version consequence (Boris, 2026-08-17):** the line continues from 0.17.x rather than restarting
at 1.0.0. The handover build in Task 4 must therefore carry a version *above* the last
`agwinterm-lite` release, and agliteterm's first release must be above that again — a version that
appears to go backwards is never offered as an update.

**Chocolatey lead time:** `agliteterm` will be a brand-new package, so its first version goes through
first-submission moderation. agwinterm's took from 2026-07-09 (v0.10.0) to 2026-08-11 to clear.
Submit early; the rename does not depend on it.

## Context (from discovery)

Everything that carries the `agwinterm-lite` identity today:

| what | where | migration risk |
|---|---|---|
| control pipe + pty-host pipe | `kAppId = L"agwinterm-lite"` (main.cpp:774), `<kAppId>-ptyhost` (1111) | scripts using the literal name break |
| instance registry | `HKCU\Software\agwinterm-lite\Instances` (131) | multi-window discovery |
| settings | `HKCU\Software\agwinterm-lite` — FontFace/FontH/FontW/CustomColors (1785-1808) | **font + colours lost on rename** |
| state + logs | `%LOCALAPPDATA%\agwinterm-lite\` — `sessions*.tsv`, `lite*.log`, `updates\` | **sessions lost on rename** |
| install dir | `%LOCALAPPDATA%\Programs\agwinterm-lite` (3150) — also the self-update channel gate | updater refuses to apply outside it |
| update feed | `api.github.com/repos/yeroo/agwinterm/releases/latest` (3295) | **strands every existing install** |
| installer | AppId `{B8F4A2D3-6C1E-4F7B-9D32-4A1E8B5C9F26}`, `agwinterm-lite-setup-<ver>.exe` | a new AppId installs alongside rather than upgrading |
| native deps | `lite/build.ps1` copies `agwinterm_core.dll` + `agwinterm-ptyhost.exe` from `native/target/release` | becomes a cross-repo dependency |
| packaging | none of its own — winget `yeroo.agwinterm` and choco `agwinterm` are the MAIN app | new IDs to reserve |

Not carried: lite has no winget/choco package, no version of its own (both `.iss` files bump
together), and no release of its own (it rides the agwinterm tag).

- **Prerequisite reading**: `docs/plans/2026-07-31-lite-restore-scenario-matrix.md` — the state file
  and its `.bak` generation are what a careless rename would strand.

## Development Approach

- **Testing approach**: Regular (code first, then tests), consistent with the two lite plans.
- Complete each task fully before moving to the next
- Make small, focused changes
- **CRITICAL: every task MUST include new/updated tests** for code changes in that task
  - migration tasks are tested by *seeding old-name state and asserting it survives*, which is the
    only thing that proves a rename didn't strand users
- **CRITICAL: all tests must pass before starting next task** - no exceptions
- **CRITICAL: update this plan file when scope changes during implementation**
- Maintain backward compatibility: an existing 0.17.x lite install must end up on agliteterm with
  its sessions, fonts and colours intact, without the user re-doing anything

## Testing Strategy

- **lite checks** (`lite/test/run-all.ps1`, 29-cell restore matrix + log/diagnose suites) move with
  the code and must stay green throughout — they are the regression net for the rename.
- **New**: a migration suite that seeds `agwinterm-lite`-named state/registry/install and asserts
  agliteterm adopts it.
- **New**: a control-API conformance suite, run in BOTH repos, that makes the compatibility promise
  testable rather than aspirational.
- House rules unchanged: sandbox instances only, posted messages never global input, `PrintWindow`
  never screen capture.

## Progress Tracking

- Mark completed items with `[x]` immediately when done
- Add newly discovered tasks with ➕ prefix
- Document issues/blockers with ⚠️ prefix
- Keep this plan in sync with actual work done

## What Goes Where

- **Implementation Steps**: everything achievable in these repos
- **Post-Completion**: the product surfaces only Boris can do (name reservation, repo creation,
  store listings, announcement)

## Implementation Steps

### Task 1: Pin the identity and reserve the names
- [x] confirm the product name and exe name (`agliteterm` / `agliteterm.exe`) and the new installer
      AppId GUID — a NEW GUID installs alongside the old one, which is what we want for a rename
      with migration, not an in-place upgrade
- [x] check availability of the winget id (`yeroo.agliteterm`) and the chocolatey package id
      (`agliteterm`), and note the moderation lead time for a brand-new choco package (first version
      of a new package sits in moderation — agwinterm took from July to August)
- [x] write the identity table into the plan as the single source of truth for every later task
- [x] no code yet — this task exists so the rename is done once, not discovered piecemeal


✅ **Task 1 complete (2026-08-17).** Names verified free, AppId minted, identity table above
is now the single source of truth. Reserving the winget/chocolatey ids is a Post-Completion
item (they can only be claimed by publishing), and does not block Task 2.

### Task 2: Make the native contract explicit and versioned
- [x] verify `agwcore_abi_version()` is checked before ANY call that writes through a caller pointer
      (`agwcore_emu_info` fills a whole `FfiEmuInfo`; a mismatch there is memory corruption, not a
      clean failure) — today's check runs at load, confirm nothing precedes it
- [x] publish `agwinterm_core.dll`, `agwinterm-ptyhost.exe` and the C header as versioned release
      artifacts of the agwinterm repo, tagged with the ABI version, so agliteterm consumes a pinned
      build rather than a sibling directory
- [x] record the ABI version in the artifact name or a manifest so a wrong pairing is obvious before
      it is loaded
- [x] write a check that fails the build when `kRequiredAbi` and the consumed artifact disagree
- [x] add the error case: a deliberately mismatched pair must fail loudly and identifiably
- [x] run the checks - must pass before task 3


✅ **Task 2 complete (2026-08-18).**
- Gate verified: `loadCore()` resolves the exports, null-checks them, then compares
  `core_abi()` against `kRequiredAbi` — and nothing calls the core before it. The two headless
  paths that return early (`--bench-agbf`, `--diagnose`) touch no core function, confirmed by
  inspection, so `agwcore_emu_info` can never fill an `FfiEmuInfo` through an unvalidated pairing.
- `tools/check-abi.ps1` compares all THREE declarations (Rust `ABI_VERSION`, C# `RequiredAbi`,
  lite `kRequiredAbi`) and fails the build on drift; wired into ci.yml ahead of the Rust build.
  Verified both ways: exit 1 with lite deliberately bumped to 16, exit 0 restored.
- The release now publishes `agwinterm_core-abi<N>.dll`, `agwinterm-ptyhost-abi<N>.exe` and
  `agwinterm-core-abi.json`, attested with everything else. The ABI is in the FILENAME, so a wrong
  pin is visible before download, and the manifest lets a consumer assert the pairing before load.
- lite's mismatch `fatal()` now names both versions instead of a hardcoded "need v15" that went
  stale on every bump and never said what the dll actually reported.

⚠️ Deliberately NOT done here: moving `native/` to its own repository. agliteterm consumes the
published artifacts; revisit only if the pairing proves painful in practice.

### Task 3: Rename inside lite, WITH migration
- [ ] rename the identity strings: `kAppId`, `kInstKey`, settings key, state dir, install dir, exe,
      installer AppId, setup filename, window class/title
- [ ] **migrate on first run**: if the new state dir is absent and `%LOCALAPPDATA%\agwinterm-lite`
      exists, copy sessions/logs/`.bak` across; same for `HKCU\Software\agwinterm-lite` settings.
      Copy, don't move — a user who rolls back must still find their old install working
- [ ] keep serving the OLD control-pipe name as an alias for a deprecation period, so
      `agwintermctl --pipe agwinterm-lite` and any existing scripts keep working; log when the alias
      is used so we can tell when it's safe to drop
- [ ] migration test: seed old-name state (sessions, fonts, colours), launch, assert everything is
      adopted and the sessions restore
- [ ] error case: old state present AND new state present -> new wins, old is left untouched
- [ ] run `lite/test/run-all.ps1` - all 29 matrix cells must still pass before task 4

### Task 4: Ship the handover release (BEFORE the repo move)
- [ ] final `agwinterm-lite` build whose updater points at the agliteterm release feed, so existing
      installs discover the successor by the mechanism they already trust
- [ ] make the update prompt say what is happening — a rename, not a routine update
- [ ] test it end to end with the existing seams (`AGWINTERM_UPDATE_API` local feed,
      `AGWINTERM_VERSION_OVERRIDE`): an install of today's 0.17.x must find, download and apply the
      agliteterm build
- [ ] error case: the successor feed unreachable -> unchanged behaviour, no nagging, no broken state
- [ ] run the checks - must pass before task 5

### Task 5: Move the code to its own repository
- [ ] extract `lite/` with its history (`git filter-repo --subdirectory-filter lite`), keeping the
      test suite, assets and the plans that document its bugs
- [ ] stand up CI mirroring today's: build with cl.exe, run `test/run-all.ps1` (which agwinterm's CI
      never did — the lite checks only ever ran locally, so this is a net gain)
- [ ] port the release workflow: installer, attestation, winget + chocolatey publish, reusing the
      `publish.ps1` pattern from #187 and the fork-sync from #188
- [ ] wire the pinned native artifacts from Task 2
- [ ] verify a full release dry-run produces an installable build
- [ ] run the suite in the new repo - must pass before task 6

### Task 6: Make the compatibility promise testable
- [ ] write a control-API conformance suite: for each of the 38 verbs, the request and the expected
      response shape, driven by `agwintermctl` against a sandbox instance
- [ ] run it in BOTH repos' CI — in agwinterm against the full app, in agliteterm against lite — so
      "same control API" is enforced rather than asserted
- [ ] include the env contract (`AGWINTERM_*` injected into sessions) that the agent skill and hooks
      depend on
- [ ] error case: a verb removed or reshaped on one side fails the other side's CI
- [ ] run both suites - must pass before task 7

### Task 7: Documentation and cross-links
- [ ] agliteterm README: what it is, who it's for (6-8 GB, ~10-year-old machines), and its
      relationship to agwinterm — credited, compatible, independent
- [ ] agwinterm README: point at agliteterm, drop the lite section
- [ ] document the migration for users on 0.17.x and the pipe-name alias with its removal timeline
- [ ] update the memories: `feature-agwin-bitmap-fonts`, `agwinterm-build-and-test-gotchas` and
      `distribution-and-code-signing` all describe lite as part of this repo

### Task 8: Verify acceptance criteria
- [ ] an existing 0.17.x lite install updates to agliteterm and keeps sessions, fonts and colours
- [ ] a fresh install works with no agwinterm present at all
- [ ] `agwintermctl`, the agent skill and the hooks work against agliteterm unchanged
- [ ] both repos' CI green; the ABI mismatch check fires when deliberately mispaired
- [ ] agwinterm releases no longer ship a lite asset, and nothing in its tree references `lite/`

## Technical Details

- **Migration direction**: copy old -> new, never move. Rollback must stay possible until the
  handover is proven in the field.
- **Pipe alias**: serve both `agwinterm-lite` and `agliteterm` control pipes during deprecation.
  `AGWINTERM_PIPE` injected into sessions gets the NEW name, so the skill and hooks follow
  automatically; only hardcoded scripts need the alias.
- **ABI pairing**: `kRequiredAbi` is the gate that already exists. The new work is making the
  *artifact* carry its ABI version so a wrong pair is caught at build time, not at `fatal()` time.
- **What does NOT move**: `native/` stays in agwinterm and is consumed by both. Moving it to a third
  repo is a bigger change than this plan needs; revisit if agliteterm ever outgrows the pairing.

## Post-Completion

*Only Boris can do these.*

- Create the `agliteterm` repository and push the extracted history
- Reserve `yeroo.agliteterm` (winget) and `agliteterm` (chocolatey); expect the choco first-version
  moderation wait that agwinterm just went through
- Decide the announcement: existing lite users meet the rename through the in-app update prompt from
  Task 4, everyone else through the two READMEs
- Decide whether agliteterm gets its own icon or keeps the VGA black+cyan one it has now

**Decided 2026-08-17:** the version line continues from 0.17.x (see the identity table). What is
still open is the icon — whether agliteterm keeps the VGA black+cyan mark it has now or gets its
own — which affects nothing before Task 5.
