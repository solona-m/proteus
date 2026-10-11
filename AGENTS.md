# Proteus

Guidance for AI coding agents working in this repository. This file
(`AGENTS.md`) is the canonical copy; `CLAUDE.md` imports it via `@AGENTS.md` so
Claude Code (and its sub-agents) load it, while other tools that read
`AGENTS.md` directly still see one source of truth. Keep it lean — as the
instruction count grows, instruction-following quality degrades across *all*
instructions. Deep detail belongs in `docs/` (including `docs/adr/`) and the
code — this file is the map, not the territory.

## Rules — non-negotiable

These apply to **every** agent — the main agent and any sub-agents it
invokes. Nothing enforces them automatically; following them is your
responsibility.

1. **Use the project's canonical commands.** Build, run, stop, and test only
   through the documented commands (see *Commands*). If a wrapper exists
   (`make`/`just`/an npm script/…), do not invoke the underlying build tool
   directly — the wrapper may add serialisation, environment setup, or safety
   the raw tool skips. If a needed capability has no command, STOP and report
   the gap (Rule 2) rather than substituting raw shell.

2. **If a command or tool fails — or succeeds without doing what it should —
   STOP and report it; never silently work around it.** Give the user the exact
   step (command or tool call) and its full output, then wait. Do **not** retry
   with different flags/ports/tools or skip the step. Surfacing the problem is
   expected, not a failure on your part.

3. **Feature work always runs on its own branch off `test`, in its own
   worktree.** This is standing permission: don't ask. At the start of every
   feature, before planning, create a new branch from `test` in a new worktree
   (`EnterWorktree`), even if the session started on another feature branch.
   Then run `git submodule update --init --recursive` in it. Commit only there;
   never commit feature work directly onto `test` or `main`. At the end,
   after the review (Rule 4), merge the branch back into `test` locally from
   the primary checkout. Outside of that, don't switch the primary checkout's
   branch without asking (restoring a file with `git checkout -- <path>` is
   fine).

4. **Get reviewed before merging; never push unless asked.** Work is merged
   into `test` locally only after a passing `code-critic` review of the current
   commit — no unresolved critical issues. Any commit made after the review
   requires a re-review. There is no pre-push hook; never `git push` or open a
   PR unless the user asks.

5. **Every build meant for the game is loadable there.** Bump
   `Plugin.BuildNumber`, build Release, and copy the output to
   `E:\repos\Proteus\Proteus\bin\release\win-x64` — the only path the game
   dev-loads. Do this before running tests, not after. From a worktree, run
   `git submodule update --init --recursive` first, and check the copied
   `Proteus.dll` timestamp rather than trusting the build's output.

6. **One clean command per step — use the right tool.** Use the `Read` tool for
   file contents (never `cat`/`head`/`tail`/`sed`); search with a single plain
   `grep`/`rg`/`find`. This applies to every command, reading or acting: no
   leading `cd` (pass a path instead), and no `;`/`&&` chaining, `for`-loops, or
   `$(…)` glue. Need several things? Issue separate (parallel) tool calls, not
   one compound command. It keeps the transcript readable and sidesteps the
   permission prompts that compound and `cd`-prefixed commands trigger.

## Project Overview

Proteus is a Dalamud plugin for FFXIV (C# 14, .NET 10, x64, ImGui via Dalamud
bindings) that composites overlay textures onto a character's skin and gear at
runtime, draws "second skin" shells as gear, and edits the models of any
installed Penumbra mod (reshape, refit to another body, part toggles, hat
compatibility). It talks to Penumbra and Glamourer over IPC (both API repos
are git submodules), ships a native BCn compression shim, and a headless refit
CLI. Release assets are served through a Cloudflare worker (`dl.solona.info`).
User-facing docs: `README.md` (translated under `docs/`), `For Creators.md`.

**Before building features, read the product docs** in `docs/product-context/`
(vision, strategy, requirements). Architecture decision records are in `docs/adr/`.
This project's structured AI development workflow, if one is configured, is
documented by that workflow's own tooling (e.g. its installed plugin).

## Commands

- `dotnet build Proteus/Proteus.csproj -c Release` — build
- `dotnet test Proteus.Tests/Proteus.Tests.csproj -c Release` — run all tests
- `dotnet test Proteus.Tests/Proteus.Tests.csproj -c Release` — all checks incl. tests (also `npm test --prefix worker` when `worker/` changes)
- run: bump `Plugin.BuildNumber`, build, copy `Proteus/bin/Release/win-x64/` to `E:\repos\Proteus\Proteus\bin\release\win-x64` — the game dev-loads it (no app URL; verification is in game)
- stop: none — unload the plugin in game via `/xlplugins`
- single test: `dotnet test Proteus.Tests/Proteus.Tests.csproj -c Release --filter "FullyQualifiedName~BodyRetargetTests"`
- default branch: `test` — reviews diff against it; work merges into it (`main` is the stable release branch)

## Architecture

```
Proteus/                 the Dalamud plugin
  Services/              compositor, second-skin/.mdl writers, body refit/retarget, hat compat, imports
  Interop/               Penumbra/Glamourer IPC bridges, live game-memory access
  Gui/                   ImGui windows: Mods, Studio, Import, Export, Settings
  Models/                sidecar metadata, colour tables, presets
  Localization/          Strings.cs + en.json and 7 translations
  native/                proteus_bcn.dll BCn compression shim (built WITHOUT /arch:AVX2)
Proteus.Refit/           headless refit CLI (bodies by file path)
Proteus.Tests/           xunit tests; user data only via LocalData + [LocalDataFact]
XivLiveMesh/             CPU skinning / picking for the live brush
worker/                  Cloudflare worker that edge-caches release assets
Glamourer/, Penumbra.Api/  git submodules (IPC APIs)
```

## Testing

xunit + NSubstitute in `Proteus.Tests/`, one `*Tests.cs` per unit; synthetic
fixtures (`SyntheticModel`, `SyntheticPack`, …) and `Fixtures/`. Tests that need
real game or mod files read them via `LocalData` and are marked
`[LocalDataFact]` so they skip on CI — never hard-code a local path. While
iterating run only targeted `--filter` tests; run the full suite once, before
merging into `test`. The worker has its own `npm test` in `worker/`.

## Sensitive Areas — the security surface

The canonical list of files/areas where mistakes are expensive. Treat any
change touching these with extra scrutiny — a more careful review, a
stronger reviewer model if one is available, an explicit call-out in the PR
description. Keep the list short and concrete; if the rationale for an entry
needs more than a line, link a page under `docs/` for the depth rather than
expanding here.

- Binary game-file writers (`Services/SecondSkinWriter.*`, `Model*Writer.cs`, `ModelLodTrimmer.cs`, `GearMaterialWriter.cs`) — a malformed .mdl/.mtrl/.tex crashes the game
- Writes into users' Penumbra mods (`RefitModService.cs`, `MeshToggleService.cs`, `PenumbraModMeta.cs`, `ModelBackupOrder.cs`) — edits persist and travel with exports; groups append, never insert; never author v3
- `Proteus/Interop/` — unsafe struct offsets and IPC threading (Penumbra events fire on the caller's thread)
- `Proteus/Configuration.cs` migrations — a mistake loses user settings
- `LogAnonymizer.cs` / `LogExportService.cs` — character names must not leak into exported logs
- `worker/` and `.github/workflows/` — live release distribution

## Review & Planning Guidance

- Code review guidance: `docs/agent-rules/code-critic.md`
- Planning guidance: `docs/agent-rules/plan-critic.md`
