using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    /// <summary>
    /// Grow a mask by <paramref name="steps"/> texels, 8-connected. <paramref name="onAt"/> must match the
    /// threshold the consumer reads the mask at, or the growth lands on texels the consumer already accepted.
    /// </summary>
    private static byte[] DilateMask(byte[] src, int w, int h, int steps, byte onAt = 128)
    {
        var cur = (byte[])src.Clone();
        for (int s = 0; s < steps; s++)
        {
            var next = (byte[])cur.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (cur[y * w + x] >= onAt) continue;
                    bool near = false;
                    for (int dy = -1; dy <= 1 && !near; dy++)
                        for (int dx = -1; dx <= 1 && !near; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h && cur[ny * w + nx] >= onAt)
                                near = true;
                        }
                    if (near) next[y * w + x] = 255;
                }
            cur = next;
        }
        return cur;
    }

    /// <summary>Set by a diagnostic to receive each cut mask as it is built: name, texels, side.</summary>
    internal static Action<string, byte[], int>? MaskDump;

    /// <summary>
    /// Bone tables and submesh bone windows, as the game reads them; a modelling package ignores the submesh
    /// bone map, so a wrong window imports perfectly and deforms as garbage in game.
    /// </summary>
    /// <summary>One mesh's per-vertex skinning, resolved to bone names, with the position it sits at.</summary>
    private static (Vec3 P, (string Bone, float W)[] W)[] ReadMeshSkinning(Source src, int m)
    {
        int mo = src.MeshStart + m * 36;
        ushort vc = BitConverter.ToUInt16(src.S, mo);
        ushort tbl = BitConverter.ToUInt16(src.S, mo + 14);
        var decl = m < src.Decls.Length ? src.Decls[m] : [];
        VElem? pEl = null, wEl = null, iEl = null;
        foreach (var el in decl)
        {
            if (el.Usage == UsePosition) pEl ??= el;
            if (el.Usage == UseBlendWeight) wEl ??= el;
            if (el.Usage == UseBlendIndices) iEl ??= el;
        }
        if (vc == 0 || pEl is not { } pe || wEl is not { } we || iEl is not { } ie
            || tbl >= src.BoneTables.Length)
            return [];

        var table = src.BoneTables[tbl];
        int nInf = BlendCount(we.Type);
        uint[] vOff = { BitConverter.ToUInt32(src.S, mo + 20), BitConverter.ToUInt32(src.S, mo + 24),
                        BitConverter.ToUInt32(src.S, mo + 28) };
        byte[] strides = { src.S[mo + 32], src.S[mo + 33], src.S[mo + 34] };

        var outp = new (Vec3, (string, float)[])[vc];
        Span<float> tmp = stackalloc float[4];
        for (int v = 0; v < vc; v++)
        {
            int pa = (int)(src.Vb + vOff[pe.Stream]) + v * strides[pe.Stream] + pe.Offset;
            ReadTyped(src.S, pa, pe.Type, tmp);
            var p = new Vec3(tmp[0], tmp[1], tmp[2]);

            int wa = (int)(src.Vb + vOff[we.Stream]) + v * strides[we.Stream] + we.Offset;
            int ia = (int)(src.Vb + vOff[ie.Stream]) + v * strides[ie.Stream] + ie.Offset;
            var acc = new Dictionary<string, float>(StringComparer.Ordinal);
            if (wa + nInf <= src.S.Length && ia + nInf <= src.S.Length)
                for (int k = 0; k < nInf; k++)
                {
                    float f = src.S[wa + k] / 255f;
                    if (f <= 0f) continue;
                    int local = src.S[ia + k];
                    string nm = local < table.Length && table[local] < src.BoneNames.Length
                        ? src.BoneNames[table[local]] : $"?{local}";
                    acc[nm] = acc.GetValueOrDefault(nm) + f;
                }
            outp[v] = (p, acc.OrderByDescending(k => k.Value).Select(k => (k.Key, k.Value)).ToArray());
        }
        return outp;
    }

    /// <summary>
    /// A cut mask covering exactly where the authored cap actually is, rasterised from its placed UVs and
    /// substituted for the painted ToeCap map whenever a cap is grafted: the painted map says where a cap is
    /// wanted, not where the modelled one reaches on this body, so the hole always matches the thing filling it.
    /// </summary>
    /// <summary>
    /// How far behind the reinforced toe's line the density fades out, in metres: 3 mm. The line itself sits
    /// at the cap's rim; see <see cref="ToeLine.Weight"/>.
    /// </summary>
    private const float ToeReinforceBand = 0.003f;

    /// <summary>One body skin triangle the reinforced-toe line is drawn over: its corners and their UVs.</summary>
    private readonly record struct ToeLineTri(Vec3 A, Vec3 B, Vec3 C,
                                              (float U, float V) Ua, (float U, float V) Ub, (float U, float V) Uc,
                                              Vec3 Ctr);

    /// <summary>
    /// Every source body's LOD0 skin triangles, with UVs: the same geometry the cap's UVs were projected
    /// from, so the line lands in the same UV space. Read once per build, only when some layer has a
    /// reinforced toe.
    /// </summary>
    private static List<ToeLineTri> ToeLineBody(IReadOnlyList<byte[]> bodies)
    {
        var list = new List<ToeLineTri>();
        foreach (var body in bodies)
        {
            if (!TryReadLod0Geometry(body, out var bp, out var bu, out var bt, out _)) continue;
            for (int t = 0; t + 2 < bt.Length; t += 3)
            {
                int a = bt[t], b = bt[t + 1], c = bt[t + 2];
                if ((a + 1) * 3 > bp.Length || (b + 1) * 3 > bp.Length || (c + 1) * 3 > bp.Length) continue;
                if ((a + 1) * 2 > bu.Length || (b + 1) * 2 > bu.Length || (c + 1) * 2 > bu.Length) continue;
                var pa = new Vec3(bp[a * 3], bp[a * 3 + 1], bp[a * 3 + 2]);
                var pb = new Vec3(bp[b * 3], bp[b * 3 + 1], bp[b * 3 + 2]);
                var pc = new Vec3(bp[c * 3], bp[c * 3 + 1], bp[c * 3 + 2]);
                list.Add(new ToeLineTri(pa, pb, pc,
                    (bu[a * 2], bu[a * 2 + 1]), (bu[b * 2], bu[b * 2 + 1]), (bu[c * 2], bu[c * 2 + 1]),
                    new Vec3((pa.X + pb.X + pc.X) / 3f, (pa.Y + pb.Y + pc.Y) / 3f, (pa.Z + pb.Z + pc.Z) / 3f)));
            }
        }
        return list;
    }

    /// <summary>
    /// Draw one placed cap's reinforced-toe region into <paramref name="map"/>: a plane through the cap's
    /// outer rim, everything on the toe side of it at full weight, fading over <see cref="ToeReinforceBand"/>
    /// behind it. Drawn over both the cap's own triangles and the body skin behind it, the latter limited to
    /// the cap's neighbourhood since a plane is infinite.
    /// </summary>
    private static void DrawToeLine(CapUvPlan pl, List<ToeLineTri> body, byte[] map, int size, Action<string>? diag)
    {
        int vc = pl.SrcPos.Length;
        if (vc == 0) return;

        // One line per foot: the cap can arrive as a single mesh covering both, so each connected piece gets
        // its own rim, plane and neighbourhood.
        var (label, pieces) = ToeLine.Components(pl.Tri, vc);
        var planes = new List<(Vec3 Origin, Vec3 Normal, Vec3 Centre, float Reach)?>(pieces);

        for (int k = 0; k < pieces; k++)
        {
            var pieceTri = new List<int>();
            for (int t = 0; t + 2 < pl.Tri.Length; t += 3)
                if (pl.Tri[t] < vc && label[pl.Tri[t]] == k)
                    pieceTri.AddRange([pl.Tri[t], pl.Tri[t + 1], pl.Tri[t + 2]]);

            double sx = 0, sy = 0, sz = 0;
            int n = 0;
            for (int i = 0; i < vc; i++)
            {
                if (label[i] != k) continue;
                sx += pl.SrcPos[i].X; sy += pl.SrcPos[i].Y; sz += pl.SrcPos[i].Z; n++;
            }
            // A stray fragment is not a foot, and a handful of vertices cannot carry a meaningful rim.
            if (n < ToeLineMinPieceVertices) { planes.Add(null); continue; }
            var centre = new Vec3((float)(sx / n), (float)(sy / n), (float)(sz / n));
            float radius = 0f;
            for (int i = 0; i < vc; i++)
                if (label[i] == k) radius = MathF.Max(radius, Dist(pl.SrcPos[i], centre));

            var rim = ToeLine.OuterRim(pieceTri, pl.SrcPos);
            var rimPts = new List<Vec3>(rim.Count);
            foreach (int i in rim) if (i < vc) rimPts.Add(pl.SrcPos[i]);
            if (!ToeLine.FitPlane(rimPts, centre, out var origin, out var normal))
            {
                diag?.Invoke($"reinforced toe: piece {k}'s rim ({rimPts.Count} vertices) does not define a plane — "
                           + "no line drawn for it");
                planes.Add(null);
                continue;
            }
            planes.Add((origin, normal, centre, radius * 1.25f + ToeReinforceBand));
            diag?.Invoke($"reinforced toe: piece {k} — line through {rimPts.Count} rim vertices, normal "
                       + $"({normal.X:F2}, {normal.Y:F2}, {normal.Z:F2}), reach {radius * 1.25f:F3}");
        }

        // The cap, each triangle against its own piece's plane: on the toe side, so at full weight.
        for (int f = 0; f + 2 < pl.Corner.Length; f += 3)
        {
            int c0 = pl.Corner[f], c1 = pl.Corner[f + 1], c2 = pl.Corner[f + 2];
            if (c0 >= pl.Uv.Length || c1 >= pl.Uv.Length || c2 >= pl.Uv.Length) continue;
            int s0 = pl.SourceOf[c0], s1 = pl.SourceOf[c1], s2 = pl.SourceOf[c2];
            if (s0 >= vc || s1 >= vc || s2 >= vc || label[s0] < 0) continue;
            if (planes[label[s0]] is not { } pp) continue;
            ToeLine.Rasterize(map, size, pl.Uv[c0], pl.Uv[c1], pl.Uv[c2],
                ToeLine.Distance(pl.SrcPos[s0], pp.Origin, pp.Normal),
                ToeLine.Distance(pl.SrcPos[s1], pp.Origin, pp.Normal),
                ToeLine.Distance(pl.SrcPos[s2], pp.Origin, pp.Normal), ToeReinforceBand);
        }

        // The foot behind each piece: a body triangle belongs to the NEAREST piece that reaches it, so a
        // triangle can never be judged against the other foot's plane.
        int drawn = 0;
        foreach (var t in body)
        {
            int best = -1;
            float bestDist = float.MaxValue;
            for (int k = 0; k < planes.Count; k++)
            {
                if (planes[k] is not { } pp) continue;
                float d = Dist(t.Ctr, pp.Centre);
                if (d <= pp.Reach && d < bestDist) { best = k; bestDist = d; }
            }
            if (best < 0) continue;
            var bp = planes[best]!.Value;
            ToeLine.Rasterize(map, size, t.Ua, t.Ub, t.Uc,
                ToeLine.Distance(t.A, bp.Origin, bp.Normal),
                ToeLine.Distance(t.B, bp.Origin, bp.Normal),
                ToeLine.Distance(t.C, bp.Origin, bp.Normal), ToeReinforceBand);
            drawn++;
        }
        diag?.Invoke($"reinforced toe: {planes.Count(p => p != null)} of {pieces} cap piece(s) drawn, "
                   + $"{drawn} body triangles");
    }

    /// <summary>The fewest vertices a connected piece of the cap needs to be treated as a foot's cap.</summary>
    private const int ToeLineMinPieceVertices = 20;

    private static void CapFootprintMask(CapUvPlan plan, SecondSkinLayer? cov, byte[] mask, int size)
    {
        var uv = plan.Uv;
        int faces = plan.Corner.Length / 3;
        for (int f = 0; f < faces; f++)
        {
            int a = plan.Corner[f * 3], b = plan.Corner[f * 3 + 1], c = plan.Corner[f * 3 + 2];
            if (a >= uv.Length || b >= uv.Length || c >= uv.Length) continue;
            // Only the part of the cap this layer will actually emit — the footprint has to describe the
            // hole the cap fills, and a coverage-trimmed cap fills less of one.
            if (cov?.Coverage != null && !AnyVisible(cov, uv[a], uv[b], uv[c])) continue;

            {
                float ax = uv[a].U * size, ay = uv[a].V * size;
                float bx = uv[b].U * size, by = uv[b].V * size;
                float cx = uv[c].U * size, cy = uv[c].V * size;
                // Tiled, not clamped: a body's UVs need not live in the [0,1] tile, and the mask's consumer samples
                // with wrap. The triangle stays in unwrapped space so its barycentric test is unaffected; only the
                // write index wraps.
                int x0 = (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))) - 1;
                int x1 = (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))) + 1;
                int y0 = (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))) - 1;
                int y1 = (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))) + 1;
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1 << 18) continue;   // a seam-straddling triangle
                int Wrap(int v) => (v % size + size) % size;

                // Conservative for anything that does not comfortably contain a texel centre: the cap's typical face
                // is smaller than a texel, and point-sampling leaves a dotted mask. Overstating a sub-texel face costs
                // at most a texel, which the weld closes.
                float wide = MathF.Max(ax, MathF.Max(bx, cx)) - MathF.Min(ax, MathF.Min(bx, cx));
                float tall = MathF.Max(ay, MathF.Max(by, cy)) - MathF.Min(ay, MathF.Min(by, cy));
                if (wide <= 1.5f && tall <= 1.5f)
                {
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                            mask[Wrap(y) * size + Wrap(x)] = 255;
                    continue;
                }

                float den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (MathF.Abs(den) < 1e-12f) continue;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float w0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den;
                        float w1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den;
                        float w2 = 1f - w0 - w1;
                        if (w0 < -0.02f || w1 < -0.02f || w2 < -0.02f) continue;
                        mask[Wrap(y) * size + Wrap(x)] = 255;
                    }
            }
        }

        // Fill what the cap encloses, not merely what it covers: the cap spans over the gaps between the toes,
        // but the shell geometry it replaces reaches into them. Anything the background cannot reach from the
        // edge of the atlas is inside the cap's outline.
        var outside = new bool[size * size];
        var queue = new Queue<int>();
        void Seed(int i) { if (mask[i] == 0 && !outside[i]) { outside[i] = true; queue.Enqueue(i); } }
        for (int x = 0; x < size; x++) { Seed(x); Seed((size - 1) * size + x); }
        for (int y = 0; y < size; y++) { Seed(y * size); Seed(y * size + size - 1); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % size, y = i / size;
            if (x > 0) Seed(i - 1);
            if (x < size - 1) Seed(i + 1);
            if (y > 0) Seed(i - size);
            if (y < size - 1) Seed(i + size);
        }
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] == 0 && !outside[i]) mask[i] = 255;
    }

    /// <summary>
    /// Faintest coverage that still counts as painted, out of 255; a resampled or compressed coverage map is
    /// full of texels at 1/255, which is invisible but would keep a whole shell.
    /// </summary>
    internal const byte CoverageFloor = 8;

    /// <summary>
    /// Does any texel under this triangle's UV footprint carry coverage? Scans the full texel bounding
    /// box (padded one texel for bilinear bleed) rather than the exact triangle: over-keeping a sliver
    /// is free, wrongly culling one leaves a visible sawtooth.
    /// </summary>
    private static bool AnyVisible(SecondSkinLayer def, (float U, float V) a, (float U, float V) b, (float U, float V) c)
    {
        var mask = def.Coverage;
        if (mask == null) return true;
        int w = def.CoverageWidth, h = def.CoverageHeight;

        float u0 = MathF.Min(a.U, MathF.Min(b.U, c.U)), u1 = MathF.Max(a.U, MathF.Max(b.U, c.U));
        float v0 = MathF.Min(a.V, MathF.Min(b.V, c.V)), v1 = MathF.Max(a.V, MathF.Max(b.V, c.V));
        int x0 = (int)MathF.Floor(u0 * w) - 1, x1 = (int)MathF.Ceiling(u1 * w) + 1;
        int y0 = (int)MathF.Floor(v0 * h) - 1, y1 = (int)MathF.Ceiling(v1 * h) + 1;

        // A triangle straddling a UV seam has a huge box; keep it rather than scan the whole texture.
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1 << 16) return true;

        for (int y = y0; y <= y1; y++)
        {
            int wy = ((y % h) + h) % h;
            for (int x = x0; x <= x1; x++)
            {
                int wx = ((x % w) + w) % w;
                if (mask[wy * w + wx] >= CoverageFloor) return true;
            }
        }
        return false;
    }
}
