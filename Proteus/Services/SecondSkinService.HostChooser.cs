using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.ContentPieceResolver;
using static Proteus.Services.PenumbraManipulations;

public sealed partial class SecondSkinService
{
    private sealed class HostChooser
    {
        private readonly SecondSkinService service;
        private readonly string cutCode;
        private readonly string equipCode;
        private readonly string wearerCode;
        private readonly IReadOnlyDictionary<string, string>? equipped;
        private readonly IReadOnlyList<string>? metModels;
        private readonly int? invisibleGlassesSet;
        private readonly string outputRoot;
        private readonly IReadOnlySet<string> hostedPackRoots;
        private readonly int? emperorRingVariant;
        private readonly int? invisibleGlassesVariant;
        private List<HostAccessory> hosts = null!;
        private List<HostAccessory> foreignAccessories = null!;
        private List<(HostAccessory Host, string By)> claimedCarriers = null!;

        public HostChooser(SecondSkinService service, string cutCode, string equipCode, string wearerCode, IReadOnlyDictionary<string, string>? equipped, IReadOnlyList<string>? metModels, int? invisibleGlassesSet, string outputRoot, IReadOnlySet<string> hostedPackRoots, int? emperorRingVariant, int? invisibleGlassesVariant)
        {
            this.service = service;
            this.cutCode = cutCode;
            this.equipCode = equipCode;
            this.wearerCode = wearerCode;
            this.equipped = equipped;
            this.metModels = metModels;
            this.invisibleGlassesSet = invisibleGlassesSet;
            this.outputRoot = outputRoot;
            this.hostedPackRoots = hostedPackRoots;
            this.emperorRingVariant = emperorRingVariant;
            this.invisibleGlassesVariant = invisibleGlassesVariant;
        }

        public List<HostAccessory> Run(out List<(HostAccessory Host, string By)> claimedCarriersOut)
        {
            claimedCarriersOut = null!;
            Begin();
            AddHeadHosts();
            AddAccessoryHosts();
            AddCarrierHosts();
            return AddFallbackHosts(ref claimedCarriersOut);
        }

        private void Begin()
        {
            service.log.Information("[Proteus] host: choosing from equipped accessories [{0}], head/glasses [{1}]",
                equipped == null ? "(null)" : string.Join(", ", equipped.Select(kv => $"{kv.Key}={kv.Value}")),
                metModels == null || metModels.Count == 0 ? "(none)" : string.Join(", ", metModels));

            hosts = new List<HostAccessory>();
            // Worn accessories that would host but load under a code the shell was not cut in: kept for the last resort below.
            foreignAccessories = new List<HostAccessory>();
        }

        private void AddHeadHosts()
        {
            // ── the host policy, in order, and the same on every race ──────────────────────────────────
            // Prefer a host we can move into cut space; never rewrite an item the player chose; never end up with no host.
            //
            // 1. Facewear CARRIER — our injected pair or a degenerate (invisible) item, REPLACED, so Build may rewrite its
            //    EQDP. The player's own visible pair is NOT a candidate.
            foreach (var metPath in OrderMetCandidates(metModels, invisibleGlassesSet))
            {
                if (LoadCandidate("met", metPath, 'e') is not { } c) continue;

                bool ours = invisibleGlassesSet is int inv && inv == c.SetId;
                if (ours || IsDegenerate(c.Bytes))
                {
                    service.log.Information("[Proteus] host: glasses/head e{0:D4} (met, REPLACE — {1}, base {2} B)",
                        c.SetId, ours ? "our injected pair" : "degenerate base", c.Bytes.Length);
                    // Our pair's variant is known from its sheet, worn or not.
                    hosts.Add(new HostAccessory(c.SetId, "met", "Head", null, 0, "equipment", 'e', metPath,
                        KnownVariant: ours ? invisibleGlassesVariant : null));
                    break;
                }
                service.log.Information("[Proteus] host: glasses/head e{0:D4} is the player's own pair ({1} material(s), "
                              + "{2} B) — not ours to redirect into c{3}, leaving it alone",
                    c.SetId, c.Mats, c.Bytes.Length, cutCode);
            }

            // Nothing occupies the head "_met" slot yet but invisible glasses are on, so the compositor is about to equip our
            // pair: host on it now (REPLACE path). Only when KNOWN empty: a null metModels means no walk succeeded, and a
            // REPLACE host would take off a hat.
            if (hosts.Count == 0 && metModels is { Count: 0 } && invisibleGlassesSet is int pending)
            {
                // Predicted with the EQUIPMENT code: equipment loads in the character's own space.
                var pendingPath = $"chara/equipment/e{pending:D4}/model/c{equipCode}e{pending:D4}_met.mdl";
                service.log.Information("[Proteus] host: invisible glasses e{0:D4} (met, REPLACE — pending injection)", pending);
                hosts.Add(new HostAccessory(pending, "met", "Head", null, 0, "equipment", 'e', pendingPath,
                    KnownVariant: invisibleGlassesVariant));
            }
        }

        private void AddAccessoryHosts()
        {
            // (An empty head/facewear slot we don't fill ourselves loads no model, so there is nothing to redirect.)

            // 2. Worn accessories — rings (right then left), bracelet, necklace — appended so they stay visible, but ONLY when
            //    they already load in the shell's own space; others are held back for step 4.
            foreach (var (slot, eqdp) in new[] { ("rir", "RFinger"), ("ril", "LFinger"), ("wrs", "Wrists"), ("nek", "Neck") })
            {
                if (Consider(slot, eqdp) is not { } acc) continue;
                if (acc.ModelPath != null && PathCharCode(acc.ModelPath) is { } accCc
                    && !string.Equals(accCc, cutCode, StringComparison.OrdinalIgnoreCase))
                {
                    service.log.Information("[Proteus] host: {0} ({1}{2:D4}) loads as c{3}, not the shell's c{4} — held back, "
                                  + "the Emperor's ring hosts in c{4} instead", slot, acc.Prefix, acc.SetId, accCc, cutCode);
                    foreignAccessories.Add(acc);
                    continue;
                }
                hosts.Add(acc);
            }
        }

        private void AddCarrierHosts()
        {
            // 3. Invisible "Emperor's New" CARRIERS (replace + EQDP), in every FREE accessory slot — right ring, left ring,
            //    bracelet, necklace. Offered even when step 2 found hosts, as spill capacity. All free slots are offered,
            //    since only a carrier can publish a native surface undeformed. A slot whose invisible piece isn't in the sheet
            //    is not offered.
            // The invisible piece for a slot. Rings keep the caller's variant; the others resolve their own.
            InvisibleRing.Identity? carrierFor(string slot)
            {
                var id = InvisibleRing.ResolveFor(Plugin.DataManager, service.log, slot);
                if (id == null) return null;
                return slot is "rir" or "ril" && emperorRingVariant is { } v
                    ? id.Value with { Variant = v }
                    : id;
            }

            // Whichever mod already provides an invisible carrier's model, if one does: those pieces have no model of their own,
            // so an answer means someone put geometry there on purpose. Checked under cut, equipment and wearer codes.
            // A PLAIN resolve: ResolveUpstream would memoise carrier paths and warn on our own files.
            // Deduped once, not per slot.
            var carrierCodes = new[] { cutCode, equipCode, wearerCode }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            string? CarrierClaimedBy(string slot, int setId)
            {
                foreach (var code in carrierCodes)
                {
                    var gamePath = $"chara/accessory/a{setId:D4}/model/c{code}a{setId:D4}_{slot}.mdl";
                    var disk = service.penumbra.ResolvePlayer(gamePath);
                    if (disk == null
                     || string.Equals(disk, gamePath, StringComparison.OrdinalIgnoreCase)   // nothing provides it
                     || IsInsideOutputRoot(disk, outputRoot)                                // our own last publish
                     || hostedPackRoots.Any(r => IsUnder(r, disk)))   // a pack we are placing — see the caller
                        continue;
                    return disk;
                }
                return null;
            }

            int freeCarriers = 0;
            // Carrier slots someone else's mod has claimed, held back for step 5 rather than dropped.
            claimedCarriers = new List<(HostAccessory Host, string By)>();
            foreach (var (slot, eqdpSlot, _) in InvisibleRing.CarrierSlots)
            {
                var worn = equipped != null && equipped.TryGetValue(slot, out var wp) ? wp : null;
                // Ours counts as free: it is the piece we equipped for exactly this on an earlier composite.
                if (worn != null && ParseSetId(worn, 'a') != EmperorSetId) continue;
                if (carrierFor(slot) is not { } id) continue;

                var host = new HostAccessory(id.ModelSet, slot, eqdpSlot, null, 0, "accessory", 'a',
                    KnownVariant: id.Variant);

                // Someone's mod lives here (wearing an Emperor's New piece to carry a mod is common); taking the slot deletes it.
                if (CarrierClaimedBy(slot, id.ModelSet) is { } owner)
                {
                    claimedCarriers.Add((host, owner));
                    service.log.Information("[Proteus] host: a{0:D4}/{1} carries another mod's model ({2}) — leaving it "
                                  + "alone so that mod keeps showing", id.ModelSet, slot, owner);
                    continue;
                }

                hosts.Add(host);
                freeCarriers++;
            }
            if (freeCarriers == 0)
            {
                // Three causes, each reported differently: every slot occupied, no invisible piece exists, or another mod's model
                // lives on the slot.
                bool anyPieceExists = InvisibleRing.CarrierSlots.Any(c => carrierFor(c.Slot) != null);
                service.log.Information(claimedCarriers.Count > 0
                    ? $"[Proteus] host: no free carrier slot — {claimedCarriers.Count} was/were left to another "
                    + "mod (see the lines above) and the rest hold the player's own pieces"
                    : anyPieceExists
                    ? "[Proteus] host: every accessory slot holds the player's own piece — no free slot for an "
                    + "invisible carrier"
                    : "[Proteus] host: no invisible carrier item could be resolved for any accessory slot — see "
                    + "the \"invisible carrier\" lines above; nothing can host a natively-authored surface");
            }
        }

        private List<HostAccessory> AddFallbackHosts(ref List<(HostAccessory Host, string By)> claimedCarriersOut)
        {
            // 4. Last resort: host on a held-back accessory anyway; a shell a race-size wrong beats no shell.
            if (hosts.Count == 0 && foreignAccessories.Count > 0)
            {
                var fallback = foreignAccessories[0];
                WarnForeignAppendHost(fallback.Slot, fallback.Prefix, fallback.SetId, fallback.ModelPath!);
                hosts.Add(fallback);
            }

            // 5. Nothing else at all: take the claimed carriers; the notice has already named the mod involved.
            if (hosts.Count == 0 && claimedCarriers.Count > 0)
            {
                foreach (var (host, by) in claimedCarriers)
                {
                    service.log.Warning("[Proteus] host: taking a{0:D4}/{1} even though \"{2}\" provides its model — "
                              + "nothing else can host the shell, so that mod's piece will not render",
                        host.SetId, host.Slot, by);
                    hosts.Add(host);
                }
                claimedCarriers.Clear();   // taken after all, so there is nothing to tell the user we spared
            }

            // Handed back for the caller to announce only if the shell actually ran short: every free carrier is offered as
            // spill capacity, so most are never needed.
            claimedCarriersOut = claimedCarriers;

            if (hosts.Count == 0)
                service.log.Warning("[Proteus] host: nothing can host the shell — no free facewear or ring slot, and no "
                          + "worn accessory to append to");

            service.log.Information("[Proteus] host: {0} host(s) in fill order: {1}", hosts.Count,
                string.Join(" -> ", hosts.Select(h => $"{h.Prefix}{h.SetId:D4}/{h.Slot}(cap {SecondSkinWriter.MaxMaterials - h.BaseMatCount})")));
            return hosts;
        }

        // Load a candidate host model: resolve, read, and count its materials. Null (having warned) when it cannot be
        // understood: an understated material count would make appended letters collide.
        private (int SetId, byte[] Bytes, int Mats)? LoadCandidate(string slot, string gamePath, char prefix)
        {
            if (ParseSetId(gamePath, prefix) is not int setId)
            {
                service.log.Warning("[Proteus] host: {0} — cannot parse a '{1}' set id from {2}, skipping", slot, prefix, gamePath);
                return null;
            }

            // A host loading under a different code than the shell was cut in renders it at the wrong SIZE, but it is never
            // rejected for that (it may be the last host). The size warning belongs to the APPEND path (WarnForeignAppendHost);
            // a carrier is redirected into cut space by Build.

            // A host's model path is one WE redirect, so it can resolve back to our own output; merging into that would double
            // the shell every composite. Go through the upstream resolver so a modded append host keeps the player's own file.
            var disk = service.resolveUpstream?.Invoke(gamePath) ?? service.penumbra.ResolvePlayer(gamePath);
            // Defence in depth for a null resolver (tests): appending the shell to itself compounds every run.
            if (disk != null && IsInsideOutputRoot(disk, outputRoot))
            {
                service.log.Debug("[Proteus] host: {0} resolved to our own output ({1}) and no upstream is known — "
                        + "reading the game's original instead", slot, disk);
                disk = null;
            }

            var bytes = service.textureLoader.LoadRawFile(disk, gamePath);
            if (bytes == null)
            {
                service.log.Warning("[Proteus] host: {0} ({1}{2:D4}) model {3} not loadable (disk={4}) — skipping", slot, prefix, setId, gamePath, disk ?? "(null)");
                return null;
            }

            try { return (setId, bytes, SecondSkinWriter.MaterialNames(bytes).Count); }
            catch (Exception ex)
            {
                service.log.Warning(ex, "[Proteus] host: {0} ({1}{2:D4}) material parse failed — skipping", slot, prefix, setId);
                return null;
            }
        }

        // True when the model has no real geometry to append onto (an invisible item); such a host is REPLACED instead.
        private bool IsDegenerate(byte[] bytes) => bytes.Length < DegenerateModelBytes;

        // An APPEND host's metadata is the player's, so one loading under a foreign code stays the wrong size and the player
        // is told. Carrier hosts are fixed silently by Build.
        private void WarnForeignAppendHost(string slot, char prefix, int setId, string gamePath)
        {
            if (PathCharCode(gamePath) is not { } pathCc
                || string.Equals(pathCc, cutCode, StringComparison.OrdinalIgnoreCase))
                return;
            service.log.Warning(
                "[Proteus] host: {0} ({1}{2:D4}) loads as c{3} but the shell was cut in c{4} — the game "
              + "deforms the body and not this worn item, so the shell will render a race-size wrong. Free "
              + "a ring slot (either hand) or your facewear slot to let Proteus host it in c{4} instead",
                slot, prefix, setId, pathCc, cutCode);
        }

        // Load an equipped model as a host candidate, or null if absent, unloadable, or FULL. Tree is "accessory"
        // (prefix a) or "equipment" (prefix e); the redirect and material game paths are built from these.
        private HostAccessory? ConsiderPath(string slot, string? gamePath, string tree, char prefix, string eqdpSlot)
        {
            if (gamePath == null)
            {
                service.log.Information("[Proteus] host: {0} — none equipped", slot);
                return null;
            }

            // The Emperor's New Ring loads a model only via our EQDP edit: it is the FALLBACK, never an append host.
            if (prefix == 'a' && ParseSetId(gamePath, prefix) == EmperorSetId)
            {
                service.log.Information("[Proteus] host: {0} is the Emperor's ring (a{1:D4}) — reserved for fallback, skipping", slot, EmperorSetId);
                return null;
            }

            if (LoadCandidate(slot, gamePath, prefix) is not { } c) return null;

            if (c.Mats >= SecondSkinWriter.MaxMaterials)
            {
                service.log.Debug("[Proteus] host: {0} ({1}{2:D4}) already carries {3}/{4} materials — no room to append, skipping",
                    slot, prefix, c.SetId, c.Mats, SecondSkinWriter.MaxMaterials);
                return null;
            }
            service.log.Information("[Proteus] host: {0} ({1}{2:D4}) candidate — {3} base material(s), capacity {4}",
                slot, prefix, c.SetId, c.Mats, SecondSkinWriter.MaxMaterials - c.Mats);
            return new HostAccessory(c.SetId, slot, eqdpSlot, c.Bytes, c.Mats, tree, prefix, gamePath);
        }

        // Accessory host (ring/bracelet): look the slot up in the equipped-accessory map.
        private HostAccessory? Consider(string slot, string eqdpSlot)
            => ConsiderPath(slot,
                equipped != null && equipped.TryGetValue(slot, out var gp) ? gp : null,
                "accessory", 'a', eqdpSlot);
    }
}
