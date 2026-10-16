using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;
using static Proteus.Services.BodyBridge;

internal static partial class NavelBridge
{
    /// <summary>How much higher the pit's edge is taken each time the last one ran into a hem or a join.</summary>
    private const float NavelLevelStep = 1.25f;

    /// <summary>The highest the pit's edge is taken, as a share of its depth; past it the fill is called off.</summary>
    private const float NavelLevelCeiling = 0.5f;

    /// <summary>How far out, as a multiple of the pit's own reach, the belly the fill carries on is read from.</summary>
    private const float NavelFitReach = 2f;

    /// <summary>
    /// The belly the fill may bow up like, as a radius of curvature: over the membrane, at a distance d from its rim,
    /// no more than d²/2R. A belly curving tighter than this is under-filled by the difference; on the bodies measured
    /// 7 cm and 10 cm fill identically (Neolithe 0.49 mm left, Rue+ 0.76, Bibo+ 0.01), so the cap only takes off a
    /// fit gone wrong.
    /// </summary>
    private const float NavelFlattestBelly = 0.1f;

    /// <summary>How far clear of the skin a node the membrane laid under it is raised: a fifth of a millimetre.</summary>
    private const float NavelSkinMargin = 0.0002f;

    /// <summary>Most Gauss–Seidel sweeps the membrane gets; it settles in far fewer over a navel.</summary>
    private const int NavelSolvePasses = 400;

    /// <summary>A sweep that moves no node further than this has settled: a hundredth of a millimetre.</summary>
    private const float NavelSolveTolerance = 1e-5f;

    /// <summary>
    /// Most times the pit is grown by what the last fill left hollow and solved again. The lift reading misjudges
    /// the steepest walls of a navel, which the first fill then uncovers; a ring or two more is all it ever needs.
    /// </summary>
    private const int NavelGrowRounds = 8;

    /// <summary>
    /// How much hollower than before a node beside the pit must read once filled to count as uncovered: the filled
    /// navel standing a quarter of a millimetre over its own rim reads as a moat round it.
    /// </summary>
    private const float NavelUncovered = 0.00015f;

    /// <summary>
    /// How far a node must move before it is tested for having gone inside the body. A node that barely moved sits on
    /// the skin, where a winding number reads a half from either side and cannot answer.
    /// </summary>
    private const float NavelInsideCheck = 0.001f;

    /// <summary>
    /// The smallest a filled triangle may come out across the axis, as a share of a mesh edge squared: under it the
    /// triangle has folded edge-on and draws as a hole. Tiny on purpose — the membrane packs a navel's floor into its
    /// mouth, and Rue+'s floor triangles come out a thirtieth of their size, still facing out.
    /// </summary>
    private const float NavelMinProjectedArea = 1e-6f;

    /// <summary>
    /// The cloth stretched flat across the navel at <paramref name="site"/>: the pit's nodes laid on a membrane held
    /// at its rim, and never lowered. Null when nothing moves; <paramref name="report"/> says why.
    /// </summary>
    /// <remarks>
    /// A MEMBRANE, not a lift to the hull: lifting each node straight up the axis stands a navel's steep walls on
    /// edge and turns an overhanging hood over. The membrane is harmonic in all three coordinates with the rim fixed,
    /// which across the axis is a Tutte embedding: it cannot fold while the rim is convex, and a navel's rim nearly
    /// is. Whether it held is checked, never assumed — every triangle round the pit must still face out and keep its
    /// area, and nothing moved may end up inside the body, or the whole fill is called off. Nothing is pulled inward,
    /// so the skin under it stays behind the cloth.
    /// </remarks>
    internal static BustBridgePlan? NavelFill(Vec3[] pos, Vec3[] nrm, ushort[] tris, NavelSite site, bool[]? covered,
                                             bool[]? joinPin, float strength, Action<string>? log,
                                             out NavelFillReport report)
    {
        report = new NavelFillReport();
        return new NavelFilling(pos, nrm, tris, site, covered, joinPin, strength, log, report).Run();
    }

    private sealed class NavelFilling(Vec3[] pos, Vec3[] nrm, ushort[] tris, NavelSite site, bool[]? covered,
                                      bool[]? joinPin, float strength, Action<string>? log, NavelFillReport report)
    {
        private NavelSurface s = null!;
        private List<int> region = null!;
        private Dictionary<int, int> index = null!;
        private float strip;
        private float[] lift = null!;
        private float[] after = null!;
        private int deepest;
        private float level;
        private bool[] cutN = null!;
        private bool[] pinN = null!;
        private HashSet<int> pit = null!;
        private HashSet<int> rim = null!;
        private Vec3[] final = null!;

        public BustBridgePlan? Run()
        {
            if (strength <= 0f || pos.Length == 0) return null;
            if (!FindPit()) return null;
            if (!BoundPit()) return null;
            for (int round = 0; ; round++)
            {
                Stretch();
                KeepAboveSkin();
                after = ClosedLift(final, s.Adj, region, site.Axis, strip);
                if (round + 1 >= NavelGrowRounds) break;
                // Growing is a refinement: where it would reach a hem or a join, the last fill stands.
                var (keptPit, keptRim) = (new HashSet<int>(pit), new HashSet<int>(rim));
                if (Grow() == 0) break;
                if (Clearance() is { } why)
                {
                    log?.Invoke($"navel fill: stopped growing the pit — {why}");
                    (pit, rim) = (keptPit, keptRim);
                    break;
                }
            }
            if (!Validate()) return null;
            return Plan();
        }

        private bool Decline(string why)
        {
            report.Outcome = NavelOutcome.Declined;
            report.Reason = why;
            log?.Invoke($"navel fill: not filled — {why}");
            return false;
        }

        /// <summary>The pit on THIS surface: the shape keys and the part's own triangles may have moved it.</summary>
        private bool FindPit()
        {
            var t = new int[tris.Length];
            for (int i = 0; i < tris.Length; i++) t[i] = tris[i];
            s = NavelSurface.Weld(pos, nrm, t, null, null);

            region = [];
            for (int n = 0; n < s.NodeCount; n++)
            {
                // A vertex no triangle uses (a hole's middle, a spare) is no part of the surface.
                if (s.Adj[n].Count == 0 || Dist(s.At[n], site.Centre) > site.DiscRadius) continue;
                if (Dot(s.Nn[n], site.Axis) < -0.3f) continue;
                region.Add(n);
            }
            // Not this mesh's navel: another part's, or another mesh of this part. Silent; the build reports the site.
            if (region.Count < MinNavelNodes) return false;

            index = new Dictionary<int, int>(region.Count);
            for (int i = 0; i < region.Count; i++) index[region[i]] = i;
            strip = MathF.Max(MeanEdgeLength(s.At, s.Adj, region), 1e-4f);
            lift = ClosedLift(s.At, s.Adj, region, site.Axis, strip);

            deepest = -1;
            float depth = 0f;
            for (int i = 0; i < region.Count; i++)
                if (Dist(s.At[region[i]], site.Centre) <= site.MouthRadius && lift[i] > depth)
                { depth = lift[i]; deepest = region[i]; }
            report.DepthBefore = depth;
            return deepest >= 0 && depth >= NavelLiftFloor;
        }

        /// <summary>
        /// The pit and the ring round it, clear of every reason not to touch them: a hem or a hole through the pit
        /// leaves half a navel, and a ring shared with another part must not move under it.
        /// </summary>
        private bool BoundPit()
        {
            cutN = new bool[s.NodeCount];
            pinN = new bool[s.NodeCount];
            for (int i = 0; i < pos.Length; i++)
            {
                if (covered != null && i < covered.Length && !covered[i]) cutN[s.NodeOf[i]] = true;
                if (joinPin != null && i < joinPin.Length && joinPin[i]) pinN[s.NodeOf[i]] = true;
            }

            // Every node with any lift, out to where the belly is back on its hull. A navel can sit in a shallow
            // hollow of its own that runs on to a hem or a join, so the edge of the pit is taken higher, a step at a
            // time, until it is clear of them — never past half the depth, where it would no longer be the navel.
            // Shallower than twice the floor, there is no level both above the floor and inside half the depth.
            if (NavelLiftFloor > NavelLevelCeiling * report.DepthBefore)
                return Decline($"the pit here is only {report.DepthBefore * 1000:0.##}mm deep");
            string? why = null;
            for (level = NavelLiftFloor; level <= NavelLevelCeiling * report.DepthBefore; level *= NavelLevelStep)
            {
                pit = [.. Flood(s, deepest, index, lift, level, null)];
                Convexify();
                if (Clearance() is not { } fails)
                {
                    if (level > NavelLiftFloor)
                        log?.Invoke($"navel fill: the pit taken from {level * 1000:0.##}mm in, clear of a hem or join "
                                  + "further out");
                    return true;
                }
                why ??= fails;
            }
            return Decline(why!);
        }

        /// <summary>
        /// Takes into the pit every node of the region that lies inside its convex hull seen down the axis, so the
        /// ring the membrane is held at is convex: the one shape Tutte's guarantee covers. A slit of a navel running
        /// into a channel is anything but, and its membrane folds.
        /// </summary>
        private void Convexify()
        {
            Basis(site.Axis, out var u, out var v);
            (float X, float Y) Flat(Vec3 p) => (Dot(p, u), Dot(p, v));
            var hull = ConvexHull([.. pit.Select(n => Flat(s.At[n]))]);
            if (hull.Length < 3) { Rim(); return; }
            foreach (int n in region)
            {
                if (pit.Contains(n)) continue;
                var q = Flat(s.At[n]);
                bool inside = true;
                for (int i = 0; i < hull.Length && inside; i++)
                {
                    var a = hull[i];
                    var b = hull[(i + 1) % hull.Length];
                    inside = (b.X - a.X) * (q.Y - a.Y) - (b.Y - a.Y) * (q.X - a.X) >= 0f;
                }
                if (inside) pit.Add(n);
            }
            Rim();
        }

        private void Rim()
        {
            rim = [];
            foreach (int n in pit)
                foreach (int k in s.Adj[n])
                    if (!pit.Contains(k)) rim.Add(k);
        }

        /// <summary>Why the pit as it stands cannot be filled, or null when it can.</summary>
        private string? Clearance()
        {
            int cut = 0, open = 0, pinned = 0, past = 0;
            foreach (int n in pit.Concat(rim))
            {
                if (cutN[n]) cut++;
                if (s.Open[n]) open++;
                if (pinN[n]) pinned++;
                if (!index.ContainsKey(n)) past++;
            }
            return pinned > 0 ? $"the navel is on the join with another part ({pinned} vertex(es) on it)"
                 : cut > 0 ? $"the garment's edge runs through the navel ({cut} vertex(es) uncovered)"
                 : open > 0 ? $"the mesh is open at the navel ({open} vertex(es) on a hole or hem)"
                 : past > 0 ? $"the hollow runs on past {site.DiscRadius * 1000:0}mm round the navel"
                 : null;
        }

        /// <summary>
        /// Takes into the pit every node beside it that the last fill UNCOVERED — left at least as hollow as the pit's
        /// own edge, and hollower than it read before: measured on the result, inside the pass. Not the shallow ground
        /// the pit was bounded clear of, which reads the same before and after. Returns how many joined.
        /// </summary>
        private int Grow()
        {
            // One beside a hem, a join or the edge of the region stays out: it would put that edge into the rim.
            bool Bad(int n) => cutN[n] || pinN[n] || s.Open[n] || !index.ContainsKey(n);
            var add = new List<int>();
            foreach (int n in rim)
                if (index.TryGetValue(n, out int i) && after[i] >= level && after[i] > lift[i] + NavelUncovered
                    && !Bad(n) && !s.Adj[n].Any(k => !pit.Contains(k) && Bad(k)))
                    add.Add(n);
            foreach (int n in add) pit.Add(n);
            if (add.Count > 0) Convexify();
            return add.Count;
        }

        /// <summary>The membrane over the pit, then the height of each node never lowered.</summary>
        private void Stretch()
        {
            var x = (Vec3[])s.At.Clone();
            var order = pit.ToArray();
            for (int pass = 0; pass < NavelSolvePasses; pass++)
            {
                float most = 0f;
                foreach (int n in order)
                {
                    var adj = s.Adj[n];
                    if (adj.Count == 0) continue;
                    float sx = 0f, sy = 0f, sz = 0f;
                    foreach (int k in adj) { sx += x[k].X; sy += x[k].Y; sz += x[k].Z; }
                    var m = new Vec3(sx / adj.Count, sy / adj.Count, sz / adj.Count);
                    most = MathF.Max(most, Dist(m, x[n]));
                    x[n] = m;
                }
                if (most < NavelSolveTolerance) break;
            }

            // Across the axis the membrane's own layout. Along it, the belly carried on over the pit: a membrane's own
            // heights sag on a convex belly by r²/2R, a millimetre over Neolithe's navel, so the height comes from a
            // quadric through the belly round the rim instead (the membrane's where the fit fails). Blended in by the
            // strength, and never below where the node started — the fill only ever lifts.
            var ax = site.Axis;
            Basis(ax, out var u, out var v);
            var c = site.Centre;
            var fit = BellyFit(u, v);
            float Q(Vec3 p)
            {
                if (fit is not { } q) return 0f;
                double du = Dot(p, u) - Dot(c, u), dv = Dot(p, v) - Dot(c, v);
                return (float)(q[0] + q[1] * du + q[2] * dv + q[3] * du * du + q[4] * du * dv + q[5] * dv * dv);
            }

            // The quadric is the belly's shape, not its exact height: on a long pit it is fitted over most of the belly,
            // and misses the rim by a millimetre. What it misses at the rim is carried in harmonically, so the fill
            // meets its rim exactly — the rim as it is, or a rim left in a shallow hollow reads as a moat round a fill
            // standing over it — and bows with the belly in between. Without a fit this is the membrane's own height.
            var residual = new float[s.NodeCount];
            foreach (int n in rim) residual[n] = Dot(s.At[n], ax) - Q(s.At[n]);
            for (int pass = 0; pass < NavelSolvePasses; pass++)
            {
                float most = 0f;
                foreach (int n in order)
                {
                    var adj = s.Adj[n];
                    if (adj.Count == 0) continue;
                    float sum = 0f;
                    foreach (int k in adj) sum += residual[k];
                    float m = sum / adj.Count;
                    most = MathF.Max(most, MathF.Abs(m - residual[n]));
                    residual[n] = m;
                }
                if (most < NavelSolveTolerance * 0.01f) break;
            }

            // Blended in by the strength, and never below where the node started — the fill only ever lifts.
            float s01 = Math.Clamp(strength, 0f, 1f);
            final = (Vec3[])s.At.Clone();
            proud = 0f;
            foreach (int n in order)
            {
                float h0 = Dot(s.At[n], ax), hm = Dot(x[n], ax);
                float target = Q(x[n]) + residual[n];
                // Never higher than the membrane plus what the flattest belly bows up there: the membrane cannot bulge
                // (its heights stay inside the rim's), so a target over that is a fit gone wrong, a raised button. Rue+'s
                // long pit's target stands 0.55 mm over it. How far it was is kept for the report.
                float d2 = float.MaxValue;
                foreach (int r in rim)
                {
                    float du = Dot(x[n], u) - Dot(s.At[r], u), dv = Dot(x[n], v) - Dot(s.At[r], v);
                    d2 = MathF.Min(d2, du * du + dv * dv);
                }
                if (d2 == float.MaxValue) d2 = 0f;
                float bow = hm + d2 / (2f * NavelFlattestBelly);
                proud = MathF.Max(proud, target - bow);
                target = MathF.Min(target, bow);
                float h = MathF.Max(h0, h0 + s01 * (target - h0));
                final[n] = new Vec3(x[n].X + ax.X * (h - hm), x[n].Y + ax.Y * (h - hm), x[n].Z + ax.Z * (h - hm));
            }
        }

        /// <summary>The most any pit node's target stood over the membrane beyond the belly's own bow, before the clamp.</summary>
        private float proud;

        /// <summary>
        /// The belly round the pit as h(u, v), a quadric about the site through everything outside the pit out to
        /// twice its reach; null when too few points or too little spread fix one. Not the rim alone: on a ring u² + v²
        /// is all but constant, so the bowl of the belly is left to chance and the middle of the fill floats. And each
        /// point at its HULL height (its own plus its lift): the shallow ground a pit fades out over bends a fit of
        /// the bare surface down toward the middle.
        /// </summary>
        private double[]? BellyFit(Vec3 u, Vec3 v)
        {
            var c = s.At[deepest];
            float reach = 0f;
            foreach (int n in pit) reach = MathF.Max(reach, Dist(s.At[n], c));
            var pts = new List<(double U, double V, double H)>();
            for (int i = 0; i < region.Count; i++)
            {
                int n = region[i];
                if (pit.Contains(n) || Dist(s.At[n], c) > NavelFitReach * reach) continue;
                pts.Add((Dot(s.At[n], u) - Dot(site.Centre, u), Dot(s.At[n], v) - Dot(site.Centre, v),
                         Dot(s.At[n], site.Axis) + lift[i]));
            }
            return FitQuadric(pts);
        }

        /// <summary>
        /// Raise every pit node the membrane laid under the skin back out over it: the membrane slides nodes across
        /// the axis, and on the convex lip of the pit sideways is a fraction of a millimetre into the body. A ray up the
        /// axis from the node through the original surface, the furthest crossing taken.
        /// </summary>
        private void KeepAboveSkin()
            => RaiseAboveSkin(s.At, final, tris, s.NodeOf, pit, index.ContainsKey, site.Axis, NavelSkinMargin);

        /// <summary>
        /// Every triangle round the pit still faces the way the belly does and keeps its area across the axis, and no
        /// node that moved went inside the body; measured on the result, inside the pass.
        /// </summary>
        private bool Validate()
        {
            int inward = NavelFolded(s.At, final, tris, s.NodeOf, pit, index.ContainsKey, site.Axis,
                                     NavelMinProjectedArea * strip * strip);
            report.InwardTriangles = inward;
            int inside = NavelInside(s.At, final, tris, s.NodeOf, pit);
            report.InsideNodes = inside;

            if (inward > 0 || inside > 0)
                return Decline($"the cloth would fold ({inward} triangle(s) turned edge-on or over) or go into the "
                             + $"body ({inside} vertex(es)) — the navel keeps its shape");
            report.Proud = proud;

            // How deep a hollow is left, read the way the pit was found.
            float left = 0f;
            for (int i = 0; i < region.Count; i++)
                if (pit.Contains(region[i]) || rim.Contains(region[i])) left = MathF.Max(left, after[i]);
            report.DepthAfter = left;
            return true;
        }

        private BustBridgePlan Plan()
        {
            var delta = new Vec3[pos.Length];
            for (int i = 0; i < pos.Length; i++)
            {
                int n = s.NodeOf[i];
                if (!pit.Contains(n)) continue;
                delta[i] = new Vec3(final[n].X - s.At[n].X, final[n].Y - s.At[n].Y, final[n].Z - s.At[n].Z);
            }
            // The rim is reshaded too: its normals still lean into a pit that is no longer there. Only the pit is kept
            // off the extra clearance: a rim node another span moved keeps what that span gives it.
            var weight = new float[s.NodeCount];
            var noExtra = new bool[s.NodeCount];
            foreach (int n in rim) weight[n] = 1f;
            foreach (int n in pit) { weight[n] = 1f; noExtra[n] = true; }

            report.Outcome = NavelOutcome.Filled;
            report.Spanned = pit.Count;
            log?.Invoke($"navel fill: filled {pit.Count} vertex(es) — {report.DepthBefore * 1000:0.##}mm deep, "
                      + $"{report.DepthAfter * 1000:0.##}mm left, {report.InwardTriangles} triangle(s) folded, "
                      + $"{report.InsideNodes} inside the body");
            return new BustBridgePlan
            {
                Delta = delta, NodeOf = s.NodeOf, NodeWeight = weight, NodeNormal = s.Nn, NoExtraClearance = noExtra,
            };
        }

        private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        /// <summary>Twice the triangle's vector area.</summary>
        private static Vec3 Cross(Vec3 a, Vec3 b, Vec3 c)
        {
            float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            return new Vec3(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
        }
    }

    /// <summary>
    /// How many triangles touching <paramref name="moved"/> no longer face the way the belly does across
    /// <paramref name="axis"/>, or have shrunk under <paramref name="minArea"/> there — turned over, or edge-on. Which way
    /// is out is read off the untouched triangles inside <paramref name="near"/>, so a mesh wound either way is judged
    /// against itself.
    /// </summary>
    internal static int NavelFolded(Vec3[] before, Vec3[] after, ushort[] tris, int[] nodeOf, ISet<int> moved,
                                    Func<int, bool> near, Vec3 axis, float minArea)
    {
        static float Facing(Vec3 a, Vec3 b, Vec3 c, Vec3 ax)
        {
            float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            return (uy * vz - uz * vy) * ax.X + (uz * vx - ux * vz) * ax.Y + (ux * vy - uy * vx) * ax.Z;
        }

        int plus = 0, minus = 0;
        var touching = new List<(int A, int B, int C)>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            if (tris[t] >= nodeOf.Length || tris[t + 1] >= nodeOf.Length || tris[t + 2] >= nodeOf.Length) continue;
            int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
            if (a == b || b == c || a == c) continue;
            if (moved.Contains(a) || moved.Contains(b) || moved.Contains(c)) { touching.Add((a, b, c)); continue; }
            if (!near(a) || !near(b) || !near(c)) continue;
            float f = Facing(before[a], before[b], before[c], axis);
            if (f > 0f) plus++; else if (f < 0f) minus++;
        }
        float sign = plus >= minus ? 1f : -1f;
        int folded = 0;
        // Twice the area across the axis, signed by winding.
        foreach (var (a, b, c) in touching)
            if (sign * Facing(after[a], after[b], after[c], axis) < 2f * minArea) folded++;
        return folded;
    }

    /// <summary>
    /// How many of <paramref name="moved"/> ended up inside the body the surface encloses, by winding number. Only
    /// those that moved past <see cref="NavelInsideCheck"/>: one still on the skin reads a half from either side.
    /// </summary>
    internal static int NavelInside(Vec3[] before, Vec3[] after, ushort[] tris, int[] nodeOf, IEnumerable<int> moved)
    {
        var body = new BodyWinding(before, Array.ConvertAll(tris, v => (int)v), nodeOf);
        int inside = 0;
        foreach (int n in moved)
            if (Dist(after[n], before[n]) > NavelInsideCheck && body.Inside(after[n])) inside++;
        return inside;
    }

    /// <summary>
    /// Raises each of <paramref name="moved"/> that lies under the surface as it was — any triangle touching
    /// <paramref name="near"/> above it along <paramref name="axis"/> — to <paramref name="margin"/> over the furthest
    /// such triangle. Returns how many were raised.
    /// </summary>
    internal static int RaiseAboveSkin(Vec3[] before, Vec3[] after, ushort[] tris, int[] nodeOf, IEnumerable<int> moved,
                                       Func<int, bool> near, Vec3 axis, float margin)
    {
        var faces = new List<(int A, int B, int C)>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            if (tris[t] >= nodeOf.Length || tris[t + 1] >= nodeOf.Length || tris[t + 2] >= nodeOf.Length) continue;
            int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
            if (a == b || b == c || a == c) continue;
            if (near(a) || near(b) || near(c)) faces.Add((a, b, c));
        }
        int raised = 0;
        foreach (int n in moved)
        {
            float far = -1f;
            foreach (var (a, b, c) in faces)
                if (RayHit(after[n], axis, before[a], before[b], before[c]) is { } d && d > far) far = d;
            if (far < 0f) continue;
            float up = far + margin;
            after[n] = new Vec3(after[n].X + axis.X * up, after[n].Y + axis.Y * up, after[n].Z + axis.Z * up);
            raised++;
        }
        return raised;
    }

    /// <summary>Distance along <paramref name="dir"/> from <paramref name="o"/> to the triangle, or null for none ahead.</summary>
    private static float? RayHit(Vec3 o, Vec3 dir, Vec3 a, Vec3 b, Vec3 c)
    {
        // Möller–Trumbore.
        float e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z;
        float e2x = c.X - a.X, e2y = c.Y - a.Y, e2z = c.Z - a.Z;
        float px = dir.Y * e2z - dir.Z * e2y, py = dir.Z * e2x - dir.X * e2z, pz = dir.X * e2y - dir.Y * e2x;
        float det = e1x * px + e1y * py + e1z * pz;
        if (MathF.Abs(det) < 1e-14f) return null;
        float inv = 1f / det;
        float tx = o.X - a.X, ty = o.Y - a.Y, tz = o.Z - a.Z;
        float u = (tx * px + ty * py + tz * pz) * inv;
        if (u < 0f || u > 1f) return null;
        float qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
        float v = (dir.X * qx + dir.Y * qy + dir.Z * qz) * inv;
        if (v < 0f || u + v > 1f) return null;
        float d = (e2x * qx + e2y * qy + e2z * qz) * inv;
        return d > 0f ? d : null;
    }

    /// <summary>
    /// The one line a build owes when a navel was found but no mesh did anything with it — neither filled nor said why
    /// not — so the log always says what became of it. Null when there is nothing to say.
    /// </summary>
    internal static string? NavelSummary(NavelSite? site, IReadOnlyCollection<NavelOutcome> outcomes)
        => site == null || outcomes.Any(o => o != NavelOutcome.NotOnThisMesh) ? null
         : $"navel fill: the navel was found on part {site.Owner}, but none of its meshes the garment covers holds it";

    /// <summary>
    /// The navel's plan merged onto the other passes', unless they already moved any vertex of its pit or its rim: two
    /// displacements summed on one vertex are checked by neither, and can fold. Then the navel is left out, said so.
    /// </summary>
    internal static BustBridgePlan? MergeNavel(BustBridgePlan? others, BustBridgePlan? navel, Action<string>? log)
    {
        if (others == null || navel == null) return others ?? navel;
        // Its rim too, not only what it moves: the fill was solved and checked against a rim that stays put.
        int shared = 0;
        for (int i = 0; i < navel.Delta.Length && i < others.Delta.Length && i < navel.NodeOf.Length; i++)
        {
            int n = navel.NodeOf[i];
            if (n < navel.NodeWeight.Length && navel.NodeWeight[n] > 0f && Len(others.Delta[i]) > BustBridgeEpsilon) shared++;
        }
        if (shared == 0) return MergePlans(others, navel);
        log?.Invoke($"navel fill: left out — another span already moves {shared} vertex(es) of the navel");
        return others;
    }
}
