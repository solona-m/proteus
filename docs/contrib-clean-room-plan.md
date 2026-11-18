> **Hand-off copy.** This is revision 7 of the plan, the version plan-critic 7 passed with Confidence HIGH (plan-critic 1–6: MEDIUM, MEDIUM, MEDIUM, MEDIUM, MEDIUM-HIGH, HIGH). Every critique amendment was folded in through the planner.
>
> **Log inputs from the planning session.** The planning participants' reads (Planner rev 1–7, Plan-critic 1–7 and the Planning orchestrator, with its disclosures and exceptions) are listed under *Environment & Preconditions* below. The orchestrator copies them into `docs/contrib-clean-room-log.md`. Plan-critic 7 opened only the rev-7 plan output (lines 1–28 and 293–392) and ran one shell command: `grep -n -E "^## |^1[25]\. \*\*|Delta|self-check 2|name-only|critic-pass|QA-bump|\(f\)" "C:/Users/solon/.claude/projects/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/toolu_01Y91ZpRto4LnGvgWTBYBWcP.txt"`. It opened no forbidden path and ran no sub-agents.

# Implementation Plan: Clean-room replacement of Caraxi's and AzureRavie's code (revision 7)

## Delta (rev 7 vs rev 6)
Plan-critic 6's two wording fixes are folded in.

- **J-1: Self-check 2 now counts only content.**
  - **Counts:** content-mode Grep result lines, Read paths, and the listed content-reading shell commands.
  - **Name-only listings, never counted:** Grep in `count` mode, Grep in `files_with_matches` mode, and Glob results. They join the sanctioned class.
  - **The in-window Grep and Glob runs are now logged** from the orchestrator's own tool-call inputs:
    - (i) a content-mode Grep, whose result lines came only from allowed csproj files;
    - (ii)–(iv) count and `files_with_matches` Greps, name-only;
    - one Glob of the session transcript.

    No content-mode Grep returned a line in a counted location, so no exception (f) is needed. The log says so.
  - Changed in: D12, the Planning orchestrator section, `Log_lists_every_participant_and_the_clean_side_kept_clear`, EDGE-32.
- **J-2: The short re-review now covers every commit after the critic's last pass.**
  - `<critic-pass-commit>` (the commit at which the code-critic last passed) is logged.
  - On top of its three `git show` commands, the re-reviewer runs:
    - `git -C W log --format=%h%x20%s <critic-pass-commit>..HEAD`
    - `git -C W show <QA-bump commit> -- Proteus/Plugin.cs`
    - `git -C W diff --stat <critic-pass-commit> HEAD`

    The last command is a small planner addition. It confirms that only `docs/contrib-clean-room-log.md` and `Proteus/Plugin.cs` changed since the pass.
  - This covers the QA-bump commit and any no-code log commits after the pass (AGENTS.md Rule 4). A code change after the pass still needs a critic re-review, as before.
  - Changed in: D12 (re-reviewer allowlist), D15, step 9, `Log_records_merges_and_the_final_bump`.
- **Log inputs:** Plan-critic 6's reads are added. Planner (rev 7) made no tool calls.

Unchanged from rev 6: the scope (Phase A only; the licence stays AGPL), the slug, the 8 ACs, D1–D23 and the step order.

## Approval Summary
**Goal:** Replace every line Caraxi and AzureRavie wrote with code written by an agent that never saw theirs. Proteus's public IPC and the small fixes must stay exactly as users and other plugins know them. The result is a record that makes a later relicensing possible. The licence stays AGPL-3.0 in this feature.

**Slug:** `contrib-clean-room`

**Acceptance Criteria:**
- AC-contrib-clean-room-1: A plugin calling Proteus's public IPC sees the same labels, argument types, return types and results as before. Tests written from the spec prove this: they pass on the old code and on the clean code.
- AC-contrib-clean-room-2: The contributors' small features still work as before, unless the spec drops one with a reason:
  - `/proteus refresh silent`, alone or with `full`;
  - clicking a mod's name in the status window opens it in Penumbra;
  - the priority control edits the priority correctly;
  - multi-select group defaults and per-option overlay tables;
  - case-insensitive overlay lookup;
  - the Glamourer cooldown counts from the equip attempt;
  - lock conflicts are logged.
- AC-contrib-clean-room-3: None of the contributors' lines remain.
  - Whole-tree blame gives 0 lines to Caraxi or AzureRavie, in both `-w -M -C` and `-w -M -C -C`, at four points: `<R>`, the step-6 tip, the branch merged with `test` at step 8, and any step-9 re-merge.
  - At the final tip, `-w -M -C` blame gives 0. The step-10 re-check on `test` goes in the final report.
  - The derived closure is removed (direct, derived, carried-over and merge-inherited lines), and its rounds and counts are logged.
  - `<R0>…<R>` and every merge into the branch add only C# markers, verified restored lines, blanks, and (in merges only) the BuildNumber token. Unmatched and other are both 0.
  - The sample folder is deleted and `proteus.slnx` is regenerated.
- AC-contrib-clean-room-4: B's code is shown to be independent. At the final tip, Agent D finds 0 expressive non-interface lines in B's commits that are identical to any removed line. Every run's numbers are logged, starting with the first, with each hit's category.
- AC-contrib-clean-room-5: The record is complete:
  - the spec is facts only and has all 12 sections;
  - the log lists every participant's reads, with commands verbatim;
  - the clean agents' sections are extracted from their transcripts and stay inside their allowlists;
  - both orchestrator self-checks are 0 beyond their named exceptions, and the window start and the sanctioned class are logged;
  - the dirty agents' self-reports give 0 identifier-grep hits;
  - the record tests pass, and they only parse files.
- AC-contrib-clean-room-6: The proof holds:
  - before removal, the old-code gate (spec tests plus WIDE-A) is green, read only through the hashing TRX script;
  - after removal, the spec tests don't pass;
  - on the clean code, and on every merged tip, everything passes. For tests that existed at step 2, the skip list is the same, and the only failures are baselines whose normalised hashes equal step 2's. A classes any mismatch as "differs only in <kind>" or "different";
  - tests that arrive with `test` pass, or skip on their own.
- AC-contrib-clean-room-7: The licence is untouched:
  - `LICENSE` is still the AGPL;
  - `docs/LICENSE-DRAFT.md` still says "DRAFT, not yet in force";
  - the three guard tests are unchanged and green;
  - the step-9 file-list check, made after the last merge of `test` and logged in the final commit, lists no licence or notice file.
- AC-contrib-clean-room-8: Finish:
  - the branch merges into `test` locally, adding nothing beyond the last reviewed merge state;
  - the Records filter, the blame re-check (0/0) and the quick suite pass on `test`;
  - only `test` is pushed: nothing goes to `main`, there are no tags, and no release or deploy workflow runs.

**Key decisions:**
- **Agent A** (dirty) writes the spec. After the orchestrator's tests from it are proven on the old code, A removes the contributor lines and everything derived from them. It may only delete, restore lines word for word, and add markers, and it never commits.
- **Agent B** (clean, fresh) fills the holes and rewrites `IpcProvider.cs`.
- **Agent D** (dirty, fresh) compares B's commits with the whole removed set, and reports only numbers and categories.
- **The clean side never sees the old code.** The orchestrator, B, the critics, the security reviewer and QA view only the clean diffs. Before `<R>`, test output reaches the orchestrator only as names, outcomes, types, codes and normalised hashes. Every script that touches mixed paths prints counts only.
- **The security review** runs as a clean sub-agent on the clean diffs, never as the `/security-review` skill in the orchestrator.
- **`test` is merged in at the start of step 8,** so review, the security review, D and QA all see the merged tip. A late re-merge in step 9 gets its own critic check. The short re-review covers every commit after the critic's last pass. Build-number bumps happen only after `<R>`, and the final number goes above both branches.

**Risk flags:** security surface: yes (`Proteus/Interop/` bridges, Proteus's public IPC) · new persisted field: no · sensitive-category data: no · schema migration: no · new dependency: no · new/changed route: no

**NEEDS_DECISION:** None. ND-1 was decided by the user; ND-3 and the self-check option by the orchestrator. The other earlier items are owner follow-ups.

*(The summary above is what the developer approves; everything below is the detailed contract it stands on.)*

## Source
- The user's prompt, choices, decision and feature arguments (verbatim under Requirements).
- Precedent: `docs/pbd-clean-room.md`, `docs/pbd-clean-room-plan.md` (rev 6), `docs/pbd-clean-room-log.md` and `Proteus.Tests/PbdCleanRoomRecordTests.cs`.
- Context only, unchanged: `docs/LICENSE-DRAFT.md`, `THIRD_PARTY_NOTICES.txt` and `XivLiveMesh/NOTICE.md`.
- The reports of Plan-critics 1–6, all folded in.
- Not opened, and forbidden to the planner: the mixed files and their history, the contributors' commits, the Luna, Glamourer and Penumbra.Api sources, and the contributors' PRs.

## Context & Decisions
**Problem.** Caraxi (11 commits) and AzureRavie (1 commit) still have lines surviving on `test`. Under §6 of the draft licence, those lines stay under the AGPL unless their authors agree. The user chose to rewrite them, and to stop before relicensing.

The code sits mostly inside files Solona otherwise wrote. So A, who may see it, removes it first. Four things prove the removal:
- blame counts;
- bounded additions;
- D's similarity check;
- tests that fail without the code and then pass again on the clean code.

**Facts (from allowed files only):**
- **Surviving lines** (blame `-w -M -C` on `test`, counts from the orchestrator):

  | File | Lines | Contributor lines |
  |---|---|---|
  | `IpcProvider.cs` | 150 | 137 (Caraxi) |
  | `PenumbraBridge.cs` | 869 | 9 (Caraxi) |
  | `Plugin.cs` | 659 | 6 (Caraxi) |
  | `SidecarDiscoveryService.cs` | 1531 | 6 (Caraxi) |
  | `StatusWindow.cs` | 2675 | 4 (Caraxi) |
  | `proteus.slnx` | 10 | 9 (Caraxi) |
  | `GlamourerBridge.cs` | 476 | 12 (AzureRavie) |

- **The sample folder** is unchanged since Caraxi added it. AzureRavie's `08cd11b2` also touched `CompositorService.cs`, where 0 lines survive.
- **No test or doc names** `IpcProvider` or a `"Proteus.*"` label.
- **`/proteus refresh silent`** is documented in README line 56 and its 7 translations. The harness has no chat-command route.
- **Harness routes:**
  - `GET /health`;
  - `GET /log`;
  - `POST /actions/equip`, which calls `glamourer.SetGearItem(slot, id, dye1, dye2)` (`GameHarnessAdapter.cs:196`);
  - `GET /sidecar?mod=&group=&option=&row=`;
  - `POST /actions/mod-settings`.
- **Tests:**
  - 12 test files find the repo root through `proteus.slnx`;
  - the record tests only read files;
  - `HarnessBindingsWiringTests` reads `GlamourerBridge.cs` source (ADR 0002).
- **A push to `test`** runs only `codeql.yml`.
- **`BuildNumber`** is 1466 on `test`. The scripts match its line with `^\s*public const int BuildNumber = \d+;\s*$`. If the real form differs, the pattern is adjusted at step 0 from a token read and logged.
- **Bot commits:** `8c787a80` (a version bump) and `d046b185` (a merge with no content).
- **Paths:**
  - `W = E:/orca/workspaces/Proteus/licence-clean-room`;
  - branch `solona-m/licence-clean-room`;
  - the session start folder is a separate checkout, so every agent uses absolute `W` paths;
  - if `W`'s `test` ref differs from the primary checkout's after a pull, use `fetch` and `FETCH_HEAD`, after `rev-parse` shows they are equal.

**Decisions (and rejected alternatives):**

1. **Roles.** Every sub-agent runs on model `opus`.

   | Role | Side | Reads | Writes | Shell / git |
   |---|---|---|---|---|
   | Orchestrator | clean | anything D9 allows; the mixed set only at or after `<R>` | tests, docs, the log, the slnx regeneration, the `BuildNumber` line; **every commit**, including A's, A3's and bump-only merges, made under D10 without viewing them | D10/D11; before `<R>`, D5; count-only scripts on mixed paths |
   | Agent A (fresh `general-purpose`; stage 1 spec, stage 2 removal, continued by SendMessage) | dirty | the whole repo and its history; the TRX files (D5, D18). Never the submodule sources, other checkouts, the web, or the memory topic files | the spec and its self-report; the removal (deletions, word-for-word restores, C# markers); the manifest; the removal self-report | git read commands, `dotnet build`, `dotnet test`. **Never commits**, pushes or merges |
   | A2 (fallback for stage 2) / A3 (EDGE-4, and D17 "other" commits) | dirty | as A; both recompute the closure from history | as A | as A. **A3 alone** may run `git -C W merge --no-commit test`; the orchestrator commits |
   | Agent D (fresh `general-purpose`; after step 6 and whenever B's commits change) | dirty | the mixed set at `<R0>^` and its history; B's commits (SHAs logged); A3's resolution lines; the spec | numbers, categories, clean-side `file:line`; self-report | git read commands and its own scripts; never commits |
   | Agent B (fresh `general-purpose`, after `<R>`) | clean | everything at HEAD except D9 | the holes, the new `IpcProvider.cs`, Localization entries, new `Proteus/**` files (named), fixes to merged tips | D12; no git; no sub-agents |
   | Code-critic | clean | the D8 diffs, files at HEAD, the spec, the log | findings, the §12 table | D12 |
   | Security reviewer (`general-purpose`, applying the security-review method) | clean | as the code-critic | findings, the §12 table | D12 |
   | Adversarial QA | clean | `docs/agent-rules/adversarial-qa.md`, the Requirements, spec §4–9, files at HEAD after `<R>`, the harness config | QA report | `curl` to `127.0.0.1`/`localhost` only |
   | Short re-reviewer | clean | the commits after `<critic-pass-commit>` | verdict | D15 |
   | Planner, plan-critics | clean | the plan and allowed files | reports listing their reads | as reported |

   - **Folders**, each outside `W`, a sibling of the session scratchpad, and logged:
     - `scratchpad-agentA\` (A, A2, A3);
     - `scratchpad-agentD\`;
     - `scratchpad-handover\` (the manifest only).
   - **Rejected:**
     - the orchestrator removing the lines, because it would see them;
     - A running the similarity check, because a fresh D is independent of the removal;
     - dirty agents committing, because a single committer keeps D10 in one place.

2. **The removal set.** A computes it before writing the spec, and lists it in §1 by file, category and count, with no code.
   - **Direct:** every line at the `test` tip that whole-tree blame (`-w -M -C` or `-w -M -C -C`) gives to either contributor. Their emails come from `git shortlog -sne test`.
   - **Derived (a transitive closure):**
     - Walk the commits in topological order, from the earliest contributor commit to the `test` tip.
     - In a non-merge commit X, every line added in a hunk of `git diff -U0 -w -M X^ X` that removed a contributor line or an already-derived line is derived.
     - In a merge commit, a line inherits its status from the parent it came from. Only lines that are new against every parent get the hunk rule, applied against each parent.
     - Repeat until a round adds nothing. The rounds are logged.
   - **Carried over:** for each commit that deletes contributor or derived lines with no replacement in the same hunk, A inspects what that commit and the next five commits touching `Proteus/` added in other files.
     - Content carried over counts as derived, even if it was edited along the way. This includes `Strings.cs` entries and their translations.
     - It is listed in §1 as "carried over", and the files it lands in join the mixed set.
   - **Whole files:** `Proteus/Interop/IpcProvider.cs`, `proteus.slnx` and `samples/ExampleMultiGroupOverlayMod/**`.
   - **Restored lines:**
     - These are Solona's lines that a contributor commit replaced or deleted. A copies them word for word from that commit's parent, where blame gives them to one of Solona's emails.
     - They go back only where they fit; otherwise the gap becomes a hole.
     - The manifest is `<session scratch root>\scratchpad-handover\contrib-restored-manifest.tsv`, with columns `<path at R>\t<line at R>\t<source commit>\t<source path>\t<line at source^>`.
   - **A only** deletes, restores word for word, and adds markers. It never edits a line it keeps. A broken build is expected, and B repairs it.
   - **Markers:**
     - In C# files only: `// CLEAN-ROOM HOLE: spec §<n>`. A marker discloses a location, but no code.
     - Holes in non-C# files appear only in A's self-report.
     - B removes every marker.
   - **The spec** covers the current behaviour of every line in the set.

3. **The scope tripwire.** Before step 2, the orchestrator re-plans through planner and plan-critic if either holds:
   - derived plus carried-over plus restored lines exceed twice the direct lines;
   - the set includes a Sensitive-Area file (as listed in AGENTS.md) beyond the six known files.

4. **Tests come from the spec and are proven on the old code before removal.**
   - Assertions come only from the spec.
   - A test that needs a seam the old code lacks carries `[Trait("CleanRoom","NewSeam")]` and `[Trait("Seam","§9.<row>")]`. A test of a departure from the old behaviour carries `NewBehaviour`. Both are left out of the gate.
   - The counts are logged per class. If NewSeam tests are more than a third of `IpcProviderSpecTests`, AC-1 counts as weaker.
   - No test is ever weakened.

5. **The old-code output filter.** This applies to every orchestrator `dotnet` run before `<R>`.
   - Each run uses `--logger "trx;LogFileName=<session scratchpad>\<run>.trx"` and redirects console output to `<run>.out`. The `.out` file is never opened and is deleted afterwards. The step-2 `.trx` is kept until every comparison is done.
   - `trx-summary.ps1` prints, per test: the name, the outcome, the skip reason, the assertion exception type, and `sha256(norm(message))[0..16]`. For any other exception, it prints only the type.
   - `norm` is fixed and logged:
     - CRLF becomes LF, and the message is trimmed;
     - paths become `<path>`;
     - GUIDs become `<guid>`;
     - digit runs become `<n>`.
   - `build-summary.ps1` prints error codes and their counts only.
   - Gated failures go to A with the TRX path, and so do CS1061 and CS1729 errors (as "spec error?").
   - A answers either "test bug: <fix>" or "spec error: §n amended", and never quotes old-code output.

6. **The strings rule.**
   - The spec describes log and UI text, without quoting it.
   - Tests assert structured facts.
   - Exact strings are used only for interface facts: the IPC labels, and the command words `refresh`, `full` and `silent`.
   - New user-visible text goes through `Strings.cs`, in all 8 locales.

7. **Agent D's similarity check.** D runs after step 6, and whenever B's commits change.
   - **Input:** the lines added by B's commits that survive at the current tip. That covers B's first commit and every later B fix commit, including merged-tip fixes; their SHAs are logged, and their lines come from `git diff -U0 c^ c`. A3's resolution lines are added too. D never uses `git diff <R> HEAD` once `test` has been merged in.
   - **Normalisation:** comments are stripped and whitespace collapsed. A line is trivial if it has fewer than 20 alphanumeric characters, or is a `using` directive.
   - **Comparison:** every non-trivial input line against the whole removed set across all files: the lines removed in `<R0>^…<R>`, plus the deleted whole files.
   - **Categories:**
     - **interface:** pinned by spec §2 or §9;
     - **idiomatic or forced by context:** it follows from Solona's surrounding names, the Dalamud API's shape or a C# idiom, or it matches Solona-authored text inside a derived line. Each gets a reason and is not rewritten;
     - **expressive:** anything else.
   - **Token similarity** per file: the Jaccard index over 5-grams.
   - **Record:**
     - the first measurement, taken before any rewrite, and every later one with its step;
     - expressive hits go to B as clean-side `file:line` references, until there are 0;
     - every file at 0.40 similarity or above gets a reason.

8. **The clean diffs.** These are the only diffs any clean agent may view:
   - before removal:

     ```
     git -C W diff test...<R0>^ -- . ':!Proteus/Interop/IpcProvider.cs' ':!Proteus/Interop/PenumbraBridge.cs' ':!Proteus/Interop/GlamourerBridge.cs' ':!Proteus/Plugin.cs' ':!Proteus/Services/SidecarDiscoveryService.cs' ':!Proteus/Gui/StatusWindow.cs' ':!proteus.slnx' ':!samples' <extra §1 files>
     ```

   - after removal:

     ```
     git -C W diff <R> HEAD
     ```

   - on a re-merge, for the critic only: `git -C W diff <P9> HEAD`, or the lighter `git -C W diff --stat <P9> HEAD`;
   - for the re-reviewer, the D15 commands.
   - **Never:**
     - an unexcluded `git diff test HEAD`;
     - any show or diff of `<R0>…<R>`;
     - `git log -p` or content blame of the mixed set before `<R>`;
     - a combined diff of a merge commit.
   - **After a merge**, `test`'s changes in these diffs are clean: they are either bump-only or A3-cleaned.
   - `<R0>`, `<R>`, `P8`, `T8`, `P9`, `T9` and `<critic-pass-commit>` are all logged.

9. **Forbidden for every clean participant:**
   - every revision of the mixed set before `<R>`, including mixed paths on `test`;
   - the contributors' commits, PRs and forks, and web searches for their code;
   - the sources under `Luna/`, `Glamourer/` and `Penumbra.Api/` (LICENSE and README files are allowed);
   - the contents of `bin/` and `obj/`;
   - other checkouts, and the files of `E:\repos\Proteus` (D16);
   - the memory topic files, until the push;
   - A's and D's folders and transcripts;
   - `scratchpad-handover\` (scripts only);
   - pre-`<R>` `.out`/`.trx` files, except through the D5 scripts.

10. **Git hygiene.**
    - Stage with `git add -u` plus explicit paths. Never use `-A`, `add -p` or `commit -v`.
    - Before every commit, `git status --short` must list only the expected paths:
      - `<R0>…<R>`: the mixed set and the deleted whole files;
      - the removal-report commit: the log only;
      - the slnx commit: `proteus.slnx` only;
      - B's commits: the mixed set, `IpcProvider.cs`, `Proteus/Localization/*`, the log, and the new files B named;
      - merges: what `git diff --name-only <branch>...test` lists;
      - any other commit: the files its step names.
    - Any other path stops the commit.
    - Summaries use only `--stat`, `--numstat`, `--name-only` or `--format`.
    - Build-number tokens come only from `git grep -h -o -E "BuildNumber = [0-9]+" <rev> -- Proteus/Plugin.cs`.

11. **The count-only scripts.** They live in the session scratchpad, are written with Write, and are never committed. Content is processed in memory and never printed.
    - **`blame-authors.ps1 <repo> <rev> <mode>`** prints `<path>\t<email>\t<count>` for every non-Solona email, plus totals.
      - `-w -M -C`: at step 0, after later fixes, and at the final tip.
      - Both modes: at `<R>`, at step 6, after every merge, and at step 10.
    - **`restored-check.ps1 <commits…>`**:
      - validates the manifest;
      - verifies each restored line word for word, and that blame gives it to Solona at `<source>^`;
      - on non-merge ranges, sorts every added line into marker (C# only), restored, blank or other. In non-C# files, any line that isn't a verified restore counts as other;
      - on merge commits, sorts only the lines that appear in neither parent into marker, restored, blank or bump (`^\s*public const int BuildNumber = \d+;\s*$`);
      - prints the counts. Unmatched and other must both be 0, and bump must be 0 on non-merge ranges.
    - **`guard-classify.ps1 <branch> <test> <mixed paths>`** prints `<sha> bump-only` or `<sha> other` per counted commit, judged from `git diff -U0 c^1 c -- <mixed paths>`.
    - **`bump-resolve.ps1 <W>`**:
      - accepts exactly one conflict hunk, in `Proteus/Plugin.cs`, made only of BuildNumber lines;
      - writes the line with the larger of the two numbers and stages it;
      - prints `conflict hunks: 1, all bump-only`. Otherwise it prints `refused: …` and exits non-zero, which leads to `--abort` and A3.
    - **`trx-summary.ps1`**, **`build-summary.ps1`** and **the extraction script.**

12. **Allowlists and extraction.**
    - **B** may run only:
      - `dotnet build W/Proteus/Proteus.csproj -c Release`;
      - `dotnet test W/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "…"`, optionally with `--logger "console;verbosity=normal"`.
    - **Code-critic** may run only:
      - the D8 pre-removal and post-removal diffs;
      - `git -C W diff <P9> HEAD` or `git -C W diff --stat <P9> HEAD`;
      - `git -C W log --format=%h%x20%an%x20%s test..HEAD`;
      - targeted `dotnet test`.
    - **Security reviewer** may run the same commands, minus the re-merge diffs.
    - **QA** may run only `curl` to `127.0.0.1` or `localhost`.
    - **Re-reviewer (J-2)** may run only:

      ```
      git -C W show --stat HEAD
      git -C W show HEAD -- docs/contrib-clean-room-log.md
      git -C W show HEAD -- Proteus/Plugin.cs
      git -C W log --format=%h%x20%s <critic-pass-commit>..HEAD
      git -C W show <QA-bump commit> -- Proteus/Plugin.cs
      git -C W diff --stat <critic-pass-commit> HEAD
      ```

    - **Everyone:** reads use Read, and searches use Grep or Glob, with absolute paths. Content Greps keep line numbers. No sub-agents.
    - **Extraction for clean agents.** By agent ID, the script prints:
      - `Opened:`;
      - `Shell commands:` (verbatim);
      - `Sub-agents:`;
      - "Grep without line numbers: N";
      - "Shell commands outside the allowlist: N";
      - "forbidden locations in listing/search results: N".
    - **Forbidden locations:**
      - `glamourer/`, `penumbra.api/` and `luna/` at the repo root;
      - any `bin` or `obj` path segment;
      - `samples/examplemultigroupoverlaymod`;
      - `e:/repos/proteus/`;
      - other checkouts;
      - `/memory/`;
      - `scratchpad-agent`;
      - `scratchpad-handover`.
    - **The orchestrator's self-checks** run over its own session transcript, up to the final commit, and are logged with their exception lists.
      - **Self-check 1 (the whole session).** It counts every Read of a mixed-set repo-relative path, in any checkout, before `<R>`'s commit time.
        - The allowed exception is the Read of `E:\orca\workspaces\Proteus\meddle\Proteus\Plugin.cs` lines 31–34, for the BuildNumber bump in the PbdFile run. Blame gives those lines 0 to the contributors.
        - It is logged as "self-check 1 (whole session): 0 beyond disclosed exceptions".
      - **Self-check 2 (from the window start).** The window starts at the timestamp of the `git -C E:/orca/workspaces/Proteus/meddle worktree add -b solona-m/licence-clean-room E:/orca/workspaces/Proteus/licence-clean-room test` tool call, logged as an ISO date and time.
        - **Counted locations:**
          - the forbidden locations above, `scratchpad-handover`, `scratchpad-agentA` and `scratchpad-agentD`;
          - mixed-set paths in their pre-`<R>` state.
        - **What counts (J-1):**
          - Read paths;
          - result lines of content-mode Grep;
          - shell commands that read file content (`cat`, `type`, `Get-Content`, `head`, `tail`, `grep`, `rg`, `Select-String`, `git show`, `git diff`, `git blame`, `git log -p`, `cmp`) whose arguments name a counted location.
        - **Sanctioned class** (logged, never counted):
          - name-only listings: Grep in `count` and `files_with_matches` modes, Glob, `ls` and `git ls-files`;
          - `Remove-Item` and `Test-Path` on `bin/` and `obj/`;
          - `Copy-Item` to the dev-load folder;
          - the `Proteus.dll` timestamp check;
          - the D16 git commands in `E:\repos\Proteus`;
          - the D11 count-only scripts.
        - **Named exceptions inside the window:**
          - (a) `git -C E:/orca/workspaces/Proteus/meddle worktree add -b solona-m/licence-clean-room E:/orca/workspaces/Proteus/licence-clean-room test`;
          - (b) `git -C E:/orca/workspaces/Proteus/licence-clean-room blame -w -M -C --line-porcelain -L 31,34 test -- Proteus/Plugin.cs | grep -c "^author-mail <git@caraxian.com>"`, which printed 0;
          - (c) the orchestrator's greps over its own session transcript jsonl for its earlier Read and Edit calls on the mixed files. They printed tool-call inputs only, with no mixed-file content;
          - (d) `head -n 3 Luna/LICENSE Glamourer/Glamourer.Api/LICENSE Penumbra.Api/LICENSE Luna/ImSharp/LICENSE`;
          - (e) `head -n 3 Glamourer/LICENSE`.
        - **In-window Grep-tool and Glob runs**, logged from the orchestrator's tool-call inputs:
          - (i) `ProjectReference|PackageReference`, content mode, path `E:/orca/workspaces/Proteus/licence-clean-room`, glob `{Proteus,XivLiveMesh,Proteus.Refit}/*.csproj`. Result lines came only from `Proteus.Refit/Proteus.Refit.csproj` and `Proteus/Proteus.csproj`, both allowed. Not counted.
          - (ii) `using Luna|Luna\.`, count mode, path `E:/orca/workspaces/Proteus/licence-clean-room/Proteus`, glob `*.cs`. It named `GlamourerBridge.cs:1` and `PenumbraBridge.cs:1`. Name-only, so sanctioned.
          - (iii) `SPDX-License-Identifier`, count mode, path `W`, glob `!{Glamourer,Luna,Penumbra.Api}/**`. It named only `docs/pbd-clean-room-plan.md`, `docs/pbd-clean-room.md`, the 7 `XivLiveMesh/*.cs` files and `Proteus.Tests/PbdCleanRoomRecordTests.cs`. Sanctioned.
          - (iv) `AGPL|Affero|LICENSE-DRAFT|Proteus Licence`, `files_with_matches`, path `W`, with the same exclude glob. It returned 18 of Proteus's own files, and no submodule or mixed file. Sanctioned.
          - One Glob, `C:/Users/solon/.claude/projects/*/5936bcc1-21b0-4a70-8eb3-e82d4006af47.jsonl` (the session transcript). Sanctioned.
        - No content-mode Grep returned a line in a counted location, so no exception (f) is needed; the log states this.
        - **Outside the window (disclosed, not counted):** the meddle Grep runs (including the Glamourer changelog hit), and the meddle shortlog, log and blame-count commands.
        - It is logged as "self-check 2 (from <ISO time>): 0 beyond named exceptions".
    - **Start commits.** Clean agents start after `<R>`, and each one's start commit is logged.
    - **Dirty agents** self-report. Their transcripts are never read.

13. **`IpcProvider.cs` is rewritten whole.** B rewrites its 13 non-contributor lines from the spec. The header is pinned in the Contract.

14. **The bot commits** are recorded, not replaced.

15. **The final commit, the bump and the short re-review.**
    - **The final bump** is `max(branch BuildNumber at the last merged tip, test's BuildNumber at the last merged T) + 1`, read with the D10 token rule. If `test`'s number is N or higher (N being the QA build's number), QA item 1 is re-run on the final build.
    - **`<critic-pass-commit>`** is the HEAD at which the code-critic last passed: at step 8, or at a step-9 re-merge check. It is logged.
      - Between that commit and the final commit, only the QA-bump commit, log-only commits and the final commit may land.
      - A commit that changes code means the critic re-reviews it, and `<critic-pass-commit>` moves forward.
    - **The final commit** holds:
      - the remaining log sections, including every merge's records, any re-merge critic check, the licence-files list and both self-checks;
      - the bump.
    - **The short re-review (J-2)** runs only the six D12 re-reviewer commands. It checks that:
      - every commit after `<critic-pass-commit>` is the QA bump, a log-only commit or the final commit;
      - `git diff --stat <critic-pass-commit> HEAD` lists only `docs/contrib-clean-room-log.md` and `Proteus/Plugin.cs`;
      - the QA bump's and the final commit's `Plugin.cs` changes are BuildNumber lines only.

      Its reads go into the merge message.

16. **`E:\repos\Proteus` takes commands only.** The allowed commands are:
    - `git pull` and `git rev-parse`;
    - the guard and `guard-classify.ps1`;
    - `git merge`;
    - `blame-authors.ps1`;
    - `dotnet test` (the Records filter and the quick suite);
    - `git push origin test`.

    Never open a file there or view conflict content. Warn other sessions before each `dotnet test`.

17. **The guard and merging `test` in** (at the start of step 8, and in step 9 if `test` moved).
    - **The guard** is `git rev-list --count <branch>..<T> -- <mixed set paths>`. If it is above 0, `guard-classify.ps1` runs and its result is logged.
    - **Guard 0, or every counted commit bump-only:**
      - the orchestrator runs `git -C W merge --no-ff --no-edit <T>`;
      - a conflict goes to `bump-resolve.ps1`, then the orchestrator commits. A refusal means `--abort` and A3;
      - `restored-check.ps1 <merge>` must show only bump and blank lines.
    - **Any "other":**
      - A3 runs `git -C W merge --no-commit <T>` and applies D2;
      - the orchestrator commits;
      - `restored-check.ps1 <merge>` must show 0 unmatched and 0 other;
      - D re-runs if A3 touched B's lines.
    - **After every merge:**
      - the proof on the merged tip (D18);
      - blame in both modes, logged as "merged with test", which must be 0/0.
    - **If a merged tip fails to build or test:**
      - B fixes it (its SHA is logged for D);
      - the critic re-reviews;
      - D re-runs;
      - if QA has already run, QA item 1 is re-run on a re-copied build;
      - then the run resumes.

18. **"Fails the same way."** This applies at step 6 and on every merged tip, to tests present in step 2's TRX (matched by fully qualified name):
    - The skip set must be the same, and every baseline's normalised hash must equal step 2's.
    - On a hash mismatch, A compares the two TRX files and answers only "same failure, differs only in <kind>" or "different". "Different" is a proof failure.
    - A step-2 test that newly fails or skips is a proof failure.
    - Tests that arrived with `test` must pass, or skip with a logged reason.

19. **The clean security review and the §12 confirmations.**
    - A clean sub-agent applies the `/security-review` method to the D8 pre-removal and post-removal diffs, at the step-8 merged tip.
    - The critic and the security reviewer each confirm every spec §12 row. Both tables are logged.
    - Findings are handled per D20, with a critic re-review afterwards.
    - A step-9 re-merge that isn't bump-only also gets a security re-check by the critic on `git -C W diff <P9> HEAD`.

20. **Who fixes what.**
    - B's production code, and any failing merged tip, is fixed by B (via SendMessage) or by a fresh clean implementer with B's allowlist and its own log section. Each fix SHA is logged for D.
    - The orchestrator fixes only tests, docs and the `BuildNumber` line.

21. **The filters.** Targeted runs only; the quick suite runs once, before the push.
    - **WIDE-A:**

      ```
      FullyQualifiedName~IpcProviderSpecTests|FullyQualifiedName~ContribSpecTests|FullyQualifiedName~SidecarDiscovery|FullyQualifiedName~SidecarCache|FullyQualifiedName~StutterFixesPureTests|FullyQualifiedName~DesignBinding|FullyQualifiedName~PlayerSettingsFilterTests|FullyQualifiedName~HarnessBridgeWrapperTests|FullyQualifiedName~HarnessBindingsWiringTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~IdleDiskIoWiringTests
      ```

      plus §10's classes, with `&Speed!=Slow`.
    - **Records:**

      ```
      FullyQualifiedName~ContribCleanRoomRecordTests|FullyQualifiedName~ReadmeTermsTests|FullyQualifiedName~PbdCleanRoomRecordTests
      ```

22. **The licence stays unchanged.** `LICENSE`, `docs/LICENSE-DRAFT.md`, `THIRD_PARTY_NOTICES.txt`, the `XivLiveMesh/*` licence and notice files, and the csproj licence fields are not touched.

23. **The slnx is regenerated** in its own commit, after the removal-report commit:
    1. `dotnet new sln --name proteus --format slnx`;
    2. `dotnet sln proteus.slnx add <csproj>`, one call per project.

    It must exist before any test runs after `<R>`.

## Requirements
**User prompt (verbatim):** "publish the new licence"

**User's first choice (verbatim):** "Rewrite their code first (Recommended) — Clean-room rewrite of Caraxi's and AzureRavie's surviving code, then swap LICENSE, update SPDX headers and notices, and push to test."

**User's decision on ND-1 (verbatim):** "Contributor rewrite only — Do the Caraxi/AzureRavie clean-room now and stop; decide about Luna later. Licence stays AGPL for now."

**Feature arguments (verbatim):**
> Clean-room rewrite of the remaining third-party-authored code so Proteus can move from AGPL-3.0 to the Proteus Licence (docs/LICENSE-DRAFT.md), then publish the licence (swap LICENSE, update SPDX headers, NOTICE/THIRD_PARTY_NOTICES, README licence mentions; push to test). Code to replace, found by `git blame -w -M -C` on test: Caraxi (git@caraxian.com) — Proteus/Interop/IpcProvider.cs 137 lines (public IPC: overlay tables assigned to mod options; names/signatures must stay identical), Proteus/Interop/PenumbraBridge.cs 9, Proteus/Plugin.cs 6 (IPC registration; `/proteus refresh silent`), Proteus/Services/SidecarDiscoveryService.cs 6 (multi-select group default + per-option tables), Proteus/Gui/StatusWindow.cs 4 (clickable mod name opening Penumbra; priority DragInt fix), proteus.slnx 9 (regenerate), samples/ExampleMultiGroupOverlayMod/ (remake or delete); AzureRavie — Proteus/Interop/GlamourerBridge.cs 12 lines (cooldowns on equip attempt, Glamourer lock-conflict logging). Follow the same two-stage clean-room method as docs/pbd-clean-room.md / docs/pbd-clean-room-log.md (spec agent reads the old code; a fresh implementer never does; record every participant's reads). The orchestrating session has not read any of this code and must stay clean. Precedent: the PbdFile.cs rewrite (build 1466).

**Derived requirements:**
- R-1: The public IPC stays identical: names, argument and return types, behaviour, and the thread it answers on.
- R-2: The small fixes keep their behaviour, or spec §11 drops one with a reason.
- R-3: A dirty agent cleans the mixed files before the implementer sees them. The removal must be provable, and Solona's own lines are restored or their gaps rewritten.
- R-4: No licence change (the user's decision).
- R-5: Commits are authored as Solona, with no Co-Authored-By and no real name. Every sub-agent is `opus`. Push to `test` only; `main`, tags and deploys are hard stops.
- R-6: Targeted `--filter` runs only, and the quick suite runs once, before the push.
- R-7: AGENTS.md Rules 3, 4 and 5 apply.

## Contract
No routes, schema or persisted fields change.
- **Public IPC:** every label, kind, argument type, return type and behaviour in spec §2–3 (the `test` tip), pinned by `IpcProviderSpecTests`.
- **Seams:** the names and signatures in spec §9, including the bridge method that holds the equip cooldown and the lock-conflict logging. B's new seams and files are logged with their names and signatures, and their tests cite §9.
- **The header of `Proteus/Interop/IpcProvider.cs`:**
  - line 1 is exactly `// SPDX-License-Identifier: AGPL-3.0-or-later`;
  - line 2 is `// Copyright (c) Solona`;
  - `docs/contrib-clean-room-spec.md` is named within the first 5 lines;
  - there is no "ported from".
- **Unchanged:** `LICENSE`, `docs/LICENSE-DRAFT.md`, and the three guard tests.

## Approach
Every step ends with a commit made under D10.

0. **Hand-off.**
   - Commit this plan as `docs/contrib-clean-room-plan.md`, with a "Log inputs from the planning session" preface.
   - Start keep-awake (PowerShell, `timeout: 7200000`).
   - Run `git submodule update --init --recursive`.
   - Run `blame-authors.ps1 W test "-w -M -C"`.
   - Log the BuildNumber line form and the window start.

1. **A, stage 1: the spec.** A writes `docs/contrib-clean-room-spec.md` with these 12 sections:
   - `## 1. What is replaced`
   - `## 2. Public IPC surface` (`| Label | Kind | Arguments | Returns |`)
   - `## 3. IPC behaviour`
   - `## 4. The refresh command`
   - `## 5. Overlay discovery: multi-select groups and per-option tables`
   - `## 6. Status window: mod name and priority`
   - `## 7. Opening a mod in Penumbra`
   - `## 8. Glamourer: equip cooldown and lock conflicts`
   - `## 9. Seams the tests need` (each §3 row says whether it can be reached without a new seam; plus the bridge method behind §8)
   - `## 10. Existing tests that exercise these lines`
   - `## 11. Dropped or changed behaviour`
   - `## 12. Security-relevant behaviour` (`| Row | Area | Behaviour |`)

   The spec follows D6. A runs its identifier self-check (0 hits) and writes its self-report. The orchestrator checks D10, the fences, the markers and the D3 tripwire, then commits.

2. **Tests and the old-code gate.**
   - The orchestrator writes:
     - `IpcProviderSpecTests`;
     - `ContribSpecTests`;
     - the EDGE-5 replacements;
     - the `ContribCleanRoomRecordTests` skeleton.
   - Gate:

     ```
     dotnet test W/Proteus.Tests/Proteus.Tests.csproj -c Release --logger "trx;LogFileName=<scratchpad>\step2.trx" --filter "(<WIDE-A>)&CleanRoom!=NewSeam&CleanRoom!=NewBehaviour&FullyQualifiedName!~ContribCleanRoomRecordTests" > <scratchpad>\step2.out 2>&1
     ```

   - If the build fails, run `build-summary.ps1`; CS1061 and CS1729 go to A. Otherwise run `trx-summary.ps1`; failures go to A.
   - Log everything (D5), then commit.

3. **A, stage 2: removal** (via SendMessage).
   - A removes the §1 set using only D2's operations, writes the manifest, and drafts its self-report.
   - The orchestrator checks D10, runs `git add -u`, and commits `<R0>` without viewing it. Then it runs `restored-check.ps1`, blame in both modes, the guard and the classifier. A check that fails means A writes a fix-up and the orchestrator commits it.
   - Once the checks pass, A appends its self-report to the log and deletes its folder. The orchestrator commits the log alone and deletes the manifest without reading it.

4. **slnx, then "not passing".**
   - Regenerate the slnx (D23) and commit.
   - Delete `bin/` and `obj/`, then confirm with `Test-Path`.
   - Run the step-2 filter and record "not passing". Log and commit.

5. **B.** Fresh and clean, briefed with the spec, R-1, R-2, D6, D9, D12 and the Contract. B removes the markers, repairs the syntax, and names its new files. The orchestrator extracts B's record, logs B's SHA, seams and files, and commits.

6. **Proof.**
   - The step-2 filter without the exclusions, judged by D18.
   - Blame in both modes: 0/0.
   - No hole markers left.
   - Log and commit.

7. **D** (D7).
   - The first measurement is logged before any rewrite.
   - Hits go to B as fix commits (their SHAs logged). Step 6's checks then repeat, and D measures again.
   - Log and commit.

8. **Merge `test`, then the gate.**
   1. **Merge.**
      1. In `E:\repos\Proteus`: `git pull` on `test`, then `rev-parse`, giving `T8`.
      2. In `W`: record `P8`, then run the guard and the classifier against `T8`.
      3. Merge `T8` (D17), then run `restored-check.ps1 <merge>`.
      4. On the merged tip: the D18 proof, and blame in both modes ("merged with test", 0/0).
      5. If A3 touched B's lines, D re-runs. A failing tip goes per D17 to B.
      6. Log and commit.
   2. **Code-critic** on the merged tip, with D8, D12 and the §12 table, and the full suite deferred. Fixes follow D20, with re-review until it passes. Log `<critic-pass-commit>`.
   3. **Security reviewer** (D19). Any code fix is followed by a critic re-review, which moves `<critic-pass-commit>` forward.
   4. **D re-run** with step 6's checks, if B's commits changed after step 7.
   5. **QA build.** Bump to N, the next number above `max(branch, T8)`, and commit "Build N for QA" (`Plugin.cs` only), logging its SHA as the QA-bump commit. Build Release, warn other sessions, copy, and check the timestamp.
   6. **Adversarial QA.**
      1. `/health` shows build N, and `/log` shows no IPC registration errors.
      2. QA reads the method §9 names, confirms that `POST /actions/equip` (→ `SetGearItem`) reaches it, and checks two equips inside the cooldown against §8. If the route doesn't reach it, the item is UNVERIFIED with that reason. If no route can create a Glamourer lock, the lock half is UNVERIFIED.
      3. `GET /sidecar` on a real multi-select overlay mod, compared with §5. If no such mod is installed: UNVERIFIED.
      4. UNVERIFIED by design: the mod-name click, `/proteus refresh silent`, and another plugin calling the IPC.
   7. **QA fixes** follow D20, or become `known-issue` tasks. Code fixes need a critic re-review (which moves `<critic-pass-commit>`), a D re-run and a re-copy.

9. **Re-merge if needed, then the final commit.**
   1. **Check `test` again.** `git pull` and `rev-parse`, giving `T9`. If `T9` differs from `T8`:
      1. record `P9`, run the guard and the classifier, merge `T9` (D17), and run `restored-check.ps1 <merge>`;
      2. run the D18 proof and blame in both modes (0/0, logged);
      3. the code-critic re-reviews `git -C W diff <P9> HEAD`, or takes the lighter check for a bump-only merge with no conflict. Either way, `<critic-pass-commit>` moves to the merge commit;
      4. a re-merge that isn't bump-only also gets the security re-check;
      5. on a failure, D17's failing-tip procedure runs, including QA item 1 on a re-copied build.
   2. **Licence files.** `git -C W diff test...HEAD --name-only` is logged together with the line "licence files untouched".
   3. **Self-checks.** Run both (D12) and log them.
   4. **Final bump** (D15). Re-run QA item 1 if the last T's number is N or higher.
   5. **Final commit:** the log sections, plus the bump.
   6. **Short re-review** (D15 and the D12 re-reviewer commands).
   7. **Final blame.** `blame-authors.ps1 "-w -M -C"` on the final tip, for the merge message.
   8. **Copy** the final build.

10. **Merge into `test`** (D16).
    1. `git pull`. If `test` has moved since the last merged T, go back to step 9.
    2. Run the guard: it must be 0.
    3. `git merge --no-ff solona-m/licence-clean-room`. The message is the PR description. It contains:
       - "clean-room in two stages";
       - the spec and log paths;
       - "licence unchanged";
       - the blame figures;
       - the re-review's reads;
       - Review & traceability;
       - no Co-Authored-By.
    4. Warn other sessions, then run the Records filter.
    5. Re-check blame in both modes, for the final report. If it isn't 0/0 because of a new contributor commit outside the mixed set: stop and re-plan. Otherwise go back to step 9 with A3. Don't push in either case.
    6. Run the quick suite.
    7. Check that only `codeql.yml` triggers.
    8. `git push origin test`.

11. **Final report:**
    - the decisions made;
    - gaps in the proof;
    - D's first and last numbers;
    - the step-10 re-check;
    - UNVERIFIED items and `known-issue` tasks;
    - owner follow-ups:
      - Luna and Penumbra.Api: a later Phase B/C, or Ottermandias's exception;
      - XivLiveMesh's licence;
      - the licence wording;
      - a README licence line;
      - the workflows' origins;
      - the NuGet notices;
      - the lawyer's review;
      - `main` and the release.

## Files
- NEW `docs/contrib-clean-room-plan.md`: this plan.
- NEW `docs/contrib-clean-room-spec.md`: A's spec.
- NEW `docs/contrib-clean-room-log.md`. Its opening lines say no licence change is made, and that the method extends `docs/pbd-clean-room.md` to mixed files. It holds:
  - the Method, with `norm`, the BuildNumber pattern and the sanctioned class;
  - every section, with commands verbatim;
  - all the SHAs: `<R0>`, `<R>`, `P8`, `T8`, `P9`, `T9`, `<critic-pass-commit>`, the QA-bump commit, B's commits, and the start commits;
  - blame figures, restore checks, guard classes and the bump-resolve output;
  - the closure rounds and D's measurements;
  - runs, skips, hashes and new-test reasons;
  - NewSeam counts;
  - B's seams and files;
  - the §12 tables;
  - the re-merge check;
  - the licence-files list;
  - both self-checks, including the in-window Grep and Glob facts and "no exception (f) needed".
- DELETE, then NEW: `Proteus/Interop/IpcProvider.cs` (deleted in `<R0>`, rewritten by B).
- EDIT `Proteus/Interop/PenumbraBridge.cs`, `Proteus/Interop/GlamourerBridge.cs`, `Proteus/Plugin.cs`, `Proteus/Services/SidecarDiscoveryService.cs` and `Proteus/Gui/StatusWindow.cs`: A's removal, then B's code. `Plugin.cs` also gets the bumps.
- EDIT any extra §1 file: A's removal, then B's code.
- DELETE, then NEW: `proteus.slnx` (D23).
- DELETE `samples/ExampleMultiGroupOverlayMod/**` (in `<R0>`).
- EDIT `Proteus/Localization/Strings.cs` and its `*.json` files (B), only if new UI text is added; all 8 locales.
- NEW any `Proteus/**` file B names.
- NEW `Proteus.Tests/IpcProviderSpecTests.cs`, `Proteus.Tests/ContribSpecTests.cs` and `Proteus.Tests/ContribCleanRoomRecordTests.cs`.
- EDIT the EDGE-5 tests named in §10.
- MERGE commits in `W`: at step 8, and at step 9 if `test` moved.
- Unchanged guards: `LICENSE`, `docs/LICENSE-DRAFT.md`, `THIRD_PARTY_NOTICES.txt`, `XivLiveMesh/*`, the csproj files, `ReadmeTermsTests`, `PbdCleanRoomRecordTests`, `SupplyChainTests`, `HarnessBindingsWiringTests` and `For Creators.md`.
- Not committed: the scripts, the `.trx` and `.out` files, the manifests, and the A, D and handover folders.

## Out of Scope
- Any licence change, Phase B and Phase C (owner follow-ups).
- The NuGet notices, the workflows' origins, XivLiveMesh's licence, the licence wording, a README line (owner follow-ups).
- `main`, tags, releases and deploys (hard stops).
- Asking for consent.
- New harness routes.
- Editing the `docs/pbd-*` records.
- Re-checking where other files came from.
- Remaking the sample.

## Edge Cases
- EDGE-contrib-clean-room-1: A contributor line edited later, at any depth. The closure removes it.
- EDGE-contrib-clean-room-2: A contribution that replaced Solona's lines. They are restored word for word and verified, or a hole is left.
- EDGE-contrib-clean-room-3: Lines moved unchanged into another file. Whole-tree blame in both modes catches them.
- EDGE-contrib-clean-room-4: A coincidental attribution, or a similarity hit. A3 or D classes it, and only expressive hits go to B.
- EDGE-contrib-clean-room-5: An existing test asserts removed text. It is replaced with a behavioural test before `<R>`.
- EDGE-contrib-clean-room-6: `test` advances. The guard and the classifier run. Bump-only commits are merged by the orchestrator; anything else goes through A3. The merge happens at step 8, a re-merge at step 9 gets the critic check, and any later move sends the run back to step 9.
- EDGE-contrib-clean-room-7: IPC called with an unknown name, a different case, before ready, or after dispose. Handled as §3 says.
- EDGE-contrib-clean-room-8: IPC called from a non-framework thread. Handled as §3 says.
- EDGE-contrib-clean-room-9: Variants of the refresh arguments. Handled as §4 says.
- EDGE-contrib-clean-room-10: Multi-select defaults, or options without tables. Handled as §5 says.
- EDGE-contrib-clean-room-11: A Glamourer lock, or repeated equips inside the cooldown. Handled as §8 says.
- EDGE-contrib-clean-room-12: Penumbra missing when a mod name is clicked. Handled as §7 says, with no throw.
- EDGE-contrib-clean-room-13: A clean agent breaks a rule. Its work is void, redone and logged.
- EDGE-contrib-clean-room-14: The bot commits. Recorded, and listed separately.
- EDGE-contrib-clean-room-15: The harness is untouched. `HarnessBindingsWiringTests` stays green.
- EDGE-contrib-clean-room-16: The slnx is gone after `<R0>`. No test runs before step 4.
- EDGE-contrib-clean-room-17: A step-2 test newly fails or skips. That is a proof failure.
- EDGE-contrib-clean-room-18: The closure removes Solona's own later lines. The spec covers them and B rebuilds them.
- EDGE-contrib-clean-room-19: Broken syntax after removal. Expected; B repairs it.
- EDGE-contrib-clean-room-20: A range or merge adds a disallowed line. The dirty agent writes a fix-up, and the orchestrator commits it, seeing counts only.
- EDGE-contrib-clean-room-21: An unexpected path in `git status`. The commit stops.
- EDGE-contrib-clean-room-22: The tripwire fires. Re-plan.
- EDGE-contrib-clean-room-23: Old-code output could reach the orchestrator. D5 prevents it.
- EDGE-contrib-clean-room-24: Log or UI text in removed lines. Described, reworded, localised and asserted structurally.
- EDGE-contrib-clean-room-25: NewSeam tests are more than a third of `IpcProviderSpecTests`. AC-1 is reported as weaker.
- EDGE-contrib-clean-room-26: The step-10 re-check isn't 0/0. If a new contributor commit sits outside the mixed set: stop and re-plan. Otherwise go back to step 9 with A3. Never push in either case.
- EDGE-contrib-clean-room-27: No route to create a lock, no multi-select mod installed, or the equip route doesn't reach the method. UNVERIFIED, with the reason.
- EDGE-contrib-clean-room-28: For Creators naming the deleted sample. It doesn't; a test pins that.
- EDGE-contrib-clean-room-29: Merge commits in history. Each line inherits status from its parent; resolution lines get the hunk rule.
- EDGE-contrib-clean-room-30: Content moved with edits, including `Strings.cs`. It counts as carried over.
- EDGE-contrib-clean-room-31: A gated failure, CS1061 or CS1729 on the old code. The content goes to A only.
- EDGE-contrib-clean-room-32: A breach.
  - Self-check 1: a pre-`<R>` Read of a mixed file in any checkout, beyond its one exception.
  - Self-check 2: in-window content access (Read paths, content-mode Grep lines, content-reading shell commands) to a counted location, beyond exceptions (a)–(e). Name-only listings and sanctioned commands never count.
  - The leak check: the same for clean agents.
  - Any breach is reported.
- EDGE-contrib-clean-room-33: A baseline's message differs only in volatile parts. `norm` handles it; otherwise A gives a kind-only answer.
- EDGE-contrib-clean-room-34: `test`'s changes to the mixed set are bump-only. The orchestrator merges them with the resolver; a refusal goes to A3.
- EDGE-contrib-clean-room-35: Tests that arrive with `test`. They must pass, or skip with a logged reason.
- EDGE-contrib-clean-room-36: The QA build number is taken on `test`. The final bump goes above both sides, and QA item 1 is re-run.
- EDGE-contrib-clean-room-37: A merged tip fails to build or test. B fixes it (SHA logged), the critic re-reviews, D re-runs, QA item 1 re-runs if QA has already run, and the run resumes.
- EDGE-contrib-clean-room-38: A commit lands after the critic's pass. If it is no-code (the QA bump, log-only, or the final commit), the short re-review covers it through the log, show and `--stat` commands. If it is a code commit, the critic re-reviews and `<critic-pass-commit>` moves forward. A `--stat` listing any other file fails the re-review.

## Test Strategy
All new tests carry `[Trait("Plan","<ID>")]`. NewSeam tests also carry `[Trait("Seam","§9.<row>")]`. Record tests only read files.

- [AC-contrib-clean-room-1] [EDGE-contrib-clean-room-7] [EDGE-contrib-clean-room-8] [EDGE-contrib-clean-room-25] `IpcProviderSpecTests`:
  - `Registers_exactly_the_documented_labels_and_signatures`
  - `Each_label_behaves_as_the_spec_says`
  - `Answers_on_the_thread_the_spec_says`
  - `Dispose_unregisters_every_label`

  They run in the gate, at step 6 and on every merged tip. The NewSeam share is logged.
- [AC-contrib-clean-room-2] [EDGE-contrib-clean-room-9] [EDGE-contrib-clean-room-10] [EDGE-contrib-clean-room-11] [EDGE-contrib-clean-room-12] [EDGE-contrib-clean-room-18] [EDGE-contrib-clean-room-24] `ContribSpecTests`:
  - `Refresh_arguments_follow_the_spec`
  - `Multi_select_group_defaults_follow_the_spec`
  - `Per_option_tables_follow_the_spec`
  - `Priority_edit_follows_the_spec`
  - `Opening_a_mod_in_Penumbra_follows_the_spec`
  - `Glamourer_cooldown_starts_at_the_attempt`
  - `Glamourer_lock_conflict_is_logged_once_with_the_mod`

  Also the §10 tests, `LocalizationTests` and QA items 1–3.
- [AC-contrib-clean-room-3] [EDGE-contrib-clean-room-1] [EDGE-contrib-clean-room-2] [EDGE-contrib-clean-room-3] [EDGE-contrib-clean-room-14] [EDGE-contrib-clean-room-20] [EDGE-contrib-clean-room-29] [EDGE-contrib-clean-room-30] [EDGE-contrib-clean-room-34]
  - `ContribCleanRoomRecordTests.Log_reports_no_contributor_lines` checks the log for:
    - 0/0 in both modes at `<R>`, at step 6, at "merged with test (step 8)", and at step 9 if there was a re-merge;
    - 0 after any later fix;
    - the closure rounds and categories;
    - both kinds of restore check with 0 unmatched and 0 other;
    - the guard classes and any resolver output;
    - the dependabot entry.
  - `ContribCleanRoomRecordTests.Sample_gone_and_slnx_present`.
- [AC-contrib-clean-room-4] [EDGE-contrib-clean-room-4] `ContribCleanRoomRecordTests.Similarity_check_is_recorded_honestly`: the B commit SHAs, the first measurement, the later ones, the categories with reasons, a final count of 0 expressive lines, and a reason for every file at 0.40 or above.
- [AC-contrib-clean-room-5] [EDGE-contrib-clean-room-13] [EDGE-contrib-clean-room-21] [EDGE-contrib-clean-room-23] [EDGE-contrib-clean-room-32] `ContribCleanRoomRecordTests`:
  - `Spec_has_every_required_section_and_no_code`.
  - `Log_lists_every_participant_and_the_clean_side_kept_clear`:
    - These sections exist, each with `Opened:`: Planner, Plan-critic 1–6, Planner (rev 2)–(rev 7), Planning orchestrator, Agent A (self-report), Orchestrator, Agent B, Agent D (self-report), Code-critic, Security review and Adversarial QA. The same holds for any other `Plan-critic <n>`, `Planner (rev <n>)` or `Agent A2/A3 (self-report)` section.
    - Every agent stayed within its allowlist.
    - `Sub-agents: none`, and the three counts are 0.
    - Dirty agents report "Identifier-grep hits: 0".
    - Start commits are logged.
    - Both §12 tables are present.
    - The Orchestrator section contains:
      - "self-check 1 (whole session): 0 beyond disclosed exceptions", with exactly the meddle `Plugin.cs` lines 31–34 Read as its exception;
      - "self-check 2 (from <ISO date-time>)", with a parseable timestamp and "0 beyond named exceptions", and exactly exceptions (a)–(e);
      - the sanctioned-class list, which names Grep `count`/`files_with_matches` and Glob as name-only;
      - the four in-window Grep facts (i)–(iv) and the one Glob;
      - the statement "no exception (f) needed";
      - the statement that the pre-window meddle commands are outside the window.
    - The opening lines contain "no licence change" and `docs/pbd-clean-room.md`.
  - `IpcProvider_header_is_pinned`.
  - `No_hole_markers_remain`.
- [AC-contrib-clean-room-6] [EDGE-contrib-clean-room-16] [EDGE-contrib-clean-room-17] [EDGE-contrib-clean-room-19] [EDGE-contrib-clean-room-31] [EDGE-contrib-clean-room-33] [EDGE-contrib-clean-room-35] [EDGE-contrib-clean-room-37]
  - The runs at step 2, step 4, step 6 and every merged tip.
  - `ContribCleanRoomRecordTests.Log_compares_baselines_by_hash`:
    - `norm` is logged;
    - every step-2 baseline is equal or "differs only in <kind>", with none "different";
    - each merge lists the tests it brought in, with their outcomes and reasons;
    - any EDGE-37 fix is listed with its SHA, critic re-review and D re-run.
  - The orchestrator's `--numstat` check on test files is logged.
- [AC-contrib-clean-room-7] The guard tests stay unchanged and green. `ContribCleanRoomRecordTests.Log_states_licence_files_untouched`.
- [AC-contrib-clean-room-8] [EDGE-contrib-clean-room-6] [EDGE-contrib-clean-room-26] [EDGE-contrib-clean-room-36] [EDGE-contrib-clean-room-38]
  - The step-10 sequence, recorded in the final report.
  - `ContribCleanRoomRecordTests.Log_records_merges_and_the_final_bump` checks the log for:
    - `P8` and `T8`, and either `P9`/`T9` or "no re-merge";
    - the re-merge critic check;
    - `<critic-pass-commit>` and the QA-bump commit's SHA;
    - the branch number, the last T's number, and final = max + 1;
    - "QA item 1 re-run", where that applied;
    - the statement that the short re-review ran the six D12 re-reviewer commands, with its reads in the merge message.
- [EDGE-contrib-clean-room-5] Each replaced test is logged and passes.
- [EDGE-contrib-clean-room-15] `HarnessBindingsWiringTests`.
- [EDGE-contrib-clean-room-22] The tripwire result is logged.
- [EDGE-contrib-clean-room-27] QA lists its UNVERIFIED items with reasons.
- [EDGE-contrib-clean-room-28] `ContribCleanRoomRecordTests.For_Creators_names_only_existing_samples`.

## Environment & Preconditions
- `PROTEUS_GAME`, `PROTEUS_MODS` and `PROTEUS_MODPACKS` are set.
- For QA: the game is running with the harness on, and a multi-select overlay mod is installed (otherwise that item is UNVERIFIED).
- The submodules are initialised.
- The build is broken from `<R0>` until step 5; any failure that isn't B's falls under Rule 2.
- The dev-load folder is written only in steps 8 and 9. The test runs in step 10 rebuild it, so warn other sessions first.
- `-C -C` blame is slow; run it in the background.

### Planner (rev 1) reads, for the log
Clean side. No show, `log -p`, diff or content blame of any forbidden path, and no URLs.

**Loaded automatically:** the global CLAUDE.md; the start folder's CLAUDE.md and AGENTS.md; the memory index; the plan-draft skill.

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
- Commands 6 and 11 counted matches inside the mixed files.
- Commands 7–10 touched the submodules' licence and readme files.

### Planner (rev 2)–(rev 7) reads, for the log
- **Rev 2:** no Read calls; same auto-loads. Commands, verbatim:
  1. `grep -n -i -E "log|/state|/mods|sidecar|overlay" E:/orca/workspaces/Proteus/licence-clean-room/docs/agent-rules/adversarial-qa.md`
  2. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -l "proteus.slnx" -- Proteus.Tests`
- **Rev 3:** no Read calls; same auto-loads. Command, verbatim:
  1. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "SetGearItem" -- Proteus/TestHarness/GameHarnessAdapter.cs`
- **Rev 4, rev 5 and rev 6:** no tool calls; same auto-loads.
- **Rev 7:** no tool calls at all (no Read, no shell or git commands, no searches, no URLs); same auto-loads.

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

**Plan-critic 2 (on rev 2).** Opened only the rev-2 plan output file, whole. Commands, verbatim:
1. `grep -n -i -E "/sidecar|/log|actions/equip|cooldown|lock" E:/orca/workspaces/Proteus/licence-clean-room/docs/agent-rules/adversarial-qa.md`
2. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "Equip|glamourer\.|GlamourerBridge|Cooldown" -- Proteus/TestHarness/GameHarnessAdapter.cs`
3. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -c -E "Process|\"git\"|git " -- Proteus.Tests/PbdCleanRoomRecordTests.cs Proteus.Tests/ReadmeTermsTests.cs`
4. `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "Process|\"git\"|git " -- Proteus.Tests/PbdCleanRoomRecordTests.cs`

**Plan-critic 3 (on rev 3).** Opened only the rev-3 plan output file (lines 1–470, then 470–770). No shell commands; nothing forbidden, no URLs, no sub-agents.

**Plan-critic 4 (on rev 4).** Opened only the rev-4 plan output file, in three reads (1–480, 480–649, 649–728). No shell commands; nothing forbidden, no URLs, no sub-agents.

**Plan-critic 5 (on rev 5).** Opened only the rev-5 plan output file, in four reads (1–420, 420–719, 720–919, then to the end). No shell commands; nothing forbidden, no URLs, no sub-agents.

**Plan-critic 6 (on rev 6).** Opened only the rev-6 plan output file, whole, in four reads (1–120, 120–449, 450–779, 780–979). One shell command, verbatim:
1. `grep -n -E "^## |^[0-9]+\. \*\*|^   [0-9]+\. |^8\.|^9\.|^10\." "C:/Users/solon/.claude/projects/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/toolu_01WHd8VAiL2zvRrzNpc9U1fr.txt"`

Nothing forbidden, no URLs, no sub-agents.

### Planning orchestrator reads, for the log
Copied verbatim into the log from the orchestrator's reports.

**The exception to self-check 1.** In the earlier PbdFile run in this session, the orchestrator opened `E:\orca\workspaces\Proteus\meddle\Proteus\Plugin.cs` lines 31–34 only (Read offset 31, limit 4, to bump BuildNumber) and edited the BuildNumber line. `git blame -w -M -C --line-porcelain -L 31,34 test -- Proteus/Plugin.cs | grep -c "^author-mail <git@caraxian.com>"` printed 0.

**Before the self-check-2 window** (confirmed from tool-call order; disclosed, not counted):
- `git -C E:/orca/workspaces/Proteus/meddle shortlog -sne test`
- `git -C E:/orca/workspaces/Proteus/meddle log test --author=Caraxi --format="%h %ad %s" --date=short --numstat`
- `git -C E:/orca/workspaces/Proteus/meddle log test --author=AzureRavie --format="%h %ad %s" --date=short --numstat`
- the blame-count pipelines `git -C E:/orca/workspaces/Proteus/meddle blame -w -M -C --line-porcelain test -- <file> | grep -c "^author Caraxi"`, run for `IpcProvider.cs`, `Plugin.cs`, `StatusWindow.cs`, `PenumbraBridge.cs`, `SidecarDiscoveryService.cs` and `proteus.slnx`, and `... | grep -c "^author AzureRavie"`, run for `GlamourerBridge.cs` and `CompositorService.cs`
- `git -C E:/orca/workspaces/Proteus/meddle ls-tree -r --name-only test -- samples proteus.slnx`
- `git -C E:/orca/workspaces/Proteus/meddle show test:LICENSE --format= -s`
- the meddle Grep-tool runs:
  - `Caraxi` (files_with_matches, excluding the submodules, `bin` and `obj`): none;
  - `Caraxi|AzureRavie` (content): one hit, `Glamourer/Glamourer/Gui/GlamourerChangelog.cs:249`, a changelog line, no code.

**Inside the window**, verbatim, each with its status:
- (a) `git -C E:/orca/workspaces/Proteus/meddle worktree add -b solona-m/licence-clean-room E:/orca/workspaces/Proteus/licence-clean-room test`. This is the window start. Named exception.
- `git -C E:/orca/workspaces/Proteus/licence-clean-room submodule update --init --recursive`. Not a content read.
- `wc -l docs/pbd-clean-room.md docs/pbd-clean-room-plan.md docs/LICENSE-DRAFT.md docs/pbd-clean-room-log.md THIRD_PARTY_NOTICES.txt XivLiveMesh/NOTICE.md`. Allowed files.
- `git -C E:/orca/workspaces/Proteus/licence-clean-room ls-files --stage Glamourer Luna Penumbra.Api .gitmodules`. Name-only, sanctioned.
- (d) `head -n 3 Luna/LICENSE Glamourer/Glamourer.Api/LICENSE Penumbra.Api/LICENSE Luna/ImSharp/LICENSE`. Named exception.
- `ls Penumbra.Api Glamourer/Glamourer.Api Glamourer`. Name-only, sanctioned.
- (e) `head -n 3 Glamourer/LICENSE`. Named exception.
- (b) `git -C E:/orca/workspaces/Proteus/licence-clean-room blame -w -M -C --line-porcelain -L 31,34 test -- Proteus/Plugin.cs | grep -c "^author-mail <git@caraxian.com>"`. Printed 0. Named exception.
- (c) The greps over the orchestrator's own session transcript jsonl for its earlier Read and Edit calls on the mixed files. They printed tool-call inputs: old Edit `old_string`/`new_string` text of `docs/pbd-clean-room-plan.md` (Solona's own doc), and one Read input, `{"file_path":"E:\\orca\\workspaces\\Proteus\\meddle\\Proteus\\Plugin.cs","offset":31,"limit":4}`. No mixed-file content. Named exception.
- Grep-tool runs (J-1):
  - (i) `ProjectReference|PackageReference`, content mode, path `E:/orca/workspaces/Proteus/licence-clean-room`, glob `{Proteus,XivLiveMesh,Proteus.Refit}/*.csproj`. Result lines only in `Proteus.Refit/Proteus.Refit.csproj` and `Proteus/Proteus.csproj`, both allowed.
  - (ii) `using Luna|Luna\.`, count mode, path `E:/orca/workspaces/Proteus/licence-clean-room/Proteus`, glob `*.cs`. Named `GlamourerBridge.cs:1` and `PenumbraBridge.cs:1`. Name-only, sanctioned.
  - (iii) `SPDX-License-Identifier`, count mode, path `W`, glob `!{Glamourer,Luna,Penumbra.Api}/**`. Named only `docs/pbd-clean-room-plan.md`, `docs/pbd-clean-room.md`, the 7 `XivLiveMesh/*.cs` files and `Proteus.Tests/PbdCleanRoomRecordTests.cs`.
  - (iv) `AGPL|Affero|LICENSE-DRAFT|Proteus Licence`, files_with_matches, path `W`, same exclude glob. Returned 18 of Proteus's own files: `docs/pbd-*`, `docs/LICENSE-DRAFT.md`, the XivLiveMesh files, `Proteus/Proteus.csproj`, `Proteus.Tests/ReadmeTermsTests.cs`, `Proteus.Tests/PbdCleanRoomRecordTests.cs` and `LICENSE`. No submodule or mixed file.
- One Glob: `C:/Users/solon/.claude/projects/*/5936bcc1-21b0-4a70-8eb3-e82d4006af47.jsonl` (the session transcript). Name-only, sanctioned.
- No content-mode Grep returned a line in a counted location, so no exception (f) is needed.

**Files read:** `docs/pbd-clean-room.md`, `docs/LICENSE-DRAFT.md`, `THIRD_PARTY_NOTICES.txt`, the rev-1 to rev-6 plan outputs, and the plan-critic reports.

## NEEDS_DECISION
None. ND-1 was decided by the user, and ND-3 and the self-check option by the orchestrator. The earlier ND-2, ND-4, ND-5 and ND-6 are owner follow-ups.

## Risks
- **Leaks to the clean side.** Possible routes:
  - pre-`<R>` diffs and merge diffs;
  - the security review's diff against `origin/HEAD`;
  - old-code test and build output;
  - assertions that hold removed text;
  - conflict content during merges;
  - identifiers in Solona's remaining call sites (accepted as interface names, and logged);
  - marker locations (disclosed);
  - the handover folder;
  - relative paths.

  Mitigations: D5, D6, D8–D12, D16, D17, D19, EDGE-5, EDGE-13 and EDGE-32.
- **Blame proves deletion, not independence.** D7's record covers independence.
- **The closure and carried-over heuristics may over-remove.** The tripwire bounds this, and the spec covers the removed behaviour.
- **AC-1 on the old code may rest on NewSeam tests.** The NewSeam share is measured and reported.
- **The Interop Sensitive Area.** Changes stay within the removed regions. The §12 rows are confirmed twice on the merged tip, `HarnessBindingsWiringTests` guards the harness wiring, and QA covers what the routes reach.
- **Merging `test` mid-branch.**
  - The step-8 merge comes before all review and QA.
  - A step-9 re-merge gets its own critic check.
  - The short re-review covers every commit after the critic's last pass.
  - The guard, the classifier, the resolver and A3 keep contributor lines out.
  - EDGE-37 covers a broken merged tip.
- **Process departures.** B writes the production code, and a clean sub-agent runs the security review. Both are noted in Review & traceability.
- **Plugin reloads.** Warn other sessions first.
- **Left to the owner:**
  - the UNVERIFIED in-game items;
  - Luna and Penumbra.Api before any relicensing;
  - the NuGet notices;
  - the lawyer's review;
  - `main` and the release.

Relevant paths:
- E:\orca\workspaces\Proteus\licence-clean-room\docs\pbd-clean-room-plan.md
- E:\orca\workspaces\Proteus\licence-clean-room\docs\pbd-clean-room-log.md
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\PbdCleanRoomRecordTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\ReadmeTermsTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\HarnessBindingsWiringTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus\TestHarness\GameHarnessAdapter.cs
- E:\orca\workspaces\Proteus\licence-clean-room\docs\agent-rules\adversarial-qa.md
- E:\orca\workspaces\Proteus\licence-clean-room\For Creators.md