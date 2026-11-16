# Hand-off: clean-room rewrite of `XivLiveMesh/PbdFile.cs`, ready to run

This is the approved plan for carrying out `docs/pbd-clean-room.md`, written for the agent that runs it.
Everything after the next rule is the plan itself (revision 3). The planner wrote it, the plan-critic
reviewed it twice, and every amendment was folded back in through the planner.

## Where things stand (2026-11-16)

- **Branch and worktree:** `solona-m/pbd-clean-room`, cut from `test` in `E:\orca\workspaces\Proteus\meddle`.
  Submodules are initialised. This hand-off file is the only commit on the branch. No spec, test or code
  work has started.
- **Plan reviews:**
  - Round 1: the critic rated it MEDIUM-HIGH. Its 12 amendments were folded in as revision 2.
  - Round 2: the critic rated it MEDIUM. Its 11 amendments were folded in as revision 3 (the Delta below).
  - A final critic pass on revision 3 started but didn't finish before the session ended. **First step:** run
    `plan-critic` (model `opus`) on this file once, and fold in any blocking findings through `planner`.
- **Decisions already made:**
  - No resemblance check by Agent A (Decision 11).
  - Agent B works in this worktree after the deletion commit, with no git and with `bin/`/`obj/` deleted (Decision 12).
  - The header keeps the AGPL SPDX line, because `docs/LICENSE-DRAFT.md` isn't in force.
  - Adversarial QA is skipped, because nothing user-facing changes. In-game other-race bending will be UNVERIFIED.

## Before you start: you are on the clean side

You are the orchestrator. Stay clean:

- **Never open:**
  - `XivLiveMesh/PbdFile.cs`, any revision or history of it, or a diff of the deletion;
  - anything under `Glamourer/` (it holds Penumbra's GPL `Penumbra.GameData/Files/PbdFile.cs`), `Penumbra.Api/`, `bin/` or `obj/`;
  - `E:\repos\Proteus` files, other Proteus worktrees, the dev-load folder or `E:\repos\DragAndDropTexturing`;
  - the memory topic files under `C:\Users\solon\.claude\projects\*\memory\` (several describe the old reader);
  - Agent A's folder or transcript.
- **Searches:** exclude every path above from every search. Use `git grep` with
  `':!XivLiveMesh/PbdFile.cs' ':!Glamourer' ':!Penumbra.Api'`. Never run a bare repo-wide grep for `PbdFile`.
- **Fixes:** only Agent A ever reads the old code. Every fix to the new `PbdFile.cs` goes back to Agent B.

## Log inputs from the planning session

The plan's *Environment & Preconditions* section lists what the planner and both plan-critics opened.

The planning orchestrator also opened only:

- `docs/pbd-clean-room.md` (both copies, compared with `git diff --no-index --stat`);
- the doc's copy under `readme-update`;
- AGENTS.md (loaded into context).

It also ran:

- a `Grep` count of `PbdFile` across the worktree, which excluded the old file but **did** list file names
  under `Glamourer/Penumbra.GameData/Files/` (names and counts only, no content);
- `git log --oneline test..HEAD` and `HEAD..test`, `git switch -c`, and `git submodule update`.

The orchestrator never opened the old file.

## Practical notes

- Start the keep-awake script with the **PowerShell** tool, not Bash, with `timeout: 7200000`. Bash strips the
  backslashes from the path, and the default timeout kills the script after 30 minutes.
- Sub-agent transcripts sit next to the session as JSONL. A small script that prints only the last assistant
  text, or only the `tool_use` inputs, is enough for Decision 8's extraction. Never print tool results,
  only the count of forbidden strings found in them.

---

# Implementation Plan: Clean-room rewrite of the racial deformer reader (`XivLiveMesh/PbdFile.cs`)

## Delta (rev 3: second plan-critic's 11 amendments, all accepted by the orchestrator)
- **B1 (allowed skips instead of "0 skips"):**
  - Step 3 records its skip list by name and reason. Only two kinds of skip are allowed: a static `Skip =` attribute (inside the wide filter that is only `EarringBonesLocalTests`, line 36, "Step 0 diagnostic"), or a named missing-mod skip.
  - Step 6's skip list must match step 3's exactly.
  - A never-skip set is added: `PbdFileTests.RealFile.*`, `RacialRouteLocalTests`, `LiveMeshTests`, and at least one named cross-race test each in `CrossSexRefitLocal*` and `KeepEndsLocal*`.
  - Before step 3, a pre-flight run proves the data paths resolve; checking that the env vars are set is not enough.
  - Corrected fact: the `SizeSetMadeOnLocalTests` (line 40) and `CrossSexGloveReshapeLocalTests` (line 28) fixtures return early when data is missing. The tests' own `[LocalDataFact]`/`[LocalDataTheory]` attributes then report the skip. So "never silently passed" holds for the tests but not for those fixtures.
- **B2 (breaking the review/log loop):**
  - The code-critic's log section and the build-number bump go in one final commit.
  - Its short re-review reads only that commit: `git show --stat HEAD` plus the diffs of those two files, away from the old path.
  - That last review's extracted reads go into the merge commit message, which is not reviewed. The log says so at the top.
- **B3 (merge conflicts):**
  - A conflict on the path keeps the feature branch's side (`git checkout --theirs XivLiveMesh/PbdFile.cs` when merging into `test`). The other side is never opened.
  - `PbdCleanRoomRecordTests` runs straight after the merge, before the quick suite.
- **B4 (wider transcript extraction):**
  - The extraction also records `glob`, `type` and `output_mode`.
  - The script also counts forbidden path strings in tool results and prints only the count, which must be 0 for B and for the critic.
- **B5 (A's own folder):**
  - A works in its own temp folder `…\scratchpad-agentA\`, a sibling of the session scratchpad. It is forbidden to the clean side and deleted after hand-over.
  - The probe has one location: a temporary test file in this worktree, deleted before hand-over. Its printed output and A's notes go to A's folder.
- **B6 (markers):**
  - A "Required change from the old reader:" marker may cite R-1 or a doc §3 "must tolerate" case that the old reader doesn't tolerate. The orchestrator sends any other marker back to A.
  - "Without throwing" is dropped from EDGE-5, EDGE-11 and EDGE-14, and "Identity" from EDGE-12, unless the spec says so.
- **B7 (exception to A5):** The orchestrator may run git, build and test commands in `E:\repos\Proteus` for the merge, the guard and the quick suite. It may not open any file there, and must not view conflict content.
- **B8 (guard prints a count only):**
  - The guard is now `git rev-list --count solona-m/pbd-clean-room..test -- XivLiveMesh/PbdFile.cs` and prints a count only.
  - Before the pre-merge run, `git pull` updates `test` in the primary checkout (Dependabot merges on GitHub).
- **B9 (what "fails the same way" means):**
  - It means the same assertion message, word for word, on both runs.
  - Deformer tests that already failed on the old code are listed as gaps in the proof in the final report.
- **B10 (allowed paths in the record test):**
  - The extraction writes paths under the worktree as repo-relative.
  - The record test accepts only repo-relative paths plus a short list of generic tool paths: the plugin cache under `C:\Users\solon\.claude\plugins\`, and any path containing `\Temp\claude\` and `\scratchpad\` but not `scratchpad-agentA`.
  - No session path is hard-coded, so the test also passes after the merge in the primary checkout.
- **B11 (existing test untouched):** AC-5 adds that `git diff test --numstat -- Proteus.Tests/PbdFileTests.cs` must show 0 deleted lines.
- **Log inputs:** the second plan-critic's reads are added to the list under Environment & Preconditions.
- **Earlier revisions** (still in force):
  - rev 2 folded in the first critic's 12 amendments: wide filter, old-code baseline, NewBehaviour trait, 1e-5 tolerance, A5 forbidden list, deleting `bin/`/`obj/`, transcript-based logs, A's identifier grep, the guard, R-1 count checks, bump after review, golden wording;
  - both NEEDS_DECISION items are decided (Decisions 11 and 12).

## Approval Summary
**Goal:** Replace the Meddle-derived `XivLiveMesh/PbdFile.cs` with one Solona owns outright. It is written in two separated stages: a spec by an agent that read the old code, then code by an agent that never saw it. Other-race bending must behave exactly as before, and a record must show the separation was kept, so Meddle no longer blocks moving Proteus off the AGPL.

**Slug:** `pbd-clean-room`

**Acceptance Criteria:**
- AC-pbd-clean-room-1: `docs/pbd-format-spec.md` exists with the seven required sections, written as tables and rules, with no code or pseudo-code. Every "Required change from the old reader:" marker cites R-1 or a doc §3 item.
- AC-pbd-clean-room-2: `docs/pbd-clean-room-log.md` lists the files, URLs and git commands of every participant.
  - B's and the code-critic's lists are extracted from their transcripts, touch nothing forbidden, and report 0 forbidden strings in tool results.
  - A's section is a labelled self-report, with its identifier-grep hit count.
  - The final re-review's reads are in the merge commit message, and the log says so.
- AC-pbd-clean-room-3: the new `PbdFile` has exactly the documented public surface, and no caller file changes.
- AC-pbd-clean-room-4: the new file starts with the AGPL SPDX line, `// Copyright (c) Solona` and a line saying it was written from `docs/pbd-format-spec.md`. It contains no "ported from", Meddle or SkinTattoo text.
- AC-pbd-clean-room-5: the wide proof filter passes on the new code. Three things must hold:
  - its skip list matches step 3's exactly, and the never-skip set ran;
  - the only failures are ones already recorded on the old code, with the same message word for word;
  - `PbdFileTests.cs` has 0 deleted lines vs `test`.
- AC-pbd-clean-room-6: Agent A's test vectors are new tests in `PbdFileTests.RealFile`: chain lengths, resolved matrices within 1e-5 per element, and `Lists` answers. They never skip, and they pass against the old code before the deletion and against the new code after.
- AC-pbd-clean-room-7: the golden baselines (`GoldenTests`, `GoldenCrossBodyLocalTests`, `SkinTuckGoldenTests`) are run once, as the doc requires, and pass with no baseline re-blessed. They do not exercise the deformer.
- AC-pbd-clean-room-8: `XivLiveMesh/NOTICE.md` no longer lists `PbdFile.cs` as third-party, and `THIRD_PARTY_NOTICES.txt` has no Meddle entry.

**Key decisions:**
- **Tests before deletion:** the orchestrator, who never sees the old code, writes the vector and edge tests from the spec. It runs them, plus the wide baseline filter, against the old code before deleting it. Tests the spec marks as new behaviour are left out of that gate.
- **Clean review:** the code-critic stays on the clean side. It sees the diff vs `test` with the old path excluded, plus the new file as a whole. Every fix to `PbdFile.cs` goes to B.
- **Isolating B:** B runs no git commands, works with `bin/` and `obj/` deleted, and searches only this worktree. Its log comes from its transcript.
- **Final commit:** the bump and the code-critic log go in one last commit, which gets a short re-review.
- **No adversarial QA in game:** nothing user-facing changes. In-game bending is UNVERIFIED (only the owner can check it).

**Risk flags:** security surface: no (but high scrutiny: decides other-race vertex positions written into users' mods) · new persisted field: no · sensitive-category data: no · schema migration: no · new dependency: no · new/changed route: no

**NEEDS_DECISION:** None. Both earlier items are decided (Decisions 11 and 12).

*(The summary above is what the developer approves; everything below is the detailed contract it stands on.)*

## Source
- User prompt: the path `E:\orca\workspaces\Proteus\readme-update\docs\pbd-clean-room.md`, identical to `docs/pbd-clean-room.md` in this worktree (on `test` since 97ab1e88). The request is to carry out that document.
- Referenced but not needed for the requirements:
  - `docs/LICENSE-DRAFT.md`, read only for its status ("DRAFT, not yet in force");
  - the Meddle and SkinTattoo repos, which are forbidden and were never opened.

## Context & Decisions
**Problem.** `XivLiveMesh/PbdFile.cs` reads `human.pbd` (the racial deformer) and resolves chains and bones. It was ported from Meddle (AGPL) by way of SkinTattoo, so while it is in the plugin, Proteus must stay AGPL. The doc's second route is a clean-room rewrite: Agent A writes a spec of facts only, and Agent B, who never sees the old code, writes the new code from it.

**Solution shape.**
1. A writes the spec and a self-report.
2. The orchestrator writes tests from the spec and proves them on the old code, along with a wide baseline run.
3. The old file and the build folders are deleted.
4. B writes the new file.
5. A wide real-data run shows nothing changed, and the goldens run as the doc requires.
6. Paperwork.
7. Clean-side review.
8. A final commit (bump + critic log) and its short re-review.
9. Build and merge.

**Facts established while planning (from allowed files only):**
- **API in use.** Callers use only `new PbdFile(byte[])` (implicitly converted to a `ReadOnlySpan<byte>`), `pbd.Chain(from, to)`, `PbdFile.Resolve(...)` and `PbdFile.Lists(...)`. A grep across `Proteus/`, `XivLiveMesh/`, `Proteus.Tests/` and `Proteus.Refit/` (excluding the old file and the submodules) found no other member, so B has nothing extra to keep. `HavokSkeleton.cs` mentions `PbdFile.Resolve` in a doc comment, which still resolves.
- **Exceptions callers expect.** `OtherRaceWearer.Build` (lines 136–144) catches only `ArgumentException` (and its subclasses), `IndexOutOfRangeException`, `OverflowException` and `OutOfMemoryException`.
  - `OtherRaceWearerTests.A_damaged_deformer_is_refused_not_thrown` pins three damaged inputs; each must throw one of those types.
  - An `EndOfStreamException` or `InvalidDataException` would escape that catch.
  - `LiveCharacter.LoadPbd` and `HairPortJob` catch every exception type.
- **Test writers for the format.** `Proteus.Tests/HairPortFixtures.BuildPbd` and `SyntheticSkeleton.Pbd`/`HighlanderPbd` are Proteus's own test writers for the format; the doc lets B read tests. They write name offsets as `(short)`, so the >32 KB test depends on the reader treating them as unsigned.
- **Real-data tests.**
  - They need `PROTEUS_GAME`, `PROTEUS_MODS` and `PROTEUS_MODPACKS` (`Proteus.Tests/LocalData.cs`). A test whose data is missing reports SKIPPED with the reason.
  - The `SizeSetMadeOnLocalTests` (line 40) and `CrossSexGloveReshapeLocalTests` (line 28) fixtures return early when data is missing. Their tests still skip, through their own attributes.
  - Static `Skip =` attributes inside the wide filter: `EarringBonesLocalTests` line 36 only. `AssetRelocationTests` and `RefitNippleSmoothLocalTests` are outside the filter.
  - Loading the real `human.pbd` through Lumina is slow, which is why `LiveMeshTests` and `RacialRouteLocalTests` are marked `Slow`.
- **Where "Meddle" appears.** Only in `THIRD_PARTY_NOTICES.txt`, `XivLiveMesh/NOTICE.md` and `docs/pbd-clean-room.md`.
- **Licence pinned to AGPL.** `ReadmeTermsTests.Licence_stays_AGPL_and_drafts_are_drafts` requires `docs/pbd-clean-room.md` to exist and the root licence to stay AGPL.
- **Build number.** `Plugin.BuildNumber` is 1464 (`Proteus/Plugin.cs:33`).
- **Forbidden code in the submodule.** The Glamourer submodule contains Penumbra's GPL `Glamourer/Penumbra.GameData/Files/PbdFile.cs` and `PbdFile.Write.cs`. Every clean-side search must exclude `Glamourer/`, `Penumbra.Api/`, `bin/` and `obj/`.
- **Auto-loaded memory index.** The memory index is loaded into every agent's context. It holds one-line descriptions, and its only fact about the deformer ("signed offsets") is already in the doc. The memory topic files themselves are forbidden.

**Decisions (and alternatives rejected):**
1. **Roles.**
   - **Agent A:** a fresh `general-purpose` sub-agent (opus). It may read what the doc allows.
   - **Orchestrator:** never opens the old file or anything forbidden.
   - **Agent B:** a second fresh `general-purpose` sub-agent (opus). It is briefed only with the doc, the spec, the allowed and forbidden lists, R-1 to R-3 and the search restrictions.
   - Departing from the /feature rule "only the main agent implements" is required by the doc.
   - Rejected: the orchestrator implementing. The doc asks for split roles, and the split keeps the reviewer and committer separate from the author.
2. **Tests are written from the spec and proven on the old code before the deletion.**
   - Edge-case test names are written neutrally (`…_follows_the_spec`), and their assertions are fixed only from the spec.
   - A test that fails because it was coded wrong is fixed by the orchestrator.
   - A test that fails because the spec is wrong goes back to A: the orchestrator names the section, the claim and the observed output, A amends the spec, and the spec is re-committed.
   - Tests for marked departures carry `[Trait("CleanRoom","NewBehaviour")]` and are left out of the gate.
   - No test is weakened to pass the gate.
   - Rejected: writing the vectors after B, because spec errors would then look like code errors.
3. **The parent lookup is a table in spec §7.** The parent lookup for the vectors is an explicit name→parent table there, so the tests need only `human.pbd`.
4. **The code-critic stays clean.**
   - It gets:
     - `git diff test -- . ':!XivLiveMesh/PbdFile.cs'`;
     - `git show HEAD:XivLiveMesh/PbdFile.cs` (the new file, taken after B's commit);
     - the spec and the log.
   - It never views the `test`-side revision of that path, and it follows Decision 5.
   - Its findings for B cite only lines of the new file and spec sections.
   - Any history or diff command on the old path without the exclusion voids the review: it is re-run fresh and the void is logged.
   - B4's count of forbidden strings in tool results must be 0.
5. **Forbidden for every clean-side agent** (B, both plan-critics, the code-critic, the orchestrator, the planner):
   - the old `XivLiveMesh/PbdFile.cs`, and any revision, commit body or history of it;
   - Meddle, SkinTattoo, Ktisis, Brio, DragAndDropTexturing (including `E:\repos\DragAndDropTexturing`), TexTools/xivModdingFramework, and Penumbra's or Glamourer's deformer code (all of `Glamourer/` and `Penumbra.Api/`);
   - any GPL/AGPL source, and any SkinTattoo or Meddle clone anywhere;
   - web searches for pbd-parser code;
   - `E:\repos\Proteus` (except as in Decision 15) and every other checkout or worktree under `E:\orca\workspaces\Proteus\` except `meddle`;
   - the dev-load folder `E:\repos\Proteus\Proteus\bin\release\win-x64` (write-only, for the final copy);
   - `C:\Users\solon\.claude\projects\*\memory\`;
   - A's folder `…\scratchpad-agentA\`;
   - other repos' git history;
   - anything under `bin/` or `obj/`.

   B's searches stay inside `E:\orca\workspaces\Proteus\meddle`, minus these paths.
6. **Git hygiene.**
   - The orchestrator deletes with `git rm XivLiveMesh/PbdFile.cs` and commits without `-v`.
   - It never runs `git show`, `git log -p` or `git diff` without the exclusion on any range that contains the deletion. Summaries use `--stat`, `--numstat` or `--name-only`.
   - B runs no git commands at all.
7. **The licence header** is `// SPDX-License-Identifier: AGPL-3.0-or-later`, because the draft licence isn't in force.
8. **The log.**
   - It opens with a note: the last short re-review (of the final commit) is recorded in the merge commit message, not in this file, because recording it here would need yet another review.
   - Sections: Planner, Plan-critic 1, Plan-critic 2, Agent A, Orchestrator, Agent B, Code-critic. Each has `Opened:` and `Git commands:` lists; B's section also has `Written:`.
   - **Planner and plan-critics:** their reports (see Environment & Preconditions).
   - **Agent A:** A's self-report. It says the orchestrator never read A's transcript, gives the identifier-grep hit count, and names the probe file and its deletion.
   - **Agent B and code-critic:** extracted mechanically from their transcripts. Each section also gives the "forbidden strings in tool results: N" count.
   - **Orchestrator:** its own actions, the pre-flight result, the skip lists and run counts from steps 3 and 6, the baseline failures with their messages, the never-skip tests it named, the guard counts and the NewBehaviour list.
   - **The extraction script** lives in the session scratchpad and is not committed; it is written with Write, not a heredoc.
     - It reads only tool names and the `file_path`, `path`, `pattern`, `glob`, `type`, `output_mode` and `command` inputs.
     - It writes paths under the worktree as repo-relative.
     - It counts forbidden path strings in tool results without printing them: `glamourer/`, `penumbra.api/`, `/bin/`, `/obj/`, `repos/proteus/`, the other `workspaces/proteus/` checkouts, `draganddroptexturing`, `/memory/` and `scratchpad-agenta`, matched case-insensitively with `\` normalised to `/`.
   - The log describes the method.
9. **The real-data vectors** go in a nested class `PbdFileTests.RealFile`. It is marked `[Trait("Speed","Slow")]` if its setup takes 5 s or more. The synthetic edge tests stay in `PbdFileTests`, in the quick suite.
10. **`PbdCleanRoomRecordTests.cs`** holds the record tests, mirroring `ReadmeTermsTests`'s repo-root helpers. It is run only after B and the paperwork, and again after the merge.
11. **Decided: no resemblance check by A.** A clean critic can't do one, and the record is the evidence.
12. **Decided: B works in this worktree after the deletion commit.** Git is forbidden to B, and `bin/` and `obj/` are deleted first. Rejected: an export with no `.git`, because it risks breaking the build that depends on the submodules.
13. **No adversarial QA in game.** The change is meant to keep behaviour the same, and it adds no surface. The finished build is copied to the game's load folder (Rule 5); in-game bending is UNVERIFIED.
14. **The final commit (B2).**
    - After the code review passes, one commit holds both the Code-critic log section and the `Plugin.BuildNumber` bump.
    - Its short re-review reads only that commit: `git show --stat HEAD`, `git show HEAD -- docs/pbd-clean-room-log.md` and `git show HEAD -- Proteus/Plugin.cs`.
    - The reads of that re-review are extracted the same way and put in the merge commit message.
    - Any change after that commit means redoing the bump commit and its re-review.
    - Before the copy to the load folder, other sessions are warned.
15. **Exception to Decision 5 for `E:\repos\Proteus` (B7).**
    - The orchestrator may run git, build and test commands there: `git pull`, the guard, `git merge`, `git checkout --theirs` on a conflict, `dotnet test` for the record tests and the quick suite, and `git push`.
    - It may not open any file there, and must not view conflict content (no `git diff` during a conflict).
16. **The guard (B8).**
    - Command: `git rev-list --count solona-m/pbd-clean-room..test -- XivLiveMesh/PbdFile.cs`, which prints a count only.
    - It runs after the deletion, and again before the merge, after `git pull` updates `test` in the primary checkout.
    - A count above 0 means: don't merge. A fresh A amends the spec from that change, then B, and the proof repeats.
17. **What "fails the same way" means (B9).**
    - A test that fails on both runs must fail with the same assertion message, word for word.
    - Deformer tests that already failed on the old code are listed in the final report as gaps in the proof.

## Requirements
Verbatim from `docs/pbd-clean-room.md` (the user's request):

> ## Why
>
> `XivLiveMesh/PbdFile.cs` is ported from Meddle (PassiveModding, AGPL-3.0): `Meddle.Utils/Files/PbdFile.cs` and `Meddle.Utils/RaceDeformer.cs`, by way of SkinTattoo @bc3663a. While it is in the plugin, the plugin must stay AGPL. Solona can either get the Meddle authors' written permission (ask first — if they agree, none of this is needed) or replace the file with an independent implementation. This document is the second route.
>
> A clean-room rewrite has two people who never meet in the code: one who reads the old code and writes down only the facts (the file format and the maths), and one who has never seen the old code and writes new code from those facts. The facts of a file format aren't protected by copyright; the way someone wrote the code can be. Keeping the two roles apart is what shows the new code wasn't copied.
>
> ## The rules for both agents
>
> - Work on a branch off `test`, in its own worktree, through the `/feature` pipeline (`AGENTS.md`, Rule 3).
> - Never open, quote or paraphrase code from: Meddle, SkinTattoo, Ktisis, Brio, DragAndDropTexturing, TexTools / xivModdingFramework, Penumbra's or Glamourer's deformer code, or any other GPL/AGPL source. Don't search the web for "pbd parser" code either. Agent B also never opens the old `PbdFile.cs` (below).
> - Keep a log: every file and URL each agent opened, committed as `docs/pbd-clean-room-log.md`. It is the evidence that the rule was kept.
>
> ## Agent A — the spec writer (may read the old code)
>
> **Goal:** write `docs/pbd-format-spec.md`: a plain-language description of the `human.pbd` format and of what the three public methods compute. No code, no pseudo-code that mirrors the old code's structure, no copied identifiers beyond the public API below, no comments lifted from the old file.
>
> **May read:** `XivLiveMesh/PbdFile.cs`, `Proteus.Tests/PbdFileTests.cs` and the other tests that use it, its callers in `Proteus/` and `XivLiveMesh/` (Proteus's own code), and a real `chara/xls/boneDeformer/human.pbd` from the game, via `LocalData` (never a hard-coded path).
>
> **The spec covers, as facts:**
>
> 1. **File layout.** Byte order; the count at the start; the table of per-race entries (what each field holds, its width and signedness); the table of tree links (parent / first child / next sibling / entry index, and the "no parent" value); where each race's deformer block starts.
> 2. **A deformer block.** Its bone count; the table of name offsets (width, signedness — they are UNSIGNED: NIRÆ's 600-odd-bone deformer puts names more than 32 KB past the block start — and what they are relative to); how names are terminated and encoded; the padding before the matrices; each matrix's 12 floats and how they map onto a 4×4 transform; what happens to a name that appears twice.
> 3. **Bad input.** What the reader must tolerate: an entry whose offset is 0, negative or past the end of the file; two entries sharing a block; a link index or parent out of range.
> 4. **The race chain.** Given a race the model was authored for and a race it is drawn on: how the tree is walked from the target towards the source, when the answer is "no bending" (same race, either race 0), when it is "no chain" (a race not in the file, a broken link, the source never reached, a loop), and the order the deformers apply in.
> 5. **One bone through a chain.** For each deformer in order: use the bone's own matrix if listed, otherwise its nearest listed ancestor's (ancestors come from a caller-supplied parent lookup), otherwise nothing; how the results combine; the depth guard; and the separate yes/no question "did any deformer list this bone or an ancestor".
> 6. **Conventions.** System.Numerics uses row vectors: state exactly which way round the stored matrix is and the multiplication order, with one worked example from a real file (a bone, its stored floats, the resulting transform applied to a sample point).
> 7. **Test vectors.** A handful of real inputs and expected outputs Agent B can check against: for chosen race pairs, the chain length; for chosen bones, the resolved matrix (to 6 decimal places).
>
> Agent A writes no implementation and hands over only `docs/pbd-format-spec.md`.
>
> ## Agent B — the implementer (never sees the old code)
>
> **Start in a fresh context**, in a worktree where the old file has already been deleted (the main agent deletes `XivLiveMesh/PbdFile.cs` in a commit before B starts). B must not run `git log -p`, `git show`, `git blame` or `git diff` on that path, nor read any earlier revision of it, nor open the files listed in the rules above.
>
> **May read:** `docs/pbd-format-spec.md`, this document, the callers in `Proteus/` and `XivLiveMesh/` (to see how the API is used), and the tests.
>
> **Write** a new `XivLiveMesh/PbdFile.cs` with this public surface, unchanged, so no caller changes:
>
>     namespace XivLiveMesh;
>
>     public sealed class PbdFile
>     {
>         public PbdFile(ReadOnlySpan<byte> data);
>
>         // The deformers that take a model authored for `from` onto a `to` skeleton, in the order they apply;
>         // empty when no bending is needed or none is defined.
>         public IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> Chain(ushort from, ushort to);
>
>         // One bone's deformation through a whole chain, as a single matrix.
>         public static Matrix4x4 Resolve(IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> chain, string bone,
>                                         Func<string, string?> parentOf);
>
>         // Whether any deformer in the chain lists the bone or one of its ancestors.
>         public static bool Lists(IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> chain, string bone,
>                                  Func<string, string?> parentOf);
>     }
>
> Header: `// SPDX-License-Identifier: LicenseRef-Proteus` (or the AGPL line if the new licence isn't in force yet), `// Copyright (c) Solona`, and one line saying it was written from `docs/pbd-format-spec.md`. No "ported from" line.
>
> ## Proving it behaves the same
>
> Run, from the worktree's own build (never overwrite the game's dev-load DLL just to test):
>
>     dotnet test Proteus.Tests/Proteus.Tests.csproj -c Release --filter "(FullyQualifiedName~PbdFileTests|FullyQualifiedName~RacialModelBakeTests|FullyQualifiedName~RacialRoute|FullyQualifiedName~OtherRaceWearerTests|FullyQualifiedName~OtherRaceRefit|FullyQualifiedName~HairPort|FullyQualifiedName~HairScalpPushTests|FullyQualifiedName~SkeletonRemapTests|FullyQualifiedName~LiveMeshTests|FullyQualifiedName~KeepEndsLocalTests|FullyQualifiedName~UvBakeStep0LocalTests|FullyQualifiedName~UvTransferMapsLocalTests)"
>
> Also run the golden baselines (`GoldenTests`, `GoldenCrossBodyLocalTests`, `SkinTuckGoldenTests`) once, because this file decides where every other-race vertex lands. The `[LocalDataFact]` tests only run on the owner's machine, where the real game files are: they matter most here, so the run has to happen there.
>
> Add Agent A's test vectors to `PbdFileTests` as new tests. A difference anywhere is a bug in the new code or the spec — fix it from the spec, never by looking at the old file.
>
> ## Paperwork once it passes
>
> - `XivLiveMesh/NOTICE.md`: remove the `PbdFile.cs` row from *Third-party sources*.
> - `THIRD_PARTY_NOTICES.txt`: remove the Meddle entry.
> - Commit `docs/pbd-format-spec.md` and `docs/pbd-clean-room-log.md` with the code: together they are the record of how it was written.
> - The merge commit says it is a clean-room replacement and names the two stages.
> - Have the lawyer reviewing `docs/LICENSE-DRAFT.md` look at this record too.

Derived requirements (from callers, repo rules and the critiques; none from the old code):
- **R-1 (failing safely):**
  - On input it can't read, the constructor throws only `ArgumentException` (or a subclass), `IndexOutOfRangeException` or `OverflowException`. It never throws `EndOfStreamException`, `InvalidDataException` or `NullReferenceException`.
  - Every count read from the file (races, links, bones) is checked against the bytes left before anything is allocated from it. There must be no `OutOfMemoryException` and no giant allocation.
  - Where this differs from the old reader, the spec marks it "Required change from the old reader:".
- **R-2 (threads):** a `PbdFile` is immutable after construction and safe to share across threads. UI windows cache it, and background jobs (`HairPortWatcher`, `UvTransferBakeJob`, `AutoRefitWatcher`) use it.
- **R-3 (speed):** parsing happens once, in the constructor. `Chain` stays cheap enough to call every frame (`LiveBrush` and `LiveMeshOverlay`).
- **R-4 (commits):** commits are authored as Solona, with no Co-Authored-By trailer and no real name. This is the user's standing rule, and it overrides the harness attribution reminder.

## Contract
None. This is a pure library replacement: the public C# surface above is pinned exactly, and no route, UI, schema or persisted field changes.

## Approach
1. **Planner and both plan-critics** (done). Their reads go into the log.
2. **Agent A: the spec.**
   - **Briefing:** the doc, its allowed-reading list, R-1, the A5 forbidden list for everything else, and A's own folder `…\scratchpad-agentA\` (a sibling of the session scratchpad) for notes and probe output.
   - **Required headings:**
     - `## 1. File layout`
     - `## 2. A deformer block`
     - `## 3. Bad input`
     - `## 4. The race chain`
     - `## 5. One bone through a chain`
     - `## 6. Conventions`
     - `## 7. Test vectors`
   - **Form:**
     - Data tables (offset, width, signedness, meaning) and rules.
     - No step-by-step procedures in the old code's order.
     - No ``` fences.
   - **§3:** for each bad-input case, says whether the reader skips the entry, gives an empty chain, or throws (and with which exception type).
   - **Markers:** a **"Required change from the old reader:"** marker goes only where R-1 requires behaviour the old reader lacks, or where the doc's §3 says the reader "must tolerate" something the old reader does not. Each marker names its R-1 clause or doc §3 item.
   - **§7 contains:**
     - the comparison rule: absolute tolerance 1e-5 per element, with values given to 6 dp;
     - chain lengths for at least 6 race pairs: same race, a race 0, a race absent from the file, a two-step pair, a cross-sex pair, and one more;
     - the explicit parent table;
     - all 16 elements for at least 4 (pair, bone) cases, covering a bone listed directly, a bone found only through an ancestor, and a bone with nothing listed;
     - `Lists` true/false for each case.
   - **Probe:** to compute the values, A adds a temporary test file in this worktree, `Proteus.Tests/PbdSpecProbeTests.cs`. It calls only the public API, uses `[LocalDataFact]`, and writes its output to A's folder. A runs it with a targeted filter, deletes it before hand-over, and names it in its self-report.
   - **Identifier self-check (A8):** A extracts the old file's private identifiers and comment strings into A's folder, greps the spec for them (public API names and ordinary English words excepted), and records only the hit count, expected 0.
   - **Hand-over:** A writes `docs/pbd-format-spec.md` and the Agent A section of `docs/pbd-clean-room-log.md`, then deletes A's folder.
   - **Orchestrator checks** (from names only, never contents):
     - `git status` shows only those two files, with no probe file;
     - A's folder is gone (`test -d`);
     - the spec has no ``` fences;
     - every marker cites R-1 or a doc §3 item (any other marker goes back to A).

     Then it commits.
3. **Orchestrator: tests from the spec, proven on the old code.**
   - **a. Pre-flight.**
     - Check that `PROTEUS_GAME`, `PROTEUS_MODS` and `PROTEUS_MODPACKS` are set and that `game/sqpack` exists under `PROTEUS_GAME`.
     - Write the `RealFile` tests, then run the never-skip set alone with verbosity normal:

       ```
       --filter "FullyQualifiedName~PbdFileTests+RealFile|FullyQualifiedName~RacialRouteLocalTests|FullyQualifiedName~LiveMeshTests"
       ```

     - It must show 0 skipped. A skip here is a blocker, logged, and step 3 does not go on.
   - **b. Write the rest:**
     - the synthetic edge tests: bytes built with `HairPortFixtures.BuildPbd`, then fields patched at the offsets spec §1 gives;
     - the NewBehaviour tests, tagged `[Trait("CleanRoom","NewBehaviour")]`;
     - `PbdCleanRoomRecordTests.cs`.
   - **c. The old-code gate.** One run:

     ```
     dotnet test Proteus.Tests/Proteus.Tests.csproj -c Release --logger "console;verbosity=normal" --filter "(<WIDE>)&CleanRoom!=NewBehaviour&FullyQualifiedName!~PbdCleanRoomRecordTests"
     ```

     `<WIDE>` is:

     ```
     FullyQualifiedName~PbdFileTests|FullyQualifiedName~RacialModelBakeTests|FullyQualifiedName~RacialRoute|FullyQualifiedName~OtherRaceWearerTests|FullyQualifiedName~OtherRaceRefit|FullyQualifiedName~HairPort|FullyQualifiedName~HairScalpPushTests|FullyQualifiedName~SkeletonRemapTests|FullyQualifiedName~LiveMeshTests|FullyQualifiedName~KeepEndsLocalTests|FullyQualifiedName~UvBakeStep0LocalTests|FullyQualifiedName~UvTransferMapsLocalTests|FullyQualifiedName~EarlessHairWatcherTests|FullyQualifiedName~EarlessHairTests|FullyQualifiedName~CrossSexRefit|FullyQualifiedName~CrossSexEndsLocalTests|FullyQualifiedName~CrossSexGloveReshapeLocalTests|FullyQualifiedName~NudeLegsLocalTests|FullyQualifiedName~SizeSetCrossSex|FullyQualifiedName~SizeSetGameCopyLocalTests|FullyQualifiedName~SizeSetMadeOnLocalTests|FullyQualifiedName~HandsFeetPort|FullyQualifiedName~EarringBones|FullyQualifiedName~MatcapHairPortTests|FullyQualifiedName~UvTransferBake|FullyQualifiedName~SkeletonRemapLocalTests|FullyQualifiedName~BodyRetargetWriterTests|FullyQualifiedName~AutoRefitTargetsTests|FullyQualifiedName~SourceBodyPickerTests|FullyQualifiedName~StudioBodyChoiceTests|FullyQualifiedName~HarnessHairPortTests
     ```

     The first 12 terms are the doc's. Classes confirmed by grep for the rest:
     - CrossSexRefitTests, CrossSexRefitLocalTests, CrossSexRefitVanillaLocalTests;
     - CrossSexEndsLocalTests, CrossSexGloveReshapeLocalTests, CrossSexNudeLegsLocalTests, TbseXNudeLegsLocalTests;
     - SizeSetCrossSexTests, SizeSetCrossSexLocalTests, SizeSetGameCopyLocalTests, SizeSetMadeOnLocalTests;
     - HandsFeetPortTests, HandsFeetPortLocalTests, HandsFeetPortOutfitLegsTests;
     - EarringBonesTests, EarringBonesLocalTests;
     - EarlessHairTests, EarlessHairWatcherTests;
     - MatcapHairPortTests, UvTransferBakeTests, UvTransferBakeLocalTests, SkeletonRemapLocalTests;
     - BodyRetargetWriterTests, AutoRefitTargetsTests, SourceBodyPickerTests, StudioBodyChoiceTests;
     - HarnessHairPortTests (in `Proteus.Tests/TestHarness/`).
   - **d. Gate rules:**
     - Every new non-NewBehaviour test passes.
     - The never-skip set ran. The orchestrator names, in the log, at least one cross-race test in `CrossSexRefitLocal*` and one in `KeepEndsLocal*` that ran.
     - Every skip is either a static `Skip =` (the `EarringBonesLocalTests` diagnostic) or a named missing-mod skip whose reason names a mod or pack under `PROTEUS_MODS` or `PROTEUS_MODPACKS`. A `PROTEUS_GAME` skip is never allowed.
     - Any existing test that fails is a baseline failure, recorded with its full assertion message.
     - Spec-caused failures are handled as in Decision 2.
   - **e. Log:** counts, the skip list by name and reason, the baseline failures with their messages, the named never-skip tests and the NewBehaviour list. Commit.
4. **Orchestrator: delete.**
   - `git rm XivLiveMesh/PbdFile.cs`, then commit "Remove the Meddle-derived racial deformer reader ahead of its clean-room replacement". Never view the diff.
   - Run the guard (Decision 16). The count must be 0.
   - Delete `bin/` and `obj/` in `XivLiveMesh`, `Proteus`, `Proteus.Tests` and `Proteus.Refit`.
   - The build is broken until step 5.
5. **Agent B: implementation** (fresh sub-agent).
   - **Briefing:**
     - the doc and the spec;
     - R-1 to R-3;
     - the allowed list: the spec, the doc, the callers in `Proteus/` and `XivLiveMesh/`, and `Proteus.Tests/`;
     - the full forbidden list (Decision 5);
     - no git commands;
     - every search scoped to this worktree, with `Glamourer/`, `Penumbra.Api/`, `bin/` and `obj/` excluded.
   - **B writes:** `XivLiveMesh/PbdFile.cs` with the AC-4 header. B may build (`dotnet build Proteus/Proteus.csproj -c Release`) and run targeted filters, including the NewBehaviour tests.
   - **B's narrative** for the log is short. The orchestrator builds B's lists and its count of forbidden strings in results from B's transcript (Decision 8), then commits.
6. **Proof on the new code.**
   - Run the step 3c command without `CleanRoom!=NewBehaviour`.
   - It must show:
     - everything passing, except step 3 baseline failures with the same message word for word;
     - a skip list identical to step 3's, by name and reason;
     - the never-skip set and the named cross-race tests ran.
   - Run the goldens once, as the doc requires (they don't exercise the deformer):

     ```
     --filter "FullyQualifiedName~GoldenTests|FullyQualifiedName~GoldenCrossBodyLocalTests|FullyQualifiedName~SkinTuckGoldenTests"
     ```

   - Log the counts and the skip list.
   - Failures go to B (SendMessage) as the test name, its assertion output and the spec section involved.
   - Never re-bless a golden, never edit an existing test, never weaken a new test.
7. **Paperwork** (orchestrator).
   - `XivLiveMesh/NOTICE.md`: replace the *Third-party sources* table with a line saying no third-party source files remain. Keep the FFXIVClientStructs paragraph, the research-references paragraph and the AGPL line.
   - `THIRD_PARTY_NOTICES.txt`: remove the Meddle block and one adjoining `----` separator.
   - Complete the orchestrator log section, plus the top note from Decision 8.
   - Commit. (Record tests: see step 8.)
8. **code-critic** (opus, clean side, Decision 4).
   - It gets the step 6 summary, with the full suite deferred to release.
   - Findings on `PbdFile.cs` go to B. Findings on tests and paperwork go to the orchestrator. Any commit after the review means a re-review.
   - Once it passes, the orchestrator extracts the critic's lists and its count of forbidden strings in results (a void review is re-run).
   - Then it makes the **final commit**: the Code-critic log section plus the `Plugin.BuildNumber` bump to the next free number on `test`.
   - Then it runs `--filter "FullyQualifiedName~PbdCleanRoomRecordTests"`, which must pass.
9. **Short re-review of the final commit** (Decision 14).
   - It reads only `git show --stat HEAD` and the diffs of those two files.
   - Its extracted reads, with the forbidden-strings count, are kept for the merge message.
   - A finding means: fix it, redo the final commit, and re-review.
10. **Build for the game** (Rule 5).
    - `git submodule update --init --recursive`, then `dotnet build Proteus/Proteus.csproj -c Release`.
    - Warn other sessions that the plugin is about to reload.
    - Copy `Proteus/bin/Release/win-x64/` to `E:\repos\Proteus\Proteus\bin\release\win-x64`, writing only, and check the copied `Proteus.dll` timestamp by `ls -l` of that one file.
    - In-game: UNVERIFIED.
11. **Merge into `test`** from the primary checkout, under Decision 15.
    - **a.** `git pull` on `test` in `E:\repos\Proteus`.
    - **b.** Run the guard. A count above 0 means stop (Decision 16).
    - **c.** `git merge --no-ff solona-m/pbd-clean-room`. The message:
      - says it is a clean-room replacement, and names the two stages: stage 1, the spec, by an agent that read the old code; stage 2, the implementation, by a fresh agent that never saw it;
      - points at `docs/pbd-format-spec.md` and `docs/pbd-clean-room-log.md`;
      - says the Meddle notices were removed;
      - includes the step 9 re-review's reads;
      - has no Co-Authored-By.
    - **d.** If the merge conflicts on the path, run `git checkout --theirs XivLiveMesh/PbdFile.cs` (the feature branch's side) without viewing the conflict.
    - **e.** Straight after the merge, run `--filter "FullyQualifiedName~PbdCleanRoomRecordTests"` in the primary checkout.
    - **f.** Run the quick suite (`--filter "Speed!=Slow"`) once.
    - **g.** Push `test` per the user's auto-mode rules: check `.github/workflows` and the hosting config first.
    - **h.** Final report:
      - decisions made;
      - the baseline-failing deformer tests, as gaps in the proof;
      - UNVERIFIED items;
      - the owner's next step: the lawyer's review of the record.

## Files
- NEW    `docs/pbd-format-spec.md`: Agent A's spec of facts only: seven headings, tables and rules, departure markers.
- NEW    `docs/pbd-clean-room-log.md`:
  - the top note about the final re-review;
  - a section per participant;
  - B's and the critic's lists from their transcripts, with their counts of forbidden strings in results;
  - run counts, skip lists, baseline failures, guard counts and the NewBehaviour list.
- DELETE `XivLiveMesh/PbdFile.cs` (the orchestrator's commit), then NEW `XivLiveMesh/PbdFile.cs` (B's commit): the clean-room reader, with the same public surface.
- EDIT   `Proteus.Tests/PbdFileTests.cs`: additions only. Synthetic edge tests, NewBehaviour tests, and the nested `RealFile` class with the §7 vectors.
- NEW    `Proteus.Tests/PbdCleanRoomRecordTests.cs`: the spec, log, header, public surface (by reflection) and notices tests.
- EDIT   `XivLiveMesh/NOTICE.md`: remove the `PbdFile.cs` third-party row.
- EDIT   `THIRD_PARTY_NOTICES.txt`: remove the Meddle entry.
- EDIT   `Proteus/Plugin.cs`: bump `BuildNumber`, in the final commit only.
- No caller file changes. A diff in any of them is an undiscussed change.
- Not committed: the transcript-extraction script (session scratchpad), A's probe (deleted before hand-over) and A's folder (deleted).

## Out of Scope
- Switching to the Proteus Licence or `LicenseRef-Proteus`.
- Asking the Meddle authors for permission.
- Re-checking where other files came from.
- Editing `docs/pbd-clean-room.md`.
- The lawyer's review (the owner's next step).
- A performance benchmark gate (only R-3 applies).
- A resemblance check of the new file against the old.
- Adversarial QA in game.
- A fallback for the NIRÆ parse failure (stays deferred).
- A `.pbd` writer.
- Fixing failures that already happen on the old code. They are recorded and reported as gaps, and become `known-issue` tasks if real.

## Edge Cases
The expected results for EDGE-2, 3, 4, 5, 10, 11, 12 and 14 come from the spec. The handling below is only their shape.
- EDGE-pbd-clean-room-1: name offsets above 32767 (a deformer of about 700 bones). They are read as unsigned, so every name resolves.
- EDGE-pbd-clean-room-2: a truncated or garbage file: empty, header cut short, a count of -1, a count larger than the file, a block cut mid-matrix, a last name with no terminator. The reader either tolerates it as spec §3 says, or throws only an R-1 type. Marked departures are NewBehaviour tests.
- EDGE-pbd-clean-room-3: a race entry whose block offset is 0, negative, or past the end of the file. Handled as §3 says, and other races still resolve.
- EDGE-pbd-clean-room-4: two entries sharing one block. Handled as §3 says.
- EDGE-pbd-clean-room-5: a link's parent or entry index out of range. Handled as §3 and §4 say.
- EDGE-pbd-clean-room-6: a loop in the parent links. `Chain` finishes and returns empty ("no chain").
- EDGE-pbd-clean-room-7: the same race, or either race 0. `Chain` returns empty ("no bending").
- EDGE-pbd-clean-room-8: a race not in the file, or the source never reached. `Chain` returns empty.
- EDGE-pbd-clean-room-9: a bone that is not listed but has a listed ancestor uses its nearest listed ancestor's matrix. A bone with nothing listed contributes nothing, and `Lists` returns false.
- EDGE-pbd-clean-room-10: `parentOf` cycles (a→b→a) or runs very deep. The depth guard ends the walk with the §5 result: no hang, no stack overflow.
- EDGE-pbd-clean-room-11: a bone name listed twice in one block. Handled as §2 says.
- EDGE-pbd-clean-room-12: an empty chain. `Resolve` and `Lists` return what §5 says.
- EDGE-pbd-clean-room-13: one instance shared across threads. Concurrent calls give identical results (R-2).
- EDGE-pbd-clean-room-14: name bytes outside ASCII. Handled as §2 says.
- EDGE-pbd-clean-room-15: the old-code gate. Three things must hold before the deletion:
  - new non-NewBehaviour tests are green on the old code;
  - the never-skip set and the named cross-race tests ran, and every skip is an allowed kind;
  - existing failures are recorded with their messages.

  A spec error goes back to A first.
- EDGE-pbd-clean-room-16: golden output drifts at float-noise level. Fixed from the order in §6, never by re-blessing a baseline.
- EDGE-pbd-clean-room-17: B or the code-critic opens something forbidden, a tool result contains a forbidden string, or it runs a diff or history command on the old path without the exclusion. B's file is discarded and a new B starts, or the review is re-run fresh. The void is logged.
- EDGE-pbd-clean-room-18: a count far larger than the bytes left (for example `0x7FFFFFFF` in an 8-byte file, or a block's bone count running past the end). The reader refuses with an R-1 exception before allocating anything. This is a NewBehaviour test if §3 marks it.
- EDGE-pbd-clean-room-19: a skip appears on the new-code run that was not on the old-code run, or a never-skip test skips. That is a failure of AC-5 or AC-6, never a pass.

## Test Strategy
All new tests carry `[Trait("Plan", "<ID>")]`. Departure tests also carry `[Trait("CleanRoom","NewBehaviour")]`.
- [AC-pbd-clean-room-1] `PbdCleanRoomRecordTests.Spec_has_every_required_section_and_no_code`:
  - the seven headings appear in order;
  - there is no ``` fence;
  - §6 has a worked example with numbers;
  - §7 states the 1e-5 tolerance and the parent table;
  - every "Required change from the old reader:" line cites `R-1` or a doc §3 item (offset 0/negative/past end, shared block, link or parent out of range).
- [AC-pbd-clean-room-2] [EDGE-pbd-clean-room-17] `PbdCleanRoomRecordTests.Log_lists_every_participant_and_the_clean_side_kept_clear`:
  - **Header and sections:** the top note says the final re-review is in the merge commit message. The sections are Planner, Plan-critic 1, Plan-critic 2, Agent A, Orchestrator, Agent B and Code-critic, each with `Opened:`.
  - **Agent A:** labelled a self-report, with an identifier-grep count of 0.
  - **B and code-critic paths:** after normalising (`\` to `/`, lowercase), every `Opened:` entry is either repo-relative (no drive letter, no leading `/`, no `..`) or a tool path. Tool paths are under `c:/users/solon/.claude/plugins/`, or contain `/temp/claude/` and `/scratchpad/` but not `scratchpad-agenta`. The allowed set is built at runtime, not from a session path.
  - **Forbidden markers:** no entry contains `glamourer/`, `penumbra.api/`, `/bin/`, `/obj/`, `meddle.utils`, `skintattoo`, `draganddroptexturing`, `/memory/` or `scratchpad-agenta`.
  - **Result counts:** each section reports "forbidden strings in tool results: 0".
  - **Git commands:** B's list is "none". Every critic git command that contains `diff`, `log -p`, `show` or `blame` either has the `:!XivLiveMesh/PbdFile.cs` exclusion, or is `git show HEAD:XivLiveMesh/PbdFile.cs` or `--stat`.
- [AC-pbd-clean-room-3] `PbdCleanRoomRecordTests.Public_surface_is_exactly_the_documented_one`: by reflection, a public sealed class with exactly the constructor `(ReadOnlySpan<byte>)`, the instance method `Chain(ushort, ushort)`, the static `Resolve` and `Lists` with the documented signatures, and no other public declared members.
- [AC-pbd-clean-room-4] `PbdCleanRoomRecordTests.Header_names_the_spec_not_a_port`:
  - line 1 is `// SPDX-License-Identifier: AGPL-3.0-or-later`;
  - line 2 is `// Copyright (c) Solona`;
  - `docs/pbd-format-spec.md` appears in the first 5 lines;
  - no `ported from` (case-insensitive), `Meddle` or `SkinTattoo`.
- [AC-pbd-clean-room-5] [EDGE-pbd-clean-room-19] The step 6 wide run on the new code, with these checks:
  - all tests pass, except step 3 baseline failures, which fail with the same message word for word;
  - the skip list matches step 3's by name and reason;
  - the never-skip set and the named cross-race tests in `CrossSexRefitLocal*` and `KeepEndsLocal*` ran;
  - `git diff test --numstat -- Proteus.Tests/PbdFileTests.cs` shows 0 deleted lines;
  - no other existing test file appears in `git diff test --stat`;
  - the counts are logged.
- [AC-pbd-clean-room-6] Vector tests, which never skip:
  - `PbdFileTests.RealFile.Chain_lengths_match_the_spec`: a theory over the §7 pairs;
  - `PbdFileTests.RealFile.Resolved_matrices_match_the_spec`: all 16 elements, `Math.Abs(expected - actual) <= 1e-5`, using the §7 parent table;
  - `PbdFileTests.RealFile.Lists_matches_the_spec`.

  They pass in the step 3a pre-flight, step 3c (old code) and step 6 (new code).
- [AC-pbd-clean-room-7] [EDGE-pbd-clean-room-16] The golden run (`GoldenTests|GoldenCrossBodyLocalTests|SkinTuckGoldenTests`), once on the owner's machine, as the doc requires. It passes, with no golden baseline in `git diff test --stat`. It is never described as a proof that the deformer behaves the same.
- [AC-pbd-clean-room-8] `PbdCleanRoomRecordTests.Notices_no_longer_name_Meddle`: there is no `Meddle` in either file, and no `PbdFile.cs` row under *Third-party sources*.
- [EDGE-pbd-clean-room-1] `PbdFileTests.Reads_a_deformer_whose_bone_names_sit_past_32_KB` (existing, unchanged).
- [EDGE-pbd-clean-room-2] `PbdFileTests.A_damaged_file_follows_the_spec`: a theory over the six damaged inputs. Each one either loads as §3 says or throws an `ArgumentException`, `IndexOutOfRangeException` or `OverflowException`. The departure cases go in a NewBehaviour theory. Also covered by the existing `OtherRaceWearerTests.A_damaged_deformer_is_refused_not_thrown`.
- [EDGE-pbd-clean-room-3] `PbdFileTests.A_bad_block_offset_follows_the_spec`: offsets patched to 0, negative and past the end. Asserts the §3 outcome, and that another race's chain is unchanged.
- [EDGE-pbd-clean-room-4] `PbdFileTests.Two_races_sharing_a_block_follow_the_spec`.
- [EDGE-pbd-clean-room-5] `PbdFileTests.A_link_out_of_range_follows_the_spec`.
- [EDGE-pbd-clean-room-6] `PbdFileTests.A_loop_in_the_tree_gives_no_chain`: finishes, and the chain is empty.
- [EDGE-pbd-clean-room-7] `PbdFileTests.Same_race_or_race_zero_needs_no_bending`.
- [EDGE-pbd-clean-room-8] `PbdFileTests.An_unknown_race_or_unreached_source_has_no_chain`.
- [EDGE-pbd-clean-room-9] `PbdFileTests.An_unlisted_bone_takes_its_nearest_listed_ancestor` and `PbdFileTests.A_bone_with_nothing_listed_follows_the_spec`, built from synthetic translations.
- [EDGE-pbd-clean-room-10] `PbdFileTests.A_parent_cycle_follows_the_spec`: returns within a time limit, with the §5 result.
- [EDGE-pbd-clean-room-11] `PbdFileTests.A_name_listed_twice_follows_the_spec`.
- [EDGE-pbd-clean-room-12] `PbdFileTests.An_empty_chain_follows_the_spec`.
- [EDGE-pbd-clean-room-13] `PbdFileTests.One_instance_gives_the_same_answers_across_threads`: `Parallel.For`, compared with a single-threaded run.
- [EDGE-pbd-clean-room-14] `PbdFileTests.A_non_ascii_name_follows_the_spec`.
- [EDGE-pbd-clean-room-15] The step 3a pre-flight and the step 3c gate run before the deletion commit. Their results are logged: the skip list and reasons, the named never-skip tests, the baseline messages, and the NewBehaviour list.
- [EDGE-pbd-clean-room-18] `PbdFileTests.A_count_larger_than_the_file_is_refused_before_allocating`: throws an R-1 type, with `GC.GetAllocatedBytesForCurrentThread` growing by less than 1 MB. Tagged NewBehaviour if §3 marks it.

## Environment & Preconditions
- **Real-data tests:** they need `PROTEUS_GAME`, `PROTEUS_MODS` and `PROTEUS_MODPACKS` resolving on the owner's machine, which the step 3a pre-flight checks. On CI they skip, so the proof runs must be local.
- **Submodules:** run `git submodule update --init --recursive` before building. It brings in Penumbra's GPL `PbdFile.cs` under `Glamourer/`, which is excluded from every clean-side search.
- **Broken build:** between step 4 and step 5 the solution doesn't build, and `bin/` and `obj/` are gone. If the first build after B fails for a reason other than B's code (for example a native `proteus_bcn.dll` that isn't restored), stop and diagnose (Rule 2).
- **Dev-load folder:** never copy there just to test. Only the finished build is copied, after warning other sessions, and the folder is write-only for the clean side.
- **`E:\repos\Proteus`:** commands only, no file opened (Decision 15).
- **Planner's reads, for the log.** The planner stayed on the clean side throughout. It never opened the old file, its history, the submodules, other checkouts, the memory topic files or A's folder. It opened no URLs and ran no show, log -p, diff or blame.
  - **Files read:**
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
  - **Listings:** `docs/`, `docs/adr`, `docs/agent-rules`, and `Proteus.Tests/` filtered by name.
  - **Searches** (all excluding `PbdFile.cs`, `Glamourer/`, `Penumbra.Api/`, `bin/`, `obj/` and `.git`):
    - `PbdFile`, `Chain`, `Resolve` and `Lists`;
    - "Meddle";
    - test class names, including `GloveReshape`, `NudeLegs` and `HarnessHairPort`;
    - `Skip =` across `Proteus.Tests`.
  - **Git:** `git log --oneline -- docs/pbd-clean-room.md`.
- **Plan-critic 1's reads, for the log:**
  - **Files read:**
    - `docs/pbd-clean-room.md`
    - `docs/agent-rules/plan-critic.md`
    - `OtherRaceWearer.cs` lines 120–154
    - `UvTransferBakeJob.cs` lines 190–229
    - `LiveMeshPoser.cs` lines 38–127
    - `PbdFileTests.cs`
    - `HairPortFixtures.cs` lines 30–109
    - `XivLiveMesh/NOTICE.md`
  - **Listings:** `docs/adr`, `docs/product-context`, `.github/workflows`.
  - **Searches:** git grep with the exclusions only. No show, log, diff or blame.
- **Plan-critic 2's reads, for the log:**
  - **Files read:**
    - `scratchpad\plan.md`
    - `docs/pbd-clean-room.md`
    - `docs/agent-rules/plan-critic.md`
    - `Proteus.Tests/LocalData.cs` lines 20–193
  - **Listings:** `Proteus/uvmaps/`.
  - **Searches** (git grep only):
    - `PROTEUS_*` and `LocalDataFact` in `LocalData.cs`;
    - output-path properties in the props, targets and csproj files;
    - `XivLiveMesh` references in the csproj, sln and slnx files;
    - `Skip` patterns in `Proteus.Tests`;
    - ±3 lines of context in `EarringBonesLocalTests`, `SizeSetMadeOnLocalTests` and `CrossSexGloveReshapeLocalTests`;
    - `LocalData(Fact|Theory)` in 8 local test classes;
    - the `BuildPbd`, `Pbd` and `HighlanderPbd` signatures.
  - No show, log, diff or blame.

## NEEDS_DECISION
None. Both earlier items are decided (Decisions 11 and 12).

## Risks
- **Old code leaking to the clean side.** Possible routes:
  - the deletion's diff, or a full `git diff test` given to the critic;
  - the submodule's GPL file;
  - `bin/` and `obj/`;
  - other checkouts, the memory topic files, or A's folder;
  - the conflict content during the merge;
  - private identifiers in A's spec.

  Mitigations:
  - Decisions 4–6, 8 and 15;
  - deleting the build folders;
  - counting forbidden strings in tool results (B4);
  - A's identifier grep.

  The orchestrator can't check identifiers itself; the log states that limit.
- **The exception contract (R-1).** A `BinaryReader` throws `EndOfStreamException`, and an unchecked count can allocate gigabytes. Either one turns a refusal into an error thrown every frame, or a crash. Pinned by EDGE-2, EDGE-18 and an existing test.
- **Float order.** Products that are equal in exact maths can differ in the last float digits depending on how they're grouped. The 1e-5 tolerance absorbs that noise but still catches a wrong order, so §6 must state the order exactly.
- **Spec gaps.** If B finds something the spec doesn't cover, B asks through the orchestrator, A amends the spec (re-committed), and the log records it.
- **Fixture early returns.** `SizeSetMadeOnLocalTests` and `CrossSexGloveReshapeLocalTests` set up nothing when data is missing. Their tests skip through their own attributes, and the skip-list comparison (B1) catches any change.
- **Changes landing on `test` during the work.** The count guard, run after a `git pull`, stops a merge that would silently drop a fix made to the old file on `test`.
- **Failures already present on the old code.** Recorded with their messages, so they are neither blamed on nor excused for the new code. Deformer tests among them are reported as gaps in the proof.
- **Single responsibility.** `PbdFile` mixes parsing with chain and bone maths. The fixed public surface requires that, so it is accepted; B may split private helpers within the file.
- **Process departure.** B, not the main agent, writes the production code. The doc requires it, and it is noted in the PR description and the *Review & traceability* section.
- **High scrutiny.** This reader decides where other-race vertices land in files that the Sensitive-Area writers put into users' mods. That is why the wide real-data run with its skip-list comparison is mandatory.
- **Left to the owner.** Other-race bending in game is UNVERIFIED, and the lawyer's review of the record is the owner's next step.

Relevant paths:
- E:\orca\workspaces\Proteus\meddle\docs\pbd-clean-room.md
- E:\orca\workspaces\Proteus\meddle\Proteus.Tests\PbdFileTests.cs
- E:\orca\workspaces\Proteus\meddle\Proteus.Tests\HairPortFixtures.cs
- E:\orca\workspaces\Proteus\meddle\Proteus.Tests\LocalData.cs
- E:\orca\workspaces\Proteus\meddle\Proteus.Tests\OtherRaceWearerTests.cs
- E:\orca\workspaces\Proteus\meddle\Proteus.Tests\EarringBonesLocalTests.cs
- E:\orca\workspaces\Proteus\meddle\Proteus.Tests\SizeSetMadeOnLocalTests.cs
- E:\orca\workspaces\Proteus\meddle\Proteus.Tests\CrossSexGloveReshapeLocalTests.cs
- E:\orca\workspaces\Proteus\meddle\Proteus\Services\OtherRaceWearer.cs
- E:\orca\workspaces\Proteus\meddle\Proteus.Tests\ReadmeTermsTests.cs
- E:\orca\workspaces\Proteus\meddle\XivLiveMesh\NOTICE.md
- E:\orca\workspaces\Proteus\meddle\THIRD_PARTY_NOTICES.txt
- E:\orca\workspaces\Proteus\meddle\Proteus\Plugin.cs