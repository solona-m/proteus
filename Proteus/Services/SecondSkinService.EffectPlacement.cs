using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CheapLoc;
using Dalamud.Game.Text.SeStringHandling;

namespace Proteus.Services;
using static Proteus.Services.ContentPieceResolver;
using static Proteus.Services.PenumbraManipulations;

public sealed partial class SecondSkinService
{
    private sealed partial class ShellSetBuild
    {
        /// <summary>
        /// Put every placed piece's item effect (<see cref="ContentPiece.Effect"/>) on an accessory carrier drawn this build:
        /// an IMC entry for the carrier's variant with an effect id of its own, and the pack's selected <c>.avfx</c> at that
        /// id's path. The game spawns an item effect only for a drawn item whose entry names it, so this is what lets it play
        /// with the pack's own item not worn. Runs after the host models are built, so "drawn" is known.
        /// </summary>
        private void PlaceEffects()
        {
            // One request per (mod, piece) with an effect, with the host its unit went to; a unit that found no host asks
            // with -1 and is dropped, as its piece is.
            var requests = EffectHostPlanner.CollectRequests(
                contentWork.Concat(contentUnhosted.Select(u => (Unit: u, HostIdx: -1))),
                u => contentUnits[u].Owners,
                o => o.Content.Piece.Effect != null,
                (a, b) => ReferenceEquals(a.Content.Piece, b.Content.Piece)
                       && string.Equals(a.Entry.ModDirectory, b.Entry.ModDirectory, StringComparison.OrdinalIgnoreCase));

            var published = new List<(string Host, int Variant, int VfxId, string File)>();
            var drops = new List<string>();
            if (requests.Count > 0)
            {
                var candidates = new List<EffectHostPlanner.Candidate>();
                for (int h = 0; h < hosts.Count; h++)
                {
                    var host = hosts[h];
                    if (EffectHostPlanner.CandidateFor(h, host.Slot, host.BaseModel != null, host.Prefix,
                            drawn: perHostLayers[h].Count > 0,
                            () => ItemVariantFolder(host)?.Variant,
                            v => VanillaEntry(host, v)?.VfxId,
                            id => !Plugin.DataManager.FileExists(VfxGamePath(host.Prefix, host.SetId, id))) is { } c)
                        candidates.Add(c);
                }

                var (assigned, dropped) = EffectHostPlanner.Assign(candidates,
                    requests.Select((r, i) => new EffectHostPlanner.Request(i, r.Host)).ToList());

                foreach (var a in assigned)
                {
                    var ((entry, content), _) = requests[a.Request];
                    var effect = content.Piece.Effect!;
                    var host = hosts[a.Host];
                    var variant = ItemVariantFolder(host)!.Value.Variant;

                    var modRoot = entry.ModRoot;
                    modSelections.TryGetValue(entry.ModDirectory, out var selection);
                    var src = modRoot == null ? null : SelectedMaterialFile(modRoot, effect.Sources, selection);
                    // A missing or unreadable file is logged, not said in chat: no slot would fix it, and the import warned.
                    if (src == null)
                    {
                        service.log.Warning("[Proteus] effect: {0} \"{1}/{2}\" — none of its effect files ({3}) is in the "
                                  + "mod, so the effect is not shown", entry.ModDirectory, content.OptionGroup ?? "",
                            content.Option ?? "", string.Join(", ", effect.Sources.Select(s => s.File)));
                        continue;
                    }

                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(src); }
                    catch (Exception ex)
                    {
                        service.log.Warning("[Proteus] effect: {0} — {1} could not be read ({2})",
                            entry.ModDirectory, src, ex.Message);
                        continue;
                    }

                    var vanilla = VanillaEntry(host, variant)!.Value;
                    manipulations.Add(ImcManipulation(host.Prefix, host.SetId, variant, host.EqdpSlot,
                        vanilla with { VfxId = (byte)a.VfxId }));

                    var disk = Path.Combine(outputRoot, "vfx", $"effect_{host.Slot}.avfx");
                    Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
                    shellChanged |= WriteIfChanged(disk, bytes);
                    var gamePath = VfxGamePath(host.Prefix, host.SetId, a.VfxId);
                    redirects[gamePath] = Rel(outputRoot, disk);

                    published.Add(($"{host.Prefix}{host.SetId:D4}/{host.Slot}", variant, a.VfxId, src));
                    service.log.Information("[Proteus] effect: {0} \"{1}/{2}\" -> {3}{4:D4}/{5} variant {6}, effect {7} ({8})",
                        entry.ModDirectory, content.OptionGroup ?? "", content.Option ?? "", host.Prefix, host.SetId,
                        host.Slot, variant, a.VfxId, Path.GetFileName(src));
                }

                foreach (var d in dropped)
                {
                    var ((entry, content), _) = requests[d.Request];
                    drops.Add(entry.ModDirectory);
                    service.log.Warning("[Proteus] effect: {0} \"{1}/{2}\" — not shown: {3}",
                        entry.ModDirectory, content.OptionGroup ?? "", content.Option ?? "", d.Reason);
                }
            }

            // An IMC entry only takes effect when the item is recreated, so a change in what is published is a model change.
            var effectKey = EffectHostPlanner.EffectKey(published);
            if (!string.Equals(effectKey, service._lastEffectKey, StringComparison.Ordinal))
            {
                modelChangedAny = true;
                service._lastEffectKey = effectKey;
            }

            ReportDroppedEffects(drops);
        }

        /// <summary>The game's own IMC entry for a host at <paramref name="variant"/>, or null when it cannot be read.</summary>
        private ImcEntry? VanillaEntry(HostAccessory host, int variant)
        {
            var modelPath = host.ModelPath
                ?? $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/model/c0101{host.Prefix}{host.SetId:D4}_{host.Slot}.mdl";
            return ImcEntrySource.FromGame(p => Plugin.DataManager.GetFile(p)?.Data, modelPath, variant);
        }

        /// <summary>In chat once per change of WHICH mods lost their effect, as well as the log: nothing else shows why.</summary>
        private void ReportDroppedEffects(List<string> drops)
        {
            if (!EffectHostPlanner.ShouldSay(ref service._lastEffectDrops, drops)) return;
            var msg = string.Format(ProteusLoc.Localize("Chat.EffectUnplaced.Fmt",
                "[Proteus] The animated effect of \"{0}\" is not shown: it needs a free accessory slot for its "
              + "invisible carrier. Free a ring, bracelet or necklace slot."),
                string.Join(", ", service._lastEffectDrops!.Split('\n')));
            _ = Plugin.Framework.RunOnFrameworkThread(
                () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));
        }
    }
}
