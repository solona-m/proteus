using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// What a garment's cloth rides on when the brush moves its skin ("Move skin too"): each cloth node close over the skin
/// bound to the skin triangle under it, so the cloth follows the body instead of the body poking through it. And where
/// the skin may move at all: its open edges, where this piece meets the next (waist, wrists, neck), held still with a
/// fade in, or sculpting would open a gap against the model beside it.
/// <para/>
/// Pure and immutable once built, so the binding — a nearest-skin search per cloth node, slow on a long dress — is made
/// on a worker (<see cref="Bind"/>) while the user keeps working, and handed to its solve when done.
/// </summary>
internal sealed class SkinFollow
{
    /// <summary>Cloth this near the skin (10 mm) follows it all the way.</summary>
    internal const float FollowFull = 0.010f;

    /// <summary>Cloth further than this (30 mm) does not follow at all: a skirt hangs free of the legs.</summary>
    internal const float FollowReach = 0.030f;

    /// <summary>How far in from an open skin edge the brushes take to reach full strength (30 mm).</summary>
    internal const float SeamFade = 0.030f;

    /// <summary>
    /// Cloth more than this (5 mm) BEHIND the nearest skin face is not on that skin: the face is the far side of a limb
    /// the cloth hugs from the other side, and binding through it would have the cloth follow the wrong limb. A little
    /// behind is a clip the author left, which rides and keeps it.
    /// </summary>
    internal const float ClipTolerance = 0.005f;

    /// <summary>What a binding is built from: the solve's own model and node numbering, never changed after.</summary>
    /// <param name="Owner">The solve it was built for, so a binding finished after the model changed is dropped.</param>
    internal sealed record Inputs(ModelParts Model, int[] NodeOf, int NodeCount, bool[] Skin, float MeanEdge, object Owner);

    private readonly float[] bind;
    private readonly float[] height;
    private readonly (int Skin, float Share)[][] corners;
    private readonly List<(int Rider, float Weight)>[] riders;

    /// <summary>The solve this binding belongs to — see <see cref="Inputs.Owner"/>.</summary>
    internal object Owner { get; }

    /// <summary>How many cloth nodes ride the skin.</summary>
    public int RiderCount { get; }

    /// <summary>How long <see cref="Bind"/> took, for the log.</summary>
    public double BuildMilliseconds { get; }

    private SkinFollow(float[] bind, float[] height, (int, float)[][] corners, List<(int, float)>[] riders,
                       int riderCount, object owner, double ms)
    {
        this.bind = bind;
        this.height = height;
        this.corners = corners;
        this.riders = riders;
        RiderCount = riderCount;
        Owner = owner;
        BuildMilliseconds = ms;
    }

    private static readonly (int, float)[] NoCorners = [];
    private static readonly List<(int, float)> NoRiders = [];

    /// <summary>How strongly node <paramref name="node"/> follows the skin, 0..1: 1 within <see cref="FollowFull"/>,
    /// fading to 0 at <see cref="FollowReach"/>. 0 for skin itself and for cloth bound to nothing.</summary>
    public float BindWeight(int node) => node >= 0 && node < bind.Length ? bind[node] : 0f;

    /// <summary>The cloth node's authored height above the skin it is bound to, along that skin's normal (metres).</summary>
    public float AuthoredHeight(int node) => height[node];

    /// <summary>The skin nodes a cloth node is bound to and each one's barycentric share; empty when unbound.</summary>
    public IReadOnlyList<(int Skin, float Share)> CornersOf(int node) => corners[node] ?? NoCorners;

    /// <summary>The cloth nodes that ride skin node <paramref name="skinNode"/>, each with bind weight × its share.</summary>
    public IReadOnlyList<(int Rider, float Weight)> RidersOf(int skinNode) => riders[skinNode] ?? NoRiders;

    /// <summary>
    /// Bind every cloth node within <see cref="FollowReach"/> of the skin to the skin triangle nearest it, on the
    /// author's positions. Safe on a worker: it reads only <paramref name="inputs"/>.
    /// </summary>
    public static SkinFollow Bind(Inputs inputs)
    {
        var clock = Stopwatch.StartNew();
        var model = inputs.Model;
        int n = inputs.NodeCount;
        var bind = new float[n];
        var height = new float[n];
        var corners = new (int, float)[n][];
        var riders = new List<(int, float)>[n];
        int riderCount = 0;

        var surface = new BodySurface(model, BodySurface.CellFor(inputs.MeanEdge));
        if (!surface.IsEmpty)
        {
            var seen = new bool[n];
            int vc = Math.Min(model.Positions.Length / 3, inputs.NodeOf.Length);
            for (int v = 0; v < vc; v++)
            {
                int node = inputs.NodeOf[v];
                if (inputs.Skin[node] || seen[node]) continue;
                seen[node] = true;

                var p = new Vector3(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
                if (!surface.Nearest(p, FollowReach, out var hit)) continue;

                float h = Vector3.Dot(p - hit.Point, hit.Normal);
                if (h < -ClipTolerance) continue;   // through a limb: see ClipTolerance
                float b = FollowWeight(hit.Distance);
                if (b <= 0f) continue;

                // Corners welded together are one skin node, its shares summed.
                var shares = new List<(int Skin, float Share)>(3);
                foreach (var (vertex, share) in new[] { (hit.A, hit.U), (hit.B, hit.V), (hit.C, hit.W) })
                {
                    if (share <= 0f) continue;
                    int s = inputs.NodeOf[vertex];
                    int at = shares.FindIndex(x => x.Skin == s);
                    if (at >= 0) shares[at] = (s, shares[at].Share + share);
                    else shares.Add((s, share));
                }
                if (shares.Count == 0) continue;

                bind[node] = b;
                height[node] = h;
                corners[node] = shares.ToArray();
                riderCount++;
                foreach (var (s, share) in shares)
                    (riders[s] ??= []).Add((node, b * share));
            }
        }

        return new SkinFollow(bind, height, corners, riders, riderCount, inputs.Owner, clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>How strongly cloth <paramref name="distance"/> off the skin follows it — see <see cref="BindWeight"/>.</summary>
    internal static float FollowWeight(float distance)
    {
        if (distance <= FollowFull) return 1f;
        if (distance >= FollowReach) return 0f;
        return MeshVolumeSolve.Falloff((distance - FollowFull) / (FollowReach - FollowFull));
    }

    /// <summary>
    /// Per node, how much of a brush stroke the skin there may take, 0..1: 0 on an open edge of the skin — an edge only
    /// one triangle of the whole model uses, where this piece ends and the next model's skin carries on — rising to 1
    /// at <see cref="SeamFade"/> along the skin from it. 1 for cloth, and for skin with no open edge in reach. Skin
    /// welded to cloth is not open there: the cloth carries the surface on.
    /// </summary>
    /// <param name="nodeTris">Every triangle of the model as node triples.</param>
    /// <param name="nodeAt">Each node's position.</param>
    public static float[] Seams(int nodeCount, bool[] skin, List<int>[] adj, int[] nodeTris, Vec3[] nodeAt)
    {
        var seam = new float[nodeCount];
        Array.Fill(seam, 1f);

        var uses = new Dictionary<(int, int), int>();
        for (int t = 0; t + 2 < nodeTris.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                var key = EdgeKey(nodeTris[t + k], nodeTris[t + (k + 1) % 3]);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }

        var dist = new Dictionary<int, float>();
        var queue = new PriorityQueue<int, float>();
        foreach (var ((a, b), count) in uses)
        {
            if (count != 1) continue;
            foreach (int s in new[] { a, b })
                if (skin[s] && dist.TryAdd(s, 0f)) queue.Enqueue(s, 0f);
        }

        while (queue.TryDequeue(out int node, out float d))
        {
            if (d > dist[node]) continue;
            foreach (int m in adj[node])
            {
                if (!skin[m]) continue;
                float dx = nodeAt[m].X - nodeAt[node].X, dy = nodeAt[m].Y - nodeAt[node].Y, dz = nodeAt[m].Z - nodeAt[node].Z;
                float next = d + MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (next >= SeamFade) continue;
                if (dist.TryGetValue(m, out float was) && was <= next) continue;
                dist[m] = next;
                queue.Enqueue(m, next);
            }
        }

        foreach (var (node, d) in dist) seam[node] = 1f - MeshVolumeSolve.Falloff(d / SeamFade);
        return seam;
    }

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);
}
