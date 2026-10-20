using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>How much of each thigh skin must carry to be the crotch (10%) — see <see cref="FindCrotch"/>.</summary>
    internal const float CrotchAnchorThigh = 0.10f;

    /// <summary>How near a left-thigh and a right-thigh skin vertex must be to meet in a crotch (2.5 cm) — see
    /// <see cref="FindCrotch"/>. The Body SE's crotch is the hips' alone for 8 mm either side of the midline: its nearest
    /// pair, a thigh's tenth on each side, is 16.5 mm apart.</summary>
    internal const float CrotchSeam = 0.025f;

    /// <summary>How far over the skin, at most, a left-thigh and a right-thigh vertex meet in a crotch (3 cm): two thighs
    /// side by side below it are near each other but joined only through it.</summary>
    internal const float CrotchSeamPath = 0.03f;

    /// <summary>How much of each thigh cloth must carry to pass between the legs (5%) — see <see cref="EaseCrotch"/>.</summary>
    internal const float CrotchThighMin = 0.05f;

    /// <summary>How near the new skin cloth between the legs must lie to count (15 mm).</summary>
    internal const float CrotchSeedReach = 0.015f;

    /// <summary>How near the crotch the cloth between the legs must be, at the reference height (3 cm).</summary>
    internal const float CrotchAnchorReach = 0.03f;

    /// <summary>How far below the crotch thigh-rigged cloth makes a piece a leg (8 cm, at the reference height).</summary>
    internal const float CrotchLegDrop = 0.08f;

    /// <summary>How near a vetoed piece another must come to share its veto (5 mm).</summary>
    internal const float CrotchVetoReach = 0.005f;

    /// <summary>The ease is whole within this of the crotch's height (2 cm, at the reference height).</summary>
    internal const float CrotchFull = 0.02f;

    /// <summary>The ease is gone this far above the crotch (7 cm).</summary>
    internal const float CrotchFade = 0.07f;

    /// <summary>The ease is gone this far below the crotch (5 cm).</summary>
    internal const float CrotchBelowFade = 0.05f;

    /// <summary>The ease is whole within this of the midline (3 cm).</summary>
    internal const float CrotchSideFull = 0.03f;

    /// <summary>The ease is gone this far off the midline (6 cm).</summary>
    internal const float CrotchSideFade = 0.06f;

    /// <summary>The ease is gone this far behind the crotch (4 cm): the seat of a brief stays on the buttocks.</summary>
    internal const float CrotchBackFade = 0.04f;

    /// <summary>A thigh band reaches at least this far off the midline (12 cm, at the reference height).</summary>
    internal const float CrotchBandReach = 0.12f;

    /// <summary>The share of its leg weight cloth keeps where the ease is whole (10%): "almost away".</summary>
    internal const float CrotchLegKept = 0.1f;

    /// <summary>The crotch height every extent above is measured at (0.80 m): a Midlander woman's. Smaller and larger
    /// races scale with their own crotch's height.</summary>
    internal const float CrotchReferenceHeight = 0.80f;

    /// <summary>The thigh: <c>j_asi_a_*</c> and every body mod's thigh physics (<c>*daitai*</c>).</summary>
    internal static bool IsThighBone(string bone)
        => bone.StartsWith("j_asi_a_", StringComparison.Ordinal) || bone.Contains("daitai", StringComparison.Ordinal);

    /// <summary>The leg below the thigh: the knee, calf and foot (<c>j_asi_b..e_*</c>) and Rue's toes.</summary>
    internal static bool IsBelowThighBone(string bone) => IsLegBone(bone) && !IsThighBone(bone);

    /// <summary>Where the new body's crotch is, and how big the body is.</summary>
    /// <param name="Y">The crotch's height.</param>
    /// <param name="MidX">The body's midline.</param>
    /// <param name="Scale">The crotch's height over <see cref="CrotchReferenceHeight"/>, clamped to 0.4-1.5: what every
    /// extent of the ease is multiplied by.</param>
    /// <param name="At">The crotch point itself.</param>
    internal readonly record struct CrotchAnchor(float Y, float MidX, float Scale, Vector3 At);

    /// <summary>Each thigh's share of a set of influences: left, right.</summary>
    private static (float Left, float Right) Thighs(IEnumerable<(string Bone, float W)> influences)
    {
        float l = 0f, r = 0f;
        foreach (var (bone, w) in influences)
        {
            if (!IsThighBone(bone)) continue;
            int side = BoneSide(bone);
            if (side > 0) l += w;
            else if (side < 0) r += w;
        }
        return (l, r);
    }

    /// <summary>
    /// The new body's crotch: its lowest skin on the midline where the thighs meet — skin rigged at least
    /// <see cref="CrotchAnchorThigh"/> to each, or, on a body with none, skin rigged so to one joined over the skin to skin
    /// rigged so to the other (within <see cref="CrotchSeamPath"/> over it and <see cref="CrotchSeam"/> apart). The midline is where they meet, weighted by how evenly; the crotch point is the middle of
    /// the band of it within a centimetre of the lowest, front to back. Null when the thighs meet nowhere.
    /// <para/>
    /// Skin only: a body mod's genitals, pubic hair and piercings are other materials, sit at or below the perineum, and
    /// are rigged mostly to their own bones. A male pouch hanging below the crotch is one of them.
    /// </summary>
    internal static CrotchAnchor? FindCrotch(IReadOnlyList<(ModelParts Body, XivLiveMesh.SkinnedMesh Skin)> bodies)
    {
        // Per body: its skin welded by place, each place's share of each thigh, and which places its triangles join.
        var shared = new List<(Vector3 P, float Even)>();
        var welded = new List<(Vector3[] At, float[] L, float[] R, HashSet<int>[] Next)>();
        foreach (var (body, skin) in bodies)
        {
            int vc = Math.Min(body.Positions.Length / 3, skin.VertexCount);
            var nodeOf = new int[vc];
            Array.Fill(nodeOf, -1);
            var byPlace = new Dictionary<(int, int, int), int>();
            var at = new List<Vector3>();
            var l = new List<float>();
            var r = new List<float>();
            var skinParts = body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
            foreach (var part in skinParts)
                foreach (int v in part.Triangles)
                {
                    if (v < 0 || v >= vc || nodeOf[v] >= 0) continue;
                    var p = new Vector3(body.Positions[v * 3], body.Positions[v * 3 + 1], body.Positions[v * 3 + 2]);
                    var key = ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));
                    if (!byPlace.TryGetValue(key, out int node))
                    {
                        byPlace[key] = node = at.Count;
                        at.Add(p);
                        l.Add(0f);
                        r.Add(0f);
                    }
                    nodeOf[v] = node;
                    var (tl, tr) = Thighs(Influences(skin, v));
                    l[node] = MathF.Max(l[node], tl);
                    r[node] = MathF.Max(r[node], tr);
                }
            var next = new HashSet<int>[at.Count];
            for (int n = 0; n < next.Length; n++) next[n] = [];
            foreach (var part in skinParts)
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int a = part.Triangles[t + e], b = part.Triangles[t + (e + 1) % 3];
                        if (a < 0 || b < 0 || a >= vc || b >= vc || nodeOf[a] < 0 || nodeOf[b] < 0 || nodeOf[a] == nodeOf[b]) continue;
                        next[nodeOf[a]].Add(nodeOf[b]);
                        next[nodeOf[b]].Add(nodeOf[a]);
                    }
            for (int n = 0; n < at.Count; n++)
                if (l[n] >= CrotchAnchorThigh && r[n] >= CrotchAnchorThigh) shared.Add((at[n], MathF.Min(l[n], r[n])));
            welded.Add((at.ToArray(), l.ToArray(), r.ToArray(), next));
        }

        // Where the two meet: skin on both. Only on a body with none, skin on one thigh joined over the skin, within
        // CrotchSeamPath and CrotchSeam, to skin on the other: The Body SE splits its crotch down the middle — no
        // vertex carries more than 3% of both thighs, the middle is the hips' — and a gusset on it is pulled apart from
        // its edges all the same. Joined over the skin, so two thighs side by side below the crotch, two surfaces, never
        // meet. Not both ways at once: on a body that shares its crotch, the inner thigh beside it would pull it down.
        if (shared.Count > 0) return AnchorOn(shared);
        var candidates = new List<(Vector3 P, float Even)>();
        foreach (var (at, l, r, next) in welded)
            for (int n = 0; n < at.Length; n++)
            {
                if (l[n] < CrotchAnchorThigh) continue;
                // Over the skin, nearest first, as far as CrotchSeamPath.
                var far = new Dictionary<int, float> { [n] = 0f };
                var queue = new PriorityQueue<int, float>();
                queue.Enqueue(n, 0f);
                int best = -1;
                float bestDist = CrotchSeam;
                while (queue.TryDequeue(out int m, out float walked))
                {
                    if (walked > far[m]) continue;
                    float d = Vector3.Distance(at[n], at[m]);
                    if (m != n && r[m] >= CrotchAnchorThigh && d <= bestDist) { bestDist = d; best = m; }
                    foreach (int o in next[m])
                    {
                        float step = walked + Vector3.Distance(at[m], at[o]);
                        if (step > CrotchSeamPath || step >= far.GetValueOrDefault(o, float.MaxValue)) continue;
                        far[o] = step;
                        queue.Enqueue(o, step);
                    }
                }
                // The crotch is no lower than the higher of the two: skin on one thigh beside it can lie a ring below.
                if (best >= 0)
                {
                    var mid = (at[n] + at[best]) / 2f;
                    candidates.Add((mid with { Y = MathF.Max(at[n].Y, at[best].Y) }, MathF.Min(l[n], r[best])));
                }
            }
        return candidates.Count == 0 ? null : AnchorOn(candidates);
    }

    /// <summary>The crotch among the places the thighs meet, each weighted by how evenly — see <see cref="FindCrotch"/>.</summary>
    private static CrotchAnchor? AnchorOn(List<(Vector3 P, float Even)> candidates)
    {
        float total = candidates.Sum(c => c.Even);
        float mid = candidates.Sum(c => c.P.X * c.Even) / total;
        var onMid = candidates.Where(c => MathF.Abs(c.P.X - mid) <= 0.015f).Select(c => c.P).ToList();
        if (onMid.Count == 0) return null;
        float low = onMid.Min(p => p.Y);
        var band = onMid.Where(p => p.Y <= low + 0.01f).ToList();
        var at = new Vector3(mid, low, band.Average(p => p.Z));
        return new CrotchAnchor(low, mid, Math.Clamp(low / CrotchReferenceHeight, 0.4f, 1.5f), at);
    }

    /// <summary>Whole within <paramref name="full"/>, gone at <paramref name="fade"/>, linear between.</summary>
    private static float Ramp(float distance, float full, float fade)
        => distance <= full ? 1f : distance >= fade ? 0f : (fade - distance) / (fade - full);

    /// <summary>
    /// How much of its leg weight a cloth point at <paramref name="p"/> loses, of the most it can: one smooth box about
    /// the crotch — whole within <see cref="CrotchFull"/> of its height and <see cref="CrotchSideFull"/> of the midline,
    /// gone by <see cref="CrotchFade"/> above, <see cref="CrotchBelowFade"/> below, <see cref="CrotchSideFade"/> to the
    /// side and <see cref="CrotchBackFade"/> behind, every extent scaled to the body. A point and its seam twins lie in
    /// one place and so ease alike.
    /// </summary>
    internal static float CrotchStrength(Vector3 p, CrotchAnchor a)
    {
        float k = a.Scale;
        float dy = p.Y - a.Y;
        float h = dy >= 0f ? Ramp(dy, CrotchFull * k, CrotchFade * k) : Ramp(-dy, CrotchFull * k, CrotchBelowFade * k);
        if (h <= 0f) return 0f;
        float w = Ramp(MathF.Abs(p.X - a.MidX), CrotchSideFull * k, CrotchSideFade * k);
        if (w <= 0f) return 0f;
        // The characters face +Z: behind the crotch is the seat, over buttocks that follow the thighs.
        float b = Ramp(MathF.Max(0f, a.At.Z - p.Z), 0f, CrotchBackFade * k);
        return h * w * b;
    }

    /// <summary>
    /// One vertex's influences with its legs eased off by <paramref name="strength"/>: every leg bone scaled by
    /// <c>1 - (1 - <see cref="CrotchLegKept"/>) * strength</c>, and what they lose given to the hips (<c>j_kosi</c>) —
    /// not to the vertex's other body bones in proportion, which would feed the buttocks' and genitals' physics and set
    /// the cloth swinging. A rig without <c>j_kosi</c> shares it over the vertex's other body bones after all. The
    /// garment's own bones are kept exactly.
    /// <para/>
    /// No more influences come out than went in, or four: over that, the smallest body influences go, a left one with its
    /// right twin when they tie, so a gusset never comes out leaning to the leg the writer happened to keep.
    /// </summary>
    /// <returns>Null when there is nothing to ease, or nothing to give the legs' weight to.</returns>
    internal static (string Bone, float W)[]? EaseLegs(IReadOnlyList<(string Bone, float W)> now, float strength,
                                                       IReadOnlySet<string> bodyBones)
    {
        if (strength <= 0f) return null;
        float legs = now.Where(i => IsLegBone(i.Bone)).Sum(i => i.W);
        if (legs <= 1e-6f) return null;
        float f = 1f - (1f - CrotchLegKept) * Math.Clamp(strength, 0f, 1f);
        float give = legs * (1f - f);

        var w = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (bone, weight) in now) w[bone] = w.GetValueOrDefault(bone) + (IsLegBone(bone) ? weight * f : weight);
        if (bodyBones.Contains("j_kosi"))
            w["j_kosi"] = w.GetValueOrDefault("j_kosi") + give;
        else
        {
            var others = w.Where(kv => bodyBones.Contains(kv.Key) && !IsLegBone(kv.Key)).ToList();
            float have = others.Sum(kv => kv.Value);
            if (have <= 1e-6f) return null;
            foreach (var (bone, weight) in others) w[bone] = weight + give * weight / have;
        }

        var list = w.Where(kv => kv.Value > 1e-4f)
                    .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => (Bone: kv.Key, W: kv.Value)).ToList();
        int limit = Math.Min(MaxInfluences, Math.Max(4, now.Count));
        while (list.Count > limit)
        {
            int drop = list.FindLastIndex(i => bodyBones.Contains(i.Bone));
            if (drop < 0) break;
            var (bone, weight) = list[drop];
            list.RemoveAt(drop);
            string twin = BoneSide(bone) > 0 ? bone[..^2] + "_r" : BoneSide(bone) < 0 ? bone[..^2] + "_l" : "";
            int pair = list.FindIndex(i => i.Bone == twin && MathF.Abs(i.W - weight) <= 1e-4f);
            if (pair >= 0) list.RemoveAt(pair);
        }

        // The body share back to its size, the garment's own bones exactly as they were.
        float total = now.Sum(i => i.W), ownShare = list.Where(i => !bodyBones.Contains(i.Bone)).Sum(i => i.W);
        var body = Normalised(list.Where(i => bodyBones.Contains(i.Bone)).ToList(), total - ownShare);
        return [.. list.Where(i => !bodyBones.Contains(i.Bone)), .. body];
    }

    /// <summary>What <see cref="EaseCrotch"/> did.</summary>
    /// <param name="Eased">Cloth vertices whose legs were eased.</param>
    /// <param name="Added">Of those, vertices the reading had left as the author made them.</param>
    /// <param name="Vetoed">Pieces the field reaches left on the legs, because they go on down one.</param>
    /// <param name="Notes">For the log: where the crotch is, and every piece left on the legs and why.</param>
    internal sealed record CrotchOutcome(int Eased, int Added, int Vetoed, IReadOnlyList<string> Notes);

    /// <summary>
    /// Ease the legs' weights almost off cloth that passes between them — panties, a thong, a leotard's crotch — so that
    /// spreading the legs no longer pulls the cloth with them, each side after its own thigh.
    /// <para/>
    /// The garment passes between the legs when cloth its author rigged to the legs lies on the new skin (within
    /// <see cref="CrotchSeedReach"/>) at the new body's crotch (within <see cref="CrotchAnchorReach"/> of
    /// <see cref="FindCrotch"/>'s point), rigged to each thigh there (at least <see cref="CrotchThighMin"/>) — the same
    /// vertex or two. Then every piece of its cloth — welded vertices joined over its triangles — eases in one field about
    /// that point (<see cref="CrotchStrength"/>), the plates and links riding on a thong's strip with it; a piece the user
    /// marked to keep its shape takes the strength at its middle, whole.
    /// <para/>
    /// A piece that goes on down a leg keeps its legs: cloth rigged mostly to a thigh more than <see cref="CrotchLegDrop"/>
    /// below the crotch (a pant leg, long shorts, a garter strap), any bone below the thigh, a piece rigged almost all to
    /// one leg reaching the outer thigh (a thigh band), or cloth that wraps a thigh all the way round (boyshorts). A piece
    /// touching one of those within the field shares its fate, so a pant's crotch insert stays with its legs.
    /// <para/>
    /// The one deliberate exception to the skin hug (<see cref="FollowBodyNearSkin"/>), which rigs cloth lying on the skin
    /// as the skin so the two move together: here the cloth follows the pelvis while the skin beneath it follows the
    /// legs, and with the legs spread wide some crotch skin may show at the gusset's edges. Chosen over cloth that
    /// stretches with the thighs; the skin is not eased and no clearance is attempted. Behind the crotch the ease fades
    /// out (<see cref="CrotchBackFade"/>), so the seat of a brief stays on the buttocks it sits over.
    /// </summary>
    /// <param name="result">New weights per vertex, null where the author's stand. Updated in place.</param>
    /// <param name="keepShape">The pieces kept whole, as vertex sets; null for none.</param>
    private static CrotchOutcome EaseCrotch(ModelParts model, XivLiveMesh.SkinnedMesh own, IReadOnlyList<SlotPair> pairs,
                                            List<(BodySurface Surface, XivLiveMesh.SkinnedMesh Skin)> targets,
                                            (string Bone, float W)[]?[] result, IReadOnlySet<string> bodyBones,
                                            IReadOnlySet<int>? held, (string Bone, float W)[]?[] legFill,
                                            IReadOnlyList<IReadOnlyCollection<int>>? keepShape)
    {
        var notes = new List<string>();
        if (FindCrotch([.. pairs.Select((p, i) => (p.Target, targets[i].Skin))]) is not { } a)
        {
            notes.Add("no crotch to anchor on");
            return new CrotchOutcome(0, 0, 0, notes);
        }
        float k = a.Scale;

        int vc = model.Positions.Length / 3;
        Vector3 At(int v) => new(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
        List<(string Bone, float W)> Now(int v) => result[v] is { } w ? [.. w] : Influences(own, v);
        bool Free(int v) => held == null || !held.Contains(v);

        // Cloth on the skin at the crotch rigged to each thigh — one vertex shared between them, or one on each side of a
        // body that splits its crotch down the middle — as it is now or as its author rigged it.
        var cloth = ClothVertices(model);
        bool onLeft = false, onRight = false;
        foreach (int v in cloth)
        {
            if (!Free(v) || legFill[v] != null || Vector3.Distance(At(v), a.At) > CrotchAnchorReach * k) continue;
            var (nl, nr) = Thighs(Now(v));
            var (al, ar) = Thighs(Influences(own, v));
            bool l = MathF.Max(nl, al) >= CrotchThighMin, r = MathF.Max(nr, ar) >= CrotchThighMin;
            if ((!l || onLeft) && (!r || onRight)) continue;
            if (NearestAny(targets, At(v), 0, out float d) == null || d > CrotchSeedReach) continue;
            onLeft |= l;
            onRight |= r;
            if (onLeft && onRight) break;
        }
        if (!onLeft || !onRight) return new CrotchOutcome(0, 0, 0, notes);
        notes.Add($"anchored at {a.Y * 1000f:0} mm (x {a.MidX * 1000f:0}); scale {k:0.00}");

        // Pieces: one node per place, as SmoothWeights welds them, joined over the cloth's triangles.
        var piece = new int[vc];
        Array.Fill(piece, -1);
        var byPlace = new Dictionary<(int, int, int), int>();
        var parent = new List<int>();
        int Find(int n) { while (parent[n] != n) n = parent[n] = parent[parent[n]]; return n; }
        var nodeOf = new int[vc];
        Array.Fill(nodeOf, -1);
        foreach (int v in cloth)
        {
            var key = ((int)MathF.Round(model.Positions[v * 3] * 1e5f), (int)MathF.Round(model.Positions[v * 3 + 1] * 1e5f),
                       (int)MathF.Round(model.Positions[v * 3 + 2] * 1e5f));
            if (!byPlace.TryGetValue(key, out int node))
            {
                byPlace[key] = node = parent.Count;
                parent.Add(node);
            }
            nodeOf[v] = node;
        }
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int u = part.Triangles[t + e], w = part.Triangles[t + (e + 1) % 3];
                    if (u < 0 || w < 0 || u >= vc || w >= vc || nodeOf[u] < 0 || nodeOf[w] < 0) continue;
                    int ru = Find(nodeOf[u]), rw = Find(nodeOf[w]);
                    if (ru != rw) parent[ru] = rw;
                }
        }
        var members = new Dictionary<int, List<int>>();
        foreach (int v in cloth)
        {
            piece[v] = Find(nodeOf[v]);
            if (!members.TryGetValue(piece[v], out var list)) members[piece[v]] = list = [];
            list.Add(v);
        }

        // Which pieces go on down a leg, and why.
        var vetoed = new Dictionary<int, string>();
        foreach (var (id, verts) in members)
        {
            if (LegPiece(verts, a, Now, legFill, At, bodyBones) is { } why) vetoed[id] = why;
        }

        // One hop: a piece touching a vetoed one where the field reaches goes with it.
        if (vetoed.Count > 0)
        {
            (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / CrotchVetoReach), (int)MathF.Floor(p.Y / CrotchVetoReach),
                                                (int)MathF.Floor(p.Z / CrotchVetoReach));
            var grid = new Dictionary<(int, int, int), List<int>>();
            foreach (var id in vetoed.Keys)
                foreach (int v in members[id])
                {
                    var c = Cell(At(v));
                    if (!grid.TryGetValue(c, out var bucket)) grid[c] = bucket = [];
                    bucket.Add(v);
                }
            var spread = new Dictionary<int, string>();
            foreach (var (id, verts) in members)
            {
                if (vetoed.ContainsKey(id)) continue;
                foreach (int v in verts)
                {
                    var p = At(v);
                    if (CrotchStrength(p, a) <= 0f) continue;
                    var (cx, cy, cz) = Cell(p);
                    int touched = -1;
                    for (int x = cx - 1; x <= cx + 1 && touched < 0; x++)
                    for (int y = cy - 1; y <= cy + 1 && touched < 0; y++)
                    for (int z = cz - 1; z <= cz + 1 && touched < 0; z++)
                        if (grid.TryGetValue((x, y, z), out var bucket))
                            foreach (int u in bucket)
                                if (Vector3.DistanceSquared(p, At(u)) <= CrotchVetoReach * CrotchVetoReach) { touched = u; break; }
                    if (touched < 0) continue;
                    spread[id] = $"touches a piece left on the legs at {(p.Y - a.Y) * 1000f:0} mm from the crotch, "
                                 + $"{MathF.Abs(p.X - a.MidX) * 1000f:0} mm sideways";
                    break;
                }
            }
            foreach (var (id, why) in spread) vetoed[id] = why;
        }
        // Said only of the pieces the field reaches: a pant leg far below it was never going to ease.
        var reached = vetoed.Keys.Where(id => members[id].Any(v => CrotchStrength(At(v), a) > 0f)).ToList();
        foreach (var id in reached)
            notes.Add($"piece {id} ({members[id].Count} pts) left on the legs: {vetoed[id]}");

        // A piece kept whole eases whole, by the strength at its middle.
        var whole = new Dictionary<int, float>();
        if (keepShape != null)
            foreach (var kept in keepShape)
            {
                var inside = kept.Where(v => v >= 0 && v < vc).ToList();
                if (inside.Count == 0) continue;
                var mid = inside.Aggregate(Vector3.Zero, (s, v) => s + At(v)) / inside.Count;
                float s = CrotchStrength(mid, a);
                foreach (int v in inside) whole[v] = s;
            }

        int eased = 0, added = 0;
        foreach (int v in cloth)
        {
            if (!Free(v) || legFill[v] != null || vetoed.ContainsKey(piece[v])) continue;
            float s = whole.TryGetValue(v, out float ws) ? ws : CrotchStrength(At(v), a);
            if (s <= 0f || EaseLegs(Now(v), s, bodyBones) is not { } w) continue;
            if (result[v] == null) added++;
            result[v] = w;
            eased++;
        }
        return new CrotchOutcome(eased, added, reached.Count, notes);
    }

    /// <summary>
    /// Why a piece of cloth between the legs goes on down one — see <see cref="EaseCrotch"/> — or null when it does not.
    /// Read off its weights as the reading left them, and the author's leg rigging.
    /// </summary>
    private static string? LegPiece(List<int> verts, CrotchAnchor a, Func<int, List<(string Bone, float W)>> now,
                                    (string Bone, float W)[]?[] legFill, Func<int, Vector3> at, IReadOnlySet<string> bodyBones)
    {
        float k = a.Scale;
        string Place(Vector3 p) => $"{(p.Y - a.Y) * 1000f:0} mm from the crotch, {MathF.Abs(p.X - a.MidX) * 1000f:0} mm sideways";

        float left = 0f, right = 0f, reach = 0f, bodyTotal = 0f;
        Vector3 reachAt = default;
        var wrap = new List<(Vector3 P, int Side)>();
        foreach (int v in verts)
        {
            var w = now(v);
            var p = at(v);
            if (w.FirstOrDefault(i => IsBelowThighBone(i.Bone) && i.W >= CrotchThighMin) is { Bone: not null } below)
                return $"rigged to {below.Bone} at {Place(p)}";
            if (legFill[v] != null) continue;   // a skirt welded on hangs from the hips, wherever it hangs

            var (l, r) = Thighs(w);
            float body = w.Where(i => bodyBones.Contains(i.Bone)).Sum(i => i.W);
            bool onThigh = body > 1e-6f && l + r >= 0.5f * body;
            if (onThigh && p.Y < a.Y - CrotchLegDrop * k) return $"down the thigh at {Place(p)}";
            left += l;
            right += r;
            bodyTotal += body;
            float off = MathF.Abs(p.X - a.MidX);
            if (l + r > 1e-4f && off > reach) { reach = off; reachAt = p; }
            if (onThigh && p.Y < a.Y - CrotchFull * k && MathF.Max(l, r) >= 0.7f * (l + r))
                wrap.Add((p, l > r ? 1 : -1));
        }

        float legs = left + right;
        // Mostly leg, almost all of it one leg's, and out on the outer thigh: a band round it. A gusset's side plate leans
        // to one leg too, but is mostly hip.
        if (legs >= 0.5f * bodyTotal && legs > 1e-4f && MathF.Max(left, right) >= 0.9f * legs && reach > CrotchBandReach * k)
            return $"around one thigh at {Place(reachAt)}";

        foreach (var side in wrap.GroupBy(i => i.Side))
        {
            var ring = side.Select(i => i.P).ToList();
            if (ring.Count < 12) continue;
            float cx = ring.Average(p => p.X), cz = ring.Average(p => p.Z);
            var bins = new bool[12];
            foreach (var p in ring)
            {
                float angle = MathF.Atan2(p.Z - cz, p.X - cx);
                bins[Math.Clamp((int)((angle + MathF.PI) / MathF.Tau * 12f), 0, 11)] = true;
            }
            if (bins.Count(b => b) >= 10) return $"wrapped round a thigh at {Place(ring.MinBy(p => p.Y))}";
        }
        return null;
    }
}
