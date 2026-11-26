using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Proteus.Services;
using Proteus.Services.Bulge;
using Xunit;

namespace Proteus.Tests;

/// <summary>The featureless bulge through <see cref="BodyRetarget.Plan"/> on the synthetic TBSE-like body (CI).</summary>
public partial class BulgeRefitTests
{
    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    internal const string Cloth = "/mt_c0101e0600_dwn_a.mtrl";
    internal const string Pouch = "/mt_c0101e0600_dwn_b.mtrl";
    internal const string Benis = "/mt_c0101b0001_benis.mtrl";

    /// <summary>
    /// A garment over the synthetic body: cloth 5 mm off the parts <paramref name="keep"/> chooses (by label), as one mesh;
    /// with <paramref name="genitalMaterial"/> a second mesh on a body mod's genital material over the dome, and with
    /// <paramref name="pouch"/> the same on the garment's own material; with <paramref name="genitalSkin"/> its own skin
    /// over the dome (a garment made on a body with a penis, carrying it).
    /// </summary>
    /// <param name="switchedSkin">The garment's own skin as Beach Briefs carries it (plan genital-switches): one skin mesh of
    /// the main skin (<c>1.1</c>, untagged) and a copy of the dome per tag set, each its own submesh tagged with that set.</param>
    /// <param name="cap">With <paramref name="switchedSkin"/>, a fan over the hole in the same mesh, tagged so (Beach Briefs'
    /// <c>0.7</c>: shown only with the briefs).</param>
    /// <param name="clothTags">Tags on the cloth's submesh (Beach Briefs' briefs, <c>atr_dv_g</c>).</param>
    /// <param name="untaggedPenis">With <paramref name="switchedSkin"/>, one more copy of the dome, untagged.</param>
    /// <param name="mergedUntaggedPenis">With <paramref name="switchedSkin"/>, the dome in the main skin's own submesh too.</param>
    /// <param name="switchedPastWindow">With <paramref name="switchedSkin"/>, one more tagged piece: the dome and the main skin.</param>
    /// <param name="liftedSwitched">With <paramref name="switchedSkin"/>, the main skin of that mesh lifted 2 cm off the body (less
    /// than a penis stands, more than a laid skin misses by)
    /// (a mesh the rebuild keeps).</param>
    /// <param name="kneeSkin">A skin mesh tagged <c>atr_hiz</c> 60 cm below the crotch, and no other skin.</param>
    /// <param name="setTaggedPiercing">A body piercing over the dome, tagged so.</param>
    internal static byte[] Garment(byte[] body, Func<ModelPart, bool> keep, bool genitalMaterial = false, bool pouch = false,
                                   bool genitalSkin = false, Func<Vector3, bool>? where = null, bool pubes = false,
                                   bool liftedSkin = false, IReadOnlyList<string[]>? switchedSkin = null, string[]? cap = null,
                                   string[]? clothTags = null, bool untaggedPenis = false, bool mergedUntaggedPenis = false,
                                   bool switchedPastWindow = false, bool liftedSwitched = false, bool kneeSkin = false,
                                   string[]? setTaggedPiercing = null)
    {
        var parts = ModelPartReader.Read(body)!;
        var skin = ModelSkinReader.Read(body, null, null)!;
        SyntheticModel.SurfaceMesh MeshOf(string material, IEnumerable<ModelPart> from, float off)
        {
            var map = new Dictionary<int, int>();
            var tris = new List<int>();
            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var w = new List<(string, float)[]?>();
            foreach (var part in from)
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    if (where != null && !Enumerable.Range(0, 3).All(k => where(At(parts, part.Triangles[t + k])))) continue;
                    for (int k = 0; k < 3; k++)
                    {
                    int v = part.Triangles[t + k];
                    if (!map.TryGetValue(v, out int o))
                    {
                        map[v] = o = pos.Count;
                        var n = new Vector3(parts.Normals[v * 3], parts.Normals[v * 3 + 1], parts.Normals[v * 3 + 2]);
                        pos.Add(At(parts, v) + n * off);
                        nrm.Add(n);
                        w.Add(Enumerable.Range(0, 4).Where(k => skin.BoneWeights[v * 8 + k] > 0f)
                                        .Select(k => (skin.BoneNames[skin.BoneIndices[v * 8 + k]], skin.BoneWeights[v * 8 + k])).ToArray());
                    }
                    tris.Add(o);
                    }
                }
            var pa = pos.ToArray();
            return new SyntheticModel.SurfaceMesh(material, pa, [.. nrm], pa.Select(p => new Vector2(p.X + 0.5f, p.Y)).ToArray(), [.. w],
                                                  [(0u, tris.ToArray())]);
        }
        var dome = parts.Parts.Where(p => p.Island < 0 && p.Label == "1.2").ToList();
        var names = new List<string>();
        uint Mask(IEnumerable<string>? tags)
        {
            uint m = 0;
            foreach (string t in tags ?? [])
            {
                int i = names.IndexOf(t);
                if (i < 0) { i = names.Count; names.Add(t); }
                m |= 1u << i;
            }
            return m;
        }
        var clothMesh = MeshOf(Cloth, parts.Parts.Where(p => p.Island < 0 && keep(p)), 0.005f);
        if (clothTags != null) clothMesh = clothMesh with { Submeshes = [(Mask(clothTags), clothMesh.Submeshes[0].Triangles)] };
        var meshes = new List<SyntheticModel.SurfaceMesh> { clothMesh };
        where = null;   // the extras sit over the dome whatever the cloth's band
        if (switchedSkin != null)
        {
            var main = MeshOf(SyntheticBulge.Skin, parts.Parts.Where(p => p.Island < 0 && p.Label == "1.1"), liftedSwitched ? 0.02f : 0f);
            var domeMesh = MeshOf(SyntheticBulge.Skin, dome, 0f);
            int n0 = main.Positions.Length;
            var domeTris = domeMesh.Submeshes[0].Triangles.Select(i => i + n0).ToArray();
            var subs = new List<(uint, int[])> { (0u, mergedUntaggedPenis ? [.. main.Submeshes[0].Triangles, .. domeTris] : main.Submeshes[0].Triangles) };
            foreach (var set in switchedSkin) subs.Add((Mask(set), domeTris));
            if (untaggedPenis) subs.Add((0u, domeTris));
            if (switchedPastWindow) subs.Add((Mask(["atr_dv_h", "atr_dv_j"]), [.. domeTris, .. main.Submeshes[0].Triangles]));
            if (cap != null)
            {
                // The hole's points in the dome mesh, in the hole's order, fanned from the first.
                var hole = TbseBulge.Shared!.Hole;
                var at = hole.Select(h => Array.FindIndex(domeMesh.Positions, p => Vector3.Distance(p, h) < 1e-5f) + n0).ToArray();
                var fan = new List<int>();
                for (int i = 1; i + 1 < at.Length; i++) fan.AddRange([at[0], at[i + 1], at[i]]);
                subs.Add((Mask(cap), fan.ToArray()));
            }
            meshes.Add(new SyntheticModel.SurfaceMesh(SyntheticBulge.Skin, [.. main.Positions, .. domeMesh.Positions],
                [.. main.Normals, .. domeMesh.Normals], [.. main.Uv, .. domeMesh.Uv], [.. main.Weights, .. domeMesh.Weights], [.. subs]));
        }
        if (genitalMaterial) meshes.Add(MeshOf(Benis, dome, 0.0005f));
        if (pouch) meshes.Add(MeshOf(Pouch, dome, 0.008f));
        if (genitalSkin && !liftedSkin) meshes.Add(MeshOf(SyntheticBulge.Skin, dome, 0f));
        if (genitalSkin && liftedSkin)
        {
            // One skin mesh: the dome on the body, and the main skin lifted 4 cm off it.
            var on = MeshOf(SyntheticBulge.Skin, dome, 0f);
            var off = MeshOf(SyntheticBulge.Skin, parts.Parts.Where(p => p.Island < 0 && p.Label == "1.1"), 0.04f);
            int n0 = on.Positions.Length;
            meshes.Add(new SyntheticModel.SurfaceMesh(SyntheticBulge.Skin, [.. on.Positions, .. off.Positions], [.. on.Normals, .. off.Normals],
                [.. on.Uv, .. off.Uv], [.. on.Weights, .. off.Weights],
                [(0u, [.. on.Submeshes[0].Triangles, .. off.Submeshes[0].Triangles.Select(i => i + n0)])]));
        }
        if (pubes) meshes.Add(MeshOf("/mt_c0101b0001_bibopube.mtrl", dome, 0.003f));
        if (kneeSkin)
        {
            var knee = MeshOf(SyntheticBulge.Skin, parts.Parts.Where(p => p.Island < 0 && p.Label == "1.1"), 0f);
            meshes.Add(knee with { Positions = [.. knee.Positions.Select(p => p - new Vector3(0, 0.6f, 0))],
                                   Submeshes = [(Mask(["atr_hiz"]), knee.Submeshes[0].Triangles)] });
        }
        if (setTaggedPiercing != null)
        {
            var ring = MeshOf("/mt_c0101b0001_genitalpierce.mtrl", dome, 0.002f);
            meshes.Add(ring with { Submeshes = [(Mask(setTaggedPiercing), ring.Submeshes[0].Triangles)] });
        }
        return SyntheticModel.BuildSurface(names, [.. meshes]);
    }

    /// <summary>The made-on body: the synthetic body with its dome a little taller, so the two bodies are two files.</summary>
    internal static byte[] Source() => SyntheticBulge.Body(height: 0.065f);

    /// <summary>The pair from <paramref name="source"/> onto <paramref name="body"/> (same mesh: identity), with its
    /// featureless factory as BuildPair attaches it.</summary>
    internal static BodyRetarget.SlotPair Pair(byte[] source, byte[] body, Func<(BodyRetarget.SlotPair?, string)>? factory = null)
    {
        var sourceParts = ModelPartReader.Read(source)!;
        var parts = ModelPartReader.Read(body)!;
        Assert.True(IdentityCorrespondence.TryBuild(sourceParts, parts, "_dwn", out var corr, out string why), why);
        var plain = new BodyRetarget.SlotPair("_dwn", corr!, parts, body, source);
        return plain with
        {
            Featureless = factory ?? (() =>
            {
                var swapped = BulgeSwap.Featureless(body, TbseBulge.Shared!, out string reason);
                if (swapped == null) return (null, reason);
                var f = ModelPartReader.Read(swapped.Model)!;
                // As BuildPair's factory does: the correspondence built again by uv, the bulge's landing uvs standing in
                // for its own — here the synthetic body's own rule, (x + 0.5, y).
                var uvF = BodyRetarget.Uv(swapped.Model);
                foreach (int slot in swapped.SlotOf) { uvF[slot * 2] = f.Positions[slot * 3] + 0.5f; uvF[slot * 2 + 1] = f.Positions[slot * 3 + 1]; }
                if (!BodyCorrespondence.TryBuild(sourceParts, BodyRetarget.Uv(source), f, uvF, "_dwn", out var fc, out string r, out _, null, true, true))
                    return (null, r);
                return (plain with { Correspondence = fc!, Target = f, TargetModel = swapped.Model }, "");
            }),
        };
    }

    private static (BodyRetarget.Planned Plan, List<string> Log) Run(byte[] garment, BodyRetarget.SlotPair pair, bool replaceSkin = true,
                                                                    bool noBulge = false)
    {
        var log = new List<string>();
        var planned = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoBulge: noBulge), () =>
            BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [pair], "_dwn", replaceSkin: replaceSkin,
                              acrossBodies: false, log: log.Add));
        return (planned, log);
    }

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    [Fact]
    [Trait("Plan", "AC-featureless-bulge-1")]
    [Trait("Plan", "EDGE-featureless-bulge-10")]
    public void Covering_garment_goes_featureless()
    {
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, _ => true);
        var (plan, log) = Run(garment, Pair(source, body));
        Assert.Contains(log, l => l.StartsWith("bulge: _dwn featureless", StringComparison.Ordinal));
        // The cloth over the crotch is fitted to the bulge: all of it within 20 mm of the bulge — and today's, fitted to the
        // dome, is not.
        var bulge = TbseBulge.Shared!.Positions;
        // The cloth points that sat over the dome (within 8 mm of it), followed through the solve's in-place edit.
        var bodyParts = ModelPartReader.Read(source)!;
        var dome = bodyParts.Parts.Single(p => p.Island < 0 && p.Label == "1.2").Triangles.Distinct().Select(v => At(bodyParts, v)).ToList();
        var gParts = ModelPartReader.Read(garment)!;
        var overDome = gParts.Parts.Where(p => p.Island < 0 && p.Material == Cloth).SelectMany(p => p.Triangles).Distinct()
                             .Where(v => dome.Min(d => Vector3.Distance(d, At(gParts, v))) < 0.008f).ToList();
        Assert.NotEmpty(overDome);
        float Off(BodyRetarget.Planned planned)
        {
            var moved = ModelPartReader.Read(MeshVolumeService.Inflate((byte[])garment.Clone(), planned.Edit).Model)!;
            return overDome.Max(v => bulge.Min(b => Vector3.Distance(b, At(moved, v))));
        }
        var (plain, _) = Run(garment, Pair(source, body), noBulge: true);
        Assert.True(Off(plan) < 0.020f, $"cloth stands {Off(plan) * 1000f:0} mm off the bulge; log: {string.Join(" | ", log)}");
        Assert.True(Off(plain) > 0.020f, $"today's cloth already sat on the bulge ({Off(plain) * 1000f:0} mm): the test proves nothing");
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-3")]
    [Trait("Plan", "AC-featureless-bulge-7")]
    public void Crotchless_garment_is_today()
    {
        var body = SyntheticBulge.Body();
        var source = Source();
        var hole = TbseBulge.Shared!.Hole;
        var centre = hole.Aggregate(Vector3.Zero, (s, p) => s + p) / hole.Length;
        // The main skin only, and only its outer band (beyond twice the hole's radius): nothing over the dome.
        float radius = hole.Max(p => Vector3.Distance(p, centre));
        var garment = Garment(source, p => p.Label == "1.1", where: p => Vector3.Distance(p, centre) >= 2f * radius);
        var (plan, log) = Run(garment, Pair(source, body));
        var (plain, _) = Run(garment, Pair(source, body), noBulge: true);
        Assert.Contains(log, l => l.Contains("crotch not covered", StringComparison.Ordinal));
        Assert.Equal(Hash(plain.Model), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-24")]
    public void No_bulge_is_todays_refit()
    {
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, _ => true);
        var (a, _) = Run(garment, Pair(source, body), noBulge: true);
        var plainPair = Pair(source, body) with { Featureless = null };
        var (b, _) = Run(garment, plainPair);
        Assert.Equal(Hash(b.Model), Hash(a.Model));
    }

    [Fact]
    [Trait("Plan", "AC-featureless-bulge-5")]
    [Trait("Plan", "EDGE-featureless-bulge-27")]
    [Trait("Plan", "EDGE-featureless-bulge-31")]
    public void Garment_genital_material_goes_its_own_pouch_stays()
    {
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, _ => true, genitalMaterial: true, pouch: true);
        var (plan, log) = Run(garment, Pair(source, body));
        Assert.Contains(log, l => l.StartsWith("bulge: _dwn featureless", StringComparison.Ordinal));
        var outParts = ModelPartReader.Read(plan.Model)!;
        int Drawn(string material) => outParts.Parts.Where(p => p.Island < 0 && p.Material == material)
                                              .Sum(p => Enumerable.Range(0, p.Triangles.Length / 3).Count(t =>
                                                  p.Triangles[t * 3] != p.Triangles[t * 3 + 1] && p.Triangles[t * 3 + 1] != p.Triangles[t * 3 + 2]
                                                  && p.Triangles[t * 3] != p.Triangles[t * 3 + 2]));
        Assert.Equal(0, Drawn(Benis));
        Assert.True(Drawn(Pouch) > 0, "the garment's own pouch was taken out");
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-36")]
    public void Garment_with_genital_skin_keeping_its_skin_is_today()
    {
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, p => p.Label == "1.1", genitalSkin: true);
        var (plan, log) = Run(garment, Pair(source, body), replaceSkin: false);
        var (plain, _) = Run(garment, Pair(source, body), replaceSkin: false, noBulge: true);
        Assert.Contains(log, l => l.Contains("garment carries genitals and this refit keeps its skin", StringComparison.Ordinal));
        Assert.Equal(Hash(plain.Model), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "AC-featureless-bulge-8")]
    [Trait("Plan", "EDGE-featureless-bulge-23")]
    public void A_failure_leaves_the_refit_as_today()
    {
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, _ => true);
        var failing = Pair(source, body, () => throw new InvalidOperationException("boom"));
        var (plan, log) = Run(garment, failing);
        var (plain, _) = Run(garment, failing, noBulge: true);
        Assert.Contains(log, l => l.Contains("left as it is — failed: boom", StringComparison.Ordinal));
        Assert.Equal(Hash(plain.Model), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-11")]
    public void The_same_file_on_both_sides_is_today()
    {
        var body = SyntheticBulge.Body();
        var garment = Garment(body, _ => true);
        var (plan, log) = Run(garment, Pair(body, body));
        var (plain, _) = Run(garment, Pair(body, body), noBulge: true);
        Assert.DoesNotContain(log, l => l.StartsWith("bulge:", StringComparison.Ordinal));
        Assert.Equal(Hash(plain.Model), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-18")]
    public void A_shape_only_pair_is_today()
    {
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, _ => true);
        var pair = Pair(source, body) with { ShapeOnly = true };
        var (plan, log) = Run(garment, pair);
        var (plain, _) = Run(garment, pair, noBulge: true);
        Assert.DoesNotContain(log, l => l.StartsWith("bulge:", StringComparison.Ordinal));
        Assert.Equal(Hash(plain.Model), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-28")]
    public void The_guard_keeps_todays_refit_when_the_bulge_folds_more()
    {
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, _ => true);
        var log = new List<string>();
        var guarded = BodyRetarget.WithTuning(new BodyRetarget.Tuning(BulgeForceGuard: true), () =>
            BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [Pair(source, body)], "_dwn", replaceSkin: true, log: log.Add));
        var (plain, _) = Run(garment, Pair(source, body), noBulge: true);
        Assert.Contains(log, l => l.StartsWith("bulge: _dwn penis kept: guard", StringComparison.Ordinal));
        Assert.Equal(Hash(plain.Model), Hash(guarded.Model));
    }

    [Fact]
    [Trait("Plan", "AC-featureless-bulge-8")]
    [Trait("Plan", "EDGE-featureless-bulge-23")]
    public void A_fault_after_the_factory_leaves_the_refit_as_today()
    {
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, _ => true);
        var log = new List<string>();
        var faulted = BodyRetarget.WithTuning(new BodyRetarget.Tuning(BulgeFault: true), () =>
            BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [Pair(source, body)], "_dwn", replaceSkin: true, log: log.Add));
        var (plain, _) = Run(garment, Pair(source, body), noBulge: true);
        Assert.Contains(log, l => l.Contains("left as it is — failed: bulge fault (test)", StringComparison.Ordinal));
        Assert.Equal(Hash(plain.Model), Hash(faulted.Model));
    }

    [Fact]
    [Trait("Plan", "AC-featureless-bulge-5")]
    [Trait("Plan", "EDGE-featureless-bulge-26")]
    public void Garment_genital_skin_goes_when_its_skin_is_swapped()
    {
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, p => p.Label == "1.1", genitalSkin: true);
        var (plan, log) = Run(garment, Pair(source, body), replaceSkin: true);
        Assert.Contains(log, l => l.StartsWith("bulge: _dwn featureless", StringComparison.Ordinal) && !l.Contains("(garment: 0)", StringComparison.Ordinal));
        // None of the garment's own dome skin is drawn: no output skin vertex where the source's dome stood proud.
        var src = ModelPartReader.Read(source)!;
        var bulge = TbseBulge.Shared!.Positions;
        var proud = src.Parts.Single(p => p.Island < 0 && p.Label == "1.2").Triangles.Distinct().Select(v => At(src, v))
                       .Where(p => bulge.Min(b => Vector3.Distance(b, p)) > 0.005f).ToList();
        var m = ModelPartReader.Read(plan.Model)!;
        var drawn = m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                     .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3).Where(t => p.Triangles[t * 3] != p.Triangles[t * 3 + 1]
                                                                                      && p.Triangles[t * 3 + 1] != p.Triangles[t * 3 + 2])
                                                .SelectMany(t => new[] { p.Triangles[t * 3], p.Triangles[t * 3 + 1], p.Triangles[t * 3 + 2] }))
                     .Distinct().Select(v => At(m, v)).ToList();
        Assert.DoesNotContain(drawn, d => proud.Any(p => Vector3.Distance(p, d) < 0.001f));
    }

    [Fact]
    [Trait("Plan", "AC-featureless-bulge-6")]
    public void Cloth_rigged_to_the_penis_follows_the_hips()
    {
        var body = SyntheticBulge.Body(domeBone: "iv_funyachin_phy_a");
        var source = SyntheticBulge.Body(height: 0.065f, domeBone: "iv_funyachin_phy_a");
        var garment = Garment(source, _ => true);   // the cloth copies the source's weights: the dome's on the penis bone
        var (plan, log) = Run(garment, Pair(source, body));
        Assert.Contains(log, l => l.Contains("rigged to the penis now follow the hips", StringComparison.Ordinal));
        var skin = ModelSkinReader.Read(plan.Model, null, null)!;
        for (int v = 0; v < skin.VertexCount; v++)
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                if (skin.BoneWeights[v * 8 + k] > 0f)
                    Assert.False(GenitalParts.IsPenisBone(skin.BoneNames[skin.BoneIndices[v * 8 + k]]), $"vertex {v} still on a penis bone");
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-19")]
    public void Parallel_refits_are_the_same_as_one()
    {
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, _ => true);
        var pair = Pair(source, body);   // one factory shared by every run, as a size set shares a pair
        var one = Hash(Run(garment, pair).Plan.Model);
        var many = System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0, 4).Select(_ => System.Threading.Tasks.Task.Run(() => Hash(Run(garment, pair).Plan.Model)))).Result;
        Assert.All(many, h => Assert.Equal(one, h));
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-22")]
    [Trait("Plan", "EDGE-featureless-bulge-40")]
    public void A_damaged_asset_is_refused()
    {
        Assert.Null(TbseBulge.Load("{ not json", out var why1));
        Assert.NotNull(why1);
        Assert.Null(TbseBulge.Load("{\"source\":\"x\",\"hole\":[],\"positions\":[],\"uv\":[],\"land\":[],\"weights\":[],\"rim\":[],\"triangles\":[]}", out var why2));
        Assert.Equal("the TBSE bulge file is malformed", why2);
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-3")]
    public void Carried_pubes_are_not_cloth_covering_the_crotch()
    {
        // Crotchless cloth, and a body mod's pubic hair mesh over the dome: the pubes cover nothing.
        var body = SyntheticBulge.Body();
        var source = Source();
        var hole = TbseBulge.Shared!.Hole;
        var centre = hole.Aggregate(Vector3.Zero, (s, p) => s + p) / hole.Length;
        float radius = hole.Max(p => Vector3.Distance(p, centre));
        var garment = Garment(source, p => p.Label == "1.1", pubes: true, where: p => Vector3.Distance(p, centre) >= 2f * radius);
        var (plan, log) = Run(garment, Pair(source, body));
        var (plain, _) = Run(garment, Pair(source, body), noBulge: true);
        Assert.Contains(log, l => l.Contains("crotch not covered", StringComparison.Ordinal));
        Assert.Equal(Hash(plain.Model), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-18")]
    public void A_shoe_or_glove_limb_refit_is_today()
    {
        // swapSlot set (the automatic refit of shoes and gloves): the hook is passed by entirely.
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, _ => true);
        var log = new List<string>();
        var limb = BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [Pair(source, body)], "_sho",
                                     replaceSkin: true, swapSlot: "_dwn", log: log.Add);
        var plain = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoBulge: true), () =>
            BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [Pair(source, body)], "_sho",
                              replaceSkin: true, swapSlot: "_dwn"));
        Assert.DoesNotContain(log, l => l.StartsWith("bulge:", StringComparison.Ordinal));
        Assert.Equal(Hash(plain.Model), Hash(limb.Model));
    }

    [Fact]
    [Trait("Plan", "AC-featureless-bulge-7")]
    [Trait("Plan", "EDGE-featureless-bulge-2")]
    public void A_body_without_a_penis_through_the_factory_is_today()
    {
        // The hook entered (the pair has its factory), the body no penis: byte for byte today's refit.
        var body = SyntheticBulge.Body(height: 0.004f);
        var source = SyntheticBulge.Body(height: 0.0045f);
        var garment = Garment(source, _ => true);
        var (plan, _) = Run(garment, Pair(source, body));
        var (plain, _) = Run(garment, Pair(source, body), noBulge: true);
        Assert.Equal(Hash(plain.Model), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-35")]
    public void Genital_skin_in_a_kept_skin_mesh_is_today()
    {
        // The garment's skin holds the old body's genitals and, in the same mesh, skin lifted far off the body: the rebuild
        // keeps that mesh whole, so its collapsed genitals would show as a hole. Today's refit instead.
        var body = SyntheticBulge.Body();
        var source = Source();
        var garment = Garment(source, p => p.Label == "1.1", genitalSkin: true, liftedSkin: true);
        var (plan, log) = Run(garment, Pair(source, body), replaceSkin: true);
        var (plain, _) = Run(garment, Pair(source, body), replaceSkin: true, noBulge: true);
        Assert.Contains(log, l => l.Contains("garment carries genitals and this refit keeps its skin", StringComparison.Ordinal));
        Assert.Equal(Hash(plain.Model), Hash(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-3")]
    public void A_carried_genital_material_is_not_cloth_covering_the_crotch()
    {
        // Crotchless cloth, and a body mod's genital material over the dome: it covers nothing.
        var body = SyntheticBulge.Body();
        var source = Source();
        var hole = TbseBulge.Shared!.Hole;
        var centre = hole.Aggregate(Vector3.Zero, (s, p) => s + p) / hole.Length;
        float radius = hole.Max(p => Vector3.Distance(p, centre));
        var garment = Garment(source, p => p.Label == "1.1", genitalMaterial: true, where: p => Vector3.Distance(p, centre) >= 2f * radius);
        var (plan, log) = Run(garment, Pair(source, body));
        var (plain, _) = Run(garment, Pair(source, body), noBulge: true);
        Assert.Contains(log, l => l.Contains("crotch not covered", StringComparison.Ordinal));
        Assert.Equal(Hash(plain.Model), Hash(plan.Model));
    }
}
