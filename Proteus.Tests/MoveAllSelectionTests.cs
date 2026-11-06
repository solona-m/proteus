using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Gui;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Move, Rotate and Scale's "All" mode: every ticked part of the open model moves as one piece. The rules the panel
/// reads, the selection it keeps, and — inside the solve, on synthetic models whose every point is known — that an All
/// drag fed through the unchanged <see cref="MeshVolumeSolve.BeginMove"/> is rigid.
/// </summary>
public class MoveAllSelectionTests
{
    private const string Cloth = "/mt_test.mtrl";
    private const string Bibo = "/mt_c0201b0001_bibo.mtrl";
    private const float Eps = 1e-5f;

    // ── fixtures ────────────────────────────────────────────────────────────

    private static ModelPart Part(int mesh, int submesh, int island, string label, int[] tris, string material = Cloth) => new()
    {
        Mesh = mesh,
        Submesh = submesh,
        Island = island,
        Label = label,
        Material = material,
        Triangles = tris,
        Ordinals = [.. Enumerable.Range(0, tris.Length / 3)],
        AttributeMask = 0,
        Toggleable = true,
        Min = Vector3.Zero,
        Max = Vector3.One,
    };

    private static ModelParts Model(List<float> pos, IReadOnlyList<ModelPart> parts)
    {
        var nrm = new float[pos.Count];
        for (int v = 0; v < pos.Count / 3; v++) nrm[v * 3 + 1] = 1f;
        return new ModelParts
        {
            Positions = pos.ToArray(),
            Normals = nrm,
            MeshSpans = [new MeshSpan(0, 0, pos.Count / 3)],
            Parts = parts,
            AttributeNames = [],
            Min = Vector3.Zero,
            Max = Vector3.One,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    /// <summary>An n×n sheet at <paramref name="origin"/>, 10 mm apart, its vertices appended to <paramref name="pos"/>.</summary>
    private static int[] Sheet(List<float> pos, Vector3 origin, int n = 4)
    {
        int first = pos.Count / 3;
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            pos.AddRange([origin.X + i * 0.01f, origin.Y, origin.Z + j * 0.01f]);
        var tris = new List<int>();
        for (int i = 0; i + 1 < n; i++)
        for (int j = 0; j + 1 < n; j++)
        {
            int a = first + i * n + j, b = first + (i + 1) * n + j, c = first + i * n + j + 1, d = first + (i + 1) * n + j + 1;
            tris.AddRange([a, d, b, a, c, d]);
        }
        return tris.ToArray();
    }

    /// <summary>Three separate cloth sheets ("1.1", "1.2" ticked; "1.3" locked) and a skin sheet ("1.4"), far apart.</summary>
    private static ModelParts FourPieces()
    {
        var pos = new List<float>();
        var a = Sheet(pos, new Vector3(0f, 0f, 0f));
        var b = Sheet(pos, new Vector3(0.2f, 0.1f, 0f));
        var c = Sheet(pos, new Vector3(-0.3f, 0f, 0.2f));
        var s = Sheet(pos, new Vector3(0f, -0.3f, 0.4f));
        return Model(pos, [Part(0, 0, -1, "1.1", a), Part(0, 1, -1, "1.2", b), Part(0, 2, -1, "1.3", c),
                           Part(0, 3, -1, "1.4", s, Bibo)]);
    }

    private static bool IsSkin(ModelPart p) => SecondSkinWriter.IsBodySkinMaterial(p.Material);

    /// <summary>The selection with <paramref name="locked"/> unticked, as the panel builds it.</summary>
    private static MoveAllSelection Select(ModelParts model, object? solve, ISet<string> locked, bool skinMoves,
                                           MoveAllSelection? into = null, int lockVersion = 0)
    {
        var sel = into ?? new MoveAllSelection();
        sel.Update(model, solve, PartsPanel.BuildPartOfVertex(model),
                   p => MoveAllSelection.Moves(IsSkin(p), locked.Contains(p.Label), skinMoves), lockVersion, skinMoves);
        return sel;
    }

    private static MeshVolumeSolve Solve(ModelParts model, ISet<string> locked, bool skinMoves = false)
    {
        var solve = new MeshVolumeSolve(model) { MoveSkin = skinMoves };
        if (skinMoves) Assert.True(solve.AttachFollow(SkinFollow.Bind(solve.FollowInputs)));
        solve.SetLocked(model.Parts.Where(p => locked.Contains(p.Label)).SelectMany(p => p.Triangles).Distinct());
        return solve;
    }

    private static Vector3 At(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);

    private static int Begin(MeshVolumeSolve solve, IEnumerable<int> seeds)
        => solve.BeginMove(seeds, MoveAllSelection.AdjacentApplies(MoveSelectMode.All, true), 0.05f,
                           alongSurface: false, mirror: null);

    private static readonly HashSet<string> LockedC = new(StringComparer.Ordinal) { "1.3" };

    // ── the pure rules ──────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-move-all-parts-4")]
    public void MovesTruthTable()
    {
        foreach (bool skin in new[] { false, true })
        foreach (bool locked in new[] { false, true })
        foreach (bool skinMoves in new[] { false, true })
            Assert.Equal(SkinMovePolicy.MovePickable(skin, locked, skinMoves), MoveAllSelection.Moves(skin, locked, skinMoves));
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-7")]
    [Trait("Plan", "EDGE-move-all-parts-10")]
    public void AdjacentAndMirrorOffUnderAll()
    {
        foreach (bool on in new[] { false, true })
        {
            Assert.False(MoveAllSelection.AdjacentApplies(MoveSelectMode.All, on));
            Assert.False(MoveAllSelection.MirrorApplies(MoveSelectMode.All, on));
            Assert.Equal(on, MoveAllSelection.AdjacentApplies(MoveSelectMode.Parts, on));
            Assert.Equal(on, MoveAllSelection.AdjacentApplies(MoveSelectMode.Polygons, on));
            Assert.Equal(on, MoveAllSelection.MirrorApplies(MoveSelectMode.Parts, on));
            Assert.Equal(on, MoveAllSelection.MirrorApplies(MoveSelectMode.Polygons, on));
        }
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-6")]
    [Trait("Plan", "EDGE-move-all-parts-9")]
    public void ClickChoosesTruthTable()
    {
        Assert.False(MoveAllSelection.ClickChooses(MoveSelectMode.All));
        Assert.True(MoveAllSelection.ClickChooses(MoveSelectMode.Parts));
        Assert.True(MoveAllSelection.ClickChooses(MoveSelectMode.Polygons));
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-6")]
    public void HelpForTable()
    {
        Assert.Equal(MoveHelpText.MoveHelp, MoveAllSelection.HelpFor(MoveHelpTool.Move, true, false));
        Assert.Equal(MoveHelpText.MoveLiveHint, MoveAllSelection.HelpFor(MoveHelpTool.Move, false, false));
        Assert.Equal(MoveHelpText.RotateHelp, MoveAllSelection.HelpFor(MoveHelpTool.Rotate, true, false));
        Assert.Equal(MoveHelpText.RotateLiveHint, MoveAllSelection.HelpFor(MoveHelpTool.Rotate, false, false));
        Assert.Equal(MoveHelpText.ScaleHelp, MoveAllSelection.HelpFor(MoveHelpTool.Scale, true, false));
        Assert.Equal(MoveHelpText.ScaleLiveHint, MoveAllSelection.HelpFor(MoveHelpTool.Scale, false, false));
        Assert.Equal(MoveHelpText.MoveAllHelp, MoveAllSelection.HelpFor(MoveHelpTool.Move, true, true));
        Assert.Equal(MoveHelpText.MoveAllLiveHint, MoveAllSelection.HelpFor(MoveHelpTool.Move, false, true));
        Assert.Equal(MoveHelpText.RotateAllHelp, MoveAllSelection.HelpFor(MoveHelpTool.Rotate, true, true));
        Assert.Equal(MoveHelpText.RotateAllLiveHint, MoveAllSelection.HelpFor(MoveHelpTool.Rotate, false, true));
        Assert.Equal(MoveHelpText.ScaleAllHelp, MoveAllSelection.HelpFor(MoveHelpTool.Scale, true, true));
        Assert.Equal(MoveHelpText.ScaleAllLiveHint, MoveAllSelection.HelpFor(MoveHelpTool.Scale, false, true));
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-1")]
    [Trait("Plan", "EDGE-move-all-parts-17")]
    public void RadiosFitTruthTable()
    {
        float[] widths = [80f, 60f, 20f];
        float box = 20f, inner = 4f, spacing = 8f;
        float exact = widths.Sum() + 3 * (box + inner) + 2 * spacing;
        Assert.True(MoveAllSelection.RadiosFit(widths, box, inner, spacing, exact));
        Assert.True(MoveAllSelection.RadiosFit(widths, box, inner, spacing, exact + 50f));
        Assert.False(MoveAllSelection.RadiosFit(widths, box, inner, spacing, exact - 1f));
    }

    // ── the selection ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-move-all-parts-6")]
    public void RowCountsAndSkinFlag()
    {
        var model = FourPieces();
        var sel = Select(model, null, LockedC, skinMoves: false);
        Assert.Equal(4, sel.Rows);
        Assert.Equal(2, sel.MovingRows);
        Assert.False(sel.IncludesSkin);
        Assert.Equal(new[] { "1.1", "1.2" }, sel.Labels.Order());

        var withSkin = Select(model, null, LockedC, skinMoves: true);
        Assert.Equal(3, withSkin.MovingRows);
        Assert.True(withSkin.IncludesSkin);

        // Islands are not rows of their own.
        var pos = new List<float>();
        var a = Sheet(pos, Vector3.Zero);
        var b = Sheet(pos, new Vector3(0.1f, 0f, 0f));
        var islands = Model(pos, [Part(0, 0, -1, "1.1", [.. a, .. b]), Part(0, 0, 0, "1.1a", a), Part(0, 0, 1, "1.1b", b)]);
        var isel = Select(islands, null, new HashSet<string>(), false);
        Assert.Equal(1, isel.Rows);
        Assert.Equal(1, isel.MovingRows);
        // Every island locked: the submesh moves nothing, so it is not counted as moving (QA, build 1439).
        var allLocked = new MoveAllSelection();
        allLocked.Update(islands, null, PartsPanel.BuildPartOfVertex(islands), p => p.Island < 0, 0, false);
        Assert.Empty(allLocked.Seeds);
        Assert.Equal(0, allLocked.MovingRows);
        Assert.Empty(allLocked.Labels);   // nothing is shown as moving that the count leaves out

        // The same with body skin: skin whose every piece is locked is not "including skin".
        var skinPos = new List<float>();
        var sa = Sheet(skinPos, Vector3.Zero);
        var sb = Sheet(skinPos, new Vector3(0.1f, 0f, 0f));
        var skinIslands = Model(skinPos, [Part(0, 0, -1, "1.1", [.. sa, .. sb], Bibo), Part(0, 0, 0, "1.1a", sa, Bibo),
                                          Part(0, 0, 1, "1.1b", sb, Bibo)]);
        var skinLocked = new MoveAllSelection();
        skinLocked.Update(skinIslands, null, PartsPanel.BuildPartOfVertex(skinIslands), p => p.Island < 0, 0, true);
        Assert.False(skinLocked.IncludesSkin);
        Assert.Equal(0, skinLocked.MovingRows);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-5")]
    public void PivotIsTheCentreOfAllSeedsBounds()
    {
        var model = FourPieces();
        var sel = Select(model, null, LockedC, skinMoves: false);
        // "1.1" spans x 0..0.03, y 0, z 0..0.03; "1.2" spans x 0.2..0.23, y 0.1, z 0..0.03.
        var centre = MoveAllSelection.BoundsCentre(sel.Seeds, model.Positions);
        Assert.NotNull(centre);
        Assert.True(Vector3.Distance(new Vector3(0.115f, 0.05f, 0.015f), centre!.Value) < Eps);
        Assert.Null(MoveAllSelection.BoundsCentre([], model.Positions));
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-1")]
    public void NothingTickedGivesNoSeeds()
    {
        var model = FourPieces();
        var all = new HashSet<string>(StringComparer.Ordinal) { "1.1", "1.2", "1.3" };
        var sel = Select(model, null, all, skinMoves: false);      // only skin left, and it does not move
        Assert.Empty(sel.Seeds);
        Assert.Equal(0, sel.MovingRows);
        Assert.Equal(0, Begin(Solve(model, all), sel.Seeds));

        all.Add("1.4");
        Assert.Empty(Select(model, null, all, skinMoves: true).Seeds);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-2")]
    public void SkinSwitchRebuildsTheSelection()
    {
        var model = FourPieces();
        var pov = PartsPanel.BuildPartOfVertex(model);
        var sel = new MoveAllSelection();
        bool Update(bool skin) => sel.Update(model, null, pov, p => MoveAllSelection.Moves(IsSkin(p), LockedC.Contains(p.Label), skin), 0, skin);
        var skinVertex = model.Parts[3].Triangles[0];

        Assert.True(Update(false));
        Assert.DoesNotContain("1.4", sel.Labels);
        Assert.DoesNotContain(skinVertex, sel.Seeds);
        Assert.True(Update(true));
        Assert.Contains("1.4", sel.Labels);
        Assert.Contains(skinVertex, sel.Seeds);
        Assert.True(sel.IncludesSkin);
        Assert.True(Update(false));
        Assert.DoesNotContain("1.4", sel.Labels);
        Assert.False(sel.IncludesSkin);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-3")]
    public void FinestPartDecides()
    {
        var pos = new List<float>();
        var a = Sheet(pos, Vector3.Zero);
        var b = Sheet(pos, new Vector3(0.1f, 0f, 0f));
        var model = Model(pos, [Part(0, 0, -1, "1.1", [.. a, .. b]), Part(0, 0, 0, "1.1a", a), Part(0, 0, 1, "1.1b", b)]);

        // Island b locked inside a ticked submesh: b stays, a moves.
        var sel = Select(model, null, new HashSet<string> { "1.1b" }, false);
        Assert.All(a, v => Assert.Contains(v, sel.Seeds));
        Assert.All(b, v => Assert.DoesNotContain(v, sel.Seeds));

        // Every island but a locked: only a moves.
        var only = new MoveAllSelection();
        only.Update(model, null, PartsPanel.BuildPartOfVertex(model), p => p.Label == "1.1a", 0, false);
        Assert.Equal(a.Distinct().Order(), only.Seeds);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-8")]
    public void NewModelOrSolveRebuilds()
    {
        var model = FourPieces();
        var pov = PartsPanel.BuildPartOfVertex(model);
        var sel = new MoveAllSelection();
        Func<ModelPart, bool> moves = p => !IsSkin(p);
        Assert.True(sel.Update(model, null, pov, moves, 3, false));
        var other = FourPieces();
        Assert.True(sel.Update(other, null, PartsPanel.BuildPartOfVertex(other), moves, 3, false));
        var otherPov = PartsPanel.BuildPartOfVertex(other);
        Assert.True(sel.Update(other, null, otherPov, moves, 3, false));        // a new vertex map
        Assert.True(sel.Update(other, new object(), otherPov, moves, 3, false)); // a new solve
        var solve = new object();
        Assert.True(sel.Update(other, solve, otherPov, moves, 3, false));
        Assert.True(sel.Update(other, solve, otherPov, moves, 4, false));       // the locks changed
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-14")]
    public void SameKeyDoesNotRebuild()
    {
        var model = FourPieces();
        var pov = PartsPanel.BuildPartOfVertex(model);
        var solve = new object();
        var sel = new MoveAllSelection();
        int calls = 0;
        Func<ModelPart, bool> moves = p => { calls++; return !IsSkin(p); };
        Assert.True(sel.Update(model, solve, pov, moves, 7, false));
        var seeds = sel.Seeds;
        int version = sel.Version, after = calls;
        Assert.False(sel.Update(model, solve, pov, moves, 7, false));
        Assert.Same(seeds, sel.Seeds);
        Assert.Equal(version, sel.Version);
        Assert.Equal(after, calls);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-9")]
    [Trait("Plan", "EDGE-move-all-parts-16")]
    public void TouchChangesTheVersion()
    {
        var model = FourPieces();
        var sel = Select(model, null, LockedC, false);
        var seeds = sel.Seeds;
        int version = sel.Version;
        sel.Touch();
        Assert.NotEqual(version, sel.Version);
        Assert.Same(seeds, sel.Seeds);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-13")]
    public void SinglePartMatchesPartsMode()
    {
        var pos = new List<float>();
        var a = Sheet(pos, Vector3.Zero);
        var model = Model(pos, [Part(0, 0, -1, "1.1", a)]);
        var sel = Select(model, null, new HashSet<string>(), false);
        Assert.Equal(a.Distinct().Order(), sel.Seeds);
        Assert.Equal(1, sel.MovingRows);
    }

    // ── inside the solve ────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-move-all-parts-2")]
    public void AllDragMovesEveryTickedPartByTheSameOffset()
    {
        var model = FourPieces();
        var solve = Solve(model, LockedC);
        var sel = Select(model, solve, LockedC, false);
        var before = solve.Positions().ToArray();
        var offset = new Vector3(0.03f, -0.02f, 0.05f);

        Assert.True(Begin(solve, sel.Seeds) > 0);
        solve.MoveTo(offset);
        var during = solve.Positions().ToArray();
        Assert.True(solve.EndMove());
        var after = solve.Positions().ToArray();

        var seeds = sel.Seeds.ToHashSet();
        for (int v = 0; v < before.Length / 3; v++)
        {
            var expected = seeds.Contains(v) ? At(before, v) + offset : At(before, v);
            Assert.True(Vector3.Distance(expected, At(during, v)) < 1e-6f, $"vertex {v} during");
            Assert.True(Vector3.Distance(expected, At(after, v)) < 1e-6f, $"vertex {v} after");
        }
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-3")]
    public void AllRotateAndScaleApplyOneTransformAboutTheCentre()
    {
        var model = FourPieces();
        var sel = Select(model, null, LockedC, false);
        var c = MoveAllSelection.BoundsCentre(sel.Seeds, model.Positions)!.Value;
        var seeds = sel.Seeds.ToHashSet();

        var transforms = new[]
        {
            Matrix4x4.CreateTranslation(-c) * Matrix4x4.CreateFromYawPitchRoll(0.6f, 0.3f, -0.2f) * Matrix4x4.CreateTranslation(c),
            Matrix4x4.CreateTranslation(-c) * Matrix4x4.CreateScale(1.7f) * Matrix4x4.CreateTranslation(c),
            Matrix4x4.CreateTranslation(-c) * Matrix4x4.CreateScale(1f, 2.5f, 1f) * Matrix4x4.CreateTranslation(c),
        };
        foreach (var m in transforms)
        {
            var solve = Solve(model, LockedC);
            var before = solve.Positions().ToArray();
            Assert.True(Begin(solve, sel.Seeds) > 0);
            solve.TransformTo(m);
            var during = solve.Positions().ToArray();
            for (int v = 0; v < before.Length / 3; v++)
            {
                var expected = seeds.Contains(v) ? Vector3.Transform(At(before, v), m) : At(before, v);
                Assert.True(Vector3.Distance(expected, At(during, v)) < Eps, $"vertex {v}");
            }
            // About the centre of everything moved: the centre itself is a fixed point of the transform.
            Assert.True(Vector3.Distance(c, Vector3.Transform(c, m)) < Eps, "centre");
            solve.EndMove();
        }

        // A turn is rigid: every distance between moved points is kept.
        var rigid = Solve(model, LockedC);
        var start = rigid.Positions().ToArray();
        Begin(rigid, sel.Seeds);
        rigid.TransformTo(transforms[0]);
        var turned = rigid.Positions().ToArray();
        var list = sel.Seeds;
        for (int i = 0; i < list.Length; i += 3)
        for (int j = i + 1; j < list.Length; j += 5)
            Assert.Equal(Vector3.Distance(At(start, list[i]), At(start, list[j])),
                         Vector3.Distance(At(turned, list[i]), At(turned, list[j])), 4);
        rigid.EndMove();
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-4")]
    public void UntickedAndSkinStayPut()
    {
        var model = FourPieces();
        var solve = Solve(model, LockedC);
        var sel = Select(model, solve, LockedC, false);
        var still = model.Parts[2].Triangles.Concat(model.Parts[3].Triangles).Distinct().ToArray();
        Assert.All(still, v => Assert.DoesNotContain(v, sel.Seeds));

        var before = solve.Positions().ToArray();
        // Even handed every vertex, the solve leaves the locked part and skin where they are.
        var everything = Enumerable.Range(0, before.Length / 3);
        Assert.True(Begin(solve, everything) > 0);
        solve.MoveTo(new Vector3(0.1f, 0.1f, 0.1f));
        var after = solve.Positions().ToArray();
        Assert.All(still, v => Assert.Equal(At(before, v), At(after, v)));
        solve.EndMove();
    }

    /// <summary>A skin sheet and, 5 mm over it, a cloth sheet, separate vertices — as "Move skin too" binds.</summary>
    private static ModelParts SkinAndCloth()
    {
        var pos = new List<float>();
        var skin = Sheet(pos, Vector3.Zero, 6);
        var cloth = Sheet(pos, new Vector3(0f, 0.005f, 0f), 6);
        return Model(pos, [Part(0, 0, -1, "1.1", cloth), Part(0, 1, -1, "1.2", skin, Bibo)]);
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-4")]
    public void SkinMovesWithAllWhenMoveSkinToo()
    {
        var model = SkinAndCloth();
        var none = new HashSet<string>();
        var solve = Solve(model, none, skinMoves: true);
        var sel = Select(model, solve, none, skinMoves: true);
        Assert.True(sel.IncludesSkin);
        Assert.Contains("1.2", sel.Labels);
        var skin = model.Parts[1].Triangles.Distinct().ToArray();
        Assert.All(skin, v => Assert.Contains(v, sel.Seeds));

        var before = solve.Positions().ToArray();
        var offset = new Vector3(0.01f, 0.02f, -0.01f);
        Assert.True(Begin(solve, sel.Seeds) > 0);
        solve.MoveTo(offset);
        var after = solve.Positions().ToArray();
        Assert.All(skin, v => Assert.True(Vector3.Distance(At(before, v) + offset, At(after, v)) < 1e-6f));
        solve.EndMove();
    }

    [Fact]
    [Trait("Plan", "AC-move-all-parts-8")]
    public void OneUndoTakesBackTheWholeAllDrag()
    {
        var model = FourPieces();
        var solve = Solve(model, LockedC);
        var sel = Select(model, solve, LockedC, false);
        var before = solve.Positions().ToArray();
        Begin(solve, sel.Seeds);
        solve.MoveTo(new Vector3(0.05f, 0f, 0f));
        solve.MoveTo(new Vector3(0.07f, 0.01f, 0f));
        Assert.True(solve.EndMove());
        solve.Undo();
        var undone = solve.Positions();
        for (int i = 0; i < before.Length; i++) Assert.Equal(before[i], undone[i], 6);
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-4")]
    public void WeldToLockedPartHolds()
    {
        // Two sheets sharing an edge by position: the moving one's edge is welded to the locked one.
        var pos = new List<float>();
        var a = Sheet(pos, Vector3.Zero);
        var b = Sheet(pos, new Vector3(0.03f, 0f, 0f));   // its first column sits on a's last
        var model = Model(pos, [Part(0, 0, -1, "1.1", a), Part(0, 1, -1, "1.2", b)]);
        var locked = new HashSet<string> { "1.2" };
        var solve = Solve(model, locked);
        var sel = Select(model, solve, locked, false);
        var before = solve.Positions().ToArray();
        var offset = new Vector3(0f, 0.02f, 0f);

        Assert.True(Begin(solve, sel.Seeds) > 0);
        solve.MoveTo(offset);
        var after = solve.Positions().ToArray();
        int welded = 0, moved = 0;
        foreach (int v in a.Distinct())
        {
            bool onSeam = MathF.Abs(before[v * 3] - 0.03f) < 1e-6f;
            if (onSeam) { welded++; Assert.Equal(At(before, v), At(after, v)); }
            else { moved++; Assert.True(Vector3.Distance(At(before, v) + offset, At(after, v)) < 1e-6f); }
        }
        Assert.Equal(4, welded);
        Assert.Equal(12, moved);
        solve.EndMove();
    }

    [Fact]
    [Trait("Plan", "EDGE-move-all-parts-11")]
    public void TapMovesNothing()
    {
        var model = FourPieces();
        var solve = Solve(model, LockedC);
        var sel = Select(model, solve, LockedC, false);
        var before = solve.Positions().ToArray();
        Begin(solve, sel.Seeds);
        solve.MoveTo(Vector3.Zero);
        Assert.False(solve.EndMove());
        Assert.Equal(before, solve.Positions());
    }
}
