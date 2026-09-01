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
    /// When non-empty, this layer IS geometry rather than a copy of the character's: the named meshes of
    /// each <see cref="ContentGeometry.Model"/> are emitted verbatim — unpushed, untrimmed, at their
    /// authored vertices, UVs and skinning — under this layer's single material. Empty for an ordinary
    /// second-skin shell, which is cut from the body sources instead.
    /// <para/>
    /// A LIST because a material is what costs a slot on the host, not a mesh. Several pieces of an imported
    /// pack that want the same material with the same colours — a mod of five piercings usually ships
    /// exactly one — would otherwise publish byte-identical materials and spend a slot each, out of a budget
    /// of ten.
    /// </summary>
    public IReadOnlyList<ContentGeometry> Geometry { get; init; } = [];

    /// <summary>
    /// Multiplies how far this layer is pushed off the surface it was cut from. 1 keeps the tuned offset.
    /// <para/>
    /// Set from <c>ShellSurfaceKey.PushScale</c>, which exists because <see cref="BaseOffset"/> is a
    /// millimetre measured against a torso and a surface an order of magnitude smaller needs less of it.
    /// </summary>
    public float PushScale { get; init; } = 1f;
}

/// <summary>
/// One imported model and the meshes of it that belong to a layer.
/// <paramref name="KeepMaterial"/> is matched against the model's own material names (leading slash
/// included, as the model stores them) — see <see cref="SecondSkinWriter.KeepByLeaf"/>.
/// <para/>
/// <paramref name="MirrorUv1"/> overwrites every uv1 slot with the mesh's own uv0. It is the ONE deviation
/// from a byte-for-byte copy, and it is set only when the layer's material was rebuilt onto
/// <c>characterscroll.shpk</c> for an animated glow: that shader samples its scroll map with uv1, and a
/// model's uv1 is as likely to hold an unrelated aux coordinate as a usable texcoord.
/// </summary>
/// <param name="HiddenAttributes">
/// Attribute names this pack's own toggles currently switch OFF, by the source model's own naming. A
/// submesh tagged only with these is dropped rather than emitted.
/// <para/>
/// Applied here, at build time, because the runtime mechanism cannot survive the move. The game decides a
/// submesh's visibility from the IMC attribute mask of the item being WORN, and Proteus appends this
/// geometry onto a host accessory — so the pack's own mask governs a set nobody has equipped, and the
/// host's governs geometry it knows nothing about. Baking the answer into the mesh sidesteps both, and
/// composes when several packs share one host, which a single per-item mask could not.
/// </param>
/// <param name="OwnAttributes">
/// Proteus has already decided this geometry's visibility, so the surviving submeshes are emitted
/// UNTAGGED — their attribute masks cleared.
/// <para/>
/// Without this the decision is made twice. A submesh's attribute mask is a gate the game closes using the
/// IMC entry of the item being WORN, and this geometry is about to be appended to a host accessory: so
/// every piece kept by <paramref name="HiddenAttributes"/> would then be judged again by the host's own
/// mask, which knows nothing about this garment. Whichever bits that item happens to carry would decide
/// what renders — arbitrary per bit, and the reason a dress's toggles could appear to do nothing while the
/// same pack's shoes toggles worked.
/// <para/>
/// Only for geometry whose visibility Proteus resolved. A pack that switches its pieces by NAME through
/// Penumbra's <c>Atr</c> manipulation still needs its tags, because there the runtime is the mechanism.
/// </param>
public sealed record ContentGeometry(
    byte[] Model, Func<string, bool> KeepMaterial, bool MirrorUv1 = false,
    IReadOnlySet<string>? HiddenAttributes = null, bool OwnAttributes = false);

/// <summary>
/// A host's shell came out with no meshes in it.
/// <para/>
/// Its own type, and <see cref="ByToggle"/> in particular, because the two ways to get here deserve
/// opposite reactions. Coverage trimming removing everything is a fault. A pack's own hide toggles
/// removing everything is the user having switched off the only thing on that host, and reporting it as a
/// failed build makes a routine action look like a bug.
/// </summary>
public sealed class EmptyShellException(string message, bool byToggle) : InvalidOperationException(message)
{
    /// <summary>The pack's own show/hide toggles emptied it, rather than anything going wrong.</summary>
    public bool ByToggle { get; } = byToggle;
}

/// <summary>
/// Builds the "second skin" model: every skin part (chest, legs, hands, feet…) duplicated, pushed out
/// along its normals, and MERGED into a single model so the whole thing rides one invisible accessory
/// (the right ring). Each part × layer becomes its own mesh group, and each group carries its layer's
/// material — so different regions can run different shaders.
///
/// Each mesh keeps its SOURCE vertex format verbatim (its own declaration and stream layout); only the
/// position (pushed), vertex colour (whitened), and uv1 (mirrored from uv0 for the scroll shader) are
/// rewritten. See <see cref="BuildVerbatim"/> — this is what lets vanilla, bibo and Neolithe bodies,
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
///    BlendIndices are ubyte4 — they address the MESH'S OWN bone table, which therefore can never
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
    /// should therefore transform rigidly with the skin — so pure joint compression does not explain all
    /// of it. Suspects: split/duplicated normals at UV seams pushing coincident vertices apart, or the
    /// skin picking up deformation the shell does not. Until that is understood this value is empirical.
    /// </summary>
    public const float BaseOffset = 1e-3f;

    /// <summary>
    /// Separation between adjacent shells. Measured in-game: 2e-4 holds, below it they clip. This is NOT
    /// a depth-precision limit (float32 depth at 1-3 units resolves far finer) — it's skinning.
    /// Layer k sits at BaseOffset + k * LayerSeparation.
    /// </summary>
    public const float LayerSeparation = 2e-4f;

    private const int DeclSize = 17 * 8;   // vertex declaration block, one per mesh
    private const int BBoxSize = 32;       // min Vec4 + max Vec4

    public readonly record struct Stats(int Meshes, int Submeshes, int Bones, int TrianglesIn, int TrianglesOut, int VerticesOut);

    /// <summary>
    /// The material names a body model references, e.g. "/mt_c0201b0001_bibo.mtrl". The shell inherits
    /// this model's UVs, so its material is the authoritative statement of which UV space those are —
    /// far more reliable than guessing from whatever body materials happen to be loaded.
    /// </summary>
    public static List<string> MaterialNames(byte[] s) => ReadMaterialNames(s, Parse(s));

    /// <summary>
    /// The model's attribute names, in the order its submesh masks index them — bit <c>i</c> of a submesh's
    /// mask means entry <c>i</c> here.
    /// <para/>
    /// The ORDER is the point, and it is why this exists beside the material-keyed reader below. An IMC
    /// attribute mask addresses these by POSITION rather than by name, so turning "bit 0 is off" into
    /// "submeshes tagged atr_sne are off" needs the table as the model wrote it. The same pack proves the
    /// position is not fixed: Denim Shorts lists <c>[atr_sne, atr_hiz]</c> on its Midlander model and
    /// <c>[atr_hiz, atr_sne]</c> on its Lalafell one.
    /// </summary>
    public static IReadOnlyList<string> AttributeNames(byte[] s) => Parse(s).AttrNames;

    /// <summary>
    /// The model's material names, and for each the attribute names of the LOD0 submeshes drawn with it —
    /// both from ONE walk of the file. An empty attribute list means the material is drawn unconditionally.
    /// <para/>
    /// The attributes are what connect a material to the mod's own checkboxes. A pack holding many
    /// accessories in one model tags each piece's submeshes with an attribute and gives it an option;
    /// walking submesh → mask → attribute → option is the only way to say which of the pack's switches a
    /// given material answers to, and therefore whether its colours are worth showing at all right now.
    /// <para/>
    /// One walk because the importer wants both of a file it has just opened, and parsing is the expensive
    /// half of either question — the pack that motivated this carries 116k vertices across 21 meshes.
    /// </summary>
    /// <remarks>
    /// The two halves fail DIFFERENTLY, and deliberately so. A model whose material names cannot be read is
    /// not a model this can use, and the exception carries that. Attributes are a nicety on top: they decide
    /// whether the panel can name a material after the checkbox that reveals it, and losing a whole piece
    /// over them would trade something that works for something that is merely nicer. The submesh ranges
    /// this walks are not validated by <see cref="Parse"/> against the submesh count, so a malformed one
    /// throws here while the names above read perfectly well.
    /// </remarks>
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
    /// LOD0 triangle geometry — object-space position and uv0 per vertex, plus triangle indices — for UV
    /// seam analysis (see <see cref="UvSeamMapService"/>). Every LOD0 mesh is concatenated into one vertex
    /// array with its indices rebased, so a seam BETWEEN two meshes is found exactly like one inside a
    /// mesh; that matters because a body's torso and legs are frequently separate meshes.
    /// <para/>
    /// Returns false rather than throwing on a model this can't read — a missing position or uv0 element,
    /// a truncated buffer, anything Parse rejects. The caller treats that as "no seam data" and falls back.
    /// <para/>
    /// <paramref name="keepMaterial"/> selects which meshes count, defaulting to body skin. The two callers
    /// genuinely want different answers and must not be unified: the seam map is built for the SKIN bake and
    /// is body-only by nature, while the shell's shape fingerprint has to describe whatever surface is being
    /// cut, or a face logs "(no skin geometry)" and the most useful diagnostic in the build goes dark.
    /// </summary>
    public static bool TryReadLod0Geometry(byte[] mdl, out float[] positions, out float[] uvs, out int[] triangles,
        Func<string, bool>? keepMaterial = null)
    {
        keepMaterial ??= IsBodySkinMaterial;
        positions = []; uvs = []; triangles = [];
        Source src;
        try { src = Parse(mdl); }
        catch { return false; }

        var s = src.S;

        // SKIN MESHES ONLY. A body model is not all skin: it carries the smallclothes/undies mesh, nails,
        // piercings and pubes, and each of those is authored in its OWN UV layout (gear space, not body
        // space). Including them lands their triangles at unrelated places in the body atlas — which bridges
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
            if (matIdx >= matNames.Count || !keepMaterial(matNames[matIdx])) continue;

            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            VElem? posEl = null, uvEl = null;
            foreach (var el in decl)
            {
                if (el.Usage == UsePosition) posEl ??= el;
                else if (el.Usage == UseUV && el.UsageIndex == 0) uvEl ??= el;
            }
            if (posEl is not { } pe || uvEl is not { } ue) continue;

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
            }
            if (!ok) { pos.RemoveRange(baseVertex * 3, pos.Count - baseVertex * 3);
                       uv.RemoveRange(baseVertex * 2, uv.Count - baseVertex * 2); continue; }

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
    /// skin material (mt_c{race}b{body}_…) belong in a second skin.
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

    /// <summary>
    /// The default mesh filter: body skin only. Split out from <see cref="SkinMaterialBodyType"/> because
    /// that function was answering two unrelated questions with one return value — "does this mesh belong in
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
    /// misalign them. It also makes the per-source-ness explicit where it matters — a shell cut from a face
    /// and one cut from a body do not want the same mesh filter or the same connector heuristic.
    /// </summary>
    /// <param name="Model">The .mdl bytes.</param>
    /// <param name="KeepMaterial">Which meshes to copy, by material name. Null = <see cref="IsBodySkinMaterial"/>.</param>
    /// <param name="EnabledShapes">Shape keys the game has enabled on this model, to bake.</param>
    /// <param name="UvConv">Vertex UV conversion into the shell's space. Null = already there, leave alone.</param>
    /// <param name="DropConnectors">
    /// Drop this source's redundant connector geometry. A body-shaped heuristic (see the emit loop), so it
    /// is only ever right for a BODY source — pointed at a face, tail or ear it deletes real geometry.
    /// </param>
    /// <param name="OtherPartBands">
    /// The vertical extent of every OTHER part in this shell. A connector ring is only redundant because a
    /// neighbouring part already covers it, so this is what makes that test answerable rather than assumed
    /// — see the emit loop. Null or empty means nothing else covers anything, and no ring is dropped.
    /// </param>
    /// <param name="UnmirrorSides">
    /// This source's UV is MIRRORED (both sides of the body share one layout) and <paramref name="UvConv"/>
    /// is expected to send the two sides to different halves of the shell's sheet. Costs a per-mesh pass
    /// over positions and indices to work out which side each vertex is on, so it is only set when a layer
    /// actually needs it — see <see cref="SurfaceMirror.AssignSides"/>.
    /// </param>
    public readonly record struct SourceSpec(
        byte[] Model,
        Func<string, bool>? KeepMaterial = null,
        HashSet<string>? EnabledShapes = null,
        UVRemapService.UvConversion? UvConv = null,
        bool DropConnectors = false,
        bool UnmirrorSides = false,
        IReadOnlyList<(float Lo, float Hi)>? OtherPartBands = null);

    /// <summary>
    /// One entry of a mesh's vertex declaration: where and in what format a given attribute (Usage) sits
    /// within its vertex stream. Read so the transcoder can locate attributes by declaration instead of
    /// assuming a fixed layout — vanilla and modded models declare different offsets and types (half vs
    /// float, compressed positions), so a fixed layout skins the wrong bytes as garbage.
    /// </summary>
    internal readonly record struct VElem(byte Stream, byte Offset, byte Type, byte Usage, byte UsageIndex);

    // Vertex Usage ids (FFXIV mdl).
    internal const byte UsePosition = 0, UseBlendWeight = 1, UseBlendIndices = 2,
                        UseNormal = 3, UseUV = 4, UseTangent2 = 5, UseTangent1 = 6, UseColor = 7;

    /// <summary>
    /// A parsed body part.
    /// <para/>
    /// Internal rather than private because two other services read a model through this parser rather than
    /// writing a second one: <see cref="ModelPartReader"/>, which lists a model's toggleable pieces, and
    /// <see cref="ModelAttributeWriter"/>, which edits its attribute table in place. Every offset either
    /// needs is already computed here, and a duplicate walk of a format this fiddly would be a second thing
    /// to get wrong.
    /// </summary>
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

        /// <summary>
        /// The model's attribute names, indexed the way its submesh masks reference them (bit <c>i</c> of a
        /// submesh's mask means attribute <c>i</c>).
        /// <para/>
        /// Carried because a mod can switch whole parts of a model on and off through these, by NAME, with
        /// Penumbra's <c>Atr</c> manipulation — which is how an accessory pack ships one model holding a
        /// dozen pieces and a checkbox for each. Drop them and every piece draws at once, whatever the mod's
        /// own options say.
        /// </summary>
        public string[] AttrNames = [];
        public ushort[][] BoneTables = [];
        public ushort[] SubmeshBoneMap = [];

        /// <summary>
        /// Where the shape block starts — i.e. where the bone tables ended. Recorded because that position
        /// is version-dependent (v5 and v6 encode bone tables differently) and every block after it is
        /// placed relative to it, so it is the single number that says whether a model was walked correctly.
        /// </summary>
        public int ShapeBlock;
        public ushort Lod0MeshIndex, Lod0MeshCount;   // only LOD0 meshes are shelled
        public byte[] BoneBBoxes = [];    // BoneCount * 32
        public byte[] ModelBBoxes = [];   // 4 * 32
        public float Radius, ModelClip, ShadowClip;
        public byte Flags1, Flags2;
        public byte[] Lods = [];          // 3 * 60

        // Shape-key morphs parsed from the .mdl (LOD0), keyed by shape name → its per-mesh index edits.
        // A ShapeValue redirects one index-buffer entry (at BaseIdx, absolute) to a morphed replacement
        // vertex (Replace). Enabled shapes for THIS body model (from BodyShapeReader); only these bake.
        public Dictionary<string, List<ShapeMeshEntry>> Shapes = new(StringComparer.Ordinal);
        public HashSet<string>? EnabledShapes;

        // Set when THIS source's UVs are in a different body UV space than the shell's (a bibo-UV heel's
        // foot beside a gen3 torso). Rewrites each vertex's uv0 into the shell space so one art set —
        // already remapped into that space — lands correctly on every part. Null = same space, leave alone.
        public UVRemapService.UvConversion? UvConv;

        // This source's UV is mirrored and UvConv separates the two sides — see SourceSpec.UnmirrorSides.
        public bool UnmirrorSides;

        // The vertical extent of every OTHER part in this shell — what makes "is this connector redundant?"
        // answerable. See SourceSpec.OtherPartBands and CoveredByAnotherPart.
        public IReadOnlyList<(float Lo, float Hi)>? OtherPartBands;

        // Which of this source's meshes belong in the shell, and whether its connector heuristic runs.
        // Both per-source: see SourceSpec.
        public Func<string, bool> Keep = IsBodySkinMaterial;
        public bool DropConnectors;
    }

    /// <summary>One mesh's index edits for a shape: for the mesh whose index range begins at
    /// <paramref name="MeshIndexOffset"/>, each value redirects index entry <c>Base</c> → vertex <c>Replace</c>.</summary>
    internal readonly record struct ShapeMeshEntry(uint MeshIndexOffset, (ushort Base, ushort Replace)[] Values);

    /// <summary>A model carries at most 10 materials — the game/Penumbra ceiling (Penumbra's own model
    /// importer caps at 10, ModelImporter.MaterialLimit). Host-selection cap, enforced in the caller.</summary>
    public const int MaxMaterials = 10;

    /// <summary>
    /// Build the merged shell. <paramref name="sources"/> are the body models the character is currently
    /// drawing (resolve them live — never ship a prebuilt shell); every one contributes its own mesh
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
    /// As above, but <paramref name="skipConnectors"/> drops each source's redundant connector geometry —
    /// the thin joint seam rings and duplicate variant submeshes — for bodies (Neolithe) that would
    /// otherwise double up on a sheer shell.
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> sources, IReadOnlyList<SecondSkinLayer> layers,
        byte[]? baseModel, bool skipConnectors, out Stats stats,
        IReadOnlyList<HashSet<string>?>? enabledShapes = null, Action<string>? diag = null,
        // Per-source UV-space converter, parallel to `sources`; null entries are already in shell space.
        // See Source.UvConv.
        IReadOnlyList<UVRemapService.UvConversion?>? uvConverters = null)
        => Build(
            sources.Select((m, i) => new SourceSpec(
                m,
                KeepMaterial: null,   // body-skin filter, the behaviour every existing caller expects
                EnabledShapes: enabledShapes != null && i < enabledShapes.Count ? enabledShapes[i] : null,
                UvConv: uvConverters != null && i < uvConverters.Count ? uvConverters[i] : null,
                DropConnectors: skipConnectors)).ToList(),
            layers, baseModel, out stats, diag);

    /// <summary>
    /// Build the merged shell from fully-described sources. Every layer is applied to every source, so all
    /// sources here must share one UV space and one race space — that is what makes them one surface.
    /// </summary>
    public static byte[] Build(IReadOnlyList<SourceSpec> sources, IReadOnlyList<SecondSkinLayer> layers,
        byte[]? baseModel, out Stats stats, Action<string>? diag = null)
    {
        if (layers.Count == 0) throw new ArgumentException("need at least one layer", nameof(layers));
        // Sources are the character geometry a SHELL is cut from, so a build made entirely of content
        // layers — an imported pack that brings its own meshes — legitimately has none. Anything else
        // still does: a shell layer with no source would emit nothing at all.
        if (sources.Count == 0 && layers.Any(l => l.Geometry.Count == 0))
            throw new ArgumentException("need at least one source model", nameof(sources));

        var parsed = sources.Select(s => Parse(s.Model)).ToList();

        // Attach each source's enabled shape keys and (Stage 2a) verify the parse against them: does the
        // .mdl actually contain the enabled shape, and how many of its index edits resolve to in-range
        // positions/vertices. This confirms the format read before any geometry is mutated.
        for (int i = 0; i < parsed.Count; i++)
        {
            var en = sources[i].EnabledShapes;
            parsed[i].EnabledShapes = en;
            parsed[i].UvConv = sources[i].UvConv;
            parsed[i].UnmirrorSides = sources[i].UnmirrorSides;
            parsed[i].OtherPartBands = sources[i].OtherPartBands;
            parsed[i].Keep = sources[i].KeepMaterial ?? IsBodySkinMaterial;
            parsed[i].DropConnectors = sources[i].DropConnectors;
            // Warn only on the failure case: an enabled shape the .mdl doesn't actually contain (nothing to
            // bake). The success path is silent — the shell simply follows the body.
            if (en == null || en.Count == 0 || diag == null) continue;
            foreach (var name in en)
                if (!parsed[i].Shapes.ContainsKey(name))
                    diag($"shape '{name}' enabled but not present in source {i} — not baked");
        }
        Source? baseSrc = baseModel != null ? Parse(baseModel) : null;

        // Imported content models, parsed ONCE each: several layers of one pack commonly bind different
        // materials of the same .mdl, and re-parsing it per layer would cost the whole header walk again
        // for no new information. Reference identity is the key because that is exactly what "the same
        // model" means here — the caller hands the same byte[] to every layer cut from it.
        var geomSrcs = new List<Source>();
        var geomByModel = new Dictionary<byte[], Source>(ReferenceEqualityComparer.Instance);
        foreach (var g in layers.SelectMany(l => l.Geometry))
        {
            if (geomByModel.ContainsKey(g.Model)) continue;
            var gs = Parse(g.Model);
            // Deliberately NOT `gs.Keep = g.KeepMaterial`. Source.Keep is unused on this path — the emit
            // loop filters with the GEOMETRY's own predicate — and now that two geometries may share one
            // model (two meshes of one file, or one file bound by two pieces) storing a single filter on
            // the shared Source would quietly be one of them.
            geomByModel[g.Model] = gs;
            geomSrcs.Add(gs);
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
        // Content models contribute bones too — a piece skinned to j_sebo_a needs that bone present in the
        // merged table or its vertices collapse onto the root.
        // Materialised, not lazy: this is walked three times below (bones, attributes, the overflow count)
        // and re-running the concat each time is work for nothing.
        List<Source> boneSources =
            [.. baseSrc != null ? new[] { baseSrc }.Concat(parsed) : parsed, .. geomSrcs];
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

        // Union ATTRIBUTE list, on exactly the same reasoning as the bones above: a submesh's mask indexes
        // its own model's attribute table, so merging two models means renumbering both onto one list.
        //
        // These are what a mod's own checkboxes drive. An accessory pack ships one model carrying a dozen
        // pieces, tags each piece's submeshes with an attribute, and toggles them by NAME through Penumbra's
        // Atr manipulation. Dropping them — which this writer used to do outright — leaves a model with
        // nothing to toggle, so every piece draws at once and the mod's options do nothing.
        //
        // 32 is the ceiling, not a choice: the mask is a u32, so bit 32 does not exist. Past that the extras
        // are left unnamed rather than silently aliased onto another attribute's bit, which would toggle the
        // wrong geometry.
        //
        // Which makes the ceiling worth spending carefully. A source whose every geometry is emitted
        // UNTAGGED — Proteus resolved its visibility and cleared the masks, see ContentGeometry.OwnAttributes
        // — has no submesh left that references its names, so contributing them buys nothing and can cost
        // another pack everything: an outfit carrying a dozen attributes it no longer uses is a dozen slots
        // a name-toggled pack does not get, and the names past 32 are the ones that stop working.
        var ownedOnly = new HashSet<Source>();
        foreach (var (model, src) in geomByModel)
            if (layers.SelectMany(l => l.Geometry).Where(g => ReferenceEquals(g.Model, model))
                      .All(g => g.OwnAttributes))
                ownedOnly.Add(src);

        var attrNames = new List<string>();
        var attrIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var src in boneSources)
            foreach (var name in ownedOnly.Contains(src) ? [] : src.AttrNames)
            {
                if (attrIndex.ContainsKey(name) || attrNames.Count >= 32) continue;
                attrIndex[name] = attrNames.Count;
                attrNames.Add(name);
            }
        // Counted over the same sources the union was built from. Including the untagged ones here would
        // report every name they no longer need as a name that was DROPPED, which is the opposite of true.
        int attrOverflow = boneSources.Where(s => !ownedOnly.Contains(s))
            .SelectMany(s => s.AttrNames).Distinct(StringComparer.Ordinal)
            .Count() - attrNames.Count;

        // One submesh's attribute mask, renumbered from its own model's table onto the union.
        uint RemapAttrs(Source src, uint mask)
        {
            if (mask == 0 || src.AttrNames.Length == 0) return 0;
            uint outMask = 0;
            for (int bit = 0; bit < 32 && bit < src.AttrNames.Length; bit++)
                if ((mask & (1u << bit)) != 0 && attrIndex.TryGetValue(src.AttrNames[bit], out var to))
                    outMask |= 1u << to;
            return outMask;
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
        int hiddenSubs = 0;                // submeshes dropped by a pack's own hide toggles

        // Emit one source mesh into the merged model. Shared by the host pre-pass (preserve=true: an exact
        // byte copy, keep every triangle, keep the authored material index) and the shell layers
        // (preserve=false: BuildVerbatim's push/colour/uv1 rewrites, coverage-trimmed). Mutates the shared
        // accumulators; `cov` null keeps all triangles; `mapBase`/`mapAppended` share the src's submesh bone
        // map across its meshes.
        void EmitMesh(Source src, int m, ushort materialIndex, float push, bool preserve,
                      SecondSkinLayer? cov, int mapBase, ref bool mapAppended, bool dropConnectors,
                      bool mirrorUv1 = false, IReadOnlySet<string>? hiddenAttrs = null,
                      bool clearAttrs = false, IReadOnlyList<(float Lo, float Hi)>? otherBands = null)
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
            (float U, float V)[] uv;
            (float U, float V)[]? uvPre = null;
            if (preserve)
            {
                CopyVerbatim(s, src.Vb, 0x44 + m * DeclSize, vc, decl, vbo, bs, mirrorUv1,
                    out outStreams, out outStrides, out declBlock);
                uv = [];   // no coverage trim for the host mesh
            }
            else
            {
                // Which side of the body each vertex is on, when the conversion needs to tell them apart.
                // Read from the triangles rather than each vertex's own X, because the midline vertices —
                // exactly the ones a mirrored layout puts a UV seam through — sit at x ~ 0 and can't answer
                // for themselves. Only computed when a layer actually un-mirrors; otherwise it is a pass over
                // positions and indices for nothing.
                sbyte[]? sides = null;
                if (src.UnmirrorSides && src.UvConv != null)
                {
                    sides = MeshSides(src, m, vc, decl, vbo, bs, out int sideConflicts, out int sideStraddling);
                    if (sideConflicts > 0 || sideStraddling > 0)
                        // Not fatal: a disputed vertex converts as if it were on the +X side, which is the
                        // behaviour it had before un-mirroring existed. Reported because a mirrored layout
                        // is not supposed to have either (measured zero on every vanilla body part), so a
                        // count here means this surface is laid out in a way this was not measured against.
                        diag?.Invoke($"mesh {m}: {sideConflicts} vertex(es) claimed by both sides and "
                                   + $"{sideStraddling} triangle(s) straddling the midline — those keep the +X half");
                }

                uvUnmapped += BuildVerbatim(s, src.Vb, 0x44 + m * DeclSize, vc, decl, vbo, bs, push,
                    out outStreams, out outStrides, out declBlock, out uv, out uvPre, src.UvConv, sides);
                if (src.UvConv != null) uvMoved += vc;

                // The tile normalization above shifts a mesh by the integer floor of its MINIMUM uv, which
                // brings it onto [0,1] only if the whole mesh sits inside one integer cell. Body meshes do;
                // an atlassed or tiled layout (hair especially) may not, and then part of the mesh keeps a
                // coordinate past 1 and samples the art through the sampler's wrap — art in the wrong place
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
                                   + "— the per-mesh tile shift cannot bring all of it onto [0,1]");
                }
            }

            // Bake enabled body shape keys (e.g. "Remove Hip Dips" = shpx_yam_softbutt) into the shell. A
            // ShapeValue redirects one index-buffer entry to a morphed replacement vertex that already lives
            // in THIS mesh's vertex buffer (within vc). Rewiring the index makes the shell's triangle use the
            // morphed vertex, and the push/compaction below treat it like any other — so the shell follows
            // the body instead of diverging. Only for shell layers (not the host ring) and only for shapes
            // this body has enabled. Bounds-guarded: a replacement >= vc is skipped, so a wrong assumption
            // degrades to "morph not applied", never an out-of-range crash.
            //
            // BaseIndicesIndex is MESH-RELATIVE (0-based within this mesh's own index range), per
            // xivModdingFramework's applier — indices[BaseIndex] where indices is the mesh's list. So the
            // lookup below subtracts the mesh's absolute StartIndex from each triangle's position. (Only when
            // StartIndex == 0 do absolute and relative coincide — that was the one tested case.)
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

            // The biggest submesh in this mesh, as the scale everything else is judged against — see the
            // connector test below for why an absolute triangle count is the wrong yardstick.
            uint largestSub = 0;
            for (int su = 0; su < srcSubCount; su++)
            {
                uint c = U32(src.SubmeshStart + (srcSubIdx + su) * 16 + 4) / 3;
                if (c > largestSub) largestSub = c;
            }

            // Keep a triangle if ANY texel under its UV footprint is visible (cov null = keep all).
            var keptPerSub = new List<ushort[]>();
            var used = new bool[vc];
            for (int su = 0; su < srcSubCount; su++)
            {
                int ss = src.SubmeshStart + (srcSubIdx + su) * 16;
                uint so = U32(ss), sc = U32(ss + 4);
                var keep = new List<ushort>();

                // Drop redundant connector geometry. TWO shapes of redundancy, and they are redundant
                // against DIFFERENT things — which is the whole reason they are tested separately here:
                //
                //  · a thin seam RING at a joint (wrist/ankle/…), redundant because the NEIGHBOURING PART
                //    draws the same stretch of body;
                //  · the mesh's LAST submesh, a duplicate variant (Neolithe's second calf), redundant
                //    because a SIBLING SUBMESH of this same mesh already draws it.
                //
                // Kept empty ⇒ contributes nothing; never applied to a single-submesh mesh (that IS the
                // whole part).
                //
                // "Small" is RELATIVE to this mesh's own largest submesh, not the flat "< 200 triangles" this
                // used to be. That threshold was read off Neolithe, whose real skin parts run 800+ triangles,
                // and it silently ate whole body regions from any lower-poly source: gear that ships its own
                // skin cuts it far coarser — Rinoa's exposed torso is 501 triangles ALL IN, so its neck (20)
                // and its elbow (144) both looked like rings and vanished.
                //
                // And "redundant" is then CHECKED rather than assumed. A ring is only redundant because a
                // neighbouring part covers the same band of the body; the ring at the top of a hand model is
                // covered by the leg model above it, while Rinoa's neck has nothing above it at all. Without
                // this the neck is indistinguishable from a wrist ring by shape or size — 20 triangles in a
                // thin band at the part's own top edge is exactly what a seam ring looks like.
                // A NULL band list means the caller told us nothing about the rest of the shell, so there is
                // no redundancy to test and the old shape-only judgement stands. An EMPTY one is a real
                // answer — this part is alone, nothing can be covering it, so no ring of it is redundant.
                //
                // The duplicate variant does NOT get that same test, and running it against the parts was a
                // bug with a very visible face: no other part of a shell goes anywhere near the middle of a
                // shin, so Neolithe's second calf (2184 triangles, y 0.14-0.41) always read as "nothing
                // covers this", and the shell emitted it INSIDE the calf already there — a doubled sheer
                // stocking from the ankle to below the knee. It is asked about its siblings instead, which
                // is what it actually duplicates.
                //
                // SIZE is what separates the two, so the branches are exclusive on it rather than merely
                // ordered. Being last is a weak signal on its own — a source is free to order its seam ring
                // last, and a ring at a mesh's own top edge is always nested inside that mesh's main
                // submesh, so a sibling test alone would delete it and hand Rinoa her bare neck straight
                // back. A duplicate variant is a body region and reads as one: Neolithe's second calf is
                // half its mesh's largest submesh, where the ankle ring beside it is a fortieth.
                bool ringLike = sc / 3 < largestSub / 10;
                bool duplicateVariant = !ringLike && su == srcSubCount - 1;
                if (dropConnectors && srcSubCount > 1
                    && (ringLike
                        ? otherBands == null || CoveredByAnotherPart(src, decl, vbo, bs, so, sc, otherBands)
                        : duplicateVariant
                          && CoveredBySibling(src, decl, vbo, bs, srcSubIdx, srcSubCount, su, hiddenAttrs)))
                {
                    keptPerSub.Add(keep.ToArray());
                    continue;
                }

                // Switched off by one of the pack's own toggles — see ContentGeometry.HiddenAttributes.
                // Kept empty, exactly like the connector case above, so the submesh contributes nothing
                // while every index and bone table around it keeps its shape.
                if (hiddenAttrs is { Count: > 0 } && IsHidden(src, U32(ss + 8), hiddenAttrs))
                {
                    hiddenSubs++;
                    keptPerSub.Add(keep.ToArray());
                    continue;
                }
                for (uint t = 0; t + 2 < sc; t += 3)
                {
                    int p = src.Ib + (int)(so + t) * 2;
                    ushort a = BitConverter.ToUInt16(s, p), b = BitConverter.ToUInt16(s, p + 2), c = BitConverter.ToUInt16(s, p + 4);
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
                    keep.Add(a); keep.Add(b); keep.Add(c);
                    used[a] = used[b] = used[c] = true;
                    triOut++;
                }
                keptPerSub.Add(keep.ToArray());
            }
            if (keptPerSub.All(k => k.Length == 0)) return;   // paints nothing here

            // The UVs just moved to another layout, so the tangent frame copied in with them no longer
            // describes them. Re-fit before compaction, while indices still address the source's vertices.
            if (uvPre != null && RetangentMesh(outStreams, outStrides, decl, vc, uvPre, uv, keptPerSub))
                uvRetangented++;

            if (!mapAppended)
            {
                // The map's ENTRIES are indices into THIS source's own bone-name list, so they need the same
                // by-name remap onto the union list that the mesh bone tables get below — only the OFFSETS
                // into the map are rebased (mapBase, written into the submesh header as boneStart).
                //
                // Appended verbatim, they were identity-correct for exactly one source: whichever seeded the
                // union list first (the host when appending, else source 0). Every later source's entries
                // then named arbitrary union bones. It has never shown because today's sources are body parts
                // from one body mod, whose bone lists match in both content and order — merge a model with a
                // genuinely different skeleton subset beside them and the identity is gone.
                foreach (var b in src.SubmeshBoneMap)
                {
                    var bn = b < src.BoneNames.Length ? src.BoneNames[b] : null;
                    submeshBoneMap.Add(bn != null && boneIndex.TryGetValue(bn, out var bi) ? bi : (ushort)0);
                }
                mapAppended = true;
            }

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
                // Cleared when Proteus owns this geometry's visibility — see
                // ContentGeometry.OwnAttributes. Leaving the tag on would let the HOST item's IMC
                // mask cull a submesh we already decided to keep.
                W32(ns, 8, clearAttrs ? 0 : RemapAttrs(src, U32(ss + 8)));
                W16(ns, 12, (ushort)(U16(ss + 12) + mapBase));            // boneStart, rebased
                W16(ns, 14, U16(ss + 14));                                // boneCount, as authored
                subsForMesh.Add(ns);
                keptSubs++;
            }

            // This mesh's OWN bone table, entries remapped onto the union list. Never merged with
            // other meshes' tables — ubyte4 vertex indices cap a table at 255 entries.
            var srcTable = srcBoneTbl < src.BoneTables.Length ? src.BoneTables[srcBoneTbl] : [];
            var table = new ushort[srcTable.Length];
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
        // material indices (0..baseMatCount-1) — so the accessory still renders under the appended shell.
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
            // Scaled per surface: the separation between stacked shells is scaled with the base offset so
            // a small surface's layers stay proportionally apart rather than collapsing onto each other.
            float push = (BaseOffset + LayerSeparation * layer) * def.PushScale;
            ushort matIndex = (ushort)(baseMatCount + layer);

            // An imported content layer brings its own geometry, so it is copied exactly as the host is —
            // preserve:true, no push, no coverage trim. Pushing it would lift a piercing off the skin it
            // was modelled against, and trimming it would need a coverage map the pack never authored: its
            // silhouette IS its mesh. Only the material index is ours to set.
            //
            // Every geometry of the layer is emitted at the SAME material index. That is what lets a mod's
            // several pieces share one published material and therefore one of the host's ten slots.
            if (def.Geometry.Count > 0)
            {
                foreach (var geo in def.Geometry)
                {
                    var gsrc = geomByModel[geo.Model];
                    var gs = gsrc.S;
                    // Per geometry, not per layer: each contributes its own copy of its source's submesh
                    // bone map, exactly as each (source, layer) pair does on the shell path below.
                    int gMapBase = submeshBoneMap.Count;
                    bool gMapAppended = false;
                    int gEnd = gsrc.Lod0MeshIndex + gsrc.Lod0MeshCount;
                    for (int m = gsrc.Lod0MeshIndex; m < gEnd && m < gsrc.MeshCount; m++)
                    {
                        int gmo = gsrc.MeshStart + m * 36;
                        if (BitConverter.ToUInt16(gs, gmo) == 0) continue;   // empty placeholder mesh

                        ushort gMat = BitConverter.ToUInt16(gs, gmo + 8);
                        if (gMat >= gsrc.MatNames.Count || !geo.KeepMaterial(gsrc.MatNames[gMat]))
                            continue;

                        EmitMesh(gsrc, m, matIndex, 0f, preserve: true, cov: null, gMapBase, ref gMapAppended,
                            dropConnectors: false, mirrorUv1: geo.MirrorUv1,
                            hiddenAttrs: geo.HiddenAttributes, clearAttrs: geo.OwnAttributes);
                    }
                }
                continue;
            }

            foreach (var src in parsed)
            {
                var s = src.S;
                ushort U16(int o) => BitConverter.ToUInt16(s, o);

                // Each (source, layer) pair contributes its own copy of the source's submesh bone map.
                int mapBase = submeshBoneMap.Count;
                bool mapAppended = false;

                // LOD0 meshes only — never the lower LODs (a full game model has all three; merging them
                // stacks overlapping low-poly copies that fling geometry across the scene).
                int mEnd = src.Lod0MeshIndex + src.Lod0MeshCount;
                for (int m = src.Lod0MeshIndex; m < mEnd && m < src.MeshCount; m++)
                {
                    int mo = src.MeshStart + m * 36;
                    if (U16(mo) == 0) continue;   // empty mesh

                    // Which meshes of this source belong in the shell — see SourceSpec.KeepMaterial. For a
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

                    EmitMesh(src, m, matIndex, push, preserve: false, cov: def, mapBase, ref mapAppended,
                        dropConnectors: src.DropConnectors, otherBands: src.OtherPartBands);
                }
            }
        }

        // Nothing to write. WHICH filter emptied it decides how the caller reports this: coverage trimming
        // going this far is a fault worth an error in the log, while a pack's own hide toggles emptying a
        // host is the user getting exactly what they asked for. Both used to arrive as "no geometry
        // survived coverage trimming", which sent someone who had ticked two checkboxes looking for a UV
        // bug that was not there.
        if (meshOut.Count == 0)
            throw new EmptyShellException(hiddenSubs > 0
                ? $"every mesh was hidden by the pack's own toggles ({hiddenSubs} submesh(es))"
                : "no geometry survived coverage trimming",
                byToggle: hiddenSubs > 0);

        int meshCount = meshOut.Count;
        int boneCount = boneNames.Count;

        // ── string block: bone names (union), attribute names (union), material names ──
        var strMs = new MemoryStream();
        var boneStrOff = new List<uint>();
        foreach (var b in boneNames)
        {
            boneStrOff.Add((uint)strMs.Position);
            strMs.Write(Encoding.ASCII.GetBytes(b));
            strMs.WriteByte(0);
        }
        var attrStrOff = new List<uint>();
        foreach (var a in attrNames)
        {
            attrStrOff.Add((uint)strMs.Position);
            strMs.Write(Encoding.ASCII.GetBytes(a));
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
        // …or, for a build made entirely of imported content, that pack's first model — same reasoning:
        // whichever source the emitted geometry actually came from is the one whose flags describe it.
        var head = parsed.Count > 0 ? parsed[0] : geomSrcs[0];

        // The CULLING quantities are different — they are about extent, and the merged model's extent is the
        // union of everything in it, exactly as UnionModelBBoxes already treats the bounding boxes. Taking
        // source 0's alone understates them the moment the sources differ in size, and understating a radius
        // or a clip distance means the game culls the shell while the body it copies is still on screen —
        // the shell blinking out at an angle or a distance, with nothing in the log. Max is the only safe
        // direction here: too large costs a little overdraw, too small loses the shell.
        float radius = head.Radius, modelClip = head.ModelClip, shadowClip = head.ShadowClip;
        foreach (var src in (baseSrc != null ? new[] { baseSrc }.Concat(parsed) : parsed).Concat(geomSrcs))
        {
            if (src.Radius     > radius)     radius     = src.Radius;
            if (src.ModelClip  > modelClip)  modelClip  = src.ModelClip;
            if (src.ShadowClip > shadowClip) shadowClip = src.ShadowClip;
        }

        uint stackSize = (uint)(meshCount * DeclSize);

        var ms = new MemoryStream();
        // ModelFileHeader, copied from source 0 and patched below — EXCEPT the version, which is forced to
        // v6 because that is the only bone-table format this writer emits (see WriteBoneTablesV6).
        //
        // Copying the version verbatim made the output describe itself wrongly the moment source 0 was a v5
        // model: a v5 header over v6 bone tables. The game then reads the tables as v5's fixed 132-byte
        // structs, every mesh's table comes out as unrelated bytes, and each vertex weights to whatever joint
        // those bytes happen to name — the whole shell flails. It stayed hidden while every source was v6;
        // gear-bundled vanilla skin (Rinoa's top is v5, and sorts first) is what put a v5 model at index 0.
        var fileHeader = new byte[0x44];
        Array.Copy(head.S, fileHeader, 0x44);
        BitConverter.TryWriteBytes(fileHeader.AsSpan(0), MdlVersionV6);
        ms.Write(fileHeader);
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
        W16(mh, 6, (ushort)attrNames.Count);                        // attribute names, carried
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
        // BETWEEN the meshes and the submeshes — the format puts the attribute name table there, and the
        // parser above locates the submeshes by stepping over it. Writing it anywhere else shifts every
        // table after it.
        foreach (var off in attrStrOff) { BitConverter.TryWriteBytes(tmp4, off); ms.Write(tmp4); }
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
        ms.Write(UnionModelBBoxes(baseSrc != null ? [baseSrc, .. parsed, .. geomSrcs] : [.. parsed, .. geomSrcs]));
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
        // 0/23/46/69/92 and boneCount 23 — windows reaching 115 — against a submesh bone map of 35 entries.
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
                           + "union list — the by-name remap failed to place them");
        }

        if (attrNames.Count > 0)
            diag?.Invoke($"attributes: {attrNames.Count} carried [{string.Join(", ", attrNames)}]");
        // Said out loud because the consequence is a checkbox that quietly stops working: the mask is a u32,
        // so an attribute past the 32nd has no bit to live in and whatever it switched is stuck on.
        if (attrOverflow > 0)
            diag?.Invoke($"ATTRIBUTES: {attrOverflow} past the 32 a submesh mask can address were dropped — "
                       + "whatever those switched can no longer be turned off");

        if (shapedTotal > 0) diag?.Invoke($"shape bake: {shapedTotal} index entries rewired to morphed vertices");
        // Per LAYER, not per vertex: every layer rebuilds the same sources, so these count each source's
        // vertices once for each of them. Divided back out so the number means what it says.
        if (uvMoved > 0)
            diag?.Invoke($"uv conversion: {uvMoved / layers.Count} vertices moved into the shell's UV space"
                       + (uvUnmapped > 0 ? $", {uvUnmapped / layers.Count} left as authored (no correspondence)" : "")
                       + $", {uvRetangented / layers.Count} mesh(es) re-tangented");

        stats = new Stats(meshCount, subOut.Count, boneCount, triIn, triOut, vertOut);
        return o;
    }

    /// <summary>
    /// v6 bone tables: a header per table ({u16 offset, u16 size}) followed by the index data. The offset
    /// is in DWORDS and relative to that table's OWN header — not to the section start.
    /// </summary>
    private static void WriteBoneTablesV6(MemoryStream ms, List<ushort[]> tables)
    {
        long start = ms.Position;
        int headerBytes = tables.Count * 4;
        long dataPos = start + headerBytes;

        // Hoisted out of the loop: a stackalloc inside one accumulates a frame per iteration and never
        // releases until the method returns, so a long table list could run the stack down. Reused
        // rather than re-allocated — every write below fills both bytes before reading it.
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

    /// <summary>
    /// Is this submesh switched off by the pack's toggles?
    /// <para/>
    /// A submesh draws only when EVERY attribute it names is on, so one hidden name is enough to drop it.
    /// An untagged submesh (mask 0) is drawn unconditionally.
    /// <para/>
    /// This started as the opposite lean — keep while any name is still on — chosen when nothing here had
    /// measured the game's rule, on the grounds that geometry wrongly kept is recoverable and geometry
    /// wrongly dropped is not. The deadrose dress settles it. Its dress material carries a submesh tagged
    /// <c>atr_tv_b</c> AND, separately, two tagged <c>atr_tv_b + atr_tv_c</c>. Under "any name on" the
    /// second pair could never differ from the first, so authoring them would be pointless; they only mean
    /// something distinct if the extra tag is a further REQUIREMENT. That is how a pack says "this piece
    /// only with the skirt and the long sleeves".
    /// <para/>
    /// It composes with the ten-bit limit rather than fighting it. An IMC mask addresses bits 0-9, so an
    /// attribute past that — the same model's <c>atr_ude</c> at bit 11 — is never in
    /// <paramref name="hidden"/> and never the reason a submesh goes. Its sleeve submeshes are tagged
    /// <c>atr_tv_f + atr_ude</c> and correctly follow <c>atr_tv_f</c> alone.
    /// </summary>
    private static bool IsHidden(Source src, uint mask, IReadOnlySet<string> hidden)
    {
        if (mask == 0) return false;
        for (int bit = 0; bit < 32 && bit < src.AttrNames.Length; bit++)
            if ((mask & (1u << bit)) != 0 && hidden.Contains(src.AttrNames[bit]))
                return true;
        return false;
    }

    /// <summary>First .mdl version with the Dawntrail bone-table layout (a header array plus a shared index
    /// pool). Anything older stores a fixed <see cref="V5BoneTableBytes"/>-byte struct per table.</summary>
    private const uint MdlVersionV6 = 0x01000006;

    /// <summary>v5 bone table: <c>u16 BoneIndex[64]</c> then <c>u32 BoneCount</c>.</summary>
    private const int V5BoneTableBytes = 132;

    internal static Source Parse(byte[] s)
    {
        uint U32(int o) => BitConverter.ToUInt32(s, o);
        ushort U16(int o) => BitConverter.ToUInt16(s, o);

        // Dawntrail (v6) or earlier (v5). Only the bone-table block differs — see the read below.
        bool isV6 = U32(0) >= MdlVersionV6;

        ushort declCount = U16(12);
        uint vtxOff = U32(16), idxOff = U32(28);
        int declEnd = 0x44 + declCount * DeclSize;

        // Vertex declarations: declCount blocks of up to 17 elements (8 bytes each), one block per mesh,
        // terminated by a Stream == 0xFF sentinel. { Stream, Offset, Type, Usage, UsageIndex, 3× pad }.
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
        // ever want LOD0 — merging the lower LODs would stack overlapping low-poly copies (polys flying
        // everywhere). LOD struct: { u16 MeshIndex, u16 MeshCount, … } at lodStart.
        ushort lod0MeshIndex = U16(lodStart), lod0MeshCount = U16(lodStart + 2);
        int meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);
        int attrStart = meshStart + meshCount * 36;
        int submeshStart = attrStart + attrCount * 4 + tsMesh * 20;
        int matOffStart = submeshStart + submeshCount * 16 + tsSubmesh * 12;
        int boneOffStart = matOffStart + matCount * 4;
        int p = boneOffStart + boneCount * 4;

        // Bounds-guarded because a caller cannot always vouch for the offset: the shape block below is
        // documented as leaving Shapes empty on a malformed block, and it reads a NAME before it can judge
        // anything — so without this an offset outside the string table throws out of the whole parse
        // instead, taking the entire second-skin build with it. An empty name simply fails to match any
        // enabled shape, which is the "leaves Shapes empty" behaviour that block already intends.
        string Str(uint rel)
        {
            int o = strBlock + (int)rel;
            // >= strSize, not > : an offset EQUAL to the block size is one past its last byte, which lands on
            // the model header and reads its bytes back as a name.
            if (rel >= strSize || o < 0 || o >= s.Length) return "";
            int e = o;
            while (e < s.Length && s[e] != 0) e++;
            return Encoding.ASCII.GetString(s, o, e - o);
        }

        var boneNames = new string[boneCount];
        for (int i = 0; i < boneCount; i++) boneNames[i] = Str(U32(boneOffStart + i * 4));

        // Attribute names, in the order the submesh masks index them — see Source.AttrNames.
        var attrNames = new string[attrCount];
        for (int i = 0; i < attrCount; i++) attrNames[i] = Str(U32(attrStart + i * 4));

        // ── Bone tables ──────────────────────────────────────────────────────
        // The ONE block whose layout changed at Dawntrail, and everything after it — the shape block, the
        // submesh bone map, the bounding boxes — is positioned relative to its end. Reading a v5 model with
        // the v6 layout walks the wrong distance and puts every later read mid-file: the shape block comes
        // out as arbitrary bytes and a shape's name offset lands outside the string table. Mods still ship
        // v5 models, so this is reached by ordinary gear, not by anything exotic.
        var tables = new ushort[boneTableCount][];
        if (isV6)
        {
            // Header array of { u16 offsetInDwords, u16 count } — the offset is relative to the table's OWN
            // header — followed by one pool shared by every table.
            for (int i = 0; i < boneTableCount; i++)
            {
                int headerPos = p + i * 4;
                ushort off = U16(headerPos), size = U16(headerPos + 2);
                int data = headerPos + off * 4;
                var t = new ushort[size];
                for (int k = 0; k < size; k++) t[k] = U16(data + k * 2);
                tables[i] = t;
            }
            p += boneTableCount * 4 + U16(mh + 44) * 2;             // headers + BoneTableArrayCountTotal
        }
        else
        {
            // Fixed struct per table, no pool: u16 BoneIndex[64] then u32 BoneCount.
            for (int i = 0; i < boneTableCount; i++)
            {
                int at = p + i * V5BoneTableBytes;
                // CLAMPED both ways. BoneCount is read straight out of the file, so a model that isn't
                // really v5 — truncated, repacked by a broken tool, or misaligned for any reason — puts
                // arbitrary bytes here. Math.Min alone bounds only the top: a value past int.MaxValue casts
                // NEGATIVE, passes the upper clamp untouched, and `new ushort[negative]` throws out of Parse
                // and takes the whole shell build with it. The v6 arm cannot do this because its count is a
                // ushort; this one has to say so explicitly.
                long declared = at + V5BoneTableBytes <= s.Length ? U32(at + 128) : 0;
                var t = new ushort[Math.Clamp(declared, 0, 64)];
                for (int k = 0; k < t.Length; k++) t[k] = U16(at + k * 2);
                tables[i] = t;
            }
            p += boneTableCount * V5BoneTableBytes;
        }

        // ── Shape (morph) block ──────────────────────────────────────────────
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
            DeclEnd = declEnd,
            StrSize = strSize,
            LodStart = lodStart,
            AttrStart = attrStart,
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
            AttrNames = attrNames,
            BoneTables = tables,
            SubmeshBoneMap = map,
            ShapeBlock = shapeBlock,
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
    /// The body carries up to 8 bone influences and gear holds 4, but almost every body vertex uses ≤4
    /// and the rest discard a fraction of a percent of their weight — measured, harmless.
    /// </summary>
    /// <summary>
    /// Copy each vertex VERBATIM into the shell, preserving the source model's own vertex format — blend
    /// weights, bone indices, UVs and tangents are never decoded or reinterpreted, so any body (vanilla,
    /// bibo, Neolithe, …) skins exactly as authored and the byte-format zoo stops mattering. Only what
    /// the shell genuinely needs is touched: position is pushed out along its normal (z-fight clearance),
    /// vertex colour is forced white (the gear shaders gate emissive on it), and a second UV set is
    /// appended when the source lacks one (characterscroll samples its scroll map with uv1). Output
    /// stream strides equal the source's (the uv1 stream grown by the copy). Also returns this mesh's
    /// declaration block (source decl, plus the uv1 element) and decoded uv0 for the coverage test.
    /// </summary>
    /// <summary>
    /// Copy a host (ring/bracelet) mesh's vertex streams and declaration byte-for-byte, with NONE of the
    /// shell tricks — no push, no colour-whiten, no uv1 mirroring, no UV normalization. The accessory must
    /// render exactly as authored, so its format passes through untouched.
    /// </summary>
    /// <summary>
    /// Where a mesh's uv1 lives, and what it would take to give it one — the single description
    /// <see cref="BuildVerbatim"/> and <see cref="CopyVerbatim"/> both work from.
    /// <para/>
    /// Three shapes, and the reason there are three is the format: a Float4 (type 3) or Half4 (type 14) uv0
    /// packs a second UV in its <c>.zw</c>; some models instead declare a separate <c>usage 4 index 1</c>
    /// element; and a mesh with a bare 2-component uv0 and neither has no uv1 at all, so one must be
    /// APPENDED to uv0's own stream — that stream is guaranteed present, which a hard-coded stream 1 is not.
    /// <para/>
    /// A model can have both the packed and the explicit form at once (the sample piercings pack does), and
    /// which one the shader reads is not worth guessing: every slot is written.
    /// </summary>
    private readonly record struct Uv1Plan(
        bool ZwValid, int ZwOffset, bool ZwHalf, VElem? Explicit, bool Append, int Stream, int AppendOffset)
    {
        /// <summary>Bytes this adds to <see cref="Stream"/>'s stride. Zero unless a uv1 is appended.</summary>
        public int ExtraBytes => Append ? 8 : 0;
    }

    private static Uv1Plan PlanUv1(VElem? uv0, VElem? uv1El, byte[] bs)
    {
        bool zwValid = uv0 is { } uz && (uz.Type == 3 || uz.Type == 14);
        int  zwOff   = uv0 is { } uo ? uo.Offset + (uo.Type == 3 ? 8 : 4) : 0;
        bool zwHalf  = uv0 is { } uh && uh.Type == 14;
        int  stream  = uv0 is { } us ? us.Stream : 1;
        return new Uv1Plan(zwValid, zwOff, zwHalf, uv1El,
            Append: uv0 is not null && !zwValid && uv1El is null,
            Stream: stream, AppendOffset: bs[stream]);
    }

    /// <summary>Write one vertex's (u, v) into every uv1 slot the plan names.</summary>
    private static void WriteUv1(
        in Uv1Plan p, VElem uv0, byte[][] outStreams, byte[] outStrides, int i, float u, float v)
    {
        if (p.ZwValid)
            WriteUV2(outStreams[uv0.Stream], i * outStrides[uv0.Stream] + p.ZwOffset, p.ZwHalf, u, v);
        if (p.Explicit is { } e1)
            WriteUV2(outStreams[e1.Stream], i * outStrides[e1.Stream] + e1.Offset, e1.Type is 13 or 14, u, v);
        if (p.Append)
            WriteUV2(outStreams[p.Stream], i * outStrides[p.Stream] + p.AppendOffset, false, u, v);
    }

    /// <summary>Splice a Float2 uv1 into a declaration block, when the plan appended one. The .zw and
    /// existing-uidx1 cases already declare theirs, so this no-ops for them.</summary>
    private static void SpliceUv1Decl(byte[] declBlock, in Uv1Plan p)
    {
        if (!p.Append) return;
        for (int e = 0; e < 17; e++)
        {
            int o = e * 8;
            if (declBlock[o] != 0xFF) continue;
            declBlock[o]     = (byte)p.Stream;
            declBlock[o + 1] = (byte)p.AppendOffset;
            declBlock[o + 2] = 1;                         // Float2
            declBlock[o + 3] = UseUV;
            declBlock[o + 4] = 1;                         // usageIndex 1
            if (e + 1 < 17) declBlock[(e + 1) * 8] = 0xFF;
            break;
        }
    }

    private static void CopyVerbatim(
        byte[] s, int vb, int srcDeclOff, ushort vc, VElem[] decl, uint[] vbo, byte[] bs, bool mirrorUv1,
        out byte[][] outStreams, out byte[] outStrides, out byte[] declBlock)
    {
        // Match BuildVerbatim's stream count: every stream carrying data OR named by a decl element.
        int streamCount = bs[2] > 0 ? 3 : (bs[1] > 0 ? 2 : 1);
        foreach (var el in decl) streamCount = Math.Max(streamCount, Math.Min((int)el.Stream, 2) + 1);

        // uv1 is touched ONLY for a glowing content piece — characterscroll samples its scroll map with it,
        // and a model's own uv1 is as likely to hold an unrelated aux coordinate as a usable texcoord (see
        // BuildVerbatim, which resolved the same ambiguity by overwriting). Everything else about this copy
        // stays byte-for-byte: the piece must render exactly as its author built it.
        VElem? uv0 = null, uv1El = null;
        if (mirrorUv1)
            foreach (var el in decl)
                if (el.Usage == UseUV)
                {
                    if (el.UsageIndex == 0) uv0 ??= el; else uv1El ??= el;
                }
        var plan = PlanUv1(uv0, uv1El, bs);
        bool doMirror = mirrorUv1 && uv0 is not null;

        outStrides = new byte[streamCount];
        for (int st = 0; st < streamCount; st++) outStrides[st] = bs[st];
        if (doMirror && plan.Append) outStrides[plan.Stream] = (byte)(bs[plan.Stream] + plan.ExtraBytes);

        outStreams = new byte[streamCount][];
        for (int st = 0; st < streamCount; st++)
        {
            outStreams[st] = new byte[vc * outStrides[st]];
            for (int i = 0; i < vc; i++)
                Array.Copy(s, vb + (int)vbo[st] + i * bs[st], outStreams[st], i * outStrides[st], bs[st]);
        }

        declBlock = new byte[DeclSize];
        Array.Copy(s, srcDeclOff, declBlock, 0, DeclSize);

        if (doMirror)
        {
            var u0 = uv0!.Value;
            Span<float> tmp = stackalloc float[4];
            for (int i = 0; i < vc; i++)
            {
                // The AUTHORED uv0, unshifted and unnormalized — unlike the shell path, which mirrors the
                // value it moved onto the [0,1] tile. A content mesh keeps its own UV island and the
                // material's tiling constants set how densely the pattern repeats across it.
                ReadTyped(s, vb + (int)vbo[u0.Stream] + i * bs[u0.Stream] + u0.Offset, u0.Type, tmp);
                WriteUv1(plan, u0, outStreams, outStrides, i, tmp[0], tmp[1]);
            }
            SpliceUv1Decl(declBlock, plan);
        }
    }

    /// <summary>Returns the number of vertices <paramref name="uvConv"/> had no correspondence for (0 when
    /// there is no conversion). Those keep their original UV — see the normalization block.
    /// <paramref name="uvsPreConv"/> holds the UVs as they were BEFORE the conversion (null when there was
    /// none): <see cref="RetangentMesh"/> needs both layouts to re-fit the tangent frame.</summary>
    /// <summary>
    /// Which side of the body each vertex of mesh <paramref name="m"/> is on, for a source whose UV is
    /// mirrored. Decodes this mesh's positions and walks its own submeshes' triangles, then hands both to
    /// <see cref="SurfaceMirror.AssignSides"/>. Raw indices on purpose: a shape key redirects an index to a
    /// morphed vertex a fraction of a unit away, which cannot move a vertex to the other side of the body.
    /// </summary>
    /// <summary>
    /// Does another part of this shell already cover the vertical band this submesh occupies?
    /// <para/>
    /// This is what "redundant connector" actually means. A seam ring at a part's edge is safe to drop only
    /// because the neighbouring part draws the same stretch of body — a hand model's top ring sits inside
    /// the leg model's range, an ankle ring inside the shoe's. Geometry with nothing beside it is not a
    /// connector however ring-shaped it looks, and dropping it leaves a bare band of the character wearing
    /// the old skin.
    /// <para/>
    /// Compared on Y alone, which is coarse but is the axis parts are split along; the caller has already
    /// established the submesh is small relative to its mesh, so this only has to separate "at a join" from
    /// "at the end of the character". Answers FALSE when nothing else is in the shell — a lone part has no
    /// neighbour, so none of its geometry is redundant.
    /// </summary>
    private static bool CoveredByAnotherPart(Source src, VElem[] decl, uint[] vbo, byte[] bs,
        uint so, uint sc, IReadOnlyList<(float Lo, float Hi)> otherBands)
        => otherBands.Count > 0
        && SubmeshBand(src, decl, vbo, bs, so, sc) is { } band
        && BandCovered(band, otherBands);

    /// <summary>
    /// Does another submesh of the SAME mesh already draw the band this one occupies?
    /// <para/>
    /// The other half of "redundant", and the one <see cref="CoveredByAnotherPart"/> cannot answer. A body's
    /// duplicate variant submesh — Neolithe's second calf — is redundant against its own sibling, not against
    /// a neighbouring part, so asking the parts about it is asking the wrong question: no other part is
    /// anywhere near the middle of a shin, the test says "not redundant", and the shell emits both copies of
    /// the calf, one inside the other. That is what a doubled sheer stocking is made of.
    /// <para/>
    /// Same Y-only comparison as the part test, for the same reason, and with the same answer when nothing
    /// can be measured: false, keep the geometry.
    /// <para/>
    /// A sibling switched off by one of the pack's own toggles does NOT count, because it is not going to be
    /// drawn: the emit loop empties those a few lines below this one's caller, so counting them would drop
    /// the variant on the strength of a submesh that ends up contributing nothing and leave the band bare.
    /// </summary>
    private static bool CoveredBySibling(Source src, VElem[] decl, uint[] vbo, byte[] bs,
        int subBase, int subCount, int self, IReadOnlySet<string>? hiddenAttrs)
    {
        var s = src.S;
        int So(int su) => src.SubmeshStart + (subBase + su) * 16;
        if (SubmeshBand(src, decl, vbo, bs,
                BitConverter.ToUInt32(s, So(self)), BitConverter.ToUInt32(s, So(self) + 4)) is not { } band)
            return false;

        for (int su = 0; su < subCount; su++)
        {
            if (su == self) continue;
            if (hiddenAttrs is { Count: > 0 }
                && IsHidden(src, BitConverter.ToUInt32(s, So(su) + 8), hiddenAttrs)) continue;
            if (SubmeshBand(src, decl, vbo, bs,
                    BitConverter.ToUInt32(s, So(su)), BitConverter.ToUInt32(s, So(su) + 4)) is not { } other)
                continue;
            if (BandCovered(band, other)) return true;
        }
        return false;
    }

    /// <summary>The vertical extent of one submesh, or null when the positions can't be read.</summary>
    private static (float Lo, float Hi)? SubmeshBand(Source src, VElem[] decl, uint[] vbo, byte[] bs,
        uint so, uint sc)
    {
        VElem? pos = null;
        foreach (var el in decl) if (el.Usage == UsePosition) { pos = el; break; }
        if (pos is not { } pe || pe.Stream > 2 || bs[pe.Stream] == 0) return null;

        var s = src.S;
        float lo = float.MaxValue, hi = float.MinValue;
        Span<float> tmp = stackalloc float[4];
        for (uint t = 0; t < sc; t++)
        {
            int ip = src.Ib + (int)(so + t) * 2;
            if (ip + 2 > s.Length) break;
            int vi = BitConverter.ToUInt16(s, ip);
            int a = (int)(src.Vb + vbo[pe.Stream]) + vi * bs[pe.Stream] + pe.Offset;
            if (a < 0 || a + 16 > s.Length) continue;
            ReadTyped(s, a, pe.Type, tmp);
            if (tmp[1] < lo) lo = tmp[1];
            if (tmp[1] > hi) hi = tmp[1];
        }
        return lo <= hi ? (lo, hi) : null;
    }

    /// <summary>Is <paramref name="band"/> contained in <paramref name="cover"/>?
    /// <para/>
    /// A hair of tolerance: geometry is authored to MEET, so ranges abut rather than overlap, and an exact
    /// containment test would keep every ring that pokes a fraction past its neighbour's edge.</summary>
    private static bool BandCovered((float Lo, float Hi) band, (float Lo, float Hi) cover)
    {
        const float Slack = 0.01f;
        return band.Lo >= cover.Lo - Slack && band.Hi <= cover.Hi + Slack;
    }

    /// <summary>Is <paramref name="band"/> contained in ANY of <paramref name="covers"/>?</summary>
    private static bool BandCovered((float Lo, float Hi) band, IReadOnlyList<(float Lo, float Hi)> covers)
    {
        foreach (var cover in covers)
            if (BandCovered(band, cover)) return true;
        return false;
    }

    private static sbyte[]? MeshSides(Source src, int m, ushort vc, VElem[] decl, uint[] vbo, byte[] bs,
        out int conflicts, out int straddling)
    {
        conflicts = 0;
        straddling = 0;
        var s = src.S;
        VElem? pos = null;
        foreach (var el in decl) if (el.Usage == UsePosition) { pos = el; break; }
        if (pos is not { } pe) return null;

        var xs = new float[vc];
        Span<float> tmp = stackalloc float[4];
        for (int i = 0; i < vc; i++)
        {
            ReadTyped(s, src.Vb + (int)vbo[pe.Stream] + i * bs[pe.Stream] + pe.Offset, pe.Type, tmp);
            xs[i] = tmp[0];
        }

        int mo = src.MeshStart + m * 36;
        ushort srcSubIdx = BitConverter.ToUInt16(s, mo + 10), srcSubCount = BitConverter.ToUInt16(s, mo + 12);
        var tris = new List<ushort>();
        for (int su = 0; su < srcSubCount; su++)
        {
            int ss = src.SubmeshStart + (srcSubIdx + su) * 16;
            uint so = BitConverter.ToUInt32(s, ss), sc = BitConverter.ToUInt32(s, ss + 4);
            for (uint t = 0; t + 2 < sc; t += 3)
            {
                int p = src.Ib + (int)(so + t) * 2;
                if (p + 5 >= s.Length) break;
                tris.Add(BitConverter.ToUInt16(s, p));
                tris.Add(BitConverter.ToUInt16(s, p + 2));
                tris.Add(BitConverter.ToUInt16(s, p + 4));
            }
        }
        return SurfaceMirror.AssignSides(xs, tris, out conflicts, out straddling);
    }

    private static int BuildVerbatim(
        byte[] s, int vb, int srcDeclOff, ushort vc, VElem[] decl, uint[] vbo, byte[] bs, float push,
        out byte[][] outStreams, out byte[] outStrides, out byte[] declBlock, out (float U, float V)[] uvs,
        out (float U, float V)[]? uvsPreConv,
        UVRemapService.UvConversion? uvConv = null, sbyte[]? sides = null)
    {
        int uvUnmapped = 0;
        uvsPreConv = null;
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
        // models add a separate uidx1 element) — junk for scrolling, so we overwrite every uv1 slot with
        // uv0. Only when uv0 is a bare 2-component element with no uidx1 do we append a Float2 uv1 — into
        // uv0's OWN stream (guaranteed present), not a hard-coded stream 1. See Uv1Plan, which CopyVerbatim
        // shares so a glowing content mesh cannot drift from this.
        var uv1Plan = PlanUv1(uv0, uv1El, bs);

        outStrides = new byte[streamCount];
        for (int st = 0; st < streamCount; st++) outStrides[st] = bs[st];
        if (uv1Plan.Append) outStrides[uv1Plan.Stream] = (byte)(bs[uv1Plan.Stream] + uv1Plan.ExtraBytes);
        outStreams = new byte[streamCount][];
        for (int st = 0; st < streamCount; st++) outStreams[st] = new byte[vc * outStrides[st]];

        uvs = new (float, float)[vc];
        Span<float> tmp = stackalloc float[4];
        int SrcAddr(int st, int i, int off) => vb + (int)vbo[st] + i * bs[st] + off;

        for (int i = 0; i < vc; i++)
        {
            for (int st = 0; st < streamCount; st++)
                Array.Copy(s, vb + (int)vbo[st] + i * bs[st], outStreams[st], i * outStrides[st], bs[st]);

            // Push position along its normalized normal, re-encoded in the position's own type.
            if (pos is { } pe && norm is { } ne)
            {
                ReadTyped(s, SrcAddr(ne.Stream, i, ne.Offset), ne.Type, tmp);
                float nx = tmp[0], ny = tmp[1], nz = tmp[2];
                if (ne.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; }
                ReadTyped(s, SrcAddr(pe.Stream, i, pe.Offset), pe.Type, tmp);
                WriteXYZ(outStreams[pe.Stream], i * outStrides[pe.Stream] + pe.Offset, pe.Type,
                    tmp[0] + nx * push, tmp[1] + ny * push, tmp[2] + nz * push);
            }

            // Force vertex colour white so the gear shader's emissive isn't gated off.
            if (col is { } ce)
            {
                int o = i * outStrides[ce.Stream] + ce.Offset;
                outStreams[ce.Stream][o] = outStreams[ce.Stream][o + 1]
                    = outStreams[ce.Stream][o + 2] = outStreams[ce.Stream][o + 3] = 0xFF;
            }

            // Decode uv0 (raw) — normalized and written below, once the mesh's UV cell is known.
            if (uv0 is { } ue) { ReadTyped(s, SrcAddr(ue.Stream, i, ue.Offset), ue.Type, tmp); uvs[i] = (tmp[0], tmp[1]); }
        }

        // Normalize the mesh's UV into the [0,1] tile and force uv1 = uv0. The overlay is a single [0,1]
        // image, but a body UV can live in another cell (vanilla U∈[1,2], bibo V∈[-1,0]); shift the WHOLE
        // mesh by the integer floor of its minimum UV. A per-mesh (not per-vertex) shift keeps islands
        // together so nothing tears, and brings an island that sits WITHIN one integer cell fully onto the
        // tile — a body part is laid out that way. (An island straddling a cell boundary would keep the
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
                // second normalization follows. A vertex the maps can't place keeps its original UV —
                // pulling it to some far-off "nearest" would drag its triangles across the texture.
                if (uvConv != null)
                {
                    uvsPreConv![i] = (u, v);
                    var moved = uvConv(u, v, sides != null && i < sides.Length ? sides[i] : 0);
                    if (moved is { } mv) { u = mv.U; v = mv.V; }
                    else uvUnmapped++;
                }
                uvs[i] = (u, v);
                WriteUV2(outStreams[u0e.Stream], i * outStrides[u0e.Stream] + u0e.Offset, uv0Half, u, v);
                WriteUv1(uv1Plan, u0e, outStreams, outStrides, i, u, v);   // the SHIFTED value, unlike content
            }
        }

        // Declaration: copy the source mesh's block verbatim, splicing in a uv1 element only when we
        // appended one (the .zw / existing-uidx1 cases already declare their uv1).
        declBlock = new byte[DeclSize];
        Array.Copy(s, srcDeclOff, declBlock, 0, DeclSize);
        SpliceUv1Decl(declBlock, uv1Plan);
        return uvUnmapped;
    }

    /// <summary>
    /// Re-fit a converted mesh's tangent frame to its NEW UVs.
    /// <para/>
    /// A tangent basis is DEFINED by the UV parameterization, and <see cref="BuildVerbatim"/> copies every
    /// vertex stream byte-for-byte before overwriting position/colour/UV — so a mesh whose UVs were moved
    /// into another body's layout is left describing the layout it came from. The shell samples its normal
    /// map in tangent space (relief in R/G, the coverage gate in blue), so a stale frame lights the fabric
    /// from the wrong direction, and a MIRRORED island (bibo's foot against gen3's, say) flips handedness
    /// and reads as an inverted normal map on that part alone while the rest of the shell looks right.
    /// <para/>
    /// Rather than author a frame from scratch — which would mean committing to the game's sign and slot
    /// conventions, and getting either backwards inverts every converted part — this READS the convention
    /// off the source and reapplies it. Per vertex it derives the surface tangent/binormal twice, from the
    /// old UVs and from the new, takes the sign the stored vector had against the OLD direction, and writes
    /// that same sign against the NEW one. Handedness (the .w lane) flips only when the two frames' own
    /// handedness disagrees. Whatever usage 5 and 6 mean to the shader, the geometry is what changed and
    /// the geometry is all this touches.
    /// <para/>
    /// Only triangles that survived the coverage trim contribute, so vertices no longer referenced get no
    /// accumulation and are left alone — the compaction below drops them anyway. Returns true when at
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
                case UseTangent2: tanEl ??= el; break;   // usage 5 — tracks dP/du
                case UseTangent1: binEl ??= el; break;   // usage 6 — tracks dP/dv (the one bodies carry)
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

            // (N x B) . T — positive or negative tells the two frames apart; disagreement means the new
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
    /// sign it had relative to the old direction — that sign IS the source's convention, whatever it is.
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
            case 8:            // Ubyte4n — what character models actually use for tangent/binormal
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
                if (mask[wy * w + wx] > 0) return true;
            }
        }
        return false;
    }

    private static ushort Half(float f) => BitConverter.HalfToUInt16Bits((Half)f);
    private static void W16(byte[] a, int o, ushort v) => BitConverter.GetBytes(v).CopyTo(a, o);
    private static void W32(byte[] a, int o, uint v) => BitConverter.GetBytes(v).CopyTo(a, o);
}
