using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Proteus.Services.Bulge;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>The folds a featureless refit may keep and still be taken without comparing it to today's (3).</summary>
    internal const int BulgeResidual = 3;

    /// <summary>How near the new body's genitals a source skin point must land to be the crotch the garment must cover (15 mm).</summary>
    internal const float BulgeCrotchReach = 0.015f;

    /// <summary>How near that crotch a garment's cloth must lie to cover it (20 mm), and how many points must (20).</summary>
    internal const float BulgeCoverReach = 0.020f;
    internal const int BulgeCoverPoints = 20;

    /// <summary>How near the new body's genitals the old body's smallclothes must lie to stand in for its crotch (3 cm).</summary>
    internal const float BulgeBriefsReach = 0.03f;

    /// <summary>How near the old body's genitals a garment's own skin triangle must lie, every corner, to be them (2 mm).</summary>
    internal const float BulgeGarmentGenitalReach = 0.002f;

    private static readonly Regex BodyModMaterial = new(@"(^|/)mt_c\d{4}b\d{4}_", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NotGenital = new("pube|pierc|nail", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The featureless bulge (plan featureless-bulge): for each legs pair onto a body with a penis that the garment covers,
    /// the pair onto that body made featureless (<see cref="SlotPair.Featureless"/>). The garment's own genitals — a body
    /// mod's genital material it carries, its own skin over the old body's genitals — are collapsed in place (length- and
    /// index-neutral, so held parts, kept pieces and the edit still index the garment as given). A garment whose own skin
    /// carries genitals keeps today's refit when its skin is not swapped: collapsed, its skin would show a hole there.
    /// Every pair it leaves alone says why in the log.
    /// </summary>
    /// <param name="any">Whether any pair was made featureless.</param>
    internal static IReadOnlyList<SlotPair> FitBulges(ModelParts garment, byte[] garmentBytes, IReadOnlyList<SlotPair> pairs,
                                                      bool replaceSkin, Action<string>? log, out ModelParts fitGarment,
                                                      out byte[] fitBytes, out bool any)
    {
        fitGarment = garment;
        fitBytes = garmentBytes;
        any = false;
        var result = pairs.ToList();
        var dropParts = new HashSet<ModelPart>();
        var dropTris = new Dictionary<ModelPart, HashSet<int>>();
        for (int i = 0; i < result.Count; i++)
        {
            var pair = result[i];
            if (pair.Featureless == null) continue;
            string slot = pair.Slot;
            try
            {
                if (!MayTakeBulge(pair.Target)) continue;   // no piece the bulge could replace: today's refit, at no cost
                var targetSkin = pair.TargetModel != null ? ModelSkinReader.Read(pair.TargetModel, null, null) : null;
                var targetReading = targetSkin != null ? GenitalParts.Read(pair.Target, targetSkin) : null;
                if (targetReading is not { Penis: true }) continue;   // no penis: today's refit, said nowhere — every women's refit
                if (!Covers(garment, pair, targetReading))
                {
                    log?.Invoke($"bulge: {slot} left as it is — crotch not covered");
                    continue;
                }
                var (fp, why) = pair.Featureless();
                if (fp is not { } featureless)
                {
                    log?.Invoke($"bulge: {slot} left as it is — {why}");
                    continue;
                }

                // The garment's own genitals, against the body it was made on.
                var sourceSkin = pair.SourceModel != null ? ModelSkinReader.Read(pair.SourceModel, null, null) : null;
                var sourceReading = sourceSkin != null ? GenitalParts.Read(pair.Correspondence.Source, sourceSkin) : null;
                var (materials, skinTris) = GarmentGenitals(garment, pair.Correspondence.Source, sourceReading);
                if (skinTris.Count > 0 && !(replaceSkin && pair.Swaps))
                {
                    log?.Invoke($"bulge: {slot} left as it is — garment carries genitals and this refit keeps its skin");
                    continue;
                }
                dropParts.UnionWith(materials);
                foreach (var (part, tris) in skinTris)
                    (dropTris.TryGetValue(part, out var set) ? set : dropTris[part] = []).UnionWith(tris);
                int genitalTris = targetReading.Genitals.Sum(g => g.Triangles.Count);
                log?.Invoke($"bulge: {slot} featureless — {genitalTris} genital triangles out (garment: "
                            + $"{materials.Sum(p => p.Triangles.Length / 3) + skinTris.Sum(kv => kv.Value.Count)}), "
                            + $"bulge {TbseBulge.Shared?.Data.triangles.Length / 3 ?? 0} triangles drawn");
                result[i] = featureless;
                any = true;
            }
            catch (Exception e)
            {
                log?.Invoke($"bulge: {slot} left as it is — failed: {e.Message}");
            }
        }
        if (!any || (dropParts.Count == 0 && dropTris.Count == 0)) return result;

        // Collapse the garment's genitals in a copy of its bytes, and leave them out of the parts the refit reads.
        fitBytes = (byte[])garmentBytes.Clone();
        var hide = new HashSet<PolygonSelection.Key>();
        foreach (var part in dropParts)
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                hide.Add(PolygonSelection.Key.Of(part.Triangles[t], part.Triangles[t + 1], part.Triangles[t + 2]));
        foreach (var (part, tris) in dropTris)
            foreach (int t in tris)
                hide.Add(PolygonSelection.Key.Of(part.Triangles[t * 3], part.Triangles[t * 3 + 1], part.Triangles[t * 3 + 2]));
        ModelPartHider.Hide(fitBytes, garment.MeshSpans, hide);
        var kept = new List<ModelPart>();
        foreach (var part in garment.Parts)
        {
            if (dropParts.Contains(part)) continue;
            if (!dropTris.TryGetValue(part, out var gone)) { kept.Add(part); continue; }
            var keepT = Enumerable.Range(0, part.Triangles.Length / 3).Where(t => !gone.Contains(t)).ToList();
            if (keepT.Count == 0) continue;
            kept.Add(new ModelPart
            {
                Mesh = part.Mesh, Submesh = part.Submesh, Island = part.Island, Label = part.Label, Material = part.Material,
                Triangles = keepT.SelectMany(t => new[] { part.Triangles[t * 3], part.Triangles[t * 3 + 1], part.Triangles[t * 3 + 2] }).ToArray(),
                Ordinals = keepT.Select(t => part.Ordinals[t]).ToArray(), AttributeMask = part.AttributeMask,
                Min = part.Min, Max = part.Max, Toggleable = part.Toggleable, AuthorSwitched = part.AuthorSwitched,
            });
        }
        fitGarment = new ModelParts
        {
            Positions = garment.Positions, Normals = garment.Normals, MeshSpans = garment.MeshSpans, Parts = kept,
            AttributeNames = garment.AttributeNames, Min = garment.Min, Max = garment.Max,
            ShatteredSubmeshes = garment.ShatteredSubmeshes, Wind = garment.Wind, HasWindChannel = garment.HasWindChannel,
            FirstColorNotWhite = garment.FirstColorNotWhite,
        };
        return result;
    }

    private static Vector3 PointOf(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>
    /// The cheap gate before any reading: the body has two untagged skin submeshes or more (its main skin and a piece beside
    /// it), one with room for the bulge (as many triangles) whose bounds lie about the bulge's hole (within 15 cm of its
    /// centre). Every other legs refit — a body whose untagged skin is its main skin alone, as every bibo women's body's
    /// is — passes by at the cost of a bounds test.
    /// </summary>
    internal static bool MayTakeBulge(ModelParts body)
    {
        if (TbseBulge.Shared is not { } bulge) return false;
        var untagged = body.Parts.Where(p => p.Island < 0 && p.AttributeMask == 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
        if (untagged.Count < 2) return false;   // one untagged skin submesh: the main skin alone, no piece beside it
        var centre = bulge.Hole.Aggregate(Vector3.Zero, (s, p) => s + p) / bulge.Hole.Length;
        return untagged.Any(p => p.Triangles.Length / 3 >= bulge.Data.triangles.Length / 3
                                 && Vector3.Distance((p.Min + p.Max) / 2, centre) < 0.15f);
    }

    /// <summary>
    /// Whether the garment covers the new body's genitals: the old body's skin points that land within
    /// <see cref="BulgeCrotchReach"/> of them, and at least <see cref="BulgeCoverPoints"/> of the garment's cloth points
    /// within <see cref="BulgeCoverReach"/> of those.
    /// </summary>
    internal static bool Covers(ModelParts garment, SlotPair pair, GenitalParts.Reading target)
    {
        var genital = target.Genitals.SelectMany(g => g.Triangles.SelectMany(t => new[]
                      { g.Part.Triangles[t * 3], g.Part.Triangles[t * 3 + 1], g.Part.Triangles[t * 3 + 2] }))
                      .Distinct().Select(v => PointOf(pair.Target, v)).ToList();
        if (genital.Count == 0) return false;
        var near = new SpatialSet(genital, BulgeCrotchReach);
        var source = pair.Correspondence.Source;
        var field = pair.Correspondence.Field;
        var crotch = new List<Vector3>();
        foreach (var part in source.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach (int v in part.Triangles.Distinct())
            {
                if (v >= field.Count || field[v] is not { } d) continue;
                var p = PointOf(source, v);
                if (near.Any(p + new Vector3(d.X, d.Y, d.Z), BulgeCrotchReach)) crotch.Add(p);
            }
        // A body that draws smallclothes over a hole at its crotch (TBSE SFW's briefs) has no skin there to land: its
        // smallclothes within 3 cm of the new body's genitals count as its crotch too — bodies of one family share their space.
        var briefs = new SpatialSet(genital, BulgeBriefsReach);
        crotch.AddRange(source.Parts.Where(p => p.Island < 0 && BodySurface.IsSmallclothes(p.Material))
                              .SelectMany(p => p.Triangles).Distinct().Select(v => PointOf(source, v))
                              .Where(p => briefs.Any(p, BulgeBriefsReach)));
        if (crotch.Count == 0) return false;
        var crotchSet = new SpatialSet(crotch, BulgeCoverReach);
        int covering = garment.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                              .SelectMany(p => p.Triangles).Distinct()
                              .Count(v => crotchSet.Any(PointOf(garment, v), BulgeCoverReach));
        return covering >= BulgeCoverPoints;
    }

    /// <summary>
    /// The garment's own genitals: meshes of a body mod's genital material (a body mod's material that is not skin, pubic
    /// hair, a piercing or nails — thong pouches, codpieces and buckles are the garment's own materials and stay), and
    /// its own skin triangles every corner of which lies on the old body's genitals.
    /// </summary>
    internal static (List<ModelPart> Materials, Dictionary<ModelPart, List<int>> SkinTris) GarmentGenitals(
        ModelParts garment, ModelParts sourceBody, GenitalParts.Reading? source)
    {
        var materials = garment.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)
                                                 && BodyModMaterial.IsMatch(p.Material) && !NotGenital.IsMatch(p.Material)).ToList();
        var skinTris = new Dictionary<ModelPart, List<int>>();
        if (source is not { Genitals.Count: > 0 }) return (materials, skinTris);
        var onGenitals = new SpatialSet(source.Genitals.SelectMany(g => g.Triangles.SelectMany(t => new[]
                                        { g.Part.Triangles[t * 3], g.Part.Triangles[t * 3 + 1], g.Part.Triangles[t * 3 + 2] }))
                                        .Distinct().Select(v => PointOf(sourceBody, v)).ToList(), BulgeGarmentGenitalReach);
        foreach (var part in garment.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
        {
            var tris = Enumerable.Range(0, part.Triangles.Length / 3)
                                 .Where(t => Enumerable.Range(0, 3).All(k => onGenitals.Any(PointOf(garment, part.Triangles[t * 3 + k]), BulgeGarmentGenitalReach)))
                                 .ToList();
            if (tris.Count > 0) skinTris[part] = tris;
        }
        return (materials, skinTris);
    }

    /// <summary>Points bucketed in cubes, for "is any within reach".</summary>
    private sealed class SpatialSet
    {
        private readonly Dictionary<(int, int, int), List<Vector3>> cells = new();
        private readonly float cell;

        public SpatialSet(IEnumerable<Vector3> points, float cellSize)
        {
            cell = MathF.Max(cellSize, 1e-4f);
            foreach (var p in points)
            {
                var k = Key(p);
                (cells.TryGetValue(k, out var l) ? l : cells[k] = []).Add(p);
            }
        }

        private (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));

        public bool Any(Vector3 p, float reach)
        {
            var (x, y, z) = Key(p);
            int r = (int)MathF.Ceiling(reach / cell);
            float r2 = reach * reach;
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dz = -r; dz <= r; dz++)
                        if (cells.TryGetValue((x + dx, y + dy, z + dz), out var l))
                            foreach (var q in l)
                                if (Vector3.DistanceSquared(p, q) <= r2) return true;
            return false;
        }
    }
}
