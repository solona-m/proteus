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

### Stage 1c (spec gap for step 2: bridge construction facts)

Opened: W/Proteus/Interop/PenumbraBridge.cs (lines 315–334); Grep tool, content mode, `void CheckAvailability|public Guid\? GetPlayerCollectionId|GetModSettings\(Guid|OnMainThread\(` in W/Proteus/Interop/PenumbraBridge.cs (-A 22, first 120 lines). Shell commands: `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/idcheck.py`; `git -C E:/orca/workspaces/Proteus/licence-clean-room status --short`. Added three interface-fact rows to spec §9.3 (both bridge constructors, and how a substitute plugin interface makes Penumbra absent). Sub-agents: none. Identifier-grep hits: 0.

### Stage 1d (gated failure `ContribSpecTests.Glamourer_lock_warnings_name_the_item_and_slot`)

Opened: W/Proteus.Tests/ContribSpecTests.cs (lines 150–233); Grep tool, content mode, `Glamourer_lock_warnings_name_the_item_and_slot` in W/Proteus.Tests/ContribSpecTests.cs. Shell commands: `python -c "import re,sys;sys.stdout.reconfigure(encoding='utf-8');t=open(r'C:\Users\solon\AppData\Local\Temp\claude\861b008b34a06d0d\5936bcc1-21b0-4a70-8eb3-e82d4006af47\scratchpad\step2.trx',encoding='utf-8').read();i=t.find('Glamourer_lock_warnings_name_the_item_and_slot\" ');j=t.find('</UnitTestResult>',i);print(t[i:j][:4000])"`; `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/idcheck.py`; `git -C E:/orca/workspaces/Proteus/licence-clean-room status --short`. Answer: spec error, §8.2 and §9.3 amended (the accessory setter's accepted slot codes, and the earlier refusal of any other slot argument, were missing). Sub-agents: none. Identifier-grep hits: 0.

### Stage 2 (removal)

Base: branch HEAD 233424a0 (mixed-set files identical to 71d450fb, where the §1 set was measured). Committed by the orchestrator as R0 = 7ce8851b.
Opened: no file with Read; the Read and Write tools touched only my own folder (the scripts `headset.py`, `remove.py`, `verify_manifest.py` and the draft of this sub-part).
Shell commands:
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff --stat 71d450fb HEAD -- Proteus proteus.slnx samples`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room status --short` (before the removal, after it, and at the end)
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/headset.py`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/remove.py`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room rm -q -r Proteus/Interop/IpcProvider.cs proteus.slnx samples/ExampleMultiGroupOverlayMod`
- `git -C E:/orca/workspaces/Proteus/licence-clean-room diff --numstat`
- `dotnet build E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Proteus.csproj -c Release > C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/build_r.out 2>&1`
- `python C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/verify_manifest.py C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/build_r.out`
- `grep -o -E "[A-Za-z.]+\.cs\([0-9]+,[0-9]+\): error CS[0-9]+" C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA/build_r.out | sort -u | grep -o -E "CS[0-9]+" | sort | uniq -c`
- `grep -n -o -E "CLEAN-ROOM HOLE: spec §[0-9]+" E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Interop/PenumbraBridge.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Interop/GlamourerBridge.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Plugin.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Services/SidecarDiscoveryService.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Gui/StatusWindow.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Gui/ModsTab.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Services/CompositorService.Carriers.cs E:/orca/workspaces/Proteus/licence-clean-room/Proteus/Services/CompositorService.cs`
- Appending this sub-part: `tail -n 3 E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md`; `grep -n -E "^## |^### " E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md`; Read of the log lines 187–192 and of my draft; then the folder deletion `rm -rf "C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentA"`, which the permission system denied (not retried).
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

Folder: C:\Users\solon\AppData\Local\Temp\claude\861b008b34a06d0d\5936bcc1-21b0-4a70-8eb3-e82d4006af47\scratchpad-agentD\
Opened:
- docs/contrib-clean-room-plan.md (Read, lines 171-370; also Grep for headings)
- docs/contrib-clean-room-spec.md (Read, lines 349-573; also Grep for headings)
- scratchpad-agentD/measure.py, idgrep.py, section.md (written by D)
Shell commands:
- mkdir -p "C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentD"
- git -C E:/orca/workspaces/Proteus/licence-clean-room diff --stat 9ae845ea^ 9ae845ea
- python -I "C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentD/measure.py" "C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentD"   (runs, inside the script: git -C W diff -U0 7ce8851b^ 7ce8851b; git -C W diff -U0 9ae845ea^ 9ae845ea -- Proteus)
- python -I -c "import json;d=json.load(open(r'C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentD/result.json',encoding='utf-8'));[print(m['file'].split('/')[-1],m['line'],'|',m['norm'],'|',m['removed_at'][:3]) for m in d['matches']]"
- git -C E:/orca/workspaces/Proteus/licence-clean-room show 9ae845ea:Proteus/Gui/StatusWindow.cs | sed -n '1355,1420p'
- git -C E:/orca/workspaces/Proteus/licence-clean-room show 9ae845ea:Proteus/Gui/ModsTab.cs | grep -n "pri_\|_priorityEdits\|var pri\|bool active"
- git -C E:/orca/workspaces/Proteus/licence-clean-room show 9ae845ea:Proteus/Services/SidecarDiscoveryService.cs | sed -n '470,545p'
- git -C E:/orca/workspaces/Proteus/licence-clean-room show 9ae845ea:Proteus/Interop/PenumbraBridge.cs | grep -n "private readonly [A-Z][A-Za-z]* [a-z][A-Za-z]*;\|try { .*} *$"
- git -C E:/orca/workspaces/Proteus/licence-clean-room diff -U0 9ae845ea^ 9ae845ea -- Proteus/Gui/StatusWindow.cs Proteus/Gui/ModsTab.cs Proteus/Services/SidecarDiscoveryService.cs | grep "^@@"
- git -C E:/orca/workspaces/Proteus/licence-clean-room diff -U1 9ae845ea^ 9ae845ea -- Proteus/Interop/GlamourerBridge.cs Proteus/Interop/IpcProvider.cs Proteus/Plugin.cs Proteus/Services/CompositorService.Carriers.cs Proteus/Interop/PenumbraBridge.cs
- python -I "C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentD/idgrep.py" "C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentD"   (runs, inside the script: the two diffs above, git ls-tree -r --name-only 9ae845ea^ -- Proteus, git show 9ae845ea^:<each .cs file>)
- python -I -c "exec(open(r'C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentD/idgrep.py').read().replace('print(\"identifier-grep hits in B','print(sorted(hits));print(\"x'))" "C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentD"
- python -I -c "exec(open(r'C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentD/idgrep.py').read().replace('only_removed = rem_ids - spec_ids - pre','low={x.lower() for x in spec_ids|pre};only_removed={x for x in rem_ids if x.lower() not in low}'))" "C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentD"
- tail -n 30 E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md
- python -I -c "open(r'E:/orca/workspaces/Proteus/licence-clean-room/docs/contrib-clean-room-log.md','a',encoding='utf-8').write(open(r'C:/Users/solon/AppData/Local/Temp/claude/861b008b34a06d0d/5936bcc1-21b0-4a70-8eb3-e82d4006af47/scratchpad-agentD/section.md',encoding='utf-8').read())"
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

Phases delivered: A

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
- Glob C:/Users/solon/AppData/Roaming/XIVLauncher/addon/Hooks/dev
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
