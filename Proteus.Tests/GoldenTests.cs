using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Proteus;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Byte-level baselines for the refactor. Every case hashes its output and is compared against a recorded
/// file; a refactor step that changes any hash changed behaviour.
/// <para/>
/// Run alone: <c>dotnet test --filter Category=Golden</c>. The baseline lives under
/// <c>%LOCALAPPDATA%\Proteus\golden</c> (override with <c>PROTEUS_GOLDEN_DIR</c>); a missing file is
/// recorded, <c>PROTEUS_GOLDEN=record</c> rewrites it. Shell cases need the local body mods and no-op
/// without them.
/// </summary>
[Trait("Category", "Golden")]
public class GoldenTests(ITestOutputHelper o)
{
    private static string Dir =>
        Environment.GetEnvironmentVariable("PROTEUS_GOLDEN_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Proteus", "golden");

    private const string ToeCapPng =
        @"E:\Penumbradt\Solona's Stockings - for Proteus\Proteus\Masks\Toe Cap.png";
    private const string ContentPack = @"E:\ModPacks\Neolithe Piercings for Proteus.pmp";
    private const string ContentEntry = "top/belly button heart/chara/accessory/a0112/model/c0201a0112_wrs.mdl";
    private const string ShellMaterial = "/mt_c0201a0053_rir_a.mtrl";
    private const int ToeCapSize = 512;

    // ── shells ────────────────────────────────────────────────────────────────

    [Fact]
    public void Shell_output_matches_baseline()
    {
        var cases = new Cases(o);
        var caps = ToeCapDiagTests.CapSets();
        var toeCap = File.Exists(ToeCapPng) ? ToeCapMask(ToeCapPng) : null;

        foreach (var (body, parts) in SecondSkinWriterVerbatimTests.Bodies)
        {
            if (!parts.All(File.Exists)) { o.WriteLine($"{body}: models missing, skipped"); continue; }

            // Fresh bytes per case: the parse cache keys on array identity and a build writes into the
            // cached parse, so sharing one array would let one case's connector pass leak into the next.
            List<byte[]> Models() => parts.Select(File.ReadAllBytes).ToList();

            cases.Shell($"{body}/plain", d =>
                SecondSkinWriter.Build(Models(), [Layer()], out var s) is var b ? (b, s) : default);
            cases.Shell($"{body}/ring", d =>
                SecondSkinWriter.Build(Models(), [Layer("/mt_c0201a0001_rir_b.mtrl")],
                    File.ReadAllBytes(SecondSkinWriterVerbatimTests.HostRing), out var s) is var b ? (b, s) : default);
            cases.Shell($"{body}/dropConnectors", d =>
                SecondSkinWriter.Build(Models(), [Layer()], null, true, out var s, diag: d.Add) is var b ? (b, s) : default);
            cases.Shell($"{body}/bridges", d =>
                SecondSkinWriter.Build(Models(), [Bridges()], null, false, out var s, diag: d.Add) is var b ? (b, s) : default);
            if (toeCap != null && caps.Count > 0)
                cases.Shell($"{body}/toecap", d =>
                    SecondSkinWriter.Build(Models(), [ToeCap(toeCap)], null, false, out var s, diag: d.Add,
                        authoredCaps: caps) is var b ? (b, s) : default);
            // No authored cap: the cap is generated from the mask (ToeCapSolve).
            if (toeCap != null)
                cases.Shell($"{body}/toecap-generated", d =>
                    SecondSkinWriter.Build(Models(), [ToeCap(toeCap)], null, false, out var s, diag: d.Add) is var b ? (b, s) : default);
            // The body smoothing that republishes the skin under a garment: nipples on the top, the fold on the legs.
            cases.Shell($"{body}/smooth-body-top", d =>
                (BodyBridge.SmoothBodyNipples(File.ReadAllBytes(parts[0]), Layer(), 1f, d.Add, 1f) ?? [], default));
            cases.Shell($"{body}/smooth-body-legs", d =>
                (BodyBridge.SmoothBodyNipples(File.ReadAllBytes(parts[1]), Layer(), 1f, d.Add, 1f) ?? [], default));
            // A mask over the toes only, so the generated cap actually cuts, sweeps, relaxes and stitches.
            if (ToesMask(File.ReadAllBytes(parts[3])) is { } toes)
                cases.Shell($"{body}/toecap-toes", d =>
                    SecondSkinWriter.Build(Models(), [ToeCap(toes)], null, false, out var s, diag: d.Add) is var b ? (b, s) : default);
        }

        if (File.Exists(SecondSkinWriterVerbatimTests.NeoTop) && File.Exists(SecondSkinWriterVerbatimTests.BiboTop))
            cases.Shell("merged/neo+bibo", d =>
                SecondSkinWriter.Build(
                    [File.ReadAllBytes(SecondSkinWriterVerbatimTests.NeoTop),
                     File.ReadAllBytes(SecondSkinWriterVerbatimTests.BiboTop)],
                    [Layer()], out var s) is var b ? (b, s) : default);

        if (ReadPackEntry(ContentEntry) is { } content)
        {
            var leaf = ContentPieceResolver.UsedMaterialNames(content, SecondSkinWriter.MaterialNames(content))[0];
            var keep = SecondSkinWriter.KeepByLeaf(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { leaf.TrimStart('/') });
            cases.Shell("content/pmp", d =>
                SecondSkinWriter.Build(Array.Empty<SecondSkinWriter.SourceSpec>(),
                    [new SecondSkinLayer { MaterialName = ShellMaterial, Geometry = [new ContentGeometry(content, keep, false)] }],
                    null, out var s, d.Add) is var b ? (b, s) : default);
        }

        BustCases(cases);
        cases.Compare("shell");
    }

    // ── body retarget ─────────────────────────────────────────────────────────

    private const string NeolitheChest = @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT CHEST - SmallClothes";
    private const string NeolitheExtra = @"E:\Penumbradt\Neolithe [ALL IN ONE]\EXTRA CHEST BUFF - SmallClothes";
    private const string NeolitheLegs  = @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT LEGS - SmallClothes";

    [Fact]
    public void Retarget_output_matches_baseline()
    {
        var cases = new Cases(o);

        // The garment stands in as a body model of a size other than the pair being refitted between, so the solve
        // has real geometry with a real body mesh to snap and real cloth (undies, pubes, piercings) to carry.
        Retarget(cases, "chest/XS-to-L", NeolitheChest + @"\SFW M.mdl",
                 "_top", NeolitheChest + @"\SFW XS.mdl", NeolitheChest + @"\SFW L.mdl");

        // Across shape families as well as the size axis, because the picker allows any pair.
        Retarget(cases, "chest/default-to-buff", NeolitheChest + @"\SFW M.mdl",
                 "_top", NeolitheChest + @"\SFW M.mdl", NeolitheExtra + @"\SFW M.mdl");

        Retarget(cases, "legs/small-to-large", NeolitheLegs + @"\GEN B Medium.mdl",
                 "_dwn", NeolitheLegs + @"\SFW Small.mdl", NeolitheLegs + @"\SFW Large.mdl");

        cases.Compare("retarget");
    }

    private void Retarget(Cases cases, string name, string garmentPath, string slot, string sourcePath, string targetPath)
    {
        if (!File.Exists(garmentPath) || !File.Exists(sourcePath) || !File.Exists(targetPath))
        {
            o.WriteLine($"{name}: models missing, skipped");
            return;
        }

        var garmentBytes = File.ReadAllBytes(garmentPath);
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var targetBytes = File.ReadAllBytes(targetPath);

        var garment = ModelPartReader.Read(garmentBytes);
        var source = ModelPartReader.Read(sourceBytes);
        var target = ModelPartReader.Read(targetBytes);
        if (garment == null || source == null || target == null)
        {
            cases.Text(name + "/bytes", "unreadable");
            return;
        }

        if (!IdentityCorrespondence.TryBuild(source, target, slot, out var built, out string refusal,
                                             BodyRetargetDiagTests.Uv(sourceBytes), BodyRetargetDiagTests.Uv(targetBytes)))
        {
            cases.Text(name + "/bytes", "refused: " + refusal);
            return;
        }

        var planned = BodyRetarget.Plan(garment, garmentBytes, [new BodyRetarget.SlotPair(slot, built!, target)], slot);
        cases.Bytes(name + "/bytes", planned.Model);

        // Alongside the bytes, so "the model changed" and "the solve decided something different" are separable.
        var r = planned.Report;
        cases.Text(name + "/report",
                   $"snapped {r.Snapped} transferred {r.Transferred} missed {r.Missed} pushed {r.Pushed} " +
                   $"worstMove {r.WorstMove:F6} worstPush {r.WorstPush:F6} spares {r.UnmappedSpares} " +
                   $"otherLods {r.HasOtherLods}");
    }

    private static void BustCases(Cases cases)
    {
        void Solve(string name, Func<(SecondSkinWriter.Vec3[] Pos, SecondSkinWriter.Vec3[] Nrm, ushort[] Tris, float[] Bust)> chest,
                   float strength, float smooth)
        {
            var (pos, nrm, tris, bust) = chest();
            var plan = BodyBridge.BustBridgeSolve(pos, nrm, tris, bust, strength, smoothStrength: smooth);
            if (plan == null) { cases.Text(name + "/delta", "null"); return; }
            cases.Bytes(name + "/delta", MemoryMarshal.AsBytes(plan.Delta.AsSpan()).ToArray());
            cases.Bytes(name + "/weight", MemoryMarshal.AsBytes(plan.NodeWeight.AsSpan()).ToArray());
            var n = SecondSkinWriter.RelaxedNormals(pos, nrm, plan.Delta, plan.NodeOf, plan.NodeWeight, plan.NodeNormal, tris);
            cases.Bytes(name + "/normals", MemoryMarshal.AsBytes(n.AsSpan()).ToArray());
        }

        Solve("bust/smooth", () => BustBridgeTests.Chest(), 0f, 1f);
        Solve("bust/bridge", () => BustBridgeTests.Chest(), 1f, 0f);
        Solve("bust/nipple", () => BustBridgeTests.Chest(nipple: 0.02f), 0f, 1f);
        Solve("bust/both", () => BustBridgeTests.Chest(nipple: 0.02f), 1f, 1f);
    }

    private static SecondSkinLayer Layer(string material = ShellMaterial) => new() { MaterialName = material, Coverage = null };

    private static SecondSkinLayer Bridges() => new()
    {
        MaterialName = ShellMaterial, Coverage = null,
        BustBridgeStrength = 1f, CleftBridgeStrength = 1f, NippleSmoothStrength = 1f, FoldSmoothStrength = 1f,
    };

    private static SecondSkinLayer ToeCap(byte[] mask) => new()
    {
        MaterialName = ShellMaterial, Coverage = null,
        ToeCap = mask, ToeCapWidth = ToeCapSize, ToeCapHeight = ToeCapSize, ToeCapStrength = 1f,
    };

    /// <summary>The mask's red channel, nearest-sampled to the cap size, as the service hands it to the writer.</summary>
    private static byte[] ToeCapMask(string png)
    {
        using var stream = File.OpenRead(png);
        var image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        var mask = new byte[ToeCapSize * ToeCapSize];
        for (int y = 0; y < ToeCapSize; y++)
            for (int x = 0; x < ToeCapSize; x++)
            {
                int sx = x * image.Width / ToeCapSize, sy = y * image.Height / ToeCapSize;
                mask[y * ToeCapSize + x] = image.Data[(sy * image.Width + sx) * 4];
            }
        return mask;
    }

    /// <summary>A toe-cap mask painted under the foot model's front, lowest vertices: the toes, and nothing else.</summary>
    private static byte[]? ToesMask(byte[] feet)
    {
        if (!SecondSkinWriter.TryReadLod0Geometry(feet, out var pos, out var uvs, out _)) return null;
        int n = pos.Length / 3;
        if (n == 0) return null;
        float minY = float.MaxValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            minY = Math.Min(minY, pos[i * 3 + 1]); maxY = Math.Max(maxY, pos[i * 3 + 1]);
            minZ = Math.Min(minZ, pos[i * 3 + 2]); maxZ = Math.Max(maxZ, pos[i * 3 + 2]);
        }
        var mask = new byte[ToeCapSize * ToeCapSize];
        for (int i = 0; i < n; i++)
        {
            if (pos[i * 3 + 1] > minY + 0.25f * (maxY - minY)) continue;
            if (pos[i * 3 + 2] < maxZ - 0.33f * (maxZ - minZ)) continue;
            float u = uvs[i * 2] - MathF.Floor(uvs[i * 2]), v = uvs[i * 2 + 1] - MathF.Floor(uvs[i * 2 + 1]);
            int cx = (int)(u * ToeCapSize), cy = (int)(v * ToeCapSize);
            for (int y = Math.Max(0, cy - 6); y <= Math.Min(ToeCapSize - 1, cy + 6); y++)
                for (int x = Math.Max(0, cx - 6); x <= Math.Min(ToeCapSize - 1, cx + 6); x++)
                    mask[y * ToeCapSize + x] = 255;
        }
        return mask;
    }

    private static byte[]? ReadPackEntry(string entry)
    {
        if (!File.Exists(ContentPack)) return null;
        using var zip = ZipFile.OpenRead(ContentPack);
        var e = zip.GetEntry(entry);
        if (e == null) return null;
        using var st = e.Open();
        using var ms = new MemoryStream();
        st.CopyTo(ms);
        return ms.ToArray();
    }

    // ── pixel math ────────────────────────────────────────────────────────────

    [Fact]
    public void Pixel_math_matches_baseline()
    {
        const int w = 256, h = 256, n = w * h;
        var cases = new Cases(o);

        static byte[] Rand(int seed, int len) { var b = new byte[len]; new Random(seed).NextBytes(b); return b; }
        static byte[] Plane(int seed) => Rand(seed, n);
        static byte[] Rgba(int seed) => Rand(seed, n * 4);

        // A few rectangles rather than noise: island labelling on noise is thousands of one-pixel islands.
        static byte[] Rects()
        {
            var inside = new byte[n];
            foreach (var (x0, y0, x1, y1) in new[] { (10, 10, 100, 120), (130, 20, 240, 90), (40, 150, 200, 250) })
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        inside[y * w + x] = 255;
            return inside;
        }

        static Dictionary<int, ColorTableRowOverride> Rows()
        {
            var r = new Random(7);
            var blends = Enum.GetValues<RowBlend>();
            var rows = new Dictionary<int, ColorTableRowOverride>();
            for (int i = 0; i < 16; i++)
                rows[i] = new ColorTableRowOverride
                {
                    A = new ColorTableSubRow { DiffuseR = r.NextSingle(), DiffuseG = r.NextSingle(), DiffuseB = r.NextSingle(),
                                               Emissive = r.NextSingle(), Opacity = r.Next(0, 101), Blend = blends[i % blends.Length] },
                    B = new ColorTableSubRow { DiffuseR = r.NextSingle(), DiffuseG = r.NextSingle(), DiffuseB = r.NextSingle(),
                                               Emissive = r.NextSingle(), Opacity = r.Next(0, 101), Blend = blends[(i + 1) % blends.Length] },
                };
            return rows;
        }

        var row = new ColorTableSubRow { DiffuseR = 0.8f, DiffuseG = 0.5f, DiffuseB = 0.3f, Emissive = 0.2f, Opacity = 50 };

        cases.Twice("flatOverlay", () => { var b = Rgba(1); OverlayBlend.ApplyFlatOverlay(b, Rgba(2), row, w, h); return b; });
        cases.Twice("indexedOverlay/diffuse", () => { var b = Rgba(3); OverlayBlend.ApplyIndexedOverlay(b, Rgba(4), Rgba(5), Rows(), false, w, h); return b; });
        cases.Twice("indexedOverlay/normal", () => { var b = Rgba(3); OverlayBlend.ApplyIndexedOverlay(b, Rgba(4), Rgba(5), Rows(), true, w, h); return b; });
        cases.Twice("indexedOverlay/painted", () => { var b = Rgba(3); OverlayBlend.ApplyIndexedOverlay(b, Rgba(4), Rgba(5), Rows(), false, w, h, Plane(6)); return b; });
        cases.Twice("combineMaskReliefs", () =>
        {
            var b = Rgba(8);
            OverlayBlend.CombineMaskReliefs(b, w, h, [(Rgba(9), Rgba(10)), (Rgba(11), Rgba(12))]);
            return b;
        });
        cases.Twice("compoundNormal", () => { var b = Rgba(13); OverlayBlend.CompoundNormal(b, Rgba(14), w, h, Rgba(15)); return b; });
        cases.Twice("alphaComposite", () => { var b = Rgba(16); OverlayBlend.AlphaComposite(b, Rgba(17), w, h, Rgba(18)); return b; });
        cases.Twice("suppressSkinColor", () => { var b = Rgba(19); OverlayBlend.SuppressSkinColorInfluence(b, Rgba(20), Rgba(21), w, h, 0.8f); return b; });
        cases.Twice("maxFilter", () => OverlayBlend.MaxFilter(Plane(22), w, h, 3));
        cases.Twice("blurCoverage", () => OverlayBlend.BlurCoverage(Plane(23), w, h, 4));
        cases.Twice("ambientOcclusion", () =>
        {
            var b = Rgba(24);
            OverlayBlend.ApplyAmbientOcclusion(b, Plane(25), OverlayBlend.BlurCoverage(Plane(25), w, h, 4), w, h, 0.7f, Plane(26), Plane(27));
            return b;
        });
        cases.Twice("normalIndent", () =>
        {
            var b = Rgba(28);
            OverlayBlend.ApplyNormalIndent(b, OverlayBlend.BlurCoverage(Plane(29), w, h, 4), Plane(29), w, h, 1f, Plane(30), 6, Rects(), Plane(31));
            return b;
        });
        cases.Twice("extendIntoPadding", () => IslandBlur.ExtendIntoPadding(Plane(32), Rects(), w, h, 8));
        cases.Twice("islands", () =>
        {
            var labels = IslandBlur.LabelIslands(Rects(), w, h, out int count);
            var owner = IslandBlur.NearestIslandOwner(labels, w, h);
            var within = IslandBlur.BlurCoverageWithinIslands(Plane(33), labels, owner, count, Rects(), null, w, h, 6);
            return [.. BitConverter.GetBytes(count), .. MemoryMarshal.AsBytes(labels.AsSpan()), .. MemoryMarshal.AsBytes(owner.AsSpan()), .. within];
        });
        cases.Twice("snapIndexRows", () => { var b = Rgba(34); OverlayBlend.SnapIndexRowsToDefined(b, w, h, [0, 3, 7, 12]); return b; });
        cases.Twice("paintCoverage", () => OverlayBlend.PaintCoverage(Rgba(35), Rgba(36), Rows(), w, h, hasIndex: true));
        cases.Twice("paintCoverage/noIndex", () => OverlayBlend.PaintCoverage(Rgba(35), null, Rows(), w, h));
        cases.Twice("indexedOpacity", () => OverlayBlend.ApplyIndexedOpacity(Rgba(37), Rgba(38), Rows()));
        cases.Twice("scaleOverlayAlpha", () => OverlayBlend.ScaleOverlayAlpha(Rgba(39), 128));
        cases.Twice("coverageMask", () => OverlayBlend.ApplyCoverageMask(Rgba(40), Plane(41), Plane(42)));
        cases.Twice("coverageMask/replace", () => OverlayBlend.ApplyCoverageMask(Rgba(40), Plane(41), Plane(42), additive: false));

        cases.Compare("pixels");
    }

    // ── recording and comparing ───────────────────────────────────────────────

    private sealed class Cases(ITestOutputHelper o)
    {
        private readonly List<(string Name, string Hash)> list = [];

        public void Bytes(string name, byte[] bytes) => list.Add((name, Sha(bytes)));
        public void Text(string name, string text) => Bytes(name, Encoding.UTF8.GetBytes(text));

        /// <summary>A shell build: the model bytes, the stats and the diagnostics are hashed separately so
        /// "bytes equal, diag differs" is visible.</summary>
        public void Shell(string name, Func<List<string>, (byte[] Out, SecondSkinWriter.Stats Stats)> build)
        {
            var diag = new List<string>();
            try
            {
                var (bytes, stats) = build(diag);
                Bytes(name + "/bytes", bytes);
                Text(name + "/stats", stats.ToString());
            }
            catch (Exception e)
            {
                Text(name + "/bytes", "exception " + e.GetType().Name + ": " + e.Message);
            }
            // Durations vary run to run, and TRACE lines come from a static debugging hook another test class may
            // have armed while running alongside this one; everything else is a function of the inputs.
            var text = System.Text.RegularExpressions.Regex.Replace(
                string.Join("\n", diag.Where(l => !l.StartsWith("TRACE", StringComparison.Ordinal))), @"\d+(\.\d+)?\s*ms\b", "#ms");
            Text(name + "/diag", text);
            // The text itself, so a changed diag hash can be diffed rather than guessed at.
            var diagDir = Path.Combine(Dir, "diag");
            Directory.CreateDirectory(diagDir);
            File.WriteAllText(Path.Combine(diagDir, name.Replace('/', '_') + ".txt"), text);
        }

        /// <summary>Runs the operation twice on fresh inputs: a parallel kernel must not depend on its partition.</summary>
        public void Twice(string name, Func<byte[]> run)
        {
            var first = Sha(run());
            var second = Sha(run());
            Assert.True(first == second, $"{name} is not deterministic");
            list.Add((name, first));
        }

        public void Compare(string set)
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, set + ".json");
            var current = list.ToDictionary(c => c.Name, c => c.Hash);
            var record = string.Equals(Environment.GetEnvironmentVariable("PROTEUS_GOLDEN"), "record",
                                       StringComparison.OrdinalIgnoreCase);
            if (record || !File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true }));
                o.WriteLine($"recorded {list.Count} cases to {path}");
                return;
            }

            var baseline = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
            var problems = new List<string>();
            foreach (var (name, hash) in list)
            {
                if (!baseline.TryGetValue(name, out var want)) problems.Add($"new case {name} (re-record)");
                else if (want != hash) problems.Add($"changed {name}");
            }
            foreach (var name in baseline.Keys.Except(current.Keys)) problems.Add($"missing case {name}");
            o.WriteLine($"{set}: {list.Count} cases, {problems.Count} problems");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
