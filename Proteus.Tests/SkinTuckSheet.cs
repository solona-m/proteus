using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A flat patch of a body's skin and a top over it, at real scale, for the cut's tuck under the cloth: a grid of
/// <see cref="Columns"/> by <see cref="Rows"/> vertices 10 mm apart in the XY plane facing +Z, row 0 at the bottom. The
/// top's own skin is the body's top rows exactly; its cloth is a grid over a span of rows, each row standing off the skin
/// by its own height — loose over some rows, tight over others.
/// </summary>
internal static class SkinTuckSheet
{
    internal const string Skin = "/mt_c0201b0001_bibo.mtrl";
    internal const string Cloth = "/mt_c0201e6114_top_a.mtrl";
    internal const string Piercing = "/mt_c0201b0001_piercings.mtrl";

    internal const int Columns = 9, Rows = 14;
    internal const float Step = 0.01f, Bottom = 1.0f;

    /// <summary>The first row of the top's own skin: rows from here up are drawn by the author.</summary>
    internal const int Edge = 10;

    internal static float X(int column) => (column - (Columns - 1) / 2f) * Step;
    internal static float Y(int row) => Bottom + row * Step;

    /// <summary>The grid's triangles over rows <paramref name="from"/>..<paramref name="to"/> (vertex rows), indices
    /// counted from <paramref name="first"/>'s row.</summary>
    private static int[] Grid(int from, int to, int first = 0)
    {
        var tris = new List<int>();
        for (int r = from; r < to; r++)
            for (int c = 0; c + 1 < Columns; c++)
            {
                int a = (r - first) * Columns + c, b = a + 1, d = a + Columns, e = d + 1;
                tris.AddRange([a, b, e, a, e, d]);
            }
        return [.. tris];
    }

    /// <summary>The bones of row <paramref name="row"/>: the spine, or an upper arm for the rows <paramref name="arm"/>
    /// names.</summary>
    private static (string Bone, float W)[] Bones(int row, Func<int, bool>? arm)
        => arm?.Invoke(row) == true ? [("j_ude_a_l", 1f)] : [("j_sebo_c", 1f)];

    private static SyntheticModel.SurfaceMesh Sheet(string material, int from, int to, Func<int, float> z,
                                                    Func<int, bool>? arm = null)
        => Sheet(material, from, to, (r, _) => z(r), arm);

    private static SyntheticModel.SurfaceMesh Sheet(string material, int from, int to, Func<int, int, float> z,
                                                    Func<int, bool>? arm = null)
    {
        var pos = new List<Vector3>();
        var uv = new List<Vector2>();
        var w = new List<(string, float)[]?>();
        for (int r = from; r <= to; r++)
            for (int c = 0; c < Columns; c++)
            {
                pos.Add(new Vector3(X(c), Y(r), z(r, c)));
                uv.Add(new Vector2((float)c / (Columns - 1), (float)r / (Rows - 1)));
                w.Add(Bones(r, arm));
            }
        return new SyntheticModel.SurfaceMesh(material, [.. pos], [.. pos.Select(_ => Vector3.UnitZ)], [.. uv], [.. w],
                                              [(0u, Grid(from, to, from))]);
    }

    /// <summary>The body: the whole grid, flat at z = 0.</summary>
    /// <param name="arm">Rows rigged to an upper arm instead of the spine.</param>
    /// <param name="splitAt">Split the skin into two meshes at this row: rows below it in the second.</param>
    /// <param name="hiddenBelow">Rows below this go in a submesh tagged <c>atr_tv_b</c>, a variant the body may hide.</param>
    /// <param name="halvesAt">Two untagged submeshes split at this row — below it first, or last when
    /// <paramref name="reversed"/>: the same skin in another order.</param>
    internal static byte[] Body(Func<int, bool>? arm = null, int? splitAt = null, int? hiddenBelow = null,
                                bool piercing = false, int? halvesAt = null, bool reversed = false)
    {
        var meshes = new List<SyntheticModel.SurfaceMesh>();
        var attrs = new List<string>();
        if (halvesAt is { } half)
        {
            var whole = Sheet(Skin, 0, Rows - 1, _ => 0f, arm);
            (uint, int[]) low = (0u, Grid(0, half)), high = (0u, Grid(half, Rows - 1));
            meshes.Add(whole with { Submeshes = reversed ? [high, low] : [low, high] });
        }
        else if (splitAt is { } split)
        {
            meshes.Add(Sheet(Skin, split, Rows - 1, _ => 0f, arm));
            meshes.Add(Sheet(Skin, 0, split, _ => 0f, arm));
        }
        else if (hiddenBelow is { } hidden)
        {
            attrs.Add("atr_tv_b");
            var whole = Sheet(Skin, 0, Rows - 1, _ => 0f, arm);
            meshes.Add(whole with
            {
                Submeshes = [(0u, Grid(hidden, Rows - 1)), (1u, Grid(0, hidden))],
            });
        }
        else
            meshes.Add(Sheet(Skin, 0, Rows - 1, _ => 0f, arm));

        if (piercing)
        {
            // A ring flat on the skin around the middle of row 7, the way a body mod ships a navel or nipple ring.
            var centre = new Vector3(0f, Y(7) + Step / 2, 0.0005f);
            var ring = new List<Vector3> { centre };
            for (int k = 0; k < 6; k++)
            {
                float a = k * MathF.Tau / 6;
                ring.Add(centre + new Vector3(MathF.Cos(a), MathF.Sin(a), 0f) * 0.003f);
            }
            var tris = new List<int>();
            for (int k = 0; k < 6; k++) tris.AddRange([0, 1 + k, 1 + (k + 1) % 6]);
            meshes.Add(new SyntheticModel.SurfaceMesh(Piercing, [.. ring], [.. ring.Select(_ => Vector3.UnitZ)],
                                                      [.. ring.Select(_ => Vector2.Zero)],
                                                      [.. ring.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])],
                                                      [(0u, [.. tris])]));
        }
        return SyntheticModel.BuildSurface(attrs, [.. meshes]);
    }

    /// <summary>
    /// The top: its own skin over rows <see cref="Edge"/> and up, and cloth over rows <paramref name="clothFrom"/> to
    /// <see cref="Rows"/> - 1, row r standing <paramref name="clothZ"/>(r) off the skin.
    /// </summary>
    /// <param name="clothAttr">Tag the cloth with this attribute (an IMC variant switch, say).</param>
    /// <param name="withSkin">False: a top with no skin of its own.</param>
    /// <param name="extra">More cloth, as (first row, last row, height) — a second layer, buried or not.</param>
    internal static byte[] Top(Func<int, float> clothZ, int clothFrom = 2, string? clothAttr = null, bool withSkin = true,
                               (int From, int To, float Z)? extra = null, int skinFrom = Edge)
        => Top((r, _) => clothZ(r), clothFrom, clothAttr, withSkin, extra, skinFrom);

    /// <inheritdoc cref="Top(Func{int, float}, int, string?, bool, (int From, int To, float Z)?, int)"/>
    /// <param name="clothZ">Each cloth vertex's height off the skin, by row and column.</param>
    internal static byte[] Top(Func<int, int, float> clothZ, int clothFrom = 2, string? clothAttr = null,
                               bool withSkin = true, (int From, int To, float Z)? extra = null, int skinFrom = Edge)
    {
        var meshes = new List<SyntheticModel.SurfaceMesh>();
        if (withSkin) meshes.Add(Sheet(Skin, skinFrom, Rows - 1, _ => 0f));
        var cloth = Sheet(Cloth, clothFrom, Rows - 1, clothZ);
        if (clothAttr != null) cloth = cloth with { Submeshes = [(1u, cloth.Submeshes[0].Triangles)] };
        meshes.Add(cloth);
        if (extra is { } layer) meshes.Add(Sheet(Cloth, layer.From, layer.To, _ => layer.Z));
        return SyntheticModel.BuildSurface(clothAttr != null ? [clothAttr] : [], [.. meshes]);
    }

    /// <summary>The loose sheet: cloth 15 mm off over the rows from <paramref name="looseFrom"/> up, 1 mm off below.</summary>
    internal static byte[] LooseTop(int looseFrom = 7) => Top(r => r >= looseFrom ? 0.015f : 0.001f);

    /// <summary>A resized chest whose new body is <paramref name="body"/>, read through the same correspondence on both
    /// sides: the top's skin already lies on it.</summary>
    internal static BodyRetarget.SlotPair Resized(byte[] body, string slot = "_top", IReadOnlySet<string>? hidden = null)
    {
        var target = BodyRetarget.Without(ModelPartReader.Read(body)!, hidden);
        Assert.True(IdentityCorrespondence.TryBuild(target, target, slot, out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair(slot, built!, target, body, body, hidden);
    }

    /// <summary>The body skin triangles a rebuilt model draws, by their centre row (rounded down) — which rows of the
    /// sheet the swap put in.</summary>
    internal static SortedDictionary<int, int> DrawnRows(byte[] model)
    {
        var read = ModelPartReader.Read(model)!;
        var rows = new SortedDictionary<int, int>();
        foreach (var part in read.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                float y = (read.Positions[part.Triangles[t] * 3 + 1] + read.Positions[part.Triangles[t + 1] * 3 + 1]
                           + read.Positions[part.Triangles[t + 2] * 3 + 1]) / 3f;
                int row = (int)MathF.Floor((y - Bottom) / Step);
                rows[row] = rows.GetValueOrDefault(row) + 1;
            }
        return rows;
    }

    internal static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
}
