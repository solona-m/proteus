# Clean-room spec: the contributors' surviving code (contrib-clean-room)

Written by Agent A (dirty side) under `docs/contrib-clean-room-plan.md` (revision 7), rules D1, D2, D3, D5 and D6. It states facts about the current behaviour of every line in the removal set, so that someone who never sees that code can rebuild it and test it. Interface facts (the IPC labels, the command words, and the names and signatures in §9) are exact. Everything else is described, never quoted.

Revisions measured: `test` at `a25a3b5678c369278010c8d4bb7f43bf1161a94c` (the tip when this spec was written) and the branch `solona-m/licence-clean-room` at `71d450fb2c5cae21405c87e1201db3a3c143f916` (base `d4734c54`). The removal set is the same at both. Only `Proteus/Plugin.cs` line numbers differ, because `test` has seven newer commits (build 1467) that touch none of the removed lines.

## 1. What is replaced

### Contributors and their commits

| Contributor | Email (from `git shortlog -sne test`) | Commits |
|---|---|---|
| Caraxi | `git@caraxian.com` | 11: `3f9c1498`, `aeb00b3b`, `3b4cdc23`, `09c65ad0`, `41683f4e`, `68a718d8`, `6207f78b`, `78bd004a`, `9a38b682`, `e5704ca4`, `c1191abd` (the last two are the same change on two branches, later merged) |
| AzureRavie | `268794375+AzureRavie@users.noreply.github.com` | 1: `08cd11b2` |

Solona's emails: `solonamods@gmail.com`, `50025762+solona-m@users.noreply.github.com`, `solona.modenkind@gmail.com`. The bot commits `8c787a80` (dependabot) and `d046b185` (github-actions) are recorded, not replaced. Whole-tree blame gives dependabot 1 line, which is not in the set.

### Categories (as plan D2 defines them)

| Category | Meaning here |
|---|---|
| Direct | Blame gives the line to a contributor in `-w -M -C` or in `-w -M -C -C` (union of both modes) |
| Derived | The closure's hunk rule: added in a non-merge hunk that removed a contributor or derived line |
| Carried over | Contributor content moved, with edits, into another file |
| Merge-inherited | New against every parent of a merge and caught by the hunk rule there |
| Whole-file | Every line of `Proteus/Interop/IpcProvider.cs`, `proteus.slnx` and `samples/ExampleMultiGroupOverlayMod/**` |
| Restored | A Solona line that a contributor commit replaced, put back word for word from that commit's parent (stage 2; candidates counted here) |

### Totals (test tip)

| Category | Lines | Files |
|---|---|---|
| Whole-file | 268 (IpcProvider.cs 150, proteus.slnx 10, the sample folder 108 counted as blame lines in 6 files, 2 of them PNG) | 8 |
| Direct, outside whole files, both modes | 40 | 8 |
| Direct, outside whole files, `-w -M -C -C` only | 212 | 179 |
| Derived, outside whole files | 51 | 6 |
| Derived inside IpcProvider.cs (already whole-file) | 9 | 1 |
| Carried over | 19 | 2 |
| Merge-inherited | 0 | 0 |
| Restored (candidates, final count fixed at stage 2) | 13 | 4 |
| Direct, all files, `-w -M -C` | 294 (Caraxi 282, AzureRavie 12) | |
| Direct, all files, `-w -M -C -C` | 504 (Caraxi 492, AzureRavie 12) | |
| Direct, all files, union of both modes | 506 | |

Inside the whole files: IpcProvider.cs has 137 direct lines (`-w -M -C`; 135 in `-C -C`), 9 derived and 4 other Solona lines. proteus.slnx has 9 direct lines and 1 Solona line. All 108 sample-folder blame lines are direct.

### Per file: the known six, the whole files and the extra files with code lines

| File | Direct (both modes) | Direct (`-C -C` only) | Derived | Carried over | Merge-inherited | Restored candidates | Sensitive Area |
|---|---|---|---|---|---|---|---|
| `Proteus/Interop/IpcProvider.cs` | whole file, 150 | | | | | 0 | yes (Interop) |
| `proteus.slnx` | whole file, 10 | | | | | 0 | no |
| `samples/ExampleMultiGroupOverlayMod/**` (6 files) | whole files, 108 | | | | | 0 | no |
| `Proteus/Interop/PenumbraBridge.cs` | 9 | 0 | 0 | 0 | 0 | 0 | yes (Interop) |
| `Proteus/Interop/GlamourerBridge.cs` | 12 | 0 | 8 | 0 | 0 | 0 | yes (Interop) |
| `Proteus/Plugin.cs` | 6 | 0 | 1 | 0 | 0 | 1 | no |
| `Proteus/Services/SidecarDiscoveryService.cs` | 6 | 0 | 8 | 0 | 0 | 4 | no |
| `Proteus/Gui/StatusWindow.cs` | 4 | 0 | 28 | 0 | 0 | 0 | no |
| `Proteus/Gui/ModsTab.cs` (extra) | 0 | 1 | 0 | 11 | 0 | 4 | no |
| `Proteus/Services/CompositorService.Carriers.cs` (extra) | 0 | 2 | 2 | 8 | 0 | 4 | no |
| `Proteus/Services/CompositorService.cs` (extra) | 0 | 0 | 4 | 0 | 0 | 0 | no |
| `Proteus/Gui/ColorTableEditor.cs` (extra) | 1 | 0 | 0 | 0 | 0 | 0 | no |
| `Proteus/Gui/ColorTableEditor.GlowFooter.cs` (extra) | 1 | 0 | 0 | 0 | 0 | 0 | no |
| `Proteus/Gui/ColorTableEditor.SubRowPanel.cs` (extra) | 1 | 1 | 0 | 0 | 0 | 0 | no |

What each group of lines is, in brief (full behaviour in §3 to §8):

| File | Lines | What they are |
|---|---|---|
| IpcProvider.cs | all | Proteus's public IPC provider (§2, §3) |
| Plugin.cs | 6 direct, 1 derived | the provider's field, construction and disposal; the refresh command's argument parsing and the quiet return (§3 R9, §4) |
| PenumbraBridge.cs | 9 direct | the open-window call gate field, its construction, and the open-a-mod method with its trailing blank line (§7) |
| GlamourerBridge.cs | 12 direct, 8 derived | the lock-conflict branches in three methods, and the equipment-change echo filter (§8) |
| SidecarDiscoveryService.cs | 6 direct, 8 derived | the overlay record's fields after the descriptor, two fields of the content record, two record constructions, and the no-selection rule (§5) |
| StatusWindow.cs | 4 direct, 28 derived | one import line; the material-reading block of the content colour-index lookup (§6.3). The block is in the set only because the 2026-09 file split aligned it with three brace lines of the old mod-name code |
| ModsTab.cs | 11 carried over, 1 direct | the clickable mod name and the priority drag (§6.1, §6.2); one import line |
| CompositorService.Carriers.cs | 8 carried over, 2 derived, 2 direct | the glasses and ring equip cooldown charged at the attempt (§8.1); two import lines |
| CompositorService.cs | 4 derived | the body-shape signature's body (§6.4). In the set only because the same file split put it in a hunk that removed the carried-over cooldown lines |
| ColorTableEditor.*.cs | 4 direct | import lines |

### The `-w -M -C -C`-only lines (176 further files)

These 208 lines, plus the 4 in ModsTab.cs, CompositorService.Carriers.cs and ColorTableEditor.SubRowPanel.cs above, are attributed to Caraxi only because `-C -C` searches every file of the commit that created each file. They are namespace import directives, three blank-and-namespace preambles, and three game-path string literals in tests. They carry no behaviour of their own. Each is rebuilt by re-adding the same import, namespace declaration, blank line or literal (§9.5).

Kinds: L = import of `System.Linq` (170 lines in all), G = `System.Collections.Generic` (19), R = `Dalamud.Interface.Utility.Raii` (5 here plus the 4 counted above), J = `System.Text.Json` (3), S = `Proteus.Services` (3), N = the file-scoped namespace declaration `Proteus.Interop` (3), B = blank line (6), P = the test material game-path literal of §9.5 (3). Line numbers are at the test tip.

| File | Lines | Line and kind | Sensitive Area |
|---|---|---|---|
| `Proteus.Tests/AutoRefitDecisionsTests.cs` | 1 | 3L | no |
| `Proteus.Tests/AutoRefitPartsTests.cs` | 1 | 3L | no |
| `Proteus.Tests/BodyRetargetTests.RelaxSink.cs` | 1 | 3L | no |
| `Proteus.Tests/BodyRetargetTests.cs` | 1 | 3L | no |
| `Proteus.Tests/ClientStructsProbe.cs` | 1 | 3L | no |
| `Proteus.Tests/ContentImportTests.RingImcGroups.cs` | 2 | 1G 2L | no |
| `Proteus.Tests/CrotchWeightsTests.cs` | 1 | 3L | no |
| `Proteus.Tests/CutOpenSkinTests.cs` | 1 | 3L | no |
| `Proteus.Tests/DeepClearTests.cs` | 1 | 3L | no |
| `Proteus.Tests/DesignRestoreSweepTests.cs` | 1 | 3L | no |
| `Proteus.Tests/FaceUvRewriteTests.cs` | 1 | 3L | no |
| `Proteus.Tests/Gen2LayoutRefitTests.cs` | 1 | 3L | no |
| `Proteus.Tests/GeometryTogglesTests.cs` | 2 | 3L 4J | no |
| `Proteus.Tests/HairPortBakerTests.cs` | 1 | 3L | no |
| `Proteus.Tests/HairPortScalpLocalTests.cs` | 1 | 3L | no |
| `Proteus.Tests/HairScalpPushTests.cs` | 1 | 3L | no |
| `Proteus.Tests/KeepEndsReshapeTests.cs` | 2 | 1G 2L | no |
| `Proteus.Tests/KeepShapeFlatCardTests.cs` | 1 | 3L | no |
| `Proteus.Tests/KeepShapeReleaseTests.cs` | 1 | 3L | no |
| `Proteus.Tests/LayerOrderTests.cs` | 2 | 1G 2L | no |
| `Proteus.Tests/LocalizationTests.cs` | 2 | 3L 4J | no |
| `Proteus.Tests/MeshVolumeSolveTests.Mirror.cs` | 1 | 3L | no |
| `Proteus.Tests/MeshVolumeSolveTests.MirrorSkin.cs` | 1 | 3L | no |
| `Proteus.Tests/MeshVolumeSolveTests.RigidGuard.cs` | 1 | 3L | no |
| `Proteus.Tests/MeshVolumeSolveTests.SkinMoves.cs` | 1 | 3L | no |
| `Proteus.Tests/MeshVolumeSolveTests.Stretch.cs` | 1 | 3L | no |
| `Proteus.Tests/MeshVolumeSolveTests.cs` | 1 | 3L | no |
| `Proteus.Tests/ModCreationTests.cs` | 2 | 208P 282P | no |
| `Proteus.Tests/ModMoveServiceTests.cs` | 1 | 3L | no |
| `Proteus.Tests/ModelAttributeWriterTests.cs` | 1 | 3L | no |
| `Proteus.Tests/ModelShapeWriterTests.cs` | 1 | 3L | no |
| `Proteus.Tests/MoveAllSelectionTests.cs` | 1 | 3L | no |
| `Proteus.Tests/NailGrade.cs` | 1 | 3L | no |
| `Proteus.Tests/NavelBridgeTests.cs` | 2 | 3L 4J | no |
| `Proteus.Tests/NecklineFloatTests.cs` | 1 | 3L | no |
| `Proteus.Tests/OnionImportTests.cs` | 1 | 559P | no |
| `Proteus.Tests/OpenPatchAuthoredTests.cs` | 1 | 3L | no |
| `Proteus.Tests/OpenSkinTests.cs` | 2 | 1G 2L | no |
| `Proteus.Tests/OutfitOrderTests.cs` | 2 | 2G 3L | no |
| `Proteus.Tests/PbdFileTests.cs` | 2 | 2G 3L | no |
| `Proteus.Tests/PolygonSelectionTests.cs` | 2 | 1G 2L | no |
| `Proteus.Tests/RacialModelBakeTests.cs` | 1 | 3L | no |
| `Proteus.Tests/RacialRouteTests.cs` | 1 | 3L | no |
| `Proteus.Tests/RefitNippleSmoothTests.cs` | 1 | 3L | no |
| `Proteus.Tests/RimTurnGuardTests.cs` | 1 | 3L | no |
| `Proteus.Tests/SeamWeightsTests.cs` | 1 | 3L | no |
| `Proteus.Tests/SkinFollowTests.cs` | 2 | 1G 2L | no |
| `Proteus.Tests/SkinIntersections.cs` | 1 | 3L | no |
| `Proteus.Tests/SkinPoke.cs` | 1 | 3L | no |
| `Proteus.Tests/SkinThroughClothTests.cs` | 2 | 1G 2L | no |
| `Proteus.Tests/SkinTuckSheet.cs` | 1 | 3L | no |
| `Proteus.Tests/SkinTuckTests.cs` | 1 | 3L | no |
| `Proteus.Tests/SkinlessTargetTests.cs` | 1 | 3L | no |
| `Proteus.Tests/SnapPushTests.cs` | 1 | 3L | no |
| `Proteus.Tests/SourceSmallclothesHoleTests.cs` | 1 | 3L | no |
| `Proteus.Tests/StackDepthTests.cs` | 1 | 3L | no |
| `Proteus.Tests/SyntheticChest.cs` | 1 | 3L | no |
| `Proteus.Tests/SyntheticCrotch.cs` | 1 | 3L | no |
| `Proteus.Tests/SyntheticFinger.cs` | 1 | 3L | no |
| `Proteus.Tests/SyntheticSkeleton.cs` | 1 | 3L | no |
| `Proteus.Tests/TestHarness/FakeGame.Bindings.cs` | 1 | 3L | no |
| `Proteus.Tests/TestHarness/HarnessChangesHttpTests.cs` | 1 | 3L | no |
| `Proteus.Tests/TestHarness/HarnessChangesTests.cs` | 1 | 3L | no |
| `Proteus.Tests/TestHarness/ModelJoinTests.cs` | 1 | 3L | no |
| `Proteus.Tests/ToeCapFootlessHostTests.cs` | 2 | 1G 2L | no |
| `Proteus.Tests/ToeLineTests.cs` | 1 | 3L | no |
| `Proteus.Tests/TurnFitTests.cs` | 1 | 3L | no |
| `Proteus.Tests/UvBakeStep0LocalTests.cs` | 1 | 3L | no |
| `Proteus.Tests/WallPairTests.cs` | 1 | 3L | no |
| `Proteus/Gui/AutoRefitPanel.cs` | 1 | 3L | no |
| `Proteus/Gui/BindingsTab.cs` | 1 | 3L | no |
| `Proteus/Gui/BodyRetargetPanel.CrossSex.cs` | 1 | 7R | no |
| `Proteus/Gui/BodyRetargetPanel.PartBodies.cs` | 2 | 3L 5R | no |
| `Proteus/Gui/BodyRetargetPanel.Race.cs` | 1 | 8R | no |
| `Proteus/Gui/HatCompatPanel.cs` | 1 | 3L | no |
| `Proteus/Gui/MatcapPanel.cs` | 1 | 7R | no |
| `Proteus/Gui/MatcapPanelModel.cs` | 1 | 3L | no |
| `Proteus/Gui/PartViewport.cs` | 1 | 3L | no |
| `Proteus/Gui/PartsPanel.SkinMoves.cs` | 1 | 4R | no |
| `Proteus/Gui/RowPickerView.cs` | 1 | 3L | no |
| `Proteus/Gui/SizeSetMadeOn.cs` | 1 | 3L | no |
| `Proteus/Gui/SizeSetPanel.cs` | 1 | 3L | no |
| `Proteus/Gui/ToeCapToggle.cs` | 1 | 3L | no |
| `Proteus/Interop/ShellCoverageFade.cs` | 4 | 11S 12B 13N 14B | yes |
| `Proteus/Interop/ShellNormalGhost.cs` | 4 | 11S 12B 13N 14B | yes |
| `Proteus/Interop/SkinDiffuseGlow.cs` | 4 | 11S 12B 13N 14B | yes |
| `Proteus/ModelRace.cs` | 2 | 2G 3L | no |
| `Proteus/Models/ContentPack.cs` | 1 | 3L | no |
| `Proteus/RenderModeInference.cs` | 2 | 1G 2L | no |
| `Proteus/Services/AutoRefit/AutoRefitDecisions.cs` | 1 | 3L | no |
| `Proteus/Services/AutoRefit/AutoRefitTargets.cs` | 1 | 3L | no |
| `Proteus/Services/AutoRefit/KeepEnds.Studio.cs` | 2 | 2G 3L | no |
| `Proteus/Services/AutoRefit/KeepEnds.cs` | 1 | 3L | no |
| `Proteus/Services/BodyBridge.BridgeSolution.cs` | 1 | 3L | no |
| `Proteus/Services/BodyBridge.CrotchFlattening.cs` | 1 | 3L | no |
| `Proteus/Services/BodyBridge.EnvelopeSolution.cs` | 1 | 3L | no |
| `Proteus/Services/BodyBridge.NippleSmoothing.cs` | 1 | 3L | no |
| `Proteus/Services/BodyBridge.cs` | 1 | 3L | no |
| `Proteus/Services/BodyCorrespondence.Identity.cs` | 1 | 3L | no |
| `Proteus/Services/BodyMaterialCatalog.cs` | 1 | 3L | no |
| `Proteus/Services/BodyRetarget.Adornments.cs` | 1 | 3L | no |
| `Proteus/Services/BodyRetarget.Crotch.cs` | 1 | 3L | no |
| `Proteus/Services/BodyRetarget.CutOpenSkin.cs` | 2 | 1G 2L | no |
| `Proteus/Services/BodyRetarget.Ends.cs` | 1 | 3L | no |
| `Proteus/Services/BodyRetarget.Nipples.cs` | 1 | 3L | no |
| `Proteus/Services/BodyRetarget.SeamWeights.cs` | 1 | 3L | no |
| `Proteus/Services/BodyRetarget.SkinSwap.cs` | 1 | 3L | no |
| `Proteus/Services/BodyRetarget.SourceWorn.cs` | 1 | 3L | no |
| `Proteus/Services/BodyRetarget.Standoff.cs` | 1 | 3L | no |
| `Proteus/Services/BodyRetarget.Tuck.cs` | 1 | 3L | no |
| `Proteus/Services/BodyRetarget.Variants.cs` | 1 | 3L | no |
| `Proteus/Services/BodyRetarget.Weights.cs` | 1 | 3L | no |
| `Proteus/Services/BrushTransfer.cs` | 1 | 3L | no |
| `Proteus/Services/CompositorService.BlendStats.cs` | 1 | 3L | no |
| `Proteus/Services/CompositorService.Trigger.cs` | 1 | 3L | no |
| `Proteus/Services/ContentGlowRow.cs` | 2 | 2G 3L | no |
| `Proteus/Services/CrossSexRefit.cs` | 1 | 3L | no |
| `Proteus/Services/CrossSexRefitReport.cs` | 2 | 1G 2L | no |
| `Proteus/Services/DrawnModelPaths.cs` | 1 | 3L | no |
| `Proteus/Services/EffectHostPlanner.cs` | 1 | 3L | no |
| `Proteus/Services/HairPort/HairPortScanSchedule.cs` | 1 | 3L | no |
| `Proteus/Services/HairPort/HairPortSkeleton.cs` | 1 | 3L | no |
| `Proteus/Services/HairPort/HairScalpPush.cs` | 1 | 3L | no |
| `Proteus/Services/HairPort/HairSkeletonTable.cs` | 2 | 3G 4L | no |
| `Proteus/Services/HatCompatSolve.HatFit.cs` | 1 | 3L | no |
| `Proteus/Services/HatCompatSolve.cs` | 1 | 3L | no |
| `Proteus/Services/HatCompatWatcher.cs` | 1 | 3L | no |
| `Proteus/Services/InertModDiagnosis.cs` | 1 | 3L | no |
| `Proteus/Services/LuminisImportService.LuminisPreview.cs` | 1 | 3L | no |
| `Proteus/Services/MeshMath.cs` | 1 | 3L | no |
| `Proteus/Services/MeshVolumeSolve.cs` | 1 | 3L | no |
| `Proteus/Services/ModelAttributeWriter.cs` | 1 | 3L | yes |
| `Proteus/Services/ModelPartReader.PartRead.cs` | 1 | 3L | no |
| `Proteus/Services/ModelPartReader.cs` | 1 | 3L | no |
| `Proteus/Services/NavelBridge.Filling.cs` | 1 | 3L | no |
| `Proteus/Services/NavelBridge.cs` | 1 | 3L | no |
| `Proteus/Services/OtherRaceRefit.Below.cs` | 1 | 3L | no |
| `Proteus/Services/OtherRaceRefit.Est.cs` | 1 | 3L | no |
| `Proteus/Services/OtherRaceRefit.cs` | 1 | 3L | no |
| `Proteus/Services/OtherRaceWearer.cs` | 1 | 3L | no |
| `Proteus/Services/OverlayBlend.cs` | 1 | 3L | no |
| `Proteus/Services/OverlayOverrideBag.cs` | 1 | 3L | no |
| `Proteus/Services/PolygonSelection.cs` | 1 | 3L | no |
| `Proteus/Services/RacialModelBake.cs` | 1 | 3L | no |
| `Proteus/Services/RedrawHold.cs` | 1 | 3L | no |
| `Proteus/Services/RetargetInputs.cs` | 1 | 3L | no |
| `Proteus/Services/SecondSkinService.HostChooser.cs` | 1 | 3L | no |
| `Proteus/Services/SecondSkinWriter.CapMissFill.cs` | 1 | 3L | yes |
| `Proteus/Services/SecondSkinWriter.CapStreamCopy.cs` | 1 | 3L | yes |
| `Proteus/Services/SecondSkinWriter.CapWeld.cs` | 1 | 3L | yes |
| `Proteus/Services/SecondSkinWriter.Connectors.cs` | 1 | 3L | yes |
| `Proteus/Services/SecondSkinWriter.CoverageMask.cs` | 1 | 3L | yes |
| `Proteus/Services/SecondSkinWriter.MeshEmitter.Reskin.cs` | 1 | 3L | yes |
| `Proteus/Services/SecondSkinWriter.NailBeds.cs` | 1 | 3L | yes |
| `Proteus/Services/SecondSkinWriter.NailSockets.cs` | 1 | 3L | yes |
| `Proteus/Services/SecondSkinWriter.Skinning.cs` | 1 | 3L | yes |
| `Proteus/Services/SecondSkinWriter.ToeRegion.cs` | 1 | 3L | yes |
| `Proteus/Services/SecondSkinWriter.Welding.cs` | 1 | 3L | yes |
| `Proteus/Services/SizeSet/SizeSetRefit.Outcomes.cs` | 2 | 1G 2L | no |
| `Proteus/Services/SizeSet/SizeSetRefit.Source.cs` | 1 | 3L | no |
| `Proteus/Services/SkeletonRemap.cs` | 1 | 3L | no |
| `Proteus/Services/SkinTagTransfer.cs` | 1 | 3L | no |
| `Proteus/Services/SkinThroughCloth.cs` | 1 | 3L | no |
| `Proteus/Services/StaleEffectImport.cs` | 1 | 3L | no |
| `Proteus/Services/UVRemapService.Layouts.cs` | 1 | 3L | no |
| `Proteus/Services/UvBakeChestWarp.cs` | 1 | 3L | no |
| `Proteus/Services/UvTransferQuality.cs` | 1 | 3L | no |
| `Proteus/Services/VertexColorWriter.cs` | 1 | 3L | no |
| `Proteus/Services/WomensArtOnMen.cs` | 1 | 3L | no |
| `Proteus/ShellSurface.cs` | 1 | 3L | no |
| `Proteus/TestHarness/GameHarnessAdapter.cs` | 1 | 3L | yes |
| `Proteus/TestHarness/HairPortJob.cs` | 1 | 3L | yes |
| `Proteus/TestHarness/HarnessChanges.cs` | 1 | 3L | yes |
| `Proteus/TestHarness/HarnessStudio.cs` | 1 | 3L | yes |
| `Proteus/TestHarness/ModelJoin.cs` | 1 | 3L | yes |
| `Proteus/TestHarness/RestoreJob.cs` | 1 | 3L | yes |

69 of these files are test files under `Proteus.Tests/`, outside Agent B's allowlist. Their lines are rebuilt by whoever the plan lets fix tests (D20).

### The closure

The walk visits every commit of `git rev-list --reverse --topo-order --parents` up to the tip. Diffs are `git diff -U0 -w -M` against each parent. Lines of contributor commits are seeded as contributor lines. Merge lines take their status from the parent they map to. Lines new against every parent get the hunk rule against each parent.

| Round | What was done | Lines added (surviving at the tip) |
|---|---|---|
| 1 | The walk from the 12 contributor commits | 58 derived: IpcProvider.cs 9, StatusWindow.cs 28, GlamourerBridge.cs 8, Plugin.cs 1, CompositorService.cs 4, SidecarDiscoveryService.cs 8. No pure-deletion hunk and no deleted file held a tainted line, so the five-commit carried-over check had nothing to inspect. Merge resolution lines tainted: 0 |
| 2 | Carried-over seeds added at `87ae2e3d` (the 2026-09-17 split of the four giant files). That commit moved the mod-name and priority code from StatusWindow.cs into ModsTab.cs, and the glasses and ring cooldown code from CompositorService.cs into CompositorService.Carriers.cs, each edited. Its hunks had replacement lines, so the five-commit rule did not trigger. Seeded anyway under EDGE-30: 11 lines in ModsTab.cs, 10 in CompositorService.Carriers.cs | 21: 19 carried over (ModsTab.cs 11, Carriers 8), plus 2 derived in Carriers by `6c20041f` |
| 3 | The round-2 replacement events re-inspected (`6c20041f` in Carriers and GlamourerBridge.cs) | 0. `6c20041f` also added `Proteus/Interop/GlamourerSignals.cs` and the per-slot cooldown helper in `Proteus/Services/CarrierDecisions.cs`. Neither holds moved contributor text: each is a new helper that a derived line calls. They are not in the set (judgment recorded here) |

Commits whose hunks produced derived lines: `dfaf1d75`, `a3842daf`, `897624ce`, `94c04ab0`, `efcc50fa`, `c3d37d7d`, `87ae2e3d`, `d5215199`, `2e63f830`, `6c20041f`.

### Restored-line candidates (verified: blame gives each line to `solonamods@gmail.com` at the source commit's parent)

| Source commit | Source path at parent | Line at parent | Lands at (test tip) | Fits |
|---|---|---|---|---|
| `c1191abd` (= `e5704ca4`) | `Proteus/Plugin.cs` | 428 | Plugin.cs, where the three argument-parsing lines are removed | yes |
| `3b4cdc23` | `Proteus/Services/SidecarDiscoveryService.cs` | 122 | SidecarDiscoveryService.cs 529 | yes |
| `41683f4e` | same file | 26, 98, 128 | 56 (record field), 480, 536 | yes (the record closes after the restored field) |
| `9a38b682` | `Proteus/Gui/StatusWindow.cs` | 113, 114 | ModsTab.cs 162 to 168 | yes |
| `78bd004a` | same file | 120, 121 | ModsTab.cs 177 to 180 | to be judged at stage 2 |
| `08cd11b2` | `Proteus/Services/CompositorService.cs` | 2676, 2677, 2678 | CompositorService.Carriers.cs 323 to 329 | yes |
| `08cd11b2` | same file | 2835 | CompositorService.Carriers.cs 511 | yes (same text as the derived line it replaces) |
| `08cd11b2` | same file | 2837 | none | no: the field it stamped no longer exists, so it becomes a hole |

AzureRavie's three replaced debug-log lines in GlamourerBridge.cs survive unchanged at the tip, so they need no restore.

### The slnx regeneration

The current `proteus.slnx` lists, in order: `Glamourer/Glamourer.Api/Glamourer.Api.csproj`, `Penumbra.Api/Penumbra.Api.csproj`, `Proteus/Proteus.csproj` and `XivLiveMesh/XivLiveMesh.csproj`. The two API projects each carry a platform mapping to x64; the other two carry none. `Proteus.Refit/Proteus.Refit.csproj` and `Proteus.Tests/Proteus.Tests.csproj` are not in it. The XivLiveMesh entry is Solona's line. Tests use the file's existence to find the repository root (12 test files).

### The sample folder

`samples/ExampleMultiGroupOverlayMod/` holds `meta.json`, `default_mod.json`, `group_001_overlays.json`, `Proteus/metadata.json`, `Proteus/overlays/left_d.png` and `Proteus/overlays/right_d.png`. It is example data with no code and no behaviour. It is deleted and not remade.

## 2. Public IPC surface

All labels are registered through the Dalamud plugin interface's call-gate providers. A subscriber reaches them with the same label and the same generic type arguments.

| Label | Kind | Arguments | Returns |
|---|---|---|---|
| `Proteus.ApiVersion` | Func | none | `(int, int)`: always `(1, 0)` |
| `Proteus.GetOverlays` | Func | none | `List<(string ModDirectory, string Name, int Priority, Dictionary<string, List<string>>? Options)>` |
| `Proteus.GetActiveOverlays` | Func | none | `List<(string ModDirectory, string Name, int Priority, Dictionary<string, List<string>>? Options)>` |
| `Proteus.Recomposite` | Action | none | none (provider generic type `ICallGateProvider<object>`, registered as an action) |
| `Proteus.GetColorTable` | Func | `string modDirectory, string? group, string? option` | `string` |
| `Proteus.SetColorTable` | Func | `string modDirectory, string? group, string? option, string colorTableJson` | `bool` |

Exact provider generic types: `ICallGateProvider<(int, int)>`, `ICallGateProvider<List<…tuple…>>` for both overlay lists, `ICallGateProvider<object>`, `ICallGateProvider<string, string?, string?, string>` and `ICallGateProvider<string, string?, string?, string, bool>`. The tuple's element names are compile-time only. Its element types and order are the contract: string, string, int, nullable dictionary of string to list of string. The label prefix is the plugin's namespace name, Proteus, followed by a dot.

## 3. IPC behaviour

| Row | Label or area | Behaviour |
|---|---|---|
| R1 | `Proteus.ApiVersion` | Returns major 1, minor 0. Touches nothing else. |
| R2 | `Proteus.GetOverlays` | Reads the compositor's last-discovered entry list once, as a snapshot. Keeps only entries whose enabled flag is true, in list order. For each kept entry it returns its directory, display name and priority. Options is null when the entry's metadata has no option groups. Otherwise it is a new dictionary with one key per option group in metadata order (the key is the group's Penumbra group name), each mapping to the names of all of that group's options in metadata order, whether or not they are selected. The dictionary uses the default case-sensitive key comparison. A fresh list is built on every call. |
| R3 | `Proteus.GetActiveOverlays` | Same entry filter, order and first three fields as R2. Options is null when the metadata has no option groups. Otherwise it comes from the discovery service's active-overlay resolution for that entry (§5), which yields one resolved overlay per overlay descriptor. Those are grouped by their option-group field. The key is the group name, or the empty string when it is null; groups appear in order of first appearance. Each group's list holds that descriptor's option name (the empty string when null), once per resolved descriptor. An option with three descriptors therefore appears three times. When the metadata has both top-level overlays and option groups, every top-level descriptor resolves with no group and no option, giving one key, the empty string, whose list holds one empty string per descriptor. When option groups exist but nothing is selected (or Penumbra's settings cannot be read), Options is an empty dictionary, not null. The resolution runs on the caller's thread and may call Penumbra over IPC and read the mod's `meta.json` file. |
| R4 | `Proteus.Recomposite` | Calls the compositor's recomposite trigger once, with a reason tag that identifies an IPC request (different from R6's tag) and every other argument at its default (200 ms delay, forced). Returns nothing. Does not check the master switch. |
| R5 | `Proteus.GetColorTable` | Everything runs inside one exception guard. It finds the first entry in the last-discovered list whose directory equals `modDirectory` under invariant-culture case-insensitive comparison. The enabled flag is not checked; a null `modDirectory` matches nothing. No entry gives the empty string. When `group` is non-null: a null `option`, or metadata with no option groups, gives the empty string. Otherwise it takes the first group whose Penumbra group name equals `group`, then the first option in it whose name equals `option`, both invariant-culture case-insensitive. A missing group or option, or a matching option whose colour rows are null, gives the empty string. Otherwise it returns that option's colour rows serialised by System.Text.Json with default options. When `group` is null, `option` is ignored: null top-level colour rows give the empty string, otherwise the top-level rows are serialised the same way. Any exception logs one error-level line carrying the exception and returns the empty string. Does not check the master switch. Reads only; writes nothing. |
| R6 | `Proteus.SetColorTable` | Returns false at once, before any lookup, when `Configuration.PluginEnabled` is false. Otherwise everything runs inside one exception guard. Entry lookup is as in R5; no entry gives false. The new rows are null when `colorTableJson` is null, empty or whitespace. Otherwise they are deserialised by System.Text.Json with default options as a list of colour-table row presets; the JSON literal null also yields null. This happens before the group checks, so malformed JSON returns false through the guard even when the group would also fail. When `group` is non-null: a null `option` gives false; metadata with no option groups gives false; group and option are then found as in R5, and a missing one gives false. Otherwise the matched option's colour rows are replaced by the new rows (null clears them). When `group` is null, the entry's top-level colour rows are replaced instead (null clears them; `option` is ignored). The change is made in place on the shared metadata object of the snapshot entry. Then the discovery service saves the entry's metadata, ignoring its success result. Then the compositor's recomposite trigger is called once, with a reason tag identifying an IPC colour change and default delay and force. Then it returns true. Any exception logs one error-level line carrying the exception and returns false. |
| R7 | Registration | The constructor stores its compositor, discovery, configuration and log arguments, with no null checks. It then gets a provider for each label and registers each one immediately, in this order: ApiVersion (func), GetOverlays (func), GetActiveOverlays (func), Recomposite (action), GetColorTable (func), SetColorTable (func). Nothing else is subscribed. A registration failure throws out of the constructor. |
| R8 | Disposal | Unregisters in this order: ApiVersion (func), GetOverlays (func), GetActiveOverlays (func), Recomposite (action), GetColorTable (func), SetColorTable (func). No exception guard. Holds no other resources. |
| R9 | Plugin wiring | The plugin keeps the provider in a read-only field. It constructs it after the preset service and the overlay edit router and before the sphere-map preview, passing the plugin interface, the compositor, the discovery service, the configuration and the log, in that order. During teardown it disposes it right after the fonts and before the Glamourer design watcher, with no try/catch around that call. |
| R10 | Threads | Every function answers on the thread of the plugin that invokes the call gate. None marshals to the framework thread or takes a lock. R3, R5 and R6 read the last-discovered list through its volatile getter. R6 mutates the metadata and writes the file on the caller's thread. |
| R11 | Unknown name, case, before ready, after dispose | Unknown directory, group or option: as R5 and R6 say. Case: directory, group and option lookups are case-insensitive; the dictionary keys returned by R2 and R3 are case-sensitive. Before the compositor's first discovery the list is empty: R2 and R3 return empty lists, R5 the empty string, R6 false. After disposal the labels are unregistered, so a subscriber's call fails in Dalamud, not in Proteus. |
| R12 | Exceptions in R2 and R3 | No guard. Duplicate group names or a null group name in metadata, a null options list, or an exception from the active-overlay resolution propagates out of the call-gate invocation to the caller. |

## 4. The refresh command

The chat command handler first trims the whole argument string. The refresh branch is taken when that string starts with the word `refresh` under ordinal case-insensitive comparison. That branch test is Solona's line and stays.

| Rule | Behaviour |
|---|---|
| Tokens | The text after the first seven characters (the length of the word `refresh`) is trimmed, then split on the single space character. Consecutive spaces give empty tokens. Tabs are not separators. |
| Full | True when any token equals `full`, `full` with one leading hyphen, or the word all, under ordinal case-insensitive comparison. |
| Silent | True when any token equals `silent` or `silent` with one leading hyphen, under ordinal case-insensitive comparison. |
| Order and other tokens | The words may come in any order, may repeat, and may sit among unknown tokens, which are ignored. |
| Prefix quirk | Because the branch test is a prefix test, text glued to the word refresh counts as a token. Refresh followed directly by `silent` is silent; refresh followed directly by `full` is full. |
| Effects | Always, silent or not: the compositor's refresh-and-recomposite with the full flag, then the Studio parts panel's list refresh. If silent, the handler returns before printing. If not silent, it prints one chat line: the plugin's bracketed prefix followed by a localised message. With full, the message says everything is being rebuilt and recomposited and that this takes longer. Without, it says recompositing is starting. Those print lines and both localisation keys are Solona's and stay. |
| No arguments | A bare refresh is neither full nor silent: a plain refresh with the plain message. |

## 5. Overlay discovery: multi-select groups and per-option tables

The resolved-overlay record pairs one overlay descriptor with what governs it. Its positional parameters, in order: the descriptor; the nullable list of colour-table row presets; the nullable option-group name; the nullable option name; the group order, defaulting to the largest int. The removed lines are everything after the descriptor parameter, including a documentation comment on the group-order parameter (described here, not quoted). That comment explains the group order: it is the group's position in the group list of the mod's `meta.json`, as Penumbra numbers it; a smaller number outranks a larger one; wherever an outranking group shows, the compositor hides the outranked groups under it; and overlays outside every group get the largest int.

The resolved-content record (imported content packs) mirrors it. Its second and third positional parameters, the nullable colour rows and the nullable option-group name, are in the removal set. Its other parameters stay.

| Rule | Behaviour |
|---|---|
| Top-level overlays | When the metadata's top-level overlay list is non-empty, every descriptor in it resolves with the top-level colour rows, no group, no option and the default group order. Option groups are then ignored. |
| No selection, any group type | Inside the option-group walk, a group whose selection from Penumbra is missing or empty contributes nothing and the walk moves on to the next group. This holds for single-select and multi-select groups alike, and also when Penumbra's settings could not be read at all. There is no fallback to the first option. (The empty-group diagnostic recorded before it is Solona's and stays.) |
| Selected options | Each selected option (matched by name, ordinal case-insensitive, Solona's lines) contributes one resolved overlay per descriptor. The colour rows are the option's own when non-null, else the top-level rows (Solona's line). The group name is the group's Penumbra group name, the option name is the option's name, and the group order is the group's index from `meta.json`, or the largest int when Penumbra does not know the group. |
| Per-option tables over IPC | R5 and R6 read and write an option's own colour rows by group and option name. A null option table falls back to the top-level rows when composited (row above), but R5 reports it as the empty string rather than falling back. |

## 6. Status window: mod name and priority

### 6.1 Mod name (Mods tab, carried over)

| Rule | Behaviour |
|---|---|
| Colour | While drawing the name, the text colour is pushed to the current style's disabled-text colour only when the row's mod is not active. Active means the row's checkbox value this frame, after any click, so the dimming follows a toggle at once. The push is scoped to the name item alone. |
| Widget | The name is a selectable item whose visible label is the mod's display name. The item's hidden ID suffix is the mod's directory, so two mods with the same display name stay distinct. Default selectable flags and size. |
| Click | When the selectable reports a click, it calls the Penumbra bridge's open-a-mod method (§7) with the row's mod directory. Nothing else happens on click. |
| After it | The existing hover tooltip that follows the name (Solona's) applies to this selectable. |

### 6.2 Priority drag (Mods tab, carried over)

| Rule | Behaviour |
|---|---|
| Widget | An integer drag control with a hidden label made of a fixed priority prefix and the mod directory, bound to the row's working priority value. Its drag speed is 0.1 per pixel. Ctrl+click still types a value. |
| While dragging | Every frame the control reports a changed value, that value is stored as the row's pending priority, keyed by mod directory, in the tab's pending-priority dictionary. The working value shown next frame is the pending one while it exists (Solona's line). |
| On release | When the control reports it was deactivated after an edit, the commit block runs. That condition line and its opening brace are carried over; the block's body is Solona's and stays: drop the pending value, read the live priority, write the new one to Penumbra, log, recomposite. |

### 6.3 Content colour-index lookup (derived lines in StatusWindow.cs)

This sits inside the method that works out which colour rows a content material's index texture selects, cached per material. The removed lines run from just after the material file is known to exist up to just before the parse-dependent extras are recorded. The result starts as unknown (no rows, no sub-row) before this block (Solona's line).

| Step | Behaviour |
|---|---|
| 1 | Read the whole material file into memory, then parse its texture and sampler table. |
| 2 | Not parsed: log one warning naming the material's relative path and the mod directory, explaining that the grid's row filter cannot be worked out for it. The result stays unknown. |
| 3 | Parsed but no colour table: no rows, no sub-row. The state is follows-hair-colour when the material's shader name equals the hair shader name (ordinal case-insensitive), otherwise no-colour-table. |
| 4 | Parsed, has a colour table, and the index sampler path is null or empty: rows = the single default content row, sub-row A, state no-sampler. |
| 5 | Otherwise resolve the index texture's game path to a file path through the window's content-texture resolver. If a path is found and it decodes to RGBA, the result is the index scan of those pixels. If that scan's state is scanned and the file is known to be compressed (the uncompressed check returns false; unknown does not count), the state becomes compressed and nothing else changes. If no path is found or the decode fails, log one warning naming the mod directory and the index game path, explaining that the texture was not found or not readable; the result stays unknown. |
| Wrapper | Any exception in these steps is caught by the method's existing guard (Solona's), which logs and keeps the result. |
| Comments in the block | Four comment lines: why the index is looked up through Penumbra, why only the parsed flag can tell an absent index apart from a material that failed to parse, why a material without rows keeps its own shader (hair-shader pieces keep following the hair colour), and why a compressed index is still used but flagged. Described, not quoted. |

### 6.4 Body-shape signature (derived lines in CompositorService.cs)

| Input | Output |
|---|---|
| Null or empty map | The empty string |
| Otherwise | Keys ignored. For each value set: its strings sorted ordinally and joined with a comma. Those joined strings sorted ordinally and joined with a vertical bar. |

## 7. Opening a mod in Penumbra

| Rule | Behaviour |
|---|---|
| Field | The Penumbra bridge holds one read-only Penumbra open-main-window call-gate subscriber. It is built in the bridge constructor from the plugin interface, after the redraw-object subscriber and before the game-object resource-paths subscriber. Building it does not need Penumbra to be loaded. |
| Method | A public method takes the mod directory and returns nothing. |
| Penumbra absent | When the bridge's availability flag is false, it returns at once without calling anything and without logging. |
| Call | Otherwise it invokes the subscriber with Penumbra's Mods tab and the given directory (no mod-name argument), on the calling thread, with no marshalling. Penumbra's returned error code is ignored. A missing or unknown directory is left to Penumbra. |
| Failure | Any exception is caught and logged as one error-level line carrying the exception, saying the open-main-window call failed. Never throws. |
| Disposal | Nothing to dispose: the bridge's dispose method does not touch this subscriber. |
| Callers (Solona's, unchanged) | The Mods tab name click (§6.1), the body-retarget panel's open-in-Penumbra button, and the content, emissive-skin, eye, Luminis and onion import services and mod creation after they create a mod. Some of these run off the framework thread. |
| Trailing line | A blank line follows the method before the bridge's dispose method. |

## 8. Glamourer: equip cooldown and lock conflicts

### 8.1 Equip cooldown counted from the attempt (CompositorService.Carriers.cs, carried over and derived)

| Carrier | Behaviour |
|---|---|
| Invisible glasses | Inside the glasses reconcile, when a shell is wanted on the facewear host: the equip branch runs only when the head-model snapshot is known, no head model is worn, and the time since the last glasses inject is strictly greater than the 5000 ms glasses cooldown. At exactly 5000 ms the separate retry branch (Solona's) applies instead. The branch attempts the equip through the framework-thread glasses helper and keeps the success flag. The last-inject tick is stamped to now right after the attempt, whatever the result (that stamp line is Solona's and stays). If the attempt failed, the reconcile returns at once: no injected flag, no log, no recomposite. Otherwise the rest of the branch (Solona's) runs: set the injected flag, log, and recomposite after 600 ms unless the shell is already hosted. |
| Ring and accessory carriers | Inside the per-slot carrier reconcile, after the decision says equip and the ask is counted: the slot's cooldown is charged at the tick captured at the start of this pass (the cooldown helper's charge, slot and tick). This happens before the attempt and whatever its result. Then the equip is attempted through the framework-thread accessory helper with the carrier's item id and the slot. On success, the block that follows runs (Solona's: mark injected, clear the removal record, log, maybe schedule the checked retry). On failure, the ask is undone (Solona's line). The ring cooldown window is 5000 ms per slot. |
| Effect | A slot locked by another plugin refuses every equip, but each refusal still starts a cooldown. Proteus retries at most once per cooldown window per carrier instead of on every redraw. |

### 8.2 Lock-conflict logging (GlamourerBridge.cs, direct and derived)

The three methods share one pattern. When Glamourer's answer is not success: InvalidKey logs one warning-level line, any other code logs the existing debug line with the code (Solona's), and the method returns false.

| Method | Warning content on InvalidKey |
|---|---|
| Reapply the player's state | Explains that Glamourer refused the reapply because a different plugin has locked the character's state, and that a complete redraw is used in its place. No parameters. A one-line comment above the check explains what the InvalidKey code means here. |
| Set the glasses bonus item | Names the item id and explains that a different plugin has locked the Glasses slot. |
| Set an accessory item (used for rings, wrists and neck) | Names the slot and the item id and explains that a different plugin has locked that slot. |

| Rule | Behaviour |
|---|---|
| Frequency | One warning per refused call. No de-duplication and no once-per-session latch. |
| Content | Item ids and Glamourer slot names only. No character name, mod name or collection name. |
| Threads | Logged on the calling thread. The carrier helpers call these on the framework thread. |
| Other paths | Thrown exceptions are logged by the methods' existing guards (Solona's) and are not lock warnings. The general gear-item setter has no lock warning and returns Glamourer's raw code (§9). |

### 8.3 Equipment-change echo filter (GlamourerBridge.cs, direct and derived)

In the Glamourer state-changed handler, for the local player only, when the change type is Equip or BonusItem (that branch test is Solona's):

| Rule | Behaviour |
|---|---|
| Comments kept with the code | Three comment lines give the reasons: Proteus's own reapply produces one Equip signal for every slot, which would otherwise loop into composite after composite; the accepted price is that a player's equip landing inside that window waits for the next trigger; and Proteus's own carrier write produces the same signal on the writing thread while the call is still running. (Described, not quoted.) |
| Decision | The equipment-change event is suppressed unless the signal helper says the change raises an equipment change. The helper gets three things: the change type; whether the bridge's own-reapply echo window is open (the same check the finalization handler uses: an explicit until-tick not yet reached, or less than 250 ms since the bridge's last own reapply); and whether the thread-static carrier-write depth counter is above zero. |
| When suppressed | If the carrier-write depth is above zero, one debug line names the change type and notes that the signal came from Proteus's carrier write and was skipped. An echo-window suppression logs nothing extra. Either way the handler returns without raising the equipment-change event. |
| Otherwise | The equipment-change event is raised (Solona's line), then the handler returns. |
| Trailing line | A blank line follows the decision block. |

## 9. Seams the tests need

### 9.1 Per §3 row: reachable by a unit test without a new seam?

| Row | Reachable without a new seam | How, or what is missing |
|---|---|---|
| R1 | yes | Build the provider over a substitute `IDalamudPluginInterface` whose `GetIpcProvider<(int, int)>(string)` returns a substitute provider, capture the delegate passed to `RegisterFunc` and invoke it. The other constructor arguments are not touched by R1 and may be null. |
| R2 | no | Needs a compositor whose `LastDiscovered` holds chosen entries. It has a private setter, and no test constructs `CompositorService`. A seam is needed to supply the entry list. |
| R3 | no | As R2. It also needs `SidecarDiscoveryService.ResolveActiveOverlays` to see Penumbra selections. The flat top-level case works with a discovery service built over a null bridge; the option-group case needs a Penumbra bridge. |
| R4 | no | Needs a compositor whose `TriggerRecomposite` can be observed. It is not virtual. |
| R5 | partly | With a null compositor, the lookup throws inside the guard, so the error-log-and-empty-string path is reachable. Every lookup outcome needs the R2 seam. |
| R6 | partly | The master-switch-off path is reachable: a `Configuration` with `PluginEnabled` false returns false before touching anything. With the switch on and a null compositor, the guard path returns false and logs. Every other outcome needs the R2 seam, plus an observable save and recomposite. |
| R7 | yes | A substitute plugin interface records each `GetIpcProvider` label and generic type, and each `RegisterFunc` and `RegisterAction` call, in order. |
| R8 | yes | As R7: call `Dispose()` and check each `UnregisterFunc` and `UnregisterAction`. |
| R9 | no | Plugin construction is not unit-testable. A source-order check is the only route. |
| R10 | yes, for R1 | Invoke the captured delegate on a non-framework thread. The rest needs the R2 seam. |
| R11 | partly | After-dispose and the before-ready empty-list cases need the R2 seam (an empty entry list). Case rules need it too. |
| R12 | no | Needs the R2 seam. |

### 9.2 Interface facts: the provider

| Name | Signature or fact |
|---|---|
| Class | `public class IpcProvider : IDisposable`, namespace `Proteus.Interop`, file `Proteus/Interop/IpcProvider.cs` |
| Constructor | `public IpcProvider(IDalamudPluginInterface pluginInterface, CompositorService compositor, SidecarDiscoveryService discovery, Configuration config, IPluginLog log)` |
| Dispose | `public void Dispose()` |
| Dalamud | `IDalamudPluginInterface.GetIpcProvider<…>(string name)`; `ICallGateProvider<…>.RegisterFunc(…)`, `.RegisterAction(Action)`, `.UnregisterFunc()`, `.UnregisterAction()` |
| Compositor | `CompositorService.LastDiscovered` (`List<OverlayEntry>`, public volatile getter, private setter); `CompositorService.TriggerRecomposite(string reason, int delayMs = 200, bool force = true, …)`; `CompositorService.RefreshAndRecomposite(bool full = false)` |
| Discovery | `SidecarDiscoveryService.ResolveActiveOverlays(OverlayEntry entry)` returns `List<ResolvedOverlay>`; `SidecarDiscoveryService.SaveMetadata(OverlayEntry entry)` returns `bool` |
| Config | `Configuration.PluginEnabled` (`bool`) |
| Model | `OverlayEntry(string ModDirectory, string ModName, int Priority, bool Enabled, ProteusMetadata Metadata, string SidecarRoot)`; `ProteusMetadata.Overlays`, `.OptionGroups` (`List<OverlayOptionGroup>?`), `.ColorTableRows` (`List<ColorTableRowPreset>?`); `OverlayOptionGroup.PenumbraGroupName`, `.Options`; `OverlayOption.Name`, `.ColorTableRows`, `.Overlays`; `ColorTableRowPreset`; `OverlayDescriptor`; `ContentPiece` |
| Records | `public record ResolvedOverlay(OverlayDescriptor Descriptor, List<ColorTableRowPreset>? ColorTableRows, string? OptionGroup, string? Option, int GroupOrder = int.MaxValue)`. Tests build it with four positional arguments. `public record ResolvedContent(ContentPiece Piece, List<ColorTableRowPreset>? ColorTableRows, string? OptionGroup, string? Option, int GroupOrder = int.MaxValue, …)`; the parameters after the group order are unchanged. |

### 9.3 Interface facts: Penumbra, Glamourer and the cooldown

| Name | Signature or fact |
|---|---|
| Open a mod | `public void OpenToMod(string modDirectory)` on `PenumbraBridge`; `PenumbraBridge.IsAvailable`; Penumbra API subscriber `OpenMainWindow` (namespace `Penumbra.Api.IpcSubscribers`), built with `new OpenMainWindow(pluginInterface)`; enum value `TabType.Mods` (namespace `Penumbra.Api.Enums`) |
| Lock logging lives in | `public bool ReapplyPlayerState()`, `public bool SetGlasses(ulong itemId)` and `public bool SetAccessory(ulong itemId, string slotName)` on `GlamourerBridge`. `public bool SetRing(ulong itemId, bool leftHand)` reaches it through `SetAccessory`. |
| Equip cooldown lives in | No bridge method. The cooldown is in the compositor's carrier code (`Proteus/Services/CompositorService.Carriers.cs`): the glasses reconcile and the per-slot carrier reconcile, both private, reached only from composites, the redraw hook and the scheduled carrier retry. They call the bridge through the private framework-thread helpers `SetGlassesOnFramework(ulong itemId)` and `SetAccessoryOnFramework(ulong itemId, string slot)`. |
| Harness route | `POST /actions/equip` calls `public GlamourerApiEc? SetGearItem(string slotName, ulong itemId, byte stain1, byte stain2)`. That method calls Glamourer directly and returns its code. It has no lock warning and no cooldown, so the route reaches neither the §8.1 cooldown nor the §8.2 lock logging. |
| Glamourer API | `GlamourerApiEc.Success`, `GlamourerApiEc.InvalidKey`; `StateChangeType.Equip`, `StateChangeType.BonusItem`; event `GlamourerBridge.LocalPlayerEquipmentChanged` |
| Signal helper (Solona's, stays) | `internal static bool GlamourerSignals.RaisesEquipmentChange(StateChangeType type, bool withinReapplyEcho, bool ownCarrierWriteOnThisThread)` |
| Cooldown helper (Solona's, stays) | `CarrierCooldowns.Charge(string slot, long nowTick)`; the compositor's instance of it is the field `_carrierCooldowns` |
| Surrounding names the rebuilt carrier lines use (declared outside the set) | `MetSnapshotKnown`, `AnyMetWorn()`, `sinceInject` (local), `GlassesInjectCooldownMs` (5000), `RingInjectCooldownMs` (5000), `g.ItemId` (glasses identity local), `r.ItemId` and `ringSlot` (carrier locals), `now` (the tick captured at the pass start) |
| Reachability | Lock logging: reachable at the bridge level without a new seam only if the test provides an `IDalamudPluginInterface` whose generic `GetIpcSubscriber` returns call-gate fakes (for example a dispatch proxy), an `IObjectTable` with a non-null `LocalPlayer`, and an `IPluginLog` substitute. The gates' generic type arguments are not stated here, because the submodule sources are off-limits to both sides. Echo filter: the same, raising the state-changed event through the faked event gate. Cooldown: no; it needs a seam (the compositor cannot be built in a test). Open a mod: the Penumbra-absent path is reachable with a bridge over a substitute interface where the version check fails; the call path needs a call-gate fake. |

### 9.4 Interface facts: refresh, discovery, the window and the signature

| Name | Signature or fact |
|---|---|
| Refresh | Command words `refresh`, `full`, `silent` (plus the hyphen forms and the word all, §4). The handler is the plugin's private chat-command method and is not unit-reachable: no, a seam is needed (a pure parse of the argument string). |
| Multi-select default | `SidecarDiscoveryService.ResolveActiveOverlays(OverlayEntry)`. The no-selection rule is reachable when settings cannot be read: a bridge over a substitute interface where Penumbra is unavailable gives no settings, so every group contributes nothing. Selected options need a Penumbra call-gate fake. |
| Per-option tables | R5 and R6 (needs the R2 seam). |
| Mods tab | ImGui calls `ImRaii.PushColor(ImGuiCol, uint, bool)`, `ImGui.GetColorU32(ImGuiCol.TextDisabled)`, `ImGuiCol.Text`, `ImGui.Selectable(string)`, `ImGui.DragInt(string, ref int, float)`, `ImGui.IsItemDeactivatedAfterEdit()`. Surrounding names: `_priorityEdits` (`Dictionary<string, int>`), `pri`, `active`, `entry`, `penumbra`. UI drawing is not unit-reachable: no, a seam is needed. |
| Content index | `TextureLoader.ParseMtrlBytes(byte[])` returns `MtrlTexturePaths` (`Parsed`, `HasColorTable`, `Index`); `TextureLoader.GetMtrlInfo(byte[])` returns `(string shader, uint[] constIds)`; `TextureLoader.IsUncompressed(string)` returns `bool?`; `textureLoader.LoadTexAsRgba(string)` returns `(byte[] rgba, int width, int height)?`; window members `ContentIndex` (record struct: `Rows`, `SubRow`, `State`, `HasDiffuse`, `Physical`), `ContentIndexState` (`Unknown`, `NoSampler`, `NoColorTable`, `FollowsHairColor`, `SelectsNothing`, `Compressed`, `Scanned`), `DefaultContentRow`, `HairShader`, `ResolveContentTexture(OverlayEntry entry, string modRoot, string gamePath)` returns `string?`, `ScanContentIndex(byte[] rgba)` returns `ContentIndex`, `Plugin.Log`. Surrounding locals: `modRoot`, `mtrlPath`, `mtrlRel`, `result`. Not unit-reachable: no, a seam is needed. |
| Body-shape signature | `private static string BodyShapeSignature(IReadOnlyDictionary<string, HashSet<string>>? shapes)` on `CompositorService`. Private: no, a seam is needed (or reflection). |

### 9.5 Interface facts: imports, namespaces and test data

| Item | Fact |
|---|---|
| Namespaces named by removed import lines | `System`, `System.Collections.Generic`, `System.Linq`, `System.Text.Json`, `Dalamud.Plugin`, `Dalamud.Plugin.Ipc`, `Dalamud.Plugin.Services`, `Dalamud.Interface.Utility.Raii`, `Proteus.Services` |
| Removed namespace declarations | file-scoped `Proteus.Interop` |
| Test literal (3 lines) | the game path of the Midlander female (`c0201`) body `b0001` bibo material: `chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl` |

## 10. Existing tests that exercise these lines

| Test class | Methods |
|---|---|
| `SidecarDiscoveryTests` | `ResolveActiveOverlays_FlatOverlays_ReturnsAllDescriptorsWithTopLevelRows`, `ResolveActiveOverlays_FlatOverlays_NoTopLevelRows_ColorTableRowsIsNull`, `ResolveActiveOverlays_EmptyFlatOverlaysList_FallsThroughToOptionGroups`, `ResolveActiveOverlays_NullOptionGroups_ReturnsEmpty`, `ResolveActiveOverlays_BothFlatAndOptionGroups_FlatWins`, `ResolveActiveContent_UnconditionalPieces_ResolveWithTopLevelRows`, `ResolveActiveContent_UnconditionalPieces_TakeTheModWideGlow`, `ResolveActiveContent_NoContentAtAll_IsEmpty` |
| `ContentPieceSelectionTests` | `An_ungated_piece_needs_no_penumbra_at_all` (builds the resolved-content record through `ResolveActiveContent`) |
| `FaceUvDoublingTests`, `ShellMensLayoutTests`, `WomensArtOnMenTests` | the whole classes: they build the resolved-overlay record with four positional arguments |
| `StutterFixesWiringTests` | `CarrierWritesBracketTheInvokeWithAThreadStaticCounter`, `ReconcileOneCarrierRewalksBeforeARetry`, `ConfirmationIsStampedAndComparedWithRedrawAndAsk`, `OnlyReconcileAndVerifySelfSchedulesTheRetry` (each reads a method body that contains removed lines) |
| `StutterFixesPureTests` | the classes `GlamourerSignalsTests` and `CarrierCooldownsTests` (the helpers the derived lines call) |
| `HarnessBindingsWiringTests` | `Apply_goes_through_Glamourers_own_ApplyDesign_ipc` (reads all of GlamourerBridge.cs; its two assertions are negative) |
| Compile-only | the 69 test files of §1 that lose an import line or the game-path literal, among them `LocalizationTests`, `PbdFileTests`, `ModCreationTests` and `OnionImportTests` |

EDGE-5 (an asserted text that sits in a removed line): one hit. `StutterFixesWiringTests.CarrierWritesBracketTheInvokeWithAThreadStaticCounter` asserts that the state-changed handler's body contains the call text of the signal helper, `GlamourerSignals.RaisesEquipmentChange(`, after the unfiltered-event raise. That call text sits in derived line 446 of GlamourerBridge.cs. The other text assertions over these files name only lines that stay (checked: every string literal in every source-reading test against every removed line).

## 11. Dropped or changed behaviour

None. Every behaviour above is kept as it is, including the quirks a rebuild might be tempted to fix:

- R2 and R3 throw on duplicate group names;
- R3 lists an option once per descriptor;
- R5 does not fall back to the top-level rows;
- the refresh branch's prefix match;
- lock warnings are not de-duplicated and do not name the mod;
- §8.1's strict greater-than.

## 12. Security-relevant behaviour

| Row | Area | Behaviour |
|---|---|---|
| S1 | Validation and null handling: IPC lookups | `modDirectory`, `group` and `option` are used only in case-insensitive equality comparisons against discovered entries and metadata names. A null directory matches nothing. A null group selects the top-level table; a null option with a non-null group fails (empty string or false). An empty string is treated as a name. |
| S2 | Validation and null handling: colour JSON | Only deserialised, as a typed list of colour-table row presets, by System.Text.Json with default options (no polymorphic type handling). Null, empty or whitespace input, or the JSON literal null, clears the table. Malformed JSON is caught: false and one error line. The row values are not range-checked here. |
| S3 | Validation and null handling: master switch | SetColorTable refuses (false) when `Configuration.PluginEnabled` is false, before any lookup. GetColorTable, GetOverlays, GetActiveOverlays and Recomposite do not check it. |
| S4 | Validation and null handling: constructor | No null checks on the constructor arguments. A null dependency fails at call time: inside the guard for R5 and R6, unguarded for R2, R3 and R4. |
| S5 | Threads and marshalling: IPC | Every function runs on the calling plugin's thread, with no marshalling and no locks. R6 mutates the shared metadata object of a snapshot entry in place and writes the sidecar file on that thread, concurrently with any composite reading the same object. R3 may make Penumbra IPC calls and read `meta.json` on that thread. |
| S6 | Threads and marshalling: Penumbra open | The open-a-mod call goes to Penumbra on the caller's thread with no marshalling, including the import services' calls from background work. |
| S7 | Threads and marshalling: Glamourer | Lock warnings are logged on the calling thread (the framework thread for the carrier helpers). The echo filter reads a thread-static depth counter, so a carrier write on one thread does not hide an Equip raised on another. |
| S8 | Exception containment | GetColorTable and SetColorTable catch every exception and log it at error level with the exception (empty string or false). GetOverlays, GetActiveOverlays, Recomposite, registration and disposal have no guard; their exceptions propagate to the caller (the subscribing plugin, or plugin construction and teardown). The open-a-mod method catches everything and logs. The Glamourer methods keep their existing guards; the lock branch itself cannot throw. The refresh parse cannot throw on any string. |
| S9 | Path and name handling | No removed line builds a file path from caller input. The IPC save writes the matched entry's own sidecar through the discovery service, never a path from the caller. The open-a-mod directory is passed to Penumbra as is. The colour-index block reads a material file and an index texture whose paths come from the mod's own manifest and Penumbra's resolution (Solona's resolver), not from IPC. Log lines carry mod directories, material and texture game paths, Glamourer slot names and item ids, never a character name. |
| S10 | Disposal and unregistration | The provider unregisters all six labels on dispose, in the §3 R8 order, and holds nothing else. The plugin disposes it once during teardown, after the fonts and before the design watcher, without a guard. The Penumbra open-window subscriber needs no disposal. The Glamourer changes add no subscription. |
