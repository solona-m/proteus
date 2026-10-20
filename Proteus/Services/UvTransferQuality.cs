using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Region = Proteus.Services.UvBakeBones.Region;

namespace Proteus.Services;

/// <summary>
/// How good a baked transfer map is, measured on the two bodies it joins — and the gate a map must pass before it is
/// shipped. Thresholds are fixed here and never loosened to let a map through: a miss is reported, not tuned away.
/// <para/>
/// Bodies are read in the frame they are baked in: +Y up, +Z the front, +X the character's left; the man already bent
/// to the woman's proportions.
/// </summary>
internal static class UvTransferQuality
{
    internal static class Gate
    {
        public const double RegionCoverage = 0.99, RegionRimSnaps = 0.001, RegionFolds = 0.01;
        public const double GlobalFolds = 0.005, GlobalCoverage = 0.99;
        public const double RoundTripGross = 0.005, RoundTripModerate = 0.10;
        public const float TorsoLandmark = 0.015f, TipLandmark = 0.01f;
        public const double IdentityP50 = 4, IdentityP95 = 16;
    }

    /// <summary>The named places on the torso and legs the gate checks, by name.</summary>
    internal static readonly string[] NamedRegions = ["sternum", "underBust", "lowerBelly", "innerThigh"];

    /// <summary>Where a body's frame is: from its own bone centroids, so each body is measured by its own shape.</summary>
    internal sealed class Frame
    {
        public float PelvisY, NeckY, PelvisZ, NeckZ;
        public float Span => NeckY - PelvisY;

        public static Frame Of(BakeBody body)
        {
            Vector3 Centroid(Func<string, bool> bone)
            {
                var sum = Vector3.Zero;
                int n = 0;
                for (int v = 0; v < body.P.Length; v++)
                {
                    string? top = null;
                    float best = 0f;
                    foreach (var (b, w) in body.Weights[v]) if (w > best) { best = w; top = b; }
                    if (top != null && bone(top)) { sum += body.P[v]; n++; }
                }
                return n == 0 ? Vector3.Zero : sum / n;
            }
            var pelvis = Centroid(b => b.StartsWith("j_kosi", StringComparison.Ordinal));
            var neck = Centroid(b => b.StartsWith("j_kubi", StringComparison.Ordinal) || b.StartsWith("j_sebo_c", StringComparison.Ordinal));
            return new Frame { PelvisY = pelvis.Y, NeckY = neck.Y, PelvisZ = pelvis.Z, NeckZ = neck.Z };
        }

        /// <summary>The named region a surface point is in, or null.</summary>
        public string? NamedRegion(Vector3 p, Vector3 n)
        {
            float h = Span <= 1e-4f ? 0 : (p.Y - PelvisY) / Span;
            float ax = MathF.Abs(p.X);
            if (ax < 0.03f && n.Z > 0.5f && h is >= 0.62f and <= 0.85f) return "sternum";
            if (ax is >= 0.03f and <= 0.12f && n.Z > 0.3f && h is >= 0.45f and <= 0.6f) return "underBust";
            if (ax < 0.08f && n.Z > 0.5f && h is >= 0.0f and <= 0.2f) return "lowerBelly";
            if (ax < 0.1f && p.X * n.X < -0.5f * ax && h is >= -0.55f and <= -0.15f) return "innerThigh";
            return null;
        }
    }

    internal sealed class RegionFigures
    {
        public long Owned, Valid, RimSnaps, Folds, Measured;
        public double Coverage => Owned == 0 ? 0 : (double)Valid / Owned;
        public double RimSnapShare => Valid == 0 ? 0 : (double)RimSnaps / Valid;
        public double FoldShare => Measured == 0 ? 0 : (double)Folds / Measured;
    }

    internal sealed class Figures
    {
        public double DestCoverage, FoldShare, RoundTripModerate = double.NaN, RoundTripGross = double.NaN;
        public long Folds, FoldMeasured;
        public Dictionary<string, RegionFigures> Regions = [];
        public Dictionary<string, float> LandmarksMm = [];
        public List<string> Failures = [];
    }

    /// <summary>The destination's triangles grouped into uv islands: triangles sharing a vertex.</summary>
    private static int[] Islands(BakeBody body)
    {
        var parent = new int[body.P.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int t = 0; t < body.TriangleCount; t++)
        {
            int a = Find(body.Tri[t * 3]);
            parent[Find(body.Tri[t * 3 + 1])] = a;
            parent[Find(body.Tri[t * 3 + 2])] = a;
        }
        var island = new int[body.TriangleCount];
        for (int t = 0; t < island.Length; t++) island[t] = Find(body.Tri[t * 3]);
        return island;
    }

    /// <summary>
    /// The map's own figures against its destination: coverage, folds (the map turning over against the rest of its
    /// island) and the named regions' share of each, rim snaps among them.
    /// </summary>
    internal static Figures Measure(UvTransferBake.Result map, UvTransferBake.Raster raster, BakeBody dest, BakeBody src,
                                    UvTransferBake.Raster srcRaster)
    {
        int size = map.Size;
        var island = Islands(dest);
        var frame = Frame.Of(dest);
        var f = new Figures { DestCoverage = map.Stats.DestCoverage };
        foreach (var name in NamedRegions) f.Regions[name] = new RegionFigures();

        // The Jacobian's sign per pixel; a jump of more than 1/32 of the sheet between neighbours is a source seam,
        // not a fold, and is not measured.
        const int Seam = 65536 / 32;
        var sign = new sbyte[size * size];
        var islandVotes = new Dictionary<int, long>();
        for (int y = 0; y + 1 < size; y++)
            for (int x = 0; x + 1 < size; x++)
            {
                int i = y * size + x, ix = i + 1, iy = i + size;
                if (!map.Valid[i] || !map.Valid[ix] || !map.Valid[iy]) continue;
                int t = raster.Owner[i];
                if (raster.Owner[ix] < 0 || raster.Owner[iy] < 0 || island[raster.Owner[ix]] != island[t]
                    || island[raster.Owner[iy]] != island[t]) continue;
                int dux = map.R[ix] - map.R[i], dvx = map.G[ix] - map.G[i];
                int duy = map.R[iy] - map.R[i], dvy = map.G[iy] - map.G[i];
                if (Math.Abs(dux) > Seam || Math.Abs(dvx) > Seam || Math.Abs(duy) > Seam || Math.Abs(dvy) > Seam) continue;
                long det = (long)dux * dvy - (long)dvx * duy;
                if (det == 0) continue;
                sign[i] = det > 0 ? (sbyte)1 : (sbyte)-1;
                islandVotes[island[t]] = islandVotes.GetValueOrDefault(island[t]) + sign[i];
            }

        for (int i = 0; i < sign.Length; i++)
        {
            int t = raster.Owner[i];
            if (t < 0) continue;
            float u = raster.BaryU[i], v = raster.BaryV[i];
            var (pos, nrm, _) = dest.At(t, u, v, 1f - u - v);
            var name = frame.NamedRegion(pos, nrm);
            var region = name == null ? null : f.Regions[name];
            if (region != null)
            {
                region.Owned++;
                if (map.Valid[i])
                {
                    region.Valid++;
                    if (SourcePoint(map, i, src, srcRaster) is { } hit
                        && src.Rim.Near(hit, UvTransferBake.RimReach) && !dest.Rim.Near(pos, UvTransferBake.RimReach))
                        region.RimSnaps++;
                }
            }
            if (sign[i] == 0) continue;
            f.FoldMeasured++;
            if (region != null) region.Measured++;
            long vote = islandVotes.GetValueOrDefault(island[t]);
            if (vote != 0 && Math.Sign(vote) != sign[i])
            {
                f.Folds++;
                if (region != null) region.Folds++;
            }
        }
        f.FoldShare = f.FoldMeasured == 0 ? 0 : (double)f.Folds / f.FoldMeasured;
        return f;
    }

    /// <summary>
    /// Round-trip error of a pair, with the thresholds <see cref="UVRemapService"/>'s loader uses to drop seam pixels:
    /// moderate past max(16, W/128) px, gross past W/8. Shares of the forward map's valid pixels.
    /// </summary>
    internal static (double Moderate, double Gross) RoundTrip(UvTransferBake.Result fwd, UvTransferBake.Result rev)
    {
        float soft = Math.Max(16f, fwd.Size / 128f), ultra = fwd.Size / 8f;
        long moderate = 0, gross = 0, valid = 0;
        for (int i = 0; i < fwd.Valid.Length; i++)
        {
            if (!fwd.Valid[i]) continue;
            valid++;
            int sx = (int)(fwd.R[i] / 65535f * (rev.Size - 1) + 0.5f);
            int sy = (int)(fwd.G[i] / 65535f * (rev.Size - 1) + 0.5f);
            int q = sy * rev.Size + sx;
            if (!rev.Valid[q]) continue;
            float bx = rev.R[q] / 65535f * (fwd.Size - 1), by = rev.G[q] / 65535f * (fwd.Size - 1);
            float dx = bx - i % fwd.Size, dy = by - i / fwd.Size;
            float e2 = dx * dx + dy * dy;
            if (e2 > ultra * ultra) gross++;
            else if (e2 > soft * soft) moderate++;
        }
        return valid == 0 ? (0, 0) : ((double)moderate / valid, (double)gross / valid);
    }

    /// <summary>
    /// Named points found on a body by its own shape, so the same point can be found on both bodies and compared:
    /// fingertips, big-toe tips, sternum, under-bust, lower belly, inner thighs, nape, shoulder tops.
    /// </summary>
    internal static Dictionary<string, (int Tri, Vector3 Bary)> Landmarks(BakeBody body)
    {
        var frame = Frame.Of(body);
        var result = new Dictionary<string, (int, Vector3)>();
        void Pick(string name, Func<Vector3, Vector3, Region, float?> score)
        {
            float best = float.MinValue;
            int bestT = -1, bestK = 0;
            for (int t = 0; t < body.TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int v = body.Tri[t * 3 + k];
                    if (score(body.P[v], body.N[v], body.VertexRegion[v]) is { } s && (s > best || (s == best && t < bestT)))
                    { best = s; bestT = t; bestK = k; }
                }
            if (bestT >= 0) result[name] = (bestT, bestK == 0 ? Vector3.UnitX : bestK == 1 ? Vector3.UnitY : Vector3.UnitZ);
        }
        float H(Vector3 p) => frame.Span <= 1e-4f ? 0 : (p.Y - frame.PelvisY) / frame.Span;

        foreach (var (side, sx) in new[] { ("L", 1f), ("R", -1f) })
        {
            var hand = sx > 0 ? Region.HandL : Region.HandR;
            var foot = sx > 0 ? Region.FootL : Region.FootR;
            var thigh = sx > 0 ? Region.ThighL : Region.ThighR;
            Pick("fingertip" + side, (p, _, r) => r == hand ? p.X * sx : null);
            Pick("toe" + side, (p, _, r) => r == foot ? p.Z : null);
            Pick("underBust" + side, (p, n, r) => r == Region.Torso && p.X * sx is > 0.06f and < 0.1f
                                                  && H(p) is > 0.5f and < 0.56f ? p.Z : null);
            Pick("innerThigh" + side, (p, _, r) => r == thigh && MathF.Abs(H(p) + 0.35f) < 0.03f ? -p.X * sx : null);
            Pick("shoulder" + side, (p, _, r) => r is Region.Torso or Region.UpperArmL or Region.UpperArmR
                                                 && p.X * sx is > 0.12f and < 0.16f ? p.Y : null);
        }
        Pick("sternum", (p, _, r) => r == Region.Torso && MathF.Abs(p.X) < 0.01f && H(p) is > 0.7f and < 0.76f ? p.Z : null);
        Pick("lowerBelly", (p, _, r) => r is Region.Pelvis or Region.Torso && MathF.Abs(p.X) < 0.01f
                                        && H(p) is > 0.08f and < 0.12f ? p.Z : null);
        Pick("nape", (p, _, r) => r is Region.Head or Region.Torso && MathF.Abs(p.X) < 0.01f
                                  && p.Z < frame.NeckZ ? p.Y : null);
        return result;
    }

    /// <summary>
    /// Each landmark of <paramref name="dest"/> carried through the map to <paramref name="src"/>'s sheet, found on
    /// <paramref name="src"/>'s surface there, and compared with <paramref name="src"/>'s own landmark of that name: the
    /// distance in millimetres. A landmark the map has no texel for is reported as NaN.
    /// </summary>
    internal static Dictionary<string, float> LandmarkErrors(UvTransferBake.Result map, BakeBody dest, BakeBody src,
                                                             UvTransferBake.Raster srcRaster)
    {
        var mine = Landmarks(dest);
        var theirs = Landmarks(src);
        var errors = new Dictionary<string, float>();
        foreach (var (name, (t, bary)) in mine)
        {
            if (!theirs.TryGetValue(name, out var them)) continue;
            var (_, _, uv) = dest.At(t, bary.X, bary.Y, bary.Z);
            int x = (int)MathF.Round(Math.Clamp(uv.X, 0, 1) * (map.Size - 1));
            int y = (int)MathF.Round(Math.Clamp(uv.Y, 0, 1) * (map.Size - 1));
            if (!NearestValid(map, ref x, ref y, 4)) { errors[name] = float.NaN; continue; }
            if (SourcePoint(map, y * map.Size + x, src, srcRaster) is not { } landed) { errors[name] = float.NaN; continue; }
            var (want, _, _) = src.At(them.Tri, them.Bary.X, them.Bary.Y, them.Bary.Z);
            errors[name] = (landed - want).Length() * 1000f;
        }
        return errors;
    }

    /// <summary>Where on <paramref name="src"/>'s surface map pixel <paramref name="i"/> lands, or null off its sheet.</summary>
    private static Vector3? SourcePoint(UvTransferBake.Result map, int i, BakeBody src, UvTransferBake.Raster srcRaster)
    {
        int sx = (int)MathF.Round(map.R[i] / 65535f * (srcRaster.Size - 1));
        int sy = (int)MathF.Round(map.G[i] / 65535f * (srcRaster.Size - 1));
        int si = sy * srcRaster.Size + sx;
        int st = srcRaster.Owner[si];
        if (st < 0) return null;
        return src.At(st, srcRaster.BaryU[si], srcRaster.BaryV[si], 1 - srcRaster.BaryU[si] - srcRaster.BaryV[si]).Pos;
    }

    private static bool NearestValid(UvTransferBake.Result map, ref int x, ref int y, int reach)
    {
        for (int r = 0; r <= reach; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= map.Size || yy >= map.Size || !map.Valid[yy * map.Size + xx]) continue;
                    x = xx; y = yy;
                    return true;
                }
        return false;
    }

    /// <summary>The gate's verdict on one map, every miss named.</summary>
    internal static List<string> Check(string mapName, Figures f)
    {
        var miss = new List<string>();
        if (f.DestCoverage < Gate.GlobalCoverage) miss.Add($"{mapName}: coverage {f.DestCoverage:P2} < {Gate.GlobalCoverage:P0}");
        if (f.FoldShare > Gate.GlobalFolds) miss.Add($"{mapName}: folds {f.FoldShare:P2} > {Gate.GlobalFolds:P1}");
        if (f.RoundTripGross > Gate.RoundTripGross) miss.Add($"{mapName}: round-trip gross {f.RoundTripGross:P2} > {Gate.RoundTripGross:P1}");
        if (f.RoundTripModerate > Gate.RoundTripModerate) miss.Add($"{mapName}: round-trip moderate {f.RoundTripModerate:P2} > {Gate.RoundTripModerate:P0}");
        foreach (var (name, r) in f.Regions)
        {
            if (r.Owned == 0) { miss.Add($"{mapName}: region {name} not found on the body"); continue; }
            if (r.Coverage < Gate.RegionCoverage) miss.Add($"{mapName}: {name} coverage {r.Coverage:P2} < {Gate.RegionCoverage:P0}");
            if (r.RimSnapShare > Gate.RegionRimSnaps) miss.Add($"{mapName}: {name} rim snaps {r.RimSnapShare:P2}");
            if (r.FoldShare > Gate.RegionFolds) miss.Add($"{mapName}: {name} folds {r.FoldShare:P2} > {Gate.RegionFolds:P0}");
        }
        foreach (var (name, mm) in f.LandmarksMm)
        {
            float limit = (name.StartsWith("fingertip") || name.StartsWith("toe") ? Gate.TipLandmark : Gate.TorsoLandmark) * 1000f;
            if (float.IsNaN(mm) || mm > limit) miss.Add($"{mapName}: landmark {name} off by {mm:F1} mm > {limit:F0} mm");
        }
        return miss;
    }
}
