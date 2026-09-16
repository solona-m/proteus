using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using Proteus.Interop;
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
    // apply would be treated as unbound, and the overrides would be dropped.
    [Fact]
    public void GlassesSlotWeOwn_IsNotCompared()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithBonus(design, "Glasses", bonusId: NoGlasses);  // design saved "no glasses"
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithBonus(state, "Glasses", bonusId: CarrierGlassesPacked); // ...but Proteus injected its host

        Assert.False(Matches(design, state));              // raw: our host looks like a real choice
        Assert.True(Matches(OurGlasses(design), state));
    }

    // Wearing our facewear must not discount a design's OWN glasses on the strict pass — the pass that
    // decides whenever anything matches at all. This is the guard that matters for a player who wears the
    // invisible pair as their own glamour, whom the reconcile's adoption makes indistinguishable from us.
    [Fact]
    public void PlayerChosenGlassesStillComparedOnTheStrictPass()
    {
        var theirs = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithBonus(theirs, "Glasses", bonusId: (2UL << 48) | 47);   // a real pair they chose
        var ours = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithBonus(ours, "Glasses", bonusId: CarrierGlassesPacked); // the carrier, captured on save
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithBonus(state, "Glasses", bonusId: CarrierGlassesPacked);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var top = DesignBindingService.BestMatches([(a, theirs), (b, ours)], state,
            new DesignBindingService.Carriers(CarrierGlassesRow, null, GlassesSlotOwned: true));

        Assert.Equal([b], top);   // the design that really names the worn pair, not a tie
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

    [Fact]
    public void BindingStore_RoundTrips_CharacterSnapshot()
    {
        var id = Guid.NewGuid();
        var store = new DesignBindingStore();
        store.Bindings[id] = new DesignBinding
        {
            DesignId = id,
            CharacterMods =
            [
                new PenumbraModSetting
                {
                    ModDirectory = "Dress", ModName = "A Dress", Enabled = true, Priority = 12,
                    Options = new() { ["Colour"] = ["Red"] },
                },
            ],
            CharacterGamePaths = ["chara/equipment/e6255/model/c0201e6255_top.mdl"],
        };

        var back = JsonSerializer.Deserialize<DesignBindingStore>(JsonSerializer.Serialize(store, JsonOpts), JsonOpts)!;

        Assert.Equal(2, back.Version);
        var b = back.Bindings[id];
        Assert.True(b.HasCharacterSnapshot);
        Assert.Equal("A Dress", b.CharacterMods[0].ModName);
        Assert.Equal(12, b.CharacterMods[0].Priority);
        Assert.Equal(["Red"], b.CharacterMods[0].Options["Colour"]);
        Assert.Equal(["chara/equipment/e6255/model/c0201e6255_top.mdl"], b.CharacterGamePaths);
    }

    [Fact]
    public void BindingStore_Version1_LoadsWithoutCharacterSnapshot()
    {
        var id = Guid.NewGuid();
        var json = $$"""
            { "Version": 1, "Bindings": { "{{id}}": { "DesignId": "{{id}}", "Mods": [ { "ModDirectory": "Overlay", "Enabled": true } ] } } }
            """;

        var back = JsonSerializer.Deserialize<DesignBindingStore>(json, JsonOpts)!;

        var b = back.Bindings[id];
        Assert.False(b.HasCharacterSnapshot);
        Assert.Empty(b.CharacterGamePaths);
        Assert.Equal("Overlay", b.Mods[0].ModDirectory);
    }

    // ── Character snapshot: capture selection ────────────────────────────────────

    private const string ModsRoot = @"C:\Penumbra";

    private static PenumbraBridge.ModSettingsSnapshot On(int priority, Dictionary<string, List<string>>? options = null, bool temporary = false)
        => new(true, priority, options ?? new(), false, temporary);

    private static PenumbraBridge.ModSettingsSnapshot Off(int priority = 0)
        => new(false, priority, new(), false, false);

    private static ModActivePaths.Contribution Writes(params string[] paths)
    {
        var c = ModActivePaths.Contribution.Empty();
        foreach (var p in paths) c.GamePaths.Add(p);
        return c;
    }

    private static Func<string, IReadOnlyDictionary<string, List<string>>?, ModActivePaths.Contribution?> Contributions(
        Dictionary<string, ModActivePaths.Contribution?> byMod)
        => (dir, _) => byMod.TryGetValue(dir, out var c) ? c : ModActivePaths.Contribution.Empty();

    private const string Top  = "chara/equipment/e6255/model/c0201e6255_top.mdl";
    private const string Body = "chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl";

    [Fact]
    public void SelectCharacterMods_TakesDrawnOwners_ConflictLosers_AndProteusMods()
    {
        var resources = new Dictionary<string, HashSet<string>>
        {
            [@"C:\Penumbra\Dress\files\top.mdl"]  = [Top],
            [@"C:\Penumbra\Proteus\out\body.mtrl"] = ["chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl"],
            [@"C:\Game\sqpack\body.mdl"]           = [Body],
        };
        var permanent = new Dictionary<string, PenumbraBridge.ModSettingsSnapshot>
        {
            ["Dress"]       = On(10, new() { ["Colour"] = ["Red"] }),
            ["OtherDress"]  = On(5),      // loses the top to Dress, but reaches it
            ["Hat"]         = On(50),     // reaches nothing drawn
            ["Overlay"]     = Off(3),     // a Proteus mod, off — still recorded
        };
        var installed = new Dictionary<string, string>
        {
            ["Dress"] = "A Dress", ["OtherDress"] = "Other", ["Hat"] = "Hat", ["Overlay"] = "Overlay", ["Proteus"] = "Proteus",
        };
        var contributions = Contributions(new()
        {
            ["OtherDress"] = Writes(Top),
            ["Hat"]        = Writes("chara/equipment/e0100/model/c0201e0100_met.mdl"),
        });

        var (mods, paths) = DesignBindingService.SelectCharacterMods(
            ModsRoot, resources, permanent, installed, new HashSet<string> { "Overlay" }, contributions);

        Assert.Equal(["Dress", "OtherDress", "Overlay"], mods.ConvertAll(m => m.ModDirectory));
        var dress = mods[0];
        Assert.Equal("A Dress", dress.ModName);
        Assert.True(dress.Enabled);
        Assert.Equal(10, dress.Priority);
        Assert.Equal(["Red"], dress.Options["Colour"]);
        Assert.False(mods[2].Enabled);
        Assert.Contains(Top, paths);
        Assert.Contains(Body, paths);
    }

    [Theory]
    [InlineData(@"C:\Penumbra\Dress\files\top.mdl", "Dress")]
    [InlineData("C:/Penumbra/Dress/files/top.mdl", "Dress")]
    [InlineData(@"c:\penumbra\dress\top.mdl", "dress")]
    [InlineData(@"C:\PenumbraOther\Dress\top.mdl", null)]
    [InlineData(@"C:\Penumbra\loose.mdl", null)]
    public void OwningMod_ReadsTheFirstFolderUnderTheModsRoot(string resolved, string? expected)
        => Assert.Equal(expected, DesignBindingService.OwningMod(ModsRoot, resolved));

    // ── Character snapshot: priority offset ──────────────────────────────────────

    [Fact]
    public void PriorityOffset_IsZero_WhenNothingContests()
    {
        var offset = DesignBindingService.ComputePriorityOffset(
            [(10, Writes(Top))],
            [(99, Writes(Body))]);

        Assert.Equal(0, offset);
    }

    [Fact]
    public void PriorityOffset_LiftsJustAboveTheHighestRival()
    {
        var offset = DesignBindingService.ComputePriorityOffset(
            [(10, Writes(Top)), (20, Writes(Body))],
            [(30, Writes(Top)), (15, Writes(Body))]);

        // Top needs 31 (from 10 → +21); Body needs 16 (already 20). One shared offset, keeping 10 < 20.
        Assert.Equal(21, offset);
    }

    [Fact]
    public void PriorityOffset_EqualPriorityStillNeedsOne()
    {
        Assert.Equal(1, DesignBindingService.ComputePriorityOffset([(10, Writes(Top))], [(10, Writes(Top))]));
    }

    [Fact]
    public void PriorityOffset_IgnoresUnreadableMods()
    {
        Assert.Equal(0, DesignBindingService.ComputePriorityOffset([(10, Writes(Top))], [(500, null)]));
    }

    // ── Character snapshot: restore plan ─────────────────────────────────────────

    private static PenumbraModSetting Bound(string dir, bool enabled, int priority, Dictionary<string, List<string>>? options = null)
        => new() { ModDirectory = dir, Enabled = enabled, Priority = priority, Options = options ?? new() };

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void PlanRestore_SwitchesOffUnboundModsOnTheCharacter_LeavesTheRestAlone()
    {
        var permanent = new Dictionary<string, PenumbraBridge.ModSettingsSnapshot>
        {
            ["Dress"]    = On(10),
            ["Intruder"] = On(50),   // installed since capture, same top
            ["UiMod"]    = On(0),    // reaches nothing the design drew
        };

        var plan = DesignBindingService.PlanRestore(
            [Bound("Dress", true, 10)], [Top],
            Set("Dress", "Intruder", "UiMod"),
            permanent, permanent, Set(), Set(),
            Contributions(new() { ["Dress"] = Writes(Top), ["Intruder"] = Writes(Top), ["UiMod"] = Writes("ui/uld/foo.tex") }));

        Assert.Equal(["Intruder"], plan.Disable);
        Assert.Equal(0, plan.PriorityOffset);
        Assert.Equal(0, plan.WriteCount - plan.Disable.Count);  // Dress already as recorded: no writes
    }

    [Fact]
    public void PlanRestore_RaisesBoundModsAboveARivalThatStaysOn()
    {
        // The rival reaches the dress's paths but not the design's (it only fights over an undrawn variant).
        var permanent = new Dictionary<string, PenumbraBridge.ModSettingsSnapshot>
        {
            ["Dress"] = On(10),
            ["Rival"] = On(40),
        };
        const string variant = "chara/equipment/e6255/model/c0101e6255_top.mdl";

        var plan = DesignBindingService.PlanRestore(
            [Bound("Dress", true, 10), Bound("Body", true, 5)], [Top],
            Set("Dress", "Body", "Rival"),
            permanent, permanent, Set(), Set(),
            Contributions(new() { ["Dress"] = Writes(Top, variant), ["Rival"] = Writes(variant), ["Body"] = Writes(Body) }));

        Assert.Empty(plan.Disable);
        Assert.Equal(31, plan.PriorityOffset);
        Assert.Contains(("Dress", 41), plan.SetPriority);
        Assert.Contains(("Body", 36), plan.SetPriority);   // same offset, order kept
        Assert.Contains(("Body", true), plan.SetEnabled);  // wasn't configured → turned on
    }

    [Fact]
    public void PlanRestore_ClearsTemporarySettings_AndWritesOnlyWhatDiffers()
    {
        var permanent = new Dictionary<string, PenumbraBridge.ModSettingsSnapshot>
        {
            ["Dress"] = On(10, new() { ["Colour"] = ["Blue"], ["Size"] = ["M"] }),
            ["Temp"]  = Off(),
        };
        var effective = new Dictionary<string, PenumbraBridge.ModSettingsSnapshot>
        {
            ["Dress"] = On(10, new() { ["Colour"] = ["Blue"] }, temporary: true),
            ["Temp"]  = On(90, temporary: true),   // Glamourer switched it on; it reaches the top
        };

        var plan = DesignBindingService.PlanRestore(
            [Bound("Dress", true, 10, new() { ["Colour"] = ["Red"], ["Size"] = ["M"] })], [Top],
            Set("Dress", "Temp"),
            permanent, effective, Set(), Set(),
            Contributions(new() { ["Dress"] = Writes(Top), ["Temp"] = Writes(Top) }));

        Assert.Equal(Set("Dress", "Temp"), plan.ClearTemporary.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(plan.Disable);                    // only on through the temporary setting
        Assert.Equal([("Dress", "Colour", new List<string> { "Red" })],
            plan.SetOptions.ConvertAll(o => (o.ModDirectory, o.Group, o.Options)), new OptionWriteComparer());
        Assert.Empty(plan.SetPriority);
        Assert.Empty(plan.SetEnabled);
    }

    [Fact]
    public void PlanRestore_ReportsMissingMods_AndSweepsUnboundProteusMods()
    {
        var permanent = new Dictionary<string, PenumbraBridge.ModSettingsSnapshot>
        {
            ["OldOverlay"]  = On(1),
            ["ContentPack"] = On(1),
        };

        var plan = DesignBindingService.PlanRestore(
            [Bound("Gone", true, 3)], [Top],
            Set("OldOverlay", "ContentPack"),
            permanent, permanent,
            proteusDirs: Set("OldOverlay", "ContentPack"),
            heldProteus: Set("ContentPack"),
            Contributions(new()));

        Assert.Equal(["Gone"], plan.Missing);
        Assert.Equal(["OldOverlay"], plan.Disable);
        Assert.Empty(plan.SetEnabled);
    }

    // ── Unequip what the design leaves unset ─────────────────────────────────────

    private static readonly DesignBindingService.Carriers NoCarriers = new(null, null);

    private static JObject GearState(params (string slot, ulong item)[] slots)
    {
        var eq = new JObject();
        foreach (var (slot, item) in slots)
            eq[slot] = new JObject { ["ItemId"] = item, ["Apply"] = true };
        return new JObject { ["Equipment"] = eq };
    }

    [Fact]
    public void UnsetSlots_EmptiesWhatTheDesignDoesNotApply()
    {
        var design = Design(("Body", 100, true), ("Legs", 101, false));
        var state  = GearState(("Body", 100), ("Legs", 555), ("Head", 777), ("Feet", uint.MaxValue - 128 - 4), ("Hands", 0));

        var slots = DesignBindingService.UnsetSlots(design, state, NoCarriers);

        Assert.Equal(["Head", "Legs"], slots);   // Feet already Nothing, Hands empty, Body applied
    }

    [Fact]
    public void UnsetSlots_LeavesProteusCarriersAlone()
    {
        var design  = Design(("Body", 100, true));
        var state   = GearState(("RFinger", 9295), ("LFinger", 1234), ("Wrists", 42));
        var carrier = new DesignBindingService.Carriers(null, [9295UL], OwnedSlots: ["Wrists"]);

        Assert.Equal(["LFinger"], DesignBindingService.UnsetSlots(design, state, carrier));
    }

    [Fact]
    public void UnsetSlots_Glasses()
    {
        var design = Design(("Body", 100, true));
        var state  = GearState(("Body", 100));
        state["Bonus"] = new JObject { ["Glasses"] = new JObject { ["BonusId"] = (1UL << 48) | 7 } };

        Assert.Equal(["Glasses"], DesignBindingService.UnsetSlots(design, state, NoCarriers));
        Assert.Empty(DesignBindingService.UnsetSlots(design, state, new DesignBindingService.Carriers(7, null)));

        state["Bonus"]!["Glasses"]!["BonusId"] = 1UL << 48;   // bare glasses: nothing worn
        Assert.Empty(DesignBindingService.UnsetSlots(design, state, NoCarriers));
    }

    // ── Leftover fields: the subset design over the active one ───────────────────

    private static JObject Outfit(bool withHat)
    {
        var d = Design(("Body", 100, true), ("Legs", 101, true), ("Feet", 102, true), ("Head", 777, withHat));
        return d;
    }

    private static JObject Worn(ulong head, ulong body = 100)
        => GearState(("Body", body), ("Legs", 101), ("Feet", 102), ("Head", head));

    [Fact]
    public void AppliedInsteadOfActive_TakesTheSubsetDesign_WhenTheHatIsALeftover()
    {
        Guid withHat = Guid.NewGuid(), noHat = Guid.NewGuid();
        var candidates = new List<(Guid, JObject)> { (withHat, Outfit(true)), (noHat, Outfit(false)) };

        // Wearing the hat design; the no-hat design applied over it changes nothing, hat stays on.
        var instead = DesignBindingService.AppliedInsteadOfActive(withHat, candidates, Worn(777), Worn(777), NoCarriers);

        Assert.Equal([noHat], instead);
    }

    [Fact]
    public void AppliedInsteadOfActive_KeepsTheActiveDesign_WhenItsExtraFieldChangedBack()
    {
        Guid withHat = Guid.NewGuid(), noHat = Guid.NewGuid();
        var candidates = new List<(Guid, JObject)> { (withHat, Outfit(true)), (noHat, Outfit(false)) };

        // A different hat was put on by hand; re-applying the hat design put 777 back.
        var instead = DesignBindingService.AppliedInsteadOfActive(withHat, candidates, Worn(555), Worn(777), NoCarriers);

        Assert.Empty(instead);
    }

    [Fact]
    public void AppliedInsteadOfActive_IgnoresDesignsThatAreNotASubset()
    {
        Guid active = Guid.NewGuid(), other = Guid.NewGuid();
        var otherDesign = Design(("Body", 100, true), ("Legs", 101, true), ("Hands", 5, true));
        var candidates = new List<(Guid, JObject)> { (active, Outfit(true)), (other, otherDesign) };
        var state = Worn(777);
        ((JObject)state["Equipment"]!)["Hands"] = new JObject { ["ItemId"] = 5UL, ["Apply"] = true };

        Assert.Empty(DesignBindingService.AppliedInsteadOfActive(active, candidates, state, state, NoCarriers));
    }

    private sealed class OptionWriteComparer : IEqualityComparer<(string, string, List<string>)>
    {
        public bool Equals((string, string, List<string>) a, (string, string, List<string>) b)
            => a.Item1 == b.Item1 && a.Item2 == b.Item2 && a.Item3.SequenceEqual(b.Item3);

        public int GetHashCode((string, string, List<string>) o) => HashCode.Combine(o.Item1, o.Item2);
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
    // 562949953421313 — the value observed in the field. Tests use the PACKED form throughout: comparing
    // raw row ids is exactly the shortcut that let a masking bug live in the state path for a month.
    private const ulong CarrierGlassesRow    = 1;
    private const ulong CarrierGlassesPacked = (2UL << 48) | CarrierGlassesRow;
    // What an empty Glasses slot actually reads as in a live state — the value from the field log.
    private const ulong NoGlasses = 844424946909184;
    // Proteus's carrier ring, from the field log: "invisible carrier: equipped item #9295 in rir".
    private const ulong CarrierRing = 9295;
    // A per-slot "nothing" sentinel, the way Glamourer really writes an empty ring. Never 0 — no design in
    // a 26-design corpus stores 0, which is what made the old state-zeroing pass unable to match anything.
    private const ulong NoRing = 4294967155;

    // We are wearing our facewear carrier and nothing else of ours.
    private static JObject OurGlasses(JObject design)
        => DesignBindingService.StripCarriers(design,
            new DesignBindingService.Carriers(CarrierGlassesRow, null, GlassesSlotOwned: true));

    // We are borrowing the named accessory slots for carriers.
    private static JObject OurAccessories(JObject design, params string[] slots)
        => DesignBindingService.StripCarriers(design,
            new DesignBindingService.Carriers(null, [CarrierRing], slots));

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

        // By ID alone — the boot path, which cannot know whether the glasses slot is ours.
        var stripped = DesignBindingService.StripCarriers(design,
            new DesignBindingService.Carriers(CarrierGlassesRow, null));
        Assert.True(Matches(stripped, state));
    }

    [Fact]
    public void StripCarriers_LeavesBonusItemsThatArentOurs()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        WithBonus(design, "Glasses", (2UL << 48) | 47);              // a real pair the player chose
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        WithBonus(state, "Glasses", NoGlasses);

        var stripped = DesignBindingService.StripCarriers(design,
            new DesignBindingService.Carriers(CarrierGlassesRow, null));
        Assert.False(Matches(stripped, state));                      // still a criterion
    }

    [Fact]
    public void StripCarriers_RemovesTheCarrierRingFromTheDesign()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true),
                            ("RFinger", CarrierRing, true));
        var state  = State(("Head", 1), ("Body", 2), ("Hands", 3));  // ring already removed

        Assert.False(Matches(design, state));

        var stripped = DesignBindingService.StripCarriers(design,
            new DesignBindingService.Carriers(null, [CarrierRing]));
        Assert.True(Matches(stripped, state));
    }

    // The reported bug, end to end: a design saved while Proteus was hosting on the right-ring slot
    // captured carrier item 9295, and every later apply compared that against a live state where the very
    // same carrier sits — matching by item id is the only thing that reconciles the two.
    [Fact]
    public void DesignThatCapturedTheCarrierRing_MatchesWhileWeStillWearIt()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true),
                            ("RFinger", CarrierRing, true));
        var worn   = State(("Head", 1), ("Body", 2), ("Hands", 3), ("RFinger", CarrierRing));

        Assert.True(Matches(OurAccessories(design, "RFinger"), worn));
    }

    // The reverse: the design named the player's OWN ring, saved before Proteus borrowed that slot. Their
    // choice is not in the live state to compare against any more, so the slot has to stop being a
    // criterion — matching by item id alone cannot see this one, which is why ownership is passed by SLOT.
    [Fact]
    public void DesignWithTheirOwnRing_MatchesWhileWeBorrowThatSlot()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true),
                            ("RFinger", 6139, true));                // a real ring they chose
        var worn   = State(("Head", 1), ("Body", 2), ("Hands", 3), ("RFinger", CarrierRing));

        Assert.False(Matches(design, worn));
        Assert.True(Matches(OurAccessories(design, "RFinger"), worn));
    }

    // Only the slots we actually hold are retired: a ring in the other hand still decides the match.
    [Fact]
    public void StripCarriers_LeavesAccessorySlotsWeDoNotHold()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true),
                            ("RFinger", CarrierRing, true), ("LFinger", 6139, true));
        var worn   = State(("Head", 1), ("Body", 2), ("Hands", 3),
                           ("RFinger", CarrierRing), ("LFinger", 7777));

        Assert.False(Matches(OurAccessories(design, "RFinger"), worn));
    }

    [Fact]
    public void CarrierStillWornAtBoot_TheSlotIsRetiredEntirely()
    {
        // The carrier ring is still equipped at load (a crash, or a Dispose removal that hasn't landed)
        // and the design saved an EMPTY right ring — which Glamourer writes as a per-slot sentinel, never
        // as 0. Doctoring the state's ItemId to 0 (what the old boot path did) therefore could not make
        // this match; retiring the slot from the design is what does.
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true),
                            ("RFinger", NoRing, true));
        var worn   = State(("Head", 1), ("Body", 2), ("Hands", 3), ("RFinger", CarrierRing));

        Assert.False(Matches(design, worn));
        Assert.True(Matches(OurAccessories(design, "RFinger"), worn));
    }

    [Fact]
    public void StripCarriers_NoCarrierPresent_ReturnsTheSameInstance()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        Assert.Same(design, DesignBindingService.StripCarriers(design,
            new DesignBindingService.Carriers(CarrierGlassesRow, [CarrierRing], ["RFinger"], true)));
    }

    // Retiring a slot costs every candidate the same point, so it cannot itself break a tie — which is
    // precisely why the loose pass must not run while a strict match exists (see below).
    [Fact]
    public void RetiringASlotCostsBothCandidatesTheSameSpecificity()
    {
        var theirs = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true),
                            ("RFinger", 6139, true));
        var ours   = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true),
                            ("RFinger", CarrierRing, true));
        var worn   = State(("Head", 1), ("Body", 2), ("Hands", 3), ("RFinger", CarrierRing));

        Assert.Equal(Specificity(OurAccessories(theirs, "RFinger"), worn),
                     Specificity(OurAccessories(ours,   "RFinger"), worn));
    }

    // ── BestMatches: strict before loose ──────────────────────────────────────

    private static DesignBindingService.Carriers HoldingRFinger
        => new(null, [CarrierRing], ["RFinger"]);

    // The design that actually names the worn ring wins outright. Retiring RFinger from BOTH would have
    // tied them and handed the decision to recency — and the loser of that tie is not just a wrong look,
    // since Restore writes enable/priority/options into the Penumbra collection.
    [Fact]
    public void BestMatches_StrictMatchWinsBeforeSlotRetirementIsTried()
    {
        var ours   = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true),
                            ("RFinger", CarrierRing, true));
        var theirs = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true),
                            ("RFinger", 6139, true));
        var worn   = State(("Head", 1), ("Body", 2), ("Hands", 3), ("RFinger", CarrierRing));

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal([a], DesignBindingService.BestMatches([(a, ours), (b, theirs)], worn, HoldingRFinger));
    }

    // ...but with no strict match to be had, retirement is the rescue: their own ring is simply not in the
    // live state to compare against while we are borrowing the slot.
    [Fact]
    public void BestMatches_SlotRetirementRescuesWhenNothingMatchesStrictly()
    {
        var theirs = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true),
                            ("RFinger", 6139, true));
        var worn   = State(("Head", 1), ("Body", 2), ("Hands", 3), ("RFinger", CarrierRing));

        var a = Guid.NewGuid();
        Assert.Empty(DesignBindingService.BestMatches([(a, theirs)], worn, HoldingRFinger.ItemsOnly));
        Assert.Equal([a], DesignBindingService.BestMatches([(a, theirs)], worn, HoldingRFinger));
    }

    // A design that matches nothing either way still matches nothing.
    [Fact]
    public void BestMatches_NoCandidateMatches_IsEmpty()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 9, true));
        var worn   = State(("Head", 1), ("Body", 2), ("Hands", 3), ("RFinger", CarrierRing));

        Assert.Empty(DesignBindingService.BestMatches([(Guid.NewGuid(), design)], worn, HoldingRFinger));
    }

    // Ties survive both passes — the caller breaks them on recency, so BestMatches must hand back all of
    // them rather than pick one itself.
    [Fact]
    public void BestMatches_EquallySpecificCandidatesAreAllReturned()
    {
        var one = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var two = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var worn = State(("Head", 1), ("Body", 2), ("Hands", 3));

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal(2, DesignBindingService.BestMatches([(a, one), (b, two)], worn, HoldingRFinger).Count);
    }
}
