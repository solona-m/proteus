using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Brushing a FACE and saving it. Dawntrail faces carry two tables no garment does — the neck morph table
/// (Patch 7.1) and a table added in Patch 7.2 — between the submesh bone map and the bounding boxes, and a
/// parse that does not walk past them reads the boxes from the wrong bytes. Vanilla faces from the game data,
/// when a game install is present.
/// </summary>
public class FaceModelWriteTests(ITestOutputHelper o)
{
    private static IEnumerable<(string Path, byte[] Bytes)> Faces(ITestOutputHelper o)
    {
        var game = Environment.GetEnvironmentVariable("PROTEUS_GAME")
                ?? @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn";
        var sqpack = Path.Combine(game, "game", "sqpack");
        if (!Directory.Exists(sqpack)) { o.WriteLine($"no game data at {sqpack}"); yield break; }
        var data = new Lumina.GameData(sqpack);
        foreach (var path in new[]
                 {
                     "chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl",
                     "chara/human/c0101/obj/face/f0001/model/c0101f0001_fac.mdl",
                     "chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl",
                 })
            if (data.GetFile(path) is { } file)
                yield return (path, file.Data);
    }

    /// <summary>
    /// The bounding boxes are found where they really are: the model box encloses every LOD0 position, and
    /// from the end of the bone boxes to the vertex data there is nothing but zero padding. Read 300-odd bytes
    /// early, neither holds.
    /// </summary>
    [Fact]
    public void ParseFindsAFacesBoundingBoxes()
    {
        int faces = 0, withNeck = 0;
        foreach (var (path, mdl) in Faces(o))
        {
            faces++;
            var src = SecondSkinWriter.Parse(mdl);
            int neck = mdl[src.Mh + 43], patch72 = BitConverter.ToUInt16(mdl, src.Mh + 48);
            o.WriteLine($"{path}: {neck} neck morph(s), {patch72} patch 7.2 record(s), model box at {src.ModelBBoxAt}");
            if (neck > 0) withNeck++;

            var parts = ModelPartReader.Read(mdl)!;
            var min = new Vector3(BitConverter.ToSingle(mdl, src.ModelBBoxAt), BitConverter.ToSingle(mdl, src.ModelBBoxAt + 4),
                                  BitConverter.ToSingle(mdl, src.ModelBBoxAt + 8));
            var max = new Vector3(BitConverter.ToSingle(mdl, src.ModelBBoxAt + 16), BitConverter.ToSingle(mdl, src.ModelBBoxAt + 20),
                                  BitConverter.ToSingle(mdl, src.ModelBBoxAt + 24));
            // 5 mm of slack: the game's own boxes are not always tight — the Au Ra face ships one 1.8 mm short of a
            // vertex. Bytes read from the wrong place are not a box at all, which this still catches.
            const float slack = 5e-3f;
            for (int v = 0; v < parts.Positions.Length / 3; v++)
            {
                var p = new Vector3(parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]);
                Assert.True(p.X >= min.X - slack && p.Y >= min.Y - slack && p.Z >= min.Z - slack
                         && p.X <= max.X + slack && p.Y <= max.Y + slack && p.Z <= max.Z + slack,
                            $"{path}: vertex {v} {p} outside the model box {min}..{max}");
            }

            int boxesEnd = src.BoneBBoxAt + src.BoneCount * 32;
            int vertexData = (int)BitConverter.ToUInt32(mdl, 16);
            Assert.True(boxesEnd <= vertexData, $"{path}: bone boxes run into the vertex data");
            for (int i = boxesEnd; i < vertexData; i++)
                Assert.True(mdl[i] == 0, $"{path}: byte {i} between the bone boxes and the vertex data is not padding");
        }
        if (faces > 0) Assert.True(withNeck > 0, "no face carried neck morph data, so this proves nothing");
    }

    /// <summary>
    /// A brushed face saves, and touches nothing outside LOD0's vertex bytes and the stored extents — the neck
    /// morph table and the Patch 7.2 table included.
    /// </summary>
    [Fact]
    public void ABrushedFaceSavesAndTouchesNothingElse()
    {
        foreach (var (path, mdl) in Faces(o))
        {
            var parts = ModelPartReader.Read(mdl)!;
            var solve = new MeshVolumeSolve(parts);
            var centre = new Vector3(parts.Positions[0], parts.Positions[1], parts.Positions[2]);
            int moved = solve.Paint(centre, 0.01f, 0.001f);
            solve.EndStroke();
            o.WriteLine($"{path}: moved {moved} node(s), worst {solve.Worst * 1000f:0.###} mm");
            Assert.True(moved > 0, $"{path}: the brush reached nothing");

            var written = MeshVolumeService.Inflate(mdl, solve);
            Assert.Equal(mdl.Length, written.Model.Length);

            var src = SecondSkinWriter.Parse(mdl);
            var allowed = new List<(int Lo, int Hi)>
            {
                (src.ModelBBoxAt, src.ModelBBoxAt + 4 * 32),
                (src.BoneBBoxAt, src.BoneBBoxAt + src.BoneCount * 32),
                (src.Mh, src.Mh + 4),            // Radius
                (src.Mh + 28, src.Mh + 36),      // model and shadow clip distances
            };
            foreach (var span in parts.MeshSpans)
            {
                int mo = src.MeshStart + span.Mesh * 36;
                int vc = BitConverter.ToUInt16(mdl, mo);
                for (int s = 0; s < 3; s++)
                {
                    int at = src.Vb + (int)BitConverter.ToUInt32(mdl, mo + 20 + s * 4);
                    allowed.Add((at, at + vc * mdl[mo + 32 + s]));
                }
            }

            var stray = new List<int>();
            for (int i = 0; i < mdl.Length; i++)
                if (mdl[i] != written.Model[i] && !allowed.Any(r => i >= r.Lo && i < r.Hi))
                    stray.Add(i);
            Assert.True(stray.Count == 0, $"{path}: {stray.Count} byte(s) changed outside the vertex data and extents, first at {stray.FirstOrDefault()}");
        }
    }
}
