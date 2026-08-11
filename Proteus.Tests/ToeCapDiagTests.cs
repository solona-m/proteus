using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>Scratch diagnostics — runs the real toe-cap mask against the real bibo foot model.</summary>
public class ToeCapDiagTests
{
    // WHICH FOOT every measurement in this harness describes. Always a midlander (c0201), but a dozen
    // installed bodies redirect that slot to their own model and they differ by more than 3x in size —
    // the cap's topology at the tips differs enormously between them. Pointed here at the equipped body
    // (Neolithe's meta.json maps c0201e0000_sho.mdl -> feet\feet.mdl); pointed at Bibo+ it reported 2
    // sliver faces where the shipped shell had 50, so a whole session's numbers described a foot nobody
    // was wearing. A stale path here does not fail, it just quietly measures something else.
    private static readonly string Sho =
        Environment.GetEnvironmentVariable("PROTEUS_SHO")
        ?? @"E:\Penumbradt\Neolithe [ALL IN ONE]\FEET\Feet.mdl";
    private const string Scratch =
        @"C:\Users\solon\AppData\Local\Temp\claude\e--repos-Proteus\c157041f-f61a-45b3-8a2b-72bc7dcbef80\scratchpad";

    private readonly ITestOutputHelper o;
    public ToeCapDiagTests(ITestOutputHelper o) => this.o = o;

    /// <summary>
    /// Rebuild the shell from the inputs the GAME used, dumped by SecondSkinService.DumpShellInputs into
    /// %TEMP%\proteus-shell-dump. Approximating those inputs here — one body instead of several, no shape
    /// keys, no connector-mesh mode, a mask baked by hand rather than remapped into this body's UV — is
    /// how the harness came to report a clean cap for a shell that shipped with slivers all over its toes.
    /// Does nothing until the dump folder exists and has been filled by a build in game.
    /// </summary>
    [Fact]
    public void DiagnoseFromGameDump()
    {
        var dir = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        if (!Directory.Exists(dir)) return;

        foreach (var info in Directory.GetFiles(dir, "host*_inputs.txt"))
        {
            var pre = info.Substring(0, info.Length - "inputs.txt".Length);
            var text = File.ReadAllLines(info);
            foreach (var l in text) o.WriteLine(l);

            bool skip = Array.Exists(text, l => l == "skipConnectors=True");
            var bodies = new List<byte[]>();
            for (int i = 0; File.Exists($"{pre}body{i}.mdl"); i++) bodies.Add(File.ReadAllBytes($"{pre}body{i}.mdl"));
            if (bodies.Count == 0) continue;
            var baseModel = File.Exists($"{pre}base.mdl") ? File.ReadAllBytes($"{pre}base.mdl") : null;

            // The dump records the cap the SERVICE decided each layer should get. Setting PROTEUS_CAP_ALL
            // hands the same map to every layer instead, which is what lowering MinToeCoverage does — it
            // answers "would capping the other shells stop them poking through?" without a round trip
            // through the game.
            bool capAll = Environment.GetEnvironmentVariable("PROTEUS_CAP_ALL") == "1";
            byte[]? anyCap = null;
            if (capAll)
                for (int i = 0; File.Exists($"{pre}layer{i}_toecap.raw") || i < 8; i++)
                    if (File.Exists($"{pre}layer{i}_toecap.raw"))
                    { anyCap = File.ReadAllBytes($"{pre}layer{i}_toecap.raw"); break; }

            var layers = new List<SecondSkinLayer>();
            var plainLayers = new List<SecondSkinLayer>();
            for (int i = 0; ; i++)
            {
                var line = Array.Find(text, l => l.StartsWith($"layer[{i}] "));
                if (line == null) break;
                var capPath = $"{pre}layer{i}_toecap.raw";
                var covPath = $"{pre}layer{i}_coverage.raw";
                // inputs.txt is the authority on whether this layer HAD a cap, not the presence of a
                // .raw file. The dump folder is never cleaned, so a layer that has since stopped being
                // capped still has yesterday's map sitting in it — and the replay dutifully capped a
                // layer the game did not, inventing a whole extra cap mesh. Every measurement taken
                // through that described a shell nobody was wearing.
                bool declaresCap = !line.Contains("toeCap=none", StringComparison.OrdinalIgnoreCase);
                var cap = declaresCap && File.Exists(capPath) ? File.ReadAllBytes(capPath)
                        : declaresCap && capAll ? anyCap : null;
                var cov = File.Exists(covPath) ? File.ReadAllBytes(covPath) : null;
                int side = cap == null ? 0 : (int)Math.Round(Math.Sqrt(cap.Length));
                // Coverage dimensions come from the dump when it records them, and from the byte count
                // when replaying an older dump. AnyVisible divides by them, so leaving them at zero is
                // not "no coverage", it is a DivideByZeroException.
                var cvm = System.Text.RegularExpressions.Regex.Match(line, @"coverage=(\d+)x(\d+)");
                int cw = cvm.Success ? int.Parse(cvm.Groups[1].Value)
                       : cov == null ? 0 : (int)Math.Round(Math.Sqrt(cov.Length));
                int ch = cvm.Success ? int.Parse(cvm.Groups[2].Value)
                       : cov == null ? 0 : (int)Math.Round(Math.Sqrt(cov.Length));
                SecondSkinLayer L(byte[]? c) => new()
                {
                    MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                    Coverage = cov,
                    CoverageWidth = cw,
                    CoverageHeight = ch,
                    ToeCap = c,
                    ToeCapWidth = c == null ? 0 : side,
                    ToeCapHeight = c == null ? 0 : side,
                    ToeCapStrength = 1f,
                };
                layers.Add(L(cap));
                plainLayers.Add(L(null));
            }
            if (layers.Count == 0) continue;

            var lines = new List<string>();
            // The bundled authored cap, if this working copy has one built.
            byte[]? authored = null;
            foreach (var cand in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "Meshes", "toecap.mdl"),
                         @"E:\repos\Proteus\Proteus\Meshes\toecap.mdl",
                     })
                if (File.Exists(cand)) { authored = File.ReadAllBytes(cand); break; }
            o.WriteLine(authored == null ? "no authored cap" : $"authored cap {authored.Length} bytes");

            // The cap's binding to the body atlas, the same file the plugin ships. Without it the cap is
            // only correct on the one foot it was modelled against.
            var caps = CapSets();
            o.WriteLine(caps.Count == 0 ? "no authored cap"
                                        : $"{caps.Count} authored cap(s): {string.Join(", ", caps.Select(c => c.Name))}");

            // PLACEMENT ALONE, using the bind that ships — before the push, the weld and the seam split
            // get near it. The build's cap comes out measurably deformed from the authored one, and this
            // splits "the binding put it in the wrong place" from "the graft moved it afterwards".
            foreach (var cs in caps.Where(c => c.Bind != null))
            {
                var back = SecondSkinWriter.TryPlaceCapFromBind(cs.Bind!, bodies, null, cs.Cap);
                if (back == null) continue;
                var authoredMeshes = SecondSkinWriter.ReadCapMeshes(cs.Cap).ToDictionary(x => x.Mesh, x => x.Pos);
                foreach (var pl in back)
                {
                    if (!authoredMeshes.TryGetValue(pl.Mesh, out var srcPos)) continue;
                    double sum = 0, worst = 0;
                    int n = Math.Min(pl.Pos.Length, srcPos.Length);
                    for (int i = 0; i < n; i++)
                    {
                        double d = Math.Sqrt(Math.Pow(pl.Pos[i].X - srcPos[i].X, 2)
                                           + Math.Pow(pl.Pos[i].Y - srcPos[i].Y, 2)
                                           + Math.Pow(pl.Pos[i].Z - srcPos[i].Z, 2));
                        sum += d; worst = Math.Max(worst, d);
                    }
                    o.WriteLine($"PLACEMENT ONLY [{cs.Name}] mesh {pl.Mesh}: {n} verts, "
                              + $"mean {sum / Math.Max(1, n):F6}, worst {worst:F6}, "
                              + $"{pl.Missed} unplaced of {pl.Considered}");
                }
            }

            var plain  = SecondSkinWriter.Build(bodies, plainLayers, baseModel, skip, out _);
            var capped = SecondSkinWriter.Build(bodies, layers, baseModel, skip, out var st,
                null, m => lines.Add(m), caps);
            if (st.CapDeclined is { } dec) o.WriteLine($"CAP DECLINED: {dec}");
            foreach (var l in lines) o.WriteLine(l);
            o.WriteLine($"stats: triIn={st.TrianglesIn} triOut={st.TrianglesOut} verts={st.VerticesOut}");

            // The BODY models themselves, not just the shell built from them: the cap has to clear the
            // player's own skin and toenails, and the shell is a pushed copy, so measuring against it
            // answers a slightly different question than the one the game renders.
            for (int i = 0; i < bodies.Count; i++)
                WriteObj(bodies[i], Path.Combine(Scratch, $"game_body{i}.obj"));

            // The built shell itself, so its headers can be inspected the way the game reads them —
            // bone tables and submesh bone windows do not survive a trip through OBJ.
            File.WriteAllBytes(Path.Combine(Scratch, "game_capped.mdl"), capped);

            WriteObj(plain,  Path.Combine(Scratch, "game_plain.obj"));
            WriteObj(capped, Path.Combine(Scratch, "game_capped.obj"));
            o.WriteLine($"wrote game_plain.obj / game_capped.obj from {Path.GetFileName(info)}");

            foreach (var l in SeamWeights(capped)) o.WriteLine(l);

            // What the game skins with, which no OBJ round trip and no modelling package can show.
            o.WriteLine("--- built shell bones");
            foreach (var l in SecondSkinWriter.DescribeBones(capped)) o.WriteLine(l);

            // Per-vertex, against the cap it was grafted from. The per-bone totals above cannot see a
            // left/right swap on a symmetric cap; this can.
            o.WriteLine("--- cap skinning, vertex by vertex");
            foreach (var cs in caps)
                foreach (var l in SecondSkinWriter.DiffCapSkinning(capped, cs.Cap))
                    o.WriteLine($"[{cs.Name}] {l}");

            // And the SHIPPED file, not the one this harness just rebuilt. The two come from the same
            // writer but not the same caller, and "the test builds it correctly" has never been the same
            // claim as "the plugin wrote that".
            foreach (var shipped in new[] { @"E:\Penumbradt\Proteus\models\secondskin_0.mdl" })
            {
                if (!File.Exists(shipped)) continue;
                var b = File.ReadAllBytes(shipped);
                WriteObj(b, Path.Combine(Scratch, "game_shipped.obj"));
                o.WriteLine($"--- SHIPPED {shipped} ({b.Length} bytes, "
                          + $"{(b.Length == capped.Length && b.AsSpan().SequenceEqual(capped) ? "identical to" : "DIFFERS from")} the rebuild)");
                foreach (var l in SecondSkinWriter.DescribeBones(b)) o.WriteLine(l);
            }
            return;   // one host is enough — the feet live on whichever host took the stocking
        }
    }

    /// <summary>
    /// The foot the cap was AUTHORED against, dumped in the same space as the game's, so the two can be
    /// laid over each other. The cap is exact on one model and nothing has ever measured how far that is
    /// from whatever the player is actually wearing.
    /// </summary>
    [Fact]
    public void DumpReferenceFoot()
    {
        if (!File.Exists(Sho)) return;
        WriteObj(File.ReadAllBytes(Sho), Path.Combine(Scratch, "ref_foot.obj"));
        o.WriteLine($"wrote ref_foot.obj from {Sho}");

        var dir = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        foreach (var f in Directory.Exists(dir) ? Directory.GetFiles(dir, "host*_body*.mdl") : [])
        {
            if (!SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(f), out var p, out _, out var t))
            { o.WriteLine($"{Path.GetFileName(f)}: no skin geometry"); continue; }
            o.WriteLine($"{Path.GetFileName(f)}: {p.Length / 3} skin verts, {t.Length / 3} tris");
        }
    }

    /// <summary>
    /// Bake the authored cap's binding against the foot it was modelled on, then put it back on that same
    /// foot and see whether it lands where it started. That round trip is the whole claim the binding
    /// makes — "these four numbers per vertex are enough to reconstruct the cap on any body" — and if it
    /// cannot reproduce the foot it was measured against, it will not reproduce any other.
    /// <para/>
    /// Then place it on the body the GAME is currently handing us, which is the heeled foot, and report
    /// how far that moves it. Writes Proteus/Meshes/toecap.bind on success.
    /// </summary>
    [Fact]
    public void BakeAndCheckCapBind()
    {
        var capPath = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Meshes", "toecap.mdl"),
            @"E:\repos\Proteus\Proteus\Meshes\toecap.mdl",
        }.FirstOrDefault(File.Exists);
        if (capPath == null || !File.Exists(Sho)) return;

        var cap = File.ReadAllBytes(capPath);
        var reference = new[] { File.ReadAllBytes(Sho) };
        var log = new List<string>();
        var bind = SecondSkinWriter.BakeCapBind(cap, reference, log.Add);
        foreach (var l in log) o.WriteLine(l);
        o.WriteLine($"bind is {bind.Length} bytes");

        // What the cap actually is, to measure the round trip against.
        var authoredMeshes = SecondSkinWriter.ReadCapMeshes(cap).ToDictionary(x => x.Mesh, x => x.Pos);
        var placedRef = SecondSkinWriter.TryPlaceCapFromBind(bind, reference, o.WriteLine);
        Assert.NotNull(placedRef);
        foreach (var pl in placedRef!)
        {
            if (!authoredMeshes.TryGetValue(pl.Mesh, out var src)) continue;
            double sum = 0, worst = 0;
            int n = Math.Min(pl.Pos.Length, src.Length);
            for (int i = 0; i < n; i++)
            {
                double d = Math.Sqrt(Math.Pow(pl.Pos[i].X - src[i].X, 2)
                                   + Math.Pow(pl.Pos[i].Y - src[i].Y, 2)
                                   + Math.Pow(pl.Pos[i].Z - src[i].Z, 2));
                sum += d; worst = Math.Max(worst, d);
            }
            o.WriteLine($"ROUND TRIP mesh {pl.Mesh}: {n} verts, mean {sum / Math.Max(1, n):F6}, "
                      + $"worst {worst:F6}, {pl.Missed} unplaced");
        }

        // ...and onto whatever the game is wearing right now.
        var dir = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        var bodies = new List<byte[]>();
        for (int i = 0; File.Exists(Path.Combine(dir, $"host0_body{i}.mdl")); i++)
            bodies.Add(File.ReadAllBytes(Path.Combine(dir, $"host0_body{i}.mdl")));
        if (bodies.Count > 0)
        {
            var placed = SecondSkinWriter.TryPlaceCapFromBind(bind, bodies, o.WriteLine);
            if (placed != null)
                foreach (var pl in placed)
                {
                    var p = pl.Pos;
                    o.WriteLine($"EQUIPPED mesh {pl.Mesh}: bbox "
                              + $"x {p.Min(q => q.X):F4}..{p.Max(q => q.X):F4} "
                              + $"y {p.Min(q => q.Y):F4}..{p.Max(q => q.Y):F4} "
                              + $"z {p.Min(q => q.Z):F4}..{p.Max(q => q.Z):F4}, {pl.Missed} unplaced");
                }
        }

        var outPath = Path.Combine(@"E:\repos\Proteus\Proteus\Meshes", "toecap.bind");
        if (Directory.Exists(Path.GetDirectoryName(outPath)!))
        {
            File.WriteAllBytes(outPath, bind);
            o.WriteLine($"wrote {outPath}");
        }
    }

    /// <summary>
    /// Every LOD0 mesh of every dumped body: its material, size, where it sits, and what it is skinned
    /// to. The question this exists to answer is which meshes the skin filter is throwing away — a body
    /// that splits its toes onto their own material looks, to everything downstream, like a foot with no
    /// toes on it.
    /// </summary>
    [Fact]
    public void DumpBodyMeshes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        if (!Directory.Exists(dir)) return;

        foreach (var f in Directory.GetFiles(dir, "host0_body*.mdl"))
        {
            o.WriteLine($"=== {Path.GetFileName(f)}");
            foreach (var l in MeshBreakdown(File.ReadAllBytes(f))) o.WriteLine(l);
        }
    }

    private static List<string> MeshBreakdown(byte[] m)
    {
        ushort U16(int x) => BitConverter.ToUInt16(m, x);
        uint U32(int x) => BitConverter.ToUInt32(m, x);

        int declCount = U16(12);
        const int declSize = 17 * 8;
        int declEnd = 0x44 + declCount * declSize;
        int strSize = (int)U32(declEnd + 4);
        int strBlock = declEnd + 8;
        int mh = strBlock + strSize;
        ushort meshCount = U16(mh + 4), attrCount = U16(mh + 6), submeshCount = U16(mh + 8);
        ushort matCount = U16(mh + 10), boneCount = U16(mh + 12), boneTableCount = U16(mh + 14);
        ushort elemCount = U16(mh + 24);
        byte tsMesh = m[mh + 26], flags2 = m[mh + 27];
        ushort tsSub = U16(mh + 38);
        int lodStart = mh + 56 + elemCount * 32;
        int meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);
        int attrStart = meshStart + meshCount * 36;
        int subStart = attrStart + attrCount * 4 + tsMesh * 20;
        int matOff = subStart + submeshCount * 16 + tsSub * 12;
        int boneOff = matOff + matCount * 4;
        uint vtxOff = U32(16);

        string Str(uint rel)
        {
            int a = strBlock + (int)rel, e = a;
            while (m[e] != 0) e++;
            return Encoding.ASCII.GetString(m, a, e - a);
        }

        int p2 = boneOff + boneCount * 4;
        var tables = new ushort[boneTableCount][];
        for (int i = 0; i < boneTableCount; i++)
        {
            int hp = p2 + i * 4;
            ushort off = U16(hp), size = U16(hp + 2);
            int data = hp + off * 4;
            var t = new ushort[size];
            for (int k = 0; k < size; k++) t[k] = U16(data + k * 2);
            tables[i] = t;
        }

        // LOD0 only: the lod table's first entry gives the mesh range.
        ushort lod0Mesh = U16(lodStart), lod0Count = U16(lodStart + 2);
        var outp = new List<string>();
        for (int mi = lod0Mesh; mi < lod0Mesh + lod0Count && mi < meshCount; mi++)
        {
            int mo = meshStart + mi * 36;
            ushort vc = U16(mo);
            if (vc == 0) continue;
            uint ic = U32(mo + 4);
            ushort matIdx = U16(mo + 8), boneTbl = U16(mo + 14);
            string mat = matIdx < matCount ? Str(U32(matOff + matIdx * 4)) : "?";

            uint[] vbo = { U32(mo + 20), U32(mo + 24), U32(mo + 28) };
            byte[] bs = { m[mo + 32], m[mo + 33], m[mo + 34] };
            (byte Stream, byte Offset, byte Type)? pos = null, wgt = null, idx = null;
            for (int e = 0; e < 17; e++)
            {
                int x = 0x44 + mi * declSize + e * 8;
                if (m[x] == 0xFF) break;
                if (m[x + 3] == 0) pos ??= (m[x], m[x + 1], m[x + 2]);
                if (m[x + 3] == 1) wgt ??= (m[x], m[x + 1], m[x + 2]);
                if (m[x + 3] == 2) idx ??= (m[x], m[x + 1], m[x + 2]);
            }
            if (pos is not { } pe) continue;

            float x0 = 9e9f, x1 = -9e9f, y0 = 9e9f, y1 = -9e9f, z0 = 9e9f, z1 = -9e9f;
            var acc = new Dictionary<string, float>();
            var table = boneTbl < tables.Length ? tables[boneTbl] : [];
            for (int v = 0; v < vc; v++)
            {
                int pa = (int)(vtxOff + vbo[pe.Stream]) + v * bs[pe.Stream] + pe.Offset;
                float px, py, pz;
                if (pe.Type == 14)
                { px = (float)BitConverter.ToHalf(m, pa); py = (float)BitConverter.ToHalf(m, pa + 2); pz = (float)BitConverter.ToHalf(m, pa + 4); }
                else
                { px = BitConverter.ToSingle(m, pa); py = BitConverter.ToSingle(m, pa + 4); pz = BitConverter.ToSingle(m, pa + 8); }
                x0 = MathF.Min(x0, px); x1 = MathF.Max(x1, px);
                y0 = MathF.Min(y0, py); y1 = MathF.Max(y1, py);
                z0 = MathF.Min(z0, pz); z1 = MathF.Max(z1, pz);

                if (wgt is not { } we || idx is not { } ie) continue;
                int wa = (int)(vtxOff + vbo[we.Stream]) + v * bs[we.Stream] + we.Offset;
                int ia = (int)(vtxOff + vbo[ie.Stream]) + v * bs[ie.Stream] + ie.Offset;
                for (int k = 0; k < 4; k++)
                {
                    float w = m[wa + k] / 255f;
                    if (w <= 0f) continue;
                    int local = m[ia + k];
                    string nm = local < table.Length && table[local] < boneCount
                        ? Str(U32(boneOff + table[local] * 4)) : $"?{local}";
                    acc[nm] = acc.GetValueOrDefault(nm) + w;
                }
            }
            float sum = acc.Values.Sum();
            var top = acc.OrderByDescending(k => k.Value).Take(5)
                         .Select(k => $"{k.Key} {100 * k.Value / MathF.Max(sum, 1e-6f):0}%");
            outp.Add($"  mesh {mi,2}: {vc,5} verts {ic / 3,6} tris  {mat,-34} "
                   + $"x {x0,7:F3}..{x1,6:F3} y {y0,7:F3}..{y1,6:F3} z {z0,7:F3}..{z1,6:F3}");
            outp.Add($"            skin={SecondSkinWriter.SkinMaterialBodyType(mat) ?? "NOT SKIN",-8} "
                   + $"bones: {string.Join(", ", top)}");
        }
        return outp;
    }

    /// <summary>
    /// Bake a binding against the body the game is CURRENTLY handing us, and ship it beside the cap.
    /// <para/>
    /// This is how a new body gets supported. A binding measured against one body does not transfer to
    /// another even when both are nominally the same UV space: Neolithe and Rue are both "bibo" and lay
    /// their toe islands in the same place, but a point on one sits about 0.008 away on the other, which
    /// is roughly a triangle — enough that the narrow toe islands miss and 78% of the cap fails to place.
    /// Measuring against each body sidesteps the comparison entirely.
    /// <para/>
    /// Workflow: wear the body, rebuild the shell in game so the dump refreshes, then run this with
    /// PROTEUS_BIND_NAME set to something recognisable. Nothing of the body itself is stored — only the
    /// four numbers per vertex — so no body mod is redistributed.
    /// </summary>
    [Fact]
    public void BakeCapBindForEquippedBody()
    {
        var name = Environment.GetEnvironmentVariable("PROTEUS_BIND_NAME");
        if (string.IsNullOrWhiteSpace(name)) return;   // opt-in: this writes a shipped file

        // Which cap to measure — the one modelled for the body currently worn.
        var capPath = Environment.GetEnvironmentVariable("PROTEUS_CAP_PATH")
                   ?? new[]
                      {
                          Path.Combine(AppContext.BaseDirectory, "Meshes", "toecap.mdl"),
                          @"E:\repos\Proteus\Proteus\Meshes\toecap.mdl",
                      }.FirstOrDefault(File.Exists);
        var dir = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        if (capPath == null || !File.Exists(capPath) || !Directory.Exists(dir)) return;
        o.WriteLine($"measuring {capPath}");

        var bodies = new List<byte[]>();
        for (int i = 0; File.Exists(Path.Combine(dir, $"host0_body{i}.mdl")); i++)
            bodies.Add(File.ReadAllBytes(Path.Combine(dir, $"host0_body{i}.mdl")));
        Assert.NotEmpty(bodies);

        // Offsets are MEASURED here, not transplanted: this cap was modelled against this body, so the
        // height it sits at above this skin is the authored one already. Transplanting only made sense
        // while one cap was being stretched across bodies, which is no longer how this works.
        var cap = File.ReadAllBytes(capPath);
        var bind = SecondSkinWriter.BakeCapBind(cap, bodies, o.WriteLine);

        // It has to reproduce the cap on the body it was just measured against, or it will not
        // reproduce it anywhere.
        var back = SecondSkinWriter.TryPlaceCapFromBind(bind, bodies, o.WriteLine, cap);
        Assert.NotNull(back);
        var authoredMeshes = SecondSkinWriter.ReadCapMeshes(cap).ToDictionary(x => x.Mesh, x => x.Pos);
        foreach (var pl in back!)
        {
            if (!authoredMeshes.TryGetValue(pl.Mesh, out var src)) continue;
            double sum = 0, worst = 0;
            int n = Math.Min(pl.Pos.Length, src.Length);
            for (int i = 0; i < n; i++)
            {
                double d = Math.Sqrt(Math.Pow(pl.Pos[i].X - src[i].X, 2)
                                   + Math.Pow(pl.Pos[i].Y - src[i].Y, 2)
                                   + Math.Pow(pl.Pos[i].Z - src[i].Z, 2));
                sum += d; worst = Math.Max(worst, d);
            }
            o.WriteLine($"ROUND TRIP mesh {pl.Mesh}: {n} verts, mean {sum / Math.Max(1, n):F6}, "
                      + $"worst {worst:F6}, {pl.Missed} unplaced of {pl.Considered}");

            // WHERE the error is, not just how big. A mean of 0.0003 with a worst of 0.0039 is not a
            // uniformly good reconstruction — it is an excellent one almost everywhere and a bad one
            // somewhere specific, and "somewhere specific" on a toe cap is a feature you can see.
            var errs = new List<(double D, SecondSkinWriter.Vec3 P)>();
            for (int i = 0; i < n; i++)
                errs.Add((Math.Sqrt(Math.Pow(pl.Pos[i].X - src[i].X, 2)
                                  + Math.Pow(pl.Pos[i].Y - src[i].Y, 2)
                                  + Math.Pow(pl.Pos[i].Z - src[i].Z, 2)), src[i]));
            errs.Sort((a, b) => b.D.CompareTo(a.D));
            foreach (var cut in new[] { 0.003, 0.002, 0.001, 0.0005 })
                o.WriteLine($"   over {cut:F4}: {errs.Count(e => e.D > cut)} vertices");
            o.WriteLine("   worst 12, authored position (x y z) -> displacement:");
            foreach (var (d, p) in errs.Take(12))
                o.WriteLine($"      ({p.X,8:F4} {p.Y,8:F4} {p.Z,8:F4})  {d:F5}");
        }

        var outPath = Path.Combine(@"E:\repos\Proteus\Proteus\Meshes", $"toecap.{name}.bind");
        File.WriteAllBytes(outPath, bind);
        o.WriteLine($"wrote {outPath} ({bind.Length} bytes)");
    }

    /// <summary>
    /// Decode the shell's baked .tex files to PNG, plus an alpha-only copy of each normal map.
    /// <para/>
    /// The normal map is where a gear shell's TRANSPARENCY lives, so a shell with correct geometry and
    /// correct UVs can still show bare skin wherever that alpha is zero. Nothing else in this harness
    /// looks at it — every measurement so far has been geometry.
    /// </summary>
    [Fact]
    public void DumpShellTextures()
    {
        var dir = Environment.GetEnvironmentVariable("PROTEUS_TEX_DIR")
               ?? @"E:\Penumbradt\Proteus\textures";
        if (!Directory.Exists(dir)) return;

        var loader = new TextureLoader(null!, new NullLog());
        foreach (var tex in Directory.GetFiles(dir, "*.tex").OrderBy(x => x))
        {
            var got = loader.LoadTexAsRgba(tex);
            if (got is not { } t) { o.WriteLine($"{Path.GetFileName(tex)}: could not decode"); continue; }
            var (rgba, w, h) = t;

            var name = Path.GetFileNameWithoutExtension(tex);
            loader.WritePng(rgba, w, h, Path.Combine(Scratch, $"{name}.png"));

            // Alpha on its own — on a normal map that IS the coverage, and it is invisible in the RGB.
            var a = new byte[rgba.Length];
            long sum = 0; int zero = 0;
            for (int i = 0; i < w * h; i++)
            {
                byte v = rgba[i * 4 + 3];
                a[i * 4] = a[i * 4 + 1] = a[i * 4 + 2] = v;
                a[i * 4 + 3] = 255;
                sum += v;
                if (v < 8) zero++;
            }
            loader.WritePng(a, w, h, Path.Combine(Scratch, $"{name}_alpha.png"));
            o.WriteLine($"{name}: {w}x{h}  mean alpha {sum / (double)(w * h):F1}  "
                      + $"{zero * 100.0 / (w * h):F1}% at or below 8");
        }
        o.WriteLine($"wrote PNGs to {Scratch}");
    }

    private sealed class NullLog : Dalamud.Plugin.Services.IPluginLog
    {
        public Serilog.Events.LogEventLevel MinimumLogLevel { get; set; }
        public Serilog.ILogger Logger => Serilog.Core.Logger.None;
        public void Debug(string m, params object[] v) { }
        public void Debug(Exception? e, string m, params object[] v) { }
        public void Error(string m, params object[] v) { }
        public void Error(Exception? e, string m, params object[] v) { }
        public void Fatal(string m, params object[] v) { }
        public void Fatal(Exception? e, string m, params object[] v) { }
        public void Info(string m, params object[] v) { }
        public void Info(Exception? e, string m, params object[] v) { }
        public void Information(string m, params object[] v) { }
        public void Information(Exception? e, string m, params object[] v) { }
        public void Verbose(string m, params object[] v) { }
        public void Verbose(Exception? e, string m, params object[] v) { }
        public void Warning(string m, params object[] v) { }
        public void Warning(Exception? e, string m, params object[] v) { }
        public void Write(Serilog.Events.LogEventLevel l, Exception? e, string m, params object[] v) { }
    }

    /// <summary>
    /// Vertex colour per LOD0 mesh. The shell forces it white on purpose; anything that does not go
    /// through that path keeps whatever its exporter wrote, and vertex colour is not inert.
    /// </summary>
    [Fact]
    public void DumpVertexColors()
    {
        var path = Environment.GetEnvironmentVariable("PROTEUS_CAP_PATH")
                ?? @"E:\Penumbradt\Proteus\models\secondskin_0.mdl";
        if (!File.Exists(path)) return;
        var m = File.ReadAllBytes(path);
        o.WriteLine($"vertex colours in {path}");

        ushort U16(int x) => BitConverter.ToUInt16(m, x);
        uint U32(int x) => BitConverter.ToUInt32(m, x);
        int declCount = U16(12);
        const int declSize = 17 * 8;
        int declEnd = 0x44 + declCount * declSize;
        int mh = declEnd + 8 + (int)U32(declEnd + 4);
        ushort meshCount = U16(mh + 4);
        ushort elemCount = U16(mh + 24);
        byte flags2 = m[mh + 27];
        int lodStart = mh + 56 + elemCount * 32;
        int meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);
        uint vtxOff = U32(16);

        for (int mi = 0; mi < meshCount; mi++)
        {
            int mo = meshStart + mi * 36;
            ushort vc = U16(mo);
            if (vc == 0) continue;
            uint[] vbo = { U32(mo + 20), U32(mo + 24), U32(mo + 28) };
            byte[] bs = { m[mo + 32], m[mo + 33], m[mo + 34] };
            (byte S, byte O, byte T)? col = null;
            for (int e = 0; e < 17; e++)
            {
                int x = 0x44 + mi * declSize + e * 8;
                if (m[x] == 0xFF) break;
                if (m[x + 3] == 7) { col = (m[x], m[x + 1], m[x + 2]); break; }
            }
            if (col is not { } ce) { o.WriteLine($"  mesh {mi,2}: {vc,5} verts — no vertex colour"); continue; }

            var seen = new Dictionary<(byte, byte, byte, byte), int>();
            for (int v = 0; v < vc; v++)
            {
                int a = (int)(vtxOff + vbo[ce.S]) + v * bs[ce.S] + ce.O;
                var k = (m[a], m[a + 1], m[a + 2], m[a + 3]);
                seen[k] = seen.GetValueOrDefault(k) + 1;
            }
            var top = seen.OrderByDescending(k => k.Value).Take(4)
                          .Select(k => $"({k.Key.Item1},{k.Key.Item2},{k.Key.Item3},{k.Key.Item4})x{k.Value}");
            bool allWhite = seen.Count == 1 && seen.Keys.First() == ((byte)255, (byte)255, (byte)255, (byte)255);
            o.WriteLine($"  mesh {mi,2}: {vc,5} verts  {seen.Count,4} distinct  "
                      + (allWhite ? "ALL WHITE" : "NOT WHITE: " + string.Join(" ", top)));
        }
    }

    [Fact]
    public void Diagnose()
    {
        // A developer harness, not an assertion: it re-exports the shell as OBJ (positions, STORED
        // normals, UVs) so scratchpad/render.py can shade it the way the game does. Rendering from
        // recomputed face normals is what hid a cap whose normals were never rewritten.
        var maskPath = Path.Combine(Scratch, "toecap512.raw");
        if (!File.Exists(Sho) || !File.Exists(maskPath)) return;   // not this machine — nothing to dump

        var body = File.ReadAllBytes(Sho);
        var mask = File.ReadAllBytes(maskPath);

        SecondSkinLayer Layer(byte[]? cap) => new()
        {
            MaterialName = "/mt_c0201a0053_rir_a.mtrl",
            Coverage = null,
            ToeCap = cap,
            ToeCapWidth = cap == null ? 0 : 512,
            ToeCapHeight = cap == null ? 0 : 512,
            ToeCapStrength = 1f,
        };

        var lines = new List<string>();
        var plain  = SecondSkinWriter.Build(new[] { body }, new[] { Layer(null) }, null, false, out _);
        var capped = SecondSkinWriter.Build(new[] { body }, new[] { Layer(mask) }, null, false, out var stats,
            null, m => lines.Add(m));

        foreach (var l in lines) o.WriteLine(l);
        o.WriteLine($"stats: meshes={stats.Meshes} submeshes={stats.Submeshes} bones={stats.Bones} " +
                    $"triIn={stats.TrianglesIn} triOut={stats.TrianglesOut} verts={stats.VerticesOut}");

        WriteObj(plain,  Path.Combine(Scratch, "foot_plain.obj"));
        WriteObj(capped, Path.Combine(Scratch, "foot_capped.obj"));
        o.WriteLine("wrote objs");

        foreach (var l in Submeshes(body)) o.WriteLine(l);
    }

    /// <summary>
    /// Structural dump of an authored cap converted out of 3ds Max, before anything tries to merge it:
    /// mesh and submesh layout, bone table, materials, and the geometry as an OBJ so it can be measured
    /// against the foot it will sit on. Runs only when the file is there.
    /// </summary>
    [Fact]
    public void DumpAuthoredCap()
    {
        var path = Environment.GetEnvironmentVariable("PROTEUS_CAP_PATH")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                "OneDrive", "Desktop", "cap.mdl");
        if (!File.Exists(path)) return;
        o.WriteLine($"inspecting {path}");
        var m = File.ReadAllBytes(path);
        o.WriteLine($"cap.mdl: {m.Length} bytes");

        ushort U16(int x) => BitConverter.ToUInt16(m, x);
        uint U32(int x) => BitConverter.ToUInt32(m, x);
        int declCount = U16(12);
        const int declCount0 = 17 * 8;
        int declEnd = 0x44 + declCount * declCount0;
        int strSize = (int)U32(declEnd + 4);
        int strBlock = declEnd + 8;
        int mh = strBlock + strSize;
        // Field offsets exactly as SecondSkinWriter.Parse reads them.
        ushort meshCount = U16(mh + 4), attrCount = U16(mh + 6), submeshCount = U16(mh + 8);
        ushort matCount = U16(mh + 10), boneCount = U16(mh + 12), boneTableCount = U16(mh + 14);
        o.WriteLine($"version {U32(0):X}, {declCount} vertex declarations, string block {strSize} bytes");
        o.WriteLine($"meshes {meshCount}, submeshes {submeshCount}, materials {matCount}, "
                  + $"bones {boneCount}, boneTables {boneTableCount}, shapes {U16(mh + 16)}");

        byte flags2b = m[mh + 27];
        ushort elemCountB = U16(mh + 24);
        byte tsMeshB = m[mh + 26];
        ushort tsSubB = U16(mh + 38);
        int lodStartB = mh + 56 + elemCountB * 32;
        int meshStartB = lodStartB + 3 * 60 + ((flags2b & 0x10) != 0 ? 3 * 40 : 0);
        int attrStartB = meshStartB + meshCount * 36;
        int subStartB = attrStartB + attrCount * 4 + tsMeshB * 20;
        int matOffB = subStartB + submeshCount * 16 + tsSubB * 12;
        int boneOffB = matOffB + matCount * 4;
        string Str(uint rel)
        {
            int a = strBlock + (int)rel, e = a;
            while (m[e] != 0) e++;
            return System.Text.Encoding.ASCII.GetString(m, a, e - a);
        }
        for (int i = 0; i < matCount; i++) o.WriteLine($"  material {i}: {Str(U32(matOffB + i * 4))}");
        for (int i = 0; i < boneCount; i++) o.WriteLine($"  bone {i}: {Str(U32(boneOffB + i * 4))}");

        foreach (var l in Submeshes(m)) o.WriteLine(l);

        // Per-vertex blend weights, resolved through the mesh's own bone table to names. The bone LIST
        // only says which bones exist; this says where the weight actually went.
        int p2 = boneOffB + boneCount * 4;
        var tables = new ushort[boneTableCount][];
        for (int i = 0; i < boneTableCount; i++)
        {
            int hp = p2 + i * 4;
            ushort off = U16(hp), size = U16(hp + 2);
            int data = hp + off * 4;
            var t = new ushort[size];
            for (int k = 0; k < size; k++) t[k] = U16(data + k * 2);
            tables[i] = t;
        }
        uint vtxOffB = U32(16);
        for (int mi = 0; mi < meshCount; mi++)
        {
            int mo = meshStartB + mi * 36;
            ushort vc = U16(mo);
            if (vc == 0) continue;
            var decl = new List<(byte Stream, byte Offset, byte Type, byte Usage)>();
            for (int e = 0; e < 17; e++)
            {
                int x = 0x44 + mi * declCount0 + e * 8;
                if (m[x] == 0xFF) break;
                decl.Add((m[x], m[x + 1], m[x + 2], m[x + 3]));
            }
            var wEl = decl.FirstOrDefault(d => d.Usage == 1);
            var iEl = decl.FirstOrDefault(d => d.Usage == 2);
            if (wEl.Type == 0 && iEl.Type == 0) { o.WriteLine($"  mesh {mi}: no blend data"); continue; }
            uint[] vbo = { U32(mo + 20), U32(mo + 24), U32(mo + 28) };
            byte[] bs = { m[mo + 32], m[mo + 33], m[mo + 34] };
            var table = mi < tables.Length ? tables[mi] : [];
            var acc = new Dictionary<int, float>();
            for (int v = 0; v < vc; v++)
            {
                int wa = (int)(vtxOffB + vbo[wEl.Stream]) + v * bs[wEl.Stream] + wEl.Offset;
                int ia = (int)(vtxOffB + vbo[iEl.Stream]) + v * bs[iEl.Stream] + iEl.Offset;
                for (int k = 0; k < 4; k++)
                {
                    float w = m[wa + k] / 255f;
                    if (w <= 0f) continue;
                    int local = m[ia + k];
                    acc[local] = acc.GetValueOrDefault(local) + w;
                }
            }
            float sum = acc.Values.Sum();
            o.WriteLine($"  mesh {mi} weight by bone (table {table.Length} entries):");
            foreach (var kv in acc.OrderByDescending(k => k.Value))
            {
                string nm = kv.Key < table.Length && table[kv.Key] < boneCount
                    ? Str(U32(boneOffB + table[kv.Key] * 4)) : $"local {kv.Key}";
                o.WriteLine($"     {nm,-14} {100 * kv.Value / sum,5:0.0}%");
            }
        }

        WriteObj(m, Path.Combine(Scratch, "cap.obj"));
        o.WriteLine("wrote cap.obj");
    }

    /// <summary>
    /// Do the two sides of the cap/shell join carry the SAME skinning? The positions are snapped until
    /// they coincide, but coincident is a bind-pose statement: two vertices in the same place with
    /// different weights sit together in the T-pose and pull apart the moment the foot is posed, which
    /// is the only pose anyone ever sees. Every measurement in this harness is bind-pose, so this is the
    /// one thing it cannot see by looking at positions.
    /// <para/>
    /// Pairs up vertices from DIFFERENT meshes that share a position and reports where their weights
    /// disagree, resolved through each mesh's own bone table to names.
    /// </summary>
    private static List<string> SeamWeights(byte[] m)
    {
        ushort U16(int x) => BitConverter.ToUInt16(m, x);
        uint U32(int x) => BitConverter.ToUInt32(m, x);

        int declCount = U16(12);
        const int declSize = 17 * 8;
        int declEnd = 0x44 + declCount * declSize;
        int strSize = (int)U32(declEnd + 4);
        int strBlock = declEnd + 8;
        int mh = strBlock + strSize;
        ushort meshCount = U16(mh + 4), attrCount = U16(mh + 6), submeshCount = U16(mh + 8);
        ushort matCount = U16(mh + 10), boneCount = U16(mh + 12), boneTableCount = U16(mh + 14);
        ushort elemCount = U16(mh + 24);
        byte tsMesh = m[mh + 26], flags2 = m[mh + 27];
        ushort tsSub = U16(mh + 38);
        int lodStart = mh + 56 + elemCount * 32;
        int meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);
        int attrStart = meshStart + meshCount * 36;
        int subStart = attrStart + attrCount * 4 + tsMesh * 20;
        int matOff = subStart + submeshCount * 16 + tsSub * 12;
        int boneOff = matOff + matCount * 4;
        uint vtxOff = U32(16);

        string Str(uint rel)
        {
            int a = strBlock + (int)rel, e = a;
            while (m[e] != 0) e++;
            return Encoding.ASCII.GetString(m, a, e - a);
        }

        int p2 = boneOff + boneCount * 4;
        var tables = new ushort[boneTableCount][];
        for (int i = 0; i < boneTableCount; i++)
        {
            int hp = p2 + i * 4;
            ushort off = U16(hp), size = U16(hp + 2);
            int data = hp + off * 4;
            var t = new ushort[size];
            for (int k = 0; k < size; k++) t[k] = U16(data + k * 2);
            tables[i] = t;
        }

        var all = new List<(int Mesh, float X, float Y, float Z, Dictionary<string, float> W)>();
        for (int mi = 0; mi < meshCount; mi++)
        {
            int mo = meshStart + mi * 36;
            ushort vc = U16(mo);
            if (vc == 0) continue;
            ushort boneTbl = U16(mo + 14);
            uint[] vbo = { U32(mo + 20), U32(mo + 24), U32(mo + 28) };
            byte[] bs = { m[mo + 32], m[mo + 33], m[mo + 34] };

            (byte Stream, byte Offset, byte Type)? pos = null, wgt = null, idx = null;
            for (int e = 0; e < 17; e++)
            {
                int x = 0x44 + mi * declSize + e * 8;
                if (m[x] == 0xFF) break;
                if (m[x + 3] == 0) pos ??= (m[x], m[x + 1], m[x + 2]);
                if (m[x + 3] == 1) wgt ??= (m[x], m[x + 1], m[x + 2]);
                if (m[x + 3] == 2) idx ??= (m[x], m[x + 1], m[x + 2]);
            }
            if (pos is not { } pe || wgt is not { } we || idx is not { } ie) continue;
            var table = boneTbl < tables.Length ? tables[boneTbl] : [];

            for (int v = 0; v < vc; v++)
            {
                int pa = (int)(vtxOff + vbo[pe.Stream]) + v * bs[pe.Stream] + pe.Offset;
                float x2, y2, z2;
                if (pe.Type == 14)
                { x2 = (float)BitConverter.ToHalf(m, pa); y2 = (float)BitConverter.ToHalf(m, pa + 2); z2 = (float)BitConverter.ToHalf(m, pa + 4); }
                else
                { x2 = BitConverter.ToSingle(m, pa); y2 = BitConverter.ToSingle(m, pa + 4); z2 = BitConverter.ToSingle(m, pa + 8); }
                if (y2 > 0.08f) continue;   // feet only

                int wa = (int)(vtxOff + vbo[we.Stream]) + v * bs[we.Stream] + we.Offset;
                int ia = (int)(vtxOff + vbo[ie.Stream]) + v * bs[ie.Stream] + ie.Offset;
                var w = new Dictionary<string, float>();
                // EIGHT on the Dawntrail format (type 17), four otherwise. Reading four from an
                // eight-influence vertex compares truncated sets and invents disagreements.
                int nInf = we.Type == 17 ? 8 : 4;
                for (int k = 0; k < nInf; k++)
                {
                    float f = m[wa + k] / 255f;
                    if (f <= 0f) continue;
                    int local = m[ia + k];
                    string nm = local < table.Length && table[local] < boneCount
                        ? Str(U32(boneOff + table[local] * 4)) : $"?{local}";
                    w[nm] = w.GetValueOrDefault(nm) + f;
                }
                all.Add((mi, x2, y2, z2, w));
            }
        }

        // Nearest neighbour ACROSS meshes rather than exact coincidence. The weld lands a shell vertex on
        // the nearest POINT of a cap segment, so the two sides almost never share a coordinate exactly —
        // testing for that found 10 pairs out of some 140 welded vertices and said nothing about the rest.
        var outp = new List<string>();
        int pairs = 0, disagree = 0;
        float worst = 0f;
        var byPair = new Dictionary<(int, int), (int N, int Bad, float Worst)>();
        // Bucketed by how far apart the pair actually is. Two vertices 1.5mm apart on the body carry
        // measurably different weights all by themselves — the body's own field varies over that
        // distance — so a flat "nearest neighbour disagrees" number says nothing about continuity. What
        // matters is the trend as the distance goes to zero: if the disagreement vanishes with it, the
        // two surfaces share one weight field and the join cannot open however the foot is posed.
        var buckets = new (float Max, int N, float Sum, float Worst)[]
        {
            (0.00005f, 0, 0, 0), (0.0002f, 0, 0, 0), (0.0005f, 0, 0, 0),
            (0.001f, 0, 0, 0), (0.0015f, 0, 0, 0),
        };
        const float near = 0.0015f;
        for (int i = 0; i < all.Count; i++)
        {
            int bestJ = -1;
            float bestD = near * near;
            for (int j = 0; j < all.Count; j++)
            {
                if (all[j].Mesh == all[i].Mesh) continue;
                float dx = all[i].X - all[j].X, dy = all[i].Y - all[j].Y, dz = all[i].Z - all[j].Z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < bestD) { bestD = d2; bestJ = j; }
            }
            if (bestJ < 0) continue;
            pairs++;
            float d = 0f;
            foreach (var nm in all[i].W.Keys.Union(all[bestJ].W.Keys))
                d += MathF.Abs(all[i].W.GetValueOrDefault(nm) - all[bestJ].W.GetValueOrDefault(nm));
            d /= 2f;   // total variation distance: 0 = identical, 1 = nothing in common
            var k2 = (Math.Min(all[i].Mesh, all[bestJ].Mesh), Math.Max(all[i].Mesh, all[bestJ].Mesh));
            var acc = byPair.GetValueOrDefault(k2);
            byPair[k2] = (acc.N + 1, acc.Bad + (d > 0.02f ? 1 : 0), MathF.Max(acc.Worst, d));
            if (d > 0.02f) disagree++;
            worst = MathF.Max(worst, d);

            float gap = MathF.Sqrt(bestD);
            for (int q = 0; q < buckets.Length; q++)
                if (gap <= buckets[q].Max)
                {
                    buckets[q] = (buckets[q].Max, buckets[q].N + 1, buckets[q].Sum + d,
                                  MathF.Max(buckets[q].Worst, d));
                    break;
                }
        }
        outp.Add($"seam weights: {pairs} coincident cross-mesh vertex pair(s) on the feet, "
               + $"{disagree} disagree by >2%, worst {worst * 100:0.0}%");
        foreach (var kv in byPair.OrderByDescending(k => k.Value.N))
            outp.Add($"   mesh{kv.Key.Item1} / mesh{kv.Key.Item2}: {kv.Value.N} pairs, "
                   + $"{kv.Value.Bad} disagree, worst {kv.Value.Worst * 100:0.0}%");
        outp.Add("   by how far apart the pair is (mean / worst disagreement):");
        foreach (var b in buckets)
            outp.Add($"      within {b.Max:F5}: {b.N,5} pairs  mean {(b.N > 0 ? b.Sum / b.N : 0) * 100,5:0.0}%"
                   + $"  worst {b.Worst * 100,5:0.0}%");
        return outp;
    }

    /// <summary>Every authored cap the plugin would ship, paired with its binding — same rule as the service.</summary>
    private static List<SecondSkinWriter.AuthoredCapSet> CapSets()
    {
        foreach (var d in new[] { Path.Combine(AppContext.BaseDirectory, "Meshes"),
                                  @"E:\repos\Proteus\Proteus\Meshes" })
        {
            if (!Directory.Exists(d)) continue;
            var found = new List<SecondSkinWriter.AuthoredCapSet>();
            foreach (var mp in Directory.GetFiles(d, "toecap*.mdl").OrderBy(x => x))
            {
                var bind = Path.ChangeExtension(mp, ".bind");
                found.Add(new SecondSkinWriter.AuthoredCapSet(
                    File.ReadAllBytes(mp),
                    File.Exists(bind) ? File.ReadAllBytes(bind) : null,
                    Path.GetFileNameWithoutExtension(mp)));
            }
            if (found.Count > 0) return found;
        }
        return [];
    }

    /// <summary>Per-mesh submesh layout of a SOURCE model — a generated triangle has to fit in one.</summary>
    private static List<string> Submeshes(byte[] m)
    {
        ushort U16(int x) => BitConverter.ToUInt16(m, x);
        uint U32(int x) => BitConverter.ToUInt32(m, x);

        int declCount = U16(12);
        int declEnd = 0x44 + declCount * 17 * 8;
        int mh = declEnd + 8 + (int)U32(declEnd + 4);
        int meshCount = U16(mh + 4);
        int elemCount = U16(mh + 24);
        byte flags2 = m[mh + 27];
        int lodStart = mh + 56 + elemCount * 32;
        int meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);
        int subStart = meshStart + meshCount * 36;

        var outp = new List<string>();
        for (int mi = 0; mi < meshCount; mi++)
        {
            int mo = meshStart + mi * 36;
            ushort vc = U16(mo), si = U16(mo + 10), sc = U16(mo + 12);
            var parts = new List<string>();
            for (int s = 0; s < sc; s++)
            {
                int ss = subStart + (si + s) * 16;
                parts.Add($"[idx {U32(ss)}+{U32(ss + 4)} bones {U16(ss + 12)}+{U16(ss + 14)}]");
            }
            outp.Add($"mesh {mi}: {vc} verts, {sc} submesh(es) {string.Join(" ", parts)}");
        }
        return outp;
    }

    /// <summary>Parse a built shell model and dump its LOD0 geometry as a wavefront OBJ.</summary>
    private static void WriteObj(byte[] m, string path)
    {
        ushort U16(int x) => BitConverter.ToUInt16(m, x);
        uint U32(int x) => BitConverter.ToUInt32(m, x);

        int declCount = U16(12);
        int declEnd = 0x44 + declCount * 17 * 8;
        int strSize = (int)U32(declEnd + 4);
        int mh = declEnd + 8 + strSize;
        int meshCount = U16(mh + 4);
        int elemCount = U16(mh + 24);
        byte flags2 = m[mh + 27];
        int lodStart = mh + 56 + elemCount * 32;
        int meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);
        int subStart = meshStart + meshCount * 36;
        uint vtxOff = U32(16), idxOff = U32(28);

        // Grouped sections and fully-qualified face references. Interleaving v/vn/vt per vertex and
        // then emitting bare "f a b c" is legal OBJ and reads back fine, but 3ds Max makes nonsense of
        // it — it imported a clean cap as a handful of enormous fins. Every importer handles the
        // conventional layout, so write that.
        var vs = new StringBuilder();
        var ts = new StringBuilder();
        var ns = new StringBuilder();
        var fs = new StringBuilder();
        int baseVert = 1;
        for (int mi = 0; mi < meshCount; mi++)
        {
            int mo = meshStart + mi * 36;
            ushort vc = U16(mo);
            uint idxCount = U32(mo + 4);
            uint startIdx = U32(mo + 16);
            uint[] vOff = { U32(mo + 20), U32(mo + 24), U32(mo + 28) };
            byte[] str = { m[mo + 32], m[mo + 33], m[mo + 34] };

            int db = 0x44 + mi * 17 * 8;
            int pStream = -1, pOff = 0, pType = 0;
            int tStream = -1, tOff = 0, tType = 0;
            int nStream = -1, nOff = 0, nType = 0;
            for (int e = 0; e < 17; e++)
            {
                int x = db + e * 8;
                if (m[x] == 0xFF) break;
                if (m[x + 3] == 0 && pStream < 0) { pStream = m[x]; pOff = m[x + 1]; pType = m[x + 2]; }
                if (m[x + 3] == 3 && nStream < 0) { nStream = m[x]; nOff = m[x + 1]; nType = m[x + 2]; }
                if (m[x + 3] == 4 && m[x + 4] == 0 && tStream < 0) { tStream = m[x]; tOff = m[x + 1]; tType = m[x + 2]; }
            }
            if (pStream < 0) continue;

            for (int i = 0; i < vc; i++)
            {
                int a = (int)(vtxOff + vOff[pStream]) + i * str[pStream] + pOff;
                float x, y, z;
                if (pType == 14) { x = (float)BitConverter.ToHalf(m, a); y = (float)BitConverter.ToHalf(m, a + 2); z = (float)BitConverter.ToHalf(m, a + 4); }
                else { x = BitConverter.ToSingle(m, a); y = BitConverter.ToSingle(m, a + 4); z = BitConverter.ToSingle(m, a + 8); }
                vs.Append("v ").Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(F(z)).Append('\n');

                // The STORED normal — what the game shades with. Renders that recompute normals from
                // geometry are exactly how a shell full of stale normals looked correct offline.
                float nx = 0, ny = 0, nz = 0;
                if (nStream >= 0)
                {
                    int b = (int)(vtxOff + vOff[nStream]) + i * str[nStream] + nOff;
                    switch (nType)
                    {
                        case 2: case 3:
                            nx = BitConverter.ToSingle(m, b); ny = BitConverter.ToSingle(m, b + 4); nz = BitConverter.ToSingle(m, b + 8); break;
                        case 14:
                            nx = (float)BitConverter.ToHalf(m, b); ny = (float)BitConverter.ToHalf(m, b + 2); nz = (float)BitConverter.ToHalf(m, b + 4); break;
                        case 10:
                            nx = BitConverter.ToInt16(m, b) / 32767f; ny = BitConverter.ToInt16(m, b + 2) / 32767f; nz = BitConverter.ToInt16(m, b + 4) / 32767f; break;
                        case 8:
                            nx = m[b] / 255f * 2 - 1; ny = m[b + 1] / 255f * 2 - 1; nz = m[b + 2] / 255f * 2 - 1; break;
                    }
                }
                ns.Append("vn ").Append(F(nx)).Append(' ').Append(F(ny)).Append(' ').Append(F(nz)).Append('\n');

                float u = 0, v = 0;
                if (tStream >= 0)
                {
                    int b = (int)(vtxOff + vOff[tStream]) + i * str[tStream] + tOff;
                    if (tType is 13 or 14) { u = (float)BitConverter.ToHalf(m, b); v = (float)BitConverter.ToHalf(m, b + 2); }
                    else { u = BitConverter.ToSingle(m, b); v = BitConverter.ToSingle(m, b + 4); }
                }
                ts.Append("vt ").Append(F(u)).Append(' ').Append(F(v)).Append('\n');
            }

            fs.Append("g mesh").Append(mi).Append('\n');
            for (uint t = 0; t + 2 < idxCount; t += 3)
            {
                int p = (int)(idxOff + (startIdx + t) * 2);
                int a = U16(p) + baseVert, b = U16(p + 2) + baseVert, c = U16(p + 4) + baseVert;
                fs.Append("f ")
                  .Append(a).Append('/').Append(a).Append('/').Append(a).Append(' ')
                  .Append(b).Append('/').Append(b).Append('/').Append(b).Append(' ')
                  .Append(c).Append('/').Append(c).Append('/').Append(c).Append('\n');
            }
            baseVert += vc;
        }
        File.WriteAllText(path, vs.Append(ts).Append(ns).Append(fs).ToString());
    }

    private static string F(float f) => f.ToString("0.######", CultureInfo.InvariantCulture);
}
