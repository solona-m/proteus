using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Reported: the vanilla Comfy Valentione Skirt (e6228 legs) refitted onto Neolithe YAS Gen A Small, with the skin
/// replaced — the body showed through the tops of the socks. Neolithe's legs carry the shins twice, <c>atr_dv_a</c> and
/// a thicker <c>atr_dv_b</c>, and an IMC option picks one; the swap dropped the variant tags and drew both, the thick
/// calf 10 mm proud of the thin one at the cuff. (The underwear measured clear of the skin: 0.2-1 mm outside it.)
/// </summary>
public class ValentineSkirtRefitDiagTests(ITestOutputHelper output)
{
    private const string GamePath = "chara/equipment/e6228/model/c0201e6228_dwn.mdl";
    private const string SourcePath = "chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string BodyMod = @"E:\Penumbradt\Neolithe YAS AIO";

    private static byte[]? Vanilla(string path)
    {
        var game = Environment.GetEnvironmentVariable("PROTEUS_GAME")
                ?? @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn";
        var sqpack = Path.Combine(game, "game", "sqpack");
        if (!Directory.Exists(sqpack)) return null;
        return new Lumina.GameData(sqpack).GetFile(path)?.Data;
    }

    [Fact]
    public void The_swapped_skin_is_only_the_variant_the_body_draws()
    {
        if (!Directory.Exists(BodyMod) || Vanilla(GamePath) is not { } garmentBytes
            || Vanilla(SourcePath) is not { } sourceBytes)
        {
            output.WriteLine("not available");
            return;
        }

        var catalog = BodySizeCatalog.Read(BodyMod);
        var option = catalog.For("_dwn").First(o => o.FullLabel.Contains("Gen A Small") && o.FullLabel.Contains("DEFAULT"));
        byte[] targetBytes = File.ReadAllBytes(catalog.PathOf(option));
        var whole = ModelPartReader.Read(targetBytes)!;

        // The mod's defaults: thin shins (a), and its two pube options (c, d).
        ushort mask = ImcEntrySource.MaskFor(BodyMod, 0, "Legs", null)!.Value;
        var hidden = BodyRetarget.UndrawnVariants(whole.AttributeNames, "_dwn", mask);
        output.WriteLine($"mask 0x{mask:x}; not drawn: {string.Join(", ", hidden)}");
        Assert.Contains("atr_dv_b", hidden);
        Assert.DoesNotContain("atr_dv_a", hidden);

        var target = BodyRetarget.Without(whole, hidden);
        var source = ModelPartReader.Read(sourceBytes)!;
        var remap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), @"E:\repos\Proteus\Proteus");
        Assert.True(BodyCorrespondence.TryBuild(source, BodyRetargetDiagTests.Uv(sourceBytes), target,
                                                BodyRetargetDiagTests.Uv(targetBytes), "_dwn", out var built,
                                                out string refusal, remap), refusal);
        var pairs = new List<BodyRetarget.SlotPair> { new("_dwn", built!, target, targetBytes, sourceBytes, hidden) };

        var garment = ModelPartReader.Read(garmentBytes)!;
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_dwn", replaceSkin: true, acrossBodies: true,
                                        clearBody: true);
        output.WriteLine($"{planned.Report}");
        var refit = ModelPartReader.Read(planned.Model)!;

        // Every skin vertex the refit draws lies on the drawn body. The thick shins sat up to 10 mm off it.
        var drawn = new BodySurface(target, BodySurface.CellFor(0.01f));
        float worst = 0f;
        foreach (int v in refit.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                               .SelectMany(p => p.Triangles).Distinct())
        {
            var p = new Vector3(refit.Positions[v * 3], refit.Positions[v * 3 + 1], refit.Positions[v * 3 + 2]);
            worst = MathF.Max(worst, drawn.Nearest(p, 0.05f, out var hit) ? hit.Distance : 0.05f);
        }
        output.WriteLine($"worst skin vertex off the drawn body: {worst * 1000:F3} mm");
        Assert.True(worst < 1e-4f, $"a skin vertex sits {worst * 1000:F2} mm off the body the game draws");
    }
}
