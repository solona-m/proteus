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
    /// <param name="Sigma">The side the gizmo is on: +1 or −1.</param>
    /// <param name="B0">The centre strip's half-width before the drag widens it.</param>
    private sealed record MirrorPlan(MirrorMode Mode, HashSet<int> Seeds, HashSet<int> DirectOnly, float Sigma, float B0,
                                     MirrorReport Report);

    private MirrorMode moveMode;
    private MoveKind moveKind;
    private Vector3 movePivot;
    private float moveSigma = 1f, moveB0;
    private HashSet<int> moveDirectOnly = [];

    /// <summary>Each carried node's share of the drag's own motion as last applied — the rest is the mirror's.
    /// Filled only in <see cref="MirrorMode.Sides"/>.</summary>
    private readonly Dictionary<int, float> moveSide = [];

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
        if (mode == MirrorMode.Off) return new MirrorPlan(mode, seeds, [], 1f, minX, report);

        // The match: partners and the far side's own region, of the pieces that have one.
        bool Keeps(int s) => Root(piece[s]) is var r && (Centred(r) || Matched(r));
        var match = new HashSet<int>();
        foreach (var (s, p) in pairs.Partner) if (Keeps(s)) match.Add(p);
        foreach (var (m, s) in pairs.Reverse) if (Keeps(s)) match.Add(m);
        CloseMatch(match, seeds);

        var all = new HashSet<int>(seeds);
        all.UnionWith(match);

        var direct = new HashSet<int>();
        float sigma = MathF.Abs(pivot.X) < minX ? 1f : MathF.Sign(pivot.X);
        float crossing = 0f;
        if (mode == MirrorMode.Sides)
        {
            var lo = new float[pieces];
            var hi = new float[pieces];
            Array.Fill(lo, float.MaxValue);
            Array.Fill(hi, float.MinValue);
            foreach (int s in seeds)
            {
                int r = Root(piece[s]);
                if (!Matched(r))
                {
                    direct.Add(s);
                    continue;
                }
                float sx = sigma * Here(s).X;
                lo[r] = MathF.Min(lo[r], sx);
                hi[r] = MathF.Max(hi[r], sx);
            }
            // A matched piece reaching across the centre: the strip must span how far it reaches over.
            for (int r = 0; r < pieces; r++)
                if (hi[r] > 0f && lo[r] < 0f) crossing = MathF.Max(crossing, -lo[r]);
        }
        return new MirrorPlan(mode, all, direct, sigma, MathF.Max(minX, crossing), report);
    }

    /// <summary>
    /// Two rounds of filling in the match: a movable node every one of whose movable neighbours (at least one) is in
    /// it joins it, so with the falloff off nothing is left standing as a spike among moved cloth.
    /// </summary>
    private void CloseMatch(HashSet<int> match, HashSet<int> seeds)
    {
        for (int round = 0; round < 2; round++)
        {
            var add = new List<int>();
            foreach (int n in match)
                foreach (int m in adj[n])
                {
                    if (match.Contains(m) || seeds.Contains(m) || skin[m] || locked[m]) continue;
                    int movable = 0;
                    bool surrounded = true;
                    foreach (int k in adj[m])
                    {
                        if (skin[k] || locked[k]) continue;
                        movable++;
                        if (!match.Contains(k)) { surrounded = false; break; }
                    }
                    if (surrounded && movable > 0) add.Add(m);
                }
            if (add.Count == 0) return;
            match.UnionWith(add);
        }
    }

    /// <summary>Take the plan for the drag that is beginning.</summary>
    private void StartMirror(MirrorPlan? plan, MoveMirror? mirror)
    {
        moveSide.Clear();
        moveMode = plan?.Mode ?? MirrorMode.Off;
        if (plan == null || mirror is not { } how)
        {
            moveDirectOnly = [];
            return;
        }
        moveKind = how.Kind;
        movePivot = how.Pivot;
        moveSigma = plan.Sigma;
        moveB0 = plan.B0;
        moveDirectOnly = plan.DirectOnly;
    }

    private void EndMirror()
    {
        moveMode = MirrorMode.Off;
        moveSide.Clear();
        moveDirectOnly = [];
    }

    /// <summary>
    /// Place a mirrored drag — <paramref name="offset"/> for a move, <paramref name="transform"/> for a turn or a
    /// stretch — from where it began.
    /// <para/>
    /// <see cref="MirrorMode.Centred"/>: every node takes the drag's symmetric part (<see cref="MirrorMotion"/>).
    /// <para/>
    /// <see cref="MirrorMode.Sides"/>: a node takes the drag (T) on the gizmo's side and its mirror image (S·T·S) on the
    /// other, blended across a strip at the centre so the centre line stays on it. The strip is <c>b = b0 + κ</c>
    /// wide each side, κ being how far the drag carries cloth near the centre toward it — weighed in softly with
    /// distance, so the strip widens smoothly. Inside the strip a move then scales X by at least
    /// <c>b0 / (b0 + κ)</c>, never zero, so cloth never crosses or folds over the centre. Measured with each node's
    /// own side's motion, so κ is the same seen from either side and the result stays symmetric.
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
            // κ: how far the drag carries cloth near the centre toward it, by each node's own side's motion.
            float kappa = 0f;
            foreach (var (n, w) in moveWeights)
            {
                if (moveDirectOnly.Contains(n)) continue;
                var p = Start(n);
                float ax = MathF.Abs(p.X);
                if (ax <= 0f) continue;
                float dx = w * Step(p, moveSigma * p.X < 0f).X;
                float toward = MathF.Max(0f, -MathF.Sign(p.X) * dx);
                if (toward <= 0f) continue;
                float reach = Math.Clamp((moveB0 + toward - ax) / moveB0, 0f, 1f);
                kappa = MathF.Max(kappa, reach * toward);
            }
            float band = moveB0 + kappa;

            foreach (var (n, w) in moveWeights)
            {
                var p = Start(n);
                float side = moveDirectOnly.Contains(n) ? 1f : MirrorMotion.Side(p.X, moveSigma, band);
                var own = Step(p, false);
                var s = side >= 1f ? own
                      : side <= 0f ? Step(p, true)
                      : own * side + Step(p, true) * (1f - side);
                moveSide[n] = side;
                Place(n, w, s);
                // A turn re-lights all it carries; a move only the strip it stretches.
                if (turns || side is > 0f and < 1f) nodeWeight[n] = MathF.Max(nodeWeight[n], w);
            }
        }
        Dirty = true;
        Spread();
    }

    /// <summary>Set node <paramref name="n"/> to where the drag began plus <paramref name="w"/> of <paramref name="s"/>.</summary>
    private void Place(int n, float w, Vector3 s)
    {
        var was = stroke![n];
        var step = new Vec3(s.X * w, s.Y * w, s.Z * w);
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
                float side = moveSide.TryGetValue(n, out float s) ? s : 1f;
                return side >= 1f ? MapDirect : side <= 0f ? MapMirror : MapNone;
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
