# Clean-room log: `XivLiveMesh/PbdFile.cs`

The evidence that `docs/pbd-clean-room.md`'s rules were kept: every file, URL and command each participant used.
The plan that governed the run is `docs/pbd-clean-room-plan.md` (revision 6).

**Note on the last review.** The short re-review of the final commit (the build-number bump plus the Code-critic
section of this log) is recorded in the merge commit message, not in this file: recording it here would change this
file and so need yet another review.

## Method

- **Planning participants** (the planner and the plan-critics) reported their own reads; those reports are copied
  from the plan's *Environment & Preconditions* section. The planning orchestrator's reads are copied verbatim from
  the plan's "Log inputs from the planning session".
- **Agent A** wrote its own section (a labelled self-report); the orchestrator never read A's transcript.
- **Agent B and the Code-critic** did not write their sections. The orchestrator extracted them mechanically from
  their transcript files, found by the agent IDs the Agent tool returned (A's transcript was never opened), with a
  script in the session scratchpad (not committed). The script reads only tool names and the `file_path`, `path`,
  `pattern`, `glob`, `type`, `output_mode`, `-n` and `command` inputs, and never prints a tool result.
  - `Opened:` lists every Read path (repo-relative when inside the worktree). A relative path, or a Grep/Glob call
    with no `path`, is resolved against the session's start folder (a different checkout) and counts as a hit.
  - `Shell commands:` copies every Bash/PowerShell command verbatim and flags any command outside the participant's
    allowlist (B: the absolute-path `dotnet build` of `Proteus.csproj` and targeted `dotnet test`; the Code-critic:
    its two `git -C E:/orca/workspaces/Proteus/meddle` commands and targeted `dotnet test`).
  - `Sub-agents:` counts `Agent`/`Task` calls.
  - "Grep without line numbers" counts content-mode Grep calls with `-n: false`.
  - "forbidden locations in listing/search results" checks each Grep and Glob result line without printing it. The
    path is taken by output mode: the whole line for Glob and `files_with_matches`; the part before the trailing
    `:<digits>` for `count`; the part before the first `:<digits>:` for `content` (never split at the first `:`, so a
    drive letter is safe). A line with no path part counts as the searched file only for a single-file search;
    anywhere else an unparseable line is a hit. Forbidden: `Glamourer/`, `Penumbra.Api/` and `Luna/` at the repo
    root, any `bin` or `obj` segment, `E:\repos\Proteus`, `E:\repos\DragAndDropTexturing`, other checkouts under
    `E:\orca\workspaces\Proteus\`, the memory topic folders and Agent A's scratch folder.
  - **Not counted:** read-type results (their paths are checked in `Opened:` and `Shell commands:`), the output of
    `dotnet build` and `dotnet test` (allowed builds print `Penumbra.Api -> …\bin\Release…`, `Glamourer.Api -> …` and
    `Test run for …\bin\Release…`), and the contents of allowed files (the doc, the plan, this log and the record test
    name the forbidden locations on purpose).

## Planner

Revisions 1–3. The planner stayed on the clean side throughout. It never opened the old file, its history, the
submodules, other checkouts, the memory topic files or A's folder. It opened no URLs and ran no show, log -p, diff or
blame.

Opened:
- `Proteus.Tests/PbdFileTests.cs`
- `Proteus.Tests/HairPortFixtures.cs` lines 35–114 (via grep)
- `Proteus.Tests/SyntheticSkeleton.cs` (grep of the `Pbd` signatures)
- `Proteus.Tests/OtherRaceWearerTests.cs` lines 40–99
- `Proteus/Services/OtherRaceWearer.cs` lines 120–159
- `Proteus.Tests/ReadmeTermsTests.cs` lines 115–144
- `Proteus.Tests/RacialRouteLocalTests.cs` lines 1–45
- `Proteus.Tests/LocalData.cs` lines 1–60, plus a grep
- `Proteus.Tests/LiveMeshTests.cs` (grep)
- `Proteus.Tests/SizeSetMadeOnLocalTests.cs` and `Proteus.Tests/CrossSexGloveReshapeLocalTests.cs` (grep of returns and attributes)
- `XivLiveMesh/NOTICE.md`
- `THIRD_PARTY_NOTICES.txt` (grep)
- `docs/LICENSE-DRAFT.md` (grep)
- `docs/agent-rules/plan-critic.md`
- `docs/agent-rules/code-critic.md` (grep of headings)
- the first lines of `XivLiveMesh/LiveMeshPoser.cs`, `XivLiveMesh/HavokSkeleton.cs` and `Proteus/Services/RacialModelBake.cs`
- context around the call sites in `OtherRaceWearer.cs`, `LiveCharacter.cs`, `HairPortJob.cs`, `UvTransferBakeJob.cs`, `LiveMeshPoser.cs`, `HavokSkeleton.cs` and `RacialModelBake.cs`
- `Proteus/Plugin.cs` (grep of `BuildNumber`)

Listings: `docs/`, `docs/adr`, `docs/agent-rules`, and `Proteus.Tests/` filtered by name.

Searches (all excluding `PbdFile.cs`, `Glamourer/`, `Penumbra.Api/`, `bin/`, `obj/` and `.git`): `PbdFile`, `Chain`,
`Resolve` and `Lists`; "Meddle"; test class names, including `GloveReshape`, `NudeLegs` and `HarnessHairPort`;
`Skip =` across `Proteus.Tests`. These searches did not name `Luna/` among their exclusions.

Git commands:
- `git log --oneline -- docs/pbd-clean-room.md`

URLs: none.

## Plan-critic 1

Opened:
- `docs/pbd-clean-room.md`
- `docs/agent-rules/plan-critic.md`
- `Proteus/Services/OtherRaceWearer.cs` lines 120–154
- `Proteus/Services/UvTransferBakeJob.cs` lines 190–229
- `XivLiveMesh/LiveMeshPoser.cs` lines 38–127
- `Proteus.Tests/PbdFileTests.cs`
- `Proteus.Tests/HairPortFixtures.cs` lines 30–109
- `XivLiveMesh/NOTICE.md`

Listings: `docs/adr`, `docs/product-context`, `.github/workflows`.

Searches: git grep with the exclusions only.

Git commands: git grep only. No show, log, diff or blame. URLs: none.

## Plan-critic 2

Opened:
- the plan draft (`scratchpad\plan.md`, the planning session's scratchpad)
- `docs/pbd-clean-room.md`
- `docs/agent-rules/plan-critic.md`
- `Proteus.Tests/LocalData.cs` lines 20–193

Listings: `Proteus/uvmaps/`.

Searches (git grep only): `PROTEUS_*` and `LocalDataFact` in `LocalData.cs`; output-path properties in the props,
targets and csproj files; `XivLiveMesh` references in the csproj, sln and slnx files; `Skip` patterns in
`Proteus.Tests`; ±3 lines of context in `EarringBonesLocalTests`, `SizeSetMadeOnLocalTests` and
`CrossSexGloveReshapeLocalTests`; `LocalData(Fact|Theory)` in 8 local test classes; the `BuildPbd`, `Pbd` and
`HighlanderPbd` signatures.

Git commands: git grep only. No show, log, diff or blame. URLs: none.

## Plan-critic 3

Opened:
- `docs/pbd-clean-room-plan.md` (each file in this list whole)
- `docs/pbd-clean-room.md`
- `docs/agent-rules/plan-critic.md`
- `.gitmodules`

Listings: `ls E:/orca/workspaces/Proteus/meddle/Luna` (top level), on `meddle`'s `Luna/`.

Git commands:
- `git grep -n "ProjectReference" -- '*.csproj'`, with the exclusions
- `git grep -n -i -c "draganddroptexturing\|glamourer/" -- docs/pbd-clean-room.md docs/pbd-clean-room-plan.md AGENTS.md` (counts only)
- `git -C E:/orca/workspaces/Proteus/meddle/Luna grep -l -i -e pbd -e deformer` (file names only, no hits)

Both touches of `Luna/` were on `meddle`'s `Luna/`, through absolute paths, before revision 4 forbade it. They saw
file names only, with no hits and no code. (Revision 5 of the plan wrongly said they ran in the session's start
folder, because the orchestrator's extraction script had shortened them to repo-relative form; revision 6 corrected
this.) No show, log, diff or blame, and no URLs. AGENTS.md, CLAUDE.md and the memory index were loaded automatically.

## Plan-critic 3 (re-check of rev 4)

Opened:
- `docs/pbd-clean-room-plan.md` lines 1–160, 195–284, 580–609 and 685–759

Searches: three `grep -n` runs on that file only (the Delta heading; forbidden location, listing, Sub-agents,
Planning orchestrator, Plan-critic 3, record test, Luna, EDGE-17; command input, shell, Commands, cat,
Select-String, rg).

Git commands: none. Listings and URLs: none. AGENTS.md, CLAUDE.md, the memory index and the plan-critic skill text
were loaded automatically.

## Plan-critic 4 (check of rev 5)

Opened:
- `E:/orca/workspaces/Proteus/meddle/docs/pbd-clean-room-plan.md`, whole, in two Read calls (lines 1–526 and 526–823)

Git commands: none. Listings, searches, other shell commands and URLs: none. Loaded automatically: AGENTS.md and
CLAUDE.md from the start folder, the user's global CLAUDE.md, the memory index and the plan-critic skill text.

## Planner (rev 4)

It stayed on the clean side.

Opened:
- `docs/pbd-clean-room-plan.md` in the `meddle` worktree, whole, in two reads (lines 1–566 and 566–707)

Git commands: none. Listings, searches and URLs: none. Loaded automatically, not opened: AGENTS.md and CLAUDE.md of
the session's start worktree (`E:\orca\workspaces\Proteus\E-orca-workspaces-Proteus-meddle-docs-pbd-clean-room-plan.md`;
nothing else in that worktree was opened), the user's global CLAUDE.md and the memory index.

## Planner (rev 5)

It stayed on the clean side, in the same session as Planner (rev 4).

Opened:
- `E:\orca\workspaces\Proteus\meddle\docs\pbd-clean-room-plan.md` lines 1–300 and 360–767, in two reads (lines
  301–359, the verbatim Requirements, were not re-read)

Git commands: none. Listings, searches, shell commands and URLs: none. Loaded automatically, not opened: as for
Planner (rev 4).

## Planner (rev 6)

It stayed on the clean side, in the same session as Planner (rev 4) and Planner (rev 5).

Opened:
- `E:\orca\workspaces\Proteus\meddle\docs\pbd-clean-room-plan.md` lines 1–320, 400–403, 480–509 and 548–823, in four
  Read calls with absolute paths

Git commands: none. Listings, searches, shell commands and URLs: none. Loaded automatically, not opened: as for
Planner (rev 4).

## Planning orchestrator

The session that wrote the plan and ran the planner and the plan-critics. Copied verbatim from the plan's "Log
inputs from the planning session":

Opened:
- `docs/pbd-clean-room.md` (both copies, compared with `git diff --no-index --stat`);
- the doc's copy under `readme-update`;
- AGENTS.md and CLAUDE.md, loaded into context automatically from the session's start folder `E:\orca\workspaces\Proteus\E-orca-workspaces-Proteus-meddle-docs-pbd-clean-room-plan.md` (a separate checkout of the same repo, not `meddle`).

It also ran:
- a `Grep` count of `PbdFile` across the worktree, which excluded the old file but **did** list file names under `Glamourer/Penumbra.GameData/Files/` (names and counts only, no content);
- `git log --oneline test..HEAD` and `HEAD..test`, `git switch -c`, and `git submodule update`.

Git commands: those above. URLs: none. The planning orchestrator never opened the old file.

**Two touches came before the rule** now in the plan's Decision 5: opening `docs/pbd-clean-room.md` in the
`readme-update` worktree (another checkout), and the Grep that listed file NAMES under
`Glamourer/Penumbra.GameData/Files/`. Both covered only the doc and file names, with no code content.

## Agent A (self-report)

This section is Agent A's own self-report. The orchestrator never read Agent A's transcript.

`Opened:`
- `docs/pbd-clean-room.md` (the governing document)
- `XivLiveMesh/PbdFile.cs` (the old reader)
- `Proteus.Tests/PbdFileTests.cs`
- `Proteus.Tests/HairPortFixtures.cs` (its `BuildPbd`)
- `Proteus.Tests/SyntheticSkeleton.cs` (lines 175–244, its synthetic pbd builder)
- `Proteus.Tests/OtherRaceWearerTests.cs` (lines 55–94, the damaged-deformer test)
- `Proteus/Services/OtherRaceWearer.cs` (lines 110–169, the caller's exception filter)
- `Proteus.Tests/RacialModelBakeTests.cs` (lines 60–99)
- `Proteus.Tests/LiveMeshTests.cs` (lines 160–189)
- `Proteus.Tests/LocalData.cs` (lines 1–80)
- Search results (matching lines only) for `PbdFile` / `BuildPbd` / `new PbdFile` across the worktree's own sources,
  which showed one-line matches in `THIRD_PARTY_NOTICES.txt`, `XivLiveMesh/NOTICE.md`, `XivLiveMesh/HavokSkeleton.cs`,
  `Proteus/Interop/LiveCharacter.cs`, `Proteus/TestHarness/HairPortJob.cs`, `Proteus/Gui/HairPortPanel.cs`,
  `Proteus/Services/HairPort/HairPortWatcher.cs`, `Proteus/Services/UvTransferBakeJob.cs` and the test files using
  `PbdFile`; no match came from `Glamourer/`, `Penumbra.Api/`, `Luna/`, `bin/` or `obj/`, and none of those folders
  was opened.
- The game's own `chara/xls/boneDeformer/human.pbd`, read through Lumina via `LocalData` (`PROTEUS_GAME`), by the probe.
- Files written: `docs/pbd-format-spec.md`, this log, the temporary probe, and notes in a private scratch folder.

`Git commands:` none.

`URLs:` none.

`Probe:` `Proteus.Tests/PbdSpecProbeTests.cs` (two `[LocalDataFact]` tests calling only the public API of `PbdFile`,
plus its own byte reads of the game file for the layout facts and `HairPortFixtures.BuildPbd` for the bad-input
cases), run with the targeted `dotnet test` filter; deleted before hand-over.

`Identifier-grep hits: 0` (after judging ordinary English words such as "name", "parent", "chain" and "count" as
excepted).

Agent A's private scratch folder (notes, extracted identifiers and comment phrases, probe output) was deleted at the
end.

## Orchestrator

The running orchestrator (this session) stayed on the clean side: it never opened the old `XivLiveMesh/PbdFile.cs`,
any revision, diff or history of it, `Glamourer/`, `Penumbra.Api/`, `Luna/`, `bin/` or `obj/` contents, other
checkouts, the memory topic files, Agent A's folder or Agent A's transcript. It opened no URLs. Its session's
AGENTS.md, CLAUDE.md and the memory index were loaded automatically from the start folder
`E:\orca\workspaces\Proteus\E-orca-workspaces-Proteus-meddle-docs-pbd-clean-room-plan.md`.

Opened:
- docs/pbd-clean-room-plan.md
- docs/pbd-format-spec.md (Agent A's hand-over)
- docs/pbd-clean-room-log.md
- Proteus.Tests/PbdFileTests.cs
- Proteus.Tests/HairPortFixtures.cs (lines 48–117, `BuildPbd`, plus a grep of it)
- Proteus.Tests/RacialRouteLocalTests.cs (lines 1–45)
- Proteus.Tests/ReadmeTermsTests.cs (lines 1–37, the repo-root helper)
- Proteus.Tests/LocalData.cs (lines 1–60, plus a grep for the attributes)
- the transcripts of Plan-critic 3 and Plan-critic 3's re-check, through the extraction script only (tool names and inputs, never results)
- its own scratch scripts (the extraction script, the plan-splicing script) in the session scratchpad

Searches: `human.pbd|boneDeformer` in `Proteus.Tests/`; `BuildPbd` in `HairPortFixtures.cs`; the attribute names in
`LocalData.cs`; the repo-root helper in `ReadmeTermsTests.cs`; stale wording in the plan; a Glob for a plan-critic's
transcript file by its agent ID under the session's project folder (file names only).

Git commands: `git status --short`, `git log --oneline test..HEAD`, `git rev-parse --short HEAD`, `git add`/`git commit`
of the plan revisions, the spec and the tests, `git diff --numstat -- Proteus.Tests/PbdFileTests.cs`,
`git diff --numstat --ignore-cr-at-eol HEAD~1 HEAD` and `git ls-files --eol` (both on the plan commit only, which
touched only `docs/pbd-clean-room-plan.md`). No `show`, `log -p`, `diff` or `blame` of the old path.

### Step 3: tests from the spec, proven on the old code

- **Pre-flight (3a).** `PROTEUS_GAME`, `PROTEUS_MODS` and `PROTEUS_MODPACKS` are set and `game/sqpack` exists under
  `PROTEUS_GAME`. The never-skip set (`PbdFileTests+RealFile`, `RacialRouteLocalTests`, `LiveMeshTests`) ran alone:
  53 tests, 53 passed, 0 skipped.
- **Old-code gate (3c).** The wide filter with `CleanRoom!=NewBehaviour` and without `PbdCleanRoomRecordTests`:
  1364 tests, 1363 passed, 0 failed, 1 skipped.
- **Skip list (3c):**
  - `EarringBonesLocalTests.Earring_nodes_measured`: the static `Skip =` attribute ("Step 0 diagnostic"), an allowed kind.
- **Baseline failures on the old code:** none.
- **Named never-skip cross-race tests that ran:**
  - `CrossSexRefitLocalTests.Starlit_drawn_with_the_game_s_women_skeleton_still_refits`
  - `KeepEndsLocalTests.Game_boots_baked_for_a_woman_keep_their_feet_and_read_on_the_game_legs`
- **NewBehaviour tests (left out of the gate):** `PbdFileTests.A_count_larger_than_the_file_is_refused_before_allocating`
  (race count and bone count), from the spec's two "Required change from the old reader:" markers (R-1).
- **Spec errors found:** none. No test was sent back to Agent A.

### Step 4: the deletion

- `git rm XivLiveMesh/PbdFile.cs`, committed as "Remove the Meddle-derived racial deformer reader ahead of its
  clean-room replacement" without `-v`. The diff was never viewed.
- Guard after the deletion: `git rev-list --count solona-m/pbd-clean-room..test -- XivLiveMesh/PbdFile.cs` printed 0.
- `bin/` and `obj/` in `XivLiveMesh`, `Proteus`, `Proteus.Tests` and `Proteus.Refit` were deleted (by the owner, with
  `rm -rf`, after the session's own delete was refused by its permission check), and checked gone with `Test-Path`
  (all eight False) before Agent B started.

### Step 5: Agent B's commit

The orchestrator extracted Agent B's record from its transcript by agent ID (see *Method* and *Agent B*), then committed
the new `XivLiveMesh/PbdFile.cs` with B's section. It did not edit B's file.

### Step 6: proof on the new code

- **Wide proof.** The step 3c command without `CleanRoom!=NewBehaviour` (still without `PbdCleanRoomRecordTests`):
  1366 tests, 1364 passed, 1 failed, 1 skipped. The two extra tests are the NewBehaviour cases (race count and bone
  count), which passed.
- **Skip list:** identical to step 3's:
  - `EarringBonesLocalTests.Earring_nodes_measured`: the static `Skip =` attribute ("Step 0 diagnostic").
- **Never-skip set and named cross-race tests ran and passed:** all `PbdFileTests+RealFile` vectors,
  `CrossSexRefitLocalTests.Starlit_drawn_with_the_game_s_women_skeleton_still_refits` and
  `KeepEndsLocalTests.Game_boots_baked_for_a_woman_keep_their_feet_and_read_on_the_game_legs`.
- **The one failure is environmental, not a baseline failure and not the reader:**
  `SizeSetGameCopyLocalTests.Older_copies_below_the_bar_stay_pieces(mod: "Sunfairy (Bibo+) - by Solona", piece: "c0101e0141_top")`,
  message "Sunfairy (Bibo+) - by Solona is not installed", raised by the test's own folder-exists check
  (`SizeSetGameCopyLocalTests.cs` line 44) before any code under test runs. It passed in step 3. Between the two runs
  the owner's mod folder was renamed to "Sunfairy - by Solona" (folder time 2026-11-17 09:08), outside this run. The
  test was not edited and the mod was not renamed back; the other cases of the same theory passed. This case is a gap
  in the proof, listed in the final report.
- **Goldens** (`GoldenTests`, `GoldenCrossBodyLocalTests`, `SkinTuckGoldenTests`), run once: 44 tests, 44 passed,
  0 skipped. No baseline was re-blessed.
- No failure went back to Agent B. No existing test was edited and no new test weakened.

### Step 7: paperwork

- `XivLiveMesh/NOTICE.md`: the *Third-party sources* table is replaced by a line saying none remain (pointing at the
  spec and this log). The FFXIVClientStructs paragraph, the research-references paragraph and the AGPL line are kept.
- `THIRD_PARTY_NOTICES.txt`: the Meddle block and one adjoining `----` separator are removed.

Later orchestrator git commands (all in `E:/orca/workspaces/Proteus/meddle`): `git rm XivLiveMesh/PbdFile.cs`,
`git rev-list --count solona-m/pbd-clean-room..test -- XivLiveMesh/PbdFile.cs`, `git status --short`, `git add` and
`git commit` of B's file, this log and the paperwork. No `show`, `log -p`, `diff` or `blame` of the old path. Later
files opened: `XivLiveMesh/NOTICE.md`, `THIRD_PARTY_NOTICES.txt` (lines 80–99, plus a grep for the Meddle block),
`Proteus.Tests/PbdCleanRoomRecordTests.cs`, `Proteus.Tests/SizeSetGameCopyLocalTests.cs` lines 36–47, the plan, and the
saved test output in the session scratchpad. It also listed `Sunfairy*` folder names in the owner's mods folder.

## Agent B

A fresh `general-purpose` sub-agent (opus) that never saw the old code. Its lists below were extracted mechanically
from its transcript (found by its agent ID), not written by it. Its own short narrative: it read the spec and the doc
in full, then `PbdFileTests.cs`, `PbdCleanRoomRecordTests.cs`, the XivLiveMesh callers and `XivLiveMesh.csproj`; it
wrote the reader from the spec, fixed one compile error of its own, and every test it ran passed first time. It found
no spec errors. Its two small choices: the "at most N blocks" length limit is checked before adding each block (spec
§4, §7.4), and `Chain` returns a new empty list rather than a shared empty array (spec §4, "each call returns a new
list").

Opened:
- docs/pbd-format-spec.md
- docs/pbd-clean-room.md
- Proteus.Tests/PbdFileTests.cs
- Proteus.Tests/PbdCleanRoomRecordTests.cs
- XivLiveMesh/HavokSkeleton.cs
- XivLiveMesh/XivLiveMesh.csproj

Searches:
- Grep `PbdFile` in `E:/orca/workspaces/Proteus/meddle/Proteus.Tests` (files_with_matches)
- Grep `PbdFile|\.Chain\(|\.Resolve\(|\.Lists\(` in `E:/orca/workspaces/Proteus/meddle/XivLiveMesh` (content, with line numbers)
- Glob `*.csproj` in `E:/orca/workspaces/Proteus/meddle/XivLiveMesh`

Written:
- XivLiveMesh/PbdFile.cs (one Write, one Edit)

Shell commands:
- `dotnet build E:/orca/workspaces/Proteus/meddle/Proteus/Proteus.csproj -c Release`
- `dotnet test E:/orca/workspaces/Proteus/meddle/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "FullyQualifiedName~PbdFileTests"`
- `dotnet test E:/orca/workspaces/Proteus/meddle/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "FullyQualifiedName~OtherRaceWearerTests|FullyQualifiedName~RacialRouteLocalTests|FullyQualifiedName~LiveMeshTests|FullyQualifiedName~HairPort|FullyQualifiedName~PbdCleanRoomRecordTests.Public_surface_is_exactly_the_documented_one|FullyQualifiedName~PbdCleanRoomRecordTests.Header_names_the_spec_not_a_port"`

Git commands: none. URLs: none.

Sub-agents: none

Grep without line numbers: 0

Shell commands outside the allowlist: 0

forbidden locations in listing/search results: 0

B's own test runs: `PbdFileTests` 75 passed, 0 failed, 0 skipped (including the `RealFile` vectors and the NewBehaviour
test); the second filter 569 passed, 0 failed, 0 skipped.
