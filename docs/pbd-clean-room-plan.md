# Hand-off: clean-room rewrite of `XivLiveMesh/PbdFile.cs`, ready to run

This is the approved plan for carrying out `docs/pbd-clean-room.md`, written for the agent that runs it.
Everything after the next rule is the plan itself (revision 6). The planner wrote it, the plan-critic
reviewed it in four rounds (the third with a re-check), and every amendment was folded back in through the planner.

## Where things stand (2026-11-16)

- **Branch and worktree:** `solona-m/pbd-clean-room`, cut from `test` in `E:\orca\workspaces\Proteus\meddle`.
  Submodules are initialised. This hand-off file is the only commit on the branch. No spec, test or code
  work has started.
- **Plan reviews:**
  - Round 1: the critic rated it MEDIUM-HIGH. Its 12 amendments were folded in as revision 2.
  - Round 2: the critic rated it MEDIUM. Its 11 amendments were folded in as revision 3.
  - Round 3: the critic rated revision 3 MEDIUM-HIGH (2 blocking findings, 3 suggestions). All 5 were folded in as revision 4.
  - Round 3 re-check: the critic rated revision 4 MEDIUM-HIGH (1 blocking finding, 1 cheap one, 1 suggestion). All 3, plus one orchestrator finding, were folded in as revision 5.
  - Round 4: the critic rated revision 5 MEDIUM-HIGH (1 cheap blocking finding). It was folded in as revision 6 (the Delta below), together with a correction of a fact the orchestrator had given for revision 5.
  - **First step:** run
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
  - anything under `Glamourer/` (it holds Penumbra's GPL `Penumbra.GameData/Files/PbdFile.cs`), `Penumbra.Api/`, `Luna/` (a third submodule, Ottermandias's), `bin/` or `obj/`;
  - `E:\repos\Proteus` files, other Proteus worktrees, the dev-load folder or `E:\repos\DragAndDropTexturing`;
  - the memory topic files under `C:\Users\solon\.claude\projects\*\memory\` (several describe the old reader);
  - Agent A's folder or transcript.
- **Searches:** exclude every path above from every search. Use `git grep` with
  `':!XivLiveMesh/PbdFile.cs' ':!Glamourer' ':!Penumbra.Api' ':!Luna'`. Never run a bare repo-wide grep for `PbdFile`.
- **Fixes:** only Agent A ever reads the old code. Every fix to the new `PbdFile.cs` goes back to Agent B.

## Log inputs from the planning session

The plan's *Environment & Preconditions* section lists what the planner (revisions 1–3, 4, 5 and 6) and the plan-critics (Plan-critic 1–3, Plan-critic 3's re-check of revision 4, and Plan-critic 4's check of revision 5) opened. The log's Planning orchestrator section copies the reads below verbatim (Decision 8).

The planning orchestrator also opened only:

- `docs/pbd-clean-room.md` (both copies, compared with `git diff --no-index --stat`);
- the doc's copy under `readme-update`;
- AGENTS.md and CLAUDE.md, loaded into context automatically from the session's start folder `E:\orca\workspaces\Proteus\E-orca-workspaces-Proteus-meddle-docs-pbd-clean-room-plan.md` (a separate checkout of the same repo, not `meddle`).

It also ran:

- a `Grep` count of `PbdFile` across the worktree, which excluded the old file but **did** list file names
  under `Glamourer/Penumbra.GameData/Files/` (names and counts only, no content);
- `git log --oneline test..HEAD` and `HEAD..test`, `git switch -c`, and `git submodule update`.

The orchestrator never opened the old file.

## Practical notes

- Start the keep-awake script with the **PowerShell** tool, not Bash, with `timeout: 7200000`. Bash strips the
  backslashes from the path, and the default timeout kills the script after 30 minutes.
- Sub-agent transcripts sit next to the session as JSONL. Point the extraction script at B's and the
  code-critic's transcript files by agent ID only, so it never opens A's. A small script that prints only
  the last assistant text, or only the `tool_use` inputs, is enough for Decision 8's extraction. Never print
  tool results. For Grep and Glob calls, print only the count of result lines that are paths inside a
  forbidden location (Decision 8). A relative path, or no `path`, counts as a hit. Also print every shell
  command that is outside the sender's allowlist. Build and test output and reads of allowed files are not counted.
- Sub-agents start in the session's start folder, not necessarily in `meddle`. For the planning session that was
  `E:\orca\workspaces\Proteus\E-orca-workspaces-Proteus-meddle-docs-pbd-clean-room-plan.md`, a separate checkout.
  Brief B, the code-critic and the re-reviewer to use absolute `E:/orca/workspaces/Proteus/meddle/...` paths.

---

# Implementation Plan: Clean-room rewrite of the racial deformer reader (`XivLiveMesh/PbdFile.cs`)

## Delta (rev 6: Plan-critic 4's check of revision 5 (1 cheap blocking finding) and a corrected fact, all accepted by the orchestrator)
- **E1 (parsing results by output mode):**
  - The gap: D3 parsed only content-mode lines that have line numbers. Grep's count mode prints `path:N`, and content mode with `-n: false` prints `path:text`. Both fell back to "a line with no path part counts as the tool's `path` input", so a hit inside `Glamourer/` could count as allowed.
  - The script now parses each result line by output mode:
    - Glob, or Grep in `files_with_matches` mode: the whole line;
    - Grep in `count` mode: the part before the trailing `:<digits>`;
    - Grep in `content` mode with line numbers: the part before the first `:<digits>:` (as D3 says).
  - A content-mode Grep with `-n: false` is outside what B and the critic may do. It voids the work (EDGE-17), and it is in their briefings.
  - The "no path part" fallback applies only to a search of a single file. In any other search, a line whose path can't be parsed counts as a hit.
  - Changed in: Decisions 4 and 8, step 5, EDGE-17 and the record test.
- **E2 (a corrected fact):**
  - Revision 5 wrongly said that Plan-critic 3's `ls` and `git -C … grep` on `Luna/` ran in the session's start folder.
  - The commands in its transcript are `ls E:/orca/workspaces/Proteus/meddle/Luna` and `git -C E:/orca/workspaces/Proteus/meddle/Luna grep -l -i -e pbd -e deformer`. Both use absolute `meddle` paths. The orchestrator's extraction script had shortened them to repo-relative form, which made them look relative.
  - They ran on `meddle`'s `Luna/`: file names only, no hits and no code, before revision 4 forbade it.
  - The general D4 rule stays as a precaution, because the start-folder fact itself is true. B, the code-critic and the re-reviewer use absolute `meddle` paths.
  - New log rule: shell commands are recorded verbatim, never shortened to repo-relative form, because shortening is what hid this.
  - Changed in: the start-folder fact, Decision 8's extraction, and Plan-critic 3's reads.
- **E3 (log inputs):** "Plan-critic 4 (check of rev 5)" and "Planner (rev 6)" are added under Environment & Preconditions, to the "at least these sections" lists, and to the hand-off's plan-review lines.
- **Earlier revisions** (still in force):
  - rev 2 folded in the first critic's 12 amendments: wide filter, old-code baseline, NewBehaviour trait, 1e-5 tolerance, A5 forbidden list, deleting `bin/`/`obj/`, transcript-based logs, A's identifier grep, the guard, R-1 count checks, bump after review, golden wording;
  - rev 3 folded in the second critic's 11 amendments:
    - allowed skips, the never-skip set and a pre-flight (B1);
    - one final bump-and-log commit, whose short re-review goes in the merge message (B2);
    - `--theirs` on a merge conflict, and the record test straight after the merge (B3);
    - the wider transcript extraction (B4; its result count was replaced by C1);
    - A's own folder and a single probe location (B5);
    - the marker rules (B6);
    - the command-only exception for `E:\repos\Proteus` (B7);
    - the count-only guard after `git pull` (B8);
    - "fails the same way" means the same message word for word (B9);
    - repo-relative allowed paths in the record test (B10);
    - 0 deleted lines in `PbdFileTests.cs` (B11);
  - rev 4 folded in Plan-critic 3's 5 amendments:
    - the leak check counts only result lines that are paths inside forbidden locations, never build output or reads of allowed files (C1; limited to Grep/Glob by D1);
    - the Plan-critic 3, Planner (rev 4) and Planning orchestrator sections, the last recording its two touches from before the rule (C2; the list is opened by D2);
    - no sub-agents for B or the critic, and extraction by agent ID only (C3);
    - a warning to other sessions before the step 11e/f tests (C4);
    - `Luna/` forbidden and excluded from searches (C5);
  - rev 5 folded in Plan-critic 3's re-check of revision 4, plus one orchestrator finding:
    - a shell allowlist for B, the critic and the re-reviewer, with reads through Read and searches through Grep/Glob only (D1);
    - an open participant list (D2);
    - content-mode lines split at `:<digits>:` (D3; widened to every output mode by E1);
    - absolute `meddle` paths for every tool input, because sub-agents start in the session's start folder (D4; its claim about Plan-critic 3's Luna commands is corrected by E2);
  - both NEEDS_DECISION items are decided (Decisions 11 and 12).

## Approval Summary
**Goal:** Replace the Meddle-derived `XivLiveMesh/PbdFile.cs` with one Solona owns outright. It is written in two separated stages: a spec by an agent that read the old code, then code by an agent that never saw it. Other-race bending must behave exactly as before, and a record must show the separation was kept, so Meddle no longer blocks moving Proteus off the AGPL.

**Slug:** `pbd-clean-room`

**Acceptance Criteria:**
- AC-pbd-clean-room-1: `docs/pbd-format-spec.md` exists with the seven required sections, written as tables and rules, with no code or pseudo-code. Every "Required change from the old reader:" marker cites R-1 or a doc §3 item.
- AC-pbd-clean-room-2: `docs/pbd-clean-room-log.md` lists the files, URLs and commands of every participant. At least these: Planner, Plan-critic 1, 2 and 3, Plan-critic 3 (re-check of rev 4), Plan-critic 4 (check of rev 5), Planner (rev 4), Planner (rev 5), Planner (rev 6), Planning orchestrator, Agent A, Orchestrator, Agent B and Code-critic. Any later plan-critic or planner pass adds its own section.
  - The Planning orchestrator section says plainly that two of its touches came before the Decision 5 rule, and that they covered only the doc and file names, with no code content.
  - B's and the code-critic's lists are extracted from their transcripts. They touch nothing forbidden, start no sub-agents, run only the shell commands on their allowlists with absolute `meddle` paths, and report 0 forbidden locations in listing/search results.
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
- **Clean review:** the code-critic stays on the clean side and starts no sub-agents. Its only shell commands are the git commands Decision 4 gives it and targeted `dotnet test`, with absolute `meddle` paths. It sees the diff vs `test` with the old path excluded, plus the new file as a whole. Every fix to `PbdFile.cs` goes to B.
- **Isolating B:** B runs no git commands, starts no sub-agents, works with `bin/` and `obj/` deleted, and searches only this worktree. Its only shell commands are an absolute-path `dotnet build` and targeted `dotnet test`. It reads with Read and searches with Grep/Glob, always with absolute `meddle` paths. Its log comes from its transcript, which the extraction finds by B's agent ID.
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
- **Forbidden code in the submodules.** The Glamourer submodule contains Penumbra's GPL `Glamourer/Penumbra.GameData/Files/PbdFile.cs` and `PbdFile.Write.cs`. Every clean-side search must exclude `Glamourer/`, `Penumbra.Api/`, `Luna/`, `bin/` and `obj/`. `Luna/` is a third submodule (Ottermandias). Plan-critic 3's file-name-only grep of `meddle`'s `Luna/` for `pbd` and `deformer` found nothing, but it is forbidden like the other two.
- **Build output looks like a leak but isn't.** `Proteus.csproj` references both API projects (lines 168 and 174), so every allowed build prints `Penumbra.Api -> …\Penumbra.Api\bin\Release…` and `Glamourer.Api -> …`, and `dotnet test` prints `Test run for …\bin\Release…`. That is why the leak check (Decision 8) looks only at Grep and Glob results.
- **Auto-loaded memory index.** The memory index is loaded into every agent's context. It holds one-line descriptions, and its only fact about the deformer ("signed offsets") is already in the doc. The memory topic files themselves are forbidden.
- **Sub-agents start in the session's start folder.**
  - For the planning session that is `E:\orca\workspaces\Proteus\E-orca-workspaces-Proteus-meddle-docs-pbd-clean-room-plan.md`, a separate checkout of the same repo, not `meddle`.
  - A sub-agent's relative paths and its default search folder land there. So B, the code-critic and the re-reviewer use absolute `meddle` paths (Decision 8).
  - The rule is a precaution. Plan-critic 3's `ls` and `git -C … grep` on `Luna/` used absolute `meddle` paths. Revision 5 wrongly said they ran in the start folder; revision 6 corrects that.

**Decisions (and alternatives rejected):**
1. **Roles.**
   - **Agent A:** a fresh `general-purpose` sub-agent (opus). It may read what the doc allows.
   - **Orchestrator:** never opens the old file or anything forbidden.
   - **Agent B:** a second fresh `general-purpose` sub-agent (opus). It is briefed only with the doc, the spec, the allowed and forbidden lists, R-1 to R-3 and the search restrictions. It starts no sub-agents of its own, because their transcripts would fall outside the extraction (Decision 8). Its only shell commands are the two `dotnet` commands on its allowlist, and every tool input path is absolute under `E:/orca/workspaces/Proteus/meddle/` (Decision 8).
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
     - `git -C E:/orca/workspaces/Proteus/meddle diff test -- . ':!XivLiveMesh/PbdFile.cs'`;
     - `git -C E:/orca/workspaces/Proteus/meddle show HEAD:XivLiveMesh/PbdFile.cs` (the new file, taken after B's commit);
     - the spec and the log.
   - It never views the `test`-side revision of that path, and it follows Decision 5.
   - Its findings for B cite only lines of the new file and spec sections.
   - Any history or diff command on the old path without the exclusion voids the review: it is re-run fresh and the void is logged.
   - It starts no sub-agents of its own, in the review or in the short re-review (Decision 8).
   - Its only shell commands are the two git commands above, the re-review's three (Decision 14) and targeted `dotnet test` runs, all with absolute `meddle` paths (Decision 8's allowlist). It reads with Read and searches with Grep/Glob. A content-mode Grep keeps line numbers on; one with `-n: false` voids the review.
   - Its count of forbidden locations in listing/search results (Decision 8) must be 0.
5. **Forbidden for every clean-side agent** (B, every plan-critic, the code-critic, the orchestrator, the planner):
   - the old `XivLiveMesh/PbdFile.cs`, and any revision, commit body or history of it;
   - Meddle, SkinTattoo, Ktisis, Brio, DragAndDropTexturing (including `E:\repos\DragAndDropTexturing`), TexTools/xivModdingFramework, and Penumbra's or Glamourer's deformer code (all of `Glamourer/` and `Penumbra.Api/`);
   - the `Luna/` submodule (Ottermandias);
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
   - **Sections, at least:** Planner, Plan-critic 1, Plan-critic 2, Plan-critic 3, Plan-critic 3 (re-check of rev 4), Plan-critic 4 (check of rev 5), Planner (rev 4), Planner (rev 5), Planner (rev 6), Planning orchestrator, Agent A, Orchestrator, Agent B, Code-critic.
     - The list is open. Any further pass adds a section whose header matches `Plan-critic <n>` (optionally followed by a bracketed sub-part, such as "(re-check of rev 4)") or `Planner (rev <n>)`.
     - Each section has `Opened:` and `Git commands:` lists. B's and the code-critic's have a verbatim `Shell commands:` list in place of `Git commands:`.
     - B's section also has `Written:`, and B's and the code-critic's sections also have `Sub-agents:`.
   - **Planner, every Plan-critic section and every Planner (rev <n>) section:** their reports (see Environment & Preconditions).
   - **Planning orchestrator:** a verbatim copy of the reads in the hand-off's "Log inputs from the planning session" (above the plan's `---` rule). It also states plainly that two of those touches came before the rule now in Decision 5: opening `docs/pbd-clean-room.md` in the `readme-update` worktree, and a Grep that listed file NAMES under `Glamourer/Penumbra.GameData/Files/`. Both covered only the doc and file names, with no code content.
   - **Agent A:** A's self-report. It says the orchestrator never read A's transcript, gives the identifier-grep hit count, and names the probe file and its deletion.
   - **Agent B and code-critic:** extracted mechanically from their transcripts. Each section also gives its `Shell commands:` verbatim, `Sub-agents: none`, "Grep without line numbers: 0", and the "forbidden locations in listing/search results: N" count, which must be 0.
   - **Orchestrator:** its own actions, the pre-flight result, the skip lists and run counts from steps 3 and 6, the baseline failures with their messages, the never-skip tests it named, the guard counts and the NewBehaviour list.
   - **The extraction script** lives in the session scratchpad and is not committed; it is written with Write, not a heredoc.
     - It is pointed at B's and the code-critic's transcript files by agent ID only: the IDs the Agent tool returned, and for the critic both the review and the short re-review. It opens no other transcript, so it never opens A's.
     - It reads tool names and the `file_path`, `path`, `pattern`, `glob`, `type`, `output_mode`, `-n` and `command` inputs. It also counts `Agent`/`Task` calls for the `Sub-agents:` line, which must be none.
     - **Shell allowlist and absolute paths (rev 5).**
       - B may run only:
         - `dotnet build E:/orca/workspaces/Proteus/meddle/Proteus/Proteus.csproj -c Release`;
         - `dotnet test E:/orca/workspaces/Proteus/meddle/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "…"`, optionally with `--logger "console;verbosity=normal"`.
       - The code-critic may run only the two git commands in Decision 4, and the re-reviewer only the three in Decision 14, each through `git -C E:/orca/workspaces/Proteus/meddle`. Both may also run the targeted `dotnet test` above.
       - Every read goes through Read, and every search and listing through Grep/Glob. A content-mode Grep keeps line numbers on. One with `-n: false` voids the work (EDGE-17), and the script counts such calls for the "Grep without line numbers" line.
       - The script copies every Bash/PowerShell command verbatim into `Shell commands:`, and flags any command outside the sender's allowlist. One such command voids the work (EDGE-17).
       - Commands are never shortened to repo-relative form. Shortening once made Plan-critic 3's absolute `meddle` commands on `Luna/` look relative (corrected in revision 6).
       - Every tool input path is absolute under `E:/orca/workspaces/Proteus/meddle/`. Sub-agents start in the session's start folder, which may be another checkout (it was for the planning session).
       - So a relative path, or a Grep/Glob call with no `path`, counts as a hit in the leak check. The script resolves such a path against the start folder, so its `Opened:` entry stays absolute, and the record test rejects it.
     - It writes `Opened:` paths under the worktree as repo-relative. Shell commands stay verbatim.
     - **Leak check (listing and search results only).**
       - It covers Grep and Glob only. Shell listings and searches are outside the allowlist, so they void the work instead. It checks each result line without printing it, reading the Grep `output_mode` and `-n` inputs to know the line format.
       - A line counts if the path it names is inside a forbidden location. The path is taken by output mode (rev 6):
         - Glob, or Grep in `files_with_matches` mode: the whole line;
         - Grep in `count` mode: the part before the trailing `:<digits>`;
         - Grep in `content` mode with line numbers: the part before the first `:<digits>:` (the line-number separator);
         - the line is never split at the first `:`, so a drive letter such as `E:` doesn't break the parse;
         - only when the Grep `path` is a single file does a line with no path part count as that file. In any other search, a line whose path can't be parsed counts as a hit;
         - resolved against the listed directory or the tool's `path` input;
         - with `\` normalised to `/`, and lowercased.
       - Forbidden locations:
         - `glamourer/`, `penumbra.api/` and `luna/` at the repo root;
         - any path with a `bin` or `obj` segment;
         - `e:/repos/proteus/` and `e:/repos/draganddroptexturing/`;
         - any `e:/orca/workspaces/proteus/` checkout other than `meddle`;
         - `c:/users/solon/.claude/projects/*/memory/`;
         - any path containing `scratchpad-agenta`.
       - It prints only "forbidden locations in listing/search results: N", which must be 0.
     - **Not counted:**
       - read-type results (Read, and the critic's allowed `git show`), whose paths are in `Opened:` and `Shell commands:` and are checked there;
       - the output of `dotnet build` and `dotnet test`, and the contents of allowed files.
       - Why: allowed builds print `Penumbra.Api -> …\bin\Release…` and `Glamourer.Api -> …`, `dotnet test` prints `Test run for …\bin\Release…`, and the doc, the plan, the log and the record test name the markers on purpose.
       - The log says so.
   - The log describes the method.
9. **The real-data vectors** go in a nested class `PbdFileTests.RealFile`. It is marked `[Trait("Speed","Slow")]` if its setup takes 5 s or more. The synthetic edge tests stay in `PbdFileTests`, in the quick suite.
10. **`PbdCleanRoomRecordTests.cs`** holds the record tests, mirroring `ReadmeTermsTests`'s repo-root helpers. It is run only after B and the paperwork, and again after the merge.
11. **Decided: no resemblance check by A.** A clean critic can't do one, and the record is the evidence.
12. **Decided: B works in this worktree after the deletion commit.** Git is forbidden to B, and `bin/` and `obj/` are deleted first. Rejected: an export with no `.git`, because it risks breaking the build that depends on the submodules.
13. **No adversarial QA in game.** The change is meant to keep behaviour the same, and it adds no surface. The finished build is copied to the game's load folder (Rule 5); in-game bending is UNVERIFIED.
14. **The final commit (B2).**
    - After the code review passes, one commit holds both the Code-critic log section and the `Plugin.BuildNumber` bump.
    - Its short re-review reads only that commit: `git -C E:/orca/workspaces/Proteus/meddle show --stat HEAD`, `git -C E:/orca/workspaces/Proteus/meddle show HEAD -- docs/pbd-clean-room-log.md` and `git -C E:/orca/workspaces/Proteus/meddle show HEAD -- Proteus/Plugin.cs`. Those three, plus a targeted `dotnet test`, are its whole shell allowlist (Decision 8).
    - The reads of that re-review are extracted the same way and put in the merge commit message.
    - Any change after that commit means redoing the bump commit and its re-review.
    - Before the copy to the load folder, and again before the step 11e/f test runs in `E:\repos\Proteus` (which rebuild that same folder), other sessions are warned.
15. **Exception to Decision 5 for `E:\repos\Proteus` (B7).**
    - The orchestrator may run git, build and test commands there: `git pull`, the guard, `git merge`, `git checkout --theirs` on a conflict, `dotnet test` for the record tests and the quick suite, and `git push`.
    - It may not open any file there, and must not view conflict content (no `git diff` during a conflict).
    - Its `dotnet test` runs build into `E:\repos\Proteus\Proteus\bin\Release\win-x64`, which is the dev-load folder, so a running game reloads the plugin. Other sessions are warned before them, as before step 10's copy.
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
1. **Planning** (done): the planner (revisions 1–6), five plan-critic passes (Plan-critic 1–3, Plan-critic 3's re-check of revision 4, and Plan-critic 4's check of revision 5) and the planning orchestrator. Their reads go into the log (Decision 8).
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
     - no git commands and no sub-agents;
     - shell commands: only the two on its allowlist (Decision 8). Every read goes through Read, and every search and listing through Grep/Glob. A content-mode Grep keeps line numbers on; one with `-n: false` voids the work;
     - absolute `E:/orca/workspaces/Proteus/meddle/...` paths for every tool input. A relative path, or a Grep/Glob call with no `path`, voids the work;
     - every search scoped to this worktree, with `Glamourer/`, `Penumbra.Api/`, `Luna/`, `bin/` and `obj/` excluded.
   - **B writes:** `XivLiveMesh/PbdFile.cs` with the AC-4 header. B may build (`dotnet build E:/orca/workspaces/Proteus/meddle/Proteus/Proteus.csproj -c Release`) and run targeted filters (`dotnet test E:/orca/workspaces/Proteus/meddle/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "…"`), including the NewBehaviour tests.
   - **B's narrative** for the log is short. The orchestrator builds B's lists (including `Shell commands:`), its `Sub-agents:` line and its count of forbidden locations in listing/search results from B's transcript, found by B's agent ID (Decision 8), then commits.
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
   - It starts no sub-agents of its own, runs only the shell commands on its allowlist, and uses absolute `meddle` paths (Decision 4).
   - Findings on `PbdFile.cs` go to B. Findings on tests and paperwork go to the orchestrator. Any commit after the review means a re-review.
   - Once it passes, the orchestrator takes the critic's transcript by its agent ID and extracts its lists, its `Sub-agents:` line and its count of forbidden locations in listing/search results (a void review is re-run).
   - Then it makes the **final commit**: the Code-critic log section plus the `Plugin.BuildNumber` bump to the next free number on `test`.
   - Then it runs `--filter "FullyQualifiedName~PbdCleanRoomRecordTests"`, which must pass.
9. **Short re-review of the final commit** (Decision 14).
   - It reads only that commit, with the three `git -C E:/orca/workspaces/Proteus/meddle show …` commands in Decision 14. Its only other shell command is a targeted `dotnet test`.
   - The re-reviewer starts no sub-agents and uses absolute `meddle` paths. Its extracted reads (from its transcript, by agent ID), with its `Shell commands:`, its `Sub-agents:` line and its count of forbidden locations in listing/search results, are kept for the merge message.
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
    - **e.** Warn other sessions first, as in step 10. This `dotnet test`, and the one in f, rebuild `Proteus\bin\Release\win-x64` in `E:\repos\Proteus`, which is the dev-load folder, so a running game reloads the plugin. Then, straight after the merge, run `--filter "FullyQualifiedName~PbdCleanRoomRecordTests"` in the primary checkout.
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
  - a section per participant: at least Planner, Plan-critic 1–3, Plan-critic 3 (re-check of rev 4), Plan-critic 4 (check of rev 5), Planner (rev 4), Planner (rev 5), Planner (rev 6), Planning orchestrator, Agent A, Orchestrator, Agent B and Code-critic, plus any later `Plan-critic <n>` or `Planner (rev <n>)` pass;
  - B's and the critic's lists from their transcripts (found by agent ID), with their verbatim `Shell commands:`, their `Sub-agents:` lines and their counts of forbidden locations in listing/search results;
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
- EDGE-pbd-clean-room-17: B or the code-critic does any of the following:
  - opens something forbidden;
  - gets a line in one of its Grep or Glob results that is a path inside a forbidden location (Decision 8);
  - gives a tool a relative path, or makes a Grep/Glob call with no `path`;
  - makes a content-mode Grep call with `-n: false`;
  - runs a shell command outside its allowlist;
  - starts a sub-agent;
  - runs a diff or history command on the old path without the exclusion.

  B's file is discarded and a new B starts, or the review is re-run fresh. The void is logged.
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
  - **Header and sections:**
    - the top note says the final re-review is in the merge commit message;
    - every header matching `Plan-critic <n>` (with an optional bracketed sub-part) or `Planner (rev <n>)` has `Opened:`;
    - at least these sections exist, each with `Opened:`: Planner, Plan-critic 1, Plan-critic 2, Plan-critic 3, Plan-critic 3 (re-check of rev 4), Plan-critic 4 (check of rev 5), Planner (rev 4), Planner (rev 5), Planner (rev 6), Planning orchestrator, Agent A, Orchestrator, Agent B and Code-critic.
  - **Planning orchestrator:** the section contains `readme-update`, `Glamourer/Penumbra.GameData/Files/` and "no code content", and says those two touches came before the rule. The path and marker checks below don't apply to it.
  - **Agent A:** labelled a self-report, with an identifier-grep count of 0.
  - **B and code-critic paths:** after normalising (`\` to `/`, lowercase), every `Opened:` entry is either repo-relative (no drive letter, no leading `/`, no `..`) or a tool path. Tool paths are under `c:/users/solon/.claude/plugins/`, or contain `/temp/claude/` and `/scratchpad/` but not `scratchpad-agenta`. The allowed set is built at runtime, not from a session path.
  - **Forbidden markers:** no `Opened:` entry in B's or the critic's section contains `glamourer/`, `penumbra.api/`, `luna/`, `/bin/`, `/obj/`, `meddle.utils`, `skintattoo`, `draganddroptexturing`, `/memory/` or `scratchpad-agenta`, or starts with `bin/` or `obj/`. The other sections, the doc and the plan name these on purpose and are not checked.
  - **Leak counts and sub-agents:**
    - B's and the critic's sections each report "forbidden locations in listing/search results: 0", "Grep without line numbers: 0" and `Sub-agents: none`;
    - the log states that `dotnet build`/`dotnet test` output and reads of allowed files are not counted;
    - the log describes the per-mode parsing (whole line for Glob and `files_with_matches`, before the trailing `:<digits>` for `count`, before the first `:<digits>:` for `content`), and says the no-path fallback applies only to a single-file search.
  - **Shell commands:**
    - B's and the critic's sections each have a `Shell commands:` list.
    - Each entry is normalised: `\` to `/`, lowercase, runs of spaces collapsed.
    - Every entry matches its sender's allowlist in Decision 8, and contains no forbidden marker from the line above.
    - B's list has no git command.
    - The allowlist's one fixed prefix, `e:/orca/workspaces/proteus/meddle/`, is the worktree Decision 5 names, not a session path. The test reads log text, so it also passes in the primary checkout.
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
- **Submodules:** run `git submodule update --init --recursive` before building. It brings in Penumbra's GPL `PbdFile.cs` under `Glamourer/`, plus `Penumbra.Api/` and `Luna/`. All three are excluded from every clean-side search (`':!Glamourer' ':!Penumbra.Api' ':!Luna'`).
- **Broken build:** between step 4 and step 5 the solution doesn't build, and `bin/` and `obj/` are gone. If the first build after B fails for a reason other than B's code (for example a native `proteus_bcn.dll` that isn't restored), stop and diagnose (Rule 2).
- **Dev-load folder:** never copy there just to test. Only the finished build is copied, after warning other sessions, and the folder is write-only for the clean side. The step 11e/f test runs in `E:\repos\Proteus` also write to it, because they build that checkout's `Proteus\bin\Release\win-x64`, so other sessions are warned before them too.
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
- **Plan-critic 3's reads, for the log:**
  - **Files read** (each whole):
    - `docs/pbd-clean-room-plan.md`
    - `docs/pbd-clean-room.md`
    - `docs/agent-rules/plan-critic.md`
    - `.gitmodules`
  - **Listings:** `ls E:/orca/workspaces/Proteus/meddle/Luna` (top level), on `meddle`'s `Luna/`.
  - **Searches:**
    - `git grep -n "ProjectReference" -- '*.csproj'`, with the exclusions;
    - `git grep -n -i -c "draganddroptexturing\|glamourer/" -- docs/pbd-clean-room.md docs/pbd-clean-room-plan.md AGENTS.md` (counts only);
    - `git -C E:/orca/workspaces/Proteus/meddle/Luna grep -l -i -e pbd -e deformer` (file names only, no hits).
  - Both touches of `Luna/` were on `meddle`'s `Luna/`, through absolute paths, before revision 4 forbade it. They saw file names only, with no hits and no code. Revision 5 wrongly said they ran in the session's start folder: the orchestrator's extraction script had shortened them to repo-relative form. Revision 6 corrects this.
  - No show, log, diff or blame, and no URLs. AGENTS.md, CLAUDE.md and the memory index were loaded automatically.
- **Planner (rev 4)'s reads, for the log:** it stayed on the clean side.
  - **Files read:** `docs/pbd-clean-room-plan.md` in the `meddle` worktree, whole, in two reads (lines 1–566 and 566–707).
  - **Listings, searches, git commands, URLs:** none.
  - **Loaded automatically, not opened:**
    - AGENTS.md and CLAUDE.md of the session's start worktree, `E:\orca\workspaces\Proteus\E-orca-workspaces-Proteus-meddle-docs-pbd-clean-room-plan.md`. Nothing else in that worktree was opened.
    - the user's global CLAUDE.md;
    - the memory index.
  - The revision 1–3 searches listed under the Planner's reads did not name `Luna/` among their exclusions.
- **Plan-critic 3 (re-check of rev 4)'s reads, for the log:**
  - **Files read:** `docs/pbd-clean-room-plan.md` lines 1–160, 195–284, 580–609 and 685–759.
  - **Searches:** three `grep -n` runs on that file only:
    - the Delta heading;
    - forbidden location, listing, Sub-agents, Planning orchestrator, Plan-critic 3, record test, Luna, EDGE-17;
    - command input, shell, Commands, cat, Select-String, rg.
  - No listings, git commands or URLs. AGENTS.md, CLAUDE.md, the memory index and the plan-critic skill text were loaded automatically.
- **Planner (rev 5)'s reads, for the log:** it stayed on the clean side, in the same session as Planner (rev 4).
  - **Files read:** `E:\orca\workspaces\Proteus\meddle\docs\pbd-clean-room-plan.md` lines 1–300 and 360–767, in two reads. Lines 301–359, the verbatim Requirements, were not re-read.
  - **Listings, searches, shell or git commands, URLs:** none.
  - **Loaded automatically, not opened:** as for Planner (rev 4).
- **Plan-critic 4 (check of rev 5)'s reads, for the log:**
  - **Files read:** `E:/orca/workspaces/Proteus/meddle/docs/pbd-clean-room-plan.md`, whole, in two Read calls (lines 1–526 and 526–823), with absolute paths.
  - **Listings, searches, shell or git commands, URLs:** none.
  - **Loaded automatically:** AGENTS.md and CLAUDE.md from the start folder, the user's global CLAUDE.md, the memory index and the plan-critic skill text.
- **Planner (rev 6)'s reads, for the log:** it stayed on the clean side, in the same session as Planner (rev 4) and Planner (rev 5).
  - **Files read:** `E:\orca\workspaces\Proteus\meddle\docs\pbd-clean-room-plan.md` lines 1–320, 400–403, 480–509 and 548–823, in four Read calls with absolute paths.
  - **Listings, searches, shell or git commands, URLs:** none.
  - **Loaded automatically, not opened:** as for Planner (rev 4).
- **Planning orchestrator's reads, for the log:** they are listed in the hand-off's "Log inputs from the planning session" (above the `---` rule). The log copies them verbatim (Decision 8). Its session's AGENTS.md and CLAUDE.md were loaded automatically from the start folder named there.

## NEEDS_DECISION
None. Both earlier items are decided (Decisions 11 and 12).

## Risks
- **Old code leaking to the clean side.** Possible routes:
  - the deletion's diff, or a full `git diff test` given to the critic;
  - the submodules: the GPL file under `Glamourer/`, plus `Penumbra.Api/` and `Luna/`;
  - `bin/` and `obj/`;
  - other checkouts, the memory topic files, or A's folder;
  - the conflict content during the merge;
  - private identifiers in A's spec;
  - a sub-agent started by B or the critic, whose transcript the extraction would not read;
  - a shell read or search by B or the critic (`grep -r`, `rg`, `cat`, …) that no check would see;
  - a relative path that lands in the session's start folder, which is another checkout.

  Mitigations:
  - Decisions 4–6, 8 and 15;
  - deleting the build folders;
  - checking B's and the critic's Grep and Glob results for paths inside forbidden locations, a shell allowlist and absolute `meddle` paths for both, and forbidding them sub-agents (Decision 8);
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
- **Plugin reloads during the merge tests.**
  - The step 11e/f `dotnet test` runs in `E:\repos\Proteus` rebuild that checkout's `Proteus\bin\Release\win-x64`, which is the dev-load folder. A running game reloads the plugin under whatever another session is testing.
  - This goes against AGENTS.md Rule 5's "do not overwrite that DLL just to run tests". It is accepted because the record tests and the pre-push suite must run on the merged `test`.
  - Other sessions are warned first (Decision 14).
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