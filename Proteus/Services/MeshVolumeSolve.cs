using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// One model's geometry under the brush: what the user has pushed out so far, and what it would be written
/// as. Pure geometry — nothing here touches a file, a mod or the game.
/// <para/>
/// Built once when a model is opened and then mutated stroke by stroke, because the expensive parts — the
/// weld, the adjacency, the boundary scan — depend only on the topology, and a brush stroke never changes
/// which vertices exist.
/// <para/>
/// EVERYTHING IS DECIDED PER WELDED NODE, never per vertex. A garment splits vertices along every UV seam
/// and every hard crease, so the same point on the surface is several vertices with different neighbours;
/// moving one copy and not its twin opens a crack down the seam. That is also why the two safety passes run
/// against node positions and a node-level triangle list rather than the vertex arrays they were originally
/// written for — <see cref="SecondSkinWriter.UnfoldTriangles"/> pulls a displacement back per INDEX it is
/// given, and given vertices it can halve one copy of a seam and leave the other at full stretch.
/// </summary>
internal sealed class MeshVolumeSolve
{
    /// <summary>
    /// Bump when the geometry this produces changes, so a model edited by an older build can be recognised
    /// and redone from the author's own backup. Same contract as <c>HatCompatSolve.Version</c>.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// The furthest any one point may end up from where its author put it, however long the brush is held.
    /// <para/>
    /// A cap rather than a warning because a stroke accumulates per frame: a held button on a fast machine
    /// applies hundreds of dabs a second, and a stuck drag or a moment's inattention would otherwise balloon
    /// a garment into a sphere. Five millimetres is far more than clipping needs — the shell layers this
    /// project ships sit a single millimetre off the skin — and it is still small enough to be undone by eye.
    /// </summary>
    public const float MaxDisplacement = 0.005f;

    /// <summary>
    /// How much more displacement one node may carry than its neighbour, as a fraction of the edge between
    /// them, before <see cref="SecondSkinWriter.LimitSlopeVector"/> pulls it back.
    /// <para/>
    /// The same value the crotch-fold relax settled on for a vector displacement, and tighter than the
    /// scalar sweep's because a limit applied independently in three axes lets neighbours differ by root
    /// three times as much in the worst direction — including straight through one another.
    /// </summary>
    private const float MaxSlope = 1.0f;

    private readonly Vec3[] basePos;
    private readonly Vec3[] baseNrm;

    /// <summary>Every LOD0 triangle, as vertex indices into <see cref="basePos"/>.</summary>
    private readonly int[] tris;

    /// <summary>The same triangles as node triples, degenerate faces dropped — what the safety passes see.</summary>
    private readonly int[] nodeTris;

    private readonly int[] nodeOf;
    private readonly int nodeCount;
    private readonly List<int>[] adj;
    private readonly Vec3[] nodeAt;

    /// <summary>
    /// The direction a node inflates along: its vertices' authored normals, averaged and renormalised.
    /// <para/>
    /// The author's normals rather than normals derived from the faces, because they are what the surface is
    /// actually shaded by and they already encode which side is out. Face accumulation is the fallback for
    /// a mesh that declares no usable normal at all, where there is nothing else to go on.
    /// <para/>
    /// Refreshed from the deformed surface at the end of every stroke. That is not tidiness: inside a
    /// concavity the normals point sideways ACROSS the gap rather than out of it, so repeatedly inflating
    /// one spot along the directions it started with drives the two walls apart instead of pushing the
    /// surface outward.
    /// </summary>
    private readonly Vec3[] nodeNormal;

    /// <summary>Nodes on the rim of a hole, which never move. See the constructor.</summary>
    private readonly bool[] frozen;

    private bool[] locked;

    private Vec3[] nodeDelta;

    /// <summary>
    /// The strongest falloff weight each node has ever been brushed with — the region ramp, not the size of
    /// its displacement.
    /// <para/>
    /// It exists to blend normals, and the distinction matters at the edge of a stroke: a node the brush
    /// barely reached has a tiny displacement but must also take only a tiny share of the recomputed normal,
    /// or the shading creases along a ring where the geometry is still smooth.
    /// </summary>
    private float[] nodeWeight;

    private Vec3[] vertDelta;
    private Vec3[] vertNrm;

    private readonly List<Dictionary<int, (Vec3 Delta, float Weight)>> undo = [];
    private Dictionary<int, (Vec3 Delta, float Weight)>? stroke;

    private readonly Dictionary<string, int[]> partVertices;

    public MeshVolumeSolve(ModelParts model)
    {
        Spans = model.MeshSpans;

        int vc = model.Positions.Length / 3;
        basePos = new Vec3[vc];
        baseNrm = new Vec3[vc];
        for (int i = 0; i < vc; i++)
        {
            basePos[i] = new Vec3(model.Positions[i * 3], model.Positions[i * 3 + 1], model.Positions[i * 3 + 2]);
            baseNrm[i] = i * 3 + 2 < model.Normals.Length
                ? new Vec3(model.Normals[i * 3], model.Normals[i * 3 + 1], model.Normals[i * 3 + 2])
                : default;
        }

        // WHOLE SUBMESHES ONLY. Islands are subsets of their submesh's triangles, so including both would
        // count every face of a split submesh twice — which doubles its contribution to every node normal.
        tris = model.Parts.Where(p => p.Island < 0).SelectMany(p => p.Triangles).ToArray();

        nodeOf = SecondSkinWriter.WeldByPosition(basePos, out nodeCount);

        nodeAt = new Vec3[nodeCount];
        for (int i = 0; i < vc; i++) nodeAt[nodeOf[i]] = basePos[i];

        // Node-level faces, built once. Degenerate triples — two corners welded to the same point — carry no
        // direction and no area, and would only add self-edges to the adjacency.
        var nt = new List<int>(tris.Length);
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
            int na = nodeOf[a], nb = nodeOf[b], nc = nodeOf[c];
            if (na == nb || nb == nc || na == nc) continue;
            nt.Add(na); nt.Add(nb); nt.Add(nc);
        }
        nodeTris = nt.ToArray();

        // Adjacency, deduped: a shared edge would otherwise pull twice as hard as a boundary one in every
        // averaging pass that walks this.
        adj = new List<int>[nodeCount];
        for (int n = 0; n < nodeCount; n++) adj[n] = [];
        var seen = new HashSet<long>();
        void Link(int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (!seen.Add(key)) return;
            adj[a].Add(b);
            adj[b].Add(a);
        }
        for (int t = 0; t + 2 < nodeTris.Length; t += 3)
        {
            Link(nodeTris[t], nodeTris[t + 1]);
            Link(nodeTris[t + 1], nodeTris[t + 2]);
            Link(nodeTris[t + 2], nodeTris[t]);
        }

        // THE RIM OF A HOLE NEVER MOVES. An edge used by exactly one face is a boundary, and there are three
        // things it can be, none of which this may touch: the seam where the next model file's geometry
        // continues (a body arrives as several models and welding never sees across the join), an authored
        // socket whose far side is not ours at all, or the open end of a garment. Measured on one body, a
        // relax that moved 14 such vertices prised an 8-edge crotch socket open into a visible gash.
        frozen = new bool[nodeCount];
        var edgeUse = new Dictionary<long, int>();
        for (int t = 0; t + 2 < nodeTris.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = nodeTris[t + k], b = nodeTris[t + (k + 1) % 3];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                edgeUse[key] = edgeUse.TryGetValue(key, out var c) ? c + 1 : 1;
            }
        foreach (var (key, uses) in edgeUse)
        {
            if (uses != 1) continue;
            frozen[(int)(key >> 32)] = true;
            frozen[(int)(key & 0xFFFFFFFF)] = true;
        }

        nodeNormal = new Vec3[nodeCount];
        var accum = new Vec3[nodeCount];
        var count = new int[nodeCount];
        for (int i = 0; i < vc; i++)
        {
            int n = nodeOf[i];
            accum[n] = new Vec3(accum[n].X + baseNrm[i].X, accum[n].Y + baseNrm[i].Y, accum[n].Z + baseNrm[i].Z);
            count[n]++;
        }
        for (int n = 0; n < nodeCount; n++) nodeNormal[n] = Unit(accum[n]);

        // Fallback for a mesh that declared no normal element, or whose normals are unreadable: the faces
        // themselves. Direction is then whatever the winding says, which is not something a mod's exporter
        // can be trusted on — but an inconsistent direction on a mesh with no normals is still better than
        // no direction at all, which would mean the brush silently does nothing there.
        if (nodeNormal.Any(n => n.X == 0f && n.Y == 0f && n.Z == 0f))
        {
            var face = new Vec3[nodeCount];
            for (int t = 0; t + 2 < nodeTris.Length; t += 3)
            {
                var fn = SecondSkinWriter.TriNormal(
                    nodeAt[nodeTris[t]], nodeAt[nodeTris[t + 1]], nodeAt[nodeTris[t + 2]]);
                for (int k = 0; k < 3; k++)
                {
                    int n = nodeTris[t + k];
                    face[n] = new Vec3(face[n].X + fn.X, face[n].Y + fn.Y, face[n].Z + fn.Z);
                }
            }
            for (int n = 0; n < nodeCount; n++)
                if (nodeNormal[n].X == 0f && nodeNormal[n].Y == 0f && nodeNormal[n].Z == 0f)
                    nodeNormal[n] = Unit(face[n]);
        }

        nodeDelta = new Vec3[nodeCount];
        nodeWeight = new float[nodeCount];
        locked = new bool[nodeCount];
        vertDelta = new Vec3[vc];
        vertNrm = (Vec3[])baseNrm.Clone();

        partVertices = model.Parts.ToDictionary(
            p => p.Label,
            p => p.Triangles.Distinct().Where(v => v >= 0 && v < vc).ToArray(),
            StringComparer.Ordinal);

        MeanEdge = SecondSkinWriter.MeanEdgeLength(nodeAt, adj, Enumerable.Range(0, nodeCount).ToList());
    }

    public IReadOnlyList<MeshSpan> Spans { get; }

    /// <summary>The mesh's own resolution, so a radius can be judged against what it will actually reach.</summary>
    public float MeanEdge { get; }

    public bool Dirty { get; private set; }

    /// <summary>The largest distance any point has been moved, in metres.</summary>
    public float Worst { get; private set; }

    public bool CanUndo => undo.Count > 0;

    /// <summary>
    /// The brush's weight at a normalised distance from its centre: 1 in the middle, 0 at the rim.
    /// <para/>
    /// Smoothstep, so the weight AND its slope both reach zero at the rim. The second property is what stops
    /// a stroke leaving a visible ring — a linear falloff arrives at the edge still descending, which creases
    /// the surface exactly where the brush stopped.
    /// <para/>
    /// Lives here and is called by the viewport's preview, rather than each having its own copy: a preview
    /// drawn from a second version of this curve is a preview that can disagree with what gets written.
    /// </summary>
    public static float Falloff(float t)
    {
        if (t <= 0f) return 1f;
        if (t >= 1f) return 0f;
        float u = 1f - t;
        return u * u * (3f - 2f * u);
    }

    /// <summary>
    /// Parts the brush may not touch, by label. The part list beside the viewport is a lock list rather than
    /// a selection: the thing a user needs while brushing a hip is for the belt over it to hold still.
    /// </summary>
    public void SetLocked(IReadOnlySet<string> labels)
    {
        var next = new bool[nodeCount];
        foreach (var label in labels)
        {
            if (!partVertices.TryGetValue(label, out var vs)) continue;
            foreach (int v in vs) next[nodeOf[v]] = true;
        }
        locked = next;
    }

    public void BeginStroke() => stroke = [];

    /// <summary>
    /// One dab: push every node inside the brush out along its own normal, by <paramref name="strength"/>
    /// scaled by the falloff. Negative strength pulls in.
    /// </summary>
    /// <returns>How many nodes moved, so a stroke that is reaching nothing can say so.</returns>
    public int Paint(Vector3 centre, float radius, float strength)
    {
        if (radius <= 0f || strength == 0f) return 0;
        stroke ??= [];

        var c = new Vec3(centre.X, centre.Y, centre.Z);
        float r2 = radius * radius;
        int moved = 0;

        for (int n = 0; n < nodeCount; n++)
        {
            if (frozen[n] || locked[n]) continue;

            float dx = nodeAt[n].X - c.X, dy = nodeAt[n].Y - c.Y, dz = nodeAt[n].Z - c.Z;
            float d2 = dx * dx + dy * dy + dz * dz;
            if (d2 >= r2) continue;

            float w = Falloff(MathF.Sqrt(d2) / radius);
            if (w <= 0f) continue;

            var dir = nodeNormal[n];
            if (dir.X == 0f && dir.Y == 0f && dir.Z == 0f) continue;

            // Recorded before the first change of this stroke, not on every dab: a stroke drags over the
            // same node many times and undo has to return it to where the stroke FOUND it.
            stroke.TryAdd(n, (nodeDelta[n], nodeWeight[n]));

            float step = strength * w;
            var next = new Vec3(nodeDelta[n].X + dir.X * step,
                                nodeDelta[n].Y + dir.Y * step,
                                nodeDelta[n].Z + dir.Z * step);

            // Clamped by TOTAL magnitude, so holding the button cannot walk a vertex away indefinitely and
            // deflating back through zero is still free.
            float len = MathF.Sqrt(next.X * next.X + next.Y * next.Y + next.Z * next.Z);
            if (len > MaxDisplacement)
            {
                float k = MaxDisplacement / len;
                next = new Vec3(next.X * k, next.Y * k, next.Z * k);
            }

            nodeDelta[n] = next;
            nodeWeight[n] = MathF.Max(nodeWeight[n], w);
            moved++;
        }

        if (moved > 0)
        {
            Dirty = true;
            // The per-vertex view has to follow now, not at the end of the stroke: it is what the preview
            // draws, and a brush whose effect only appears when you let go is unusable. One pass over the
            // vertices, which is nothing beside the fill the viewport is about to do anyway.
            //
            // Normals are NOT refreshed here. The viewport shades from face normals it computes itself, so
            // the preview is correct without them, and the stored ones are only needed at write time.
            Spread();
        }
        return moved;
    }

    /// <summary>
    /// Finish a stroke: limit the slope, pull back anything that folded, rebuild the normals, and re-aim the
    /// brush at the surface as it now is.
    /// <para/>
    /// At the END of a stroke rather than per dab, and that is a correctness point as much as a cost one. The
    /// unfold halves a displacement back wherever a triangle inverted; run per frame it would fight the user,
    /// pulling in the very thing they are pushing out while they hold the button.
    /// </summary>
    public void EndStroke()
    {
        var touched = stroke;
        stroke = null;
        if (touched is { Count: > 0 }) undo.Add(touched);
        Settle();
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        var last = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        foreach (var (n, was) in last) { nodeDelta[n] = was.Delta; nodeWeight[n] = was.Weight; }
        Settle();
        Dirty = undo.Count > 0 || nodeDelta.Any(d => d.X != 0f || d.Y != 0f || d.Z != 0f);
    }

    public void Reset()
    {
        Array.Clear(nodeDelta);
        Array.Clear(nodeWeight);
        undo.Clear();
        stroke = null;
        Dirty = false;
        Settle();
    }

    private void Settle()
    {
        // Both passes at NODE level. LimitSlopeVector only ever pulls a displacement toward zero, so it
        // cannot invent movement on an untouched node or unpin the boundary.
        SecondSkinWriter.LimitSlopeVector(nodeDelta, nodeAt, adj, nodeCount, MaxSlope);
        SecondSkinWriter.UnfoldTriangles(nodeAt, nodeDelta, nodeTris, null);

        // Frozen nodes again, AFTER both passes. Neither pass knows about the boundary, and the slope limit
        // reaches across an edge — so a rim node beside a heavily brushed one can be dragged off zero by a
        // pass whose whole job is to reduce differences.
        for (int n = 0; n < nodeCount; n++)
            if (frozen[n] || locked[n]) { nodeDelta[n] = default; nodeWeight[n] = 0f; }

        Spread();

        vertNrm = SecondSkinWriter.RelaxedNormals(
            basePos, baseNrm, vertDelta, nodeOf, nodeWeight, nodeNormal, tris);

        // Re-aim for the next stroke, from the surface as it now is — see nodeNormal. Only where something
        // moved: elsewhere the authored normal is still the best answer and re-deriving it would drift.
        var accum = new Vec3[nodeCount];
        for (int i = 0; i < vertDelta.Length; i++)
        {
            int n = nodeOf[i];
            if (nodeWeight[n] <= 0f) continue;
            accum[n] = new Vec3(accum[n].X + vertNrm[i].X, accum[n].Y + vertNrm[i].Y, accum[n].Z + vertNrm[i].Z);
        }
        for (int n = 0; n < nodeCount; n++)
        {
            if (nodeWeight[n] <= 0f) continue;
            var u = Unit(accum[n]);
            if (u.X != 0f || u.Y != 0f || u.Z != 0f) nodeNormal[n] = u;
        }
    }

    /// <summary>Push the node displacements out to every vertex, and re-measure the worst of them.</summary>
    private void Spread()
    {
        float worst = 0f;
        for (int i = 0; i < vertDelta.Length; i++)
        {
            var d = nodeDelta[nodeOf[i]];
            vertDelta[i] = d;
            float len = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            if (len > worst) worst = len;
        }
        Worst = worst;
        positionsStale = true;
    }

    private float[]? positionCache;
    private bool positionsStale = true;

    /// <summary>
    /// The deformed model, laid out exactly like <see cref="ModelParts.Positions"/> so the viewport can
    /// rasterise it without knowing an edit happened.
    /// <para/>
    /// One buffer, refilled — this is asked for on every frame of a stroke, and a fresh array per frame on a
    /// 60,000-vertex coat is 700 KB of garbage per frame for no benefit. The caller only ever reads it.
    /// </summary>
    public float[] Positions()
    {
        positionCache ??= new float[basePos.Length * 3];
        if (!positionsStale) return positionCache;

        for (int i = 0; i < basePos.Length; i++)
        {
            positionCache[i * 3] = basePos[i].X + vertDelta[i].X;
            positionCache[i * 3 + 1] = basePos[i].Y + vertDelta[i].Y;
            positionCache[i * 3 + 2] = basePos[i].Z + vertDelta[i].Z;
        }
        positionsStale = false;
        return positionCache;
    }

    /// <summary>Per-vertex displacement, indexed as <see cref="ModelParts.Positions"/> is.</summary>
    public Vec3 DeltaAt(int vertex) => vertDelta[vertex];

    /// <summary>Per-vertex normal after the edit, indexed as <see cref="ModelParts.Positions"/> is.</summary>
    public Vec3 NormalAt(int vertex) => vertNrm[vertex];

    private static Vec3 Unit(Vec3 v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len > 1e-6f ? new Vec3(v.X / len, v.Y / len, v.Z / len) : default;
    }
}
