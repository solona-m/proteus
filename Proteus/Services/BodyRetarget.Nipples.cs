using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>How far in front of the source skin the garment's cloth still covers it (30 mm).</summary>
    internal const float NippleCoverReach = 0.03f;

    /// <summary>How far behind the source skin cloth still covers it (10 mm): a nipple can already stand through the
    /// cloth on the body it was made on.</summary>
    internal const float NippleCoverBehind = PullBehind;

    /// <summary>How far from a target skin vertex the nearest source landing may be and still say what covers it
    /// (10 mm). Further, nothing is known and the vertex is left alone.</summary>
    internal const float NippleLandingMatch = 0.01f;

    /// <summary>How far a source landing may sit off the unsmoothed target and still take the smoothing's movement
    /// (5 mm): see <see cref="SmoothedCorrespondence"/>.</summary>
    internal const float NippleShiftReach = 0.005f;

    /// <summary>
    /// The least excess worth smoothing (0.5 mm): under it the two bodies' nipples are the same. Absolute, not a share of
    /// the target's prominence: the ruler reads the breast's own curve too (6 mm on a breast with no nipple), so a share
    /// of it says little about the nipple. One body mod's sizes read under it — Rue+ Yiggle M to L -0.5 mm, Neolithe
    /// Pushup XS to L -0.9 mm, measured on "This Old Thing".
    /// </summary>
    internal const float ExcessFloor = 5e-4f;

    /// <summary>How hard the refit smooths, before each side's share of it: all the way.</summary>
    internal const float RefitNippleStrength = 1f;

    /// <summary>How far off its excess a tip may come down (10%) before the side's share is corrected once.</summary>
    internal const float TipTolerance = 0.1f;

    /// <summary>How far from a located tip the vertex measured as the tip may be (3 mm).</summary>
    private const float TipReach = 0.003f;

    /// <summary>How close to the source skin a garment mesh must lie to be a copy of it rather than cloth over it
    /// (1 mm): an author's sculpted skin under a custom material sits 0.4-0.9 mm off.</summary>
    internal const float SkinCopyReach = 0.001f;

    /// <summary>
    /// The new chest body with the part of each covered nipple that stands prouder than the source body's smoothed off,
    /// put in place of the old one in every pair it is in: the cloth lands on it, the push-out measures against it and
    /// the swap draws it. One surface; a garment made on a featureless chest shows nothing of a nipple it never had.
    /// <para/>
    /// Only the <c>_top</c> pair carrying its body's file is touched, and only when the garment covers a nipple the new
    /// body has and the old one did not: the same body on both sides (a <c>--finish</c>), or a garment made on a body
    /// with nipples as proud, leaves the pair as it was. A failure leaves it as it was too — this never fails a refit.
    /// </summary>
    /// <param name="smoothed">Target vertices moved, in all.</param>
    internal static IReadOnlyList<SlotPair> SmoothCoveredNipples(ModelParts garment, IReadOnlyList<SlotPair> pairs,
                                                                 out int smoothed, Action<string>? log = null)
    {
        smoothed = 0;
        List<SlotPair>? replaced = null;
        for (int i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (pair.Slot != "_top" || pair.TargetModel == null) continue;
            SlotPair? done;
            int moved;
            try
            {
                done = SmoothPair(garment, pair, log, out moved);
            }
            catch (Exception ex)
            {
                log?.Invoke($"refit nipple smooth: {pair.Slot} left as it was — {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            if (done is not { } p) continue;
            (replaced ??= [.. pairs])[i] = p;
            smoothed += moved;
        }
        return replaced ?? pairs;
    }

    private static SlotPair? SmoothPair(ModelParts garment, SlotPair pair, Action<string>? log, out int moved)
    {
        moved = 0;
        var targetModel = pair.TargetModel!;
        if (pair.SourceModel != null && pair.SourceModel.AsSpan().SequenceEqual(targetModel))
        {
            log?.Invoke($"refit nipple smooth: {pair.Slot} is the same body on both sides — nothing to smooth");
            return null;
        }

        var covered = CoveredTargetSkin(garment, pair, out var landings);
        int coveredCount = covered.Count(c => c);
        if (coveredCount == 0)
        {
            log?.Invoke($"refit nipple smooth: {pair.Slot} — the garment covers none of the new body's skin");
            return null;
        }

        var target = pair.Target;
        var drawn = DrawnVertices(targetModel, pair);
        var targetSkin = new SkinIndex(target);
        var sourceSkin = new SkinIndex(pair.Correspondence.Source);
        var sides = new Dictionary<bool, (Vector3 Tip, float Excess, float Scale)>();
        var gate = new BodyBridge.BodySmoothGate(
            (m, vc, _) => SliceOf(covered, target, m, vc, false),
            drawn == null ? null : (m, vc) => SliceOf(drawn, target, m, vc, true),
            found =>
            {
                var (excess, scale) = ExcessScale(found, targetSkin, sourceSkin, landings, log);
                sides[found.Left] = (new Vector3(found.Tip.X, found.Tip.Y, found.Tip.Z), excess, scale);
                return scale;
            },
            FrameFromBust: true);

        var bytes = BodyBridge.SmoothBodyNipples(targetModel, gate, RefitNippleStrength, log);
        if (bytes == null)
        {
            log?.Invoke($"refit nipple smooth: {pair.Slot} left as it was — nothing on the new body was smoothed");
            return null;
        }
        var read = ModelPartReader.Read(bytes);
        if (read == null || read.Positions.Length != target.Positions.Length)
        {
            log?.Invoke($"refit nipple smooth: {pair.Slot} left as it was — the smoothed body did not read back the same");
            return null;
        }

        // The smoothing's movement is near enough linear in its strength, not exactly: a tip that came down more than
        // TipTolerance off its excess gets one step more, scaled by what the first run actually did there.
        var corrected = new Dictionary<bool, float>();
        foreach (var (left, (tip, excess, scale)) in sides)
        {
            if (scale <= 0f || excess <= 0f) continue;
            int v = -1;
            float best = float.MaxValue;
            foreach (int k in targetSkin.Within(tip, TipReach))
            {
                float d = Vector3.DistanceSquared(At(target, k), tip);
                if (d < best) { best = d; v = k; }
            }
            if (v < 0 || Normal(target, v).LengthSquared() < 1e-12f) continue;
            float drop = Vector3.Dot(At(target, v) - At(read, v), Vector3.Normalize(Normal(target, v)));
            if (drop <= 1e-5f || MathF.Abs(drop / excess - 1f) <= TipTolerance) continue;
            corrected[left] = Math.Clamp(scale * excess / drop, 0f, 1f);
            log?.Invoke($"refit nipple smooth: side {(left ? "L" : "R")} tip came down {drop * 1000:0.##} mm of "
                      + $"{excess * 1000:0.##} — scale {scale:0.###} -> {corrected[left]:0.###}");
        }
        if (corrected.Count > 0
            && BodyBridge.SmoothBodyNipples(targetModel,
                   gate with { SideScale = f => corrected.TryGetValue(f.Left, out float s) ? s : sides[f.Left].Scale },
                   RefitNippleStrength) is { } again
            && ModelPartReader.Read(again) is { } reread && reread.Positions.Length == target.Positions.Length)
        {
            bytes = again;
            read = reread;
        }

        var delta = new Vector3[target.Positions.Length / 3];
        for (int v = 0; v < delta.Length; v++)
        {
            delta[v] = At(read, v) - At(target, v);
            if (delta[v].LengthSquared() > 1e-12f) moved++;
        }
        if (moved == 0) return null;
        log?.Invoke($"refit nipple smooth: {pair.Slot} covered {coveredCount} of the new body's vertices, moved {moved}, "
                  + $"up to {delta.Max(d => d.Length()) * 1000:0.##} mm");
        return pair with
        {
            TargetModel = bytes,
            Target = Without(read, pair.TargetHidden),
            Correspondence = new SmoothedCorrespondence(pair.Correspondence, target, delta),
        };
    }

    /// <summary>Where a source skin vertex lands on the target, and whether the garment's cloth covers it there.</summary>
    internal readonly record struct Landing(Vector3 At, int Source, bool Covered);

    /// <summary>The landings, bucketed by position for the nearest one to a target point.</summary>
    internal sealed class Landings
    {
        private const float Cell = NippleLandingMatch;
        private readonly List<Landing> all = [];
        private readonly Dictionary<(int, int, int), List<int>> grid = [];

        public void Add(Landing landing)
        {
            var key = CellOf(landing.At);
            if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = [];
            bucket.Add(all.Count);
            all.Add(landing);
        }

        /// <summary>The landing nearest <paramref name="p"/> within <paramref name="reach"/> (at most one cell), or null.</summary>
        public Landing? Nearest(Vector3 p, float reach)
        {
            var (cx, cy, cz) = CellOf(p);
            Landing? best = null;
            float bestD = reach * reach;
            for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            for (int z = cz - 1; z <= cz + 1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int i in bucket)
                {
                    float d = Vector3.DistanceSquared(all[i].At, p);
                    if (d > bestD) continue;
                    bestD = d;
                    best = all[i];
                }
            }
            return best;
        }

        private static (int, int, int) CellOf(Vector3 p)
            => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));
    }

    /// <summary>
    /// Per vertex of the pair's target, whether the garment's cloth covers it: read on the SOURCE body, where the cloth
    /// sits as its author made it, along each skin vertex's normal from <see cref="NippleCoverBehind"/> behind to
    /// <see cref="NippleCoverReach"/> in front, and carried to the target by where that vertex lands. A target vertex
    /// takes its nearest landing's answer, or is uncovered with none within <see cref="NippleLandingMatch"/>.
    /// <para/>
    /// Cloth switched by an IMC variant counts: which option is drawn is the wearer's choice, and missing a panel is the
    /// poke-through this exists to stop. A garment mesh that is a copy of the source skin does not: it is skin, whatever
    /// its material is named.
    /// </summary>
    internal static bool[] CoveredTargetSkin(ModelParts garment, SlotPair pair, out Landings landings)
    {
        var source = pair.Correspondence.Source;
        var field = pair.Correspondence.Field;
        var cloth = new ClothCrossings(WithoutSkinCopies(garment, source), countVariants: true);
        landings = new Landings();
        var skin = new SkinIndex(source);
        foreach (int s in skin.Vertices)
        {
            if (s >= field.Count || field[s] is not { } d) continue;
            var p = At(source, s);
            var n = Normal(source, s);
            bool over = n.LengthSquared() > 1e-12f
                        && cloth.Over(p - Vector3.Normalize(n) * NippleCoverBehind, n, NippleCoverBehind + NippleCoverReach);
            landings.Add(new Landing(p + d, s, over));
        }

        var target = pair.Target;
        var covered = new bool[target.Positions.Length / 3];
        foreach (int t in new SkinIndex(target).Vertices)
            covered[t] = landings.Nearest(At(target, t), NippleLandingMatch) is { Covered: true };
        return covered;
    }

    /// <summary>The garment without its meshes that lie on the source skin (<see cref="OnBodyShare"/> of their vertices
    /// within <see cref="SkinCopyReach"/>): a copy of the body under another material name is skin, not cloth.</summary>
    private static ModelParts WithoutSkinCopies(ModelParts garment, ModelParts source)
    {
        var body = new BodySurface(source, BodySurface.CellFor(MeanEdgeOf(source)));
        if (body.IsEmpty) return garment;
        var copies = new HashSet<int>();
        foreach (var mesh in garment.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                          .GroupBy(p => p.Mesh))
        {
            var verts = mesh.SelectMany(p => p.Triangles).Distinct().ToList();
            int on = verts.Count(v => body.Nearest(At(garment, v), SkinCopyReach, out _));
            if (verts.Count > 0 && on >= verts.Count * OnBodyShare) copies.Add(mesh.Key);
        }
        if (copies.Count == 0) return garment;
        return new ModelParts
        {
            Positions = garment.Positions,
            Normals = garment.Normals,
            MeshSpans = garment.MeshSpans,
            Parts = garment.Parts.Where(p => !copies.Contains(p.Mesh)).ToList(),
            AttributeNames = garment.AttributeNames,
            Min = garment.Min,
            Max = garment.Max,
            ShatteredSubmeshes = garment.ShatteredSubmeshes,
        };
    }

    /// <summary>
    /// Per vertex of the target, false where ONLY parts its mod does not draw use it — null when every part is drawn. A
    /// vertex no triangle uses (a shape key's spare) counts as drawn: it was never measured as skin, and stays where the
    /// smoothing has always left it.
    /// </summary>
    private static bool[]? DrawnVertices(byte[] targetModel, SlotPair pair)
    {
        if (pair.TargetHidden is not { Count: > 0 }) return null;
        var full = ModelPartReader.Read(targetModel);
        if (full == null) return null;
        var drawnParts = new HashSet<(int, int)>(pair.Target.Parts.Where(p => p.Island < 0).Select(p => (p.Mesh, p.Submesh)));
        var count = full.Positions.Length / 3;
        var drawn = new bool[count];
        var hidden = new bool[count];
        foreach (var part in full.Parts.Where(p => p.Island < 0))
        {
            var mark = drawnParts.Contains((part.Mesh, part.Submesh)) ? drawn : hidden;
            foreach (int v in part.Triangles) if (v >= 0 && v < count) mark[v] = true;
        }
        var keep = new bool[count];
        bool any = false;
        for (int v = 0; v < count; v++)
        {
            keep[v] = drawn[v] || !hidden[v];
            any |= !keep[v];
        }
        return any ? keep : null;
    }

    /// <summary>
    /// The part of a per-vertex array that is the model's mesh <paramref name="mesh"/>, <paramref name="vertexCount"/>
    /// long; <paramref name="fallback"/> everywhere when the parts reader has no such mesh or counts it differently. Never
    /// null: to the body smoothing a null coverage means everything is covered.
    /// </summary>
    internal static bool[] SliceOf(bool[] all, ModelParts parts, int mesh, int vertexCount, bool fallback)
    {
        var slice = new bool[vertexCount];
        foreach (var span in parts.MeshSpans)
        {
            if (span.Mesh != mesh) continue;
            if (span.Count != vertexCount || span.BaseVertex + vertexCount > all.Length) break;
            Array.Copy(all, span.BaseVertex, slice, 0, vertexCount);
            return slice;
        }
        if (fallback) Array.Fill(slice, true);
        return slice;
    }

    /// <summary>
    /// The share of a located nipple the refit smooths: the part of it standing prouder than the source body's at the
    /// same spot, over the whole of it — none when that excess is under <see cref="ExcessFloor"/>. Both bodies are
    /// measured with one ruler (<see cref="ProminenceAt"/>), so two equal bodies compare equal; a source with no nipple
    /// there, or nothing landing near the tip, counts as flat. The smoothing's movement is near enough linear in its
    /// strength that the share lands the tip within a few tenths of a millimetre of the source's (Zayne onto LavaBod+
    /// Teardrop: 4.4 mm asked, 4.57 moved).
    /// </summary>
    private static (float Excess, float Scale) ExcessScale(BodyBridge.NippleFound found, SkinIndex target, SkinIndex source,
                                                           Landings landings, Action<string>? log)
    {
        var tip = new Vector3(found.Tip.X, found.Tip.Y, found.Tip.Z);
        float promT = ProminenceAt(target, tip, found.RingIn, found.RingOut);
        float promS = 0f;
        if (landings.Nearest(tip, NippleLandingMatch) is { } landing)
            promS = ProminenceAt(source, source.PositionOf(landing.Source), found.RingIn, found.RingOut);
        float excess = promT - promS;
        float scale = promT <= 0f || excess < ExcessFloor ? 0f : excess / promT;
        log?.Invoke($"refit nipple smooth: side {(found.Left ? "L" : "R")} tip ({tip.X:0.###},{tip.Y:0.###},{tip.Z:0.###}) "
                  + $"target prom {promT * 1000:0.##} mm, source prom {promS * 1000:0.##} mm, excess {excess * 1000:0.##} mm, "
                  + $"scale {scale:0.###}");
        return (excess, scale);
    }

    /// <summary>
    /// How proud the skin near <paramref name="at"/> stands: of the skin vertices within <paramref name="ringIn"/> of it,
    /// the most any stands above the ring of skin <paramref name="ringIn"/>..<paramref name="ringOut"/> around it, along
    /// its own normal — the nipple locator's measure. A vertex whose ring does not surround it (fewer than four samples,
    /// or a quarter of the circle empty) is not measured: the edge of the skin would read as proud. 0 when none is.
    /// </summary>
    internal static float ProminenceAt(SkinIndex skin, Vector3 at, float ringIn, float ringOut)
    {
        float best = 0f;
        foreach (int v in skin.Within(at, ringIn))
        {
            var p = skin.PositionOf(v);
            var n = skin.NormalOf(v);
            if (n.LengthSquared() < 1e-12f) continue;
            n = Vector3.Normalize(n);
            // A tangent frame, for "does the ring surround it".
            var t1 = Vector3.Normalize(MathF.Abs(n.Y) < 0.9f ? Vector3.Cross(n, Vector3.UnitY) : Vector3.Cross(n, Vector3.UnitX));
            var t2 = Vector3.Cross(n, t1);
            double sum = 0;
            int ring = 0, sides = 0;
            foreach (int q in skin.Within(p, ringOut))
            {
                var d = p - skin.PositionOf(q);
                float r = d.Length();
                if (r < ringIn || r > ringOut) continue;
                sum += Vector3.Dot(d, n);
                ring++;
                sides |= (Vector3.Dot(-d, t1) >= 0f ? 1 : 2) | (Vector3.Dot(-d, t2) >= 0f ? 4 : 8);
            }
            if (ring < 4 || sides != 15) continue;
            best = MathF.Max(best, (float)(sum / ring));
        }
        return best;
    }

    /// <summary>A body's drawn skin vertices, bucketed by position: the haystack the prominence ruler and the coverage
    /// read. Only skin parts, so a piercing on the nipple or the smallclothes over it never count as the breast.</summary>
    internal sealed class SkinIndex
    {
        private const float Cell = 0.01f;
        private readonly ModelParts body;
        private readonly Dictionary<(int, int, int), List<int>> grid = [];

        public IReadOnlyList<int> Vertices { get; }

        public SkinIndex(ModelParts body)
        {
            this.body = body;
            var skin = new SortedSet<int>();
            int count = body.Positions.Length / 3;
            foreach (var part in body.Parts)
            {
                if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                foreach (int v in part.Triangles) if (v >= 0 && v < count) skin.Add(v);
            }
            Vertices = [.. skin];
            foreach (int v in Vertices)
            {
                var key = CellOf(At(body, v));
                if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = [];
                bucket.Add(v);
            }
        }

        public Vector3 PositionOf(int v) => At(body, v);
        public Vector3 NormalOf(int v) => Normal(body, v);

        /// <summary>Skin vertices within <paramref name="radius"/> of <paramref name="p"/>.</summary>
        public IEnumerable<int> Within(Vector3 p, float radius)
        {
            var (x0, y0, z0) = CellOf(p - new Vector3(radius));
            var (x1, y1, z1) = CellOf(p + new Vector3(radius));
            float r2 = radius * radius;
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int v in bucket)
                    if (Vector3.DistanceSquared(At(body, v), p) <= r2) yield return v;
            }
        }

        private static (int, int, int) CellOf(Vector3 p)
            => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));
    }

    private static Vector3 Normal(ModelParts m, int v)
        => new(m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2]);
}
