# pr-gate.sh: Claude Code PreToolUse hook (Bash and PowerShell tools). A REMINDER, NOT ENFORCEMENT.
#
# Before a contributor's Claude runs `gh pr create`, it asks for the /feature pipeline to be finished: a passing
# code-critic review of the current HEAD and an adversarial-QA outcome, recorded in the gitignored <worktree>/.pr-ready
# by `sh .claude/hooks/pr-ready.sh "<outcome>"`. Anyone can bypass it (delete it, write the file, use the web UI); it
# exists so the pipeline is the default path. Guide: docs/agent-rules/contributing-with-claude.md.
#
# When it runs: .claude/settings.json's `if` filters start it for a `gh pr create *` / `gh pr new *` subcommand, and
# Claude Code also starts it for any command too complex to parse (unquoted heredocs and the like). So this script must
# decide for itself, and it must never block anything but a real PR command:
#   - It reads only the unquoted first-line prefix of the command: the raw JSON text up to its first backslash (every
#     JSON escape, so newlines and quotes), quote, backtick, `#`, `$(` or `<<`. Text in quotes, heredocs, comments or on
#     a later line can never block.
#   - It splits that prefix on & | ; and blocks only a part that starts with `gh pr create` or `gh pr new`.
#   - Every doubt fails open (exit 0). A detected PR command with no valid record fails closed (exit 2).
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

block() {
    {
        printf 'pr-gate: blocked gh pr create: %s\n' "$1"
        printf '%s\n' \
            "This repo asks for its review pipeline before a PR (a reminder, not enforcement):" \
            "  1. /feature (ai-workflow), with code-critic passing on the current commit; skip its scripts/review-ok.sh step, this repo has none." \
            "  2. Adversarial QA, in game through the harness: docs/agent-rules/adversarial-qa.md." \
            "  3. Record it: sh .claude/hooks/pr-ready.sh \"ran: <what QA found>\" (or \"skipped: <why>\" / \"blocked: <why>\")." \
            "  4. Then, only if your user asked for a PR: gh pr create --base test" \
            "Guide: docs/agent-rules/contributing-with-claude.md." \
            "No /feature? /plugin marketplace add cunhaax/ai-workflow, then /plugin install ai-workflow@ai-workflow." \
            "Your user can always open the PR themselves."
    } >&2
    exit 2
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

head=$(git -C "$cwd" rev-parse --verify -q HEAD 2>/dev/null) || block "cannot read HEAD"
top=$(git -C "$cwd" rev-parse --show-toplevel 2>/dev/null) || block "cannot read HEAD"
[ -n "$head" ] && [ -n "$top" ] || block "cannot read HEAD"

record=$top/.pr-ready
[ -f "$record" ] || block "no .pr-ready record"

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

[ -n "$commit" ] || block "record has no commit"
if [ "$commit" != "$head" ]; then
    short=${commit%"${commit#???????}"}
    [ -n "$short" ] || short=$commit
    block "record is for $short but HEAD is ${head%"${head#???????}"}"
fi
case $qa in
    "ran: "[![:space:]]* | "skipped: "[![:space:]]* | "blocked: "[![:space:]]*) ;;
    *) block "qa outcome missing or malformed" ;;
esac
exit 0
