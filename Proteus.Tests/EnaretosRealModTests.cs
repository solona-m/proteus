using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The user's real mod (<see cref="LocalData.EnaretosMod"/>): a row matcap on the Enaretos bracelet the gear shader
/// upgrade moved to character.shpk — the case that was refused. Every test works on a whole-folder temp copy and checks
/// the original is untouched.
/// </summary>
public class EnaretosRealModTests : IDisposable
{
    private const string ModelPath = "chara/accessory/a0153/model/c0201a0153_wrs.mdl";
    private const string ModelRel = "body shape/bibo+/chara/accessory/a0153/model/c0201a0153_wrs.mdl";
    private const string Mat = "/mt_c0201a0153_wrs_a.mtrl";
    private const string MatRel = "enaretos bracelet/chara/accessory/a0153/material/v0001/mt_c0201a0153_wrs_a.mtrl";

    private readonly string temp = Path.Combine(Path.GetTempPath(), "proteus-enaretos-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TextureLoader loader = new(Substitute.For<Dalamud.Plugin.Services.IDataManager>(),
                                                Substitute.For<Dalamud.Plugin.Services.IPluginLog>());

    public void Dispose()
    {
        try { Directory.Delete(temp, true); } catch { }
    }

    private static Dictionary<string, string> Hashes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                 .ToDictionary(f => Path.GetRelativePath(root, f), f => HairPortPlan.Sha8(File.ReadAllBytes(f)));

    /// <summary>The original's hashes, and a whole-folder copy of it under a temp mods root.</summary>
    private (Dictionary<string, string> Original, string Copy) CopyMod()
    {
        var src = LocalData.Path(LocalData.EnaretosMod);
        var original = Hashes(src);
        var copy = Path.Combine(temp, "mods", Path.GetFileName(src));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(copy, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return (original, copy);
    }

    private void Untouched(Dictionary<string, string> original)
    {
        var now = Hashes(LocalData.Path(LocalData.EnaretosMod));
        Assert.Equal(original.Keys.Order(), now.Keys.Order());
        foreach (var (k, v) in original) Assert.True(v == now[k], $"the original mod's {k} changed");
    }

    /// <summary>The copy without the owner's own matcaps (the real mod may carry some): every test starts from none.</summary>
    private static void NoMatcaps(string mod)
    {
        if (!MatcapRecord.Exists(mod)) return;
        foreach (var m in MatcapRecord.Read(mod).Models.Select(x => x.File).ToList())
            if (MatcapRecord.Exists(mod)) MatcapWriter.Undo(mod, m);
        Assert.False(MatcapRecord.Exists(mod), "the copy's matcaps could not be undone");
    }

    private MatcapStudio.Environment Env(Func<string, byte[]?> game) =>
        new(p => p == MatcapMaterial.VanillaTemplatePath ? SyntheticMatcapMod.Reflection() : game(p),
            loader, false, Path.Combine(temp, "work"), WornFolder: 1, Affordable: () => false, GearShadersOn: true);

    private static IReadOnlyList<ModelPart> Pool(byte[] model) =>
        MatcapPlan.RowPool(ModelPartReader.Read(model)!.Parts, MatcapPlan.Kind.Gear, [], Mat);

    private int UsedRow(string mod, MatcapStudio.Environment env)
    {
        var info = MatcapStudio.ReadRowInfo(mod, ModelRel, ModelPath, Mat, env);
        Assert.NotNull(info);
        Assert.NotNull(info!.UsedRows);
        return info.UsedRows!.Order().First();
    }

    private static MatcapImage Image(string dir, TextureLoader loader)
    {
        var rgba = new byte[32 * 32 * 4];
        for (int i = 0; i < 32 * 32; i++) { rgba[i * 4] = (byte)i; rgba[i * 4 + 1] = 90; rgba[i * 4 + 2] = 200; rgba[i * 4 + 3] = 255; }
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "gem.tex");
        Assert.True(loader.WriteTex(rgba, 32, 32, path));
        return new MatcapImage("gem.tex", path, false);
    }

    [LocalDataFact(LocalData.EnaretosMod, LocalData.GameData)]
    [Trait("Plan", "AC-row-matcap-glass-1")]
    [Trait("Plan", "AC-row-matcap-glass-2")]
    [Trait("Plan", "AC-row-matcap-glass-4")]
    public void Real_bracelet_glass_row_round_trip()
    {
        const string glassMat = "/mt_c0201a0153_wrs_b.mtrl";
        const string glassRel = "enaretos bracelet/chara/accessory/a0153/material/v0001/mt_c0201a0153_wrs_b.mtrl";
        var (original, mod) = CopyMod();
        NoMatcaps(mod);
        var game = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        byte[]? Read(string p) { try { return game.GetFile(p)?.Data; } catch { return null; } }
        var baseline = Hashes(mod);
        var glassBefore = File.ReadAllBytes(MatcapWriter.Full(mod, glassRel));

        var env = Env(Read) with { RowOnly = 8, RowSub = SubRowPick.B };
        var model = File.ReadAllBytes(MatcapWriter.Full(mod, ModelRel));
        var pool = MatcapPlan.RowPool(ModelPartReader.Read(model)!.Parts, MatcapPlan.Kind.Gear, [], glassMat);
        var p = MatcapStudio.Prepare(mod, ModelRel, ModelPath, model, pool, MatcapChoice.OfImage(Image(Path.Combine(temp, "img"), loader)), env);
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        Assert.True(p.RowGlass);
        Assert.True(p.RowGlassTriangles > 0);

        var applied = MatcapWriter.Apply(mod, p.Request!);
        Assert.True(applied.Ok, applied.Message);
        var row = MatcapRecord.Read(mod).Materials.Single().Row!;
        var cut = row.Files.Single();
        // The count shown is the triangles the open model now draws from the converted copy.
        var drawn = File.ReadAllBytes(MatcapWriter.Full(mod, ModelRel));
        Assert.Equal(p.RowGlassTriangles, ModelPartReader.Read(drawn)!.Parts.Where(x => x.Island < 0 && x.Material == row.Cut)
                                                  .Sum(x => ModelMaterialWriter.TriangleUvs(drawn, [x]).Count));
        var cutBytes = File.ReadAllBytes(MatcapWriter.Full(mod, cut.File));
        Assert.Equal(CharacterShaderUpgrade.Character, CharacterShaderUpgrade.ShaderOf(cutBytes));
        Assert.True(MatcapGlassCut.IsSound(glassBefore, cutBytes, Read(MatcapGlassCut.TemplatePath)!,
                                           MatcapMaterial.ReadHeader(cutBytes)!.TextureOf(MatcapMaterial.SamplerNormal)));
        Assert.Equal(glassBefore, File.ReadAllBytes(MatcapWriter.Full(mod, glassRel)));   // the author's glass untouched
        Assert.Equal(MatcapGlassCut.GlassShader, CharacterShaderUpgrade.ShaderOf(glassBefore));

        // The whole row was moved: no triangle still drawn as glass touches a row texel of any bake.
        var bakes = p.Request!.Materials.Single().Row!.Cut.Bakes;
        var stillGlass = ModelPartReader.Read(drawn)!.Parts
            .Where(x => x.Island < 0 && string.Equals(x.Material, glassMat, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(stillGlass);   // the rest of the part is still the author's glass (AC-2)
        foreach (var part in stillGlass)
            Assert.DoesNotContain(ModelMaterialWriter.TriangleUvs(drawn, [part]), t => MatcapRowCut.Touches(t, bakes));

        Assert.True(MatcapWriter.Undo(mod, ModelRel).Ok);
        var after = Hashes(mod);
        Assert.Equal(baseline.Keys.Order(), after.Keys.Where(k => !k.Contains("-superseded")).Order());
        foreach (var (k, v) in baseline) Assert.True(v == after[k], $"{k} differs after Undo");
        Untouched(original);
    }

    [LocalDataFact(LocalData.EnaretosMod, LocalData.GameData)]
    [Trait("Plan", "EDGE-row-matcap-glass-13")]
    public void Glass_row_applies_beside_an_existing_row_matcap_and_undo_restores_both()
    {
        const string glassMat = "/mt_c0201a0153_wrs_b.mtrl";
        var (original, mod) = CopyMod();
        NoMatcaps(mod);
        var baseline = Hashes(mod);
        var game = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        byte[]? Read(string p) { try { return game.GetFile(p)?.Data; } catch { return null; } }
        var image = MatcapChoice.OfImage(Image(Path.Combine(temp, "img"), loader));

        // First a row matcap on _a (the owner's own case), then the glass row on _b beside it.
        var envA = Env(Read) with { RowOnly = UsedRow(mod, Env(Read)) };
        var model = File.ReadAllBytes(MatcapWriter.Full(mod, ModelRel));
        var pa = MatcapStudio.Prepare(mod, ModelRel, ModelPath, model, Pool(model), image, envA);
        Assert.True(pa.Ok, pa.Code + ": " + pa.Message);
        Assert.True(MatcapWriter.Apply(mod, pa.Request!).Ok);
        var aCut = MatcapRecord.Read(mod).Materials.Single().Row!.Cut;

        var envB = Env(Read) with { RowOnly = 8, RowSub = SubRowPick.B };
        model = File.ReadAllBytes(MatcapWriter.Full(mod, ModelRel));
        var poolB = MatcapPlan.RowPool(ModelPartReader.Read(model)!.Parts, MatcapPlan.Kind.Gear, [], glassMat);
        var pb = MatcapStudio.Prepare(mod, ModelRel, ModelPath, model, poolB, image, envB);
        Assert.True(pb.Ok, pb.Code + ": " + pb.Message);
        Assert.True(pb.RowGlass);
        Assert.True(MatcapWriter.Apply(mod, pb.Request!).Ok);

        var record = MatcapRecord.Read(mod);
        Assert.Equal(2, record.Materials.Count(m => m.Row != null));
        var drawn = ModelPartReader.Read(File.ReadAllBytes(MatcapWriter.Full(mod, ModelRel)))!.Parts;
        Assert.Contains(drawn, x => x.Material == aCut);                                     // _a's cut still drawn
        Assert.Contains(PenumbraModMeta.ReadContainerRedirects(mod), r => r.GamePath.EndsWith(aCut.TrimStart('/'), StringComparison.OrdinalIgnoreCase));

        Assert.True(MatcapWriter.Undo(mod, ModelRel).Ok);                                      // both undone together
        var after = Hashes(mod);
        Assert.Equal(baseline.Keys.Order(), after.Keys.Where(k => !k.Contains("-superseded")).Order());
        foreach (var (k, v) in baseline) Assert.True(v == after[k], $"{k} differs after Undo");
        Untouched(original);
    }

    [LocalDataFact(LocalData.EnaretosMod)]
    [Trait("Plan", "AC-upgrade-vs-matcap-1")]
    public void Make_cuts_the_real_upgraded_bracelet()
    {
        var (original, mod) = CopyMod();
        NoMatcaps(mod);
        var env = Env(_ => null);
        var row = UsedRow(mod, env);
        var target = MatcapPlan.TargetOf(ModelPath, out _)!;
        var (plan, refused) = MatcapRowCut.Make(new MatcapRowCut.Inputs(mod, target, Mat, [1], 1, env with { RowOnly = row }, row,
            SubRowPick.Both, "abc123", "/mt_c0201a0153_wrs_a_mcutabc1231.mtrl"));
        Assert.True(refused == null, refused?.Code + ": " + refused?.Message);
        var file = Assert.Single(plan!.Files);
        Assert.True(PenumbraModMeta.SameFile(MatRel, file.Source.File!));
        Assert.True(PenumbraModMeta.SameFile("enaretos bracelet/chara/accessory/a0153/texture/c0201a0153_wrs_n.tex", file.Normal.File!));
        Assert.True(PenumbraModMeta.SameFile("enaretos bracelet/chara/accessory/a0153/texture/c0201a0153_wrs_id.tex", file.Index!.File!));
        Untouched(original);
    }

    [LocalDataFact(LocalData.EnaretosMod, LocalData.GameData)]
    [Trait("Plan", "AC-upgrade-vs-matcap-1")]
    public void Prepare_follows_a0153s_real_imc()
    {
        var (original, mod) = CopyMod();
        NoMatcaps(mod);
        var game = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        byte[]? Read(string p) { try { return game.GetFile(p)?.Data; } catch { return null; } }
        var env = Env(Read);
        var row = UsedRow(mod, env);
        var item = MatcapPlan.TargetOf(ModelPath, out _)!.Item!.Value;
        var gameImc = ImcEntrySource.GameEntries(Read, item.SetTag, item.SlotLabel);
        Assert.NotNull(gameImc);
        Assert.NotEmpty(gameImc!);
        var redirects = PenumbraModMeta.ReadAllRedirects(mod);
        var expected = MatcapPlan.GearFolders(item, gameImc, MatcapPlan.ModImcEntries(mod, item), redirects.Select(r => r.GamePath), 1);

        var model = File.ReadAllBytes(MatcapWriter.Full(mod, ModelRel));
        var p = MatcapStudio.Prepare(mod, ModelRel, ModelPath, model, Pool(model), MatcapChoice.OfImage(Image(Path.Combine(temp, "img"), loader)),
                                     env with { RowOnly = row });
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        var folders = p.Request!.Materials.Single().Row!.Cut.Folders;
        Assert.Equal(expected.Order(), folders.Select(x => x.Folder).Order());

        var containers = PenumbraModMeta.ReadContainerRedirects(mod);
        string MatAt(int f) => item.MaterialDir(f).TrimEnd('/') + Mat;
        bool wornHeld = containers.Any(r => PenumbraModMeta.SameFile(r.GamePath, MatAt(1)));
        foreach (var x in folders)
        {
            var want = containers.Any(r => PenumbraModMeta.SameFile(r.GamePath, MatAt(x.Folder))) ? MatcapRecord.RowFingerprint.CaseContainer
                     : Read(MatAt(x.Folder)) != null ? MatcapRecord.RowFingerprint.CaseGame
                     : wornHeld ? MatcapRecord.RowFingerprint.CaseWorn
                     : MatcapRecord.RowFingerprint.CaseNone;
            Assert.True(want == x.Case, $"folder {x.Folder}: expected {want}, planned {x.Case}");
        }
        // The game's IMC was really read: a0153 lists the worn folder at least (the c0201 material is the mod's own, so
        // no folder is the game's case unless the game ships a c0201 copy — recorded in the PR either way).
        Assert.Contains(1, folders.Select(x => x.Folder));
        Assert.True(gameImc!.Length >= 1);
        Untouched(original);
    }

    [LocalDataFact(LocalData.EnaretosMod)]
    [Trait("Plan", "AC-upgrade-vs-matcap-3")]
    [Trait("Plan", "AC-upgrade-vs-matcap-5")]
    public void Real_bracelet_copy_round_trip()
    {
        var (original, mod) = CopyMod();
        NoMatcaps(mod);
        var env = Env(_ => null);
        var row = UsedRow(mod, env);
        var model = File.ReadAllBytes(MatcapWriter.Full(mod, ModelRel));
        var p = MatcapStudio.Prepare(mod, ModelRel, ModelPath, model, Pool(model), MatcapChoice.OfImage(Image(Path.Combine(temp, "img"), loader)),
                                     env with { RowOnly = row });
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        var applied = MatcapWriter.Apply(mod, p.Request!);
        Assert.True(applied.Ok, applied.Message);

        var rel = MeshVolumeService.Rel(MatRel);
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, ShaderUpgradeWriter.RevertOrForget(mod, rel, false, out var message));
        Assert.Contains(rel, message);
        Assert.True(MatcapWriter.Undo(mod, ModelRel).Ok);
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, ShaderUpgradeWriter.RevertOrForget(mod, rel, false, out _));
        var backup = File.ReadAllBytes(Path.Combine(LocalData.Path(LocalData.EnaretosMod), "Proteus", ShaderUpgradeWriter.BackupSubdir,
                                                    MatRel.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(backup, File.ReadAllBytes(MatcapWriter.Full(mod, MatRel)));
        Untouched(original);
    }
}
