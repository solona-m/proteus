using System;
using System.Collections.Generic;
using System.Text.Json;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for the design-binding pieces that are pure / serialization-only:
/// the gear-match heuristic predicate, the binding store round-trip, and the
/// in-memory color-override resolution. No Dalamud / game data required.
/// </summary>
public class DesignBindingTests
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // Build a design JObject: each tuple is an equipment slot with an item id and an Apply flag.
    private static JObject Design(params (string slot, ulong item, bool apply)[] slots)
    {
        var eq = new JObject();
        foreach (var (slot, item, apply) in slots)
            eq[slot] = new JObject { ["ItemId"] = item, ["Apply"] = apply };
        return new JObject { ["Equipment"] = eq };
    }

    // Build a player-state JObject: every slot is "applied" (full applied state).
    private static JObject State(params (string slot, ulong item)[] slots)
    {
        var eq = new JObject();
        foreach (var (slot, item) in slots)
            eq[slot] = new JObject { ["ItemId"] = item, ["Apply"] = true };
        return new JObject { ["Equipment"] = eq };
    }

    private static bool Matches(JObject design, JObject state)
        => DesignBindingService.StateMatches(design, state, out _);

    private static int Specificity(JObject design, JObject state)
    {
        DesignBindingService.StateMatches(design, state, out var spec);
        return spec;
    }

    // ── Fluent fingerprint builders (mutate + return the JObject) ──────────────

    private static JObject WithStain(JObject o, string slot, ulong stain, ulong stain2, bool applyStain = true)
    {
        var eq = (JObject)(o["Equipment"] ??= new JObject());
        var s  = (JObject)(eq[slot] ??= new JObject());
        s["Stain"]      = stain;
        s["Stain2"]     = stain2;
        s["ApplyStain"] = applyStain;
        return o;
    }

    private static JObject WithBonus(JObject o, string slot, ulong bonusId, bool apply = true)
    {
        var b = (JObject)(o["Bonus"] ??= new JObject());
        b[slot] = new JObject { ["BonusId"] = bonusId, ["Apply"] = apply };
        return o;
    }

    private static JObject WithCustomize(JObject o, string index, long value, bool apply = true)
    {
        var c = (JObject)(o["Customize"] ??= new JObject());
        c[index] = new JObject { ["Value"] = value, ["Apply"] = apply };
        return o;
    }

    private static JObject WithParameter(JObject o, string flag, bool apply, params (string field, double val)[] fields)
    {
        var p = (JObject)(o["Parameters"] ??= new JObject());
        var e = new JObject { ["Apply"] = apply };
        foreach (var (f, v) in fields)
            e[f] = v;
        p[flag] = e;
        return o;
    }

    // ── StateMatches: equipment baseline (formerly GearMatches) ────────────────

    [Fact]
    public void StateMatches_AllAppliedSlotsEqual_AndEnoughSlots_IsTrue()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var state  = State(("Head", 1), ("Body", 2), ("Hands", 3), ("Legs", 99));
        Assert.True(Matches(design, state));
    }

    [Fact]
    public void StateMatches_WeaponSlotsAreIgnored()
    {
        // The applied design matches the outfit in every armor slot but the drawn weapon differs
        // (job/gearset/sheathe state) — must still match, because MainHand/OffHand are excluded.
        var design = Design(("MainHand", 8654, true), ("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var state  = State(("MainHand", 16060), ("Head", 1), ("Body", 2), ("Hands", 3));
        Assert.True(Matches(design, state));
    }

    [Fact]
    public void StateMatches_OneAppliedItemDiffers_IsFalse()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var state  = State(("Head", 1), ("Body", 2), ("Hands", 7)); // hands differ
        Assert.False(Matches(design, state));
    }

    [Fact]
    public void StateMatches_FewerThanMinimumAppliedSlots_IsFalse()
    {
        // Only two applied slots — below MinGearSlots — even though they match.
        var design = Design(("Head", 1, true), ("Body", 2, true));
        var state  = State(("Head", 1), ("Body", 2));
        Assert.False(Matches(design, state));
    }

    [Fact]
    public void StateMatches_UnappliedSlotsAreIgnored()
    {
        // Legs is present but Apply=false with a mismatching item — must be ignored.
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true), ("Legs", 555, false));
        var state  = State(("Head", 1), ("Body", 2), ("Hands", 3), ("Legs", 1));
        Assert.True(Matches(design, state));
    }

    [Fact]
    public void StateMatches_StateMissingAppliedSlot_IsFalse()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var state  = State(("Head", 1), ("Body", 2)); // no Hands in state
        Assert.False(Matches(design, state));
    }

    [Fact]
    public void StateMatches_MetaEntriesWithoutItemId_AreIgnored()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        // Add a meta entry (no ItemId), like Hat/Visor — should be skipped, not crash.
        ((JObject)design["Equipment"]!)["Hat"] = new JObject { ["Show"] = true, ["Apply"] = true };
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        Assert.True(Matches(design, state));
    }

    [Fact]
    public void StateMatches_MissingEquipmentObject_IsFalse()
    {
        Assert.False(Matches(new JObject(), State(("Head", 1))));
        Assert.False(Matches(Design(("Head", 1, true)), new JObject()));
    }

    // ── StateMatches: dyes / stains ────────────────────────────────────────────

    [Fact]
    public void StateMatches_SameGearDifferentAppliedDye_IsFalse()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithStain(design, "Body", stain: 10, stain2: 0);
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithStain(state, "Body", stain: 20, stain2: 0); // player is wearing a different dye
        Assert.False(Matches(design, state));
    }

    [Fact]
    public void StateMatches_MatchingAppliedDye_CountsTowardSpecificity()
    {
        var gearOnly = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var dyed     = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithStain(dyed, "Body", stain: 10, stain2: 5);

        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithStain(state, "Body", stain: 10, stain2: 5);

        Assert.True(Matches(gearOnly, state));
        Assert.True(Matches(dyed, state));
        // The dyed design constrains one extra field, so it is strictly more specific.
        Assert.True(Specificity(dyed, state) > Specificity(gearOnly, state));
    }

    [Fact]
    public void StateMatches_UnappliedDyeIsIgnored()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithStain(design, "Body", stain: 10, stain2: 0, applyStain: false); // dye present but not applied
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithStain(state, "Body", stain: 99, stain2: 0); // mismatching dye must be tolerated
        Assert.True(Matches(design, state));
    }

    // ── StateMatches: bonus items ──────────────────────────────────────────────

    [Fact]
    public void StateMatches_BonusItemMismatch_IsFalse()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithBonus(design, "Glasses", bonusId: 4);
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithBonus(state, "Glasses", bonusId: 7); // different glasses
        Assert.False(Matches(design, state));
    }

    [Fact]
    public void StateMatches_UnappliedBonusItemIsIgnored()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithBonus(design, "Glasses", bonusId: 4, apply: false);
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithBonus(state, "Glasses", bonusId: 7);
        Assert.True(Matches(design, state));
    }

    // Proteus's own invisible-glasses host sits in the Glasses slot without the player choosing it. If it
    // were compared like a real bonus item, EVERY design that saved the slot would stop matching, the
    // apply would be treated as unbound, and HandleUnboundDesign would disable all Proteus mods.
    [Fact]
    public void Neutralize_SyntheticGlassesReadAsEmpty()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithBonus(design, "Glasses", bonusId: 0);          // design saved "no glasses"
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithBonus(state, "Glasses", bonusId: 1);           // ...but Proteus injected its host

        Assert.False(Matches(design, state));              // raw state: our host looks like a real choice
        var clean = DesignBindingService.NeutralizeProteusOwnedState(state, syntheticGlassesId: 1);
        Assert.True(Matches(design, clean));
    }

    // Only OUR item is discounted — glasses the player actually chose still take part in the match.
    [Fact]
    public void Neutralize_PlayerChosenGlassesStillCompared()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithBonus(design, "Glasses", bonusId: 4);
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithBonus(state, "Glasses", bonusId: 7);           // a different, player-picked pair

        var clean = DesignBindingService.NeutralizeProteusOwnedState(state, syntheticGlassesId: 1);
        Assert.False(Matches(design, clean));
    }

    // The normalizer must not mutate the caller's state object — the raw state is reused elsewhere.
    [Fact]
    public void Neutralize_DoesNotMutateInput()
    {
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithBonus(state, "Glasses", bonusId: 1);

        var clean = DesignBindingService.NeutralizeProteusOwnedState(state, syntheticGlassesId: 1);

        Assert.Equal(1ul, ((JObject)state["Bonus"]!["Glasses"]!)["BonusId"]!.ToObject<ulong>());
        Assert.Equal(0ul, ((JObject)clean["Bonus"]!["Glasses"]!)["BonusId"]!.ToObject<ulong>());
    }

    // A design that applies the glasses slot but carries no BonusId must still be REJECTED (IdEquals
    // treats a missing id as a mismatch); coercing absent ids to 0 would let it match spuriously.
    [Fact]
    public void StateMatches_MissingBonusIdIsRejected()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var dBonus = (JObject)(design["Bonus"] ??= new JObject());
        dBonus["Glasses"] = new JObject { ["Apply"] = true };   // applied, but no BonusId
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithBonus(state, "Glasses", bonusId: 0);

        Assert.False(Matches(design, state));
    }

    // ── StateMatches: customize ────────────────────────────────────────────────

    [Fact]
    public void StateMatches_CustomizeValueMismatch_IsFalse()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithCustomize(design, "SkinColor", value: 12);
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithCustomize(state, "SkinColor", value: 30);
        Assert.False(Matches(design, state));
    }

    [Fact]
    public void StateMatches_WetnessIsIgnored()
    {
        // Wetness lives in Customize with a bool Value; it is situational and must be skipped
        // (both that it isn't compared and that its bool Value doesn't crash the numeric compare).
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        ((JObject)(design["Customize"] ??= new JObject()))["Wetness"] = new JObject { ["Value"] = true, ["Apply"] = true };
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        ((JObject)(state["Customize"] ??= new JObject()))["Wetness"] = new JObject { ["Value"] = false, ["Apply"] = true };
        Assert.True(Matches(design, state));
    }

    [Fact]
    public void StateMatches_CustomizeArrayFormIsSkipped()
    {
        // Non-human models serialize customize as a base64 "Array" scalar, not per-index objects.
        // It has nothing to compare and must not crash or reject.
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        ((JObject)(design["Customize"] ??= new JObject()))["Array"] = "AAAA==";
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        ((JObject)(state["Customize"] ??= new JObject()))["Array"] = "BBBB==";
        Assert.True(Matches(design, state));
    }

    // ── StateMatches: advanced parameters ──────────────────────────────────────

    [Fact]
    public void StateMatches_ParameterMismatch_IsFalse()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithParameter(design, "SkinDiffuse", apply: true, ("Red", 0.5), ("Green", 0.5), ("Blue", 0.5));
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithParameter(state, "SkinDiffuse", apply: true, ("Red", 0.9), ("Green", 0.5), ("Blue", 0.5));
        Assert.False(Matches(design, state));
    }

    [Fact]
    public void StateMatches_ParameterWithinTolerance_Matches()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithParameter(design, "SkinDiffuse", apply: true, ("Red", 0.5));
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithParameter(state, "SkinDiffuse", apply: true, ("Red", 0.50000001)); // float round-trip noise
        Assert.True(Matches(design, state));
    }

    [Fact]
    public void StateMatches_UnappliedParameterIsIgnored()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithParameter(design, "SkinDiffuse", apply: false, ("Red", 0.5));
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithParameter(state, "SkinDiffuse", apply: true, ("Red", 0.9));
        Assert.True(Matches(design, state));
    }

    // ── IsApplySignal ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(StateFinalizationType.DesignApplied)]     // StateEditor, when settings.IsFinal
    [InlineData(StateFinalizationType.ReapplyAutomation)] // AutoDesignApplier: automation-applied designs
    public void IsApplySignal_DesignApplications_AreSignals(StateFinalizationType type)
        => Assert.True(DesignBindingService.IsApplySignal(type));

    // Glamourer raises plain Reapply from ReapplyState only: the IPC call (our own post-composite
    // reapply), /glamour reapply, a UI button, Penumbra auto-redraw. No design-application path ends
    // there, so treating it as an apply signal just fed the heuristic our own echo.
    [Fact]
    public void IsApplySignal_PlainReapply_IsNotADesignApplication()
        => Assert.False(DesignBindingService.IsApplySignal(StateFinalizationType.Reapply));

    // Measured in-game: a gearset change and a revert both arrived as Reapply/Reset on the OLD
    // StateChangeType signal, so the heuristic ran on both — a gearset swap restored an unrelated design,
    // and a revert reached HandleUnboundDesign and disabled every Proteus mod. The finalization type tells
    // them apart, and neither may re-evaluate the applied design.
    [Theory]
    [InlineData(StateFinalizationType.Gearset)]           // gear moved, the design did not
    [InlineData(StateFinalizationType.Revert)]
    [InlineData(StateFinalizationType.RevertAutomation)]  // observed: previously hit the disable-all path
    [InlineData(StateFinalizationType.RevertCustomize)]
    [InlineData(StateFinalizationType.RevertEquipment)]
    [InlineData(StateFinalizationType.RevertAdvanced)]
    [InlineData(StateFinalizationType.ModelChange)]
    public void IsApplySignal_NonApplications_AreNotSignals(StateFinalizationType type)
        => Assert.False(DesignBindingService.IsApplySignal(type));

    // A revert leaves no design applied, so the active override must be dropped — otherwise the previous
    // design's colours stay composited onto a character reverted to vanilla.
    [Theory]
    [InlineData(StateFinalizationType.Revert)]
    [InlineData(StateFinalizationType.RevertCustomize)]
    [InlineData(StateFinalizationType.RevertEquipment)]
    [InlineData(StateFinalizationType.RevertAdvanced)]
    public void IsRevertSignal_Reverts_ClearTheOverride(StateFinalizationType type)
        => Assert.True(DesignBindingService.IsRevertSignal(type));

    // RevertAutomation is followed by a Reapply that restores the right design, so clearing on it would
    // only add a wasted clear-then-restore pair. Applications obviously must not be treated as reverts.
    [Theory]
    [InlineData(StateFinalizationType.RevertAutomation)]
    [InlineData(StateFinalizationType.DesignApplied)]
    [InlineData(StateFinalizationType.Reapply)]
    [InlineData(StateFinalizationType.Gearset)]
    public void IsRevertSignal_OthersAreNotReverts(StateFinalizationType type)
        => Assert.False(DesignBindingService.IsRevertSignal(type));

    // The two sets must never overlap: a type that both applies and clears would race itself.
    [Fact]
    public void ApplyAndRevertSignals_AreDisjoint()
    {
        foreach (StateFinalizationType t in Enum.GetValues<StateFinalizationType>())
            Assert.False(DesignBindingService.IsApplySignal(t) && DesignBindingService.IsRevertSignal(t),
                $"{t} is classified as both an application and a revert");
    }

    // ── IsInferredAutomationApply ────────────────────────────────────────────────

    // Automation applying a design on a gearset/job change raises no apply signal of its own: the apply
    // runs with StateSource.Fixed (empty actor set → no Design/DesignApplied over IPC) and is followed by
    // ReapplyState(isFinal: false) rather than ReapplyAutomationState. All that arrives is the game's
    // Gearset — preceded, a moment earlier, by the Reapply that automation caused.
    [Fact]
    public void InferredAutomationApply_ReapplyThenGearset_IsAnApply()
        => Assert.True(DesignBindingService.IsInferredAutomationApply(
            StateFinalizationType.Gearset, msSinceForeignReapply: 30, isOwnRedrawEcho: false));

    // The pairing is what separates "automation re-asserted state" from "the game loaded gear"; a Gearset
    // that stands alone is just a gear change and must not re-evaluate anything.
    [Theory]
    [InlineData(3000)]              // outside the pair window
    [InlineData(long.MaxValue)]     // no foreign reapply this session at all
    public void InferredAutomationApply_GearsetWithoutAPairedReapply_IsNotAnApply(long msSinceForeignReapply)
        => Assert.False(DesignBindingService.IsInferredAutomationApply(
            StateFinalizationType.Gearset, msSinceForeignReapply, isOwnRedrawEcho: false));

    // Proteus's own redraw manufactures the exact same pair — the draw object reload reports Gearset, and
    // Penumbra's mod-setting change makes Glamourer reapply state right after — so the one Gearset our own
    // redraw is owed gets discounted no matter how perfect its timing looks.
    [Fact]
    public void InferredAutomationApply_OurOwnRedrawEcho_IsNotAnApply()
        => Assert.False(DesignBindingService.IsInferredAutomationApply(
            StateFinalizationType.Gearset, msSinceForeignReapply: 30, isOwnRedrawEcho: true));

    // Only Gearset is inferred from. Every other type either reports an application honestly (and goes
    // through IsApplySignal) or means something else entirely — perfect timings must not promote it.
    [Theory]
    [InlineData(StateFinalizationType.Reapply)]
    [InlineData(StateFinalizationType.DesignApplied)]
    [InlineData(StateFinalizationType.ReapplyAutomation)]
    [InlineData(StateFinalizationType.Revert)]
    [InlineData(StateFinalizationType.RevertAutomation)]
    [InlineData(StateFinalizationType.ModelChange)]
    public void InferredAutomationApply_OtherTypes_AreNeverInferred(StateFinalizationType type)
        => Assert.False(DesignBindingService.IsInferredAutomationApply(
            type, msSinceForeignReapply: 30, isOwnRedrawEcho: false));

    // The inference is additive: it must not quietly reclassify anything the reported signals already own.
    [Fact]
    public void InferredAutomationApply_NeverOverlapsTheReportedSignals()
    {
        foreach (StateFinalizationType t in Enum.GetValues<StateFinalizationType>())
        {
            var inferred = DesignBindingService.IsInferredAutomationApply(t, 30, false);
            Assert.False(inferred && DesignBindingService.IsApplySignal(t),  $"{t} is both reported and inferred");
            Assert.False(inferred && DesignBindingService.IsRevertSignal(t), $"{t} is both a revert and an apply");
        }
    }

    // ── Store round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void BindingStore_RoundTrips_ThroughJson()
    {
        var id = Guid.NewGuid();
        var store = new DesignBindingStore();
        store.Bindings[id] = new DesignBinding
        {
            DesignId    = id,
            DesignName  = "Outfit A",
            CapturedUtc = new DateTime(2026, 5, 27, 12, 0, 0, DateTimeKind.Utc),
            Mods =
            [
                new ProteusModBinding
                {
                    ModDirectory = "SomeMod",
                    Enabled      = true,
                    Priority     = 5,
                    Options      = new() { ["Length"] = ["Thigh-high"] },
                    Colors       = new OverlayColorOverride
                    {
                        Top = [ new ColorTableRowPreset { Row = 16, SubRowA = new() { Diffuse = "#FF0000", Opacity = -20 } } ],
                        Options = new()
                        {
                            ["Length"] = new()
                            {
                                ["Thigh-high"] = [ new ColorTableRowPreset { Row = 3, SubRowB = new() { Emissive = 0.5f } } ],
                            },
                        },
                    },
                },
            ],
        };

        var back = JsonSerializer.Deserialize<DesignBindingStore>(JsonSerializer.Serialize(store, JsonOpts), JsonOpts);

        Assert.NotNull(back);
        Assert.True(back!.Bindings.ContainsKey(id));
        var mod = back.Bindings[id].Mods[0];
        Assert.Equal("SomeMod", mod.ModDirectory);
        Assert.True(mod.Enabled);
        Assert.Equal(5, mod.Priority);
        Assert.Equal(["Thigh-high"], mod.Options["Length"]);
        Assert.Equal("#FF0000", mod.Colors.Top![0].SubRowA!.Diffuse);
        Assert.Equal(-20, mod.Colors.Top![0].SubRowA!.Opacity);
        Assert.Equal(0.5f, mod.Colors.Options!["Length"]["Thigh-high"][0].SubRowB!.Emissive);
    }

    // ── OverlayColorOverride.Resolve ─────────────────────────────────────────────

    [Fact]
    public void Resolve_PrefersMatchingOption_FallsBackToTop()
    {
        var top    = new List<ColorTableRowPreset> { new() { Row = 16 } };
        var optRows = new List<ColorTableRowPreset> { new() { Row = 3 } };
        var ovr = new OverlayColorOverride
        {
            Top = top,
            Options = new() { ["Length"] = new() { ["Thigh-high"] = optRows } },
        };

        Assert.Same(optRows, ovr.Resolve("Length", "Thigh-high")); // exact option
        Assert.Same(top, ovr.Resolve("Length", "Ankle"));          // option not stored → top
        Assert.Same(top, ovr.Resolve(null, null));                 // no option context → top
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNothingStored()
    {
        var ovr = new OverlayColorOverride();
        Assert.Null(ovr.Resolve(null, null));
        Assert.Null(ovr.Resolve("g", "o"));
    }

    /// <summary>
    /// A content pack's glow and a mod's overlay gear settings must not reach each other.
    /// <para/>
    /// Top is captured from the mod's first OVERLAY descriptor. A mod that ships both — the mixed case this
    /// branch already supports — would otherwise hand its imported meshes whatever scroll effect one of its
    /// overlays happens to use, and the piece would start glowing on its own.
    /// </summary>
    [Fact]
    public void ResolveContent_TakesTheContentSlot_NeverTheOverlaysTop()
    {
        var overlayTop = new GearSettingsPreset { Scroll = "overlay-fire.png" };
        var contentTop = new GearSettingsPreset { Scroll = "content-rainbow.png" };
        var perOption  = new GearSettingsPreset { Scroll = "option-stars.png" };
        var ovr = new OverlayGearOverride
        {
            Top = overlayTop,
            Content = contentTop,
            Options = new() { ["Metal"] = new() { ["Gold"] = perOption } },
        };

        // An option both kinds could name resolves to that option either way — the collision is inherent to
        // keying by Penumbra group/option, and it is the one place they legitimately share.
        Assert.Same(perOption, ovr.Resolve("Metal", "Gold"));
        Assert.Same(perOption, ovr.ResolveContent("Metal", "Gold"));

        // Everywhere else they part company. Overlays fall back to Top; content falls back to Content.
        Assert.Same(overlayTop, ovr.Resolve(null, null));
        Assert.Same(contentTop, ovr.ResolveContent(null, null));
        Assert.Same(overlayTop, ovr.Resolve("Metal", "Silver"));
        Assert.Same(contentTop, ovr.ResolveContent("Metal", "Silver"));

        // A mod with overlays but no content glow gets NO glow on its pieces — not the overlay's.
        var overlaysOnly = new OverlayGearOverride { Top = overlayTop };
        Assert.Same(overlayTop, overlaysOnly.Resolve(null, null));
        Assert.Null(overlaysOnly.ResolveContent(null, null));

        // And the reverse: a pure content pack's glow never reaches an overlay added to it later.
        var contentOnly = new OverlayGearOverride { Content = contentTop };
        Assert.Null(contentOnly.Resolve(null, null));
        Assert.Same(contentTop, contentOnly.ResolveContent(null, null));
    }

    // ── SkinToneMask through the gear snapshot ─────────────────────────────────

    /// <summary>
    /// SkinToneMask has to survive From → Clone → ApplyTo, because that is the whole route a design
    /// binding takes: CaptureGear snapshots with From, the store round-trips, and ApplyTo puts it back on
    /// a descriptor clone the compositor then reads.
    /// <para/>
    /// From is the one that bites if forgotten. GetEditableGearOverride seeds a brand-new per-option
    /// preset with From(seed) the first time a panel is opened under an active binding — so a From that
    /// dropped this would silently reset an author's 0 to full suppression just for looking at the tab.
    /// </summary>
    [Fact]
    public void GearSettingsPreset_CarriesSkinToneMask_ThroughFromCloneAndApply()
    {
        var authored = new OverlayDescriptor { Diffuse = "skin.png", SkinToneMask = 0f };

        var snap = GearSettingsPreset.From(authored);
        Assert.Equal(0f, snap.SkinToneMask);

        var copy = snap.Clone();
        Assert.Equal(0f, copy.SkinToneMask);
        copy.SkinToneMask = 0.25f;
        Assert.Equal(0f, snap.SkinToneMask);          // the clone is independent

        var target = new OverlayDescriptor { Diffuse = "skin.png" };
        copy.ApplyTo(target);
        Assert.Equal(0.25f, target.SkinToneMask);

        // Unset stays unset rather than coalescing to 1 on the way through.
        Assert.Null(GearSettingsPreset.From(new OverlayDescriptor()).SkinToneMask);
    }

    /// <summary>
    /// A binding that says nothing about skin tint must leave the mod's own value alone.
    /// <para/>
    /// This is the upgrade path, and it is the whole reason ApplyTo treats this field differently from
    /// Shader and Scroll. SkinToneMask was added to a type that was already being persisted, and
    /// design_bindings.json has no migration — Version is written and never read — so every binding saved
    /// before it existed loads with null. An unconditional write would push that null onto the descriptor
    /// and undo an author's SkinToneMask = 0, paling imported skins again for exactly the users who had
    /// bound the mod to a design.
    /// </summary>
    [Fact]
    public void ApplyTo_WithNoSkinToneMask_LeavesTheAuthorsValueAlone()
    {
        var target = new OverlayDescriptor { Diffuse = "skin.png", SkinToneMask = 0f };
        new GearSettingsPreset().ApplyTo(target);
        Assert.Equal(0f, target.SkinToneMask);
    }

    /// <summary>
    /// The other half of that bargain: an explicit value still overrides the author, including an
    /// explicit 1. The editor never stores null in an override for exactly this reason — dragging a
    /// binding's slider to full suppression has to be distinguishable from the binding being silent, or
    /// the author's 0 would quietly win back.
    /// </summary>
    [Fact]
    public void ApplyTo_WithAnExplicitSkinToneMask_OverridesTheAuthorsValue()
    {
        var target = new OverlayDescriptor { Diffuse = "skin.png", SkinToneMask = 0f };
        new GearSettingsPreset { SkinToneMask = 1f }.ApplyTo(target);
        Assert.Equal(1f, target.SkinToneMask);

        new GearSettingsPreset { SkinToneMask = 0.25f }.ApplyTo(target);
        Assert.Equal(0.25f, target.SkinToneMask);
    }

    // ── PickMostRecent ─────────────────────────────────────────────────────────

    [Fact]
    public void PickMostRecent_ReturnsBindingWithLatestCapturedUtc()
    {
        var older  = Guid.NewGuid();
        var newer  = Guid.NewGuid();
        var newest = Guid.NewGuid();
        var bindings = new Dictionary<Guid, DesignBinding>
        {
            [older]  = new() { DesignId = older,  CapturedUtc = new(2026, 5, 27, 12, 0, 0, DateTimeKind.Utc) },
            [newer]  = new() { DesignId = newer,  CapturedUtc = new(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc) },
            [newest] = new() { DesignId = newest, CapturedUtc = new(2026, 5, 28, 19, 51, 0, DateTimeKind.Utc) },
        };
        Assert.Equal(newest, DesignBindingService.PickMostRecent(new[] { older, newer, newest }, bindings));
        Assert.Equal(newer,  DesignBindingService.PickMostRecent(new[] { older, newer },         bindings));
        Assert.Equal(older,  DesignBindingService.PickMostRecent(new[] { older },                bindings));
    }

    [Fact]
    public void PickMostRecent_MissingBindingTreatedAsOldest()
    {
        // An ID present in `matches` but missing from the store can occur transiently if a binding
        // is removed between match-collection and pick. Such IDs must not be preferred.
        var present = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var bindings = new Dictionary<Guid, DesignBinding>
        {
            [present] = new() { DesignId = present, CapturedUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        };
        Assert.Equal(present, DesignBindingService.PickMostRecent(new[] { missing, present }, bindings));
    }

    // ── BootIdStillApplies (boot restore, step 1) ──────────────────────────────

    [Fact]
    public void BootIdStillApplies_RememberedDesignStillWorn_IsTrue()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var state  = State(("Head", 1), ("Body", 2), ("Hands", 3));
        Assert.True(DesignBindingService.BootIdStillApplies(design, state));
    }

    [Fact]
    public void BootIdStillApplies_DesignDeletedFromGlamourer_IsFalse()
    {
        // GetDesignCached returns null for a design that no longer exists, and a remembered id we
        // cannot verify must never be adopted on trust.
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        Assert.False(DesignBindingService.BootIdStillApplies(null, state));
    }

    [Fact]
    public void BootIdStillApplies_CharacterChangedWhileUnloaded_IsFalse()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var state  = State(("Head", 1), ("Body", 2), ("Hands", 7)); // hands differ
        Assert.False(DesignBindingService.BootIdStillApplies(design, state));
    }

    // ── StripCarriers ─────────────────────────────────────────────────────────

    // Glamourer packs a bonus item as (type << 48) | row id; Glasses is type 2, so carrier row 1 is
    // 562949953421313 — the value observed in the field.
    private const ulong CarrierGlassesRow    = 1;
    private const ulong CarrierGlassesPacked = (2UL << 48) | CarrierGlassesRow;
    // What an empty Glasses slot actually reads as in a live state — the value from the field log.
    private const ulong NoGlasses = 844424946909184;

    [Fact]
    public void StripCarriers_DesignThatCapturedOurGlasses_MatchesAStateWithoutThem()
    {
        // The design was saved while the shell was hosted, so it demands our carrier facewear. By the
        // time the boot restore verifies, Dispose has taken it off — and that slot is our doing, not
        // the player's, so it must not decide the match.
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithBonus(design, "Glasses", CarrierGlassesPacked);
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithBonus(state, "Glasses", NoGlasses);                      // carrier already removed

        Assert.False(Matches(design, state));                        // the bug

        var stripped = DesignBindingService.StripCarriers(design, CarrierGlassesRow, null);
        Assert.True(Matches(stripped, state));
    }

    [Fact]
    public void StripCarriers_LeavesBonusItemsThatArentOurs()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithBonus(design, "Glasses", (2UL << 48) | 47);              // a real pair the player chose
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithBonus(state, "Glasses", NoGlasses);

        var stripped = DesignBindingService.StripCarriers(design, CarrierGlassesRow, null);
        Assert.False(Matches(stripped, state));                      // still a criterion
    }

    [Fact]
    public void StripCarriers_RemovesTheCarrierRingFromTheDesign()
    {
        const ulong ring = 9295;
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true),
                            ("RFinger", ring, true));
        var state  = State(("Head", 1), ("Body", 2), ("Hands", 3));  // ring already removed

        Assert.False(Matches(design, state));

        var stripped = DesignBindingService.StripCarriers(design, null, [ring]);
        Assert.True(Matches(stripped, state));
    }

    [Fact]
    public void CarrierStillWornAtBoot_StateIsZeroedNotStripped()
    {
        // The carrier ring is still equipped at load (a crash, or a Dispose removal that hasn't landed)
        // and the design saved an EMPTY right ring, which Glamourer stores as ItemId 0 + Apply. The state
        // must be neutralized by ZEROING our ring, the way the boot restore does it: removing the slot
        // instead would leave the design carrying RFinger with nothing on the state side to compare, and
        // the match would fail on a slot that is entirely Proteus's doing.
        const ulong ring = 9295;
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true), ("RFinger", 0, true));
        var worn   = State(("Head", 1), ("Body", 2), ("Hands", 3), ("RFinger", ring));

        Assert.False(Matches(design, worn));                                   // our ring, unneutralized

        var zeroed = DesignBindingService.NeutralizeProteusOwnedState(worn, null, [ring]);
        Assert.True(Matches(design, zeroed));                                  // what the boot path does

        // Stripping the state instead is the regression this guards: the slot vanishes and the design's
        // RFinger has nothing to compare against.
        var strippedState = DesignBindingService.StripCarriers(worn, null, [ring]);
        Assert.False(Matches(design, strippedState));
    }

    [Fact]
    public void StripCarriers_NoCarrierPresent_ReturnsTheSameInstance()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        Assert.Same(design, DesignBindingService.StripCarriers(design, CarrierGlassesRow, [9295UL]));
    }
}
