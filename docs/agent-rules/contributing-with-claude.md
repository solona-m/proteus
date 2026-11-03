# Contributing with Claude Code

If you work on a fork of Proteus with Claude Code, this repo offers the same pipeline its owner uses: plan, critique,
implement, `code-critic` review, then adversarial QA in game. It is **recommended for non-trivial changes**.
Small changes and contributors not using Claude Code don't need it, and nothing here ever blocks a PR. It is a
recommendation, not enforcement.

## Getting the workflow

The committed `.claude/settings.json` declares the ai-workflow marketplace and enables its plugin, so once you trust
the folder, Claude Code offers or loads `/feature` for you. If it doesn't (headless `claude -p`, or you declined), add
it by hand:

```
/plugin marketplace add cunhaax/ai-workflow
/plugin install ai-workflow@ai-workflow
```

The plugin also starts two `npx` MCP servers (playwright, context7); without Node they fail to start, which is harmless.
On Windows the reminder hook needs Git for Windows (Claude Code's own Bash tool needs it too).

Don't run `/init-workflow` here. If it runs, decline its `githooks/`, `scripts/review-ok.sh`,
`scripts/check-hook-status.sh` and settings proposals: this repo has no pre-push gate.

## The recommended path (non-trivial changes)

1. Branch off `test` (AGENTS.md Rule 3).
2. Run `/feature`. It plans, critiques, implements and gets a `code-critic` review of the current commit. Skip its
   step 6 `scripts/review-ok.sh`: this repo has none.
3. Run adversarial QA in game through the QA harness: `docs/agent-rules/adversarial-qa.md`, with `/proteus harness on`
   in a local build. That guide and AGENTS.md Rule 5 name the owner's dev-load path, `E:\repos\Proteus\...`;
   use your own dev-load path instead.
4. Optionally, once every QA finding and Blocker is dispositioned, record it for the current commit. This silences the
   reminder below for that commit:

   ```
   sh .claude/hooks/pr-ready.sh "ran: 2 findings fixed, 1 deferred as an issue"
   sh .claude/hooks/pr-ready.sh "skipped: no UI or API surface"
   sh .claude/hooks/pr-ready.sh "blocked: harness not listening; user chose to proceed"
   ```

   Run it only after a passing review of the current commit: running it is the attestation. A new commit makes it
   stale.
5. Open the PR against `test`, never `main`, and only when your user asks: `gh pr create --base test`. AGENTS.md Rule 4
   still applies; the hook does not replace your user asking.

## What the reminder does

- It never blocks: your Claude's `gh pr create` always runs.
- When the current commit has no `.pr-ready` record (gitignored), Claude gets a short note with the command's result:
  the pipeline is recommended for non-trivial changes, small changes don't need it, and fixes pushed to the branch update
  the PR. Claude relays it to you in one line, and you see a one-line note in the interactive UI. That includes the
  owner, who keeps no record.
- It notices `gh pr create` / `gh pr new` at the start of a command, or after `&&`, `||`, `;`, `|` or `&`, in the first
  line before any quote, written in lower case. Anything else gets no note: it never touches commits, merges or pushes.
- It is not enforcement. A human can open the PR themselves, and the owner's own local merge flow is not affected.

## Changing `.claude/`

`.claude/settings.json` and `.claude/hooks/` run in every agent session in the repo as soon as a branch with them is
checked out. If your PR changes them, say so in its description.
