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
        public string Root { get; }

        /// <param name="parent">A mods root to put it in; the system temp folder when null.</param>
        public Mod(byte[] model, string? parent = null)
        {
            Root = Path.Combine(parent ?? Path.GetTempPath(), "proteus_hat_" + Path.GetRandomFileName());
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
            [FacePath, HairPath], p => p == HairPath ? file : null, modsRoot, _ => null);

        Assert.NotNull(target);
        Assert.Equal(HairPath, target!.GamePath);
        Assert.Equal(mod.Root, target.ModRoot);
        Assert.Equal(ModelRel, target.Rel);
        Assert.NotEmpty(target.Model);
        Assert.Null(target.Head);            // no game data behind this reader
    }

    /// <summary>
    /// The wearer's face comes from the GAME'S OWN DATA when no mod replaces it, and that is the whole
    /// difference between a measured skull and none.
    /// <para/>
    /// Penumbra hands back the game path unchanged for an unmodded file, which is what <c>resolve</c> does
    /// here. The old code required <c>File.Exists</c> on that, so every wearer with a vanilla face was
    /// fitted against a skull guessed from their hair — 66 mm low and nearly twice the radius, which tags
    /// most of a hairstyle to vanish under a hat. Nothing in the plugin reported it as anything but a
    /// working fit.
    /// </summary>
    [Fact]
    public void FindsTheWearersFaceInTheGameDataWhenNoModSuppliesIt()
    {
        using var mod = new Mod(Hair());
        var modsRoot = Path.GetDirectoryName(mod.Root)!;
        var file = Path.Combine(mod.Root, ModelRel.Replace('/', Path.DirectorySeparatorChar));
        var faceBytes = Hair();          // any bytes: this is about reaching them, not reading them

        var target = HatCompatService.FindEquippedHair(
            [FacePath, HairPath],
            // Exactly Penumbra's unmodded behaviour: the game path back, verbatim, not a file on disk.
            p => p == HairPath ? file : p,
            modsRoot,
            p => p == FacePath ? faceBytes : null);

        Assert.NotNull(target);
        Assert.Equal(faceBytes, target!.Head);
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

        Assert.Null(HatCompatService.FindEquippedHair([HairPath], _ => elsewhere, modsRoot, _ => null));
        Assert.Null(HatCompatService.FindEquippedHair([HairPath], _ => null, modsRoot, _ => null));
        Assert.Null(HatCompatService.FindEquippedHair([FacePath], _ => null, modsRoot, _ => null));  // no hair
        Assert.Null(HatCompatService.FindEquippedHair(null, _ => null, modsRoot, _ => null));
        Assert.Null(HatCompatService.FindEquippedHair([HairPath], _ => null, null, _ => null));
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

    /// <summary>
    /// Existing hat support stands when there is no head to judge it against.
    /// <para/>
    /// Pins the ORDER of the two refusals, which is not arbitrary. Proteus now replaces a hairstyle's own
    /// <c>atr_kam</c> mask when that mask measures as hiding hair no hat covers — but the measurement needs
    /// a hat line, so with no head there is no verdict, and a verdict is required to overrule an author.
    /// Refusing outright here would be the wrong refusal: it would report "Proteus could not find your
    /// head" for a hairstyle that works under hats perfectly well.
    /// </summary>
    [Fact]
    public void StandsDownForExistingHatSupportWhenTheHeadIsUnknown()
    {
        var mdl = SyntheticModel.Build(
            [HatCompatService.ScalpAttribute],
            [new SyntheticModel.Mesh("/mt_c0201h0001_hir_a.mtrl", new SyntheticModel.Sub(0))],
            SyntheticModel.V6, [HatCompatService.HatShape]);

        var proposal = HatCompatService.Inspect(mdl, ModelRel, head: null);

        Assert.NotNull(proposal);
        Assert.True(proposal!.AlreadyCompatible);
        Assert.False(proposal.TookOver);
        Assert.False(proposal.Unmeasurable);

        // And with no head the mask cannot be measured at all, which is not a verdict of any kind.
        Assert.Null(HatCompatSolve.MeasureScalpTagging(mdl, null, "0201"));
    }

    // ── one file, two spellings ─────────────────────────────────────────────

    /// <summary>
    /// A record written with backslashes still names the same file, because one file under two spellings is
    /// not two files.
    /// <para/>
    /// The spelling is not hypothetical: <c>InMods</c> normalises a resolved path to forward slashes while
    /// the mod manifest spells it with backslashes, so a record could carry both — and one in the wild
    /// does. Every comparison here is a plain string compare, so the backslash-recorded file reported as
    /// UNPATCHED, which meant Proteus found the shape it had written itself and credited it to the mod's
    /// author, and undo answered "there is nothing to undo" with the patch sitting on disk.
    /// </summary>
    [Fact]
    public void ARecordWrittenWithBackslashesStillNamesTheSameFile()
    {
        using var mod = new Mod(Hair());
        var original = mod.Model();
        var parts = ModelPartReader.Read(original)!;

        var outcome = HatCompatService.Apply(mod.Root, original, Proposal(original, parts),
                                             [PartOf(parts, 1)]);
        Assert.True(outcome.Ok);

        // Rewrite the record the way an older build could leave it: native separators throughout. Written
        // as a JSON escape (\\), which is how the real record in the wild spells it — a lone backslash is
        // not valid JSON and would make this test pass for the wrong reason, by failing to parse at all.
        var native = ModelRel.Replace("/", "\\\\");
        File.WriteAllText(mod.Record,
            "{\"Files\":[\"" + native + "\"],"
          + "\"Hidden\":{\"" + native + "\":[\"1.0\"]},"
          + "\"Versions\":{\"" + native + "\":" + HatCompatSolve.Version + "}}");

        Assert.True(HatCompatService.IsPatched(mod.Root, ModelRel, mod.Model(), out var stale));
        Assert.False(stale);

        // And undo reaches it, restores the author's bytes exactly, and retires the record — which only
        // happens if the Hidden and Versions removes matched the same key too.
        var undo = HatCompatService.Revert(mod.Root, ModelRel);
        Assert.True(undo.Ok);
        Assert.Equal(original, mod.Model());
        Assert.False(File.Exists(mod.Record));
    }

    /// <summary>
    /// Undo reaches every file the record names, including a race variant that is not the one being worn.
    /// <para/>
    /// Hair mods ship ONE MODEL PER RACE, each behind its own game path. Scoping the FIT to the worn path
    /// is right — each race is different geometry around a different skull — but undo used to inherit that
    /// scope, so fitting as one race and then changing to another stranded the first patch where nothing
    /// could name it.
    /// </summary>
    [Fact]
    public void RevertReachesEveryFileTheRecordNamesWhateverIsWorn()
    {
        const string otherRel  = "hair/c0801h0001_hir.mdl";
        const string otherPath = "chara/human/c0801/obj/hair/h0001/model/c0801h0001_hir.mdl";

        using var mod = new Mod(Hair());
        var original = mod.Model();

        // A second race variant in the same mod, at its own game path.
        var otherFile = Path.Combine(mod.Root, otherRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(otherFile)!);
        File.WriteAllBytes(otherFile, original);
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Bob\",\"Groups\":[],\"DefaultData\":{\"Files\":{"
          + $"\"{GamePath}\":\"{ModelRel}\",\"{otherPath}\":\"{otherRel}\"" + "}}}");

        var parts = ModelPartReader.Read(original)!;
        Assert.True(HatCompatService.Apply(mod.Root, original, Proposal(original, parts),
                                          [PartOf(parts, 1)]).Ok);
        Assert.True(HatCompatService.Apply(
            mod.Root, original,
            new HatCompatService.Proposal(otherRel, parts, Proposal(original, parts).Solve, false),
            [PartOf(parts, 1)]).Ok);

        Assert.Equal(2, HatCompatService.PatchedCount(mod.Root));

        // The fit stays scoped to the worn path — this is the behaviour being preserved, not a bug.
        var siblings = HatCompatService.SiblingFiles(mod.Root, GamePath, ModelRel);
        Assert.Equal([ModelRel], siblings);

        // Undo of everything is not so scoped.
        var undo = HatCompatService.Revert(mod.Root, only: null);
        Assert.True(undo.Ok);
        Assert.Equal(2, undo.FilesPatched);
        Assert.Equal(original, mod.Model());
        Assert.Equal(original, File.ReadAllBytes(otherFile));
        Assert.False(File.Exists(mod.Record));
    }

    // ── replacing an author's hat support ───────────────────────────────────

    /// <summary>Hair with its author's own hat support: a shp_hib pressing mesh 0, atr_kam on mesh 1.</summary>
    private static byte[] AuthoredHair()
    {
        var mdl = ModelAttributeWriter.AddShape(Hair(), HatCompatService.HatShape,
            new Dictionary<int, IReadOnlyDictionary<int, Vector3>>
            {
                [0] = new Dictionary<int, Vector3> { [2] = new(9f, 9f, 9f) },
            });
        var parts = ModelPartReader.Read(mdl)!;
        var (split, targets) = ModelAttributeWriter.IsolateParts(mdl, [PartOf(parts, 1)]);
        return ModelAttributeWriter.AddAttribute(split, HatCompatService.ScalpAttribute, targets);
    }

    [Fact]
    public void AuthoredHatSupportIsLeftAloneUnlessReplacingIsAsked()
    {
        var mdl = AuthoredHair();
        Assert.True(HatCompatService.Inspect(mdl, ModelRel, head: Hair())!.AlreadyCompatible);

        var replacing = HatCompatService.Inspect(mdl, ModelRel, head: Hair(), replaceAuthored: true)!;
        Assert.True(replacing.Replaced);
        Assert.False(replacing.AlreadyCompatible);

        // Replacing still needs a head to fit against; without one nothing is proposed.
        Assert.True(HatCompatService.Inspect(mdl, ModelRel, head: null, replaceAuthored: true)!.Unmeasurable);
    }

    /// <summary>
    /// Replacing discards the author's shape AND mask, so what is left is entirely Proteus's fit: one shp_hib (ours,
    /// on mesh 0 vertex 1, not the author's on vertex 2), and atr_kam only where Proteus cut. Undo gives back the
    /// author's file byte for byte.
    /// </summary>
    [Fact]
    public void ReplacingSwapsTheAuthorsShapeAndMaskForProteussAndUndoRestoresTheirs()
    {
        var authored = AuthoredHair();
        using var mod = new Mod(authored);
        var parts = ModelPartReader.Read(authored)!;
        var proposal = Proposal(authored, parts) with { Replaced = true };

        // Proteus tags nothing here, so the author's atr_kam on mesh 1 must be gone, not kept alongside ours.
        var outcome = HatCompatService.Apply(mod.Root, authored, proposal, []);
        Assert.True(outcome.Ok, outcome.Message);

        var after = mod.Model();
        var src = SecondSkinWriter.Parse(after);
        Assert.Equal(1, BitConverter.ToUInt16(after, src.Mh + 16));          // one shape, not two
        var entry = Assert.Single(src.Shapes[HatCompatService.HatShape]);
        var mesh0 = src.MeshStart;
        Assert.Equal(BitConverter.ToUInt32(after, mesh0 + 16), entry.MeshIndexOffset);
        int bit = Array.IndexOf(src.AttrNames, HatCompatService.ScalpAttribute);
        for (int m = 0; m < src.MeshCount; m++)
            Assert.Equal(0u, MaskOf(after, src, m) & (1u << bit));

        Assert.True(HatCompatService.IsPatched(mod.Root, ModelRel, mod.Model(), out _, out var replaced));
        Assert.True(replaced);

        var undo = HatCompatService.Revert(mod.Root, ModelRel);
        Assert.True(undo.Ok, undo.Message);
        Assert.Equal(authored, mod.Model());
        Assert.False(File.Exists(mod.Record));
    }

    /// <summary>
    /// Switching replacement off gives back the author's version on every hairstyle it replaced, in every mod —
    /// and touches nothing Proteus fitted on a hairstyle that had no hat support to begin with.
    /// </summary>
    [Fact]
    public void RestoringReplacedPutsBackOnlyTheAuthorsInEveryMod()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), "proteus_mods_" + Path.GetRandomFileName());
        Directory.CreateDirectory(modsRoot);
        try
        {
            var authored = AuthoredHair();
            using var replaced = new Mod(authored, modsRoot);
            var parts = ModelPartReader.Read(authored)!;
            Assert.True(HatCompatService.Apply(replaced.Root, authored,
                                               Proposal(authored, parts) with { Replaced = true }, []).Ok);

            var plain = Hair();
            using var fitted = new Mod(plain, modsRoot);
            Assert.True(HatCompatService.Apply(fitted.Root, plain, Proposal(plain, ModelPartReader.Read(plain)!),
                                               []).Ok);
            var fittedBytes = fitted.Model();

            var (restored, mods, failed) = HatCompatService.RestoreReplaced(modsRoot);

            Assert.Empty(failed);
            Assert.Equal(1, restored);
            Assert.Equal([replaced.Root], mods);
            Assert.Equal(authored, replaced.Model());               // the author's, byte for byte
            Assert.Equal(fittedBytes, fitted.Model());              // Proteus's own fit stays
            Assert.True(HatCompatService.IsPatched(fitted.Root, ModelRel, fitted.Model(), out _));

            // Nothing left to restore the second time.
            Assert.Equal(0, HatCompatService.RestoreReplaced(modsRoot).Restored);
        }
        finally { try { Directory.Delete(modsRoot, true); } catch { } }
    }

    /// <summary>
    /// With nothing of its own to put in place, Proteus keeps the author's: throwing theirs away for an empty fit
    /// would leave the hat clipping through hair the author had handled.
    /// </summary>
    [Fact]
    public void ReplacingWithNothingKeepsTheAuthors()
    {
        var authored = AuthoredHair();
        using var mod = new Mod(authored);
        var parts = ModelPartReader.Read(authored)!;
        var empty = new HatCompatService.Proposal(ModelRel, parts, HatCompatSolve.Result.None, false, Replaced: true);

        Assert.False(HatCompatService.Apply(mod.Root, authored, empty, []).Ok);
        Assert.Equal(authored, mod.Model());
        Assert.False(File.Exists(mod.Record));
    }

    /// <summary>Inspect on ordinary hair proposes a press, and names the file it was asked about.</summary>
    [Fact]
    public void InspectProposesAPressForOrdinaryHair()
    {
        var mdl = Hair();
        var proposal = HatCompatService.Inspect(mdl, ModelRel, head: Hair());
        Assert.NotNull(proposal);
        Assert.False(proposal!.AlreadyCompatible);
        Assert.False(proposal.Unmeasurable);
        Assert.Equal(ModelRel, proposal.Rel);
        Assert.NotNull(proposal.Parts);
    }

    /// <summary>
    /// Without the wearer's head there is no hat line, and Proteus does nothing at all.
    /// <para/>
    /// The skull used to be guessed from the hair here. That guess measured the hairstyle's own extent,
    /// tails included, so it put the centre 66 mm low and the radius at nearly double — and the cut plane
    /// derived from it took the crown, the sides and the fringe. Refusing leaves a hat clipping through
    /// hair, which is the smaller harm by a wide margin and the one the wearer can actually see.
    /// </summary>
    [Fact]
    public void InspectRefusesWhenTheWearersHeadIsUnknown()
    {
        var mdl = Hair();
        var proposal = HatCompatService.Inspect(mdl, ModelRel, head: null);

        Assert.NotNull(proposal);
        Assert.True(proposal!.Unmeasurable);
        Assert.False(proposal.AlreadyCompatible);
        Assert.Empty(proposal.Solve.Cut);
        Assert.Empty(proposal.Solve.Moved);

        // And it must refuse to write, not merely propose nothing — Apply is reachable from the panel's
        // button and from the sibling loop without going back through Inspect's gate.
        using var mod = new Mod(mdl);
        var outcome = HatCompatService.Apply(mod.Root, mdl, proposal, []);
        Assert.False(outcome.Ok);
        Assert.Equal(mdl, mod.Model());
        Assert.False(File.Exists(mod.Backup));
        Assert.False(File.Exists(mod.Record));
    }

    // ── version 33: the ear skin ─────────────────────────────────────────────

    /// <summary>A hair that names a Miqo'te ear bone, so its output changed in version 33.</summary>
    private static byte[] MiqoteHair() => SyntheticModel.Build(
        ["atr_hv_a"],
        new SyntheticModel.Mesh("/mt_c0801h0001_hir_a.mtrl", new SyntheticModel.Sub(0, Weights: [("j_kao", 1f)])),
        new SyntheticModel.Mesh("/mt_c0801h0001_hir_b.mtrl", new SyntheticModel.Sub(0, Weights: [("j_mimi_l", 1f)])));

    /// <summary>Fit, then stamp the record as if an older build had: the version only.</summary>
    private static Mod FittedAt(byte[] hair, int version)
    {
        var mod = new Mod(hair);
        var parts = ModelPartReader.Read(hair)!;
        Assert.True(HatCompatService.Apply(mod.Root, hair, Proposal(hair, parts), [PartOf(parts, 0)]).Ok);
        File.WriteAllText(mod.Record, File.ReadAllText(mod.Record).Replace(
            $": {HatCompatSolve.Version}", $": {version}"));
        return mod;
    }

    /// <summary>A Miqo'te hair patched before the ear-skin fix is refit the next time it is worn.</summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-5")]
    public void AMiqoteHairPatchedBeforeTheEarSkinFixIsStale()
    {
        using var mod = FittedAt(MiqoteHair(), HatCompatSolve.WithoutEars);
        Assert.True(HatCompatService.IsPatched(mod.Root, ModelRel, mod.Model(), out var stale));
        Assert.True(stale);
    }

    /// <summary>No other hairstyle's output changed, so its patch is not refit for nothing.</summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-4")]
    [Trait("Plan", "AC-miqote-ear-skin-5")]
    public void AnotherRacesPatchFromBeforeTheEarSkinFixStaysCurrent()
    {
        using var mod = FittedAt(Hair(), HatCompatSolve.WithoutEars);
        Assert.True(HatCompatService.IsPatched(mod.Root, ModelRel, mod.Model(), out var stale));
        Assert.False(stale);
    }

    /// <summary>
    /// An author's update replaced the fitted file. Restoring the backup would put the OLD file back over it; the
    /// patch is let go instead, and the new file kept as found.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-5")]
    [Trait("Plan", "EDGE-miqote-ear-skin-8")]
    public void AFileReplacedSinceItWasFittedIsForgottenNotRestored()
    {
        using var mod = FittedAt(MiqoteHair(), HatCompatSolve.WithoutEars);
        Assert.True(HatCompatService.LooksPatched(mod.Model()));

        var update = MiqoteHair();
        File.WriteAllBytes(Path.Combine(mod.Root, ModelRel.Replace('/', Path.DirectorySeparatorChar)), update);
        Assert.False(HatCompatService.LooksPatched(update));

        Assert.Null(HatCompatService.Forget(mod.Root, ModelRel));
        Assert.False(HatCompatService.IsPatched(mod.Root, ModelRel, update, out _));
        Assert.False(File.Exists(mod.Backup));
        Assert.Equal(update, mod.Model());
    }

    private const string SiblingRel = "hair/option b/c0201h0001_hir.mdl";

    /// <summary>Fit a second option of the same hairstyle into <paramref name="mod"/>, as the watcher fits siblings.</summary>
    private static void FitSibling(Mod mod, byte[] hair)
    {
        var path = Path.Combine(mod.Root, SiblingRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, hair);
        var parts = ModelPartReader.Read(hair)!;
        var proposal = Proposal(hair, parts) with { Rel = SiblingRel };
        Assert.True(HatCompatService.Apply(mod.Root, hair, proposal, [PartOf(parts, 0)]).Ok);
    }

    /// <summary>
    /// Each option is judged on its own: the one its author updated since keeps the update — its old backup let go, never
    /// restored over it — while the one still fitted comes back as the author shipped it.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-5")]
    [Trait("Plan", "AC-miqote-ear-skin-6")]
    [Trait("Plan", "EDGE-miqote-ear-skin-7")]
    public void UndoJudgesEverySiblingOnItsOwn()
    {
        var original = MiqoteHair();
        using var mod = FittedAt(original, HatCompatSolve.WithoutEars);
        FitSibling(mod, original);

        var update = SyntheticModel.Build(["atr_hv_b"],
            new SyntheticModel.Mesh("/mt_c0801h0001_hir_a.mtrl", new SyntheticModel.Sub(0, Weights: [("j_mimi_l", 1f)])));
        var siblingPath = Path.Combine(mod.Root, SiblingRel.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(siblingPath, update);

        Assert.Equal(HatCompatService.Undone.Restored, HatCompatService.RevertOrForget(mod.Root, ModelRel, out _));
        Assert.Equal(original, mod.Model());
        Assert.Equal(HatCompatService.Undone.Forgotten, HatCompatService.RevertOrForget(mod.Root, SiblingRel, out _));
        Assert.Equal(update, File.ReadAllBytes(siblingPath));
        Assert.False(File.Exists(mod.Record));
        Assert.Equal(HatCompatService.Undone.Nothing, HatCompatService.RevertOrForget(mod.Root, SiblingRel, out _));
    }

    /// <summary>Letting one file go keeps the record for the others.</summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-5")]
    public void ForgettingOneFileKeepsTheOthersPatched()
    {
        var hair = MiqoteHair();
        using var mod = FittedAt(hair, HatCompatSolve.Version);
        FitSibling(mod, hair);

        Assert.Null(HatCompatService.Forget(mod.Root, SiblingRel));
        Assert.True(HatCompatService.IsPatched(mod.Root, ModelRel, mod.Model(), out _));
        Assert.True(File.Exists(mod.Backup));
        Assert.DoesNotContain(SiblingRel, HatCompatService.ReadRecord(mod.Root)!.Files);
    }

    /// <summary>
    /// A backup that cannot be deleted is the OLD file: a fit after it would keep it and the next undo restore it. So
    /// the failure is reported, the record keeps naming the file, and nothing is half done.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-5")]
    public void AForgetThatCannotDeleteTheBackupSaysSoAndChangesNothing()
    {
        using var mod = FittedAt(MiqoteHair(), HatCompatSolve.WithoutEars);
        string? why;
        using (File.Open(mod.Backup, FileMode.Open, FileAccess.Read, FileShare.None))
            why = HatCompatService.Forget(mod.Root, ModelRel);
        Assert.NotNull(why);
        Assert.True(File.Exists(mod.Backup));
        Assert.True(HatCompatService.IsPatched(mod.Root, ModelRel, mod.Model(), out _));
    }

    /// <summary>
    /// With auto-fit off nothing refits a stale patch: the watcher's gate refits only when the hairstyle is stale, may be
    /// fitted, a hat is worn, and both the plugin and auto-fit are on. Pinned by reading the source, since the watcher
    /// cannot be built in a test.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-miqote-ear-skin-8")]
    public void AStalePatchIsRefitOnlyWhenAutoFitIsOn()
    {
        var source = File.ReadAllText(Path.Combine(SourceDir(), "Proteus", "Services", "HatCompatWatcher.cs"));
        Assert.Contains("if (!(stale && mayApply && hatWorn && config.PluginEnabled && config.AutoHatCompat))", source);
    }

    /// <summary>
    /// A stale refit leaves its reload to the fit; when no fit follows (the face unreadable, the hairstyle compatible
    /// after all), the examination still reloads, or the game keeps drawing a patch no longer on disk.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-5")]
    public void AStaleRefitsUndoIsReloadedEvenWhenNoFitFollows()
    {
        var source = File.ReadAllText(Path.Combine(SourceDir(), "Proteus", "Services", "HatCompatWatcher.cs"));
        int stale = source.IndexOf("RevertFiles(target, redraw: false)", StringComparison.Ordinal);
        Assert.True(stale >= 0);
        Assert.Contains("reloadOwed = target;", source[stale..(stale + 600)]);
        var examine = source[source.IndexOf("private void Examine(bool mayApply)", StringComparison.Ordinal)..];
        examine = examine[..examine.IndexOf("private void ExamineCore(", StringComparison.Ordinal)];
        Assert.Contains("finally", examine);
        Assert.Contains("ReloadAndRedraw(owed)", examine);
    }

    /// <summary>
    /// An ordinary refusal — a sibling whose backup is gone, or one another feature edited later — leaves that file as
    /// it is and is reported, so the rest of the hairstyle can still be undone; only a backup that cannot be let go stops
    /// everything.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-6")]
    public void ARefusedFileIsReportedAndLeftAsItIs()
    {
        using var mod = FittedAt(MiqoteHair(), HatCompatSolve.Version);
        var patched = mod.Model();
        File.Delete(mod.Backup);
        Assert.Equal(HatCompatService.Undone.Refused,
                     HatCompatService.RevertOrForget(mod.Root, ModelRel, out var message));
        Assert.NotEmpty(message);
        Assert.Equal(patched, mod.Model());
    }

    /// <summary>
    /// Undo carries on past a refused sibling, so the rest of the hairstyle is still undone and the undo sticks: the
    /// watcher's <c>Refused</c> case notes it and goes on. Pinned by reading the source, since the watcher cannot be
    /// built in a test.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-6")]
    public void UndoCarriesOnPastARefusedSibling()
    {
        var source = File.ReadAllText(Path.Combine(SourceDir(), "Proteus", "Services", "HatCompatWatcher.cs"));
        var revertFiles = source[source.IndexOf("private bool RevertFiles(", StringComparison.Ordinal)..];
        revertFiles = revertFiles[..revertFiles.IndexOf("private bool RevertWholeMod(", StringComparison.Ordinal)];
        int refused = revertFiles.IndexOf("case HatCompatService.Undone.Refused:", StringComparison.Ordinal);
        Assert.True(refused >= 0);
        var arm = revertFiles[refused..];
        arm = arm[..arm.IndexOf("case HatCompatService.Undone.", "case ".Length, StringComparison.Ordinal)];
        Assert.DoesNotContain("return", arm);
        Assert.Contains("break;", arm);
    }

    /// <summary>Undo-all restores every file and lets the record go.</summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-6")]
    public void UndoAllRestoresEveryFile()
    {
        var hair = MiqoteHair();
        using var mod = FittedAt(hair, HatCompatSolve.Version);
        FitSibling(mod, hair);

        var undo = HatCompatService.RevertOrForgetAll(mod.Root);
        Assert.True(undo.Ok);
        Assert.Equal(2, undo.Restored);
        Assert.Equal(hair, mod.Model());
        Assert.Equal(hair, File.ReadAllBytes(Path.Combine(mod.Root, SiblingRel.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(mod.Record));
    }

    /// <summary>
    /// A later feature (here the brush) holding its own backup of ANY file blocks the whole undo before a single file is
    /// touched: restoring ours would silently undo that edit too.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-6")]
    public void UndoAllTouchesNothingWhileALaterEditHoldsAnyFile()
    {
        var hair = MiqoteHair();
        using var mod = FittedAt(hair, HatCompatSolve.Version);
        FitSibling(mod, hair);
        var patched = mod.Model();
        var siblingPath = Path.Combine(mod.Root, SiblingRel.Replace('/', Path.DirectorySeparatorChar));
        var patchedSibling = File.ReadAllBytes(siblingPath);

        var brushBackup = Path.Combine(mod.Root, "Proteus", MeshVolumeService.BackupSubdir,
                                       SiblingRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(brushBackup)!);
        File.WriteAllBytes(brushBackup, patchedSibling);
        var ours = Path.Combine(mod.Root, "Proteus", HatCompatService.BackupSubdir,
                                SiblingRel.Replace('/', Path.DirectorySeparatorChar));
        File.SetLastWriteTimeUtc(brushBackup, File.GetLastWriteTimeUtc(ours).AddMinutes(1));

        var undo = HatCompatService.RevertOrForgetAll(mod.Root);
        Assert.False(undo.Ok);
        Assert.False(undo.Failed);
        Assert.NotEmpty(undo.Message);
        Assert.Equal(0, undo.Restored);
        Assert.Equal(patched, mod.Model());
        Assert.Equal(patchedSibling, File.ReadAllBytes(siblingPath));
        Assert.Equal(2, HatCompatService.ReadRecord(mod.Root)!.Files.Count);
    }

    /// <summary>A file whose backup is gone is left and reported; the others are still undone.</summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-6")]
    public void UndoAllLeavesAFileWithoutABackupAndUndoesTheRest()
    {
        var hair = MiqoteHair();
        using var mod = FittedAt(hair, HatCompatSolve.Version);
        FitSibling(mod, hair);
        var siblingPath = Path.Combine(mod.Root, SiblingRel.Replace('/', Path.DirectorySeparatorChar));
        var patchedSibling = File.ReadAllBytes(siblingPath);
        File.Delete(Path.Combine(mod.Root, "Proteus", HatCompatService.BackupSubdir,
                                 SiblingRel.Replace('/', Path.DirectorySeparatorChar)));

        var undo = HatCompatService.RevertOrForgetAll(mod.Root);
        Assert.True(undo.Ok);
        Assert.Equal(1, undo.Restored);
        Assert.Equal(hair, mod.Model());
        Assert.Equal(patchedSibling, File.ReadAllBytes(siblingPath));
    }

    /// <summary>Nothing recorded: a plain "nothing to undo", not a failure.</summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-6")]
    public void UndoAllWithNothingRecordedSaysSo()
    {
        using var mod = new Mod(MiqoteHair());
        var undo = HatCompatService.RevertOrForgetAll(mod.Root);
        Assert.False(undo.Ok);
        Assert.False(undo.Failed);
        Assert.NotEmpty(undo.Message);
    }

    /// <summary>A Miqo'te hair whose attribute table is full refuses the fit cleanly, writing nothing.</summary>
    [Fact]
    [Trait("Plan", "EDGE-miqote-ear-skin-12")]
    public void AFullAttributeTableStillRefusesCleanly()
    {
        var hair = SyntheticModel.Build(
            Enumerable.Range(0, 32).Select(i => $"atr_x{i:D2}").ToArray(),
            new SyntheticModel.Mesh("/mt_c0801h0001_hir_a.mtrl", new SyntheticModel.Sub(0, Weights: [("j_kao", 1f)])),
            new SyntheticModel.Mesh("/mt_c0801h0001_hir_b.mtrl", new SyntheticModel.Sub(0, Weights: [("j_mimi_l", 1f)])));
        using var mod = new Mod(hair);
        var parts = ModelPartReader.Read(hair)!;
        var outcome = HatCompatService.Apply(mod.Root, hair, Proposal(hair, parts), [PartOf(parts, 0)]);
        Assert.False(outcome.Ok);
        Assert.Equal(hair, mod.Model());
        Assert.False(File.Exists(mod.Backup));
        Assert.False(File.Exists(mod.Record));
    }

    /// <summary>
    /// Undo restores the file on disk, and the game must then load it: Penumbra caches a model per resolved path, so a
    /// redraw without a reload kept drawing the patched hairstyle. Every way back reloads the mod first.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-6")]
    public void EveryUndoReloadsTheModBeforeItRedraws()
    {
        var source = File.ReadAllText(Path.Combine(SourceDir(), "Proteus", "Services", "HatCompatWatcher.cs"));
        foreach (var method in new[] { "private bool RevertFiles(", "private bool RevertWholeMod(" })
        {
            int at = source.IndexOf(method, StringComparison.Ordinal);
            Assert.True(at >= 0, method);
            int end = source.IndexOf("\n    }", at, StringComparison.Ordinal);
            var body = source[at..end];
            Assert.Contains("ReloadAndRedraw(target)", body);
            Assert.DoesNotContain("compositor.RedrawForChangedModel(", body);
        }
        var reload = source[source.IndexOf("private void ReloadAndRedraw(", StringComparison.Ordinal)..];
        Assert.True(reload.IndexOf("ReloadModDirectory(", StringComparison.Ordinal)
                  < reload.IndexOf("RedrawForChangedModel(", StringComparison.Ordinal));
    }

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));
}
