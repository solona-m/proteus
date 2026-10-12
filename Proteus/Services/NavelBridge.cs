using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;
using static Proteus.Services.BodyBridge;

/// <summary>
/// The navel fill: where a body's navel is (<see cref="LocateNavel"/>), and the second skin stretched flat across it
/// (<see cref="NavelFill"/>). Its own solve beside <see cref="BodyBridge"/>'s spans, sharing their plan type.
/// </summary>
internal static partial class NavelBridge
{

    /// <summary>
    /// Where a body's navel is: the bottom of the pit, the outward axis the fill lifts along, how far the pit's mouth
    /// reaches from its bottom, how far round it the fill looks, how deep it is, and which source part carries it.
    /// Found once per build over every part (<see cref="LocateNavel"/>), so each mesh solves the same pit.
    /// </summary>
    internal sealed record NavelSite(Vec3 Centre, Vec3 Axis, float MouthRadius, float DiscRadius, float Depth, int Owner);

    /// <summary>One source part's skin, as <see cref="LocateNavel"/> reads it: the bone weights only exclude.</summary>
    internal readonly record struct NavelMesh(Vec3[] Pos, Vec3[] Nrm, int[] Tris, float[] Bust, float[] Thigh);

    /// <summary>
    /// On a body with no breast bones (the game's men), how far up the midline from the crotch to the neck the navel is
    /// looked for: the navel sits under halfway on every body, the gap between the pectorals and the throat above it.
    /// </summary>
    private const float NavelBelowNeck = 0.5f;

    /// <summary>What <see cref="NavelFill"/> did on one mesh.</summary>
    internal enum NavelOutcome { Filled, NotOnThisMesh, Declined }

    /// <summary>The figures of one <see cref="NavelFill"/>, for the log and the tests; depths in model units.</summary>
    internal sealed class NavelFillReport
    {
        public NavelOutcome Outcome { get; set; } = NavelOutcome.NotOnThisMesh;
        public string? Reason { get; set; }
        public float DepthBefore { get; set; }
        public float DepthAfter { get; set; }
        public int Spanned { get; set; }
        public int InwardTriangles { get; set; }
        public int InsideNodes { get; set; }

        /// <summary>How far the fill would have stood over the membrane beyond the belly's bow, had it not been clamped there.</summary>
        public float Proud { get; set; }
    }

    /// <summary>
    /// Bone weight at or above which a vertex belongs to the breasts or a thigh and can hold no navel. Not "any
    /// weight": bodies leave stray midline breast weights of 0.01–0.04 well down the belly.
    /// </summary>
    internal const float NavelBoneFloor = 0.1f;

    /// <summary>How far either side of the midline the navel is looked for: well past any navel, short of the hips.</summary>
    private const float NavelMidline = 0.05f;

    /// <summary>How close to the midline skin must be to mark the crotch, the lowest point the navel is looked above.</summary>
    private const float NavelCrotchMidline = 0.01f;

    /// <summary>
    /// The least lift, closed on every side, that counts a vertex as in a pit. Under it is the float noise of a hull
    /// over a smooth belly — 0.3 mm, under anything shading shows.
    /// </summary>
    private const float NavelLiftFloor = 0.0003f;

    /// <summary>The shallowest pit taken for a navel: 1 mm. A shallower dimple reads as painted on at worst.</summary>
    private const float NavelMinDepth = 0.001f;

    /// <summary>
    /// The share of a pit's depth its shape is judged at. Lower, a navel runs together with the shallow ground round
    /// it; at half its depth it is the navel alone.
    /// </summary>
    private const float NavelCoreShare = 0.5f;

    /// <summary>The widest a pit's core may be, across either axis: past it the hollow is a bowl, not a navel.</summary>
    private const float NavelMaxWidth = 0.03f;

    /// <summary>
    /// How deep a pit must be for the length of its core. A navel is deep for its size, round or a slit (Rue+'s is
    /// 0.34, Neolithe's 0.49, Bibo+'s 0.56); the groove down the middle of the belly, wherever it happens to end, is
    /// long and shallow (0.04–0.11 on the same bodies).
    /// </summary>
    private const float NavelMinSteepness = 0.2f;

    /// <summary>
    /// How far up from the crotch toward the breasts the navel is looked for, as a share of the way. The navel sits
    /// about two thirds of the way up on every body measured; the vulva, closed on every side, sits under a third.
    /// </summary>
    private const float NavelAboveCrotch = 0.4f;

    /// <summary>The share of the depth the fill spans down to: everything at least this far into the pit is filled.</summary>
    private const float NavelMouthShare = 0.1f;

    /// <summary>
    /// How far round the pit the fill looks, as a multiple of the mouth's reach: past the whole pit, so every chord
    /// across it has belly at both ends.
    /// </summary>
    private const float NavelDiscMouths = 1.5f;

    /// <summary>Fewest welded nodes a part must have round the site for its own copy of the navel to be solved.</summary>
    private const int MinNavelNodes = 12;

    /// <summary>Directions the pit's lift is read along, half a turn in eight steps — <see cref="MeshVolumeSolve"/>'s set.</summary>
    private static readonly (float Cos, float Sin)[] NavelDirections =
        [.. Enumerable.Range(0, 8).Select(i => (MathF.Cos(i * MathF.PI / 8f), MathF.Sin(i * MathF.PI / 8f)))];

    /// <summary>A part's skin welded by position: what both the locator and the fill read.</summary>
    private sealed class NavelSurface
    {
        public int[] NodeOf = null!;
        public int NodeCount;
        public Vec3[] At = null!;
        public Vec3[] Nn = null!;
        public float[] Bust = null!;
        public float[] Thigh = null!;
        public List<int>[] Adj = null!;

        /// <summary>Nodes on an edge only one triangle uses: a hem, a hole, or where the part ends.</summary>
        public bool[] Open = null!;

        public static NavelSurface Weld(Vec3[] pos, Vec3[] nrm, IReadOnlyList<int> tris, float[]? bust, float[]? thigh)
        {
            var s = new NavelSurface { NodeOf = WeldByPosition(pos, out int nodeCount) };
            s.NodeCount = nodeCount;
            s.At = new Vec3[nodeCount];
            s.Nn = new Vec3[nodeCount];
            s.Bust = new float[nodeCount];
            s.Thigh = new float[nodeCount];
            var members = new int[nodeCount];
            for (int i = 0; i < pos.Length; i++)
            {
                int n = s.NodeOf[i];
                s.At[n] = new Vec3(s.At[n].X + pos[i].X, s.At[n].Y + pos[i].Y, s.At[n].Z + pos[i].Z);
                if (i < nrm.Length) s.Nn[n] = new Vec3(s.Nn[n].X + nrm[i].X, s.Nn[n].Y + nrm[i].Y, s.Nn[n].Z + nrm[i].Z);
                if (bust != null && i < bust.Length) s.Bust[n] = MathF.Max(s.Bust[n], bust[i]);
                if (thigh != null && i < thigh.Length) s.Thigh[n] = MathF.Max(s.Thigh[n], thigh[i]);
                members[n]++;
            }
            for (int n = 0; n < nodeCount; n++)
            {
                s.At[n] = new Vec3(s.At[n].X / members[n], s.At[n].Y / members[n], s.At[n].Z / members[n]);
                s.Nn[n] = Normalize(s.Nn[n]) ?? default;
            }
            s.Adj = NodeAdjacency(s.NodeOf, tris, nodeCount);
            s.Open = new bool[nodeCount];
            var uses = new Dictionary<(int, int), int>();
            void Edge(int a, int b)
            {
                if (a == b) return;
                var k = a < b ? (a, b) : (b, a);
                uses[k] = uses.GetValueOrDefault(k) + 1;
            }
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                if (tris[t] >= pos.Length || tris[t + 1] >= pos.Length || tris[t + 2] >= pos.Length) continue;
                int a = s.NodeOf[tris[t]], b = s.NodeOf[tris[t + 1]], c = s.NodeOf[tris[t + 2]];
                if (a == b || b == c || a == c) continue;
                Edge(a, b); Edge(b, c); Edge(c, a);
            }
            foreach (var ((a, b), n) in uses)
                if (n == 1) { s.Open[a] = true; s.Open[b] = true; }
            return s;
        }
    }

    /// <summary>
    /// One pit a lift reading found: its deepest node, depth, the core's extent across and up, the mouth's reach,
    /// its nodes, and whether it runs out to an open edge.
    /// </summary>
    private readonly record struct NavelPit(int Deepest, float Depth, float Wide, float Tall, float Mouth, List<int> Nodes, bool Open);

    /// <summary>
    /// The outward axis of the belly round a pit: the mean normal of the two rings of nodes just outside it. Not
    /// in it, whose walls face every way, and not far off, where the belly has turned toward the hips or the ribs.
    /// </summary>
    private static Vec3 RimAxis(NavelSurface s, List<int> pit)
    {
        var inside = new HashSet<int>(pit);
        var ring = new HashSet<int>();
        var front = new List<int>(pit);
        for (int r = 0; r < 2; r++)
        {
            var next = new List<int>();
            foreach (int n in front)
                foreach (int k in s.Adj[n])
                    if (!inside.Contains(k) && ring.Add(k)) next.Add(k);
            front = next;
        }
        var sum = new Vec3(0, 0, 0);
        foreach (int n in ring) sum = new Vec3(sum.X + s.Nn[n].X, sum.Y + s.Nn[n].Y, sum.Z + s.Nn[n].Z);
        return Normalize(sum) ?? new Vec3(0, 0, 1);
    }

    /// <summary>
    /// The navel of a body made of these parts, or null with the reason. The deepest pit on the front of the belly
    /// that is closed on every side: the lift of each vertex up to the upper hull of the surface along the forward
    /// axis, read as chords in eight directions, the least of the eight kept (<see cref="ClosedLift"/>). A pit has lift
    /// in all of them; a groove has none along its own length, and a convex belly none at all. Breast- and
    /// thigh-weighted skin, everything above the breasts' lowest point and everything below the crotch are never searched.
    /// </summary>
    /// <remarks>
    /// Each part is read ON ITS OWN: where the top and the legs overlap at the waist, the inner layer sits under the
    /// outer one and reads as a pit as deep as the gap. Read from each part's plain LOD0 skin, before any shape key or
    /// hidden submesh: only where the navel is matters here, and <see cref="NavelFill"/> finds the pit again on the
    /// surface each mesh actually draws. A non-skin mesh (a piercing in the navel) is not in the skin at all.
    /// </remarks>
    internal static NavelSite? LocateNavel(IReadOnlyList<NavelMesh> parts, Action<string>? log, out string? why)
    {
        why = null;
        var surfaces = parts.Select(p => p.Pos.Length == 0 ? null
                                        : NavelSurface.Weld(p.Pos, p.Nrm, p.Tris, p.Bust, p.Thigh)).ToList();
        if (surfaces.All(s => s == null)) { why = "no skin to search"; return null; }

        // Nothing above the breasts' lowest point: the cleavage and the crease under the breasts are deep pits too. A
        // body with no breast bones (the game's men) is capped halfway up its midline instead, or the gap between the
        // pectorals and the throat are searched. Not at a bone: the upper spine's weight runs right down their bellies.
        // Nothing below the crotch, the lowest skin on the midline: the hands and feet never reach the midline.
        float bustLow = float.MaxValue, crotch = float.MaxValue, neck = float.MinValue;
        foreach (var s in surfaces)
        {
            if (s == null) continue;
            for (int n = 0; n < s.NodeCount; n++)
            {
                if (s.Bust[n] >= NavelBoneFloor) bustLow = MathF.Min(bustLow, s.At[n].Y);
                if (MathF.Abs(s.At[n].X) >= NavelCrotchMidline) continue;
                crotch = MathF.Min(crotch, s.At[n].Y);
                neck = MathF.Max(neck, s.At[n].Y);
            }
        }
        if (crotch == float.MaxValue) crotch = float.MinValue;
        if (bustLow == float.MaxValue && crotch > float.MinValue) bustLow = crotch + NavelBelowNeck * (neck - crotch);
        // Only with the legs there: without thigh-weighted skin the lowest midline point is a hem, not the crotch, and
        // there is no vulva to keep out.
        bool hasLegs = surfaces.Any(s => s != null && s.Thigh.Any(w => w >= NavelBoneFloor));
        float floorY = hasLegs && bustLow < float.MaxValue && crotch > float.MinValue
            ? crotch + NavelAboveCrotch * (bustLow - crotch)
            : crotch;

        var pits = new List<(int Part, NavelPit Pit, NavelSurface Surface)>();
        int searched = 0, byBone = 0;
        for (int p = 0; p < surfaces.Count; p++)
        {
            if (surfaces[p] is not { } s) continue;
            var region = new List<int>();
            for (int n = 0; n < s.NodeCount; n++)
            {
                var a = s.At[n];
                if (s.Adj[n].Count == 0) continue;
                if (a.Z <= 0f || MathF.Abs(a.X) > NavelMidline || a.Y >= bustLow || a.Y <= floorY) continue;
                // A pit's walls face sideways and its hood faces down, so only the back-facing is left out.
                if (s.Nn[n].Z < -0.3f) continue;
                if (s.Bust[n] >= NavelBoneFloor || s.Thigh[n] >= NavelBoneFloor) { byBone++; continue; }
                region.Add(n);
            }
            if (region.Count < MinNavelNodes) continue;
            searched += region.Count;
            float strip = MathF.Max(MeanEdgeLength(s.At, s.Adj, region), 1e-4f);
            var lift = ClosedLift(s.At, s.Adj, region, new Vec3(0, 0, 1), strip);
            foreach (var pit in Pits(s, region, lift, strip))
                pits.Add((p, pit, s));
        }
        if (searched == 0)
        {
            why = $"no skin on the front of the belly ({byBone} vertex(es) there are on breast or thigh weights)";
            return null;
        }

        pits.Sort((a, b) => b.Pit.Depth.CompareTo(a.Pit.Depth));
        foreach (var (part, pt, s) in pits.Take(5))
        {
            var c = s.At[pt.Deepest];
            log?.Invoke($"navel fill: pit on part {part} at ({c.X:0.####}, {c.Y:0.####}, {c.Z:0.####}) "
                      + $"{pt.Depth * 1000:0.##}mm deep, its core {pt.Wide * 1000:0.#}mm wide and "
                      + $"{pt.Tall * 1000:0.#}mm tall, mouth {pt.Mouth * 1000:0.#}mm, {pt.Nodes.Count} vertex(es)"
                      + (pt.Open ? ", reaching an open edge" : ""));
        }

        string? rejected = null;
        foreach (var (part, pt, s) in pits)
        {
            float wide = MathF.Max(pt.Wide, pt.Tall);
            string? fails = pt.Depth < NavelMinDepth ? $"{pt.Depth * 1000:0.##}mm deep, under {NavelMinDepth * 1000:0.#}mm"
                          : wide > NavelMaxWidth ? $"{wide * 1000:0.#}mm across, wider than a navel"
                          : pt.Depth < NavelMinSteepness * wide
                              ? $"{pt.Depth * 1000:0.##}mm deep over {wide * 1000:0.#}mm — a groove"
                          : pt.Open ? "cut open by the edge of its part"
                          : null;
            if (fails != null) { rejected ??= fails; continue; }

            var c = s.At[pt.Deepest];
            float disc = pt.Mouth * NavelDiscMouths;
            var axis = RimAxis(s, pt.Nodes);
            log?.Invoke($"navel fill: the navel is the pit on part {part}, axis ({axis.X:0.###}, {axis.Y:0.###}, "
                      + $"{axis.Z:0.###})");
            return new NavelSite(c, axis, pt.Mouth, disc, pt.Depth, part);
        }
        why = pits.Count == 0 ? "no pit on the front of the belly" : $"no pit is a navel (the deepest is {rejected})";
        return null;
    }

    /// <summary>
    /// How deep a hollow this surface has in the mouth of <paramref name="site"/>, read the way <see cref="NavelFill"/>
    /// reads the pit: for measuring a written shell against the body it was cut from.
    /// </summary>
    internal static float NavelDepthAt(Vec3[] pos, Vec3[] nrm, int[] tris, NavelSite site)
    {
        var lift = NavelLiftAt(pos, nrm, tris, site);
        float depth = 0f;
        for (int i = 0; i < pos.Length; i++)
            if (Dist(pos[i], site.Centre) <= site.MouthRadius) depth = MathF.Max(depth, lift[i]);
        return depth;
    }

    /// <summary>
    /// Per vertex, how far under the surface's upper hull round <paramref name="site"/> it sits (0 outside the disc).
    /// </summary>
    internal static float[] NavelLiftAt(Vec3[] pos, Vec3[] nrm, int[] tris, NavelSite site)
    {
        var outL = new float[pos.Length];
        var s = NavelSurface.Weld(pos, nrm, tris, null, null);
        var region = new List<int>();
        for (int n = 0; n < s.NodeCount; n++)
            if (s.Adj[n].Count > 0 && Dist(s.At[n], site.Centre) <= site.DiscRadius
                && s.Nn[n].X * site.Axis.X + s.Nn[n].Y * site.Axis.Y + s.Nn[n].Z * site.Axis.Z >= -0.3f)
                region.Add(n);
        if (region.Count < MinNavelNodes) return outL;
        var lift = ClosedLift(s.At, s.Adj, region, site.Axis, MathF.Max(MeanEdgeLength(s.At, s.Adj, region), 1e-4f));
        var byNode = new Dictionary<int, float>(region.Count);
        for (int i = 0; i < region.Count; i++) byNode[region[i]] = lift[i];
        for (int i = 0; i < pos.Length; i++) outL[i] = byNode.GetValueOrDefault(s.NodeOf[i]);
        return outL;
    }

    /// <summary>
    /// The pits in a lift reading: connected runs over <see cref="NavelLiftFloor"/>, each measured from its deepest node.
    /// The core (<see cref="NavelCoreShare"/> of the depth, connected to the deepest node) gives the shape; the mouth is
    /// how far from the deepest node the pit still reaches <see cref="NavelMouthShare"/> of its depth.
    /// </summary>
    private static List<NavelPit> Pits(NavelSurface s, List<int> region, float[] lift, float strip)
    {
        var index = new Dictionary<int, int>(region.Count);
        for (int i = 0; i < region.Count; i++) index[region[i]] = i;
        var seen = new bool[region.Count];
        var pits = new List<NavelPit>();
        for (int i = 0; i < region.Count; i++)
        {
            if (seen[i] || lift[i] < NavelLiftFloor) continue;
            var comp = Flood(s, region[i], index, lift, NavelLiftFloor, seen);
            int deepest = comp[0];
            foreach (int n in comp) if (lift[index[n]] > lift[index[deepest]]) deepest = n;
            float depth = lift[index[deepest]];

            var core = Flood(s, deepest, index, lift, MathF.Max(NavelLiftFloor, NavelCoreShare * depth), null);
            float lx = float.MaxValue, hx = float.MinValue, ly = float.MaxValue, hy = float.MinValue;
            foreach (int n in core)
            {
                lx = MathF.Min(lx, s.At[n].X); hx = MathF.Max(hx, s.At[n].X);
                ly = MathF.Min(ly, s.At[n].Y); hy = MathF.Max(hy, s.At[n].Y);
            }
            float mouth = 0f;
            foreach (int n in Flood(s, deepest, index, lift, MathF.Max(NavelLiftFloor, NavelMouthShare * depth), null))
                mouth = MathF.Max(mouth, Dist(s.At[n], s.At[deepest]));
            pits.Add(new NavelPit(deepest, depth, hx - lx, hy - ly, MathF.Max(mouth, 2f * strip), comp,
                                  comp.Any(n => s.Open[n])));
        }
        return pits;
    }

    /// <summary>The nodes connected to <paramref name="from"/> through region nodes whose lift is at least <paramref name="floor"/>.</summary>
    private static List<int> Flood(NavelSurface s, int from, Dictionary<int, int> index, float[] lift, float floor, bool[]? seen)
    {
        var comp = new List<int>();
        var mark = seen ?? new bool[lift.Length];
        var stack = new Stack<int>();
        stack.Push(from);
        mark[index[from]] = true;
        while (stack.Count > 0)
        {
            int c = stack.Pop();
            comp.Add(c);
            foreach (int k in s.Adj[c])
                if (index.TryGetValue(k, out int ki) && !mark[ki] && lift[ki] >= floor)
                { mark[ki] = true; stack.Push(k); }
        }
        return comp;
    }

    /// <summary>
    /// A part's skin as <see cref="TryReadLod0Geometry(byte[], out float[], out float[], out int[], out (string, float)[][], out float[], bool, bool, Func{string, bool})"/>
    /// hands it back, made into a <see cref="NavelMesh"/>: the breast weight is the breast bones' sum, the thigh weight
    /// the heavier thigh.
    /// </summary>
    internal static NavelMesh ToNavelMesh(float[] positions, float[] normals, int[] triangles,
                                          (string Bone, float W)[][] weights)
    {
        int n = positions.Length / 3;
        var pos = new Vec3[n];
        var nrm = new Vec3[n];
        var bustW = new float[n];
        var thighW = new float[n];
        for (int i = 0; i < n; i++)
        {
            pos[i] = new Vec3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
            if (i * 3 + 2 < normals.Length) nrm[i] = new Vec3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
            if (i >= weights.Length) continue;
            foreach (var (bone, w) in weights[i])
            {
                if (IsBustBone(bone)) bustW[i] += w;
                else if (bone.Equals(ThighBoneL, StringComparison.Ordinal) || bone.Equals(ThighBoneR, StringComparison.Ordinal))
                    thighW[i] = MathF.Max(thighW[i], w);
            }
        }
        return new NavelMesh(pos, nrm, triangles, bustW, thighW);
    }

    /// <summary>Welded-node neighbours from a triangle list over vertices.</summary>
    private static List<int>[] NodeAdjacency(int[] nodeOf, IReadOnlyList<int> tris, int nodeCount)
    {
        var sets = new HashSet<int>[nodeCount];
        for (int n = 0; n < nodeCount; n++) sets[n] = [];
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            if (tris[t] >= nodeOf.Length || tris[t + 1] >= nodeOf.Length || tris[t + 2] >= nodeOf.Length) continue;
            int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
            if (a != b) { sets[a].Add(b); sets[b].Add(a); }
            if (b != c) { sets[b].Add(c); sets[c].Add(b); }
            if (a != c) { sets[a].Add(c); sets[c].Add(a); }
        }
        var adj = new List<int>[nodeCount];
        for (int n = 0; n < nodeCount; n++) adj[n] = [.. sets[n]];
        return adj;
    }

    /// <summary>
    /// Per listed node, how far below the surface's upper hull along <paramref name="axis"/> it sits, read as 1-D
    /// chords in <see cref="NavelDirections"/> and the LEAST of them kept: lift closed on every side. The construction
    /// of the Studio's bridge brush (<see cref="MeshVolumeSolve"/>): strips one mesh edge wide, each point's height
    /// carried back to its strip's centre line along the fitted surface slope, or a sloped surface reads its upper rows
    /// as chords.
    /// </summary>
    private static float[] ClosedLift(Vec3[] at, List<int>[] adj, List<int> nodes, Vec3 axis, float strip)
    {
        Basis(axis, out var u, out var v);
        int count = nodes.Count;
        var px = new float[count];
        var py = new float[count];
        var ph = new float[count];
        for (int i = 0; i < count; i++)
        {
            var p = at[nodes[i]];
            px[i] = p.X * u.X + p.Y * u.Y + p.Z * u.Z;
            py[i] = p.X * v.X + p.Y * v.Y + p.Z * v.Z;
            ph[i] = p.X * axis.X + p.Y * axis.Y + p.Z * axis.Z;
        }

        // Each point's height gradient over the plane, least squares over its mesh neighbours.
        var gx = new float[count];
        var gy = new float[count];
        for (int i = 0; i < count; i++)
        {
            float sxx = 0f, sxy = 0f, syy = 0f, sxh = 0f, syh = 0f;
            foreach (int k in adj[nodes[i]])
            {
                var q = at[k];
                float dx = q.X * u.X + q.Y * u.Y + q.Z * u.Z - px[i];
                float dy = q.X * v.X + q.Y * v.Y + q.Z * v.Z - py[i];
                float dh = q.X * axis.X + q.Y * axis.Y + q.Z * axis.Z - ph[i];
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy; sxh += dx * dh; syh += dy * dh;
            }
            float det = sxx * syy - sxy * sxy;
            if (MathF.Abs(det) < 1e-18f) continue;
            // Clamped: on a near-vertical wall the fit runs to huge slopes, which are noise.
            gx[i] = Math.Clamp((sxh * syy - syh * sxy) / det, -3f, 3f);
            gy[i] = Math.Clamp((syh * sxx - sxh * sxy) / det, -3f, 3f);
        }

        var closed = new float[count];
        Array.Fill(closed, float.MaxValue);
        var s = new float[count];
        var hc = new float[count];
        var strips = new Dictionary<int, List<int>>();
        var hull = new List<int>();
        foreach (var (cos, sin) in NavelDirections)
        {
            strips.Clear();
            for (int i = 0; i < count; i++)
            {
                s[i] = px[i] * cos + py[i] * sin;
                float t = -px[i] * sin + py[i] * cos;
                int bin = (int)MathF.Floor(t / strip);
                float slopeAcross = -gx[i] * sin + gy[i] * cos;
                hc[i] = ph[i] - slopeAcross * (t - (bin + 0.5f) * strip);
                (strips.TryGetValue(bin, out var l) ? l : strips[bin] = []).Add(i);
            }
            foreach (var list in strips.Values)
            {
                list.Sort((a, b) => s[a] != s[b] ? s[a].CompareTo(s[b]) : hc[a].CompareTo(hc[b]));
                // The upper hull of the strip's (s, h) profile.
                hull.Clear();
                foreach (int i in list)
                {
                    while (hull.Count >= 2)
                    {
                        int o = hull[^2], a = hull[^1];
                        float cross = (s[a] - s[o]) * (hc[i] - hc[o]) - (hc[a] - hc[o]) * (s[i] - s[o]);
                        if (cross >= 0f) hull.RemoveAt(hull.Count - 1); else break;
                    }
                    hull.Add(i);
                }
                int seg = 0;
                foreach (int i in list)
                {
                    while (seg + 1 < hull.Count - 1 && s[hull[seg + 1]] < s[i]) seg++;
                    float lift;
                    if (hull.Count < 2) lift = 0f;
                    else
                    {
                        int a = hull[seg], b = hull[seg + 1];
                        float span = s[b] - s[a];
                        float top = span > 1e-9f ? hc[a] + (hc[b] - hc[a]) * (s[i] - s[a]) / span : MathF.Max(hc[a], hc[b]);
                        lift = MathF.Max(0f, top - hc[i]);
                    }
                    if (lift < closed[i]) closed[i] = lift;
                }
            }
        }
        for (int i = 0; i < count; i++) if (closed[i] == float.MaxValue) closed[i] = 0f;
        return closed;
    }
}
