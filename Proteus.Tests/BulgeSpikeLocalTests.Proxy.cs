using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>A0.0 (plan featureless-bulge rev11): the TBSE sculpt's landing, predicted on a proxy before the owner sculpts.</summary>
public partial class BulgeSpikeLocalTests
{
    internal const string MistletoeLegs = LocalData.Mods + ":Mistletoe Panties - by Solona/size/chara/equipment/e0231/model/c0201e0231_dwn.mdl";
    internal const string GenALegs = TbseOpt + "gen a" + E0 + "c0101e0000_dwn.mdl";

    /// <summary>
    /// The A0.0 proxy: TBSE Gen A with its genital piece (<c>0.3</c>) smoothed — same vertices, same order, same uvs, only
    /// its positions moved to the biharmonic fill of the hole it plugs, the weld ring and every <c>0.0</c> vertex held.
    /// This is the exact form the owner's sculpt will take, so a garment's landing on it predicts the landing on hers.
    /// </summary>
    internal static byte[] SmoothedGenA(byte[] genA, out int moved, out float most)
    {
        var parts = ModelPartReader.Read(genA)!;
        var penis = parts.Parts.Single(p => p.Island < 0 && p.Label == "1.4");
        var rest = parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material) && p != penis).ToList();
        int vc = parts.Positions.Length / 3;
        var nodeOf = new int[vc];
        Array.Fill(nodeOf, -1);
        var byPlace = new Dictionary<(long, long, long), int>();
        var at = new List<Vector3>();
        foreach (var part in rest.Append(penis))
            foreach (int v in part.Triangles)
            {
                if (nodeOf[v] >= 0) continue;
                var p = At(parts, v);
                var key = ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));
                if (!byPlace.TryGetValue(key, out int n)) { byPlace[key] = n = at.Count; at.Add(p); }
                nodeOf[v] = n;
            }
        int nc = at.Count;
        var adj = Enumerable.Range(0, nc).Select(_ => new HashSet<int>()).ToArray();
        foreach (var part in rest.Append(penis))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = nodeOf[part.Triangles[t + k]], b = nodeOf[part.Triangles[t + (k + 1) % 3]];
                    if (a != b) { adj[a].Add(b); adj[b].Add(a); }
                }
        var restNodes = rest.SelectMany(p => p.Triangles).Select(v => nodeOf[v]).ToHashSet();
        var isFree = new bool[nc];
        foreach (int v in penis.Triangles) if (!restNodes.Contains(nodeOf[v])) isFree[nodeOf[v]] = true;

        // L²x = 0 on the free nodes, the rest held: conjugate gradients on the uniform graph Laplacian, per axis.
        Vector3[] L(Vector3[] x)
        {
            var r = new Vector3[nc];
            for (int n = 0; n < nc; n++)
            {
                if (adj[n].Count == 0) continue;
                var sum = Vector3.Zero;
                foreach (int m in adj[n]) sum += x[m];
                r[n] = x[n] * adj[n].Count - sum;
            }
            return r;
        }
        Vector3[] A(Vector3[] v)
        {
            var r = L(L(v));
            for (int n = 0; n < nc; n++) if (!isFree[n]) r[n] = Vector3.Zero;
            return r;
        }
        Vector3 Dot(Vector3[] u, Vector3[] w)
        {
            var s = Vector3.Zero;
            for (int n = 0; n < nc; n++) s += u[n] * w[n];
            return s;
        }
        static Vector3 Div(Vector3 a, Vector3 b) => new(b.X > 0 ? a.X / b.X : 0, b.Y > 0 ? a.Y / b.Y : 0, b.Z > 0 ? a.Z / b.Z : 0);

        var held = at.Select((p, n) => isFree[n] ? Vector3.Zero : p).ToArray();
        var rr = A(held).Select(x => -x).ToArray();
        var xv = new Vector3[nc];
        var pp = (Vector3[])rr.Clone();
        var rs = Dot(rr, rr);
        for (int it = 0; it < 6000 && rs.X + rs.Y + rs.Z > 1e-24f; it++)
        {
            var ap = A(pp);
            var alpha = Div(rs, Dot(pp, ap));
            for (int n = 0; n < nc; n++) { xv[n] += alpha * pp[n]; rr[n] -= alpha * ap[n]; }
            var rs2 = Dot(rr, rr);
            var beta = Div(rs2, rs);
            for (int n = 0; n < nc; n++) pp[n] = rr[n] + beta * pp[n];
            rs = rs2;
        }

        var delta = new Vec3[vc];
        var normal = new Vec3[vc];
        moved = 0;
        most = 0f;
        foreach (int v in penis.Triangles.Distinct())
        {
            int n = nodeOf[v];
            if (!isFree[n]) continue;
            var d = xv[n] - at[n];
            delta[v] = new Vec3(d.X, d.Y, d.Z);
            moved++;
            most = MathF.Max(most, d.Length());
        }
        return MeshVolumeService.Inflate((byte[])genA.Clone(), new RetargetEdit(parts.MeshSpans, delta, normal)).Model;
    }

    /// <summary>
    /// A0.0: women's legs landed through the bibo→TBSE uv field on the proxy, against plain Gen A — the displaced source
    /// skin's folds in the crotch field (gate ≤ 2), and the garment's cloth folds through the refit.
    /// </summary>
    [LocalDataFact(LocalData.TbseUvMaps, CrossSexNudeLegsLocalTests.LavaBod, CrossSexNudeLegsLocalTests.StormblessedLegs, MistletoeLegs, GenALegs)]
    public void Proxy_a0_3()
    {
        var genA = File.ReadAllBytes(LocalData.Path(GenALegs));
        var proxy = SmoothedGenA(genA, out int movedVerts, out float most);
        output.WriteLine($"proxy: {movedVerts} genital vertices smoothed, up to {most * 1000f:0} mm");
        var maps = new UVRemapService(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));
        foreach (var (gname, need) in new[] { ("Stormblessed", CrossSexNudeLegsLocalTests.StormblessedLegs), ("Mistletoe", MistletoeLegs) })
        {
            var garment = File.ReadAllBytes(LocalData.Path(need));
            (BodySizeCatalog cat, BodyOption option) made;
            try { made = CrossSexNudeLegsLocalTests.MadeFor(need, CrossSexNudeLegsLocalTests.LavaBod); }
            catch (Xunit.Sdk.XunitException)
            {
                try { made = CrossSexNudeLegsLocalTests.MadeFor(need, CrossSexRefitLocalTests.Bibo); }
                catch (Xunit.Sdk.XunitException) { output.WriteLine($"{gname}: made-on body not found on LavaBod+ or Bibo+ — skipped"); continue; }
            }
            var (cat, option) = made;
            output.WriteLine($"{gname}: made on {option.Label}");
            var source = File.ReadAllBytes(cat.PathOf(option));
            foreach (var (tname, target) in new[] { ("Gen A", genA), ("proxy", proxy) })
            {
                var refusal = BodyRetarget.BuildPair("_dwn", source, target, "_dwn", false, null, null, maps, out var pair,
                                                     out var stats, targetMale: true);
                if (refusal != null) { output.WriteLine($"{gname} -> {tname}: refused — {refusal}"); continue; }
                var src = pair.Correspondence.Source;
                var anchor = BodyRetarget.FindCrotch([(src, ModelSkinReader.Read(source, null, null)!)])!.Value;
                var whole = BodyRetarget.SourceBody.Whole(src, pair.Correspondence.Field);
                int srcFolds = 0;
                foreach (var part in src.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
                    for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                    {
                        int x = part.Triangles[t], y = part.Triangles[t + 1], z = part.Triangles[t + 2];
                        var c = (At(src, x) + At(src, y) + At(src, z)) / 3;
                        if (MathF.Abs(c.X - anchor.MidX) > 0.06f * anchor.Scale || c.Y < anchor.Y - 0.05f * anchor.Scale
                            || c.Y > anchor.Y + 0.14f * anchor.Scale) continue;
                        if (whole[x] is not { } dx || whole[y] is not { } dy || whole[z] is not { } dz) continue;
                        var p0 = At(src, x) + new Vector3(dx.X, dx.Y, dx.Z);
                        var p1 = At(src, y) + new Vector3(dy.X, dy.Y, dy.Z);
                        var p2 = At(src, z) + new Vector3(dz.X, dz.Y, dz.Z);
                        var n0 = Vector3.Cross(At(src, y) - At(src, x), At(src, z) - At(src, x));
                        var n1 = Vector3.Cross(p1 - p0, p2 - p0);
                        if (n0.LengthSquared() > 1e-14f && Vector3.Dot(n0, n1) < 0f) srcFolds++;
                    }
                var plan = BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [pair], "_dwn",
                                             replaceSkin: true, acrossBodies: true, cutHidden: true);
                output.WriteLine($"{gname} -> {tname}: coverage {stats?.VertexCoverage:P1}; displaced source skin folds in the field "
                                 + $"{srcFolds}; cloth folds {CrossSexRefit.ClothFolded(garment, plan)}, report folded {plan.Report.Folded}, "
                                 + $"missed {plan.Report.Missed}");
            }
        }
    }
}
