using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for <see cref="CompositorService.Explain"/> — the ladder that says why a mod which is switched
/// on contributed nothing. Pure logic: no compositor, no collection, no character.
/// <para/>
/// The ladder is ordered, and the order is the behaviour under test as much as the individual rungs are.
/// A pack can be wrong in two ways at once, and which of them it is TOLD about decides whether the user
/// is sent somewhere useful or somewhere empty.
/// </summary>
public class InertModTests
{
    private static ResolutionDiagnostic Diag(
        SettingsRead read = SettingsRead.Ok, int groups = 0, string[]? empty = null,
        string[]? missing = null, bool unconditional = false, string[]? penumbraGroups = null)
        => new(read, groups, empty ?? [], missing ?? [], unconditional, penumbraGroups ?? []);

    /// <summary>The healthy defaults, so each test states only the thing it is about.</summary>
    private static CompositorService.InertReason Run(
        ResolutionDiagnostic diag,
        bool maskGroup = false, int masksOn = 0, bool maskGear = false,
        bool filtered = false, string wants = "", string have = "")
        => CompositorService.Explain(diag, maskGroup, masksOn, maskGear, filtered, wants, have);

    // ── rung 1: we could not find out ─────────────────────────────────────────

    [Fact]
    public void UnreadableSettings_IsNotDressedUpAsSomethingElse()
    {
        // Every other field is the shape of "nothing is ticked", and reporting that would be a claim
        // about the mod when the only true statement is one about Penumbra.
        var r = Run(Diag(SettingsRead.Unavailable, groups: 2, empty: ["Fabric", "Patterns"]));
        Assert.Equal(CompositorService.InertCause.SettingsUnreadable, r.Cause);
    }

    [Fact]
    public void NeverAskedIsNotTheSameAsNoAnswer()
    {
        // A pack with nothing selection-dependent short-circuits before the IPC hop. That is the healthy
        // state, not a failure — reporting it as one would make every unconditional pack look broken.
        var r = Run(Diag(SettingsRead.NotAsked, unconditional: true));
        Assert.NotEqual(CompositorService.InertCause.SettingsUnreadable, r.Cause);
    }

    // ── rung 2: the pack names groups Penumbra has not got ───────────────────

    [Fact]
    public void AllGroupsMissing_BlamesTheExport()
    {
        var r = Run(Diag(groups: 2, missing: ["Fabric", "Patterns"]));
        Assert.Equal(CompositorService.InertCause.GroupsMissing, r.Cause);
        Assert.Equal("Fabric, Patterns", r.Groups);
    }

    [Fact]
    public void OneStaleGroupAmongGood_IsNotAnExportProblem()
    {
        // One bad name out of three is a mod the user simply hasn't finished ticking; sending them to
        // the author over it would be wrong, and they can still fix it themselves.
        var r = Run(Diag(groups: 3, empty: ["Fabric", "Patterns"], missing: ["Trim"]));
        Assert.Equal(CompositorService.InertCause.NothingTicked, r.Cause);
    }

    // ── rung 3: nothing is ticked ────────────────────────────────────────────

    [Fact]
    public void NothingTicked_CountsMasksAsAGroupAndNamesThem()
    {
        // The reporting case: a freshly installed pack with Fabric, Patterns and Masks all empty. The
        // message has to name Masks too — it is the group that builds the garment, and a user sent to
        // Penumbra to tick "Fabric, Patterns" would come straight back.
        var r = Run(Diag(groups: 2, empty: ["Fabric", "Patterns"]), maskGroup: true, maskGear: true);
        Assert.Equal(CompositorService.InertCause.NothingTicked, r.Cause);
        Assert.Equal(3, r.GroupCount);
        Assert.Equal("Fabric, Patterns, Masks", r.Groups);
    }

    [Fact]
    public void NothingTicked_OmitsMasksWhenThePackShipsNone()
    {
        var r = Run(Diag(groups: 1, empty: ["Fabric"]));
        Assert.Equal(CompositorService.InertCause.NothingTicked, r.Cause);
        Assert.Equal(1, r.GroupCount);
        Assert.Equal("Fabric", r.Groups);
    }

    [Fact]
    public void MaskTickedButNoOverlayGroup_IsNotNothingTicked()
    {
        // Something IS ticked, so the ladder must fall through rather than tell the user to go and tick
        // the thing they have already ticked.
        var r = Run(Diag(groups: 1, empty: ["Fabric"]), maskGroup: true, masksOn: 2);
        Assert.NotEqual(CompositorService.InertCause.NothingTicked, r.Cause);
    }

    // ── rung 4: built for another body ───────────────────────────────────────

    [Fact]
    public void FilteredMaterialsWithAKnownWearer_NamesBothSides()
    {
        var r = Run(Diag(groups: 1, empty: []), filtered: true,
                    wants: "bibo · Midlander F", have: "gen3 · Roegadyn F");
        Assert.Equal(CompositorService.InertCause.WrongRace, r.Cause);
        Assert.Equal("bibo · Midlander F", r.Wants);
        Assert.Equal("gen3 · Roegadyn F", r.Have);
    }

    [Fact]
    public void FilteredMaterialsMidRedraw_DoesNotAccuseTheMod()
    {
        // The snapshot legitimately reports no char code at all while a redraw is settling. Claiming a
        // race mismatch off that would libel every correctly-authored pack once per race change.
        var r = Run(Diag(groups: 1, empty: []), filtered: true, wants: "bibo · Midlander F", have: "");
        Assert.Equal(CompositorService.InertCause.NothingReached, r.Cause);
    }

    // ── rung 5: a mask shell with no mask ────────────────────────────────────

    [Fact]
    public void MaskLayerIsGearWithNoMaskTicked_SaysSo()
    {
        // Fabric is ticked (so rung 3 does not fire) but the garment is built FROM a mask, and there
        // isn't one — the fabric has nowhere to land.
        var r = Run(Diag(groups: 1, empty: []), maskGroup: true, masksOn: 0, maskGear: true);
        Assert.Equal(CompositorService.InertCause.MaskNeedsShell, r.Cause);
        Assert.Equal(SidecarDiscoveryService.MaskGroupName, r.Groups);
    }

    [Fact]
    public void MaskLayerOnSkin_IsNotAShellProblem()
    {
        var r = Run(Diag(groups: 1, empty: []), maskGroup: true, masksOn: 0, maskGear: false);
        Assert.Equal(CompositorService.InertCause.NothingReached, r.Cause);
    }

    [Fact]
    public void WrongRaceOutranksMaskShell()
    {
        // Both are true of a Bibo-only mask pack worn by a gen3 character with no mask ticked. Ticking a
        // mask would change nothing, so the race is the fact worth having.
        var r = Run(Diag(groups: 1, empty: []), maskGroup: true, maskGear: true,
                    filtered: true, wants: "bibo · Midlander F", have: "gen3 · Viera F");
        Assert.Equal(CompositorService.InertCause.WrongRace, r.Cause);
    }

    // ── rung 6: the honest shrug ─────────────────────────────────────────────

    [Fact]
    public void EverythingResolvedAndNothingArrived_SaysNothingMore()
    {
        var r = Run(Diag(groups: 1, empty: []));
        Assert.Equal(CompositorService.InertCause.NothingReached, r.Cause);
    }

    [Fact]
    public void FlatOverlaysWithNoGroups_CannotBeTheUsersDoing()
    {
        // A pack with top-level Overlays and no groups has nothing to select, so "tick an option" is
        // never the answer for it.
        var r = Run(Diag(unconditional: true));
        Assert.Equal(CompositorService.InertCause.NothingReached, r.Cause);
    }

    // ── content packs: the merged diagnostic ─────────────────────────────────
    //
    // A content pack has no OptionGroups at all, so before the two halves were merged the ladder saw an
    // empty overlay diagnostic and shrugged at exactly the case it exists to explain.

    [Fact]
    public void ContentOnlyPackWithNothingTicked_SaysNothingIsTicked()
    {
        // What a freshly installed geometry pack looks like: no overlay groups, one content group, empty.
        var overlays = ResolutionDiagnostic.None;
        var content  = Diag(groups: 1, empty: ["Pieces"]);
        var r = Run(overlays.Merge(content));
        Assert.Equal(CompositorService.InertCause.NothingTicked, r.Cause);
        Assert.Equal(1, r.GroupCount);
        Assert.Equal("Pieces", r.Groups);
    }

    [Fact]
    public void ContentTickedWithOverlayGroupsEmpty_IsNotNothingTicked()
    {
        // The false accusation the merge exists to prevent: the user HAS ticked a content option (so the
        // content half reports no empty group), and telling them nothing is ticked would be a lie.
        var overlays = Diag(groups: 2, empty: ["Fabric", "Patterns"]);
        var content  = Diag(groups: 1);
        var r = Run(overlays.Merge(content));
        Assert.NotEqual(CompositorService.InertCause.NothingTicked, r.Cause);
    }

    [Fact]
    public void MergeNamesBothHalvesGroups()
    {
        var r = Run(Diag(groups: 1, empty: ["Fabric"]).Merge(Diag(groups: 1, empty: ["Pieces"])));
        Assert.Equal(CompositorService.InertCause.NothingTicked, r.Cause);
        Assert.Equal(2, r.GroupCount);
        Assert.Equal("Fabric, Pieces", r.Groups);
    }

    [Fact]
    public void MergeTakesTheWorstSettingsState()
    {
        // One unreadable half makes the whole answer untrustworthy — the merge must not let a half that
        // never needed to ask vouch for one that asked and got nothing.
        var merged = Diag(SettingsRead.NotAsked).Merge(Diag(SettingsRead.Unavailable, groups: 1));
        Assert.Equal(SettingsRead.Unavailable, merged.Settings);
        Assert.Equal(CompositorService.InertCause.SettingsUnreadable, Run(merged).Cause);

        Assert.Equal(SettingsRead.Ok, Diag(SettingsRead.NotAsked).Merge(Diag()).Settings);
        Assert.Equal(SettingsRead.NotAsked,
            Diag(SettingsRead.NotAsked).Merge(Diag(SettingsRead.NotAsked)).Settings);
    }

    [Fact]
    public void MergeKeepsWhicheverHalfReadTheManifest()
    {
        // PenumbraGroups is how the mask question gets answered without a second parse of meta.json, so
        // it has to survive a merge with a half that never opened the file.
        var merged = ResolutionDiagnostic.None.Merge(Diag(penumbraGroups: ["Fabric", "Masks"]));
        Assert.Equal(["Fabric", "Masks"], merged.PenumbraGroups);
    }
}
