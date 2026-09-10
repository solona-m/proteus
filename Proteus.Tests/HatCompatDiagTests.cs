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
/// Scratch diagnostics for Auto Hat Compat — dissects the <c>shp_hib</c> shape in hair a human already
/// made hat-compatible by hand, so the generator can be aimed at what a good one actually looks like
/// rather than at a guess.
/// <para/>
/// Nothing here asserts. These read installed mods, which no build machine has, so every test returns
/// quietly when its input is missing.
/// </summary>
public class HatCompatDiagTests(ITestOutputHelper o)
{
    /// <summary>Where installed Penumbra mods live. Overridable, because only this machine has these.</summary>
    private static readonly string Mods =
        Environment.GetEnvironmentVariable("PROTEUS_MODS") ?? @"E:\Penumbradt";

    /// <summary>The shape the game blends to while a head piece is worn.</summary>
    private const string HatShape = "shp_hib";

    /// <summary>"Scalp" — the attribute marking hair the game drops entirely under a hat.</summary>
    private const string ScalpAttr = "atr_kam";

    /// <summary>Every installed hair model, or nothing at all when the mod folder is elsewhere.</summary>
    private static string[] HairModels()
    {
        if (!Directory.Exists(Mods)) return [];
        try { return Directory.GetFiles(Mods, "*_hir.mdl", SearchOption.AllDirectories); }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }
    }

    /// <summary>
    /// How common hat compatibility actually is, and — the part that matters — whether the two halves of
    /// it travel together. If <c>shp_hib</c> without <c>atr_kam</c> were common, "smush AND hide" would be
    /// the wrong model of the feature.
    /// </summary>
    [Fact]
    public void HowManyInstalledHairsAreHatCompatible()
    {
        var files = HairModels();
        if (files.Length == 0) return;

        int both = 0, shapeOnly = 0, attrOnly = 0, neither = 0;
        foreach (var f in files)
        {
            byte[] b;
            try { b = File.ReadAllBytes(f); } catch (IOException) { continue; }
            var text = System.Text.Encoding.ASCII.GetString(b);
            bool s = text.Contains(HatShape, StringComparison.Ordinal);
            bool a = text.Contains(ScalpAttr, StringComparison.Ordinal);
            if (s && a) both++; else if (s) shapeOnly++; else if (a) attrOnly++; else neither++;
        }

        o.WriteLine($"{files.Length} installed hair models");
        o.WriteLine($"  shp_hib + atr_kam : {both}");
        o.WriteLine($"  shp_hib only      : {shapeOnly}");
        o.WriteLine($"  atr_kam only      : {attrOnly}");
        o.WriteLine($"  neither           : {neither}");
    }

    /// <summary>
    /// Dissect every hand-authored <c>shp_hib</c> that can be found: how far it moves the hair, in which
    /// direction, over how much of the mesh — and, the one structural question the generator's design
    /// rests on, WHERE the replacement vertices sit in the mesh's own vertex buffer. If a shape a real
    /// tool wrote puts them anywhere but at the end, appending is not the shape of this edit.
    /// </summary>
    [Fact]
    public void DissectAuthoredHatShapes()
    {
        var files = HairModels().Where(f =>
        {
            try { return System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(f)).Contains(HatShape, StringComparison.Ordinal); }
            catch (IOException) { return false; }
        }).Take(12).ToArray();
        if (files.Length == 0) return;

        foreach (var f in files)
        {
            o.WriteLine("");
            o.WriteLine(f.Length > Mods.Length ? f[(Mods.Length + 1)..] : f);
            try { DumpOne(File.ReadAllBytes(f)); }
            catch (Exception ex) { o.WriteLine($"  parse failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void DumpOne(byte[] mdl)
    {
        var src = SecondSkinWriter.Parse(mdl);
        o.WriteLine($"  version 0x{BitConverter.ToUInt32(mdl, 0):x8}  meshes {src.MeshCount} "
                  + $"(lod0 {src.Lod0MeshIndex}..{src.Lod0MeshIndex + src.Lod0MeshCount - 1})  "
                  + $"attrs [{string.Join(", ", src.AttrNames)}]");
        o.WriteLine($"  shapes: {string.Join(", ", src.Shapes.Keys)}");

        if (!src.Shapes.TryGetValue(HatShape, out var entries))
        {
            o.WriteLine("  (no LOD0 shp_hib entries)");
            return;
        }

        // Which submeshes carry the scalp attribute — the parts hidden outright rather than smushed.
        int scalpBit = Array.IndexOf(src.AttrNames, ScalpAttr);
        if (scalpBit >= 0)
        {
            var tagged = new List<string>();
            for (int m = 0; m < src.MeshCount; m++)
            {
                int mo = src.MeshStart + m * 36;
                int si = BitConverter.ToUInt16(mdl, mo + 10), sc = BitConverter.ToUInt16(mdl, mo + 12);
                for (int k = 0; k < sc; k++)
                    if ((BitConverter.ToUInt32(mdl, src.SubmeshStart + (si + k) * 16 + 8) & (1u << scalpBit)) != 0)
                        tagged.Add($"{m}.{k}");
            }
            o.WriteLine($"  atr_kam on submeshes: {(tagged.Count == 0 ? "(none)" : string.Join(" ", tagged))}");
        }

        // Model-space centroid of every LOD0 vertex, as the origin displacements are judged against. Not
        // the skull centre, but stable and cheap, and enough to answer "does the shape move hair inward".
        var centre = Lod0Centroid(mdl, src);
        o.WriteLine($"  lod0 centroid {F(centre)}");

        foreach (var e in entries)
        {
            // ShapeMesh names its mesh by that mesh's StartIndex, not by index.
            int mesh = -1;
            for (int m = 0; m < src.MeshCount; m++)
                if (BitConverter.ToUInt32(mdl, src.MeshStart + m * 36 + 16) == e.MeshIndexOffset) { mesh = m; break; }
            if (mesh < 0) { o.WriteLine($"  shapeMesh startIndex={e.MeshIndexOffset}: no mesh matches"); continue; }

            int mo = src.MeshStart + mesh * 36;
            ushort vc = BitConverter.ToUInt16(mdl, mo);
            uint ic = BitConverter.ToUInt32(mdl, mo + 4), startIndex = BitConverter.ToUInt32(mdl, mo + 16);

            var pos = src.Decls[mesh].FirstOrDefault(x => x.Usage == SecondSkinWriter.UsePosition);
            uint vbo = BitConverter.ToUInt32(mdl, mo + 20 + pos.Stream * 4);
            byte stride = mdl[mo + 32 + pos.Stream];

            Vector3 P(int v)
            {
                Span<float> t = stackalloc float[4];
                SecondSkinWriter.ReadTyped(mdl, src.Vb + (int)vbo + v * stride + pos.Offset, pos.Type, t);
                return new Vector3(t[0], t[1], t[2]);
            }

            // base slot -> the vertex the slot originally names; the replacement is the morphed copy.
            var moves = new List<(int Src, int Rep, float D, float Radial)>();
            var reps = new HashSet<int>();
            var srcs = new HashSet<int>();
            foreach (var (bIdx, rep) in e.Values)
            {
                if (bIdx >= ic || rep >= vc) continue;
                int sv = BitConverter.ToUInt16(mdl, src.Ib + (int)(startIndex + bIdx) * 2);
                if (sv >= vc) continue;
                var a = P(sv);
                var b = P(rep);
                var dir = a - centre;
                float len = dir.Length();
                float radial = len > 1e-6f ? Vector3.Dot(b - a, dir / len) : 0f;
                moves.Add((sv, rep, (b - a).Length(), radial));
                reps.Add(rep);
                srcs.Add(sv);
            }
            if (moves.Count == 0) { o.WriteLine($"  mesh {mesh}: no usable shape values"); continue; }

            var d = moves.Select(x => x.D).OrderBy(x => x).ToArray();
            int inward = moves.Count(x => x.Radial < 0);
            int repMin = reps.Min(), repMax = reps.Max();

            o.WriteLine($"  mesh {mesh}: {vc} verts, {ic / 3} tris, {e.Values.Length} shape values");
            o.WriteLine($"    moved {srcs.Count} distinct verts ({100.0 * srcs.Count / vc:F1}% of the mesh), "
                      + $"{reps.Count} replacements");
            o.WriteLine($"    replacement indices {repMin}..{repMax}  "
                      + $"{(repMin >= vc - reps.Count ? "APPENDED AT THE END" : "interleaved — NOT appended")}");
            o.WriteLine($"    displacement  min {d[0]:F4}  med {d[d.Length / 2]:F4}  "
                      + $"p90 {d[(int)(d.Length * 0.9)]:F4}  max {d[^1]:F4}");
            o.WriteLine($"    radial: {inward}/{moves.Count} move INWARD "
                      + $"({100.0 * inward / moves.Count:F0}%), mean radial {moves.Average(x => x.Radial):F4}");
        }
    }

    /// <summary>
    /// Put a real <c>shp_hib</c> into real hair that has none, and prove the file survived it.
    /// <para/>
    /// The fixture cannot reach the cases that matter here. Installed hair is 200 KB to 6 MB, v5 and v6,
    /// three LODs, several meshes, dozens of submeshes, and — the one with no visible symptom — often more
    /// than one vertex stream. This asserts, unlike the rest of this file: the models are optional, but when
    /// they are present a failure is a real failure.
    /// </summary>
    [Fact]
    public void ShapesRealHairWithoutBreakingIt()
    {
        // A SPREAD, not the first twenty. Sorted by size and sampled evenly, the set takes in 200 KB hair and
        // 6 MB hair, v5 and v6, and — the case worth reaching deliberately — models that already carry a
        // shape, where the new records have to append beside existing ones rather than into an empty block.
        var all = HairModels().OrderBy(f => new FileInfo(f).Length).ToArray();
        if (all.Length == 0) return;
        int step = Math.Max(1, all.Length / 40);
        var files = all.Where((_, i) => i % step == 0).Take(40).ToArray();

        int done = 0, multiStream = 0, v5 = 0, hadShape = 0, windowed = 0, orphans = 0;
        Span<float> scratch = stackalloc float[4];
        foreach (var f in files)
        {
            var before = File.ReadAllBytes(f);
            SecondSkinWriter.Source src;
            try { src = SecondSkinWriter.Parse(before); } catch { continue; }
            if (!SecondSkinWriter.TryReadLod0Geometry(before, out var pos0, out _, out var tri0,
                                                      keepMaterial: _ => true)) continue;

            // Move every eighth vertex of the first LOD0 mesh a little way along +Y. What the displacement
            // means is Part 2's problem; this asks only whether the file survives being given one.
            int mesh = src.Lod0MeshIndex;
            int mo = src.MeshStart + mesh * 36;
            ushort vc = BitConverter.ToUInt16(before, mo);
            if (vc < 16) continue;
            if (before[mo + 33] != 0) multiStream++;
            if (BitConverter.ToUInt32(before, 0) < 0x01000006) v5++;

            var posEl = src.Decls[mesh].FirstOrDefault(x => x.Usage == SecondSkinWriter.UsePosition);
            uint vbo = BitConverter.ToUInt32(before, mo + 20 + posEl.Stream * 4);
            byte stride = before[mo + 32 + posEl.Stream];

            var want = new Dictionary<int, Vector3>();
            for (int v = 0; v < vc; v += 8)
            {
                SecondSkinWriter.ReadTyped(before, src.Vb + (int)vbo + v * stride + posEl.Offset, posEl.Type, scratch);
                want[v] = new Vector3(scratch[0], scratch[1] + 0.01f, scratch[2]);
            }

            byte[] after;
            // Models that already have the hat shape get a differently-named one, so the append-beside-an-
            // existing-record path is exercised on real files rather than only on the fixture.
            bool already = src.Shapes.ContainsKey(HatShape);
            if (already) hadShape++;
            string shape = already ? "shp_proteustest" : HatShape;

            var moved = new Dictionary<int, IReadOnlyDictionary<int, Vector3>> { [mesh] = want };
            int stuck;
            try { after = ModelAttributeWriter.AddShape(before, shape, moved, out stuck); }
            catch (ModelAttributeWriter.ModelEditException ex) { o.WriteLine($"  refused: {ex.Message}  {f}"); continue; }
            // Nothing is dropped any more: a mesh longer than a u16 can count gets one ShapeMesh record per
            // 65536-slot window rather than losing its tail.
            int added = want.Count - stuck;
            Assert.Equal(0, stuck);
            Assert.True(added > 0);

            var a = SecondSkinWriter.Parse(after);
            var name = Path.GetFileName(f);

            // The shape is there, over the mesh it was aimed at — and any shape the model already had is
            // still there beside it.
            Assert.True(a.Shapes.ContainsKey(shape), $"{name}: no {shape} after adding it");
            foreach (var had in src.Shapes.Keys)
                Assert.True(a.Shapes.ContainsKey(had), $"{name}: adding a shape lost the existing {had}");
            // One record per slot window: the first based at the mesh's own StartIndex, any others exactly
            // 65536 slots further in, and all of them inside the mesh's own index range.
            var entries = a.Shapes[shape];
            uint startAfter = BitConverter.ToUInt32(after, a.MeshStart + mesh * 36 + 16);
            uint icAfter = BitConverter.ToUInt32(after, a.MeshStart + mesh * 36 + 4);
            uint icBefore = BitConverter.ToUInt32(before, src.MeshStart + mesh * 36 + 4);
            Assert.Equal((int)((icBefore + 65535) / 65536), entries.Count);
            for (int w = 0; w < entries.Count; w++)
            {
                Assert.Equal(startAfter + (uint)w * 65536u, entries[w].MeshIndexOffset);
                Assert.InRange(entries[w].MeshIndexOffset, startAfter, startAfter + icAfter - 1);
                Assert.All(entries[w].Values, v => Assert.InRange(v.Replace, vc, vc + added - 1));
            }
            if (entries.Count > 1) windowed++;
            // Every window taken together covers what was asked for, bar vertices no triangle draws — real
            // models carry orphans, and a spare for one of those would deform nothing. What matters is that
            // nothing is lost to ADDRESSING any more, which the per-window checks above establish.
            int covered = entries.SelectMany(e => e.Values).Select(v => v.Replace).Distinct().Count();
            Assert.InRange(covered, 1, want.Count);
            if (covered < want.Count) orphans += want.Count - covered;

            // The mesh grew by exactly the spares, and no other mesh's vertex count moved.
            Assert.Equal(vc + added, BitConverter.ToUInt16(after, a.MeshStart + mesh * 36));
            for (int m = 0; m < src.MeshCount; m++)
                if (m != mesh)
                    Assert.Equal(BitConverter.ToUInt16(before, src.MeshStart + m * 36),
                                 BitConverter.ToUInt16(after, a.MeshStart + m * 36));

            // Nothing is displaced until the game turns the shape on, and the proof is per MESH rather than
            // through the flattening reader: that concatenates the LOD0 meshes, so mesh 0's spares land in
            // the MIDDLE of its arrays, not at the end. Compare the bytes each mesh's streams actually hold.
            Assert.True(SecondSkinWriter.TryReadLod0Geometry(after, out var pos1, out _, out var tri1,
                                                             keepMaterial: _ => true), $"{name}: unreadable after");
            Assert.Equal(tri0.Length, tri1.Length);
            Assert.Equal(pos0.Length + added * 3, pos1.Length);

            int lod0End = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
            for (int m = src.Lod0MeshIndex; m < lod0End; m++)
            {
                int bo = src.MeshStart + m * 36, ao = a.MeshStart + m * 36;
                ushort mvc = BitConverter.ToUInt16(before, bo);
                for (int j = 0; j < 3; j++)
                {
                    byte st = before[bo + 32 + j];
                    if (st == 0) continue;
                    int bAt = src.Vb + (int)BitConverter.ToUInt32(before, bo + 20 + j * 4);
                    int aAt = a.Vb + (int)BitConverter.ToUInt32(after, ao + 20 + j * 4);
                    Assert.True(before.AsSpan(bAt, mvc * st).SequenceEqual(after.AsSpan(aAt, mvc * st)),
                                $"{name}: mesh {m} stream {j} moved");
                }
            }

            // The index buffer is untouched — every existing shape addresses positions in it.
            Assert.True(before.AsSpan(src.Ib, BitConverter.ToInt32(before, 52))
                              .SequenceEqual(after.AsSpan(a.Ib, BitConverter.ToInt32(after, 52))),
                        $"{name}: the index buffer moved");

            // Self-consistent header: buffer 0 still runs exactly up to the index buffer, and the sizes agree.
            Assert.Equal(BitConverter.ToUInt32(after, 16) + BitConverter.ToUInt32(after, 40),
                         BitConverter.ToUInt32(after, 28));
            Assert.Equal(BitConverter.ToUInt32(after, 16), BitConverter.ToUInt32(after, a.LodStart + 52));
            Assert.Equal(BitConverter.ToUInt32(after, 40), BitConverter.ToUInt32(after, a.LodStart + 44));
            Assert.Equal((uint)(after.Length - before.Length),
                         BitConverter.ToUInt32(after, 8) - BitConverter.ToUInt32(before, 8)
                       + (uint)(added * TotalStride(before, mo)));

            Assert.NotNull(ModelPartReader.Read(after));
            done++;
        }
        o.WriteLine($"shaped {done} of {files.Length} real hair models "
                  + $"({multiStream} multi-stream, {v5} v5, {hadShape} already carried a shape, {windowed} needed more than one slot window, {orphans} vertices drawn by no triangle)");
        Assert.True(done > 0, "no installed hair model could be shaped");
    }

    /// <summary>
    /// What rule are the hand-authored shapes actually following?
    /// <para/>
    /// The generator has to pick a direction and a magnitude for every vertex it moves, and inventing both
    /// and then tuning them against one hairstyle is how the toe cap spent a day converging on something
    /// that fitted one foot. There are dozens of hand-made answers installed on this machine; this asks them.
    /// <para/>
    /// The hypothesis under test is "displacement is radial, toward a single point". If it holds, every
    /// displacement line passes near a common centre, and the least-squares intersection of those lines IS
    /// the head centre the generator needs — measured, not guessed. The residual says whether to believe it.
    /// </summary>
    [Fact]
    public void WhereDoAuthoredShapesPushTheHair()
    {
        var files = HairModels().Where(f =>
        {
            try { return System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(f)).Contains(HatShape, StringComparison.Ordinal); }
            catch (IOException) { return false; }
        }).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;

        o.WriteLine($"{"model",-34} {"n",6} {"centre (x,y,z)",-26} {"resid",8} {"|d| med",8} {"radial%",7} {"r med",7}");
        foreach (var f in files.Take(30))
        {
            byte[] mdl;
            try { mdl = File.ReadAllBytes(f); } catch (IOException) { continue; }
            SecondSkinWriter.Source src;
            try { src = SecondSkinWriter.Parse(mdl); } catch { continue; }
            if (!src.Shapes.TryGetValue(HatShape, out var entries)) continue;

            var pairs = new List<(Vector3 A, Vector3 B)>();
            foreach (var e in entries) CollectMoves(mdl, src, e, pairs);
            if (pairs.Count < 32) continue;

            var (centre, residual) = FitRadialCentre(pairs);
            var mag = pairs.Select(p => (p.B - p.A).Length()).OrderBy(x => x).ToArray();
            int inward = pairs.Count(p => Vector3.Dot(p.B - p.A, p.A - centre) < 0);
            var radii = pairs.Select(p => (p.A - centre).Length()).OrderBy(x => x).ToArray();

            o.WriteLine($"{Trim(f),-34} {pairs.Count,6} {F(centre),-26} {residual,8:F4} "
                      + $"{mag[mag.Length / 2],8:F4} {100.0 * inward / pairs.Count,6:F0}% {radii[radii.Length / 2],7:F4}");
        }
    }

    /// <summary>An installed c0201 head, standing in for the one the player would actually be wearing.</summary>
    private static byte[]? HeadModel()
    {
        if (!Directory.Exists(Mods)) return null;
        var f = Directory.GetFiles(Mods, "c0201f*_fac.mdl", SearchOption.AllDirectories).FirstOrDefault();
        return f == null ? null : File.ReadAllBytes(f);
    }

    /// <summary>A TexTools export of the reference hat, on the machine that owns the game. Never shipped.</summary>
    private static readonly string HatFbx =
        Environment.GetEnvironmentVariable("PROTEUS_HAT_FBX")
        ?? @"K:\Users\Corey\OneDrive\DocumentsOld\TexTools\Saved\Head\Wrangler's Hat\3D\c0201e0380_met.fbx";

    /// <summary>
    /// Where does a real hat actually sit, in the space hair vertices live in?
    /// <para/>
    /// TexTools writes centimetres and the game works in metres, but the exporter's own scaling has been
    /// wrong-by-a-factor before, so the conversion is settled by MEASUREMENT rather than by arithmetic: the
    /// candidate that puts a hat around a head — roughly 20 cm across, sitting at the top of the hair — is
    /// the right one, and the others are off by an order of magnitude and obvious.
    /// </summary>
    [Fact]
    public void WhereIsTheReferenceHat()
    {
        if (!File.Exists(HatFbx)) return;
        var hat = FbxMesh.Load(HatFbx);
        Assert.NotNull(hat);
        o.WriteLine($"{hat!.Positions.Length} verts, {hat.Triangles.Length / 3} triangles");

        foreach (var scale in new[] { 1f, 0.1f, 0.01f })
        {
            var (lo, hi) = Bounds(hat.Positions, scale);
            o.WriteLine($"  /{1 / scale,-4:F0} game-space bounds {F(lo)} .. {F(hi)}   size {F(hi - lo)}");
        }

        // Are the TRIANGLES sane, or only the point cloud? Bounds are computed from positions alone and say
        // nothing about the index list, so a mis-decoded polygon stream passes that check and then stretches
        // triangles right across the model — which a ray test reads as a surface everywhere, including a
        // centimetre from the head centre.
        var edges = new List<float>();
        for (int t = 0; t + 2 < hat.Triangles.Length; t += 3)
        {
            var a = hat.Positions[hat.Triangles[t]];
            var b = hat.Positions[hat.Triangles[t + 1]];
            var c = hat.Positions[hat.Triangles[t + 2]];
            edges.Add((b - a).Length()); edges.Add((c - b).Length()); edges.Add((a - c).Length());
        }
        edges.Sort();
        o.WriteLine($"  triangle edges: med {edges[edges.Count / 2]:F4}  p90 {edges[edges.Count * 9 / 10]:F4}  "
                  + $"max {edges[^1]:F4}   (a hat spans about 0.35, so a max near that is a scrambled index list)");
        o.WriteLine($"  index range {hat.Triangles.Min()}..{hat.Triangles.Max()} over {hat.Positions.Length} verts");

        // Against a real c0201 hair, in the same space, under the conversion that looks right.
        var hair = HairModels().FirstOrDefault(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal));
        if (hair == null) return;
        var mdl = File.ReadAllBytes(hair);
        if (!SecondSkinWriter.TryReadLod0Geometry(mdl, out var pos, out _, out _, keepMaterial: _ => true)) return;
        var hp = new Vector3[pos.Length / 3];
        for (int i = 0; i < hp.Length; i++) hp[i] = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
        var (hlo, hhi) = Bounds(hp, 1f);
        o.WriteLine($"  hair {Trim(hair)} bounds {F(hlo)} .. {F(hhi)}   size {F(hhi - hlo)}");
    }

    /// <summary>
    /// The measurement the whole feature answers to: how much hair pokes through a real hat, before the
    /// press and after it.
    /// <para/>
    /// Measured INSIDE the pass — over the solve's own vertices and its own head frame — rather than on an
    /// exported model, because an export re-welds and the numbers stop describing what the pass did.
    /// <para/>
    /// A vertex is through the hat when the ray from the head centre out through it leaves the hat shell
    /// before reaching it. Vertices whose ray misses the hat entirely are not counted: the hat is an open
    /// shell with a brim, and hair below it was never the hat's business.
    /// </summary>
    [Fact]
    public void DoesThePressPutTheHairInsideARealHat()
    {
        if (!File.Exists(HatFbx)) return;
        var hat = FbxMesh.Load(HatFbx);
        if (hat == null) return;

        var files = HairModels()
            .Where(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal))
            .OrderBy(f => new FileInfo(f).Length).ToArray();
        if (files.Length == 0) return;
        int step = Math.Max(1, files.Length / 12);

        o.WriteLine($"{"hair",-34} {"verts",7} {"through",8} {"worst",8} → {"through",8} {"worst",8}  {"press med",9}");
        int improved = 0, tested = 0, totalBefore = 0, totalAfter = 0;
        foreach (var f in files.Where((_, i) => i % step == 0).Take(12))
        {
            var mdl = File.ReadAllBytes(f);
            ModelParts? parts;
            HatCompatSolve.Result solved;
            try
            {
                parts = ModelPartReader.Read(mdl);
                if (parts == null) continue;
                solved = HatCompatSolve.Solve(mdl, parts, HeadModel());
            }
            catch (Exception ex) { o.WriteLine($"  {Trim(f)}: {ex.GetType().Name}"); continue; }
            if (solved.Considered == 0) { o.WriteLine($"  {Trim(f)}: nothing to press"); continue; }

            var meshes = HatCompatSolve.ReadLod0Meshes(mdl);
            if (tested == 0)
            {
                // What the ray test is actually seeing, once, for the first model — the distances, not the
                // verdict. A metric whose verdict flips wholesale on a 3 cm move of its origin is reporting
                // something other than what it claims to.
                var hits = new List<float>();
                var lens = new List<float>();
                foreach (var mv in meshes)
                    for (int v = 0; v < mv.Positions.Length; v += 8)
                    {
                        var d = mv.Positions[v] - solved.Centre;
                        float len = d.Length();
                        if (len < 1e-5f) continue;
                        var dir = d / len;
                        if (dir.Y < 0.25f) continue;
                        float th = FirstHit(hat, solved.Centre, dir);
                        if (th <= 0f) continue;
                        hits.Add(th);
                        lens.Add(len);
                    }
                hits.Sort();
                lens.Sort();
                if (hits.Count > 0)
                    o.WriteLine($"  probe: hair r med {lens[lens.Count / 2]:F4} "
                              + $"(max {lens[^1]:F4})   hat hit med {hits[hits.Count / 2]:F4} "
                              + $"(min {hits[0]:F4} max {hits[^1]:F4})  centre {F(solved.Centre)}");
            }
            var (b4, w4, n) = Penetration(hat, meshes, solved.Centre, null);
            var (af, wa, _) = Penetration(hat, meshes, solved.Centre, solved.Moved);

            int hideTris = solved.Hide.Sum(p => p.TriangleCount);
            int allTris = parts.Parts.Where(p => p.Island < 0).Sum(p => p.TriangleCount);
            o.WriteLine($"{Trim(f),-34} {n,7} {b4,8} {w4,8:F4} → {af,8} {wa,8:F4}  {solved.MedianPress,9:F4}"
                      + $"  hide {solved.Hide.Count,3}p {(allTris > 0 ? 100.0 * hideTris / allTris : 0),5:F1}%"
                      + $"  c.y {solved.Centre.Y,6:F3} r {solved.Radius,5:F3}");
            tested++;
            if (af <= b4) improved++;
            totalBefore += b4;
            totalAfter += af;
        }
        o.WriteLine($"{improved}/{tested} hairstyles no worse after the press; "
                  + $"{totalBefore} vertices through the hat before, {totalAfter} after");
        if (tested == 0) return;
        Assert.Equal(tested, improved);
        // The press exists to get hair inside a hat, so the bar is that it mostly does — not merely that it
        // does no harm. A quarter leaves room for a hairstyle no press can save without hiding parts of it.
        Assert.True(totalAfter * 4 <= totalBefore,
                    $"the press left {totalAfter} of {totalBefore} vertices outside the hat");
    }

    /// <summary>How many sampled vertices sit outside the hat shell, and by how much at worst.</summary>
    private static (int Through, float Worst, int Sampled) Penetration(
        FbxMesh.Mesh hat, IReadOnlyList<HatCompatSolve.MeshVerts> meshes, Vector3 centre,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>>? moved)
    {
        int through = 0, sampled = 0;
        float worst = 0f;
        foreach (var mv in meshes)
        {
            moved?.TryGetValue(mv.Mesh, out var here);
            IReadOnlyDictionary<int, Vector3>? disp = null;
            if (moved != null) moved.TryGetValue(mv.Mesh, out disp);

            // Every eighth vertex: a ray cast against 864 triangles per vertex, over models with tens of
            // thousands, is the one part of this harness that would be slow enough to notice.
            for (int v = 0; v < mv.Positions.Length; v += 8)
            {
                var p = disp != null && disp.TryGetValue(v, out var np) ? np : mv.Positions[v];
                var d = p - centre;
                float len = d.Length();
                if (len < 1e-5f) continue;
                var dir = d / len;

                // THE CROWN ONLY. A brimmed hat is an open shell, and a ray heading outwards and slightly
                // down leaves through the underside of the brim — so hair sitting perfectly happily beneath
                // the brim reads as poking through the hat, which is how this metric first reported 95% of
                // every hairstyle outside a hat its bounding box fits inside. Above the horizon there is no
                // brim to cross and the nearest hit is the crown, which is the surface hair must stay under.
                if (dir.Y < 0.25f) continue;
                float tHat = FirstHit(hat, centre, dir);
                if (tHat <= 0f) continue;            // the ray misses the hat: not the hat's business
                sampled++;
                if (len > tHat) { through++; worst = MathF.Max(worst, len - tHat); }
            }
        }
        return (through, worst, sampled);
    }

    /// <summary>
    /// FARTHEST ray-mesh hit distance, or 0 for a miss. Möller–Trumbore, no acceleration.
    /// <para/>
    /// Farthest, not nearest, and the difference is not a detail. A hat is not a single surface: this one
    /// has an inner shell about 1.5 cm from the head centre, so a nearest-hit rule measures the lining and
    /// calls every hair on the model — including hair sitting comfortably inside the crown — poked through.
    /// Being outside a hat means being beyond its OUTERMOST surface along the line of sight, and that is
    /// what the last hit is.
    /// </summary>
    private static float FirstHit(FbxMesh.Mesh m, Vector3 o, Vector3 dir)
    {
        float best = 0f;
        for (int t = 0; t + 2 < m.Triangles.Length; t += 3)
        {
            var a = m.Positions[m.Triangles[t]];
            var e1 = m.Positions[m.Triangles[t + 1]] - a;
            var e2 = m.Positions[m.Triangles[t + 2]] - a;
            var h = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, h);
            if (MathF.Abs(det) < 1e-9f) continue;
            float inv = 1f / det;
            var s = o - a;
            float u = Vector3.Dot(s, h) * inv;
            if (u < 0f || u > 1f) continue;
            var q = Vector3.Cross(s, e1);
            float vv = Vector3.Dot(dir, q) * inv;
            if (vv < 0f || u + vv > 1f) continue;
            float dist = Vector3.Dot(e2, q) * inv;
            if (dist > 1e-5f && dist > best) best = dist;
        }
        return best;
    }

    /// <summary>
    /// Is the head in the FACE model, and where?
    /// <para/>
    /// The press needs to know where the skull is. Inferring it from the hair's own inner surface works
    /// only where the hair touches down, and two of twelve hairstyles never do — a big updo has nothing
    /// near the scalp in the crown direction, so the floor stays up at the hair and the press has nothing
    /// to press towards. The head model would answer it outright, IF <c>_fac</c> carries the whole cranium
    /// rather than just a face; a one-sided z range would say it is only the front.
    /// </summary>
    [Fact]
    public void WhereIsTheHeadInTheFaceModel()
    {
        var faces = Directory.Exists(Mods)
            ? Directory.GetFiles(Mods, "c0201f*_fac.mdl", SearchOption.AllDirectories) : [];
        if (faces.Length == 0) return;

        foreach (var f in faces.Take(4))
        {
            var mdl = File.ReadAllBytes(f);
            if (!SecondSkinWriter.TryReadLod0Geometry(mdl, out var pos, out _, out _, keepMaterial: _ => true))
                continue;
            var p = new Vector3[pos.Length / 3];
            for (int i = 0; i < p.Length; i++) p[i] = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            var (lo, hi) = Bounds(p, 1f);
            o.WriteLine($"{Trim(f),-40} {p.Length,6} verts  {F(lo)} .. {F(hi)}");

            // The head as a sphere: centre from the bounds, radius as the median distance to it over the
            // upper half, which is cranium rather than jaw and chin.
            var c = (lo + hi) * 0.5f;
            var up = p.Where(v => v.Y > c.Y).Select(v => (v - c).Length()).OrderBy(x => x).ToArray();
            if (up.Length > 0)
                o.WriteLine($"    centre {F(c)}  upper-half radius med {up[up.Length / 2]:F4} "
                          + $"p10 {up[up.Length / 10]:F4} p90 {up[up.Length * 9 / 10]:F4}");
        }
    }

    /// <summary>
    /// Where does the hat actually SIT on the head — the band line, not the brim's outer edge?
    /// <para/>
    /// This is the one number the press needs and the only one it should need: above it a hat covers
    /// everything, so the hair can be crushed as hard as you like and nothing shows; below it the hat is not
    /// there at all and a single moved vertex is a visible defect. The brim's lowest point is NOT the line —
    /// a brim flares out well away from the head — so it is found as the height at which the hat comes
    /// CLOSEST to the head's own vertical axis.
    /// </summary>
    [Fact]
    public void WhereIsTheHatBandLine()
    {
        if (!File.Exists(HatFbx)) return;
        var hat = FbxMesh.Load(HatFbx);
        var head = HeadModel();
        if (hat == null || head == null) return;
        var frame = HatCompatSolve.HeadFrameFrom(head);
        if (frame == null) return;
        var (centre, radius) = frame.Value;
        o.WriteLine($"head centre {F(centre)}  radius {radius:F4}");

        // Horizontal distance from the head's vertical axis, sliced by height.
        o.WriteLine($"{"y",8} {"rel",7} {"min r",7} {"verts",6}");
        float bandY = 0f, bandR = float.MaxValue;
        for (float y = 1.38f; y <= 1.70f; y += 0.01f)
        {
            float min = float.MaxValue;
            int n = 0;
            foreach (var p in hat.Positions)
            {
                if (p.Y < y || p.Y >= y + 0.01f) continue;
                float r = MathF.Sqrt((p.X - centre.X) * (p.X - centre.X) + (p.Z - centre.Z) * (p.Z - centre.Z));
                if (r < min) min = r;
                n++;
            }
            if (n == 0) continue;
            o.WriteLine($"{y,8:F3} {y - centre.Y,7:+0.000;-0.000} {min,7:F4} {n,6}");
            if (min < bandR) { bandR = min; bandY = y; }
        }
        o.WriteLine($"tightest at y {bandY:F3} ({bandY - centre.Y:+0.000;-0.000} from head centre), "
                  + $"radius {bandR:F4} — head radius there is about {radius:F4}");
    }

    /// <summary>Axis-aligned bounds after the FBX-to-game axis swap, <c>game = (x, z, -y) * scale</c>.</summary>
    private static (Vector3 Lo, Vector3 Hi) Bounds(Vector3[] p, float scale)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var v in p)
        {
            var g = scale == 1f ? v : new Vector3(v.X, v.Z, -v.Y) * scale;
            lo = Vector3.Min(lo, g);
            hi = Vector3.Max(hi, g);
        }
        return (lo, hi);
    }

    /// <summary>Source and morphed position for every shape value that resolves cleanly.</summary>
    private static void CollectMoves(byte[] mdl, SecondSkinWriter.Source src,
                                     SecondSkinWriter.ShapeMeshEntry e, List<(Vector3, Vector3)> into)
    {
        int mesh = -1;
        for (int m = 0; m < src.MeshCount; m++)
            if (BitConverter.ToUInt32(mdl, src.MeshStart + m * 36 + 16) == e.MeshIndexOffset) { mesh = m; break; }
        if (mesh < 0 || mesh >= src.Decls.Length) return;

        int mo = src.MeshStart + mesh * 36;
        ushort vc = BitConverter.ToUInt16(mdl, mo);
        uint ic = BitConverter.ToUInt32(mdl, mo + 4), startIndex = BitConverter.ToUInt32(mdl, mo + 16);
        var pos = src.Decls[mesh].FirstOrDefault(x => x.Usage == SecondSkinWriter.UsePosition);
        uint vbo = BitConverter.ToUInt32(mdl, mo + 20 + pos.Stream * 4);
        byte stride = mdl[mo + 32 + pos.Stream];
        if (stride == 0) return;

        var seen = new HashSet<int>();
        Span<float> t = stackalloc float[4];
        foreach (var (bIdx, rep) in e.Values)
        {
            if (bIdx >= ic || rep >= vc || !seen.Add(rep)) continue;
            int sv = BitConverter.ToUInt16(mdl, src.Ib + (int)(startIndex + bIdx) * 2);
            if (sv >= vc) continue;
            int at = src.Vb + (int)vbo;
            SecondSkinWriter.ReadTyped(mdl, at + sv * stride + pos.Offset, pos.Type, t);
            var a = new Vector3(t[0], t[1], t[2]);
            SecondSkinWriter.ReadTyped(mdl, at + rep * stride + pos.Offset, pos.Type, t);
            var b = new Vector3(t[0], t[1], t[2]);
            if ((b - a).LengthSquared() > 1e-12f) into.Add((a, b));
        }
    }

    /// <summary>
    /// The point every displacement line passes closest to, and the RMS distance from it to those lines.
    /// <para/>
    /// Each move gives a line through <c>A</c> along its own direction; a purely radial field has them all
    /// meeting at the centre. Minimising the perpendicular distance to every line is the standard 3x3
    /// normal-equation solve, <c>(Σ I - ddᵀ) C = Σ (I - ddᵀ) A</c>. The residual is the honest part: a large
    /// one means the field is not radial and the fitted centre means nothing.
    /// </summary>
    private static (Vector3 Centre, float Residual) FitRadialCentre(List<(Vector3 A, Vector3 B)> moves)
    {
        Span<double> m = stackalloc double[9];
        Span<double> rhs = stackalloc double[3];
        foreach (var (a, b) in moves)
        {
            var d = Vector3.Normalize(b - a);
            double[] dv = [d.X, d.Y, d.Z];
            double[] av = [a.X, a.Y, a.Z];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    double p = (i == j ? 1 : 0) - dv[i] * dv[j];
                    m[i * 3 + j] += p;
                    rhs[i] += p * av[j];
                }
            }
        }

        // Cramer, on a 3x3 that is well conditioned whenever the directions are not all parallel.
        double Det(double a0, double a1, double a2, double b0, double b1, double b2, double c0, double c1, double c2)
            => a0 * (b1 * c2 - b2 * c1) - a1 * (b0 * c2 - b2 * c0) + a2 * (b0 * c1 - b1 * c0);
        double det = Det(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8]);
        if (Math.Abs(det) < 1e-9) return (Vector3.Zero, float.NaN);
        var c = new Vector3(
            (float)(Det(rhs[0], m[1], m[2], rhs[1], m[4], m[5], rhs[2], m[7], m[8]) / det),
            (float)(Det(m[0], rhs[0], m[2], m[3], rhs[1], m[5], m[6], rhs[2], m[8]) / det),
            (float)(Det(m[0], m[1], rhs[0], m[3], m[4], rhs[1], m[6], m[7], rhs[2]) / det));

        double sum = 0;
        foreach (var (a, b) in moves)
        {
            var d = Vector3.Normalize(b - a);
            var v = c - a;
            sum += (v - Vector3.Dot(v, d) * d).LengthSquared();
        }
        return (c, (float)Math.Sqrt(sum / moves.Count));
    }

    /// <summary>"&lt;mod folder&gt;/&lt;model&gt;" — the mod being the first segment under the mods root.</summary>
    private static string Trim(string f)
    {
        var rel = f.StartsWith(Mods, StringComparison.OrdinalIgnoreCase) ? f[(Mods.Length + 1)..] : f;
        var mod = rel.Split(Path.DirectorySeparatorChar)[0];
        if (mod.Length > 18) mod = mod[..18];
        return $"{mod}/{Path.GetFileNameWithoutExtension(f)}";
    }

    /// <summary>
    /// WHERE are the vertices a shape value cannot address?
    /// <para/>
    /// If they were scattered evenly through the mesh, leaving them behind would cost a little accuracy
    /// everywhere. If they are a contiguous REGION — one side of the head — then pressing everything around
    /// them and not them stretches the triangles along that boundary, and the result is a visible seam down
    /// the side rather than a slightly imperfect fit. That is the difference between a limitation and a bug,
    /// and the field report ("left side, and the hair under the hat looked broken") says which.
    /// </summary>
    [Fact]
    public void AreTheUnaddressableVerticesAllInOnePlace()
    {
        var files = HairModels()
            .Where(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;

        o.WriteLine($"{"hair",-34} {"stuck",6} {"of",6}  {"stuck centroid",-26} {"free centroid",-26} {"split"}");
        foreach (var f in files.Take(14))
        {
            byte[] mdl;
            try { mdl = File.ReadAllBytes(f); } catch (IOException) { continue; }
            SecondSkinWriter.Source src;
            try { src = SecondSkinWriter.Parse(mdl); } catch { continue; }

            int mesh = src.Lod0MeshIndex;
            int mo = src.MeshStart + mesh * 36;
            ushort vc = BitConverter.ToUInt16(mdl, mo);
            uint ic = BitConverter.ToUInt32(mdl, mo + 4), start = BitConverter.ToUInt32(mdl, mo + 16);
            if (vc == 0 || ic <= ushort.MaxValue) continue;      // this mesh fits; nothing is stuck

            var stuck = new HashSet<int>();
            for (uint s = ushort.MaxValue + 1u; s < ic; s++)
            {
                int at = src.Ib + (int)(start + s) * 2;
                if (at + 2 <= mdl.Length) stuck.Add(BitConverter.ToUInt16(mdl, at));
            }

            var verts = HatCompatSolve.ReadLod0Meshes(mdl).FirstOrDefault(m => m.Mesh == mesh);
            if (verts == null) continue;

            var a = Vector3.Zero;
            var b = Vector3.Zero;
            int na = 0, nb = 0;
            for (int v = 0; v < verts.Positions.Length; v++)
            {
                if (stuck.Contains(v)) { a += verts.Positions[v]; na++; }
                else { b += verts.Positions[v]; nb++; }
            }
            if (na == 0 || nb == 0) continue;
            a /= na;
            b /= nb;

            // A pair of centroids far apart, especially in x, means the two populations occupy different
            // parts of the head rather than being interleaved.
            float dx = MathF.Abs(a.X - b.X);
            var verdict = (a - b).Length() > 0.02f
                ? (dx > 0.015f ? "SPLIT LEFT/RIGHT" : "SPLIT (not by side)")
                : "interleaved";
            o.WriteLine($"{Trim(f),-34} {na,6} {verts.Positions.Length,6}  {F(a),-26} {F(b),-26} {verdict}");
        }
    }

    /// <summary>
    /// Could splitting an oversized mesh at a SUBMESH boundary bring every slot back into range?
    /// <para/>
    /// A shape value names an index slot relative to its mesh's own <c>StartIndex</c>, and that field is a
    /// u16 — so a mesh with more than 65535 slots has a tail no shape can reach. Cut the mesh in two at a
    /// boundary its submeshes already have, though, and the second half gets a <c>StartIndex</c> of its own
    /// and counts from zero again.
    /// <para/>
    /// This asks whether the cut is available: how many pieces each oversized mesh would need, and whether
    /// its submeshes are fine-grained enough to land one near the limit rather than miles short of it.
    /// </summary>
    [Fact]
    public void CanAnOversizedMeshBeCutAtASubmeshBoundary()
    {
        var files = HairModels().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;

        int oversized = 0, splittable = 0, hopeless = 0;
        o.WriteLine($"{"hair",-34} {"mesh",4} {"slots",8} {"subs",5} {"pieces",6}  {"largest piece"}");
        foreach (var f in files)
        {
            byte[] mdl;
            try { mdl = File.ReadAllBytes(f); } catch (IOException) { continue; }
            SecondSkinWriter.Source src;
            try { src = SecondSkinWriter.Parse(mdl); } catch { continue; }

            int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
            for (int m = src.Lod0MeshIndex; m < end; m++)
            {
                int mo = src.MeshStart + m * 36;
                if (mo + 36 > mdl.Length) break;
                uint ic = BitConverter.ToUInt32(mdl, mo + 4);
                if (ic <= ushort.MaxValue) continue;
                oversized++;

                int subIdx = BitConverter.ToUInt16(mdl, mo + 10), subCount = BitConverter.ToUInt16(mdl, mo + 12);

                // Greedily pack whole submeshes into pieces of at most 65536 slots, in the order the
                // index buffer already has them — nothing is reordered, so no existing shape moves.
                int pieces = 1, run = 0, largest = 0;
                bool ok = true;
                for (int k = 0; k < subCount; k++)
                {
                    int ss = src.SubmeshStart + (subIdx + k) * 16;
                    int count = (int)BitConverter.ToUInt32(mdl, ss + 4);
                    if (count > ushort.MaxValue + 1) { ok = false; break; }   // one submesh too big to fit
                    if (run + count > ushort.MaxValue + 1) { largest = Math.Max(largest, run); pieces++; run = count; }
                    else run += count;
                }
                largest = Math.Max(largest, run);
                if (ok) splittable++; else hopeless++;

                if (oversized <= 14)
                    o.WriteLine($"{Trim(f),-34} {m,4} {ic,8} {subCount,5} {(ok ? pieces.ToString() : "—"),6}  "
                              + $"{(ok ? largest.ToString() : "a single submesh exceeds the limit")}");
            }
        }
        o.WriteLine($"{oversized} oversized LOD0 meshes: {splittable} can be cut at submesh boundaries, "
                  + $"{hopeless} cannot");
    }

    /// <summary>
    /// Is <c>ShapeMesh.MeshIndexOffset</c> always a mesh's <c>StartIndex</c>, or is it a BASE the slot is
    /// added to?
    /// <para/>
    /// Everything about the 65535 limit turns on this. If the field merely identifies which mesh the entry
    /// belongs to, a mesh with more slots than a u16 can count has an unreachable tail and the only way to
    /// reach it is to cut the mesh up. If instead the game reads <c>indexBuffer[MeshIndexOffset + slot]</c>,
    /// then several entries per mesh — at StartIndex, StartIndex+65536, and so on — each address their own
    /// window, and there is no limit worth working around at all.
    /// <para/>
    /// Two installed mods already hint at the second reading: their shape meshes name an offset no mesh's
    /// StartIndex matches. This counts how often that happens across every hair on the machine, and whether
    /// those offsets land INSIDE some mesh's index range rather than nowhere.
    /// </summary>
    [Fact]
    public void IsShapeMeshOffsetAMeshIdOrAnIndexBase()
    {
        var files = HairModels();
        if (files.Length == 0) return;

        int exact = 0, insideAMesh = 0, nowhere = 0;
        var examples = new List<string>();
        foreach (var f in files)
        {
            byte[] mdl;
            try { mdl = File.ReadAllBytes(f); } catch (IOException) { continue; }
            SecondSkinWriter.Source src;
            try { src = SecondSkinWriter.Parse(mdl); } catch { continue; }
            if (src.Shapes.Count == 0) continue;

            // Every mesh's index range, so an offset can be judged against all of them.
            var ranges = new List<(int Mesh, uint Start, uint Count)>();
            for (int m = 0; m < src.MeshCount; m++)
            {
                int mo = src.MeshStart + m * 36;
                if (mo + 36 > mdl.Length) break;
                ranges.Add((m, BitConverter.ToUInt32(mdl, mo + 16), BitConverter.ToUInt32(mdl, mo + 4)));
            }

            foreach (var (name, entries) in src.Shapes)
                foreach (var e in entries)
                {
                    if (ranges.Any(r => r.Start == e.MeshIndexOffset)) { exact++; continue; }
                    var host = ranges.FirstOrDefault(r => e.MeshIndexOffset > r.Start
                                                       && e.MeshIndexOffset < r.Start + r.Count);
                    if (host.Count > 0)
                    {
                        insideAMesh++;
                        if (examples.Count < 8)
                            examples.Add($"{Trim(f)} {name}: offset {e.MeshIndexOffset} sits "
                                       + $"{e.MeshIndexOffset - host.Start} into mesh {host.Mesh} "
                                       + $"(start {host.Start}, {host.Count} slots)");
                    }
                    else nowhere++;
                }
        }

        o.WriteLine($"shape mesh offsets: {exact} equal a mesh StartIndex exactly, "
                  + $"{insideAMesh} land inside a mesh's range, {nowhere} match nothing");
        foreach (var e in examples) o.WriteLine("  " + e);
    }

    /// <summary>
    /// Every hairstyle in a named set of mods, patched for real and then measured against a real hat.
    /// <para/>
    /// Unlike <see cref="DoesThePressPutTheHairInsideARealHat"/>, which measures the SOLVE's proposed
    /// positions, this one writes the shape with <see cref="ModelAttributeWriter.AddShape"/> and then reads
    /// the deformed positions back out of the patched file — so the writer is in the loop. A shape whose
    /// values address the wrong slots would score perfectly on the solve and fail here.
    /// <para/>
    /// Set <c>PROTEUS_MOD_PREFIX</c> to choose which mods; c0201 only, because the reference hat is.
    /// </summary>
    [Fact]
    public void SweepOneAuthorsModsAgainstTheHat()
    {
        var prefix = Environment.GetEnvironmentVariable("PROTEUS_MOD_PREFIX") ?? "LM";
        if (!File.Exists(HatFbx)) return;
        var hat = FbxMesh.Load(HatFbx);
        if (hat == null) return;

        var files = HairModels()
            .Where(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal))
            .Where(f => Trim(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) { o.WriteLine($"no c0201 hair under a mod starting with \"{prefix}\""); return; }

        var head = HeadModel();
        o.WriteLine($"{files.Length} c0201 hairstyles under mods starting with \"{prefix}\"; "
                  + $"head model {(head == null ? "NOT FOUND — press falls back to guessing" : "loaded")}");
        o.WriteLine("");
        o.WriteLine($"{"hair",-40} {"thru",5} {"worst",7} → {"thru",5} {"worst",7} {"press",7} {"hide",5} {"win",4} {"note"}");

        int clean = 0, tested = 0, totalBefore = 0, totalAfter = 0;
        foreach (var f in files)
        {
            var before = File.ReadAllBytes(f);
            string note = "";
            ModelParts? parts;
            HatCompatSolve.Result solve;
            try
            {
                parts = ModelPartReader.Read(before);
                if (parts == null) { o.WriteLine($"{Trim(f),-40} unreadable"); continue; }
                solve = HatCompatSolve.Solve(before, parts, head);
            }
            catch (Exception ex) { o.WriteLine($"{Trim(f),-40} {ex.GetType().Name}"); continue; }

            if (HatCompatService.IsHatCompatible(before)) note = "author already did it";
            if (solve.Considered == 0) { o.WriteLine($"{Trim(f),-40} nothing to press  {note}"); continue; }

            // Write it, exactly as the plugin would.
            byte[] after;
            int windows;
            try
            {
                after = ModelAttributeWriter.AddShape(before, HatShape + "_test", solve.Moved, out _);
                var reparsed = SecondSkinWriter.Parse(after);
                windows = reparsed.Shapes.TryGetValue(HatShape + "_test", out var e) ? e.Count : 0;
                Assert.True(windows > 0, $"{Trim(f)}: the shape did not survive the write");
            }
            catch (ModelAttributeWriter.ModelEditException ex)
            {
                o.WriteLine($"{Trim(f),-40} refused: {ex.Message}");
                continue;
            }

            var meshes = HatCompatSolve.ReadLod0Meshes(before);
            var (b4, w4, _) = Penetration(hat, meshes, solve.Centre, null);
            var (af, wa, _) = Penetration(hat, meshes, solve.Centre, ShapedPositions(after, HatShape + "_test"));

            int hideTris = solve.Hide.Sum(p => p.TriangleCount);
            int allTris = parts.Parts.Where(p => p.Island < 0).Sum(p => p.TriangleCount);
            // How many vertices the press actually moved, and how many shape values that cost — the budget
            // is a u16 ceiling on the whole model, so a hairstyle can silently be pressed only in part.
            int values = SecondSkinWriter.Parse(after) is { } re
                ? BitConverter.ToUInt16(after, re.Mh + 20) : 0;
            o.WriteLine($"{Trim(f),-40} {b4,5} {w4,7:F4} → {af,5} {wa,7:F4} {solve.MedianPress,7:F4} "
                      + $"{(allTris > 0 ? 100.0 * hideTris / allTris : 0),4:F0}% {windows,4} "
                      + $"moved {solve.Considered,5} cost {values,6} {note}");

            // WHERE is what is left? A residual spread evenly over the crown is a ceiling set slightly too
            // high; one bunched in a direction is the head model being wrong there — an ear, or the face's
            // own lashes — inflating the ceiling exactly where a hat is tightest.
            if (af > 0 && tested < 3)
                foreach (var line in WorstResiduals(hat, meshes, solve, ShapedPositions(after, HatShape + "_test")))
                    o.WriteLine("      " + line);

            tested++;
            totalBefore += b4;
            totalAfter += af;
            if (af == 0) clean++;
        }

        o.WriteLine("");
        o.WriteLine($"{clean}/{tested} hairstyles end up entirely inside the hat; "
                  + $"{totalBefore} vertices through before, {totalAfter} after");
        if (tested > 0) Assert.True(totalAfter <= totalBefore);
    }

    /// <summary>
    /// The handful of vertices still outside the hat after the press, described in a way that says WHY:
    /// where they sit, how far out they are, and — the diagnostic that matters — how much room the hat
    /// actually leaves over the head in that direction.
    /// <para/>
    /// A hat that clears the head by less than the ceiling the press aims at cannot be satisfied by any
    /// press, and that is a statement about the head model, not about the tuning.
    /// </summary>
    private static List<string> WorstResiduals(
        FbxMesh.Mesh hat, IReadOnlyList<HatCompatSolve.MeshVerts> meshes, HatCompatSolve.Result solve,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>> shaped)
    {
        var worst = new List<(float Out, Vector3 P, float Room)>();
        foreach (var mv in meshes)
        {
            shaped.TryGetValue(mv.Mesh, out var disp);
            for (int v = 0; v < mv.Positions.Length; v += 8)
            {
                var p = disp != null && disp.TryGetValue(v, out var np) ? np : mv.Positions[v];
                var d = p - solve.Centre;
                float len = d.Length();
                if (len < 1e-5f) continue;
                var dir = d / len;
                if (dir.Y < 0.25f) continue;
                float tHat = FirstHit(hat, solve.Centre, dir);
                if (tHat <= 0f || len <= tHat) continue;
                worst.Add((len - tHat, p, tHat - len));
            }
        }
        if (worst.Count == 0) return [];

        // Group by side, because "the left side" is the report this is chasing.
        int left = worst.Count(w => w.P.X < -0.01f), right = worst.Count(w => w.P.X > 0.01f);
        var lines = new List<string>
        {
            $"residual {worst.Count} sampled: {left} left (x<0), {right} right (x>0), "
          + $"{worst.Count - left - right} centred",
        };
        foreach (var w in worst.OrderByDescending(w => w.Out).Take(3))
            lines.Add($"worst {w.Out * 1000:F1} mm out at {F(w.P)}  (y-height {w.P.Y - solve.Centre.Y:+0.000;-0.000})");
        return lines;
    }

    /// <summary>
    /// The positions a written shape actually produces: for every value, the ORIGINAL vertex a slot named
    /// and the replacement's position.
    /// <para/>
    /// Read back out of the patched file rather than taken from the solve, so the measurement covers the
    /// writer too — the slot arithmetic, the windows, and the spare vertices it appended.
    /// </summary>
    private static Dictionary<int, IReadOnlyDictionary<int, Vector3>> ShapedPositions(byte[] mdl, string shape)
    {
        var src = SecondSkinWriter.Parse(mdl);
        var result = new Dictionary<int, IReadOnlyDictionary<int, Vector3>>();
        if (!src.Shapes.TryGetValue(shape, out var entries)) return result;

        var verts = HatCompatSolve.ReadLod0Meshes(mdl).ToDictionary(m => m.Mesh, m => m.Positions);
        foreach (var e in entries)
        {
            // The record's base is an absolute index-buffer position; find the mesh whose range contains it.
            int mesh = -1;
            for (int m = 0; m < src.MeshCount; m++)
            {
                int mo = src.MeshStart + m * 36;
                if (mo + 36 > mdl.Length) break;
                uint start = BitConverter.ToUInt32(mdl, mo + 16), count = BitConverter.ToUInt32(mdl, mo + 4);
                if (e.MeshIndexOffset >= start && e.MeshIndexOffset < start + count) { mesh = m; break; }
            }
            if (mesh < 0 || !verts.TryGetValue(mesh, out var pos)) continue;

            if (!result.TryGetValue(mesh, out var into))
                result[mesh] = into = new Dictionary<int, Vector3>();
            var map = (Dictionary<int, Vector3>)into;

            foreach (var (slot, rep) in e.Values)
            {
                int at = src.Ib + (int)(e.MeshIndexOffset + slot) * 2;
                if (at + 2 > mdl.Length || rep >= pos.Length) continue;
                int original = BitConverter.ToUInt16(mdl, at);
                if (original < pos.Length) map[original] = pos[rep];
            }
        }
        return result;
    }

    /// <summary>Bytes one vertex of a mesh occupies across every stream it has.</summary>
    private static int TotalStride(byte[] mdl, int mo)
    {
        int n = 0;
        for (int j = 0; j < 3; j++) n += mdl[mo + 32 + j];
        return n;
    }

    private static Vector3 Lod0Centroid(byte[] mdl, SecondSkinWriter.Source src)
    {
        var sum = Vector3.Zero;
        int n = 0;
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        Span<float> t = stackalloc float[4];
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            ushort vc = BitConverter.ToUInt16(mdl, mo);
            var pos = src.Decls[m].FirstOrDefault(x => x.Usage == SecondSkinWriter.UsePosition);
            uint vbo = BitConverter.ToUInt32(mdl, mo + 20 + pos.Stream * 4);
            byte stride = mdl[mo + 32 + pos.Stream];
            if (stride == 0) continue;
            for (int v = 0; v < vc; v++)
            {
                SecondSkinWriter.ReadTyped(mdl, src.Vb + (int)vbo + v * stride + pos.Offset, pos.Type, t);
                sum += new Vector3(t[0], t[1], t[2]);
                n++;
            }
        }
        return n == 0 ? Vector3.Zero : sum / n;
    }

    private static string F(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "({0:F4}, {1:F4}, {2:F4})", v.X, v.Y, v.Z);
}
