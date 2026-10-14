using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumina.Excel.Sheets;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The game's own Scion Adventurer's gear refitted onto Rue+, as the automatic refit does it: from the game's body,
/// skin replaced. Reported as "the new skin isn't right and my tattoos don't show": skin the refit kept of the garment's
/// own still named the game's skin material, which on a Rue character draws a texture the body's composite never
/// touches. Needs the game's data and Rue+ installed.
/// </summary>
public class ScionAdventurerRefitTests(ITestOutputHelper output)
{
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";

    /// <summary>EquipSlotCategory row to the body slot its model is: body, hands, legs, feet.</summary>
    private static readonly Dictionary<uint, string> SlotOf = new() { [4] = "_top", [5] = "_glv", [7] = "_dwn", [8] = "_sho" };

    private sealed record Piece(string Name, string Slot, string GamePath, byte[] Model);

    private static List<Piece> ScionAdventurer(Lumina.GameData data)
    {
        var pieces = new List<Piece>();
        foreach (var item in data.GetExcelSheet<Item>()!)
        {
            string name = item.Name.ExtractText();
            if (!name.StartsWith("Scion Adventurer's", StringComparison.Ordinal)) continue;
            if (!SlotOf.TryGetValue(item.EquipSlotCategory.RowId, out string? slot)) continue;
            int set = (int)(item.ModelMain & 0xFFFF);
            string path = $"chara/equipment/e{set:D4}/model/c0201e{set:D4}{slot}.mdl";
            if (data.GetFile(path) is { } file && pieces.All(p => p.GamePath != path))
                pieces.Add(new Piece(name, slot, path, file.Data));
        }
        return pieces;
    }

    /// <summary>
    /// The skin materials a model DRAWS with that the body's own skin does not. Drawn, not merely listed: a swap can
    /// leave a material in the table with no mesh under it, which shows nothing.
    /// </summary>
    private static List<string> Foreign(byte[] model, IReadOnlyCollection<string> bodySkins)
        => ModelPartReader.Read(model)!.Parts
                          .Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)
                                      && !bodySkins.Contains(p.Material))
                          .Select(p => p.Material).Distinct().ToList();

    [LocalDataFact(LocalData.GameData, Rue)]
    public void Kept_skin_is_drawn_with_Rues_material_and_layout()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        var rue = BodySizeCatalog.Read(LocalData.Path(Rue));

        var pieces = ScionAdventurer(data);
        Assert.NotEmpty(pieces);
        int failedBefore = 0;
        foreach (var piece in pieces)
        {
            var garment = ModelPartReader.Read(piece.Model)!;
            string source = vanilla.PathOf(vanilla.For(piece.Slot, "0201")[0]);
            string target = rue.PathOf(rue.For(piece.Slot, "0201")[0]);
            var bodySkins = SecondSkinWriter.Parse(File.ReadAllBytes(target)).MatNames
                                            .Where(SecondSkinWriter.IsBodySkinMaterial).ToList();

            Assert.Null(BodyRetarget.BuildPair(piece.Slot, source, target, piece.Slot, false, null, null, uv, out var pair));
            var plan = BodyRetarget.Plan(garment, piece.Model, [pair], piece.Slot, replaceSkin: true, acrossBodies: true,
                                         cutHidden: true);

            // The old code: the refit as it was saved.
            var before = Foreign(plan.Model, bodySkins);
            // The new: the kept skin redrawn like the body's.
            var fixedModel = RefitCore.MatchSkinToBody(plan.Model, source, target, uv, false, log);
            var after = Foreign(fixedModel, bodySkins);

            output.WriteLine($"{piece.Name} ({piece.GamePath}): swap {plan.Report.Swap}");
            output.WriteLine("   garment draws: " + Drawn(garment));
            output.WriteLine("   old output draws: " + Drawn(ModelPartReader.Read(plan.Model)!));
            output.WriteLine($"   old: skin not the body's = [{string.Join(", ", before)}]");
            output.WriteLine($"   new: skin not the body's = [{string.Join(", ", after)}]; skin materials now " +
                             string.Join(", ", SecondSkinWriter.Parse(fixedModel).MatNames.Where(SecondSkinWriter.IsBodySkinMaterial)));
            if (before.Count > 0) failedBefore++;
            Assert.Empty(after);

            // Where skin was kept and moved, it moved onto the bibo sheet: the game body's layout is one side of it.
            if (before.Count > 0)
            {
                // Vertex for vertex, in order: the rewrite edits coordinates where they sit and the rename moves nothing,
                // so every skin vertex keeps its index. (Matching by position is wrong here — the midline seam has two
                // vertices at one position, one per side of the sheet.)
                Assert.True(SecondSkinWriter.TryReadLod0Geometry(plan.Model, out var pos, out var uvOld, out _));
                Assert.True(SecondSkinWriter.TryReadLod0Geometry(fixedModel, out var pos2, out var uvNew, out _));
                Assert.Equal(pos, pos2);
                Assert.True(SecondSkinWriter.TryReadLod0Geometry(plan.Model, out var keptPos, out _, out _,
                                                                 m => before.Contains(m)));

                int moved = 0, onSheet = 0;
                for (int i = 0; i < pos.Length / 3; i++)
                {
                    if (uvOld[i * 2] == uvNew[i * 2] && uvOld[i * 2 + 1] == uvNew[i * 2 + 1]) continue;
                    moved++;
                    float x = pos[i * 3];
                    float right = 0.5f + Frac(uvOld[i * 2]) * 0.5f;
                    bool plus = MathF.Abs(Frac(uvNew[i * 2]) - right) < 3e-3f;
                    bool minus = MathF.Abs(Frac(uvNew[i * 2]) - UVRemapService.MirrorU(right)) < 3e-3f;
                    // Off the midline the vertex's own side decides; on it, either half is a side the seam belongs to.
                    bool ok = MathF.Abs(x) < 0.02f ? plus || minus : x > 0 ? plus : minus;
                    if (ok && MathF.Abs(uvNew[i * 2 + 1] - uvOld[i * 2 + 1]) < 3e-3f) onSheet++;
                }
                output.WriteLine($"   kept skin converted: {moved} of {keptPos.Length / 3} kept vertices moved, " +
                                 $"{onSheet} exactly where gen2→bibo puts them");
                Assert.Equal(keptPos.Length / 3, moved);
                Assert.Equal(moved, onSheet);
            }
        }
        output.WriteLine($"{failedBefore} of {pieces.Count} pieces kept skin the old code left in the game's material");
        Assert.True(failedBefore > 0, "none of the Scion Adventurer's gear reproduced the old fault");
    }

    private static string Drawn(ModelParts m)
        => string.Join(", ", m.Parts.Where(p => p.Island < 0).GroupBy(p => p.Material)
                              .Select(g => $"{g.Key} ({g.Sum(p => p.Triangles.Length / 3)} tris)"));

    private static float Frac(float u) => u - MathF.Floor(u);

    private static (int, int, int) Key(float[] p, int v)
        => ((int)MathF.Round(p[v * 3] * 1e4f), (int)MathF.Round(p[v * 3 + 1] * 1e4f), (int)MathF.Round(p[v * 3 + 2] * 1e4f));
}
