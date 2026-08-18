using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Proteus.Services;

/// <summary>One stacked shell of the second skin: its own copy of the geometry, material and coverage.</summary>
public sealed class SecondSkinLayer
{
    /// <summary>Material game path as the model stores it, e.g. "/mt_c0201a0053_rir_a.mtrl".</summary>
    public required string MaterialName { get; init; }

    /// <summary>
    /// Coverage mask (one byte per texel = opacity). Triangles whose entire UV footprint is zero are
    /// dropped, so a shell only carries the geometry its layer actually paints. Null keeps everything.
    /// </summary>
    public byte[]? Coverage { get; init; }

    public int CoverageWidth { get; init; }
    public int CoverageHeight { get; init; }

    /// <summary>
    /// Optional toe-cap mask (one byte per texel, body UV, 0 = untouched .. 255 = fully capped). Where it
    /// is non-zero the shell is inflated onto a smooth envelope instead of following the body contour, so
    /// hosiery webs the gaps between the toes rather than sleeving each one. Null = today's behaviour.
    /// </summary>
    public byte[]? ToeCap { get; init; }

    public int ToeCapWidth { get; init; }
    public int ToeCapHeight { get; init; }

    /// <summary>How far the masked region inflates toward its envelope (0 = off, 1 = full).</summary>
    public float ToeCapStrength { get; init; } = 1f;
}

/// <summary>
/// Builds the "second skin" model: every skin part (chest, legs, hands, feetâ€¦) duplicated, pushed out
/// along its normals, and MERGED into a single model so the whole thing rides one invisible accessory
/// (the right ring). Each part Ã— layer becomes its own mesh group, and each group carries its layer's
/// material â€” so different regions can run different shaders.
///
/// Each mesh keeps its SOURCE vertex format verbatim (its own declaration and stream layout); only the
/// position (pushed), vertex colour (whitened), and uv1 (mirrored from uv0 for the scroll shader) are
/// rewritten. See <see cref="BuildVerbatim"/> â€” this is what lets vanilla, bibo and Neolithe bodies,
/// whose blend/uv byte formats differ, all skin correctly without reinterpreting the skinning data.
///
/// Hard-won constraints, each of which was a crash or a silent no-render:
///  - Every mesh needs its own vertex declaration (vertDeclCount == meshCount); the meshes may mix
///    formats, since each declaration describes its own mesh.
///  - RuntimeSize must be recomputed (vtxOffset - 0x44 - StackSize).
///  - Each mesh must keep its source's FULL submesh structure; collapsing to one submesh yields a bone
///    range that doesn't cover the mesh's vertices -> ModelDrawInit fault.
///  - Only declare materials that are actually used; the game loads every declared material and an
///    unresolvable one faults.
///  - Sources MUST be the body models the character is actually drawing. A shell cut from a different
///    body/chest size is a different SHAPE, and the body pokes through it at any push distance.
///  - Bodies can have 400+ bones. Bone indices are u16 so a big union list is fine, but vertex
///    BlendIndices are ubyte4 â€” they address the MESH'S OWN bone table, which therefore can never
///    exceed 255 entries. So each mesh keeps its own table and only the table's ENTRIES are remapped
///    onto the union bone list; vertex indices are never touched.
/// </summary>
public static class SecondSkinWriter
{
    /// <summary>
    /// How far the FIRST shell sits off the skin. Much larger than <see cref="LayerSeparation"/>: the
    /// skin underneath is what moves, and shells are offset in BIND POSE and only then skinned, so the
    /// gap is not preserved once the body deforms.
    ///
    /// Note the gap also closes on the UPPER ARM, where vertices have ~1 bone influence and the shell
    /// should therefore transform rigidly with the skin â€” so pure joint compression does not explain all
    /// of it. Suspects: split/duplicated normals at UV seams pushing coincident vertices apart, or the
    /// skin picking up deformation the shell does not. Until that is understood this value is empirical.
    /// </summary>
    public const float BaseOffset = 1e-3f;

    /// <summary>
    /// Separation between adjacent shells. Measured in-game: 2e-4 holds, below it they clip. This is NOT
    /// a depth-precision limit (float32 depth at 1-3 units resolves far finer) â€” it's skinning.
    /// Layer k sits at BaseOffset + k * LayerSeparation.
    /// </summary>
    public const float LayerSeparation = 2e-4f;

    private const int DeclSize = 17 * 8;   // vertex declaration block, one per mesh
    private const int BBoxSize = 32;       // min Vec4 + max Vec4

    /// <param name="CapDeclined">
    /// Set when a toe cap was asked for but no binding described this body well enough to place it, so
    /// none was emitted. Worth telling the wearer about: the toes silently lose their cap, and the only
    /// alternative â€” emitting it anyway â€” tears it into shards.
    /// </param>
    /// <param name="CapUsed">
    /// Which authored cap this shell got, and how well its binding fitted. Reported because "is the cap I
    /// just authored actually being used?" is otherwise only answerable from the Dalamud log, which is
    /// size-capped and stops writing.
    /// </param>
    public readonly record struct Stats(int Meshes, int Submeshes, int Bones, int TrianglesIn,
                                        int TrianglesOut, int VerticesOut, string? CapDeclined = null,
                                        string? CapUsed = null);

    /// <summary>
    /// A toe cap modelled for one body, with the binding that says where it sits on it.
    /// <para/>
    /// One per body: a cap authored for one body cannot be fitted to another well enough to look right,
    /// even when both are nominally the same UV space. The binding's job is the other axis â€” heels and
    /// any other foot MODEL swap for the body the cap belongs to.
    /// </summary>
    /// <param name="Bind">Null only for a cap shipped without one, which can then be used as authored.</param>
    /// <param name="Name">For the log, so it is clear which cap a shell ended up with.</param>
    public readonly record struct AuthoredCapSet(byte[] Cap, byte[]? Bind, string Name);

    /// <summary>
    /// The material names a body model references, e.g. "/mt_c0201b0001_bibo.mtrl". The shell inherits
    /// this model's UVs, so its material is the authoritative statement of which UV space those are â€”
    /// far more reliable than guessing from whatever body materials happen to be loaded.
    /// </summary>
    public static List<string> MaterialNames(byte[] s) => ReadMaterialNames(s, Parse(s));

    /// <summary>
    /// LOD0 triangle geometry â€” object-space position and uv0 per vertex, plus triangle indices â€” for UV
    /// seam analysis (see <see cref="UvSeamMapService"/>). Every LOD0 mesh is concatenated into one vertex
    /// array with its indices rebased, so a seam BETWEEN two meshes is found exactly like one inside a
    /// mesh; that matters because a body's torso and legs are frequently separate meshes.
    /// <para/>
    /// Returns false rather than throwing on a model this can't read â€” a missing position or uv0 element,
    /// a truncated buffer, anything Parse rejects. The caller treats that as "no seam data" and falls back.
    /// <para/>
    /// <paramref name="keepMaterial"/> selects which meshes count, defaulting to body skin. The two callers
    /// genuinely want different answers and must not be unified: the seam map is built for the SKIN bake and
    /// is body-only by nature, while the shell's shape fingerprint has to describe whatever surface is being
    /// cut, or a face logs "(no skin geometry)" and the most useful diagnostic in the build goes dark.
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
    /// <param name="weights">
    /// Per vertex, the bones it is skinned to BY NAME with their weights. Names, not indices: an index is
    /// only meaningful against the mesh's own bone table, and the whole point of reading these is to hand
    /// them to a different mesh.
    /// </param>
    /// <param name="normals">
    /// Per vertex, the stored normal. A cap vertex records how far it sits OFF the skin, and "off" only
    /// means anything along this.
    /// </param>
    /// <param name="skinOnly">
    /// False keeps every LOD0 mesh, not just the skin materials. Wanted for SKINNING, not for UV: the
    /// toenails are their own mesh under their own material and carry their own UV island, so projecting
    /// a coordinate onto one is wrong â€” but they are also weighted to their own bones, and a cap that
    /// covers a nail while deforming with the skin beside it lets the nail through the moment a toe bends.
    /// </param>
    /// <param name="keepMaterial">
    /// Which meshes count, by material name. Supplied by the shell builder so a face or a tail is read
    /// with its own filter; null falls back to <paramref name="skinOnly"/>'s body-skin test.
    /// </param>
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

        // SKIN MESHES ONLY. A body model is not all skin: it carries the smallclothes/undies mesh, nails,
        // piercings and pubes, and each of those is authored in its OWN UV layout (gear space, not body
        // space). Including them lands their triangles at unrelated places in the body atlas â€” which bridges
        // the gap between genuinely separate islands and invents seam edges between surfaces that never
        // touch. Same filter, and the same reason, as the shell builder's.
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
            // nonSkin inverts the filter: exactly the meshes the skin filter throws away â€” toenails,
            // fingernails, undies, piercings. Wanted for the CAP BINDING and nothing else. A body's
            // skin mesh has a HOLE where each toenail sits (Rue does; Neolithe carries skin under its
            // nails), so a cap vertex over a nail has no skin beneath it and binds to the rim of that
            // hole instead â€” the offset there came out at 0.0085 against about 0.001 everywhere else,
            // and measuring from an edge is what pressed a dish into the toenail.
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

    private static List<string> ReadMaterialNames(byte[] s, Source src)
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
    /// The UV space of a SKIN material, or null if this isn't skin at all.
    ///
    /// This is what "select all the skin elements" means in practice. A body model is NOT all skin: it
    /// also carries the smallclothes/undies mesh (gear UV!), plus nails, piercings and pubes, each with
    /// its own material and UV layout. Duplicating those into the shell and painting them with a
    /// body-UV overlay smears the art across the hips and hands. Only meshes whose material is a body
    /// skin material (mt_c{race}b{body}_â€¦) belong in a second skin.
    /// </summary>
    public static string? SkinMaterialBodyType(string materialName)
    {
        var n = materialName.TrimStart('/');
        if (!n.StartsWith("mt_c", StringComparison.OrdinalIgnoreCase)) return null;

        // skin is mt_c{race}b{body}_â€¦ ; equipment is mt_c{race}e{id}_â€¦
        int b = n.IndexOf('b', 4);
        if (b < 0 || b > 8) return null;

        if (n.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)) return "bibo";
        if (n.EndsWith("_eve.mtrl", StringComparison.OrdinalIgnoreCase)) return "gen3";
        if (n.EndsWith("_b.mtrl", StringComparison.OrdinalIgnoreCase)) return "gen3";
        if (n.EndsWith("_a.mtrl", StringComparison.OrdinalIgnoreCase)) return "gen2";
        return null;   // _neolithe_undies, _nails, _piercings, _bibopube, â€¦ â€” not skin
    }

    /// <summary>
    /// The default mesh filter: body skin only. Split out from <see cref="SkinMaterialBodyType"/> because
    /// that function was answering two unrelated questions with one return value â€” "does this mesh belong in
    /// a shell" (a bool, asked per mesh) and "what UV space is this model in" (a string, asked per model).
    /// Only the caller ever wanted the string, and only this predicate is what a non-body surface needs to
    /// replace.
    /// </summary>
    public static bool IsBodySkinMaterial(string materialName) => SkinMaterialBodyType(materialName) != null;

    /// <summary>
    /// A mesh filter that keeps exactly the meshes bound to the named materials, compared by leaf name
    /// (the model stores them with a leading slash). This is what a non-body surface uses: a face model
    /// carries eyes, lashes and brows beside the face itself, each with its own material AND its own UV
    /// layout, so an overlay that declares it paints <c>mt_c1401f0001_fac_a.mtrl</c> must get that mesh and
    /// nothing else. Naming the material is stricter than any suffix rule and cannot drift with the game's
    /// naming conventions, because it is the mod's own declared target.
    /// </summary>
    public static Func<string, bool> KeepByLeaf(IReadOnlySet<string> leaves)
        => n => leaves.Contains(n.TrimStart('/'));

    /// <summary>
    /// One source model and everything the merge needs to know about it. Replaces the parallel arrays this
    /// used to take (enabled shapes, UV converters, and a single connector flag shared by every source):
    /// they were index-aligned by convention, and each new per-source concern was another chance to
    /// misalign them. It also makes the per-source-ness explicit where it matters â€” a shell cut from a face
    /// and one cut from a body do not want the same mesh filter or the same connector heuristic.
    /// </summary>
    /// <param name="Model">The .mdl bytes.</param>
    /// <param name="KeepMaterial">Which meshes to copy, by material name. Null = <see cref="IsBodySkinMaterial"/>.</param>
    /// <param name="EnabledShapes">Shape keys the game has enabled on this model, to bake.</param>
    /// <param name="UvConv">Vertex UV conversion into the shell's space. Null = already there, leave alone.</param>
    /// <param name="DropConnectors">
    /// Drop this source's redundant connector geometry. A Neolithe-tuned heuristic (see the emit loop), so
    /// it is only ever right for a BODY source â€” pointed at a face, tail or ear it deletes real geometry.
    /// </param>
    public readonly record struct SourceSpec(
        byte[] Model,
        Func<string, bool>? KeepMaterial = null,
        HashSet<string>? EnabledShapes = null,
        Func<float, float, (float U, float V)?>? UvConv = null,
        bool DropConnectors = false);

    /// <summary>
    /// One entry of a mesh's vertex declaration: where and in what format a given attribute (Usage) sits
    /// within its vertex stream. Read so the transcoder can locate attributes by declaration instead of
    /// assuming a fixed layout â€” vanilla and modded models declare different offsets and types (half vs
    /// float, compressed positions), so a fixed layout skins the wrong bytes as garbage.
    /// </summary>
    private readonly record struct VElem(byte Stream, byte Offset, byte Type, byte Usage, byte UsageIndex);

    /// <summary>
    /// How many bone influences a blend-weight or blend-index element of this type holds.
    /// <para/>
    /// Dawntrail added an EIGHT-influence format (type 17, eight bytes) alongside the old four. Treating
    /// one as the other is silent and destructive: writing four weights into an eight-influence vertex
    /// leaves the last four holding whatever the source had, so the total comes to 1.7x and the vertex is
    /// dragged toward a bone nothing intended. In game that shows as triangles stretched away or gone,
    /// while a modelling package â€” reading the first four and the bind pose â€” shows it perfectly correct.
    /// </summary>
    private static int BlendCount(byte type) => type == 17 ? 8 : 4;

    // Vertex Usage ids (FFXIV mdl).
    private const byte UsePosition = 0, UseBlendWeight = 1, UseBlendIndices = 2,
                       UseNormal = 3, UseUV = 4, UseTangent2 = 5, UseTangent1 = 6, UseColor = 7;

    /// <summary>A parsed body part.</summary>
    private sealed class Source
    {
        public required byte[] S;
        public int Mh, MeshStart, SubmeshStart, Vb, Ib, StrBlock, MatOffStart;
        public ushort MeshCount, SubmeshCount, BoneCount, MatCount;
        public VElem[][] Decls = [];      // one element list per mesh (declCount == meshCount)
        public List<string> MatNames = [];
        public string[] BoneNames = [];
        public ushort[][] BoneTables = [];
        public ushort[] SubmeshBoneMap = [];
        public ushort Lod0MeshIndex, Lod0MeshCount;   // only LOD0 meshes are shelled
        public byte[] BoneBBoxes = [];    // BoneCount * 32
        public byte[] ModelBBoxes = [];   // 4 * 32
        public float Radius, ModelClip, ShadowClip;
        public byte Flags1, Flags2;
        public byte[] Lods = [];          // 3 * 60

        // Shape-key morphs parsed from the .mdl (LOD0), keyed by shape name â†’ its per-mesh index edits.
        // A ShapeValue redirects one index-buffer entry (at BaseIdx, absolute) to a morphed replacement
        // vertex (Replace). Enabled shapes for THIS body model (from BodyShapeReader); only these bake.
        public Dictionary<string, List<ShapeMeshEntry>> Shapes = new(StringComparer.Ordinal);
        public HashSet<string>? EnabledShapes;

        // Set when THIS source's UVs are in a different body UV space than the shell's (a bibo-UV heel's
        // foot beside a gen3 torso). Rewrites each vertex's uv0 into the shell space so one art set â€”
        // already remapped into that space â€” lands correctly on every part. Null = same space, leave alone.
        public Func<float, float, (float U, float V)?>? UvConv;

        // Which of this source's meshes belong in the shell, and whether its connector heuristic runs.
        // Both per-source: see SourceSpec.
        public Func<string, bool> Keep = IsBodySkinMaterial;
        public bool DropConnectors;
    }

    /// <summary>One mesh's index edits for a shape: for the mesh whose index range begins at
    /// <paramref name="MeshIndexOffset"/>, each value redirects index entry <c>Base</c> â†’ vertex <c>Replace</c>.</summary>
    internal readonly record struct ShapeMeshEntry(uint MeshIndexOffset, (ushort Base, ushort Replace)[] Values);

    /// <summary>A model carries at most 10 materials â€” the game/Penumbra ceiling (Penumbra's own model
    /// importer caps at 10, ModelImporter.MaterialLimit). Host-selection cap, enforced in the caller.</summary>
    public const int MaxMaterials = 10;

    /// <summary>
    /// Build the merged shell. <paramref name="sources"/> are the body models the character is currently
    /// drawing (resolve them live â€” never ship a prebuilt shell); every one contributes its own mesh
    /// groups. Every layer is applied to every source.
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> sources, IReadOnlyList<SecondSkinLayer> layers, out Stats stats)
        => Build(sources, layers, null, false, out stats);

    /// <summary>
    /// Append the shell into a HOST accessory model (an equipped ring/bracelet) rather than replacing it:
    /// <paramref name="baseModel"/>'s meshes/materials are emitted verbatim FIRST (so the ring still
    /// renders), then the body-shell layers are appended with material indices offset past the host's.
    /// Null <paramref name="baseModel"/> = the original replace behaviour (a fresh shell-only model).
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> sources, IReadOnlyList<SecondSkinLayer> layers,
        byte[]? baseModel, out Stats stats)
        => Build(sources, layers, baseModel, false, out stats);

    /// <summary>
    /// As above, but <paramref name="skipConnectors"/> drops each source's redundant connector geometry â€”
    /// the thin joint seam rings and duplicate variant submeshes â€” for bodies (Neolithe) that would
    /// otherwise double up on a sheer shell.
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> sources, IReadOnlyList<SecondSkinLayer> layers,
        byte[]? baseModel, bool skipConnectors, out Stats stats,
        IReadOnlyList<HashSet<string>?>? enabledShapes = null, Action<string>? diag = null,
        IReadOnlyList<AuthoredCapSet>? authoredCaps = null,
        // Per-source UV-space converter, parallel to `sources`; null entries are already in shell space.
        // See Source.UvConv.
        IReadOnlyList<Func<float, float, (float U, float V)?>?>? uvConverters = null)
        => Build(
            sources.Select((m, i) => new SourceSpec(
                m,
                KeepMaterial: null,   // body-skin filter, the behaviour every existing caller expects
                EnabledShapes: enabledShapes != null && i < enabledShapes.Count ? enabledShapes[i] : null,
                UvConv: uvConverters != null && i < uvConverters.Count ? uvConverters[i] : null,
                DropConnectors: skipConnectors)).ToList(),
            layers, baseModel, out stats, diag, authoredCaps);

    /// <summary>
    /// Build the merged shell from fully-described sources. Every layer is applied to every source, so all
    /// sources here must share one UV space and one race space â€” that is what makes them one surface.
    /// </summary>
    public static byte[] Build(IReadOnlyList<SourceSpec> sources, IReadOnlyList<SecondSkinLayer> layers,
        byte[]? baseModel, out Stats stats, Action<string>? diag = null,
        IReadOnlyList<AuthoredCapSet>? authoredCaps = null)
    {
        if (sources.Count == 0) throw new ArgumentException("need at least one source model", nameof(sources));
        if (layers.Count == 0) throw new ArgumentException("need at least one layer", nameof(layers));

        var parsed = sources.Select(s => Parse(s.Model)).ToList();

        // The raw model bytes, for the cap passes that read geometry straight out of a .mdl rather than
        // going through the parsed sources â€” the binding probe, the UV projection and the skin-triangle
        // collection all take a plain list of models.
        var sourceModels = sources.Select(s => s.Model).ToList();

        // Attach each source's enabled shape keys and (Stage 2a) verify the parse against them: does the
        // .mdl actually contain the enabled shape, and how many of its index edits resolve to in-range
        // positions/vertices. This confirms the format read before any geometry is mutated.
        for (int i = 0; i < parsed.Count; i++)
        {
            var en = sources[i].EnabledShapes;
            parsed[i].EnabledShapes = en;
            parsed[i].UvConv = sources[i].UvConv;
            parsed[i].Keep = sources[i].KeepMaterial ?? IsBodySkinMaterial;
            parsed[i].DropConnectors = sources[i].DropConnectors;
            // Warn only on the failure case: an enabled shape the .mdl doesn't actually contain (nothing to
            // bake). The success path is silent â€” the shell simply follows the body.
            if (en == null || en.Count == 0 || diag == null) continue;
            foreach (var name in en)
                if (!parsed[i].Shapes.ContainsKey(name))
                    diag($"shape '{name}' enabled but not present in source {i} â€” not baked");
        }
        Source? baseSrc = baseModel != null ? Parse(baseModel) : null;

        // The hand-modelled toe box, bundled with the plugin. It replaces the generated cap: a shell is
        // a displaced copy of the body, so it sleeves each toe unless something covers the toe box, and
        // generating that something is a topology problem that kept producing pinched, lumpy geometry.
        // Merged like any other source â€” its own bone table joins the union by name and its vertices
        // keep their blend indices, so it skins without anything being reinterpreted.
        // ONE CAP PER BODY, each modelled against its own toes, each with a binding measured against the
        // body it was modelled on. Fitting a cap authored for one body onto another was tried at length
        // and abandoned: placing it is measurable and works, but reconciling its rim with a cut made from
        // a map painted in a DIFFERENT body's parameterisation is not something the numbers can settle â€”
        // it took four changes, two of them reverted, and still looked wrong. A modeller closes that loop
        // by looking at it, in minutes.
        //
        // The binding still earns its place, on the axis authoring cannot cover: heels are not another
        // body, they are another foot MODEL for the same one, and a cap per body per footwear would be
        // combinatorial. The binding collapses that axis, where it measures cleanly (0-4% unplaced).
        //
        // Nothing can ask which body is equipped â€” Proteus knows only three UV buckets and Neolithe, Rue
        // and Bibo+ are all "bibo" â€” so the caps identify themselves: whichever binding places the most
        // vertices belongs to this foot, and its cap is the one to graft. Scored on a sample first,
        // because a full placement is every cap vertex against every skin triangle.
        Source? capSrc = null;
        byte[]? capBytes = null;
        Dictionary<int, CapPlacement>? capPlaced = null;
        string? capDeclined = null, capUsed = null;
        if (authoredCaps is { Count: > 0 })
        {
            // WHICH BONES THE BODY HAS, before asking where anything lands. Position alone cannot tell
            // these bodies apart: barefoot it ranked the right cap first by a whisker, and in heels — a
            // foot model neither cap was ever measured against — the order flipped and a Rue cap went
            // onto a Bibo+ foot, which is where the big toe caved in. Rue weights its toes to IVCS bones
            // that a Bibo+ body simply does not have, so the bind's bone list separates them outright
            // where a 4%-against-1% placement score is noise.
            var bodyBones = new HashSet<string>(StringComparer.Ordinal);
            foreach (var psrc in parsed)
                foreach (var bn in psrc.BoneNames)
                    if (!string.IsNullOrEmpty(bn)) bodyBones.Add(bn);

            AuthoredCapSet? best = null;
            float bestRate = float.MaxValue, bestCover = -1f;
            foreach (var cand in authoredCaps)
            {
                if (cand.Bind == null)
                {
                    // No binding: usable only as a last resort, exactly as authored.
                    if (best == null) { best = cand; bestRate = float.MaxValue; bestCover = -1f; }
                    continue;
                }

                var want = ReadBindBones(cand.Bind);
                float cover = want.Count == 0 ? 0f
                            : (float)want.Count(bodyBones.Contains) / want.Count;
                var probe = TryPlaceCapFromBind(cand.Bind, sourceModels, null, cand.Cap, CapBindProbeStride);
                if (probe is not { Count: > 0 }) continue;
                int tot = probe.Sum(p => p.Considered), miss = probe.Sum(p => p.Missed);
                float rate = tot > 0 ? (float)miss / tot : 1f;
                if (authoredCaps.Count > 1)
                    diag?.Invoke($"authored cap: '{cand.Name}' places all but {rate * 100:F0}% on this body, "
                               + $"and this body has {cover * 100:F0}% of the {want.Count} bone(s) it was "
                               + "bound to");

                // Bone coverage decides; the placement score only separates caps the body can equally
                // carry. A cap missing bones is not a worse fit, it is the wrong body.
                bool better = cover > bestCover + CapBoneCoverTie
                           || (cover >= bestCover - CapBoneCoverTie && rate < bestRate);
                if (better) { bestCover = cover; bestRate = rate; best = cand; }
            }

            if (best is { } chosen && bestRate <= CapBindMaxUnplaced)
            {
                capBytes = chosen.Cap;
                try { capSrc = Parse(chosen.Cap); }
                catch (Exception ex) { diag?.Invoke($"authored cap failed to parse, ignoring: {ex.Message}"); }
                if (capSrc != null && chosen.Bind != null)
                {
                    capUsed = $"{chosen.Name} ({(1f - bestRate) * 100:F0}% placed)";
                    diag?.Invoke($"authored cap: using '{chosen.Name}'");
                    var placed = TryPlaceCapFromBind(chosen.Bind, sourceModels, diag, chosen.Cap);
                    // NOT lifted clear of the toenails. Tried that: the cap measured as intersecting the
                    // nail mesh by up to 4.7 mm, so a pass pushed the buried vertices out along their
                    // normals. It was chasing an artefact â€” the cap hugs the foot exactly in a modelling
                    // package, and a signed distance taken against a thin two-sided nail shell reads
                    // "buried" for vertices that are merely beside it. Whatever is wrong in game is not
                    // the cap sitting under the nails.
                    if (placed is { Count: > 0 }) capPlaced = placed.ToDictionary(p => p.Mesh);
                }
            }
            else if (best != null)
            {
                // Emitting it anyway is what produced the shards; no cap is the better answer. Note this
                // must also call off the CUT â€” the toe-cap map carves the toe box out of the shell for
                // the cap to fill, so declining the cap without declining the cut leaves a hole. capSrc
                // stays non-null so BuildVerbatim does not fall back to GENERATING one; that path is long
                // dead and throws.
                capBytes = best.Value.Cap;
                try { capSrc = Parse(best.Value.Cap); } catch { /* declined anyway */ }
                capDeclined = bestRate is > 0f and < float.MaxValue
                    ? $"{bestRate * 100:F0}% of the toe cap could not be placed on this body"
                    : "no toe cap has been measured against this body";
                diag?.Invoke($"authored cap: DECLINED â€” {capDeclined}; the toes keep the plain shell");
            }
        }
        int baseMatCount = baseSrc?.MatNames.Count ?? 0;
        if (baseMatCount + layers.Count > MaxMaterials)
            throw new InvalidOperationException(
                $"host has {baseMatCount} materials + {layers.Count} layers > {MaxMaterials} max");

        // Union bone list. u16 indices, so hundreds of bones are fine. The host (if any) goes FIRST so its
        // own meshes can remap their bone tables by name.
        var boneNames = new List<string>();
        var boneIndex = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var boneBBox = new List<byte[]>();
        var boneSources = (baseSrc != null ? new[] { baseSrc }.Concat(parsed) : parsed)
            .Concat(capSrc != null ? new[] { capSrc } : []);
        foreach (var src in boneSources)
            for (int i = 0; i < src.BoneNames.Length; i++)
            {
                if (boneIndex.ContainsKey(src.BoneNames[i])) continue;
                boneIndex[src.BoneNames[i]] = (ushort)boneNames.Count;
                boneNames.Add(src.BoneNames[i]);
                var bb = new byte[BBoxSize];
                if ((i + 1) * BBoxSize <= src.BoneBBoxes.Length)
                    Array.Copy(src.BoneBBoxes, i * BBoxSize, bb, 0, BBoxSize);
                boneBBox.Add(bb);
            }

        var vBuf = new MemoryStream();
        var iBuf = new MemoryStream();
        var meshOut = new List<byte[]>();
        var declOut = new List<byte[]>();        // per-mesh vertex declaration (source format, preserved)
        var subOut = new List<byte[]>();
        var boneTables = new List<ushort[]>();   // one per emitted mesh
        var submeshBoneMap = new List<ushort>();
        uint idxCursor = 0;
        ushort subCursor = 0;
        int triIn = 0, triOut = 0, vertOut = 0;
        int shapedTotal = 0;   // index entries rewired to a morphed vertex by an enabled body shape key
        int uvMoved = 0, uvUnmapped = 0;   // vertices put through a UV-space conversion, and those it couldn't place
        int uvRetangented = 0;             // meshes whose tangent frame was re-fitted to the converted UVs

        // The rim of the cap the CURRENT layer is about to graft, already pushed to that layer's offset,
        // and how many shell vertices have been welded onto it. Set before the layer's bodies are emitted
        // so each shell mesh can close onto it as it goes; null on a layer with no cap.
        RimSeg[]? weldRim = null;
        // Which cap vertices (pre-split indices) that rim runs through, so the graft knows which of its
        // own vertices to snap back. Per layer, for the same reason the rim itself is.
        var weldRimVerts = new HashSet<int>();
        // ONE SOURCE OF TRUTH for where the cap's rim ends up. The rim above was worked out from the
        // placement plus the push; the graft used to arrive at the same point a second way, from the
        // placement written into its stream and pushed along the stream's own normal. Two computations of
        // one coordinate agree only to whatever the stream's element type keeps, and everything downstream
        // â€” the weld, both splits â€” is matching positions exactly. So the value is kept here, keyed by the
        // cap mesh and its PRE-SPLIT source index, and the graft reads it back rather than recomputing it.
        var weldRimPos = new Dictionary<(int Mesh, int Src), Vec3>();
        // Every point on the cap's rim that a shell vertex was welded onto, this layer. The cap is split
        // at these when it is emitted, so both boundaries end up with the same vertex positions.
        var capRimLandings = new List<Vec3>();
        int welded = 0, weldWorst = 0;
        float weldWorstD = 0f;

        // ...and the return half: the shell's OWN rim once it has been welded, carrying the normals it
        // had BEFORE averaging, for the cap to be snapped back onto when it is grafted.
        var shellRim = new List<RimSeg>();
        int capWelded = 0;

        // The body's own skin, for re-deriving the skinning of a shell vertex the weld has MOVED. Built
        // once, on first use, because it is the same expensive collection the cap projection makes.
        List<SkinTri>? bodySkin = null;

        // The cap's projection depends only on the cap and the bodies, never on the layer wearing it, and
        // it is the most expensive thing in the build â€” every cap vertex against every skin triangle.
        var capUvCache = new Dictionary<int, CapUvPlan?>();

        // ── THE CAP IS GRAFTED INTO THE SHELL'S OWN MESH ──────────────────────────────────────────────
        // Not beside it. Two meshes in a .mdl have separate vertex buffers, so a cap emitted as its own
        // mesh can only ever hold a COPY of each rim vertex — and a copy agreeing to float precision is
        // not the same vertex. Every attribute equalised at the join (position, normal, uv, skinning)
        // closed a real defect and made the line fainter, and every one of them can be reopened by the
        // next attribute nobody thought of. Emitted into the shell's mesh, the cap's triangles reference
        // the shell's OWN vertex indices along the rim: there is no join left to leak, shade differently
        // or come apart when posed, and no future attribute can undo that.
        //
        // These carry the current layer's cap settings into EmitMesh, which is declared above the layer
        // loop and so cannot see its locals.
        // Every vertex of the cap as it will be placed this layer. The toenail drop needs it, and that
        // has to run for EVERY capped layer — not only the one the cap is grafted into. A second shell
        // over the same toes keeps its own nail patches otherwise, and they float over the toenails as
        // little discs of fabric: measured, an inner shell of 840 triangles that was ten nail rings and
        // almost nothing else.
        var capAllVerts = new List<Vec3>();
        float capPushNow = 0f;
        SecondSkinLayer? capDefNow = null;
        bool capGrafted = false;

        // Emit one source mesh into the merged model. Shared by the host pre-pass (preserve=true: an exact
        // byte copy, keep every triangle, keep the authored material index) and the shell layers
        // (preserve=false: BuildVerbatim's push/colour/uv1 rewrites, coverage-trimmed). Mutates the shared
        // accumulators; `cov` null keeps all triangles; `mapBase`/`mapAppended` share the src's submesh bone
        // map across its meshes.
        void EmitMesh(Source src, int m, ushort materialIndex, float push, bool preserve,
                      SecondSkinLayer? cov, int mapBase, ref bool mapAppended, bool dropConnectors,
                      CapUvPlan? capUv = null)
        {
            var s = src.S;
            uint U32(int o) => BitConverter.ToUInt32(s, o);
            ushort U16(int o) => BitConverter.ToUInt16(s, o);

            int mo = src.MeshStart + m * 36;
            ushort vc = U16(mo);
            if (vc == 0) return;

            ushort srcSubIdx = U16(mo + 10), srcSubCount = U16(mo + 12), srcBoneTbl = U16(mo + 14);
            // Up to three vertex streams (offset + stride each); a v6 MeshStruct carries all three.
            uint[] vbo = { U32(mo + 20), U32(mo + 24), U32(mo + 28) };
            byte[] bs  = { s[mo + 32], s[mo + 33], s[mo + 34] };
            var decl = m < src.Decls.Length ? src.Decls[m] : [];

            byte[][] outStreams; byte[] outStrides; byte[] declBlock;
            // Set when the cap is reskinned: its blend indices then address THIS table, not the one the
            // authored mesh shipped with.
            ushort[]? capBoneTable = null;
            (float U, float V)[] uv;
            Vec3[]? capSrcPos = null, capOutPos = null;   // set only where a toe cap actually moved geometry
            ToeCapPlan? capPlan = null;
            (float U, float V)[]? uvPre = null;
            if (preserve)
            {
                CopyVerbatim(s, src.Vb, 0x44 + m * DeclSize, vc, decl, vbo, bs,
                    out outStreams, out outStrides, out declBlock);
                // The host accessory has no coordinate to trim by; the grafted cap does, and it MUST be
                // trimmed the same way the shell is. Without this every capped layer emitted the entire
                // cap whatever that layer covered â€” so an overlay reaching a sliver of the foot still put
                // a full toe box on top of everything, with its rim attached to nothing.
                uv = capUv is { } uvPlan ? uvPlan.Uv : [];

                // A supplied UV still has to be written, and this path copies bytes rather than going
                // through BuildVerbatim, so it does not happen by itself. The authored toe cap arrives
                // with every vertex at (0,1) â€” one corner texel of the overlay, transparent â€” and looks
                // perfect in a modelling package while rendering as nothing at all in game.
                if (capUv is { } tooBig && tooBig.SourceOf.Length > ushort.MaxValue)
                    diag?.Invoke($"authored cap: {tooBig.SourceOf.Length} vertices after the seam split "
                               + "exceeds a 16-bit index â€” UVs NOT applied, the cap will render blank");
                if (capUv is { } plan && plan.SourceOf.Length <= ushort.MaxValue)
                {
                    // The projection cuts the body's UV seams into the cap, which means vertices on the
                    // seam exist once per chart. Everything but the coordinate is identical, so each copy
                    // is a byte-for-byte duplicate of the vertex it came from, in every stream.
                    int nvNew = plan.SourceOf.Length;
                    var grownStreams = new byte[outStreams.Length][];
                    for (int st = 0; st < outStreams.Length; st++)
                    {
                        int stride = outStrides[st];
                        var g = new byte[nvNew * stride];
                        for (int i = 0; i < nvNew; i++)
                        {
                            int from = plan.SourceOf[i];
                            if (from >= 0 && from < vc)
                                Buffer.BlockCopy(outStreams[st], from * stride, g, i * stride, stride);
                        }
                        grownStreams[st] = g;
                    }
                    outStreams = grownStreams;
                    vc = (ushort)nvNew;

                    // The binding's placement, written into the stream the copies were taken from. Done
                    // before the push and the weld, so both act on a cap that is already on the right
                    // foot. Indexed through SourceOf because the seam split gave some vertices more than
                    // one copy and the binding is per authored vertex.
                    if (capPlaced != null && capPlaced.TryGetValue(m, out var place))
                    {
                        VElem? pP = null, pN = null;
                        foreach (var el in decl)
                        {
                            if (el.Usage == UsePosition) pP ??= el;
                            if (el.Usage == UseNormal) pN ??= el;
                        }
                        if (pP is { } pe4)
                        {
                            int moved = 0;
                            for (int i = 0; i < nvNew; i++)
                            {
                                int from = plan.SourceOf[i];
                                if (from < 0 || from >= place.Pos.Length) continue;
                                WriteXYZ(outStreams[pe4.Stream], i * outStrides[pe4.Stream] + pe4.Offset,
                                         pe4.Type, place.Pos[from].X, place.Pos[from].Y, place.Pos[from].Z);
                                if (pN is { } ne8)
                                    WriteNormal(outStreams[ne8.Stream],
                                                i * outStrides[ne8.Stream] + ne8.Offset, ne8.Type,
                                                place.Nrm[from].X, place.Nrm[from].Y, place.Nrm[from].Z);
                                moved++;
                            }
                            diag?.Invoke($"authored cap: mesh {m} placed onto the equipped body, "
                                       + $"{moved} vertices moved, {place.Missed} left as authored");
                        }
                    }

                    // RESKIN THE CAP TO THE BODY. The cap ships with authored weights, and they are not
                    // wrong so much as unrelated: the shell around it carries the BODY's weights, and
                    // where the two meet they disagreed on 123 of 192 vertex pairs by as much as 12.5%.
                    // Coincident positions with different weights is a bind-pose weld â€” the two edges sit
                    // together in the T-pose and separate as soon as a toe bends, which is every pose
                    // anyone actually sees. Taking the weights from the same body triangle the UV already
                    // comes from makes the cap deform identically to the skin under it, so the join holds
                    // by construction rather than by measurement.
                    //
                    // The bone table grows to fit: the cap's own table is unlikely to name every bone the
                    // body's foot uses, and an index is only meaningful against the table it belongs to.
                    if (plan.Weights.Length == nvNew)
                    {
                        VElem? wEl2 = null, iEl2 = null;
                        foreach (var el in decl)
                        {
                            if (el.Usage == UseBlendWeight) wEl2 ??= el;
                            if (el.Usage == UseBlendIndices) iEl2 ??= el;
                        }
                        // UPGRADE THE CAP TO EIGHT INFLUENCES if it was authored with four. The shell it
                        // welds to is eight-influence, and the two sides of a weld must deform the same
                        // way or the join is only closed in bind pose. Truncating the body's skinning to
                        // the cap's four would do that too, and worse â€” it would coarsen the cap. The
                        // game takes eight, so the cap is widened to match rather than the shell narrowed.
                        //
                        // Only stream 0 is touched, and only when it holds exactly position, weights and
                        // indices â€” which is what a skinned mesh's first stream is. Anything else and the
                        // upgrade is skipped rather than guessed at.
                        if (wEl2 is { } wUp && iEl2 is { } iUp && BlendCount(wUp.Type) == 4
                            && wUp.Stream == 0 && iUp.Stream == 0
                            && decl.Count(e => e.Stream == 0) == 3
                            && decl.Any(e => e.Stream == 0 && e.Usage == UsePosition))
                        {
                            var pEl0 = decl.First(e => e.Stream == 0 && e.Usage == UsePosition);
                            const int wOffNew = 12, iOffNew = 20, strideNew = 28;
                            if (pEl0.Offset == 0 && outStrides[0] >= 20)
                            {
                                var wide = new byte[nvNew * strideNew];
                                for (int v = 0; v < nvNew; v++)
                                {
                                    int from = v * outStrides[0], to = v * strideNew;
                                    Buffer.BlockCopy(outStreams[0], from, wide, to, 12);   // position
                                    // The AUTHORED four influences carry over into the first four slots;
                                    // the rest stay zero. The reskin below only touches the seam band, so
                                    // anything dropped here would leave the cap's interior unweighted.
                                    for (int q = 0; q < 4; q++)
                                    {
                                        wide[to + wOffNew + q] = outStreams[0][from + wUp.Offset + q];
                                        wide[to + iOffNew + q] = outStreams[0][from + iUp.Offset + q];
                                    }
                                }
                                outStreams[0] = wide;
                                outStrides[0] = strideNew;

                                // The declaration has to say so too, or the game reads the old layout.
                                for (int e = 0; e < 17; e++)
                                {
                                    int x = e * 8;
                                    if (declBlock[x] == 0xFF) break;
                                    if (declBlock[x + 3] == UseBlendWeight)
                                    { declBlock[x + 1] = wOffNew; declBlock[x + 2] = 17; }
                                    else if (declBlock[x + 3] == UseBlendIndices)
                                    { declBlock[x + 1] = iOffNew; declBlock[x + 2] = 17; }
                                }
                                decl = decl.Select(e =>
                                    e.Usage == UseBlendWeight ? e with { Offset = wOffNew, Type = 17 } :
                                    e.Usage == UseBlendIndices ? e with { Offset = iOffNew, Type = 17 } : e)
                                    .ToArray();
                                wEl2 = decl.First(e => e.Usage == UseBlendWeight);
                                iEl2 = decl.First(e => e.Usage == UseBlendIndices);
                                diag?.Invoke("authored cap: widened to 8 bone influences to match the shell");
                            }
                        }

                        if (wEl2 is { } we5 && iEl2 is { } ie5)
                        {
                            var srcTbl = srcBoneTbl < src.BoneTables.Length ? src.BoneTables[srcBoneTbl] : [];
                            var tbl = new List<ushort>();
                            var slot = new Dictionary<string, int>();
                            foreach (var bi in srcTbl)
                            {
                                var nmB = bi < src.BoneNames.Length ? src.BoneNames[bi] : null;
                                if (nmB != null && boneIndex.TryGetValue(nmB, out var ui2))
                                { slot.TryAdd(nmB, tbl.Count); tbl.Add(ui2); }
                                else tbl.Add(0);
                            }
                            int reskinned = 0, dropped = 0;
                            int nInfNow = BlendCount(we5.Type);
                            // Hoisted out of the loop: an 8-byte stackalloc per iteration grows the frame
                            // by the iteration count. Cleared at each use below, so reusing one is identical.
                            Span<byte> wb2 = stackalloc byte[8], ib2 = stackalloc byte[8];
                            for (int i = 0; i < nvNew; i++)
                            {
                                // KEEP THE AUTHORED SKINNING. The cap is weighted by hand to the skin AND
                                // the nails; taking the body's skin-only weights instead leaves it unable
                                // to follow a nail at all, and it caves in over the toenail. Only a band
                                // at the back seam is blended toward the body, so the cap and the shell
                                // it welds to still agree where they meet.
                                int srcV = plan.SourceOf[i];
                                int ring = srcV >= 0 && srcV < plan.RimRing.Length ? plan.RimRing[srcV] : int.MaxValue;
                                if (ring >= CapSeamBlendRings) continue;
                                float toBody = 1f - ring / (float)CapSeamBlendRings;

                                var body = plan.Weights[i];
                                if (body.Length == 0) continue;

                                // What the author put here, resolved to names through the cap's own table.
                                var mine = new List<(string Bone, float W)>(nInfNow);
                                {
                                    int wa0 = i * outStrides[we5.Stream] + we5.Offset;
                                    int ia0 = i * outStrides[ie5.Stream] + ie5.Offset;
                                    for (int q = 0; q < nInfNow; q++)
                                    {
                                        float fw = outStreams[we5.Stream][wa0 + q] / 255f;
                                        if (fw <= 0f) continue;
                                        int local = outStreams[ie5.Stream][ia0 + q];
                                        if (local >= srcTbl.Length) continue;
                                        var nm2 = srcTbl[local] < src.BoneNames.Length
                                            ? src.BoneNames[srcTbl[local]] : null;
                                        if (nm2 != null) mine.Add((nm2, fw));
                                    }
                                }
                                var w = mine.Count > 0
                                    ? BlendWeights(mine.ToArray(), 1f - toBody, body, toBody, [], 0f)
                                    : body;
                                if (w.Length == 0) continue;
                                // As many influences as THIS element declares â€” see BlendCount. Anything
                                // not written must be zeroed, or the leftovers skin the vertex too.
                                int nInf = BlendCount(we5.Type);
                                wb2.Clear(); ib2.Clear();
                                int used2 = 0, total = 0;
                                foreach (var (bone, f) in w)
                                {
                                    if (used2 == nInf) break;
                                    if (!slot.TryGetValue(bone, out int at2))
                                    {
                                        if (!boneIndex.TryGetValue(bone, out var ui3)) { dropped++; continue; }
                                        if (tbl.Count >= 255) { dropped++; continue; }
                                        slot[bone] = at2 = tbl.Count;
                                        tbl.Add(ui3);
                                    }
                                    byte q = (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
                                    if (q == 0) continue;
                                    ib2[used2] = (byte)at2; wb2[used2] = q; total += q;
                                    used2++;
                                }
                                if (used2 == 0) continue;
                                // The bytes must come to 255 or the vertex shrinks toward the origin.
                                wb2[0] = (byte)Math.Clamp(wb2[0] + (255 - total), 0, 255);
                                int wo = i * outStrides[we5.Stream] + we5.Offset;
                                int io = i * outStrides[ie5.Stream] + ie5.Offset;
                                for (int q2 = 0; q2 < nInf; q2++)
                                {
                                    outStreams[we5.Stream][wo + q2] = wb2[q2];
                                    outStreams[ie5.Stream][io + q2] = ib2[q2];
                                }
                                reskinned++;
                            }
                            capBoneTable = tbl.ToArray();
                            diag?.Invoke($"authored cap: reskinned {reskinned} vertices from the body, "
                                       + $"bone table {srcTbl.Length} -> {tbl.Count}"
                                       + (dropped > 0 ? $", {dropped} influence(s) dropped" : ""));
                        }
                    }

                    // Two corrections, both on the cap's own vertices, both needed before the UV pass.
                    //
                    // PUSH. The cap is authored ON the skin; the shell it lands in is pushed off it. Left
                    // at zero the shell's cut edge stands proud of the cap by the whole offset â€” 0.001,
                    // better than a quarter of the mesh's own edge â€” as a lip running the length of the
                    // join with bare skin showing in it.
                    //
                    // SHARED NORMAL, but NOT a shared position any more. The cap's rim used to be pulled
                    // onto the shell's here, to make each polyline pass through the other's vertices â€”
                    // the shell's edge otherwise ran as a chord past each cap rim vertex and the lens
                    // between chord and boundary was an open gap, up to 0.00047. The shell is now split at
                    // those vertices instead, which closes the same lens without moving anything: an
                    // authoritative rim cannot be allowed to move, or the coordinates the shell was welded
                    // and split against stop being the ones the cap ships with. The normal is still
                    // averaged against the shell's PRE-average value, so both sides arrive at the same
                    // answer and the join stops shading as a crease â€” which is what makes it read as a
                    // dark line even when there is no light coming through.
                    VElem? pEl = null, nEl = null;
                    foreach (var el in decl)
                    {
                        if (el.Usage == UsePosition) pEl ??= el;
                        if (el.Usage == UseNormal) nEl ??= el;
                    }
                    if (pEl is { } pe3 && nEl is { } ne3 && (push != 0f || shellRim.Count > 0))
                    {
                        Span<float> tmp3 = stackalloc float[4];
                        for (int i = 0; i < vc; i++)
                        {
                            ReadTyped(outStreams[ne3.Stream], i * outStrides[ne3.Stream] + ne3.Offset,
                                      ne3.Type, tmp3);
                            float nx = tmp3[0], ny = tmp3[1], nz = tmp3[2];
                            if (ne3.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
                            var n3 = NormalizeOr(new Vec3(nx, ny, nz), default);
                            if (n3 is { X: 0, Y: 0, Z: 0 }) continue;

                            int po = i * outStrides[pe3.Stream] + pe3.Offset;
                            ReadTyped(outStreams[pe3.Stream], po, pe3.Type, tmp3);
                            int from2 = plan.SourceOf[i];
                            // A rim vertex takes the coordinate the shell was welded and split against,
                            // verbatim. Everything else is pushed the ordinary way. See weldRimPos.
                            var p3 = weldRimPos.TryGetValue((m, from2), out var atRim)
                                ? atRim
                                : new Vec3(tmp3[0] + n3.X * push, tmp3[1] + n3.Y * push,
                                           tmp3[2] + n3.Z * push);

                            // The cap KEEPS its own normal here. It used to average with the shell's, which
                            // worked only for the handful of vertices the weld had moved and left every
                            // split-inserted one disagreeing — a few degrees on the outer layer, tens on
                            // the inner. The shell now adopts the cap rim's normal instead (see the
                            // one-normal-per-position pass in the weld block), so averaging on this side
                            // would pull the two answers apart again.
                            if (shellRim.Count > 0 && weldRimVerts.Contains(from2)) capWelded++;
                            WriteXYZ(outStreams[pe3.Stream], po, pe3.Type, p3.X, p3.Y, p3.Z);
                        }
                    }

                    VElem? u0 = null, u1 = null;
                    foreach (var el in decl)
                        if (el.Usage == UseUV) { if (el.UsageIndex == 0) u0 ??= el; else u1 ??= el; }
                    if (u0 is { } ue2)
                    {
                        // Shifted onto the [0,1] tile the same way every other mesh is: a body UV can
                        // live in another cell, and the overlay is a single tile.
                        float minU = float.MaxValue, minV = float.MaxValue;
                        for (int i = 0; i < vc; i++)
                        { minU = MathF.Min(minU, plan.Uv[i].U); minV = MathF.Min(minV, plan.Uv[i].V); }
                        float capUOff = MathF.Floor(minU), capVOff = MathF.Floor(minV);
                        bool half0 = ue2.Type is 13 or 14;
                        bool zwOk = ue2.Type is 3 or 14;
                        int zwOff2 = ue2.Offset + (ue2.Type == 3 ? 8 : 4);
                        for (int i = 0; i < vc; i++)
                        {
                            float u = plan.Uv[i].U - capUOff, v = plan.Uv[i].V - capVOff;
                            int so = i * outStrides[ue2.Stream];
                            WriteUV2(outStreams[ue2.Stream], so + ue2.Offset, half0, u, v);
                            if (zwOk) WriteUV2(outStreams[ue2.Stream], so + zwOff2, ue2.Type == 14, u, v);
                            if (u1 is { } ue3)
                                WriteUV2(outStreams[ue3.Stream], i * outStrides[ue3.Stream] + ue3.Offset,
                                         ue3.Type is 13 or 14, u, v);
                        }
                    }
                }
            }
            else
            {
                // The toe cap smooths across the mesh's own topology, so it needs the mesh's triangle list
                // BEFORE coverage trimming â€” read it only when a layer actually asks for a cap.
                bool wantCap = cov is { ToeCap: not null } && cov.ToeCapStrength > 0f;
                var capTris = wantCap ? MeshTriangles(src, srcSubIdx, srcSubCount) : null;
                uvUnmapped += BuildVerbatim(s, src.Vb, 0x44 + m * DeclSize, vc, decl, vbo, bs, push,
                    out outStreams, out outStrides, out declBlock, out uv, out uvPre, src.UvConv,
                    out capSrcPos, out capOutPos, out capPlan, cov, capTris, diag,
                    buildCapGeometry: capSrc == null);
                if (src.UvConv != null) uvMoved += vc;

                // The tile normalization above shifts a mesh by the integer floor of its MINIMUM uv, which
                // brings it onto [0,1] only if the whole mesh sits inside one integer cell. Body meshes do;
                // an atlassed or tiled layout (hair especially) may not, and then part of the mesh keeps a
                // coordinate past 1 and samples the art through the sampler's wrap â€” art in the wrong place
                // on one mesh, which looks like a dozen other faults. Reported, not corrected: correcting it
                // per-island is real work and no body model has ever needed it. This is the line that says
                // whether a new surface does.
                if (uv.Length > 0)
                {
                    float uLo = float.MaxValue, uHi = float.MinValue, vLo = float.MaxValue, vHi = float.MinValue;
                    foreach (var (cu, cv) in uv)
                    {
                        if (cu < uLo) uLo = cu; if (cu > uHi) uHi = cu;
                        if (cv < vLo) vLo = cv; if (cv > vHi) vHi = cv;
                    }
                    if (uHi - uLo > 1f || vHi - vLo > 1f)
                        diag?.Invoke($"mesh {m} straddles a UV cell (u {uLo:F2}..{uHi:F2}, v {vLo:F2}..{vHi:F2}) "
                                   + "â€” the per-mesh tile shift cannot bring all of it onto [0,1]");
                }
            }

            // Bake enabled body shape keys (e.g. "Remove Hip Dips" = shpx_yam_softbutt) into the shell. A
            // ShapeValue redirects one index-buffer entry to a morphed replacement vertex that already lives
            // in THIS mesh's vertex buffer (within vc). Rewiring the index makes the shell's triangle use the
            // morphed vertex, and the push/compaction below treat it like any other â€” so the shell follows
            // the body instead of diverging. Only for shell layers (not the host ring) and only for shapes
            // this body has enabled. Bounds-guarded: a replacement >= vc is skipped, so a wrong assumption
            // degrades to "morph not applied", never an out-of-range crash.
            //
            // BaseIndicesIndex is MESH-RELATIVE (0-based within this mesh's own index range), per
            // xivModdingFramework's applier â€” indices[BaseIndex] where indices is the mesh's list. So the
            // lookup below subtracts the mesh's absolute StartIndex from each triangle's position. (Only when
            // StartIndex == 0 do absolute and relative coincide â€” that was the one tested case.)
            uint meshStartIndex = U32(mo + 16);
            Dictionary<int, ushort>? shapeReplace = null;
            if (!preserve && src.EnabledShapes is { Count: > 0 })
            {
                foreach (var shapeName in src.EnabledShapes)
                {
                    if (!src.Shapes.TryGetValue(shapeName, out var entries)) continue;
                    foreach (var e in entries)
                    {
                        if (e.MeshIndexOffset != meshStartIndex) continue;
                        foreach (var (bIdx, rep) in e.Values)
                            if (rep < vc)
                                (shapeReplace ??= new Dictionary<int, ushort>())[bIdx] = rep;   // key = mesh-relative
                    }
                }
            }

            // Keep a triangle if ANY texel under its UV footprint is visible (cov null = keep all).
            var keptPerSub = new List<ushort[]>();
            var used = new bool[vc];
            // Vertices the TOE-CAP cut exposed, as opposed to coverage trimming or any other hole. They
            // are the ones the weld is allowed to drag a long way, because the cap is what is supposed to
            // be filling the space they were pulled back from.
            var cutAway = new bool[vc];
            int cutTris = 0;
            // Position in the cap's flattened corner list. The projection walked the index buffer in
            // exactly this order (submeshes ascending, triangles ascending, nothing skipped), so a simple
            // running cursor lines the two up. Only ever used with dropConnectors off, which is the one
            // thing below that would skip a whole submesh and desync it.
            int cornerCursor = 0;
            for (int su = 0; su < srcSubCount; su++)
            {
                int ss = src.SubmeshStart + (srcSubIdx + su) * 16;
                uint so = U32(ss), sc = U32(ss + 4);
                var keep = new List<ushort>();

                // Drop redundant connector geometry on these bodies (Neolithe): the thin seam RINGS at the
                // joints (wrist/ankle/â€¦, ~100-120 tris â€” real skin parts are 800+), plus the mesh's LAST
                // submesh (a duplicate variant, e.g. the second calf). Kept empty â‡’ contributes nothing;
                // never applied to a single-submesh mesh (that IS the whole part).
                if (dropConnectors && srcSubCount > 1 && (sc / 3 < 200 || su == srcSubCount - 1))
                {
                    keptPerSub.Add(keep.ToArray());
                    continue;
                }
                for (uint t = 0; t + 2 < sc; t += 3)
                {
                    int p = src.Ib + (int)(so + t) * 2;
                    ushort a = BitConverter.ToUInt16(s, p), b = BitConverter.ToUInt16(s, p + 2), c = BitConverter.ToUInt16(s, p + 4);
                    if (capUv is { } cplan)
                    {
                        if (cornerCursor + 2 < cplan.Corner.Length)
                        {
                            a = (ushort)cplan.Corner[cornerCursor];
                            b = (ushort)cplan.Corner[cornerCursor + 1];
                            c = (ushort)cplan.Corner[cornerCursor + 2];
                        }
                        cornerCursor += 3;
                    }
                    // Redirect any of the triangle's three index entries whose (mesh-relative) position
                    // carries a shape edit. so is absolute; subtract meshStartIndex to get the mesh-local
                    // position the shape's BaseIndicesIndex keys are in.
                    if (shapeReplace != null)
                    {
                        int rel = (int)(so + t - meshStartIndex);
                        if (shapeReplace.TryGetValue(rel,     out var ra)) { a = ra; shapedTotal++; }
                        if (shapeReplace.TryGetValue(rel + 1, out var rb)) { b = rb; shapedTotal++; }
                        if (shapeReplace.TryGetValue(rel + 2, out var rc)) { c = rc; shapedTotal++; }
                    }
                    triIn++;
                    if (cov != null && !AnyVisible(cov, uv[a], uv[b], uv[c])) continue;
                    // The toe box was replaced wholesale, so its triangles go; the cap's own are added
                    // below. Anything the cap merely nudged is dropped only if it collapsed outright.
                    if (capPlan != null && capPlan.IsCut(a, b, c))
                    { cutAway[a] = cutAway[b] = cutAway[c] = true; cutTris++; continue; }
                    if (capPlan != null && capPlan.IsDropped(a, b, c)) continue;
                    if (capOutPos != null && CapDegenerate(capSrcPos!, capOutPos, a, b, c)) continue;
                    keep.Add(a); keep.Add(b); keep.Add(c);
                    used[a] = used[b] = used[c] = true;
                    triOut++;
                }
                keptPerSub.Add(keep.ToArray());
            }
            if (cov?.ToeCap != null)
                diag?.Invoke($"toe cap: mesh {m} â€” plan {(capPlan == null ? "NULL" : "present")}, "
                           + $"the cut removed {cutTris} triangle(s)");

            // The rebuilt cap joins the submesh that lost the most geometry to the cut. Its vertices are
            // all reused originals from that region, so they already skin through that submesh's bone
            // window â€” which is the one thing a new triangle here must respect.
            if (capPlan is { NewTriangles.Count: > 0 })
            {
                int host = 0;
                for (int su = 1; su < keptPerSub.Count; su++)
                    if (keptPerSub[su].Length > keptPerSub[host].Length) host = su;

                var grown = new List<ushort>(keptPerSub[host]);
                foreach (var (a, b, c) in capPlan.NewTriangles)
                {
                    if (a >= vc || b >= vc || c >= vc) continue;
                    grown.Add(a); grown.Add(b); grown.Add(c);
                    used[a] = used[b] = used[c] = true;
                    triOut++;
                }
                keptPerSub[host] = grown.ToArray();
            }

            // WATERTIGHT THE JOIN. The shell's lip has already been welded onto this cap's rim, but it
            // landed part-way along rim EDGES, not on rim vertices â€” 2 of 134 coincided. The cap has no
            // vertex at the others, so each is a T-junction and the surfaces separate by a sliver as soon
            // as the two edges are not exactly collinear. That is the fine light seam: the join measures
            // closed surface-to-surface (0.0002) and still leaks, because closed and watertight are
            // different properties and only the first was ever being checked.
            //
            // Splitting the cap's rim at each landing puts a vertex where the shell has one, without
            // moving anything. Snapping the shell to rim vertices instead was tried before and is
            // recorded in NearestOnRim as having collapsed rim triangles outright.
            if (preserve && capUv != null && capRimLandings.Count > 0)
            {
                int added = SplitCapRim(capRimLandings, decl, ref outStreams, outStrides, ref vc,
                                        keptPerSub, ref used, out int onVert, out int offEdge);
                diag?.Invoke($"authored cap: split the CAP's rim at {added} of {capRimLandings.Count} "
                           + $"shell landing(s) â€” {onVert} snapped onto, {offEdge} off the boundary");
                JoinAudit("CAP rim", capRimLandings, keptPerSub, decl, outStreams, outStrides, vc, diag);
            }
            if (keptPerSub.All(k => k.Length == 0)) return;   // paints nothing here

            // The UVs just moved to another layout, so the tangent frame copied in with them no longer
            // describes them. Re-fit while indices still address the source's vertices.
            //
            // BEFORE the weld, which is where it has to be: the weld's mirror split INSERTS vertices, so
            // `vc` grows past the length of `uv`/`uvPre` and re-fitting afterwards indexes off the end of
            // both. The split's new vertices are interpolated copies (see LerpVertex) and carry a frame
            // from the endpoint they came from, so they need no re-fit of their own. Only ever runs for a
            // UV-converted SHELL mesh â€” the cap path is `preserve`, which leaves uvPre null.
            if (uvPre != null && uvPre.Length >= vc && uv.Length >= vc
                && RetangentMesh(outStreams, outStrides, decl, vc, uvPre, uv, keptPerSub))
                uvRetangented++;

            // â”€â”€ weld the cut lip onto the cap's rim â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // The cut is decided in TEXTURE space, from a painted map, so its edge lands wherever the
            // map's texels happen to fall â€” measured, a median of 0.007 from where the cap's boundary
            // actually is, in a ragged line following the texel grid. The cap then either laps over the
            // shell or leaves bare skin showing, and the join reads as a torn edge either way.
            //
            // Each lip vertex slides onto the NEAREST POINT OF A RIM SEGMENT, not onto the nearest rim
            // vertex. The two loops have different vertex counts (56 on the cap against 107 and 90 on the
            // shell), so vertex-to-vertex pairing would collapse several lip vertices onto one rim vertex
            // and tear the triangles between them. The cap is snapped back onto this rim in the graft â€”
            // see the note there for why only doing it in this direction leaves a gap.
            if (weldRim is { Length: > 0 } wr && !preserve)
            {
                VElem? pEl2 = null, nEl2 = null, wEl3 = null, iEl3 = null, uEl0 = null, uEl1 = null;
                foreach (var el in decl)
                {
                    if (el.Usage == UsePosition) pEl2 ??= el;
                    if (el.Usage == UseNormal) nEl2 ??= el;
                    if (el.Usage == UseBlendWeight) wEl3 ??= el;
                    if (el.Usage == UseBlendIndices) iEl3 ??= el;
                    if (el.Usage == UseUV) { if (el.UsageIndex == 0) uEl0 ??= el; else uEl1 ??= el; }
                }
                if (pEl2 is { } pw2)
                {
                    int stride = outStrides[pw2.Stream];
                    var movedV = new bool[vc];
                    var weldPos = new Vec3[vc];
                    var weldNrm = new Vec3[vc];
                    var weldWgt = new (string Bone, float W)[vc][];
                    for (int i = 0; i < vc; i++) weldWgt[i] = [];
                    Span<float> tmpW = stackalloc float[4];

                    // The shell's bone table, grown on demand so a welded vertex can be given the cap's
                    // skinning even when that names a bone this mesh never used.
                    var shellTbl = new List<ushort>();
                    var shellSlot = new Dictionary<string, int>();
                    {
                        var st0 = srcBoneTbl < src.BoneTables.Length ? src.BoneTables[srcBoneTbl] : [];
                        foreach (var bi in st0)
                        {
                            var nmB = bi < src.BoneNames.Length ? src.BoneNames[bi] : null;
                            if (nmB != null && boneIndex.TryGetValue(nmB, out var ui4))
                            { shellSlot.TryAdd(nmB, shellTbl.Count); shellTbl.Add(ui4); }
                            else shellTbl.Add(0);
                        }
                    }
                    int reweighted = 0, uvFixed = 0, fromCapRim = 0;
                    // Hoisted clear of BOTH loops below â€” the rounds and the per-vertex walk. Cleared at
                    // each use, so one buffer behaves identically to a fresh stackalloc per iteration.
                    Span<byte> wb3 = stackalloc byte[8], ib3 = stackalloc byte[8];

                    // TWICE, and the second round is not belt and braces. Dropping a collapsed triangle
                    // EXPOSES vertices that were interior when the boundary was worked out, and those
                    // never got a look â€” measured, one such vertex sat 0.0075 from the rim, two edge
                    // lengths, holding the join open at exactly one spot on the sole while the median
                    // along the rest of it was already 0.000015. A second round costs nothing because
                    // every vertex settled in the first is skipped.
                    for (int round = 0; round < WeldRounds; round++)
                    {
                        // WHAT COUNTS AS THE LIP is the shell's open boundary, not "whatever the toe-cap
                        // cut removed". Keying off the cut alone left every vertex that coverage trimming
                        // exposed along the same line unwelded. A hole is a hole whatever made it.
                        var edgeUses = new Dictionary<(ushort, ushort), int>();
                        foreach (var sub in keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                                for (int k = 0; k < 3; k++)
                                {
                                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                                    var e = (Math.Min(x, y), Math.Max(x, y));
                                    edgeUses[e] = edgeUses.GetValueOrDefault(e) + 1;
                                }
                        var onBoundary = new bool[vc];
                        foreach (var (e, n) in edgeUses)
                            if (n == 1) { onBoundary[e.Item1] = true; onBoundary[e.Item2] = true; }

                        int movedThisRound = 0;
                        for (int i = 0; i < vc; i++)
                        {
                            if (!used[i] || !onBoundary[i] || movedV[i]) continue;
                            int po = i * stride + pw2.Offset;
                            ReadTyped(outStreams[pw2.Stream], po, pw2.Type, tmpW);
                            var p = new Vec3(tmpW[0], tmpW[1], tmpW[2]);

                            // Past the radius the vertex is not on the cap's boundary at all â€” the far
                            // side of a toe, or another hole entirely â€” and dragging it in would fold the
                            // mesh. Counted only on the last round, or the first round's near misses get
                            // reported twice.
                            // How far this vertex may be dragged depends on WHY it is on a boundary. The
                            // toe-cap cut deliberately takes out more than the cap covers â€” 8156 texels
                            // against the cap's 2048 â€” and the weld pulling the lip forward onto the rim
                            // is what closes the difference. On the body the map was painted for that is
                            // a short pull; on another body the atlas differs by about a triangle and the
                            // pull is much longer, and refusing it is what leaves a band of bare skin
                            // across the foot. Every OTHER boundary keeps the short leash, so a coverage
                            // edge or an unrelated hole is still never dragged into the cap.
                            float reach = cutAway[i] ? WeldCutReach : WeldRadius;
                            if (!NearestOnRim(p, wr, reach, out var best, out var capN, out var capW,
                                              out float dist))
                            { if (round == WeldRounds - 1) weldWorst++; continue; }
                            WriteXYZ(outStreams[pw2.Stream], po, pw2.Type, best.X, best.Y, best.Z);
                            movedV[i] = true;
                            weldPos[i] = best;
                            welded++;
                            movedThisRound++;
                            weldWorstD = MathF.Max(weldWorstD, dist);

                            // THE SKINNING MOVES WITH THE VERTEX. This is the half that was missing, and
                            // it is the half that shows: a lip vertex dragged as much as 0.0118 kept the
                            // weights of where it used to be, so it ended up sitting exactly on a cap
                            // vertex skinned from somewhere else. Measured, the pairs that coincided most
                            // closely were the ones that disagreed most â€” mean 8.3% against 0.1% for
                            // pairs a fifth of a millimetre apart. In bind pose that is invisible; posed,
                            // it is the seam.
                            // IT TAKES THE CAP'S WEIGHTS — all of it, not just where it lands on a rim
                            // VERTEX. This vertex is now ON the rim by construction, and `capW` is the
                            // cap's own skinning interpolated along the segment at the same parameter as
                            // the position, the normal and the uv. Give it anything else and the two sides
                            // are welded in BIND POSE ONLY: they hold at rest and separate the instant a
                            // toe bends, which is a dotted line rather than a continuous one, and which
                            // every static measurement here reports as perfectly closed.
                            //
                            // Restricting this to exact rim vertices left 105 of 122 lip vertices on
                            // body-derived weights and the dotting survived. There is no accuracy cost:
                            // CapUvPlan.SrcW is itself BlendWeights of the body triangle each rim vertex
                            // was projected onto, and the cap reskins its seam band from the body over
                            // CapSeamBlendRings — so both sides are body-derived AND equal.
                            //
                            // The old body-at-the-landing override is gone. Its purpose was a lip vertex
                            // dragged into the crevice between two toes following the wrong bones; that
                            // vertex is welded to the cap's rim now, and following the rim is exactly what
                            // keeps it attached to the surface it is joined to.
                            bodySkin ??= CollectSkinTriangles(sourceModels);
                            if (capW.Length > 0) fromCapRim++;

                            // ...AND ITS UV, for exactly the same reason. This vertex has moved as much
                            // as 0.019 to reach the rim and was keeping the coordinate it had before,
                            // which is a different place in the atlas — measured against the body's own
                            // uv, the shell's lip was out by a median of 22 texels of 4096 while the cap
                            // beside it was out by 0.47. Everything on this surface is sampled through
                            // uv: the diffuse, the alpha that makes it sheer, and the normal map that
                            // does most of the shading. Two coincident vertices reading different texels
                            // draw a line along the join however watertight it is, which is why closing
                            // the geometry, matching the normals and ramping the step all left it there.
                            //
                            // Applied as a DELTA rather than written absolutely: BuildVerbatim shifts
                            // each mesh onto the [0,1] tile by the floor of its own minimum, and that
                            // shift is not known here. The difference between two body lookups carries
                            // no tile, so it survives it.
                            if (uEl0 is { } ue4 && i < uv.Length)
                            {
                                var uvWas = NearestUv(p, bodySkin, WeldRadius);
                                var uvNow = NearestUv(best, bodySkin, WeldRadius);
                                if (uvWas is { } w0 && uvNow is { } w1)
                                {
                                    var moved = (U: uv[i].U + (w1.U - w0.U), V: uv[i].V + (w1.V - w0.V));
                                    uv[i] = moved;
                                    bool half4 = ue4.Type is 13 or 14;
                                    int so4 = i * outStrides[ue4.Stream];
                                    WriteUV2(outStreams[ue4.Stream], so4 + ue4.Offset, half4, moved.U, moved.V);
                                    if (ue4.Type is 3 or 14)
                                        WriteUV2(outStreams[ue4.Stream],
                                                 so4 + ue4.Offset + (ue4.Type == 3 ? 8 : 4),
                                                 ue4.Type == 14, moved.U, moved.V);
                                    if (uEl1 is { } ue5)
                                        WriteUV2(outStreams[ue5.Stream],
                                                 i * outStrides[ue5.Stream] + ue5.Offset,
                                                 ue5.Type is 13 or 14, moved.U, moved.V);
                                    uvFixed++;
                                }
                            }

                            weldWgt[i] = capW;
                            if (wEl3 is { } we6 && iEl3 is { } ie6 && capW.Length > 0)
                            {
                                // The shell is EIGHT-influence on this body (type 17, stride 28). Writing
                                // four and leaving the rest was what put 70 of its vertices at 1.7x weight
                                // and tore the triangles around the join.
                                int nInf3 = BlendCount(we6.Type);
                                wb3.Clear(); ib3.Clear();
                                int used3 = 0, total3 = 0;
                                foreach (var (bone, f) in capW)
                                {
                                    if (used3 == nInf3) break;
                                    if (!shellSlot.TryGetValue(bone, out int at3))
                                    {
                                        if (!boneIndex.TryGetValue(bone, out var ui5)) continue;
                                        if (shellTbl.Count >= 255) continue;
                                        shellSlot[bone] = at3 = shellTbl.Count;
                                        shellTbl.Add(ui5);
                                    }
                                    byte q3 = (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
                                    if (q3 == 0) continue;
                                    ib3[used3] = (byte)at3; wb3[used3] = q3; total3 += q3;
                                    used3++;
                                }
                                if (used3 > 0)
                                {
                                    wb3[0] = (byte)Math.Clamp(wb3[0] + (255 - total3), 0, 255);
                                    int wo3 = i * outStrides[we6.Stream] + we6.Offset;
                                    int io3 = i * outStrides[ie6.Stream] + ie6.Offset;
                                    for (int q4 = 0; q4 < nInf3; q4++)
                                    {
                                        outStreams[we6.Stream][wo3 + q4] = wb3[q4];
                                        outStreams[ie6.Stream][io3 + q4] = ib3[q4];
                                    }
                                    reweighted++;
                                }
                            }

                            if (nEl2 is not { } ne4) continue;
                            int no = i * outStrides[ne4.Stream] + ne4.Offset;
                            ReadTyped(outStreams[ne4.Stream], no, ne4.Type, tmpW);
                            float sx = tmpW[0], sy = tmpW[1], sz = tmpW[2];
                            if (ne4.Type == 8) { sx = sx * 2 - 1; sy = sy * 2 - 1; sz = sz * 2 - 1; }
                            var own = NormalizeOr(new Vec3(sx, sy, sz), capN);
                            // Kept BEFORE averaging: the cap has to average against the shell's own
                            // normal, not against a value that already has the cap folded into it, or the
                            // two sides land on different answers and the crease survives.
                            weldNrm[i] = own;
                            var avg = NormalizeOr(new Vec3(own.X + capN.X, own.Y + capN.Y, own.Z + capN.Z), own);
                            WriteNormal(outStreams[ne4.Stream], no, ne4.Type, avg.X, avg.Y, avg.Z);
                        }
                        if (movedThisRound == 0) break;

                        var wp = new Vec3[vc];
                        for (int i = 0; i < vc; i++)
                        {
                            ReadTyped(outStreams[pw2.Stream], i * stride + pw2.Offset, pw2.Type, tmpW);
                            wp[i] = new Vec3(tmpW[0], tmpW[1], tmpW[2]);
                        }

                        // Sliding the lip inevitably flattens a few of the triangles behind it â€” one
                        // measured at an aspect ratio of 720, against 14 for the worst the mesh had
                        // before. They have no area left to draw and the cap covers exactly where they
                        // were, so they go.
                        var lens = new List<float>();
                        foreach (var sub in keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                                for (int k = 0; k < 3; k++)
                                    lens.Add(Dist(wp[sub[t + k]], wp[sub[t + (k + 1) % 3]]));
                        if (lens.Count > 0)
                        {
                            lens.Sort();
                            // BY AREA, not by shortest edge. A triangle can have one very short edge and
                            // still cover ground â€” a long thin one covers exactly the sliver of skin it
                            // is standing on â€” and dropping those is what leaves bare patches along the
                            // join. Only a triangle with no area left is safe to remove, because it was
                            // already drawing nothing.
                            float med = lens[lens.Count / 2];
                            float floorArea = med * med * WeldCollapse * WeldCollapse;
                            int dropped = 0;
                            for (int su = 0; su < keptPerSub.Count; su++)
                            {
                                var sub = keptPerSub[su];
                                var trimmed = new List<ushort>(sub.Length);
                                for (int t = 0; t + 2 < sub.Length; t += 3)
                                {
                                    ushort a3 = sub[t], b3 = sub[t + 1], c3 = sub[t + 2];
                                    bool touched = movedV[a3] || movedV[b3] || movedV[c3];
                                    if (touched && TriArea(wp[a3], wp[b3], wp[c3]) < floorArea)
                                    { triOut--; dropped++; continue; }
                                    trimmed.Add(a3); trimmed.Add(b3); trimmed.Add(c3);
                                }
                                keptPerSub[su] = trimmed.ToArray();
                            }
                            if (dropped > 0)
                                diag?.Invoke($"authored cap: {dropped} collapsed triangle(s) dropped at the join");
                        }
                    }

                    // ── RAMP THE SHELL UP TO THE CAP ─────────────────────────────────────────────────
                    // The cap is authored standing off the skin — measured on Neolithe, 2.62mm against
                    // the shell's 1.00mm, so it floats 1.6mm proud. The weld hauls the lip up to meet the
                    // rim, which closes the join, and leaves the whole of that 1.6mm to be crossed by the
                    // ONE row of triangles behind the lip. A cliff along the length of the rim shades as a
                    // hard line whatever the geometry does, which is why welding and matching normals both
                    // left it there.
                    //
                    // The cap is NOT lowered to meet the shell instead: its clearance over the toes is
                    // only 1.65mm at the closest point, so taking 1.6mm out of it would have the dome
                    // grazing the toe tips — and a fixed subtraction is already recorded as unwelding both
                    // bodies. Spreading the step over a band behind the lip costs nothing but a few
                    // vertices moving a fraction of a millimetre, and the cap never moves at all.
                    if (nEl2 is { } ne6 && weldRim is { Length: > 0 } wr3 && welded > 0 && CapFeather > 0f)
                    {
                        Span<float> tmpF = stackalloc float[4];
                        int ramped = 0;
                        float worstLift = 0f;
                        for (int i = 0; i < vc; i++)
                        {
                            if (!used[i] || movedV[i]) continue;   // the lip itself is already on the rim
                            int po3 = i * outStrides[pw2.Stream] + pw2.Offset;
                            ReadTyped(outStreams[pw2.Stream], po3, pw2.Type, tmpF);
                            var p4 = new Vec3(tmpF[0], tmpF[1], tmpF[2]);
                            if (!NearestOnRim(p4, wr3, CapFeather, out var onRim, out _, out _, out float dRim))
                                continue;

                            ReadTyped(outStreams[ne6.Stream], i * outStrides[ne6.Stream] + ne6.Offset,
                                      ne6.Type, tmpF);
                            float nx2 = tmpF[0], ny2 = tmpF[1], nz2 = tmpF[2];
                            if (ne6.Type == 8) { nx2 = nx2 * 2 - 1; ny2 = ny2 * 2 - 1; nz2 = nz2 * 2 - 1; }
                            var n4 = NormalizeOr(new Vec3(nx2, ny2, nz2), default);
                            if (n4 is { X: 0, Y: 0, Z: 0 }) continue;

                            // How much higher the rim sits, along this vertex's own normal, and how much
                            // of that this vertex should take: all of it at the lip, none at the edge of
                            // the band. Only ever lifts — pulling the shell INTO the body would show the
                            // skin through it.
                            float gap = (onRim.X - p4.X) * n4.X + (onRim.Y - p4.Y) * n4.Y + (onRim.Z - p4.Z) * n4.Z;
                            if (gap <= 0f) continue;
                            float lift = gap * (1f - dRim / CapFeather);
                            if (lift <= 1e-6f) continue;
                            WriteXYZ(outStreams[pw2.Stream], po3, pw2.Type,
                                     p4.X + n4.X * lift, p4.Y + n4.Y * lift, p4.Z + n4.Z * lift);
                            worstLift = MathF.Max(worstLift, lift);
                            ramped++;
                        }
                        if (ramped > 0)
                            diag?.Invoke($"authored cap: ramped {ramped} vertices behind the lip up to the "
                                       + $"cap's standoff (furthest lift {worstLift:F4} over {CapFeather:F3})");
                    }

                    if (welded > 0)
                    {

                        // The welded lip as segments, taken from the triangles that SURVIVED so a chord of
                        // a dropped one cannot pull the cap sideways. Any edge with both ends on the lip
                        // lies along the join; the few that cut across it are chords of the same line and
                        // make no difference to a nearest-point query.
                        var seenEdge = new HashSet<(ushort, ushort)>();
                        var stillOnLip = new bool[vc];
                        foreach (var sub in keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                                for (int k = 0; k < 3; k++)
                                {
                                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                                    if (!movedV[x] || !movedV[y]) continue;
                                    stillOnLip[x] = stillOnLip[y] = true;
                                    if (!seenEdge.Add((Math.Min(x, y), Math.Max(x, y)))) continue;
                                    shellRim.Add(new RimSeg(weldPos[x], weldNrm[x], weldWgt[x],
                                                            weldPos[y], weldNrm[y], weldWgt[y]));
                                }

                        _ = stillOnLip;
                    }

                    // THE OTHER HALF OF WATERTIGHT. The weld put every lip vertex onto a rim SEGMENT, and
                    // the cap is split at those landings when it is grafted â€” that direction is closed.
                    // This is the mirror: the cap's rim has far more vertices than the lip does (256
                    // against 155 on Rue), and the shell has none at most of them, so the lip runs as one
                    // long edge past several cap vertices and parts from the cap by whatever the cap bows
                    // off that chord. Splitting the lip at each one costs no movement â€” the new vertices
                    // land exactly where the cap already is.
                    //
                    // AFTER shellRim is built, deliberately: that list is indexed by the pre-split vertex
                    // numbering, and the split grows it.
                    if (welded > 0 && weldRimPos.Count > 0)
                    {
                        // EVERY cap mesh's rim, deliberately: they all belong to the one cap and all of
                        // them meet this shell. The (mesh, source) key exists for the graft's own
                        // read-back, not to partition the join.
                        var rimPts = weldRimPos.Values.ToList();
                        int mirrored = SplitCapRim(rimPts, decl, ref outStreams, outStrides,
                                                   ref vc, keptPerSub, ref used,
                                                   out int onVert2, out int offEdge2);
                        diag?.Invoke($"authored cap: split the SHELL's lip at {mirrored} of "
                                   + $"{rimPts.Count} cap rim vertex/vertices â€” {onVert2} snapped onto, "
                                   + $"{offEdge2} off the boundary");
                        JoinAudit("SHELL lip", rimPts, keptPerSub, decl, outStreams, outStrides, vc, diag);

                        // EVERY vertex of the FINAL lip is a landing the cap must have a vertex at — not
                        // just the ones the weld moved. Collecting only those left 12 shell rim vertices
                        // with no partner (0.0003 to 0.002 out): vertices the weld skipped because they
                        // were already settled, and the ones this split has just inserted. Both are on the
                        // boundary the cap has to meet, and a vertex on one side with nothing opposite is
                        // a T-junction whatever put it there.
                        //
                        // Read AFTER the split and off the surviving triangles, so a landing can never
                        // name a vertex that the collapse pass has since dropped.
                        var lipUses = new Dictionary<(ushort, ushort), int>();
                        foreach (var sub in keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                                for (int k = 0; k < 3; k++)
                                {
                                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                                    var e = (Math.Min(x, y), Math.Max(x, y));
                                    lipUses[e] = lipUses.GetValueOrDefault(e) + 1;
                                }
                        var lipVerts = new HashSet<ushort>();
                        foreach (var (e, n) in lipUses)
                            if (n == 1) { lipVerts.Add(e.Item1); lipVerts.Add(e.Item2); }

                        Span<float> tmpL = stackalloc float[4];
                        int landed = 0;
                        foreach (var v in lipVerts)
                        {
                            if (v >= vc || !used[v]) continue;
                            ReadTyped(outStreams[pw2.Stream], v * outStrides[pw2.Stream] + pw2.Offset,
                                      pw2.Type, tmpL);
                            var lp = new Vec3(tmpL[0], tmpL[1], tmpL[2]);
                            // Only the stretch that meets the cap. A coverage edge or the ankle cut is
                            // boundary too and has no business being split into the cap.
                            if (!NearestOnRim(lp, wr, CapRimSplitReach, out _, out _, out _, out _)) continue;
                            capRimLandings.Add(lp);
                            landed++;
                        }
                        diag?.Invoke($"authored cap: {landed} lip vertices offered to the cap as landings "
                                   + "so every one of them gets a partner");
                    }

                    // ── ONE NORMAL PER POSITION ALONG THE JOIN ───────────────────────────────────────
                    // The join can be watertight and still show a line. Both splits insert vertices whose
                    // normals come from interpolating the edge they were inserted into — the shell's along
                    // the shell's boundary, the cap's along the cap's — and nothing afterwards reconciles
                    // the two. Measured on the shipped shell: 32 of 219 coincident rim pairs disagreed by
                    // more than a degree and the worst by 7.2, which on a glossy stocking is exactly the
                    // seam being chased. The weld's own averaging only ever covered the vertices it moved.
                    //
                    // Fixed by making the normal a function of POSITION rather than of whichever mesh is
                    // asking: every lip vertex takes the cap rim's normal at the point it sits on. The cap
                    // keeps its own (see the graft's push block, which no longer averages), and that IS
                    // this value — NearestOnRim interpolates the same segment at the same parameter — so
                    // the two sides land on one answer by construction, whatever the tessellation.
                    if (nEl2 is { } ne5 && weldRim is { Length: > 0 } wr2)
                    {
                        var lipNow = new bool[vc];
                        var uses = new Dictionary<(ushort, ushort), int>();
                        foreach (var sub in keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                                for (int k = 0; k < 3; k++)
                                {
                                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                                    var e = (Math.Min(x, y), Math.Max(x, y));
                                    uses[e] = uses.GetValueOrDefault(e) + 1;
                                }
                        foreach (var (e, n) in uses)
                            if (n == 1) { lipNow[e.Item1] = true; lipNow[e.Item2] = true; }

                        Span<float> tmpN = stackalloc float[4];
                        int reshaded = 0, reuved = 0;
                        for (int i = 0; i < vc; i++)
                        {
                            if (!lipNow[i] || !used[i]) continue;
                            int po2 = i * outStrides[pw2.Stream] + pw2.Offset;
                            ReadTyped(outStreams[pw2.Stream], po2, pw2.Type, tmpN);
                            var here = new Vec3(tmpN[0], tmpN[1], tmpN[2]);
                            // Only where the cap actually is. Coverage cuts and the ankle are boundary too,
                            // and they have no cap normal to take.
                            if (!NearestOnRim(here, wr2, CapWeldRadius, out _, out var capN2, out _, out _,
                                              out var capUvAt))
                                continue;
                            WriteNormal(outStreams[ne5.Stream], i * outStrides[ne5.Stream] + ne5.Offset,
                                        ne5.Type, capN2.X, capN2.Y, capN2.Z);
                            reshaded++;

                            // ── AND ONE UV PER POSITION, for the same reason ──────────────────────────
                            // Measured with the mask shell switched off, so this is the visible fabric
                            // alone: 44 of 211 shared rim positions had a cap coordinate with no matching
                            // shell coordinate, the worst 674 texels of 4096 apart, clustered at the two
                            // ends of the rim. Both sides sit at one point, shade identically, and then
                            // read unrelated texture — the diffuse, the alpha that makes it sheer, and the
                            // normal map all break at once. 21% of the rim doing that is a dashed line.
                            //
                            // Only where the cap's own coordinate is unambiguous: at the body's atlas seam
                            // a rim vertex carries one per chart and there is no single value to share.
                            if (uEl0 is not { } ue6 || i >= uv.Length || capUvAt is not { } capUv2) continue;

                            // Keep the shell's own TILE. BuildVerbatim shifts each mesh onto [0,1] by the
                            // floor of its own minimum and the cap does the same with its own, so the two
                            // can differ by whole tiles. Rounding the difference takes the cap's position
                            // within the atlas and leaves the tile alone; the disagreement being fixed is
                            // fractional (0.09 uv), so this rounds to zero and the cap's value wins.
                            float cu = capUv2.U, cvv = capUv2.V;
                            cu += MathF.Round(uv[i].U - cu);
                            cvv += MathF.Round(uv[i].V - cvv);
                            uv[i] = (cu, cvv);
                            bool half6 = ue6.Type is 13 or 14;
                            int so6 = i * outStrides[ue6.Stream];
                            WriteUV2(outStreams[ue6.Stream], so6 + ue6.Offset, half6, cu, cvv);
                            if (ue6.Type is 3 or 14)
                                WriteUV2(outStreams[ue6.Stream], so6 + ue6.Offset + (ue6.Type == 3 ? 8 : 4),
                                         ue6.Type == 14, cu, cvv);
                            if (uEl1 is { } ue7)
                                WriteUV2(outStreams[ue7.Stream], i * outStrides[ue7.Stream] + ue7.Offset,
                                         ue7.Type is 13 or 14, cu, cvv);
                            reuved++;
                        }
                        if (reshaded > 0)
                            diag?.Invoke($"authored cap: {reshaded} lip vertices took the cap rim's normal "
                                       + $"and {reuved} took its uv, so both sides of the join shade and "
                                       + "sample as one surface");
                    }
                    if (reweighted > 0)
                    {
                        capBoneTable = shellTbl.ToArray();
                        diag?.Invoke($"authored cap: {reweighted} welded shell vertices took the rim's "
                                   + $"skinning, bone table -> {shellTbl.Count}");
                    }
                    if (uvFixed > 0)
                        diag?.Invoke($"authored cap: {uvFixed} welded shell vertices re-read their uv from "
                                   + "the body at where they landed");
                    if (fromCapRim > 0)
                        diag?.Invoke($"authored cap: {fromCapRim} lip vertices took the cap rim's skinning, "
                                   + "so the pair cannot separate when posed");

                    // ── GRAFT THE CAP INTO THIS MESH ─────────────────────────────────────────────────
                    // See the note on capGrafted. Here, at the end of the weld, because this is the last
                    // moment the shell's vertices are still addressable by their own indices — the
                    // compaction below renumbers everything — and because the rim the cap has to share is
                    // exactly what the weld and the two splits have just finished agreeing on.
                    if (!capGrafted && capSrc is { } gcap && capDefNow is { } gdef && welded > 0)
                    {
                        int reused = 0, added = 0, capTriCount = 0;
                        var capVerts = new List<Vec3>();
                        // Where the shell already has a vertex. The cap reuses these rather than emitting
                        // its own copy — that is the whole point: at the rim there is one vertex, not two.
                        var atPos = new Dictionary<(int, int, int), ushort>();
                        Span<float> tmpG = stackalloc float[4];
                        for (int i = 0; i < vc; i++)
                        {
                            if (!used[i]) continue;
                            ReadTyped(outStreams[pw2.Stream], i * outStrides[pw2.Stream] + pw2.Offset,
                                      pw2.Type, tmpG);
                            atPos[QuantPos(tmpG[0], tmpG[1], tmpG[2])] = (ushort)i;
                        }

                        VElem? gN = null, gU0 = null, gU1 = null, gW = null, gI = null;
                        foreach (var el in decl)
                        {
                            if (el.Usage == UseNormal) gN ??= el;
                            if (el.Usage == UseBlendWeight) gW ??= el;
                            if (el.Usage == UseBlendIndices) gI ??= el;
                            if (el.Usage == UseUV) { if (el.UsageIndex == 0) gU0 ??= el; else gU1 ??= el; }
                        }

                        var newTris = new List<ushort>();
                        int gEnd = gcap.Lod0MeshIndex + gcap.Lod0MeshCount;
                        for (int cm = gcap.Lod0MeshIndex; cm < gEnd && cm < gcap.MeshCount; cm++)
                        {
                            if (BitConverter.ToUInt16(gcap.S, gcap.MeshStart + cm * 36) == 0) continue;
                            if (!capUvCache.TryGetValue(cm, out var gpl) || gpl == null) continue;
                            if (capPlaced == null || !capPlaced.TryGetValue(cm, out var gplace)) continue;

                            int cmLocal = cm;
                            Vec3 Final(int s) => weldRimPos.TryGetValue((cmLocal, s), out var atRim)
                                ? atRim
                                : new Vec3(gplace.Pos[s].X + gplace.Nrm[s].X * capPushNow,
                                           gplace.Pos[s].Y + gplace.Nrm[s].Y * capPushNow,
                                           gplace.Pos[s].Z + gplace.Nrm[s].Z * capPushNow);

                            // The shell shifts its UVs onto the [0,1] tile by the floor of its own minimum;
                            // the cap's come straight from the body. Take the whole-tile difference off a
                            // vertex the two already share, so the cap lands in the shell's tile.
                            // A shell vertex to seed every grafted one from, so colour, tangent and
                            // anything else not written below arrives valid rather than zero.
                            ushort template = 0;
                            bool haveTemplate = false;
                            float shU = 0f, shV = 0f;
                            for (int oi = 0; oi < gpl.SourceOf.Length; oi++)
                            {
                                int sv0 = gpl.SourceOf[oi];
                                if (sv0 < 0 || sv0 >= gplace.Pos.Length || oi >= gpl.Uv.Length) continue;
                                var fp0 = Final(sv0);
                                if (!atPos.TryGetValue(QuantPos(fp0.X, fp0.Y, fp0.Z), out ushort sv)) continue;
                                if (sv >= uv.Length) continue;
                                shU = MathF.Round(uv[sv].U - gpl.Uv[oi].U);
                                shV = MathF.Round(uv[sv].V - gpl.Uv[oi].V);
                                template = sv; haveTemplate = true;
                                break;
                            }

                            if (!haveTemplate)
                            {
                                diag?.Invoke($"authored cap: mesh {cm} shares no vertex with this shell - "
                                           + "not grafting it here");
                                continue;
                            }

                            var map = new ushort[gpl.SourceOf.Length];
                            var have = new bool[gpl.SourceOf.Length];
                            for (int f = 0; f * 3 + 2 < gpl.Corner.Length; f++)
                            {
                                int c0 = gpl.Corner[f * 3], c1 = gpl.Corner[f * 3 + 1], c2 = gpl.Corner[f * 3 + 2];
                                if (c0 >= gpl.Uv.Length || c1 >= gpl.Uv.Length || c2 >= gpl.Uv.Length) continue;
                                if (gdef.Coverage != null
                                    && !AnyVisible(gdef, gpl.Uv[c0], gpl.Uv[c1], gpl.Uv[c2])) continue;

                                bool ok = true;
                                foreach (int c in new[] { c0, c1, c2 })
                                {
                                    if (have[c]) continue;
                                    int cs2 = gpl.SourceOf[c];
                                    if (cs2 < 0 || cs2 >= gplace.Pos.Length) { ok = false; break; }
                                    var fp = Final(cs2);
                                    if (atPos.TryGetValue(QuantPos(fp.X, fp.Y, fp.Z), out ushort sv))
                                    { map[c] = sv; have[c] = true; reused++; capVerts.Add(fp); continue; }
                                    if (vc >= ushort.MaxValue - 4) { ok = false; break; }

                                    ushort nv2 = GrowOne(ref outStreams, outStrides, ref vc, ref used, template);
                                    WriteXYZ(outStreams[pw2.Stream], nv2 * outStrides[pw2.Stream] + pw2.Offset,
                                             pw2.Type, fp.X, fp.Y, fp.Z);
                                    if (gN is { } ne9)
                                        WriteNormal(outStreams[ne9.Stream],
                                                    nv2 * outStrides[ne9.Stream] + ne9.Offset, ne9.Type,
                                                    gplace.Nrm[cs2].X, gplace.Nrm[cs2].Y, gplace.Nrm[cs2].Z);
                                    if (gU0 is { } ue8)
                                    {
                                        float cu2 = gpl.Uv[c].U + shU, cv2 = gpl.Uv[c].V + shV;
                                        bool h8 = ue8.Type is 13 or 14;
                                        int so8 = nv2 * outStrides[ue8.Stream];
                                        WriteUV2(outStreams[ue8.Stream], so8 + ue8.Offset, h8, cu2, cv2);
                                        if (ue8.Type is 3 or 14)
                                            WriteUV2(outStreams[ue8.Stream],
                                                     so8 + ue8.Offset + (ue8.Type == 3 ? 8 : 4),
                                                     ue8.Type == 14, cu2, cv2);
                                        if (gU1 is { } ue9)
                                            WriteUV2(outStreams[ue9.Stream],
                                                     nv2 * outStrides[ue9.Stream] + ue9.Offset,
                                                     ue9.Type is 13 or 14, cu2, cv2);
                                    }
                                    // Skinning by NAME into this mesh's own table, the same route the
                                    // welded lip takes, so one table serves the merged mesh.
                                    if (gW is { } we9 && gI is { } ie9 && cs2 < gpl.SrcW.Length)
                                        WriteSkinNamed(outStreams, outStrides, we9, ie9, nv2, gpl.SrcW[cs2],
                                                       shellSlot, shellTbl, boneIndex);
                                    map[c] = nv2; have[c] = true; added++; capVerts.Add(fp);
                                }
                                if (!ok) continue;
                                newTris.Add(map[c0]); newTris.Add(map[c1]); newTris.Add(map[c2]);
                                capTriCount++;
                            }
                        }

                        if (newTris.Count > 0)
                        {
                            // Into the submesh that lost the most to the cut â€” its bone window already
                            // covers this region, the same reasoning the generated cap's fill uses.
                            int host = 0;
                            for (int su = 1; su < keptPerSub.Count; su++)
                                if (keptPerSub[su].Length > keptPerSub[host].Length) host = su;
                            var grown = new List<ushort>(keptPerSub[host]);
                            grown.AddRange(newTris);
                            keptPerSub[host] = grown.ToArray();
                            foreach (var t in newTris) used[t] = true;
                            triOut += capTriCount;
                            capBoneTable = shellTbl.ToArray();
                            capGrafted = true;
                            diag?.Invoke($"authored cap: grafted INTO the shell mesh â€” {capTriCount} triangle(s), "
                                       + $"{reused} vertex references shared with the shell, {added} added; "
                                       + "the join is interior edges now, not two boundaries");

                            // MERGING IS NOT ENOUGH ON ITS OWN. The cap's rim carries the 112 vertices it
                            // was projected with; the shell's lip carries those PLUS everything the weld
                            // and the mirror split put between them - 215 in all. One mesh or two, an edge
                            // spanning several vertices of the edge opposite is still a T-junction and
                            // still leaks. Splitting the cap's rim at every lip position makes the two
                            // runs share EDGES, not just vertices, and only then is the join interior.
                            // Both runs already occupy the same curve, so ask per EDGE which positions
                            // lie on it rather than per landing which edge is nearest - see
                            // StitchBoundaryAt. Fed both runs' vertices, so each is split at the other's.
                            var rimPts2 = new List<Vec3>(capRimLandings);
                            rimPts2.AddRange(weldRimPos.Values);
                            int stitched = StitchBoundaryAt(rimPts2, decl, ref outStreams, outStrides,
                                                            ref vc, keptPerSub, ref used);
                            diag?.Invoke($"authored cap: stitched the merged rim - {stitched} vertex/vertices "
                                       + $"inserted and {StitchShared} split point(s) reused a vertex the mesh "
                                       + $"already had, so the two runs share edges ({rimPts2.Count} positions)");

                        }
                    }

                    // EVERY capped layer, not just the one the cap was grafted into. A second shell over
                    // the same toes keeps its own nail patches otherwise.
                    if (capAllVerts.Count > 0 && capDefNow != null)
                        // ── DROP THE TOENAIL PATCHES ─────────────────────────────────────────────
                        // The shell is a displaced copy of the body, nails included, and the nails are
                        // their own UV islands: the cap's footprint is in body UV and never covers
                        // them, so the cut leaves each one as a small closed patch of fabric floating
                        // under the cap with an open ring around it. Measured on Neolithe: ten
                        // components of 92-188 triangles at the toe positions, 172 open edges between
                        // them. They are inside a cap that already covers the toes, so they draw
                        // nothing but their own rim.
                        //
                        // Identified by what they ARE rather than by size alone: a component that is
                        // small AND lies entirely within a whisker of the cap's own vertices. The feet
                        // fail the second test by a mile (they run back to the ankle), so the only
                        // things this can take are patches the cap is already covering.
                        {
                            var nodeOfPos = new Dictionary<(int, int, int), int>();
                            var parent = new List<int>();
                            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                            void Union(int a2, int b2) { int ra = Find(a2), rb = Find(b2); if (ra != rb) parent[ra] = rb; }
                            Span<float> tmpC = stackalloc float[4];
                            var posOf = new Vec3[vc];
                            var node = new int[vc];
                            for (int i = 0; i < vc; i++) node[i] = -1;
                            for (int i = 0; i < vc; i++)
                            {
                                if (!used[i]) continue;
                                ReadTyped(outStreams[pw2.Stream], i * outStrides[pw2.Stream] + pw2.Offset,
                                          pw2.Type, tmpC);
                                posOf[i] = new Vec3(tmpC[0], tmpC[1], tmpC[2]);
                                var k = QuantPos(tmpC[0], tmpC[1], tmpC[2]);
                                if (!nodeOfPos.TryGetValue(k, out int n)) { nodeOfPos[k] = n = parent.Count; parent.Add(n); }
                                node[i] = n;
                            }
                            foreach (var sub in keptPerSub)
                                for (int t = 0; t + 2 < sub.Length; t += 3)
                                {
                                    if (node[sub[t]] < 0 || node[sub[t + 1]] < 0 || node[sub[t + 2]] < 0) continue;
                                    Union(node[sub[t]], node[sub[t + 1]]);
                                    Union(node[sub[t + 1]], node[sub[t + 2]]);
                                }
                            var count = new Dictionary<int, int>();
                            foreach (var sub in keptPerSub)
                                for (int t = 0; t + 2 < sub.Length; t += 3)
                                {
                                    if (node[sub[t]] < 0) continue;
                                    int r = Find(node[sub[t]]);
                                    count[r] = count.GetValueOrDefault(r) + 1;
                                }
                            int biggest = 0;
                            foreach (int n in count.Values) biggest = Math.Max(biggest, n);
                            diag?.Invoke($"NAILPROBE comps={count.Count} biggest={biggest} capVerts={capAllVerts.Count}");

                            var capAt = new HashSet<(int, int, int)>();
                            foreach (var cp in capAllVerts) capAt.Add(QuantPos(cp.X, cp.Y, cp.Z));
                            bool NearCap(Vec3 q)
                            {
                                foreach (var cp in capAllVerts)
                                    if (Dist(q, cp) <= NailUnderCap) return true;
                                return false;
                            }
                            var drop = new HashSet<int>();
                            foreach (var (root, n) in count)
                            {
                                    // An ABSOLUTE ceiling, not a fraction of the biggest here: an inner shell
                                    // whose surviving geometry is nothing BUT ten nail patches has them all at
                                    // one size, so none is small next to the others and every one survives
                                    // (measured: comps=10, biggest=248, nothing dropped). The near-cap test
                                    // below is what identifies these; this only bounds the cost of asking.
                                    if (n > NailIslandMaxTris) continue;
                                drop.Add(root);
                            }
                            if (drop.Count > 0)
                            {
                                // Confirm each candidate really is under the cap before taking it.
                                var byRoot = new Dictionary<int, List<ushort>>();
                                for (ushort i = 0; i < vc; i++)
                                {
                                    if (node[i] < 0) continue;
                                    int r = Find(node[i]);
                                    if (!drop.Contains(r)) continue;
                                    (byRoot.TryGetValue(r, out var l) ? l : byRoot[r] = new List<ushort>()).Add(i);
                                }
                                foreach (var (r, vs) in byRoot)
                                    foreach (var i in vs)
                                        if (!NearCap(posOf[i])) { drop.Remove(r); break; }
                            }
                            if (drop.Count > 0)
                            {
                                int gone = 0;
                                for (int su = 0; su < keptPerSub.Count; su++)
                                {
                                    var sub = keptPerSub[su];
                                    var keepT = new List<ushort>(sub.Length);
                                    for (int t = 0; t + 2 < sub.Length; t += 3)
                                    {
                                        if (node[sub[t]] >= 0 && drop.Contains(Find(node[sub[t]]))) { gone++; continue; }
                                        keepT.Add(sub[t]); keepT.Add(sub[t + 1]); keepT.Add(sub[t + 2]);
                                    }
                                    keptPerSub[su] = keepT.ToArray();
                                }
                                triOut -= gone;
                                diag?.Invoke($"authored cap: dropped {drop.Count} toenail patch(es) under the "
                                           + $"cap, {gone} triangle(s) - they drew nothing but their own rim");
                            }
                        }

                    // Whatever is left. Several passes above can each drop a triangle or two along the
                    // join, and a two-triangle hole is a bright polygon of bare skin in game.
                    int holesShut = FillSmallHoles(keptPerSub, vc, ref used, SmallHoleEdges);
                    if (holesShut > 0)
                        diag?.Invoke($"authored cap: closed {holesShut} small hole(s) left along the join");
                    // ── RAISE ANYTHING THE SKIN POKES THROUGH ────────────────────────────────────
                    // The shell is pushed 1 mm off the body, but the weld drags a lip vertex onto the
                    // cap's RIM and the cap sits wherever its binding places it on a body it was not
                    // modelled against. Either can leave the surface BETWEEN two clear vertices cutting
                    // under the body's own curve, and skin a hair proud of a shell is a bright patch of
                    // bare foot. Asymmetric, because which vertices fall short depends on the body and
                    // not on the cap — reported from a modelling package as a few faces needing a lift.
                    //
                    // Driven from the BODY, deliberately. Asking each shell vertex for its distance to
                    // the nearest skin point reports everything clear, because the vertices ARE clear;
                    // what shows through is the body bulging past the flat triangle between them.
                    //
                    // Only the strays: MinSkinClearance is well under the push, so anything already
                    // standing off is untouched, and MaxSkinLift stops this reshaping a surface that is
                    // low for a reason rather than by accident.
                    if (bodySkin != null && capAllVerts.Count > 0 && nEl2 is { } ne10)
                    {
                        float lx = float.MaxValue, ly = float.MaxValue, lz = float.MaxValue;
                        float hx = float.MinValue, hy = float.MinValue, hz = float.MinValue;
                        foreach (var q0 in capAllVerts)
                        {
                            lx = MathF.Min(lx, q0.X); hx = MathF.Max(hx, q0.X);
                            ly = MathF.Min(ly, q0.Y); hy = MathF.Max(hy, q0.Y);
                            lz = MathF.Min(lz, q0.Z); hz = MathF.Max(hz, q0.Z);
                        }
                        const float pad = 0.01f;
                        // Tested against the shell's TRIANGLES, not its nearest vertex. Every vertex
                        // around the toes stands a clean 1 mm off the body and a vertex-to-vertex test
                        // duly reports the whole surface clear; what shows through is the body's curve
                        // rising past the flat triangle spanning them, which is why this reads in a
                        // modelling package as "these faces need raising the slightest amount" and why
                        // it lands on one foot and not the other (measured: 17 body vertices out through
                        // the right foot's triangles, 6 through the left, up to 0.32 mm proud).
                        const float cell = 0.004f;
                        (int, int, int) Cell(Vec3 q) => ((int)MathF.Floor(q.X / cell),
                                                         (int)MathF.Floor(q.Y / cell),
                                                         (int)MathF.Floor(q.Z / cell));
                        Span<float> tmpR = stackalloc float[4];
                        var shellPos = new Vec3[vc];
                        for (int i = 0; i < vc; i++)
                        {
                            if (!used[i]) continue;
                            ReadTyped(outStreams[pw2.Stream], i * outStrides[pw2.Stream] + pw2.Offset,
                                      pw2.Type, tmpR);
                            shellPos[i] = new Vec3(tmpR[0], tmpR[1], tmpR[2]);
                        }

                        // Every surviving triangle near the cap, registered in each cell its bounding box
                        // touches so a body vertex can find the ones it might be standing through.
                        var triHash = new Dictionary<(int, int, int), List<(ushort, ushort, ushort)>>();
                        foreach (var sub in keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                            {
                                ushort ia = sub[t], ib = sub[t + 1], ic = sub[t + 2];
                                if (!used[ia] || !used[ib] || !used[ic]) continue;
                                Vec3 a = shellPos[ia], b = shellPos[ib], c = shellPos[ic];
                                float tlx = MathF.Min(a.X, MathF.Min(b.X, c.X));
                                float thx = MathF.Max(a.X, MathF.Max(b.X, c.X));
                                float tly = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
                                float thy = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
                                float tlz = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
                                float thz = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
                                if (thx < lx - pad || tlx > hx + pad || thy < ly - pad || tly > hy + pad
                                    || thz < lz - pad || tlz > hz + pad) continue;
                                var k0 = Cell(new Vec3(tlx, tly, tlz));
                                var k1 = Cell(new Vec3(thx, thy, thz));
                                for (int cx = k0.Item1; cx <= k1.Item1; cx++)
                                for (int cy = k0.Item2; cy <= k1.Item2; cy++)
                                for (int cz = k0.Item3; cz <= k1.Item3; cz++)
                                {
                                    var k = (cx, cy, cz);
                                    (triHash.TryGetValue(k, out var l)
                                        ? l : triHash[k] = new List<(ushort, ushort, ushort)>()).Add((ia, ib, ic));
                                }
                            }

                        var lift = new float[vc];
                        foreach (var t in bodySkin)
                            foreach (var (bp, bn) in new[] { (t.A, t.Na), (t.B, t.Nb), (t.C, t.Nc) })
                            {
                                if (bp.X < lx - pad || bp.X > hx + pad || bp.Y < ly - pad || bp.Y > hy + pad
                                    || bp.Z < lz - pad || bp.Z > hz + pad) continue;
                                var n2 = NormalizeOr(bn, default);
                                if (n2 is { X: 0, Y: 0, Z: 0 }) continue;
                                if (!triHash.TryGetValue(Cell(bp), out var near)) continue;

                                foreach (var (ia, ib, ic) in near)
                                {
                                    Vec3 a = shellPos[ia], b = shellPos[ib], c = shellPos[ic];
                                    var e1 = new Vec3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                                    var e2 = new Vec3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                                    var fn = NormalizeOr(new Vec3(e1.Y * e2.Z - e1.Z * e2.Y,
                                                                  e1.Z * e2.X - e1.X * e2.Z,
                                                                  e1.X * e2.Y - e1.Y * e2.X), default);
                                    if (fn is { X: 0, Y: 0, Z: 0 }) continue;
                                    // Orient outward, by the body's own normal: winding alone cannot be
                                    // trusted across a mesh assembled from several sources.
                                    if (fn.X * n2.X + fn.Y * n2.Y + fn.Z * n2.Z < 0)
                                        fn = new Vec3(-fn.X, -fn.Y, -fn.Z);

                                    // How far the triangle's plane stands above this body vertex.
                                    float h = (a.X - bp.X) * fn.X + (a.Y - bp.Y) * fn.Y + (a.Z - bp.Z) * fn.Z;
                                    if (h >= MinSkinClearance || h < -MaxSkinLift) continue;

                                    // Only if the body vertex is actually UNDER this triangle: the plane
                                    // of a triangle elsewhere on the foot says nothing about this spot.
                                    var q = new Vec3(bp.X + fn.X * h, bp.Y + fn.Y * h, bp.Z + fn.Z * h);
                                    bool inside = true;
                                    foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                                    {
                                        var ev = new Vec3(v.X - u.X, v.Y - u.Y, v.Z - u.Z);
                                        var qv = new Vec3(q.X - u.X, q.Y - u.Y, q.Z - u.Z);
                                        float side = (ev.Y * qv.Z - ev.Z * qv.Y) * fn.X
                                                   + (ev.Z * qv.X - ev.X * qv.Z) * fn.Y
                                                   + (ev.X * qv.Y - ev.Y * qv.X) * fn.Z;
                                        if (side < -1e-9f) { inside = false; break; }
                                    }
                                    if (!inside) continue;

                                    // Lift the whole face — one corner is not what the skin came through.
                                    float need = MathF.Min(MinSkinClearance - h, MaxSkinLift);
                                    lift[ia] = MathF.Max(lift[ia], need);
                                    lift[ib] = MathF.Max(lift[ib], need);
                                    lift[ic] = MathF.Max(lift[ic], need);
                                }
                            }

                        int raised = 0;
                        float worstLift2 = 0f;
                        for (int i = 0; i < vc; i++)
                        {
                            if (!used[i] || lift[i] <= 1e-6f) continue;
                            ReadTyped(outStreams[ne10.Stream], i * outStrides[ne10.Stream] + ne10.Offset,
                                      ne10.Type, tmpR);
                            float rx = tmpR[0], ry = tmpR[1], rz = tmpR[2];
                            if (ne10.Type == 8) { rx = rx * 2 - 1; ry = ry * 2 - 1; rz = rz * 2 - 1; }
                            var rn = NormalizeOr(new Vec3(rx, ry, rz), default);
                            if (rn is { X: 0, Y: 0, Z: 0 }) continue;
                            var sp = shellPos[i];
                            WriteXYZ(outStreams[pw2.Stream], i * outStrides[pw2.Stream] + pw2.Offset, pw2.Type,
                                     sp.X + rn.X * lift[i], sp.Y + rn.Y * lift[i], sp.Z + rn.Z * lift[i]);
                            worstLift2 = MathF.Max(worstLift2, lift[i]);
                            raised++;
                        }
                        if (raised > 0)
                            diag?.Invoke($"authored cap: raised {raised} vertex/vertices clear of the skin "
                                       + $"(furthest {worstLift2:F5}) so it cannot show through");
                    }
                }
            }

            if (!mapAppended)
            {
                // The map's ENTRIES are indices into THIS source's own bone-name list, so they need the same
                // by-name remap onto the union list that the mesh bone tables get below â€” only the OFFSETS
                // into the map are rebased (mapBase, written into the submesh header as boneStart).
                //
                // Appended verbatim, they were identity-correct for exactly one source: whichever seeded the
                // union list first (the host when appending, else source 0). Every later source's entries
                // then named arbitrary union bones. It has never shown because today's sources are body parts
                // from one body mod, whose bone lists match in both content and order â€” merge a model with a
                // genuinely different skeleton subset beside them and the identity is gone.
                foreach (var b in src.SubmeshBoneMap)
                {
                    var bn = b < src.BoneNames.Length ? src.BoneNames[b] : null;
                    submeshBoneMap.Add(bn != null && boneIndex.TryGetValue(bn, out var bi) ? bi : (ushort)0);
                }
                mapAppended = true;
            }

            // WHAT SURVIVED, recounted. `used` was filled while triangles were being kept, but the weld
            // drops collapsed ones afterwards, and a vertex referenced only by those stayed marked used â€”
            // so it was emitted with nothing pointing at it. Measured on the shipped shell: 51 loose
            // vertices, all along the toe join. They render as nothing, but they are visible in a
            // modelling package and they are the fingerprint of surface having been removed.
            Array.Clear(used);
            foreach (var sub in keptPerSub)
                foreach (var i in sub) used[i] = true;

            // Compact each vertex stream down to the vertices the surviving triangles reference.
            int streamCount = outStreams.Length;
            var remap = new ushort[vc];
            ushort nv = 0;
            var comp = new MemoryStream[streamCount];
            for (int st = 0; st < streamCount; st++) comp[st] = new MemoryStream();
            for (int i = 0; i < vc; i++)
            {
                if (!used[i]) continue;
                remap[i] = nv++;
                for (int st = 0; st < streamCount; st++)
                    comp[st].Write(outStreams[st], i * outStrides[st], outStrides[st]);
            }
            vertOut += nv;

            var vOff = new uint[streamCount];
            for (int st = 0; st < streamCount; st++)
            {
                vOff[st] = (uint)vBuf.Position;
                vBuf.Write(comp[st].GetBuffer(), 0, (int)comp[st].Length);
            }

            uint meshStartIdx = idxCursor;
            ushort keptSubs = 0;
            var subsForMesh = new List<byte[]>();

            // A REBUILT bone table needs a bone map of its own. The source's map describes the source's
            // table, and once the table has been replaced its entries mean nothing â€” so publish the whole
            // new table as this mesh's window, which is what every unmodified mesh here already has.
            int rebuiltMapBase = -1;
            if (capBoneTable != null)
            {
                rebuiltMapBase = submeshBoneMap.Count;
                for (int i = 0; i < capBoneTable.Length; i++) submeshBoneMap.Add((ushort)i);
            }

            for (int su = 0; su < srcSubCount; su++)
            {
                var keep = keptPerSub[su];
                if (keep.Length == 0) continue;
                int ss = src.SubmeshStart + (srcSubIdx + su) * 16;
                uint subStart = idxCursor;
                var idxBytes = new byte[keep.Length * 2];
                for (int k = 0; k < keep.Length; k++)
                    BitConverter.TryWriteBytes(idxBytes.AsSpan(k * 2), remap[keep[k]]);
                iBuf.Write(idxBytes);
                idxCursor += (uint)keep.Length;

                var ns = new byte[16];
                W32(ns, 0, subStart);
                W32(ns, 4, (uint)keep.Length);
                W32(ns, 8, 0);                                            // attributes dropped
                // The submesh's BONE WINDOW. Normally the source's, rebased â€” but not when this mesh's
                // bone table was rebuilt underneath it. The cap ships weighted to four bones and comes
                // out weighted to twenty-four once it takes the body's skinning, and copying "4" through
                // left the game building a four-bone palette for vertices indexing up to 23. Every other
                // mesh in the shell has window == table size; the cap was the one that did not, and it is
                // invisible in a modelling package because that reads the mesh's table directly.
                if (capBoneTable != null)
                {
                    W16(ns, 12, (ushort)rebuiltMapBase);
                    W16(ns, 14, (ushort)capBoneTable.Length);
                }
                else
                {
                    W16(ns, 12, (ushort)(U16(ss + 12) + mapBase));        // boneStart, rebased
                    W16(ns, 14, U16(ss + 14));                            // boneCount, as authored
                }
                subsForMesh.Add(ns);
                keptSubs++;
            }

            // This mesh's OWN bone table, entries remapped onto the union list. Never merged with
            // other meshes' tables â€” ubyte4 vertex indices cap a table at 255 entries.
            var srcTable = srcBoneTbl < src.BoneTables.Length ? src.BoneTables[srcBoneTbl] : [];
            var table = capBoneTable ?? new ushort[srcTable.Length];
            if (capBoneTable == null)
                for (int i = 0; i < srcTable.Length; i++)
                {
                    var name = srcTable[i] < src.BoneNames.Length ? src.BoneNames[srcTable[i]] : null;
                    table[i] = name != null && boneIndex.TryGetValue(name, out var ui) ? ui : (ushort)0;
                }

            var nm = new byte[36];
            W16(nm, 0, nv);
            W32(nm, 4, idxCursor - meshStartIdx);
            W16(nm, 8, materialIndex);
            W16(nm, 10, subCursor);
            W16(nm, 12, keptSubs);
            W16(nm, 14, (ushort)boneTables.Count);  // this mesh's own table
            W32(nm, 16, meshStartIdx);
            W32(nm, 20, vOff[0]);
            W32(nm, 24, streamCount > 1 ? vOff[1] : 0);
            W32(nm, 28, streamCount > 2 ? vOff[2] : 0);
            nm[32] = outStrides[0];
            nm[33] = streamCount > 1 ? outStrides[1] : (byte)0;
            nm[34] = streamCount > 2 ? outStrides[2] : (byte)0;
            nm[35] = (byte)streamCount;

            meshOut.Add(nm);
            declOut.Add(declBlock);
            boneTables.Add(table);
            subOut.AddRange(subsForMesh);
            subCursor += keptSubs;
        }

        // Host pre-pass: the ring/bracelet's own LOD0 meshes, verbatim and unfiltered, at their authored
        // material indices (0..baseMatCount-1) â€” so the accessory still renders under the appended shell.
        if (baseSrc != null)
        {
            int mapBase = submeshBoneMap.Count;
            bool mapAppended = false;
            int bEnd = baseSrc.Lod0MeshIndex + baseSrc.Lod0MeshCount;
            for (int m = baseSrc.Lod0MeshIndex; m < bEnd && m < baseSrc.MeshCount; m++)
            {
                int bmo = baseSrc.MeshStart + m * 36;
                ushort srcMat = BitConverter.ToUInt16(baseSrc.S, bmo + 8);
                EmitMesh(baseSrc, m, srcMat, 0f, preserve: true, cov: null, mapBase, ref mapAppended,
                    dropConnectors: false);
            }
        }

        for (ushort layer = 0; layer < layers.Count; layer++)
        {
            var def = layers[layer];
            float push = BaseOffset + LayerSeparation * layer;
            ushort matIndex = (ushort)(baseMatCount + layer);

            // THE CAP TAKES THE FULL PUSH, like everything else in the layer.
            //
            // It was briefly emitted at push - BaseOffset, on the reasoning that the cap is modelled at
            // the shell's surface already: the Rue cap sits a median 1.12 mm off the skin and BaseOffset
            // is 1.00 mm, so pushing it again put it at 2.09 mm against the shell's 1.00 and left a ridge
            // along the join. But that 1.12 was a coincidence, not a rule â€” the Neolithe cap measures
            // 1.64 mm off its own skin. Each cap is authored at whatever height its author chose, so
            // subtracting a fixed offset moves every cap by a different amount relative to the rim it has
            // to meet, and it broke the weld on both bodies.
            //
            // The ridge is real and still wants fixing. The measurement to drive it is the cap's OWN
            // clearance over the body it is being placed on, not a constant.
            float capPush = push;

            // A declined cap takes its cut with it, so the toes keep the shell they already had.
            if (capDeclined != null && def.ToeCap != null)
                def = new SecondSkinLayer
                {
                    MaterialName = def.MaterialName,
                    Coverage = def.Coverage,
                    CoverageWidth = def.CoverageWidth,
                    CoverageHeight = def.CoverageHeight,
                    ToeCap = null,
                    ToeCapWidth = 0,
                    ToeCapHeight = 0,
                    ToeCapStrength = def.ToeCapStrength,
                };

            // The cap's rim, pushed to this layer's offset, ready for the shell meshes below to close
            // onto. It has to exist BEFORE they are emitted, which is why the projection runs here rather
            // than at the graft â€” it is cached, so asking early costs nothing.
            weldRim = null;
            weldRimVerts.Clear();
            weldRimPos.Clear();
            capAllVerts.Clear();
            welded = 0; weldWorst = 0; weldWorstD = 0f; capWelded = 0;
            capRimLandings.Clear();
            shellRim.Clear();
            // THE CAP IS TRIMMED AGAINST A SLIGHTLY WIDER COVERAGE THAN THE SHELL IS.
            //
            // Both use the same test â€” keep a triangle if ANY texel under its UV footprint is visible â€”
            // and that test dilates outward by the size of the triangle asking. The cap's faces are far
            // smaller than the shell's (sub-texel against several texels), so the same coverage curve
            // stops the cap EARLIER than it stops the shell. The cap's edge then lands part-way across
            // surviving shell fabric, with no shell boundary opposite to weld or split against: measured
            // on Rue's inner layer, 153 of 315 cap rim points had no shell boundary within reach and 659
            // lip vertices never welded, leaving 0.87 of cap edge lying on top of the shell. That reads as
            // a line on the fabric, which is the seam â€” not a gap.
            //
            // Widening the coverage for the CAP's trims only closes that difference at its source, so both
            // boundaries fall on the same curve and the weld/split pair has something to work with.
            // Handed to EmitMesh, which is declared above this loop and cannot see its locals.
            capPushNow = capPush;
            capGrafted = false;

            var capDef = def;
            if (def.Coverage != null && def.CoverageWidth > 0 && def.CoverageHeight > 0 && CapCoverDilate > 0)
                capDef = new SecondSkinLayer
                {
                    MaterialName = def.MaterialName,
                    Coverage = DilateMask(def.Coverage, def.CoverageWidth, def.CoverageHeight,
                                          CapCoverDilate, CoverageFloor),
                    CoverageWidth = def.CoverageWidth,
                    CoverageHeight = def.CoverageHeight,
                    ToeCap = def.ToeCap,
                    ToeCapWidth = def.ToeCapWidth,
                    ToeCapHeight = def.ToeCapHeight,
                    ToeCapStrength = def.ToeCapStrength,
                };

            byte[]? footprint = null;
            int footprintSize = def.ToeCapWidth > 0 && def.ToeCapWidth == def.ToeCapHeight
                ? def.ToeCapWidth : CapFootprintSize;
            if (capSrc is { } cw && def.ToeCap != null)
            {
                var segs = new List<RimSeg>();
                footprint = new byte[footprintSize * footprintSize];
                int cwEnd = cw.Lod0MeshIndex + cw.Lod0MeshCount;
                for (int m = cw.Lod0MeshIndex; m < cwEnd && m < cw.MeshCount; m++)
                {
                    if (BitConverter.ToUInt16(cw.S, cw.MeshStart + m * 36) == 0) continue;
                    if (!capUvCache.TryGetValue(m, out var pl))
                        capUvCache[m] = pl = ProjectCapUV(cw, m, sourceModels, diag,
                            capPlaced != null && capPlaced.TryGetValue(m, out var pw2) ? pw2 : null);
                    if (pl == null) continue;

                    // The rim of the cap AS THIS LAYER WILL EMIT IT â€” that is, after the layer's own
                    // coverage has trimmed it. It cannot be worked out once and shared: a layer covering
                    // a sliver of the foot keeps a sliver of the cap, and its rim is nothing like the
                    // full cap's. Measured before this: the outer layer emitted the whole cap against a
                    // 400-face shell and its boundary stood a median of 0.015 from anything to weld to,
                    // a free edge across the top of the foot â€” which is what shows in game, because the
                    // outermost layer is the one being looked at.
                    //
                    // Counted on PRE-SPLIT indices: a split vertex has one copy per UV chart, so counting
                    // on the split indices would read the seam as a boundary and run a phantom rim
                    // through the middle of the cap.
                    var edgeUse = new Dictionary<(int A, int B), int>();
                    for (int f = 0; f * 3 + 2 < pl.Corner.Length; f++)
                    {
                        int c0 = pl.Corner[f * 3], c1 = pl.Corner[f * 3 + 1], c2 = pl.Corner[f * 3 + 2];
                        if (capDef.Coverage != null && !AnyVisible(capDef, pl.Uv[c0], pl.Uv[c1], pl.Uv[c2]))
                            continue;
                        int s0 = pl.SourceOf[c0], s1 = pl.SourceOf[c1], s2 = pl.SourceOf[c2];
                        foreach (var (x, y) in new[] { (s0, s1), (s1, s2), (s2, s0) })
                        {
                            var e = (Math.Min(x, y), Math.Max(x, y));
                            edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1;
                        }
                    }
                    CapFootprintMask(pl, capDef, footprint, footprintSize);

                    // The one place a cap rim vertex's final position is worked out. See weldRimPos.
                    Vec3 CapFinal(int i)
                    {
                        var (p, n2) = (pl.SrcPos[i], pl.SrcNrm[i]);
                        return new Vec3(p.X + n2.X * capPush, p.Y + n2.Y * capPush, p.Z + n2.Z * capPush);
                    }

                    // How many emitted copies each authored cap vertex has. One means its UV is
                    // unambiguous and the shell can safely take it; two means it sits on the body's atlas
                    // seam, carries a coordinate per chart, and there is no single value to share.
                    var copies = new Dictionary<int, int>();
                    var firstCopy = new Dictionary<int, int>();
                    for (int oi = 0; oi < pl.SourceOf.Length; oi++)
                    {
                        int s = pl.SourceOf[oi];
                        copies[s] = copies.GetValueOrDefault(s) + 1;
                        if (!firstCopy.ContainsKey(s)) firstCopy[s] = oi;
                    }

                    foreach (var (e, n) in edgeUse)
                    {
                        if (n != 1) continue;
                        Vec3 pa = CapFinal(e.A), pb = CapFinal(e.B);
                        // The cap's UV at each end, when both are unambiguous. A rim vertex on the body's
                        // atlas seam has a coordinate per chart and no single one to share, so a segment
                        // touching one carries no UV and the lip there keeps its own.
                        bool uvOk = copies.GetValueOrDefault(e.A) == 1 && copies.GetValueOrDefault(e.B) == 1
                                 && firstCopy.TryGetValue(e.A, out int oa) && oa < pl.Uv.Length
                                 && firstCopy.TryGetValue(e.B, out int ob) && ob < pl.Uv.Length;
                        segs.Add(uvOk
                            ? new RimSeg(pa, pl.SrcNrm[e.A], pl.SrcW[e.A],
                                         pb, pl.SrcNrm[e.B], pl.SrcW[e.B],
                                         pl.Uv[firstCopy[e.A]], pl.Uv[firstCopy[e.B]], true)
                            : new RimSeg(pa, pl.SrcNrm[e.A], pl.SrcW[e.A],
                                         pb, pl.SrcNrm[e.B], pl.SrcW[e.B]));
                        weldRimVerts.Add(e.A); weldRimVerts.Add(e.B);
                        weldRimPos[(m, e.A)] = pa; weldRimPos[(m, e.B)] = pb;
                    }

                    for (int oi = 0; oi < pl.SourceOf.Length; oi++)
                    {
                        int sIdx = pl.SourceOf[oi];
                        if (capPlaced == null || !capPlaced.TryGetValue(m, out var pcap)) break;
                        if (sIdx < 0 || sIdx >= pcap.Pos.Length) continue;
                        capAllVerts.Add(new Vec3(pcap.Pos[sIdx].X + pcap.Nrm[sIdx].X * capPush,
                                                 pcap.Pos[sIdx].Y + pcap.Nrm[sIdx].Y * capPush,
                                                 pcap.Pos[sIdx].Z + pcap.Nrm[sIdx].Z * capPush));
                    }
                }
                if (segs.Count > 0) weldRim = segs.ToArray();
                diag?.Invoke($"authored cap: layer {layer} keeps a rim of {segs.Count} edge(s) after its "
                           + "own coverage trims the cap");
                // Only now is capDef final; the graft inside EmitMesh trims the cap with it.
                capDefNow = capDef;
            }

            // Where an authored cap fills the toe box, optionally pull the CUT in before it is applied.
            // See CapCutErode â€” normally zero, because the weld closes the join instead.
            //
            // THE PAINTED MAP CUTS, not the cap's own footprint. This has now been tried both ways twice
            // and measured on Rue, so it is settled:
            //
            //                        painted map      cap footprint
            //   join two-way max        0.0043           0.0396
            //   cap -> shell median     0.000019         0.027431
            //   slivers                 160              639
            //   aspect >10 / max        6 / 27.3         111 / 412.5
            //   winding / non-manifold  0 / 0            4 / 1
            //
            // The reason is that the footprint is where the cap IS, and the hole has to be where the cap
            // ENDS. Cutting to the footprint leaves the shell standing inside the cap's own boundary â€”
            // its rim ends up a median of 0.027 from the rim it is supposed to meet â€” because a shell
            // triangle only goes if the mask covers it, and the mask stops exactly where the cap's
            // surface stops. The map's wider cut deliberately overshoots and the weld pulls the lip back
            // onto the rim, which is the mechanism that closes the join.
            var cutDef = def;
            if (capSrc != null && def.ToeCap is { } paint && def.ToeCapWidth > 0 && def.ToeCapHeight > 0)
            {
                int mwp = def.ToeCapWidth, mhp = def.ToeCapHeight;
                var eroded = (byte[])paint.Clone();
                for (int step = 0; step < CapCutErode; step++)
                {
                    var next = (byte[])eroded.Clone();
                    for (int y = 0; y < mhp; y++)
                        for (int x = 0; x < mwp; x++)
                        {
                            if (eroded[y * mwp + x] < 128) continue;
                            bool edge = false;
                            for (int dy = -1; dy <= 1 && !edge; dy++)
                                for (int dx = -1; dx <= 1 && !edge; dx++)
                                {
                                    int nx = x + dx, ny = y + dy;
                                    if (nx < 0 || ny < 0 || nx >= mwp || ny >= mhp || eroded[ny * mwp + nx] < 128)
                                        edge = true;
                                }
                            if (edge) next[y * mwp + x] = 0;
                        }
                    eroded = next;
                }
                int lit = 0, painted = 0;
                foreach (byte px in eroded) if (px >= 128) lit++;
                if (footprint != null) foreach (byte px in footprint) if (px >= 128) painted++;

                // CUT TO THE CAP, not to the painted map â€” the map only says a cap is WANTED.
                //
                // The map is an authored asset sized for the cap it was drawn alongside; on another
                // body's cap it takes out far more shell than that cap fills â€” measured on Rue, 8156
                // texels cut against 2480 covered. Nothing snaps a difference like that shut: the weld
                // hauls boundary vertices as much as 0.0139 to reach the rim, the triangles behind them
                // collapse, WeldCollapse drops them, and the shell tears open well away from the join.
                // And a map that is simply wrong â€” all white, say â€” marks every UV island end to end, so
                // MaxCoreFraction skips every one of them, nothing is cut at all, and the cap is laid
                // over untouched sleeved toes.
                //
                // Cutting to the cap was tried twice before and reverted, both times against the BARE
                // footprint, which under-covers: the rasterised faces stop exactly where the cap's
                // surface stops, so the shell was left standing just INSIDE the cap's boundary with
                // nothing to weld to â€” its rim a median 0.027 from the rim it was supposed to meet. The
                // two pieces that fixes are both here now: CapFootprintMask fills what the cap ENCLOSES
                // rather than merely what it covers, and CapCutDilate supplies the overshoot the painted
                // map used to provide by being drawn generously.
                var cutMask = footprint != null
                    ? DilateMask(footprint, footprintSize, footprintSize, CapCutDilate)
                    : eroded;
                int cutSide = footprint != null ? footprintSize : mwp;
                int cutLit = 0;
                foreach (byte px in cutMask) if (px >= 128) cutLit++;

                if (MaskDump is { } dump)
                {
                    dump("painted", eroded, mwp);
                    if (footprint != null) dump("footprint", footprint, footprintSize);
                    dump("cut", cutMask, cutSide);
                }

                diag?.Invoke($"authored cap: cutting to the cap's own footprint â€” {painted} texels "
                           + $"covered, {cutLit} after dilating by {CapCutDilate} "
                           + $"(the painted map would have cut {lit})");

                cutDef = new SecondSkinLayer
                {
                    MaterialName = def.MaterialName,
                    Coverage = def.Coverage,
                    CoverageWidth = def.CoverageWidth,
                    CoverageHeight = def.CoverageHeight,
                    ToeCap = cutMask,
                    ToeCapWidth = cutSide,
                    ToeCapHeight = cutSide,
                    ToeCapStrength = def.ToeCapStrength,
                };
            }

            foreach (var src in parsed)
            {
                var s = src.S;
                ushort U16(int o) => BitConverter.ToUInt16(s, o);

                // Each (source, layer) pair contributes its own copy of the source's submesh bone map.
                int mapBase = submeshBoneMap.Count;
                bool mapAppended = false;

                // LOD0 meshes only â€” never the lower LODs (a full game model has all three; merging them
                // stacks overlapping low-poly copies that fling geometry across the scene).
                int mEnd = src.Lod0MeshIndex + src.Lod0MeshCount;
                for (int m = src.Lod0MeshIndex; m < mEnd && m < src.MeshCount; m++)
                {
                    int mo = src.MeshStart + m * 36;
                    if (U16(mo) == 0) continue;   // empty mesh

                    // Which meshes of this source belong in the shell â€” see SourceSpec.KeepMaterial. For a
                    // body that is SKIN ONLY: a body model also holds the smallclothes/undies mesh (gear
                    // UV), nails, piercings and pubes, and duplicating those to paint them with a body-UV
                    // overlay smears the art across the hips and hands. For a face or a tail it is the
                    // material the overlay named, which excludes eyes and lashes on the same reasoning.
                    //
                    // The range guard stays OUTSIDE the predicate: a mesh whose material index is out of
                    // range has no name to hand it.
                    ushort srcMat = U16(mo + 8);
                    if (srcMat >= src.MatNames.Count || !src.Keep(src.MatNames[srcMat]))
                        continue;

                    // cutDef, not def: the toe-cap cut is applied through the layer's coverage argument.
                    // DropConnectors is per-source now, off the SourceSpec.
                    EmitMesh(src, m, matIndex, push, preserve: false, cov: cutDef, mapBase, ref mapAppended,
                        dropConnectors: src.DropConnectors);
                }
            }

            if (weldRim != null)
                diag?.Invoke($"authored cap: welded {welded} cut-lip vertices onto the rim "
                           + $"(furthest moved {weldWorstD:F4}), {weldWorst} left beyond {WeldRadius:F3}");

            // Graft the authored cap on for any layer that asked for one, wearing that layer's material.
            // Emitted verbatim apart from the layer push, the weld back onto the shell's rim, and the
            // projected UV â€” it is already modelled where it belongs on the foot it was authored against.
            // Fitting it to a DIFFERENT foot comes later and is a separate step.
            // Skipped when the cap went INTO a shell mesh, which is the normal path now: emitting it again
            // beside the shell would draw it twice and put back the very boundary the graft removed.
            if (capSrc is { } cs && def.ToeCap != null && !capGrafted)
            {
                // The cap is authored WITHOUT UVs â€” every vertex arrives at (0,1), so without this it
                // samples one corner texel of the overlay, which is transparent, and the whole cap is
                // invisible in game while looking perfectly correct in a modelling package.
                //
                // Give it the body's UV by dropping each vertex onto the skin underneath and taking the
                // coordinate where it lands. That is what makes the stocking's texture AND its alpha
                // continue across the cap, carried over the gaps between toes from the flanks either
                // side. Nothing here needs the author to unwrap anything: fresh UV space would have no
                // art in it at all, since the overlays are painted in the body's layout.

                int capMapBase = submeshBoneMap.Count;
                bool capMapAppended = false;
                int cEnd = cs.Lod0MeshIndex + cs.Lod0MeshCount;
                int emitted = 0;
                for (int m = cs.Lod0MeshIndex; m < cEnd && m < cs.MeshCount; m++)
                {
                    if (BitConverter.ToUInt16(cs.S, cs.MeshStart + m * 36) == 0) continue;   // empty mesh
                    EmitMesh(cs, m, matIndex, capPush, preserve: true, cov: capDef, capMapBase,
                             ref capMapAppended, dropConnectors: false,
                             capUv: capUvCache.TryGetValue(m, out var cached) ? cached
                                  : capUvCache[m] = ProjectCapUV(cs, m, sourceModels, diag,
                                        capPlaced != null && capPlaced.TryGetValue(m, out var pc2) ? pc2 : null));
                    emitted++;
                }
                diag?.Invoke($"authored toe cap: grafted {emitted} mesh(es) onto layer {layer}, "
                           + $"{capWelded} rim vertices welded back onto {shellRim.Count} shell segment(s)");
            }
        }

        if (meshOut.Count == 0) throw new InvalidOperationException("no geometry survived coverage trimming");

        int meshCount = meshOut.Count;
        int boneCount = boneNames.Count;

        // â”€â”€ string block: bone names (union) + material names. Attributes are dropped. â”€â”€
        var strMs = new MemoryStream();
        var boneStrOff = new List<uint>();
        foreach (var b in boneNames)
        {
            boneStrOff.Add((uint)strMs.Position);
            strMs.Write(Encoding.ASCII.GetBytes(b));
            strMs.WriteByte(0);
        }
        var matStrOff = new List<uint>();
        // Host materials FIRST (indices 0..baseMatCount-1, referenced verbatim by the host's own meshes),
        // then the appended shell layer materials.
        if (baseSrc != null)
            foreach (var name in baseSrc.MatNames)
            {
                matStrOff.Add((uint)strMs.Position);
                strMs.Write(Encoding.ASCII.GetBytes(name));
                strMs.WriteByte(0);
            }
        foreach (var l in layers)
        {
            matStrOff.Add((uint)strMs.Position);
            strMs.Write(Encoding.ASCII.GetBytes(l.MaterialName));
            strMs.WriteByte(0);
        }
        while (strMs.Position % 4 != 0) strMs.WriteByte(0);
        byte[] strings = strMs.ToArray();

        // Flags, the 0x44 file header and the LOD block still come from source 0. That is a real choice, not
        // an accident: source 0 is the surface this shell was cut from, and its flags are the ones that
        // describe the geometry we are actually emitting. (The host's would describe a ring.)
        var head = parsed[0];

        // The CULLING quantities are different â€” they are about extent, and the merged model's extent is the
        // union of everything in it, exactly as UnionModelBBoxes already treats the bounding boxes. Taking
        // source 0's alone understates them the moment the sources differ in size, and understating a radius
        // or a clip distance means the game culls the shell while the body it copies is still on screen â€”
        // the shell blinking out at an angle or a distance, with nothing in the log. Max is the only safe
        // direction here: too large costs a little overdraw, too small loses the shell.
        float radius = head.Radius, modelClip = head.ModelClip, shadowClip = head.ShadowClip;
        foreach (var src in (baseSrc != null ? new[] { baseSrc }.Concat(parsed) : parsed))
        {
            if (src.Radius     > radius)     radius     = src.Radius;
            if (src.ModelClip  > modelClip)  modelClip  = src.ModelClip;
            if (src.ShadowClip > shadowClip) shadowClip = src.ShadowClip;
        }

        uint stackSize = (uint)(meshCount * DeclSize);

        var ms = new MemoryStream();
        ms.Write(head.S, 0, 0x44);                                  // ModelFileHeader (patched below)
        for (int i = 0; i < meshCount; i++) ms.Write(declOut[i]);   // each mesh's own (source) declaration
        ms.Write(new byte[4]);                                      // string count (unused)
        Span<byte> tmp4 = stackalloc byte[4];
        BitConverter.TryWriteBytes(tmp4, (uint)strings.Length);
        ms.Write(tmp4);
        ms.Write(strings);

        long mhPos = ms.Position;
        var mh = new byte[56];
        BitConverter.GetBytes(radius).CopyTo(mh, 0);
        W16(mh, 4, (ushort)meshCount);
        W16(mh, 6, 0);                                              // attributes dropped
        W16(mh, 8, (ushort)subOut.Count);
        W16(mh, 10, (ushort)(baseMatCount + layers.Count));
        W16(mh, 12, (ushort)boneCount);
        W16(mh, 14, (ushort)boneTables.Count);
        W16(mh, 16, 0); W16(mh, 18, 0); W16(mh, 20, 0);             // shapes dropped
        mh[22] = 1;                                                 // lodCount
        mh[23] = head.Flags1;
        W16(mh, 24, 0);                                             // elementIdCount
        mh[26] = 0;                                                 // terrain shadow meshes
        mh[27] = (byte)(head.Flags2 & ~0x10);                       // no extra LODs
        BitConverter.GetBytes(modelClip).CopyTo(mh, 28);
        BitConverter.GetBytes(shadowClip).CopyTo(mh, 32);
        int boneTableShorts = boneTables.Sum(t => (t.Length + 1) & ~1);
        W16(mh, 44, (ushort)boneTableShorts);                       // BoneTableArrayCountTotal
        ms.Write(mh);

        long lodPos = ms.Position;
        ms.Write(head.Lods, 0, 3 * 60);                             // patched below

        foreach (var nm in meshOut) ms.Write(nm);
        foreach (var ns in subOut) ms.Write(ns);
        foreach (var off in matStrOff) { BitConverter.TryWriteBytes(tmp4, off); ms.Write(tmp4); }
        foreach (var off in boneStrOff) { BitConverter.TryWriteBytes(tmp4, off); ms.Write(tmp4); }

        WriteBoneTablesV6(ms, boneTables);

        // submesh bone map
        BitConverter.TryWriteBytes(tmp4, (uint)(submeshBoneMap.Count * 2));
        ms.Write(tmp4);
        var mapBytes = new byte[submeshBoneMap.Count * 2];
        for (int i = 0; i < submeshBoneMap.Count; i++)
            BitConverter.TryWriteBytes(mapBytes.AsSpan(i * 2), submeshBoneMap[i]);
        ms.Write(mapBytes);

        ms.WriteByte(0);                                            // padding amount

        // Bounding boxes: 4 model-level boxes then one per union bone. The model box must cover EVERY
        // part, or the merged model gets culled whenever only one part is on screen.
        ms.Write(UnionModelBBoxes(baseSrc != null ? [baseSrc, .. parsed] : parsed));
        foreach (var bb in boneBBox) ms.Write(bb);

        long vtxOffOut = ms.Position;
        vBuf.Position = 0; vBuf.CopyTo(ms);
        long idxOffOut = ms.Position;
        iBuf.Position = 0; iBuf.CopyTo(ms);
        byte[] o = ms.ToArray();

        uint vtxSize = (uint)vBuf.Length, idxSize = (uint)iBuf.Length;
        W32(o, 4, stackSize);
        W32(o, 8, (uint)(vtxOffOut - 0x44 - stackSize));            // RuntimeSize
        W16(o, 12, (ushort)meshCount);                              // vertDeclCount == meshCount
        W16(o, 14, (ushort)(baseMatCount + layers.Count));
        W32(o, 16, (uint)vtxOffOut); W32(o, 20, 0); W32(o, 24, 0);
        W32(o, 28, (uint)idxOffOut); W32(o, 32, 0); W32(o, 36, 0);
        W32(o, 40, vtxSize); W32(o, 44, 0); W32(o, 48, 0);
        W32(o, 52, idxSize); W32(o, 56, 0); W32(o, 60, 0);
        o[64] = 1;                                                  // lodCount

        int ol = (int)lodPos;
        W16(o, ol + 0, 0);                                          // mesh index
        W16(o, ol + 2, (ushort)meshCount);
        W32(o, ol + 44, vtxSize);
        W32(o, ol + 48, idxSize);
        W32(o, ol + 52, (uint)vtxOffOut);
        W32(o, ol + 56, (uint)idxOffOut);
        for (int l = 1; l < 3; l++)                                 // LOD 1/2 carry no meshes
        {
            int p = ol + l * 60;
            W16(o, p + 0, (ushort)meshCount);
            W16(o, p + 2, 0);
        }
        _ = mhPos;

        // Every submesh bone map entry must name a bone that exists in the union list. This is the invariant
        // the by-name remap above is responsible for, and the one whose failure would be ours.
        //
        // Deliberately NOT also checking that each submesh's [boneStart, boneStart+boneCount) window fits
        // inside the map. That check was written, run, and thrown away on the evidence: real body models
        // fail it as authored. A Neolithe e0000 top declares one mesh of five submeshes with boneStart
        // 0/23/46/69/92 and boneCount 23 â€” windows reaching 115 â€” against a submesh bone map of 35 entries.
        // Those numbers are the SOURCE's own, carried through unchanged, and shells built from them have
        // been rendering in game for hundreds of builds. So the game does not read that field the way the
        // struct layout suggests, and flagging it would fire on every composite while describing nothing.
        //
        // Worth knowing rather than just worth silencing: it means the submesh bone map is largely inert for
        // these models, so the remap above is defence, not load-bearing machinery. If a merged-skeleton
        // shell ever does misbehave, this is evidence that the bone TABLES (which are honoured) are where
        // to look first.
        {
            int badEntry = submeshBoneMap.Count(v => v >= boneNames.Count);
            if (badEntry > 0)
                diag?.Invoke($"BONE MAP: {badEntry} entry(ies) name a bone past the {boneNames.Count}-bone "
                           + "union list â€” the by-name remap failed to place them");
        }

        if (shapedTotal > 0) diag?.Invoke($"shape bake: {shapedTotal} index entries rewired to morphed vertices");
        // Per LAYER, not per vertex: every layer rebuilds the same sources, so these count each source's
        // vertices once for each of them. Divided back out so the number means what it says.
        if (uvMoved > 0)
            diag?.Invoke($"uv conversion: {uvMoved / layers.Count} vertices moved into the shell's UV space"
                       + (uvUnmapped > 0 ? $", {uvUnmapped / layers.Count} left as authored (no correspondence)" : "")
                       + $", {uvRetangented / layers.Count} mesh(es) re-tangented");

        stats = new Stats(meshCount, subOut.Count, boneCount, triIn, triOut, vertOut, capDeclined, capUsed);
        return o;
    }

    /// <summary>
    /// v6 bone tables: a header per table ({u16 offset, u16 size}) followed by the index data. The offset
    /// is in DWORDS and relative to that table's OWN header â€” not to the section start.
    /// </summary>
    private static void WriteBoneTablesV6(MemoryStream ms, List<ushort[]> tables)
    {
        long start = ms.Position;
        int headerBytes = tables.Count * 4;
        long dataPos = start + headerBytes;

        // Hoisted out of the loop: a stackalloc inside one accumulates a frame per iteration and never
        // releases until the method returns, so a long table list could run the stack down. Reused
        // rather than re-allocated â€” every write below fills both bytes before reading it.
        Span<byte> t = stackalloc byte[2];
        for (int i = 0; i < tables.Count; i++)
        {
            long headerPos = start + i * 4;
            ms.Position = headerPos;

            BitConverter.TryWriteBytes(t, (ushort)((dataPos - headerPos) / 4));
            ms.Write(t);
            BitConverter.TryWriteBytes(t, (ushort)tables[i].Length);
            ms.Write(t);

            ms.Position = dataPos;
            foreach (var b in tables[i])
            {
                BitConverter.TryWriteBytes(t, b);
                ms.Write(t);
            }
            if ((tables[i].Length & 1) == 1) { BitConverter.TryWriteBytes(t, (ushort)0); ms.Write(t); }
            dataPos = ms.Position;
        }
        ms.Position = dataPos;
    }

    /// <summary>The 4 model-level bounding boxes, unioned across every part.</summary>
    private static byte[] UnionModelBBoxes(List<Source> parsed)
    {
        var outBB = new byte[4 * BBoxSize];
        for (int box = 0; box < 4; box++)
        {
            var min = new float[4] { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new float[4] { float.MinValue, float.MinValue, float.MinValue, float.MinValue };
            bool any = false;
            foreach (var src in parsed)
            {
                if (src.ModelBBoxes.Length < (box + 1) * BBoxSize) continue;
                any = true;
                for (int c = 0; c < 4; c++)
                {
                    min[c] = MathF.Min(min[c], BitConverter.ToSingle(src.ModelBBoxes, box * BBoxSize + c * 4));
                    max[c] = MathF.Max(max[c], BitConverter.ToSingle(src.ModelBBoxes, box * BBoxSize + 16 + c * 4));
                }
            }
            if (!any) continue;
            for (int c = 0; c < 4; c++)
            {
                BitConverter.GetBytes(min[c]).CopyTo(outBB, box * BBoxSize + c * 4);
                BitConverter.GetBytes(max[c]).CopyTo(outBB, box * BBoxSize + 16 + c * 4);
            }
        }
        return outBB;
    }

    private static Source Parse(byte[] s)
    {
        uint U32(int o) => BitConverter.ToUInt32(s, o);
        ushort U16(int o) => BitConverter.ToUInt16(s, o);

        ushort declCount = U16(12);
        uint vtxOff = U32(16), idxOff = U32(28);
        int declEnd = 0x44 + declCount * DeclSize;

        // Vertex declarations: declCount blocks of up to 17 elements (8 bytes each), one block per mesh,
        // terminated by a Stream == 0xFF sentinel. { Stream, Offset, Type, Usage, UsageIndex, 3Ã— pad }.
        var decls = new VElem[declCount][];
        for (int d = 0; d < declCount; d++)
        {
            int db = 0x44 + d * DeclSize;
            var elems = new List<VElem>(17);
            for (int e = 0; e < 17; e++)
            {
                int o = db + e * 8;
                if (s[o] == 0xFF) break;
                elems.Add(new VElem(s[o], s[o + 1], s[o + 2], s[o + 3], s[o + 4]));
            }
            decls[d] = elems.ToArray();
        }
        uint strSize = U32(declEnd + 4);
        int strBlock = declEnd + 8;
        int mh = strBlock + (int)strSize;

        ushort meshCount = U16(mh + 4), attrCount = U16(mh + 6), submeshCount = U16(mh + 8), matCount = U16(mh + 10);
        ushort boneCount = U16(mh + 12), boneTableCount = U16(mh + 14);
        ushort shapeCount = U16(mh + 16), shapeMeshCount = U16(mh + 18), shapeValueCount = U16(mh + 20);
        byte flags1 = s[mh + 23], flags2 = s[mh + 27];
        ushort elemCount = U16(mh + 24);
        byte tsMesh = s[mh + 26];
        ushort tsSubmesh = U16(mh + 38);

        int lodStart = mh + 56 + elemCount * 32;
        // LOD0's mesh range. A full game model carries 3 LODs; a mod .mdl is usually LOD0-only. We only
        // ever want LOD0 â€” merging the lower LODs would stack overlapping low-poly copies (polys flying
        // everywhere). LOD struct: { u16 MeshIndex, u16 MeshCount, â€¦ } at lodStart.
        ushort lod0MeshIndex = U16(lodStart), lod0MeshCount = U16(lodStart + 2);
        int meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);
        int attrStart = meshStart + meshCount * 36;
        int submeshStart = attrStart + attrCount * 4 + tsMesh * 20;
        int matOffStart = submeshStart + submeshCount * 16 + tsSubmesh * 12;
        int boneOffStart = matOffStart + matCount * 4;
        int p = boneOffStart + boneCount * 4;

        string Str(uint rel)
        {
            int o = strBlock + (int)rel, e = o;
            while (s[e] != 0) e++;
            return Encoding.ASCII.GetString(s, o, e - o);
        }

        var boneNames = new string[boneCount];
        for (int i = 0; i < boneCount; i++) boneNames[i] = Str(U32(boneOffStart + i * 4));

        // v6 bone tables â€” offset is in dwords, relative to each table's own header.
        var tables = new ushort[boneTableCount][];
        for (int i = 0; i < boneTableCount; i++)
        {
            int headerPos = p + i * 4;
            ushort off = U16(headerPos), size = U16(headerPos + 2);
            int data = headerPos + off * 4;
            var t = new ushort[size];
            for (int k = 0; k < size; k++) t[k] = U16(data + k * 2);
            tables[i] = t;
        }
        p += boneTableCount * 4 + U16(mh + 44) * 2;                 // headers + BoneTableArrayCountTotal

        // â”€â”€ Shape (morph) block â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Layout: Shape[shapeCount] (16 B) then ShapeMesh[shapeMeshCount] (12 B) then ShapeValue[..] (4 B).
        //   Shape:     u32 nameOffset; u16 shapeMeshStart[3]; u16 shapeMeshCount[3]   (LOD0 = index 0)
        //   ShapeMesh: u32 meshIndexOffset; u32 valueCount; u32 valueStart
        //   ShapeValue:u16 baseIndicesIndex; u16 replacingVertexIndex
        // Parse LOD0 only (the shell keeps only LOD0). Bounds-guarded: a malformed block leaves Shapes empty
        // and the shell builds exactly as before.
        var shapes = new Dictionary<string, List<ShapeMeshEntry>>(StringComparer.Ordinal);
        int shapeBlock = p, shapeMeshBlock = p + shapeCount * 16, shapeValBlock = p + shapeCount * 16 + shapeMeshCount * 12;
        if (shapeValBlock + shapeValueCount * 4 <= s.Length)
        {
            for (int si = 0; si < shapeCount; si++)
            {
                int shp = shapeBlock + si * 16;
                string sname = Str(U32(shp));
                ushort smStart = U16(shp + 4), smCount = U16(shp + 10);   // LOD0
                var entries = new List<ShapeMeshEntry>(smCount);
                for (int mi = 0; mi < smCount; mi++)
                {
                    int sm = shapeMeshBlock + (smStart + mi) * 12;
                    if (sm + 12 > s.Length) break;
                    uint meshIdxOff = U32(sm), vCount = U32(sm + 4), vStart = U32(sm + 8);
                    if (shapeValBlock + (long)(vStart + vCount) * 4 > s.Length) continue;
                    var vals = new (ushort, ushort)[vCount];
                    for (int vi = 0; vi < vCount; vi++)
                    {
                        int sv = shapeValBlock + (int)(vStart + vi) * 4;
                        vals[vi] = (U16(sv), U16(sv + 2));
                    }
                    entries.Add(new ShapeMeshEntry(meshIdxOff, vals));
                }
                if (entries.Count > 0) shapes[sname] = entries;
            }
        }

        p += shapeCount * 16 + shapeMeshCount * 12 + shapeValueCount * 4;

        uint mapBytes = U32(p); p += 4;
        var map = new ushort[mapBytes / 2];
        for (int i = 0; i < map.Length; i++) map[i] = U16(p + i * 2);
        p += (int)mapBytes;

        byte padding = s[p]; p += 1 + padding;

        var modelBB = new byte[4 * BBoxSize];
        Array.Copy(s, p, modelBB, 0, Math.Min(modelBB.Length, s.Length - p));
        p += 4 * BBoxSize;

        var boneBB = new byte[boneCount * BBoxSize];
        Array.Copy(s, p, boneBB, 0, Math.Min(boneBB.Length, s.Length - p));

        var lods = new byte[3 * 60];
        Array.Copy(s, lodStart, lods, 0, lods.Length);

        var matNames = new List<string>();
        for (int i = 0; i < matCount; i++)
        {
            int o = strBlock + (int)U32(matOffStart + i * 4), e = o;
            while (s[e] != 0) e++;
            matNames.Add(Encoding.ASCII.GetString(s, o, e - o));
        }

        return new Source
        {
            MatNames = matNames,
            S = s,
            Mh = mh,
            MeshStart = meshStart,
            SubmeshStart = submeshStart,
            Vb = (int)vtxOff,
            Ib = (int)idxOff,
            StrBlock = strBlock,
            MatOffStart = matOffStart,
            MatCount = matCount,
            Decls = decls,
            Lod0MeshIndex = lod0MeshIndex,
            Lod0MeshCount = lod0MeshCount,
            MeshCount = meshCount,
            SubmeshCount = submeshCount,
            BoneCount = boneCount,
            BoneNames = boneNames,
            BoneTables = tables,
            SubmeshBoneMap = map,
            BoneBBoxes = boneBB,
            ModelBBoxes = modelBB,
            Radius = BitConverter.ToSingle(s, mh),
            ModelClip = BitConverter.ToSingle(s, mh + 28),
            ShadowClip = BitConverter.ToSingle(s, mh + 32),
            Flags1 = flags1,
            Flags2 = flags2,
            Lods = lods,
            Shapes = shapes,
        };
    }

    /// <summary>
    /// Body vertex format -> gear vertex format, pushing each vertex out along its normal, and decoding
    /// each vertex's UV (returned in <paramref name="uvs"/> for the coverage test).
    ///
    /// Attributes are located via the mesh's own vertex <paramref name="decl"/>, NOT a fixed layout:
    /// modded bodies store position/normal/uv as float and blend at offsets 12/16, but vanilla models
    /// use half-precision and different offsets, so a fixed reader would skin the wrong bytes as garbage.
    /// The body carries up to 8 bone influences and gear holds 4, but almost every body vertex uses â‰¤4
    /// and the rest discard a fraction of a percent of their weight â€” measured, harmless.
    /// </summary>
    /// <summary>
    /// Copy each vertex VERBATIM into the shell, preserving the source model's own vertex format â€” blend
    /// weights, bone indices, UVs and tangents are never decoded or reinterpreted, so any body (vanilla,
    /// bibo, Neolithe, â€¦) skins exactly as authored and the byte-format zoo stops mattering. Only what
    /// the shell genuinely needs is touched: position is pushed out along its normal (z-fight clearance),
    /// vertex colour is forced white (the gear shaders gate emissive on it), and a second UV set is
    /// appended when the source lacks one (characterscroll samples its scroll map with uv1). Output
    /// stream strides equal the source's (the uv1 stream grown by the copy). Also returns this mesh's
    /// declaration block (source decl, plus the uv1 element) and decoded uv0 for the coverage test.
    /// </summary>
    /// <summary>
    /// Copy a host (ring/bracelet) mesh's vertex streams and declaration byte-for-byte, with NONE of the
    /// shell tricks â€” no push, no colour-whiten, no uv1 mirroring, no UV normalization. The accessory must
    /// render exactly as authored, so its format passes through untouched.
    /// </summary>
    private static void CopyVerbatim(
        byte[] s, int vb, int srcDeclOff, ushort vc, VElem[] decl, uint[] vbo, byte[] bs,
        out byte[][] outStreams, out byte[] outStrides, out byte[] declBlock)
    {
        // Match BuildVerbatim's stream count: every stream carrying data OR named by a decl element.
        int streamCount = bs[2] > 0 ? 3 : (bs[1] > 0 ? 2 : 1);
        foreach (var el in decl) streamCount = Math.Max(streamCount, Math.Min((int)el.Stream, 2) + 1);

        outStrides = new byte[streamCount];
        for (int st = 0; st < streamCount; st++) outStrides[st] = bs[st];
        outStreams = new byte[streamCount][];
        for (int st = 0; st < streamCount; st++)
        {
            outStreams[st] = new byte[vc * bs[st]];
            for (int i = 0; i < vc; i++)
                Array.Copy(s, vb + (int)vbo[st] + i * bs[st], outStreams[st], i * bs[st], bs[st]);
        }

        declBlock = new byte[DeclSize];
        Array.Copy(s, srcDeclOff, declBlock, 0, DeclSize);
    }

    /// <summary>Returns the number of vertices <paramref name="uvConv"/> had no correspondence for (0 when
    /// there is no conversion). Those keep their original UV â€” see the normalization block.
    /// <paramref name="uvsPreConv"/> holds the UVs as they were BEFORE the conversion (null when there was
    /// none): <see cref="RetangentMesh"/> needs both layouts to re-fit the tangent frame.</summary>
    private static int BuildVerbatim(
        byte[] s, int vb, int srcDeclOff, ushort vc, VElem[] decl, uint[] vbo, byte[] bs, float push,
        out byte[][] outStreams, out byte[] outStrides, out byte[] declBlock, out (float U, float V)[] uvs,
        out (float U, float V)[]? uvsPreConv,
        Func<float, float, (float U, float V)?>? uvConv,
        out Vec3[]? capSrcPos, out Vec3[]? capOutPos, out ToeCapPlan? capPlan,
        SecondSkinLayer? cap = null, ushort[]? capTris = null, Action<string>? capLog = null,
        bool buildCapGeometry = true)
    {
        int uvUnmapped = 0;
        uvsPreConv = null;
        capPlan = null;
        VElem? pos = null, norm = null, uv0 = null, uv1El = null, col = null;
        foreach (var el in decl)
            switch (el.Usage)
            {
                case UsePosition: pos ??= el; break;
                case UseNormal:   norm ??= el; break;
                case UseColor:    col ??= el; break;
                case UseUV:       if (el.UsageIndex == 0) uv0 ??= el; else uv1El ??= el; break;
            }

        // Emit every stream that carries data OR is named by a declaration element, so the per-attribute
        // writes below can never index past the arrays (a mesh with only stream 0, or a decl that names a
        // stream the stride table didn't flag, would otherwise crash).
        int streamCount = bs[2] > 0 ? 3 : (bs[1] > 0 ? 2 : 1);
        foreach (var el in decl) streamCount = Math.Max(streamCount, Math.Min((int)el.Stream, 2) + 1);

        // The scroll shader reads its texcoord from uv1; a body has one real UV, so uv1 must MIRROR uv0.
        // The model's own uv1 slot holds an unrelated aux coord (a Float4/Half4 uv0 packs it in .zw; some
        // models add a separate uidx1 element) â€” junk for scrolling, so we overwrite every uv1 slot with
        // uv0. Only when uv0 is a bare 2-component element with no uidx1 do we append a Float2 uv1 â€” into
        // uv0's OWN stream (guaranteed present), not a hard-coded stream 1.
        bool zwValid = uv0 is { } uz && (uz.Type == 3 || uz.Type == 14);
        int  zwOff   = uv0 is { } uo ? uo.Offset + (uo.Type == 3 ? 8 : 4) : 0;
        bool zwHalf  = uv0 is { } uh && uh.Type == 14;
        bool appendUv1 = uv0 is not null && !zwValid && uv1El is null;
        int  uv1Stream = uv0 is { } us ? us.Stream : 1;
        int  uv1Bytes  = appendUv1 ? 8 : 0;                  // appended as Float2

        outStrides = new byte[streamCount];
        for (int st = 0; st < streamCount; st++) outStrides[st] = bs[st];
        if (appendUv1) outStrides[uv1Stream] = (byte)(bs[uv1Stream] + uv1Bytes);
        outStreams = new byte[streamCount][];
        for (int st = 0; st < streamCount; st++) outStreams[st] = new byte[vc * outStrides[st]];

        uvs = new (float, float)[vc];
        Span<float> tmp = stackalloc float[4];
        int SrcAddr(int st, int i, int off) => vb + (int)vbo[st] + i * bs[st] + off;

        // Positions and normalized normals are decoded here but written AFTER the UV pass, because the
        // toe cap displaces them and it samples its mask with the normalized UV.
        Vec3[]? basePos = null, baseNrm = null;
        if (pos is not null && norm is not null) { basePos = new Vec3[vc]; baseNrm = new Vec3[vc]; }

        for (int i = 0; i < vc; i++)
        {
            for (int st = 0; st < streamCount; st++)
                Array.Copy(s, vb + (int)vbo[st] + i * bs[st], outStreams[st], i * outStrides[st], bs[st]);

            if (basePos is not null && baseNrm is not null && pos is { } pe && norm is { } ne)
            {
                ReadTyped(s, SrcAddr(ne.Stream, i, ne.Offset), ne.Type, tmp);
                float nx = tmp[0], ny = tmp[1], nz = tmp[2];
                if (ne.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; }
                baseNrm[i] = new Vec3(nx, ny, nz);
                ReadTyped(s, SrcAddr(pe.Stream, i, pe.Offset), pe.Type, tmp);
                basePos[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
            }

            // Force vertex colour white so the gear shader's emissive isn't gated off.
            if (col is { } ce)
            {
                int o = i * outStrides[ce.Stream] + ce.Offset;
                outStreams[ce.Stream][o] = outStreams[ce.Stream][o + 1]
                    = outStreams[ce.Stream][o + 2] = outStreams[ce.Stream][o + 3] = 0xFF;
            }

            // Decode uv0 (raw) â€” normalized and written below, once the mesh's UV cell is known.
            if (uv0 is { } ue) { ReadTyped(s, SrcAddr(ue.Stream, i, ue.Offset), ue.Type, tmp); uvs[i] = (tmp[0], tmp[1]); }
        }

        // Normalize the mesh's UV into the [0,1] tile and force uv1 = uv0. The overlay is a single [0,1]
        // image, but a body UV can live in another cell (vanilla Uâˆˆ[1,2], bibo Vâˆˆ[-1,0]); shift the WHOLE
        // mesh by the integer floor of its minimum UV. A per-mesh (not per-vertex) shift keeps islands
        // together so nothing tears, and brings an island that sits WITHIN one integer cell fully onto the
        // tile â€” a body part is laid out that way. (An island straddling a cell boundary would keep the
        // overflow past 1; no body mesh does that, so it's left to the sampler's wrap.) Then write uv0 and
        // every uv1 slot (.zw / uidx1 / appended) with the shifted value.
        if (uv0 is { } u0e)
        {
            float minU = float.MaxValue, minV = float.MaxValue;
            for (int i = 0; i < vc; i++) { minU = MathF.Min(minU, uvs[i].U); minV = MathF.Min(minV, uvs[i].V); }
            float uOff = MathF.Floor(minU), vOff = MathF.Floor(minV);
            bool uv0Half = u0e.Type is 13 or 14;
            if (uvConv != null) uvsPreConv = new (float, float)[vc];
            for (int i = 0; i < vc; i++)
            {
                float u = uvs[i].U - uOff, v = uvs[i].V - vOff;
                // Then, for a part whose UVs are in another body's space, move each vertex to where the
                // same point on the body sits in the SHELL's space. Done after the tile shift because the
                // transfer maps are indexed over [0,1]; the result is already on the shell's tile, so no
                // second normalization follows. A vertex the maps can't place keeps its original UV â€”
                // pulling it to some far-off "nearest" would drag its triangles across the texture.
                if (uvConv != null)
                {
                    uvsPreConv![i] = (u, v);
                    var moved = uvConv(u, v);
                    if (moved is { } mv) { u = mv.U; v = mv.V; }
                    else uvUnmapped++;
                }
                uvs[i] = (u, v);
                int so = i * outStrides[u0e.Stream];
                WriteUV2(outStreams[u0e.Stream], so + u0e.Offset, uv0Half, u, v);   // uv0.xy (normalized)
                if (zwValid)         WriteUV2(outStreams[u0e.Stream], so + zwOff, zwHalf, u, v);
                if (uv1El is { } e1) WriteUV2(outStreams[e1.Stream], i * outStrides[e1.Stream] + e1.Offset, e1.Type is 13 or 14, u, v);
                if (appendUv1)       WriteUV2(outStreams[uv1Stream], i * outStrides[uv1Stream] + bs[uv1Stream], false, u, v);
            }
        }

        // Position write-back: base + (optional) toe-cap displacement, then the push along the vertex's
        // normal, re-encoded in the position's own type. The cap needs the mesh's topology and a UV, so
        // it can only run here; with no cap this is byte-for-byte what the in-loop push produced.
        capSrcPos = null;
        capOutPos = null;
        if (basePos is not null && baseNrm is not null && pos is { } pw)
        {
            var plan = uv0 is not null && cap is { ToeCap: { } tc } && capTris is not null
                ? ToeCapSolve(basePos, baseNrm, uvs, capTris, tc, cap.ToeCapWidth, cap.ToeCapHeight,
                              cap.ToeCapStrength, capLog, buildCapGeometry)
                : null;
            var delta = plan?.Delta;
            capPlan = plan;

            // Normals recomputed from the REBUILT surface â€” the source triangles minus the ones the cut
            // removed, plus the cap's own. Without this the shell keeps shading as the toes it replaced.
            var finalNrm = plan is null ? baseNrm : CapNormals(basePos, baseNrm, plan, CappedTopology(plan, capTris!));

            int stride = outStrides[pw.Stream];
            int normalsWritten = 0, uvsWritten = 0;
            bool encoderMissing = false;
            var outPos = plan is null ? null : new Vec3[vc];

            for (int i = 0; i < vc; i++)
            {
                var p = basePos[i];
                var n = finalNrm[i];
                if (delta is not null) p = new Vec3(p.X + delta[i].X, p.Y + delta[i].Y, p.Z + delta[i].Z);

                var final = new Vec3(p.X + n.X * push, p.Y + n.Y * push, p.Z + n.Z * push);
                WriteXYZ(outStreams[pw.Stream], i * stride + pw.Offset, pw.Type, final.X, final.Y, final.Z);
                if (outPos is not null) outPos[i] = final;

                // Only vertices the cap actually reached get a new normal; everything else keeps the
                // bytes it arrived with. The normal element has its own stream â€” it need not be pos's.
                if (plan is not null && norm is { } ne2 && plan.NodeWeight[plan.NodeOf[i]] > 0f)
                {
                    if (WriteNormal(outStreams[ne2.Stream], i * outStrides[ne2.Stream] + ne2.Offset, ne2.Type,
                            n.X, n.Y, n.Z))
                        normalsWritten++;
                    else
                        encoderMissing = true;
                }

                // ...and the UV it was projected onto, for the same vertices. Written into every uv slot
                // the mesh has, exactly as the normalization pass above did â€” uv1 mirrors uv0 for the
                // scroll shader, so leaving it on the donor's coordinate would show through there.
                //
                // uvs[] is updated with it too: the coverage test that decides which triangles survive
                // reads that array, and testing a moved vertex at its donor's UV asks about the wrong
                // part of the texture.
                if (plan is not null && plan.NodeUV is { } capUV && uv0 is { } u0w
                    && plan.NodeWeight[plan.NodeOf[i]] > 0f)
                {
                    var (cu, cv) = capUV[plan.NodeOf[i]];
                    uvs[i] = (cu, cv);
                    int so2 = i * outStrides[u0w.Stream];
                    bool half0 = u0w.Type is 13 or 14;
                    WriteUV2(outStreams[u0w.Stream], so2 + u0w.Offset, half0, cu, cv);
                    if (zwValid)         WriteUV2(outStreams[u0w.Stream], so2 + zwOff, zwHalf, cu, cv);
                    if (uv1El is { } e2) WriteUV2(outStreams[e2.Stream], i * outStrides[e2.Stream] + e2.Offset, e2.Type is 13 or 14, cu, cv);
                    if (appendUv1)       WriteUV2(outStreams[uv1Stream], i * outStrides[uv1Stream] + bs[uv1Stream], false, cu, cv);
                    uvsWritten++;
                }
            }

            if (plan is not null)
            {
                capSrcPos = basePos;
                capOutPos = outPos;
            }

            // Report what the cap did. "0 moved" on a mesh that should hold the toes means the mask
            // missed the UV; "0 normals" means it moved geometry nobody will see move.
            if (capLog != null && cap?.ToeCap != null)
            {
                int moved = 0;
                float max = 0f;
                if (delta != null)
                    foreach (var d in delta)
                    {
                        float m = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
                        if (m > 1e-7f) moved++;
                        max = MathF.Max(max, m);
                    }
                capLog($"toe cap: {moved}/{vc} vertices moved, max {max:0.#####}, {normalsWritten} normals rewritten"
                     + (plan is null ? "" : $", {plan.NewTriangles.Count} triangles rebuilt")
                     + (uvsWritten == 0 ? "" : $", {uvsWritten} uvs reprojected"));
                if (encoderMissing)
                    capLog($"toe cap: no encoder for normal type {norm?.Type} â€” that mesh keeps its old shading");
            }
        }

        // Declaration: copy the source mesh's block verbatim, splicing in a uv1 element only when we
        // appended one (the .zw / existing-uidx1 cases already declare their uv1).
        declBlock = new byte[DeclSize];
        Array.Copy(s, srcDeclOff, declBlock, 0, DeclSize);
        if (appendUv1)
            for (int e = 0; e < 17; e++)
            {
                int o = e * 8;
                if (declBlock[o] != 0xFF) continue;
                declBlock[o]     = (byte)uv1Stream;
                declBlock[o + 1] = bs[uv1Stream];
                declBlock[o + 2] = 1;                         // Float2
                declBlock[o + 3] = UseUV;
                declBlock[o + 4] = 1;                         // usageIndex 1
                if (e + 1 < 17) declBlock[(e + 1) * 8] = 0xFF;
                break;
            }
        return uvUnmapped;
    }

    /// <summary>
    /// Re-fit a converted mesh's tangent frame to its NEW UVs.
    /// <para/>
    /// A tangent basis is DEFINED by the UV parameterization, and <see cref="BuildVerbatim"/> copies every
    /// vertex stream byte-for-byte before overwriting position/colour/UV â€” so a mesh whose UVs were moved
    /// into another body's layout is left describing the layout it came from. The shell samples its normal
    /// map in tangent space (relief in R/G, the coverage gate in blue), so a stale frame lights the fabric
    /// from the wrong direction, and a MIRRORED island (bibo's foot against gen3's, say) flips handedness
    /// and reads as an inverted normal map on that part alone while the rest of the shell looks right.
    /// <para/>
    /// Rather than author a frame from scratch â€” which would mean committing to the game's sign and slot
    /// conventions, and getting either backwards inverts every converted part â€” this READS the convention
    /// off the source and reapplies it. Per vertex it derives the surface tangent/binormal twice, from the
    /// old UVs and from the new, takes the sign the stored vector had against the OLD direction, and writes
    /// that same sign against the NEW one. Handedness (the .w lane) flips only when the two frames' own
    /// handedness disagrees. Whatever usage 5 and 6 mean to the shader, the geometry is what changed and
    /// the geometry is all this touches.
    /// <para/>
    /// Only triangles that survived the coverage trim contribute, so vertices no longer referenced get no
    /// accumulation and are left alone â€” the compaction below drops them anyway. Returns true when at
    /// least one vertex was re-fitted.
    /// </summary>
    private static bool RetangentMesh(
        byte[][] outStreams, byte[] outStrides, VElem[] decl, ushort vc,
        (float U, float V)[] uvOld, (float U, float V)[] uvNew, List<ushort[]> keptPerSub)
    {
        VElem? pos = null, norm = null, tanEl = null, binEl = null;
        foreach (var el in decl)
            switch (el.Usage)
            {
                case UsePosition: pos ??= el; break;
                case UseNormal:   norm ??= el; break;
                case UseTangent2: tanEl ??= el; break;   // usage 5 â€” tracks dP/du
                case UseTangent1: binEl ??= el; break;   // usage 6 â€” tracks dP/dv (the one bodies carry)
            }
        if (pos is not { } pe || norm is not { } ne) return false;
        if (tanEl == null && binEl == null) return false;   // nothing to re-fit

        // Positions here are the PUSHED ones the shell will ship, which is the surface the frame belongs
        // to. The push is along the normal and identical for both UV sets, so it can't skew the comparison.
        var px = new float[vc * 3];
        var nrm = new float[vc * 3];
        Span<float> tmp = stackalloc float[4];
        for (int i = 0; i < vc; i++)
        {
            ReadTyped(outStreams[pe.Stream], i * outStrides[pe.Stream] + pe.Offset, pe.Type, tmp);
            px[i * 3] = tmp[0]; px[i * 3 + 1] = tmp[1]; px[i * 3 + 2] = tmp[2];
            ReadTyped(outStreams[ne.Stream], i * outStrides[ne.Stream] + ne.Offset, ne.Type, tmp);
            float a = tmp[0], b = tmp[1], c = tmp[2];
            if (ne.Type == 8) { a = a * 2 - 1; b = b * 2 - 1; c = c * 2 - 1; }
            nrm[i * 3] = a; nrm[i * 3 + 1] = b; nrm[i * 3 + 2] = c;
        }

        var tOld = new float[vc * 3]; var bOld = new float[vc * 3];
        var tNew = new float[vc * 3]; var bNew = new float[vc * 3];
        AccumulateFrames(px, uvOld, keptPerSub, tOld, bOld);
        AccumulateFrames(px, uvNew, keptPerSub, tNew, bNew);

        int fixedUp = 0;
        for (int i = 0; i < vc; i++)
        {
            int o = i * 3;
            float nx = nrm[o], ny = nrm[o + 1], nz = nrm[o + 2];
            float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-8f) continue;
            nx /= nl; ny /= nl; nz /= nl;

            // All four directions must be well-defined: a vertex touched only by UV-degenerate triangles
            // has no measurable frame either side, and guessing one is worse than keeping what it had.
            if (!InTangentPlane(tOld, o, nx, ny, nz, out var tox, out var toy, out var toz)) continue;
            if (!InTangentPlane(bOld, o, nx, ny, nz, out var box, out var boy, out var boz)) continue;
            if (!InTangentPlane(tNew, o, nx, ny, nz, out var tnx, out var tny, out var tnz)) continue;
            if (!InTangentPlane(bNew, o, nx, ny, nz, out var bnx, out var bny, out var bnz)) continue;

            // (N x B) . T â€” positive or negative tells the two frames apart; disagreement means the new
            // island is mirrored relative to the old one.
            float hOld = (ny * boz - nz * boy) * tox + (nz * box - nx * boz) * toy + (nx * boy - ny * box) * toz;
            float hNew = (ny * bnz - nz * bny) * tnx + (nz * bnx - nx * bnz) * tny + (nx * bny - ny * bnx) * tnz;
            bool mirrored = hOld * hNew < 0;

            bool any = false;
            if (binEl is { } be)
                any |= Refit(outStreams[be.Stream], i * outStrides[be.Stream] + be.Offset, be.Type,
                             box, boy, boz, bnx, bny, bnz, mirrored);
            if (tanEl is { } te)
                any |= Refit(outStreams[te.Stream], i * outStrides[te.Stream] + te.Offset, te.Type,
                             tox, toy, toz, tnx, tny, tnz, mirrored);
            if (any) fixedUp++;
        }
        return fixedUp > 0;
    }

    /// <summary>
    /// Sum each triangle's surface derivatives (dP/du, dP/dv) onto its three vertices, the standard
    /// area-weighted tangent accumulation. Triangles with no UV area contribute no direction and are
    /// skipped rather than dividing by ~0.
    /// </summary>
    private static void AccumulateFrames(float[] p, (float U, float V)[] uv, List<ushort[]> keptPerSub,
                                         float[] tAcc, float[] bAcc)
    {
        foreach (var keep in keptPerSub)
            for (int k = 0; k + 2 < keep.Length; k += 3)
            {
                int ia = keep[k], ib = keep[k + 1], ic = keep[k + 2];
                int a = ia * 3, b = ib * 3, c = ic * 3;
                float e1x = p[b] - p[a], e1y = p[b + 1] - p[a + 1], e1z = p[b + 2] - p[a + 2];
                float e2x = p[c] - p[a], e2y = p[c + 1] - p[a + 1], e2z = p[c + 2] - p[a + 2];
                float du1 = uv[ib].U - uv[ia].U, dv1 = uv[ib].V - uv[ia].V;
                float du2 = uv[ic].U - uv[ia].U, dv2 = uv[ic].V - uv[ia].V;
                float det = du1 * dv2 - du2 * dv1;
                if (MathF.Abs(det) < 1e-12f) continue;
                float r = 1f / det;
                float tx = (e1x * dv2 - e2x * dv1) * r, ty = (e1y * dv2 - e2y * dv1) * r, tz = (e1z * dv2 - e2z * dv1) * r;
                float bx = (e2x * du1 - e1x * du2) * r, by = (e2y * du1 - e1y * du2) * r, bz = (e2z * du1 - e1z * du2) * r;
                foreach (var v in (ReadOnlySpan<int>)[a, b, c])
                {
                    tAcc[v] += tx; tAcc[v + 1] += ty; tAcc[v + 2] += tz;
                    bAcc[v] += bx; bAcc[v + 1] += by; bAcc[v + 2] += bz;
                }
            }
    }

    /// <summary>Gram-Schmidt an accumulated derivative into the plane of the normal and normalize it.
    /// False when nothing measurable survives (a vertex with no non-degenerate triangle).</summary>
    private static bool InTangentPlane(float[] acc, int o, float nx, float ny, float nz,
                                       out float x, out float y, out float z)
    {
        x = acc[o]; y = acc[o + 1]; z = acc[o + 2];
        float d = x * nx + y * ny + z * nz;
        x -= nx * d; y -= ny * d; z -= nz * d;
        float len = MathF.Sqrt(x * x + y * y + z * z);
        if (len < 1e-8f) return false;
        x /= len; y /= len; z /= len;
        return true;
    }

    /// <summary>
    /// Rewrite one stored frame vector so it points along <c>new*</c> instead of <c>old*</c>, keeping the
    /// sign it had relative to the old direction â€” that sign IS the source's convention, whatever it is.
    /// <paramref name="mirrored"/> flips the .w handedness lane. Returns false for an element type we have
    /// no encoder for, leaving it byte-identical rather than writing something malformed.
    /// </summary>
    private static bool Refit(byte[] a, int off, byte type,
                              float ox, float oy, float oz, float nx, float ny, float nz, bool mirrored)
    {
        Span<float> cur = stackalloc float[4];
        ReadTyped(a, off, type, cur);
        bool byteNorm = type == 8;
        float sx = cur[0], sy = cur[1], sz = cur[2], sw = cur[3];
        if (byteNorm) { sx = sx * 2 - 1; sy = sy * 2 - 1; sz = sz * 2 - 1; sw = sw * 2 - 1; }
        float sign = sx * ox + sy * oy + sz * oz >= 0f ? 1f : -1f;
        float w = mirrored ? -sw : sw;
        return WriteVec4Typed(a, off, type, sign * nx, sign * ny, sign * nz, w);
    }

    /// <summary>Encode a signed 4-vector into a vertex element. False for a type this can't write.</summary>
    private static bool WriteVec4Typed(byte[] a, int off, byte type, float x, float y, float z, float w)
    {
        static byte B(float v) => (byte)Math.Clamp((int)MathF.Round((v * 0.5f + 0.5f) * 255f), 0, 255);
        switch (type)
        {
            case 8:            // Ubyte4n â€” what character models actually use for tangent/binormal
                a[off] = B(x); a[off + 1] = B(y); a[off + 2] = B(z); a[off + 3] = B(w);
                return true;
            case 10:           // Short4n
                W16(a, off,     (ushort)(short)Math.Clamp((int)MathF.Round(x * 32767f), -32767, 32767));
                W16(a, off + 2, (ushort)(short)Math.Clamp((int)MathF.Round(y * 32767f), -32767, 32767));
                W16(a, off + 4, (ushort)(short)Math.Clamp((int)MathF.Round(z * 32767f), -32767, 32767));
                W16(a, off + 6, (ushort)(short)Math.Clamp((int)MathF.Round(w * 32767f), -32767, 32767));
                return true;
            case 14:           // Half4
                W16(a, off, Half(x)); W16(a, off + 2, Half(y));
                W16(a, off + 4, Half(z)); W16(a, off + 6, Half(w));
                return true;
            case 3:            // Float4
                W32(a, off,      (uint)BitConverter.SingleToInt32Bits(x));
                W32(a, off + 4,  (uint)BitConverter.SingleToInt32Bits(y));
                W32(a, off + 8,  (uint)BitConverter.SingleToInt32Bits(z));
                W32(a, off + 12, (uint)BitConverter.SingleToInt32Bits(w));
                return true;
            case 2:            // Float3 (no handedness lane to keep)
                W32(a, off,     (uint)BitConverter.SingleToInt32Bits(x));
                W32(a, off + 4, (uint)BitConverter.SingleToInt32Bits(y));
                W32(a, off + 8, (uint)BitConverter.SingleToInt32Bits(z));
                return true;
            default:
                return false;
        }
    }

    /// <summary>A position/normal/displacement in model space.</summary>
    internal readonly record struct Vec3(float X, float Y, float Z);

    /// <summary>
    /// Mask value at which a vertex counts as part of the cap's CORE rather than its soft edge. The core
    /// alone sets the axis, the band and the slicing; a fringe of 1/255 covers a lot of ground and would
    /// otherwise drag all three off the toes.
    /// </summary>
    private const float ToeCapCoreWeight = 0.5f;

    /// <summary>Bounds on the ring count swept from the rim to the tip; the actual number follows edge length.</summary>
    private const int MinRings = 4, MaxRings = 64;

    /// <summary>
    /// Ring spacing as a fraction of the mesh's own edge length. Below 1 the cap is finer than the body
    /// it replaces, which is what lets the end taper with the toes instead of ending on a blunt cone.
    /// </summary>
    private const float RingDensity = 0.5f;

    /// <summary>Share of the cut region's vertices the rings may consume, leaving the rest as slack.</summary>
    private const float DonorBudget = 0.75f;

    /// <summary>
    /// Texels the toe-cap map is pulled in by before it cuts, when an authored cap is filling the hole.
    /// <para/>
    /// Zero now: this existed to hide the mismatch between where the map cuts and where the cap's mesh
    /// actually reaches, by leaving the shell lapping over the cap. The weld closes that properly, and
    /// the two work against each other â€” eroding by 3 left the lip a median of 0.007 from the rim instead
    /// of 0.002, and dragging it that far collapsed triangles (aspect 720, against 14 before). Kept as
    /// one number so the lap can be brought back if a cap is ever authored short of its map.
    /// </summary>
    private const int CapCutErode = 0;

    /// <summary>
    /// How far a boundary vertex may be dragged to meet the cap's rim. Past the widest mismatch the
    /// painted map produces (0.0093 measured, against a median edge of 0.0037) and short of the next
    /// open edge, so a vertex belonging to some other hole â€” an ankle cut, a coverage bite elsewhere â€”
    /// is left alone. It has to be tighter than it once was: the test is now "on the shell's boundary"
    /// rather than "the toe cap cut this away", which is a much broader set of vertices.
    /// </summary>
    private const float WeldRadius = 0.012f;

    /// <summary>
    /// How far a vertex the TOE-CAP cut exposed may be dragged to reach the cap's rim. Much longer than
    /// <see cref="WeldRadius"/> because that pull is the whole mechanism by which the map's deliberate
    /// over-cut is closed, and on a body other than the one the map was painted for it has real distance
    /// to cover â€” measured on Rue, 329 lip vertices sat beyond 0.012 and the join stayed open as a band
    /// of bare skin. The slivers a long pull leaves are dropped by <see cref="WeldCollapse"/>.
    /// </summary>
    private const float WeldCutReach = 0.04f;

    /// <summary>
    /// Fraction of the shell's own median edge whose SQUARE a triangle's area must fall under, after the
    /// weld touched it, before it counts as collapsed and is dropped.
    /// <para/>
    /// Deliberately tiny â€” a thousandth of a typical triangle's area â€” because every triangle dropped
    /// here is a hole in the shell. At 0.10 this removed real surface along the join and showed in game
    /// as bare skin between the shell and the cap; the dropped triangles also left 51 of their vertices
    /// behind as loose points, which is how it was finally caught. A sliver draws almost nothing and
    /// costs almost nothing to keep; a hole is visible.
    /// </summary>
    private const float WeldCollapse = 0.02f;

    /// <summary>
    /// How far a cap rim vertex may be moved to land on the shell's rim. Far tighter than
    /// <see cref="WeldRadius"/>, and deliberately so: the shell has already been pulled onto this rim, so
    /// all that is left to correct is the chord error, measured at 0.00047. A layer whose shell barely
    /// got cut contributes a handful of rim segments, and a generous radius would drag the whole cap
    /// boundary onto them.
    /// </summary>
    private const float CapWeldRadius = 0.002f;

    /// <summary>
    /// How wide a band behind the welded lip is ramped up toward the cap's standoff. The step being
    /// spread is the cap's authored height off the skin — measured at 1.6mm on Neolithe — and spreading
    /// it over several of the shell's median edges (0.0037) turns a one-triangle cliff into a slope
    /// nothing catches the light on. Too wide and the shell visibly swells before the join; too narrow
    /// and the crease survives.
    /// </summary>
    private const float CapFeather = 0.015f;

    /// <summary>
    /// Rings in from the cap's back seam over which its authored skinning is blended toward the body's.
    /// ONE â€” the open edge itself and nothing else. Everything else keeps exactly what the author
    /// weighted, including to the toenails, which the body's skin-only weights cannot express and which
    /// the cap collapses without. It was 3, which reached a ring and a half into geometry that had no
    /// business being touched: the two surfaces only have to agree where they meet.
    /// </summary>
    private const int CapSeamBlendRings = 1;

    /// <summary>
    /// Weld-then-drop rounds. Dropping a collapsed triangle exposes vertices that were interior when the
    /// boundary was worked out, so one round always leaves a few behind; the second finds them and in
    /// practice there is nothing left for a third.
    /// </summary>
    private const int WeldRounds = 3;

    /// <summary>
    /// Smallest island, as a fraction of the largest, that the authored cap will take UVs from. Keeps
    /// the feet and rejects the toenails, which carry their own UV island and are often the nearest
    /// surface to the cap â€” projecting onto one stretches a triangle across the gap between islands.
    /// </summary>
    private const float ProjectIslandFloor = 0.25f;

    /// <summary>
    /// Distinct landings kept per cap vertex before the seam pass chooses between them. Only ever more
    /// than one where the body's atlas is cut, or where two body parts nearly touch.
    /// </summary>
    private const int ProjectCandidates = 6;

    /// <summary>
    /// How far apart two landings must be in UV to count as different places. Below this they are the
    /// same patch of atlas reached through neighbouring triangles, and keeping both would crowd out the
    /// far side of a seam â€” which is the only rival that matters.
    /// </summary>
    private const float ProjectMergeUV = 0.004f;

    /// <summary>
    /// Cap edge lengths a rival landing may sit further away and still be considered. The two sides of a
    /// UV seam are welded in 3D, so the rival is normally within a whisker; this only has to cover a
    /// vertex sitting a little back from the cut.
    /// </summary>
    private const float ProjectSeamSlack = 1.5f;

    /// <summary>Sweeps of the agreement pass. It settles in three or four; the rest are free.</summary>
    private const int ProjectSeamPasses = 24;

    /// <summary>
    /// UV span across one cap face that means it has straddled a seam rather than covered texture. The
    /// cap's own faces measure about 0.0024, so this is two orders of magnitude clear of normal.
    /// </summary>
    private const float ProjectSeamSpan = 0.10f;

    /// <summary>
    /// Multiples of the cap's median UV stretch (UV distance per unit of 3D distance) at which an edge is
    /// taken to cross a cut in the body's atlas rather than to cover texture.
    /// </summary>
    private const float ProjectSeamStretch = 8f;

    /// <summary>
    /// Weight on staying near, relative to agreeing with the neighbours, in the seam pass. Keeps the
    /// undisputed interior exactly where nearest-hit put it.
    /// </summary>
    private const float ProjectNearBias = 1.0f;

    /// <summary>Rim vertices needed before a cut boundary is a usable loop to sew onto.</summary>
    private const int MinRimNodes = 8;

    /// <summary>
    /// How much of an island the cap may claim. Past this there is no rim left to sew to â€” a toenail is
    /// masked end to end â€” and the island is better left alone inside the cap.
    /// </summary>
    private const float MaxCoreFraction = 0.8f;

    /// <summary>How far past the last ring the end of the cap reaches, in ring spacings.</summary>
    private const float TipReach = 0.5f;

    /// <summary>
    /// Shrinking rings that round the end off before it closes, each halving the slot count of the one
    /// before, so the cap does not fan its full-width last ring straight to a point.
    /// <para/>
    /// They keep the rim's slot count â€” the grid patch closes whatever is left, so nothing has to narrow.
    /// </summary>
    private const int TipRings = 3;

    /// <summary>Fewest slots a dome ring is worth building with; below this the closing patch takes over.</summary>
    private const int MinDomeSlots = 8;

    /// <summary>
    /// How far the end domes over, as a fraction of the cap's own radius there. Scaling it to the ring
    /// spacing instead â€” which is perhaps a tenth of that â€” leaves the toe box ending in a stump.
    /// </summary>
    private const float TipRound = 0.3f;


    /// <summary>
    /// Closest the relaxed cap may come to the skin, in mesh edge lengths â€” the fabric's thickness. Taken
    /// from what a hand pass over this cap left in place (its tightest 5% sat at about a fifth of an edge).
    /// </summary>
    private const float SkinClearance = 0.2f;

    /// <summary>
    /// Furthest the finished cap may float above the skin it lies on, in mesh edge lengths â€” how much
    /// loft the fabric is allowed. Applies only where there is skin underneath: a slot bridging a gap has
    /// its allowance opened out in proportion, since nothing under it is worth measuring against.
    /// </summary>
    private const float MaxStandoff = 0.5f;

    /// <summary>How bridged a slot must be before nothing may pull it down toward the skin at all.</summary>
    private const float BridgeExempt = 0.999f;

    /// <summary>
    /// Passes lifting the cap off skin that comes through the middle of a face. Moving a corner changes
    /// what its neighbours measure, so it is worth repeating; it stops early once a pass finds nothing.
    /// </summary>
    private const int PokePasses = 4;

    /// <summary>
    /// Ceiling on how far that lift may move any one cap vertex, in mesh edge lengths. The correction
    /// wanted is a fraction of an edge; this is only here so that a bad measurement cannot send a vertex
    /// off the foot, which an earlier version â€” summing every skin vertex's request instead of taking
    /// the largest â€” did spectacularly.
    /// </summary>
    private const float MaxPokeLift = 1.5f;

    /// <summary>
    /// How far to the side the cap may be and still count as covering a skin vertex, in edge lengths.
    /// Without it the nearest cap face to a vertex on a toe's inner flank is the bridge across the gap,
    /// and lifting that along the flank's own normal pushes the bridge into the neighbouring toe.
    /// </summary>
    private const float PokeReach = 0.25f;

    /// <summary>Aspect ratio above which a cap face is badly enough shaped to be worth evening out.</summary>
    private const float TangentTrigger = 4f;

    /// <summary>Passes of that evening-out. It converges; more than this buys nothing.</summary>
    private const int TangentPasses = 30;

    /// <summary>
    /// How far it may slide any vertex from where the rings placed it, in edge lengths. Sliding along
    /// the surface cannot change the silhouette, but it can still walk a vertex a long way round the cap
    /// given enough passes, and that spoils the cells it walks through.
    /// </summary>
    private const float TangentClamp = 0.75f;

    /// <summary>Skin triangles shortlisted per cap vertex, so clearance can be enforced on every pass.</summary>
    private const int SkinCandidates = 16;

    /// <summary>
    /// How wide a gap must be, in edge lengths, before the cap spans it rather than following the surface
    /// down into it. Below this the cap settles onto the toe â€” and into the shallow valleys between them,
    /// which is wanted; above it, it bridges. Lower creeps deeper into the valleys, higher bridges more.
    /// </summary>
    private const float BridgeSpan = 1.5f;

    /// <summary>Angular buckets a cross-section's outline is read into, when it has more slots than this.</summary>
    private const int MinOutlineBins = 32;


    /// <summary>Smoothing passes over the finished cap â€” the equivalent of relaxing it by hand.</summary>
    private const int RelaxPasses = 24;

    /// <summary>
    /// Smoothing passes over the end once the closing patch exists. Separate from the main relax: the
    /// patch has never been smoothed at all when this runs, so it starts from further out.
    /// </summary>
    private const int TipRelaxPasses = 24;

    /// <summary>
    /// Extra rings behind the dome the end relax is allowed to move, beyond the dome itself. The spikes
    /// the tip reads as crunchy sit on the last full rings, not only on the dome, and pinning those
    /// leaves them exactly where they were however many passes run.
    /// </summary>
    private const int TipRelaxSpan = 3;

    /// <summary>
    /// How many rings back from the rim the join is smoothed over. The cut boundary is denser and less
    /// even than the mesh it was cut from, and the first rings inherit its spacing, so this is where the
    /// pinched cells over the top of the toes come from.
    /// </summary>
    private const int RimRelaxRings = 4;

    /// <summary>How far each pass moves a vertex toward its neighbours' average.</summary>
    private const float RelaxRate = 0.5f;

    /// <summary>
    /// How firmly the relax holds each vertex in its own ring's plane, 1 being rigidly. Rigid keeps the
    /// last rings from walking back off the toe tips, but it also means a bump along a ring can only be
    /// smoothed across the section and never along the foot, and the surface converges lumpy.
    /// </summary>
    private const float RingPlaneHold = 0.5f;

    /// <summary>
    /// How far the cap stands off the outline it is built from, as a fraction of the cross-section's own
    /// radius â€” the fabric's thickness, in effect. Zero would leave it tangent to the toes underneath and
    /// they would poke through the moment the foot deforms.
    /// </summary>
    private const float CapClearance = 0.02f;

    /// <summary>
    /// How far beyond its own slice a cross-section reads points for its hull, in slice thicknesses.
    /// Overlapping the windows keeps the outline from jumping where a toe ends; too much and the cap
    /// stops following the shape it is meant to enclose.
    /// </summary>
    private const float SliceWindow = 0.7f;

    /// <summary>
    /// Largest share of a mesh an island may be and still be dropped when the cap swallows it whole. A
    /// toenail is a tenth of the mesh it lives in; a foot, under a mask painted over the whole foot, is
    /// all of it â€” and must never be removed.
    /// </summary>
    private const float SmallIslandFraction = 0.25f;

    /// <summary>
    /// Smallest masked island worth capping. Guards against a stray scrap of geometry â€” a toenail, a
    /// detached sliver â€” being treated as its own toe box.
    /// </summary>
    private const int MinToeCapNodes = 24;

    /// <summary>
    /// Fewest slots a ring is built with, however small its cross-section gets. Below a handful the
    /// ring stops describing the shape at all.
    /// </summary>
    private const int MinRingSlots = 12;

    /// <summary>Closest two neighbouring slots in a ring may be smoothed, in edge lengths.</summary>
    private const float SlotMinGap = 0.35f;


    /// <summary>
    /// What fraction of the rim's slot count an average ring is expected to carry, for budgeting donors.
    /// Only an estimate: too high and the cap gets fewer rings than it could afford, too low and a ring
    /// runs the pool dry part way and is abandoned.
    /// </summary>
    private const float RingWidthEstimate = 0.7f;

    /// <summary>Points a cross-section needs before its hull is a meaningful outline.</summary>
    private const int MinSliceNodes = 8;

    /// <summary>How many times a cross-section may widen its band looking for enough points.</summary>
    private const int BandWidenSteps = 5;



    /// <summary>
    /// Fraction of its original area a capped triangle must keep to survive. Relative, not absolute, so
    /// a dense body isn't culled for having small triangles to begin with.
    /// </summary>
    private const float DegenerateAreaFraction = 0.02f;

    /// <summary>Movement below which a vertex counts as untouched, matching the cap's own reporting.</summary>
    private const float DegenerateMoveEpsilon = 1e-7f;

    /// <summary>Distance at which two capped corners count as the same point â€” the weld's own grid.</summary>
    private const float DegenerateWeldDistance = 1e-5f;

    /// <summary>
    /// Every triangle of one mesh, as mesh-local vertex indices, across all of its submeshes. The toe cap
    /// needs the mesh's full topology (adjacency) â€” including submeshes coverage or the connector filter
    /// will later drop, since those still hold the surface together.
    /// </summary>
    private static ushort[] MeshTriangles(Source src, ushort subIdx, ushort subCount)
    {
        var s = src.S;
        var tris = new List<ushort>();
        for (int su = 0; su < subCount; su++)
        {
            int ss = src.SubmeshStart + (subIdx + su) * 16;
            uint so = BitConverter.ToUInt32(s, ss), sc = BitConverter.ToUInt32(s, ss + 4);
            for (uint t = 0; t + 2 < sc; t += 3)
            {
                int p = src.Ib + (int)(so + t) * 2;
                tris.Add(BitConverter.ToUInt16(s, p));
                tris.Add(BitConverter.ToUInt16(s, p + 2));
                tris.Add(BitConverter.ToUInt16(s, p + 4));
            }
        }
        return tris.ToArray();
    }

    /// <summary>
    /// Toe cap: per-vertex displacement that inflates the masked region onto a smooth envelope, so a
    /// stocking shell webs the gaps between the toes instead of sleeving each toe individually.
    /// <para/>
    /// The region is treated as a height field measured radially from the centre of the masked area, and
    /// each height is repeatedly raised to at least the average of its neighbours'. Smoothing alone can't
    /// do this: it equalizes, converging right back onto the toes, and clamping it outward along each
    /// vertex's own normal stalls at once, because inside a gap the normals point sideways ACROSS the gap
    /// rather than out of it. Raising toward the neighbour mean, in a frame the whole region shares, lets
    /// a toe tip's height propagate into the gaps beside it â€” the taut membrane real hosiery forms.
    /// <para/>
    /// The result only ever inflates, so the toes stay inside the cap instead of poking through it, and
    /// every step is scaled by the vertex's mask value, so black is pinned and the cap fades into the
    /// untouched shell across the grey.
    /// <para/>
    /// Vertices are WELDED by source position first: a body mesh splits vertices at UV seams, and two
    /// coincident copies with different neighbour sets would otherwise smooth apart and crack open. Each
    /// weld group moves as one, by a single shared delta, so hard-edge normal splits keep their offsets.
    /// <para/>
    /// Returns null when nothing is masked â€” the caller then writes exactly what it would have without
    /// this feature.
    /// </summary>
    internal static Vec3[]? ToeCapDelta(
        Vec3[] pos, Vec3[] nrm, (float U, float V)[] uv, ushort[] tris,
        byte[] mask, int mw, int mh, float strength)
        => ToeCapSolve(pos, nrm, uv, tris, mask, mw, mh, strength)?.Delta;

    /// <summary>
    /// What the toe cap decided: the displacement, plus the welding and per-node data the normal pass
    /// needs. Moving the vertices is only half the job â€” a shell whose normals still describe five
    /// separate toes shades as five separate toes no matter where the geometry sits.
    /// </summary>
    internal sealed class ToeCapPlan
    {
        /// <summary>Per-vertex displacement, indexed like the mesh's vertices.</summary>
        public required Vec3[] Delta { get; init; }

        /// <summary>Vertex index -> welded node index.</summary>
        public required int[] NodeOf { get; init; }

        /// <summary>Per-node mask weight (max over the node's members), 0 where the cap left it alone.</summary>
        public required float[] NodeWeight { get; init; }

        /// <summary>Per-node normalized average of the members' SOURCE normals.</summary>
        public required Vec3[] NodeNormal { get; init; }

        /// <summary>Nodes inside the cap: every triangle touching one is cut out and replaced.</summary>
        public required bool[] CutNode { get; init; }

        /// <summary>
        /// Per-node UV for the nodes the cap moved, projected back onto the surface it replaced. Null
        /// when nothing moved. A cap vertex is a REUSED one, and it arrives carrying the UV of wherever
        /// it was borrowed from, which is somewhere else entirely on the toe box.
        /// </summary>
        public (float U, float V)[]? NodeUV { get; init; }

        /// <summary>The rebuilt cap, as mesh-local vertex indices.</summary>
        public required List<(ushort A, ushort B, ushort C)> NewTriangles { get; init; }

        /// <summary>Nodes on an island the cap swallowed whole: their triangles are simply removed.</summary>
        public required bool[] DropNode { get; init; }


        /// <summary>Does this triangle belong to the region the cap replaced?</summary>
        public bool IsCut(ushort a, ushort b, ushort c)
            => CutNode[NodeOf[a]] || CutNode[NodeOf[b]] || CutNode[NodeOf[c]];

        /// <summary>Is this triangle entirely on a swallowed island, and so nothing the shell should draw?</summary>
        public bool IsDropped(ushort a, ushort b, ushort c)
            => DropNode[NodeOf[a]] && DropNode[NodeOf[b]] && DropNode[NodeOf[c]];
    }

    private static ToeCapPlan? ToeCapSolve(
        Vec3[] pos, Vec3[] nrm, (float U, float V)[] uv, ushort[] tris,
        byte[] mask, int mw, int mh, float strength, Action<string>? capLogSink = null,
        bool buildGeometry = true)
    {
        int vc = pos.Length;
        if (vc == 0 || mw <= 0 || mh <= 0 || strength <= 0f || mask.Length < mw * mh) return null;

        // Mask weight per vertex, sampled nearest at the vertex's (already normalized) UV.
        var w = new float[vc];
        bool any = false;
        for (int i = 0; i < vc; i++)
        {
            int x = ((int)MathF.Floor(uv[i].U * mw) % mw + mw) % mw;
            int y = ((int)MathF.Floor(uv[i].V * mh) % mh + mh) % mh;
            float m = mask[y * mw + x] / 255f * strength;
            if (m <= 0f) continue;
            w[i] = MathF.Min(1f, m);
            any = true;
        }
        if (!any) return null;

        var nodeOf = WeldByPosition(pos, out int nodeCount);

        var start = new Vec3[nodeCount];
        var nNorm = new Vec3[nodeCount];
        var nW = new float[nodeCount];
        var members = new int[nodeCount];
        for (int i = 0; i < vc; i++)
        {
            int n = nodeOf[i];
            start[n] = new Vec3(start[n].X + pos[i].X, start[n].Y + pos[i].Y, start[n].Z + pos[i].Z);
            nNorm[n] = new Vec3(nNorm[n].X + nrm[i].X, nNorm[n].Y + nrm[i].Y, nNorm[n].Z + nrm[i].Z);
            nW[n] = MathF.Max(nW[n], w[i]);
            members[n]++;
        }
        for (int n = 0; n < nodeCount; n++)
        {
            float inv = 1f / members[n];
            start[n] = new Vec3(start[n].X * inv, start[n].Y * inv, start[n].Z * inv);
            var q = nNorm[n];
            float len = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            nNorm[n] = len > 1e-6f ? new Vec3(q.X / len, q.Y / len, q.Z / len) : default;
        }

        // Edge adjacency over the welded nodes, deduped (a shared edge would otherwise weight twice).
        var adj = new List<int>[nodeCount];
        var seen = new HashSet<long>();
        void Link(int a, int b)
        {
            if (a == b) return;
            long key = a < b ? (long)a * nodeCount + b : (long)b * nodeCount + a;
            if (!seen.Add(key)) return;
            (adj[a] ??= new List<int>()).Add(b);
            (adj[b] ??= new List<int>()).Add(a);
        }
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;   // never fault on a bad index
            int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
            Link(a, b); Link(b, c); Link(c, a);
        }

        // Connected components: the two feet are separate islands and must never share a centre, or the
        // envelope would bridge the gap BETWEEN them.
        var comp = new int[nodeCount];
        Array.Fill(comp, -1);
        int compCount = 0;
        var stack = new Stack<int>();
        for (int n = 0; n < nodeCount; n++)
        {
            if (comp[n] >= 0) continue;
            comp[n] = compCount;
            stack.Push(n);
            while (stack.Count > 0)
            {
                int q = stack.Pop();
                if (adj[q] == null) continue;
                foreach (int k in adj[q])
                    if (comp[k] < 0) { comp[k] = compCount; stack.Push(k); }
            }
            compCount++;
        }

        var maskedByComp = new List<int>[compCount];
        for (int n = 0; n < nodeCount; n++)
            if (nW[n] > 0f) (maskedByComp[comp[n]] ??= new List<int>()).Add(n);

        var target = new Vec3[nodeCount];
        var hasTarget = new bool[nodeCount];
        var dropNode = new bool[nodeCount];
        // Which ring and slot placed each cap vertex, so a defect in the finished mesh can be traced
        // back to the construction that made it instead of guessed at from its geometry.
        var fromRing = new int[nodeCount];
        var fromSlot = new int[nodeCount];
        Array.Fill(fromRing, -1);
        Array.Fill(fromSlot, -1);
        // How much of this node's placement was a bridge across empty space rather than the surface it
        // lies on. 1 means nothing is under it, so nothing may pull it down; the closing patch keeps that
        // default so the rounded end is never dragged back onto the toe tips.
        var nodeFill = new float[nodeCount];
        Array.Fill(nodeFill, 1f);
        var cutNode = new bool[nodeCount];
        var newTris = new List<(ushort A, ushort B, ushort C)>();
        bool capped = false;

        // One representative vertex per node â€” the cap's triangles are written in vertex indices.
        var repOf = new ushort[nodeCount];
        var haveRep = new bool[nodeCount];
        for (int i = 0; i < vc; i++)
        {
            int n = nodeOf[i];
            if (!haveRep[n]) { repOf[n] = (ushort)i; haveRep[n] = true; }
        }
        ushort Rep(int n) => repOf[n];

        var islandSize = new int[compCount];
        for (int n = 0; n < nodeCount; n++) islandSize[comp[n]]++;

        // Which islands the cap swallows whole â€” the toenails. Settled BEFORE anything is capped, because
        // the foot's own component is capped in this same loop and needs to know about them by then: on
        // some bodies the nails are a separate MESH, but on others (Neolithe) they are separate islands
        // inside the foot mesh itself, and then nothing outside this function can see them at all.
        for (int c = 0; c < compCount; c++)
        {
            var m2 = maskedByComp[c];
            if (m2 is not { Count: >= MinToeCapNodes }) continue;
            int core2 = 0;
            foreach (int n in m2) if (nW[n] >= ToeCapCoreWeight) core2++;
            if (core2 < MinToeCapNodes) continue;
            if (core2 > MaxCoreFraction * islandSize[c] && islandSize[c] <= nodeCount * SmallIslandFraction)
                foreach (int n in m2) dropNode[n] = true;
        }

        for (int c = 0; c < compCount; c++)
        {
            var masked = maskedByComp[c];
            // Every island that the mask touches at all, and what happened to it. The cut silently
            // declining an island looks exactly like the cut working â€” the shell simply comes out whole
            // â€” and a mask that lit the right region in the atlas still cut nothing at all because of a
            // gate down here. Cheap enough to always report; a foot has a handful of islands.
            if (masked is { Count: > 0 })
                capLogSink?.Invoke($"toe cap: island {c} of {islandSize[c]} node(s), {masked.Count} masked"
                    + (masked.Count < MinToeCapNodes ? $" â€” SKIPPED, under MinToeCapNodes ({MinToeCapNodes})" : ""));
            if (masked is not { Count: >= MinToeCapNodes }) continue;

            // The CORE of the mask â€” where it is actually painted in, not its antialiased fringe. A soft
            // edge covers a lot of ground at a value of 1 or 2/255, and letting that define the region
            // stretches it over the whole foot: the axis tilts and the slices below land mostly behind the
            // toes, where they do nothing. Everything that sets up the frame uses the core; the fringe
            // still moves, just by its own small weight.
            var core = new List<int>();
            foreach (int n in masked)
                if (nW[n] >= ToeCapCoreWeight) core.Add(n);
            if (core.Count < MinToeCapNodes)
            {
                capLogSink?.Invoke($"toe cap: island {c} â€” SKIPPED, core {core.Count} of {masked.Count} "
                    + $"masked is under MinToeCapNodes ({MinToeCapNodes}); mask weight below "
                    + $"{ToeCapCoreWeight} does not count");
                continue;
            }

            // A cap is sewn onto surviving geometry. An island that is ENTIRELY masked â€” each toenail is
            // â€” has no rim to sew to, so no cap can be built for it. It used to be left where it was, on
            // the assumption it would end up inside the cap; that held only while the cap ballooned over
            // the toes. Now that the cap hugs them, the nails stand proud of it in ten little scallops,
            // which is exactly the crunch it reads as. They are underneath a stocking, so drop them.
            //
            // Only ever a SMALL island: a mask painted over a whole foot would otherwise swallow the foot.
            if (core.Count > MaxCoreFraction * islandSize[c])
            {
                capLogSink?.Invoke($"toe cap: island {c} â€” SKIPPED, core {core.Count} is over "
                    + $"{MaxCoreFraction:P0} of the island's {islandSize[c]} node(s)");
                continue;   // marked by the pre-pass above
            }
            capLogSink?.Invoke($"toe cap: island {c} â€” CUT, core {core.Count} of {islandSize[c]} node(s)");

            // An authored cap is filling this region, so only the CUT is wanted: take the toe box out
            // and leave it to the modelled mesh. Everything past here â€” the swept rings, the dome, the
            // closing patch, the relax and the clearance passes that argue with it â€” exists solely to
            // invent a surface to put back, and it is exactly what the authored cap replaces.
            if (!buildGeometry)
            {
                foreach (int n in core) cutNode[n] = true;
                capped = true;
                continue;
            }

            float cx = 0, cy = 0, cz = 0, wsum = 0;
            foreach (int n in core)
            {
                cx += start[n].X * nW[n]; cy += start[n].Y * nW[n]; cz += start[n].Z * nW[n];
                wsum += nW[n];
            }
            if (wsum <= 0f) continue;
            var mid = new Vec3(cx / wsum, cy / wsum, cz / wsum);

            float ax = 0, ay = 0, az = 0;
            int all = 0;
            for (int n = 0; n < nodeCount; n++)
                if (comp[n] == c) { ax += start[n].X; ay += start[n].Y; az += start[n].Z; all++; }
            var islandMid = new Vec3(ax / all, ay / all, az / all);

            // A mask covering its whole island puts the two centres on top of each other and leaves no
            // direction; fall back to the region's longest extent, which for a foot is still its length.
            var axis = Normalize(new Vec3(mid.X - islandMid.X, mid.Y - islandMid.Y, mid.Z - islandMid.Z))
                    ?? LongestExtent(start, core);
            if (axis is null) continue;
            Basis(axis.Value, out var eu, out var ev);

            float Axial(Vec3 p) => (p.X - mid.X) * axis.Value.X + (p.Y - mid.Y) * axis.Value.Y + (p.Z - mid.Z) * axis.Value.Z;
            (float X, float Y) Flatten(Vec3 p)
            {
                var d = new Vec3(p.X - mid.X, p.Y - mid.Y, p.Z - mid.Z);
                return (d.X * eu.X + d.Y * eu.Y + d.Z * eu.Z, d.X * ev.X + d.Y * ev.Y + d.Z * ev.Z);
            }

            float lo = float.MaxValue, hi = float.MinValue;
            foreach (int n in core) { float t = Axial(start[n]); lo = MathF.Min(lo, t); hi = MathF.Max(hi, t); }
            float span = hi - lo;
            if (span <= 1e-6f) continue;

            // â”€â”€ the cut â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // Every triangle with a core corner leaves the mesh, and the edges left used by only one of
            // them form the rim the cap is sewn onto. Displacing the toes could never work â€” a stocking's
            // toe box is a DIFFERENT surface, not the toes moved â€” so the toes come out and a new one
            // goes in, exactly as a modeller builds it.
            var inCut = new bool[nodeCount];
            foreach (int n in core) inCut[n] = true;

            // A painted mask is never perfectly solid: grey specks and the deep creases between the toes
            // leave patches of unmasked geometry STRANDED inside the cut. Each one survives as a scrap
            // floating under the finished cap, ringed by its own hole â€” the overlapping shards on the top
            // of the foot, and the reason a smaller mask made it worse. Anything no longer joined to the
            // surviving foot is absorbed into the cut, which also leaves exactly one rim to sew.
            var reached = new bool[nodeCount];
            var patches = new List<List<int>>();
            var flood = new Stack<int>();
            for (int n = 0; n < nodeCount; n++)
            {
                if (comp[n] != c || inCut[n] || reached[n]) continue;
                var patch = new List<int>();
                flood.Push(n);
                reached[n] = true;
                while (flood.Count > 0)
                {
                    int q = flood.Pop();
                    patch.Add(q);
                    if (adj[q] == null) continue;
                    foreach (int k in adj[q])
                        if (comp[k] == c && !inCut[k] && !reached[k]) { reached[k] = true; flood.Push(k); }
                }
                patches.Add(patch);
            }
            int mainPatch = 0;
            for (int i = 1; i < patches.Count; i++)
                if (patches[i].Count > patches[mainPatch].Count) mainPatch = i;
            for (int i = 0; i < patches.Count; i++)
            {
                if (i == mainPatch) continue;
                foreach (int n in patches[i])
                {
                    inCut[n] = true;
                    nW[n] = 1f;       // fully inside the cap, so its normal is rebuilt with the rest
                    core.Add(n);      // and it joins the pool the rings draw their vertices from
                }
            }

            // The skin the cap has to stay off: the triangles it replaced, at their original positions,
            // each with the direction that is OUT of the body â€” taken from the corners' own normals, since
            // index winding is not dependable here.
            var skinTris = new List<(Vec3 A, Vec3 B, Vec3 C, Vec3 Out)>();

            var edgeUse = new Dictionary<(int, int), int>();
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;
                int na = nodeOf[tris[t]], nb = nodeOf[tris[t + 1]], nc2 = nodeOf[tris[t + 2]];
                if (comp[na] != c) continue;
                if (!inCut[na] && !inCut[nb] && !inCut[nc2]) continue;
                {
                    Vec3 pa = start[na], pb = start[nb], pc = start[nc2];
                    float ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
                    float wx = pc.X - pa.X, wy = pc.Y - pa.Y, wz = pc.Z - pa.Z;
                    var face = Normalize(new Vec3(uy * wz - uz * wy, uz * wx - ux * wz, ux * wy - uy * wx));
                    if (face is { } fn)
                    {
                        float agree = fn.X * (nNorm[na].X + nNorm[nb].X + nNorm[nc2].X)
                                    + fn.Y * (nNorm[na].Y + nNorm[nb].Y + nNorm[nc2].Y)
                                    + fn.Z * (nNorm[na].Z + nNorm[nb].Z + nNorm[nc2].Z);
                        if (agree < 0) fn = new Vec3(-fn.X, -fn.Y, -fn.Z);
                        skinTris.Add((pa, pb, pc, fn));
                    }
                }
                foreach (var (p, q) in new[] { (na, nb), (nb, nc2), (nc2, na) })
                {
                    var key = p < q ? (p, q) : (q, p);
                    edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
                }
            }

            // The toenails, and anything else of this body sitting inside the capped stretch. Added here,
            // BEFORE the per-vertex shortlists below are built, so they are candidates like any other skin
            // triangle â€” an earlier attempt appended them afterwards and every shortlist was already full
            // of flesh, so nothing ever tested against a nail and the numbers did not move.
            //
            // Their outward side is the direction away from the cap's own sweep axis, which is right for
            // something lying ON the surface the cap encloses. They take no part in the rim: the cap is
            // sewn to the mesh it was cut from, not to these.
            // The swallowed islands are geometry the cap has to close OVER, not through. They sit proud
            // of the flesh, so without this the cap passes underneath them and the player's own toenails
            // come through the fabric â€” 649 cap vertices inside a nail on the equipped body, worst 0.027.
            // Oriented by their own source normals, which are to hand here because they are the same mesh.
            int islandObs = 0;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;
                int na2 = nodeOf[tris[t]], nb3 = nodeOf[tris[t + 1]], nc4 = nodeOf[tris[t + 2]];
                if (!dropNode[na2] || !dropNode[nb3] || !dropNode[nc4]) continue;
                Vec3 pa2 = start[na2], pb2 = start[nb3], pc2 = start[nc4];
                float ux3 = pb2.X - pa2.X, uy3 = pb2.Y - pa2.Y, uz3 = pb2.Z - pa2.Z;
                float wx3 = pc2.X - pa2.X, wy3 = pc2.Y - pa2.Y, wz3 = pc2.Z - pa2.Z;
                if (Normalize(new Vec3(uy3 * wz3 - uz3 * wy3, uz3 * wx3 - ux3 * wz3, ux3 * wy3 - uy3 * wx3))
                    is not { } fn3) continue;
                float agree2 = fn3.X * (nNorm[na2].X + nNorm[nb3].X + nNorm[nc4].X)
                             + fn3.Y * (nNorm[na2].Y + nNorm[nb3].Y + nNorm[nc4].Y)
                             + fn3.Z * (nNorm[na2].Z + nNorm[nb3].Z + nNorm[nc4].Z);
                if (agree2 < 0) fn3 = new Vec3(-fn3.X, -fn3.Y, -fn3.Z);
                skinTris.Add((pa2, pb2, pc2, fn3));
                islandObs++;
            }

            var rim = new Dictionary<int, List<int>>();
            foreach (var (e, uses) in edgeUse)
            {
                if (uses != 1) continue;
                (rim.TryGetValue(e.Item1, out var l1) ? l1 : rim[e.Item1] = new List<int>()).Add(e.Item2);
                (rim.TryGetValue(e.Item2, out var l2) ? l2 : rim[e.Item2] = new List<int>()).Add(e.Item1);
            }
            var loop = LongestLoop(rim);
            if (loop.Count < MinRimNodes) continue;

            // The walk gives the rim its true cyclic order; only rotate and orient it, never re-sort â€”
            // sorting by angle crosses the stitch and shreds the seam.
            OrientLoop(loop, start, Flatten);
            int rimCount = loop.Count;

            // â”€â”€ the sweep â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // Rings of the cross-section outline, marching from the rim to the tip. Each ring is sampled
            // radially off the slice's convex hull, so it bridges every toe in that slice by construction.
            var chain = new List<List<int>> { loop };
            var chainAt = new List<float> { lo };   // each ring's own axial position, domes included
            var taken = new HashSet<int>(loop);
            float edgeLen = MeanEdgeLength(start, adj, core);

            // Rings reuse vertices from the cut region, so the pool is finite: ask for more than it holds
            // and the last rings are stitched from whatever is left, dragging vertices in from across the
            // foot. Budget for slack so each slot still gets a donor that was already near it.
            // Leave room for the grid that closes the end. It spans the LAST DOME RING, not the rim: the
            // dome rings carry slots in proportion to their radius, so the opening is a fraction of the
            // rim's width and the grid across it a fraction of the rim's cost.
            float lastShrink = MathF.Cos((float)TipRings / (TipRings + 1) * MathF.PI / 2f);
            int gridSide = Math.Max(2, (int)(rimCount * lastShrink) / 4);
            int gridCost = gridSide * gridSide;
            // And a ring no longer costs the rim's worth of donors either â€” it carries slots for its own
            // perimeter, and the cross-section narrows toward the toes. Budgeting as though every ring
            // were full width is what held the ring count down and left the cells twice as long along
            // the foot as they are around it.
            int ringCost = Math.Max(1, (int)(rimCount * RingWidthEstimate));
            int affordable = (int)(core.Count * DonorBudget - gridCost) / ringCost - TipRings - 1;
            int ringCount = Math.Clamp((int)MathF.Round(span / MathF.Max(edgeLen * RingDensity, 1e-6f)),
                                       MinRings, Math.Clamp(affordable, MinRings, MaxRings));
            float ringStep = span / ringCount;

            // Where each slot sits ANGULARLY. The rim's vertices are far from evenly spaced â€” on this
            // foot they range from 0.12 to 0.46 radians apart â€” so a ring of evenly spaced slots skews
            // every strip against it and the worst ones cross, which is the overlap on top of the foot.
            // Rings therefore start on the rim's own angles and even out as they climb, by which point
            // they are far from the seam. Both sequences increase, so no blend of them can cross.
            var rimAngle = new float[rimCount];
            for (int j = 0; j < rimCount; j++)
            {
                var f = Flatten(start[loop[j]]);
                rimAngle[j] = MathF.Atan2(f.Y, f.X);
            }
            for (int j = 1; j < rimCount; j++)
            {
                float a = rimAngle[j], prev = rimAngle[j - 1];
                while (a - prev > MathF.PI) a -= MathF.Tau;
                while (a - prev < -MathF.PI) a += MathF.Tau;
                rimAngle[j] = a;
            }

            // The rim follows the painted mask edge and is nowhere near flat â€” here it juts forward over
            // a sixth of the cap's length. Rings still march up the whole span (starting them past the
            // rim's leading edge leaves one long chord that cuts under the foot and lets skin through),
            // but each rim slot WAITS at the rim until the rings have passed it. Slots therefore join the
            // sweep at different rings, which is what stops the first strip folding back on itself.
            var joinAt = new int[rimCount];
            for (int j = 0; j < rimCount; j++)
            {
                float rt = Axial(start[loop[j]]);
                joinAt[j] = ringCount + 1;
                for (int r = 1; r <= ringCount; r++)
                    if (lo + ringStep * r > rt + ringStep * 0.5f) { joinAt[j] = r; break; }
            }

            // Kept so the relax below can push a vertex back out onto the outline it belongs on.
            var ringHull = new (float X, float Y)[ringCount + 1][];
            var ringCentre = new (float X, float Y)[ringCount + 1];
            var ringAt = new float[ringCount + 1];
            var ringClear = new float[ringCount + 1];
            var ringSpans = new bool[ringCount + 1][];   // per slot: is this one bridging a gap?

            List<int>? BuildRing(int r)
            {
                float t = lo + ringStep * r;

                // Read the cross-section from the NARROWEST band that still holds enough points, widening
                // only where the geometry thins out. A fixed band drags the wider sections behind the toes
                // forward, which is what left the end of the cap blunt and standing off the tips.
                var slicePts = new List<(float X, float Y)>();
                float band = MathF.Min(ringStep, edgeLen * SliceWindow);
                for (int widen = 0; widen < BandWidenSteps; widen++)
                {
                    slicePts.Clear();
                    for (int n = 0; n < nodeCount; n++)
                        if (comp[n] == c && MathF.Abs(Axial(start[n]) - t) <= band)
                            slicePts.Add(Flatten(start[n]));
                    if (slicePts.Count >= MinSliceNodes) break;
                    band *= 1.8f;
                }
                if (slicePts.Count < MinSliceNodes) return null;

                var hull = ConvexHull(slicePts.ToArray());
                if (hull.Length < 3) return null;

                float hx = 0, hy = 0;
                foreach (var h in hull) { hx += h.X; hy += h.Y; }
                var centre = (X: hx / hull.Length, Y: hy / hull.Length);

                // Sitting exactly ON the hull leaves the cap tangent to the toes it encloses, so the skin
                // pokes through it as soon as the body deforms. Stand it off by a little, eased in from
                // the rim so the join stays flush.
                float clear = 1f + CapClearance * MathF.Min(1f, r / 2f);
                ringHull[r] = hull;
                ringCentre[r] = centre;
                ringAt[r] = t;
                ringClear[r] = clear;

                // The outline the slice points actually trace: bucket them by angle about the centre,
                // keep the farthest in each bucket, then fill the empty buckets by interpolating round
                // the circle. This is read BEFORE the slots are chosen, because how many slots the ring
                // should carry depends on how big it is.
                int bins = Math.Max(rimCount, MinOutlineBins);
                var binR = new float[bins];
                for (int b = 0; b < bins; b++) binR[b] = -1f;
                int filled = 0;
                foreach (var q in slicePts)
                {
                    float ox = q.X - centre.X, oy = q.Y - centre.Y;
                    float len = MathF.Sqrt(ox * ox + oy * oy);
                    if (len <= 1e-9f) continue;
                    float ang0 = MathF.Atan2(oy, ox);
                    int b = (int)MathF.Floor((ang0 + MathF.PI) / MathF.Tau * bins);
                    b = Math.Clamp(b, 0, bins - 1);
                    if (binR[b] < 0f) filled++;
                    binR[b] = MathF.Max(binR[b], len);
                }
                if (filled < 3) return null;

                // Circular gap fill: for each empty bucket walk out to the nearest filled one either way
                // and blend by how far each is. A run of empty buckets is a stretch the slice simply has
                // no points over, and a straight chord across it is the honest reading.
                if (filled < bins)
                {
                    var back = new int[bins];
                    var fwd = new int[bins];
                    int last = -1;
                    for (int k = 0; k < bins * 2; k++) { int b = k % bins; if (binR[b] >= 0f) last = b; if (k >= bins) back[b] = last; }
                    last = -1;
                    for (int k = bins * 2 - 1; k >= 0; k--) { int b = k % bins; if (binR[b] >= 0f) last = b; if (k < bins) fwd[b] = last; }
                    var solid = (float[])binR.Clone();
                    for (int b = 0; b < bins; b++)
                    {
                        if (binR[b] >= 0f) continue;
                        int lb = back[b], rb = fwd[b];
                        int dl = (b - lb + bins) % bins, dr = (rb - b + bins) % bins;
                        solid[b] = dl + dr == 0 ? binR[lb] : binR[lb] + (binR[rb] - binR[lb]) * ((float)dl / (dl + dr));
                    }
                    binR = solid;
                }

                float OutlineRadius(float ang)
                {
                    float f2 = (ang + MathF.PI) / MathF.Tau * bins - 0.5f;
                    int b0 = (int)MathF.Floor(f2);
                    float w2 = f2 - b0;
                    return binR[((b0 % bins) + bins) % bins] * (1f - w2)
                         + binR[(((b0 + 1) % bins) + bins) % bins] * w2;
                }

                // HOW MANY SLOTS THIS RING CARRIES. Its own perimeter, at the mesh's own edge length.
                // Every ring used to carry the rim's count whatever its size, and the cross-section
                // narrows toward the toes, so the slots bunched â€” measured at about 60% of an edge
                // apart around the cap while the rings sat 110% apart along it. Cells came out twice as
                // long as they were wide, and where two slots landed on top of each other the strip
                // between them was a sliver. It also wasted the donor pool, which is what limited how
                // many rings the cap could afford in the first place.
                //
                // The FIRST ring keeps the rim's count: each rim slot waits at the rim until the sweep
                // passes it (joinAt), and that staircase only means anything while ring slot j is rim
                // slot j.
                float perim = 0f;
                {
                    var prevP = (X: 0f, Y: 0f);
                    bool first = true;
                    var firstP = (X: 0f, Y: 0f);
                    for (int b = 0; b <= bins; b++)
                    {
                        float ang0 = -MathF.PI + MathF.Tau * (b % bins) / bins;
                        float rr0 = binR[b % bins];
                        var pt = (X: MathF.Cos(ang0) * rr0, Y: MathF.Sin(ang0) * rr0);
                        if (first) { firstP = pt; first = false; }
                        else perim += MathF.Sqrt((pt.X - prevP.X) * (pt.X - prevP.X) + (pt.Y - prevP.Y) * (pt.Y - prevP.Y));
                        prevP = pt;
                    }
                    perim += MathF.Sqrt((firstP.X - prevP.X) * (firstP.X - prevP.X) + (firstP.Y - prevP.Y) * (firstP.Y - prevP.Y));
                }

                int width = rimCount;
                if (r > 1)
                {
                    width = (int)MathF.Round(perim / MathF.Max(edgeLen, 1e-6f));
                    width = Math.Clamp(width, Math.Min(MinRingSlots, rimCount), rimCount);
                }
                bool evenSlots = width != rimCount;

                // Which slots are BRIDGING and which are simply lying on a toe. A bridging slot's ray
                // crosses empty space to reach the outline, so the outline sits well beyond anything
                // actually there; a slot on a toe meets the surface right where the outline is. The relax
                // below leans on this: a vertex on a toe may settle inward and even the surface out, but
                // one spanning a gap has nothing under it and would simply fall into the crevice.
                var dirs = new (float X, float Y)[width];
                var hullRad = new float[width];
                var reach = new float[width];
                var ang = new float[width];
                for (int j = 0; j < width; j++)
                    ang[j] = evenSlots
                        ? rimAngle[0] + MathF.Tau * j / width          // its own even spacing
                        : rimAngle[j] + (rimAngle[0] + MathF.Tau * j / rimCount - rimAngle[j]) * ((float)r / ringCount);

                for (int j = 0; j < width; j++)
                {
                    dirs[j] = (MathF.Cos(ang[j]), MathF.Sin(ang[j]));
                    hullRad[j] = HullRadius(hull, centre, dirs[j].X, dirs[j].Y);
                    reach[j] = OutlineRadius(ang[j]);
                }

                // Sitting on the hull is right only where the ray crosses empty space. Over the crown of a
                // toe the hull is a CHORD strung to the next toe's outer corner, and placing the slot on it
                // floats the cap above the toe by that chord's sagitta â€” further over the second toe, whose
                // chord is longer. So blend from where the skin actually is toward the hull, by how much
                // space the ray crosses.
                float bridgeAt = edgeLen * BridgeSpan;
                var fill = new float[width];
                var spans = new bool[width];
                for (int j = 0; j < width; j++)
                {
                    // Nothing known to be under this slot: it bridges. Without the guard reach is 0 and
                    // the slot collapses to the section's centre.
                    if (reach[j] <= 0f) { fill[j] = 1f; spans[j] = true; continue; }
                    float u = Math.Clamp((hullRad[j] - reach[j]) / bridgeAt, 0f, 1f);
                    fill[j] = u * u * (3f - 2f * u);   // smoothstep: a hard cut steps where a toe ends
                    spans[j] = fill[j] > 0.5f;
                }
                ringSpans[r] = spans;

                // Donors claimed here are released again if the ring turns out unusable. Left claimed,
                // they keep the position the abandoned ring gave them while belonging to no ring at all â€”
                // so nothing relaxes them and nothing checks them against the skin.
                var claimed = new List<int>(width);
                void Abandon()
                {
                    foreach (int taken2 in claimed)
                    {
                        taken.Remove(taken2);
                        hasTarget[taken2] = false;
                        target[taken2] = default;
                    }
                }

                var ring = new List<int>(width);
                for (int j = 0; j < width; j++)
                {
                    // Only while this ring is still slot-for-slot with the rim does waiting mean anything.
                    if (!evenSlots && r < joinAt[j]) { ring.Add(-1); continue; }

                    float dx = dirs[j].X, dy = dirs[j].Y;
                    float rad = (reach[j] + (hullRad[j] - reach[j]) * fill[j]) * clear;
                    float qx = centre.X + dx * rad, qy = centre.Y + dy * rad;
                    var p = new Vec3(
                        mid.X + axis.Value.X * t + eu.X * qx + ev.X * qy,
                        mid.Y + axis.Value.Y * t + eu.Y * qx + ev.Y * qy,
                        mid.Z + axis.Value.Z * t + eu.Z * qx + ev.Z * qy);

                    // Reuse a vertex already in the region rather than creating one: it keeps its own
                    // blend weights and its place in the submesh's bone window, so the cap skins and
                    // draws with no new vertex data to author.
                    int donor = NearestFree(core, start, taken, p);
                    if (donor < 0) { Abandon(); return null; }   // pool exhausted; the caller stops here
                    taken.Add(donor);
                    claimed.Add(donor);
                    target[donor] = new Vec3(p.X - start[donor].X, p.Y - start[donor].Y, p.Z - start[donor].Z);
                    hasTarget[donor] = true;
                    nodeFill[donor] = fill[j];
                    fromRing[donor] = r; fromSlot[donor] = j;
                    ring.Add(donor);
                }
                if (ring.Count == width) return ring;
                Abandon();
                return null;
            }

            // Claim the LAST ring first. It is the one that decides whether the toe tips are enclosed, and
            // if it is the ring that gets lost â€” to a thin cross-section or an exhausted vertex pool â€” the
            // cap ends on a cone that runs straight through the tips of the middle toes.
            var tipRing = BuildRing(ringCount);
            for (int r = 1; r < ringCount; r++)
            {
                var ring = BuildRing(r);
                if (ring == null) break;
                chain.Add(ring);
                chainAt.Add(lo + ringStep * r);
            }
            if (tipRing != null) { chain.Add(tipRing); chainAt.Add(lo + ringStep * ringCount); }
            if (chain.Count < 2) continue;

            float domeTop = float.NaN;   // where the rounded end finishes, for the patch that closes it

            // Round the end off over a few shrinking rings before closing it. Fanning the full-width last
            // ring straight to a point makes a pole: rimCount long, thin triangles all meeting at one
            // vertex, which is poor topology and shades badly in game. Each extra ring follows a quarter
            // circle, so the tip comes to a dome and the closing fan is small and even.
            {
                int lastFull = chain.Count - 1;
                int rr = Math.Clamp(lastFull, 1, ringCount);
                var lastCentre = ringCentre[rr];
                float lastT = ringAt[rr];
                float domeReach;

                // Rings that curve the end over, each narrower and further along than the last, following a
                // quarter circle so the cap finishes as a dome rather than a stump.
                //
                // Each ring carries slots in proportion to its own PERIMETER, so the spacing round it
                // stays at the mesh's own edge length. Keeping the full count instead packs them together
                // as the radius falls away â€” the last of them sits at 38% of the radius, so its slots end
                // up a third of their spacing apart, and the grid closing the end inherits that. Measured
                // on the equipped body, the last 0.002 of the cap carried 439 faces whose edges were a
                // sixth of the mesh's own: the clump of vertices at the toes.
                //
                // The height of that dome is a fraction of the CAP'S OWN RADIUS, not of the ring spacing:
                // scaled to the spacing it comes to about a tenth of what the shape needs, and the end
                // reads as squared off.
                float endRadius = 0;
                {
                    int counted = 0;
                    foreach (int v in chain[lastFull])
                    {
                        if (v < 0) continue;
                        var f = Flatten(new Vec3(
                            start[v].X + target[v].X, start[v].Y + target[v].Y, start[v].Z + target[v].Z));
                        endRadius += MathF.Sqrt((f.X - lastCentre.X) * (f.X - lastCentre.X)
                                              + (f.Y - lastCentre.Y) * (f.Y - lastCentre.Y));
                        counted++;
                    }
                    if (counted > 0) endRadius /= counted;
                }
                domeReach = endRadius * TipRound;
                domeTop = lastT + domeReach;      // the crown of the quarter circle the rings follow

                // The last full ring's outline as radius against angle, so a dome ring with a different
                // number of slots can be sampled from the SHAPE. Decimating it instead â€” taking every
                // n-th vertex â€” keeps whichever bumps happen to fall on the surviving slots and drops
                // the rest, and the ring stops being round.
                var prof = new List<(float A, float R)>(rimCount);
                float perimeter = 0;
                {
                    (float X, float Y)? first = null, prev = null;
                    foreach (int v in chain[lastFull])
                    {
                        if (v < 0) continue;
                        var f = Flatten(Placed(v));
                        float ox = f.X - lastCentre.X, oy = f.Y - lastCentre.Y;
                        float rad2 = MathF.Sqrt(ox * ox + oy * oy);
                        if (rad2 > 1e-9f) prof.Add((MathF.Atan2(oy, ox), rad2));
                        if (prev is { } pv)
                            perimeter += MathF.Sqrt((f.X - pv.X) * (f.X - pv.X) + (f.Y - pv.Y) * (f.Y - pv.Y));
                        else first = f;
                        prev = f;
                    }
                    if (first is { } fs && prev is { } lv)
                        perimeter += MathF.Sqrt((fs.X - lv.X) * (fs.X - lv.X) + (fs.Y - lv.Y) * (fs.Y - lv.Y));
                }
                if (prof.Count < 3) prof.Clear();
                prof.Sort((u, v) => u.A.CompareTo(v.A));

                float OutlineAt(float ang)
                {
                    if (prof.Count == 0) return endRadius;
                    while (ang < prof[0].A) ang += MathF.Tau;
                    while (ang > prof[0].A + MathF.Tau) ang -= MathF.Tau;
                    for (int q = 0; q < prof.Count; q++)
                    {
                        var (a0, r0) = prof[q];
                        var (a1, r1) = q + 1 < prof.Count ? prof[q + 1] : (prof[0].A + MathF.Tau, prof[0].R);
                        if (ang >= a0 && ang <= a1)
                            return a1 - a0 <= 1e-9f ? r0 : r0 + (r1 - r0) * ((ang - a0) / (a1 - a0));
                    }
                    return prof[^1].R;
                }

                float phase = prof.Count > 0 ? prof[0].A : 0f;
                int prevWidth = chain[lastFull].Count;

                for (int k = 1; k <= TipRings; k++)
                {
                    float frac = (float)k / (TipRings + 1);
                    float shrink = MathF.Cos(frac * MathF.PI / 2f);
                    float along = lastT + domeReach * MathF.Sin(frac * MathF.PI / 2f);

                    // Slots for THIS ring's perimeter, at the mesh's own edge length. Even, because the
                    // grid closing the end needs an even loop; never wider than the ring before it, or
                    // the strip between them folds.
                    int width2 = (int)MathF.Round(perimeter * shrink / MathF.Max(edgeLen, 1e-6f));
                    width2 = Math.Min(width2, prevWidth);
                    width2 -= width2 & 1;
                    if (width2 < MinDomeSlots) break;      // too narrow to be a ring; the patch closes it

                    var dome = new List<int>(width2);
                    var claimed = new List<int>(width2);
                    for (int j = 0; j < width2; j++)
                    {
                        float ang = phase + MathF.Tau * j / width2;
                        float rad2 = OutlineAt(ang) * shrink;
                        float qx = lastCentre.X + MathF.Cos(ang) * rad2;
                        float qy = lastCentre.Y + MathF.Sin(ang) * rad2;
                        var p = new Vec3(
                            mid.X + axis.Value.X * along + eu.X * qx + ev.X * qy,
                            mid.Y + axis.Value.Y * along + eu.Y * qx + ev.Y * qy,
                            mid.Z + axis.Value.Z * along + eu.Z * qx + ev.Z * qy);

                        int donor = NearestFree(core, start, taken, p);
                        if (donor < 0) break;
                        taken.Add(donor);
                        claimed.Add(donor);
                        target[donor] = new Vec3(p.X - start[donor].X, p.Y - start[donor].Y, p.Z - start[donor].Z);
                        hasTarget[donor] = true;
                        // Whether a slot bridges is a property of the DIRECTION, so take it from the slot
                        // of the full ring pointing the same way.
                        int near = chain[lastFull][Math.Clamp(
                            (int)MathF.Round((float)j * prevWidth / width2), 0, prevWidth - 1)];
                        nodeFill[donor] = near >= 0 ? nodeFill[near] : 1f;
                        fromRing[donor] = 1000 + k; fromSlot[donor] = j;
                        dome.Add(donor);
                    }

                    if (dome.Count != width2)
                    {
                        foreach (int c2 in claimed) { taken.Remove(c2); hasTarget[c2] = false; target[c2] = default; }
                        break;
                    }
                    prevWidth = width2;
                    chain.Add(dome);
                    chainAt.Add(along);
                }
            }

            // A slot that has not joined yet is still the rim vertex, so a strip crossing the join is a
            // triangle rather than a quad and the degenerate halves fall away below.
            int At(int level, int j)
            {
                if (level == 0) return loop[j];
                int v = chain[level][j];
                return v >= 0 ? v : loop[j];
            }

            Vec3 Placed(int n) => new(start[n].X + target[n].X, start[n].Y + target[n].Y, start[n].Z + target[n].Z);

            // â”€â”€ the relax â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // The rings inherit the rim's spacing, which is uneven enough that a few quads come out
            // twisted however they are split. So even the cap out the way you would by hand: pull each
            // vertex toward the average of its four neighbours in the ring grid, then push it back out
            // onto its cross-section's outline so the relax can only slide it ALONG the cap, never let it
            // sink onto the toes underneath. The rim and the tip are pinned, so the seam does not move.
            // Nearest bit of skin under each cap vertex, found once. The relax is bounded by THIS rather
            // than by the cross-section outline: holding every vertex out on the hull is what stops the
            // surface evening out, and a hand pass over this cap moves more than half its vertices, over
            // a third of them inward. What actually has to hold is clearance over the skin, not the hull.
            var anchor = new Vec3[nodeCount];
            for (int r = 1; r < chain.Count; r++)
                foreach (int n in chain[r])
                {
                    if (n < 0) continue;
                    var p = Placed(n);
                    float bestD = float.MaxValue;
                    foreach (int m in core)
                    {
                        float dx = start[m].X - p.X, dy = start[m].Y - p.Y, dz = start[m].Z - p.Z;
                        float d = dx * dx + dy * dy + dz * dz;
                        if (d < bestD) { bestD = d; anchor[n] = start[m]; }
                    }
                }
            float minClear = edgeLen * SkinClearance;

            // A shortlist of the skin under each cap vertex, so the clearance rule can be enforced on
            // EVERY relax pass instead of once at the end. Snapping vertices onto the surface after the
            // fact leaves its own creases; letting them settle against the constraint does not.
            var nearSkin = new Dictionary<int, int[]>();
            if (skinTris.Count > 0)
                for (int r = 1; r < chain.Count; r++)
                    foreach (int n in chain[r])
                    {
                        if (n < 0 || nearSkin.ContainsKey(n)) continue;
                        var p = Placed(n);
                        var order = new (float D, int I)[skinTris.Count];
                        for (int t = 0; t < skinTris.Count; t++)
                        {
                            var (ta, tb, tc, _) = skinTris[t];
                            float cx2 = (ta.X + tb.X + tc.X) / 3f - p.X;
                            float cy2 = (ta.Y + tb.Y + tc.Y) / 3f - p.Y;
                            float cz2 = (ta.Z + tb.Z + tc.Z) / 3f - p.Z;
                            order[t] = (cx2 * cx2 + cy2 * cy2 + cz2 * cz2, t);
                        }
                        Array.Sort(order, (x, y) => x.D.CompareTo(y.D));
                        int take = Math.Min(SkinCandidates, order.Length);
                        var pick = new int[take];
                        for (int k = 0; k < take; k++) pick[k] = order[k].I;
                        nearSkin[n] = pick;
                    }

            // Signed distance out of the body, over that vertex's shortlist.
            float Clearance(int n, Vec3 p, out Vec3 onSkin, out Vec3 outward)
            {
                onSkin = default; outward = default;
                if (!nearSkin.TryGetValue(n, out var cand)) return float.MaxValue;
                float bestD = float.MaxValue;
                foreach (int t in cand)
                {
                    var (ta, tb, tc, to) = skinTris[t];
                    var q = ClosestOnTriangle(p, ta, tb, tc);
                    float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d < bestD) { bestD = d; onSkin = q; outward = to; }
                }
                if (bestD == float.MaxValue) return float.MaxValue;
                return (p.X - onSkin.X) * outward.X + (p.Y - onSkin.Y) * outward.Y + (p.Z - onSkin.Z) * outward.Z;
            }

            for (int pass = 0; pass < RelaxPasses; pass++)
            {
                var moved = new List<(int Node, Vec3 To)>();
                // Every ring relaxes, including the last: it cannot drift backwards because the step below
                // holds each vertex in its own ring's plane.
                for (int r = 1; r < chain.Count; r++)
                    for (int j = 0; j < chain[r].Count; j++)
                    {
                        int n = chain[r][j];
                        if (n < 0) continue;
                        int width = chain[r].Count;

                        float sx = 0, sy = 0, sz = 0;
                        int count = 0;
                        void Gather(int m)
                        {
                            if (m < 0) return;
                            var q = Placed(m);
                            sx += q.X; sy += q.Y; sz += q.Z; count++;
                        }
                        // Neighbours along the ring always exist; the ones fore and aft only when those
                        // rings carry the same number of slots, which the dome rings deliberately do not.
                        if (r - 1 == 0 || chain[r - 1].Count == width) Gather(At(r - 1, j));
                        if (r + 1 < chain.Count && chain[r + 1].Count == width) Gather(chain[r + 1][j]);
                        Gather(chain[r][(j + 1) % width]);
                        Gather(chain[r][(j - 1 + width) % width]);
                        if (count == 0) continue;

                        var p = Placed(n);
                        float nx = p.X + (sx / count - p.X) * RelaxRate;
                        float ny = p.Y + (sy / count - p.Y) * RelaxRate;
                        float nz = p.Z + (sz / count - p.Z) * RelaxRate;

                        // Hold the vertex in its own ring's plane â€” a hand pass moves these more than
                        // three times as far across the section as along the foot, and letting them drift
                        // axially walks the last rings back off the toe tips.
                        // Held in its own ring's plane, but otherwise free to settle wherever the
                        // smoothing takes it â€” including inward, and including down into a toe gap. The
                        // only thing that must hold is clearance over the skin, and that is enforced
                        // exactly, against the skin's own triangles, once the relax has finished.
                        var rel = new Vec3(nx, ny, nz);
                        int rr = Math.Clamp(r, 1, ringCount);
                        if (ringHull[rr] != null)
                        {
                            var f2 = Flatten(rel);
                            // Its OWN ring's plane. Clamping to the last full ring's instead flattens
                            // every dome ring back onto it, which folds the rounded end inside out.
                            //
                            // Held only PARTLY. Clamped hard the relax converges lumpy â€” measured at 22%
                            // of an edge and unchanged by four times the passes â€” because a bump along a
                            // ring can only be smoothed across the section, never along the foot. A hand
                            // relax over the same region reaches 6% because it moves in 3D.
                            float t2 = chainAt[r];
                            var flat = new Vec3(
                                mid.X + axis.Value.X * t2 + eu.X * f2.X + ev.X * f2.Y,
                                mid.Y + axis.Value.Y * t2 + eu.Y * f2.X + ev.Y * f2.Y,
                                mid.Z + axis.Value.Z * t2 + eu.Z * f2.X + ev.Z * f2.Y);
                            rel = new Vec3(rel.X + (flat.X - rel.X) * RingPlaneHold,
                                           rel.Y + (flat.Y - rel.Y) * RingPlaneHold,
                                           rel.Z + (flat.Z - rel.Z) * RingPlaneHold);
                        }

                        // Free to settle inward â€” down into a toe gap is fine, and reads better than a
                        // flat bridge â€” but never through the skin.
                        float side = Clearance(n, rel, out var onSkin, out var outward);
                        if (side < minClear && side != float.MaxValue)
                            rel = new Vec3(
                                onSkin.X + outward.X * minClear,
                                onSkin.Y + outward.Y * minClear,
                                onSkin.Z + outward.Z * minClear);
                        // ...and never floating far above it either. Smoothing a surface removes its
                        // concavities, so a vertex sitting down on a toe is lifted toward its neighbours
                        // out over the gaps either side â€” which is what stood the fabric off the big and
                        // second toes. Capped here rather than after the relax so the passes that follow
                        // even the spacing out again under the constraint; capping it at the end only
                        // leaves slivers where the cap is tightest.
                        else if (side != float.MaxValue && nodeFill[n] < BridgeExempt)
                        {
                            float allow = edgeLen * MaxStandoff / (1f - nodeFill[n]);
                            if (side > allow)
                                rel = new Vec3(
                                    rel.X - outward.X * (side - allow),
                                    rel.Y - outward.Y * (side - allow),
                                    rel.Z - outward.Z * (side - allow));
                        }
                        // ...and never on top of the slot beside it. Smoothing pulls neighbours together
                        // as readily as it evens them out, and where a ring dips into the valley between
                        // two toes it can close a pair to almost nothing â€” the worst face in the cap came
                        // from one, at 2% of an edge. Turning the relax off avoids it and costs far more
                        // elsewhere (faces past aspect 6 go from 15 to 70), so hold the spacing instead.
                        float keep = edgeLen * SlotMinGap;
                        for (int nbSide = 0; nbSide < 2; nbSide++)
                        {
                            int nbIdx = chain[r][(j + (nbSide == 0 ? 1 : width - 1)) % width];
                            if (nbIdx < 0 || nbIdx == n) continue;
                            var np2 = Placed(nbIdx);
                            float ddx = rel.X - np2.X, ddy = rel.Y - np2.Y, ddz = rel.Z - np2.Z;
                            float dist = MathF.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                            if (dist >= keep || dist <= 1e-9f) continue;
                            float grow = (keep - dist) / dist;
                            rel = new Vec3(rel.X + ddx * grow, rel.Y + ddy * grow, rel.Z + ddz * grow);
                        }

                        moved.Add((n, rel));
                    }

                foreach (var (n, to) in moved)
                    target[n] = new Vec3(to.X - start[n].X, to.Y - start[n].Y, to.Z - start[n].Z);
            }

            // â”€â”€ keep it off the skin â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // The relax already holds this against each vertex's shortlist; this last sweep checks the
            // whole surface, in case settling carried a vertex over some triangle that was not on its
            // list. Measured against TRIANGLES, not vertices â€” a vertex-only test lets the cap sink
            // through the middle of a face and call it clear.
            void PushOffSkin(int n)
            {
                if (skinTris.Count == 0 || n < 0 || !hasTarget[n]) return;
                var p = Placed(n);

                Vec3 best = default, bestOut = default;
                float bestD = float.MaxValue;
                foreach (var (ta, tb, tc, to) in skinTris)
                {
                    var q = ClosestOnTriangle(p, ta, tb, tc);
                    float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d < bestD) { bestD = d; best = q; bestOut = to; }
                }
                if (bestD == float.MaxValue) return;

                // SIGNED, against the surface's own outward direction. Measuring plain distance and
                // shoving along (p - closest) drives a vertex that has ended up UNDER the skin further
                // under it, since that direction points into the body.
                float want = edgeLen * SkinClearance;
                float side = (p.X - best.X) * bestOut.X + (p.Y - best.Y) * bestOut.Y + (p.Z - best.Z) * bestOut.Z;
                if (side >= want) return;

                target[n] = new Vec3(
                    best.X + bestOut.X * want - start[n].X,
                    best.Y + bestOut.Y * want - start[n].Y,
                    best.Z + bestOut.Z * want - start[n].Z);
            }

            for (int r = 1; r < chain.Count; r++)
                foreach (int n in chain[r])
                    PushOffSkin(n);

            // Which way is "out" here. Measured against the SOURCE normals of the vertices involved, not
            // against a radial from the sweep axis: radial is fine around the sides but meaningless at the
            // end of the cap, where the surface faces along the axis rather than away from it.
            float FacesOut(int i0, int i1, int i2)
            {
                if (i0 == i1 || i1 == i2 || i0 == i2) return float.MaxValue;   // degenerate: dropped anyway
                Vec3 p0 = Placed(i0), p1 = Placed(i1), p2 = Placed(i2);
                float ux = p1.X - p0.X, uy = p1.Y - p0.Y, uz = p1.Z - p0.Z;
                float wx = p2.X - p0.X, wy = p2.Y - p0.Y, wz = p2.Z - p0.Z;
                var nrmF = Normalize(new Vec3(uy * wz - uz * wy, uz * wx - ux * wz, ux * wy - uy * wx));
                if (nrmF is null) return float.MaxValue;

                var outward = Normalize(new Vec3(
                    nNorm[i0].X + nNorm[i1].X + nNorm[i2].X,
                    nNorm[i0].Y + nNorm[i1].Y + nNorm[i2].Y,
                    nNorm[i0].Z + nNorm[i1].Z + nNorm[i2].Z));
                if (outward is null) return float.MaxValue;
                return nrmF.Value.X * outward.Value.X + nrmF.Value.Y * outward.Value.Y + nrmF.Value.Z * outward.Value.Z;
            }

            // Winding comes from the ring order and stays consistent â€” never flipped per triangle. The
            // vertex normals are averaged FROM these faces, so flipping one to satisfy a normal test just
            // corrupts the normal it was tested against, and the surface ends up worse than it started.
            void Emit(int i0, int i1, int i2)
            {
                // Tested on the VERTICES, after merging, not on the nodes going in: two nodes welded
                // together are still different nodes, and a triangle spanning them is degenerate all the
                // same. Checking before the merge lets exactly those through.
                ushort a = Rep(i0), b = Rep(i1), c = Rep(i2);
                if (a == b || b == c || a == c) return;
                newTris.Add((a, b, c));
            }

            // Split the quad on whichever diagonal keeps BOTH halves facing out. Where a joined slot sits
            // beside one still waiting at the rim the quad is a bowtie, and one diagonal folds it back
            // through the cap â€” those are the slivers left poking out of the seam.
            void EmitQuad(int a0, int a1, int b1, int b0)
            {
                float d1 = MathF.Min(FacesOut(a0, a1, b1), FacesOut(a0, b1, b0));
                float d2 = MathF.Min(FacesOut(a0, a1, b0), FacesOut(a1, b1, b0));
                if (d1 >= d2) { Emit(a0, a1, b1); Emit(a0, b1, b0); }
                else          { Emit(a0, a1, b0); Emit(a1, b1, b0); }
            }

            // â”€â”€ the stitch â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            for (int r = 0; r + 1 < chain.Count; r++)
            {
                int outer = r == 0 ? rimCount : chain[r].Count;
                int inner = chain[r + 1].Count;

                if (outer == inner)
                {
                    for (int j = 0; j < outer; j++)
                    {
                        int k = (j + 1) % outer;
                        EmitQuad(At(r, j), At(r, k), At(r + 1, k), At(r + 1, j));
                    }
                }
                else
                {
                    // Rings of different lengths, paired BY INDEX RATIO. Both rings are laid out in the
                    // same angular order and start from the same phase, so outer slot i belongs against
                    // inner slot round(i * inner / outer): a quad wherever the inner index holds, a
                    // triangle wherever it steps on. The reduction comes out evenly spread by
                    // construction, whatever the vertices themselves are doing.
                    //
                    // The previous pairing walked both loops in order of BEARING about the section
                    // centre. Bearings bunch precisely where the ring bunches, so it handed a long run of
                    // outer vertices to one inner vertex and fanned it â€” max valence 28 the one time ring
                    // counts were reduced, which is what made that attempt look unworkable.
                    int Inner(int i) => (int)MathF.Round((float)i * inner / outer) % inner;
                    for (int i = 0; i < outer; i++)
                    {
                        int o0 = At(r, i), o1 = At(r, (i + 1) % outer);
                        int b0 = Inner(i), b1 = Inner(i + 1);
                        if (b0 == b1)
                        {
                            Emit(o0, o1, chain[r + 1][b0]);
                        }
                        else
                        {
                            // The inner ring steps on here: one triangle to carry the outer edge, then a
                            // fan across however many inner slots this outer edge spans (normally one).
                            Emit(o0, o1, chain[r + 1][b1]);
                            for (int k = b0; k != b1; k = (k + 1) % inner)
                                Emit(o0, chain[r + 1][(k + 1) % inner], chain[r + 1][k]);
                        }
                    }
                }
            }

            // â”€â”€ smooth where the cap meets the foot â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // The rings nearest the rim take their slot ANGLES from the rim's own, and only reach even
            // spacing at the far end of the cap (the blend in BuildRing runs on r/ringCount). The cut
            // boundary follows mesh edges diagonally, so it is denser and less even than the mesh â€” and
            // the first rings inherit that, leaving pinched cells over the top of the toes where they
            // join the foot: faces with a short edge a fifth of the mesh's own.
            //
            // The main relax ran before the stitch and holds every vertex in its own ring's plane. This
            // is the pass a modeller would make by hand instead: relax the join, in place, over the few
            // rings either side of it, against the triangles actually emitted. The rim itself never
            // moves â€” it is shared with the untouched shell, and moving it tears the seam.
            {
                var joinAdj = new Dictionary<int, List<int>>();
                var joinSeen = new HashSet<(int, int)>();
                var joinSet = new HashSet<int>();
                var joinMove = new HashSet<int>();
                for (int r = 1; r < chain.Count && r <= RimRelaxRings; r++)
                    foreach (int n in chain[r])
                        if (n >= 0 && hasTarget[n]) { joinSet.Add(n); joinMove.Add(n); }
                // The rim and the ring beyond the band are the fixed edges this smooths between.
                foreach (int v in loop) joinSet.Add(v);
                if (RimRelaxRings + 1 < chain.Count)
                    foreach (int n in chain[RimRelaxRings + 1]) if (n >= 0) joinSet.Add(n);

                void JoinLink(int a, int b)
                {
                    if (a < 0 || b < 0 || a == b) return;
                    if (!joinSet.Contains(a) || !joinSet.Contains(b)) return;
                    if (!joinSeen.Add(a < b ? (a, b) : (b, a))) return;
                    (joinAdj.TryGetValue(a, out var la) ? la : joinAdj[a] = new List<int>()).Add(b);
                    (joinAdj.TryGetValue(b, out var lb) ? lb : joinAdj[b] = new List<int>()).Add(a);
                }
                foreach (var (ta, tb, tc) in newTris)
                {
                    int na = nodeOf[ta], nb2 = nodeOf[tb], nc3 = nodeOf[tc];
                    JoinLink(na, nb2); JoinLink(nb2, nc3); JoinLink(nc3, na);
                }

                for (int pass = 0; pass < RelaxPasses; pass++)
                {
                    var moved2 = new List<(int Node, Vec3 To)>();
                    foreach (int n in joinMove)
                    {
                        if (!joinAdj.TryGetValue(n, out var nb) || nb.Count == 0) continue;
                        float sx = 0, sy = 0, sz = 0;
                        foreach (int k in nb) { var q = Placed(k); sx += q.X; sy += q.Y; sz += q.Z; }
                        var p2 = Placed(n);
                        moved2.Add((n, new Vec3(
                            p2.X + (sx / nb.Count - p2.X) * RelaxRate,
                            p2.Y + (sy / nb.Count - p2.Y) * RelaxRate,
                            p2.Z + (sz / nb.Count - p2.Z) * RelaxRate)));
                    }
                    foreach (var (n, to) in moved2)
                        target[n] = new Vec3(to.X - start[n].X, to.Y - start[n].Y, to.Z - start[n].Z);
                    foreach (int n in joinMove) PushOffSkin(n);
                }
            }

            // â”€â”€ close the end with a grid â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // Not a fan to a single apex: that makes a pole, where every vertex of the last ring meets at
            // one point, and it shades badly however carefully the triangles are shaped. Instead the
            // opening is filled the way a modeller would â€” an even quad grid spanning it, four sides
            // taken off the ring and the inside interpolated (a Coons patch), then domed so the end
            // rounds off. No vertex ends up with more than the ordinary handful of faces.
            int last = chain.Count - 1;
            var rim2 = new List<int>();
            for (int j = 0; j < chain[last].Count; j++)
            {
                int v = At(last, j);
                if (rim2.Count == 0 || v != rim2[^1]) rim2.Add(v);
            }
            if (rim2.Count >= 8 && rim2.Count % 2 == 0)
            {
                int n2 = rim2.Count;
                int sideA = n2 / 4, sideB = n2 / 2 - sideA;      // the loop as four sides: a, b, a, b

                (float U, float V) Flat2(int v) => Flatten(Placed(v));
                int Ring(int t) => rim2[((t % n2) + n2) % n2];

                // Corner-to-corner walk: grid[i,j], i across side A, j across side B.
                var gridV = new int[sideA + 1, sideB + 1];
                for (int i = 0; i <= sideA; i++) gridV[i, 0] = Ring(i);
                for (int j = 0; j <= sideB; j++) gridV[sideA, j] = Ring(sideA + j);
                for (int i = 0; i <= sideA; i++) gridV[sideA - i, sideB] = Ring(sideA + sideB + i);
                for (int j = 0; j <= sideB; j++) gridV[0, sideB - j] = Ring(2 * sideA + sideB + j);

                var p00 = Flat2(gridV[0, 0]); var p10 = Flat2(gridV[sideA, 0]);
                var p01 = Flat2(gridV[0, sideB]); var p11 = Flat2(gridV[sideA, sideB]);
                // The patch sits on the LAST ring the cap actually has â€” which is a dome ring, not the last
                // full-width one. Clamping to the full rings puts it back behind the dome that was just
                // built, and the end caves in: a crater sunk into the big toe instead of a rounded tip.
                float domeAt = chainAt[last];
                float domeUp = float.IsNaN(domeTop)
                    ? MathF.Max(ringStep, edgeLen) * TipRound
                    : MathF.Max(domeTop - domeAt, 0f);

                bool ok = true;
                for (int i = 1; i < sideA && ok; i++)
                    for (int j = 1; j < sideB && ok; j++)
                    {
                        float u = (float)i / sideA, v2 = (float)j / sideB;
                        var a0 = Flat2(gridV[i, 0]); var a1 = Flat2(gridV[i, sideB]);
                        var b0 = Flat2(gridV[0, j]); var b1 = Flat2(gridV[sideA, j]);

                        // Coons: the two rulings, less the bilinear corner sheet they share.
                        float qx = (1 - v2) * a0.U + v2 * a1.U + (1 - u) * b0.U + u * b1.U
                                 - ((1 - u) * (1 - v2) * p00.U + u * (1 - v2) * p10.U
                                  + (1 - u) * v2 * p01.U + u * v2 * p11.U);
                        float qy = (1 - v2) * a0.V + v2 * a1.V + (1 - u) * b0.V + u * b1.V
                                 - ((1 - u) * (1 - v2) * p00.V + u * (1 - v2) * p10.V
                                  + (1 - u) * v2 * p01.V + u * v2 * p11.V);

                        // Lift it into a dome: zero at the edges, most in the middle.
                        float lift = domeUp * MathF.Sin(u * MathF.PI) * MathF.Sin(v2 * MathF.PI);
                        float t4 = domeAt + lift;
                        var p = new Vec3(
                            mid.X + axis.Value.X * t4 + eu.X * qx + ev.X * qy,
                            mid.Y + axis.Value.Y * t4 + eu.Y * qx + ev.Y * qy,
                            mid.Z + axis.Value.Z * t4 + eu.Z * qx + ev.Z * qy);

                        int donor = NearestFree(core, start, taken, p);
                        if (donor < 0) { ok = false; break; }
                        taken.Add(donor);
                        target[donor] = new Vec3(p.X - start[donor].X, p.Y - start[donor].Y, p.Z - start[donor].Z);
                        hasTarget[donor] = true;
                        fromRing[donor] = 2000; fromSlot[donor] = i * 1000 + j;
                        gridV[i, j] = donor;

                        // These are made after the relax has run, so they have to be checked against the
                        // skin here â€” otherwise the patch closing the end is the one part of the cap
                        // nothing keeps off the toes, and they come through it.
                        PushOffSkin(donor);
                    }

                if (ok)
                {
                    // The patch spans the opening, so its inside lies on the OPPOSITE side of the boundary
                    // loop from where the next ring would have been, and the strip convention comes out
                    // backwards here. Which way round is settled by the faces it joins, not by the source
                    // normals: those belong to the toe surface each vertex was borrowed from and say
                    // nothing about which way this surface faces. Two faces sharing an edge must run it
                    // in opposite directions, so count how the strips already ran the boundary edges.
                    var sewn = new HashSet<(ushort, ushort)>();
                    foreach (var (ta, tb, tc) in newTris)
                    {
                        sewn.Add((ta, tb)); sewn.Add((tb, tc)); sewn.Add((tc, ta));
                    }
                    int agrees = 0;
                    for (int i = 0; i < sideA; i++)
                        for (int j = 0; j < sideB; j++)
                            foreach (var (u, v) in new[]
                                     {
                                         (gridV[i, j], gridV[i + 1, j]),
                                         (gridV[i + 1, j], gridV[i + 1, j + 1]),
                                         (gridV[i + 1, j + 1], gridV[i, j + 1]),
                                         (gridV[i, j + 1], gridV[i, j]),
                                     })
                            {
                                if (sewn.Contains((Rep(u), Rep(v)))) agrees--;   // same way round: wrong
                                if (sewn.Contains((Rep(v), Rep(u)))) agrees++;   // opposite: right
                            }
                    bool flip = agrees < 0;

                    for (int i = 0; i < sideA; i++)
                        for (int j = 0; j < sideB; j++)
                        {
                            if (flip)
                            {
                                Emit(gridV[i, j], gridV[i + 1, j + 1], gridV[i + 1, j]);
                                Emit(gridV[i, j], gridV[i, j + 1], gridV[i + 1, j + 1]);
                            }
                            else
                            {
                                Emit(gridV[i, j], gridV[i + 1, j], gridV[i + 1, j + 1]);
                                Emit(gridV[i, j], gridV[i + 1, j + 1], gridV[i, j + 1]);
                            }
                        }

                    // â”€â”€ smooth the end â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                    // Everything before this point relaxed against a tip that did not exist yet: the
                    // patch is built after the relax has finished, so nothing has ever smoothed it, and
                    // the dome rings it meets were smoothed with nothing beyond them. That is what makes
                    // the end read crunchy while the sides look clean. Same rate and the same clearance
                    // floor as the main relax, over the end only, with the last dome ring's outer edge
                    // pinned so the smoothing cannot creep back down the cap.
                    // Which vertices the end is made of: the dome rings, the ring below them so the
                    // patch has something to blend into, and the patch itself.
                    int endFrom = Math.Max(1, chain.Count - TipRings - 1 - TipRelaxSpan);
                    var endSet = new HashSet<int>();
                    var movable = new HashSet<int>();
                    for (int r = endFrom; r < chain.Count; r++)
                        foreach (int n in chain[r])
                        {
                            if (n < 0) continue;
                            endSet.Add(n);
                            if (r > endFrom) movable.Add(n);   // the ring below is the pinned boundary
                        }
                    for (int i = 0; i <= sideA; i++)
                        for (int j = 0; j <= sideB; j++)
                        {
                            endSet.Add(gridV[i, j]);
                            if (i > 0 && i < sideA && j > 0 && j < sideB) movable.Add(gridV[i, j]);
                        }
                    endSet.Remove(-1);
                    movable.Remove(-1);

                    // Neighbours read off the triangles actually emitted, not off the grid and ring
                    // structure. Where rings of unequal length are zipped together the mesh has edges
                    // the structure knows nothing about, and smoothing against the wrong neighbours is
                    // what left spikes at the tip â€” one with an edge three times the mesh's own, beside
                    // another a sixth of it.
                    var endAdj = new Dictionary<int, List<int>>();
                    var seenEdge = new HashSet<(int, int)>();
                    void Join(int a, int b)
                    {
                        if (a < 0 || b < 0 || a == b) return;
                        if (!endSet.Contains(a) || !endSet.Contains(b)) return;
                        if (!seenEdge.Add(a < b ? (a, b) : (b, a))) return;
                        (endAdj.TryGetValue(a, out var la) ? la : endAdj[a] = new List<int>()).Add(b);
                        (endAdj.TryGetValue(b, out var lb) ? lb : endAdj[b] = new List<int>()).Add(a);
                    }
                    foreach (var (ta, tb, tc) in newTris)
                    {
                        int na = nodeOf[ta], nb2 = nodeOf[tb], nc3 = nodeOf[tc];
                        Join(na, nb2); Join(nb2, nc3); Join(nc3, na);
                    }

                    for (int pass = 0; pass < TipRelaxPasses; pass++)
                    {
                        var endMoved = new List<(int Node, Vec3 To)>();
                        foreach (int n in movable)
                        {
                            if (!hasTarget[n] || !endAdj.TryGetValue(n, out var nb) || nb.Count == 0) continue;
                            float sx = 0, sy = 0, sz = 0;
                            foreach (int k in nb) { var q = Placed(k); sx += q.X; sy += q.Y; sz += q.Z; }
                            var p = Placed(n);
                            endMoved.Add((n, new Vec3(
                                p.X + (sx / nb.Count - p.X) * RelaxRate,
                                p.Y + (sy / nb.Count - p.Y) * RelaxRate,
                                p.Z + (sz / nb.Count - p.Z) * RelaxRate)));
                        }
                        foreach (var (n, to) in endMoved)
                            target[n] = new Vec3(to.X - start[n].X, to.Y - start[n].Y, to.Z - start[n].Z);
                        foreach (int n in movable) PushOffSkin(n);
                    }
                }
            }

            foreach (int n in core) cutNode[n] = true;
            capped = true;
        }

        // â”€â”€ stop the skin bulging through the cap â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Clearance has been enforced one way only: every cap vertex is pushed off the nearest skin
        // triangle. Nothing tested the reverse, and a convex toe pad comes through the MIDDLE of a flat
        // cap triangle while all three corners sit comfortably clear â€” the shape of the underside of a
        // toe, and where this showed worst: 57 of 407 skin vertices under the toes outside the shell,
        // the worst by 0.0029 against a 0.005 edge.
        //
        // So walk the skin instead, and lift the cap where it passes under a vertex. The lift each
        // corner needs is the LARGEST any skin vertex asks of it, applied once â€” summing every request
        // stacks a full lift per vertex, and a triangle spanning twenty of them flies off the foot.
        // Capped as well, at a fraction of an edge, so a bad measurement can never do that again.
        if (capped && newTris.Count > 0)
        {
            var capNodes = new int[newTris.Count * 3];
            for (int t = 0; t < newTris.Count; t++)
            {
                var (ta, tb, tc) = newTris[t];
                capNodes[t * 3] = nodeOf[ta];
                capNodes[t * 3 + 1] = nodeOf[tb];
                capNodes[t * 3 + 2] = nodeOf[tc];
            }

            Vec3 At2(int n) => new(start[n].X + target[n].X, start[n].Y + target[n].Y, start[n].Z + target[n].Z);

            var skinPts = new List<int>();
            var capped2 = new List<int>();
            for (int n = 0; n < nodeCount; n++)
            {
                if (cutNode[n]) skinPts.Add(n);
                if (hasTarget[n]) capped2.Add(n);
            }
            float edge2 = MeanEdgeLength(start, adj, skinPts.Count > 0 ? skinPts : capped2);
            float wantClear = edge2 * SkinClearance;
            float maxLift = edge2 * MaxPokeLift;

            var want = new float[nodeCount];      // largest lift any skin vertex asks of this node
            var dir = new Vec3[nodeCount];        // and the direction that asked for it
            var used = new float[nodeCount];      // total already applied, against the cap
            float biggest = 0f;

            for (int pass = 0; pass < PokePasses; pass++)
            {
                Array.Clear(want);
                int asked = 0;

                foreach (int m in skinPts)
                {
                    var pm = start[m];
                    var nm = nNorm[m];

                    int bestT = -1;
                    float bestD = float.MaxValue;
                    Vec3 bestQ = default;
                    for (int t = 0; t < newTris.Count; t++)
                    {
                        Vec3 a = At2(capNodes[t * 3]), b = At2(capNodes[t * 3 + 1]), c = At2(capNodes[t * 3 + 2]);
                        var q = ClosestOnTriangle(pm, a, b, c);
                        float dx = pm.X - q.X, dy = pm.Y - q.Y, dz = pm.Z - q.Z;
                        float d = dx * dx + dy * dy + dz * dz;
                        if (d < bestD) { bestD = d; bestT = t; bestQ = q; }
                    }
                    if (bestT < 0) continue;

                    // Only where the cap actually passes OVER this vertex. On the inner flank of a toe
                    // the skin's normal points at its neighbour, and the nearest cap face is the bridge
                    // spanning the gap â€” lifting that along this normal drives the bridge into the toe
                    // opposite, which is precisely what the two-toe fixture caught.
                    float dqx = bestQ.X - pm.X, dqy = bestQ.Y - pm.Y, dqz = bestQ.Z - pm.Z;
                    float off = dqx * nm.X + dqy * nm.Y + dqz * nm.Z;
                    float latx = dqx - nm.X * off, laty = dqy - nm.Y * off, latz = dqz - nm.Z * off;
                    if (latx * latx + laty * laty + latz * latz > (edge2 * PokeReach) * (edge2 * PokeReach))
                        continue;
                    if (off >= wantClear) continue;
                    float deficit = wantClear - off;
                    asked++;

                    int na = capNodes[bestT * 3], nb = capNodes[bestT * 3 + 1], nc = capNodes[bestT * 3 + 2];
                    Vec3 pa = At2(na), pb = At2(nb), pc = At2(nc);

                    // Barycentric coordinates of the landing point, so the lift stays local to the bulge.
                    float v0x = pb.X - pa.X, v0y = pb.Y - pa.Y, v0z = pb.Z - pa.Z;
                    float v1x = pc.X - pa.X, v1y = pc.Y - pa.Y, v1z = pc.Z - pa.Z;
                    float v2x = bestQ.X - pa.X, v2y = bestQ.Y - pa.Y, v2z = bestQ.Z - pa.Z;
                    float e00 = v0x * v0x + v0y * v0y + v0z * v0z;
                    float e01 = v0x * v1x + v0y * v1y + v0z * v1z;
                    float e11 = v1x * v1x + v1y * v1y + v1z * v1z;
                    float e20 = v2x * v0x + v2y * v0y + v2z * v0z;
                    float e21 = v2x * v1x + v2y * v1y + v2z * v1z;
                    float den = e00 * e11 - e01 * e01;
                    float wa = 1f / 3f, wb = 1f / 3f, wc = 1f / 3f;
                    if (MathF.Abs(den) > 1e-20f)
                    {
                        wb = Math.Clamp((e11 * e20 - e01 * e21) / den, 0f, 1f);
                        wc = Math.Clamp((e00 * e21 - e01 * e20) / den, 0f, 1f);
                        wa = Math.Clamp(1f - wb - wc, 0f, 1f);
                        float sum = wa + wb + wc;
                        if (sum > 1e-6f) { wa /= sum; wb /= sum; wc /= sum; }
                    }

                    // The MAXIMUM asked of each corner this pass, never the sum.
                    void Ask(int n, float w)
                    {
                        if (n < 0 || !hasTarget[n]) return;      // rim vertices are shared: moving one tears the seam
                        float need = deficit * w;
                        if (need <= want[n]) return;
                        want[n] = need;
                        dir[n] = nm;
                    }
                    Ask(na, wa); Ask(nb, wb); Ask(nc, wc);
                }

                if (asked == 0) break;

                foreach (int n in capped2)
                {
                    if (want[n] <= 0f) continue;
                    float step = MathF.Min(want[n], maxLift - used[n]);
                    if (step <= 0f) continue;
                    used[n] += step;
                    biggest = MathF.Max(biggest, used[n]);
                    target[n] = new Vec3(target[n].X + dir[n].X * step,
                                         target[n].Y + dir[n].Y * step,
                                         target[n].Z + dir[n].Z * step);
                }
            }

            capLogSink?.Invoke($"poke: lifted the cap off the skin, most-moved vertex {biggest:F5} "
                             + $"(ceiling {maxLift:F5}, clearance {wantClear:F5})");
        }

        // â”€â”€ even out the pinched cells â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // What is left are pinches: two ring slots that came to rest a tenth of an edge apart, with a
        // long third edge. Ring slots sit at around 60% of the mesh's own edge length â€” oversampled â€”
        // and follow the rim's uneven angles, so now and then two land on top of each other.
        //
        // Smoothing them by POSITION does not work and was measured not to: a pinched pair has nearly
        // the same neighbourhood, so the average pulls both the same way and the short edge survives,
        // while the vertices sink toward the skin (clearance went negative). This slides them along the
        // surface instead â€” the Laplacian with its normal component removed. Spacing evens out, the
        // silhouette does not move, and nothing can descend into a toe, which is what went wrong with
        // every positional attempt at this.
        //
        // Prototyped on the shell the game actually builds before being written here: faces over aspect
        // 8 fall from 8 to 1, the worst from 11.4 to 8.6. It plateaus there â€” no smoothing separates a
        // coincident pair properly. Removing the pinches for good means giving each ring slots in
        // proportion to its own perimeter, as the dome rings already do.
        if (capped && newTris.Count > 0)
        {
            var tanAdj = new Dictionary<int, HashSet<int>>();
            var tanFaces = new Dictionary<int, List<(int A, int B, int C)>>();
            void TanEdge(int a, int b)
            {
                if (a == b) return;
                (tanAdj.TryGetValue(a, out var la) ? la : tanAdj[a] = new HashSet<int>()).Add(b);
                (tanAdj.TryGetValue(b, out var lb) ? lb : tanAdj[b] = new HashSet<int>()).Add(a);
            }
            var capTri = new List<(int A, int B, int C)>(newTris.Count);
            foreach (var (ta, tb, tc) in newTris)
            {
                int na = nodeOf[ta], nb = nodeOf[tb], nc = nodeOf[tc];
                capTri.Add((na, nb, nc));
                TanEdge(na, nb); TanEdge(nb, nc); TanEdge(nc, na);
                foreach (int n in stackalloc[] { na, nb, nc })
                    (tanFaces.TryGetValue(n, out var lf) ? lf : tanFaces[n] = new List<(int, int, int)>())
                        .Add((na, nb, nc));
            }

            // The surviving shell around the cap joins the graph too â€” without it a cap vertex on the
            // rim only sees its cap-side neighbours and the average drags it inward, which is both a
            // worse result and the thing that has gone wrong every other time.
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;
                int na = nodeOf[tris[t]], nb = nodeOf[tris[t + 1]], nc = nodeOf[tris[t + 2]];
                if (cutNode[na] || cutNode[nb] || cutNode[nc]) continue;
                TanEdge(na, nb); TanEdge(nb, nc); TanEdge(nc, na);
                foreach (int n in stackalloc[] { na, nb, nc })
                    (tanFaces.TryGetValue(n, out var lf2) ? lf2 : tanFaces[n] = new List<(int, int, int)>())
                        .Add((na, nb, nc));
            }

            Vec3 Now(int n) => new(start[n].X + target[n].X, start[n].Y + target[n].Y, start[n].Z + target[n].Z);

            var allCap = new List<int>();
            foreach (var kv in tanAdj) if (hasTarget[kv.Key]) allCap.Add(kv.Key);
            float tanEdge = MeanEdgeLength(start, adj, allCap);
            float tanLimit = tanEdge * TangentClamp;

            // Only the vertices of faces that are actually badly shaped; everything else stays put.
            var tanMove = new HashSet<int>();
            foreach (var (a, b, c) in capTri)
            {
                Vec3 pa = Now(a), pb = Now(b), pc = Now(c);
                float e0 = Dist(pa, pb), e1 = Dist(pb, pc), e2 = Dist(pc, pa);
                float lo2 = MathF.Min(e0, MathF.Min(e1, e2)), hi2 = MathF.Max(e0, MathF.Max(e1, e2));
                if (lo2 <= 1e-9f || hi2 / lo2 <= TangentTrigger) continue;
                if (hasTarget[a]) tanMove.Add(a);
                if (hasTarget[b]) tanMove.Add(b);
                if (hasTarget[c]) tanMove.Add(c);
            }

            if (tanMove.Count > 0)
            {
                var from = new Dictionary<int, Vec3>();
                foreach (int n in tanMove) from[n] = Now(n);

                for (int pass = 0; pass < TangentPasses; pass++)
                {
                    var next = new List<(int Node, Vec3 To)>(tanMove.Count);
                    foreach (int n in tanMove)
                    {
                        if (!tanAdj.TryGetValue(n, out var nb) || nb.Count == 0) continue;
                        var p2 = Now(n);
                        float sx = 0, sy = 0, sz = 0;
                        foreach (int k in nb) { var q = Now(k); sx += q.X; sy += q.Y; sz += q.Z; }
                        float dx = sx / nb.Count - p2.X, dy = sy / nb.Count - p2.Y, dz = sz / nb.Count - p2.Z;

                        // The surface normal here, area weighted over the faces this vertex belongs to.
                        float ax = 0, ay = 0, az = 0;
                        if (tanFaces.TryGetValue(n, out var fl))
                            foreach (var (a, b, c) in fl)
                            {
                                Vec3 pa = Now(a), pb = Now(b), pc = Now(c);
                                float ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
                                float wx = pc.X - pa.X, wy = pc.Y - pa.Y, wz = pc.Z - pa.Z;
                                ax += uy * wz - uz * wy; ay += uz * wx - ux * wz; az += ux * wy - uy * wx;
                            }
                        if (Normalize(new Vec3(ax, ay, az)) is { } nn)
                        {
                            float along = dx * nn.X + dy * nn.Y + dz * nn.Z;
                            dx -= nn.X * along; dy -= nn.Y * along; dz -= nn.Z * along;   // tangential only
                        }

                        var cand = new Vec3(p2.X + dx * RelaxRate, p2.Y + dy * RelaxRate, p2.Z + dz * RelaxRate);

                        // ...and never on top of a neighbour. Sliding vertices together evens the
                        // spacing as readily as it evens the shape, and three of the four collapsed
                        // pairs left in the cap were made here â€” traced back to adjacent slots of one
                        // ring, 2% of an edge apart, which is what shows in game as a black fleck.
                        float keep2 = tanEdge * SlotMinGap;
                        foreach (int k in nb)
                        {
                            var np3 = Now(k);
                            float gx = cand.X - np3.X, gy = cand.Y - np3.Y, gz = cand.Z - np3.Z;
                            float gd = MathF.Sqrt(gx * gx + gy * gy + gz * gz);
                            if (gd >= keep2 || gd <= 1e-9f) continue;
                            float grow2 = (keep2 - gd) / gd;
                            cand = new Vec3(cand.X + gx * grow2, cand.Y + gy * grow2, cand.Z + gz * grow2);
                        }

                        // Never far from where it started, however many passes run.
                        var o = from[n];
                        float tx = cand.X - o.X, ty = cand.Y - o.Y, tz = cand.Z - o.Z;
                        float travel = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                        if (travel > tanLimit)
                        {
                            float k2 = tanLimit / travel;
                            cand = new Vec3(o.X + tx * k2, o.Y + ty * k2, o.Z + tz * k2);
                        }
                        next.Add((n, cand));
                    }
                    foreach (var (n, to) in next)
                        target[n] = new Vec3(to.X - start[n].X, to.Y - start[n].Y, to.Z - start[n].Z);
                }
            }

        }

        if (capLogSink != null && capped)
        {
            var placed = new List<int>();
            for (int n = 0; n < nodeCount; n++) if (hasTarget[n]) placed.Add(n);
            var all = new List<int>();
            for (int n = 0; n < nodeCount; n++) if (cutNode[n] || hasTarget[n]) all.Add(n);
            float near = MeanEdgeLength(start, adj, all) * 0.15f;
            Vec3 Fin(int n) => new(start[n].X + target[n].X, start[n].Y + target[n].Y, start[n].Z + target[n].Z);
            string Where(int n) => fromRing[n] switch
            {
                -1 => "rim",
                2000 => $"patch[{fromSlot[n] / 1000},{fromSlot[n] % 1000}]",
                >= 1000 => $"dome{fromRing[n] - 1000} slot {fromSlot[n]}",
                _ => $"ring {fromRing[n]} slot {fromSlot[n]}",
            };
            int reported = 0;
            for (int a = 0; a < placed.Count && reported < 12; a++)
                for (int b = a + 1; b < placed.Count && reported < 12; b++)
                {
                    var pa = Fin(placed[a]);
                    var pb = Fin(placed[b]);
                    float dx = pa.X - pb.X, dy = pa.Y - pb.Y, dz = pa.Z - pb.Z;
                    float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d >= near) continue;
                    capLogSink($"collapsed pair {d:F6} apart at ({pa.X:F4},{pa.Y:F4},{pa.Z:F4}): "
                             + $"{Where(placed[a])} <-> {Where(placed[b])}");
                    reported++;
                }
        }

        // â”€â”€ UVs for the rebuilt surface â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Every cap vertex is a vertex REUSED from somewhere else in the toe box, and it still carries
        // that donor's texture coordinate. Left alone, the cap samples the skin's texture â€” and its
        // alpha â€” from wherever each vertex happened to come from: measured on the equipped body, 494
        // of 2112 cap faces had their UV scale off by more than 8x from the shell's own median, the
        // worst by 6300x. It does not read as torn geometry, it reads as a smeared texture.
        //
        // So shrink-wrap instead: drop each moved vertex onto the surface the cap replaced and take
        // the UV where it lands, interpolated across that triangle. This is what projecting the UVs
        // by hand would do, and it keeps the cap continuous with the skin it is sewn to.
        (float U, float V)[]? nodeUV = null;
        if (capped)
        {
            var srcTri = new List<(Vec3 A, Vec3 B, Vec3 C, int NA, int NB, int NC)>();
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;
                int na = nodeOf[tris[t]], nb = nodeOf[tris[t + 1]], nc = nodeOf[tris[t + 2]];
                // The replaced region plus a rim of what survives, so the join stays continuous.
                if (!cutNode[na] && !cutNode[nb] && !cutNode[nc]) continue;
                srcTri.Add((start[na], start[nb], start[nc], na, nb, nc));
            }

            if (srcTri.Count > 0)
            {
                // One UV per welded node. Coincident duplicates across a UV seam collapse to one node
                // and one of their UVs wins; that is already true of every other cap attribute.
                var uvOf = new (float U, float V)[nodeCount];
                for (int i = 0; i < vc; i++) uvOf[nodeOf[i]] = uv[i];

                nodeUV = new (float U, float V)[nodeCount];
                for (int n = 0; n < nodeCount; n++)
                {
                    if (!hasTarget[n]) continue;
                    var p2 = new Vec3(start[n].X + target[n].X, start[n].Y + target[n].Y, start[n].Z + target[n].Z);

                    float bestD = float.MaxValue;
                    (float U, float V) best = uvOf[n];
                    foreach (var (a, b, c, na, nb, nc) in srcTri)
                    {
                        var q = ClosestOnTriangle(p2, a, b, c);
                        float dx = p2.X - q.X, dy = p2.Y - q.Y, dz = p2.Z - q.Z;
                        float d = dx * dx + dy * dy + dz * dz;
                        if (d >= bestD) continue;
                        bestD = d;

                        // Barycentric coordinates of the landing point, by area.
                        float v0x = b.X - a.X, v0y = b.Y - a.Y, v0z = b.Z - a.Z;
                        float v1x = c.X - a.X, v1y = c.Y - a.Y, v1z = c.Z - a.Z;
                        float v2x = q.X - a.X, v2y = q.Y - a.Y, v2z = q.Z - a.Z;
                        float d00 = v0x * v0x + v0y * v0y + v0z * v0z;
                        float d01 = v0x * v1x + v0y * v1y + v0z * v1z;
                        float d11 = v1x * v1x + v1y * v1y + v1z * v1z;
                        float d20 = v2x * v0x + v2y * v0y + v2z * v0z;
                        float d21 = v2x * v1x + v2y * v1y + v2z * v1z;
                        float den = d00 * d11 - d01 * d01;
                        if (MathF.Abs(den) < 1e-20f) { best = uvOf[na]; continue; }
                        float wb = (d11 * d20 - d01 * d21) / den;
                        float wc = (d00 * d21 - d01 * d20) / den;
                        float wa = 1f - wb - wc;
                        best = (uvOf[na].U * wa + uvOf[nb].U * wb + uvOf[nc].U * wc,
                                uvOf[na].V * wa + uvOf[nb].V * wb + uvOf[nc].V * wc);
                    }
                    nodeUV[n] = best;
                }
            }
        }

        // A mesh may have nothing to cap and still have islands to drop â€” the toenail mesh is exactly
        // that: every island on it is swallowed whole, so no cap is ever built for it.
        bool anyDropped = false;
        foreach (bool d in dropNode) if (d) { anyDropped = true; break; }
        // An empty NewTriangles is a failure only when geometry was meant to be built. On the authored
        // path it is the expected outcome â€” the cap is a modelled mesh, so all this pass contributes is
        // the CUT, and demanding new triangles here threw that cut away every time. It went unnoticed
        // because the painted map is wide enough to swallow the toenail islands whole, which sets
        // anyDropped and carries the plan past this line; the moment the cut was narrowed to the cap the
        // nails stopped being covered, the plan came back null, and the shell was emitted as the entire
        // uncut foot with the cap laid on top of it.
        if ((!capped || (buildGeometry && newTris.Count == 0)) && !anyDropped) return null;

        var delta = new Vec3[vc];
        for (int i = 0; i < vc; i++)
        {
            int n = nodeOf[i];
            if (hasTarget[n]) delta[i] = target[n];
        }

        // Nodes the cap never moved must report zero weight, so the normal pass leaves their bytes
        // exactly as they were â€” that is what keeps an untouched shell byte-identical.
        for (int n = 0; n < nodeCount; n++)
            if (!hasTarget[n]) nW[n] = 0f;

        return new ToeCapPlan
        {
            Delta = delta, NodeOf = nodeOf, NodeWeight = nW, NodeNormal = nNorm, DropNode = dropNode,
            NodeUV = nodeUV,
            CutNode = cutNode, NewTriangles = newTris,
        };
    }

    /// <summary>Longest cycle in a rim adjacency map, walked in connectivity order.</summary>
    private static List<int> LongestLoop(Dictionary<int, List<int>> rim)
    {
        var best = new List<int>();
        var seen = new HashSet<int>();
        foreach (int s in rim.Keys)
        {
            if (!seen.Add(s)) continue;
            var loop = new List<int> { s };
            int cur = s, prev = -1;
            while (true)
            {
                int next = -1;
                foreach (int k in rim[cur])
                    if (k != prev && !seen.Contains(k)) { next = k; break; }
                if (next < 0) break;
                seen.Add(next);
                loop.Add(next);
                prev = cur;
                cur = next;
            }
            if (loop.Count > best.Count) best = loop;
        }
        return best;
    }

    /// <summary>
    /// Rotate a rim loop to start near angle zero and run counter-clockwise, WITHOUT reordering it â€”
    /// its walk order is the only thing that keeps the stitch from crossing itself.
    /// </summary>
    private static void OrientLoop(List<int> loop, Vec3[] pos, Func<Vec3, (float X, float Y)> flatten)
    {
        int n = loop.Count;
        var ang = new float[n];
        for (int i = 0; i < n; i++)
        {
            var f = flatten(pos[loop[i]]);
            ang[i] = MathF.Atan2(f.Y, f.X);
        }

        float turn = 0;
        for (int i = 0; i < n; i++)
        {
            float d = ang[(i + 1) % n] - ang[i];
            while (d > MathF.PI) d -= MathF.Tau;
            while (d < -MathF.PI) d += MathF.Tau;
            turn += d;
        }
        if (turn < 0) { loop.Reverse(); Array.Reverse(ang); }

        int startAt = 0;
        for (int i = 1; i < n; i++)
            if (MathF.Abs(ang[i]) < MathF.Abs(ang[startAt])) startAt = i;
        if (startAt == 0) return;

        var rotated = new List<int>(n);
        for (int i = 0; i < n; i++) rotated.Add(loop[(startAt + i) % n]);
        loop.Clear();
        loop.AddRange(rotated);
    }

    /// <summary>Closest point to <paramref name="p"/> on a triangle, including its edges and corners.</summary>
    private static float Dist(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>"PTCB" â€” the authored cap's binding to the body, see <see cref="BakeCapBind"/>.</summary>
    private const uint CapBindMagic = 0x42435450;

    /// <summary>
    /// Where the authored cap sits, expressed so it survives a change of foot: per vertex a coordinate in
    /// the BODY's UV atlas, how far off that surface it sits along the normal, and which side of the body
    /// it is on. Every body shares the atlas â€” that is the premise the whole overlay system rests on â€” so
    /// a vertex recorded this way can be put back on any foot in any shape.
    /// <para/>
    /// The side matters because the atlas is MIRRORED: measured on this body, the cap vertices at
    /// x = +0.0440 and x = -0.0440 both land on uv (0.874, 0.297). A coordinate alone would be ambiguous
    /// between the two feet.
    /// <para/>
    /// The reference foot is NOT shipped and must not be â€” it is somebody's body mod, and bundling it
    /// would redistribute it. Only these four numbers per vertex travel.
    /// </summary>
    /// <param name="offsetsFrom">
    /// An existing binding to take the OFFSETS from, leaving only the atlas coordinate to be measured
    /// here. The coordinate has to be per-body, because two bodies parameterise the same layout
    /// differently; the offset must not be, because it is how high the cap was MODELLED above the skin.
    /// Re-measuring it against another body bakes that body's shape difference into the cap â€” on Rue,
    /// whose toes are slimmer than the ones the cap was authored on, that reproduced Neolithe's bulk and
    /// left the cap standing visibly clear of the toes.
    /// </param>
    public static byte[] BakeCapBind(byte[] capMdl, IReadOnlyList<byte[]> referenceBodies,
                                     Action<string>? diag = null, byte[]? offsetsFrom = null)
    {
        Dictionary<int, float[]>? keepOff = null;
        if (offsetsFrom != null) keepOff = ReadBindOffsets(offsetsFrom);

        var cap = Parse(capMdl);
        var tris = BindSurface(referenceBodies);
        if (tris.Count == 0) throw new InvalidOperationException("reference body has no skin geometry");


        var meshes = new List<int>();
        int meshEnd = cap.Lod0MeshIndex + cap.Lod0MeshCount;
        for (int m = cap.Lod0MeshIndex; m < meshEnd && m < cap.MeshCount; m++)
            if (BitConverter.ToUInt16(cap.S, cap.MeshStart + m * 36) != 0) meshes.Add(m);

        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        w.Write(meshes.Count);

        // WHICH PART OF THE BODY the cap belongs to, recorded as the bones its landings are skinned to.
        // Every body model carries its OWN [0,1] atlas â€” the feet and the torso both use the whole square
        // â€” so an atlas coordinate is meaningless without knowing which one it belongs to. Without this
        // the first placement put the toe cap at y = 0.87, on the waist, having found the same coordinate
        // there. Bones separate them cleanly and survive any body or mod: a foot triangle is weighted to
        // j_asi_*, a torso triangle never is.
        var parts = new HashSet<string>(StringComparer.Ordinal);

        float worstOff = 0f, worstRes = 0f;
        int total = 0;
        foreach (int m in meshes)
        {
            ReadCapVertices(cap, m, out var cp, out _);
            w.Write(m);
            w.Write(cp.Length);
            var authored = keepOff != null && keepOff.TryGetValue(m, out var ao) ? ao : null;
            for (int vi = 0; vi < cp.Length; vi++)
            {
                var p = cp[vi];
                float bestD = float.MaxValue;
                (float U, float V) uv = default;
                float off = 0f;
                (string Bone, float W)[] landedOn = [];
                // Which way the skin faced where this vertex landed. Kept because an atlas coordinate can
                // be covered by more than one triangle â€” the sole and the top of a toe can be packed over
                // each other â€” and the two candidates face opposite ways. Without it the round trip onto
                // the very foot the cap was measured on was out by as much as 0.0104, three edge lengths.
                Vec3 face = default;
                foreach (var t in tris)
                {
                    float cx = t.Ctr.X - p.X, cy = t.Ctr.Y - p.Y, cz = t.Ctr.Z - p.Z;
                    if (cx * cx + cy * cy + cz * cz > bestD + 0.01f) continue;
                    var q = ClosestOnTriangle(p, t.A, t.B, t.C);
                    float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d >= bestD) continue;
                    bestD = d;
                    var (ba, bb, bc) = Barycentric(q, t.A, t.B, t.C);
                    // Stored in the triangle's OWN cell, so it means the same thing on a body that packs
                    // its atlas somewhere else. See TileOf.
                    var tile = TileOf(t);
                    uv = (t.Ua.U * ba + t.Ub.U * bb + t.Uc.U * bc - tile.U,
                          t.Ua.V * ba + t.Ub.V * bb + t.Uc.V * bc - tile.V);
                    var n = NormalizeOr(new Vec3(t.Na.X * ba + t.Nb.X * bb + t.Nc.X * bc,
                                                 t.Na.Y * ba + t.Nb.Y * bb + t.Nc.Y * bc,
                                                 t.Na.Z * ba + t.Nb.Z * bb + t.Nc.Z * bc), default);
                    // SIGNED along the normal, so a vertex tucked under the surface comes back under it.
                    off = dx * n.X + dy * n.Y + dz * n.Z;
                    landedOn = t.Wa;
                    face = n;
                }
                foreach (var (bone, _) in landedOn) parts.Add(bone);
                // The height the cap was MODELLED at, where one is on offer â€” see offsetsFrom.
                if (authored != null && vi < authored.Length) off = authored[vi];

                int side = p.X >= 0f ? 1 : -1;

                // WHAT THE PLACEMENT WILL ACTUALLY REBUILD, and the difference from what was authored.
                // The atlas coordinate is recovered by inverting the UV parameterisation, which is not
                // the operation that produced it (that was a closest-point in 3D), so the two disagree
                // wherever the atlas is compressed or a coordinate is shared by more than one triangle.
                // Alone that is sub-millimetre, but the offset multiplies it, and the offset is largest
                // exactly over the toenails â€” the nails are their own mesh and not in the skin surface,
                // so the reference there is the recessed nail bed. Storing the difference makes the
                // round trip onto this body exact, and carries the author's intent to any other body
                // because it travels in the surface's own tangent frame.
                float rt = 0f, rb = 0f, rn = 0f;
                if (ResolveBindLanding(tris, uv.U, uv.V, side, face,
                                       out var at, out var n2, out _, out var tan, out var bit)
                    < float.MaxValue)
                {
                    float ex = p.X - (at.X + n2.X * off), ey = p.Y - (at.Y + n2.Y * off),
                          ez = p.Z - (at.Z + n2.Z * off);
                    rt = ex * tan.X + ey * tan.Y + ez * tan.Z;
                    rb = ex * bit.X + ey * bit.Y + ez * bit.Z;
                    rn = ex * n2.X + ey * n2.Y + ez * n2.Z;
                    worstRes = MathF.Max(worstRes, MathF.Sqrt(ex * ex + ey * ey + ez * ez));
                }

                w.Write(uv.U); w.Write(uv.V); w.Write(off); w.Write(side);
                w.Write(face.X); w.Write(face.Y); w.Write(face.Z);
                w.Write(rt); w.Write(rb); w.Write(rn);
                worstOff = MathF.Max(worstOff, MathF.Abs(off));
                total++;
            }
        }

        var ms = new MemoryStream();
        var head = new BinaryWriter(ms);
        head.Write(CapBindMagic);
        head.Write(2);   // version â€” 2 adds the tangent-frame residual, see the note at the write site
        head.Write(parts.Count);
        foreach (var b in parts.OrderBy(x => x, StringComparer.Ordinal)) head.Write(b);
        ms.Write(body.GetBuffer(), 0, (int)body.Length);

        diag?.Invoke($"cap bind: {total} vertices over {meshes.Count} mesh(es), furthest off the skin "
                   + $"{worstOff:F5}, worst residual corrected {worstRes:F5}, anchored to {parts.Count} bone(s): "
                   + string.Join(", ", parts.OrderBy(x => x, StringComparer.Ordinal)));
        return ms.ToArray();
    }

    /// <summary>Where one cap mesh's vertices land on the body currently equipped.</summary>
    internal sealed class CapPlacement
    {
        public required int Mesh;
        public required Vec3[] Pos;
        public required Vec3[] Nrm;
        public required (float U, float V)[] Uv;
        public required (string Bone, float W)[][] W;
        /// <summary>Vertices whose atlas coordinate is not covered by this body; they keep their authored place.</summary>
        public required int Missed;

        /// <summary>How many vertices were actually resolved â€” all of them, or the sample when scoring.</summary>
        public required int Considered;
    }

    /// <summary>
    /// Fit the authored cap to the body actually equipped, by looking each baked atlas coordinate back up
    /// on it and stepping off along the normal there. This is what makes one authored cap work on a foot
    /// it was never modelled against â€” a heeled foot sits a median of 0.067 from the flat one it was
    /// authored on, which is about eighteen edge lengths and reads in game as the cap floating clear of
    /// the toes altogether.
    /// </summary>
    /// <param name="stride">
    /// Resolve only every n-th vertex. For scoring a binding against a body, where the hit rate is all
    /// that is wanted and a full placement is every cap vertex against every skin triangle.
    /// </param>
    /// <summary>
    /// The bone NAMES a binding was baked against, read from its header alone — no placement, no surface
    /// collection. This is a body fingerprint: Rue weights its toes to IVCS bones (iv_asi_*) where
    /// Neolithe and stock Bibo+ use the game's own (j_asi_*), so a cap baked for one names bones the
    /// other does not have.
    /// </summary>
    private static HashSet<string> ReadBindBones(byte[] bind)
    {
        var parts = new HashSet<string>(StringComparer.Ordinal);
        if (bind.Length < 12 || BitConverter.ToUInt32(bind, 0) != CapBindMagic) return parts;
        try
        {
            var r = new BinaryReader(new MemoryStream(bind));
            r.ReadUInt32();
            if (r.ReadInt32() is not (1 or 2)) return parts;
            int n = r.ReadInt32();
            if (n is < 0 or > 4096) return parts;
            for (int i = 0; i < n; i++) parts.Add(r.ReadString());
        }
        catch { parts.Clear(); }
        return parts;
    }

    internal static List<CapPlacement>? TryPlaceCapFromBind(byte[] bind, IReadOnlyList<byte[]> bodies,
                                                            Action<string>? diag = null,
                                                            byte[]? capMdl = null, int stride = 1)
    {
        if (bind.Length < 12 || BitConverter.ToUInt32(bind, 0) != CapBindMagic) return null;
        var tris = BindSurface(bodies);
        if (tris.Count == 0) return null;

        var r = new BinaryReader(new MemoryStream(bind));
        r.ReadUInt32();
        int version = r.ReadInt32();
        // 1: (u, v, offset, side, facing). 2: the same plus a residual in the landing's tangent frame.
        // Version 1 still loads â€” a cap bound before the residual existed is imperfect, not unusable.
        if (version is not (1 or 2)) { diag?.Invoke($"cap bind: version {version} not understood"); return null; }

        int partCount = r.ReadInt32();
        var parts = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < partCount; i++) parts.Add(r.ReadString());
        // Only the part of the body the cap was bound to. See the note in BakeCapBind: without this the
        // toe cap finds its own atlas coordinate on the torso and lands at the waist.
        tris = tris.Where(t => t.Wa.Any(x => parts.Contains(x.Bone))
                            || t.Wb.Any(x => parts.Contains(x.Bone))
                            || t.Wc.Any(x => parts.Contains(x.Bone))).ToList();
        if (tris.Count == 0) { diag?.Invoke("cap bind: this body has none of the bound bones"); return null; }
        {
            var all = BindSurface(bodies);
            (float, float, float, float) Span(List<SkinTri> ts)
            {
                float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
                foreach (var t in ts)
                    foreach (var c in new[] { t.Ua, t.Ub, t.Uc })
                    {
                        var tile = TileOf(t);
                        u0 = MathF.Min(u0, c.U - tile.U); u1 = MathF.Max(u1, c.U - tile.U);
                        v0 = MathF.Min(v0, c.V - tile.V); v1 = MathF.Max(v1, c.V - tile.V);
                    }
                return (u0, u1, v0, v1);
            }
            var sp = Span(tris);
            diag?.Invoke($"cap bind: {tris.Count} of {all.Count} triangle(s) carry the bound bones; "
                       + $"their atlas spans u {sp.Item1:F3}..{sp.Item2:F3} v {sp.Item3:F3}..{sp.Item4:F3}");
        }

        int meshCount = r.ReadInt32();

        var outp = new List<CapPlacement>();
        for (int mi = 0; mi < meshCount; mi++)
        {
            int mesh = r.ReadInt32();
            int vc = r.ReadInt32();
            var pos = new Vec3[vc];
            var nrm = new Vec3[vc];
            var uvs = new (float U, float V)[vc];
            var wts = new (string Bone, float W)[vc][];
            var found = new bool[vc];
            int missed = 0, sampled = 0;

            for (int i = 0; i < vc; i++)
            {
                float u = r.ReadSingle(), v = r.ReadSingle(), off = r.ReadSingle();
                int side = r.ReadInt32();
                var face = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                // Version 2 carries the reconstruction residual in the landing's tangent frame. See
                // BakeCapBind: without it the round trip onto the very body the cap was authored on is
                // out by up to 0.0039, and every one of the worst offenders sits on a toenail â€” the cap
                // stands furthest off the skin there (the nails are not in the skin mesh, so the
                // reference surface is the recessed nail bed), and that long lever multiplies any slip
                // in the reconstructed landing. It read in game as a dish pressed into the toenail.
                float rt = 0f, rb = 0f, rn = 0f;
                if (version >= 2) { rt = r.ReadSingle(); rb = r.ReadSingle(); rn = r.ReadSingle(); }
                uvs[i] = (u, v);
                wts[i] = [];
                if (stride > 1 && i % stride != 0) continue;
                sampled++;

                float best = ResolveBindLanding(tris, u, v, side, face,
                                                out var at, out var n2, out var w2, out var tan, out var bit);
                if (best < float.MaxValue)
                {
                    pos[i] = new Vec3(at.X + n2.X * off + tan.X * rt + bit.X * rb + n2.X * rn,
                                      at.Y + n2.Y * off + tan.Y * rt + bit.Y * rb + n2.Y * rn,
                                      at.Z + n2.Z * off + tan.Z * rt + bit.Z * rb + n2.Z * rn);
                    nrm[i] = n2;
                    wts[i] = w2;
                }
                found[i] = best <= CapBindMissTolerance;
                if (!found[i]) missed++;
            }

            // A vertex whose coordinate falls in a gap of the atlas has nowhere to go â€” and leaving it at
            // its authored place is far worse than it sounds, because every neighbour has moved. On the
            // heeled foot that is a jump of 0.067, and the 76 stragglers dragged triangles across it:
            // slivers went from 2 to 540, worst aspect from 8 to 49, and the mesh stopped being manifold.
            // They follow the crowd instead, taking the average of whichever neighbours did land, spread
            // outwards until none are left. A vertex placed this way is in the right region and smooth
            // with its surroundings, which is all the cap needs of it.
            // Scoring only wants the hit rate; reported against what was actually looked at.
            if (stride > 1)
            {
                outp.Add(new CapPlacement
                {
                    Mesh = mesh, Pos = pos, Nrm = nrm, Uv = uvs, W = wts,
                    Missed = missed, Considered = sampled,
                });
                continue;
            }

            if (missed > 0 && capMdl != null)
            {
                try
                {
                    var capSrc2 = Parse(capMdl);
                    ReadCapVertices(capSrc2, mesh, out var asAuthored, out _);
                    var tri2 = CapTriangles(capSrc2, mesh, (ushort)vc);
                    var near2 = new List<int>[vc];
                    for (int i = 0; i < vc; i++) near2[i] = [];
                    for (int t = 0; t + 2 < tri2.Count; t += 3)
                        for (int k = 0; k < 3; k++)
                        {
                            int a = tri2[t + k], b = tri2[t + (k + 1) % 3];
                            if (a < vc && b < vc) { near2[a].Add(b); near2[b].Add(a); }
                        }

                    int filled = 0;
                    for (int pass = 0; pass < CapBindFillPasses; pass++)
                    {
                        int did = 0;
                        for (int i = 0; i < vc; i++)
                        {
                            if (found[i]) continue;
                            // Each neighbour votes for where this vertex should be by carrying its OWN
                            // move and keeping the authored gap between them. Averaging the neighbours'
                            // positions outright looks equivalent and is not: two adjacent stragglers
                            // sharing a neighbourhood average to the SAME point, which is a zero-area
                            // triangle â€” measured, faces at aspect 2.7e9 and the cap no longer manifold.
                            Vec3 sp = default, sn = default;
                            int c = 0;
                            foreach (int j in near2[i])
                            {
                                if (!found[j]) continue;
                                var keep = i < asAuthored.Length && j < asAuthored.Length
                                    ? new Vec3(asAuthored[i].X - asAuthored[j].X,
                                               asAuthored[i].Y - asAuthored[j].Y,
                                               asAuthored[i].Z - asAuthored[j].Z)
                                    : default;
                                sp = new Vec3(sp.X + pos[j].X + keep.X, sp.Y + pos[j].Y + keep.Y,
                                              sp.Z + pos[j].Z + keep.Z);
                                sn = new Vec3(sn.X + nrm[j].X, sn.Y + nrm[j].Y, sn.Z + nrm[j].Z);
                                c++;
                            }
                            if (c == 0) continue;
                            pos[i] = new Vec3(sp.X / c, sp.Y / c, sp.Z / c);
                            nrm[i] = NormalizeOr(sn, nrm[i]);
                            did++;
                        }
                        if (did == 0) break;
                        for (int i = 0; i < vc; i++) if (!found[i] && near2[i].Any(j => found[j])) found[i] = true;
                        filled += did;
                    }
                    diag?.Invoke($"cap bind: {filled} of {missed} unplaced vertices filled from their neighbours");
                }
                catch (Exception ex)
                {
                    diag?.Invoke($"cap bind: could not fill unplaced vertices ({ex.Message})");
                }
            }

            outp.Add(new CapPlacement
            {
                Mesh = mesh, Pos = pos, Nrm = nrm, Uv = uvs, W = wts,
                Missed = missed, Considered = sampled,
            });
            {
                float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
                foreach (var (u2, v2) in uvs)
                { u0 = MathF.Min(u0, u2); u1 = MathF.Max(u1, u2); v0 = MathF.Min(v0, v2); v1 = MathF.Max(v1, v2); }
                diag?.Invoke($"cap bind: mesh {mesh} placed {vc} vertices on the equipped body"
                           + (missed > 0 ? $", {missed} outside its atlas" : "")
                           + $"; the cap wants u {u0:F3}..{u1:F3} v {v0:F3}..{v1:F3}");
            }
        }
        return outp;
    }

    /// <summary>
    /// Which cell of the atlas a triangle lives in. A body's UVs are not obliged to sit in [0,1]: vanilla
    /// puts U in [1,2], bibo puts V in [-1,0], and the model equipped here is in a different cell again.
    /// Comparing a coordinate from one body against a triangle from another is meaningless until both are
    /// brought back to the same cell â€” before this, every lookup on the heeled foot missed by 56 to 58
    /// barycentric units, which is not a near miss, it is a different coordinate system.
    /// </summary>
    private static (float U, float V) TileOf(SkinTri t)
        => (MathF.Floor(MathF.Min(t.Ua.U, MathF.Min(t.Ub.U, t.Uc.U))),
            MathF.Floor(MathF.Min(t.Ua.V, MathF.Min(t.Ub.V, t.Uc.V))));

    /// <summary>
    /// Skinning of the body surface nearest a point, blended across the triangle it lands on. Returns
    /// empty when nothing is within <paramref name="reach"/>.
    /// </summary>
    private static (string Bone, float W)[] NearestWeights(Vec3 p, List<SkinTri> tris, float reach)
    {
        float best = reach * reach;
        (string Bone, float W)[] found = [];
        foreach (var t in tris)
        {
            float cx = t.Ctr.X - p.X, cy = t.Ctr.Y - p.Y, cz = t.Ctr.Z - p.Z;
            if (cx * cx + cy * cy + cz * cz > best + 0.01f) continue;
            var q = ClosestOnTriangle(p, t.A, t.B, t.C);
            float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d >= best) continue;
            best = d;
            var (ba, bb, bc) = Barycentric(q, t.A, t.B, t.C);
            found = BlendWeights(t.Wa, ba, t.Wb, bb, t.Wc, bc);
        }
        return found;
    }

    /// <summary>
    /// UV of the body surface nearest a point, interpolated across the triangle it lands on. Null when
    /// nothing is within <paramref name="reach"/>. The sibling of <see cref="NearestWeights"/>, and used
    /// for the same reason: a welded vertex has MOVED, so everything it carries has to be re-read at
    /// where it ended up rather than kept from where it started.
    /// </summary>
    private static (float U, float V)? NearestUv(Vec3 p, List<SkinTri> tris, float reach)
    {
        float best = reach * reach;
        (float U, float V)? found = null;
        foreach (var t in tris)
        {
            float cx = t.Ctr.X - p.X, cy = t.Ctr.Y - p.Y, cz = t.Ctr.Z - p.Z;
            if (cx * cx + cy * cy + cz * cz > best + 0.01f) continue;
            var q = ClosestOnTriangle(p, t.A, t.B, t.C);
            float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d >= best) continue;
            best = d;
            var (ba, bb, bc) = Barycentric(q, t.A, t.B, t.C);
            found = (t.Ua.U * ba + t.Ub.U * bb + t.Uc.U * bc,
                     t.Ua.V * ba + t.Ub.V * bb + t.Uc.V * bc);
        }
        return found;
    }

    /// <summary>Barycentric coordinate of a point against a triangle in UV space.</summary>
    private static (float A, float B, float C) Barycentric2(
        float u, float v, (float U, float V) a, (float U, float V) b, (float U, float V) c)
    {
        float v0u = b.U - a.U, v0v = b.V - a.V;
        float v1u = c.U - a.U, v1v = c.V - a.V;
        float den = v0u * v1v - v1u * v0v;
        // A triangle with no area in the ATLAS contains nothing. Reporting it as (1,0,0) reads as
        // "strictly inside" and the search stops there â€” measured, three different coordinates all
        // resolved to the same vertex, because the first collapsed triangle in the list swallowed them.
        if (MathF.Abs(den) < 1e-14f) return (1f, -1f, -1f);
        float v2u = u - a.U, v2v = v - a.V;
        float wb = (v2u * v1v - v1u * v2v) / den;
        float wc = (v0u * v2v - v2u * v0v) / den;
        return (1f - wb - wc, wb, wc);
    }

    /// <summary>
    /// How far outside a triangle, in barycentric terms, a baked coordinate may land before it counts as
    /// unplaced. Small but not zero: the atlas has gaps between islands and a vertex on a seam can miss
    /// every triangle by a hair.
    /// </summary>
    private const float CapBindMissTolerance = 0.01f;


    /// <summary>
    /// Rings a vertex with no atlas coordinate may be filled from. A handful is plenty â€” the gaps are a
    /// vertex or two wide â€” and a bound stops a cap that failed to place at all from being smeared into
    /// one point by repeated averaging.
    /// </summary>
    private const int CapBindFillPasses = 6;

    /// <summary>
    /// Share of a cap's vertices that may fail to place before the binding is judged not to describe this
    /// body at all, and the cap is declined rather than emitted. Measured: the foot it was authored on
    /// gives 0%, the same foot in heels 4%, and a different body 80% â€” so the two cases are nowhere near
    /// each other and the exact cut-off does not much matter.
    /// <para/>
    /// Declining matters because the alternative is not a slightly-wrong cap: the vertices that DO place
    /// move to the new body while the rest stay where they were authored, and the triangles between them
    /// stretch across the gap. That is the fan of shards this guard exists to prevent.
    /// </summary>
    /// <summary>
    /// How close two caps' bone coverage must be to count as equal, leaving the placement score to
    /// separate them. Anything wider than this is a different body, not a worse fit.
    /// </summary>
    private const float CapBoneCoverTie = 0.02f;

    private const float CapBindMaxUnplaced = 0.15f;

    /// <summary>
    /// Vertices skipped between samples when scoring a binding against a body. Choosing between bindings
    /// only needs the rough hit rate, and the difference being measured is 0% against 80%.
    /// </summary>
    private const int CapBindProbeStride = 8;

    /// <summary>
    /// Coverage texels the cap's own trim is widened by, so it reaches at least as far as the shell's.
    /// See the capDef note in the layer loop â€” the shared "any texel visible" test dilates by the asking
    /// triangle's size, and the cap's triangles are far smaller than the shell's.
    /// </summary>
    private const int CapCoverDilate = 2;

    /// <summary>

    /// <summary>
    /// Most triangles a connected component may have and still be considered a toenail patch. A nail is
    /// about a hundred; a foot is thousands. Absolute, because a shell can consist of nothing but nail
    /// patches and then nothing is small relative to anything.
    /// </summary>
    private const int NailIslandMaxTris = 600;

    /// <summary>Largest boundary loop, in edges, that FillSmallHoles will close. Under the 16-20 a
    /// toenail socket carries, which has to stay open.</summary>
    private const int SmallHoleEdges = 8;

    /// <summary>
    /// How far a shell or cap vertex must stand off the body's skin around the toes. The shell is pushed
    /// 1 mm, but the weld drags lip vertices onto the cap's rim and the cap sits where its binding puts
    /// it, so a few end up level with the skin or just under it — and skin a hair proud of a shell reads
    /// in game as a bright patch of bare foot. Well under the push, so this only rescues the strays.
    /// </summary>
    private const float MinSkinClearance = 0.0006f;

    /// <summary>Most a vertex may be lifted to reach that clearance. Past this it is not a straggler and
    /// moving it would distort the surface rather than repair it.</summary>
    private const float MaxSkinLift = 0.0030f;
    /// Largest a connected component may be, as a fraction of the biggest, and still be considered a
    /// toenail patch rather than a foot. The feet run to thousands of triangles; a nail is about a
    /// hundred, so this sits far clear of both.
    /// </summary>
    private const float NailIslandMax = 0.05f;

    /// <summary>How close to the cap's own vertices every vertex of a candidate patch must be before it
    /// counts as sitting under the cap and can be dropped. A nail sits a millimetre or two under it.</summary>
    private const float NailUnderCap = 0.010f;

    /// <summary>Resolution the cap's own footprint is rasterised at when the layer's map isn't square.</summary>
    private const int CapFootprintSize = 512;

    /// <summary>
    /// How many texels the cap's footprint is widened by before it cuts. The hole has to be where the cap
    /// ENDS, not where it IS: cut to the bare footprint and the shell is left standing just inside the
    /// cap's own boundary with nothing to weld to. A painted map supplied that overshoot by being drawn
    /// generously; this is the same overshoot, derived instead of authored, so it is the same on every
    /// body rather than sized for the one the map was drawn against.
    /// </summary>
    private const int CapCutDilate = 0;

    /// <summary>
    /// Grow a mask by <paramref name="steps"/> texels, 8-connected. <paramref name="onAt"/> is what counts
    /// as already set â€” it must match the threshold the CONSUMER reads the mask at, or the growth lands on
    /// texels the consumer already accepted and the whole pass is a no-op. (Dilating the cut mask's 128
    /// against coverage, which AnyVisible reads at CoverageFloor = 8, was exactly that.)
    /// </summary>
    private static byte[] DilateMask(byte[] src, int w, int h, int steps, byte onAt = 128)
    {
        var cur = (byte[])src.Clone();
        for (int s = 0; s < steps; s++)
        {
            var next = (byte[])cur.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (cur[y * w + x] >= onAt) continue;
                    bool near = false;
                    for (int dy = -1; dy <= 1 && !near; dy++)
                        for (int dx = -1; dx <= 1 && !near; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h && cur[ny * w + nx] >= onAt)
                                near = true;
                        }
                    if (near) next[y * w + x] = 255;
                }
            cur = next;
        }
        return cur;
    }

    /// <summary>
    /// Set by a diagnostic to receive each cut mask as it is built â€” name, texels, side. The cut is a UV
    /// mask and every argument about it so far has been made from texel COUNTS, which say nothing about
    /// where a mask actually lands in the atlas. Two masks of similar size can describe completely
    /// different regions, and that is exactly the confusion this exists to end.
    /// </summary>
    internal static Action<string, byte[], int>? MaskDump;


    /// <summary>
    /// Bone tables and submesh bone windows, as the GAME reads them. A modelling package builds its
    /// skin from the mesh bone table alone and ignores the submesh bone map entirely, so a model whose
    /// window is wrong imports perfectly and deforms as garbage in game â€” which is the one failure mode
    /// no offline check has ever been able to see.
    /// </summary>
    /// <summary>
    /// One mesh's per-vertex skinning, resolved to bone names, with the position it sits at.
    /// </summary>
    private static (Vec3 P, (string Bone, float W)[] W)[] ReadMeshSkinning(Source src, int m)
    {
        int mo = src.MeshStart + m * 36;
        ushort vc = BitConverter.ToUInt16(src.S, mo);
        ushort tbl = BitConverter.ToUInt16(src.S, mo + 14);
        var decl = m < src.Decls.Length ? src.Decls[m] : [];
        VElem? pEl = null, wEl = null, iEl = null;
        foreach (var el in decl)
        {
            if (el.Usage == UsePosition) pEl ??= el;
            if (el.Usage == UseBlendWeight) wEl ??= el;
            if (el.Usage == UseBlendIndices) iEl ??= el;
        }
        if (vc == 0 || pEl is not { } pe || wEl is not { } we || iEl is not { } ie
            || tbl >= src.BoneTables.Length)
            return [];

        var table = src.BoneTables[tbl];
        int nInf = BlendCount(we.Type);
        uint[] vOff = { BitConverter.ToUInt32(src.S, mo + 20), BitConverter.ToUInt32(src.S, mo + 24),
                        BitConverter.ToUInt32(src.S, mo + 28) };
        byte[] strides = { src.S[mo + 32], src.S[mo + 33], src.S[mo + 34] };

        var outp = new (Vec3, (string, float)[])[vc];
        Span<float> tmp = stackalloc float[4];
        for (int v = 0; v < vc; v++)
        {
            int pa = (int)(src.Vb + vOff[pe.Stream]) + v * strides[pe.Stream] + pe.Offset;
            ReadTyped(src.S, pa, pe.Type, tmp);
            var p = new Vec3(tmp[0], tmp[1], tmp[2]);

            int wa = (int)(src.Vb + vOff[we.Stream]) + v * strides[we.Stream] + we.Offset;
            int ia = (int)(src.Vb + vOff[ie.Stream]) + v * strides[ie.Stream] + ie.Offset;
            var acc = new Dictionary<string, float>(StringComparer.Ordinal);
            if (wa + nInf <= src.S.Length && ia + nInf <= src.S.Length)
                for (int k = 0; k < nInf; k++)
                {
                    float f = src.S[wa + k] / 255f;
                    if (f <= 0f) continue;
                    int local = src.S[ia + k];
                    string nm = local < table.Length && table[local] < src.BoneNames.Length
                        ? src.BoneNames[table[local]] : $"?{local}";
                    acc[nm] = acc.GetValueOrDefault(nm) + f;
                }
            outp[v] = (p, acc.OrderByDescending(k => k.Value).Select(k => (k.Key, k.Value)).ToArray());
        }
        return outp;
    }

    /// <summary>
    /// The grafted cap's skinning against the cap file it came from, VERTEX BY VERTEX.
    /// <para/>
    /// Comparing per-bone weight TOTALS between the two is not a check: the cap is near-symmetric â€”
    /// <c>iv_asi_oya_b_l</c> and <c>_r</c> both carry 16.8% â€” so a left/right swap, or any per-vertex
    /// permutation that preserves the totals, produces an identical summary. A vertex driven by the
    /// opposite foot's toe bone is perfect in bind pose and ruinous once the toes move, which is
    /// exactly the failure this exists to catch and exactly what the summary cannot see.
    /// <para/>
    /// Matched authored â†’ shipped by nearest position; placement round-trips at a mean of 0.0003, well
    /// inside the mesh's own edge length, so the pairing is unambiguous.
    /// </summary>
    /// <param name="shell">The built shell.</param>
    /// <param name="capMdl">The authored cap the graft was taken from.</param>
    internal static List<string> DiffCapSkinning(byte[] shell, byte[] capMdl)
    {
        var outp = new List<string>();
        Source sh, cp;
        try { sh = Parse(shell); cp = Parse(capMdl); }
        catch (Exception ex) { outp.Add($"cap skinning diff: cannot parse ({ex.Message})"); return outp; }

        // The authored side: every LOD0 mesh the cap has that carries skinning.
        var authored = new List<(Vec3 P, (string Bone, float W)[] W)>();
        for (int m = cp.Lod0MeshIndex; m < cp.Lod0MeshIndex + cp.Lod0MeshCount && m < cp.MeshCount; m++)
            authored.AddRange(ReadMeshSkinning(cp, m));
        if (authored.Count == 0) { outp.Add("cap skinning diff: authored cap has no skinning"); return outp; }

        // The shipped side: the shell's cap meshes are the ones with the cap's vertex count after the
        // seam split, so identify them by bone-table content instead â€” a cap mesh's table is the cap's
        // own bone set, which no body mesh reproduces exactly.
        var capBones = new HashSet<string>(authored.SelectMany(a => a.W).Select(w => w.Bone), StringComparer.Ordinal);
        for (int m = sh.Lod0MeshIndex; m < sh.Lod0MeshIndex + sh.Lod0MeshCount && m < sh.MeshCount; m++)
        {
            var got = ReadMeshSkinning(sh, m);
            if (got.Length == 0) continue;
            var mine = new HashSet<string>(got.SelectMany(g => g.W).Select(w => w.Bone), StringComparer.Ordinal);
            // A cap mesh draws its bones from the cap's set and essentially nothing else. A body mesh
            // that happens to share the toe bones still brings ankle and leg bones with it.
            int shared = mine.Count(b => capBones.Contains(b));
            if (mine.Count == 0 || shared < mine.Count * 0.8) continue;

            int domDiff = 0, setDiff = 0, sideFlip = 0, unmatched = 0;
            float worstMove = 0f;
            var examples = new List<string>();
            foreach (var (ap, aw) in authored)
            {
                if (aw.Length == 0) continue;
                int best = -1;
                float bestD = float.MaxValue;
                for (int v = 0; v < got.Length; v++)
                {
                    float dx = got[v].P.X - ap.X, dy = got[v].P.Y - ap.Y, dz = got[v].P.Z - ap.Z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d < bestD) { bestD = d; best = v; }
                }
                if (best < 0 || bestD > CapDiffMatchRadius * CapDiffMatchRadius) { unmatched++; continue; }
                worstMove = MathF.Max(worstMove, MathF.Sqrt(bestD));

                var bw = got[best].W;
                if (bw.Length == 0) { setDiff++; continue; }
                string a0 = aw[0].Bone, b0 = bw[0].Bone;
                if (a0 != b0)
                {
                    domDiff++;
                    // The one that matters: same bone, opposite foot. Invisible to any summary, and
                    // it renders as the cap tearing off the toe the moment the toes are posed.
                    if (a0.Length > 2 && b0.Length > 2 && a0[..^1] == b0[..^1]
                        && (a0[^1], b0[^1]) is ('l', 'r') or ('r', 'l'))
                        sideFlip++;
                    if (examples.Count < 6)
                        examples.Add($"      ({ap.X:F4},{ap.Y:F4},{ap.Z:F4}) {a0} {aw[0].W:P0} -> {b0} {bw[0].W:P0}");
                }
                var aset = aw.Where(x => x.W > 0.02f).Select(x => x.Bone).OrderBy(x => x, StringComparer.Ordinal);
                var bset = bw.Where(x => x.W > 0.02f).Select(x => x.Bone).OrderBy(x => x, StringComparer.Ordinal);
                if (!aset.SequenceEqual(bset, StringComparer.Ordinal)) setDiff++;
            }

            outp.Add($"cap skinning diff, shell mesh {m}: {authored.Count} authored vertices, "
                   + $"{got.Length} shipped, furthest match {worstMove:F4}");
            outp.Add($"   dominant bone differs: {domDiff}   of those, LEFT/RIGHT FLIPPED: {sideFlip}");
            outp.Add($"   influence set differs: {setDiff}   unmatched beyond {CapDiffMatchRadius:F3}: {unmatched}");
            outp.AddRange(examples);
        }
        if (outp.Count == 0) outp.Add("cap skinning diff: no cap mesh found in the shell");
        return outp;
    }

    /// <summary>How far a shipped cap vertex may sit from its authored one and still be the same vertex.
    /// The graft moves them by the layer push plus the weld, both well under this.</summary>
    private const float CapDiffMatchRadius = 0.02f;

    internal static List<string> DescribeBones(byte[] mdl)
    {
        var src = Parse(mdl);
        var outp = new List<string>
        {
            $"model bones {src.BoneCount}, tables {src.BoneTables.Length}, "
          + $"submesh bone map {src.SubmeshBoneMap.Length}",
        };
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            ushort vc = BitConverter.ToUInt16(src.S, mo);
            ushort subIdx = BitConverter.ToUInt16(src.S, mo + 10);
            ushort subCnt = BitConverter.ToUInt16(src.S, mo + 12);
            ushort tbl = BitConverter.ToUInt16(src.S, mo + 14);
            var names = tbl < src.BoneTables.Length
                ? string.Join(",", src.BoneTables[tbl].Select(b => b < src.BoneNames.Length
                                                                 ? src.BoneNames[b] : $"?{b}"))
                : "(no table)";
            outp.Add($"mesh {m}: {vc} verts, table {tbl} [{(tbl < src.BoneTables.Length ? src.BoneTables[tbl].Length : 0)}] = {names}");

            // Where this mesh's weight actually goes, by bone name. The table can name the right bones
            // and the indices still point at the wrong ones â€” that is invisible in a bone list and it is
            // exactly what drives a mesh to a pose nobody authored.
            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            VElem? wEl = null, iEl = null;
            foreach (var el in decl)
            {
                if (el.Usage == UseBlendWeight) wEl ??= el;
                if (el.Usage == UseBlendIndices) iEl ??= el;
            }
            if (wEl is { } we && iEl is { } ie && tbl < src.BoneTables.Length)
            {
                var table = src.BoneTables[tbl];
                int nInf = BlendCount(we.Type);
                uint[] vOff = { BitConverter.ToUInt32(src.S, mo + 20), BitConverter.ToUInt32(src.S, mo + 24),
                                BitConverter.ToUInt32(src.S, mo + 28) };
                byte[] strides = { src.S[mo + 32], src.S[mo + 33], src.S[mo + 34] };
                var acc = new Dictionary<string, float>(StringComparer.Ordinal);
                for (int v = 0; v < vc; v++)
                {
                    int wa = (int)(src.Vb + vOff[we.Stream]) + v * strides[we.Stream] + we.Offset;
                    int ia = (int)(src.Vb + vOff[ie.Stream]) + v * strides[ie.Stream] + ie.Offset;
                    if (wa + nInf > src.S.Length || ia + nInf > src.S.Length) break;
                    for (int k = 0; k < nInf; k++)
                    {
                        float f = src.S[wa + k] / 255f;
                        if (f <= 0f) continue;
                        int local = src.S[ia + k];
                        string nm = local < table.Length && table[local] < src.BoneNames.Length
                            ? src.BoneNames[table[local]] : $"?{local}";
                        acc[nm] = acc.GetValueOrDefault(nm) + f;
                    }
                }
                float tot = acc.Values.Sum();
                if (tot > 0)
                    outp.Add("      weight: " + string.Join("  ", acc.OrderByDescending(k => k.Value).Take(8)
                        .Select(k => $"{k.Key} {100 * k.Value / tot:0.0}%")));
            }
            for (int s = subIdx; s < subIdx + subCnt; s++)
            {
                int so = src.SubmeshStart + s * 16;
                if (so + 16 > src.S.Length) break;
                ushort bStart = BitConverter.ToUInt16(src.S, so + 12);
                ushort bCount = BitConverter.ToUInt16(src.S, so + 14);
                var win = new List<string>();
                for (int k = bStart; k < bStart + bCount && k < src.SubmeshBoneMap.Length; k++)
                {
                    ushort b = src.SubmeshBoneMap[k];
                    win.Add(b < src.BoneNames.Length ? src.BoneNames[b] : $"?{b}");
                }
                bool overrun = bStart + bCount > src.SubmeshBoneMap.Length;
                outp.Add($"   sub {s}: bone window {bStart}+{bCount}"
                       + (overrun ? "  *** RUNS PAST THE MAP ***" : "")
                       + $" = {string.Join(",", win)}");
            }
        }
        return outp;
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

    /// <summary>
    /// One triangle of body skin, with everything a cap vertex needs to be placed against it or read off
    /// it: geometry, atlas coordinate, normal and skinning at each corner.
    /// </summary>
    /// <summary>
    /// The surface the cap is BOUND to. Skin only, and it has to stay that way.
    /// <para/>
    /// The diagnosis that led here is right: Rue's skin mesh has a HOLE where each toenail sits, so a cap
    /// vertex over a nail measures from the rim of that hole rather than from a surface under it â€” the
    /// offset came out at 0.0085 there against about 0.001 elsewhere, and every one of the worst
    /// placements landed on a toenail. Neolithe never showed it because its skin continues under its
    /// nails. But adding the nail mesh to this set is not the cure: a binding is an ATLAS coordinate, and
    /// the nails carry their own UV island in gear space, so a coordinate measured on a nail is looked up
    /// somewhere unrelated on the body. Measured â€” 8 vertices placed over a metre out, and the round trip
    /// went from exact to a mean of 0.0041.
    /// <para/>
    /// The residual in the bind (version 2) fixes the same defect from the other end: the coordinate
    /// stays in the skin atlas where it transfers, and the difference between what that reconstructs and
    /// what the author modelled is carried alongside it. On the reference body that is exact.
    /// </summary>
    private static List<SkinTri> BindSurface(IReadOnlyList<byte[]> bodies)
        => CollectSkinTriangles(bodies);

    /// <summary>
    /// Look one baked atlas coordinate back up on a body: which point of which triangle it names, the
    /// normal there, the skinning there, and a tangent frame for the surface.
    /// <para/>
    /// Shared by <see cref="BakeCapBind"/> and <see cref="TryPlaceCapFromBind"/> so the bake can predict
    /// exactly what the placement will reconstruct. Two copies of this would drift, and the residual the
    /// bake stores is only a correction if both sides agree to the last bit about where the vertex lands.
    /// </summary>
    /// <returns>How far outside the winning triangle the coordinate fell; 0 means inside it.</returns>
    private static float ResolveBindLanding(IReadOnlyList<SkinTri> tris, float u, float v, int side,
                                            Vec3 face, out Vec3 at, out Vec3 nrm,
                                            out (string Bone, float W)[] w, out Vec3 tan, out Vec3 bit)
    {
        at = default; nrm = default; w = []; tan = default; bit = default;
        float best = float.MaxValue, bestFacing = -2f;
        foreach (var t in tris)
        {
            // Left and right carry their own coordinates, but a body may also mirror them onto each
            // other; the side recorded at bake time keeps the two feet apart either way.
            if (MathF.Sign(t.Ctr.X) != side && t.Ctr.X != 0f) continue;
            var tile = TileOf(t);
            var (ba, bb, bc) = Barycentric2(u + tile.U, v + tile.V, t.Ua, t.Ub, t.Uc);
            // Least-outside wins; among candidates that all contain the coordinate, the one facing the
            // way the skin faced at bake time wins. No early exit: the first triangle to contain a
            // coordinate is not necessarily the right one.
            float outside = MathF.Max(0f, -ba) + MathF.Max(0f, -bb) + MathF.Max(0f, -bc);
            if (outside > best + 1e-6f) continue;

            var n = NormalizeOr(new Vec3(t.Na.X * ba + t.Nb.X * bb + t.Nc.X * bc,
                                         t.Na.Y * ba + t.Nb.Y * bb + t.Nc.Y * bc,
                                         t.Na.Z * ba + t.Nb.Z * bb + t.Nc.Z * bc), default);
            float facing = n.X * face.X + n.Y * face.Y + n.Z * face.Z;
            if (outside > best - 1e-6f && facing <= bestFacing) continue;   // tie: keep the better facing

            best = MathF.Min(best, outside);
            bestFacing = facing;
            at = new Vec3(t.A.X * ba + t.B.X * bb + t.C.X * bc,
                          t.A.Y * ba + t.B.Y * bb + t.C.Y * bc,
                          t.A.Z * ba + t.B.Z * bb + t.C.Z * bc);
            nrm = n;
            w = BlendWeights(t.Wa, ba, t.Wb, bb, t.Wc, bc);
            (tan, bit) = UvFrame(t, n);
        }
        return best;
    }

    /// <summary>
    /// A tangent frame for a triangle, taken from its UV parameterisation rather than its edges. The
    /// residual a cap vertex carries is stored in this frame, so it means the same thing on any body
    /// laid out in the same atlas however that body is posed â€” an edge-derived frame would rotate with
    /// the triangle and put the correction somewhere else on a heeled foot.
    /// </summary>
    private static (Vec3 T, Vec3 B) UvFrame(SkinTri t, Vec3 n)
    {
        float du1 = t.Ub.U - t.Ua.U, dv1 = t.Ub.V - t.Ua.V;
        float du2 = t.Uc.U - t.Ua.U, dv2 = t.Uc.V - t.Ua.V;
        var e1 = new Vec3(t.B.X - t.A.X, t.B.Y - t.A.Y, t.B.Z - t.A.Z);
        var e2 = new Vec3(t.C.X - t.A.X, t.C.Y - t.A.Y, t.C.Z - t.A.Z);
        float det = du1 * dv2 - du2 * dv1;
        Vec3 tan;
        if (MathF.Abs(det) < 1e-12f)
            tan = NormalizeOr(e1, new Vec3(1, 0, 0));
        else
        {
            float rr = 1f / det;
            tan = NormalizeOr(new Vec3((e1.X * dv2 - e2.X * dv1) * rr,
                                       (e1.Y * dv2 - e2.Y * dv1) * rr,
                                       (e1.Z * dv2 - e2.Z * dv1) * rr), e1);
        }
        float d = tan.X * n.X + tan.Y * n.Y + tan.Z * n.Z;      // Gram-Schmidt against the normal
        tan = NormalizeOr(new Vec3(tan.X - n.X * d, tan.Y - n.Y * d, tan.Z - n.Z * d), tan);
        var bitan = new Vec3(n.Y * tan.Z - n.Z * tan.Y,
                             n.Z * tan.X - n.X * tan.Z,
                             n.X * tan.Y - n.Y * tan.X);
        return (tan, bitan);
    }

    private readonly record struct SkinTri(
        Vec3 A, Vec3 B, Vec3 C,
        (float U, float V) Ua, (float U, float V) Ub, (float U, float V) Uc,
        Vec3 Na, Vec3 Nb, Vec3 Nc,
        (string Bone, float W)[] Wa, (string Bone, float W)[] Wb, (string Bone, float W)[] Wc,
        Vec3 Ctr);

    /// <summary>
    /// Every body's LOD0 SKIN triangles in one list, with the toenail islands dropped.
    /// <para/>
    /// TOENAILS ARE NOT A PROJECTION TARGET. They are skin by material, they sit proud of the flesh, and
    /// over the toes they are frequently the NEAREST surface â€” but they carry their own UV island. A cap
    /// triangle with one corner landing on a nail and another on skin then stretches clean across the gap
    /// between two islands and samples whatever lies between, which shows as a jagged transparent band
    /// through the middle of the cap. They are separate connected components, so drop the small ones.
    /// </summary>
    /// <param name="skinOnly">See <see cref="TryReadLod0Geometry"/>.</param>
    /// <param name="dropIslands">
    /// False keeps the small connected components. They are dropped when collecting a surface to take UV
    /// from â€” a toenail is its own island in the atlas and projecting onto one stretches a triangle
    /// across the gap â€” but they must be KEPT when collecting a surface to take skinning from, since a
    /// nail the cap covers is exactly the thing whose bones it needs to follow.
    /// </param>
    private static List<SkinTri> CollectSkinTriangles(IReadOnlyList<byte[]> bodies, bool skinOnly = true,
                                                      bool dropIslands = true, bool nonSkin = false)
    {
        var tri = new List<SkinTri>();
        foreach (var body in bodies)
        {
            if (!TryReadLod0Geometry(body, out var bp, out var bu, out var bt, out var bw, out var bn,
                                     skinOnly, nonSkin))
                continue;

            int nv = bp.Length / 3;
            var parent = new int[nv];
            for (int i = 0; i < nv; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int x, int y) { int rx = Find(x), ry = Find(y); if (rx != ry) parent[rx] = ry; }
            for (int t = 0; t + 2 < bt.Length; t += 3)
            {
                if (bt[t] >= nv || bt[t + 1] >= nv || bt[t + 2] >= nv) continue;
                Union(bt[t], bt[t + 1]); Union(bt[t + 1], bt[t + 2]);
            }
            var size = new Dictionary<int, int>();
            for (int i = 0; i < nv; i++) { int r = Find(i); size[r] = size.GetValueOrDefault(r) + 1; }
            int biggest = 0;
            foreach (int v in size.Values) biggest = Math.Max(biggest, v);
            // A foot is within a fraction of the other foot's size; a nail is a small fraction of either.
            int keepAbove = dropIslands ? (int)(biggest * ProjectIslandFloor) : 0;

            Vec3 P(int i) => new(bp[i * 3], bp[i * 3 + 1], bp[i * 3 + 2]);
            Vec3 N(int i) => i * 3 + 2 < bn.Length ? new Vec3(bn[i * 3], bn[i * 3 + 1], bn[i * 3 + 2]) : default;
            (float, float) U(int i) => (bu[i * 2], bu[i * 2 + 1]);
            (string, float)[] W(int i) => i < bw.Length ? bw[i] : [];

            for (int t = 0; t + 2 < bt.Length; t += 3)
            {
                int a = bt[t], b = bt[t + 1], c = bt[t + 2];
                if ((a + 1) * 3 > bp.Length || (b + 1) * 3 > bp.Length || (c + 1) * 3 > bp.Length) continue;
                if (size.GetValueOrDefault(Find(a)) < keepAbove) continue;
                var (pa, pb, pc) = (P(a), P(b), P(c));
                tri.Add(new SkinTri(pa, pb, pc, U(a), U(b), U(c), N(a), N(b), N(c), W(a), W(b), W(c),
                                    new Vec3((pa.X + pb.X + pc.X) / 3f, (pa.Y + pb.Y + pc.Y) / 3f,
                                             (pa.Z + pb.Z + pc.Z) / 3f)));
            }
        }
        return tri;
    }

    /// <summary>Barycentric coordinate of <paramref name="q"/> in the plane of a triangle.</summary>
    private static (float A, float B, float C) Barycentric(Vec3 q, Vec3 a, Vec3 b, Vec3 c)
    {
        float v0x = b.X - a.X, v0y = b.Y - a.Y, v0z = b.Z - a.Z;
        float v1x = c.X - a.X, v1y = c.Y - a.Y, v1z = c.Z - a.Z;
        float v2x = q.X - a.X, v2y = q.Y - a.Y, v2z = q.Z - a.Z;
        float d00 = v0x * v0x + v0y * v0y + v0z * v0z;
        float d01 = v0x * v1x + v0y * v1y + v0z * v1z;
        float d11 = v1x * v1x + v1y * v1y + v1z * v1z;
        float d20 = v2x * v0x + v2y * v0y + v2z * v0z;
        float d21 = v2x * v1x + v2y * v1y + v2z * v1z;
        float den = d00 * d11 - d01 * d01;
        if (MathF.Abs(den) < 1e-20f) return (1f, 0f, 0f);
        float wb = (d11 * d20 - d01 * d21) / den;
        float wc = (d00 * d21 - d01 * d20) / den;
        return (1f - wb - wc, wb, wc);
    }

    /// <summary>
    /// Three body vertices' skinning combined at a barycentric coordinate, keyed by bone NAME, reduced to
    /// the four slots a vertex has and renormalised. Names because the result is destined for a different
    /// mesh with a different bone table â€” an index would mean something else there.
    /// </summary>
    /// <param name="max">
    /// Influences to keep. Eight where the destination format holds eight (see BlendCount) â€” trimming to
    /// four and hoping is how a body's own skinning gets quietly coarsened on the way through.
    /// </param>
    private static (string Bone, float W)[] BlendWeights(
        (string Bone, float W)[] a, float wa, (string Bone, float W)[] b, float wb,
        (string Bone, float W)[] c, float wc, int max = 8)
    {
        var acc = new Dictionary<string, float>(8);
        void Add((string Bone, float W)[] src, float k)
        {
            if (k <= 0f) return;                       // a clamped barycentric can go slightly negative
            foreach (var (bone, w) in src) acc[bone] = acc.GetValueOrDefault(bone) + w * k;
        }
        Add(a, wa); Add(b, wb); Add(c, wc);
        if (acc.Count == 0) return [];

        var top = acc.OrderByDescending(kv => kv.Value).Take(max).ToArray();
        float sum = top.Sum(kv => kv.Value);
        if (sum <= 0f) return [];
        return top.Select(kv => (kv.Key, kv.Value / sum)).ToArray();
    }

    /// <summary>Area of a triangle â€” what it actually covers, as opposed to how thin it is.</summary>
    private static float TriArea(Vec3 a, Vec3 b, Vec3 c)
    {
        float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
        return 0.5f * MathF.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    private static Vec3 ClosestOnSegment(Vec3 p, Vec3 a, Vec3 b, out float t)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        float len2 = dx * dx + dy * dy + dz * dz;
        if (len2 < 1e-20f) { t = 0f; return a; }
        t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy + (p.Z - a.Z) * dz) / len2;
        t = MathF.Max(0f, MathF.Min(1f, t));
        return new Vec3(a.X + dx * t, a.Y + dy * t, a.Z + dz * t);
    }

    private static Vec3 NormalizeOr(Vec3 v, Vec3 fallback)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len > 1e-6f ? new Vec3(v.X / len, v.Y / len, v.Z / len) : fallback;
    }

    /// <summary>
    /// One segment of a join, carrying everything both sides have to agree on: where it is, which way it
    /// faces, and what it is skinned to. The weights travel with it because moving a vertex onto this
    /// line without moving its skinning produces a weld that only holds in bind pose.
    /// </summary>
    /// <param name="UA">
    /// The cap's own UV at this end, so a lip vertex landing anywhere along the segment can take the
    /// coordinate the cap will sample there. Without it only the handful of lip vertices that coincide
    /// with a rim VERTEX could be corrected — 18 of 215 — and the rest kept reading their own.
    /// </param>
    private readonly record struct RimSeg(
        Vec3 PA, Vec3 NA, (string Bone, float W)[] WA,
        Vec3 PB, Vec3 NB, (string Bone, float W)[] WB,
        (float U, float V) UA = default, (float U, float V) UB = default,
        bool HasUv = false);

    /// <summary>
    /// Put a vertex on the cap's boundary wherever a shell vertex was welded onto it, so the two
    /// boundaries share positions instead of one crossing the middle of the other's edges.
    /// <para/>
    /// A boundary edge belongs to exactly one triangle, so a split is a fan: the triangle keeps its
    /// opposite corner and is replaced by one triangle per sub-segment. Nothing moves â€” the new vertices
    /// sit exactly where the shell already is â€” so this cannot reopen a join or distort the cap.
    /// </summary>
    /// <returns>How many vertices were inserted.</returns>
    /// <param name="atVertex">Landings skipped because the boundary already has a vertex there.</param>
    /// <param name="offBoundary">Landings skipped because no boundary edge was within reach â€” those are
    /// the ones that leave the join open, and they are a different problem from the ones above.</param>
    /// <summary>
    /// Split EVERY open-boundary edge at EVERY given position lying on it, so two runs of boundary that
    /// share vertices end up sharing EDGES as well.
    /// <para/>
    /// This is the inverse of <see cref="SplitCapRim"/> and both are needed. That one asks "which edge is
    /// this landing nearest to", which is right when one boundary is being fitted to another; here both
    /// runs already occupy the same curve, so a landing sits at distance zero on the run it came from and
    /// nearest-edge always picks that one, splitting nothing. Asking the question the other way round —
    /// per edge, which positions lie on me — splits the run that actually needs it.
    /// <para/>
    /// Nothing moves: each inserted vertex is written at the position exactly.
    /// </summary>
    /// <summary>How many of the last stitch's split points reused a vertex already in the mesh.</summary>
    private static int StitchShared;

    private static int StitchBoundaryAt(IReadOnlyList<Vec3> at, VElem[] decl, ref byte[][] streams,
                                        byte[] strides, ref ushort vc, List<ushort[]> keptPerSub,
                                        ref bool[] used)
    {
        var edgeUse = new Dictionary<(ushort A, ushort B), int>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                foreach (var (x, y) in new[] { (sub[t], sub[t + 1]), (sub[t + 1], sub[t + 2]), (sub[t + 2], sub[t]) })
                {
                    var e = x < y ? (x, y) : (y, x);
                    edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1;
                }
        var boundary = edgeUse.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        if (boundary.Count == 0 || at.Count == 0) return 0;

        VElem? pEl = null;
        foreach (var el in decl) if (el.Usage == UsePosition) { pEl = el; break; }
        if (pEl is not { } pe) return 0;

        var posStream = streams[pe.Stream];
        int posStride = strides[pe.Stream];
        Vec3 PosOf(int v)
        {
            Span<float> tmp = stackalloc float[4];
            ReadTyped(posStream, v * posStride + pe.Offset, pe.Type, tmp);
            return new Vec3(tmp[0], tmp[1], tmp[2]);
        }

        var cuts = new Dictionary<(ushort A, ushort B), List<(float T, Vec3 P)>>();
        foreach (var e in boundary)
        {
            var a = PosOf(e.A);
            var b = PosOf(e.B);
            float ex = b.X - a.X, ey = b.Y - a.Y, ez = b.Z - a.Z;
            float len2 = ex * ex + ey * ey + ez * ez;
            if (len2 < 1e-20f) continue;
            float edgeLen = MathF.Sqrt(len2);
            float margin = CapRimSplitMargin / edgeLen;
            foreach (var p in at)
            {
                float t = ((p.X - a.X) * ex + (p.Y - a.Y) * ey + (p.Z - a.Z) * ez) / len2;
                if (t <= margin || t >= 1f - margin) continue;   // an endpoint already, or too close to one
                float qx = a.X + ex * t, qy = a.Y + ey * t, qz = a.Z + ez * t;
                float d2 = (p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy) + (p.Z - qz) * (p.Z - qz);
                if (d2 > CapRimSplitMargin * CapRimSplitMargin) continue;   // not on this edge
                (cuts.TryGetValue(e, out var l) ? l : cuts[e] = new List<(float, Vec3)>()).Add((t, p));
            }
        }
        if (cuts.Count == 0) return 0;

        int newCount = cuts.Sum(kv => kv.Value.Count);
        int baseV = vc;
        for (int st = 0; st < streams.Length; st++)
        {
            if (streams[st] == null || strides[st] == 0) continue;
            var g = new byte[(vc + newCount) * strides[st]];
            Buffer.BlockCopy(streams[st], 0, g, 0, vc * strides[st]);
            streams[st] = g;
        }
        var grownUsed = new bool[vc + newCount];
        Array.Copy(used, grownUsed, vc);
        used = grownUsed;

        // REUSE THE VERTEX THAT IS ALREADY THERE. This is the whole point of the pass and the one thing
        // SplitCapRim cannot do: inserting a NEW vertex at a position the mesh already occupies leaves two
        // indices on one point, so the two runs still do not share the edge between them and both stay
        // open. 3ds Max showed exactly that — two green rings around the cap, one per run, where every
        // position-based measurement here reported the join closed because the rings pair off by position.
        var already = new Dictionary<(int, int, int), ushort>();
        for (ushort i = 0; i < baseV; i++)
        {
            if (!used[i]) continue;
            var q = PosOf(i);
            already[QuantPos(q.X, q.Y, q.Z)] = i;
        }

        int next = baseV;
        int shared = 0;
        var inserted = new Dictionary<(ushort A, ushort B), List<(float T, ushort V)>>();
        foreach (var (e, list) in cuts)
        {
            list.Sort((x, y) => x.T.CompareTo(y.T));
            var made = new List<(float, ushort)>();
            foreach (var (t, p) in list)
            {
                if (already.TryGetValue(QuantPos(p.X, p.Y, p.Z), out ushort reuseAt))
                { made.Add((t, reuseAt)); shared++; continue; }

                ushort nv = (ushort)next++;
                LerpVertex(decl, streams, strides, e.A, e.B, t, nv);
                WriteXYZ(streams[pe.Stream], nv * strides[pe.Stream] + pe.Offset, pe.Type, p.X, p.Y, p.Z);
                used[nv] = true;
                already[QuantPos(p.X, p.Y, p.Z)] = nv;
                made.Add((t, nv));
            }
            inserted[e] = made;
        }
        vc = (ushort)next;
        StitchShared = shared;

        for (int su = 0; su < keptPerSub.Count; su++)
        {
            var sub = keptPerSub[su];
            var outp = new List<ushort>(sub.Length);
            for (int t = 0; t + 2 < sub.Length; t += 3)
            {
                ushort a = sub[t], b = sub[t + 1], c = sub[t + 2];
                bool done = false;
                for (int k = 0; k < 3 && !done; k++)
                {
                    (ushort x, ushort y, ushort opp) = k switch
                    {
                        0 => (a, b, c),
                        1 => (b, c, a),
                        _ => (c, a, b),
                    };
                    var e = x < y ? (A: x, B: y) : (A: y, B: x);
                    if (!inserted.TryGetValue(e, out var pts)) continue;
                    var seq = new List<ushort> { x };
                    if (x == e.A) seq.AddRange(pts.Select(q => q.V));
                    else for (int i = pts.Count - 1; i >= 0; i--) seq.Add(pts[i].V);
                    seq.Add(y);
                    for (int i = 0; i + 1 < seq.Count; i++)
                    { outp.Add(seq[i]); outp.Add(seq[i + 1]); outp.Add(opp); }
                    done = true;
                }
                if (!done) { outp.Add(a); outp.Add(b); outp.Add(c); }
            }
            keptPerSub[su] = outp.ToArray();
        }
        return newCount;
    }

    /// <summary>
    /// Triangulate any open boundary loop of at most <see cref="SmallHoleEdges"/> edges. A handful of
    /// triangles go missing along the join for several small reasons — a collapsed sliver dropped, a
    /// coverage texel landing awkwardly — and each leaves a two-or-three-triangle hole that shows in game
    /// as a bright polygon of bare skin. Chasing every producer one at a time is endless; closing what is
    /// left costs nothing and cannot make a hole worse.
    /// <para/>
    /// Bounded deliberately: the toenail sockets a body carries are 16-20 edges and MUST stay open (the
    /// nail draws through them), the coverage and ankle cuts are hundreds. Only the small strays qualify.
    /// Winding is taken from the triangle that owns each boundary edge and reversed, so a filled hole
    /// faces the same way as the surface around it.
    /// </summary>
    private static int FillSmallHoles(List<ushort[]> keptPerSub, ushort vc, ref bool[] used, int maxEdges)
    {
        var dir = new Dictionary<(ushort, ushort), int>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                    var e = (Math.Min(x, y), Math.Max(x, y));
                    dir[e] = dir.GetValueOrDefault(e) + 1;
                }
        var open = new HashSet<(ushort, ushort)>();
        foreach (var (e, n) in dir) if (n == 1) open.Add(e);
        if (open.Count == 0) return 0;

        // The direction each boundary edge is traversed by the triangle that owns it.
        var next = new Dictionary<ushort, ushort>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                    if (open.Contains((Math.Min(x, y), Math.Max(x, y)))) next[x] = y;
                }

        var seen = new HashSet<ushort>();
        var fill = new List<ushort>();
        int closed = 0;
        foreach (var start in next.Keys.ToList())
        {
            if (seen.Contains(start)) continue;
            var loop = new List<ushort>();
            ushort at = start;
            while (loop.Count <= maxEdges + 1)
            {
                if (!next.TryGetValue(at, out ushort nx)) { loop.Clear(); break; }
                loop.Add(at);
                if (nx == start) break;
                at = nx;
            }
            if (loop.Count < 3 || loop.Count > maxEdges) continue;
            if (loop.Any(seen.Contains)) continue;
            foreach (var v in loop) seen.Add(v);
            // Reversed against the owning triangles, so the patch faces outward like its neighbours.
            for (int i = 1; i + 1 < loop.Count; i++)
            { fill.Add(loop[0]); fill.Add(loop[i + 1]); fill.Add(loop[i]); }
            closed++;
        }
        if (fill.Count == 0) return 0;

        int host = 0;
        for (int su = 1; su < keptPerSub.Count; su++)
            if (keptPerSub[su].Length > keptPerSub[host].Length) host = su;
        var grown = new List<ushort>(keptPerSub[host]);
        grown.AddRange(fill);
        keptPerSub[host] = grown.ToArray();
        foreach (var v in fill) if (v < used.Length) used[v] = true;
        return closed;
    }

    /// <summary>Nearest point on the body's skin, and how far away it is. Null when nothing is in reach.</summary>
    private static bool NearestOnSkin(Vec3 p, List<SkinTri> tris, float reach, out Vec3 at)
    {
        float best = reach * reach;
        at = default;
        bool got = false;
        foreach (var t in tris)
        {
            float cx = t.Ctr.X - p.X, cy = t.Ctr.Y - p.Y, cz = t.Ctr.Z - p.Z;
            if (cx * cx + cy * cy + cz * cz > best + 0.01f) continue;
            var q = ClosestOnTriangle(p, t.A, t.B, t.C);
            float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d >= best) continue;
            best = d; at = q; got = true;
        }
        return got;
    }

    /// <summary>
    /// A position as an exact-match key. 1e-6 of a metre — finer than any coordinate the writer produces
    /// deliberately, coarse enough to absorb the last bit of a float. Two vertices with the same key are
    /// the same point and the graft gives them one index.
    /// </summary>
    private static (int, int, int) QuantPos(float x, float y, float z)
        => ((int)MathF.Round(x * 1e6f), (int)MathF.Round(y * 1e6f), (int)MathF.Round(z * 1e6f));

    /// <summary>
    /// Append one vertex to every stream and return its index, copied byte-for-byte from
    /// <paramref name="template"/> so that everything the caller does NOT overwrite still holds a sane
    /// value. Left zeroed, a grafted vertex takes vertex colour 0 — and the gear shaders gate on it, so
    /// the whole cap renders as nothing at all — plus a zero tangent frame and, if the skinning write is
    /// ever skipped, zero bone weights, which collapse the vertex onto the model origin. The same reason
    /// LerpVertex copies its nearer endpoint before interpolating anything.
    /// </summary>
    private static ushort GrowOne(ref byte[][] streams, byte[] strides, ref ushort vc, ref bool[] used,
                                  ushort template)
    {
        for (int st = 0; st < streams.Length; st++)
        {
            if (streams[st] == null || strides[st] == 0) continue;
            var g = new byte[(vc + 1) * strides[st]];
            Buffer.BlockCopy(streams[st], 0, g, 0, vc * strides[st]);
            if (template < vc)
                Buffer.BlockCopy(g, template * strides[st], g, vc * strides[st], strides[st]);
            streams[st] = g;
        }
        var u = new bool[vc + 1];
        Array.Copy(used, u, vc);
        used = u;
        used[vc] = true;
        return vc++;
    }

    /// <summary>
    /// Write one vertex's skinning from bone NAMES into a mesh's own table, growing the table on demand.
    /// The same route the welded lip takes, so a merged mesh is served by a single table.
    /// </summary>
    private static void WriteSkinNamed(byte[][] streams, byte[] strides, VElem wEl, VElem iEl, ushort v,
                                       (string Bone, float W)[] w, Dictionary<string, int> slot,
                                       List<ushort> table, Dictionary<string, ushort> boneIndex)
    {
        if (w.Length == 0) return;
        int nInf = BlendCount(wEl.Type);
        Span<byte> wb = stackalloc byte[8], ib = stackalloc byte[8];
        wb.Clear(); ib.Clear();
        int used = 0, total = 0;
        foreach (var (bone, f) in w)
        {
            if (used == nInf) break;
            if (!slot.TryGetValue(bone, out int at))
            {
                if (!boneIndex.TryGetValue(bone, out var ui)) continue;
                if (table.Count >= 255) continue;
                slot[bone] = at = table.Count;
                table.Add(ui);
            }
            byte q = (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
            if (q == 0) continue;
            ib[used] = (byte)at; wb[used] = q; total += q;
            used++;
        }
        if (used == 0) return;
        // The bytes must come to 255 or the vertex shrinks toward the origin.
        wb[0] = (byte)Math.Clamp(wb[0] + (255 - total), 0, 255);
        int wo = v * strides[wEl.Stream] + wEl.Offset;
        int io = v * strides[iEl.Stream] + iEl.Offset;
        for (int q2 = 0; q2 < nInf; q2++)
        {
            streams[wEl.Stream][wo + q2] = wb[q2];
            streams[iEl.Stream][io + q2] = ib[q2];
        }
    }

    /// <summary>
    /// After a split: how many of the landings still have no boundary vertex exactly on them. The target
    /// is zero on both sides of the join — that is what "the two meshes meet at vertices" means, and it
    /// is the one property none of the surface-distance measurements can see. A T-junction leaves a
    /// sliver, and a sliver is a line.
    /// </summary>
    private static void JoinAudit(string what, List<Vec3> landings, List<ushort[]> keptPerSub,
                                  VElem[] decl, byte[][] streams, byte[] strides, ushort vc,
                                  Action<string>? diag)
    {
        if (diag == null || landings.Count == 0) return;
        VElem? pEl = null;
        foreach (var el in decl) if (el.Usage == UsePosition) { pEl = el; break; }
        if (pEl is not { } pe) return;

        var uses = new Dictionary<(ushort, ushort), int>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                    var e = (Math.Min(x, y), Math.Max(x, y));
                    uses[e] = uses.GetValueOrDefault(e) + 1;
                }
        var onEdge = new HashSet<ushort>();
        foreach (var (e, n) in uses)
            if (n == 1) { onEdge.Add(e.Item1); onEdge.Add(e.Item2); }

        Span<float> tmp = stackalloc float[4];
        var pts = new List<Vec3>(onEdge.Count);
        foreach (var v in onEdge)
        {
            if (v >= vc) continue;
            ReadTyped(streams[pe.Stream], v * strides[pe.Stream] + pe.Offset, pe.Type, tmp);
            pts.Add(new Vec3(tmp[0], tmp[1], tmp[2]));
        }

        int missing = 0;
        float worst = 0f;
        foreach (var land in landings)
        {
            float best = float.MaxValue;
            foreach (var p in pts) best = MathF.Min(best, Dist(land, p));
            if (best <= 1e-6f) continue;
            // Past the reach it was never this boundary's landing to match.
            if (best > CapRimSplitReach) continue;
            missing++;
            worst = MathF.Max(worst, best);
        }
        diag?.Invoke(missing == 0
            ? $"join audit [{what}]: every one of {landings.Count} landing(s) has a vertex on it"
            : $"join audit [{what}]: {missing} of {landings.Count} landing(s) have NO vertex on them "
              + $"(worst {worst:F6}) — each is a T-junction");
    }

    private static int SplitCapRim(List<Vec3> landings, VElem[] decl, ref byte[][] streams,
                                   byte[] strides, ref ushort vc, List<ushort[]> keptPerSub,
                                   ref bool[] used, out int atVertex, out int offBoundary)
    {
        atVertex = 0; offBoundary = 0;
        // Boundary edges of what this mesh is actually emitting, with the triangle each belongs to.
        var edgeUse = new Dictionary<(ushort A, ushort B), int>();
        foreach (var sub in keptPerSub)
            for (int t = 0; t + 2 < sub.Length; t += 3)
                foreach (var (x, y) in new[] { (sub[t], sub[t + 1]), (sub[t + 1], sub[t + 2]), (sub[t + 2], sub[t]) })
                {
                    var e = x < y ? (x, y) : (y, x);
                    edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1;
                }
        var boundary = edgeUse.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        if (boundary.Count == 0) return 0;

        VElem? pEl = null;
        foreach (var el in decl) if (el.Usage == UsePosition) { pEl = el; break; }
        if (pEl is not { } pe) return 0;

        var posStream = streams[pe.Stream];
        int posStride = strides[pe.Stream];
        Vec3 PosOf(int v)
        {
            Span<float> tmp = stackalloc float[4];
            ReadTyped(posStream, v * posStride + pe.Offset, pe.Type, tmp);
            return new Vec3(tmp[0], tmp[1], tmp[2]);
        }

        // Each landing against the boundary edge it sits on, as a parameter along that edge. A landing at
        // an endpoint is not split at — the vertex is MOVED onto it instead; see the snap below.
        var cuts = new Dictionary<(ushort A, ushort B), List<(float T, Vec3 P)>>();
        // Boundary vertices already pulled onto a landing. First landing wins: a second one arriving at
        // the same vertex must not drag it somewhere else, so it falls through to a real split.
        var snapped = new HashSet<ushort>();
        foreach (var land in landings)
        {
            (ushort A, ushort B) bestE = default;
            float bestD2 = float.MaxValue, bestT = 0f;
            foreach (var e in boundary)
            {
                var a = PosOf(e.A);
                var b = PosOf(e.B);
                float ex = b.X - a.X, ey = b.Y - a.Y, ez = b.Z - a.Z;
                float len = ex * ex + ey * ey + ez * ez;
                if (len < 1e-20f) continue;
                float t = Math.Clamp(((land.X - a.X) * ex + (land.Y - a.Y) * ey + (land.Z - a.Z) * ez) / len, 0f, 1f);
                float qx = a.X + ex * t, qy = a.Y + ey * t, qz = a.Z + ez * t;
                float d2 = (land.X - qx) * (land.X - qx) + (land.Y - qy) * (land.Y - qy) + (land.Z - qz) * (land.Z - qz);
                if (d2 >= bestD2) continue;
                bestD2 = d2; bestE = e; bestT = t;
            }
            if (bestD2 > CapRimSplitReach * CapRimSplitReach) { offBoundary++; continue; }
            // Already a shared vertex, or close enough to one that a split would make a sliver.
            float edgeLen = Dist(PosOf(bestE.A), PosOf(bestE.B));
            if (edgeLen < 1e-6f) { offBoundary++; continue; }
            float margin = CapRimSplitMargin / edgeLen;
            if (bestT <= margin || bestT >= 1f - margin)
            {
                // SNAP, don't skip. This used to decline the landing as "already on a vertex", which is
                // only true to within CapRimSplitMargin — and a fifth of a millimetre short of shared IS
                // a T-junction, with a sliver at it. Measured on layer 0, this branch alone accounted for
                // 18 of the shell's unmatched rim vertices and 17 of the cap's.
                //
                // Moving the vertex the last fraction is not the wholesale lip-snapping recorded as
                // collapsing triangles: that pulled every lip vertex to its nearest rim vertex, however
                // far. This one is bounded by the margin, so nothing travels further than the error it
                // is removing.
                ushort at = bestT <= margin ? bestE.A : bestE.B;
                if (snapped.Add(at))
                {
                    WriteXYZ(streams[pe.Stream], at * strides[pe.Stream] + pe.Offset, pe.Type,
                             land.X, land.Y, land.Z);
                    atVertex++;
                    continue;
                }
                // That vertex is already serving another landing — fall through and split properly.
            }
            (cuts.TryGetValue(bestE, out var l) ? l : cuts[bestE] = new List<(float, Vec3)>()).Add((bestT, land));
        }
        if (cuts.Count == 0) return 0;

        // Grow every stream by the number of vertices about to be inserted, then fill each by
        // interpolating its edge's endpoints. Blend indices and weights are taken from the NEARER
        // endpoint rather than mixed: two adjacent rim vertices can name different bones, and averaging
        // index bytes produces a bone nobody asked for.
        int newCount = cuts.Sum(kv => kv.Value.Count);
        int baseV = vc;
        for (int st = 0; st < streams.Length; st++)
        {
            if (streams[st] == null || strides[st] == 0) continue;
            var g = new byte[(vc + newCount) * strides[st]];
            Buffer.BlockCopy(streams[st], 0, g, 0, vc * strides[st]);
            streams[st] = g;
        }
        var grownUsed = new bool[vc + newCount];
        Array.Copy(used, grownUsed, vc);
        used = grownUsed;

        int next = baseV;
        var inserted = new Dictionary<(ushort A, ushort B), List<(float T, ushort V)>>();
        foreach (var (e, list) in cuts)
        {
            list.Sort((x, y) => x.T.CompareTo(y.T));
            var made = new List<(float, ushort)>();
            foreach (var (t, p) in list)
            {
                ushort nv = (ushort)next++;
                LerpVertex(decl, streams, strides, e.A, e.B, t, nv);
                // The position is the shell's landing exactly, not the interpolation â€” that is the whole
                // point, and the two differ by however far the shell's rim bows off the chord.
                WriteXYZ(streams[pe.Stream], nv * strides[pe.Stream] + pe.Offset, pe.Type, p.X, p.Y, p.Z);
                used[nv] = true;
                made.Add((t, nv));
            }
            inserted[e] = made;
        }
        vc = (ushort)(baseV + newCount);

        // Re-fan every triangle that owns a split edge.
        for (int su = 0; su < keptPerSub.Count; su++)
        {
            var sub = keptPerSub[su];
            var outp = new List<ushort>(sub.Length);
            for (int t = 0; t + 2 < sub.Length; t += 3)
            {
                ushort a = sub[t], b = sub[t + 1], c = sub[t + 2];
                bool done = false;
                for (int k = 0; k < 3 && !done; k++)
                {
                    (ushort x, ushort y, ushort opp) = k switch
                    {
                        0 => (a, b, c),
                        1 => (b, c, a),
                        _ => (c, a, b),
                    };
                    var e = x < y ? (A: x, B: y) : (A: y, B: x);
                    if (!inserted.TryGetValue(e, out var pts)) continue;
                    // Walk the edge in the triangle's own winding so the fan keeps its facing.
                    var seq = new List<ushort> { x };
                    if (x == e.A) seq.AddRange(pts.Select(p => p.V));
                    else for (int i = pts.Count - 1; i >= 0; i--) seq.Add(pts[i].V);
                    seq.Add(y);
                    for (int i = 0; i + 1 < seq.Count; i++)
                    { outp.Add(seq[i]); outp.Add(seq[i + 1]); outp.Add(opp); }
                    done = true;
                }
                if (!done) { outp.Add(a); outp.Add(b); outp.Add(c); }
            }
            keptPerSub[su] = outp.ToArray();
        }
        return newCount;
    }

    /// <summary>Write vertex <paramref name="dst"/> as the interpolation of <paramref name="va"/> and
    /// <paramref name="vb"/>, attribute by attribute as the declaration describes them.</summary>
    private static void LerpVertex(VElem[] decl, byte[][] streams, byte[] strides,
                                   ushort va, ushort vb, float t, ushort dst)
    {
        // Start from the nearer endpoint, so anything not explicitly interpolated below â€” blend indices,
        // blend weights, colour â€” arrives as a coherent set rather than a mix of two.
        ushort near = t < 0.5f ? va : vb;
        for (int st = 0; st < streams.Length; st++)
        {
            if (streams[st] == null || strides[st] == 0) continue;
            Buffer.BlockCopy(streams[st], near * strides[st], streams[st], dst * strides[st], strides[st]);
        }

        Span<float> A = stackalloc float[4], B = stackalloc float[4];
        foreach (var el in decl)
        {
            if (el.Usage is not (UsePosition or UseNormal or UseUV)) continue;
            var s = streams[el.Stream];
            if (s == null) continue;
            ReadTyped(s, va * strides[el.Stream] + el.Offset, el.Type, A);
            ReadTyped(s, vb * strides[el.Stream] + el.Offset, el.Type, B);
            float x = A[0] + (B[0] - A[0]) * t, y = A[1] + (B[1] - A[1]) * t, z = A[2] + (B[2] - A[2]) * t;
            int off = dst * strides[el.Stream] + el.Offset;
            switch (el.Usage)
            {
                case UsePosition: WriteXYZ(s, off, el.Type, x, y, z); break;
                case UseNormal:
                {
                    var n = NormalizeOr(new Vec3(x, y, z), new Vec3(A[0], A[1], A[2]));
                    WriteNormal(s, off, el.Type, n.X, n.Y, n.Z);
                    break;
                }
                case UseUV:
                    WriteUV2(s, off, el.Type is 13 or 14, x, y);
                    break;
            }
        }
    }

    /// <summary>How far a welded landing may sit from a cap boundary edge and still be treated as on it.</summary>
    private const float CapRimSplitReach = 0.004f;

    /// <summary>How close to an existing rim vertex a landing must be before splitting is pointless â€” a
    /// split there would only make a sliver, and the vertex it would share is already there.</summary>
    private const float CapRimSplitMargin = 2e-4f;

    /// <summary>
    /// Nearest point on a rim, with the normal and the skinning interpolated along the segment it lands
    /// on. False when nothing is within <paramref name="radius"/>.
    /// </summary>
    private static bool NearestOnRim(Vec3 p, RimSeg[] rim, float radius,
                                     out Vec3 at, out Vec3 normal, out (string Bone, float W)[] weights,
                                     out float dist)
        => NearestOnRim(p, rim, radius, out at, out normal, out weights, out dist, out _);

    /// <inheritdoc cref="NearestOnRim(Vec3, RimSeg[], float, out Vec3, out Vec3, out (string, float)[], out float)"/>
    /// <param name="uv">
    /// The cap's UV at the landing, interpolated along the same segment at the same parameter as the
    /// normal, or null on a segment whose ends sit on the body's atlas seam and therefore have no single
    /// coordinate to offer.
    /// </param>
    private static bool NearestOnRim(Vec3 p, RimSeg[] rim, float radius,
                                     out Vec3 at, out Vec3 normal, out (string Bone, float W)[] weights,
                                     out float dist, out (float U, float V)? uv)
    {
        at = default; normal = default; weights = []; uv = null;
        float best = float.MaxValue;

        // Always the nearest POINT of a segment, never the nearest rim VERTEX. Preferring vertices was
        // tried, to stop an edge cutting a corner off, and it is the wrong tool: several vertices land on
        // the same one, and on the cap side that collapsed rim triangles to zero area outright (aspect
        // 3e9) and took the whole shell from manifold to 21 bad edges and 36 winding errors. The corner
        // problem it was aimed at turned out to be unwelded boundary instead â€” see WeldRounds.
        int win = -1;
        float winT = 0f;
        for (int i = 0; i < rim.Length; i++)
        {
            var q = ClosestOnSegment(p, rim[i].PA, rim[i].PB, out float t);
            float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d >= best) continue;
            best = d; win = i; winT = t; at = q;
        }
        if (win < 0) { dist = float.MaxValue; return false; }

        // ...but a landing that is ALREADY at an end of its segment takes the end exactly. This is not the
        // vertex-preference above â€” the vertex has to be within CapRimSplitMargin, so the correction is
        // under a fifth of a millimetre and cannot drag anything to a distant rim vertex. It is here
        // because the alternative is worse: a landing a hair off a rim vertex is too close to split the
        // boundary at (the split would only make a sliver, so both sides decline it) and too far to share
        // a position with, and what is left is a T-junction of exactly the size this margin allows.
        // Measured on Rue, that was the whole residual â€” 5 pairs from 4.6e-5 to 1.3e-4 apart.
        var seg2 = rim[win];
        if (Dist(at, seg2.PA) <= CapRimSplitMargin) { at = seg2.PA; winT = 0f; }
        else if (Dist(at, seg2.PB) <= CapRimSplitMargin) { at = seg2.PB; winT = 1f; }

        var (na, nb) = (seg2.NA, seg2.NB);
        normal = NormalizeOr(new Vec3(na.X + (nb.X - na.X) * winT, na.Y + (nb.Y - na.Y) * winT,
                                      na.Z + (nb.Z - na.Z) * winT), na);
        weights = BlendWeights(seg2.WA, 1f - winT, seg2.WB, winT, [], 0f);
        if (seg2.HasUv)
            uv = (seg2.UA.U + (seg2.UB.U - seg2.UA.U) * winT,
                  seg2.UA.V + (seg2.UB.V - seg2.UA.V) * winT);
        dist = Dist(p, at);
        return dist <= radius;
    }

    private static Vec3 ClosestOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
    {
        static float Dot(Vec3 u, Vec3 v) => u.X * v.X + u.Y * v.Y + u.Z * v.Z;
        static Vec3 Sub(Vec3 u, Vec3 v) => new(u.X - v.X, u.Y - v.Y, u.Z - v.Z);
        static Vec3 Add(Vec3 u, Vec3 v, float s) => new(u.X + v.X * s, u.Y + v.Y * s, u.Z + v.Z * s);

        Vec3 ab = Sub(b, a), ac = Sub(c, a), ap = Sub(p, a);
        float d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;

        Vec3 bp = Sub(p, b);
        float d3 = Dot(ab, bp), d4 = Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;

        float vc2 = d1 * d4 - d3 * d2;
        if (vc2 <= 0 && d1 >= 0 && d3 <= 0) return Add(a, ab, d1 / (d1 - d3));

        Vec3 cp = Sub(p, c);
        float d5 = Dot(ab, cp), d6 = Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return Add(a, ac, d2 / (d2 - d6));

        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return Add(b, Sub(c, b), (d4 - d3) / (d4 - d3 + (d5 - d6)));

        float den = 1f / (va + vb + vc2);
        return Add(Add(a, ab, vb * den), ac, vc2 * den);
    }

    /// <summary>Nearest not-yet-claimed node to a target point, or -1 when the region is exhausted.</summary>
    private static int NearestFree(List<int> pool, Vec3[] pos, HashSet<int> taken, Vec3 p)
    {
        int best = -1;
        float bestD = float.MaxValue;
        foreach (int n in pool)
        {
            if (taken.Contains(n)) continue;
            float dx = pos[n].X - p.X, dy = pos[n].Y - p.Y, dz = pos[n].Z - p.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d < bestD) { bestD = d; best = n; }
        }
        return best;
    }

    /// <summary>Distance from an interior point to the hull boundary along a unit direction.</summary>
    private static float HullRadius((float X, float Y)[] hull, (float X, float Y) c, float dx, float dy)
    {
        float best = 0;
        for (int i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            float ex = b.X - a.X, ey = b.Y - a.Y;
            float den = dx * ey - dy * ex;
            if (MathF.Abs(den) < 1e-12f) continue;
            float t = ((a.X - c.X) * ey - (a.Y - c.Y) * ex) / den;
            float u = ((a.X - c.X) * dy - (a.Y - c.Y) * dx) / den;
            if (t > 0 && u >= -1e-6f && u <= 1 + 1e-6f) best = MathF.Max(best, t);
        }
        return best;
    }

    /// <summary>
    /// The mesh's triangle list as the cap leaves it â€” the source triangles it did not cut out, plus the
    /// ones it built. Vertex normals must be averaged over THIS, not the source list, or the cap is
    /// shaded by the toes it replaced.
    /// </summary>
    private static ushort[] CappedTopology(ToeCapPlan plan, ushort[] tris)
    {
        var kept = new List<ushort>(tris.Length);
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            ushort a = tris[t], b = tris[t + 1], c = tris[t + 2];
            if (a >= plan.NodeOf.Length || b >= plan.NodeOf.Length || c >= plan.NodeOf.Length) continue;
            if (plan.IsCut(a, b, c)) continue;
            kept.Add(a); kept.Add(b); kept.Add(c);
        }
        foreach (var (a, b, c) in plan.NewTriangles) { kept.Add(a); kept.Add(b); kept.Add(c); }
        return kept.ToArray();
    }

    /// <summary>
    /// Did the cap collapse this triangle? Only triangles it actually moved are eligible, so an
    /// uncapped shell can never lose geometry to this test.
    /// </summary>
    private static bool CapDegenerate(Vec3[] src, Vec3[] def, ushort a, ushort b, ushort c)
    {
        if (a >= def.Length || b >= def.Length || c >= def.Length) return false;

        static float Dist2(Vec3 p, Vec3 q)
        {
            float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            return dx * dx + dy * dy + dz * dz;
        }
        static float Cross2(Vec3 p, Vec3 q, Vec3 r)
        {
            float ux = q.X - p.X, uy = q.Y - p.Y, uz = q.Z - p.Z;
            float wx = r.X - p.X, wy = r.Y - p.Y, wz = r.Z - p.Z;
            float x = uy * wz - uz * wy, y = uz * wx - ux * wz, z = ux * wy - uy * wx;
            return x * x + y * y + z * z;   // = (2*area)^2
        }

        float eps2 = DegenerateMoveEpsilon * DegenerateMoveEpsilon;
        if (Dist2(src[a], def[a]) <= eps2 && Dist2(src[b], def[b]) <= eps2 && Dist2(src[c], def[c]) <= eps2)
            return false;   // the cap never touched this one

        float weld2 = DegenerateWeldDistance * DegenerateWeldDistance;
        if (Dist2(def[a], def[b]) <= weld2 || Dist2(def[b], def[c]) <= weld2 || Dist2(def[a], def[c]) <= weld2)
            return true;

        return Cross2(def[a], def[b], def[c])
             <= DegenerateAreaFraction * DegenerateAreaFraction * Cross2(src[a], src[b], src[c]);
    }

    /// <summary>
    /// Vertex normals recomputed from the CAPPED surface, blended back to the original by mask weight.
    /// <para/>
    /// Without this the whole cap is invisible: the vertex streams are a raw byte copy, so every normal
    /// still describes the toe it was cut from, and the shell shades as five separate toes however far
    /// the geometry moved. It also fixes the push â€” <c>position += normal * push</c> along a stale
    /// sidewall normal drives the two halves of a bridged gap apart instead of offsetting them together.
    /// <para/>
    /// Faces contribute their unnormalized cross product, so area weights itself and the slivers left
    /// where a gap closed contribute almost nothing. Normals are accumulated per WELDED NODE and every
    /// copy of a node gets the same answer, so UV seams inside the cap don't crack.
    /// </summary>
    private static Vec3[] CapNormals(Vec3[] basePos, Vec3[] baseNrm, ToeCapPlan plan, ushort[] tris)
    {
        int vc = basePos.Length;
        var nodeOf = plan.NodeOf;
        int nodeCount = plan.NodeWeight.Length;

        var def = new Vec3[vc];
        for (int i = 0; i < vc; i++)
            def[i] = new Vec3(basePos[i].X + plan.Delta[i].X, basePos[i].Y + plan.Delta[i].Y, basePos[i].Z + plan.Delta[i].Z);

        // Deduped by node triple: capTris spans every submesh of the mesh, including the duplicate
        // variant the connector filter drops later, and a doubled face would skew the average.
        var accum = new Vec3[nodeCount];
        var seenFace = new HashSet<(int, int, int)>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            ushort ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
            if (ia >= vc || ib >= vc || ic >= vc) continue;
            int na = nodeOf[ia], nb = nodeOf[ib], nc = nodeOf[ic];
            if (na == nb || nb == nc || na == nc) continue;

            int s0 = Math.Min(na, Math.Min(nb, nc)), s2 = Math.Max(na, Math.Max(nb, nc));
            if (!seenFace.Add((s0, na + nb + nc - s0 - s2, s2))) continue;

            float ux = def[ib].X - def[ia].X, uy = def[ib].Y - def[ia].Y, uz = def[ib].Z - def[ia].Z;
            float wx = def[ic].X - def[ia].X, wy = def[ic].Y - def[ia].Y, wz = def[ic].Z - def[ia].Z;
            float cxp = uy * wz - uz * wy, cyp = uz * wx - ux * wz, czp = ux * wy - uy * wx;
            if (cxp * cxp + cyp * cyp + czp * czp <= 1e-24f) continue;   // collapsed: no direction to give

            foreach (int n in stackalloc[] { na, nb, nc })
                accum[n] = new Vec3(accum[n].X + cxp, accum[n].Y + cyp, accum[n].Z + czp);
        }

        // Winding is not guaranteed here. Getting it backwards shades the cap inside out AND makes the
        // push drive the shell into the body, so decide it once from the source normals we trust.
        float agree = 0;
        for (int n = 0; n < nodeCount; n++)
            if (plan.NodeWeight[n] > 0f)
                agree += accum[n].X * plan.NodeNormal[n].X + accum[n].Y * plan.NodeNormal[n].Y + accum[n].Z * plan.NodeNormal[n].Z;
        float sign = agree < 0f ? -1f : 1f;

        var outN = new Vec3[vc];
        for (int i = 0; i < vc; i++)
        {
            int n = nodeOf[i];
            float w = plan.NodeWeight[n];
            if (w <= 0f) { outN[i] = baseNrm[i]; continue; }   // untouched: original bytes must survive

            var a = accum[n];
            var fresh = Normalize(new Vec3(a.X * sign, a.Y * sign, a.Z * sign)) ?? plan.NodeNormal[n];
            var src = plan.NodeNormal[n];

            // Blend against the NODE-averaged source normal, not this vertex's own, so welded copies
            // land on identical bytes; the weight fade rejoins the untouched shell without a crease.
            outN[i] = Normalize(new Vec3(
                src.X + (fresh.X - src.X) * w,
                src.Y + (fresh.Y - src.Y) * w,
                src.Z + (fresh.Z - src.Z) * w)) ?? baseNrm[i];
        }
        return outN;
    }

    /// <summary>
    /// Encode a unit normal into a vertex element of the given type, leaving any 4th component (often
    /// handedness or an occlusion term) intact. False when the type has no room for three components or
    /// no defined scale to encode into â€” the caller then leaves the original bytes and says so.
    /// <para/>
    /// Deliberately NOT <see cref="WriteXYZ"/>: that one writes a Half2 by dropping z, which is a fine
    /// partial write for a position and silent corruption for a normal.
    /// </summary>
    internal static bool WriteNormal(byte[] a, int off, byte type, float x, float y, float z)
    {
        switch (type)
        {
            case 2: case 3:   // Float3 / Float4
                W32(a, off, (uint)BitConverter.SingleToInt32Bits(x));
                W32(a, off + 4, (uint)BitConverter.SingleToInt32Bits(y));
                W32(a, off + 8, (uint)BitConverter.SingleToInt32Bits(z));
                return true;
            case 14:          // Half4
                W16(a, off, Half(x)); W16(a, off + 2, Half(y)); W16(a, off + 4, Half(z));
                return true;
            case 10:          // Short4n
                W16(a, off,     (ushort)(short)Math.Clamp(MathF.Round(x * 32767f), -32767f, 32767f));
                W16(a, off + 2, (ushort)(short)Math.Clamp(MathF.Round(y * 32767f), -32767f, 32767f));
                W16(a, off + 4, (ushort)(short)Math.Clamp(MathF.Round(z * 32767f), -32767f, 32767f));
                return true;
            case 8:           // Ubyte4n â€” inverts ReadTyped's /255 AND BuildVerbatim's *2-1 unbias
                a[off]     = (byte)Math.Clamp(MathF.Round((x * 0.5f + 0.5f) * 255f), 0f, 255f);
                a[off + 1] = (byte)Math.Clamp(MathF.Round((y * 0.5f + 0.5f) * 255f), 0f, 255f);
                a[off + 2] = (byte)Math.Clamp(MathF.Round((z * 0.5f + 0.5f) * 255f), 0f, 255f);
                return true;
            default:
                return false;   // 9/13 have no z; 5/6/7/16/17 have no normalized scale
        }
    }

    /// <summary>
    /// Group coincident vertices into shared nodes, returning each vertex's node index. A body mesh
    /// splits vertices at UV seams and hard edges; the cap's displacement and its normals must both be
    /// decided per NODE, or two copies of the same point drift apart and the surface cracks open along
    /// the seam. One function so that grouping is structurally identical in both passes.
    /// </summary>
    private static int[] WeldByPosition(Vec3[] pos, out int nodeCount)
    {
        var nodeOf = new int[pos.Length];
        var byPos = new Dictionary<(int, int, int), int>(pos.Length);
        nodeCount = 0;
        for (int i = 0; i < pos.Length; i++)
        {
            var key = ((int)MathF.Round(pos[i].X * 1e5f), (int)MathF.Round(pos[i].Y * 1e5f), (int)MathF.Round(pos[i].Z * 1e5f));
            if (!byPos.TryGetValue(key, out int n)) byPos[key] = n = nodeCount++;
            nodeOf[i] = n;
        }
        return nodeOf;
    }

    /// <summary>
    /// UVs for an authored mesh that has none, taken from the body it is grafted onto: drop each vertex
    /// onto the nearest skin triangle and interpolate that triangle's coordinate where it lands.
    /// <para/>
    /// This is what carries the overlay's texture â€” and its alpha â€” across the cap, continued over the
    /// gaps between the toes from the flanks either side. Unwrapping the cap into fresh UV space
    /// instead would be worse than useless: the overlays are painted in the BODY's layout and have
    /// nothing anywhere else, so the cap would sample empty texture.
    /// </summary>
    private static CapUvPlan? ProjectCapUV(Source cap, int mesh, IReadOnlyList<byte[]> bodies,
                                           Action<string>? diag, CapPlacement? placed = null)
    {
        var s = cap.S;
        int mo = cap.MeshStart + mesh * 36;
        ushort vc = BitConverter.ToUInt16(s, mo);
        if (vc == 0) return null;

        var decl = mesh < cap.Decls.Length ? cap.Decls[mesh] : [];
        VElem? pos = null, nrm = null, wgtEl = null;
        foreach (var el in decl)
        {
            if (el.Usage == UsePosition) pos ??= el;
            if (el.Usage == UseNormal) nrm ??= el;
            if (el.Usage == UseBlendWeight) wgtEl ??= el;
        }
        if (pos is not { } pe) return null;


        uint[] vbo = { BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                       BitConverter.ToUInt32(s, mo + 28) };
        byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
        var cp = new Vec3[vc];
        var cn = new Vec3[vc];
        {
            Span<float> tmp = stackalloc float[4];
            for (int i = 0; i < vc; i++)
            {
                ReadTyped(s, cap.Vb + (int)vbo[pe.Stream] + i * bs[pe.Stream] + pe.Offset, pe.Type, tmp);
                cp[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
                if (nrm is not { } ne) continue;
                ReadTyped(s, cap.Vb + (int)vbo[ne.Stream] + i * bs[ne.Stream] + ne.Offset, ne.Type, tmp);
                float nx = tmp[0], ny = tmp[1], nz = tmp[2];
                if (ne.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
                cn[i] = NormalizeOr(new Vec3(nx, ny, nz), default);
            }

            // Where the binding put it, if there is one. Everything below â€” the landing search, the seam
            // split, the rim â€” then describes the cap as it will actually be emitted rather than as it
            // was authored against a foot this player may not be wearing.
            if (placed is { } pl && pl.Pos.Length == vc)
                for (int i = 0; i < vc; i++) { cp[i] = pl.Pos[i]; cn[i] = pl.Nrm[i]; }
        }

        // Every body's LOD0 SKIN geometry, in one list. Reuses the reader the seam analysis uses, which
        // already applies the same skin-only filter the shell builder does.
        var tri = new List<(Vec3 A, Vec3 B, Vec3 C, (float U, float V) Ua, (float U, float V) Ub,
                            (float U, float V) Uc, Vec3 Ctr,
                            (string Bone, float W)[] Wa, (string Bone, float W)[] Wb, (string Bone, float W)[] Wc)>();
        foreach (var body in bodies)
        {
            if (!TryReadLod0Geometry(body, out var bp, out var bu, out var bt, out var bw)) continue;

            // TOENAILS ARE NOT A PROJECTION TARGET. They are skin by material, they sit proud of the
            // flesh, and over the toes they are frequently the NEAREST surface â€” but they carry their
            // own UV island. A cap triangle with one corner landing on a nail and another on skin then
            // stretches clean across the gap between two islands and samples whatever lies between,
            // which shows as a jagged transparent band through the middle of the cap.
            //
            // They are separate connected components, so drop the small ones and keep the feet.
            int nv = bp.Length / 3;
            var parent = new int[nv];
            for (int i = 0; i < nv; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int x, int y) { int rx = Find(x), ry = Find(y); if (rx != ry) parent[rx] = ry; }
            for (int t = 0; t + 2 < bt.Length; t += 3)
            {
                if (bt[t] >= nv || bt[t + 1] >= nv || bt[t + 2] >= nv) continue;
                Union(bt[t], bt[t + 1]); Union(bt[t + 1], bt[t + 2]);
            }
            var size = new Dictionary<int, int>();
            for (int i = 0; i < nv; i++)
            {
                int r = Find(i);
                size[r] = size.GetValueOrDefault(r) + 1;
            }
            int biggest = 0;
            foreach (int v in size.Values) biggest = Math.Max(biggest, v);
            // A foot is within a fraction of the other foot's size; a nail is a small fraction of either.
            int keepAbove = (int)(biggest * ProjectIslandFloor);

            for (int t = 0; t + 2 < bt.Length; t += 3)
            {
                int a = bt[t], b = bt[t + 1], c = bt[t + 2];
                if ((a + 1) * 3 > bp.Length || (b + 1) * 3 > bp.Length || (c + 1) * 3 > bp.Length) continue;
                if (size.GetValueOrDefault(Find(a)) < keepAbove) continue;
                var pa = new Vec3(bp[a * 3], bp[a * 3 + 1], bp[a * 3 + 2]);
                var pb = new Vec3(bp[b * 3], bp[b * 3 + 1], bp[b * 3 + 2]);
                var pc = new Vec3(bp[c * 3], bp[c * 3 + 1], bp[c * 3 + 2]);
                tri.Add((pa, pb, pc, (bu[a * 2], bu[a * 2 + 1]), (bu[b * 2], bu[b * 2 + 1]),
                         (bu[c * 2], bu[c * 2 + 1]), new Vec3((pa.X + pb.X + pc.X) / 3f,
                                                              (pa.Y + pb.Y + pc.Y) / 3f,
                                                              (pa.Z + pb.Z + pc.Z) / 3f),
                         a < bw.Length ? bw[a] : [], b < bw.Length ? bw[b] : [], c < bw.Length ? bw[c] : []));
            }
        }
        if (tri.Count == 0) { diag?.Invoke("authored cap: no body geometry to project UVs from"); return null; }

        // NOT taking skinning from the toenails, though they are what the cap physically covers there.
        // Tried: give each cap vertex the bones of the nearest real surface, nails included, so a vertex
        // over a nail follows the nail. It made things worse in game â€” the cap stood visibly off the toes
        // â€” because the nails are weighted to their own IVCS bones and following them pulls the cap away
        // from the skin it is supposed to hug. The nail poke-through is better fixed in the mesh.

        // The cap's OWN connectivity, and how big one of its edges is. Both drive the seam pass below:
        // the graph says which vertices have to agree, the length says how far away a rival landing may
        // sit and still count as a genuine alternative rather than a worse answer.
        var capTri = CapTriangles(cap, mesh, vc);
        var adj = new HashSet<int>[vc];
        for (int i = 0; i < vc; i++) adj[i] = [];
        var edgeLen = new List<float>();
        for (int t = 0; t + 2 < capTri.Count; t += 3)
        {
            int a = capTri[t], b = capTri[t + 1], c = capTri[t + 2];
            adj[a].Add(b); adj[b].Add(a);
            adj[b].Add(c); adj[c].Add(b);
            adj[c].Add(a); adj[a].Add(c);
            edgeLen.Add(Dist(cp[a], cp[b]));
            edgeLen.Add(Dist(cp[b], cp[c]));
            edgeLen.Add(Dist(cp[c], cp[a]));
        }
        edgeLen.Sort();
        float capEdge = edgeLen.Count > 0 ? edgeLen[edgeLen.Count / 2] : 0.002f;

        // Every landing worth considering, nearest first â€” not just the nearest one. A UV seam is a cut
        // in TEXTURE space only: the two sides are still welded in 3D, so a vertex sitting on one is very
        // nearly equidistant from body triangles carrying wildly different coordinates. Taking the
        // nearest per vertex then hands the three corners of one cap face landings on opposite sides of
        // the cut, and the face stretches clean across the atlas.
        var candU = new float[vc * ProjectCandidates];
        var candV = new float[vc * ProjectCandidates];
        var candD = new float[vc * ProjectCandidates];
        // The SKINNING that goes with each landing, blended by the same barycentric coordinate as the UV.
        // The cap has to deform exactly as the skin beneath it does or the join opens the moment the foot
        // is posed: measured against the authored weights, 123 of 192 vertex pairs across the join
        // disagreed, by up to 12.5%. A bind-pose measurement cannot see that, which is why every
        // geometric fix improved the numbers and changed nothing on screen.
        var candW = new (string Bone, float W)[vc * ProjectCandidates][];
        var candN = new int[vc];
        float worst = 0f;
        for (int i = 0; i < vc; i++)
        {
            var p = cp[i];
            int b0 = i * ProjectCandidates;
            int n = 0;
            float cull = float.MaxValue;   // squared distance the K-th best already achieves
            foreach (var (a, b, c, ua, ub, uc, ctr, wga, wgb, wgc) in tri)
            {
                float cx = ctr.X - p.X, cy = ctr.Y - p.Y, cz = ctr.Z - p.Z;
                if (cx * cx + cy * cy + cz * cz > cull + 0.01f) continue;
                var q = ClosestOnTriangle(p, a, b, c);
                float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                float d = dx * dx + dy * dy + dz * dz;
                if (n == ProjectCandidates && d >= candD[b0 + n - 1]) continue;

                float v0x = b.X - a.X, v0y = b.Y - a.Y, v0z = b.Z - a.Z;
                float v1x = c.X - a.X, v1y = c.Y - a.Y, v1z = c.Z - a.Z;
                float v2x = q.X - a.X, v2y = q.Y - a.Y, v2z = q.Z - a.Z;
                float d00 = v0x * v0x + v0y * v0y + v0z * v0z;
                float d01 = v0x * v1x + v0y * v1y + v0z * v1z;
                float d11 = v1x * v1x + v1y * v1y + v1z * v1z;
                float d20 = v2x * v0x + v2y * v0y + v2z * v0z;
                float d21 = v2x * v1x + v2y * v1y + v2z * v1z;
                float den = d00 * d11 - d01 * d01;
                float hu, hv;
                (string Bone, float W)[] hw;
                if (MathF.Abs(den) < 1e-20f) { hu = ua.U; hv = ua.V; hw = wga; }
                else
                {
                    float wb = (d11 * d20 - d01 * d21) / den;
                    float wc = (d00 * d21 - d01 * d20) / den;
                    float wa = 1f - wb - wc;
                    hu = ua.U * wa + ub.U * wb + uc.U * wc;
                    hv = ua.V * wa + ub.V * wb + uc.V * wc;
                    hw = BlendWeights(wga, wa, wgb, wb, wgc, wc);
                }

                // One landing per DISTINCT patch of the atlas. Body triangles that share an edge in UV
                // give near-identical coordinates; keeping them all would fill the list with one side of
                // the seam and crowd the other side out entirely.
                int dup = -1;
                for (int k = 0; k < n; k++)
                {
                    float du = candU[b0 + k] - hu, dv = candV[b0 + k] - hv;
                    if (du * du + dv * dv < ProjectMergeUV * ProjectMergeUV) { dup = k; break; }
                }
                if (dup >= 0)
                {
                    if (d >= candD[b0 + dup]) continue;
                    for (int k = dup; k + 1 < n; k++)
                    {
                        candU[b0 + k] = candU[b0 + k + 1]; candV[b0 + k] = candV[b0 + k + 1];
                        candD[b0 + k] = candD[b0 + k + 1]; candW[b0 + k] = candW[b0 + k + 1];
                    }
                    n--;
                }
                else if (n == ProjectCandidates) n--;

                int ins = n;
                while (ins > 0 && candD[b0 + ins - 1] > d)
                {
                    candU[b0 + ins] = candU[b0 + ins - 1]; candV[b0 + ins] = candV[b0 + ins - 1];
                    candD[b0 + ins] = candD[b0 + ins - 1]; candW[b0 + ins] = candW[b0 + ins - 1];
                    ins--;
                }
                candU[b0 + ins] = hu; candV[b0 + ins] = hv; candD[b0 + ins] = d; candW[b0 + ins] = hw;
                n++;
                if (n == ProjectCandidates) cull = candD[b0 + n - 1];
            }
            candN[i] = n;
            if (n > 0) worst = MathF.Max(worst, candD[b0]);
        }

        var outUV = new (float U, float V)[vc];
        var pick = new int[vc];
        for (int i = 0; i < vc; i++) outUV[i] = candN[i] > 0 ? (candU[i * ProjectCandidates], candV[i * ProjectCandidates]) : default;

        // Sweep the cap's own graph and let agreement, not proximity, settle the ties. A vertex switches
        // to a rival landing only when its neighbours' coordinates say so and the rival is no further
        // away than about one cap edge â€” so the vast interior, where there is one landing and no dispute,
        // never moves, and only the strip lying over the cut changes side.
        float slack = capEdge * ProjectSeamSlack;
        int moved = 0;
        for (int pass = 0; pass < ProjectSeamPasses; pass++)
        {
            int changed = 0;
            for (int i = 0; i < vc; i++)
            {
                int n = candN[i];
                if (n < 2 || adj[i].Count == 0) continue;
                int b0 = i * ProjectCandidates;
                float near = MathF.Sqrt(candD[b0]);

                int bestK = pick[i];
                float bestCost = float.MaxValue;
                for (int k = 0; k < n; k++)
                {
                    float far = MathF.Sqrt(candD[b0 + k]);
                    if (far > near + slack) continue;
                    float sum = 0f;
                    foreach (int j in adj[i])
                    {
                        float du = candU[b0 + k] - outUV[j].U, dv = candV[b0 + k] - outUV[j].V;
                        sum += MathF.Sqrt(du * du + dv * dv);
                    }
                    // The distance term is only a tie-break, converted into UV units at the cap's own
                    // scale so the two halves of the cost are comparable.
                    float cost = sum / adj[i].Count + (far - near) * ProjectNearBias / MathF.Max(capEdge, 1e-6f) * ProjectMergeUV;
                    if (cost < bestCost) { bestCost = cost; bestK = k; }
                }
                if (bestK == pick[i]) continue;
                pick[i] = bestK;
                outUV[i] = (candU[b0 + bestK], candV[b0 + bestK]);
                changed++;
            }
            moved += changed;
            if (changed == 0) break;
        }

        // Agreement alone cannot finish the job, and it is worth being clear why. Where the body's atlas
        // is cut, the cap straddles the cut, and BOTH sides are locally self-consistent â€” every vertex on
        // the far side agrees with its own neighbours, so nothing wants to move and the sweeps converge
        // with the seam still running through the middle. Measured: 32 straddling faces before, 30 after.
        //
        // Nor can one side simply be folded onto the other. That was tried, on the assumption that the
        // far side was a thin strip: it is not. The cut runs right through the toe box, 326 vertices
        // against 561 on the Neolithe foot, and forcing them together made it worse (30 straddling faces
        // to 36) because both charts carry real, different art.
        //
        // The cut is REPRODUCED instead. Label every cap face with the chart it belongs to, then give
        // each vertex one copy per chart its faces use, each copy taking a landing in its own chart. No
        // face has corners in two charts any more, so none can stretch between them â€” the same trick the
        // body's own mesh uses at the same place, which is why the seam is there to begin with.
        //
        // What marks an edge as crossing the cut is STRETCH â€” UV travelled per unit of 3D travelled â€” and
        // not an absolute UV distance. Two charts can pass arbitrarily close in the atlas: measured here,
        // a 0.10 cut-off left the far side still reachable from the near side through a chain of short
        // steps, so the whole cap read as one patch. Stretch has no such blind spot.
        var stretch = new List<float>();
        for (int t = 0; t + 2 < capTri.Count; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = capTri[t + k], b = capTri[t + (k + 1) % 3];
                float d3 = Dist(cp[a], cp[b]);
                if (d3 < 1e-7f) continue;
                float du = outUV[a].U - outUV[b].U, dv = outUV[a].V - outUV[b].V;
                stretch.Add(MathF.Sqrt(du * du + dv * dv) / d3);
            }
        stretch.Sort();
        float seamCut = (stretch.Count > 0 ? stretch[stretch.Count / 2] : 1f) * ProjectSeamStretch;

        var patchAdj = new HashSet<int>[vc];
        for (int i = 0; i < vc; i++) patchAdj[i] = [];
        for (int i = 0; i < vc; i++)
            foreach (int j in adj[i])
            {
                float d3 = Dist(cp[i], cp[j]);
                float du = outUV[i].U - outUV[j].U, dv = outUV[i].V - outUV[j].V;
                if (d3 < 1e-7f || MathF.Sqrt(du * du + dv * dv) / d3 <= seamCut) patchAdj[i].Add(j);
            }
        var patch = ConnectedComponents(patchAdj, vc);

        // A face belongs to whichever chart most of its corners are in; a three-way split (a corner
        // exactly on a junction) goes to the corner whose landing is nearest, which is the one whose
        // projection is least of a guess.
        int triCount = capTri.Count / 3;
        var faceChart = new int[triCount];
        for (int f = 0; f < triCount; f++)
        {
            int a = capTri[f * 3], b = capTri[f * 3 + 1], c = capTri[f * 3 + 2];
            faceChart[f] = patch[a] == patch[b] || patch[a] == patch[c] ? patch[a]
                         : patch[b] == patch[c] ? patch[b]
                         : candD[a * ProjectCandidates] <= candD[b * ProjectCandidates]
                           && candD[a * ProjectCandidates] <= candD[c * ProjectCandidates] ? patch[a]
                         : candD[b * ProjectCandidates] <= candD[c * ProjectCandidates] ? patch[b]
                         : patch[c];
        }

        // One output vertex per (vertex, chart) actually used. Everything away from the cut keeps a
        // single copy, so the cap grows by the width of the seam and nothing else.
        var copyOf = new Dictionary<(int V, int Chart), int>();
        var sourceOf = new List<int>();
        var corner = new int[capTri.Count];
        for (int f = 0; f < triCount; f++)
            for (int k = 0; k < 3; k++)
            {
                var key = (capTri[f * 3 + k], faceChart[f]);
                if (!copyOf.TryGetValue(key, out int outIdx))
                {
                    copyOf[key] = outIdx = sourceOf.Count;
                    sourceOf.Add(key.Item1);
                }
                corner[f * 3 + k] = outIdx;
            }

        // Each copy takes the landing that best suits ITS chart â€” for the copy on the far side of the
        // cut that is a different body triangle from the one nearest in 3D, which is the whole point.
        var finalUV = new (float U, float V)[sourceOf.Count];
        var finalW = new (string Bone, float W)[sourceOf.Count][];
        var srcW = new (string Bone, float W)[vc][];
        for (int i = 0; i < vc; i++) srcW[i] = [];
        int reseated = 0;
        foreach (var ((v, chart), outIdx) in copyOf)
        {
            int b0 = v * ProjectCandidates;
            int bestK = pick[v];
            if (patch[v] != chart)
            {
                float bestCost = float.MaxValue;
                for (int k = 0; k < candN[v]; k++)
                {
                    float sum = 0f;
                    int c = 0;
                    foreach (int j in adj[v])
                    {
                        if (patch[j] != chart) continue;
                        float du = candU[b0 + k] - outUV[j].U, dv = candV[b0 + k] - outUV[j].V;
                        sum += MathF.Sqrt(du * du + dv * dv);
                        c++;
                    }
                    if (c > 0 && sum / c < bestCost) { bestCost = sum / c; bestK = k; }
                }
                if (bestK != pick[v]) reseated++;
            }
            finalUV[outIdx] = candN[v] > 0 ? (candU[b0 + bestK], candV[b0 + bestK]) : default;
            finalW[outIdx] = candN[v] > 0 ? candW[b0 + bestK] ?? [] : [];
            // The rim is expressed in PRE-SPLIT indices, so it needs a weight per source vertex too.
            // Where a vertex was split the copies sit at the same place on the body and their weights
            // agree; which copy answers is immaterial.
            srcW[v] = finalW[outIdx];
        }

        // What the whole exercise is for: faces that still straddle the atlas. Reported so a regression
        // shows up in the build log rather than in game.
        int hops = 0;
        for (int f = 0; f < triCount; f++)
        {
            var (ua, ub, uc) = (finalUV[corner[f * 3]], finalUV[corner[f * 3 + 1]], finalUV[corner[f * 3 + 2]]);
            float span = MathF.Max(
                MathF.Max(MathF.Abs(ua.U - ub.U), MathF.Max(MathF.Abs(ub.U - uc.U), MathF.Abs(uc.U - ua.U))),
                MathF.Max(MathF.Abs(ua.V - ub.V), MathF.Max(MathF.Abs(ub.V - uc.V), MathF.Abs(uc.V - ua.V))));
            if (span > ProjectSeamSpan) hops++;
        }
        int charts = 0;
        {
            var seen = new HashSet<int>();
            foreach (int p in patch) seen.Add(p);
            charts = seen.Count;
        }
        diag?.Invoke($"authored cap: projected {vc} uvs from the body, furthest landing {MathF.Sqrt(worst):F4}, "
                   + $"{moved} settled by agreement; {charts} chart patch(es) split into {sourceOf.Count} "
                   + $"vertices ({reseated} copies reprojected), {hops} face(s) spanning >{ProjectSeamSpan:F2} uv");
        // Rings in from the cap's open boundary. The authored skinning is kept everywhere; only a band
        // this deep at the back seam is blended toward the body's, so the cap and the shell it welds to
        // deform alike where they meet without the rest of the cap being overwritten.
        var rimRing = new int[vc];
        Array.Fill(rimRing, int.MaxValue);
        {
            var edgeSeen = new Dictionary<(int A, int B), int>();
            for (int t = 0; t + 2 < capTri.Count; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = capTri[t + k], b = capTri[t + (k + 1) % 3];
                    var e = (Math.Min(a, b), Math.Max(a, b));
                    edgeSeen[e] = edgeSeen.GetValueOrDefault(e) + 1;
                }
            var queue = new Queue<int>();
            foreach (var (e, n) in edgeSeen)
                if (n == 1)
                {
                    foreach (int x in new[] { e.A, e.B })
                        if (rimRing[x] != 0) { rimRing[x] = 0; queue.Enqueue(x); }
                }
            while (queue.Count > 0)
            {
                int x = queue.Dequeue();
                foreach (int y in adj[x])
                    if (rimRing[y] > rimRing[x] + 1) { rimRing[y] = rimRing[x] + 1; queue.Enqueue(y); }
            }
        }

        return new CapUvPlan
        {
            Uv = finalUV, Weights = finalW, SourceOf = sourceOf.ToArray(), Corner = corner,
            Tri = capTri.ToArray(), SrcPos = cp, SrcNrm = cn, SrcW = srcW, RimRing = rimRing,
        };
    }

    /// <summary>
    /// How the authored cap's vertices are laid out once the body's UV seams have been cut into it:
    /// one entry per output vertex, and where every triangle corner points.
    /// </summary>
    private sealed class CapUvPlan
    {
        /// <summary>Projected coordinate per OUTPUT vertex.</summary>
        public required (float U, float V)[] Uv;

        /// <summary>
        /// Skinning per OUTPUT vertex, taken from the body underneath at the same landing as the UV, by
        /// bone NAME so it can be remapped into whatever table the emitted mesh ends up with. This is
        /// what keeps the cap moving with the skin it sits on â€” the authored weights coincided with the
        /// shell's in bind pose and differed by up to 12.5%, which opens the join in any real pose.
        /// </summary>
        public required (string Bone, float W)[][] Weights;

        /// <summary>The cap vertex each output vertex is a copy of â€” everything but the UV comes from it.</summary>
        public required int[] SourceOf;

        /// <summary>
        /// Output vertex per triangle corner, in the cap's own submesh-then-triangle order. The emitter
        /// walks its index buffer in that same order and substitutes these.
        /// </summary>
        public required int[] Corner;

        /// <summary>
        /// The cap's triangles by PRE-SPLIT index, and the position and normal of each such vertex. The
        /// boundary cannot be baked in here: it depends on the coverage map, which differs per layer, so
        /// each layer works out its own trimmed rim from these.
        /// </summary>
        public required int[] Tri;

        /// <inheritdoc cref="Tri"/>
        public required Vec3[] SrcPos;

        /// <inheritdoc cref="Tri"/>
        public required Vec3[] SrcNrm;

        /// <inheritdoc cref="Tri"/>
        public required (string Bone, float W)[][] SrcW;

        /// <summary>
        /// Rings from the cap's open boundary, per PRE-SPLIT vertex; 0 on the boundary itself and int.Max
        /// where the boundary is unreachable. Drives how far the seam blend reaches in from the back edge,
        /// so the authored skinning survives everywhere else.
        /// </summary>
        public required int[] RimRing;
    }

    /// <summary>
    /// Component label per vertex over an adjacency list, optionally restricted to a subset. Vertices
    /// outside the subset keep label -1.
    /// </summary>
    private static int[] ConnectedComponents(HashSet<int>?[] adj, int vc, IReadOnlyList<int>? subset = null)
    {
        var label = new int[vc];
        Array.Fill(label, -1);
        var seeds = subset ?? Enumerable.Range(0, vc).ToList();
        var stack = new Stack<int>();
        int next = 0;
        foreach (int seed in seeds)
        {
            if (label[seed] >= 0) continue;
            int id = next++;
            stack.Push(seed);
            label[seed] = id;
            while (stack.Count > 0)
            {
                int v = stack.Pop();
                if (adj[v] is not { } near) continue;
                foreach (int j in near)
                    if (label[j] < 0) { label[j] = id; stack.Push(j); }
            }
        }
        return label;
    }

    /// <summary>LOD0 triangle indices of one mesh of a parsed source, flattened.</summary>
    private static List<int> CapTriangles(Source src, int mesh, ushort vc)
    {
        var s = src.S;
        int mo = src.MeshStart + mesh * 36;
        ushort si = BitConverter.ToUInt16(s, mo + 10), sc = BitConverter.ToUInt16(s, mo + 12);
        var outp = new List<int>();
        for (int su = 0; su < sc; su++)
        {
            int ss = src.SubmeshStart + (si + su) * 16;
            uint so = BitConverter.ToUInt32(s, ss), cnt = BitConverter.ToUInt32(s, ss + 4);
            for (uint t = 0; t + 2 < cnt; t += 3)
            {
                int q = src.Ib + (int)(so + t) * 2;
                int a = BitConverter.ToUInt16(s, q), b = BitConverter.ToUInt16(s, q + 2), c = BitConverter.ToUInt16(s, q + 4);
                // Clamped, never skipped: the emitter walks the same triangles in the same order and
                // reads the result positionally, so dropping one here would shift every corner after it.
                outp.Add(Math.Min(a, vc - 1)); outp.Add(Math.Min(b, vc - 1)); outp.Add(Math.Min(c, vc - 1));
            }
        }
        return outp;
    }

    /// <summary>
    /// A cut mask covering exactly where the authored cap actually is, rasterised from its placed UVs and
    /// substituted for the painted ToeCap map whenever a cap is grafted.
    /// <para/>
    /// The painted map says where a cap is WANTED; it cannot say where the modelled one reaches, and the
    /// two only agree on the body the map was painted for. On another body they come apart: the map is
    /// authored in bibo UV and a point on Rue sits about 0.008 from the same point on Neolithe, so the
    /// cut lands well behind the cap's rim, past the weld's reach â€” 329 lip vertices too far to weld and
    /// only 84 of the cap's 112 rim edges closed. In game that is a band of bare skin between the cap and
    /// the shell, jagged because its edge follows the map's texel grid.
    /// <para/>
    /// The cap's own footprint has no such problem: it is measured from where the cap was actually placed
    /// on THIS body, so the hole always matches the thing filling it, and the weld only has a texel of
    /// quantisation left to close.
    /// </summary>
    private static void CapFootprintMask(CapUvPlan plan, SecondSkinLayer? cov, byte[] mask, int size)
    {
        var uv = plan.Uv;
        int faces = plan.Corner.Length / 3;
        for (int f = 0; f < faces; f++)
        {
            int a = plan.Corner[f * 3], b = plan.Corner[f * 3 + 1], c = plan.Corner[f * 3 + 2];
            if (a >= uv.Length || b >= uv.Length || c >= uv.Length) continue;
            // Only the part of the cap this layer will actually emit â€” the footprint has to describe the
            // hole the cap fills, and a coverage-trimmed cap fills less of one.
            if (cov?.Coverage != null && !AnyVisible(cov, uv[a], uv[b], uv[c])) continue;

            {
                float ax = uv[a].U * size, ay = uv[a].V * size;
                float bx = uv[b].U * size, by = uv[b].V * size;
                float cx = uv[c].U * size, cy = uv[c].V * size;
                // TILED, not clamped. A body's UVs need not live in the [0,1] tile â€” the foot model a
                // heel swaps in sits a whole tile down (v -0.70..-0.56 measured, against the v 0.30..0.44
                // the same cap occupies barefoot) â€” and the mask's consumer samples it with wrap, so the
                // rasteriser has to write with wrap or the two disagree. Clamping put every texel of the
                // cap on row 0: 97 lit against 2650, nothing over the toes was marked, and the shell was
                // left uncut in heels while barefoot was perfect. The triangle stays in unwrapped space
                // so its barycentric test is unaffected; only the write index wraps.
                int x0 = (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))) - 1;
                int x1 = (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))) + 1;
                int y0 = (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))) - 1;
                int y1 = (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))) + 1;
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1 << 18) continue;   // a seam-straddling triangle
                int Wrap(int v) => (v % size + size) % size;

                // CONSERVATIVE for anything that doesn't comfortably contain a texel centre. The cap is
                // far denser in UV than the map it replaces â€” 3432 faces over about 1351 texels, so the
                // typical face is SMALLER than a texel â€” and point-sampling a mesh like that leaves a
                // dotted mask, not a solid one. Measured: the shell kept its toes right to the tips
                // underneath the cap, because most of its triangles found no lit texel to be cut by.
                // Covering the whole footprint of a sub-texel face overstates it by at most a texel, and
                // the weld closes that.
                float wide = MathF.Max(ax, MathF.Max(bx, cx)) - MathF.Min(ax, MathF.Min(bx, cx));
                float tall = MathF.Max(ay, MathF.Max(by, cy)) - MathF.Min(ay, MathF.Min(by, cy));
                if (wide <= 1.5f && tall <= 1.5f)
                {
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                            mask[Wrap(y) * size + Wrap(x)] = 255;
                    continue;
                }

                float den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (MathF.Abs(den) < 1e-12f) continue;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float w0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den;
                        float w1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den;
                        float w2 = 1f - w0 - w1;
                        if (w0 < -0.02f || w1 < -0.02f || w2 < -0.02f) continue;
                        mask[Wrap(y) * size + Wrap(x)] = 255;
                    }
            }
        }

        // FILL WHAT THE CAP ENCLOSES, not merely what it covers. The cap is a smooth dome spanning OVER
        // the gaps between the toes, so its faces never touch the atlas where those crevices live â€” but
        // the shell geometry it replaces does, and leaving that behind is a sleeved toe poking out
        // through the cap. Measured: cutting by the face footprint alone left the shell's toes intact to
        // the tips (triangles out 11587 -> 18029) with the cap sitting over them.
        //
        // Anything the background cannot reach from the edge of the atlas is inside the cap's outline.
        var outside = new bool[size * size];
        var queue = new Queue<int>();
        void Seed(int i) { if (mask[i] == 0 && !outside[i]) { outside[i] = true; queue.Enqueue(i); } }
        for (int x = 0; x < size; x++) { Seed(x); Seed((size - 1) * size + x); }
        for (int y = 0; y < size; y++) { Seed(y * size); Seed(y * size + size - 1); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % size, y = i / size;
            if (x > 0) Seed(i - 1);
            if (x < size - 1) Seed(i + 1);
            if (y > 0) Seed(i - size);
            if (y < size - 1) Seed(i + size);
        }
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] == 0 && !outside[i]) mask[i] = 255;
    }

    /// <summary>Mean length of the edges touching the given nodes â€” the mesh's own resolution.</summary>
    private static float MeanEdgeLength(Vec3[] pos, List<int>[] adj, List<int> nodes)
    {
        float total = 0;
        int count = 0;
        foreach (int n in nodes)
        {
            if (adj[n] == null) continue;
            foreach (int k in adj[n])
            {
                float dx = pos[k].X - pos[n].X, dy = pos[k].Y - pos[n].Y, dz = pos[k].Z - pos[n].Z;
                total += MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                count++;
            }
        }
        return count > 0 ? total / count : 0f;
    }

    /// <summary>Axis of the given nodes' longest bounding-box side, or null when they occupy no space.</summary>
    private static Vec3? LongestExtent(Vec3[] pos, List<int> nodes)
    {
        float lox = float.MaxValue, loy = float.MaxValue, loz = float.MaxValue;
        float hix = float.MinValue, hiy = float.MinValue, hiz = float.MinValue;
        foreach (int n in nodes)
        {
            lox = MathF.Min(lox, pos[n].X); hix = MathF.Max(hix, pos[n].X);
            loy = MathF.Min(loy, pos[n].Y); hiy = MathF.Max(hiy, pos[n].Y);
            loz = MathF.Min(loz, pos[n].Z); hiz = MathF.Max(hiz, pos[n].Z);
        }
        float ex = hix - lox, ey = hiy - loy, ez = hiz - loz;
        if (ex <= 1e-6f && ey <= 1e-6f && ez <= 1e-6f) return null;
        return ex >= ey && ex >= ez ? new Vec3(1, 0, 0)
             : ey >= ez             ? new Vec3(0, 1, 0)
                                    : new Vec3(0, 0, 1);
    }

    /// <summary>Unit vector, or null when the input is too short to have a direction.</summary>
    private static Vec3? Normalize(Vec3 v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len > 1e-6f ? new Vec3(v.X / len, v.Y / len, v.Z / len) : null;
    }

    /// <summary>Any two unit vectors spanning the plane perpendicular to <paramref name="n"/>.</summary>
    private static void Basis(Vec3 n, out Vec3 u, out Vec3 v)
    {
        var seed = MathF.Abs(n.X) < 0.9f ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        u = Normalize(new Vec3(
            seed.Y * n.Z - seed.Z * n.Y,
            seed.Z * n.X - seed.X * n.Z,
            seed.X * n.Y - seed.Y * n.X))!.Value;
        v = new Vec3(n.Y * u.Z - n.Z * u.Y, n.Z * u.X - n.X * u.Z, n.X * u.Y - n.Y * u.X);
    }

    /// <summary>Convex hull of a 2D point set, counter-clockwise (Andrew's monotone chain).</summary>
    private static (float X, float Y)[] ConvexHull((float X, float Y)[] pts)
    {
        if (pts.Length < 3) return pts;
        var p = (( float X, float Y)[])pts.Clone();
        Array.Sort(p, (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        static float Cross((float X, float Y) o, (float X, float Y) a, (float X, float Y) b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        var hull = new (float X, float Y)[p.Length * 2];
        int k = 0;
        foreach (var q in p)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], q) <= 0) k--;
            hull[k++] = q;
        }
        int lower = k + 1;
        for (int i = p.Length - 2; i >= 0; i--)
        {
            var q = p[i];
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], q) <= 0) k--;
            hull[k++] = q;
        }
        return hull[..Math.Max(k - 1, 0)];
    }

    /// <summary>Nearest point to <paramref name="q"/> on the hull's boundary.</summary>
    private static (float X, float Y) ClosestOnHull((float X, float Y)[] hull, (float X, float Y) q)
    {
        var best = hull[0];
        float bestD = float.MaxValue;
        for (int i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            float ex = b.X - a.X, ey = b.Y - a.Y;
            float len2 = ex * ex + ey * ey;
            float t = len2 > 1e-20f ? ((q.X - a.X) * ex + (q.Y - a.Y) * ey) / len2 : 0f;
            t = Math.Clamp(t, 0f, 1f);
            float px = a.X + ex * t, py = a.Y + ey * t;
            float d = (px - q.X) * (px - q.X) + (py - q.Y) * (py - q.Y);
            if (d < bestD) { bestD = d; best = (px, py); }
        }
        return best;
    }

    /// <summary>Write a 2-component UV (u,v) at <paramref name="off"/>, as two halves or two floats.</summary>
    private static void WriteUV2(byte[] a, int off, bool half, float u, float v)
    {
        if (half) { W16(a, off, Half(u)); W16(a, off + 2, Half(v)); }
        else
        {
            W32(a, off, (uint)BitConverter.SingleToInt32Bits(u));
            W32(a, off + 4, (uint)BitConverter.SingleToInt32Bits(v));
        }
    }

    /// <summary>Write x,y,z into a position element of the given type, leaving any 4th component intact.</summary>
    private static void WriteXYZ(byte[] a, int off, byte type, float x, float y, float z)
    {
        switch (type)
        {
            case 2: case 3:   // Float3 / Float4
                W32(a, off, (uint)BitConverter.SingleToInt32Bits(x));
                W32(a, off + 4, (uint)BitConverter.SingleToInt32Bits(y));
                W32(a, off + 8, (uint)BitConverter.SingleToInt32Bits(z));
                break;
            case 14:          // Half4
                W16(a, off, Half(x)); W16(a, off + 2, Half(y)); W16(a, off + 4, Half(z));
                break;
            case 13:          // Half2 (unusual for position)
                W16(a, off, Half(x)); W16(a, off + 2, Half(y));
                break;
        }
    }

    /// <summary>
    /// Decode a vertex attribute of the given FFXIV vertex-declaration <paramref name="type"/> into up to
    /// four floats. Covers the types skin meshes actually use for position/normal/uv (float, half, and
    /// the normalized integer forms); unknown types leave the destination zeroed.
    /// </summary>
    internal static void ReadTyped(byte[] s, int addr, byte type, Span<float> o)
    {
        o.Clear();
        float H(int a) => (float)BitConverter.ToHalf(s, a);
        float F(int a) => BitConverter.ToSingle(s, a);
        short I(int a) => BitConverter.ToInt16(s, a);
        ushort U(int a) => BitConverter.ToUInt16(s, a);
        switch (type)
        {
            case 0:  o[0] = F(addr); break;                                                             // Float1
            case 1:  o[0] = F(addr); o[1] = F(addr + 4); break;                                         // Float2
            case 2:  o[0] = F(addr); o[1] = F(addr + 4); o[2] = F(addr + 8); break;                     // Float3
            case 3:  o[0] = F(addr); o[1] = F(addr + 4); o[2] = F(addr + 8); o[3] = F(addr + 12); break; // Float4
            case 5:  for (int k = 0; k < 4; k++) o[k] = s[addr + k]; break;                             // Ubyte4
            case 8:  for (int k = 0; k < 4; k++) o[k] = s[addr + k] / 255f; break;                      // Ubyte4n
            case 6:  o[0] = I(addr); o[1] = I(addr + 2); break;                                         // Short2
            case 7:  for (int k = 0; k < 4; k++) o[k] = I(addr + k * 2); break;                         // Short4
            case 9:  o[0] = I(addr) / 32767f; o[1] = I(addr + 2) / 32767f; break;                       // Short2n
            case 10: for (int k = 0; k < 4; k++) o[k] = I(addr + k * 2) / 32767f; break;                // Short4n
            case 13: o[0] = H(addr); o[1] = H(addr + 2); break;                                         // Half2
            case 14: o[0] = H(addr); o[1] = H(addr + 2); o[2] = H(addr + 4); o[3] = H(addr + 6); break; // Half4
            case 16: o[0] = U(addr); o[1] = U(addr + 2); break;                                         // Ushort2
            case 17: for (int k = 0; k < 4; k++) o[k] = U(addr + k * 2); break;                         // Ushort4
        }
    }

    /// <summary>
    /// Faintest coverage that still counts as painted, out of 255. Testing for any non-zero texel at all
    /// keeps geometry under alpha of 1/255, which is invisible â€” and a resampled or compressed coverage
    /// map is full of that. One measured here had 7.4% of its texels non-zero with a MEDIAN non-zero
    /// value of 5, and it kept an entire second shell over the feet for an overlay whose art is a band
    /// on the thigh. That shell then pokes through the one above it.
    /// </summary>
    private const byte CoverageFloor = 8;

    /// <summary>
    /// Does any texel under this triangle's UV footprint carry coverage? Scans the full texel bounding
    /// box (padded one texel for bilinear bleed) rather than the exact triangle: over-keeping a sliver
    /// is free, wrongly culling one leaves a visible sawtooth.
    /// </summary>
    private static bool AnyVisible(SecondSkinLayer def, (float U, float V) a, (float U, float V) b, (float U, float V) c)
    {
        var mask = def.Coverage;
        if (mask == null) return true;
        int w = def.CoverageWidth, h = def.CoverageHeight;

        float u0 = MathF.Min(a.U, MathF.Min(b.U, c.U)), u1 = MathF.Max(a.U, MathF.Max(b.U, c.U));
        float v0 = MathF.Min(a.V, MathF.Min(b.V, c.V)), v1 = MathF.Max(a.V, MathF.Max(b.V, c.V));
        int x0 = (int)MathF.Floor(u0 * w) - 1, x1 = (int)MathF.Ceiling(u1 * w) + 1;
        int y0 = (int)MathF.Floor(v0 * h) - 1, y1 = (int)MathF.Ceiling(v1 * h) + 1;

        // A triangle straddling a UV seam has a huge box; keep it rather than scan the whole texture.
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1 << 16) return true;

        for (int y = y0; y <= y1; y++)
        {
            int wy = ((y % h) + h) % h;
            for (int x = x0; x <= x1; x++)
            {
                int wx = ((x % w) + w) % w;
                if (mask[wy * w + wx] >= CoverageFloor) return true;
            }
        }
        return false;
    }

    private static ushort Half(float f) => BitConverter.HalfToUInt16Bits((Half)f);
    private static void W16(byte[] a, int o, ushort v) => BitConverter.GetBytes(v).CopyTo(a, o);
    private static void W32(byte[] a, int o, uint v) => BitConverter.GetBytes(v).CopyTo(a, o);
}
