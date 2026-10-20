using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// Move, Rotate and Scale with Mirror on: the drag carried onto the matching cloth across X = 0. Which cloth matches is
/// <see cref="MirrorPairing"/>'s; how the drag is shared out is decided here, once, when the drag begins
/// (<see cref="PlanMirror"/>), and applied per node every frame (<see cref="MirroredMove"/>).
/// </summary>
internal sealed partial class MeshVolumeSolve
{
    /// <summary>What the open (or last) mirrored drag found; null when it was not mirrored. Kept until the next drag
    /// begins, so the panel can show why a drag moved as it did.</summary>
    public MirrorReport? DragMirror { get; private set; }

    /// <summary>What the last mirrored <see cref="MoveFalloff"/> found; null after an unmirrored one, or one that found
    /// nothing to move.</summary>
    public MirrorReport? PreviewMirror { get; private set; }

    /// <summary>A share of a piece's nodes that must match inside the piece itself for it to be its own mirror.</summary>
    private const float SelfPairedShare = 0.6f;

    /// <summary>
    /// A share of a piece's nodes off the centre line that must match something outside the piece for it to have a
    /// mirror image at all. Nodes on the centre line always match themselves, so they do not count.
    /// </summary>
    private const float MatchedShare = 0.2f;

    private MirrorPairing? mirrorPairing;

    private MirrorPairing Pairing()
        => mirrorPairing ??= new MirrorPairing(nodeAt, nodeNormal, nodeNormalUsable, skin, nodeMaterial, adj, MeanEdge);

    /// <summary>How a mirrored drag is shared out, decided when it begins.</summary>
    /// <param name="Seeds">Every node the drag carries whole: the selection and its match.</param>
    /// <param name="DirectOnly">Selected nodes of pieces with no mirror image: they take the drag itself, whatever side
    /// they are on, so such a piece is never torn by the strip at the centre.</param>
    /// <param name="Sigma">The side the gizmo is on: +1 or −1. Its side takes the drag, the other the mirror image.</param>
    private sealed record MirrorPlan(MirrorMode Mode, HashSet<int> Seeds, HashSet<int> DirectOnly, float Sigma,
                                     MirrorReport Report);

    private MirrorMode moveMode;
    private MoveKind moveKind;
    private Vector3 movePivot;
    private float moveSigma = 1f;
    private HashSet<int> moveDirectOnly = [];

    /// <summary>Which map (<see cref="MapDirect"/>, <see cref="MapMirror"/> or <see cref="MapNone"/>) each carried node
    /// was last placed by. Filled only in <see cref="MirrorMode.Sides"/>.</summary>
    private readonly Dictionary<int, int> moveMapId = [];

    /// <summary>The middle of the seeds' bounds as they now stand: the pivot a preview, which has no gizmo, assumes.</summary>
    private Vector3 SeedCentre(HashSet<int> seeds)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (int n in seeds)
        {
            var p = Here(n);
            lo = Vector3.Min(lo, new Vector3(p.X, p.Y, p.Z));
            hi = Vector3.Max(hi, new Vector3(p.X, p.Y, p.Z));
        }
        return (lo + hi) * 0.5f;
    }

    /// <summary>
    /// Decide how a mirrored drag of <paramref name="seeds"/> moves.
    /// <list type="bullet">
    /// <item>The selection is split into its connected pieces; two pieces whose edges meet at the centre and match
    /// each other are one (a bra whose cups are separate islands is still one bra).</item>
    /// <item>A piece that mostly matches itself is its own mirror: the whole drag is then <see cref="MirrorMode.Centred"/>,
    /// and keeps only the drag's symmetric part, so the piece stays centred and cannot tear.</item>
    /// <item>Otherwise, pieces that match something elsewhere carry it along, mirrored
    /// (<see cref="MirrorMode.Sides"/>); a piece that matches nothing moves on its own side alone.</item>
    /// <item>Nothing matched at all: <see cref="MirrorMode.Off"/>, exactly a plain drag.</item>
    /// </list>
    /// </summary>
    private MirrorPlan PlanMirror(HashSet<int> seeds, Vector3 pivot)
    {
        var pairing = Pairing();
        var pairs = pairing.Pair(seeds, locked, Here, EditVersion);
        var report = new MirrorReport(seeds.Count, pairs.Partner.Count + pairs.Held.Count, pairs.Held.Count, MirrorMode.Off);

        // Connected pieces of the selection, joined where they meet at the centre and match across it.
        var piece = new Dictionary<int, int>();
        var queue = new Queue<int>();
        int pieces = 0;
        foreach (int s in seeds)
        {
            if (piece.ContainsKey(s)) continue;
            piece[s] = pieces;
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                foreach (int m in adj[n])
                    if (seeds.Contains(m) && piece.TryAdd(m, pieces)) queue.Enqueue(m);
            }
            pieces++;
        }
        var parent = new int[pieces];
        for (int i = 0; i < pieces; i++) parent[i] = i;
        int Root(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        float joinX = 2f * pairing.TolCap;
        foreach (var (s, p) in pairs.Partner)
            if (seeds.Contains(p) && MathF.Abs(Here(s).X) < joinX && MathF.Abs(Here(p).X) < joinX)
                parent[Root(piece[s])] = Root(piece[p]);

        var size = new int[pieces];
        var self = new int[pieces];
        var offMid = new int[pieces];
        var offMidMatched = new int[pieces];
        float minX = MirrorMinX;
        foreach (int s in seeds)
        {
            int r = Root(piece[s]);
            size[r]++;
            bool inside = pairs.Partner.TryGetValue(s, out int p) && seeds.Contains(p) && Root(piece[p]) == r;
            if (inside) self[r]++;
            if (MathF.Abs(Here(s).X) < minX) continue;
            offMid[r]++;
            if (pairs.Held.Contains(s) || (pairs.Partner.ContainsKey(s) && !inside)) offMidMatched[r]++;
        }
        bool Centred(int r) => self[r] >= SelfPairedShare * size[r];
        bool Matched(int r) => offMid[r] > 0 && offMidMatched[r] >= MatchedShare * offMid[r];

        bool anyCentred = false, anyMatched = false;
        for (int r = 0; r < pieces; r++)
        {
            if (Root(r) != r) continue;
            anyCentred |= Centred(r);
            anyMatched |= Matched(r);
        }
        var mode = anyCentred ? MirrorMode.Centred : anyMatched ? MirrorMode.Sides : MirrorMode.Off;
        report = report with { Mode = mode };
        if (mode == MirrorMode.Off) return new MirrorPlan(mode, seeds, [], 1f, report);

        // The match: partners and the far side's own region, of the pieces that have one. The report counts only
        // matches that move with the drag: a piece with no mirror image keeps none.
        bool Keeps(int s) => Root(piece[s]) is var r && (Centred(r) || Matched(r));
        var match = new HashSet<int>();
        int paired = 0, held = 0;
        foreach (var (s, p) in pairs.Partner)
            if (Keeps(s)) { match.Add(p); paired++; }
        foreach (int s in pairs.Held)
            if (Keeps(s)) held++;
        report = report with { Paired = paired + held, Held = held };
        foreach (var (m, s) in pairs.Reverse) if (Keeps(s)) match.Add(m);

        var all = new HashSet<int>(seeds);
        all.UnionWith(match);

        var direct = new HashSet<int>();
        if (mode == MirrorMode.Sides)
            foreach (int s in seeds)
                if (!Matched(Root(piece[s]))) direct.Add(s);
        float sigma = MathF.Abs(pivot.X) < minX ? 1f : MathF.Sign(pivot.X);
        return new MirrorPlan(mode, all, direct, sigma, report);
    }

    /// <summary>Take the plan for the drag that is beginning.</summary>
    private void StartMirror(MirrorPlan? plan, MoveMirror? mirror)
    {
        moveMapId.Clear();
        moveMode = plan?.Mode ?? MirrorMode.Off;
        if (plan == null || mirror is not { } how)
        {
            moveDirectOnly = [];
            return;
        }
        moveKind = how.Kind;
        movePivot = how.Pivot;
        moveSigma = plan.Sigma;
        moveDirectOnly = plan.DirectOnly;
    }

    private void EndMirror()
    {
        moveMode = MirrorMode.Off;
        moveMapId.Clear();
        moveDirectOnly = [];
    }

    /// <summary>
    /// Where a node <paramref name="from"/> off the centre plane, carried by its own side's motion to
    /// <paramref name="to"/> (negative past the centre), ends up: <paramref name="to"/> itself while that stays at
    /// least <c>m = min(from, margin)</c> off the centre, else <c>m·e^((to − m)/m)</c> — closer and closer, never there.
    /// <para/>
    /// Continuous and increasing in <paramref name="to"/> with slope 1 where the two pieces meet, so nothing jumps as a
    /// drag goes on; and for two nodes in order, the one nearer the centre has the smaller m, which (as m·e^(to/m−1)
    /// grows with m) keeps it nearer — so cloth near the centre keeps its order wherever the drag itself keeps it, and
    /// never crosses over. A node not moved toward the centre is not moved at all.
    /// </summary>
    internal static float KeepOffCentre(float from, float to, float margin)
    {
        float m = MathF.Min(from, margin);
        if (to >= m) return to;
        return m * MathF.Exp((to - m) / m);
    }

    /// <summary>
    /// Place a mirrored drag — <paramref name="offset"/> for a move, <paramref name="transform"/> for a turn or a
    /// stretch — from where it began.
    /// <para/>
    /// <see cref="MirrorMode.Centred"/>: every node takes the drag's symmetric part (<see cref="MirrorMotion"/>).
    /// <para/>
    /// <see cref="MirrorMode.Sides"/>: a node takes the drag (T) on the gizmo's side and its mirror image (S·T·S) on the
    /// other — its own side's motion — and a node on the centre plane the average of the two, which keeps it there.
    /// Only a node's distance from the centre is then passed through <see cref="KeepOffCentre"/>, so cloth carried
    /// toward the centre gathers up at it instead of crossing over or folding. Each side is the exact mirror of the
    /// other; a node the drag leaves well off the centre follows its side's motion exactly.
    /// </summary>
    private void MirroredMove(Vector3? offset, Matrix4x4? transform)
    {
        bool turns = transform.HasValue;
        var t = transform ?? Matrix4x4.Identity;
        var tm = MirrorMotion.Reflect(t);
        var o = offset ?? Vector3.Zero;
        var om = MirrorMotion.Reflect(o);

        Vector3 Start(int n)
        {
            var was = stroke![n].Delta;
            return new Vector3(nodeAt[n].X + was.X, nodeAt[n].Y + was.Y, nodeAt[n].Z + was.Z);
        }
        Vector3 Step(Vector3 p, bool mirrored)
            => turns ? Vector3.Transform(p, mirrored ? tm : t) - p : mirrored ? om : o;

        if (moveMode == MirrorMode.Centred)
        {
            var linear = turns ? MirrorMotion.CentredLinear(t, moveKind) : default;
            var centred = MirrorMotion.CentredOffset(o);
            foreach (var (n, w) in moveWeights)
            {
                var s = turns ? MirrorMotion.CentredStep(Start(n), linear, movePivot) : centred;
                Place(n, w, s);
                if (turns) nodeWeight[n] = MathF.Max(nodeWeight[n], w);
            }
        }
        else
        {
            float margin = MirrorMinX;
            // Within this of the centre plane a node is on it: a centre seam an author or a refit left a hair off zero
            // must not have its points sent alternately left and right.
            float onPlane = 1e-3f * margin;
            foreach (var (n, w) in moveWeights)
            {
                var p = Start(n);
                int id;
                Vec3 step;
                if (moveDirectOnly.Contains(n))
                {
                    id = MapDirect;
                    step = Weighted(Step(p, false), w);
                }
                else if (MathF.Abs(p.X) <= onPlane)
                {
                    // On the centre plane: the two sides' motions differ only in X, which they cancel.
                    id = MapNone;
                    step = Weighted((Step(p, false) + Step(p, true)) * 0.5f, w);
                }
                else
                {
                    bool mirrored = moveSigma * p.X < 0f;
                    id = mirrored ? MapMirror : MapDirect;
                    step = Weighted(Step(p, mirrored), w);
                    float from = MathF.Abs(p.X), side = MathF.Sign(p.X);
                    float to = from + side * step.X;
                    float kept = KeepOffCentre(from, to, margin);
                    if (kept != to)
                    {
                        id = MapNone;
                        step = new Vec3(side * kept - p.X, step.Y, step.Z);
                    }
                }
                moveMapId[n] = id;
                Place(n, step);
                // A turn re-lights all it carries; a move only what it gathers at the centre.
                if (turns || id == MapNone) nodeWeight[n] = MathF.Max(nodeWeight[n], w);
            }
        }
        Dirty = true;
        Spread();
    }

    private static Vec3 Weighted(Vector3 s, float w) => new(s.X * w, s.Y * w, s.Z * w);

    /// <summary>Set node <paramref name="n"/> to where the drag began plus <paramref name="w"/> of <paramref name="s"/>.</summary>
    private void Place(int n, float w, Vector3 s) => Place(n, Weighted(s, w));

    /// <summary>Set node <paramref name="n"/> to where the drag began plus <paramref name="step"/>.</summary>
    private void Place(int n, Vec3 step)
    {
        var was = stroke![n];
        nodeDelta[n] = new Vec3(was.Delta.X + step.X, was.Delta.Y + step.Y, was.Delta.Z + step.Z);
        nodeMoved[n] = new Vec3(was.Moved.X + step.X, was.Moved.Y + step.Y, was.Moved.Z + step.Z);
    }

    // ── exact normals per node ──────────────────────────────────────────────

    private const int MapNone = 0, MapDirect = 1, MapMirror = 2, MapCentred = 3;

    /// <summary>Which transform carried node <paramref name="n"/> in the open drag: the drag's own, its mirror image,
    /// the centred part, or a blend of two (none: its normals are turned, not mapped).</summary>
    private int MapIdOf(int n)
    {
        switch (moveMode)
        {
            case MirrorMode.Centred: return MapCentred;
            case MirrorMode.Sides:
                return moveMapId.TryGetValue(n, out int id) ? id : MapNone;
            default: return MapDirect;
        }
    }

    /// <summary>The normal map of each transform a node may have been carried by, indexed by map id; null where it
    /// turns no normal (an even scale, no change) or has none to give.</summary>
    private Matrix4x4?[] NodeMaps(Matrix4x4 m)
    {
        static Matrix4x4? MapOf(Matrix4x4 x) => LinearNormals.IsUniform(x) ? null : LinearNormals.NormalMap(x);

        var maps = new Matrix4x4?[4];
        maps[MapDirect] = MapOf(m);
        if (moveMode == MirrorMode.Sides) maps[MapMirror] = MapOf(MirrorMotion.Reflect(m));
        if (moveMode == MirrorMode.Centred) maps[MapCentred] = MapOf(Matrix4x4.Identity + MirrorMotion.CentredLinear(m, moveKind));
        return maps;
    }
}
