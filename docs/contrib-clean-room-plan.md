> **Hand-off copy, revision 10.** Revision 7 (passed by plan-critic 7, Confidence HIGH) was committed at step 0. At step 1 the D3 tripwire fired, so the plan was revised through the planner. Plan-critic 8 rated revision 8 MEDIUM-HIGH, plan-critic 9 rated revision 9 HIGH, and plan-critic 10 passed revision 10 with Confidence HIGH and no findings. Every critique amendment was folded in through the planner.
>
> **Log inputs from the planning session.** The planning participants' reads (Planner rev 1–10, Plan-critic 1–10, and the Planning orchestrator with its disclosures and exceptions) are under *Environment & Preconditions* below. The orchestrator copies them into `docs/contrib-clean-room-log.md`.
> - **Plan-critic 7** opened only the rev-7 plan output (lines 1–28 and 293–392). Its one shell command: `grep -n -E "^## |^1[25]\. \*\*|Delta|self-check 2|name-only|critic-pass|QA-bump|\(f\)" "C:/Users/solon/.claude/projects/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/toolu_01Y91ZpRto4LnGvgWTBYBWcP.txt"`.
> - **Plan-critic 10** opened only the rev-10 plan output (lines 1–23 and 309–342). Its one shell command: `grep -n -E "^## |^11\. \*\*|^12\. \*\*" "C:/Users/solon/.claude/projects/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/toolu_01JhiPxTHv2eiBz3a5qGiDpj.txt"`.
>
> Neither opened a forbidden path or ran sub-agents.

# Implementation Plan: Clean-room replacement of Caraxi's and AzureRavie's code (revision 10)

## Delta (rev 10 vs rev 9)
Plan-critic 9 rated rev 9 HIGH and asked for two wording fixes. Both are folded in. Steps 0 and 1 are done (`71d450fb` for the plan, `1a2cd3ef` for the spec and A's self-report). Step 1b and everything after it are still to come.

- **M-1: The game-fact limit is per file, not per line number.**
  - Game-fact lines may appear only in the files spec §1 lists. Each file may hold no more of them than §1 gives it, and the total is at most 3.
  - §1 keeps each line's `file:line` as the record at `<R>`. Line moves inside those files are not failures.
  - Changed in: D2, D11, D17's after-every-merge bullet, step 6, step 10.5, `Log_reports_no_contributor_lines` and EDGE-39.
- **M-2: The `-w -M -C` trivial limit becomes a logged change, not a failure.**
  - A trivial line in `-w -M -C` outside the three `ColorTableEditor*.cs` files is a logged change, with the reason "copy-detected import block from a test commit".
  - Only a non-trivial line, or a line in no category, fails. Logged changes never start an A3 loop.
  - The list of allowed change reasons is now:
    - a new file by B (named);
    - a file new from `test`;
    - an import edit arriving from `test`;
    - a copy-detected import block from a test commit.
  - Changed in: AC-3, D11, D17, step 6, step 10.5, EDGE-26, EDGE-39 and `Log_reports_no_contributor_lines`.
- **Log inputs:** Plan-critic 9's reads are added. Planner (rev 10) made no tool calls.

Unchanged from rev 9: the scope (Phase A only; the licence stays AGPL), the slug, ACs 1, 2 and 4–8, D1–D23 (amended in place) and the step order.

## Approval Summary
**Goal:** Replace every non-trivial line Caraxi and AzureRavie wrote with code written by an agent that never saw theirs. Proteus's public IPC and the small fixes stay exactly as users and other plugins know them. The result is a record that makes a later relicensing possible. The licence stays AGPL-3.0 in this feature.

**Slug:** `contrib-clean-room`

**Acceptance Criteria:**
- AC-contrib-clean-room-1: A plugin calling Proteus's public IPC sees the same labels, argument types, return types and results as before. Tests written from the spec prove this: they pass on the old code and on the clean code.
- AC-contrib-clean-room-2: The contributors' small features still work as before, unless the spec drops one with a reason:
  - `/proteus refresh silent`, alone or with `full`;
  - clicking a mod's name in the Mods tab opens it in Penumbra;
  - the priority control edits the priority correctly;
  - multi-select group defaults and per-option overlay tables;
  - case-insensitive overlay lookup;
  - the Glamourer cooldown counts from the equip attempt;
  - lock conflicts are logged.
- AC-contrib-clean-room-3: No non-trivial contributor line remains.
  - **The pass rule.** Whole-tree blame runs at `<R>`, the step-6 tip, the step-8 merged tip and any step-9 re-merge, in both `-w -M -C` and `-w -M -C -C`, and at the final tip in `-w -M -C`. Every line it gives to Caraxi or AzureRavie must be either:
    - in a trivial category (blank, using, namespace or brace); or
    - a game-fact line in a file §1 lists, within that file's §1 count (3 in total at most).
  - **Count changes.** Every change in the counts is logged by file and category, with a reason from the allowed list.
  - **The final re-check.** The step-10 re-check on `test` goes in the final report.
  - **The derived closure is removed:** direct, derived, carried-over and merge-inherited lines. Its rounds and counts are logged.
  - **Allowed additions.** `<R0>…<R>` and every merge add only C# markers, verified restored lines, blanks and, in merges only, the BuildNumber token. Unmatched and other are both 0.
  - The sample folder is deleted and `proteus.slnx` is regenerated.
- AC-contrib-clean-room-4: B's code is shown to be independent. At the final tip, Agent D finds 0 expressive non-interface lines in B's commits that are identical to any removed line. Every run's numbers are logged, starting with the first, together with each hit's category.
- AC-contrib-clean-room-5: The record is complete:
  - the spec is facts only, with all 12 sections, the trivial class shown as excluded, and the game-fact lines listed;
  - the log lists every participant's reads, with commands verbatim;
  - the clean agents' sections are extracted from their transcripts and stay inside their allowlists;
  - both orchestrator self-checks are 0 beyond their named exceptions;
  - the dirty agents' self-reports give 0 identifier-grep hits;
  - the tripwire re-plan is recorded;
  - the record tests pass, and they only parse files.
- AC-contrib-clean-room-6: The proof holds:
  - before removal, the old-code gate (spec tests plus WIDE-A) is green, read only through the hashing TRX script;
  - after removal, the spec tests don't pass;
  - on the clean code and on every merged tip, everything passes. For tests that existed at step 2, the skip list is the same, and the only failures are baselines with equal normalised hashes. A classes any mismatch as "differs only in <kind>" or "different";
  - tests that arrive with `test` pass, or skip on their own.
- AC-contrib-clean-room-7: The licence is untouched:
  - `LICENSE` is still the AGPL;
  - `docs/LICENSE-DRAFT.md` still says "DRAFT, not yet in force";
  - the three guard tests are unchanged and green;
  - the step-9 file-list check lists no licence or notice file.
- AC-contrib-clean-room-8: Finish:
  - the branch merges into `test` locally, adding nothing beyond the last reviewed merge state;
  - the Records filter, the blame re-check and the quick suite pass on `test`;
  - only `test` is pushed. Nothing goes to `main`, no tags are made, and no release or deploy workflow runs.

**Key decisions:**
- **Agent A** (dirty) writes the spec. After the orchestrator's tests from it are proven on the old code, A removes the non-trivial contributor lines, everything derived from them (the transitive set), and every set line in the mixed-set files. A only deletes, restores word for word, and adds markers. It never commits. Trivial coincidental lines in other files stay in place and are logged.
- **Agent B** (clean, fresh) fills the holes, rewrites `IpcProvider.cs`, and rebuilds the two coincidental Solona blocks.
- **Agent D** (dirty, fresh) compares B's commits with the whole removed set, and reports numbers and categories only.
- **The clean side never sees the old code.** The orchestrator, B, the critics, the security reviewer and QA view only the clean diffs. Before `<R>`, test output reaches the orchestrator only as names, outcomes, types, codes and normalised hashes. Scripts that touch mixed paths print counts only.
- **The security review** runs as a clean sub-agent on the clean diffs, never as the `/security-review` skill in the orchestrator.
- **`test` is merged in at the start of step 8,** so the review, the security review, D and QA all see the merged tip. A re-merge in step 9 gets its own critic check. The short re-review covers every commit after the critic's last pass.

**Risk flags:** security surface: yes (`Proteus/Interop/` bridges, Proteus's public IPC) · new persisted field: no · sensitive-category data: no · schema migration: no · new dependency: no · new/changed route: no

**NEEDS_DECISION:** None. The user decided ND-1. The orchestrator decided ND-3, the self-check option, and the rev-8 to rev-10 amendments. The other earlier items are owner follow-ups.

*(The summary above is what the developer approves; everything below is the detailed contract it stands on.)*

## Source
- The user's prompt, choices, decision and feature arguments, verbatim under Requirements.
- The precedent: `docs/pbd-clean-room.md`, `docs/pbd-clean-room-plan.md` (rev 6), `docs/pbd-clean-room-log.md` and `Proteus.Tests/PbdCleanRoomRecordTests.cs`.
- The committed spec `docs/contrib-clean-room-spec.md` (`1a2cd3ef`), and Agent A's stage-1 report as summarised by the orchestrator.
- The orchestrator's step-0 blame figures, and its rev-8 amendments.
- The reports of Plan-critics 1–6, 8 and 9.
- Context only, unchanged: `docs/LICENSE-DRAFT.md`, `THIRD_PARTY_NOTICES.txt` and `XivLiveMesh/NOTICE.md`.
- Not opened, and forbidden to the planner: the mixed files and their history, the contributors' commits, the Luna, Glamourer and Penumbra.Api sources, and the contributors' PRs.

## Context & Decisions
**Problem.** Caraxi (11 commits) and AzureRavie (1 commit) still have surviving lines on `test`. Under §6 of the draft licence, those lines stay under the AGPL unless their authors agree. The user chose to have them rewritten, and to stop before relicensing.

The code sits mostly inside files Solona wrote. So A, who may see it, removes it first. Four things prove the removal:
- blame counts with a trivial classifier;
- bounded additions;
- D's similarity check;
- tests that fail after the removal and pass again on the clean code.

**Facts (from allowed files, the committed spec and the orchestrator's counts):**
- **Step-0 blame** (`-w -M -C` on `test`):
  - Caraxi: 274 lines, made up of the sample 100, IpcProvider 137, slnx 9, StatusWindow 4, PenumbraBridge 9, Plugin 6, SidecarDiscoveryService 6, and one line in each of the three `ColorTableEditor*.cs` files;
  - AzureRavie: 12 lines, in GlamourerBridge;
  - dependabot: 1 line.
- **Spec §1** (at `test` `a25a3b56`):

  | Category | Lines |
  |---|---|
  | Whole-file | 268 (in 8 files) |
  | Direct in both modes, outside whole files | 40 (in 8 files) |
  | Direct in `-C -C` only, outside whole files | 212 (in 179 files; import, blank and namespace lines, plus 3 game-path test lines) |
  | Derived | 51 |
  | Carried over | 19 |
  | Merge-inherited | 0 |
  | Restored candidates | 13 |

  The closure took three rounds: 58, 21, then 0.
- **The mixed set has eleven entries:**
  - `Proteus/Interop/IpcProvider.cs`
  - `Proteus/Interop/PenumbraBridge.cs`
  - `Proteus/Interop/GlamourerBridge.cs`
  - `Proteus/Plugin.cs`
  - `Proteus/Services/SidecarDiscoveryService.cs`
  - `Proteus/Gui/StatusWindow.cs`
  - `Proteus/Gui/ModsTab.cs`
  - `Proteus/Services/CompositorService.Carriers.cs`
  - `Proteus/Services/CompositorService.cs`
  - `proteus.slnx`
  - `samples/ExampleMultiGroupOverlayMod/**`

  It grows only through the step-1b failure path or EDGE-6.
- **Public IPC:** six labels (spec §2).
- **Seams** (spec §9.1):
  - reachable without a new seam: R1, R7, R8 and R10 (for R1);
  - partly reachable: R5, R6 and R11;
  - need a seam: R2, R3, R4, R9 and R12.
- **The equip route reaches neither behaviour** (spec §9.3). The cooldown is in private carrier reconciles. The lock logging is in `ReapplyPlayerState()`, `SetGlasses(ulong)` and `SetAccessory(ulong, string)`. `POST /actions/equip` reaches only `SetGearItem`.
- **Existing tests** (spec §10): `SidecarDiscoveryTests`, `ContentPieceSelectionTests`, `FaceUvDoublingTests`, `ShellMensLayoutTests`, `WomensArtOnMenTests`, `StutterFixesWiringTests`, `StutterFixesPureTests` and `HarnessBindingsWiringTests`. There is one EDGE-5 hit, and it is kept.
- **Harness routes:** `GET /health`, `GET /log`, `POST /actions/equip`, `GET /sidecar` and `POST /actions/mod-settings`.
- **Repo facts:**
  - 12 test files find the repo root through `proteus.slnx`;
  - record tests only read files;
  - pushing `test` runs only `codeql.yml`.
- **`BuildNumber`:** 1467 on `test` (`a25a3b56`), 1466 on the branch. The scripts match it with `^\s*public const int BuildNumber = \d+;\s*$`.
- **Bot commits:** `8c787a80` (a version bump) and `d046b185` (a merge with no content).
- **Paths:** `W = E:/orca/workspaces/Proteus/licence-clean-room`, on branch `solona-m/licence-clean-room`. Every agent uses absolute `W` paths.

**Decisions (and rejected alternatives):**

1. **Roles.** Every sub-agent runs on model `opus`.

   | Role | Side | Reads | Writes | Shell / git |
   |---|---|---|---|---|
   | Orchestrator | clean | anything D9 allows; the mixed set only at or after `<R>` | tests, docs, the log, the slnx regeneration, the `BuildNumber` line; **every commit** under D10, without viewing it | D10/D11; D5 before `<R>`; count-only scripts on mixed paths |
   | Agent A (fresh `general-purpose`; stage 1, step 1b, stage 2, continued by SendMessage) | dirty | the whole repo and its history; the TRX files (D5, D18). Never the submodule sources, other checkouts, the web or memory topic files | the spec and self-report; the 1b amendment; the removal (deletions, word-for-word restores, C# markers); the manifest; the removal self-report | git read commands, `dotnet build`, `dotnet test`. **Never commits**, pushes or merges |
   | A2 (stage-2 fallback) / A3 (EDGE-4, D17 "other") | dirty | as A; each recomputes the closure from history | as A | as A. **A3 alone** may run `git -C W merge --no-commit test`; the orchestrator commits |
   | Agent D (fresh `general-purpose`) | dirty | the mixed set at `<R0>^` and its history, B's commits, A3's resolution lines, the spec | numbers, categories, clean-side `file:line`; self-report | git read commands, its own scripts |
   | Agent B (fresh `general-purpose`, after `<R>`) | clean | everything at HEAD except what D9 forbids | the holes, the new `IpcProvider.cs`, the §6.3/§6.4 blocks, Localization entries, new `Proteus/**` files (each named), fixes to merged tips | D12; no git; no sub-agents |
   | Code-critic | clean | the D8 diffs, files at HEAD, the spec, the log | findings; §12 table | D12 |
   | Security reviewer | clean | as the code-critic | findings; §12 table | D12 |
   | Adversarial QA | clean | `docs/agent-rules/adversarial-qa.md`, the Requirements, spec §4–9, files at HEAD after `<R>`, the harness config | QA report | `curl` to `127.0.0.1`/`localhost` only |
   | Short re-reviewer | clean | the commits after `<critic-pass-commit>` | verdict | D15 |
   | Planner, plan-critics | clean | the plan, allowed files, the committed spec | reports listing their reads | as reported |

   - **Folders.** All sit outside `W`, as siblings of the session scratchpad, and are logged:
     - `scratchpad-agentA\` (A, A2, A3);
     - `scratchpad-agentD\`;
     - `scratchpad-handover\` (the manifest only).
   - **Rejected:**
     - the orchestrator removing lines, because it would see them;
     - A running the similarity check, because D must be independent;
     - dirty agents committing, because D10 should be enforced in one place.

2. **The removal set.** A computes it and lists it in §1 by file, category and count, with no code.
   - **Direct:** every line at the `test` tip that whole-tree blame gives to either contributor, in `-w -M -C` or `-w -M -C -C`.
   - **The trivial coincidental class.** A direct line is in the class, and is **not removed**, when (a) or (b) holds, and (c) also holds:
     - (a) after trimming, it matches the fixed trivial regex:

       ```
       ^$
       ^using\s+(static\s+)?[A-Za-z_][\w.]*(\s*=\s*[\w.<>, ]+)?;$
       ^namespace\s+[\w.]+;?$
       ^[{}];?$
       ```

       The categories are blank, using, namespace and brace.
     - (b) it is a game-fact line. A lists the 3 lines in §1 by `file:line` (the record at `<R>`), each with a one-word shape such as "const" or "assert". Each must:
       - sit under `Proteus.Tests/`;
       - contain the spec §9.5 literal;
       - with the quoted literal removed, be trivial itself: at most one declaration or one assert call, as A judges.

       From then on the limit is per file (M-1): game-fact lines appear only in the files §1 lists, each file at or below its §1 count, at most 3 in total.
     - (c) its file holds no non-trivial set line.

     Trivial lines in mixed-set files are removed, and B re-adds them.
   - **Derived:** a transitive closure.
     - Walk the commits in topological order.
     - In a non-merge commit, every line added in a hunk of `git diff -U0 -w -M X^ X` that removed a contributor line or a derived line is derived.
     - In a merge commit, a line inherits its status from its source parent. Only lines that are new against every parent get the hunk rule, applied against each parent.
     - Repeat until a round adds nothing.
   - **Carried over:** for each commit that deletes set lines without an in-hunk replacement, A inspects that commit and the next five commits touching `Proteus/`. A's `87ae2e3d` seeding is accepted.
   - **Whole files:** `Proteus/Interop/IpcProvider.cs`, `proteus.slnx` and `samples/ExampleMultiGroupOverlayMod/**`.
   - **Restored lines:**
     - These are Solona's own lines, copied word for word from the contributor commit's parent, where blame gives them to Solona. They are restored only where they fit.
     - The manifest is `<session scratch root>\scratchpad-handover\contrib-restored-manifest.tsv`, with columns `<path at R>\t<line at R>\t<source commit>\t<source path>\t<line at source^>`.
   - **A's operations:** A only deletes lines, restores lines word for word, and adds markers. It never edits a line it keeps. A broken build after removal is expected.
   - **Markers:** in C# files only, `// CLEAN-ROOM HOLE: spec §<n>`. Holes in non-C# files appear only in A's self-report. B removes every marker.

3. **The scope tripwire.** It is checked after step 1b, and again after any growth of the set.
   - **Condition 1:** derived + carried over + restored > 2 × direct, where direct = whole-file lines + non-trivial direct lines outside whole files.
   - **Condition 2:** the set, excluding the trivial class, includes an AGENTS.md Sensitive-Area file beyond the known Interop files.
   - Either one firing means a re-plan through planner and plan-critic.
   - **The step-1 firing and the rev-8 re-plan are logged:**
     - both readings of condition 1;
     - condition 2 before (21) and after (0) the trivial class;
     - the `87ae2e3d` scope accepted.

4. **Tests are written from the spec and proven on the old code before removal.**
   - Assertions come only from the spec.
   - A test that needs a seam the old code lacks carries `[Trait("CleanRoom","NewSeam")]` and `[Trait("Seam","§9.<row>")]`. Departure tests carry `NewBehaviour`. Neither is gated.
   - Counts are logged per class. If NewSeam tests are more than a third of the total, AC-1 is reported as weaker; that is expected here.
   - No test is weakened.
   - The EDGE-5 hit is kept unchanged.

5. **The old-code output filter.** This applies to every orchestrator `dotnet` run before `<R>`.
   - Runs log to TRX in the session scratchpad. Console output goes to an unread `.out` file, deleted afterwards. The step-2 `.trx` is kept.
   - `trx-summary.ps1` prints, per test: name, outcome, skip reason, assertion type, and `sha256(norm(message))[0..16]`. For any other exception, it prints the type only.
   - `norm` is fixed and logged:
     - CRLF becomes LF, and the message is trimmed;
     - paths become `<path>`;
     - GUIDs become `<guid>`;
     - digit runs become `<n>`.
   - `build-summary.ps1` prints error codes and their counts only.
   - Gated failures go to A, and so do CS1061 and CS1729. A answers "test bug: <fix>" or "spec error: §n amended", and never quotes output.

6. **The strings rule.**
   - The spec describes log and UI text; it never quotes it.
   - Tests assert structured facts.
   - Exact strings are allowed only for interface facts.
   - New user-visible text is translated into all 8 locales.

7. **Agent D's similarity check.** It runs after step 6, and again whenever B's commits change.
   - **Input:** the surviving lines added by B's commits (SHAs logged, each taken from `git diff -U0 c^ c`), plus A3's resolution lines.
   - **Normalisation:** comments stripped, whitespace collapsed. A line is trivial if it has fewer than 20 alphanumeric characters, or is a `using` directive.
   - **Comparison:** against the whole removed set.
   - **Categories:**
     - interface;
     - idiomatic or forced by context (with a reason; not rewritten);
     - expressive.
   - **Similarity:** the Jaccard index over 5-grams, per file.
   - **The record:**
     - the first measurement is logged before any rewrite, and every later one too;
     - expressive hits go to B until there are none left;
     - any file at 0.40 or above gets a reason.

8. **The clean diffs.**
   - **Pre-removal:**

     ```
     git -C W diff test...<R0>^ -- . ':!Proteus/Interop/IpcProvider.cs' ':!Proteus/Interop/PenumbraBridge.cs' ':!Proteus/Interop/GlamourerBridge.cs' ':!Proteus/Plugin.cs' ':!Proteus/Services/SidecarDiscoveryService.cs' ':!Proteus/Gui/StatusWindow.cs' ':!Proteus/Gui/ModsTab.cs' ':!Proteus/Services/CompositorService.Carriers.cs' ':!Proteus/Services/CompositorService.cs' ':!proteus.slnx' ':!samples' <any file added by the 1b failure path>
     ```

   - **Post-removal:**

     ```
     git -C W diff <R> HEAD
     ```

   - **Re-merge** (critic only): `git -C W diff <P9> HEAD`, or `--stat` alone.
   - **Re-reviewer:** the D15 commands only.
   - **Never:**
     - an unexcluded `git diff test HEAD`;
     - any show or diff of `<R0>…<R>`;
     - `log -p` or content blame of the mixed set before `<R>`;
     - a combined merge diff.
   - **Logged:** `<R0>`, `<R>`, `P8`, `T8`, `P9`, `T9` and `<critic-pass-commit>`.

9. **Forbidden for every clean participant:**
   - every revision of the mixed set before `<R>`, including its paths on `test`;
   - the contributors' commits, PRs and forks, and web searches for their code;
   - the sources under `Luna/`, `Glamourer/` and `Penumbra.Api/` (their LICENSE and README files are allowed);
   - `bin/` and `obj/`;
   - other checkouts;
   - the files of `E:\repos\Proteus` (D16);
   - the memory topic files, until the push;
   - A's and D's folders and transcripts;
   - `scratchpad-handover\` (scripts only);
   - the pre-`<R>` `.out` and `.trx` files, except through the D5 scripts.

   Trivial-class files outside the mixed set are **not** forbidden.

10. **Git hygiene.**
    - Stage with `git add -u` plus explicit paths.
    - Before every commit, `git status --short` must list only the expected paths:
      - `<R0>…<R>`: the mixed set and the deleted whole files;
      - the removal report: the log only;
      - the slnx commit: `proteus.slnx` only;
      - B's commits: the mixed set, `IpcProvider.cs`, `Proteus/Localization/*`, the log, and the named files;
      - merges: whatever `git diff --name-only <branch>...test` lists;
      - step 1b: the spec and the log;
      - any other commit: the files its step names.
    - No commit touches a trivial-class line outside the mixed set.
    - Summaries use only `--stat`, `--numstat`, `--name-only` and `--format`.
    - BuildNumber tokens are read only with `git grep -h -o -E "BuildNumber = [0-9]+" <rev> -- Proteus/Plugin.cs`.

11. **The count-only scripts.** They live in the session scratchpad, are written with Write, and are never committed. Content stays in memory.
    - **`blame-authors.ps1 <repo> <rev> <mode> [<previous checkpoint file>]`.**
      - **What it prints.** It applies the fixed regex, and the game-fact file list with its per-file counts, both written into the log. For every non-Solona email, per file, it prints:

        ```
        <path>\t<email>\tnontrivial:<n>\ttrivial:blank=<n>,using=<n>,namespace=<n>,brace=<n>,gamefact=<n>
        ```

        It also prints totals per email and mode, and a "changed:" list against the previous checkpoint, giving each file with its old and new counts.
      - **Pass rule (M-1, M-2):**
        - `nontrivial` = 0 in every file;
        - `gamefact` appears only in the files §1 lists, each at or below its §1 count, at most 3 in total.

        Nothing else fails. That includes trivial lines in `-w -M -C` outside the `ColorTableEditor*.cs` files.
      - **Changes.** Every trivial-count change is logged by file and category, with one of these reasons:
        - a new file by B (named);
        - a file new from `test`;
        - an import edit arriving from `test`;
        - a copy-detected import block from a test commit.

        A logged change never starts an A3 loop.
      - **Baseline:** what `<R>` measures. It is compared per file and category with §1: each count must be at or below §1, or the difference is logged with its file. Any non-trivial difference fails.
      - **Modes:**
        - `-w -M -C` at step 0, after later fixes, and at the final tip;
        - both modes at `<R>`, at step 6, after every merge, and at step 10.
    - **`restored-check.ps1`.** It validates the manifest and verifies each restore.
      - On non-merge ranges: marker (C# only), restored, blank or other.
      - On merges: only lines that appear in neither parent are checked, as marker, restored, blank, bump or other.
      - Unmatched and other must be 0. Bump must be 0 on non-merge ranges.
    - **`guard-classify.ps1`:** prints `<sha> bump-only` or `<sha> other`.
    - **`bump-resolve.ps1`:** accepts one conflict hunk made only of BuildNumber lines and prints `conflict hunks: 1, all bump-only`. Otherwise it refuses, the merge is aborted, and A3 takes it.
    - **Also:** `trx-summary.ps1`, `build-summary.ps1`, and the extraction script.

12. **Allowlists and extraction.**
    - **B** may run only:
      - `dotnet build W/Proteus/Proteus.csproj -c Release`;
      - `dotnet test W/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "…"`, optionally with `--logger "console;verbosity=normal"`.
    - **The code-critic** may run only:
      - the D8 pre- and post-removal diffs;
      - `git -C W diff <P9> HEAD`, or the same with `--stat`;
      - `git -C W log --format=%h%x20%an%x20%s test..HEAD`;
      - targeted `dotnet test`.
    - **The security reviewer** may run the same, minus the re-merge diffs.
    - **QA** may run only `curl`, to `127.0.0.1` or `localhost`.
    - **The re-reviewer** may run only:

      ```
      git -C W show --stat HEAD
      git -C W show HEAD -- docs/contrib-clean-room-log.md
      git -C W show HEAD -- Proteus/Plugin.cs
      git -C W log --format=%h%x20%s <critic-pass-commit>..HEAD
      git -C W show <QA-bump commit> -- Proteus/Plugin.cs
      git -C W diff --stat <critic-pass-commit> HEAD
      ```

    - **Everyone:** Read, Grep and Glob with absolute paths; content Greps keep line numbers on; no sub-agents.
    - **Extraction for clean agents.** Per agent ID, it prints:
      - `Opened:`;
      - `Shell commands:` (verbatim);
      - `Sub-agents:`;
      - "Grep without line numbers: N";
      - "Shell commands outside the allowlist: N";
      - "forbidden locations in listing/search results: N".

      Forbidden locations are:
      - `glamourer/`, `penumbra.api/` and `luna/` at the repo root;
      - any `bin` or `obj` segment;
      - `samples/examplemultigroupoverlaymod`;
      - `e:/repos/proteus/`;
      - other checkouts;
      - `/memory/`;
      - `scratchpad-agent`;
      - `scratchpad-handover`.
    - **The orchestrator's self-checks** run over its own session transcript, up to the final commit.
      - **Self-check 1 (whole session).** It counts every Read of a mixed-set repo-relative path, in any checkout, before `<R>`'s commit time.
        - Disclosed exception: the meddle `Plugin.cs` Read of lines 31–34.
        - Any earlier Read of `ModsTab.cs`, `CompositorService.Carriers.cs`, `CompositorService.cs`, or a file added by the 1b failure path, is listed as a further disclosed exception, with its range and what it showed.
        - Logged as "self-check 1 (whole session): 0 beyond disclosed exceptions".
      - **Self-check 2 (from the window start).** The window starts at the `worktree add` call, logged as an ISO time.
        - **Counted locations:** the forbidden locations, `scratchpad-handover`, A's and D's folders, and mixed-set paths in their pre-`<R>` state.
        - **What counts:** content access only, meaning Read paths, content-mode Grep result lines, and content-reading shell commands that name a counted location.
        - **Sanctioned class** (never counted):
          - Grep in `count` or `files_with_matches` mode;
          - Glob, `ls` and `git ls-files`;
          - `Remove-Item` and `Test-Path` on `bin/` and `obj/`;
          - `Copy-Item` to the dev-load folder;
          - the timestamp check;
          - the D16 git commands;
          - the D11 count-only scripts.
        - **Named exceptions:**
          - (a) the `worktree add` call;
          - (b) the `-L 31,34` count-only blame;
          - (c) the greps of the transcript jsonl;
          - (d) `head -n 3 Luna/LICENSE Glamourer/Glamourer.Api/LICENSE Penumbra.Api/LICENSE Luna/ImSharp/LICENSE`;
          - (e) `head -n 3 Glamourer/LICENSE`.
        - The in-window Grep facts (i)–(iv) and the one Glob are logged. No exception (f) is needed.
        - The orchestrator's count and files-with-matches greps over `Proteus/*.cs` are sanctioned.
        - Logged as "self-check 2 (from <ISO time>): 0 beyond named exceptions".
    - **Start commits** of the clean agents are logged.
    - **Dirty agents** self-report. Their transcripts are never read.

13. **`IpcProvider.cs` is rewritten whole by B,** from the spec.

14. **The bot commits** are recorded, not replaced.

15. **The final commit, the bump and the short re-review.**
    - **Final bump:** `max(branch BuildNumber, last merged T's BuildNumber) + 1`. If `test`'s number is N or higher, QA item 1 is re-run on the final build.
    - **`<critic-pass-commit>`** is logged. After it, only the QA-bump commit, log-only commits and the final commit may land. A code commit sends the work back to the critic, and the pass commit moves.
    - **The final commit** holds the remaining log sections and the bump.
    - **The short re-review** runs the six D12 commands and checks:
      - the commit list;
      - that `--stat` shows only the log and `Plugin.cs`;
      - that the bumps change only BuildNumber lines.

      Its reads go into the merge message.

16. **`E:\repos\Proteus`: commands only.**
    - Allowed: `git pull`, `git rev-parse`, the guard and the classifier, `git merge`, `blame-authors.ps1`, `dotnet test` (the Records filter and the quick suite), and `git push origin test`.
    - Never open a file there.
    - Warn other sessions before each `dotnet test`.

17. **The guard and merging `test` in** (at the start of step 8, and in step 9 if `test` has moved).
    - **The guard:** `git rev-list --count <branch>..<T> -- <mixed set paths>`. If it is above 0, the classifier runs and its output is logged. At step 3 it is logged only; step 8 handles it.
    - **Guard at 0, or every counted commit bump-only:**
      - the orchestrator runs `git -C W merge --no-ff --no-edit <T>`;
      - on a conflict: `bump-resolve.ps1`, then the orchestrator commits. If the resolver refuses: `--abort`, and A3 takes over;
      - `restored-check.ps1 <merge>` must show only bump and blank lines.
    - **Any commit classed "other":**
      - A3 runs `git merge --no-commit` and applies D2, including the trivial-class rule, to the lines that arrived;
      - the orchestrator commits;
      - `restored-check.ps1 <merge>` passes;
      - D re-runs if A3 touched B's lines;
      - the tripwire is re-checked if the set grew.
    - **After every merge:**
      - the D18 proof;
      - blame in both modes, logged as "merged with test". The pass rule is D11's: `nontrivial` = 0, and `gamefact` only in the files §1 lists, each within its §1 count, at most 3 in total;
      - each trivial-count change is logged by file and category, with an allowed reason. For a `-w -M -C` trivial line outside the `ColorTableEditor*.cs` files, the reason is "copy-detected import block from a test commit". These changes don't fail.
    - **A merged tip that fails to build or test:**
      - B fixes it, and the fix SHA is logged;
      - the critic re-reviews;
      - D re-runs;
      - if QA has already run, QA item 1 is re-run on a re-copied build;
      - then the run resumes.

18. **"Fails the same way."** For tests present in step 2's TRX:
    - the skips are the same;
    - the normalised hashes are equal;
    - a mismatch goes to A, who answers "differs only in <kind>" or "different". "Different" is a failure;
    - a step-2 test that newly fails or skips is a failure;
    - a test new from `test` must pass, or skip with a reason.

19. **The clean security review and §12.**
    - A clean sub-agent applies the security-review method to the D8 diffs at the step-8 merged tip.
    - The critic and the reviewer each confirm S1–S10.
    - A non-bump re-merge gets the critic's security re-check.
    - Findings are handled per D20.

20. **Who fixes what.**
    - B, or a fresh clean implementer with B's allowlist and its own section, fixes B's code, the §6.3 and §6.4 blocks, and failing merged tips. Every fix SHA is logged.
    - The orchestrator fixes only tests, docs and the `BuildNumber` line.

21. **The filters.** Targeted runs only, plus the quick suite once before the push.
    - **WIDE-A:**

      ```
      FullyQualifiedName~IpcProviderSpecTests|FullyQualifiedName~ContribSpecTests|FullyQualifiedName~SidecarDiscovery|FullyQualifiedName~SidecarCache|FullyQualifiedName~ContentPieceSelectionTests|FullyQualifiedName~FaceUvDoublingTests|FullyQualifiedName~ShellMensLayoutTests|FullyQualifiedName~WomensArtOnMenTests|FullyQualifiedName~StutterFixesWiringTests|FullyQualifiedName~StutterFixesPureTests|FullyQualifiedName~DesignBinding|FullyQualifiedName~PlayerSettingsFilterTests|FullyQualifiedName~HarnessBridgeWrapperTests|FullyQualifiedName~HarnessBindingsWiringTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~IdleDiskIoWiringTests
      ```

      with `&Speed!=Slow`, plus the existing test classes for `ModsTab`, `CompositorService` and the carriers, found by name.
    - **Records:**

      ```
      FullyQualifiedName~ContribCleanRoomRecordTests|FullyQualifiedName~ReadmeTermsTests|FullyQualifiedName~PbdCleanRoomRecordTests
      ```

22. **The licence stays unchanged.**

23. **The slnx is regenerated** in its own commit, from §1's project list:
    - `dotnet new sln --name proteus --format slnx`;
    - then `dotnet sln proteus.slnx add <csproj>`, one project per call.

    It must exist before any test runs after `<R>`.

## Requirements
**User prompt (verbatim):** "publish the new licence"

**User's first choice (verbatim):** "Rewrite their code first (Recommended) — Clean-room rewrite of Caraxi's and AzureRavie's surviving code, then swap LICENSE, update SPDX headers and notices, and push to test."

**User's decision on ND-1 (verbatim):** "Contributor rewrite only — Do the Caraxi/AzureRavie clean-room now and stop; decide about Luna later. Licence stays AGPL for now."

**Feature arguments (verbatim):**
> Clean-room rewrite of the remaining third-party-authored code so Proteus can move from AGPL-3.0 to the Proteus Licence (docs/LICENSE-DRAFT.md), then publish the licence (swap LICENSE, update SPDX headers, NOTICE/THIRD_PARTY_NOTICES, README licence mentions; push to test). Code to replace, found by `git blame -w -M -C` on test: Caraxi (git@caraxian.com) — Proteus/Interop/IpcProvider.cs 137 lines (public IPC: overlay tables assigned to mod options; names/signatures must stay identical), Proteus/Interop/PenumbraBridge.cs 9, Proteus/Plugin.cs 6 (IPC registration; `/proteus refresh silent`), Proteus/Services/SidecarDiscoveryService.cs 6 (multi-select group default + per-option tables), Proteus/Gui/StatusWindow.cs 4 (clickable mod name opening Penumbra; priority DragInt fix), proteus.slnx 9 (regenerate), samples/ExampleMultiGroupOverlayMod/ (remake or delete); AzureRavie — Proteus/Interop/GlamourerBridge.cs 12 lines (cooldowns on equip attempt, Glamourer lock-conflict logging). Follow the same two-stage clean-room method as docs/pbd-clean-room.md / docs/pbd-clean-room-log.md (spec agent reads the old code; a fresh implementer never does; record every participant's reads). The orchestrating session has not read any of this code and must stay clean. Precedent: the PbdFile.cs rewrite (build 1466).

**Derived requirements:**
- R-1: The public IPC stays identical: names, argument and return types, behaviour, and the thread it answers on.
- R-2: The small fixes keep their behaviour. Spec §11 drops nothing.
- R-3: A dirty agent cleans the mixed files before the implementer sees them. The removal must be provable. Solona's own lines are restored, or their gaps rewritten.
- R-4: No licence change.
- R-5: Commits are authored as Solona, with no Co-Authored-By and no real name. Every sub-agent runs on `opus`. Push to `test` only; `main`, tags and deploys are hard stops.
- R-6: Targeted `--filter` runs only, plus the quick suite once before the push.
- R-7: AGENTS.md Rules 3, 4 and 5 apply.

## Contract
No routes, schema or persisted fields change.
- **Public IPC:** spec §2–3, pinned by `IpcProviderSpecTests`.
- **Seams:** spec §9.2–§9.5. B's new seams and files are logged with their names and signatures.
- **The `Proteus/Interop/IpcProvider.cs` header:**
  - line 1 is exactly `// SPDX-License-Identifier: AGPL-3.0-or-later`;
  - line 2 is `// Copyright (c) Solona`;
  - `docs/contrib-clean-room-spec.md` is named within the first 5 lines;
  - there is no "ported from".
- **Unchanged:** `LICENSE`, `docs/LICENSE-DRAFT.md`, the three guard tests, and every trivial-class line outside the mixed set.

## Approach
Every step ends with a commit made under D10.

0. **Hand-off.** Done (`71d450fb`). Commit rev 10 as `docs/contrib-clean-room-plan.md`.
1. **A, stage 1.** Done (`1a2cd3ef`). The tripwire fired there, which led to this re-plan.

   1b. **Spec amendment and tripwire re-check.** A (by SendMessage) amends the spec:
   - **§1:**
     - shows the trivial class as excluded and not removed, with per-file category counts;
     - lists the `ColorTableEditor*.cs` lines;
     - lists the 3 game-fact lines by `file:line` (the record at `<R>`), each with its one-word shape and the per-file counts;
     - restates the totals.
   - **§9.5 and §10:** files outside the mixed set don't change.
   - **§10:** the EDGE-5 hit is noted as kept.
   - **Confirmation:** by count only, every class line matches the regex or the game-fact rule.

   **Failure path.** A line that matches neither rule is non-trivial:
   - A adds its file to the mixed set in §1;
   - the orchestrator adds the file to D8, D9, D10, self-check 1 and the guard paths, and records it in the log;
   - the tripwire is re-checked, and the run re-plans if it fires.

   A updates its self-report. The orchestrator checks D10 (the spec and the log only), re-runs the D3 tripwire, logs it, and commits.
2. **Tests and the old-code gate.**
   - The orchestrator writes `IpcProviderSpecTests`, `ContribSpecTests` and the `ContribCleanRoomRecordTests` skeleton.
   - The gate (D5):

     ```
     dotnet test W/Proteus.Tests/Proteus.Tests.csproj -c Release --logger "trx;LogFileName=<scratchpad>\step2.trx" --filter "(<WIDE-A>)&CleanRoom!=NewSeam&CleanRoom!=NewBehaviour&FullyQualifiedName!~ContribCleanRoomRecordTests" > <scratchpad>\step2.out 2>&1
     ```

   - A build failure goes to `build-summary.ps1`, with CS1061 and CS1729 sent to A. Otherwise `trx-summary.ps1` reads the results, and failures go to A.
   - Log, then commit.
3. **A, stage 2: removal** (by SendMessage).
   - A removes the amended §1 set. Trivial-class lines outside the mixed set are untouched. A writes the manifest and drafts its self-report.
   - The orchestrator checks D10, runs `git add -u`, and commits `<R0>` without viewing it. Then:
     - `restored-check.ps1`;
     - `blame-authors.ps1` in both modes. This sets the baseline (at or below §1 per file and category, or logged with its file) and applies the D11 pass rule;
     - the guard and the classifier, logged only.

     A check failure sends A back for a fix-up.
   - When everything passes, A appends its self-report and deletes its folder. The orchestrator commits the log and deletes the manifest unread.
4. **slnx, then "not passing".**
   - Regenerate the slnx (D23), then commit.
   - Delete `bin/` and `obj/`, then confirm with `Test-Path`.
   - Run the step-2 filter and record "not passing". Log, then commit.
5. **B.** A fresh clean agent, briefed with the spec, R-1, R-2, D6, D9, D12 and the Contract.
   - B removes the markers, repairs syntax, rebuilds §6.3 and §6.4, and names every new file and seam.
   - The orchestrator extracts B's record, logs B's SHA, seams and files, and commits.
6. **Proof.**
   - Run the step-2 filter without the exclusions; D18 applies.
   - Run blame in both modes with the D11 pass rule. Trivial-count changes since `<R>` are logged by file and category, each with an allowed reason, and B's new files are named.
   - Check that no markers remain.
   - Log, then commit.
7. **D** (D7).
   - Log the first measurement before any rewrite.
   - Send hits to B.
   - Repeat step 6's checks, then re-measure.
   - Log, then commit.
8. **Merge `test`, then the gate.**
   1. **Merge.**
      - `git pull`, and record `T8`.
      - Record `P8`, run the guard and the classifier, then merge (D17).
      - Run `restored-check.ps1 <merge>`.
      - Run the D18 proof and blame in both modes ("merged with test"; D11 pass rule; changes logged with reasons).
      - Re-run D if needed. A failing tip goes to B.
      - Log, then commit.
   2. **Code-critic** on the merged tip (D8, D12, §12). Fixes and re-reviews until it passes. Log `<critic-pass-commit>`.
   3. **Security reviewer** (D19). A code fix needs a critic re-review, and the pass commit moves.
   4. **D re-run** if B's commits changed after step 7.
   5. **QA build.**
      - Bump to N = `max(branch, T8) + 1`. Commit "Build N for QA" (`Plugin.cs` only), and log its SHA.
      - Build Release, warn other sessions, copy to the dev-load folder, and check the timestamp.
   6. **Adversarial QA.**
      1. `/health` shows build N, and `/log` shows no IPC registration errors.
      2. The cooldown and the lock logging are UNVERIFIED by design: the equip route reaches neither. The unit tests are the evidence.
      3. `GET /sidecar` against a real multi-select overlay mod, checked against §5. UNVERIFIED if no such mod is installed.
      4. Also UNVERIFIED by design: the Mods-tab click and priority drag, `/proteus refresh silent`, another plugin's IPC calls, and the §6.3 grid lookup in game.
   7. **QA fixes** follow D20, or become `known-issue` tasks. A code fix needs a critic re-review, a D re-run and a re-copy.
9. **Re-merge if needed, then the final commit.**
   1. **Check for a moved `test`.** `git pull`, and record `T9`. If `T9` ≠ `T8`:
      - record `P9`, merge (D17), and run `restored-check.ps1`;
      - run the proof, and blame with the D11 rule (changes logged);
      - the critic re-reviews `git -C W diff <P9> HEAD`, or takes the lighter check for a bump-only merge with no conflict;
      - a non-bump re-merge also gets the security re-check;
      - if the tip fails, follow D17.
   2. **Licence files.** Run `git -C W diff test...HEAD --name-only` and log it with "licence files untouched".
   3. **Self-checks.** Run both and log them.
   4. **Final bump** (D15). If the last T's number is N or higher, re-run QA item 1.
   5. **Final commit:** the log sections and the bump.
   6. **Short re-review** (D15).
   7. **Final blame:** `blame-authors.ps1 "-w -M -C"` on the final tip, with the D11 rule, for the merge message.
   8. **Copy** the final build.
10. **Merge into `test`** (D16).
    1. `git pull`. If `test` has moved since the last T, go back to step 9.
    2. The guard must read 0.
    3. `git merge --no-ff solona-m/licence-clean-room`. The message is the PR description, and contains:
       - "clean-room in two stages";
       - the spec and log paths;
       - "licence unchanged";
       - "trivial coincidental lines left in place, logged";
       - the blame figures;
       - the re-review's reads;
       - Review & traceability;
       - no Co-Authored-By.
    4. Warn other sessions, then run the Records filter.
    5. Re-check blame in both modes on `test`, with the D11 rule (`nontrivial` = 0; game-fact lines per file within §1, at most 3 in total), for the final report. Trivial-count changes go in the final report with their reasons; they are not failures. If the rule fails:
       - because of a new contributor commit outside the mixed set: stop and re-plan;
       - otherwise: go back to step 9 with A3.

       Never push while it fails.
    6. Run the quick suite.
    7. Check that only `codeql.yml` triggers.
    8. `git push origin test`.
11. **Final report:**
    - decisions;
    - gaps in the proof, including the weaker AC-1;
    - D's first and last numbers;
    - the trivial-class totals and their changes;
    - the step-10 re-check;
    - UNVERIFIED items;
    - `known-issue` tasks;
    - owner follow-ups: Luna and Penumbra.Api, XivLiveMesh's licence, the licence wording, a README line, the workflows' origins, the NuGet notices, the lawyer's review, `main` and the release.

## Files
- EDIT `docs/contrib-clean-room-plan.md` (rev 10).
- EDIT `docs/contrib-clean-room-spec.md`: A's step-1b amendment.
- NEW/EDIT `docs/contrib-clean-room-log.md`. Its opening lines say there is no licence change, that the method extends `docs/pbd-clean-room.md` to mixed files, and that the tripwire fired at step 1 and was re-planned (rev 8). It holds:
  - the Method: `norm`, the BuildNumber pattern, the trivial regex, the game-fact file list with per-file counts and the rule, the sanctioned class, and the allowed change reasons;
  - every participant's section;
  - all SHAs;
  - the blame figures by category;
  - the baseline and its comparison with §1;
  - every trivial-count change, with its reason;
  - the restore checks;
  - the guard classes;
  - the rounds;
  - D's measurements;
  - runs, skips and hashes;
  - NewSeam counts;
  - B's seams and files;
  - the §12 tables;
  - the re-merge check;
  - the licence-files list;
  - both self-checks;
  - the tripwire record;
  - the L-4 disclosures.
- DELETE then NEW `Proteus/Interop/IpcProvider.cs`.
- EDIT the mixed-set files:
  - `PenumbraBridge.cs`
  - `GlamourerBridge.cs`
  - `Plugin.cs` (also the bumps)
  - `SidecarDiscoveryService.cs`
  - `StatusWindow.cs`
  - `ModsTab.cs`
  - `CompositorService.Carriers.cs`
  - `CompositorService.cs`
  - any file added by the 1b failure path
- DELETE then NEW `proteus.slnx`.
- DELETE `samples/ExampleMultiGroupOverlayMod/**`.
- EDIT `Proteus/Localization/*`, only if new UI text is added.
- NEW any `Proteus/**` file B names.
- NEW `Proteus.Tests/IpcProviderSpecTests.cs`, `Proteus.Tests/ContribSpecTests.cs` and `Proteus.Tests/ContribCleanRoomRecordTests.cs`.
- MERGE commits in `W`.
- Unchanged: the guards, `StutterFixesWiringTests`, `For Creators.md`, and every trivial-class file outside the mixed set.
- Not committed: the scripts, the `.trx` and `.out` files, the manifests, the checkpoint files, and the A, D and handover folders.

## Out of Scope
- Any licence change, Phase B and Phase C (owner follow-ups).
- The NuGet notices, the workflows' origins, XivLiveMesh's licence, the licence wording and a README line (owner follow-ups).
- `main`, tags, releases and deploys (hard stops).
- Asking for consent.
- New harness routes.
- Editing the `docs/pbd-*` records.
- Re-checking where other files came from.
- Remaking the sample.
- Removing or rewriting trivial coincidental lines outside the mixed set.

## Edge Cases
- EDGE-contrib-clean-room-1: A contributor line that was edited later. The closure covers it.
- EDGE-contrib-clean-room-2: A contribution that replaced Solona's lines. They are restored and verified, or a hole is left.
- EDGE-contrib-clean-room-3: Lines moved unchanged. Whole-tree blame in both modes finds them.
- EDGE-contrib-clean-room-4: Coincidental attribution. A trivial line outside the mixed set goes into the trivial class. A non-trivial coincidental line is classified by A3 or D, and only expressive lines go to B.
- EDGE-contrib-clean-room-5: An existing test asserts removed text. If the text is a §9 interface name, the test is kept (this is the one hit). Otherwise the assertion is replaced before `<R>`.
- EDGE-contrib-clean-room-6: `test` advances. The guard and the classifier run (logged only at step 3); the step-8 merge or the step-9 re-merge handles it. If it moves after step 9, go back to step 9.
- EDGE-contrib-clean-room-7: IPC calls with an unknown name, a different case, before ready or after dispose. Handled per §3.
- EDGE-contrib-clean-room-8: IPC calls from another thread. Handled per §3.
- EDGE-contrib-clean-room-9: Refresh variants. Handled per §4.
- EDGE-contrib-clean-room-10: Multi-select defaults, and options without tables. Handled per §5.
- EDGE-contrib-clean-room-11: A Glamourer lock, and the cooldown. Handled per §8.
- EDGE-contrib-clean-room-12: Penumbra is missing when a mod name is clicked. Handled per §7.
- EDGE-contrib-clean-room-13: A clean agent breaks a rule. Its work is void and redone, and the breach is logged.
- EDGE-contrib-clean-room-14: The bot commits. Recorded.
- EDGE-contrib-clean-room-15: The harness stays untouched.
- EDGE-contrib-clean-room-16: The slnx is gone after `<R0>`. No tests run before step 4.
- EDGE-contrib-clean-room-17: A step-2 test newly fails or skips. That is a failure.
- EDGE-contrib-clean-room-18: Solona's lines caught by the hunk rule (§6.3, §6.4). B rebuilds them, and NewSeam tests cover them.
- EDGE-contrib-clean-room-19: Broken syntax after removal. Expected.
- EDGE-contrib-clean-room-20: A disallowed added line. A fix-up is made; the orchestrator sees counts only.
- EDGE-contrib-clean-room-21: An unexpected path in `git status`. The commit stops.
- EDGE-contrib-clean-room-22: The tripwire fires. Re-plan. This has happened once, at step 1.
- EDGE-contrib-clean-room-23: Old-code output could reach the orchestrator. D5 prevents it.
- EDGE-contrib-clean-room-24: Log or UI text in removed lines. It is described, reworded and localised.
- EDGE-contrib-clean-room-25: The NewSeam share exceeds a third. Reported.
- EDGE-contrib-clean-room-26: The step-10 re-check fails the D11 rule (a non-trivial line, or a game-fact line outside its file limit).
  - From a new contributor commit outside the mixed set: re-plan.
  - Otherwise: go back to step 9 with A3.
  - Never push while it fails.
  - Trivial-count changes on their own, including copy-detected import blocks in `-w -M -C`, are reported with reasons. They are not failures.
- EDGE-contrib-clean-room-27: Behaviour no harness route can reach. UNVERIFIED, with the reason.
- EDGE-contrib-clean-room-28: For Creators naming the deleted sample. It doesn't, and a test pins that.
- EDGE-contrib-clean-room-29: Merge commits in history. Lines inherit their status; resolution lines get the hunk rule.
- EDGE-contrib-clean-room-30: Content moved with edits, including in `87ae2e3d`. It counts as carried over.
- EDGE-contrib-clean-room-31: Gated failures, and CS1061 or CS1729 on the old code. They go to A.
- EDGE-contrib-clean-room-32: A self-check or leak-check breach. Reported.
- EDGE-contrib-clean-room-33: Volatile message parts. `norm` handles them; otherwise A gives a kind-only answer.
- EDGE-contrib-clean-room-34: `test` brings only bumps. The orchestrator merges with the resolver.
- EDGE-contrib-clean-room-35: Tests new from `test`. They must pass, or skip with a reason.
- EDGE-contrib-clean-room-36: The QA build number is taken on `test`. The final bump goes above both, and QA item 1 is re-run.
- EDGE-contrib-clean-room-37: A merged tip fails. B fixes it, the critic re-reviews, D re-runs, QA item 1 is re-run if needed, and the run resumes.
- EDGE-contrib-clean-room-38: A commit lands after the critic's pass. A no-code commit goes to the short re-review; a code commit goes back to the critic.
- EDGE-contrib-clean-room-39: Trivial coincidental lines.
  - **What stays:** class lines outside the mixed set, namely:
    - the `ColorTableEditor*.cs` `-w -M -C` imports;
    - the `-C -C`-only import, namespace and blank lines;
    - the game-fact lines (only in the files §1 lists, each file within its §1 count, at most 3 in total; their positions within those files may move).
  - **Changes are logged, never capped,** each with an allowed reason. A `-w -M -C` trivial line outside `ColorTableEditor*.cs` is logged as "copy-detected import block from a test commit".
  - **Lines in mixed-set files** are removed.
  - **A line that fails both rules** is handled by when it shows up:
    - at step 1b, its file joins the mixed set;
    - at a later checkpoint, it fails, and goes to A3 (EDGE-4, D17).

## Test Strategy
All new tests carry `[Trait("Plan","<ID>")]`. NewSeam tests also carry `[Trait("Seam","§9.<row>")]`. Record tests only read files.

- [AC-contrib-clean-room-1] [EDGE-contrib-clean-room-7] [EDGE-contrib-clean-room-8] [EDGE-contrib-clean-room-25] `IpcProviderSpecTests`:
  - `Registers_exactly_the_documented_labels_and_signatures` (R7, gated);
  - `Dispose_unregisters_every_label` (R8, gated);
  - `ApiVersion_is_1_0_on_any_thread` (R1 and R10, gated);
  - `Each_label_behaves_as_the_spec_says` (R2–R6, R11 and R12; NewSeam where §9.1 says so; R5 and R6's guard and master-switch paths are gated).
- [AC-contrib-clean-room-2] [EDGE-contrib-clean-room-9] [EDGE-contrib-clean-room-10] [EDGE-contrib-clean-room-11] [EDGE-contrib-clean-room-12] [EDGE-contrib-clean-room-18] [EDGE-contrib-clean-room-24] `ContribSpecTests`:
  - `Refresh_arguments_follow_the_spec`
  - `Multi_select_group_with_no_selection_contributes_nothing`
  - `Per_option_tables_follow_the_spec`
  - `Priority_edit_follows_the_spec`
  - `Opening_a_mod_in_Penumbra_follows_the_spec`
  - `Glamourer_lock_conflict_is_logged_once_with_the_item`
  - `Glamourer_cooldown_starts_at_the_attempt`
  - `Content_index_lookup_follows_the_spec`
  - `Body_shape_signature_follows_the_spec`

  Also the existing §10 tests, `LocalizationTests`, and QA items 1 and 3.
- [AC-contrib-clean-room-3] [EDGE-contrib-clean-room-1] [EDGE-contrib-clean-room-2] [EDGE-contrib-clean-room-3] [EDGE-contrib-clean-room-14] [EDGE-contrib-clean-room-20] [EDGE-contrib-clean-room-29] [EDGE-contrib-clean-room-30] [EDGE-contrib-clean-room-34] [EDGE-contrib-clean-room-39]
  - `ContribCleanRoomRecordTests.Log_reports_no_contributor_lines` checks that the log contains:
    - the trivial regex, and the game-fact rule with its file list (the `file:line` record at `<R>`, a shape for each line, per-file counts, a total of at most 3);
    - the `<R>` baseline compared with §1 per file and category, with every difference listed against its file;
    - "nontrivial: 0" in both modes at `<R>`, step 6, the step-8 merged tip and any step-9 re-merge, and in `-w -M -C` after later fixes and at the final tip;
    - game-fact counts only in §1's files, each within its §1 count;
    - every "changed:" entry with a reason from the allowed list (including "copy-detected import block from a test commit" for any `-w -M -C` trivial line outside `ColorTableEditor*.cs`);
    - the closure rounds;
    - the restore checks;
    - the guard classes;
    - the dependabot entry.
  - `ContribCleanRoomRecordTests.Sample_gone_and_slnx_present`.
- [AC-contrib-clean-room-4] [EDGE-contrib-clean-room-4] `ContribCleanRoomRecordTests.Similarity_check_is_recorded_honestly`.
- [AC-contrib-clean-room-5] [EDGE-contrib-clean-room-13] [EDGE-contrib-clean-room-21] [EDGE-contrib-clean-room-22] [EDGE-contrib-clean-room-23] [EDGE-contrib-clean-room-32] `ContribCleanRoomRecordTests`:
  - `Spec_has_every_required_section_and_no_code`: the 12 headings, no fences, the trivial class shown as excluded, and the game-fact lines listed with their shapes and per-file counts.
  - `Log_lists_every_participant_and_the_clean_side_kept_clear`. It checks:
    - the sections, each with `Opened:`: Planner, every Plan-critic (1–6, 8, 9, and any 7 or later pass), Planner (rev 2)–(rev 10), Planning orchestrator, Agent A (self-report), Orchestrator, Agent B, Agent D (self-report), Code-critic, Security review, Adversarial QA, and any Agent A2 or A3 section;
    - the allowlists;
    - the zero counts;
    - identifier-grep hits at 0;
    - the start commits;
    - the §12 tables;
    - both self-check lines, with their exact exception lists, the sanctioned class, the in-window Grep facts, and "no exception (f) needed";
    - the L-4 disclosure paragraph;
    - the opening lines.
  - `Log_records_the_tripwire_replan`:
    - condition 1 under both readings, and condition 2 at 21 before and 0 after;
    - the `87ae2e3d` scope accepted;
    - "re-planned (rev 8)";
    - the step-1b result.
  - `IpcProvider_header_is_pinned`.
  - `No_hole_markers_remain`.
- [AC-contrib-clean-room-6] [EDGE-contrib-clean-room-16] [EDGE-contrib-clean-room-17] [EDGE-contrib-clean-room-19] [EDGE-contrib-clean-room-31] [EDGE-contrib-clean-room-33] [EDGE-contrib-clean-room-35] [EDGE-contrib-clean-room-37]
  - The runs at step 2, step 4, step 6 and every merged tip.
  - `ContribCleanRoomRecordTests.Log_compares_baselines_by_hash`.
  - The orchestrator's `--numstat` check.
- [AC-contrib-clean-room-7] The guard tests. `ContribCleanRoomRecordTests.Log_states_licence_files_untouched`.
- [AC-contrib-clean-room-8] [EDGE-contrib-clean-room-6] [EDGE-contrib-clean-room-26] [EDGE-contrib-clean-room-36] [EDGE-contrib-clean-room-38] The step-10 sequence, in the final report. `ContribCleanRoomRecordTests.Log_records_merges_and_the_final_bump`.
- [EDGE-contrib-clean-room-5] `StutterFixesWiringTests.CarrierWritesBracketTheInvokeWithAThreadStaticCounter`, unchanged: it passes at step 2, fails after `<R>`, and passes from step 6 on.
- [EDGE-contrib-clean-room-15] `HarnessBindingsWiringTests`.
- [EDGE-contrib-clean-room-27] QA's UNVERIFIED list, with reasons.
- [EDGE-contrib-clean-room-28] `ContribCleanRoomRecordTests.For_Creators_names_only_existing_samples`.

## Environment & Preconditions
- `PROTEUS_GAME`, `PROTEUS_MODS` and `PROTEUS_MODPACKS` are set.
- QA needs the game running, the harness on, and a multi-select overlay mod installed.
- The submodules are initialised.
- The build is broken from `<R0>` until step 5.
- The dev-load folder is written only in steps 8–10. Warn other sessions first.
- `-C -C` blame is slow. Run it in the background.

### Planner (rev 1) reads, for the log
Clean side. No show, `log -p`, diff or content blame of a forbidden path, and no URLs.

**Loaded automatically:** the global CLAUDE.md, the start folder's CLAUDE.md and AGENTS.md, the memory index, and the plan-draft skill.

**Files opened with Read** (all under `E:/orca/workspaces/Proteus/licence-clean-room/`):
- `docs/pbd-clean-room-plan.md` (lines 1–523, then 523–842)
- `docs/pbd-clean-room.md`
- `docs/LICENSE-DRAFT.md`
- `Proteus.Tests/PbdCleanRoomRecordTests.cs`
- `THIRD_PARTY_NOTICES.txt`
- `XivLiveMesh/NOTICE.md`
- `Proteus.Tests/ReadmeTermsTests.cs`
- `docs/pbd-clean-room-log.md` (lines 1–140 and 205–454)
- `Proteus/Proteus.csproj`
- `AI-DECLARATION.md` (lines 60–99)
- `TERMS.md` (lines 38–62)
- `.gitmodules`
- `Penumbra.Api/README.md`
- `Luna/README.md`
- `XivLiveMesh/XivLiveMesh.csproj`
- `Proteus/packages.lock.json` (lines 48–137)
- `Proteus.Tests/SupplyChainTests.cs` (lines 320–349 and 470–499)
- `README.md` (lines 40–64)
- `docs/agent-rules/plan-critic.md`

**Shell commands, verbatim:**
1. `wc -l E:/orca/workspaces/Proteus/licence-clean-room/docs/pbd-clean-room-log.md E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/PbdCleanRoomRecordTests.cs E:/orca/workspaces/Proteus/licence-clean-room/THIRD_PARTY_NOTICES.txt E:/orca/workspaces/Proteus/licence-clean-room/XivLiveMesh/NOTICE.md E:/orca/workspaces/Proteus/licence-clean-room/XivLiveMesh/LICENSE E:/orca/workspaces/Proteus/licence-clean-room/LICENSE E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ReadmeTermsTests.cs`
2. `grep -n "^## \|^### " E:/orca/workspaces/Proteus/licence-clean-room/docs/pbd-clean-room-log.md`
3. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -i -E "AGPL|Affero|LICENSE-DRAFT|Proteus Licence|LicenseRef|PackageLicense|SPDX" -- . ":!Glamourer" ":!Penumbra.Api" ":!Luna" ":!Proteus/Interop/IpcProvider.cs" ":!Proteus/Interop/PenumbraBridge.cs" ":!Proteus/Interop/GlamourerBridge.cs" ":!Proteus/Plugin.cs" ":!Proteus/Services/SidecarDiscoveryService.cs" ":!Proteus/Gui/StatusWindow.cs" ":!proteus.slnx" ":!samples/ExampleMultiGroupOverlayMod" ":!LICENSE" ":!XivLiveMesh/LICENSE" ":!docs/pbd-clean-room-plan.md" ":!docs/pbd-clean-room-log.md"`
4. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -i -E "licen[cs]e|licen[cs]ed" -- "*.md" "*.txt" "*.yml" "*.yaml" "*.json" "*.js" "*.ts" "*.toml" ":!Glamourer" ":!Penumbra.Api" ":!Luna" ":!samples/ExampleMultiGroupOverlayMod" ":!docs/pbd-clean-room-plan.md" ":!docs/pbd-clean-room-log.md" ":!docs/LICENSE-DRAFT.md" ":!THIRD_PARTY_NOTICES.txt" ":!worker/node_modules" ":!**/package-lock.json"`
5. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -c -E "using (Penumbra\.Api|Glamourer\.Api)|Penumbra\.Api\.|Glamourer\.Api\." -- "Proteus/*.cs" "Proteus.Tests/*.cs" "Proteus.Refit/*.cs" "XivLiveMesh/*.cs" ":!Proteus/Interop/IpcProvider.cs" ":!Proteus/Interop/PenumbraBridge.cs" ":!Proteus/Interop/GlamourerBridge.cs" ":!Proteus/Plugin.cs" ":!Proteus/Services/SidecarDiscoveryService.cs" ":!Proteus/Gui/StatusWindow.cs"`
6. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -c -E "using (Penumbra\.Api|Glamourer\.Api|Luna)|Penumbra\.Api\.|Glamourer\.Api\.|Luna\." -- Proteus/Interop/IpcProvider.cs Proteus/Interop/PenumbraBridge.cs Proteus/Interop/GlamourerBridge.cs Proteus/Plugin.cs Proteus/Services/SidecarDiscoveryService.cs Proteus/Gui/StatusWindow.cs`
7. `git -C E:/orca/workspaces/Proteus/licence-clean-room/Penumbra.Api ls-files -- "README*" "LICENSE*" "LICENCE*" "COPYING*" "*.md"`
8. `git -C E:/orca/workspaces/Proteus/licence-clean-room/Glamourer ls-files -- "README*" "LICENSE*" "LICENCE*" "COPYING*" "Glamourer.Api/README*" "Glamourer.Api/LICENSE*"`
9. `git -C E:/orca/workspaces/Proteus/licence-clean-room/Luna ls-files -- "README*" "LICENSE*" "LICENCE*" "COPYING*" "*/LICENSE*" "*/README*"`
10. `head -n 12 E:/orca/workspaces/Proteus/licence-clean-room/Glamourer/LICENSE E:/orca/workspaces/Proteus/licence-clean-room/Luna/LICENSE`
11. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -c -E "IpcSubscribers|EventSubscriber|FuncSubscriber|ActionSubscriber|GetIpcSubscriber|GetIpcProvider" -- "Proteus/*.cs" "Proteus.Tests/*.cs" "XivLiveMesh/*.cs" "Proteus.Refit/*.cs"`
12. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -h -o -E "using (Penumbra\.Api[A-Za-z.]*|Glamourer\.Api[A-Za-z.]*|Luna[A-Za-z.]*);" -- "Proteus/*.cs" "Proteus.Tests/*.cs" ":!Proteus/Interop/IpcProvider.cs" ":!Proteus/Interop/PenumbraBridge.cs" ":!Proteus/Interop/GlamourerBridge.cs" ":!Proteus/Plugin.cs" ":!Proteus/Services/SidecarDiscoveryService.cs" ":!Proteus/Gui/StatusWindow.cs"`
13. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "IpcSubscribers|EventSubscriber|FuncSubscriber|ActionSubscriber|GetIpcSubscriber|GetIpcProvider|using (Penumbra|Glamourer|Luna)" -- Proteus/TestHarness/GameHarnessAdapter.cs Proteus.Tests/HarnessBindingsWiringTests.cs Proteus/Interop/GlamourerSignals.cs`
14. `git -C E:/orca/workspaces/Proteus/licence-clean-room ls-files -- "*packages.lock.json" "Directory.Build.props" "Directory.Packages.props" ".github/*" "worker/wrangler*" "netlify.toml" "vercel.json" "Proteus/Plugin.json" "Proteus.Tests/SupplyChainTests.cs" "samples/*"`
15. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -A6 -E "^on:" -- .github/workflows`
16. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -i -E "licen|LICENSE|THIRD_PARTY|NOTICE|zip|Luna" -- .github/workflows Directory.Build.props Proteus/Plugin.json worker/wrangler.toml`
17. `git -C E:/orca/workspaces/Proteus/licence-clean-room shortlog -sne test -- XivLiveMesh Proteus.Refit Proteus.Tests worker .github docs README.md "For Creators.md" TERMS.md AI-DECLARATION.md Proteus/native THIRD_PARTY_NOTICES.txt`
18. `git -C E:/orca/workspaces/Proteus/licence-clean-room log test --format="%h %an %ad %s" --date=short --numstat --author=dependabot --author=github-actions`
19. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -c -i -E "IpcProvider|Proteus\.(Get|Set|Assign|List)|\"silent\"|refresh silent|SidecarDiscovery|StatusWindow|OpenModInPenumbra|OpenMainWindow|ExampleMultiGroupOverlayMod|multi.?select|GlamourerLock|cooldown" -- Proteus.Tests "Proteus/*.cs" ":!Proteus/Interop/IpcProvider.cs" ":!Proteus/Interop/PenumbraBridge.cs" ":!Proteus/Interop/GlamourerBridge.cs" ":!Proteus/Plugin.cs" ":!Proteus/Services/SidecarDiscoveryService.cs" ":!Proteus/Gui/StatusWindow.cs"`
20. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "IpcProvider|\"Proteus\.[A-Z][A-Za-z]+\"|refresh silent|\"silent\"" -- Proteus.Tests "Proteus/*.cs" README.md "For Creators.md" docs ":!Proteus/Interop/IpcProvider.cs" ":!Proteus/Interop/PenumbraBridge.cs" ":!Proteus/Interop/GlamourerBridge.cs" ":!Proteus/Plugin.cs" ":!Proteus/Services/SidecarDiscoveryService.cs" ":!Proteus/Gui/StatusWindow.cs" ":!docs/pbd-*"`
21. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -i -E "\bIPC\b" -- README.md "For Creators.md" docs/adr docs/product-context AGENTS.md`
22. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "\"refresh\"|\"full\"|OnCommand|CommandInfo|ParseRefresh" -- "Proteus/*.cs" Proteus.Tests ":!Proteus/Interop/IpcProvider.cs" ":!Proteus/Interop/PenumbraBridge.cs" ":!Proteus/Interop/GlamourerBridge.cs" ":!Proteus/Plugin.cs" ":!Proteus/Services/SidecarDiscoveryService.cs" ":!Proteus/Gui/StatusWindow.cs"`
23. `wc -l E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Interop/IpcProvider.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Interop/PenumbraBridge.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Interop/GlamourerBridge.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Plugin.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Services/SidecarDiscoveryService.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Gui/StatusWindow.cs E:/orca/workspaces/Proteus/licence-clean-room/proteus.slnx`
24. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "IsTypedOnly|/command|\"/proteus" -- Proteus/TestHarness Proteus.Tests/TestHarness docs/agent-rules/adversarial-qa.md`
25. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "ProjectReference|PackageReference|License|Reference Include" -- Proteus.Refit/Proteus.Refit.csproj Proteus.Tests/Proteus.Tests.csproj Directory.Build.props`
26. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -i -E "Luna|Penumbra\.Api|Glamourer\.Api|ProjectReference" -- Proteus.Tests/SupplyChainTests.cs Proteus.Tests/HarnessReleaseGuardTests.cs`
27. `ls E:/orca/workspaces/Proteus/licence-clean-room/docs/adr E:/orca/workspaces/Proteus/licence-clean-room/docs/product-context E:/orca/workspaces/Proteus/licence-clean-room/docs/agent-rules`
28. `grep -n -E "^#|route|Route" E:/orca/workspaces/Proteus/licence-clean-room/docs/agent-rules/adversarial-qa.md`
29. `grep -n -i -E "ApplyDesign|ApplyState|Interop/|IpcSubscriber" E:/orca/workspaces/Proteus/licence-clean-room/docs/adr/0002-qa-harness-compile-out-and-threat-model.md E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/HarnessBindingsWiringTests.cs`
30. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -i -E "lock|Restore" -- Directory.Build.props Proteus/Proteus.csproj .github/workflows/test.yml .github/workflows/release.yml`
31. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -c -E "(AC|EDGE)-licence-clean-room-" -- . ":!Glamourer" ":!Penumbra.Api" ":!Luna"`
32. `cmp E:/orca/workspaces/Proteus/licence-clean-room/AGENTS.md "E:/orca/workspaces/Proteus/E-orca-workspaces-Proteus-meddle-docs-pbd-clean-room-plan.md/AGENTS.md"`
33. `git -C E:/orca/workspaces/Proteus/licence-clean-room log --oneline test..HEAD`
34. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -o -E "BuildNumber = [0-9]+" test -- Proteus/Plugin.cs`

**Disclosed:**
- Command 34 printed only `test:Proteus/Plugin.cs:33:BuildNumber = 1466`.
- Command 14 listed the sample's file names.
- Commands 6 and 11 counted inside the mixed files.
- Commands 7–10 touched the submodules' licence and readme files.
- **L-4, the newly mixed files** (`ModsTab.cs`, `CompositorService.Carriers.cs`, `CompositorService.cs`):
  - **Command 12** printed whole import-directive lines (`using Penumbra.Api.Enums;` and `using Glamourer.Api.Enums;`) with `-h`, so without file names. Those include any such lines in `CompositorService.Carriers.cs` and `CompositorService.cs`, which command 5 shows reference the API namespaces.
    - Spec §1 gives the set's import lines in those files as kind R (`Dalamud.Interface.Utility.Raii`), so the printed lines are Solona's.
    - They are trivial import content. This is a planner-read disclosure.
  - **Commands 5, 11 and 19** were count mode: names and counts only. Commands 5 and 19 named `CompositorService.Carriers.cs` and `CompositorService.cs`.
  - **Content-mode commands 3, 20 and 22** printed no line from the three newly mixed files:
    - command 3 printed only `.cs` lines from `Proteus.Tests/PbdCleanRoomRecordTests.cs`, `Proteus.Tests/ReadmeTermsTests.cs` and the `XivLiveMesh/*.cs` SPDX lines;
    - command 20 printed only `Proteus.Tests/CompositorMathTests.cs:11`, `Proteus.Tests/CrossSexRefitTests.cs:398`, `Proteus.Tests/HarnessBuildFlagTests.cs:25`, `Proteus.Tests/HarnessReleaseGuardTests.cs:102`, `Proteus.Tests/LocalizationTests.cs` lines 1201, 1253, 1266 and 1267, `Proteus/AssemblyAttributes.cs` lines 3 and 4, `Proteus/Services/CompositorService.ShellRecovery.cs:249`, and the README/translation refresh lines;
    - command 22 printed only `Proteus.Tests/TestHarness/HarnessHostTests.cs:154`, `Proteus/Services/BodyRetarget.Crotch.cs:210`, `Proteus/Services/CompositorService.Trigger.cs:21`, `Proteus/Services/HairPort/HairPortScan.cs:93`, `Proteus/Services/HairPort/HairPortWatcher.cs:421` and `Proteus/TestHarness/HarnessRouter.Matcaps.cs:28`.

### Planner (rev 2)–(rev 10) reads, for the log
- **Rev 2.** No Read calls. Commands, verbatim:
  1. `grep -n -i -E "log|/state|/mods|sidecar|overlay" E:/orca/workspaces/Proteus/licence-clean-room/docs/agent-rules/adversarial-qa.md`
  2. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -l "proteus.slnx" -- Proteus.Tests`
- **Rev 3.** No Read calls. Command, verbatim:
  1. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "SetGearItem" -- Proteus/TestHarness/GameHarnessAdapter.cs`
- **Revs 4–7.** No tool calls.
- **Rev 8.** Opened `E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-spec.md`, lines 1–140 and 255–558 (lines 141–254 not read). Commands, verbatim:
  1. `wc -l E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-spec.md`
  2. `grep -n -E "^## |^### " E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-spec.md`
- **Revs 9 and 10.** No tool calls at all: no Read, no shell or git commands, no searches, no URLs.
- **All revisions** had the same automatic loads.

### Plan-critic reads, for the log
**Plan-critic 1 (on rev 1).** No forbidden reads and no sub-agents.

Files opened:
- the rev-1 plan output file
- `docs/agent-rules/plan-critic.md`
- `Proteus/Proteus.csproj`
- `docs/LICENSE-DRAFT.md`
- `THIRD_PARTY_NOTICES.txt`
- `XivLiveMesh/NOTICE.md`
- `AI-DECLARATION.md`
- `Proteus/packages.lock.json`
- `Proteus.Tests/Proteus.Tests.csproj` (lines 1–80)
- `XivLiveMesh/XivLiveMesh.csproj`
- `Proteus.Tests/HarnessBindingsWiringTests.cs` (lines 25–64)
- `docs/product-context/README.md`
- `docs/pbd-clean-room.md`

Commands, verbatim:
1. `ls E:/orca/workspaces/Proteus/licence-clean-room/docs/adr E:/orca/workspaces/Proteus/licence-clean-room/docs/product-context E:/orca/workspaces/Proteus/licence-clean-room/docs`
2. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -c -E "using Luna|Luna\." -- "Proteus/*.cs" "Proteus.Tests/*.cs" "XivLiveMesh/*.cs" "Proteus.Refit/*.cs" ":!Proteus/Interop/IpcProvider.cs" ":!Proteus/Interop/PenumbraBridge.cs" ":!Proteus/Interop/GlamourerBridge.cs" ":!Proteus/Plugin.cs" ":!Proteus/Services/SidecarDiscoveryService.cs" ":!Proteus/Gui/StatusWindow.cs"`
3. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -l -i -E "ported from|adapted from|based on .*(penumbra|meddle|textools|xande|brio|ktisis|glamourer|anamnesis|lumina|godbert|skintattoo)|derived from .*(penumbra|meddle|textools|xande|brio|ktisis|glamourer)|copied from|taken from|courtesy of|credit:" -- "Proteus/*.cs" "XivLiveMesh/*.cs" "Proteus.Refit/*.cs" "Proteus/native/*" ":!Proteus/Interop/IpcProvider.cs" ":!Proteus/Interop/PenumbraBridge.cs" ":!Proteus/Interop/GlamourerBridge.cs" ":!Proteus/Plugin.cs" ":!Proteus/Services/SidecarDiscoveryService.cs" ":!Proteus/Gui/StatusWindow.cs"`
4. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -c -i -E "Meddle|TexTools|xivModdingFramework|Xande|Brio|Ktisis|SkinTattoo|DragAndDrop|Anamnesis|Godbert|LooseTextureCompiler|Penumbra's (source|code|implementation)|ported|port of" -- "Proteus/*.cs" "XivLiveMesh/*.cs" "Proteus.Refit/*.cs" "Proteus/native/*" ":!Proteus/Interop/IpcProvider.cs" ":!Proteus/Interop/PenumbraBridge.cs" ":!Proteus/Interop/GlamourerBridge.cs" ":!Proteus/Plugin.cs" ":!Proteus/Services/SidecarDiscoveryService.cs" ":!Proteus/Gui/StatusWindow.cs"`
5. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -c -i -E "Meddle|TexTools|xivModdingFramework|Xande|Brio\b|Ktisis|SkinTattoo|Anamnesis|LooseTextureCompiler|Penumbra's (source|code|implementation)" -- "Proteus/*.cs" "XivLiveMesh/*.cs" "Proteus.Refit/*.cs" ":!Proteus/Interop/IpcProvider.cs" ":!Proteus/Interop/PenumbraBridge.cs" ":!Proteus/Interop/GlamourerBridge.cs" ":!Proteus/Plugin.cs" ":!Proteus/Services/SidecarDiscoveryService.cs" ":!Proteus/Gui/StatusWindow.cs"`
6. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -i -E "luna|penumbra\.api|glamourer\.api" -- Proteus/packages.lock.json`
7. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -l -E "LICENSE-DRAFT|Proteus Licence" -- . ":!Glamourer" ":!Penumbra.Api" ":!Luna"`
8. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -i -E "Luna|zip|Exclude|latest" -- Proteus.Tests/SupplyChainTests.cs .github/workflows/release.yml .github/workflows/testing-release.yml`
9. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "ProjectReference|PackageReference|Compile Include|Link=" -- Proteus.Refit/Proteus.Refit.csproj Proteus.Tests/Proteus.Tests.csproj XivLiveMesh/XivLiveMesh.csproj`
10. `git -C E:/orca/workspaces/Proteus/licence-clean-room ls-files -- "*packages.lock.json" "proteus.slnx" "*.sln" "Directory.Build.props" "Directory.Build.targets" "global.json" "nuget.config" "NuGet.config"`
11. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -l -E "Mono\.Cecil|System\.Reflection\.Metadata|MetadataLoadContext" -- "*.cs" "*.csproj" "*.props" ":!Glamourer" ":!Penumbra.Api" ":!Luna"`
12. `grep -n -i -E "ApplyDesign|ApplyState|Interop/|IpcSubscriber|Glamourer" E:/orca/workspaces/Proteus/licence-clean-room/docs/adr/0002-qa-harness-compile-out-and-threat-model.md`
13. `grep -n "^## \|^### " E:/orca/workspaces/Proteus/licence-clean-room/docs/pbd-clean-room-log.md`
14. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -i -E "slnx|\.sln\b" -- .github/workflows Proteus.Tests "*.md" ":!docs/pbd-clean-room-plan.md" ":!docs/pbd-clean-room-log.md"`

L-4 disclosure: commands 3, 4 and 5 touched `CompositorService.Carriers.cs` and `CompositorService.cs` by file name and count only. Command 3 listed names; commands 4 and 5 printed counts. No content was printed.

**Plan-critic 2 (on rev 2).** Opened only the rev-2 plan output file. Commands, verbatim:
1. `grep -n -i -E "/sidecar|/log|actions/equip|cooldown|lock" E:/orca/workspaces/Proteus/licence-clean-room/docs/agent-rules/adversarial-qa.md`
2. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "Equip|glamourer\.|GlamourerBridge|Cooldown" -- Proteus/TestHarness/GameHarnessAdapter.cs`
3. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -c -E "Process|\"git\"|git " -- Proteus.Tests/PbdCleanRoomRecordTests.cs Proteus.Tests/ReadmeTermsTests.cs`
4. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "Process|\"git\"|git " -- Proteus.Tests/PbdCleanRoomRecordTests.cs`

**Plan-critics 3–6, 8 and 9.** Each opened only the plan output file of the revision it reviewed:

| Critic | Revision | Lines read |
|---|---|---|
| 3 | rev 3 | 1–470, 470–770 |
| 4 | rev 4 | 1–480, 480–649, 649–728 |
| 5 | rev 5 | 1–420, 420–719, 720–919, then to the end |
| 6 | rev 6 | 1–120, 120–449, 450–779, 780–979 |
| 8 | rev 8 | 1–135, 134–368, 369–528, 529–698 |
| 9 | rev 9 | 1–42, 215–374, 459–483, 548–672 |

Commands, verbatim (Plan-critics 3, 4 and 5 ran none):
- **Plan-critic 6:** `grep -n -E "^## |^[0-9]+\. \*\*|^   [0-9]+\. |^8\.|^9\.|^10\." "C:/Users/solon/.claude/projects/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/toolu_01WHd8VAiL2zvRrzNpc9U1fr.txt"`
- **Plan-critic 8:** `grep -n -E "^## |^[0-9]+\. \*\*|^   [0-9]+[a-z]?\. \*\*" "C:/Users/solon/.claude/projects/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/toolu_01NyKy9HngjsMs9KVXr2vbeh.txt"`
- **Plan-critic 9:** `grep -n -E "^## |^[0-9]+\. \*\*|^   [0-9]+[a-z]?\. \*\*" "C:/Users/solon/.claude/projects/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/toolu_014i7hYS36gwktxroFMLmjga.txt"`

None of them read anything forbidden, opened URLs or used sub-agents.

### Planning orchestrator reads, for the log
Copied verbatim from the orchestrator's reports.

**Self-check 1's disclosed exception:** the meddle `Plugin.cs` Read of lines 31–34 (offset 31, limit 4, for the BuildNumber bump). The blame count printed 0.

**Before the self-check-2 window** (disclosed, not counted):
- `git -C E:/orca/workspaces/Proteus/meddle shortlog -sne test`
- `git -C E:/orca/workspaces/Proteus/meddle log test --author=Caraxi --format="%h %ad %s" --date=short --numstat`
- `git -C E:/orca/workspaces/Proteus/meddle log test --author=AzureRavie --format="%h %ad %s" --date=short --numstat`
- the blame-count pipelines `git -C E:/orca/workspaces/Proteus/meddle blame -w -M -C --line-porcelain test -- <file> | grep -c "^author Caraxi"` (over the six Caraxi files), and `... | grep -c "^author AzureRavie"` (over `GlamourerBridge.cs` and `CompositorService.cs`)
- `git -C E:/orca/workspaces/Proteus/meddle ls-tree -r --name-only test -- samples proteus.slnx`
- `git -C E:/orca/workspaces/Proteus/meddle show test:LICENSE --format= -s`
- the meddle Grep runs: `Caraxi` (files_with_matches, no hits) and `Caraxi|AzureRavie` (content: one Glamourer changelog line, no code)

**Inside the window:**
- (a) `git -C E:/orca/workspaces/Proteus/meddle worktree add -b solona-m/licence-clean-room E:/orca/workspaces/Proteus/licence-clean-room test` (the window start)
- `git -C E:/orca/workspaces/Proteus/licence-clean-room submodule update --init --recursive`
- `wc -l docs/pbd-clean-room.md docs/pbd-clean-room-plan.md docs/LICENSE-DRAFT.md docs/pbd-clean-room-log.md THIRD_PARTY_NOTICES.txt XivLiveMesh/NOTICE.md`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room ls-files --stage Glamourer Luna Penumbra.Api .gitmodules` (sanctioned)
- (d) `head -n 3 Luna/LICENSE Glamourer/Glamourer.Api/LICENSE Penumbra.Api/LICENSE Luna/ImSharp/LICENSE`
- `ls Penumbra.Api Glamourer/Glamourer.Api Glamourer` (sanctioned)
- (e) `head -n 3 Glamourer/LICENSE`
- (b) `git -C E:/orca/workspaces/Proteus/licence-clean-room blame -w -M -C --line-porcelain -L 31,34 test -- Proteus/Plugin.cs | grep -c "^author-mail <git@caraxian.com>"` (printed 0)
- (c) the transcript-jsonl greps. They printed tool-call inputs only, with no mixed-file content.
- the Grep runs:
  - (i) `ProjectReference|PackageReference`, content mode, over the three csproj files. Allowed lines only.
  - (ii) `using Luna|Luna\.`, count mode, over `W/Proteus` `*.cs`. It named only `GlamourerBridge.cs:1` and `PenumbraBridge.cs:1`.
  - (iii) `SPDX-License-Identifier`, count mode.
  - (iv) `AGPL|Affero|LICENSE-DRAFT|Proteus Licence`, files_with_matches. It found 18 of Proteus's own files.
- one Glob, of the session transcript

No exception (f) is needed. L-4: these count and files-with-matches greps touched the newly mixed files by name or count only, if at all. They are sanctioned.

**After rev 7:** step 0's blame-authors runs, the commits `71d450fb` and `1a2cd3ef`, and reading the committed spec, all logged by the orchestrator. Self-check 1 is re-evaluated against the eleven-entry mixed set.

**Files read:** `docs/pbd-clean-room.md`, `docs/LICENSE-DRAFT.md`, `THIRD_PARTY_NOTICES.txt`, the rev-1 to rev-9 plan outputs, the plan-critic reports, and the committed spec.

## NEEDS_DECISION
None. The user decided ND-1. The orchestrator decided ND-3, the self-check option, and the rev-8 to rev-10 amendments. The earlier ND-2, ND-4, ND-5 and ND-6 are owner follow-ups.

## Risks
- **Leaks to the clean side.** Possible routes:
  - pre-`<R>` diffs and merge diffs;
  - the security review's `origin/HEAD` diff;
  - old-code output;
  - assertions holding removed text;
  - conflict content;
  - identifiers at Solona's call sites (interface names, logged);
  - marker locations (disclosed);
  - the handover folder;
  - relative paths.

  Mitigations: D5, D6, D8–D12, D16, D17, D19, EDGE-5, EDGE-13 and EDGE-32.
- **The trivial class.** It rests on the judgment that import, namespace, blank and brace lines, and the narrowly defined game-fact lines, carry no expression.
  - The regex, the game-fact file list and every count change are logged for the lawyer's review.
  - No claim of "zero lines in any mode" is made.
  - Changes are logged rather than capped, so ordinary import moves and copy detection on `test` don't cause false failures. The non-trivial rule and the per-file game-fact limit stay hard.
- **The `87ae2e3d` scope.** B rebuilds 32 lines of Solona's own code from a description. NewSeam tests cover them. In game, they are UNVERIFIED.
- **AC-1 on the old code** rests mostly on NewSeam tests. This is reported.
- **The cooldown and the lock logging** can't be reached through the harness. Unit tests are the only evidence.
- **The Interop Sensitive Area.**
  - Changes are confined to the removed regions.
  - §12 is confirmed twice.
  - The 21 Sensitive-Area files that hold trivial-class lines are untouched.
- **Merging `test` mid-branch.** Covered by:
  - the step-8 merge before review;
  - the step-9 re-merge check;
  - the short re-review of every post-pass commit;
  - the guard, the classifier, the resolver and A3;
  - EDGE-37.
- **Process departures.** B writes the production code, and a clean sub-agent does the security review. Both are noted in Review & traceability.
- **Plugin reloads.** Warn other sessions first.
- **Left to the owner:**
  - the UNVERIFIED items;
  - Luna and Penumbra.Api;
  - the NuGet notices;
  - the lawyer's review, including the trivial-class judgment and the L-4 disclosures;
  - `main` and the release.

Relevant paths:
- E:\orca\workspaces\Proteus\licence-clean-room\docs\contrib-clean-room-spec.md
- E:\orca\workspaces\Proteus\licence-clean-room\docs\contrib-clean-room-plan.md
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\PbdCleanRoomRecordTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\ReadmeTermsTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\HarnessBindingsWiringTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus\TestHarness\GameHarnessAdapter.cs
- E:\orca\workspaces\Proteus\licence-clean-room\docs\agent-rules\adversarial-qa.md