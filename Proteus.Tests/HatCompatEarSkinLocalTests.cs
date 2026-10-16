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
/// The ear-skin rule on real Miqo'te hairstyles, against the vanilla face. What each one must keep was read by hand
/// from the model (2026-10-16): which mesh is the ear, and that its skin rides the head bone — not from the rule itself.
/// Every hairstyle is solved from its AUTHOR's file: where the one in the mod is already fitted, Proteus's backup.
/// </summary>
public class HatCompatEarSkinLocalTests(ITestOutputHelper output)
{
    private const string UmbraMod = LocalData.Mods + ":CO Umbra w Scrunchie";
    private const string UmbraRel = "items/chara/human/c0801/obj/hair/h0115/model/c0801h0115_hir.mdl";
    private const string PowerMod = LocalData.Mods + ":[Yuria] Power Hair (162)";
    private const string PowerLong = "3D Install - Miqo'te/Long hair - longer bang/chara/human/c0801/obj/hair/h0162/model/c0801h0162_hir.mdl";
    private const string PowerShort = "3D Install - Miqo'te/Long hair - short bang/chara/human/c0801/obj/hair/h0162/model/c0801h0162_hir.mdl";
    private const string KikiroMod = LocalData.Mods + ":Commission AVA Kikiro Hair";
    private const string KikiroRel = "hair kikiro/007/chara/human/c0801/obj/hair/h0007/model/c0801h0007_hir.mdl";
    private const string TideRotMod = LocalData.Mods + ":⟡LM_TideRot";
    private const string TideRotRel = "races/miqote/chara/human/c0801/obj/hair/h0118/model/c0801h0118_hir.mdl";

    // Opening the game's data costs seconds; the faces are read once for every test here.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Faces = new();

    private static byte[] Face(string race = "0801") => Faces.GetOrAdd(race, r =>
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        return data.GetFile($"chara/human/c{r}/obj/face/f0001/model/c{r}f0001_fac.mdl")!.Data;
    });

    private static HatCompatSolve.Result Solve(byte[] hair, bool withoutEarSkin = false, string race = "0801")
    {
        HatCompatSolve.WithoutEarSkin = withoutEarSkin;
        try { return HatCompatSolve.Solve(hair, ModelPartReader.Read(hair)!, Face(race), raceCode: race); }
        finally { HatCompatSolve.WithoutEarSkin = false; }
    }

    /// <summary>The author's file: Proteus's backup where the file in the mod is already fitted.</summary>
    private static byte[] Author(string modNeed, string rel)
    {
        string modRoot = LocalData.Path(modNeed);
        var backup = Path.Combine(modRoot, "Proteus", "hatcompat-backup", rel.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllBytes(File.Exists(backup) ? backup : Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>Umbra's ear is mesh 1: fur, and 26 head-bone triangles of skin around the ear's root.</summary>
    [LocalDataFact(LocalData.GameData, UmbraMod)]
    [Trait("Plan", "AC-miqote-ear-skin-1")]
    [Trait("Plan", "AC-miqote-ear-skin-2")]
    [Trait("Plan", "EDGE-miqote-ear-skin-2")]
    public void UmbrasEarKeepsItsSkinUnderAHatAndIsNeverPressed()
    {
        var r = Solve(Author(UmbraMod, UmbraRel));
        Assert.DoesNotContain(r.Cut, p => p.Mesh == 1);
        Assert.DoesNotContain(1, r.Moved.Keys);
        Assert.NotEmpty(r.Cut);                     // the hair itself still goes under the hat
        // And the rule is what kept it: version 32 cut 24 triangles of that ear.
        Assert.Contains(Solve(Author(UmbraMod, UmbraRel), withoutEarSkin: true).Cut, p => p.Mesh == 1);
    }

    /// <summary>Power Hair's ear is mesh 3: 60 head-bone triangles of skin beside the fur.</summary>
    [LocalDataFact(LocalData.GameData, PowerMod)]
    [Trait("Plan", "AC-miqote-ear-skin-1")]
    public void PowerHairsEarKeepsItsSkinUnderAHatAndIsNeverPressed()
    {
        var r = Solve(Author(PowerMod, PowerLong));
        Assert.DoesNotContain(r.Cut, p => p.Mesh == 3);
        Assert.DoesNotContain(3, r.Moved.Keys);
        Assert.NotEmpty(r.Cut);
    }

    /// <summary>Two options of one hair pack serving the same hairstyle get the same rule.</summary>
    [LocalDataFact(LocalData.GameData, PowerMod)]
    [Trait("Plan", "EDGE-miqote-ear-skin-7")]
    public void EverySiblingOptionGetsTheSameEarRule()
    {
        foreach (var rel in new[] { PowerLong, PowerShort })
            Assert.DoesNotContain(Solve(Author(PowerMod, rel)).Cut, p => p.Mesh == 3);
    }

    /// <summary>
    /// Kikiro welds its scalp into the fur's submesh (4,774 non-ear triangles beside the fur). That is no ear submesh:
    /// the ear-skin rule leaves its cut exactly as version 32 made it.
    /// </summary>
    [LocalDataFact(LocalData.GameData, KikiroMod)]
    [Trait("Plan", "AC-miqote-ear-skin-3")]
    [Trait("Plan", "EDGE-miqote-ear-skin-1")]
    public void KikirosWeldedHairIsCutExactlyAsBefore()
    {
        var hair = Author(KikiroMod, KikiroRel);
        Assert.Equal(Key(Solve(hair, withoutEarSkin: true).Cut), Key(Solve(hair).Cut));
    }

    /// <summary>
    /// A hairstyle with hat support of its own (TideRot ships <c>shp_hib</c>), fitted anyway — taken over or replaced.
    /// Both go through the same solve, and its ear (mesh 2) keeps its skin either way.
    /// </summary>
    [LocalDataFact(LocalData.GameData, TideRotMod)]
    [Trait("Plan", "EDGE-miqote-ear-skin-5")]
    [Trait("Plan", "EDGE-miqote-ear-skin-6")]
    public void ATakeOverOrAReplacementKeepsTheEarSkin()
    {
        var hair = Author(TideRotMod, TideRotRel);
        // The rule is what keeps it: version 32's solve cuts 28 triangles of that ear.
        Assert.Contains(Solve(hair, withoutEarSkin: true).Cut, p => p.Mesh == 2);

        var replaced = HatCompatService.Inspect(hair, TideRotRel, Face(), "0801", replaceAuthored: true)!;
        Assert.True(replaced.Replaced);
        Assert.DoesNotContain(replaced.Solve.Cut, p => p.Mesh == 2);

        // TideRot's own mask hides hair no hat covers, so Proteus takes it over — through the same solve.
        var takenOver = HatCompatService.Inspect(hair, TideRotRel, Face(), "0801", replaceAuthored: false)!;
        Assert.True(takenOver.TookOver, "TideRot is no longer a take-over; pick a hairstyle that is");
        Assert.DoesNotContain(takenOver.Solve.Cut, p => p.Mesh == 2);
    }

    /// <summary>
    /// The second cut takes what the press wanted and could not afford. With the shape budget all but spent (capped at
    /// 10 values), it runs — and still never reaches the ear.
    /// </summary>
    [LocalDataFact(LocalData.GameData, UmbraMod)]
    [Trait("Plan", "AC-miqote-ear-skin-1")]
    [Trait("Plan", "AC-miqote-ear-skin-3")]
    public void TheSecondCutNeverReachesTheEarWhenTheBudgetIsSpent()
    {
        var hair = Author(UmbraMod, UmbraRel);
        HatCompatSolve.Result r;
        HatCompatSolve.BudgetCap = 10;
        try { r = Solve(hair); }
        finally { HatCompatSolve.BudgetCap = null; }
        Assert.True(r.Dropped > 0, "the budget was not short; the second cut never ran");
        Assert.DoesNotContain(r.Cut, p => p.Mesh == 1);
        Assert.DoesNotContain(1, r.Moved.Keys);
    }

    /// <summary>
    /// The press now stops beyond the skin ring instead of at the fur's root row. Where pressed hair meets the
    /// unpressed ear, no triangle may fold over or collapse — a folded triangle reads in game as a hole.
    /// </summary>
    [LocalDataFact(LocalData.GameData, UmbraMod, PowerMod)]
    [Trait("Plan", "AC-miqote-ear-skin-1")]
    public void ThePressFoldsNothingWhereItMeetsTheEar()
    {
        foreach (var hair in new[] { Author(UmbraMod, UmbraRel), Author(PowerMod, PowerLong) })
        {
            var src = SecondSkinWriter.Parse(hair);
            var meshes = HatCompatSolve.ReadLod0Meshes(hair);
            var ears = HatCompatSolve.ReadEarGeometry(hair, src, meshes);
            var r = Solve(hair);
            int folded = 0, collapsed = 0, boundary = 0;
            foreach (var mv in meshes)
            {
                if (!r.Moved.TryGetValue(mv.Mesh, out var moved)) continue;
                int mo = src.MeshStart + mv.Mesh * 36;
                uint ic = BitConverter.ToUInt32(hair, mo + 4), start = BitConverter.ToUInt32(hair, mo + 16);
                for (uint t = 0; t + 3 <= ic; t += 3)
                {
                    var idx = Enumerable.Range(0, 3).Select(k => (int)BitConverter.ToUInt16(hair, src.Ib + (int)(start + t + k) * 2)).ToArray();
                    bool anyMoved = idx.Any(moved.ContainsKey);
                    bool anyEar = idx.Any(i => ears.Touched.Contains(((long)mv.Mesh << 32) | (uint)i));
                    if (!anyMoved || !anyEar) continue;
                    boundary++;
                    Vector3 Before(int i) => mv.Positions[i];
                    Vector3 After(int i) => moved.TryGetValue(i, out var to) ? to : mv.Positions[i];
                    var n0 = Vector3.Cross(Before(idx[1]) - Before(idx[0]), Before(idx[2]) - Before(idx[0]));
                    var n1 = Vector3.Cross(After(idx[1]) - After(idx[0]), After(idx[2]) - After(idx[0]));
                    if (Vector3.Dot(n0, n1) < 0) folded++;
                    if (n1.Length() < 1e-9f && n0.Length() >= 1e-9f) collapsed++;
                }
            }
            output.WriteLine($"boundary triangles {boundary}, folded {folded}, collapsed {collapsed}");
            Assert.Equal(0, folded);
            Assert.Equal(0, collapsed);
        }
    }

    /// <summary>
    /// Every installed Miqo'te hairstyle: the ear-skin rule changes the cut ONLY inside ear submeshes (where fur
    /// outnumbers the rest) — never spares a scalp triangle elsewhere through a shared vertex — and only within reach of
    /// the fur. That is what keeps hair from newly poking through hats.
    /// </summary>
    [LocalDataFact(LocalData.GameData, LocalData.Mods)]
    [Trait("Plan", "AC-miqote-ear-skin-3")]
    [Trait("Plan", "AC-miqote-ear-skin-4")]
    public void NoMiqoteHairstyleGainsPokeThroughOutsideItsEar()
    {
        string mods = LocalData.Path(LocalData.Mods);
        var seen = new HashSet<string>();
        var failed = new List<string>();
        int hairs = 0, changed = 0;
        foreach (var file in Directory.EnumerateFiles(mods, "c0?01h*_hir.mdl", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (!name.StartsWith("c0801") && !name.StartsWith("c0701")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}Proteus{Path.DirectorySeparatorChar}")) continue;
            // The author's file, never a patch of ours.
            string? modRoot = ModRootOf(mods, file);
            var rel = modRoot == null ? null : Path.GetRelativePath(modRoot, file);
            var backup = rel == null ? null : Path.Combine(modRoot!, "Proteus", "hatcompat-backup", rel);
            var hair = File.ReadAllBytes(backup != null && File.Exists(backup) ? backup : file);
            if (!seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(hair)))) continue;
            // No ear bone (an earless conversion): the rule never runs, so the two solves could only agree.
            if (HatCompatSolve.VersionFor(hair) == HatCompatSolve.WithoutEars) continue;

            string race = name.Substring(1, 4);
            HatCompatSolve.Result before, after;
            try { before = Solve(hair, withoutEarSkin: true, race: race); after = Solve(hair, race: race); }
            catch (Exception ex) { failed.Add($"{Path.GetRelativePath(mods, file)}: {ex.Message}"); continue; }
            hairs++;

            var earSubmeshes = EarSubmeshes(hair);
            var outsideBefore = Key(before.Cut.Where(p => !earSubmeshes.Contains((p.Mesh, p.Submesh))));
            var outsideAfter = Key(after.Cut.Where(p => !earSubmeshes.Contains((p.Mesh, p.Submesh))));
            Assert.True(outsideBefore == outsideAfter, $"{Path.GetRelativePath(mods, file)}: the cut outside the ear changed");

            // Nor the press: a skin vertex shared with another submesh would leave that submesh's hair unpressed.
            var earVertices = EarSubmeshVertices(hair, earSubmeshes);
            Assert.True(PressOutside(before, earVertices) == PressOutside(after, earVertices),
                        $"{Path.GetRelativePath(mods, file)}: the press outside the ear changed");
            if (Key(before.Cut) != Key(after.Cut)) changed++;
        }
        output.WriteLine($"{hairs} Miqo'te hairstyles, {changed} with ear skin newly spared");
        foreach (var f in failed) output.WriteLine("could not solve " + f);
        Assert.Empty(failed);
        Assert.True(hairs > 0);
    }

    /// <summary>Every vertex any triangle of an ear submesh names.</summary>
    private static HashSet<long> EarSubmeshVertices(byte[] mdl, HashSet<(int, int)> earSubmeshes)
    {
        var src = SecondSkinWriter.Parse(mdl);
        var found = new HashSet<long>();
        foreach (var (mesh, sub) in earSubmeshes)
        {
            int mo = src.MeshStart + mesh * 36;
            ushort si = BitConverter.ToUInt16(mdl, mo + 10);
            int ss = src.SubmeshStart + (si + sub) * 16;
            uint io = BitConverter.ToUInt32(mdl, ss), ic = BitConverter.ToUInt32(mdl, ss + 4);
            for (uint i = 0; i < ic; i++)
                found.Add(((long)mesh << 32) | BitConverter.ToUInt16(mdl, src.Ib + (int)(io + i) * 2));
        }
        return found;
    }

    /// <summary>The press on every vertex outside the ear submeshes, as one comparable string.</summary>
    private static string PressOutside(HatCompatSolve.Result r, HashSet<long> earVertices)
        => string.Join(";", r.Moved.OrderBy(m => m.Key).SelectMany(m => m.Value
               .Where(v => !earVertices.Contains(((long)m.Key << 32) | (uint)v.Key))
               .OrderBy(v => v.Key).Select(v => $"{m.Key}.{v.Key}:{v.Value.X:F5},{v.Value.Y:F5},{v.Value.Z:F5}")));

    private static string? ModRootOf(string mods, string file)
    {
        var rel = Path.GetRelativePath(mods, file);
        int sep = rel.IndexOf(Path.DirectorySeparatorChar);
        return sep < 0 ? null : Path.Combine(mods, rel[..sep]);
    }

    /// <summary>Submeshes where triangles with an ear-weighted corner (≥ the fur floor) outnumber the rest.</summary>
    private static HashSet<(int, int)> EarSubmeshes(byte[] mdl)
    {
        var src = SecondSkinWriter.Parse(mdl);
        HatCompatSolve.WithoutEarSkin = true;
        HashSet<long> fur;
        try { fur = HatCompatSolve.ReadEarGeometry(mdl, src, HatCompatSolve.ReadLod0Meshes(mdl)).Fur; }
        finally { HatCompatSolve.WithoutEarSkin = false; }
        var found = new HashSet<(int, int)>();
        foreach (var mv in HatCompatSolve.ReadLod0Meshes(mdl))
        {
            int mo = src.MeshStart + mv.Mesh * 36;
            ushort si = BitConverter.ToUInt16(mdl, mo + 10), sc = BitConverter.ToUInt16(mdl, mo + 12);
            for (int s = 0; s < sc; s++)
            {
                int ss = src.SubmeshStart + (si + s) * 16;
                uint io = BitConverter.ToUInt32(mdl, ss), ic = BitConverter.ToUInt32(mdl, ss + 4);
                int furTris = 0;
                for (uint t = 0; t + 3 <= ic; t += 3)
                    if (Enumerable.Range(0, 3).Any(k => fur.Contains(((long)mv.Mesh << 32)
                            | BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t + k) * 2)))) furTris++;
                if (furTris > (int)(ic / 3) - furTris) found.Add((mv.Mesh, s));
            }
        }
        return found;
    }

    /// <summary>A cut as one comparable string: piece by piece, which triangles.</summary>
    private static string Key(IEnumerable<ModelPart> cut)
        => string.Join(";", cut.OrderBy(p => p.Mesh).ThenBy(p => p.Submesh)
                               .Select(p => $"{p.Mesh}.{p.Submesh}:{(p.Island < 0 ? "all" : string.Join(",", p.Ordinals))}"));
}
