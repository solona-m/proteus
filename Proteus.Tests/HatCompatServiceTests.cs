using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The hat-compatibility patch end to end against a mod folder on disk: the hairstyle gains a
/// <c>shp_hib</c> shape and its ponytails gain <c>atr_kam</c>, and revert leaves the folder byte for byte
/// as it was found.
/// <para/>
/// The revert half matters more than it looks. This edit goes INTO somebody else's mod rather than into a
/// Proteus redirect, so "undo" is the only way back — a backup that is subtly not the original is worse
/// than never having offered the feature.
/// </summary>
public class HatCompatServiceTests
{
    private const string ModelRel = "hair/c0201h0001_hir.mdl";
    private const string GamePath = "chara/human/c0201/obj/hair/h0001/model/c0201h0001_hir.mdl";

    /// <summary>A throwaway mod folder publishing one hair model.</summary>
    private sealed class Mod : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "proteus_hat_" + Path.GetRandomFileName());

        public Mod(byte[] model)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "meta.json"),
                "{\"FileVersion\":4,\"Name\":\"Bob\",\"Groups\":[],\"DefaultData\":{\"Files\":{"
                + $"\"{GamePath}\":\"{ModelRel}\"" + "}}}");
            var dest = Path.Combine(Root, ModelRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, model);
        }

        public byte[] Model()
            => File.ReadAllBytes(Path.Combine(Root, ModelRel.Replace('/', Path.DirectorySeparatorChar)));

        public string Backup =>
            Path.Combine(Root, "Proteus", HatCompatService.BackupSubdir,
                         ModelRel.Replace('/', Path.DirectorySeparatorChar));

        public string Record => Path.Combine(Root, "Proteus", HatCompatService.RecordFile);

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    /// <summary>Two meshes of one submesh each, which is enough to tag one and shape the other.</summary>
    private static byte[] Hair() => SyntheticModel.Build(
        ["atr_hv_a"],
        new SyntheticModel.Mesh("/mt_c0201h0001_hir_a.mtrl", new SyntheticModel.Sub(0)),
        new SyntheticModel.Mesh("/mt_c0201h0001_hir_b.mtrl", new SyntheticModel.Sub(0)));

    /// <summary>A proposal that presses one vertex and hides one submesh, built without the solve so the
    /// service is tested on its own rather than through the geometry.</summary>
    private static HatCompatService.Proposal Proposal(byte[] mdl, ModelParts parts)
    {
        var moved = new Dictionary<int, IReadOnlyDictionary<int, Vector3>>
        {
            [0] = new Dictionary<int, Vector3> { [1] = new(0.5f, 0.5f, 0.5f) },
        };
        var solve = new HatCompatSolve.Result(moved, Vector3.Zero, 0.075f, 0.004f, 0.01f, 1);
        return new HatCompatService.Proposal(ModelRel, parts, solve, false);
    }

    private static ModelPart PartOf(ModelParts parts, int mesh)
        => parts.Parts.First(p => p.Mesh == mesh && p.Island < 0);

    [Fact]
    public void WritesTheShapeAndTheScalpTag()
    {
        using var mod = new Mod(Hair());
        var mdl = mod.Model();
        var parts = ModelPartReader.Read(mdl)!;

        var outcome = HatCompatService.Apply(mod.Root, mdl, Proposal(mdl, parts), [PartOf(parts, 1)]);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(1, outcome.FilesPatched);

        var after = mod.Model();
        Assert.True(ModelAttributeWriter.DeclaresShape(after, HatCompatService.HatShape));

        var src = SecondSkinWriter.Parse(after);
        Assert.Contains(HatCompatService.ScalpAttribute, src.AttrNames);

        // The tag is on mesh 1's submesh and NOT on mesh 0's — the whole point of a hide list.
        int bit = Array.IndexOf(src.AttrNames, HatCompatService.ScalpAttribute);
        Assert.Equal(0u, MaskOf(after, src, 0) & (1u << bit));
        Assert.NotEqual(0u, MaskOf(after, src, 1) & (1u << bit));
    }

    private static uint MaskOf(byte[] mdl, SecondSkinWriter.Source src, int mesh)
    {
        int mo = src.MeshStart + mesh * 36;
        int sub = BitConverter.ToUInt16(mdl, mo + 10);
        return BitConverter.ToUInt32(mdl, src.SubmeshStart + sub * 16 + 8);
    }

    /// <summary>The undo has to be exact, not merely close — this is somebody else's mod.</summary>
    [Fact]
    public void RevertRestoresTheModelByteForByte()
    {
        using var mod = new Mod(Hair());
        var original = mod.Model();
        var parts = ModelPartReader.Read(original)!;

        Assert.True(HatCompatService.Apply(mod.Root, original, Proposal(original, parts),
                                           [PartOf(parts, 1)]).Ok);
        Assert.NotEqual(original, mod.Model());
        Assert.True(File.Exists(mod.Backup));
        Assert.True(File.Exists(mod.Record));

        var revert = HatCompatService.Revert(mod.Root);
        Assert.True(revert.Ok, revert.Message);
        Assert.Equal(original, mod.Model());
        Assert.False(File.Exists(mod.Record));
        Assert.False(File.Exists(mod.Backup));
    }

    /// <summary>
    /// A second patch must not overwrite the pristine backup with an already-patched file, or revert takes
    /// the mod back to the previous edit instead of to what its author shipped.
    /// </summary>
    [Fact]
    public void RevertUndoesEverySuccessivePatch()
    {
        using var mod = new Mod(Hair());
        var original = mod.Model();
        var parts = ModelPartReader.Read(original)!;

        Assert.True(HatCompatService.Apply(mod.Root, original, Proposal(original, parts), []).Ok);
        var once = mod.Model();

        // The second pass sees a model that already declares the shape, and refuses on that ground.
        var second = HatCompatService.Apply(mod.Root, once, Proposal(once, ModelPartReader.Read(once)!), []);
        Assert.False(second.Ok);

        Assert.True(HatCompatService.Revert(mod.Root).Ok);
        Assert.Equal(original, mod.Model());
    }

    [Fact]
    public void RefusesHairThatAlreadyHasAHatShape()
    {
        var mdl = SyntheticModel.Build(
            ["atr_hv_a"],
            [new SyntheticModel.Mesh("/mt_c0201h0001_hir_a.mtrl", new SyntheticModel.Sub(0))],
            SyntheticModel.V6, [HatCompatService.HatShape]);

        Assert.True(HatCompatService.IsHatCompatible(mdl));
        var proposal = HatCompatService.Inspect(mdl, ModelRel, null);
        Assert.NotNull(proposal);
        Assert.True(proposal!.AlreadyCompatible);

        using var mod = new Mod(mdl);
        var outcome = HatCompatService.Apply(mod.Root, mdl, proposal, []);
        Assert.False(outcome.Ok);
        Assert.Equal(mdl, mod.Model());       // and it did not touch the file
        Assert.False(File.Exists(mod.Record));
    }

    /// <summary>
    /// Undoing one hairstyle leaves the others in the same mod alone.
    /// <para/>
    /// A hair pack ships a dozen styles out of one folder, so a mod-wide undo triggered from the style you
    /// happen to be wearing would silently revert eleven you never asked about.
    /// </summary>
    [Fact]
    public void RevertingOneFileLeavesTheOthersPatched()
    {
        const string second = "hair/c0201h0002_hir.mdl";
        using var mod = new Mod(Hair());

        // A second hairstyle in the same folder, patched too.
        var dest = Path.Combine(mod.Root, second.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var originalTwo = Hair();
        File.WriteAllBytes(dest, originalTwo);

        var one = mod.Model();
        var parts = ModelPartReader.Read(one)!;
        Assert.True(HatCompatService.Apply(mod.Root, one, Proposal(one, parts), []).Ok);

        var proposalTwo = new HatCompatService.Proposal(
            second, parts, Proposal(originalTwo, parts).Solve, false);
        Assert.True(HatCompatService.Apply(mod.Root, originalTwo, proposalTwo, []).Ok);
        var patchedTwo = File.ReadAllBytes(dest);
        Assert.NotEqual(originalTwo, patchedTwo);

        var revert = HatCompatService.Revert(mod.Root, ModelRel);
        Assert.True(revert.Ok, revert.Message);
        Assert.Equal(1, revert.FilesPatched);
        Assert.Equal(one, mod.Model());                       // the one asked for is back
        Assert.Equal(patchedTwo, File.ReadAllBytes(dest));    // the other is untouched

        // The record survives, still naming the hairstyle that is still patched.
        var record = HatCompatService.ReadRecord(mod.Root);
        Assert.NotNull(record);
        Assert.Equal([second], record!.Files);

        Assert.True(HatCompatService.Revert(mod.Root, second).Ok);
        Assert.Equal(originalTwo, File.ReadAllBytes(dest));
        Assert.False(File.Exists(mod.Record));                // and now it has nothing left to describe
    }

    [Fact]
    public void RevertingAModItNeverTouchedSaysSo()
    {
        using var mod = new Mod(Hair());
        var outcome = HatCompatService.Revert(mod.Root);
        Assert.False(outcome.Ok);
        Assert.Equal(0, outcome.FilesPatched);
    }

    /// <summary>
    /// A refusal from the model editor must leave the mod untouched — every edit happens in memory and the
    /// file is only written once they have all succeeded.
    /// </summary>
    [Fact]
    public void AFailedEditWritesNothing()
    {
        using var mod = new Mod(Hair());
        var mdl = mod.Model();
        var parts = ModelPartReader.Read(mdl)!;

        // Aim the press at a mesh that is not in LOD0, which AddShape refuses.
        var moved = new Dictionary<int, IReadOnlyDictionary<int, Vector3>>
        {
            [9] = new Dictionary<int, Vector3> { [0] = Vector3.Zero },
        };
        var proposal = new HatCompatService.Proposal(
            ModelRel, parts,
            new HatCompatSolve.Result(moved, Vector3.Zero, 0, 0, 0, 1), false);

        var outcome = HatCompatService.Apply(mod.Root, mdl, proposal, []);
        Assert.False(outcome.Ok);
        Assert.Contains("Nothing has been written", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(mdl, mod.Model());
        Assert.False(File.Exists(mod.Backup));
        Assert.False(File.Exists(mod.Record));
    }

    // ── finding what to patch ───────────────────────────────────────────────

    private const string HairPath = "chara/human/c0201/obj/hair/h0104/model/c0201h0104_hir.mdl";
    private const string FacePath = "chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl";

    [Fact]
    public void FindsTheEquippedHairAndTheModThatSuppliesIt()
    {
        using var mod = new Mod(Hair());
        var modsRoot = Path.GetDirectoryName(mod.Root)!;
        var file = Path.Combine(mod.Root, ModelRel.Replace('/', Path.DirectorySeparatorChar));

        var target = HatCompatService.FindEquippedHair(
            [FacePath, HairPath], p => p == HairPath ? file : null, modsRoot);

        Assert.NotNull(target);
        Assert.Equal(HairPath, target!.GamePath);
        Assert.Equal(mod.Root, target.ModRoot);
        Assert.Equal(ModelRel, target.Rel);
        Assert.NotEmpty(target.Model);
        Assert.Null(target.Head);            // the face path resolved to nothing here
    }

    /// <summary>
    /// Vanilla hair resolves outside the mods folder — to the game's own data — and is not ours to edit.
    /// It also already has a hat shape, so there would be nothing to do even if it were.
    /// </summary>
    [Fact]
    public void IgnoresHairThatIsNotSuppliedByAMod()
    {
        using var mod = new Mod(Hair());
        var modsRoot = Path.GetDirectoryName(mod.Root)!;
        var elsewhere = Path.Combine(Path.GetTempPath(), "not_a_mod", "hair.mdl");

        Assert.Null(HatCompatService.FindEquippedHair([HairPath], _ => elsewhere, modsRoot));
        Assert.Null(HatCompatService.FindEquippedHair([HairPath], _ => null, modsRoot));
        Assert.Null(HatCompatService.FindEquippedHair([FacePath], _ => null, modsRoot));   // no hair at all
        Assert.Null(HatCompatService.FindEquippedHair(null, _ => null, modsRoot));
        Assert.Null(HatCompatService.FindEquippedHair([HairPath], _ => null, null));
    }

    /// <summary>
    /// The identity used to decide "is this still the same hairstyle" has to notice a MOD swap, not just a
    /// hairstyle swap.
    /// <para/>
    /// Right-clicking a hair mod in Penumbra leaves the game path identical and changes only which file
    /// serves it, so a key built from the path alone reports no change and the panel goes on describing
    /// the mod that was just switched away from — which is exactly what happened in game.
    /// </summary>
    [Fact]
    public void TheHairKeyNoticesADifferentModServingTheSamePath()
    {
        using var one = new Mod(Hair());
        using var two = new Mod(Hair());
        var modsRoot = Path.GetDirectoryName(one.Root)!;
        string File1 = Path.Combine(one.Root, ModelRel.Replace('/', Path.DirectorySeparatorChar));
        string File2 = Path.Combine(two.Root, ModelRel.Replace('/', Path.DirectorySeparatorChar));

        var keyOne = HatCompatService.EquippedHairKey([HairPath], _ => File1, modsRoot);
        var keyTwo = HatCompatService.EquippedHairKey([HairPath], _ => File2, modsRoot);

        Assert.NotNull(keyOne);
        Assert.NotNull(keyTwo);
        Assert.NotEqual(keyOne, keyTwo);        // same game path, different mod
        Assert.Equal(keyOne, HatCompatService.EquippedHairKey([HairPath], _ => File1, modsRoot));
    }

    /// <summary>And it notices the file being rewritten under it — which is how a patch is spotted.</summary>
    [Fact]
    public void TheHairKeyNoticesTheFileChanging()
    {
        using var mod = new Mod(Hair());
        var modsRoot = Path.GetDirectoryName(mod.Root)!;
        var file = Path.Combine(mod.Root, ModelRel.Replace('/', Path.DirectorySeparatorChar));

        var before = HatCompatService.EquippedHairKey([HairPath], _ => file, modsRoot);
        var bigger = Hair().Concat(new byte[64]).ToArray();
        File.WriteAllBytes(file, bigger);

        Assert.NotEqual(before, HatCompatService.EquippedHairKey([HairPath], _ => file, modsRoot));
    }

    [Fact]
    public void TheHairKeyIsNullWhenThereIsNothingToWatch()
    {
        using var mod = new Mod(Hair());
        var modsRoot = Path.GetDirectoryName(mod.Root)!;
        Assert.Null(HatCompatService.EquippedHairKey(null, _ => null, modsRoot));
        Assert.Null(HatCompatService.EquippedHairKey([HairPath], _ => null, modsRoot));
        Assert.Null(HatCompatService.EquippedHairKey([FacePath], _ => null, modsRoot));
    }

    /// <summary>
    /// The mod is the FIRST folder under the mods root, however deep the file sits — that is the unit a
    /// backup and a record belong to, and taking a deeper folder would scatter both.
    /// </summary>
    [Theory]
    [InlineData(@"C:\mods", @"C:\mods\Bob\hair\a.mdl", @"C:\mods\Bob", "hair/a.mdl")]
    [InlineData(@"C:\mods\", @"C:\mods\Bob\deep\er\a.mdl", @"C:\mods\Bob", "deep/er/a.mdl")]
    [InlineData(@"C:\mods", @"c:\MODS\Bob\a.mdl", @"C:\mods\Bob", "a.mdl")]
    public void SplitsAResolvedFileIntoModAndRelativePath(
        string root, string file, string expectMod, string expectRel)
    {
        Assert.True(HatCompatService.InMods(file, root, out var modRoot, out var rel));
        Assert.Equal(expectMod, modRoot, ignoreCase: true);
        Assert.Equal(expectRel, rel);
    }

    [Theory]
    [InlineData(@"C:\mods", @"C:\other\Bob\a.mdl")]      // outside the root
    [InlineData(@"C:\mods", @"C:\mods\loose.mdl")]       // directly in the root: no mod owns it
    [InlineData(@"C:\mods", @"C:\modsize\Bob\a.mdl")]    // a prefix match that is not a path prefix
    public void RejectsAFileNoModOwns(string root, string file)
        => Assert.False(HatCompatService.InMods(file, root, out _, out _));

    /// <summary>Inspect on ordinary hair proposes a press, and names the file it was asked about.</summary>
    [Fact]
    public void InspectProposesAPressForOrdinaryHair()
    {
        var mdl = Hair();
        var proposal = HatCompatService.Inspect(mdl, ModelRel, null);
        Assert.NotNull(proposal);
        Assert.False(proposal!.AlreadyCompatible);
        Assert.Equal(ModelRel, proposal.Rel);
        Assert.NotNull(proposal.Parts);
    }
}
