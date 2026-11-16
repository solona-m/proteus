# Clean-room log: `XivLiveMesh/PbdFile.cs`

The evidence that `docs/pbd-clean-room.md`'s rules were kept: every file, URL and command each participant used.
The plan that governed the run is `docs/pbd-clean-room-plan.md` (revision 6).

**Note on the last review.** The short re-review of the final commit (the build-number bump plus the Code-critic
section of this log) is recorded in the merge commit message, not in this file: recording it here would change this
file and so need yet another review.

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
