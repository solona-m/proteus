using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Hips and legs at body scale for the refit's crotch ease: a hip tube from the waist down to the crotch at
/// <see cref="Crotch"/>, two thigh tubes from there to the knee, and the perineum between them — a flat patch facing
/// down. Rigged the way a body mod is: the hips on <c>j_kosi</c>, taking the thighs on at the bottom; each thigh on its
/// own <c>j_asi_a_*</c>, the knee on <c>j_asi_b_*</c>; and the perineum on both thighs and the hips. The character faces
/// +Z and its left is +X, as the game's are.
/// <para/>
/// Garments are cut from the same surfaces, 1 mm out along the skin's normal, weighted as the skin under them — as an
/// author builds them, by copying a body's weights.
/// </summary>
internal static class SyntheticCrotch
{
    internal const string Skin = "/mt_c0201b0001_bibo.mtrl";
    internal const string Cloth = "/mt_c0201e0600_dwn_a.mtrl";
    internal const string Pouch = "/mt_c0201e0001_pouch.mtrl";

    internal const float Crotch = 0.80f, Waist = 1.00f, Knee = 0.40f;
    internal const float HipRx = 0.16f, HipRz = 0.11f, LegX = 0.08f, LegR = 0.07f;
    internal const float PerineumX = 0.03f, PerineumZ = 0.06f;
    internal static readonly Vector3 HipL = new(LegX, 0.86f, 0f), HipR = new(-LegX, 0.86f, 0f);

    /// <summary>Which surface a body vertex is on.</summary>
    internal enum Part { Hips, LegL, LegR, Perineum }

    /// <summary>The body: per vertex its position, normal, bones and surface, and the triangles of each surface.</summary>
    internal sealed record Surface(Vector3[] Pos, Vector3[] Nrm, (string Bone, float W)[][] Weights, Part[] Of, int[] Tris);

    /// <summary>How far toward the left thigh a point at <paramref name="x"/> is rigged: smooth across the midline.</summary>
    private static float LeftShare(float x)
    {
        float t = Math.Clamp((x + PerineumX) / (2 * PerineumX), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    /// <summary>The body's bones at a point of a surface.</summary>
    /// <param name="physics">Give each thigh a physics bone beside it, as body mods do (<c>ya_daitai_phys_*</c>).</param>
    /// <param name="kosi">False rigs the hips to <c>j_sebo_a</c> instead: a rig without <c>j_kosi</c>.</param>
    /// <param name="split">Split the crotch hard down the midline, each side on its own thigh alone, as The Body SE does.</param>
    internal static (string Bone, float W)[] Bones(Part part, Vector3 p, bool physics = false, bool kosi = true,
                                                   bool split = false)
    {
        float LeftShare(float x) => split ? (x >= 0f ? 1f : 0f) : SyntheticCrotch.LeftShare(x);
        string hip = kosi ? "j_kosi" : "j_sebo_a";
        var w = new List<(string, float)>();
        void Thigh(string side, float share)
        {
            if (share <= 0f) return;
            if (physics) { w.Add(($"j_asi_a_{side}", share * 0.7f)); w.Add(($"ya_daitai_phys_{side}", share * 0.3f)); }
            else w.Add(($"j_asi_a_{side}", share));
        }
        switch (part)
        {
            case Part.Hips:
            {
                float legs = Math.Clamp((0.88f - p.Y) / 0.08f, 0f, 1f) * 0.6f;
                float l = LeftShare(p.X);
                w.Add((hip, 1f - legs));
                // Physics on a sided vertex only, so no vertex needs more than the four a synthetic mesh carries.
                if (!physics) { Thigh("l", legs * l); Thigh("r", legs * (1 - l)); }
                else if (l <= 0f || l >= 1f) Thigh(l >= 1f ? "l" : "r", legs);
                else { w.Add(("j_asi_a_l", legs * l)); w.Add(("j_asi_a_r", legs * (1 - l))); }
                break;
            }
            case Part.LegL or Part.LegR:
            {
                string side = part == Part.LegL ? "l" : "r";
                float top = Math.Clamp((p.Y - 0.76f) / 0.04f, 0f, 1f) * 0.3f;
                float knee = Math.Clamp((0.55f - p.Y) / 0.10f, 0f, 1f);
                if (top > 0f) w.Add((hip, top));
                Thigh(side, (1 - top) * (1 - knee));
                if (knee > 0f) w.Add(($"j_asi_b_{side}", (1 - top) * knee));
                break;
            }
            default:
            {
                float l = LeftShare(p.X);
                w.Add((hip, 0.4f));
                w.Add(("j_asi_a_l", 0.6f * l));
                w.Add(("j_asi_a_r", 0.6f * (1 - l)));
                break;
            }
        }
        return [.. w.Where(i => i.Item2 > 1e-4f)];
    }

    /// <summary>The body's surfaces, 1 cm apart, scaled by <paramref name="scale"/> about the floor.</summary>
    /// <param name="crotch">False rigs the hips and the perineum to the hips alone: a body with no skin on both thighs.</param>
    internal static Surface Build(bool physics = false, bool kosi = true, float scale = 1f, bool perineum = true,
                                  bool crotch = true, bool split = false)
    {
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var of = new List<Part>();
        var tris = new List<int>();

        void Tube(Part part, float cx, float rx, float rz, float y0, float y1, int around)
        {
            int rows = (int)MathF.Round((y1 - y0) / 0.01f) + 1, start = pos.Count;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < around; c++)
                {
                    float a = c * MathF.Tau / around;
                    float x = rx * MathF.Cos(a), z = rz * MathF.Sin(a);
                    pos.Add(new Vector3(cx + x, y0 + r * 0.01f, z));
                    nrm.Add(Vector3.Normalize(new Vector3(x / (rx * rx), 0f, z / (rz * rz))));
                    of.Add(part);
                }
            for (int r = 0; r + 1 < rows; r++)
                for (int c = 0; c < around; c++)
                {
                    int a = start + r * around + c, b = start + r * around + (c + 1) % around;
                    int a2 = a + around, b2 = b + around;
                    tris.AddRange([a, b2, b, a, a2, b2]);
                }
        }

        Tube(Part.Hips, 0f, HipRx, HipRz, Crotch, Waist, 48);
        Tube(Part.LegL, LegX, LegR, LegR, Knee, Crotch, 32);
        Tube(Part.LegR, -LegX, LegR, LegR, Knee, Crotch, 32);
        if (perineum)
        {
            int cols = (int)MathF.Round(2 * PerineumX / 0.01f) + 1, rows = (int)MathF.Round(2 * PerineumZ / 0.01f) + 1;
            int start = pos.Count;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    pos.Add(new Vector3(-PerineumX + c * 0.01f, Crotch, -PerineumZ + r * 0.01f));
                    nrm.Add(-Vector3.UnitY);
                    of.Add(Part.Perineum);
                }
            for (int r = 0; r + 1 < rows; r++)
                for (int c = 0; c + 1 < cols; c++)
                {
                    int a = start + r * cols + c, b = a + 1, a2 = a + cols, b2 = a2 + 1;
                    tris.AddRange([a, b, b2, a, b2, a2]);
                }
        }

        var weights = pos.Select((p, i) => crotch || of[i] is Part.LegL or Part.LegR
                                               ? Bones(of[i], p, physics, kosi, split)
                                               : [(kosi ? "j_kosi" : "j_sebo_a", 1f)]).ToArray();
        var scaled = pos.Select(p => p * scale).ToArray();
        return new Surface(scaled, [.. nrm], weights, [.. of], [.. tris]);
    }

    /// <summary>A body model of <see cref="Build"/>'s surfaces, and optionally a pouch below the crotch on a material of
    /// its own, rigged mostly to its own bone and a little to each thigh — the way a male body mod's is.</summary>
    internal static byte[] Body(bool physics = false, bool kosi = true, float scale = 1f, bool pouch = false,
                                bool perineum = true, bool crotch = true, bool split = false)
    {
        var s = Build(physics, kosi, scale, perineum, crotch, split);
        var meshes = new List<SyntheticModel.SurfaceMesh>
        {
            new(Skin, s.Pos, s.Nrm, s.Pos.Select(p => new Vector2(p.X + 0.5f, p.Y)).ToArray(),
                s.Weights.Select(w => ((string, float)[]?)w).ToArray(), [(0u, s.Tris)]),
        };
        if (pouch)
        {
            // A small cap hanging 3 cm below the crotch, in front.
            var p = new List<Vector3>();
            var t = new List<int>();
            p.Add(new Vector3(0f, (Crotch - 0.03f) * scale, 0.03f * scale));
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.Tau / 8;
                p.Add(new Vector3(0.02f * MathF.Cos(a), Crotch - 0.01f, 0.03f + 0.02f * MathF.Sin(a)) * scale);
            }
            for (int k = 0; k < 8; k++) t.AddRange([0, 1 + k, 1 + (k + 1) % 8]);
            var pa = p.ToArray();
            meshes.Add(new(Pouch, pa, pa.Select(_ => -Vector3.UnitY).ToArray(), pa.Select(_ => Vector2.Zero).ToArray(),
                           pa.Select(_ => ((string, float)[]?)[("iv_kintama", 0.7f), ("j_asi_a_l", 0.15f), ("j_asi_a_r", 0.15f)])
                             .ToArray(), [(0u, t.ToArray())]));
        }
        return SyntheticModel.BuildSurface([], [.. meshes]);
    }

    /// <summary>One piece of cloth: the body triangles all of whose corners <see cref="Keep"/> holds, <see cref="Off"/> out
    /// along the skin's normal.</summary>
    /// <param name="Weights">The cloth's bones at a body vertex, in place of the skin's; null copies the skin's.</param>
    /// <param name="OwnMesh">A mesh of its own, as a lace layer over a garment usually is, rather than a submesh.</param>
    internal sealed record Piece(Func<Part, Vector3, bool> Keep, float Off = 0.001f,
                                 Func<Part, Vector3, (string Bone, float W)[]>? Weights = null, bool OwnMesh = false);

    /// <summary>
    /// A garment of <paramref name="pieces"/>, each its own submesh. With <paramref name="stitch"/>, the hips' bottom edge
    /// is sewn onto the nearest cloth of the surfaces it names, so the garment is one sheet, as a real pair of pants is.
    /// </summary>
    /// <param name="skin">Also carry the body's own skin where this holds, as a garment's body mesh.</param>
    internal static byte[] Garment(Piece[] pieces, Func<Part, bool>? stitch = null, float scale = 1f, bool physics = false,
                                   bool kosi = true, IReadOnlyList<SyntheticModel.SurfaceMesh>? extra = null,
                                   Func<Part, Vector3, bool>? skin = null, bool split = false)
    {
        var s = Build(physics, kosi, scale, split: split);
        var cp = new List<Vector3>();
        var cn = new List<Vector3>();
        var cw = new List<(string, float)[]?>();
        var subs = new List<(uint, int[])>();
        var maps = new List<Dictionary<int, int>>();
        foreach (var piece in pieces.Where(p => !p.OwnMesh))
        {
            var map = new Dictionary<int, int>();
            var ct = new List<int>();
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
            {
                bool all = true;
                for (int k = 0; k < 3; k++)
                {
                    int v = s.Tris[t + k];
                    if (!piece.Keep(s.Of[v], s.Pos[v] / scale)) all = false;
                }
                if (!all) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = s.Tris[t + k];
                    if (!map.TryGetValue(v, out int c))
                    {
                        c = cp.Count;
                        map[v] = c;
                        cp.Add(s.Pos[v] + s.Nrm[v] * piece.Off * scale);
                        cn.Add(s.Nrm[v]);
                        cw.Add(piece.Weights?.Invoke(s.Of[v], s.Pos[v] / scale) ?? s.Weights[v]);
                    }
                    ct.Add(c);
                }
            }
            maps.Add(map);
            if (ct.Count > 0) subs.Add((0u, ct.ToArray()));
        }

        if (stitch != null)
        {
            // Every hip cloth vertex on the bottom ring takes a triangle to the nearest cloth vertex of the legs or the
            // perineum, so welding finds one sheet.
            var bottom = maps.SelectMany(m => m).Where(kv => s.Of[kv.Key] == Part.Hips && MathF.Abs(s.Pos[kv.Key].Y / scale - Crotch) < 1e-4f)
                             .OrderBy(kv => MathF.Atan2(s.Pos[kv.Key].Z, s.Pos[kv.Key].X)).Select(kv => kv.Value).ToList();
            var below = maps.SelectMany(m => m).Where(kv => s.Of[kv.Key] != Part.Hips && stitch(s.Of[kv.Key]))
                            .Select(kv => kv.Value).ToList();
            var st = new List<int>();
            for (int i = 0; below.Count > 0 && i < bottom.Count; i++)
            {
                int a = bottom[i], b = bottom[(i + 1) % bottom.Count];
                int c = below.MinBy(u => Vector3.DistanceSquared(cp[u], (cp[a] + cp[b]) / 2));
                st.AddRange([a, b, c]);
            }
            if (st.Count > 0) subs.Add((0u, st.ToArray()));
        }

        var meshes = new List<SyntheticModel.SurfaceMesh>();
        if (cp.Count > 0)
        {
            var pa = cp.ToArray();
            meshes.Add(new(Cloth, pa, cn.ToArray(), pa.Select(_ => new Vector2(0.5f, 0.5f)).ToArray(), cw.ToArray(),
                           subs.ToArray()));
        }
        foreach (var piece in pieces.Where(p => p.OwnMesh))
        {
            var own = Garment([piece with { OwnMesh = false }], scale: scale, physics: physics, kosi: kosi, split: split);
            meshes.Add(MeshOf(own));
        }
        if (skin != null)
        {
            var map = new Dictionary<int, int>();
            var st = new List<int>();
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
            {
                if (!Enumerable.Range(0, 3).All(k => skin(s.Of[s.Tris[t + k]], s.Pos[s.Tris[t + k]] / scale))) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = s.Tris[t + k];
                    if (!map.TryGetValue(v, out int c)) map[v] = c = map.Count;
                    st.Add(c);
                }
            }
            var order = map.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray();
            var pa = order.Select(v => s.Pos[v]).ToArray();
            meshes.Add(new(Skin, pa, order.Select(v => s.Nrm[v]).ToArray(), pa.Select(_ => Vector2.Zero).ToArray(),
                           order.Select(v => ((string, float)[]?)s.Weights[v]).ToArray(), [(0u, st.ToArray())]));
        }
        if (extra != null) meshes.AddRange(extra);
        return SyntheticModel.BuildSurface([], [.. meshes]);
    }

    /// <summary>The one cloth mesh of a <see cref="Garment"/>, read back.</summary>
    private static SyntheticModel.SurfaceMesh MeshOf(byte[] garment)
    {
        var parts = ModelPartReader.Read(garment)!;
        var pos = Positions(garment);
        var w = Weights(garment);
        var tris = parts.Parts.Where(p => p.Island < 0).SelectMany(p => p.Triangles).ToArray();
        return new(Cloth, pos, [.. pos.Select(_ => Vector3.UnitY)], [.. pos.Select(_ => Vector2.Zero)],
                   [.. w.Select(x => ((string, float)[]?)[.. x])], [(0u, tris)]);
    }

    // ── the regions garments are cut from ──────────────────────────────────────────────────────────────────

    internal static bool Perineum(Part part, Vector3 _) => part == Part.Perineum;

    internal static Func<Part, Vector3, bool> Hips(float top)
        => (part, p) => part == Part.Hips && p.Y <= top + 1e-4f;

    internal static Func<Part, Vector3, bool> Legs(float bottom)
        => (part, p) => part is Part.LegL or Part.LegR && p.Y >= bottom - 1e-4f;

    internal static Func<Part, Vector3, bool> Leg(Part leg, float bottom, float top)
        => (part, p) => part == leg && p.Y >= bottom - 1e-4f && p.Y <= top + 1e-4f;

    /// <summary>Briefs: the hips up to 7 cm above the crotch, and the perineum.</summary>
    internal static Piece[] Briefs => [new(Hips(Crotch + 0.07f)), new(Perineum)];

    /// <summary>
    /// A refit pair from <paramref name="source"/> to <paramref name="target"/> on the legs slot, through the identity:
    /// the bodies share one topology.
    /// </summary>
    internal static BodyRetarget.SlotPair Pair(byte[] source, byte[] target)
    {
        var s = ModelPartReader.Read(source)!;
        var t = ModelPartReader.Read(target)!;
        Assert.True(IdentityCorrespondence.TryBuild(s, t, "_dwn", out var built, out string why), why);
        return new BodyRetarget.SlotPair("_dwn", built!, t, target, source);
    }

    /// <summary>The garment refitted across body mods onto <paramref name="target"/> (the same body by default), its
    /// weights planned and rebuilt — with the ease, or as builds before it did.</summary>
    internal static (BodyRetarget.WeightPlan Plan, byte[] Rebuilt, List<(string Bone, float W)>[] Weights) Refit(
        byte[] garment, bool ease = true, byte[]? source = null, byte[]? target = null, bool acrossBodies = true,
        IReadOnlySet<int>? held = null, IReadOnlyList<IReadOnlyCollection<int>>? keepShape = null)
    {
        source ??= Body();
        target ??= source;
        var pairs = new[] { Pair(source, target) };
        var plan = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoCrotchEase: !ease),
            () => BodyRetarget.PlanWeights(garment, pairs, acrossBodies, before: garment, held: held, keepShape: keepShape));
        Assert.NotNull(plan);
        var rebuilt = BodyRetarget.Rebuild(garment, pairs, swapSkin: false, plan, out var report)!;
        Assert.NotNull(rebuilt);
        Assert.Equal(0, report.Unplaced);
        return (plan!, rebuilt, Weights(rebuilt));
    }

    /// <summary>Every vertex's influences, by bone name, in the part reader's order.</summary>
    internal static List<(string Bone, float W)>[] Weights(byte[] mdl)
    {
        var skin = ModelSkinReader.Read(mdl, null, null)!;
        var all = new List<(string, float)>[skin.VertexCount];
        for (int v = 0; v < skin.VertexCount; v++)
        {
            all[v] = [];
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
            {
                float w = skin.BoneWeights[v * 8 + k];
                if (w > 0f) all[v].Add((skin.BoneNames[skin.BoneIndices[v * 8 + k]], w));
            }
        }
        return all;
    }

    /// <summary>A vertex's leg weight.</summary>
    internal static float Legs(List<(string Bone, float W)> w) => w.Where(i => BodyRetarget.IsLegBone(i.Bone)).Sum(i => i.W);

    /// <summary>Cloth vertex positions of a model, in the part reader's order.</summary>
    internal static Vector3[] Positions(byte[] mdl)
    {
        var parts = ModelPartReader.Read(mdl)!;
        return Enumerable.Range(0, parts.Positions.Length / 3)
                         .Select(v => new Vector3(parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]))
                         .ToArray();
    }

    /// <summary>
    /// The skeleton the body is rigged to, in model space, with both thighs turned at the hips by
    /// <paramref name="turn"/> (left thigh) and its mirror (right thigh): <see cref="Spread"/> or <see cref="Sit"/>.
    /// </summary>
    internal static XivLiveMesh.LivePose Pose(Func<bool, Matrix4x4> turn)
    {
        string[] names = ["n_root", "j_kosi", "j_sebo_a", "j_asi_a_l", "j_asi_a_r", "ya_daitai_phys_l", "ya_daitai_phys_r",
                          "j_asi_b_l", "j_asi_b_r", "iv_kintama"];
        int[] parents = [-1, 0, 1, 1, 1, 3, 4, 3, 4, 1];
        Vector3[] at = [Vector3.Zero, new(0, 0.95f, 0), new(0, 1.0f, 0), HipL, HipR, HipL + new Vector3(0, -0.1f, 0),
                        HipR + new Vector3(0, -0.1f, 0), new(LegX, Knee + 0.1f, 0), new(-LegX, Knee + 0.1f, 0),
                        new(0, Crotch - 0.03f, 0.03f)];
        var bind = at.Select(Matrix4x4.CreateTranslation).ToArray();
        var pose = (Matrix4x4[])bind.Clone();
        for (int b = 0; b < names.Length; b++)
        {
            bool left = names[b].EndsWith("_l", StringComparison.Ordinal);
            bool right = names[b].EndsWith("_r", StringComparison.Ordinal);
            if (!left && !right) continue;
            var hip = left ? HipL : HipR;
            pose[b] = bind[b] * Matrix4x4.CreateTranslation(-hip) * turn(left) * Matrix4x4.CreateTranslation(hip);
        }
        var live = new XivLiveMesh.LivePose();
        live.Set(names, parents, bind, pose, Matrix4x4.Identity, 201);
        return live;
    }

    /// <summary>Each thigh turned 30° out to its own side.</summary>
    internal static Matrix4x4 Spread(bool left) => Matrix4x4.CreateRotationZ(left ? MathF.PI / 6 : -MathF.PI / 6);

    /// <summary>Both thighs turned 90° forward, as sitting.</summary>
    internal static Matrix4x4 Sit(bool left) => Matrix4x4.CreateRotationX(-MathF.PI / 2);

    /// <summary>The model's vertices posed onto <paramref name="pose"/>.</summary>
    internal static Vector3[] Posed(byte[] mdl, XivLiveMesh.LivePose pose)
    {
        var mesh = ModelSkinReader.Read(mdl, null, null)!;
        var world = new Vector3[mesh.VertexCount];
        new XivLiveMesh.LiveMeshPoser().Pose(mesh, pose, null, world);
        return world;
    }

    /// <summary>Cloth triangles whose posed face turned more than a right angle from its bind face, or collapsed to
    /// nothing; with <paramref name="where"/>, only those all of whose corners it holds for.</summary>
    internal static int Folded(byte[] mdl, Vector3[] posed, Func<Vector3, bool>? where = null)
    {
        var parts = ModelPartReader.Read(mdl)!;
        var bind = Positions(mdl);
        int folded = 0;
        foreach (var part in parts.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (where != null && !(where(bind[a]) && where(bind[b]) && where(bind[c]))) continue;
                var n0 = Vector3.Cross(bind[b] - bind[a], bind[c] - bind[a]);
                var n1 = Vector3.Cross(posed[b] - posed[a], posed[c] - posed[a]);
                if (n0.LengthSquared() < 1e-14f) continue;
                if (n1.LengthSquared() < 1e-14f || Vector3.Dot(n0, n1) < 0f) folded++;
            }
        return folded;
    }
}
