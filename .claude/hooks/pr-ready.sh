# pr-ready.sh "<qa outcome>": record that the current HEAD is ready for a PR, for .claude/hooks/pr-gate.sh.
#
# NEVER run this without a passing code-critic review of the current HEAD and every adversarial-QA finding and Blocker
# dispositioned: running it is the attestation. The /feature main agent runs it after step 8 and before step 9, in
# place of step 6's scripts/review-ok.sh (this repo has none). A new commit makes the record stale.
#
# The outcome is one of:
#   "ran: <what QA found and how it was dispositioned>"
#   "skipped: <why, e.g. no UI or API surface>"
#   "blocked: <what blocked QA; the user chose to proceed>"
#
# Writes <worktree root>/.pr-ready (gitignored):
#   commit=<40-hex HEAD sha>
#   qa=<outcome>
# A reminder, not enforcement: see docs/agent-rules/contributing-with-claude.md.

fail() {
    printf 'pr-ready: %s\n' "$1" >&2
    exit 1
}

[ "$#" -eq 1 ] || fail 'give exactly one argument: "ran: <...>", "skipped: <...>" or "blocked: <...>"'
qa=$1
case $qa in
    "ran: "[![:space:]]* | "skipped: "[![:space:]]* | "blocked: "[![:space:]]*) ;;
    *) fail "the QA outcome must start with 'ran: ', 'skipped: ' or 'blocked: ' and give a reason" ;;
esac

head=$(git rev-parse --verify -q HEAD 2>/dev/null) || fail "not in a git repository with a commit"
top=$(git rev-parse --show-toplevel 2>/dev/null) || fail "not in a git repository"
[ -n "$head" ] && [ -n "$top" ] || fail "not in a git repository with a commit"

tmp=$top/.pr-ready.tmp.$$
if ! printf 'commit=%s\nqa=%s\n' "$head" "$qa" > "$tmp"; then
    rm -f "$tmp"
    fail "could not write $tmp"
fi
if ! mv -f "$tmp" "$top/.pr-ready"; then
    rm -f "$tmp"
    fail "could not write $top/.pr-ready"
fi
printf 'Recorded PR readiness for %s. If your user asked for a PR: gh pr create --base test\n' "${head%"${head#???????}"}"
