using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    /// <summary>
    /// Largest a hand's UV island may be, as a fraction of its biggest, and still be a nail bed. Measured on
    /// Neolithe (nail beds 16–120 triangles, fingers 790+, back of hand 2746), Rue+ (2–208, 448+, 1393) and
    /// Bibo+ (60–188, 500, 1122).
    /// </summary>
    private const float NailBedIslandMax = 0.2f;

    /// <summary>
    /// How near the rest of the skin every vertex of a nail bed must lie. Measured: nail islands reach 1.8 mm
    /// (Neolithe), 4.9 (Bibo+), 5.1 (Rue+); the small wrist connectors Neolithe and Bibo+ also carry, 19–22 mm.
    /// </summary>
    private const float NailBedReach = 0.008f;

    /// <summary>Least mean coverage the fingertip must have for a nail bed to move onto it: an unpainted finger
    /// has nothing to lend.</summary>
    private const float NailBedPaintedFloor = CoverageFloor;

    /// <summary>The layer's coverage at one UV, nearest texel, wrapped like <see cref="AnyVisible"/>.</summary>
    private static float CoverageAt(SecondSkinLayer def, (float U, float V) uv)
    {
        if (def.Coverage is not { } mask) return 255f;
        int w = def.CoverageWidth, h = def.CoverageHeight;
        int x = (((int)MathF.Floor(uv.U * w) % w) + w) % w;
        int y = (((int)MathF.Floor(uv.V * h) % h) + h) % h;
        return mask[y * w + x];
    }

    /// <summary>How near a nail mesh must be to a nail bed vertex to count as standing on it.</summary>
    private const float NailLiftReach = 0.001f;
    /// <summary>Clearance kept over a nail mesh, on top of its height: the same as the shell keeps over skin. More
    /// than this does not help — measured, a taller nail bed just steps away from the finger at its rim and opens
    /// the gap the skin shows through.</summary>
    private const float NailClearance = BaseOffset;

    /// <summary>
    /// One hand mesh's nail beds: the small UV islands the skin lays under each nail, apart from the finger in the
    /// atlas. Glove art paints the fingers and not these, so the coverage trim would open a hole over every nail;
    /// VerbatimCopy.RescueNailBeds moves an unpainted one onto <see cref="FingertipUv"/> instead.
    /// </summary>
    internal sealed class NailBedPlan
    {
        /// <summary>Each nail bed's triangles, as mesh-local vertex indices.</summary>
        public required List<int[]> Islands;
        /// <summary>Each nail bed's vertices.</summary>
        public required List<int[]> VertsOf;
        /// <summary>Per vertex, the RAW uv0 of the nearest skin outside every nail bed — the fingertip around the
        /// nail. Null for a vertex that is not in a nail bed.</summary>
        public required (float U, float V)?[] FingertipUv;
        /// <summary>Per vertex, extra push to clear a nail MESH standing on the bed (Neolithe, Rue+); zero off the
        /// beds and on hands whose nails are the skin itself (Bibo+). The shell sits a twentieth of a millimetre
        /// off the skin, and a nail stands up to a fifth.</summary>
        public required float[] Lift;
        /// <summary>
        /// Per nail-bed vertex, where it sits on the skin AROUND the nail, a hair under the surface: the finger as
        /// it would be with no nail on it. <c>BodyBridge.FlattenNails</c> takes the nail down onto this. Not a
        /// surface spanning the island's own boundary — that boundary IS the nail's outline, and a patch spanning
        /// it is still a nail, flat instead of domed.
        /// </summary>
        public required Vec3?[] OnSkin;
        /// <summary>
        /// Per island, whether it is SEWN into the finger — its boundary shared with skin that is not a nail, so
        /// removing it would open a hole and it has to be laid down onto <see cref="OnSkin"/> instead. False for a
        /// nail that merely sits on a closed finger: that one collapses to a point and draws nothing, which no
        /// amount of sinking a flattened patch can match, since its triangles are chords and a chord cuts back out
        /// of the skin across a fold.
        /// </summary>
        public required List<bool> Sewn;
    }

    /// <summary>
    /// How far under the skin a flattened nail is tucked, so the finger wins every pixel. Two tenths of a
    /// millimetre left it grazing the surface at the very fingertip, where the skin curves away fastest and the
    /// landing's normal points least like the surface the nail is seen against; three clears it.
    /// </summary>
    private const float NailSinkDepth = 0.0003f;

    /// <summary>One skin mesh of a source, as read for the nail-bed plan.</summary>
    private sealed class NailSkinMesh
    {
        public required int Mesh;
        public required Vec3[] Pos;
        public required Vec3[] Nrm;
        public required (float U, float V)[] Uv;
        public required List<int> Tris;
        public required int[] Island;          // per triangle: its island's root vertex
        public required Dictionary<int, int> IslandSize;
    }

    /// <summary>Each of these vertices' skinning, as text, so a caller can ask whether they all match. The indices
    /// are the first skin mesh's, which is where a hand keeps its nail beds.</summary>
    internal static List<string> SkinningOf(byte[] mdl, int[] verts)
    {
        var outp = new List<string>();
        if (!TryReadLod0Geometry(mdl, out _, out _, out _, out var weights, out _)) return outp;
        foreach (int v in verts)
            if (v < weights.Length)
                outp.Add(string.Join(",", weights[v].OrderBy(w => w.Bone, StringComparer.Ordinal)
                                                    .Select(w => $"{w.Bone}:{w.W:F3}")));
        return outp;
    }

    /// <summary>How far apart these vertices sit: zero once they are collapsed onto one point.</summary>
    internal static float SpreadOf(byte[] mdl, int mesh, int[] verts)
    {
        var byMesh = ReadCapMeshes(mdl).ToDictionary(x => x.Mesh, x => x.Pos);
        if (!byMesh.TryGetValue(mesh, out var pos)) return float.MaxValue;
        float worst = 0f;
        for (int i = 0; i < verts.Length; i++)
            for (int j = i + 1; j < verts.Length; j++)
                if (verts[i] < pos.Length && verts[j] < pos.Length)
                    worst = MathF.Max(worst, Dist(pos[verts[i]], pos[verts[j]]));
        return worst;
    }

    /// <summary>
    /// A hand model's nail beds, as mesh index → each bed's triangles (mesh-local corner indices). For the BODY
    /// pass that flattens them away under a glove — see <c>BodyBridge.FlattenNails</c>. Empty for a model with
    /// none, and for anything this cannot read.
    /// </summary>
    internal static Dictionary<int, List<int[]>> NailBedIslands(byte[] mdl, Action<string>? diag = null)
    {
        var found = new Dictionary<int, List<int[]>>();
        foreach (var (mesh, plan) in NailBedPlans(mdl, diag)) found[mesh] = plan.Islands;
        return found;
    }

    /// <summary>A hand model's nail beds in full, keyed by mesh — see <see cref="NailBedPlan"/>.</summary>
    internal static Dictionary<int, NailBedPlan> NailBedPlans(byte[] mdl, Action<string>? diag = null)
    {
        Source src;
        try { src = Parse(mdl); }
        catch { return []; }
        return NailBeds(src, diag);
    }

    /// <summary>
    /// Plan the nail beds of every skin mesh of a hand source, keyed by mesh; a mesh with none is absent.
    /// Islands are each mesh's index-connected components, so a UV seam separates them. A nail bed is one under
    /// <see cref="NailBedIslandMax"/> of the source's biggest lying wholly within <see cref="NailBedReach"/> of
    /// the rest of the skin — Bibo+ keeps its nail beds in a mesh of their own, so the fingertips they land on are
    /// in another. Each bed vertex lands on the nearest point of the rest of the skin: the rim lands on the finger's
    /// own rim, and the inside takes the fingertip's colour.
    /// </summary>
    private static Dictionary<int, NailBedPlan> NailBeds(Source src, Action<string>? diag)
    {
        var plans = new Dictionary<int, NailBedPlan>();
        var meshes = ReadNailSkinMeshes(src);
        int biggest = 0;
        foreach (var sm in meshes)
            foreach (int n in sm.IslandSize.Values) biggest = Math.Max(biggest, n);
        int bedMax = (int)(biggest * NailBedIslandMax);

        // The rest of the skin, from every mesh: what a bed is sewn into and lands on.
        var rest = new List<(Vec3 A, Vec3 B, Vec3 C, (float U, float V) Ua, (float U, float V) Ub, (float U, float V) Uc, Vec3 Ctr)>();
        foreach (var sm in meshes)
            for (int t = 0; t + 2 < sm.Tris.Count; t += 3)
            {
                if (sm.IslandSize[sm.Island[t / 3]] <= bedMax) continue;
                var (a, b, c) = (sm.Pos[sm.Tris[t]], sm.Pos[sm.Tris[t + 1]], sm.Pos[sm.Tris[t + 2]]);
                rest.Add((a, b, c, sm.Uv[sm.Tris[t]], sm.Uv[sm.Tris[t + 1]], sm.Uv[sm.Tris[t + 2]],
                          new Vec3((a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f, (a.Z + b.Z + c.Z) / 3f)));
            }
        if (rest.Count == 0) return plans;

        // Nearest point of the rest of the skin: its distance, the UV there, and WHERE it is — a nail is taken
        // down onto that point, which is the finger as it would look with no nail on it.
        (float D, (float U, float V) Uv, Vec3 At, Vec3 Out) Land(Vec3 p)
        {
            float best = float.MaxValue;
            (float U, float V) bestUv = default;
            Vec3 bestAt = default, bestOut = default;
            foreach (var t in rest)
            {
                // Culled on the centroid: a hand's triangles are a few millimetres across, so 2 cm is generous.
                float cx = t.Ctr.X - p.X, cy = t.Ctr.Y - p.Y, cz = t.Ctr.Z - p.Z;
                if (best < float.MaxValue && cx * cx + cy * cy + cz * cz > best + 0.0004f) continue;
                var q = ClosestOnTriangle(p, t.A, t.B, t.C);
                float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                float d = dx * dx + dy * dy + dz * dz;
                if (d >= best) continue;
                best = d;
                var (wa, wb, wc) = Barycentric(q, t.A, t.B, t.C);
                bestUv = (t.Ua.U * wa + t.Ub.U * wb + t.Uc.U * wc, t.Ua.V * wa + t.Ub.V * wb + t.Uc.V * wc);
                bestAt = q;
                bestOut = NormalizeOr(new Vec3(
                    (t.B.Y - t.A.Y) * (t.C.Z - t.A.Z) - (t.B.Z - t.A.Z) * (t.C.Y - t.A.Y),
                    (t.B.Z - t.A.Z) * (t.C.X - t.A.X) - (t.B.X - t.A.X) * (t.C.Z - t.A.Z),
                    (t.B.X - t.A.X) * (t.C.Y - t.A.Y) - (t.B.Y - t.A.Y) * (t.C.X - t.A.X)), default);
            }
            return (MathF.Sqrt(best), bestUv, bestAt, bestOut);
        }

        int beds = 0, loose = 0;
        float worst = 0f, highest = 0f;
        foreach (var sm in meshes)
        {
            var bedTris = new Dictionary<int, List<int>>();
            for (int t = 0; t + 2 < sm.Tris.Count; t += 3)
            {
                int r = sm.Island[t / 3];
                if (sm.IslandSize[r] > bedMax) continue;
                if (!bedTris.TryGetValue(r, out var l)) bedTris[r] = l = [];
                l.Add(sm.Tris[t]); l.Add(sm.Tris[t + 1]); l.Add(sm.Tris[t + 2]);
            }
            if (bedTris.Count == 0) continue;

            // Which welded edges the skin that is NOT a nail uses: a nail bed sharing one is sewn into the finger.
            var restEdges = new HashSet<((int, int, int), (int, int, int))>();
            foreach (var other in meshes)
                for (int t = 0; t + 2 < other.Tris.Count; t += 3)
                {
                    if (other.IslandSize[other.Island[t / 3]] <= bedMax) continue;
                    for (int e = 0; e < 3; e++)
                    {
                        var pa = other.Pos[other.Tris[t + e]];
                        var pb = other.Pos[other.Tris[t + (e + 1) % 3]];
                        restEdges.Add(WeldedEdge(pa, pb));
                    }
                }

            var islands = new List<int[]>();
            var vertsOf = new List<int[]>();
            var sewn = new List<bool>();
            var tip = new (float U, float V)?[sm.Pos.Length];
            var onSkin = new Vec3?[sm.Pos.Length];
            foreach (var l in bedTris.Values)
            {
                // Hugging the fingertip all over? A nail's islands (the top and the tip, which overhangs the finger)
                // stay within a few millimetres of it; a wrist connector hangs a couple of centimetres off the arm.
                var verts = new HashSet<int>(l);
                var landed = new (int V, (float U, float V) Uv, Vec3 At)[verts.Count];
                float far = 0f;
                int j = 0;
                foreach (int v in verts)
                {
                    var (d, uv, at, outward) = Land(sm.Pos[v]);
                    far = MathF.Max(far, d);
                    // Just under the skin it lands on: level with it they would fight for the same pixels.
                    landed[j++] = (v, uv, new Vec3(at.X - outward.X * NailSinkDepth,
                                                   at.Y - outward.Y * NailSinkDepth,
                                                   at.Z - outward.Z * NailSinkDepth));
                }
                if (far > NailBedReach) { loose++; continue; }
                worst = MathF.Max(worst, far);
                foreach (var (v, uv, at) in landed) { tip[v] = uv; onSkin[v] = at; }
                islands.Add(l.ToArray());
                vertsOf.Add([.. verts]);
                sewn.Add(IsSewnIn(sm, l, restEdges));
            }
            if (islands.Count == 0) continue;

            var lift = NailLift(src, sm.Pos, vertsOf, out float high);
            highest = MathF.Max(highest, high);
            beds += islands.Count;
            plans[sm.Mesh] = new NailBedPlan
            {
                Islands = islands, VertsOf = vertsOf, FingertipUv = tip, Lift = lift, OnSkin = onSkin, Sewn = sewn,
            };
        }

        diag?.Invoke($"nail beds: {beds} in {plans.Count} mesh(es) (islands of at most {bedMax} of {biggest} triangles; "
                   + $"{loose} small island(s) straying past {NailBedReach:F3} of the rest of the skin left alone), "
                   + $"furthest from the fingertip {worst:F4}, a nail mesh up to {highest:F5} above them");
        return plans;
    }

    /// <summary>One edge as a pair of welded positions, smaller end first, so two meshes agree on it.</summary>
    private static ((int, int, int), (int, int, int)) WeldedEdge(Vec3 a, Vec3 b)
    {
        var ka = ((int)MathF.Round(a.X * 1e5f), (int)MathF.Round(a.Y * 1e5f), (int)MathF.Round(a.Z * 1e5f));
        var kb = ((int)MathF.Round(b.X * 1e5f), (int)MathF.Round(b.Y * 1e5f), (int)MathF.Round(b.Z * 1e5f));
        return ka.CompareTo(kb) <= 0 ? (ka, kb) : (kb, ka);
    }

    /// <summary>
    /// Is this island sewn into the surrounding skin — does its own boundary run along an edge the rest of the
    /// skin also uses? Then taking it away opens a hole. A nail that merely lies on a closed finger shares no
    /// boundary edge with it.
    /// </summary>
    private static bool IsSewnIn(NailSkinMesh sm, List<int> tris,
                                 HashSet<((int, int, int), (int, int, int))> restEdges)
    {
        var use = new Dictionary<(int, int), int>();
        for (int t = 0; t + 2 < tris.Count; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int x = tris[t + e], y = tris[t + (e + 1) % 3];
                var k = (Math.Min(x, y), Math.Max(x, y));
                use[k] = use.GetValueOrDefault(k) + 1;
            }
        foreach (var (k, n) in use)
            if (n == 1 && restEdges.Contains(WeldedEdge(sm.Pos[k.Item1], sm.Pos[k.Item2]))) return true;
        return false;
    }

    /// <summary>Every LOD0 skin mesh of the source (the ones the shell copies), with its UV islands.</summary>
    private static List<NailSkinMesh> ReadNailSkinMeshes(Source src)
    {
        var list = new List<NailSkinMesh>();
        var s = src.S;
        Span<float> tmp = stackalloc float[4];
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > s.Length) break;
            ushort vc = BitConverter.ToUInt16(s, mo);
            if (vc == 0) continue;
            ushort mat = BitConverter.ToUInt16(s, mo + 8);
            if (mat >= src.MatNames.Count || !src.Keep(src.MatNames[mat])) continue;

            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            VElem? posEl = null, uvEl = null, nrmEl = null;
            foreach (var el in decl)
            {
                if (el.Usage == UsePosition) posEl ??= el;
                else if (el.Usage == UseNormal) nrmEl ??= el;
                else if (el.Usage == UseUV && el.UsageIndex == 0) uvEl ??= el;
            }
            if (posEl is not { } pe || uvEl is not { } ue || pe.Stream > 2 || ue.Stream > 2) continue;

            var pos = new Vec3[vc];
            var nrm = new Vec3[vc];
            var uv = new (float U, float V)[vc];
            bool ok = true;
            for (int i = 0; i < vc && ok; i++)
            {
                int pa = src.Vb + (int)BitConverter.ToUInt32(s, mo + 20 + pe.Stream * 4) + i * s[mo + 32 + pe.Stream] + pe.Offset;
                int ua = src.Vb + (int)BitConverter.ToUInt32(s, mo + 20 + ue.Stream * 4) + i * s[mo + 32 + ue.Stream] + ue.Offset;
                if (pa < 0 || ua < 0 || pa + 16 > s.Length || ua + 16 > s.Length) { ok = false; break; }
                ReadTyped(s, pa, pe.Type, tmp); pos[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
                ReadTyped(s, ua, ue.Type, tmp); uv[i] = (tmp[0], tmp[1]);
                if (nrmEl is not { } ne || ne.Stream > 2) continue;
                int na = src.Vb + (int)BitConverter.ToUInt32(s, mo + 20 + ne.Stream * 4) + i * s[mo + 32 + ne.Stream] + ne.Offset;
                if (na < 0 || na + 16 > s.Length) continue;
                ReadTyped(s, na, ne.Type, tmp);
                float nx = tmp[0], ny = tmp[1], nz = tmp[2];
                if (ne.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
                nrm[i] = NormalizeOr(new Vec3(nx, ny, nz), default);
            }
            if (!ok) continue;

            // UV islands: connected through shared vertex INDICES, which a seam splits.
            var tris = CapTriangles(src, m, vc);
            var parent = new int[vc];
            for (int i = 0; i < vc; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = Find(tris[t]), b = Find(tris[t + 1]);
                if (a != b) parent[a] = b;
                a = Find(tris[t + 1]); b = Find(tris[t + 2]);
                if (a != b) parent[a] = b;
            }
            var island = new int[tris.Count / 3];
            var size = new Dictionary<int, int>();
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int r = Find(tris[t]);
                island[t / 3] = r;
                size[r] = size.GetValueOrDefault(r) + 1;
            }
            list.Add(new NailSkinMesh { Mesh = m, Pos = pos, Nrm = nrm, Uv = uv, Tris = tris, Island = island, IslandSize = size });
        }
        return list;
    }

    /// <summary>
    /// How far each nail-bed vertex must rise to clear the nail mesh over it: its distance to the nearest point of
    /// the source's non-skin geometry, plus <see cref="NailClearance"/>, where that geometry is within
    /// <see cref="NailLiftReach"/>. Everything else stays at zero.
    /// </summary>
    private static float[] NailLift(Source src, Vec3[] pos, List<int[]> vertsOf, out float highest)
    {
        var lift = new float[pos.Length];
        highest = 0f;
        if (!TryReadLod0Geometry(src.S, out var np, out _, out var nt, out _, out _, skinOnly: false, nonSkin: true))
            return lift;

        int nTri = nt.Length / 3;
        var a = new Vec3[nTri]; var b = new Vec3[nTri]; var c = new Vec3[nTri]; var ctr = new Vec3[nTri];
        Vec3 P(int i) => new(np[i * 3], np[i * 3 + 1], np[i * 3 + 2]);
        for (int t = 0; t < nTri; t++)
        {
            (a[t], b[t], c[t]) = (P(nt[t * 3]), P(nt[t * 3 + 1]), P(nt[t * 3 + 2]));
            ctr[t] = new Vec3((a[t].X + b[t].X + c[t].X) / 3f, (a[t].Y + b[t].Y + c[t].Y) / 3f, (a[t].Z + b[t].Z + c[t].Z) / 3f);
        }

        // Centroid cull: a nail triangle is a few millimetres across at most.
        const float cull = (NailLiftReach + 0.01f) * (NailLiftReach + 0.01f);
        foreach (var verts in vertsOf)
            foreach (int i in verts)
            {
                var p = pos[i];
                float best = float.MaxValue;
                for (int t = 0; t < nTri; t++)
                {
                    float cx = ctr[t].X - p.X, cy = ctr[t].Y - p.Y, cz = ctr[t].Z - p.Z;
                    if (cx * cx + cy * cy + cz * cz > cull) continue;
                    var q = ClosestOnTriangle(p, a[t], b[t], c[t]);
                    float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                    best = MathF.Min(best, dx * dx + dy * dy + dz * dz);
                }
                if (best > NailLiftReach * NailLiftReach) continue;
                float d = MathF.Sqrt(best);
                lift[i] = d + NailClearance;
                highest = MathF.Max(highest, d);
            }
        return lift;
    }
}
