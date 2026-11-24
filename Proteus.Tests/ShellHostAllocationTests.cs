using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Services.ShellHostAllocation;

namespace Proteus.Tests;

/// <summary>
/// Which host carries which second-skin layer and content piece. Fixtures mirror what HostChooser hands over: the
/// facewear carrier first, then the worn accessories (APPEND) in rir/ril/wrs/nek order, then Emperor's New carriers on
/// the FREE slots only (a worn slot is never offered as a carrier).
/// </summary>
public sealed class ShellHostAllocationTests
{
    private static readonly Host Glasses = new(10, Carrier: true, FacewearCarrier: true, EffectCarrier: false);
    private static readonly Host WornRing = new(8, Carrier: false, FacewearCarrier: false, EffectCarrier: false);
    private static readonly Host RingCarrier = new(10, Carrier: true, FacewearCarrier: false, EffectCarrier: true);

    private static readonly Surface Body = new(CarrierOnly: false, Face: false);
    private static readonly Surface Face = new(CarrierOnly: true, Face: true);
    private static readonly Surface Iris = new(CarrierOnly: true, Face: false);

    /// <summary>F1: glasses, rir + ril worn, carriers on wrs + nek.</summary>
    private static readonly Host[] F1 = [Glasses, WornRing, WornRing, RingCarrier, RingCarrier];
    /// <summary>F2: glasses + carriers on all four accessory slots.</summary>
    private static readonly Host[] F2 = [Glasses, RingCarrier, RingCarrier, RingCarrier, RingCarrier];
    /// <summary>F3: glasses only.</summary>
    private static readonly Host[] F3 = [Glasses];
    /// <summary>F4: glasses + every accessory slot worn, no carriers.</summary>
    private static readonly Host[] F4 = [Glasses, WornRing, WornRing, WornRing, WornRing];

    private static int[] Layers(params (int Surface, int Count)[] runs)
        => runs.SelectMany(r => Enumerable.Repeat(r.Surface, r.Count)).ToArray();

    private static int[] OnHost(State s, int host) => s.Work.Where(w => w.HostIdx == host).Select(w => w.LayerIdx).ToArray();

    private static int[] Range(int from, int toInclusive) => Enumerable.Range(from, toInclusive - from + 1).ToArray();

    private static State Distribute(Host[] hosts, Surface[] surfaces, int[] layerSurface, int disk = 36)
    {
        var s = Begin(hosts, disk);
        DistributeLayers(s, hosts, surfaces, layerSurface);
        return s;
    }

    // ── Commit 1: today's behaviour, pinned before the face reservation changes any of it ──────────────

    [Fact]
    public void Characterise_F1_Body25MaskLast()
    {
        var s = Distribute(F1, [Body], Layers((0, 25)));

        Assert.Equal(Range(15, 24), OnHost(s, 0));
        Assert.Equal(Range(7, 14), OnHost(s, 1));
        Assert.Equal(Range(0, 6), OnHost(s, 2));
        Assert.Empty(OnHost(s, 3));
        Assert.Equal([0, 0, 1, 10, 10], s.Remaining);
        Assert.Empty(s.Dropped);
        Assert.Equal(36 - 25, s.DiskBudget);
    }

    [Fact]
    public void Characterise_F2_FaceTakesFirstFreeCarrierAfterBody()
    {
        var s = Distribute(F2, [Body, Face], Layers((0, 12), (1, 2)));

        Assert.Equal(Range(2, 11), OnHost(s, 0));
        Assert.Equal(Range(0, 1), OnHost(s, 1));
        Assert.Equal([12, 13], OnHost(s, 2));
        Assert.Equal(0, s.HostSurface[0]);
        Assert.Equal(1, s.HostSurface[2]);
    }

    [Fact]
    public void Characterise_F3_FaceUnhosted()
    {
        // The body is always surface 0 (ShellSetBuild resolves it first).
        var s = Distribute(F3, [Body, Face], Layers((0, 3), (1, 2)));

        Assert.Equal([0, 1, 2], OnHost(s, 0));
        var unhosted = Assert.Single(s.Unhosted);
        Assert.Equal(1, unhosted.Surface);
        Assert.Equal([3, 4], unhosted.Layers);
    }

    [Fact]
    public void Characterise_OverBudgetDropsTheBottomLayersNeverTheMask()
    {
        var s = Distribute(F3, [Body], Layers((0, 13)));

        Assert.Equal(Range(3, 12), OnHost(s, 0));
        Assert.Equal([0, 1, 2], s.Dropped);
    }

    [Fact]
    public void Characterise_ContentEffectOrdering()
    {
        var s = Begin(F1, 36);
        PlaceContent(s, F1, [Body], [
            new Unit(0, EffectOnly: false, CarriesEffect: true),    // geometry + effect: an effect carrier first
            new Unit(0, EffectOnly: false, CarriesEffect: false),   // plain geometry: the first host with room
            new Unit(0, EffectOnly: true, CarriesEffect: true),     // effect only: an effect carrier or nothing
        ]);

        Assert.Equal([(0, 3), (1, 0), (2, 3)], s.ContentWork);
        Assert.Empty(s.ContentUnhosted);
    }

    [Fact]
    public void Characterise_ContentOfAnotherSurfaceSkipsAClaimedHost()
    {
        var s = Distribute(F2, [Body, Face], Layers((0, 3)));
        PlaceContent(s, F2, [Body, Face], [new Unit(1, EffectOnly: false, CarriesEffect: false)]);

        Assert.Equal([(0, 1)], s.ContentWork);       // the glasses carry the body; the face piece takes rir
    }

    // ── Commit 2: a mod on the face material holds the facewear carrier ───────────────────────────────

    private static State RunAll(Host[] hosts, Surface[] surfaces, int[] layerSurface, Unit[]? units = null, int disk = 36)
        => Run(hosts, surfaces, layerSurface, units ?? [], disk);

    [Fact]
    public void FaceLayersTakeTheGlassesBeforeTheBody() // [AC-face-glasses-priority-1]
    {
        var s = RunAll(F2, [Body, Face], Layers((0, 12), (1, 2)));

        Assert.Equal([12, 13], OnHost(s, 0));
        Assert.Equal(Range(2, 11), OnHost(s, 1));
        Assert.Equal(Range(0, 1), OnHost(s, 2));
        Assert.Equal(1, s.HostSurface[0]);
        Assert.Equal(new FacewearHold(0, 1, 2), s.Reservation);
        Assert.Empty(s.Dropped);
        Assert.Equal(0, s.DisplacedByReservation);
    }

    [Fact]
    public void FaceWinsTheGlassesWhenEveryRingSlotIsWorn() // [AC-face-glasses-priority-1]
    {
        var s = RunAll(F4, [Body, Face], Layers((0, 12), (1, 2)));

        Assert.Equal([12, 13], OnHost(s, 0));           // today: unhosted
        Assert.Empty(s.Unhosted);
        Assert.Equal(Range(4, 11), OnHost(s, 1));        // the body appends to the worn accessories
        Assert.Equal(Range(0, 3), OnHost(s, 2));
    }

    [Fact]
    public void WithoutFaceTheResultMatchesCharacterisation() // [AC-face-glasses-priority-2] [EDGE-face-glasses-priority-1]
    {
        foreach (var (hosts, count) in new[] { (F1, 25), (F2, 12), (F3, 13), (F4, 30) })
        {
            var before = Distribute(hosts, [Body], Layers((0, count)));
            var now = RunAll(hosts, [Body], Layers((0, count)));

            Assert.Null(now.Reservation);
            Assert.Equal(before.Work, now.Work);
            Assert.Equal(before.Remaining, now.Remaining);
            Assert.Equal(before.HostClaim, now.HostClaim);
            Assert.Equal(before.HostSurface, now.HostSurface);
            Assert.Equal(before.Dropped, now.Dropped);
            Assert.Equal(before.DiskBudget, now.DiskBudget);
        }
    }

    [Fact]
    public void ContentPlacementUnchangedWithoutFace() // [AC-face-glasses-priority-2]
    {
        Unit[] units = [new(0, false, true), new(0, false, false), new(0, true, true)];
        var before = Begin(F1, 36);
        PlaceContent(before, F1, [Body], units);
        var now = RunAll(F1, [Body], [], units);

        Assert.Equal(before.ContentWork, now.ContentWork);
        Assert.Equal(before.ContentUnhosted, now.ContentUnhosted);
    }

    [Fact]
    public void FaceContentPieceTakesTheGlassesAheadOfBodyContent() // [AC-face-glasses-priority-3] [EDGE-face-glasses-priority-5]
    {
        var s = RunAll(F2, [Body, Face], [], [new(0, false, false), new(0, false, false), new(1, false, false)]);

        Assert.Equal([(0, 1), (1, 1), (2, 0)], s.ContentWork);
        Assert.Equal(new FacewearHold(0, 1, 1), s.Reservation);
    }

    [Fact]
    public void NoFacewearCarrierNoReservation() // [EDGE-face-glasses-priority-2]
    {
        Host[] rings = [RingCarrier, RingCarrier, RingCarrier, RingCarrier];
        var s = RunAll(rings, [Body, Face], Layers((0, 12), (1, 2)));

        Assert.Null(s.Reservation);
        Assert.Equal([12, 13], OnHost(s, 2));
    }

    [Fact]
    public void FaceOverflowSpillsAfterTheBody() // [EDGE-face-glasses-priority-3]
    {
        var s = RunAll(F2, [Body, Face], Layers((0, 20), (1, 13)));

        Assert.Equal(Range(23, 32), OnHost(s, 0));       // the top 10 face layers
        Assert.Equal(Range(10, 19), OnHost(s, 1));       // the body fills the rings first
        Assert.Equal(Range(0, 9), OnHost(s, 2));
        Assert.Equal(Range(20, 22), OnHost(s, 3));       // the face's overflow, after the body
        Assert.Empty(s.Dropped);
    }

    [Fact]
    public void BodyKeepsTheGlassesWhenNothingElseCanCarryIt() // [EDGE-face-glasses-priority-4]
    {
        var s = RunAll(F3, [Body, Face], Layers((0, 3), (1, 2)));

        Assert.Null(s.Reservation);
        Assert.Equal([0, 1, 2], OnHost(s, 0));
        Assert.Equal([3, 4], Assert.Single(s.Unhosted).Layers);
    }

    [Fact]
    public void EffectFaceUnitsDoNotReserve() // [EDGE-face-glasses-priority-6]
    {
        var s = RunAll(F2, [Body, Face], Layers((0, 3)), [new(1, true, true), new(1, false, true)]);

        Assert.Null(s.Reservation);
        Assert.Equal([0, 1, 2], OnHost(s, 0));
    }

    [Fact]
    public void FirstFaceSurfaceWinsTheReservation() // [EDGE-face-glasses-priority-7]
    {
        var s = RunAll(F2, [Body, Face, Face], [0, 0, 0, 1, 2]);

        Assert.Equal(1, s.Reservation!.Surface);
        Assert.Equal([3], OnHost(s, 0));
        Assert.Equal([0, 1, 2], OnHost(s, 1));
        Assert.Equal([4], OnHost(s, 2));
    }

    [Fact]
    public void DiskBudgetHeldForTheFace() // [EDGE-face-glasses-priority-8]
    {
        var s = RunAll(F2, [Body, Face], Layers((0, 40), (1, 2)));

        Assert.Equal([40, 41], OnHost(s, 0));
        Assert.Equal(34, s.Work.Count(w => w.LayerIdx < 40));
        Assert.Equal(Range(0, 5), s.Dropped);
        Assert.Equal(0, s.DiskBudget);
    }

    [Fact]
    public void IrisAndHairGetNoPriority() // [EDGE-face-glasses-priority-9]
    {
        var s = RunAll(F2, [Body, Iris], Layers((0, 12), (1, 2)));

        Assert.Null(s.Reservation);
        Assert.Equal(Range(2, 11), OnHost(s, 0));
    }

    [Fact]
    public void DisplacedBodyDropsBottomNeverMaskAndReportsCause() // [EDGE-face-glasses-priority-10]
    {
        Host[] tight = [Glasses, WornRing];
        var s = RunAll(tight, [Body, Face], Layers((0, 12), (1, 2)));

        Assert.Equal([12, 13], OnHost(s, 0));
        Assert.Equal(Range(4, 11), OnHost(s, 1));        // the body's mask (11) is kept
        Assert.Equal(Range(0, 3), s.Dropped);
        Assert.Equal(4, s.DisplacedByReservation);       // drives the "didn't fit because…" log line
    }

    [Fact]
    public void DisplacedCountsOnlyBodyLayers() // [EDGE-face-glasses-priority-10]
    {
        // Face priority pushes the body onto the ring carrier the iris would have taken: the iris becoming unhosted is
        // not counted as "body layer(s) didn't fit".
        Host[] hosts = [Glasses, RingCarrier];
        var s = RunAll(hosts, [Body, Face, Iris], Layers((0, 5), (1, 1), (2, 1)));

        Assert.Equal([5], OnHost(s, 0));
        Assert.Equal([6], Assert.Single(s.Unhosted, u => u.Surface == 2).Layers);
        Assert.Empty(s.Dropped);
        Assert.Equal(0, s.DisplacedByReservation);
    }

    [Fact]
    public void ReservationUnusedOnADirectlyBuiltState() // [EDGE-face-glasses-priority-11]
    {
        var s = Begin(F2, 36);
        s.Reservation = new FacewearHold(0, 1, 2);
        Assert.True(ReservationUnused(s));

        s.ContentWork.Add((0, 0));
        Assert.False(ReservationUnused(s));

        var t = Begin(F2, 36);
        t.Reservation = new FacewearHold(0, 1, 2);
        t.Work.Add((5, 0));
        Assert.False(ReservationUnused(t));
        Assert.False(ReservationUnused(Begin(F2, 36)));  // no reservation, nothing to drop
    }

    [Fact]
    public void ReservedButUnbuiltOnlyWhenTheReservedHostIsMissing() // [EDGE-face-glasses-priority-12]
    {
        Assert.True(ReservedButUnbuilt(0, new HashSet<int> { 1, 2 }));
        Assert.False(ReservedButUnbuilt(0, new HashSet<int> { 0, 1 }));
        Assert.False(ReservedButUnbuilt(null, new HashSet<int>()));
    }
}
