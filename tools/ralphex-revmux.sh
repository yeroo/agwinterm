#!/usr/bin/env bash
# Bridge: make revmux serve as ralphex's external review tool.
#
# Ralphex writes its rendered custom_review.txt to a temporary .txt file and
# launches the configured executable with that path as the only argument. On
# Windows the configured executable is the native launcher, which locates Git
# Bash and executes the project-local prompt; that safe shell wrapper then calls
# this script and passes its own path through.

set -uo pipefail

PROMPT_FILE="${1:-}"
NEW_LOG=""
REPORT_JSON=""
PROGRESS_LOG=""
TASK_META_TMP=""

cleanup() {
  [ -z "$NEW_LOG" ] || rm -f -- "$NEW_LOG"
  [ -z "$REPORT_JSON" ] || rm -f -- "$REPORT_JSON"
  [ -z "$PROGRESS_LOG" ] || rm -f -- "$PROGRESS_LOG"
  [ -z "$TASK_META_TMP" ] || rm -f -- "$TASK_META_TMP"
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
REPO_ROOT_FS="$REPO_ROOT"
if [[ "$REPO_ROOT" =~ ^[A-Za-z]:/ ]] && command -v cygpath >/dev/null 2>&1; then
  REPO_ROOT_FS="$(cygpath -u "$REPO_ROOT")"
fi

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
DEFAULT_BRANCH="$(sed -n 's/^#   \(.*\) - default branch name$/\1/p' "$PROMPT_FILE" | head -1)"

[ -n "$GOAL" ] || fail "could not extract goal from custom-review prompt"
[ -n "$DIFF_INSTRUCTION" ] || fail "could not extract diff command from custom-review prompt"
[ -n "$PLAN_PATH" ] || fail "could not extract plan path from custom-review prompt"
[ -n "$DEFAULT_BRANCH" ] || fail "could not extract review base from custom-review prompt"

normalize_plan_path() {
  local raw unix_path relative
  raw="$(printf '%s' "$1" | tr '\\' '/')"
  unix_path="$raw"
  if [[ "$raw" =~ ^[A-Za-z]:/ ]] && command -v cygpath >/dev/null 2>&1; then
    unix_path="$(cygpath -u "$raw")"
  fi
  if [ -e "$unix_path" ] && command -v realpath >/dev/null 2>&1; then
    relative="$(realpath --relative-to="$REPO_ROOT_FS" "$unix_path" 2>/dev/null || true)"
    if [ -n "$relative" ] && [[ "$relative" != ../* ]]; then
      unix_path="$relative"
    fi
  fi
  printf '%s' "$unix_path" | sed 's#^\./##; s#//*#/#g'
}

PLAN_REL="$(normalize_plan_path "$PLAN_PATH")"
TASK_SUBJECT="$PLAN_REL"
if [[ "$PLAN_REL" == "(no plan file"* ]]; then
  CURRENT_BRANCH="$(git branch --show-current 2>/dev/null || true)"
  if [ -z "$CURRENT_BRANCH" ]; then
    CURRENT_BRANCH="detached-$(git rev-parse --short HEAD 2>/dev/null)" \
      || fail "could not identify planless review checkout"
  fi
  TASK_SUBJECT="branch/$CURRENT_BRANCH"
fi

PLAN_STEM="${TASK_SUBJECT%.md}"
PLAN_SLUG="$(printf '%s' "$PLAN_STEM" \
  | tr '[:upper:]' '[:lower:]' \
  | sed 's/[^a-z0-9]/-/g; s/-\{2,\}/-/g; s/^-//; s/-$//' \
  | cut -c1-48)"
[ -n "$PLAN_SLUG" ] || PLAN_SLUG="review"
PLAN_HASH="$(printf '%s' "$TASK_SUBJECT" | sha256sum | cut -c1-10)"
TASK="ralphex-${PLAN_SLUG}-${PLAN_HASH}"
NEW_LOG="$(mktemp)" || fail "could not allocate revmux-new log"
RUN_STAMP="$(date +%Y%m%d-%H%M%S)"
PATHS_JSON=""
NEW_OK=false
for attempt in 1 2 3; do
  # The timestamp is readable but not unique: two external reviews can start in the same second
  # for the same deterministic task. PID plus Bash's per-process random value separates them;
  # the attempt suffix guarantees that a reported collision gets a fresh name on retry.
  RUN="$RUN_STAMP-$$-${RANDOM:-0}-$attempt"
  # The 2> below opens with O_TRUNC before revmux execs, so only the last attempt's
  # refusal survives to the tail. That is the intent, but it is the redirect doing it:
  # an edit to 2>> would change what gets reported and nothing here would look wrong.
  if PATHS_JSON="$(revmux new --task "$TASK" --run "$RUN" 2>"$NEW_LOG")"; then
    NEW_OK=true
    break
  fi
  # Every failure retries, deliberately, rather than only the ones whose wording reads
  # like a taken name. This loop used to gate the retry on
  # `grep -Eqi 'already exists|duplicate|collision'` and revmux says none of those: its
  # four refusals for a name it will not open are "has already run", "is being written
  # by a run holding it", "was claimed by a run that never came back" and "is reserved".
  # The gate matched nothing, so the retry never ran, and correcting it to those four
  # phrases only moves the next silent breakage to the next wording change. Two of the
  # messages end by advising "open a new round instead", which is what a retry does.
  # A failure a fresh name cannot cure costs two extra sub-second calls; the attempt cap
  # is what bounds this loop, not the wording.
done
if [ "$NEW_OK" != true ]; then
  tail -n 20 "$NEW_LOG" >&2
  fail "revmux new failed after $attempt attempts"
fi

pluck() {
  printf '%s' "$PATHS_JSON" | jq -er --arg key "$1" '.[$key] // empty'
}

SCOPE="$(pluck scope)" || fail "could not read scope path from revmux new"
TASK_FILE="$(pluck task_file)" || fail "could not read task-file path from revmux new"
[ -n "$SCOPE" ] || fail "revmux new returned an empty scope path"
[ -n "$TASK_FILE" ] || fail "revmux new returned an empty task-file path"
printf '%s' "$PATHS_JSON" \
  | jq -e '
      (.created | type) == "array" and
      all(.created[]; type == "string")
    ' >/dev/null \
  || fail "revmux new returned an incompatible paths payload"

{
  printf '%s\n' '- Automated Ralphex external-review round.'
  printf '%s\n' "- Goal: ${GOAL:0:500}"
  printf '%s\n' "- Plan: $PLAN_REL"
  printf '%s\n' '- Review the cumulative implementation diff with:'
  printf '%s\n' '```bash' "$DIFF_INSTRUCTION" '```'
  printf '%s\n' '- Read the changed files and the plan in full.'
  printf '%s\n' '- Ignore generated .ralphex/progress and .revmux/tasks artifacts.'
} > "$SCOPE" || fail "could not write scope"

[ -f "$TASK_FILE" ] || fail "revmux task file does not exist: $TASK_FILE"

BRANCH="$(git branch --show-current 2>/dev/null || true)"
if [ -z "$BRANCH" ]; then
  BRANCH="detached-$(git rev-parse --short HEAD 2>/dev/null)" \
    || fail "could not identify detached review checkout"
fi

yaml_quote() {
  printf '%s' "$1" | jq -Rs .
}

DESCRIPTION_YAML="$(yaml_quote "Ralphex review loop for $PLAN_REL")" \
  || fail "could not encode task description"
BRANCH_YAML="$(yaml_quote "$BRANCH")" || fail "could not encode task branch"
BASE_YAML="$(yaml_quote "$DEFAULT_BRANCH")" || fail "could not encode task base"
TASK_META_TMP="$(mktemp)" || fail "could not allocate task metadata file"
TASK_CR=""
TASK_HEX="$(od -An -tx1 -v "$TASK_FILE" | tr '\n' ' ')" \
  || fail "could not inspect revmux task-file line endings"
if [[ " $TASK_HEX " == *" 0d 0a "* ]]; then
  TASK_CR=$'\r'
fi

# Revmux creates a commented template, but task.md belongs to the user after
# that. Insert only missing active keys inside its front matter; never truncate
# a URL, notes, or other metadata added between review rounds.
if ! DESCRIPTION_LINE="description: $DESCRIPTION_YAML" \
     BRANCH_LINE="branch: $BRANCH_YAML" \
     BASE_LINE="base: $BASE_YAML" \
     TASK_CR="$TASK_CR" \
     awk '
       BEGIN { cr = ENVIRON["TASK_CR"] }
       {
         line = $0
         sub(/\r$/, "", line)
       }
       NR == 1 {
         if (line != "---") exit 41
         in_front = 1
         print line cr
         next
       }
       in_front && line == "---" {
         if (!description) print ENVIRON["DESCRIPTION_LINE"] cr
         if (!branch) print ENVIRON["BRANCH_LINE"] cr
         if (!base) print ENVIRON["BASE_LINE"] cr
         in_front = 0
         closed = 1
         print line cr
         next
       }
       in_front && line ~ /^description:[[:space:]]*/ { description = 1 }
       in_front && line ~ /^branch:[[:space:]]*/ { branch = 1 }
       in_front && line ~ /^base:[[:space:]]*/ { base = 1 }
       { print line cr }
       END { if (!closed) exit 42 }
     ' "$TASK_FILE" > "$TASK_META_TMP"; then
  fail "revmux task file has incompatible front matter: $TASK_FILE"
fi
if ! cmp -s -- "$TASK_FILE" "$TASK_META_TMP"; then
  mv -f -- "$TASK_META_TMP" "$TASK_FILE" \
    || fail "could not initialize revmux task metadata"
  TASK_META_TMP=""
fi

printf 'ralphex-revmux: running revmux (profile=%s, task=%s, run=%s)\n' \
  "$PROFILE" "$TASK" "$RUN" >&2

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

jq -e '
  type == "object" and
  (.sources | type == "object") and
  (.sources.expected | type == "number" and . > 0 and . == floor) and
  (.sources.reported | type == "number" and . > 0 and . == floor) and
  (.sources.reported <= .sources.expected) and
  (.sources.degraded | type == "array") and
  (all(.sources.degraded[]; type == "string")) and
  (.findings | type == "array") and
  (all(.findings[];
    type == "object" and
    (.severity | type == "string") and
    (.confidence | type == "number") and
    (.file | type == "string") and
    (.line | type == "number") and
    (.title | type == "string") and
    (.body | type == "string") and
    (.fix | type == "string"))) and
  (.open_questions | type == "array") and
  (.pre_existing | type == "array") and
  (.immaterial | type == "array")
' "$REPORT_JSON" >/dev/null 2>&1 \
  || fail "revmux returned an incompatible report schema"

IS_DEGRADED="$(jq -er '((.sources.expected != .sources.reported) or (.sources.degraded | length > 0)) | tostring' \
  "$REPORT_JSON")" || fail "could not read revmux source completeness"
if [ "$IS_DEGRADED" = "true" ]; then
  jq -r '"degraded review: expected \(.sources.expected), reported \(.sources.reported), degraded=\((.sources.degraded // []) | join(","))"' \
    "$REPORT_JSON" >&2 || fail "could not render degraded source details"
  fail "refusing to treat a partial panel as a usable review"
fi

OPEN_QUESTION_COUNT="$(jq -er '.open_questions | length' "$REPORT_JSON")" \
  || fail "could not read revmux open questions"
if [ "$OPEN_QUESTION_COUNT" -gt 0 ]; then
  jq -r '.open_questions[] | "open question: " + (.title // .body // tostring)' \
    "$REPORT_JSON" >&2 || fail "could not render revmux open questions"
  fail "review needs a human decision before Ralphex can continue"
fi

FINDING_COUNT="$(jq -er '.findings | length' "$REPORT_JSON")" \
  || fail "could not read revmux findings"
if [ "$FINDING_COUNT" -eq 0 ]; then
  printf '%s\n' 'NO ISSUES FOUND'
else
  jq -r '.findings[] |
    "- [\(.severity), confidence \(.confidence)] \(.file):\(.line) - \(.title)\n" +
    "  \(.body)\n" +
    "  Fix: \(.fix)"' "$REPORT_JSON" \
    || fail "could not render actionable revmux findings"
fi

exit 0
