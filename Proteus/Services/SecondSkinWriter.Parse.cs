using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Proteus.Services;

using static Proteus.Services.FaceUvRewriter;

using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    /// <summary>The material names a body model references, e.g. "/mt_c0201b0001_bibo.mtrl". The shell
    /// inherits this model's UVs, so its material is the authoritative statement of their UV space.</summary>
    public static List<string> MaterialNames(byte[] s) => ReadMaterialNames(s, Parse(s));

    /// <summary>
    /// The material names at least one LOD0 mesh actually draws with, in declaration order. A model can
    /// DECLARE a material nothing draws (an emptied stock mesh keeps its vanilla binding). Header-only, unlike
    /// <see cref="ContentPieceResolver.UsedMaterialNames"/>.
    /// </summary>
    public static List<string> DrawnMaterialNames(byte[] s)
    {
        var src = Parse(s);
        var names = ReadMaterialNames(s, src);
        var drawn = new bool[names.Count];
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > s.Length) break;
            if (BitConverter.ToUInt16(s, mo) == 0 || BitConverter.ToUInt32(s, mo + 4) < 3) continue;
            ushort mat = BitConverter.ToUInt16(s, mo + 8);
            if (mat < drawn.Length) drawn[mat] = true;
        }
        return names.Where((_, i) => drawn[i]).ToList();
    }

    /// <summary>The model's attribute names, in the order its submesh masks index them: bit <c>i</c> of a
    /// submesh's mask means entry <c>i</c> here. The order is not fixed across models.</summary>
    public static IReadOnlyList<string> AttributeNames(byte[] s) => Parse(s).AttrNames;

    /// <summary>
    /// The model's material names, and for each the attribute names of the LOD0 submeshes drawn with it, from
    /// ONE walk of the file. An empty attribute list means the material is drawn unconditionally.
    /// </summary>
    /// <remarks>Unreadable material names throw; unreadable attributes degrade to an empty table, since the
    /// submesh ranges are not validated by <see cref="Parse"/>.</remarks>
    public static (List<string> Names, Dictionary<string, List<string>> Attributes) MaterialsAndAttributes(byte[] s)
    {
        var src = Parse(s);
        var names = ReadMaterialNames(s, src);
        try { return (names, ReadMaterialAttributes(src)); }
        catch { return (names, []); }
    }

    private static Dictionary<string, List<string>> ReadMaterialAttributes(Source src)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        int end = src.Lod0MeshIndex + src.Lod0MeshCount;
        for (int m = src.Lod0MeshIndex; m < end && m < src.MeshCount; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (BitConverter.ToUInt16(src.S, mo) == 0) continue;   // empty placeholder mesh

            ushort mat = BitConverter.ToUInt16(src.S, mo + 8);
            if (mat >= src.MatNames.Count) continue;
            if (!result.TryGetValue(src.MatNames[mat], out var names))
                result[src.MatNames[mat]] = names = [];

            ushort subIdx = BitConverter.ToUInt16(src.S, mo + 10), subCount = BitConverter.ToUInt16(src.S, mo + 12);
            for (int su = 0; su < subCount; su++)
            {
                uint mask = BitConverter.ToUInt32(src.S, src.SubmeshStart + (subIdx + su) * 16 + 8);
                for (int bit = 0; bit < 32 && bit < src.AttrNames.Length; bit++)
                    if ((mask & (1u << bit)) != 0 && !names.Contains(src.AttrNames[bit], StringComparer.Ordinal))
                        names.Add(src.AttrNames[bit]);
            }
        }
        return result;
    }

    /// <summary>
    /// LOD0 triangle geometry (object-space position and uv0 per vertex, plus triangle indices) for UV seam
    /// analysis. Every LOD0 mesh is concatenated with its indices rebased, so a seam BETWEEN meshes is found
    /// like one inside a mesh. Returns false rather than throwing on a model this can't read.
    /// <paramref name="keepMaterial"/> selects which meshes count, defaulting to body skin.
    /// </summary>
    public static bool TryReadLod0Geometry(byte[] mdl, out float[] positions, out float[] uvs, out int[] triangles,
        Func<string, bool>? keepMaterial = null)
        => TryReadLod0Geometry(mdl, out positions, out uvs, out triangles, out _, out _,
                               keepMaterial: keepMaterial);

    /// <inheritdoc cref="TryReadLod0Geometry(byte[], out float[], out float[], out int[], out (string, float)[][], out float[])"/>
    public static bool TryReadLod0Geometry(byte[] mdl, out float[] positions, out float[] uvs,
                                           out int[] triangles, out (string Bone, float W)[][] weights)
        => TryReadLod0Geometry(mdl, out positions, out uvs, out triangles, out weights, out _);

    /// <inheritdoc cref="TryReadLod0Geometry(byte[], out float[], out float[], out int[])"/>
    /// <param name="weights">Per vertex, the bones it is skinned to BY NAME with their weights: an index is
    /// only meaningful against the mesh's own bone table.</param>
    /// <param name="normals">Per vertex, the stored normal; a cap vertex's offset off the skin is along it.</param>
    /// <param name="skinOnly">False keeps every LOD0 mesh, not just skin materials. Wanted for SKINNING, not
    /// UV: the toenails carry their own UV island but are weighted to their own bones.</param>
    /// <param name="keepMaterial">Which meshes count, by material name; null falls back to
    /// <paramref name="skinOnly"/>'s body-skin test.</param>
    public static bool TryReadLod0Geometry(byte[] mdl, out float[] positions, out float[] uvs,
                                           out int[] triangles, out (string Bone, float W)[][] weights,
                                           out float[] normals, bool skinOnly = true, bool nonSkin = false,
                                           Func<string, bool>? keepMaterial = null)
    {
        positions = []; uvs = []; triangles = []; weights = []; normals = [];
        Source src;
        try { src = Parse(mdl); }
        catch { return false; }

        var s = src.S;
        var wgt = new List<(string Bone, float W)[]>();
        var nrm = new List<float>();

        // SKIN MESHES ONLY: undies, nails, piercings and pubes are authored in their own UV layout, and
        // including them invents seam edges between surfaces that never touch. Same filter as the shell builder's.
        var matNames = ReadMaterialNames(s, src);
        var pos = new List<float>();
        var uv  = new List<float>();
        var tri = new List<int>();
        Span<float> tmp = stackalloc float[4];

        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > s.Length) break;
            ushort vc = BitConverter.ToUInt16(s, mo);
            uint ic = BitConverter.ToUInt32(s, mo + 4);
            uint startIndex = BitConverter.ToUInt32(s, mo + 16);
            if (vc == 0 || ic < 3) continue;

            ushort matIdx = BitConverter.ToUInt16(s, mo + 8);
            // nonSkin inverts the filter: exactly the meshes the skin filter throws away. Wanted for the CAP
            // BINDING only: a body's skin mesh has a hole where each toenail sits, and a cap vertex over a nail
            // must not bind to the rim of that hole.
            if (nonSkin)
            {
                if (matIdx < matNames.Count && SkinMaterialBodyType(matNames[matIdx]) != null) continue;
            }
            // An explicit filter wins over the body-skin default: a face or a tail is not body skin, and
            // reading one with the body test logs "(no skin geometry)" and goes dark.
            else if (keepMaterial != null)
            {
                if (matIdx >= matNames.Count || !keepMaterial(matNames[matIdx])) continue;
            }
            else if (skinOnly && (matIdx >= matNames.Count || SkinMaterialBodyType(matNames[matIdx]) == null))
                continue;

            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            VElem? posEl = null, uvEl = null, wEl = null, iEl = null, nEl = null;
            foreach (var el in decl)
            {
                if (el.Usage == UsePosition) posEl ??= el;
                else if (el.Usage == UseUV && el.UsageIndex == 0) uvEl ??= el;
                else if (el.Usage == UseBlendWeight) wEl ??= el;
                else if (el.Usage == UseBlendIndices) iEl ??= el;
                else if (el.Usage == UseNormal) nEl ??= el;
            }
            if (posEl is not { } pe || uvEl is not { } ue) continue;

            ushort meshBoneTbl = BitConverter.ToUInt16(s, mo + 14);
            var boneTbl = meshBoneTbl < src.BoneTables.Length ? src.BoneTables[meshBoneTbl] : [];

            uint[] vbo = { BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24), BitConverter.ToUInt32(s, mo + 28) };
            byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
            if (pe.Stream > 2 || ue.Stream > 2 || bs[pe.Stream] == 0 || bs[ue.Stream] == 0) continue;

            int baseVertex = pos.Count / 3;
            bool ok = true;
            for (int k = 0; k < vc && ok; k++)
            {
                int pa = (int)(src.Vb + vbo[pe.Stream]) + k * bs[pe.Stream] + pe.Offset;
                int ua = (int)(src.Vb + vbo[ue.Stream]) + k * bs[ue.Stream] + ue.Offset;
                // 16 bytes is the widest element ReadTyped touches (Float4).
                if (pa < 0 || ua < 0 || pa + 16 > s.Length || ua + 16 > s.Length) { ok = false; break; }
                ReadTyped(s, pa, pe.Type, tmp); pos.Add(tmp[0]); pos.Add(tmp[1]); pos.Add(tmp[2]);
                ReadTyped(s, ua, ue.Type, tmp); uv.Add(tmp[0]); uv.Add(tmp[1]);

                if (nEl is { } ne7 && ne7.Stream <= 2)
                {
                    int na = (int)(src.Vb + vbo[ne7.Stream]) + k * bs[ne7.Stream] + ne7.Offset;
                    if (na >= 0 && na + 16 <= s.Length)
                    {
                        ReadTyped(s, na, ne7.Type, tmp);
                        float nx = tmp[0], ny = tmp[1], nz = tmp[2];
                        if (ne7.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
                        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; }
                        nrm.Add(nx); nrm.Add(ny); nrm.Add(nz);
                    }
                    else { nrm.Add(0); nrm.Add(0); nrm.Add(0); }
                }
                else { nrm.Add(0); nrm.Add(0); nrm.Add(0); }

                if (wEl is not { } we2 || iEl is not { } ie2 || we2.Stream > 2 || ie2.Stream > 2)
                { wgt.Add([]); continue; }
                int nInf = BlendCount(we2.Type);
                int wa = (int)(src.Vb + vbo[we2.Stream]) + k * bs[we2.Stream] + we2.Offset;
                int ia2 = (int)(src.Vb + vbo[ie2.Stream]) + k * bs[ie2.Stream] + ie2.Offset;
                if (wa < 0 || ia2 < 0 || wa + nInf > s.Length || ia2 + nInf > s.Length) { wgt.Add([]); continue; }
                var acc = new List<(string, float)>(nInf);
                for (int q = 0; q < nInf; q++)
                {
                    float f = s[wa + q] / 255f;
                    if (f <= 0f) continue;
                    int local = s[ia2 + q];
                    if (local >= boneTbl.Length || boneTbl[local] >= src.BoneNames.Length) continue;
                    acc.Add((src.BoneNames[boneTbl[local]], f));
                }
                wgt.Add(acc.ToArray());
            }
            if (!ok) { pos.RemoveRange(baseVertex * 3, pos.Count - baseVertex * 3);
                       uv.RemoveRange(baseVertex * 2, uv.Count - baseVertex * 2);
                       if (nrm.Count > baseVertex * 3) nrm.RemoveRange(baseVertex * 3, nrm.Count - baseVertex * 3);
                       if (wgt.Count > baseVertex) wgt.RemoveRange(baseVertex, wgt.Count - baseVertex);
                       continue; }

            for (uint i = 0; i + 2 < ic; i += 3)
            {
                int ia = (int)(src.Ib + (startIndex + i) * 2);
                if (ia < 0 || ia + 6 > s.Length) break;
                int a = BitConverter.ToUInt16(s, ia), b = BitConverter.ToUInt16(s, ia + 2), c = BitConverter.ToUInt16(s, ia + 4);
                if (a >= vc || b >= vc || c >= vc) continue;    // a stale index must not reach another mesh
                tri.Add(baseVertex + a); tri.Add(baseVertex + b); tri.Add(baseVertex + c);
            }
        }

        if (tri.Count == 0) return false;
        positions = pos.ToArray(); uvs = uv.ToArray(); triangles = tri.ToArray();
        while (wgt.Count < pos.Count / 3) wgt.Add([]);
        while (nrm.Count < pos.Count) nrm.Add(0);
        weights = wgt.ToArray();
        normals = nrm.ToArray();
        return true;
    }

    internal static List<string> ReadMaterialNames(byte[] s, Source src)
    {
        var names = new List<string>();
        for (int i = 0; i < src.MatCount; i++)
        {
            int o = src.StrBlock + (int)BitConverter.ToUInt32(s, src.MatOffStart + i * 4), e = o;
            while (s[e] != 0) e++;
            names.Add(Encoding.ASCII.GetString(s, o, e - o));
        }
        return names;
    }

    /// <summary>
    /// The UV space of a SKIN material, or null if this isn't skin at all. A body model also carries undies
    /// (gear UV), nails, piercings and pubes; only mt_c{race}b{body}_… skin materials belong in a second skin.
    /// </summary>
    public static string? SkinMaterialBodyType(string materialName)
    {
        var n = materialName.TrimStart('/');
        if (!n.StartsWith("mt_c", StringComparison.OrdinalIgnoreCase)) return null;

        // skin is mt_c{race}b{body}_… ; equipment is mt_c{race}e{id}_…
        int b = n.IndexOf('b', 4);
        if (b < 0 || b > 8) return null;

        if (n.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)) return "bibo";
        if (n.EndsWith("_eve.mtrl", StringComparison.OrdinalIgnoreCase)) return "gen3";
        if (n.EndsWith("_b.mtrl", StringComparison.OrdinalIgnoreCase)) return "gen3";
        if (n.EndsWith("_a.mtrl", StringComparison.OrdinalIgnoreCase)) return "gen2";
        return null;   // _neolithe_undies, _nails, _piercings, _bibopube, … — not skin
    }

    /// <summary>One entry of a mesh's vertex declaration: where and in what format a given attribute (Usage)
    /// sits within its vertex stream. Models declare different offsets and types, so no fixed layout.</summary>
    internal readonly record struct VElem(byte Stream, byte Offset, byte Type, byte Usage, byte UsageIndex);

    /// <summary>How many bone influences a blend-weight or blend-index element of this type holds: Dawntrail
    /// added an eight-influence format (type 17) beside the old four, and treating one as the other silently
    /// corrupts the weights.</summary>
    private static int BlendCount(byte type) => type == 17 ? 8 : 4;

    /// <summary>A parsed body part. Internal because <see cref="ModelPartReader"/> and
    /// <see cref="ModelAttributeWriter"/> read a model through this parser rather than a second one.</summary>
    /// <summary>
    /// <see cref="Parse"/>, memoised on the byte array it was handed. Keyed by REFERENCE and held weakly:
    /// callers sharing an array share a parse, and a model that goes away takes its parse with it. The factory
    /// may run twice under a race; both produce the same thing.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Source> ParseCache = new();

    private static Source ParseCached(byte[] mdl) => ParseCache.GetValue(mdl, static m => Parse(m));

    internal sealed class Source
    {
        public required byte[] S;
        public int Mh, MeshStart, SubmeshStart, Vb, Ib, StrBlock, MatOffStart;

        /// <summary>End of the vertex-declaration block — where the string block's count and size live
        /// (<c>DeclEnd+0</c> and <c>DeclEnd+4</c>), and so where a string-block edit starts measuring.</summary>
        public int DeclEnd;

        /// <summary>Declared size of the string block, at <c>DeclEnd+4</c>.</summary>
        public uint StrSize;

        /// <summary>First of the three 60-byte LOD structs. Their vertex/index data offsets are ABSOLUTE, so
        /// anything that changes the file's length ahead of them has to shift them.</summary>
        public int LodStart;

        /// <summary>The attribute name-offset table, which the format puts BETWEEN the meshes and the
        /// submeshes.</summary>
        public int AttrStart;
        public ushort MeshCount, SubmeshCount, BoneCount, MatCount;
        public VElem[][] Decls = [];      // one element list per mesh (declCount == meshCount)
        public List<string> MatNames = [];
        public string[] BoneNames = [];

        /// <summary>The model's attribute names, indexed the way its submesh masks reference them. A mod
        /// switches parts on and off through these by NAME (Penumbra's <c>Atr</c> manipulation).</summary>
        public string[] AttrNames = [];
        public ushort[][] BoneTables = [];
        public ushort[] SubmeshBoneMap = [];

        /// <summary>Where the shape block starts (where the bone tables ended). Version-dependent, and every
        /// block after it is placed relative to it.</summary>
        public int ShapeBlock;
        public ushort Lod0MeshIndex, Lod0MeshCount;   // only LOD0 meshes are shelled
        public byte[] BoneBBoxes = [];    // BoneCount * 32
        public byte[] ModelBBoxes = [];   // 4 * 32

        /// <summary>
        /// Where the bounding-box blocks sit IN THE FILE, so an in-place edit can widen them; the position is
        /// only knowable from the walk that found them. An understated radius or clip distance makes the game
        /// cull the geometry while the body is still on screen.
        /// </summary>
        public int ModelBBoxAt, BoneBBoxAt;
        public float Radius, ModelClip, ShadowClip;
        public byte Flags1, Flags2;
        public byte[] Lods = [];          // 3 * 60

        // Shape-key morphs (LOD0), keyed by shape name → per-mesh index edits: a ShapeValue redirects one
        // index-buffer entry (BaseIdx, absolute) to a morphed vertex (Replace). Only EnabledShapes bake.
        public Dictionary<string, List<ShapeMeshEntry>> Shapes = new(StringComparer.Ordinal);
        public HashSet<string>? EnabledShapes;

        // Set when THIS source's UVs are in a different body UV space than the shell's: rewrites each vertex's
        // uv0 into the shell space. Null = same space, leave alone.
        public UVRemapService.UvConversion? UvConv;

        // This source's UV is mirrored and UvConv separates the two sides — see SourceSpec.UnmirrorSides.
        public bool UnmirrorSides;

        // Which of this source's submeshes are already drawn by something else, as (absolute mesh,
        // mesh-relative submesh). Planned once per source, before the layer loop, by PlanConnectorDrops —
        // the emit loop only asks. Null = nothing to drop.
        public IReadOnlySet<(int Mesh, int Sub)>? DropSubmeshes;

        // Per absolute mesh index, the vertices of this source's flap past a join. The ring itself is NOT in
        // here, which lets the emit loop drop a triangle with ANY corner in the set. See PlanJoinCut.
        public Dictionary<int, HashSet<ushort>>? JoinFlaps;

        // Per absolute mesh index, the only vertices this source may draw. A triangle with NO corner among them
        // is dropped — the opposite polarity to JoinFlaps, so the kept region ends one triangle PAST the set and
        // leaves no gap along its edge. Set for a body whose skin is being cut down to what another model drew
        // (see BodyRetarget.CutLike). Null, or a mesh missing from it, draws whole.
        public Dictionary<int, HashSet<ushort>>? DrawOnly;

        // Positions, bucketed at JoinWeld, of this source's vertices that coincide with another source's — the
        // rings it is stitched to its neighbours on. A displacement pass must not move these: the part on the
        // other side of the ring is solved on its own and will not follow. Null = not measured.
        public Dictionary<(long, long, long), List<Vec3>>? JoinRing;

        // Attributes the game is not drawing on this source — see SourceSpec.HiddenAttributes.
        public IReadOnlySet<string>? HiddenAttrs;

        // Which of this source's meshes belong in the shell. Per-source: see SourceSpec.
        public Func<string, bool> Keep = IsBodySkinMaterial;

        // The hands: unpainted nail beds take the fingertip's UV — see SourceSpec.CoverNails.
        public bool CoverNails;
    }

    /// <summary>One mesh's index edits for a shape: for the mesh whose index range begins at
    /// <paramref name="MeshIndexOffset"/>, each value redirects index entry <c>Base</c> → vertex <c>Replace</c>.</summary>
    internal readonly record struct ShapeMeshEntry(uint MeshIndexOffset, (ushort Base, ushort Replace)[] Values);

    /// <summary>v5 bone table: <c>u16 BoneIndex[64]</c> then <c>u32 BoneCount</c>.</summary>
    private const int V5BoneTableBytes = 132;

    internal static Source Parse(byte[] s)
    {
        return new MdlParser(s).Run();
    }

    /// <summary>Every triangle of one mesh, as mesh-local vertex indices, across all of its submeshes,
    /// including ones the connector filter will later drop: the toe cap needs the full topology.</summary>
    internal static ushort[] MeshTriangles(Source src, ushort subIdx, ushort subCount)
        => MeshTrianglesAt(src, src.Ib, subIdx, subCount);

    /// <summary>The body shape keys this source has enabled, for one mesh, as mesh-relative index position →
    /// the morphed vertex that replaces it. Null when none apply; a replacement past the mesh's vertex count
    /// is skipped.</summary>
    private static Dictionary<int, ushort>? ShapeReplacements(Source src, uint meshStartIndex, ushort vc)
    {
        if (src.EnabledShapes is not { Count: > 0 }) return null;
        Dictionary<int, ushort>? replace = null;
        foreach (var shapeName in src.EnabledShapes)
        {
            if (!src.Shapes.TryGetValue(shapeName, out var entries)) continue;
            foreach (var e in entries)
            {
                if (e.MeshIndexOffset != meshStartIndex) continue;
                foreach (var (bIdx, rep) in e.Values)
                    if (rep < vc)
                        (replace ??= new Dictionary<int, ushort>())[bIdx] = rep;   // key = mesh-relative
            }
        }
        return replace;
    }

    /// <summary><see cref="MeshTriangles"/> with the enabled shape keys baked in — the triangles the shell draws.</summary>
    private static ushort[] ShapedTriangles(Source src, ushort subIdx, ushort subCount, uint meshStartIndex,
                                            Dictionary<int, ushort> replace)
    {
        var s = src.S;
        var tris = new List<ushort>();
        for (int su = 0; su < subCount; su++)
        {
            int ss = src.SubmeshStart + (subIdx + su) * 16;
            if (ss + 8 > s.Length) break;
            uint so = BitConverter.ToUInt32(s, ss), sc = BitConverter.ToUInt32(s, ss + 4);
            for (uint t = 0; t + 2 < sc; t += 3)
            {
                int p = src.Ib + (int)(so + t) * 2;
                if (p < 0 || p + 6 > s.Length) break;
                int rel = (int)(so + t - meshStartIndex);
                for (int c = 0; c < 3; c++)
                    tris.Add(replace.TryGetValue(rel + c, out var r) ? r : BitConverter.ToUInt16(s, p + c * 2));
            }
        }
        return tris.ToArray();
    }

    /// <inheritdoc cref="MeshTriangles"/>
    /// <param name="indexBase">Where this LOD's index buffer starts. <see cref="Source.Ib"/> is LOD0's;
    /// <see cref="RewriteFaceUv0"/> reads LOD1 and LOD2, whose submesh offsets are relative to their OWN buffer.</param>
    internal static ushort[] MeshTrianglesAt(Source src, int indexBase, ushort subIdx, ushort subCount)
    {
        var s = src.S;
        var tris = new List<ushort>();
        for (int su = 0; su < subCount; su++)
        {
            int ss = src.SubmeshStart + (subIdx + su) * 16;
            if (ss + 8 > s.Length) break;
            uint so = BitConverter.ToUInt32(s, ss), sc = BitConverter.ToUInt32(s, ss + 4);
            for (uint t = 0; t + 2 < sc; t += 3)
            {
                int p = indexBase + (int)(so + t) * 2;
                if (p < 0 || p + 6 > s.Length) break;
                tris.Add(BitConverter.ToUInt16(s, p));
                tris.Add(BitConverter.ToUInt16(s, p + 2));
                tris.Add(BitConverter.ToUInt16(s, p + 4));
            }
        }
        return tris.ToArray();
    }

    /// <summary>
    /// The authored cap's own LOD0 vertices, per mesh. Not <see cref="TryReadLod0Geometry"/>: that filters
    /// to SKIN materials and the cap wears the overlay's, so it comes back empty.
    /// </summary>
    internal static List<(int Mesh, Vec3[] Pos)> ReadCapMeshes(byte[] capMdl)
    {
        var cap = Parse(capMdl);
        var outp = new List<(int, Vec3[])>();
        int end = cap.Lod0MeshIndex + cap.Lod0MeshCount;
        for (int m = cap.Lod0MeshIndex; m < end && m < cap.MeshCount; m++)
        {
            if (BitConverter.ToUInt16(cap.S, cap.MeshStart + m * 36) == 0) continue;
            ReadCapVertices(cap, m, out var p, out _);
            outp.Add((m, p));
        }
        return outp;
    }

    /// <summary>The per-vertex offsets out of an existing binding, by mesh. See BakeCapBind's offsetsFrom.</summary>
    private static Dictionary<int, float[]>? ReadBindOffsets(byte[] bind)
    {
        if (bind.Length < 12 || BitConverter.ToUInt32(bind, 0) != CapBindMagic) return null;
        var r = new BinaryReader(new MemoryStream(bind));
        r.ReadUInt32();
        int version = r.ReadInt32();
        if (version is not (1 or 2)) return null;
        int partCount = r.ReadInt32();
        for (int i = 0; i < partCount; i++) r.ReadString();

        var outp = new Dictionary<int, float[]>();
        int meshCount = r.ReadInt32();
        for (int mi = 0; mi < meshCount; mi++)
        {
            int mesh = r.ReadInt32(), vc = r.ReadInt32();
            var off = new float[vc];
            for (int i = 0; i < vc; i++)
            {
                r.ReadSingle(); r.ReadSingle();      // u, v
                off[i] = r.ReadSingle();
                r.ReadInt32();                       // side
                r.ReadSingle(); r.ReadSingle(); r.ReadSingle();   // facing
                if (version >= 2) { r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); }   // residual
            }
            outp[mesh] = off;
        }
        return outp;
    }

    /// <summary>Positions and normals of one mesh of a parsed cap.</summary>
    private static void ReadCapVertices(Source cap, int mesh, out Vec3[] pos, out Vec3[] nrm)
    {
        var s = cap.S;
        int mo = cap.MeshStart + mesh * 36;
        ushort vc = BitConverter.ToUInt16(s, mo);
        pos = new Vec3[vc];
        nrm = new Vec3[vc];
        var decl = mesh < cap.Decls.Length ? cap.Decls[mesh] : [];
        VElem? pe = null, ne = null;
        foreach (var el in decl)
        {
            if (el.Usage == UsePosition) pe ??= el;
            if (el.Usage == UseNormal) ne ??= el;
        }
        if (pe is not { } p0) return;

        uint[] vbo = { BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                       BitConverter.ToUInt32(s, mo + 28) };
        byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
        Span<float> tmp = stackalloc float[4];
        for (int i = 0; i < vc; i++)
        {
            ReadTyped(s, cap.Vb + (int)vbo[p0.Stream] + i * bs[p0.Stream] + p0.Offset, p0.Type, tmp);
            pos[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
            if (ne is not { } n0) continue;
            ReadTyped(s, cap.Vb + (int)vbo[n0.Stream] + i * bs[n0.Stream] + n0.Offset, n0.Type, tmp);
            float nx = tmp[0], ny = tmp[1], nz = tmp[2];
            if (n0.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
            nrm[i] = NormalizeOr(new Vec3(nx, ny, nz), default);
        }
    }
}
