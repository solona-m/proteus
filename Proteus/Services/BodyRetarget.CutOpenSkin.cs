using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// How nearly the garment's laid skin must face the way the body does where it spans it, for
    /// <see cref="GrowSpannedOpenSkin"/> to draw the body there: the cosine between the laid skin's normal and the body's
    /// (0.9, about 26°). Below it the landing is on another sheet of skin — the other thigh, the inside of an arm, the
    /// underside of a breast — and the author's cut stands. Just under what the reported hole measured: the 68 spanned
    /// vertices of Codeine's slits on Muse+ "Defined" faced the laid skin at 0.99-1.00. The laid skin's normal is the
    /// author's, interpolated across the triangle the body lands in (<see cref="BodySurface.Hit.Normal"/>), not that
    /// triangle's face: the refit never moves the author's normals, they are what the skin is shaded by, and the coarse
    /// span's face can lean where its shading does not — so the margin is set from a real garment, not from a sheet.
    /// </summary>
    internal const float CutOpenSkinFacing = 0.9f;

    /// <summary>
    /// The new body's skin the cut left out in the open inside the author's own skin, drawn after all — on a slot the
    /// garment only reaches onto, and only there.
    /// <para/>
    /// Such a slot is cut whatever the setting (see <see cref="Rebuild"/>'s alwaysCut): whole, a long top's hips would
    /// bring the entire legs down to the ankles. But the cut keeps a body vertex only where the garment's laid skin comes
    /// within <see cref="CutReach"/>, or the sag of its chord (<see cref="CutSagShare"/>), and laid skin bridges a body
    /// that is deeper than the one it was made on. LINXX's Codeine, a mini dress whose one skin mesh runs from the neck
    /// to the feet, made on YAB+: refitted onto Muse+ "Defined" legs its laid skin spans the deeper fold under the
    /// buttock, the fold's vertices 0.30-0.32 of the chord under it, and the cut took 118 triangles there — two slits
    /// across the backs of the thighs, 15.4 cm² of skin neither drawn nor under cloth, the ground showing through.
    /// <para/>
    /// What tells a miss like that from the author's own cut is the tuck's open-skin rule (see <see cref="Spans"/>): the
    /// author drew skin across the place — the body lands inside the laid skin's triangles or on an edge two share, not on
    /// the edge where the author stopped — and no cloth is over it or crowds it (<see cref="TuckOver"/>,
    /// <see cref="TuckClear"/>, <see cref="TuckBehind"/>). Grown as the tuck grows, one welded body vertex at a time out
    /// from the kept skin, and a vertex goes in only when every body triangle it would newly draw — the writer draws a
    /// triangle when any corner is drawn — qualifies at every corner and has no cloth that close at its middle or the
    /// middles of its edges: so nothing is drawn a ring past the author's edge, and nothing off the kept skin (the other
    /// thigh, laid skin facing another way) comes in on its own. No arm guard: that is for skin drawn under a sleeve, and
    /// this draws none. Judged against the cut as made, so the result is the same in any order. Nothing moves.
    /// <para/>
    /// Measured on Codeine onto Muse+ "Defined - Gen A": 28 triangles drawn, 15.4 cm² of the backs of the thighs neither
    /// drawn nor under cloth down to 0.1, skin through the cloth unchanged (11.6 cm²). The tuck's rule as it stands — no
    /// cloth over the skin within <see cref="TuckOver"/> — closed it as well as keeping skin under cloth did
    /// (<see cref="Tuning.CutOpenSkinUnderCloth"/>), so the stricter one is kept.
    /// <para/>
    /// Only in the automatic refit (see <see cref="Plan"/>'s openSkin), and only on a slot cut because the garment reaches
    /// onto it: everywhere else the cut carries an author's cut on purpose, and the Studio's refit is written into the
    /// modder's own mod — and the rule was measured on the game's own tops.
    /// </summary>
    /// <param name="drawn">The garment's own skin, laid onto the new body.</param>
    /// <param name="edges">Which of <paramref name="drawn"/>'s edges two of its triangles share.</param>
    /// <param name="cloth">The garment's cloth.</param>
    /// <param name="parts">The new body, without the variants its mod does not draw.</param>
    /// <param name="covered">Per vertex of <paramref name="parts"/>, whether the cut drew it.</param>
    /// <param name="keptTris">The triangles the cut kept.</param>
    /// <param name="movedAt">Where corners the pull moved stood (see <see cref="PullToEdge"/>).</param>
    /// <param name="baseOf">Each mesh's first vertex in <paramref name="parts"/>.</param>
    /// <returns>Per mesh, the vertices to draw as well (mesh-local), and how many triangles they newly draw.</returns>
    private static (Dictionary<int, HashSet<ushort>> Grown, int Triangles) GrowSpannedOpenSkin(
        BodySurface drawn, SkinEdges edges, ClothCrossings cloth, ModelParts parts, bool[] covered,
        IReadOnlyCollection<(int A, int B, int C)> keptTris, IReadOnlySet<Vector3> movedAt,
        IReadOnlyDictionary<int, int> baseOf)
    {
        int vc = parts.Positions.Length / 3;
        // TuckUnderCloth's walk without its weights: a fix to one is a fix to the other.
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

        // Per vertex, not per welded node: at a seam with a hard normal each copy is judged by its own normal.
        var cornerOk = new bool?[vc];
        bool CornerOk(int v)
        {
            if (cornerOk[v] is { } known) return known;
            var p = At(parts, v);
            var nrm = NormalOf(v);
            var level = new Vector3(nrm.X, 0f, nrm.Z);
            bool ok = nrm.LengthSquared() > 1e-12f
                      && drawn.Nearest(p, TuckBand, out var hit)
                      && Spans(drawn, hit, edges)
                      && (Tuned.CutOpenSkinAnyFacing || Vector3.Dot(hit.Normal, nrm) >= CutOpenSkinFacing)
                      && !cloth.Crowds(p, nrm, TuckClear, TuckBehind)
                      && !movedAt.Contains(p)
                      && (Tuned.CutOpenSkinUnderCloth
                          || !(cloth.Over(p, nrm, TuckOver)
                               || (level.LengthSquared() > 1e-6f && cloth.Over(p, Vector3.Normalize(level), TuckOver))));
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
        return (sets, drawnNow.Count);
    }
}
