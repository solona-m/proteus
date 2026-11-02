# pr-gate.sh: Claude Code PreToolUse hook (Bash and PowerShell tools). A RECOMMENDATION; IT NEVER BLOCKS.
#
# When a contributor's Claude runs `gh pr create` and the current HEAD has no review record, this lets the command run
# and hands Claude a short note: for non-trivial changes this repo recommends its review pipeline (/feature with a
# code-critic review, then adversarial QA in game); small changes and contributors not using Claude Code don't need it.
# `sh .claude/hooks/pr-ready.sh "<outcome>"` records a reviewed commit in the gitignored <worktree>/.pr-ready, which
# silences the note for that commit. Guide: docs/agent-rules/contributing-with-claude.md.
#
# When it runs: .claude/settings.json's `if` filters start it for a `gh pr create *` / `gh pr new *` subcommand, and
# Claude Code also starts it for any command too complex to parse (unquoted heredocs and the like). So this script
# decides for itself, and only ever notes a real PR command:
#   - It reads only the unquoted first-line prefix of the command: the raw JSON text up to its first backslash (every
#     JSON escape, so newlines and quotes), quote, backtick, `#`, `$(` or `<<`. Text in quotes, heredocs, comments or on
#     a later line never gets a note.
#   - It splits that prefix on & | ; and notes only a part that starts with `gh pr create` or `gh pr new`.
#   - Every doubt stays silent. A detected PR command with no valid record gets the note: exit 0 with JSON on stdout
#     (hookSpecificOutput.additionalContext for Claude, systemMessage for the user, and no permission decision, so the
#     user's own permission rules decide as usual).
#
# It can never block: exit 2 would, so this script only ever exits 0, and the settings command ends in `|| true` so a
# crash or syntax error can't either.
#
# Record (<worktree root>/.pr-ready, written only by pr-ready.sh):
#   commit=<40-hex HEAD sha>
#   qa=<ran|skipped|blocked>: <reason>
#
# POSIX sh builtins only, plus one `cat` and, on a detected PR command, two `git` calls. No set -e: every exit is explicit.

json=$(cat)

# The value of the first "<key>" whose text after it is optional whitespace, `:`, optional whitespace, then `"` — the
# raw JSON text after that opening quote. Empty (and status 1) when there is none.
value_after() {
    key="\"$1\""
    rest=$json
    while :; do
        case $rest in
            *"$key"*) rest=${rest#*"$key"} ;;
            *) return 1 ;;
        esac
        v=$rest
        while :; do case $v in [[:space:]]*) v=${v#?} ;; *) break ;; esac; done
        case $v in :*) v=${v#:} ;; *) continue ;; esac
        while :; do case $v in [[:space:]]*) v=${v#?} ;; *) break ;; esac; done
        case $v in \"*) value=${v#\"}; return 0 ;; *) continue ;; esac
    done
}

value_after command || exit 0
prefix=$value
prefix=${prefix%%\\*}
prefix=${prefix%%\"*}
prefix=${prefix%%\'*}
prefix=${prefix%%\`*}
prefix=${prefix%%#*}
prefix=${prefix%%\$\(*}
prefix=${prefix%%<<*}

found=
while [ -n "$prefix" ]; do
    seg=${prefix%%[\&\|\;]*}
    case $prefix in
        *[\&\|\;]*) prefix=${prefix#*[\&\|\;]} ;;
        *) prefix= ;;
    esac
    while :; do case $seg in " "*) seg=${seg# } ;; *) break ;; esac; done
    case $seg in
        "gh pr create" | "gh pr create "* | "gh pr new" | "gh pr new "*) found=1; break ;;
    esac
done
[ -n "$found" ] || exit 0

# The note, as one JSON object on stdout. Every dynamic part goes in through %s, never into the format, and holds no
# quote or backslash (the reasons are fixed text, the record's commit is cleaned, HEAD is git's hex).
note() {
    printf '{"systemMessage":"%s","hookSpecificOutput":{"hookEventName":"PreToolUse","additionalContext":"%s\\n%s\\n%s\\n%s\\n%s"}}\n' \
        "Proteus PR reminder: review and QA are recommended for non-trivial changes; small changes don't need it. This never blocks. Guide: docs/agent-rules/contributing-with-claude.md" \
        "pr-gate: recommendation only; this gh pr create was not blocked ($1)." \
        "This repo's review pipeline (/feature with a code-critic review, then adversarial QA in game: docs/agent-rules/adversarial-qa.md) is recommended for non-trivial changes. Small changes and contributors not using Claude Code don't need it." \
        "The PR is already open: if you or your user run the pipeline later, push fixes to this branch to update the PR." \
        "PRs target test; if this one targets main, gh pr edit --base test." \
        "Guide: docs/agent-rules/contributing-with-claude.md. Please tell your user this in one line, and take no other action unless they ask."
    exit 0
}

cwd=
if value_after cwd; then
    raw=${value%%\"*}
    while [ -n "$raw" ]; do
        case $raw in
            *'\\'*) cwd=$cwd${raw%%'\\'*}'\'; raw=${raw#*'\\'} ;;
            *) cwd=$cwd$raw; raw= ;;
        esac
    done
fi
[ -n "$cwd" ] || cwd=$PWD

head=$(git -C "$cwd" rev-parse --verify -q HEAD 2>/dev/null) || note "cannot read HEAD"
top=$(git -C "$cwd" rev-parse --show-toplevel 2>/dev/null) || note "cannot read HEAD"
[ -n "$head" ] && [ -n "$top" ] || note "cannot read HEAD"

record=$top/.pr-ready
[ -f "$record" ] || note "no .pr-ready record"

cr=$(printf '\r')
commit=
qa=
while IFS= read -r line || [ -n "$line" ]; do
    line=${line%"$cr"}
    case $line in
        commit=*) commit=${line#commit=} ;;
        qa=*) qa=${line#qa=} ;;
    esac
done < "$record"

[ -n "$commit" ] || note "record has no commit"
if [ "$commit" != "$head" ]; then
    # The record's first 7 characters (all of it when shorter), each outside an explicit hex list shown as `?`: it is
    # hand-editable text going into JSON. An explicit list, not a range, so the locale can't widen it.
    raw7=${commit%"${commit#???????}"}
    [ -n "$raw7" ] || raw7=$commit
    short=
    while [ -n "$raw7" ]; do
        c=${raw7%"${raw7#?}"}
        raw7=${raw7#?}
        case $c in
            [0123456789abcdefABCDEF]) short=$short$c ;;
            *) short="$short?" ;;
        esac
    done
    note "record is for $short but HEAD is ${head%"${head#???????}"}"
fi
case $qa in
    "ran: "[![:space:]]* | "skipped: "[![:space:]]* | "blocked: "[![:space:]]*) ;;
    *) note "qa outcome missing or malformed" ;;
esac
exit 0
