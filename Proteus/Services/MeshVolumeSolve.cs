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
    /// a garment into a sphere.
    /// <para/>
    /// Ten centimetres. It started at five millimetres, on the reasoning that clearing a body poking through
    /// needs only a fraction of that — and in use that read as the brush refusing to pull cloth away from
    /// the skin, because pulling a garment visibly OFF the body is a different job from clearing a clip and
    /// wants far more room. Undo per stroke is what makes a generous ceiling safe.
    /// </summary>
    public const float MaxDisplacement = 0.1f;

    /// <summary>
    /// Cell size of the grid skin points are bucketed into when aiming the pull, in metres. Big enough that
    /// the 3×3×3 neighbourhood of a cloth point reliably holds the skin beneath it; small enough that a point
    /// does not scan the whole body.
    /// </summary>
    private const float SkinCell = 0.03f;

    /// <summary>How many cells out the skin search reaches, in turn: a hand's width, a forearm's, a long skirt's.</summary>
    private static readonly int[] SkinReach = [1, 3, 8];

    /// <summary>
    /// Rounds of the light smoothing run over each finished stroke — see <see cref="SmoothStroke"/>. Each
    /// round is a Taubin pair: a step toward the neighbours' average, then a slightly larger step back out.
    /// </summary>
    private const int SmoothRounds = 2;
    private const float SmoothLambda = 0.5f, SmoothMu = -0.53f;
    private static readonly float[] TaubinSteps = [SmoothLambda, SmoothMu];

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
    /// Each node's authored normal: its vertices' normals, averaged and renormalised, or the faces' when the
    /// mesh declares none. What the normal rebuild blends back toward — NOT the direction the brush pulls,
    /// which is <see cref="pullDir"/>.
    /// </summary>
    private readonly Vec3[] nodeNormal;

    /// <summary>
    /// The direction the brush moves each node: straight AWAY FROM THE NEAREST SKIN, where the model carries
    /// skin, and along the node's normal only where it does not.
    /// <para/>
    /// Pulling along each point's own normal is what Outfit Studio's inflate does, and on clothing it is
    /// wrong in two ways that were both seen in game. A hem's normals point DOWN, so pulling a pair of shorts
    /// out moved the hem down the leg instead of away from it. And cloth is a thin shell with an inside and
    /// an outside whose normals point opposite ways, so pulling moved the two layers apart — outside out,
    /// inside into the leg — and anything that then evened out neighbours dragged the whole patch back when
    /// the stroke ended. Away-from-skin gives both layers and the hem between them one shared direction,
    /// which is the direction the user means by "pull away".
    /// <para/>
    /// Aimed once, from the author's positions: skin never moves, and cloth pulled along the line away from
    /// it is still on that line.
    /// </summary>
    private readonly Vec3[] pullDir;

    /// <summary>Nodes whose pull was aimed from skin rather than from their normal.</summary>
    private readonly bool[] aimedFromSkin;

    /// <summary>The pulls as first aimed, so starting over also forgets the re-aiming strokes did.</summary>
    private readonly Vec3[] initialPull;

    /// <summary>
    /// Nodes belonging to SKIN, which the brush never moves. The tool is for pushing clothing clear of the
    /// body, and a garment model routinely carries the body underneath it — push that out along with the
    /// cloth and the clipping simply moves with it.
    /// <para/>
    /// Skin is decided by material, with <see cref="SecondSkinWriter.IsBodySkinMaterial"/>: the same test
    /// the second skin uses, measured against real bodies, so smallclothes, nails and piercings on a body
    /// model are not mistaken for it. A cloth vertex sitting exactly on a skin vertex welds into the same
    /// node and is held with it — moving only one copy of a shared point would split the two apart.
    /// </summary>
    private readonly bool[] skin;

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

        // Open edges MOVE. They used to be pinned — an edge used by one face can be the seam where the next
        // model file continues, and moving one side of that tears the join — but a garment's hem, neckline and
        // sleeve ends are open edges too, and pinning them made the brush refuse to pull cloth away anywhere
        // near one: the slope limit then held every node a few triangles in to a few millimetres. A torn seam
        // is visible and undoable per stroke; a brush that will not pull a hem out has no workaround.
        skin = new bool[nodeCount];
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
                if (v >= 0 && v < vc) skin[nodeOf[v]] = true;
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

        pullDir = (Vec3[])nodeNormal.Clone();
        aimedFromSkin = new bool[nodeCount];
        AimAwayFromSkin();
        initialPull = (Vec3[])pullDir.Clone();

        nodeDelta = new Vec3[nodeCount];
        nodeWeight = new float[nodeCount];
        vertDelta = new Vec3[vc];
        vertNrm = (Vec3[])baseNrm.Clone();

        MeanEdge = SecondSkinWriter.MeanEdgeLength(nodeAt, adj, Enumerable.Range(0, nodeCount).ToList());
    }

    /// <summary>
    /// Point every cloth node's pull away from the skin around it — see <see cref="pullDir"/>.
    /// <para/>
    /// The direction is the field of the nearby skin points, each pushing the cloth point away with a weight
    /// of one over the distance squared, rather than the direction to the single nearest one. Nearest-point
    /// jitters from vertex to vertex of the skin mesh — a few degrees either way at every step — and a
    /// displacement tens of millimetres long turns a few degrees into a visibly rough surface. The field is
    /// smooth by construction, is dominated by the skin right under the cloth, and where the cloth hangs
    /// between two limbs points away from both.
    /// <para/>
    /// Searched outward in rings so a point close to the body pays for its own cell neighbourhood only, and a
    /// point hanging far off it — the flare of a skirt — still finds skin. A model with no skin at all leaves
    /// every direction on its normal.
    /// </summary>
    private void AimAwayFromSkin()
    {
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int n = 0; n < nodeCount; n++)
        {
            if (!skin[n]) continue;
            var key = Cell(nodeAt[n]);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(n);
        }
        if (grid.Count == 0) return;

        for (int n = 0; n < nodeCount; n++)
        {
            if (skin[n]) continue;
            var p = nodeAt[n];
            var (cx, cy, cz) = Cell(p);
            double fx = 0, fy = 0, fz = 0;
            bool found = false;

            // Rings of 1, 3 and 8 cells: a hand's width, then a forearm's, then far enough for a long skirt.
            foreach (int reach in SkinReach)
            {
                for (int x = cx - reach; x <= cx + reach; x++)
                for (int y = cy - reach; y <= cy + reach; y++)
                for (int z = cz - reach; z <= cz + reach; z++)
                {
                    if (!grid.TryGetValue((x, y, z), out var near)) continue;
                    foreach (int s in near)
                    {
                        double dx = p.X - nodeAt[s].X, dy = p.Y - nodeAt[s].Y, dz = p.Z - nodeAt[s].Z;
                        double d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 < 1e-12) continue;
                        double w = 1.0 / (d2 * Math.Sqrt(d2));      // unit direction over distance squared
                        fx += dx * w; fy += dy * w; fz += dz * w;
                        found = true;
                    }
                }
                if (found) break;
                fx = fy = fz = 0;
            }
            if (!found) continue;

            var dir = Unit(new Vec3((float)fx, (float)fy, (float)fz));
            if (dir.X == 0f && dir.Y == 0f && dir.Z == 0f) continue;
            pullDir[n] = dir;
            aimedFromSkin[n] = true;
        }

        static (int, int, int) Cell(Vec3 v)
            => ((int)MathF.Floor(v.X / SkinCell), (int)MathF.Floor(v.Y / SkinCell), (int)MathF.Floor(v.Z / SkinCell));
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
            if (skin[n]) continue;

            float dx = nodeAt[n].X - c.X, dy = nodeAt[n].Y - c.Y, dz = nodeAt[n].Z - c.Z;
            float d2 = dx * dx + dy * dy + dz * dz;
            if (d2 >= r2) continue;

            float w = Falloff(MathF.Sqrt(d2) / radius);
            if (w <= 0f) continue;

            var dir = pullDir[n];
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
        if (touched is { Count: > 0 })
        {
            undo.Add(touched);
            SmoothStroke(touched.Keys);
        }
        Settle();
    }

    /// <summary>
    /// Smooth the displacement of the nodes one stroke touched, lightly.
    /// <para/>
    /// TAUBIN, not a plain average, and that choice is the whole difference between this and the slope limit
    /// it replaced. Averaging a displacement toward its neighbours also shrinks it — the peak of a pull sinks
    /// toward the untouched cloth around it — and that shrinking was exactly the snap-back seen in game when
    /// the button came up. A Taubin pair steps toward the average and then a touch further back out, which
    /// takes the roughness out of a stroke (the jitter of dabs landing frame by frame) while leaving its bulk
    /// where the user put it.
    /// <para/>
    /// Only the stroke's own nodes are written, so each stroke is smoothed once rather than every earlier one
    /// being smoothed again each time — and so undo, which restores exactly those nodes, stays exact.
    /// Neighbours outside the stroke are read, so its rim blends into what is already there. Skin nodes are
    /// neither written nor, since their displacement is zero, able to drag anything along.
    /// </summary>
    private void SmoothStroke(IEnumerable<int> touched)
    {
        var nodes = touched.Where(n => !skin[n] && adj[n].Count > 0).ToArray();
        if (nodes.Length == 0) return;

        var next = new Vec3[nodes.Length];
        for (int round = 0; round < SmoothRounds; round++)
        foreach (float step in TaubinSteps)
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                int n = nodes[i];
                float sx = 0f, sy = 0f, sz = 0f;
                foreach (int k in adj[n]) { sx += nodeDelta[k].X; sy += nodeDelta[k].Y; sz += nodeDelta[k].Z; }
                float inv = 1f / adj[n].Count;
                var d = nodeDelta[n];
                next[i] = new Vec3(d.X + (sx * inv - d.X) * step,
                                   d.Y + (sy * inv - d.Y) * step,
                                   d.Z + (sz * inv - d.Z) * step);
            }
            for (int i = 0; i < nodes.Length; i++) nodeDelta[nodes[i]] = next[i];
        }
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
        Array.Copy(initialPull, pullDir, nodeCount);
        undo.Clear();
        stroke = null;
        Dirty = false;
        Settle();
    }

    private void Settle()
    {
        // NO SLOPE LIMIT. It used to run here, and it is what made a pull snap back on release: it evens out
        // neighbours by pulling the larger displacement toward the smaller, and on a hem or a thin double-sided
        // garment neighbouring nodes legitimately differ a lot — so it quietly took back most of what the user
        // had just painted, only once they let go. Smoothing is SmoothStroke's job now, which does not shrink;
        // what the limit guarded against, faces passing through one another, is the unfold's.
        SecondSkinWriter.UnfoldTriangles(nodeAt, nodeDelta, nodeTris, null);

        // Skin again, after the unfold, which knows nothing about it.
        for (int n = 0; n < nodeCount; n++)
            if (skin[n]) { nodeDelta[n] = default; nodeWeight[n] = 0f; }

        Spread();

        vertNrm = SecondSkinWriter.RelaxedNormals(
            basePos, baseNrm, vertDelta, nodeOf, nodeWeight, nodeNormal, tris);

        // Re-aim, for the next stroke, the pulls that follow a normal — from the surface as it now is. Inside a
        // concavity normals point sideways ACROSS the gap, so repeatedly pulling one spot along the directions
        // it started with drives the walls apart instead of outward. Pulls aimed from skin are left alone:
        // the skin has not moved, and a point pulled away from it is still on the same line.
        var accum = new Vec3[nodeCount];
        for (int i = 0; i < vertDelta.Length; i++)
        {
            int n = nodeOf[i];
            if (nodeWeight[n] <= 0f || aimedFromSkin[n]) continue;
            accum[n] = new Vec3(accum[n].X + vertNrm[i].X, accum[n].Y + vertNrm[i].Y, accum[n].Z + vertNrm[i].Z);
        }
        for (int n = 0; n < nodeCount; n++)
        {
            if (nodeWeight[n] <= 0f || aimedFromSkin[n]) continue;
            var u = Unit(accum[n]);
            if (u.X != 0f || u.Y != 0f || u.Z != 0f) pullDir[n] = u;
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
