#!/usr/bin/env bash
# Bridge: make revmux serve as ralphex's external review tool.
#
# ralphex calls this with exec.Command(script, promptFile) — no shell, one argument,
# stdout and stderr merged and streamed line by line. It watches the stream for
# <<<RALPHEX:CODEX_REVIEW_DONE>>>, which we must emit exactly once when finished.
#
# The prompt file ralphex hands us is its rendered custom_review.txt: it already
# contains the goal, the git diff command for this iteration, the plan path and the
# progress log. That is very nearly a revmux scope, so we pass it through as one
# rather than reconstructing it.
#
# Wire it up with, in .ralphex/config:
#     external_review_tool = custom
#     custom_review_script = <repo>/tools/ralphex-revmux.cmd
#
# Invoked on Windows through the .cmd sibling, because exec.Command cannot run a
# .sh directly there.

set -uo pipefail

PROMPT_FILE="${1:-}"
DONE_SIGNAL='<<<RALPHEX:CODEX_REVIEW_DONE>>>'

# Always emit the completion signal, on every exit path. Without it ralphex waits
# out its idle timeout on a review that already finished.
finish() { printf '%s\n' "$DONE_SIGNAL"; }
trap finish EXIT

if [ -z "$PROMPT_FILE" ] || [ ! -f "$PROMPT_FILE" ]; then
  echo "ralphex-revmux: no prompt file passed (got '${PROMPT_FILE}')" >&2
  exit 0
fi

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || REPO_ROOT="$PWD"
cd "$REPO_ROOT" || exit 0

command -v revmux >/dev/null 2>&1 || { echo "ralphex-revmux: revmux not on PATH" >&2; exit 0; }

# Profile: comprehensive is the diff-shaped roster (bugs+impl, arch+quality,
# docs+tests on claude plus an adversarial codex peer). Override per repo with
# RALPHEX_REVMUX_PROFILE.
PROFILE="${RALPHEX_REVMUX_PROFILE:-comprehensive}"
MIN_CONFIDENCE="${RALPHEX_REVMUX_MIN_CONFIDENCE:-60}"
# revmux's default hard timeout is 20m per agent attempt, which a wide diff
# exceeds — this repo's own manual round was given 40m for exactly that reason.
# An agent killed mid-read reports nothing, so a review that silently covered
# less looks identical to a clean one.
HARD_TIMEOUT="${RALPHEX_REVMUX_HARD_TIMEOUT:-40m}"

# One revmux task per plan, one run per review iteration. Keeping the task stable
# across iterations is the point: revmux carries earlier rounds into every later
# prompt, so iteration 2 knows what iteration 1 already reported.
PLAN_NAME="$(grep -m1 -oE '[^ /\\]+\.md' "$PROMPT_FILE" 2>/dev/null | head -1 | sed 's/\.md$//')"
[ -z "$PLAN_NAME" ] && PLAN_NAME="review"
TASK="ralphex-${PLAN_NAME}"
RUN="$(date +%Y%m%d-%H%M%S)"

PATHS_JSON="$(revmux new --task "$TASK" --run "$RUN" 2>/dev/null)" || {
  echo "ralphex-revmux: revmux new failed" >&2; exit 0; }

# Take the scope path out of revmux's payload rather than joining it by hand.
pluck() {
  printf '%s' "$PATHS_JSON" \
    | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\(.*\)\".*/\1/p" \
    | head -1 | sed 's/\\\\/\//g'
}
SCOPE="$(pluck scope)"
# revmux allocates a profile file beside the scope. Left unwritten, every
# reviewer runs with this project's conventions empty, and the panel spends
# every round re-reporting the same non-findings.
PROFILE_MD="$(pluck profile)"
[ -z "$SCOPE" ] && { echo "ralphex-revmux: could not read scope path from revmux new" >&2; exit 0; }

{
  echo "# Review scope (handed over by ralphex)"
  echo
  echo "This round was opened automatically by ralphex's external review phase for the"
  echo "task it just implemented. Everything below is ralphex's own review prompt,"
  echo "verbatim — it carries the goal, the exact diff command for this iteration, and"
  echo "the paths to the plan and the progress log."
  echo
  echo "Review the diff it names. The plan file states what the task was supposed to do;"
  echo "a change that works but does not match the plan is a finding worth reporting."
  echo
  echo '---'
  echo
  cat "$PROMPT_FILE"
} > "$SCOPE" 2>/dev/null || { echo "ralphex-revmux: could not write scope" >&2; exit 0; }

# The conventions every reviewer is held to, in revmux's own profile slot. Kept
# apart from the scope because it describes the REPOSITORY rather than this
# diff: what the harness can and cannot do, and what counts as a finding here.
if [ -n "$PROFILE_MD" ]; then
  cat > "$PROFILE_MD" <<'CONVENTIONS' || \
    echo "ralphex-revmux: could not write profile (continuing)" >&2
# Project conventions

agwinterm is a Windows terminal for AI coding agents, built on .NET with a
native layer beside it (`native/`). The lite product moved to its own
repository (agliteterm) at 0.17.5 — `lite/` here holds only the frozen
handover installer, and is not something a change should touch.

## Build and test

```bash
dotnet build Agwinterm.slnx -c Release
dotnet test  Agwinterm.slnx -c Release
```

Both must be clean before a task closes. `native/` builds separately from the
solution (`cargo build --release`, `cargo test`) — a green `Agwinterm.slnx`
says nothing about it, so check it whenever the change reaches the core. The
core's C ABI is declared in two places that MUST agree (`ABI_VERSION` in
`native/agwinterm-core/src/lib.rs`, `RequiredAbi` in
`src/Agwinterm.Core/RustEmulatorCore.cs`); `tools/check-abi.ps1` fails the
build otherwise, and agliteterm pins the same number from its own repository.

## What is worth reporting

- Real defects: wrong behaviour, dropped data, unhandled exceptions, silent
  fallbacks that hide a caller's mistake.
- Anything touching the ConPTY layer, the control pipe, or the pty host
  process boundary — lifetime, encoding and ownership errors there are the
  severest class in this repo, because they corrupt a terminal session rather
  than failing loudly.
- Control-API changes that break the documented request/response shape, since
  agents drive this terminal programmatically and a silent shape change breaks
  them at a distance.
- A change that works but does not match the plan it was built from, and a plan
  or doc left describing a world the code no longer has.
- Missing tests for a code path the change introduced.

## What is not

- Style preferences, naming, comment density. The codebase has settled
  conventions and matching them beats improving them.
- UI behaviour that can only be confirmed by looking at a running terminal —
  there is no automated harness for it, so "add a test" is not actionable.
- Anything the plan file lists as out of scope or deferred, or argues against
  as a recorded decision.
CONVENTIONS
fi

echo "ralphex-revmux: running revmux (profile=$PROFILE, task=$TASK, run=$RUN)" >&2

# Verify the wiring without paying for a panel: RALPHEX_REVMUX_DRY_RUN=1 stops here,
# having proven the argument arrived, the round was created and the scope was written.
if [ "${RALPHEX_REVMUX_DRY_RUN:-0}" = "1" ]; then
  echo "ralphex-revmux: DRY RUN — round created, scope written, revmux not invoked" >&2
  echo "scope: $SCOPE" >&2
  echo "NO ISSUES FOUND"
  exit 0
fi

# revmux exits nonzero when it HAS findings — that is a normal review outcome, not a
# failure, so the status is deliberately not propagated. ralphex reads the findings
# off stdout.
revmux --task "$TASK" --run "$RUN" \
       --profile "$PROFILE" \
       --min-confidence "$MIN_CONFIDENCE" \
       --hard-timeout "$HARD_TIMEOUT" \
       --markdown --no-tui \
       --workdir "$REPO_ROOT" 2>&1 || true

exit 0
