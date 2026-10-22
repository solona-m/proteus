using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// The old body read as it is worn where its skin has a hole under its own underwear.
/// <para/>
/// The game's chest has no skin over the breasts: the bust is its bra, a mesh of the smallclothes material and nothing
/// else. Read as skin alone, the old body had a hole there, and cloth over the bust took its move from the hole's rim —
/// the No.2 Type B Dress refitted onto Muse+'s flat chest kept the game body's breasts, 25 mm proud of Muse at the
/// fullest. The bra is read with the skin instead, and each point of it given a move of its own: first the move of the
/// skin around it, then wherever that lands on the new body as worn — the same carry and landing
/// <see cref="UvAtlasCorrespondence"/> gives skin the new body draws no skin under.
/// <para/>
/// Two limits, both measured: only the game's own chest is read this way (<see cref="SlotPair.SourceIsGameChest"/>) —
/// other bodies stand underwear over a hole too, and nothing was measured on them — and a landing only ever brings the
/// cloth in (<see cref="Inward"/>).
/// </summary>
internal static partial class BodyRetarget
{
    /// <summary>How far behind an underwear point (30 mm) the body's skin must be missing for the point to stand over a
    /// hole in it: a bra over skin lies a millimetre or two off it.</summary>
    internal const float HoleProbe = 0.03f;

    /// <summary>The least area of underwear over a hole (4 cm²) that reads the old body as worn: a band poking past the
    /// skin's edge at the waist is not a hole the cloth was fitted over.</summary>
    internal const float MinHoleArea = 4e-4f;

    /// <summary>How far from where the skin around it carries it (80 mm) an underwear point may land on the new body —
    /// two bodies' busts differ by a few centimetres.</summary>
    internal const float BraLandReach = 0.08f;

    /// <summary>How closely the new body must face the way the underwear does where a nearest landing is taken (60°):
    /// past it the nearest point is the underside or the flank of a fuller breast, and the landing goes along the
    /// underwear's normal instead.</summary>
    internal const float BraLandAgreement = 0.5f;

    /// <summary>How many times underwear the landing turned over is relaxed toward its neighbours and landed again.</summary>
    internal const int BraFoldRounds = 8;

    /// <summary>Within this distance of the midline (15 mm) a point has no side — see <see cref="UvAtlasCorrespondence"/>.</summary>
    private const float BraSideBand = 0.015f;

    /// <summary>
    /// Per vertex of <paramref name="body"/>, whether it is a corner of its own smallclothes standing over a hole in its
    /// skin: no skin along its inward normal within <see cref="HoleProbe"/>. Null when the body has none, or too little
    /// to count (<see cref="MinHoleArea"/>) — and then the refit reads it as it always has.
    /// </summary>
    /// <param name="skin">The body's skin, as <see cref="BodySurface"/> reads it.</param>
    internal static bool[]? SmallclothesOverHoles(ModelParts body, BodySurface skin)
    {
        if (skin.IsEmpty) return null;
        int vc = body.Positions.Length / 3;
        var tris = SmallclothesTriangles(body);
        if (tris.Count == 0) return null;

        // Asked once per vertex, along the inward normal: behind a bra over skin the skin is a millimetre or two away;
        // behind the game's bra there is nothing nearer than the back. The model's own outer edge — the neck, the waist —
        // has skin beside it, not behind it, so a band past it is a hole only where nothing lies under it at all.
        var asked = new Dictionary<int, bool>();
        bool Over(int v)
        {
            if (asked.TryGetValue(v, out bool known)) return known;
            var p = new Vector3(body.Positions[v * 3], body.Positions[v * 3 + 1], body.Positions[v * 3 + 2]);
            var n = new Vector3(body.Normals[v * 3], body.Normals[v * 3 + 1], body.Normals[v * 3 + 2]);
            bool over = n.LengthSquared() > 1e-12f && !skin.Ray(p, -Vector3.Normalize(n), HoleProbe, out _);
            asked[v] = over;
            return over;
        }

        var marks = new bool[vc];
        double area = 0;
        foreach (var (a, b, c) in tris)
        {
            if (!Over(a) || !Over(b) || !Over(c)) continue;
            marks[a] = marks[b] = marks[c] = true;
            area += Vector3.Cross(P(body, b) - P(body, a), P(body, c) - P(body, a)).Length() * 0.5;
        }
        return area >= MinHoleArea ? marks : null;
    }

    /// <summary>
    /// Gives each underwear point over the hole (<paramref name="over"/>) with no move of its own a move: carried by the
    /// skin nearest it, then landed on the new body as worn — the nearest point that faces the way the underwear does,
    /// else the new body along the underwear's normal — on its own side, where that brings it in (see
    /// <see cref="Inward"/>). A landing that turns the underwear over is relaxed (see <see cref="RelaxFolds"/>). A point
    /// with nothing in reach, or whose landing — relaxed or not — is further out, is taken out of <paramref name="over"/>:
    /// there the old body is read as it always was, its skin alone.
    /// </summary>
    /// <param name="whole">The old body's field, holes filled (<see cref="SourceBody.Whole"/>); written in place.</param>
    /// <param name="over">Written in place: left marking only the points that landed.</param>
    /// <param name="skin">The old body's skin.</param>
    /// <param name="target">The new body, as the swap will draw it.</param>
    /// <returns>How many underwear points are read with the skin: landed on the new body, or placed by the correspondence.</returns>
    internal static int LandSmallclothes(ModelParts src, BodySurface skin, Vector3?[] whole, bool[] over, ModelParts target)
    {
        int vc = src.Positions.Length / 3;
        var worn = BodySurface.OfBodyWorn(target, BodySurface.CellFor(MeanEdgeOf(target)));
        var sides = new sbyte[target.Positions.Length / 3];
        for (int v = 0; v < sides.Length; v++) sides[v] = (sbyte)BraSide(target.Positions[v * 3]);

        // Points the correspondence placed itself (the same mesh, a size of one body) keep that move and are read as
        // they are.
        int placed = 0;
        for (int v = 0; v < vc; v++)
            if (over[v] && whole[v] != null) placed++;

        // Welded by position: a uv seam across the bra is two vertices at one point, and both must land as one.
        var verts = new List<int>();
        for (int v = 0; v < vc; v++)
            if (over[v] && whole[v] == null) verts.Add(v);
        if (verts.Count == 0) return placed;
        var at = new Vec3[verts.Count];
        for (int i = 0; i < verts.Count; i++) at[i] = ToVec(P(src, verts[i]));
        int[] nodeOf = MeshMath.WeldByPosition(at, out int nodes);
        var members = new List<int>[nodes];
        for (int i = 0; i < verts.Count; i++) (members[nodeOf[i]] ??= []).Add(verts[i]);

        var pos = new Vector3[nodes];
        var normal = new Vector3[nodes];
        var carried = new Vector3[nodes];
        var move = new Vector3[nodes];
        var landed = new bool[nodes];
        for (int n = 0; n < nodes; n++)
        {
            int v = members[n][0];
            pos[n] = P(src, v);
            var sum = Vector3.Zero;
            foreach (int m in members[n])
                sum += new Vector3(src.Normals[m * 3], src.Normals[m * 3 + 1], src.Normals[m * 3 + 2]);
            normal[n] = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : Vector3.UnitZ;
            carried[n] = Carried(skin, whole, pos[n]);
            var landing = Land(worn, sides, pos[n], carried[n], normal[n]);
            landed[n] = landing is { } l && Inward(l, carried[n], normal[n]);
            move[n] = landed[n] ? landing!.Value : carried[n];
        }

        // Ring-1 over the welded underwear, from the triangles read over the hole.
        var index = new Dictionary<int, int>();
        for (int i = 0; i < verts.Count; i++) index[verts[i]] = nodeOf[i];
        var ring = new HashSet<int>[nodes];
        var faces = new List<(int A, int B, int C)>();
        foreach (var (a, b, c) in SmallclothesTriangles(src))
        {
            if (!index.TryGetValue(a, out int na) || !index.TryGetValue(b, out int nb) || !index.TryGetValue(c, out int nc))
                continue;
            faces.Add((na, nb, nc));
            (ring[na] ??= []).UnionWith([nb, nc]);
            (ring[nb] ??= []).UnionWith([na, nc]);
            (ring[nc] ??= []).UnionWith([na, nb]);
        }
        RelaxFolds(worn, sides, pos, normal, carried, move, landed, faces, ring);

        // Only what landed is read with the skin: the rest of the bra is left out, so the cloth over it reads the hole's
        // rim exactly as it always has.
        int count = placed;
        for (int n = 0; n < nodes; n++)
        {
            foreach (int v in members[n])
            {
                if (landed[n]) whole[v] = move[n];
                else over[v] = false;
            }
            if (landed[n]) count += members[n].Count;
        }
        return count;
    }

    /// <summary>
    /// Underwear the landing turned over, relaxed: each landed corner of a turned face, and the landed points around it,
    /// moved to its landed neighbours' average move and landed again — for <see cref="BraFoldRounds"/> rounds on the body,
    /// as many more off it. Only faces every corner of which landed are judged: no other is read. A relaxed move that no
    /// longer lies inward of the point's own carried move (<see cref="Inward"/>) is not taken — the point is dropped from
    /// the landed, and the old body is read without it there.
    /// </summary>
    /// <param name="move">Per welded point, its move; written in place.</param>
    /// <param name="landed">Per welded point, whether it landed; written in place.</param>
    private static void RelaxFolds(BodySurface worn, sbyte[] sides, Vector3[] pos, Vector3[] normal, Vector3[] carried,
                                   Vector3[] move, bool[] landed, List<(int A, int B, int C)> faces, HashSet<int>?[] ring)
    {
        bool Folded((int A, int B, int C) f)
        {
            if (!landed[f.A] || !landed[f.B] || !landed[f.C]) return false;
            var before = Vector3.Cross(pos[f.B] - pos[f.A], pos[f.C] - pos[f.A]);
            if (before.LengthSquared() < 1e-20f) return false;
            var after = Vector3.Cross(pos[f.B] + move[f.B] - (pos[f.A] + move[f.A]), pos[f.C] + move[f.C] - (pos[f.A] + move[f.A]));
            return Vector3.Dot(before, after) <= 0f;
        }

        for (int round = 0; round < 2 * BraFoldRounds; round++)
        {
            var folding = new HashSet<int>();
            foreach (var f in faces)
                if (Folded(f)) folding.UnionWith([f.A, f.B, f.C]);
            if (folding.Count == 0) break;
            foreach (int n in folding.ToArray())
                if (ring[n] is { } around)
                    foreach (int m in around)
                        if (landed[m]) folding.Add(m);

            // Landed again for the first rounds, so the fold relaxes on the body; what still folds after that relaxes off it.
            bool land = round < BraFoldRounds;
            var relaxed = new List<(int Node, Vector3? Move)>();
            foreach (int n in folding)
            {
                if (ring[n] is not { } around) continue;
                var sum = Vector3.Zero;
                int count = 0;
                foreach (int m in around)
                    if (landed[m]) { sum += move[m]; count++; }
                if (count == 0) continue;
                var average = sum / count;
                var next = land ? Land(worn, sides, pos[n], average, normal[n]) ?? average : average;
                relaxed.Add((n, Inward(next, carried[n], normal[n]) ? next : null));
            }
            foreach (var (n, m) in relaxed)
            {
                landed[n] = m != null;
                move[n] = m ?? carried[n];
            }
        }
    }

    /// <summary>The skin's move at its nearest point to <paramref name="p"/>, over the corners that have one.</summary>
    internal static Vector3 Carried(BodySurface skin, Vector3?[] whole, Vector3 p)
    {
        if (!skin.Nearest(p, FarBand, out var hit)) return Vector3.Zero;
        var sum = Vector3.Zero;
        float weight = 0f;
        if (whole[hit.A] is { } fa) { sum += fa * hit.U; weight += hit.U; }
        if (whole[hit.B] is { } fb) { sum += fb * hit.V; weight += hit.V; }
        if (whole[hit.C] is { } fc) { sum += fc * hit.W; weight += hit.W; }
        return weight > 0f ? sum / weight : Vector3.Zero;
    }

    /// <summary>Where an underwear point carried by <paramref name="carried"/> lands on the new body, as a move from
    /// <paramref name="p"/>; null when nothing is in reach.</summary>
    private static Vector3? Land(BodySurface worn, sbyte[] sides, Vector3 p, Vector3 carried, Vector3 normal)
    {
        var q = p + carried;
        int avoid = -BraSide(p.X);
        if (worn.Nearest(q, BraLandReach, out var hit, sides, avoid) && Vector3.Dot(hit.Normal, normal) >= BraLandAgreement)
            return hit.Point - p;

        // The nearest point faces away: from inside a fuller breast that is its underside or flank, not its front.
        bool outward = worn.Ray(q, normal, BraLandReach, out var ahead, sides, avoid);
        bool inward = worn.Ray(q, -normal, BraLandReach, out var behind, sides, avoid);
        if (outward && (!inward || ahead.Distance <= behind.Distance)) return ahead.Point - p;
        if (inward) return behind.Point - p;
        return null;
    }

    /// <summary>
    /// Whether a move lies further in than <paramref name="carried"/> along the underwear's normal: the old body's bra only
    /// ever brings cloth IN to a flatter chest. Taken out to a fuller one, the
    /// cloth landed where the push-out and the deep clear, tuned on the old reading, then drew more skin through it —
    /// LavaBod+'s Teardrop under the No.2 Type B Dress, 81 cm² to 161 — and even read at the carried move it was not the
    /// reading they were tuned on (138), so such points are left out of the old body altogether.
    /// </summary>
    internal static bool Inward(Vector3 move, Vector3 carried, Vector3 normal) => Vector3.Dot(move - carried, normal) < 0f;

    private static int BraSide(float x) => x > BraSideBand ? 1 : x < -BraSideBand ? -1 : 0;

    private static Vector3 P(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>The body's own smallclothes triangles (<see cref="BodySurface.IsSmallclothes"/>), valid corners only.</summary>
    private static List<(int A, int B, int C)> SmallclothesTriangles(ModelParts body)
    {
        int vc = body.Positions.Length / 3;
        var tris = new List<(int, int, int)>();
        foreach (var part in body.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)
                || !BodySurface.IsSmallclothes(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
                tris.Add((a, b, c));
            }
        }
        return tris;
    }
}
