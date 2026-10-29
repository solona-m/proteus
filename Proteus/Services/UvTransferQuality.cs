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
        public const double RegionCoverage = 0.99, RegionFolds = 0.01;
        // Overall folds: the plan's 0.5% accepted at 1.2% by the user (2026-10-20) — the bake that shipped measured
        // 1.1%, the folds gathered where the bodies' anatomy differs (the crotch above all).
        public const double GlobalFolds = 0.012, GlobalCoverage = 0.99;
        // Gross round trip: no worse than the shipped bibo↔gen3 maps (2.58% at worst, measured by RoundTrip below),
        // the user's call when the first bake missed the 0.5% the plan began with.
        public const double RoundTripGross = 0.026, RoundTripModerate = 0.10;
        public const float TorsoLandmark = 0.015f, TipLandmark = 0.01f;

        /// <summary>
        /// Landmarks the user accepted past <see cref="TorsoLandmark"/> (2026-10-20), where a woman's body and a man's
        /// differ in shape rather than the map in placement: the belly and pubis are shaped apart. At what the shipped
        /// bake measured, rounded up. (The old under-bust point's 32 mm went with it: that point sat at Bibo+'s nipple
        /// height; the crease that replaced it is bounded by <see cref="LandmarkLimitMm"/>.)
        /// </summary>
        public static readonly Dictionary<string, float> AcceptedLandmarkMm = new(StringComparer.Ordinal)
        {
            ["lowerBelly"] = 46f,
        };
        // Identity (two bodies on one layout baked onto each other), in px at 4096: the plan's 4 / 16 accepted by the user
        // (2026-10-20, after checking tattoos in game) at what was measured — Rue+→Bibo+ 4.5 / 30, TBSE-X→TBSE 2.7 / 66 —
        // rounded up. The outliers are where those bodies' shapes differ (Rue+'s wide hips, TBSE-X's torso).
        public const double IdentityP50 = 5, IdentityP95 = 68;

        /// <summary>How far, in px at 4096, a dot on one body's sternum may land from the other's (6.5 mm measured; 27 px).</summary>
        public const float SternumDotPx = 30f;

        /// <summary>How far, in px at 4096, a dot on one body's nipple may land from the other's (about 3 mm).</summary>
        public const float NippleDotPx = 12f;

        /// <summary>
        /// How far ACROSS the skin a nipple may land from the other body's (3 mm). Not the full distance: a breast stands
        /// centimetres before a man's chest, which is the bodies' shape, not the map's placement.
        /// </summary>
        public const float NippleTangentMm = 3f;

        // The chest field (ChestField), checked before any map is baked with it: no triangle of the destination squeezed
        // under a fifth of its area or turned over; pins held to 0.1 mm; arm and neck skin moved no more than 3 mm; no
        // point moved further than 1.05 × the largest offset or more than 1 mm against it; the sternum and the clavicle
        // row held within 2 mm; each target's landing within 0.5 mm of where it must be.
        // The chest's distortion where the field reaches: no more than 1.25 × the plain map's there (area against the
        // map's median scale, and anisotropy, at p99). What the anatomy predicts is reported beside it, not gated: a
        // cap under the anatomy's own need would forbid the correspondence asked for.
        public const double StretchMargin = 1.25;

        public const float FieldJacobianMin = 0.2f, FieldPinMm = 0.1f, FieldOffTorsoMm = 3f, FieldOvershoot = 1.05f,
                           FieldReverseMm = 1f, FieldStillMm = 2f, FieldResidualMm = 0.5f;
    }

    /// <summary>The named places on the torso and legs the gate checks, by name.</summary>
    internal static readonly string[] NamedRegions = ["sternum", "underBust", "lowerBelly", "innerThigh", Disc];

    /// <summary>
    /// The skin within <see cref="DiscRadius"/> of a nipple, facing forward: counted on its own, not by
    /// <see cref="Frame.NamedRegion"/>'s first match, so a texel may be both in it and in another region.
    /// </summary>
    internal const string Disc = "nipple";
    internal const float DiscRadius = 0.03f;

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
        public Dictionary<string, float> LandmarksTangentMm = [];
        /// <summary>The landmarks as the plain projection (no chest field) lands them: the crease's bound comes from here.</summary>
        public Dictionary<string, float> PlainLandmarksMm = [];
        public StretchFigures? Stretch;
        public List<string> Failures = [];
    }

    /// <summary>
    /// What the chest field does to the map where it reaches: per texel, the map's Jacobian against the plain map's —
    /// area as max(a, 1/a), anisotropy as the ratio of singular-value ratios — and what the anatomy predicts of it.
    /// </summary>
    internal sealed class StretchFigures
    {
        public double AreaP50, AreaP99, AreaP999, AreaMax, AnisoP50, AnisoP99, AnisoP999, AnisoMax;
        public double PredictedArea, PredictedAniso, PlainAreaP99, PlainAnisoP99;
        public long Measured, ChangedInside, ChangedOutside;
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

        var nipples = UvBakeNipples.Find(dest).Found is { } found ? new[] { found.L.Pos, found.R.Pos } : [];
        var counted = new List<RegionFigures>(2);
        for (int i = 0; i < sign.Length; i++)
        {
            int t = raster.Owner[i];
            if (t < 0) continue;
            float u = raster.BaryU[i], v = raster.BaryV[i];
            var (pos, nrm, _) = dest.At(t, u, v, 1f - u - v);
            counted.Clear();
            if (frame.NamedRegion(pos, nrm) is { } name) counted.Add(f.Regions[name]);
            if (nrm.Z > 0f && nipples.Any(n => Vector3.Distance(n, pos) <= DiscRadius)) counted.Add(f.Regions[Disc]);
            foreach (var region in counted)
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
            foreach (var region in counted) region.Measured++;
            long vote = islandVotes.GetValueOrDefault(island[t]);
            if (vote != 0 && Math.Sign(vote) != sign[i])
            {
                f.Folds++;
                foreach (var region in counted) region.Folds++;
            }
        }
        f.FoldShare = f.FoldMeasured == 0 ? 0 : (double)f.Folds / f.FoldMeasured;
        return f;
    }

    /// <summary>
    /// The chest field's effect on a map: <paramref name="warped"/> (baked with it) against <paramref name="plain"/>
    /// (without), on <paramref name="dest"/>'s raster. Every texel the field cannot reach must be the same in both; where
    /// it can, the stretch of the one against the other.
    /// </summary>
    internal static StretchFigures WarpStretch(UvTransferBake.Result warped, UvTransferBake.Result plain,
                                               UvTransferBake.Raster raster, BakeBody dest, ChestField field)
        => WarpStretch(warped, plain, raster, i =>
        {
            int t = raster.Owner[i];
            return field.InSupport(dest.At(t, raster.BaryU[i], raster.BaryV[i], 1 - raster.BaryU[i] - raster.BaryV[i]).Pos);
        });

    /// <summary>The same, where <paramref name="reach"/> says which destination texels the correction may change.</summary>
    internal static StretchFigures WarpStretch(UvTransferBake.Result warped, UvTransferBake.Result plain,
                                               UvTransferBake.Raster raster, Func<int, bool> reach)
    {
        int size = warped.Size;
        const int Seam = 65536 / 32;
        var s = new StretchFigures();
        // Each map's own distortion: per texel, its area scale against the map's median (as max(a, 1/a)) and its
        // anisotropy (the Jacobian's singular-value ratio) — for the map with the field and the plain one, at the
        // texels the field reaches. The plain map is no yardstick of what is right there (it squeezed a man's
        // under-bust onto a sliver of the breast), so each is measured on its own and the two compared.
        double medianW = MedianDet(warped), medianP = MedianDet(plain);
        var areaW = new List<double>();
        var anisoW = new List<double>();
        var areaP = new List<double>();
        var anisoP = new List<double>();
        for (int i = 0; i < raster.Owner.Length; i++)
        {
            if (raster.Owner[i] < 0) continue;
            bool same = warped.Valid[i] == plain.Valid[i] && warped.R[i] == plain.R[i] && warped.G[i] == plain.G[i];
            if (!reach(i)) { if (!same) s.ChangedOutside++; continue; }
            if (!same) s.ChangedInside++;
            int x = i % size, y = i / size;
            if (x + 1 >= size || y + 1 >= size) continue;
            int ix = i + 1, iy = i + size;
            foreach (var (m, median, areas, anisos) in new[] { (warped, medianW, areaW, anisoW), (plain, medianP, areaP, anisoP) })
            {
                if (!Both(m, i, ix, iy) || Jacobian(m, i, ix, iy, Seam) is not { } j) continue;
                double det = Math.Abs(j.A * j.D - j.B * j.C);
                if (det == 0) continue;
                double a = det / median;
                areas.Add(Math.Max(a, 1 / a));
                anisos.Add(Ratio(j));
            }
        }
        s.Measured = areaW.Count;
        double P(List<double> v, double q) => v.Count == 0 ? double.NaN : v[Math.Min(v.Count - 1, (int)(v.Count * q))];
        foreach (var v in new[] { areaW, anisoW, areaP, anisoP }) v.Sort();
        (s.AreaP50, s.AreaP99, s.AreaP999, s.AreaMax) = (P(areaW, 0.5), P(areaW, 0.99), P(areaW, 0.999), areaW.Count == 0 ? double.NaN : areaW[^1]);
        (s.AnisoP50, s.AnisoP99, s.AnisoP999, s.AnisoMax) = (P(anisoW, 0.5), P(anisoW, 0.99), P(anisoW, 0.999), anisoW.Count == 0 ? double.NaN : anisoW[^1]);
        (s.PlainAreaP99, s.PlainAnisoP99) = (P(areaP, 0.99), P(anisoP, 0.99));
        return s;

        static double MedianDet(UvTransferBake.Result m)
        {
            var dets = new List<double>();
            for (int i = 0; i + m.Size + 1 < m.Valid.Length; i += 7)
                if (Both(m, i, i + 1, i + m.Size) && Jacobian(m, i, i + 1, i + m.Size, Seam) is { } j && j.A * j.D - j.B * j.C != 0)
                    dets.Add(Math.Abs(j.A * j.D - j.B * j.C));
            dets.Sort();
            return dets.Count == 0 ? 1 : dets[dets.Count / 2];
        }

        static bool Both(UvTransferBake.Result m, int i, int ix, int iy) => m.Valid[i] && m.Valid[ix] && m.Valid[iy];
        static (double A, double B, double C, double D)? Jacobian(UvTransferBake.Result m, int i, int ix, int iy, int seam)
        {
            int dux = m.R[ix] - m.R[i], dvx = m.G[ix] - m.G[i], duy = m.R[iy] - m.R[i], dvy = m.G[iy] - m.G[i];
            if (Math.Abs(dux) > seam || Math.Abs(dvx) > seam || Math.Abs(duy) > seam || Math.Abs(dvy) > seam) return null;
            return (dux, duy, dvx, dvy);
        }
        static double Ratio((double A, double B, double C, double D) j)
        {
            double f = j.A * j.A + j.B * j.B + j.C * j.C + j.D * j.D, det = j.A * j.D - j.B * j.C;
            double root = Math.Sqrt(Math.Max(f * f - 4 * det * det, 0));
            double s1 = Math.Sqrt((f + root) / 2), s2 = Math.Sqrt(Math.Max((f - root) / 2, 0));
            return s2 <= 1e-9 ? 1e9 : s1 / s2;
        }
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
    /// Named points found on each body the same way, so the same point can be found on both and compared. Heights and
    /// sides come from the centroid of the skin each bone carries most of — the bodies share the game's skeleton once
    /// TBSE is bent, so a bone's centroid is the same part of both — and the point is the body's surface there:
    /// fingertips and big-toe tips (the extremes of the hand and foot), the sternum, under-bust and lower belly (the
    /// front of the body at the upper spine, the middle spine and the pelvis), the nape (the back of the neck), the
    /// inner thighs (the innermost thigh at the thigh bone) and the shoulder tops (the top of the body over the upper
    /// arm bone).
    /// </summary>
    internal static Dictionary<string, (int Tri, Vector3 Bary)> Landmarks(BakeBody body)
    {
        var result = new Dictionary<string, (int, Vector3)>();
        var centroid = new Dictionary<string, (Vector3 Sum, int N)>(StringComparer.Ordinal);
        for (int v = 0; v < body.P.Length; v++)
        {
            string? top = null;
            float best = 0f;
            foreach (var (b, w) in body.Weights[v]) if (w > best) { best = w; top = b; }
            if (top == null) continue;
            var (sum, n) = centroid.GetValueOrDefault(top);
            centroid[top] = (sum + body.P[v], n + 1);
        }
        Vector3? C(string bone) => centroid.TryGetValue(bone, out var c) && c.N > 0 ? c.Sum / c.N : null;

        void Pick(string name, Func<Vector3, Region, float?> score)
        {
            float best = float.MinValue;
            int bestT = -1, bestK = 0;
            for (int t = 0; t < body.TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int v = body.Tri[t * 3 + k];
                    if (score(body.P[v], body.VertexRegion[v]) is { } s && (s > best || (s == best && t < bestT)))
                    { best = s; bestT = t; bestK = k; }
                }
            if (bestT >= 0) result[name] = (bestT, bestK == 0 ? Vector3.UnitX : bestK == 1 ? Vector3.UnitY : Vector3.UnitZ);
        }
        const float Band = 0.01f;

        // The nipples and, under each, the under-bust crease (UvBakeNipples). The old under-bust point — the front at
        // the middle spine's height — is kept as underBustOld for the record: on Bibo+ that height is its nipple's.
        if (UvBakeNipples.Find(body).Found is { } nipples)
        {
            var bvh = new TriangleBvh(body);
            foreach (var (side, n) in new[] { ("L", nipples.L), ("R", nipples.R) })
            {
                result["nipple" + side] = (n.Tri, n.Bary);
                if (UvBakeNipples.Crease(body, n, bvh).Found is { } crease) result["underBust" + side] = (crease.Tri, crease.Bary);
            }
        }
        foreach (var (side, sx, suffix) in new[] { ("L", 1f, "_l"), ("R", -1f, "_r") })
        {
            var hand = sx > 0 ? Region.HandL : Region.HandR;
            var foot = sx > 0 ? Region.FootL : Region.FootR;
            var thigh = sx > 0 ? Region.ThighL : Region.ThighR;
            Pick("fingertip" + side, (p, r) => r == hand ? p.X * sx : null);
            Pick("toe" + side, (p, r) => r == foot ? p.Z : null);
            if (C("j_sebo_b") is { } mid)
                Pick("underBustOld" + side, (p, _) => MathF.Abs(p.X - 0.08f * sx) < Band && MathF.Abs(p.Y - mid.Y) < Band ? p.Z : null);
            if (C("j_asi_a" + suffix) is { } th)
                Pick("innerThigh" + side, (p, r) => r == thigh && MathF.Abs(p.Y - th.Y) < Band ? -p.X * sx : null);
            if (C("j_ude_a" + suffix) is { } arm)
                Pick("shoulder" + side, (p, _) => MathF.Abs(p.X - arm.X) < Band ? p.Y : null);
        }
        if (C("j_sebo_c") is { } upper)
            Pick("sternum", (p, _) => MathF.Abs(p.X) < Band && MathF.Abs(p.Y - upper.Y) < Band ? p.Z : null);
        if (C("j_kosi") is { } pelvis)
            Pick("lowerBelly", (p, _) => MathF.Abs(p.X) < Band && MathF.Abs(p.Y - pelvis.Y) < Band ? p.Z : null);
        if (C("j_kubi") is { } neck)
            Pick("nape", (p, _) => MathF.Abs(p.X) < Band && MathF.Abs(p.Y - neck.Y) < Band ? -p.Z : null);
        return result;
    }

    /// <summary>
    /// Each landmark of <paramref name="dest"/> carried through the map to <paramref name="src"/>'s sheet, found on
    /// <paramref name="src"/>'s surface there, and compared with <paramref name="src"/>'s own landmark of that name: the
    /// distance in millimetres. A landmark the map has no texel for is reported as NaN.
    /// </summary>
    /// <param name="tangent">Filled, when given, with each error less its part along the source's normal there: how far
    /// the landing is ACROSS the skin, which a correspondence decides, without how far the two surfaces stand apart,
    /// which the bodies' shapes do (a breast stands centimetres before a man's chest).</param>
    internal static Dictionary<string, float> LandmarkErrors(UvTransferBake.Result map, BakeBody dest, BakeBody src,
                                                             UvTransferBake.Raster srcRaster,
                                                             Dictionary<string, float>? tangent = null)
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
            if (tangent != null)
            {
                var n = src.SmoothAt(them.Tri, them.Bary.X, them.Bary.Y, them.Bary.Z);
                var d = landed - want;
                tangent[name] = (d - n * Vector3.Dot(d, n)).Length() * 1000f;
            }
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
            if (r.FoldShare > Gate.RegionFolds) miss.Add($"{mapName}: {name} folds {r.FoldShare:P2} > {Gate.RegionFolds:P0}");
        }
        foreach (var (name, mm) in f.LandmarksMm)
        {
            if (name.StartsWith("underBustOld", StringComparison.Ordinal)) continue;   // reported only
            if (name.StartsWith("nipple", StringComparison.Ordinal))
            {
                float across = f.LandmarksTangentMm.GetValueOrDefault(name, float.NaN);
                if (float.IsNaN(across) || across > Gate.NippleTangentMm)
                    miss.Add($"{mapName}: landmark {name} off by {across:F1} mm across the skin > {Gate.NippleTangentMm:F0} mm");
                continue;
            }
            float limit = LandmarkLimitMm(name, f);
            if (float.IsNaN(mm) || mm > limit) miss.Add($"{mapName}: landmark {name} off by {mm:F1} mm > {limit:F0} mm");
        }
        if (f.Stretch is { } s)
        {
            if (!(s.AreaP99 <= s.PlainAreaP99 * Gate.StretchMargin))
                miss.Add($"{mapName}: chest distortion area p99 {s.AreaP99:F2} > {Gate.StretchMargin} × the plain map's {s.PlainAreaP99:F2} there");
            if (!(s.AnisoP99 <= s.PlainAnisoP99 * Gate.StretchMargin))
                miss.Add($"{mapName}: chest distortion anisotropy p99 {s.AnisoP99:F2} > {Gate.StretchMargin} × the plain map's {s.PlainAnisoP99:F2} there");
            if (s.ChangedOutside > 0) miss.Add($"{mapName}: the chest field changed {s.ChangedOutside} texels outside its reach");
        }
        return miss;
    }

    /// <summary>
    /// A landmark's bound: the under-bust crease's from the plain projection's own error there (rounded up, never
    /// under the torso's 15 mm) — the crease replaced a mis-placed point and has no older bound — the user's accepted
    /// figures, then the torso's or tip's.
    /// </summary>
    internal static float LandmarkLimitMm(string name, Figures f)
    {
        if (name.StartsWith("underBust", StringComparison.Ordinal) && f.PlainLandmarksMm.TryGetValue(name, out var plain) && !float.IsNaN(plain))
            return Math.Max(MathF.Ceiling(plain), Gate.TorsoLandmark * 1000f);
        return Gate.AcceptedLandmarkMm.TryGetValue(name, out var accepted) ? accepted
             : (name.StartsWith("fingertip") || name.StartsWith("toe") ? Gate.TipLandmark : Gate.TorsoLandmark) * 1000f;
    }
}
