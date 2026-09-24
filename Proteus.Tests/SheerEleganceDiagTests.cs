using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using XivLiveMesh;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// One reported refit, measured: "BiboPlus Sheer Elegance" upscaled from Bibo+ to Neolithe, clipping badly in the
/// breasts with weights that look wrong.
/// <para/>
/// The mod ships the same garment in four chest sizes the author fitted by hand, so the first question is which Bibo+
/// size each of those four IS — the refit recorded "Bibo+ — Perky - Small" as the size it was made for, and every
/// millimetre of the displacement field is measured from that choice. Instruments, not assertions.
/// </summary>
public class SheerEleganceDiagTests(ITestOutputHelper output)
{
    private const string Mod = @"E:\Penumbradt\BiboPlus Sheer Elegance";
    private const string BiboRoot = @"E:\Penumbradt\Bibo+";
    private const string NeolitheRoot = @"E:\Penumbradt\Neolithe [ALL IN ONE]";
    private const string Model = @"chara\equipment\e6010\model\c0201e6010_top.mdl";
    private const string Refit =
        Mod + @"\Body Retarget\Neolithe [ALL IN ONE] - DEFAULT ALMOND - NSFW Almond XS\" + Model;

    private static readonly string[] AuthorSizes = ["Small", "Medium", "Large", "XL"];

    /// <summary>
    /// Which Bibo+ size each of the author's four chest sizes is actually built on. The refit believed the one it was
    /// given was "Perky - Small"; if that is the wrong answer for the file the user had open, the field is measured
    /// from the wrong body and the breasts are where the error lands.
    /// </summary>
    [Fact]
    public void Which_Bibo_size_each_author_chest_is_built_on()
    {
        if (!Directory.Exists(Mod) || !Directory.Exists(BiboRoot)) return;

        var catalog = BodySizeCatalog.Read(BiboRoot);
        var options = catalog.For("_top", "0201");
        output.WriteLine($"Bibo+ offers {options.Count} chest options");
        if (options.Count == 0) return;

        foreach (string size in AuthorSizes)
        {
            string path = Path.Combine(Mod, "Chest size", size, Model);
            if (!File.Exists(path)) { output.WriteLine($"{size}: not installed"); continue; }

            var garment = ModelPartReader.Read(File.ReadAllBytes(path))!;
            var ranking = BodySizeMatch.Rank(garment, options, catalog.PathOf);
            output.WriteLine("");
            output.WriteLine($"=== author's \"{size}\" ({garment.Positions.Length / 3} verts) -> {ranking.Confidence}");
            foreach (var s in ranking.Scores.Take(4))
                output.WriteLine($"   {s.HitRate,7:P1}  rms {s.Rms * 1000f,7:F3} mm  {s.Option.FullLabel}");
        }
    }

    /// <summary>
    /// Which of the author's four the refit was cut from. The refit moves positions only and never the vertex count or
    /// the uvs, so the output still carries its input's numbering and texture coordinates exactly.
    /// </summary>
    [Fact]
    public void Which_author_size_the_refit_was_cut_from()
    {
        if (!File.Exists(Refit)) { output.WriteLine("no refit saved"); return; }

        byte[] refitBytes = File.ReadAllBytes(Refit);
        var refit = ModelPartReader.Read(refitBytes)!;
        output.WriteLine($"refit: {refit.Positions.Length / 3} verts, {new FileInfo(Refit).Length:N0} bytes");

        foreach (string size in AuthorSizes)
        {
            string path = Path.Combine(Mod, "Chest size", size, Model);
            if (!File.Exists(path)) continue;

            byte[] bytes = File.ReadAllBytes(path);
            var author = ModelPartReader.Read(bytes)!;
            bool sameCount = author.Positions.Length == refit.Positions.Length;
            bool sameUv = sameCount && Uv(bytes).AsSpan().SequenceEqual(Uv(refitBytes));

            // How far the refit moved it, vertex for vertex, when the numbering lines up.
            string moved = "";
            if (sameCount)
            {
                var d = new List<float>();
                for (int v = 0; v < author.Positions.Length / 3; v++)
                    d.Add((At(refit, v) - At(author, v)).Length());
                d.Sort();
                moved = $"moved mean {d.Average() * 1000f:F1} mm, p95 {d[(int)(d.Count * 0.95f)] * 1000f:F1} mm, " +
                        $"max {d[^1] * 1000f:F1} mm";
            }
            output.WriteLine($"{size,-8} {author.Positions.Length / 3,6} verts  sameCount={sameCount} " +
                             $"sameUv={sameUv}  {moved}");
        }
    }

    /// <summary>
    /// How far the refit's cloth ends up inside the body it was refitted ONTO, and how that compares with what the
    /// author already had inside their own body. Cloth inside the drawn skin is what "clipping" means here.
    /// </summary>
    [Fact]
    public void How_far_the_cloth_sits_inside_each_body()
    {
        if (!File.Exists(Refit) || !Directory.Exists(NeolitheRoot) || !Directory.Exists(BiboRoot)) return;

        var neolithe = BodySizeCatalog.Read(NeolitheRoot);
        var target = neolithe.For("_top", "0201")
                             .FirstOrDefault(o => o.FullLabel?.Contains("NSFW Almond XS", StringComparison.OrdinalIgnoreCase) == true);
        if (target is null) { output.WriteLine("target size not found"); return; }
        output.WriteLine($"target body: {target.FullLabel}");

        var targetBody = ModelPartReader.Read(File.ReadAllBytes(neolithe.PathOf(target)))!;
        Report("refit on Neolithe Almond XS", ModelPartReader.Read(File.ReadAllBytes(Refit))!, targetBody);

        var bibo = BodySizeCatalog.Read(BiboRoot);
        var source = bibo.For("_top", "0201")
                         .FirstOrDefault(o => o.FullLabel?.Contains("Perky", StringComparison.OrdinalIgnoreCase) == true
                                           && o.FullLabel?.Contains("Small", StringComparison.OrdinalIgnoreCase) == true);
        if (source is null) return;
        output.WriteLine($"source body: {source.FullLabel}");

        var sourceBody = ModelPartReader.Read(File.ReadAllBytes(bibo.PathOf(source)))!;
        foreach (string size in AuthorSizes)
        {
            string path = Path.Combine(Mod, "Chest size", size, Model);
            if (File.Exists(path))
                Report($"author \"{size}\" on Bibo+ Perky Small",
                       ModelPartReader.Read(File.ReadAllBytes(path))!, sourceBody);
        }
    }

    /// <summary>
    /// Cloth vertices inside the body, how deep, and where up the body they are — the bust sits around y 1.30-1.40 on
    /// a c0201 female, so a breast problem shows as depth concentrated there.
    /// </summary>
    private void Report(string label, ModelParts garment, ModelParts body)
    {
        var surface = new BodySurface(body, BodySurface.CellFor(MeanEdge(body)));
        var inside = new List<(float Depth, float Y)>();
        int cloth = 0, off = 0;

        for (int v = 0; v < garment.Positions.Length / 3; v++)
        {
            var p = At(garment, v);
            cloth++;
            if (!surface.Nearest(p, 0.05f, out var hit)) { off++; continue; }
            float signed = Vector3.Dot(p - hit.Point, hit.Normal);
            if (signed < 0f) inside.Add((-signed, p.Y));
        }

        output.WriteLine("");
        output.WriteLine($"--- {label}: {cloth:N0} verts, {off:N0} not over the body");
        if (inside.Count == 0) { output.WriteLine("    nothing inside"); return; }

        var depths = inside.Select(i => i.Depth).OrderBy(d => d).ToList();
        output.WriteLine($"    inside {inside.Count:N0} ({(float)inside.Count / cloth:P1})  " +
                         $"mean {depths.Average() * 1000f:F1} mm  p95 {depths[(int)(depths.Count * 0.95f)] * 1000f:F1} mm  " +
                         $"max {depths[^1] * 1000f:F1} mm");

        foreach (var band in new[] { (1.40f, 9f, "above the bust"), (1.30f, 1.40f, "bust"),
                                     (1.15f, 1.30f, "waist"), (-9f, 1.15f, "hips and below") })
        {
            var here = inside.Where(i => i.Y >= band.Item1 && i.Y < band.Item2).Select(i => i.Depth).ToList();
            if (here.Count == 0) continue;
            output.WriteLine($"      {band.Item3,-18} {here.Count,6:N0} inside, mean {here.Average() * 1000f:F1} mm, " +
                             $"max {here.Max() * 1000f:F1} mm");
        }
    }

    /// <summary>
    /// What actually drives the bust, in each model. The geometry measures clean in the bind pose, so a breast that
    /// misbehaves is the skinning: which bones the cloth over the bust is weighted to, and how much of its weight
    /// those bones carry.
    /// </summary>
    [Fact]
    public void What_drives_the_bust_in_each_model()
    {
        if (!File.Exists(Refit) || !Directory.Exists(NeolitheRoot) || !Directory.Exists(BiboRoot)) return;

        Bones("refit (Neolithe Almond XS)", File.ReadAllBytes(Refit));

        string small = Path.Combine(Mod, "Chest size", "Small", Model);
        if (File.Exists(small)) Bones("author \"Small\" (Bibo+)", File.ReadAllBytes(small));

        var neolithe = BodySizeCatalog.Read(NeolitheRoot);
        var target = neolithe.For("_top", "0201")
                             .FirstOrDefault(o => o.FullLabel?.Contains("NSFW Almond XS", StringComparison.OrdinalIgnoreCase) == true);
        if (target is not null) Bones("Neolithe body Almond XS", File.ReadAllBytes(neolithe.PathOf(target)));

        var bibo = BodySizeCatalog.Read(BiboRoot);
        var source = bibo.For("_top", "0201")
                         .FirstOrDefault(o => o.FullLabel?.Contains("Perky", StringComparison.OrdinalIgnoreCase) == true
                                           && o.FullLabel?.Contains("Small", StringComparison.OrdinalIgnoreCase) == true);
        if (source is not null) Bones("Bibo+ body Perky Small", File.ReadAllBytes(bibo.PathOf(source)));
    }

    /// <summary>
    /// The bust located by the bones that drive it, not by a guessed height: every bone whose name carries "mune",
    /// how much weight it holds, and the box its weight sits in. A bust bone missing from one model's table, or
    /// holding a different share, is the skinning difference that a bind-pose measurement cannot see.
    /// </summary>
    private void Bones(string label, byte[] mdl)
    {
        if (ModelSkinReader.Read(mdl, null, null) is not { } skin) { output.WriteLine($"{label}: unreadable"); return; }

        var weight = new Dictionary<string, float>(StringComparer.Ordinal);
        var lowY = new Dictionary<string, float>(StringComparer.Ordinal);
        var highY = new Dictionary<string, float>(StringComparer.Ordinal);
        var verts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int v = 0; v < skin.VertexCount; v++)
            for (int i = 0; i < SkinnedMesh.MaxInfluences; i++)
            {
                float w = skin.BoneWeights[v * SkinnedMesh.MaxInfluences + i];
                if (w <= 0.001f) continue;
                string bone = skin.BoneNames[skin.BoneIndices[v * SkinnedMesh.MaxInfluences + i]];
                if (!bone.Contains("mune", StringComparison.OrdinalIgnoreCase)) continue;
                weight[bone] = weight.GetValueOrDefault(bone) + w;
                verts[bone] = verts.GetValueOrDefault(bone) + 1;
                float y = skin.Positions[v].Y;
                lowY[bone] = lowY.TryGetValue(bone, out float lo) ? MathF.Min(lo, y) : y;
                highY[bone] = highY.TryGetValue(bone, out float hi) ? MathF.Max(hi, y) : y;
            }

        var busty = skin.BoneNames.Where(b => b.Contains("mune", StringComparison.OrdinalIgnoreCase)).ToList();
        output.WriteLine("");
        output.WriteLine($"--- {label}: {skin.VertexCount:N0} verts, {skin.BoneNames.Length} bones, " +
                         $"{busty.Count} of them bust bones [{string.Join(", ", busty)}]");
        if (weight.Count == 0) { output.WriteLine("    NO vertex is weighted to any bust bone"); return; }

        foreach (var (bone, w) in weight.OrderByDescending(t => t.Value))
            output.WriteLine($"      {bone,-16} weight {w,8:F1} over {verts[bone],5:N0} verts, " +
                             $"y {lowY[bone]:F3}..{highY[bone]:F3}");
    }

    /// <summary>
    /// Whether the cloth over the bust moves with the skin under it.
    /// <para/>
    /// The bind pose measures clean, so nothing shows until the bust bone turns. If the cloth is weighted to j_mune
    /// less than the skin it covers, the breast swells out through the fabric the moment it moves — which is a clip
    /// that no still measurement of the authored pose can see. Each garment is compared against its OWN skin, so
    /// there is nothing to map between models: the author's shipped pairing is the baseline the refit has to match.
    /// </summary>
    [Fact]
    public void Does_the_cloth_follow_the_skin_under_it()
    {
        if (!File.Exists(Refit)) return;

        Follow("refit (Bibo+ cloth on Neolithe skin)", File.ReadAllBytes(Refit));

        foreach (string size in AuthorSizes)
        {
            string path = Path.Combine(Mod, "Chest size", size, Model);
            if (File.Exists(path)) Follow($"author \"{size}\" as shipped (Bibo+)", File.ReadAllBytes(path));
        }
    }

    private void Follow(string label, byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } parts || ModelSkinReader.Read(mdl, null, null) is not { } skin
            || skin.VertexCount * 3 != parts.Positions.Length)
        {
            output.WriteLine($"{label}: readers disagree, skipped");
            return;
        }

        // Which vertices are the drawn body, by the material they are drawn with — the same test the skin swap uses.
        var isSkin = new bool[skin.VertexCount];
        foreach (var part in parts.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles) isSkin[v] = true;
        }

        var skinVerts = new List<int>();
        for (int v = 0; v < skin.VertexCount; v++)
            if (isSkin[v] && Bust(skin.Positions[v])) skinVerts.Add(v);

        var gaps = new List<float>();
        var pairs = new List<(float Gap, float Cloth, float Skin, float Dist)>();
        int clothCount = 0;
        for (int v = 0; v < skin.VertexCount; v++)
        {
            if (isSkin[v] || !Bust(skin.Positions[v])) continue;
            clothCount++;
            if (skinVerts.Count == 0) continue;

            var p = skin.Positions[v];
            int best = -1;
            float bestD = float.MaxValue;
            foreach (int s in skinVerts)
            {
                float d = Vector3.DistanceSquared(p, skin.Positions[s]);
                if (d < bestD) { bestD = d; best = s; }
            }
            if (best < 0) continue;

            float cloth = Mune(skin, v), under = Mune(skin, best);
            gaps.Add(cloth - under);
            pairs.Add((cloth - under, cloth, under, MathF.Sqrt(bestD)));
        }

        output.WriteLine("");
        output.WriteLine($"--- {label}: {clothCount:N0} cloth verts over the bust, {skinVerts.Count:N0} skin verts there");
        if (gaps.Count == 0) { output.WriteLine("    nothing to compare"); return; }

        gaps.Sort();
        output.WriteLine($"    cloth j_mune mean {pairs.Average(p => p.Cloth):F3}, " +
                         $"skin under it {pairs.Average(p => p.Skin):F3}, " +
                         $"nearest skin {pairs.Average(p => p.Dist) * 1000f:F1} mm away");
        output.WriteLine($"    cloth minus skin: mean {gaps.Average():F3}, " +
                         $"p05 {gaps[(int)(gaps.Count * 0.05f)]:F3}, p95 {gaps[(int)(gaps.Count * 0.95f)]:F3}");
        output.WriteLine($"    cloth weighted at least 0.10 LESS than the skin it covers: " +
                         $"{gaps.Count(g => g < -0.10f):N0} ({(float)gaps.Count(g => g < -0.10f) / gaps.Count:P1})");
    }

    /// <summary>The bust, as the bust bones themselves place it: y 1.19 to 1.35, front only.</summary>
    private static bool Bust(Vector3 p) => p.Y >= 1.19f && p.Y < 1.35f && p.Z > 0f;

    private static float Mune(SkinnedMesh skin, int v)
    {
        float sum = 0f;
        for (int i = 0; i < SkinnedMesh.MaxInfluences; i++)
        {
            float w = skin.BoneWeights[v * SkinnedMesh.MaxInfluences + i];
            if (w <= 0f) continue;
            if (skin.BoneNames[skin.BoneIndices[v * SkinnedMesh.MaxInfluences + i]]
                    .Contains("mune", StringComparison.OrdinalIgnoreCase))
                sum += w;
        }
        return sum;
    }

    /// <summary>
    /// Which Neolithe chest this refit actually fits. It was cut for "NSFW Almond XS" and measures clean against it —
    /// so if it clips in game, the question is what the character is WEARING. A refit is fitted to one size; worn over
    /// a larger one, the breast comes straight through the fabric, and nothing about the refit itself looks wrong.
    /// </summary>
    [Fact]
    public void Which_Neolithe_chest_this_refit_fits()
    {
        if (!File.Exists(Refit) || !Directory.Exists(NeolitheRoot)) return;

        var refit = ModelPartReader.Read(File.ReadAllBytes(Refit))!;
        var neolithe = BodySizeCatalog.Read(NeolitheRoot);

        output.WriteLine($"{"Neolithe chest option",-52}{"inside",9}{"mean",9}{"max",9}  (bust only, mm)");
        foreach (var option in neolithe.For("_top", "0201")
                                       .Where(o => o.FullLabel?.Contains("Almond", StringComparison.OrdinalIgnoreCase) == true)
                                       .DistinctBy(o => o.Rel))
        {
            var body = ModelPartReader.Read(File.ReadAllBytes(neolithe.PathOf(option)));
            if (body == null) continue;
            var surface = new BodySurface(body, BodySurface.CellFor(MeanEdge(body)));

            var depths = new List<float>();
            for (int v = 0; v < refit.Positions.Length / 3; v++)
            {
                var p = At(refit, v);
                if (p.Y < 1.19f || p.Y >= 1.35f || p.Z <= 0f) continue;
                if (!surface.Nearest(p, 0.05f, out var hit)) continue;
                float signed = Vector3.Dot(p - hit.Point, hit.Normal);
                if (signed < 0f) depths.Add(-signed);
            }

            string label = option.FullLabel.Length > 50 ? option.FullLabel[^50..] : option.FullLabel;
            output.WriteLine(depths.Count == 0
                ? $"{label,-52}{0,9}{"",9}{"",9}"
                : $"{label,-52}{depths.Count,9:N0}{depths.Average() * 1000f,9:F1}{depths.Max() * 1000f,9:F1}");
        }
    }

    /// <summary>
    /// What the rebuild cost in shape keys. Swapping the skin re-emits the model through the second-skin writer, which
    /// keeps LOD0 only and no shapes — and the bust is exactly where a lost shape shows, since the game drives breast
    /// variation through them.
    /// </summary>
    [Fact]
    public void What_the_refit_lost_in_shape_keys()
    {
        if (!File.Exists(Refit)) return;

        Shapes("refit", File.ReadAllBytes(Refit));
        foreach (string size in AuthorSizes)
        {
            string path = Path.Combine(Mod, "Chest size", size, Model);
            if (File.Exists(path)) Shapes($"author \"{size}\"", File.ReadAllBytes(path));
        }

        if (!Directory.Exists(NeolitheRoot)) return;
        var neolithe = BodySizeCatalog.Read(NeolitheRoot);
        var target = neolithe.For("_top", "0201")
                             .FirstOrDefault(o => o.FullLabel?.Contains("NSFW Almond XS", StringComparison.OrdinalIgnoreCase) == true);
        if (target is not null) Shapes("Neolithe body Almond XS", File.ReadAllBytes(neolithe.PathOf(target)));
    }

    private void Shapes(string label, byte[] mdl)
    {
        var parsed = SecondSkinWriter.Parse(mdl);
        var names = parsed.Shapes.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
        output.WriteLine($"{label,-26} {names.Count,3} shape keys  {string.Join(" ", names.Take(12))}");
    }

    /// <summary>
    /// Every chest refit saved into this mod, and the Neolithe size each one actually fits best. Two were saved — the
    /// one the record keeps, and an earlier one left on disk — and if they fit different sizes, the size being aimed
    /// at moved between runs.
    /// </summary>
    [Fact]
    public void Which_size_each_saved_refit_fits_best()
    {
        string folder = Mod + @"\Body Retarget";
        if (!Directory.Exists(folder) || !Directory.Exists(NeolitheRoot)) return;

        var neolithe = BodySizeCatalog.Read(NeolitheRoot);
        var bodies = neolithe.For("_top", "0201").DistinctBy(o => o.Rel)
                             .Select(o => (o.FullLabel, Body: ModelPartReader.Read(File.ReadAllBytes(neolithe.PathOf(o)))))
                             .Where(b => b.Body != null)
                             .Select(b => (b.FullLabel, Surface: new BodySurface(b.Body!, BodySurface.CellFor(MeanEdge(b.Body!)))))
                             .ToList();

        foreach (string file in Directory.GetFiles(folder, "*_top.mdl", SearchOption.AllDirectories).OrderBy(f => f))
        {
            var refit = ModelPartReader.Read(File.ReadAllBytes(file));
            if (refit == null) continue;

            output.WriteLine("");
            output.WriteLine($"=== {Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(file))))!)}");

            var scored = new List<(string Label, int Count, float Mean)>();
            foreach (var (label, surface) in bodies)
            {
                var depths = new List<float>();
                for (int v = 0; v < refit.Positions.Length / 3; v++)
                {
                    var p = At(refit, v);
                    if (p.Y < 1.19f || p.Y >= 1.35f || p.Z <= 0f) continue;
                    if (!surface.Nearest(p, 0.05f, out var hit)) continue;
                    float signed = Vector3.Dot(p - hit.Point, hit.Normal);
                    if (signed < 0f) depths.Add(-signed);
                }
                scored.Add((label, depths.Count, depths.Count == 0 ? 0f : depths.Average() * 1000f));
            }

            foreach (var (label, count, mean) in scored.OrderBy(t => t.Mean).ThenBy(t => t.Count).Take(3))
                output.WriteLine($"    best fit: {mean,6:F1} mm over {count,6:N0} verts   {label}");
        }
    }

    /// <summary>
    /// The same size name in every Neolithe install on this machine. The refit aimed at "DEFAULT ALMOND · NSFW Almond
    /// XS" of "Neolithe [ALL IN ONE]"; the refits earlier the same day aimed at the identically-named option of
    /// "Neolithe YAS AIO". If those two are different meshes, aiming at the wrong install is invisible in the panel —
    /// the option reads the same — and the garment is fitted to a bust the character does not have.
    /// </summary>
    [Fact]
    public void The_same_size_name_across_every_Neolithe_install()
    {
        string[] installs =
        [
            @"E:\Penumbradt\Neolithe [ALL IN ONE]",
            @"E:\Penumbradt\Neolithe YAS AIO",
            @"E:\Penumbradt\Neolithe 4k",
        ];

        var found = new List<(string Install, string Label, ModelParts Body)>();
        foreach (string root in installs)
        {
            if (!Directory.Exists(root)) { output.WriteLine($"{root}: not installed"); continue; }
            var catalog = BodySizeCatalog.Read(root);
            var match = catalog.For("_top", "0201")
                               .FirstOrDefault(o => o.FullLabel?.Contains("Almond XS", StringComparison.OrdinalIgnoreCase) == true
                                                 && o.FullLabel?.Contains("NSFW", StringComparison.OrdinalIgnoreCase) == true);
            if (match is null)
            {
                output.WriteLine($"{Path.GetFileName(root)}: no NSFW Almond XS " +
                                 $"({catalog.For("_top", "0201").Count} chest options)");
                continue;
            }
            var body = ModelPartReader.Read(File.ReadAllBytes(catalog.PathOf(match)));
            if (body == null) continue;
            output.WriteLine($"{Path.GetFileName(root),-26} {match.FullLabel}  " +
                             $"{body.Positions.Length / 3:N0} verts  {new FileInfo(catalog.PathOf(match)).Length:N0} bytes");
            found.Add((Path.GetFileName(root), match.FullLabel, body));
        }

        // How far apart the bust surfaces of two installs are, measured where the garment actually sits.
        for (int i = 0; i < found.Count; i++)
            for (int j = i + 1; j < found.Count; j++)
            {
                var surface = new BodySurface(found[j].Body, BodySurface.CellFor(MeanEdge(found[j].Body)));
                var gaps = new List<float>();
                var a = found[i].Body;
                for (int v = 0; v < a.Positions.Length / 3; v++)
                {
                    var p = At(a, v);
                    if (p.Y < 1.19f || p.Y >= 1.35f || p.Z <= 0f) continue;
                    if (!surface.Nearest(p, 0.08f, out var hit)) continue;
                    gaps.Add(Vector3.Distance(p, hit.Point));
                }
                output.WriteLine("");
                if (gaps.Count == 0) { output.WriteLine($"{found[i].Install} vs {found[j].Install}: no overlap"); continue; }
                gaps.Sort();
                output.WriteLine($"{found[i].Install} vs {found[j].Install} over the bust: " +
                                 $"mean {gaps.Average() * 1000f:F1} mm, p95 {gaps[(int)(gaps.Count * 0.95f)] * 1000f:F1} mm, " +
                                 $"max {gaps[^1] * 1000f:F1} mm");
            }

        // And what the refit does against each install's version of the size it was cut for.
        if (!File.Exists(Refit)) return;
        var refit = ModelPartReader.Read(File.ReadAllBytes(Refit))!;
        output.WriteLine("");
        foreach (var (install, label, body) in found)
        {
            var surface = new BodySurface(body, BodySurface.CellFor(MeanEdge(body)));
            var depths = new List<float>();
            for (int v = 0; v < refit.Positions.Length / 3; v++)
            {
                var p = At(refit, v);
                if (p.Y < 1.19f || p.Y >= 1.35f || p.Z <= 0f) continue;
                if (!surface.Nearest(p, 0.05f, out var hit)) continue;
                float signed = Vector3.Dot(p - hit.Point, hit.Normal);
                if (signed < 0f) depths.Add(-signed);
            }
            output.WriteLine(depths.Count == 0
                ? $"refit on {install,-26} nothing inside"
                : $"refit on {install,-26} {depths.Count,6:N0} inside, mean {depths.Average() * 1000f:F1} mm, " +
                  $"max {depths.Max() * 1000f:F1} mm");
        }
    }

    /// <summary>
    /// Every mesh in the refit and in the author's original, with the material it draws with and how far it sits from
    /// the body it was refitted onto. Two skin meshes over the same breast — the author's old one left in beside the
    /// new body's — would draw one through the other, which is a clip no measurement of the cloth can find.
    /// </summary>
    [Fact]
    public void Every_mesh_in_the_refit_and_where_it_sits()
    {
        if (!File.Exists(Refit) || !Directory.Exists(NeolitheRoot)) return;

        var neolithe = BodySizeCatalog.Read(NeolitheRoot);
        var target = neolithe.For("_top", "0201")
                             .FirstOrDefault(o => o.FullLabel?.Contains("NSFW Almond XS", StringComparison.OrdinalIgnoreCase) == true);
        if (target is null) return;
        var body = ModelPartReader.Read(File.ReadAllBytes(neolithe.PathOf(target)))!;
        var surface = new BodySurface(body, BodySurface.CellFor(MeanEdge(body)));

        Meshes("refit", File.ReadAllBytes(Refit), surface);
        string small = Path.Combine(Mod, "Chest size", "Small", Model);
        if (File.Exists(small)) Meshes("author \"Small\"", File.ReadAllBytes(small), surface);
    }

    private void Meshes(string label, byte[] mdl, BodySurface target)
    {
        if (ModelPartReader.Read(mdl) is not { } parts) return;

        output.WriteLine("");
        output.WriteLine($"=== {label}");
        output.WriteLine($"{"mesh",5}{"verts",8}{"bust",8}{"skin?",7}  {"to body (mm)",16}  material");

        foreach (var group in parts.Parts.GroupBy(p => p.Mesh).OrderBy(g => g.Key))
        {
            var verts = group.SelectMany(p => p.Triangles).Distinct().ToList();
            string material = group.First().Material;
            bool skin = SecondSkinWriter.IsBodySkinMaterial(material);

            var d = new List<float>();
            int bust = 0;
            foreach (int v in verts)
            {
                var p = At(parts, v);
                if (p.Y >= 1.19f && p.Y < 1.35f && p.Z > 0f) bust++;
                if (target.Nearest(p, 0.08f, out var hit)) d.Add(Vector3.Distance(p, hit.Point));
            }

            string dist = d.Count == 0 ? "off the body" : $"mean {d.Average() * 1000f,5:F1} max {d.Max() * 1000f,5:F1}";
            output.WriteLine($"{group.Key,5}{verts.Count,8:N0}{bust,8:N0}{(skin ? "SKIN" : ""),7}  {dist,16}  {material}");
        }
    }

    /// <summary>
    /// The standoff: how far the cloth floats off the skin beneath it. This is what a refit has to preserve — the
    /// author chose it, and it is the whole margin the breast has to move inside before it comes through the fabric.
    /// The author's cloth over THEIR body is the right answer; the refit's cloth over the NEW body has to match it.
    /// <para/>
    /// Cloth only, bust only, and the gap measured where the cloth actually has body under it.
    /// </summary>
    [Fact]
    public void Does_the_refit_keep_the_author_s_standoff()
    {
        if (!File.Exists(Refit) || !Directory.Exists(NeolitheRoot) || !Directory.Exists(BiboRoot)) return;

        var neolithe = BodySizeCatalog.Read(NeolitheRoot);
        var target = neolithe.For("_top", "0201")
                             .FirstOrDefault(o => o.FullLabel?.Contains("NSFW Almond XS", StringComparison.OrdinalIgnoreCase) == true);
        var bibo = BodySizeCatalog.Read(BiboRoot);
        var source = bibo.For("_top", "0201")
                         .FirstOrDefault(o => o.FullLabel?.Contains("Perky", StringComparison.OrdinalIgnoreCase) == true
                                           && o.FullLabel?.Contains("Small", StringComparison.OrdinalIgnoreCase) == true);
        if (target is null || source is null) return;

        var onNeolithe = Surface(neolithe.PathOf(target));
        var onBibo = Surface(bibo.PathOf(source));

        output.WriteLine($"{"",-46}{"verts",8}{"mean",9}{"p05",9}{"p50",9}   (mm off the skin)");
        Standoff("author \"Small\" over its own Bibo+ Perky Small", File.ReadAllBytes(SizePath("Small")), onBibo);
        Standoff("author \"Small\" over Neolithe Almond XS (unrefitted)", File.ReadAllBytes(SizePath("Small")), onNeolithe);
        Standoff("the refit over Neolithe Almond XS", File.ReadAllBytes(Refit), onNeolithe);

        foreach (string size in AuthorSizes)
            if (File.Exists(SizePath(size)))
                Standoff($"author \"{size}\" over its own Bibo+ Perky Small", File.ReadAllBytes(SizePath(size)), onBibo);
    }

    private static string SizePath(string size) => Path.Combine(Mod, "Chest size", size, Model);

    private static BodySurface Surface(string path)
    {
        var body = ModelPartReader.Read(File.ReadAllBytes(path))!;
        return new BodySurface(body, BodySurface.CellFor(MeanEdge(body)));
    }

    private void Standoff(string label, byte[] mdl, BodySurface skin)
    {
        if (ModelPartReader.Read(mdl) is not { } parts) return;

        var gaps = new List<float>();
        foreach (var group in parts.Parts.GroupBy(p => p.Mesh))
        {
            if (SecondSkinWriter.IsBodySkinMaterial(group.First().Material)) continue;   // cloth only
            foreach (int v in group.SelectMany(p => p.Triangles).Distinct())
            {
                var p = At(parts, v);
                if (p.Y < 1.19f || p.Y >= 1.35f || p.Z <= 0f) continue;
                if (!skin.Nearest(p, 0.03f, out var hit)) continue;   // only where there IS body under it
                gaps.Add(Vector3.Dot(p - hit.Point, hit.Normal));
            }
        }

        if (gaps.Count == 0) { output.WriteLine($"{label,-46}{"nothing over the body",26}"); return; }
        gaps.Sort();
        output.WriteLine($"{label,-46}{gaps.Count,8:N0}{gaps.Average() * 1000f,9:F2}" +
                         $"{gaps[(int)(gaps.Count * 0.05f)] * 1000f,9:F2}{gaps[gaps.Count / 2] * 1000f,9:F2}");
    }

    /// <summary>
    /// Where the cup lies ON the breast, the only thing keeping the skin from coming through is that the two move
    /// together. A cloth vertex weighted w_cloth to the bust bone over skin weighted w_skin diverges by
    /// (w_skin - w_cloth) x however far the bone carries the breast — so a gap of 0.3 over a 20 mm bounce is 6 mm of
    /// skin through the fabric, with a standoff of one or two millimetres to spend.
    /// <para/>
    /// Measured against the garment's OWN skin mesh, which is what is drawn beside the cloth, and only where the cloth
    /// is within 5 mm of it: the cups, not the loose skirt of the corset.
    /// </summary>
    [Fact]
    public void Do_the_cup_and_the_breast_under_it_move_together()
    {
        if (!File.Exists(Refit)) return;

        Cups("refit (Bibo+ cloth, Neolithe skin)", File.ReadAllBytes(Refit));
        foreach (string size in AuthorSizes)
            if (File.Exists(SizePath(size)))
                Cups($"author \"{size}\" as shipped", File.ReadAllBytes(SizePath(size)));
    }

    private void Cups(string label, byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } parts || ModelSkinReader.Read(mdl, null, null) is not { } skin
            || skin.VertexCount * 3 != parts.Positions.Length) { output.WriteLine($"{label}: skipped"); return; }

        var isSkin = new bool[skin.VertexCount];
        foreach (var part in parts.Parts)
            if (part.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(part.Material))
                foreach (int v in part.Triangles) isSkin[v] = true;

        var skinVerts = new List<int>();
        for (int v = 0; v < skin.VertexCount; v++)
            if (isSkin[v] && Bust(skin.Positions[v])) skinVerts.Add(v);
        if (skinVerts.Count == 0) { output.WriteLine($"{label}: no skin over the bust"); return; }

        var gaps = new List<float>();      // w_skin - w_cloth, where the cup touches
        var stand = new List<float>();     // how much room it has
        for (int v = 0; v < skin.VertexCount; v++)
        {
            if (isSkin[v] || !Bust(skin.Positions[v])) continue;
            var p = skin.Positions[v];

            int best = -1;
            float bestD = float.MaxValue;
            foreach (int sv in skinVerts)
            {
                float d = Vector3.DistanceSquared(p, skin.Positions[sv]);
                if (d < bestD) { bestD = d; best = sv; }
            }
            float dist = MathF.Sqrt(bestD);
            if (best < 0 || dist > 0.005f) continue;   // the cup, not the loose parts

            stand.Add(dist);
            gaps.Add(Mune(skin, best) - Mune(skin, v));
        }

        output.WriteLine("");
        output.WriteLine($"--- {label}: {gaps.Count:N0} cup verts within 5 mm of the breast");
        if (gaps.Count == 0) return;

        gaps.Sort();
        var abs = gaps.Select(MathF.Abs).OrderBy(g => g).ToList();
        output.WriteLine($"    standoff there: mean {stand.Average() * 1000f:F2} mm");
        output.WriteLine($"    skin minus cloth j_mune: mean {gaps.Average():F3}, p50 {gaps[gaps.Count / 2]:F3}, " +
                         $"p95 {gaps[(int)(gaps.Count * 0.95f)]:F3}, max {gaps[^1]:F3}");
        output.WriteLine($"    |gap| over 0.10: {abs.Count(g => g > 0.10f),6:N0} ({(float)abs.Count(g => g > 0.10f) / abs.Count:P1})   " +
                         $"over 0.25: {abs.Count(g => g > 0.25f),6:N0} ({(float)abs.Count(g => g > 0.25f) / abs.Count:P1})   " +
                         $"over 0.50: {abs.Count(g => g > 0.50f),6:N0} ({(float)abs.Count(g => g > 0.50f) / abs.Count:P1})");
        output.WriteLine($"    skin pulls AWAY from cloth (gap > 0) on {gaps.Count(g => g > 0f) / (float)gaps.Count:P1} of the cup");
    }

    /// <summary>
    /// Folded and stretched triangles in the cloth, which is what a shard of skin through a bra cup looks like.
    /// <para/>
    /// The refit moves positions and never touches normals, so the author's vertex normals are still in the file. A
    /// triangle whose GEOMETRIC normal now disagrees with the normals its own corners carry has been turned inside out
    /// by the solve — it draws backwards, and whatever is behind it shows through. The cloth mesh keeps its numbering
    /// through the refit, so every triangle can be compared with the author's own.
    /// </summary>
    [Fact]
    public void Folded_triangles_in_the_cloth()
    {
        if (!File.Exists(Refit) || !File.Exists(SizePath("Small"))) return;

        Folds("author \"Small\" as shipped", File.ReadAllBytes(SizePath("Small")));
        Folds("refit", File.ReadAllBytes(Refit));
    }

    private void Folds(string label, byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } parts) return;

        int flipped = 0, bustFlipped = 0, sliver = 0, bustSliver = 0, total = 0, bust = 0;
        var worstStretch = 0f;
        var flippedY = new List<float>();

        foreach (var group in parts.Parts.GroupBy(p => p.Mesh))
        {
            if (SecondSkinWriter.IsBodySkinMaterial(group.First().Material)) continue;   // cloth only
            foreach (var part in group)
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                    var pa = At(parts, a); var pb = At(parts, b); var pc = At(parts, c);
                    var cross = Vector3.Cross(pb - pa, pc - pa);
                    float area = cross.Length() * 0.5f;
                    var mid = (pa + pb + pc) / 3f;
                    bool inBust = Bust(mid);

                    total++;
                    if (inBust) bust++;

                    // The normals the author left on the corners, which the refit never rewrites.
                    var n = Normal(parts, a) + Normal(parts, b) + Normal(parts, c);
                    if (area > 1e-12f && n.LengthSquared() > 1e-12f
                        && Vector3.Dot(Vector3.Normalize(cross), Vector3.Normalize(n)) < 0f)
                    {
                        flipped++;
                        if (inBust) { bustFlipped++; flippedY.Add(mid.Y); }
                    }

                    // A sliver: long and thin enough that it reads as a crack or a spike.
                    float longest = MathF.Max((pb - pa).Length(), MathF.Max((pc - pb).Length(), (pa - pc).Length()));
                    if (longest > 0f && area / (longest * longest) < 0.02f)
                    {
                        sliver++;
                        if (inBust) bustSliver++;
                    }
                    if (longest > worstStretch) worstStretch = longest;
                }
        }

        output.WriteLine("");
        output.WriteLine($"--- {label}: {total:N0} cloth triangles, {bust:N0} over the bust");
        output.WriteLine($"    turned inside out: {flipped:N0} overall, {bustFlipped:N0} over the bust");
        output.WriteLine($"    slivers:           {sliver:N0} overall, {bustSliver:N0} over the bust");
        output.WriteLine($"    longest edge {worstStretch * 1000f:F1} mm");
        if (flippedY.Count > 0)
            output.WriteLine($"    flipped over the bust span y {flippedY.Min():F3}..{flippedY.Max():F3}");
    }

    private static Vector3 Normal(ModelParts m, int v)
        => m.Normals.Length >= (v + 1) * 3
            ? new Vector3(m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2])
            : Vector3.Zero;

    /// <summary>
    /// Triangles the refit turned over, measured against the author's own geometry and nothing else.
    /// <para/>
    /// The cloth's vertices survive a refit in order — established by
    /// <see cref="Does_the_refit_cloth_still_line_up_with_the_author_s"/>, which finds all 6,172 texture coordinates
    /// identical — so the two files' cloth vertices correspond by a fixed offset. ONE triangle list is used, the
    /// refit's, and each corner is looked up in both models through that offset: nothing here depends on the two files
    /// listing their triangles in the same order, which the rebuild does not promise.
    /// <para/>
    /// If a triangle's face normal before and after point away from each other, the solve turned it inside out. It then
    /// draws backwards, and what is behind it — the breast — shows through.
    /// </summary>
    [Fact]
    public void Triangles_the_refit_turned_over()
    {
        if (!File.Exists(Refit) || !File.Exists(SizePath("Small"))) return;

        var author = ModelPartReader.Read(File.ReadAllBytes(SizePath("Small")))!;
        var refit = ModelPartReader.Read(File.ReadAllBytes(Refit))!;

        var a = author.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(author, sp));
        var r = refit.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(refit, sp));
        if (a.Count == 0 || a.Count != r.Count) { output.WriteLine("cloth spans do not correspond"); return; }
        int shift = a.BaseVertex - r.BaseVertex;
        output.WriteLine($"cloth vertex {r.BaseVertex} in the refit is vertex {a.BaseVertex} in the author's file");

        int flipped = 0, bustFlipped = 0, degenerate = 0, total = 0;
        var ys = new List<float>();
        var shrunk = new List<float>();

        foreach (var group in refit.Parts.GroupBy(p => p.Mesh))
        {
            if (SecondSkinWriter.IsBodySkinMaterial(group.First().Material)) continue;
            foreach (var part in group)
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int i0 = part.Triangles[t], i1 = part.Triangles[t + 1], i2 = part.Triangles[t + 2];
                    if (i0 + shift < 0 || i0 + shift >= author.Positions.Length / 3
                        || i1 + shift < 0 || i1 + shift >= author.Positions.Length / 3
                        || i2 + shift < 0 || i2 + shift >= author.Positions.Length / 3) continue;

                    var a0 = At(author, i0 + shift); var b0 = At(author, i1 + shift); var c0 = At(author, i2 + shift);
                    var a1 = At(refit, i0); var b1 = At(refit, i1); var c1 = At(refit, i2);
                    var n0 = Vector3.Cross(b0 - a0, c0 - a0);
                    var n1 = Vector3.Cross(b1 - a1, c1 - a1);
                    total++;
                    if (n0.Length() <= 1e-12f) continue;
                    if (n1.Length() <= 1e-12f) { degenerate++; continue; }

                    float ratio = n1.Length() / n0.Length();
                    if (ratio < 0.25f) shrunk.Add(ratio);
                    if (Vector3.Dot(n0, n1) >= 0f) continue;

                    flipped++;
                    var mid = (a1 + b1 + c1) / 3f;
                    if (Bust(mid)) { bustFlipped++; ys.Add(mid.Y); }
                }
        }

        output.WriteLine($"{total:N0} cloth triangles compared");
        output.WriteLine($"turned inside out by the refit: {flipped:N0}, of which {bustFlipped:N0} over the bust");
        output.WriteLine($"collapsed to nothing: {degenerate:N0}");
        output.WriteLine($"shrunk to under a quarter of their area: {shrunk.Count:N0}");
        if (ys.Count > 0) output.WriteLine($"the flipped ones over the bust span y {ys.Min():F3}..{ys.Max():F3}");
    }

    /// <summary>
    /// Whether the refit's cloth still lines up with the author's, vertex for vertex. Everything that compares the two
    /// files triangle by triangle rests on this, and the rebuild reorders the meshes — so it has to be established,
    /// not assumed. The texture coordinates settle it: the refit never touches them.
    /// </summary>
    [Fact]
    public void Does_the_refit_cloth_still_line_up_with_the_author_s()
    {
        if (!File.Exists(Refit) || !File.Exists(SizePath("Small"))) return;

        byte[] authorBytes = File.ReadAllBytes(SizePath("Small")), refitBytes = File.ReadAllBytes(Refit);
        var author = ModelPartReader.Read(authorBytes)!;
        var refit = ModelPartReader.Read(refitBytes)!;
        float[] authorUv = Uv(authorBytes), refitUv = Uv(refitBytes);

        foreach (var (label, m, uv) in new[] { ("author", author, authorUv), ("refit", refit, refitUv) })
        {
            output.WriteLine($"{label}: {m.Positions.Length / 3:N0} verts, uv {uv.Length / 2:N0} entries, " +
                             $"{m.MeshSpans.Count} mesh spans");
            foreach (var span in m.MeshSpans)
            {
                string material = m.Parts.FirstOrDefault(p => p.Mesh == span.Mesh)?.Material ?? "?";
                output.WriteLine($"    mesh {span.Mesh}  base {span.BaseVertex,6:N0}  count {span.Count,6:N0}  " +
                                 $"{(SecondSkinWriter.IsBodySkinMaterial(material) ? "SKIN" : "cloth")}  {material}");
            }
        }

        var a = author.MeshSpans.FirstOrDefault(s => !IsSkinSpan(author, s));
        var r = refit.MeshSpans.FirstOrDefault(s => !IsSkinSpan(refit, s));
        if (a.Count == 0 || r.Count == 0 || a.Count != r.Count)
        {
            output.WriteLine($"cloth spans differ: author {a.Count}, refit {r.Count}");
            return;
        }

        int same = 0, differ = 0;
        float worst = 0f;
        for (int i = 0; i < a.Count; i++)
        {
            int ai = (a.BaseVertex + i) * 2, ri = (r.BaseVertex + i) * 2;
            if (ai + 1 >= authorUv.Length || ri + 1 >= refitUv.Length) break;
            float d = MathF.Abs(authorUv[ai] - refitUv[ri]) + MathF.Abs(authorUv[ai + 1] - refitUv[ri + 1]);
            if (d < 1e-6f) same++; else { differ++; worst = MathF.Max(worst, d); }
        }
        output.WriteLine("");
        output.WriteLine($"cloth vertices in the same order: {same:N0} match, {differ:N0} differ (worst {worst:F5})");
    }

    private static bool IsSkinSpan(ModelParts m, MeshSpan span)
        => SecondSkinWriter.IsBodySkinMaterial(m.Parts.FirstOrDefault(p => p.Mesh == span.Mesh)?.Material ?? "");

    /// <summary>
    /// The reported refit, run again here: Bibo+ Perky Small to Neolithe DEFAULT ALMOND NSFW Almond XS, the same pair
    /// the mod's record names. Prints what the solve says about itself beside what its output actually is, so the two
    /// can be compared — the folds are all over the bust, and the question is whether the solver knows.
    /// </summary>
    [Fact]
    public void Reproduce_the_reported_refit()
    {
        if (!File.Exists(SizePath("Small")) || !Directory.Exists(NeolitheRoot) || !Directory.Exists(BiboRoot)) return;

        var bibo = BodySizeCatalog.Read(BiboRoot);
        var source = bibo.For("_top", "0201")
                         .FirstOrDefault(o => o.FullLabel?.Contains("Perky - Small", StringComparison.OrdinalIgnoreCase) == true);
        var neolithe = BodySizeCatalog.Read(NeolitheRoot);
        var target = neolithe.For("_top", "0201")
                             .FirstOrDefault(o => o.FullLabel?.Contains("DEFAULT ALMOND", StringComparison.OrdinalIgnoreCase) == true
                                               && o.FullLabel?.Contains("NSFW Almond XS", StringComparison.OrdinalIgnoreCase) == true);
        if (source is null || target is null) { output.WriteLine("body sizes not found"); return; }
        output.WriteLine($"from {source.FullLabel}");
        output.WriteLine($"to   {target.FullLabel}");

        byte[] garmentBytes = File.ReadAllBytes(SizePath("Small"));
        byte[] sourceBytes = File.ReadAllBytes(bibo.PathOf(source));
        byte[] targetBytes = File.ReadAllBytes(neolithe.PathOf(target));
        var garment = ModelPartReader.Read(garmentBytes)!;
        var sourceParts = ModelPartReader.Read(sourceBytes)!;
        var targetParts = ModelPartReader.Read(targetBytes)!;

        if (!BodyCorrespondence.TryBuild(sourceParts, Uv(sourceBytes), targetParts, Uv(targetBytes), "_top",
                                         out var built, out string refusal))
        {
            output.WriteLine($"correspondence refused: {refusal}");
            return;
        }
        output.WriteLine($"correspondence: {built!.Describe()}");

        var pairs = new List<BodyRetarget.SlotPair>
        {
            new("_top", built, targetParts, targetBytes, sourceBytes),
        };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top",
                                        replaceSkin: true, acrossBodies: true);
        clock.Stop();

        var r = planned.Report;
        output.WriteLine("");
        output.WriteLine($"solve {clock.ElapsedMilliseconds} ms");
        output.WriteLine($"  the solver reports: folded {r.Folded}, snapped {r.Snapped:N0} ({r.SnapRate:P0}), " +
                         $"pushed {r.Pushed:N0} (worst {r.WorstPush * 1000f:F2} mm), " +
                         $"worst move {r.WorstMove * 1000f:F2} mm, missed {r.Missed:N0}");
        if (r.Swap is { } sw)
            output.WriteLine($"  skin swap: removed {sw.Removed}, added {sw.Added}, kept {sw.Kept}, cut {sw.Cut}, " +
                             $"reweighted {sw.Reweighted:N0}, trimmed {sw.Trimmed:N0}, unplaced {sw.Unplaced:N0}");

        // And what the output really is, at the vertices the game draws.
        var refit = ModelPartReader.Read(planned.Model)!;
        var a = garment.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(garment, sp));
        var rr = refit.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(refit, sp));
        if (a.Count == 0 || a.Count != rr.Count) { output.WriteLine("  cloth spans do not correspond"); return; }
        int shift = a.BaseVertex - rr.BaseVertex;

        int flipped = 0, bustFlipped = 0;
        var ys = new List<float>();
        foreach (var group in refit.Parts.GroupBy(p => p.Mesh))
        {
            if (SecondSkinWriter.IsBodySkinMaterial(group.First().Material)) continue;
            foreach (var part in group)
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int i0 = part.Triangles[t], i1 = part.Triangles[t + 1], i2 = part.Triangles[t + 2];
                    int j0 = i0 + shift, j1 = i1 + shift, j2 = i2 + shift;
                    if (j0 < 0 || j2 < 0 || j0 >= garment.Positions.Length / 3
                        || j1 >= garment.Positions.Length / 3 || j2 >= garment.Positions.Length / 3) continue;
                    var n0 = Vector3.Cross(At(garment, j1) - At(garment, j0), At(garment, j2) - At(garment, j0));
                    var n1 = Vector3.Cross(At(refit, i1) - At(refit, i0), At(refit, i2) - At(refit, i0));
                    if (n0.Length() <= 1e-12f || n1.Length() <= 1e-12f) continue;
                    if (Vector3.Dot(n0, n1) >= 0f) continue;
                    flipped++;
                    var mid = (At(refit, i0) + At(refit, i1) + At(refit, i2)) / 3f;
                    if (Bust(mid)) { bustFlipped++; ys.Add(mid.Y); }
                }
        }
        output.WriteLine($"  the output really has: {flipped:N0} cloth triangles inside out, {bustFlipped:N0} over the bust");
        if (ys.Count > 0) output.WriteLine($"  spanning y {ys.Min():F3}..{ys.Max():F3}");
    }

    /// <summary>
    /// Which pass folds the bust. The same refit with the push-out and the skin replacement turned on and off, so the
    /// folds can be pinned on the pass that makes them rather than guessed at.
    /// </summary>
    [Fact]
    public void Which_pass_folds_the_bust()
    {
        if (!File.Exists(SizePath("Small")) || !Directory.Exists(NeolitheRoot) || !Directory.Exists(BiboRoot)) return;
        if (Setup() is not { } setup) { output.WriteLine("bodies not found"); return; }
        var (garment, garmentBytes, pairs) = setup;

        output.WriteLine($"{"push-out",10}{"new skin",10}{"reported",10}{"real",8}{"bust",8}{"worst move",12}");
        foreach (bool push in new[] { true, false })
            foreach (bool skin in new[] { true, false })
            {
                var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top",
                                                pushOut: push, replaceSkin: skin, acrossBodies: true);
                var (flipped, bust) = Inverted(garment, planned.Model);
                output.WriteLine($"{push,10}{skin,10}{planned.Report.Folded,10}{flipped,8:N0}{bust,8:N0}" +
                                 $"{planned.Report.WorstMove * 1000f,11:F2}mm");
            }
    }

    /// <summary>The reported pair, ready to solve.</summary>
    private (ModelParts Garment, byte[] Bytes, List<BodyRetarget.SlotPair> Pairs)? Setup()
    {
        var bibo = BodySizeCatalog.Read(BiboRoot);
        var source = bibo.For("_top", "0201")
                         .FirstOrDefault(o => o.FullLabel?.Contains("Perky - Small", StringComparison.OrdinalIgnoreCase) == true);
        var neolithe = BodySizeCatalog.Read(NeolitheRoot);
        var target = neolithe.For("_top", "0201")
                             .FirstOrDefault(o => o.FullLabel?.Contains("DEFAULT ALMOND", StringComparison.OrdinalIgnoreCase) == true
                                               && o.FullLabel?.Contains("NSFW Almond XS", StringComparison.OrdinalIgnoreCase) == true);
        if (source is null || target is null) return null;

        byte[] garmentBytes = File.ReadAllBytes(SizePath("Small"));
        byte[] sourceBytes = File.ReadAllBytes(bibo.PathOf(source));
        byte[] targetBytes = File.ReadAllBytes(neolithe.PathOf(target));
        var sourceParts = ModelPartReader.Read(sourceBytes)!;
        var targetParts = ModelPartReader.Read(targetBytes)!;
        if (!BodyCorrespondence.TryBuild(sourceParts, Uv(sourceBytes), targetParts, Uv(targetBytes), "_top",
                                         out var built, out _)) return null;

        return (ModelPartReader.Read(garmentBytes)!, garmentBytes,
                [new BodyRetarget.SlotPair("_top", built!, targetParts, targetBytes, sourceBytes)]);
    }

    /// <summary>Cloth triangles the refit turned over, and how many of those are over the bust.</summary>
    private (int All, int Bust) Inverted(ModelParts author, byte[] refitBytes)
    {
        var refit = ModelPartReader.Read(refitBytes)!;
        var a = author.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(author, sp));
        var r = refit.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(refit, sp));
        if (a.Count == 0 || a.Count != r.Count) return (-1, -1);
        int shift = a.BaseVertex - r.BaseVertex;

        int flipped = 0, bust = 0;
        foreach (var group in refit.Parts.GroupBy(p => p.Mesh))
        {
            if (SecondSkinWriter.IsBodySkinMaterial(group.First().Material)) continue;
            foreach (var part in group)
            {
                if (part.Island >= 0) continue;   // a subset of its own submesh: counting both doubles every triangle
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int i0 = part.Triangles[t], i1 = part.Triangles[t + 1], i2 = part.Triangles[t + 2];
                    int j0 = i0 + shift, j1 = i1 + shift, j2 = i2 + shift;
                    int n = author.Positions.Length / 3;
                    if (j0 < 0 || j1 < 0 || j2 < 0 || j0 >= n || j1 >= n || j2 >= n) continue;
                    var n0 = Vector3.Cross(At(author, j1) - At(author, j0), At(author, j2) - At(author, j0));
                    var n1 = Vector3.Cross(At(refit, i1) - At(refit, i0), At(refit, i2) - At(refit, i0));
                    if (n0.Length() <= 1e-12f || n1.Length() <= 1e-12f) continue;
                    if (Vector3.Dot(n0, n1) >= 0f) continue;
                    flipped++;
                    if (Bust((At(refit, i0) + At(refit, i1) + At(refit, i2)) / 3f)) bust++;
                }
            }
        }
        return (flipped, bust);
    }

    /// <summary>
    /// The garment's parts, and whether the folded triangles are in ones the solve ever looks at. Sets.From skips
    /// every island part when it builds the triangle list the fold tests run over, so a triangle in an island is never
    /// tested for folding and never counted as folded — it is simply moved.
    /// </summary>
    [Fact]
    public void Are_the_folds_in_parts_the_solve_never_tests()
    {
        if (!File.Exists(SizePath("Small"))) return;
        var author = ModelPartReader.Read(File.ReadAllBytes(SizePath("Small")))!;

        output.WriteLine("the author's parts:");
        int islandTris = 0, wholeTris = 0;
        foreach (var part in author.Parts)
        {
            bool skin = SecondSkinWriter.IsBodySkinMaterial(part.Material);
            if (part.Island >= 0) islandTris += part.Triangles.Length / 3; else wholeTris += part.Triangles.Length / 3;
            output.WriteLine($"   mesh {part.Mesh}  island {part.Island,3}  {part.Triangles.Length / 3,7:N0} tris  " +
                             $"{(skin ? "SKIN " : "cloth")}  {part.Material}");
        }
        output.WriteLine($"   triangles the solve tests: {wholeTris:N0}; in islands, never tested: {islandTris:N0}");

        // Which parts the folded triangles belong to.
        if (Setup() is not { } setup) return;
        var (garment, garmentBytes, pairs) = setup;
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top", replaceSkin: true, acrossBodies: true);
        var refit = ModelPartReader.Read(planned.Model)!;

        var a = garment.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(garment, sp));
        var r = refit.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(refit, sp));
        if (a.Count == 0 || a.Count != r.Count) return;
        int shift = a.BaseVertex - r.BaseVertex;

        var byIsland = new Dictionary<int, int>();
        foreach (var part in refit.Parts)
        {
            if (SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int i0 = part.Triangles[t], i1 = part.Triangles[t + 1], i2 = part.Triangles[t + 2];
                int j0 = i0 + shift, j1 = i1 + shift, j2 = i2 + shift;
                int n = garment.Positions.Length / 3;
                if (j0 < 0 || j1 < 0 || j2 < 0 || j0 >= n || j1 >= n || j2 >= n) continue;
                var n0 = Vector3.Cross(At(garment, j1) - At(garment, j0), At(garment, j2) - At(garment, j0));
                var n1 = Vector3.Cross(At(refit, i1) - At(refit, i0), At(refit, i2) - At(refit, i0));
                if (n0.Length() <= 1e-12f || n1.Length() <= 1e-12f) continue;
                if (Vector3.Dot(n0, n1) >= 0f) continue;
                byIsland[part.Island] = byIsland.GetValueOrDefault(part.Island) + 1;
            }
        }
        output.WriteLine("");
        output.WriteLine("folded triangles by the part they are in:");
        foreach (var (island, count) in byIsland.OrderByDescending(p => p.Value))
            output.WriteLine($"   island {island,3}: {count,5:N0} folded" + (island >= 0 ? "   (never fold-tested)" : ""));
    }

    /// <summary>
    /// Whether the garment's own skin sticks out THROUGH its own cloth — which is what skin-coloured shapes drawn on
    /// top of the fabric are. Measured the only way that matches what is drawn: the skin mesh against the cloth
    /// surface, signed by the cloth's own normal, not either of them against the body.
    /// </summary>
    [Fact]
    public void Does_the_skin_stick_out_through_the_cloth()
    {
        if (!File.Exists(Refit)) return;

        Poke("refit (Neolithe skin in Bibo+ cloth)", File.ReadAllBytes(Refit));
        foreach (string size in AuthorSizes)
            if (File.Exists(SizePath(size)))
                Poke($"author \"{size}\" as shipped", File.ReadAllBytes(SizePath(size)));
    }

    private void Poke(string label, byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } parts) { output.WriteLine($"{label}: unreadable"); return; }

        // The two surfaces, split by material, whole submeshes only.
        var clothTris = new List<int>();
        var skinTris = new List<int>();
        foreach (var part in parts.Parts)
        {
            if (part.Island >= 0) continue;
            (SecondSkinWriter.IsBodySkinMaterial(part.Material) ? skinTris : clothTris).AddRange(part.Triangles);
        }
        if (clothTris.Count == 0 || skinTris.Count == 0) { output.WriteLine($"{label}: needs both meshes"); return; }

        var cloth = new BodySurface(OnlyCloth(parts), BodySurface.CellFor(MeanEdge(parts)));

        var outside = new List<(float Depth, float Y)>();
        int considered = 0;
        foreach (int v in skinTris.Distinct())
        {
            var p = At(parts, v);
            if (p.Y < 1.15f || p.Y >= 1.40f || p.Z <= 0f) continue;   // the front of the chest
            considered++;
            if (!cloth.Nearest(p, 0.03f, out var hit)) continue;       // no fabric over it: bare skin, fine
            float signed = Vector3.Dot(p - hit.Point, hit.Normal);
            if (signed > 0f) outside.Add((signed, p.Y));
        }

        output.WriteLine("");
        output.WriteLine($"--- {label}: {considered:N0} skin verts on the front of the chest");
        if (outside.Count == 0) { output.WriteLine("    none of it outside the cloth"); return; }

        var d = outside.Select(o => o.Depth).OrderBy(x => x).ToList();
        output.WriteLine($"    OUTSIDE the cloth: {outside.Count:N0}  mean {d.Average() * 1000f:F2} mm  " +
                         $"p95 {d[(int)(d.Count * 0.95f)] * 1000f:F2} mm  max {d[^1] * 1000f:F2} mm");
        foreach (var band in new[] { (1.30f, 1.40f, "upper chest"), (1.25f, 1.30f, "top of the cup"),
                                     (1.19f, 1.25f, "cup and nipple"), (1.15f, 1.19f, "under the bust") })
        {
            var here = outside.Where(o => o.Y >= band.Item1 && o.Y < band.Item2).Select(o => o.Depth).ToList();
            if (here.Count == 0) continue;
            output.WriteLine($"      {band.Item3,-16} {here.Count,6:N0} out, mean {here.Average() * 1000f:F2} mm, " +
                             $"max {here.Max() * 1000f:F2} mm");
        }
    }

    /// <summary>The garment's cloth on its own, as a surface: its own parts, minus the skin and the islands.</summary>
    private static ModelParts OnlyCloth(ModelParts m)
        => new()
        {
            Positions = m.Positions,
            Normals = m.Normals,
            MeshSpans = m.MeshSpans,
            Parts = [.. m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))],
            AttributeNames = m.AttributeNames,
            Min = m.Min,
            Max = m.Max,
            ShatteredSubmeshes = m.ShatteredSubmeshes,
        };

    /// <summary>
    /// What skin material each body declares, and whether the swapped-in skin is being drawn with one that belongs to
    /// its own uv layout. The swap takes the body mod's mesh verbatim but has to choose a material for it; pick the
    /// garment's and the new geometry is textured through the OLD body's uv assumptions, which scrambles the skin
    /// wherever the two layouts disagree.
    /// </summary>
    [Fact]
    public void What_material_the_swapped_in_skin_is_drawn_with()
    {
        if (!Directory.Exists(NeolitheRoot) || !Directory.Exists(BiboRoot)) return;

        var bibo = BodySizeCatalog.Read(BiboRoot);
        var source = bibo.For("_top", "0201")
                         .FirstOrDefault(o => o.FullLabel?.Contains("Perky - Small", StringComparison.OrdinalIgnoreCase) == true);
        var neolithe = BodySizeCatalog.Read(NeolitheRoot);
        var target = neolithe.For("_top", "0201")
                             .FirstOrDefault(o => o.FullLabel?.Contains("NSFW Almond XS", StringComparison.OrdinalIgnoreCase) == true);
        if (source is null || target is null) return;

        Declare("Bibo+ Perky Small (the garment's body)", File.ReadAllBytes(bibo.PathOf(source)));
        Declare("Neolithe Almond XS (the new body)", File.ReadAllBytes(neolithe.PathOf(target)));
        if (File.Exists(SizePath("Small"))) Declare("the author's garment", File.ReadAllBytes(SizePath("Small")));
        if (File.Exists(Refit)) Declare("the refit", File.ReadAllBytes(Refit));

        // Do the two bodies agree on where a point of skin sits in the texture? Take each Neolithe skin vertex, find
        // the nearest Bibo one in SPACE, and compare their uvs. Same layout means the same place in the sheet.
        var b = ModelPartReader.Read(File.ReadAllBytes(bibo.PathOf(source)))!;
        var n = ModelPartReader.Read(File.ReadAllBytes(neolithe.PathOf(target)))!;
        float[] bUv = Uv(File.ReadAllBytes(bibo.PathOf(source))), nUv = Uv(File.ReadAllBytes(neolithe.PathOf(target)));
        if (bUv.Length < b.Positions.Length / 3 * 2 || nUv.Length < n.Positions.Length / 3 * 2)
        {
            output.WriteLine("uvs unreadable");
            return;
        }

        var surface = new BodySurface(b, BodySurface.CellFor(MeanEdge(b)));
        var gaps = new List<float>();
        for (int v = 0; v < n.Positions.Length / 3; v += 7)      // every seventh: this is a shape, not a census
        {
            var p = At(n, v);
            if (p.Y < 1.15f || p.Y >= 1.40f || p.Z <= 0f) continue;
            if (!surface.Nearest(p, 0.01f, out var hit)) continue;

            // The nearest Bibo VERTEX to that landing, so there is a uv to read.
            int best = -1;
            float bestD = float.MaxValue;
            for (int w = 0; w < b.Positions.Length / 3; w++)
            {
                float d = Vector3.DistanceSquared(hit.Point, At(b, w));
                if (d < bestD) { bestD = d; best = w; }
            }
            if (best < 0) continue;
            float du = nUv[v * 2] - bUv[best * 2], dv = nUv[v * 2 + 1] - bUv[best * 2 + 1];
            gaps.Add(MathF.Sqrt(du * du + dv * dv));
        }

        output.WriteLine("");
        if (gaps.Count == 0) { output.WriteLine("no overlap to compare uvs on"); return; }
        gaps.Sort();
        output.WriteLine($"uv distance between the two bodies at the same point of skin, over the chest " +
                         $"({gaps.Count:N0} samples):");
        output.WriteLine($"   mean {gaps.Average():F4}  p50 {gaps[gaps.Count / 2]:F4}  p95 {gaps[(int)(gaps.Count * 0.95f)]:F4}  max {gaps[^1]:F4}");
        output.WriteLine($"   further than a hundredth of the sheet apart: {gaps.Count(g => g > 0.01f) / (float)gaps.Count:P1}");
    }

    private void Declare(string label, byte[] mdl)
    {
        var parsed = SecondSkinWriter.Parse(mdl);
        var names = parsed.MatNames.ToList();
        output.WriteLine($"{label,-40} {string.Join("  ", names)}");
        foreach (string name in names)
            if (SecondSkinWriter.SkinMaterialBodyType(name) is { } layout)
                output.WriteLine($"{"",-40}    {name} is the {layout} layout");
    }

    /// <summary>
    /// Whether the cloth and the skin under it are weighted to the SAME BONES, across the whole skeleton and not just
    /// the bust pair.
    /// <para/>
    /// This is the reported symptom stated exactly: the top's weights not matching the skin's. Two surfaces a
    /// millimetre apart come apart under any animation by the amount their influences differ, whatever bone carries
    /// the difference — a spine bone in the corset's body panel matters as much as j_mune. Reported as total
    /// variation: 0 means identical skinning, 1 means they share no bone at all.
    /// </summary>
    [Fact]
    public void Do_the_cloth_and_the_skin_share_their_skinning()
    {
        if (!File.Exists(Refit)) return;

        Skinning("refit (Bibo+ cloth, Neolithe skin)", File.ReadAllBytes(Refit));
        foreach (string size in AuthorSizes)
            if (File.Exists(SizePath(size)))
                Skinning($"author \"{size}\" as shipped", File.ReadAllBytes(SizePath(size)));
    }

    private void Skinning(string label, byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } parts || ModelSkinReader.Read(mdl, null, null) is not { } skin
            || skin.VertexCount * 3 != parts.Positions.Length) { output.WriteLine($"{label}: skipped"); return; }

        var isSkin = new bool[skin.VertexCount];
        foreach (var part in parts.Parts)
            if (part.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(part.Material))
                foreach (int v in part.Triangles) isSkin[v] = true;

        var skinVerts = new List<int>();
        for (int v = 0; v < skin.VertexCount; v++)
            if (isSkin[v] && Bust(skin.Positions[v])) skinVerts.Add(v);
        if (skinVerts.Count == 0) { output.WriteLine($"{label}: no skin over the bust"); return; }

        var apart = new List<float>();
        var worstBone = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int v = 0; v < skin.VertexCount; v++)
        {
            if (isSkin[v] || !Bust(skin.Positions[v])) continue;
            var p = skin.Positions[v];

            int best = -1;
            float bestD = float.MaxValue;
            foreach (int sv in skinVerts)
            {
                float d = Vector3.DistanceSquared(p, skin.Positions[sv]);
                if (d < bestD) { bestD = d; best = sv; }
            }
            if (best < 0 || MathF.Sqrt(bestD) > 0.005f) continue;   // only where the cloth lies on the skin

            var mine = Weights(skin, v);
            var theirs = Weights(skin, best);
            float tv = 0f;
            foreach (string bone in mine.Keys.Union(theirs.Keys))
            {
                float diff = MathF.Abs(mine.GetValueOrDefault(bone) - theirs.GetValueOrDefault(bone));
                tv += diff;
                if (diff > worstBone.GetValueOrDefault(bone)) worstBone[bone] = diff;
            }
            apart.Add(tv / 2f);
        }

        output.WriteLine("");
        output.WriteLine($"--- {label}: {apart.Count:N0} cloth verts lying on the skin over the bust");
        if (apart.Count == 0) return;
        apart.Sort();
        output.WriteLine($"    skinning apart: mean {apart.Average():F3}  p50 {apart[apart.Count / 2]:F3}  " +
                         $"p95 {apart[(int)(apart.Count * 0.95f)]:F3}  max {apart[^1]:F3}");
        output.WriteLine($"    more than a tenth apart: {apart.Count(a => a > 0.10f):N0} " +
                         $"({apart.Count(a => a > 0.10f) / (float)apart.Count:P1});  " +
                         $"more than a quarter: {apart.Count(a => a > 0.25f):N0} " +
                         $"({apart.Count(a => a > 0.25f) / (float)apart.Count:P1})");
        output.WriteLine("    the bones that differ most:");
        foreach (var (bone, diff) in worstBone.OrderByDescending(b => b.Value).Take(6))
            output.WriteLine($"      {bone,-18} up to {diff:F3}");
    }

    private static Dictionary<string, float> Weights(SkinnedMesh skin, int v)
    {
        var w = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int i = 0; i < SkinnedMesh.MaxInfluences; i++)
        {
            float weight = skin.BoneWeights[v * SkinnedMesh.MaxInfluences + i];
            if (weight <= 0f) continue;
            string bone = skin.BoneNames[skin.BoneIndices[v * SkinnedMesh.MaxInfluences + i]];
            w[bone] = w.GetValueOrDefault(bone) + weight;
        }
        return w;
    }

    /// <summary>
    /// How deep the cup sits INSIDE the breast it covers, on every refit saved into the mod, against the garment's own
    /// skin mesh — which is the breast the game draws.
    /// <para/>
    /// Signed by the SKIN's normal, not the cloth's. The cloth is a sheer double-sided sheet whose normals face both
    /// ways, so a sign taken from it means nothing; the skin is a body surface with a consistent outward normal. An
    /// earlier reading of mine took the cloth's and concluded nothing was buried, which was an artefact of that choice.
    /// </summary>
    [Fact]
    public void How_deep_the_cup_sits_inside_the_breast()
    {
        string folder = Mod + @"\Body Retarget";
        if (Directory.Exists(folder))
            foreach (string file in Directory.GetFiles(folder, "*e6010_top.mdl", SearchOption.AllDirectories)
                                             .OrderBy(f => new FileInfo(f).LastWriteTime))
                Buried($"refit: {Path.GetFileName(Path.GetDirectoryName(file)!.Split(@"\Body Retarget\")[^1].Split('\\')[0])}"
                       + $"  [{new FileInfo(file).LastWriteTime:HH:mm}]", File.ReadAllBytes(file));

        foreach (string size in AuthorSizes)
            if (File.Exists(SizePath(size)))
                Buried($"author \"{size}\" as shipped", File.ReadAllBytes(SizePath(size)));
    }

    private void Buried(string label, byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } parts) { output.WriteLine($"{label}: unreadable"); return; }

        var skinParts = parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
        var clothParts = parts.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
        if (skinParts.Count == 0 || clothParts.Count == 0) { output.WriteLine($"{label}: needs both meshes"); return; }

        var skin = new BodySurface(WithParts(parts, skinParts), BodySurface.CellFor(MeanEdge(parts)));

        var inside = new List<(float Depth, float Y)>();
        int over = 0;
        foreach (int v in clothParts.SelectMany(p => p.Triangles).Distinct())
        {
            var p = At(parts, v);
            if (!Bust(p)) continue;
            if (!skin.Nearest(p, 0.03f, out var hit)) continue;
            over++;
            float signed = Vector3.Dot(p - hit.Point, hit.Normal);
            if (signed < 0f) inside.Add((-signed, p.Y));
        }

        output.WriteLine("");
        output.WriteLine($"--- {label}");
        output.WriteLine($"    {over:N0} cup verts with breast under them");
        if (inside.Count == 0) { output.WriteLine("    none of the cup is inside the breast"); return; }

        var d = inside.Select(i => i.Depth).OrderBy(x => x).ToList();
        output.WriteLine($"    INSIDE the breast: {inside.Count:N0} ({inside.Count / (float)Math.Max(over, 1):P1})  " +
                         $"mean {d.Average() * 1000f:F2} mm  p95 {d[(int)(d.Count * 0.95f)] * 1000f:F2} mm  " +
                         $"max {d[^1] * 1000f:F2} mm");
    }

    /// <summary>The model with only these parts, so one of its meshes can be used as a surface on its own.</summary>
    private static ModelParts WithParts(ModelParts m, List<ModelPart> parts)
        => new()
        {
            Positions = m.Positions,
            Normals = m.Normals,
            MeshSpans = m.MeshSpans,
            Parts = [.. parts],
            AttributeNames = m.AttributeNames,
            Min = m.Min,
            Max = m.Max,
            ShatteredSubmeshes = m.ShatteredSubmeshes,
        };

    /// <summary>
    /// Why the cup ends up inside the breast, and what clears it. The push-out exists to undo exactly this, so either
    /// it is not seeing these vertices or something is holding it back — and "Clear the body" is the switch whose own
    /// documentation describes this garment: one whose skin is drawn over the body's, where cloth left buried shows as
    /// the body poking through the fabric.
    /// </summary>
    [Fact]
    public void What_clears_the_cup_out_of_the_breast()
    {
        if (Setup() is not { } setup) { output.WriteLine("bodies not found"); return; }
        var (garment, garmentBytes, pairs) = setup;

        output.WriteLine($"{"push-out",10}{"clear body",12}{"new skin",10}{"buried",9}{"mean",9}{"max",9}{"pushed",9}");
        foreach (bool push in new[] { true, false })
            foreach (bool skin in new[] { true, false })
            {
                var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top", pushOut: push,
                                                replaceSkin: skin, acrossBodies: true);
                var (count, mean, max) = BuriedIn(planned.Model);
                output.WriteLine($"{push,10}{"-",12}{(skin ? "swapped" : "the author's"),14}" +
                                 $"{count,9:N0}{mean,8:F2}mm{max,8:F2}mm{planned.Report.Pushed,9:N0}");
            }

        output.WriteLine("");
        output.WriteLine("for comparison, the author's own file:");
        var (a, am, ax) = BuriedIn(File.ReadAllBytes(SizePath("Small")));
        output.WriteLine($"{"",10}{"",12}{"",10}{a,9:N0}{am,8:F2}mm{ax,8:F2}mm");
    }

    /// <summary>Cup vertices inside the garment's own skin, and how deep.</summary>
    private (int Count, float Mean, float Max) BuriedIn(byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } parts) return (-1, 0f, 0f);
        var skinParts = parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
        var clothParts = parts.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
        if (skinParts.Count == 0 || clothParts.Count == 0) return (-1, 0f, 0f);

        var skin = new BodySurface(WithParts(parts, skinParts), BodySurface.CellFor(MeanEdge(parts)));
        var depths = new List<float>();
        foreach (int v in clothParts.SelectMany(p => p.Triangles).Distinct())
        {
            var p = At(parts, v);
            if (!Bust(p) || !skin.Nearest(p, 0.03f, out var hit)) continue;
            float signed = Vector3.Dot(p - hit.Point, hit.Normal);
            if (signed < 0f) depths.Add(-signed * 1000f);
        }
        return depths.Count == 0 ? (0, 0f, 0f) : (depths.Count, depths.Average(), depths.Max());
    }

    /// <summary>
    /// Whether the refit leaves the cloth and the skin beside it weighted to DIFFERENT bodies.
    /// <para/>
    /// PlanWeights rewrites cloth vertices only. With the skin swapped that is consistent — the skin arrives carrying
    /// the new body's own weights. With the skin KEPT it may not be: the cloth is moved onto the new body's weighting
    /// while the garment's own body mesh, drawn right beside it and often sharing its vertices, keeps the old body's.
    /// Two surfaces a millimetre apart, weighted to two different bodies, come apart the moment the bust bone turns.
    /// </summary>
    [Fact]
    public void Are_the_cloth_and_the_skin_weighted_to_the_same_body()
    {
        if (Setup() is not { } setup) { output.WriteLine("bodies not found"); return; }
        var (garment, garmentBytes, pairs) = setup;

        foreach (bool swap in new[] { false, true })
        {
            var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top",
                                            replaceSkin: swap, acrossBodies: true);
            Skinning($"refit, new body's skin = {swap}", planned.Model);
            if (planned.Report.Swap is { } sw)
                output.WriteLine($"    (swap: removed {sw.Removed}, added {sw.Added}, reweighted {sw.Reweighted:N0})");
        }

        Skinning("the author's own file", File.ReadAllBytes(SizePath("Small")));
    }

    /// <summary>
    /// Seams a solve run HERE pulls apart, across the range of gaps an author might leave. The lock has to close the
    /// ones that matter without welding a panel to itself, so the whole curve is worth seeing, not one number.
    /// </summary>
    [Fact]
    public void Seams_the_refit_pulls_apart()
    {
        if (Setup() is not { } setup) return;
        var (garment, garmentBytes, pairs) = setup;
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top", replaceSkin: false, acrossBodies: true);
        var refit = ModelPartReader.Read(planned.Model)!;
        if (refit.Positions.Length != garment.Positions.Length) { output.WriteLine("count changed"); return; }

        int vc = garment.Positions.Length / 3;
        foreach (float tol in new[] { 0.0002f, 0.0005f, 0.001f, 0.002f, 0.005f })
        {
            var grid = new Dictionary<(int, int, int), List<int>>();
            for (int v = 0; v < vc; v++)
            {
                var p = At(garment, v);
                var key = ((int)MathF.Floor(p.X / tol), (int)MathF.Floor(p.Y / tol), (int)MathF.Floor(p.Z / tol));
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
                list.Add(v);
            }

            int touching = 0, opened = 0;
            float worst = 0f;
            for (int v = 0; v < vc; v++)
            {
                var p = At(garment, v);
                var key = ((int)MathF.Floor(p.X / tol), (int)MathF.Floor(p.Y / tol), (int)MathF.Floor(p.Z / tol));
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!grid.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var list)) continue;
                            foreach (int w in list)
                            {
                                if (w <= v) continue;
                                float was = Vector3.Distance(p, At(garment, w));
                                if (was > tol) continue;
                                touching++;
                                float now = Vector3.Distance(At(refit, v), At(refit, w));
                                if (now - was <= 0.0002f) continue;
                                opened++;
                                if (now > worst) worst = now;
                            }
                        }
            }
            output.WriteLine($"   within {tol * 1000f,5:F1} mm as authored: {touching,7:N0} pairs, " +
                             $"{opened,6:N0} opened, worst now {worst * 1000f,6:F2} mm");
        }
    }

    /// <summary>
    /// Seams in the file that was actually SAVED, at several tolerances — not a solve run here. The author's pieces do
    /// not all meet at zero: a panel laid over a boning strip can be a fraction of a millimetre clear of it and still
    /// read as joined, and whatever tolerance the lock uses, anything the author left wider than it is free to move.
    /// </summary>
    [Fact]
    public void Seams_in_the_saved_file()
    {
        string folder = Mod + @"\Body Retarget";
        if (!Directory.Exists(folder) || !File.Exists(SizePath("Small"))) return;

        string newest = Directory.GetFiles(folder, "*e6010_top.mdl", SearchOption.AllDirectories)
                                 .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                                 .FirstOrDefault() ?? "";
        if (newest.Length == 0) return;
        output.WriteLine($"saved {new FileInfo(newest).LastWriteTime:HH:mm}  {newest.Split(@"\Body Retarget\")[^1].Split('\\')[0]}");

        var author = ModelPartReader.Read(File.ReadAllBytes(SizePath("Small")))!;
        var refit = ModelPartReader.Read(File.ReadAllBytes(newest))!;

        // The saved file may carry a swapped skin, which changes the numbering; pair up through the cloth span.
        var a = author.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(author, sp));
        var r = refit.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(refit, sp));
        if (a.Count == 0 || a.Count != r.Count) { output.WriteLine("cloth spans do not correspond"); return; }
        output.WriteLine($"cloth: {a.Count:N0} verts, author base {a.BaseVertex}, refit base {r.BaseVertex}");

        foreach (float tol in new[] { 0.0002f, 0.0005f, 0.001f, 0.002f, 0.005f })
        {
            var grid = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < a.Count; i++)
            {
                var p = At(author, a.BaseVertex + i);
                var key = ((int)MathF.Floor(p.X / tol), (int)MathF.Floor(p.Y / tol), (int)MathF.Floor(p.Z / tol));
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
                list.Add(i);
            }

            int touching = 0, opened = 0;
            float worst = 0f;
            var wideY = new List<float>();
            for (int i = 0; i < a.Count; i++)
            {
                var p = At(author, a.BaseVertex + i);
                var key = ((int)MathF.Floor(p.X / tol), (int)MathF.Floor(p.Y / tol), (int)MathF.Floor(p.Z / tol));
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!grid.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var list)) continue;
                            foreach (int j in list)
                            {
                                if (j <= i) continue;
                                float was = Vector3.Distance(p, At(author, a.BaseVertex + j));
                                if (was > tol) continue;
                                touching++;
                                float now = Vector3.Distance(At(refit, r.BaseVertex + i), At(refit, r.BaseVertex + j));
                                if (now - was <= 0.0002f) continue;
                                opened++;
                                if (now > worst) worst = now;
                                wideY.Add(At(refit, r.BaseVertex + i).Y);
                            }
                        }
            }
            output.WriteLine($"   within {tol * 1000f,5:F1} mm as authored: {touching,6:N0} pairs, " +
                             $"{opened,6:N0} opened, worst now {worst * 1000f,6:F2} mm" +
                             (wideY.Count > 0 ? $"   y {wideY.Min():F3}..{wideY.Max():F3}" : ""));
        }
    }

    private static Vector3 At(ModelParts m, int v)
        => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    private static float MeanEdge(ModelParts m)
    {
        double sum = 0; int n = 0;
        foreach (var part in m.Parts)
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                sum += (At(m, part.Triangles[t + 1]) - At(m, part.Triangles[t])).Length();
                n++;
            }
        return n == 0 ? 0.01f : (float)(sum / n);
    }

    private static float[] Uv(byte[] mdl) => BodyRetargetDiagTests.Uv(mdl);
}
