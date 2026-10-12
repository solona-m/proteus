using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

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

    /// <summary>How close a garment vertex's texture coordinate must be to the source skin's under it (0.01 of the
    /// sheet) for the two to share a layout: a copy of the skin keeps the body's uvs, cloth painted on it does not.</summary>
    internal const float SkinCopyUv = 0.01f;

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
    /// <param name="swapSlot">See <see cref="Plan"/>: set, the refit is of a glove's or shoe's limb, which covers no
    /// chest, and nothing is smoothed.</param>
    /// <param name="garmentUv">The garment's uv0, in <see cref="ModelParts.Positions"/> order: tells a copy of the skin
    /// from cloth painted on it (see <see cref="WithoutSkinCopies"/>). Null judges by distance alone.</param>
    /// <param name="warn">Where a smoothing that FAILED says so — a bug, not a garment with nothing to smooth. Null
    /// falls back to <paramref name="log"/>.</param>
    internal static IReadOnlyList<SlotPair> SmoothCoveredNipples(ModelParts garment, IReadOnlyList<SlotPair> pairs,
                                                                 out int smoothed, Action<string>? log = null,
                                                                 string? swapSlot = null, float[]? garmentUv = null,
                                                                 Action<string>? warn = null)
    {
        smoothed = 0;
        if (swapSlot != null) return pairs;
        List<SlotPair>? replaced = null;
        for (int i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            // A ShapeOnly pair's skin is never drawn: there is no surface for the cloth to show a nipple through.
            if (pair.Slot != "_top" || pair.TargetModel == null || pair.ShapeOnly) continue;
            SlotPair? done;
            int moved;
            try
            {
                done = SmoothPair(garment, pair, garmentUv, log, warn ?? log, out moved);
            }
            catch (Exception ex)
            {
                (warn ?? log)?.Invoke($"refit nipple smooth: {pair.Slot} left as it was — {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            if (done is not { } p) continue;
            (replaced ??= [.. pairs])[i] = p;
            smoothed += moved;
        }
        return replaced ?? pairs;
    }

    /// <summary>One side of the chest as the excess gate read it: where its tip is, how much prouder than the source's
    /// it stands, and the share of the smoothing that asked for.</summary>
    private readonly record struct NippleSide(Vector3 Tip, float Excess, float Scale);

    private static SlotPair? SmoothPair(ModelParts garment, SlotPair pair, float[]? garmentUv, Action<string>? log,
                                        Action<string>? warn, out int moved)
    {
        moved = 0;
        var targetModel = pair.TargetModel!;
        if (pair.SourceModel != null && pair.SourceModel.AsSpan().SequenceEqual(targetModel))
        {
            log?.Invoke($"refit nipple smooth: {pair.Slot} is the same body on both sides — nothing to smooth");
            return null;
        }

        var covered = CoveredTargetSkin(garment, pair, out var landings, garmentUv);
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
        var sides = new Dictionary<bool, NippleSide>();
        var gate = new BodyBridge.BodySmoothGate(
            (m, vc, _) => SliceOf(covered, target, m, vc, false),
            drawn == null ? null : (m, vc) => SliceOf(drawn, target, m, vc, true),
            found =>
            {
                var (excess, scale) = ExcessScale(found, targetSkin, sourceSkin, landings, log);
                sides[found.Left] = new NippleSide(new Vector3(found.Tip.X, found.Tip.Y, found.Tip.Z), excess, scale);
                return scale;
            },
            FrameFromBust: true);

        var bytes = BodyBridge.SmoothBodyNipples(targetModel, gate, RefitNippleStrength, log);
        if (bytes == null)
        {
            log?.Invoke($"refit nipple smooth: {pair.Slot} left as it was — nothing on the new body was smoothed");
            return null;
        }
        var read = ReadBack(bytes, target);
        if (read == null)
        {
            warn?.Invoke($"refit nipple smooth: {pair.Slot} left as it was — the smoothed body did not read back the same");
            return null;
        }

        var corrected = CorrectSideScales(sides, target, read, targetSkin, log);
        if (corrected.Count > 0)
        {
            var again = BodyBridge.SmoothBodyNipples(targetModel,
                gate with { SideScale = f => corrected.TryGetValue(f.Left, out float s) ? s : sides[f.Left].Scale },
                RefitNippleStrength);
            if (again != null && ReadBack(again, target) is { } reread)
            {
                bytes = again;
                read = reread;
            }
            else
                warn?.Invoke($"refit nipple smooth: {pair.Slot} kept its first smoothing — the corrected one did not "
                           + "read back the same");
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

    /// <summary>The smoothed body read back, or null when it does not read or no longer counts its vertices the same.
    /// </summary>
    private static ModelParts? ReadBack(byte[] bytes, ModelParts target)
        => ModelPartReader.Read(bytes) is { } read && read.Positions.Length == target.Positions.Length ? read : null;

    /// <summary>
    /// Each side whose tip came down more than <see cref="TipTolerance"/> off its excess, with the share that would have
    /// landed it: the smoothing's movement is near enough linear in its strength, not exactly (LavaBod+ LaRue Teardrop's
    /// tip came down 8.2 mm for a 4.4 mm excess, Bibo+ Nude Large's 1.32 for 1.64), so one step on what the first run
    /// actually did there.
    /// </summary>
    private static Dictionary<bool, float> CorrectSideScales(Dictionary<bool, NippleSide> sides, ModelParts target,
                                                            ModelParts smoothed, SkinIndex targetSkin, Action<string>? log)
    {
        var corrected = new Dictionary<bool, float>();
        foreach (var (left, side) in sides)
        {
            if (side.Scale <= 0f || side.Excess <= 0f) continue;
            int v = -1;
            float best = float.MaxValue;
            foreach (int k in targetSkin.Within(side.Tip, TipReach))
            {
                float d = Vector3.DistanceSquared(At(target, k), side.Tip);
                if (d < best) { best = d; v = k; }
            }
            if (v < 0 || Normal(target, v).LengthSquared() < 1e-12f) continue;
            float drop = Vector3.Dot(At(target, v) - At(smoothed, v), Vector3.Normalize(Normal(target, v)));
            if (drop <= 1e-5f || MathF.Abs(drop / side.Excess - 1f) <= TipTolerance) continue;
            corrected[left] = Math.Clamp(side.Scale * side.Excess / drop, 0f, 1f);
            log?.Invoke($"refit nipple smooth: side {(left ? "L" : "R")} tip came down {drop * 1000:0.##} mm of "
                      + $"{side.Excess * 1000:0.##} — scale {side.Scale:0.###} -> {corrected[left]:0.###}");
        }
        return corrected;
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
    /// <param name="garmentUv">See <see cref="SmoothCoveredNipples"/>.</param>
    internal static bool[] CoveredTargetSkin(ModelParts garment, SlotPair pair, out Landings landings,
                                             float[]? garmentUv = null)
    {
        var source = pair.Correspondence.Source;
        var field = pair.Correspondence.Field;
        var sourceUv = pair.SourceModel != null ? Uv(pair.SourceModel) : null;
        var cloth = new ClothCrossings(WithoutSkinCopies(garment, source, garmentUv, sourceUv), countVariants: true);
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

    /// <summary>
    /// The garment without its meshes that are copies of the source skin under another material name: skin, not cloth.
    /// A copy lies on the body (<see cref="OnBodyShare"/> of its vertices within <see cref="SkinCopyReach"/>) AND keeps
    /// the body's texture layout there (within <see cref="SkinCopyUv"/>) — latex painted on within a millimetre has the
    /// garment's own uvs, and is cloth. With either model's uvs unreadable, distance alone decides.
    /// </summary>
    private static ModelParts WithoutSkinCopies(ModelParts garment, ModelParts source, float[]? garmentUv,
                                                float[]? sourceUv)
    {
        var body = new BodySurface(source, BodySurface.CellFor(MeanEdgeOf(source)));
        if (body.IsEmpty) return garment;
        bool uvs = garmentUv?.Length == garment.Positions.Length / 3 * 2 && sourceUv?.Length == source.Positions.Length / 3 * 2;
        bool IsCopy(int v)
        {
            if (!body.Nearest(At(garment, v), SkinCopyReach, out var hit)) return false;
            if (!uvs) return true;
            float u = sourceUv![hit.A * 2] * hit.U + sourceUv[hit.B * 2] * hit.V + sourceUv[hit.C * 2] * hit.W;
            float w = sourceUv[hit.A * 2 + 1] * hit.U + sourceUv[hit.B * 2 + 1] * hit.V + sourceUv[hit.C * 2 + 1] * hit.W;
            // Wrapped: a body may store its uvs a whole tile away, and the sheet repeats.
            float du = garmentUv![v * 2] - u, dw = garmentUv[v * 2 + 1] - w;
            du -= MathF.Round(du);
            dw -= MathF.Round(dw);
            return MathF.Abs(du) <= SkinCopyUv && MathF.Abs(dw) <= SkinCopyUv;
        }
        var copies = new HashSet<int>();
        foreach (var mesh in garment.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                          .GroupBy(p => p.Mesh))
        {
            var verts = mesh.SelectMany(p => p.Triangles).Distinct().ToList();
            if (verts.Count > 0 && verts.Count(IsCopy) >= verts.Count * OnBodyShare) copies.Add(mesh.Key);
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
    /// there, or nothing landing near the tip, counts as flat. <see cref="CorrectSideScales"/> refines the share on what
    /// it actually did.
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

    private static Vector3 Normal(ModelParts m, int v)
        => new(m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2]);
}
