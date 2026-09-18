using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    /// <summary>Most a socket fit may move a vertex: a bound on the fit going wrong, not on the repair.</summary>
    private const float CapFitMaxMove = 0.0026f;

    /// <summary>Most the per-socket bound may grow beyond <see cref="CapFitMaxMove"/> for a socket larger than the median.</summary>
    private const float CapFitMoveScaleMax = 2.0f;

    /// <summary>Where the fade from the fitted position back to the resolved one begins, as a fraction of the socket's radius.</summary>
    private const float CapFitFeatherStart = 0.6f;

    /// <summary>Smoothing passes over a socket patch once fitted: enough to take the crease out of the seam, not to move the nail.</summary>
    private const int CapRelaxPasses = 8;

    /// <summary>How far each pass eases a vertex toward the average of its neighbours.</summary>
    private const float CapRelaxWeight = 0.5f;

    /// <summary>Furthest relaxing may carry a vertex from where the fit put it; unbounded, Laplacian smoothing would pull the nail flat.</summary>
    private const float CapRelaxMaxDrift = 0.0008f;

    /// <summary>Most the per-socket relax drift may grow beyond <see cref="CapRelaxMaxDrift"/> for a socket larger than the median.</summary>
    private const float CapRelaxDriftScaleMax = 4.0f;

    /// <summary>Rings to walk out from a socket patch looking for landed vertices to fit against.</summary>
    private const int CapFitAnchorRings = 6;

    /// <summary>Fewest anchors before a patch is fitted rather than left alone; enough that the fit describes the neighbourhood.</summary>
    private const int CapFitMinAnchors = 8;

    /// <summary>Relaxation sweeps used to solve the cap's shape-preserving fit; zero disables it and takes the raw landings.</summary>
    private const int CapShapePasses = 48;

    /// <summary>How hard a landing pulls, against the cap's own curvature; the landing is the noisy term.</summary>
    private const float CapShapeFollow = 0.20f;

    /// <summary>How much further off the skin the shape fit may leave a vertex than its landing was.</summary>
    private const float CapShapeLiftAllow = 0.0004f;

    /// <summary>Halvings used to walk a lifting move back toward its landing before giving up on it.</summary>
    private const int CapShapeBackoffSteps = 6;

    /// <summary>Distinct landings kept per cap vertex before the seam pass chooses between them.</summary>
    private const int ProjectCandidates = 6;

    /// <summary>
    /// How far apart two landings must be in UV to count as different places; below this they are the
    /// same patch of atlas reached through neighbouring triangles.
    /// </summary>
    private const float ProjectMergeUV = 0.004f;

    /// <summary>Cap edge lengths a rival landing may sit further away and still be considered.</summary>
    private const float ProjectSeamSlack = 1.5f;

    /// <summary>Sweeps of the agreement pass.</summary>
    private const int ProjectSeamPasses = 24;

    /// <summary>UV span across one cap face that means it has straddled a seam rather than covered texture.</summary>
    private const float ProjectSeamSpan = 0.10f;

    /// <summary>
    /// Multiples of the cap's median UV stretch (UV distance per unit of 3D distance) at which an edge is
    /// taken to cross a cut in the body's atlas rather than to cover texture.
    /// </summary>
    private const float ProjectSeamStretch = 8f;

    /// <summary>Weight on staying near, relative to agreeing with the neighbours, in the seam pass.</summary>
    private const float ProjectNearBias = 1.0f;

    /// <summary>"PTCB" — the authored cap's binding to the body, see <see cref="BakeCapBind"/>.</summary>
    private const uint CapBindMagic = 0x42435450;

    /// <summary>
    /// Where the authored cap sits, expressed so it survives a change of foot: per vertex a coordinate in
    /// the body's UV atlas, how far off that surface it sits along the normal, and which side of the body
    /// it is on. The side matters because the atlas is mirrored between the two feet.
    /// The reference foot is not shipped: it is somebody's body mod. Only these numbers per vertex travel.
    /// </summary>
    /// <param name="offsetsFrom">
    /// An existing binding to take the offsets from: the atlas coordinate is per-body, but the offset is
    /// how high the cap was modelled above the skin and must not be re-measured against another body.
    /// </param>
    public static byte[] BakeCapBind(byte[] capMdl, IReadOnlyList<byte[]> referenceBodies,
                                     Action<string>? diag = null, byte[]? offsetsFrom = null)
    {
        Dictionary<int, float[]>? keepOff = null;
        if (offsetsFrom != null) keepOff = ReadBindOffsets(offsetsFrom);

        var cap = Parse(capMdl);
        var tris = BindSurface(referenceBodies);
        if (tris.Count == 0) throw new InvalidOperationException("reference body has no skin geometry");


        var meshes = new List<int>();
        int meshEnd = cap.Lod0MeshIndex + cap.Lod0MeshCount;
        for (int m = cap.Lod0MeshIndex; m < meshEnd && m < cap.MeshCount; m++)
            if (BitConverter.ToUInt16(cap.S, cap.MeshStart + m * 36) != 0) meshes.Add(m);

        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        w.Write(meshes.Count);

        // Which part of the body the cap belongs to, recorded as the bones its landings are skinned to:
        // every body model carries its own [0,1] atlas, so a coordinate is meaningless without its part.
        var parts = new HashSet<string>(StringComparer.Ordinal);

        float worstOff = 0f, worstRes = 0f;
        int total = 0;
        foreach (int m in meshes)
        {
            ReadCapVertices(cap, m, out var cp, out _);
            w.Write(m);
            w.Write(cp.Length);
            var authored = keepOff != null && keepOff.TryGetValue(m, out var ao) ? ao : null;
            for (int vi = 0; vi < cp.Length; vi++)
            {
                var p = cp[vi];
                float bestD = float.MaxValue;
                (float U, float V) uv = default;
                float off = 0f;
                (string Bone, float W)[] landedOn = [];
                // Which way the skin faced where this vertex landed: an atlas coordinate can be covered by more
                // than one triangle, facing opposite ways.
                Vec3 face = default;
                foreach (var t in tris)
                {
                    float cx = t.Ctr.X - p.X, cy = t.Ctr.Y - p.Y, cz = t.Ctr.Z - p.Z;
                    if (cx * cx + cy * cy + cz * cz > bestD + 0.01f) continue;
                    var q = ClosestOnTriangle(p, t.A, t.B, t.C);
                    float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d >= bestD) continue;
                    bestD = d;
                    var (ba, bb, bc) = Barycentric(q, t.A, t.B, t.C);
                    // Stored in the triangle's OWN cell, so it means the same thing on a body that packs
                    // its atlas somewhere else. See TileOf.
                    var tile = TileOf(t);
                    uv = (t.Ua.U * ba + t.Ub.U * bb + t.Uc.U * bc - tile.U,
                          t.Ua.V * ba + t.Ub.V * bb + t.Uc.V * bc - tile.V);
                    var n = NormalizeOr(new Vec3(t.Na.X * ba + t.Nb.X * bb + t.Nc.X * bc,
                                                 t.Na.Y * ba + t.Nb.Y * bb + t.Nc.Y * bc,
                                                 t.Na.Z * ba + t.Nb.Z * bb + t.Nc.Z * bc), default);
                    // SIGNED along the normal, so a vertex tucked under the surface comes back under it.
                    off = dx * n.X + dy * n.Y + dz * n.Z;
                    landedOn = t.Wa;
                    face = n;
                }
                foreach (var (bone, _) in landedOn) parts.Add(bone);
                // The height the cap was MODELLED at, where one is on offer — see offsetsFrom.
                if (authored != null && vi < authored.Length) off = authored[vi];

                int side = p.X >= 0f ? 1 : -1;

                // The difference between what the placement will rebuild and what was authored, stored in the
                // surface's own tangent frame: inverting the UV parameterisation is not the operation that
                // produced the coordinate, and the offset multiplies the disagreement.
                float rt = 0f, rb = 0f, rn = 0f;
                if (ResolveBindLanding(tris, uv.U, uv.V, side, face,
                                       out var at, out var n2, out _, out var tan, out var bit)
                    < float.MaxValue)
                {
                    float ex = p.X - (at.X + n2.X * off), ey = p.Y - (at.Y + n2.Y * off),
                          ez = p.Z - (at.Z + n2.Z * off);
                    rt = ex * tan.X + ey * tan.Y + ez * tan.Z;
                    rb = ex * bit.X + ey * bit.Y + ez * bit.Z;
                    rn = ex * n2.X + ey * n2.Y + ez * n2.Z;
                    worstRes = MathF.Max(worstRes, MathF.Sqrt(ex * ex + ey * ey + ez * ez));
                }

                w.Write(uv.U); w.Write(uv.V); w.Write(off); w.Write(side);
                w.Write(face.X); w.Write(face.Y); w.Write(face.Z);
                w.Write(rt); w.Write(rb); w.Write(rn);
                worstOff = MathF.Max(worstOff, MathF.Abs(off));
                total++;
            }
        }

        var ms = new MemoryStream();
        var head = new BinaryWriter(ms);
        head.Write(CapBindMagic);
        head.Write(2);   // version — 2 adds the tangent-frame residual, see the note at the write site
        head.Write(parts.Count);
        foreach (var b in parts.OrderBy(x => x, StringComparer.Ordinal)) head.Write(b);
        ms.Write(body.GetBuffer(), 0, (int)body.Length);

        diag?.Invoke($"cap bind: {total} vertices over {meshes.Count} mesh(es), furthest off the skin "
                   + $"{worstOff:F5}, worst residual corrected {worstRes:F5}, anchored to {parts.Count} bone(s): "
                   + string.Join(", ", parts.OrderBy(x => x, StringComparer.Ordinal)));
        return ms.ToArray();
    }

    /// <summary>Where one cap mesh's vertices land on the body currently equipped.</summary>
    internal sealed class CapPlacement
    {
        public required int Mesh;
        public required Vec3[] Pos;
        public required Vec3[] Nrm;
        public required (float U, float V)[] Uv;
        public required (string Bone, float W)[][] W;
        /// <summary>Vertices whose atlas coordinate is not covered by this body; they keep their authored place.</summary>
        public required int Missed;

        /// <summary>How many vertices were actually resolved — all of them, or the sample when scoring.</summary>
        public required int Considered;
    }

    /// <summary>
    /// Fit the authored cap to the body actually equipped, by looking each baked atlas coordinate back up
    /// on it and stepping off along the normal there; this is what makes one authored cap work on a foot
    /// it was never modelled against.
    /// </summary>
    /// <param name="stride">Resolve only every n-th vertex, for scoring a binding against a body.</param>
    /// <summary>
    /// How closely a placed cap reproduces the shape it was authored as: the mean distance from each
    /// placed vertex to the same vertex of the cap's own model. Near zero only on the body the binding
    /// was baked against, so it is an identity test, not a quality one. float.MaxValue when the cap
    /// cannot be checked.
    /// </summary>
    private static float CapRoundTrip(IReadOnlyList<CapPlacement> placed, byte[] capMdl)
    {
        try
        {
            var src = ParseCached(capMdl);
            double sum = 0; int n = 0;
            foreach (var pl in placed)
            {
                ReadCapVertices(src, pl.Mesh, out var authored, out _);
                int c = Math.Min(authored.Length, pl.Pos.Length);
                for (int i = 0; i < c; i++)
                {
                    if (pl.Pos[i] is { X: 0, Y: 0, Z: 0 }) continue;   // never placed; says nothing
                    sum += Dist(pl.Pos[i], authored[i]);
                    n++;
                }
            }
            return n == 0 ? float.MaxValue : (float)(sum / n);
        }
        catch
        {
            return float.MaxValue;
        }
    }

    /// <summary>
    /// How unevenly a placed cap sits off the body's skin, as the spread of its standoffs between the
    /// tenth and ninetieth percentile; percentiles so a few odd rim vertices do not decide it.
    /// </summary>
    private static float CapStandoffSpread(IReadOnlyList<CapPlacement> placed, List<SkinTri> skin,
                                           out float median)
    {
        median = 0f;
        if (skin.Count == 0) return float.MaxValue;
        var d = new List<float>();
        foreach (var pl in placed)
            foreach (var q in pl.Pos)
            {
                if (q is { X: 0, Y: 0, Z: 0 }) continue;      // never placed
                if (!NearestOnSkin(q, skin, CapStandoffReach, out var at)) continue;
                d.Add(Dist(q, at));
            }
        if (d.Count < 8) return float.MaxValue;
        d.Sort();
        median = d[d.Count / 2];
        return d[(int)(d.Count * 0.90f)] - d[(int)(d.Count * 0.10f)];
    }

    /// <summary>
    /// Which body a cap is for, from its file name (<c>toecap.&lt;body&gt;.mdl</c>), for saying out loud.
    /// A name with no body in it is reported as it stands.
    /// </summary>
    private static string CapBodyName(string fileStem)
    {
        int dot = fileStem.LastIndexOf('.');
        if (dot < 0 || dot == fileStem.Length - 1) return fileStem;
        var body = fileStem[(dot + 1)..];
        return char.ToUpperInvariant(body[0]) + body[1..];
    }

    /// <summary>
    /// The bone names a binding was baked against, read from its header alone: a body fingerprint,
    /// since bodies weight their toes to different bones.
    /// </summary>
    private static HashSet<string> ReadBindBones(byte[] bind)
    {
        var parts = new HashSet<string>(StringComparer.Ordinal);
        if (bind.Length < 12 || BitConverter.ToUInt32(bind, 0) != CapBindMagic) return parts;
        try
        {
            var r = new BinaryReader(new MemoryStream(bind));
            r.ReadUInt32();
            if (r.ReadInt32() is not (1 or 2)) return parts;
            int n = r.ReadInt32();
            if (n is < 0 or > 4096) return parts;
            for (int i = 0; i < n; i++) parts.Add(r.ReadString());
        }
        catch { parts.Clear(); }
        return parts;
    }

    internal static List<CapPlacement>? TryPlaceCapFromBind(byte[] bind, IReadOnlyList<byte[]> bodies,
                                                            Action<string>? diag = null,
                                                            byte[]? capMdl = null, int stride = 1)
    {
        return new CapPlacementFromBind(bind, bodies, diag, capMdl, stride).Run();
    }

    /// <summary>
    /// Which cell of the atlas a triangle lives in. A body's UVs are not obliged to sit in [0,1], so a
    /// coordinate from one body and a triangle from another must be brought to the same cell first.
    /// </summary>
    private static (float U, float V) TileOf(SkinTri t)
        => (MathF.Floor(MathF.Min(t.Ua.U, MathF.Min(t.Ub.U, t.Uc.U))),
            MathF.Floor(MathF.Min(t.Ua.V, MathF.Min(t.Ub.V, t.Uc.V))));

    /// <summary>How far outside a triangle, in barycentric terms, a baked coordinate may land before it counts as unplaced; the atlas has gaps between islands.</summary>
    private const float CapBindMissTolerance = 0.01f;

    /// <summary>Rings a vertex with no atlas coordinate may be filled from; a bound stops a cap that failed to place from being smeared into one point.</summary>
    private const int CapBindFillPasses = 6;

    /// <summary>
    /// Share of a cap's vertices that may fail to place before the binding is judged not to describe this
    /// body and the cap is declined; otherwise the placed vertices move, the rest stay, and the triangles
    /// between them stretch into shards.
    /// </summary>
    /// <summary>How close two caps' bone coverage must be to count as equal; anything wider is a different body, not a worse fit.</summary>
    private const float CapBoneCoverTie = 0.02f;

    /// <summary>
    /// How close two caps' placement rates must be before the tie falls through to how evenly each sits
    /// off the skin. Deliberately tight: the placement rate identifies a body's own cap where the fit
    /// score cannot.
    /// </summary>
    private const float CapPlaceRateTie = 0.02f;

    /// <summary>How much closer one cap's round trip must be than another's to decide between them, as a ratio.</summary>
    private const float CapRoundTripTie = 0.5f;

    /// <summary>How far a placed cap vertex may look for the skin when measuring its standoff; past this it did not land on the foot.</summary>
    private const float CapStandoffReach = 0.030f;

    private const float CapBindMaxUnplaced = 0.15f;

    /// <summary>Vertices skipped between samples when scoring a binding against a body; only the rough hit rate is needed.</summary>
    private const int CapBindProbeStride = 8;

    /// <summary>
    /// Coverage texels the cap's own trim is widened by, so it reaches at least as far as the shell's;
    /// the shared visibility test dilates by the asking triangle's size, and the cap's are far smaller.
    /// </summary>
    private const int CapCoverDilate = 2;

    /// <summary>
    /// UVs for an authored mesh that has none, taken from the body it is grafted onto: drop each vertex
    /// onto the nearest skin triangle and interpolate that triangle's coordinate where it lands. The
    /// overlays are painted in the body's layout, so a fresh unwrap would sample empty texture.
    /// </summary>
    private static CapUvPlan? ProjectCapUV(Source cap, int mesh, IReadOnlyList<byte[]> bodies,
                                           Action<string>? diag, CapPlacement? placed = null)
    {
        return new CapUvProjection(cap, mesh, bodies, diag, placed).Run();
    }

    /// <summary>
    /// How the authored cap's vertices are laid out once the body's UV seams have been cut into it:
    /// one entry per output vertex, and where every triangle corner points.
    /// </summary>
    private sealed class CapUvPlan
    {
        /// <summary>Projected coordinate per OUTPUT vertex.</summary>
        public required (float U, float V)[] Uv;

        /// <summary>
        /// Skinning per output vertex, taken from the body underneath at the same landing as the UV, by
        /// bone name so it can be remapped into the emitted mesh's table.
        /// </summary>
        public required (string Bone, float W)[][] Weights;

        /// <summary>The cap vertex each output vertex is a copy of — everything but the UV comes from it.</summary>
        public required int[] SourceOf;

        /// <summary>
        /// Output vertex per triangle corner, in the cap's own submesh-then-triangle order. The emitter
        /// walks its index buffer in that same order and substitutes these.
        /// </summary>
        public required int[] Corner;

        /// <summary>
        /// The cap's triangles by pre-split index, and each such vertex's position and normal; each layer
        /// works out its own trimmed rim from these, since the coverage map differs per layer.
        /// </summary>
        public required int[] Tri;

        /// <inheritdoc cref="Tri"/>
        public required Vec3[] SrcPos;

        /// <inheritdoc cref="Tri"/>
        public required Vec3[] SrcNrm;

        /// <inheritdoc cref="Tri"/>
        public required (string Bone, float W)[][] SrcW;

        /// <summary>Rings from the cap's open boundary, per pre-split vertex; 0 on the boundary, int.Max where unreachable.</summary>
        public required int[] RimRing;
    }

    /// <summary>
    /// Component label per vertex over an adjacency list, optionally restricted to a subset. Vertices
    /// outside the subset keep label -1.
    /// </summary>
    private static int[] ConnectedComponents(HashSet<int>?[] adj, int vc, IReadOnlyList<int>? subset = null)
    {
        var label = new int[vc];
        Array.Fill(label, -1);
        var seeds = subset ?? Enumerable.Range(0, vc).ToList();
        var stack = new Stack<int>();
        int next = 0;
        foreach (int seed in seeds)
        {
            if (label[seed] >= 0) continue;
            int id = next++;
            stack.Push(seed);
            label[seed] = id;
            while (stack.Count > 0)
            {
                int v = stack.Pop();
                if (adj[v] is not { } near) continue;
                foreach (int j in near)
                    if (label[j] < 0) { label[j] = id; stack.Push(j); }
            }
        }
        return label;
    }

    /// <summary>LOD0 triangle indices of one mesh of a parsed source, flattened.</summary>
    private static List<int> CapTriangles(Source src, int mesh, ushort vc)
    {
        var s = src.S;
        int mo = src.MeshStart + mesh * 36;
        ushort si = BitConverter.ToUInt16(s, mo + 10), sc = BitConverter.ToUInt16(s, mo + 12);
        var outp = new List<int>();
        for (int su = 0; su < sc; su++)
        {
            int ss = src.SubmeshStart + (si + su) * 16;
            uint so = BitConverter.ToUInt32(s, ss), cnt = BitConverter.ToUInt32(s, ss + 4);
            for (uint t = 0; t + 2 < cnt; t += 3)
            {
                int q = src.Ib + (int)(so + t) * 2;
                int a = BitConverter.ToUInt16(s, q), b = BitConverter.ToUInt16(s, q + 2), c = BitConverter.ToUInt16(s, q + 4);
                // Clamped, never skipped: the emitter walks the same triangles in the same order and
                // reads the result positionally, so dropping one here would shift every corner after it.
                outp.Add(Math.Min(a, vc - 1)); outp.Add(Math.Min(b, vc - 1)); outp.Add(Math.Min(c, vc - 1));
            }
        }
        return outp;
    }
}
