using System.Collections.Generic;
using System.Linq;
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

        var shipped = new HashSet<string> { "chara/accessory/a0002/vfx/eff/va0001.avfx" };
        Assert.Equal("Bracelets", StaleEffectImport.Detect(Meta(null), [Imc(1)], shipped));
        Assert.Null(StaleEffectImport.Detect(Meta(new ContentEffect { VfxId = 1 }), [Imc(1)], shipped));
        Assert.Null(StaleEffectImport.Detect(Meta(null), [Imc(0)], shipped));
        // Another item's effect is not this piece's.
        Assert.Null(StaleEffectImport.Detect(Meta(null), [Imc(1) with { PrimaryId = 3 }], shipped));
        // A game effect the pack only switches on (no .avfx of its own) is left on the item by the import too.
        Assert.Null(StaleEffectImport.Detect(Meta(null), [Imc(1)], new HashSet<string>()));
    }

    [Theory]   // [AC-item-vfx-host-8] [EDGE-item-vfx-host-12] [EDGE-item-vfx-host-10]
    [InlineData(false)]
    [InlineData(true)]
    public void A_fresh_import_is_never_told_to_re_import(bool avfxRedirected)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "proteus-stale-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            using var pack = SyntheticPack.EffectBearing(avfxRedirected: avfxRedirected);
            var preview = ContentImportService.Inspect(pack.Path);
            ContentImportService.WriteMod(root, "Veil", "Someone", preview);

            var sidecar = ContentImportService.BuildSidecar(preview, "Veil", "Someone");
            var (imc, redirected) = PenumbraPackage.ReadManifestImc(root);
            Assert.Null(StaleEffectImport.Detect(sidecar, imc, redirected));

            // The same pack's own manifests under an older import's sidecar (no effect recorded): told, when it ships one.
            using var original = SyntheticPack.EffectBearing(avfxRedirected: avfxRedirected);
            var raw = root + "-raw";
            PenumbraPackage.ExtractTo(original.Path, raw);
            foreach (var p in sidecar.ContentGroups!.SelectMany(g => g.Options).SelectMany(o => o.Pieces)) p.Effect = null;
            var (rawImc, rawRedirected) = PenumbraPackage.ReadManifestImc(raw);
            var told = StaleEffectImport.Detect(sidecar, rawImc, rawRedirected);
            if (avfxRedirected) Assert.NotNull(told); else Assert.Null(told);
            System.IO.Directory.Delete(raw, true);
        }
        finally { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true); }
    }

    [Theory]   // [AC-item-vfx-host-8] [EDGE-item-vfx-host-12]
    [InlineData(false)]
    [InlineData(true)]
    public void An_installed_mods_effects_are_read_from_its_manifests_alone(bool legacy)
    {
        // What the older-import check reads: an installed copy of the pack, in either layout, without walking its files.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "proteus-manifest-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            using var pack = SyntheticPack.EffectBearing(legacy: legacy);
            PenumbraPackage.ExtractTo(pack.Path, dir);
            var (all, redirected) = PenumbraPackage.ReadManifestImc(dir);
            Assert.Equal(new PenumbraPackage.PackImc("Accessory", 2, 15, "Wrists", 15, 0, 1, 0, 0x3FF, 0), Assert.Single(all));
            Assert.Contains("chara/accessory/a0002/vfx/eff/va0001.avfx", redirected);
            Assert.Contains("vfx/FroVeil/FroVeilDustT.atex", redirected);
        }
        finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
    }

    /// <summary>
    /// [AC-imported-imc-toggles-4] The game's entry for an item named by set and slot is the one its model path names,
    /// for equipment and accessories, and a variant outside the file reads the default set.
    /// </summary>
    [Fact]
    public void FromGame_by_set_and_slot_matches_the_model_path_overload()
    {
        // Two variants, five slots each, after the default set; every entry's mask is unique to where it sits.
        static byte[] Imc()
        {
            var b = new byte[4 + 3 * 5 * 6];
            System.BitConverter.GetBytes((ushort)2).CopyTo(b, 0);
            System.BitConverter.GetBytes((ushort)31).CopyTo(b, 2);
            for (int set = 0; set < 3; set++)
                for (int part = 0; part < 5; part++)
                    System.BitConverter.GetBytes((ushort)(set * 10 + part + 1)).CopyTo(b, 4 + (set * 5 + part) * 6 + 2);
            return b;
        }
        var files = new System.Collections.Generic.Dictionary<string, byte[]>
        {
            ["chara/equipment/e0666/e0666.imc"] = Imc(),
            ["chara/accessory/a0053/a0053.imc"] = Imc(),
        };
        byte[]? Read(string p) => files.TryGetValue(p, out var b) ? b : null;

        foreach (var (model, tag, label) in new[]
                 {
                     ("chara/equipment/e0666/model/c0201e0666_sho.mdl", "e0666", "Feet"),
                     ("chara/equipment/e0666/model/c0201e0666_top.mdl", "e0666", "Body"),
                     ("chara/accessory/a0053/model/c0201a0053_rir.mdl", "a0053", "Right ring"),
                 })
            foreach (var variant in new[] { 0, 1, 2, 7 })
            {
                var byPath = ImcEntrySource.FromGame(Read, model, variant);
                Assert.NotNull(byPath);
                Assert.Equal(byPath, ImcEntrySource.FromGame(Read, tag, label, variant));
            }

        Assert.Equal((ushort)15, ImcEntrySource.FromGame(Read, "e0666", "Feet", 1)!.Value.AttributeMask);
        Assert.Equal((ushort)5, ImcEntrySource.FromGame(Read, "e0666", "Feet", 7)!.Value.AttributeMask);
        Assert.Null(ImcEntrySource.FromGame(Read, "h0001", "Hair", 1));
    }
}
