using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// One model's geometry under the brush: what the user has pushed out so far. Pure geometry — no files, mods or game.
/// Everything is decided per welded node, never per vertex: moving one copy of a seam and not its twin opens a crack.
/// </summary>
internal sealed class MeshVolumeSolve : IMeshEdit
{
    /// <summary>
    /// Bump when the geometry this produces changes, so an older build's edit is redone from the author's backup.
    /// Same contract as <c>HatCompatSolve.Version</c>.
    /// </summary>
    public const int Version = 2;   // 2: wind painted into the second vertex colour

    /// <summary>
    /// The furthest any point may end up from where its author put it (0.1 m): a held button applies hundreds of
    /// dabs a second, and without a cap would balloon a garment.
    /// </summary>
    public const float GarmentMaxDisplacement = 0.1f;

    /// <summary>The same ceiling for hair, twice as far: restyling moves strands further than clearing cloth does.</summary>
    public const float HairMaxDisplacement = 0.2f;

    /// <summary>This model's ceiling: <see cref="HairMaxDisplacement"/> for a hairstyle, else
    /// <see cref="GarmentMaxDisplacement"/>.</summary>
    public float MaxDisplacement { get; }

    /// <summary>A model is hair when it draws with a hair material (<c>mt_c0201h0162_hir_a.mtrl</c>).</summary>
    internal static bool IsHairMaterial(string material)
        => material.Contains("_hir", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cell size (metres) of the skin grid used to aim the pull: big enough that a cloth point's 3×3×3
    /// neighbourhood holds the skin beneath it.
    /// </summary>
    private const float SkinCell = 0.03f;

    /// <summary>How many cells out the skin search reaches, in turn: a hand's width, a forearm's, a long skirt's.</summary>
    private static readonly int[] SkinReach = [1, 3, 8];

    /// <summary>
    /// Rounds of Taubin smoothing (a step toward the neighbours' average, then a slightly larger step back) run
    /// over each finished stroke — see <see cref="SmoothStroke"/>.
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
    /// Each node's authored normal (vertex normals averaged, or the faces' when there are none). What the normal
    /// rebuild blends back toward — not the pull direction, which is <see cref="pullDir"/>.
    /// </summary>
    private readonly Vec3[] nodeNormal;

    /// <summary>
    /// The direction the brush moves each node: away from the nearest skin where the model carries skin, else
    /// along the node's normal. Away-from-skin gives both layers of a cloth shell and its hem one shared direction.
    /// Aimed once from the author's positions: skin never moves.
    /// </summary>
    private readonly Vec3[] pullDir;

    /// <summary>Nodes whose pull was aimed from skin rather than from their normal.</summary>
    private readonly bool[] aimedFromSkin;

    /// <summary>The pulls as first aimed, so starting over also forgets the re-aiming strokes did.</summary>
    private readonly Vec3[] initialPull;

    /// <summary>
    /// Nodes belonging to skin (by <see cref="SecondSkinWriter.IsBodySkinMaterial"/>), which the brush never moves.
    /// A cloth vertex welded onto a skin vertex is held with it.
    /// </summary>
    private readonly bool[] skin;

    /// <summary>
    /// Nodes the user has locked, which no brush moves; they hold still as anchors the cloth around them relaxes
    /// into. See <see cref="SetLocked"/>.
    /// </summary>
    private readonly bool[] locked;

    private Vec3[] nodeDelta;

    /// <summary>
    /// The strongest falloff weight each node has ever been brushed with, used to blend normals: a node the brush
    /// barely reached takes only a tiny share of the recomputed normal, or shading creases at the stroke's edge.
    /// </summary>
    private float[] nodeWeight;

    private Vec3[] vertDelta;
    private Vec3[] vertNrm;

    /// <summary>
    /// Each node's wind, 0..1: the red of the second vertex colour. Per node, so both copies of a seam and both
    /// faces of a double-sided hair card sway together.
    /// </summary>
    private readonly float[] nodeWind;

    /// <summary>The wind the model arrived with, for Start over and for telling whether wind was edited.</summary>
    private readonly float[] initialWind;

    /// <summary>
    /// The share of each node's displacement the Move tool put there, already included in <see cref="nodeDelta"/>;
    /// kept apart so <see cref="MaxDisplacement"/> caps only the brushes' share.
    /// </summary>
    private Vec3[] nodeMoved;

    /// <summary>A node's values before a stroke first changed it: what undo restores.</summary>
    private readonly record struct Was(Vec3 Delta, float Weight, float Wind, Vec3 Moved);

    private readonly List<Dictionary<int, Was>> undo = [];
    private Dictionary<int, Was>? stroke;

    private Was Snapshot(int n) => new(nodeDelta[n], nodeWeight[n], nodeWind[n], nodeMoved[n]);


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

        // Whole submeshes only: islands are subsets of their submesh, so including both would count faces twice.
        tris = model.Parts.Where(p => p.Island < 0).SelectMany(p => p.Triangles).ToArray();

        nodeOf = MeshMath.WeldByPosition(basePos, out nodeCount);

        nodeAt = new Vec3[nodeCount];
        for (int i = 0; i < vc; i++) nodeAt[nodeOf[i]] = basePos[i];

        // Node-level faces; degenerate triples (two corners welded together) are dropped.
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

        // Adjacency, deduped: a shared edge must not pull twice as hard as a boundary one.
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

        // Open edges move: a hem or neckline must be pullable; a torn seam is visible and undoable per stroke.
        locked = new bool[nodeCount];
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

        // Fallback for a mesh with no usable normals: face normals, so the brush still has a direction.
        if (nodeNormal.Any(n => n.X == 0f && n.Y == 0f && n.Z == 0f))
        {
            var face = new Vec3[nodeCount];
            for (int t = 0; t + 2 < nodeTris.Length; t += 3)
            {
                var fn = MeshMath.TriNormal(
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
        nodeMoved = new Vec3[nodeCount];
        nodeWeight = new float[nodeCount];
        vertDelta = new Vec3[vc];
        vertNrm = (Vec3[])baseNrm.Clone();

        // The strongest of a node's vertices, so opening a model never stills part of it.
        nodeWind = new float[nodeCount];
        for (int i = 0; i < vc && i < model.Wind.Length; i++)
            nodeWind[nodeOf[i]] = MathF.Max(nodeWind[nodeOf[i]], Math.Clamp(model.Wind[i], 0f, 1f));
        initialWind = (float[])nodeWind.Clone();
        HasWindChannel = model.HasWindChannel;

        MeanEdge = MeshMath.MeanEdgeLength(nodeAt, adj, Enumerable.Range(0, nodeCount).ToList());
        MaxDisplacement = model.Parts.Any(p => IsHairMaterial(p.Material)) ? HairMaxDisplacement : GarmentMaxDisplacement;
    }

    /// <summary>
    /// Point every cloth node's pull away from the skin around it — see <see cref="pullDir"/>. The direction is the
    /// inverse-square field of nearby skin points (smooth, unlike nearest-point), searched outward in rings.
    /// A model with no skin leaves every direction on its normal.
    /// </summary>
    private void AimAwayFromSkin()
    {
        var grid = skinGrid;
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
    }

    /// <summary>Skin nodes bucketed by <see cref="SkinCell"/>, filled once by <see cref="AimAwayFromSkin"/>.
    /// Skin never moves, so the buckets never go stale.</summary>
    private readonly Dictionary<(int, int, int), List<int>> skinGrid = [];

    private static (int, int, int) Cell(Vec3 v)
        => ((int)MathF.Floor(v.X / SkinCell), (int)MathF.Floor(v.Y / SkinCell), (int)MathF.Floor(v.Z / SkinCell));

    /// <summary>The clearance (metres) relax keeps between cloth and the skin under it.</summary>
    private const float SkinFloorGap = 0.001f;

    /// <summary>
    /// Each cloth node's authored height above the skin beneath it, or NaN where none is in reach; computed on first
    /// use. A node the author had closer than <see cref="SkinFloorGap"/> keeps that as its floor.
    /// </summary>
    private float[]? restClearance;

    /// <summary>
    /// The nearest skin node to <paramref name="p"/> within one grid cell each way (at least
    /// <see cref="SkinCell"/>), and the point's height above it along that skin's outward normal.
    /// </summary>
    private bool SkinBeneath(Vec3 p, out int skinNode, out float height)
    {
        skinNode = -1;
        height = 0f;
        if (skinGrid.Count == 0) return false;

        var (cx, cy, cz) = Cell(p);
        float best = float.MaxValue;
        for (int x = cx - 1; x <= cx + 1; x++)
        for (int y = cy - 1; y <= cy + 1; y++)
        for (int z = cz - 1; z <= cz + 1; z++)
        {
            if (!skinGrid.TryGetValue((x, y, z), out var near)) continue;
            foreach (int s in near)
            {
                float dx = p.X - nodeAt[s].X, dy = p.Y - nodeAt[s].Y, dz = p.Z - nodeAt[s].Z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < best) { best = d2; skinNode = s; }
            }
        }
        if (skinNode < 0) return false;

        var nrm = nodeNormal[skinNode];
        if (nrm.X == 0f && nrm.Y == 0f && nrm.Z == 0f) { skinNode = -1; return false; }
        height = (p.X - nodeAt[skinNode].X) * nrm.X + (p.Y - nodeAt[skinNode].Y) * nrm.Y
               + (p.Z - nodeAt[skinNode].Z) * nrm.Z;
        return true;
    }

    /// <summary>
    /// Lift a relaxed cloth node back above the skin under it if it sank below the floor. Where the model carries no
    /// skin beneath the point, the node is left to sink.
    /// </summary>
    private Vec3 KeepAboveSkin(int n, Vec3 delta)
    {
        var p = new Vec3(nodeAt[n].X + delta.X, nodeAt[n].Y + delta.Y, nodeAt[n].Z + delta.Z);
        if (!SkinBeneath(p, out int s, out float height)) return delta;

        if (restClearance == null)
        {
            restClearance = new float[nodeCount];
            Array.Fill(restClearance, float.NaN);
        }
        if (float.IsNaN(restClearance[n]))
            restClearance[n] = SkinBeneath(nodeAt[n], out _, out float rest) ? rest : SkinFloorGap;

        float floor = MathF.Min(SkinFloorGap, restClearance[n]);
        if (height >= floor) return delta;

        var nrm = nodeNormal[s];
        float lift = floor - height;
        return new Vec3(delta.X + nrm.X * lift, delta.Y + nrm.Y * lift, delta.Z + nrm.Z * lift);
    }

    public IReadOnlyList<MeshSpan> Spans { get; }

    /// <summary>
    /// Lock exactly these vertices (indexed like <see cref="ModelParts.Positions"/>) and unlock the rest. A node is
    /// locked when any of its welded vertices is. Moves nothing and touches neither undo nor <see cref="Dirty"/>.
    /// </summary>
    public void SetLocked(IEnumerable<int> vertices)
    {
        Array.Clear(locked);
        foreach (int v in vertices)
            if (v >= 0 && v < nodeOf.Length) locked[nodeOf[v]] = true;
        LockVersion++;
    }

    /// <summary>Bumped by every <see cref="SetLocked"/>, so a view can tell when to rebuild what it greys out.</summary>
    public int LockVersion { get; private set; }

    /// <summary>Whether the node at vertex <paramref name="vertex"/> is locked.</summary>
    public bool IsLocked(int vertex) => vertex >= 0 && vertex < nodeOf.Length && locked[nodeOf[vertex]];

    /// <summary>
    /// A dab's weight at node <paramref name="n"/>: the falloff from <paramref name="c"/>, or with
    /// <paramref name="mirror"/> the max (not sum) of that and the falloff from <c>(−x, y, z)</c>. 0 outside both.
    /// </summary>
    /// <param name="mirrored">The mirrored disc gave the weight.</param>
    private float DabWeight(int n, Vec3 c, float radius, bool mirror, out bool mirrored)
    {
        mirrored = false;
        float r2 = radius * radius;
        var at = Reach(n);
        float dy = at.Y - c.Y, dz = at.Z - c.Z;
        float yz = dy * dy + dz * dz;
        if (yz >= r2) return 0f;

        float dx = at.X - c.X;
        float d2 = dx * dx + yz;
        float w = d2 < r2 ? Falloff(MathF.Sqrt(d2) / radius) : 0f;
        if (!mirror) return w;

        float mx = at.X + c.X;
        float m2 = mx * mx + yz;
        if (m2 >= r2) return w;
        float wm = Falloff(MathF.Sqrt(m2) / radius);
        if (wm > w) { mirrored = true; return wm; }
        return w;
    }

    /// <summary>
    /// Where a brush finds node <paramref name="n"/>: the author's position plus any move, but not what brushes did,
    /// so reach is steady under a stroke yet follows a moved part.
    /// </summary>
    private Vec3 Reach(int n)
        => new(nodeAt[n].X + nodeMoved[n].X, nodeAt[n].Y + nodeMoved[n].Y, nodeAt[n].Z + nodeMoved[n].Z);

    /// <summary>
    /// A brush's proposed displacement for node <paramref name="n"/>, with the BRUSHES' share — everything but
    /// the move — held within <see cref="MaxDisplacement"/>. See <see cref="nodeMoved"/>.
    /// </summary>
    private Vec3 CapBrush(int n, Vec3 next)
    {
        var m = nodeMoved[n];
        var own = new Vec3(next.X - m.X, next.Y - m.Y, next.Z - m.Z);
        float len = MathF.Sqrt(own.X * own.X + own.Y * own.Y + own.Z * own.Z);
        if (len <= MaxDisplacement) return next;
        float k = MaxDisplacement / len;
        return new Vec3(m.X + own.X * k, m.Y + own.Y * k, m.Z + own.Z * k);
    }

    /// <summary>Whether a dab should also paint its mirror: asked for, and not already on the midline.</summary>
    private bool MirrorAt(Vector3 centre, bool mirror) => mirror && MathF.Abs(centre.X) >= MathF.Max(MeanEdge, 1e-4f);

    /// <summary>The mesh's own resolution, so a radius can be judged against what it will actually reach.</summary>
    public float MeanEdge { get; }

    public bool Dirty { get; private set; }

    /// <summary>The model already carried the wind channel when it was opened.</summary>
    public bool HasWindChannel { get; }

    /// <summary>Any node's wind differs from what the model arrived with.</summary>
    public bool WindEdited
    {
        get
        {
            for (int n = 0; n < nodeCount; n++)
                if (nodeWind[n] != initialWind[n]) return true;
            return false;
        }
    }

    /// <summary>Bumped whenever any wind value changes, so a view can tell when to rebuild what it draws.</summary>
    public int WindVersion { get; private set; }

    /// <summary>Wind at vertex <paramref name="vertex"/> (indexed like <see cref="ModelParts.Positions"/>), 0..1.</summary>
    public float WindAt(int vertex) => nodeWind[nodeOf[vertex]];

    /// <summary>
    /// One dab of the wind brush: move each node's wind toward <paramref name="target"/> by
    /// <paramref name="rate"/>, scaled by the falloff — strongest in the middle, nothing at the rim. Skin is never
    /// painted: it is not what sways.
    /// </summary>
    /// <param name="target">0..1: the amount being painted; 0 erases.</param>
    /// <param name="rate">0..1: how much of the remaining difference each dab closes at the middle of the brush.</param>
    /// <param name="mirror">Also paint the mirror image across the body's midline — see <see cref="DabWeight"/>.</param>
    /// <returns>How many nodes changed.</returns>
    public int PaintWind(Vector3 centre, float radius, float target, float rate, bool mirror = false)
    {
        if (radius <= 0f || rate <= 0f) return 0;
        stroke ??= [];
        target = Math.Clamp(target, 0f, 1f);
        rate = Math.Clamp(rate, 0f, 1f);

        var c = new Vec3(centre.X, centre.Y, centre.Z);
        mirror = MirrorAt(centre, mirror);
        int changed = 0;
        for (int n = 0; n < nodeCount; n++)
        {
            if (skin[n] || locked[n]) continue;
            float w = DabWeight(n, c, radius, mirror, out _);
            if (w <= 0f) continue;

            float next = Math.Clamp(nodeWind[n] + (target - nodeWind[n]) * rate * w, 0f, 1f);
            // Settle exactly on the target within a 255th: the file stores a byte.
            if (MathF.Abs(next - target) < 0.5f / 255f) next = target;
            if (next == nodeWind[n]) continue;

            stroke.TryAdd(n, Snapshot(n));   // before the first change — see Paint
            nodeWind[n] = next;
            changed++;
        }

        if (changed > 0)
        {
            WindVersion++;
            Dirty = true;
        }
        return changed;
    }

    /// <summary>The largest distance any point has been moved, in metres.</summary>
    public float Worst { get; private set; }

    public bool CanUndo => undo.Count > 0;

    /// <summary>
    /// The brush's weight at a normalised distance from its centre: 1 in the middle, 0 at the rim. Smoothstep, so the
    /// slope also reaches zero and no ring is left. Shared with the viewport preview so the two cannot disagree.
    /// </summary>
    public static float Falloff(float t)
    {
        if (t <= 0f) return 1f;
        if (t >= 1f) return 0f;
        float u = 1f - t;
        return u * u * (3f - 2f * u);
    }

    /// <summary>
    /// One dab: push every node inside the brush out along its pull direction, by <paramref name="strength"/>
    /// scaled by the falloff. Negative strength pulls in.
    /// </summary>
    /// <param name="toViewer">Unit direction toward the viewer in model space: the "out" for a node with no direction
    /// of its own (double-sided surfaces whose normals cancel). Zero leaves such nodes where they are.</param>
    /// <param name="mirror">Also paint the mirror image across the body's midline — see <see cref="DabWeight"/>.</param>
    /// <returns>How many nodes moved.</returns>
    public int Paint(Vector3 centre, float radius, float strength, Vector3 toViewer = default, bool mirror = false)
    {
        if (radius <= 0f || strength == 0f) return 0;
        stroke ??= [];
        var viewer = Unit(new Vec3(toViewer.X, toViewer.Y, toViewer.Z));
        var viewerMirrored = new Vec3(-viewer.X, viewer.Y, viewer.Z);

        var c = new Vec3(centre.X, centre.Y, centre.Z);
        mirror = MirrorAt(centre, mirror);
        int moved = 0;

        for (int n = 0; n < nodeCount; n++)
        {
            if (skin[n] || locked[n]) continue;

            float w = DabWeight(n, c, radius, mirror, out bool mirrored);
            if (w <= 0f) continue;

            var dir = pullDir[n];
            if (dir.X == 0f && dir.Y == 0f && dir.Z == 0f) dir = mirrored ? viewerMirrored : viewer;   // double-sided: see toViewer
            if (dir.X == 0f && dir.Y == 0f && dir.Z == 0f) continue;

            // Recorded before the first change of this stroke: undo returns a node to where the stroke found it.
            stroke.TryAdd(n, Snapshot(n));

            float step = strength * w;
            var next = new Vec3(nodeDelta[n].X + dir.X * step,
                                nodeDelta[n].Y + dir.Y * step,
                                nodeDelta[n].Z + dir.Z * step);

            // Clamped by total magnitude, so holding the button cannot walk a vertex away indefinitely.
            nodeDelta[n] = CapBrush(n, next);
            nodeWeight[n] = MathF.Max(nodeWeight[n], w);
            moved++;
        }

        if (moved > 0)
        {
            Dirty = true;
            // The per-vertex view follows now, since the preview draws it; normals are only needed at write time.
            Spread();
        }
        return moved;
    }

    /// <summary>
    /// One dab of the relax brush: a plain average toward the neighbours of the surface as it now is, strongest in the
    /// middle. It shrinks deliberately, like 3ds Max's relax, but stops <see cref="SkinFloorGap"/> above skin the
    /// model carries (<see cref="KeepAboveSkin"/>). Reads every node before writing any, so node order does not matter.
    /// </summary>
    /// <param name="rate">0..1: how far each dab moves toward the average, at the middle of the brush.</param>
    /// <param name="mirror">Also relax the mirror image across the body's midline — see <see cref="DabWeight"/>.</param>
    /// <returns>How many nodes moved.</returns>
    public int Relax(Vector3 centre, float radius, float rate, bool mirror = false)
    {
        if (radius <= 0f || rate <= 0f) return 0;
        stroke ??= [];

        var c = new Vec3(centre.X, centre.Y, centre.Z);
        mirror = MirrorAt(centre, mirror);

        var nodes = brushNodes;
        var weights = brushWeights;
        nodes.Clear();
        weights.Clear();
        for (int n = 0; n < nodeCount; n++)
        {
            if (skin[n] || locked[n] || adj[n].Count == 0) continue;
            float w = DabWeight(n, c, radius, mirror, out _);
            if (w <= 0f) continue;
            nodes.Add(n);
            weights.Add(w);
        }
        if (nodes.Count == 0) return 0;

        // Recorded before the first change of this stroke — see Paint.
        foreach (int n in nodes) stroke.TryAdd(n, Snapshot(n));

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

            // Half the way at full rate, Max's default relax value of 0.5.
            float f = SmoothLambda * rate * weights[i];
            next[i] = KeepAboveSkin(n, new Vec3(d.X + (sx * inv - px) * f,
                                                d.Y + (sy * inv - py) * f,
                                                d.Z + (sz * inv - pz) * f));
        }
        for (int i = 0; i < nodes.Count; i++) nodeDelta[nodes[i]] = next[i];

        for (int i = 0; i < nodes.Count; i++)
        {
            int n = nodes[i];
            nodeDelta[n] = CapBrush(n, nodeDelta[n]);
            nodeWeight[n] = MathF.Max(nodeWeight[n], weights[i]);
        }

        Dirty = true;
        Spread();
        return nodes.Count;
    }

    // Per-dab working storage for Relax and Bridge, reused to avoid per-frame garbage.
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
    /// One dab of the bridge brush: stretch the cloth straight across a hollow instead of following the body into it.
    /// Only ever lifts. A construction, not a relaxation: a cleft is a saddle, which isotropic smoothing cannot span.
    /// The target is the upper convex hull of the outward-facing cloth along a held outward axis, read as 1-D chords in
    /// eight directions; inward-facing points (linings) take the lift of the nearest outward point, keeping thickness.
    /// The axis is the average pull direction on the stroke's first dab, falling back to <paramref name="toViewer"/>.
    /// </summary>
    /// <param name="rate">0..1: how much of the remaining gap each dab closes, at the middle of the brush.</param>
    /// <param name="toViewer">Unit direction from the model toward the camera.</param>
    /// <param name="mirror">Also bridge the mirror image across the midline, along the mirrored held axis; a node
    /// the first disc lifted this dab is not lifted again.</param>
    /// <returns>How many nodes moved.</returns>
    public int Bridge(Vector3 centre, float radius, float rate, Vector3 toViewer, bool mirror = false)
    {
        if (radius <= 0f || rate <= 0f) return 0;
        stroke ??= [];

        var c = new Vec3(centre.X, centre.Y, centre.Z);
        bool both = MirrorAt(centre, mirror);
        bridgeLifted.Clear();
        int moved = BridgeDab(c, radius, rate, toViewer, ref bridgeAxis, both ? bridgeLifted : null, null);
        if (both)
        {
            // The mirrored side holds the mirror of the first side's axis, so the two lift symmetrically.
            if (bridgeMirrorAxis == null && bridgeAxis is { } held) bridgeMirrorAxis = new Vec3(-held.X, held.Y, held.Z);
            moved += BridgeDab(new Vec3(-c.X, c.Y, c.Z), radius, rate, new Vector3(-toViewer.X, toViewer.Y, toViewer.Z),
                               ref bridgeMirrorAxis, null, bridgeLifted);
        }

        if (moved > 0)
        {
            Dirty = true;
            Spread();
        }
        return moved;
    }

    /// <summary>The mirrored half of a mirrored bridge stroke's held axis; null between strokes.</summary>
    private Vec3? bridgeMirrorAxis;

    /// <summary>Nodes the first disc of a mirrored bridge dab lifted, which the second leaves alone.</summary>
    private readonly HashSet<int> bridgeLifted = [];

    /// <summary>One disc of <see cref="Bridge"/>.</summary>
    /// <param name="heldAxis">The stroke's held outward axis for this disc: read, or set on its first dab.</param>
    /// <param name="lifted">Receives every node this disc moves; null for none.</param>
    /// <param name="skip">Nodes this disc must not move; null for none.</param>
    private int BridgeDab(Vec3 c, float radius, float rate, Vector3 toViewer, ref Vec3? heldAxis,
                          HashSet<int>? lifted, HashSet<int>? skip)
    {
        float r2 = radius * radius;

        var nodes = brushNodes;
        var weights = brushWeights;
        nodes.Clear();
        weights.Clear();
        float ax = 0f, ay = 0f, az = 0f, wsum = 0f;
        for (int n = 0; n < nodeCount; n++)
        {
            if (skin[n] || locked[n]) continue;
            var at = Reach(n);
            float dx = at.X - c.X, dy = at.Y - c.Y, dz = at.Z - c.Z;
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

        // The outward axis, taken on the stroke's first dab and held: re-read per dab it swings, and lifting
        // along several directions can turn a triangle over.
        if (heldAxis is not { } axis)
        {
            float alen = MathF.Sqrt(ax * ax + ay * ay + az * az);
            axis = alen >= 0.5f * wsum
                ? new Vec3(ax / alen, ay / alen, az / alen)
                : Unit(new Vec3(toViewer.X, toViewer.Y, toViewer.Z));
            if (axis.X == 0f && axis.Y == 0f && axis.Z == 0f) return 0;
            heldAxis = axis;
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

            // Facing out of the body, from the authored normal (pullDir points away from skin on both layers).
            // Generous, so a cleft's near-sideways walls still count.
            var nn = nodeNormal[n];
            outward[i] = nn.X * axis.X + nn.Y * axis.Y + nn.Z * axis.Z > -0.2f;
            if (outward[i]) outwardCount++;
        }
        // A model whose normals are all flipped, or that has none, would leave nothing to build a hull from.
        if (outwardCount < 3) Array.Fill(outward, true, 0, count);

        // Strips one mesh edge wide. Each point's height is corrected back to its strip's centre line along the
        // fitted surface slope, or a sloped surface reads its upper rows as chords and inflates.
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
            // Clamped: on a near-vertical wall the fit runs to huge slopes, which are noise.
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

                    // Never above the chord ends' real heights: a slope fit at its clamp would otherwise feed a
                    // phantom rim that rises every dab.
                    float roof = MathF.Max(ph[h0], ph[h1]);
                    gapOf[m] = MathF.Max(0f, MathF.Min(chord - hc[i], roof - ph[i]) - tolerance);
                }

                // An inward-facing point takes the gap of the nearest outward point along the chord, so both
                // layers move together.
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
            if (skip != null && skip.Contains(n)) continue;
            lifted?.Add(n);

            stroke!.TryAdd(n, Snapshot(n));   // before the first change — see Paint

            var next = new Vec3(nodeDelta[n].X + axis.X * step,
                                nodeDelta[n].Y + axis.Y * step,
                                nodeDelta[n].Z + axis.Z * step);
            nodeDelta[n] = CapBrush(n, next);
            nodeWeight[n] = MathF.Max(nodeWeight[n], weights[i]);
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// Finish a stroke: smooth, pull back anything that folded, rebuild the normals, and re-aim the brush. Runs at
    /// the end rather than per dab, or the unfold would fight the user while they push.
    /// </summary>
    /// <param name="bridge">The stroke was the bridge brush, which is not smoothed and may collapse triangles.</param>
    /// <param name="wind">The stroke only painted wind: it is recorded for undo and nothing else.</param>
    /// <param name="bridge">The stroke was the bridge brush, which is finished differently — see below.</param>
    /// <param name="wind">The stroke only painted wind: nothing moved, so there is nothing to smooth, unfold or
    /// re-light — it is recorded for undo and that is all.</param>
    public void EndStroke(bool bridge = false, bool wind = false)
    {
        var touched = stroke;
        stroke = null;
        bridgeAxis = bridgeMirrorAxis = null;
        if (wind)
        {
            if (touched is { Count: > 0 }) undo.Add(touched);
            return;
        }
        if (touched is not { Count: > 0 })
        {
            Settle(null);
            return;
        }
        undo.Add(touched);

        // A bridge stroke is not smoothed and may collapse triangles: smoothing flattens its narrow band of lift,
        // and filling a steep crack is meant to lay its walls flat.
        if (!bridge) SmoothStroke(touched);
        Settle(touched, allowCollapse: bridge);
    }

    /// <summary>
    /// Lightly smooth what this stroke added, with Taubin pairs so the bulk does not shrink. Only the stroke's own
    /// nodes are written, so earlier strokes stay intact and undo stays exact. Skin is never written.
    /// </summary>
    private void SmoothStroke(Dictionary<int, Was> strokeStart)
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
        bool moved = false;
        foreach (var (n, was) in last)
        {
            moved |= nodeDelta[n] != was.Delta;
            nodeDelta[n] = was.Delta;
            nodeMoved[n] = was.Moved;
            nodeWeight[n] = was.Weight;
            nodeWind[n] = was.Wind;
        }
        WindVersion++;
        // No unfold: the restored displacement was settled when its own stroke ended.
        if (moved) Settle(null);
        Dirty = undo.Count > 0 || nodeDelta.Any(d => d.X != 0f || d.Y != 0f || d.Z != 0f) || WindEdited;
    }

    public void Reset()
    {
        Array.Clear(nodeDelta);
        Array.Clear(nodeMoved);
        Array.Clear(nodeWeight);
        Array.Copy(initialWind, nodeWind, nodeCount);
        WindVersion++;
        Array.Copy(initialPull, pullDir, nodeCount);
        undo.Clear();
        stroke = null;
        bridgeAxis = bridgeMirrorAxis = null;
        Dirty = false;
        Settle(null);
    }

    // ── move ────────────────────────────────────────────────────────────────

    /// <summary>Each node a move drag carries, with how much of the offset it takes; empty between drags.</summary>
    private readonly Dictionary<int, float> moveWeights = [];

    /// <summary>A move drag is under way: <see cref="BeginMove"/> was called and <see cref="EndMove"/> not yet.</summary>
    public bool Moving { get; private set; }

    /// <summary>
    /// Start dragging one part: its nodes take the whole offset and, with <paramref name="adjacent"/>, every other
    /// node within <paramref name="falloffRadius"/> (through space, joined or not) takes a fading share.
    /// Skin and locked nodes never move; a part node welded to a neighbour moves both copies.
    /// </summary>
    /// <param name="partVertices">The part's triangle corners, indexed like <see cref="ModelParts.Positions"/>.</param>
    /// <param name="alongSurface">Measure the falloff ALONG the surface, through joined polygons only, instead of
    /// straight through space. What a polygon selection wants: the fade follows the garment, and a separate piece that
    /// merely sits nearby — the other cup, a strap over the cloth — takes none of the move.</param>
    /// <returns>How many nodes the drag will move; 0 when there is nothing it may.</returns>
    public int BeginMove(IEnumerable<int> partVertices, bool adjacent, float falloffRadius, bool alongSurface = false)
    {
        if (Moving) EndMove();
        moveWeights.Clear();

        var seeds = new HashSet<int>();
        foreach (int v in partVertices)
            if (v >= 0 && v < nodeOf.Length && !skin[nodeOf[v]] && !locked[nodeOf[v]]) seeds.Add(nodeOf[v]);
        if (seeds.Count == 0) return 0;

        foreach (int n in seeds) moveWeights[n] = 1f;

        if (adjacent && falloffRadius > 0f)
            foreach (var (n, d) in alongSurface ? AlongSurface(seeds, falloffRadius) : NearPart(seeds, falloffRadius))
            {
                float w = Falloff(d / falloffRadius);
                if (w > 0f) moveWeights[n] = w;
            }

        stroke = [];
        foreach (int n in moveWeights.Keys) stroke[n] = Snapshot(n);
        Moving = true;
        return moveWeights.Count;
    }

    /// <summary>
    /// Every movable node outside <paramref name="seeds"/> reachable within <paramref name="radius"/> by walking the
    /// mesh's edges from one of them, with the shortest such walk, measured on the surface as it now stands. Dijkstra
    /// over the welded adjacency, so a walk crosses uv seams but never a gap between pieces. Skin and locked nodes are
    /// walls: nothing that may not move is walked through to reach cloth beyond it.
    /// </summary>
    private List<(int Node, float Distance)> AlongSurface(HashSet<int> seeds, float radius)
    {
        var dist = new Dictionary<int, float>();
        var queue = new PriorityQueue<int, float>();
        foreach (int s in seeds)
        {
            dist[s] = 0f;
            queue.Enqueue(s, 0f);
        }

        while (queue.TryDequeue(out int n, out float d))
        {
            if (d > dist[n]) continue;                     // a stale entry: a shorter walk already settled it
            var here = Here(n);
            foreach (int m in adj[n])
            {
                if (skin[m] || locked[m]) continue;
                var there = Here(m);
                float dx = there.X - here.X, dy = there.Y - here.Y, dz = there.Z - here.Z;
                float next = d + MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (next > radius) continue;
                if (dist.TryGetValue(m, out float was) && was <= next) continue;
                dist[m] = next;
                queue.Enqueue(m, next);
            }
        }

        var found = new List<(int, float)>(dist.Count);
        foreach (var (n, d) in dist)
            if (!seeds.Contains(n)) found.Add((n, d));
        return found;
    }

    /// <summary>
    /// Every movable node outside <paramref name="seeds"/> within <paramref name="radius"/> of one of them, with its
    /// distance to the nearest, measured on the current surfaces. Grid-bucketed with a ring search.
    /// </summary>
    private List<(int Node, float Distance)> NearPart(HashSet<int> seeds, float radius)
    {
        var found = new List<(int, float)>();
        float cell = Math.Clamp(MathF.Max(MeanEdge * 2f, radius / 4f), 1e-4f, radius);
        int reach = (int)MathF.Ceiling(radius / cell);

        var grid = new Dictionary<(int, int, int), List<Vec3>>();
        var lo = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        (int, int, int) CellOf(Vec3 p)
            => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));

        foreach (int s in seeds)
        {
            var p = Here(s);
            var key = CellOf(p);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(p);
            lo = new Vec3(MathF.Min(lo.X, p.X), MathF.Min(lo.Y, p.Y), MathF.Min(lo.Z, p.Z));
            hi = new Vec3(MathF.Max(hi.X, p.X), MathF.Max(hi.Y, p.Y), MathF.Max(hi.Z, p.Z));
        }

        float r2 = radius * radius;
        for (int n = 0; n < nodeCount; n++)
        {
            if (seeds.Contains(n) || skin[n] || locked[n]) continue;
            var p = Here(n);
            if (p.X < lo.X - radius || p.Y < lo.Y - radius || p.Z < lo.Z - radius
                || p.X > hi.X + radius || p.Y > hi.Y + radius || p.Z > hi.Z + radius)
                continue;

            var (cx, cy, cz) = CellOf(p);
            float best = r2;
            bool any = false;
            for (int ring = 0; ring <= reach; ring++)
            {
                for (int x = -ring; x <= ring; x++)
                for (int y = -ring; y <= ring; y++)
                for (int z = -ring; z <= ring; z++)
                {
                    if (Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) != ring) continue;   // this shell only
                    if (!grid.TryGetValue((cx + x, cy + y, cz + z), out var near)) continue;
                    foreach (var q in near)
                    {
                        float dx = q.X - p.X, dy = q.Y - p.Y, dz = q.Z - p.Z;
                        float d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 < best) { best = d2; any = true; }
                    }
                }
                // Every cell of the next ring is at least `ring` cells away, so nothing there can be nearer.
                float shell = ring * cell;
                if (any && best <= shell * shell) break;
            }
            if (any) found.Add((n, MathF.Sqrt(best)));
        }
        return found;
    }

    /// <summary>
    /// Place the dragged part at <paramref name="offset"/> from where the drag found it. Absolute, so rounding cannot
    /// walk the part off the mouse. Not capped: a move goes exactly as far as the user drags.
    /// </summary>
    public void MoveTo(Vector3 offset)
    {
        if (!Moving || stroke == null) return;
        foreach (var (n, w) in moveWeights)
        {
            var was = stroke[n];
            var step = new Vec3(offset.X * w, offset.Y * w, offset.Z * w);
            nodeDelta[n] = new Vec3(was.Delta.X + step.X, was.Delta.Y + step.Y, was.Delta.Z + step.Z);
            nodeMoved[n] = new Vec3(was.Moved.X + step.X, was.Moved.Y + step.Y, was.Moved.Z + step.Z);
        }
        Dirty = true;
        Spread();
    }

    /// <summary>
    /// Turn or scale the dragged part by <paramref name="transform"/> — a model-space matrix about the part's pivot —
    /// from where the drag found it. The part's nodes take the whole transform; each nearby node carried by
    /// <see cref="BeginMove"/> takes its share of the way there. Absolute and not capped, like <see cref="MoveTo"/>.
    /// </summary>
    public void TransformTo(Matrix4x4 transform)
    {
        if (!Moving || stroke == null) return;
        foreach (var (n, w) in moveWeights)
        {
            var was = stroke[n];
            var start = new Vector3(nodeAt[n].X + was.Delta.X, nodeAt[n].Y + was.Delta.Y, nodeAt[n].Z + was.Delta.Z);
            var goal = Vector3.Transform(start, transform);
            var step = new Vec3((goal.X - start.X) * w, (goal.Y - start.Y) * w, (goal.Z - start.Z) * w);
            nodeDelta[n] = new Vec3(was.Delta.X + step.X, was.Delta.Y + step.Y, was.Delta.Z + step.Z);
            nodeMoved[n] = new Vec3(was.Moved.X + step.X, was.Moved.Y + step.Y, was.Moved.Z + step.Z);
            // Unlike a move, a turn changes which way the surface faces: the normal rebuild at release has to reach
            // these nodes (a translation leaves the part's own nodes at weight 0 and its normals as they were).
            // The undo snapshot taken by BeginMove restores the weight with everything else.
            nodeWeight[n] = MathF.Max(nodeWeight[n], w);
        }
        Dirty = true;
        Spread();
    }

    /// <summary>
    /// Finish a move drag: record it for undo and re-light the neighbours it stretched. No smoothing or unfold, which
    /// would take back part of the move.
    /// </summary>
    public void EndMove()
    {
        if (!Moving) return;
        Moving = false;
        var touched = stroke;
        stroke = null;

        bool moved = false;
        if (touched != null)
            foreach (var (n, was) in touched)
                if (nodeDelta[n] != was.Delta) { moved = true; break; }

        if (moved)
        {
            undo.Add(touched!);
            foreach (var (n, w) in moveWeights)
                if (w < 1f) nodeWeight[n] = MathF.Max(nodeWeight[n], w);
        }
        moveWeights.Clear();
        Settle(null);
    }

    /// <summary>A node's position as it now stands: the author's plus every edit.</summary>
    private Vec3 Here(int n)
        => new(nodeAt[n].X + nodeDelta[n].X, nodeAt[n].Y + nodeDelta[n].Y, nodeAt[n].Z + nodeDelta[n].Z);

    /// <param name="strokeStart">The stroke just ending — each node it moved, with the displacement it had
    /// before — whose own movement gets the fold check; null for none.</param>
    /// <param name="allowCollapse">The stroke was a bridge — see <see cref="UnfoldStroke"/>.</param>
    private void Settle(Dictionary<int, Was>? strokeStart, bool allowCollapse = false)
    {
        // No slope limit: it pulled painted displacement back on release. Smoothing is SmoothStroke's job.
        if (strokeStart is { Count: > 0 }) UnfoldStroke(strokeStart, allowCollapse);

        // Skin again, after the unfold, which knows nothing about it.
        for (int n = 0; n < nodeCount; n++)
            if (skin[n]) { nodeDelta[n] = default; nodeMoved[n] = default; nodeWeight[n] = 0f; }

        Spread();

        vertNrm = SecondSkinWriter.RelaxedNormals(
            basePos, baseNrm, vertDelta, nodeOf, nodeWeight, nodeNormal, tris);

        // Re-aim normal-following pulls from the current surface: inside a concavity the old directions drive the
        // walls apart. Pulls aimed from skin stay, since the skin has not moved.
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
    /// Take back as much of one stroke's movement as it takes for none of the triangles it touched to fold, judged
    /// against the surface as the stroke found it; earlier strokes are never scaled. A bridge stroke
    /// (<paramref name="allowCollapse"/>) is checked only for triangles that turned over and still have area.
    /// </summary>
    private void UnfoldStroke(Dictionary<int, Was> strokeStart, bool allowCollapse)
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

        BodyBridge.UnfoldTriangles(found, added, around.ToArray(), null, allowCollapse);

        // Back onto the displacement; the MaxDisplacement cap still holds.
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
    /// The deformed model, laid out exactly like <see cref="ModelParts.Positions"/>. One buffer, refilled every call
    /// that follows a change; the caller only reads it.
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

    /// <summary>The share of <see cref="DeltaAt"/> the Move tool put there — see <see cref="nodeMoved"/>.</summary>
    public Vec3 MovedAt(int vertex) => nodeMoved[nodeOf[vertex]];

    /// <summary>Per-vertex normal-blend weight — see <see cref="nodeWeight"/>.</summary>
    public float WeightAt(int vertex) => nodeWeight[nodeOf[vertex]];

    /// <summary>
    /// Take an edit made on another size of this garment (<see cref="BrushTransfer"/>) as this model's whole edit:
    /// per node the average displacement (brushes' share capped), strongest weight and, when <paramref name="wind"/>,
    /// strongest wind. Skin is left alone. Settled with no undo history.
    /// </summary>
    /// <param name="samples">Per vertex; null where the vertex has no counterpart and keeps nothing. Moved is the
    /// share of Delta that came from the Move tool.</param>
    /// <param name="wind">Carry wind too. Off when the source's wind was never painted, so a size keeps its author's.</param>
    public void ImportEdit(IReadOnlyList<(Vector3 Delta, float Weight, float Wind, Vector3 Moved)?> samples, bool wind)
    {
        var sum = new Vec3[nodeCount];
        var movedSum = new Vec3[nodeCount];
        var count = new int[nodeCount];
        var weight = new float[nodeCount];
        var windMax = new float[nodeCount];
        var windSeen = new bool[nodeCount];
        for (int v = 0; v < samples.Count && v < nodeOf.Length; v++)
        {
            if (samples[v] is not { } s) continue;
            int n = nodeOf[v];
            sum[n] = new Vec3(sum[n].X + s.Delta.X, sum[n].Y + s.Delta.Y, sum[n].Z + s.Delta.Z);
            movedSum[n] = new Vec3(movedSum[n].X + s.Moved.X, movedSum[n].Y + s.Moved.Y, movedSum[n].Z + s.Moved.Z);
            count[n]++;
            weight[n] = MathF.Max(weight[n], s.Weight);
            windMax[n] = windSeen[n] ? MathF.Max(windMax[n], s.Wind) : s.Wind;
            windSeen[n] = true;
        }

        undo.Clear();
        stroke = null;
        for (int n = 0; n < nodeCount; n++)
        {
            if (skin[n] || count[n] == 0) continue;
            var d = new Vec3(sum[n].X / count[n], sum[n].Y / count[n], sum[n].Z / count[n]);
            nodeMoved[n] = new Vec3(movedSum[n].X / count[n], movedSum[n].Y / count[n], movedSum[n].Z / count[n]);
            nodeDelta[n] = CapBrush(n, d);
            nodeWeight[n] = Math.Clamp(weight[n], 0f, 1f);
            if (wind && windSeen[n]) nodeWind[n] = Math.Clamp(MathF.Round(windMax[n] * 255f) / 255f, 0f, 1f);
        }

        WindVersion++;
        Dirty = nodeDelta.Any(x => x.X != 0f || x.Y != 0f || x.Z != 0f) || WindEdited;
        Settle(null);
    }

    /// <summary>Per-vertex normal after the edit, indexed as <see cref="ModelParts.Positions"/> is.</summary>
    public Vec3 NormalAt(int vertex) => vertNrm[vertex];

    private static Vec3 Unit(Vec3 v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len > 1e-6f ? new Vec3(v.X / len, v.Y / len, v.Z / len) : default;
    }
}
