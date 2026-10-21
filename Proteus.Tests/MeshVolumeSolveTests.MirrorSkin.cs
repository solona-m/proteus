using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Kind = Proteus.Services.MeshVolumeSolve.MoveKind;
using Mode = Proteus.Services.MeshVolumeSolve.MirrorMode;
using MoveMirror = Proteus.Services.MeshVolumeSolve.MoveMirror;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// Move, Rotate and Scale with Mirror and "Move skin too" both on: picked skin carries the matching skin across X = 0,
/// the cloth riding each side follows its own side, and skin and cloth each match only their own kind. Measured inside
/// the pass, never on an exported model.
/// </summary>
public partial class MeshVolumeSolveTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────

    /// <summary>A body skin sheet across the centre and a cloth sheet 1 mm over it, both mirror images of themselves.</summary>
    /// <param name="skinFrom">Where the skin's first column is: −0.1 centres it; −0.097 leaves the body 3 mm off centre.</param>
    private static (ModelParts Model, int[,] Skin, int[,] Cloth) SkinUnderCloth(float skinFrom = -0.1f, float gap = 0.001f)
    {
        var mesh = new MirrorMesh();
        var skin = mesh.Grid(Steps(skinFrom, 21), Steps(0f, 21), y: 0f, material: SkinMaterial);
        var cloth = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21), y: gap);
        return (mesh.Build(), skin, cloth);
    }

    /// <summary>Each vertex's mirror twin — the vertex of the same kind at its reflected position — or −1.</summary>
    private static int[] Twins(ModelParts model)
    {
        int count = model.Positions.Length / 3;
        var skin = SkinVertices(model);
        var at = new Dictionary<(long, long, long, bool), int>();
        static (long, long, long) Key(Vector3 p)
            => ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));
        for (int v = 0; v < count; v++)
        {
            var (x, y, z) = Key(At(model.Positions, v));
            at.TryAdd((x, y, z, skin.Contains(v)), v);
        }
        var twins = new int[count];
        for (int v = 0; v < count; v++)
        {
            var (x, y, z) = Key(Reflect(At(model.Positions, v)));
            twins[v] = at.TryGetValue((x, y, z, skin.Contains(v)), out int t) ? t : -1;
        }
        return twins;
    }

    private static HashSet<int> SkinVertices(ModelParts model)
        => model.Parts.Where(p => SecondSkinWriter.IsBodySkinMaterial(p.Material)).SelectMany(p => p.Triangles).ToHashSet();

    /// <summary>A pairing over the model's vertices as nodes, as the solve builds one, with extra joins for welds.</summary>
    private static (MirrorPairing Pairing, bool[] Skin) PairingOf(ModelParts model, IEnumerable<(int, int)>? welds = null)
    {
        int count = model.Positions.Length / 3;
        var at = new Vec3[count];
        var nrm = new Vec3[count];
        for (int v = 0; v < count; v++)
        {
            at[v] = new Vec3(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
            nrm[v] = new Vec3(model.Normals[v * 3], model.Normals[v * 3 + 1], model.Normals[v * 3 + 2]);
        }
        var skin = new bool[count];
        var material = new int[count];
        var adj = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        void Join(int a, int b)
        {
            if (!adj[a].Contains(b)) adj[a].Add(b);
            if (!adj[b].Contains(a)) adj[b].Add(a);
        }
        for (int p = 0; p < model.Parts.Count; p++)
        {
            var part = model.Parts[p];
            bool isSkin = SecondSkinWriter.IsBodySkinMaterial(part.Material);
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                foreach (int v in new[] { a, b, c }) { skin[v] = isSkin; material[v] = p; }
                Join(a, b); Join(b, c); Join(c, a);
            }
        }
        foreach (var (a, b) in welds ?? []) Join(a, b);
        var usable = Enumerable.Repeat(true, count).ToArray();
        return (new MirrorPairing(at, nrm, usable, skin, material, adj, Spacing), skin);
    }

    private static Vec3 ToVec3(Vector3 v) => new(v.X, v.Y, v.Z);

    // ── the other side's skin follows ───────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-1")]
    public void MirroredSkinRotateAndScaleTurnTheOtherSideTheMirroredWay()
    {
        var (model, skin, _) = SkinUnderCloth();
        var patch = Cells(skin, 14, 16, 8, 12);
        var far = Cells(skin, 4, 6, 8, 12);
        var twins = Twins(model);
        foreach (var kind in new[] { Kind.Rotate, Kind.Scale })
        {
            var solve = SkinSolve(model);
            var how = Mirrored(kind, model.Positions, patch);
            var t = kind == Kind.Rotate
                ? RotateGizmo.About(how.Pivot, Vector3.Normalize(new Vector3(0.3f, 0.2f, 1f)), 0.4f)
                : PartScaleDrag.Build(how.Pivot, new Vector3(1.3f, 1.2f, 1f));
            solve.BeginMove(patch, false, 0f, mirror: how);
            Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
            solve.TransformTo(t);
            var after = solve.Positions();

            foreach (int v in patch) AssertNear(Vector3.Transform(At(model.Positions, v), t), At(after, v), 1e-6f, $"{kind}: picked skin");
            foreach (int v in far)
                AssertNear(Vector3.Transform(At(model.Positions, v), MirrorMotion.Reflect(t)), At(after, v), 1e-6f, $"{kind}: matching skin");
            for (int v = 0; v < twins.Length; v++)
                if (twins[v] >= 0)
                    AssertNear(Reflect(At(after, v)), At(after, twins[v]), 1e-5f, $"{kind}: vertex {v} and its twin");
            Assert.True(solve.EndMove());
        }
    }

    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-2")]
    public void WholeSkinAcrossTheCentreStaysCentred()
    {
        var (model, skin, cloth) = SkinUnderCloth();
        var all = All(skin);
        var solve = SkinSolve(model);
        solve.BeginMove(all, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, all));
        Assert.Equal(Mode.Centred, solve.DragMirror?.Mode);
        solve.MoveTo(new Vector3(0.01f, 0f, 0f));
        Assert.False(solve.EndMove(), "a sideways move of a centred piece should change nothing");

        solve.BeginMove(all, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, all));
        solve.MoveTo(new Vector3(0.01f, 0.004f, 0f));
        var after = solve.Positions();
        foreach (int v in all) AssertNear(At(model.Positions, v) + new Vector3(0f, 0.004f, 0f), At(after, v), 1e-6f, "skin");
        foreach (int v in Cells(cloth, 3, 17, 3, 17))
            AssertNear(At(model.Positions, v) + new Vector3(0f, 0.004f, 0f), At(after, v), 1e-4f, "cloth riding the skin");
        Assert.True(solve.EndMove());
    }

    // ── each kind matches its own ───────────────────────────────────────────

    /// <summary>The body 3 mm off centre, tight cloth 1 mm over it: a skin point's reflection lands nearer the far
    /// cloth than the far skin, yet skin pairs with skin, and cloth with cloth.</summary>
    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-3")]
    public void PairingMatchesEachKindWithItsOwn()
    {
        var (model, skin, cloth) = SkinUnderCloth(skinFrom: -0.097f);
        var (pairing, isSkin) = PairingOf(model);
        var locked = new bool[isSkin.Length];
        var at = model.Positions;

        var skinPairs = pairing.Pair(Cells(skin, 14, 16, 8, 12), locked, v => ToVec3(At(at, v)), 0);
        Assert.NotEmpty(skinPairs.Partner);
        Assert.All(skinPairs.Partner.Values, p => Assert.True(isSkin[p], $"skin paired with cloth vertex {p}"));
        Assert.All(skinPairs.Reverse.Keys, m => Assert.True(isSkin[m], $"cloth vertex {m} in skin's matching region"));

        var clothPairs = pairing.Pair(Cells(cloth, 14, 16, 8, 12), locked, v => ToVec3(At(at, v)), 0);
        Assert.NotEmpty(clothPairs.Partner);
        Assert.All(clothPairs.Partner.Values, p => Assert.False(isSkin[p], $"cloth paired with skin vertex {p}"));
        Assert.All(clothPairs.Reverse.Keys, m => Assert.False(isSkin[m], $"skin vertex {m} in cloth's matching region"));
    }

    /// <summary>A stocking welded to the leg: cloth's partners and matching region are the same whether skin is picked
    /// with it or not — neither kind's region is joined to a partner through the other's.</summary>
    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-3")]
    public void WeldedClothPairsTheSameWithSkinSeeds()
    {
        var mesh = new MirrorMesh();
        var leg = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 11), material: SkinMaterial);
        var stocking = mesh.Grid(Steps(-0.1f, 21), Steps(0.1f, 11));
        var model = mesh.Build();
        var welds = Enumerable.Range(0, 21).Select(i => (leg[i, 10], stocking[i, 0]));
        var (pairing, isSkin) = PairingOf(model, welds);
        var locked = new bool[isSkin.Length];
        var at = model.Positions;
        var clothSeeds = Cells(stocking, 14, 16, 0, 4);
        var skinSeeds = Cells(leg, 14, 16, 6, 10);

        var alone = pairing.Pair(clothSeeds, locked, v => ToVec3(At(at, v)), 0);
        var mixed = pairing.Pair(clothSeeds.Concat(skinSeeds).ToArray(), locked, v => ToVec3(At(at, v)), 0);
        Assert.Equal(alone.Partner.OrderBy(e => e.Key), mixed.Partner.Where(e => !isSkin[e.Key]).OrderBy(e => e.Key));
        Assert.Equal(alone.Reverse.OrderBy(e => e.Key), mixed.Reverse.Where(e => !isSkin[e.Key]).OrderBy(e => e.Key));
        Assert.Contains(mixed.Partner, e => isSkin[e.Key]);
    }

    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-3")]
    public void SkinIsNeverAPartnerWithSkinMovesOn()
    {
        var (model, skin, cloth) = SkinUnderCloth(skinFrom: -0.097f);
        var patch = Cells(cloth, 14, 16, 8, 12);
        var o = new Vector3(0.005f, 0.01f, 0f);
        var solve = SkinSolve(model);
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        solve.MoveTo(o);
        var after = solve.Positions();
        AssertUnmoved(model.Positions, after, All(skin), "skin");
        foreach (int v in Cells(cloth, 4, 6, 8, 12)) AssertNear(At(model.Positions, v) + Reflect(o), At(after, v), 1e-7f, "mirrored cloth");
    }

    // ── skin with no mirror image ───────────────────────────────────────────

    /// <summary>Skin on the right only, under cloth across the whole width.</summary>
    private static (ModelParts Model, int[,] Skin, int[,] Cloth) OneSidedSkin()
    {
        var mesh = new MirrorMesh();
        var skin = mesh.Grid(Steps(0.01f, 10), Steps(0f, 21), material: SkinMaterial);
        var cloth = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21), y: 0.001f);
        return (mesh.Build(), skin, cloth);
    }

    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-4")]
    [Trait("Plan", "EDGE-mirror-skin-move-2")]
    public void SkinWithNoMirrorImageMovesOnItsOwnSide()
    {
        var (model, skin, cloth) = OneSidedSkin();
        var all = All(skin);
        var o = new Vector3(0.002f, 0.004f, 0f);
        var solve = SkinSolve(model);
        solve.BeginMove(all, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, all));
        Assert.Equal(Mode.Off, solve.DragMirror?.Mode);
        solve.MoveTo(o);
        var after = solve.Positions();
        foreach (int v in all) AssertNear(At(model.Positions, v) + o, At(after, v), 1e-6f, "skin");
        foreach (int v in Cells(cloth, 13, 18, 3, 17)) AssertNear(At(model.Positions, v) + o, At(after, v), 1e-4f, "cloth riding it");
        AssertUnmoved(model.Positions, after, Cells(cloth, 0, 7, 0, 20), "the other side");
    }

    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-4")]
    [Trait("Plan", "AC-mirror-skin-move-5")]
    [Trait("Plan", "EDGE-mirror-skin-move-2")]
    public void SkinOnlyWithNoMatchReportsNone()
    {
        var (model, skin, _) = OneSidedSkin();
        var solve = SkinSolve(model);
        Assert.NotNull(solve.MoveFalloff(All(skin), 0f, false, mirror: true));
        Assert.Equal(Strings.Parts.MoveMirrorNone, PartsPanel.MirrorStatus(true, false, null, solve.PreviewMirror));
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-3")]
    public void UnmatchedSkinWithMatchedClothTakesTheDragItself()
    {
        var (model, skin, cloth) = OneSidedSkin();
        var patch = Cells(cloth, 14, 16, 8, 12);
        var sel = patch.Concat(All(skin)).ToArray();
        var o = new Vector3(0.002f, 0.004f, 0f);
        var solve = SkinSolve(model);
        solve.BeginMove(sel, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, sel));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        solve.MoveTo(o);
        var after = solve.Positions();
        foreach (int v in All(skin)) AssertNear(At(model.Positions, v) + o, At(after, v), 1e-6f, "unmatched skin");
        foreach (int v in Cells(cloth, 18, 19, 3, 17)) AssertNear(At(model.Positions, v) + o, At(after, v), 1e-4f, "unpicked cloth riding it");
        foreach (int v in patch) AssertNear(At(model.Positions, v) + o, At(after, v), 1e-4f, "picked cloth");
        foreach (int v in Cells(cloth, 4, 6, 8, 12)) AssertNear(At(model.Positions, v) + Reflect(o), At(after, v), 1e-6f, "its match");
    }

    // ── what the views and the status line are shown ────────────────────────

    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-5")]
    public void SkinPreviewReportsAndCarriesTheFarSkin()
    {
        var (model, skin, _) = SkinUnderCloth();
        var patch = Cells(skin, 14, 16, 8, 12);
        var solve = SkinSolve(model);
        var weights = solve.MoveFalloff(patch, 0f, false, mirror: true);
        Assert.NotNull(weights);
        var r = solve.PreviewMirror!.Value;
        Assert.Equal(Mode.Sides, r.Mode);
        Assert.Equal(r.Selected, r.Paired);
        foreach (int v in Cells(skin, 4, 6, 8, 12)) Assert.Equal(1f, weights![v]);
        Assert.Equal(string.Format(Strings.Parts.MoveMirrorPairedFmt, r.Paired, r.Selected),
                     PartsPanel.MirrorStatus(true, false, null, solve.PreviewMirror));
    }

    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-10")]
    public void MirrorTipMentionsSkin()
        => Assert.Contains("skin", Strings.Parts.MirrorTip, StringComparison.OrdinalIgnoreCase);

    // ── off is today ────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-6")]
    public void MirroredMoveWithSkinOffNeverMovesSkin()
    {
        var (model, skin, cloth) = SkinUnderCloth();
        var patch = Cells(cloth, 14, 16, 8, 12);
        var sel = patch.Concat(Cells(skin, 14, 16, 8, 12)).ToArray();
        var o = new Vector3(0.002f, 0.004f, 0f);
        float[] Drag(IEnumerable<int> verts)
        {
            var solve = SkinSolve(model, on: false);
            solve.BeginMove(verts, false, 0f, mirror: new MoveMirror(Kind.Translate, BoundsCentre(model.Positions, patch)));
            solve.MoveTo(o);
            solve.EndMove();
            return solve.Positions();
        }
        var both = Drag(sel);
        AssertUnmoved(model.Positions, both, All(skin), "skin");
        Assert.Equal(Drag(patch), both);
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-9")]
    public void UnboundSkinSolveHoldsSkin()
    {
        var (model, skin, cloth) = SkinUnderCloth();
        var sel = Cells(cloth, 14, 16, 8, 12).Concat(Cells(skin, 14, 16, 8, 12)).ToArray();
        var solve = new MeshVolumeSolve(model) { MoveSkin = true };
        solve.BeginMove(sel, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, sel));
        solve.MoveTo(new Vector3(0.002f, 0.004f, 0f));
        solve.EndMove();
        AssertUnmoved(model.Positions, solve.Positions(), All(skin), "skin");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-11")]
    public void ClothOnlySelectionIsTheSameWithSkinMovesOnOrOff()
    {
        var (model, _, cloth) = SkinUnderCloth();
        var patch = Cells(cloth, 14, 16, 8, 12);
        (float[] During, float[] After) Drag(bool on)
        {
            var solve = SkinSolve(model, on);
            solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
            solve.MoveTo(new Vector3(0.002f, 0.004f, 0f));
            var during = solve.Positions();
            solve.EndMove();
            return (during, solve.Positions());
        }
        var on = Drag(true);
        var off = Drag(false);
        Assert.Equal(off.During, on.During);
        Assert.Equal(off.After, on.After);
    }

    // ── undo, taps and normals ──────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-7")]
    public void OneUndoTakesBackBothSidesSkin()
    {
        var (model, skin, _) = SkinUnderCloth();
        var patch = Cells(skin, 14, 16, 8, 12);
        var solve = SkinSolve(model);
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        solve.MoveTo(new Vector3(0.002f, 0.006f, 0f));
        Assert.True(solve.EndMove());
        Assert.NotEqual(At(model.Positions, skin[5, 10]), At(solve.Positions(), skin[5, 10]));
        solve.Undo();
        var back = solve.Positions();
        for (int v = 0; v < model.Positions.Length / 3; v++) AssertNear(At(model.Positions, v), At(back, v), 1e-6f, $"vertex {v}");
        Assert.False(solve.CanUndo);
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-12")]
    public void ATapOnMirroredSkinLeavesNoEdit()
    {
        var (model, skin, _) = SkinUnderCloth();
        var patch = Cells(skin, 14, 16, 8, 12);
        foreach (var kind in new[] { Kind.Translate, Kind.Rotate, Kind.Scale })
        {
            var solve = SkinSolve(model);
            solve.BeginMove(patch, false, 0f, mirror: Mirrored(kind, model.Positions, patch));
            if (kind == Kind.Translate) solve.MoveTo(Vector3.Zero);
            else solve.TransformTo(Matrix4x4.Identity);
            Assert.False(solve.EndMove(), $"{kind}: a tap counted as a change");
            Assert.False(solve.CanUndo);
            Assert.False(solve.Dirty);
            Assert.Equal(model.Positions, solve.Positions());
        }
    }

    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-8")]
    public void MirroredSkinRotateMirrorsItsNormals()
    {
        var (model, skin, _) = SkinUnderCloth();
        var patch = Cells(skin, 14, 16, 8, 12);
        var twins = Twins(model);
        var how = Mirrored(Kind.Rotate, model.Positions, patch);
        var t = RotateGizmo.About(how.Pivot, Vector3.Normalize(new Vector3(0.6f, 0f, 0.8f)), 0.3f);
        var solve = SkinSolve(model);
        solve.BeginMove(patch, false, 0f, mirror: how);
        solve.TransformTo(t);
        Assert.True(solve.EndMove());
        var map = LinearNormals.NormalMap(t)!.Value;
        foreach (int v in Cells(skin, 15, 15, 9, 11))
        {
            AssertNormal(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, map)), solve.NormalAt(v), 1e-3f, $"picked skin {v}");
            AssertNormal(Reflect(V(solve.NormalAt(v))), solve.NormalAt(twins[v]), 1e-3f, $"its twin {twins[v]}");
        }
    }

    // ── where the drag meets the centre ─────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-1")]
    public void AnOffCentreBodyStillPairsSkinWithSkin()
    {
        var (model, skin, cloth) = SkinUnderCloth(skinFrom: -0.097f);
        var patch = Cells(skin, 14, 16, 8, 12);                    // x 43..63 mm
        var o = new Vector3(0.002f, 0.004f, 0f);
        var solve = SkinSolve(model);
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        Assert.Equal(patch.Length, solve.DragMirror!.Value.Paired);
        solve.MoveTo(o);
        var during = solve.Positions();
        foreach (int v in Cells(skin, 3, 5, 8, 12)) AssertNear(At(model.Positions, v) + Reflect(o), At(during, v), 1e-6f, "matching skin");
        AssertUnmoved(model.Positions, during, Cells(cloth, 0, 1, 0, 20), "cloth far from the matching skin");
        Assert.Equal(0, SkinPoke.MadeWorse(model, during));
        solve.EndMove();
        Assert.Equal(0, SkinPoke.MadeWorse(model, solve.Positions()));
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-5")]
    public void LockedFarSkinIsHeld()
    {
        var (model, skin, cloth) = SkinUnderCloth();
        var patch = Cells(skin, 14, 16, 8, 12);
        var solve = SkinSolve(model);
        solve.SetLocked(Cells(skin, 0, 8, 0, 20));
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        var r = solve.DragMirror!.Value;
        Assert.Equal(r.Selected, r.Held);
        solve.MoveTo(new Vector3(0.002f, 0.004f, 0f));
        var after = solve.Positions();
        AssertUnmoved(model.Positions, after, Cells(skin, 0, 8, 0, 20), "locked skin");
        AssertUnmoved(model.Positions, after, Cells(cloth, 0, 7, 0, 20), "cloth riding it");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-6")]
    public void SkinMirroredAboutACentrePivot()
    {
        var (model, skin, _) = SkinUnderCloth();
        var left = Cells(skin, 4, 6, 8, 12);
        var o = new Vector3(0.002f, 0.004f, 0f);
        var solve = SkinSolve(model);
        var pivot = BoundsCentre(model.Positions, left) with { X = 0f };
        solve.BeginMove(left, false, 0f, mirror: new MoveMirror(Kind.Translate, pivot));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        solve.MoveTo(o);
        var after = solve.Positions();
        foreach (int v in left) AssertNear(At(model.Positions, v) + Reflect(o), At(after, v), 1e-6f, "picked skin left of the pivot");
        foreach (int v in Cells(skin, 14, 16, 8, 12)) AssertNear(At(model.Positions, v) + o, At(after, v), 1e-6f, "its match");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-7")]
    public void SkinFalloffMeasuredFromBothSides()
    {
        var (model, skin, _) = SkinUnderCloth();
        var patch = Cells(skin, 14, 16, 8, 12);
        var twins = Twins(model);
        var solve = SkinSolve(model);
        solve.BeginMove(patch, true, 0.03f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        var w = solve.DragFalloff()!;
        Assert.True(w[skin[2, 10]] > 0f && w[skin[2, 10]] < 1f, "the falloff should reach past the far patch");
        for (int v = 0; v < w.Length; v++)
            if (twins[v] >= 0) Assert.True(MathF.Abs(w[v] - w[twins[v]]) <= 1e-5f, $"vertex {v}: {w[v]} against its twin's {w[twins[v]]}");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-8")]
    public void SkinDraggedToTheCentreNeverCrosses()
    {
        var (model, skin, _) = SkinUnderCloth();
        var patch = Cells(skin, 11, 15, 8, 12);                    // x 10..50 mm
        var solve = SkinSolve(model);
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        solve.MoveTo(new Vector3(-0.04f, 0f, 0f));
        var during = solve.Positions();
        for (int v = 0; v < during.Length / 3; v++)
        {
            float was = At(model.Positions, v).X;
            if (MathF.Abs(was) > 1e-5f) Assert.True(MathF.Sign(At(during, v).X) == MathF.Sign(was), $"vertex {v} crossed the centre");
        }
        AssertNoFolds(model, model.Positions, during, "toward the centre");
        Assert.Equal(0, SkinPoke.MadeWorse(model, during));
        solve.EndMove();
        Assert.Equal(0, SkinPoke.MadeWorse(model, solve.Positions()));
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-10")]
    public void CentreLineSkinStaysOnThePlane()
    {
        var (model, skin, _) = SkinUnderCloth();
        var patch = Cells(skin, 9, 16, 8, 12);                     // spans the centre, mostly on the right
        var solve = SkinSolve(model);
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        solve.MoveTo(new Vector3(0.003f, 0.004f, 0f));
        var after = solve.Positions();
        for (int j = 8; j <= 12; j++)
            Assert.True(MathF.Abs(At(after, skin[10, j]).X - At(model.Positions, skin[10, j]).X) <= 1e-7f,
                        $"centre row {j} went sideways");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-13")]
    public void MirroredSkinPolygonsAlongTheSurface()
    {
        var (model, skin, cloth) = SkinUnderCloth(gap: 0.005f);
        var patch = Cells(skin, 14, 16, 8, 12);
        var o = new Vector3(0f, 0.004f, 0f);
        var solve = SkinSolve(model);
        solve.BeginMove(patch, true, 0.02f, alongSurface: true, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
        var w = solve.DragFalloff()!;
        Assert.True(w[skin[2, 10]] > 0f && w[skin[2, 10]] < 1f, "the falloff should run along the far skin");
        solve.MoveTo(o);
        var after = solve.Positions();
        foreach (int v in Cells(skin, 4, 6, 8, 12)) AssertNear(At(model.Positions, v) + o, At(after, v), 1e-6f, "matching skin");
        AssertUnmoved(model.Positions, after, Cells(cloth, 9, 11, 0, 20), "cloth away from both patches");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-14")]
    public void ABodyModelMirrorsItsSkin()
    {
        var mesh = new MirrorMesh();
        var body = mesh.Grid(Steps(-0.1f, 21), Steps(0f, 21), material: SkinMaterial);
        var model = mesh.Build();
        var patch = Cells(body, 14, 16, 8, 12);
        var o = new Vector3(0.002f, 0.004f, 0f);
        var solve = SkinSolve(model);
        solve.BeginMove(patch, false, 0f, mirror: Mirrored(Kind.Translate, model.Positions, patch));
        solve.MoveTo(o);
        var after = solve.Positions();
        foreach (int v in Cells(body, 4, 6, 8, 12)) AssertNear(At(model.Positions, v) + Reflect(o), At(after, v), 1e-6f, "matching skin");
    }

    [Fact]
    [Trait("Plan", "EDGE-mirror-skin-move-15")]
    public void MirroredSkinMoveGuardsBothSides()
    {
        var l = Layered(21, 0.001f, centred: true);
        var picked = l.Model.Parts[1].Triangles.Where(v => At(l.Model.Positions, v).X is > 0.03f and < 0.07f).ToArray();
        int authored = SkinThrough(l.Model, SkinSolve(l.Model));
        var solve = SkinSolve(l.Model);
        solve.BeginMove(picked, true, 0.02f, mirror: Mirrored(Kind.Translate, l.Model.Positions, picked));
        solve.MoveTo(new Vector3(0f, 0.003f, 0f));
        solve.EndMove();
        Assert.Equal(0, SkinPoke.MadeWorse(l.Model, solve.Positions()));
        Assert.True(SkinThrough(l.Model, solve) <= authored);
        Assert.True(Moved(l.Model, solve.Positions(), l.SkinAt[5, 10]).Y > 0.002f, "the other side should have been lifted too");
    }

    /// <summary>
    /// Skin twice as coarse on one side as on the other, and triangulated otherwise, with the falloff off: all of the
    /// matching skin moves whole, nothing is left behind as a spike, and nothing folds.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-mirror-skin-move-9")]
    public void CoarseAsymmetricSkinWithFalloffOffLeavesNoSpikes()
    {
        var mesh = new MirrorMesh();
        var right = mesh.Grid(Steps(0.01f, 10), Steps(0f, 11), material: SkinMaterial);
        var left = mesh.Grid(Steps(-0.1f, 19, Spacing / 2f), Steps(0f, 21, Spacing / 2f), material: SkinMaterial);
        mesh.Grid(Steps(-0.1f, 41, Spacing / 2f), Steps(0f, 21, Spacing / 2f), y: 0.001f);
        var model = mesh.Build();
        var patch = Cells(right, 3, 5, 4, 6);                       // x 40..60 mm, z 40..60 mm
        var o = new Vector3(0.002f, 0.01f, 0f);

        foreach (var kind in new[] { Kind.Translate, Kind.Rotate })
        {
            var solve = SkinSolve(model);
            var how = Mirrored(kind, model.Positions, patch);
            var turn = RotateGizmo.About(how.Pivot, Vector3.UnitY, 0.2f);
            Func<Vector3, Vector3> mirrored = kind == Kind.Translate
                ? p => p + Reflect(o)
                : p => Vector3.Transform(p, MirrorMotion.Reflect(turn));
            solve.BeginMove(patch, false, 0f, mirror: how);
            Assert.Equal(Mode.Sides, solve.DragMirror?.Mode);
            if (kind == Kind.Translate) solve.MoveTo(o);
            else solve.TransformTo(turn);
            var after = solve.Positions();

            foreach (int v in All(left))
            {
                var p = At(model.Positions, v);
                bool inside = p.X is >= -0.0601f and <= -0.0399f && p.Z is >= 0.0399f and <= 0.0601f;
                if (inside) AssertNear(mirrored(p), At(after, v), 1e-6f, $"{kind}: inside the mirrored region, left behind");
                else if (At(after, v) != p)
                {
                    Assert.True(p.X is >= -0.066f and <= -0.034f && p.Z is >= 0.034f and <= 0.066f,
                                $"{kind}: vertex {v} at {p} moved, more than half an edge outside the mirrored region");
                    AssertNear(mirrored(p), At(after, v), 1e-6f, $"{kind}: the ring around the mirrored region");
                }
            }
            AssertNoFolds(model, model.Positions, after, kind.ToString());
        }
    }
}
