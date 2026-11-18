# Clean-room log: Caraxi's and AzureRavie's code

This feature has three phases:
- **Phase A** replaces every line Caraxi and AzureRavie wrote with code written by an agent who never saw theirs.
- **Phase B** makes Proteus talk to Penumbra and Glamourer through its own IPC call gates, so Luna (AGPL-3.0), Penumbra.Api
  and Glamourer.Api are no longer shipped.
- **Phase C** publishes the Proteus Licence.

Phases A and B made no licence change. The original scope was Phase A alone; the user brought B and C back mid-run (see
"Orchestrator (Phase B)"). The method extends the PbdFile precedent (`docs/pbd-clean-room.md`,
`docs/pbd-clean-room-log.md`) from one whole file to code mixed into files Solona otherwise wrote. The plan is
`docs/contrib-clean-room-plan.md` (revision 12 with amendments 12a–12c; earlier revisions are in its git history). The
spec is `docs/contrib-clean-room-spec.md`, and the IPC contract is `docs/ipc-contract.md`.

## Method

- **Agent A** (dirty: it may read the contributors' code and history) writes the spec. Once the orchestrator's tests from
  that spec pass on the old code, A removes the contributors' lines: direct, derived (a transitive closure), carried over,
  whole files. It also restores Solona's own replaced lines word for word, and leaves `// CLEAN-ROOM HOLE: spec §n` markers
  in C# files.
- **Agent B** (clean, fresh, started after the removal) fills the holes and rewrites `Proteus/Interop/IpcProvider.cs`
  from the spec alone.
- **Agent D** (dirty, fresh) compares B's lines with the whole removed set and reports only numbers and categories.
- **The orchestrator, B, the critics, the security reviewer and QA** never see the removed code. Before the removal,
  test output reaches the orchestrator only as names, outcomes, assertion types and hashes.
- **The proof:**
  - blame counts at each step (count-only script);
  - a check that the removal adds only markers and verified restored lines;
  - the spec tests pass on the old code, fail after the removal, and pass again on the clean code;
  - Agent D's similarity numbers.
- **The clean agents' reads** are extracted from their transcripts. The dirty agents report their own reads.

**What is not counted:** `dotnet` build and test output, results of read-type tool calls, and the contents of allowed
files.

## Agent A (self-report)

Folder: <session-scratch>\scratchpad-agentA\
Opened:
- W = E:/orca/workspaces/Proteus/licence-clean-room. Read tool:
- W/docs/contrib-clean-room-plan.md (lines 1–545, then 546–993)
- W/AGENTS.md (whole)
- W/Proteus/Services/CompositorService.Carriers.cs (lines 470–529, 345–404)
- W/Proteus/Services/SidecarDiscoveryService.cs (lines 1–110, 440–669)
- W/Proteus/Interop/GlamourerBridge.cs (lines 150–469, 1–150)
- W/Proteus/Gui/StatusWindow.cs (lines 1340–1419, 1290–1339)
- W/Proteus/Services/CompositorService.cs (lines 725–745)
- W/Proteus/Plugin.cs (lines 170–194, 500–658)
- W/Proteus/Interop/IpcProvider.cs (lines 24–43, 112–119)
- W/Proteus/Interop/PenumbraBridge.cs (lines 1–140, 820–869, 139–228)
- W/Proteus/Gui/ModsTab.cs (lines 1–200)
- W/Proteus.Tests/StutterFixesWiringTests.cs (lines 30–129, 129–153)
- W/Proteus.Tests/HarnessBindingsWiringTests.cs (lines 36–91)
- W/docs/contrib-clean-room-log.md (whole, before appending this section)
- <folder>/cc_table.md (whole; my own generated table)
- Grep tool (content mode unless noted; all under W, none in a submodule):
  - `ATTEMPT|var equipped = Set|_lastRingInjectTick = |_lastGlassesInjectTick = ` in W/Proteus
  - `_lastRingInjectTick|SetAccessoryOnFramework\(` in W/Proteus
  - `OpenToMod|##pri_|IsItemDeactivatedAfterEdit|ImRaii.PushColor\(ImGuiCol.Text, ImGui.GetColorU32\(ImGuiCol.TextDisabled\)` in W/Proteus
  - `LastDiscovered\b.*[={]|public void TriggerRecomposite\(|public .*ResolveActiveOverlays\(|public void SaveMetadata\(|class OverlayOptionGroup|class OverlayOption\b|PenumbraGroupName|public List<OverlayOptionGroup>|class ProteusMetadata|class ColorTableRowPreset` in W/Proteus, glob `*.cs`
  - `_carrierCooldowns|class CarrierCooldowns|public void Charge|public long LeftMs` in W/Proteus (-A 3)
  - `record ContentIndex|enum ContentIndexState|DefaultContentRow =|ContentIndex ScanContentIndex\(|ResolveContentTexture\(OverlayEntry|public static .* ParseMtrlBytes\(|public static .*GetMtrlInfo\(|public static bool\? IsUncompressed|LoadTexAsRgba\(string` in W/Proteus (-A 2)
  - `ParseMtrlBytes|record struct MtrlSlots|struct MtrlSlots|MtrlSlots\(` in W/Proteus/Services/TextureLoader.cs (-A 3)
  - `record (struct )?MtrlTexturePaths` in W/Proteus (-A 6)
  - `SetGearItem|SetGlasses|SetAccessory|SetRing|ReapplyPlayerState` in W/Proteus
  - `ContainsAny` in W/Proteus
  - `private void OnCommand|var a = |string a =` in W/Proteus/Plugin.cs (-A 3)
  - `public bool SaveMetadata|public .*LastDiscovered|public void TriggerRecomposite|public void RefreshAndRecomposite|public void Refresh\(\)` in W/Proteus (-A 3)
  - `ResolveActiveOverlays|new ResolvedOverlay|ResolvedContent\(|OpenToMod|ReapplyPlayerState|SetGlasses|SetAccessory|InvalidKey|GlamourerSignals|BodyShapeSignature|CarrierCooldowns|IpcProvider|ModsTab|##pri_|FollowsHairColor|NoColorTable|ContentIndexFor|OptionGroup\b|GroupOrder|silent|refreshArgs|ContainsAny|OpenMainWindow|ownCarrierWriteDepth|WithinOwnReapplyEcho|_carrierCooldowns|SetGlassesOnFramework|SetAccessoryOnFramework|sinceInject|ResolvedContent` in W/Proteus.Tests (count mode)
  - `ResolveActiveOverlays|OpenToMod|ReapplyPlayerState|SetGlasses\b|SetAccessory\b|InvalidKey|GlamourerSignals|BodyShapeSignature|CarrierCooldowns|IpcProvider|ModsTab|FollowsHairColor|ContentIndex|silent|ContainsAny|OpenMainWindow|ownCarrierWriteDepth|WithinOwnReapplyEcho|_carrierCooldowns|SetGlassesOnFramework|SetAccessoryOnFramework|sinceInject|GlassesInjectCooldown|RingInjectCooldown|GlamourerBridge\.cs|Carriers\.cs|StatusWindow\.cs|Plugin\.cs|SidecarDiscoveryService\.cs|PenumbraBridge\.cs|CompositorService\.cs"` in W/Proteus.Tests (output saved by the harness to a tool-results file, then filtered by a shell command below)
  - `ReapplyPlayerState\(|"public bool SetGlasses\(|"public bool SetAccessory\(|OnStateChanged\(|ContentIndexFor\(|DrawModsTab\(|ReconcileInvisibleGlasses\(|ReconcileOneCarrier\(|BodyShapeSignature|OnCommand\(|private void DisposeServices\(|ResolveActiveOverlays\(OverlayEntry|"public Plugin\(|record ResolvedOverlay|record ResolvedContent|OpenToMod` in W/Proteus.Tests
  - `ResolvedOverlay|\.OptionGroup\b|\.Option\b|GroupOrder|ResolveActiveContent|ResolvedContent` in W/Proteus.Tests (count mode)
  - `public void \w+` in W/Proteus.Tests/SidecarDiscoveryTests.cs
  - `ResolveActiveOverlays|ResolvedOverlay|ResolveActiveContent|ResolvedContent` in W/Proteus.Tests
  - `new CompositorService\(|RuntimeHelpers.GetUninitializedObject|FormatterServices|new PenumbraBridge\(|new GlamourerBridge\(|new SidecarDiscoveryService\(|InternalsVisibleTo|LastDiscovered` in W, glob `{Proteus.Tests/**/*.cs,Proteus/*.csproj,Proteus/Properties/*.cs,Proteus/AssemblyInfo.cs}` (first 40 lines)
  - `GlamourerBridge|PenumbraBridge\(|DispatchProxy|GetIpcSubscriber|GetIpcProvider` in W/Proteus.Tests (first 30 lines)
  - `IpcOverlayDetail|ipcProvider` in W, glob `!{Glamourer,Luna,Penumbra.Api}/**`
  - `disk|master switch is|no index|cannot be narrowed|resolved or decoded|holds a|full redraw|equip per slot|foreign equip|player change|synchronously inside|carrier write, ignored|own group ordinal|suppresses lower|belong to no group|lock on this state` in W/docs/contrib-clean-room-spec.md
- Not opened: `docs/pbd-clean-room.md` and `docs/pbd-clean-room-log.md` (the precedent was taken from the plan's description of it).
Shell commands:
- `mkdir -p "<session-scratch>/scratchpad-agentA"`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room shortlog -sne test`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room log test --format="%H %P | %an <%ae> | %ad | %s" --date=short --author=Caraxi --author=AzureRavie`
- `python --version`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room ls-tree -r test --name-only | wc -l`
- `python <session-scratch>/scratchpad-agentA/blame_all.py test "-w -M -C" <session-scratch>/scratchpad-agentA/blame_C.tsv`
- `python <session-scratch>/scratchpad-agentA/blame_all.py test "-w -M -C -C" <session-scratch>/scratchpad-agentA/blame_CC.tsv` (background)
- `cut -f1,3 <session-scratch>/scratchpad-agentA/blame_C.tsv | sort | uniq -c`
- `grep -v "^samples/" <session-scratch>/scratchpad-agentA/blame_C.tsv | grep -v "^Proteus/Interop/IpcProvider.cs"`
- `grep -a -v "^samples/" <session-scratch>/scratchpad-agentA/blame_C.tsv | grep -a -v "^Proteus/Interop/IpcProvider.cs"`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room log test --format="== %h %s" --numstat -M --author=Caraxi --author=AzureRavie`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show --format="== %h %s" -M c1191abd e5704ca4 08cd11b2 9a38b682 78bd004a 41683f4e -- Proteus`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show --format="== %h %s" -M 3f9c1498 aeb00b3b 3b4cdc23 6207f78b`
- `python <session-scratch>/scratchpad-agentA/closure.py test <session-scratch>/scratchpad-agentA/closure_r1.tsv <session-scratch>/scratchpad-agentA/events_r1.tsv`
- `python <session-scratch>/scratchpad-agentA/annotate.py test <session-scratch>/scratchpad-agentA/closure_r1.tsv samples` (twice: once failed on console encoding, once after the fix)
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show --format="== %h %ae %ad %s" --date=short -U0 -w -M 87ae2e3d -- Proteus/Gui/StatusWindow.cs Proteus/Interop/GlamourerBridge.cs` (output saved by the harness to a tool-results file; only its first 2 KB were shown)
- `cat <session-scratch>/tasks/b9sr1awyl.output`
- `cut -f1,3 <session-scratch>/scratchpad-agentA/blame_CC.tsv | sort | uniq -c`
- `grep -a -v -E "^(samples/|proteus.slnx|Proteus/Interop/(IpcProvider|GlamourerBridge|PenumbraBridge)\.cs|Proteus/Plugin\.cs|Proteus/Services/SidecarDiscoveryService\.cs|Proteus/Gui/StatusWindow\.cs)" <session-scratch>/scratchpad-agentA/blame_CC.tsv | cut -f4,5,6,7 | sort | uniq -c | sort -rn`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room blame -w -M -C -L 300,345 test -- Proteus/Services/CompositorService.Carriers.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "equipped|_lastRingInjectTick|_lastGlassesInjectTick = |attempt|ATTEMPT|sinceInject > GlassesInjectCooldownMs" 87ae2e3d -- Proteus/Services/CompositorService.Carriers.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "equipped|_lastRingInjectTick|_lastGlassesInjectTick = |attempt|ATTEMPT|sinceInject > GlassesInjectCooldownMs|locked" 87ae2e3d^ -- Proteus/Services/CompositorService.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show 87ae2e3d:Proteus/Services/CompositorService.Carriers.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "PushColor\(ImGuiCol.Text, ImGui.GetColorU32\(ImGuiCol.TextDisabled\), !active|Selectable\(\\$\"\{entry.ModName\}|OpenToMod|##pri_|IsItemDeactivatedAfterEdit|PriorityOverride = pri" 87ae2e3d -- Proteus/Gui/ModsTab.cs Proteus/Gui/StatusWindow.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show 87ae2e3d:Proteus/Gui/ModsTab.cs`
- `python <session-scratch>/scratchpad-agentA/closure.py 87ae2e3d^ <session-scratch>/scratchpad-agentA/closure_pre_split.tsv <session-scratch>/scratchpad-agentA/events_pre_split.tsv`
- `python <session-scratch>/scratchpad-agentA/annotate.py 87ae2e3d^ <session-scratch>/scratchpad-agentA/closure_pre_split.tsv samples`
- `python <session-scratch>/scratchpad-agentA/closure.py test <session-scratch>/scratchpad-agentA/closure_r2.tsv <session-scratch>/scratchpad-agentA/events_r2.tsv <session-scratch>/scratchpad-agentA/seeds_r2.tsv`
- `cat <session-scratch>/scratchpad-agentA/events_r2.tsv`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show --stat --format="== %h %ad %s" --date=short 6c20041f efcc50fa c3d37d7d dfaf1d75 2e63f830 94c04ab0 a3842daf 897624ce d5215199`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show --format="== %h" -w 6c20041f -- Proteus/Interop/GlamourerSignals.cs Proteus/Interop/GlamourerBridge.cs`
- `python <session-scratch>/scratchpad-agentA/annotate.py test <session-scratch>/scratchpad-agentA/closure_r2.tsv samples`
- `python <session-scratch>/scratchpad-agentA/removal_set.py` (twice: once failed on a binary row, once after the fix)
- `python <session-scratch>/scratchpad-agentA/restore_check.py`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show test:proteus.slnx`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room log test --format="%h %ae %ad %s" --date=short -- proteus.slnx`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room ls-files -- "*.csproj"`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room blame -w -M -C -L 70,76 test -- Proteus/Plugin.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff --stat test -- Proteus/Plugin.cs Proteus/Gui/StatusWindow.cs Proteus/Interop/GlamourerBridge.cs`
- `grep -c $'\r' E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Plugin.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room log --oneline HEAD..test`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room status --short`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff --stat HEAD test -- Proteus`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room merge-base HEAD test`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff HEAD test -- Proteus/Plugin.cs`
- `python <session-scratch>/scratchpad-agentA/blame_all.py HEAD "-w -M -C -C" <session-scratch>/scratchpad-agentA/blame_CC_head.tsv` (background)
- `python <session-scratch>/scratchpad-agentA/blame_all.py HEAD "-w -M -C" <session-scratch>/scratchpad-agentA/blame_C_head.tsv`
- `python <session-scratch>/scratchpad-agentA/closure.py HEAD <session-scratch>/scratchpad-agentA/closure_r2_head.tsv <session-scratch>/scratchpad-agentA/events_r2_head.tsv <session-scratch>/scratchpad-agentA/seeds_r2.tsv`
- `grep -v -i "silent" "<claude-projects>/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/toolu_01NZrmd4nVgV39R7PdhS7sxe.txt"`
- `python <session-scratch>/scratchpad-agentA/edge5.py test` (output saved by the harness to a tool-results file)
- `awk -F'\t' '$7=="True" && $4 ~ /^Proteus\// && length($3) >= 12' "<claude-projects>/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/beewitjih.txt"`
- `grep -n -B8 -A4 "TextureLoader.ParseMtrlBytes" E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ContentImportTests.cs`
- `grep -n -B12 -A3 "LoadTexAsRgba(" E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/RowHoverWiringTests.cs`
- `grep -n -B10 -A3 "log.Warning(" E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ContentImcEditsTests.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/IssueSweepDWiringTests.cs`
- `awk -F'\t' '$7=="True" && $4 ~ /^Proteus\// && $4 !~ /IpcProvider/ && length($3) < 12' "<claude-projects>/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/beewitjih.txt"` (output saved by the harness to a tool-results file; only its first 2 KB were shown)
- `cat <session-scratch>/tasks/bp45niosx.output`
- `grep -n -A12 "private void DisposeServices()" E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ColorsetSaveOnReleaseTests.cs`
- `grep -n "new ResolvedOverlay\|new(.*Descriptor\|new ResolvedContent\|GroupOrder" E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/FaceUvDoublingTests.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/WomensArtOnMenTests.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ShellMensLayoutTests.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ContentPieceSelectionTests.cs`
- `sed -n 26,32p E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/FaceUvDoublingTests.cs`
- `grep -a -E "^Proteus.Tests/(ModCreationTests|OnionImportTests|ContentImportTests.RingImcGroups|LocalizationTests|PbdFileTests)\.cs" <session-scratch>/scratchpad-agentA/blame_CC.tsv`
- `grep -a -v -E "^(samples/|proteus.slnx)" <session-scratch>/scratchpad-agentA/blame_CC.tsv | cut -f7 | sort | uniq -c | sort -rn`
- `diff <(grep -a -o -E "^[^	]+	[0-9]+	[^	]+" <session-scratch>/scratchpad-agentA/blame_CC.tsv | sort) <(grep -a -o -E "^[^	]+	[0-9]+	[^	]+" <session-scratch>/scratchpad-agentA/blame_CC_head.tsv | sort)` (the bracket expressions hold a literal tab)
- `grep -a -E "^Proteus/(Services/CompositorService.Carriers|Gui/ModsTab|Interop/ShellCoverageFade)\.cs" <session-scratch>/scratchpad-agentA/blame_CC.tsv`
- `python <session-scratch>/scratchpad-agentA/cc_only.py`
- `python <session-scratch>/scratchpad-agentA/cc_table.py`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room rev-parse test HEAD`
- `python <session-scratch>/scratchpad-agentA/idcheck.py` (run four times while rewording the spec; the last two runs printed TOTAL 0)
- `grep -n -E "^## |^\`\`\`|CLEAN-ROOM" E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-spec.md`
- `grep -n -o -E ".{0,80}(disk|master switch is|holds a|full redraw|own group ordinal|suppresses lower|belong to no group|resolved or decoded).{0,80}" E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-spec.md`
- `grep -n -E "public void |public async Task " E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ContentPieceSelectionTests.cs`
- `sed -n 583,630p E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ContentPieceSelectionTests.cs`
- `wc -l E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room status --short` (again, after writing the spec)
- Files written with the Write and Edit tools: the scripts `blame_all.py`, `closure.py`, `annotate.py`, `removal_set.py`, `restore_check.py`, `edge5.py`, `cc_only.py`, `cc_table.py`, `idcheck.py` and the seed list `seeds_r2.tsv` in the folder; in W only `docs/contrib-clean-room-spec.md` and this section of `docs/contrib-clean-room-log.md`. No `dotnet` command was run in stage 1.
- Forbidden locations: none opened. No file under `Luna/`, `Glamourer/` or `Penumbra.Api/` was read or searched (blame and `ls-tree` skip gitlinks), no other checkout, no web, no memory topic file.
Sub-agents: none
Identifier-grep hits: 0 (script `idcheck.py`: whole-word, case-sensitive search for 36 private identifiers of the removal set; 4-word shingles of every string literal and comment of every removed C# line; verbatim string literals of 6 or more characters. The §2 labels, the command words and the §9.5 game-path literal are excluded as interface facts.)
Removal set: (test `a25a3b56`; identical at the branch `71d450fb` apart from Plugin.cs line numbers) whole-file 268 lines in 8 files (IpcProvider.cs 150, proteus.slnx 10, sample folder 108 in 6 files); direct outside whole files 252 (both modes 40 in 8 files, `-w -M -C -C` only 212 in 179 files, 69 of them test files); derived 51 outside whole files (plus 9 inside IpcProvider.cs); carried over 19 (ModsTab.cs 11, CompositorService.Carriers.cs 8); merge-inherited 0; restored candidates 13 (Plugin.cs 1, SidecarDiscoveryService.cs 4, ModsTab.cs 4, CompositorService.Carriers.cs 4). Direct across all files: 294 in `-w -M -C`, 504 in `-w -M -C -C`, 506 in the union. Closure rounds: 1 added 58 derived, 2 added 21 (19 carried over and 2 derived), 3 added 0.
Tripwire: fired. Condition 2: the set includes 21 Sensitive-Area files beyond the six known ones, all through `-w -M -C -C`-only import, blank or namespace lines: `Proteus/Interop/ShellCoverageFade.cs`, `Proteus/Interop/ShellNormalGhost.cs`, `Proteus/Interop/SkinDiffuseGlow.cs`; `Proteus/TestHarness/GameHarnessAdapter.cs`, `HairPortJob.cs`, `HarnessChanges.cs`, `HarnessStudio.cs`, `ModelJoin.cs`, `RestoreJob.cs`; the binary writers `Proteus/Services/SecondSkinWriter.{CapMissFill,CapStreamCopy,CapWeld,Connectors,CoverageMask,MeshEmitter.Reskin,NailBeds,NailSockets,Skinning,ToeRegion,Welding}.cs` and `Proteus/Services/ModelAttributeWriter.cs`. Condition 1 does not fire under D2's definition: derived + carried over + restored = 51 + 19 + 13 = 83 (92 counting IpcProvider.cs's 9 derived lines), against twice the direct lines = 1012 (union, all files) or 504 (union, outside whole files). It would fire only if "direct" meant the 40 `-w -M -C` lines outside whole files (83 > 80).

### Stage 1b (plan rev 10, commit 14b9118b)

Folder: same as above.
Opened:
- W/docs/contrib-clean-room-plan.md (lines 11–30, 185–228, 533–552)
- W/docs/contrib-clean-room-spec.md (lines 14–29, 555–579)
- W/docs/contrib-clean-room-log.md (lines 150–154)
- Grep tool, content mode: `69 |lose|176|179|Compile-only|removed import|Removed namespace|9\.5|Test literal|EDGE-5` in W/docs/contrib-clean-room-spec.md
Shell commands:
- `grep -n -E "^## |^[0-9]+\. \*\*|trivial|1b" E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-plan.md`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -h -B2 -A1 "mt_c0201b0001_bibo.mtrl\"," test -- Proteus.Tests/ModCreationTests.cs Proteus.Tests/OnionImportTests.cs`
- `python <session-scratch>/scratchpad-agentA/classify1b.py test`
- `python <session-scratch>/scratchpad-agentA/classify1b.py HEAD`
- `grep -a -E "^Proteus/Gui/ColorTableEditor" <session-scratch>/scratchpad-agentA/blame_CC.tsv <session-scratch>/scratchpad-agentA/blame_C.tsv`
- `python <session-scratch>/scratchpad-agentA/class_table.py`
- `python <session-scratch>/scratchpad-agentA/splice.py`
- `python <session-scratch>/scratchpad-agentA/idcheck.py`
- `grep -n -E "^## |^### |^\`\`\`|CLEAN-ROOM|outside B|lose an import" E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-spec.md`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room status --short`
- `grep -n -E "^## |^### |^Tripwire:" E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md`
- `wc -l E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md`
- Files written with Write and Edit: the scripts `classify1b.py`, `class_table.py`, `splice.py` and the text `s1_block.md` in the folder; in W only `docs/contrib-clean-room-spec.md` (§1, §9.5 and §10 amended) and this sub-part of the log.
- Forbidden locations: none opened. No `dotnet` command, no commit.
Sub-agents: none
Confirmation counts (test `a25a3b56` and branch `71d450fb` give the same result): 212 direct lines outside the whole files and the mixed set, in 179 files; 212 match the trivial regex or the game-fact rule (using 200, blank 6, namespace 3, brace 0, game-fact 3), 0 match neither; no derived or carried-over line lies in those files, so (c) holds for all 179; no file added to the mixed set. Game-fact lines: `Proteus.Tests/ModCreationTests.cs:208` (element), `:282` (assert), `Proteus.Tests/OnionImportTests.cs:559` (element); per file 2 and 1. The 40 direct lines inside the mixed set (29 non-trivial, 11 trivial-shaped) stay in the removal set.
Tripwire (rev 10 definition): not fired. Condition 1: 51 + 19 + 13 = 83 (92 with IpcProvider.cs's derived lines) against 2 × (268 whole-file + 29 non-trivial direct) = 594. Condition 2: 0 Sensitive-Area files beyond the known Interop files once the class is excluded (21 before).
Identifier-grep hits: 0

### Stage 1c (spec gap for step 2: bridge construction facts)

Opened: W/Proteus/Interop/PenumbraBridge.cs (lines 315–334); Grep tool, content mode, `void CheckAvailability|public Guid\? GetPlayerCollectionId|GetModSettings\(Guid|OnMainThread\(` in W/Proteus/Interop/PenumbraBridge.cs (-A 22, first 120 lines). Shell commands: `python <session-scratch>/scratchpad-agentA/idcheck.py`; `git -C E:/orca/workspaces/Proteus/licence-clean-room status --short`. Added three interface-fact rows to spec §9.3 (both bridge constructors, and how a substitute plugin interface makes Penumbra absent). Sub-agents: none. Identifier-grep hits: 0.

### Stage 1d (gated failure `ContribSpecTests.Glamourer_lock_warnings_name_the_item_and_slot`)

Opened: W/Proteus.Tests/ContribSpecTests.cs (lines 150–233); Grep tool, content mode, `Glamourer_lock_warnings_name_the_item_and_slot` in W/Proteus.Tests/ContribSpecTests.cs. Shell commands: `python -c "import re,sys;sys.stdout.reconfigure(encoding='utf-8');t=open(r'<session-scratch>\scratchpad\step2.trx',encoding='utf-8').read();i=t.find('Glamourer_lock_warnings_name_the_item_and_slot\" ');j=t.find('</UnitTestResult>',i);print(t[i:j][:4000])"`; `python <session-scratch>/scratchpad-agentA/idcheck.py`; `git -C E:/orca/workspaces/Proteus/licence-clean-room status --short`. Answer: spec error, §8.2 and §9.3 amended (the accessory setter's accepted slot codes, and the earlier refusal of any other slot argument, were missing). Sub-agents: none. Identifier-grep hits: 0.

### Stage 2 (removal)

Base: branch HEAD 233424a0 (mixed-set files identical to 71d450fb, where the §1 set was measured). Committed by the orchestrator as R0 = 7ce8851b.
Opened: no file with Read; the Read and Write tools touched only my own folder (the scripts `headset.py`, `remove.py`, `verify_manifest.py` and the draft of this sub-part).
Shell commands:
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff --stat 71d450fb HEAD -- Proteus proteus.slnx samples`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room status --short` (before the removal, after it, and at the end)
- `python <session-scratch>/scratchpad-agentA/headset.py`
- `python <session-scratch>/scratchpad-agentA/remove.py`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room rm -q -r Proteus/Interop/IpcProvider.cs proteus.slnx samples/ExampleMultiGroupOverlayMod`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff --numstat`
- `dotnet build E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Proteus.csproj -c Release > <session-scratch>/scratchpad-agentA/build_r.out 2>&1`
- `python <session-scratch>/scratchpad-agentA/verify_manifest.py <session-scratch>/scratchpad-agentA/build_r.out`
- `grep -o -E "[A-Za-z.]+\.cs\([0-9]+,[0-9]+\): error CS[0-9]+" <session-scratch>/scratchpad-agentA/build_r.out | sort -u | grep -o -E "CS[0-9]+" | sort | uniq -c`
- `grep -n -o -E "CLEAN-ROOM HOLE: spec §[0-9]+" E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Interop/PenumbraBridge.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Interop/GlamourerBridge.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Plugin.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Services/SidecarDiscoveryService.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Gui/StatusWindow.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Gui/ModsTab.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Services/CompositorService.Carriers.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Services/CompositorService.cs`
- Appending this sub-part: `tail -n 3 E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md`; `grep -n -E "^## |^### " E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md`; Read of the log lines 187–192 and of my draft; then the folder deletion `rm -rf "<session-scratch>/scratchpad-agentA"`, which the permission system denied (not retried).
Counts per file (removed / restored / markers):
- `Proteus/Interop/IpcProvider.cs`: whole file deleted (git rm), 150 lines
- `proteus.slnx`: whole file deleted (git rm), 10 lines; non-C# hole, regenerated per D23 from §1's list (Glamourer.Api and Penumbra.Api, each with an x64 platform mapping; Proteus; XivLiveMesh)
- `samples/ExampleMultiGroupOverlayMod/**`: 6 files deleted (git rm), 108 blame lines; non-C#, not remade
- `Proteus/Interop/PenumbraBridge.cs`: 9 direct → 9 / 0 / 3
- `Proteus/Interop/GlamourerBridge.cs`: 12 direct + 8 derived → 20 / 0 / 4
- `Proteus/Plugin.cs`: 6 direct + 1 derived → 7 / 1 / 5
- `Proteus/Services/SidecarDiscoveryService.cs`: 6 direct + 8 derived → 14 / 4 / 5
- `Proteus/Gui/StatusWindow.cs`: 4 direct + 28 derived → 32 / 0 / 2
- `Proteus/Gui/ModsTab.cs`: 1 direct + 11 carried over → 12 / 4 / 3
- `Proteus/Services/CompositorService.Carriers.cs`: 2 direct + 2 derived + 8 carried over → 12 / 4 / 4
- `Proteus/Services/CompositorService.cs`: 4 derived → 4 / 0 / 1
- Totals in the partial files: 110 removed, 13 restored (manifest rows), 27 markers. The 212 trivial-class lines outside the mixed set are untouched.
- Not restored (no fit): the ring-cooldown stamp line of `08cd11b2`'s parent (`CompositorService.cs` line 2837); the field it stamped no longer exists.
- One restored line in `ModsTab.cs` (from `78bd004a`'s parent, `StatusWindow.cs` line 121) is identical to the removed line at the same spot, so the diff shows it as unchanged; it is in the manifest (hence 39 added lines against 27 markers + 13 restores).
Holes (file:line at R → spec section):
- `Proteus/Interop/PenumbraBridge.cs`:40, :133, :851 → §7
- `Proteus/Interop/GlamourerBridge.cs`:199, :228, :338, :436 → §8
- `Proteus/Plugin.cs`:73 → §3; :183 → §3; :553 → §4; :557 → §4; :635 → §3
- `Proteus/Services/SidecarDiscoveryService.cs`:57, :67, :473, :523, :531 → §5
- `Proteus/Gui/StatusWindow.cs`:12, :1374 → §6
- `Proteus/Gui/ModsTab.cs`:3, :164, :175 → §6
- `Proteus/Services/CompositorService.Carriers.cs`:3, :326, :328, :508 → §8
- `Proteus/Services/CompositorService.cs`:739 → §6
- Non-C# holes: `proteus.slnx` (whole file) and `samples/ExampleMultiGroupOverlayMod/**` (deleted, not remade).
Manifest: `scratchpad-handover/contrib-restored-manifest.tsv`, 13 rows, each verified word for word and blamed to `solonamods@gmail.com` at the source commit's parent.
Build (`dotnet build W/Proteus/Proteus.csproj -c Release`): failed, as expected. Unique errors by code: CS0106 47, CS1519 8, CS1022 5, CS0116 3, CS1026 2, CS8124 2, CS1002 1, CS1003 1, CS1031 1, CS1524 1, CS1525 1, CS8641 1, CS8803 1.
Folder: `scratchpad-agentA\` NOT deleted: the deletion command was denied by the permission system. It still holds my scripts, blame lists and removal-set list, and must be deleted by the owner or orchestrator.
Sub-agents: none
Identifier-grep hits: 0


## Orchestrator

### Step 0: hand-off

- **Plan:** revision 7 committed at `71d450fb`; revision 10 at `14b9118b`, after the step-1 tripwire.
- **Window start:** 2026-11-17T23:38:46.968Z. This is the timestamp of the `git -C E:/orca/workspaces/Proteus/meddle worktree add -b solona-m/licence-clean-room E:/orca/workspaces/Proteus/licence-clean-room test` tool call. Self-check 2 counts from here.
- **BuildNumber line form:** `public const int BuildNumber = <n>;`. `test` read `BuildNumber = 1467` at step 0. The branch base `d4734c54` has 1466.
- **Blame on `test`, before the trivial classifier** (`-w -M -C`, count-only script, 1127 text files):
  - Caraxi has 274 lines:
    - `Proteus/Interop/IpcProvider.cs`: 137;
    - the sample folder: 100;
    - `proteus.slnx`: 9;
    - `Proteus/Interop/PenumbraBridge.cs`: 9;
    - `Proteus/Plugin.cs`: 6;
    - `Proteus/Services/SidecarDiscoveryService.cs`: 6;
    - `Proteus/Gui/StatusWindow.cs`: 4;
    - one each in `Proteus/Gui/ColorTableEditor.cs`, `ColorTableEditor.GlowFooter.cs` and `ColorTableEditor.SubRowPanel.cs`.
  - AzureRavie has 12 lines, all in `Proteus/Interop/GlamourerBridge.cs`.
  - dependabot[bot] has 1 line, in `Proteus.Tests/Proteus.Tests.csproj`.

### Step 1 and 1b: spec and tripwire

- **Step 1:** the spec and A's self-report, `1a2cd3ef`.
- **The D3 tripwire fired at step 1**, on condition 2. 21 Sensitive-Area files beyond the known ones were attributed only through `-w -M -C -C` import, blank or namespace lines.
- **Condition 1 under both readings:**
  - 83 lines (derived + carried over + restored) against 2 × 252 or 2 × 506 direct lines: not fired;
  - fired only if "direct" meant the 40 `-w -M -C` lines outside whole files (83 > 80).
- **Re-plan:** through the planner and plan-critics 8–10 (revision 10). The trivial coincidental class is counted, not removed. `87ae2e3d`'s carried-over scope is accepted.
- **Step 1b re-check by A, under the revision-10 definitions:**
  - The trivial class is 212 lines in 179 files: using 200, blank 6, namespace 3, brace 0, game fact 3. Every class line matched the regex or the game-fact rule.
  - Condition 1: 83 (92 with `IpcProvider.cs`'s derived lines) against 2 × (268 + 29) = 594. Not fired.
  - Condition 2: 0 Sensitive-Area files beyond the known ones (21 before the class was excluded). Not fired.
  - **Tripwire: fired at step 1, re-planned (rev 10); not fired at 1b.**
- **Mixed set after 1b:** the six original files, `Proteus/Gui/ModsTab.cs`, `Proteus/Services/CompositorService.Carriers.cs`, `Proteus/Services/CompositorService.cs`, `proteus.slnx` and the sample folder.

### Step 2: tests from the spec, proven on the old code

- **Tests written by the orchestrator from the spec alone:**
  - `Proteus.Tests/IpcProviderSpecTests.cs`;
  - `Proteus.Tests/ContribSpecTests.cs`;
  - the `Proteus.Tests/ContribCleanRoomRecordTests.cs` skeleton.
- **Spec gaps returned to A, both answered as "spec error, amended" (A's stages 1c and 1d):**
  - 1c: the bridge constructors, and how a test makes Penumbra absent;
  - 1d: the accessory setter accepts only the four game slot codes, which led to a one-line test fix (slot code `nek`).
- **Gated vs NewSeam test methods, per class:**
  - `IpcProviderSpecTests`: 7 gated (registration, version, thread, dispose, the guard paths of R5/R6, the R6 master switch) and 7 NewSeam (R2, R3, R4, R5, R6, R11, R12), all citing §9.1. The NewSeam share is 7 of 14, above one third, so **AC-1's proof on the old code is weaker**: the registration and guard paths are proven on both codes, the lookups on the clean code only.
  - `ContribSpecTests`: 6 gated (opening a mod with Penumbra absent, multi-select no-selection, the resolved-overlay record's shape, the body-shape signature with 5 cases, lock logging ×2) and 1 NewSeam theory (the refresh parse, 16 cases, citing §9.4).
- **Not unit-reachable, so no test was written:**
  - the Mods-tab click and the priority drag (§6.1, §6.2);
  - the colour-index block (§6.3);
  - the equip cooldown charged at the attempt (§8.1);
  - the plugin wiring order (R9).

  The existing `StutterFixesWiringTests` and `StutterFixesPureTests` cover the carrier helpers. The rest is left to the critics' §12 and spec review, and is UNVERIFIED in game where QA has no route.
- **Seams the tests require of B:**
  - `internal IpcProvider(IDalamudPluginInterface, Func<List<OverlayEntry>>, Func<OverlayEntry, List<ResolvedOverlay>>, Func<OverlayEntry, bool>, Action<string>, Configuration, IPluginLog)`;
  - `internal static (bool Full, bool Silent) Plugin.ParseRefreshArguments(string)`.
- **The gate**, run through D5: TRX output, console output never opened and then deleted, results read with `trx-summary.ps1`. Filter: WIDE-A plus the §10 classes (`ContentPieceSelectionTests`, `FaceUvDoublingTests`, `ShellMensLayoutTests`, `WomensArtOnMenTests`, `StutterFixesWiringTests`), `&CleanRoom!=NewSeam&CleanRoom!=NewBehaviour&FullyQualifiedName!~ContribCleanRoomRecordTests&Speed!=Slow`.
  - Run 1: 1008 passed, 1 failed (`Glamourer_lock_warnings_name_the_item_and_slot`, `Assert.Contains()`, hash `e39328fca658ac0c`). It went to A, who answered "spec error" (1d), and the test now uses the game slot code.
  - Run 2: **1009 passed, 0 failed, 0 skipped.** There are no baselines and no skips to carry forward. That TRX is kept as `step2-gate.trx` in the session scratchpad.
- **`norm` (D5):** CRLF becomes LF, then trim; paths become `<path>`; GUIDs become `<guid>`; digit runs become `<n>`. The hash is the first 16 hex digits of SHA-256.

### Step 3: removal checks (orchestrator, count-only)

- **The removal commit:** `<R0>` = `<R>` = `7ce8851b`. It was committed with `git add -u` without viewing it. `git status --short` listed only the mixed-set files, the deleted `IpcProvider.cs` and `proteus.slnx`, and the six deleted sample files.
- **`restored-check.ps1 -From 7ce8851b -To 7ce8851b`:** `added: 39, markers: 27, restored verified: 12 (manifest rows verified: 13), unmatched: 0, blank: 0, bump: 0, other: 0`. The 13th restored line is identical to the line it replaces, so it is not an added line; Agent A reported this.
  - **Two void runs came before it,** because of bugs in my script. A variable name clashed with the manifest parameter, since PowerShell names are case-insensitive. Separately, the section sign in the marker regex was decoded as ANSI. Both void runs printed counts only.
  - **A count-only marker-probe script** (`marker-probe.ps1`) located the encoding bug.
- **Blame at `<R>` (`blame-authors.ps1`, 1114 text files):**
  - In `-w -M -C`: contributor non-trivial 0. Caraxi has `using` 3, all in the `ColorTableEditor*.cs` files. AzureRavie has 0. dependabot[bot] has non-trivial 1 (the bot commit, which is recorded and not replaced).
  - In `-w -M -C -C`: contributor non-trivial 0. Caraxi has blank 6, `using` 202, namespace 3, brace 0 and game fact 3.
  - **Baseline against §1:** compared per file, the only differences are two new files with one `using` line each, `Proteus.Tests/ContribSpecTests.cs` and `Proteus.Tests/IpcProviderSpecTests.cs`. These are new test files by the orchestrator at step 2; copy detection matched their import header. Every other file is identical to §1's class table.
- **The guard against `test`:** 2. `106c450b` is bump-only and `dd53194b` is "other". This is logged only, and is handled at the step-8 merge through A3.
  - The first `guard-classify.ps1` run printed "guard: 0", because of a parameter-passing bug (`-File` passes a comma list as one string). A direct `git rev-list --count` gave 2, and the fixed script gives the same.
- **The 1b failure path added no file** (A confirmed this at 1b), so D8's exclusion list stays as written.
- **Two deletions were refused by the session's permission system, and neither was worked around:**
  - Agent A's `rm -rf` of `scratchpad-agentA\`;
  - the orchestrator's `Remove-Item` of `scratchpad-handover\contrib-restored-manifest.tsv`.

  Both stay on disk, unread by the clean side. Both are forbidden locations for every clean agent, so the owner should delete them.

### Step 4: slnx and "not passing"

- **`proteus.slnx`** was regenerated with `dotnet new sln --name proteus --format slnx`, then one `dotnet sln add --include-references false` call with the four projects from §1: Glamourer.Api, Penumbra.Api, Proteus and XivLiveMesh. It was committed alone, as `0dfaa9f8`.
  - A first attempt without `--include-references false` also pulled in the Luna projects. It was discarded and regenerated with `--force` before the commit.
- **Deleting `bin/` and `obj/` in the four projects was refused** by the session's permission system, and was not worked around. Agent B's allowlist forbids those folders, the transcript extraction checks for them, and B's own build overwrites the outputs.
- **The "not passing" run** (the spec tests on the removed code) fails to compile, with 74 `error CS` lines. This is the expected state: the spec tests don't pass.

## Agent B (run 1, void)

Start commit: `6c167978`. Extracted from its transcript by agent ID (extract_contrib.py, role b).

VOID: one build command was piped through grep/head, outside the allowlist (plan EDGE-13). It read nothing forbidden, but its work was discarded unseen by the orchestrator (git checkout of the eight files, git clean of the new IpcProvider.cs) and redone by a fresh Agent B (run 2). The 24 "unparseable" listing hits first reported came from the extraction script not parsing context lines (path-N-text) and -- separators; both are fixed and the recount is 0.

Opened:
- Read docs/contrib-clean-room-spec.md
- Grep Proteus
- Grep docs/contrib-clean-room-plan.md
- Read Proteus/Services/SidecarDiscoveryService.cs
- Edit Proteus/Services/SidecarDiscoveryService.cs
- Read Proteus/Interop/PenumbraBridge.cs
- Edit Proteus/Interop/PenumbraBridge.cs
- Read Proteus/Interop/GlamourerBridge.cs
- Grep Proteus.Tests/StutterFixesWiringTests.cs
- Edit Proteus/Interop/GlamourerBridge.cs
- Grep Proteus/Interop/GlamourerSignals.cs
- Read Proteus/Services/CompositorService.Carriers.cs
- Edit Proteus/Services/CompositorService.Carriers.cs
- Read Proteus.Tests/StutterFixesWiringTests.cs
- Grep Proteus/Services/CarrierDecisions.cs
- Read Proteus/Services/CompositorService.cs
- Edit Proteus/Services/CompositorService.cs
- Read Proteus/Gui/StatusWindow.cs
- Grep Proteus/Gui/StatusWindow.cs
- Grep Proteus/Services/TextureLoader.cs
- Read Proteus/Models/ColorTable.cs
- Edit Proteus/Gui/StatusWindow.cs
- Read Proteus/Gui/ModsTab.cs
- Edit Proteus/Gui/ModsTab.cs
- Read Proteus/Plugin.cs
- Grep Proteus.Tests
- Read Proteus.Tests/ContribSpecTests.cs
- Grep Proteus/Plugin.cs
- Edit Proteus/Plugin.cs
- Read Proteus.Tests/IpcProviderSpecTests.cs
- Grep Proteus/Services
- Read Proteus/Models/OverlayDescriptor.cs
- Write Proteus/Interop/IpcProvider.cs
Shell commands:
- OUTSIDE ALLOWLIST: dotnet build E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Proteus.csproj -c Release 2>&1 | grep -E "error|Warn|Build succeeded|Time Elapsed" | grep -v "warning" | head -40
- dotnet build E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Proteus.csproj -c Release
- dotnet test E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "(FullyQualifiedName~IpcProviderSpecTests|FullyQualifiedName~ContribSpecTests)&Speed!=Slow"
- dotnet test E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "(FullyQualifiedName~SidecarDiscoveryTests|FullyQualifiedName~ContentPieceSelectionTests|FullyQualifiedName~FaceUvDoublingTests|FullyQualifiedName~ShellMensLayoutTests|FullyQualifiedName~WomensArtOnMenTests|FullyQualifiedName~StutterFixesWiringTests|FullyQualifiedName~StutterFixesPureTests|FullyQualifiedName~HarnessBindingsWiringTests)&Speed!=Slow"
- dotnet test E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "(FullyQualifiedName~LocalizationTests|FullyQualifiedName~SidecarCache|FullyQualifiedName~DesignBinding|FullyQualifiedName~PlayerSettingsFilterTests|FullyQualifiedName~HarnessBridgeWrapperTests|FullyQualifiedName~IdleDiskIoWiringTests)&Speed!=Slow"
Sub-agents: none
Grep without line numbers: 0
Shell commands outside the allowlist: 1
forbidden locations in listing/search results: 0

## Agent B

Start commit: `6c167978`. Extracted from its transcript by agent ID (extract_contrib.py, role b).

Run 2, the delivered implementation. Files: the eight mixed files and a new Proteus/Interop/IpcProvider.cs; no Localization change, no other new file. Seams: internal IpcProvider(IDalamudPluginInterface, Func<List<OverlayEntry>>, Func<OverlayEntry, List<ResolvedOverlay>>, Func<OverlayEntry, bool>, Action<string>, Configuration, IPluginLog); internal static (bool Full, bool Silent) Plugin.ParseRefreshArguments(string). Its tests: 40 + 183 + 795 passed, 0 failed.

Opened:
- Read AGENTS.md
- Read docs/contrib-clean-room-spec.md
- Grep docs/contrib-clean-room-plan.md
- Read docs/contrib-clean-room-plan.md
- Grep Proteus
- Read Proteus/Plugin.cs
- Edit Proteus/Plugin.cs
- Grep Proteus/Plugin.cs
- Grep Proteus.Tests
- Read Proteus.Tests/ContribSpecTests.cs
- Read Proteus.Tests/IpcProviderSpecTests.cs
- Grep Proteus/Models
- Read Proteus/Models/OverlayDescriptor.cs
- Read Proteus/Services/SidecarDiscoveryService.cs
- Edit Proteus/Services/SidecarDiscoveryService.cs
- Read Proteus/Interop/PenumbraBridge.cs
- Edit Proteus/Interop/PenumbraBridge.cs
- Grep docs
- Read Proteus/Interop/GlamourerBridge.cs
- Edit Proteus/Interop/GlamourerBridge.cs
- Grep Proteus.Tests/StutterFixesWiringTests.cs
- Read Proteus.Tests/StutterFixesWiringTests.cs
- Read Proteus/Services/CompositorService.Carriers.cs
- Grep Proteus/Services/CompositorService.Carriers.cs
- Edit Proteus/Services/CompositorService.Carriers.cs
- Read Proteus/Services/CompositorService.cs
- Edit Proteus/Services/CompositorService.cs
- Grep Proteus/Services/CompositorService.cs
- Read Proteus/Gui/ModsTab.cs
- Edit Proteus/Gui/ModsTab.cs
- Read Proteus/Gui/StatusWindow.cs
- Grep Proteus/Services
- Grep Proteus/Gui/StatusWindow.cs
- Read Proteus/Models/ColorTable.cs
- Edit Proteus/Gui/StatusWindow.cs
- Glob E:/orca/workspaces/Proteus/licence-clean-room
- Write Proteus/Interop/IpcProvider.cs
- Edit Proteus/Interop/IpcProvider.cs
- Grep Proteus/Interop/IpcProvider.cs
Shell commands:
- dotnet build E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Proteus.csproj -c Release
- dotnet test E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "FullyQualifiedName~IpcProviderSpecTests|FullyQualifiedName~ContribSpecTests&Speed!=Slow"
- dotnet test E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "FullyQualifiedName~SidecarDiscoveryTests|FullyQualifiedName~ContentPieceSelectionTests|FullyQualifiedName~FaceUvDoublingTests|FullyQualifiedName~ShellMensLayoutTests|FullyQualifiedName~WomensArtOnMenTests|FullyQualifiedName~StutterFixesWiringTests|FullyQualifiedName~StutterFixesPureTests|FullyQualifiedName~HarnessBindingsWiringTests&Speed!=Slow"
- dotnet test E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "FullyQualifiedName~LocalizationTests|FullyQualifiedName~SidecarCache|FullyQualifiedName~DesignBinding|FullyQualifiedName~PlayerSettingsFilterTests|FullyQualifiedName~HarnessBridgeWrapperTests|FullyQualifiedName~IdleDiskIoWiringTests&Speed!=Slow"
Sub-agents: none
Grep without line numbers: 0
Shell commands outside the allowlist: 0
forbidden locations in listing/search results: 0

## Agent D (self-report)

Folder: <session-scratch>\scratchpad-agentD\
Opened:
- docs/contrib-clean-room-plan.md (Read, lines 171-370; also Grep for headings)
- docs/contrib-clean-room-spec.md (Read, lines 349-573; also Grep for headings)
- scratchpad-agentD/measure.py, idgrep.py, section.md (written by D)
Shell commands:
- mkdir -p "<session-scratch>/scratchpad-agentD"
- git -C E:/orca/workspaces/Proteus/licence-clean-room diff --stat 9ae845ea^ 9ae845ea
- python -I "<session-scratch>/scratchpad-agentD/measure.py" "<session-scratch>/scratchpad-agentD"   (runs, inside the script: git -C W diff -U0 7ce8851b^ 7ce8851b; git -C W diff -U0 9ae845ea^ 9ae845ea -- Proteus)
- python -I -c "import json;d=json.load(open(r'<session-scratch>/scratchpad-agentD/result.json',encoding='utf-8'));[print(m['file'].split('/')[-1],m['line'],'|',m['norm'],'|',m['removed_at'][:3]) for m in d['matches']]"
- git -C E:/orca/workspaces/Proteus/licence-clean-room show 9ae845ea:Proteus/Gui/StatusWindow.cs | sed -n '1355,1420p'
- git -C E:/orca/workspaces/Proteus/licence-clean-room show 9ae845ea:Proteus/Gui/ModsTab.cs | grep -n "pri_\|_priorityEdits\|var pri\|bool active"
- git -C E:/orca/workspaces/Proteus/licence-clean-room show 9ae845ea:Proteus/Services/SidecarDiscoveryService.cs | sed -n '470,545p'
- git -C E:/orca/workspaces/Proteus/licence-clean-room show 9ae845ea:Proteus/Interop/PenumbraBridge.cs | grep -n "private readonly [A-Z][A-Za-z]* [a-z][A-Za-z]*;\|try { .*} *$"
- git -C E:/orca/workspaces/Proteus/licence-clean-room diff -U0 9ae845ea^ 9ae845ea -- Proteus/Gui/StatusWindow.cs Proteus/Gui/ModsTab.cs Proteus/Services/SidecarDiscoveryService.cs | grep "^@@"
- git -C E:/orca/workspaces/Proteus/licence-clean-room diff -U1 9ae845ea^ 9ae845ea -- Proteus/Interop/GlamourerBridge.cs Proteus/Interop/IpcProvider.cs Proteus/Plugin.cs Proteus/Services/CompositorService.Carriers.cs Proteus/Interop/PenumbraBridge.cs
- python -I "<session-scratch>/scratchpad-agentD/idgrep.py" "<session-scratch>/scratchpad-agentD"   (runs, inside the script: the two diffs above, git ls-tree -r --name-only 9ae845ea^ -- Proteus, git show 9ae845ea^:<each .cs file>)
- python -I -c "exec(open(r'<session-scratch>/scratchpad-agentD/idgrep.py').read().replace('print(\"identifier-grep hits in B','print(sorted(hits));print(\"x'))" "<session-scratch>/scratchpad-agentD"
- python -I -c "exec(open(r'<session-scratch>/scratchpad-agentD/idgrep.py').read().replace('only_removed = rem_ids - spec_ids - pre','low={x.lower() for x in spec_ids|pre};only_removed={x for x in rem_ids if x.lower() not in low}'))" "<session-scratch>/scratchpad-agentD"
- tail -n 30 E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md
- python -I -c "open(r'E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md','a',encoding='utf-8').write(open(r'<session-scratch>/scratchpad-agentD/section.md',encoding='utf-8').read())"
Sub-agents: none
Identifier-grep hits: 0 (the grep: identifiers found in the removed set but in neither the spec nor the pre-B tree, then searched in B's lines. Exact-case pass: 3 hits, all forced: one BCL enum member named by the spec's wording, and two camelCase field names made from type names the spec pins. Case-folded pass: 1 hit, the BCL member. None is a contributor-coined identifier.)
First measurement (B commit 9ae845ea):
- Removed set: 369 lines, 145 non-trivial, 1681 distinct 5-grams
- Proteus/Gui/ModsTab.cs: non-trivial 5; exact 5 (interface 3, idiomatic/forced 2, expressive 0); Jaccard 0.0379 (containment 0.914)
- Proteus/Gui/StatusWindow.cs: non-trivial 19; exact 8 (interface 2, idiomatic/forced 6, expressive 0); Jaccard 0.0858 (containment 0.659)
- Proteus/Interop/GlamourerBridge.cs: non-trivial 9; exact 3 (interface 3, idiomatic/forced 0, expressive 0); Jaccard 0.0174 (containment 0.423)
- Proteus/Interop/IpcProvider.cs: non-trivial 112; exact 3 (interface 1, idiomatic/forced 2, expressive 0); Jaccard 0.1156 (containment 0.261)
- Proteus/Interop/PenumbraBridge.cs: non-trivial 5; exact 4 (interface 3, idiomatic/forced 1, expressive 0); Jaccard 0.0297 (containment 0.909)
- Proteus/Plugin.cs: non-trivial 12; exact 2 (interface 1, idiomatic/forced 1, expressive 0); Jaccard 0.0220 (containment 0.226)
- Proteus/Services/CompositorService.Carriers.cs: non-trivial 4; exact 2 (interface 2, idiomatic/forced 0, expressive 0); Jaccard 0.0254 (containment 0.827)
- Proteus/Services/CompositorService.cs: non-trivial 4; exact 0; Jaccard 0.0193 (containment 0.516)
- Proteus/Services/SidecarDiscoveryService.cs: non-trivial 5; exact 5 (interface 4, idiomatic/forced 1, expressive 0); Jaccard 0.0405 (containment 1.000)
- Overall: non-trivial 175; exact 32 (interface 19, idiomatic/forced 13, expressive 0); Jaccard 0.2602 (containment 0.394)
- Files at Jaccard >= 0.40: none
Expressive hits: 0 (none)

## Orchestrator (steps 5-7)

Phases delivered: A,B,C

### Step 5: Agent B

- **Run 1 was void** (see "Agent B (run 1, void)"). One build command was piped through grep and head, outside the allowlist. Its edits were discarded unseen with `git checkout --` on the eight files and `git clean -f` on the new `IpcProvider.cs`.
- **Run 2 is clean,** with every count at 0. It was committed as `9ae845ea`.
  - `git status --short` listed only the eight mixed files, the new `Proteus/Interop/IpcProvider.cs` and the log.
  - B named no other new file and made no Localization change.
- **The extraction script was fixed** before run 1 was counted. It now parses content-Grep context lines (`path-N-text`) and `--` separators. Before the fix, 31 lines were reported as "unparseable" hits. None of them was a forbidden location.

### Step 6: proof on the clean code

- **The step-2 filter without the CleanRoom exclusions** gave 1032 passed, 0 failed and 0 skipped: the 1009 step-2 tests plus 23 NewSeam cases (7 in `IpcProviderSpecTests` and 16 refresh-parse cases). The skip set equals step 2's (empty), and there are no baselines.
- **`git grep` for `CLEAN-ROOM HOLE` under `Proteus/`:** none.
- **Blame at `9ae845ea`:**
  - `-w -M -C`: contributor non-trivial 0; Caraxi `using` 3 (ColorTableEditor). No per-file change since `<R>`.
  - `-w -M -C -C`: contributor non-trivial 0; Caraxi blank 6, `using` 204, namespace 3, brace 0, game fact 3. The one change since `<R>` is in `Proteus/Interop/IpcProvider.cs`: `using` +2, with the reason "a new file by B". B's new file header was copy-matched.

### Step 7: Agent D

- **First measurement** (B commit `9ae845ea`): 0 expressive matches.
  - Of 175 non-trivial B lines, 32 match the removed set exactly: 19 interface, 13 idiomatic or forced.
  - No file is at 0.40 Jaccard; the overall figure is 0.2602.
  - No rewrite was needed, so this first measurement is also the last.
- **One command is missing from D's own list** because it ran after D's append: `git -C E:/orca/workspaces/Proteus/licence-clean-room status --short`. D reported it.
- **`T_A`** is the commit holding this section.

## Agent C (self-report)

Opened:
- docs/contrib-clean-room-plan.md (Grep for D24-D26/B1/Agent C; Read lines 130-159 and 290-329)
- Proteus/Interop/PenumbraBridge.cs
- Proteus/Interop/GlamourerBridge.cs
- Proteus/TestHarness/GameHarnessAdapter.cs (lines 285-334, plus Grep matches)
- Proteus/**, Proteus.Tests/** (Grep only: API usings, IpcSubscribers/GetIpcSubscriber/subscriber-type references, enum-name usage)
- Penumbra.Api/BasePenumbraSubscriber.cs
- Penumbra.Api/GlobalUsings.cs
- Penumbra.Api/IpcSubscribers/Mods.cs
- Penumbra.Api/IpcSubscribers/PluginState.cs
- Penumbra.Api/IpcSubscribers/Collection.cs
- Penumbra.Api/IpcSubscribers/ModSettings.cs
- Penumbra.Api/IpcSubscribers/Temporary.cs
- Penumbra.Api/IpcSubscribers/Resolve.cs
- Penumbra.Api/IpcSubscribers/Redraw.cs
- Penumbra.Api/IpcSubscribers/Ui.cs
- Penumbra.Api/IpcSubscribers/ResourceTree.cs (Grep match, class GetGameObjectResourcePaths)
- Penumbra.Api/IpcSubscribers/Meta.cs (Grep match, class GetPlayerMetaManipulations)
- Penumbra.Api/IpcSubscribers/Legacy/*.cs (Grep only: namespace lines; ResourceTree.cs GetGameObjectResourcePaths match)
- Penumbra.Api/Enums/ModSettingChange.cs
- Penumbra.Api/Enums/PenumbraApiEc.cs
- Penumbra.Api/Enums/RedrawType.cs
- Penumbra.Api/Enums/TabType.cs
- Glamourer/Glamourer.Api/IpcSubscribers/Designs.cs
- Glamourer/Glamourer.Api/IpcSubscribers/State.cs
- Glamourer/Glamourer.Api/IpcSubscribers/Items.cs
- Glamourer/Glamourer.Api/IpcSubscribers/PluginState.cs
- Glamourer/Glamourer.Api/Enums/ApplyFlag.cs
- Glamourer/Glamourer.Api/Enums/GlamourerApiEc.cs
- Glamourer/Glamourer.Api/Enums/ApiEquipSlot.cs
- Glamourer/Glamourer.Api/Enums/ApiBonusSlot.cs
- Glamourer/Glamourer.Api/Enums/StateChangeType.cs
- Glamourer/Glamourer.Api/Enums/StateFinalizationType.cs
- Glamourer/LICENSE (lines 1-15, and Grep for "Copyright")
- Luna/Luna/Ipc/FuncSubscriber.cs
- Luna/Luna/Ipc/EventSubscriber.cs
- Luna/Luna/Ipc/ActionSubscriber.cs
- Glamourer/Luna/Luna/Ipc/FuncSubscriber.cs
- Glamourer/Luna/Luna/Ipc/EventSubscriber.cs (lines 110-167)
- Luna/ and Glamourer/Luna/ (Grep only, files-with-matches for the four subscriber class declarations, to locate the files)
- docs/contrib-clean-room-log.md (Grep for headings; Read lines 470-502, to append)
Shell commands:
- git -C E:/orca/workspaces/Proteus/licence-clean-room/Penumbra.Api ls-files
- git -C E:/orca/workspaces/Proteus/licence-clean-room/Glamourer ls-files Glamourer.Api LICENSE NOTICE NOTICE.md NOTICE.txt
- git -C E:/orca/workspaces/Proteus/licence-clean-room/Glamourer ls-files ":(glob,icase)NOTICE*"
- git -C E:/orca/workspaces/Proteus/licence-clean-room/Glamourer/Glamourer.Api ls-files
- git -C E:/orca/workspaces/Proteus/licence-clean-room/Penumbra.Api ls-files ":(glob,icase)LICENSE*" ":(glob,icase)NOTICE*"
Sub-agents: none
Identifier-grep hits: 0
Luna files read (facts only): Luna/Luna/Ipc/FuncSubscriber.cs, Luna/Luna/Ipc/EventSubscriber.cs, Luna/Luna/Ipc/ActionSubscriber.cs, Glamourer/Luna/Luna/Ipc/FuncSubscriber.cs, Glamourer/Luna/Luna/Ipc/EventSubscriber.cs (lines 110-167)
Glamourer NOTICE file: none

## Agent B2

Start commit: `22502d52`. Extracted from its transcript by agent ID (extract_contrib.py, role b).

Phase B implementer (clean; never read the API or Luna sources). New: Proteus/Interop/Ipc/{IpcContract,IpcGates,PenumbraGates,GlamourerGates,PenumbraApiTypes,GlamourerApiTypes}.cs, Proteus/TestHarness/{GlamourerApplyDesign,GlamourerApplyState}.cs. B2 edits of existing files (named): Proteus/Interop/PenumbraBridge.cs, Proteus/Interop/GlamourerBridge.cs, Proteus/TestHarness/GameHarnessAdapter.cs, Proteus/Plugin.cs (wiring: IpcLog.Attach/Detach), Proteus/Proteus.csproj (three ProjectReferences and the Luna comment removed), Proteus/packages.lock.json (hand-edited because restore kept stale entries; confirmed by the orchestrator's dotnet restore --force-evaluate, which changed nothing further). Proteus/Interop/LunaLog.cs was emptied by B2 and removed by the orchestrator (git rm) together with its test Proteus.Tests/LunaLogTests.cs: it only routed Luna's log, which no longer exists, and its role moved into IpcLog. IpcContract.Entries: 43 (P1-P35, G1-G8). The 7 listing hits first reported were lines of one ripgrep regex-parse error message (no result paths); the extraction now skips such errors and the recount is 0.

Opened:
- Read docs/ipc-contract.md
- Grep docs/contrib-clean-room-plan.md
- Glob E:/orca/workspaces/Proteus/licence-clean-room
- Read docs/contrib-clean-room-plan.md
- Read Proteus.Tests/IpcContractTests.cs
- Read Proteus.Tests/BridgeAvailabilityTests.cs
- Read Proteus.Tests/ShippedAssembliesTests.cs
- Read Proteus.Tests/HarnessBindingsWiringTests.cs
- Grep Proteus.Tests/ContribSpecTests.cs
- Read Proteus.Tests/ContribSpecTests.cs
- Read Proteus/Interop/PenumbraBridge.cs
- Read Proteus/Interop/GlamourerBridge.cs
- Grep Proteus
- Read Proteus/Proteus.csproj
- Read Proteus/Interop/LunaLog.cs
- Grep E:/orca/workspaces/Proteus/licence-clean-room
- Read Proteus.Tests/LunaLogTests.cs
- Grep Proteus.Tests/Proteus.Tests.csproj
- Read Proteus.Tests/Proteus.Tests.csproj
- Glob %APPDATA%/XIVLauncher/addon/Hooks/dev
- Grep Proteus/packages.lock.json
- Grep Proteus.Tests
- Grep docs
- Read Proteus/Plugin.cs
- Grep Proteus/TestHarness/GameHarnessAdapter.cs
- Read Proteus/TestHarness/GameHarnessAdapter.cs
- Write Proteus/Interop/Ipc/PenumbraApiTypes.cs
- Write Proteus/Interop/Ipc/GlamourerApiTypes.cs
- Write Proteus/Interop/Ipc/IpcGates.cs
- Write Proteus/Interop/Ipc/PenumbraGates.cs
- Write Proteus/Interop/Ipc/GlamourerGates.cs
- Write Proteus/Interop/Ipc/IpcContract.cs
- Edit Proteus/Interop/PenumbraBridge.cs
- Edit Proteus/Interop/GlamourerBridge.cs
- Write Proteus/TestHarness/GlamourerApplyDesign.cs
- Write Proteus/TestHarness/GlamourerApplyState.cs
- Edit Proteus/TestHarness/GameHarnessAdapter.cs
- Grep Proteus.Tests/HarnessReleaseGuardTests.cs
- Grep Proteus.Tests/HarnessBuildFlagTests.cs
- Read Proteus.Tests/HarnessReleaseGuardTests.cs
- Write Proteus/Interop/LunaLog.cs
- Edit Proteus/Plugin.cs
- Edit Proteus/Proteus.csproj
- Read Proteus/packages.lock.json
- Write Proteus/packages.lock.json
- Read Proteus.Tests/IdleDiskIoWiringTests.cs
- Grep Proteus.Tests/SupplyChainTests.cs
- Read Proteus.Tests/SupplyChainTests.cs
Shell commands:
- dotnet build E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Proteus.csproj -c Release
- dotnet build E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Proteus.csproj -c Release
- dotnet test E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "FullyQualifiedName~IpcContractTests|FullyQualifiedName~ShippedAssembliesTests|FullyQualifiedName~BridgeAvailabilityTests|FullyQualifiedName~HarnessBindingsWiringTests|FullyQualifiedName~HarnessBuildFlagTests|FullyQualifiedName~HarnessReleaseGuardTests&Speed!=Slow"
Sub-agents: none
Grep without line numbers: 0
Shell commands outside the allowlist: 0
forbidden locations in listing/search results: 0

## Orchestrator (Phase B)

- **The scope change.** After step 3 the user wrote: "Do the Luna work as well. Keep going until you've pushed that and all the other needed changes." Phases B and C came back through the planner and Plan-critics 11–13: revision 12 with amendments 12a–12c. The toe-cap data was confirmed by the owner ("The toecaps were handmade by me") and is classed as Proteus's own.
- **The "Phases delivered" line.** The phases line in the steps 5–7 section was written as "A" at `T_A` (`209e5bbc`). It was changed to "A,B" in the commit holding this section, which is `T_B`.
- **B1:** Agent C's contract, `docs/ipc-contract.md` (`0a3fe30e`).
- **B2:** the orchestrator's tests, written from the contract (`22502d52`):
  - `IpcContractTests` (Cecil, following contract §5; failure messages name only the row and the fact);
  - `ShippedAssembliesTests`;
  - `BridgeAvailabilityTests`;
  - the `HarnessBindingsWiringTests` gate names.

  Adding the aliased API references to the test project made it fail to compile until B3. That was the expected red state.
- **B3:** Agent B2, `dc8f5e4e`. The orchestrator removed the emptied `LunaLog.cs` and its test with `git rm`. `dotnet restore --force-evaluate` left B2's hand-edited lock file unchanged.
- **B4 proof:**
  - **WIDE-A plus WIDE-B:** 1901 passed, 0 failed, 0 skipped.
  - **Blame at `dc8f5e4e`:**
    - `-w -M -C`: contributor non-trivial 0, with no per-file change since `<R>`.
    - `-w -M -C -C`: contributor non-trivial 0. The change since `<R>` is `using` +1 in `Proteus/Interop/Ipc/IpcContract.cs`, a new file by B2 (named). `Proteus/Interop/IpcProvider.cs` (+2, B's new file) was already logged at step 6.
  - **The three DLLs:** after `dotnet clean` and a Release build, `Test-Path` is False for `Luna.dll`, `Penumbra.Api.dll` and `Glamourer.Api.dll` in `Proteus/bin/Release/win-x64`.
  - **`zip-entries.ps1 latest.zip`** lists none of them. The entries are: BCnEncoder.dll, BitMiracle.LibTiff.NET.dll, CommunityToolkit.HighPerformance.dll, Meshes\toecap.{bibo,neolithe,rue,yab}.{bind,mdl}, Proteus.deps.json, Proteus.dll, Proteus.json, proteus_bcn.dll, StbImageSharp.dll, StbImageWriteSharp.dll, THIRD_PARTY_NOTICES.txt, XivLiveMesh.dll and XivLiveMesh.pdb.
  - **AC-7:** Phases A and B changed no licence or notice file. `git diff test...dc8f5e4e --name-only` lists none of `LICENSE`, `docs/LICENSE-DRAFT.md`, `THIRD_PARTY_NOTICES.txt` or `XivLiveMesh/LICENSE|NOTICE.md`.
- **B5, the smoke check in game:**
  - Build 1474 (`2c384009`, the next number above `test`'s 1473) was copied to `E:\repos\Proteus\Proteus\bin\release\win-x64`, after warning the peer sessions. The `Proteus.dll` timestamp is 10:50:32 PM.
  - The warning to `matcap-gear-shader-5d` was held by that session and expired undelivered. The other two warnings were delivered.
  - The QA-lite agent found no harness session file (`test-harness.json` missing): the game wasn't running or the plugin wasn't loaded. Per the QA rules that is a Blocker, so all four smoke items are **UNVERIFIED**, not passes. Nothing was changed in game.
  - Following the global rule (a QA Blocker is noted and the run finishes without that part), the run continues to Phase C. The in-game check of the new IPC layer is left to step 8's QA, or to the owner.

## Planning participants

**Who took part.** The planner (revisions 1 to 12, then amendments 12a, 12b and 12c), plan-critics 1 to 13, and the planning orchestrator. All were on the clean side. None opened the code being replaced or its history, or the Luna, Glamourer or Penumbra.Api sources (apart from LICENSE and README files). None started a sub-agent or opened a URL.

**Where their reads are recorded.** Each revision's *Environment & Preconditions* section lists every planning participant's reads verbatim, with full paths:
- Opened: every file each participant opened.
- Shell commands: every command each participant ran.

The revisions are in `docs/contrib-clean-room-plan.md` and its git history:
- revision 7, at `71d450fb`;
- revision 10, at `14b9118b`;
- revision 11, at `16b93eab`;
- revision 12 with 12a–12c, at `e560d301`.

Plan-critics 7, 10, 12 and 13 read only plan output files. Their reads are in the committed plan's preface and in the 12a–12c amendments.

**The planning orchestrator's disclosures** are in revision 12's *Reads, for the log*. They are:
- the earlier `Plugin.cs` lines 31–34 Read, from the PbdFile run;
- the count-only `-L 31,34` blame, which printed 0;
- the meddle-worktree greps and blame counts, which ran before the window started;
- the transcript greps;
- the two `head -n 3` LICENSE reads.

**Deviation from the precedent.** These reads are recorded once, in the committed plan revisions, rather than copied into a separate section per participant here. The plan text is the committed record.

## Orchestrator (Phase C)

- **The Phase B tip correction.** The commit `02d2514a` (the Phase B record) also carried the deletion of `docs/LICENSE-DRAFT.md`, which had been staged early for Phase C. Rewriting history is a hard stop, so the next commit, `ef7aa083`, restores the draft. **`T_B` is `ef7aa083`.** The phases line went to "A,B" in `02d2514a` and to "A,B,C" in the commit holding this section.

### C1 (XivLiveMesh usage check)

These were name-only searches:
- A files-with-matches Grep for `XivLiveMesh` over `E:\repos` with glob `**/*.csproj` found only Proteus checkouts: `E:\repos\Proteus`, `E:\repos\Proteus-milieu-route` and `E:\repos\Proteus-wt\release-main`. A first run with glob `*/*.csproj` matched nothing, because the glob was too shallow.
- A count-mode Grep for `XivLiveMesh` over `E:\repos\DragAndDropTexturing` found 0. No file there was opened.
- `gh search code XivLiveMesh --owner solona-m --json repository,path --limit 50` found hits in `solona-m/proteus` only.

So no other plugin uses XivLiveMesh, and it moves to the Proteus Licence outright: there is no dual licence. `XivLiveMesh/LICENSE` is byte-identical to `LICENSE`.

### C2 (publication)

This is commit `4f535282`.
- **`LICENSE`:** the reviewed draft, word for word, with line 3 changed to "*Version 1.0 — in force from 2026-11-17*". The date is corrected at step 9 if the push falls on a later day.
- **`docs/LICENSE-DRAFT.md`:** moved to `Proteus.Tests/Fixtures/proteus-licence-1.0-draft.md`, as the licence test's expected text. `LICENSE` ships as `LICENSE.txt`.
- **SPDX:** `LicenseRef-Proteus` in the 7 `XivLiveMesh/*.cs` files, `IpcProvider.cs` and `PenumbraApiTypes.cs`. `GlamourerApiTypes.cs` stays `Apache-2.0`.
- **Project files:** `PackageLicenseExpression` is removed from both csproj files.
- **Package licences,** read from the nuspec files in the local NuGet cache, with these exceptions:
  - BitMiracle.LibTiff.NET's nuspec has only a licence URL, and the package holds no licence file. Its New BSD text was read from that URL (https://bitmiracle.github.io/libtiff.net/help/articles/license.html), through the web-fetch agent.
  - StbImageWriteSharp's nuspec has no licence, and the repository has no licence file (`gh api`: 404 on the licence and on raw LICENSE). Its README says "Public Domain".

  Both readings are from the projects' own published statements, not guesses. This deviates from D29's "nuspec and licence file" wording.
- **The Glamourer block** reproduces Glamourer's root `LICENSE` text (Apache-2.0), copied with `cat` from `Glamourer/LICENSE`, a LICENSE file the clean side may read.
- **The inventory** is in `Proteus.Tests/Fixtures/package-inventory.tsv`. All 21 entries of the rebuilt `latest.zip` are in it, `LICENSE.txt` included. The two embedded `Resources/*.png` slices are carved out, and `Meshes/**` is classed as own, as the owner confirmed.
- **Proof:** the Records filter plus `LicencePublicationTests` and `SupplyChainTests` gave 143 passed, 0 failed.

### C3 (proof)

- **`git diff ef7aa083 4f535282 --name-only`** lists exactly the Phase C set: AGENTS.md, LICENSE, the test files and fixtures, `PenumbraApiTypes.cs`, `IpcProvider.cs`, `Proteus.csproj`, `THIRD_PARTY_NOTICES.txt`, and the XivLiveMesh files.
- **Phase C blame at `4f535282`:**
  - `-w -M -C`: contributor non-trivial 0, no per-file change.
  - `-w -M -C -C`: contributor non-trivial 0. The one new change since the Phase B tip is `XivLiveMesh/HavokSkeleton.cs` `using` +1, with the reason "Phase C SPDX": the line-1 edit shifted copy detection onto an existing import.

## Agent A3 (self-report)

The step-8 merge of `test` (T8 = `1e2c5cc6`) into the branch (P8 = `d9d8f7d4`), left staged and uncommitted for the orchestrator. Merge base `d4734c54`.

Folder: <session-scratch>\scratchpad-agentA\ (reused; still holds my stage 1 and 2 scratch, see Agent A's stage 2 note)
Opened:
- W/Proteus/Plugin.cs (lines 33–38, the conflict hunk)
- W/docs/contrib-clean-room-log.md (lines 710–713, before appending)
Shell commands:
- `git -C E:/orca/workspaces/Proteus/licence-clean-room status --short` (before the merge, after staging)
- `git -C E:/orca/workspaces/Proteus/licence-clean-room rev-parse HEAD test`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room merge --no-commit --no-ff test`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff --name-only --diff-filter=U`
- `grep -n -E "^(<<<<<<<|=======|>>>>>>>)" E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Plugin.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room merge-base HEAD test`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff --stat --cached HEAD`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff HEAD -- Proteus/Gui/StatusWindow.cs Proteus/Plugin.cs`
- `comm -12 <(git -C E:/orca/workspaces/Proteus/licence-clean-room diff --name-only d4734c54 d9d8f7d4 | sort) <(git -C E:/orca/workspaces/Proteus/licence-clean-room diff --name-only d4734c54 test | sort)` (refused by the worktree guard; replaced by the next two)
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff --name-only d4734c54 d9d8f7d4`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff --name-only d4734c54 test`
- `python <session-scratch>/scratchpad-agentA/closure.py test <session-scratch>/scratchpad-agentA/closure_t8.tsv <session-scratch>/scratchpad-agentA/events_t8.tsv <session-scratch>/scratchpad-agentA/seeds_r2.tsv`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room log --format="%h %s" a25a3b56..test -- Proteus/Gui/StatusWindow.cs Proteus/Plugin.cs Proteus/Gui/ModsTab.cs Proteus/Services/CompositorService.cs Proteus/Services/CompositorService.Carriers.cs Proteus/Services/SidecarDiscoveryService.cs Proteus/Interop/PenumbraBridge.cs Proteus/Interop/GlamourerBridge.cs`
- `grep -c -E "^(fef434a7|31a00c0d|fea86dd5|dd53194b|91336ab4)" <session-scratch>/scratchpad-agentA/events_t8.tsv`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff -U0 d4734c54 d9d8f7d4 -- Proteus/Plugin.cs Proteus/Gui/StatusWindow.cs AGENTS.md`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff -U0 d4734c54 test -- AGENTS.md`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room add Proteus/Plugin.cs`
- `dotnet build E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Proteus.csproj -c Release > <session-scratch>/scratchpad-agentA/build_m8.out 2>&1`
- `grep -c -E "error CS|warning CS" <session-scratch>/scratchpad-agentA/build_m8.out`
- `grep -E "Build succeeded|Warning\(s\)|Error\(s\)" <session-scratch>/scratchpad-agentA/build_m8.out`
- `tail -n 2 E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md`; `wc -l E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md`
Sub-agents: none
Conflicts: 1, in `Proteus/Plugin.cs` (the BuildNumber line only). Resolved with the branch's line (the larger number, 1474 against test's 1473). No other resolution line, no new code. AGENTS.md and StatusWindow.cs auto-merged: their test hunks and branch hunks do not overlap.
Files changed on both sides since the merge base: AGENTS.md, `Proteus/Gui/StatusWindow.cs`, `Proteus/Plugin.cs`.
The "other" commits:
- fef434a7, 31a00c0d (merge), fea86dd5, 91336ab4 (merge): each touched `StatusWindow.cs` or `Plugin.cs` only in Solona's own regions (StatusWindow's colour-window highlight handling, Plugin's import list and the log-export construction). None removed or edited a contributor or derived line.
- dd53194b: touched `Plugin.cs` only in the log-export construction and one import line (already on `test` at a25a3b56, so measured in stage 1). No contributor or derived line.
- Closure over `test` to T8 with the stage-1 seeds: identical to the stage-1 result (same 20 replacement events, none from these five commits; 0 tainted merge-resolution lines). Lines arriving from `test` that are contributor or derived: 0. Arriving lines added to the set: 0.
Holes left: none.
B/B2 regions touched by `test`: none. Test's hunks in `StatusWindow.cs` sit in the colour-window drawing (around lines 703–743 of the branch tip), away from B's rebuilt import line and content-index block; test's hunks in `Plugin.cs` (an import line near the top, the log-export construction near line 297) are away from B's and B2's lines (the provider wiring, the refresh parser and handler, the IPC log attach and detach). The BuildNumber line is the only shared line.
Build of the merged tree (`dotnet build W/Proteus/Proteus.csproj -c Release`): succeeded, 0 errors, 0 warnings.
Identifier-grep hits: 0 (this section quotes no code or strings of the removal set)

## QA-lite smoke agent

Start commit: `2c384009`. Extracted from its transcript by agent ID (extract_contrib.py, role b).

B5 in-game smoke check of build 1474 (clean; curl to localhost only). The harness session file test-harness.json did not exist (game not running or plugin not loaded), so no curl was run and all four items are UNVERIFIED (a Blocker per the QA rules; nothing changed in game).

Opened:
- Read docs/agent-rules/adversarial-qa.md
- Read %APPDATA%/XIVLauncher/pluginConfigs/Proteus/test-harness.json
Shell commands:
- none
Sub-agents: none
Grep without line numbers: 0
Shell commands outside the allowlist: 0
forbidden locations in listing/search results: 0

## Web-fetch agent

Start commit: `ef7aa083`. Extracted from its transcript by agent ID (extract_contrib.py, role b).

C2 licence lookups (clean; WebFetch tool only, no repository access). Its three WebFetch calls were to https://bitmiracle.github.io/libtiff.net/help/articles/license.html (200; the New BSD text used in the notices), https://raw.githubusercontent.com/StbSharp/StbImageWriteSharp/master/LICENSE (404) and https://raw.githubusercontent.com/StbSharp/StbImageWriteSharp/main/LICENSE (404). The StbImageWriteSharp licence was then read by the orchestrator with gh api (repository details, root file list, and the README's licence section: Public Domain).

Opened:
- none
Shell commands:
- none
Sub-agents: none
Grep without line numbers: 0
Shell commands outside the allowlist: 0
forbidden locations in listing/search results: 0

## Security review (run 1, void)

Start commit: `4fafbc7c`. Extracted from its transcript by agent ID (extract_contrib.py, role b).

VOID: one shell command outside the allowlist (git diff --stat 7ce8851b HEAD, a summary of an allowed diff; it read nothing forbidden). Its result (no findings) was not used; a fresh security reviewer re-ran the review.

Opened:
- Grep docs/contrib-clean-room-spec.md
- Read docs/contrib-clean-room-spec.md
- Grep docs/ipc-contract.md
- Glob E:/orca/workspaces/Proteus/licence-clean-room
- Read docs/ipc-contract.md
- Read Proteus/Interop/IpcProvider.cs
- Read Proteus/Interop/Ipc/IpcGates.cs
- Grep <claude-projects>/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/bn50preb8.txt
- Read <claude-projects>/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/bn50preb8.txt
- Read Proteus/Interop/Ipc/PenumbraGates.cs
- Read Proteus/Interop/Ipc/PenumbraApiTypes.cs
- Grep E:/orca/workspaces/Proteus/licence-clean-room
- Grep Proteus
- Grep Proteus/Services
- Grep Proteus/Interop
- Grep docs
- Grep Proteus/Gui/StatusWindow.cs
Shell commands:
- git -C E:/orca/workspaces/Proteus/licence-clean-room log --format=%h%x20%an%x20%s test..HEAD
- OUTSIDE ALLOWLIST: git -C E:/orca/workspaces/Proteus/licence-clean-room diff --stat 7ce8851b HEAD
- git -C E:/orca/workspaces/Proteus/licence-clean-room diff 7ce8851b HEAD
- dotnet test E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "(FullyQualifiedName~IpcProviderSpecTests|FullyQualifiedName~IpcContractTests|FullyQualifiedName~HarnessBuildFlagTests|FullyQualifiedName~BridgeAvailabilityTests|FullyQualifiedName~ShippedAssembliesTests|FullyQualifiedName~LicencePublicationTests|FullyQualifiedName~ContribSpecTests)&Speed!=Slow"
Sub-agents: none
Grep without line numbers: 0
Shell commands outside the allowlist: 1
forbidden locations in listing/search results: 0

## Security review

Start commit: `4fafbc7c`. Extracted from its transcript by agent ID (extract_contrib.py, role b).

Clean security review (run 2) of the D8 clean diffs at the merged tip: NO FINDINGS. Spec section 12 rows S1-S10 all confirmed (S1 equality-only lookups; S2 typed System.Text.Json, no polymorphism; S3 master switch on SetColorTable only; S4 no constructor null checks, deferred lambdas; S5 caller's thread, writeGate-locked save; S6 OpenToMod no hop; S7 thread-static echo filter; S8 guards as specified, refresh parse cannot throw; S9 no path from caller input; S10 R8 dispose order). Its targeted dotnet test could not compile because the code-critic was building the same worktree at the same time (file lock), so this review is static. The extraction script's allowlist for the pre-removal diff was missing the three mixed files added at 1b; fixed before this count.

Opened:
- Grep <claude-projects>/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/bbyo44ung.txt
- Grep docs/contrib-clean-room-spec.md
- Read <claude-projects>/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/bbyo44ung.txt
- Read docs/contrib-clean-room-spec.md
- Grep docs/ipc-contract.md
- Read docs/ipc-contract.md
- Grep Proteus
- Grep Proteus/Proteus.csproj
- Grep Directory.Build.props
- Grep <claude-projects>/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/bse9snm0n.txt
- Grep Proteus/Gui/StatusWindow.cs
- Grep Proteus/Services
Shell commands:
- git -C E:/orca/workspaces/Proteus/licence-clean-room log --format=%h%x20%an%x20%s test..HEAD
- git -C E:/orca/workspaces/Proteus/licence-clean-room diff 7ce8851b HEAD
- git -C E:/orca/workspaces/Proteus/licence-clean-room diff test...7ce8851b^ -- . ':!Proteus/Interop/IpcProvider.cs' ':!Proteus/Interop/PenumbraBridge.cs' ':!Proteus/Interop/GlamourerBridge.cs' ':!Proteus/Plugin.cs' ':!Proteus/Services/SidecarDiscoveryService.cs' ':!Proteus/Gui/StatusWindow.cs' ':!Proteus/Gui/ModsTab.cs' ':!Proteus/Services/CompositorService.Carriers.cs' ':!Proteus/Services/CompositorService.cs' ':!proteus.slnx' ':!samples'
- dotnet test E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/Proteus.Tests.csproj -c Release --filter "(FullyQualifiedName~IpcProviderSpecTests|FullyQualifiedName~IpcContractTests|FullyQualifiedName~BridgeAvailabilityTests|FullyQualifiedName~HarnessBuildFlagTests|FullyQualifiedName~ShippedAssembliesTests|FullyQualifiedName~LogAnonymizerTests|FullyQualifiedName~LogExportServiceTests|FullyQualifiedName~HarnessBindingsWiringTests|FullyQualifiedName~ContribSpecTests)&Speed!=Slow"
Sub-agents: none
Grep without line numbers: 0
Shell commands outside the allowlist: 0
forbidden locations in listing/search results: 0

## Orchestrator (step 8)

- **The merge.**
  - `T8` is `test` at `1e2c5cc6`, equal to `origin/test` after `git fetch`. The worktree guard blocks `git -C E:/repos/Proteus`, so `test` was read through this worktree's shared refs. `P8` is `d9d8f7d4`.
  - The guard counted 13 commits. 8 are bump-only and 5 are "other": fef434a7, 31a00c0d, fea86dd5, dd53194b and 91336ab4. So the merge went through the A3 route.
  - A3 merged with `--no-commit`. There was 1 conflict, the BuildNumber line (A3 kept 1474), no holes and no B/B2 regions touched. The closure added 0 lines. The orchestrator committed the merge unviewed as `4fafbc7c`.
- **`restored-check.ps1 -Merge 4fafbc7c`:**
  - Result: added 42, markers 0, unmatched 0, blank 3, bump 0, other 39. All 39 "other" lines are in `docs/contrib-clean-room-log.md`: A3's own section, staged into the merge commit. No code file has a line new against both parents.
  - The first two merge-mode runs were void because of script bugs: the wrong diff form, and the removal manifest still being checked. Both runs printed counts only.
- **The proof on the merged tip:** WIDE-A, WIDE-B and the Records filter (minus the participant test, until the gate sections exist) gave 1935 passed, 0 failed, 0 skipped.
- **Blame at `4fafbc7c`, `-w -M -C`:** contributor non-trivial 0, with no change.
- **Code-critic, round 1** (merged tip `4fafbc7c`): **FAIL**, on tests and records only. Every spec §12 row and contract §4 row was confirmed in the code, and R-1 and R-8 hold. Its seven fix items were answered in the next commit:
  1. **The gate layer had no tests.** New: `IpcGatesTests`. It covers A1, A2, A4, A5, A12, A13 and A15, plus `IpcLog`.
  2. **Argument order and defaults at the gate were unpinned.** `IpcGatesTests.Gates_pass_the_contracts_argument_order_and_defaults` covers P17, P8, P21, P18, P24 and G5.
  3. **Contract §6 and `ApplyFlagEx` were unchecked.** New: `IpcContractTests.Harness_gates_match_the_API` and `ApplyFlagEx_constants_match_the_API`.
  4. **AC-7 had no tag.** It is now tagged and asserted in `Log_reports_no_contributor_lines`, case B.
  5. **EDGE-50 had no tag.** `No_file_claims_AGPL_for_Proteus_code` now carries EDGE-50.
  6. **Participant sections were missing.** Added: "QA-lite smoke agent" and "Web-fetch agent". The record test now requires those two and A3.
  7. **The notices contradicted themselves.** The interoperability note now excepts the Glamourer.Api enum names and values.
- **Its NEEDS_DECISION items, decided by the orchestrator in auto mode (safer or recommended option):**
  1. **The draft fixture.** It is deleted. `ReadmeTermsTests` now pins `LICENSE`, minus its status line, to the draft's SHA-256 (`397567b1…fa0b`), so "the draft is gone" holds.
  2. **The aliased API ProjectReferences in the test project**, instead of `ReferenceOutputAssembly="false"`, are accepted. They are test-only, never shipped, and keep the API types from clashing with the owned enums.
  3. **The wholesale `MayMentionAgpl` exclusion of `XivLiveMesh/NOTICE.md`** is accepted. Its AGPL mentions are earlier copies and a research reference, and `XivLiveMesh_licence_follows_the_usage_check` pins that it names the Proteus Licence.
  4. **Planning participants recorded once**, not in one section each, is accepted as the documented deviation.
  5. **AC-8 and AC-10 in game** are verified by the process: the step-10 sequence, and QA where it can run. The in-game part is UNVERIFIED.
  6. **Local paths in the committed docs** are redacted: `<session-scratch>`, `%APPDATA%`, `<claude-projects>`, `%USERPROFILE%`.
- **The licence date.** The date changed to 2026-11-18, so `LICENSE` line 3 now says "in force from 2026-11-18". `XivLiveMesh/LICENSE` was re-copied.
- **Code-critic, round 2** (`021053f8`): every round-1 FAIL item passed, except ND-6. The two security-review sections, appended after the redaction, still held `<claude-projects>`-style local paths. Fix:
  - those 5 paths are redacted;
  - a new `ContribCleanRoomRecordTests.Record_docs_carry_no_local_user_paths` keeps the four record docs free of `X:\Users\` paths.
- **Its NEEDS_DECISION** (P9 and P6 argument order still unpinned): option (a), the cheap one, was taken. `Gates_pass_the_contracts_argument_order_and_defaults` now also pins P9's (inherit, enabled, priority, options) tuple and P6's (ignoreInheritance, ignoreTemporary) order.
- **Its open question** (EDGE-18 on two tests): rev 10's EDGE-18 is the closure removing Solona's own lines, which matches `Body_shape_signature_follows_the_spec`. The tag on `Harness_gates_match_the_API` is removed; it keeps AC-10.
- **Tests at this fix:** `IpcGatesTests`, `IpcContractTests` and `ContribCleanRoomRecordTests` (minus the participant test) gave 27 passed, 0 failed.
