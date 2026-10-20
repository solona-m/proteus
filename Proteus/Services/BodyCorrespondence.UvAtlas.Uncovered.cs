using System;
using System.Collections.Generic;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// A target that draws something else, not skin, where the source has skin: Muse+'s SFW legs cut the skin away from the
/// lower belly to the upper thighs and model boxers there instead — a mesh of their own, on a sheet of their own, in the
/// game's smallclothes material. 2,755 of Neolithe's 7,489 legs skin vertices then find no target skin by uv at all,
/// though not one of them lands anywhere wrong; counted as misses, the pair was refused as "two layouts" at 55%.
/// <para/>
/// A source point whose uv lands on no target skin, beside the boxers, is not a miss of the layout but skin the target
/// does not draw. It follows the skin around it, then lands on what the target draws there — its body as worn. Only for
/// one known layout, only for a pair the count refuses, and only for points that land NOWHERE: a uv that lands somewhere
/// far is the mark of another layout and is never excused.
/// </summary>
internal sealed partial class UvAtlasCorrespondence
{
    /// <summary>
    /// How far the target's smallclothes may be from where the carry puts a point with no skin, and still lie under it
    /// (40 mm): a body's boxers sit millimetres off where its skin would be, and two bodies differ by centimetres there.
    /// </summary>
    internal const float CoverReach = 0.04f;

    /// <summary>The share of the source's skin that must land on real skin, whatever the target draws elsewhere.</summary>
    internal const float MinLandedOnSkin = 0.5f;

    /// <summary>
    /// Within this distance of the midline (15 mm) a point or a target vertex has no side: the crotch and the butt cleft
    /// are drawn across it, and a narrower band left the boxers' crotch out of reach of both sides.
    /// </summary>
    internal const float SideBand = 0.015f;

    /// <summary>How many times a projection that turns a triangle over is relaxed node by node before its whole piece is
    /// undone.</summary>
    private const int FoldRounds = 3;

    /// <summary>The most of the excused points that may keep their carried move instead of landing on the body.</summary>
    internal const float MaxUnprojectedShare = 0.05f;

    /// <summary>How far a point kept at its carried move may be from the body as worn (10 mm).</summary>
    internal const float CarriedOnlyReach = 0.01f;

    /// <summary>
    /// Judges a same-layout pair the count refused by what the target draws where the source's skin missed, and either
    /// builds the correspondence — the points with no skin projected onto the target's smallclothes — or refuses it,
    /// saying whether the target lacks skin or the layout.
    /// </summary>
    private static bool Uncovered(ModelParts source, ModelParts target, string what, List<int> skin, Vector3?[] field,
                                  HashSet<int> noLanding, HashSet<int> nearLanded, int astray, Stats measured,
                                  out UvAtlasCorrespondence? correspondence, out string refusal, out Stats result)
    {
        correspondence = null;
        result = measured with { Astray = astray };
        int placeable = measured.Placeable;
        var nodes = SkinNodes.Build(source, skin);

        // A uv just past the gap's edge snaps onto its rim (Atlas.Land's second pass): beside skin with no landing, that
        // is a point of the gap squeezed onto the cut, not a landing.
        var noSkin = new HashSet<int>(noLanding);
        var gap = new bool[nodes.Count];
        foreach (int v in noLanding) gap[nodes.NodeOf[v]] = true;
        foreach (int v in nearLanded)
        {
            int n = nodes.NodeOf[v];
            bool beside = gap[n];
            foreach (int m in nodes.Ring1[n]) beside |= gap[m];
            if (!beside) continue;
            noSkin.Add(v);
            field[v] = null;
        }
        result = result with { NoLanding = noSkin.Count };

        float cell = BodySurface.CellFor(BodyRetarget.MeanEdgeOf(target));
        var cover = BodySurface.OfSmallclothes(target, cell);
        if (noSkin.Count == 0 || cover.IsEmpty) return Refuse(what, measured.VertexCoverage, out refusal);

        var landedBefore = new HashSet<int>();
        foreach (int v in skin)
            if (field[v] != null) landedBefore.Add(v);
        var coherence = Distrust(source, skin, [], field);
        var setAside = new HashSet<int>();
        foreach (int v in landedBefore)
            if (field[v] == null) setAside.Add(v);
        int trusted = landedBefore.Count - setAside.Count;

        // Where the skin around each unplaced point would put it; excused where the target's smallclothes lie there.
        var carried = (Vector3?[])field.Clone();
        var unplaced = new HashSet<int>();
        foreach (int v in skin)
            if (field[v] == null) unplaced.Add(v);
        Carry(source, carried, unplaced);

        var sides = Sides(target);
        var excused = new HashSet<int>();
        var reach = new List<float>();
        foreach (int v in noSkin)
        {
            if (carried[v] is not { } d) continue;
            var p = P(source, v);
            if (!cover.Nearest(p + d, CoverReach, out var hit, sides, -SideOf(p.X))) continue;
            excused.Add(v);
            reach.Add(hit.Distance);
        }
        reach.Sort();
        result = result with
        {
            Excused = excused.Count, Disagreed = coherence.Disagreed, Unvouched = coherence.Unvouched,
            Unjudged = coherence.Unjudged, P99Disagreement = coherence.P99Ratio, MaxDisagreement = coherence.MaxRatio,
            P99Cover = reach.Count > 0 ? reach[Math.Min(reach.Count - 1, (int)(reach.Count * 0.99f))] : 0f,
            MaxCover = reach.Count > 0 ? reach[^1] : 0f,
        };

        bool enough = trusted >= MinLandedOnSkin * placeable && trusted >= MinCoverage * (placeable - excused.Count);
        if (excused.Count == 0 || !enough)
            return excused.Count * 2 >= placeable - trusted
                ? RefuseNoSkin(what, measured.VertexCoverage, out refusal)
                : Refuse(what, measured.VertexCoverage, out refusal);

        foreach (int v in skin)
            if (excused.Contains(v) || setAside.Contains(v)) field[v] = carried[v];

        var projection = Project(source, target, skin, nodes, excused, setAside, field, sides, cell);
        result = result with
        {
            Unprojected = projection.Unprojected, OnSkin = projection.OnSkin,
            ComponentsReverted = projection.ComponentsReverted, FoldedElsewhere = projection.FoldedElsewhere,
            FoldedInIslands = projection.FoldedInIslands,
        };
        if (!projection.Fits) return RefuseNoSkin(what, measured.VertexCoverage, out refusal);

        correspondence = new UvAtlasCorrespondence(source, field,
            $"{what}: matched by texture coordinate, {measured.Landed:N0} of {placeable:N0} skin vertices " +
            $"({measured.VertexCoverage:P1}); {excused.Count:N0} where the target has no skin follow the skin around " +
            "them onto its smallclothes" +
            (projection.Unprojected > 0
                 ? $"; {projection.Unprojected:N0} of them kept where the skin put them, so no triangle turns over"
                 : "") +
            (result.Disagreed > 0
                 ? $"; {result.Disagreed:N0} whose landing disagreed with the skin around them follow it instead"
                 : "") +
            (result.Unvouched > 0 ? $"; {result.Unvouched:N0} that landed amid skin with no place follow it too" : ""),
            result, targetWorn: true);
        refusal = "";
        return true;
    }

    /// <summary>What the projection did, and whether the result may stand.</summary>
    private readonly record struct Projection(bool Fits, int Unprojected, int OnSkin, int ComponentsReverted,
                                              int FoldedElsewhere, int FoldedInIslands);

    /// <summary>
    /// Lands each excused point — weld twins together, from their averaged carried move — on the nearest point of the
    /// target as worn, on its own side. A landing that turns a source skin triangle over is undone node by node, then
    /// for its whole connected piece; a fold that still touches a moved point, too many points left at the carry, or one
    /// of them left off the body, and the fit does not stand.
    /// </summary>
    private static Projection Project(ModelParts source, ModelParts target, List<int> skin, SkinNodes nodes,
                                      HashSet<int> excused, HashSet<int> setAside, Vector3?[] field, sbyte[] sides,
                                      float cell)
    {
        var worn = BodySurface.OfBodyWorn(target, cell);
        var smallclothes = SmallclothesVertices(target);

        // Each node's excused members and their averaged carried move: what a reverted node goes back to.
        var carriedOf = new Dictionary<int, (List<int> Members, Vector3 Move)>();
        foreach (int v in excused)
        {
            int n = nodes.NodeOf[v];
            if (!carriedOf.TryGetValue(n, out var entry)) carriedOf[n] = entry = ([], Vector3.Zero);
            entry.Members.Add(v);
            carriedOf[n] = (entry.Members, entry.Move + field[v]!.Value);
        }

        var projected = new HashSet<int>();
        var onSkinNodes = new HashSet<int>();
        foreach (var (n, (members, sum)) in carriedOf)
        {
            var move = sum / members.Count;
            carriedOf[n] = (members, move);
            var p = P(source, members[0]);
            Vector3 landed = move;
            if (worn.Nearest(p + move, CoverReach, out var hit, sides, -SideOf(p.X)))
            {
                landed = hit.Point - p;
                projected.Add(n);
                if (!smallclothes[hit.A]) onSkinNodes.Add(n);
            }
            foreach (int v in members) field[v] = landed;
        }

        // A whole island of the source's skin with nothing landed — Neolithe's crotch detail, drawn on a patch of the
        // sheet where Muse has no skin at all — is pressed flat onto the smallclothes: a pocket laid on a smooth surface
        // turns half its triangles over by its shape alone (459 of the 493 folds measured on Muse). Such an island
        // follows the skin around it like one drawn off the sheet, and its folds are recorded, not judged.
        var tris = SkinTriangles(source);
        var pieceOf = ExcusedPieces(tris, excused);
        var detached = DetachedIslands(tris, excused);
        var judged = new List<(int A, int B, int C)>();
        int inIslands = 0;
        foreach (var t in tris)
            if (!detached.Contains(t.A) || !detached.Contains(t.B) || !detached.Contains(t.C)) judged.Add(t);
            else if (Folded(source, field, t.A, t.B, t.C)) inIslands++;

        void Revert(int n)
        {
            if (!projected.Remove(n)) return;
            var (members, move) = carriedOf[n];
            foreach (int v in members) field[v] = move;
        }

        // A landing that turns a triangle over moves to where its neighbours landed, averaged, and lands again: the fold
        // relaxes on the body instead of being pulled back to the carry, which can lie centimetres off it.
        for (int round = 0; round < FoldRounds; round++)
        {
            var folding = new HashSet<int>();
            foreach (var (a, b, c) in judged)
            {
                if (!Folded(source, field, a, b, c)) continue;
                foreach (int v in new[] { a, b, c })
                    if (excused.Contains(v) && projected.Contains(nodes.NodeOf[v])) folding.Add(nodes.NodeOf[v]);
            }
            DebugHook?.Invoke($"round {round}: folding nodes {folding.Count}");
            if (folding.Count == 0) break;

            var relaxed = new List<(int Node, Vector3 Move, bool Landed)>();
            foreach (int n in folding)
            {
                var sum = Vector3.Zero;
                int count = 0;
                foreach (int m in nodes.Ring1[n])
                    foreach (int w in nodes.Members[m])
                        if (field[w] is { } d) { sum += d; count++; }
                if (count == 0) continue;
                var p = P(source, nodes.Members[n][0]);
                var move = sum / count;
                bool landed = worn.Nearest(p + move, CoverReach, out var hit, sides, -SideOf(p.X));
                relaxed.Add((n, landed ? hit.Point - p : move, landed));
            }
            foreach (var (n, move, landed) in relaxed)
            {
                foreach (int v in carriedOf[n].Members) field[v] = move;
                if (!landed) projected.Remove(n);
            }
        }

        // Still folding: the whole connected piece of excused skin goes back to where the skin around it put it.
        int components = 0;
        var revertedPieces = new HashSet<int>();
        foreach (var (a, b, c) in judged)
        {
            if (!Folded(source, field, a, b, c)) continue;
            foreach (int v in new[] { a, b, c })
            {
                if (!excused.Contains(v) || !projected.Contains(nodes.NodeOf[v])) continue;
                int piece = pieceOf[v];
                if (!revertedPieces.Add(piece)) continue;
                components++;
                foreach (int w in excused)
                    if (pieceOf[w] == piece) Revert(nodes.NodeOf[w]);
            }
        }

        DebugHook?.Invoke($"pieces reverted {components}");
        bool fits = true;
        int elsewhere = 0;
        foreach (var (a, b, c) in judged)
        {
            if (!Folded(source, field, a, b, c)) continue;
            bool ours = excused.Contains(a) || excused.Contains(b) || excused.Contains(c)
                     || setAside.Contains(a) || setAside.Contains(b) || setAside.Contains(c);
            if (ours) { fits = false; DebugHook?.Invoke($"fold left at y {P(source, a).Y:F3}"); }
            else elsewhere++;
        }

        int unprojected = 0, onSkin = 0;
        foreach (int v in excused)
        {
            if (projected.Contains(nodes.NodeOf[v]))
            {
                if (onSkinNodes.Contains(nodes.NodeOf[v])) onSkin++;
                continue;
            }
            unprojected++;
            if (!worn.Nearest(P(source, v) + field[v]!.Value, CarriedOnlyReach, out _)) { fits = false; DebugHook?.Invoke("carried far"); }
        }
        if (unprojected > MaxUnprojectedShare * excused.Count) fits = false;

        return new Projection(fits, unprojected, onSkin, components, elsewhere, inIslands);
    }

    /// <summary>
    /// The excused vertices on islands of the source's skin — connected by triangle, so split from the rest by a uv
    /// seam — every vertex of which is excused: skin of a patch of the sheet where the target has none at all.
    /// </summary>
    private static HashSet<int> DetachedIslands(List<(int A, int B, int C)> tris, HashSet<int> excused)
    {
        var parent = new Dictionary<int, int>();
        int Root(int v)
        {
            if (!parent.TryGetValue(v, out int p)) return parent[v] = v;
            while (p != v) { v = p; p = parent[v]; }
            return v;
        }
        void Join(int a, int b)
        {
            int ra = Root(a), rb = Root(b);
            if (ra != rb) parent[rb] = ra;
        }
        foreach (var (a, b, c) in tris)
        {
            Join(a, b);
            Join(b, c);
        }

        var whole = new Dictionary<int, bool>();
        foreach (var (a, b, c) in tris)
            foreach (int v in new[] { a, b, c })
            {
                int r = Root(v);
                whole[r] = whole.GetValueOrDefault(r, true) && excused.Contains(v);
            }
        var detached = new HashSet<int>();
        foreach (int v in excused)
            if (parent.ContainsKey(v) && whole[Root(v)]) detached.Add(v);
        return detached;
    }

    internal static Action<string>? DebugHook;

    private static Vector3 P(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>Which side of the body a point is on: +1 for the character's +X, 0 within <see cref="SideBand"/>.</summary>
    private static int SideOf(float x) => x > SideBand ? 1 : x < -SideBand ? -1 : 0;

    /// <summary>Per target vertex, its side (<see cref="SideOf"/>), for <see cref="BodySurface.Nearest"/>'s avoid rule.</summary>
    private static sbyte[] Sides(ModelParts target)
    {
        var sides = new sbyte[target.Positions.Length / 3];
        for (int v = 0; v < sides.Length; v++) sides[v] = (sbyte)SideOf(target.Positions[v * 3]);
        return sides;
    }

    /// <summary>Per target vertex, whether it belongs to the body's own smallclothes rather than its skin.</summary>
    private static bool[] SmallclothesVertices(ModelParts target)
    {
        int vc = target.Positions.Length / 3;
        var marks = new bool[vc];
        foreach (var part in target.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)
                || !BodySurface.IsSmallclothes(part.Material)) continue;
            foreach (int v in part.Triangles)
                if (v >= 0 && v < vc) marks[v] = true;
        }
        return marks;
    }

    /// <summary>The source's skin triangles, valid corners only.</summary>
    private static List<(int A, int B, int C)> SkinTriangles(ModelParts source)
    {
        int vc = source.Positions.Length / 3;
        var tris = new List<(int, int, int)>();
        foreach (var part in source.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
                tris.Add((a, b, c));
            }
        }
        return tris;
    }

    /// <summary>A skin triangle turned over by the field: its moved normal points against its own. Unmoved corners, and
    /// triangles with no area to begin with, never count.</summary>
    private static bool Folded(ModelParts source, Vector3?[] field, int a, int b, int c)
    {
        if (field[a] is not { } da || field[b] is not { } db || field[c] is not { } dc) return false;
        Vector3 pa = P(source, a), pb = P(source, b), pc = P(source, c);
        var before = Vector3.Cross(pb - pa, pc - pa);
        if (before.LengthSquared() < 1e-20f) return false;
        var after = Vector3.Cross(pb + db - (pa + da), pc + dc - (pa + da));
        return Vector3.Dot(before, after) <= 0f;
    }

    /// <summary>The connected pieces of excused skin, joined by triangle: each excused vertex's piece.</summary>
    private static Dictionary<int, int> ExcusedPieces(List<(int A, int B, int C)> tris, HashSet<int> excused)
    {
        var parent = new Dictionary<int, int>();
        foreach (int v in excused) parent[v] = v;
        int Root(int v)
        {
            while (parent[v] != v) v = parent[v] = parent[parent[v]];
            return v;
        }
        foreach (var (a, b, c) in tris)
        {
            if (excused.Contains(a) && excused.Contains(b)) parent[Root(b)] = Root(a);
            if (excused.Contains(b) && excused.Contains(c)) parent[Root(c)] = Root(b);
            if (excused.Contains(a) && excused.Contains(c)) parent[Root(c)] = Root(a);
        }
        var pieceOf = new Dictionary<int, int>();
        foreach (int v in excused) pieceOf[v] = Root(v);
        return pieceOf;
    }
}
