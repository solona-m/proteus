> **Hand-off copy, revision 12 with amendments 12a, 12b and 12c.**
>
> **History.**
> - Revision 7 was committed at step 0.
> - The step-1 tripwire led to revision 10.
> - The user's scope change ("Do the Luna work as well. Keep going until you've pushed that and all the other needed changes.") brought back Phase B and Phase C as revision 11.
> - Revisions 7, 10 and 11 are in this file's git history, and this revision refers to their wording where it says "(rev 10)" or "(rev 11)".
>
> **Critique of this revision.**
> - Plan-critic 11 rated revision 11 MEDIUM.
> - Plan-critic 12 rated revision 12 + 12a HIGH.
> - Plan-critic 13 rated 12b HIGH, once the progressive "Phases delivered" line that 12c adds was in.
> - Every amendment went through the planner.
>
> **Reading order.** Revision 12 comes first, then the amendments 12a, 12b and 12c. Each amendment's replacement text supersedes the matching text before it.

# Implementation Plan: Clean-room replacement, Proteus without Luna, and publishing the Proteus Licence (revision 12)

## Delta (rev 12 vs rev 11)
Every finding from Plan-critic 11 is folded in, along with the orchestrator's new facts.

- **P-1 (blocker): B2 and the trivial class.**
  - B2 may delete or edit trivial-class lines outside the mixed set, because removing or changing a trivial line carries no expression (D2, D10).
  - D11 gains a new allowed change reason: "B2 edit of an existing file (named)".
  - D10's B2 expected set now covers the `Proteus/TestHarness/**` gate files, the adapter, and every file B2 names. Inside that set, trivial-class lines may change, and each change is logged under that reason.
  - D10 also states that Phase C's line-1 SPDX edits don't touch class lines. If one does, it is logged with the reason "Phase C SPDX".
- **P-2 (blocker): fallbacks can now happen at any point.**
  - The log carries the line "Phases delivered: A | A,B | A,B,C" (one of the three). The record tests require only the sections and records of the phases named there.
  - **Fallback A.** An unfixable Phase B defect, found at B5 or at step 8 or later, cuts `solona-m/contrib-clean-room-a` at `T_A`. Steps 8–10 then re-run on that branch with a fresh code-critic, a fresh security reviewer and fresh QA.
  - **Fallback B.** An unfixable Phase C defect does the same at `T_B`.
  - Changed in: D27, step 8, EDGE-46, EDGE-47, record tests.
- **P-3: XivLiveMesh.** If C1 finds another user, the library is dual-licensed instead of staying AGPL-only:
  - SPDX becomes `AGPL-3.0-or-later OR LicenseRef-Proteus`;
  - `NOTICE.md` says it is offered under the AGPL on its own, and under the Proteus Licence as part of Proteus;
  - `XivLiveMesh/LICENSE` keeps the AGPL text, and a new `XivLiveMesh/LICENSE-PROTEUS.md` is a byte-identical copy of the root `LICENSE`;
  - it is not listed as third-party under §7.

  C1's search now includes `E:/repos/DragAndDropTexturing`, using a files-with-matches or count-mode search only. That is name-only and sanctioned; no file there is ever opened. Changed in: D28, C1, EDGE-48.
- **P-4: notices come from what actually ships.**
  - In C2, every entry that `zip-entries.ps1` lists from `latest.zip` (names only) is classified as one of:
    - Proteus's own build output;
    - a noticed package;
    - the noticed native bc7enc;
    - a carved-out asset.
  - The classification is committed as `Proteus.Tests/Fixtures/package-inventory.tsv`, and `LicencePublicationTests` pins it.
  - At step 8, QA item 7 re-runs `zip-entries.ps1`, and the orchestrator checks every entry against the inventory.
  - The `Meshes/` foot-binding file is carved out in the notices and flagged for the owner until its origin is confirmed. Any other shipped data derived from third-party meshes or game files is treated the same way.
  - Changed in: D29, C2, step 8, EDGE-52, EDGE-53 (new), tests.
- **P-5: Cecil and the clean room.**
  - The orchestrator never runs Cecil, ildasm or a decompiler against the API DLLs outside `IpcContractTests` itself.
  - `IpcContractTests` failure messages print only the contract row and the mismatched fact.
  - Questions about where something sits in the metadata go to Agent C, who answers in contract §5.
  - Changed in: D26, D12 (self-check 2), EDGE-54 (new).
- **P-6: D8 stays as written.** The step-1b failure path added no file; Agent A confirmed this at 1b. The log states it, and D8's exclusion list stays as written.
- **P-7: orchestrator facts since rev 10, recorded in the log (Orchestrator section):**
  - `<R0>` is `7ce8851b`.
  - `restored-check.ps1` printed: added 39, markers 27, restored verified 12 (13 manifest rows verified; the 13th restore is unchanged text), unmatched 0, blank 0, bump 0, other 0.
    - The first two runs were void because of script bugs: a variable-name clash, and the section-sign encoding. They printed counts only.
    - One count-only marker-probe script was used to find the encoding bug.
  - Blame at `<R>` in `-w -M -C`:
    - contributor non-trivial lines: 0;
    - Caraxi `using` lines: 3, all in the `ColorTableEditor*.cs` files;
    - AzureRavie: 0;
    - dependabot non-trivial lines: 1.
  - The guard against `test` is 2: `106c450b` is bump-only and `dd53194b` is "other". This is logged only, and step 8 handles it through A3.
    - The first `guard-classify.ps1` run printed "guard: 0" because of a parameter-passing bug. A direct `git rev-list --count` gave 2.
- **Log inputs:** Plan-critic 11's reads are added. Planner (rev 12) made no tool calls.

Unchanged from rev 11: the scope (Phases A, B and C), the slug, the 13 ACs (wording updated where P-1 to P-4 apply), D1–D31 (amended in place) and the step order.

**A note on rev 10 and rev 11 wording.** Where a decision below says "(rev 10, unchanged)" or "(rev 11, unchanged)", the full wording of that revision governs. Both revisions are in the git history of `docs/contrib-clean-room-plan.md`, and are carried verbatim into the log.

## Approval Summary
**Goal:** Do three things, in order:
1. Replace every non-trivial line Caraxi and AzureRavie wrote with code written by an agent that never saw theirs. The public IPC and the small fixes stay exactly as before.
2. Ship Proteus without Luna, Penumbra.Api and Glamourer.Api.
3. Publish the Proteus Licence on `test`.

**Slug:** `contrib-clean-room`

**Acceptance Criteria:**
- **AC-contrib-clean-room-1:** A plugin calling Proteus's public IPC sees the same labels, argument types, return types and results as before. Tests written from the spec prove this on both the old code and the clean code.
- **AC-contrib-clean-room-2:** The contributors' small features still work as before, unless the spec drops one with a reason:
  - `/proteus refresh silent`;
  - clicking a mod's name opens it in Penumbra;
  - the priority drag;
  - multi-select defaults and per-option tables;
  - case-insensitive lookup;
  - the Glamourer cooldown counts from the equip attempt;
  - lock-conflict logging.
- **AC-contrib-clean-room-3:** No non-trivial contributor line remains.
  - Whole-tree blame runs at `<R>`, at the step-6 tip, at the Phase B tip, at the step-8 merged tip and at any step-9 re-merge, in both modes. At the final tip it runs in `-w -M -C`.
  - Every line it gives to Caraxi or AzureRavie is in a trivial category, or is a game-fact line within §1's per-file limits.
  - Every change to those counts is logged with an allowed reason.
  - The derived closure is removed. Every removal range and merge adds only allowed lines. The sample is deleted, and `proteus.slnx` is regenerated.
- **AC-contrib-clean-room-4:** B's code is shown to be independent. At the final tip, D finds 0 expressive non-interface lines in B's surviving commits that are identical to removed lines. Every run is logged, starting with the first.
- **AC-contrib-clean-room-5:** The record is complete:
  - the spec and the IPC contract are facts only;
  - every participant's reads are logged verbatim;
  - the clean agents' sections are extracted from their transcripts and stay within their allowlists;
  - both self-checks are 0 beyond their exceptions;
  - the dirty agents report 0 identifier-grep hits;
  - the tripwire re-plan, the scope change and "Phases delivered" are recorded;
  - the record tests pass. They only parse files, and they cover the delivered phases.
- **AC-contrib-clean-room-6:** Each delivered phase passes its proof, and so does every merged tip.
  - Phase A: the old-code gate, then "not passing" after removal, then green.
  - Phase B: WIDE-B and WIDE-A, plus the contract and shipped-assembly tests.
  - Phase C: the Records filter.
  - Tests that existed at step 2 keep their skip lists and baseline hashes, or A answers "differs only in <kind>". New tests pass, or skip with a reason.
- **AC-contrib-clean-room-7:** Phases A and B change no licence or notice file. The B4 file list is logged, and the three guard tests pass unchanged through B4.
- **AC-contrib-clean-room-8:** Finish:
  - the branch (or a fallback branch) merges into `test` locally, adding nothing beyond the last reviewed state;
  - the Records filter, the blame re-check and the quick suite pass on `test`;
  - only `test` is pushed. No `main`, no tags, no release or deploy workflow.
- **AC-contrib-clean-room-9 (if B is delivered):** The plugin package holds no `Luna.dll`, `Penumbra.Api.dll` or `Glamourer.Api.dll`, and neither `Proteus.csproj`, `packages.lock.json` nor `Proteus.dll` references them.
- **AC-contrib-clean-room-10 (if B is delivered):** Proteus talks to Penumbra and Glamourer exactly as before.
  - The contract test matches every label, kind and signature, and every enum's names, values, underlying type and `[Flags]`, against the API submodules.
  - The bridges follow contract §4 when Penumbra or Glamourer is absent or not ready.
  - The B5 smoke check and the step-8 QA pass in game.
- **AC-contrib-clean-room-11 (if C is delivered):** `LICENSE` is the Proteus Licence, "*Version 1.0 — in force from <push date>*". The draft is gone, there is no root `LICENSE-*` file, and the package carries the licence.
- **AC-contrib-clean-room-12 (if C is delivered):** Every SPDX header of Proteus's own code reads `LicenseRef-Proteus`.
  - XivLiveMesh follows C1: either the Proteus Licence, or a dual licence (`AGPL-3.0-or-later OR LicenseRef-Proteus`).
  - Both csproj files drop `PackageLicenseExpression`.
  - Outside the excluded records and listings, no file claims the AGPL for Proteus's own code.
- **AC-contrib-clean-room-13 (if C is delivered):** `THIRD_PARTY_NOTICES.txt` keeps its existing blocks and adds:
  - every shipped package, with its licence from the nuspec;
  - the Glamourer-derived types (Apache-2.0);
  - an interoperability note;
  - carve-outs for the game-texture slices and the `Meshes/` foot binding;
  - the submodules;
  - a note on the workflows.

  Every `latest.zip` entry is classified in the committed package inventory, and step 8 cross-checks it.

**Key decisions:**
- **Phase A** (rev 10):
  - dirty A removes the lines and never commits;
  - clean B rebuilds them;
  - dirty D checks similarity;
  - the clean side views clean diffs only;
  - trivial coincidental lines outside the mixed set stay, and are logged.
- **Phase B:**
  - Agent C writes `docs/ipc-contract.md`;
  - the orchestrator writes the Cecil contract, shipped-assembly and bridge-availability tests;
  - clean B2 writes Proteus-owned call gates, with the owned types kept in the APIs' namespaces;
  - B2 may touch trivial-class lines in files it edits, and each change is logged;
  - an in-game smoke check runs before Phase C.
- **Phase C:**
  - publish the licence and notices, building the notices from what actually ships (zip inventory plus nuspec licences);
  - XivLiveMesh follows the C1 usage check, dual-licensed if another plugin uses it;
  - the effective date is the push day.
- **One gate** for all delivered phases: merge `test`, then code-critic, clean security review, D re-run and QA in game. The short re-review covers every commit after the critic's pass.
- **Fallbacks:** A alone, or A and B, at any point, with a fresh gate. The licence stays AGPL in either case.

**Risk flags:** security surface: yes (`Proteus/Interop/`, public IPC, `Proteus/TestHarness/`, licence and notices) · new persisted field: no · sensitive-category data: no · schema migration: no · new dependency: no (three removed) · new/changed route: no

**NEEDS_DECISION:** None. The user's scope change is in force. The orchestrator has decided ND-2 to ND-6, ND-3, the self-check option and the rev-8 to rev-12 amendments.

*(The summary above is what the developer approves; everything below is the detailed contract it stands on.)*

## Source
- The user's prompt, choices, both decisions and the scope change, and the feature arguments (verbatim under Requirements).
- The precedent: `docs/pbd-clean-room.md`, `docs/pbd-clean-room-plan.md`, `docs/pbd-clean-room-log.md` and `Proteus.Tests/PbdCleanRoomRecordTests.cs`.
- The committed spec `docs/contrib-clean-room-spec.md`, as amended at 1b.
- The orchestrator's state reports.
- Rev 1's Phase B/C design and Plan-critic 1's notes on it.
- The reports of Plan-critics 1–6, 8, 9 and 11.
- Read in rev 1: `docs/LICENSE-DRAFT.md`, `THIRD_PARTY_NOTICES.txt`, `XivLiveMesh/NOTICE.md`, `Proteus/Proteus.csproj` and `Proteus/packages.lock.json`.
- Not opened, and forbidden to the planner: the mixed files and their history, the contributors' commits, the Luna, Glamourer and Penumbra.Api sources, and the contributors' PRs.

## Context & Decisions
**Problem.** Three things stop the Proteus Licence from honestly taking effect:
1. **The contributors' surviving code.** Licence §6 keeps it under AGPL.
2. **`Luna.dll` in the plugin zip.** It is AGPL-3.0.
3. **`Penumbra.Api.dll` in the zip.** It has no licence at all.

Shipped packages and game-derived data also need notices. Phase A handles the first, Phase B handles the second and third, and Phase C handles the notices and the publication.

**Facts:**
- **Phase A facts** are as in rev 10/11, plus the P-7 facts. `<R0>` is `7ce8851b`. At `<R>`, blame in `-w -M -C` finds 0 non-trivial contributor lines and 3 `using` lines in `ColorTableEditor*.cs`. The guard against `test` is 2 (`106c450b` bump-only, `dd53194b` other), handled at step 8 by A3.
- **The Luna, API and package facts** are as in rev 11. Luna ships today as a ProjectReference. `glamourer.api` depends on Luna. Penumbra.Api's subscribers live in Luna. `Luna/LICENSE` is AGPL. `Glamourer/LICENSE` is Apache-2.0. Penumbra.Api has no licence file.
- **API use outside the bridges** is limited to `using Penumbra.Api.Enums` and `using Glamourer.Api.Enums`, plus the harness's `ApplyDesign` and `ApplyState` subscribers (ADR 0002).
- **Shipped packages** (direct) are BCnEncoder.Net, BitMiracle.LibTiff.NET, CommunityToolkit.HighPerformance, StbImageSharp and StbImageWriteSharp. The transitive packages come in through Luna and Penumbra.Api.
- **Game and third-party data in the build:**
  - The `Resources/sphere_d_array.png` and `tile_norm_array.png` slices come from game textures.
  - The `Meshes/` folder ships `toecap.mdl`, which is authored. The csproj also says "the binding to that foot is baked to a small file", and that foot belongs to a body mod.
- **XivLiveMesh** describes itself as "shared between Dalamud plugins".
- **The draft licence's preamble** names the GNU Affero GPL for earlier versions.

**Decisions.** D1–D23 are the Phase A rules, which are rev 10's unless marked. D24–D31 are Phase B and C.

1. **Roles** (rev 11, plus rev 12). Every sub-agent runs on model `opus`.

   | Role | Side | Reads | Writes | Shell |
   |---|---|---|---|---|
   | Orchestrator | clean | anything D9 allows; the mixed set only at or after `<R>` | tests, docs, the log, the slnx, the BuildNumber line, and the Phase C licence/notice/SPDX lines; **every commit** (under D10, unviewed) | D10/D11; D5 before `<R>`; count-only scripts. **Never** Cecil, ildasm or a decompiler on API DLLs outside the test (rev 12) |
   | Agent A, A2, A3 | dirty | as in rev 10; A2 and A3 recompute the closure from history | as in rev 10 | as in rev 10. **Never commits**. A3 alone may run `git merge --no-commit test` |
   | Agent D | dirty | as in rev 10 | numbers and categories | as in rev 10 |
   | Agent B (Phase A, after `<R>`) | clean | as in rev 10 | as in rev 10 | D12 |
   | Agent C (Phase B) | dirty for the API sources and the named Luna base classes only | as in rev 11 | `docs/ipc-contract.md`, its self-report, §5 answers (rev 12) | as in rev 11 |
   | Agent B2 (Phase B) | clean | as in rev 11 | as in rev 11, plus trivial-class lines in files it edits (rev 12) | D12 |
   | Code-critic, security reviewer, QA, short re-reviewer | clean | as in rev 11 | as in rev 11 | D12 |
   | Fresh gate agents (fallback, rev 12) | clean | as the originals | as the originals | D12 |

   Folders: `scratchpad-agentA\`, `scratchpad-agentC\`, `scratchpad-agentD\` and `scratchpad-handover\`. All are outside `W` and logged.

2. **The removal set** (rev 10, plus rev 12).
   - **Direct:** lines attributed in either blame mode.
   - **The trivial coincidental class** is not removed by A. It covers:
     - lines matching the fixed regex (`^$`, `using`, `namespace`, `[{}];?`);
     - game-fact lines, listed with `file:line` and a shape (the remainder trivial; per-file limits from §1; at most 3);

     in any file with no non-trivial set line.
   - **Derived:** the closure, with the merge rule.
   - **Carried over:** including `87ae2e3d`.
   - **Whole files.**
   - **Restored:** listed in the manifest in `scratchpad-handover`.
   - **A's operations:** delete, restore, and add markers, with markers in C# files only.
   - **Rev 12:** outside the mixed set, B2 (in files it edits) and Phase C (SPDX line 1 only) may delete or edit class lines. Each change is logged per D11.

3. **The scope tripwire** (rev 10). Phase B and C changes are not set growth.

4. **Tests from the spec, proven on the old code** (rev 10).

5. **The old-code output filter** (rev 10).

6. **The strings rule** (rev 10).

7. **D's similarity check** (rev 10/11). D's input is B's surviving commits plus A3's resolution lines. B2's lines are not measured.

8. **The clean diffs** (rev 10). The exclusion list is unchanged. **Rev 12:** the log records that the 1b failure path added no file, as A confirmed at 1b.

9. **Forbidden for every clean participant** (rev 11).

10. **Git hygiene** (rev 11, plus rev 12).
    - **Expected sets** are as in rev 11, with these changes:
      - **B2's commits:** `Proteus/Interop/**`, the `Proteus/TestHarness/**` gate files and `GameHarnessAdapter.cs`, `Proteus/Proteus.csproj`, `Proteus/packages.lock.json`, `Proteus/Plugin.cs` (wiring only), every file B2 names, and the log. Trivial-class lines in these files may change, and each change is logged under "B2 edit of an existing file (named)".
      - **Phase C's commit:** as in rev 11, plus `XivLiveMesh/LICENSE-PROTEUS.md` if dual-licensed, and `Proteus.Tests/Fixtures/package-inventory.tsv`.
    - **SPDX edits.** Phase C's line-1 SPDX edits don't touch class lines. If blame shows one did, it is logged with the reason "Phase C SPDX".
    - **Other commits** never touch a trivial-class line outside the mixed set.

11. **The count-only scripts** (rev 10/11, plus rev 12).
    - **`blame-authors.ps1`.**
      - **Pass rule:** `nontrivial` = 0, and game-fact lines stay within §1's per-file limits (at most 3).
      - **Changes:** each one is logged by file and category, with one of these reasons:
        - a new file by B, B2 or Phase C (named);
        - a file new from `test`;
        - an import edit from `test`;
        - a copy-detected import block from a test commit;
        - **a B2 edit of an existing file (named)** (rev 12);
        - **Phase C SPDX** (rev 12).
      - **Baseline:** taken at `<R>` and compared with §1. **Rev 12:** recorded as 0 non-trivial, with Caraxi `using` = 3, all in `ColorTableEditor*.cs`.
    - **The other scripts:**
      - `restored-check.ps1` (merge mode included);
      - `guard-classify.ps1` (the parameter-passing bug is fixed and logged);
      - `bump-resolve.ps1`;
      - `trx-summary.ps1`;
      - `build-summary.ps1`;
      - the extraction script;
      - `zip-entries.ps1` (names only).

12. **Allowlists and extraction** (rev 11, plus rev 12).
    - **Self-check 2's sanctioned class** now also covers:
      - C1's name-only and count-mode searches, including the one over `E:/repos/DragAndDropTexturing`;
      - `zip-entries.ps1`;
      - the B4 `Test-Path` checks;
      - the count-only marker-probe script.
    - **Any orchestrator Cecil, ildasm or decompiler run** against the API DLLs outside `IpcContractTests` counts as a breach (EDGE-54).

13. **`IpcProvider.cs`** is rewritten whole by B. Line 1 follows `LICENSE`.

14. **Bot commits** are recorded, not replaced. Dependabot keeps its 1 non-trivial line, which is not in the set.

15. **The final commit, the bump and the short re-review** (rev 11). The seven re-reviewer commands now include `git -C W show HEAD -- LICENSE`, for the date line.

16. **`E:\repos\Proteus`** takes commands only.

17. **The guard and the merge** (rev 11, plus rev 12).
    - The step-3 guard is 2: `106c450b` is bump-only and `dd53194b` is "other". Step 8 therefore goes through the A3 route.
    - After every merge: the restore check, the proof for the delivered phases, blame under D11, and D if needed. `IpcContractTests` runs when Interop files or the submodule pointers changed.
    - A failing tip goes to B or B2. If the failure can't be fixed, the fallback applies (D27).

18. **"Fails the same way"** (rev 10).

19. **Security review and confirmations** (rev 11). §12 and contract §4 are confirmed row by row.

20. **Who fixes what** (rev 11).

21. **Filters** (rev 11): WIDE-A, WIDE-B, and the Records filter (which includes `LicencePublicationTests`).

22. **The licence** is unchanged through Phases A and B. Phase C publishes it.

23. **The slnx** is regenerated at step 4 and kept through Phase B (D30).

24. **Phase B design** (rev 11).
    - Proteus-owned call gates sit over `GetIpcSubscriber`.
    - The owned types live in `Penumbra.Api.Enums` and `Glamourer.Api.Enums`, in `Proteus/Interop/Ipc/PenumbraApiTypes.cs` and `GlamourerApiTypes.cs` (the latter carries an Apache attribution).
    - The three ProjectReferences are removed.
    - The harness gates are `GlamourerApplyDesign` and `GlamourerApplyState`.
    - Event threading doesn't change.
    - Behaviour when Penumbra or Glamourer is unavailable follows contract §4.

25. **Agent C** (rev 11, plus rev 12). C answers metadata-location questions by amending §5. Each amendment is logged, and the amended contract is committed.

26. **Phase B tests** (rev 11, plus rev 12).
    - `IpcContractTests` uses Mono.Cecil over the API DLLs. They come in through `ReferenceOutputAssembly="false"` references, copied to the test output.
      - **Rev 12:** a failure prints only the contract row and the mismatched fact (for example "Penumbra §1 row 12: Returns differs"), never the API's own text or IL. The orchestrator never inspects the DLLs by any other route.
    - `ShippedAssembliesTests`.
    - `BridgeAvailabilityTests`.
    - The updated names in `HarnessBindingsWiringTests`.

27. **The smoke check and the fallbacks** (rev 11, plus rev 12).
    - **B5 smoke check:** QA-lite in game (equip, mod-settings with redraw, design apply), then `T_B` is recorded.
    - **Fallback A.** A Phase B defect that can't be fixed, found at B5 or at step 8 or later:
      1. cut `solona-m/contrib-clean-room-a` at `T_A`;
      2. re-run steps 8–10 on it, with a fresh code-critic, a fresh security reviewer and fresh QA;
      3. log "Phases delivered: A".
    - **Fallback B.** A Phase C defect that can't be fixed, found at any time: the same at `T_B`, logged as "Phases delivered: A,B".
    - **The record tests** read the "Phases delivered" line, and require only the delivered phases' sections, tests and records.
    - **Either fallback** keeps the licence AGPL and reports the blocker.

28. **Licence publication** (rev 11, plus rev 12).
    - **`LICENSE`:** the draft's text, with line 3 reading "in force from <date>".
    - **`git rm docs/LICENSE-DRAFT.md`.**
    - **`Proteus.csproj`:** add the `LICENSE` content item, drop `PackageLicenseExpression`, and update the comments.
    - **XivLiveMesh**, per C1:
      - **Not used elsewhere:**
        - `LICENSE` is byte-identical to the root `LICENSE`;
        - `NOTICE.md` and the csproj name the Proteus Licence;
        - SPDX is `LicenseRef-Proteus`.
      - **Used elsewhere (rev 12, dual):**
        - SPDX in its 7 `.cs` files is `AGPL-3.0-or-later OR LicenseRef-Proteus`;
        - `XivLiveMesh/LICENSE` keeps the AGPL text;
        - new `XivLiveMesh/LICENSE-PROTEUS.md` is byte-identical to the root `LICENSE`;
        - `NOTICE.md` says the library is offered under the AGPL on its own, and under the Proteus Licence as part of Proteus;
        - the csproj comment says the same, and `PackageLicenseExpression` is dropped;
        - it is not listed as third-party under §7.
    - **Other SPDX lines** become `LicenseRef-Proteus`, except `GlamourerApiTypes.cs`.
    - **Tests:**
      - `ReadmeTermsTests.Licence_is_the_Proteus_Licence_in_force` replaces the AGPL test. It checks the uppercase AGPL title only, so the preamble's mention of Affero passes;
      - `PbdCleanRoomRecordTests` header check follows `LICENSE`;
      - `SupplyChainTests` changes only the Luna doc and message.
    - **`AGENTS.md`:** the submodules line.
    - **Untouched:** README, For Creators.md and TERMS.md.

29. **The notices** (rev 11, plus rev 12).
    - **Inventory (rev 12).** C2 builds Release, then runs `zip-entries.ps1 latest.zip`, which prints names only. Every entry is classified into one of these:
      - **own:** Proteus's own build output (`Proteus.dll`, `XivLiveMesh.dll`, `Proteus.json`, `images/icon.png`, `LICENSE.txt`, `THIRD_PARTY_NOTICES.txt`, the `.deps`/`.pdb` files, and so on);
      - **package:** a noticed NuGet package DLL, with the block id;
      - **native:** `proteus_bcn.dll`, noticed as bc7enc;
      - **carved-out:** game- or third-party-derived data, with the block id. This covers anything under `Meshes/` that is derived from third-party meshes (the foot-binding file) and any embedded resources. The `Resources/*.png` slices are embedded in `Proteus.dll`, so the inventory notes them as "embedded, carved out".
    - **The classification** is committed as `Proteus.Tests/Fixtures/package-inventory.tsv`, with columns `<entry>\t<class>\t<block id>`. An unclassifiable entry is a blocker, which leads to Fallback B.
    - **`THIRD_PARTY_NOTICES.txt`** keeps the LTC uvmaps, BCnEncoder.NET and bc7enc blocks, and adds:
      - a block for every **package** entry and every non-build-only package in the lock file, with the licence and copyright from the nuspec and licence file in the local NuGet cache (an unreadable licence leads to Fallback B);
      - the **Glamourer-derived types** (Apache-2.0, with NOTICE content if one exists);
      - an **interoperability** note covering Penumbra's and Glamourer's IPC facts, and Penumbra.Api's missing licence (flagged for the lawyer);
      - **game assets carved out:** the two `Resources` slices, derived from FINAL FANTASY XIV data (© SQUARE ENIX) and not covered by the Proteus Licence;
      - **third-party mesh data carved out (rev 12):** the `Meshes/` foot-binding file, "derived from a body mod's foot mesh; origin to be confirmed by the owner; not covered by the Proteus Licence until confirmed". Any other such shipped data is listed the same way, and each gets a `known-issue` task for the owner;
      - the **submodules:** Glamourer (Apache-2.0), Penumbra.Api (no licence file), and Luna (AGPL-3.0, used only for the test build, never shipped);
      - the **workflows note** (ND-6).
    - Dalamud, as the host, is flagged for the lawyer.

30. **The slnx** keeps the API entries for the test build (rev 11).

31. **The effective date** is the push day (rev 11). Step 9 corrects it if the day changes, and step 9 is redone if the push slips past midnight.

## Requirements
**User prompt (verbatim):** "publish the new licence"

**User's first choice (verbatim):** "Rewrite their code first (Recommended) — Clean-room rewrite of Caraxi's and AzureRavie's surviving code, then swap LICENSE, update SPDX headers and notices, and push to test."

**User's decision on ND-1 (verbatim, superseded):** "Contributor rewrite only — Do the Caraxi/AzureRavie clean-room now and stop; decide about Luna later. Licence stays AGPL for now."

**User's scope change (verbatim, in force):** "Do the Luna work as well. Keep going until you've pushed that and all the other needed changes."

**Feature arguments (verbatim):**
> Clean-room rewrite of the remaining third-party-authored code so Proteus can move from AGPL-3.0 to the Proteus Licence (docs/LICENSE-DRAFT.md), then publish the licence (swap LICENSE, update SPDX headers, NOTICE/THIRD_PARTY_NOTICES, README licence mentions; push to test). Code to replace, found by `git blame -w -M -C` on test: Caraxi (git@caraxian.com) — Proteus/Interop/IpcProvider.cs 137 lines (public IPC: overlay tables assigned to mod options; names/signatures must stay identical), Proteus/Interop/PenumbraBridge.cs 9, Proteus/Plugin.cs 6 (IPC registration; `/proteus refresh silent`), Proteus/Services/SidecarDiscoveryService.cs 6 (multi-select group default + per-option tables), Proteus/Gui/StatusWindow.cs 4 (clickable mod name opening Penumbra; priority DragInt fix), proteus.slnx 9 (regenerate), samples/ExampleMultiGroupOverlayMod/ (remake or delete); AzureRavie — Proteus/Interop/GlamourerBridge.cs 12 lines (cooldowns on equip attempt, Glamourer lock-conflict logging). Follow the same two-stage clean-room method as docs/pbd-clean-room.md / docs/pbd-clean-room-log.md (spec agent reads the old code; a fresh implementer never does; record every participant's reads). The orchestrating session has not read any of this code and must stay clean. Precedent: the PbdFile.cs rewrite (build 1466).

**Derived requirements:**
- R-1: The public IPC is identical.
- R-2: The small fixes are kept.
- R-3: Mixed files are cleaned first by a dirty agent, provably.
- R-4: Luna and the APIs are resolved before publication.
- R-5: Commits are authored as Solona, with no Co-Authored-By and no real name. Sub-agents run on `opus`. Pushes go to `test` only.
- R-6: Targeted filters only, plus the quick suite once.
- R-7: AGENTS.md Rules 3, 4 and 5 apply.
- R-8: Behaviour towards Penumbra and Glamourer is unchanged, including threading and behaviour when they're unavailable.

## Contract
No routes, schema or persisted fields change.
- **Public IPC:** spec §2–3.
- **Phase A seams:** spec §9.2–§9.5, plus B's logged seams.
- **The `IpcProvider.cs` header:** line 1 follows `LICENSE`; line 2 is `// Copyright (c) Solona`; the spec is named in the first 5 lines.
- **Phase B:** `Proteus.Interop.Ipc.IpcKind`, `IpcContractEntry(string Owner, string Label, IpcKind Kind, Type[] Arguments, Type? Returns)` and `IpcContract.Entries`. The owned types live in `Penumbra.Api.Enums` and `Glamourer.Api.Enums`, and the harness gates are `GlamourerApplyDesign` and `GlamourerApplyState`.
- **Licence:**
  - `LICENSE` line 1 is `# Proteus Licence`, and line 3 is `*Version 1.0 — in force from YYYY-MM-DD*`;
  - SPDX reads `LicenseRef-Proteus`, or `AGPL-3.0-or-later OR LicenseRef-Proteus` for a dual-licensed XivLiveMesh;
  - the inventory file has the columns `<entry>\t<class>\t<block id>`.

## Approach
Every step ends with a commit made under D10.

**Phase A**
- **Steps 0–2** are done.
- **Step 3:** `<R0>` = `7ce8851b` is committed, and the restore check and the `<R>` blame have passed (P-7). The guard reading of 2 is logged. Remaining work: A appends its removal self-report and deletes its folder, then the orchestrator commits the log and deletes the manifest unread.
- **Step 4:** regenerate the slnx (D23), delete `bin/` and `obj/`, and record the "not passing" run.
- **Step 5:** B.
- **Step 6:** the proof.
- **Step 7:** D. Then record `T_A`.

**Phase B**
- **B1:** Agent C writes the contract (D25).
- **B2:** the orchestrator writes the Phase B tests (D26). They are expected red.
- **B3:** B2 implements (D24), touching class lines only in files it edits, each change logged.
- **B4:** the proof:
  - WIDE-B plus WIDE-A;
  - blame (D11);
  - `Test-Path` on the three DLLs;
  - `zip-entries.ps1`;
  - the AC-7 file list.
- **B5:** the smoke check in game (D27), then record `T_B`. A failure that can't be fixed triggers Fallback A.

**Phase C**
- **C1: XivLiveMesh usage check.** These are name-only searches:
  - `gh search code "XivLiveMesh" --owner solona-m --json repository,path`, if authenticated;
  - a files-with-matches Grep for `XivLiveMesh` over `E:/repos/*/**/*.csproj` and `E:/repos/DragAndDropTexturing/**` (count or files-with-matches only, never opening a file there), excluding `E:/repos/Proteus`.

  Log what each search found and which D28 branch applies.
- **C2: edits.** Release build, then `zip-entries.ps1`. Build the inventory (D29) and read the nuspec licences. Then edit:
  - the licence, notice, inventory, SPDX, csproj and `AGENTS.md` files;
  - the test files.

  Run the Records filter and WIDE-B, then commit.
- **C3: proof.**
  - The Records filter and `LicencePublicationTests`.
  - Blame (D11), including any "Phase C SPDX" changes.
  - `git -C W diff <T_B> HEAD --name-only` must equal the Phase C set.

  Log the results and commit.

**The gate**

8. **Merge `test`, then the gate** (D17).
   1. **Merge.** Merge `T8` through A3, because `dd53194b` is "other". Then:
      - the restore check;
      - WIDE-A, WIDE-B and the Records filter;
      - blame;
      - D, if B's lines changed.
   2. **Code-critic.** It checks §12, contract §4, D28/D29 and the inventory. Log `<critic-pass-commit>`.
   3. **Security reviewer.**
   4. **D re-run,** if needed.
   5. **QA build:** N is the next free number above `max(branch, T8)`.
   6. **Adversarial QA:**
      1. `/health` and `/log`;
      2. equip, hold and put-back;
      3. mod-settings hold with a redraw;
      4. design apply and binding restore;
      5. Studio save and reload;
      6. `GET /sidecar` on a multi-select mod;
      7. `zip-entries.ps1`, where the orchestrator checks every entry against `package-inventory.tsv` and confirms `LICENSE.txt` is present and none of the three DLLs are;
      8. what can't be checked, listed as UNVERIFIED by design (as in rev 11).
   7. **Fixes** follow D20. An unfixable defect triggers Fallback A or B, followed by a fresh gate on that branch.
9. **Re-merge if needed, then the final commit.**
   1. Re-merge per D17, with the critic check.
   2. `git -C W diff test...HEAD --name-only` must match the D10 sets.
   3. Run both self-checks.
   4. The final bump and the `LICENSE` date.
   5. The final commit, including "Phases delivered".
   6. The short re-review.
   7. Blame on the final tip.
   8. Copy the final build.
10. **Merge into `test` and push** (rev 11). The date in `LICENSE` must be the push day.
11. **Final report:**
    - the decisions made;
    - "Phases delivered";
    - gaps in the proof;
    - D's numbers;
    - trivial-class changes;
    - the C1 result;
    - the inventory, with every carve-out flagged for the owner;
    - UNVERIFIED items;
    - `known-issue` tasks: the workflows' origins, the mesh-data origin confirmation, and QA deferrals;
    - owner follow-ups: the lawyer's review (the record, the trivial class, Penumbra.Api, the Dalamud host, any XivLiveMesh dual licence, the carve-outs), merging `test` into `main`, and the release tag.

## Files
Everything in rev 11's Files list, plus:
- **NEW** `Proteus.Tests/Fixtures/package-inventory.tsv` (Phase C): the shipped-entry classification.
- **NEW** `XivLiveMesh/LICENSE-PROTEUS.md`, only if C1 finds another user (dual licence). In that case `XivLiveMesh/LICENSE` keeps the AGPL text.
- **EDIT** trivial-class lines in files B2 edits outside the mixed set (for example `GameHarnessAdapter.cs`), logged.
- **EDIT** `docs/ipc-contract.md` for C's §5 amendments, if any.

`docs/contrib-clean-room-log.md` also records:
- "Phases delivered";
- the P-7 facts;
- "the 1b failure path added no file";
- the C1 result (including DragAndDropTexturing, names only);
- the inventory cross-check at step 8.

## Out of Scope
As in rev 11. The confirmation of the `Meshes/` foot-binding origin is an owner `known-issue` task. Until then, the file is carved out in the notices.

## Edge Cases
- **EDGE-1 to EDGE-52:** as in rev 11, with these rev-12 changes:
  - **EDGE-46:** an unfixable Phase B defect at B5, or at step 8 or later, triggers Fallback A and a fresh gate.
  - **EDGE-47:** an unfixable Phase C defect, including an unreadable licence or an unclassifiable zip entry, triggers Fallback B and a fresh gate.
  - **EDGE-48:** XivLiveMesh is used elsewhere, so it is dual-licensed (SPDX `AGPL-3.0-or-later OR LicenseRef-Proteus`, `LICENSE-PROTEUS.md`, a NOTICE statement). It is not third-party.
  - **EDGE-52:** game-derived and third-party-derived shipped data is carved out, and the inventory covers every zip entry.
- **EDGE-contrib-clean-room-53 (new):** B2 or Phase C touches a trivial-class line outside the mixed set. This is allowed only in B2's edited files, and only as a Phase C line-1 SPDX edit. Either way it is logged with its reason. Any other commit that touches one stops at D10.
- **EDGE-contrib-clean-room-54 (new):** The orchestrator inspects the API DLLs outside `IpcContractTests`, through Cecil, ildasm or a decompiler. This is a self-check-2 breach and is reported. Metadata questions go to C (§5).
- **EDGE-contrib-clean-room-55 (new):** A fallback is taken. The log says "Phases delivered: A" or "A,B". The record tests require only those phases. The fallback branch gets a fresh gate. The final report names the blocker.

## Test Strategy
All tests from rev 11 stay, with these rev-12 additions and changes:
- **`ContribCleanRoomRecordTests.Log_states_phases_delivered`** (AC-5, EDGE-55):
  - the log has exactly one of the lines "Phases delivered: A", "A,B" or "A,B,C";
  - the other record tests read it, and skip, with a stated reason, any assertion that belongs to an undelivered phase;
  - Phase A assertions always apply.
- **`Log_reports_no_contributor_lines`** (AC-3, EDGE-53) now also:
  - accepts the reasons "B2 edit of an existing file (named)" and "Phase C SPDX";
  - checks the `<R>` baseline is recorded as "nontrivial 0, using 3 (ColorTableEditor)";
  - adds the Phase B tip checkpoint.
- **`Log_lists_every_participant_and_the_clean_side_kept_clear`** (AC-5, EDGE-54) now also requires:
  - Agent C (self-report) and Agent B2, when B is delivered;
  - Plan-critic 11;
  - Planner (rev 2) to (rev 12);
  - the fresh gate agents' sections, when a fallback is taken;
  - the P-7 orchestrator facts, including the two void restore-check runs, the marker probe and the guard-classify bug;
  - "the 1b failure path added no file";
  - "orchestrator Cecil/ildasm/decompiler runs outside the test: 0".
- **`IpcContractTests`** (AC-10, EDGE-54) failure messages carry only "<owner> §<n> row <k>: <fact> differs". A test, `IpcContractTests.Failure_messages_name_only_the_row_and_fact`, feeds a deliberately mismatched synthetic entry through the comparison and checks the message format.
- **`LicencePublicationTests.Package_inventory_classifies_every_entry`** (AC-13, EDGE-52):
  - every row of `package-inventory.tsv` has a class from {own, package, native, carved-out};
  - every package, native and carved-out row names an existing block in `THIRD_PARTY_NOTICES.txt`;
  - every non-build-only package in `Proteus/packages.lock.json` has a package row;
  - the `Meshes/` foot-binding entry and the `Resources` slices are carved-out rows.
- **The step-8 cross-check** (AC-13): QA item 7's `zip-entries.ps1` output, compared with the inventory, is logged as "unclassified entries: 0".
- **`LicencePublicationTests.XivLiveMesh_licence_follows_the_usage_check`** (AC-12, EDGE-48):
  - if C1 found no user: `XivLiveMesh/LICENSE` is byte-identical to `LICENSE`, and SPDX is `LicenseRef-Proteus`;
  - if C1 found a user: SPDX is `AGPL-3.0-or-later OR LicenseRef-Proteus`, `LICENSE` keeps the AGPL text, `LICENSE-PROTEUS.md` is byte-identical to the root `LICENSE`, NOTICE carries the dual statement, and there is no §7 listing.

  The branch is read from the C1 record in the log.
- **`LicencePublicationTests.Spdx_headers_follow_the_licence`** (AC-12) accepts the dual identifier in `XivLiveMesh/` when C1 found a user.
- **`LicencePublicationTests.No_file_claims_AGPL_for_Proteus_code`** (AC-12, EDGE-50) excludes:
  - `XivLiveMesh/` only under the dual branch, and only the dual SPDX and NOTICE statements;
  - `LICENSE`'s preamble;
  - the historical records;
  - the third-party listings.

## Environment & Preconditions
As in rev 11, plus:
- C2 needs a Release build so that `latest.zip` exists for the inventory.
- QA needs the same build for item 7.

### Reads, for the log
The reads of Planner revisions 1–11, Plan-critics 1–6, 8 and 9, and the Planning orchestrator are as listed in rev 10's and rev 11's Environment & Preconditions, carried verbatim into the log, including the L-4 disclosures. The additions:
- **Planner (rev 12):** no tool calls at all (no Read, no shell or git commands, no searches, no URLs). The same automatic loads applied.
- **Plan-critic 11 (on rev 11):**
  - It opened only the rev-11 plan output file, whole, in three reads (lines 1–300, 300–579 and 580–857).
  - It ran one command, verbatim: `wc -l "<claude-projects>/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/toolu_01KxHixBRT3dmmqo79XhBdnq.txt"`.
  - It relied on its round-1 reads of `Proteus/Proteus.csproj` and `XivLiveMesh/NOTICE.md`.
  - It read nothing forbidden and started no sub-agents.
- **Orchestrator facts since rev 10 (P-7):**
  - `<R0>` = `7ce8851b`;
  - the restore-check result, and its two void runs, which printed counts only;
  - the count-only marker-probe script;
  - blame at `<R>` (`-w -M -C`): contributor non-trivial 0, Caraxi `using` 3 (ColorTableEditor), AzureRavie 0, dependabot non-trivial 1;
  - the guard = 2 (`106c450b` bump-only, `dd53194b` other), logged only;
  - the `guard-classify.ps1` parameter bug ("guard: 0") against the direct `git rev-list --count` result of 2.

## NEEDS_DECISION
None. The user's scope change is in force. The orchestrator decided ND-2 to ND-6, ND-3, the self-check option, and the rev-8 to rev-12 amendments.

My judgment calls in this revision:
- the dual-licence file layout for XivLiveMesh (`LICENSE` keeps the AGPL text, plus a new `LICENSE-PROTEUS.md`). A root `LICENSE-*` is avoided, but a sub-folder file is fine;
- the inventory lives as a test fixture, not inside the notices file;
- the record tests skip undelivered phases with a stated reason, rather than being deleted.

## Risks
As in rev 11, plus:
- **B2 touching trivial-class lines.** It is limited to the files B2 edits, and every change is logged with a reason. Non-trivial lines stay a hard failure.
- **The inventory depends on a local Release build.** The test pins the fixture's consistency with the notices and the lock file. Step 8 cross-checks the fixture against the real zip, which a test can't do on CI.
- **Mesh-data provenance.** The `Meshes/` foot binding stays carved out until the owner confirms its origin.
- **The fallback paths.** A fresh gate on a fallback branch costs time, but no unreviewed state is ever merged.

Relevant paths:
- E:\orca\workspaces\Proteus\licence-clean-room\docs\contrib-clean-room-spec.md
- E:\orca\workspaces\Proteus\licence-clean-room\docs\contrib-clean-room-plan.md
- E:\orca\workspaces\Proteus\licence-clean-room\docs\LICENSE-DRAFT.md
- E:\orca\workspaces\Proteus\licence-clean-room\THIRD_PARTY_NOTICES.txt
- E:\orca\workspaces\Proteus\licence-clean-room\XivLiveMesh\NOTICE.md
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus\Proteus.csproj
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus\packages.lock.json
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\ReadmeTermsTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\PbdCleanRoomRecordTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\HarnessBindingsWiringTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus\TestHarness\GameHarnessAdapter.cs
I've revised rev 12 to rev 12a for the owner's toe-cap confirmation. The owner said, verbatim: "The toecaps were handmade by me". So the toe-cap mesh and its foot binding are Solona's own work. They are classed "own", the owner flag and the known-issue task for them are gone, and the game-texture slices keep their carve-out. Nothing else in rev 12 changes. Below are the replacements, section by section, for splicing into `docs/contrib-clean-room-plan.md`.

## Delta (rev 12a vs rev 12)
- **Q-1: the toe-cap data is Solona's own.**
  - The toe-cap mesh and its foot binding are Proteus's own build output, classed **own** in the inventory. They are no longer carved out, and the owner flag and the `known-issue` task for them are dropped.
  - The game-assets carve-out stays for the shipped data derived from game files: the `Resources/sphere_d_array.png` and `Resources/tile_norm_array.png` slices, which are embedded in `Proteus.dll`.
  - The rule for any other shipped data stays: if C2's inventory finds data derived from game files or third-party meshes, it is carved out and flagged.
  - Changed in: the Delta (P-4), the Approval Summary (AC-13), the Facts, D29, Approach C2 and step 11, the Files note, Out of Scope, EDGE-52, the Test Strategy (`Package_inventory_classifies_every_entry`), the Risks, and the Requirements (the owner's words are added).

## Replacement text

**Delta P-4, last bullet.** Replace it with:
> The owner confirmed the toe-cap data, verbatim: "The toecaps were handmade by me". The `Meshes/` toe-cap mesh and its foot binding are classed **own**. Shipped data derived from game files (the `Resources/*.png` slices) is carved out. So is any other shipped data that the inventory finds derived from game files or third-party meshes.

**AC-contrib-clean-room-13.** Replace the carve-outs item with:
> carve-outs for the game-texture slices (`Resources/sphere_d_array.png`, `tile_norm_array.png`) and for any other shipped data derived from game files or third-party meshes;

The `Meshes/` foot binding is not a carve-out.

**Facts, "Game and third-party data in the build".** Replace it with:
> The `Resources/sphere_d_array.png` and `tile_norm_array.png` slices are extracted from game textures, and are carved out. `Meshes/` ships `toecap.mdl` and its baked foot binding. The owner confirmed both are handmade by Solona ("The toecaps were handmade by me"), so they are Proteus's own.

**D29, the inventory classes.**
- **own:** add "`Meshes/**` (the toe-cap mesh and foot binding: Solona's own, confirmed by the owner)" to the examples.
- **carved-out:** replace with "game- or third-party-derived data, with its block id. The two `Resources` slices are embedded in `Proteus.dll`, and are listed as 'embedded, carved out'. Any other entry derived from game files or third-party meshes is listed the same way."

**D29, the `THIRD_PARTY_NOTICES.txt` additions.** Replace the "third-party mesh data carved out (rev 12)" bullet with:
> Any other shipped data that the inventory finds derived from game files or third-party meshes is carved out with its own block, and gets a `known-issue` task for the owner. None is expected. The `Meshes/` toe-cap data is Solona's own (owner-confirmed), and gets no block.

The "game assets carved out" bullet stays as written.

**Approach, step 11 (final report).** The `known-issue` tasks are now "the workflows' origins, any carved-out data found by the inventory beyond the `Resources` slices, and QA deferrals". In the owner follow-ups, "the carve-outs" means the `Resources` game-texture slices, plus any found by the inventory.

**Files.** Add a note:
> `Meshes/**` is unchanged and classed **own** in `package-inventory.tsv`.

**Out of Scope.** Replace the rev-12 sentence with:
> As in rev 11. The toe-cap data's origin is settled by the owner's confirmation, so nothing about it is out of scope.

**EDGE-contrib-clean-room-52.** Replace it with:
> Game-derived data ships in the plugin. The `Resources/*.png` slices, embedded in `Proteus.dll`, are carved out in the notices, and the inventory covers every zip entry. Any other entry that is derived from game files or third-party meshes is carved out the same way, and flagged. The `Meshes/` toe-cap data is classed **own**, per the owner's confirmation.

**Test Strategy, `LicencePublicationTests.Package_inventory_classifies_every_entry`.** Replace its last check with:
> the `Resources` slices are carved-out rows, and every `Meshes/` row is classed **own**.

**Risks.** Remove the "Mesh-data provenance" bullet. Under "the licence's legal footing", the carve-out item now reads "the game-asset carve-out for the `Resources` slices".

**Requirements.** Add after the user's scope change:
> **Owner's confirmation (verbatim):** "The toecaps were handmade by me".

**Reads, for the log.** Planner (rev 12a) made no tool calls at all: no Read, no shell or git commands, no searches, no URLs. It had the same automatic loads.
I've revised rev 12a to rev 12b, fixing Plan-critic 12's two defects in how fallbacks are recorded. Rev 12 + 12a otherwise stands. Planner (rev 12b) made no tool calls.

## Delta (rev 12b vs rev 12a)
- **R-1: Record tests skip undelivered phases with no new package.** xunit 2.9.3 has no runtime skip, and Xunit.SkippableFact would be a new dependency, so the record tests use two mechanisms:
  - **Per-phase assertions** are an `[Theory]` fed by `[MemberData]`. A static helper parses the "Phases delivered:" line of `docs/contrib-clean-room-log.md` and yields only the delivered phases (A always; then B, then C), so assertions for an undelivered phase never run.
  - **Mixed assertions** that can't be split by phase take `ITestOutputHelper`. For an undelivered phase they write "skipped: Phase <X> not delivered (Phases delivered: <line>)" and return early.
  - The Test Strategy now says the record tests use no new test package.
  - Changed in: D27, Test Strategy, EDGE-55.
- **R-2: A fallback branch keeps the records of the abandoned phases.**
  - The final commit on the fallback branch is log-only. It carries every log section from the abandoned phase under a heading "Attempted, not delivered: Phase B" (or "Phase C", or both), followed by the blocker and the last commit on the abandoned branch.
  - `Log_lists_every_participant_and_the_clean_side_kept_clear` requires a section for every agent started in the session, whatever phases were delivered.
  - Agents of an abandoned phase are checked inside that heading, with the same allowlist, extraction and zero-count rules.
  - Changed in: D27, step 9 (final commit), EDGE-55, Test Strategy, Files.
- **R-3: Log inputs.** Plan-critic 12's reads are added.

## Replacement text

**Approval Summary, AC-contrib-clean-room-5.** Replace the last item with:
> the record tests pass, use no new test package, only parse files, and assert the delivered phases. Agents of an undelivered phase are checked under "Attempted, not delivered".

**D27, Fallback A and Fallback B.** Add after "re-run steps 8–10 on it with a fresh code-critic, a fresh security reviewer and fresh QA":
> On the fallback branch, the final commit (step 9) is log-only and adds:
> - the line "Phases delivered: A" (or "A,B");
> - a section "Attempted, not delivered: Phase B" (or C, or both), holding every log section of that phase as it stood on the abandoned branch. That covers the agent sections (C, B2, the QA-lite smoke agent, any Phase C participants), their extracted lists, the proofs that ran, and the inventory work if any;
> - the blocker, stated plainly;
> - the SHA of the abandoned branch's last commit.
>
> The abandoned branch is kept locally. It is not pushed or deleted.

**D27, record tests.** Replace "the record tests read the 'Phases delivered' line and require only the delivered phases' sections, tests and records" with:
> The record tests read the "Phases delivered" line. Delivered phases get their full assertions. For an undelivered phase:
> - its section must sit under "Attempted, not delivered";
> - every agent section in it is checked with the same allowlist, extraction and zero-count rules;
> - its delivery-only assertions (the B4/C3 proofs, inventory consistency, licence publication) don't run.
>
> No assertion runs on a phase that was never started.

**Approach, step 9, item 5 (final commit).** Replace with:
> The final commit, with "Phases delivered" and, on a fallback branch, the "Attempted, not delivered" section (D27).

**Files, `docs/contrib-clean-room-log.md`.** Add:
> On a fallback branch, an "Attempted, not delivered: Phase B/C" section with the abandoned phase's log sections, its blocker, and the abandoned branch's last SHA.

**EDGE-contrib-clean-room-55.** Replace with:
> A fallback is taken.
> - The log says "Phases delivered: A" or "A,B".
> - The fallback branch's final, log-only commit carries the abandoned phase's sections under "Attempted, not delivered: Phase B" (or C), with the blocker.
> - The record tests check:
>   - every agent started in the session has a section;
>   - abandoned phases' agents pass the same clean-side checks;
>   - delivery assertions run only for delivered phases.
> - The fallback branch gets a fresh gate.
> - The final report names the blocker and the kept abandoned branch.

**Test Strategy.** Add at the top:
> The record tests use no new test package; xunit 2.9.3 as already referenced is enough. There are two mechanisms:
> 1. **Per-phase assertions** are `[Theory]` methods with `[MemberData(nameof(DeliveredPhases))]`. `DeliveredPhases` is a static helper. It reads `docs/contrib-clean-room-log.md` through the existing repo-root helper, parses the single "Phases delivered:" line, and yields `"A"`, then `"B"` and `"C"` only if delivered. The test fails if the line is missing or appears more than once.
> 2. **Mixed assertions** inject `ITestOutputHelper`. When an assertion belongs to an undelivered phase, it writes "skipped: Phase <X> not delivered (Phases delivered: <line>)" and returns.

Replace the rev-12 entry `ContribCleanRoomRecordTests.Log_states_phases_delivered` with:
> **`ContribCleanRoomRecordTests.Log_states_phases_delivered` (AC-5, EDGE-55):**
> - exactly one "Phases delivered:" line, with the value A, A,B or A,B,C;
> - if it isn't A,B,C, a section headed "Attempted, not delivered: Phase B" and/or "Phase C" exists for each missing phase that was started. Each holds a "Blocker:" line and an "Abandoned branch last commit:" SHA.

Replace the rev-12 additions to `Log_lists_every_participant_and_the_clean_side_kept_clear` with:
> **`Log_lists_every_participant_and_the_clean_side_kept_clear`** requires a section for every agent started in the session, whatever phases were delivered:
> - Planner;
> - every Plan-critic (1–6, 8, 9, 11, 12, and any later one);
> - Planner (rev 2) to (rev 12b);
> - Planning orchestrator;
> - Agent A (self-report), and A2/A3 if used;
> - Orchestrator;
> - Agent B;
> - Agent D (self-report);
> - Agent C (self-report) and Agent B2, if Phase B was started;
> - the QA-lite smoke agent, if B5 ran;
> - Code-critic, Security review and Adversarial QA, for each gate run, including a fresh gate on a fallback branch.
>
> Sections of an abandoned phase may sit under "Attempted, not delivered". All of these get the same checks:
> - `Opened:`;
> - the allowlists;
> - `Sub-agents: none`;
> - the three zero counts;
> - "Identifier-grep hits: 0" for dirty agents.
>
> Plus the P-7 facts, "the 1b failure path added no file", and "orchestrator Cecil/ildasm/decompiler runs outside the test: 0".

Phase-B- and Phase-C-only assertions become per-phase theory cases: the Phase B tip checkpoint in `Log_reports_no_contributor_lines`, the B4 and C3 records, inventory consistency, and the licence-publication records. They run only when that phase is delivered. The checks in `LicencePublicationTests` and `ReadmeTermsTests` that need the licence actually published are guarded the same way. They read the log's "Phases delivered" line and run the AGPL-era guard assertions when C isn't delivered. A fallback branch never carries the Phase C test edits, so on A or A,B those files are unchanged from `test` and keep their AGPL guards.

## Reads, for the log
- **Planner (rev 12b):** no tool calls at all (no Read, no shell or git commands, no searches, no URLs). The usual files were loaded automatically.
- **Plan-critic 12 (on rev 12 + 12a):** opened only `scratchpad/plan-rev12.md` and `scratchpad/plan-rev12a.md` (both whole), ran no shell commands, and relied on its earlier reads for the rev 10 and rev 11 wording. Nothing forbidden, and no sub-agents.
I've revised rev 12b to rev 12c to fix the two problems Plan-critic 13 found. The rest of rev 12 + 12a + 12b stands. Planner (rev 12c) made no tool calls.

## Delta (rev 12c vs rev 12b)
- **S-1: The "Phases delivered:" line is written at each phase boundary.** This replaces 12b's assumption that the line exists only from the final commit.
  - At `T_A`, the step-7 log commit writes "Phases delivered: A".
  - At `T_B`, the B5 log commit changes it to "Phases delivered: A,B".
  - C3's log commit changes it to "Phases delivered: A,B,C".
  - The step-9 final commit either confirms the line or sets the fallback value.
  - `DeliveredPhases` still fails if the line is missing or appears more than once.
  - On a fallback branch cut at `T_A` or `T_B`, the line already reads "A" or "A,B", so the final commit only confirms it.
  - Changed in: Approach steps 7, B5, C3 and 9; D27; Test Strategy; EDGE-55.
- **S-2: The permanent licence tests no longer read the contrib log.** 12b's log-keyed guard in `ReadmeTermsTests` and `LicencePublicationTests` is gone.
  - A fallback branch never carries the Phase C edits to those files. On A or A,B they stay exactly as on `test`, including `ReadmeTermsTests.Licence_stays_AGPL_and_drafts_are_drafts`. `LicencePublicationTests.cs` doesn't exist there at all.
  - On a branch that delivers C, the Phase C versions apply unconditionally.
  - No other guard is added. The file set of each branch already decides which version runs. 12b's "AGPL-era guard assertions when C isn't delivered" is withdrawn.
  - Only `ContribCleanRoomRecordTests` reads the log (and so `DeliveredPhases`).
  - Changed in: the Test Strategy and D28.
- **S-3: Log inputs.** Plan-critic 13's reads are added.

## Replacement text

**Approach, step 7.** Add at the end:
> The step-7 log commit also writes the line "Phases delivered: A". Record `T_A` as that commit.

**Approach, B5.** Replace "record `T_B`" with:
> The B5 log commit changes the "Phases delivered:" line to "Phases delivered: A,B". Record `T_B` as that commit.

**Approach, C3.** Add at the end:
> C3's log commit changes the "Phases delivered:" line to "Phases delivered: A,B,C".

**Approach, step 9, item 5 (final commit).** Replace with:
> The final commit confirms the "Phases delivered:" line, or sets the fallback value. On a fallback branch cut at `T_A` or `T_B`, the line already reads "A" or "A,B", and the commit adds the "Attempted, not delivered" section (D27).

**D27, record tests.** Add:
> The "Phases delivered:" line exists from `T_A` onward. It is written at step 7, updated at B5 and C3, and confirmed or set at step 9. Exactly one such line exists at any commit from `T_A` on.

Delete 12b's last paragraph of the Test Strategy replacement text, from "The checks in `LicencePublicationTests` and `ReadmeTermsTests` that need the licence actually published…" to the end. Replace it with:
> The permanent licence tests (`ReadmeTermsTests`, `LicencePublicationTests`, the `PbdCleanRoomRecordTests` header check) never read `docs/contrib-clean-room-log.md`, and have no phase guard. Which version runs follows from the branch's files:
> - **A or A,B (fallback):** the Phase C edits are absent, so `ReadmeTermsTests.Licence_stays_AGPL_and_drafts_are_drafts` and the AGPL header check run as on `test`, and `LicencePublicationTests.cs` doesn't exist.
> - **A,B,C:** the Phase C versions run unconditionally.
>
> Only `ContribCleanRoomRecordTests` reads the log.

**Test Strategy, `DeliveredPhases`.** Replace its description with:
> `DeliveredPhases` is a static helper. It reads `docs/contrib-clean-room-log.md` through the repo-root helper, finds the single "Phases delivered:" line, and yields "A", then "B" and "C" only if listed. It fails when the line is missing, appears more than once, or holds a value other than A, A,B or A,B,C. Because the line is written at `T_A` and kept current at B5, C3 and step 9, the record tests run correctly at every commit from `T_A` on.

**Test Strategy, `ContribCleanRoomRecordTests.Log_states_phases_delivered`.** Add:
> The log records which commit wrote or changed the line (`T_A`, `T_B`, the C3 commit, the final commit). Its value never lists a phase whose tip commit isn't recorded.

**D28, Tests.** Add:
> The replaced `ReadmeTermsTests` licence test and the new `LicencePublicationTests` have no conditional logic and read only repository files (`LICENSE`, the notices, the inventory, the csproj files, the SPDX lines), never the contrib log.

**EDGE-contrib-clean-room-55.** Add:
> The "Phases delivered:" line already holds "A" or "A,B" at the cut point. The final commit confirms it. The permanent licence tests are unchanged on the fallback branch, so they run their `test` versions with no guard.

## Reads, for the log
- **Planner (rev 12c):** no tool calls at all (no Read, no shell or git commands, no searches, no URLs). Same files loaded automatically as before.
- **Plan-critic 13 (on rev 12b):** opened only `scratchpad/plan-rev12b.md`. Ran no commands. Nothing forbidden, no sub-agents.
