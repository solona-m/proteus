using System;
using System.IO;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.PenumbraManipulations;

public sealed partial class SecondSkinService
{
    private sealed partial class ShellSetBuild
    {
        private sealed class HostModelBuild
        {
            private readonly ShellSetBuild build;
            private readonly PushSweep? pushSweep;
            private readonly int h;
            private HostAccessory host;
            private ResolvedSurface surface = null!;
            private bool carrier;
            private string hostRace = null!;
            private bool nativeAtHostRace;
            private string? publishCode;
            private bool differs;
            private byte[] shell = null!;
            private SecondSkinWriter.Stats stats;
            private string mdlGamePath = null!;
            private string mdlDisk = null!;

            public HostModelBuild(ShellSetBuild build, PushSweep? pushSweep, int h)
            {
                this.build = build;
                this.pushSweep = pushSweep;
                this.h = h;
            }

            public void Run()
            {
                if (!BuildModel()) return;
                ReportStats();
                PublishModel();
                RedirectCarrier();
            }

            private bool BuildModel()
            {
                if (build.perHostLayers[h].Count == 0) return false;
                host = build.hosts[h];
                // The surface THIS host carries: its geometry, and the race space every decision below is made against.
                surface = build.surfaces[build.hostSurface[h]];

                // Resolved above the material loop, so materials and publish path agree on a race code.
                carrier = host.BaseModel == null;
                (hostRace, nativeAtHostRace, publishCode) = build.plan[h];
                differs = !string.Equals(hostRace, surface.CutCode, StringComparison.OrdinalIgnoreCase);

                
                
                try
                {
                    // A host filled entirely with imported content needs no sources, and must not take its model flags from a body.
                    var srcs = build.perHostLayers[h].All(l => l.Geometry.Count > 0)
                        ? []
                        : surface.Sources;
                    var tDump = PhaseCounter.Begin();
                    build.service.DumpShellInputs(h, srcs, build.perHostLayers[h], host.BaseModel);
                    build.service.statsDump.Stop(tDump);
                    var tWriter = PhaseCounter.Begin();
                    try
                    {
                        var geometryKey = pushSweep == null ? ShellGeometryKey(srcs, build.perHostLayers[h], host.BaseModel) : null;
                        (string Key, byte[] Shell, SecondSkinWriter.Stats Stats) memo = default;
                        bool hit;
                        // Locked: a superseded composite can still be inside Build while its replacement starts one.
                        lock (build.service._shellMemo)
                            hit = geometryKey != null && build.service._shellMemo.TryGetValue(h, out memo)
                               && string.Equals(memo.Key, geometryKey, StringComparison.Ordinal);
                        if (hit)
                        {
                            shell = memo.Shell!;   // hit implies the entry was found
                            stats = memo.Stats;
                            build.service.statsWriterReused.Count();
                            build.service.log.Debug("[Proteus] second skin: host {0}{1:D4}/{2} geometry unchanged — reusing the built shell",
                                host.Prefix, host.SetId, host.Slot);
                        }
                        else
                        {
                            // Say which input moved when there is a previous build to compare against.
                            if (geometryKey != null && memo.Key != null)
                                build.service.log.Debug("[Proteus] second skin: host {0}{1:D4}/{2} shell rebuilt — {3}",
                                    host.Prefix, host.SetId, host.Slot, FirstKeyDifference(memo.Key, geometryKey));
                            lock (build.service._shellMemo) build.service._shellMemo.Remove(h);
                            shell = SecondSkinWriter.Build(srcs, build.perHostLayers[h], host.BaseModel,
                                out stats, msg => build.service.log.Debug("[Proteus] second skin: {0}", msg), build.service.AuthoredCaps(), pushSweep,
                                build.service.writerTimings);
                            if (geometryKey != null) lock (build.service._shellMemo) build.service._shellMemo[h] = (geometryKey, shell, stats);
                        }
                    }
                    finally { build.service.statsWriter.Stop(tWriter); }
                    tDump = PhaseCounter.Begin();
                    build.service.DumpShellOutput(h, shell);
                    build.service.statsDump.Stop(tDump);

                    // This host's reinforced-toe regions, per material, unioned across hosts: a texture sheet is shared by the material.
                    if (stats.ToeReinforceMaps is { } hostMaps)
                        foreach (var (mat, rm) in hostMaps)
                        {
                            if (build.toeReinforceMaps.TryGetValue(mat, out var had) && had.Size == rm.Size)
                            {
                                var merged = (byte[])had.Mask.Clone();
                                for (int i = 0; i < merged.Length && i < rm.Mask.Length; i++)
                                    if (rm.Mask[i] > merged[i]) merged[i] = rm.Mask[i];
                                build.toeReinforceMaps[mat] = (merged, rm.Size);
                            }
                            else build.toeReinforceMaps[mat] = rm;
                        }
                    if (pushSweep != null)
                        build.service.log.Information("[Proteus] second skin: push sweep, host {0}{1:D4}/{2}: {3}",
                            host.Prefix, host.SetId, host.Slot, pushSweep.TakeReport());
                }
                catch (EmptyShellException ex) when (ex.ByToggle)
                {
                    // Not a failure: the user switched off the only thing this host carried. Information, not Error.
                    build.service.log.Information("[Proteus] second skin: host {0}{1:D4}/{2} has nothing to draw — {3}",
                        host.Prefix, host.SetId, host.Slot, ex.Message);
                    return false;
                }
                catch (Exception ex)
                {
                    build.service.log.Error(ex, "[Proteus] second skin: model build failed for host {0}{1:D4}/{2}", host.Prefix, host.SetId, host.Slot);
                    return false;   // this host fails; the others still build
                }
                return true;
            }

            private void ReportStats()
            {
                // A toe cap was wanted but no binding described this body. Logged, not chat (the wearer cannot fix it); deduped.
                if (stats.CapDeclined is { } declined && build.service.lastCapDeclined != declined)
                {
                    build.service.lastCapDeclined = declined;
                    build.service.lastCapUsed = null;   // the cap placed again later is news, the same way a new decline is
                    build.service.log.Warning("[Proteus] second skin: toe cap declined — {0}", declined);
                }

                // Which cap this shell actually got. Only on a change, so it is not log spam.
                if (stats.CapUsed is { } capUsed)
                {
                    // A placed cap re-arms the decline line, so declining again later is logged.
                    build.service.lastCapDeclined = null;
                    if (build.service.lastCapUsed != capUsed)
                    {
                        build.service.lastCapUsed = capUsed;
                        build.service.log.Information("[Proteus] second skin: toe cap {0}", capUsed);
                    }
                }

                // What the redundancy pass took out, at Information (the writer's per-drop lines are Debug) and naming the switch.
                // Deduped on the tally, keyed by host as well.
                if (stats.RedundantSubs > 0)
                {
                    var tally = $"{host.Prefix}{host.SetId:D4}/{host.Slot}:{stats.RedundantSubs}/{stats.RedundantTris}";
                    if (build.service.lastRedundant != tally)
                    {
                        build.service.lastRedundant = tally;
                        build.service.log.Information("[Proteus] second skin: dropped {0} redundant submesh(es) ({1} triangles) "
                                      + "as geometry the shell already draws — if a patch of skin is missing from "
                                      + "the shell, turn off \"Hide redundant body meshes\" in Settings",
                            stats.RedundantSubs, stats.RedundantTris);
                    }
                }
            }

            private void PublishModel()
            {
                // Redirect the path the game ACTUALLY loads (host.ModelPath) for an equipped host; the Emperor fallback's path is
                // rebuilt in cutCode space, paired with the EQDP entries below so the game falls through to the parent race and
                // race-deforms the shell exactly as it deforms the body.
                // One published path per host, except the carrier case below: an alias of an ordinary host comes back as an
                // equipped accessory and poisons the model-race vote. The Emperor's ring is exempt only because a{EmperorSetId}
                // is filtered out of that vote at its source.
                mdlGamePath = host.ModelPath
                    ?? $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/model/c{publishCode}{host.Prefix}{host.SetId:D4}_{host.Slot}.mdl";
                mdlDisk = Path.Combine(build.modelsDir, $"secondskin_{h}.mdl");
                var tModelWrite = PhaseCounter.Begin();
                var modelChanged = WriteIfChanged(mdlDisk, shell);

                // What the model ON DISK asks the game for, read back from the FILE, not the built bytes: shell files are keyed by
                // host index and the host list changes between composites, so only the file catches a write that did not land.
                try
                {
                    // Re-read only after a write; an unchanged WriteIfChanged has already proven the file equals `shell`.
                    var onDisk = shell;
                    if (modelChanged)
                    {
                        onDisk = File.ReadAllBytes(mdlDisk);
                        if (!onDisk.AsSpan().SequenceEqual(shell))
                            build.service.log.Warning("[Proteus] shell file {0} does NOT match the bytes just built for host "
                                      + "{1}{2:D4}/{3} — the write did not land, so the game is loading a stale shell",
                                Path.GetFileName(mdlDisk), host.Prefix, host.SetId, host.Slot);
                    }

                    var declared = SecondSkinWriter.MaterialNames(onDisk);
                    // Slot as well as set: one accessory set covers _nek, _ear, _wrs and _rir.
                    var published = build.redirects.Keys.Where(k =>
                        k.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)
                     && k.Contains($"{host.Prefix}{host.SetId:D4}/", StringComparison.OrdinalIgnoreCase)
                     && k.Contains($"_{host.Slot}_", StringComparison.OrdinalIgnoreCase));

                    build.service.log.Information("[Proteus] second skin host {0}{1:D4}/{2}: model declares {3} material(s) [{4}] "
                                  + "— published [{5}]",
                        host.Prefix, host.SetId, host.Slot, declared.Count, string.Join(", ", declared),
                        string.Join(", ", published));
                }
                catch (System.Exception ex)
                {
                    build.service.log.Warning("[Proteus] could not read back the shell model's material names: {0}", ex.Message);
                }
                build.service.statsModelWrite.Stop(tModelWrite);
                build.shellChanged   |= modelChanged;
                build.modelChangedAny |= modelChanged;
                build.redirects[mdlGamePath] = Rel(build.outputRoot, mdlDisk);
                build.hostModelPaths.Add(mdlGamePath);
                // Append hosts only: this path has a real upstream (the player's own item) that later composites must read back.
                if (host.BaseModel != null) build.appendHostModelPaths.Add(mdlGamePath);
            }

            private void RedirectCarrier()
            {
                // ── carrier hosts: make the game load our copy from CUT space ─────────────────────────
                // A host whose model we REPLACE has no appearance of its own, so its per-race metadata is ours: empty the wearer's
                // entry and the lookup falls through to the c0201 cut space, picking up the body's deform. NOT for an APPEND host,
                // whose metadata belongs to the player's item. A fall-through pair is only emitted for codes proven to chain.
                if (carrier && nativeAtHostRace)
                {
                    // Cut space was rejected: publish at the wearer's own code, and this entry makes the game load it with no deform.
                    // Nothing is emptied, since an empty entry requests a deform.
                    build.manipulations.Add(EqdpManipulation(hostRace, host.EqdpSlot, host.SetId));

                    // Publish at c{hostRace} explicitly rather than trusting mdlGamePath, which for a facewear carrier is built from
                    // equipCode and can name a different race than the entry just written. Whatever mdlGamePath registered stays.
                    var nativePath = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/model/"
                                   + $"c{hostRace}{host.Prefix}{host.SetId:D4}_{host.Slot}.mdl";
                    if (!build.redirects.ContainsKey(nativePath))
                    {
                        build.redirects[nativePath] = Rel(build.outputRoot, mdlDisk);
                        build.hostModelPaths.Add(nativePath);
                    }
                    build.service.log.Information("[Proteus] second skin: EQDP for {0} {1}{2:D4} — c{3} has the model, loaded "
                                  + "natively with no fall-through -> {4}",
                        host.EqdpSlot, host.Prefix, host.SetId, hostRace, nativePath);
                }
                else if (carrier && differs)
                {
                    build.manipulations.Add(EqdpManipulation(surface.CutCode, host.EqdpSlot, host.SetId));
                    build.manipulations.Add(EqdpManipulation(hostRace, host.EqdpSlot, host.SetId, hasModel: false));

                    // Publish at BOTH codes, derived from hostRace/cutCode: if the emptied entry takes, the cut-space copy loads and
                    // deforms; if the slot is not EQDP-driven, the native copy loads. Deriving (not reusing host.ModelPath) keeps the set stable.
                    string PathFor(string code)
                        => $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/model/"
                         + $"c{code}{host.Prefix}{host.SetId:D4}_{host.Slot}.mdl";

                    foreach (var code in new[] { surface.CutCode, hostRace })
                    {
                        var p = PathFor(code);
                        if (build.redirects.ContainsKey(p)) continue;
                        build.redirects[p] = Rel(build.outputRoot, mdlDisk);
                        build.hostModelPaths.Add(p);
                    }
                    build.service.log.Information("[Proteus] second skin: EQDP for {0} {1}{2:D4} — c{3} has the model, c{4} "
                                  + "emptied so the game falls through to it -> {5} (native copy kept at {6})",
                        host.EqdpSlot, host.Prefix, host.SetId, surface.CutCode, hostRace,
                        PathFor(surface.CutCode), PathFor(hostRace));
                }
                else if (carrier && host.Prefix == 'a')
                {
                    // Emperor's ring in a race that is already cut-space: it loads no model without an entry saying it has one.
                    build.manipulations.Add(EqdpManipulation(surface.CutCode, host.EqdpSlot, host.SetId));
                    build.service.log.Information("[Proteus] second skin: EQDP for {0} {1}{2:D4} — c{3} has the model -> {4}",
                        host.EqdpSlot, host.Prefix, host.SetId, surface.CutCode, mdlGamePath);
                }
                build.service.log.Information("[Proteus] second skin: host {0}{1:D4}/{2} <- {3} layer(s) -> {4} meshes, {5} KB (append={6})",
                    host.Prefix, host.SetId, host.Slot, build.perHostLayers[h].Count, stats.Meshes, shell.Length / 1024, host.BaseModel != null);
            }
        }
    }
}
