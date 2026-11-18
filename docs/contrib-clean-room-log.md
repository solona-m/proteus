# Clean-room log: Caraxi's and AzureRavie's code

This feature makes **no licence change**: `LICENSE` stays the GNU AGPL v3 and `docs/LICENSE-DRAFT.md` stays a draft.
It replaces every line Caraxi and AzureRavie wrote with code written by an agent who never saw theirs, so that a later
relicensing is possible. The method extends the PbdFile precedent (`docs/pbd-clean-room.md`, `docs/pbd-clean-room-log.md`)
from one whole file to code mixed into files Solona otherwise wrote. The plan is `docs/contrib-clean-room-plan.md`
(revision 7) and the spec is `docs/contrib-clean-room-spec.md`.

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

Folder: C:\Users\solon\AppData\Local\Temp\claude\861b008b34a06d0d\5936bcc1-21b0-4a70-8eb3-e82d4006af47\scratchpad-agentA\
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
- `mkdir -p "C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA"`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room shortlog -sne test`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room log test --format="%H %P | %an <%ae> | %ad | %s" --date=short --author=Caraxi --author=AzureRavie`
- `python --version`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room ls-tree -r test --name-only | wc -l`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_all.py test "-w -M -C" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_C.tsv`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_all.py test "-w -M -C -C" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_CC.tsv` (background)
- `cut -f1,3 C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_C.tsv | sort | uniq -c`
- `grep -v "^samples/" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_C.tsv | grep -v "^Proteus/Interop/IpcProvider.cs"`
- `grep -a -v "^samples/" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_C.tsv | grep -a -v "^Proteus/Interop/IpcProvider.cs"`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room log test --format="== %h %s" --numstat -M --author=Caraxi --author=AzureRavie`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show --format="== %h %s" -M c1191abd e5704ca4 08cd11b2 9a38b682 78bd004a 41683f4e -- Proteus`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show --format="== %h %s" -M 3f9c1498 aeb00b3b 3b4cdc23 6207f78b`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/closure.py test C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/closure_r1.tsv C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/events_r1.tsv`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/annotate.py test C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/closure_r1.tsv samples` (twice: once failed on console encoding, once after the fix)
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show --format="== %h %ae %ad %s" --date=short -U0 -w -M 87ae2e3d -- Proteus/Gui/StatusWindow.cs Proteus/Interop/GlamourerBridge.cs` (output saved by the harness to a tool-results file; only its first 2 KB were shown)
- `cat C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tasks/b9sr1awyl.output`
- `cut -f1,3 C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_CC.tsv | sort | uniq -c`
- `grep -a -v -E "^(samples/|proteus.slnx|Proteus/Interop/(IpcProvider|GlamourerBridge|PenumbraBridge)\.cs|Proteus/Plugin\.cs|Proteus/Services/SidecarDiscoveryService\.cs|Proteus/Gui/StatusWindow\.cs)" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_CC.tsv | cut -f4,5,6,7 | sort | uniq -c | sort -rn`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room blame -w -M -C -L 300,345 test -- Proteus/Services/CompositorService.Carriers.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "equipped|_lastRingInjectTick|_lastGlassesInjectTick = |attempt|ATTEMPT|sinceInject > GlassesInjectCooldownMs" 87ae2e3d -- Proteus/Services/CompositorService.Carriers.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "equipped|_lastRingInjectTick|_lastGlassesInjectTick = |attempt|ATTEMPT|sinceInject > GlassesInjectCooldownMs|locked" 87ae2e3d^ -- Proteus/Services/CompositorService.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show 87ae2e3d:Proteus/Services/CompositorService.Carriers.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room grep -n -E "PushColor\(ImGuiCol.Text, ImGui.GetColorU32\(ImGuiCol.TextDisabled\), !active|Selectable\(\\$\"\{entry.ModName\}|OpenToMod|##pri_|IsItemDeactivatedAfterEdit|PriorityOverride = pri" 87ae2e3d -- Proteus/Gui/ModsTab.cs Proteus/Gui/StatusWindow.cs`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show 87ae2e3d:Proteus/Gui/ModsTab.cs`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/closure.py 87ae2e3d^ C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/closure_pre_split.tsv C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/events_pre_split.tsv`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/annotate.py 87ae2e3d^ C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/closure_pre_split.tsv samples`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/closure.py test C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/closure_r2.tsv C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/events_r2.tsv C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/seeds_r2.tsv`
- `cat C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/events_r2.tsv`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show --stat --format="== %h %ad %s" --date=short 6c20041f efcc50fa c3d37d7d dfaf1d75 2e63f830 94c04ab0 a3842daf 897624ce d5215199`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room show --format="== %h" -w 6c20041f -- Proteus/Interop/GlamourerSignals.cs Proteus/Interop/GlamourerBridge.cs`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/annotate.py test C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/closure_r2.tsv samples`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/removal_set.py` (twice: once failed on a binary row, once after the fix)
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/restore_check.py`
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
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_all.py HEAD "-w -M -C -C" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_CC_head.tsv` (background)
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_all.py HEAD "-w -M -C" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_C_head.tsv`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/closure.py HEAD C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/closure_r2_head.tsv C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/events_r2_head.tsv C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/seeds_r2.tsv`
- `grep -v -i "silent" "C:/Users/solon/.claude/projects/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/toolu_01NZrmd4nVgV39R7PdhS7sxe.txt"`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/edge5.py test` (output saved by the harness to a tool-results file)
- `awk -F'\t' '$7=="True" && $4 ~ /^Proteus\// && length($3) >= 12' "C:/Users/solon/.claude/projects/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/beewitjih.txt"`
- `grep -n -B8 -A4 "TextureLoader.ParseMtrlBytes" E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ContentImportTests.cs`
- `grep -n -B12 -A3 "LoadTexAsRgba(" E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/RowHoverWiringTests.cs`
- `grep -n -B10 -A3 "log.Warning(" E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ContentImcEditsTests.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/IssueSweepDWiringTests.cs`
- `awk -F'\t' '$7=="True" && $4 ~ /^Proteus\// && $4 !~ /IpcProvider/ && length($3) < 12' "C:/Users/solon/.claude/projects/E--orca-workspaces-Proteus-licence-clean-room/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tool-results/beewitjih.txt"` (output saved by the harness to a tool-results file; only its first 2 KB were shown)
- `cat C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/tasks/bp45niosx.output`
- `grep -n -A12 "private void DisposeServices()" E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ColorsetSaveOnReleaseTests.cs`
- `grep -n "new ResolvedOverlay\|new(.*Descriptor\|new ResolvedContent\|GroupOrder" E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/FaceUvDoublingTests.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/WomensArtOnMenTests.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ShellMensLayoutTests.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/ContentPieceSelectionTests.cs`
- `sed -n 26,32p E:/orca/workspaces/Proteus/licence-clean-room/Proteus.Tests/FaceUvDoublingTests.cs`
- `grep -a -E "^Proteus.Tests/(ModCreationTests|OnionImportTests|ContentImportTests.RingImcGroups|LocalizationTests|PbdFileTests)\.cs" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_CC.tsv`
- `grep -a -v -E "^(samples/|proteus.slnx)" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_CC.tsv | cut -f7 | sort | uniq -c | sort -rn`
- `diff <(grep -a -o -E "^[^	]+	[0-9]+	[^	]+" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_CC.tsv | sort) <(grep -a -o -E "^[^	]+	[0-9]+	[^	]+" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_CC_head.tsv | sort)` (the bracket expressions hold a literal tab)
- `grep -a -E "^Proteus/(Services/CompositorService.Carriers|Gui/ModsTab|Interop/ShellCoverageFade)\.cs" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_CC.tsv`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/cc_only.py`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/cc_table.py`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room rev-parse test HEAD`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/idcheck.py` (run four times while rewording the spec; the last two runs printed TOTAL 0)
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
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/classify1b.py test`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/classify1b.py HEAD`
- `grep -a -E "^Proteus/Gui/ColorTableEditor" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_CC.tsv C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/blame_C.tsv`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/class_table.py`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/splice.py`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/idcheck.py`
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
