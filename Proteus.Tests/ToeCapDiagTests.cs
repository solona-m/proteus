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
    /// Export the shell the GAME last built, and the body parts it was cut from, as .obj on the Desktop.
    /// <para/>
    /// For looking at the geometry directly instead of reading pixels off a screenshot — which is how a
    /// band across the thighs got diagnosed three different ways without anyone opening the mesh. The
    /// shell is what shipped, so a hole in it is a hole here; the sources are beside it because "is this
    /// missing from the shell or missing from the body?" is the first question worth answering.
    /// <para/>
    /// Does nothing until a build in game has filled %TEMP%\proteus-shell-dump.
    /// </summary>
    [Fact]
    public void ExportGameShellForInspection()
    {
        var dump = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        if (!Directory.Exists(dump)) return;

        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "OneDrive", "Desktop", "proteus-shell");
        Directory.CreateDirectory(outDir);

        // The BODY as the game draws it, under the shell, by its real name. Proteus keeps the user's own
        // models here — the bytes it read as its sources — so this is the skin, not a re-derivation of it.
        // Exported beside the shell because the only way to judge a band on a thigh is to see whether the
        // body has it too.
        var upstream = Environment.GetEnvironmentVariable("PROTEUS_UPSTREAM")
                    ?? @"E:\Penumbradt\Proteus\models\upstream";
        if (Directory.Exists(upstream))
            foreach (var mdl in Directory.GetFiles(upstream, "*.mdl"))
            {
                var leaf = Path.GetFileNameWithoutExtension(mdl);
                // Only what this character is actually wearing: the four e0000 skin parts. The rest of the
                // folder is every gear model Proteus has ever read.
                if (!leaf.Contains("e0000")) continue;
                string part = leaf.EndsWith("_top") ? "body_top"
                            : leaf.EndsWith("_dwn") ? "body_legs"
                            : leaf.EndsWith("_glv") ? "body_hands"
                            : leaf.EndsWith("_sho") ? "body_feet" : "body_" + leaf;
                try
                {
                    WriteObj(File.ReadAllBytes(mdl), Path.Combine(outDir, part + ".obj"));
                    File.Copy(mdl, Path.Combine(outDir, part + ".mdl"), overwrite: true);
                    o.WriteLine($"{part}.obj  <- {leaf}.mdl");
                }
                catch (Exception ex) { o.WriteLine($"{part}: {ex.Message}"); }
            }

        int written = 0;
        foreach (var mdl in Directory.GetFiles(dump, "host*.mdl").OrderBy(p => p))
        {
            var name = Path.GetFileNameWithoutExtension(mdl);
            try
            {
                var bytes = File.ReadAllBytes(mdl);
                WriteObj(bytes, Path.Combine(outDir, name + ".obj"));
                File.Copy(mdl, Path.Combine(outDir, name + ".mdl"), overwrite: true);
                written++;
                o.WriteLine($"{name}.obj  <- {mdl} ({bytes.Length / 1024} KB, "
                          + $"{File.GetLastWriteTime(mdl):yyyy-MM-dd HH:mm})");
            }
            catch (Exception ex)
            {
                o.WriteLine($"{name}: {ex.Message}");
            }
        }
        o.WriteLine($"wrote {written} model(s) to {outDir}");

        // Where the shell has an OPEN EDGE, by height. An edge used by one triangle is a border: some are
        // meant (each part ends somewhere, and the shell is cut to the overlay's coverage), but a hole
        // opened by removing surface shows as a cluster at one height that nothing explains.
        //
        // Worth having next to the export because it answers in numbers what a screenshot only suggests —
        // a smooth band on a thigh is either a hole or the art, and the two look identical at 4x zoom.
        // Against the SOURCE the shell was cut from, which is what makes the number mean something. Every
        // model has open edges where a part ends and where a UV seam splits it, and the body has those
        // too — so the question is never "are there open edges here" but "are there MORE than the body
        // had". A seam shows the same count in both. Surface that was removed shows only in the shell.
        var shell = Path.Combine(dump, "host0_shell.mdl");
        if (!File.Exists(shell)) return;
        var shellBands = OpenEdgesByHeight(File.ReadAllBytes(shell));

        var bodyBands = new SortedDictionary<int, int>();
        for (int i = 0; ; i++)
        {
            var body = Path.Combine(dump, $"host0_body{i}.mdl");
            if (!File.Exists(body)) break;
            foreach (var (band, n) in OpenEdgesByHeight(File.ReadAllBytes(body)))
                bodyBands[band] = bodyBands.GetValueOrDefault(band) + n;
        }

        o.WriteLine("open edges by height (5cm bands) — shell vs the bodies it was cut from:");
        foreach (var band in shellBands.Keys.Union(bodyBands.Keys).OrderBy(b => b))
        {
            int s = shellBands.GetValueOrDefault(band), b2 = bodyBands.GetValueOrDefault(band);
            if (s < 20 && b2 < 20) continue;
            o.WriteLine($"  y {band / 20f:F2}..{(band + 1) / 20f:F2} — shell {s}, bodies {b2}"
                      + (s > b2 + 20 ? $"   <-- {s - b2} MORE in the shell" : ""));
        }

        // Do coincident vertices AGREE about which way the surface faces?
        //
        // A shell is the body displaced along its normals, so two vertices at the same position with
        // different normals are pushed to different places — the surface tears open along the seam
        // between them without a single triangle being removed. The toe-cap join hit exactly this and
        // records it: "the join can be watertight and still show a line", measured there at 32 of 219
        // pairs disagreeing, worst 7.2 degrees, "which on a glossy stocking is exactly the seam".
        //
        // Measured on the BODY, because that is what the push is applied to.
        o.WriteLine("coincident vertices whose normals disagree, by height (body sources):");
        for (int i = 0; ; i++)
        {
            var body = Path.Combine(dump, $"host0_body{i}.mdl");
            if (!File.Exists(body)) break;
            foreach (var line in NormalSplits(File.ReadAllBytes(body)))
                o.WriteLine($"  body{i}: {line}");
        }
    }

    /// <summary>
    /// SCRATCH: for every foot skin vertex of the dumped bodies, how far the game-built shell's triangle
    /// above it stands off, per layer material, and whether that triangle's corners are skinned to other
    /// bones than the body vertex. Locates where the 0.05 mm push leaves the skin nearest the surface.
    /// </summary>
    [Fact]
    public void FootClearanceFromGameShell()
    {
        var dump = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        var shellPath = Path.Combine(dump, "host0_shell.mdl");
        if (!File.Exists(shellPath)) return;
        var sb = new StringBuilder();
        void W(string l) { o.WriteLine(l); sb.AppendLine(l); }

        var bp = new List<SecondSkinWriter.Vec3>();
        var bn = new List<SecondSkinWriter.Vec3>();
        var bw = new List<string>();
        for (int i = 0; File.Exists(Path.Combine(dump, $"host0_body{i}.mdl")); i++)
        {
            if (!SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(Path.Combine(dump, $"host0_body{i}.mdl")),
                    out var p, out _, out _, out var w, out var n)) continue;
            for (int v = 0; v < p.Length / 3; v++)
            {
                if (p[v * 3 + 1] > 0.15f) continue;
                bp.Add(new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]));
                bn.Add(new(n[v * 3], n[v * 3 + 1], n[v * 3 + 2]));
                bw.Add(v < w.Length && w[v].Length > 0 ? w[v].MaxBy(x => x.W).Bone : "?");
            }
        }
        W($"{bp.Count} foot skin vertices");

        W("##### GAME SHELL");
        Measure(File.ReadAllBytes(shellPath));

        // The same build replayed from the dump, so a fix can be measured without the game.
        var text = File.ReadAllLines(Path.Combine(dump, "host0_inputs.txt"));
        var specs = new List<SecondSkinWriter.SourceSpec>();
        for (int i = 0; File.Exists(Path.Combine(dump, $"host0_body{i}.mdl")); i++)
        {
            var line = text.First(l => l.StartsWith($"source[{i}] "));
            var ha = System.Text.RegularExpressions.Regex.Match(line, @"hiddenAttrs=(\S*)").Groups[1].Value;
            specs.Add(new SecondSkinWriter.SourceSpec(File.ReadAllBytes(Path.Combine(dump, $"host0_body{i}.mdl")),
                DropConnectors: line.Contains("dropRedundant=True"),
                HiddenAttributes: ha.Length == 0 ? null : ha.Split(',').ToHashSet()));
        }
        var layers = new List<SecondSkinLayer>();
        for (int i = 0; text.FirstOrDefault(l => l.StartsWith($"layer[{i}] ")) is { } line; i++)
        {
            var capP = Path.Combine(dump, $"host0_layer{i}_toecap.raw");
            var covP = Path.Combine(dump, $"host0_layer{i}_coverage.raw");
            byte[]? cap = !line.Contains("toeCap=none") && File.Exists(capP) ? File.ReadAllBytes(capP) : null;
            byte[]? cov = !line.Contains("coverage=none") && File.Exists(covP) ? File.ReadAllBytes(covP) : null;
            int cs = cap == null ? 0 : (int)Math.Round(Math.Sqrt(cap.Length));
            int vs = cov == null ? 0 : (int)Math.Round(Math.Sqrt(cov.Length));
            float F(string k) => float.Parse(System.Text.RegularExpressions.Regex.Match(line, k + @"=(\S+)").Groups[1].Value,
                                             CultureInfo.InvariantCulture);
            layers.Add(new SecondSkinLayer
            {
                MaterialName = System.Text.RegularExpressions.Regex.Match(line, @"material=(\S+)").Groups[1].Value,
                Coverage = cov, CoverageWidth = vs, CoverageHeight = vs,
                ToeCap = cap, ToeCapWidth = cs, ToeCapHeight = cs,
                ToeCapStrength = F("strength"), BustBridgeStrength = F("bustBridge"),
                NippleSmoothStrength = F("nippleSmooth"), CleftBridgeStrength = F("cleftBridge"),
                FoldSmoothStrength = F("smoothFold"),
            });
        }
        // The dump records whether this build had a base; an older dump without the line falls back to the
        // file being there, which can be a leftover from an earlier build.
        var basePath = Path.Combine(dump, "host0_base.mdl");
        var baseLine = text.FirstOrDefault(l => l.StartsWith("base="));
        bool hasBase = baseLine != null ? baseLine == "base=yes" : File.Exists(basePath);
        if (baseLine == null && hasBase) W("inputs.txt does not say whether this build had a base; using host0_base.mdl");
        if (hasBase && !File.Exists(basePath)) { W("inputs.txt names a base but host0_base.mdl is missing"); return; }
        var diag = new List<string>();
        var replay = SecondSkinWriter.Build(specs, layers, hasBase ? File.ReadAllBytes(basePath) : null,
            out _, diag.Add, CapSets());
        foreach (var l in diag.Where(l => l.Contains("raised") || l.Contains("clearance")))
            W("  diag: " + l);
        W("##### REPLAY");
        Measure(replay);

        // Beside the dump, which exists by now, rather than in any one session's scratch folder.
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "proteus-foot-clearance.txt"), sb.ToString());

        void Measure(byte[] shell) {
        foreach (var mat in new[] { "ril_c", "ril_d", "ril_e", "ril_f" })
        {
            if (!SecondSkinWriter.TryReadLod0Geometry(shell, out var p, out _, out var t, out var w, out _,
                    keepMaterial: m => m.Contains(mat))) continue;
            SecondSkinWriter.Vec3 P(int i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
            string Dom(int i) => i < w.Length && w[i].Length > 0 ? w[i].MaxBy(x => x.W).Bone : "?";
            const float cell = 0.004f;
            (int, int, int) C(float x, float y, float z) =>
                ((int)MathF.Floor(x / cell), (int)MathF.Floor(y / cell), (int)MathF.Floor(z / cell));
            var hash = new Dictionary<(int, int, int), List<int>>();
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                var a = P(t[k]); var b = P(t[k + 1]); var c = P(t[k + 2]);
                if (MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) > 0.16f) continue;
                var lo = C(MathF.Min(a.X, MathF.Min(b.X, c.X)), MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), MathF.Min(a.Z, MathF.Min(b.Z, c.Z)));
                var hi = C(MathF.Max(a.X, MathF.Max(b.X, c.X)), MathF.Max(a.Y, MathF.Max(b.Y, c.Y)), MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));
                for (int x = lo.Item1; x <= hi.Item1; x++)
                for (int y = lo.Item2; y <= hi.Item2; y++)
                for (int z = lo.Item3; z <= hi.Item3; z++)
                    (hash.TryGetValue((x, y, z), out var l) ? l : hash[(x, y, z)] = []).Add(k);
            }

            var hits = new List<(float H, SecondSkinWriter.Vec3 At, float Edge, bool Foreign, string Bones)>();
            for (int v = 0; v < bp.Count; v++)
            {
                var q = bp[v];
                if (!hash.TryGetValue(C(q.X, q.Y, q.Z), out var near)) continue;
                float best = float.MaxValue; (float H, SecondSkinWriter.Vec3 At, float Edge, bool Foreign, string Bones) bestHit = default;
                foreach (int k in near)
                {
                    var a = P(t[k]); var b = P(t[k + 1]); var c = P(t[k + 2]);
                    var e1 = new SecondSkinWriter.Vec3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                    var e2 = new SecondSkinWriter.Vec3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                    var fn = new SecondSkinWriter.Vec3(e1.Y * e2.Z - e1.Z * e2.Y, e1.Z * e2.X - e1.X * e2.Z, e1.X * e2.Y - e1.Y * e2.X);
                    float len = MathF.Sqrt(fn.X * fn.X + fn.Y * fn.Y + fn.Z * fn.Z);
                    if (len < 1e-12f) continue;
                    fn = new(fn.X / len, fn.Y / len, fn.Z / len);
                    if (fn.X * bn[v].X + fn.Y * bn[v].Y + fn.Z * bn[v].Z < 0) fn = new(-fn.X, -fn.Y, -fn.Z);
                    float h = (a.X - q.X) * fn.X + (a.Y - q.Y) * fn.Y + (a.Z - q.Z) * fn.Z;
                    if (h < -0.003f || h > 0.003f) continue;
                    var pq = new SecondSkinWriter.Vec3(q.X + fn.X * h, q.Y + fn.Y * h, q.Z + fn.Z * h);
                    bool inside = true;
                    foreach (var (u, uu) in new[] { (a, b), (b, c), (c, a) })
                    {
                        var ev = new SecondSkinWriter.Vec3(uu.X - u.X, uu.Y - u.Y, uu.Z - u.Z);
                        var qv = new SecondSkinWriter.Vec3(pq.X - u.X, pq.Y - u.Y, pq.Z - u.Z);
                        if ((ev.Y * qv.Z - ev.Z * qv.Y) * fn.X + (ev.Z * qv.X - ev.X * qv.Z) * fn.Y
                            + (ev.X * qv.Y - ev.Y * qv.X) * fn.Z < -1e-9f) { inside = false; break; }
                    }
                    if (!inside || MathF.Abs(h) >= best) continue;
                    best = MathF.Abs(h);
                    float edge = MathF.Max(Dist(a, b), MathF.Max(Dist(b, c), Dist(c, a)));
                    var doms = new[] { Dom(t[k]), Dom(t[k + 1]), Dom(t[k + 2]) };
                    bestHit = (h, q, edge, doms.Any(d => d != bw[v]), $"body {bw[v]} / tri {string.Join(",", doms)}");
                }
                if (best < float.MaxValue) hits.Add(bestHit);
            }

            // Cracks: foot vertices that nearly coincide without being the same point — a seam pair pulled
            // apart. Bucketed at 0.5 mm, compared against the neighbouring buckets.
            const float crackCell = 0.0005f;
            var buckets = new Dictionary<(int, int, int), List<SecondSkinWriter.Vec3>>();
            for (int i = 0; i < p.Length / 3; i++)
            {
                var q = P(i);
                if (q.Y > 0.15f) continue;
                var key = ((int)MathF.Floor(q.X / crackCell), (int)MathF.Floor(q.Y / crackCell), (int)MathF.Floor(q.Z / crackCell));
                (buckets.TryGetValue(key, out var l) ? l : buckets[key] = []).Add(q);
            }
            int cracks = 0; float worstCrack = 0f;
            foreach (var (key, list) in buckets)
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!buckets.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var other)) continue;
                    foreach (var a in list)
                    foreach (var b in other)
                    {
                        float d = Dist(a, b);
                        if (d <= 1e-6f || d > crackCell) continue;
                        cracks++; worstCrack = MathF.Max(worstCrack, d);
                    }
                }
            W($"=== {mat}: near-coincident foot vertex pairs (1e-6..0.5 mm apart): {cracks / 2}, widest {worstCrack * 1000:0.000} mm");
            W($"=== {mat}: {hits.Count} foot vertices under a triangle");
            float[] edges = [float.NegativeInfinity, 0f, 2e-5f, 4e-5f, 6e-5f, 2e-4f, 1e-3f, float.PositiveInfinity];
            for (int e = 0; e + 1 < edges.Length; e++)
            {
                var inBin = hits.Where(x => x.H >= edges[e] && x.H < edges[e + 1]).ToList();
                W($"  h {edges[e] * 1000,8:0.###}..{edges[e + 1] * 1000,-8:0.###} mm: {inBin.Count,6}  "
                  + $"(foreign skinning {inBin.Count(x => x.Foreign)}, median edge {(inBin.Count == 0 ? 0 : inBin.OrderBy(x => x.Edge).ElementAt(inBin.Count / 2).Edge * 1000):0.0} mm)");
            }
            foreach (var x in hits.OrderBy(x => x.H).Take(10))
                W($"  h {x.H * 1000,7:0.000} mm at ({x.At.X,7:0.0000} {x.At.Y,7:0.0000} {x.At.Z,7:0.0000}) "
                  + $"edge {x.Edge * 1000,5:0.0} mm {(x.Foreign ? "FOREIGN" : "       ")} {x.Bones}");
        }
        }

        static float Dist(SecondSkinWriter.Vec3 a, SecondSkinWriter.Vec3 b)
            => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    }

    /// <summary>
    /// SCRATCH: where "span the cleavage" leaves the chest nearest the skin at the 0.05 mm push. For every
    /// chest skin vertex of a dumped body, the height of the shell triangle above it — measured on the game's
    /// own shell, on a replay of it, and on a replay with the bridge switched off. For the replay, each hit
    /// also carries how far the bridge moved that triangle's corners (bridge-on against bridge-off, same
    /// indices) and how squarely the skin faces the bridge's axis: a lift along an axis the skin runs parallel
    /// to slides the vertex across the surface instead of away from it.
    /// <para/>
    /// Reads <c>%TEMP%\proteus-bust-dump</c> when it exists — a frozen copy, because <c>dotnet test</c> rebuilds
    /// into the game's load path and the game rewrites the live dump mid-session — else the live dump.
    /// </summary>
    [Fact]
    public void BustClearanceFromGameShell()
    {
        var frozen = Path.Combine(Path.GetTempPath(), "proteus-bust-dump");
        ClearanceFromGameShell(Directory.Exists(frozen) ? frozen : Path.Combine(Path.GetTempPath(), "proteus-shell-dump"),
                               1.0f, 1.5f, "proteus-bust-clearance.txt");
    }

    /// <summary>
    /// SCRATCH: the same measurement over the hips and crotch, on <c>%TEMP%\proteus-gen3-dump</c> — where the
    /// fold and cleft passes move the legs part. The "bridge off" replay turns those off too.
    /// </summary>
    /// <summary>SCRATCH: the chest measurement on <c>%TEMP%\proteus-gen3-dump</c>.</summary>
    [Fact]
    public void Gen3ChestClearanceFromGameShell()
    {
        var dump = Path.Combine(Path.GetTempPath(), "proteus-gen3-dump");
        if (Directory.Exists(dump)) ClearanceFromGameShell(dump, 1.05f, 1.35f, "proteus-gen3-chest-clearance.txt");
    }

    [Fact]
    public void FoldClearanceFromGameShell()
    {
        var dump = Path.Combine(Path.GetTempPath(), "proteus-gen3-dump");
        if (Directory.Exists(dump)) ClearanceFromGameShell(dump, 0.8f, 1.1f, "proteus-fold-clearance.txt");
    }

    private void ClearanceFromGameShell(string dump, float yLo, float yHi, string report)
    {
        int host = -1;
        for (int h = 0; File.Exists(Path.Combine(dump, $"host{h}_inputs.txt")); h++)
            if (File.ReadAllLines(Path.Combine(dump, $"host{h}_inputs.txt"))
                    .Any(l => l.StartsWith("layer[") && (!l.Contains("bustBridge=0 ") || !l.Contains("cleftBridge=0 ")
                                                         || !l.Contains("smoothFold=0"))))
            { host = h; break; }
        if (host < 0) return;
        string F0(string name) => Path.Combine(dump, $"host{host}_{name}");
        var sb = new StringBuilder();
        void W(string l) { o.WriteLine(l); sb.AppendLine(l); }
        W($"dump {dump}, host {host}");

        // Chest skin of the bodies the shell was cut from.
        var bp = new List<SecondSkinWriter.Vec3>();
        var bn = new List<SecondSkinWriter.Vec3>();
        var bw = new List<string>();
        var bwt = new List<Dictionary<string, float>>();   // the sample's full skinning, interpolated
        // Sampled ACROSS each skin triangle, not only at its corners. A shell face the bridge slid no longer
        // sits over the body vertices it was copied from, and a long flat face over a curved crease can pass
        // under the skin between vertices while clearing every one of them.
        const int Sub = 6;
        for (int i = 0; File.Exists(F0($"body{i}.mdl")); i++)
        {
            if (!SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(F0($"body{i}.mdl")),
                    out var p, out _, out var bt, out var w, out var n)) continue;
            string BDom(int v) => v < w.Length && w[v].Length > 0 ? w[v].MaxBy(x => x.W).Bone : "?";
            var seen = new HashSet<(int, int, int)>();
            for (int k = 0; k + 2 < bt.Length; k += 3)
            {
                int ia = bt[k], ib = bt[k + 1], ic = bt[k + 2];
                if (p[ia * 3 + 1] < yLo || p[ia * 3 + 1] > yHi) continue;
                for (int s = 0; s <= Sub; s++)
                for (int r = 0; r <= Sub - s; r++)
                {
                    float wb = s / (float)Sub, wc = r / (float)Sub, wa = 1f - wb - wc;
                    float x = wa * p[ia * 3] + wb * p[ib * 3] + wc * p[ic * 3];
                    float y = wa * p[ia * 3 + 1] + wb * p[ib * 3 + 1] + wc * p[ic * 3 + 1];
                    float z = wa * p[ia * 3 + 2] + wb * p[ib * 3 + 2] + wc * p[ic * 3 + 2];
                    // Shared edges and corners are sampled by every triangle that owns them; count each once.
                    if (!seen.Add(((int)MathF.Round(x * 1e5f), (int)MathF.Round(y * 1e5f), (int)MathF.Round(z * 1e5f))))
                        continue;
                    var nn = new SecondSkinWriter.Vec3(
                        wa * n[ia * 3] + wb * n[ib * 3] + wc * n[ic * 3],
                        wa * n[ia * 3 + 1] + wb * n[ib * 3 + 1] + wc * n[ic * 3 + 1],
                        wa * n[ia * 3 + 2] + wb * n[ib * 3 + 2] + wc * n[ic * 3 + 2]);
                    float nl = MathF.Sqrt(nn.X * nn.X + nn.Y * nn.Y + nn.Z * nn.Z);
                    if (nl < 1e-6f) continue;
                    bp.Add(new(x, y, z));
                    bn.Add(new(nn.X / nl, nn.Y / nl, nn.Z / nl));
                    bw.Add(wa >= wb && wa >= wc ? BDom(ia) : wb >= wc ? BDom(ib) : BDom(ic));
                    var mix = new Dictionary<string, float>();
                    foreach (var (vi, f) in new[] { (ia, wa), (ib, wb), (ic, wc) })
                        if (f > 0f && vi < w.Length)
                            foreach (var (bone, bwv) in w[vi]) mix[bone] = mix.GetValueOrDefault(bone) + f * bwv;
                    bwt.Add(mix);
                }
            }
        }
        W($"{bp.Count} skin sample points (y {yLo:0.00}..{yHi:0.00}, {Sub} per edge)");

        var text = File.ReadAllLines(F0("inputs.txt"));
        var specs = new List<SecondSkinWriter.SourceSpec>();
        for (int i = 0; File.Exists(F0($"body{i}.mdl")); i++)
        {
            var line = text.First(l => l.StartsWith($"source[{i}] "));
            var ha = System.Text.RegularExpressions.Regex.Match(line, @"hiddenAttrs=(\S*)").Groups[1].Value;
            // Shape keys the game baked into this source — a YAB torso carries its size in them, and a replay
            // without them is a different body.
            var sh = System.Text.RegularExpressions.Regex.Match(line, @"shapes=(\S*)").Groups[1].Value;
            specs.Add(new SecondSkinWriter.SourceSpec(File.ReadAllBytes(F0($"body{i}.mdl")),
                EnabledShapes: sh.Length == 0 ? null : sh.Split(',').ToHashSet(),
                DropConnectors: line.Contains("dropRedundant=True"),
                HiddenAttributes: ha.Length == 0 ? null : ha.Split(',').ToHashSet()));
        }
        List<SecondSkinLayer> Layers(bool bridge, bool trim = true)
        {
            var layers = new List<SecondSkinLayer>();
            for (int i = 0; text.FirstOrDefault(l => l.StartsWith($"layer[{i}] ")) is { } line; i++)
            {
                var capP = F0($"layer{i}_toecap.raw");
                var covP = F0($"layer{i}_coverage.raw");
                byte[]? cap = !line.Contains("toeCap=none") && File.Exists(capP) ? File.ReadAllBytes(capP) : null;
                byte[]? cov = trim && !line.Contains("coverage=none") && File.Exists(covP) ? File.ReadAllBytes(covP) : null;
                int cs = cap == null ? 0 : (int)Math.Round(Math.Sqrt(cap.Length));
                int vs = cov == null ? 0 : (int)Math.Round(Math.Sqrt(cov.Length));
                float F(string k) => float.Parse(System.Text.RegularExpressions.Regex.Match(line, k + @"=(\S+)").Groups[1].Value,
                                                 CultureInfo.InvariantCulture);
                layers.Add(new SecondSkinLayer
                {
                    MaterialName = System.Text.RegularExpressions.Regex.Match(line, @"material=(\S+)").Groups[1].Value,
                    Coverage = cov, CoverageWidth = vs, CoverageHeight = vs,
                    ToeCap = cap, ToeCapWidth = cs, ToeCapHeight = cs,
                    ToeCapStrength = F("strength"), BustBridgeStrength = bridge ? F("bustBridge") : 0f,
                    NippleSmoothStrength = F("nippleSmooth"),
                    CleftBridgeStrength = bridge ? F("cleftBridge") : 0f,
                    FoldSmoothStrength = bridge ? F("smoothFold") : 0f,
                });
            }
            return layers;
        }
        byte[]? baseModel = text.Contains("base=yes") ? File.ReadAllBytes(F0("base.mdl")) : null;
        var diagOn = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        SecondSkinWriter.TraceSpanNode = p => p.Z > 0.010f && p.Z < 0.014f && MathF.Abs(p.X) < 0.003f && p.Y > 0.850f && p.Y < 0.875f;
        var on = SecondSkinWriter.Build(specs, Layers(true), baseModel, out var onStats, diagOn.Add, CapSets());
        SecondSkinWriter.TraceSpanNode = null;
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), Path.ChangeExtension(report, ".diag.txt")),
            diagOn.Prepend($"stats: {onStats}"));
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), Path.ChangeExtension(report, ".replay.mdl")), on);
        long onMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var off = SecondSkinWriter.Build(specs, Layers(false), baseModel, out _, _ => { }, CapSets());
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), Path.ChangeExtension(report, ".replay-off.mdl")), off);
        W($"build time: bridge on {onMs} ms, bridge off {clock.ElapsedMilliseconds} ms");
        foreach (var l in diagOn.Where(l => l.StartsWith("bust bridge: axis") || l.Contains("outside the body")))
            W("  diag: " + l);
        var axisM = diagOn.Select(l => System.Text.RegularExpressions.Regex.Match(l,
                        @"axis \(([-\d.]+),([-\d.]+),([-\d.]+)\)")).FirstOrDefault(m => m.Success);
        var ax = axisM == null ? new SecondSkinWriter.Vec3(0, 0, 1)
            : new SecondSkinWriter.Vec3(float.Parse(axisM.Groups[1].Value, CultureInfo.InvariantCulture),
                                        float.Parse(axisM.Groups[2].Value, CultureInfo.InvariantCulture),
                                        float.Parse(axisM.Groups[3].Value, CultureInfo.InvariantCulture));

        var materials = text.Where(l => l.StartsWith("layer["))
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"material=/mt_\w+?_(\w+)\.mtrl").Groups[1].Value)
            .Where(s => s.Length > 0).ToList();

        // The replay must BE the game's shell before any of its numbers mean anything.
        foreach (var mat in materials)
        {
            if (!SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(F0("shell.mdl")), out var pg, out _, out _,
                    out _, out _, keepMaterial: m => m.Contains(mat)) ||
                !SecondSkinWriter.TryReadLod0Geometry(on, out var pr, out _, out _, out _, out _,
                    keepMaterial: m => m.Contains(mat))) continue;
            float worst = 0f;
            if (pg.Length == pr.Length)
                for (int i = 0; i < pg.Length; i++) worst = MathF.Max(worst, MathF.Abs(pg[i] - pr[i]));
            W($"{mat}: game shell {pg.Length / 3} vertices, replay {pr.Length / 3}"
              + (pg.Length == pr.Length ? $", worst coordinate difference {worst * 1000:0.0000} mm" : " — DIFFERENT COUNTS"));
        }

        // DID THE BRIDGE CARRY A VERTEX THROUGH THE SURFACE? The path from each vertex's bridge-off position to
        // its bridge-on one, tested against the bridge-off shell's own triangles. Both shells have every shape
        // key and body pass baked in, so this cannot be fooled the way sampling a raw body model can. A path
        // that crosses a face that isn't its own is a lift into the body — under a breast, into its underside.
        // The surface is an UNTRIMMED bridge-off shell: every face of the shaped body, not just the ones this
        // garment covers. A coverage-trimmed shell is missing faces — inside a cleavage the cups leave bare —
        // and a path through a missing face counts one crossing short, so "ends inside" comes out backwards.
        var full = SecondSkinWriter.Build(specs, Layers(false, trim: false), baseModel, out _, _ => { }, CapSets());
        foreach (var mat in materials)
        {
            if (!SecondSkinWriter.TryReadLod0Geometry(off, out var po, out _, out _, out _, out _,
                    keepMaterial: m => m.Contains(mat)) ||
                !SecondSkinWriter.TryReadLod0Geometry(on, out var pn, out _, out _, out _, out _,
                    keepMaterial: m => m.Contains(mat)) || po.Length != pn.Length ||
                !SecondSkinWriter.TryReadLod0Geometry(full, out var pf, out _, out var to, out _, out _,
                    keepMaterial: m => m.Contains(mat))) continue;
            SecondSkinWriter.Vec3 O(int i) => new(po[i * 3], po[i * 3 + 1], po[i * 3 + 2]);
            SecondSkinWriter.Vec3 N(int i) => new(pn[i * 3], pn[i * 3 + 1], pn[i * 3 + 2]);
            SecondSkinWriter.Vec3 Fp(int i) => new(pf[i * 3], pf[i * 3 + 1], pf[i * 3 + 2]);
            // The writer's own inside test, so the two cannot disagree about what "inside" means.
            var body = new SecondSkinWriter.BodyWinding(
                Enumerable.Range(0, pf.Length / 3).Select(Fp).ToArray(), to, Enumerable.Range(0, pf.Length / 3).ToArray());
            // Does the measure itself read this body correctly? The chest centre is inside; a point well in front
            // of the apexes, and one far off to the side, are not.
            W($"{mat}: winding sanity — chest centre (0,1.22,0.02) {body.Winding(new(0f, 1.22f, 0.02f)):0.00}, "
              + $"in front (0,1.22,0.30) {body.Winding(new(0f, 1.22f, 0.30f)):0.00}, "
              + $"off to the side (0.6,1.22,0) {body.Winding(new(0.6f, 1.22f, 0f)):0.00}");

            // Same-position copies (UV seams) share a path; count each position once.
            var seenPos = new HashSet<(int, int, int)>();
            var inside = new List<(float W, float Travel, SecondSkinWriter.Vec3 At, SecondSkinWriter.Vec3 End)>();
            int movedPaths = 0;
            for (int i = 0; i < po.Length / 3; i++)
            {
                var s0 = O(i); var s1 = N(i);
                float travel = Dist(s0, s1);
                if (travel < 0.002f) continue;   // 2 mm: well past the clearance changes, into real displacement
                if (!seenPos.Add(((int)MathF.Round(s0.X * 1e5f), (int)MathF.Round(s0.Y * 1e5f), (int)MathF.Round(s0.Z * 1e5f)))) continue;
                movedPaths++;
                float w = body.Winding(s1);
                if (w > 0.5f) inside.Add((w, travel, s0, s1));
            }
            W($"{mat}: {movedPaths} position(s) the bridge moved 2 mm or more; {inside.Count} of them END inside "
              + $"the body (winding > 0.5)");

            // THE WAIST, BACK: how far the spans moved each shell vertex around the torso/legs overlap, in 5 mm
            // height bands. A step between the two parts shows as one band moving and the next not.
            // Split by whether a TRIANGLE uses the vertex. A shape key redirects the triangles to replacement
            // vertices appended to the mesh, so a vertex no triangle draws can move all it likes and show nothing.
            SecondSkinWriter.TryReadLod0Geometry(on, out _, out _, out var tn, out _, out _, keepMaterial: m => m.Contains(mat));
            var drawn = new HashSet<int>(tn);
            var waist = new SortedDictionary<(int Band, bool Drawn), (int N, float Max, double Sum)>();
            for (int i = 0; i < po.Length / 3; i++)
            {
                var s0 = O(i);
                if (s0.Y < 0.80f || s0.Y > 1.10f || s0.Z > -0.02f) continue;
                float m = Dist(s0, N(i));
                var key = ((int)MathF.Floor(s0.Y * 50) * 4, drawn.Contains(i));
                var cur = waist.TryGetValue(key, out var had2) ? had2 : (0, 0f, 0.0);
                waist[key] = (cur.Item1 + 1, MathF.Max(cur.Item2, m), cur.Item3 + m);
            }
            W($"{mat}: back of the hips, movement by height (on vs off), drawn vertices vs vertices no triangle uses:");
            foreach (var (key, v) in waist)
                W($"  y {key.Band / 200f:0.000} {(key.Drawn ? "drawn  " : "UNDRAWN")}: {v.N,4} vertices, "
                  + $"mean {v.Sum / v.N * 1000,6:0.000} mm, max {v.Max * 1000,6:0.000} mm");

            // The same table for the GAME's shell against the replay with the spans off — what shipped.
            if (SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(F0("shell.mdl")), out var pgm, out _, out _, out _, out _,
                    keepMaterial: m => m.Contains(mat)) && pgm.Length == po.Length)
            {
                var game = new SortedDictionary<int, (int N, float Max, double Sum)>();
                for (int i = 0; i < po.Length / 3; i++)
                {
                    var s0 = O(i);
                    if (s0.Y < 0.80f || s0.Y > 1.10f || s0.Z > -0.02f) continue;
                    float m = Dist(s0, new SecondSkinWriter.Vec3(pgm[i * 3], pgm[i * 3 + 1], pgm[i * 3 + 2]));
                    int band = (int)MathF.Floor(s0.Y * 50) * 4;
                    var cur = game.TryGetValue(band, out var had3) ? had3 : (0, 0f, 0.0);
                    game[band] = (cur.Item1 + 1, MathF.Max(cur.Item2, m), cur.Item3 + m);
                }
                W($"{mat}: back of the hips, GAME shell vs the spans-off replay:");
                foreach (var (band, v) in game)
                    W($"  y {band / 200f:0.000}: {v.N,4} vertices, mean {v.Sum / v.N * 1000,6:0.000} mm, max {v.Max * 1000,6:0.000} mm");
            }
            else W($"{mat}: game shell vertex count differs from the replay — no per-vertex comparison");

            // Are the vertices that end inside ordinary vertices of the torso, or shape-key vertices? A shape key
            // redirects triangles to extra vertices appended to the mesh; if these are those, the solve saw them
            // with no triangles attached.
            if (SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(F0("body0.mdl")), out var rawP, out _, out var rawT,
                    out _, out _))
            {
                var referenced = new HashSet<(int, int, int)>();
                foreach (int vi in rawT)
                    referenced.Add(((int)MathF.Round(rawP[vi * 3] * 1e4f), (int)MathF.Round(rawP[vi * 3 + 1] * 1e4f),
                                    (int)MathF.Round(rawP[vi * 3 + 2] * 1e4f)));
                int onRaw = 0;
                foreach (var c in inside)
                {
                    // The shell vertex is the skin pushed 0.05 mm, so match at 0.1 mm.
                    var q = c.At;
                    bool hit = false;
                    for (int dx = -1; dx <= 1 && !hit; dx++)
                    for (int dy = -1; dy <= 1 && !hit; dy++)
                    for (int dz = -1; dz <= 1 && !hit; dz++)
                        hit = referenced.Contains(((int)MathF.Round(q.X * 1e4f) + dx, (int)MathF.Round(q.Y * 1e4f) + dy,
                                                   (int)MathF.Round(q.Z * 1e4f) + dz));
                    if (hit) onRaw++;
                }
                W($"  of the {inside.Count} ending inside, {onRaw} start on a vertex the UNSHAPED torso's triangles use");
            }

            // The same end points against each dumped BODY source on its own, mesh by mesh — the solve runs per
            // mesh, so a breast in a different mesh from the chest wall it lifts is invisible to it.
            for (int bi = 0; File.Exists(F0($"body{bi}.mdl")); bi++)
            {
                var bytes = File.ReadAllBytes(F0($"body{bi}.mdl"));
                var names = SecondSkinWriter.DrawnMaterialNames(bytes);
                foreach (var name in names)
                {
                    if (!SecondSkinWriter.TryReadLod0Geometry(bytes, out var bpp, out _, out var btt, out _, out _,
                            keepMaterial: m => m == name) || btt.Length == 0) continue;
                    var bodyOnly = new SecondSkinWriter.BodyWinding(
                        Enumerable.Range(0, bpp.Length / 3).Select(k => new SecondSkinWriter.Vec3(bpp[k * 3], bpp[k * 3 + 1], bpp[k * 3 + 2])).ToArray(),
                        btt, Enumerable.Range(0, bpp.Length / 3).ToArray());
                    var sample = inside.Take(6).Select(c => bodyOnly.Winding(c.End)).ToList();
                    if (sample.Count == 0) continue;
                    W($"  body{bi} {name}: {btt.Length / 3} tris, winding at the first ends "
                      + string.Join(" ", sample.Select(s => s.ToString("0.00"))));
                }
            }
            foreach (var c in inside.OrderByDescending(c => c.Travel).Take(15))
                W($"  winding {c.W:0.00} after {c.Travel * 1000,5:0.0} mm, from ({c.At.X,7:0.0000} {c.At.Y,7:0.0000} {c.At.Z,7:0.0000}) "
                  + $"to ({c.End.X,7:0.0000} {c.End.Y,7:0.0000} {c.End.Z,7:0.0000})");
        }

        W("##### GAME SHELL");
        Measure(File.ReadAllBytes(F0("shell.mdl")), off);
        W("##### REPLAY, bridge on");
        Measure(on, off);
        W("##### REPLAY, bridge off");
        Measure(off, null);

        File.WriteAllText(Path.Combine(Path.GetTempPath(), report), sb.ToString());

        void Measure(byte[] shell, byte[]? unmoved)
        {
            foreach (var mat in materials)
            {
                if (!SecondSkinWriter.TryReadLod0Geometry(shell, out var p, out _, out var t, out var sw, out _,
                        keepMaterial: m => m.Contains(mat))) continue;
                string Dom(int i) => i < sw.Length && sw[i].Length > 0 ? sw[i].MaxBy(x => x.W).Bone : "?";
                float[]? q0 = null;
                if (unmoved != null && SecondSkinWriter.TryReadLod0Geometry(unmoved, out var pu, out _, out _,
                        out _, out _, keepMaterial: m => m.Contains(mat)) && pu.Length == p.Length) q0 = pu;
                SecondSkinWriter.Vec3 P(int i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
                float Moved(int i) => q0 == null ? float.NaN
                    : MathF.Sqrt((p[i * 3] - q0[i * 3]) * (p[i * 3] - q0[i * 3])
                               + (p[i * 3 + 1] - q0[i * 3 + 1]) * (p[i * 3 + 1] - q0[i * 3 + 1])
                               + (p[i * 3 + 2] - q0[i * 3 + 2]) * (p[i * 3 + 2] - q0[i * 3 + 2]));
                const float cell = 0.004f;
                (int, int, int) C(float x, float y, float z) =>
                    ((int)MathF.Floor(x / cell), (int)MathF.Floor(y / cell), (int)MathF.Floor(z / cell));
                var hash = new Dictionary<(int, int, int), List<int>>();
                for (int k = 0; k + 2 < t.Length; k += 3)
                {
                    var a = P(t[k]); var b = P(t[k + 1]); var c = P(t[k + 2]);
                    if (MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) < yLo - 0.01f || MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) > yHi + 0.01f) continue;
                    var lo = C(MathF.Min(a.X, MathF.Min(b.X, c.X)), MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), MathF.Min(a.Z, MathF.Min(b.Z, c.Z)));
                    var hi = C(MathF.Max(a.X, MathF.Max(b.X, c.X)), MathF.Max(a.Y, MathF.Max(b.Y, c.Y)), MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));
                    for (int x = lo.Item1; x <= hi.Item1; x++)
                    for (int y = lo.Item2; y <= hi.Item2; y++)
                    for (int z = lo.Item3; z <= hi.Item3; z++)
                        (hash.TryGetValue((x, y, z), out var l) ? l : hash[(x, y, z)] = []).Add(k);
                }

                // How differently the skin and the shell above it are skinned: half the summed absolute weight
                // difference over every bone (0 = identical, 1 = no bone in common), and the difference in weight
                // on the breast bones alone. A bone translation T moves the two apart by roughly that much of T.
                (float Tv, float Mune) Mismatch(int v, int k, SecondSkinWriter.Vec3 at)
                {
                    var a = P(t[k]); var b = P(t[k + 1]); var c = P(t[k + 2]);
                    float Area(SecondSkinWriter.Vec3 x, SecondSkinWriter.Vec3 y, SecondSkinWriter.Vec3 z)
                    {
                        var u = new SecondSkinWriter.Vec3(y.X - x.X, y.Y - x.Y, y.Z - x.Z);
                        var s = new SecondSkinWriter.Vec3(z.X - x.X, z.Y - x.Y, z.Z - x.Z);
                        var cr = new SecondSkinWriter.Vec3(u.Y * s.Z - u.Z * s.Y, u.Z * s.X - u.X * s.Z, u.X * s.Y - u.Y * s.X);
                        return MathF.Sqrt(cr.X * cr.X + cr.Y * cr.Y + cr.Z * cr.Z);
                    }
                    float total = Area(a, b, c);
                    if (total < 1e-12f) return (float.NaN, float.NaN);
                    float fa = Area(at, b, c) / total, fb = Area(a, at, c) / total, fc = 1f - fa - fb;
                    var shellMix = new Dictionary<string, float>();
                    foreach (var (vi, f) in new[] { (t[k], fa), (t[k + 1], fb), (t[k + 2], fc) })
                        if (vi < sw.Length)
                            foreach (var (bone, bwv) in sw[vi]) shellMix[bone] = shellMix.GetValueOrDefault(bone) + f * bwv;
                    float tv = 0f, mune = 0f;
                    foreach (var bone in bwt[v].Keys.Union(shellMix.Keys))
                    {
                        float d = bwt[v].GetValueOrDefault(bone) - shellMix.GetValueOrDefault(bone);
                        tv += MathF.Abs(d);
                        if (bone.StartsWith("j_mune")) mune += d;
                    }
                    return (tv * 0.5f, mune);
                }

                var hits = new List<(float H, int V, float Move, float Edge, string Tri, float Tv, float Mune)>();
                for (int v = 0; v < bp.Count; v++)
                {
                    var q = bp[v];
                    if (!hash.TryGetValue(C(q.X, q.Y, q.Z), out var near)) continue;
                    float best = float.MaxValue; (float H, int V, float Move, float Edge, string Tri, float Tv, float Mune) bestHit = default;
                    foreach (int k in near)
                    {
                        var a = P(t[k]); var b = P(t[k + 1]); var c = P(t[k + 2]);
                        var e1 = new SecondSkinWriter.Vec3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                        var e2 = new SecondSkinWriter.Vec3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                        var fn = new SecondSkinWriter.Vec3(e1.Y * e2.Z - e1.Z * e2.Y, e1.Z * e2.X - e1.X * e2.Z, e1.X * e2.Y - e1.Y * e2.X);
                        float len = MathF.Sqrt(fn.X * fn.X + fn.Y * fn.Y + fn.Z * fn.Z);
                        if (len < 1e-12f) continue;
                        fn = new(fn.X / len, fn.Y / len, fn.Z / len);
                        if (fn.X * bn[v].X + fn.Y * bn[v].Y + fn.Z * bn[v].Z < 0) fn = new(-fn.X, -fn.Y, -fn.Z);
                        float h = (a.X - q.X) * fn.X + (a.Y - q.Y) * fn.Y + (a.Z - q.Z) * fn.Z;
                        if (h < -0.003f || h > 0.003f) continue;
                        var pq = new SecondSkinWriter.Vec3(q.X + fn.X * h, q.Y + fn.Y * h, q.Z + fn.Z * h);
                        bool inside = true;
                        foreach (var (u, uu) in new[] { (a, b), (b, c), (c, a) })
                        {
                            var ev = new SecondSkinWriter.Vec3(uu.X - u.X, uu.Y - u.Y, uu.Z - u.Z);
                            var qv = new SecondSkinWriter.Vec3(pq.X - u.X, pq.Y - u.Y, pq.Z - u.Z);
                            if ((ev.Y * qv.Z - ev.Z * qv.Y) * fn.X + (ev.Z * qv.X - ev.X * qv.Z) * fn.Y
                                + (ev.X * qv.Y - ev.Y * qv.X) * fn.Z < -1e-9f) { inside = false; break; }
                        }
                        if (!inside || MathF.Abs(h) >= best) continue;
                        best = MathF.Abs(h);
                        float edge = MathF.Max(Dist(a, b), MathF.Max(Dist(b, c), Dist(c, a)));
                        float move = MathF.Max(Moved(t[k]), MathF.Max(Moved(t[k + 1]), Moved(t[k + 2])));
                        var (tv, mune) = Mismatch(v, k, pq);
                        bestHit = (h, v, move, edge, $"{Dom(t[k])},{Dom(t[k + 1])},{Dom(t[k + 2])}", tv, mune);
                    }
                    if (best < float.MaxValue) hits.Add(bestHit);
                }

                W($"=== {mat}: {hits.Count} chest vertices under a triangle");
                float[] edges = [float.NegativeInfinity, 0f, 2e-5f, 4e-5f, 6e-5f, 2e-4f, 1e-3f, float.PositiveInfinity];
                for (int e = 0; e + 1 < edges.Length; e++)
                {
                    var inBin = hits.Where(x => x.H >= edges[e] && x.H < edges[e + 1]).ToList();
                    string moved = q0 == null || inBin.Count == 0 ? ""
                        : $", under a bridge-moved face (>0.01 mm) {inBin.Count(x => x.Move > 1e-5f)}";
                    W($"  h {edges[e] * 1000,8:0.###}..{edges[e + 1] * 1000,-8:0.###} mm: {inBin.Count,6}{moved}");
                }
                // Skinning mismatch between the skin and the shell right above it, within 1 mm. A copied shell
                // matches its own skin exactly; a face the bridge slid carries the weights of where it came from.
                var close = hits.Where(x => x.H < 1e-3f && !float.IsNaN(x.Tv)).ToList();
                float[] tvEdges = [0f, 0.02f, 0.05f, 0.1f, 0.2f, 0.4f, 1.01f];
                W($"  skinning mismatch (half summed |dw|) for the {close.Count} within 1 mm:");
                for (int e = 0; e + 1 < tvEdges.Length; e++)
                {
                    var inBin = close.Where(x => x.Tv >= tvEdges[e] && x.Tv < tvEdges[e + 1]).ToList();
                    W($"    {tvEdges[e],4:0.00}..{tvEdges[e + 1],-4:0.00}: {inBin.Count,6}"
                      + (inBin.Count == 0 ? "" : $", worst breast-bone difference {inBin.Max(x => MathF.Abs(x.Mune)):0.000}"));
                }

                if (q0 != null)
                {
                    // Faces moved a millimetre or more: a hair's move leaks onto the unshaped-body artefacts at the waist
                    // and would count them.
                    var through = close.Where(x => x.H < 0f && x.Move > 0.001f).ToList();
                    W($"  through under MOVED faces: {through.Count} — deeper than 1mm {through.Count(x => x.H < -0.001f)}, "
                      + $"0.2-1mm {through.Count(x => x.H < -0.0002f && x.H >= -0.001f)}, under 0.2mm {through.Count(x => x.H >= -0.0002f)}");
                    foreach (var x in through.OrderBy(x => x.H).Take(12))
                    {
                        var q = bp[x.V];
                        W($"    DEEPEST h {x.H * 1000,7:0.000} mm at ({q.X,7:0.0000} {q.Y,7:0.0000} {q.Z,7:0.0000}) edge {x.Edge * 1000,5:0.0} mm face moved {x.Move * 1000,6:0.000} mm");
                    }
                }

                // Through the shell, or the closest with a real skinning mismatch — a positive bind-pose clearance
                // closes once the bones move under a face that does not follow them.
                foreach (var x in close.Where(x => x.H < 2e-5f || x.Tv >= 0.05f)
                                       .OrderByDescending(x => x.Tv / MathF.Max(x.H, 1e-5f)).Take(30))
                {
                    var q = bp[x.V]; var n = bn[x.V];
                    float facing = MathF.Abs(n.X * ax.X + n.Y * ax.Y + n.Z * ax.Z);
                    W($"  h {x.H * 1000,7:0.000} mm at ({q.X,7:0.0000} {q.Y,7:0.0000} {q.Z,7:0.0000}) "
                      + $"|n.axis| {facing:0.00} edge {x.Edge * 1000,5:0.0} mm"
                      + (q0 == null ? "" : $" face moved {x.Move * 1000,6:0.000} mm")
                      + $" mismatch {x.Tv:0.000} breast {x.Mune,6:0.000}"
                      + $" body {bw[x.V]} / tri {x.Tri}");
                }
            }
        }

        static float Dist(SecondSkinWriter.Vec3 a, SecondSkinWriter.Vec3 b)
            => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    }

    /// <summary>
    /// SCRATCH: a cross-section of the back of the hips, per height — how far back the cheeks reach (the most
    /// negative z anywhere) against how far back the cleft's floor sits (the least negative z on the midline),
    /// for the spans-on and spans-off replays of <see cref="FoldClearanceFromGameShell"/>.
    /// </summary>
    [Fact]
    public void CleftCrossSection()
    {
        var on = Path.Combine(Path.GetTempPath(), "proteus-fold-clearance.replay.mdl");
        var off = Path.Combine(Path.GetTempPath(), "proteus-fold-clearance.replay-off.mdl");
        if (!File.Exists(on) || !File.Exists(off)) return;
        var sb = new System.Text.StringBuilder();
        float[]? P(string f) => SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(f), out var p, out _, out var t,
            out _, out _, keepMaterial: m => m.Contains("ril_")) ? DrawnOnly(p, t) : null;
        static float[] DrawnOnly(float[] p, int[] t)
        {
            var used = new bool[p.Length / 3];
            foreach (int i in t) used[i] = true;
            var outP = new List<float>();
            for (int v = 0; v < used.Length; v++)
                if (used[v]) outP.AddRange([p[v * 3], p[v * 3 + 1], p[v * 3 + 2]]);
            return outP.ToArray();
        }
        var pOn = P(on); var pOff = P(off);
        if (pOn is null || pOff is null) return;
        sb.AppendLine("y      | OFF: cheek z  midline z  depth | ON: cheek z  midline z  depth   (mm)");
        for (float y = 0.78f; y < 1.08f; y += 0.01f)
        {
            (float cheek, float mid) Cut(float[] p)
            {
                float cheek = 0f, mid = float.NegativeInfinity;
                for (int v = 0; v < p.Length / 3; v++)
                {
                    float x = p[v * 3], vy = p[v * 3 + 1], z = p[v * 3 + 2];
                    if (vy < y || vy >= y + 0.01f || z >= -0.02f) continue;
                    if (MathF.Abs(x) < 0.15f) cheek = MathF.Min(cheek, z);
                    if (MathF.Abs(x) < 0.006f) mid = MathF.Max(mid, z);
                }
                return (cheek, mid);
            }
            var a = Cut(pOff); var b = Cut(pOn);
            sb.AppendLine($"{y:0.00}   | {a.cheek * 1000,8:0.0} {a.mid * 1000,9:0.0} {(a.mid - a.cheek) * 1000,6:0.0} | {b.cheek * 1000,8:0.0} {b.mid * 1000,9:0.0} {(b.mid - b.cheek) * 1000,6:0.0}");
        }
        sb.AppendLine();
        sb.AppendLine("CHEST y | OFF: apex z  midline z  depth | ON: apex z  midline z  depth   (mm)");
        for (float y = 1.08f; y < 1.30f; y += 0.01f)
        {
            (float apex, float mid) Cut(float[] p)
            {
                float apex = 0f, mid = float.PositiveInfinity;
                for (int v = 0; v < p.Length / 3; v++)
                {
                    float x = p[v * 3], vy = p[v * 3 + 1], z = p[v * 3 + 2];
                    if (vy < y || vy >= y + 0.01f || z <= 0.02f) continue;
                    if (MathF.Abs(x) < 0.15f) apex = MathF.Max(apex, z);
                    if (MathF.Abs(x) < 0.006f) mid = MathF.Min(mid, z);
                }
                return (apex, mid);
            }
            var a = Cut(pOff); var b = Cut(pOn);
            sb.AppendLine($"{y:0.00}    | {a.apex * 1000,8:0.0} {a.mid * 1000,9:0.0} {(a.apex - a.mid) * 1000,6:0.0} | {b.apex * 1000,8:0.0} {b.mid * 1000,9:0.0} {(b.apex - b.mid) * 1000,6:0.0}");
        }

        // THE CROTCH FROM BELOW: in 2mm slices front to back, the LOWEST shell y (mm above 0.84) in 1mm columns across
        // the midline. A notch rising into the labia shows as the middle columns sitting higher than their neighbours.
        sb.AppendLine();
        sb.AppendLine("CROTCH UNDERSIDE (lowest shell y, mm above 0.840, per 1mm column of x from -8 to +8)");
        foreach (var (label, pp) in new[] { ("OFF", pOff), ("ON ", pOn) })
            for (float zz = -0.004f; zz <= 0.016f; zz += 0.002f)
            {
                var cols = new float[17];
                Array.Fill(cols, float.NaN);
                for (int v = 0; v < pp.Length / 3; v++)
                {
                    float x = pp[v * 3], y = pp[v * 3 + 1], z = pp[v * 3 + 2];
                    if (MathF.Abs(z - zz) > 0.001f || y < 0.835f || y > 0.885f) continue;
                    int c = (int)MathF.Round(x * 1000f) + 8;
                    if (c < 0 || c >= cols.Length) continue;
                    if (float.IsNaN(cols[c]) || y < cols[c]) cols[c] = y;
                }
                sb.AppendLine($"  {label} z {zz * 1000,5:0}: " + string.Join(" ", cols.Select(y => float.IsNaN(y) ? "  -  " : $"{(y - 0.84f) * 1000,5:0.0}")));
            }

        // THE SIDE PROFILE under the breast: front-most z per 5mm of height, over the whole width and in columns, ON vs
        // OFF, with the second difference of ON (negative = convex, a lump; positive = concave).
        sb.AppendLine();
        sb.AppendLine("SIDE PROFILE (front-most z mm per 5mm of y) — all | x 0-30 | x 30-60 | x 60-90 | x 90-120 ;  ON d2 all");
        {
            var rowsOn = new List<float>();
            string Row(float[] p, float yy, float lo, float hi)
            {
                float m = float.NaN;
                for (int v = 0; v < p.Length / 3; v++)
                {
                    float ax = MathF.Abs(p[v * 3]);
                    if (MathF.Abs(p[v * 3 + 1] - yy) > 0.0025f || ax < lo || ax >= hi) continue;
                    if (float.IsNaN(m) || p[v * 3 + 2] > m) m = p[v * 3 + 2];
                }
                return float.IsNaN(m) ? "    -  " : $"{m * 1000,7:0.0}";
            }
            float Max(float[] p, float yy)
            {
                float m = float.MinValue;
                for (int v = 0; v < p.Length / 3; v++)
                    if (MathF.Abs(p[v * 3 + 1] - yy) <= 0.0025f) m = MathF.Max(m, p[v * 3 + 2]);
                return m;
            }
            var ys = Enumerable.Range(0, 37).Select(i => 1.25f - i * 0.005f).ToList();
            var onAll = ys.Select(yy => Max(pOn, yy)).ToList();
            for (int i = 0; i < ys.Count; i++)
            {
                float yy = ys[i];
                string d2 = i > 0 && i < ys.Count - 1 ? $"{(onAll[i - 1] + onAll[i + 1] - 2 * onAll[i]) * 1000,6:0.0}" : "     ";
                sb.AppendLine($"  y {yy:0.000} OFF {Row(pOff, yy, 0f, 1f)} {Row(pOff, yy, 0f, .03f)} {Row(pOff, yy, .03f, .06f)} {Row(pOff, yy, .06f, .09f)} {Row(pOff, yy, .09f, .12f)}"
                            + $" | ON {Row(pOn, yy, 0f, 1f)} {Row(pOn, yy, 0f, .03f)} {Row(pOn, yy, .03f, .06f)} {Row(pOn, yy, .06f, .09f)} {Row(pOn, yy, .09f, .12f)}  d2 {d2}");
            }
        }

        // Down the breast face at a few columns out from the midline: how far each drawn vertex moved (ON vs OFF,
        // index-aligned) — which band of the breast the passes are touching.
        {
            SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(off), out var fo, out _, out var ft, out _, out _,
                keepMaterial: m => m.Contains("ril_"));
            SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(on), out var fn, out _, out _, out _, out _,
                keepMaterial: m => m.Contains("ril_"));
            var usedF = new bool[fo.Length / 3];
            foreach (int i in ft) usedF[i] = true;
            sb.AppendLine();
            sb.AppendLine("BREAST FACE MOVES (mm) — rows y, columns |x| 40..110mm, front vertices; max move in each cell");
            for (float yy = 1.26f; yy >= 1.12f; yy -= 0.01f)
            {
                var cells = new float[8];
                for (int v = 0; v < usedF.Length && v * 3 + 2 < fn.Length; v++)
                {
                    if (!usedF[v] || MathF.Abs(fo[v * 3 + 1] - yy) > 0.005f || fo[v * 3 + 2] < 0.08f) continue;
                    int c = (int)((MathF.Abs(fo[v * 3]) - 0.04f) / 0.01f);
                    if (c < 0 || c >= cells.Length) continue;
                    float dx = fn[v * 3] - fo[v * 3], dy = fn[v * 3 + 1] - fo[v * 3 + 1], dz = fn[v * 3 + 2] - fo[v * 3 + 2];
                    cells[c] = MathF.Max(cells[c], MathF.Sqrt(dx * dx + dy * dy + dz * dz) * 1000f);
                }
                sb.AppendLine($"  y {yy:0.00}: " + string.Join(" ", cells.Select(c => $"{c,5:0.0}")));
            }
        }

        // Across the lower cleavage: the front-most z in 5mm columns from the midline out, ON vs OFF. A W shows as
        // z dropping and rising again between the midline and the breast.
        sb.AppendLine();
        sb.AppendLine("CHEST PROFILE (front-most z, mm, per 5mm column of |x|)");
        foreach (float yy in new[] { 1.17f, 1.18f, 1.19f, 1.20f, 1.21f, 1.22f })
            foreach (var (label, pp) in new[] { ("OFF", pOff), ("ON ", pOn) })
            {
                var cols = new float[14];
                Array.Fill(cols, float.NaN);
                for (int v = 0; v < pp.Length / 3; v++)
                {
                    if (MathF.Abs(pp[v * 3 + 1] - yy) > 0.005f || pp[v * 3 + 2] < 0.05f) continue;
                    int c = (int)(MathF.Abs(pp[v * 3]) / 0.005f);
                    if (c < cols.Length && (float.IsNaN(cols[c]) || pp[v * 3 + 2] > cols[c])) cols[c] = pp[v * 3 + 2];
                }
                sb.AppendLine($"  y {yy:0.00} {label}: " + string.Join(" ", cols.Select(z => float.IsNaN(z) ? "   -  " : $"{z * 1000,6:0.0}")));
            }

        // The lower cleft's own vertices: where they are, which way they face, what the gate makes of them, and
        // how far the span moved them. Index-aligned between the two replays (same build, same inputs).
        SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(off), out var qo, out _, out var to, out var wo, out var no,
            keepMaterial: m => m.Contains("ril_"));
        SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(on), out var qn, out _, out _, out _, out _,
            keepMaterial: m => m.Contains("ril_"));
        var usedO = new bool[qo.Length / 3];
        foreach (int i in to) usedO[i] = true;
        sb.AppendLine();
        sb.AppendLine("lower cleft vertices (|x| < 25mm, y 0.860..0.905, z < -20mm), sorted by y then |x|:");
        var rows = new List<(float y, float ax, string line)>();
        for (int v = 0; v < usedO.Length && v * 3 + 2 < qn.Length; v++)
        {
            float x = qo[v * 3], y = qo[v * 3 + 1], z = qo[v * 3 + 2];
            if (!usedO[v] || MathF.Abs(x) > 0.025f || y < 0.86f || y > 0.905f || z > -0.02f) continue;
            float nx = no[v * 3], ny = no[v * 3 + 1], nz = no[v * 3 + 2];
            static float Ss(float t) => t * t * (3 - 2 * t);
            float back = Ss(Math.Clamp((-nz - 0.15f) / 0.15f, 0f, 1f));
            float inward = -MathF.Sign(x) * nx;
            float wall = nz <= 0 ? Ss(Math.Clamp((inward - 0.15f) / 0.15f, 0f, 1f)) * Ss(Math.Clamp(-z / 0.02f, 0f, 1f)) : 0f;
            float kosi = wo[v].Where(b => b.Item1 == "j_kosi").Sum(b => b.Item2);
            float thigh = wo[v].Where(b => b.Item1.StartsWith("j_asi_a")).Sum(b => b.Item2);
            float dx = qn[v * 3] - x, dy = qn[v * 3 + 1] - y, dz = qn[v * 3 + 2] - z;
            rows.Add((y, MathF.Abs(x), $"  ({x * 1000,6:0.0},{y:0.000},{z * 1000,6:0.0}) n({nx,5:0.00},{ny,5:0.00},{nz,5:0.00}) back {back:0.00} wall {wall:0.00} kosi {kosi:0.00} thigh {thigh:0.00} moved {MathF.Sqrt(dx * dx + dy * dy + dz * dz) * 1000,5:0.0}mm"));
        }
        foreach (var r in rows.OrderBy(r => MathF.Round(r.y, 2)).ThenBy(r => r.ax).Take(160)) sb.AppendLine(r.line);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "proteus-cleft-section.txt"), sb.ToString());
    }

    /// <summary>
    /// SCRATCH: the shell as the game SHADES it — its stored vertex normals, interpolated, lit from behind and
    /// above — for the game's own shell (left) and the spans-off replay (right). Back view of the hips.
    /// </summary>
    [Fact]
    public void ShadedBackRender()
    {
        ShadedBackRenderAt(-0.07f, 0.07f, 0.84f, 0.98f, "proteus-shaded-back-zoom.rgb");
        ShadedBackRenderAt(-0.2f, 0.2f, 0.78f, 1.10f, "proteus-shaded-back.rgb");
        ShadedBackRenderAt(-0.12f, 0.12f, 1.08f, 1.32f, "proteus-shaded-chest.rgb", front: true);
        ShadedBackRenderAt(-0.06f, 0.06f, 1.12f, 1.24f, "proteus-shaded-chest-zoom.rgb", front: true);
        ShadedBackRenderAt(-0.16f, 0.16f, 1.02f, 1.30f, "proteus-shaded-chest-top.rgb", front: true, lightAbove: true);
        {
            // The crotch from the front and a little below, the angle of the in-game screenshots.
            const float pitch = 0.7f;
            float yc = 0.86f * MathF.Cos(pitch) + 0.03f * MathF.Sin(pitch);
            ShadedBackRenderAt(-0.05f, 0.05f, yc - 0.05f, yc + 0.05f, "proteus-shaded-crotch.rgb", front: true, pitch: pitch,
                               lightBelow: true);
            // Straight up from below, closer, by FACE: grey faces one winding, red the other — folded faces show red.
            const float under = 1.35f;
            float yu = 0.86f * MathF.Cos(under) + 0.03f * MathF.Sin(under);
            ShadedBackRenderAt(-0.025f, 0.025f, yu - 0.025f, yu + 0.025f, "proteus-faces-crotch.rgb", front: true, pitch: under,
                               lightBelow: true, byFace: true);
        }
        ShadedBackRenderAt(-0.16f, 0.16f, 1.06f, 1.32f, "proteus-shaded-chest-left.rgb", front: true, yaw: 0.9f);
        ShadedBackRenderAt(-0.16f, 0.16f, 1.06f, 1.32f, "proteus-shaded-chest-right.rgb", front: true, yaw: -0.9f);
    }

    private void ShadedBackRenderAt(float x0, float x1, float y0, float y1, string outName, bool front = false,
                                    float yaw = 0f, bool lightAbove = false, float pitch = 0f, bool lightBelow = false,
                                    bool byFace = false)
    {
        float flip = front ? 1f : -1f;
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        var dump = Path.Combine(Path.GetTempPath(), "proteus-gen3-dump");
        var off = Path.Combine(Path.GetTempPath(), "proteus-fold-clearance.replay-off.mdl");
        if (!File.Exists(Path.Combine(dump, "host0_shell.mdl")) || !File.Exists(off)) return;
        const int W = 400, H = 400;
        var rgb = new byte[W * 2 * H * 3];
        int col = 0;
        var on = Path.Combine(Path.GetTempPath(), "proteus-fold-clearance.replay.mdl");
        foreach (var file in new[] { File.Exists(on) ? on : Path.Combine(dump, "host0_shell.mdl"), off })
        {
            if (!SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(file), out var p, out _, out var t, out _, out var nr,
                    keepMaterial: m => m.Contains("ril_"))) return;
            var depth = new float[W * H];
            Array.Fill(depth, float.NegativeInfinity);
            // Light from behind the body (model -Z), a little above and to the side.
            // As a model-space direction toward the light: from behind, above and to one side; from the front, a
            // raking light from the side, which is what shows a ridge.
            float lvx = front ? 0.75f : -0.3f, ly = front ? 0.25f : 0.5f, lvz = front ? 0.61f : -0.81f;
            // Overhead and a little in front, the way the game lit the screenshot of the lumps under the breasts.
            if (lightAbove) { lvx = 0.1f; ly = 0.85f; lvz = 0.52f; }
            // From below and in front, and a little to the side, for faces that look down.
            if (lightBelow) { lvx = 0.35f; ly = -0.75f; lvz = 0.56f; }
            // The light turns with the camera, so a turned view is lit the same way as the straight one.
            float lx = lvx * cy - lvz * sy, lz = lvx * sy + lvz * cy;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                int a = t[k], b = t[k + 1], c = t[k + 2];
                // From behind: flip x and z.
                // Turned about the vertical by yaw first, then viewed from the front or back.
                float RX(int i) => flip * (p[i * 3] * cy + p[i * 3 + 2] * sy);
                float RZ0(int i) => flip * (-p[i * 3] * sy + p[i * 3 + 2] * cy);
                // Pitched: the camera below and looking up, so lower points come nearer.
                float RY(int i) => p[i * 3 + 1] * cp + RZ0(i) * sp;
                float RZ(int i) => RZ0(i) * cp - p[i * 3 + 1] * sp;
                float ax = RX(a), ay = RY(a), az = RZ(a);
                float bx = RX(b), by = RY(b), bz = RZ(b);
                float cx = RX(c), cyy = RY(c), cz = RZ(c);
                float Px(float x) => (x - x0) / (x1 - x0) * (W - 1);
                float Py(float y) => (y1 - y) / (y1 - y0) * (H - 1);
                float pax = Px(ax), pay = Py(ay), pbx = Px(bx), pby = Py(by), pcx = Px(cx), pcy = Py(cyy);
                float area = (pbx - pax) * (pcy - pay) - (pcx - pax) * (pby - pay);
                if (MathF.Abs(area) < 1e-9f) continue;
                int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(pax, MathF.Min(pbx, pcx))));
                int maxX = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(pax, MathF.Max(pbx, pcx))));
                int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(pay, MathF.Min(pby, pcy))));
                int maxY = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(pay, MathF.Max(pby, pcy))));
                for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float w0 = ((pbx - x) * (pcy - y) - (pcx - x) * (pby - y)) / area;
                    float w1 = ((pcx - x) * (pay - y) - (pax - x) * (pcy - y)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * az + w1 * bz + w2 * cz;
                    int i = y * W + x;
                    if (z <= depth[i]) continue;
                    depth[i] = z;
                    float nx = w0 * nr[a * 3] + w1 * nr[b * 3] + w2 * nr[c * 3];
                    float ny = w0 * nr[a * 3 + 1] + w1 * nr[b * 3 + 1] + w2 * nr[c * 3 + 1];
                    float nz = w0 * nr[a * 3 + 2] + w1 * nr[b * 3 + 2] + w2 * nr[c * 3 + 2];
                    float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    float lam = nl > 0 ? MathF.Max(0f, (nx * lx + ny * ly + nz * lz) / nl) : 0f;
                    bool flipped = false;
                    if (byFace)
                    {
                        float ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
                        float vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
                        float fx = uy * vz - uz * vy, fy = uz * vx - ux * vz, fz = ux * vy - uy * vx;
                        float fl = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
                        lam = fl > 0 ? MathF.Abs(fx * lx + fy * ly + fz * lz) / fl : 0f;
                        flipped = area * flip > 0;
                    }
                    byte s = (byte)(30 + 225 * lam);
                    int o = (y * W * 2 + col * W + x) * 3;
                    rgb[o] = flipped ? (byte)255 : s; rgb[o + 1] = s; rgb[o + 2] = flipped ? (byte)(s / 3) : s;
                }
            }
            col++;
        }
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), outName), rgb);

        // Across the cleft at a few heights: position and stored normal of drawn vertices, on vs off.
        var sb = new System.Text.StringBuilder();
        foreach (var (label, file) in new[] { ("ON", File.Exists(on) ? on : Path.Combine(dump, "host0_shell.mdl")), ("OFF", off) })
        {
            SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(file), out var p, out _, out var t, out _, out var nr,
                keepMaterial: m => m.Contains("ril_"));
            var used = new bool[p.Length / 3];
            foreach (int i in t) used[i] = true;
            foreach (float yy in new[] { 0.90f, 0.94f, 0.97f })
            {
                sb.AppendLine($"== {label} y {yy:0.00}");
                foreach (int v in Enumerable.Range(0, used.Length).Where(v => used[v] && MathF.Abs(p[v * 3 + 1] - yy) < 0.004f
                             && MathF.Abs(p[v * 3]) < 0.05f && p[v * 3 + 2] < -0.03f).OrderBy(v => p[v * 3]))
                    sb.AppendLine($"  x {p[v * 3] * 1000,6:0.0} z {p[v * 3 + 2] * 1000,7:0.0}  n ({nr[v * 3],5:0.00},{nr[v * 3 + 1],5:0.00},{nr[v * 3 + 2],5:0.00})");
            }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "proteus-cleft-normals.txt"), sb.ToString());
    }

    /// <summary>
    /// SCRATCH: the body fold pass on the dumped legs part, and where the NON-skin meshes (the genital mesh) sit
    /// against the skin before and after it — how many of their vertices stand outside the skin surface by more
    /// than the shell's push, which is what shows through a garment.
    /// </summary>
    [Fact]
    public void FoldGenitalClearance()
    {
        var dump = Path.Combine(Path.GetTempPath(), "proteus-gen3-dump");
        var f = Path.Combine(dump, "host0_body1.mdl");
        var covP = Path.Combine(dump, "host0_layer0_coverage.raw");
        if (!File.Exists(f) || !File.Exists(covP)) return;
        var cov = File.ReadAllBytes(covP);
        int vs = (int)Math.Round(Math.Sqrt(cov.Length));
        var gate = new SecondSkinLayer { MaterialName = "gate", Coverage = cov, CoverageWidth = vs, CoverageHeight = vs };
        var input = File.ReadAllBytes(f);
        var log = new List<string>();
        var output = SecondSkinWriter.SmoothBodyNipples(input, gate, 0f, log.Add, foldStrength: 1f);
        var sb = new System.Text.StringBuilder();
        foreach (var l in log) sb.AppendLine(l);

        foreach (var (label, bytes) in new[] { ("BEFORE", input), ("AFTER", output ?? input) })
        {
            if (!SecondSkinWriter.TryReadLod0Geometry(bytes, out var sp, out _, out var st, out _, out _,
                    keepMaterial: SecondSkinWriter.IsBodySkinMaterial)) continue;
            // The front view across the crotch: front-most skin z (mm) in 3mm columns of x, per row of height. A notch
            // rising into the labia shows as the middle columns sitting back from their neighbours.
            sb.AppendLine($"{label} front-most skin z across the crotch (x -18..+18mm in 3mm columns):");
            for (float yy = 0.880f; yy >= 0.842f; yy -= 0.004f)
            {
                var cols = new float[12];
                Array.Fill(cols, float.NaN);
                for (int v = 0; v < sp.Length / 3; v++)
                {
                    if (MathF.Abs(sp[v * 3 + 1] - yy) > 0.002f || sp[v * 3 + 2] < -0.01f) continue;
                    int c = (int)MathF.Floor((sp[v * 3] + 0.018f) / 0.003f);
                    if (c < 0 || c >= cols.Length) continue;
                    if (float.IsNaN(cols[c]) || sp[v * 3 + 2] > cols[c]) cols[c] = sp[v * 3 + 2];
                }
                sb.AppendLine($"  y {yy:0.000}: " + string.Join(" ", cols.Select(z => float.IsNaN(z) ? "   -  " : $"{z * 1000,6:0.0}")));
            }
            if (!SecondSkinWriter.TryReadLod0Geometry(bytes, out var gp, out _, out var gt, out _, out _,
                    keepMaterial: m => !SecondSkinWriter.IsBodySkinMaterial(m))) continue;
            var nodes = new SecondSkinWriter.Vec3[sp.Length / 3];
            for (int i = 0; i < nodes.Length; i++) nodes[i] = new SecondSkinWriter.Vec3(sp[i * 3], sp[i * 3 + 1], sp[i * 3 + 2]);
            var winding = new SecondSkinWriter.BodyWinding(nodes, st, Enumerable.Range(0, nodes.Length).ToArray());
            var used = new bool[gp.Length / 3];
            foreach (int i in gt) used[i] = true;
            // Signed distance to the skin SURFACE: nearest point on a skin triangle, positive on the side the
            // triangle's (winding-derived, outward-by-majority) normal faces. Past the shell's push = shows through.
            var crotchTris = new List<int>();
            for (int k = 0; k + 2 < st.Length; k += 3)
            {
                var a = nodes[st[k]];
                if (a.Y > 0.76f && a.Y < 0.97f && MathF.Abs(a.X) < 0.08f) crotchTris.Add(k);
            }
            int crotch = 0, proud = 0;
            var worst = new List<(float d, SecondSkinWriter.Vec3 p)>();
            for (int v = 0; v < used.Length; v++)
            {
                if (!used[v]) continue;
                var p = new SecondSkinWriter.Vec3(gp[v * 3], gp[v * 3 + 1], gp[v * 3 + 2]);
                if (p.Y < 0.78f || p.Y > 0.95f || MathF.Abs(p.X) > 0.05f) continue;
                crotch++;
                float best = float.MaxValue, signed = 0f;
                foreach (int k in crotchTris)
                {
                    var (q, nrmT) = ClosestOnTri(p, nodes[st[k]], nodes[st[k + 1]], nodes[st[k + 2]]);
                    float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                    float d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 >= best) continue;
                    best = d2;
                    signed = MathF.Sign(dx * nrmT.X + dy * nrmT.Y + dz * nrmT.Z) * MathF.Sqrt(d2);
                }
                if (signed > 0.00005f) { proud++; worst.Add((signed, p)); }
            }
            sb.AppendLine($"{label}: {crotch} non-skin vertices at the crotch, {proud} standing more than 0.05mm in front of the skin surface"
                          + $" — {worst.Count(w => w.p.Y < 0.88f)} of them below y 0.88 (the fold), worst there "
                          + $"{(worst.Any(w => w.p.Y < 0.88f) ? worst.Where(w => w.p.Y < 0.88f).Max(w => w.d) * 1000 : 0):0.00}mm");
            foreach (var w in worst.Where(w => w.p.Y < 0.88f).OrderByDescending(w => w.d).Take(12))
                sb.AppendLine($"   {w.d * 1000,6:0.00}mm in front at ({w.p.X:0.0000},{w.p.Y:0.0000},{w.p.Z:0.0000})");
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "proteus-fold-genital.txt"), sb.ToString());

        static (SecondSkinWriter.Vec3 q, SecondSkinWriter.Vec3 n) ClosestOnTri(SecondSkinWriter.Vec3 p,
            SecondSkinWriter.Vec3 a, SecondSkinWriter.Vec3 b, SecondSkinWriter.Vec3 c)
        {
            SecondSkinWriter.Vec3 Sub(SecondSkinWriter.Vec3 x, SecondSkinWriter.Vec3 y) => new(x.X - y.X, x.Y - y.Y, x.Z - y.Z);
            float Dot(SecondSkinWriter.Vec3 x, SecondSkinWriter.Vec3 y) => x.X * y.X + x.Y * y.Y + x.Z * y.Z;
            SecondSkinWriter.Vec3 At(SecondSkinWriter.Vec3 o, SecondSkinWriter.Vec3 d, float t) => new(o.X + d.X * t, o.Y + d.Y * t, o.Z + d.Z * t);
            var ab = Sub(b, a); var ac = Sub(c, a); var ap = Sub(p, a);
            var n = new SecondSkinWriter.Vec3(ab.Y * ac.Z - ab.Z * ac.Y, ab.Z * ac.X - ab.X * ac.Z, ab.X * ac.Y - ab.Y * ac.X);
            float d1 = Dot(ab, ap), d2 = Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return (a, n);
            var bp = Sub(p, b); float d3 = Dot(ab, bp), d4 = Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return (b, n);
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return (At(a, ab, d1 / (d1 - d3)), n);
            var cp = Sub(p, c); float d5 = Dot(ab, cp), d6 = Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return (c, n);
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return (At(a, ac, d2 / (d2 - d6)), n);
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
                return (At(b, Sub(c, b), (d4 - d3) / ((d4 - d3) + (d5 - d6))), n);
            float den = 1f / (va + vb + vc);
            float v2 = vb * den, w2 = vc * den;
            return (new SecondSkinWriter.Vec3(a.X + ab.X * v2 + ac.X * w2, a.Y + ab.Y * v2 + ac.Y * w2, a.Z + ab.Z * v2 + ac.Z * w2), n);
        }
    }

    /// <summary>
    /// SCRATCH: faces at the crotch the spans turned over — same-index triangles of the replay shell against the spans-off
    /// replay whose face normals disagree, per 5mm of z.
    /// </summary>
    [Fact]
    public void CrotchFlippedFaces()
    {
        var on = Path.Combine(Path.GetTempPath(), "proteus-fold-clearance.replay.mdl");
        var off = Path.Combine(Path.GetTempPath(), "proteus-fold-clearance.replay-off.mdl");
        if (!File.Exists(on) || !File.Exists(off)) return;
        var sb = new System.Text.StringBuilder();
        foreach (var (label, file) in new[] { ("ON", on), ("OFF", off) })
        {
            SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(file), out var p, out _, out var t, out _, out var nr,
                keepMaterial: m => m.Contains("ril_"));
            // A face whose winding disagrees with the normals stored at its corners: shaded from the wrong side.
            var bins = new SortedDictionary<int, (int flipped, int total, float area)>();
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                int a = t[k], b = t[k + 1], c = t[k + 2];
                float x = p[a * 3], y = p[a * 3 + 1], z = p[a * 3 + 2];
                if (MathF.Abs(x) > 0.02f || y < 0.83f || y > 0.9f || z < -0.06f || z > 0.09f) continue;
                float ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
                float vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
                float fx = uy * vz - uz * vy, fy = uz * vx - ux * vz, fz = ux * vy - uy * vx;
                float ax = nr[a * 3] + nr[b * 3] + nr[c * 3], ay = nr[a * 3 + 1] + nr[b * 3 + 1] + nr[c * 3 + 1];
                float az = nr[a * 3 + 2] + nr[b * 3 + 2] + nr[c * 3 + 2];
                int bin = (int)MathF.Floor(z * 200f) * 5;
                var e = bins.GetValueOrDefault(bin);
                e.total++;
                float fl = MathF.Sqrt(fx * fx + fy * fy + fz * fz), al = MathF.Sqrt(ax * ax + ay * ay + az * az);
                if (fl > 0 && al > 0 && (fx * ax + fy * ay + fz * az) / (fl * al) < -0.2f)
                {
                    e.flipped++;
                    e.area += fl * 0.5f * 1e6f;
                }
                bins[bin] = e;
            }
            sb.AppendLine($"== {label}");
            foreach (var (bin, e) in bins)
                if (e.flipped > 0) sb.AppendLine($"  z {bin,4}mm: {e.flipped,3} of {e.total,4} against their normals, {e.area:0.00}mm²");
            sb.AppendLine($"  total {bins.Values.Sum(e => e.flipped)}, {bins.Values.Sum(e => e.area):0.00}mm²");
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "proteus-crotch-flipped.txt"), sb.ToString());
    }

    /// <summary>
    /// SCRATCH: the crotch seen from BELOW — the dumped body parts and the game's shell, depth tested. Grey = shell
    /// wins; tan = skin wins (shell missing or behind it); orange = a non-skin body mesh wins (the genital mesh).
    /// Left: everything. Right: the shell alone, with its open boundary edges drawn red.
    /// </summary>
    [Fact]
    public void CrotchFromBelowRender()
    {
        var dump = Path.Combine(Path.GetTempPath(), "proteus-gen3-dump");
        var replayF = Path.Combine(Path.GetTempPath(), "proteus-fold-clearance.replay.mdl");
        var shellF = File.Exists(replayF) ? replayF : Path.Combine(dump, "host0_shell.mdl");
        if (!File.Exists(shellF)) return;
        const int W = 400, H = 400;
        float x0 = -0.05f, x1 = 0.05f, z0 = -0.05f, z1 = 0.05f;
        var depth = new float[W * H];
        var kind = new byte[W * H];
        Array.Fill(depth, float.NegativeInfinity);
        var shellDepth = new float[W * H];
        var shellShade = new byte[W * H];
        Array.Fill(shellDepth, float.NegativeInfinity);

        void Raster(float[] p, int[] t, byte k, float[] dep, byte[]? kindOut, byte[]? shadeOut)
        {
            for (int q = 0; q + 2 < t.Length; q += 3)
            {
                int a = t[q], b = t[q + 1], c = t[q + 2];
                if (p[a * 3 + 1] > 0.92f || p[a * 3 + 1] < 0.78f) continue;
                float Px(float x) => (x - x0) / (x1 - x0) * (W - 1);
                float Pz(float z) => (z1 - z) / (z1 - z0) * (H - 1);
                float pax = Px(p[a * 3]), pay = Pz(p[a * 3 + 2]), pbx = Px(p[b * 3]), pby = Pz(p[b * 3 + 2]);
                float pcx = Px(p[c * 3]), pcy = Pz(p[c * 3 + 2]);
                // From below: nearer = LOWER y.
                float da = -p[a * 3 + 1], db = -p[b * 3 + 1], dc = -p[c * 3 + 1];
                float area = (pbx - pax) * (pcy - pay) - (pcx - pax) * (pby - pay);
                if (MathF.Abs(area) < 1e-9f) continue;
                float ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
                float vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                byte shade = (byte)(90 + 165 * MathF.Abs(nl > 0 ? ny / nl : 0));
                int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(pax, MathF.Min(pbx, pcx))));
                int maxX = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(pax, MathF.Max(pbx, pcx))));
                int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(pay, MathF.Min(pby, pcy))));
                int maxY = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(pay, MathF.Max(pby, pcy))));
                for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float w0 = ((pbx - x) * (pcy - y) - (pcx - x) * (pby - y)) / area;
                    float w1 = ((pcx - x) * (pay - y) - (pax - x) * (pcy - y)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float d = w0 * da + w1 * db + w2 * dc;
                    int i = y * W + x;
                    if (d <= dep[i]) continue;
                    dep[i] = d;
                    if (kindOut != null) kindOut[i] = k;
                    if (shadeOut != null) shadeOut[i] = shade;
                }
            }
        }

        var sb = new System.Text.StringBuilder();
        for (int part = 0; File.Exists(Path.Combine(dump, $"host0_body{part}.mdl")); part++)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dump, $"host0_body{part}.mdl"));
            if (SecondSkinWriter.TryReadLod0Geometry(bytes, out var sp, out _, out var st, out _, out _,
                    keepMaterial: SecondSkinWriter.IsBodySkinMaterial))
                Raster(sp, st, 1, depth, kind, null);
            if (SecondSkinWriter.TryReadLod0Geometry(bytes, out var gp, out _, out var gt, out _, out _,
                    keepMaterial: m => !SecondSkinWriter.IsBodySkinMaterial(m)))
                Raster(gp, gt, 2, depth, kind, null);
        }
        if (!SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(shellF), out var hp, out _, out var ht, out _, out _,
                keepMaterial: m => m.Contains("ril_"))) return;
        // The shell sits 0.05mm off the skin; nudge it toward the camera by that much so coincident skin does not win.
        var hpn = (float[])hp.Clone();
        Raster(hpn, ht, 3, depth, kind, null);
        Raster(hpn, ht, 3, shellDepth, null, shellShade);

        // Open boundary edges of the shell near the crotch.
        var edgeUse = new Dictionary<(long, long), int>();
        (long, long, long) Q(int v) => ((long)MathF.Round(hp[v * 3] / 1e-5f), (long)MathF.Round(hp[v * 3 + 1] / 1e-5f), (long)MathF.Round(hp[v * 3 + 2] / 1e-5f));
        var weldId = new Dictionary<(long, long, long), long>();
        long Id(int v) { var k = Q(v); if (!weldId.TryGetValue(k, out var id)) weldId[k] = id = weldId.Count; return id; }
        for (int q = 0; q + 2 < ht.Length; q += 3)
            for (int e = 0; e < 3; e++)
            {
                long ia = Id(ht[q + e]), ib = Id(ht[q + (e + 1) % 3]);
                var key = ia < ib ? (ia, ib) : (ib, ia);
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
            }
        var pos = new Dictionary<long, (float X, float Y, float Z)>();
        for (int v = 0; v < hp.Length / 3; v++) pos[Id(v)] = (hp[v * 3], hp[v * 3 + 1], hp[v * 3 + 2]);
        var rgb = new byte[W * 2 * H * 3];
        for (int i = 0; i < W * H; i++)
        {
            (byte R, byte G, byte B) c = kind[i] switch { 1 => (214, 150, 110), 2 => (240, 110, 20), 3 => (170, 170, 170), _ => (20, 20, 40) };
            rgb[i / W * W * 2 * 3 + (i % W) * 3] = c.R; rgb[i / W * W * 2 * 3 + (i % W) * 3 + 1] = c.G; rgb[i / W * W * 2 * 3 + (i % W) * 3 + 2] = c.B;
            byte s = float.IsNegativeInfinity(shellDepth[i]) ? (byte)20 : shellShade[i];
            int o = (i / W * W * 2 + W + i % W) * 3;
            rgb[o] = s; rgb[o + 1] = s; rgb[o + 2] = s;
        }
        int open = 0;
        foreach (var (e, n) in edgeUse)
        {
            if (n != 1) continue;
            var a = pos[e.Item1]; var b = pos[e.Item2];
            if (a.Y > 0.92f || a.Y < 0.78f || MathF.Abs(a.X) > 0.05f || MathF.Abs(a.Z) > 0.05f) continue;
            open++;
            sb.AppendLine($"open edge ({a.X * 1000:0.0},{a.Y:0.000},{a.Z * 1000:0.0})-({b.X * 1000:0.0},{b.Y:0.000},{b.Z * 1000:0.0})");
            for (int s = 0; s <= 20; s++)
            {
                float f = s / 20f;
                float x = a.X + (b.X - a.X) * f, z = a.Z + (b.Z - a.Z) * f;
                int px = (int)((x - x0) / (x1 - x0) * (W - 1)), py = (int)((z1 - z) / (z1 - z0) * (H - 1));
                if (px < 0 || py < 0 || px >= W || py >= H) continue;
                int o = (py * W * 2 + W + px) * 3;
                rgb[o] = 255; rgb[o + 1] = 0; rgb[o + 2] = 0;
            }
        }
        sb.Insert(0, $"{open} open shell edge(s) at the crotch\n");
        int orange = kind.Count(k => k == 2), tan = kind.Count(k => k == 1);
        sb.Insert(0, $"pixels from below: genital mesh wins {orange}, skin wins {tan}\n");
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "proteus-crotch-below.rgb"), rgb);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "proteus-crotch-below.txt"), sb.ToString());
    }

    /// <summary>SCRATCH: the nipple pass's own report on every frozen torso dump there is, for comparing bodies.</summary>
    [Fact]
    public void NippleLocatorAcrossDumps()
    {
        var scratch = @"C:\Users\solon\AppData\Local\Temp\claude\E--repos-Proteus--claude-worktrees-shell-push\bc97e109-cdec-4a75-bc25-b4b2a19fbbe2\scratchpad";
        var dirs = new[] { Path.Combine(Path.GetTempPath(), "proteus-gen3-dump"), Path.Combine(scratch, "bust-dump"),
                           Path.Combine(scratch, "bust-dump-yab"), Path.Combine(scratch, "bust-dump-840"), Path.Combine(scratch, "gen3-dump") };
        var sb = new System.Text.StringBuilder();
        foreach (var dir in dirs)
            for (int host = 0; host < 5; host++)
            for (int part = 0; part < 4; part++)
            {
                var f = Path.Combine(dir, $"host{host}_body{part}.mdl");
                if (!File.Exists(f)) continue;
                if (!SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(f), out var pos, out _, out var tri, out var bw, out var nrm))
                    continue;
                int vc = pos.Length / 3;
                var bust = new float[vc];
                var p3 = new SecondSkinWriter.Vec3[vc];
                var n3 = new SecondSkinWriter.Vec3[vc];
                for (int i = 0; i < vc; i++)
                {
                    bust[i] = MathF.Min(1f, bw[i].Where(b => b.Item1.Contains("mune")).Sum(b => b.Item2));
                    p3[i] = new SecondSkinWriter.Vec3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
                    n3[i] = new SecondSkinWriter.Vec3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
                }
                if (bust.Count(b => b > 0f) < 100) continue;
                var log = new List<string>();
                SecondSkinWriter.BustBridgeSolve(p3, n3, tri, bust, 0f, log.Add, null, 1f);
                sb.AppendLine($"== {Path.GetFileName(dir)} host{host} body{part}");
                foreach (var l in log.Where(l => l.Contains("nipple"))) sb.AppendLine("  " + l);
                break;
            }
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "proteus-nipple-locator.txt"), sb.ToString() + "\n########\n");
    }

    /// <summary>
    /// SCRATCH: the chest solve on the dumped torso (unshaped), reporting per height on the midline whether a
    /// node carries breast weight, its region weight, and how far it was lifted.
    /// </summary>
    [Fact]
    public void BustRegionOnMidline()
    {
        var f = Path.Combine(Path.GetTempPath(), "proteus-gen3-dump", "host0_body0.mdl");
        if (!File.Exists(f)) return;
        if (!SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(f), out var pos, out _, out var tri, out var bw, out var nrm))
            return;
        int vc = pos.Length / 3;
        var bust = new float[vc];
        var p3 = new SecondSkinWriter.Vec3[vc];
        var n3 = new SecondSkinWriter.Vec3[vc];
        for (int i = 0; i < vc; i++)
        {
            bust[i] = MathF.Min(1f, bw[i].Where(b => b.Item1.Contains("mune")).Sum(b => b.Item2));
            p3[i] = new SecondSkinWriter.Vec3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            n3[i] = new SecondSkinWriter.Vec3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
        }
        var log = new List<string>();
        var nipLog = new List<string>();
        SecondSkinWriter.BustBridgeSolve(p3, n3, tri, bust, 0f, nipLog.Add, null, 1f);
        var plan = SecondSkinWriter.BustBridgeSolve(p3, n3, tri, bust, 1f, log.Add);
        var sb = new System.Text.StringBuilder();
        foreach (var l in nipLog) sb.AppendLine("NIPPLE PASS: " + l);
        // Small-scale bumps: z above the mean of a 3-6mm ring (in x,y), over the front of the breasts.
        var front = Enumerable.Range(0, vc).Where(i => p3[i].Z > 0.09f && p3[i].Y > 1.12f && p3[i].Y < 1.32f && MathF.Abs(p3[i].X) > 0.02f).ToList();
        var bumps = new List<(float prom, int i, int ring)>();
        foreach (int i in front)
        {
            float s = 0; int c = 0, sides = 0;
            var nn = n3[i];
            foreach (int k in front)
            {
                float dx = p3[k].X - p3[i].X, dy = p3[k].Y - p3[i].Y, dz = p3[k].Z - p3[i].Z;
                float r = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (r < 0.003f || r > 0.006f) continue;
                s += -(dx * nn.X + dy * nn.Y + dz * nn.Z); c++; sides |= (dx >= 0 ? 1 : 2) | (dy >= 0 ? 4 : 8);
            }
            if (c >= 4 && sides == 15) bumps.Add((s / c, i, c));
        }
        foreach (var b in bumps.OrderByDescending(b => b.prom).Take(4))
            sb.AppendLine($"BUMP 3-6mm: prom {b.prom * 1000:0.00}mm at ({p3[b.i].X:0.000},{p3[b.i].Y:0.000},{p3[b.i].Z:0.000}) ring {b.ring} bust {bust[b.i]:0.00}");
        // The production ring (10.2-20.4mm), along the vertex's own normal, over breast-weighted vertices only.
        var onB = Enumerable.Range(0, vc).Where(i => bust[i] > 0f && p3[i].Z > 0.05f).ToList();
        var big = new List<(float prom, int i)>();
        foreach (int i in onB)
        {
            float s = 0; int c = 0, sides = 0;
            var nn = n3[i];
            foreach (int k in onB)
            {
                float dx = p3[k].X - p3[i].X, dy = p3[k].Y - p3[i].Y, dz = p3[k].Z - p3[i].Z;
                float r = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (r < 0.0102f || r > 0.0204f) continue;
                s += -(dx * nn.X + dy * nn.Y + dz * nn.Z); c++; sides |= (dx >= 0 ? 1 : 2) | (dy >= 0 ? 4 : 8);
            }
            if (c >= 4 && sides == 15) big.Add((s / c, i));
        }
        foreach (var b in big.OrderByDescending(b => b.prom).Take(6))
            sb.AppendLine($"NORMAL RING 10-20mm: prom {b.prom * 1000:0.00}mm at ({p3[b.i].X:0.000},{p3[b.i].Y:0.000},{p3[b.i].Z:0.000})");
        foreach (var l in log) sb.AppendLine(l);
        if (plan is null) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "proteus-bust-midline.txt"), sb.ToString()); return; }
        sb.AppendLine("region vertices WITHOUT breast weight, by height: count, x range, z range");
        for (float y = 0.9f; y < 1.6f; y += 0.02f)
        {
            var hit = Enumerable.Range(0, vc).Where(i => bust[i] <= 0f && plan.NodeWeight[plan.NodeOf[i]] > 0f
                                                         && p3[i].Y >= y && p3[i].Y < y + 0.02f).ToList();
            if (hit.Count == 0) continue;
            sb.AppendLine($"  y {y:0.00}: {hit.Count,4}  x {hit.Min(i => p3[i].X):0.000}..{hit.Max(i => p3[i].X):0.000}  z {hit.Min(i => p3[i].Z):0.000}..{hit.Max(i => p3[i].Z):0.000}");
        }
        var reg = Enumerable.Range(0, vc).Where(i => plan.NodeWeight[plan.NodeOf[i]] > 0f).ToList();
        sb.AppendLine($"region box x {reg.Min(i => p3[i].X):0.000}..{reg.Max(i => p3[i].X):0.000} y {reg.Min(i => p3[i].Y):0.000}..{reg.Max(i => p3[i].Y):0.000} z {reg.Min(i => p3[i].Z):0.000}..{reg.Max(i => p3[i].Z):0.000}");
        foreach (int i in reg.Where(i => p3[i].Z < 0.05f || p3[i].Y < 1.10f || p3[i].Y > 1.36f).Take(20))
            sb.AppendLine($"  odd ({p3[i].X:0.000},{p3[i].Y:0.000},{p3[i].Z:0.000}) bust {bust[i]:0.00} w {plan.NodeWeight[plan.NodeOf[i]]:0.00}");
        sb.AppendLine("region vertices past |x| 0.12:");
        foreach (int i in Enumerable.Range(0, vc).Where(i => plan.NodeWeight[plan.NodeOf[i]] > 0f && MathF.Abs(p3[i].X) > 0.12f).Take(30))
            sb.AppendLine($"  ({p3[i].X:0.000},{p3[i].Y:0.000},{p3[i].Z:0.000}) n({n3[i].X:0.00},{n3[i].Y:0.00},{n3[i].Z:0.00}) bust {bust[i]:0.00} w {plan.NodeWeight[plan.NodeOf[i]]:0.00}");
        sb.AppendLine("y      z     | bust w  region w  lift mm   (front vertices |x| < 4mm, and the column at |x| 20..30mm)");
        foreach (var (lo, hi, label) in new[] { (0f, 0.004f, "midline"), (0.02f, 0.03f, "20-30mm out") })
        {
            sb.AppendLine($"--- {label}");
            for (int i = 0; i < vc; i++)
            {
                float ax = MathF.Abs(p3[i].X);
                if (ax < lo || ax >= hi || p3[i].Z < 0.05f || p3[i].Y < 1.10f || p3[i].Y > 1.30f) continue;
                var d = plan.Delta[i];
                sb.AppendLine($"{p3[i].Y:0.000} {p3[i].Z * 1000,6:0.0} | {bust[i],5:0.00} {plan.NodeWeight[plan.NodeOf[i]],8:0.00} {MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z) * 1000,7:0.0}");
            }
        }
        var lines = sb.ToString();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "proteus-bust-midline.txt"), lines);
    }

    /// <summary>
    /// SCRATCH: per height band, which bones the dumped body parts weight their vertices to — split front
    /// (z &gt; 0) and back — so a region chosen from bone weights can be checked against where it should reach.
    /// </summary>
    [Fact]
    public void RegionBonesByHeight()
    {
        var dump = Path.Combine(Path.GetTempPath(), "proteus-gen3-dump");
        if (!File.Exists(Path.Combine(dump, "host0_body0.mdl"))) return;
        var sb = new System.Text.StringBuilder();
        for (int part = 0; File.Exists(Path.Combine(dump, $"host0_body{part}.mdl")); part++)
        {
            if (!SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(Path.Combine(dump, $"host0_body{part}.mdl")),
                    out var p, out _, out _, out var w, out _)) continue;
            sb.AppendLine($"== body{part}: {p.Length / 3} vertices");
            foreach (bool front in new[] { true, false })
            {
                sb.AppendLine(front ? "  FRONT (z>0)" : "  BACK (z<0)");
                for (float y = 0.70f; y < 1.40f; y += 0.02f)
                {
                    var sum = new Dictionary<string, float>();
                    int n = 0;
                    for (int v = 0; v < p.Length / 3 && v < w.Length; v++)
                    {
                        float vy = p[v * 3 + 1], vz = p[v * 3 + 2];
                        if (vy < y || vy >= y + 0.02f || (vz > 0) != front || MathF.Abs(p[v * 3]) > 0.12f) continue;
                        n++;
                        foreach (var (bone, wt) in w[v])
                            sum[bone] = sum.GetValueOrDefault(bone) + wt;
                    }
                    if (n == 0) continue;
                    sb.AppendLine($"    y {y:0.00} n={n,4}: " + string.Join("  ",
                        sum.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} {kv.Value / n:0.00}")));
                }
            }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "proteus-region-bones.txt"), sb.ToString());
    }

    /// <summary>
    /// SCRATCH: which vertices of a gen3 part the gen3 → bibo conversion cannot place, and where they are on the
    /// body. Those keep their authored gen3 UV inside a bibo shell, so they read the coverage and the art from the
    /// wrong place. Reads <c>%TEMP%\proteus-gen3-dump</c>; does nothing without it.
    /// </summary>
    [Fact]
    public void Gen3PartsUnplacedInBibo()
    {
        var dump = Path.Combine(Path.GetTempPath(), "proteus-gen3-dump");
        if (!File.Exists(Path.Combine(dump, "host0_inputs.txt"))) return;
        var sb = new StringBuilder();
        void W(string l) { o.WriteLine(l); sb.AppendLine(l); }

        // The maps ship in the plugin project, not beside the test binaries.
        var pluginDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Proteus"));
        var conv = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), pluginDir)
            .UvConverter("gen3", "bibo");
        W($"maps from {pluginDir}");
        if (conv == null) { W("no gen3 -> bibo converter (maps missing?)"); File.WriteAllText(Path.Combine(Path.GetTempPath(), "proteus-gen3-unplaced.txt"), sb.ToString()); return; }

        var text = File.ReadAllLines(Path.Combine(dump, "host0_inputs.txt"));
        for (int i = 0; File.Exists(Path.Combine(dump, $"host0_body{i}.mdl")); i++)
        {
            var line = text.First(l => l.StartsWith($"source[{i}] "));
            if (!line.Contains("uvConv=yes")) continue;
            var bytes = File.ReadAllBytes(Path.Combine(dump, $"host0_body{i}.mdl"));
            foreach (var mat in SecondSkinWriter.DrawnMaterialNames(bytes))
            {
                if (!SecondSkinWriter.TryReadLod0Geometry(bytes, out var p, out var uv, out var t, out _, out _,
                        keepMaterial: m => m == mat) || t.Length == 0) continue;
                var used = new HashSet<int>(t);
                int n = p.Length / 3;
                // The writer shifts a mesh onto the [0,1] tile by the floor of its minimum UV before converting.
                float minU = float.MaxValue, minV = float.MaxValue;
                foreach (int v in used) { minU = MathF.Min(minU, uv[v * 2]); minV = MathF.Min(minV, uv[v * 2 + 1]); }
                float uOff = MathF.Floor(minU), vOff = MathF.Floor(minV);

                var misses = new List<(float X, float Y, float Z, float U, float V)>();
                foreach (int v in used)
                {
                    float u = uv[v * 2] - uOff, vv = uv[v * 2 + 1] - vOff;
                    if (conv(u, vv, p[v * 3] >= 0 ? 1 : -1) == null)
                        misses.Add((p[v * 3], p[v * 3 + 1], p[v * 3 + 2], u, vv));
                }
                W($"body{i} {mat}: {used.Count} used vertices, {misses.Count} unplaced");
                if (misses.Count == 0) continue;
                foreach (var g in misses.GroupBy(m => (int)MathF.Floor(m.Y * 20)).OrderBy(g => g.Key))
                    W($"  y {g.Key / 20f:0.00}..{(g.Key + 1) / 20f:0.00}: {g.Count(),5}  x {g.Min(m => m.X):0.000}..{g.Max(m => m.X):0.000}"
                      + $"  z {g.Min(m => m.Z):0.000}..{g.Max(m => m.Z):0.000}"
                      + $"  uv u {g.Min(m => m.U):0.000}..{g.Max(m => m.U):0.000} v {g.Min(m => m.V):0.000}..{g.Max(m => m.V):0.000}");
            }
        }
        // IS THE SHELL THERE AT ALL? For every skin triangle of every source, whether the game's shell has a
        // triangle within 3 mm straight out from its centre — binned by height, so a band of body with no shell
        // over it shows as a band of misses.
        if (SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(Path.Combine(dump, "host0_shell.mdl")),
                out var sp, out _, out var st, out _, out _, keepMaterial: m => m.Contains("ril_")))
        {
            SecondSkinWriter.Vec3 S(int k) => new(sp[k * 3], sp[k * 3 + 1], sp[k * 3 + 2]);
            const float cell = 0.004f;
            (int, int, int) C(float x, float y, float z) =>
                ((int)MathF.Floor(x / cell), (int)MathF.Floor(y / cell), (int)MathF.Floor(z / cell));
            var hash = new Dictionary<(int, int, int), List<int>>();
            for (int k = 0; k + 2 < st.Length; k += 3)
            {
                var a = S(st[k]); var b = S(st[k + 1]); var c = S(st[k + 2]);
                var lo = C(MathF.Min(a.X, MathF.Min(b.X, c.X)), MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), MathF.Min(a.Z, MathF.Min(b.Z, c.Z)));
                var hi = C(MathF.Max(a.X, MathF.Max(b.X, c.X)), MathF.Max(a.Y, MathF.Max(b.Y, c.Y)), MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));
                for (int x = lo.Item1; x <= hi.Item1; x++)
                for (int y = lo.Item2; y <= hi.Item2; y++)
                for (int z = lo.Item3; z <= hi.Item3; z++)
                    (hash.TryGetValue((x, y, z), out var l) ? l : hash[(x, y, z)] = []).Add(k);
            }
            for (int i = 0; File.Exists(Path.Combine(dump, $"host0_body{i}.mdl")); i++)
            {
                if (!SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(Path.Combine(dump, $"host0_body{i}.mdl")),
                        out var bp, out _, out var bt, out _, out _)) continue;   // skin materials only
                var bands = new SortedDictionary<int, (int Tris, int Covered, float MinX, float MaxX)>();
                for (int k = 0; k + 2 < bt.Length; k += 3)
                {
                    float cx = (bp[bt[k] * 3] + bp[bt[k + 1] * 3] + bp[bt[k + 2] * 3]) / 3f;
                    float cy = (bp[bt[k] * 3 + 1] + bp[bt[k + 1] * 3 + 1] + bp[bt[k + 2] * 3 + 1]) / 3f;
                    float cz = (bp[bt[k] * 3 + 2] + bp[bt[k + 1] * 3 + 2] + bp[bt[k + 2] * 3 + 2]) / 3f;
                    bool covered = false;
                    for (int dx = -1; dx <= 1 && !covered; dx++)
                    for (int dy = -1; dy <= 1 && !covered; dy++)
                    for (int dz = -1; dz <= 1 && !covered; dz++)
                    {
                        var key = C(cx, cy, cz);
                        if (!hash.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var list)) continue;
                        foreach (int s in list)
                        {
                            var a = S(st[s]); var b = S(st[s + 1]); var c = S(st[s + 2]);
                            var q = new SecondSkinWriter.Vec3(cx, cy, cz);
                            float ex = MathF.Max(Dist(a, b), MathF.Max(Dist(b, c), Dist(c, a)));
                            // Near enough to the shell triangle's own neighbourhood: within 3 mm of a corner, or
                            // of the centre when the triangle is large.
                            var ctr = new SecondSkinWriter.Vec3((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                            if (Dist(q, ctr) < MathF.Max(0.003f, ex * 0.6f)) { covered = true; break; }
                        }
                    }
                    int band = (int)MathF.Floor(cy * 20);
                    (int Tris, int Covered, float MinX, float MaxX) bb =
                        bands.TryGetValue(band, out var had) ? had : (0, 0, float.MaxValue, float.MinValue);
                    bands[band] = (bb.Tris + 1, bb.Covered + (covered ? 1 : 0),
                                   covered ? bb.MinX : MathF.Min(bb.MinX, cx), covered ? bb.MaxX : MathF.Max(bb.MaxX, cx));
                }
                W($"body{i}: skin triangles with the shell over them, by height");
                foreach (var (band, v) in bands)
                    W($"  y {band / 20f:0.00}..{(band + 1) / 20f:0.00}: {v.Covered,5}/{v.Tris,-5}"
                      + (v.Covered < v.Tris ? $"  uncovered x {v.MinX:0.000}..{v.MaxX:0.000}" : ""));
            }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "proteus-gen3-unplaced.txt"), sb.ToString());

        static float Dist(SecondSkinWriter.Vec3 a, SecondSkinWriter.Vec3 b)
            => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    }

    /// <summary>
    /// SCRATCH: a front orthographic render of the dumped body parts with the game's shell over them, depth
    /// tested, as raw RGB (<c>%TEMP%\proteus-gen3-front.rgb</c>, width and height in the file name's sibling
    /// .txt). Skin colour = the body wins that pixel (shell missing or behind it); grey = the shell wins; the
    /// shell alone in the second half of the image. Bind pose, so it shows geometry, not skinning.
    /// </summary>
    [Fact]
    public void Gen3FrontRender() => FrontRender(back: false, -0.25f, 0.25f, 0.70f, 1.20f, "proteus-gen3");

    /// <summary>SCRATCH: <see cref="Gen3FrontRender"/> from BEHIND, zoomed on the small of the back.</summary>
    [Fact]
    public void Gen3BackWaistRender()
    {
        FrontRender(back: true, -0.18f, 0.18f, 0.92f, 1.12f, "proteus-gen3-back");
        FrontRender(back: true, -0.18f, 0.18f, 0.92f, 1.12f, "proteus-gen3-back-off", "proteus-fold-clearance.replay-off.mdl");
        // The game's own shell, whatever replay exists: a name that is never written forces the dump's file.
        FrontRender(back: true, -0.20f, 0.20f, 0.78f, 1.10f, "proteus-gen3-back-game", "(the game's own shell)");
        FrontRender(back: true, -0.20f, 0.20f, 0.78f, 1.10f, "proteus-gen3-back-game-off", "proteus-fold-clearance.replay-off.mdl");
        FrontRender(back: true, -0.20f, 0.20f, 0.78f, 1.10f, "proteus-gen3-back-replay");
    }

    private void FrontRender(bool back, float x0, float x1, float y0, float y1, string name,
                             string replayName = "proteus-fold-clearance.replay.mdl")
    {
        var dump = Path.Combine(Path.GetTempPath(), "proteus-gen3-dump");
        if (!File.Exists(Path.Combine(dump, "host0_shell.mdl"))) return;
        const int W = 400, H = 400;
        var depthBody = new float[W * H];
        var depthShell = new float[W * H];
        var shade = new byte[W * H];
        Array.Fill(depthBody, float.NegativeInfinity);
        Array.Fill(depthShell, float.NegativeInfinity);

        void Raster(float[] p, int[] t, float[] depth, bool keepShade)
        {
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                // From behind is the same view turned half a turn about Y: x and z both flip.
                float f = back ? -1f : 1f;
                float ax = f * p[t[k] * 3], ay = p[t[k] * 3 + 1], az = f * p[t[k] * 3 + 2];
                float bx = f * p[t[k + 1] * 3], by = p[t[k + 1] * 3 + 1], bz = f * p[t[k + 1] * 3 + 2];
                float cx = f * p[t[k + 2] * 3], cy = p[t[k + 2] * 3 + 1], cz = f * p[t[k + 2] * 3 + 2];
                float Px(float x) => (x - x0) / (x1 - x0) * (W - 1);
                float Py(float y) => (y1 - y) / (y1 - y0) * (H - 1);
                float pax = Px(ax), pay = Py(ay), pbx = Px(bx), pby = Py(by), pcx = Px(cx), pcy = Py(cy);
                int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(pax, MathF.Min(pbx, pcx))));
                int maxX = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(pax, MathF.Max(pbx, pcx))));
                int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(pay, MathF.Min(pby, pcy))));
                int maxY = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(pay, MathF.Max(pby, pcy))));
                float area = (pbx - pax) * (pcy - pay) - (pcx - pax) * (pby - pay);
                if (MathF.Abs(area) < 1e-9f) continue;
                // Lambert-ish shade from the face normal's z, for the shell layer only.
                float ux = bx - ax, uy = by - ay, uz = bz - az, vx = cx - ax, vy = cy - ay, vz = cz - az;
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                byte s = (byte)(90 + 165 * MathF.Abs(nl > 0 ? nz / nl : 0));
                for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float w0 = ((pbx - x) * (pcy - y) - (pcx - x) * (pby - y)) / area;
                    float w1 = ((pcx - x) * (pay - y) - (pax - x) * (pcy - y)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * az + w1 * bz + w2 * cz;
                    int i = y * W + x;
                    if (z <= depth[i]) continue;
                    depth[i] = z;
                    if (keepShade) shade[i] = s;
                }
            }
        }

        for (int i = 0; File.Exists(Path.Combine(dump, $"host0_body{i}.mdl")); i++)
            if (SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(Path.Combine(dump, $"host0_body{i}.mdl")),
                    out var bp, out _, out var bt, out _, out _))
                Raster(bp, bt, depthBody, false);
        // The latest replay of this dump when one exists (FoldClearanceFromGameShell writes it), so a fix can be
        // looked at before the game rebuilds; otherwise the game's own shell.
        var replay = Path.Combine(Path.GetTempPath(), replayName);
        var shellBytes = File.ReadAllBytes(File.Exists(replay) ? replay : Path.Combine(dump, "host0_shell.mdl"));
        if (!SecondSkinWriter.TryReadLod0Geometry(shellBytes,
                out var sp, out _, out var st, out _, out _, keepMaterial: m => m.Contains("ril_"))) return;
        Raster(sp, st, depthShell, true);

        // Left: shell over body. Right: shell alone.
        var rgb = new byte[W * 2 * H * 3];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = y * W + x;
            int l = (y * W * 2 + x) * 3, r = (y * W * 2 + W + x) * 3;
            bool body = !float.IsNegativeInfinity(depthBody[i]), shell = !float.IsNegativeInfinity(depthShell[i]);
            (byte R, byte G, byte B) left = shell && (!body || depthShell[i] >= depthBody[i] - 1e-5f)
                ? (shade[i], shade[i], shade[i])
                : body ? ((byte)214, (byte)150, (byte)110) : ((byte)20, (byte)20, (byte)40);
            rgb[l] = left.R; rgb[l + 1] = left.G; rgb[l + 2] = left.B;
            (byte R, byte G, byte B) right = shell ? (shade[i], shade[i], shade[i]) : ((byte)20, (byte)20, (byte)40);
            rgb[r] = right.R; rgb[r + 1] = right.G; rgb[r + 2] = right.B;
        }
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"{name}-front.rgb"), rgb);

        // Each body source on its own, shaded, side by side — which part owns the skin in front of the shell.
        var parts = new List<byte[]>();
        for (int i = 0; File.Exists(Path.Combine(dump, $"host0_body{i}.mdl")); i++)
        {
            Array.Fill(depthShell, float.NegativeInfinity);
            Array.Clear(shade);
            if (SecondSkinWriter.TryReadLod0Geometry(File.ReadAllBytes(Path.Combine(dump, $"host0_body{i}.mdl")),
                    out var bp, out _, out var bt, out _, out _))
                Raster(bp, bt, depthShell, true);
            var img = new byte[W * H * 3];
            for (int k = 0; k < W * H; k++)
            {
                byte s = float.IsNegativeInfinity(depthShell[k]) ? (byte)20 : shade[k];
                img[k * 3] = s; img[k * 3 + 1] = s; img[k * 3 + 2] = float.IsNegativeInfinity(depthShell[k]) ? (byte)40 : s;
            }
            parts.Add(img);
        }
        var strip = new byte[W * parts.Count * H * 3];
        for (int p2 = 0; p2 < parts.Count; p2++)
            for (int y = 0; y < H; y++)
                Buffer.BlockCopy(parts[p2], y * W * 3, strip, (y * W * parts.Count + p2 * W) * 3, W * 3);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"{name}-parts-{parts.Count}.rgb"), strip);
    }

    /// <summary>Pairs of vertices at the same position whose stored normals differ, bucketed by height and
    /// reported with the worst disagreement in each band.</summary>
    private static IEnumerable<string> NormalSplits(byte[] m)
    {
        if (!SecondSkinWriter.TryReadLod0Geometry(m, out var pos, out _, out _, out _, out var nrm))
            yield break;

        var at = new Dictionary<(long, long, long), List<int>>();
        const float Q = 0.0001f;
        for (int v = 0; v < pos.Length / 3; v++)
        {
            var key = ((long)MathF.Floor(pos[v * 3] / Q),
                       (long)MathF.Floor(pos[v * 3 + 1] / Q),
                       (long)MathF.Floor(pos[v * 3 + 2] / Q));
            if (!at.TryGetValue(key, out var list)) at[key] = list = [];
            list.Add(v);
        }

        var count = new SortedDictionary<int, int>();
        var worst = new SortedDictionary<int, float>();
        foreach (var list in at.Values)
        {
            if (list.Count < 2) continue;
            for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                int a = list[i], b = list[j];
                float dot = nrm[a * 3] * nrm[b * 3] + nrm[a * 3 + 1] * nrm[b * 3 + 1]
                          + nrm[a * 3 + 2] * nrm[b * 3 + 2];
                float deg = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 180f / MathF.PI;
                if (deg < 1f) continue;
                int band = (int)MathF.Floor(pos[a * 3 + 1] * 20);
                count[band] = count.GetValueOrDefault(band) + 1;
                if (deg > worst.GetValueOrDefault(band)) worst[band] = deg;
            }
        }
        foreach (var (band, n) in count)
            if (n > 10)
                yield return $"y {band / 20f:F2}..{(band + 1) / 20f:F2} — {n} pair(s), worst {worst[band]:F1} deg";
    }

    /// <summary>Open (single-use) edges of a model's LOD0, counted into 5cm height bands.</summary>
    private static SortedDictionary<int, int> OpenEdgesByHeight(byte[] m)
    {
        var edges = new Dictionary<(int, int), int>();
        var pos = new Dictionary<int, float>();
        ForEachTriangle(m, (a, b, c, ya, yb, yc) =>
        {
            pos[a] = ya; pos[b] = yb; pos[c] = yc;
            Bump(a, b); Bump(b, c); Bump(c, a);
            void Bump(int x, int y)
            {
                var e = x < y ? (x, y) : (y, x);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        });

        var bands = new SortedDictionary<int, int>();
        foreach (var (e, n) in edges)
        {
            if (n != 1) continue;
            float y = 0.5f * (pos[e.Item1] + pos[e.Item2]);
            int band = (int)MathF.Floor(y * 20);
            bands[band] = bands.GetValueOrDefault(band) + 1;
        }
        return bands;
    }

    /// <summary>Walk a model's LOD0 triangles, handing each corner's index and Y to the caller.</summary>
    private static void ForEachTriangle(byte[] m, Action<int, int, int, float, float, float> onTri)
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
        uint vtxOff = U32(16), idxOff = U32(28);

        int vbase = 0;
        for (int mi = 0; mi < meshCount; mi++)
        {
            int mo = meshStart + mi * 36;
            ushort vc = U16(mo);
            uint idxCount = U32(mo + 4), startIdx = U32(mo + 16);
            uint[] vOff = { U32(mo + 20), U32(mo + 24), U32(mo + 28) };
            byte[] str = { m[mo + 32], m[mo + 33], m[mo + 34] };

            int db = 0x44 + mi * 17 * 8;
            int ps = -1, po = 0, pt = 0;
            for (int e = 0; e < 17; e++)
            {
                int x = db + e * 8;
                if (m[x] == 0xFF) break;
                if (m[x + 3] == 0) { ps = m[x]; po = m[x + 1]; pt = m[x + 2]; break; }
            }
            if (ps < 0) { vbase += vc; continue; }

            float YOf(int i)
            {
                int a = (int)(vtxOff + vOff[ps]) + i * str[ps] + po;
                return pt == 14 ? (float)BitConverter.ToHalf(m, a + 2) : BitConverter.ToSingle(m, a + 4);
            }

            for (uint i = 0; i + 2 < idxCount; i += 3)
            {
                int ia = (int)(idxOff + (startIdx + i) * 2);
                if (ia + 6 > m.Length) break;
                int a = BitConverter.ToUInt16(m, ia);
                int b = BitConverter.ToUInt16(m, ia + 2);
                int c = BitConverter.ToUInt16(m, ia + 4);
                if (a >= vc || b >= vc || c >= vc) continue;
                onTri(vbase + a, vbase + b, vbase + c, YOf(a), YOf(b), YOf(c));
            }
            vbase += vc;
        }
    }

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

            // Matched against the field the dump ACTUALLY writes, per source, since the flag went
            // per-source. The old test looked for a bare "skipConnectors=True" line that stopped being
            // written then, so this replayed with the pass off no matter what the shell was built with —
            // exactly the kind of silent divergence the class doc above warns about.
            bool skip = Array.Exists(text, l => l.Contains("dropRedundant=True"));
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
                         Path.Combine(AppContext.BaseDirectory, "Meshes", "toecap.neolithe.mdl"),
                         @"E:\repos\Proteus\Proteus\Meshes\toecap.neolithe.mdl",
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

            // THE CUT MASKS, in atlas space. Every argument about the cut so far has been made from texel
            // counts, which cannot tell two completely different regions of the same size apart. One
            // colour per mask plus a composite, so "is the shadow mask even landing on the toes" and "is
            // the painted map's extra area one band or several islands" are answerable by looking.
            var maskLoader = new TextureLoader(null!, new NullLog());
            var masks = new Dictionary<string, (byte[] M, int N)>();
            SecondSkinWriter.MaskDump = (nm, m, side) =>
            {
                if (masks.ContainsKey(nm)) return;      // first layer only
                masks[nm] = ((byte[])m.Clone(), side);
                var rgba = new byte[side * side * 4];
                for (int i = 0; i < side * side; i++)
                {
                    byte v = m[i] >= 128 ? (byte)255 : (byte)0;
                    rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = v;
                    rgba[i * 4 + 3] = 255;
                }
                maskLoader.WritePng(rgba, side, side, Path.Combine(Scratch, $"cutmask_{nm}.png"));
                int lit = 0;
                foreach (var px in m) if (px >= 128) lit++;
                o.WriteLine($"cut mask '{nm}': {side}x{side}, {lit} texels lit");
            };

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

            SecondSkinWriter.MaskDump = null;
            // Composite: red = painted only, green = shadow only, yellow = both. Where the painted map
            // takes out shell the cap has no claim on shows as red; where the 3D test finds skin the
            // painted map misses shows as green.
            if (masks.TryGetValue("painted", out var pm) && masks.TryGetValue("shadow", out var sm)
                && pm.N == sm.N)
            {
                int n = pm.N;
                var rgba = new byte[n * n * 4];
                int both = 0, pOnly = 0, sOnly = 0;
                for (int i = 0; i < n * n; i++)
                {
                    bool p = pm.M[i] >= 128, s = sm.M[i] >= 128;
                    rgba[i * 4] = (byte)(p ? 255 : 0);
                    rgba[i * 4 + 1] = (byte)(s ? 255 : 0);
                    rgba[i * 4 + 3] = 255;
                    if (p && s) both++; else if (p) pOnly++; else if (s) sOnly++;
                }
                maskLoader.WritePng(rgba, n, n, Path.Combine(Scratch, "cutmask_compare.png"));
                o.WriteLine($"cut masks: {both} texels in both, {pOnly} painted-only, {sOnly} shadow-only");
            }

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
    /// how far that moves it. Writes Proteus/Meshes/toecap.neolithe.bind on success.
    /// </summary>
    [Fact]
    public void BakeAndCheckCapBind()
    {
        var capPath = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Meshes", "toecap.neolithe.mdl"),
            @"E:\repos\Proteus\Proteus\Meshes\toecap.neolithe.mdl",
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

        // OPT IN TO SHIPPING IT. This used to write on every run, and it is an ordinary [Fact] — so every
        // `dotnet test` silently replaced the Neolithe binding the plugin ships, and which one was in the
        // build stopped being anybody's decision. It cost most of a session: the file kept coming back
        // modified after being restored, and Neolithe kept changing behaviour between builds for no
        // reason visible in the diff. Same gate as BakeCapBindForEquippedBody, which had it from the
        // start; measuring the round trip is the useful part and that still runs unconditionally.
        if (Environment.GetEnvironmentVariable("PROTEUS_WRITE_BIND") != "1")
        {
            o.WriteLine("not writing toecap.neolithe.bind — set PROTEUS_WRITE_BIND=1 to ship this bake");
            return;
        }
        var outPath = Path.Combine(@"E:\repos\Proteus\Proteus\Meshes", "toecap.neolithe.bind");
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
                          Path.Combine(AppContext.BaseDirectory, "Meshes", "toecap.neolithe.mdl"),
                          @"E:\repos\Proteus\Proteus\Meshes\toecap.neolithe.mdl",
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

        // Beside the cap it was measured from, not at a fixed absolute path. The old path named the
        // primary checkout, so running this from a git worktree wrote the binding into a directory that
        // only exists on the branch - and silently, since nothing here checked.
        var outPath = Path.Combine(Path.GetDirectoryName(capPath)!, $"toecap.{name}.bind");
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
    /// Convert any .mdl to .obj so it can be opened in a modelling package. Aimed at the SHIPPED shell —
    /// the file Penumbra actually serves the game — rather than anything the harness rebuilds, because
    /// "is what I am looking at what I measured" is otherwise a matter of trust.
    /// <para/>
    /// Opt-in: set PROTEUS_OBJ_IN (the model) and optionally PROTEUS_OBJ_OUT (where the .obj lands,
    /// default beside the input). Does nothing without them, so an ordinary test run is unaffected.
    /// </summary>
    [Fact]
    public void ExportModelToObj()
    {
        var inPath = Environment.GetEnvironmentVariable("PROTEUS_OBJ_IN");
        if (string.IsNullOrWhiteSpace(inPath) || !File.Exists(inPath)) return;
        var outPath = Environment.GetEnvironmentVariable("PROTEUS_OBJ_OUT")
                   ?? Path.ChangeExtension(inPath, ".obj");
        var bytes = File.ReadAllBytes(inPath);
        WriteObj(bytes, outPath);
        o.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length} bytes) from {inPath} ({bytes.Length} bytes)");

        // ...and a cut-down copy of just the meshes named in PROTEUS_OBJ_ONLY (comma-separated, e.g.
        // "mesh3,mesh4"). A whole second skin is ten meshes of body, and the join is two of them; opening
        // the small file is the difference between inspecting the seam and looking for it.
        var only = Environment.GetEnvironmentVariable("PROTEUS_OBJ_ONLY");
        if (string.IsNullOrWhiteSpace(only)) return;
        var keep = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trimmed = Path.Combine(Path.GetDirectoryName(outPath)!,
                                   Path.GetFileNameWithoutExtension(outPath) + "_join.obj");
        // Vertex/uv/normal lines are shared by every mesh and indices are absolute, so keeping all of them
        // and dropping only the unwanted faces keeps every index valid without renumbering anything.
        using (var w = new StreamWriter(trimmed))
        {
            string cur = "";
            foreach (var ln in File.ReadLines(outPath))
            {
                if (ln.StartsWith("o ", StringComparison.Ordinal) || ln.StartsWith("g ", StringComparison.Ordinal))
                { cur = ln[2..].Trim(); if (keep.Contains(cur)) w.WriteLine(ln); continue; }
                if (ln.StartsWith("f ", StringComparison.Ordinal)) { if (keep.Contains(cur)) w.WriteLine(ln); continue; }
                w.WriteLine(ln);
            }
        }
        o.WriteLine($"wrote {trimmed} keeping [{string.Join(", ", keep)}]");
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

            // BOTH `o` and `g`. A group is a face tag, and importers are free to keep only the last one —
            // which is how a ten-mesh shell opened showing nothing but the cap, that being mesh9. An
            // object statement is the one every importer splits on.
            fs.Append("o mesh").Append(mi).Append('\n');
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
