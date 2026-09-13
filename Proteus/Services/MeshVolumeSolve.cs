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
    /// One dab of the relax brush: smooth the surface inside the brush toward its neighbours, strongest in
    /// the middle and fading to nothing at the rim.
    /// <para/>
    /// It smooths the surface AS IT NOW IS — the author's geometry plus every pull so far — so it takes out
    /// lumps the garment shipped with, not only the ones the brush made. The change still lands in the
    /// displacement, which is what keeps undo, the autosave, the writer and the stored extents all working
    /// without knowing a different brush was used.
    /// <para/>
    /// A PLAIN AVERAGE per dab, on positions, the way 3ds Max's relax works: each node steps toward its
    /// neighbours' average and nothing steps it back. That shrinks — a curved surface flattens and pulls in
    /// on itself, and cloth over a curve sinks toward the body — and that is deliberate: it is what users
    /// coming from Max expect the brush to do. The stroke-end smoothing of a pull (<see cref="SmoothStroke"/>)
    /// is still a Taubin pair, because there shrinking would undo the pull. The full 3-D step, tangential
    /// part included: most of what makes a relax look smooth is tangential, and a normal-only step leaves
    /// the facets.
    /// <para/>
    /// The step reads every node in the brush before writing any, so the result does not depend on the
    /// order nodes happen to be numbered in. Skin never moves, but a skin neighbour is still read — it is
    /// real surface, and cloth next to it should blend into it rather than into nothing. Running per dab it
    /// cannot reach convergence (a relaxed patch at its fixed point is a bowl), because the user lifts the
    /// brush first: the stroke is the stopping rule.
    /// </summary>
    /// <param name="rate">0..1: how far each dab moves toward the average, at the middle of the brush.</param>
    /// <returns>How many nodes moved.</returns>
    public int Relax(Vector3 centre, float radius, float rate)
    {
        if (radius <= 0f || rate <= 0f) return 0;
        stroke ??= [];

        var c = new Vec3(centre.X, centre.Y, centre.Z);
        float r2 = radius * radius;

        var nodes = brushNodes;
        var weights = brushWeights;
        nodes.Clear();
        weights.Clear();
        for (int n = 0; n < nodeCount; n++)
        {
            if (skin[n] || adj[n].Count == 0) continue;
            float dx = nodeAt[n].X - c.X, dy = nodeAt[n].Y - c.Y, dz = nodeAt[n].Z - c.Z;
            float d2 = dx * dx + dy * dy + dz * dz;
            if (d2 >= r2) continue;
            float w = Falloff(MathF.Sqrt(d2) / radius);
            if (w <= 0f) continue;
            nodes.Add(n);
            weights.Add(w);
        }
        if (nodes.Count == 0) return 0;

        // Recorded before the first change of this stroke — see Paint.
        foreach (int n in nodes) stroke.TryAdd(n, (nodeDelta[n], nodeWeight[n]));

        var next = Scratch(ref relaxNext, nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            int n = nodes[i];
            float sx = 0f, sy = 0f, sz = 0f;
            foreach (int k in adj[n])
            {
                sx += nodeAt[k].X + nodeDelta[k].X;
                sy += nodeAt[k].Y + nodeDelta[k].Y;
                sz += nodeAt[k].Z + nodeDelta[k].Z;
            }
            float inv = 1f / adj[n].Count;
            var d = nodeDelta[n];
            float px = nodeAt[n].X + d.X, py = nodeAt[n].Y + d.Y, pz = nodeAt[n].Z + d.Z;

            // One step toward the average, half the way at full rate — Max's default relax value of 0.5.
            float f = SmoothLambda * rate * weights[i];
            next[i] = new Vec3(d.X + (sx * inv - px) * f,
                               d.Y + (sy * inv - py) * f,
                               d.Z + (sz * inv - pz) * f);
        }
        for (int i = 0; i < nodes.Count; i++) nodeDelta[nodes[i]] = next[i];

        for (int i = 0; i < nodes.Count; i++)
        {
            int n = nodes[i];
            var d = nodeDelta[n];
            float len = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            if (len > MaxDisplacement)
            {
                float k = MaxDisplacement / len;
                nodeDelta[n] = new Vec3(d.X * k, d.Y * k, d.Z * k);
            }
            nodeWeight[n] = MathF.Max(nodeWeight[n], weights[i]);
        }

        Dirty = true;
        Spread();
        return nodes.Count;
    }

    // Per-dab working storage for Relax and Bridge, kept between dabs. Both run every frame of a stroke on the
    // render thread, and fresh arrays, lists and a dictionary each time were steady garbage while painting.
    private readonly List<int> brushNodes = [];
    private readonly List<float> brushWeights = [];
    private Vec3[]? relaxNext;
    private float[]? bridgePx, bridgePy, bridgePh, bridgeGx, bridgeGy, bridgeHc, bridgeLift, bridgeS, bridgeGap;
    private int[]? bridgePrevOut;
    private bool[]? bridgeOutward;
    private readonly Dictionary<int, List<int>> bridgeStrips = [];
    private readonly List<int> bridgeHull = [];
    private Comparison<int>? bridgeOrder;

    /// <summary>The current bridge stroke's outward axis, fixed on its first dab; null between strokes.</summary>
    private Vec3? bridgeAxis;

    /// <summary>A buffer at least <paramref name="count"/> long with its first <paramref name="count"/> entries
    /// cleared, reusing <paramref name="buffer"/> when it is big enough.</summary>
    private static T[] Scratch<T>(ref T[]? buffer, int count)
    {
        if (buffer == null || buffer.Length < count)
            buffer = new T[Math.Max(count, (buffer?.Length ?? 0) * 2)];
        else
            Array.Clear(buffer, 0, count);
        return buffer;
    }

    /// <summary>Directions the bridge reads chords along, half a turn in eight steps.</summary>
    private static readonly (float Cos, float Sin)[] BridgeDirections =
        [.. Enumerable.Range(0, 8).Select(i => (MathF.Cos(i * MathF.PI / 8f), MathF.Sin(i * MathF.PI / 8f)))];

    /// <summary>
    /// One dab of the bridge brush: stretch the cloth straight across a hollow — a cleft, a crease, the dip
    /// between two cheeks — instead of letting it follow the body down into it. Only ever lifts.
    /// <para/>
    /// A CONSTRUCTION, NOT A RELAXATION, and that is the lesson this is built on. The project already tried
    /// to span a cleavage by relaxing, three ways, and every one failed for the same reason: a cleft is a
    /// SADDLE — dished across it, bulging along it — and any isotropic smoothing sees those two curvatures
    /// cancel and finds almost nothing to do. What works is to take the spanning surface directly
    /// (<c>SecondSkinWriter.ChordTarget</c> does it for the cleavage) and lift toward it.
    /// <para/>
    /// Here the spanning surface is the upper convex hull of the cloth under the brush, seen along the brush's
    /// outward axis: the tightest surface stretched over its high points. A convex region is already on it —
    /// each cheek keeps its own curve — and only a hollow sits below it and rises. It is read as chords: for
    /// eight directions across the brush, the cloth is cut into strips a couple of mesh edges wide, each
    /// strip's upper hull is taken in 1-D, and a point's target is the highest of those chords over it. Every
    /// chord lies inside the true hull, so this never lifts past it; and a chord only exists between points
    /// either side, so it cannot extrapolate — the failure that ran a fitted line away to 894 units.
    /// <para/>
    /// THICKNESS IS KEPT. A garment with a lining is two layers a couple of millimetres apart, and lifting
    /// every point to the hull would press the lining up into the outer surface. So the hull is built from
    /// the OUTWARD-FACING cloth only — a lining faces the body — and each inward-facing point is lifted by
    /// the same amount as the outward point nearest it along its chord, which moves the two layers together.
    /// <para/>
    /// Not by "the top of the cloth near this point", which was the first construction and is wrong on a
    /// single layer: there the nearby top is just the rim of the hollow, so every point within reach of the
    /// rim saw no gap and never rose — and the steep walls of a cleft are exactly that case.
    /// <para/>
    /// The outward axis is the average of the pull directions under the brush on the stroke's first dab,
    /// held for the rest of the stroke; they point away from skin and so agree across both layers. Where they do not agree — a model with no skin, whose inside and
    /// outside normals cancel — it falls back to <paramref name="toViewer"/>: the side being painted is the
    /// side being looked at.
    /// </summary>
    /// <param name="rate">0..1: how much of the remaining gap each dab closes, at the middle of the brush.</param>
    /// <param name="toViewer">Unit direction from the model toward the camera.</param>
    /// <returns>How many nodes moved.</returns>
    public int Bridge(Vector3 centre, float radius, float rate, Vector3 toViewer)
    {
        if (radius <= 0f || rate <= 0f) return 0;
        stroke ??= [];

        var c = new Vec3(centre.X, centre.Y, centre.Z);
        float r2 = radius * radius;

        var nodes = brushNodes;
        var weights = brushWeights;
        nodes.Clear();
        weights.Clear();
        float ax = 0f, ay = 0f, az = 0f, wsum = 0f;
        for (int n = 0; n < nodeCount; n++)
        {
            if (skin[n]) continue;
            float dx = nodeAt[n].X - c.X, dy = nodeAt[n].Y - c.Y, dz = nodeAt[n].Z - c.Z;
            float d2 = dx * dx + dy * dy + dz * dz;
            if (d2 >= r2) continue;
            float w = Falloff(MathF.Sqrt(d2) / radius);
            if (w <= 0f) continue;
            nodes.Add(n);
            weights.Add(w);
            ax += pullDir[n].X * w; ay += pullDir[n].Y * w; az += pullDir[n].Z * w;
            wsum += w;
        }
        if (nodes.Count < 3) return 0;

        // The outward axis — see the summary for why the viewer is only the fallback — taken on the stroke's
        // first dab and HELD for the rest of it. Re-read per dab it swings as the brush crosses from one cheek
        // to the other, and points lifted along several directions can turn a triangle over, where lifting
        // along one cannot (see EndStroke).
        if (bridgeAxis is not { } axis)
        {
            float alen = MathF.Sqrt(ax * ax + ay * ay + az * az);
            axis = alen >= 0.5f * wsum
                ? new Vec3(ax / alen, ay / alen, az / alen)
                : Unit(new Vec3(toViewer.X, toViewer.Y, toViewer.Z));
            if (axis.X == 0f && axis.Y == 0f && axis.Z == 0f) return 0;
            bridgeAxis = axis;
        }

        // Two directions across the axis, so each point has a place on the brush's face and a height along it.
        var seed = MathF.Abs(axis.X) < 0.9f ? new Vec3(1f, 0f, 0f) : new Vec3(0f, 1f, 0f);
        var u = Unit(new Vec3(seed.Y * axis.Z - seed.Z * axis.Y,
                              seed.Z * axis.X - seed.X * axis.Z,
                              seed.X * axis.Y - seed.Y * axis.X));
        var v = new Vec3(axis.Y * u.Z - axis.Z * u.Y, axis.Z * u.X - axis.X * u.Z, axis.X * u.Y - axis.Y * u.X);

        int count = nodes.Count;
        var px = Scratch(ref bridgePx, count);
        var py = Scratch(ref bridgePy, count);
        var ph = Scratch(ref bridgePh, count);
        var outward = Scratch(ref bridgeOutward, count);
        int outwardCount = 0;
        for (int i = 0; i < count; i++)
        {
            int n = nodes[i];
            var p = new Vec3(nodeAt[n].X + nodeDelta[n].X, nodeAt[n].Y + nodeDelta[n].Y, nodeAt[n].Z + nodeDelta[n].Z);
            px[i] = p.X * u.X + p.Y * u.Y + p.Z * u.Z;
            py[i] = p.X * v.X + p.Y * v.Y + p.Z * v.Z;
            ph[i] = p.X * axis.X + p.Y * axis.Y + p.Z * axis.Z;

            // Facing out of the body, from the AUTHORED normal — pullDir cannot tell, since it points away
            // from skin on both layers alike. Generous, so a cleft's walls, which face mostly sideways, still
            // count as outer cloth.
            var nn = nodeNormal[n];
            outward[i] = nn.X * axis.X + nn.Y * axis.Y + nn.Z * axis.Z > -0.2f;
            if (outward[i]) outwardCount++;
        }
        // A model whose normals are all flipped, or that has none, would leave nothing to build a hull from.
        if (outwardCount < 3) Array.Fill(outward, true, 0, count);

        // Strips one mesh edge wide — narrow enough to be one line across the hollow, wide enough to hold
        // points from both sides of it.
        //
        // A strip of finite width holds points from neighbouring rows, and on a SLOPED surface those rows sit
        // at different heights: the row further up the slope reads as a chord over the one below it, and the
        // brush lifts it. That is first-order in the strip's width — 4 mm on the flank of a test dome — and a
        // cheek's flank is steep, so left alone it inflates exactly the rounded areas the brush must not touch.
        // So each point's height is corrected back to its strip's centre line using the surface's own slope
        // across the strip, from a gradient fitted to its mesh neighbours. What is left is second-order, and
        // the small tolerance absorbs it.
        float strip = MathF.Max(MeanEdge, 1e-4f);
        float tolerance = 0.05f * strip;

        // Each point's height gradient over the brush's face, least squares over its mesh neighbours.
        var gx = Scratch(ref bridgeGx, count);
        var gy = Scratch(ref bridgeGy, count);
        for (int i = 0; i < count; i++)
        {
            int n = nodes[i];
            float sxx = 0f, sxy = 0f, syy = 0f, sxh = 0f, syh = 0f;
            foreach (int k in adj[n])
            {
                var q = new Vec3(nodeAt[k].X + nodeDelta[k].X, nodeAt[k].Y + nodeDelta[k].Y, nodeAt[k].Z + nodeDelta[k].Z);
                float dx = q.X * u.X + q.Y * u.Y + q.Z * u.Z - px[i];
                float dy = q.X * v.X + q.Y * v.Y + q.Z * v.Z - py[i];
                float dh = q.X * axis.X + q.Y * axis.Y + q.Z * axis.Z - ph[i];
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy; sxh += dx * dh; syh += dy * dh;
            }
            float det = sxx * syy - sxy * sxy;
            if (MathF.Abs(det) < 1e-18f) continue;
            // Clamped: on a cleft's near-vertical wall the fit runs to huge slopes, and a huge slope times a
            // few millimetres of strip is noise, not a correction.
            gx[i] = Math.Clamp((sxh * syy - syh * sxy) / det, -3f, 3f);
            gy[i] = Math.Clamp((syh * sxx - sxh * sxy) / det, -3f, 3f);
        }
        var hc = Scratch(ref bridgeHc, count);
        var lift = Scratch(ref bridgeLift, count);
        var s = Scratch(ref bridgeS, count);
        var gapOf = Scratch(ref bridgeGap, count);
        var prevOut = Scratch(ref bridgePrevOut, count);
        var strips = bridgeStrips;
        var hull = bridgeHull;
        // Built once: a lambda over this dab's arrays would be a fresh delegate for every strip of every dab.
        bridgeOrder ??= (a, b) => bridgeS![a] != bridgeS[b]
            ? bridgeS[a].CompareTo(bridgeS[b])
            : bridgeHc![a].CompareTo(bridgeHc[b]);

        foreach (var (cos, sin) in BridgeDirections)
        {
            // The lists are kept and emptied rather than dropped, so a dab allocates nothing once warm.
            foreach (var list in strips.Values) list.Clear();
            for (int i = 0; i < count; i++)
            {
                s[i] = px[i] * cos + py[i] * sin;
                float t = -px[i] * sin + py[i] * cos;
                int bin = (int)MathF.Floor(t / strip);

                // Height carried back to the strip's centre line along the surface's slope across the strip —
                // see the tolerance above.
                float slopeAcross = -gx[i] * sin + gy[i] * cos;
                hc[i] = ph[i] - slopeAcross * (t - (bin + 0.5f) * strip);

                if (!strips.TryGetValue(bin, out var list)) strips[bin] = list = [];
                list.Add(i);
            }

            foreach (var (_, members) in strips)
            {
                if (members.Count < 3) continue;
                members.Sort(bridgeOrder);

                // Upper hull of (along, height), left to right, over the OUTWARD-facing cloth only.
                hull.Clear();
                foreach (int i in members)
                {
                    if (!outward[i]) continue;
                    while (hull.Count >= 2)
                    {
                        int o = hull[^2], a = hull[^1];
                        float cross = (s[a] - s[o]) * (hc[i] - hc[o]) - (hc[a] - hc[o]) * (s[i] - s[o]);
                        if (cross < 0f) break;   // a clockwise turn keeps the hull upper
                        hull.RemoveAt(hull.Count - 1);
                    }
                    hull.Add(i);
                }
                if (hull.Count < 2) continue;

                // Each outward point's gap to its chord.
                Array.Clear(gapOf, 0, members.Count);
                int seg = 0;
                for (int m = 0; m < members.Count; m++)
                {
                    int i = members[m];
                    if (!outward[i]) continue;
                    while (seg < hull.Count - 2 && s[hull[seg + 1]] < s[i]) seg++;
                    int h0 = hull[seg], h1 = hull[seg + 1];
                    float span = s[h1] - s[h0];
                    float chord = span > 1e-9f
                        ? hc[h0] + (hc[h1] - hc[h0]) * Math.Clamp((s[i] - s[h0]) / span, 0f, 1f)
                        : MathF.Max(hc[h0], hc[h1]);

                    // Never above the chord's ends as they REALLY stand. The slope correction is only as good
                    // as the fit under it, and beside a near-vertical wall the fit sits at its clamp: the rim
                    // then reads several millimetres higher than it is, every rim point lifts toward that
                    // phantom, and — the heights being re-read each dab — the next dab finds it higher still,
                    // until the rim hits MaxDisplacement. Capped by the real heights, no point can end up above
                    // the highest real point of its strip, so a dab has nothing to feed on.
                    float roof = MathF.Max(ph[h0], ph[h1]);
                    gapOf[m] = MathF.Max(0f, MathF.Min(chord - hc[i], roof - ph[i]) - tolerance);
                }

                // An inward-facing point — a lining — takes the gap of the outward point nearest it along the
                // chord, so both layers move by the same amount. See the summary.
                for (int m = 0, last = -1; m < members.Count; m++)
                {
                    if (outward[members[m]]) last = m;
                    prevOut[m] = last;
                }
                for (int m = members.Count - 1, next = -1; m >= 0; m--)
                {
                    int i = members[m];
                    if (outward[i]) { next = m; continue; }
                    int p = prevOut[m];
                    int pick = p < 0 ? next
                             : next < 0 ? p
                             : s[i] - s[members[p]] <= s[members[next]] - s[i] ? p : next;
                    if (pick >= 0) gapOf[m] = gapOf[pick];
                }

                for (int m = 0; m < members.Count; m++)
                    if (gapOf[m] > lift[members[m]]) lift[members[m]] = gapOf[m];
            }
        }

        int moved = 0;
        for (int i = 0; i < count; i++)
        {
            float step = lift[i] * rate * weights[i];
            if (step <= 1e-7f) continue;
            int n = nodes[i];

            stroke.TryAdd(n, (nodeDelta[n], nodeWeight[n]));   // before the first change — see Paint

            var next = new Vec3(nodeDelta[n].X + axis.X * step,
                                nodeDelta[n].Y + axis.Y * step,
                                nodeDelta[n].Z + axis.Z * step);
            float len = MathF.Sqrt(next.X * next.X + next.Y * next.Y + next.Z * next.Z);
            if (len > MaxDisplacement)
            {
                float k = MaxDisplacement / len;
                next = new Vec3(next.X * k, next.Y * k, next.Z * k);
            }
            nodeDelta[n] = next;
            nodeWeight[n] = MathF.Max(nodeWeight[n], weights[i]);
            moved++;
        }

        if (moved > 0)
        {
            Dirty = true;
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
    /// <param name="bridge">The stroke was the bridge brush, which is finished differently — see below.</param>
    public void EndStroke(bool bridge = false)
    {
        var touched = stroke;
        stroke = null;
        bridgeAxis = null;
        if (touched is not { Count: > 0 })
        {
            Settle(null);
            return;
        }
        undo.Add(touched);

        // A BRIDGE STROKE IS NOT SMOOTHED, and its fold check lets a triangle collapse. Both the smoothing and
        // the ordinary check suit a pull, and both undid a bridge the moment the button came up, which read in
        // game as it snapping back.
        //
        // The smoothing works on the displacement field, and a bridge's displacement is a narrow band — the
        // crack rises, the cheeks either side do not move — which is exactly the sharp feature a low-pass
        // filter flattens. The ordinary check halves any triangle whose area collapses, and filling a steep
        // crack is MEANT to lay its walls down flat into the span. What the bridge check still catches is a
        // triangle that has turned over with area left to show it — see UnfoldStroke.
        if (!bridge) SmoothStroke(touched);
        Settle(touched, allowCollapse: bridge);
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
    /// Only what THIS STROKE ADDED is smoothed, and only the stroke's own nodes are written. Smoothing the whole
    /// displacement under the stroke instead re-smoothed every earlier stroke there, and a pull or relax
    /// over a bridge flattened the bridge's narrow band of lift and dropped it back into the crack. Nodes the
    /// stroke did not move add nothing, so the stroke's rim blends out to zero, and undo — which restores
    /// exactly the stroke's nodes — stays exact. Skin is never written.
    /// </summary>
    private void SmoothStroke(Dictionary<int, (Vec3 Delta, float Weight)> strokeStart)
    {
        var nodes = strokeStart.Keys.Where(n => !skin[n] && adj[n].Count > 0).ToArray();
        if (nodes.Length == 0) return;

        var added = new Vec3[nodeCount];
        foreach (var (n, was) in strokeStart)
            added[n] = new Vec3(nodeDelta[n].X - was.Delta.X, nodeDelta[n].Y - was.Delta.Y, nodeDelta[n].Z - was.Delta.Z);

        var next = new Vec3[nodes.Length];
        for (int round = 0; round < SmoothRounds; round++)
        foreach (float step in TaubinSteps)
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                int n = nodes[i];
                float sx = 0f, sy = 0f, sz = 0f;
                foreach (int k in adj[n]) { sx += added[k].X; sy += added[k].Y; sz += added[k].Z; }
                float inv = 1f / adj[n].Count;
                var d = added[n];
                next[i] = new Vec3(d.X + (sx * inv - d.X) * step,
                                   d.Y + (sy * inv - d.Y) * step,
                                   d.Z + (sz * inv - d.Z) * step);
            }
            for (int i = 0; i < nodes.Length; i++) added[nodes[i]] = next[i];
        }

        foreach (int n in nodes)
        {
            var was = strokeStart[n].Delta;
            nodeDelta[n] = new Vec3(was.X + added[n].X, was.Y + added[n].Y, was.Z + added[n].Z);
        }
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        var last = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        foreach (var (n, was) in last) { nodeDelta[n] = was.Delta; nodeWeight[n] = was.Weight; }
        // No unfold: the displacement restored here was already settled when its own stroke ended.
        Settle(null);
        Dirty = undo.Count > 0 || nodeDelta.Any(d => d.X != 0f || d.Y != 0f || d.Z != 0f);
    }

    public void Reset()
    {
        Array.Clear(nodeDelta);
        Array.Clear(nodeWeight);
        Array.Copy(initialPull, pullDir, nodeCount);
        undo.Clear();
        stroke = null;
        bridgeAxis = null;
        Dirty = false;
        Settle(null);
    }

    /// <param name="strokeStart">The stroke just ending — each node it moved, with the displacement it had
    /// before — whose own movement gets the fold check; null for none.</param>
    /// <param name="allowCollapse">The stroke was a bridge — see <see cref="UnfoldStroke"/>.</param>
    private void Settle(Dictionary<int, (Vec3 Delta, float Weight)>? strokeStart, bool allowCollapse = false)
    {
        // NO SLOPE LIMIT. It used to run here, and it is what made a pull snap back on release: it evens out
        // neighbours by pulling the larger displacement toward the smaller, and on a hem or a thin double-sided
        // garment neighbouring nodes legitimately differ a lot — so it quietly took back most of what the user
        // had just painted, only once they let go. Smoothing is SmoothStroke's job now, which does not shrink;
        // what the limit guarded against, faces passing through one another, is the unfold's.
        if (strokeStart is { Count: > 0 }) UnfoldStroke(strokeStart, allowCollapse);

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

    /// <summary>
    /// Take back as much of ONE STROKE'S movement as it takes for none of the triangles it touched to fold.
    /// <para/>
    /// Judged against the surface as the stroke FOUND it, and only the stroke's own addition is scaled back
    /// — never what earlier strokes left. Measured against the author's shape instead, a bridge's walls,
    /// laid flat on purpose, read as collapsed to the next pull or relax that brushed them, and that stroke's
    /// release halved them straight back into the crack. The nodes the stroke did not move have nothing
    /// added to scale, so they stay exactly where they were, and undo — which restores only the stroke's
    /// nodes — stays exact.
    /// <para/>
    /// A bridge stroke (<paramref name="allowCollapse"/>) is checked only for triangles that TURNED OVER and
    /// still have area to show it. It lifts every point along one axis held for the stroke, which leaves
    /// each triangle's footprint seen down that axis unchanged: a wall can legitimately flatten to almost
    /// nothing, and nothing can turn over as seen from outside. A steep wall's footprint is only a sliver,
    /// though, and a wall tipped past upright would be a real overhang — which is what this still catches.
    /// </summary>
    private void UnfoldStroke(Dictionary<int, (Vec3 Delta, float Weight)> strokeStart, bool allowCollapse)
    {
        var around = new List<int>();
        for (int t = 0; t + 2 < nodeTris.Length; t += 3)
        {
            int a = nodeTris[t], b = nodeTris[t + 1], c = nodeTris[t + 2];
            if (strokeStart.ContainsKey(a) || strokeStart.ContainsKey(b) || strokeStart.ContainsKey(c))
                around.AddRange([a, b, c]);
        }
        if (around.Count == 0) return;
        // The surface as the stroke found it, and what the stroke added to it. Nodes it never moved add zero.
        var found = new Vec3[nodeCount];
        var added = new Vec3[nodeCount];
        for (int n = 0; n < nodeCount; n++)
            found[n] = new Vec3(nodeAt[n].X + nodeDelta[n].X, nodeAt[n].Y + nodeDelta[n].Y, nodeAt[n].Z + nodeDelta[n].Z);
        foreach (var (n, was) in strokeStart)
        {
            found[n] = new Vec3(nodeAt[n].X + was.Delta.X, nodeAt[n].Y + was.Delta.Y, nodeAt[n].Z + was.Delta.Z);
            added[n] = new Vec3(nodeDelta[n].X - was.Delta.X, nodeDelta[n].Y - was.Delta.Y, nodeDelta[n].Z - was.Delta.Z);
        }

        SecondSkinWriter.UnfoldTriangles(found, added, around.ToArray(), null, allowCollapse);

        // Back onto the displacement. A point between where the stroke found it and where it left it is no
        // further out than the larger of the two, so the MaxDisplacement cap still holds.
        foreach (var (n, was) in strokeStart)
            nodeDelta[n] = new Vec3(was.Delta.X + added[n].X, was.Delta.Y + added[n].Y, was.Delta.Z + added[n].Z);
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
