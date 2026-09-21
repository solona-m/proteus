using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    /// <summary>Which side of the body each vertex of mesh <paramref name="m"/> is on, for a mirrored-UV
    /// source. Raw indices on purpose: a shape key cannot move a vertex to the other side of the body.</summary>
    // ── redundant geometry ────────────────────────────────────────────────────────────────────────
    //
    // A body draws some stretches of its own skin twice, and a shell cut from it inherits both copies; on a
    // sheer overlay the overlap doubles the alpha. Two rules: a seam RING at a joint is redundant because the
    // neighbouring part draws the same stretch of body; a duplicate VARIANT submesh is redundant because a
    // sibling submesh already draws it. Neither rule may assume a shape read off one body.

    /// <summary>
    /// One body model measured for the redundancy pass: every LOD0 skin submesh's size, extent and geometry.
    /// The measurement is intrinsic to the model and cached per body; the verdict depends on the other parts
    /// worn and on the pack's toggles, so it is not.
    /// </summary>
    public sealed class ConnectorProfile
    {
        /// <summary>An axis-aligned extent on all three axes: Y alone says only that two things are at the
        /// same height.</summary>
        public readonly record struct Box(float MinX, float MinY, float MinZ,
                                          float MaxX, float MaxY, float MaxZ)
        {
            /// <summary>Nothing measurable — no vertex was read. Covers nothing and is covered by nothing.</summary>
            public bool Empty => MinX > MaxX;

            public static Box Nothing => new(float.MaxValue, float.MaxValue, float.MaxValue,
                                             float.MinValue, float.MinValue, float.MinValue);
        }

        /// <summary>One LOD0 submesh, measured.</summary>
        /// <param name="Mesh">ABSOLUTE model mesh index.</param>
        /// <param name="Index">MESH-RELATIVE submesh index: the pair the emit loop counts with.</param>
        /// <param name="AttrMask">The submesh's attribute bits, for the hidden-sibling test and the log.</param>
        /// <param name="VertFirst">Window into the owning <see cref="MeshProfile"/>'s <c>SubVerts</c>: this
        /// submesh's DISTINCT vertices, since the coincidence fraction is per vertex.</param>
        /// <param name="TriFirst">Window into the owning mesh's <c>Tris</c>, in TRIANGLES not corners.</param>
        public readonly record struct Sub(
            int Mesh, int Index, uint AttrMask, int Triangles, Box Box,
            int VertFirst, int VertCount,
            int TriFirst, int TriCount);

        /// <summary>
        /// One LOD0 mesh that passed the source's material filter, held mesh-local: <paramref name="Pos"/> is
        /// 3 floats per vertex and <paramref name="Tris"/> 3 indices into it per triangle, submeshes concatenated.
        /// </summary>
        /// <param name="LargestSubTriangles">The biggest submesh in THIS mesh; the ring rule is relative to it
        /// because gear that ships its own skin is cut far coarser than a body.</param>
        public readonly record struct MeshProfile(
            int Index, int LargestSubTriangles,
            float[] Pos, ushort[] Tris, ushort[] SubVerts,
            int SubFirst, int SubCount);

        public required MeshProfile[] Meshes { get; init; }

        /// <summary>Every measured submesh, grouped by mesh — see <see cref="MeshProfile.SubFirst"/>.</summary>
        public required Sub[] Subs { get; init; }

        /// <summary>
        /// The extent of this source's substantial submeshes: what another source's ring rule is judged
        /// against. Null when nothing could be measured, in which case this part covers nothing.
        /// </summary>
        public Box? PartBox { get; init; }

        public int Vertices { get; init; }

        /// <summary>
        /// This measurement with the submeshes <paramref name="hidden"/> picks out removed: largest submesh and
        /// part extent are re-taken, an emptied mesh goes, positions are shared not copied, and every remaining
        /// submesh keeps its mesh and submesh index. Returns this instance when nothing is removed.
        /// </summary>
        public ConnectorProfile Without(Func<Sub, bool> hidden)
        {
            bool any = false;
            foreach (var sb in Subs) if (hidden(sb)) { any = true; break; }
            if (!any) return this;

            var meshes = new List<MeshProfile>(Meshes.Length);
            var subs = new List<Sub>(Subs.Length);
            var part = Box.Nothing;
            int totalVerts = 0;
            foreach (var mesh in Meshes)
            {
                var tris = new List<ushort>(mesh.Tris.Length);
                var subVerts = new List<ushort>(mesh.SubVerts.Length);
                int subFirst = subs.Count, largest = 0;
                for (int k = 0; k < mesh.SubCount; k++)
                {
                    var sb = Subs[mesh.SubFirst + k];
                    if (hidden(sb)) continue;
                    int triFirst = tris.Count / 3, vertFirst = subVerts.Count;
                    for (int c = sb.TriFirst * 3; c < (sb.TriFirst + sb.TriCount) * 3; c++) tris.Add(mesh.Tris[c]);
                    for (int v = sb.VertFirst; v < sb.VertFirst + sb.VertCount; v++) subVerts.Add(mesh.SubVerts[v]);
                    if (sb.Triangles > largest) largest = sb.Triangles;
                    subs.Add(sb with { TriFirst = triFirst, VertFirst = vertFirst });
                }
                if (tris.Count == 0) { subs.RemoveRange(subFirst, subs.Count - subFirst); continue; }

                // Same rule as ReadConnectorProfile: substantial submeshes only.
                for (int k = subFirst; k < subs.Count; k++)
                {
                    var sb = subs[k];
                    if (sb.Box.Empty || sb.Triangles < largest / 10) continue;
                    part = new Box(
                        MathF.Min(part.MinX, sb.Box.MinX), MathF.Min(part.MinY, sb.Box.MinY),
                        MathF.Min(part.MinZ, sb.Box.MinZ), MathF.Max(part.MaxX, sb.Box.MaxX),
                        MathF.Max(part.MaxY, sb.Box.MaxY), MathF.Max(part.MaxZ, sb.Box.MaxZ));
                }
                totalVerts += subVerts.Count;
                meshes.Add(mesh with
                {
                    LargestSubTriangles = largest, Tris = tris.ToArray(), SubVerts = subVerts.ToArray(),
                    SubFirst = subFirst, SubCount = subs.Count - subFirst,
                });
            }

            return new ConnectorProfile
            {
                Meshes = meshes.ToArray(),
                Subs = subs.ToArray(),
                PartBox = part.Empty ? null : part,
                Vertices = totalVerts,
            };
        }
    }

    /// <summary>
    /// Measure <paramref name="mdl"/> for the redundancy pass, positions and indices only.
    /// <paramref name="keepMaterial"/> must be the SAME predicate the emit loop filters with; null is the
    /// body-skin default, <see cref="Source.Keep"/>. Returns null on a model this cannot read.
    /// </summary>
    public static ConnectorProfile? ReadConnectorProfile(byte[] mdl, Func<string, bool>? keepMaterial = null)
    {
        Source src;
        try { src = ParseCached(mdl); }
        catch { return null; }

        var s = src.S;
        var keep = keepMaterial ?? IsBodySkinMaterial;
        var meshes = new List<ConnectorProfile.MeshProfile>();
        var subs = new List<ConnectorProfile.Sub>();
        var part = ConnectorProfile.Box.Nothing;
        int totalVerts = 0;
        Span<float> tmp = stackalloc float[4];

        // The same mesh selection the shell path makes (empty-mesh skip, then the material filter), so
        // the profile and the emit loop are describing one set of meshes.
        int mEnd = src.Lod0MeshIndex + src.Lod0MeshCount;
        for (int m = src.Lod0MeshIndex; m < mEnd && m < src.MeshCount; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > s.Length) break;
            ushort vc = BitConverter.ToUInt16(s, mo);
            if (vc == 0) continue;
            ushort matIdx = BitConverter.ToUInt16(s, mo + 8);
            if (matIdx >= src.MatNames.Count || !keep(src.MatNames[matIdx])) continue;

            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            VElem? posEl = null;
            foreach (var el in decl) if (el.Usage == UsePosition) { posEl = el; break; }
            if (posEl is not { } pe || pe.Stream > 2) continue;

            uint[] vbo = { BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                           BitConverter.ToUInt32(s, mo + 28) };
            byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
            if (bs[pe.Stream] == 0) continue;

            var pos = new float[vc * 3];
            bool ok = true;
            for (int k = 0; k < vc; k++)
            {
                int a = (int)(src.Vb + vbo[pe.Stream]) + k * bs[pe.Stream] + pe.Offset;
                // 16 bytes is the widest element ReadTyped touches, as in TryReadLod0Geometry.
                if (a < 0 || a + 16 > s.Length) { ok = false; break; }
                ReadTyped(s, a, pe.Type, tmp);
                pos[k * 3] = tmp[0]; pos[k * 3 + 1] = tmp[1]; pos[k * 3 + 2] = tmp[2];
            }
            if (!ok) continue;

            ushort subIdx = BitConverter.ToUInt16(s, mo + 10), subCount = BitConverter.ToUInt16(s, mo + 12);
            if (subCount == 0) continue;

            var tris = new List<ushort>();
            var subVerts = new List<ushort>();
            // Which vertices this submesh has already claimed. Stamped rather than cleared so the reset
            // between submeshes costs nothing.
            var seen = new int[vc];
            for (int i = 0; i < vc; i++) seen[i] = -1;

            int subFirst = subs.Count, largest = 0;
            for (int su = 0; su < subCount; su++)
            {
                int ss = src.SubmeshStart + (subIdx + su) * 16;
                if (ss + 16 > s.Length) break;
                uint so = BitConverter.ToUInt32(s, ss), sc = BitConverter.ToUInt32(s, ss + 4);
                uint attr = BitConverter.ToUInt32(s, ss + 8);

                int triFirst = tris.Count / 3, vertFirst = subVerts.Count;
                float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue;
                float mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;

                for (uint t = 0; t + 2 < sc; t += 3)
                {
                    int p = src.Ib + (int)(so + t) * 2;
                    if (p < 0 || p + 6 > s.Length) break;
                    ushort a = BitConverter.ToUInt16(s, p),
                           b = BitConverter.ToUInt16(s, p + 2),
                           c = BitConverter.ToUInt16(s, p + 4);
                    // A stale index must not reach another mesh's vertices — same guard as the geometry
                    // reader. A triangle dropped here is dropped from the measurement only.
                    if (a >= vc || b >= vc || c >= vc) continue;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    Claim(a); Claim(b); Claim(c);

                    void Claim(ushort v)
                    {
                        if (seen[v] == su) return;
                        seen[v] = su;
                        subVerts.Add(v);
                        float x = pos[v * 3], y = pos[v * 3 + 1], z = pos[v * 3 + 2];
                        if (x < mnx) mnx = x; if (x > mxx) mxx = x;
                        if (y < mny) mny = y; if (y > mxy) mxy = y;
                        if (z < mnz) mnz = z; if (z > mxz) mxz = z;
                    }
                }

                int triCount = tris.Count / 3 - triFirst;
                if (triCount > largest) largest = triCount;
                subs.Add(new ConnectorProfile.Sub(
                    m, su, attr, triCount,
                    new ConnectorProfile.Box(mnx, mny, mnz, mxx, mxy, mxz),
                    vertFirst, subVerts.Count - vertFirst,
                    triFirst, triCount));
            }

            if (tris.Count == 0) { subs.RemoveRange(subFirst, subs.Count - subFirst); continue; }

            // The part's extent, from its SUBSTANTIAL submeshes only: counting the rings makes the ring rule
            // circular (two parts' copies of one wrist ring each dropped on the other's authority). Taken after
            // the loop because "substantial" is relative to the largest submesh.
            for (int k = subFirst; k < subs.Count; k++)
            {
                var sb = subs[k];
                if (sb.Box.Empty || sb.Triangles < largest / 10) continue;
                part = new ConnectorProfile.Box(
                    MathF.Min(part.MinX, sb.Box.MinX), MathF.Min(part.MinY, sb.Box.MinY),
                    MathF.Min(part.MinZ, sb.Box.MinZ), MathF.Max(part.MaxX, sb.Box.MaxX),
                    MathF.Max(part.MaxY, sb.Box.MaxY), MathF.Max(part.MaxZ, sb.Box.MaxZ));
            }
            totalVerts += subVerts.Count;
            meshes.Add(new ConnectorProfile.MeshProfile(
                m, largest, pos, tris.ToArray(), subVerts.ToArray(), subFirst, subs.Count - subFirst));
        }

        if (meshes.Count == 0) return null;
        return new ConnectorProfile
        {
            Meshes = meshes.ToArray(),
            Subs = subs.ToArray(),
            PartBox = part.Empty ? null : part,
            Vertices = totalVerts,
        };
    }

    /// <summary>
    /// How close a vertex has to sit to other geometry to count as already drawn by it: 5 mm, in metres.
    /// Above the distance a connector is authored proud of the skin, below a body's vertex spacing.
    /// </summary>
    private const float CoincidenceEps = 0.005f;

    /// <summary>
    /// What fraction of a submesh's vertices must already be drawn by its siblings before it counts as a
    /// duplicate. High on purpose: a false positive is a bare band on the character, and a true duplicate
    /// scores near 100%.
    /// </summary>
    private const float CoincidenceFraction = 0.90f;

    /// <summary>Below this the fraction is not a statistic. Such a submesh is ring-sized anyway, and the
    /// ring rule owns it.</summary>
    private const int MinCoincidenceVerts = 16;

    /// <summary>Report a near miss from here up, so a threshold that is wrong on a body nobody measured
    /// says so in the log instead of just silently keeping (or eating) geometry.</summary>
    private const float NearMissFraction = 0.70f;

    /// <summary>
    /// Which of this source's submeshes are already drawn by something else. Returns MESH-ABSOLUTE,
    /// SUBMESH-RELATIVE keys, matching the emit loop's counters. The whole SOURCE is one pool (a connector
    /// shipped as its own mesh is otherwise invisible to both rules); hidden submeshes are neither candidates
    /// nor cover; candidates are walked LARGEST FIRST and judged only against geometry already KEPT, ties on
    /// ascending (mesh, submesh).
    /// </summary>
    /// <param name="otherPartBoxes">The extent of every OTHER part in this shell. EMPTY means this part is
    /// alone, so no ring is redundant.</param>
    /// <param name="isHidden">Attribute mask → is this submesh switched off by one of the pack's toggles.</param>
    /// <param name="eps">Coincidence distance; defaults to <see cref="CoincidenceEps"/>, overridable so the
    /// gated report can sweep it.</param>
    /// <param name="variantBits">Which attribute bits are IMC variant attributes (see <see cref="VariantBits"/>).
    /// Submeshes carrying different variants are alternatives, and neither is cover for the other.</param>
    /// <param name="keepAddOns">A submesh drawn only with a variant is never covered by untagged geometry — see
    /// <see cref="AddOnTo"/>. Set for the hands (SourceSpec.CoverNails), whose body-material nails are one.</param>
    internal static HashSet<(int Mesh, int Sub)> PlanConnectorDrops(
        ConnectorProfile profile, IReadOnlyList<ConnectorProfile.Box> otherPartBoxes,
        Func<uint, bool>? isHidden, Action<string>? diag, string label,
        out int droppedSubs, out int droppedTris, float eps = CoincidenceEps, uint variantBits = 0,
        bool keepAddOns = false)
    {
        var drops = new HashSet<(int Mesh, int Sub)>();
        droppedSubs = 0; droppedTris = 0;

        {
            // Which MeshProfile each submesh belongs to, so the cover grid can reach its positions. The
            // Sub's own Mesh field is the ABSOLUTE model mesh index, which is what the emit loop counts
            // with and not an index into Meshes.
            var meshOf = new int[profile.Subs.Length];
            for (int mi = 0; mi < profile.Meshes.Length; mi++)
            {
                var mp = profile.Meshes[mi];
                for (int k = 0; k < mp.SubCount; k++) meshOf[mp.SubFirst + k] = mi;
            }

            var live = new List<int>(profile.Subs.Length);
            for (int i = 0; i < profile.Subs.Length; i++)
            {
                var sub = profile.Subs[i];
                if (sub.TriCount == 0) continue;
                if (isHidden != null && isHidden(sub.AttrMask)) continue;
                live.Add(i);
            }
            // A part drawing one thing IS that thing — there is nothing beside it to be redundant against.
            if (live.Count < 2) return drops;

            live.Sort((x, y) =>
            {
                var a = profile.Subs[x];
                var b = profile.Subs[y];
                if (b.Triangles != a.Triangles) return b.Triangles.CompareTo(a.Triangles);
                if (a.Mesh != b.Mesh) return a.Mesh.CompareTo(b.Mesh);
                return a.Index.CompareTo(b.Index);
            });

            // The ring rule's scale is the largest DRAWN submesh source-wide, not MeshProfile.LargestSubTriangles:
            // a connector shipped as its own mesh is never small against its own mesh, and a hidden largest
            // submesh would push the threshold up (the profile is cached across composites that disagree on it).
            int largest = profile.Subs[live[0]].Triangles;

            var cover = new CoverGrid(eps);
            int kept = 0;
            var keptSubs = new List<int>();
            var partDrops = new List<int>();

            foreach (int si in live)
            {
                var sub = profile.Subs[si];
                bool drop;
                string why;

                if (sub.Triangles < largest / 10)
                {
                    // A seam ring: small alone is not evidence, so it is redundant only where a neighbouring
                    // part's extent encloses it.
                    drop = !sub.Box.Empty && otherPartBoxes.Count > 0
                        && BoxCovered(sub.Box, otherPartBoxes);
                    why = $"rule=ring, x {sub.Box.MinX:F3}..{sub.Box.MaxX:F3} "
                        + $"z {sub.Box.MinZ:F3}..{sub.Box.MaxZ:F3} "
                        + (drop ? "inside another part's extent" : "inside no other part's extent");
                }
                else
                {
                    // A kept sibling that is this submesh's VARIANT is not cover for it: the game draws one of
                    // the two, and dropping this one may drop the one being drawn. With keepAddOns, nor is an
                    // UNTAGGED sibling cover for a tagged one: a piece drawn only with a variant is an add-on to the
                    // surface, not a copy of it — a hand's body-material nails (atr_gv_a) lie on the fingertips, well
                    // inside the distance.
                    var against = cover;
                    bool NotCover(int k) => Alternatives(sub.AttrMask, profile.Subs[k].AttrMask, variantBits)
                                            || keepAddOns && AddOnTo(sub.AttrMask, profile.Subs[k].AttrMask, variantBits);
                    if (variantBits != 0 && keptSubs.Any(NotCover))
                    {
                        against = new CoverGrid(eps);
                        foreach (int k in keptSubs)
                            if (!NotCover(k))
                                against.AddSub(profile, meshOf, k);
                    }
                    float frac = against.CoveredFraction(profile, meshOf, si, out string by, out int bestHits);
                    drop = sub.VertCount >= MinCoincidenceVerts && frac >= CoincidenceFraction;
                    why = $"rule=coincident, {frac * 100:F0}% of {sub.VertCount} vertices within "
                        + $"{eps * 1000:F1}mm of {by} ({bestHits} hit)";
                    if (!drop && frac >= NearMissFraction && sub.VertCount >= MinCoincidenceVerts)
                        diag?.Invoke($"redundant near-miss: {label} mesh {sub.Mesh} sub {sub.Index} — "
                                   + $"{sub.Triangles} tri, {frac * 100:F0}% covered by {by} "
                                   + $"(threshold {CoincidenceFraction * 100:F0}%)");
                }

                if (drop)
                {
                    partDrops.Add(si);
                    diag?.Invoke($"redundant drop: {label} mesh {sub.Mesh} sub {sub.Index} — "
                               + $"{sub.Triangles} tri, y {sub.Box.MinY:F3}..{sub.Box.MaxY:F3}, {why}, "
                               + $"attrs 0x{sub.AttrMask:x}");
                }
                else
                {
                    kept++;
                    keptSubs.Add(si);
                    cover.AddSub(profile, meshOf, si);
                }
            }

            // The PART must never contribute nothing at all (a whole mesh going is legitimate). Its own token,
            // not "redundant drop:", which is the one marker for a removed submesh and must agree with the counters.
            if (kept == 0 && partDrops.Count > 0)
            {
                int biggest = partDrops[0];
                foreach (int d in partDrops)
                    if (profile.Subs[d].Triangles > profile.Subs[biggest].Triangles) biggest = d;
                partDrops.Remove(biggest);
                var b = profile.Subs[biggest];
                diag?.Invoke($"redundant keep: {label} would have been emptied — keeping mesh {b.Mesh} "
                           + $"sub {b.Index} ({b.Triangles} tri) regardless");
            }

            foreach (int d in partDrops)
            {
                var sub = profile.Subs[d];
                drops.Add((sub.Mesh, sub.Index));
                droppedSubs++;
                droppedTris += sub.Triangles;
            }
        }

        return drops;
    }

    /// <summary>How close two parts' vertices must be to count as the SAME authored vertex: a tenth of a
    /// millimetre, a tolerance rather than a search radius.</summary>
    private const float JoinWeld = 0.0001f;

    /// <summary>
    /// What fraction of a component's vertices another part must draw before the component counts as that
    /// part's geometry duplicated. Coarse on purpose: components are whole regions bounded by an exact ring,
    /// so the answer is near 0 or near 1.
    /// </summary>
    private const float FlapCovered = 0.90f;

    /// <summary>How much smaller than the surface it meets at a join a component has to be before it counts
    /// as the margin rather than the body. A quarter; real margins sit at a sixth or below.</summary>
    private const float FlapShare = 0.25f;

    /// <summary>What fraction of an inner-join component's vertices must sit BEHIND its neighbour's surface
    /// for it to count as a lap tucked under that neighbour. A true lap scores 100%; counted on faces, nothing
    /// else scores above 0.</summary>
    private const float FlapBehind = 0.95f;

    /// <summary>
    /// The join cut: RING (vertices shared with another part to <see cref="JoinWeld"/>), SPLIT (flood-fill
    /// triangles, never crossing an edge with both ends on a ring), JUDGE (a component another part
    /// overwhelmingly draws goes). The returned set excludes the ring itself, so the emit loop drops a
    /// triangle if ANY corner is in it.
    /// </summary>
    /// <summary>
    /// Fill each source's <see cref="Source.JoinRing"/>: the positions of its drawn vertices that coincide, to
    /// <see cref="JoinWeld"/>, with a drawn vertex of ANOTHER source. The same rings <see cref="PlanJoinCut"/>
    /// cuts at, kept by position so a pass working on one mesh's own vertex order can look them up.
    /// </summary>
    private static void MarkJoinRings(IReadOnlyList<ConnectorProfile?> profiles, IReadOnlyList<Source> parsed)
    {
        (long, long, long) Cell(Vec3 p) => ((long)MathF.Floor(p.X / JoinWeld), (long)MathF.Floor(p.Y / JoinWeld),
                                            (long)MathF.Floor(p.Z / JoinWeld));
        var grid = new Dictionary<(long, long, long), List<(int Src, Vec3 P)>>();
        void Each(int src, Action<Vec3> act)
        {
            if (profiles[src] is not { } p) return;
            foreach (var mesh in p.Meshes)
            {
                var used = new bool[mesh.Pos.Length / 3];
                foreach (ushort u in mesh.Tris) if (u < used.Length) used[u] = true;
                for (int v = 0; v < used.Length; v++)
                    if (used[v]) act(new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2]));
            }
        }
        for (int i = 0; i < profiles.Count; i++)
        {
            int src = i;
            Each(src, q => (grid.TryGetValue(Cell(q), out var l) ? l : grid[Cell(q)] = []).Add((src, q)));
        }
        for (int i = 0; i < profiles.Count && i < parsed.Count; i++)
        {
            int src = i;
            var ring = new Dictionary<(long, long, long), List<Vec3>>();
            Each(src, q =>
            {
                var (cx, cy, cz) = Cell(q);
                for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                for (long dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var others)) continue;
                    foreach (var (os, op) in others)
                        if (os != src && Dist(q, op) <= JoinWeld)
                        {
                            (ring.TryGetValue(Cell(q), out var l) ? l : ring[Cell(q)] = []).Add(q);
                            return;
                        }
                }
            });
            parsed[i].JoinRing = ring.Count > 0 ? ring : null;
        }
    }

    /// <summary>
    /// Per vertex of <paramref name="pos"/>, whether it sits on one of <paramref name="ring"/>'s join-ring
    /// positions (to <see cref="JoinWeld"/>). Null when no vertex does.
    /// </summary>
    private static bool[]? JoinPins(Vec3[] pos, Dictionary<(long, long, long), List<Vec3>>? ring)
    {
        if (ring == null) return null;
        bool[]? pins = null;
        for (int i = 0; i < pos.Length; i++)
        {
            var q = pos[i];
            long cx = (long)MathF.Floor(q.X / JoinWeld), cy = (long)MathF.Floor(q.Y / JoinWeld),
                 cz = (long)MathF.Floor(q.Z / JoinWeld);
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            for (long dz = -1; dz <= 1; dz++)
            {
                if (!ring.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                foreach (var r in list)
                    if (Dist(q, r) <= JoinWeld) { (pins ??= new bool[pos.Length])[i] = true; goto next; }
            }
            next:;
        }
        return pins;
    }

    internal static Dictionary<int, HashSet<ushort>>[] PlanJoinCut(
        IReadOnlyList<ConnectorProfile?> profiles, float coverEps, Action<string>? diag,
        out int flapVerts)
    {
        return new JoinCutPlan(profiles, coverEps, diag).Run(out flapVerts);
    }

    /// <summary>
    /// What fraction of a part-join component's vertices must lie within the extent of the part it is joined
    /// to before its size alone can mark it as that join's margin. A margin really inside its neighbour clears
    /// this trivially; a small surface drawn in FRONT of the skin, bounded by the ring, does not.
    /// </summary>
    private const float FlapInJoinedPart = 0.8f;

    /// <summary>
    /// How far outside a joined part's extent a margin may reach and still count as inside it — the depth a
    /// lap tucks under its neighbour's surface, with room to spare. See <see cref="FlapInJoinedPart"/>.
    /// </summary>
    private const float FlapTuckReach = 0.01f;

    /// <summary>
    /// How many of a component's vertices sit BEHIND the surface of another component: the sign of
    /// <c>(p - closest) . n</c> against the nearest triangle, normal from the winding. Ring vertices are
    /// skipped (they sit exactly on the surface). A vertex counts only when its closest point is on the
    /// triangle's FACE: a ring carrying on PAST its neighbour's rim clamps to the rim, where the sign means nothing.
    /// </summary>
    /// <remarks>
    /// The nearest triangle is found through a grid searched outward in shells; the search stops only once no
    /// unvisited cell can hold anything closer, falls back to a full walk when nothing is near, and breaks a
    /// distance tie by <paramref name="against"/> order.
    /// </remarks>
    private static (int Behind, int Of) BehindFraction(
        ConnectorProfile.MeshProfile mesh, IEnumerable<ushort> verts, bool[] ring, List<int> against)
    {
        const float H = 0.01f;          // cell size: a centimetre, against body edges of a few millimetres
        const long MaxCells = 4096;     // a triangle stamping more than this is walked linearly instead
        const int MaxShells = 8;        // beyond this, nothing is near — do the full walk

        Vec3 Corner(int t, int k)
        {
            int i = mesh.Tris[t * 3 + k];
            return new Vec3(mesh.Pos[i * 3], mesh.Pos[i * 3 + 1], mesh.Pos[i * 3 + 2]);
        }
        static bool Usable(float v) => float.IsFinite(v) && MathF.Abs(v) < 1000f;
        static long Cell(float v) => (long)MathF.Floor(v / H);

        // Bucket the neighbour, by position in `against` so the tie-break can see the original order.
        var cells = new Dictionary<(long, long, long), List<int>>();
        var linear = new List<int>();
        for (int k = 0; k < against.Count; k++)
        {
            var (a, b, c) = (Corner(against[k], 0), Corner(against[k], 1), Corner(against[k], 2));
            float mnx = MathF.Min(a.X, MathF.Min(b.X, c.X)), mxx = MathF.Max(a.X, MathF.Max(b.X, c.X));
            float mny = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), mxy = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
            float mnz = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), mxz = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            if (!Usable(mnx) || !Usable(mxx) || !Usable(mny) || !Usable(mxy) || !Usable(mnz) || !Usable(mxz))
            { linear.Add(k); continue; }
            long x0 = Cell(mnx), x1 = Cell(mxx), y0 = Cell(mny), y1 = Cell(mxy), z0 = Cell(mnz), z1 = Cell(mxz);
            if ((x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > MaxCells) { linear.Add(k); continue; }
            for (long x = x0; x <= x1; x++)
            for (long y = y0; y <= y1; y++)
            for (long z = z0; z <= z1; z++)
            {
                if (!cells.TryGetValue((x, y, z), out var list)) cells[(x, y, z)] = list = [];
                list.Add(k);
            }
        }

        var seen = new int[against.Count];
        int query = 0;
        int behind = 0, of = 0;
        foreach (ushort v in verts)
        {
            if (ring[v]) continue;
            var p = new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2]);
            float best = float.MaxValue;
            int bestOrder = int.MaxValue;
            float sign = 0f;
            bool onFace = false;
            query++;

            void Test(int k)
            {
                if (seen[k] == query) return;
                seen[k] = query;
                var (a, b, c) = (Corner(against[k], 0), Corner(against[k], 1), Corner(against[k], 2));
                var q = ClosestOnTriangle(p, a, b, c);
                float d = Dist(p, q);
                if (d > best || (d == best && k > bestOrder)) return;
                best = d;
                bestOrder = k;
                float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
                float wx = c.X - a.X, wy = c.Y - a.Y, wz = c.Z - a.Z;
                float nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                sign = (p.X - q.X) * nx + (p.Y - q.Y) * ny + (p.Z - q.Z) * nz;
                // The closest point is on the FACE when the offset to it runs along the normal, and clamped to
                // an edge or corner when it does not. Within about 6 degrees, to allow for float noise.
                onFace = sign * sign >= 0.99f * d * d * (nx * nx + ny * ny + nz * nz);
            }

            foreach (int k in linear) Test(k);
            bool settled = false;
            if (cells.Count > 0 && Usable(p.X) && Usable(p.Y) && Usable(p.Z))
            {
                long cx = Cell(p.X), cy = Cell(p.Y), cz = Cell(p.Z);
                for (int r = 0; r <= MaxShells && !settled; r++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != r) continue;
                        if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                            foreach (int k in list) Test(k);
                    }
                    // Every cell not yet visited is at least r cells away, so nothing in it is closer than
                    // r*H. Strictly closer, so a tie at exactly that distance still gets looked at.
                    settled = best < r * H;
                }
            }
            if (!settled)
                for (int k = 0; k < against.Count; k++) Test(k);

            of++;
            if (best < float.MaxValue && onFace && sign < 0f) behind++;
        }
        return (behind, of);
    }

    /// <summary>
    /// Rings INSIDE one part: positions two different submeshes of the same mesh both carry a vertex at.
    /// A body laps its regions (thigh, knee, shin) over each other as it laps the parts. Reported, not acted on.
    /// </summary>
    internal static void DescribeInternalRings(ConnectorProfile profile, float weld, Action<string> report)
    {
        foreach (var mesh in profile.Meshes)
        {
            if (mesh.SubCount < 2) continue;

            // Which submeshes each vertex belongs to.
            var subsOf = new Dictionary<ushort, HashSet<int>>();
            for (int k = 0; k < mesh.SubCount; k++)
            {
                var sub = profile.Subs[mesh.SubFirst + k];
                for (int i = sub.VertFirst; i < sub.VertFirst + sub.VertCount; i++)
                {
                    ushort v = mesh.SubVerts[i];
                    if (!subsOf.TryGetValue(v, out var set)) subsOf[v] = set = [];
                    set.Add(sub.Index);
                }
            }

            var at = new Dictionary<(long, long, long), List<ushort>>();
            (long, long, long) Cell(int v) => ((long)MathF.Floor(mesh.Pos[v * 3] / weld),
                                               (long)MathF.Floor(mesh.Pos[v * 3 + 1] / weld),
                                               (long)MathF.Floor(mesh.Pos[v * 3 + 2] / weld));
            foreach (var v in subsOf.Keys)
            {
                var key = Cell(v);
                if (!at.TryGetValue(key, out var list)) at[key] = list = [];
                list.Add(v);
            }

            // Pairs at one position belonging to DIFFERENT submeshes — the stitch between two regions,
            // as opposed to a UV seam, which duplicates a vertex within one submesh.
            var pairs = new Dictionary<(int, int), (int Count, float Lo, float Hi)>();
            foreach (var list in at.Values)
            {
                if (list.Count < 2) continue;
                for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                foreach (int sa in subsOf[list[i]])
                foreach (int sb in subsOf[list[j]])
                {
                    if (sa == sb) continue;
                    var key = sa < sb ? (sa, sb) : (sb, sa);
                    float y = mesh.Pos[list[i] * 3 + 1];
                    var got = pairs.TryGetValue(key, out var g)
                        ? g : (Count: 0, Lo: float.MaxValue, Hi: float.MinValue);
                    pairs[key] = (got.Count + 1, MathF.Min(got.Lo, y), MathF.Max(got.Hi, y));
                }
            }

            foreach (var ((sa, sb), (n, lo, hi)) in pairs.OrderByDescending(p => p.Value.Count))
                if (n > 10)
                    report($"mesh {mesh.Index}: sub {sa} and sub {sb} share {n} vertex position(s), "
                         + $"over y {lo:F3}..{hi:F3}");
        }
    }

    internal static void DescribeJoinRings(IReadOnlyList<ConnectorProfile?> profiles, float weld,
                                           Action<string> report)
    {
        for (int a = 0; a < profiles.Count; a++)
        {
            if (profiles[a] is not { } pa) continue;

            // Every vertex of part A, bucketed at the weld radius so the lookup is a handful of cells.
            var grid = new Dictionary<(long, long, long), List<Vec3>>();
            foreach (var mesh in pa.Meshes)
                for (int v = 0; v < mesh.Pos.Length / 3; v++)
                {
                    var p = new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2]);
                    var key = Cell(p);
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
                    list.Add(p);
                }

            for (int b = 0; b < profiles.Count; b++)
            {
                if (a == b || profiles[b] is not { } pb) continue;
                int shared = 0;
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var mesh in pb.Meshes)
                    for (int v = 0; v < mesh.Pos.Length / 3; v++)
                    {
                        var p = new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2]);
                        if (!Near(p)) continue;
                        shared++;
                        if (p.Y < lo) lo = p.Y;
                        if (p.Y > hi) hi = p.Y;
                    }
                if (shared > 0)
                    report($"part {b} shares {shared} vertex position(s) with part {a}, "
                         + $"over y {lo:F4}..{hi:F4} ({(hi - lo) * 1000:F1}mm tall)");
            }

            bool Near(Vec3 p)
            {
                var (cx, cy, cz) = Cell(p);
                for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                for (long dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                        foreach (var q in list)
                            if (Dist(p, q) <= weld) return true;
                return false;
            }

            (long, long, long) Cell(Vec3 p)
                => ((long)MathF.Floor(p.X / weld),
                    (long)MathF.Floor(p.Y / weld),
                    (long)MathF.Floor(p.Z / weld));
        }
    }

    /// <summary>
    /// Every pair of this source's submeshes that draw any of the same surface, with how much and where.
    /// Diagnostic only. The Y extent of the covered vertices tells a displaced duplicate (covered evenly over
    /// its height) from an overlapping join (covered only in a band at one end).
    /// </summary>
    internal static void DescribeOverlaps(ConnectorProfile profile, float eps, Action<string> report)
    {
        var meshOf = new int[profile.Subs.Length];
        for (int mi = 0; mi < profile.Meshes.Length; mi++)
        {
            var mp = profile.Meshes[mi];
            for (int k = 0; k < mp.SubCount; k++) meshOf[mp.SubFirst + k] = mi;
        }

        for (int a = 0; a < profile.Subs.Length; a++)
        for (int b = 0; b < profile.Subs.Length; b++)
        {
            if (a == b || profile.Subs[a].Triangles < profile.Subs[b].Triangles) continue;
            var grid = new CoverGrid(eps);
            grid.AddSub(profile, meshOf, a);
            float f = grid.CoveredFraction(profile, meshOf, b, out _, out _, out float lo, out float hi);
            if (f <= 0f) continue;
            var sa = profile.Subs[a];
            var sb = profile.Subs[b];
            report($"mesh {sb.Mesh} sub {sb.Index} ({sb.Triangles} tri, y {sb.Box.MinY:F3}..{sb.Box.MaxY:F3}) "
                 + $"is {f * 100:F0}% drawn by mesh {sa.Mesh} sub {sa.Index} — covered over y {lo:F3}..{hi:F3}");
        }
    }

    /// <summary>
    /// The surface a SOURCE's kept submeshes occupy, as a uniform voxel hash across meshes. Cover is bucketed
    /// by TRIANGLE, so the test is point-to-SURFACE and a re-tessellated duplicate still scores. The cell size
    /// equals <see cref="CoincidenceEps"/> and every candidate is settled by an exact point-triangle distance,
    /// so the grid affects speed only, never the verdict.
    /// </summary>
    private sealed class CoverGrid
    {
        /// <summary>Above this many cells a triangle is tested linearly instead of bucketed. A degenerate
        /// or enormous triangle would otherwise stamp an unbounded number of cells.</summary>
        private const int MaxCellsPerTriangle = 4096;

        /// <summary>How close counts as already drawn. A field rather than the constant so the gated report
        /// can sweep it.</summary>
        private readonly float eps;

        /// <summary>Every covering triangle by VALUE (three corners and a caller-chosen tag), so one grid can
        /// hold geometry from several sources at once.</summary>
        private readonly List<(Vec3 A, Vec3 B, Vec3 C, int Tag)> tris = [];

        private readonly Dictionary<long, List<int>> cells = new();

        /// <summary>
        /// Triangles the grid would not take (too big to bucket, or a corner that is not a coordinate). Still
        /// exact cover, tested by walking this list; each carries its own bounding box so that walk stays
        /// cheap. Capping the list instead silently removes cover and changes verdicts.
        /// </summary>
        private readonly List<(int Tri, float MnX, float MnY, float MnZ,
                                        float MxX, float MxY, float MxZ)> oversized = [];

        public CoverGrid(float eps = CoincidenceEps) => this.eps = eps;

        public bool Empty => cells.Count == 0 && oversized.Count == 0;

        /// <summary>Add every triangle of submesh <paramref name="subSlot"/>. Tagged with that slot unless
        /// the caller needs a tag of its own — across sources a slot alone does not identify a submesh.
        /// </summary>
        public void AddSub(ConnectorProfile profile, int[] meshOf, int subSlot, int? tag = null)
        {
            var sub = profile.Subs[subSlot];
            var mesh = profile.Meshes[meshOf[subSlot]];
            for (int k = 0; k < sub.TriCount; k++)
            {
                int tri = sub.TriFirst + k;
                ushort ia = mesh.Tris[tri * 3], ib = mesh.Tris[tri * 3 + 1], ic = mesh.Tris[tri * 3 + 2];
                Add(new Vec3(mesh.Pos[ia * 3], mesh.Pos[ia * 3 + 1], mesh.Pos[ia * 3 + 2]),
                    new Vec3(mesh.Pos[ib * 3], mesh.Pos[ib * 3 + 1], mesh.Pos[ib * 3 + 2]),
                    new Vec3(mesh.Pos[ic * 3], mesh.Pos[ic * 3 + 1], mesh.Pos[ic * 3 + 2]),
                    tag ?? subSlot);
            }
        }

        /// <summary>Add one covering triangle by value, tagged with whatever the caller wants back out.</summary>
        public void Add(Vec3 a, Vec3 b, Vec3 c, int tag)
        {
            int t = tris.Count;
            tris.Add((a, b, c, tag));
            float mnx = MathF.Min(a.X, MathF.Min(b.X, c.X)), mxx = MathF.Max(a.X, MathF.Max(b.X, c.X));
            float mny = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), mxy = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
            float mnz = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), mxz = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            // A triangle whose corners are not finite, or further apart than a character could be, is a
            // misread declaration or a corrupt file and must not size a loop. Tested on the COORDINATES,
            // before they become cell indices, which saturate past about 6.4e6 units.
            if (!Finite(mnx, mxx) || !Finite(mny, mxy) || !Finite(mnz, mxz))
            { oversized.Add((t, mnx, mny, mnz, mxx, mxy, mxz)); return; }

            long x0 = Cell(mnx), x1 = Cell(mxx), y0 = Cell(mny), y1 = Cell(mxy),
                 z0 = Cell(mnz), z1 = Cell(mxz);
            // In long throughout. As int, each (hi - lo + 1) wrapped before it was ever widened.
            long span = (x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (span > MaxCellsPerTriangle)
            { oversized.Add((t, mnx, mny, mnz, mxx, mxy, mxz)); return; }
            for (long x = x0; x <= x1; x++)
            for (long y = y0; y <= y1; y++)
            for (long z = z0; z <= z1; z++)
            {
                long key = Key(x, y, z);
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
                list.Add(t);
            }
        }

        /// <summary>Is this axis's extent real geometry — finite, and inside the range a cell index can
        /// hold? A triangle that fails goes in the linear list, where it is still tested exactly and costs
        /// nothing but time.</summary>
        private static bool Finite(float lo, float hi)
            => float.IsFinite(lo) && float.IsFinite(hi) && MathF.Abs(lo) < CoordCeiling
            && MathF.Abs(hi) < CoordCeiling;

        /// <summary>Roughly a kilometre, against a character a little over a metre and a half tall. Nothing
        /// legitimate comes near it, and it keeps every cell index inside int range with room to spare.</summary>
        private const float CoordCeiling = 1000f;

        /// <summary>
        /// What fraction of submesh <paramref name="subSlot"/>'s distinct vertices already sit on the covered
        /// surface: the union per vertex, NOT the best single neighbour. <paramref name="by"/> is for the log only.
        /// </summary>
        public float CoveredFraction(ConnectorProfile profile, int[] meshOf, int subSlot,
                                     out string by, out int bestHits)
            => CoveredFraction(profile, meshOf, subSlot, out by, out bestHits, out _, out _);

        /// <param name="coveredLo">The Y extent of the vertices that WERE covered: a displaced duplicate is
        /// covered evenly across its height, two regions that merely MEET only in a band at the join.</param>
        public float CoveredFraction(ConnectorProfile profile, int[] meshOf, int subSlot,
                                     out string by, out int bestHits,
                                     out float coveredLo, out float coveredHi)
        {
            by = "nothing"; bestHits = 0;
            coveredLo = float.MaxValue; coveredHi = float.MinValue;
            var sub = profile.Subs[subSlot];
            if (sub.VertCount == 0 || Empty) return 0f;

            var mesh = profile.Meshes[meshOf[subSlot]];
            var credit = new Dictionary<int, int>();
            int covered = 0;
            for (int i = sub.VertFirst; i < sub.VertFirst + sub.VertCount; i++)
            {
                int v = mesh.SubVerts[i];
                var p = new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2]);
                int hit = Nearest(p);
                if (hit < 0) continue;
                covered++;
                if (p.Y < coveredLo) coveredLo = p.Y;
                if (p.Y > coveredHi) coveredHi = p.Y;
                int owner = tris[hit].Tag;
                credit[owner] = credit.TryGetValue(owner, out var n) ? n + 1 : 1;
            }
            foreach (var (k, n) in credit)
                if (n > bestHits)
                {
                    bestHits = n;
                    var o = profile.Subs[k];
                    by = $"mesh {o.Mesh} sub {o.Index}";
                }
            return (float)covered / sub.VertCount;
        }

        /// <summary>A covering triangle within eps of <paramref name="p"/>, or -1. The 27-cell probe covers
        /// [p-eps, p+eps] because a triangle is stamped into every cell its bounding box touches.</summary>
        private int Nearest(Vec3 p, int exclude = int.MinValue)
        {
            // Only the bucketed half needs a finite point; the linear list's exact distance test rejects a
            // non-finite point on its own.
            if (Finite(p.X, p.X) && Finite(p.Y, p.Y) && Finite(p.Z, p.Z))
            {
                long cx = Cell(p.X), cy = Cell(p.Y), cz = Cell(p.Z);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var list)) continue;
                    foreach (int t in list)
                        if (tris[t].Tag != exclude && Within(p, t)) return t;
                }
            }
            foreach (var (t, mnx, mny, mnz, mxx, mxy, mxz) in oversized)
            {
                if (tris[t].Tag == exclude) continue;
                // The box reject, which is what makes walking this list affordable. Written so a NaN bound
                // fails every comparison and falls through to the exact test, which rejects it properly.
                if (p.X < mnx - eps || p.X > mxx + eps
                 || p.Y < mny - eps || p.Y > mxy + eps
                 || p.Z < mnz - eps || p.Z > mxz + eps) continue;
                if (Within(p, t)) return t;
            }
            return -1;
        }

        private bool Within(Vec3 p, int t)
        {
            var (a, b, c) = Corners(t);
            return Dist(p, ClosestOnTriangle(p, a, b, c)) <= eps;
        }

        private (Vec3 A, Vec3 B, Vec3 C) Corners(int t)
        {
            var (a, b, c, _) = tris[t];
            return (a, b, c);
        }

        /// <summary>The tag of a covering triangle within eps of <paramref name="p"/>, or -1.</summary>
        /// <param name="exclude">A tag to ignore, for when the grid holds the geometry being ASKED about:
        /// every vertex of a part sits on its own surface at distance zero.</param>
        public int CoveredBy(Vec3 p, int exclude = int.MinValue)
        {
            int hit = Nearest(p, exclude);
            return hit < 0 ? -1 : tris[hit].Tag;
        }

        // long, and only ever called on a coordinate Finite has passed — so the conversion cannot saturate
        // and the arithmetic around it cannot wrap.
        private long Cell(float v) => (long)MathF.Floor(v / eps);

        // Collisions only merge two buckets, which adds candidates that the exact distance test then
        // rejects. They can never lose one, because insertion and probe use the same function.
        private static long Key(long x, long y, long z)
            => x * 73856093L ^ y * 19349663L ^ z * 83492791L;
    }

    /// <summary>Is <paramref name="box"/> contained in <paramref name="cover"/>, on all three axes?
    /// <para/>
    /// A hair of tolerance: geometry is authored to MEET, so extents abut rather than overlap, and an exact
    /// containment test would keep every ring that pokes a fraction past its neighbour's edge.</summary>
    private static bool BoxCovered(ConnectorProfile.Box box, ConnectorProfile.Box cover)
    {
        const float Slack = 0.01f;
        return !box.Empty && !cover.Empty
            && box.MinX >= cover.MinX - Slack && box.MaxX <= cover.MaxX + Slack
            && box.MinY >= cover.MinY - Slack && box.MaxY <= cover.MaxY + Slack
            && box.MinZ >= cover.MinZ - Slack && box.MaxZ <= cover.MaxZ + Slack;
    }

    /// <summary>Is <paramref name="box"/> contained in ANY ONE of <paramref name="covers"/>?
    /// <para/>
    /// One, not their union. A ring straddling the edge between two neighbours is drawn entirely by
    /// neither, and the evidence for dropping it has to be a part that draws all of it.</summary>
    private static bool BoxCovered(ConnectorProfile.Box box, IReadOnlyList<ConnectorProfile.Box> covers)
    {
        foreach (var cover in covers)
            if (BoxCovered(box, cover)) return true;
        return false;
    }

    private static sbyte[]? MeshSides(Source src, int m, ushort vc, VElem[] decl, uint[] vbo, byte[] bs,
        out int conflicts, out int straddling)
    {
        conflicts = 0;
        straddling = 0;
        var s = src.S;
        VElem? pos = null;
        foreach (var el in decl) if (el.Usage == UsePosition) { pos = el; break; }
        if (pos is not { } pe) return null;

        var xs = new float[vc];
        Span<float> tmp = stackalloc float[4];
        for (int i = 0; i < vc; i++)
        {
            ReadTyped(s, src.Vb + (int)vbo[pe.Stream] + i * bs[pe.Stream] + pe.Offset, pe.Type, tmp);
            xs[i] = tmp[0];
        }

        int mo = src.MeshStart + m * 36;
        ushort srcSubIdx = BitConverter.ToUInt16(s, mo + 10), srcSubCount = BitConverter.ToUInt16(s, mo + 12);
        var tris = new List<ushort>();
        for (int su = 0; su < srcSubCount; su++)
        {
            int ss = src.SubmeshStart + (srcSubIdx + su) * 16;
            uint so = BitConverter.ToUInt32(s, ss), sc = BitConverter.ToUInt32(s, ss + 4);
            for (uint t = 0; t + 2 < sc; t += 3)
            {
                int p = src.Ib + (int)(so + t) * 2;
                if (p + 5 >= s.Length) break;
                tris.Add(BitConverter.ToUInt16(s, p));
                tris.Add(BitConverter.ToUInt16(s, p + 2));
                tris.Add(BitConverter.ToUInt16(s, p + 4));
            }
        }
        return SurfaceMirror.AssignSides(xs, tris, out conflicts, out straddling);
    }

    /// <summary>
    /// Per-vertex region weight for one mesh: how much of the vertex is skinned to any of
    /// <paramref name="bones"/>, clamped to 1. Null when this mesh's bone table names none of them (the cheap
    /// early-out). The bones define the region because they are on the game's own skeleton, so every body has
    /// them. Weights are read through the mesh's own bone table into the model's names, as
    /// <see cref="TryReadLod0Geometry(byte[], out float[], out float[],
    /// out int[], out (string, float)[][], out float[], bool, bool, Func{string, bool})"/> does.
    /// </summary>
    /// <param name="losesTo">Bones that, where they outweigh <paramref name="bones"/>, mean the vertex belongs
    /// to them instead: the hip's influence runs into the thighs, where the surface is two legs, not two cheeks.</param>
    internal static float[]? MeshRegionWeights(Source src, int m, ushort vc, VElem[] decl, uint[] vbo,
                                              byte[] bs, string[] bones, string[]? losesTo = null)
    {
        var s = src.S;
        int mo = src.MeshStart + m * 36;
        if (mo + 36 > s.Length) return null;
        ushort meshBoneTbl = BitConverter.ToUInt16(s, mo + 14);
        if (meshBoneTbl >= src.BoneTables.Length) return null;
        var boneTbl = src.BoneTables[meshBoneTbl];

        // Which LOCAL indices are the region's bones, resolved once per mesh so a mesh with none of them
        // skips the per-vertex scan.
        Span<bool> isRegion = stackalloc bool[256];
        Span<bool> isRival = stackalloc bool[256];
        bool anyBone = false;
        for (int i = 0; i < boneTbl.Length && i < 256; i++)
        {
            if (boneTbl[i] >= src.BoneNames.Length) continue;
            var nm = src.BoneNames[boneTbl[i]];
            foreach (var want in bones)
                if (string.Equals(nm, want, StringComparison.OrdinalIgnoreCase))
                { isRegion[i] = true; anyBone = true; break; }
            if (losesTo == null) continue;
            foreach (var rival in losesTo)
                if (string.Equals(nm, rival, StringComparison.OrdinalIgnoreCase))
                { isRival[i] = true; break; }
        }
        if (!anyBone) return null;

        VElem? wEl = null, iEl = null;
        foreach (var el in decl)
        {
            if (el.Usage == UseBlendWeight) wEl ??= el;
            else if (el.Usage == UseBlendIndices) iEl ??= el;
        }
        if (wEl is not { } we || iEl is not { } ie || we.Stream > 2 || ie.Stream > 2) return null;

        int nInf = BlendCount(we.Type);
        var outW = new float[vc];
        bool any = false;
        for (int k = 0; k < vc; k++)
        {
            int wa = src.Vb + (int)vbo[we.Stream] + k * bs[we.Stream] + we.Offset;
            int ia = src.Vb + (int)vbo[ie.Stream] + k * bs[ie.Stream] + ie.Offset;
            if (wa < 0 || ia < 0 || wa + nInf > s.Length || ia + nInf > s.Length) continue;
            float acc = 0f, rival = 0f;
            for (int q = 0; q < nInf; q++)
            {
                int local = s[ia + q];
                if (local >= 256) continue;
                if (isRegion[local]) acc += s[wa + q] / 255f;
                else if (isRival[local]) rival += s[wa + q] / 255f;
            }
            if (acc <= 0f || rival >= acc) continue;
            outW[k] = MathF.Min(1f, acc);
            any = true;
        }
        return any ? outW : null;
    }
}
