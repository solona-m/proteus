using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The IMC entry and effect path the composite writes for a carried effect, and the older-import detector.</summary>
public class ImcManipulationTests
{
    [Fact]
    public void Imc_manipulation_matches_penumbras_shape()   // [AC-item-vfx-host-2] [EDGE-item-vfx-host-17]
    {
        var entry = new ImcEntry(MaterialId: 1, DecalId: 0, AttributeMask: 0x3FF, SoundId: 2, VfxId: 1,
                                 MaterialAnimationId: 0);
        var json = JsonSerializer.Serialize(PenumbraManipulations.ImcManipulation('a', 53, 1, "RFinger", entry));
        var o = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("Imc", (string?)o["Type"]);
        var m = o["Manipulation"]!.AsObject();
        Assert.Equal("Accessory", (string?)m["ObjectType"]);
        Assert.Equal(53, (int)m["PrimaryId"]!);
        Assert.Equal(0, (int)m["SecondaryId"]!);
        Assert.Equal(1, (int)m["Variant"]!);
        Assert.Equal("RFinger", (string?)m["EquipSlot"]);
        Assert.Equal("Unknown", (string?)m["BodySlot"]);
        var e = m["Entry"]!.AsObject();
        Assert.Equal(0x3FF | (2 << 10), (int)e["AttributeAndSound"]!);
        Assert.Equal(0x3FF, (int)e["AttributeMask"]!);
        Assert.Equal(1, (int)e["VfxId"]!);
        Assert.Equal(1, (int)e["MaterialId"]!);

        // Read back the way an imported pack's manipulation is read: the same entry, field for field.
        var pack = new JsonObject { ["Manipulations"] = new JsonArray(o.DeepClone()) };
        var read = Assert.Single(PenumbraPackage.ReadImc(pack));
        Assert.Equal(new PenumbraPackage.PackImc("Accessory", 53, 1, "RFinger", 1, 0, 1, 0, 0x3FF, 2), read);

        Assert.Equal("chara/accessory/a0053/vfx/eff/va0001.avfx", PenumbraManipulations.VfxGamePath('a', 53, 1));
        Assert.Equal("chara/equipment/e5521/vfx/eff/ve0002.avfx", PenumbraManipulations.VfxGamePath('e', 5521, 2));
    }

    [Fact]
    public void Effect_only_model_ignores_race()   // [AC-item-vfx-host-6] [EDGE-item-vfx-host-3]
    {
        // A race-locked piece (a Miqo'te-only model) refused to an Au Ra woman, as it always was...
        var garment = new ContentPiece { Models = new() { ["0801"] = "items/c0801e6001_top.mdl" } };
        Assert.Null(ContentPieceResolver.ResolveForWearerOrEffect(garment, "1401", "1401"));

        // ...but an effect-only placeholder resolves for anyone: it is one invisible point.
        var effect = new ContentPiece
        {
            EffectOnly = true,
            Models = new() { ["0801"] = "items/c0801a0002_wrs.mdl" },
        };
        Assert.Null(ContentPieceResolver.ResolveForWearer(effect, "1401", "1401"));
        var resolved = ContentPieceResolver.ResolveForWearerOrEffect(effect, "1401", "1401");
        Assert.Equal("items/c0801a0002_wrs.mdl", resolved?.Path);
        Assert.Null(resolved?.Code);
    }

    [Fact]
    public void Stale_effect_import_is_detected()   // [AC-item-vfx-host-8] [EDGE-item-vfx-host-12]
    {
        ProteusMetadata Meta(ContentEffect? effect) => new()
        {
            ContentGroups =
            [
                new ContentOptionGroup
                {
                    PenumbraGroupName = "Items",
                    Options = [new ContentOption
                    {
                        Name = "Astral Bracelet",
                        Pieces = [new ContentPiece
                        {
                            Slot = "Bracelets", Effect = effect,
                            Models = new() { ["0101"] = "items/chara/accessory/a0002/model/c0101a0002_wrs.mdl" },
                        }],
                    }],
                },
            ],
        };
        PenumbraPackage.PackImc Imc(int vfx) => new("Accessory", 2, 15, "Wrists", 15, 0, vfx, 0, 0x3FF, 0);

        Assert.Equal("Bracelets", StaleEffectImport.Detect(Meta(null), [Imc(1)]));
        Assert.Null(StaleEffectImport.Detect(Meta(new ContentEffect { VfxId = 1 }), [Imc(1)]));
        Assert.Null(StaleEffectImport.Detect(Meta(null), [Imc(0)]));
        // Another item's effect is not this piece's.
        Assert.Null(StaleEffectImport.Detect(Meta(null), [Imc(1) with { PrimaryId = 3 }]));
    }
}
