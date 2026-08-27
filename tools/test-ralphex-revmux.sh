#!/usr/bin/env bash
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
BRIDGE="$ROOT/tools/ralphex-revmux.sh"
TMP="$(mktemp -d)"
trap 'rm -rf -- "$TMP"' EXIT

mkdir -p "$TMP/bin" "$TMP/rounds"

cat > "$TMP/bin/revmux" <<'FAKE_REVMUX'
#!/usr/bin/env bash
set -u
if [ "${1:-}" = "new" ]; then
  [ "${FAKE_NEW_STATUS:-0}" = "0" ] || exit "$FAKE_NEW_STATUS"
  task=""
  run=""
  while [ "$#" -gt 0 ]; do
    case "$1" in
      --task) task="$2"; shift 2 ;;
      --run) run="$2"; shift 2 ;;
      *) shift ;;
    esac
  done
  round="$FAKE_ROOT/rounds/$task/$run"
  mkdir -p "$round/input"
  task_file="$FAKE_ROOT/rounds/$task/task.md"
  if [ ! -f "$task_file" ]; then
    printf '%s\n' '---' 'description:' 'branch:' 'base:' '---' > "$task_file"
  fi
  printf '%s\n' "$task" >> "$FAKE_ROOT/tasks.log"
  printf '%s' "$round/input/scope.md" > "$FAKE_ROOT/last-scope"
  jq -n --arg round_dir "$round" \
        --arg scope "$round/input/scope.md" \
        --arg task_file "$task_file" \
        '{round_dir:$round_dir, scope:$scope, task_file:$task_file}'
  exit 0
fi

cat "$FAKE_REPORT"
[ -z "${FAKE_PROGRESS:-}" ] || printf '%s\n' "$FAKE_PROGRESS" >&2
exit "${FAKE_STATUS:-0}"
FAKE_REVMUX
chmod +x "$TMP/bin/revmux"

cat > "$TMP/clean.json" <<'JSON'
{"sources":{"expected":4,"reported":4,"degraded":[],"agents":[]},"findings":[],"open_questions":[],"pre_existing":[],"immaterial":[]}
JSON

cat > "$TMP/findings.json" <<'JSON'
{"sources":{"expected":4,"reported":4,"degraded":[],"agents":[]},"findings":[{"severity":"major","confidence":98,"file":"src/a.cs","line":7,"title":"Actionable defect","body":"Trigger and consequence.","fix":"Repair the mechanism."}],"open_questions":[],"pre_existing":[{"title":"Do not fix old code"}],"immaterial":[{"title":"Do not polish this"}]}
JSON

cat > "$TMP/degraded.json" <<'JSON'
{"sources":{"expected":4,"reported":3,"degraded":["docs+tests"],"agents":[]},"findings":[],"open_questions":[],"pre_existing":[],"immaterial":[]}
JSON

cat > "$TMP/questions.json" <<'JSON'
{"sources":{"expected":4,"reported":4,"degraded":[],"agents":[]},"findings":[],"open_questions":[{"title":"Choose the wire format"}],"pre_existing":[],"immaterial":[]}
JSON

write_prompt() {
  local path="$1" plan="$2"
  cat > "$path" <<EOF
#   git diff main...HEAD - git diff command appropriate for current iteration
#   Add session metrics - human-readable goal description
#   $plan - path to the plan file being executed
#   .ralphex/progress/run.log - path to progress log with previous review iterations
#   STALE_CONTEXT_MUST_NOT_REACH_SCOPE - previous review context
You are reviewing code changes for: Add session metrics

Run this command to see the changes:
git diff main...HEAD

## Output Format
NO ISSUES FOUND

## Previous Review History
STALE_CONTEXT_MUST_NOT_REACH_SCOPE
EOF
}

run_bridge() {
  local prompt="$1" report="$2" status="${3:-0}"
  PATH="$TMP/bin:$PATH" FAKE_ROOT="$TMP" FAKE_REPORT="$report" FAKE_STATUS="$status" \
    "$BRIDGE" "$prompt"
}

assert_contains() {
  grep -Fq -- "$2" "$1" || { printf 'missing %s in %s\n' "$2" "$1" >&2; exit 1; }
}

assert_not_contains() {
  if grep -Fq -- "$2" "$1"; then
    printf 'unexpected %s in %s\n' "$2" "$1" >&2
    exit 1
  fi
}

write_prompt "$TMP/api-auth.txt" 'docs/plans/api/auth.md'
run_bridge "$TMP/api-auth.txt" "$TMP/clean.json" > "$TMP/clean.out" 2> "$TMP/clean.err"
assert_contains "$TMP/clean.out" 'NO ISSUES FOUND'
assert_contains "$TMP/clean.out" '<<<RALPHEX:CODEX_REVIEW_DONE>>>'
scope="$(cat "$TMP/last-scope")"
assert_contains "$scope" 'git diff main...HEAD'
assert_contains "$scope" 'docs/plans/api/auth.md'
assert_not_contains "$scope" 'Output Format'
assert_not_contains "$scope" 'STALE_CONTEXT_MUST_NOT_REACH_SCOPE'

write_prompt "$TMP/legacy-auth.txt" 'docs/plans/legacy/auth.md'
RALPHEX_REVMUX_DRY_RUN=1 run_bridge "$TMP/legacy-auth.txt" "$TMP/clean.json" \
  > "$TMP/legacy.out" 2> "$TMP/legacy.err"
last_two="$(tail -n 2 "$TMP/tasks.log" | sort -u | wc -l | tr -d ' ')"
[ "$last_two" = "2" ] || { echo 'colliding plan basenames reused one task id' >&2; exit 1; }

existing_plan='docs/plans/20260821-image-frameshm-command.md'
write_prompt "$TMP/relative-plan.txt" "$existing_plan"
RALPHEX_REVMUX_DRY_RUN=1 run_bridge "$TMP/relative-plan.txt" "$TMP/clean.json" \
  > "$TMP/relative.out" 2> "$TMP/relative.err"
relative_task="$(tail -n 1 "$TMP/tasks.log")"
absolute_plan="$(cygpath -w "$ROOT/$existing_plan")"
write_prompt "$TMP/absolute-plan.txt" "$absolute_plan"
RALPHEX_REVMUX_DRY_RUN=1 run_bridge "$TMP/absolute-plan.txt" "$TMP/clean.json" \
  > "$TMP/absolute.out" 2> "$TMP/absolute.err"
absolute_task="$(tail -n 1 "$TMP/tasks.log")"
[ "$relative_task" = "$absolute_task" ] \
  || { echo 'absolute and relative spellings produced different task ids' >&2; exit 1; }

run_bridge "$TMP/api-auth.txt" "$TMP/findings.json" 1 > "$TMP/findings.out" 2> "$TMP/findings.err"
assert_contains "$TMP/findings.out" 'Actionable defect'
assert_contains "$TMP/findings.out" 'Repair the mechanism.'
assert_not_contains "$TMP/findings.out" 'Do not fix old code'
assert_not_contains "$TMP/findings.out" 'Do not polish this'

set +e
run_bridge "$TMP/api-auth.txt" "$TMP/degraded.json" > "$TMP/degraded.out" 2> "$TMP/degraded.err"
degraded_status=$?
set -e
[ "$degraded_status" -ne 0 ] || { echo 'degraded panel returned success' >&2; exit 1; }
assert_not_contains "$TMP/degraded.out" 'NO ISSUES FOUND'
assert_not_contains "$TMP/degraded.out" '<<<RALPHEX:CODEX_REVIEW_DONE>>>'

set +e
run_bridge "$TMP/api-auth.txt" "$TMP/clean.json" 2 > "$TMP/failure.out" 2> "$TMP/failure.err"
failure_status=$?
set -e
[ "$failure_status" = "2" ] || { echo "revmux exit 2 became $failure_status" >&2; exit 1; }

set +e
PATH="$TMP/bin:$PATH" FAKE_ROOT="$TMP" FAKE_REPORT="$TMP/clean.json" FAKE_NEW_STATUS=9 \
  "$BRIDGE" "$TMP/api-auth.txt" > "$TMP/new-failure.out" 2> "$TMP/new-failure.err"
new_failure_status=$?
set -e
[ "$new_failure_status" -ne 0 ] || { echo 'revmux new failure returned success' >&2; exit 1; }

printf '%s\n' 'not-json' > "$TMP/malformed.json"
set +e
run_bridge "$TMP/api-auth.txt" "$TMP/malformed.json" > "$TMP/malformed.out" 2> "$TMP/malformed.err"
malformed_status=$?
set -e
[ "$malformed_status" -ne 0 ] || { echo 'malformed report returned success' >&2; exit 1; }

set +e
run_bridge "$TMP/api-auth.txt" "$TMP/questions.json" > "$TMP/questions.out" 2> "$TMP/questions.err"
question_status=$?
set -e
[ "$question_status" -ne 0 ] || { echo 'open question returned success' >&2; exit 1; }

PATH="$TMP/bin:$PATH" FAKE_ROOT="$TMP" FAKE_REPORT="$TMP/clean.json" \
  RALPHEX_REVMUX_DRY_RUN=1 bash "$ROOT/.ralphex/prompts/custom_review.txt" \
  > "$TMP/wrapper.out" 2> "$TMP/wrapper.err"
assert_contains "$TMP/wrapper.out" 'NO ISSUES FOUND'

echo 'ralphex-revmux bridge tests passed'
