using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Proteus.Services.Bulge;

/// <summary>
/// Which parts of a legs body are its genitals, and whether it draws a penis — the featureless bulge's first question
/// (plan featureless-bulge, step 2). Fixed from the S0.1 inventory (<c>BulgeSpikeLocalTests.Genital_inventory</c>):
/// <list type="bullet">
/// <item>The Body SE draws its penis as an untagged skin submesh of its own, wholly at the crotch, on no genital bone
/// (Gen A <c>0.3</c>, 1770 triangles, standing 73 mm off the rest of the skin).</item>
/// <item>TBSE-X does the same on IVCS penis bones (<c>iv_funyachin_*</c>, <c>iv_kintama_*</c>), plus a <c>_dwn_c</c>
/// mesh rigged wholly to the penis; its erect options merge the genitals into the main skin submesh, rigged to
/// <c>iv_ochinko_*</c> and <c>iv_kougan_*</c>.</item>
/// <item>Women's bodies with a penis draw it as a material of its own (<c>_benis</c>, <c>_neolithe_penis</c>,
/// <c>_hotdog</c>, <c>_tre_trans</c>), standing 56–156 mm off the skin; Muse+ as extra skin submeshes on IVCS bones.</item>
/// <item>Pubic hair stands at most 14 mm off a body without a penis (30 mm over Tre's socket) and is not a penis — unless
/// rigged to it (Nyl's Hotdog's <c>_hotdogpube</c>, 43% on the testicles: its scrotum).</item>
/// </list>
/// </summary>
internal static class GenitalParts
{
    /// <summary>How far a genital part must stand off the rest of the skin to be a penis (30 mm). Measured: every penis
    /// part ≥ 55.9 mm (Rosaline's); pubic hair on a body without a penis ≤ 8.7 mm, and ≤ 29.5 mm anywhere (Tre).</summary>
    internal const float PenisProtrusion = 0.030f;

    /// <summary>The share of a vertex's weight on penis bones that makes it the penis's (50%).</summary>
    internal const float PenisWeight = 0.5f;

    /// <summary>The share of a pubic-hair part's weight on penis bones that makes it part of the genitals (30%):
    /// <c>_hotdogpube</c> 43%; Bibo+'s <c>_bibopube</c> 23%, every other pube under 6%.</summary>
    internal const float PubeOnPenis = 0.3f;

    /// <summary>The share of an untagged skin submesh's vertices in the window that makes it the genitals' own (95%).</summary>
    internal const float WhollyInWindow = 0.95f;

    /// <summary>The penis and testicle bones: TBSE-X's and Bibo+'s IVCS names (funyachin, ochinko the shaft; kintama,
    /// kougan the testicles). Not the anus (koumon), the vulva (inshin, kuritto, omanko) nor the buttocks (shiri).</summary>
    internal static bool IsPenisBone(string bone)
        => bone.Contains("funyachin", StringComparison.Ordinal) || bone.Contains("kintama", StringComparison.Ordinal)
        || bone.Contains("ochinko", StringComparison.Ordinal) || bone.Contains("kougan", StringComparison.Ordinal);

    private static readonly Regex Pube = new("pube", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Piercing = new("pierc", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Nails = new("nail", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>One genital part: the part, and which of its triangles (by index into its triangle list, in threes) are
    /// genital — all of them, except for a skin submesh the genitals are merged into (rule b).</summary>
    internal sealed record Genital(ModelPart Part, string Rule, IReadOnlyList<int> Triangles, float Protrusion);

    /// <summary>What a body's crotch holds.</summary>
    /// <param name="Anchor">The crotch, from the body without its genital candidates.</param>
    /// <param name="Socket">The crotch came from the nearest pair of thigh skin, the body having no skin on both thighs.</param>
    internal sealed record Reading(BodyRetarget.CrotchAnchor Anchor, bool Socket, IReadOnlyList<Genital> Genitals, bool Penis);

    /// <summary>The crotch window about an anchor: |x−MidX| ≤ 8 cm·s, y −8…+16 cm·s, in front of the anchor's depth − 4 cm·s.</summary>
    internal static bool InWindow(Vector3 p, BodyRetarget.CrotchAnchor a)
        => MathF.Abs(p.X - a.MidX) <= 0.08f * a.Scale && p.Y >= a.Y - 0.08f * a.Scale && p.Y <= a.Y + 0.16f * a.Scale
           && p.Z >= a.At.Z - 0.04f * a.Scale;

    internal static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>A vertex's weight on penis bones.</summary>
    internal static float OnPenis(XivLiveMesh.SkinnedMesh skin, int v)
    {
        float w = 0f;
        if (v < 0 || v >= skin.VertexCount) return 0f;
        for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
        {
            float x = skin.BoneWeights[v * 8 + k];
            if (x > 0f && IsPenisBone(skin.BoneNames[skin.BoneIndices[v * 8 + k]])) w += x;
        }
        return w;
    }

    /// <summary>
    /// Read <paramref name="body"/>'s genitals. Null when it has no crotch to read them about. The body is read as given:
    /// parts its options hide are already out of it (<see cref="BodyRetarget.Without"/>).
    /// </summary>
    internal static Reading? Read(ModelParts body, XivLiveMesh.SkinnedMesh skin)
    {
        // Pass one: a provisional crotch — FindCrotch, or the socket's for a body with no skin on both thighs.
        bool socket = false;
        var provisional = BodyRetarget.FindCrotch([(body, skin)]);
        if (provisional == null) { provisional = SocketCrotch(body, skin); socket = true; }
        if (provisional is not { } a0) return null;

        var candidates = Candidates(body, skin, a0);
        // Pass two (R12): the crotch of the body without its skin candidates, which FindCrotch would otherwise read as
        // crotch skin; the window and the protrusions are measured about it.
        var skinCandidates = candidates.Where(c => c.Rule is "c" or "b").Select(c => c.Part).ToHashSet();
        var without = WithoutParts(body, skinCandidates);
        var anchor = a0;
        if (skinCandidates.Count > 0 && !socket && BodyRetarget.FindCrotch([(without, skin)]) is { } a1)
        {
            anchor = a1;
            candidates = Candidates(body, skin, anchor);
        }

        // Protrusion of each candidate off the skin that is no candidate: a penis stands well off it.
        var grid = PlainSkin(body, candidates);
        var genitals = new List<Genital>();
        foreach (var c in candidates)
            genitals.Add(c with { Protrusion = Protrusion(body, c.Triangles.SelectMany(t => new[] { c.Part.Triangles[t * 3],
                                                                    c.Part.Triangles[t * 3 + 1], c.Part.Triangles[t * 3 + 2] }), grid, anchor) });

        bool penis = genitals.Any(g => g.Rule == "b" || g.Protrusion > PenisProtrusion
                                       || g.Part.Triangles.Distinct().Average(v => OnPenis(skin, v)) >= PenisWeight);
        return new Reading(anchor, socket, genitals, penis);
    }

    /// <summary>The body's skin that is no genital piece (a piece merged into the skin, rule b, stays in it): what a
    /// penis stands off.</summary>
    internal static PointGrid PlainSkin(ModelParts body, IReadOnlyList<Genital> genitals)
        => new(body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)
                                     && !genitals.Any(c => c.Part == p && c.Rule != "b"))
                         .SelectMany(p => p.Triangles).Distinct().Select(v => At(body, v)).ToList(), 2f * ProtrusionReach / 3f);

    /// <summary>How far a protrusion is measured out to (<see cref="PenisProtrusion"/> and 1 mm): it is only ever compared
    /// with that, and the search for a point standing far off scans every cell out to its reach.</summary>
    internal const float ProtrusionReach = PenisProtrusion + 0.001f;

    /// <summary>How far the farthest of <paramref name="verts"/> inside the crotch window stands off
    /// <paramref name="plain"/>, up to <see cref="ProtrusionReach"/>; 0 when none is in the window.</summary>
    internal static float Protrusion(ModelParts m, IEnumerable<int> verts, PointGrid plain, BodyRetarget.CrotchAnchor a)
    {
        float protrusion = 0f;
        foreach (int v in verts)
        {
            var p = At(m, v);
            if (InWindow(p, a)) protrusion = MathF.Max(protrusion, plain.Nearest(p, ProtrusionReach));
        }
        return protrusion;
    }

    /// <summary>The parts that are genital by rule (a), (b) or (c), about <paramref name="a"/>.</summary>
    private static List<Genital> Candidates(ModelParts body, XivLiveMesh.SkinnedMesh skin, BodyRetarget.CrotchAnchor a)
    {
        var found = new List<Genital>();
        var genitalMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Parts.Where(p => p.Island < 0))
        {
            var vs = part.Triangles.Distinct().ToArray();
            if (vs.Length == 0) continue;
            float inWindow = vs.Count(v => InWindow(At(body, v), a)) / (float)vs.Length;
            float onPenis = vs.Average(v => OnPenis(skin, v));
            var all = Enumerable.Range(0, part.Triangles.Length / 3).ToList();
            bool skinPart = SecondSkinWriter.IsBodySkinMaterial(part.Material);
            // The shipped bulge, drawn in place of a body's genitals, is not genitals: a body made featureless reads so.
            if (skinPart && IsShippedBulge(body, part)) continue;
            if (!skinPart)
            {
                // (a) a material of its own at the crotch: not nails, piercings or pubic hair (unless rigged to the penis),
                // and not smallclothes unless rigged to the penis (TBSE-X's _dwn_c).
                if (inWindow < 0.5f || Nails.IsMatch(part.Material) || Piercing.IsMatch(part.Material)) continue;
                bool pube = Pube.IsMatch(part.Material);
                bool smallclothes = BodySurface.IsSmallclothes(part.Material);
                if (pube && onPenis < PubeOnPenis) continue;
                if (smallclothes && onPenis < PenisWeight) continue;
                found.Add(new Genital(part, "a", all, 0f));
                genitalMaterials.Add(part.Material);
                continue;
            }
            // (c) an untagged skin submesh wholly at the crotch.
            if (part.AttributeMask == 0 && inWindow >= WhollyInWindow) { found.Add(new Genital(part, "c", all, 0f)); continue; }
            // A tagged skin submesh wholly at the crotch and rigged to the penis: a penis an option switches (Muse+'s atr_dv_*).
            if (inWindow >= WhollyInWindow && onPenis >= PenisWeight) { found.Add(new Genital(part, "c", all, 0f)); continue; }
            // (b) the penis merged into a skin submesh: its triangles rigged to the penis, inside the window.
            var merged = all.Where(t => Enumerable.Range(0, 3).All(k =>
            {
                int v = part.Triangles[t * 3 + k];
                return OnPenis(skin, v) >= PenisWeight && InWindow(At(body, v), a);
            })).ToList();
            if (merged.Count > 0) found.Add(new Genital(part, "b", merged, 0f));
        }
        // Every other submesh of a genital material at the crotch is the same genitals (Tre's small _tre_trans piece).
        foreach (var part in body.Parts.Where(p => p.Island < 0 && genitalMaterials.Contains(p.Material) && found.All(g => g.Part != p)))
            found.Add(new Genital(part, "a", Enumerable.Range(0, part.Triangles.Length / 3).ToList(), 0f));
        return found;
    }

    /// <summary>Whether a skin part is the shipped TBSE bulge as <see cref="BulgeSwap"/> draws it: as many drawn vertices and
    /// triangles as the bulge (the collapsed spares aside), every drawn vertex within <see cref="BulgeSwap.HoleFit"/> of one
    /// of its points (it may be warped onto a body's hole by that much).</summary>
    internal static bool IsShippedBulge(ModelParts body, ModelPart part)
    {
        if (TbseBulge.Shared is not { } bulge) return false;
        var drawn = new HashSet<int>();
        for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
        {
            int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
            if (a == b || b == c || a == c) continue;
            drawn.Add(a); drawn.Add(b); drawn.Add(c);
        }
        int drawnTris = 0;
        for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            if (part.Triangles[t] != part.Triangles[t + 1] && part.Triangles[t + 1] != part.Triangles[t + 2] && part.Triangles[t] != part.Triangles[t + 2])
                drawnTris++;
        if (drawn.Count != bulge.Positions.Length || drawnTris != bulge.Data.triangles.Length / 3) return false;
        var grid = new PointGrid(bulge.Positions, 0.01f);
        return drawn.All(v => grid.Nearest(At(body, v), BulgeSwap.HoleFit * 2) <= BulgeSwap.HoleFit);
    }

    private static ModelParts WithoutParts(ModelParts model, IReadOnlySet<ModelPart> drop)
        => drop.Count == 0 ? model : new ModelParts
        {
            Positions = model.Positions,
            Normals = model.Normals,
            MeshSpans = model.MeshSpans,
            Parts = model.Parts.Where(p => !drop.Contains(p)).ToList(),
            AttributeNames = model.AttributeNames,
            Min = model.Min,
            Max = model.Max,
            ShatteredSubmeshes = model.ShatteredSubmeshes,
        };

    /// <summary>
    /// A crotch for a body whose crotch skin is a socket (Bibo+ common/75 under its <c>_benis</c>, briefs over a hole):
    /// no skin vertex carries both thighs, so <see cref="BodyRetarget.FindCrotch"/> finds none. The nearest pair of a
    /// left-thigh and a right-thigh skin vertex near the top of the thighs stands in, its midpoint the crotch.
    /// </summary>
    internal static BodyRetarget.CrotchAnchor? SocketCrotch(ModelParts body, XivLiveMesh.SkinnedMesh skin)
    {
        var left = new List<Vector3>();
        var right = new List<Vector3>();
        foreach (var part in body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach (int v in part.Triangles.Distinct())
            {
                if (v < 0 || v >= skin.VertexCount) continue;
                float wl = 0f, wr = 0f;
                for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                {
                    float w = skin.BoneWeights[v * 8 + k];
                    if (w <= 0f) continue;
                    string b = skin.BoneNames[skin.BoneIndices[v * 8 + k]];
                    if (!BodyRetarget.IsThighBone(b)) continue;
                    if (b.EndsWith("_l", StringComparison.Ordinal)) wl += w; else wr += w;
                }
                var p = At(body, v);
                if (wl >= BodyRetarget.CrotchAnchorThigh && p.X > 0f) left.Add(p);
                if (wr >= BodyRetarget.CrotchAnchorThigh && p.X < 0f) right.Add(p);
            }
        if (left.Count == 0 || right.Count == 0) return null;
        float topL = left.Max(p => p.Y), topR = right.Max(p => p.Y);
        var l = left.Where(p => p.Y > topL - 0.12f).ToList();
        var r = right.Where(p => p.Y > topR - 0.12f).ToList();
        (Vector3 A, Vector3 B, float D) best = (default, default, float.MaxValue);
        foreach (var x in l)
            foreach (var y in r)
            {
                float d = Vector3.DistanceSquared(x, y);
                if (d < best.D) best = (x, y, d);
            }
        var at = (best.A + best.B) / 2;
        float scale = Math.Clamp(at.Y / BodyRetarget.CrotchReferenceHeight, 0.4f, 1.5f);
        return new BodyRetarget.CrotchAnchor(at.Y, at.X, scale, at);
    }

    /// <summary>Points bucketed in cubes, for the nearest distance to any of them.</summary>
    internal sealed class PointGrid(IReadOnlyList<Vector3> points, float cell)
    {
        private readonly Dictionary<(int, int, int), List<Vector3>> cells = points
            .GroupBy(p => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell)))
            .ToDictionary(g => g.Key, g => g.ToList());

        /// <summary>The distance to the nearest point, searched out to <paramref name="reach"/>; reach when none.</summary>
        public float Nearest(Vector3 p, float reach)
        {
            var c = ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
            float best = float.MaxValue;
            int rings = (int)MathF.Ceiling(reach / cell);
            for (int r = 0; r <= rings && best > (r - 1) * cell; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        // Only the ring's own shell: every dz on the x or y faces, the two z faces elsewhere.
                        bool side = Math.Abs(dx) == r || Math.Abs(dy) == r;
                        for (int dz = side ? -r : -r; dz <= r; dz += side || r == 0 ? 1 : 2 * r)
                        {
                            if (!cells.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var list)) continue;
                            foreach (var q in list) best = MathF.Min(best, Vector3.Distance(p, q));
                        }
                    }
            return MathF.Min(best, reach);
        }
    }
}
