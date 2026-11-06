using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Proteus.Gui;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The "All" mode of Move and Rotate on a real game top — several cloth pieces, its own skin, shape-key spares and
/// welded seams — measured inside the pass, from <see cref="MeshVolumeSolve.Positions"/>, never from a re-exported model:
/// every point either takes the one offset or matrix, or stays because it is skin, locked or welded to either.
/// </summary>
public class MoveAllLocalTests(ITestOutputHelper output)
{
    private static Vector3 At(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);

    private static bool IsSkin(ModelPart p) => SecondSkinWriter.IsBodySkinMaterial(p.Material);

    /// <summary>Cloth to lock that leaves other cloth moving: the second-largest cloth submesh, or with only one, its
    /// smallest island; null when the model has neither.</summary>
    private static ModelPart? ClothToLock(ModelParts model)
    {
        var cloth = model.Parts.Where(p => p.Island < 0 && !IsSkin(p)
                                           && !p.Material.Contains("piercing", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(p => p.Triangles.Length).ToList();
        if (cloth.Count >= 2) return cloth[1];
        return cloth.Count == 0 ? null
             : model.Parts.Where(p => p.Island >= 0 && p.Mesh == cloth[0].Mesh && p.Submesh == cloth[0].Submesh)
                          .OrderBy(p => p.Triangles.Length).FirstOrDefault();
    }

    /// <summary>Game tops with several cloth pieces and their own skin, as the Studio opens them.</summary>
    private static readonly string[] GameTops = ["Witch's Coatee", "Neo-Ishgardian Top of Maiming", "Scion Adventurer's"];

    /// <summary>The first game top with cloth to lock beside other cloth. (Neolithe's chests carry one cloth piece
    /// with no islands, so nothing could be locked while other cloth moves.)</summary>
    private static ModelParts TopWithClothToLock()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        foreach (string name in GameTops)
            if (ModelPartReader.Read(VanillaGearRefitLocalTests.TopOf(data, name)) is { } m && ClothToLock(m) != null)
                return m;
        throw new InvalidOperationException("no game top has cloth to lock beside other cloth");
    }

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-move-all-parts-2")]
    public void AllMoveAndRotateOnARealModelAreOnePiece()
    {
        var model = TopWithClothToLock();
        var pov = PartsPanel.BuildPartOfVertex(model);
        int vc = model.Positions.Length / 3;

        // Lock some cloth, so the move has a held part to leave behind.
        var lockedPart = ClothToLock(model)!;
        output.WriteLine($"{model.Parts.Count} parts, {vc} vertices; locked {lockedPart.Label} ({lockedPart.Material})");

        ModelPart? ParentOf(ModelPart p)
            => p.Island < 0 ? null : model.Parts.FirstOrDefault(q => q.Island < 0 && q.Mesh == p.Mesh && q.Submesh == p.Submesh);
        bool IsLocked(ModelPart p) => p.Label == lockedPart.Label || ParentOf(p)?.Label == lockedPart.Label;

        var solve = new MeshVolumeSolve(model);
        solve.SetLocked(lockedPart.Triangles.Distinct());
        var sel = new MoveAllSelection();
        sel.Update(model, solve, pov, p => MoveAllSelection.Moves(IsSkin(p), IsLocked(p), false), solve.LockVersion, false);

        // Preconditions: something moves, and some cloth is held.
        Assert.NotEmpty(sel.Seeds);
        Assert.Contains(lockedPart.Triangles, v => !IsSkin(model.Parts[pov[v]]));
        var seeds = sel.Seeds.ToHashSet();

        // Which vertices share a solve node: the solve welds by position.
        var vec = new SecondSkinWriter.Vec3[vc];
        for (int v = 0; v < vc; v++) vec[v] = new(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
        int[] nodeOf = MeshMath.WeldByPosition(vec, out int nodes);
        var heldNode = new bool[nodes];
        var seedNode = new bool[nodes];
        for (int v = 0; v < vc; v++)
        {
            if (pov[v] >= 0 && (IsSkin(model.Parts[pov[v]]) || IsLocked(model.Parts[pov[v]]))) heldNode[nodeOf[v]] = true;
            if (seeds.Contains(v)) seedNode[nodeOf[v]] = true;
        }

        void Check(float[] before, float[] during, Func<Vector3, Vector3> expected, string what)
        {
            // The nodes a part vertex moved at — spares left out, so a spare alone at its node must stay.
            var movedNode = new bool[nodes];
            for (int v = 0; v < vc; v++)
                if (pov[v] >= 0 && Vector3.Distance(At(before, v), At(during, v)) > 1e-6f) movedNode[nodeOf[v]] = true;

            int carried = 0, held = 0, spares = 0;
            for (int v = 0; v < vc; v++)
            {
                var was = At(before, v);
                var now = At(during, v);
                bool stayed = Vector3.Distance(was, now) <= 1e-6f;
                if (pov[v] < 0)
                {
                    // A shape-key spare: carried exactly when its solve node moved.
                    spares++;
                    Assert.True(stayed != movedNode[nodeOf[v]], $"{what}: spare {v} stayed={stayed}");
                    continue;
                }
                bool tookIt = Vector3.Distance(expected(was), now) <= 1e-5f;
                Assert.True(tookIt || stayed, $"{what}: vertex {v} moved {now - was}, neither the transform nor still");
                if (seeds.Contains(v) && stayed && !tookIt)
                {
                    held++;
                    Assert.True(heldNode[nodeOf[v]], $"{what}: seed {v} stayed but is neither skin, locked nor welded to them");
                }
                else if (!seeds.Contains(v) && !stayed)
                    Assert.True(seedNode[nodeOf[v]], $"{what}: non-seed {v} moved without sharing a node with a seed");
                if (tookIt && !stayed) carried++;
            }
            output.WriteLine($"{what}: {carried} carried, {held} seeds held at welds, {spares} spares");
            Assert.True(carried > 0, $"{what}: nothing moved");
        }

        // Move.
        var start = solve.Positions().ToArray();
        var offset = new Vector3(0.012f, 0.004f, -0.009f);
        Assert.True(solve.BeginMove(sel.Seeds, MoveAllSelection.AdjacentApplies(MoveSelectMode.All, true), 0.05f) > 0);
        solve.MoveTo(offset);
        Check(start, solve.Positions().ToArray(), p => p + offset, "All Move");
        var sw = Stopwatch.StartNew();
        Assert.True(solve.EndMove());
        output.WriteLine($"All Move EndMove: {sw.Elapsed.TotalMilliseconds:F1} ms");

        // Rotate, after taking the move back.
        solve.Undo();
        var back = solve.Positions().ToArray();
        for (int i = 0; i < start.Length; i++) Assert.Equal(start[i], back[i], 5);
        var c = MoveAllSelection.BoundsCentre(sel.Seeds, back)!.Value;
        var turn = Matrix4x4.CreateTranslation(-c) * Matrix4x4.CreateFromYawPitchRoll(0.25f, 0.1f, -0.05f)
                   * Matrix4x4.CreateTranslation(c);
        Assert.True(solve.BeginMove(sel.Seeds, false, 0.05f) > 0);
        solve.TransformTo(turn);
        Check(back, solve.Positions().ToArray(), p => Vector3.Transform(p, turn), "All Rotate");
        sw.Restart();
        Assert.True(solve.EndMove());
        output.WriteLine($"All Rotate EndMove: {sw.Elapsed.TotalMilliseconds:F1} ms");
    }
}
