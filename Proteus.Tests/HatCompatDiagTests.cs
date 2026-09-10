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
        try
        {
            // Not the copies Proteus took before patching. They live inside the mods they belong to and
            // are byte-identical to what the author shipped, so a sweep that picks them up measures every
            // patched hairstyle twice — once patched, once pristine — and reports the pristine one as
            // untouched. That is exactly as confusing as it sounds.
            return Directory.GetFiles(Mods, "*_hir.mdl", SearchOption.AllDirectories)
                .Where(f => !f.Contains(HatCompatService.BackupSubdir, StringComparison.OrdinalIgnoreCase)
                         && !f.Contains(MeshToggleService.BackupSubdir, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
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

    /// <summary>
    /// Where TexTools exports live on the machine that owns the game. Every c0201 head piece under it is a
    /// reference hat; nothing derived from them is ever shipped.
    /// </summary>
    private static readonly string HatRoot =
        Environment.GetEnvironmentVariable("PROTEUS_HAT_ROOT")
        ?? @"K:\Users\Corey\OneDrive\DocumentsOld\TexTools\Saved\Head";

    /// <summary>Every reference hat exported for a Midlander female, named by its folder.</summary>
    private static List<(string Name, string Path)> Hats()
    {
        var found = new List<(string, string)>();
        if (!Directory.Exists(HatRoot)) return found;
        try
        {
            foreach (var f in Directory.GetFiles(HatRoot, "c0201*_met.fbx", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(Path.GetDirectoryName(f));
                found.Add((dir == null ? "?" : Path.GetFileName(dir), f));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return found.OrderBy(h => h.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A TexTools export of one reference hat, for the tests that need something hat-shaped.</summary>
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

            int hideTris = solved.Cut.Sum(p => p.TriangleCount);
            int allTris = parts.Parts.Where(p => p.Island < 0).Sum(p => p.TriangleCount);
            o.WriteLine($"{Trim(f),-34} {n,7} {b4,8} {w4,8:F4} → {af,8} {wa,8:F4}  {solved.MedianPress,9:F4}"
                      + $"  hide {solved.Cut.Count,3}p {(allTris > 0 ? 100.0 * hideTris / allTris : 0),5:F1}%"
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
        // No stricter bar than "no worse", deliberately. This used to demand that three quarters of the
        // penetration go away, which was the right test while the press moved every covered vertex. It no
        // longer does: a strand that hangs off the head is left ALONE, tail and root together, because
        // pressing only the root drove it into the skull and splayed the rest into a flat sheet behind the
        // hat. What is left outside the hat afterwards is mostly ponytail, on purpose — hiding it is what
        // the separate setting is for — so tightening this number would only measure how many hairstyles
        // in the sample have tails.
        Assert.True(totalAfter <= totalBefore,
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
    /// For every vertex still outside a hat after the press, WHY was it left there?
    /// <para/>
    /// The sweep says how much comes through and this says what to do about it, which are different
    /// questions with different answers. Each remaining vertex falls into exactly one bucket, and each
    /// bucket has its own fix: "tail" is the hide setting doing its job, "frozen" means a triangle straddling
    /// the hat line pinned it, "ramped" means the falloff reached it before the press did, and "full press"
    /// means it was pressed as hard as the solve allows and is STILL outside — which would mean the target
    /// depth is wrong, not the gating.
    /// <para/>
    /// Written because two strands came through the crown of a hat on a hairstyle the numbers called fitted,
    /// and every candidate explanation for it was equally plausible from reading the code.
    /// </summary>
    [Fact]
    public void WhyIsAnyHairStillOutsideTheHat()
    {
        var hats = Hats();
        if (hats.Count == 0) return;
        var head = HeadModel();

        // The hairstyle that prompted this, if it is installed; otherwise a spread of whatever is.
        var all = HairModels().Where(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal))
                              .OrderBy(f => new FileInfo(f).Length).ToArray();
        var named = all.Where(f => f.Contains("Locksley", StringComparison.OrdinalIgnoreCase)).ToArray();
        var files = named.Length > 0 ? named : all.Where((_, i) => i % Math.Max(1, all.Length / 4) == 0).Take(4).ToArray();
        if (files.Length == 0) return;

        foreach (var (hatName, hatPath) in hats)
        {
            var hat = FbxMesh.Load(hatPath);
            if (hat == null) continue;

            // Two views, because they answer different complaints. Up through the CROWN is what a strand
            // standing off the top of the head does, and it is the sweep's own cut-off. Sideways as well
            // catches a strand standing out of the BACK, which leaves along a near-level ray and which the
            // crown view cannot see at all. Relaxing the cut-off is safe because the hit test takes the
            // FARTHEST surface: a level ray crosses the brim and comes back with the brim's outer edge, so
            // only hair genuinely past it counts.
            foreach (var (view, minDir) in new[] { ("crown", 0.25f), ("crown+sides", -0.1f) })
            {
            o.WriteLine($"── {hatName} ({view}) ──");
            o.WriteLine($"{"hair",-34} {"out",5} {"tail",5} {"frozen",6} {"ramped",6} {"full",5} "
                      + $"{"below",5} {"inhead",7} {"skipped",7}");

            foreach (var f in files)
            {
                byte[] mdl;
                ModelParts? parts;
                HatCompatSolve.Result solved;
                try
                {
                    mdl = File.ReadAllBytes(f);
                    parts = ModelPartReader.Read(mdl);
                    if (parts == null) continue;
                    solved = HatCompatSolve.Solve(mdl, parts, head);
                }
                catch (Exception) { continue; }

                var meshes = HatCompatSolve.ReadLod0Meshes(mdl);
                var src = SecondSkinWriter.Parse(mdl);
                float hatLine = solved.Centre.Y + HatCompatSolve.HatLine;

                // The three gates, recomputed here from the same public facts the solve used, so this
                // measures the shipped rule rather than a second copy of it that could drift.
                var tail = new HashSet<(int, int)>();
                foreach (var s in HatCompatSolve.Strands(mdl, parts, head).Where(s => s.Tail))
                    foreach (var v in HatCompatSolve.VerticesOf(mdl, src, s.Part)) tail.Add((s.Part.Mesh, v));
                var frozen = FrozenIn(mdl, src, meshes, hatLine);

                var ff = HatCompatSolve.FrameAndFloor(mdl, head);
                int outside = 0, nTail = 0, nFrozen = 0, nRamped = 0, nFull = 0, nBelow = 0, nSkipped = 0;
                int nInsideScalp = 0;
                foreach (var mv in meshes)
                {
                    solved.Moved.TryGetValue(mv.Mesh, out var movedHere);
                    for (int v = 0; v < mv.Positions.Length; v++)
                    {
                        var p = mv.Positions[v];
                        bool wasMoved = movedHere != null && movedHere.TryGetValue(v, out var to);
                        var now = wasMoved ? movedHere![v] : p;

                        var d = now - solved.Centre;
                        float len = d.Length();
                        if (len < 1e-5f) continue;
                        var dir = d / len;
                        if (dir.Y < minDir) continue;
                        float tHat = FirstHit(hat, solved.Centre, dir);
                        if (tHat <= 0f || len <= tHat) continue;

                        outside++;
                        if (tail.Contains((mv.Mesh, v))) nTail++;
                        else if (p.Y < hatLine) nBelow++;         // below the line: never eligible, by design
                        else if (frozen.Contains((mv.Mesh, v))) nFrozen++;
                        else if (!wasMoved)
                        {
                            // The press had its chance at this one and passed. Which of its reasons was it?
                            // "inside the scalp" is the interesting answer, because it means the floor —
                            // read off the FACE model, ears and all — claims the head reaches further out
                            // in this direction than it really does.
                            var od = p - solved.Centre;
                            float olen = od.Length();
                            if (olen > 1e-5f && ff != null
                             && olen <= ff.Value.Floor[HatCompatSolve.BinFor(od / olen)]) nInsideScalp++;
                            else nSkipped++;
                        }
                        else if ((now - p).Length() < (p - solved.Centre).Length() * 0.2f) nRamped++;
                        else nFull++;
                    }
                }
                o.WriteLine($"{Trim(f),-34} {outside,5} {nTail,5} {nFrozen,6} {nRamped,6} {nFull,5} "
                          + $"{nBelow,5} {nInsideScalp,7} {nSkipped,7}   (solve pressed {solved.Considered}, "
                          + $"dropped {solved.Dropped} for budget)");
            }
            }
        }
    }

    /// <summary>
    /// The same question asked of the FILE THAT SHIPS, rather than of the solve's intentions.
    /// <para/>
    /// Everything else here measures <c>Result.Moved</c> — what the press decided to do. The game never sees
    /// that. It sees a patched <c>.mdl</c>, and between the two sits <see cref="ModelAttributeWriter.AddShape"/>,
    /// which can decline a vertex it cannot address and reports that through an <c>unaddressable</c> count
    /// that <c>HatCompatService.Inspect</c> hard-codes to zero and <c>Apply</c> discards. So a vertex can be
    /// pressed in every measurement taken so far and still stand untouched in game, and nothing above would
    /// show it.
    /// <para/>
    /// This patches the model for real, reads <c>shp_hib</c> back out of the result, rebuilds the geometry
    /// the game would draw with the shape at full strength and the scalp-tagged submeshes dropped, and only
    /// then asks what is outside the hat.
    /// </summary>
    [Fact]
    public void WhatDoesTheShippedFileActuallyLookLikeUnderAHat()
    {
        var hats = Hats();
        if (hats.Count == 0) return;
        var head = HeadModel();
        var all = HairModels().Where(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal))
                              .OrderBy(f => new FileInfo(f).Length).ToArray();
        var named = all.Where(f => f.Contains("Locksley", StringComparison.OrdinalIgnoreCase)).ToArray();
        var files = named.Length > 0 ? named
                                     : all.Where((_, i) => i % Math.Max(1, all.Length / 3) == 0).Take(3).ToArray();
        if (files.Length == 0) return;

        foreach (var f in files)
        {
            // THE AUTHOR'S FILE, not the one on disk, wherever Proteus has already patched this hairstyle.
            // Solving a patched model presses hair that is already pressed and tags parts that are already
            // tagged, so every number that comes out of it describes a hairstyle nobody is wearing. It is the
            // same trap as feeding a smoothing pass its own output, and it silently invalidated a round of
            // measurement here before it was noticed.
            var pristine = Pristine(f);
            var mdl = File.ReadAllBytes(pristine ?? f);
            if (pristine != null) o.WriteLine($"  (using the backup of {Trim(f)} — the live file is patched)");
            var parts = ModelPartReader.Read(mdl);
            if (parts == null) continue;
            HatCompatSolve.Result solve;
            try { solve = HatCompatSolve.Solve(mdl, parts, head); } catch { continue; }
            if (solve.Moved.Count == 0) continue;

            // Exactly what HatCompatService.Apply does, in the order it does it.
            byte[] patched;
            int stuck;
            try
            {
                // Exactly what HatCompatService.Apply tags, which is the CUT and nothing else — hiding
                // ponytails is withdrawn, so solve.Cut is computed and not applied. Tagging Hide here
                // instead measured a patch the plugin does not produce.
                var tag = solve.Cut;
                var (split, targets) = ModelAttributeWriter.IsolateParts(mdl, tag);
                patched = tag.Count > 0
                    ? ModelAttributeWriter.AddAttribute(split, "atr_kam", targets) : split;
                patched = ModelAttributeWriter.AddShape(patched, HatShape, solve.Moved, out stuck);
            }
            catch (Exception ex) { o.WriteLine($"{Trim(f)}: {ex.Message}"); continue; }

            var src = SecondSkinWriter.Parse(patched);
            int kamBit = Array.IndexOf(src.AttrNames, "atr_kam");
            var meshes = HatCompatSolve.ReadLod0Meshes(patched);

            // Deform: a shape value redirects one index SLOT to a spare vertex, so the drawn position of a
            // corner is the spare's, not the original's. Keyed by slot for that reason — the same vertex can
            // be redirected in one triangle and not another.
            var swap = new Dictionary<uint, ushort>();
            if (src.Shapes.TryGetValue(HatShape, out var entries))
                foreach (var e in entries)
                    foreach (var (b, rep) in e.Values) swap[e.MeshIndexOffset + b] = rep;

            o.WriteLine($"{Trim(f)}: solve wanted {solve.Considered} moved "
                      + $"({solve.Dropped} dropped for budget), file carries {swap.Count} shape values "
                      + $"over {swap.Values.Distinct().Count()} spares, {stuck} unaddressable");

            foreach (var (hatName, hatPath) in hats)
            {
                var hat = FbxMesh.Load(hatPath);
                if (hat == null) continue;

                // THE VIEW FROM ABOVE, which is the one the defect was actually reported from and the one a
                // ray out of the head centre cannot take. Hair lying ON the brim sits well inside the brim's
                // outer edge, so a centre-out ray passes it, carries on, and hits the rim further out —
                // reporting the hair as safely inside the hat while it is plainly sitting on top of it.
                // Straight up from the vertex answers the real question: is there any hat between this piece
                // of hair and the sky? Asked only of hair above the hat line, since hair below it is meant
                // to be seen.
                int upMiss = 0, upMissPressed = 0, upSeen = 0, belowBandMiss = 0;
                float hatLineY = solve.Centre.Y + HatCompatSolve.HatLine;

                // And for the ones with nothing over them that the press never touched: which of its gates
                // turned them away. Computed from the AUTHOR'S model, whose vertex numbering the patched
                // file preserves — spares are appended past the original count, so an original index still
                // means the same vertex.
                var why = new Dictionary<string, int>(StringComparer.Ordinal);
                var srcPre = SecondSkinWriter.Parse(mdl);
                var pre = HatCompatSolve.ReadLod0Meshes(mdl);
                var ff = HatCompatSolve.FrameAndFloor(mdl, head);
                var tailV = new HashSet<(int, int)>();
                foreach (var st in HatCompatSolve.Strands(mdl, parts, head).Where(st => st.Tail))
                    foreach (var vv in HatCompatSolve.VerticesOf(mdl, srcPre, st.Part))
                        tailV.Add((st.Part.Mesh, vv));
                var frozenV = FrozenIn(mdl, srcPre, pre, hatLineY);

                int outside = 0, seen = 0, outSide2 = 0, seen2 = 0;
                foreach (var mv in meshes)
                {
                    int mo = src.MeshStart + mv.Mesh * 36;
                    ushort subIdx = BitConverter.ToUInt16(patched, mo + 10);
                    ushort subCount = BitConverter.ToUInt16(patched, mo + 12);
                    for (int s = 0; s < subCount; s++)
                    {
                        int ss = src.SubmeshStart + (subIdx + s) * 16;
                        uint io = BitConverter.ToUInt32(patched, ss), ic = BitConverter.ToUInt32(patched, ss + 4);
                        // Tagged to vanish under a hat: the game does not draw it, so neither does this.
                        if (kamBit >= 0 && (BitConverter.ToUInt32(patched, ss + 8) & (1u << kamBit)) != 0) continue;

                        for (uint k = 0; k < ic; k++)
                        {
                            int at = src.Ib + (int)(io + k) * 2;
                            if (at + 2 > patched.Length) break;
                            int v = BitConverter.ToUInt16(patched, at);
                            bool pressed = swap.TryGetValue(io + k, out var rep);
                            if (pressed) v = rep;
                            if (v >= mv.Positions.Length) continue;

                            // Also the band BELOW the line, which the press never touches. Hair there with
                            // nothing over it is hair standing up through the brim — invisible to every
                            // measurement so far, all of which stop at the hat line.
                            float rel = mv.Positions[v].Y - hatLineY;
                            if (rel < 0f && rel > -0.08f
                             && FirstHit(hat, mv.Positions[v], Vector3.UnitY) <= 0f) belowBandMiss++;

                            if (mv.Positions[v].Y >= hatLineY)
                            {
                                upSeen++;
                                if (FirstHit(hat, mv.Positions[v], Vector3.UnitY) <= 0f)
                                {
                                    upMiss++;
                                    if (pressed) upMissPressed++;
                                    else why[Gate(v)] = why.GetValueOrDefault(Gate(v)) + 1;

                                    string Gate(int vv)
                                    {
                                        var pos = pre.FirstOrDefault(m => m.Mesh == mv.Mesh)?.Positions;
                                        if (pos == null || vv >= pos.Length) return "a spare, or a mesh not read";
                                        if (tailV.Contains((mv.Mesh, vv))) return "tail, but its submesh is not tagged";
                                        if (frozenV.Contains((mv.Mesh, vv))) return "frozen by a straddling triangle";
                                        var dd = pos[vv] - solve.Centre;
                                        float ll = dd.Length();
                                        if (ll > 1e-5f && ff != null
                                         && ll <= ff.Value.Floor[HatCompatSolve.BinFor(dd / ll)])
                                            return "judged already inside the head";
                                        return "eligible but never moved";
                                    }
                                }
                            }

                            var d = mv.Positions[v] - solve.Centre;
                            float len = d.Length();
                            if (len < 1e-5f) continue;
                            var dir = d / len;
                            // Two views: up through the crown, and out through the brim as well. A strand
                            // standing off the BACK of the head leaves along a near-level ray, so the crown
                            // view alone reports it as fitting perfectly — which it did, while two of them
                            // were plainly visible in game.
                            if (dir.Y < -0.1f) continue;
                            float tHat = FirstHit(hat, solve.Centre, dir);
                            if (tHat <= 0f) continue;
                            seen2++;
                            if (len > tHat) outSide2++;
                            if (dir.Y < 0.25f) continue;
                            seen++;
                            if (len > tHat) outside++;
                        }
                    }
                }
                o.WriteLine($"    {hatName,-28} crown {outside,5}/{seen,6}   crown+sides {outSide2,5}/{seen2,6}"
                          + $"   nothing overhead {upMiss,5}/{upSeen,6} ({upMissPressed} pressed)"
                          + $"   in the 80mm below the line {belowBandMiss,5}");
                foreach (var (reason, n) in why.OrderByDescending(kv => kv.Value))
                    o.WriteLine($"        {n,5} × {reason}");
            }
        }
    }

    /// <summary>
    /// How much freeze slack does it take to stop leaving strands on top of the brim, and what does it cost?
    /// <para/>
    /// The press pins the corners of any triangle reaching below the hat line, because pressing one drags a
    /// ribbon out of it. Pinning is what left two strands lying across a Wrangler's brim: their triangles
    /// crossed the line, so nothing about them could move. Slack buys those strands back at the price of a
    /// ribbon no taller than the slack itself.
    /// <para/>
    /// Two numbers per setting, and they pull opposite ways: hair with NOTHING OVERHEAD is the defect being
    /// chased, and hair pressed BELOW THE LINE is the ribbon being paid for it.
    /// </summary>
    [Fact]
    public void HowMuchFreezeSlackDoesTheBrimNeed()
    {
        var hats = Hats().Where(h => h.Name.Contains("Wrangler", StringComparison.OrdinalIgnoreCase)
                                  || h.Name.Contains("Battlemage", StringComparison.OrdinalIgnoreCase)).ToList();
        if (hats.Count == 0) return;
        var head = HeadModel();
        var files = HairModels()
            .Where(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal))
            .Select(f => Pristine(f) ?? f)
            .Where(f => f.Contains("Locksley", StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (files.Length == 0) return;

        o.WriteLine($"{"fan",6} {"hat",-22} {"overhead-free",14} {"pressed below line",19} {"pressed",8}");
        foreach (float slack in new[] { 0f, 0.020f, 0.040f, 0.080f, 0.120f })
        {
            foreach (var f in files)
            {
                var mdl = File.ReadAllBytes(f);
                var parts = ModelPartReader.Read(mdl);
                if (parts == null) continue;
                HatCompatSolve.Result solve;
                try { solve = HatCompatSolve.Solve(mdl, parts, head, fan: slack); } catch { continue; }

                float hatLineY = solve.Centre.Y + HatCompatSolve.HatLine;
                var meshes = HatCompatSolve.ReadLod0Meshes(mdl);

                // The ribbon's price: how far below the line the press is now willing to drag geometry. Not
                // vertices it MOVED — it never moves one below the line — but the reach of the triangles it
                // is now willing to distort, which is what actually shows.
                float worstDip = 0f;
                foreach (var mv in meshes)
                {
                    if (!solve.Moved.TryGetValue(mv.Mesh, out var here)) continue;
                    foreach (var v in here.Keys)
                        if (v < mv.Positions.Length)
                            worstDip = MathF.Max(worstDip, hatLineY - LowestNeighbourY(mdl, mv, v, hatLineY));
                }

                foreach (var (hatName, hatPath) in hats)
                {
                    var hat = FbxMesh.Load(hatPath);
                    if (hat == null) continue;

                    var moved = solve.Moved.TryGetValue(0, out _) ? solve.Moved : solve.Moved;
                    // Everything from 120 mm below the line upward, so the fan's own working range is inside
                    // the window. Counting only above the line — as every earlier metric did — cannot see
                    // the hair the fan exists to reach.
                    int miss = 0, seen = 0;
                    foreach (var mv in meshes)
                    {
                        moved.TryGetValue(mv.Mesh, out var here);
                        for (int v = 0; v < mv.Positions.Length; v++)
                        {
                            var p = here != null && here.TryGetValue(v, out var to) ? to : mv.Positions[v];
                            if (p.Y < hatLineY - 0.12f) continue;
                            seen++;
                            if (FirstHit(hat, p, Vector3.UnitY) <= 0f) miss++;
                        }
                    }
                    o.WriteLine($"{slack * 1000,4:F0}mm {hatName,-22} {miss,6}/{seen,-7} "
                              + $"{worstDip * 1000,17:F0}mm {solve.Considered,8}   {Trim(f)}");
                }
            }
        }
    }

    /// <summary>The lowest corner of any triangle this vertex belongs to — how far a press on it can reach.</summary>
    private static float LowestNeighbourY(byte[] mdl, HatCompatSolve.MeshVerts mv, int vertex, float cap)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int mo = src.MeshStart + mv.Mesh * 36;
        uint ic = BitConverter.ToUInt32(mdl, mo + 4), start = BitConverter.ToUInt32(mdl, mo + 16);
        float lowest = cap;
        for (uint t = 0; t + 3 <= ic; t += 3)
        {
            int a = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t) * 2);
            int b = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t + 1) * 2);
            int c = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t + 2) * 2);
            if (a != vertex && b != vertex && c != vertex) continue;
            foreach (var k in new[] { a, b, c })
                if (k < mv.Positions.Length) lowest = MathF.Min(lowest, mv.Positions[k].Y);
        }
        return lowest;
    }

    /// <summary>
    /// Every LM hairstyle installed, put through the whole pipeline and judged on the result.
    /// <para/>
    /// A regression net rather than an investigation. Everything else here was written to chase one defect
    /// on one hairstyle, and each of those chases changed a rule that applies to all of them — the press
    /// reaching below the hat line, hiding narrowed to tails that meet a hat, the budget re-ordered. This
    /// runs the author's own file through patch and read-back and reports, per hairstyle, the three things
    /// that can go wrong: hair still standing through the hat, hair hidden that should not be, and the
    /// format quietly running out of room.
    /// <para/>
    /// Sampled every fourth vertex. The ray cast is linear in the hat's triangles and this is a sweep over
    /// dozens of models; a quarter of sixty thousand vertices is still thousands of samples per hairstyle,
    /// which is ample for a number that only has to catch a hairstyle behaving differently from its peers.
    /// </summary>
    [Fact]
    public void SweepEveryLMHairThroughTheCurrentDesign()
    {
        var hats = Hats().Where(h => h.Name.Contains("Wrangler", StringComparison.OrdinalIgnoreCase)
                                  || h.Name.Contains("Battlemage", StringComparison.OrdinalIgnoreCase))
                         .Select(h => (h.Name, Mesh: FbxMesh.Load(h.Path)))
                         .Where(h => h.Mesh != null).ToList();
        if (hats.Count == 0) return;
        var head = HeadModel();

        var files = HairModels()
            .Where(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal))
            .Select(f => Pristine(f) ?? f)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => f.Contains(Path.DirectorySeparatorChar + "LM ", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("LM_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;

        o.WriteLine($"{"hairstyle",-38} {"press",6} {"drop",5} {"stuck",5} "
                  + "parts tails hide kept  through the hat");
        var trouble = new List<string>();

        foreach (var f in files)
        {
            var mdl = File.ReadAllBytes(f);
            ModelParts? parts;
            HatCompatSolve.Result solve;
            try
            {
                parts = ModelPartReader.Read(mdl);
                if (parts == null) { trouble.Add($"{Trim(f)}: unreadable"); continue; }
                solve = HatCompatSolve.Solve(mdl, parts, head);
            }
            catch (Exception ex) { trouble.Add($"{Trim(f)}: solve threw {ex.GetType().Name}"); continue; }

            byte[] patched;
            int stuck = 0;
            try
            {
                // Exactly what HatCompatService.Apply tags, which is the CUT and nothing else — hiding
                // ponytails is withdrawn, so solve.Cut is computed and not applied. Tagging Hide here
                // instead measured a patch the plugin does not produce.
                var tag = solve.Cut;
                var (split, targets) = ModelAttributeWriter.IsolateParts(mdl, tag);
                patched = tag.Count > 0
                    ? ModelAttributeWriter.AddAttribute(split, "atr_kam", targets) : split;
                if (solve.Moved.Count > 0)
                    patched = ModelAttributeWriter.AddShape(patched, HatShape, solve.Moved, out stuck);
            }
            catch (Exception ex) { trouble.Add($"{Trim(f)}: {ex.Message}"); continue; }

            var strands = HatCompatSolve.Strands(mdl, parts, head).Where(s => s.Tail).ToList();
            int hidTris = solve.Cut.Sum(p => p.TriangleCount);
            int allTris = parts.Parts.Where(p => p.Island < 0).Sum(p => p.TriangleCount);
            double hidPct = allTris > 0 ? 100.0 * hidTris / allTris : 0;

            var src = SecondSkinWriter.Parse(patched);
            int kamBit = Array.IndexOf(src.AttrNames, "atr_kam");
            var meshes = HatCompatSolve.ReadLod0Meshes(patched);
            // THE HAT LINE, and nothing lower. Below it hair is supposed to be visible — that is the whole
            // point of the fade — so counting it as "through the hat" measures the hairstyle, not the fit.
            // Two earlier windows got this wrong in both directions: the fan's own bottom moved whenever the
            // constants moved, flattering any change that shortened it, and a fixed 100 mm below centre swept
            // in hair hanging past the jaw that no hat was ever going to cover. What the cut promises is that
            // nothing DRAWN remains above the line, so that is what to check.
            float fanBottom = solve.Centre.Y + HatCompatSolve.HatLine;

            var swap = new Dictionary<uint, ushort>();
            if (src.Shapes.TryGetValue(HatShape, out var entries))
                foreach (var e in entries)
                    foreach (var (b, rep) in e.Values) swap[e.MeshIndexOffset + b] = rep;

            // Which vertices the game actually draws, after the shape and after the tagged parts go.
            var drawn = new HashSet<(int, int)>();
            foreach (var mv in meshes)
            {
                int mo = src.MeshStart + mv.Mesh * 36;
                ushort subIdx = BitConverter.ToUInt16(patched, mo + 10);
                ushort subCount = BitConverter.ToUInt16(patched, mo + 12);
                for (int s = 0; s < subCount; s++)
                {
                    int ss = src.SubmeshStart + (subIdx + s) * 16;
                    if (kamBit >= 0 && (BitConverter.ToUInt32(patched, ss + 8) & (1u << kamBit)) != 0) continue;
                    uint io = BitConverter.ToUInt32(patched, ss), ic = BitConverter.ToUInt32(patched, ss + 4);
                    for (uint k = 0; k < ic; k += 4)
                    {
                        int at = src.Ib + (int)(io + k) * 2;
                        if (at + 2 > patched.Length) break;
                        int v = BitConverter.ToUInt16(patched, at);
                        if (swap.TryGetValue(io + k, out var rep)) v = rep;
                        if (v < mv.Positions.Length) drawn.Add((mv.Mesh, v));
                    }
                }
            }

            var report = new List<string>();
            foreach (var (hatName, hat) in hats)
            {
                int miss = 0;
                foreach (var (mesh, v) in drawn)
                {
                    var p = meshes.First(m => m.Mesh == mesh).Positions[v];
                    if (p.Y < fanBottom) continue;
                    if (FirstHit(hat!, p, Vector3.UnitY) <= 0f) miss++;
                }
                report.Add($"{hatName.Split('\'')[0]} {miss}");
                if (miss > 0) trouble.Add($"{Trim(f)}: {miss} sampled vertices stand through {hatName}");
            }

            if (hidPct > 60) trouble.Add($"{Trim(f)}: hides {hidPct:F0}% of its triangles");
            if (solve.Dropped > 0 || stuck > 0)
                trouble.Add($"{Trim(f)}: {solve.Dropped} dropped for budget, {stuck} unaddressable");

            var all = HatCompatSolve.Strands(mdl, parts, head);
            if (strands.Count == 0 && all.Count > 0)
            {
                // No tails at all on a hairstyle that clearly has long hair: say what the two tests saw, so
                // the reason is a number rather than a guess.
                var byDrop = all.OrderByDescending(s => s.Drop).First();
                var byReach = all.OrderByDescending(s => s.Reach).First();
                int passDrop = all.Count(s => s.Drop > HatCompatSolve.TailDrop);
                int passReach = all.Count(s => s.Reach > HatCompatSolve.TailReach);
                o.WriteLine($"    !! {Trim(f)}: no tails. deepest drop {byDrop.Drop * 1000:F0} mm "
                          + $"(needs >{HatCompatSolve.TailDrop * 1000:F0}), furthest reach "
                          + $"{byReach.Reach * 1000:F0} mm (needs >{HatCompatSolve.TailReach * 1000:F0}); "
                          + $"{passDrop} parts pass drop, {passReach} pass reach");
            }
            o.WriteLine($"{Trim(f),-38} {solve.Considered,6} {solve.Dropped,5} {stuck,5} "
                      + $"{all.Count,4}p {strands.Count,4}t {solve.Cut.Count,4}h "
                      + $"{strands.Count(s => !s.Hideable),4}k  {string.Join("  ", report)}");
        }

        o.WriteLine(trouble.Count == 0
            ? $"\nAll {files.Length} LM hairstyles clean."
            : $"\n{trouble.Count} thing(s) to look at across {files.Length} LM hairstyles:");
        foreach (var t in trouble) o.WriteLine($"  {t}");
    }

    /// <summary>
    /// What is being hidden, and does any of it actually meet a hat?
    /// <para/>
    /// Hiding is the one thing here that destroys hair rather than moving it, so the bar for it should be
    /// that a strand cannot be dealt with any other way. A ponytail whose every vertex hangs BELOW the hat
    /// line meets no hat at all — it falls past the brim, in front of nothing — so hiding it removes hair
    /// the wearer chose and a hat was never going to touch.
    /// </summary>
    [Fact]
    public void IsAnythingBeingHiddenThatNoHatWouldTouch()
    {
        var head = HeadModel();
        var files = HairModels()
            .Where(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal))
            .Where(f => f.Contains("Maria", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("Locksley", StringComparison.OrdinalIgnoreCase))
            .Select(f => Pristine(f) ?? f)
            .Distinct().ToArray();
        if (files.Length == 0) return;

        foreach (var f in files)
        {
            var mdl = File.ReadAllBytes(f);
            var parts = ModelPartReader.Read(mdl);
            if (parts == null) continue;
            if (HatCompatSolve.FrameAndFloor(mdl, head) is not { } frame) continue;
            float hatLineY = frame.Centre.Y + HatCompatSolve.HatLine;

            var src = SecondSkinWriter.Parse(mdl);
            var verts = HatCompatSolve.ReadLod0Meshes(mdl).ToDictionary(m => m.Mesh, m => m.Positions);

            int tails = 0, hidden = 0, tailVerts = 0, keptVerts = 0;
            float highestKept = float.MinValue;
            foreach (var s in HatCompatSolve.Strands(mdl, parts, head).Where(s => s.Tail))
            {
                if (!verts.TryGetValue(s.Part.Mesh, out var pos)) continue;
                tails++;
                float top = float.MinValue;
                int n = 0;
                foreach (var v in HatCompatSolve.VerticesOf(mdl, src, s.Part))
                    if (v < pos.Length) { top = MathF.Max(top, pos[v].Y); n++; }
                tailVerts += n;

                if (s.Hideable) { hidden++; continue; }
                keptVerts += n;
                highestKept = MathF.Max(highestKept, top);
            }

            o.WriteLine($"{Trim(f)}: {tails} tail strand(s), {tailVerts} vertices — {hidden} hidden, "
                      + $"{tails - hidden} kept ({keptVerts} vertices)"
                      + (highestKept > float.MinValue
                          ? $", the highest kept one topping out {(hatLineY - highestKept) * 1000:F0} mm "
                          + "below the hat line" : ""));
        }
    }

    /// <summary>
    /// How low can the cut go before it shows?
    /// <para/>
    /// The hat line means something different now that geometry above it is DELETED rather than pressed. It
    /// used to be "how far down does a hat press hair against the head", and being generous with it cost
    /// nothing. It is now "how far down is hair certainly hidden", and every millimetre too low is a hole
    /// cut in hair the wearer can see.
    /// <para/>
    /// So measure that directly, and from the head rather than from the hat: walk the scalp, cast straight
    /// UP from each point, and ask whether any hat is between it and the sky. The lowest scalp height that
    /// still answers yes is the lowest a cut may go on that hat. Anything below it is in view.
    /// </summary>
    [Fact]
    public void HowLowCanTheCutGoBeforeItShows()
    {
        var head = HeadModel();
        var hats = Hats();
        if (head == null || hats.Count == 0) return;
        if (HatCompatSolve.FrameAndFloor(head, head) is not { } f) return;

        var scalp = HatCompatSolve.ReadLod0Meshes(head)
            .SelectMany(m => m.Positions)
            .Where(p => (p - f.Centre).Length() > f.Radius * 0.8f)     // the cranium, not the mouth's inside
            .ToArray();
        if (scalp.Length == 0) return;

        o.WriteLine($"head centre y {f.Centre.Y:F4};  covered = a hat is directly overhead");
        o.WriteLine($"{"hat",-26} {"lowest covered scalp point",28} {"as an offset from centre",26}");
        float worst = float.MinValue;
        foreach (var (name, path) in hats)
        {
            var hat = FbxMesh.Load(path);
            if (hat == null) continue;

            // The lowest point that is still covered, and the highest that is not: a hat whose coverage is
            // not a clean horizontal band would show these overlapping, which is worth knowing.
            // Straight up is the wrong question and answered yes everywhere: a brim covers the whole cranium
            // from directly overhead, so by that test a cut could go anywhere. What exposes a cut edge is
            // being LOOKED AT, from around eye level and under the brim. So cast outward from each scalp
            // point along the directions a viewer occupies — level, a little above, a little below — and
            // call the point hidden only if the hat blocks EVERY one of them.
            float lowestCovered = float.MaxValue, highestBare = float.MinValue;
            int covered = 0;
            foreach (var p in scalp)
            {
                var outward = new Vector3(p.X - f.Centre.X, 0f, p.Z - f.Centre.Z);
                if (outward.LengthSquared() < 1e-8f) continue;
                outward = Vector3.Normalize(outward);

                bool hidden = true;
                foreach (float el in new[] { -10f, 0f, 20f, 40f })
                {
                    float r = el * MathF.PI / 180f;
                    var dir = Vector3.Normalize(outward * MathF.Cos(r) + Vector3.UnitY * MathF.Sin(r));
                    if (FirstHit(hat, p, dir) <= 0f) { hidden = false; break; }
                }
                if (!hidden) { highestBare = MathF.Max(highestBare, p.Y); continue; }
                covered++;
                lowestCovered = MathF.Min(lowestCovered, p.Y);
            }
            if (covered == 0) { o.WriteLine($"{name,-26} {"never overhead",28}"); continue; }

            // The safe line is the highest BARE point, not the lowest covered one: below that height the
            // hat has stopped covering somewhere, whatever it still does elsewhere.
            float safe = highestBare > float.MinValue ? highestBare : lowestCovered;
            worst = MathF.Max(worst, safe - f.Centre.Y);
            o.WriteLine($"{name,-26} {(lowestCovered - f.Centre.Y) * 1000,20:F0} mm "
                      + $"   highest bare {(safe - f.Centre.Y) * 1000,6:F0} mm   ({covered}/{scalp.Length} covered)");
        }
        o.WriteLine($"\nleast generous hat wants the cut at or above {worst * 1000:F0} mm; "
                  + $"HatLine is {HatCompatSolve.HatLine * 1000:F0} mm");
    }

    /// <summary>
    /// Do hats actually grip the head along one consistent line?
    /// <para/>
    /// The premise behind a much better shape than the one built here: if every hat hugs the skull at the
    /// same height, then above that height a hat is CLAMPED to the head and hair there can go anywhere at
    /// all — deleted, even — while below it the hat lifts away and the hair must simply be left alone. The
    /// current design has no such line. It has a single hat-line height taken from the worst hat measured,
    /// and above it presses everything the same distance towards the skull, which is why tuning it has been
    /// a running battle between hair standing proud and hair squashed where a hat does not reach.
    /// <para/>
    /// Measured per DIRECTION, not per height, because that is the only way to tell a hat gripping a skull
    /// from a brim sailing past it: cast out from the head centre, take the hat's outermost surface and the
    /// head's own, and the difference is the gap between hat and scalp along that line. Then, for each ring
    /// of latitude, report the gap. A grip line shows up as a latitude where the gap collapses to near zero
    /// across most of the compass, and it is only real if every hat picks the same one.
    /// </summary>
    [Fact]
    public void DoHatsGripTheHeadAtOneConsistentLine()
    {
        var hats = Hats();
        var head = HeadModel();
        if (hats.Count == 0 || head == null) return;

        var frame = HatCompatSolve.FrameAndFloor(head, head);
        if (frame is not { } f) return;
        var (centre, radius, _) = f;
        var headMesh = HatCompatSolve.ReadLod0Meshes(head);
        o.WriteLine($"head centre {F(centre)} r {radius:F4};  gap = hat surface − head surface, in mm");
        o.WriteLine($"{"hat",-26} {"+60°",7} {"+45°",7} {"+30°",7} {"+15°",7} {"0°",7} {"-15°",7} {"-30°",7}");

        // Rings of latitude, sampled all the way round. Only directions where BOTH surfaces answer count —
        // a ray that misses the hat says nothing about how tightly that hat fits.
        float[] lats = [60f, 45f, 30f, 15f, 0f, -15f, -30f];
        foreach (var (name, path) in hats)
        {
            var hat = FbxMesh.Load(path);
            if (hat == null) continue;

            var cells = new List<string>();
            foreach (var lat in lats)
            {
                var gaps = new List<float>();
                for (int a = 0; a < 48; a++)
                {
                    float az = a * MathF.PI * 2f / 48f, el = lat * MathF.PI / 180f;
                    var dir = Vector3.Normalize(new Vector3(
                        MathF.Cos(el) * MathF.Cos(az), MathF.Sin(el), MathF.Cos(el) * MathF.Sin(az)));
                    float tHat = FirstHit(hat, centre, dir);
                    if (tHat <= 0f) continue;
                    float tHead = FarthestVertexAlong(headMesh, centre, dir);
                    if (tHead <= 0f) continue;
                    gaps.Add(tHat - tHead);
                }
                if (gaps.Count == 0) { cells.Add($"{"-",7}"); continue; }
                gaps.Sort();
                cells.Add($"{gaps[gaps.Count / 2] * 1000,6:F0} ");
            }
            o.WriteLine($"{name,-26} {string.Concat(cells)}");
        }
    }

    /// <summary>
    /// How far the head reaches along one direction, as the farthest vertex within a narrow cone about it.
    /// <para/>
    /// A cone rather than a ray-triangle hit, because a head model is not a closed shell in every direction
    /// and a ray can slip between its triangles. Farthest, not nearest, for the reason
    /// <see cref="FirstHit"/> gives: the nearest surface is a lash or the inside of a mouth.
    /// </summary>
    private static float FarthestVertexAlong(
        IReadOnlyList<HatCompatSolve.MeshVerts> head, Vector3 centre, Vector3 dir)
    {
        float best = 0f;
        foreach (var mv in head)
            foreach (var p in mv.Positions)
            {
                var d = p - centre;
                float len = d.Length();
                if (len < 1e-5f) continue;
                if (Vector3.Dot(d / len, dir) < 0.985f) continue;    // within ~10°
                if (len > best) best = len;
            }
        return best;
    }

    /// <summary>
    /// The backup Proteus took of this model before patching it, if there is one — the author's own bytes.
    /// <para/>
    /// Walks up to the mod root and looks for the same relative path under the backup folder, which is how
    /// <see cref="HatCompatService"/> stores it.
    /// </summary>
    private static string? Pristine(string file)
    {
        try
        {
            var full = Path.GetFullPath(file);
            if (!HatCompatService.InMods(full, Mods, out var modRoot, out var rel)) return null;
            var backup = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir,
                                      HatCompatService.BackupSubdir,
                                      rel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(backup) ? backup : null;
        }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The press's own freeze rule, recomputed: every corner of a triangle reaching below the line.</summary>
    private static HashSet<(int, int)> FrozenIn(
        byte[] mdl, SecondSkinWriter.Source src, IReadOnlyList<HatCompatSolve.MeshVerts> meshes, float hatLine)
    {
        var frozen = new HashSet<(int, int)>();
        foreach (var mv in meshes)
        {
            int mo = src.MeshStart + mv.Mesh * 36;
            if (mo + 36 > mdl.Length) continue;
            uint ic = BitConverter.ToUInt32(mdl, mo + 4), start = BitConverter.ToUInt32(mdl, mo + 16);
            if ((long)src.Ib + (start + ic) * 2 > mdl.Length) continue;
            for (uint t = 0; t + 3 <= ic; t += 3)
            {
                int a = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t) * 2);
                int b = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t + 1) * 2);
                int c = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t + 2) * 2);
                bool low = Low(mv.Positions, a) || Low(mv.Positions, b) || Low(mv.Positions, c);
                if (!low) continue;
                frozen.Add((mv.Mesh, a));
                frozen.Add((mv.Mesh, b));
                frozen.Add((mv.Mesh, c));
            }
            bool Low(Vector3[] p, int v) => v >= p.Length || p[v].Y < hatLine - HatCompatSolve.FanBelow;
        }
        return frozen;
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

    /// <summary>
    /// Profile EVERY reference hat, and report the tightest of them.
    /// <para/>
    /// Everything about the press was calibrated against one hat, and hats are not one shape. A strand that
    /// tucks under a wide-brimmed Wrangler's sits outside a Battlemage's, which is taller and narrower —
    /// so the press flattened it where the second hat leaves it on show. What the constants have to
    /// describe is the WORST case across hats, which is the same advice Ulli's guide gives when it names
    /// two particular caps to check against.
    /// <para/>
    /// Two numbers per hat: where its opening sits on the head, and how much room its crown leaves over
    /// the scalp. The press must use the highest opening and the smallest clearance of any hat here.
    /// </summary>
    [Fact]
    public void ProfileEveryReferenceHat()
    {
        var hats = Hats();
        var head = HeadModel();
        if (hats.Count == 0 || head == null) return;
        var frame = HatCompatSolve.HeadFrameFrom(head);
        if (frame == null) return;
        var (centre, radius) = frame.Value;
        o.WriteLine($"head centre {F(centre)}  radius {radius:F4}");
        o.WriteLine($"{"hat",-28} {"verts",6} {"band y",8} {"vs centre",10} {"crown gap",10} {"min gap",9}");

        // The head as a ray target, so "how far off the scalp" is measured against the scalp itself rather
        // than a sphere — a head is nothing like one, and every gap below would inherit the error.
        if (!SecondSkinWriter.TryReadLod0Geometry(head, out var hp, out _, out var ht, keepMaterial: _ => true))
            return;
        var headMesh = new FbxMesh.Mesh(
            Enumerable.Range(0, hp.Length / 3).Select(i => new Vector3(hp[i * 3], hp[i * 3 + 1], hp[i * 3 + 2])).ToArray(),
            ht);

        float highestBand = float.MinValue, tightestCrown = float.MaxValue;
        string bandFrom = "", crownFrom = "";
        foreach (var (name, path) in hats)
        {
            var hat = FbxMesh.Load(path);
            if (hat == null) { o.WriteLine($"{name,-28} unreadable"); continue; }

            // Cast outward from the head centre over the whole sphere. Where a hat WRAPS the head, its
            // outer surface sits just past the scalp; where it flares into a brim it is far away and the
            // head is not covered there at all. That difference is what tells an opening from a brim, and
            // measuring the hat's own radius cannot see it — which is why the first attempt found the
            // pointed tip of a witch's hat and called it the band.
            float lowestCovered = float.MaxValue;
            var gaps = new List<float>();
            for (int iv = 1; iv < 40; iv++)
            for (int iu = 0; iu < 64; iu++)
            {
                float theta = MathF.PI * iv / 40f, phi = 2 * MathF.PI * iu / 64f;
                var dir = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta),
                                      MathF.Sin(theta) * MathF.Sin(phi));
                float tHead = FirstHit(headMesh, centre, dir);
                if (tHead <= 0f) continue;
                float tHat = FirstHit(hat, centre, dir);
                if (tHat <= 0f) continue;

                float gap = tHat - tHead;
                if (gap > 0.08f) continue;              // a brim out in the air, not the hat on the head
                gaps.Add(gap);
                lowestCovered = MathF.Min(lowestCovered, centre.Y + dir.Y * tHead);
            }
            if (gaps.Count == 0) { o.WriteLine($"{name,-28} {hat.Positions.Length,6}  covers nothing"); continue; }

            gaps.Sort();
            float band = lowestCovered - centre.Y;
            float tight = gaps[gaps.Count / 10];        // the tightest tenth, not the single worst texel
            o.WriteLine($"{name,-28} {hat.Positions.Length,6} {lowestCovered,8:F3} {band * 1000,9:F0}mm "
                      + $"{gaps[gaps.Count / 2] * 1000,9:F0}mm {tight * 1000,8:F0}mm");

            if (band > highestBand) { highestBand = band; bandFrom = name; }
            if (tight < tightestCrown) { tightestCrown = tight; crownFrom = name; }
        }

        o.WriteLine("");
        o.WriteLine($"WORST CASE: opening sits {highestBand * 1000:F0} mm above the head centre ({bandFrom}); "
                  + $"crown leaves {tightestCrown * 1000:F0} mm over the scalp ({crownFrom})");
        o.WriteLine($"press currently uses hat line {HatCompatSolve.HatLine * 1000:F0} mm, "
                  + $"allowed dip {HatCompatSolve.PressDip * 1000:F0} mm, tail from {HatCompatSolve.TailDrop * 1000:F0} mm drop and {HatCompatSolve.TailReach * 1000:F0} mm reach");
    }

    /// <summary>
    /// Run the WHOLE apply path over a real hairstyle and check the tails actually end up tagged.
    /// <para/>
    /// Solve, cut the tail strands out of the submeshes they share, tag them <c>atr_kam</c>, add the shape
    /// — exactly what pressing the button does — and then read the result back to see which triangles the
    /// game would drop under a hat. A classifier that finds the ponytail and a writer that fails to tag it
    /// look identical from the outside, and the only way to tell them apart is to look at the bytes.
    /// </summary>
    [Fact]
    public void TaggingTheTailsActuallyMarksThem()
    {
        var wanted = Environment.GetEnvironmentVariable("PROTEUS_HAIR") ?? "Locksley";
        var head = HeadModel();
        // Prefer a pristine copy: an already-patched file has nothing left to find.
        var file = HairModels().Concat(PristineBackups())
            .Where(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal))
            .Where(f => f.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => HatCompatService.IsHatCompatible(File.ReadAllBytes(f)) ? 1 : 0)
            .FirstOrDefault();
        if (file == null || head == null) return;

        var mdl = File.ReadAllBytes(file);
        o.WriteLine($"{Trim(file)}  (already patched: {HatCompatService.IsHatCompatible(mdl)})");
        var parts = ModelPartReader.Read(mdl);
        if (parts == null) return;

        var solve = HatCompatSolve.Solve(mdl, parts, head);
        o.WriteLine($"solve: {solve.Considered} vertices pressed, {solve.Cut.Count} tail strands to hide");
        if (solve.Cut.Count == 0) { o.WriteLine("nothing classified as a tail — nothing to check"); return; }

        var (split, targets) = ModelAttributeWriter.IsolateParts(mdl, solve.Cut);
        o.WriteLine($"isolate: {targets.Count} submeshes now hold exactly those strands");
        var tagged = ModelAttributeWriter.AddAttribute(split, HatCompatService.ScalpAttribute, targets);

        var src = SecondSkinWriter.Parse(tagged);
        int bit = Array.IndexOf(src.AttrNames, HatCompatService.ScalpAttribute);
        Assert.True(bit >= 0, "atr_kam is not in the attribute table");

        // How much geometry the game would actually drop, against how much the solve wanted dropped.
        long taggedTris = 0, allTris = 0;
        for (int m = 0; m < src.MeshCount; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > tagged.Length) break;
            int si = BitConverter.ToUInt16(tagged, mo + 10), sc = BitConverter.ToUInt16(tagged, mo + 12);
            for (int k = 0; k < sc; k++)
            {
                int ss = src.SubmeshStart + (si + k) * 16;
                uint count = BitConverter.ToUInt32(tagged, ss + 4) / 3;
                allTris += count;
                if ((BitConverter.ToUInt32(tagged, ss + 8) & (1u << bit)) != 0) taggedTris += count;
            }
        }
        // A part cut down to a chosen set of triangles carries its ordinals but no rebased Triangles array
        // — nothing draws it — so TriangleCount reads zero for those and the ordinals are the real count.
        long wantTris = solve.Cut.Sum(p => (long)(p.TriangleCount > 0 ? p.TriangleCount : p.Ordinals.Length));
        o.WriteLine($"tagged {taggedTris} of {allTris} triangles ({100.0 * taggedTris / allTris:F0}%); "
                  + $"the solve asked for {wantTris}");

        Assert.True(taggedTris > 0, "no triangle carries atr_kam");
        Assert.Equal(wantTris, taggedTris);
    }

    /// <summary>
    /// What is ACTUALLY in the patched file on disk right now.
    /// <para/>
    /// The offline pipeline can be perfect and the thing in the mod folder still be wrong — a different
    /// code path, an older build, a write that silently failed. When the game disagrees with the tests,
    /// this is the file the game is reading, so this is the one worth asking.
    /// </summary>
    [Fact]
    public void WhatIsInThePatchedFileOnDisk()
    {
        var wanted = Environment.GetEnvironmentVariable("PROTEUS_HAIR") ?? "Locksley";
        foreach (var f in HairModels()
                     .Where(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal))
                     .Where(f => f.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                     .Take(4))
        {
            var mdl = File.ReadAllBytes(f);
            SecondSkinWriter.Source src;
            try { src = SecondSkinWriter.Parse(mdl); }
            catch (Exception ex) { o.WriteLine($"{Trim(f)}: parse failed {ex.GetType().Name}"); continue; }

            o.WriteLine("");
            o.WriteLine($"{Trim(f)}  {new FileInfo(f).Length} bytes, written {new FileInfo(f).LastWriteTime:HH:mm:ss}");
            o.WriteLine($"  attributes: [{string.Join(", ", src.AttrNames)}]");
            o.WriteLine($"  shapes:     [{string.Join(", ", src.Shapes.Keys)}]  "
                      + $"(declares shp_hib: {ModelAttributeWriter.DeclaresShape(mdl, HatCompatService.HatShape)})");

            int bit = Array.IndexOf(src.AttrNames, HatCompatService.ScalpAttribute);
            if (bit < 0) { o.WriteLine("  atr_kam is NOT in this file"); continue; }

            long tagged = 0, all = 0;
            int taggedSubs = 0, subs = 0;
            for (int m = 0; m < src.MeshCount; m++)
            {
                int mo = src.MeshStart + m * 36;
                if (mo + 36 > mdl.Length) break;
                int si = BitConverter.ToUInt16(mdl, mo + 10), sc = BitConverter.ToUInt16(mdl, mo + 12);
                for (int k = 0; k < sc; k++)
                {
                    int ss = src.SubmeshStart + (si + k) * 16;
                    uint tris = BitConverter.ToUInt32(mdl, ss + 4) / 3;
                    all += tris;
                    subs++;
                    if ((BitConverter.ToUInt32(mdl, ss + 8) & (1u << bit)) == 0) continue;
                    tagged += tris;
                    taggedSubs++;
                }
            }
            o.WriteLine($"  atr_kam is bit {bit}, on {taggedSubs} of {subs} submeshes, "
                      + $"{tagged} of {all} triangles ({(all > 0 ? 100.0 * tagged / all : 0):F0}%)");

            // WHERE the tagged geometry is. A count that matches proves the right NUMBER of triangles was
            // tagged and nothing about which ones — and a ponytail still on show while a third of the model
            // carries the attribute means the attribute is on something else.
            var verts = HatCompatSolve.ReadLod0Meshes(mdl).ToDictionary(x => x.Mesh, x => x.Positions);
            var inTag = new List<Vector3>();
            var outTag = new List<Vector3>();
            for (int m = 0; m < src.MeshCount; m++)
            {
                int mo = src.MeshStart + m * 36;
                if (mo + 36 > mdl.Length || !verts.TryGetValue(m, out var pos)) continue;
                int si = BitConverter.ToUInt16(mdl, mo + 10), sc = BitConverter.ToUInt16(mdl, mo + 12);
                uint start = BitConverter.ToUInt32(mdl, mo + 16);
                for (int k = 0; k < sc; k++)
                {
                    int ss = src.SubmeshStart + (si + k) * 16;
                    uint off = BitConverter.ToUInt32(mdl, ss), cnt = BitConverter.ToUInt32(mdl, ss + 4);
                    bool on = (BitConverter.ToUInt32(mdl, ss + 8) & (1u << bit)) != 0;
                    for (uint i = 0; i < cnt; i += 33)      // sampled; only the extent is wanted
                    {
                        int at = src.Ib + (int)(off + i) * 2;
                        if (at + 2 > mdl.Length) break;
                        int v = BitConverter.ToUInt16(mdl, at);
                        if (v < pos.Length) (on ? inTag : outTag).Add(pos[v]);
                    }
                }
                _ = start;
            }
            void Extent(string what, List<Vector3> ps)
            {
                if (ps.Count == 0) { o.WriteLine($"    {what}: none"); return; }
                var lo = ps.Aggregate(Vector3.Min);
                var hi = ps.Aggregate(Vector3.Max);
                var mid = new Vector3(ps.Average(p => p.X), ps.Average(p => p.Y), ps.Average(p => p.Z));
                o.WriteLine($"    {what}: centroid {F(mid)}  y {lo.Y:F3}..{hi.Y:F3}  z {lo.Z:F3}..{hi.Z:F3}");
            }
            Extent("tagged  ", inTag);
            Extent("untagged", outTag);
        }
    }

    /// <summary>
    /// What each reference hat's EQP entry says about hiding hair.
    /// <para/>
    /// This is the question the whole hide feature turns on, and it is not answered by anything in the hair
    /// model. <c>atr_kam</c> marks WHICH part of a hairstyle is the scalp; whether that part is drawn is the
    /// HAT's decision, carried in its equipment parameters. A hat that asks for neither
    /// <c>HeadHideScalp</c> nor <c>HeadHideHair</c> shows every strand of hair no matter what the hair
    /// model is tagged with — which is exactly what a correctly tagged ponytail refusing to disappear
    /// looks like.
    /// <para/>
    /// Head entries are 3 bytes at byte 5 of each item's 8-byte EQP block, per Penumbra's
    /// <c>EqpEntry</c> and its <c>(offset, mask)</c> table for <c>EquipSlot.Head</c>.
    /// </summary>
    [Fact]
    public void WhatDoTheReferenceHatsSayAboutHidingHair()
    {
        var game = Environment.GetEnvironmentVariable("PROTEUS_GAME")
                ?? @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn";
        var sqpack = Path.Combine(game, "game", "sqpack");
        if (!Directory.Exists(sqpack)) { o.WriteLine($"no game data at {sqpack}"); return; }

        Lumina.GameData data;
        try { data = new Lumina.GameData(sqpack); }
        catch (Exception ex) { o.WriteLine($"could not open game data: {ex.Message}"); return; }

        var eqp = data.GetFile("chara/xls/equipmentparameter/equipmentparameter.eqp");
        if (eqp == null) { o.WriteLine("no equipmentparameter.eqp"); return; }
        var bytes = eqp.Data;
        o.WriteLine($"eqp: {bytes.Length} bytes");

        // Set ids come from the exported file names: c0201eNNNN_met.
        foreach (var (name, path) in Hats())
        {
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path), @"e(\d{4})_met");
            if (!m.Success) { o.WriteLine($"{name,-28} (no set id in the file name)"); continue; }
            int set = int.Parse(m.Groups[1].Value);

            // The EQP file is a block-compressed table; entry N lives at N*8 once expanded, and the plain
            // layout holds for the low set ids these hats use.
            int at = set * 8;
            if (at + 8 > bytes.Length) { o.WriteLine($"{name,-28} set {set}: past the end of the table"); continue; }
            ulong entry = BitConverter.ToUInt64(bytes, at);

            bool hideScalp = (entry & (0x02ul << 40)) != 0;
            bool hideHair = (entry & (0x04ul << 40)) != 0;
            bool showOverride = (entry & (0x08ul << 40)) != 0;
            o.WriteLine($"{name,-28} set {set,4}  0x{entry:X16}  "
                      + $"HideScalp={hideScalp,-5} HideHair={hideHair,-5} ShowHairOverride={showOverride}");
        }
    }

    /// <summary>
    /// WHERE do hand-made hat-compatible hairstyles put <c>atr_kam</c>?
    /// <para/>
    /// The guide says to tag the parts you want hidden — ponytails and such — with "scalp". Glamourer's own
    /// table describes the same attribute as "a part of a hairstyle denoted as the scalp", which is the
    /// opposite reading: the part that STAYS. Both cannot be right, and a ponytail correctly tagged and
    /// still on show under a hat that asks for HideScalp says one of them is wrong.
    /// <para/>
    /// Seventy-six installed hairstyles were made hat-compatible by hand. Where their authors put the
    /// attribute settles it: near the skull means it marks the scalp, out where a tail hangs means it marks
    /// what disappears.
    /// </summary>
    [Fact]
    public void WhereDoWorkingModsPutTheScalpAttribute()
    {
        var head = HeadModel();
        var frame = head == null ? null : HatCompatSolve.HeadFrameFrom(head);
        if (frame == null) return;
        var centre = frame.Value.Centre;

        int nearScalp = 0, outFar = 0;
        o.WriteLine($"{"hair",-34} {"tagged%",8} {"tagged centroid",-26} {"reach mm",9}");
        foreach (var f in HairModels().Where(f => !f.Contains("hatcompat-backup", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] mdl;
            try { mdl = File.ReadAllBytes(f); } catch (IOException) { continue; }
            if (!System.Text.Encoding.ASCII.GetString(mdl).Contains(HatShape, StringComparison.Ordinal)) continue;

            SecondSkinWriter.Source src;
            try { src = SecondSkinWriter.Parse(mdl); } catch { continue; }
            int bit = Array.IndexOf(src.AttrNames, ScalpAttr);
            if (bit < 0) continue;

            var verts = HatCompatSolve.ReadLod0Meshes(mdl).ToDictionary(x => x.Mesh, x => x.Positions);
            var tagged = new List<Vector3>();
            long tagTris = 0, allTris = 0;
            for (int m = 0; m < src.MeshCount; m++)
            {
                int mo = src.MeshStart + m * 36;
                if (mo + 36 > mdl.Length || !verts.TryGetValue(m, out var pos)) continue;
                int si = BitConverter.ToUInt16(mdl, mo + 10), sc = BitConverter.ToUInt16(mdl, mo + 12);
                for (int k = 0; k < sc; k++)
                {
                    int ss = src.SubmeshStart + (si + k) * 16;
                    uint off = BitConverter.ToUInt32(mdl, ss), cnt = BitConverter.ToUInt32(mdl, ss + 4);
                    allTris += cnt / 3;
                    if ((BitConverter.ToUInt32(mdl, ss + 8) & (1u << bit)) == 0) continue;
                    tagTris += cnt / 3;
                    for (uint i = 0; i < cnt; i += 21)
                    {
                        int at = src.Ib + (int)(off + i) * 2;
                        if (at + 2 > mdl.Length) break;
                        int v = BitConverter.ToUInt16(mdl, at);
                        if (v < pos.Length) tagged.Add(pos[v]);
                    }
                }
            }
            if (tagged.Count == 0 || allTris == 0) continue;

            var mid = new Vector3(tagged.Average(p => p.X), tagged.Average(p => p.Y), tagged.Average(p => p.Z));
            float reach = tagged.Max(p => (p - centre).Length());
            if (reach < 0.20f) nearScalp++; else outFar++;
            o.WriteLine($"{Trim(f),-34} {100.0 * tagTris / allTris,7:F0}% {F(mid),-26} {reach * 1000,9:F0}");
        }
        o.WriteLine("");
        o.WriteLine($"{nearScalp} hairstyles tag geometry that hugs the head (reach < 200 mm), "
                  + $"{outFar} tag geometry that hangs out beyond it");
    }

    /// <summary>Backup copies Proteus took before patching — the author's original bytes.</summary>
    private static IEnumerable<string> PristineBackups()
    {
        if (!Directory.Exists(Mods)) return [];
        try
        {
            return Directory.GetFiles(Mods, "*_hir.mdl", SearchOption.AllDirectories)
                .Where(f => f.Contains(HatCompatService.BackupSubdir, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
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

            int hideTris = solve.Cut.Sum(p => p.TriangleCount);
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

    /// <summary>
    /// Per connected strand: how much of it hangs below the hat line, and how far down it reaches.
    /// <para/>
    /// The press crushes everything above the hat line and nothing below it, which is right for a scalp and
    /// wrong for a ponytail — a tail's ROOT is above the line, so it gets driven into the skull while its
    /// length stays where it was, and it splays out into a flat sheet behind the hat. Leaving whole strands
    /// alone is the fix, but only if a scalp and a tail can actually be told apart. This prints the numbers
    /// to choose that test from, rather than guessing a threshold and finding out in game.
    /// </summary>
    [Fact]
    public void HowFarDoesEachStrandHangBelowTheHatLine()
    {
        var head = HeadModel();
        var wanted = Environment.GetEnvironmentVariable("PROTEUS_HAIR") ?? "Locksley";
        var files = HairModels()
            .Where(f => Path.GetFileName(f).StartsWith("c0201h", StringComparison.Ordinal))
            .Where(f => Trim(f).Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0 || head == null) return;

        var frame = HatCompatSolve.HeadFrameFrom(head);
        if (frame == null) return;
        float hatLine = frame.Value.Centre.Y + HatCompatSolve.HatLine;
        o.WriteLine($"head centre y {frame.Value.Centre.Y:F4}, hat line {hatLine:F4}");

        foreach (var f in files.Take(3))
        {
            var mdl = File.ReadAllBytes(f);
            var parts = ModelPartReader.Read(mdl);
            if (parts == null) continue;
            var src = SecondSkinWriter.Parse(mdl);
            var verts = HatCompatSolve.ReadLod0Meshes(mdl).ToDictionary(m => m.Mesh, m => m.Positions);

            o.WriteLine("");
            o.WriteLine(Trim(f));
            o.WriteLine($"  {"part",-10} {"verts",6} {"below%",7} {"drop mm",8} {"above",8} {"standoff",11}");

            // Islands where a submesh has them, the whole submesh where it does not — the same granularity
            // a press gate would work at.
            // Straight from the classifier the press uses, so these numbers cannot drift from the ones the
            // gate is actually deciding on.
            var strands = HatCompatSolve.Strands(mdl, parts, head);
            int covered = strands.Count(s => s.Covered);
            // Strand COUNT is a poor measure once submeshes have been split — what matters is how much
            // geometry each side of the line accounts for.
            int vAll = strands.Sum(s => s.Verts), vCov = strands.Where(s => s.Covered).Sum(s => s.Verts);
            int vAbove = strands.Sum(s => s.Verts - s.Below);
            o.WriteLine($"  {strands.Count} strands, {covered} covered and pressed, "
                      + $"{strands.Count - covered} left alone, {strands.Count(s => s.Tail)} tails offered for hiding");
            o.WriteLine($"  vertices: {vAll} total, {vAbove} above the hat line, {vCov} in pressed strands "
                      + $"({(vAbove > 0 ? 100.0 * vCov / vAbove : 0):F0}% of what sits above the line)");
            // How much of the hairstyle a given cut would hide. The visible ponytail is a contiguous mass,
            // so the threshold that catches it should show up as a plateau — a range where moving the cut
            // changes the share very little, because there is nothing there to catch.
            o.WriteLine("  if a strand were hidden when it hangs more than X below the hat line:");
            foreach (var t in new[] { .10f, .15f, .20f, .25f, .30f, .35f, .40f, .45f, .50f, .60f })
            {
                var hit = strands.Where(s => s.Drop > t).ToList();
                o.WriteLine($"    drop > {t * 1000,4:F0} mm: {hit.Count,4} strands, "
                          + $"{hit.Sum(s => s.Part.TriangleCount),7} tris "
                          + $"({100.0 * hit.Sum(s => s.Part.TriangleCount) / Math.Max(1, strands.Sum(s => s.Part.TriangleCount)),5:F1}%), "
                          + $"reach {(hit.Count > 0 ? hit.Min(s => s.Reach) * 1000 : 0),5:F0}"
                          + $"-{(hit.Count > 0 ? hit.Max(s => s.Reach) * 1000 : 0),5:F0} mm");
            }

            o.WriteLine("  if a strand were hidden when it reaches more than X off the scalp:");
            foreach (var t in new[] { .10f, .15f, .20f, .25f, .30f, .35f, .40f, .45f, .50f, .60f })
            {
                var hit = strands.Where(s => s.Reach > t).ToList();
                o.WriteLine($"    reach > {t * 1000,4:F0} mm: {hit.Count,4} strands, "
                          + $"{hit.Sum(s => s.Part.TriangleCount),7} tris "
                          + $"({100.0 * hit.Sum(s => s.Part.TriangleCount) / Math.Max(1, strands.Sum(s => s.Part.TriangleCount)),5:F1}%), "
                          + $"drop {(hit.Count > 0 ? hit.Min(s => s.Drop) * 1000 : 0),5:F0}"
                          + $"-{(hit.Count > 0 ? hit.Max(s => s.Drop) * 1000 : 0),5:F0} mm");
            }

            var tails = strands.Where(s => s.Tail).ToList();
            var kept = strands.Where(s => !s.Tail && !s.Covered).ToList();
            if (tails.Count > 0)
                o.WriteLine($"  tails:  {tails.Sum(s => s.Verts),7} verts, drop {tails.Min(s => s.Drop) * 1000:F0}"
                          + $"-{tails.Max(s => s.Drop) * 1000:F0} mm, reach {tails.Min(s => s.Reach) * 1000:F0}"
                          + $"-{tails.Max(s => s.Reach) * 1000:F0} mm");
            if (kept.Count > 0)
                o.WriteLine($"  kept:   {kept.Sum(s => s.Verts),7} verts, drop {kept.Min(s => s.Drop) * 1000:F0}"
                          + $"-{kept.Max(s => s.Drop) * 1000:F0} mm, reach {kept.Min(s => s.Reach) * 1000:F0}"
                          + $"-{kept.Max(s => s.Reach) * 1000:F0} mm");

            // Only strands with geometry ABOVE the line can be pressed at all, so they are the population a
            // threshold has to cut. Bucketed by how deep below the line they also reach.
            var live = strands.Where(s => s.Verts > s.Below).ToList();
            o.WriteLine($"  {live.Count} strands have geometry above the line, {vAbove} vertices between them");
            foreach (var (lo, hi) in new[] { (0f, .001f), (.001f, .02f), (.02f, .05f), (.05f, .10f),
                                             (.10f, .20f), (.20f, .40f), (.40f, 9f) })
            {
                var band = live.Where(s => s.Drop >= lo && s.Drop < hi).ToList();
                if (band.Count == 0) continue;
                o.WriteLine($"    drop {lo * 1000,5:F0}-{(hi > 8 ? 9999 : hi * 1000),5:F0} mm: "
                          + $"{band.Count,4} strands, {band.Sum(s => s.Verts - s.Below),7} verts above the line, "
                          + $"reach {band.Min(s => s.Reach) * 1000,5:F0}-{band.Max(s => s.Reach) * 1000,5:F0} mm");
            }
        }
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
