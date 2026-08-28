using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// One piece of a model the user can put behind a toggle.
/// <para/>
/// Two granularities, and the second is the one that makes this feature work at all. A SUBMESH is what the
/// author left behind — "mesh 1.2" — and where a mod is already split into parts, that is the whole answer.
/// But the mods this exists for are precisely the ones that are NOT split: a bow, a collar and a skirt
/// welded into one submesh, which offers exactly one row and nothing to toggle. So a submesh is also broken
/// into ISLANDS — runs of triangles connected through shared geometry — and those are separate shells far
/// more often than not.
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
    /// Where each of those triangles sits in the submesh's own index range, by ordinal (0 = the submesh's
    /// first triangle). One entry per triangle, so <c>Ordinals[k]</c> describes <c>Triangles[3k..3k+3]</c>.
    /// <para/>
    /// Carried rather than recomputed, and that is load-bearing. <see cref="Triangles"/> is rebased across
    /// meshes for drawing, which makes it useless for editing — two meshes can present the same corner
    /// triple after rebasing, and the rebase itself skips meshes the reader could not decode. Any writer
    /// that walked the model a second time to recover these would have to reproduce that skip exactly, and
    /// a drift of one mesh silently edits the wrong geometry.
    /// </summary>
    public required int[] Ordinals { get; init; }

    /// <summary>
    /// The submesh's attribute mask as authored. NON-ZERO means the author already gates this geometry, and
    /// this part is therefore not offered as a toggle target — see <see cref="Toggleable"/>.
    /// </summary>
    public required uint AttributeMask { get; init; }

    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }

    public int TriangleCount => Triangles.Length / 3;

    /// <summary>
    /// Whether a toggle may claim this part.
    /// <para/>
    /// Only an UNTAGGED submesh. An untagged submesh always draws, so tagging it with a fresh attribute can
    /// only ever mean "draws while this bit is on" — true whichever way the game combines a mask with more
    /// than one bit in it, which is a question this does not have to answer. A submesh the author already
    /// tagged is a different matter: it is already behind one of the item's ten bits, adding another changes
    /// a rule that is currently working, and the user's own mod already has a toggle for it.
    /// </summary>
    public bool Toggleable => AttributeMask == 0;
}

/// <summary>Everything one model offers, read once.</summary>
public sealed class ModelParts
{
    /// <summary>Object-space xyz per vertex, every LOD0 mesh concatenated with its indices rebased — the
    /// same arrangement <see cref="SecondSkinWriter.TryReadLod0Geometry"/> returns, and for the same reason:
    /// the caller wants to draw the model, not its meshes.</summary>
    public required float[] Positions { get; init; }

    public required IReadOnlyList<ModelPart> Parts { get; init; }

    /// <summary>The model's attribute names. What a new toggle has to avoid colliding with — see
    /// <see cref="ModelPartReader.FreeLetters"/>.</summary>
    public required IReadOnlyList<string> AttributeNames { get; init; }

    /// <summary>Bounds over every LOD0 vertex, so each part's thumbnail is drawn to the SAME frame and the
    /// silhouettes line up when read down a list.</summary>
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }

    /// <summary>Submeshes whose islands were suppressed for being too many, by label — so the panel can say
    /// why a mesh it cannot break up offers only one row.</summary>
    public required IReadOnlyDictionary<string, int> ShatteredSubmeshes { get; init; }
}

/// <summary>
/// Reads a .mdl's toggleable pieces. Read-only and offline: nothing here touches the game, a mod, or a
/// published file.
/// <para/>
/// The parse itself is <see cref="SecondSkinWriter.Parse"/> rather than a second walk of the format. Every
/// offset needed is already computed there and has been proven against real mod models for as long as the
/// shell builder has existed.
/// </summary>
public static class ModelPartReader
{
    /// <summary>
    /// A safety bound on how finely one submesh is broken up, not a judgement about what is useful.
    /// <para/>
    /// It used to be 64, on the reasoning that a submesh shattering into hundreds of pieces is chainmail
    /// rather than a garment with parts, and that a list of four hundred unnamed rows is worse than the one
    /// row it replaces. Both halves of that were wrong. A pair of trousers with 78 belt straps in one
    /// submesh is exactly the case this feature exists for, and it was the ONLY case the cap ever fired on —
    /// it suppressed every island and handed back the whole 53,000-triangle piece, which is the opposite of
    /// what was wanted. And the list stopped being the interface the moment the model became clickable:
    /// picking a strap does not care how many other straps there are.
    /// <para/>
    /// So this is now only high enough to stop a degenerate model — one whose every triangle is its own
    /// island — from building a part list the same size as its geometry.
    /// </summary>
    public const int MaxIslands = 2048;

    /// <summary>
    /// How close two vertices must be to count as the same point when islands are found.
    /// <para/>
    /// Islands are welded by POSITION, never by vertex index, and that is the whole difficulty. A model
    /// duplicates vertices along every UV seam and every hard normal crease, so two triangles that share an
    /// edge on the surface frequently share no index at all. Splitting on indices cuts a bow into its UV
    /// islands — three or four pieces of one object, none of them a thing the user would name.
    /// <para/>
    /// Character models are authored at roughly 1 unit ≈ 1 metre, so this is a tenth of a millimetre: far
    /// below any real gap, far above the drift between two copies of one vertex.
    /// </summary>
    private const float WeldEpsilon = 1e-4f;

    /// <summary>
    /// The IMC attribute letters this model does NOT already use, in order.
    /// <para/>
    /// Ten bits exist and the letter in the name IS the bit — see
    /// <c>SecondSkinService.PartAttributeBit</c>. So the budget for new toggles is whatever letters the
    /// author left, and a mod already using <c>atr_tv_a</c> and <c>atr_tv_b</c> has eight.
    /// </summary>
    public static List<char> FreeLetters(IEnumerable<string> attributeNames)
    {
        var used = new HashSet<char>();
        foreach (var name in attributeNames)
            if (SecondSkinService.PartAttributeBit(name) is { } bit)
                used.Add((char)('a' + bit));

        return Enumerable.Range(0, 10).Select(i => (char)('a' + i)).Where(c => !used.Contains(c)).ToList();
    }

    /// <summary>
    /// Read a model's parts, or null when it cannot be read at all.
    /// <para/>
    /// Null rather than an exception for the same reason <see cref="SecondSkinWriter.TryReadLod0Geometry"/>
    /// returns false: this runs against whatever .mdl files a mod happens to ship, including ones no tool
    /// wrote, and the panel's answer to an unreadable model is a row saying so — not a crash in a draw loop.
    /// </summary>
    public static ModelParts? Read(byte[] mdl)
    {
        SecondSkinWriter.Source src;
        try { src = SecondSkinWriter.Parse(mdl); }
        catch { return null; }

        var s = src.S;
        var pos = new List<float>();
        var parts = new List<ModelPart>();
        var shattered = new Dictionary<string, int>(StringComparer.Ordinal);
        Span<float> tmp = stackalloc float[4];

        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        int ordinal = 0;
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > s.Length) break;

            ushort vc = BitConverter.ToUInt16(s, mo);
            // An emptied mesh is the norm in a mod: an author starts from a stock model, deletes the vanilla
            // geometry and adds their own. It draws nothing, so it is not a part and does not take an
            // ordinal — numbering it would put a gap in the list the user is asked to read.
            if (vc == 0) continue;

            ushort matIdx = BitConverter.ToUInt16(s, mo + 8);
            var material = matIdx < src.MatNames.Count ? src.MatNames[matIdx] : "?";

            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            SecondSkinWriter.VElem? posEl = null;
            foreach (var el in decl)
                if (el.Usage == SecondSkinWriter.UsePosition) { posEl = el; break; }
            if (posEl is not { } pe) continue;

            uint[] vbo =
            {
                BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                BitConverter.ToUInt32(s, mo + 28),
            };
            byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
            if (pe.Stream > 2 || bs[pe.Stream] == 0) continue;

            int baseVertex = pos.Count / 3;
            bool ok = true;
            for (int k = 0; k < vc; k++)
            {
                int pa = (int)(src.Vb + vbo[pe.Stream]) + k * bs[pe.Stream] + pe.Offset;
                // 16 bytes is the widest element ReadTyped touches (Float4).
                if (pa < 0 || pa + 16 > s.Length) { ok = false; break; }
                SecondSkinWriter.ReadTyped(s, pa, pe.Type, tmp);
                pos.Add(tmp[0]); pos.Add(tmp[1]); pos.Add(tmp[2]);
            }
            // A truncated buffer costs this mesh and nothing else. Rewinding matters: a half-decoded mesh
            // left in the array would put garbage vertices under the NEXT mesh's rebased indices.
            if (!ok) { pos.RemoveRange(baseVertex * 3, pos.Count - baseVertex * 3); continue; }

            ordinal++;
            ushort subIdx = BitConverter.ToUInt16(s, mo + 10), subCount = BitConverter.ToUInt16(s, mo + 12);
            for (int su = 0; su < subCount; su++)
            {
                int ss = src.SubmeshStart + (subIdx + su) * 16;
                if (ss + 16 > s.Length) break;
                uint so = BitConverter.ToUInt32(s, ss), sc = BitConverter.ToUInt32(s, ss + 4);
                uint mask = BitConverter.ToUInt32(s, ss + 8);

                var tris = new List<int>((int)sc);
                var ordinals = new List<int>((int)sc / 3);
                for (uint t = 0; t + 2 < sc; t += 3)
                {
                    int ia = (int)(src.Ib + (so + t) * 2);
                    if (ia < 0 || ia + 6 > s.Length) break;
                    int a = BitConverter.ToUInt16(s, ia),
                        b = BitConverter.ToUInt16(s, ia + 2),
                        c = BitConverter.ToUInt16(s, ia + 4);
                    // A stale index must never reach another mesh's vertices through the rebase. Skipping it
                    // is also why the ordinal is recorded rather than inferred from position in the list.
                    if (a >= vc || b >= vc || c >= vc) continue;
                    tris.Add(baseVertex + a); tris.Add(baseVertex + b); tris.Add(baseVertex + c);
                    ordinals.Add((int)(t / 3));
                }
                if (tris.Count == 0) continue;

                var label = $"{ordinal}.{su + 1}";
                var triArr = tris.ToArray();
                var ordArr = ordinals.ToArray();
                parts.Add(Make(m, su, -1, label, material, triArr, ordArr, mask, pos));

                // Islands are offered only when they say something the submesh row does not. One island IS
                // the submesh, and a shattered submesh is reported rather than listed — see MaxIslands.
                var islands = SplitIslands(triArr, pos);
                if (islands.Count <= 1) continue;
                if (islands.Count > MaxIslands) { shattered[label] = islands.Count; continue; }

                // Largest first: on a garment the big island is the garment and the small ones are its
                // trimmings, which is the order someone hunting for "the bow" wants to read.
                //
                // Numbered, not lettered. Letters read well for the two or three islands a tidy submesh has
                // and then fall off a cliff: a real pair of trousers turned out to hold 78 straps in one
                // submesh, where "a + 78" is not a letter at all but '{'.
                int i = 0;
                foreach (var island in islands.OrderByDescending(x => x.Count))
                {
                    parts.Add(Make(m, su, i, $"{label}.{i + 1}", material,
                        [.. island.SelectMany(k => new[] { triArr[k * 3], triArr[k * 3 + 1], triArr[k * 3 + 2] })],
                        [.. island.Select(k => ordArr[k])], mask, pos));
                    i++;
                }
            }
        }

        if (parts.Count == 0) return null;

        var (min, max) = Bounds(pos, null);
        return new ModelParts
        {
            Positions = pos.ToArray(),
            Parts = parts,
            AttributeNames = src.AttrNames,
            Min = min,
            Max = max,
            ShatteredSubmeshes = shattered,
        };
    }

    private static ModelPart Make(
        int mesh, int submesh, int island, string label, string material, int[] triangles, int[] ordinals,
        uint mask, List<float> pos)
    {
        var (min, max) = Bounds(pos, triangles);
        return new ModelPart
        {
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
    /// Split a submesh's triangles into connected runs, welding by position so a UV seam does not read as a
    /// gap — see <see cref="WeldEpsilon"/>.
    /// <para/>
    /// Union-find over TRIANGLES rather than vertices: what comes out has to be a set of triangles, and
    /// unioning vertices then gathering triangles back out of them is the same work with an extra pass.
    /// <para/>
    /// Returns SLOTS into <paramref name="triangles"/> — triangle k is <c>triangles[3k..3k+3]</c> — so the
    /// caller can index its parallel ordinal list with the same number.
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
