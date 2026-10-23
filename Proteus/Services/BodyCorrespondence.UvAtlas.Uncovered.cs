using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>How far inside the target's smallclothes (1 mm) a carried point must lie to count as pressed out onto them.</summary>
    private const float PressedDepth = 0.001f;

    /// <summary>The share of the source's skin that must land on real skin, whatever the target draws elsewhere.</summary>
    internal const float MinLandedOnSkin = 0.5f;

    /// <summary>
    /// Within this distance of the midline (15 mm) a point or a target vertex has no side: the crotch and the butt cleft
    /// are drawn across it, and a narrower band left the boxers' crotch out of reach of both sides.
    /// </summary>
    internal const float SideBand = 0.015f;

    /// <summary>How many times a projection that turns a triangle over is relaxed node by node before its whole piece is
    /// undone.</summary>
    private const int FoldRounds = 8;

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
            FoldedElsewhere = projection.FoldedElsewhere,
            FoldedInIslands = projection.FoldedInIslands,
            FoldedSetAside = projection.FoldedSetAside, FoldedTouching = projection.FoldedTouching,
        };
        if (!projection.Fits) return RefuseNoSkin(what, measured.VertexCoverage, out refusal);

        correspondence = new UvAtlasCorrespondence(source, field,
            $"{what}: matched by texture coordinate, {measured.Landed:N0} of {placeable:N0} skin vertices " +
            $"({measured.VertexCoverage:P1}); {excused.Count:N0} where the target has no skin follow the skin around " +
            "them onto its smallclothes" +
            (projection.Unprojected > 0
                 ? $"; {projection.Unprojected:N0} of them kept where the skin put them, so no triangle turns over"
                 : "") +
            (projection.FoldedSetAside > 0
                 ? $"; {projection.FoldedSetAside:N0} whose landing turned the skin over were filled from their neighbours"
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
    /// <param name="FoldedSetAside">Moved points whose landing still turned a skin triangle over after the relax: left
    /// with no landing, filled from their neighbours downstream — never a reason to refuse the pair.</param>
    /// <param name="FoldedTouching">The skin triangles those folds were.</param>
    private readonly record struct Projection(bool Fits, int Unprojected, int OnSkin, int FoldedElsewhere,
                                              int FoldedInIslands, int FoldedSetAside = 0, int FoldedTouching = 0);

    /// <summary>
    /// Lands each excused point — weld twins together, from their averaged carried move — on the nearest point of the
    /// target as worn, on its own side. A landing that turns a source skin triangle over is relaxed toward its
    /// neighbours; the points a fold still touches after that are left with no landing (filled from their neighbours
    /// downstream) and counted, as are those the relax kept off the body. One of those further off it than
    /// <see cref="CarriedOnlyReach"/>, and the fit does not stand — never a fold, nor how many folds kept off it.
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
        var normalOf = new Dictionary<int, Vector3>();
        var pressed = new HashSet<int>();
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
                normalOf[n] = hit.Normal;
                if (Vector3.Dot(p + move - hit.Point, hit.Normal) < -PressedDepth) pressed.Add(n);
                if (!smallclothes[hit.A]) onSkinNodes.Add(n);
            }
            foreach (int v in members) field[v] = landed;
        }

        // A whole island of the source's skin with nothing landed — Neolithe's crotch detail, drawn on a patch of the
        // sheet where Muse has no skin at all — is pressed flat onto the smallclothes: a pocket laid on a smooth surface
        // turns half its triangles over by its shape alone (459 of the 493 folds measured on Muse). Such an island
        // follows the skin around it like one drawn off the sheet, and its folds are recorded, not judged.
        var tris = SkinTriangles(source);
        var detached = DetachedIslands(tris, excused);
        var judged = new List<(int A, int B, int C)>();
        int inIslands = 0;
        foreach (var t in tris)
            if (!detached.Contains(t.A) || !detached.Contains(t.B) || !detached.Contains(t.C)) judged.Add(t);
            else if (Folded(source, field, t.A, t.B, t.C)) inIslands++;

        // Skin the target's smallclothes bridge (the butt cleft, under the seat of Muse's boxers) lies inside them and
        // is pressed out onto them: its walls meet on one smooth surface and cross there by their shape alone, every
        // point on the body. A triangle every corner of which was pressed out is recorded like the islands, not judged.
        bool Pressed(int a, int b, int c)
            => pressed.Contains(nodes.NodeOf[a]) && pressed.Contains(nodes.NodeOf[b]) && pressed.Contains(nodes.NodeOf[c])
               && excused.Contains(a) && excused.Contains(b) && excused.Contains(c);

        // Turned over: for a triangle laid on the body (every corner landed), facing into it where it lies — a cleft's
        // wall laid flat on the seat of the boxers faces sideways before and back after, and against its own normal
        // reads as turned over by noise; for any other, against its own normal.
        bool TurnedOver(int a, int b, int c)
        {
            if (normalOf.TryGetValue(nodes.NodeOf[a], out var na) && normalOf.TryGetValue(nodes.NodeOf[b], out var nb)
                && normalOf.TryGetValue(nodes.NodeOf[c], out var nc)
                && excused.Contains(a) && excused.Contains(b) && excused.Contains(c))
            {
                Vector3 pa = P(source, a) + field[a]!.Value, pb = P(source, b) + field[b]!.Value, pc = P(source, c) + field[c]!.Value;
                return Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), na + nb + nc) <= 0f;
            }
            return Folded(source, field, a, b, c);
        }

        // The excused nodes a triangle turned over touches.
        HashSet<int> Folding()
        {
            var folding = new HashSet<int>();
            foreach (var (a, b, c) in judged)
            {
                if (Pressed(a, b, c) || !TurnedOver(a, b, c)) continue;
                foreach (int v in new[] { a, b, c })
                    if (excused.Contains(v)) folding.Add(nodes.NodeOf[v]);
            }
            return folding;
        }

        // A node's neighbours' moves, averaged: where the skin around it says it goes.
        Vector3? Around(int n)
        {
            var sum = Vector3.Zero;
            int count = 0;
            foreach (int m in nodes.Ring1[n])
                foreach (int w in nodes.Members[m])
                    if (field[w] is { } d) { sum += d; count++; }
            return count > 0 ? sum / count : null;
        }

        // A landing that turns a triangle over moves to where its neighbours landed, averaged, and lands again: the fold
        // relaxes on the body instead of being pulled back to the carry, which can lie centimetres off it. What still
        // folds after that relaxes the same way off the body — a sliver between landings that cross — and is counted.
        for (int round = 0; round < 2 * FoldRounds; round++)
        {
            var folding = Folding();
            if (folding.Count == 0) break;

            // The crossing is between neighbours (the butt cleft's two walls pressed onto one smooth seat), so the
            // whole neighbourhood relaxes, not just the node a fold touches.
            foreach (int n in folding.ToArray())
                foreach (int m in nodes.Ring1[n])
                    if (carriedOf.ContainsKey(m)) folding.Add(m);
            bool land = round < FoldRounds;
            var relaxed = new List<(int Node, Vector3 Move, Vector3? Normal)>();
            foreach (int n in folding)
            {
                if (Around(n) is not { } move) continue;
                var p = P(source, nodes.Members[n][0]);
                BodySurface.Hit hit = default;
                bool landed = land && worn.Nearest(p + move, CoverReach, out hit, sides, -SideOf(p.X));
                relaxed.Add((n, landed ? hit.Point - p : move, landed ? hit.Normal : null));
            }
            foreach (var (n, move, normal) in relaxed)
            {
                foreach (int v in carriedOf[n].Members) field[v] = move;
                if (normal is { } landedNormal) normalOf[n] = landedNormal;
                else
                {
                    projected.Remove(n);
                    normalOf.Remove(n);
                }
            }
        }

        // A fold that still touches a moved point is never a reason to refuse the pair: those points are left with no
        // landing — every one found first, then cleared, so no fold is judged against a landing already taken away —
        // and filled from the skin around them downstream, like any other point that landed nowhere.
        bool fits = true;
        var stillFolded = new List<(int A, int B, int C)>();
        foreach (var (a, b, c) in judged)
        {
            if (!TurnedOver(a, b, c)) continue;
            if (Pressed(a, b, c) && projected.Contains(nodes.NodeOf[a]) && projected.Contains(nodes.NodeOf[b])
                && projected.Contains(nodes.NodeOf[c]))
            {
                inIslands++;
                continue;
            }
            stillFolded.Add((a, b, c));
        }
        var moved = new HashSet<int>(excused);
        moved.UnionWith(setAside);
        var (foldedSetAside, touching, elsewhere) = SetAsideFolds(
            stillFolded, nodes.NodeOf, n => carriedOf.TryGetValue(n, out var carry) ? carry.Members : nodes.Members[n],
            moved, field, projected, normalOf);

        int unprojected = 0, onSkin = 0;
        foreach (int v in excused)
        {
            if (field[v] == null) continue;   // set aside above: filled downstream, neither placed nor kept
            if (projected.Contains(nodes.NodeOf[v]))
            {
                if (onSkinNodes.Contains(nodes.NodeOf[v])) onSkin++;
                continue;
            }
            unprojected++;
            if (!worn.Nearest(P(source, v) + field[v]!.Value, CarriedOnlyReach, out _)) fits = false;
        }
        // How many were kept off the body is counted, never a refusal: a point is kept there only so that no triangle
        // turns over, and nothing is refused for folds (cross-sex-tops-yas). How far off it is still is (above).

        return new Projection(fits, unprojected, onSkin, elsewhere, inIslands, foldedSetAside, touching);
    }

    /// <summary>
    /// The points a fold still touches after the relax, left with no landing: every node a still-folded triangle's moved
    /// corner belongs to is found first, then cleared — taken out of <paramref name="projected"/> and
    /// <paramref name="normalOf"/>, its moved members' fields set to null — so no fold is judged against a landing already
    /// taken away, and the result does not depend on the order of the triangles. Never a reason to refuse the pair.
    /// </summary>
    /// <param name="folded">Skin triangles still turned over (pressed-out islands already left out).</param>
    /// <param name="membersOf">A node's vertices.</param>
    /// <param name="moved">The excused and set-aside vertices: those whose field the projection moved.</param>
    /// <returns>Vertices left with no landing; folded triangles touching a moved point; and those touching none.</returns>
    internal static (int SetAside, int Touching, int Elsewhere) SetAsideFolds(
        IEnumerable<(int A, int B, int C)> folded, int[] nodeOf, Func<int, IReadOnlyList<int>> membersOf,
        IReadOnlySet<int> moved, Vector3?[] field, ISet<int> projected, IDictionary<int, Vector3> normalOf)
    {
        int touching = 0, elsewhere = 0;
        var foldedNodes = new HashSet<int>();
        foreach (var (a, b, c) in folded)
        {
            bool ours = false;
            foreach (int v in new[] { a, b, c })
                if (moved.Contains(v)) { foldedNodes.Add(nodeOf[v]); ours = true; }
            if (ours) touching++;
            else elsewhere++;
        }
        int setAside = 0;
        foreach (int n in foldedNodes)
        {
            projected.Remove(n);
            normalOf.Remove(n);
            foreach (int v in membersOf(n))
                if (moved.Contains(v) && field[v] != null)
                {
                    field[v] = null;
                    setAside++;
                }
        }
        return (setAside, touching, elsewhere);
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
}
