using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Region = Proteus.Services.UvBakeBones.Region;

namespace Proteus.Services;

/// <summary>
/// The chest correction of one map: a smooth displacement of the destination body's points, made before each is cast
/// onto the source, so its nipples land on the source's while the rest of the chest stays where the plain projection
/// puts it. Without it a woman's breast, standing forward and lower than a man's chest, takes a ray from his nipple
/// above hers: her nipple art lands about 3 cm under his.
/// <para/>
/// The field is a sum of Wendland C2 bumps, one per site, weighted so that each nipple moves by its solved offset and
/// each pin by nothing; faded to nothing inside the clavicle row and the lower pin row. Only the ray's origin moves:
/// normals, regions and <see cref="UvTransferBake.Land"/>'s rules are the plain bake's.
/// <para/>
/// The under-bust creases are not targets, only where the lower row is found: matching crease to crease as well pulls
/// a man's shirt refit for a woman into the fold under her breast (49 faces turned over on the lumberjack shirt, against
/// 8), where nipples alone leave it as it hung (8) and still bring the crease nearer than the plain projection does.
/// </summary>
internal sealed class ChestField
{
    /// <summary>
    /// The bumps' reach (14 cm). The nipples stand 157 mm apart and 79 mm off the sternum, the clavicle row 53 mm (TBSE)
    /// to 99 mm (Bibo+) above them: this reaches the pins from each nipple, so the pins hold the field. Fixed, not searched.
    /// </summary>
    internal const float Radius = 0.14f;

    /// <summary>How far inside the clavicle row and the lower pin row the field fades to nothing (2 cm).</summary>
    internal const float Fade = 0.02f;

    /// <summary>How far under the destination's crease the lower pin row lies at least (4 cm) and at most (15 cm).</summary>
    internal const float LowerRow = 0.04f, MaxLowerRow = 0.15f;

    /// <summary>How far either side of the middle the clavicle row is held still (12 cm: past each nipple).</summary>
    internal const float ClavicleBand = 0.12f;

    /// <summary>A pin may be no nearer a target than this (1 cm).</summary>
    internal const float PinClearance = 0.01f;

    internal const int MaxIterations = 8;

    /// <summary>
    /// The condition number past which the solve takes a fixed ridge (λ = <see cref="Ridge"/> × the mean diagonal):
    /// bumps far wider than the sites' spacing make the system nearly singular, and its weights ring.
    /// </summary>
    internal const double MaxCondition = 1e8, Ridge = 1e-6;

    internal readonly record struct Site(string Name, Vector3 Centre, Vector3 Offset, bool Pin);

    public IReadOnlyList<Site> Sites { get; }
    public float YLow { get; }
    public float YClav { get; }
    public double Condition { get; }
    public bool Ridged { get; }
    public int Iterations { get; }
    /// <summary>Per target, how far across the skin its landing ended from the source's point (mm).</summary>
    public IReadOnlyDictionary<string, float> ResidualMm { get; }

    private readonly Vector3[] centres;
    private readonly double[,] weights;   // n × 3

    private ChestField(IReadOnlyList<Site> sites, double[,] weights, float yLow, float yClav, double condition, bool ridged,
                       int iterations, IReadOnlyDictionary<string, float> residual)
    {
        Sites = sites;
        centres = sites.Select(s => s.Centre).ToArray();
        this.weights = weights;
        YLow = yLow;
        YClav = yClav;
        Condition = condition;
        Ridged = ridged;
        Iterations = iterations;
        ResidualMm = residual;
    }

    internal static double Phi(double r) => r >= 1 ? 0 : Math.Pow(1 - r, 4) * (4 * r + 1);

    /// <summary>
    /// 1 inside the band between the lower row and the clavicle row, falling smoothly (C2) to 0 over the last
    /// <see cref="Fade"/> before each row and 0 beyond: the bumps overshoot just past a pin, and past the rows nothing
    /// is moved at all. The targets lie further inside than the fade.
    /// </summary>
    private float Band(float y)
    {
        if (y <= YLow || y >= YClav) return 0f;
        float s = Math.Min(Math.Min((y - YLow) / Fade, (YClav - y) / Fade), 1f);
        return s * s * s * (s * (s * 6f - 15f) + 10f);
    }

    /// <summary>How far the field moves the destination point <paramref name="p"/>.</summary>
    public Vector3 Displacement(Vector3 p)
    {
        float band = Band(p.Y);
        if (band == 0f) return Vector3.Zero;
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < centres.Length; i++)
        {
            double f = Phi(Vector3.Distance(p, centres[i]) / Radius);
            if (f == 0) continue;
            x += weights[i, 0] * f; y += weights[i, 1] * f; z += weights[i, 2] * f;
        }
        return new Vector3((float)x, (float)y, (float)z) * band;
    }

    public Vector3 Apply(Vector3 p) => p + Displacement(p);

    /// <summary>
    /// How far forward of the source's the destination's nipples may stand (1 cm). A field is cast from a flat chest
    /// onto a breast; cast from a breast onto a flat chest, along the breast's own turning normals or by nearness, its
    /// rays cross and the map folds — that map is the other one's inverse (<see cref="UvTransferBake.InvertInto"/>).
    /// </summary>
    internal const float MaxStandout = 0.01f;

    /// <summary>Where the destination point <paramref name="p"/> lands on <paramref name="src"/>: moved by the field, then cast.</summary>
    public UvTransferBake.Landing Land(TriangleBvh bvh, BakeBody src, Vector3 p, Vector3 n, Region region)
        => UvTransferBake.Land(bvh, src, Apply(p), n, region);

    /// <summary>Whether the field can move <paramref name="p"/> at all: within its band and within reach of a site.</summary>
    public bool InSupport(Vector3 p)
    {
        if (Band(p.Y) == 0f) return false;
        foreach (var c in centres) if (Vector3.Distance(p, c) < Radius) return true;
        return false;
    }

    /// <summary>
    /// The field that lands <paramref name="dest"/>'s nipples and creases on <paramref name="src"/>'s, or why none
    /// could be made. Deterministic: sites in a fixed order, a fixed number of iterations at most.
    /// </summary>
    internal static (ChestField? Field, string? Failure) Build(BakeBody dest, BakeBody src, TriangleBvh srcBvh)
    {
        var (dn, df) = UvBakeNipples.Find(dest);
        if (dn == null) return (null, "destination: " + df);
        var (sn, sf) = UvBakeNipples.Find(src);
        if (sn == null) return (null, "source: " + sf);
        float standout = (dn.Value.L.Pos.Z + dn.Value.R.Pos.Z - sn.Value.L.Pos.Z - sn.Value.R.Pos.Z) / 2;
        if (standout > MaxStandout)
            return (null, $"the destination's nipples stand {standout * 1000:F0} mm before the source's: cast the other way and invert");
        var destBvh = new TriangleBvh(dest);

        // Targets: each destination nipple and the source nipple it must land on. The creases, each with the source's,
        // only place the lower row.
        var targets = new List<(string Name, int Tri, Vector3 Bary, Vector3 Pos, Vector3 Want)>();
        var creases = new List<(string Name, int Tri, Vector3 Bary, Vector3 Pos, Vector3 Want)>();
        foreach (var (side, d, s) in new[] { ("L", dn.Value.L, sn.Value.L), ("R", dn.Value.R, sn.Value.R) })
        {
            targets.Add(("nipple" + side, d.Tri, d.Bary, d.Pos, s.Pos));
            var (dc, dcf) = UvBakeNipples.Crease(dest, d, destBvh);
            if (dc == null) return (null, $"destination {side}: {dcf}");
            var (sc, scf) = UvBakeNipples.Crease(src, s, srcBvh);
            if (sc == null) return (null, $"source {side}: {scf}");
            creases.Add(("crease" + side, dc.Value.Tri, dc.Value.Bary, dc.Value.Pos, sc.Value.Pos));
        }

        // Pins: the sternum, the clavicle row over each nipple, each flank at nipple height, a row across the chest
        // under the creases, and whichever far landmarks the bumps reach.
        var pins = new List<(string Name, Vector3 Pos)>();
        var marks = UvTransferQuality.Landmarks(dest);
        Vector3 Mark(string name) { var (t, b) = marks[name]; return dest.At(t, b.X, b.Y, b.Z).Pos; }
        if (!marks.ContainsKey("sternum")) return (null, "destination has no sternum landmark");
        pins.Add(("sternum", Mark("sternum")));
        float yClav = float.MinValue;
        foreach (var (side, nip) in new[] { ("L", dn.Value.L), ("R", dn.Value.R) })
        {
            if (BoneY(dest, "j_sako_" + side.ToLowerInvariant()) is not { } cy) return (null, $"destination has no j_sako_{side.ToLowerInvariant()} skin");
            if (Front(dest, destBvh, nip.Pos.X, cy) is not { } clav) return (null, $"no front skin at the {side} clavicle row");
            pins.Add(("clavicle" + side, clav));
            yClav = Math.Max(yClav, cy);
            if (Flank(dest, nip.Pos.Y, side == "L" ? 1f : -1f) is not { } flank) return (null, $"no {side} flank at nipple height");
            pins.Add(("flank" + side, flank));
        }
        // The lower row: on each side, the first height at least LowerRow under the destination's crease where the
        // plain projection has caught up with the source — the chest wall from crease to row lands on a stretch of the
        // source between half and twice its length — so the field can fall to nothing there without folding.
        float yLow = float.MaxValue;
        foreach (var crease in creases)
        {
            float? found = null;
            for (float below = LowerRow; below <= MaxLowerRow + 1e-4f; below += 0.005f)
            {
                float y = crease.Pos.Y - below;
                if (FrontHit(dest, destBvh, crease.Pos.X, y) is not { } hit) continue;
                var land = UvTransferBake.Land(srcBvh, src, hit.Pos, dest.SmoothAt(hit.Tri, hit.U, hit.V, hit.W),
                                               dest.RegionAt(hit.Tri, hit.U, hit.V, hit.W));
                if (!land.Found) continue;
                float ratio = (crease.Want.Y - src.At(land.Tri, land.U, land.V, land.W).Pos.Y) / below;
                if (ratio is >= 0.5f and <= 2f) { found = y; break; }
            }
            if (found == null) return (null, $"no lower row under {crease.Name}: the projection has not caught up {MaxLowerRow * 100:F0} cm down");
            yLow = Math.Min(yLow, found.Value);
        }
        // Across the front, and out to each flank: the waist there is too narrow for a fixed outer x.
        foreach (float x in new[] { 0f, 0.04f, -0.04f, 0.08f, -0.08f })
        {
            if (Front(dest, destBvh, x, yLow) is not { } low) return (null, $"no front skin on the lower pin row at x {x * 1000:F0} mm");
            pins.Add(($"low{x * 1000:+0;-0;0}", low));
        }
        foreach (var (side, sx) in new[] { ("L", 1f), ("R", -1f) })
        {
            if (Flank(dest, yLow, sx) is not { } flank) return (null, $"no {side} flank on the lower pin row");
            pins.Add(("lowFlank" + side, flank));
        }
        foreach (var far in new[] { "shoulderL", "shoulderR", "nape", "lowerBelly" })
            if (marks.ContainsKey(far) && targets.Any(t => Vector3.Distance(t.Pos, Mark(far)) < Radius))
                pins.Add((far, Mark(far)));
        foreach (var (name, pos) in pins)
            foreach (var t in targets)
                if (Vector3.Distance(pos, t.Pos) < PinClearance)
                    return (null, $"pin {name} too near {t.Name} ({Vector3.Distance(pos, t.Pos) * 1000:F1} mm)");

        int nt = targets.Count, n = nt + pins.Count;
        var all = targets.Select(t => t.Pos).Concat(pins.Select(p => p.Pos)).ToArray();
        var phi = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                phi[i, j] = Phi(Vector3.Distance(all[i], all[j]) / Radius);
        double condition = ConditionNumber(phi);
        bool ridged = condition > MaxCondition;
        if (ridged)
        {
            double trace = 0;
            for (int i = 0; i < n; i++) trace += phi[i, i];
            for (int i = 0; i < n; i++) phi[i, i] += Ridge * trace / n;
            condition = ConditionNumber(phi);
        }

        var normals = targets.Select(t => dest.SmoothAt(t.Tri, t.Bary.X, t.Bary.Y, t.Bary.Z)).ToArray();
        var regions = targets.Select(t => dest.RegionAt(t.Tri, t.Bary.X, t.Bary.Y, t.Bary.Z)).ToArray();
        // A ray decides only where across the skin a point lands: the offsets are solved across it.
        Vector3 Across(Vector3 d, Vector3 nrm) => d - nrm * Vector3.Dot(d, nrm);
        Vector3? Landed(Vector3 from, int k)
        {
            var land = UvTransferBake.Land(srcBvh, src, from, normals[k], regions[k]);
            return land.Found ? src.At(land.Tri, land.U, land.V, land.W).Pos : null;
        }

        var offsets = new Vector3[nt];
        for (int k = 0; k < nt; k++)
        {
            if (Landed(targets[k].Pos, k) is not { } first) return (null, $"{targets[k].Name} lands nowhere on the source");
            offsets[k] = Across(targets[k].Want - first, normals[k]);
        }
        ChestField? field = null;
        var residual = new Dictionary<string, float>(StringComparer.Ordinal);
        int iter;
        for (iter = 1; ; iter++)
        {
            var rhs = new double[n, 3];
            for (int k = 0; k < nt; k++) { rhs[k, 0] = offsets[k].X; rhs[k, 1] = offsets[k].Y; rhs[k, 2] = offsets[k].Z; }
            var w = Solve(phi, rhs);
            if (w == null) return (null, "the field's system is singular");
            var sites = targets.Select((t, k) => new Site(t.Name, t.Pos, offsets[k], false))
                               .Concat(pins.Select(p => new Site(p.Name, p.Pos, Vector3.Zero, true))).ToList();
            field = new ChestField(sites, w, yLow, yClav, condition, ridged, iter, residual);
            bool done = true;
            var step = new Vector3[nt];
            for (int k = 0; k < nt; k++)
            {
                if (Landed(field.Apply(targets[k].Pos), k) is not { } got) return (null, $"{targets[k].Name} lands nowhere once moved");
                step[k] = Across(targets[k].Want - got, normals[k]);
                residual[targets[k].Name] = step[k].Length() * 1000f;
                if (step[k].Length() > UvTransferQuality.Gate.FieldResidualMm / 1000f) done = false;
            }
            if (done) break;
            if (iter == MaxIterations)
                return (null, $"the offsets did not settle in {MaxIterations} passes: "
                              + string.Join(", ", residual.Select(r => $"{r.Key} {r.Value:F1} mm")));
            for (int k = 0; k < nt; k++) offsets[k] += step[k];
        }
        return (field, null);
    }

    /// <summary>The figures a field is judged by before any map is baked with it.</summary>
    internal sealed class Checks
    {
        public float MinJacobian = float.MaxValue, MaxPinMm, MaxOffTorsoMm, MaxMm, MaxReverseMm, MaxStillMm;
        public Vector3 ReverseAt;
        public int FoldedTriangles;
    }

    /// <summary>
    /// The field measured over the whole destination: the surface Jacobian of every triangle it reaches (in the
    /// triangle's own plane), every pin, every non-Torso vertex, and every vertex for overshoot — no further than
    /// 1.05 × the largest offset, no lobe pushing the wrong way, nothing moving on the sternum or along the clavicle row.
    /// </summary>
    internal Checks Measure(BakeBody dest)
    {
        var c = new Checks();
        foreach (var s in Sites) if (s.Pin) c.MaxPinMm = Math.Max(c.MaxPinMm, Displacement(s.Centre).Length() * 1000f);
        var frame = UvTransferQuality.Frame.Of(dest);
        var targets = Sites.Where(s => !s.Pin).ToList();
        var d = new Vector3[dest.P.Length];
        for (int v = 0; v < dest.P.Length; v++)
        {
            var p = dest.P[v];
            d[v] = Displacement(p);
            float mm = d[v].Length() * 1000f;
            if (mm == 0f) continue;
            c.MaxMm = Math.Max(c.MaxMm, mm);
            if (dest.VertexRegion[v] != Region.Torso) c.MaxOffTorsoMm = Math.Max(c.MaxOffTorsoMm, mm);
            // Against the targets' offsets blended by nearness, so the turn between the two nipples' is not one.
            var blend = Vector3.Zero;
            foreach (var t in targets) blend += t.Offset * (float)Phi(Vector3.Distance(t.Centre, p) / Radius);
            if (blend.LengthSquared() > 1e-12f)
            {
                float reverse = -Vector3.Dot(d[v], Vector3.Normalize(blend)) * 1000f;
                if (reverse > c.MaxReverseMm) { c.MaxReverseMm = reverse; c.ReverseAt = p; }
            }
            // Still: the clavicle row across the chest, and the sternum above it. The sternum region reaches down
            // between the nipples, where the chest must move with them.
            bool still = dest.N[v].Z > 0.3f && MathF.Abs(p.X) <= ClavicleBand
                         && (MathF.Abs(p.Y - YClav) < 0.005f || (p.Y >= YClav && frame.NamedRegion(p, dest.N[v]) == "sternum"));
            if (still) c.MaxStillMm = Math.Max(c.MaxStillMm, mm);
        }
        for (int t = 0; t < dest.TriangleCount; t++)
        {
            int a = dest.Tri[t * 3], b = dest.Tri[t * 3 + 1], e = dest.Tri[t * 3 + 2];
            if (d[a] == Vector3.Zero && d[b] == Vector3.Zero && d[e] == Vector3.Zero) continue;
            var e1 = dest.P[b] - dest.P[a];
            var e2 = dest.P[e] - dest.P[a];
            var nrm = dest.FaceNormal[t];
            if (nrm == Vector3.Zero || e1.LengthSquared() < 1e-14f) continue;
            var u1 = Vector3.Normalize(e1);
            var u2 = Vector3.Cross(nrm, u1);
            float det0 = Vector3.Dot(e1, u1) * Vector3.Dot(e2, u2) - Vector3.Dot(e2, u1) * Vector3.Dot(e1, u2);
            if (MathF.Abs(det0) < 1e-12f) continue;
            var w1 = e1 + d[b] - d[a];
            var w2 = e2 + d[e] - d[a];
            float det1 = Vector3.Dot(w1, u1) * Vector3.Dot(w2, u2) - Vector3.Dot(w2, u1) * Vector3.Dot(w1, u2);
            float j = det1 / det0;
            c.MinJacobian = Math.Min(c.MinJacobian, j);
            if (j < UvTransferQuality.Gate.FieldJacobianMin) c.FoldedTriangles++;
        }
        if (c.MinJacobian == float.MaxValue) c.MinJacobian = 1f;
        return c;
    }

    /// <summary>Every check a field must pass, each miss named.</summary>
    internal List<string> Check(Checks c)
    {
        var miss = new List<string>();
        float maxOffset = Sites.Where(s => !s.Pin).Max(s => s.Offset.Length()) * 1000f;
        if (c.MinJacobian < UvTransferQuality.Gate.FieldJacobianMin)
            miss.Add($"chest field folds: {c.FoldedTriangles} triangles under Jacobian {UvTransferQuality.Gate.FieldJacobianMin}, least {c.MinJacobian:F2}");
        if (c.MaxPinMm > UvTransferQuality.Gate.FieldPinMm) miss.Add($"chest field moves a pin {c.MaxPinMm:F2} mm");
        if (c.MaxOffTorsoMm > UvTransferQuality.Gate.FieldOffTorsoMm) miss.Add($"chest field moves arm or neck skin {c.MaxOffTorsoMm:F1} mm");
        if (c.MaxMm > maxOffset * UvTransferQuality.Gate.FieldOvershoot)
            miss.Add($"chest field overshoots: {c.MaxMm:F1} mm against offsets of {maxOffset:F1} mm");
        if (c.MaxReverseMm > UvTransferQuality.Gate.FieldReverseMm) miss.Add($"chest field pushes {c.MaxReverseMm:F1} mm the wrong way");
        if (c.MaxStillMm > UvTransferQuality.Gate.FieldStillMm) miss.Add($"chest field moves the sternum or clavicle row {c.MaxStillMm:F1} mm");
        if (Condition > MaxCondition) miss.Add($"chest field system ill-conditioned even ridged: {Condition:E1}");
        return miss;
    }

    /// <summary>
    /// The stretch the anatomy asks of this field: down each nipple's line, the bands between the clavicle row, the
    /// nipple and the lower row, and across between the nipples — each the source length between its two
    /// ends as the field lands them over the length as the plain projection does. Area is the worst band's vertical
    /// times the horizontal (or its inverse), anisotropy the worst ratio of the two.
    /// </summary>
    internal (double Area, double Aniso) Predict(BakeBody dest, BakeBody src, TriangleBvh srcBvh)
    {
        var destBvh = new TriangleBvh(dest);
        (Vector3 Plain, Vector3 Field)? Ends(float x, float y)
        {
            if (FrontHit(dest, destBvh, x, y) is not { } hit) return null;
            var n = dest.SmoothAt(hit.Tri, hit.U, hit.V, hit.W);
            var region = dest.RegionAt(hit.Tri, hit.U, hit.V, hit.W);
            var a = UvTransferBake.Land(srcBvh, src, hit.Pos, n, region);
            var b = Land(srcBvh, src, hit.Pos, n, region);
            if (!a.Found || !b.Found) return null;
            return (src.At(a.Tri, a.U, a.V, a.W).Pos, src.At(b.Tri, b.U, b.V, b.W).Pos);
        }
        double Ratio((Vector3 Plain, Vector3 Field) p, (Vector3 Plain, Vector3 Field) q)
            => Vector3.Distance(p.Field, q.Field) / Math.Max(Vector3.Distance(p.Plain, q.Plain), 1e-6f);

        var nipples = new Dictionary<string, (Vector3 Plain, Vector3 Field)>();
        var vertical = new List<double>();
        foreach (var side in new[] { "L", "R" })
        {
            var nipple = Sites.First(s => s.Name == "nipple" + side).Centre;
            var clav = Sites.First(s => s.Name == "clavicle" + side).Centre;
            var ends = new[] { clav.Y, nipple.Y, YLow + 0.001f }.Select(y => Ends(nipple.X, y)).ToList();
            if (ends.Any(e => e == null)) return (double.PositiveInfinity, double.PositiveInfinity);
            for (int k = 0; k + 1 < ends.Count; k++) vertical.Add(Ratio(ends[k]!.Value, ends[k + 1]!.Value));
            nipples[side] = ends[1]!.Value;
        }
        double h = Ratio(nipples["L"], nipples["R"]);
        double area = vertical.Max(s => Math.Max(s * h, 1 / (s * h)));
        double aniso = vertical.Max(s => Math.Max(s / h, h / s));
        return (area, aniso);
    }

    /// <summary>The foremost Torso skin at (<paramref name="x"/>, <paramref name="y"/>): a ray from well in front.</summary>
    internal static Vector3? Front(BakeBody body, TriangleBvh bvh, float x, float y) => FrontHit(body, bvh, x, y)?.Pos;

    private static (Vector3 Pos, int Tri, float U, float V, float W)? FrontHit(BakeBody body, TriangleBvh bvh, float x, float y)
    {
        if (!bvh.Ray(new Vector3(x, y, 0.5f), -Vector3.UnitZ, 0.5f, t => body.TriRegion[t] == Region.Torso,
                     out int tri, out float u, out float v, out float w, out _)) return null;
        return (body.At(tri, u, v, w).Pos, tri, u, v, w);
    }

    /// <summary>The outermost Torso skin within 5 mm of height <paramref name="y"/> facing to side <paramref name="sx"/>.</summary>
    private static Vector3? Flank(BakeBody body, float y, float sx)
    {
        Vector3? best = null;
        for (int v = 0; v < body.P.Length; v++)
        {
            var p = body.P[v];
            if (MathF.Abs(p.Y - y) > 0.005f || body.VertexRegion[v] != Region.Torso || body.N[v].X * sx <= 0.7f) continue;
            if (best == null || p.X * sx > best.Value.X * sx) best = p;
        }
        return best;
    }

    private static float? BoneY(BakeBody body, string bone)
    {
        float sum = 0;
        int n = 0;
        for (int v = 0; v < body.P.Length; v++)
        {
            string? top = null;
            float best = 0f;
            foreach (var (b, w) in body.Weights[v]) if (w > best) { best = w; top = b; }
            if (top == bone) { sum += body.P[v].Y; n++; }
        }
        return n == 0 ? null : sum / n;
    }

    /// <summary>A·X = B by Gaussian elimination with partial pivoting, rows in a fixed order; null when singular.</summary>
    internal static double[,]? Solve(double[,] a, double[,] b)
    {
        int n = a.GetLength(0), m = b.GetLength(1);
        var A = (double[,])a.Clone();
        var B = (double[,])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(A[r, col]) > Math.Abs(A[piv, col])) piv = r;
            if (Math.Abs(A[piv, col]) < 1e-15) return null;
            if (piv != col)
            {
                for (int k = 0; k < n; k++) (A[col, k], A[piv, k]) = (A[piv, k], A[col, k]);
                for (int k = 0; k < m; k++) (B[col, k], B[piv, k]) = (B[piv, k], B[col, k]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = A[r, col] / A[col, col];
                if (f == 0) continue;
                for (int k = col; k < n; k++) A[r, k] -= f * A[col, k];
                for (int k = 0; k < m; k++) B[r, k] -= f * B[col, k];
            }
        }
        var x = new double[n, m];
        for (int k = 0; k < m; k++)
            for (int r = n - 1; r >= 0; r--)
            {
                double s = B[r, k];
                for (int c = r + 1; c < n; c++) s -= A[r, c] * x[c, k];
                x[r, k] = s / A[r, r];
            }
        return x;
    }

    /// <summary>The 2-norm condition number of symmetric <paramref name="a"/>, from its eigenvalues (cyclic Jacobi).</summary>
    internal static double ConditionNumber(double[,] a)
    {
        int n = a.GetLength(0);
        var A = (double[,])a.Clone();
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int p = 0; p < n; p++) for (int q = p + 1; q < n; q++) off += A[p, q] * A[p, q];
            if (off < 1e-30) break;
            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(A[p, q]) < 1e-300) continue;
                    double theta = (A[q, q] - A[p, p]) / (2 * A[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = A[k, p], akq = A[k, q];
                        A[k, p] = c * akp - s * akq;
                        A[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = A[p, k], aqk = A[q, k];
                        A[p, k] = c * apk - s * aqk;
                        A[q, k] = s * apk + c * aqk;
                    }
                }
        }
        double lo = double.MaxValue, hi = 0;
        for (int i = 0; i < n; i++) { double e = Math.Abs(A[i, i]); lo = Math.Min(lo, e); hi = Math.Max(hi, e); }
        return lo == 0 ? double.PositiveInfinity : hi / lo;
    }
}
