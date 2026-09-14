using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The brush's geometry, against a mesh built here rather than a <c>.mdl</c>.
/// <para/>
/// <see cref="SyntheticModel"/> deliberately emits isolated triangles that share corner POSITIONS and no
/// indices, which is the right fixture for the island split and the wrong one here: its triangles share no
/// edges, so there is no neighbourhood for the slope limit or the normals to work over. A displacement pass
/// needs a connected surface, so these build one — <see cref="ModelParts"/> is
/// public and its members are init-only, so the solve can be fed directly without a file in the way.
/// </summary>
public class MeshVolumeSolveTests
{
    private const float Spacing = 0.01f;    // 10 mm, so a 20 mm brush reaches a handful of nodes

    /// <summary>
    /// A flat <paramref name="n"/>×<paramref name="n"/> grid in the XZ plane, normals along +Y.
    /// <para/>
    /// Triangulated so every interior edge is used by two faces and only the perimeter is a boundary. That
    /// is the property the rim pin is tested against, and getting the diagonal wrong would silently make the
    /// whole grid a rim.
    /// </summary>
    private static (ModelParts Model, int[,] Index) Grid(int n)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var index = new int[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            index[i, j] = pos.Count / 3;
            pos.Add(i * Spacing); pos.Add(0f); pos.Add(j * Spacing);
            nrm.Add(0f); nrm.Add(1f); nrm.Add(0f);
        }

        var tris = new List<int>();
        for (int i = 0; i + 1 < n; i++)
        for (int j = 0; j + 1 < n; j++)
        {
            int a = index[i, j], b = index[i + 1, j], c = index[i, j + 1], d = index[i + 1, j + 1];
            tris.AddRange([a, b, d]);
            tris.AddRange([a, d, c]);
        }

        return (Assemble(pos, nrm, tris), index);
    }

    private static ModelParts Assemble(List<float> pos, List<float> nrm, List<int> tris,
                                       int[]? secondPart = null, string secondMaterial = "/mt_test.mtrl")
    {
        var parts = new List<ModelPart> { Part(0, 0, "1.1", tris.ToArray()) };
        if (secondPart != null) parts.Add(Part(0, 1, "1.2", secondPart, secondMaterial));

        return new ModelParts
        {
            Positions = pos.ToArray(),
            Normals = nrm.ToArray(),
            MeshSpans = [new MeshSpan(0, 0, pos.Count / 3)],
            Parts = parts,
            AttributeNames = [],
            Min = Vector3.Zero,
            Max = Vector3.One,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    private static ModelPart Part(int mesh, int submesh, string label, int[] tris,
                                  string material = "/mt_test.mtrl") => new()
    {
        Mesh = mesh,
        Submesh = submesh,
        Island = -1,
        Label = label,
        Material = material,
        Triangles = tris,
        Ordinals = [.. Enumerable.Range(0, tris.Length / 3)],
        AttributeMask = 0,
        Toggleable = true,
        Min = Vector3.Zero,
        Max = Vector3.One,
    };

    private static Vector3 At(float[] positions, int vertex)
        => new(positions[vertex * 3], positions[vertex * 3 + 1], positions[vertex * 3 + 2]);

    /// <summary>
    /// The middle of the brush moves by the full strength, and a point half way out moves by the falloff —
    /// so the shape of the soft selection is the shape of the edit, not an approximation of it.
    /// <para/>
    /// Measured before the stroke ends on purpose. The settle passes are allowed to pull a displacement
    /// back, so asserting exact values after them would be asserting the slope limiter's output rather than
    /// the brush's.
    /// </summary>
    [Fact]
    public void PaintsStrengthAtTheCentreAndTheFalloffFurtherOut()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);

        int middle = index[5, 5];
        var centre = At(model.Positions, middle);

        const float radius = 0.04f, strength = 0.001f;
        Assert.True(solve.Paint(centre, radius, strength) > 0);

        var after = solve.Positions();

        // Dead centre: falloff is 1, so the whole strength lands, straight up the +Y normal.
        Assert.Equal(strength, At(after, middle).Y, 1e-6f);
        Assert.Equal(centre.X, At(after, middle).X, 1e-6f);
        Assert.Equal(centre.Z, At(after, middle).Z, 1e-6f);

        // Two cells out is 20 mm of a 40 mm radius — exactly half way, where smoothstep gives 0.5.
        int half = index[5, 7];
        float expected = strength * MeshVolumeSolve.Falloff(0.5f);
        Assert.Equal(expected, At(after, half).Y, 1e-6f);

        // And nothing outside the radius moved at all.
        Assert.Equal(0f, At(after, index[0, 0]).Y, 1e-9f);
    }

    /// <summary>
    /// Open edges move like everything else.
    /// <para/>
    /// They used to be pinned, to keep a seam shared with another model file from tearing — but a garment's
    /// hem, neckline and sleeve ends are open edges too, and pinning them made the brush refuse to pull cloth
    /// away anywhere near one. This holds the brush to moving them.
    /// </summary>
    [Fact]
    public void MovesOpenEdges()
    {
        var (model, index) = Grid(9);
        var solve = new MeshVolumeSolve(model);

        // Wide enough to cover the whole grid at full weight near the corner it is centred on.
        solve.Paint(At(model.Positions, index[0, 0]), 1f, 0.002f);
        solve.EndStroke();

        var after = solve.Positions();
        for (int k = 0; k < 9; k++)
        {
            Assert.True(At(after, index[0, k]).Y > 0f, $"edge node [0,{k}] did not move");
            Assert.True(At(after, index[k, 0]).Y > 0f, $"edge node [{k},0] did not move");
        }
    }

    /// <summary>
    /// Two vertices at the same position move by the same amount, so a UV seam does not crack open.
    /// <para/>
    /// This is the failure that makes welding non-negotiable rather than tidy: a garment duplicates vertices
    /// along every seam and every hard crease, so the same point on the surface is several vertices with
    /// different neighbours. Displace one copy and not the other and the mesh splits along the seam.
    /// </summary>
    [Fact]
    public void MovesBothCopiesOfASeamVertexTogether()
    {
        var (model, index) = Grid(11);

        // Duplicate one interior vertex and hand the copy to a single triangle, which is precisely what an
        // exporter does at a seam: same place, different index, different set of faces.
        var pos = model.Positions.ToList();
        var nrm = model.Normals.ToList();
        int original = index[5, 5];
        int copy = pos.Count / 3;
        pos.AddRange([model.Positions[original * 3], model.Positions[original * 3 + 1],
                      model.Positions[original * 3 + 2]]);
        nrm.AddRange([0f, 1f, 0f]);

        var tris = model.Parts[0].Triangles.ToArray();
        for (int t = 0; t < tris.Length; t++)
            if (tris[t] == original) { tris[t] = copy; break; }

        var seamed = Assemble(pos, nrm, tris.ToList());
        var solve = new MeshVolumeSolve(seamed);

        solve.Paint(At(seamed.Positions, original), 0.04f, 0.001f);
        solve.EndStroke();

        var after = solve.Positions();
        Assert.Equal(At(after, original).X, At(after, copy).X, 1e-9f);
        Assert.Equal(At(after, original).Y, At(after, copy).Y, 1e-9f);
        Assert.Equal(At(after, original).Z, At(after, copy).Z, 1e-9f);
        Assert.True(At(after, original).Y > 0f);
    }

    /// <summary>
    /// Skin never moves, locked or not — the brush is for pushing clothing clear of the body, and a garment
    /// model routinely carries the body underneath it. Cloth right beside it still moves.
    /// </summary>
    [Fact]
    public void NeverMovesSkin()
    {
        var (model, index) = Grid(11);

        // Every triangle touching the middle node becomes a body-skin part; the rest stays cloth.
        var all = model.Parts[0].Triangles;
        var cloth = new List<int>();
        var body = new List<int>();
        int mid = index[5, 5];
        for (int t = 0; t + 2 < all.Length; t += 3)
        {
            var target = all[t] == mid || all[t + 1] == mid || all[t + 2] == mid ? body : cloth;
            target.AddRange([all[t], all[t + 1], all[t + 2]]);
        }

        var split = Assemble(model.Positions.ToList(), model.Normals.ToList(), cloth, body.ToArray(),
                             "/mt_c0201b0001_b.mtrl");
        Assert.True(SecondSkinWriter.IsBodySkinMaterial("/mt_c0201b0001_b.mtrl"));
        var solve = new MeshVolumeSolve(split);

        solve.Paint(At(split.Positions, mid), 0.04f, 0.001f);
        solve.EndStroke();

        var after = solve.Positions();
        Assert.Equal(Vector3.Zero, At(after, mid) - At(split.Positions, mid));

        // In any direction: cloth beside skin in the same plane is pulled away from it sideways, not up.
        var moved = At(after, index[5, 8]) - At(split.Positions, index[5, 8]);
        Assert.True(moved.Length() > 0f, "cloth inside the brush should still have moved");
    }

    /// <summary>
    /// A double-sided surface — the way hair is built: every card drawn twice on the same positions, the back
    /// copy wound the other way with normals pointing down. Welded, each point's two normals cancel, so the
    /// brush has no direction of its own there; it pushes along the viewer instead. And the rebuilt normals
    /// must keep each side facing its own way, or one side of the hair shades inside out after any edit.
    /// </summary>
    [Fact]
    public void ADoubleSidedSurfacePushesAlongTheViewerAndKeepsBothFacings()
    {
        const int n = 9;
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        for (int side = 0; side < 2; side++)
        {
            int start = pos.Count / 3;
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                pos.AddRange([i * Spacing, 0f, j * Spacing]);
                nrm.AddRange([0f, side == 0 ? 1f : -1f, 0f]);
            }
            for (int i = 0; i + 1 < n; i++)
            for (int j = 0; j + 1 < n; j++)
            {
                int a = start + i * n + j, b = start + (i + 1) * n + j;
                int c = start + i * n + j + 1, d = start + (i + 1) * n + j + 1;
                if (side == 0) tris.AddRange([a, b, d, a, d, c]);
                else tris.AddRange([a, d, b, a, c, d]);
            }
        }
        var model = Assemble(pos, nrm, tris);
        var solve = new MeshVolumeSolve(model);
        int front = 4 * n + 4, back = n * n + 4 * n + 4;

        // No direction of its own and no viewer: nothing to push along, so nothing moves.
        Assert.Equal(0, solve.Paint(At(model.Positions, front), 0.03f, -0.002f));

        // Pushed in, seen from above: away from the viewer, both copies together.
        Assert.True(solve.Paint(At(model.Positions, front), 0.03f, -0.002f, Vector3.UnitY) > 0);
        solve.EndStroke();
        var after = solve.Positions();
        Assert.True(At(after, front).Y < -0.001f, $"the surface did not push in: {At(after, front).Y * 1000f:F2} mm");
        Assert.Equal(At(after, front), At(after, back));

        Assert.True(solve.NormalAt(front).Y > 0.5f, $"the front copy should still face up: {solve.NormalAt(front)}");
        Assert.True(solve.NormalAt(back).Y < -0.5f, $"the back copy should still face down: {solve.NormalAt(back)}");
    }

    /// <summary>
    /// A cloth dome over a flat skin sheet, its crest 10 mm up and its rim 15 mm INTO the skin (clipping, as
    /// garments do), with the skin as part of the model or not. Deep enough that relaxing it freely takes the
    /// crest below the floor, so keeping it up is the floor's doing and not the shape's.
    /// </summary>
    private static (ModelParts Model, int ClothStart, int N) DomeOverSkin(bool withSkin)
    {
        const int n = 13;
        var pos = new List<float>();
        var nrm = new List<float>();
        var cloth = new List<int>();
        var body = new List<int>();

        int Sheet(Func<int, int, float> height, List<int> tris)
        {
            int start = pos.Count / 3;
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                pos.AddRange([i * Spacing, height(i, j), j * Spacing]);
                nrm.AddRange([0f, 1f, 0f]);
            }
            for (int i = 0; i + 1 < n; i++)
            for (int j = 0; j + 1 < n; j++)
            {
                int a = start + i * n + j, b = start + (i + 1) * n + j;
                int c = start + i * n + j + 1, d = start + (i + 1) * n + j + 1;
                tris.AddRange([a, b, d, a, d, c]);
            }
            return start;
        }

        if (withSkin) Sheet((_, _) => 0f, body);
        int clothStart = Sheet((i, j) =>
        {
            float u = (i - 6) / 6f, v = (j - 6) / 6f;
            return 0.025f * MathF.Max(0f, 1f - u * u - v * v) - 0.015f;
        }, cloth);

        var model = withSkin ? Assemble(pos, nrm, cloth, body.ToArray(), "/mt_c0201b0001_bibo.mtrl")
                             : Assemble(pos, nrm, cloth);
        return (model, clothStart, n);
    }

    /// <summary>
    /// Relax shrinks cloth toward the body — but not into skin the model carries. Every cloth point that started
    /// above the skin stays at least the 1 mm floor above it however long the brush is held, while the rim the
    /// author already had clipping is held where it was rather than pushed out.
    /// </summary>
    [Fact]
    public void RelaxKeepsClothAboveSkinTheModelCarries()
    {
        var (model, clothStart, n) = DomeOverSkin(withSkin: true);
        var solve = new MeshVolumeSolve(model);
        var centre = At(model.Positions, clothStart + 6 * n + 6);

        for (int d = 0; d < 300; d++) solve.Relax(centre, 0.2f, 1f);
        var during = solve.Positions().ToArray();
        solve.EndStroke();
        var after = solve.Positions();

        for (int v = clothStart; v < clothStart + n * n; v++)
        {
            float rest = At(model.Positions, v).Y;
            if (rest < 0.001f) continue;
            Assert.True(At(during, v).Y >= 0.001f - 1e-5f, $"vertex {v} sank into the skin while painting: {At(during, v).Y * 1000f:F2} mm");
            Assert.True(At(after, v).Y >= 0.0005f, $"vertex {v} sank into the skin on release: {At(after, v).Y * 1000f:F2} mm");
        }
    }

    /// <summary>The same dome with no skin in the model has nothing to stop on, and sinks — the brush is still a
    /// shrinking relax wherever the body is not known.</summary>
    [Fact]
    public void RelaxSinksWhereTheModelHasNoSkin()
    {
        var (model, clothStart, n) = DomeOverSkin(withSkin: false);
        var solve = new MeshVolumeSolve(model);
        int crest = clothStart + 6 * n + 6;

        for (int d = 0; d < 300; d++) solve.Relax(At(model.Positions, crest), 0.2f, 1f);
        Assert.True(At(solve.Positions(), crest).Y < 0.0005f, $"the crest stayed up at {At(solve.Positions(), crest).Y * 1000f:F2} mm");
    }

    /// <summary>With the skin in the model, the same stroke leaves the crest on the floor.</summary>
    [Fact]
    public void RelaxStopsTheCrestOnTheSkinFloor()
    {
        var (model, clothStart, n) = DomeOverSkin(withSkin: true);
        var solve = new MeshVolumeSolve(model);
        int crest = clothStart + 6 * n + 6;

        for (int d = 0; d < 300; d++) solve.Relax(At(model.Positions, crest), 0.2f, 1f);
        float y = At(solve.Positions(), crest).Y;
        Assert.True(y >= 0.001f - 1e-5f && y < 0.003f, $"the crest should rest on the 1 mm floor, is at {y * 1000f:F2} mm");
    }

    /// <summary>
    /// Cloth pulls AWAY FROM THE SKIN, not along its own normal.
    /// <para/>
    /// The case from game: the hem of a pair of shorts has normals pointing down the leg, and pulling along
    /// them slid the hem down instead of lifting it off the thigh. Here the cloth sits 10 mm above a skin
    /// sheet with its normals deliberately pointing sideways; it must move up, away from the skin.
    /// <para/>
    /// And it must still be there after the stroke ends — the slope limit that used to run on release pulled
    /// most of a pull back, which read in game as the cloth snapping back when the button came up.
    /// </summary>
    [Fact]
    public void PullsAwayFromSkinNotAlongTheNormalAndKeepsItOnRelease()
    {
        const int n = 9;
        var pos = new List<float>();
        var nrm = new List<float>();
        var cloth = new List<int>();
        var body = new List<int>();

        // Two sheets over the same XZ square: skin at y = 0, cloth at y = 10 mm.
        int Sheet(float y, float nx, float ny, List<int> tris)
        {
            int start = pos.Count / 3;
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                pos.AddRange([i * Spacing, y, j * Spacing]);
                nrm.AddRange([nx, ny, 0f]);
            }
            for (int i = 0; i + 1 < n; i++)
            for (int j = 0; j + 1 < n; j++)
            {
                int a = start + i * n + j, b = start + (i + 1) * n + j;
                int c = start + i * n + j + 1, d = start + (i + 1) * n + j + 1;
                tris.AddRange([a, b, d, a, d, c]);
            }
            return start;
        }

        Sheet(0f, 0f, 1f, body);
        int clothStart = Sheet(0.01f, 1f, 0f, cloth);      // normals along +X: the "hem pointing the wrong way"

        var model = Assemble(pos, nrm, cloth, body.ToArray(), "/mt_c0201b0001_bibo.mtrl");
        var solve = new MeshVolumeSolve(model);

        int middle = clothStart + 4 * n + 4;
        solve.Paint(At(model.Positions, middle), 0.2f, 0.004f);
        var during = At(solve.Positions(), middle) - At(model.Positions, middle);
        solve.EndStroke();
        var after = At(solve.Positions(), middle) - At(model.Positions, middle);

        Assert.True(after.Y > 0.003f, $"cloth should lift off the skin, moved {after}");
        Assert.True(MathF.Abs(after.X) < after.Y * 0.2f, $"cloth followed its sideways normal: {after}");

        // Nothing meaningful taken back when the stroke ended. The light smoothing that runs on release may
        // nudge a point, but it is a Taubin pass that does not shrink — unlike the slope limit it replaced,
        // which took back most of the pull.
        Assert.True(after.Y > during.Y * 0.97f, $"the pull snapped back on release: {during.Y} -> {after.Y}");
    }

    /// <summary>A grid with one interior node raised, as a lump the garment arrived with.</summary>
    private static (ModelParts Model, int[,] Index, int Bump) Bumped(float height)
    {
        var (grid, index) = Grid(11);
        var pos = grid.Positions.ToArray();
        int bump = index[5, 5];
        pos[bump * 3 + 1] = height;
        return (Assemble(pos.ToList(), grid.Normals.ToList(), grid.Parts[0].Triangles.ToList()), index, bump);
    }

    /// <summary>Relax flattens a lump the model shipped with.</summary>
    [Fact]
    public void RelaxFlattensALump()
    {
        const float height = 0.01f;
        var (model, _, bump) = Bumped(height);
        var solve = new MeshVolumeSolve(model);

        for (int i = 0; i < 10; i++) solve.Relax(At(model.Positions, bump), 0.04f, 1f);
        solve.EndStroke();

        var after = solve.Positions();
        Assert.True(At(after, bump).Y < height * 0.8f, $"the lump did not flatten: {At(after, bump).Y}");
    }

    /// <summary>
    /// Relax shrinks, the way 3ds Max's does: relaxing the top of a dome sinks its crest. A Taubin pair
    /// would hold the crest where it is (or nudge it up), so this pins the plain-average behaviour.
    /// </summary>
    [Fact]
    public void RelaxShrinksADomeLikeMax()
    {
        const int n = 21;
        var (pos, nrm, tris, _) = Sheet(n, (i, j) =>
        {
            float x = (i - 10) * Spacing, z = (j - 10) * Spacing;
            return -2f * (x * x + z * z);            // a cap, highest in the middle
        }, 1f);
        var model = Assemble(pos, nrm, tris);
        var solve = new MeshVolumeSolve(model);
        int crest = 10 * n + 10;

        for (int d = 0; d < 20; d++) solve.Relax(At(model.Positions, crest), 0.08f, 1f);
        solve.EndStroke();

        float sank = -At(solve.Positions(), crest).Y;
        Assert.True(sank > 0.0005f, $"the crest sank only {sank * 1000f:F3} mm");
    }

    /// <summary>A pull that came out sharp is softened: the peak comes down toward the cloth around it.</summary>
    [Fact]
    public void RelaxSoftensASharpPull()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);
        int peak = index[5, 5], side = index[5, 6];

        // A narrow, strong pull — about one cell wide — leaves a spike.
        solve.Paint(At(model.Positions, peak), 0.012f, 0.004f);
        solve.EndStroke();
        // A COPY: Positions() refills and returns one buffer every call, so holding the array itself would
        // silently turn "before" into "after".
        var pulled = solve.Positions().ToArray();
        float stepBefore = At(pulled, peak).Y - At(pulled, side).Y;
        Assert.True(stepBefore > 0f);

        for (int i = 0; i < 10; i++) solve.Relax(At(model.Positions, peak), 0.04f, 1f);
        solve.EndStroke();
        var relaxed = solve.Positions();

        Assert.True(At(relaxed, peak).Y < At(pulled, peak).Y, "the peak did not come down");
        Assert.True(At(relaxed, peak).Y - At(relaxed, side).Y < stepBefore, "the pull did not soften");
    }

    /// <summary>Skin never moves under the relax brush either.</summary>
    [Fact]
    public void RelaxNeverMovesSkin()
    {
        var (model, _, bump) = Bumped(0.01f);
        var all = model.Parts[0].Triangles;
        var cloth = new List<int>();
        var body = new List<int>();
        for (int t = 0; t + 2 < all.Length; t += 3)
        {
            var target = all[t] == bump || all[t + 1] == bump || all[t + 2] == bump ? body : cloth;
            target.AddRange([all[t], all[t + 1], all[t + 2]]);
        }

        var split = Assemble(model.Positions.ToList(), model.Normals.ToList(), cloth, body.ToArray(),
                             "/mt_c0201b0001_b.mtrl");
        var solve = new MeshVolumeSolve(split);

        for (int i = 0; i < 10; i++) solve.Relax(At(split.Positions, bump), 0.04f, 1f);
        solve.EndStroke();

        Assert.Equal(At(split.Positions, bump), At(solve.Positions(), bump));
    }

    /// <summary>
    /// The wind brush moves wind toward the amount by the rate and the falloff — full in the middle, less toward
    /// the rim, nothing beyond it — and painting toward 0 erases. Nothing moves while it paints.
    /// </summary>
    [Fact]
    public void WindPaintsTowardTheAmountAndErases()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);
        int middle = index[5, 5], near = index[5, 7], outside = index[0, 0];
        var centre = At(model.Positions, middle);

        solve.PaintWind(centre, 0.04f, 1f, 0.5f);
        Assert.Equal(0.5f, solve.WindAt(middle), 1e-4f);
        Assert.True(solve.WindAt(near) > 0f && solve.WindAt(near) < 0.5f, $"near the rim: {solve.WindAt(near)}");
        Assert.Equal(0f, solve.WindAt(outside));

        for (int i = 0; i < 40; i++) solve.PaintWind(centre, 0.04f, 1f, 0.5f);
        Assert.Equal(1f, solve.WindAt(middle));   // settles exactly, not one byte short

        for (int i = 0; i < 40; i++) solve.PaintWind(centre, 0.04f, 0f, 0.5f);
        Assert.Equal(0f, solve.WindAt(middle));
        solve.EndStroke(wind: true);

        Assert.Equal(model.Positions, solve.Positions());
    }

    /// <summary>Wind is never painted onto skin — it is the garment that sways.</summary>
    [Fact]
    public void WindNeverPaintsSkin()
    {
        var (model, clothStart, n) = DomeOverSkin(withSkin: true);
        var solve = new MeshVolumeSolve(model);
        solve.PaintWind(At(model.Positions, clothStart + 6 * n + 6), 1f, 1f, 1f);

        for (int v = 0; v < clothStart; v++) Assert.Equal(0f, solve.WindAt(v));
        Assert.True(solve.WindAt(clothStart + 6 * n + 6) > 0.9f);
    }

    /// <summary>Undo takes a wind stroke back exactly, and leaves the model unedited.</summary>
    [Fact]
    public void WindUndoIsExact()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);
        solve.PaintWind(At(model.Positions, index[5, 5]), 0.05f, 0.7f, 1f);
        solve.EndStroke(wind: true);
        Assert.True(solve.WindEdited);
        Assert.True(solve.Dirty);

        solve.Undo();
        for (int v = 0; v < model.Positions.Length / 3; v++) Assert.Equal(0f, solve.WindAt(v));
        Assert.False(solve.WindEdited);
        Assert.False(solve.Dirty);
    }

    /// <summary>Undoing a relax stroke puts the surface back exactly.</summary>
    [Fact]
    public void RelaxUndoIsExact()
    {
        var (model, _, bump) = Bumped(0.01f);
        var solve = new MeshVolumeSolve(model);

        for (int i = 0; i < 10; i++) solve.Relax(At(model.Positions, bump), 0.04f, 1f);
        solve.EndStroke();
        Assert.True(solve.Dirty);

        solve.Undo();
        var back = solve.Positions();
        for (int v = 0; v < model.Positions.Length / 3; v++)
            Assert.Equal(At(model.Positions, v), At(back, v));
    }

    /// <summary>
    /// A sheet over an n×n grid shaped by <paramref name="height"/>, its normals all pointing
    /// <paramref name="normalY"/> — the way to build a groove, a dome, or the lining under an outer layer.
    /// </summary>
    private static (List<float> Pos, List<float> Nrm, List<int> Tris, int Start) Sheet(
        int n, Func<int, int, float> height, float normalY,
        List<float>? pos = null, List<float>? nrm = null, List<int>? tris = null)
    {
        pos ??= [];
        nrm ??= [];
        tris ??= [];
        int start = pos.Count / 3;
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            pos.AddRange([i * Spacing, height(i, j), j * Spacing]);
            nrm.AddRange([0f, normalY, 0f]);
        }
        for (int i = 0; i + 1 < n; i++)
        for (int j = 0; j + 1 < n; j++)
        {
            int a = start + i * n + j, b = start + (i + 1) * n + j;
            int c = start + i * n + j + 1, d = start + (i + 1) * n + j + 1;
            tris.AddRange([a, b, d, a, d, c]);
        }
        return (pos, nrm, tris, start);
    }

    /// <summary>A V groove 8 mm deep and 80 mm across, running the length of the sheet: the cleft.</summary>
    private static float Groove(int i, int j) => -0.008f * MathF.Max(0f, 1f - MathF.Abs(i - 10) / 4f);

    /// <summary>
    /// Bridge fills a groove between two flat sides, and it gets all the way: the relaxations tried on a
    /// cleavage stalled because a saddle's curvatures cancel, and the first bridge construction stalled because
    /// a point near the rim read the rim as the top of the cloth. This holds it to actually closing the gap.
    /// </summary>
    [Fact]
    public void BridgeSpansAGroove()
    {
        const int n = 21;
        var (pos, nrm, tris, _) = Sheet(n, Groove, 1f);
        var model = Assemble(pos, nrm, tris);
        var solve = new MeshVolumeSolve(model);

        int bottom = 10 * n + 10;
        for (int d = 0; d < 30; d++) solve.Bridge(At(model.Positions, bottom), 0.15f, 1f, Vector3.UnitY);
        solve.EndStroke(bridge: true);

        var after = solve.Positions();
        Assert.True(At(after, bottom).Y > -0.008f * 0.25f,
                    $"the groove was not spanned: bottom at {At(after, bottom).Y * 1000f:F2} mm of -8 mm");

        // Only ever lifts.
        for (int v = 0; v < model.Positions.Length / 3; v++)
            Assert.True(At(after, v).Y >= At(model.Positions, v).Y - 1e-6f, $"vertex {v} was lowered");
    }

    /// <summary>
    /// A bridge stays where it was painted when the button comes up. The stroke-end smoothing and unfold both
    /// suit a pull and both took a bridge back — the smoothing flattens a narrow band of lift, and the unfold
    /// reads a crack wall laid flat into the span as a collapsed triangle — which read in game as the cloth
    /// snapping back down into the crack.
    /// <para/>
    /// A deep, narrow groove, because that is the case that trips both.
    /// </summary>
    [Fact]
    public void BridgeDoesNotSnapBackOnRelease()
    {
        const int n = 21;
        var (pos, nrm, tris, _) = Sheet(n, (i, j) => i == 10 ? -0.03f : 0f, 1f);
        var model = Assemble(pos, nrm, tris);
        var solve = new MeshVolumeSolve(model);
        int bottom = 10 * n + 10;

        for (int d = 0; d < 30; d++) solve.Bridge(At(model.Positions, bottom), 0.15f, 1f, Vector3.UnitY);
        float lifted = At(solve.Positions(), bottom).Y - At(model.Positions, bottom).Y;
        Assert.True(lifted > 0.01f, $"the groove did not lift: {lifted * 1000f:F2} mm");

        solve.EndStroke(bridge: true);
        float kept = At(solve.Positions(), bottom).Y - At(model.Positions, bottom).Y;
        Assert.True(kept > lifted * 0.98f, $"the bridge snapped back on release: {lifted * 1000f:F2} -> {kept * 1000f:F2} mm");
    }

    /// <summary>
    /// A channel with VERTICAL walls, 60 mm deep and 20 mm across, run along Z: the profile steps along X at the
    /// rim, drops straight down, crosses the floor, and climbs straight back up. Seen from above a wall has no
    /// footprint, so bridging the channel squashes each wall to a sliver — the shape that used to read as a
    /// collapsed triangle to every later stroke.
    /// </summary>
    private static (ModelParts Model, int Floor) Channel()
    {
        const int cols = 21;
        var profile = new List<(float X, float Y)>();
        for (int k = 0; k <= 9; k++) profile.Add((k * Spacing, 0f));
        profile.Add((9 * Spacing, -0.06f));
        profile.Add((11 * Spacing, -0.06f));
        for (int k = 11; k <= 20; k++) profile.Add((k * Spacing, 0f));

        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        int rows = profile.Count;
        for (int r = 0; r < rows; r++)
        for (int j = 0; j < cols; j++)
        {
            pos.AddRange([profile[r].X, profile[r].Y, j * Spacing]);
            nrm.AddRange([0f, 1f, 0f]);
        }
        for (int r = 0; r + 1 < rows; r++)
        for (int j = 0; j + 1 < cols; j++)
        {
            int a = r * cols + j, b = (r + 1) * cols + j, c = r * cols + j + 1, d = (r + 1) * cols + j + 1;
            tris.AddRange([a, b, d, a, d, c]);
        }
        return (Assemble(pos, nrm, tris), 10 * cols + 10);
    }

    /// <summary>
    /// A bridge spans up to the rim and no higher. Beside a vertical wall the slope correction once read the rim
    /// as higher than it was, and dab after dab chased that phantom until the rim stood 97 mm up.
    /// </summary>
    private static void AssertNothingAboveTheRim(float[] positions)
    {
        for (int v = 0; v < positions.Length / 3; v++)
            Assert.True(At(positions, v).Y < 0.0005f, $"vertex {v} rose above the rim to {At(positions, v).Y * 1000f:F2} mm");
    }

    /// <summary>
    /// A later pull over a bridge leaves the bridge up. The pull's fold check used to judge every triangle
    /// against the AUTHOR'S shape and halve whole displacements, so the bridge's squashed walls read as
    /// collapsed and the pull's release dropped the span back into the channel.
    /// </summary>
    [Fact]
    public void APullOverABridgeDoesNotTakeItBack()
    {
        var (model, floor) = Channel();
        var solve = new MeshVolumeSolve(model);

        for (int d = 0; d < 30; d++) solve.Bridge(At(model.Positions, floor), 0.15f, 1f, Vector3.UnitY);
        solve.EndStroke(bridge: true);
        float bridged = At(solve.Positions(), floor).Y - At(model.Positions, floor).Y;
        Assert.True(bridged > 0.05f, $"the channel did not bridge: {bridged * 1000f:F2} mm");
        AssertNothingAboveTheRim(solve.Positions());

        solve.Paint(At(model.Positions, floor), 0.15f, 0.0002f);
        solve.EndStroke();
        float kept = At(solve.Positions(), floor).Y - At(model.Positions, floor).Y;
        Assert.True(kept > bridged * 0.98f, $"the pull took the bridge back: {bridged * 1000f:F2} -> {kept * 1000f:F2} mm");
    }

    /// <summary>
    /// Undo after a small pull beside a bridge puts every point back exactly — including the bridge's points
    /// just outside the pull, which the old fold check scaled back without recording them.
    /// </summary>
    [Fact]
    public void UndoIsExactAfterAPullBesideABridge()
    {
        var (model, floor) = Channel();
        var solve = new MeshVolumeSolve(model);

        for (int d = 0; d < 30; d++) solve.Bridge(At(model.Positions, floor), 0.15f, 1f, Vector3.UnitY);
        solve.EndStroke(bridge: true);
        var bridged = solve.Positions().ToArray();
        AssertNothingAboveTheRim(bridged);

        solve.Paint(At(model.Positions, floor), 0.025f, 0.002f);
        solve.EndStroke();
        solve.Undo();

        var back = solve.Positions();
        for (int v = 0; v < bridged.Length / 3; v++)
            Assert.Equal(At(bridged, v), At(back, v));
    }

    /// <summary>A rounded surface is already on its own hull, so bridging it changes nothing — each cheek keeps
    /// its curve.</summary>
    [Fact]
    public void BridgeLeavesADomeAlone()
    {
        const int n = 21;
        var (pos, nrm, tris, _) = Sheet(n, (i, j) =>
        {
            float x = (i - 10) * Spacing, z = (j - 10) * Spacing;
            return -2f * (x * x + z * z);            // a cap, highest in the middle
        }, 1f);
        var model = Assemble(pos, nrm, tris);
        var solve = new MeshVolumeSolve(model);

        for (int d = 0; d < 10; d++) solve.Bridge(At(model.Positions, 10 * n + 10), 0.15f, 1f, Vector3.UnitY);

        Assert.True(solve.Worst < 0.0002f, $"a convex surface moved {solve.Worst * 1000f:F3} mm");
    }

    /// <summary>
    /// A lining stays a lining. The outer layer spans the groove and the inward-facing layer 2 mm beneath it
    /// rises with it, rather than being pressed up into the outer surface.
    /// </summary>
    [Fact]
    public void BridgeMovesALiningWithTheOuterLayer()
    {
        const int n = 21;
        var outer = Sheet(n, Groove, 1f);
        var inner = Sheet(n, (i, j) => Groove(i, j) - 0.002f, -1f, outer.Pos, outer.Nrm, outer.Tris);
        var model = Assemble(inner.Pos, inner.Nrm, inner.Tris);
        var solve = new MeshVolumeSolve(model);

        int outerBottom = outer.Start + 10 * n + 10, innerBottom = inner.Start + 10 * n + 10;
        for (int d = 0; d < 30; d++) solve.Bridge(At(model.Positions, outerBottom), 0.15f, 1f, Vector3.UnitY);

        var after = solve.Positions();
        Assert.True(At(after, outerBottom).Y > -0.008f * 0.5f, "the outer layer did not span");
        float gap = At(after, outerBottom).Y - At(after, innerBottom).Y;
        Assert.InRange(gap, 0.0015f, 0.0025f);
    }

    /// <summary>Skin never moves under the bridge either.</summary>
    [Fact]
    public void BridgeNeverMovesSkin()
    {
        const int n = 21;
        var (pos, nrm, tris, _) = Sheet(n, Groove, 1f);
        int bottom = 10 * n + 10;
        var cloth = new List<int>();
        var body = new List<int>();
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            var target = tris[t] == bottom || tris[t + 1] == bottom || tris[t + 2] == bottom ? body : cloth;
            target.AddRange([tris[t], tris[t + 1], tris[t + 2]]);
        }
        var model = Assemble(pos, nrm, cloth, body.ToArray(), "/mt_c0201b0001_b.mtrl");
        var solve = new MeshVolumeSolve(model);

        for (int d = 0; d < 10; d++) solve.Bridge(At(model.Positions, bottom), 0.15f, 1f, Vector3.UnitY);

        Assert.Equal(At(model.Positions, bottom), At(solve.Positions(), bottom));
    }

    /// <summary>
    /// Holding the brush down cannot walk a vertex away without limit.
    /// <para/>
    /// A stroke applies a dab per frame, so on a fast machine a held button is hundreds of dabs a second —
    /// without a ceiling, a moment's inattention turns a garment into a balloon.
    /// </summary>
    [Fact]
    public void CapsTotalDisplacement()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);

        var centre = At(model.Positions, index[5, 5]);
        for (int i = 0; i < 500; i++) solve.Paint(centre, 0.04f, 0.001f);

        Assert.Equal(MeshVolumeSolve.GarmentMaxDisplacement, solve.MaxDisplacement);
        Assert.True(solve.Worst <= solve.MaxDisplacement + 1e-6f,
                    $"worst {solve.Worst} exceeded the {solve.MaxDisplacement} cap");
        Assert.Equal(solve.MaxDisplacement, At(solve.Positions(), index[5, 5]).Y, 1e-5f);
    }

    /// <summary>Hair gets twice the room a garment does — restyling moves strands much further.</summary>
    [Fact]
    public void HairMayMoveTwiceAsFar()
    {
        var (grid, index) = Grid(11);
        var model = Assemble([.. grid.Positions], [.. grid.Normals], [], [.. grid.Parts[0].Triangles],
                             "/mt_c0201h0162_hir_a.mtrl");
        var solve = new MeshVolumeSolve(model);
        Assert.Equal(MeshVolumeSolve.HairMaxDisplacement, solve.MaxDisplacement);

        var centre = At(model.Positions, index[5, 5]);
        for (int i = 0; i < 500; i++) solve.Paint(centre, 0.04f, 0.001f);
        Assert.Equal(0.2f, At(solve.Positions(), index[5, 5]).Y, 1e-5f);
    }

    /// <summary>Undo puts a stroke back where it found the surface, not back to flat.</summary>
    [Fact]
    public void UndoReturnsTheSurfaceToWhereTheStrokeFoundIt()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);
        int middle = index[5, 5];
        var centre = At(model.Positions, middle);

        solve.Paint(centre, 0.04f, 0.001f);
        solve.EndStroke();
        float afterFirst = At(solve.Positions(), middle).Y;
        Assert.True(afterFirst > 0f);

        solve.Paint(centre, 0.04f, 0.001f);
        solve.EndStroke();
        Assert.True(At(solve.Positions(), middle).Y > afterFirst);

        solve.Undo();
        Assert.Equal(afterFirst, At(solve.Positions(), middle).Y, 1e-6f);
        Assert.True(solve.CanUndo);

        solve.Undo();
        Assert.Equal(0f, At(solve.Positions(), middle).Y, 1e-9f);
        Assert.False(solve.CanUndo);
        Assert.False(solve.Dirty);
    }

    /// <summary>Deflate is the same brush with the sign flipped, and it is not clamped differently.</summary>
    [Fact]
    public void DeflatePullsIn()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);
        int middle = index[5, 5];

        solve.Paint(At(model.Positions, middle), 0.04f, -0.001f);
        Assert.Equal(-0.001f, At(solve.Positions(), middle).Y, 1e-6f);
    }

    /// <summary>
    /// A brush smaller than the mesh reaches nothing, and says so by moving nothing — which is what the
    /// panel's warning about the mesh's own resolution exists to explain before the user concludes the tool
    /// is broken.
    /// </summary>
    [Fact]
    public void ReportsReachingNothingWhenTheBrushIsTinierThanTheMesh()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);

        // Between two nodes, with a radius far smaller than the 10 mm spacing.
        var between = At(model.Positions, index[5, 5]) + new Vector3(Spacing * 0.5f, 0f, 0f);
        Assert.Equal(0, solve.Paint(between, 0.001f, 0.001f));
        Assert.False(solve.Dirty);

        // And the mesh's own resolution is reported, so the panel can compare a radius against it.
        Assert.Equal(Spacing, solve.MeanEdge, Spacing * 0.5f);
    }

    // ── mirror ───────────────────────────────────────────────────────────────

    /// <summary>
    /// An n×n sheet (n odd) centred on X = 0 — the body's midline — shaped by <paramref name="height"/>, normals
    /// +Y. Columns are placed at (i − middle) × spacing, so column i and its mirror are exact negations.
    /// Vertex i·n + j is column i, row j.
    /// <para/>
    /// The diagonals mirror too: one way left of the middle, the other way right of it. With every cell cut the same
    /// way the mirror of a cell is cut along its OTHER diagonal, the two sides have different neighbours, and
    /// anything that averages over neighbours — relax, the stroke-end smoothing — is honestly asymmetric.
    /// </summary>
    private static ModelParts Centred(int n, Func<int, int, float> height)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        int mid = n / 2;
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            pos.AddRange([(i - mid) * Spacing, height(i, j), j * Spacing]);
            nrm.AddRange([0f, 1f, 0f]);
        }
        for (int i = 0; i + 1 < n; i++)
        for (int j = 0; j + 1 < n; j++)
        {
            int a = i * n + j, b = (i + 1) * n + j, c = i * n + j + 1, d = (i + 1) * n + j + 1;
            if (i < mid) tris.AddRange([a, b, d, a, d, c]);
            else tris.AddRange([a, b, c, b, d, c]);
        }
        return Assemble(pos, nrm, tris);
    }

    private static void AssertSymmetric(float[] positions, int n, float tolerance, string what)
    {
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            var p = At(positions, i * n + j);
            var q = At(positions, (n - 1 - i) * n + j);
            Assert.True(MathF.Abs(p.Y - q.Y) <= tolerance && MathF.Abs(p.X + q.X) <= tolerance,
                        $"{what}: column {i} row {j} at {p} does not mirror {q}");
        }
    }

    /// <summary>A mirrored pull on one side moves the other side by the same amount.</summary>
    [Fact]
    public void MirroredPaintMovesBothSidesAlike()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        var solve = new MeshVolumeSolve(model);

        int right = 14 * n + 10, left = 6 * n + 10;
        Assert.True(solve.Paint(At(model.Positions, right), 0.03f, 0.001f, mirror: true) > 0);

        var after = solve.Positions();
        Assert.Equal(0.001f, At(after, right).Y, 1e-6f);
        Assert.Equal(0.001f, At(after, left).Y, 1e-6f);
        AssertSymmetric(after, n, 1e-7f, "before release");

        solve.EndStroke();
        // Slack for the stroke-end passes; the dab itself is exact (above).
        AssertSymmetric(solve.Positions(), n, 2e-4f, "after release");
    }

    /// <summary>
    /// Where the two discs overlap, the centre line is painted as hard as either side reaches it — the stronger
    /// falloff, not the sum — and a dab on the midline itself is exactly the unmirrored dab.
    /// </summary>
    [Fact]
    public void MirroredPaintDoesNotPaintTheMidlineTwice()
    {
        const int n = 21;
        var model = Centred(n, (_, _) => 0f);
        const float radius = 0.04f, strength = 0.001f;

        var solve = new MeshVolumeSolve(model);
        solve.Paint(At(model.Positions, 12 * n + 10), radius, strength, mirror: true);   // 20 mm right of centre
        Assert.Equal(strength * MeshVolumeSolve.Falloff(0.5f), At(solve.Positions(), 10 * n + 10).Y, 1e-6f);

        var mirrored = new MeshVolumeSolve(model);
        var plain = new MeshVolumeSolve(model);
        mirrored.Paint(At(model.Positions, 10 * n + 10), radius, strength, mirror: true);
        plain.Paint(At(model.Positions, 10 * n + 10), radius, strength);
        Assert.Equal(plain.Positions(), mirrored.Positions());
    }

    /// <summary>A mirrored relax and a mirrored wind stroke leave a symmetric model symmetric, and one undo takes back both sides.</summary>
    [Fact]
    public void MirroredRelaxAndWindAreSymmetricAndUndoneTogether()
    {
        const int n = 21;
        var model = Centred(n, (i, j) => (i == 6 || i == 14) && j == 10 ? 0.01f : 0f);
        var solve = new MeshVolumeSolve(model);
        int right = 14 * n + 10, left = 6 * n + 10;

        for (int d = 0; d < 10; d++) solve.Relax(At(model.Positions, right), 0.04f, 1f, mirror: true);
        AssertSymmetric(solve.Positions(), n, 1e-5f, "relax dabs");
        solve.EndStroke();
        var after = solve.Positions();
        Assert.True(At(after, right).Y < 0.009f, $"right bump not relaxed: {At(after, right).Y}");
        Assert.True(At(after, left).Y < 0.009f, $"left bump not relaxed: {At(after, left).Y}");
        AssertSymmetric(after, n, 1e-3f, "relax released");   // slack for the stroke-end passes

        solve.PaintWind(At(model.Positions, right), 0.04f, 1f, 1f, mirror: true);
        solve.EndStroke(wind: true);
        Assert.True(solve.WindAt(left) > 0.9f);
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            Assert.Equal(solve.WindAt(i * n + j), solve.WindAt((n - 1 - i) * n + j), 1e-6f);

        solve.Undo();
        for (int v = 0; v < n * n; v++) Assert.Equal(0f, solve.WindAt(v));
        solve.Undo();
        var back = solve.Positions();
        for (int v = 0; v < n * n; v++) Assert.Equal(At(model.Positions, v), At(back, v));
    }

    /// <summary>A mirrored bridge spans the groove it was painted on AND the matching groove on the other side.</summary>
    [Fact]
    public void MirroredBridgeSpansBothGrooves()
    {
        const int n = 21;
        static float Grooves(int i, int j) => -0.008f * MathF.Max(0f, 1f - MathF.Min(MathF.Abs(i - 4), MathF.Abs(i - 16)) / 3f);
        var model = Centred(n, Grooves);
        var solve = new MeshVolumeSolve(model);
        int right = 16 * n + 10, left = 4 * n + 10;

        for (int d = 0; d < 30; d++) solve.Bridge(At(model.Positions, right), 0.05f, 1f, Vector3.UnitY, mirror: true);
        solve.EndStroke(bridge: true);

        var after = solve.Positions();
        Assert.True(At(after, right).Y > -0.002f, $"right groove not spanned: {At(after, right).Y * 1000f:F2} mm");
        Assert.True(At(after, left).Y > -0.002f, $"left groove not spanned: {At(after, left).Y * 1000f:F2} mm");
        Assert.Equal(At(after, right).Y, At(after, left).Y, 1e-4f);
    }

    /// <summary>
    /// A mirrored bridge whose two discs overlap across a groove on the midline does not lift the seam twice:
    /// nothing rises above the flat cloth either side.
    /// </summary>
    [Fact]
    public void MirroredBridgeDoesNotLiftTheSeamTwice()
    {
        const int n = 21;
        static float Middle(int i, int j) => -0.008f * MathF.Max(0f, 1f - MathF.Abs(i - 10) / 4f);
        var model = Centred(n, Middle);
        var solve = new MeshVolumeSolve(model);

        for (int d = 0; d < 30; d++) solve.Bridge(At(model.Positions, 12 * n + 10), 0.15f, 1f, Vector3.UnitY, mirror: true);
        solve.EndStroke(bridge: true);

        var after = solve.Positions();
        for (int v = 0; v < n * n; v++)
            Assert.True(At(after, v).Y <= 1e-4f, $"vertex {v} lifted above the cloth: {At(after, v).Y * 1000f:F3} mm");
        Assert.True(At(after, 10 * n + 10).Y > -0.002f, "the midline groove was not spanned");
    }

    // ── other sizes ──────────────────────────────────────────────────────────

    /// <summary>
    /// A pull carried onto another size lands on the matching spot, even though that size is a different mesh —
    /// scaled up, and with its vertices in the opposite order — and painted wind goes with it.
    /// </summary>
    [Fact]
    public void TransfersAnEditOntoAnotherSizeByPlace()
    {
        const int n = 21;
        var source = Centred(n, (_, _) => 0f);

        // The other size: 10 % larger about the middle, vertices listed back to front.
        var pos = new List<float>();
        var nrm = new List<float>();
        int count = n * n;
        for (int v = count - 1; v >= 0; v--)
        {
            var p = At(source.Positions, v);
            pos.AddRange([p.X * 1.1f, p.Y, 0.1f + (p.Z - 0.1f) * 1.1f]);
            nrm.AddRange([0f, 1f, 0f]);
        }
        var tris = source.Parts[0].Triangles.Select(v => count - 1 - v).ToList();
        var larger = Assemble(pos, nrm, tris);

        var solve = new MeshVolumeSolve(source);
        int middle = 10 * n + 10;
        solve.Paint(At(source.Positions, middle), 0.05f, 0.002f);
        solve.EndStroke();
        solve.PaintWind(At(source.Positions, middle), 0.05f, 1f, 1f);
        solve.EndStroke(wind: true);

        var carried = BrushTransfer.Transfer(solve, source, larger);

        int largerMiddle = count - 1 - middle;
        Assert.Equal(solve.DeltaAt(middle).Y, carried.DeltaAt(largerMiddle).Y, 1e-5f);
        Assert.Equal(solve.WindAt(middle), carried.WindAt(largerMiddle), 0.01f);
        Assert.Equal(0f, carried.DeltaAt(count - 1).Y, 1e-6f);   // a corner, far outside the brush
        Assert.True(carried.Dirty);
        Assert.True(carried.WindEdited);
    }

    /// <summary>Wind the source never painted is not carried: the other size keeps its author's.</summary>
    [Fact]
    public void TransferLeavesWindAloneWhenNoneWasPainted()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);
        solve.Paint(At(model.Positions, index[5, 5]), 0.04f, 0.001f);
        solve.EndStroke();

        var carried = BrushTransfer.Transfer(solve, model, model);
        Assert.False(carried.WindEdited);
        Assert.Equal(solve.DeltaAt(index[5, 5]).Y, carried.DeltaAt(index[5, 5]).Y, 1e-6f);
    }

    // ── locked parts ─────────────────────────────────────────────────────────

    /// <summary>
    /// Two flat 11×11 sheets side by side, sharing one column of positions (x = 100 mm) where they weld — trousers
    /// and a belt sewn to them. Part 1.1 is the first sheet, 1.2 the second.
    /// </summary>
    private static (ModelParts Model, int N, int SecondStart) TwoParts()
    {
        const int n = 11;
        var (pos, nrm, tris, _) = Sheet(n, (_, _) => 0f, 1f);
        int firstTris = tris.Count;
        var (_, _, _, start) = Sheet(n, (_, _) => 0f, 1f, pos, nrm, tris);
        for (int v = start; v < start + n * n; v++) pos[v * 3] += (n - 1) * Spacing;

        var second = tris.Skip(firstTris).ToArray();
        tris.RemoveRange(firstTris, tris.Count - firstTris);
        return (Assemble(pos, nrm, tris, second), n, start);
    }

    /// <summary>
    /// A locked part is left out of pull, push, relax and wind — the seam it shares with the unlocked part holds
    /// too — while the unlocked part beside it moves.
    /// </summary>
    [Fact]
    public void LockedPartsAreLeftOutOfEveryBrush()
    {
        var (model, n, second) = TwoParts();
        var solve = new MeshVolumeSolve(model);
        solve.SetLocked(Enumerable.Range(second, n * n));

        var seam = At(model.Positions, second + 5);                        // on the shared column
        var firstNear = 7 * n + 5;                                         // 30 mm into the first sheet

        Assert.True(solve.Paint(seam, 0.05f, 0.001f) > 0);
        solve.PaintWind(seam, 0.05f, 1f, 1f);
        solve.EndStroke();

        var after = solve.Positions();
        for (int v = second; v < second + n * n; v++)
        {
            Assert.Equal(At(model.Positions, v), At(after, v));
            Assert.Equal(0f, solve.WindAt(v));
        }
        Assert.Equal(0f, At(after, (n - 1) * n + 5).Y);                    // the first sheet's copy of the seam
        Assert.True(At(after, firstNear).Y > 0f, "the unlocked part did not move");
        Assert.True(solve.WindAt(firstNear) > 0f, "the unlocked part took no wind");
        Assert.True(solve.IsLocked((n - 1) * n + 5), "a node welded to the locked part is not locked");

        // Relax over the seam, where the pull left a slope down to the locked side: the locked side still does not move.
        for (int d = 0; d < 10; d++) solve.Relax(seam, 0.05f, 1f);
        solve.EndStroke();
        for (int v = second; v < second + n * n; v++) Assert.Equal(At(model.Positions, v), At(solve.Positions(), v));
    }

    /// <summary>
    /// Locking after a stroke keeps what the stroke did, and undo still takes it back; unlocking lets the brush
    /// reach the part again.
    /// </summary>
    [Fact]
    public void LockingKeepsEarlierStrokesAndUndoStillWorks()
    {
        var (model, n, second) = TwoParts();
        var solve = new MeshVolumeSolve(model);
        int middle = second + 5 * n + 5;

        solve.Paint(At(model.Positions, middle), 0.03f, 0.001f);
        solve.EndStroke();
        float pulled = At(solve.Positions(), middle).Y;
        Assert.True(pulled > 0f);

        solve.SetLocked(Enumerable.Range(second, n * n));
        Assert.Equal(pulled, At(solve.Positions(), middle).Y);
        Assert.Equal(0, solve.Paint(At(model.Positions, middle), 0.03f, 0.001f));

        solve.Undo();
        Assert.Equal(0f, At(solve.Positions(), middle).Y);

        solve.SetLocked([]);
        Assert.True(solve.Paint(At(model.Positions, middle), 0.03f, 0.001f) > 0);
    }
}
