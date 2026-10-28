using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// How far from the garment's skin, as laid on the new body, the tuck may carry the new body's skin (50 mm). The
    /// game's Dirndl's Bodice draws its chest skin to the neckline and turns it in under the ruffle; laid onto Neolithe's
    /// Almond XS that lip flattens onto the top of the bust, and the bust under the neckline lies 10-49 mm from it.
    /// </summary>
    internal const float TuckBand = 0.05f;

    /// <summary>How far off the skin the garment's cloth over it may stand and still hide it (60 mm).</summary>
    internal const float TuckOver = 0.06f;

    /// <summary>
    /// How much room the cloth must leave over skin the tuck draws (3 mm), on every face alike — a rim's or a ruffle's
    /// too. <see cref="PullClearRim"/>'s quarter millimetre suits one triangle under a lifted hem; drawn on under a rim,
    /// skin that close showed through it as dark notches (see <see cref="PullToEdge"/>), and a body moves.
    /// </summary>
    internal const float TuckClear = 0.003f;

    /// <summary>How far behind the skin cloth is looked for (20 mm): skin drawn over buried cloth is through it. Past
    /// <see cref="ClearDepth"/> and the 15 mm the game buries its tops' cloth in its own body.</summary>
    internal const float TuckBehind = 0.02f;

    /// <summary>The share of a vertex's weight on arm and hand bones above which it is the arm, not the torso (25%):
    /// a sleeve and the arm in it move on different bones, and room measured standing says little about either.</summary>
    internal const float TuckArmShare = 0.25f;

    /// <summary>
    /// How far under the garment's laid skin, as a share of the chord it spans there (see <see cref="ChordUnder"/>), the
    /// new body may dip in the open — no cloth over it — and still be drawn (a half). The author drew skin across it, so
    /// the body belongs there; the cut keeps it only within <see cref="CutSagShare"/>, but in the automatic refit on a slot
    /// the garment only reaches onto (see <see cref="GrowSpannedOpenSkin"/>). Measured in game: Dirndl's Bodice on Neolithe's Almond XS left
    /// the cleavage open, its vertices 8-11 mm under a 27 mm chord (0.30-0.39) of the game's coarse chest skin, a hole
    /// straight through to the bodice's inside.
    /// </summary>
    internal const float TuckSpanShare = 0.5f;

    /// <summary>Whether a landing on the garment's laid skin is one it spans (see <see cref="TuckSpanShare"/>): inside one of
    /// its triangles or on an edge two share, no further off — either side — than that share of the chord there, and
    /// within <see cref="TuckBand"/>. An edge one triangle uses, or a corner, is where the author stopped drawing.</summary>
    internal static bool Spans(BodySurface drawn, BodySurface.Hit hit, SkinEdges? edges)
        => ChordUnder(drawn, hit, edges) is float chord && hit.Distance <= MathF.Min(TuckBand, TuckSpanShare * chord);

    /// <summary>
    /// How far into the body the tuck looks for the garment's cloth behind open skin (0.5 m): skin in the open that the
    /// laid skin does not span is still drawn when the first of this garment's cloth behind it is seen from inside — left
    /// out, the camera would look through it into the garment, whose inside the game does not draw. Measured on the
    /// Witch's Coatee: on Neolithe's Almond XS the cut left the skin out at each strap tip on the upper breast, a slit into
    /// the bodice's back (2.16 cm² from the user's camera, 0 with this); the cloth behind lay 6–431 mm off, 285–346 at the
    /// 99th percentile over ten bodies and tops. Only within <see cref="TuckBand"/>, and only for the game's own top (see
    /// <see cref="Rebuild"/>'s seeInside).
    /// </summary>
    internal const float TuckSeeReach = 0.5f;

    private static readonly string[] ArmBonePrefixes = ["j_ude_", "j_kata_", "n_hkata_", "n_hhiji_", "n_hte_"];

    /// <summary>An upper arm, forearm, shoulder or hand bone.</summary>
    internal static bool IsArmBone(string bone)
        => ArmBonePrefixes.Any(p => bone.StartsWith(p, StringComparison.Ordinal)) || IsHandBone(bone);

    /// <summary>
    /// The new body's skin carried on past the author's edge, in under the garment's cloth, so the edge lies hidden.
    /// <para/>
    /// The cut draws the new body's skin only where the garment's own skin, laid onto it, still lies. The game's own tops
    /// end their skin at the neckline and turn it under the cloth there; laid onto a fuller bust that turned-in lip
    /// flattens onto the top of the breast, while the refit carries the neckline out with the bust, and nothing of the
    /// new skin reaches under it: looking down into Dirndl's Bodice on Neolithe's Almond XS, the bust was gone and the
    /// inside of the bodice's back showed through.
    /// <para/>
    /// Grown one welded body vertex at a time out from the kept skin, and a vertex goes in only when every body triangle
    /// it would newly draw — the writer draws a triangle when any corner is drawn — has, at each corner: the garment's
    /// laid skin within <see cref="TuckBand"/>, this garment's cloth over it within <see cref="TuckOver"/>, no cloth from
    /// <see cref="TuckBehind"/> behind to <see cref="TuckClear"/> in front, under <see cref="TuckArmShare"/> of its
    /// weight on the arm, and no move from <see cref="PullToEdge"/>; and its middle and the middles of its edges no
    /// cloth that close either. Newly drawn is judged against the cut as made, so whether a vertex qualifies is its own
    /// affair and the result is the same in any order. Nothing moves.
    /// <para/>
    /// A corner with no cloth over it still qualifies where the garment's laid skin spans it — lands inside its triangles
    /// or on an edge two share, within <see cref="TuckSpanShare"/> of the chord: skin the author drew, over a body that dips
    /// further below it than the cut's sag allows (the new body's cleavage under the game's coarse chest skin). With
    /// <paramref name="seeInside"/> it also qualifies where the first of the garment's cloth behind it, within
    /// <see cref="TuckSeeReach"/>, is seen from inside: without it there, the camera looks into the garment.
    /// </summary>
    /// <param name="edges">Which of <paramref name="drawn"/>'s edges two of its triangles share.</param>
    /// <param name="drawn">The garment's own skin, laid onto the new body.</param>
    /// <param name="cloth">The garment's cloth.</param>
    /// <param name="body">The new body's file, for its weights.</param>
    /// <param name="parts">The new body, without the variants its mod does not draw.</param>
    /// <param name="covered">Per vertex of <paramref name="parts"/>, whether the cut drew it.</param>
    /// <param name="keptTris">The triangles the cut kept.</param>
    /// <param name="movedAt">Where corners the pull moved stood (see <see cref="PullToEdge"/>).</param>
    /// <param name="baseOf">Each mesh's first vertex in <paramref name="parts"/>.</param>
    /// <param name="seeInside">Also draw open skin through which the garment's inside would show (see
    /// <see cref="TuckSeeReach"/>).</param>
    /// <returns>Per mesh, the vertices to draw as well (mesh-local), how many triangles they draw, how many of those have
    /// a corner in the open inside the author's skin and none that only the garment's inside let in, how many have such a
    /// corner, and whether the body's weights could not be read — then nothing is grown.</returns>
    internal static (Dictionary<int, HashSet<ushort>> Grown, int Triangles, int Opened, int SeenInto, bool Unread)
        TuckUnderCloth(BodySurface drawn, ClothCrossings cloth, byte[] body, ModelParts parts, bool[] covered,
                       IReadOnlyCollection<(int A, int B, int C)> keptTris, IReadOnlySet<Vector3> movedAt,
                       IReadOnlyDictionary<int, int> baseOf, SkinEdges? edges, bool seeInside)
    {
        int vc = parts.Positions.Length / 3;
        // Unread weights cannot tell the torso from the arm: nothing is grown, the safe way round.
        if (ModelSkinReader.Read(body, null, null) is not { } skin || skin.VertexCount != vc) return ([], 0, 0, 0, true);

        // The walk below — welding, per-node triangles, the face-sum normal, the triangle and vertex tests, the mesh-local
        // mapping — is GrowSpannedOpenSkin's too, with weights: a fix to one is a fix to the other.
        var pos = new Vec3[vc];
        for (int v = 0; v < vc; v++) pos[v] = ToVec(At(parts, v));
        var nodeOf = MeshMath.WeldByPosition(pos, out int nodes);

        // The body's skin triangles, and per node its copies and its triangles.
        var tris = new List<(int A, int B, int C)>();
        foreach (var part in parts.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                tris.Add((part.Triangles[t], part.Triangles[t + 1], part.Triangles[t + 2]));
        }
        var copies = new List<int>[nodes];
        var around = new List<int>[nodes];
        for (int n = 0; n < nodes; n++) { copies[n] = []; around[n] = []; }
        var onSkin = new bool[vc];
        var faceSum = new Vector3[vc];
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            var face = Vector3.Cross(At(parts, b) - At(parts, a), At(parts, c) - At(parts, a));
            foreach (int v in new[] { a, b, c })
            {
                faceSum[v] += face;
                if (!onSkin[v]) { onSkin[v] = true; copies[nodeOf[v]].Add(v); }
                if (around[nodeOf[v]].Count == 0 || around[nodeOf[v]][^1] != t) around[nodeOf[v]].Add(t);
            }
        }

        // A body vertex's normal: its own, or — where the file has none — the sum of its triangles' faces.
        Vector3 NormalOf(int v)
        {
            var n = new Vector3(parts.Normals[v * 3], parts.Normals[v * 3 + 1], parts.Normals[v * 3 + 2]);
            if (n.LengthSquared() > 1e-12f) return Vector3.Normalize(n);
            return faceSum[v].LengthSquared() > 1e-20f ? Vector3.Normalize(faceSum[v]) : Vector3.Zero;
        }

        bool NewlyDrawn(int t) => !covered[tris[t].A] && !covered[tris[t].B] && !covered[tris[t].C];

        // Per vertex, not per welded node: at a seam with a hard normal each copy is judged by its own normal, whichever
        // of them is asked first.
        var cornerOk = new bool?[vc];
        // Corners that qualify only because the garment's laid skin spans them, with no cloth over them; and those that
        // qualify only because the garment's inside would show through them.
        var openOnly = new bool[vc];
        var seenOnly = new bool[vc];
        bool CornerOk(int v)
        {
            int n = nodeOf[v];
            if (cornerOk[v] is { } known) return known;
            var p = At(parts, v);
            var nrm = NormalOf(v);
            var level = new Vector3(nrm.X, 0f, nrm.Z);
            BodySurface.Hit hit = default;
            bool ok = nrm.LengthSquared() > 1e-12f
                      && drawn.Nearest(p, TuckBand, out hit)
                      && !cloth.Crowds(p, nrm, TuckClear, TuckBehind)
                      && copies[n].All(c => EndShareOf(skin, c, IsArmBone) < TuckArmShare)
                      && !movedAt.Contains(p);
            if (ok && !(cloth.Over(p, nrm, TuckOver)
                        || (level.LengthSquared() > 1e-6f && cloth.Over(p, Vector3.Normalize(level), TuckOver))))
            {
                bool spanned = Spans(drawn, hit, edges);
                // Last, as the dearest: a walk through the cloth's grid.
                bool insideShows = !spanned && seeInside && !Tuned.NoSeeInside && cloth.SeesInside(p, -nrm, TuckSeeReach);
                ok = !Tuned.NoOpenSkin && (spanned || insideShows);
                openOnly[v] = ok && spanned;
                seenOnly[v] = ok && insideShows;
            }
            cornerOk[v] = ok;
            return ok;
        }

        bool TriangleOk(int t)
        {
            var (a, b, c) = tris[t];
            if (!CornerOk(a) || !CornerOk(b) || !CornerOk(c)) return false;
            Vector3 pa = At(parts, a), pb = At(parts, b), pc = At(parts, c);
            var face = Vector3.Cross(pb - pa, pc - pa);
            if (face.LengthSquared() < 1e-20f) return true;   // no area, nothing to see
            // Wound either way: the face's normal turned to the side its corners' normals face.
            if (Vector3.Dot(face, NormalOf(a) + NormalOf(b) + NormalOf(c)) < 0f) face = -face;
            foreach (var q in new[] { (pa + pb + pc) / 3f, (pa + pb) / 2f, (pb + pc) / 2f, (pc + pa) / 2f })
                if (cloth.Crowds(q, face, TuckClear, TuckBehind)) return false;
            return true;
        }

        var triangleOk = new bool?[tris.Count];
        bool Qualifies(int n)
        {
            if (copies[n].Count == 0 || copies[n].Any(v => covered[v])) return false;
            bool any = false;
            foreach (int t in around[n])
            {
                if (!NewlyDrawn(t)) continue;
                any = true;
                triangleOk[t] ??= TriangleOk(t);
                if (triangleOk[t] == false) return false;
            }
            return any;
        }

        // Out from the kept skin's corners, through vertices that qualify.
        var grown = new bool[nodes];
        var seen = new bool[nodes];
        var queue = new Queue<int>();
        foreach (int n in keptTris.SelectMany(t => new[] { t.A, t.B, t.C }).Select(v => nodeOf[v]).Distinct().Order())
        {
            seen[n] = true;
            queue.Enqueue(n);
        }
        while (queue.Count > 0)
        {
            int n = queue.Dequeue();
            if (!Qualifies(n)) continue;
            grown[n] = true;
            var next = around[n].SelectMany(t => new[] { tris[t].A, tris[t].B, tris[t].C }).Select(v => nodeOf[v])
                                .Where(m => !seen[m]).Distinct().Order();
            foreach (int m in next)
            {
                seen[m] = true;
                queue.Enqueue(m);
            }
        }

        var meshOf = new int[vc];
        foreach (var span in parts.MeshSpans)
            for (int k = 0; k < span.Count && span.BaseVertex + k < vc; k++) meshOf[span.BaseVertex + k] = span.Mesh;
        var sets = new Dictionary<int, HashSet<ushort>>();
        var drawnNow = new HashSet<int>();
        for (int n = 0; n < nodes; n++)
        {
            if (!grown[n]) continue;
            foreach (int v in copies[n])
            {
                int mesh = meshOf[v];
                if (!baseOf.TryGetValue(mesh, out int bv) || v - bv is < 0 or > ushort.MaxValue) continue;
                if (!sets.TryGetValue(mesh, out var set)) sets[mesh] = set = [];
                set.Add((ushort)(v - bv));
            }
            foreach (int t in around[n])
                if (NewlyDrawn(t)) drawnNow.Add(t);
        }
        var (opened, seenInto) = CountOpen(drawnNow.Select(t => tris[t]), openOnly, seenOnly);
        return (sets, drawnNow.Count, opened, seenInto, false);
    }

    /// <summary>
    /// Of the triangles a tuck drew, those in the open inside the author's skin — a corner only the laid skin's span let
    /// in — and those where the garment's inside would show — a corner only that let in. A triangle with both counts in
    /// the second, never the first: <see cref="SwapReport.Opened"/> keeps meaning skin inside the author's own.
    /// </summary>
    internal static (int Opened, int SeenInto) CountOpen(IEnumerable<(int A, int B, int C)> drawn, IReadOnlyList<bool> openOnly,
                                                         IReadOnlyList<bool> seenOnly)
    {
        int opened = 0, seenInto = 0;
        foreach (var (a, b, c) in drawn)
        {
            if (seenOnly[a] || seenOnly[b] || seenOnly[c]) seenInto++;
            else if (openOnly[a] || openOnly[b] || openOnly[c]) opened++;
        }
        return (opened, seenInto);
    }
}
