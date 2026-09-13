using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Adding the wind channel — a mesh's second vertex colour — to a model in place, and writing wind into it.
/// The edit grows every LOD0 vertex record, so these check that nothing else in the file noticed.
/// </summary>
public class VertexColorWriterTests(ITestOutputHelper o)
{
    private static IEnumerable<(string Name, byte[] Bytes)> Models(ITestOutputHelper o)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Meshes");
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "*.mdl"))
                yield return (Path.GetFileName(f), File.ReadAllBytes(f));

        // Vanilla gear too, when a game install is present: three LODs, so the offsets past LOD0 get exercised.
        var game = Environment.GetEnvironmentVariable("PROTEUS_GAME")
                ?? @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn";
        var sqpack = Path.Combine(game, "game", "sqpack");
        if (!Directory.Exists(sqpack)) { o.WriteLine($"no game data at {sqpack}"); yield break; }
        var data = new Lumina.GameData(sqpack);
        foreach (var path in new[]
                 {
                     "chara/equipment/e0000/model/c0201e0000_top.mdl",
                     "chara/equipment/e0233/model/c0201e0233_top.mdl",
                     "chara/equipment/e6058/model/c0101e6058_dwn.mdl",
                 })
            if (data.GetFile(path) is { } file)
                yield return (path, file.Data);
    }

    /// <summary>
    /// The channel goes in and nothing else moves: positions, normals and triangles read back identical, shape
    /// keys survive, the lower LODs' vertex and index bytes are untouched, and the new colour reads as still.
    /// </summary>
    [Fact]
    public void AddingTheWindChannelChangesNothingElse()
    {
        int models = 0;
        foreach (var (name, before) in Models(o))
        {
            models++;
            var partsBefore = ModelPartReader.Read(before)!;
            var after = VertexColorWriter.EnsureSecondColor(before, out var report);
            o.WriteLine($"{name}: added to {report.MeshesAdded} mesh(es), refused {report.MeshesRefused}, " +
                        $"first colour added {report.AddedFirstColor}, {before.Length} -> {after.Length} bytes");

            Assert.True(VertexColorWriter.HasWindChannel(after) || report.MeshesRefused > 0, $"{name}: no wind channel");
            var partsAfter = ModelPartReader.Read(after);
            Assert.NotNull(partsAfter);
            Assert.Equal(partsBefore.Positions, partsAfter!.Positions);
            Assert.Equal(partsBefore.Normals, partsAfter.Normals);
            Assert.Equal(partsBefore.Parts.Select(p => p.Triangles.Length), partsAfter.Parts.Select(p => p.Triangles.Length));
            for (int p = 0; p < partsBefore.Parts.Count; p++)
                Assert.Equal(partsBefore.Parts[p].Triangles, partsAfter.Parts[p].Triangles);

            var srcBefore = SecondSkinWriter.Parse(before);
            var srcAfter = SecondSkinWriter.Parse(after);
            Assert.Equal(srcBefore.Shapes.Keys.OrderBy(k => k), srcAfter.Shapes.Keys.OrderBy(k => k));
            foreach (var (shape, entries) in srcBefore.Shapes)
                Assert.Equal(entries.SelectMany(e => e.Values), srcAfter.Shapes[shape].SelectMany(e => e.Values));

            // Everything past LOD0's vertex buffer is the same bytes, just further along.
            AssertSameRegion(before, after, 28, 52);            // index buffer 0
            AssertSameRegion(before, after, 20, 44);            // vertex buffer 1
            AssertSameRegion(before, after, 32, 56);            // index buffer 1
            AssertSameRegion(before, after, 24, 48);            // vertex buffer 2
            AssertSameRegion(before, after, 36, 60);            // index buffer 2

            // Still, everywhere it was added.
            for (int m = srcAfter.Lod0MeshIndex; m < srcAfter.Lod0MeshIndex + srcAfter.Lod0MeshCount; m++)
            {
                if (srcBefore.Decls[m].Any(VertexColorWriter.IsSecondColor)) continue;
                int vc = BitConverter.ToUInt16(after, srcAfter.MeshStart + m * 36);
                for (int k = 0; k < vc; k++)
                    if (VertexColorWriter.ReadWind(after, srcAfter, m, k) is { } w)
                        Assert.Equal(0f, w);
            }

            // A second pass has nothing to add.
            var again = VertexColorWriter.EnsureSecondColor(after, out var second);
            Assert.Equal(0, second.MeshesAdded);
            Assert.Same(after, again);
        }
        Assert.True(models > 0);
    }

    /// <summary>Wind lands in the red byte and only there; a shape key's spare takes its base vertex's value.</summary>
    [Fact]
    public void WindIsWrittenToRedOnly()
    {
        foreach (var (name, before) in Models(o))
        {
            var parts = ModelPartReader.Read(before)!;
            var withChannel = VertexColorWriter.EnsureSecondColor(before, out _);
            var painted = (byte[])withChannel.Clone();
            float Wind(int v) => v % 3 == 0 ? 1f : 0.5f;
            int written = VertexColorWriter.WriteWind(painted, parts.MeshSpans, Wind);
            o.WriteLine($"{name}: wrote {written} vertices");

            var src = SecondSkinWriter.Parse(painted);
            int changedOutsideRed = 0;
            var redBytes = new HashSet<int>();
            foreach (var span in parts.MeshSpans)
            {
                var ce = src.Decls[span.Mesh].FirstOrDefault(VertexColorWriter.IsSecondColor);
                if (ce.Usage != 7) continue;
                int mo = src.MeshStart + span.Mesh * 36;
                uint vbo = BitConverter.ToUInt32(painted, mo + 20 + ce.Stream * 4);
                byte stride = painted[mo + 32 + ce.Stream];
                for (int k = 0; k < span.Count; k++)
                {
                    int at = (int)(src.Vb + vbo) + k * stride + ce.Offset;
                    redBytes.Add(at);
                    var w = VertexColorWriter.ReadWind(painted, src, span.Mesh, k)!.Value;
                    bool spare = src.Shapes.Values.SelectMany(e => e).SelectMany(e => e.Values).Any(v => v.Replace == k);
                    if (!spare) Assert.Equal(MathF.Round(Wind(span.BaseVertex + k) * 255f) / 255f, w, 1e-4f);
                }
            }
            for (int i = 0; i < painted.Length; i++)
                if (painted[i] != withChannel[i] && !redBytes.Contains(i)) changedOutsideRed++;
            Assert.Equal(0, changedOutsideRed);
        }
    }

    /// <summary>
    /// A region the header addresses by offset (<paramref name="offsetAt"/>) and LOD size (hdr
    /// <paramref name="sizeAt"/>) holds the same bytes before and after, wherever it now starts.
    /// </summary>
    private static void AssertSameRegion(byte[] before, byte[] after, int offsetAt, int sizeAt)
    {
        uint size = BitConverter.ToUInt32(before, sizeAt);
        if (size == 0) return;
        Assert.Equal(size, BitConverter.ToUInt32(after, sizeAt));
        int a = (int)BitConverter.ToUInt32(before, offsetAt), b = (int)BitConverter.ToUInt32(after, offsetAt);
        Assert.True(before.AsSpan(a, (int)size).SequenceEqual(after.AsSpan(b, (int)size)),
                    $"region at header+{offsetAt} changed");
    }
}
