using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Proteus.Services.Bulge;

/// <summary>
/// The shipped featureless bulge (plan featureless-bulge, step 1): <c>Resources/lava_bulge.mdl</c>, the owner's sculpt
/// on a LavaBod+ 2.2 legs model, and our footprint of the sculpt (<c>lava_bulge.footprint.json</c>, the sculpted
/// vertices by index plus a two-ring margin, with the asset's hash).
/// <para/>
/// The bulge is lava's SECOND skin copy, submeshes <c>0.3</c> (the whole skin, sculpted) and <c>0.4</c> (its thighs);
/// <c>0.0</c>–<c>0.2</c> are the body it was sculpted on with only the crotch crease closed, drawn inside it.
/// <para/>
/// <see cref="Base"/> is that copy moved onto lava's inner copy — lava's own crotch with the bulge taken off — so the
/// bulge's RELIEF (lava − base) can be carried onto another body by the refit, as cloth over the base.
/// </summary>
internal sealed class BulgeAsset
{
    internal const string ModelResource = "Proteus.Resources.lava_bulge.mdl";
    internal const string FootprintResource = "Proteus.Resources.lava_bulge.footprint.json";

    /// <summary>The parts of lava that are the bulge copy, by the part reader's label.</summary>
    internal static readonly string[] BulgeCopy = ["1.4", "1.5"];

    /// <summary>The model's bytes, as shipped.</summary>
    public required byte[] Bytes { get; init; }

    /// <summary>The model, read.</summary>
    public required ModelParts Lava { get; init; }

    /// <summary>The bulge copy's parts.</summary>
    public required IReadOnlyList<ModelPart> Copy { get; init; }

    /// <summary>The sculpted vertices and their margin, by the part reader's vertex index.</summary>
    public required IReadOnlySet<int> Footprint { get; init; }

    /// <summary>The footprint vertices on its edge, beside a vertex outside it.</summary>
    public required IReadOnlySet<int> Rim { get; init; }

    /// <summary>Every vertex's position with the bulge copy moved onto lava's inner copy (xyz per vertex, as
    /// <see cref="ModelParts.Positions"/>): lava without its bulge.</summary>
    public required float[] Base { get; init; }

    /// <summary>How far, at most, the bulge stands off the base.</summary>
    public required float ReliefMax { get; init; }

    private static readonly Lazy<BulgeAsset?> shared = new(() => Load(null, out _));

    /// <summary>The asset embedded in Proteus.dll, or null when it is missing or does not match its footprint (logged
    /// once by whoever asks first through <see cref="Load"/>).</summary>
    internal static BulgeAsset? Shared => shared.Value;

    /// <summary>Load the asset from <paramref name="bytes"/> and <paramref name="footprintJson"/>, or the embedded ones
    /// when null. Null, with the reason, when either is missing, the hash does not match, or the copy is not there.</summary>
    internal static BulgeAsset? Load(byte[]? bytes, out string? why, string? footprintJson = null)
    {
        why = null;
        try
        {
            bytes ??= ReadResource(ModelResource);
            footprintJson ??= bytes == null ? null : ReadResourceText(FootprintResource);
            if (bytes == null || footprintJson == null) { why = "the bulge asset is not embedded"; return null; }
            var fp = JsonSerializer.Deserialize<FootprintFile>(footprintJson);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (fp == null || !string.Equals(fp.asset, hash, StringComparison.OrdinalIgnoreCase))
            {
                why = "the bulge asset does not match its footprint";
                return null;
            }
            var lava = ModelPartReader.Read(bytes);
            var copy = lava?.Parts.Where(p => p.Island < 0 && BulgeCopy.Contains(p.Label)).ToList();
            if (lava == null || copy is not { Count: 2 }) { why = "the bulge asset's bulge copy is not there"; return null; }
            int vc = lava.Positions.Length / 3;
            var footprint = fp.vertices.Where(v => v >= 0 && v < vc).ToHashSet();
            var (baseAt, rim) = Fill(lava, copy, footprint, bytes);
            float relief = footprint.Max(v => Vector3.Distance(At(lava.Positions, v), At(baseAt, v)));
            return new BulgeAsset
            {
                Bytes = bytes, Lava = lava, Copy = copy, Footprint = footprint, Rim = rim, Base = baseAt, ReliefMax = relief,
            };
        }
        catch (Exception e)
        {
            why = "the bulge asset could not be read: " + e.Message;
            return null;
        }
    }

    private static Vector3 At(float[] pos, int v) => new(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);

    /// <summary>lava's inner copy: the body the bulge was sculpted on, crease closed — <c>0.0</c> and <c>0.1</c>.</summary>
    internal static readonly string[] InnerCopy = ["1.1", "1.2"];

    /// <summary>
    /// The base: each bulge-copy vertex moved to its partner in lava's INNER copy (<c>0.0</c>/<c>0.1</c>, the body the
    /// bulge was sculpted on with only its crotch crease closed), matched by uv and then by nearness, each partner once.
    /// The plan's harmonic fill of the footprint was measured 43 mm off (p95): the footprint wraps round the crotch, and
    /// a membrane spanned across it collapses. The inner copy ships in the same file, so nothing more ships.
    /// A vertex the sculpt added (no partner) takes the move of its nearest partnered neighbour.
    /// </summary>
    internal static (float[] Base, IReadOnlySet<int> Rim) Fill(ModelParts lava, IReadOnlyList<ModelPart> copy, IReadOnlySet<int> footprint,
                                                              byte[]? bytes = null)
    {
        var pos = (float[])lava.Positions.Clone();
        var uv = bytes != null ? BodyRetarget.Uv(bytes) : [];
        var inner = lava.Parts.Where(p => p.Island < 0 && InnerCopy.Contains(p.Label)).SelectMany(p => p.Triangles).Distinct().ToList();
        var outer = copy.SelectMany(p => p.Triangles).Distinct().ToList();
        if (uv.Length < pos.Length / 3 * 2 || inner.Count == 0) return (pos, new HashSet<int>());

        (long, long) UvKey(int v) => ((long)MathF.Round(uv[v * 2] / 1e-4f), (long)MathF.Round(uv[v * 2 + 1] / 1e-4f));
        var byUv = inner.GroupBy(UvKey).ToDictionary(g => g.Key, g => g.ToList());
        var partner = new Dictionary<int, int>();
        foreach (var group in outer.GroupBy(UvKey))
        {
            if (!byUv.TryGetValue(group.Key, out var cands)) continue;
            var used = new HashSet<int>();
            foreach (var (o, i, _) in group.SelectMany(o => cands.Select(i => (o, i, Vector3.DistanceSquared(At(pos, o), At(pos, i)))))
                                           .OrderBy(x => x.Item3).ThenBy(x => x.o).ThenBy(x => x.i))
                if (!partner.ContainsKey(o) && used.Add(i)) partner[o] = i;
        }
        var moveOf = new Dictionary<int, Vector3>();
        foreach (var (o, i) in partner) moveOf[o] = At(lava.Positions, i) - At(lava.Positions, o);
        foreach (int o in outer.Where(o => !partner.ContainsKey(o)))
        {
            int near = partner.Keys.MinBy(k => Vector3.DistanceSquared(At(pos, k), At(pos, o)));
            moveOf[o] = moveOf[near];
        }
        foreach (var (v, d) in moveOf)
        {
            pos[v * 3] += d.X;
            pos[v * 3 + 1] += d.Y;
            pos[v * 3 + 2] += d.Z;
        }

        // The rim: footprint vertices beside a vertex outside it, through the copy's triangles.
        var rim = new HashSet<int>();
        foreach (var part in copy)
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = part.Triangles[t + k], b = part.Triangles[t + (k + 1) % 3];
                    if (footprint.Contains(a) && !footprint.Contains(b)) rim.Add(a);
                    if (footprint.Contains(b) && !footprint.Contains(a)) rim.Add(b);
                }
        return (pos, rim);
    }

    private static byte[]? ReadResource(string name)
    {
        using var s = typeof(BulgeAsset).Assembly.GetManifestResourceStream(name);
        if (s == null) return null;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    private static string? ReadResourceText(string name)
        => ReadResource(name) is { } b ? System.Text.Encoding.UTF8.GetString(b) : null;

    /// <summary>The footprint file: the asset's sha256, the vertices, the margin they include.</summary>
    private sealed record FootprintFile(string asset, int[] vertices, int margin);
}
