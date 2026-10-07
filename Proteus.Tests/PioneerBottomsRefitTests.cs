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
/// Reported: the vanilla Pioneer's Bottoms (e0689 legs) refitted with "take the new body's skin" ticked still drew the
/// vanilla skin — a pale band across the lower back between the coat and the belt. Its skin is one 38-vertex strip;
/// laid onto Rue+ two corners missed the hip by 6 mm (on Neolithe four, by 2-6 mm), and the swap read the strip as a
/// posed piece of the garment's own and kept it. Bibo+ laid every corner and swapped.
/// </summary>
public class PioneerBottomsRefitTests(ITestOutputHelper output)
{
    private const string Bottoms = "chara/equipment/e0689/model/c0201e0689_dwn.mdl";
    private const string VanillaLegs = "chara/equipment/e0000/model/c0201e0000_dwn.mdl";

    [LocalDataTheory(LocalData.GameData, LocalData.Mods + ":hs-Rue+-2.2.7-y0f", LocalData.Mods + ":Neolithe [ALL IN ONE]",
                     LocalData.Mods + ":Bibo+")]
    [InlineData("hs-Rue+-2.2.7-y0f")]
    [InlineData("Neolithe [ALL IN ONE]")]
    [InlineData("Bibo+")]
    public void The_vanilla_waist_skin_is_swapped_for_the_body_s(string bodyMod)
    {
        var game = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        byte[] garmentBytes = game.GetFile(Bottoms)!.Data;
        byte[] sourceBytes = game.GetFile(VanillaLegs)!.Data;

        var catalog = BodySizeCatalog.Read(LocalData.Path(LocalData.Mods + ":" + bodyMod));
        byte[] targetBytes = File.ReadAllBytes(catalog.PathOf(catalog.For("_dwn").First()));
        var source = ModelPartReader.Read(sourceBytes)!;
        var target = ModelPartReader.Read(targetBytes)!;
        var remap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
        Assert.True(BodyCorrespondence.TryBuild(source, Uv(sourceBytes), target, Uv(targetBytes), "_dwn", out var built,
                                                out string refusal, remap), refusal);
        var pairs = new List<BodyRetarget.SlotPair> { new("_dwn", built!, target, targetBytes, sourceBytes) };

        var garment = ModelPartReader.Read(garmentBytes)!;
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_dwn", replaceSkin: true, acrossBodies: true);
        output.WriteLine($"{planned.Report.Swap}");

        var swap = planned.Report.Swap!.Value;
        Assert.Equal(30, swap.Removed);
        Assert.Equal(0, swap.Kept);
        var refit = ModelPartReader.Read(planned.Model)!;
        Assert.DoesNotContain(refit.Parts, p => p.Material == "/mt_c0201b0001_a.mtrl");
    }

    /// <summary>
    /// Reported: refitted onto Neolithe Gen C Small, the buttock showed through the shorts' hem in teeth and the stocking
    /// tops' corners stood out of the thigh as black spikes. The vanilla strip of skin stops a few millimetres under both;
    /// the cut kept Neolithe's skin a whole body triangle past it, under faces 60-75 mm across lying on the skin. Skin the
    /// cloth crowds — within 0.5 mm under it, or already through it — is what shows.
    /// </summary>
    [LocalDataFact(LocalData.GameData, LocalData.Mods + ":Neolithe [ALL IN ONE]")]
    public void The_new_skin_stops_under_the_hem_instead_of_showing_through_it()
    {
        var refit = RefitOntoNeolitheGenCSmall();

        var cloth = new List<(Vector3, Vector3, Vector3)>();
        var skin = new List<(Vector3 A, Vector3 B, Vector3 C, Vector3 Out)>();
        foreach (var part in refit.Parts.Where(p => p.Island < 0))
        {
            bool isSkin = SecondSkinWriter.IsBodySkinMaterial(part.Material);
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (!isSkin) cloth.Add((At(refit, a), At(refit, b), At(refit, c)));
                else skin.Add((At(refit, a), At(refit, b), At(refit, c), NormalAt(refit, a) + NormalAt(refit, b) + NormalAt(refit, c)));
            }
        }

        // Skin through the cloth, by AREA: it shows between the cloth's corners, through the middles of its faces, which
        // no count of skin vertices sees. Each skin triangle sampled on a grid; a sample is through when cloth crosses
        // the line under it, within 10 mm behind the skin.
        const int Grid = 6;
        float through = 0f;
        foreach (var (a, b, c, outward) in skin)
        {
            float area = Vector3.Cross(b - a, c - a).Length() / 2f;
            if (area < 1e-10f || outward.LengthSquared() < 1e-12f) continue;
            var normal = Vector3.Normalize(outward);
            int samples = 0, hits = 0;
            for (int i = 0; i <= Grid; i++)
                for (int j = 0; i + j <= Grid; j++)
                {
                    samples++;
                    var p = a + (b - a) * ((i + 1f / 3f) / (Grid + 1f)) + (c - a) * ((j + 1f / 3f) / (Grid + 1f));
                    if (cloth.Any(t => Behind(p, normal, t))) hits++;
                }
            through += area * hits / samples;
        }
        float mm2 = through * 1e6f;
        output.WriteLine($"skin through the cloth: {mm2:0} mm²");
        Assert.True(mm2 < ThroughLimit, $"{mm2:0} mm² of skin shows through the cloth");
    }

    /// <summary>
    /// Reported next (build 1133): the background through a slit between the skin and the shorts' hem at the back. The
    /// hem's corners are welded to the vanilla strip, so the refit laid them flat on Neolithe's rounder buttock as if they
    /// were skin, and the hem's edges sank 5 mm into it between them; skin cut back from the buried edge left a gap
    /// under it. Looked at from behind, a slit is a point whose nearest surface is far behind the surface around it,
    /// beside the skin.
    /// </summary>
    [LocalDataFact(LocalData.GameData, LocalData.Mods + ":Neolithe [ALL IN ONE]")]
    public void No_slit_opens_between_the_hem_and_the_skin_at_the_back()
    {
        var refit = RefitOntoNeolitheGenCSmall();

        // From behind (looking along +z, nearest = least z), the band from the stocking tops to above the hem.
        const float Step = 0.0005f, X0 = -0.16f, X1 = 0.16f, Y0 = 0.80f, Y1 = 0.90f;
        int cols = (int)((X1 - X0) / Step), rows = (int)((Y1 - Y0) / Step);
        var depth = new float[cols, rows];
        var isSkin = new bool[cols, rows];
        for (int i = 0; i < cols; i++)
            for (int j = 0; j < rows; j++)
                depth[i, j] = float.PositiveInfinity;

        foreach (var part in refit.Parts.Where(p => p.Island < 0))
        {
            bool skin = SecondSkinWriter.IsBodySkinMaterial(part.Material);
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                Vector3 a = At(refit, part.Triangles[t]), b = At(refit, part.Triangles[t + 1]), c = At(refit, part.Triangles[t + 2]);
                int i0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.X, MathF.Min(b.X, c.X)) - X0) / Step));
                int i1 = Math.Min(cols - 1, (int)MathF.Ceiling((MathF.Max(a.X, MathF.Max(b.X, c.X)) - X0) / Step));
                int j0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - Y0) / Step));
                int j1 = Math.Min(rows - 1, (int)MathF.Ceiling((MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - Y0) / Step));
                float den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(den) < 1e-14f) continue;
                for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                    {
                        float x = X0 + (i + 0.5f) * Step, y = Y0 + (j + 0.5f) * Step;
                        float wa = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / den;
                        float wb = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / den;
                        float wc = 1f - wa - wb;
                        if (wa < 0f || wb < 0f || wc < 0f) continue;
                        float z = wa * a.Z + wb * b.Z + wc * c.Z;
                        if (z >= depth[i, j]) continue;
                        depth[i, j] = z;
                        isSkin[i, j] = skin;
                    }
            }
        }

        const int Around = 6;   // 3 mm
        int slit = 0;
        for (int i = 0; i < cols; i++)
            for (int j = 0; j < rows; j++)
            {
                if (float.IsPositiveInfinity(depth[i, j])) continue;
                float nearest = depth[i, j];
                bool besideSkin = false;
                for (int di = -Around; di <= Around; di++)
                    for (int dj = -Around; dj <= Around; dj++)
                    {
                        int u = i + di, v = j + dj;
                        if (u < 0 || v < 0 || u >= cols || v >= rows) continue;
                        nearest = MathF.Min(nearest, depth[u, v]);
                        besideSkin |= isSkin[u, v];
                    }
                if (besideSkin && depth[i, j] - nearest > 0.025f) slit++;
            }
        float mm2 = slit * Step * Step * 1e6f;
        output.WriteLine($"slit under the hem, from behind: {mm2:0} mm²");
        Assert.True(mm2 < SlitLimit, $"{mm2:0} mm² of slit between the hem and the skin");
    }

    /// <summary>
    /// The most slit the test allows. Measured: 110 mm² as build 1133 had it (hem fused to the strip, no rim lift, the
    /// same clearance under every face), 36 fixed — the corners between the legs, where the inner thigh turns away.
    /// </summary>
    private const float SlitLimit = 70f;

    /// <summary>The vanilla Pioneer's Bottoms refitted onto Neolithe DEFAULT Gen C Small, the skin swapped, as reported.</summary>
    private static ModelParts RefitOntoNeolitheGenCSmall()
    {
        var game = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        byte[] garmentBytes = game.GetFile(Bottoms)!.Data;
        byte[] sourceBytes = game.GetFile(VanillaLegs)!.Data;
        string neo = LocalData.Path(LocalData.Mods + ":Neolithe [ALL IN ONE]");
        byte[] targetBytes = File.ReadAllBytes(Path.Combine(neo, "default legs - smallclothes", "gen c small.mdl"));
        var remap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
        Assert.True(BodyCorrespondence.TryBuild(ModelPartReader.Read(sourceBytes)!, Uv(sourceBytes),
                                                ModelPartReader.Read(targetBytes)!, Uv(targetBytes), "_dwn", out var built,
                                                out string refusal, remap), refusal);
        var pairs = new List<BodyRetarget.SlotPair>
        {
            new("_dwn", built!, ModelPartReader.Read(targetBytes)!, targetBytes, sourceBytes),
        };
        var planned = BodyRetarget.Plan(ModelPartReader.Read(garmentBytes)!, garmentBytes, pairs, "_dwn",
                                        replaceSkin: true, acrossBodies: true);
        return ModelPartReader.Read(planned.Model)!;
    }

    /// <summary>
    /// The most skin area through the cloth the test allows. Measured: 9,584 mm² with the kept skin left a body triangle
    /// past the author's edge; 3,496 pulled back to where the cloth crowds it plus a fixed 3 mm tuck under the rims (the
    /// dark notches seen in game); 142 walked out only as far as the cloth leaves room; 13 with the hem no longer welded
    /// to the swapped-out strip and its rim lifted off the buttock.
    /// </summary>
    private const float ThroughLimit = 500f;

    /// <summary>Whether cloth triangle <paramref name="tri"/> crosses the line under skin point <paramref name="p"/>
    /// within 10 mm behind it, looking along <paramref name="n"/> — the skin is in front of that cloth.</summary>
    private static bool Behind(Vector3 p, Vector3 n, (Vector3 A, Vector3 B, Vector3 C) tri)
    {
        const float Depth = 0.01f;
        var lo = Vector3.Min(tri.A, Vector3.Min(tri.B, tri.C)) - new Vector3(Depth);
        var hi = Vector3.Max(tri.A, Vector3.Max(tri.B, tri.C)) + new Vector3(Depth);
        if (p.X < lo.X || p.Y < lo.Y || p.Z < lo.Z || p.X > hi.X || p.Y > hi.Y || p.Z > hi.Z) return false;
        return Crosses(p - n * Depth, n, tri, out float t) && t < Depth - 0.0003f;
    }

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    private static Vector3 NormalAt(ModelParts m, int v) => new(m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2]);

    /// <summary>Möller–Trumbore, both faces, forward of <paramref name="o"/>.</summary>
    private static bool Crosses(Vector3 o, Vector3 d, (Vector3 A, Vector3 B, Vector3 C) tri, out float t)
    {
        t = 0f;
        var e1 = tri.B - tri.A;
        var e2 = tri.C - tri.A;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        var s = o - tri.A;
        float u = Vector3.Dot(s, p) / det;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        float w = Vector3.Dot(d, q) / det;
        if (w < 0f || u + w > 1f) return false;
        t = Vector3.Dot(e2, q) / det;
        return t >= 0f;
    }

    private static float[] Uv(byte[] mdl)
        => SecondSkinWriter.TryReadLod0Geometry(mdl, out _, out var uv, out _, out _, out _, false, false, null) ? uv : [];
}
