using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// One piece of a model the user can put behind a toggle: a whole submesh, or one island of it (a run of triangles
/// connected through shared geometry), since the mods this is for weld several objects into one submesh.
/// </summary>
public sealed class ModelPart
{
    /// <summary>Mesh index in the model's own table (NOT the LOD0 ordinal shown to the user).</summary>
    public required int Mesh { get; init; }

    /// <summary>Submesh index within the mesh, 0-based.</summary>
    public required int Submesh { get; init; }

    /// <summary>Island within the submesh, or -1 for the whole submesh.</summary>
    public required int Island { get; init; }

    /// <summary>What the user sees: "1.2" for a submesh, "1.2b" for its second island.</summary>
    public required string Label { get; init; }

    /// <summary>The material this part's mesh draws with, as the model names it (leading slash and all).</summary>
    public required string Material { get; init; }

    /// <summary>
    /// Triangle corners, as indices into <see cref="ModelParts.Positions"/> — already rebased across meshes,
    /// so a part can be drawn against the whole model without knowing which mesh it came from.
    /// </summary>
    public required int[] Triangles { get; init; }

    /// <summary>
    /// Where each triangle sits in the submesh's own index range, by ordinal: <c>Ordinals[k]</c> describes
    /// <c>Triangles[3k..3k+3]</c>. Carried, not recomputed: <see cref="Triangles"/> is rebased and skips undecodable
    /// meshes, so a writer re-walking the model could edit the wrong geometry.
    /// </summary>
    public required int[] Ordinals { get; init; }

    /// <summary>
    /// The submesh's attribute mask as authored. Non-zero does not mean the author switches this geometry; see
    /// <see cref="Toggleable"/>.
    /// </summary>
    public required uint AttributeMask { get; init; }

    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }

    public int TriangleCount => Triangles.Length / 3;

    /// <summary>
    /// Whether a toggle may claim this part. A submesh draws only when all its attributes are enabled, so adding one
    /// is purely additive. Refused only for a mask bit with no name behind it: <see cref="ModelPartReader.FreeLetters"/>
    /// cannot see that letter, and a new switch could reuse a bit the author's IMC group drives.
    /// </summary>
    public required bool Toggleable { get; init; }

    /// <summary>
    /// At least one of this part's attributes is an IMC part switch (a name ending <c>_a</c>..<c>_j</c>, see
    /// <c>ContentPieceResolver.PartAttributeBit</c>), so the author already has a checkbox over it. Informational: a
    /// switch added here stacks on top. Body-suppression attributes do not count. An island inherits its submesh's.
    /// </summary>
    public bool AuthorSwitched { get; init; }
}

/// <summary>
/// One mesh's run inside the concatenated vertex arrays: vertex <c>BaseVertex + k</c> of
/// <see cref="ModelParts.Positions"/> is vertex <c>k</c> of model mesh <see cref="Mesh"/>.
/// </summary>
/// <param name="Mesh">Index in the model's own mesh table, the number every writer addresses a mesh by — not the
/// LOD0 ordinal shown to the user.</param>
public readonly record struct MeshSpan(int Mesh, int BaseVertex, int Count);

public sealed class ModelParts
{
    /// <summary>Object-space xyz per vertex, every LOD0 mesh concatenated with its indices rebased, as
    /// <see cref="SecondSkinWriter.TryReadLod0Geometry"/> returns.</summary>
    public required float[] Positions { get; init; }

    /// <summary>
    /// Unit normals, one per vertex of <see cref="Positions"/> in the same order. A mesh with no readable normal
    /// contributes zeroes rather than being skipped, so the arrays never fall out of step.
    /// </summary>
    public required float[] Normals { get; init; }

    /// <summary>
    /// Which model mesh each run of <see cref="Positions"/> came from, in order: the only way from a rebased index
    /// back to the file. Recorded while reading, since <see cref="ModelPartReader.Read"/>'s mesh skips cannot be
    /// reconstructed afterwards.
    /// </summary>
    public required IReadOnlyList<MeshSpan> MeshSpans { get; init; }

    /// <summary>
    /// Each vertex's wind — the red of its second vertex colour, 0..1 — in the same order as
    /// <see cref="Positions"/>; 0 where the mesh has no second colour. Empty when not read.
    /// </summary>
    public float[] Wind { get; init; } = [];

    /// <summary>Every mesh already carries the second vertex colour, so painting wind adds nothing to the file.</summary>
    public bool HasWindChannel { get; init; }

    /// <summary>Some mesh's first vertex colour is not white, which the wind effect expects.</summary>
    public bool FirstColorNotWhite { get; init; }

    public required IReadOnlyList<ModelPart> Parts { get; init; }

    /// <summary>The model's attribute names. What a new toggle has to avoid colliding with — see
    /// <see cref="ModelPartReader.FreeLetters"/>.</summary>
    public required IReadOnlyList<string> AttributeNames { get; init; }

    /// <summary>Bounds over every LOD0 vertex, so every part's thumbnail is drawn to the same frame.</summary>
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }

    /// <summary>Submeshes whose islands were suppressed for being too many, by label, with the island count.</summary>
    public required IReadOnlyDictionary<string, int> ShatteredSubmeshes { get; init; }
}

/// <summary>
/// Reads a .mdl's toggleable pieces. Read-only and offline. The parse is <see cref="SecondSkinWriter.Parse"/>.
/// </summary>
public static partial class ModelPartReader
{
    /// <summary>
    /// A safety bound on how many islands one submesh is broken into, only to stop a degenerate model building a part
    /// list the size of its geometry.
    /// </summary>
    public const int MaxIslands = 2048;

    /// <summary>
    /// How close (model units ≈ metres, so 0.1 mm) two vertices must be to count as one point when islands are found.
    /// Islands are welded by position, never by index, since UV seams and hard creases duplicate vertices.
    /// </summary>
    private const float WeldEpsilon = 1e-4f;

    /// <summary>
    /// The IMC attribute letters this model does not already use, in order. Ten bits exist and the letter is the bit
    /// (see <c>ContentPieceResolver.PartAttributeBit</c>).
    /// </summary>
    public static List<char> FreeLetters(IEnumerable<string> attributeNames)
    {
        var used = new HashSet<char>();
        foreach (var name in attributeNames)
            if (ContentPieceResolver.PartAttributeBit(name) is { } bit)
                used.Add((char)('a' + bit));

        return Enumerable.Range(0, 10).Select(i => (char)('a' + i)).Where(c => !used.Contains(c)).ToList();
    }

    /// <summary>
    /// Read a model's parts, or null when it cannot be read at all.
    /// ModelSkinReader relies on this vertex order, index for index: keep its mesh skips in step with these.
    /// </summary>
    public static ModelParts? Read(byte[] mdl)
    {
        return new PartRead(mdl).Run();
    }

    private static ModelPart Make(
        int mesh, int submesh, int island, string label, string material, int[] triangles, int[] ordinals,
        uint mask, bool toggleable, bool authorSwitched, List<float> pos)
    {
        var (min, max) = Bounds(pos, triangles);
        return new ModelPart
        {
            Toggleable = toggleable,
            AuthorSwitched = authorSwitched,
            Mesh = mesh,
            Submesh = submesh,
            Island = island,
            Label = label,
            Material = material,
            Triangles = triangles,
            Ordinals = ordinals,
            AttributeMask = mask,
            Min = min,
            Max = max,
        };
    }

    /// <summary>Bounds over the whole vertex array (<paramref name="triangles"/> null) or over just the
    /// vertices a part references.</summary>
    private static (Vector3 Min, Vector3 Max) Bounds(List<float> pos, int[]? triangles)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        void Add(int v)
        {
            var p = new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        if (triangles == null)
            for (int v = 0; v < pos.Count / 3; v++) Add(v);
        else
            foreach (var v in triangles) Add(v);

        return min.X > max.X ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }

    /// <summary>
    /// Split a submesh's triangles into connected runs, welding by position (<see cref="WeldEpsilon"/>). Union-find
    /// over triangles. Returns slots into <paramref name="triangles"/> (triangle k is <c>triangles[3k..3k+3]</c>).
    /// </summary>
    private static List<List<int>> SplitIslands(int[] triangles, List<float> pos)
    {
        int n = triangles.Length / 3;
        if (n <= 1) return n == 1 ? [[0]] : [];

        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }

        // First triangle seen at each welded point; every later triangle touching it joins that one, which
        // transitively connects everything sharing the point without comparing triangles pairwise.
        var atPoint = new Dictionary<(int, int, int), int>(triangles.Length);
        for (int t = 0; t < n; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                int v = triangles[t * 3 + c];
                var key = (
                    (int)MathF.Round(pos[v * 3]     / WeldEpsilon),
                    (int)MathF.Round(pos[v * 3 + 1] / WeldEpsilon),
                    (int)MathF.Round(pos[v * 3 + 2] / WeldEpsilon));
                if (atPoint.TryGetValue(key, out var first)) Union(first, t);
                else atPoint[key] = t;
            }
        }

        var byRoot = new Dictionary<int, List<int>>();
        for (int t = 0; t < n; t++)
        {
            if (!byRoot.TryGetValue(Find(t), out var list)) byRoot[Find(t)] = list = [];
            list.Add(t);
        }
        return byRoot.Values.ToList();
    }
}
