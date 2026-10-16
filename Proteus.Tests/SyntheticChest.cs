using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A chest at body scale for the refit's nipple smoothing: two breast lobes facing +Z, each with an optional nipple, rigged
/// to the bust bones the way a body mod is — the lobes on <c>j_mune_l</c>/<c>j_mune_r</c>, the midline and the sides on
/// <c>j_sebo_c</c> — with real normals and a uv layout every body built here shares. Garments are built from the SOURCE
/// chest, as an author builds one: its skin copied, and cloth panels standing a few millimetres off it.
/// <para/>
/// The lobes are <see cref="BustBridgeTests.Chest"/>'s, on a finer grid (5 mm) so a 12 mm nipple is a dozen vertices
/// across rather than one, and lifted to chest height so nothing about a real body's position is special here.
/// </summary>
internal static class SyntheticChest
{
    internal const string Skin = "/mt_c0201b0001_bibo.mtrl";
    internal const string Cloth = "/mt_c0201e6017_top_a.mtrl";
    internal const string Piercing = "/mt_c0201b0001_piercings.mtrl";

    internal const float HalfWidth = 0.20f, Height = 0.30f, Base = 1.10f;
    internal const float Centre = 0.10f, Lobe = 0.10f, Spread = 0.06f, NippleSpread = 0.012f;
    internal const int Columns = 81, Rows = 61;

    /// <summary>Where each nipple tip sits in X and Y: on its lobe's peak, half way up.</summary>
    internal static Vector2 Tip(bool left) => new(left ? -Centre : Centre, Base + Height * 0.5f);

    /// <summary>The chest's surface height at (x, y), in +Z.</summary>
    internal static float Z(float x, float y, float nippleL, float nippleR, float spread = NippleSpread)
    {
        float LobeAt(float c) => Lobe * MathF.Exp(-((x - c) * (x - c)) / (Spread * Spread));
        float NippleAt(float c, float h)
            => h <= 0f ? 0f
             : h * MathF.Exp(-(((x - c) * (x - c) + (y - Tip(true).Y) * (y - Tip(true).Y)) / (spread * spread)));
        return LobeAt(-Centre) + LobeAt(Centre) + NippleAt(-Centre, nippleL) + NippleAt(Centre, nippleR);
    }

    /// <summary>The grid: vertex (row, column) is <c>row * columns + column</c>.</summary>
    internal sealed record Grid(Vector3[] Pos, Vector3[] Nrm, Vector2[] Uv, int[] Tris, int Columns, int Rows);

    /// <summary>A chest grid with nipples of the given heights (metres, 0 for none).</summary>
    /// <param name="step">Columns and rows are taken every <paramref name="step"/>th line: 2 gives a coarser body over
    /// the same uv, a different topology for the atlas correspondence.</param>
    internal static Grid Surface(float nippleL, float nippleR, int step = 1, float spread = NippleSpread)
    {
        int cols = (Columns - 1) / step + 1, rows = (Rows - 1) / step + 1;
        var pos = new Vector3[cols * rows];
        var uv = new Vector2[cols * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                float u = (float)c / (cols - 1), v = (float)r / (rows - 1);
                float x = -HalfWidth + 2f * HalfWidth * u, y = Base + Height * v;
                pos[r * cols + c] = new Vector3(x, y, Z(x, y, nippleL, nippleR, spread));
                uv[r * cols + c] = new Vector2(u, v);
            }
        var tris = new List<int>();
        for (int r = 0; r + 1 < rows; r++)
            for (int c = 0; c + 1 < cols; c++)
            {
                int a = r * cols + c, b = a + 1, a2 = a + cols, b2 = a2 + 1;
                tris.AddRange([a, b, b2, a, b2, a2]);
            }
        var t = tris.ToArray();
        return new Grid(pos, Normals(pos, t), uv, t, cols, rows);
    }

    /// <summary>Area-weighted vertex normals.</summary>
    internal static Vector3[] Normals(Vector3[] pos, int[] tris)
    {
        var n = new Vector3[pos.Length];
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            var f = Vector3.Cross(pos[tris[t + 1]] - pos[tris[t]], pos[tris[t + 2]] - pos[tris[t]]);
            n[tris[t]] += f; n[tris[t + 1]] += f; n[tris[t + 2]] += f;
        }
        for (int i = 0; i < n.Length; i++) n[i] = n[i].LengthSquared() > 0 ? Vector3.Normalize(n[i]) : Vector3.UnitZ;
        return n;
    }

    /// <summary>A body vertex's bones: a lobe on its own breast bone, the midline and the outer edge on the spine.</summary>
    internal static (string Bone, float W)[] BodyBones(Vector3 p, bool bust = true)
    {
        if (!bust || MathF.Abs(p.X) < 0.02f || MathF.Abs(p.X) > HalfWidth - 0.02f) return [("j_sebo_c", 1f)];
        return [(p.X < 0 ? "j_mune_l" : "j_mune_r", 1f)];
    }

    /// <summary>A body model: the chest's skin, and optionally a piercing ring standing on each nipple and an
    /// alternative nipple mesh behind an attribute (the way a body mod ships a variant it may not draw).</summary>
    /// <param name="bust">False rigs every vertex to the spine: a body the bust bones never reach.</param>
    /// <param name="altNipple">Adds, as a second submesh tagged <c>atr_tv_b</c>, a copy of the left lobe's nipple disc
    /// raised <paramref name="altNipple"/> further — welded to the drawn skin along its rim, as a real alternative is.</param>
    internal static byte[] Body(float nippleL, float nippleR, bool bust = true, bool piercing = false, int step = 1,
                                float altNipple = 0f, float spread = NippleSpread)
    {
        var g = Surface(nippleL, nippleR, step, spread);
        var weights = g.Pos.Select(p => (((string, float)[]?)BodyBones(p, bust))).ToArray();
        var subs = new List<(uint, int[])> { (0u, g.Tris) };
        var pos = g.Pos.ToList();
        var nrm = g.Nrm.ToList();
        var uv = g.Uv.ToList();
        var w = weights.ToList();
        var attrs = new List<string>();
        if (altNipple > 0f)
        {
            // The disc around the left tip, its rim on the drawn skin's own positions and its middle raised.
            var tip = Tip(true);
            var map = new Dictionary<int, int>();
            var alt = new List<int>();
            for (int t = 0; t + 2 < g.Tris.Length; t += 3)
            {
                bool near = true;
                for (int k = 0; k < 3; k++)
                    if (Vector2.Distance(new Vector2(g.Pos[g.Tris[t + k]].X, g.Pos[g.Tris[t + k]].Y), tip) > 0.02f) near = false;
                if (!near) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = g.Tris[t + k];
                    if (!map.TryGetValue(v, out int copy))
                    {
                        copy = pos.Count;
                        map[v] = copy;
                        var p = g.Pos[v];
                        float d = Vector2.Distance(new Vector2(p.X, p.Y), tip);
                        float lift = d >= 0.015f ? 0f : altNipple * (1f - d / 0.015f);
                        pos.Add(p + new Vector3(0, 0, lift));
                        nrm.Add(g.Nrm[v]);
                        uv.Add(g.Uv[v]);
                        w.Add(weights[v]);
                    }
                    alt.Add(copy);
                }
            }
            attrs.Add("atr_tv_b");
            subs.Add((1u, alt.ToArray()));
        }
        var meshes = new List<SyntheticModel.SurfaceMesh>
        {
            new(Skin, pos.ToArray(), nrm.ToArray(), uv.ToArray(), w.ToArray(), subs.ToArray()),
        };
        if (piercing) meshes.Add(Ring(nippleL, nippleR));
        return SyntheticModel.BuildSurface(attrs, meshes.ToArray());
    }

    /// <summary>A small ring lying on each nipple tip, 1 mm off it, on a piercing material.</summary>
    internal static SyntheticModel.SurfaceMesh Ring(float nippleL, float nippleR)
    {
        var pos = new List<Vector3>();
        var tris = new List<int>();
        foreach (bool left in new[] { true, false })
        {
            var tip = Tip(left);
            float z = Z(tip.X, tip.Y, nippleL, nippleR) + 0.001f;
            int start = pos.Count;
            pos.Add(new Vector3(tip.X, tip.Y, z));
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.Tau / 8;
                float x = tip.X + 0.004f * MathF.Cos(a), y = tip.Y + 0.004f * MathF.Sin(a);
                pos.Add(new Vector3(x, y, Z(x, y, nippleL, nippleR) + 0.001f));
            }
            for (int k = 0; k < 8; k++) tris.AddRange([start, start + 1 + k, start + 1 + (k + 1) % 8]);
        }
        var p = pos.ToArray();
        var t = tris.ToArray();
        return new SyntheticModel.SurfaceMesh(Piercing, p, Normals(p, t), p.Select(_ => Vector2.Zero).ToArray(),
            p.Select(q => (((string, float)[]?)BodyBones(q))).ToArray(), [(0u, t)]);
    }

    /// <summary>
    /// A garment made on the source chest: its skin copied verbatim, and a cloth panel <paramref name="off"/> out along
    /// the skin's normal over every grid cell whose middle lies in <paramref name="box"/> (x0, y0, x1, y1).
    /// </summary>
    /// <param name="clothAttr">The panel's attribute name (e.g. a variant tag), or null for none.</param>
    /// <param name="skinCopyMaterial">Also carry a copy of the source skin, <paramref name="skinCopyOff"/> out, under
    /// this (non-skin) material: an author's sculpted or custom-material skin.</param>
    /// <param name="behind">Put the panel behind the chest instead (a back piece): mirrored to -Z.</param>
    /// <param name="step">The source chest's grid step (see <see cref="Surface"/>): the garment is made on that chest.</param>
    /// <param name="offAt">Per panel vertex, by its x: how far out it stands instead of <paramref name="off"/>.</param>
    internal static byte[] Garment(float sourceNippleL, float sourceNippleR, (float X0, float Y0, float X1, float Y1) box,
                                   float off = 0.002f, string? clothAttr = null, string? skinCopyMaterial = null,
                                   float skinCopyOff = 0f, bool behind = false, bool withSkin = true, int step = 1,
                                   Func<float, float>? offAt = null)
    {
        var g = Surface(sourceNippleL, sourceNippleR, step);
        var weights = g.Pos.Select(p => (((string, float)[]?)BodyBones(p))).ToArray();
        var meshes = new List<SyntheticModel.SurfaceMesh>();
        if (withSkin) meshes.Add(new(Skin, g.Pos, g.Nrm, g.Uv, weights, [(0u, g.Tris)]));

        var map = new Dictionary<int, int>();
        var cp = new List<Vector3>();
        var cn = new List<Vector3>();
        var cw = new List<(string, float)[]?>();
        var ct = new List<int>();
        for (int t = 0; t + 2 < g.Tris.Length; t += 3)
        {
            var mid = (g.Pos[g.Tris[t]] + g.Pos[g.Tris[t + 1]] + g.Pos[g.Tris[t + 2]]) / 3f;
            if (mid.X < box.X0 || mid.X > box.X1 || mid.Y < box.Y0 || mid.Y > box.Y1) continue;
            for (int k = 0; k < 3; k++)
            {
                int v = g.Tris[t + k];
                if (!map.TryGetValue(v, out int c))
                {
                    c = cp.Count;
                    map[v] = c;
                    var p = g.Pos[v] + g.Nrm[v] * (offAt?.Invoke(g.Pos[v].X) ?? off);
                    var n = g.Nrm[v];
                    if (behind) { p = new Vector3(p.X, p.Y, -0.15f - p.Z); n = new Vector3(n.X, n.Y, -n.Z); }
                    cp.Add(p);
                    cn.Add(n);
                    cw.Add(weights[v]);
                }
                ct.Add(c);
            }
        }
        var attrs = new List<string>();
        uint mask = 0;
        if (clothAttr != null) { attrs.Add(clothAttr); mask = 1; }
        if (cp.Count > 0)
            meshes.Add(new(Cloth, cp.ToArray(), cn.ToArray(), cp.Select(_ => new Vector2(0.5f, 0.5f)).ToArray(),
                           cw.ToArray(), [(mask, ct.ToArray())]));
        if (skinCopyMaterial != null)
            meshes.Add(new(skinCopyMaterial, g.Pos.Select((p, i) => p + g.Nrm[i] * skinCopyOff).ToArray(), g.Nrm, g.Uv,
                           weights, [(0u, g.Tris)]));
        return SyntheticModel.BuildSurface(attrs, meshes.ToArray());
    }

    /// <summary>Both lobes, nipple and all.</summary>
    internal static readonly (float, float, float, float) BothCups = (-0.17f, Base + 0.06f, 0.17f, Base + 0.24f);

    /// <summary>The right lobe only, stopping well short of the midline.</summary>
    internal static readonly (float, float, float, float) RightCup = (0.04f, Base + 0.06f, 0.17f, Base + 0.24f);

    /// <summary>A refit pair from <paramref name="source"/> to <paramref name="target"/>, through the identity when
    /// they share a topology and the uv atlas when they do not.</summary>
    /// <param name="atlas">Go through the uv atlas even when the identity would do.</param>
    internal static BodyRetarget.SlotPair Pair(byte[] source, byte[] target, string slot = "_top",
                                               IReadOnlySet<string>? hidden = null, bool atlas = false)
    {
        var s = ModelPartReader.Read(source)!;
        var t = BodyRetarget.Without(ModelPartReader.Read(target)!, hidden);
        IBodyCorrespondence? built;
        string refusal;
        if (atlas)
        {
            Assert.True(UvAtlasCorrespondence.TryBuild(s, Uv(source), t, Uv(target), slot, out var byUv, out refusal), refusal);
            built = byUv;
        }
        else if (s.Positions.Length == t.Positions.Length)
        {
            Assert.True(IdentityCorrespondence.TryBuild(s, t, slot, out var identity, out refusal), refusal);
            built = identity;
        }
        else
            Assert.True(BodyCorrespondence.TryBuild(s, Uv(source), t, Uv(target), slot, out built, out refusal), refusal);
        return new BodyRetarget.SlotPair(slot, built!, t, target, source, hidden);
    }

    internal static float[] Uv(byte[] mdl)
        => SecondSkinWriter.TryReadLod0Geometry(mdl, out _, out var uv, out _, out _, out _, false, false, null) ? uv : [];
}
