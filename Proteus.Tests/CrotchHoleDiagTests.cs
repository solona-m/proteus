using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Scratch diagnostic for the hole reported in the crotch of the SMOOTHED body — the skin itself, before
/// any shell is cut from it.
/// <para/>
/// Runs against %TEMP%\proteus-shell-dump, which holds the exact bodies the game was handed. Those files
/// are the SMOOTHED bodies, not the mod's originals: SecondSkinService re-points every surface at the
/// republished body before it dumps (see the smoothedBody swap), so what is measured here is what was on
/// screen.
/// <para/>
/// The question it answers is which of three things the hole is, because the fix differs completely:
/// <list type="number">
/// <item>A seam between two mesh PARTS that were never welded to each other. Welding is per-mesh — both
/// BustBridgeSolve and FoldPlan call WeldByPosition on one mesh's vertex array — so two parts sharing a
/// boundary get independent solves and can drift apart. Shows up as open edges in two meshes at the same
/// positions.</item>
/// <item>A tear INSIDE one mesh: open edges with no partner in any other mesh.</item>
/// <item>No hole in the topology at all, in which case the black wedge is folded or inverted triangles
/// and every weld in the world will not close it. Shows up as zero open edges near the crotch, with
/// flipped normals or degenerate triangles instead.</item>
/// </list>
/// </summary>
public class CrotchHoleDiagTests
{
    private readonly ITestOutputHelper o;
    public CrotchHoleDiagTests(ITestOutputHelper o) => this.o = o;

    private static string Dump => Path.Combine(Path.GetTempPath(), "proteus-shell-dump");

    /// <summary>One mesh's geometry, welded by position exactly the way the relax passes weld it.</summary>
    private sealed class Mesh
    {
        public required int Index;
        public required string Material;
        public required bool Skin;
        public required Vector3[] Pos;
        public required ushort[] Tris;
        public required int[] NodeOf;
        public required Vector3[] NodePos;
    }

    [Fact]
    public void WhereIsTheHoleInTheSmoothedBody()
    {
        var files = Directory.Exists(Dump)
            ? Directory.GetFiles(Dump, "host*_body*.mdl").OrderBy(f => f).ToList()
            : new List<string>();

        // The dump only ever holds the SMOOTHED body, so on its own it cannot say whether a hole was
        // inherited from the mod or opened by our own pass. Point this at the mod's originals to compare;
        // match them up by vertex and triangle count, which the relax leaves untouched.
        var extra = Environment.GetEnvironmentVariable("PROTEUS_DIAG_EXTRA");
        if (!string.IsNullOrWhiteSpace(extra))
            foreach (var spec in extra.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(spec)) { files.Add(spec); continue; }
                var dir = Path.GetDirectoryName(spec);
                var pat = Path.GetFileName(spec);
                if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir!, pat).OrderBy(x => x));
                else if (Directory.Exists(spec)) files.AddRange(Directory.GetFiles(spec, "*.mdl").OrderBy(x => x));
            }

        if (files.Count == 0) { o.WriteLine($"skipped — no dump at {Dump} and no PROTEUS_DIAG_EXTRA"); return; }

        foreach (var f in files)
        {
            o.WriteLine(new string('=', 78));
            o.WriteLine($"{Path.GetFileName(f)}  ({new FileInfo(f).Length:N0} bytes, "
                      + $"written {new FileInfo(f).LastWriteTime:yyyy-MM-dd HH:mm:ss})");
            byte[] mdl;
            try { mdl = File.ReadAllBytes(f); } catch (Exception ex) { o.WriteLine($"  unreadable: {ex.Message}"); continue; }

            SecondSkinWriter.Source src;
            try { src = SecondSkinWriter.Parse(mdl); }
            catch (Exception ex) { o.WriteLine($"  not a model we can parse: {ex.Message}"); continue; }

            var meshes = ReadMeshes(mdl, src);
            if (meshes.Count == 0) { o.WriteLine("  no LOD0 meshes"); continue; }

            foreach (var me in meshes)
            {
                var (lo, hi) = Bounds(me.Pos);
                o.WriteLine($"  mesh {me.Index,2} {(me.Skin ? "SKIN" : "    ")} {me.Material,-40} "
                          + $"{me.Pos.Length,6} verts ({me.NodePos.Length,6} welded) {me.Tris.Length / 3,6} tris");
                o.WriteLine($"          y {lo.Y,7:F3}..{hi.Y,7:F3}   x {lo.X,7:F3}..{hi.X,7:F3}   z {lo.Z,7:F3}..{hi.Z,7:F3}");
            }

            // ── 1. open edges, per mesh, over WELDED nodes ────────────────────────────────────────────
            // An edge used by exactly one triangle is a boundary. A body part legitimately has them where
            // it meets another part (the waist ring, a wrist), so the count alone means nothing — where
            // they sit, and whether another mesh has matching ones, is the whole question.
            var openByMesh = new Dictionary<int, List<(Vector3 A, Vector3 B)>>();
            foreach (var me in meshes)
            {
                if (!me.Skin) continue;
                var open = OpenEdges(me);
                openByMesh[me.Index] = open;
                if (open.Count == 0) { o.WriteLine($"  mesh {me.Index,2}: watertight (no open edges)"); continue; }
                var mids = open.Select(e => (e.A + e.B) * 0.5f).ToArray();
                var (lo, hi) = Bounds(mids);
                o.WriteLine($"  mesh {me.Index,2}: {open.Count} OPEN edge(s)  "
                          + $"y {lo.Y:F3}..{hi.Y:F3}  x {lo.X:F3}..{hi.X:F3}  z {lo.Z:F3}..{hi.Z:F3}");
                foreach (var (c, n, blo, bhi) in ClusterByY(mids))
                    o.WriteLine($"          band y {blo:F3}..{bhi:F3}: {n,5} edge(s), centre {F(c)}");
            }

            // ── 2. do two different meshes share the same boundary? ───────────────────────────────────
            // This is the user's hypothesis made measurable. If the crotch is a part seam, two meshes hold
            // open edges at the SAME positions — and since each is relaxed independently, they can move
            // apart. Matching is by the same 1e-5 quantization WeldByPosition uses.
            var owners = new Dictionary<(int, int, int), List<int>>();
            foreach (var (mi, open) in openByMesh)
                foreach (var p in open.SelectMany(e => new[] { e.A, e.B }).Distinct())
                {
                    var k = Key(p);
                    if (!owners.TryGetValue(k, out var l)) owners[k] = l = new List<int>();
                    if (!l.Contains(mi)) l.Add(mi);
                }
            var shared = owners.Where(kv => kv.Value.Count > 1).ToList();
            o.WriteLine($"  boundary points shared by 2+ skin meshes: {shared.Count}");
            foreach (var g in shared.GroupBy(kv => string.Join("+", kv.Value.OrderBy(x => x))))
            {
                var pts = g.Select(kv => new Vector3(kv.Key.Item1 / 1e5f, kv.Key.Item2 / 1e5f, kv.Key.Item3 / 1e5f)).ToArray();
                var (lo, hi) = Bounds(pts);
                o.WriteLine($"      meshes {g.Key}: {pts.Length,5} point(s)  y {lo.Y:F3}..{hi.Y:F3}  "
                          + $"x {lo.X:F3}..{hi.X:F3}  z {lo.Z:F3}..{hi.Z:F3}");
            }

            // ── 3. triangles that are broken rather than missing ──────────────────────────────────────
            // The other candidate: nothing is open, but triangles are folded onto each other. A degenerate
            // (zero-area) triangle renders as nothing, which is a hole you cannot weld shut.
            foreach (var me in meshes)
            {
                if (!me.Skin) continue;
                int degen = 0, tiny = 0;
                float worstAspect = 0f;
                for (int t = 0; t + 2 < me.Tris.Length; t += 3)
                {
                    var a = me.Pos[me.Tris[t]]; var b = me.Pos[me.Tris[t + 1]]; var c = me.Pos[me.Tris[t + 2]];
                    float area = Vector3.Cross(b - a, c - a).Length() * 0.5f;
                    if (me.Tris[t] == me.Tris[t + 1] || me.Tris[t + 1] == me.Tris[t + 2] || me.Tris[t] == me.Tris[t + 2]) degen++;
                    else if (area <= 1e-12f) degen++;
                    else
                    {
                        float longest = MathF.Max((b - a).Length(), MathF.Max((c - b).Length(), (a - c).Length()));
                        float aspect = longest * longest / area;
                        if (aspect > worstAspect) worstAspect = aspect;
                        if (area < 1e-9f) tiny++;
                    }
                }
                o.WriteLine($"  mesh {me.Index,2}: {degen} degenerate, {tiny} near-zero-area, "
                          + $"worst aspect {worstAspect:F0}");
            }

            boundaries[Path.GetFileName(f)] = openByMesh.Values.SelectMany(l => l)
                .SelectMany(e => new[] { e.A, e.B }).Distinct().ToArray();
            geometry[Path.GetFileName(f)] = meshes.Where(x => x.Skin).ToList();
        }

        // ── 4. ACROSS FILES ───────────────────────────────────────────────────────────────────────────
        // The body arrives as several models — torso, legs, arms — and SmoothBodyNipples is called on each
        // one SEPARATELY. WeldByPosition only ever sees one file's one mesh, so a boundary two files share
        // is welded in neither: each side is relaxed against its own neighbours and they are free to drift
        // apart. This is the only place that can show it.
        o.WriteLine(new string('=', 78));
        o.WriteLine("shared boundaries BETWEEN files (what no weld in the current code covers)");
        var namesList = boundaries.Keys.OrderBy(x => x).ToList();
        for (int i = 0; i < namesList.Count; i++)
            for (int j = i + 1; j < namesList.Count; j++)
            {
                var a = boundaries[namesList[i]]; var b = boundaries[namesList[j]];
                if (a.Length == 0 || b.Length == 0) continue;
                int coincident = 0, near = 0;
                float worst = 0f;
                var bKeys = new HashSet<(int, int, int)>(b.Select(Key));
                foreach (var p in a)
                {
                    if (bKeys.Contains(Key(p))) { coincident++; continue; }
                    float d = float.MaxValue;
                    foreach (var q in b) d = MathF.Min(d, Vector3.Distance(p, q));
                    if (d < 0.02f) { near++; worst = MathF.Max(worst, d); }
                }
                if (coincident == 0 && near == 0) continue;
                o.WriteLine($"  {namesList[i]} <-> {namesList[j]}: {coincident} exactly coincident, "
                          + $"{near} within 20mm but NOT coincident (worst {worst * 1000:F2}mm)");
            }

        // ── 5. same topology, different bytes: what did the relax actually move? ──────────────────────
        // Matching a smoothed body to the mod's original by vertex+triangle count, then measuring the
        // movement AT THE BOUNDARY specifically. A relax that moves a boundary vertex is a relax that
        // opens a seam, because the file on the other side of that boundary never hears about it.
        o.WriteLine(new string('=', 78));
        o.WriteLine("same-topology pairs (smoothed vs original), movement at the shared boundary");
        var byShape = geometry.Where(kv => kv.Value.Count == 1)
                              .GroupBy(kv => (kv.Value[0].Pos.Length, kv.Value[0].Tris.Length))
                              .Where(g => g.Count() > 1);
        foreach (var g in byShape)
        {
            var items = g.ToList();
            for (int i = 0; i < items.Count; i++)
                for (int j = i + 1; j < items.Count; j++)
                {
                    var A = items[i].Value[0]; var B = items[j].Value[0];
                    var openA = new HashSet<(int, int, int)>(OpenEdges(A).SelectMany(e => new[] { e.A, e.B }).Select(Key));
                    int moved = 0, movedOnBoundary = 0;
                    float worst = 0f, worstBoundary = 0f;
                    for (int v = 0; v < A.Pos.Length; v++)
                    {
                        float d = Vector3.Distance(A.Pos[v], B.Pos[v]);
                        if (d <= 1e-6f) continue;
                        moved++; worst = MathF.Max(worst, d);
                        if (openA.Contains(Key(A.Pos[v]))) { movedOnBoundary++; worstBoundary = MathF.Max(worstBoundary, d); }
                    }
                    o.WriteLine($"  {items[i].Key} vs {items[j].Key}: {moved} vertex(es) differ "
                              + $"(max {worst * 1000:F2}mm); OF THOSE {movedOnBoundary} sit on an open "
                              + $"boundary (max {worstBoundary * 1000:F2}mm)");
                }
        }
    }

    private readonly Dictionary<string, Vector3[]> boundaries = new();
    private readonly Dictionary<string, List<Mesh>> geometry = new();

    /// <summary>
    /// The same question asked INSIDE the pass, so no guess about which of the mod's variants was equipped
    /// can confuse it: take a model, run SmoothBodyNipples on it, and measure what moved on an OPEN
    /// BOUNDARY. A boundary vertex is the rim of a hole. Nothing on the other side of that rim — the part
    /// that plugs into it, or simply the hole staying the size it was authored — hears about the move, so
    /// any displacement there widens a gap rather than smoothing a surface.
    /// </summary>
    [Fact]
    public void TheRelaxMustNotMoveTheRimOfAHole()
    {
        var target = Environment.GetEnvironmentVariable("PROTEUS_DIAG_MODEL")
                  ?? @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT LEGS - SmallClothes\GEN C Small.mdl";
        if (!File.Exists(target)) { o.WriteLine($"skipped — no model at {target}"); return; }

        var before = File.ReadAllBytes(target);
        var gate = new SecondSkinLayer { MaterialName = "/probe.mtrl" };   // no coverage map = covers all
        var after = SecondSkinWriter.SmoothBodyNipples(before, gate, 1f, o.WriteLine, 1f);
        if (after == null) { o.WriteLine("the pass declined this model (no region found)"); return; }

        var a = ReadMeshes(before, SecondSkinWriter.Parse(before)).Single(m => m.Skin);
        var b = ReadMeshes(after, SecondSkinWriter.Parse(after)).Single(m => m.Skin);

        var rim = new HashSet<(int, int, int)>(OpenEdges(a).SelectMany(e => new[] { e.A, e.B }).Select(Key));
        o.WriteLine($"{Path.GetFileName(target)}: {a.Pos.Length} verts, {rim.Count} of them on an open boundary");

        int moved = 0, rimMoved = 0;
        float worst = 0f, worstRim = 0f;
        var rimHits = new List<(Vector3 At, float D)>();
        for (int v = 0; v < a.Pos.Length && v < b.Pos.Length; v++)
        {
            float d = Vector3.Distance(a.Pos[v], b.Pos[v]);
            if (d <= 1e-6f) continue;
            moved++; worst = MathF.Max(worst, d);
            if (rim.Contains(Key(a.Pos[v]))) { rimMoved++; worstRim = MathF.Max(worstRim, d); rimHits.Add((a.Pos[v], d)); }
        }
        o.WriteLine($"the pass moved {moved} vertex(es), max {worst * 1000:F2}mm");
        o.WriteLine($"OF THOSE, {rimMoved} sit on the rim of a hole, max {worstRim * 1000:F2}mm");
        foreach (var (at, d) in rimHits.OrderByDescending(x => x.D).Take(20))
            o.WriteLine($"    rim vertex {F(at)} moved {d * 1000:F2}mm");

        // The pass must still DO something, or this passes by doing nothing at all — which is exactly how
        // the scoped-too-widely version of the pin looked when it silently killed the span.
        Assert.True(moved > 0, "the pass moved nothing — the fold is inert, not pinned");
        Assert.True(rimMoved == 0,
            $"{rimMoved} vertex(es) on the rim of a hole moved, worst {worstRim * 1000:F2}mm — that rim is "
          + "shared with a surface this pass never sees, so moving it prises the hole open");

        // Slivers the pass created that the source did not have. A collapsed triangle renders as nothing,
        // so it is a hole that no amount of pinning closes — worth knowing whether pinning the rim merely
        // moved the damage next door.
        var sa = Slivers(a); var sb = Slivers(b);
        o.WriteLine($"near-zero-area triangles: {sa.Count} before -> {sb.Count} after");
        foreach (var (at, area, onRim) in sb.OrderBy(x => x.Area).Take(12))
            o.WriteLine($"    sliver at {F(at)} area {area:E2}{(onRim ? "  ON THE PINNED RIM" : "")}");

        static List<(Vector3 At, float Area, bool OnRim)> Slivers(Mesh me)
        {
            var rim = new HashSet<(int, int, int)>(OpenEdges(me).SelectMany(e => new[] { e.A, e.B }).Select(Key));
            var outp = new List<(Vector3, float, bool)>();
            for (int t = 0; t + 2 < me.Tris.Length; t += 3)
            {
                var p = me.Pos[me.Tris[t]]; var q = me.Pos[me.Tris[t + 1]]; var r = me.Pos[me.Tris[t + 2]];
                if (me.Tris[t] == me.Tris[t + 1] || me.Tris[t + 1] == me.Tris[t + 2] || me.Tris[t] == me.Tris[t + 2]) continue;
                float area = Vector3.Cross(q - p, r - p).Length() * 0.5f;
                if (area >= 1e-9f) continue;
                bool onRim = rim.Contains(Key(p)) || rim.Contains(Key(q)) || rim.Contains(Key(r));
                outp.Add(((p + q + r) / 3f, area, onRim));
            }
            return outp;
        }
    }

    private static List<Mesh> ReadMeshes(byte[] mdl, SecondSkinWriter.Source src)
    {
        var names = SecondSkinWriter.MaterialNames(mdl);
        var outp = new List<Mesh>();
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        Span<float> t = stackalloc float[4];
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > mdl.Length) break;
            ushort vc = BitConverter.ToUInt16(mdl, mo);
            if (vc == 0) continue;
            ushort matIdx = BitConverter.ToUInt16(mdl, mo + 8);
            var decl = m < src.Decls.Length ? src.Decls[m] : Array.Empty<SecondSkinWriter.VElem>();
            var pe = decl.FirstOrDefault(x => x.Usage == SecondSkinWriter.UsePosition);
            if (pe.Stream > 2) continue;
            uint vbo = BitConverter.ToUInt32(mdl, mo + 20 + pe.Stream * 4);
            byte stride = mdl[mo + 32 + pe.Stream];
            if (stride == 0) continue;

            var pos = new Vector3[vc];
            for (int v = 0; v < vc; v++)
            {
                SecondSkinWriter.ReadTyped(mdl, src.Vb + (int)vbo + v * stride + pe.Offset, pe.Type, t);
                pos[v] = new Vector3(t[0], t[1], t[2]);
            }

            ushort subIdx = BitConverter.ToUInt16(mdl, mo + 10), subCount = BitConverter.ToUInt16(mdl, mo + 12);
            var tris = new List<ushort>();
            for (int su = 0; su < subCount; su++)
            {
                int ss = src.SubmeshStart + (subIdx + su) * 16;
                if (ss + 8 > mdl.Length) break;
                uint so = BitConverter.ToUInt32(mdl, ss), sc = BitConverter.ToUInt32(mdl, ss + 4);
                for (uint k = 0; k + 2 < sc; k += 3)
                {
                    int p = src.Ib + (int)(so + k) * 2;
                    if (p + 6 > mdl.Length) break;
                    tris.Add(BitConverter.ToUInt16(mdl, p));
                    tris.Add(BitConverter.ToUInt16(mdl, p + 2));
                    tris.Add(BitConverter.ToUInt16(mdl, p + 4));
                }
            }

            var nodeOf = Weld(pos, out var nodePos);
            string mat = matIdx < names.Count ? names[matIdx] : $"?{matIdx}";
            outp.Add(new Mesh
            {
                Index = m,
                Material = mat,
                Skin = SecondSkinWriter.SkinMaterialBodyType(mat) != null,
                Pos = pos,
                Tris = tris.ToArray(),
                NodeOf = nodeOf,
                NodePos = nodePos,
            });
        }
        return outp;
    }

    /// <summary>The same 1e-5 quantization WeldByPosition uses, so this measures what the passes see.</summary>
    private static (int, int, int) Key(Vector3 p) =>
        ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));

    private static int[] Weld(Vector3[] pos, out Vector3[] nodePos)
    {
        var nodeOf = new int[pos.Length];
        var byPos = new Dictionary<(int, int, int), int>(pos.Length);
        var reps = new List<Vector3>();
        for (int i = 0; i < pos.Length; i++)
        {
            var k = Key(pos[i]);
            if (!byPos.TryGetValue(k, out int n)) { byPos[k] = n = reps.Count; reps.Add(pos[i]); }
            nodeOf[i] = n;
        }
        nodePos = reps.ToArray();
        return nodeOf;
    }

    /// <summary>
    /// How flat the front of the crotch actually is, before and after the pass — the thing being asked
    /// for, measured the way the eye sees it.
    /// <para/>
    /// The visible surface is the SILHOUETTE: for each lateral bin across the corridor, the frontmost
    /// vertex (+Z is forward). Flatness is then how far that outline departs from a straight line across
    /// the corridor. Peak-to-trough is reported alongside it because a bowed-but-smooth cross-section and
    /// a ridged one can share an rms, and only the second reads as a fold.
    /// <para/>
    /// Deliberately NOT the "fit leaves N mm rms on the envelope" the pass already logs: that is the
    /// residual of the fit against the surface it started from, so it says how well the target was
    /// described, not how flat anything ended up.
    /// </summary>
    [Fact]
    public void HowFlatIsItBetweenTheLegs()
    {
        var target = Environment.GetEnvironmentVariable("PROTEUS_DIAG_MODEL")
                  ?? @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT LEGS - SmallClothes\GEN C Small.mdl";
        if (!File.Exists(target)) { o.WriteLine($"skipped — no model at {target}"); return; }

        var before = File.ReadAllBytes(target);
        var gate = new SecondSkinLayer { MaterialName = "/probe.mtrl" };
        var after = SecondSkinWriter.SmoothBodyNipples(before, gate, 0f, o.WriteLine, 1f);
        if (after == null) { o.WriteLine("the pass declined this model"); return; }

        var a = ReadMeshes(before, SecondSkinWriter.Parse(before)).Single(m => m.Skin);
        var b = ReadMeshes(after, SecondSkinWriter.Parse(after)).Single(m => m.Skin);

        // The pass's own numbers for this body, read off its log above: the crotch sits at y=0.858 and is
        // 66.3mm across, so the corridor is half of that.
        const float Lowest = 0.858f, CrotchHalf = 0.0331f;
        float corridor = CrotchHalf * 0.5f;
        float top = Lowest + CrotchHalf * 2f * 0.5f;

        o.WriteLine($"corridor +/-{corridor * 1000:F1}mm about x=0, bands y {Lowest:F3}..{top:F3}");
        o.WriteLine("  band      before rms    after rms    before p2t     after p2t");

        double sumA = 0, sumB = 0; int n = 0;
        for (float y = Lowest; y < top; y += 0.003f)
        {
            var pa = Silhouette(a, y, y + 0.003f, corridor);
            var pb = Silhouette(b, y, y + 0.003f, corridor);
            if (pa.Count < 6 || pb.Count < 6) continue;
            var (rmsA, p2tA) = Flatness(pa);
            var (rmsB, p2tB) = Flatness(pb);
            sumA += rmsA; sumB += rmsB; n++;
            o.WriteLine($"  y {y:F3}  {rmsA * 1000,9:F3}mm {rmsB * 1000,9:F3}mm  "
                      + $"{p2tA * 1000,9:F3}mm {p2tB * 1000,9:F3}mm");
        }
        if (n == 0) { o.WriteLine("no band had enough surface to measure"); return; }
        o.WriteLine($"MEAN over {n} band(s): {sumA / n * 1000:F3}mm -> {sumB / n * 1000:F3}mm "
                  + $"({(sumA > 0 ? (1 - sumB / sumA) * 100 : 0):F1}% flatter)");
    }

    /// <summary>
    /// How far sideways the fold actually reaches, and how evenly it dies out.
    /// <para/>
    /// The complaint this answers is the garment's trim wobbling: the trim runs down the side of the
    /// crotch, so anything the pass does to the body out there is inherited by the shell cut from it. A
    /// displacement that merely REACHES the trim is not the problem — one that reaches it unevenly is,
    /// because a fixed lift moves the trim and a ragged one makes it wander.
    /// <para/>
    /// So both are reported per lateral bin: the mean move (how much) and the spread within the bin (how
    /// even). A bin where the spread rivals the mean is a bin where the edge wobbles.
    /// </summary>
    [Fact]
    public void HowFarSidewaysDoesTheFoldReach()
    {
        var target = Environment.GetEnvironmentVariable("PROTEUS_DIAG_MODEL")
                  ?? @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT LEGS - SmallClothes\GEN C Small.mdl";
        if (!File.Exists(target)) { o.WriteLine($"skipped — no model at {target}"); return; }

        var before = File.ReadAllBytes(target);
        var gate = new SecondSkinLayer { MaterialName = "/probe.mtrl" };
        var after = SecondSkinWriter.SmoothBodyNipples(before, gate, 0f, o.WriteLine, 1f);
        if (after == null) { o.WriteLine("the pass declined this model"); return; }

        var a = ReadMeshes(before, SecondSkinWriter.Parse(before)).Single(m => m.Skin);
        var b = ReadMeshes(after, SecondSkinWriter.Parse(after)).Single(m => m.Skin);

        const int Bins = 20;
        const float Reach = 0.060f;               // 60mm each side, well past the feathered corridor
        var n = new int[Bins]; var sum = new double[Bins]; var max = new float[Bins];
        var lo = new float[Bins]; var hi = new float[Bins];
        for (int i = 0; i < Bins; i++) { lo[i] = float.MaxValue; hi[i] = float.MinValue; }
        float furthest = 0f;

        for (int v = 0; v < a.Pos.Length && v < b.Pos.Length; v++)
        {
            float d = Vector3.Distance(a.Pos[v], b.Pos[v]);
            if (d <= 1e-6f) continue;
            float ax = MathF.Abs(a.Pos[v].X);
            furthest = MathF.Max(furthest, ax);
            int q = (int)(ax / Reach * Bins);
            if (q >= Bins) continue;
            n[q]++; sum[q] += d; max[q] = MathF.Max(max[q], d);
            lo[q] = MathF.Min(lo[q], d); hi[q] = MathF.Max(hi[q], d);
        }

        o.WriteLine($"furthest moved vertex sits {furthest * 1000:F1}mm from the midline");
        o.WriteLine("   |x| band      moved     mean       max     spread within the band");
        for (int q = 0; q < Bins; q++)
        {
            if (n[q] == 0) continue;
            o.WriteLine($"  {q * Reach / Bins * 1000,5:F1}-{(q + 1) * Reach / Bins * 1000,-5:F1}mm "
                      + $"{n[q],6} {sum[q] / n[q] * 1000,8:F3}mm {max[q] * 1000,8:F3}mm "
                      + $"{(hi[q] - lo[q]) * 1000,8:F3}mm");
        }
    }

    /// <summary>
    /// How far BACK between the legs the fold reaches, and what the underside looks like across there.
    /// <para/>
    /// Behind the mid-plane the envelope flatten does nothing — it is gated to front-facing surface — so
    /// everything back here is the relax alone. Two things are reported per depth slice: how much the pass
    /// moved anything at that depth, and the shape of the underside across the corridor, which is where a
    /// ridge running fore-aft would show as a bump in the profile.
    /// <para/>
    /// The underside is the LOWEST surface at each lateral position, because between the legs that is what
    /// you see looking up — not the frontmost, which is the measure the front of the crotch needs.
    /// </summary>
    [Fact]
    public void WhatHappensFurtherBackBetweenTheLegs()
    {
        var target = Environment.GetEnvironmentVariable("PROTEUS_DIAG_MODEL")
                  ?? @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT LEGS - SmallClothes\GEN C Small.mdl";
        if (!File.Exists(target)) { o.WriteLine($"skipped — no model at {target}"); return; }

        var before = File.ReadAllBytes(target);
        var gate = new SecondSkinLayer { MaterialName = "/probe.mtrl" };
        var after = SecondSkinWriter.SmoothBodyNipples(before, gate, 0f, o.WriteLine, 1f);
        if (after == null) { o.WriteLine("the pass declined this model"); return; }

        var a = ReadMeshes(before, SecondSkinWriter.Parse(before)).Single(m => m.Skin);
        var b = ReadMeshes(after, SecondSkinWriter.Parse(after)).Single(m => m.Skin);

        const float CrotchHalf = 0.0331f;
        float corridor = CrotchHalf * 0.5f;

        // ── how much the pass does at each depth ──────────────────────────────────────────────────────
        o.WriteLine("displacement by depth (z<0 is behind the mid-plane, where only the relax acts)");
        o.WriteLine("     z band      moved     mean       max     spread");
        for (float z = 0.030f; z > -0.040f; z -= 0.005f)
        {
            int n = 0; double sum = 0; float mx = 0, lo = float.MaxValue, hi = float.MinValue;
            for (int v = 0; v < a.Pos.Length && v < b.Pos.Length; v++)
            {
                var p = a.Pos[v];
                if (p.Z > z || p.Z <= z - 0.005f) continue;
                if (MathF.Abs(p.X) > corridor) continue;
                float d = Vector3.Distance(p, b.Pos[v]);
                if (d <= 1e-6f) continue;
                n++; sum += d; mx = MathF.Max(mx, d);
                lo = MathF.Min(lo, d); hi = MathF.Max(hi, d);
            }
            if (n == 0) continue;
            o.WriteLine($"  {(z - 0.005f) * 1000,6:F1}..{z * 1000,-6:F1}mm {n,5} {sum / n * 1000,8:F3}mm "
                      + $"{mx * 1000,8:F3}mm {(hi - lo) * 1000,8:F3}mm");
        }

        // ── the shape of the underside, across the corridor, at each depth ────────────────────────────
        o.WriteLine("");
        o.WriteLine("underside profile across the corridor — rms/p2t off a straight line");
        o.WriteLine("     z band     before rms    after rms    before p2t     after p2t");
        double sa = 0, sb = 0; int bands = 0;
        for (float z = 0.010f; z > -0.035f; z -= 0.005f)
        {
            var pa = Underside(a, z - 0.005f, z, corridor);
            var pb = Underside(b, z - 0.005f, z, corridor);
            if (pa.Count < 6 || pb.Count < 6) continue;
            var (rA, tA) = Flatness(pa);
            var (rB, tB) = Flatness(pb);
            sa += rA; sb += rB; bands++;
            o.WriteLine($"  {(z - 0.005f) * 1000,6:F1}..{z * 1000,-6:F1}mm {rA * 1000,9:F3}mm "
                      + $"{rB * 1000,9:F3}mm  {tA * 1000,9:F3}mm {tB * 1000,9:F3}mm");
        }
        if (bands > 0)
            o.WriteLine($"MEAN over {bands} slice(s): {sa / bands * 1000:F3}mm -> {sb / bands * 1000:F3}mm "
                      + $"({(sa > 0 ? (1 - sb / sa) * 100 : 0):F1}% flatter)");
    }

    /// <summary>
    /// Diff a HAND-EDITED crotch against the model it was edited from, and report where the modeller
    /// actually moved the surface and how far.
    /// <para/>
    /// This is the measurement that settled the nipple, for the same reason: an rms against a fitted curve
    /// says how much structure is left but never says which structure is the feature. A modeller's own
    /// relax pass says exactly where the answer is, so the pass can be aimed at it instead of at a
    /// constant that looked plausible.
    /// <para/>
    /// Matched by POSITION, not by index, because an export may reorder. Vertices that merged on the way
    /// out cannot be matched at all, so the counts are reported first — if they disagree wildly the diff
    /// below is comparing two different meshes and should not be believed.
    /// </summary>
    [Fact]
    public void WhereDidTheModellerMoveTheCrotch()
    {
        var edited = Environment.GetEnvironmentVariable("PROTEUS_DIAG_EDIT")
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                  "OneDrive", "Desktop", "crotch.mdl");
        if (!File.Exists(edited)) { o.WriteLine($"skipped — no edited model at {edited}"); return; }

        var candidates = new List<string>
        {
            Path.Combine(Path.GetTempPath(), "proteus-shell-dump", "host0_body1.mdl"),
            @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT LEGS - SmallClothes\GEN C Small.mdl",
            @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT LEGS - SmallClothes\GEN B Small.mdl",
        };

        // NOT filtered to skin materials. A round trip through a modelling package renames materials, so
        // the filter the pass itself uses rejects the very file being measured. The biggest mesh is the
        // body either way.
        var ed = ReadMeshes(File.ReadAllBytes(edited), SecondSkinWriter.Parse(File.ReadAllBytes(edited)));
        o.WriteLine($"edited: {Path.GetFileName(edited)} — {ed.Count} mesh(es)");
        foreach (var m in ed)
            o.WriteLine($"    mesh {m.Index,2} {(m.Skin ? "SKIN" : "    ")} {m.Material,-44} "
                      + $"{m.Pos.Length,6} verts {m.Tris.Length / 3,6} tris");
        if (ed.Count == 0) { o.WriteLine("no mesh in the edited model"); return; }
        var E = ed.OrderByDescending(m => m.Pos.Length).First();

        foreach (var c in candidates)
        {
            if (!File.Exists(c)) { o.WriteLine($"  (missing) {c}"); continue; }
            var src = ReadMeshes(File.ReadAllBytes(c), SecondSkinWriter.Parse(File.ReadAllBytes(c)));
            if (src.Count == 0) continue;
            var S = src.OrderByDescending(m => m.Pos.Length).First();
            o.WriteLine($"  candidate {Path.GetFileName(c),-20} {S.Pos.Length} verts, {S.Tris.Length / 3} tris"
                      + $"{(S.Pos.Length == E.Pos.Length && S.Tris.Length == E.Tris.Length ? "   <= SAME TOPOLOGY" : "")}");
        }

        // Which base was it edited from? All three share a topology, so the counts cannot say. The size of
        // the diff can: editing the smoothed body leaves only the hand edit, editing the original leaves
        // the hand edit PLUS everything the pass did, so the smallest diff names the true base.
        foreach (var c in candidates.Where(File.Exists))
        {
            var S = ReadMeshes(File.ReadAllBytes(c), SecondSkinWriter.Parse(File.ReadAllBytes(c)))
                    .OrderByDescending(m => m.Pos.Length).First();
            if (S.Pos.Length != E.Pos.Length) continue;
            int mv = 0; float mx = 0;
            for (int i = 0; i < E.Pos.Length; i++)
            {
                float d = Vector3.Distance(S.Pos[i], E.Pos[i]);
                if (d <= 2e-5f) continue;
                mv++; mx = MathF.Max(mx, d);
            }
            o.WriteLine($"  vs {Path.GetFileName(c),-20} {mv,5} moved, max {mx * 1000,7:F2}mm");
        }

        // THE ORIGINAL, always — never the dumped smoothed body, however much smaller that diff looks.
        //
        // The dump is rewritten every time the game recomposites, so it is not a fixed reference: measured
        // here, it was rewritten eight minutes AFTER the hand edit was saved, and the "modeller's edit"
        // silently became `edit minus a newer pass output`. It showed as the edit's own bounding box
        // drifting between runs of this test (±10.7mm, then ±13.5mm, then ±15.5mm) while nothing about the
        // edited file had changed.
        //
        // Against the original the diff is the WHOLE target — everything the surface has to do to get from
        // the mod's mesh to the one the modeller signed off — and the pass's own displacement is measured
        // from the same place, so the two columns are comparable and neither moves.
        var best = candidates.Where(File.Exists).Skip(1)
            .Select(c => (Path: c, M: ReadMeshes(File.ReadAllBytes(c), SecondSkinWriter.Parse(File.ReadAllBytes(c)))
                                    .OrderByDescending(m => m.Pos.Length).FirstOrDefault()))
            .Where(x => x.M != null)
            .FirstOrDefault();
        if (best.M == null) { o.WriteLine("no candidate source could be read"); return; }
        o.WriteLine($"diffing against {Path.GetFileName(best.Path)}");
        var B = best.M!;

        // Index-aligned when the counts agree; otherwise nearest position, which survives a reorder.
        var byPos = new Dictionary<(int, int, int), int>();
        if (B.Pos.Length != E.Pos.Length)
            for (int i = 0; i < B.Pos.Length; i++) byPos[Key(B.Pos[i])] = i;

        int moved = 0; float worst = 0f;
        var hits = new List<(Vector3 At, Vector3 D)>();
        int n2 = Math.Min(B.Pos.Length, E.Pos.Length);
        for (int i = 0; i < (B.Pos.Length == E.Pos.Length ? n2 : E.Pos.Length); i++)
        {
            Vector3 from;
            if (B.Pos.Length == E.Pos.Length) from = B.Pos[i];
            else
            {
                // Unmatched means the export merged or added it; skip rather than invent a pairing.
                if (!byPos.TryGetValue(Key(E.Pos[i]), out int j)) continue;
                from = B.Pos[j];
            }
            var d = E.Pos[i] - from;
            float m = d.Length();
            if (m <= 2e-5f) continue;
            moved++; worst = MathF.Max(worst, m);
            hits.Add((from, d));
        }

        o.WriteLine($"{moved} vertex(es) moved, max {worst * 1000:F2}mm");
        if (moved == 0) return;

        var at = hits.Select(h => h.At).ToArray();
        var (lo, hi) = Bounds(at);
        o.WriteLine($"edit region: x {lo.X:F4}..{hi.X:F4}  y {lo.Y:F4}..{hi.Y:F4}  z {lo.Z:F4}..{hi.Z:F4}");
        o.WriteLine($"  ({(hi.X - lo.X) * 1000:F1}mm wide, {(hi.Y - lo.Y) * 1000:F1}mm tall, "
                  + $"{(hi.Z - lo.Z) * 1000:F1}mm deep)");

        // MY pass's own displacement, over the same bins, so the two can be read against each other. This
        // is the whole point of the exercise: not "is the modeller's edit big" but "where is the pass
        // short of it, and in which direction".
        var origPath = candidates[1];
        List<(Vector3 At, Vector3 D)> mine = new();
        if (File.Exists(origPath))
        {
            var raw = File.ReadAllBytes(origPath);
            var smoothed = SecondSkinWriter.SmoothBodyNipples(raw, new SecondSkinLayer { MaterialName = "/probe.mtrl" },
                                                              0f, null, 1f);
            if (smoothed != null)
            {
                var O = ReadMeshes(raw, SecondSkinWriter.Parse(raw)).OrderByDescending(m => m.Pos.Length).First();
                var P = ReadMeshes(smoothed, SecondSkinWriter.Parse(smoothed)).OrderByDescending(m => m.Pos.Length).First();
                for (int i = 0; i < O.Pos.Length && i < P.Pos.Length; i++)
                {
                    var d = P.Pos[i] - O.Pos[i];
                    if (d.Length() > 1e-6f) mine.Add((O.Pos[i], d));
                }
            }
        }
        o.WriteLine($"the pass moves {mine.Count} vertex(es) for comparison");

        foreach (var (name, sel) in new (string, Func<Vector3, float>)[]
                 { ("x", v => v.X), ("y", v => v.Y), ("z", v => v.Z) })
        {
            o.WriteLine($"  by {name}:            ------ modeller ------   -------- pass --------");
            float blo = sel(lo), bhi = sel(hi), w2 = MathF.Max(1e-6f, bhi - blo);
            for (int q = 0; q < 10; q++)
            {
                float a0 = blo + w2 * q / 10, a1 = blo + w2 * (q + 1) / 10;
                var inBand = hits.Where(h => sel(h.At) >= a0 && sel(h.At) < a1).ToList();
                var mineBand = mine.Where(h => sel(h.At) >= a0 && sel(h.At) < a1).ToList();
                if (inBand.Count == 0 && mineBand.Count == 0) continue;
                o.WriteLine($"    {a0,8:F4}..{a1,-8:F4} {inBand.Count,5} @ "
                          + $"{(inBand.Count > 0 ? inBand.Average(h => h.D.Length()) * 1000 : 0),6:F2}mm mean "
                          + $"{(inBand.Count > 0 ? inBand.Max(h => h.D.Length()) * 1000 : 0),6:F2} max   "
                          + $"{mineBand.Count,5} @ "
                          + $"{(mineBand.Count > 0 ? mineBand.Average(h => h.D.Length()) * 1000 : 0),6:F2}mm mean "
                          + $"{(mineBand.Count > 0 ? mineBand.Max(h => h.D.Length()) * 1000 : 0),6:F2} max");
            }
        }
    }

    /// <summary>Lowest surface per lateral bin — what you see looking up between the legs.</summary>
    private static List<(float X, float Z)> Underside(Mesh me, float zLo, float zHi, float corridor)
    {
        const int Bins = 16;
        var best = new float[Bins]; var at = new float[Bins]; var has = new bool[Bins];
        for (int i = 0; i < me.Pos.Length; i++)
        {
            var p = me.Pos[i];
            if (p.Z < zLo || p.Z >= zHi) continue;
            if (MathF.Abs(p.X) > corridor) continue;
            int q = Math.Clamp((int)((p.X + corridor) / (2 * corridor) * Bins), 0, Bins - 1);
            if (!has[q] || p.Y < best[q]) { best[q] = p.Y; at[q] = p.X; has[q] = true; }
        }
        var outp = new List<(float, float)>();
        for (int q = 0; q < Bins; q++) if (has[q]) outp.Add((at[q], best[q]));
        return outp;
    }

    /// <summary>Frontmost vertex per lateral bin — the outline the eye actually sees.</summary>
    private static List<(float X, float Z)> Silhouette(Mesh me, float yLo, float yHi, float corridor)
    {
        const int Bins = 16;
        var best = new float[Bins]; var at = new float[Bins]; var has = new bool[Bins];
        for (int i = 0; i < me.Pos.Length; i++)
        {
            var p = me.Pos[i];
            if (p.Y < yLo || p.Y >= yHi || p.Z <= 0f) continue;
            if (MathF.Abs(p.X) > corridor) continue;
            int q = Math.Clamp((int)((p.X + corridor) / (2 * corridor) * Bins), 0, Bins - 1);
            if (!has[q] || p.Z > best[q]) { best[q] = p.Z; at[q] = p.X; has[q] = true; }
        }
        var outp = new List<(float, float)>();
        for (int q = 0; q < Bins; q++) if (has[q]) outp.Add((at[q], best[q]));
        return outp;
    }

    /// <summary>Departure from the straight line fitted across the outline: rms, and peak-to-trough.</summary>
    private static (float Rms, float P2T) Flatness(List<(float X, float Z)> p)
    {
        double sx = 0, sz = 0, sxx = 0, sxz = 0;
        foreach (var (x, z) in p) { sx += x; sz += z; sxx += x * x; sxz += x * z; }
        double d = p.Count * sxx - sx * sx;
        double m = Math.Abs(d) < 1e-12 ? 0 : (p.Count * sxz - sx * sz) / d;
        double c = (sz - m * sx) / p.Count;
        double acc = 0, lo = double.MaxValue, hi = double.MinValue;
        foreach (var (x, z) in p)
        {
            double e = z - (m * x + c);
            acc += e * e; lo = Math.Min(lo, e); hi = Math.Max(hi, e);
        }
        return ((float)Math.Sqrt(acc / p.Count), (float)(hi - lo));
    }

    private static List<(Vector3 A, Vector3 B)> OpenEdges(Mesh me)
    {
        var count = new Dictionary<(int, int), int>();
        void Bump(int a, int b)
        {
            if (a == b) return;
            var k = a < b ? (a, b) : (b, a);
            count[k] = count.TryGetValue(k, out int c) ? c + 1 : 1;
        }
        for (int t = 0; t + 2 < me.Tris.Length; t += 3)
        {
            if (me.Tris[t] >= me.NodeOf.Length || me.Tris[t + 1] >= me.NodeOf.Length || me.Tris[t + 2] >= me.NodeOf.Length) continue;
            int a = me.NodeOf[me.Tris[t]], b = me.NodeOf[me.Tris[t + 1]], c = me.NodeOf[me.Tris[t + 2]];
            Bump(a, b); Bump(b, c); Bump(c, a);
        }
        return count.Where(kv => kv.Value == 1)
                    .Select(kv => (me.NodePos[kv.Key.Item1], me.NodePos[kv.Key.Item2]))
                    .ToList();
    }

    /// <summary>Group boundary points into horizontal bands, so a waist ring reads as one band and a
    /// torn patch reads as its own.</summary>
    private static List<(Vector3 Centre, int N, float Lo, float Hi)> ClusterByY(Vector3[] pts)
    {
        var outp = new List<(Vector3, int, float, float)>();
        if (pts.Length == 0) return outp;
        var sorted = pts.OrderBy(p => p.Y).ToArray();
        int start = 0;
        for (int i = 1; i <= sorted.Length; i++)
        {
            if (i < sorted.Length && sorted[i].Y - sorted[i - 1].Y < 0.01f) continue;
            var band = sorted[start..i];
            var c = band.Aggregate(Vector3.Zero, (s, p) => s + p) / band.Length;
            outp.Add((c, band.Length, band[0].Y, band[^1].Y));
            start = i;
        }
        return outp;
    }

    private static (Vector3 Lo, Vector3 Hi) Bounds(Vector3[] p)
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var v in p) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
        return (lo, hi);
    }

    private static string F(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3})", v.X, v.Y, v.Z);
}
