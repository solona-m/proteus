using System;
using System.IO;
using System.Linq;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Skin a refit KEEPS of a garment's own is drawn the way the new body draws its skin: in its texture layout and with
/// its skin material — or, on a Rue character, a gen2 or gen3 garment's kept skin shows the wrong skin and none of the
/// tattoos Proteus composites into the body's own. Players reported exactly that. Against real mods, so skipped where
/// they are not installed.
/// </summary>
public class MatchSkinToBodyTests
{
    private const string Slayer = LocalData.Mods + ":Beautiful Slayer - by Solona/default/chara/equipment/e6090/model/c0201e6090_top.mdl";
    private const string Araneidae = LocalData.Mods + ":Araneidae - by Solona/southern seas shirt/chara/equipment/e6109/model/c0201e6109_top.mdl";
    private const string Gen2Body = LocalData.Mods + ":tight&firmgen2body-nsfw-installer";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";

    private static string FirstChest(string modNeed)
    {
        var catalog = BodySizeCatalog.Read(LocalData.Path(modNeed));
        return catalog.PathOf(catalog.For("_top", "0201")[0]);
    }

    private static byte[] Match(string garmentNeed, string sourceModNeed)
        => RefitCore.MatchSkinToBody(File.ReadAllBytes(LocalData.Path(garmentNeed)), FirstChest(sourceModNeed),
                                     FirstChest(Rue), new UVRemapService(Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), "."),
                                     male: false, Substitute.For<Dalamud.Plugin.Services.IPluginLog>());

    [LocalDataFact(Slayer, Gen2Body, Rue)]
    public void A_gen2_garments_own_skin_moves_onto_the_bibo_sheet_and_takes_the_bodys_material()
    {
        var before = File.ReadAllBytes(LocalData.Path(Slayer));
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(before, out var pos, out var uv, out _,
                                                         m => m.StartsWith("/mt_c0201b0001_a.", StringComparison.Ordinal)));

        var after = Match(Slayer, Gen2Body);

        var materials = SecondSkinWriter.Parse(after).MatNames;
        Assert.Contains("/mt_c0201b0001_bibo.mtrl", materials);
        Assert.DoesNotContain(materials, m => m.StartsWith("/mt_c0201b0001_a.", StringComparison.Ordinal));

        Assert.True(SecondSkinWriter.TryReadLod0Geometry(after, out var pos2, out var uv2, out _,
                                                         m => m == "/mt_c0201b0001_bibo.mtrl"));
        Assert.Equal(pos.Length, pos2.Length);

        // gen2 is one side of bibo's sheet: the +X side lands in the right half, the -X side in its mirror.
        int checkedSides = 0;
        for (int i = 0; i < pos.Length / 3; i++)
        {
            float x = pos[i * 3];
            if (MathF.Abs(x) < 0.01f) continue;   // the midline's side is a judgement call
            float right = 0.5f + uv[i * 2] * 0.5f;
            float want = x > 0 ? right : UVRemapService.MirrorU(right);
            Assert.InRange(uv2[i * 2], want - 2e-3f, want + 2e-3f);
            Assert.InRange(uv2[i * 2 + 1], uv[i * 2 + 1] - 2e-3f, uv[i * 2 + 1] + 2e-3f);
            checkedSides++;
        }
        Assert.True(checkedSides > 100, $"only {checkedSides} skin vertices off the midline were checked");
    }

    [LocalDataFact(Araneidae, Bibo, Rue)]
    public void A_bibo_garment_naming_another_skin_material_only_takes_the_bodys_name()
    {
        // Araneidae is a Bibo+ garment that names "_b" — which gen3 uses too. Its coordinates are already bibo's; only
        // the name was wrong for a Rue character, and converting them as if they were gen3 would have broken them.
        var before = File.ReadAllBytes(LocalData.Path(Araneidae));
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(before, out _, out var uv, out _));

        var after = Match(Araneidae, Bibo);

        Assert.Contains("/mt_c0201b0001_bibo.mtrl", SecondSkinWriter.Parse(after).MatNames);
        Assert.DoesNotContain(SecondSkinWriter.Parse(after).MatNames, m => m.StartsWith("/mt_c0201b0001_b.", StringComparison.Ordinal));
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(after, out _, out var uv2, out _));
        Assert.Equal(uv, uv2);
    }

    [LocalDataFact(Rue)]
    public void A_garment_already_drawn_like_the_body_is_returned_untouched()
    {
        string rue = FirstChest(Rue);
        var body = File.ReadAllBytes(rue);
        var same = RefitCore.MatchSkinToBody(body, rue, rue, null, false, Substitute.For<Dalamud.Plugin.Services.IPluginLog>());
        Assert.Same(body, same);
    }
}
