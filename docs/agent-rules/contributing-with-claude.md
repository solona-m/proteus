# Contributing with Claude Code

If you work on a fork of Proteus with Claude Code, this repo sets you up to use the same pipeline its owner does:
plan, critique, implement, `code-critic` review, then adversarial QA in game, before any pull request. A small hook
reminds your Claude of this before it opens a PR. It is **a reminder, not enforcement**: anyone can bypass it.

## Getting the workflow

The committed `.claude/settings.json` declares the ai-workflow marketplace and enables its plugin, so once you trust
the folder, Claude Code offers or loads `/feature` for you. If it doesn't (headless `claude -p`, or you declined), add
it by hand:

```
/plugin marketplace add cunhaax/ai-workflow
/plugin install ai-workflow@ai-workflow
```

The plugin also starts two `npx` MCP servers (playwright, context7); without Node they fail to start, which is harmless.
On Windows the hook needs Git for Windows (Claude Code's own Bash tool needs it too).

Don't run `/init-workflow` here. If it runs, decline its `githooks/`, `scripts/review-ok.sh`,
`scripts/check-hook-status.sh` and settings proposals: this repo has no pre-push gate.

## The path to a PR

1. Branch off `test` (AGENTS.md Rule 3).
2. Run `/feature`. It plans, critiques, implements and gets a `code-critic` review of the current commit. Skip its
   step 6 `scripts/review-ok.sh`: this repo has none.
3. Run adversarial QA in game through the QA harness: `docs/agent-rules/adversarial-qa.md`, with `/proteus harness on`
   in a local build. That guide and AGENTS.md Rule 5 name the owner's dev-load path, `E:\repos\Proteus\...`;
   use your own dev-load path instead.
4. Once every QA finding and Blocker is dispositioned, record it for the current commit:

   ```
   sh .claude/hooks/pr-ready.sh "ran: 2 findings fixed, 1 deferred as an issue"
   sh .claude/hooks/pr-ready.sh "skipped: no UI or API surface"
   sh .claude/hooks/pr-ready.sh "blocked: harness not listening; user chose to proceed"
   ```

   Run it only after a passing review of the current commit: running it is the attestation. A new commit makes it
   stale.
5. Open the PR against `test`, never `main`, and only when your user asks: `gh pr create --base test`. AGENTS.md Rule 4
   still applies; the hook does not replace your user asking.

## What the gate is

- It runs before your Claude's `gh pr create` and blocks it, with these steps, unless `.pr-ready` (gitignored) names the
  current commit and a QA outcome.
- It catches `gh pr create` / `gh pr new` at the start of a command, or after `&&`, `||`, `;`, `|` or `&`, in the first
  line before any quote. Anything else passes: it never gets in the way of commits, merges or pushes.
- It is not enforcement. A human can open the PR themselves, and the owner's own local merge flow is not gated.

## Changing `.claude/`

`.claude/settings.json` and `.claude/hooks/` run in every agent session in the repo as soon as a branch with them is
checked out. If your PR changes them, say so in its description.
