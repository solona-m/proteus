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

    /// <summary>The bones of row <paramref name="row"/>: the spine, or for the rows <paramref name="arm"/> names
    /// <paramref name="armBone"/> by <paramref name="armShare"/> and the spine by the rest.</summary>
    private static (string Bone, float W)[] Bones(int row, Func<int, bool>? arm, float armShare = 1f,
                                                  string armBone = "j_ude_a_l")
        => arm?.Invoke(row) != true ? [("j_sebo_c", 1f)]
         : armShare >= 1f ? [(armBone, 1f)]
         : [(armBone, armShare), ("j_sebo_c", 1f - armShare)];

    private static SyntheticModel.SurfaceMesh Sheet(string material, int from, int to, Func<int, float> z,
                                                    Func<int, bool>? arm = null, float armShare = 1f,
                                                    string armBone = "j_ude_a_l")
        => Sheet(material, from, to, (r, _) => z(r), arm, armShare, armBone);

    /// <param name="shaped">Give the vertices their own surface's normals; otherwise every one faces +Z, as the sheets the
    /// goldens were pinned on do.</param>
    private static SyntheticModel.SurfaceMesh Sheet(string material, int from, int to, Func<int, int, float> z,
                                                    Func<int, bool>? arm = null, float armShare = 1f,
                                                    string armBone = "j_ude_a_l", bool shaped = false)
    {
        var pos = new List<Vector3>();
        var uv = new List<Vector2>();
        var w = new List<(string, float)[]?>();
        for (int r = from; r <= to; r++)
            for (int c = 0; c < Columns; c++)
            {
                pos.Add(new Vector3(X(c), Y(r), z(r, c)));
                uv.Add(new Vector2((float)c / (Columns - 1), (float)r / (Rows - 1)));
                w.Add(Bones(r, arm, armShare, armBone));
            }
        var normals = shaped ? Normals(pos, Grid(from, to, from)) : pos.Select(_ => Vector3.UnitZ).ToArray();
        return new SyntheticModel.SurfaceMesh(material, [.. pos], normals, [.. uv], [.. w],
                                              [(0u, Grid(from, to, from))]);
    }

    /// <summary>Area-weighted vertex normals of a triangle list.</summary>
    private static Vector3[] Normals(IReadOnlyList<Vector3> pos, int[] tris)
    {
        var n = new Vector3[pos.Count];
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            var f = Vector3.Cross(pos[tris[t + 1]] - pos[tris[t]], pos[tris[t + 2]] - pos[tris[t]]);
            n[tris[t]] += f; n[tris[t + 1]] += f; n[tris[t + 2]] += f;
        }
        for (int i = 0; i < n.Length; i++) n[i] = n[i].LengthSquared() > 0 ? Vector3.Normalize(n[i]) : Vector3.UnitZ;
        return n;
    }

    /// <summary>The body: the whole grid, flat at z = 0.</summary>
    /// <param name="arm">Rows rigged to an upper arm instead of the spine.</param>
    /// <param name="splitAt">Split the skin into two meshes at this row: rows below it in the second.</param>
    /// <param name="hiddenBelow">Rows below this go in a submesh tagged <c>atr_tv_b</c>, a variant the body may hide.</param>
    /// <param name="halvesAt">Two untagged submeshes split at this row — below it first, or last when
    /// <paramref name="reversed"/>: the same skin in another order.</param>
    /// <param name="armShare">How much of the <paramref name="arm"/> rows' weight is on the arm; the spine has the rest.</param>
    /// <param name="armBone">The arm's bone — or a breast's, <c>j_mune_l</c>, for a bust.</param>
    /// <param name="z">The skin's height off the plane by row and column; flat when null.</param>
    /// <param name="hiddenBand">Rows (from, to) in a variant the body mod does not draw (tagged atr_tv_b); null for none.</param>
    /// <param name="piercingAt">The ring's row (its middle) and height, instead of row 7.5 on the plane.</param>
    internal static byte[] Body(Func<int, bool>? arm = null, int? splitAt = null, int? hiddenBelow = null,
                                bool piercing = false, int? halvesAt = null, bool reversed = false, float armShare = 1f,
                                string armBone = "j_ude_a_l", Func<int, int, float>? z = null,
                                (int From, int To)? hiddenBand = null, (float Row, float Z)? piercingAt = null)
    {
        var height = z ?? ((_, _) => 0f);
        SyntheticModel.SurfaceMesh Skin_(int from, int to) => Sheet(Skin, from, to, height, arm, armShare, armBone,
                                                                    shaped: z != null);
        var meshes = new List<SyntheticModel.SurfaceMesh>();
        var attrs = new List<string>();
        if (halvesAt is { } half)
        {
            var whole = Skin_(0, Rows - 1);
            (uint, int[]) low = (0u, Grid(0, half)), high = (0u, Grid(half, Rows - 1));
            meshes.Add(whole with { Submeshes = reversed ? [high, low] : [low, high] });
        }
        else if (splitAt is { } split)
        {
            meshes.Add(Skin_(split, Rows - 1));
            meshes.Add(Skin_(0, split));
        }
        else if (hiddenBand is { } band)
        {
            attrs.Add("atr_tv_b");
            var whole = Skin_(0, Rows - 1);
            meshes.Add(whole with
            {
                Submeshes = [(0u, [.. Grid(0, band.From), .. Grid(band.To, Rows - 1)]), (1u, Grid(band.From, band.To))],
            });
        }
        else if (hiddenBelow is { } hidden)
        {
            attrs.Add("atr_tv_b");
            var whole = Skin_(0, Rows - 1);
            meshes.Add(whole with
            {
                Submeshes = [(0u, Grid(hidden, Rows - 1)), (1u, Grid(0, hidden))],
            });
        }
        else
            meshes.Add(Skin_(0, Rows - 1));

        if (piercing || piercingAt != null)
        {
            // A ring flat on the skin around the middle of row 7, the way a body mod ships a navel or nipple ring.
            var centre = piercingAt is { } at ? new Vector3(0f, Bottom + at.Row * Step, at.Z + 0.0005f)
                                              : new Vector3(0f, Y(7) + Step / 2, 0.0005f);
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

    /// <summary>The breast's rows: a ridge across the sheet over rows 3-8, rigged to the left breast.</summary>
    internal static bool BreastRow(int row) => row is >= 3 and <= 8;

    /// <summary>The breast ridge's height over row <paramref name="row"/> at its full <paramref name="height"/>: zero at
    /// rows 2 and 9, smooth between.</summary>
    internal static float Ridge(int row, float height)
        => row is > 2 and < 9 ? height * MathF.Pow(MathF.Sin(MathF.PI * (row - 2) / 7f), 2) : 0f;

    /// <summary>A breast-rigged body whose ridge stands <paramref name="height"/> off the plane (0: the flat one).</summary>
    internal static byte[] Bust(float height, Func<int, bool>? breast = null)
        => Body(arm: breast ?? BreastRow, armBone: "j_mune_l",
                z: height == 0f ? null : (r, _) => Ridge(r, height));

    /// <summary>
    /// Cloth edges of <paramref name="after"/> stretched past 1.5× their length in <paramref name="before"/> and by more
    /// than 5 mm, by index — two plans of the same garment, laid (see <see cref="Laid"/>).
    /// </summary>
    /// <param name="withSkin">The garment's own skin too — a skin lip welded to the cloth moves with it.</param>
    internal static int ClothStretch(ModelParts before, ModelParts after, Action<string>? describe = null, bool withSkin = false)
    {
        Assert.Equal(before.Positions.Length, after.Positions.Length);
        Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
        int stretched = 0;
        var seen = new HashSet<(int, int)>();
        foreach (var part in before.Parts.Where(p => p.Island < 0 && (withSkin || !SecondSkinWriter.IsBodySkinMaterial(p.Material))))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = part.Triangles[t + e], b = part.Triangles[t + (e + 1) % 3];
                    if (!seen.Add(a < b ? (a, b) : (b, a))) continue;
                    float was = Vector3.Distance(At(before, a), At(before, b));
                    float now = Vector3.Distance(At(after, a), At(after, b));
                    if (now <= 1.5f * was || now - was <= 0.005f) continue;
                    stretched++;
                    describe?.Invoke($"edge {a}-{b} at y {At(after, a).Y:0.000}: {was * 1000:0.0} -> {now * 1000:0.0} mm");
                }
        return stretched;
    }

    /// <summary>Cloth triangles of <paramref name="laid"/> turned over against <paramref name="authored"/>'s winding — the
    /// same garment, by index; what the written cloth shows, counted here rather than taken from the plan's report.</summary>
    /// <param name="withSkin">The garment's own skin too — a skin lip welded to the cloth moves with it.</param>
    internal static int FoldedAgainstAuthored(ModelParts authored, ModelParts laid, bool withSkin = false)
    {
        Vector3 P(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
        int folded = 0;
        foreach (var part in authored.Parts.Where(p => p.Island < 0 && (withSkin || !SecondSkinWriter.IsBodySkinMaterial(p.Material))))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                var was = Vector3.Cross(P(authored, b) - P(authored, a), P(authored, c) - P(authored, a));
                if (was.LengthSquared() < 1e-20f) continue;
                var now = Vector3.Cross(P(laid, b) - P(laid, a), P(laid, c) - P(laid, a));
                if (Vector3.Dot(was, now) <= 0f) folded++;
            }
        return folded;
    }

    /// <summary>How far a point stands off <paramref name="on"/> along its normal at the nearest point (negative
    /// inside); NaN with nothing within 60 mm.</summary>
    internal static float Standoff(Vector3 p, BodySurface on)
        => on.Nearest(p, 0.06f, out var h) ? Vector3.Dot(p - h.Point, h.Normal) : float.NaN;

    /// <summary>The garment as a plan laid it, before the rebuild: the garment's own vertices, moved.</summary>
    internal static ModelParts Laid(byte[] garment, BodyRetarget.Planned plan)
        => ModelPartReader.Read(MeshVolumeService.Inflate(garment, plan.Edit).Model)!;

    /// <summary>A body that grew: the chest pair from <paramref name="source"/> to <paramref name="target"/>, the same
    /// grid, each vertex to its own — the garment authored on the first refitted onto the second.</summary>
    internal static BodyRetarget.SlotPair Grown(byte[] source, byte[] target, string slot = "_top")
    {
        var s = ModelPartReader.Read(source)!;
        var t = ModelPartReader.Read(target)!;
        Assert.True(IdentityCorrespondence.TryBuild(s, t, slot, out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair(slot, built!, t, target, source);
    }

    /// <summary>
    /// The top with its cloth, below its own skin, buried <paramref name="depth"/> inside the flat body it was authored on
    /// — the way the game buries a top's cloth in its own body — and 3 mm over its own skin above.
    /// </summary>
    internal static byte[] BuriedTop(float depth) => Top(r => r < Edge ? -depth : 0.003f);

    /// <summary>
    /// The top with coarse cloth, as the game's own is, buried <paramref name="depth"/> inside the flat body it was authored
    /// on: a 3×3 grid of points 35-40 mm apart over rows 2, 5.5 and 9 and columns 0, 4 and 8 — the middle row over the
    /// ridge's peak, the outer ones where it is flat — so no point of it lies near cloth the usual clear reaches, whose
    /// spread would drag it out regardless. Its own skin over rows <see cref="Edge"/> and up, as every sheet top's.
    /// </summary>
    /// <param name="lining">A second coarse sheet this much deeper again behind it, over rows 5-6 — where the ridge grew
    /// past <see cref="BodyRetarget.DeepGrowLeast"/> all over it; 0 for none.</param>
    /// <param name="rows">The cloth's rows instead of 2, 5.5 and 9.</param>
    /// <param name="columns">The cloth's columns instead of 0, 4 and 8.</param>
    internal static byte[] CoarseBuriedTop(float depth, float lining = 0f, float[]? rows = null, int[]? columns = null)
    {
        SyntheticModel.SurfaceMesh Coarse(float z, float[] rs, int[] cs)
        {
            var pos = rs.SelectMany(r => cs.Select(c => new Vector3(X(c), Bottom + r * Step, z))).ToArray();
            var tris = new List<int>();
            for (int r = 0; r + 1 < rs.Length; r++)
                for (int c = 0; c + 1 < cs.Length; c++)
                {
                    int a = r * cs.Length + c, b = a + 1, d = a + cs.Length, e = d + 1;
                    tris.AddRange([a, b, e, a, e, d]);
                }
            return new SyntheticModel.SurfaceMesh(Cloth, pos, [.. pos.Select(_ => Vector3.UnitZ)], [.. pos.Select(_ => Vector2.Zero)],
                                                  [.. pos.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])], [(0u, [.. tris])]);
        }
        int[] cols = columns ?? [0, 4, 8];
        var meshes = new List<SyntheticModel.SurfaceMesh>
        {
            Sheet(Skin, Edge, Rows - 1, _ => 0f), Coarse(-depth, rows ?? [2f, (2f + Edge - 1) / 2f, Edge - 1], cols),
        };
        if (lining > 0f) meshes.Add(Coarse(-depth - lining, [5f, 5.5f, 6f], cols));
        return SyntheticModel.BuildSurface([], [.. meshes]);
    }

    /// <summary>
    /// A game top whose neckline the refit carries off the upper chest: the bust (rows 3-8) grows <paramref name="grow"/>
    /// while the chest above it (rows 10 and up, half at row 9) sinks <paramref name="shrink"/>; the top's cloth is 6 mm
    /// buried over the breast and 6 mm off above, as the game authors it. The push lifts the buried cloth out of the grown
    /// breast and carries the neckline with it — measured: the fine sheet's rows 9-11 end 9-12 mm off (own 6), the coarse
    /// one's rows 10-12 24-29 mm. Row 2, below the breast, is carried off too: a drape.
    /// </summary>
    /// <param name="coarse">Coarse cloth (<see cref="CoarseNeckline"/>) instead of the fine sheet.</param>
    /// <param name="lining">A second cloth sheet over rows 9-13 this far off, under the shell (8 mm off there); 0 for none.</param>
    internal static (byte[] Top, BodyRetarget.SlotPair Pair) FloatingNeckline(bool coarse = false, float grow = 0.02f,
                                                                            float shrink = 0.008f, float lining = 0f)
    {
        byte[] target = Body(arm: BreastRow, armBone: "j_mune_l",
                             z: (r, _) => Ridge(r, grow) - (r >= 10 ? shrink : r == 9 ? shrink / 2 : 0f));
        byte[] top = coarse ? CoarseNeckline(-0.006f, 0.006f)
                   : lining > 0f ? Top(r => r <= 8 ? -0.006f : 0.008f, extra: (9, Rows - 1, lining))
                   : Top(r => r <= 8 ? -0.006f : 0.006f);
        return (top, Grown(Bust(0f), target));
    }

    /// <summary>
    /// A top with coarse cloth over rows 2, 5, 8, 10 and 12 (columns 0, 4 and 8): <paramref name="buried"/> off its flat
    /// body up to row 8, <paramref name="off"/> above — a game top's cloth buried over the breast, standing off the upper
    /// chest — and its own skin over rows <see cref="Edge"/> and up.
    /// </summary>
    internal static byte[] CoarseNeckline(float buried, float off, float[]? rows = null)
    {
        float[] rs = rows ?? [2f, 5f, 8f, 10f, 12f];
        int[] cs = [0, 4, 8];
        var pos = rs.SelectMany(r => cs.Select(c => new Vector3(X(c), Bottom + r * Step, r <= 8f ? buried : off))).ToArray();
        var tris = new List<int>();
        for (int r = 0; r + 1 < rs.Length; r++)
            for (int c = 0; c + 1 < cs.Length; c++)
            {
                int a = r * cs.Length + c, b = a + 1, d = a + cs.Length, e = d + 1;
                tris.AddRange([a, b, e, a, e, d]);
            }
        var cloth = new SyntheticModel.SurfaceMesh(Cloth, pos, [.. pos.Select(_ => Vector3.UnitZ)], [.. pos.Select(_ => Vector2.Zero)],
                                                   [.. pos.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])], [(0u, [.. tris])]);
        return SyntheticModel.BuildSurface([], Sheet(Skin, Edge, Rows - 1, _ => 0f), cloth);
    }

    /// <summary>
    /// A top whose own skin is two triangles, the corners of rows <see cref="Edge"/> to <see cref="Rows"/> - 1 — a game
    /// top's coarse skin — with its cloth over the rows below only.
    /// </summary>
    /// <param name="clothTo">The cloth's last row; below the author's edge unless given.</param>
    /// <param name="patch">A patch of cloth over the dip's rows (11-12) and columns (3-5), at this height; null for none.</param>
    internal static byte[] CoarseSkinTop(float clothZ = 0.015f, int clothTo = Edge - 1, float? patch = null)
    {
        Vector3[] corners =
        [
            new(X(0), Y(Edge), 0f), new(X(Columns - 1), Y(Edge), 0f),
            new(X(0), Y(Rows - 1), 0f), new(X(Columns - 1), Y(Rows - 1), 0f),
        ];
        var skin = new SyntheticModel.SurfaceMesh(Skin, corners, [.. corners.Select(_ => Vector3.UnitZ)],
                                                  [.. corners.Select(_ => Vector2.Zero)],
                                                  [.. corners.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])],
                                                  [(0u, [0, 1, 3, 0, 3, 2])]);
        var meshes = new List<SyntheticModel.SurfaceMesh> { skin, Sheet(Cloth, 2, clothTo, _ => clothZ) };
        if (patch is float z)
        {
            var pos = new[] { 10.5f, 13f }.SelectMany(r => new[] { 2, 6 }.Select(c => new Vector3(X(c), Bottom + r * Step, z))).ToArray();
            meshes.Add(new SyntheticModel.SurfaceMesh(Cloth, pos, [.. pos.Select(_ => Vector3.UnitZ)], [.. pos.Select(_ => Vector2.Zero)],
                                                      [.. pos.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])],
                                                      [(0u, [0, 1, 3, 0, 3, 2])]));
        }
        return SyntheticModel.BuildSurface([], [.. meshes]);
    }

    /// <summary>A body whose chest dips <paramref name="depth"/> (negative: bulges) under rows 11-12, columns 3-5 —
    /// under <see cref="CoarseSkinTop"/>'s skin, where its chord spans 85 mm.</summary>
    /// <param name="hideDip">Put the dip's rows in a variant the body mod does not draw.</param>
    /// <param name="ringInDip">A piercing ring in the middle of the dip.</param>
    internal static byte[] Dipped(float depth, Func<int, bool>? arm = null, bool hideDip = false, bool ringInDip = false)
        => Body(arm: arm, hiddenBand: hideDip ? (11, 12) : null, piercingAt: ringInDip ? (11.5f, -depth) : null,
                z: (r, c) => r is 11 or 12 && c is >= 3 and <= 5 ? -depth : 0f);

    /// <summary>Whether a rebuilt model draws a skin triangle lying wholly in the dip of <see cref="Dipped"/> — the cut
    /// itself draws the ones on its rim, through their corners outside it.</summary>
    internal static bool DrawsDip(byte[] model)
    {
        var read = ModelPartReader.Read(model)!;
        bool In(int v) => MathF.Abs(read.Positions[v * 3 + 2]) > 0.002f;
        return read.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                   .Any(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                       .Any(t => In(p.Triangles[t * 3]) && In(p.Triangles[t * 3 + 1]) && In(p.Triangles[t * 3 + 2])));
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

    /// <summary>
    /// The body skin triangles <paramref name="tucked"/> draws that <paramref name="untucked"/> does not, and of those how
    /// many are NOT one of <paramref name="body"/>'s own skin triangles at the same three positions wound the same way —
    /// what the written model shows, checked on the model itself: a tucked triangle is the body's, never a new or
    /// turned-over one.
    /// </summary>
    internal static (int New, int NotTheBodys) NewSkinAgainstBody(ModelParts untucked, ModelParts tucked, ModelParts body)
    {
        var had = new HashSet<string>(SkinTriangles(untucked).Select(t => Oriented(t, ordered: false)));
        var bodys = new HashSet<string>(SkinTriangles(body).Select(t => Oriented(t, ordered: true)));
        int fresh = 0, wrong = 0;
        foreach (var t in SkinTriangles(tucked))
        {
            if (had.Contains(Oriented(t, ordered: false))) continue;
            fresh++;
            if (!bodys.Contains(Oriented(t, ordered: true))) wrong++;
        }
        return (fresh, wrong);
    }

    private static IEnumerable<(Vector3, Vector3, Vector3)> SkinTriangles(ModelParts m)
    {
        Vector3 At(int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
        foreach (var part in m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                yield return (At(part.Triangles[t]), At(part.Triangles[t + 1]), At(part.Triangles[t + 2]));
    }

    /// <summary>A triangle by its corners' positions: in winding order from the least corner when
    /// <paramref name="ordered"/>, any order otherwise.</summary>
    private static string Oriented((Vector3 A, Vector3 B, Vector3 C) t, bool ordered)
    {
        static string K(Vector3 p) => $"{MathF.Round(p.X * 1e5f)},{MathF.Round(p.Y * 1e5f)},{MathF.Round(p.Z * 1e5f)}";
        string[] k = [K(t.A), K(t.B), K(t.C)];
        if (!ordered) return string.Join(";", k.Order(StringComparer.Ordinal));
        int least = Array.IndexOf(k, k.Min(StringComparer.Ordinal));
        return string.Join(";", k[least], k[(least + 1) % 3], k[(least + 2) % 3]);
    }
}
