#!/usr/bin/env bash
# Bridge: make revmux serve as ralphex's external review tool.
#
# Ralphex writes its rendered custom_review.txt to a temporary .txt file and
# launches the configured executable with that path as the only argument. On
# Windows the executable is Git Bash; the project-local prompt is itself a safe
# shell wrapper which calls this script and passes its own path through.

set -uo pipefail

PROMPT_FILE="${1:-}"
DONE_SIGNAL='<<<RALPHEX:CODEX_REVIEW_DONE>>>'
REPORT_JSON=""
PROGRESS_LOG=""

cleanup() {
  [ -z "$REPORT_JSON" ] || rm -f -- "$REPORT_JSON"
  [ -z "$PROGRESS_LOG" ] || rm -f -- "$PROGRESS_LOG"
}
trap cleanup EXIT

fail() {
  printf 'ralphex-revmux: %s\n' "$1" >&2
  exit "${2:-2}"
}

[ -n "$PROMPT_FILE" ] && [ -f "$PROMPT_FILE" ] \
  || fail "no readable prompt file passed (got '${PROMPT_FILE}')"

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" \
  || fail "not running inside a git worktree"
cd "$REPO_ROOT" || fail "cannot enter repository root"

command -v revmux >/dev/null 2>&1 || fail "revmux not on PATH" 127
command -v jq >/dev/null 2>&1 || fail "jq not on PATH" 127
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum not on PATH" 127

PROFILE="${RALPHEX_REVMUX_PROFILE:-comprehensive}"
MIN_CONFIDENCE="${RALPHEX_REVMUX_MIN_CONFIDENCE:-60}"
HARD_TIMEOUT="${RALPHEX_REVMUX_HARD_TIMEOUT:-40m}"

# Extract only the stable fields provided by the tracked custom-review template.
# Do not copy the prompt's output contract or prior-review prose into revmux: its
# agents have their own JSON contract and revmux injects earlier rounds itself.
GOAL="$(sed -n 's/^You are reviewing code changes for: //p' "$PROMPT_FILE" | head -1)"
DIFF_INSTRUCTION="$(awk '/^Run this command to see the changes:$/ { getline; print; exit }' "$PROMPT_FILE")"
PLAN_PATH="$(sed -n 's/^#   \(.*\) - path to the plan file being executed$/\1/p' "$PROMPT_FILE" | head -1)"

[ -n "$GOAL" ] || fail "could not extract goal from custom-review prompt"
[ -n "$DIFF_INSTRUCTION" ] || fail "could not extract diff command from custom-review prompt"
[ -n "$PLAN_PATH" ] || fail "could not extract plan path from custom-review prompt"

normalize_plan_path() {
  local raw unix_path relative
  raw="$(printf '%s' "$1" | tr '\\' '/')"
  unix_path="$raw"
  if [[ "$raw" =~ ^[A-Za-z]:/ ]] && command -v cygpath >/dev/null 2>&1; then
    unix_path="$(cygpath -u "$raw")"
  fi
  if [ -e "$unix_path" ] && command -v realpath >/dev/null 2>&1; then
    relative="$(realpath --relative-to="$REPO_ROOT" "$unix_path" 2>/dev/null || true)"
    [ -z "$relative" ] || unix_path="$relative"
  fi
  printf '%s' "$unix_path" | sed 's#^\./##; s#//*#/#g'
}

PLAN_REL="$(normalize_plan_path "$PLAN_PATH")"
PLAN_STEM="${PLAN_REL%.md}"
PLAN_SLUG="$(printf '%s' "$PLAN_STEM" \
  | tr '[:upper:]' '[:lower:]' \
  | sed 's/[^a-z0-9]/-/g; s/-\{2,\}/-/g; s/^-//; s/-$//' \
  | cut -c1-48)"
[ -n "$PLAN_SLUG" ] || PLAN_SLUG="review"
PLAN_HASH="$(printf '%s' "$PLAN_REL" | sha256sum | cut -c1-10)"
TASK="ralphex-${PLAN_SLUG}-${PLAN_HASH}"
RUN="$(date +%Y%m%d-%H%M%S)"

PATHS_JSON="$(revmux new --task "$TASK" --run "$RUN" 2>/dev/null)" \
  || fail "revmux new failed"

pluck() {
  printf '%s' "$PATHS_JSON" | jq -er --arg key "$1" '.[$key] // empty'
}

SCOPE="$(pluck scope 2>/dev/null || true)"
TASK_FILE="$(pluck task_file 2>/dev/null || true)"
[ -n "$SCOPE" ] || fail "could not read scope path from revmux new"

{
  printf '%s\n' '- Automated Ralphex external-review round.'
  printf '%s\n' "- Goal: ${GOAL:0:500}"
  printf '%s\n' "- Plan: $PLAN_REL"
  printf '%s\n' '- Review the cumulative implementation diff with:'
  printf '%s\n' '```bash' "$DIFF_INSTRUCTION" '```'
  printf '%s\n' '- Read the changed files and the plan in full.'
  printf '%s\n' '- Ignore generated .ralphex/progress and .revmux/tasks artifacts.'
} > "$SCOPE" || fail "could not write scope"

if [ -n "$TASK_FILE" ] && grep -q '^description:[[:space:]]*$' "$TASK_FILE" 2>/dev/null; then
  BRANCH="$(git branch --show-current 2>/dev/null || true)"
  DEFAULT_BRANCH="$(git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null | sed 's#^origin/##')"
  [ -n "$DEFAULT_BRANCH" ] || DEFAULT_BRANCH="main"
  {
    printf '%s\n' '---'
    printf 'description: Ralphex review loop for %s\n' "$PLAN_REL"
    printf 'branch: %s\n' "$BRANCH"
    printf 'base: %s\n' "$DEFAULT_BRANCH"
    printf '%s\n' '---' '' "- Plan: $PLAN_REL"
  } > "$TASK_FILE" || fail "could not initialize revmux task metadata"
fi

printf 'ralphex-revmux: running revmux (profile=%s, task=%s, run=%s)\n' \
  "$PROFILE" "$TASK" "$RUN" >&2

if [ "${RALPHEX_REVMUX_DRY_RUN:-0}" = "1" ]; then
  printf 'ralphex-revmux: dry run; scope=%s\n' "$SCOPE" >&2
  printf '%s\n%s\n' 'NO ISSUES FOUND' "$DONE_SIGNAL"
  exit 0
fi

REPORT_JSON="$(mktemp)" || fail "could not allocate report file"
PROGRESS_LOG="$(mktemp)" || fail "could not allocate progress log"

revmux --task "$TASK" --run "$RUN" \
       --profile "$PROFILE" \
       --min-confidence "$MIN_CONFIDENCE" \
       --hard-timeout "$HARD_TIMEOUT" \
       --no-tui \
       --workdir "$REPO_ROOT" >"$REPORT_JSON" 2>"$PROGRESS_LOG"
REVMUX_STATUS=$?

if [ "$REVMUX_STATUS" -ne 0 ] && [ "$REVMUX_STATUS" -ne 1 ]; then
  tail -n 20 "$PROGRESS_LOG" >&2
  fail "revmux failed with exit $REVMUX_STATUS" "$REVMUX_STATUS"
fi

jq -e . "$REPORT_JSON" >/dev/null 2>&1 \
  || fail "revmux returned malformed JSON"

if jq -e '(.sources.expected != .sources.reported) or ((.sources.degraded // []) | length > 0)' \
     "$REPORT_JSON" >/dev/null; then
  jq -r '"degraded review: expected \(.sources.expected), reported \(.sources.reported), degraded=\((.sources.degraded // []) | join(","))"' \
    "$REPORT_JSON" >&2
  fail "refusing to treat a partial panel as a usable review"
fi

if jq -e '(.open_questions // []) | length > 0' "$REPORT_JSON" >/dev/null; then
  jq -r '.open_questions[] | "open question: " + (.title // .body // tostring)' \
    "$REPORT_JSON" >&2
  fail "review needs a human decision before Ralphex can continue"
fi

FINDING_COUNT="$(jq '(.findings // []) | length' "$REPORT_JSON")"
if [ "$FINDING_COUNT" -eq 0 ]; then
  printf '%s\n' 'NO ISSUES FOUND'
else
  jq -r '.findings[] |
    "- [\(.severity), confidence \(.confidence)] \(.file):\(.line) - \(.title)\n" +
    "  \(.body)\n" +
    "  Fix: \(.fix)"' "$REPORT_JSON"
fi

printf '%s\n' "$DONE_SIGNAL"
exit 0
