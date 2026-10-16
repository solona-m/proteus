using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// How much further off the new chest than its author put it off the old one the standoff pass leaves cloth (3 mm):
    /// the gap is measured nearest-point on each body, and two bodies' surfaces are not the same shape.
    /// </summary>
    internal const float FloatSlack = 0.003f;

    /// <summary>
    /// The nearest the standoff pass brings cloth to the new chest (4 mm): past <see cref="TuckClear"/> and
    /// <see cref="Clearance"/> together, so the skin the tuck draws under it keeps its room.
    /// </summary>
    internal const float FloatFloor = 0.004f;

    /// <summary>
    /// How far the new chest may stand outside the old one where cloth lands on it and the standoff pass still bring it
    /// back (4 mm): where the body grew, cloth standing off is the bust's own drape. Measured over the back of the chest,
    /// where nothing grew, the same reading spreads from -5 to +4 mm between two bodies' tessellations.
    /// </summary>
    internal const float FloatGrowMost = 0.004f;

    /// <summary>How far from a body the standoff pass looks for it (60 mm).</summary>
    internal const float FloatReach = DeepGrowReach;

    /// <summary>How much more room than <see cref="TuckClear"/> a face the standoff pass moves keeps between its corners
    /// (0.5 mm).</summary>
    private const float FloatFaceMargin = 0.0005f;

    /// <summary>The steps a side of the grid a face the standoff pass moves is sampled on.</summary>
    private const int FaceGrid = 6;

    /// <summary>The chest pair's two surfaces and what the standoff pass reads off the new one — see
    /// <see cref="StandoffTest"/>.</summary>
    internal sealed record Standoff(BodySurface Source, BodySurface Target, float ApexY, Func<Vector3, bool> IsArm);

    /// <summary>
    /// The garment's own chest, before and after: the source chest the author drew on, the new one, the height of the new
    /// breast's apex — on each side, the breast vertex (weighted at least <see cref="BreastWeight"/> to the breast bones)
    /// standing furthest forward, a vertex within 10 mm of the middle counted on both sides; the lower of the two, so a
    /// sagging bust's apex is not missed — and which landings are the arm. Null when the chest's weights cannot be read
    /// or it has no breast.
    /// </summary>
    internal static Standoff? StandoffTest(IReadOnlyList<SlotPair> pairs, string? garmentSlot)
    {
        var chest = pairs.FirstOrDefault(p => p.Slot == garmentSlot && p.Swaps);
        if (chest.TargetModel == null || ModelSkinReader.Read(chest.TargetModel, null, null) is not { } skin
            || skin.VertexCount * 3 != chest.Target.Positions.Length
            || RegionTest([chest], IsArmBone, TuckArmShare) is not { } isArm) return null;

        var target = chest.Target;
        float left = float.NaN, right = float.NaN, leftZ = float.MinValue, rightZ = float.MinValue;
        for (int v = 0; v < skin.VertexCount; v++)
        {
            if (EndShareOf(skin, v, b => b.Contains("mune", StringComparison.Ordinal)) < BreastWeight) continue;
            float x = target.Positions[v * 3], y = target.Positions[v * 3 + 1], z = target.Positions[v * 3 + 2];
            if (x <= 0.01f && z > leftZ) { leftZ = z; left = y; }
            if (x >= -0.01f && z > rightZ) { rightZ = z; right = y; }
        }
        if (float.IsNaN(left) || float.IsNaN(right)) return null;

        var source = chest.Correspondence.Source;
        return new Standoff(new BodySurface(source, BodySurface.CellFor(MeanEdgeOf(source))),
                            new BodySurface(target, BodySurface.CellFor(MeanEdgeOf(target))), MathF.Min(left, right), isArm);
    }

    /// <summary>
    /// The cloth the refit carried further off the new chest than its author put it off the old one, brought back toward
    /// the author's own standoff.
    /// <para/>
    /// The game's tops sit close over the upper chest — Dirndl's Bodice's chemise 6-13 mm off the game's body — and the
    /// refit onto a body mod carried them off it: the transfer follows the body less far than the upper chest shrank, the
    /// push-out lifts already-clear neckline cloth with the cloth it clears out of the grown breast, and the rim lift adds
    /// to it. Measured on Neolithe's Almond XS: that chemise 23-33 mm off, standing away from the chest like a stiff collar
    /// in game, 26 points of the front neckline more than 5 mm further off than the author put them.
    /// <para/>
    /// Each cloth point that stands further off the new chest than <see cref="FloatSlack"/> past its authored standoff, both
    /// read nearest-point on their own chest, comes back along the new chest's normal to that standoff — never nearer than
    /// <see cref="FloatFloor"/>. Only above the new breast's apex, where the body did not grow (<see cref="FloatGrowMost"/>),
    /// off the arm, and for cloth the author put outside the body: below the apex standing off is a drape, and cloth the
    /// author buried has the clearance by design. Hard pieces, snapped cloth and the garment's skin stay — but skin welded
    /// to the cloth, which comes back with its rim. The pull keeps the
    /// push-out's slope to its neighbours, and is halved wherever it would turn a face over against the author's winding,
    /// bring the skin within <see cref="TuckClear"/> plus <see cref="FloatFaceMargin"/> of a face it moves anywhere across
    /// it, or take a point through other cloth — and given up where halving does not mend it. Positions only.
    /// </summary>
    /// <returns>How many points came back.</returns>
    /// <param name="worst">The furthest any came back.</param>
    /// <param name="held">How many a guard or the slope held short of where they would have come back to.</param>
    internal static int KeepStandoff(ModelParts garment, Sets sets, Standoff test, Vec3[] nodeDelta, bool[] snapped,
                                     bool[]? stay, out float worst, out int held)
    {
        worst = 0f;
        held = 0;
        // The cloth, and the garment's own skin welded to it: Dirndl's Bodice's chemise ends in a rim welded to the game's
        // turned-in skin lip, which the swap keeps (it lies off the body) — that rim, 7 mm further off than authored on
        // Almond XS, comes back with its lip, or the weld would open.
        var isCloth = new bool[sets.NodeCount];
        foreach (int n in sets.ClothNodes) isCloth[n] = true;
        foreach (var part in garment.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
                if (v >= 0 && v < sets.NodeOf.Length) isCloth[sets.NodeOf[v]] = true;
        }
        var movable = Enumerable.Range(0, sets.NodeCount).Where(n => isCloth[n]).ToArray();

        // Each eligible point's wish and its direction (toward the new chest), read where the passes above left it.
        var want = new float[sets.NodeCount];
        var dir = new Vector3[sets.NodeCount];
        foreach (int n in movable)
        {
            if (snapped[n] || stay?[n] == true) continue;
            var authored = ToVector(sets.NodeAt[n]);
            if (!test.Source.Nearest(authored, FloatReach, out var h0)) continue;
            float own = Vector3.Dot(authored - h0.Point, h0.Normal);
            if (own < 0f) continue;
            var p = Placed(sets, nodeDelta, n);
            if (!test.Target.Nearest(p, FloatReach, out var h) || h.Point.Y < test.ApexY || test.IsArm(h.Point)) continue;
            if (!test.Source.Nearest(h.Point, FloatReach, out var q)
                || Vector3.Dot(h.Point - q.Point, h.Normal) > FloatGrowMost) continue;
            float now = Vector3.Dot(p - h.Point, h.Normal);
            float to = MathF.Max(own + FloatSlack, FloatFloor);
            if (now <= to) continue;
            want[n] = now - to;
            dir[n] = -h.Normal;
        }
        if (Array.TrueForAll(want, w => w <= 0f)) return 0;

        var pull = (float[])want.Clone();
        float step = MathF.Max(sets.MeanEdge, 1e-6f) * PushSlope;
        Vector3 Trial(int n) => pull[n] > 0f ? Placed(sets, nodeDelta, n) + dir[n] * pull[n] : Placed(sets, nodeDelta, n);

        // The cloth faces, and the faces any pulled point touches.
        var tris = new List<(int A, int B, int C)>();
        for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
        {
            int va = sets.Tris[t], vb = sets.Tris[t + 1], vc = sets.Tris[t + 2];
            if (va < 0 || vb < 0 || vc < 0
                || va >= sets.NodeOf.Length || vb >= sets.NodeOf.Length || vc >= sets.NodeOf.Length) continue;
            int a = sets.NodeOf[va], b = sets.NodeOf[vb], c = sets.NodeOf[vc];
            if (a == b || b == c || c == a) continue;
            tris.Add((a, b, c));
        }
        var touched = tris.Where(f => want[f.A] > 0f || want[f.B] > 0f || want[f.C] > 0f).ToList();
        var clothTris = tris.Where(f => isCloth[f.A] && isCloth[f.B] && isCloth[f.C]).ToList();

        // Each touched face as the passes above left it: turned over against its author already, and how close the skin
        // came between its corners — neither this pass's doing.
        var foldedBefore = new HashSet<(int, int, int)>();
        var roomBefore = new Dictionary<(int, int, int), float>();
        foreach (var f in touched)
        {
            Vector3 a = Placed(sets, nodeDelta, f.A), b = Placed(sets, nodeDelta, f.B), c = Placed(sets, nodeDelta, f.C);
            if (TurnedAgainstAuthor(f, a, b, c)) foldedBefore.Add(f);
            roomBefore[f] = Room(a, b, c);
        }

        bool TurnedAgainstAuthor((int A, int B, int C) f, Vector3 a, Vector3 b, Vector3 c)
        {
            var was = ToVector(sets.NodeAt[f.A]);
            var n0 = Vector3.Cross(ToVector(sets.NodeAt[f.B]) - was, ToVector(sets.NodeAt[f.C]) - was);
            return n0.LengthSquared() > 1e-24f && Vector3.Dot(n0, Vector3.Cross(b - a, c - a)) <= 0f;
        }

        // The least room the new chest leaves a face across it: its edges' middles and a grid inside it, six steps a side.
        // The game's faces are coarse — 20-50 mm across a rounding chest — and its corners and middle say little of the
        // skin between them: sampled there alone, the Top of Maiming on neobelly M took 1.5 cm² of skin through its cloth.
        float Room(Vector3 a, Vector3 b, Vector3 c)
        {
            float least = float.MaxValue;
            for (int i = 0; i <= FaceGrid; i++)
                for (int j = 0; i + j <= FaceGrid; j++)
                {
                    if ((i == 0 && j == 0) || (i == FaceGrid && j == 0) || (i == 0 && j == FaceGrid)) continue;   // corners
                    var s = a + (b - a) * (i / (float)FaceGrid) + (c - a) * (j / (float)FaceGrid);
                    if (test.Target.Nearest(s, FloatReach, out var h))
                        least = MathF.Min(least, Vector3.Dot(s - h.Point, h.Normal));
                }
            return least;
        }

        bool FaceFails((int A, int B, int C) f)
        {
            Vector3 a = Trial(f.A), b = Trial(f.B), c = Trial(f.C);
            if (!foldedBefore.Contains(f) && TurnedAgainstAuthor(f, a, b, c)) return true;
            // The tuck's room kept where a face had it; where it was nearer already — beside cloth the clear held at the
            // clearance — kept clear of the skin, and never nearer than it was if it was nearer than that.
            float was = roomBefore[f];
            float least = was >= TuckClear + FloatFaceMargin ? TuckClear + FloatFaceMargin : MathF.Min(was, Clearance);
            return Room(a, b, c) < least;
        }

        bool Crosses(int n)
        {
            if (pull[n] <= 0f) return false;
            var p = Placed(sets, nodeDelta, n);
            var d = dir[n] * pull[n];
            var lo = Vector3.Min(p, p + d);
            var hi = Vector3.Max(p, p + d);
            foreach (var (a, b, c) in clothTris)
            {
                if (a == n || b == n || c == n) continue;
                Vector3 pa = Trial(a), pb = Trial(b), pc = Trial(c);
                var tlo = Vector3.Min(pa, Vector3.Min(pb, pc));
                var thi = Vector3.Max(pa, Vector3.Max(pb, pc));
                if (thi.X < lo.X || thi.Y < lo.Y || thi.Z < lo.Z || tlo.X > hi.X || tlo.Y > hi.Y || tlo.Z > hi.Z) continue;
                if (SegmentHits(p, d, pa, pb, pc)) return true;
            }
            return false;
        }

        // The slope: no point pulled further than a neighbour plus one step; cloth that is not pulled counts as none.
        void Slope()
        {
            for (int round = 0; round < PushSpreadRounds; round++)
            {
                bool changed = false;
                foreach (int n in movable)
                {
                    if (pull[n] <= 0f) continue;
                    float cap = pull[n];
                    foreach (int m in sets.Adj[n])
                        if (isCloth[m]) cap = MathF.Min(cap, pull[m] + step);
                    if (cap >= pull[n] - 1e-9f) continue;
                    pull[n] = cap;
                    changed = true;
                }
                if (!changed) break;
            }
        }

        // Never nearer the new chest than the floor, read where the pull would leave the point.
        bool Clamp()
        {
            bool changed = false;
            foreach (int n in movable)
            {
                if (pull[n] <= 0f) continue;
                var p = Trial(n);
                if (!test.Target.Nearest(p, FloatReach, out var h)) continue;
                float short_ = FloatFloor - Vector3.Dot(p - h.Point, h.Normal);
                if (short_ <= 1e-4f) continue;
                pull[n] = MathF.Max(0f, pull[n] - short_);
                changed = true;
            }
            return changed;
        }

        Slope();
        for (int pass = 0; pass <= PushUnfoldPasses; pass++)
        {
            bool giveUp = pass == PushUnfoldPasses;
            bool changed = Clamp();
            foreach (var f in touched)
            {
                if (!FaceFails(f)) continue;
                foreach (int m in new[] { f.A, f.B, f.C })
                    if (pull[m] > 0f) pull[m] = giveUp ? 0f : pull[m] * 0.5f;
                changed = true;
            }
            foreach (int n in movable)
                if (Crosses(n)) { pull[n] = 0f; changed = true; }
            if (!changed) break;
            Slope();
        }
        // What giving up left: corners zeroed one face at a time until no face this pass moved fails. Each round only
        // returns points to where the passes above left them, so it ends with every face as it was or better.
        for (bool failing = true; failing;)
        {
            failing = false;
            foreach (var f in touched)
            {
                if (!FaceFails(f)) continue;
                foreach (int m in new[] { f.A, f.B, f.C })
                    if (pull[m] > 0f) { pull[m] = 0f; failing = true; }
            }
        }

        int pulled = 0;
        foreach (int n in movable)
        {
            if (want[n] <= 0f) continue;
            if (pull[n] < want[n] - 1e-4f) held++;
            if (pull[n] <= 1e-5f) continue;
            var d = dir[n] * pull[n];
            nodeDelta[n] = new Vec3(nodeDelta[n].X + d.X, nodeDelta[n].Y + d.Y, nodeDelta[n].Z + d.Z);
            worst = MathF.Max(worst, pull[n]);
            pulled++;
        }
        return pulled;
    }
}
