using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Grades what <c>BodyBridge.FlattenNails</c> did to a hand, from the geometry alone: it never asks the pass which
/// socket it found or which nail it chose. Sockets are found here the test's own way (closed loops of open skin edges
/// near a nail), a nail counts as spanning when it moved and still has area, and every check is a measurement —
/// rays, distances, orientations, bytes.
/// </summary>
internal sealed class NailGrade
{
    private const float Reach = 0.0015f, OwnReach = 0.0035f;

    private readonly byte[] hand;
    private readonly Vector3[] H, F;       // global skin positions: the hand, and the result being graded
    private readonly int[] T;
    private readonly (int, int, int)[] key;  // each global vertex's welded key, in the HAND
    private readonly List<Island> islands = [];
    private readonly List<int> restTris = [];
    private readonly List<Loop> loops = [];
    internal readonly List<(int A, int B)> OpenEdges = [];

    private sealed class Island
    {
        public required (int Mesh, int Island) Id;
        public required bool Sewn;
        public required HashSet<int> Verts;     // global
        public required List<int> Tris;         // global triangle starts
        public required HashSet<int> Boundary;  // global vertices on the island's outline
    }

    private sealed class Loop
    {
        public required List<int> Verts;        // global, one per welded key, in no particular order
        public required List<(int A, int B)> Edges;
        public required Vector3 Centroid, N;
        public required Dictionary<(int, int), Vector3> EdgeNormal;
        public required Dictionary<(int, int), Vector3> Mid;
    }

    internal NailGrade(byte[] hand, byte[] result) : this(hand, ReadPositions(result)) { }

    private NailGrade(byte[] hand, Vector3[] result)
    {
        this.hand = hand;
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(hand, out var p, out _, out var t));
        H = Enumerable.Range(0, p.Length / 3).Select(i => new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2])).ToArray();
        T = t;
        F = result;
        Assert.Equal(H.Length, F.Length);
        key = H.Select(Key).ToArray();

        var plans = SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach);   // the nails as FlattenNails takes them
        // Where each skin mesh's vertices start in the global arrays: the reader appends every kept mesh whole, in
        // order, so a mesh is kept exactly when its vertices are found at the running offset.
        var offsetOf = new Dictionary<int, int>();
        int run = 0;
        foreach (var (m, pos) in SecondSkinWriter.ReadCapMeshes(hand))
        {
            if (run + pos.Length > H.Length) continue;
            bool here = pos.Length > 0 && Conv(pos[0]) == H[run] && Conv(pos[^1]) == H[run + pos.Length - 1];
            if (!here) continue;
            offsetOf[m] = run;
            run += pos.Length;
        }
        var triAt = new Dictionary<(int, int, int), int>();
        for (int i = 0; i + 2 < T.Length; i += 3) triAt.TryAdd((T[i], T[i + 1], T[i + 2]), i);
        var isBed = new HashSet<int>();
        var bedEdges = new HashSet<((int, int, int), (int, int, int))>();
        foreach (var (m, plan) in plans)
            for (int k = 0; k < plan.VertsOf.Count; k++)
            {
                var isl = new Island { Id = (m, k), Sewn = k < plan.Sewn.Count && plan.Sewn[k], Verts = [], Tris = [], Boundary = [] };
                islands.Add(isl);
                if (!offsetOf.TryGetValue(m, out int off)) continue;
                var ts = plan.Islands[k];
                for (int q = 0; q + 2 < ts.Length; q += 3)
                {
                    if (!triAt.TryGetValue((ts[q] + off, ts[q + 1] + off, ts[q + 2] + off), out int i)) continue;
                    if (!isBed.Add(i)) continue;
                    isl.Tris.Add(i);
                    for (int e = 0; e < 3; e++) isl.Verts.Add(T[i + e]);
                }
            }
        for (int i = 0; i + 2 < T.Length; i += 3) if (!isBed.Contains(i)) restTris.Add(i);
        foreach (var isl in islands)
        {
            var use = new Dictionary<((int, int, int), (int, int, int)), int>();
            foreach (int i in isl.Tris)
                for (int e = 0; e < 3; e++) { var ek = EK(key[T[i + e]], key[T[i + (e + 1) % 3]]); use[ek] = use.GetValueOrDefault(ek) + 1; }
            var bk = use.Where(kv => kv.Value == 1).SelectMany(kv => new[] { kv.Key.Item1, kv.Key.Item2 }).ToHashSet();
            foreach (int v in isl.Verts) if (bk.Contains(key[v])) isl.Boundary.Add(v);
            // A sewn nail's outline is the rim it is sewn into: no hole there.
            if (isl.Sewn) foreach (var (ek, n) in use) if (n == 1) bedEdges.Add(ek);
        }

        // Open rest edges: used once among the rest skin's distinct triangles, and by no nail.
        var seen = new HashSet<((int, int, int), (int, int, int), (int, int, int))>();
        var useR = new Dictionary<((int, int, int), (int, int, int)), (int Count, int A, int B, int Tri)>();
        foreach (int i in restTris)
        {
            var s = new[] { key[T[i]], key[T[i + 1]], key[T[i + 2]] }; Array.Sort(s);
            if (!seen.Add((s[0], s[1], s[2]))) continue;
            for (int e = 0; e < 3; e++)
            {
                int a = T[i + e], b = T[i + (e + 1) % 3];
                var ek = EK(key[a], key[b]);
                useR[ek] = useR.TryGetValue(ek, out var u) ? (u.Count + 1, u.A, u.B, u.Tri) : (1, a, b, i);
            }
        }
        var edgeTri = new Dictionary<(int, int), int>();
        foreach (var (ek, u) in useR)
            if (u.Count == 1 && !bedEdges.Contains(ek)) { OpenEdges.Add((u.A, u.B)); edgeTri[(u.A, u.B)] = u.Tri; }

        // Loops: components of open edges near a nail.
        var byKey = new Dictionary<(int, int, int), List<int>>();
        foreach (var (a, b) in OpenEdges)
        {
            (byKey.TryGetValue(key[a], out var la) ? la : byKey[key[a]] = []).Add(b);
            (byKey.TryGetValue(key[b], out var lb) ? lb : byKey[key[b]] = []).Add(a);
        }
        var rep = new Dictionary<(int, int, int), int>();
        foreach (var (a, b) in OpenEdges) { rep.TryAdd(key[a], a); rep.TryAdd(key[b], b); }
        var done = new HashSet<(int, int, int)>();
        foreach (var k0 in byKey.Keys)
        {
            if (!done.Add(k0)) continue;
            var comp = new List<(int, int, int)> { k0 };
            for (int q = 0; q < comp.Count; q++)
                foreach (int nb in byKey[comp[q]]) if (done.Add(key[nb])) comp.Add(key[nb]);
            var verts = comp.Select(k => rep[k]).ToList();
            bool near = islands.Any(isl => !isl.Sewn && verts.Any(v => isl.Verts.Any(w => Vector3.Distance(H[v], H[w]) <= OwnReach)));
            if (!near) continue;
            var set = comp.ToHashSet();
            var edges = OpenEdges.Where(e => set.Contains(key[e.A])).ToList();
            var en = new Dictionary<(int, int), Vector3>();
            var sum = Vector3.Zero;
            foreach (var e in edges) { var n = TriN(H, edgeTri[e]); en[e] = n; sum += n; }
            var c = verts.Aggregate(Vector3.Zero, (s, v) => s + H[v]) / verts.Count;
            loops.Add(new Loop { Verts = verts, Edges = edges, Centroid = c, N = Vector3.Normalize(sum), EdgeNormal = en,
                                 Mid = edges.ToDictionary(e => e, e => (H[e.A] + H[e.B]) / 2) });
        }

        Spanned = islands.Where(IsSpanned).Select(i => i.Id).ToList();
    }

    private static Vector3[] ReadPositions(byte[] m)
    {
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(m, out var p, out _, out _));
        return Enumerable.Range(0, p.Length / 3).Select(i => new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2])).ToArray();
    }

    private static Vector3 Conv(SecondSkinWriter.Vec3 v) => new(v.X, v.Y, v.Z);
    private static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));
    private static ((int, int, int), (int, int, int)) EK((int, int, int) a, (int, int, int) b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    private Vector3 TriN(Vector3[] P, int i) => Vector3.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]);

    /// <summary>Nails that moved and still have area: the ones spanning a socket (sewn nails are laid down instead).</summary>
    internal readonly List<(int Mesh, int Island)> Spanned;

    private bool IsSpanned(Island isl)
    {
        if (isl.Sewn) return false;
        bool moved = isl.Verts.Any(v => Vector3.Distance(H[v], F[v]) > 1e-6f);
        float area = isl.Tris.Sum(i => TriN(F, i).Length() / 2);
        return moved && area > 1e-9f;
    }

    private Island Of((int Mesh, int Island) id) => islands.Single(i => i.Id == id);

    private static float Nearest(Vector3 p, IEnumerable<int> verts, Vector3[] P)
    { float best = float.MaxValue; foreach (int v in verts) best = MathF.Min(best, Vector3.Distance(p, P[v])); return best; }

    /// <summary>Loops a spanning nail sits in.</summary>
    private List<Loop> Owned() => loops.Where(l => Spanned.Any(id =>
        l.Edges.Any(e => Nearest((H[e.A] + H[e.B]) / 2, Of(id).Verts, H) <= Reach))).ToList();

    // ------------------------------------------------------------------ rays

    private static (float T, bool Front)? FirstHit(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> soup, Vector3 o, Vector3 d)
    {
        float best = float.MaxValue; bool front = false;
        foreach (var (a, b, c) in soup)
        {
            var e1 = b - a; var e2 = c - a;
            var pv = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-14f) continue;
            float inv = 1f / det;
            var tv = o - a;
            float u = Vector3.Dot(tv, pv) * inv;
            if (u < -1e-5f || u > 1 + 1e-5f) continue;
            var qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(d, qv) * inv;
            if (v < -1e-5f || u + v > 1 + 1e-5f) continue;
            float t = Vector3.Dot(e2, qv) * inv;
            if (t <= 1e-7f || t >= best) continue;
            best = t;
            front = Vector3.Dot(Vector3.Cross(e1, e2), d) < 0;
        }
        return best < float.MaxValue ? (best, front) : null;
    }

    private List<(Vector3 A, Vector3 B, Vector3 C)> Soup(Vector3[] P, IEnumerable<int> tris)
        => tris.Select(i => (P[T[i]], P[T[i + 1]], P[T[i + 2]])).Where(x => Vector3.Cross(x.Item2 - x.Item1, x.Item3 - x.Item1).Length() > 1e-14f).ToList();

    private static List<(Vector3, Vector3, Vector3)> SoupOf(byte[] model, bool skinOnly)
    {
        SecondSkinWriter.TryReadLod0Geometry(model, out var p, out _, out var t, out _, out _, skinOnly: skinOnly);
        Vector3 P(int i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
        var s = new List<(Vector3, Vector3, Vector3)>();
        for (int i = 0; i + 2 < t.Length; i += 3) s.Add((P(t[i]), P(t[i + 1]), P(t[i + 2])));
        return s;
    }

    private record Ray(Vector3 O, Vector3 D, string Kind, int Loop, (int, int)? Island, float Far = 0);

    private List<(Vector3, Vector3, Vector3)>? restSoup;
    private List<(Vector3, Vector3, Vector3)> RestSoup => restSoup ??= Soup(H, restTris);

    private Vector3 VertexNormal(Island isl, int v, Vector3[] P)
    {
        var s = Vector3.Zero;
        foreach (int i in isl.Tris) if (T[i] == v || T[i + 1] == v || T[i + 2] == v) s += TriN(P, i);
        return s.LengthSquared() > 0 ? Vector3.Normalize(s) : Vector3.UnitZ;
    }

    /// <summary>Rays through every nail and through the gap at every loop's rim, kept only where they reach the
    /// finger's far side through the hand's rest skin — rays that went through a hole.</summary>
    private List<Ray> SocketRays()
    {
        var rays = new List<Ray>();
        foreach (var isl in islands.Where(i => !i.Sewn))
        {
            foreach (int i in isl.Tris)
            {
                var n = TriN(H, i); if (n.LengthSquared() < 1e-20f) continue; n = Vector3.Normalize(n);
                var c = (H[T[i]] + H[T[i + 1]] + H[T[i + 2]]) / 3;
                rays.Add(new Ray(c + n * 0.001f, -n, "nail", -1, isl.Id));
            }
            foreach (int v in isl.Verts.Where(v => !isl.Boundary.Contains(v)))
            {
                var n = VertexNormal(isl, v, H);
                rays.Add(new Ray(H[v] + n * 0.001f, -n, "nail", -1, isl.Id));
            }
        }
        for (int li = 0; li < loops.Count; li++)
            foreach (var e in loops[li].Edges)
            {
                var mid = (H[e.A] + H[e.B]) / 2;
                var n = loops[li].EdgeNormal[e]; if (n.LengthSquared() < 1e-20f) continue; n = Vector3.Normalize(n);
                // A step inward ALONG the skin — toward the loop's middle with its part along the normal taken out.
                var toC = loops[li].Centroid - mid; toC -= n * Vector3.Dot(toC, n);
                if (toC.LengthSquared() > 0) toC = Vector3.Normalize(toC);
                rays.Add(new Ray(mid + toC * 0.0003f + n * 0.001f, -n, "gap", li, null));
            }
        // Through a hole means INSIDE the finger after it: the first skin met is the far wall, from behind.
        return rays.Where(r => OutsideSkin(r.O))
                   .Select(r => FirstHit(RestSoup, r.O, r.D) is { } h && !h.Front && h.T > 0.004f && h.T < 0.030f ? r with { Far = h.T } : null)
                   .Where(r => r != null).Select(r => r!).ToList();
    }


    /// <summary>Does a point lie outside the finger — on the outward side of the nearest rest skin? A ray starting
    /// inside (off a nail edge that curls down into its socket) reaches the far wall from behind without going through
    /// any hole, and is no socket ray.</summary>
    private bool OutsideSkin(Vector3 p)
    {
        float best = float.MaxValue, signed = 0;
        foreach (int i in restTris)
        {
            var (a, b, c) = (H[T[i]], H[T[i + 1]], H[T[i + 2]]);
            if (Vector3.DistanceSquared(p, (a + b + c) / 3) > 0.0001f) continue;
            var q = Closest(p, a, b, c);
            float d = Vector3.DistanceSquared(p, q);
            if (d >= best) continue;
            best = d;
            var nn = Vector3.Cross(b - a, c - a);
            signed = nn.LengthSquared() > 0 ? Vector3.Dot(p - q, Vector3.Normalize(nn)) : 0;
        }
        return best == float.MaxValue || signed >= 0;
    }
    private static bool Closed(List<(Vector3, Vector3, Vector3)> soup, Ray r)
        => FirstHit(soup, r.O, r.D) is { } h && h.Front && h.T < r.Far - 0.001f || Covered(soup, r);

    /// <summary>
    /// A rim "gap" ray of a spanned socket that starts UNDER the spanning patch — looking back out, the first face of the
    /// patch met is its back — is inside the finger already, where the patch leans out over a fingertip curling up past
    /// the rim: reaching the far wall says nothing of a hole. Asked of gap rays only and of the patch's own faces only,
    /// and sound only because the seam is measured shut both ways on its own (<see cref="SeamGap"/>): on an open mesh,
    /// "the start is enclosed" alone passed slivers.
    /// </summary>
    private static bool UnderPatch(List<(Vector3, Vector3, Vector3)> patch, Ray r)
        => r.Kind == "gap" && FirstHit(patch, r.O, -r.D) is { } b && !b.Front && b.T < 0.004f;

    /// <summary>Where a patch copying a nail's steep side leans out over the rim, a ray can start under it; then the same
    /// ray from 2 mm further out must meet a front face first, short of the far wall.</summary>
    private static bool Covered(List<(Vector3, Vector3, Vector3)> soup, Ray r)
        => FirstHit(soup, r.O - r.D * 0.002f, r.D) is { } b && b.Front && b.T < r.Far + 0.001f;

    /// <summary>The socket rays a spanning nail answers for: those through its own loop's gap and its socket's nails.</summary>
    private List<Ray> OwnedRays()
    {
        var owned = Owned();
        var idx = owned.Select(l => loops.IndexOf(l)).ToHashSet();
        var members = islands.Where(isl => owned.Any(l => l.Edges.Any(e => Nearest((H[e.A] + H[e.B]) / 2, isl.Verts, H) <= Reach)))
                             .Select(i => i.Id).ToHashSet();
        return SocketRays().Where(r => (r.Kind == "gap" && idx.Contains(r.Loop)) || (r.Island is { } id && members.Contains(id))).ToList();
    }

    internal int SocketRayCount => OwnedRays().Count;

    // ------------------------------------------------------------------ checks

    /// <summary>Everything wrong with the result, as text — the sweep's verdict and the CI tests' too.</summary>
    internal List<string> Problems(ITestOutputHelper? o)
    {
        var bad = new List<string>();
        var model = Soup(F, Enumerable.Range(0, T.Length / 3).Select(i => i * 3));
        // Spanned sockets are closed.
        var owned = OwnedRays();
        var patch = Soup(F, Spanned.SelectMany(id => Of(id).Tris));
        int open = owned.Count(r => !Closed(model, r) && !UnderPatch(patch, r));
        if (open > 0) bad.Add($"{open} of {owned.Count} socket rays through a spanned socket still open");
        // A collapsed nail never sits over a hole (a missed socket).
        var collapsed = islands.Where(i => !i.Sewn && i.Tris.Sum(t => TriN(F, t).Length()) < 1e-12f).Select(i => i.Id).ToHashSet();
        var missed = SocketRays().Where(r => r.Island is { } id && collapsed.Contains(id) && !Closed(model, r)).ToList();
        if (missed.Count > 0) bad.Add($"{missed.Count} socket rays open through collapsed nails {string.Join(",", missed.Select(r => r.Island).Distinct())}");
        float seam = SeamGap();
        if (seam > 0.00005f) bad.Add($"seam gap {seam * 1000:F3}mm");
        int flips = Flips();
        if (flips > 0) bad.Add($"{flips} folded triangles");
        var (aboveLo, _) = DepthUnderAuthored(o);
        float height = -aboveLo;
        // An inset nail comes up to its rim: it may rise by as much as its outline sat under the rim, and no more.
        float inset = Spanned.Select(id => Offsets(Of(id)).Select(x => x.S).DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();
        if (height > MathF.Max(0, inset) + 0.00015f)
            bad.Add($"a spanning nail stands {height * 1000:F3}mm above the authored nail (its outline was inset {inset * 1000:F3}mm)");
        if (!FloorHolds(o)) bad.Add("below the rim-plane floor");
        int onTop = OnTopOfRestSkin();
        if (onTop > 0) bad.Add($"{onTop} membrane triangles lie on rest skin");
        var coll = Collateral();
        if (coll != null) bad.Add(coll);
        o?.WriteLine($"  grade: {Spanned.Count} spanning, {owned.Count} socket rays ({open} open), seam {seam * 1000:F3}mm, "
                   + $"{flips} folds, height {height * 1000:F3}mm, {onTop} on rest skin");
        return bad;
    }

    internal void AssertSpanned(ITestOutputHelper o, bool band)
    {
        Assert.NotEmpty(Spanned);
        Assert.True(SocketRayCount > 0, "no ray goes through a hole in the hand — this measures nothing");
        var bad = Problems(o);
        if (band && !BandHolds(o)) bad.Add("off the authored-depth band");
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }

    internal void AssertNoSocketRayOpen(ITestOutputHelper o)
    {
        var model = Soup(F, Enumerable.Range(0, T.Length / 3).Select(i => i * 3));
        var rays = SocketRays();
        int open = rays.Count(r => !Closed(model, r));
        o.WriteLine($"{rays.Count} socket rays, {open} open");
        Assert.Equal(0, open);
    }

    internal void AssertClosedAgainst(byte[] other, bool skinOnly, ITestOutputHelper o)
    {
        var soup = SoupOf(other, skinOnly);
        // A garment stands off the body — up to a millimetre for a thick layer — so its rays start further out.
        var rays = OwnedRays().Select(r => r with { O = r.O - r.D * 0.002f }).ToList();
        Assert.True(rays.Count > 0, "no socket rays to cast");
        // Started 2 mm further out, the garment's far side is about 1 mm nearer than the body's would be: short of that.
        int open = rays.Count(r => !(FirstHit(soup, r.O, r.D) is { } h && h.Front && h.T < r.Far || Covered(soup, r)));
        o.WriteLine($"against the shell: {rays.Count} socket rays, {open} open");
        Assert.Equal(0, open);
    }


    /// <summary>
    /// The nail meshes by their nail's fate: over a spanning nail, gone (no area left); over a nail left as authored
    /// (unsewn, unmoved, beside a socket), exactly as it was. Returns what is still drawn over spanning nails and how
    /// many nail-mesh triangles over authored nails moved.
    /// </summary>
    internal (float DrawnOverSpanned, int MovedOverAuthored, int OverSpanned, int OverAuthored) NailMeshFates(byte[] result)
    {
        SecondSkinWriter.TryReadLod0Geometry(hand, out var hp, out _, out var ht, out _, out _, skinOnly: false, nonSkin: true);
        SecondSkinWriter.TryReadLod0Geometry(result, out var fp, out _, out var ft, out _, out _, skinOnly: false, nonSkin: true);
        Assert.Equal(ht, ft);
        Vector3 P(float[] p, int i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
        var spanned = Spanned.SelectMany(id => Of(id).Verts).Select(v => H[v]).ToList();
        var authored = islands.Where(i => !i.Sewn && !Spanned.Contains(i.Id) && i.Verts.All(v => H[v] == F[v])
                                          && loops.Any(l => l.Verts.Any(w => Nearest(H[w], i.Verts, H) <= OwnReach)))
                              .SelectMany(i => i.Verts).Select(v => H[v]).ToList();
        float drawn = 0; int moved = 0, overS = 0, overA = 0;
        for (int i = 0; i + 2 < ht.Length; i += 3)
        {
            var c = (P(hp, ht[i]) + P(hp, ht[i + 1]) + P(hp, ht[i + 2])) / 3;
            bool nearS = spanned.Any(q => Vector3.Distance(q, c) < 0.001f);
            bool nearA = !nearS && authored.Any(q => Vector3.Distance(q, c) < 0.001f);
            if (nearS)
            {
                overS++;
                drawn += Vector3.Cross(P(fp, ft[i + 1]) - P(fp, ft[i]), P(fp, ft[i + 2]) - P(fp, ft[i])).Length() / 2;
            }
            else if (nearA)
            {
                overA++;
                if (Enumerable.Range(0, 3).Any(e => P(hp, ht[i + e]) != P(fp, ft[i + e]))) moved++;
            }
        }
        return (drawn, moved, overS, overA);
    }
    internal void AssertCollateral() { var c = Collateral(); Assert.True(c == null, c); }

    internal void AssertNoMembraneOnRestSkin() => Assert.Equal(0, OnTopOfRestSkin());

    /// <summary>Furthest a spanned loop's rim vertex sits from the spanning nails' outline.</summary>
    private float SeamGap()
    {
        var edges = new List<(Vector3, Vector3)>();
        foreach (var id in Spanned)
        {
            var isl = Of(id);
            foreach (int i in isl.Tris)
                for (int e = 0; e < 3; e++)
                {
                    int a = T[i + e], b = T[i + (e + 1) % 3];
                    if (isl.Boundary.Contains(a) && isl.Boundary.Contains(b)) edges.Add((F[a], F[b]));
                }
        }
        static float ToChain(Vector3 p, List<(Vector3, Vector3)> chain)
        {
            float best = float.MaxValue;
            foreach (var (a, b) in chain)
            {
                var ab = b - a; float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
                best = MathF.Min(best, Vector3.Distance(p, a + ab * t));
            }
            return best;
        }
        float worst = 0;
        var owned = Owned();
        // Rim to outline: every rim vertex lies on the patch's outline...
        foreach (var l in owned)
            foreach (int v in l.Verts) worst = MathF.Max(worst, ToChain(F[v], edges));
        // ...and outline to rim: every outline vertex lies on the rim. A rim vertex always sits ON the outline (it is a
        // target, or moved onto the chain), so the crack between the two — a crescent where the chain bends at an
        // outline node — shows only from the outline's side. A pinhole's corners, welded to one point inside the patch,
        // are no part of the outline.
        // Only a node off the rim TOWARD the hole opens a crack: one lying out over the skin beside the rim is the patch
        // overlapping the skin, which nothing sees through. "Toward the hole" is the step inward along the skin, as the
        // gap rays take it.
        var rimEdges = owned.SelectMany(l => l.Edges.Select(e =>
        {
            var n = l.EdgeNormal[e];
            var mid = (F[e.A] + F[e.B]) / 2;
            var toC = l.Centroid - mid;
            if (n.LengthSquared() > 1e-20f) { n = Vector3.Normalize(n); toC -= n * Vector3.Dot(toC, n); }
            return (A: F[e.A], B: F[e.B], In: toC.LengthSquared() > 0 ? Vector3.Normalize(toC) : Vector3.Zero);
        })).ToList();
        foreach (var id in Spanned)
        {
            var isl = Of(id);
            foreach (int v in isl.Boundary)
            {
                bool welded = isl.Boundary.Any(u => u != v && Vector3.Distance(F[u], F[v]) < 1e-7f && Vector3.Distance(H[u], H[v]) > 1e-6f);
                if (welded) continue;
                var p = F[v];
                float best = float.MaxValue; Vector3 off = default, inward = default;
                foreach (var (a, b, inDir) in rimEdges)
                {
                    var ab = b - a; float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
                    var d = p - (a + ab * t);
                    if (d.Length() < best) { best = d.Length(); off = d; inward = inDir; }
                }
                if (best < float.MaxValue && Vector3.Dot(off, inward) > 0) worst = MathF.Max(worst, best);
            }
        }
        return worst;
    }

    private Loop NearestLoop(Island isl) => loops.OrderBy(l => l.Verts.Min(v => Nearest(H[v], isl.Verts, H))).First();

    /// <summary>The finger's outward direction over a socket: its rim edges' own normals, weighted by nearness. A rim that
    /// wraps the finger has a patch legitimately running steep down its sides, so one normal for the socket will not do.</summary>
    private static Vector3 FingerNormalAt(Loop l, Vector3 p)
    {
        var s = Vector3.Zero;
        foreach (var (e, n) in l.EdgeNormal)
        {
            if (n.LengthSquared() <= 0) continue;
            s += Vector3.Normalize(n) / MathF.Max(Vector3.DistanceSquared(p, l.Mid[e]), 1e-12f);
        }
        return s.LengthSquared() > 0 ? Vector3.Normalize(s) : l.N;
    }

    private int Flips()
    {
        int n = 0;
        foreach (var id in Spanned)
        {
            var isl = Of(id);
            var N = NearestLoop(isl).N;
            float wound = isl.Tris.Sum(i => Vector3.Dot(TriN(H, i), N));
            float sign = wound >= 0 ? 1 : -1;
            foreach (int i in isl.Tris)
            {
                var tn = TriN(F, i);
                bool onOutline = isl.Boundary.Contains(T[i]) && isl.Boundary.Contains(T[i + 1]) && isl.Boundary.Contains(T[i + 2]);
                // Two corners welded onto one point (a pinhole in the nail shut) draw nothing and turn nothing over.
                bool welded = Vector3.Distance(F[T[i]], F[T[i + 1]]) < 1e-7f || Vector3.Distance(F[T[i + 1]], F[T[i + 2]]) < 1e-7f
                              || Vector3.Distance(F[T[i + 2]], F[T[i]]) < 1e-7f;
                if (tn.Length() / 2 < 1e-12f) { if (!onOutline && !welded) n++; continue; }
                bool intoFinger = sign * Vector3.Dot(tn, FingerNormalAt(NearestLoop(isl), (F[T[i]] + F[T[i + 1]] + F[T[i + 2]]) / 3)) <= 0;
                bool turned = Vector3.Dot(tn, TriN(H, i)) <= 0;
                if (intoFinger && turned) n++;
            }
        }
        foreach (int i in restTris)
        {
            bool moved = Enumerable.Range(0, 3).Any(e => Vector3.Distance(H[T[i + e]], F[T[i + e]]) > 1e-7f);
            if (moved && (Vector3.Dot(TriN(H, i), TriN(F, i)) <= 0 || TriN(F, i).Length() / 2 < 1e-12f)) n++;
        }
        return n;
    }


    private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var q = MeshMath.ClosestOnTriangle(new(p.X, p.Y, p.Z), new(a.X, a.Y, a.Z), new(b.X, b.Y, b.Z), new(c.X, c.Y, c.Z));
        return new Vector3(q.X, q.Y, q.Z);
    }

    /// <summary>The loop's own plane: centre, outward normal, and the lowest rim vertex's height along it.</summary>
    private (Vector3 C, Vector3 N, float HMin) Plane(Loop l)
    {
        var c = l.Centroid;
        double[,] cov = new double[3, 3];
        foreach (int v in l.Verts)
        {
            var d = H[v] - c; double[] a = { d.X, d.Y, d.Z };
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) cov[i, j] += a[i] * a[j];
        }
        double tr = cov[0, 0] + cov[1, 1] + cov[2, 2];
        double[] n = { l.N.X, l.N.Y, l.N.Z };
        for (int it = 0; it < 200; it++)
        {
            var r = new double[3];
            for (int i = 0; i < 3; i++) { r[i] = tr * n[i]; for (int j = 0; j < 3; j++) r[i] -= cov[i, j] * n[j]; }
            double len = Math.Sqrt(r[0] * r[0] + r[1] * r[1] + r[2] * r[2]);
            for (int i = 0; i < 3; i++) n[i] = r[i] / len;
        }
        var N = new Vector3((float)n[0], (float)n[1], (float)n[2]);
        if (Vector3.Dot(N, l.N) < 0) N = -N;
        float hMin = l.Verts.Min(v => Vector3.Dot(H[v] - c, N));
        return (c, N, hMin);
    }

    internal float LowestRimBelowPlane() => loops.Count == 0 ? 0 : loops.Min(l => Plane(l).HMin);

    /// <summary>No spanning nail's inside vertex sinks more than 0.5 mm under its loop's lowest rim point.</summary>
    internal bool FloorHolds(ITestOutputHelper? o)
    {
        bool ok = true;
        foreach (var l in Owned())
        {
            var (c, N, hMin) = Plane(l);
            float worst = float.MaxValue;
            foreach (var id in Spanned)
            {
                var isl = Of(id);
                if (l.Verts.Min(v => Nearest(H[v], isl.Verts, H)) > OwnReach) continue;
                foreach (int v in isl.Verts.Where(v => !isl.Boundary.Contains(v)))
                    worst = MathF.Min(worst, Vector3.Dot(F[v] - c, N) - (hMin - 0.0005f));
            }
            o?.WriteLine($"  floor: rim lowest {hMin * 1000:F2}mm under its plane, margin {worst * 1000:F2}mm");
            if (worst < 0) ok = false;
        }
        return ok;
    }

    /// <summary>Each outline node's signed offset to the rim along the authored nail's normal: negative where the nail
    /// stood proud of the rim, positive where it was inset.</summary>
    internal List<float> OffsetsOf((int Mesh, int Island) id) => Offsets(Of(id)).Select(x => x.S).ToList();

    private List<(int V, float S)> Offsets(Island isl)
    {
        var l = NearestLoop(isl);
        var outp = new List<(int, float)>();
        foreach (int b in isl.Boundary)
        {
            var n = VertexNormal(isl, b, H);
            if (Vector3.Dot(n, l.N) < 0) n = -n;
            Vector3 best = H[b]; float bd = float.MaxValue;
            foreach (var (ea, eb) in l.Edges)
            {
                var a = H[ea]; var ab = H[eb] - a;
                float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(H[b] - a, ab) / ab.LengthSquared(), 0, 1) : 0;
                var q = a + ab * t; float d = Vector3.Distance(q, H[b]);
                if (d < bd) { bd = d; best = q; }
            }
            outp.Add((b, Vector3.Dot(best - H[b], n)));
        }
        return outp;
    }

    /// <summary>
    /// Where each spanning nail sits against the AUTHORED nail, along the authored nail's own rays from inside it:
    /// the least and most depth below it (negative = above). A spanning nail keeps the authored shape, with the
    /// nail's thickness taken off where its outline shows it — so never above it, and never far under it.
    /// </summary>
    private (float Lo, float Hi) DepthUnderAuthored(ITestOutputHelper? o)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var id in Spanned)
        {
            var isl = Of(id);
            float ilo = float.MaxValue, ihi = float.MinValue;
            var model = Soup(F, isl.Tris);   // against this nail's own surface: a neighbour or a plug is not it
            foreach (int v in isl.Verts.Where(v => !isl.Boundary.Contains(v)))
            {
                var n = VertexNormal(isl, v, H);
                if (FirstHit(model, H[v] + n * 0.001f, -n) is not { } h) continue;
                float d = h.T - 0.001f;
                ilo = MathF.Min(ilo, d); ihi = MathF.Max(ihi, d);
            }
            if (ilo == float.MaxValue) continue;
            o?.WriteLine($"  depth under the authored nail {id}: {ilo * 1000:F2}..{ihi * 1000:F2}mm");
            lo = MathF.Min(lo, ilo); hi = MathF.Max(hi, ihi);
        }
        return lo == float.MaxValue ? (0, 0) : (lo, hi);
    }

    /// <summary>The band: never more than 0.15 mm above the authored nail, never more than a millimetre under it.</summary>
    internal bool BandHolds(ITestOutputHelper? o)
    {
        var (lo, hi) = DepthUnderAuthored(o);
        float inset = Spanned.Select(id => Offsets(Of(id)).Select(x => x.S).DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();
        return lo >= -(MathF.Max(0, inset) + 0.00015f) && hi <= 0.001f;
    }

    /// <summary>Spanning nails pushed in along their normals by <paramref name="depth"/>: what a dent looks like.</summary>
    internal NailGrade Dent(float depth)
    {
        var p = (Vector3[])F.Clone();
        foreach (var id in Spanned)
        {
            var isl = Of(id);
            foreach (int v in isl.Verts.Where(v => !isl.Boundary.Contains(v))) p[v] = F[v] - VertexNormal(isl, v, F) * depth;
        }
        return new NailGrade(hand, p);
    }

    /// <summary>The membrane as it should be at best, with nothing from the pass: for each loop the nearest nail with an
    /// outline, left exactly where the author put it (nudged a hair so it counts as moved). The grading must pass it.</summary>
    internal NailGrade IdealMembrane(ITestOutputHelper o)
    {
        var p = (Vector3[])H.Clone();
        foreach (var l in loops)
        {
            var cands = islands.Where(i => !i.Sewn && i.Boundary.Count >= 3).ToList();
            if (cands.Count == 0) continue;
            var owner = cands.OrderBy(i => l.Verts.Max(v => Nearest(H[v], i.Verts, H))).First();
            if (l.Verts.Max(v => Nearest(H[v], owner.Verts, H)) > OwnReach) continue;
            foreach (int v in owner.Verts.Where(v => !owner.Boundary.Contains(v))) p[v] = H[v] + VertexNormal(owner, v, H) * 0.00001f;
        }
        o.WriteLine($"ideal membrane over {loops.Count} loops");
        return new NailGrade(hand, p);
    }

    private int OnTopOfRestSkin()
    {
        var loopKeys = loops.SelectMany(l => l.Verts).Select(v => key[v]).ToHashSet();
        var soup = Soup(H, restTris.Where(i => !Enumerable.Range(0, 3).Any(e => loopKeys.Contains(key[T[i + e]]))));
        int n = 0;
        foreach (var id in Spanned)
            foreach (int i in Of(id).Tris)
            {
                var tn = TriN(F, i); if (tn.LengthSquared() < 1e-24f) continue; tn = Vector3.Normalize(tn);
                var c = (F[T[i]] + F[T[i + 1]] + F[T[i + 2]]) / 3;
                bool hit = (FirstHit(soup, c, tn) is { } a && a.T < 0.0002f) || (FirstHit(soup, c, -tn) is { } b && b.T < 0.0002f);
                if (hit) n++;
            }
        return n;
    }

    /// <summary>Only rim vertices of the finger move, each no further than one rim edge (onto the chord of a coarse
    /// spanning nail).</summary>
    private string? Collateral()
    {
        var rimKeys = new Dictionary<(int, int, int), float>();
        foreach (var l in loops)
            foreach (var (a, b) in l.Edges)
            {
                float len = Vector3.Distance(H[a], H[b]);
                rimKeys[key[a]] = MathF.Max(rimKeys.GetValueOrDefault(key[a]), len);
                rimKeys[key[b]] = MathF.Max(rimKeys.GetValueOrDefault(key[b]), len);
            }
        var bedVerts = islands.SelectMany(i => i.Verts).ToHashSet();
        foreach (int i in restTris)
            for (int e = 0; e < 3; e++)
            {
                int v = T[i + e];
                if (bedVerts.Contains(v)) continue;
                float d = Vector3.Distance(H[v], F[v]);
                if (d <= 1e-7f) continue;
                if (!rimKeys.TryGetValue(key[v], out float edge)) return $"finger vertex {v} off the rim moved {d * 1000:F3}mm";
                if (d > edge + 1e-6f) return $"rim vertex {v} moved {d * 1000:F3}mm, more than a rim edge";
            }
        return null;
    }

    internal float IslandArea(byte[] model, (int Mesh, int Island) id)
    {
        var P = ReadPositions(model);
        return Of(id).Tris.Sum(i => Vector3.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]).Length() / 2);
    }

    internal int OpenEdgesInReach(int mesh, int k)
    {
        var isl = islands.First(i => i.Id == (mesh, k));
        return OpenEdges.Count(e => Nearest((H[e.A] + H[e.B]) / 2, isl.Verts, H) <= Reach);
    }

    internal (int InReach, float NearShare) TipCounts(int mesh, int k)
    {
        var isl = Of((mesh, k));
        var l = NearestLoop(isl);
        float share = l.Verts.Count(v => Nearest(H[v], isl.Verts, H) <= OwnReach) / (float)l.Verts.Count;
        return (OpenEdgesInReach(mesh, k), share);
    }

    /// <summary>Median distance from the shell to the membrane's inside vertices, and to the finger near the loops.</summary>
    internal (float OverMembrane, float OverFinger) ShellOffsets(byte[] shell)
    {
        var soup = SoupOf(shell, skinOnly: false);
        float Dist(Vector3 p) => soup.Min(t => Vector3.Distance(p, Closest(p, t.Item1, t.Item2, t.Item3)));
        var mem = Spanned.SelectMany(id => Of(id).Verts.Where(v => !Of(id).Boundary.Contains(v))).Select(v => Dist(F[v])).OrderBy(x => x).ToList();
        var bed = islands.SelectMany(i => i.Verts).ToHashSet();
        var near = restTris.SelectMany(i => new[] { T[i], T[i + 1], T[i + 2] }).Distinct()
            .Where(v => !bed.Contains(v) && Owned().Any(l => l.Verts.Min(w => Vector3.Distance(H[v], H[w])) <= 0.003f
                                                         && l.Verts.Min(w => Vector3.Distance(H[v], H[w])) > 0.0005f))
            .Select(v => Dist(F[v])).OrderBy(x => x).ToList();
        return (mem[mem.Count / 2], near[near.Count / 2]);
    }

    /// <summary>Are an island's positions exactly as they were?</summary>
    internal static bool SameIsland(byte[] hand, byte[] flat, Dictionary<int, SecondSkinWriter.NailBedPlan> plans, (int Mesh, int Island) id)
    {
        var a = SecondSkinWriter.ReadCapMeshes(hand).Single(x => x.Mesh == id.Mesh).Pos;
        var b = SecondSkinWriter.ReadCapMeshes(flat).Single(x => x.Mesh == id.Mesh).Pos;
        return plans[id.Mesh].VertsOf[id.Island].All(v => a[v] == b[v]);
    }
}
