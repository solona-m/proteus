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
}
