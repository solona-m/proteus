using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// A Cloth-mode face: asymmetric face art cut onto a second-skin shell of the face, the shell's two sides sent to
/// the two halves of the doubled (<see cref="UVRemapService.FaceSplitSpace"/>) sheet. Real vanilla faces from the
/// game data, when an install is present.
/// </summary>
public class FaceShellUnmirrorTests(ITestOutputHelper o)
{
    private static UVRemapService.UvConversion Convert()
        => new UVRemapService(Substitute.For<IPluginLog>(), ".")
            .UvConverter(UVRemapService.FaceSpace, UVRemapService.FaceSplitSpace, unmirror: true)!;

    private static IEnumerable<(string Path, string Material, byte[] Bytes)> Faces()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        foreach (var (path, mtrl) in new[]
                 {
                     ("chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl", "mt_c0201f0001_fac_a.mtrl"),
                     ("chara/human/c0101/obj/face/f0001/model/c0101f0001_fac.mdl", "mt_c0101f0001_fac_a.mtrl"),
                     ("chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl", "mt_c1401f0001_fac_a.mtrl"),
                 })
            if (data.GetFile(path) is { } file)
                yield return (path, mtrl, file.Data);
    }

    /// <summary>
    /// Every vertex off the midline samples its own side's half. A couple of triangles still stretch: the writer's
    /// diag reports two midline vertices on c0201/c0101 claimed by triangles on both sides, and one vertex cannot sit
    /// in two halves. That is a separate limit, and the bar the shaped shell below is held to.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    public void Each_side_of_a_face_shell_samples_its_own_half()
    {
        foreach (var (path, wrongHalf, stretched) in CutAndMeasure(withShapes: false))
        {
            Assert.True(wrongHalf == 0, $"{path}: {wrongHalf} vertices in the wrong half");
            Assert.True(stretched <= DisputedStretch, $"{path}: {stretched} triangles stretched across the sheet");
        }
    }

    /// <summary>
    /// The same with every shape key the face carries baked in, as the game's customisation does (a lip shape is
    /// <c>shp_mth_*</c>). A morph vertex is named by no authored triangle, so it has to learn its side from the vertex
    /// it replaces; left at the +X default, c0201's mouth put 403 vertices on the other cheek's half and smeared 143
    /// triangles across the sheet.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    public void A_face_shell_with_its_shapes_baked_in_keeps_its_sides()
    {
        var plain = CutAndMeasure(withShapes: false);
        var shaped = CutAndMeasure(withShapes: true);
        for (int i = 0; i < shaped.Count; i++)
        {
            var (path, wrongHalf, stretched) = shaped[i];
            Assert.True(wrongHalf == 0, $"{path}: {wrongHalf} vertices in the wrong half");
            Assert.True(stretched <= plain[i].Stretched,
                $"{path}: {stretched} triangles stretched across the sheet, {plain[i].Stretched} without shapes");
        }
    }

    /// <summary>The disputed-midline triangles a vanilla face shell is known to keep (see above).</summary>
    private const int DisputedStretch = 2;

    /// <summary>Every face is measured before anything is asserted, so one failure still reports the rest.</summary>
    private List<(string Path, int WrongHalf, int Stretched)> CutAndMeasure(bool withShapes)
    {
        var results = new List<(string Path, int WrongHalf, int Stretched)>();
        foreach (var (path, mtrl, mdl) in Faces())
        {
            var keep = SecondSkinWriter.KeepByLeaf(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mtrl });
            HashSet<string>? shapes = null;
            if (withShapes)
            {
                shapes = new HashSet<string>(SecondSkinWriter.Parse(mdl).Shapes.Keys, StringComparer.Ordinal);
                o.WriteLine($"{path}: shapes [{string.Join(", ", shapes)}]");
            }
            var sources = new List<SecondSkinWriter.SourceSpec>
            {
                new(mdl, KeepMaterial: keep, EnabledShapes: shapes, UvConv: Convert(), DropConnectors: false,
                    UnmirrorSides: true),
            };
            var layers = new[] { new SecondSkinLayer { MaterialName = "/ss_0.mtrl", Coverage = null } };
            var diag = new List<string>();
            var shell = SecondSkinWriter.Build(sources, layers, null, out _, diag: diag.Add);
            foreach (var line in diag) o.WriteLine($"  diag: {line}");

            Assert.True(SecondSkinWriter.TryReadLod0Geometry(shell, out var pos, out var uv, out var tri, out _, out _,
                                                             skinOnly: false),
                        $"{path}: the shell could not be read back");

            // A vertex off the midline belongs in the half of the sheet its side owns: +X right, -X left.
            int n = pos.Length / 3, wrongHalf = 0, offMid = 0;
            for (int i = 0; i < n; i++)
            {
                float x = pos[i * 3], u = uv[i * 2];
                if (MathF.Abs(x) < 2e-3f) continue;
                offMid++;
                if (x > 0 ? u < 0.5f : u > 0.5f) wrongHalf++;
            }

            // A triangle whose corners sit in both halves samples a smear across the whole sheet.
            int stretched = 0;
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                float a = uv[tri[t] * 2], b = uv[tri[t + 1] * 2], c = uv[tri[t + 2] * 2];
                if (MathF.Max(a, MathF.Max(b, c)) - MathF.Min(a, MathF.Min(b, c)) > 0.25f) stretched++;
            }

            o.WriteLine($"{path}: {n} vertices ({offMid} off the midline), {wrongHalf} in the wrong half; "
                      + $"{tri.Length / 3} triangles, {stretched} stretched across the sheet");
            results.Add((path, wrongHalf, stretched));
        }
        Assert.True(results.Count > 0, "no face model read out of the game data");
        return results;
    }
}
