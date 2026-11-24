using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Which host carries which second-skin layer and content piece: the arithmetic of <c>ShellSetBuild</c>'s distribution,
/// apart from the game and Penumbra so it can be tested (the pattern of <see cref="CarrierDecisions"/>). No logging,
/// no host identities: <c>ShellSetBuild</c> maps its hosts, surfaces and units in and the results back out.
/// </summary>
internal static class ShellHostAllocation
{
    /// <param name="Capacity">Layers it can still take: <c>MaxMaterials - BaseMatCount</c>.</param>
    /// <param name="Carrier">An invisible carrier (no base model), whose EQDP is ours to rewrite.</param>
    /// <param name="FacewearCarrier">The facewear carrier (the invisible glasses, or a degenerate pair).</param>
    /// <param name="EffectCarrier">An accessory carrier whose IMC entry may carry an item effect.</param>
    internal readonly record struct Host(int Capacity, bool Carrier, bool FacewearCarrier, bool EffectCarrier);

    /// <param name="CarrierOnly">Must be published with no race deform (every surface but the body).</param>
    /// <param name="Face">A face surface (the face material).</param>
    internal readonly record struct Surface(bool CarrierOnly, bool Face);

    /// <param name="Surface">Index into the surfaces.</param>
    /// <param name="EffectOnly">Only an effect, no geometry: an invisible accessory carrier only.</param>
    /// <param name="CarriesEffect">Real geometry AND an effect: tries the effect carriers first.</param>
    internal readonly record struct Unit(int Surface, bool EffectOnly, bool CarriesEffect);

    internal sealed class State
    {
        public required int[] Remaining { get; init; }
        /// <summary>The surface that has taken each host; null while free.</summary>
        public required int?[] HostClaim { get; init; }
        /// <summary>The surface each host carries (0 for an unclaimed host, as before the extraction).</summary>
        public required int[] HostSurface { get; init; }
        public int DiskBudget { get; set; }
        public List<(int LayerIdx, int HostIdx)> Work { get; } = new();
        public List<(int Unit, int HostIdx)> ContentWork { get; } = new();
        /// <summary>Layers of a carrier-only surface that no host could carry, per surface.</summary>
        public List<(int Surface, List<int> Layers)> Unhosted { get; } = new();
        /// <summary>The bottom layers that ran out of capacity (over budget), bottom first.</summary>
        public List<int> Dropped { get; } = new();
        public List<int> ContentUnhosted { get; } = new();

        /// <summary>The facewear carrier held for a face surface; null when none applies.</summary>
        public FacewearHold? Reservation { get; set; }
        /// <summary>Disk ids still held back from every other surface for the reserved face layers/pieces.</summary>
        public int ReservedOutstanding { get; set; }
        /// <summary>A reservation was made but placed nothing, so the allocation was re-run without it (defensive).</summary>
        public bool ReservationDropped { get; set; }
        /// <summary>BODY layers (non-carrier-only surfaces) dropped over budget only because of the reservation.</summary>
        public int DisplacedByReservation { get; set; }
    }

    /// <summary>The facewear carrier <paramref name="Host"/> held for face surface <paramref name="Surface"/>.</summary>
    /// <param name="Demand">The face layers plus face pieces that will draw and could use it.</param>
    internal sealed record FacewearHold(int Host, int Surface, int Demand);

    /// <summary>
    /// A mod on the face material gets the facewear carrier first: the first Face surface (in surface order) with demand
    /// that will draw holds the facewear carrier, when one exists and some other host can still take the body. Its gear
    /// layers count, and its content pieces that are neither effect-only nor carry an effect (an effect sorts onto an
    /// accessory carrier, and an effect-only piece can never use the facewear slot). Null when nothing applies.
    /// </summary>
    public static FacewearHold? FacewearReservation(IReadOnlyList<Host> hosts, IReadOnlyList<Surface> surfaces,
                                                    IReadOnlyList<int> layerSurface, IReadOnlyList<Unit> units)
    {
        int g = -1;
        for (int i = 0; i < hosts.Count; i++)
            if (hosts[i].FacewearCarrier && hosts[i].Carrier && hosts[i].Capacity > 0) { g = i; break; }
        if (g < 0) return null;

        bool otherHost = false;
        for (int i = 0; i < hosts.Count; i++)
            if (i != g && hosts[i].Capacity > 0) { otherHost = true; break; }
        // A face overlay never costs the whole body look: with nothing else to carry it, the body keeps the glasses.
        if (!otherHost) return null;

        for (int surf = 0; surf < surfaces.Count; surf++)
        {
            if (!surfaces[surf].Face) continue;
            int demand = 0;
            foreach (var ls in layerSurface) if (ls == surf) demand++;
            foreach (var u in units) if (u.Surface == surf && !u.EffectOnly && !u.CarriesEffect) demand++;
            if (demand > 0) return new FacewearHold(g, surf, demand);
        }
        return null;
    }

    /// <summary>The reservation placed nothing on its host (unreachable by construction today; kept as a guard).</summary>
    public static bool ReservationUnused(State s)
        => s.Reservation is { } r
           && !s.Work.Exists(w => w.HostIdx == r.Host)
           && !s.ContentWork.Exists(w => w.HostIdx == r.Host);

    /// <summary>A reserved facewear host that ended up with no published model after baking and writing.</summary>
    public static bool ReservedButUnbuilt(int? reservedHost, IReadOnlyCollection<int> builtHosts)
        => reservedHost is { } h && !builtHosts.Contains(h);

    /// <summary>
    /// The whole allocation: the facewear reservation (if any), the layers, then the content pieces. A reservation that
    /// placed nothing is re-run without it.
    /// </summary>
    public static State Run(IReadOnlyList<Host> hosts, IReadOnlyList<Surface> surfaces, IReadOnlyList<int> layerSurface,
                            IReadOnlyList<Unit> units, int diskBudget)
    {
        var hold = FacewearReservation(hosts, surfaces, layerSurface, units);
        var s = RunWith(hold, hosts, surfaces, layerSurface, units, diskBudget);
        if (hold == null) return s;
        if (ReservationUnused(s))
        {
            s = RunWith(null, hosts, surfaces, layerSurface, units, diskBudget);
            s.ReservationDropped = true;
            return s;
        }
        // How many BODY layers the reservation cost (dropped over budget), against the body-first allocation: what the
        // "body layer(s) didn't fit" log line counts.
        var baseline = RunWith(null, hosts, surfaces, layerSurface, units, diskBudget);
        int BodyDropped(State st) => st.Dropped.Count(k => layerSurface[k] >= 0 && !surfaces[layerSurface[k]].CarrierOnly);
        s.DisplacedByReservation = Math.Max(0, BodyDropped(s) - BodyDropped(baseline));
        return s;
    }

    internal static State RunWith(FacewearHold? hold, IReadOnlyList<Host> hosts, IReadOnlyList<Surface> surfaces,
                                  IReadOnlyList<int> layerSurface, IReadOnlyList<Unit> units, int diskBudget)
    {
        var s = Begin(hosts, diskBudget);
        if (hold != null)
        {
            // Pre-claimed through the existing identity rule: every other surface's layers and pieces skip this host.
            s.HostClaim[hold.Host] = hold.Surface;
            s.HostSurface[hold.Host] = hold.Surface;
            s.Reservation = hold;
            s.ReservedOutstanding = Math.Min(Math.Min(hold.Demand, hosts[hold.Host].Capacity), diskBudget);
        }
        DistributeLayers(s, hosts, surfaces, layerSurface);
        PlaceContent(s, hosts, surfaces, units);
        return s;
    }

    /// <summary>The disk ids a surface may spend: everything for the reserved face surface, the rest minus what is held.</summary>
    private static int Budget(State s, int surfIdx)
        => s.Reservation is { } r && r.Surface == surfIdx ? s.DiskBudget : Math.Max(0, s.DiskBudget - s.ReservedOutstanding);

    /// <summary>The reserved face surface placed <paramref name="n"/> on <paramref name="host"/>: that much of the hold is spent.</summary>
    private static void Spend(State s, int surfIdx, int host, int n)
    {
        if (s.Reservation is { } r && r.Surface == surfIdx && r.Host == host)
            s.ReservedOutstanding = Math.Max(0, s.ReservedOutstanding - n);
    }

    public static State Begin(IReadOnlyList<Host> hosts, int diskBudget)
    {
        var remaining = new int[hosts.Count];
        for (int i = 0; i < hosts.Count; i++) remaining[i] = hosts[i].Capacity;
        return new State
        {
            Remaining = remaining,
            HostClaim = new int?[hosts.Count],
            HostSurface = new int[hosts.Count],
            DiskBudget = diskBudget,
        };
    }

    /// <summary>
    /// Layers arrive bottom-first with the mask LAST. The FIRST host draws in front, so the TOP layers fill it and lower
    /// layers spill to the hosts behind. Within a host, stack order is kept so the topmost has the highest material
    /// index. Over capacity, the BOTTOM layers drop, never the mask. Done per SURFACE, in surface order (body first): a
    /// host has one EQDP entry, so its layers must agree on a race code, and a natively-authored surface needs a CARRIER.
    /// </summary>
    /// <param name="layerSurface">Each layer's surface index; -1 for a layer that never resolved.</param>
    public static void DistributeLayers(State s, IReadOnlyList<Host> hosts, IReadOnlyList<Surface> surfaces, IReadOnlyList<int> layerSurface)
    {
        for (int surfIdx = 0; surfIdx < surfaces.Count; surfIdx++)
        {
            var layerIdxs = new List<int>();
            for (int i = 0; i < layerSurface.Count; i++)
                if (layerSurface[i] == surfIdx) layerIdxs.Add(i);
            if (layerIdxs.Count == 0) continue;

            bool carrierOnly = surfaces[surfIdx].CarrierOnly;
            var eligible = new List<int>();
            for (int i = 0; i < hosts.Count; i++)
            {
                if (s.Remaining[i] <= 0) continue;
                // Only a carrier's EQDP may be rewritten, so only a carrier can publish a native surface undeformed.
                if (carrierOnly && !hosts[i].Carrier) continue;
                // Already taken by ANOTHER SURFACE. Identity, not cut-code equality: a host is built from exactly one surface's
                // sources, so sharing it would replace the first surface's geometry.
                if (s.HostClaim[i] is { } claimed && claimed != surfIdx) continue;
                eligible.Add(i);
            }

            int capacity = Math.Min(eligible.Sum(i => s.Remaining[i]), Budget(s, surfIdx));
            if (capacity == 0 && carrierOnly)
            {
                // Skipped, not squeezed: a native surface on a deforming host renders visibly wrong.
                s.Unhosted.Add((surfIdx, layerIdxs));
                continue;
            }

            int placeable = Math.Min(layerIdxs.Count, capacity);
            int dropCount = layerIdxs.Count - placeable;
            int cursor = layerIdxs.Count - 1;              // the TOP layer of THIS surface (its mask)
            foreach (var h in eligible)
            {
                if (cursor < dropCount) break;
                int take = Math.Min(s.Remaining[h], cursor - dropCount + 1);
                take = Math.Min(take, Budget(s, surfIdx));
                if (take <= 0) break;
                for (int k = cursor - take + 1; k <= cursor; k++)   // ascending → topmost lands last (highest idx)
                    s.Work.Add((layerIdxs[k], h));
                s.Remaining[h] -= take;
                s.DiskBudget -= take;
                Spend(s, surfIdx, h, take);
                s.HostClaim[h] = surfIdx;
                s.HostSurface[h] = surfIdx;
                cursor -= take;
            }
            for (int k = 0; k < dropCount; k++)            // the dropped bottom layers = over budget
                s.Dropped.Add(layerIdxs[k]);
        }
    }

    /// <summary>
    /// Content units take what the shells left, from the same state. One slot per unit, first host with room that may
    /// carry its surface; a unit that finds none is reported, never squeezed in over a shell.
    /// </summary>
    public static void PlaceContent(State s, IReadOnlyList<Host> hosts, IReadOnlyList<Surface> surfaces, IReadOnlyList<Unit> units)
    {
        for (int u = 0; u < units.Count; u++)
        {
            int surfIdx = units[u].Surface;
            bool carrierOnly = surfaces[surfIdx].CarrierOnly;
            // An effect's IMC entry is only ours to write on an invisible ACCESSORY carrier (see EffectHostPlanner), so its
            // placeholder goes nowhere else: on a worn ring it would rewrite the player's item for nothing visible.
            bool effectOnly = units[u].EffectOnly;
            // A piece with real geometry AND an effect tries the carriers first, so its effect has a host that can carry
            // it; it may still fall back to a worn item, where only its geometry is placed.
            var order = Enumerable.Range(0, hosts.Count);
            if (units[u].CarriesEffect && !effectOnly)
                order = order.OrderBy(h => hosts[h].EffectCarrier ? 0 : 1);
            int chosen = -1;
            foreach (var h in order)
            {
                if (Budget(s, surfIdx) <= 0) break;
                if (s.Remaining[h] <= 0) continue;
                // Only a carrier's EQDP may be rewritten, so only a carrier can publish a natively-authored piece undeformed.
                if (carrierOnly && !hosts[h].Carrier) continue;
                if (effectOnly && !hosts[h].EffectCarrier) continue;
                if (s.HostClaim[h] is { } claimed && claimed != surfIdx) continue;
                chosen = h;
                break;
            }
            if (chosen < 0) { s.ContentUnhosted.Add(u); continue; }

            s.Remaining[chosen]--;
            s.DiskBudget--;
            Spend(s, surfIdx, chosen, 1);
            s.HostClaim[chosen] = surfIdx;
            s.HostSurface[chosen] = surfIdx;
            s.ContentWork.Add((u, chosen));
        }
    }
}
