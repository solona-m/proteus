using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    /// <summary>
    /// How near a nail an open edge of the finger must lie to count against it, measured from the edge's midpoint to
    /// the nearest vertex of the nail. The probe's own metric: every nail sitting in a socket on Rue+/YAB+ had 4–17
    /// open edges this near, FreyjaAF 6–16; a closed finger (Neolithe, AB Eve, Tre YAS) has none.
    /// </summary>
    private const float NailSocketReach = 0.0015f;

    /// <summary>
    /// Fewest open edges near a nail that make it a nail over open skin. Stray cracks — a split vertex, a sloppy
    /// seam — leave one or two (Tight&amp;Firm, Kräuter measured 0–2); every socket measured has ten or more.
    /// </summary>
    private const int NailSocketMinEdges = 3;

    /// <summary>Largest a socket's rim may be across. Measured sockets span 4–13 mm; the wrist ring is far wider.</summary>
    private const float NailSocketMaxSpan = 0.020f;

    /// <summary>
    /// How far from the rim the nail that spans a socket may lie. The nail on top reaches every rim vertex within
    /// 2.4 mm on Rue+, 2.6 on LavaBod+, 2.3 on Kräuter, 0.93 on FreyjaAF; a tip island over the same socket is 7–11
    /// mm off on Rue+, so it never qualifies.
    /// </summary>
    private const float NailSocketOwnReach = 0.0035f;

    /// <summary>
    /// How squarely a nail triangle must face out of the socket (cosine against the rim's normal) to count as the nail's
    /// top, whose height a spanning patch keeps. Anything steeper — an edge curling down over the fingertip — is left
    /// to the smooth run from the rim, or its sudden drop stands the patch's triangles on edge.
    /// </summary>
    private const float NailTopFacing = 0.7f;

    /// <summary>How far down a seam ray may meet the patch or the finger and still count as closed: the ray starts a
    /// millimetre up, and a steep patch wall can sit a few millimetres further; anything past this is no part of
    /// the socket's surface (a curled tip, another finger).</summary>
    private const float NailSeamReach = 0.004f;

    /// <summary>How near the spanning nail's outline a rim edge must lie for the seam to count as shut where the seam rays
    /// start under the patch: a twentieth of a millimetre, the grader's own seam-gap bound.</summary>
    private const float NailSeamTight = 0.00005f;

    /// <summary>How far under the rim's lowest point the smooth fill may dip before it is refused for the nail-guided
    /// heights: 0.4 mm, inside the grader's 0.5 mm floor.</summary>
    private const double NailFillFloor = 0.0004;

    /// <summary>A second outline of a nail this small is a pinhole — a quad missing from the mesh (YAB+'s middle fingers:
    /// 4 corners, 2.4 mm round, against 33 mm for the nail), or the small hole a two-triangle plug island fills on Rue+,
    /// YAB+ and LavaBod+ thumbs (the plug is a socket member and collapses with it) — and is welded shut rather than
    /// leaving the socket as authored. A real double layer (FreyjaAF) is far larger. The welded corners share a
    /// position in the bind pose only (each keeps its own skinning): 0.6 mm apart on one finger bone, accepted.</summary>
    private const int NailPinholeMaxNodes = 8;
    private const float NailPinholeMaxShare = 0.15f;

    /// <summary>How far under its lowest rim point skin inside a socket's outline can lie and still be ON the socket
    /// (a patch of finger the socket rings round) rather than the far side of the finger, a finger's width down.</summary>
    private const float NailSocketSkinBelow = 0.002f;

    /// <summary>One socket in the finger skin — a closed loop of open edges — and what is done about it.</summary>
    internal sealed class NailSocket
    {
        /// <summary>The rim, in walk order: each edge as its two ends.</summary>
        public required (Vec3 A, Vec3 B)[] Rim;
        /// <summary>Every unsewn, drawn (not wholly hidden) nail island with an edge of this rim within <see cref="NailSocketReach"/>.</summary>
        public required List<(int Mesh, int Island)> Members;
        /// <summary>The island that spans it; null when the socket is left as authored.</summary>
        public (int Mesh, int Island)? Owner;
        /// <summary>Why it is left as authored, when it is.</summary>
        public string? LeftAsAuthored;
        /// <summary>Owner vertex → where it goes.</summary>
        public Dictionary<int, Vec3> Spanned = [];
        /// <summary>Owner vertex on the rim → the finger's normal there.</summary>
        public Dictionary<int, Vec3> RimNormal = [];
        /// <summary>Finger rim vertices moved onto the spanning nail's boundary, every welded copy of each.</summary>
        public List<(int Mesh, int Vertex, Vec3 To)> RimMoves = [];
        /// <summary>Furthest the spanning nail's inside moved off its authored shape (the outline's move onto the rim,
        /// carried in), for the log.</summary>
        public float MiddleOffset;
    }

    /// <summary>Every socket of a hand, and the nails over open skin no socket explains.</summary>
    internal sealed class NailSocketPlan
    {
        public required List<NailSocket> Sockets;
        /// <summary>
        /// Unsewn nail islands with <see cref="NailSocketMinEdges"/> or more open edges within reach that belong to no
        /// socket: collapsing one would open whatever it sits over, so it is left as authored.
        /// </summary>
        public required HashSet<(int Mesh, int Island)> OverOpenSkin;
        /// <summary>Closed loops of open edges that are not sockets (too big, too small, not a hole).</summary>
        public required int RejectedLoops;
        /// <summary>The model could not be read for sockets: nothing about its nails can be shown safe to change.</summary>
        public bool Unreadable;
        /// <summary>Nail islands wholly on a switch the game is hiding for this body: drawn by nothing, left untouched.</summary>
        public HashSet<(int Mesh, int Island)> HiddenIslands = [];
        /// <summary>Nail islands only partly hidden: left as authored.</summary>
        public HashSet<(int Mesh, int Island)> PartlyHidden = [];
    }

    /// <summary>One welded rest-skin vertex: where it is, its normal, and every copy of it in the skin meshes.</summary>
    private sealed class RimPoint
    {
        public required Vec3 P;
        public Vec3 N;
        public readonly List<(int Mesh, int Vertex)> Copies = [];
    }

    private static (int, int, int) Sorted3(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    private static ((int, int, int), (int, int, int)) EdgeKey((int, int, int) a, (int, int, int) b)
        => a.CompareTo(b) <= 0 ? (a, b) : (b, a);

    private static (int, int, int) VKey(Vec3 p) => PositionKey(p, WeldPerMetre);

    /// <summary>
    /// The welded edges of every rest-skin triangle — exactly the set <see cref="IsSewnIn"/> has always been given —
    /// built once for the whole source rather than once per mesh.
    /// </summary>
    private static HashSet<((int, int, int), (int, int, int))> RestEdges(List<NailSkinMesh> meshes, int bedMax)
    {
        var restEdges = new HashSet<((int, int, int), (int, int, int))>();
        foreach (var other in meshes)
            for (int t = 0; t + 2 < other.Tris.Count; t += 3)
            {
                if (other.IslandSize[other.Island[t / 3]] <= bedMax) continue;
                for (int e = 0; e < 3; e++)
                    restEdges.Add(WeldedEdge(other.Pos[other.Tris[t + e]], other.Pos[other.Tris[t + (e + 1) % 3]]));
            }
        return restEdges;
    }

    /// <summary>
    /// Plan every socket of a hand's finger skin. Called by <c>BodyBridge.FlattenNails</c> only — the shell build
    /// never needs it. A socket is a closed loop of open rest-skin edges (used once, after exact copies of a
    /// triangle are counted once, and on no sewn nail's outline), small enough to be a nail's, shaped like a hole, and
    /// with no skin inside it. The nail nearest its whole rim spans it; anything that cannot be done cleanly leaves
    /// the socket's nails as authored — a nail where a nail was, never a hole.
    /// </summary>
    internal static NailSocketPlan NailSockets(byte[] mdl, IReadOnlyDictionary<int, NailBedPlan> plans,
                                               Action<string>? diag = null, IReadOnlySet<string>? hidden = null)
    {
        var empty = new NailSocketPlan { Sockets = [], OverOpenSkin = [], RejectedLoops = 0 };
        if (plans.Count == 0) return empty;
        Source src;
        try { src = Parse(mdl); }
        catch { return new NailSocketPlan { Sockets = [], OverOpenSkin = [], RejectedLoops = 0, Unreadable = true }; }
        var meshes = ReadNailSkinMeshes(src);
        var byMesh = meshes.ToDictionary(m => m.Mesh);
        int biggest = 0;
        foreach (var sm in meshes) foreach (int n in sm.IslandSize.Values) biggest = Math.Max(biggest, n);
        int bedMax = (int)(biggest * NailBedIslandMax);

        // ---- Each triangle's attribute switches, and which of them the game is hiding for this body: a NAIL wholly
        // on hidden switches is drawn by nothing and is no member of a socket. (Hidden SKIN still counts as skin: open
        // edges along a hidden part are no hole a nail should span.) ----
        var maskOf = new Dictionary<(int Mesh, (int, int, int) Tri), uint>();
        foreach (var sm in meshes)
        {
            int mo = src.MeshStart + sm.Mesh * 36;
            ushort si = BitConverter.ToUInt16(src.S, mo + 10), sc = BitConverter.ToUInt16(src.S, mo + 12);
            for (int su = 0; su < sc; su++)
            {
                int ss = src.SubmeshStart + (si + su) * 16;
                if (ss + 16 > src.S.Length) break;
                uint so = BitConverter.ToUInt32(src.S, ss), cnt = BitConverter.ToUInt32(src.S, ss + 4);
                uint mask = BitConverter.ToUInt32(src.S, ss + 8);
                for (uint t = 0; t + 2 < cnt; t += 3)
                {
                    int q = src.Ib + (int)(so + t) * 2;
                    if (q + 6 > src.S.Length) break;
                    maskOf[(sm.Mesh, Sorted3(BitConverter.ToUInt16(src.S, q), BitConverter.ToUInt16(src.S, q + 2),
                                             BitConverter.ToUInt16(src.S, q + 4)))] = mask;
                }
            }
        }
        bool Hidden(int mesh, int a, int b, int c)
            => hidden is { Count: > 0 } && maskOf.TryGetValue((mesh, Sorted3(a, b, c)), out var hm) && IsHidden(src, hm, hidden);

        // ---- The rest skin's edges, each exact triangle counted once, and the nail beds' edges. ----
        var points = new Dictionary<(int, int, int), RimPoint>();
        var bedEdges = new HashSet<((int, int, int), (int, int, int))>();
        var bedVerts = new HashSet<(int Mesh, int V)>();
        foreach (var (m, plan) in plans)
            foreach (var vs in plan.VertsOf) foreach (int v in vs) bedVerts.Add((m, v));
        // The outlines of SEWN nails: a rim a nail is sewn into is no hole, though the rest skin uses each of its edges
        // once. Only those — a floating nail whose own surface happens to run along a socket's rim (FreyjaAF's
        // double-layered ones) must not hide the socket, or it collapses and opens it.
        foreach (var (m, plan) in plans)
        {
            if (!byMesh.TryGetValue(m, out var sm)) continue;
            for (int k = 0; k < plan.Islands.Count && k < plan.Sewn.Count; k++)
            {
                if (!plan.Sewn[k]) continue;
                var tris = plan.Islands[k];
                // A sewn nail wholly on hidden switches is drawn by nothing, so it closes no rim in game: its rim is a
                // socket like any other (#54) — the same rule as hiddenIslands below. One only partly hidden still
                // closes it.
                bool wholly = tris.Length >= 3;
                for (int t = 0; wholly && t + 2 < tris.Length; t += 3)
                    if (!Hidden(m, tris[t], tris[t + 1], tris[t + 2])) wholly = false;
                if (wholly) continue;
                var inIsland = new Dictionary<((int, int, int), (int, int, int)), int>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        var ek = EdgeKey(VKey(sm.Pos[tris[t + e]]), VKey(sm.Pos[tris[t + (e + 1) % 3]]));
                        inIsland[ek] = inIsland.GetValueOrDefault(ek) + 1;
                    }
                foreach (var (ek, n) in inIsland) if (n == 1) bedEdges.Add(ek);
            }
        }
        // The finger's skin: every skin triangle that is no nail — including a small patch of skin the nail-bed planner
        // left alone, which is drawn skin all the same.
        var bedTri = new Dictionary<int, HashSet<(int, int, int)>>();
        foreach (var (m, plan) in plans)
        {
            var set = bedTri[m] = [];
            foreach (var ts in plan.Islands)
                for (int t = 0; t + 2 < ts.Length; t += 3) set.Add(Sorted3(ts[t], ts[t + 1], ts[t + 2]));
        }
        bool IsRest(NailSkinMesh sm, int t)
            => !(bedTri.TryGetValue(sm.Mesh, out var set) && set.Contains(Sorted3(sm.Tris[t], sm.Tris[t + 1], sm.Tris[t + 2])));
        var seenTri = new HashSet<((int, int, int), (int, int, int), (int, int, int))>();
        var use = new Dictionary<((int, int, int), (int, int, int)), int>();
        // Each edge's far corner in the triangle that uses it: what the hole test looks at.
        var opposite = new Dictionary<((int, int, int), (int, int, int)), Vec3>();
        var restTris = new List<((int, int, int) A, (int, int, int) B, (int, int, int) C)>();
        int deduped = 0;
        foreach (var sm in meshes)
        {
            // The rest skin's vertices — those its triangles use; a vertex nothing draws is no skin.
            var used = new HashSet<int>();
            for (int t = 0; t + 2 < sm.Tris.Count; t += 3)
                if (IsRest(sm, t)) { used.Add(sm.Tris[t]); used.Add(sm.Tris[t + 1]); used.Add(sm.Tris[t + 2]); }
            for (int v = 0; v < sm.Pos.Length; v++)
            {
                if (bedVerts.Contains((sm.Mesh, v)) || !used.Contains(v)) continue;
                var k = VKey(sm.Pos[v]);
                if (!points.TryGetValue(k, out var rp)) points[k] = rp = new RimPoint { P = sm.Pos[v], N = sm.Nrm[v] };
                rp.Copies.Add((sm.Mesh, v));
            }
            for (int t = 0; t + 2 < sm.Tris.Count; t += 3)
            {
                if (!IsRest(sm, t)) continue;
                var ka = VKey(sm.Pos[sm.Tris[t]]); var kb = VKey(sm.Pos[sm.Tris[t + 1]]); var kc = VKey(sm.Pos[sm.Tris[t + 2]]);
                var sorted = new[] { ka, kb, kc }; Array.Sort(sorted);
                if (!seenTri.Add((sorted[0], sorted[1], sorted[2]))) { deduped++; continue; }
                restTris.Add((ka, kb, kc));
                var ks = new[] { ka, kb, kc };
                for (int e = 0; e < 3; e++)
                {
                    var ek = EdgeKey(ks[e], ks[(e + 1) % 3]);
                    use[ek] = use.GetValueOrDefault(ek) + 1;
                    opposite[ek] = sm.Pos[sm.Tris[t + (e + 2) % 3]];
                }
            }
        }
        var open = new List<((int, int, int) A, (int, int, int) B)>();
        foreach (var (ek, n) in use)
            if (n == 1 && !bedEdges.Contains(ek) && points.ContainsKey(ek.Item1) && points.ContainsKey(ek.Item2))
                open.Add(ek);

        // ---- Nail islands: positions, and how many open edges lie near each. A nail wholly on a hidden switch is not
        // drawn: no member of anything, and left untouched. One only PARTLY hidden is left as authored. ----
        var islandVerts = new Dictionary<(int Mesh, int Island), Vec3[]>();
        var hiddenIslands = new HashSet<(int Mesh, int Island)>();
        var partlyHidden = new HashSet<(int Mesh, int Island)>();
        foreach (var (m, plan) in plans)
        {
            if (!byMesh.TryGetValue(m, out var sm)) continue;
            for (int k = 0; k < plan.VertsOf.Count; k++)
            {
                var ts = plan.Islands[k];
                int nHidden = 0, nTri = ts.Length / 3;
                for (int t = 0; t + 2 < ts.Length; t += 3) if (Hidden(m, ts[t], ts[t + 1], ts[t + 2])) nHidden++;
                if (nHidden > 0 && nHidden == nTri) { hiddenIslands.Add((m, k)); continue; }
                if (nHidden > 0) partlyHidden.Add((m, k));
                if (k >= plan.Sewn.Count || !plan.Sewn[k])
                    islandVerts[(m, k)] = plan.VertsOf[k].Select(v => sm.Pos[v]).ToArray();
            }
        }
        Vec3 Mid((int, int, int) a, (int, int, int) b)
        {
            var (p, q) = (points[a].P, points[b].P);
            return new Vec3((p.X + q.X) / 2, (p.Y + q.Y) / 2, (p.Z + q.Z) / 2);
        }
        static float Nearest(Vec3 p, Vec3[] vs)
        {
            float best = float.MaxValue;
            foreach (var v in vs) best = MathF.Min(best, Dist(p, v));
            return best;
        }
        var openNear = new Dictionary<(int, int), int>();
        foreach (var (id, vs) in islandVerts)
            openNear[id] = open.Count(e => Nearest(Mid(e.A, e.B), vs) <= NailSocketReach);

        // ---- Closed loops of open edges. ----
        var adj = new Dictionary<(int, int, int), List<(int, int, int)>>();
        foreach (var (a, b) in open)
        {
            (adj.TryGetValue(a, out var la) ? la : adj[a] = []).Add(b);
            (adj.TryGetValue(b, out var lb) ? lb : adj[b] = []).Add(a);
        }
        var done = new HashSet<(int, int, int)>();
        var sockets = new List<NailSocket>();
        var loopKeys = new List<List<(int, int, int)>>();
        int rejected = 0;
        var reasons = new Dictionary<string, int>();
        foreach (var start in adj.Keys)
        {
            if (done.Contains(start)) continue;
            var comp = new List<(int, int, int)>();
            var stack = new Stack<(int, int, int)>();
            stack.Push(start); done.Add(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                comp.Add(c);
                foreach (var n in adj[c]) if (done.Add(n)) stack.Push(n);
            }
            // Near any nail at all? A loop nowhere near one (the wrist, a neckline) is not this pass's business.
            bool nearNail = islandVerts.Values.Any(vs => comp.Any(k => Nearest(points[k].P, vs) <= NailSocketOwnReach));
            if (!nearNail) continue;
            if (comp.Count < NailSocketMinEdges || comp.Any(k => adj[k].Count != 2)) { rejected++; continue; }
            var loop = WalkLoop(comp[0], adj);
            if (loop.Count != comp.Count) { rejected++; continue; }
            float span = 0f;
            foreach (var a in loop) foreach (var b in loop) span = MathF.Max(span, Dist(points[a].P, points[b].P));
            if (span > NailSocketMaxSpan) { rejected++; continue; }

            var lp = loop.Select(k => points[k].P).ToArray();
            var frame = LoopFrame(lp, loop.Select(k => points[k].N).ToArray());
            var poly = lp.Select(frame.Flat).ToArray();
            bool wraps = SelfIntersects(poly);
            if (!wraps && !IsHole(loop, poly, frame, opposite)) { rejected++; continue; }

            var socket = new NailSocket
            {
                Rim = [.. loop.Select((k, i) => (points[k].P, points[loop[(i + 1) % loop.Count]].P))],
                Members = [],
            };
            foreach (var (id, vs) in islandVerts)
            {
                bool member = false;
                for (int i = 0; i < loop.Count && !member; i++)
                    member = Nearest(Mid(loop[i], loop[(i + 1) % loop.Count]), vs) <= NailSocketReach;
                if (member) socket.Members.Add(id);
            }
            if (socket.Members.Count == 0) { rejected++; continue; }
            if (wraps) socket.LeftAsAuthored = "rim wraps the finger";
            // Skin INSIDE the rim, at the rim's own height: the socket is a ring round a patch of finger (LavaBod+'s
            // two-part rims), and a nail spanning the outer rim would lie on that patch. Left as authored. The far
            // side of the finger also projects inside the rim, but a finger's width below it.
            if (socket.LeftAsAuthored == null)
            {
                var loopSet = loop.ToHashSet();
                float hMin = lp.Min(frame.H);
                int inside = 0;
                foreach (var (k, q) in points)
                {
                    if (loopSet.Contains(k) || frame.H(q.P) < hMin - NailSocketSkinBelow) continue;
                    if (Dist(q.P, frame.C) > span) continue;
                    if (Inside(poly, frame.Flat(q.P)) && ++inside >= NailSocketMinEdges) break;
                }
                if (inside >= NailSocketMinEdges) socket.LeftAsAuthored = "skin inside the socket";
            }
            sockets.Add(socket);
            loopKeys.Add(loop);
        }

        // ---- Owners: the member nearest the WHOLE rim (every rim vertex within NailSocketOwnReach of it), closed
        // islands included. ----
        var ownerOf = new Dictionary<(int, int), List<int>>();
        for (int si = 0; si < sockets.Count; si++)
        {
            var sock = sockets[si];
            if (sock.LeftAsAuthored != null) continue;
            var lp = loopKeys[si].Select(k => points[k].P).ToArray();
            (int, int)? best = null;
            float bestFar = float.MaxValue;
            foreach (var id in sock.Members)
            {
                var vs = islandVerts[id];
                float far = lp.Max(p => Nearest(p, vs));
                if (far < bestFar) { bestFar = far; best = id; }
            }
            if (best is not { } b || bestFar > NailSocketOwnReach) { sock.LeftAsAuthored = "no nail near the whole rim"; continue; }
            sock.Owner = b;
            (ownerOf.TryGetValue(b, out var l) ? l : ownerOf[b] = []).Add(si);
        }
        foreach (var (_, list) in ownerOf)
            if (list.Count > 1)
                foreach (int si in list) { sockets[si].Owner = null; sockets[si].LeftAsAuthored = "one nail nearest two sockets"; }

        // ---- Every nail of a socket on the same attribute switches as its owner. ----
        // The game hides submeshes by attribute: were the owner on one switch and a nail it collapses on another, hiding
        // the owner's would leave the collapsed nail showing over an open socket. Such a socket is left as authored. Only
        // DRAWN nails count: one on a switch hidden for this body is no member (Stabbies' alternative nail layers).
        HashSet<uint> MasksOf((int Mesh, int Island) id)
        {
            var set = new HashSet<uint>();
            var ts = plans[id.Mesh].Islands[id.Island];
            for (int t = 0; t + 2 < ts.Length; t += 3)
                set.Add(maskOf.TryGetValue((id.Mesh, Sorted3(ts[t], ts[t + 1], ts[t + 2])), out var mk) ? mk : 0u);
            return set;
        }
        foreach (var sock in sockets)
        {
            if (sock.Owner is not { } own) continue;
            var ownMasks = MasksOf(own);
            if (sock.Members.Any(partlyHidden.Contains))
            { sock.Owner = null; sock.LeftAsAuthored = "a nail partly on a hidden switch"; }
            else if (sock.Members.Any(m => !MasksOf(m).SetEquals(ownMasks)))
            { sock.Owner = null; sock.LeftAsAuthored = "the nails are on different attribute switches"; }
        }

        // ---- Span each owned socket. ----
        for (int si = 0; si < sockets.Count; si++)
        {
            var sock = sockets[si];
            if (sock.Owner is not { } id) continue;
            var plan = plans[id.Mesh];
            var sm = byMesh[id.Mesh];
            var loop = loopKeys[si];
            string? why = SpanSocket(sock, sm, plan.Islands[id.Island], plan.VertsOf[id.Island],
                                     loop.Select(k => points[k]).ToArray(), points, restTris);
            if (why != null) { sock.Owner = null; sock.LeftAsAuthored = why; sock.Spanned.Clear(); sock.RimNormal.Clear(); sock.RimMoves.Clear(); }
        }

        // ---- A nail cannot both stay and go: any socket touching a nail left as authored is left as authored. ----
        bool changed = true;
        while (changed)
        {
            changed = false;
            var authored = new HashSet<(int, int)>();
            var roles = new Dictionary<(int, int), HashSet<bool>>();   // true = owner, false = collapsed member
            foreach (var s in sockets)
            {
                if (s.Owner == null) { foreach (var m in s.Members) authored.Add(m); continue; }
                foreach (var m in s.Members)
                    (roles.TryGetValue(m, out var r) ? r : roles[m] = []).Add(s.Owner == m);
            }
            foreach (var s in sockets)
            {
                if (s.Owner == null) continue;
                if (s.Members.Any(m => authored.Contains(m) || roles[m].Count > 1))
                {
                    s.Owner = null; s.LeftAsAuthored ??= "shares a nail with a socket left as authored";
                    s.Spanned.Clear(); s.RimNormal.Clear(); s.RimMoves.Clear();
                    changed = true;
                }
            }
        }

        var inSocket = new HashSet<(int, int)>(sockets.SelectMany(s => s.Members));
        var over = new HashSet<(int Mesh, int Island)>();
        foreach (var (id, n) in openNear)
            if (n >= NailSocketMinEdges && !inSocket.Contains(id)) over.Add(id);

        foreach (var s in sockets.Where(s => s.Owner == null))
            reasons[s.LeftAsAuthored!] = reasons.GetValueOrDefault(s.LeftAsAuthored!) + 1;
        diag?.Invoke($"nail sockets: {sockets.Count} found ({rejected} other loop(s) of open edges rejected, "
                   + $"{deduped} repeated triangle(s) counted once), {sockets.Count(s => s.Owner != null)} spanned"
                   + (reasons.Count > 0 ? ", left as authored: " + string.Join(", ", reasons.Select(kv => $"{kv.Value} {kv.Key}")) : "")
                   + $", {over.Count} nail(s) over open skin outside any socket"
                   + string.Concat(sockets.Where(s => s.Owner != null)
                         .Select(s => $"; nail moved off its own shape up to {s.MiddleOffset * 1000:F2}mm")));
        return new NailSocketPlan { Sockets = sockets, OverOpenSkin = over, RejectedLoops = rejected,
                                    HiddenIslands = hiddenIslands, PartlyHidden = partlyHidden };
    }

    /// <summary>Walk a component whose every vertex has two neighbours, in order.</summary>
    private static List<(int, int, int)> WalkLoop((int, int, int) start, Dictionary<(int, int, int), List<(int, int, int)>> adj)
    {
        var loop = new List<(int, int, int)> { start };
        var prev = start;
        var cur = adj[start][0];
        while (cur != start && loop.Count <= adj.Count)
        {
            loop.Add(cur);
            var next = adj[cur][0] == prev ? adj[cur][1] : adj[cur][0];
            prev = cur;
            cur = next;
        }
        return loop;
    }

    /// <summary>A loop's best-fit plane: its centre, two in-plane axes, and the normal facing out of the finger.</summary>
    private readonly record struct Frame(Vec3 C, Vec3 U, Vec3 W, Vec3 N)
    {
        public (float X, float Y) Flat(Vec3 p)
        {
            float dx = p.X - C.X, dy = p.Y - C.Y, dz = p.Z - C.Z;
            return (dx * U.X + dy * U.Y + dz * U.Z, dx * W.X + dy * W.Y + dz * W.Z);
        }
        public float H(Vec3 p) => (p.X - C.X) * N.X + (p.Y - C.Y) * N.Y + (p.Z - C.Z) * N.Z;
    }

    private static Frame LoopFrame(Vec3[] pts, Vec3[] normals)
    {
        double cx = 0, cy = 0, cz = 0;
        foreach (var p in pts) { cx += p.X; cy += p.Y; cz += p.Z; }
        cx /= pts.Length; cy /= pts.Length; cz /= pts.Length;
        var cov = new double[3, 3];
        foreach (var p in pts)
        {
            double[] d = { p.X - cx, p.Y - cy, p.Z - cz };
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) cov[i, j] += d[i] * d[j];
        }
        // The smallest axis of the spread: power iteration on (trace·I − cov), whose largest axis it is.
        double tr = cov[0, 0] + cov[1, 1] + cov[2, 2];
        double[] n = { 0.31, 0.53, 0.79 };
        for (int it = 0; it < 100; it++)
        {
            var r = new double[3];
            for (int i = 0; i < 3; i++) { r[i] = tr * n[i]; for (int j = 0; j < 3; j++) r[i] -= cov[i, j] * n[j]; }
            double len = Math.Sqrt(r[0] * r[0] + r[1] * r[1] + r[2] * r[2]);
            if (len < 1e-30) break;
            for (int i = 0; i < 3; i++) n[i] = r[i] / len;
        }
        var nv = new Vec3((float)n[0], (float)n[1], (float)n[2]);
        float sx = 0, sy = 0, sz = 0;
        foreach (var q in normals) { sx += q.X; sy += q.Y; sz += q.Z; }
        if (nv.X * sx + nv.Y * sy + nv.Z * sz < 0) nv = new Vec3(-nv.X, -nv.Y, -nv.Z);
        Basis(nv, out var u, out var w);
        return new Frame(new Vec3((float)cx, (float)cy, (float)cz), u, w, nv);
    }

    private static bool SelfIntersects((float X, float Y)[] poly)
    {
        int n = poly.Length;
        static float Cross((float X, float Y) o, (float X, float Y) a, (float X, float Y) b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        for (int i = 0; i < n; i++)
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;
                var (a, b, c, d) = (poly[i], poly[(i + 1) % n], poly[j], poly[(j + 1) % n]);
                float d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
                if ((d1 > 0) != (d2 > 0) && (d3 > 0) != (d4 > 0) && d1 != 0 && d2 != 0 && d3 != 0 && d4 != 0) return true;
            }
        return false;
    }

    private static bool Inside((float X, float Y)[] poly, (float X, float Y) p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y)
                && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>
    /// Is the loop a HOLE in the skin — does the skin around it lie outside it? A rest quad that merely sits under a
    /// nail of the same outline has its triangles inside its own loop, and spanning that would lay a second skin on
    /// top of the first.
    /// </summary>
    private static bool IsHole(List<(int, int, int)> loop, (float X, float Y)[] poly, Frame frame,
                               Dictionary<((int, int, int), (int, int, int)), Vec3> opposite)
    {
        int outside = 0, asked = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            if (!opposite.TryGetValue(EdgeKey(loop[i], loop[(i + 1) % loop.Count]), out var far)) continue;
            asked++;
            if (!Inside(poly, frame.Flat(far))) outside++;
        }
        return asked > 0 && outside * 2 > asked;
    }

    /// <summary>
    /// Span one socket with its owner island: match the island's outline to the rim in order, lay it out across the
    /// socket in the rim's plane (Tutte), and lift it to the authored nail's height less the nail's thickness, running
    /// smoothly down to the rim past the nail's edge. Nothing is kept unless the layout is sound, nothing folds, and
    /// rays onto the seam meet skin. Returns why it could not, or null when <paramref name="sock"/> is filled in.
    /// </summary>
    private static string? SpanSocket(NailSocket sock, NailSkinMesh sm, int[] tris, int[] verts,
                                      RimPoint[] rim,
                                      Dictionary<(int, int, int), RimPoint> points,
                                      List<((int, int, int) A, (int, int, int) B, (int, int, int) C)> restTris)
    {
        // Welded nodes of the island, and its outline loops.
        Dictionary<int, int> nodeOf = [];
        List<Vec3> nodePos = [];
        HashSet<int>[] nbr = [];
        List<List<int>> Outline(int[] ts, IEnumerable<int> vs, Dictionary<(int, int, int), (int, int, int)>? alias = null)
        {
            nodeOf = [];
            nodePos = [];
            var nodeKey = new Dictionary<(int, int, int), int>();
            foreach (int v in vs)
            {
                var k = VKey(sm.Pos[v]);
                if (alias != null && alias.TryGetValue(k, out var to)) k = to;
                if (!nodeKey.TryGetValue(k, out int n)) { nodeKey[k] = n = nodePos.Count; nodePos.Add(sm.Pos[v]); }
                nodeOf[v] = n;
            }
            var eUse = new Dictionary<(int, int), int>();
            nbr = new HashSet<int>[nodePos.Count];
            for (int i = 0; i < nbr.Length; i++) nbr[i] = [];
            for (int t = 0; t + 2 < ts.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = nodeOf[ts[t + e]], b = nodeOf[ts[t + (e + 1) % 3]];
                    if (a == b) continue;
                    nbr[a].Add(b); nbr[b].Add(a);
                    var k = (Math.Min(a, b), Math.Max(a, b));
                    eUse[k] = eUse.GetValueOrDefault(k) + 1;
                }
            var bAdj = new Dictionary<int, List<int>>();
            foreach (var ((a, b), n) in eUse)
                if (n == 1)
                {
                    (bAdj.TryGetValue(a, out var la) ? la : bAdj[a] = []).Add(b);
                    (bAdj.TryGetValue(b, out var lb) ? lb : bAdj[b] = []).Add(a);
                }
            var loops = new List<List<int>>();
            if (bAdj.Values.Any(l => l.Count != 2)) return [[]];   // a pinch: not a loop at all
            var seen = new HashSet<int>();
            foreach (int st in bAdj.Keys)
            {
                if (!seen.Add(st)) continue;
                var l = new List<int> { st };
                int prev = st, cur = bAdj[st][0];
                while (cur != st && seen.Add(cur))
                {
                    l.Add(cur);
                    int next = bAdj[cur][0] == prev ? bAdj[cur][1] : bAdj[cur][0];
                    prev = cur; cur = next;
                }
                loops.Add(l);
            }
            return loops;
        }
        float Perim(IReadOnlyList<Vec3> pts) { float s = 0; for (int i = 0; i < pts.Count; i++) s += Dist(pts[i], pts[(i + 1) % pts.Count]); return s; }

        var outline = Outline(tris, verts);
        // A pinhole in the nail — a quad missing from the mesh (YAB+'s middle fingers) — is a second outline, and would be
        // a hole in the patch. One that small is welded shut: its corners become one node, its triangles meet there.
        if (outline.Count > 1)
        {
            var outer = outline.OrderByDescending(l => Perim(l.Select(n => nodePos[n]).ToList())).First();
            float outerLen = Perim(outer.Select(n => nodePos[n]).ToList());
            var holes = outline.Where(l => !ReferenceEquals(l, outer)).ToList();
            if (holes.All(h => h.Count >= 3 && h.Count <= NailPinholeMaxNodes
                               && Perim(h.Select(n => nodePos[n]).ToList()) <= outerLen * NailPinholeMaxShare))
            {
                var alias = new Dictionary<(int, int, int), (int, int, int)>();
                foreach (var h in holes)
                {
                    var rep = VKey(nodePos[h[0]]);
                    foreach (int n in h) alias[VKey(nodePos[n])] = rep;
                }
                outline = Outline(tris, verts, alias);
            }
        }
        if (outline.Count == 0) return "the nearest nail is closed";
        if (outline.Any(l => l.Count < 3)) return "the nearest nail's outline is not one loop";
        // One outline only. A nail with a hole in it bigger than a pinhole — a double-layered nail (FreyjaAF) — is left as
        // authored: spanning it could not be checked as a single disk.
        if (outline.Count != 1) return "the nearest nail's outline is not one loop";
        var bLoop = outline[0];
        int nn = nodePos.Count;

        var rp = rim.Select(r => r.P).ToArray();
        var bp = bLoop.Select(n => nodePos[n]).ToList();
        float rimLen = Perim(rp), bLen = Perim(bp);
        if (bLen <= 0 || rimLen < bLen * 0.5f || rimLen > bLen * 2f) return "rim and nail differ in size";

        var frame = LoopFrame(rp, rim.Select(r => r.N).ToArray());
        var matchWhy = MatchLoopOnto(rim, bp, frame, out var lm);
        if (matchWhy != null) return matchWhy;
        if (lm!.Reversed) bLoop.Reverse();
        var R = lm.R;
        var Rp = R.Select(r => r.P).ToArray();
        var target = lm.Target;
        var rimN = lm.RimN;
        var rimMoves = lm.RimMoves;
        int nb = bLoop.Count, nr = R.Length;

        // The authored nail as a height field over the rim's plane: its triangles that face out of the finger (an
        // edge curling over the fingertip is no guide to the finger under it), and its outline to carry that height
        // on past its edge. Measured on Rue+ against the finger's own cross-section, the authored nail sits within a
        // millimetre of the finger at its middle — a far better guide than any surface fitted to the rim, which on
        // a rim that is nearly an ellipse cannot say how domed the middle is (it left the patch 1–2 mm inside).
        float wound = 0;
        var faceN = new List<Vec3>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            var tn = TriNormalRaw(nodePos[nodeOf[tris[t]]], nodePos[nodeOf[tris[t + 1]]], nodePos[nodeOf[tris[t + 2]]]);
            faceN.Add(tn);
            wound += tn.X * frame.N.X + tn.Y * frame.N.Y + tn.Z * frame.N.Z;
        }
        float sign = wound >= 0 ? 1f : -1f;
        var top = new List<(double Ax, double Ay, double Ah, double Bx, double By, double Bh, double Cx, double Cy, double Ch)>();
        for (int t = 0, f = 0; t + 2 < tris.Length; t += 3, f++)
        {
            var tn = faceN[f];
            float tl = MathF.Sqrt(tn.X * tn.X + tn.Y * tn.Y + tn.Z * tn.Z);   // by hand: a raw normal of a millimetre triangle is far under NormalizeOr's floor
            if (tl <= 0) continue;
            tn = new Vec3(tn.X / tl, tn.Y / tl, tn.Z / tl);
            if (sign * (tn.X * frame.N.X + tn.Y * frame.N.Y + tn.Z * frame.N.Z) <= NailTopFacing) continue;
            var (a, b, c) = (nodePos[nodeOf[tris[t]]], nodePos[nodeOf[tris[t + 1]]], nodePos[nodeOf[tris[t + 2]]]);
            var (fa, fb, fc) = (frame.Flat(a), frame.Flat(b), frame.Flat(c));
            top.Add((fa.X, fa.Y, frame.H(a), fb.X, fb.Y, frame.H(b), fc.X, fc.Y, frame.H(c)));
        }
        double? Authored(double x, double y)
        {
            foreach (var q in top)
            {
                double den = (q.By - q.Cy) * (q.Ax - q.Cx) + (q.Cx - q.Bx) * (q.Ay - q.Cy);
                if (Math.Abs(den) < 1e-18) continue;
                double l1 = ((q.By - q.Cy) * (x - q.Cx) + (q.Cx - q.Bx) * (y - q.Cy)) / den;
                double l2 = ((q.Cy - q.Ay) * (x - q.Cx) + (q.Ax - q.Cx) * (y - q.Cy)) / den;
                double l3 = 1 - l1 - l2;
                if (l1 >= -1e-6 && l2 >= -1e-6 && l3 >= -1e-6) return l1 * q.Ah + l2 * q.Bh + l3 * q.Ch;
            }
            return null;   // past the nail's edge, in the gap to the rim
        }

        // The nail's thickness, place by place: how far each outline node stands above the finger's tangent plane at
        // the rim point it is matched to — measured along the FINGER's normal there, not the nail's (on the side of a
        // finger the outline sits up the slope from the rim, and along the nail's own tilted normal that sideways step
        // read as the nail being inset). It is carried across the nail harmonically, and the nail comes down by it
        // where it lies: a Rue+ nail stands a millimetre proud at its sides and a tenth at its middle, and one
        // thickness for the lot left a wall at the side of the socket.
        var thick = new double[nn];
        {
            var known = new bool[nn];
            for (int j = 0; j < nb; j++)
            {
                var p = nodePos[bLoop[j]];
                var fnj = rimN[j];
                thick[bLoop[j]] = (p.X - target[j].X) * fnj.X + (p.Y - target[j].Y) * fnj.Y + (p.Z - target[j].Z) * fnj.Z;
                known[bLoop[j]] = true;
            }
            for (int it = 0; it < 4000; it++)
            {
                double moved = 0;
                for (int n = 0; n < nn; n++)
                {
                    if (known[n] || nbr[n].Count == 0) continue;
                    double s = 0;
                    foreach (int k in nbr[n]) s += thick[k];
                    s /= nbr[n].Count;
                    moved = Math.Max(moved, Math.Abs(s - thick[n]));
                    thick[n] = s;
                }
                if (moved < 1e-9) break;
            }
        }

        // Lay the island out across the socket in the rim's plane: its outline on the rim, every inside node at the
        // mean of its neighbours (a Tutte layout — on a rim that does not fold back on itself, no triangle can turn
        // over, which is what a curled nail edge carried along by its authored shape did). Then lift it: where a node
        // lies over the nail it takes the nail's height moved by that mean offset (the nail's shape, its thickness
        // taken off); in the gap between the nail's edge and the rim, the height runs smoothly from the one to the
        // other (harmonic), so there is neither a step at the nail's edge nor a lip at the rim.
        var pinned = new bool[nn];
        var bIndex = new Dictionary<int, int>();
        var lx = new double[nn]; var ly = new double[nn];
        for (int n = 0; n < nn; n++) { var (x, y) = frame.Flat(nodePos[n]); lx[n] = x; ly[n] = y; }
        for (int j = 0; j < nb; j++)
        {
            int n = bLoop[j];
            bIndex[n] = j;
            pinned[n] = true;
            var (x, y) = frame.Flat(target[j]);
            lx[n] = x; ly[n] = y;
        }
        for (int it = 0; it < 4000; it++)
        {
            double movedSq = 0;
            for (int n = 0; n < nn; n++)
            {
                if (pinned[n] || nbr[n].Count == 0) continue;
                double x = 0, y = 0;
                foreach (int k in nbr[n]) { x += lx[k]; y += ly[k]; }
                int c = nbr[n].Count;
                x /= c; y /= c;
                movedSq = Math.Max(movedSq, (x - lx[n]) * (x - lx[n]) + (y - ly[n]) * (y - ly[n]));
                lx[n] = x; ly[n] = y;
            }
            if (movedSq < 1e-18) break;
        }
        var lh = new double[nn];
        var fixedH = new bool[nn];
        for (int n = 0; n < nn; n++)
        {
            if (pinned[n]) { lh[n] = frame.H(target[bIndex[n]]); fixedH[n] = true; continue; }
            if (Authored(lx[n], ly[n]) is { } ah) { lh[n] = ah - thick[n]; fixedH[n] = true; }
        }
        for (int it = 0; it < 4000; it++)
        {
            double moved = 0;
            for (int n = 0; n < nn; n++)
            {
                if (fixedH[n] || nbr[n].Count == 0) continue;
                double h = 0;
                foreach (int k in nbr[n]) h += lh[k];
                h /= nbr[n].Count;
                moved = Math.Max(moved, Math.Abs(h - lh[n]));
                lh[n] = h;
            }
            if (moved < 1e-9) break;
        }
        var final = new Vec3[nn];
        float middle = (float)thick.Max(t => Math.Abs(t));
        for (int n = 0; n < nn; n++)
        {
            if (pinned[n]) { final[n] = target[bIndex[n]]; continue; }
            double h = lh[n];
            final[n] = new Vec3((float)(frame.C.X + frame.U.X * lx[n] + frame.W.X * ly[n] + frame.N.X * h),
                                (float)(frame.C.Y + frame.U.Y * lx[n] + frame.W.Y * ly[n] + frame.N.Y * h),
                                (float)(frame.C.Z + frame.U.Z * lx[n] + frame.W.Z * ly[n] + frame.N.Z * h));
        }

        // The finger's outward direction at a point over the socket: the rim's own normals, weighted by nearness.
        Vec3 FingerNormal(Vec3 p)
        {
            float x = 0, y = 0, z = 0;
            foreach (var r in R)
            {
                float d = Dist(p, r.P), w = 1f / MathF.Max(d * d, 1e-10f);
                x += r.N.X * w; y += r.N.Y * w; z += r.N.Z * w;
            }
            return NormalizeOr(new Vec3(x, y, z), frame.N);
        }

        // The layout itself must be sound: every triangle the same way round in the rim's plane as the island is wound.
        // A Tutte layout is guaranteed that only inside a convex rim; where a rim bends in, nodes can land past it, and
        // the patch then folds back over the skin at the seam, leaving a sliver of the socket open on the other side.
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
            if (a == b || b == c || a == c || (pinned[a] && pinned[b] && pinned[c])) continue;
            double area2 = (lx[b] - lx[a]) * (ly[c] - ly[a]) - (lx[c] - lx[a]) * (ly[b] - ly[a]);
            if (sign * area2 <= 0) return "the rim bends in too far to span";
        }

        // The finger as it would be with no nail on it: heights filled across the socket smoothly (the least bending —
        // a bilaplacian over the patch), held at the rim AND at the ring of skin just outside it, so the patch leaves
        // the rim at the finger's own slope. The nail-guided heights above copy the nail's dome with its thickness
        // taken off, and leave a groove where they drop to the rim: an outline of the nail under any garment. They
        // stay as the fallback when the smooth fill fails a check below.
        var smooth = SmoothFill();
        Vec3[]? SmoothFill()
        {
            var rimKeys = R.Select(r => VKey(r.P)).ToHashSet();
            var outer = new Dictionary<(int, int, int), HashSet<(int, int, int)>>();
            foreach (var (ka, kb, kc) in restTris)
                foreach (var (x, y) in new[] { (ka, kb), (kb, kc), (kc, ka), (kb, ka), (kc, kb), (ka, kc) })
                    if (rimKeys.Contains(x) && !rimKeys.Contains(y) && points.ContainsKey(y))
                        (outer.TryGetValue(x, out var hs) ? hs : outer[x] = []).Add(y);
            var ghost = new List<double>[nn];
            var cnt = new int[nn];
            for (int n = 0; n < nn; n++)
            {
                ghost[n] = [];
                if (pinned[n])
                {
                    var t = target[bIndex[n]];
                    var near = R.MinBy(r => Dist(r.P, t))!;
                    // Skin standing ABOVE the rim — the cuticle's fold, a fingertip curling up past the socket — is no
                    // slope for the finger under the nail: met, it sends the patch diving into the socket. Held level.
                    double rimH = frame.H(t);
                    if (outer.TryGetValue(VKey(near.P), out var gs))
                        foreach (var g in gs) ghost[n].Add(Math.Min(frame.H(points[g].P), rimH));
                    if (ghost[n].Count == 0) return null;   // no skin beside the rim here: no slope to meet
                }
                cnt[n] = nbr[n].Count + ghost[n].Count;
            }
            var h = (double[])lh.Clone();
            double LAt(int i)
            {
                if (cnt[i] == 0) return 0;
                double sum = 0;
                foreach (int k in nbr[i]) sum += h[k];
                foreach (double g in ghost[i]) sum += g;
                return sum / cnt[i] - h[i];
            }
            for (int it = 0; it < 6000; it++)
            {
                double moved = 0;
                for (int k = 0; k < nn; k++)
                {
                    if (pinned[k] || cnt[k] == 0) continue;
                    double num = 0, den = 0;
                    double l0 = LAt(k), r0 = l0 + h[k];
                    num += -r0; den += 1;
                    foreach (int i in nbr[k])
                    {
                        if (cnt[i] == 0) continue;
                        double ai = 1.0 / cnt[i], li = LAt(i), ri = li - ai * h[k];
                        num += ai * ri; den += ai * ai;
                    }
                    double nh = -num / den;
                    moved = Math.Max(moved, Math.Abs(nh - h[k]));
                    h[k] = nh;
                }
                if (moved < 1e-10) break;
            }
            // Never above the authored nail where the nail lies over it: a garment over the finger must not stand
            // further out than the nail it replaces.
            for (int n = 0; n < nn; n++)
                if (!pinned[n] && Authored(lx[n], ly[n]) is { } top && h[n] > top) h[n] = top;
            // Never under the rim's lowest point by more than the grader's floor allows (a fill that sank, like the
            // drum, is no finger): then the nail-guided heights are used instead.
            double floor = Enumerable.Range(0, nn).Where(n => pinned[n]).Min(n => h[n]) - NailFillFloor;
            for (int n = 0; n < nn; n++) if (!pinned[n] && h[n] < floor) return null;
            var f = new Vec3[nn];
            for (int n = 0; n < nn; n++)
            {
                if (pinned[n]) { f[n] = target[bIndex[n]]; continue; }
                f[n] = new Vec3((float)(frame.C.X + frame.U.X * lx[n] + frame.W.X * ly[n] + frame.N.X * h[n]),
                                (float)(frame.C.Y + frame.U.Y * lx[n] + frame.W.Y * ly[n] + frame.N.Y * h[n]),
                                (float)(frame.C.Z + frame.U.Z * lx[n] + frame.W.Z * ly[n] + frame.N.Z * h[n]));
            }
            return f;
        }

        string? Verify(Vec3[] final)
        {
            // Nothing written unless the result is finite, every triangle of it faces out of the finger the way the
            // island is wound, and none has lost its area.
            static bool Finite(Vec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
            for (int n = 0; n < nn; n++) if (!Finite(final[n])) return "the span came out non-finite";
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
                if (a == b || b == c || a == c) continue;
                var after = TriNormalRaw(final[a], final[b], final[c]);
                // A triangle whose three corners are all on the outline lands flat on the rim where the rim runs straight
                // (a corner of the nail on the side of the socket): no area, nothing drawn, nothing overlapped.
                bool onRim = pinned[a] && pinned[b] && pinned[c];
                if (TriArea(final[a], final[b], final[c]) < 1e-12f) { if (onRim) continue; return "the span folds"; }
                // Folded only if it faces INTO the finger (against the finger's direction there — the rim's normals,
                // weighted by nearness — not one normal for the whole socket) AND against the way it was authored. A wall
                // that copies the authored nail's own steep side passes; a triangle turned over passes neither.
                var cen = new Vec3((final[a].X + final[b].X + final[c].X) / 3, (final[a].Y + final[b].Y + final[c].Y) / 3,
                                   (final[a].Z + final[b].Z + final[c].Z) / 3);
                var fn = FingerNormal(cen);
                var was = TriNormalRaw(nodePos[a], nodePos[b], nodePos[c]);
                bool intoFinger = sign * (after.X * fn.X + after.Y * fn.Y + after.Z * fn.Z) <= 0;
                bool turned = after.X * was.X + after.Y * was.Y + after.Z * was.Z <= 0;
                if (intoFinger && turned) return "the span folds";
            }
            foreach (var (ka, kb, kc) in restTris)
            {
                if (!rimMoves.ContainsKey(ka) && !rimMoves.ContainsKey(kb) && !rimMoves.ContainsKey(kc)) continue;
                Vec3 P0(( int, int, int) k) => points.TryGetValue(k, out var q) ? q.P : default;
                Vec3 P1((int, int, int) k) => rimMoves.TryGetValue(k, out var to) ? to : P0(k);
                var before = TriNormalRaw(P0(ka), P0(kb), P0(kc));
                var after = TriNormalRaw(P1(ka), P1(kb), P1(kc));
                if (TriArea(P1(ka), P1(kb), P1(kc)) < 1e-12f) return "the rim folds";
                if (before.X * after.X + before.Y * after.Y + before.Z * after.Z <= 0) return "the rim folds";
            }

            // Last, look at the seam the way a garment over it would: a ray down onto the skin just inside each rim edge
            // must meet the patch or the finger from outside, near. Measured on Rue+ Ballerina, a patch can be sound in
            // every other respect and still leave a sliver of the socket open beside the rim where the finger turns
            // sharply; that socket is left as authored instead.
            {
                Vec3 Now((int, int, int) k) => rimMoves.TryGetValue(k, out var to) ? to : points[k].P;
                var rimC = new Vec3(Rp.Average(p => p.X), Rp.Average(p => p.Y), Rp.Average(p => p.Z));
                float reach = Rp.Max(p => Dist(p, rimC)) + 0.004f;
                var soup = new List<(Vec3 A, Vec3 B, Vec3 C)>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                    soup.Add(sign >= 0 ? (final[nodeOf[tris[t]]], final[nodeOf[tris[t + 1]]], final[nodeOf[tris[t + 2]]])
                                       : (final[nodeOf[tris[t]]], final[nodeOf[tris[t + 2]]], final[nodeOf[tris[t + 1]]]));   // facing out
                var edgeNormal = new Dictionary<((int, int, int), (int, int, int)), Vec3>();
                foreach (var (ka, kb, kc) in restTris)
                {
                    if (!points.ContainsKey(ka) || !points.ContainsKey(kb) || !points.ContainsKey(kc)) continue;
                    var (pa, pb, pc) = (Now(ka), Now(kb), Now(kc));
                    if (Dist(pa, rimC) > reach && Dist(pb, rimC) > reach && Dist(pc, rimC) > reach) continue;
                    soup.Add((pa, pb, pc));
                    var tn = TriNormalRaw(pa, pb, pc);
                    edgeNormal[EdgeKey(ka, kb)] = tn; edgeNormal[EdgeKey(kb, kc)] = tn; edgeNormal[EdgeKey(kc, ka)] = tn;
                }
                int np0 = tris.Length / 3;
                bool UnderPatch(Vec3 p, Vec3 up)
                {
                    var patchOnly = soup.GetRange(0, np0);
                    return FirstHit(patchOnly, p, up) is { } h && !h.Front && h.T <= NailSeamReach;
                }
                bool SeamTight(Vec3 a, Vec3 b)
                {
                    float Off(Vec3 p)
                    {
                        float best = float.MaxValue;
                        for (int j = 0; j < nb; j++)
                        {
                            var (s0, s1) = (final[bLoop[j]], final[bLoop[(j + 1) % nb]]);
                            var e = new Vec3(s1.X - s0.X, s1.Y - s0.Y, s1.Z - s0.Z);
                            float ee = e.X * e.X + e.Y * e.Y + e.Z * e.Z;
                            float t = ee <= 0 ? 0 : Math.Clamp(((p.X - s0.X) * e.X + (p.Y - s0.Y) * e.Y + (p.Z - s0.Z) * e.Z) / ee, 0f, 1f);
                            best = MathF.Min(best, Dist(p, new Vec3(s0.X + e.X * t, s0.Y + e.Y * t, s0.Z + e.Z * t)));
                        }
                        return best;
                    }
                    static float ToSeg(Vec3 p, Vec3 s0, Vec3 s1)
                    {
                        var e = new Vec3(s1.X - s0.X, s1.Y - s0.Y, s1.Z - s0.Z);
                        float ee = e.X * e.X + e.Y * e.Y + e.Z * e.Z;
                        float t = ee <= 0 ? 0 : Math.Clamp(((p.X - s0.X) * e.X + (p.Y - s0.Y) * e.Y + (p.Z - s0.Z) * e.Z) / ee, 0f, 1f);
                        return Dist(p, new Vec3(s0.X + e.X * t, s0.Y + e.Y * t, s0.Z + e.Z * t));
                    }
                    var m = new Vec3((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
                    float worst = MathF.Max(Off(a), MathF.Max(Off(b), Off(m)));
                    // ...and from the outline's side: a rim vertex always lies ON the outline (it is a target, or was moved
                    // onto the chain), so the crack — a crescent where the chain bends at an outline node between two rim
                    // vertices — is widest AT that node. Every outline node nearer this rim edge than any other is asked.
                    for (int j = 0; j < nb; j++)
                    {
                        var p = final[bLoop[j]];
                        float here = ToSeg(p, a, b);
                        if (here <= worst) continue;
                        bool nearest = true;
                        for (int k = 0; k < nr && nearest; k++)
                            nearest = ToSeg(p, Now(VKey(R[k].P)), Now(VKey(R[(k + 1) % nr].P))) >= here - 1e-9f;
                        if (nearest) worst = here;
                    }
                    return worst <= NailSeamTight;
                }
                for (int i = 0; i < nr; i++)
                {
                    var (ka, kb) = (VKey(R[i].P), VKey(R[(i + 1) % nr].P));
                    // No readable skin beside an edge cannot be shown closed: fail safe.
                    if (!edgeNormal.TryGetValue(EdgeKey(ka, kb), out var en)) return "the seam leaks";
                    float el = MathF.Sqrt(en.X * en.X + en.Y * en.Y + en.Z * en.Z);
                    if (el <= 0) return "the seam leaks";
                    var n = new Vec3(en.X / el, en.Y / el, en.Z / el);
                    var a = Now(ka); var b = Now(kb);
                    var mid = new Vec3((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
                    // Inward ALONG the skin (the step to the rim's middle with its part along the normal taken out): where the
                    // middle lies above the rim, a step straight at it would bring the ray down the seam itself.
                    var toC = new Vec3(rimC.X - mid.X, rimC.Y - mid.Y, rimC.Z - mid.Z);
                    float along = toC.X * n.X + toC.Y * n.Y + toC.Z * n.Z;
                    var tc = NormalizeOr(new Vec3(toC.X - n.X * along, toC.Y - n.Y * along, toC.Z - n.Z * along), default);
                    var o = new Vec3(mid.X + tc.X * 0.0003f + n.X * 0.001f, mid.Y + tc.Y * 0.0003f + n.Y * 0.001f,
                                     mid.Z + tc.Z * 0.0003f + n.Z * 0.001f);
                    var d = new Vec3(-n.X, -n.Y, -n.Z);
                    // Through a hole, the first face met is the inside of the finger's far wall — a back face.
                    // Bounded, as a garment would see it: a front face further off may be another part of the finger.
                    bool met = FirstHit(soup, o, d) is { } hit && hit.Front && hit.T <= NailSeamReach;
                    // ...or, where the patch rises steeply off the rim and stands over the ray's start, the same ray from 3 mm out.
                    // (Not "the start is enclosed": on an open mesh that test passed slivers the 3 mm cast shows open.)
                    var far = new Vec3(o.X + n.X * 0.002f, o.Y + n.Y * 0.002f, o.Z + n.Z * 0.002f);
                    bool under = FirstHit(soup, far, d) is { } back && back.Front && back.T <= NailSeamReach + 0.002f;
                    // ...or the ray starts UNDER the patch — where the patch leans out over a fingertip that curls up past the rim
                    // (YAB+, Rue+, LavaBod+), the ray from 1 mm out and the one from 3 mm out both start inside the finger and reach
                    // its far wall without passing any hole. Then the seam itself is asked: the rim edge must lie on the patch's
                    // outline. Shut there, nothing passes between them.
                    if (!met && !under && !(UnderPatch(o, n) && SeamTight(a, b))) return "the seam leaks";
                }
            }

            return null;
        }
        string? smoothWhy = smooth == null ? "no skin beside the rim" : Verify(smooth);
        if (smoothWhy == null) final = smooth!;
        else if (Verify(final) is { } why) return why;

        foreach (int v in verts)
        {
            int n = nodeOf[v];
            sock.Spanned[v] = final[n];
            if (bIndex.TryGetValue(n, out int j)) sock.RimNormal[v] = rimN[j];
        }
        foreach (var (k, to) in rimMoves)
            if (points.TryGetValue(k, out var q))
                foreach (var (m, v) in q.Copies) sock.RimMoves.Add((m, v, to));
        sock.MiddleOffset = middle;
        return null;
    }

    private static Vec3 TriNormalRaw(Vec3 a, Vec3 b, Vec3 c)
        => new((b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y),
               (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z),
               (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));

    /// <summary>The nearest triangle a ray meets (Möller–Trumbore), its distance and whether it meets the front.</summary>
    private static (float T, bool Front)? FirstHit(List<(Vec3 A, Vec3 B, Vec3 C)> soup, Vec3 o, Vec3 d)
    {
        float best = float.MaxValue; bool front = false;
        foreach (var (a, b, c) in soup)
        {
            var e1 = new Vec3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var e2 = new Vec3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            var pv = new Vec3(d.Y * e2.Z - d.Z * e2.Y, d.Z * e2.X - d.X * e2.Z, d.X * e2.Y - d.Y * e2.X);
            float det = e1.X * pv.X + e1.Y * pv.Y + e1.Z * pv.Z;
            if (MathF.Abs(det) < 1e-14f) continue;
            float inv = 1f / det;
            var tv = new Vec3(o.X - a.X, o.Y - a.Y, o.Z - a.Z);
            float u = (tv.X * pv.X + tv.Y * pv.Y + tv.Z * pv.Z) * inv;
            if (u < -1e-5f || u > 1 + 1e-5f) continue;
            var qv = new Vec3(tv.Y * e1.Z - tv.Z * e1.Y, tv.Z * e1.X - tv.X * e1.Z, tv.X * e1.Y - tv.Y * e1.X);
            float v = (d.X * qv.X + d.Y * qv.Y + d.Z * qv.Z) * inv;
            if (v < -1e-5f || u + v > 1 + 1e-5f) continue;
            float t = (e2.X * qv.X + e2.Y * qv.Y + e2.Z * qv.Z) * inv;
            if (t <= 1e-7f || t >= best) continue;
            best = t;
            var n = TriNormalRaw(a, b, c);
            front = n.X * d.X + n.Y * d.Y + n.Z * d.Z < 0;
        }
        return best < float.MaxValue ? (best, front) : null;
    }

    /// <summary>What <see cref="MatchLoopOnto"/> found: the rim turned counter-clockwise, whether the loop was turned
    /// round, each loop point's target on the rim and the finger's normal there, and rim vertices moved (by key).</summary>
    private sealed record LoopMatch(RimPoint[] R, bool Reversed, Vec3[] Target, Vec3[] RimN, Dictionary<(int, int, int), Vec3> RimMoves);

    /// <summary>
    /// Lay a loop (the spanning nail's outline) onto a rim in order: both turned the same way round in the rim's plane,
    /// matched by arc length from the best of 256 start offsets, each rim vertex claiming the loop point nearest in
    /// parameter (monotonic, once each). Every loop point gets a target on the rim and the finger's normal there; rim
    /// vertices no loop point claimed are moved onto the loop's targets. Turns <paramref name="bp"/> round IN PLACE when
    /// it runs the other way (<see cref="LoopMatch.Reversed"/>), so the caller can turn its own copy too. Returns why it
    /// could not, or null.
    /// </summary>

    private static string? MatchLoopOnto(RimPoint[] rim, List<Vec3> bp, Frame frame, out LoopMatch? match)
    {
        match = null; bool reversed = false;
        var rp = rim.Select(r => r.P).ToArray();
        static float SignedArea((float X, float Y)[] p) { float a = 0; for (int i = 0; i < p.Length; i++) { var (q, r) = (p[i], p[(i + 1) % p.Length]); a += q.X * r.Y - r.X * q.Y; } return a / 2; }
        var rimOrder = Enumerable.Range(0, rim.Length).ToList();
        if (SignedArea(rp.Select(frame.Flat).ToArray()) < 0) rimOrder.Reverse();
        if (SignedArea(bp.Select(frame.Flat).ToArray()) < 0) { bp.Reverse(); reversed = true; }
        var R = rimOrder.Select(i => rim[i]).ToArray();
        var Rp = R.Select(r => r.P).ToArray();

        static float[] Params(IReadOnlyList<Vec3> pts)
        {
            var s = new float[pts.Count];
            float total = 0;
            for (int i = 1; i < pts.Count; i++) { total += Dist(pts[i - 1], pts[i]); s[i] = total; }
            total += Dist(pts[^1], pts[0]);
            for (int i = 0; i < s.Length; i++) s[i] /= total;
            return s;
        }
        static Vec3 At(IReadOnlyList<Vec3> pts, float[] s, float t, out int seg, out float f)
        {
            t -= MathF.Floor(t);
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                float a = s[i], b = i + 1 < n ? s[i + 1] : 1f;
                if (t >= a && t <= b)
                {
                    f = b > a ? (t - a) / (b - a) : 0f;
                    seg = i;
                    var (p, q) = (pts[i], pts[(i + 1) % n]);
                    return new Vec3(p.X + (q.X - p.X) * f, p.Y + (q.Y - p.Y) * f, p.Z + (q.Z - p.Z) * f);
                }
            }
            seg = n - 1; f = 1f;
            return pts[0];
        }
        var rs = Params(Rp);
        var bs = Params(bp);

        // The start offset that lays the outline on the rim best.
        float bestO = 0, bestCost = float.MaxValue;
        for (int k = 0; k < 256; k++)
        {
            float o = k / 256f, cost = 0;
            for (int j = 0; j < 64; j++)
            {
                var a = At(Rp, rs, j / 64f, out _, out _);
                var b = At(bp, bs, j / 64f + o, out _, out _);
                float d = Dist(a, b); cost += d * d;
            }
            if (cost < bestCost) { bestCost = cost; bestO = o; }
        }

        // Each rim vertex claims the outline node nearest in parameter — in order, once each.
        static float Circ(float a, float b) { float d = MathF.Abs(a - b) % 1f; return MathF.Min(d, 1f - d); }
        int nb = bp.Count, nr = R.Length;
        var claimOf = new int[nr];   // rim index -> outline index, or -1
        int first = -1, last = -1;
        for (int i = 0; i < nr; i++)
        {
            float t = rs[i] + bestO;
            int j = 0;
            for (int q = 1; q < nb; q++) if (Circ(bs[q], t) < Circ(bs[j], t)) j = q;
            claimOf[i] = -1;
            if (first < 0) { first = j; last = 0; claimOf[i] = j; continue; }
            int d = (j - first + nb) % nb;
            if (d > last && d < nb) { claimOf[i] = j; last = d; }
        }
        var claimed = Enumerable.Range(0, nr).Where(i => claimOf[i] >= 0).ToList();
        if (claimed.Count < 3) return "the nail's outline does not follow the rim";

        // Targets for every outline node, and the finger's normal there.
        var target = new Vec3[nb];
        var rimN = new Vec3[nb];
        for (int c = 0; c < claimed.Count; c++)
        {
            int ia = claimed[c], ib = claimed[(c + 1) % claimed.Count];
            int ja = claimOf[ia], jb = claimOf[ib];
            target[ja] = R[ia].P; rimN[ja] = R[ia].N;
            float ra = rs[ia], rb = rs[ib]; if (rb <= ra) rb += 1f;
            float ba = bs[ja], bb = bs[jb]; if (bb <= ba) bb += 1f;
            for (int j = (ja + 1) % nb; j != jb; j = (j + 1) % nb)
            {
                float bj = bs[j]; if (bj < ba) bj += 1f;
                float f = bb > ba ? (bj - ba) / (bb - ba) : 0f;
                target[j] = At(Rp, rs, ra + f * (rb - ra), out int seg, out float sf);
                var (n0, n1) = (R[seg].N, R[(seg + 1) % nr].N);
                rimN[j] = NormalizeOr(new Vec3(n0.X + (n1.X - n0.X) * sf, n0.Y + (n1.Y - n0.Y) * sf, n0.Z + (n1.Z - n0.Z) * sf), frame.N);
            }
        }

        // Rim vertices no outline node claimed move onto the outline, at their share of the way between claims.
        var rimMoves = new Dictionary<(int, int, int), Vec3>();
        for (int c = 0; c < claimed.Count; c++)
        {
            int ia = claimed[c], ib = claimed[(c + 1) % claimed.Count];
            int ja = claimOf[ia], jb = claimOf[ib];
            var seg = new List<Vec3>();
            for (int j = ja; ; j = (j + 1) % nb) { seg.Add(target[j]); if (j == jb) break; }
            var segS = new float[seg.Count];
            float tot = 0; for (int q = 1; q < seg.Count; q++) { tot += Dist(seg[q - 1], seg[q]); segS[q] = tot; }
            float ra = rs[ia], rb = rs[ib]; if (rb <= ra) rb += 1f;
            for (int i = (ia + 1) % nr; i != ib; i = (i + 1) % nr)
            {
                float ri = rs[i]; if (ri < ra) ri += 1f;
                float want = (rb > ra ? (ri - ra) / (rb - ra) : 0f) * tot;
                Vec3 to = seg[^1];
                for (int q = 1; q < seg.Count; q++)
                    if (segS[q] >= want)
                    {
                        float f = segS[q] > segS[q - 1] ? (want - segS[q - 1]) / (segS[q] - segS[q - 1]) : 0f;
                        to = new Vec3(seg[q - 1].X + (seg[q].X - seg[q - 1].X) * f, seg[q - 1].Y + (seg[q].Y - seg[q - 1].Y) * f,
                                      seg[q - 1].Z + (seg[q].Z - seg[q - 1].Z) * f);
                        break;
                    }
                rimMoves[VKey(R[i].P)] = to;
            }
        }
        match = new LoopMatch(R, reversed, target, rimN, rimMoves);
        return null;
    }
}
