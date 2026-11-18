# Implementation Plan: Clean-room replacement, Proteus without Luna, and publishing the Proteus Licence (revision 11)

## Delta (rev 11 vs rev 10)
The user changed the scope. Their message, verbatim: "Do the Luna work as well. Keep going until you've pushed that and all the other needed changes." That reverses their earlier ND-1 choice, so the goal is again to publish the Proteus Licence, pushed to `test` only.

**Where Phase A stands:**
- Steps 0, 1, 1b and 2 are done.
- `<R0>` = `7ce8851b`. `restored-check.ps1` printed: added 39, markers 27, restored verified 12, unmatched 0, other 0.
- The blame at `<R>` is running.

**Changes in this revision:**
- **N-1: Phase B and Phase C come back,** adapted from rev 1. They run on the same branch after Phase A's step 7, as steps B1–B6 and C1–C3.
  - **Phase B** drops Luna, Penumbra.Api and Glamourer.Api from the plugin. Proteus gets its own call gates over Dalamud's `GetIpcSubscriber`. The work is done by Agent C (contract), the orchestrator (tests) and Agent B2 (implementation), followed by an in-game smoke check.
  - **Phase C** publishes the licence.
  - The single gate at step 8 now covers A, B and C. Each phase keeps its own proof.
  - New decisions: D24–D31.
- **N-2: Fallbacks (D27).**
  - If Phase B can't pass, only Phase A merges, and the licence stays AGPL.
  - If Phase C can't pass, A and B merge, and the licence stays AGPL.
  - Either way, the blocker is reported.
- **N-3: Plan-critic 1's Phase B/C notes are folded in:**
  - owned types stay in the same namespaces (`Penumbra.Api.Enums`, `Glamourer.Api.Enums`), so the ~60 `using` lines don't change (D24);
  - the contract test also compares each enum's underlying type and `[Flags]`, and member names exactly, because enum-keyed dictionaries cross by name (D26);
  - Agent C may read Luna's IPC subscriber base classes, for facts about errors and availability only, and bridge-availability tests are added (D25, D26);
  - B5 adds an in-game smoke check before Phase C (D27);
  - the licence's completeness now covers the game-texture slices in `Resources/`, the submodules, the workflows and the XivLiveMesh usage check (D28, D29).
- **N-4: The slnx keeps the two API entries (D30).** The test build still builds the APIs for the contract test.
- **N-5: Clean-room interaction.**
  - Agent C is dirty only for the API sources and the named Luna base classes, and never reads the contributors' commits.
  - B2 starts after B's last commit and never reads C's sources or the removed set.
  - D measures only B's lines. B2's lines get no similarity check, because B2 never saw the removed set.
  - New forbidden location: `scratchpad-agentC`.
- **N-6: The orchestrator's ND decisions are recorded:**
  - ND-2: XivLiveMesh moves to the Proteus Licence, unless the C1 usage check finds another plugin using it. Then it stays AGPL as Solona's separately offered library, noticed under §7.
  - ND-4: only the status line of the licence changes.
  - ND-5: no README licence line.
  - ND-6: the workflows' origins become a `known-issue` task. The workflows are still noted under §7.
  - Penumbra.Api's missing licence is flagged for the owner's lawyer.
  - The effective date is the day `test` is pushed.
- **N-7: These rules change to fit the new phases:**
  - AC-7 is now "Phases A and B change no licence or notice file", checked at the Phase B tip.
  - New ACs 9–13 cover Phase B and Phase C.
  - The three guard tests stay unchanged through Phases A and B. Phase C replaces `ReadmeTermsTests.Licence_stays_AGPL_and_drafts_are_drafts`, makes `PbdCleanRoomRecordTests.Header_names_the_spec_not_a_port` follow `LICENSE`, and edits only the Luna doc and message in `SupplyChainTests`.
  - `IpcProvider.cs` line 1 follows `LICENSE`.
  - The orchestrator may edit SPDX lines in Phase C.
  - The short re-review also covers a `LICENSE` date line.
  - The log's opening lines now describe all three phases.
- **N-8: Why 13 ACs.** This is over the ~10 guideline. The user asked for the whole job in one run, every phase has its own proof and fallback, and splitting would leave the licence unpublished.

All rev-10 rules for Phase A stay as they were, except where N-7 changes them. Planner (rev 11) made no tool calls.

## Approval Summary
**Goal:** Replace every non-trivial line Caraxi and AzureRavie wrote with code written by an agent that never saw theirs, keeping the public IPC and the small fixes exactly as before. Ship Proteus without Luna, Penumbra.Api and Glamourer.Api. Then publish the Proteus Licence on `test`.

**Slug:** `contrib-clean-room`

**Acceptance Criteria:**
- AC-contrib-clean-room-1: A plugin calling Proteus's public IPC sees the same labels, argument types, return types and results as before. Tests written from the spec prove it: they pass on the old code and on the clean code.
- AC-contrib-clean-room-2: The contributors' small features still work as before, unless the spec drops one with a reason:
  - `/proteus refresh silent`;
  - clicking a mod's name opens it in Penumbra;
  - the priority drag;
  - multi-select defaults and per-option tables;
  - case-insensitive lookup;
  - the Glamourer cooldown counted from the attempt;
  - lock-conflict logging.
- AC-contrib-clean-room-3: No non-trivial contributor line remains.
  - Whole-tree blame runs at `<R>`, the step-6 tip, the Phase B tip, the step-8 merged tip and any step-9 re-merge, in both modes, and at the final tip in `-w -M -C`.
  - Every line it gives to Caraxi or AzureRavie must fall in a trivial category, or be a game-fact line in a file §1 lists, within that file's §1 count (3 at most in total).
  - Count changes are logged by file and category, with a reason.
  - The derived closure is removed.
  - `<R0>…<R>` and every merge add only allowed lines.
  - The sample is deleted, and `proteus.slnx` is regenerated.
- AC-contrib-clean-room-4: B's code is shown to be independent. At the final tip, D finds 0 expressive non-interface lines in B's surviving commits that match any removed line. Every run is logged, starting with the first.
- AC-contrib-clean-room-5: The record is complete:
  - the spec (with its trivial class and game-fact lines) and the IPC contract are facts only;
  - the log has every participant's reads, verbatim;
  - the clean agents' sections are extracted from transcripts and stay inside their allowlists;
  - both self-checks are 0 beyond their exceptions;
  - the dirty agents give 0 identifier-grep hits;
  - the tripwire re-plan and the scope change are recorded;
  - the record tests pass, and they only parse files.
- AC-contrib-clean-room-6: The proof holds in each phase:
  - the old-code gate (read through the hashing TRX script), "not passing" after removal, then green on the clean code;
  - the Phase B proof (WIDE-B plus WIDE-A, contract and shipped-assembly tests);
  - green on every merged tip.

  In each run, tests that existed at step 2 keep the same skips, and baseline hashes are equal (or A answers "differs only in <kind>"). New tests pass, or skip with a reason.
- AC-contrib-clean-room-7: Phases A and B change no licence or notice file. `git diff test...<B tip> --name-only` lists none, and that is logged. The three guard tests pass, unchanged, through the end of Phase B.
- AC-contrib-clean-room-8: Finish:
  - the branch merges into `test` locally, adding nothing beyond the last reviewed merge state;
  - the Records filter, the blame re-check and the quick suite pass on `test`;
  - only `test` is pushed. Nothing goes to `main`, there are no tags, and no release or deploy workflow runs.
- AC-contrib-clean-room-9: The plugin package contains no `Luna.dll`, `Penumbra.Api.dll` or `Glamourer.Api.dll`. Neither `Proteus.csproj` nor `packages.lock.json` references them, and `Proteus.dll` references none of them.
- AC-contrib-clean-room-10: Proteus talks to Penumbra and Glamourer exactly as before:
  - every IPC label, kind and signature it uses matches the API submodules;
  - so does every enum's name, member, value, underlying type and `[Flags]`;
  - the bridges behave as the contract says when Penumbra or Glamourer is absent or not ready;
  - the B5 smoke check and step 8's QA pass in game (equip and hold, redraw, design apply, bindings, mod settings).
- AC-contrib-clean-room-11: `LICENSE` is the Proteus Licence, marked "*Version 1.0 — in force from <push date>*", with no DRAFT marker. `docs/LICENSE-DRAFT.md` is gone, there is no root `LICENSE-*` file, and the plugin package carries the licence.
- AC-contrib-clean-room-12:
  - Every SPDX header of Proteus's own code reads `LicenseRef-Proteus`.
  - XivLiveMesh's `LICENSE`, `NOTICE.md` and csproj follow the C1 usage check: either the Proteus Licence (a byte-identical `LICENSE`), or AGPL as a separately noticed library.
  - Both csproj files drop `PackageLicenseExpression`.
  - Outside the historical records and the third-party listings, no file claims the AGPL for Proteus's own code.
- AC-contrib-clean-room-13: `THIRD_PARTY_NOTICES.txt` keeps its existing MIT and public-domain blocks, and adds:
  - every package that ships, with its licence taken from the nuspec;
  - the Glamourer-derived types (Apache-2.0);
  - an interoperability note for Penumbra's and Glamourer's IPC facts;
  - the game-asset carve-out (`Resources/sphere_d_array.png`, `tile_norm_array.png`);
  - the submodules, with their licences;
  - a note on the workflows adapted from other projects.

**Key decisions:**
- **Phase A** (unchanged from rev 10):
  - dirty A removes the lines and never commits;
  - clean B rebuilds them;
  - dirty D checks the similarity;
  - the clean side sees only the clean diffs;
  - trivial coincidental lines outside the mixed set stay, and are logged.
- **Phase B:**
  - Agent C (dirty only for the API sources and the named Luna base classes) writes `docs/ipc-contract.md`;
  - the orchestrator writes Cecil contract tests, shipped-assembly tests and bridge-availability tests;
  - clean B2 replaces every Penumbra.Api, Glamourer.Api and Luna use with Proteus-owned call gates, keeping the owned types in the APIs' namespaces;
  - an in-game smoke check (B5) comes before Phase C.
- **Phase C:**
  - the orchestrator publishes the licence and the notices (licence text from the nuspec files);
  - XivLiveMesh's licence follows a usage check;
  - the effective date is the push day.
- **One gate for all three phases:** merge `test`, then code-critic, then a clean security review, then the D re-run, then QA in game. The short re-review covers every commit after the critic's pass.
- **Fallbacks:** A alone, or A and B, with the licence staying AGPL, if a later phase can't pass.

**Risk flags:** security surface: yes (`Proteus/Interop/`, public IPC, `Proteus/TestHarness/`, licence and notices) · new persisted field: no · sensitive-category data: no · schema migration: no · new dependency: no (three removed) · new/changed route: no

**NEEDS_DECISION:** None. The user decided ND-1 (now reversed: full scope). The orchestrator decided ND-2 to ND-6, ND-3, the self-check option and the rev-8 to rev-11 amendments.

*(The summary above is what the developer approves; everything below is the detailed contract it stands on.)*

## Source
- The user's prompt, choices, both decisions and the feature arguments (verbatim under Requirements).
- The precedent:
  - `docs/pbd-clean-room.md`
  - `docs/pbd-clean-room-plan.md`
  - `docs/pbd-clean-room-log.md`
  - `Proteus.Tests/PbdCleanRoomRecordTests.cs`
- The committed spec `docs/contrib-clean-room-spec.md` (`1a2cd3ef`, amended at 1b).
- The orchestrator's state report.
- Rev 1's Phase B/C design, and Plan-critic 1's notes on it.
- The reports of Plan-critics 1–6, 8 and 9.
- `docs/LICENSE-DRAFT.md`, `THIRD_PARTY_NOTICES.txt`, `XivLiveMesh/NOTICE.md`, `Proteus/Proteus.csproj` and `Proteus/packages.lock.json`, as read in rev 1.
- Not opened, and forbidden to the planner: the mixed files and their history, the contributors' commits, the Luna, Glamourer and Penumbra.Api sources, and the contributors' PRs.

## Context & Decisions
**Problem.** The Proteus Licence can't honestly take effect while any of these three hold:
- the contributors' code survives, because licence §6 keeps it under AGPL;
- the plugin zip ships `Luna.dll`, which is AGPL-3.0;
- the zip ships `Penumbra.Api.dll`, which has no licence file.

Shipped NuGet packages and game-derived assets also need notices. Phase A deals with the first, Phase B with the second and third, and Phase C with the notices and the publication.

**Facts (from allowed files, the committed spec and the orchestrator's reports):**
- **Phase A facts carry over from rev 10:**
  - the step-0 blame;
  - §1's totals;
  - the 11-entry mixed set;
  - the six IPC labels;
  - the §9.1 seam reachability;
  - the cooldown and lock locations;
  - the §10 tests and the EDGE-5 hit, which is kept;
  - the harness routes;
  - 12 test files using `proteus.slnx`;
  - pushing to `test` runs only `codeql.yml`;
  - `BuildNumber` is 1467 on `test` and 1466 on the branch;
  - `W = E:/orca/workspaces/Proteus/licence-clean-room`.
- **Luna ships today.** `Proteus.csproj` has ProjectReferences to `Penumbra.Api`, `Luna` and `Glamourer.Api`. The csproj comment says Luna holds Penumbra.Api's IPC subscribers. `packages.lock.json` shows `glamourer.api` depending on `Luna`. `SupplyChainTests.Luna_IsOnTheCommitGlamourerPins` says "one Luna.dll ships".
- **Licences:** `Luna/LICENSE` is AGPL-3.0, `Glamourer/LICENSE` is Apache-2.0, and `Penumbra.Api` has no licence file.
- **API uses outside the bridges** are only `using Penumbra.Api.Enums` and `using Glamourer.Api.Enums`, in about 53 production and test files. `GameHarnessAdapter.cs` creates `Glamourer.Api.IpcSubscribers.ApplyDesign` and `ApplyState`. ADR 0002 says ApplyDesign is "created inside the harness, never in `Interop/`". `HarnessBindingsWiringTests` lines 36–54 pin that.
- **Shipped packages** (direct, per the lock file): BCnEncoder.Net, BitMiracle.LibTiff.NET, CommunityToolkit.HighPerformance, StbImageSharp and StbImageWriteSharp. The transitive ones (Microsoft.Extensions.*, System.IO.Hashing, JetBrains.Annotations) come in through Luna and Penumbra.Api. DalamudPackager and DotNet.ReproducibleBuilds are build-time only.
- **Packaging.** `release.yml` and `testing-release.yml` ship `latest.zip`, which is the build output. `THIRD_PARTY_NOTICES.txt` already ships as content. `LICENSE` doesn't ship.
- **Game assets in the build.** The csproj comments call `Resources/sphere_d_array.png` and `tile_norm_array.png` slices extracted from game array textures.
- **Licence mentions:**
  - `Proteus.csproj:26` and `:175`;
  - `XivLiveMesh.csproj:6` and `:19`;
  - `XivLiveMesh/NOTICE.md` lines 6 and 18;
  - SPDX lines in 7 `XivLiveMesh/*.cs` files;
  - `ReadmeTermsTests`;
  - `PbdCleanRoomRecordTests`.

  README and For Creators.md have none. TERMS.md points at `[LICENSE](LICENSE)`.
- **The draft licence's preamble** names "the GNU Affero General Public License, version 3" for earlier versions. So the publication test checks for the AGPL's uppercase title, not for the word "Affero".
- **XivLiveMesh.csproj** says "Shared between Dalamud plugins". Whether another plugin actually uses it is unknown (C1 checks).

**Decisions (and rejected alternatives).** D1–D23 are the Phase A rules from rev 10, with the N-7 edits marked "(rev 11)". D24–D31 are new.

1. **Roles.** Every sub-agent runs on model `opus`.

   | Role | Side | Reads | Writes | Shell / git |
   |---|---|---|---|---|
   | Orchestrator | clean | anything D9 allows; the mixed set only at or after `<R>` | tests, docs, the log, the slnx, the `BuildNumber` line, SPDX lines and licence/notice files in Phase C (rev 11); **every commit**, made under D10 without viewing | D10/D11; D5 before `<R>`; count-only scripts |
   | Agent A (stages 1/1b/2) | dirty | the repo and its history, the TRX files | the spec, the removal (deletions, restores, markers), the manifest, its self-reports | git reads, `dotnet`. **Never commits** |
   | A2 / A3 | dirty | as A; each recomputes the closure from history | as A | as A; **A3 alone** may run `git merge --no-commit test` |
   | Agent D | dirty | the mixed set at `<R0>^`, B's commits, A3's resolution lines, the spec | numbers, categories, clean-side `file:line` | git reads, its own scripts |
   | Agent B (after `<R>`) | clean | HEAD, minus D9 | the holes, `IpcProvider.cs`, §6.3/§6.4, Localization, named new files, fixes to the merged tip | D12 |
   | **Agent C (rev 11)** (fresh `general-purpose`, after step 7) | dirty only for the API sources | `Penumbra.Api/**`; `Glamourer/Glamourer.Api/**`; Glamourer's root LICENSE and NOTICE; the Luna IPC subscriber base classes that those API subscribers derive from (facts only; each file logged); Proteus's own sources at the Phase A tip. Never the contributors' commits, the mixed set before `<R>`, other `Glamourer/` folders or the rest of `Luna/` | `docs/ipc-contract.md`, its self-report | `git -C <submodule> ls-files`, Read/Grep; no `dotnet` |
   | **Agent B2 (rev 11)** (fresh `general-purpose`, after C's commit) | clean; never reads C's sources or the removed set | HEAD minus D9, and `docs/ipc-contract.md` | `Proteus/Interop/**`, `Proteus/TestHarness/**` gate files and the adapter, `Proteus/Proteus.csproj` (reference removal), `Proteus/packages.lock.json` (through the build), named new files, Phase B fixes | D12 (same allowlist as B) |
   | Code-critic | clean | the D8 diffs, HEAD, the spec, the contract, the log | findings, the §12 table, the contract §4 table | D12 |
   | Security reviewer | clean | as the critic | findings, the §12 table | D12 |
   | Adversarial QA | clean | `docs/agent-rules/adversarial-qa.md`, the Requirements, spec §4–9, the contract, HEAD after `<R>`, the harness config | QA report | `curl` to `127.0.0.1`/`localhost` |
   | Short re-reviewer | clean | the commits after `<critic-pass-commit>` | verdict | D15 |
   | Planner, plan-critics | clean | the plan, allowed files, the spec, the contract | reports listing their reads | as reported |

   - **Folders.** All sit outside `W` and are logged: `scratchpad-agentA\`, `scratchpad-agentC\` (rev 11), `scratchpad-agentD\` and `scratchpad-handover\`.

2. **The removal set** (rev 10, unchanged).
   - **Direct lines:** blame in either mode.
   - **The trivial coincidental class:** lines that match the fixed regex (`^$`, `using`, `namespace`, `[{}];?`) or are game-fact lines (listed by `file:line` with a shape; the remainder trivial; a per-file limit from §1, at most 3 in total), in files with no non-trivial set line. These are not removed.
   - **Derived lines:** the transitive closure, with the merge rule.
   - **Carried-over lines:** including `87ae2e3d`.
   - **Whole files.**
   - **Restored lines,** with the manifest in `scratchpad-handover`.
   - **A's operations:** delete, restore, add markers. Markers go in C# files only.

3. **The scope tripwire** (rev 10).
   - **Condition 1:** derived + carried over + restored > 2 × (whole-file lines + non-trivial direct lines).
   - **Condition 2:** a non-trivial set line in a Sensitive-Area file beyond the known Interop files.
   - The step-1 firing and the rev-8 re-plan are logged.
   - Phase B's changes are not set growth: B2 is clean.

4. **Tests are written from the spec and proven on the old code** (rev 10).
   - Tests that need a new seam carry the traits NewSeam and `Seam=§9.<row>`. Departure tests carry NewBehaviour.
   - If the NewSeam share is above a third, it is reported as a weaker AC-1.
   - The EDGE-5 test is kept.

5. **The old-code output filter** (rev 10).
   - TRX goes to the scratchpad, and console output to an unread file.
   - `trx-summary.ps1` prints names, outcomes, skips, types and normalised hashes.
   - `build-summary.ps1` prints codes only.
   - Failures, CS1061 and CS1729 go to A.

6. **The strings rule** (rev 10). Text is described, not quoted. Only interface strings are exact. New UI text goes into all 8 locales.

7. **D's similarity check** (rev 10).
   - **Input:** B's commits' surviving lines (SHAs logged) and A3's resolution lines.
   - **Categories:** interface, idiomatic or forced, expressive.
   - **Record:** the first measurement and every later one. Expressive hits go to B until none are left.
   - **Rev 11:** B2's lines are not measured. B2 never saw the removed set, and its work starts after D's step-7 measurement. D re-runs only if B's own lines change.

8. **The clean diffs** (rev 10).
   - **Pre-removal:**

     ```
     git -C W diff test...<R0>^ -- . ':!Proteus/Interop/IpcProvider.cs' ':!Proteus/Interop/PenumbraBridge.cs' ':!Proteus/Interop/GlamourerBridge.cs' ':!Proteus/Plugin.cs' ':!Proteus/Services/SidecarDiscoveryService.cs' ':!Proteus/Gui/StatusWindow.cs' ':!Proteus/Gui/ModsTab.cs' ':!Proteus/Services/CompositorService.Carriers.cs' ':!Proteus/Services/CompositorService.cs' ':!proteus.slnx' ':!samples'
     ```

   - **Post-removal:** `git -C W diff <R> HEAD`. This covers Phases B and C too.
   - **Re-merge:** `git -C W diff <P9> HEAD`, or `--stat`.
   - **Never:**
     - an unexcluded diff from `test`;
     - any show or diff of `<R0>…<R>`;
     - `log -p` or content blame of the mixed set before `<R>`;
     - a combined merge diff.

9. **Forbidden for every clean participant** (rev 10, plus rev 11):
   - the mixed set before `<R>`;
   - the contributors' commits;
   - the submodule sources (their LICENSE and README files are allowed);
   - `bin/` and `obj/` contents, except the Phase B name-only checks (B4);
   - other checkouts;
   - the files of `E:\repos\Proteus`;
   - the memory topic files;
   - A's, C's and D's folders and transcripts (rev 11: C added);
   - `scratchpad-handover\`;
   - the pre-`<R>` `.out` and `.trx` files.

   **Rev 11:** B2 and every clean agent are also barred from Luna's base classes and from all API sources.

10. **Git hygiene** (rev 10, plus rev 11 expected sets).
    - Use `git add -u` plus explicit paths, after a `git status --short` check. Allowed paths per commit:
      - `<R0>…<R>`: the mixed set and the whole files;
      - removal report: the log;
      - slnx commit: `proteus.slnx`;
      - B's commits: the mixed set, `IpcProvider.cs`, Localization, the log, and the named files;
      - merges: what `--name-only` lists against `test`;
      - **C's commit:** `docs/ipc-contract.md` and the log;
      - **Phase B test commit:** `Proteus.Tests/IpcContractTests.cs`, `ShippedAssembliesTests.cs`, `BridgeAvailabilityTests.cs`, `HarnessBindingsWiringTests.cs` (the lines-40/43/44 names only), `Proteus.Tests.csproj` and the log;
      - **B2's commits:** `Proteus/Interop/**`, `Proteus/TestHarness/**`, `Proteus/Proteus.csproj`, `Proteus/packages.lock.json`, `Proteus/Plugin.cs` (wiring only, if any), the files B2 names, and the log;
      - **Phase C commit:** `LICENSE`, `docs/LICENSE-DRAFT.md` (deleted), `XivLiveMesh/LICENSE`, `XivLiveMesh/NOTICE.md`, `XivLiveMesh/XivLiveMesh.csproj`, `XivLiveMesh/*.cs` (line 1 only), `Proteus/Interop/IpcProvider.cs` (line 1 only), any B2 file with an SPDX line (line 1 only), `Proteus/Proteus.csproj`, `THIRD_PARTY_NOTICES.txt`, `AGENTS.md`, `Proteus.Tests/ReadmeTermsTests.cs`, `PbdCleanRoomRecordTests.cs`, `SupplyChainTests.cs`, `LicencePublicationTests.cs`, and the log.
    - No commit touches a trivial-class line outside the mixed set.

11. **The count-only scripts** (rev 10).
    - **`blame-authors.ps1`.** The pass rule: `nontrivial` = 0, and game-fact lines stay within the per-file §1 limits. Changes are logged with an allowed reason:
      - a new file by B, B2 or Phase C (named);
      - a file new from `test`;
      - an import edit from `test`;
      - a copy-detected import block from a test commit.

      The baseline is `<R>`, compared with §1.
    - **`restored-check.ps1`, `guard-classify.ps1`, `bump-resolve.ps1`, `trx-summary.ps1` and `build-summary.ps1`.**
    - **The extraction script.**
    - **Rev 11, `zip-entries.ps1`:** prints the entry names of `latest.zip` only.

12. **Allowlists and extraction** (rev 10, plus rev 11).
    - **B and B2** may run only:
      - `dotnet build W/Proteus/Proteus.csproj -c Release`;
      - `dotnet test W/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "…"`, optionally with a logger.
    - **The critic** may run the D8 diffs, the re-merge diffs, `git -C W log --format=%h%x20%an%x20%s test..HEAD` and targeted `dotnet test`.
    - **The security reviewer** may run the same, minus the re-merge diffs.
    - **QA** may run `curl` to `127.0.0.1` or `localhost` only.
    - **The re-reviewer** may run only:

      ```
      git -C W show --stat HEAD
      git -C W show HEAD -- docs/contrib-clean-room-log.md
      git -C W show HEAD -- Proteus/Plugin.cs
      git -C W show HEAD -- LICENSE
      git -C W log --format=%h%x20%s <critic-pass-commit>..HEAD
      git -C W show <QA-bump commit> -- Proteus/Plugin.cs
      git -C W diff --stat <critic-pass-commit> HEAD
      ```

      `git -C W show HEAD -- LICENSE` is the one command added in rev 11.
    - **Leak-check forbidden locations:** those from rev 10, plus `scratchpad-agentC` (rev 11).
    - **Self-checks** (rev 10):
      - **Self-check 1:** Reads of the mixed set before `<R>`, in any checkout. The exception is the meddle `Plugin.cs` lines 31–34.
      - **Self-check 2:** content access only, from the window start. Named exceptions (a)–(e). The sanctioned class is as before.

        Rev 11 additions:
        - C's folder joins the counted locations;
        - the B4 `Test-Path` checks of three DLL names in `Proteus/bin/Release/win-x64/` join the sanctioned class;
        - `zip-entries.ps1` joins the sanctioned class;
        - the C1 name-only usage search joins the sanctioned class.
    - **Extraction.** Clean agents, B2 included, are extracted by agent ID. Dirty agents (A, A2, A3, C, D) self-report.

13. **`IpcProvider.cs`** is rewritten whole by B. Rev 11: its line 1 follows `LICENSE` (AGPL until Phase C).

14. **Bot commits** are recorded, not replaced.

15. **The final commit, the bump and the short re-review.**
    - **The final bump:** `max(branch, last merged T) + 1`. QA item 1 is re-run if N is taken on `test`.
    - **`<critic-pass-commit>` is logged.** After it, only these may land: the QA-bump commit, log-only commits, and the final commit.
    - **The final commit** holds the log sections, the bump, and (rev 11) the `LICENSE` effective-date line (D31) if the push day differs from Phase C's date.
    - **The short re-review** runs the seven D12 commands. It checks:
      - the commit list;
      - `--stat` shows only the log, `Plugin.cs` and `LICENSE`;
      - the bumps change only BuildNumber lines;
      - a `LICENSE` change, if any, touches only line 3's date.

16. **`E:\repos\Proteus` takes commands only.**

17. **The guard and merging `test`** (rev 10).
    - **Guard and classifier.** At step 3 the result is only logged.
    - **Bump-only:** the orchestrator merges, with the resolver. **Other:** A3 merges.
    - **After every merge:**
      - the restore check;
      - the proof (rev 11: WIDE-A, plus WIDE-B and the Records filter once Phase B and C exist);
      - blame under the D11 rule;
      - D, if B's lines changed;
      - if the merge touched `Proteus/Interop/**` or the API submodule pointers, `IpcContractTests` must pass.
    - **A failing tip** goes to B or B2: then the critic, D, and QA item 1.

18. **"Fails the same way"** (rev 10). This applies to step-2 tests. New tests must pass, or skip with a reason.

19. **The clean security review and §12** (rev 10, plus rev 11). The critic and the reviewer also confirm contract §4 (threads, availability and errors) row by row.

20. **Who fixes what** (rev 10, plus rev 11).
    - B fixes Phase A code. B2 fixes Phase B code. Either can be replaced by a fresh clean implementer, who gets its own section.
    - The orchestrator fixes tests, docs, the `BuildNumber` line and the Phase C licence, notice and SPDX lines.

21. **Filters.**
    - **WIDE-A:** as in rev 10.
    - **WIDE-B (rev 11):**

      ```
      FullyQualifiedName~IpcContractTests|FullyQualifiedName~ShippedAssembliesTests|FullyQualifiedName~BridgeAvailabilityTests|FullyQualifiedName~HairPortScanScheduleTests|FullyQualifiedName~ModMoveServiceTests|FullyQualifiedName~ToeCapToggleTests|FullyQualifiedName~SupplyChainTests|FullyQualifiedName~Proteus.Tests.TestHarness|FullyQualifiedName~HarnessBuildFlagTests|FullyQualifiedName~HarnessReleaseGuardTests|FullyQualifiedName~HarnessBindingsWiringTests|FullyQualifiedName~DesignBinding|FullyQualifiedName~PlayerSettingsFilterTests|FullyQualifiedName~StutterFixesPureTests|FullyQualifiedName~SidecarDiscoveryCoreTests
      ```

      plus every test class that imports `Penumbra.Api.Enums` or `Glamourer.Api.Enums` (found by name with `git grep -l`), with `&Speed!=Slow`.
    - **Records:**

      ```
      FullyQualifiedName~ContribCleanRoomRecordTests|FullyQualifiedName~ReadmeTermsTests|FullyQualifiedName~PbdCleanRoomRecordTests|FullyQualifiedName~LicencePublicationTests
      ```

22. **The licence** is unchanged through Phases A and B (AC-7). Phase C publishes it (D28).

23. **The slnx** is regenerated at step 4 from §1's list, which is Glamourer.Api, Penumbra.Api, Proteus and XivLiveMesh. Phase B keeps it as it is (D30).

24. **Phase B design (rev 11).**
    - B2 writes Proteus-owned call gates over `IDalamudPluginInterface.GetIpcSubscriber<…>(label)`, using only Dalamud's public API and `docs/ipc-contract.md`. They cover every Penumbra and Glamourer call and event the Phase-A-tip code uses.
    - **Owned types keep the APIs' namespaces** (Plugin-critic 1's suggestion). They are `Penumbra.Api.Enums` and `Glamourer.Api.Enums`, plus any non-enum data types the contract lists.
      - The type names, member names, values, underlying types and `[Flags]` match the API's. This is interface fact only; no doc comments are copied.
      - Files:
        - `Proteus/Interop/Ipc/PenumbraApiTypes.cs`;
        - `Proteus/Interop/Ipc/GlamourerApiTypes.cs`, which carries an Apache-2.0 attribution header and is noticed in D29.
      - The ~60 existing `using` lines don't change.
    - The three ProjectReferences and the Luna comment are removed from `Proteus.csproj`. The build regenerates `packages.lock.json`.
    - **The harness's Glamourer gates** are `GlamourerApplyDesign` and `GlamourerApplyState`, in `Proteus/TestHarness/`. Each exposes `Invoke(...)` with the parameters the adapter already passes. They don't appear in `Interop/` or in `IpcContract.Entries` (ADR 0002).
    - **Events** keep firing on the caller's thread, as before.
    - **Behaviour when Penumbra or Glamourer is absent, not ready, or a call throws** follows contract §4. The bridges keep their existing guards.
    - **Rejected:**
      - keeping Penumbra.Api for its enums only, because the DLL would still ship unlicensed, and Glamourer.Api pulls Luna non-privately;
      - `PrivateAssets` and `ExcludeAssets` tricks, because they're unreliable for copy-local;
      - a new namespace for the owned types, because it means ~60 `using` edits for no gain.

25. **Agent C and the IPC contract (rev 11).**
    - **What C reads:**
      - `Penumbra.Api/**`;
      - `Glamourer/Glamourer.Api/**`;
      - Glamourer's root LICENSE and NOTICE (names only, apart from the LICENSE text);
      - the specific Luna IPC subscriber base classes those API subscribers derive from, for facts only (behaviour when unavailable, on errors and on version mismatch), each file logged;
      - Proteus's own sources at the Phase A tip, which are clean, to list which IPC Proteus uses.
    - **C never reads** the contributors' commits, the mixed set before `<R>`, other `Glamourer/` folders, or the rest of `Luna/`.
    - **C writes `docs/ipc-contract.md`** with these headings:
      - `## 1. Calls Proteus makes`, with a table `| Owner | Label | Kind | Arguments | Returns | Used in |`;
      - `## 2. Events Proteus subscribes to`;
      - `## 3. Types that cross the boundary`, with a table `| Type | Member | Value | Underlying | Flags |`, plus any records and tuples;
      - `## 4. Threading, availability and errors`, as a table of rows A1… that the critic and the reviewer confirm;
      - `## 5. How to check it mechanically`, saying where the labels and generic signatures sit in the API assemblies' metadata;
      - `## 6. Calls only the harness makes`.
    - **Form:**
      - facts only;
      - no fences;
      - no code;
      - an identifier self-check: 0 hits for any Luna private identifier.
    - C writes "Agent C (self-report)".

26. **The Phase B tests (rev 11).** The orchestrator writes them from the contract. They are red until B3.
    - **`IpcContractTests`** reads the API assemblies with Mono.Cecil. `Proteus.Tests.csproj` gets ProjectReferences to the two API projects with `ReferenceOutputAssembly="false"`, and their output DLLs are copied to the test output. The test checks:
      - every `IpcContract.Entries` row (owner, label, kind, argument and return types) against the matching API subscriber's label and generic signature;
      - every owned type against the API type with the same full name: member names, values, underlying type and `[Flags]`;
      - that shipped code reaches Penumbra and Glamourer only through `IpcContract` gates;
      - that the harness gates live only in `TestHarness/`.
    - **`ShippedAssembliesTests`** checks that:
      - `Proteus.csproj` has no Luna, Penumbra.Api or Glamourer.Api reference;
      - `packages.lock.json` has none of them;
      - `typeof(Plugin).Assembly.GetReferencedAssemblies()` has none of them.
    - **`BridgeAvailabilityTests`** works through a substitute `IDalamudPluginInterface` whose gates are absent, not ready or throwing. The bridges must behave as contract §4 says. Event wrappers must fire on the caller's thread.
    - **`HarnessBindingsWiringTests`:** lines 40, 43 and 44 move to the pinned harness gate names, keeping their meaning.
    - Phase B's Cecil reading of the API DLLs is a mechanical test read, and is logged.

27. **The Phase B smoke check and the fallbacks (rev 11).**
    - **B5:**
      1. Bump to `N0` and build Release. Warn other sessions, copy to the dev-load folder, and check the timestamp.
      2. A clean QA-lite agent checks through the harness:
         - `/health` and `/log` show no IPC errors;
         - one `POST /actions/equip` and put-back (Glamourer, enum-typed return);
         - one `POST /actions/mod-settings` hold and put-back with a redraw (Penumbra);
         - one `glamourer-apply` of a design (state-changed events with enums).
      3. Failures go to B2. If a failure can't be fixed from here, the fallback applies.
    - **Fallback A:** Phase B can't pass. Cut `solona-m/contrib-clean-room-a` at the Phase A tip `T_A` (after step 7), and run steps 8–10 on it. The licence stays AGPL.
    - **Fallback B:** Phase C can't pass (for example, an unreadable package licence). Cut at the Phase B tip `T_B`, and run steps 8–10 on it. The licence stays AGPL.
    - Either way, the blocker is reported.

28. **The licence publication (rev 11, Phase C).**
    - **`LICENSE`** becomes the text of `docs/LICENSE-DRAFT.md`, with line 3 changed to `*Version 1.0 — in force from <YYYY-MM-DD>*` (the planned push day; D31). Nothing else changes (ND-4).
    - **`git rm docs/LICENSE-DRAFT.md`.**
    - **`Proteus.csproj`:**
      - adds `<Content Include="..\LICENSE" Link="LICENSE.txt">`, copied to the output, so the zip carries the licence;
      - drops `PackageLicenseExpression`;
      - updates its comments.
    - **XivLiveMesh,** decided by the C1 usage check (ND-2):
      - **Not used elsewhere:**
        - `XivLiveMesh/LICENSE` becomes a byte-identical copy of `LICENSE`;
        - `NOTICE.md` lines 6 and 18 name the Proteus Licence;
        - the csproj comment changes, and `PackageLicenseExpression` is dropped;
        - the SPDX lines in its 7 `.cs` files become `LicenseRef-Proteus`.
      - **Used elsewhere:**
        - XivLiveMesh stays AGPL-3.0-or-later as Solona's separately offered library;
        - its SPDX lines stay AGPL;
        - `THIRD_PARTY_NOTICES.txt` lists it under §7;
        - it is flagged for the lawyer.
    - **SPDX lines elsewhere:** `IpcProvider.cs` line 1, and any B2 file with an SPDX line (except `GlamourerApiTypes.cs`, which is Apache), become `LicenseRef-Proteus`.
    - **Tests:**
      - `ReadmeTermsTests.Licence_stays_AGPL_and_drafts_are_drafts` is replaced by `Licence_is_the_Proteus_Licence_in_force`, which checks:
        - line 1 of `LICENSE` is `# Proteus Licence`;
        - line 3 matches the in-force pattern;
        - there's no "DRAFT" and no uppercase "GNU AFFERO GENERAL PUBLIC LICENSE";
        - the body equals the draft's text minus the status line (embedded in the test);
        - `docs/LICENSE-DRAFT.md` is gone, and there is no root `LICENSE-*` file;
        - `docs/pbd-clean-room.md` exists;
        - no README mentions `LICENSE-DRAFT`.
      - `PbdCleanRoomRecordTests.Header_names_the_spec_not_a_port` and the contrib record test's `IpcProvider` header check now expect the SPDX identifier that `LICENSE` implies.
      - `SupplyChainTests`: only the Luna doc comment and message change ("Luna builds the APIs for the contract test only").
    - **`AGENTS.md`:** the submodules line now says "IPC contracts, checked by tests; not shipped".
    - **Untouched:** README, its translations, For Creators.md and TERMS.md (ND-5). TERMS.md's wording stays true.

29. **The notices (rev 11).** `THIRD_PARTY_NOTICES.txt` keeps the LTC uvmaps, BCnEncoder.NET and bc7enc blocks, and adds:
    - **A block for every shipped package** left in `Proteus/packages.lock.json` after Phase B, Direct or Transitive, minus the build-only DalamudPackager and DotNet.ReproducibleBuilds. Each block's licence and copyright are taken from the package's `.nuspec` and licence file in `%USERPROFILE%\.nuget\packages\<id>\<ver>\`. An unreadable licence is a blocker, and leads to Fallback B.
    - **The Glamourer-derived types file** (`GlamourerApiTypes.cs`), with the Apache-2.0 text, and NOTICE content if Glamourer has a NOTICE file.
    - **An interoperability note:** Penumbra's and Glamourer's IPC labels, signatures and enum names and values are used to talk to them, and no code from them ships. Penumbra.Api carries no licence file; this is flagged for the lawyer.
    - **A game-assets carve-out:** `Proteus/Resources/sphere_d_array.png` and `tile_norm_array.png` are derived from FINAL FANTASY XIV game data (© SQUARE ENIX). They are not covered by the Proteus Licence.
    - **The submodules:**
      - `Glamourer/` (Apache-2.0);
      - `Penumbra.Api/` (no licence file);
      - `Luna/` (AGPL-3.0; used only to build the APIs for the test build, never shipped).

      All three are pointers, not part of Proteus's code.
    - **A workflows note:** some steps in `.github/workflows` were adapted from other open-source projects (per `AI-DECLARATION.md`), and those parts stay under their own licences. Their origins are a `known-issue` task for the owner (ND-6).
    - **XivLiveMesh,** if C1 keeps it AGPL.
    - Dalamud and its bundled libraries are host-provided and not shipped. Whether a plugin may carry this licence is flagged for the lawyer.

30. **The slnx (rev 11).** It keeps the Glamourer.Api and Penumbra.Api entries. The test build still builds them for `IpcContractTests`. The slnx only serves IDEs and the root-finding tests, and doesn't affect the plugin. Rejected: removing the entries, because IDE builds of the test project would then miss them.

31. **The effective date (rev 11).** The date in `LICENSE` is the day `test` is pushed.
    - Phase C writes the planned date.
    - If step 10's push happens on another day, the final commit (step 9) corrects line 3 to the push day before the short re-review.
    - If the push then slips past midnight, the final commit is redone, re-reviewed, and the push follows.

## Requirements
**User prompt (verbatim):** "publish the new licence"

**User's first choice (verbatim):** "Rewrite their code first (Recommended) — Clean-room rewrite of Caraxi's and AzureRavie's surviving code, then swap LICENSE, update SPDX headers and notices, and push to test."

**User's decision on ND-1 (verbatim, superseded):** "Contributor rewrite only — Do the Caraxi/AzureRavie clean-room now and stop; decide about Luna later. Licence stays AGPL for now."

**User's scope change (verbatim, in force):** "Do the Luna work as well. Keep going until you've pushed that and all the other needed changes."

**Feature arguments (verbatim):**
> Clean-room rewrite of the remaining third-party-authored code so Proteus can move from AGPL-3.0 to the Proteus Licence (docs/LICENSE-DRAFT.md), then publish the licence (swap LICENSE, update SPDX headers, NOTICE/THIRD_PARTY_NOTICES, README licence mentions; push to test). Code to replace, found by `git blame -w -M -C` on test: Caraxi (git@caraxian.com) — Proteus/Interop/IpcProvider.cs 137 lines (public IPC: overlay tables assigned to mod options; names/signatures must stay identical), Proteus/Interop/PenumbraBridge.cs 9, Proteus/Plugin.cs 6 (IPC registration; `/proteus refresh silent`), Proteus/Services/SidecarDiscoveryService.cs 6 (multi-select group default + per-option tables), Proteus/Gui/StatusWindow.cs 4 (clickable mod name opening Penumbra; priority DragInt fix), proteus.slnx 9 (regenerate), samples/ExampleMultiGroupOverlayMod/ (remake or delete); AzureRavie — Proteus/Interop/GlamourerBridge.cs 12 lines (cooldowns on equip attempt, Glamourer lock-conflict logging). Follow the same two-stage clean-room method as docs/pbd-clean-room.md / docs/pbd-clean-room-log.md (spec agent reads the old code; a fresh implementer never does; record every participant's reads). The orchestrating session has not read any of this code and must stay clean. Precedent: the PbdFile.cs rewrite (build 1466).

**Derived requirements:**
- R-1: The public IPC stays identical.
- R-2: The small fixes keep their behaviour.
- R-3: Mixed files are cleaned by a dirty agent first, provably.
- R-4 (rev 11): The Luna, Penumbra.Api and Glamourer.Api problem is resolved before the licence is published.
- R-5: Commits are authored as Solona, with no Co-Authored-By and no real name. Sub-agents run on `opus`. Push to `test` only; `main`, tags and deploys are hard stops.
- R-6: Targeted `--filter` runs only, plus the quick suite once before the push.
- R-7: AGENTS.md Rules 3, 4 and 5 apply.
- R-8 (rev 11): Proteus's behaviour towards Penumbra and Glamourer is unchanged, including threads and behaviour when they are unavailable.

## Contract
No routes, schema or persisted fields change.
- **Public IPC:** spec §2–3, pinned by `IpcProviderSpecTests`.
- **Phase A seams:** spec §9.2–§9.5. B's new seams are logged.
- **`IpcProvider.cs` header:**
  - line 1 is the SPDX identifier that `LICENSE` implies (`AGPL-3.0-or-later` until Phase C, then `LicenseRef-Proteus`);
  - line 2 is `// Copyright (c) Solona`;
  - the spec is named within the first 5 lines;
  - there is no "ported from".
- **Phase B (namespace `Proteus.Interop.Ipc`):**
  - `public enum IpcKind { Func, Action, Event }`;
  - `public sealed record IpcContractEntry(string Owner, string Label, IpcKind Kind, Type[] Arguments, Type? Returns)`;
  - `public static class IpcContract { public static IReadOnlyList<IpcContractEntry> Entries { get; } }`.
- **Owned types** live in namespaces `Penumbra.Api.Enums` and `Glamourer.Api.Enums`, plus any data types the contract lists, in `Proteus/Interop/Ipc/PenumbraApiTypes.cs` and `GlamourerApiTypes.cs` (Apache attribution).
- **Harness gates:** `GlamourerApplyDesign` and `GlamourerApplyState` in `Proteus/TestHarness/`.
- **Licence:**
  - `LICENSE` line 1 is `# Proteus Licence`;
  - line 3 is `*Version 1.0 — in force from YYYY-MM-DD*`;
  - SPDX lines read `// SPDX-License-Identifier: LicenseRef-Proteus`.

## Approach
Every step ends with a commit made under D10.

**Phase A** (as in rev 10; current state in the Delta):

0. Done. Commit rev 11 as `docs/contrib-clean-room-plan.md`.
1. Done (`1a2cd3ef`). 1b is done.
2. Done.
3. `<R0>` = `7ce8851b` is committed and `restored-check.ps1` passed. Still to do:
   - blame in both modes (the baseline, D11);
   - the guard, logged only;
   - A appends its removal self-report and deletes its folder;
   - the orchestrator commits the log and deletes the manifest unread.
4. Regenerate the slnx (D23), delete `bin/` and `obj/`, and record "not passing".
5. B: the holes, `IpcProvider.cs` (line 1 AGPL), §6.3 and §6.4, and the named files and seams.
6. Proof: the step-2 filter without the exclusions, and blame under the D11 rule. No markers may remain.
7. D: the first measurement, B's fixes, then a re-measure. Record `T_A` = the commit after step 7.

**Phase B (rev 11):**
- **B1. Agent C writes `docs/ipc-contract.md` (D25).** The orchestrator checks D10, the absence of fences and the self-report, then commits.
- **B2. The orchestrator writes the Phase B tests (D26):**
  - `IpcContractTests`;
  - `ShippedAssembliesTests`;
  - `BridgeAvailabilityTests`;
  - the `HarnessBindingsWiringTests` name lines;
  - the `Proteus.Tests.csproj` API references with `ReferenceOutputAssembly="false"`.

  They are expected red. Commit.
- **B3. Agent B2** (fresh, clean, briefed with the contract, R-1, R-8, D6, D9, D12, D24 and the Contract):
  - writes the owned types, the `Proteus.Interop.Ipc` call gates and `IpcContract`;
  - rewrites the bridges' subscriber use and the harness gates;
  - removes the three ProjectReferences;
  - lets the build update `packages.lock.json`;
  - names every new file.

  The orchestrator extracts B2's record and commits.
- **B4. Proof.**
  - WIDE-B and WIDE-A, judged by D18.
  - `blame-authors.ps1` in both modes (D11), with changes logged.
  - `Test-Path` returns False for `Luna.dll`, `Penumbra.Api.dll` and `Glamourer.Api.dll` in `Proteus/bin/Release/win-x64/`.
  - `zip-entries.ps1` on `latest.zip` lists none of them.
  - `git -C W diff test...HEAD --name-only` lists no licence or notice file (AC-7). Log it.
  - Commit.
- **B5. The in-game smoke check (D27).** Bump to `N0` and commit "Build N0 for smoke". Then build, warn other sessions, copy, and run the QA-lite agent. Fixes go to B2, which also re-runs B4. An unfixable failure leads to Fallback A. Record `T_B`.

**Phase C (rev 11):**
- **C1. XivLiveMesh usage check (ND-2).** Name-only searches:
  - `gh search code "XivLiveMesh" --owner solona-m --json repository,path` (if gh is authenticated);
  - a files-with-matches Grep for `XivLiveMesh` over `E:/repos/*/**/*.csproj`, excluding `E:/repos/Proteus` and `E:/repos/DragAndDropTexturing`.

  Log the repositories and paths found (names only) and the resulting branch of D28.
- **C2. Publication edits (D28, D29).**
  - Read the licences from the nuspec files in the local NuGet cache (allowed).
  - Edit the licence, notice, SPDX, csproj, AGENTS.md and test files.
  - Run the Records filter and WIDE-B.
  - Commit.
- **C3. Proof:**
  - the Records filter;
  - `LicencePublicationTests`;
  - blame (D11);
  - `git -C W diff <T_B> HEAD --name-only` lists exactly the D10 Phase C set.

  Log and commit.

**The gate:**

8. **Merge `test`, then the gate** (as in rev 10, now covering A, B and C).
   1. Merge `T8` (D17).
   2. Run WIDE-A, WIDE-B and the Records filter, then blame in both modes, then D if needed.
   3. The code-critic reviews: §12, contract §4, and the licence and notices against D28 and D29. Log `<critic-pass-commit>`.
   4. The security reviewer reviews.
   5. Re-run D if B's lines changed.
   6. **QA build.** Bump to N, the next free number above `max(branch, T8)`.
   7. **Adversarial QA** (rev 11 scope):
      1. `/health` and `/log` show no IPC errors.
      2. Equip, hold and put-back.
      3. Mod-settings hold and put-back, with a redraw.
      4. Design apply, and binding restore.
      5. A Studio save that reloads a mod.
      6. `GET /sidecar` on a multi-select overlay mod.
      7. The `latest.zip` entry names include `LICENSE.txt` and none of the three DLLs.
      8. UNVERIFIED by design: the cooldown and lock logging, the Mods-tab click and drag, `/proteus refresh silent`, another plugin calling the IPC, the §6.3 grid lookup, and Penumbra or Glamourer being unloaded in game.
   8. Fixes go per D20, with a re-review.
9. **Re-merge if needed, then the final commit.**
   1. Re-merge per D17 if `test` moved, with the critic check.
   2. Run `git -C W diff test...HEAD --name-only`. Log it, and compare it with the D10 sets: Phase A/B files, plus exactly the Phase C files.
   3. Run both self-checks.
   4. Final bump and the `LICENSE` date line (D15, D31).
   5. Final commit.
   6. Short re-review (seven commands).
   7. Final-tip blame.
   8. Copy the final build.
10. **Merge into `test` and push** (as in rev 10).
    1. `git pull`.
    2. The guard.
    3. `git merge --no-ff solona-m/licence-clean-room`. The message is the PR description, and covers:
       - the clean room in two stages;
       - IPC without Luna, Penumbra.Api and Glamourer.Api;
       - the Proteus Licence published, in force from <date>;
       - the blame figures;
       - the re-review's reads;
       - Review & traceability;
       - no Co-Authored-By.
    4. The Records filter.
    5. The blame re-check (D11).
    6. The quick suite.
    7. The workflow check: only `codeql.yml` may trigger.
    8. `git push origin test`.

    If the date in `LICENSE` isn't the push day, go back to step 9 (D31).
11. **Final report:**
    - decisions, including the scope change and ND-2 to ND-6;
    - gaps in the proof (the weaker AC-1, baselines);
    - D's numbers;
    - the trivial-class changes;
    - the C1 result;
    - the step-10 re-check;
    - UNVERIFIED items;
    - `known-issue` tasks: the workflows' origins, and any QA deferrals;
    - owner follow-ups: the lawyer's review of the record, the trivial class, Penumbra.Api's missing licence, the Dalamud host question and XivLiveMesh; merging `test` into `main`; the release tag.

## Files
- EDIT `docs/contrib-clean-room-plan.md` (rev 11).
- EDIT `docs/contrib-clean-room-spec.md` (done at 1b).
- NEW `docs/ipc-contract.md`: Agent C's contract.
- NEW/EDIT `docs/contrib-clean-room-log.md`. Its opening lines: Phases A and B made no licence change; Phase C publishes the Proteus Licence; the method extends `docs/pbd-clean-room.md` to mixed files; the tripwire fired at step 1 (rev-8 re-plan); the user's scope change (rev 11). It contains:
  - every Phase A record (rev 10);
  - C's and B2's sections;
  - B4's DLL and zip checks;
  - the B5 smoke report;
  - the C1 usage result;
  - the nuspec licence sources;
  - the AC-7 file list.
- DELETE then NEW `Proteus/Interop/IpcProvider.cs` (B; line 1 swapped in Phase C).
- EDIT the mixed-set files (A's removal, then B; the bridges again by B2; `Plugin.cs` bumps).
- DELETE then NEW `proteus.slnx`.
- DELETE `samples/ExampleMultiGroupOverlayMod/**`.
- NEW `Proteus/Interop/Ipc/*.cs` (B2): `IpcContract`, the call gates, `PenumbraApiTypes.cs`, `GlamourerApiTypes.cs` (Apache header).
- NEW `Proteus/TestHarness/GlamourerApplyDesign.cs` and `GlamourerApplyState.cs` (B2).
- EDIT `Proteus/TestHarness/GameHarnessAdapter.cs` (B2): the gates.
- EDIT `Proteus/Proteus.csproj`:
  - B2 removes the three references and the Luna comment;
  - Phase C adds the `LICENSE` content item, drops `PackageLicenseExpression` and updates the comments.
- EDIT `Proteus/packages.lock.json` (regenerated by the build).
- EDIT `Proteus/Localization/*` (B, only if new text).
- NEW any file B or B2 names.
- NEW `Proteus.Tests/IpcProviderSpecTests.cs`, `ContribSpecTests.cs`, `ContribCleanRoomRecordTests.cs`, `IpcContractTests.cs`, `ShippedAssembliesTests.cs`, `BridgeAvailabilityTests.cs` and `LicencePublicationTests.cs`.
- EDIT:
  - `Proteus.Tests/Proteus.Tests.csproj`: API references with `ReferenceOutputAssembly="false"`;
  - `HarnessBindingsWiringTests.cs`: lines 40, 43 and 44;
  - `ReadmeTermsTests.cs`: the licence test replaced;
  - `PbdCleanRoomRecordTests.cs`: the header test follows `LICENSE`;
  - `SupplyChainTests.cs`: the Luna doc and message only.
- EDIT `LICENSE`: the Proteus Licence, in force.
- DELETE `docs/LICENSE-DRAFT.md`.
- EDIT `XivLiveMesh/LICENSE`, `XivLiveMesh/NOTICE.md`, `XivLiveMesh/XivLiveMesh.csproj` and `XivLiveMesh/*.cs` (SPDX lines only), per C1.
- EDIT `THIRD_PARTY_NOTICES.txt` (D29).
- EDIT `AGENTS.md`: the submodules line.
- MERGE commits in `W`.
- **Unchanged:**
  - `StutterFixesWiringTests`;
  - For Creators.md, README and its translations, TERMS.md;
  - the historical `docs/pbd-*` records;
  - every trivial-class file outside the mixed set (apart from Phase C's SPDX lines in XivLiveMesh);
  - the submodules.
- **Not committed:** scripts, `.trx` and `.out` files, manifests, checkpoint files, and the A, C, D and handover folders.

## Out of Scope
- `main`, tags, releases and deploys (hard stops).
- The lawyer's review (an owner follow-up).
- Asking anyone for consent.
- New harness routes.
- README licence lines (ND-5).
- Auditing the workflows' origins (a `known-issue` task).
- Re-checking where other files came from beyond D29's list.
- Removing the submodules.
- Distributing `Proteus.Refit`.
- Remaking the sample.
- Editing the historical records.
- Removing trivial coincidental lines outside the mixed set.
- The Dalamud host-licence question (lawyer).

## Edge Cases
- **EDGE-1 to EDGE-39:** as in rev 10, with these rev-11 additions:
  - EDGE-6's after-merge proof includes WIDE-B and the Records filter once those phases exist;
  - EDGE-26's re-check applies after Phase C too;
  - EDGE-37's failing tip goes to B or B2 by phase.
- EDGE-contrib-clean-room-40: Enum values crossing plugin load contexts. Proteus-owned types in the same namespaces: Dalamud converts across contexts as it did for Proteus's copy of Penumbra.Api. The contract test pins names, values, underlying types and `[Flags]`. B5 and step 8's QA confirm in game.
- EDGE-contrib-clean-room-41: A submodule bump renames or re-versions a label. `IpcContractTests` fails at test time, not in game.
- EDGE-contrib-clean-room-42: Penumbra or Glamourer is absent, not ready, or throws. The bridges behave as contract §4 says (`BridgeAvailabilityTests`). Unloading them in game is UNVERIFIED.
- EDGE-contrib-clean-room-43: Events fire on the caller's thread and in bursts. The wrappers subscribe directly, with no marshalling change (`BridgeAvailabilityTests`, QA).
- EDGE-contrib-clean-room-44: ADR 0002. The harness gates exist only in `TestHarness/`, which is compiled out of CI (`IpcContractTests`, `HarnessBindingsWiringTests`, `HarnessBuildFlagTests`).
- EDGE-contrib-clean-room-45: Luna reaches the output by some other route. B4 checks the plugin output and the zip, never the test output.
- EDGE-contrib-clean-room-46: The B5 smoke check fails and can't be fixed. Fallback A.
- EDGE-contrib-clean-room-47: A shipped package's licence can't be read. Fallback B.
- EDGE-contrib-clean-room-48: The C1 usage check finds another plugin using XivLiveMesh. It stays AGPL, is noticed under §7, keeps its SPDX lines, and is flagged for the lawyer. The no-AGPL-claim test excludes `XivLiveMesh/`.
- EDGE-contrib-clean-room-49: The push day differs from Phase C's date. Step 9 corrects `LICENSE` line 3, and the short re-review covers it. If the push slips past midnight, step 9 is redone (D31).
- EDGE-contrib-clean-room-50: Historical records and third-party listings still say AGPL: `docs/pbd-*`, `docs/contrib-clean-room-*`, `THIRD_PARTY_NOTICES.txt`, the `XivLiveMesh/NOTICE.md` research-references line, and `LICENSE`'s preamble about earlier versions. The no-AGPL-claim test excludes them by explicit path, and the `LICENSE` check looks for the AGPL's uppercase title only.
- EDGE-contrib-clean-room-51: A root `LICENSE-*` file would show on GitHub as a second licence. None may exist.
- EDGE-contrib-clean-room-52: Game-derived assets ship in the plugin (`Resources/*.png` slices). They are carved out in the notices, not covered by the Proteus Licence.

## Test Strategy
All new tests carry `[Trait("Plan","<ID>")]`. Record tests only read files.

- **AC-1 to AC-6, and EDGE-1 to EDGE-39:** as in rev 10, with these rev-11 additions:
  - `Log_lists_every_participant_and_the_clean_side_kept_clear` also requires the sections "Agent C (self-report)" and "Agent B2", Planner (rev 2) to (rev 11), and C's folder in the forbidden list;
  - `Spec_has_every_required_section_and_no_code` gains a sibling, `Contract_has_every_required_section_and_no_code`: the 6 headings, no fences, and the §1 and §3 table headers (with Underlying and Flags);
  - `IpcProvider_header_is_pinned` follows `LICENSE`;
  - `Log_reports_no_contributor_lines` adds the Phase B tip checkpoint.
- **[AC-contrib-clean-room-7]:**
  - the three guard tests stay unchanged and green through B4;
  - `ContribCleanRoomRecordTests.Log_states_no_licence_change_before_phase_C`: the log has the B4 file list, and it names no licence or notice file.
- **[AC-contrib-clean-room-8] [EDGE-26] [EDGE-36] [EDGE-38] [EDGE-49]:**
  - the step-10 sequence;
  - `Log_records_merges_and_the_final_bump`, which now also checks the seven re-reviewer commands and the `LICENSE` date record.
- **[AC-contrib-clean-room-9] [EDGE-45]:**
  - `ShippedAssembliesTests.Plugin_references_no_Luna_or_API_assembly`;
  - the B4 `Test-Path` and zip-entry checks, logged;
  - QA item 7.
- **[AC-contrib-clean-room-10] [EDGE-40] [EDGE-41] [EDGE-42] [EDGE-43] [EDGE-44]:**
  - `IpcContractTests`: `Every_call_gate_matches_the_API`, `Every_owned_type_matches_the_API` (names, values, underlying type, `[Flags]`), `Shipped_code_uses_only_IpcContract_gates`, `Harness_gates_live_only_in_TestHarness`;
  - `BridgeAvailabilityTests`: `Bridges_follow_contract_section_4_when_unavailable`, `Event_wrappers_fire_on_the_callers_thread`;
  - `HarnessBindingsWiringTests` (updated) and `HarnessBuildFlagTests`;
  - the B5 smoke report and step 8's QA items 1–5, logged.
- **[AC-contrib-clean-room-11] [EDGE-49] [EDGE-51]:**
  - `ReadmeTermsTests.Licence_is_the_Proteus_Licence_in_force`;
  - `LicencePublicationTests.Package_carries_the_licence`: the `..\LICENSE` content item;
  - QA item 7: `LICENSE.txt` is in the zip.
- **[AC-contrib-clean-room-12] [EDGE-48] [EDGE-50]:**
  - `LicencePublicationTests.Spdx_headers_follow_the_licence` (excepting `GlamourerApiTypes.cs`, and `XivLiveMesh/` if C1 kept it AGPL);
  - `LicencePublicationTests.XivLiveMesh_licence_follows_the_usage_check`: either byte-identical to `LICENSE`, or AGPL with a notice entry, according to the C1 record in the log;
  - `LicencePublicationTests.Projects_drop_PackageLicenseExpression`;
  - `LicencePublicationTests.No_file_claims_AGPL_for_Proteus_code` (with the explicit exclusion list);
  - `PbdCleanRoomRecordTests.Header_names_the_spec_not_a_port` (updated).
- **[AC-contrib-clean-room-13] [EDGE-47] [EDGE-52]:**
  - `LicencePublicationTests.Every_shipped_package_has_a_notice`: every non-build-only package in `Proteus/packages.lock.json` has a block naming its id and licence;
  - `LicencePublicationTests.Existing_notices_are_kept`;
  - `LicencePublicationTests.Glamourer_types_are_noticed`;
  - `LicencePublicationTests.Game_assets_are_carved_out`: both `Resources` slices named;
  - `LicencePublicationTests.Submodules_and_workflows_are_listed`.
- **[EDGE-46]:** if Fallback A or B happens, the log records the blocker and the branch cut. The final report names it.

## Environment & Preconditions
- `PROTEUS_GAME`, `PROTEUS_MODS` and `PROTEUS_MODPACKS` are set.
- For QA and B5: the game is running and the harness is on, with a multi-select overlay mod and a Glamourer design available.
- The submodules are initialised. The test build needs them.
- The build is broken from `<R0>` until step 5. Phase B tests are red from B2 until B3.
- The dev-load folder is written at B5, step 8, step 9 and step 10's test runs. Warn other sessions first.
- The NuGet cache holds the shipped packages (`%USERPROFILE%\.nuget\packages`).
- `gh` authentication is optional for C1. Without it, only the local search runs, and that is logged.
- `-C -C` blame is slow. Run it in the background.

**Reads for the log** (Planner revisions 1–10, Plan-critics 1–6, 8 and 9, and the Planning orchestrator) are exactly as listed in rev 10's Environment & Preconditions. Those lists are carried into the log unchanged, verbatim, with the L-4 disclosures. Additions:
- **Planner (rev 11):** no tool calls at all (no Read, no shell or git commands, no searches, no URLs), and the same automatic loads.
- Any plan-critic pass on rev 11 adds its own section.

## NEEDS_DECISION
None. The user decided ND-1, now superseded by the scope change. The orchestrator decided ND-2 to ND-6, ND-3, the self-check option, and the rev-8 to rev-11 amendments (N-6). My judgment calls in this revision:
- owned types stay in the APIs' namespaces (D24);
- C may read the named Luna base classes for facts only (D25);
- the slnx keeps the API entries (D30);
- B5 runs before Phase C (D27);
- B2's lines get no similarity check (D7).

## Risks
- **The scope is large: 13 ACs, three phases.** It isn't split, because the user asked for everything in one run. Each phase has its own proof and fallback.
- **Leaks to the clean side.** The rev-10 routes, plus two from Phase B: C's API and Luna reads reaching B2 (C writes facts only; B2 never reads sources), and the Cecil read of the API DLLs (mechanical, logged).
- **The Interop rewrite (Sensitive Area).** A wrong label or type fails only at runtime. The mitigations:
  - the Cecil contract test against the submodules;
  - the bridge-availability tests;
  - the B5 smoke check;
  - the step-8 QA;
  - ADR 0002 guards.
- **Owned types in the APIs' namespaces.** A future change that re-adds a reference to Penumbra.Api or Glamourer.Api would collide with them. `ShippedAssembliesTests` blocks that reference.
- **The licence's legal footing.** These are left to the owner's lawyer:
  - the trivial-class judgment;
  - Penumbra.Api's missing licence (interface facts only);
  - the Glamourer types (Apache, noticed);
  - the Dalamud host question;
  - XivLiveMesh's dual status if C1 finds another user;
  - the game-asset carve-out;
  - the workflows' origins.
- **The `87ae2e3d` scope.** B rebuilds 32 lines of Solona's own code from a description. In game it is UNVERIFIED.
- **AC-1 on the old code rests mostly on NewSeam tests.** This is reported.
- **Merging `test` mid-branch.** The rev-10 mitigations hold, plus the contract test whenever a merge touches Interop or the submodule pointers.
- **Process departures.** B and B2 write the production code; a clean sub-agent runs the security review. Both are noted in Review & traceability.
- **Plugin reloads** at B5 and step 8–10. Other sessions are warned first.
- **Left to the owner:**
  - the UNVERIFIED in-game items;
  - the lawyer's review;
  - merging `test` into `main`;
  - the release tag.

Relevant paths:
- E:\orca\workspaces\Proteus\licence-clean-room\docs\contrib-clean-room-spec.md
- E:\orca\workspaces\Proteus\licence-clean-room\docs\contrib-clean-room-plan.md
- E:\orca\workspaces\Proteus\licence-clean-room\docs\LICENSE-DRAFT.md
- E:\orca\workspaces\Proteus\licence-clean-room\THIRD_PARTY_NOTICES.txt
- E:\orca\workspaces\Proteus\licence-clean-room\XivLiveMesh\NOTICE.md
- E:\orca\workspaces\Proteus\licence-clean-room\XivLiveMesh\XivLiveMesh.csproj
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus\Proteus.csproj
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus\packages.lock.json
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\ReadmeTermsTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\PbdCleanRoomRecordTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\SupplyChainTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus.Tests\HarnessBindingsWiringTests.cs
- E:\orca\workspaces\Proteus\licence-clean-room\Proteus\TestHarness\GameHarnessAdapter.cs
- E:\orca\workspaces\Proteus\licence-clean-room\docs\adr\0002-qa-harness-compile-out-and-threat-model.md