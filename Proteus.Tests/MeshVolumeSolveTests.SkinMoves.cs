using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// "Move skin too": the brushes and the Move tools moving the garment's own body skin, the cloth lying on it riding
/// along (<see cref="SkinFollow"/>), and — with it off — the brush exactly as it was. Over a skin sheet with a cloth
/// sheet above it, built here so where every point is, and what lies under it, is known.
/// </summary>
public partial class MeshVolumeSolveTests
{
    private const string Bibo = "/mt_c0201b0001_bibo.mtrl";

    /// <summary>The body-skin materials of the bodies the brush meets: bibo, gen3 (two names) and gen2.</summary>
    public static TheoryData<string> SkinMaterials => new()
    {
        "/mt_c0201b0001_bibo.mtrl", "/mt_c0201b0001_b.mtrl", "/mt_c0201b0001_eve.mtrl", "/mt_c0101b0001_a.mtrl",
    };

    /// <summary>A skin sheet and the cloth over it.</summary>
    /// <param name="SkinAt">Skin vertex [i, j]; the sheet lies in y = 0, normals +Y.</param>
    /// <param name="ClothAt">Cloth vertex [i, j]; <see cref="ClothStride"/> skin cells apart.</param>
    private sealed record Layers(ModelParts Model, int[,] SkinAt, int[,] ClothAt, int ClothStride)
    {
        /// <summary>The skin vertex straight under cloth vertex [i, j].</summary>
        public int SkinUnder(int i, int j) => SkinAt[i * ClothStride, j * ClothStride];
    }

    /// <summary>
    /// An n×n skin sheet in y = 0, 10 mm apart, centred on x = 0 when <paramref name="centred"/>, and a cloth sheet over
    /// the same ground at <paramref name="gap"/> (negative: under the skin), its vertices every
    /// <paramref name="clothStride"/> skin cells. Separate vertices: the cloth is not welded to the skin.
    /// </summary>
    private static Layers Layered(int n, float gap, string skinMaterial = Bibo, int clothStride = 1, bool centred = false,
                                  Func<int, int, bool>? skinCut = null, Func<int, int, float>? clothGap = null)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        float x0 = centred ? -(n - 1) * Spacing / 2f : 0f;

        var skinAt = new int[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            skinAt[i, j] = pos.Count / 3;
            pos.AddRange([x0 + i * Spacing, 0f, j * Spacing]);
            nrm.AddRange([0f, 1f, 0f]);
        }

        int m = (n - 1) / clothStride + 1;
        var clothAt = new int[m, m];
        for (int i = 0; i < m; i++)
        for (int j = 0; j < m; j++)
        {
            clothAt[i, j] = pos.Count / 3;
            float g = clothGap?.Invoke(i, j) ?? gap;
            pos.AddRange([x0 + i * clothStride * Spacing, g, j * clothStride * Spacing]);
            nrm.AddRange([0f, 1f, 0f]);
        }

        var skinTris = new List<int>();
        for (int i = 0; i + 1 < n; i++)
        for (int j = 0; j + 1 < n; j++)
        {
            if (skinCut != null && skinCut(i, j)) continue;
            int a = skinAt[i, j], b = skinAt[i + 1, j], c = skinAt[i, j + 1], d = skinAt[i + 1, j + 1];
            skinTris.AddRange([a, d, b, a, c, d]);
        }
        var clothTris = new List<int>();
        for (int i = 0; i + 1 < m; i++)
        for (int j = 0; j + 1 < m; j++)
        {
            int a = clothAt[i, j], b = clothAt[i + 1, j], c = clothAt[i, j + 1], d = clothAt[i + 1, j + 1];
            clothTris.AddRange([a, d, b, a, c, d]);
        }

        return new Layers(Assemble(pos, nrm, clothTris, skinTris.ToArray(), skinMaterial), skinAt, clothAt, clothStride);
    }

    /// <summary>An n×n skin-only sheet — a body model — with its middle raised <paramref name="bump"/>.</summary>
    private static (ModelParts Model, int[,] Index) SkinSheet(int n, float bump = 0f, string material = Bibo)
    {
        var (grid, index) = Grid(n);
        var pos = grid.Positions.ToArray();
        if (bump != 0f) pos[index[n / 2, n / 2] * 3 + 1] = bump;
        var model = new ModelParts
        {
            Positions = pos,
            Normals = grid.Normals,
            MeshSpans = grid.MeshSpans,
            Parts = [Part(0, 0, "1.1", grid.Parts[0].Triangles, material)],
            AttributeNames = [],
            Min = Vector3.Zero,
            Max = Vector3.One,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
        return (model, index);
    }

    /// <summary>A solve with "Move skin too" on and its clothes bound, as the panel leaves it.</summary>
    private static MeshVolumeSolve SkinSolve(ModelParts model, bool on = true)
    {
        var solve = new MeshVolumeSolve(model) { MoveSkin = on };
        Assert.True(solve.AttachFollow(SkinFollow.Bind(solve.FollowInputs)));
        return solve;
    }

    /// <summary>The model as the solve has it now — positions and normals — for <see cref="SkinThroughCloth.Find"/>.</summary>
    private static ModelParts Deformed(ModelParts model, MeshVolumeSolve solve)
    {
        int vc = model.Positions.Length / 3;
        var nrm = new float[vc * 3];
        for (int v = 0; v < vc; v++)
        {
            var n = solve.NormalAt(v);
            nrm[v * 3] = n.X; nrm[v * 3 + 1] = n.Y; nrm[v * 3 + 2] = n.Z;
        }
        return new ModelParts
        {
            Positions = solve.Positions().ToArray(),
            Normals = nrm,
            MeshSpans = model.MeshSpans,
            Parts = model.Parts,
            AttributeNames = model.AttributeNames,
            Min = model.Min,
            Max = model.Max,
            ShatteredSubmeshes = model.ShatteredSubmeshes,
        };
    }

    /// <summary>Skin points standing outside the cloth over them, as the game would show them.</summary>
    private static int SkinThrough(ModelParts model, MeshVolumeSolve solve)
        => SkinThroughCloth.Find(Deformed(model, solve)).Outside.Count;

    /// <summary>Triangles of the model that turned over against the author's.</summary>
    private static int Folded(ModelParts model, float[] after)
    {
        int folded = 0;
        foreach (var part in model.Parts.Where(p => p.Island < 0))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                var was = Vector3.Cross(At(model.Positions, b) - At(model.Positions, a), At(model.Positions, c) - At(model.Positions, a));
                var now = Vector3.Cross(At(after, b) - At(after, a), At(after, c) - At(after, a));
                if (Vector3.Dot(was, now) <= 0f) folded++;
            }
        return folded;
    }

    private static Vector3 Moved(ModelParts model, float[] after, int vertex) => At(after, vertex) - At(model.Positions, vertex);

    // ── AC-1: off is today ─────────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> Operations => new() { "paint", "relax", "smooth", "grab", "move", "rotate", "scale" };

    /// <summary>One operation of every kind over the middle of a <see cref="Layered"/> model, reaching the skin.</summary>
    private static void Operate(MeshVolumeSolve solve, Layers l, string op)
    {
        var centre = At(l.Model.Positions, l.SkinAt[10, 10]);
        var cloth = l.Model.Parts[0].Triangles;
        switch (op)
        {
            case "paint": solve.Paint(centre, 0.06f, 0.004f); solve.EndStroke(); break;
            case "relax": for (int d = 0; d < 5; d++) solve.Relax(centre, 0.06f, 1f); solve.EndStroke(); break;
            case "smooth": for (int d = 0; d < 5; d++) solve.Smooth(centre, 0.06f, 1f); solve.EndStroke(); break;
            case "grab":
                solve.BeginGrab(centre, 0.06f);
                solve.GrabTo(new Vector3(0f, 0.01f, 0.005f));
                solve.EndGrab();
                break;
            default:
                Assert.True(solve.BeginMove(cloth, adjacent: true, falloffRadius: 0.05f) > 0);
                if (op == "move") solve.MoveTo(new Vector3(0f, 0.01f, 0f));
                else if (op == "rotate") solve.TransformTo(Matrix4x4.CreateRotationX(0.3f, centre));
                else solve.TransformTo(Matrix4x4.CreateScale(1.2f, 1.5f, 1.2f, centre));
                solve.EndMove();
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    [Trait("Plan", "AC-studio-move-skin-1")]
    public void SkinStaysPutWithMoveSkinOff(string op)
    {
        var l = Layered(21, 0.005f);
        var solve = new MeshVolumeSolve(l.Model);
        Operate(solve, l, op);

        var after = solve.Positions();
        foreach (int v in l.SkinAt) Assert.Equal(At(l.Model.Positions, v), At(after, v));
        Assert.False(solve.SkinEdited);
    }

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-1")]
    [Trait("Plan", "EDGE-studio-move-skin-13")]
    public void MoveSkinDefaultsOffAndBindsNothing()
    {
        var l = Layered(11, 0.005f);
        var solve = new MeshVolumeSolve(l.Model);
        Assert.False(solve.MoveSkin);
        solve.Paint(At(l.Model.Positions, l.ClothAt[5, 5]), 0.04f, 0.002f);
        solve.EndStroke();
        Assert.False(solve.FollowReady);
    }

    /// <summary>
    /// A binding attached and the checkbox then off — the panel binds once and keeps it — leaves every brush exactly as
    /// one that never had a binding: every rider rule is gated on the operation having begun with skin moving.
    /// </summary>
    [Theory]
    [MemberData(nameof(Operations))]
    [Trait("Plan", "AC-studio-move-skin-1")]
    public void AttachedThenOffMatchesNeverAttached(string op)
    {
        var l = Layered(21, 0.005f);
        var plain = new MeshVolumeSolve(l.Model);
        var bound = SkinSolve(l.Model, on: false);

        Operate(plain, l, op);
        Operate(bound, l, op);

        Assert.Equal(plain.Positions(), bound.Positions());
        for (int v = 0; v < l.Model.Positions.Length / 3; v++) Assert.Equal(plain.NormalAt(v), bound.NormalAt(v));
    }

    // ── AC-2: the brushes move skin ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(SkinMaterials))]
    [Trait("Plan", "AC-studio-move-skin-2")]
    public void InflateMovesSkinWhenOn(string material)
    {
        Assert.True(SecondSkinWriter.IsBodySkinMaterial(material));
        var l = Layered(21, 0.005f, material);
        var solve = SkinSolve(l.Model);
        int mid = l.SkinAt[10, 10];

        Assert.True(solve.Paint(At(l.Model.Positions, mid), 0.05f, 0.004f) > 0);
        Assert.Equal(0.004f, Moved(l.Model, solve.Positions(), mid).Y, 1e-6f);
        solve.EndStroke();
        Assert.True(Moved(l.Model, solve.Positions(), mid).Y > 0.003f);
        Assert.True(solve.SkinEdited);
    }

    /// <summary>Relax and smooth pull a skin lump IN — skin is never held above itself.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Plan", "AC-studio-move-skin-2")]
    public void RelaxAndSmoothPullASkinLumpInWhenOn(bool relax)
    {
        var (model, index) = SkinSheet(21, bump: 0.006f);
        var solve = SkinSolve(model);
        int mid = index[10, 10];
        var centre = At(model.Positions, mid);
        for (int d = 0; d < 10; d++)
            if (relax) solve.Relax(centre, 0.04f, 1f);
            else solve.Smooth(centre, 0.04f, 1f);
        solve.EndStroke();
        Assert.True(At(solve.Positions(), mid).Y < 0.006f - 0.001f, $"the lump stayed at {At(solve.Positions(), mid).Y}");
    }

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-2")]
    public void GrabDragsSkinWhenOn()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        int mid = l.SkinAt[10, 10];
        Assert.True(solve.BeginGrab(At(l.Model.Positions, mid), 0.05f) > 0);
        solve.GrabTo(new Vector3(0f, 0.01f, 0f));
        Assert.True(solve.EndGrab());
        Assert.True(Moved(l.Model, solve.Positions(), mid).Y > 0.009f);
        Assert.Equal(0, Folded(l.Model, solve.Positions()));
    }

    // ── AC-3: the Move tools move skin ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-3")]
    public void MoveRotateScaleASkinPartWhenOn()
    {
        var l = Layered(11, 0.005f);
        var skinTris = l.Model.Parts[1].Triangles;
        var pivot = At(l.Model.Positions, l.SkinAt[5, 5]);

        var off = new MeshVolumeSolve(l.Model);
        Assert.Equal(0, off.BeginMove(skinTris, adjacent: false, falloffRadius: 0f));

        var solve = SkinSolve(l.Model);
        Assert.True(solve.BeginMove(skinTris, adjacent: false, falloffRadius: 0f) > 0);
        solve.MoveTo(new Vector3(0.002f, 0.01f, 0f));
        Assert.True(solve.EndMove());
        foreach (int v in l.SkinAt) Assert.Equal(0.01f, Moved(l.Model, solve.Positions(), v).Y, 1e-6f);

        var turned = SkinSolve(l.Model);
        var spin = Matrix4x4.CreateRotationZ(0.4f, pivot);
        turned.BeginMove(skinTris, adjacent: false, falloffRadius: 0f);
        turned.TransformTo(spin);
        turned.EndMove();
        int corner = l.SkinAt[0, 0];
        Assert.True(Vector3.Distance(Vector3.Transform(At(l.Model.Positions, corner), spin), At(turned.Positions(), corner)) < 1e-5f);

        // One axis: the skin's normals are the stretch's exact ones.
        var stretched = SkinSolve(l.Model);
        stretched.BeginMove(skinTris, adjacent: false, falloffRadius: 0f);
        stretched.TransformTo(Matrix4x4.CreateScale(2f, 1f, 1f, pivot));
        stretched.EndMove();
        Assert.Equal(2f * (At(l.Model.Positions, corner).X - pivot.X) + pivot.X, At(stretched.Positions(), corner).X, 1e-5f);
    }

    // ── AC-4: cloth follows ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    public void ClothWithin10mmFollowsSkinFully()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.06f, 0.008f);
        solve.EndStroke();

        var after = solve.Positions();
        for (int i = 0; i < 21; i++)
        for (int j = 0; j < 21; j++)
        {
            var skinGo = Moved(l.Model, after, l.SkinAt[i, j]);
            var clothGo = Moved(l.Model, after, l.ClothAt[i, j]);
            Assert.True(Vector3.Distance(skinGo, clothGo) < 0.0002f, $"cloth [{i},{j}] went {clothGo}, its skin {skinGo}");
            Assert.True(At(after, l.ClothAt[i, j]).Y - At(after, l.SkinAt[i, j]).Y >= 0.001f);
        }
        Assert.True(Moved(l.Model, after, l.ClothAt[10, 10]).Y > 0.006f);
        Assert.Equal(0, SkinThrough(l.Model, solve));
    }

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    public void FollowFadesFrom10To30mm()
    {
        var l = Layered(21, 0.020f);
        var solve = SkinSolve(l.Model);
        // A brush in the skin's plane, too narrow to reach the cloth 20 mm over it itself.
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.019f, 0.004f);

        var after = solve.Positions();
        float skin = Moved(l.Model, after, l.SkinAt[10, 10]).Y;
        float cloth = Moved(l.Model, after, l.ClothAt[10, 10]).Y;
        Assert.Equal(SkinFollow.FollowWeight(0.020f), cloth / skin, 0.05f);
        Assert.Equal(MeshVolumeSolve.Falloff(0.5f), SkinFollow.FollowWeight(0.020f), 1e-6f);
    }

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    [Trait("Plan", "EDGE-studio-move-skin-23")]
    public void ClothBeyond30mmDoesNotFollow()
    {
        var l = Layered(21, 0.040f);
        var on = SkinSolve(l.Model);
        var off = new MeshVolumeSolve(l.Model);
        var centre = At(l.Model.Positions, l.SkinAt[10, 10]);
        foreach (var s in new[] { on, off })
        {
            s.Paint(centre, 0.06f, 0.004f);
            s.EndStroke();
        }

        var follow = SkinFollow.Bind(on.FollowInputs);
        Assert.Equal(0, follow.RiderCount);
        // Brushed as cloth, by its own weight unscaled: as it is with the checkbox off.
        foreach (int v in l.ClothAt) Assert.Equal(At(off.Positions(), v), At(on.Positions(), v));
    }

    /// <summary>Cloth welded onto the skin is skin's own node, so it goes wherever the skin goes.</summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    public void WeldedClothMovesWithSkin()
    {
        var (grid, index) = Grid(21);
        // The middle rows are skin; the rest cloth, welded to it along the shared rows.
        var all = grid.Parts[0].Triangles;
        var cloth = new List<int>();
        var skin = new List<int>();
        for (int t = 0; t + 2 < all.Length; t += 3)
        {
            bool mid = new[] { all[t], all[t + 1], all[t + 2] }.All(v => At(grid.Positions, v).X is > 0.045f and < 0.155f);
            (mid ? skin : cloth).AddRange([all[t], all[t + 1], all[t + 2]]);
        }
        var model = Assemble(grid.Positions.ToList(), grid.Normals.ToList(), cloth, skin.ToArray(), Bibo);
        var solve = SkinSolve(model);
        solve.BeginMove(skin, adjacent: false, falloffRadius: 0f);
        solve.MoveTo(new Vector3(0f, 0.01f, 0f));
        solve.EndMove();

        // [5, j] is a corner the skin and the cloth beside it share.
        Assert.Equal(0.01f, Moved(model, solve.Positions(), index[5, 10]).Y, 1e-6f);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("rotate")]
    [InlineData("scale")]
    [Trait("Plan", "AC-studio-move-skin-4")]
    public void MoveRotateScaleCarryClothLyingOnSkin(string op)
    {
        var l = Layered(11, 0.005f);
        var solve = SkinSolve(l.Model);
        var pivot = At(l.Model.Positions, l.SkinAt[5, 5]);
        var m = op switch
        {
            "move" => Matrix4x4.CreateTranslation(0f, 0.01f, 0f),
            "rotate" => Matrix4x4.CreateRotationX(0.3f, pivot),
            _ => Matrix4x4.CreateScale(1.3f, 1f, 1.3f, pivot),
        };
        solve.BeginMove(l.Model.Parts[1].Triangles, adjacent: false, falloffRadius: 0f);
        if (op == "move") solve.MoveTo(new Vector3(0f, 0.01f, 0f));
        else solve.TransformTo(m);
        solve.EndMove();

        // The cloth went as the skin did: by the same transform, through its own place.
        foreach (int v in l.ClothAt)
        {
            var expected = Vector3.Transform(At(l.Model.Positions, v), m);
            Assert.True(Vector3.Distance(expected, At(solve.Positions(), v)) < 1e-4f, $"cloth {v} did not follow");
        }
        Assert.Equal(0, Folded(l.Model, solve.Positions()));
    }

    /// <summary>A rider the brush carried has its normal turned with the skin under it, and that normal is written.</summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    public void CarriedRiderNormalTurns()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.06f, 0.012f);
        solve.EndStroke();

        // On the bump's flank the cloth now leans.
        var flank = solve.NormalAt(l.ClothAt[10, 13]);
        Assert.True(MathF.Abs(flank.Z) > 0.05f, $"the rider's normal stayed {flank}");
    }

    /// <summary>Riders keep their authored clearance over the moved skin: neither sinking into it nor floating off it.</summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    public void RiderClearanceKeptBothWaysAfterSettle()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.06f, 0.006f);
        solve.EndStroke();

        var after = solve.Positions();
        foreach (var (i, j) in from i in Enumerable.Range(0, 21) from j in Enumerable.Range(0, 21) select (i, j))
            Assert.Equal(0.005f, At(after, l.ClothAt[i, j]).Y - At(after, l.SkinAt[i, j]).Y, 0.0005f);
    }

    // ── AC-7: undo ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("paint")]
    [InlineData("move")]
    [InlineData("grab")]
    [Trait("Plan", "AC-studio-move-skin-7")]
    [Trait("Plan", "EDGE-studio-move-skin-11")]
    public void UndoTakesBackSkinAndFollowingClothExactly(string op)
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        // An earlier cloth stroke, so undo must go back to it rather than to the author's.
        solve.Paint(At(l.Model.Positions, l.ClothAt[4, 4]), 0.03f, 0.002f);
        solve.EndStroke();
        var before = solve.Positions().ToArray();

        var centre = At(l.Model.Positions, l.SkinAt[10, 10]);
        if (op == "paint") { solve.Paint(centre, 0.06f, 0.006f); solve.EndStroke(); }
        else if (op == "grab") { solve.BeginGrab(centre, 0.05f); solve.GrabTo(new Vector3(0f, 0.01f, 0f)); solve.EndGrab(); }
        else { solve.BeginMove(l.Model.Parts[1].Triangles, false, 0f); solve.MoveTo(new Vector3(0f, 0.01f, 0f)); solve.EndMove(); }
        Assert.NotEqual(before, solve.Positions());

        solve.Undo();
        Assert.Equal(before, solve.Positions());

        solve.Reset();
        Assert.Equal(l.Model.Positions, solve.Positions());
        Assert.False(solve.SkinEdited);
    }

    // ── AC-9: the skin's open edges ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-9")]
    public void BrushLeavesTheSkinsOpenEdgeInPlace()
    {
        var (model, index) = SkinSheet(21);
        var solve = SkinSolve(model);
        solve.Paint(At(model.Positions, index[10, 10]), 1f, 0.002f);
        var after = solve.Positions();

        // The same sheet as cloth, which no edge holds: what the stroke gives each point without the fade.
        var (clothModel, _) = SkinSheet(21, material: "/mt_test.mtrl");
        var cloth = new MeshVolumeSolve(clothModel);
        cloth.Paint(At(model.Positions, index[10, 10]), 1f, 0.002f);
        var free = cloth.Positions();

        for (int k = 0; k < 21; k++)
        {
            Assert.Equal(0f, At(after, index[0, k]).Y);
            Assert.Equal(0f, At(after, index[k, 20]).Y);
        }
        // Past 30 mm in: the whole stroke.
        for (int i = 4; i <= 16; i++) Assert.Equal(At(free, index[i, 10]).Y, At(after, index[i, 10]).Y, 1e-7f);
        float Share(int i) => At(after, index[i, 10]).Y / At(free, index[i, 10]).Y;
        for (int i = 0; i < 4; i++) Assert.True(Share(i) <= Share(i + 1));
    }

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-9")]
    public void MoveToolMovesTheOpenEdgeToo()
    {
        var (model, index) = SkinSheet(11);
        var solve = SkinSolve(model);
        solve.BeginMove(model.Parts[0].Triangles, adjacent: false, falloffRadius: 0f);
        solve.MoveTo(new Vector3(0f, 0.005f, 0f));
        solve.EndMove();
        Assert.Equal(0.005f, At(solve.Positions(), index[0, 0]).Y, 1e-6f);
    }

    /// <summary>Skin cut away under the cloth leaves a rim, which holds; the cloth over it is brushed as cloth.</summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-9")]
    [Trait("Plan", "EDGE-studio-move-skin-27")]
    public void ClothOverACutOutRimStillBrushes()
    {
        static bool Hole(int i, int j) => i is >= 7 and < 13 && j is >= 7 and < 13;
        var l = Layered(21, 0.003f, skinCut: Hole);
        var on = SkinSolve(l.Model);
        var off = new MeshVolumeSolve(l.Model);
        var centre = At(l.Model.Positions, l.ClothAt[10, 10]);
        on.Paint(centre, 0.05f, 0.004f);
        off.Paint(centre, 0.05f, 0.004f);

        // Over the rim of the hole.
        int rim = l.ClothAt[7, 10];
        float with = Moved(l.Model, on.Positions(), rim).Y, without = Moved(l.Model, off.Positions(), rim).Y;
        Assert.True(without > 0f);
        Assert.True(with >= 0.9f * without, $"cloth over the rim went {with}, {without} with the checkbox off");
    }

    /// <summary>
    /// Cloth draped over a lump of skin near an open edge, relaxed with skin moving: it is brushed by its own weight
    /// there (the edge holds its skin), and is still held above the skin, where that skin now is.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-9")]
    public void RelaxAtTheWaistEdgeDoesNotSinkCloth()
    {
        const int n = 21;
        float Lump(int i, int j) => 0.008f * MathF.Exp(-((i - 2) * (i - 2) + (j - 10) * (j - 10)) / 4f);
        var l = Layered(n, 0f, clothGap: (i, j) => Lump(i, j) + 0.002f);
        // Raise the skin the same: rebuild its positions with the lump.
        var pos = l.Model.Positions.ToArray();
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            pos[l.SkinAt[i, j] * 3 + 1] = Lump(i, j);
        var model = new ModelParts
        {
            Positions = pos, Normals = l.Model.Normals, MeshSpans = l.Model.MeshSpans, Parts = l.Model.Parts,
            AttributeNames = [], Min = Vector3.Zero, Max = Vector3.One, ShatteredSubmeshes = new Dictionary<string, int>(),
        };

        var solve = SkinSolve(model);
        var centre = At(model.Positions, l.ClothAt[2, 10]);
        for (int d = 0; d < 20; d++) solve.Relax(centre, 0.04f, 1f);
        solve.EndStroke();

        var after = solve.Positions();
        for (int i = 0; i < 6; i++)
        for (int j = 6; j < 15; j++)
            Assert.True(At(after, l.ClothAt[i, j]).Y - At(after, l.SkinAt[i, j]).Y > 0f, $"cloth [{i},{j}] sank into the skin");
    }

    // ── edge cases ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-1")]
    public void TurningMoveSkinOffKeepsTheMovedSkin()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.BeginMove(l.Model.Parts[1].Triangles, false, 0f);
        solve.MoveTo(new Vector3(0f, 0.004f, 0f));
        solve.EndMove();

        solve.MoveSkin = false;
        solve.Paint(At(l.Model.Positions, l.ClothAt[10, 10]), 0.03f, 0.003f);
        solve.EndStroke();
        foreach (int v in l.SkinAt) Assert.Equal(0.004f, Moved(l.Model, solve.Positions(), v).Y, 1e-6f);
        solve.Undo();
        foreach (int v in l.SkinAt) Assert.Equal(0.004f, Moved(l.Model, solve.Positions(), v).Y, 1e-6f);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-2")]
    public void ChangingMoveSkinMidStrokeTakesEffectNextStroke()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model, on: false);
        var centre = At(l.Model.Positions, l.SkinAt[10, 10]);
        solve.Paint(centre, 0.05f, 0.002f);
        solve.MoveSkin = true;
        solve.Paint(centre, 0.05f, 0.002f);
        solve.EndStroke();
        Assert.False(solve.SkinEdited);

        solve.Paint(centre, 0.05f, 0.002f);
        solve.EndStroke();
        Assert.True(solve.SkinEdited);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    [Trait("Plan", "EDGE-studio-move-skin-3")]
    public void MoveSkinOnAModelWithNoSkinMatchesOff(string op)
    {
        var l = Layered(21, 0.005f, skinMaterial: "/mt_c0201e6255_top_b.mtrl");
        var on = SkinSolve(l.Model);
        var off = new MeshVolumeSolve(l.Model);
        Assert.Equal(0, SkinFollow.Bind(on.FollowInputs).RiderCount);
        Operate(on, l, op);
        Operate(off, l, op);
        Assert.Equal(off.Positions(), on.Positions());
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-4")]
    public void AnAllSkinModelSculptsWithMoveSkinOn()
    {
        var (model, index) = SkinSheet(21);
        var solve = SkinSolve(model);
        Assert.Equal(0, SkinFollow.Bind(solve.FollowInputs).RiderCount);
        solve.Paint(At(model.Positions, index[10, 10]), 0.05f, 0.003f);
        solve.EndStroke();
        Assert.True(At(solve.Positions(), index[10, 10]).Y > 0.002f);
        Assert.Equal(0f, At(solve.Positions(), index[0, 10]).Y);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-5")]
    public void LockedSkinStaysAndClothOverItStillBrushes()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.SetLocked(l.Model.Parts[1].Triangles);
        solve.Paint(At(l.Model.Positions, l.ClothAt[10, 10]), 0.04f, 0.003f);
        solve.EndStroke();
        foreach (int v in l.SkinAt) Assert.Equal(Vector3.Zero, Moved(l.Model, solve.Positions(), v));
        Assert.True(Moved(l.Model, solve.Positions(), l.ClothAt[10, 10]).Y > 0.002f);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-6")]
    public void LockedClothDoesNotFollow()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.SetLocked(l.Model.Parts[0].Triangles);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.05f, 0.003f);
        solve.EndStroke();
        Assert.True(Moved(l.Model, solve.Positions(), l.SkinAt[10, 10]).Y > 0.002f);
        foreach (int v in l.ClothAt) Assert.Equal(Vector3.Zero, Moved(l.Model, solve.Positions(), v));
    }

    /// <summary>Cloth the author left a little under the skin rides it and keeps its clip — not fixed behind their back.</summary>
    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-8")]
    public void ClothClippedUnderTheSkinKeepsItsClip()
    {
        var l = Layered(21, -0.003f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.06f, 0.004f);
        solve.EndStroke();
        var after = solve.Positions();
        Assert.True(Moved(l.Model, after, l.ClothAt[10, 10]).Y > 0.003f, "the clipped cloth did not ride");
        Assert.Equal(-0.003f, At(after, l.ClothAt[10, 10]).Y - At(after, l.SkinAt[10, 10]).Y, 0.0005f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Plan", "EDGE-studio-move-skin-9")]
    public void BridgeAndWindNeverMoveSkinEvenWhenOn(bool bridge)
    {
        var l = Layered(21, 0.005f);
        var on = SkinSolve(l.Model);
        var off = new MeshVolumeSolve(l.Model);
        var centre = At(l.Model.Positions, l.ClothAt[10, 10]);
        foreach (var s in new[] { on, off })
        {
            if (bridge) s.Bridge(centre, 0.05f, 1f, Vector3.UnitY);
            else s.PaintWind(centre, 0.05f, 1f, 1f);
            s.EndStroke();
        }
        Assert.False(on.SkinEdited);
        Assert.Equal(off.Positions(), on.Positions());
        for (int v = 0; v < l.Model.Positions.Length / 3; v++) Assert.Equal(off.WindAt(v), on.WindAt(v));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-10")]
    public void DeflatePullsSkinInAndClothFollows()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.06f, -0.006f);
        solve.EndStroke();
        var after = solve.Positions();
        Assert.True(Moved(l.Model, after, l.SkinAt[10, 10]).Y < -0.004f);
        Assert.Equal(0.005f, At(after, l.ClothAt[10, 10]).Y - At(after, l.SkinAt[10, 10]).Y, 0.0005f);
        Assert.Equal(0, SkinThrough(l.Model, solve));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-14")]
    public void NoFoldsOrSkinThroughAfterSkinInflateUnderTightCloth()
    {
        var l = Layered(21, 0.001f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.04f, 0.006f);
        solve.EndStroke();
        Assert.Equal(0, Folded(l.Model, solve.Positions()));
        Assert.Equal(0, SkinThrough(l.Model, solve));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-17")]
    public void MirroredSkinInflateIsSymmetric()
    {
        var l = Layered(21, 0.005f, centred: true);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[14, 10]), 0.03f, 0.004f, mirror: true);
        solve.EndStroke();
        var after = solve.Positions();
        Assert.Equal(At(after, l.SkinAt[14, 10]).Y, At(after, l.SkinAt[6, 10]).Y, 1e-6f);
        Assert.Equal(At(after, l.ClothAt[14, 10]).Y, At(after, l.ClothAt[6, 10]).Y, 1e-6f);
        Assert.True(At(after, l.ClothAt[6, 10]).Y > 0.006f);
    }

    /// <summary>Cloth rising from 5 mm to 35 mm over the skin — fully, partly and not bound — grabbed with the skin
    /// under it: no tear between neighbours where the binding fades.</summary>
    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-20")]
    public void GrabOverPartlyBoundClothIsContinuous()
    {
        var l = Layered(21, 0f, clothGap: (i, _) => 0.005f + i * 0.0015f);
        var solve = SkinSolve(l.Model);
        solve.BeginGrab(At(l.Model.Positions, l.SkinAt[10, 10]), 0.12f);
        solve.GrabTo(new Vector3(0f, 0.01f, 0f));
        solve.EndGrab();
        var after = solve.Positions();

        float worstSkin = 0f, worstCloth = 0f;
        for (int i = 0; i + 1 < 21; i++)
        {
            worstSkin = MathF.Max(worstSkin, MathF.Abs(Moved(l.Model, after, l.SkinAt[i + 1, 10]).Y - Moved(l.Model, after, l.SkinAt[i, 10]).Y));
            worstCloth = MathF.Max(worstCloth, MathF.Abs(Moved(l.Model, after, l.ClothAt[i + 1, 10]).Y - Moved(l.Model, after, l.ClothAt[i, 10]).Y));
        }
        Assert.True(worstCloth <= 1.5f * worstSkin + 1e-4f, $"cloth step {worstCloth}, skin step {worstSkin}");
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-21")]
    public void FalloffFromClothCarriesSkinAndItsRiders()
    {
        var l = Layered(21, 0.005f);
        var on = SkinSolve(l.Model);
        var off = new MeshVolumeSolve(l.Model);
        int[] seeds = [l.ClothAt[10, 10]];
        var withSkin = on.MoveFalloff(seeds, 0.03f, alongSurface: false)!;
        var without = off.MoveFalloff(seeds, 0.03f, alongSurface: false)!;
        Assert.True(withSkin[l.SkinAt[10, 10]] > 0f);
        Assert.Equal(0f, without[l.SkinAt[10, 10]]);
        for (int v = 0; v < withSkin.Length; v++) Assert.True(withSkin[v] >= without[v]);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-24")]
    public void SkinHeldUntilFollowAttached()
    {
        var l = Layered(11, 0.005f);
        var solve = new MeshVolumeSolve(l.Model) { MoveSkin = true };
        solve.Paint(At(l.Model.Positions, l.SkinAt[5, 5]), 0.04f, 0.003f);
        solve.EndStroke();
        Assert.False(solve.SkinEdited);

        Assert.True(solve.AttachFollow(SkinFollow.Bind(solve.FollowInputs)));
        solve.Paint(At(l.Model.Positions, l.SkinAt[5, 5]), 0.04f, 0.003f);
        solve.EndStroke();
        Assert.True(solve.SkinEdited);
    }

    /// <summary>
    /// A binding that arrives while a stroke, grab or move is open changes nothing about that operation: it began
    /// without skin, and finishes without it, exactly as a solve that never had one. The next operation moves skin.
    /// </summary>
    [Theory]
    [InlineData("paint")]
    [InlineData("grab")]
    [InlineData("move")]
    [Trait("Plan", "EDGE-studio-move-skin-2")]
    [Trait("Plan", "EDGE-studio-move-skin-24")]
    public void ABindingArrivingMidOperationWaitsForTheNext(string op)
    {
        var l = Layered(21, 0.005f);
        var never = new MeshVolumeSolve(l.Model) { MoveSkin = true };
        var late = new MeshVolumeSolve(l.Model) { MoveSkin = true };
        var binding = SkinFollow.Bind(late.FollowInputs);
        var centre = At(l.Model.Positions, l.SkinAt[10, 10]);

        foreach (var s in new[] { never, late })
        {
            bool attach = ReferenceEquals(s, late);
            if (op == "paint")
            {
                s.Paint(centre, 0.05f, 0.002f);
                if (attach) Assert.True(s.AttachFollow(binding));
                s.Paint(centre, 0.05f, 0.002f);
                s.EndStroke();
            }
            else if (op == "grab")
            {
                s.BeginGrab(centre, 0.05f);
                if (attach) Assert.True(s.AttachFollow(binding));
                s.GrabTo(new Vector3(0f, 0.01f, 0f));
                s.EndGrab();
            }
            else
            {
                s.BeginMove(l.Model.Parts[0].Triangles, adjacent: true, falloffRadius: 0.03f);
                if (attach) Assert.True(s.AttachFollow(binding));
                s.MoveTo(new Vector3(0f, 0.01f, 0f));
                s.EndMove();
            }
        }
        Assert.Equal(never.Positions(), late.Positions());
        Assert.False(late.SkinEdited);

        late.Paint(centre, 0.05f, 0.002f);
        late.EndStroke();
        Assert.True(late.SkinEdited);
    }

    /// <summary>Cloth over locked skin is brushed AND settled as cloth: the stroke ends exactly as with the checkbox off.</summary>
    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-5")]
    public void ClothOverLockedSkinSettlesAsWithTheCheckboxOff()
    {
        var l = Layered(21, 0.005f);
        var on = SkinSolve(l.Model);
        var off = new MeshVolumeSolve(l.Model);
        foreach (var s in new[] { on, off })
        {
            s.SetLocked(l.Model.Parts[1].Triangles);
            s.Paint(At(l.Model.Positions, l.ClothAt[10, 10]), 0.04f, 0.003f);
            s.EndStroke();
        }
        Assert.Equal(off.Positions(), on.Positions());
    }

    /// <summary>
    /// Cloth the author left a hair under the skin (0.5 mm — a slight clip, not cloth inside the body): the clip is
    /// theirs to keep, but a sculpt pushing the skin through further is guarded like any other.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    [Trait("Plan", "EDGE-studio-move-skin-8")]
    public void ASlightClipIsKeptButNotGrown()
    {
        var l = Layered(21, -0.0005f, clothStride: 2);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[11, 11]), 0.03f, 0.006f);
        solve.EndStroke();
        var after = solve.Positions();

        // Every skin point stands no further outside the cloth over it than the author's 0.5 mm: measured on the cloth's
        // FACES, between its vertices, where the peak of a 6 mm push would otherwise stand 2.5 mm through.
        Assert.Equal(0, SkinPoke.MadeWorse(l.Model, after));
        // Straight down through the cloth face over each skin point: the face's height there, between its corners.
        var cloth = l.Model.Parts[0].Triangles;
        int checks = 0;
        float peak = float.NaN;
        foreach (int s in l.SkinAt)
        {
            var p = At(after, s);
            for (int t = 0; t + 2 < cloth.Length; t += 3)
            {
                var a = At(after, cloth[t]); var b = At(after, cloth[t + 1]); var c = At(after, cloth[t + 2]);
                // Barycentrics of the skin point's XZ in the face's XZ, edges included.
                float det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                if (MathF.Abs(det) < 1e-12f) continue;
                float u = ((b.Z - c.Z) * (p.X - c.X) + (c.X - b.X) * (p.Z - c.Z)) / det;
                float v = ((c.Z - a.Z) * (p.X - c.X) + (a.X - c.X) * (p.Z - c.Z)) / det;
                float w = 1f - u - v;
                if (u < -1e-4f || v < -1e-4f || w < -1e-4f) continue;
                float over = u * a.Y + v * b.Y + w * c.Y;
                checks++;
                if (s == l.SkinAt[11, 11]) peak = p.Y - over;
                Assert.True(p.Y - over <= 0.0005f + SkinThroughCloth.Tolerance + 1e-5f,
                            $"skin {s} stands {(p.Y - over) * 1000f:F2} mm through the cloth, the author's 0.5");
                break;
            }
        }
        Assert.True(checks >= 300, $"only {checks} skin points were measured");
        Assert.False(float.IsNaN(peak), "the peak was not measured");
        Assert.Equal(0, Folded(l.Model, after));
    }

    /// <summary>
    /// What the guard cannot lift — cloth locked over skin pushed through it — is counted after every operation, so the
    /// panel can log it; with the cloth free, the same stroke leaves none.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    [Trait("Plan", "EDGE-studio-move-skin-6")]
    public void WhatTheGuardLeavesIsCountedForTheLog()
    {
        var l = Layered(21, 0.001f);
        var locked = SkinSolve(l.Model);
        locked.SetLocked(l.Model.Parts[0].Triangles);
        int before = locked.GuardVersion;
        locked.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.04f, 0.004f);
        locked.EndStroke();
        Assert.Equal(before + 1, locked.GuardVersion);
        Assert.True(locked.SkinStillOutside > 0, "skin through locked cloth went uncounted");

        var free = SkinSolve(l.Model);
        int freeBefore = free.GuardVersion;
        free.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.04f, 0.004f);
        free.EndStroke();
        Assert.Equal(freeBefore + 1, free.GuardVersion);
        Assert.Equal(0, free.SkinStillOutside);
    }

    /// <summary>
    /// The guard lifts riders of the skin it moved, not every bound cloth point it recorded: cloth the user deflated onto
    /// skin that stayed (an open edge holds it) keeps where they put it.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    public void ClothDeflatedOntoStillSkinIsNotLifted()
    {
        var l = Layered(21, 0.001f);
        var solve = SkinSolve(l.Model, on: false);
        // The user deflates the cloth over the first rows into the skin, with the checkbox off.
        solve.Paint(At(l.Model.Positions, l.ClothAt[2, 10]), 0.025f, -0.003f);
        solve.EndStroke();
        var placed = solve.Positions().ToArray();
        Assert.True(placed[l.ClothAt[2, 10] * 3 + 1] < 0f, "the deflate should have reached under the skin");

        // Then sculpts the skin out nearby, with it on: the guard looks over still skin near what this stroke moved,
        // and leaves the cloth the earlier stroke placed where it is — the boundary rows included.
        solve.MoveSkin = true;
        solve.Paint(At(l.Model.Positions, l.SkinAt[9, 10]), 0.04f, 0.003f);
        Assert.True(solve.SkinEdited);
        solve.EndStroke();
        foreach (int i in new[] { 0, 1, 2, 3, 4 })
            Assert.Equal(placed[l.ClothAt[i, 10] * 3 + 1], At(solve.Positions(), l.ClothAt[i, 10]).Y);
    }

    /// <summary>
    /// Still skin the moved cloth passes over is judged too: a row of skin locked between moving rows, under coarse
    /// cloth whose corners ride the moving rows down — the face over the locked row comes to cross it, and is lifted.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    [Trait("Plan", "EDGE-studio-move-skin-5")]
    public void StillSkinUnderMovedClothIsJudged()
    {
        var l = Layered(21, 0.001f, clothStride: 2);
        var solve = SkinSolve(l.Model);
        solve.SetLocked(Enumerable.Range(0, 21).Select(j => l.SkinAt[11, j]));
        solve.Paint(At(l.Model.Positions, l.SkinAt[11, 10]), 0.05f, -0.004f);
        solve.EndStroke();
        Assert.Equal(0f, At(solve.Positions(), l.SkinAt[11, 10]).Y);
        Assert.Equal(0, SkinPoke.MadeWorse(l.Model, solve.Positions()));
        Assert.Equal(0, Folded(l.Model, solve.Positions()));
    }

    /// <summary>
    /// A mirrored Move of skin moves the picked skin on its own side, with the cloth on it, and nothing on the other
    /// side: skin has no mirror match, and its reflection must not take the cloth lying on the other side's skin.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-3")]
    [Trait("Plan", "EDGE-studio-move-skin-17")]
    public void AMirroredSkinMoveLeavesTheOtherSideAlone()
    {
        var l = Layered(21, 0.001f, centred: true);
        var solve = SkinSolve(l.Model);
        var picked = l.Model.Parts[1].Triangles.Where(v => At(l.Model.Positions, v).X > 0.03f).ToArray();
        var pivot = At(l.Model.Positions, l.SkinAt[15, 10]);
        Assert.True(solve.BeginMove(picked, adjacent: false, falloffRadius: 0f,
                                    mirror: new MeshVolumeSolve.MoveMirror(MeshVolumeSolve.MoveKind.Translate, pivot)) > 0);
        solve.MoveTo(new Vector3(0f, 0.005f, 0f));
        solve.EndMove();

        var after = solve.Positions();
        Assert.Equal(0.005f, Moved(l.Model, after, l.SkinAt[15, 10]).Y, 1e-6f);
        Assert.Equal(0.005f, Moved(l.Model, after, l.ClothAt[15, 10]).Y, 1e-4f);
        for (int i = 0; i < 7; i++)
        for (int j = 0; j < 21; j++)
        {
            Assert.Equal(Vector3.Zero, Moved(l.Model, after, l.ClothAt[i, j]));
            Assert.Equal(Vector3.Zero, Moved(l.Model, after, l.SkinAt[i, j]));
        }
        Assert.Equal(0, Folded(l.Model, after));
    }

    /// <summary>
    /// How far cloth rode its skin is the Move tools' kind of displacement: the brush limit still allows the cloth's
    /// own strokes their whole reach on top of it.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    public void RiddenDistanceDoesNotEatTheBrushLimit()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.MaxDisplacement = MeshVolumeSolve.MinMaxDisplacement;   // 10 mm
        // The skin, and the cloth on it, 8 mm out.
        solve.BeginMove(l.Model.Parts[1].Triangles, adjacent: false, falloffRadius: 0f);
        solve.MoveTo(new Vector3(0f, 0.008f, 0f));
        solve.EndMove();
        int cloth = l.ClothAt[10, 10];
        Assert.Equal(0.008f, Moved(l.Model, solve.Positions(), cloth).Y, 1e-5f);

        // Then the cloth alone, pushed well past the limit: it stops 10 mm beyond where it rode to, not 2 mm.
        solve.SetLocked(l.Model.Parts[1].Triangles);
        for (int d = 0; d < 20; d++) solve.Paint(At(solve.Positions(), cloth), 0.03f, 0.002f);
        Assert.Equal(0.018f, Moved(l.Model, solve.Positions(), cloth).Y, 1e-4f);
        solve.EndStroke();
    }

    /// <summary>
    /// Skin and cloth picked together, mirrored. The cloth is mirrored as ever — here a strip across the centre, its own
    /// mirror, which keeps only the drag's symmetric part — while the picked skin, and cloth riding it that was not
    /// picked, take the drag itself on their own side. The other side's skin stays.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-3")]
    [Trait("Plan", "EDGE-studio-move-skin-17")]
    public void AMirroredMoveOfSkinWithACentredClothStrip()
    {
        var l = Layered(21, 0.001f, centred: true);
        var solve = SkinSolve(l.Model);
        var at = l.Model.Positions;
        var cloth = l.Model.Parts[0].Triangles;
        var strip = Enumerable.Range(0, cloth.Length / 3)
                              .Where(t => Enumerable.Range(0, 3).All(k => MathF.Abs(At(at, cloth[t * 3 + k]).X) <= 0.0201f))
                              .SelectMany(t => new[] { cloth[t * 3], cloth[t * 3 + 1], cloth[t * 3 + 2] });
        var skinSide = l.Model.Parts[1].Triangles.Where(v => At(at, v).X > 0.045f);
        var pivot = At(at, l.SkinAt[10, 10]);
        Assert.True(solve.BeginMove(strip.Concat(skinSide), adjacent: false, falloffRadius: 0f,
                                    mirror: new MeshVolumeSolve.MoveMirror(MeshVolumeSolve.MoveKind.Translate, pivot)) > 0);
        Assert.Equal(MeshVolumeSolve.MirrorMode.Centred, solve.DragMirror?.Mode);
        var offset = new Vector3(0.002f, 0.004f, 0f);
        solve.MoveTo(offset);
        solve.EndMove();

        var after = solve.Positions();
        // The strip: the symmetric part, X dropped.
        Assert.True(Vector3.Distance(MirrorMotion.CentredOffset(offset), Moved(l.Model, after, l.ClothAt[10, 5])) < 1e-5f);
        // The picked skin, and the cloth riding it that was not picked: the drag itself.
        Assert.True(Vector3.Distance(offset, Moved(l.Model, after, l.SkinAt[16, 5])) < 1e-5f);
        Assert.True(Vector3.Distance(offset, Moved(l.Model, after, l.ClothAt[16, 5])) < 1e-4f);
        // The other side's skin, unpicked, stays.
        Assert.Equal(Vector3.Zero, Moved(l.Model, after, l.SkinAt[4, 5]));
        Assert.Equal(0, Folded(l.Model, after));
    }

    /// <summary>
    /// The same, with cloth on one side whose mirror match is on the other: that cloth and its match move as mirror
    /// images, while the picked skin takes the drag on its own side and the other side's skin stays.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-3")]
    [Trait("Plan", "EDGE-studio-move-skin-17")]
    public void AMirroredMoveOfSkinWithClothOnOneSide()
    {
        var l = Layered(21, 0.001f, centred: true);
        var solve = SkinSolve(l.Model);
        var at = l.Model.Positions;
        var cloth = l.Model.Parts[0].Triangles;
        var patch = Enumerable.Range(0, cloth.Length / 3)
                              .Where(t => Enumerable.Range(0, 3).All(k => At(at, cloth[t * 3 + k]).X is >= 0.0399f and <= 0.0801f))
                              .SelectMany(t => new[] { cloth[t * 3], cloth[t * 3 + 1], cloth[t * 3 + 2] });
        var skinSide = l.Model.Parts[1].Triangles.Where(v => At(at, v).X > 0.045f);
        var pivot = At(at, l.SkinAt[16, 10]);
        Assert.True(solve.BeginMove(patch.Concat(skinSide), adjacent: false, falloffRadius: 0f,
                                    mirror: new MeshVolumeSolve.MoveMirror(MeshVolumeSolve.MoveKind.Translate, pivot)) > 0);
        Assert.Equal(MeshVolumeSolve.MirrorMode.Sides, solve.DragMirror?.Mode);
        var offset = new Vector3(0.002f, 0.004f, 0f);
        solve.MoveTo(offset);
        solve.EndMove();

        var after = solve.Positions();
        // The patch on its own side, and its match across the centre as the mirror image.
        Assert.True(Vector3.Distance(offset, Moved(l.Model, after, l.ClothAt[16, 5])) < 1e-4f);
        Assert.True(Vector3.Distance(MirrorMotion.Reflect(offset), Moved(l.Model, after, l.ClothAt[4, 5])) < 1e-4f);
        // The picked skin: the drag itself. The other side's skin stays.
        Assert.True(Vector3.Distance(offset, Moved(l.Model, after, l.SkinAt[16, 5])) < 1e-5f);
        Assert.Equal(Vector3.Zero, Moved(l.Model, after, l.SkinAt[4, 5]));
        Assert.Equal(0, Folded(l.Model, after));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-24")]
    public void AttachFollowIgnoresAnotherSolvesBinding()
    {
        var l = Layered(11, 0.005f);
        var first = new MeshVolumeSolve(l.Model);
        var second = new MeshVolumeSolve(l.Model);
        Assert.False(second.AttachFollow(SkinFollow.Bind(first.FollowInputs)));
        Assert.False(second.FollowReady);
    }

    // ── AC-8: other sizes ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A copy of <paramref name="l"/>'s model with every vertex shifted — another size, a little off.</summary>
    private static ModelParts Shifted(Layers l, Func<Vector3, Vector3> by)
    {
        var pos = l.Model.Positions.ToArray();
        for (int v = 0; v < pos.Length / 3; v++)
        {
            var p = by(At(pos, v));
            pos[v * 3] = p.X; pos[v * 3 + 1] = p.Y; pos[v * 3 + 2] = p.Z;
        }
        return new ModelParts
        {
            Positions = pos, Normals = l.Model.Normals, MeshSpans = l.Model.MeshSpans, Parts = l.Model.Parts,
            AttributeNames = [], Min = Vector3.Zero, Max = Vector3.One, ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-8")]
    public void TransferCarriesASkinMoveToTheOtherSizesSkin()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.06f, 0.006f);
        solve.EndStroke();

        var target = Shifted(l, p => p * 1.02f);
        var other = BrushTransfer.Transfer(solve, l.Model, target);
        var after = other.Positions();
        Assert.True(Moved(target, after, l.SkinAt[10, 10]).Y > 0.004f, "the other size's skin did not take the move");
        Assert.True(At(after, l.ClothAt[10, 10]).Y - At(after, l.SkinAt[10, 10]).Y >= 0.001f);
        Assert.Equal(0, Folded(target, after));
    }

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-8")]
    public void TransferWithoutASkinMoveLeavesTargetSkinAlone()
    {
        var l = Layered(21, 0.005f);
        var solve = new MeshVolumeSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.ClothAt[10, 10]), 0.06f, 0.004f);
        solve.EndStroke();
        var target = Shifted(l, p => p * 1.02f);
        var other = BrushTransfer.Transfer(solve, l.Model, target);
        Assert.False(other.SkinEdited);
        foreach (int v in l.SkinAt) Assert.Equal(At(target.Positions, v), At(other.Positions(), v));
    }

    /// <summary>The other size's skin edge sits 8 mm off the brushed one's: its own edge holds, not the brushed one's.</summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-8")]
    public void TransferHoldsTheTargetsOwnOpenEdge()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 1f, 0.004f);
        solve.EndStroke();

        // The target's edge row lands inside the brushed size, where its skin moved.
        var target = Shifted(l, p => p with { X = p.X + 0.008f });
        var other = BrushTransfer.Transfer(solve, l.Model, target);
        for (int j = 0; j < 21; j++) Assert.Equal(0f, Moved(target, other.Positions(), l.SkinAt[0, j]).Y, 1e-6f);
        Assert.True(Moved(target, other.Positions(), l.SkinAt[10, 10]).Y > 0.003f);
    }

    /// <summary>The target's seam fade is for its skin only: a hem edited near the waist carries over unchanged.</summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-8")]
    public void HemEditNearTheWaistTransfersUnchanged()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.06f, 0.004f);   // skin moved: SkinMoved is set
        solve.EndStroke();
        solve.SetLocked(l.Model.Parts[1].Triangles);
        solve.Paint(At(l.Model.Positions, l.ClothAt[0, 3]), 0.02f, 0.003f);     // the hem, on the skin's open edge
        solve.EndStroke();

        var other = BrushTransfer.Transfer(solve, l.Model, l.Model);
        int hem = l.ClothAt[0, 3];
        Assert.Equal(solve.DeltaAt(hem).Y, other.DeltaAt(hem).Y, 1e-5f);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-15")]
    public void TransferOntoASizeWithNoSkin()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 0.06f, 0.006f);
        solve.EndStroke();

        var clothOnly = Layered(21, 0.005f, skinMaterial: "/mt_c0201e6255_top_b.mtrl");
        var other = BrushTransfer.Transfer(solve, l.Model, clothOnly.Model);
        Assert.True(Moved(clothOnly.Model, other.Positions(), l.ClothAt[10, 10]).Y > 0.004f);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-16")]
    public void TransferSkinBeyondReachKeepsNoEdit()
    {
        var l = Layered(21, 0.005f);
        var solve = SkinSolve(l.Model);
        solve.Paint(At(l.Model.Positions, l.SkinAt[10, 10]), 1f, 0.004f);
        solve.EndStroke();
        var far = Shifted(l, p => p with { Y = p.Y - 0.5f });
        var other = BrushTransfer.Transfer(solve, l.Model, far);
        foreach (int v in l.SkinAt) Assert.Equal(At(far.Positions, v), At(other.Positions(), v));
    }

    /// <summary>
    /// Cloth twice as coarse as the skin under it, a millimetre over it: skin pushed out between the cloth's vertices
    /// pokes through the faces, which no vertex check sees. The face guard lifts them. Undo is still exact.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-25")]
    [Trait("Plan", "EDGE-studio-move-skin-11")]
    public void CoarseClothFaceGuardLeavesNoSkinThrough()
    {
        var l = Layered(21, 0.001f, clothStride: 2);
        var solve = SkinSolve(l.Model);
        Assert.Equal(0, SkinThrough(l.Model, solve));
        solve.Paint(At(l.Model.Positions, l.SkinAt[11, 11]), 0.03f, 0.006f);
        solve.EndStroke();
        Assert.Equal(0, SkinThrough(l.Model, solve));
        Assert.Equal(0, Folded(l.Model, solve.Positions()));

        solve.Undo();
        Assert.Equal(l.Model.Positions, solve.Positions());
    }
}
