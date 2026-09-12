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

    /// <summary>
    /// How far this layer's cloth relaxes across the cleavage instead of following the body into it
    /// (0 = off, which is the default and every existing shell's behaviour; 1 = a flat span).
    /// <para/>
    /// Unlike the toe cap there is no map: the region is the bust bones' influence intersected with this
    /// layer's own <see cref="Coverage"/>, so nothing has to be painted and nothing can fall out of sync
    /// with the art. See <c>BustBridgeSolve</c>.
    /// </summary>
    public float BustBridgeStrength { get; init; }

    /// <summary>
    /// How far this layer's cloth is smoothed over the nipple (0 = off, which is the default and every
    /// existing shell's behaviour; 1 = full smoothing).
    /// <para/>
    /// Shares the region, the axis and the solve with <see cref="BustBridgeStrength"/> and is independent
    /// of it: a mod may want either. See <c>NippleSmoothTarget</c>.
    /// </summary>
    public float NippleSmoothStrength { get; init; }

    /// <summary>
    /// How far this layer's cloth spans the gluteal cleft instead of following the body into it
    /// (0 = off, which is the default and every existing shell's behaviour; 1 = a flat span).
    /// <para/>
    /// The same construction as <see cref="BustBridgeStrength"/> on the same shape — two lobes with a
    /// recessed midline — and it runs as its own solve rather than sharing the bust's, because the two
    /// sit on opposite sides of the body with their own axes. Measured on a real body the cleft dishes
    /// 39mm at its deepest against the cleavage's 11mm, so it is the larger of the two features.
    /// <para/>
    /// Seeded differently, and that difference is the whole reason this is not simply the bust pass with
    /// other bone names: the breast bones are a symmetric PAIR whose influence misses the sternum, while
    /// the hip is ONE midline bone that covers the cheeks, the cleft and the crotch together. See
    /// <c>HipBone</c> and <c>Facing</c>.
    /// </summary>
    public float CleftBridgeStrength { get; init; }

    /// <summary>
    /// How far this layer's shell flattens the crotch fold (0 = off, the default; 1 = flat onto the
    /// surface either side).
    /// <para/>
    /// Carried here only as the DECLARATION, like <see cref="NippleSmoothStrength"/>: the flattening is a
    /// body pass, and this is what the coverage union it runs under is built from.
    /// </summary>
    public float FoldSmoothStrength { get; init; }

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

    /// <param name="CapDeclined">
    /// Set when a toe cap was asked for but no binding described this body well enough to place it, so
    /// none was emitted. Worth telling the wearer about: the toes silently lose their cap, and the only
    /// alternative — emitting it anyway — tears it into shards.
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
    /// even when both are nominally the same UV space. The binding's job is the other axis — heels and
    /// any other foot MODEL swap for the body the cap belongs to.
    /// </summary>
    /// <param name="Bind">Null only for a cap shipped without one, which can then be used as authored.</param>
    /// <param name="Name">For the log, so it is clear which cap a shell ended up with.</param>
    public readonly record struct AuthoredCapSet(byte[] Cap, byte[]? Bind, string Name);

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
    /// a coordinate onto one is wrong — but they are also weighted to their own bones, and a cap that
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
            // nonSkin inverts the filter: exactly the meshes the skin filter throws away — toenails,
            // fingernails, undies, piercings. Wanted for the CAP BINDING and nothing else. A body's
            // skin mesh has a HOLE where each toenail sits (Rue does; Neolithe carries skin under its
            // nails), so a cap vertex over a nail has no skin beneath it and binds to the rim of that
            // hole instead — the offset there came out at 0.0085 against about 0.001 everywhere else,
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

    /// <summary>
    /// How many bone influences a blend-weight or blend-index element of this type holds.
    /// <para/>
    /// Dawntrail added an EIGHT-influence format (type 17, eight bytes) alongside the old four. Treating
    /// one as the other is silent and destructive: writing four weights into an eight-influence vertex
    /// leaves the last four holding whatever the source had, so the total comes to 1.7x and the vertex is
    /// dragged toward a bone nothing intended. In game that shows as triangles stretched away or gone,
    /// while a modelling package — reading the first four and the bind pose — shows it perfectly correct.
    /// </summary>
    private static int BlendCount(byte type) => type == 17 ? 8 : 4;

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
    /// <summary>
    /// <see cref="Parse"/>, memoised on the byte array it was handed.
    /// <para/>
    /// A cap model is parsed by the placement, again by the neighbour fill, again by the shape fit and
    /// again by the round-trip score - four walks of the same 80-135 KB buffer, and selection does that
    /// for every candidate cap on every composite. The buffers are read-only and long-lived (the caps are
    /// loaded once at startup), so the parse is worth keeping.
    /// <para/>
    /// Keyed by REFERENCE and held weakly: two callers with the same array share a parse, a caller with
    /// its own copy gets its own, and a model that goes away takes its parse with it rather than pinning
    /// it for the process lifetime. The factory may run twice under a race; both produce the same thing
    /// and only one is kept.
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
        IReadOnlyList<AuthoredCapSet>? authoredCaps = null,
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
            layers, baseModel, out stats, diag, authoredCaps);

    /// <summary>
    /// Build the merged shell from fully-described sources. Every layer is applied to every source, so all
    /// sources here must share one UV space and one race space — that is what makes them one surface.
    /// </summary>
    public static byte[] Build(IReadOnlyList<SourceSpec> sources, IReadOnlyList<SecondSkinLayer> layers,
        byte[]? baseModel, out Stats stats, Action<string>? diag = null,
        IReadOnlyList<AuthoredCapSet>? authoredCaps = null)
    {
        if (layers.Count == 0) throw new ArgumentException("need at least one layer", nameof(layers));
        // Sources are the character geometry a SHELL is cut from, so a build made entirely of content
        // layers — an imported pack that brings its own meshes — legitimately has none. Anything else
        // still does: a shell layer with no source would emit nothing at all.
        if (sources.Count == 0 && layers.Any(l => l.Geometry.Count == 0))
            throw new ArgumentException("need at least one source model", nameof(sources));

        var parsed = sources.Select(s => Parse(s.Model)).ToList();

        // The raw model bytes, for the cap passes that read geometry straight out of a .mdl rather than
        // going through the parsed sources — the binding probe, the UV projection and the skin-triangle
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

        // The hand-modelled toe box, bundled with the plugin. It replaces the generated cap: a shell is
        // a displaced copy of the body, so it sleeves each toe unless something covers the toe box, and
        // generating that something is a topology problem that kept producing pinched, lumpy geometry.
        // Merged like any other source — its own bone table joins the union by name and its vertices
        // keep their blend indices, so it skins without anything being reinterpreted.
        // ONE CAP PER BODY, each modelled against its own toes, each with a binding measured against the
        // body it was modelled on. Fitting a cap authored for one body onto another was tried at length
        // and abandoned: placing it is measurable and works, but reconciling its rim with a cut made from
        // a map painted in a DIFFERENT body's parameterisation is not something the numbers can settle —
        // it took four changes, two of them reverted, and still looked wrong. A modeller closes that loop
        // by looking at it, in minutes.
        //
        // The binding still earns its place, on the axis authoring cannot cover: heels are not another
        // body, they are another foot MODEL for the same one, and a cap per body per footwear would be
        // combinatorial. The binding collapses that axis, where it measures cleanly (0-4% unplaced).
        //
        // Nothing can ask which body is equipped — Proteus knows only three UV buckets and Neolithe, Rue
        // and Bibo+ are all "bibo" — so the caps identify themselves: whichever binding places the most
        // vertices belongs to this foot, and its cap is the one to graft. Scored on a sample first,
        // because a full placement is every cap vertex against every skin triangle.
        Source? capSrc = null;
        byte[]? capBytes = null;
        Dictionary<int, CapPlacement>? capPlaced = null;
        string? capDeclined = null, capUsed = null;
        // How far the chosen cap already stands off this body, measured at selection. The push below is
        // driven from it, so a cap authored thick and a cap authored thin both land at the same height.
        float capStandoff = 0f;
        // A cap is only worth choosing if something is going to graft it. `authoredCaps` says only that the
        // PLUGIN SHIPS caps, which it always does — so on its own this ran the five-candidate selection and
        // then a full placement on every build, for every host, whether or not any layer had asked for a
        // cap. Measured at 2545 ms of a 4601 ms composite on a look with no cap selected anywhere, and the
        // result was used by nothing: the per-layer test below (`wantCap`) is the first thing that asks.
        //
        // Same predicate as that test, hoisted. Keeping the two in step matters — a layer that wants a cap
        // and finds none chosen renders sleeved toes, which is the fault this whole path exists to avoid.
        bool anyLayerWantsCap = layers.Any(l => l.ToeCap != null && l.ToeCapStrength > 0f);

        // ── the bust bridge is solved ONCE for the host, not once per layer ──────────────────────────
        //
        // STACK ORDER IS THE REASON. Shells sit a fifth of a millimetre apart and render in layer order,
        // and a per-layer solve breaks that: each layer's region is gated on its OWN coverage, so two mods
        // that both span — a bodysuit and a bralette over it — reach different heights across the same
        // cleavage, and wherever the lower one lifts further it comes through the upper one. Seen exactly
        // that way in game: the bralette's lace punching through the bodysuit it sits under.
        //
        // One displacement for every spanning layer keeps them exactly LayerSeparation apart, because the
        // push that separates them is applied along the normal AFTER this and is untouched by it. The
        // coverage handed to the solve is the UNION of the spanning layers', so the region is "where any
        // of this host's spanning cloth is" — a layer whose own cloth stops earlier still moves with the
        // rest there, and the triangles it does not draw are trimmed away regardless.
        //
        // It is also the cheap way round: the solve costs tens of milliseconds per mesh and was being paid
        // once per layer per mesh for an answer that could not legitimately differ.
        // Both chest passes come through here — the span and the nipple smooth. Separate settings, one
        // solve: same region, same axis, and the same reason for sharing it across a host's layers.
        // Only the SPAN is a shell pass. The nipple relax belongs to the body — SmoothBodyNipples runs it
        // there, and the shell inherits it by being cut from the smoothed body afterwards. Running it here
        // as well is the defect that put the garment inside the skin: two relaxes over two different meshes
        // cannot agree, and measured on a real capture they disagreed by 2-3.3mm. `NippleSmoothStrength`
        // survives on the layer as the DECLARATION — the body pass builds its coverage union from it.
        // ONE DEFINITION PER FEATURE, not one shared by both. They are separate passes over separate parts
        // of the body, and each one's coverage gate has to be the union of the layers that asked for THAT
        // pass. Sharing a single union let a cleft layer with no coverage map (which legitimately means
        // "paints everything") widen the BUST's gate to the whole bust region — moving cloth under
        // garments whose mods never ticked the bust bridge at all.
        const float smoothStrength = 0f;
        var bustLayers  = layers.Where(l => l.BustBridgeStrength > 0f).ToList();
        var cleftLayers = layers.Where(l => l.CleftBridgeStrength > 0f).ToList();
        float bridgeStrength = bustLayers.Count  == 0 ? 0f : bustLayers.Max(l => l.BustBridgeStrength);
        float cleftStrength  = cleftLayers.Count == 0 ? 0f : cleftLayers.Max(l => l.CleftBridgeStrength);

        SecondSkinLayer? MakeDef(List<SecondSkinLayer> ls, float bust, float cleft, string what)
        {
            if (ls.Count == 0) return null;
            // A spanning layer with no coverage map paints everything, so the union is everything and the
            // gate falls away — which is what a null Coverage already means downstream.
            var sized = ls.Where(l => l.Coverage != null && l.CoverageWidth > 0 && l.CoverageHeight > 0)
                          .ToList();
            byte[]? union = null;
            int uw = 0, uh = 0;
            if (sized.Count == ls.Count && sized.Count > 0)
            {
                uw = sized[0].CoverageWidth; uh = sized[0].CoverageHeight;
                if (sized.All(l => l.CoverageWidth == uw && l.CoverageHeight == uh
                                && l.Coverage!.Length >= uw * uh))
                {
                    union = (byte[])sized[0].Coverage!.Clone();
                    for (int k = 1; k < sized.Count; k++)
                    {
                        var c = sized[k].Coverage!;
                        for (int p = 0; p < union.Length; p++) if (c[p] > union[p]) union[p] = c[p];
                    }
                }
            }
            diag?.Invoke($"bust bridge: one {what} solve for {ls.Count} layer(s) — span {bust:0.##}, "
                       + $"cleft {cleft:0.##}, smooth {smoothStrength:0.##}, coverage "
                       + (union == null ? "union unavailable — spanning every seeded vertex"
                                        : $"{uw}x{uh} union"));
            return new SecondSkinLayer
            {
                MaterialName = "/bridge.mtrl",       // never emitted; this carries coverage and strength only
                Coverage = union,
                CoverageWidth = union == null ? 0 : uw,
                CoverageHeight = union == null ? 0 : uh,
                BustBridgeStrength = bust,
                NippleSmoothStrength = smoothStrength,
                CleftBridgeStrength = cleft,
            };
        }

        var bridgeDef = MakeDef(bustLayers, bridgeStrength, 0f, "chest");
        var cleftDef  = MakeDef(cleftLayers, 0f, cleftStrength, "cleft");
        // Per SOURCE MESH AND PER FEATURE SET, so every layer of this host that asked for the same thing
        // reuses the one answer — and a layer that asked for something else does not inherit it.
        //
        // Keyed on what the layer wants, not on the mesh alone, because the settings are per MOD and two
        // mods on one host legitimately differ. Under a mesh-only key the first layer to arrive decided
        // for all of them: a bust-only layer cached a bust-only plan and the cleft layer behind it was
        // then displaced by a bust bridge it never asked for and got no cleft at all. Worse, a
        // nipple-smoothing layer passes the gate, solves with both strengths at zero, gets null back and
        // caches THAT — and a cached null reads as a hit, so every later layer on that mesh did nothing.
        var bridgePlans = new Dictionary<(Source, int, bool, bool), BustBridgePlan?>();
        var bridgeWeights = new Dictionary<(Source, int), float[]?>();
        var cleftWeightCache = new Dictionary<(Source, int), float[]?>();
        if (authoredCaps is { Count: > 0 } && anyLayerWantsCap)
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
            float bestRate = float.MaxValue, bestCover = -1f, bestSpread = float.MaxValue;
            float bestTrip = float.MaxValue;
            List<SkinTri>? fitSurface = null;
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
                float spread = CapStandoffSpread(probe, fitSurface ??= BindSurface(sourceModels),
                                                 out float standoff);
                float trip = CapRoundTrip(probe, cand.Cap);
                if (authoredCaps.Count > 1)
                    diag?.Invoke($"authored cap: '{cand.Name}' places all but {rate * 100:F0}% on this body, "
                               + $"this body has {cover * 100:F0}% of the {want.Count} bone(s) it was bound "
                               + $"to, sits a median {standoff:F5} off the skin within {spread:F5}, and "
                               + $"reproduces itself here to {trip:F5}");

                // Bone coverage decides; the placement score only separates caps the body can equally
                // carry. A cap missing bones is not a worse fit, it is the wrong body.
                //
                // AND WHEN THOSE TWO TIE, HOW EVENLY IT SITS. Two bodies can share a skeleton entirely -
                // Bibo+ and Neolithe feet both weight to j_asi_d and j_asi_e and nothing else - so bone
                // coverage cannot tell their caps apart, and a cap made for either places every vertex
                // on the other. Both then scored 100% and 0%, the comparison fell through to a strict
                // "better than", and the winner was whichever the directory listing named first:
                // toecap.bibo.mdl sorts before toecap.mdl, so a Neolithe foot got the Bibo+ cap.
                //
                // A cap on the body it was modelled for hugs it at the standoff its author gave it. On a
                // foot shaped differently the same binding lands some of it buried and some of it
                // floating, and that spread is the thing to compare.
                // ...AND THEN, WHICH BODY WAS IT BAKED AGAINST? A binding reconstructs its own cap
                // EXACTLY on the body it was measured on - that is what the version-2 residual is for -
                // and cannot on any other, because the residual is a correction measured somewhere else.
                // So the round trip is not a quality score, it is an identity test, and it answers the
                // question the other three cannot.
                //
                // It is the discriminator this needed all along. Bone coverage cannot separate two bodies
                // that share a skeleton: YAB and Rue are both IVCS, so on a YAB foot both caps cover
                // 100% of their bones, both place 100% of their vertices, and the fit put Rue ahead by
                // 0.0002 - handing a YAB body the Rue cap. The round trip separates them by two orders
                // of magnitude. Bibo and Neolithe share j_asi_* the same way, which is what made the
                // earlier heeled-foot selection so hard to read.
                bool better;
                if (cover > bestCover + CapBoneCoverTie) better = true;
                else if (cover < bestCover - CapBoneCoverTie) better = false;
                else if (rate < bestRate - CapPlaceRateTie) better = true;
                else if (rate > bestRate + CapPlaceRateTie) better = false;
                else if (trip < bestTrip * CapRoundTripTie) better = true;
                else if (trip > bestTrip / CapRoundTripTie) better = false;
                else better = spread < bestSpread;
                if (better)
                {
                    bestCover = cover; bestRate = rate; bestSpread = spread; bestTrip = trip;
                    capStandoff = standoff; best = cand;
                }
            }

            if (best is { } chosen && bestRate <= CapBindMaxUnplaced)
            {
                capBytes = chosen.Cap;
                try { capSrc = ParseCached(chosen.Cap); }
                catch (Exception ex) { diag?.Invoke($"authored cap failed to parse, ignoring: {ex.Message}"); }
                if (capSrc != null && chosen.Bind != null)
                {
                    capUsed = $"{CapBodyName(chosen.Name)} ({(1f - bestRate) * 100:F0}% placed)";
                    diag?.Invoke($"authored cap: using '{chosen.Name}'");
                    var placed = TryPlaceCapFromBind(chosen.Bind, sourceModels, diag, chosen.Cap);
                    // NOT lifted clear of the toenails. Tried that: the cap measured as intersecting the
                    // nail mesh by up to 4.7 mm, so a pass pushed the buried vertices out along their
                    // normals. It was chasing an artefact — the cap hugs the foot exactly in a modelling
                    // package, and a signed distance taken against a thin two-sided nail shell reads
                    // "buried" for vertices that are merely beside it. Whatever is wrong in game is not
                    // the cap sitting under the nails.
                    if (placed is { Count: > 0 }) capPlaced = placed.ToDictionary(p => p.Mesh);
                }
            }
            else if (best != null)
            {
                // Emitting it anyway is what produced the shards; no cap is the better answer. Note this
                // must also call off the CUT — the toe-cap map carves the toe box out of the shell for
                // the cap to fill, so declining the cap without declining the cut leaves a hole. capSrc
                // stays non-null so BuildVerbatim does not fall back to GENERATING one; that path is long
                // dead and throws.
                capBytes = best.Value.Cap;
                try { capSrc = ParseCached(best.Value.Cap); } catch { /* declined anyway */ }
                capDeclined = bestRate is > 0f and < float.MaxValue
                    ? $"{bestRate * 100:F0}% of the toe cap could not be placed on this body"
                    : "no toe cap has been measured against this body";
                diag?.Invoke($"authored cap: DECLINED — {capDeclined}; the toes keep the plain shell");
            }
        }

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
        // merged table or its vertices collapse onto the root, and the authored toe cap is one more source
        // of them: it arrives weighted to its own four and leaves weighted to the body's.
        // Materialised, not lazy: this is walked three times below (bones, attributes, the overflow count)
        // and re-running the concat each time is work for nothing.
        List<Source> boneSources =
            [.. baseSrc != null ? new[] { baseSrc }.Concat(parsed) : parsed, .. geomSrcs,
             .. capSrc != null ? new[] { capSrc } : []];
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
        // — the weld, both splits — is matching positions exactly. So the value is kept here, keyed by the
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

        // EVERYTHING SOLID, for the clearance pass alone. bodySkin is the skin filter's idea of the body
        // and the right one for reading a UV from, but it is not what the shell has to clear. A body may
        // carry its toenails on their own mesh under their own material, and a shoe routinely brings them
        // along: on this heeled foot the skin has ten holes where the nails should be and no nail
        // geometry at all, so the nail pokes through a shell that was never told it existed. Nothing
        // reads a coordinate from this - only distances.
        List<SkinTri>? bodySolid = null;

        // The cap's projection depends only on the cap and the bodies, never on the layer wearing it, and
        // it is the most expensive thing in the build — every cap vertex against every skin triangle.
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
                      bool mirrorUv1 = false, IReadOnlySet<string>? hiddenAttrs = null,
                      bool clearAttrs = false, IReadOnlyList<(float Lo, float Hi)>? otherBands = null,
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
                CopyVerbatim(s, src.Vb, 0x44 + m * DeclSize, vc, decl, vbo, bs, mirrorUv1,
                    out outStreams, out outStrides, out declBlock);
                // The host accessory has no coordinate to trim by; the grafted cap does, and it MUST be
                // trimmed the same way the shell is. Without this every capped layer emitted the entire
                // cap whatever that layer covered — so an overlay reaching a sliver of the foot still put
                // a full toe box on top of everything, with its rim attached to nothing.
                uv = capUv is { } uvPlan ? uvPlan.Uv : [];

                // A supplied UV still has to be written, and this path copies bytes rather than going
                // through BuildVerbatim, so it does not happen by itself. The authored toe cap arrives
                // with every vertex at (0,1) — one corner texel of the overlay, transparent — and looks
                // perfect in a modelling package while rendering as nothing at all in game.
                if (capUv is { } tooBig && tooBig.SourceOf.Length > ushort.MaxValue)
                    diag?.Invoke($"authored cap: {tooBig.SourceOf.Length} vertices after the seam split "
                               + "exceeds a 16-bit index — UVs NOT applied, the cap will render blank");
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
                    // Coincident positions with different weights is a bind-pose weld — the two edges sit
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
                        // the cap's four would do that too, and worse — it would coarsen the cap. The
                        // game takes eight, so the cap is widened to match rather than the shell narrowed.
                        //
                        // Only stream 0 is touched, and only when it holds exactly position, weights and
                        // indices — which is what a skinned mesh's first stream is. Anything else and the
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
                                // As many influences as THIS element declares — see BlendCount. Anything
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
                    // at zero the shell's cut edge stands proud of the cap by the whole offset — 0.001,
                    // better than a quarter of the mesh's own edge — as a lip running the length of the
                    // join with bare skin showing in it.
                    //
                    // SHARED NORMAL, but NOT a shared position any more. The cap's rim used to be pulled
                    // onto the shell's here, to make each polyline pass through the other's vertices —
                    // the shell's edge otherwise ran as a chord past each cap rim vertex and the lens
                    // between chord and boundary was an open gap, up to 0.00047. The shell is now split at
                    // those vertices instead, which closes the same lens without moving anything: an
                    // authoritative rim cannot be allowed to move, or the coordinates the shell was welded
                    // and split against stop being the ones the cap ships with. The normal is still
                    // averaged against the shell's PRE-average value, so both sides arrive at the same
                    // answer and the join stops shading as a crease — which is what makes it read as a
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
                // BEFORE coverage trimming — read it only when a layer actually asks for a cap.
                bool wantCap = cov is { ToeCap: not null } && cov.ToeCapStrength > 0f;
                // The bust bridge relaxes across the same topology and wants the same list. It also needs
                // to know which vertices are on the bust, and that is settled from the mesh's BONE TABLE
                // before any vertex is read — every mesh of the body but the torso names neither bust bone
                // and drops out for the cost of one walk over a handful of names.
                //
                // Asked of the HOST's shared definition, not this layer's, so every spanning layer gets the
                // same answer and the stack keeps its order. Memoised per mesh; only the first layer to
                // reach a mesh pays for it.
                // What THIS layer asked for. The nipple smooth is not here: it is a body-side pass now, so
                // a layer that only smooths nipples must not drag the chest solve in behind it — that is
                // what produced the null plan that poisoned the mesh for everyone after it.
                bool wantBust  = bridgeDef != null && cov is { } cl  && cl.BustBridgeStrength  > 0f;
                bool wantCleft = cleftDef  != null && cov is { } cl2 && cl2.CleftBridgeStrength > 0f;

                float[]? bustWeights = null, cleftWeights = null;
                if (wantBust)
                {
                    if (!bridgeWeights.TryGetValue((src, m), out bustWeights))
                        bridgeWeights[(src, m)] = bustWeights =
                            MeshRegionWeights(src, m, vc, decl, vbo, bs, [BustBoneL, BustBoneR]);
                }
                // The cleft is its own seed on its own mesh: the bust rides the torso and this rides the
                // legs part, so on a real body the two never even reach the same call.
                if (wantCleft)
                {
                    if (!cleftWeightCache.TryGetValue((src, m), out cleftWeights))
                        cleftWeightCache[(src, m)] = cleftWeights =
                            MeshRegionWeights(src, m, vc, decl, vbo, bs, [HipBone], [ThighBoneL, ThighBoneR]);
                }
                var capTris = wantCap || bustWeights != null || cleftWeights != null
                    ? MeshTriangles(src, srcSubIdx, srcSubCount)
                    : null;

                // The solve itself, deferred until BuildVerbatim has normalised the mesh's UVs onto the
                // tile the coverage map is indexed over — it cannot run before that and must not run twice.
                Func<Vec3[], Vec3[], ushort[], (float U, float V)[], BustBridgePlan?>? bridge = null;
                if (bustWeights != null || cleftWeights != null)
                {
                    // The definitions travel with the weights they belong to: the weights are only
                    // non-null when their own definition was, so capturing the pair keeps that provable
                    // inside the lambda instead of asserted.
                    var bw = bustWeights;   var bDef = bridgeDef;
                    var cw = cleftWeights;  var cDef = cleftDef;
                    var key = (src, m, bw != null, cw != null);
                    bridge = (bPos, bNrm, bTris, bUv) =>
                    {
                        if (bridgePlans.TryGetValue(key, out var cached)) return cached;

                        // Each pass is gated by the union of the layers that asked for IT, so the two
                        // coverages are resolved separately even though one mesh carries both.
                        var plan = bw == null || bDef == null ? null
                            : BustBridgeSolve(bPos, bNrm, bTris, bw, bridgeStrength, diag,
                                              CoveredVertices(bUv, bDef, bPos.Length), smoothStrength);

                        if (cw != null && cDef != null)
                        {
                            // Facing is gated on a COPY: the weights are memoised per mesh and shared by
                            // every layer of the host, so gating in place would have the second layer read
                            // a seed the first had already cut down.
                            var backOnly = (float[])cw.Clone();
                            GateToBackFacing(backOnly, bNrm);
                            // The seed doubles as the RAMP. BustRegionWeights reads it as a boolean and
                            // builds its own one-ring fade, which is right against a coverage edge — cloth
                            // that must not move at all — and wrong at this region's own two boundaries,
                            // which are lines through open skin: the hip bone's taper at the waist, and
                            // the turn of the flank. Left to the one-ring fade the span died over a single
                            // edge at the waist and drew a seam across the small of the back. These weights
                            // already describe both boundaries smoothly, so hand them over rather than
                            // inventing a feather.
                            plan = MergePlans(plan,
                                BustBridgeSolve(bPos, bNrm, bTris, backOnly, cleftStrength, diag,
                                                CoveredVertices(bUv, cDef, bPos.Length),
                                                smoothStrength: 0f, fillGap: false,
                                                ramp: backOnly));
                        }

                        bridgePlans[key] = plan;
                        return plan;
                    };
                }
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
                    out outStreams, out outStrides, out declBlock, out uv, out uvPre, src.UvConv,
                    out capSrcPos, out capOutPos, out capPlan, sides, cov, capTris, diag,
                    buildCapGeometry: capSrc == null, bridge: bridge);
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
                diag?.Invoke($"toe cap: mesh {m} — plan {(capPlan == null ? "NULL" : "present")}, "
                           + $"the cut removed {cutTris} triangle(s)");

            // The rebuilt cap joins the submesh that lost the most geometry to the cut. Its vertices are
            // all reused originals from that region, so they already skin through that submesh's bone
            // window — which is the one thing a new triangle here must respect.
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
            // landed part-way along rim EDGES, not on rim vertices — 2 of 134 coincided. The cap has no
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
                           + $"shell landing(s) — {onVert} snapped onto, {offEdge} off the boundary");
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
            // UV-converted SHELL mesh — the cap path is `preserve`, which leaves uvPre null.
            if (uvPre != null && uvPre.Length >= vc && uv.Length >= vc
                && RetangentMesh(outStreams, outStrides, decl, vc, uvPre, uv, keptPerSub))
                uvRetangented++;

            // ── weld the cut lip onto the cap's rim ───────────────────────────────────────────────────
            // The cut is decided in TEXTURE space, from a painted map, so its edge lands wherever the
            // map's texels happen to fall — measured, a median of 0.007 from where the cap's boundary
            // actually is, in a ragged line following the texel grid. The cap then either laps over the
            // shell or leaves bare skin showing, and the join reads as a torn edge either way.
            //
            // Each lip vertex slides onto the NEAREST POINT OF A RIM SEGMENT, not onto the nearest rim
            // vertex. The two loops have different vertex counts (56 on the cap against 107 and 90 on the
            // shell), so vertex-to-vertex pairing would collapse several lip vertices onto one rim vertex
            // and tear the triangles between them. The cap is snapped back onto this rim in the graft —
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
                    // Hoisted clear of BOTH loops below — the rounds and the per-vertex walk. Cleared at
                    // each use, so one buffer behaves identically to a fresh stackalloc per iteration.
                    Span<byte> wb3 = stackalloc byte[8], ib3 = stackalloc byte[8];

                    // TWICE, and the second round is not belt and braces. Dropping a collapsed triangle
                    // EXPOSES vertices that were interior when the boundary was worked out, and those
                    // never got a look — measured, one such vertex sat 0.0075 from the rim, two edge
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

                            // Past the radius the vertex is not on the cap's boundary at all — the far
                            // side of a toe, or another hole entirely — and dragging it in would fold the
                            // mesh. Counted only on the last round, or the first round's near misses get
                            // reported twice.
                            // How far this vertex may be dragged depends on WHY it is on a boundary. The
                            // toe-cap cut deliberately takes out more than the cap covers — 8156 texels
                            // against the cap's 2048 — and the weld pulling the lip forward onto the rim
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
                            // closely were the ones that disagreed most — mean 8.3% against 0.1% for
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

                        // Sliding the lip inevitably flattens a few of the triangles behind it — one
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
                            // still cover ground — a long thin one covers exactly the sliver of skin it
                            // is standing on — and dropping those is what leaves bare patches along the
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
                    // the cap is split at those landings when it is grafted — that direction is closed.
                    // This is the mirror: the cap's rim has far more vertices than the lip does (256
                    // against 155 on Rue), and the shell has none at most of them, so the lip runs as one
                    // long edge past several cap vertices and parts from the cap by whatever the cap bows
                    // off that chord. Splitting the lip at each one costs no movement — the new vertices
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
                                   + $"{rimPts.Count} cap rim vertex/vertices — {onVert2} snapped onto, "
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
                        // The cap's INTERIOR, as vertex indices in the merged mesh. Reused vertices are
                        // shared with the shell at the join and are deliberately left out: the weld set
                        // their normals so the two sides shade as one surface, and that is the one place
                        // the cap's own shading should not win.
                        var capInterior = new List<ushort>();
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
                                    capInterior.Add(nv2);
                                }
                                if (!ok) continue;
                                newTris.Add(map[c0]); newTris.Add(map[c1]); newTris.Add(map[c2]);
                                capTriCount++;
                            }
                        }

                        if (newTris.Count > 0)
                        {
                            // Into the submesh that lost the most to the cut — its bone window already
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
                            diag?.Invoke($"authored cap: grafted INTO the shell mesh — {capTriCount} triangle(s), "
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

                            // THE CAP KEEPS ITS OWN SHADING. Each grafted vertex was given the normal of
                            // the BODY at the point its binding landed on — the surface it was projected
                            // onto, not the surface it is. The positions are the authored cap to within
                            // 0.4 mm at p90, but the normals leaned like the toes underneath: 860 of 1829
                            // vertices off by more than 10 degrees, 339 by more than 30. A smooth surface
                            // shaded that way reads as a wrinkled one, which is exactly how it looked in
                            // game while every measurement of its SHAPE came back clean.
                            //
                            // Recomputed from the merged mesh rather than copied from the authored cap,
                            // because the cap is placed by its binding and on a body it was not modelled
                            // against that placement bends it — an authored normal would then describe a
                            // shape that is no longer there. Taken from the geometry that actually got
                            // emitted, this is right on every body.
                            //
                            // Interior only. A vertex shared with the shell at the join keeps the normal
                            // the weld gave it, so the two sides still shade as one surface; and since a
                            // vertex just inside the rim averages in the shell's faces too, the two meet
                            // without a crease rather than at a hard line.
                            if (gN is { } ne11 && capInterior.Count > 0)
                            {
                                var acc = new Dictionary<ushort, Vec3>();
                                // A plain array: the local function below reads it, and a ref local
                                // cannot be captured.
                                var tp = new float[4];
                                Vec3 PosOf(ushort v)
                                {
                                    ReadTyped(outStreams[pw2.Stream], v * outStrides[pw2.Stream] + pw2.Offset,
                                              pw2.Type, tp);
                                    return new Vec3(tp[0], tp[1], tp[2]);
                                }
                                var want = new HashSet<ushort>(capInterior);
                                foreach (var sub in keptPerSub)
                                    for (int t = 0; t + 2 < sub.Length; t += 3)
                                    {
                                        ushort a5 = sub[t], b5 = sub[t + 1], c5 = sub[t + 2];
                                        if (!want.Contains(a5) && !want.Contains(b5) && !want.Contains(c5))
                                            continue;
                                        Vec3 pa = PosOf(a5), pb = PosOf(b5), pc = PosOf(c5);
                                        // Unnormalised, so a big triangle counts for more than a sliver.
                                        var fn = new Vec3(
                                            (pb.Y - pa.Y) * (pc.Z - pa.Z) - (pb.Z - pa.Z) * (pc.Y - pa.Y),
                                            (pb.Z - pa.Z) * (pc.X - pa.X) - (pb.X - pa.X) * (pc.Z - pa.Z),
                                            (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X));
                                        foreach (var v in new[] { a5, b5, c5 })
                                        {
                                            if (!want.Contains(v)) continue;
                                            var had = acc.GetValueOrDefault(v);
                                            acc[v] = new Vec3(had.X + fn.X, had.Y + fn.Y, had.Z + fn.Z);
                                        }
                                    }

                                int reshaded = 0;
                                float worstTurn = 0f;
                                Span<float> tn = stackalloc float[4];
                                foreach (var (v, sum) in acc)
                                {
                                    var nn = NormalizeOr(sum, default);
                                    if (nn is { X: 0, Y: 0, Z: 0 }) continue;
                                    ReadTyped(outStreams[ne11.Stream], v * outStrides[ne11.Stream] + ne11.Offset,
                                              ne11.Type, tn);
                                    float ox = tn[0], oy = tn[1], oz = tn[2];
                                    if (ne11.Type == 8) { ox = ox * 2 - 1; oy = oy * 2 - 1; oz = oz * 2 - 1; }
                                    var was = NormalizeOr(new Vec3(ox, oy, oz), nn);
                                    // Keep the emitted winding's sense: the merged mesh is assembled from
                                    // several sources and a flipped face here would invert the shading.
                                    if (nn.X * was.X + nn.Y * was.Y + nn.Z * was.Z < 0)
                                        nn = new Vec3(-nn.X, -nn.Y, -nn.Z);
                                    WriteNormal(outStreams[ne11.Stream],
                                                v * outStrides[ne11.Stream] + ne11.Offset, ne11.Type,
                                                nn.X, nn.Y, nn.Z);
                                    worstTurn = MathF.Max(worstTurn,
                                        MathF.Acos(Math.Clamp(nn.X * was.X + nn.Y * was.Y + nn.Z * was.Z,
                                                              -1f, 1f)) * 180f / MathF.PI);
                                    reshaded++;
                                }
                                if (reshaded > 0)
                                    diag?.Invoke($"authored cap: re-shaded {reshaded} interior vertices from "
                                               + $"the cap's own surface instead of the body it was projected "
                                               + $"onto (furthest turn {worstTurn:F1} degrees); the rim keeps "
                                               + "the weld's normals so the join still shades as one surface");
                            }

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
                    // WELD THE CRACKS ALONG THE TRIMS. The coverage cut, the cap's footprint cut and
                    // the join all leave boundary vertices that sit on top of one another without being
                    // the same vertex - measured on a heeled foot, 15 pairs in the toe region alone, the
                    // closest 0.00003 apart. Each is a crack the shell shows daylight through, and in a
                    // modelling package they are what a weld at 0.001 closes.
                    //
                    // Only vertices already on a BOUNDARY, so nothing interior is touched, and only where
                    // both sit on the same spot: this closes cracks, it does not simplify the mesh. Note
                    // the two sides usually disagree on UV - they are opposite sides of a chart boundary
                    // - so the survivor's coordinate wins and the texture shifts a little along that
                    // edge. A few texels of drift on a trim edge beats a hole.
                    {
                        var onEdge = new HashSet<ushort>();
                        {
                            var cnt = new Dictionary<(ushort, ushort), int>();
                            foreach (var sub in keptPerSub)
                                for (int t = 0; t + 2 < sub.Length; t += 3)
                                    for (int k = 0; k < 3; k++)
                                    {
                                        ushort a3 = sub[t + k], b3 = sub[t + (k + 1) % 3];
                                        var key = a3 < b3 ? (a3, b3) : (b3, a3);
                                        cnt[key] = cnt.GetValueOrDefault(key) + 1;
                                    }
                            foreach (var (k, n) in cnt)
                                if (n == 1) { onEdge.Add(k.Item1); onEdge.Add(k.Item2); }
                        }

                        var at = new Dictionary<(int, int, int), ushort>();
                        var into = new ushort[vc];
                        for (ushort i = 0; i < vc; i++) into[i] = i;
                        int welds = 0, snapped = 0;
                        if (pEl2 is { } pw3)
                        {
                            Span<float> tw = stackalloc float[4];
                            // A plain array, not a stackalloc: the local function below reads it
                            // and a ref local cannot be captured.
                            var tuv = new float[4];
                            // Quantised at the weld tolerance, so two points inside it share a bucket.
                            (int, int, int) Bucket(float x, float y, float z)
                                => ((int)MathF.Round(x / CrackWeldTolerance),
                                    (int)MathF.Round(y / CrackWeldTolerance),
                                    (int)MathF.Round(z / CrackWeldTolerance));
                            // ...AND ONLY IF THE TWO SAMPLE THE SAME PLACE. Two vertices can share a
                            // position and still belong to opposite sides of a UV chart, and welding
                            // those merges one chart's coordinate into the other: every face on the
                            // losing side then reaches across the atlas, sampling a streak of texture
                            // rather than a patch of it, which draws a bright line over the surface.
                            // Measured on a foot - welding without this test left 1324 of 12681 faces
                            // stretched past ten times the median, the worst at 117 times; with it, 18.
                            var uvAt = new Dictionary<ushort, (float U, float V)>();
                            (float, float) UvOf(ushort i)
                            {
                                if (uvAt.TryGetValue(i, out var got)) return got;
                                var r = (0f, 0f);
                                if (uEl0 is { } ue7)
                                {
                                    ReadTyped(outStreams[ue7.Stream], i * outStrides[ue7.Stream] + ue7.Offset,
                                              ue7.Type, tuv);
                                    r = (tuv[0], tuv[1]);
                                }
                                uvAt[i] = r;
                                return r;
                            }
                            foreach (var i in onEdge)
                            {
                                if (!used[i]) continue;
                                ReadTyped(outStreams[pw3.Stream], i * outStrides[pw3.Stream] + pw3.Offset,
                                          pw3.Type, tw);
                                var b4 = Bucket(tw[0], tw[1], tw[2]);
                                // THE 27 BUCKETS AROUND IT, not just its own. Two points a tenth of a
                                // millimetre apart still land either side of a bucket boundary, and
                                // looking only in one bucket left 31 cracks open at a tolerance sixteen
                                // times their width.
                                ushort keep = i;
                                bool found2 = false;
                                for (int dx = -1; dx <= 1 && !found2; dx++)
                                for (int dy = -1; dy <= 1 && !found2; dy++)
                                for (int dz = -1; dz <= 1 && !found2; dz++)
                                    if (at.TryGetValue((b4.Item1 + dx, b4.Item2 + dy, b4.Item3 + dz),
                                                       out var cand2) && cand2 != i)
                                    { keep = cand2; found2 = true; }
                                if (found2)
                                {
                                    var (u1, v1) = UvOf(i);
                                    var (u2, v2) = UvOf(keep);
                                    float du = u1 - u2, dv = v1 - v2;
                                    if (du * du + dv * dv <= CrackWeldUvTolerance * CrackWeldUvTolerance)
                                    { into[i] = keep; welds++; continue; }

                                    // DIFFERENT CHARTS: SNAP, DO NOT MERGE. Merging would make one
                                    // coordinate serve both sides and drag every face on the losing side
                                    // across the atlas - measured, 1324 of 12681 faces stretched past ten
                                    // times the median, drawing a bright streak along the toes. But
                                    // merging was never what closed the crack: two vertices at exactly
                                    // the same POSITION leave no gap to see, whether or not the mesh
                                    // considers them one vertex. So move this one onto its partner and
                                    // leave it otherwise alone. The surface closes, both sides keep the
                                    // coordinate they sample, and the seam stays where the author put it.
                                    ReadTyped(outStreams[pw3.Stream],
                                              keep * outStrides[pw3.Stream] + pw3.Offset, pw3.Type, tw);
                                    WriteXYZ(outStreams[pw3.Stream],
                                             i * outStrides[pw3.Stream] + pw3.Offset, pw3.Type,
                                             tw[0], tw[1], tw[2]);
                                    snapped++;
                                }
                                else at[b4] = i;
                            }
                        }

                        if (snapped > 0)
                            diag?.Invoke($"authored cap: closed {snapped} crack(s) by moving one side onto "
                                       + "the other without merging them, so each keeps the part of the "
                                       + "atlas it samples");
                        if (welds > 0)
                        {
                            int lost = 0;
                            for (int su = 0; su < keptPerSub.Count; su++)
                            {
                                var sub = keptPerSub[su];
                                var keepT = new List<ushort>(sub.Length);
                                for (int t = 0; t + 2 < sub.Length; t += 3)
                                {
                                    ushort a4 = into[sub[t]], b5 = into[sub[t + 1]], c4 = into[sub[t + 2]];
                                    // A triangle whose corners weld together has no area left.
                                    if (a4 == b5 || b5 == c4 || c4 == a4) { lost++; continue; }
                                    keepT.Add(a4); keepT.Add(b5); keepT.Add(c4);
                                }
                                keptPerSub[su] = keepT.ToArray();
                            }
                            triOut -= lost;
                            for (int i = 0; i < vc; i++) if (into[i] != i) used[i] = false;
                            diag?.Invoke($"authored cap: welded {welds} crack(s) along the trims at "
                                       + $"{CrackWeldTolerance:F4}"
                                       + (lost > 0 ? $", dropping {lost} triangle(s) left with no area" : "")
                                       );
                        }
                    }

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
                    // RELAX THE SHELL AROUND THE TOES, OUTWARD ONLY. The cap's own repairs cannot reach
                    // a fault in the surface it was grafted onto: one nail vertex sitting 0.0003 off the
                    // skin came out 0.0053 proud of the shell, and the socket fit, the fit bound, the
                    // clearance ceiling and the patch relax each left it at exactly that.
                    //
                    // Done the way a modelling package does it, which two earlier attempts were not:
                    //
                    //   WELDED, so the copies a UV chart split apart - 66 vertices over 56 positions
                    //   around this spot - move as one point instead of opening the seam between them.
                    //
                    //   UNIQUE EDGES, not one per triangle corner, or the average leans toward whichever
                    //   neighbour carries more triangles rather than toward the middle.
                    //
                    //   TAUBIN, whose negative second step undoes the shrink a plain Laplacian causes.
                    //
                    //   AND NEVER INWARD. That is the part neither earlier attempt had, and the reason
                    //   both made things worse. Smoothing a surface held a millimetre off a body moves
                    //   it toward its neighbours' average, which on a curved shell means INTO the body;
                    //   the clearance pass then shoves it back out, and the two fight. Measured, the
                    //   clearance pass went from lifting about a hundred vertices to 471. Dropping the
                    //   inward component of each step leaves the smoothing purely lateral, so it takes
                    //   the jaggedness out and leaves nothing for the pass below to undo.
                    if (capAllVerts.Count > 0 && pEl2 is { } pr0 && ShellRelaxPasses > 0)
                    {
                        Span<float> tmpS = stackalloc float[4];
                        var cur0 = new Vec3[vc];
                        for (int i = 0; i < vc; i++)
                        {
                            if (!used[i]) continue;
                            ReadTyped(outStreams[pr0.Stream], i * outStrides[pr0.Stream] + pr0.Offset,
                                      pr0.Type, tmpS);
                            cur0[i] = new Vec3(tmpS[0], tmpS[1], tmpS[2]);
                        }

                        var nodeOf = new Dictionary<(int, int, int), int>();
                        var owner = new int[vc];
                        Array.Fill(owner, -1);
                        var nodePos = new List<Vec3>();
                        var nodeVerts = new List<List<ushort>>();
                        for (ushort i = 0; i < vc; i++)
                        {
                            if (!used[i]) continue;
                            var key = QuantPos(cur0[i].X, cur0[i].Y, cur0[i].Z);
                            if (!nodeOf.TryGetValue(key, out int nd))
                            {
                                nd = nodePos.Count;
                                nodeOf[key] = nd;
                                nodePos.Add(cur0[i]);
                                nodeVerts.Add([]);
                            }
                            owner[i] = nd;
                            nodeVerts[nd].Add(i);
                        }

                        int nn = nodePos.Count;
                        var nb = new HashSet<int>[nn];
                        var acc = new Vec3[nn];
                        foreach (var sub in keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                            {
                                int a2 = owner[sub[t]], b2 = owner[sub[t + 1]], c2 = owner[sub[t + 2]];
                                if (a2 < 0 || b2 < 0 || c2 < 0) continue;
                                foreach (var (x2, y2) in new[] { (a2, b2), (b2, c2), (c2, a2) })
                                {
                                    if (x2 == y2) continue;
                                    (nb[x2] ??= []).Add(y2);
                                    (nb[y2] ??= []).Add(x2);
                                }
                                var e1 = new Vec3(nodePos[b2].X - nodePos[a2].X, nodePos[b2].Y - nodePos[a2].Y,
                                                  nodePos[b2].Z - nodePos[a2].Z);
                                var e2 = new Vec3(nodePos[c2].X - nodePos[a2].X, nodePos[c2].Y - nodePos[a2].Y,
                                                  nodePos[c2].Z - nodePos[a2].Z);
                                var fn = new Vec3(e1.Y * e2.Z - e1.Z * e2.Y, e1.Z * e2.X - e1.X * e2.Z,
                                                  e1.X * e2.Y - e1.Y * e2.X);
                                foreach (int q in new[] { a2, b2, c2 })
                                    acc[q] = new Vec3(acc[q].X + fn.X, acc[q].Y + fn.Y, acc[q].Z + fn.Z);
                            }

                        var outN = new Vec3[nn];
                        for (int n = 0; n < nn; n++) outN[n] = NormalizeOr(acc[n], default);

                        var move = new bool[nn];
                        int inBand = 0;
                        for (int n = 0; n < nn; n++)
                        {
                            if (nb[n] is not { Count: > 2 }) continue;
                            if (outN[n] is { X: 0, Y: 0, Z: 0 }) continue;
                            foreach (var q0 in capAllVerts)
                                if (Dist(nodePos[n], q0) <= ShellRelaxReach) { move[n] = true; inBand++; break; }
                        }

                        var start = nodePos.ToArray();
                        var cur = nodePos.ToArray();
                        var nxt = new Vec3[nn];
                        for (int pass = 0; pass < ShellRelaxPasses; pass++)
                            foreach (float lam in new[] { ShellRelaxLambda, ShellRelaxMu })
                            {
                                Array.Copy(cur, nxt, nn);
                                for (int n = 0; n < nn; n++)
                                {
                                    if (!move[n]) continue;
                                    Vec3 sum = default;
                                    int c3 = 0;
                                    foreach (int mn in nb[n]!)
                                    { sum = new Vec3(sum.X + cur[mn].X, sum.Y + cur[mn].Y, sum.Z + cur[mn].Z); c3++; }
                                    if (c3 == 0) continue;
                                    float ic = 1f / c3;
                                    var to = new Vec3(cur[n].X + (sum.X * ic - cur[n].X) * lam,
                                                      cur[n].Y + (sum.Y * ic - cur[n].Y) * lam,
                                                      cur[n].Z + (sum.Z * ic - cur[n].Z) * lam);

                                    // Measured from where it STARTED, so the constraint is on the result
                                    // rather than on one step of it.
                                    float dx7 = to.X - start[n].X, dy7 = to.Y - start[n].Y, dz7 = to.Z - start[n].Z;
                                    var un = outN[n];
                                    float along = dx7 * un.X + dy7 * un.Y + dz7 * un.Z;
                                    if (along < 0f)
                                    {   // drop the inward part; keep the sideways part entire
                                        dx7 -= un.X * along; dy7 -= un.Y * along; dz7 -= un.Z * along;
                                    }
                                    float dl = MathF.Sqrt(dx7 * dx7 + dy7 * dy7 + dz7 * dz7);
                                    if (dl > ShellRelaxMaxDrift)
                                    {
                                        float k4 = ShellRelaxMaxDrift / dl;
                                        dx7 *= k4; dy7 *= k4; dz7 *= k4;
                                    }
                                    nxt[n] = new Vec3(start[n].X + dx7, start[n].Y + dy7, start[n].Z + dz7);
                                }
                                (cur, nxt) = (nxt, cur);
                            }

                        int smoothed = 0;
                        float worstS = 0f;
                        for (int n = 0; n < nn; n++)
                        {
                            if (!move[n]) continue;
                            float d5 = Dist(cur[n], start[n]);
                            if (d5 <= 1e-6f) continue;
                            foreach (var i in nodeVerts[n])
                                WriteXYZ(outStreams[pr0.Stream], i * outStrides[pr0.Stream] + pr0.Offset,
                                         pr0.Type, cur[n].X, cur[n].Y, cur[n].Z);
                            worstS = MathF.Max(worstS, d5);
                            smoothed++;
                        }
                        if (smoothed > 0)
                            diag?.Invoke($"authored cap: relaxed {smoothed} of {inBand} welded shell points "
                                       + $"around the toes, outward only, furthest {worstS:F5}");
                    }

                    if (bodySolid == null)
                    {
                        bodySolid = CollectSkinTriangles(sourceModels, dropIslands: false);

                        // ONLY THE NON-SKIN THAT HUGS THE SKIN. This set exists for toenails, which a
                        // body may carry on their own mesh and a shoe routinely brings with it — but
                        // "not skin" is also every strap and sole of that shoe, and a sandal strap
                        // crossing the toes is SUPPOSED to sit outside the stocking. Handing the whole
                        // lot to the clearance pass had it inflating the shell to clear the footwear:
                        // 667 vertices lifted, every one pinned at the 3 mm ceiling, against 124 at
                        // 0.65 mm on the same body barefoot.
                        //
                        // A nail lies on the flesh; a strap stands off it. That is the whole test, and
                        // it needs no material names, so it holds for any shoe anyone models.
                        var skinNear = new HashSet<(int, int, int)>();
                        foreach (var t in bodySolid)
                            foreach (var q in new[] { t.A, t.B, t.C })
                                skinNear.Add(((int)MathF.Floor(q.X / NailHugsSkin),
                                              (int)MathF.Floor(q.Y / NailHugsSkin),
                                              (int)MathF.Floor(q.Z / NailHugsSkin)));
                        bool HugsSkin(Vec3 q)
                        {
                            int cx = (int)MathF.Floor(q.X / NailHugsSkin);
                            int cy = (int)MathF.Floor(q.Y / NailHugsSkin);
                            int cz = (int)MathF.Floor(q.Z / NailHugsSkin);
                            for (int dx = -1; dx <= 1; dx++)
                            for (int dy = -1; dy <= 1; dy++)
                            for (int dz = -1; dz <= 1; dz++)
                                if (skinNear.Contains((cx + dx, cy + dy, cz + dz))) return true;
                            return false;
                        }

                        // ...AND ONLY IF IT IS NAIL-SIZED. Proximity alone is not enough: a shoe's inner
                        // surfaces lie against the foot as closely as a nail does, so the first cut of
                        // this kept 18589 of its triangles and the pass went on lifting the shell around
                        // the footwear exactly as before. What separates them is that a toenail is a
                        // small island of a few hundred triangles and a shoe is one piece of tens of
                        // thousands. Grouped by shared position, since these arrive as a triangle soup.
                        var nonSkin = CollectSkinTriangles(sourceModels, skinOnly: false,
                                                           dropIslands: false, nonSkin: true);
                        var owner = new Dictionary<(int, int, int), int>();
                        var parent = new List<int>();
                        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                        void Union(int a, int b)
                        { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
                        int NodeAt(Vec3 q)
                        {
                            var k = QuantPos(q.X, q.Y, q.Z);
                            if (owner.TryGetValue(k, out int got)) return got;
                            owner[k] = parent.Count;
                            parent.Add(parent.Count);
                            return parent.Count - 1;
                        }
                        var triNode = new int[nonSkin.Count];
                        for (int i = 0; i < nonSkin.Count; i++)
                        {
                            var t = nonSkin[i];
                            int na = NodeAt(t.A), nb = NodeAt(t.B), nc = NodeAt(t.C);
                            Union(na, nb); Union(nb, nc);
                            triNode[i] = na;
                        }
                        var islandSize = new Dictionary<int, int>();
                        for (int i = 0; i < nonSkin.Count; i++)
                        {
                            int r = Find(triNode[i]);
                            islandSize[r] = islandSize.GetValueOrDefault(r) + 1;
                        }

                        int keptNonSkin = 0, dropped = 0;
                        for (int i = 0; i < nonSkin.Count; i++)
                        {
                            var t = nonSkin[i];
                            var ctr = new Vec3((t.A.X + t.B.X + t.C.X) / 3f,
                                               (t.A.Y + t.B.Y + t.C.Y) / 3f,
                                               (t.A.Z + t.B.Z + t.C.Z) / 3f);
                            if (islandSize[Find(triNode[i])] <= NailIslandMaxTris && HugsSkin(ctr))
                            { bodySolid.Add(t); keptNonSkin++; }
                            else dropped++;
                        }
                        if (dropped > 0)
                            diag?.Invoke($"authored cap: clearance sees {keptNonSkin} non-skin triangle(s) "
                                       + $"small enough and close enough to be a toenail, and ignores "
                                       + $"{dropped} (a shoe is meant to be outside the shell)");
                    }
                    if (bodySolid.Count > 0 && capAllVerts.Count > 0 && nEl2 is { } ne10)
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
                        foreach (var t in bodySolid)
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

            // WHAT SURVIVED, recounted. `used` was filled while triangles were being kept, but the weld
            // drops collapsed ones afterwards, and a vertex referenced only by those stayed marked used —
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
            // table, and once the table has been replaced its entries mean nothing — so publish the whole
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
                // Cleared when Proteus owns this geometry's visibility — see
                // ContentGeometry.OwnAttributes. Leaving the tag on would let the HOST item's IMC
                // mask cull a submesh we already decided to keep.
                W32(ns, 8, clearAttrs ? 0 : RemapAttrs(src, U32(ss + 8)));
                // The submesh's BONE WINDOW. Normally the source's, rebased — but not when this mesh's
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
            // other meshes' tables — ubyte4 vertex indices cap a table at 255 entries.
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

            // THE CAP TAKES THE FULL PUSH, like everything else in the layer.
            //
            // It was briefly emitted at push - BaseOffset, on the reasoning that the cap is modelled at
            // the shell's surface already: the Rue cap sits a median 1.12 mm off the skin and BaseOffset
            // is 1.00 mm, so pushing it again put it at 2.09 mm against the shell's 1.00 and left a ridge
            // along the join. But that 1.12 was a coincidence, not a rule — the Neolithe cap measures
            // 1.64 mm off its own skin. Each cap is authored at whatever height its author chose, so
            // subtracting a fixed offset moves every cap by a different amount relative to the rim it has
            // to meet, and it broke the weld on both bodies.
            //
            // The ridge is real and still wants fixing. The measurement to drive it is the cap's OWN
            // clearance over the body it is being placed on, not a constant.
            // THE CAP IS PUSHED TO THE SHELL'S HEIGHT, NOT BY THE SHELL'S PUSH.
            //
            // It used to take the full push like everything else, and that is wrong for the same reason
            // in both directions: the cap is not a copy of the skin sitting on the skin, it is a modelled
            // object already standing off it by whatever its author chose. Measured on this body the
            // Neolithe cap sits 1.58 mm out and the Bibo one 1.82 mm, so adding another millimetre put
            // the cap at 2.6 mm against a shell at 1.0 — visibly lifted off the toes, with a ridge that
            // size everywhere the two meet.
            //
            // Subtracting a CONSTANT was tried once and broke the weld on both bodies, which is what the
            // note here used to say: BaseOffset is not what any particular cap was authored at, so taking
            // it off moves each cap by a different amount relative to the rim it has to meet. The fix the
            // note asked for is this one — the cap's OWN clearance over the body it is being placed on,
            // measured at selection, so whatever height it was drawn at it arrives level with the shell.
            //
            // Clamped at zero: a cap authored below the shell's height is left where it is rather than
            // pulled into the skin, since the ramp behind the lip can lift the shell down to meet it but
            // nothing can rescue geometry that has been pushed inside the body.
            float capPush = capStandoff > 0f ? MathF.Max(0f, push - capStandoff) : push;

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
                    BustBridgeStrength = def.BustBridgeStrength,
                    NippleSmoothStrength = def.NippleSmoothStrength,
                    CleftBridgeStrength = def.CleftBridgeStrength,
                };

            // The cap's rim, pushed to this layer's offset, ready for the shell meshes below to close
            // onto. It has to exist BEFORE they are emitted, which is why the projection runs here rather
            // than at the graft — it is cached, so asking early costs nothing.
            weldRim = null;
            weldRimVerts.Clear();
            weldRimPos.Clear();
            capAllVerts.Clear();
            welded = 0; weldWorst = 0; weldWorstD = 0f; capWelded = 0;
            capRimLandings.Clear();
            shellRim.Clear();
            // THE CAP IS TRIMMED AGAINST A SLIGHTLY WIDER COVERAGE THAN THE SHELL IS.
            //
            // Both use the same test — keep a triangle if ANY texel under its UV footprint is visible —
            // and that test dilates outward by the size of the triangle asking. The cap's faces are far
            // smaller than the shell's (sub-texel against several texels), so the same coverage curve
            // stops the cap EARLIER than it stops the shell. The cap's edge then lands part-way across
            // surviving shell fabric, with no shell boundary opposite to weld or split against: measured
            // on Rue's inner layer, 153 of 315 cap rim points had no shell boundary within reach and 659
            // lip vertices never welded, leaving 0.87 of cap edge lying on top of the shell. That reads as
            // a line on the fabric, which is the seam — not a gap.
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
                    BustBridgeStrength = def.BustBridgeStrength,
                    NippleSmoothStrength = def.NippleSmoothStrength,
                    CleftBridgeStrength = def.CleftBridgeStrength,
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

                    // The rim of the cap AS THIS LAYER WILL EMIT IT — that is, after the layer's own
                    // coverage has trimmed it. It cannot be worked out once and shared: a layer covering
                    // a sliver of the foot keeps a sliver of the cap, and its rim is nothing like the
                    // full cap's. Measured before this: the outer layer emitted the whole cap against a
                    // 400-face shell and its boundary stood a median of 0.015 from anything to weld to,
                    // a free edge across the top of the foot — which is what shows in game, because the
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
            // See CapCutErode — normally zero, because the weld closes the join instead.
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
            // ENDS. Cutting to the footprint leaves the shell standing inside the cap's own boundary —
            // its rim ends up a median of 0.027 from the rim it is supposed to meet — because a shell
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

                // CUT TO THE CAP, not to the painted map — the map only says a cap is WANTED.
                //
                // The map is an authored asset sized for the cap it was drawn alongside; on another
                // body's cap it takes out far more shell than that cap fills — measured on Rue, 8156
                // texels cut against 2480 covered. Nothing snaps a difference like that shut: the weld
                // hauls boundary vertices as much as 0.0139 to reach the rim, the triangles behind them
                // collapse, WeldCollapse drops them, and the shell tears open well away from the join.
                // And a map that is simply wrong — all white, say — marks every UV island end to end, so
                // MaxCoreFraction skips every one of them, nothing is cut at all, and the cap is laid
                // over untouched sleeved toes.
                //
                // Cutting to the cap was tried twice before and reverted, both times against the BARE
                // footprint, which under-covers: the rasterised faces stop exactly where the cap's
                // surface stops, so the shell was left standing just INSIDE the cap's boundary with
                // nothing to weld to — its rim a median 0.027 from the rim it was supposed to meet. The
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

                diag?.Invoke($"authored cap: cutting to the cap's own footprint — {painted} texels "
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
                    BustBridgeStrength = def.BustBridgeStrength,
                    NippleSmoothStrength = def.NippleSmoothStrength,
                    CleftBridgeStrength = def.CleftBridgeStrength,
                };
            }

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

                    // cutDef, not def: the toe-cap cut is applied through the layer's coverage argument.
                    // DropConnectors is per-source now, off the SourceSpec.
                    EmitMesh(src, m, matIndex, push, preserve: false, cov: cutDef, mapBase, ref mapAppended,
                        dropConnectors: src.DropConnectors, otherBands: src.OtherPartBands);
                }
            }

            if (weldRim != null)
                diag?.Invoke($"authored cap: welded {welded} cut-lip vertices onto the rim "
                           + $"(furthest moved {weldWorstD:F4}), {weldWorst} left beyond {WeldRadius:F3}");

            // Graft the authored cap on for any layer that asked for one, wearing that layer's material.
            // Emitted verbatim apart from the layer push, the weld back onto the shell's rim, and the
            // projected UV — it is already modelled where it belongs on the foot it was authored against.
            // Fitting it to a DIFFERENT foot comes later and is a separate step.
            // Skipped when the cap went INTO a shell mesh, which is the normal path now: emitting it again
            // beside the shell would draw it twice and put back the very boundary the graft removed.
            if (capSrc is { } cs && def.ToeCap != null && !capGrafted)
            {
                // The cap is authored WITHOUT UVs — every vertex arrives at (0,1), so without this it
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

        stats = new Stats(meshCount, subOut.Count, boneCount, triIn, triOut, vertOut, capDeclined, capUsed);
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

        // CLAMPED to what is actually left in the file, for the same reason the v5 bone count above is: this
        // length is read straight off the disk, so a model walked wrongly — or simply truncated — puts
        // arbitrary bytes here, and `new ushort[0xFFFFFFFF / 2]` asks for four gigabytes and takes the whole
        // caller down with an OutOfMemoryException. Reading a short map degrades to geometry the shell does
        // not split; allocating on a corrupt length degrades to nothing working at all.
        uint mapBytes = U32(p); p += 4;
        var map = new ushort[Math.Clamp((long)mapBytes, 0, Math.Max(0, s.Length - p)) / 2];
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

    /// <summary>
    /// Per-vertex region weight for one mesh — how much of the vertex is skinned to any of
    /// <paramref name="bones"/>, clamped to 1. Null when this mesh's bone table names none of them, which
    /// is every mesh but the one carrying that part of the body and is the cheap early-out these passes
    /// lean on.
    /// <para/>
    /// The bones, not the art, define the region: they are on the game's own skeleton, so every body a
    /// shell can be cut from has them. A painted map would have to be redrawn per UV space and would drift
    /// from the mesh; a protrusion heuristic would find the belly and the shoulder blades as readily as
    /// the bust.
    /// <para/>
    /// A LIST rather than the original pair, because the regions differ in shape. The bust is two bones
    /// whose influence misses the sternum, so the gap between them IS the cleavage and the region grows
    /// into it. The buttocks are one midline bone (<see cref="HipBone"/>) covering both cheeks and the
    /// cleft together — and covering the crotch on the other side of the body, which is why a seed taken
    /// from bones alone is not enough down there and the caller adds a facing gate.
    /// <para/>
    /// Weights are read the same way <see cref="TryReadLod0Geometry(byte[], out float[], out float[],
    /// out int[], out (string, float)[][], out float[], bool, bool, Func{string, bool})"/> reads them:
    /// through the mesh's own bone table into the model's names, because a blend index means nothing
    /// outside the table it was written against.
    /// </summary>
    /// <param name="losesTo">
    /// Bones that, where they outweigh <paramref name="bones"/>, mean the vertex belongs to them instead.
    /// <para/>
    /// The hip's influence does not stop at the buttocks — it runs down into the upper thighs, and there
    /// the surface either side of the midline is two LEGS rather than two cheeks. Spanned, that lays a
    /// sheet across the gap between them: measured, 192 vertices moved by up to 68mm through the 120mm
    /// below the cleft. Bounding it by height would need a number per body; asking which bone owns the
    /// vertex is the same question the skeleton already answers, and it cut exactly those bands and
    /// nothing else.
    /// </param>
    private static float[]? MeshRegionWeights(Source src, int m, ushort vc, VElem[] decl, uint[] vbo,
                                              byte[] bs, string[] bones, string[]? losesTo = null)
    {
        var s = src.S;
        int mo = src.MeshStart + m * 36;
        if (mo + 36 > s.Length) return null;
        ushort meshBoneTbl = BitConverter.ToUInt16(s, mo + 14);
        if (meshBoneTbl >= src.BoneTables.Length) return null;
        var boneTbl = src.BoneTables[meshBoneTbl];

        // Which LOCAL indices are the region's bones. Resolved once per mesh, before any vertex is
        // touched: a torso is thousands of vertices and every other mesh of the body would otherwise pay
        // for the whole per-vertex scan to learn it has none of them.
        Span<bool> isRegion = stackalloc bool[256];
        Span<bool> isRival = stackalloc bool[256];
        bool anyBone = false;
        for (int i = 0; i < boneTbl.Length && i < 256; i++)
        {
            if (boneTbl[i] >= src.BoneNames.Length) continue;
            var nm = src.BoneNames[boneTbl[i]];
            foreach (var want in bones)
                if (string.Equals(nm, want, StringComparison.OrdinalIgnoreCase))
                { isRegion[i] = true; anyBone = true; break; }
            if (losesTo == null) continue;
            foreach (var rival in losesTo)
                if (string.Equals(nm, rival, StringComparison.OrdinalIgnoreCase))
                { isRival[i] = true; break; }
        }
        if (!anyBone) return null;

        VElem? wEl = null, iEl = null;
        foreach (var el in decl)
        {
            if (el.Usage == UseBlendWeight) wEl ??= el;
            else if (el.Usage == UseBlendIndices) iEl ??= el;
        }
        if (wEl is not { } we || iEl is not { } ie || we.Stream > 2 || ie.Stream > 2) return null;

        int nInf = BlendCount(we.Type);
        var outW = new float[vc];
        bool any = false;
        for (int k = 0; k < vc; k++)
        {
            int wa = src.Vb + (int)vbo[we.Stream] + k * bs[we.Stream] + we.Offset;
            int ia = src.Vb + (int)vbo[ie.Stream] + k * bs[ie.Stream] + ie.Offset;
            if (wa < 0 || ia < 0 || wa + nInf > s.Length || ia + nInf > s.Length) continue;
            float acc = 0f, rival = 0f;
            for (int q = 0; q < nInf; q++)
            {
                int local = s[ia + q];
                if (local >= 256) continue;
                if (isRegion[local]) acc += s[wa + q] / 255f;
                else if (isRival[local]) rival += s[wa + q] / 255f;
            }
            if (acc <= 0f || rival >= acc) continue;
            outW[k] = MathF.Min(1f, acc);
            any = true;
        }
        return any ? outW : null;
    }

    private static int BuildVerbatim(
        byte[] s, int vb, int srcDeclOff, ushort vc, VElem[] decl, uint[] vbo, byte[] bs, float push,
        out byte[][] outStreams, out byte[] outStrides, out byte[] declBlock, out (float U, float V)[] uvs,
        out (float U, float V)[]? uvsPreConv,
        UVRemapService.UvConversion? uvConv,
        out Vec3[]? capSrcPos, out Vec3[]? capOutPos, out ToeCapPlan? capPlan,
        sbyte[]? sides = null,
        SecondSkinLayer? cap = null, ushort[]? capTris = null, Action<string>? capLog = null,
        bool buildCapGeometry = true,
        Func<Vec3[], Vec3[], ushort[], (float U, float V)[], BustBridgePlan?>? bridge = null)
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

            // The bust bridge, on the same footing: another displacement of the vertices this mesh already
            // has. Resolved through the caller's own memo rather than solved here, so every layer of a host
            // shares one answer and the stack keeps its order; it can only run at this point because the
            // region is gated on coverage and uvs[] is only now on the tile that map is indexed over.
            var bridgePlan = bridge is not null && capTris is not null
                ? bridge(basePos, baseNrm, capTris, uvs)
                : null;

            // Normals recomputed from the REBUILT surface — the source triangles minus the ones the cut
            // removed, plus the cap's own. Without this the shell keeps shading as the toes it replaced.
            // The bridge changes no topology, so it reshades against the mesh's own triangles; a flattened
            // span still carrying the cleavage's normals reads as a cleavage however far it moved, and the
            // push along a stale normal drives the two sides of the span apart.
            var finalNrm = plan is not null
                ? CapNormals(basePos, baseNrm, plan, CappedTopology(plan, capTris!))
                : bridgePlan is not null
                    ? RelaxedNormals(basePos, baseNrm, bridgePlan.Delta, bridgePlan.NodeOf, bridgePlan.NodeWeight,
                                     bridgePlan.NodeNormal, capTris!)
                    : baseNrm;

            int stride = outStrides[pw.Stream];
            int normalsWritten = 0, uvsWritten = 0;
            bool encoderMissing = false;
            var outPos = plan is null ? null : new Vec3[vc];

            for (int i = 0; i < vc; i++)
            {
                var p = basePos[i];
                var n = finalNrm[i];
                if (delta is not null) p = new Vec3(p.X + delta[i].X, p.Y + delta[i].Y, p.Z + delta[i].Z);
                if (bridgePlan is not null)
                {
                    var bd = bridgePlan.Delta[i];
                    p = new Vec3(p.X + bd.X, p.Y + bd.Y, p.Z + bd.Z);
                }

                var final = new Vec3(p.X + n.X * push, p.Y + n.Y * push, p.Z + n.Z * push);
                WriteXYZ(outStreams[pw.Stream], i * stride + pw.Offset, pw.Type, final.X, final.Y, final.Z);
                if (outPos is not null) outPos[i] = final;

                // Only vertices the cap or the bridge actually reached get a new normal; everything else
                // keeps the bytes it arrived with. The normal element has its own stream — not pos's.
                bool reshade = plan is not null && plan.NodeWeight[plan.NodeOf[i]] > 0f
                            || bridgePlan is not null && bridgePlan.NodeWeight[bridgePlan.NodeOf[i]] > 0f;
                if (reshade && norm is { } ne2)
                {
                    if (WriteNormal(outStreams[ne2.Stream], i * outStrides[ne2.Stream] + ne2.Offset, ne2.Type,
                            n.X, n.Y, n.Z))
                        normalsWritten++;
                    else
                        encoderMissing = true;
                }

                // ...and the UV it was projected onto, for the same vertices. Written into every uv slot
                // the mesh has, exactly as the normalization pass above did — uv1 mirrors uv0 for the
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
                    // Through main's uv1 plan rather than the three loose locals this used to read:
                    // the .zw slot, an explicit uidx1 and an appended one are the same three cases, and
                    // PlanUv1 is now the single place that decides which of them this declaration has.
                    WriteUv1(uv1Plan, u0w, outStreams, outStrides, i, cu, cv);
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
                    capLog($"toe cap: no encoder for normal type {norm?.Type} — that mesh keeps its old shading");
            }

            // A cap and a bridge on ONE mesh: a foot and a chest are different meshes on every body seen
            // so far, so finalNrm above takes the cap's rebuilt topology and the bridge rides its
            // positions without reshading. Said out loud rather than assumed away — if a body ever puts
            // both on one mesh, the shading is what will look wrong, and this is the line that explains it.
            if (plan is not null && bridgePlan is not null)
                capLog?.Invoke("bust bridge: this mesh also carries a toe cap — the cap's normals win, the "
                             + "bridge moves positions only");
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
    /// the two work against each other — eroding by 3 left the lip a median of 0.007 from the rim instead
    /// of 0.002, and dragging it that far collapsed triangles (aspect 720, against 14 before). Kept as
    /// one number so the lap can be brought back if a cap is ever authored short of its map.
    /// </summary>
    private const int CapCutErode = 0;

    /// <summary>
    /// How far a boundary vertex may be dragged to meet the cap's rim. Past the widest mismatch the
    /// painted map produces (0.0093 measured, against a median edge of 0.0037) and short of the next
    /// open edge, so a vertex belonging to some other hole — an ankle cut, a coverage bite elsewhere —
    /// is left alone. It has to be tighter than it once was: the test is now "on the shell's boundary"
    /// rather than "the toe cap cut this away", which is a much broader set of vertices.
    /// </summary>
    private const float WeldRadius = 0.012f;

    /// <summary>
    /// How far a vertex the TOE-CAP cut exposed may be dragged to reach the cap's rim. Much longer than
    /// <see cref="WeldRadius"/> because that pull is the whole mechanism by which the map's deliberate
    /// over-cut is closed, and on a body other than the one the map was painted for it has real distance
    /// to cover — measured on Rue, 329 lip vertices sat beyond 0.012 and the join stayed open as a band
    /// of bare skin. The slivers a long pull leaves are dropped by <see cref="WeldCollapse"/>.
    /// </summary>
    private const float WeldCutReach = 0.04f;

    /// <summary>
    /// Fraction of the shell's own median edge whose SQUARE a triangle's area must fall under, after the
    /// weld touched it, before it counts as collapsed and is dropped.
    /// <para/>
    /// Deliberately tiny — a thousandth of a typical triangle's area — because every triangle dropped
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
    /// ONE — the open edge itself and nothing else. Everything else keeps exactly what the author
    /// weighted, including to the toenails, which the body's skin-only weights cannot express and which
    /// the cap collapses without. It was 3, which reached a ring and a half into geometry that had no
    /// business being touched: the two surfaces only have to agree where they meet.
    /// </summary>
    private const int CapSeamBlendRings = 1;

    /// <summary>
    /// Largest boundary loop, in edges, that counts as a hole in the skin rather than an edge of the
    /// mesh. The sockets a body leaves when its toenails are their own mesh measure ten and twelve; the
    /// ankle cut on the same foot runs to a hundred and sixty.
    /// </summary>
    private const int CapHoleMaxEdges = 20;

    /// <summary>
    /// ...and how far such a loop may reach in the atlas. A socket spans a few hundredths; a chart
    /// boundary spans the texture, and mistaking one for the other would distrust half the cap.
    /// </summary>
    private const float CapHoleMaxUvSpan = 0.08f;

    /// <summary>
    /// How far past a socket's own extent a landing is still compromised, as a multiple of its radius.
    /// <para/>
    /// It was 1.6, on the reasoning that the rim triangles reach outwards from the hole and it is those a
    /// landing is measured from. That is true, but it also claimed a band of toe well behind the nail and
    /// carried it along with the fit - in game the cap rose off the toe far enough to clip through a
    /// sandal strap. The rim itself is the part that cannot be trusted; just past it the surface is real.
    /// </summary>
    private const float CapSocketReach = 1.35f;

    /// <summary>
    /// Most a socket fit may move a vertex. The dish it exists to remove measured 0.0035, so this is not
    /// a bound on the repair so much as on the fit going wrong: a rotation solved from anchors that
    /// happen to be poorly spread can throw a patch a long way, and a nail standing proud of the toe is
    /// as visible as one sunk into it.
    /// </summary>
    private const float CapFitMaxMove = 0.0026f;

    /// <summary>
    /// Most the per-socket bound may grow beyond <see cref="CapFitMaxMove"/> for a socket larger than the
    /// median. A big toe's nail is half again the size of a little one's and sits that much prouder; past
    /// this the socket is not a nail and the fit should not be trusted with it either way.
    /// </summary>
    private const float CapFitMoveScaleMax = 2.0f;

    /// <summary>
    /// Where the fade from the fitted position back to the resolved one begins, as a fraction of the
    /// socket's radius. Inside this the nail is carried entirely by the fit; outside it the two are
    /// blended so the patch meets the surface around it without a step.
    /// </summary>
    private const float CapFitFeatherStart = 0.6f;

    /// <summary>
    /// Smoothing passes run over a socket patch once it has been fitted. A modelling package's relax at
    /// a low strength: enough to take the crease out of the seam between the fitted nail and the surface
    /// blended back to around it, not enough to move the nail.
    /// </summary>
    private const int CapRelaxPasses = 8;

    /// <summary>How far each pass eases a vertex toward the average of its neighbours.</summary>
    private const float CapRelaxWeight = 0.5f;

    /// <summary>
    /// Furthest relaxing may carry a vertex from where the fit put it. Laplacian smoothing shrinks what
    /// it is run on, and left unbounded over enough passes it would pull the nail flat again.
    /// </summary>
    private const float CapRelaxMaxDrift = 0.0008f;

    /// <summary>
    /// Most the per-socket relax drift may grow beyond <see cref="CapRelaxMaxDrift"/> for a socket larger
    /// than the median. Higher than the fit's equivalent because settling a crease is a gentler thing
    /// than moving a nail: it is bounded by the shape of the surface either way.
    /// </summary>
    private const float CapRelaxDriftScaleMax = 4.0f;

    /// <summary>Smoothing rounds over the shell around the toes. Each runs a positive step and a
    /// negative one, so this counts rounds rather than passes.</summary>
    private const int ShellRelaxPasses = 4;

    /// <summary>The smoothing step of Taubin's pair.</summary>
    private const float ShellRelaxLambda = 0.50f;

    /// <summary>...and the un-smoothing step, slightly larger in magnitude, which is what keeps the
    /// surface from shrinking.</summary>
    private const float ShellRelaxMu = -0.53f;

    /// <summary>How near the cap a shell point must be to be relaxed at all.</summary>
    private const float ShellRelaxReach = 0.004f;

    /// <summary>Furthest the relax may carry a point from where it started, once the inward part of the
    /// move has been dropped.</summary>
    private const float ShellRelaxMaxDrift = 0.0010f;

    /// <summary>
    /// How far apart two boundary vertices may sit and still be the same point. The trims leave pairs as
    /// close as 0.00003 and the shell shows daylight between them; this is the tolerance a modelling
    /// package would be given to weld them, and it is well under an edge length so nothing that is
    /// genuinely two places gets merged.
    /// </summary>
    private const float CrackWeldTolerance = 0.0010f;

    /// <summary>
    /// How far apart in the atlas two coincident boundary vertices may sample and still be welded. A
    /// crack whose sides sit in one chart closes harmlessly; one that straddles two charts must stay
    /// open, because merging them drags every face on the losing side across the atlas and paints a
    /// bright streak where a hairline crack used to be.
    /// </summary>
    private const float CrackWeldUvTolerance = 0.0020f;

    /// <summary>Rings to walk out from a socket patch looking for landed vertices to fit against.</summary>
    private const int CapFitAnchorRings = 6;

    /// <summary>
    /// Fewest anchors before a patch is fitted rather than left alone. Three would define a rotation and
    /// be at the mercy of any one of them; this is enough that the fit describes the neighbourhood.
    /// </summary>
    private const int CapFitMinAnchors = 8;

    /// <summary>
    /// Relaxation sweeps used to solve the cap's shape-preserving fit. Enough that the solution has
    /// settled at this mesh size; zero disables it and takes the raw landings.
    /// </summary>
    private const int CapShapePasses = 48;

    /// <summary>
    /// How hard a landing pulls, against the cap's own curvature. Low, because the landing is the noisy
    /// term and the curvature is the trustworthy one: at 1 the two weigh the same and the noise comes
    /// straight through, and near zero the cap keeps its shape but drifts off the foot.
    /// </summary>
    private const float CapShapeFollow = 0.20f;

    /// <summary>How much further off the skin the shape fit may leave a vertex than its landing was.</summary>
    private const float CapShapeLiftAllow = 0.0004f;

    /// <summary>Halvings used to walk a lifting move back toward its landing before giving up on it.</summary>
    private const int CapShapeBackoffSteps = 6;

    /// <summary>Polar-decomposition iterations in <see cref="BestRotation"/>. It converges quadratically;
    /// this is well past the point where the step stops changing anything.</summary>
    private const int CapFitPolarSteps = 24;

    /// <summary>
    /// Weld-then-drop rounds. Dropping a collapsed triangle exposes vertices that were interior when the
    /// boundary was worked out, so one round always leaves a few behind; the second finds them and in
    /// practice there is nothing left for a third.
    /// </summary>
    private const int WeldRounds = 3;

    /// <summary>
    /// Smallest island, as a fraction of the largest, that the authored cap will take UVs from. Keeps
    /// the feet and rejects the toenails, which carry their own UV island and are often the nearest
    /// surface to the cap — projecting onto one stretches a triangle across the gap between islands.
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
    /// far side of a seam — which is the only rival that matters.
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
    /// How much of an island the cap may claim. Past this there is no rim left to sew to — a toenail is
    /// masked end to end — and the island is better left alone inside the cap.
    /// </summary>
    private const float MaxCoreFraction = 0.8f;

    /// <summary>How far past the last ring the end of the cap reaches, in ring spacings.</summary>
    private const float TipReach = 0.5f;

    /// <summary>
    /// Shrinking rings that round the end off before it closes, each halving the slot count of the one
    /// before, so the cap does not fan its full-width last ring straight to a point.
    /// <para/>
    /// They keep the rim's slot count — the grid patch closes whatever is left, so nothing has to narrow.
    /// </summary>
    private const int TipRings = 3;

    /// <summary>Fewest slots a dome ring is worth building with; below this the closing patch takes over.</summary>
    private const int MinDomeSlots = 8;

    /// <summary>
    /// How far the end domes over, as a fraction of the cap's own radius there. Scaling it to the ring
    /// spacing instead — which is perhaps a tenth of that — leaves the toe box ending in a stump.
    /// </summary>
    private const float TipRound = 0.3f;


    /// <summary>
    /// Closest the relaxed cap may come to the skin, in mesh edge lengths — the fabric's thickness. Taken
    /// from what a hand pass over this cap left in place (its tightest 5% sat at about a fifth of an edge).
    /// </summary>
    private const float SkinClearance = 0.2f;

    /// <summary>
    /// Furthest the finished cap may float above the skin it lies on, in mesh edge lengths — how much
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
    /// off the foot, which an earlier version — summing every skin vertex's request instead of taking
    /// the largest — did spectacularly.
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
    /// down into it. Below this the cap settles onto the toe — and into the shallow valleys between them,
    /// which is wanted; above it, it bridges. Lower creeps deeper into the valleys, higher bridges more.
    /// </summary>
    private const float BridgeSpan = 1.5f;

    /// <summary>Angular buckets a cross-section's outline is read into, when it has more slots than this.</summary>
    private const int MinOutlineBins = 32;


    /// <summary>Smoothing passes over the finished cap — the equivalent of relaxing it by hand.</summary>
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
    /// radius — the fabric's thickness, in effect. Zero would leave it tangent to the toes underneath and
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
    /// all of it — and must never be removed.
    /// </summary>
    private const float SmallIslandFraction = 0.25f;

    /// <summary>
    /// Smallest masked island worth capping. Guards against a stray scrap of geometry — a toenail, a
    /// detached sliver — being treated as its own toe box.
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

    /// <summary>Distance at which two capped corners count as the same point — the weld's own grid.</summary>
    private const float DegenerateWeldDistance = 1e-5f;

    /// <summary>
    /// Every triangle of one mesh, as mesh-local vertex indices, across all of its submeshes. The toe cap
    /// needs the mesh's full topology (adjacency) — including submeshes coverage or the connector filter
    /// will later drop, since those still hold the surface together.
    /// </summary>
    private static ushort[] MeshTriangles(Source src, ushort subIdx, ushort subCount)
        => MeshTrianglesAt(src, src.Ib, subIdx, subCount);

    /// <inheritdoc cref="MeshTriangles"/>
    /// <param name="indexBase">
    /// Where this LOD's index buffer starts. <see cref="Source.Ib"/> is LOD0's, which is all any other walk
    /// in this file needs; <see cref="RewriteFaceUv0"/> reads LOD1 and LOD2 as well, and their submesh
    /// offsets are relative to their OWN buffer — read through LOD0's they address another LOD's triangles.
    /// </param>
    private static ushort[] MeshTrianglesAt(Source src, int indexBase, ushort subIdx, ushort subCount)
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
    /// Toe cap: per-vertex displacement that inflates the masked region onto a smooth envelope, so a
    /// stocking shell webs the gaps between the toes instead of sleeving each toe individually.
    /// <para/>
    /// The region is treated as a height field measured radially from the centre of the masked area, and
    /// each height is repeatedly raised to at least the average of its neighbours'. Smoothing alone can't
    /// do this: it equalizes, converging right back onto the toes, and clamping it outward along each
    /// vertex's own normal stalls at once, because inside a gap the normals point sideways ACROSS the gap
    /// rather than out of it. Raising toward the neighbour mean, in a frame the whole region shares, lets
    /// a toe tip's height propagate into the gaps beside it — the taut membrane real hosiery forms.
    /// <para/>
    /// The result only ever inflates, so the toes stay inside the cap instead of poking through it, and
    /// every step is scaled by the vertex's mask value, so black is pinned and the cap fades into the
    /// untouched shell across the grey.
    /// <para/>
    /// Vertices are WELDED by source position first: a body mesh splits vertices at UV seams, and two
    /// coincident copies with different neighbour sets would otherwise smooth apart and crack open. Each
    /// weld group moves as one, by a single shared delta, so hard-edge normal splits keep their offsets.
    /// <para/>
    /// Returns null when nothing is masked — the caller then writes exactly what it would have without
    /// this feature.
    /// </summary>
    internal static Vec3[]? ToeCapDelta(
        Vec3[] pos, Vec3[] nrm, (float U, float V)[] uv, ushort[] tris,
        byte[] mask, int mw, int mh, float strength)
        => ToeCapSolve(pos, nrm, uv, tris, mask, mw, mh, strength)?.Delta;

    /// <summary>
    /// What the toe cap decided: the displacement, plus the welding and per-node data the normal pass
    /// needs. Moving the vertices is only half the job — a shell whose normals still describe five
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

        // One representative vertex per node — the cap's triangles are written in vertex indices.
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

        // Which islands the cap swallows whole — the toenails. Settled BEFORE anything is capped, because
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
            // declining an island looks exactly like the cut working — the shell simply comes out whole
            // — and a mask that lit the right region in the atlas still cut nothing at all because of a
            // gate down here. Cheap enough to always report; a foot has a handful of islands.
            if (masked is { Count: > 0 })
                capLogSink?.Invoke($"toe cap: island {c} of {islandSize[c]} node(s), {masked.Count} masked"
                    + (masked.Count < MinToeCapNodes ? $" — SKIPPED, under MinToeCapNodes ({MinToeCapNodes})" : ""));
            if (masked is not { Count: >= MinToeCapNodes }) continue;

            // The CORE of the mask — where it is actually painted in, not its antialiased fringe. A soft
            // edge covers a lot of ground at a value of 1 or 2/255, and letting that define the region
            // stretches it over the whole foot: the axis tilts and the slices below land mostly behind the
            // toes, where they do nothing. Everything that sets up the frame uses the core; the fringe
            // still moves, just by its own small weight.
            var core = new List<int>();
            foreach (int n in masked)
                if (nW[n] >= ToeCapCoreWeight) core.Add(n);
            if (core.Count < MinToeCapNodes)
            {
                capLogSink?.Invoke($"toe cap: island {c} — SKIPPED, core {core.Count} of {masked.Count} "
                    + $"masked is under MinToeCapNodes ({MinToeCapNodes}); mask weight below "
                    + $"{ToeCapCoreWeight} does not count");
                continue;
            }

            // A cap is sewn onto surviving geometry. An island that is ENTIRELY masked — each toenail is
            // — has no rim to sew to, so no cap can be built for it. It used to be left where it was, on
            // the assumption it would end up inside the cap; that held only while the cap ballooned over
            // the toes. Now that the cap hugs them, the nails stand proud of it in ten little scallops,
            // which is exactly the crunch it reads as. They are underneath a stocking, so drop them.
            //
            // Only ever a SMALL island: a mask painted over a whole foot would otherwise swallow the foot.
            if (core.Count > MaxCoreFraction * islandSize[c])
            {
                capLogSink?.Invoke($"toe cap: island {c} — SKIPPED, core {core.Count} is over "
                    + $"{MaxCoreFraction:P0} of the island's {islandSize[c]} node(s)");
                continue;   // marked by the pre-pass above
            }
            capLogSink?.Invoke($"toe cap: island {c} — CUT, core {core.Count} of {islandSize[c]} node(s)");

            // An authored cap is filling this region, so only the CUT is wanted: take the toe box out
            // and leave it to the modelled mesh. Everything past here — the swept rings, the dome, the
            // closing patch, the relax and the clearance passes that argue with it — exists solely to
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

            // ── the cut ────────────────────────────────────────────────────────────────────────────
            // Every triangle with a core corner leaves the mesh, and the edges left used by only one of
            // them form the rim the cap is sewn onto. Displacing the toes could never work — a stocking's
            // toe box is a DIFFERENT surface, not the toes moved — so the toes come out and a new one
            // goes in, exactly as a modeller builds it.
            var inCut = new bool[nodeCount];
            foreach (int n in core) inCut[n] = true;

            // A painted mask is never perfectly solid: grey specks and the deep creases between the toes
            // leave patches of unmasked geometry STRANDED inside the cut. Each one survives as a scrap
            // floating under the finished cap, ringed by its own hole — the overlapping shards on the top
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
            // each with the direction that is OUT of the body — taken from the corners' own normals, since
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
            // triangle — an earlier attempt appended them afterwards and every shortlist was already full
            // of flesh, so nothing ever tested against a nail and the numbers did not move.
            //
            // Their outward side is the direction away from the cap's own sweep axis, which is right for
            // something lying ON the surface the cap encloses. They take no part in the rim: the cap is
            // sewn to the mesh it was cut from, not to these.
            // The swallowed islands are geometry the cap has to close OVER, not through. They sit proud
            // of the flesh, so without this the cap passes underneath them and the player's own toenails
            // come through the fabric — 649 cap vertices inside a nail on the equipped body, worst 0.027.
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

            // The walk gives the rim its true cyclic order; only rotate and orient it, never re-sort —
            // sorting by angle crosses the stitch and shreds the seam.
            OrientLoop(loop, start, Flatten);
            int rimCount = loop.Count;

            // ── the sweep ──────────────────────────────────────────────────────────────────────────
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
            // And a ring no longer costs the rim's worth of donors either — it carries slots for its own
            // perimeter, and the cross-section narrows toward the toes. Budgeting as though every ring
            // were full width is what held the ring count down and left the cells twice as long along
            // the foot as they are around it.
            int ringCost = Math.Max(1, (int)(rimCount * RingWidthEstimate));
            int affordable = (int)(core.Count * DonorBudget - gridCost) / ringCost - TipRings - 1;
            int ringCount = Math.Clamp((int)MathF.Round(span / MathF.Max(edgeLen * RingDensity, 1e-6f)),
                                       MinRings, Math.Clamp(affordable, MinRings, MaxRings));
            float ringStep = span / ringCount;

            // Where each slot sits ANGULARLY. The rim's vertices are far from evenly spaced — on this
            // foot they range from 0.12 to 0.46 radians apart — so a ring of evenly spaced slots skews
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

            // The rim follows the painted mask edge and is nowhere near flat — here it juts forward over
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
                // narrows toward the toes, so the slots bunched — measured at about 60% of an edge
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
                // floats the cap above the toe by that chord's sagitta — further over the second toe, whose
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
                // they keep the position the abandoned ring gave them while belonging to no ring at all —
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
            // if it is the ring that gets lost — to a thin cross-section or an exhausted vertex pool — the
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
                // as the radius falls away — the last of them sits at 38% of the radius, so its slots end
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
                // number of slots can be sampled from the SHAPE. Decimating it instead — taking every
                // n-th vertex — keeps whichever bumps happen to fall on the surviving slots and drops
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

            // ── the relax ──────────────────────────────────────────────────────────────────────────
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

                        // Hold the vertex in its own ring's plane — a hand pass moves these more than
                        // three times as far across the section as along the foot, and letting them drift
                        // axially walks the last rings back off the toe tips.
                        // Held in its own ring's plane, but otherwise free to settle wherever the
                        // smoothing takes it — including inward, and including down into a toe gap. The
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
                            // Held only PARTLY. Clamped hard the relax converges lumpy — measured at 22%
                            // of an edge and unchanged by four times the passes — because a bump along a
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

                        // Free to settle inward — down into a toe gap is fine, and reads better than a
                        // flat bridge — but never through the skin.
                        float side = Clearance(n, rel, out var onSkin, out var outward);
                        if (side < minClear && side != float.MaxValue)
                            rel = new Vec3(
                                onSkin.X + outward.X * minClear,
                                onSkin.Y + outward.Y * minClear,
                                onSkin.Z + outward.Z * minClear);
                        // ...and never floating far above it either. Smoothing a surface removes its
                        // concavities, so a vertex sitting down on a toe is lifted toward its neighbours
                        // out over the gaps either side — which is what stood the fabric off the big and
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
                        // two toes it can close a pair to almost nothing — the worst face in the cap came
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

            // ── keep it off the skin ───────────────────────────────────────────────────────────────
            // The relax already holds this against each vertex's shortlist; this last sweep checks the
            // whole surface, in case settling carried a vertex over some triangle that was not on its
            // list. Measured against TRIANGLES, not vertices — a vertex-only test lets the cap sink
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

            // Winding comes from the ring order and stays consistent — never flipped per triangle. The
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
            // through the cap — those are the slivers left poking out of the seam.
            void EmitQuad(int a0, int a1, int b1, int b0)
            {
                float d1 = MathF.Min(FacesOut(a0, a1, b1), FacesOut(a0, b1, b0));
                float d2 = MathF.Min(FacesOut(a0, a1, b0), FacesOut(a1, b1, b0));
                if (d1 >= d2) { Emit(a0, a1, b1); Emit(a0, b1, b0); }
                else          { Emit(a0, a1, b0); Emit(a1, b1, b0); }
            }

            // ── the stitch ─────────────────────────────────────────────────────────────────────────
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
                    // outer vertices to one inner vertex and fanned it — max valence 28 the one time ring
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

            // ── smooth where the cap meets the foot ────────────────────────────────────────────────
            // The rings nearest the rim take their slot ANGLES from the rim's own, and only reach even
            // spacing at the far end of the cap (the blend in BuildRing runs on r/ringCount). The cut
            // boundary follows mesh edges diagonally, so it is denser and less even than the mesh — and
            // the first rings inherit that, leaving pinched cells over the top of the toes where they
            // join the foot: faces with a short edge a fifth of the mesh's own.
            //
            // The main relax ran before the stitch and holds every vertex in its own ring's plane. This
            // is the pass a modeller would make by hand instead: relax the join, in place, over the few
            // rings either side of it, against the triangles actually emitted. The rim itself never
            // moves — it is shared with the untouched shell, and moving it tears the seam.
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

            // ── close the end with a grid ──────────────────────────────────────────────────────────
            // Not a fan to a single apex: that makes a pole, where every vertex of the last ring meets at
            // one point, and it shades badly however carefully the triangles are shaped. Instead the
            // opening is filled the way a modeller would — an even quad grid spanning it, four sides
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
                // The patch sits on the LAST ring the cap actually has — which is a dome ring, not the last
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
                        // skin here — otherwise the patch closing the end is the one part of the cap
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

                    // ── smooth the end ─────────────────────────────────────────────────────────
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
                    // what left spikes at the tip — one with an edge three times the mesh's own, beside
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

        // ── stop the skin bulging through the cap ──────────────────────────────────────────────────
        // Clearance has been enforced one way only: every cap vertex is pushed off the nearest skin
        // triangle. Nothing tested the reverse, and a convex toe pad comes through the MIDDLE of a flat
        // cap triangle while all three corners sit comfortably clear — the shape of the underside of a
        // toe, and where this showed worst: 57 of 407 skin vertices under the toes outside the shell,
        // the worst by 0.0029 against a 0.005 edge.
        //
        // So walk the skin instead, and lift the cap where it passes under a vertex. The lift each
        // corner needs is the LARGEST any skin vertex asks of it, applied once — summing every request
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
                    // spanning the gap — lifting that along this normal drives the bridge into the toe
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

        // ── even out the pinched cells ─────────────────────────────────────────────────────────────
        // What is left are pinches: two ring slots that came to rest a tenth of an edge apart, with a
        // long third edge. Ring slots sit at around 60% of the mesh's own edge length — oversampled —
        // and follow the rim's uneven angles, so now and then two land on top of each other.
        //
        // Smoothing them by POSITION does not work and was measured not to: a pinched pair has nearly
        // the same neighbourhood, so the average pulls both the same way and the short edge survives,
        // while the vertices sink toward the skin (clearance went negative). This slides them along the
        // surface instead — the Laplacian with its normal component removed. Spacing evens out, the
        // silhouette does not move, and nothing can descend into a toe, which is what went wrong with
        // every positional attempt at this.
        //
        // Prototyped on the shell the game actually builds before being written here: faces over aspect
        // 8 fall from 8 to 1, the worst from 11.4 to 8.6. It plateaus there — no smoothing separates a
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

            // The surviving shell around the cap joins the graph too — without it a cap vertex on the
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
                        // pairs left in the cap were made here — traced back to adjacent slots of one
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

        // ── UVs for the rebuilt surface ────────────────────────────────────────────────────────────
        // Every cap vertex is a vertex REUSED from somewhere else in the toe box, and it still carries
        // that donor's texture coordinate. Left alone, the cap samples the skin's texture — and its
        // alpha — from wherever each vertex happened to come from: measured on the equipped body, 494
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

        // A mesh may have nothing to cap and still have islands to drop — the toenail mesh is exactly
        // that: every island on it is swallowed whole, so no cap is ever built for it.
        bool anyDropped = false;
        foreach (bool d in dropNode) if (d) { anyDropped = true; break; }
        // An empty NewTriangles is a failure only when geometry was meant to be built. On the authored
        // path it is the expected outcome — the cap is a modelled mesh, so all this pass contributes is
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
        // exactly as they were — that is what keeps an untouched shell byte-identical.
        for (int n = 0; n < nodeCount; n++)
            if (!hasTarget[n]) nW[n] = 0f;

        return new ToeCapPlan
        {
            Delta = delta, NodeOf = nodeOf, NodeWeight = nW, NodeNormal = nNorm, DropNode = dropNode,
            NodeUV = nodeUV,
            CutNode = cutNode, NewTriangles = newTris,
        };
    }

    // ── bust bridge ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The two base-skeleton breast bones. Present on every body a shell can be cut from — vanilla,
    /// Bibo+, gen3 and their descendants all rig to the game's own skeleton — which is what lets the bust
    /// region be found with no painted map and no per-body table.
    /// </summary>
    private const string BustBoneL = "j_mune_l", BustBoneR = "j_mune_r";

    /// <summary>
    /// The base-skeleton hip bone. One bone, on the midline, and its influence covers the buttocks, the
    /// cleft between them, AND the crotch on the other side of the body — so unlike the bust it cannot
    /// seed a region on its own. See <see cref="Facing"/>.
    /// <para/>
    /// The thighs (<c>j_asi_a_l</c>/<c>j_asi_a_r</c>) were the obvious alternative, being a symmetric pair
    /// like the breast bones, and they are not usable for the cleft: measured on a real body they reach
    /// the buttock band with about a fifth of the hip's weight (80 against 678 in total), because the
    /// lobes either side of the cleft are hip-weighted and the thigh bones are for the legs below them.
    /// </summary>
    private const string HipBone = "j_kosi";

    /// <summary>
    /// The two thigh roots. Not a seed — the cleft's lobes are hip-weighted, and measured on a real body
    /// these reach the buttock band with about a fifth of the hip's weight. They are the RIVAL: where they
    /// outweigh the hip the vertex is on a leg, and the surface either side of the midline there is two
    /// legs rather than two cheeks. See <c>MeshRegionWeights</c>'s losesTo.
    /// </summary>
    private const string ThighBoneL = "j_asi_a_l", ThighBoneR = "j_asi_a_r";

    /// <summary>
    /// How close to the midpoint between a band's two apexes the surface has to come before that band
    /// counts as a cleft rather than a gap, as a fraction of the apexes' own separation. See the check in
    /// <c>ChordTarget</c>.
    /// <para/>
    /// Generous on purpose: it is separating "there is a floor here" from "there is nothing here at all",
    /// not measuring how deep the floor is. A quarter of the way in from either side leaves plenty of room
    /// for a cleavage whose sternum sits well off-centre.
    /// </summary>
    private const float ChordMidlineGap = 0.25f;


    /// <summary>
    /// Two solves over one mesh, as a single plan. The chest and the cleft are separate passes — opposite
    /// sides of the body, each with its own axis — but the writer applies ONE plan per mesh.
    /// <para/>
    /// Safe to add because both weld the same positions with <see cref="WeldByPosition"/>, so their node
    /// numbering is identical, and because both only ever raise along their own outward axis: the regions
    /// do not overlap, and if a body ever put them within reach of each other the sum is still the two
    /// displacements the pair asked for rather than one silently replacing the other.
    /// </summary>
    private static BustBridgePlan? MergePlans(BustBridgePlan? a, BustBridgePlan? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        if (a.Delta.Length != b.Delta.Length || a.NodeWeight.Length != b.NodeWeight.Length) return a;

        var delta = new Vec3[a.Delta.Length];
        for (int i = 0; i < delta.Length; i++)
            delta[i] = new Vec3(a.Delta[i].X + b.Delta[i].X,
                                a.Delta[i].Y + b.Delta[i].Y,
                                a.Delta[i].Z + b.Delta[i].Z);

        // Reshading is gated on this, so a node either pass touched has to report non-zero or it keeps a
        // normal describing the surface it no longer has.
        var weight = new float[a.NodeWeight.Length];
        for (int n = 0; n < weight.Length; n++) weight[n] = MathF.Max(a.NodeWeight[n], b.NodeWeight[n]);

        return new BustBridgePlan
        {
            Delta = delta, NodeOf = a.NodeOf, NodeWeight = weight, NodeNormal = a.NodeNormal,
        };
    }

    /// <summary>
    /// Zero the seed wherever the surface does not face backwards. Needed because <see cref="HipBone"/>
    /// covers the front and the back of the body at the same height: without this the cleft pass would
    /// take the crotch with it and span across both at once.
    /// <para/>
    /// Read off each vertex's own normal against model +Z, which is forward for every body a shell is cut
    /// from — verified on this geometry, where the crotch sits at z &gt; 0 and the buttocks at z &lt; 0.
    /// Deliberately a LOCAL test rather than a plane through the body: the surface curves round the hip,
    /// so any single dividing plane either clips the cheeks or admits the thigh.
    /// <para/>
    /// Back only, because that is the only direction anything asks for. A front-facing twin belongs with
    /// the pass that needs it rather than here in advance of one.
    /// </summary>
    private static void GateToBackFacing(float[] w, Vec3[] nrm)
    {
        // A band around edge-on is dropped rather than assigned to a side: a vertex on the hip's flank
        // belongs to neither feature, and handing it to one makes that region's boundary run through a
        // place where the surface is still turning.
        //
        // FADED across that band, not cut at it. The caller passes this on as the region's ramp, and a
        // step there is a step in the displacement: the span reaches its full height on one vertex and
        // nothing on its neighbour, which the slope limit can only soften to BustMaxSlope — a 39° kink,
        // and a kink in a garment reads as a seam. Same defect as the crotch fold's, whose region ended
        // at a hard ceiling and drew a line straight across the front.
        const float Edge = 0.15f;
        for (int i = 0; i < w.Length && i < nrm.Length; i++)
        {
            if (w[i] <= 0f) continue;
            w[i] *= Smoothstep(Math.Clamp((-nrm[i].Z - Edge) / Edge, 0f, 1f));
        }
    }

    /// <summary>
    /// How many bands past the last one with a cleft of its own a borrowed chord survives, fading to
    /// nothing across them. Three bands is a few millimetres of body — long enough that the span dies
    /// gradually, short enough that it cannot carry the buttocks' chord up onto the waist.
    /// </summary>
    private const float ChordBorrowBands = 3f;

    /// <summary>
    /// Passes of Jacobi smoothing over a caller-supplied region ramp. A diffusion spreads about the square
    /// root of its pass count in rings, so sixteen reaches four rings — roughly a centimetre on a body mesh,
    /// which is the distance over which a few millimetres of displacement has to die to stop reading as a
    /// line. Costs a little strength at the region's rim, which is the rim's job.
    /// </summary>
    private const int RampSmoothPasses = 16;

    /// <summary>
    /// Fewest welded nodes the region needs before a bridge is attempted. A handful of stray bust-weighted
    /// vertices — the top of a mesh that mostly holds the arms — describe no cleavage to span.
    /// </summary>
    private const int MinBustBridgeNodes = 24;

    /// <summary>
    /// Movement below which a bust-bridge node counts as untouched, in model units — a thousandth of the
    /// shell's own <see cref="BaseOffset"/>, so comfortably under anything that could be seen. It decides
    /// which nodes are reported as moved and which keep their original normal bytes.
    /// </summary>
    private const float BustBridgeEpsilon = 1e-6f;

    /// <summary>
    /// How much longer than the shortest crossing a path between the two bust lobes may be and still count
    /// as "between" them. In edges, so it scales with a body's own mesh density rather than with any
    /// distance measured in model units.
    /// <para/>
    /// The sternum is not equally narrow at every height — the crossing at the apexes is shorter than the
    /// one near the collarbone — so this has to be more than a step or two or the fill is a single band
    /// across the middle rather than the whole cleavage.
    /// </summary>
    private const int BustGapSlack = 8;

    /// <summary>
    /// Ceiling on how far the gap search will walk from a lobe. Bounds the breadth-first sweep on a mesh
    /// whose lobes are not actually facing each other; the <see cref="BustGapSlack"/> test does the real
    /// selecting.
    /// </summary>
    private const int BustGapMaxSteps = 64;

    /// <summary>
    /// How many rings of the region's outer edge the effect ramps over, so the bridge rejoins the
    /// untouched shell without a crease. In edges, for the same reason as <see cref="BustGapSlack"/>.
    /// <para/>
    /// ONE, not the four this started with, because <see cref="BustMaxSlope"/> now does this job properly
    /// and does it in the mesh's own units. Counting rings cannot know how far a ring IS: on a bralette,
    /// whose region is a narrow band, four rings ate a third of the span (0.055 asked, 0.037 left) purely
    /// because the region was small — the fade was set by how much cloth there was rather than by how
    /// steeply the surface may bend. The slope limit is the same guarantee expressed as geometry, so this
    /// is left at one ring only to take the hard 0-to-1 step off the outermost vertices.
    /// </summary>
    private const int BustFadeSteps = 1;


    /// <summary>
    /// How many [1,2,1] passes the per-band apex line is smoothed with before the chords are drawn between
    /// the two of them. Enough to take the mesh-sampling wobble out of it, few enough that the apex line
    /// still follows the breast it was measured from.
    /// </summary>
    private const int BustBandSmoothing = 3;

    /// <summary>
    /// Steepest the bridge's displacement may change between two vertices, as a ratio to the distance
    /// between them — about 39° of tilt away from the shell it grows out of.
    /// <para/>
    /// This is what bounds the fade in mesh terms rather than in vertex counts. A garment cannot lift a
    /// centimetre three vertices from its own edge without tearing, however the coverage map happens to
    /// have cut it — and the first build in game tore exactly there, at a slope around 10.
    /// <para/>
    /// Chosen by measuring. Swept against a real torso with the axis and the fade as they now stand:
    /// <code>
    ///   slope   mean dish left   edges steeper than 1.0
    ///   0.4         0.00591            0
    ///   0.6         0.00494            0
    ///   0.8         0.00440            0
    ///   1.0         0.00419           72
    ///   1.5         0.00412          146
    /// </code>
    /// Above 0.8 the span stops improving — 0.00412 against 0.00440, under 1% of the 0.01101 it started
    /// from — and all that is bought is folds of 45° and steeper. Those are not smoothed by anything
    /// afterwards and read as a jagged notch along a garment's edge, which is how this was found.
    /// <para/>
    /// It was 1.5 first, on a sweep taken before the axis was corrected and while a four-ring ramp was
    /// still eating a third of the span. Both of those made a loose limit look free. Re-measure this
    /// whenever either changes — a safety limit that has quietly become the thing shaping the result is
    /// exactly what it should never be.
    /// </summary>
    private const float BustMaxSlope = 0.8f;

    /// <summary>
    /// How many times the slope limit is swept before giving up. One pass propagates one edge, and a
    /// region is tens of edges across; it normally settles long before this and stops early when it does.
    /// </summary>
    private const int BustSlopePasses = 200;

    /// <summary>
    /// What the bust bridge decided. The same four fields <see cref="RelaxedNormals"/> needs, and nothing
    /// else: this pass moves vertices and never changes topology, so there is no cut, no dropped island
    /// and no reprojected UV — a vertex keeps the coordinate it already had, which is still the right one
    /// because it only slid forward along the chest.
    /// </summary>
    internal sealed class BustBridgePlan
    {
        /// <summary>Per-vertex displacement, indexed like the mesh's vertices.</summary>
        public required Vec3[] Delta { get; init; }

        /// <summary>Vertex index -> welded node index.</summary>
        public required int[] NodeOf { get; init; }

        /// <summary>Per-node region weight, 0 where the bridge left it alone.</summary>
        public required float[] NodeWeight { get; init; }

        /// <summary>Per-node normalized average of the members' source normals.</summary>
        public required Vec3[] NodeNormal { get; init; }
    }

    /// <summary>
    /// Bust bridge: per-vertex displacement that relaxes cloth across the cleavage, so a garment spans
    /// between the breasts as fabric does instead of sinking into the valley the way a copy of the body
    /// must.
    /// <para/>
    /// The region is measured, not painted: <paramref name="bust"/> is the bust bones' influence per
    /// vertex, already multiplied by this layer's coverage so only cloth moves. Where a neckline has cut
    /// the cloth away between the cups there is nothing to relax and the pass correctly does nothing.
    /// <para/>
    /// The surface is treated as a height field along the chest's outward axis, and every node is lifted to
    /// the straight line between the furthest-forward point of each breast in its own horizontal row — the
    /// chord, taken directly rather than converged toward. <see cref="ChordTarget"/> holds the construction
    /// and the record of the three relaxations that were tried before it and why each failed.
    /// <para/>
    /// Because a node is only ever lifted, never lowered, no vertex can move into the body; because the
    /// chord's endpoints are the apexes themselves, the breasts keep their shape exactly. Not clipping the
    /// breasts and spanning between them flat are the same construction, not two constraints traded off
    /// against each other.
    /// <para/>
    /// Vertices are WELDED by position first, for the reason the cap welds: a body mesh splits vertices at
    /// UV seams and the sternum carries one, so two coincident copies relaxing on their own neighbour sets
    /// would drift apart and crack the shell open down the middle.
    /// <para/>
    /// Returns null when the region is too small to describe a cleavage — the caller then writes exactly
    /// what it would have without this feature.
    /// </summary>
    /// <param name="bust">Per-vertex region weight in 0..1, already gated on coverage.</param>
    /// <param name="ramp">
    /// Optional per-vertex 0..1 multiplied into the finished region weight — a geometric feather the
    /// caller supplies on top of the one the coverage boundary gets.
    /// <para/>
    /// It exists because <paramref name="bust"/> is read as a BOOLEAN seed: any positive value marks a
    /// node and <see cref="BustRegionWeights"/> then builds its own ramp, one ring wide. One ring is right
    /// for a coverage edge, where the shell is pinned to cloth that must not move at all, and wrong for a
    /// region whose edge is a line drawn through open skin — there the displacement has to die over
    /// centimetres or the boundary reads as a crease. Measured on the crotch fold: a 4.8mm lift ending at
    /// the region's ceiling, clipped by <see cref="BustMaxSlope"/> to die in 6mm, which is a 39° kink and
    /// showed in game as a break straight across the front.
    /// </param>
    /// <param name="relaxSeed">
    /// Optional per-vertex 0..1 region for the finishing 3-D relax, separate from the height field's own.
    /// <para/>
    /// Separate because the two operators can work where the other cannot. A height field along one axis
    /// has no leverage on surface that faces across it — under the crotch the skin turns to face DOWN, and
    /// pushing it along +Z slides it sideways rather than smoothing it — while a Laplacian is indifferent
    /// to which way a surface points and simply has no opinion about shape. So the span is gated to the
    /// front and the relax is allowed to wrap underneath.
    /// </param>
    internal static BustBridgePlan? BustBridgeSolve(
        Vec3[] pos, Vec3[] nrm, ushort[] tris, float[] bust, float strength,
        Action<string>? log = null, bool[]? covered = null, float smoothStrength = 0f,
        bool fillGap = true, Vec3? outward = null, bool envelope = false,
        float[]? ramp = null, float[]? relaxSeed = null, bool pinBoundary = false)
        => BustBridgeSolve(pos, nrm, Array.ConvertAll(tris, t => (int)t), bust, strength, log, covered,
                           smoothStrength, fillGap, outward, envelope, ramp, relaxSeed, pinBoundary);

    /// <inheritdoc cref="BustBridgeSolve(Vec3[], Vec3[], ushort[], float[], float, Action{string}, bool[])"/>
    /// <remarks>
    /// Int indices, because a caller working on the WHOLE body rather than one mesh — the standoff map
    /// does — concatenates every skin mesh and runs past what a ushort can address.
    /// </remarks>
    /// <param name="pinBoundary">
    /// Hold every vertex on an open boundary — the rim of a hole — exactly where it is.
    /// <para/>
    /// FOR THE BODY PASSES ONLY, and the scoping is the whole of the design. On a body an open edge is
    /// somewhere another surface has to meet: the waist ring the legs model shares with the torso, or an
    /// authored socket. Nothing on the far side of it is solved here, so moving it tears the join.
    /// <para/>
    /// A SHELL is the opposite case and must NOT pass this. A shell is a cut patch, so its hem is open
    /// edge along its entire perimeter, and pinning that pins the garment itself — which is what the
    /// span's own fixture demonstrates: a finite grid whose whole border is boundary, where turning this
    /// on left the chord completely undisplaced (0.088 off a valley 0.088 deep, i.e. nothing moved). A
    /// shell's hem is already held by <paramref name="covered"/>, which is the right tool for it, because
    /// a hem is pinned where it LIES ON the body rather than because it is an edge.
    /// </param>
    internal static BustBridgePlan? BustBridgeSolve(
        Vec3[] pos, Vec3[] nrm, int[] tris, float[] bust, float strength,
        Action<string>? log = null, bool[]? covered = null, float smoothStrength = 0f,
        bool fillGap = true, Vec3? outward = null, bool envelope = false,
        float[]? ramp = null, float[]? relaxSeed = null, bool pinBoundary = false)
    {
        int vc = pos.Length;
        if (vc == 0 || (strength <= 0f && smoothStrength <= 0f) || bust.Length < vc) return null;

        var nodeOf = WeldByPosition(pos, out int nodeCount);

        // THE RIM OF A HOLE NEVER MOVES.
        //
        // An edge used by exactly one triangle is a boundary: the surface simply stops there. Welding
        // cannot help — there is nothing on the other side of it to weld TO, in this mesh. Two different
        // things are on the far side of such an edge and both break if it moves:
        //
        // The next PART. A body arrives as several models (torso, legs, arms, feet), each parsed and
        // solved on its own, so WeldByPosition never sees across the join. Torso and legs share a
        // 54-point ring at the waist, and it is coincident today only because nothing has moved it.
        //
        // Or an AUTHORED HOLE, whose far side is not geometry we own at all. Neolithe's legs carry an
        // 8-edge socket at the crotch where the genital mesh plugs in — a gap of about 3mm that normally
        // sits hidden between the legs. Measured on that body, the crotch fold moved 14 rim vertices by
        // up to 4.97mm and collapsed 8 triangles into slivers, prising the socket open into a visible
        // gash. The relax was working correctly: evening out vertex spacing is what a relax does, and
        // nothing told it that some of those vertices were the lip of a hole.
        //
        // Fed in through `cut`, NOT zeroed afterwards, for exactly the reason `cut` documents below:
        // subtracting after the ramp is built leaves full-weight nodes sitting beside zeroed ones, which
        // is what turned a garment's edge into a row of centimetre-high spikes. Excluded up front, the
        // ramp runs down to the rim properly.
        //
        // One caveat, and it is deliberate: the gap fill between two lobes ignores exclusions (see
        // BustRegionWeights), so a rim node lying inside the sternum gap can still be pulled back into
        // the region. That gap is the cleavage, which no body boundary crosses.
        bool[]? rimNode = null;
        if (pinBoundary)
        {
            var use = new Dictionary<(int, int), int>();
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;
                int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
                Count(a, b); Count(b, c); Count(c, a);
            }
            foreach (var (e, n) in use)
            {
                if (n != 1) continue;
                rimNode ??= new bool[nodeCount];
                rimNode[e.Item1] = true;
                rimNode[e.Item2] = true;
            }

            void Count(int a, int b)
            {
                if (a == b) return;
                var k = a < b ? (a, b) : (b, a);
                use[k] = use.TryGetValue(k, out int c) ? c + 1 : 1;
            }
        }

        var start = new Vec3[nodeCount];
        var nNorm = new Vec3[nodeCount];
        var members = new int[nodeCount];
        // The SEED — where the bust bones say a breast is, on cloth this layer actually paints. It only
        // has to find the two lobes; BustRegionWeights turns it into the region that may move.
        var seed = new bool[nodeCount];
        // Uncovered cloth pins the region at the garment's own edge, so a node is seeded only if EVERY
        // welded copy of it is painted. Any copy being cut away means the boundary runs through here.
        var cut = new bool[nodeCount];
        // The bust WITHOUT the coverage gate. Where a breast is, is a fact about the body; what a garment
        // paints is a fact about the garment, and the nipple smooth needs the first to locate itself.
        var onBust = new bool[nodeCount];
        for (int i = 0; i < vc; i++)
        {
            int n = nodeOf[i];
            start[n] = new Vec3(start[n].X + pos[i].X, start[n].Y + pos[i].Y, start[n].Z + pos[i].Z);
            nNorm[n] = new Vec3(nNorm[n].X + nrm[i].X, nNorm[n].Y + nrm[i].Y, nNorm[n].Z + nrm[i].Z);
            if (bust[i] > 0f) { seed[n] = true; onBust[n] = true; }
            if (covered != null && i < covered.Length && !covered[i]) cut[n] = true;
            members[n]++;
        }
        int seeded = 0, pinned = 0;
        for (int n = 0; n < nodeCount; n++)
        {
            float inv = 1f / members[n];
            start[n] = new Vec3(start[n].X * inv, start[n].Y * inv, start[n].Z * inv);
            nNorm[n] = Normalize(nNorm[n]) ?? default;
            // Counted only where it BITES — a rim node the region never reached is not a pin, and
            // reporting every boundary in the mesh would bury the ones that mattered.
            if (rimNode != null && rimNode[n]) { if (seed[n]) pinned++; cut[n] = true; }
            if (cut[n]) seed[n] = false;
            if (seed[n]) seeded++;
        }
        if (pinned > 0)
            log?.Invoke($"bust bridge: {pinned} seeded node(s) pinned for sitting on the rim of a hole");
        if (seeded < MinBustBridgeNodes)
        {
            if (seeded > 0)
                log?.Invoke($"bust bridge: SKIPPED, {seeded} bust node(s) is under "
                          + $"MinBustBridgeNodes ({MinBustBridgeNodes})");
            return null;
        }

        // Edge adjacency over the welded nodes, deduped — a shared edge would otherwise pull twice and
        // bias the plane fit toward whichever neighbour happens to be used by more triangles.
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
        for (int n = 0; n < nodeCount; n++) adj[n] ??= new List<int>();

        // The gap between the lobes is where the whole feature happens, and the bones do not reach it.
        // Cloth the layer does not paint is excluded from the region OUTRIGHT — handed in, not subtracted
        // afterwards. Subtracting it afterwards is what tore the shell in game: the fade ramp had already
        // been computed over a region that still contained the unpainted vertices, so zeroing them left
        // full-weight vertices sitting directly beside zeroed ones. Adjacent vertices then differed by the
        // whole displacement instead of by a fifth of it, and the garment's edge came out as a row of
        // centimetre-high spikes. Excluded first, the ramp runs down to the garment's own edge properly.
        var nW = BustRegionWeights(seed, cut, adj, nodeCount, log, fillGap);

        // The caller's own feather, welded to nodes the same way the geometry was. Averaged over the
        // welded copies rather than maxed: a node is one place on the surface, and its copies differ only
        // by which UV chart they belong to, so they cannot honestly disagree about how far into the region
        // it sits.
        var relaxW = new float[nodeCount];
        if (ramp != null || relaxSeed != null)
        {
            var rampN = new float[nodeCount];
            for (int i = 0; i < vc && i < pos.Length; i++)
            {
                int n = nodeOf[i];
                if (ramp != null && i < ramp.Length) rampN[n] += ramp[i];
                if (relaxSeed != null && i < relaxSeed.Length) relaxW[n] += relaxSeed[i];
            }
            for (int n = 0; n < nodeCount; n++)
            {
                float inv = 1f / members[n];
                relaxW[n] *= inv;
                rampN[n] *= inv;
            }

            // SMOOTHED over the mesh before it is used, because a caller's ramp can be RAGGED even when
            // the quantity behind it is smooth. The cleft hands over bone weights, and skinning weights
            // are quantized: a vertex either carries j_kosi among its four influences or it carries none
            // of it, so at the waist one vertex holds 0.02 and its neighbour holds nothing at all. Used
            // raw that is not a taper, it is a jagged edge — and a jagged edge in the displacement draws a
            // BROKEN line across the small of the back rather than a continuous one, which is exactly how
            // it looked in game.
            //
            // Jacobi over the welded graph, reading neighbours regardless of their own weight so the
            // outside pulls the boundary down to nothing instead of holding it up.
            if (ramp != null && RampSmoothPasses > 0)
            {
                var next = new float[nodeCount];
                for (int pass = 0; pass < RampSmoothPasses; pass++)
                {
                    for (int n = 0; n < nodeCount; n++)
                    {
                        if (adj[n] is not { Count: > 0 } near) { next[n] = rampN[n]; continue; }
                        float s = 0f;
                        foreach (int k in near) s += rampN[k];
                        next[n] = rampN[n] + (s / near.Count - rampN[n]) * 0.5f;
                    }
                    (rampN, next) = (next, rampN);
                }
            }

            if (ramp != null)
                for (int n = 0; n < nodeCount; n++) nW[n] *= rampN[n];
        }

        // The rim again, on the OTHER channel. Pinning it out of the seed above is not enough: the relax
        // does not run on the region, it runs on every node with relaxW > 0, so a boundary node kept its
        // full share of the Laplacian and moved anyway. Measured on the crotch socket, pinning the seed
        // alone took the worst rim move from 4.97mm to 4.57mm — which is to say it did nothing.
        //
        // Zeroed here rather than skipped at the loop, so the rim keeps behaving the way every other node
        // outside the relax region already does: READ by its neighbours, never written. That is the
        // mechanism the relax already relies on to avoid stepping at its own edge, so the surface still
        // smooths right up to the hole; only the lip of it stays put.
        //
        // Deliberately after the ramp smoothing, not folded into the averaging loop above: relaxW is not
        // smoothed today, and a pin that a later Jacobi pass could bleed back into is not a pin.
        //
        // FADED OVER SEVERAL RINGS, not zeroed at the rim alone. Zeroing only the boundary nodes is the
        // same mistake this file records twice elsewhere — a full-weight node sitting directly beside a
        // pinned one, so the whole displacement appears across a single edge. Measured on the crotch
        // socket that turned the fold from a flattener into a lip: the cross-section came out 9.9% ROUGHER
        // than the untouched body, worst in exactly the three bands the socket passes through.
        //
        // So the pin gets a skirt. Graph distance out from the rim, smoothstepped, which leaves the rim
        // exactly where it is and lets the relax come back up to full strength a few rings away.
        if (rimNode != null)
        {
            var ring = new int[nodeCount];
            Array.Fill(ring, -1);
            var q0 = new Queue<int>();
            for (int n = 0; n < nodeCount; n++) if (rimNode[n]) { ring[n] = 0; q0.Enqueue(n); }
            while (q0.Count > 0)
            {
                int q = q0.Dequeue();
                if (ring[q] >= RimPinFade || adj[q] == null) continue;
                foreach (int k in adj[q])
                    if (ring[k] < 0) { ring[k] = ring[q] + 1; q0.Enqueue(k); }
            }
            for (int n = 0; n < nodeCount; n++)
            {
                if (ring[n] < 0) continue;                       // beyond the skirt, untouched
                relaxW[n] *= Smoothstep(ring[n] / (float)RimPinFade);
            }
        }

        int region = 0;
        for (int n = 0; n < nodeCount; n++) if (nW[n] > 0f) region++;
        if (region < MinBustBridgeNodes)
        {
            log?.Invoke($"bust bridge: SKIPPED, {region} region node(s) survive the coverage gate");
            return null;
        }

        // A rough outward direction to get started — the region's own weighted mean normal. Only used to
        // find the other two axes and to settle the final one's sign; it is NOT the direction anything
        // moves in. See below for why that distinction is the difference between a span and a mess.
        //
        // A CALLER MAY SUPPLY IT, because the mean normal is only outward while the region is a surface
        // facing outward. Inside a crease it is not: measured across the crotch fold the mean normal's
        // forward component is 0.001, the walls facing each other across ±X instead, and the derivation
        // built on it returned an axis of (-1,0,0) — sideways. Everything downstream was then solving a
        // different problem competently. A region that cannot say which way is out has to be told.
        var seedOut = outward ?? Normalize(WeightedMean(nNorm, nW, nodeCount));
        if (seedOut is not { } outSeed)
        {
            log?.Invoke("bust bridge: SKIPPED, the region's normals cancel out — no outward axis");
            return null;
        }

        // The direction the span runs in — lobe to lobe. Everything is spanned ACROSS this and along
        // nothing else, because a cleft is a saddle and an isotropic relax settles on it unchanged.
        var across = PrincipalAcross(start, nW, nodeCount, outSeed);
        if (across is not { } lateral)
        {
            log?.Invoke("bust bridge: SKIPPED, the region has no principal direction across the chest");
            return null;
        }

        var everywhere = new float[nodeCount];
        Array.Fill(everywhere, 1f);

        // Up the body: the widest spread in the plane across the span direction, measured over the WHOLE
        // MESH rather than over the region.
        //
        // Over the region it is wrong, and not subtly. A bust region is a curved band, so in the
        // (up, depth) plane its points lie on a diagonal and the principal direction follows that diagonal
        // rather than the vertical — 38° off on a whole bust, 11° on a bralette, each one tilting the
        // final axis by the same amount. The mesh it is cut from is a torso: hips to neck, unambiguously
        // taller than it is deep, and its principal direction is the body's own vertical whatever shape
        // the garment on it happens to be.
        var upward = PrincipalAcross(start, everywhere, nodeCount, lateral);
        if (upward is not { } vertical)
        {
            log?.Invoke("bust bridge: SKIPPED, the region has no vertical extent");
            return null;
        }

        // THE TWO CAN COME BACK SWAPPED, and the region's own proportions are what say so. The span
        // direction is taken from the REGION's principal direction, which is only lobe-to-lobe while the
        // region is wider than it is tall. A bust is: two breasts side by side. The buttocks are not —
        // measured on a real body that region runs 330mm from waist to thigh against 150mm across — so the
        // PCA returned the body's vertical, the pass spanned from one HEIGHT to another, both apexes came
        // back on the same cheek 22mm apart in y, and the cleft was left exactly as deep as it was found.
        //
        // Detected against MODEL +Y, which is up on every body a shell can be cut from. That is an
        // assumption, and it is deliberately the only one here — it is the same fact <see cref="Facing"/>
        // already rests on for +Z, and the alternatives were tried and do not work:
        //
        //  - comparing the region's extent along the two axes is tautological. PCA returns the longest
        //    direction BY CONSTRUCTION, so the region is always longer along `lateral` than across it and
        //    the test can never fire.
        //  - the whole mesh's own principal direction is not a vertical either. On a fixture wider than it
        //    is tall it returns the lateral, and a guard built on it swapped the axes on the BUST — eight
        //    tests, every one of them right to fail.
        if (MathF.Abs(lateral.Y) > 0.7f)
        {
            // The honest span direction is then the remaining axis: perpendicular to up and to out.
            var side = Normalize(new Vec3(1f * outSeed.Z - 0f * outSeed.Y,
                                          0f * outSeed.X - 0f * outSeed.Z,
                                          0f * outSeed.Y - 1f * outSeed.X));
            if (side is { } acrossBody)
            {
                log?.Invoke("bust bridge: the region's principal direction is the body's own vertical, so "
                          + "it is taller than it is wide — spanning across the body instead");
                lateral = acrossBody;
                var reUp = PrincipalAcross(start, everywhere, nodeCount, lateral);
                if (reUp is not { } v2)
                {
                    log?.Invoke("bust bridge: SKIPPED, no vertical remains once the axes are swapped");
                    return null;
                }
                vertical = v2;
            }
        }

        // THE DIRECTION CLOTH ACTUALLY MOVES: perpendicular to both, i.e. straight out from the chest.
        //
        // Deliberately not the mean normal, which is what this used at first and which is wrong wherever a
        // garment does not cover the breast symmetrically. A bralette sits on the UPPER slope, so its
        // normals average 21° upward — and lifting along that slides every vertex up the body as well as
        // out. Below the apex the surface rises faster than 21°, so a vertex moving up-and-out ends up
        // INSIDE the breast: the "it's clipping into the breasts underneath" report, from a pass whose
        // whole promise is that it never moves anything inward. It never did — along its own axis. The
        // axis was the bug.
        //
        // Perpendicular to the body's vertical, a chest front is single-valued: moving out along it cannot
        // re-enter the body, which is what makes the promise true against the body rather than against a
        // number.
        var ax = Normalize(new Vec3(lateral.Y * vertical.Z - lateral.Z * vertical.Y,
                                    lateral.Z * vertical.X - lateral.X * vertical.Z,
                                    lateral.X * vertical.Y - lateral.Y * vertical.X)) ?? outSeed;
        if (ax.X * outSeed.X + ax.Y * outSeed.Y + ax.Z * outSeed.Z < 0f)
            ax = new Vec3(-ax.X, -ax.Y, -ax.Z);   // point it out of the body, not into it

        var h0 = new float[nodeCount];
        var lat = new float[nodeCount];
        var ver = new float[nodeCount];
        for (int n = 0; n < nodeCount; n++)
        {
            var p = start[n];
            h0[n] = p.X * ax.X + p.Y * ax.Y + p.Z * ax.Z;
            lat[n] = p.X * lateral.X + p.Y * lateral.Y + p.Z * lateral.Z;
            ver[n] = p.X * vertical.X + p.Y * vertical.Y + p.Z * vertical.Z;
        }

        var h = strength <= 0f ? h0
              : envelope ? EnvelopeTarget(h0, lat, ver, nW, nodeCount, adj, start, log)
                         : ChordTarget(h0, lat, ver, nW, nodeCount, adj, start, log);

        // How far of the way to the chord each node actually goes: the region ramp fades the effect into
        // the untouched shell at the region's edge, and the strength is the user's "how much of this do I
        // want". Both scale the finished displacement rather than the construction, so the span the solve
        // computed stays exactly straight and only how far the cloth travels toward it varies.
        var scale = new float[nodeCount];
        for (int n = 0; n < nodeCount; n++) scale[n] = (h[n] - h0[n]) * nW[n] * strength;

        // What the CONSTRUCTION asked for, before the ramp and the slope limit trim it, so the report can
        // say which of the three actually decided the result. Without this a span that came out shallow
        // looks identical whether the chord was shallow, the region ramp ate it, or the slope limit did —
        // and they need completely different fixes.
        float wantedMax = 0f, rampedMax = 0f;
        for (int n = 0; n < nodeCount; n++)
        {
            wantedMax = MathF.Max(wantedMax, h[n] - h0[n]);
            rampedMax = MathF.Max(rampedMax, MathF.Abs(scale[n]));
        }

        // SLOPE LIMIT — the guarantee that the shell cannot tear, whatever shape the region came out.
        //
        // Everything above decides how far each vertex should travel; nothing above bounds how much that
        // can differ between two vertices joined by an edge, and a garment lifting a centimetre where its
        // neighbour lifts nothing is a spike, not a span. The region ramp is meant to prevent that and
        // depends on the region's boundary being smooth, which the coverage map does not promise.
        //
        // Each node is pulled toward its neighbour's displacement until the difference is no more than the
        // edge between them can absorb, repeated until it settles because one pass only propagates one
        // edge. This is what makes the fade a property of the mesh rather than of a step count guessed
        // against one body.
        //
        // TWO-SIDED, because the span only ever raises but the nipple smooth may lower: a limit that
        // looked at positive displacements alone would leave the rim of the smoothed patch unbounded.
        //
        // It only ever moves a node TOWARD zero. That restriction is what makes the sweep safe rather than
        // merely symmetric — a plain "clamp into the neighbour's window" also pulls an untouched node UP
        // to meet a displaced one, which invents displacement where the region deliberately has none,
        // unpins the boundary, and tears the shell. Measured that way at a slope of 3.45 on a ragged
        // coverage edge, against a limit of 0.8.
        void LimitSlope(float[] v)
        {
            for (int pass = 0; pass < BustSlopePasses; pass++)
            {
                float worst = 0f;
                for (int n = 0; n < nodeCount; n++)
                {
                    if (v[n] == 0f) continue;
                    foreach (int k in adj[n])
                    {
                        float dx = start[k].X - start[n].X, dy = start[k].Y - start[n].Y, dz = start[k].Z - start[n].Z;
                        float room = BustMaxSlope * MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (v[n] > 0f)
                        {
                            float cap = MathF.Max(0f, v[k] + room);
                            if (cap >= v[n]) continue;
                            worst = MathF.Max(worst, v[n] - cap);
                            v[n] = cap;
                        }
                        else
                        {
                            float flo = MathF.Min(0f, v[k] - room);
                            if (flo <= v[n]) continue;
                            worst = MathF.Max(worst, flo - v[n]);
                            v[n] = flo;
                        }
                    }
                }
                if (worst <= BustBridgeEpsilon) break;
            }
        }
        LimitSlope(scale);

        // THE NIPPLE RELAX, on the surface the span left behind, so a garment doing both gets a spanned
        // chest that is then smoothed rather than two constructions arguing over the same vertices.
        //
        // It is a 3-D displacement and the span is not, so it cannot be folded into `scale`. That is the
        // whole point: see NippleSmoothTarget. Slope-limited per component — the sweep only ever moves a
        // value toward zero, so doing it axis by axis is safe — because the falloff is multiplied by the
        // coverage ramp, and a coverage edge cutting through the disc is exactly the ragged boundary the
        // limit exists to absorb.
        Vec3[]? nipple = null;
        if (smoothStrength > 0f)
        {
            var spanned = new Vec3[nodeCount];
            var spannedH = new float[nodeCount];
            for (int n = 0; n < nodeCount; n++)
            {
                spanned[n] = new Vec3(start[n].X + ax.X * scale[n],
                                      start[n].Y + ax.Y * scale[n],
                                      start[n].Z + ax.Z * scale[n]);
                spannedH[n] = h0[n] + scale[n];
            }
            nipple = NippleSmoothTarget(spanned, spannedH, lat, ver, nW, seed, nodeCount, adj, ax,
                                        smoothStrength, log);
            if (nipple != null)
            {
                var comp = new float[nodeCount];
                for (int c = 0; c < 3; c++)
                {
                    for (int n = 0; n < nodeCount; n++)
                        comp[n] = c == 0 ? nipple[n].X : c == 1 ? nipple[n].Y : nipple[n].Z;
                    LimitSlope(comp);
                    for (int n = 0; n < nodeCount; n++)
                        nipple[n] = c == 0 ? new Vec3(comp[n], nipple[n].Y, nipple[n].Z)
                                  : c == 1 ? new Vec3(nipple[n].X, comp[n], nipple[n].Z)
                                           : new Vec3(nipple[n].X, nipple[n].Y, comp[n]);
                }
            }
        }

        // ── THE FINISHING RELAX ─────────────────────────────────────────────────────────────────────
        //
        // A plain Laplacian over the region, run on the surface everything above has already produced. It
        // is the same operator as the nipple's finish and it is here for the second of the two reasons
        // given there: the height field only ever moves a node ALONG one axis, so wherever the surface
        // turns to face across that axis the span has no purchase on it at all. Under the crotch it turns
        // to face down. The span cleaned the front and left the underside exactly as rough as it found it,
        // which is what "the front is smooth now, underneath is still bumpy" looks like from the geometry
        // side.
        //
        // Plain rather than Taubin, for the reason set out at NippleFinishPasses: the negative step that
        // protects shape is the same one that undoes the redistribution this exists for, and it stalls
        // well short.
        //
        // Neighbours OUTSIDE the relax region are read but never written, so they pin the boundary, and
        // relaxW fades the result on top of that. The pass therefore cannot step at its own edge however
        // ragged that edge is.
        if (relaxSeed != null && strength > 0f && FoldRelaxPasses > 0)
        {
            var cur = new Vec3[nodeCount];
            for (int n = 0; n < nodeCount; n++)
            {
                float d = scale[n];
                cur[n] = new Vec3(start[n].X + ax.X * d, start[n].Y + ax.Y * d, start[n].Z + ax.Z * d);
                if (nipple is { } q)
                    cur[n] = new Vec3(cur[n].X + q[n].X, cur[n].Y + q[n].Y, cur[n].Z + q[n].Z);
            }
            var basis = (Vec3[])cur.Clone();
            var next = (Vec3[])cur.Clone();

            var relaxNodes = new List<int>();
            for (int n = 0; n < nodeCount; n++) if (relaxW[n] > 0f && adj[n].Count > 0) relaxNodes.Add(n);

            for (int pass = 0; pass < FoldRelaxPasses; pass++)
            {
                foreach (int n in relaxNodes)
                {
                    var near = adj[n];
                    float sx = 0f, sy = 0f, sz = 0f;
                    foreach (int j in near) { sx += cur[j].X; sy += cur[j].Y; sz += cur[j].Z; }
                    float inv = 1f / near.Count;
                    next[n] = new Vec3(cur[n].X + (sx * inv - cur[n].X) * FoldRelaxLambda,
                                       cur[n].Y + (sy * inv - cur[n].Y) * FoldRelaxLambda,
                                       cur[n].Z + (sz * inv - cur[n].Z) * FoldRelaxLambda);
                }
                foreach (int n in relaxNodes) cur[n] = next[n];
            }

            var free = nipple ?? new Vec3[nodeCount];
            float mostRelax = 0f;
            foreach (int n in relaxNodes)
            {
                float a = relaxW[n] * strength;
                if (a <= 0f) continue;
                var d = new Vec3((cur[n].X - basis[n].X) * a,
                                 (cur[n].Y - basis[n].Y) * a,
                                 (cur[n].Z - basis[n].Z) * a);
                free[n] = new Vec3(free[n].X + d.X, free[n].Y + d.Y, free[n].Z + d.Z);
                mostRelax = MathF.Max(mostRelax, Len(d));
            }

            // Limited as a VECTOR, and tighter than the span's own limit. Both differences are load-bearing
            // and the mesh said so: at BustMaxSlope per component this tore 12 triangles inside out and
            // collapsed 2 more, all of them in the two millimetres just above the crotch.
            //
            // Per component is right for the span because every node there moves along ONE axis, so a
            // slope limit tilts a triangle and can never fold it. This channel moves freely in 3-D, where
            // the same limit applied three times over allows neighbours to differ by root-three times as
            // much, in any direction — including straight through each other. The crotch is the densest
            // part of the body's mesh, with edges around a millimetre, so a limit that permits 1.39mm of
            // difference across a 1mm edge is not a limit at all.
            //
            // The constant then has to be well under a half: a triangle inverts once one corner's
            // displacement exceeds its distance to the opposite edge, and a third of the shortest edge is
            // comfortably inside that for any triangle that is not already degenerate.
            LimitSlopeVector(free, start, adj, nodeCount, FoldRelaxMaxSlope);
            nipple = free;

            log?.Invoke($"bust bridge: relax {FoldRelaxPasses} pass(es) over {relaxNodes.Count} node(s), "
                      + $"moving them by up to {mostRelax:0.#####}");
        }

        var delta = new Vec3[vc];
        int moved = 0;
        float maxMove = 0f;
        // Magnitude, not sign: the span only raises but the nipple relax may lower, and a test on the
        // signed value would drop every vertex the relax moved and then report that nothing happened.
        //
        // The two compose by ADDITION, and they are different kinds of thing — the span is a scalar along
        // one axis, the relax is a free 3-D move. Nothing here reduces one to the other.
        float NodeMove(int n)
        {
            float s = MathF.Abs(scale[n]);
            return nipple is null ? s : s + Len(nipple[n]);
        }
        for (int i = 0; i < vc; i++)
        {
            int n = nodeOf[i];
            float d = scale[n];
            var v = new Vec3(ax.X * d, ax.Y * d, ax.Z * d);
            if (nipple is { } np) v = new Vec3(v.X + np[n].X, v.Y + np[n].Y, v.Z + np[n].Z);
            float mag = Len(v);
            if (mag <= BustBridgeEpsilon) continue;
            delta[i] = v;
        }

        // THE MESH HAS TO STILL BE A MESH — but only the FOLD gets this, and that scoping is not caution,
        // it is a bug fixed.
        //
        // Run over every pass it wrecked the nipple. Flattening a point legitimately shrinks the triangles
        // at the tip, so they trip the area test, their three vertices get halved, that distorts the
        // triangles around them until those trip it too, and eight rounds of the cascade leave a spiked
        // mess where the nipple was — far worse than the point it set out to remove.
        //
        // The tearing it exists for was measured in one place: the crotch, where the span's axis lies
        // along the surface and slides neighbours past each other. That is the pass that needs it.
        if (relaxSeed != null)
            UnfoldTriangles(pos, delta, tris, log);

        foreach (var v in delta) maxMove = MathF.Max(maxMove, Len(v));
        // Counted per NODE, not per vertex, so the number means "how much of the chest moved" rather than
        // how many UV-seam copies the mesh happens to carry.
        for (int n = 0; n < nodeCount; n++) if (NodeMove(n) > BustBridgeEpsilon) moved++;

        if (moved == 0)
        {
            log?.Invoke($"bust bridge: {region} region node(s), nothing moved — the cloth here is already "
                      + "the shape it was asked for");
            return null;
        }

        // Measured on the FINAL heights — after the region ramp and the strength — because that is the
        // surface the shell actually gets, and a report on the unscaled solve would claim a flat span for
        // a bridge the user had turned down to a quarter.
        var hFinal = new float[nodeCount];
        for (int n = 0; n < nodeCount; n++) hFinal[n] = h0[n] + scale[n];
        var chord = ChordReport(start, h0, hFinal, nW, nodeCount, ax, lateral);

        // The finishing relax reaches past the height field's own region — that is the point of it having
        // a separate one — so nodes it alone moved carry nW of zero and would be written at their new
        // positions while still shaded from their old ones. Admit them here, after the report above has
        // measured the span on the weights the span actually used.
        //
        // BY HOW FAR EACH NODE MOVED, not by whether it is in the region. nW is not a gate: RelaxedNormals
        // uses it to blend between the body's AUTHORED normal and one recomputed from the faces, and at 1
        // the authored normal is discarded outright. Admitting the whole region at full weight therefore
        // rewrote the normals of every node in it, including the ones that had barely moved and whose
        // authored normal was still perfectly good.
        //
        // That is not a cosmetic difference, because a shell is built as `position + normal * BaseOffset`.
        // Changing the normal changes which way the garment is pushed, and where the relax region crossed
        // the leg opening the fabric lifted off the skin and opened a gap along its own hem — bright
        // slivers of the cut edge between the trim and the leg, with the hem going wobbly along the line
        // where the blend fell back to zero.
        //
        // Movement measured against the node's own edge length, so it means "how much did the surface
        // here actually change" on any mesh density. A node that moved a good fraction of the distance to
        // its neighbours has a genuinely new surface and needs the new normal; one that moved a hundredth
        // of that keeps what the artist gave it.
        if (relaxSeed != null)
        {
            int full = 0, token = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                if (relaxW[n] <= 0f || adj[n].Count == 0) continue;
                float edge = 0f;
                foreach (int k in adj[n])
                {
                    float dx = start[k].X - start[n].X, dy = start[k].Y - start[n].Y, dz = start[k].Z - start[n].Z;
                    edge += MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                }
                edge /= adj[n].Count;
                if (edge <= 1e-9f) continue;
                float need = Math.Clamp(NodeMove(n) / (edge * NormalReshadeSpan), 0f, 1f);
                nW[n] = MathF.Max(nW[n], relaxW[n] * strength * need);
                if (nW[n] >= 0.5f) full++; else if (nW[n] > 0f) token++;
            }
            log?.Invoke($"bust bridge: {full} node(s) reshaded from the new surface, {token} keeping most "
                      + "of the normal they arrived with");
        }

        // Nodes the bridge never moved report zero weight, so the normal pass leaves their bytes exactly
        // as they were and an untouched shell stays byte-identical. It has to count the relax as well as
        // the span: a node the relax alone moved would otherwise be written at its new position and
        // reshaded from its old one.
        for (int n = 0; n < nodeCount; n++)
            if (NodeMove(n) <= BustBridgeEpsilon) nW[n] = 0f;

        log?.Invoke($"bust bridge: axis ({ax.X:0.###},{ax.Y:0.###},{ax.Z:0.###}), {region} region node(s), "
                  + $"{moved} moved, max {maxMove:0.#####} "
                  + $"(chord asked {wantedMax:0.#####}, ramp left {rampedMax:0.#####}, slope left {maxMove:0.#####})"
                  + chord);

        return new BustBridgePlan
        {
            Delta = delta, NodeOf = nodeOf, NodeWeight = nW, NodeNormal = nNorm,
        };
    }

    /// <summary>
    /// Which vertices this layer actually paints, or null when it paints everything — the gate that makes
    /// the bust region "the cloth over the bust" rather than "the bust".
    /// <para/>
    /// Sampled nearest per vertex, like the toe cap's mask, and deliberately NOT through
    /// <see cref="AnyVisible"/>: that keeps a triangle when any texel under it is lit, so the cloth
    /// survives a little past where a per-vertex test says it is. The difference is the point — the
    /// outermost ring of cloth vertices comes out uncovered and becomes the pinned boundary the relax
    /// solves against, which is exactly the condition a span needs at its edge.
    /// </summary>
    private static bool[]? CoveredVertices((float U, float V)[] uv, SecondSkinLayer layer, int count)
    {
        var mask = layer.Coverage;
        int w = layer.CoverageWidth, h = layer.CoverageHeight;
        if (mask == null || w <= 0 || h <= 0 || mask.Length < w * h) return null;

        var outC = new bool[count];
        int n = Math.Min(count, uv.Length);
        for (int i = 0; i < n; i++)
        {
            int x = ((int)MathF.Floor(uv[i].U * w) % w + w) % w;
            int y = ((int)MathF.Floor(uv[i].V * h) % h + h) % h;
            outC[i] = mask[y * w + x] >= CoverageFloor;
        }
        return outC;
    }

    /// <summary>
    /// The region the bridge is allowed to move: the bust, PLUS the gap between its two lobes, faded to
    /// zero at its outer edge. 1 inside, 0 outside, a ramp in between.
    /// <para/>
    /// The gap fill is not a refinement — without it the feature cannot work at all. Measured on a shipped
    /// Neolithe torso, the bust bones' influence forms exactly TWO components of 1139 nodes each and they
    /// do not meet: the sternum between them carries no bust weight, so 39 of the 59 midline nodes were
    /// pinned and the cleavage — the one place a bridge exists to span — was the one place that could not
    /// move. The relax dutifully reported 667 vertices moved while leaving the dish at 0.01633, exactly as
    /// deep as it found it.
    /// <para/>
    /// The gap is selected by a GEODESIC ELLIPSE: a node counts as between the lobes when its combined
    /// graph distance to both is within <see cref="BustGapSlack"/> of the shortest crossing there is. A
    /// plain dilation would have done as well for the sternum and also swallowed the belly, which lies
    /// directly below both lobes and is reachable from each in a handful of steps; requiring the SUM to be
    /// near-minimal is what distinguishes "between them" from "near both of them".
    /// <para/>
    /// The gap ignores <paramref name="excluded"/>, unlike the seed, and that asymmetry is the point. A
    /// garment's hem is pinned everywhere it lies ON the body — the band under the bust, the edges by the
    /// arms — because there it really is anchored. Between the cups it is not: that hem is the top of the
    /// span itself, and pinning it holds the one edge the whole feature exists to lift. On a bralette that
    /// left the top of the cleavage diving as deep as it started while the surface below it spanned.
    /// So: coverage decides where the bust IS, and the gap between the lobes is part of the region whether
    /// or not anything is painted there.
    /// <para/>
    /// The bone weight is used to FIND the bust and then discarded as a ramp. It is a poor one: on that
    /// same torso the midline nodes that carry any bust weight at all carry 0.008 to 0.035, so scaling the
    /// displacement by it would have cancelled the span even after the gap was unpinned. The ramp instead
    /// comes from graph distance to the region's own edge, which is uniform across bodies and does not
    /// depend on how an author happened to paint weights.
    /// </summary>
    /// <param name="fillGap">
    /// Whether the region grows into the gap between its two largest lobes. TRUE for the bust, where the
    /// two breast bones miss the sternum and that gap IS the cleavage.
    /// <para/>
    /// FALSE for the gluteal cleft, and the difference is a property of the skeleton rather than a
    /// preference. One midline hip bone covers both cheeks and the cleft floor between them, so the seed
    /// arrives already connected and there is no gap to find: measured on a real body it comes to one
    /// component of 1473 nodes against scraps of 64 and 54, at every facing threshold from 0 to 0.75, with
    /// 465 midline nodes already inside it. Left on, the fill picks the largest scrap as the second lobe
    /// and bridges the region to somewhere arbitrary.
    /// </param>
    private static float[] BustRegionWeights(bool[] seed, bool[] excluded, List<int>[] adj, int nodeCount,
                                             Action<string>? log, bool fillGap = true)
    {
        // The lobes: connected components of the seed.
        var comp = new int[nodeCount];
        Array.Fill(comp, -1);
        var sizes = new List<int>();
        var stack = new Stack<int>();
        for (int n = 0; n < nodeCount; n++)
        {
            if (!seed[n] || comp[n] >= 0) continue;
            int id = sizes.Count, size = 0;
            comp[n] = id;
            stack.Push(n);
            while (stack.Count > 0)
            {
                int q = stack.Pop();
                size++;
                if (adj[q] == null) continue;
                foreach (int k in adj[q])
                    if (seed[k] && comp[k] < 0) { comp[k] = id; stack.Push(k); }
            }
            sizes.Add(size);
        }

        var inRegion = (bool[])seed.Clone();
        int gapAdded = 0;

        // Two lobes or more: fill between the two LARGEST. Anything smaller is a stray scrap of weighting,
        // not a breast, and bridging to one would drag the region somewhere arbitrary.
        var largest = Enumerable.Range(0, sizes.Count).OrderByDescending(i => sizes[i]).Take(2).ToList();
        if (!fillGap)
        {
            log?.Invoke($"bust bridge: {sizes.Count} lobe(s) "
                      + $"[{string.Join(", ", sizes.OrderByDescending(s => s).Take(4))}] — "
                      + "seed used as the region, no gap to fill");
        }
        else if (largest.Count == 2)
        {
            var dA = GapDistance(comp, largest[0], seed, adj, nodeCount);
            var dB = GapDistance(comp, largest[1], seed, adj, nodeCount);

            int best = int.MaxValue;
            for (int n = 0; n < nodeCount; n++)
            {
                if (seed[n] || dA[n] < 0 || dB[n] < 0) continue;
                int sum = dA[n] + dB[n];
                if (sum < best) best = sum;
            }
            if (best != int.MaxValue)
                for (int n = 0; n < nodeCount; n++)
                {
                    if (seed[n] || dA[n] < 0 || dB[n] < 0) continue;
                    if (dA[n] + dB[n] > best + BustGapSlack) continue;
                    inRegion[n] = true;
                    gapAdded++;
                }
            log?.Invoke($"bust bridge: {sizes.Count} lobe(s) "
                      + $"[{string.Join(", ", sizes.OrderByDescending(s => s).Take(4))}], "
                      + $"{gapAdded} node(s) added across the gap (shortest crossing {best} edges)");
        }
        else
        {
            log?.Invoke($"bust bridge: {sizes.Count} lobe(s) — no gap to fill");
        }

        // The ramp: graph distance inward from the region's edge, so the bridge fades into the untouched
        // shell instead of ending in a crease.
        var depth = new int[nodeCount];
        Array.Fill(depth, -1);
        var queue = new Queue<int>();
        for (int n = 0; n < nodeCount; n++)
        {
            if (inRegion[n] || adj[n] == null) continue;
            foreach (int k in adj[n])
                if (inRegion[k] && depth[k] < 0) { depth[k] = 1; queue.Enqueue(k); }
        }
        while (queue.Count > 0)
        {
            int q = queue.Dequeue();
            if (depth[q] >= BustFadeSteps + 1 || adj[q] == null) continue;
            foreach (int k in adj[q])
                if (inRegion[k] && depth[k] < 0) { depth[k] = depth[q] + 1; queue.Enqueue(k); }
        }

        var w = new float[nodeCount];
        for (int n = 0; n < nodeCount; n++)
        {
            if (!inRegion[n]) continue;
            // depth < 0 means the ramp never reached it — deep inside, or a region with no outside at all
            // (a mesh entirely covered by the bust, which no body has). Full weight either way.
            w[n] = depth[n] < 0 ? 1f : MathF.Min(1f, depth[n] / (float)(BustFadeSteps + 1));
        }
        return w;
    }

    /// <summary>
    /// Breadth-first edge distance from one component of <paramref name="seed"/> to every node OUTSIDE the
    /// seed. -1 for anything unreached. Seeded from the component's own nodes at distance 0 and never
    /// travelling back through the seed, so the numbers describe the gap and not a walk over the bust.
    /// </summary>
    private static int[] GapDistance(int[] comp, int id, bool[] seed, List<int>[] adj, int nodeCount)
    {
        var d = new int[nodeCount];
        Array.Fill(d, -1);
        var queue = new Queue<int>();
        for (int n = 0; n < nodeCount; n++)
        {
            if (comp[n] != id || adj[n] == null) continue;
            foreach (int k in adj[n])
                if (!seed[k] && d[k] < 0) { d[k] = 1; queue.Enqueue(k); }
        }
        while (queue.Count > 0)
        {
            int q = queue.Dequeue();
            if (d[q] >= BustGapMaxSteps || adj[q] == null) continue;
            foreach (int k in adj[q])
                if (!seed[k] && d[k] < 0) { d[k] = d[q] + 1; queue.Enqueue(k); }
        }
        return d;
    }

    /// <summary>
    /// How far a bridged shell stands OFF the skin, as a body-UV map: 0 where the cloth still lies on the
    /// body, 255 where it has lifted <paramref name="fullAt"/> or more. Null when this body has no bust
    /// bones, nothing is covered, or the bridge would not fire.
    /// <para/>
    /// Exists because the skin bake and the shell are built in that order, and the bake needs to know
    /// something only the shell knows. Skindenting presses a groove into the SKIN under a garment's edge,
    /// which is right while the cloth is against the body and wrong the moment it is not: a spanned
    /// cleavage floats centimetres clear, and denting the skin beneath it draws the seam of a garment that
    /// is no longer touching there. The compositor cannot wait for the shell — it has already published the
    /// normal by then — so the bridge's own solve is run here over the body models the bake already holds.
    /// <para/>
    /// Deliberately the same <see cref="BustBridgeSolve"/> the shell uses, not an approximation of it. A
    /// second implementation of "where does the cloth lift" would drift from the first, and the failure
    /// would be a groove appearing exactly where the geometry says there is no contact — invisible in code
    /// review and obvious in game.
    /// </summary>
    /// <param name="fullAt">Lift at which suppression is total, in model units.</param>
    internal static byte[]? BustStandoffMap(IReadOnlyList<byte[]> bodies, byte[]? coverage, int covW, int covH,
                                           float strength, int size, float fullAt, Action<string>? log = null)
    {
        if (bodies.Count == 0 || size <= 0 || strength <= 0f || fullAt <= 0f) return null;

        byte[]? map = null;
        foreach (var mdl in bodies)
        {
            if (mdl is not { Length: > 0 }) continue;
            if (!TryReadLod0Geometry(mdl, out var fPos, out var fUv, out var fTri, out var fW, out var fNrm))
                continue;
            int vc = fPos.Length / 3;
            if (vc == 0 || fTri.Length < 3) continue;

            var bust = new float[vc];
            bool anyBust = false;
            for (int i = 0; i < vc; i++)
            {
                float acc = 0f;
                foreach (var (bone, bw) in fW[i])
                    if (bone.Equals(BustBoneL, StringComparison.OrdinalIgnoreCase)
                     || bone.Equals(BustBoneR, StringComparison.OrdinalIgnoreCase))
                        acc += bw;
                if (acc <= 0f) continue;
                bust[i] = MathF.Min(1f, acc);
                anyBust = true;
            }
            if (!anyBust) continue;

            // Sampled with WRAP, like every other body-UV read here: a body's UVs need not live in the
            // [0,1] tile, and the shell writer normalises per mesh while this reads every mesh at once.
            // Wrapping makes an integer tile offset irrelevant instead of putting a whole mesh on row 0.
            bool[]? covered = null;
            if (coverage != null && covW > 0 && covH > 0 && coverage.Length >= covW * covH)
            {
                covered = new bool[vc];
                for (int i = 0; i < vc; i++)
                {
                    int x = ((int)MathF.Floor(fUv[i * 2] * covW) % covW + covW) % covW;
                    int y = ((int)MathF.Floor(fUv[i * 2 + 1] * covH) % covH + covH) % covH;
                    covered[i] = coverage[y * covW + x] >= CoverageFloor;
                }
            }

            var p3 = new Vec3[vc];
            var n3 = new Vec3[vc];
            for (int i = 0; i < vc; i++)
            {
                p3[i] = new Vec3(fPos[i * 3], fPos[i * 3 + 1], fPos[i * 3 + 2]);
                n3[i] = new Vec3(fNrm[i * 3], fNrm[i * 3 + 1], fNrm[i * 3 + 2]);
            }

            var plan = BustBridgeSolve(p3, n3, fTri, bust, strength, log, covered);
            if (plan == null) continue;

            var lift = new float[vc];
            for (int i = 0; i < vc; i++)
            {
                var d = plan.Delta[i];
                lift[i] = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            }

            map ??= new byte[size * size];
            // INTERPOLATED across the triangle, not the max of its corners.
            //
            // The max is the obvious choice for a conservative gate and it leaves a visible seam. The lift
            // runs to forty times the contact threshold, so the map's boundary is where lift falls through
            // a value it crosses steeply — and taking the corner max there makes that boundary follow the
            // body's TRIANGLE EDGES rather than the contour of the lift. At a 4K map a body triangle spans
            // more texels than the feather that follows, so the facets survive it: a hard, straight,
            // stepped line of suppression across the ribs.
            //
            // Sub-texel triangles keep the conservative footprint fill — point-sampling a mesh finer than
            // the map it writes into leaves a dotted mask rather than a solid one, which is the same trap
            // CapFootprintMask documents.
            for (int t = 0; t + 2 < fTri.Length; t += 3)
            {
                int a = fTri[t], b = fTri[t + 1], c = fTri[t + 2];
                if (a >= vc || b >= vc || c >= vc) continue;
                if (MathF.Max(lift[a], MathF.Max(lift[b], lift[c])) <= BustBridgeEpsilon) continue;

                float ax = fUv[a * 2] * size, ay = fUv[a * 2 + 1] * size;
                float bx = fUv[b * 2] * size, by = fUv[b * 2 + 1] * size;
                float cx = fUv[c * 2] * size, cy = fUv[c * 2 + 1] * size;
                int x0 = (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)));
                int x1 = (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx)));
                int y0 = (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)));
                int y1 = (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy)));
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1 << 18) continue;   // straddles a UV seam

                float det = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                bool tiny = x1 - x0 <= 2 && y1 - y0 <= 2;
                byte flat = (byte)Math.Clamp(
                    MathF.Round(MathF.Max(lift[a], MathF.Max(lift[b], lift[c])) / fullAt * 255f), 0f, 255f);

                for (int y = y0; y <= y1; y++)
                {
                    int wy = (y % size + size) % size;
                    for (int x = x0; x <= x1; x++)
                    {
                        byte v;
                        if (tiny || MathF.Abs(det) < 1e-9f) v = flat;
                        else
                        {
                            float px = x + 0.5f, py = y + 0.5f;
                            float l0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / det;
                            float l1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / det;
                            float l2 = 1f - l0 - l1;
                            // A small negative margin keeps neighbouring triangles from leaving a seam of
                            // untouched texels between them; the values agree along a shared edge anyway.
                            if (l0 < -0.02f || l1 < -0.02f || l2 < -0.02f) continue;
                            float m = l0 * lift[a] + l1 * lift[b] + l2 * lift[c];
                            v = (byte)Math.Clamp(MathF.Round(m / fullAt * 255f), 0f, 255f);
                        }
                        if (v == 0) continue;
                        int p = wy * size + (x % size + size) % size;
                        if (map[p] < v) map[p] = v;
                    }
                }
            }
        }
        return map;
    }

    /// <summary>
    /// The height each node should reach: the straight line between the furthest-forward point of each
    /// breast, taken row by row across the chest. Never below where the node already is.
    /// <para/>
    /// This is the whole solve, and it is a construction rather than an iteration. Three relaxations were
    /// tried first and each failed for a reason worth keeping, because each is the obvious thing to reach
    /// for:
    /// <list type="bullet">
    /// <item>"raise to the neighbour MEAN" reads any slope as concavity on a triangulated quad grid, where
    /// a vertex's neighbours are lopsided, and inflates the whole garment a little more every pass.</item>
    /// <item>"raise to a fitted PLANE" fixes that and cannot touch a cleavage at all: a cleavage is a
    /// SADDLE — dished across, bulging from collarbone to ribcage — and an isotropic operator sees the two
    /// curvatures cancel. It converged in 320 passes having closed 0.012 of a 0.059 dish.</item>
    /// <item>"raise to a LINE fitted across" is directionally right and fragile in practice: on an
    /// irregular mesh the per-vertex test for whether a node even has neighbours either side goes both
    /// ways between adjacent vertices, so half a row lifts and half stays, which crumples the surface
    /// rather than flattening it.</item>
    /// </list>
    /// A row's answer is known in closed form — it is the chord — so nothing is gained by iterating toward
    /// it, and everything that went wrong above came from iterating. Taking it directly also makes the
    /// three properties the feature promises true by construction rather than at convergence: the span is
    /// exactly straight, the apexes are its endpoints and cannot move, and no node is ever placed below
    /// where it started, so it cannot enter the body.
    /// <para/>
    /// Rows are bands of <paramref name="ver"/> about one mesh edge tall, and a node reads the chord
    /// interpolated between the two nearest band centres, so the result is a smooth ruled surface rather
    /// than a stack of steps. Outside the two apexes nothing moves: this bridges BETWEEN the breasts and
    /// leaves their outer flanks alone.
    /// </summary>
    private static float[] ChordTarget(float[] h0, float[] lat, float[] ver, float[] w, int count,
                                       List<int>[] adj, Vec3[] pos, Action<string>? log)
    {
        var target = (float[])h0.Clone();

        float loV = float.MaxValue, hiV = float.MinValue, midLat = 0f;
        int n0 = 0;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            loV = MathF.Min(loV, ver[n]); hiV = MathF.Max(hiV, ver[n]);
            midLat += lat[n];
            n0++;
        }
        if (n0 < 2) return target;
        midLat /= n0;

        // One band per edge of mesh, so a band is as fine as the geometry can express and no finer.
        float edge = 0f;
        int edges = 0;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f || adj[n] == null) continue;
            foreach (int k in adj[n])
            {
                float dx = pos[k].X - pos[n].X, dy = pos[k].Y - pos[n].Y, dz = pos[k].Z - pos[n].Z;
                edge += MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                edges++;
            }
        }
        edge = edges > 0 ? edge / edges : 0f;
        int bands = edge > 1e-6f ? (int)MathF.Round((hiV - loV) / edge) : 0;
        bands = Math.Clamp(bands, 3, 512);
        float bandH = (hiV - loV) / bands;
        if (bandH <= 1e-9f) return target;

        // Each band's two apexes: the furthest-forward node either side of the chest's midline.
        var latL = new float[bands]; var hL = new float[bands];
        var latR = new float[bands]; var hR = new float[bands];
        var have = new bool[bands];
        for (int b = 0; b < bands; b++) { hL[b] = float.MinValue; hR[b] = float.MinValue; }
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            int b = Math.Clamp((int)((ver[n] - loV) / bandH), 0, bands - 1);
            if (lat[n] < midLat) { if (h0[n] > hL[b]) { hL[b] = h0[n]; latL[b] = lat[n]; } }
            else                 { if (h0[n] > hR[b]) { hR[b] = h0[n]; latR[b] = lat[n]; } }
        }
        // Is there any surface BETWEEN the two apexes? A cleft has a floor; a gap does not, and the
        // difference is not visible from the apexes alone.
        //
        // Below the buttocks the body stops being one surface and becomes two legs, and a band there still
        // has a furthest-back point on each side — one per thigh. Spanned, that lays a sheet across the
        // gap between them: measured on a real body, 235 vertices moved by up to 68mm through the 160mm
        // below the cleft, which is a skirt rather than a bridge. The bust never showed this because a
        // sternum is always there between the breasts.
        var midHole = new float[bands];
        for (int b = 0; b < bands; b++) midHole[b] = float.MaxValue;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            int b = Math.Clamp((int)((ver[n] - loV) / bandH), 0, bands - 1);
            if (hL[b] <= float.MinValue || hR[b] <= float.MinValue) continue;
            float mid = (latL[b] + latR[b]) * 0.5f;
            midHole[b] = MathF.Min(midHole[b], MathF.Abs(lat[n] - mid));
        }

        int usable = 0, holed = 0;
        var hole = new bool[bands];
        for (int b = 0; b < bands; b++)
        {
            have[b] = hL[b] > float.MinValue && hR[b] > float.MinValue && latR[b] - latL[b] > 1e-6f;
            // Measured as a fraction of THIS band's own apex separation, so it means the same thing on a
            // cleavage and a cleft and needs no length in model units.
            if (have[b] && midHole[b] > (latR[b] - latL[b]) * ChordMidlineGap)
            { have[b] = false; hole[b] = true; holed++; }
            if (have[b]) usable++;
        }
        if (holed > 0)
            log?.Invoke($"bust bridge: {holed} band(s) have no surface between their two sides — a gap, "
                      + "not a cleft, and nothing to span there");
        if (usable == 0)
        {
            log?.Invoke("bust bridge: no band has a forward-most point on BOTH sides — nothing to span");
            return target;
        }

        // A band with a lobe on only one side (the very top and bottom of the region, where the cleavage
        // has run out) borrows its neighbour's, so the span tapers away instead of ending in a step.
        //
        // A band with a HOLE does not, and the distinction is the whole point of tracking them apart. The
        // first is a cleft that has run out and wants a taper; the second is open space, and borrowing a
        // chord there is what carried the span down into the gap between the legs even after those bands
        // had been recognised.
        // How much of the chord each band is entitled to. A band that found its own apexes gets all of it;
        // a band that BORROWED gets less the further it had to reach, and nothing past ChordBorrowBands.
        //
        // Borrowing alone does not taper, which is what the note here used to claim. A copied chord is the
        // chord of a band that HAD a cleft, applied to one that does not — above the buttocks the surface
        // has fallen away, so the same chord sits further and further in front of it and the lift GROWS
        // with height until the region weight cuts it off. That is an extrapolation with a hard end, and
        // it drew a horizontal line across the small of the back that survived both a smooth region ramp
        // and a feathered facing gate, because neither was where the step lived.
        var bandFade = new float[bands];
        for (int b = 0; b < bands; b++)
        {
            if (have[b]) { bandFade[b] = 1f; continue; }
            if (hole[b]) continue;
            int near = -1, reach = 0;
            for (int d = 1; d < bands && near < 0; d++)
            {
                if (b - d >= 0 && have[b - d]) { near = b - d; reach = d; }
                else if (b + d < bands && have[b + d]) { near = b + d; reach = d; }
            }
            if (near < 0) continue;
            latL[b] = latL[near]; hL[b] = hL[near];
            latR[b] = latR[near]; hR[b] = hR[near];
            bandFade[b] = 1f - Smoothstep(Math.Clamp((reach - 1f) / ChordBorrowBands, 0f, 1f));
        }

        // Smooth the apex LINE down each breast before spanning between the two of them. Each band takes
        // its apex from whichever vertex happens to be furthest forward in it, and on an irregular mesh
        // that wobbles by a fraction of an edge from one band to the next — which the chord then amplifies
        // all the way across the cleavage. Measured: two nodes 1mm apart at the sternum came out 5mm apart
        // because they fell in adjacent bands.
        //
        // Safe to smooth downward as well as up: a node is only ever lifted from where it started, so a
        // chord pulled slightly under a real apex leaves that apex exactly where it is.
        for (int pass = 0; pass < BustBandSmoothing; pass++)
        {
            Smooth1D(hL, bands); Smooth1D(latL, bands);
            Smooth1D(hR, bands); Smooth1D(latR, bands);
        }

        // The chord at one band, evaluated at a lateral position — or null outside the two apexes, which
        // is what keeps this a bridge BETWEEN the breasts rather than a flattening of the whole chest.
        float? Chord(int b, float u)
        {
            if (u <= latL[b] || u >= latR[b]) return null;
            float t = (u - latL[b]) / (latR[b] - latL[b]);
            return hL[b] + (hR[b] - hL[b]) * t;
        }

        int lifted = 0;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;

            // Between the two nearest band CENTRES, so the surface is ruled between rows rather than
            // stepped at their boundaries.
            float f = (ver[n] - loV) / bandH - 0.5f;
            int b0 = Math.Clamp((int)MathF.Floor(f), 0, bands - 1);
            int b1 = Math.Clamp(b0 + 1, 0, bands - 1);
            float mix = Math.Clamp(f - b0, 0f, 1f);

            var c0 = Chord(b0, lat[n]);
            var c1 = Chord(b1, lat[n]);
            // A band with no chord at this lateral position — past its own apexes — contributes the node's
            // OWN height, meaning "no span here", and the blend fades toward that.
            //
            // Handing the blend over to whichever band still has a chord is the obvious alternative and it
            // steps: adjacent bands have slightly different apex positions, so a node just past one band's
            // apex jumped from a half-blended lift to a full one while its neighbour a row away did not.
            // That put a notch in the garment's edge, at band granularity, following the band boundary —
            // the jagged bit reported on a bralette strap. Fading to no-lift keeps it continuous.
            float want = (c0, c1) switch
            {
                ({ } a, { } b) => a + (b - a) * mix,
                ({ } a, null)  => a + (h0[n] - a) * mix,
                (null, { } b)  => h0[n] + (b - h0[n]) * mix,
                _              => float.MinValue,
            };
            // No chord at this lateral position in EITHER band — past both sets of apexes. Guarded on its
            // own rather than left to the clamp below, which caught it only by accident: the sentinel is
            // float.MinValue, so `want <= h0` swallowed it silently. That held while this was the only
            // target; a two-sided variant tried here let it straight through and the displacement came
            // out infinite.
            if (c0 == null && c1 == null) continue;

            // Faded by how far each band had to reach for its chord, blended between the two the same way
            // the heights are. This is what makes the span die out at the top and bottom of the cleft
            // instead of holding a borrowed lift right up to the region's edge.
            float fade = bandFade[b0] + (bandFade[b1] - bandFade[b0]) * mix;
            if (fade <= 0f) continue;
            want = h0[n] + (want - h0[n]) * fade;

            // RAISE-ONLY, and that clamp is the no-clip guarantee itself: a node only ever leaves the
            // skin, so it can never be driven into it. It is also what keeps this a bridge across the gap
            // rather than a flattening of the lobes either side. A pass that needs to lower wants
            // EnvelopeTarget instead — relaxing this is not the way to get there.
            if (want <= h0[n]) continue;
            target[n] = want;
            lifted++;
        }

        log?.Invoke($"bust bridge: {bands} band(s) of {bandH:0.#####} ({usable} with a lobe either side), "
                  + $"{lifted} node(s) lifted to the chord");
        return target;
    }

    /// <summary>
    /// The crotch fold, flattened to the surface either side of it. A body pass, for the same reason the
    /// nipple's is: it lowers as well as raises, so the shell has to be cut from the result.
    /// <para/>
    /// Three things make this the chord rather than the nipple's relax, and each was measured before it
    /// was believed:
    /// <list type="bullet">
    /// <item>A plain 3-D relax cannot close it. The fold is an invagination — 51.5mm of relief across the
    /// middle 12mm at one height — and a Laplacian dragged the region 73mm while taking that only to 44mm.
    /// It would destroy the surroundings long before it closed the fold.</item>
    /// <item>The chord over the whole hip front finds the two THIGHS as its lobes. They sit 40mm further
    /// forward than the crotch, so it spans between them and webs the legs together. Hence the CORRIDOR:
    /// a band either side of the midline, so the apexes it finds are the fold's own shoulders.</item>
    /// <item>Inside the corridor the surface cannot say which way is out. A crease's walls face each other
    /// across the midline, and the region's mean normal came to 0.001 forward — the axis derived from it
    /// was (-1,0,0), sideways, and everything downstream then solved a different problem competently. So
    /// the outward direction is SUPPLIED.</item>
    /// </list>
    /// Two-sided, because the fold's cross-section is a W: shoulders at ±10mm, grooves at ±6mm, and a
    /// ridge on the midline standing proud of the shoulders. Raising alone fills the grooves and leaves
    /// the ridge, which is most of what still shows.
    /// </summary>
    /// <param name="hip">
    /// The hip bone's influence with the thigh rivalry applied — where the legs outweigh the hip the
    /// vertex is on a leg. This is the SPAN's region, and the veto has to be there: spanning between two
    /// surfaces that belong to different legs is what webs them together.
    /// </param>
    /// <param name="near">
    /// The same influence WITHOUT that veto — hip and thighs together. This is the relax's region, and the
    /// veto has to be gone: measured on a real body, only 65 vertices behind the mid-plane survive it and
    /// another 266 in the same box do not, so four fifths of the surface directly under the crotch was
    /// excluded and the underside came back as rough as it went in.
    /// <para/>
    /// Safe here for the reason it is not safe for the span. A Laplacian moves a node toward its own
    /// neighbours along mesh EDGES, and below the crotch the two legs share none, so there is no path by
    /// which it could pull them together. Above the crotch they do share edges, and smoothing across those
    /// is the entire point. What has to bound it instead is geometry, which is what the box below is.
    /// </param>
    private static BustBridgePlan? FoldPlan(Vec3[] pos, Vec3[] nrm, ushort[] tris, float[] hip, float[] near,
                                            bool[]? covered, float strength, Action<string>? log)
    {
        int vc = pos.Length;

        // The front of the body, by POSITION rather than by facing. The cleft could use the surface's own
        // normals to tell front from back; here it cannot, for the reason in the summary.
        //
        // Model +Z is forward on every body a shell is cut from, the same fact GateToBackFacing rests on.
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < vc; i++)
        {
            if (hip[i] <= 0f || pos[i].Z <= 0f) continue;
            lo = MathF.Min(lo, pos[i].X); hi = MathF.Max(hi, pos[i].X);
        }
        if (hi <= lo) { log?.Invoke("crotch fold: no hip-owned surface on the front"); return null; }

        // The corridor's width comes from the CROTCH, not from the whole region. The hip owns the waist
        // too, so the region runs ±154mm on a real body and a fraction of that is a corridor 100mm wide —
        // wide enough to take the inner thighs back in, which is the thing the corridor exists to exclude.
        //
        // The crotch is where the legs meet: the LOWEST height at which the body still has surface on the
        // midline at all. Below that the two legs are separate and there is nothing spanning between them,
        // which is the same fact the chord's own hole test rests on. Measured there, the region's width is
        // the fold's own scale on any body.
        float mid = (lo + hi) * 0.5f;
        float half = (hi - lo) * 0.5f;

        float lowest = float.MaxValue;
        for (int i = 0; i < vc; i++)
        {
            if (hip[i] <= 0f || pos[i].Z <= 0f) continue;
            if (MathF.Abs(pos[i].X - mid) > half * FoldMidlineProbe) continue;
            lowest = MathF.Min(lowest, pos[i].Y);
        }
        if (lowest == float.MaxValue)
        {
            log?.Invoke("crotch fold: the body has no surface on the front midline — nothing to flatten");
            return null;
        }

        float wLo = float.MaxValue, wHi = float.MinValue;
        for (int i = 0; i < vc; i++)
        {
            if (hip[i] <= 0f || pos[i].Z <= 0f) continue;
            if (pos[i].Y > lowest + half * FoldCrotchBand) continue;
            wLo = MathF.Min(wLo, pos[i].X); wHi = MathF.Max(wHi, pos[i].X);
        }
        float crotchHalf = wHi > wLo ? (wHi - wLo) * 0.5f : half;

        // IS THAT ACTUALLY A CROTCH? Everything from here down is scaled by it, so if the answer is no the
        // pass does not merely underperform, it reshapes whatever it found instead — and it found it on a
        // real body. A skin mesh that stops at the hips has its lowest midline surface at its own bottom
        // EDGE, and measured there the "crotch" came out 183.5mm across at y=1.031 against a hip region
        // 222.3mm wide. That is a waist. The pass then moved the belly by up to 9.5mm and published a hole
        // through the stomach.
        //
        // Two independent things are wrong with a boundary pretending to be a crotch, and both are worth
        // testing because a mesh could fail either alone:
        //
        // A crotch is NARROW. It is the last place the two legs still meet, so it is a small fraction of
        // the pelvis around it — a fifth on the body that works, against five sixths on the one that does
        // not. Nothing near half is a crotch.
        //
        // And a crotch has LEGS below it. Surface continues past it for the length of a limb, where a
        // boundary has nothing underneath at all.
        // Measured on `near` and NOT on `hip`, which would make the test vacuous: the thigh veto zeroes the
        // hip's weight exactly where the legs begin, so asking it how far the legs reach can only ever
        // answer "barely". It said 18.8mm on a body whose legs run the length of the model.
        float bottom = float.MaxValue;
        for (int i = 0; i < vc; i++)
            if (near[i] > 0f && pos[i].Z > 0f) bottom = MathF.Min(bottom, pos[i].Y);

        if (crotchHalf > half * FoldCrotchMaxWidth)
        {
            log?.Invoke($"crotch fold: SKIPPED, what the midline found at y={lowest:0.###} is "
                      + $"{crotchHalf * 2000:0.#}mm across against a hip region {half * 2000:0.#}mm wide — "
                      + "that is a waist, not a crotch, and this mesh most likely stops at the hips");
            return null;
        }
        if (bottom == float.MaxValue || lowest - bottom < crotchHalf * FoldLegsBelow)
        {
            log?.Invoke($"crotch fold: SKIPPED, the body reaches only {(lowest - bottom) * 1000:0.#}mm "
                      + $"below y={lowest:0.###} — there are no legs under it, so that is the edge of the "
                      + "mesh rather than the place they meet");
            return null;
        }

        float corridor = crotchHalf * FoldCorridor;

        // Bounded ABOVE as well. The hip owns the belly, and the corridor is a vertical strip, so without
        // this the pass reaches the waist — measured, it moved rows at y 0.94-1.00 by up to 17mm, which is
        // a stomach being reshaped by a setting about the crotch. The fold lives just above where the legs
        // meet, so its own scale bounds it: the crotch's width is the right ruler and needs no per-body
        // number.
        float ceiling = lowest + crotchHalf * 2f * FoldHeight;

        // EVERY edge of this region is a line drawn through open skin, not a garment's hem, so every one of
        // them has to be feathered by hand. The one-ring ramp BustRegionWeights builds is for a coverage
        // boundary, where the neighbouring cloth genuinely must not move; here it left a 4.8mm lift dying
        // over about 6mm at the ceiling, and that showed in game as a break across the front.
        //
        // Three edges, three fades, each measured in the crotch's own width so no body needs a number:
        //  - SIDEWAYS, out toward the inner thigh, which is the direction the corridor exists to stop.
        //  - UPWARD past the ceiling, into the belly.
        //  - FORWARD from the body's mid-plane, where the height field's axis becomes tangent to the
        //    surface and it stops meaning anything. This one fades IN rather than out.
        float feather = corridor * (FoldFeather - 1f);
        float roof = (ceiling - lowest) * (FoldFeather - 1f);
        float frontFade = crotchHalf * FoldFrontFade;
        float back = crotchHalf * FoldBack;

        float Fade(float x, float width) => width <= 1e-9f ? 1f : 1f - Smoothstep(Math.Clamp(x / width, 0f, 1f));

        var w = new float[vc];
        var ramp = new float[vc];
        var relax = new float[vc];
        // A FLOOR as well, and it only becomes necessary once the relax stops asking the thigh bones for
        // permission: without it the relax region runs on down the two inner thighs for as long as the
        // corridor is narrow enough to hold them, and a strip of smoothed skin down the inside of each leg
        // is a seam, not a fix. The fold is done a short way below where the legs meet, so the crotch's own
        // width bounds it from below exactly as it does from above.
        float floorY = lowest - crotchHalf * FoldDrop;
        float drop = crotchHalf * FoldDropFeather;

        int seeded = 0, relaxed = 0, under = 0;
        for (int i = 0; i < vc; i++)
        {
            float side = MathF.Abs(pos[i].X - mid);
            if (side > corridor + feather) continue;
            float above = pos[i].Y - ceiling;
            if (above > roof) continue;
            float below = floorY - pos[i].Y;
            if (below > drop) continue;

            float box = Fade(side - corridor, feather) * Fade(above, roof) * Fade(below, drop);
            if (box <= 0f) continue;

            // The height field: front-facing only, faded in from the mid-plane, and still subject to the
            // thigh veto because spanning between two legs is what webs them together.
            //
            // FADED BY FACING as well, and that is the one that stops it tearing the mesh. A height field
            // pushes every node along one axis; where the surface is square to that axis it lifts, and
            // where the surface has turned to lie ALONG it the same push slides the skin sideways instead.
            // Two adjacent vertices sliding at a slope of BustMaxSlope across a one-millimetre edge — and
            // the crotch is the densest millimetre-edged part of the body — simply pass through each
            // other. Measured: 12 triangles inside out and 2 collapsed, every one of them in the two
            // millimetres just above the crotch, which is exactly where the surface turns under.
            //
            // The z ramp above is the same idea done badly: position is only a proxy for orientation, and
            // eight millimetres forward of the mid-plane the surface can still be raked hard. The normal
            // says it directly.
            if (hip[i] > 0f && pos[i].Z > 0f)
            {
                float square = Smoothstep(Math.Clamp(nrm[i].Z / FoldFacing, 0f, 1f));
                float f = box * Smoothstep(Math.Clamp(pos[i].Z / frontFade, 0f, 1f)) * square;
                if (f > 0f) { w[i] = hip[i]; ramp[i] = f; seeded++; }
            }

            // The relax: the same box, carried on round underneath and taking the thighs in with it. This
            // is the half of the region the span can never help — see the relaxSeed parameter on
            // BustBridgeSolve, and the `near` parameter above for why the veto is lifted here.
            if (near[i] > 0f)
            {
                float b = Fade(-pos[i].Z, back);

                // CONFINED TO THE FOLD'S OWN WIDTH, which is narrower than the corridor. `box` holds full
                // strength everywhere inside the corridor and only fades beyond it, so the relax was
                // smoothing the inner thigh as hard as the crotch — and the thigh is where a garment's leg
                // trim runs. Measured per lateral bin, the mean displacement PEAKED at 15-18mm off the
                // midline (3.39mm, against 0.93mm on the midline) with a 4.63mm spread inside that single
                // bin: both larger and less even than anything at the centre, which is a trim that wanders
                // rather than one that lifts.
                //
                // The fold's shoulders sit at ±10mm against a corridor of ±16.6mm, so full strength out to
                // half the corridor covers the feature and the fade covers the rest. Tapering from the
                // midline instead would weaken the shoulders, which are part of what is being flattened.
                float side2 = MathF.Abs(pos[i].X - mid);
                float core = corridor * FoldRelaxCore;
                float confine = side2 <= core ? 1f
                              : 1f - Smoothstep(Math.Clamp((side2 - core) / (corridor - core), 0f, 1f));

                if (b > 0f && confine > 0f)
                { relax[i] = box * b * confine; relaxed++; if (pos[i].Z <= 0f) under++; }
            }
        }
        if (seeded < MinBustBridgeNodes)
        {
            log?.Invoke($"crotch fold: {seeded} vertex(es) in the corridor — too few to describe a fold");
            return null;
        }
        log?.Invoke($"crotch fold: {seeded} vertex(es) in a corridor {corridor * 2000:0.#}mm wide about "
                  + $"x={mid:0.####} (feathered out to {(corridor + feather) * 2000:0.#}mm), {relaxed} "
                  + $"in the relax region ({under} of them behind the mid-plane), reaching "
                  + $"{back * 1000:0.#}mm behind it and down to y={floorY:0.###}, from a crotch "
                  + $"{crotchHalf * 2000:0.#}mm across at y={lowest:0.###} (the hip region itself is "
                  + $"{half * 2000:0.#}mm across)");

        // pinBoundary: this runs on the BODY, and the crotch is where a body is most likely to have one —
        // a socket for a genital mesh, or simply where the legs model ends. See the parameter's own note.
        return BustBridgeSolve(pos, nrm, tris, w, strength, log, covered,
                               smoothStrength: 0f, fillGap: false,
                               outward: new Vec3(0, 0, 1), envelope: true,
                               ramp: ramp, relaxSeed: relax, pinBoundary: true);
    }

    /// <summary>
    /// How wide the fold's corridor is, as a fraction of the CROTCH's width where the legs meet. Wide
    /// enough to contain the fold's shoulders — measured at ±10mm — and no wider, because everything past
    /// them is the inner thigh and spanning to THAT is what webs the legs together.
    /// </summary>
    private const float FoldCorridor = 0.25f;

    /// <summary>
    /// How much of the corridor the RELAX holds at full strength before it starts fading, as a fraction of
    /// the corridor's own half-width. The rest of the corridor gets the fade.
    /// <para/>
    /// Half, because the fold's shoulders sit at about ±10mm against a corridor of ±16.6mm: full strength
    /// to ±8.3mm and a fade from there covers the feature and lets go of the inner thigh, which is what
    /// the garment's leg trim lies on. This is NOT the same thing as the corridor's own feather — that
    /// fades the region out past its edge to avoid a step; this decides how much of the inside of the
    /// region the relax is entitled to work on at all.
    /// </summary>
    private const float FoldRelaxCore = 0.5f;

    /// <summary>
    /// Flatten each row of the region onto a straight line fitted across its own OUTER surface — the
    /// target for the crotch fold, where the chord cannot work.
    /// <para/>
    /// <see cref="ChordTarget"/> spans a valley between two peaks: it takes the highest point either side
    /// of the midline and lifts everything between them. The fold is the other shape — a peak between two
    /// valleys. Measured per band, the "apexes" it found sat at ±3 to ±6mm, because the highest point on
    /// each side IS the midline ridge, so the chord hopped across the ridge and left the grooves either
    /// side of it untouched. That is not a tuning problem; the construction assumes the middle is down.
    /// <para/>
    /// So this fits a LINE instead and moves the surface onto it in both directions, which flattens a W as
    /// readily as a V and needs no opinion about which way the middle goes.
    /// <para/>
    /// Fitted to the ENVELOPE, not to the region's nodes. A fold is an invagination — 51mm of relief
    /// across the middle 12mm on a real body — so its interior walls are in the region too, sitting far
    /// behind the surface anyone can see. Least squares over all of them drags the line back into the body
    /// and "flattening" then pulls the outer surface in to meet it. Binning by lateral position and taking
    /// each bin's frontmost node gives the silhouette, which is the thing a garment lies on.
    /// </summary>
    private static float[] EnvelopeTarget(float[] h0, float[] lat, float[] ver, float[] w, int count,
                                          List<int>[] adj, Vec3[] pos, Action<string>? log)
    {
        var target = (float[])h0.Clone();

        float loV = float.MaxValue, hiV = float.MinValue;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            loV = MathF.Min(loV, ver[n]); hiV = MathF.Max(hiV, ver[n]);
        }
        if (hiV <= loV) return target;

        // One band per edge of mesh, as the chord does — a band as fine as the geometry can express.
        float edge = 0f; int edges = 0;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f || adj[n] == null) continue;
            foreach (int k in adj[n])
            {
                float dx = pos[k].X - pos[n].X, dy = pos[k].Y - pos[n].Y, dz = pos[k].Z - pos[n].Z;
                edge += MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                edges++;
            }
        }
        edge = edges > 0 ? edge / edges : 0f;
        int bands = edge > 1e-6f ? (int)MathF.Round((hiV - loV) / edge) : 0;
        bands = Math.Clamp(bands, 3, 512);
        float bandH = (hiV - loV) / bands;
        if (bandH <= 1e-9f) return target;

        // The envelope, per band: the frontmost node in each lateral bin.
        var binMax = new float[bands, EnvelopeBins];
        var binLat = new float[bands, EnvelopeBins];
        for (int b = 0; b < bands; b++)
            for (int q = 0; q < EnvelopeBins; q++) binMax[b, q] = float.MinValue;

        float loL = float.MaxValue, hiL = float.MinValue;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            loL = MathF.Min(loL, lat[n]); hiL = MathF.Max(hiL, lat[n]);
        }
        if (hiL <= loL) return target;
        float latW = hiL - loL;
        float midL = (loL + hiL) * 0.5f;

        int Bin(float u) => Math.Clamp((int)((u - loL) / latW * EnvelopeBins), 0, EnvelopeBins - 1);

        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            int b = Math.Clamp((int)((ver[n] - loV) / bandH), 0, bands - 1);
            int q = Bin(lat[n]);
            if (h0[n] > binMax[b, q]) { binMax[b, q] = h0[n]; binLat[b, q] = lat[n]; }
        }

        // A LOW-PASS ALONG THE BAND, not a polynomial fitted to it.
        //
        // This used to fit a parabola, and the note that stood here said why that could not be rescued by
        // feeding it better samples: the fold's cross-section is a W — shoulders at ±10mm, grooves at
        // ±6mm, a ridge on the midline — and a parabola cannot describe a W, so the fold is not what
        // deviates from the fitted curve. It showed in its own log line: the fit left 8.6mm rms against a
        // feature only 4.6mm tall, which is to say the target was mostly noise. Snapping a surface onto a
        // target that wrong is why the pass MADE the crotch rougher, measured at 9.9% worse than the
        // untouched body across the bands it moved.
        //
        // What actually separates the two is SCALE, not shape. The body's curvature runs the width of the
        // corridor; the fold wiggles several times across it. So smooth the envelope laterally and keep
        // what survives: a low-pass carries any curvature the body happens to have — no assumption that it
        // is a parabola, or symmetric, or has one minimum — and removes the fold whatever shape it is.
        //
        // It is also TWO-SIDED, which a raise-only construction is not. Filling the grooves alone leaves
        // the midline ridge standing, and the ridge is most of what still shows; here a node above the
        // smoothed line moves in and one below moves out, so the ridge comes down as the grooves come up.
        // SIZED IN MILLIMETRES, not in bins. The bins span whatever lateral extent the region happens to
        // have, and that is the FEATHERED corridor — 59.7mm on the body this was tuned against, not the
        // 33.2mm corridor itself. Counting passes instead of distance therefore made the filter almost
        // twice as wide as intended (sigma 8.9mm against the 5mm meant), wide enough to smooth the inner
        // thigh's own curvature. That showed up as the pass moving the surface MOST at 15-18mm off the
        // midline — further out than the fold reaches, and right where a garment's leg trim runs, which is
        // exactly the trim wobbling in game.
        //
        // k passes of [1 2 1] is a Gaussian of sigma = sqrt(k/2) bins, so k = 2*(sigma/binWidth)^2.
        float binW = latW / EnvelopeBins;
        int passes = Math.Clamp((int)MathF.Round(2f * (EnvelopeSmoothSigma / binW) * (EnvelopeSmoothSigma / binW)),
                                1, 40);

        var prof = new float[bands, EnvelopeBins];
        var fitOk = new bool[bands];
        var row = new float[EnvelopeBins];
        var next = new float[EnvelopeBins];
        for (int b = 0; b < bands; b++)
        {
            int have = 0;
            for (int q = 0; q < EnvelopeBins; q++) if (binMax[b, q] != float.MinValue) have++;
            if (have < EnvelopeMinBins) continue;

            // Gaps filled from the nearest sample either side before smoothing, so an empty bin does not
            // drag its neighbours toward zero — and so the smoothing kernel stays uniform, which is what
            // makes "how many passes" mean a fixed distance on the body.
            for (int q = 0; q < EnvelopeBins; q++)
            {
                if (binMax[b, q] != float.MinValue) { row[q] = binMax[b, q]; continue; }
                float lo2 = float.MinValue, hi2 = float.MinValue;
                for (int k = q - 1; k >= 0; k--) if (binMax[b, k] != float.MinValue) { lo2 = binMax[b, k]; break; }
                for (int k = q + 1; k < EnvelopeBins; k++) if (binMax[b, k] != float.MinValue) { hi2 = binMax[b, k]; break; }
                row[q] = lo2 == float.MinValue ? hi2 : hi2 == float.MinValue ? lo2 : (lo2 + hi2) * 0.5f;
            }

            // [1 2 1], repeated. Clamped at the ends rather than wrapped or reflected: the corridor's edge
            // is the inner thigh, not the other side of the same feature.
            for (int pass = 0; pass < passes; pass++)
            {
                for (int q = 0; q < EnvelopeBins; q++)
                {
                    float l = row[Math.Max(0, q - 1)], r = row[Math.Min(EnvelopeBins - 1, q + 1)];
                    next[q] = (l + 2f * row[q] + r) * 0.25f;
                }
                (row, next) = (next, row);
            }

            for (int q = 0; q < EnvelopeBins; q++) prof[b, q] = row[q];
            fitOk[b] = true;
        }

        // The smoothed profile read back at an arbitrary lateral position, linearly between bin centres.
        float Envelope(int b, float u)
        {
            float t = (u - loL) / latW * EnvelopeBins - 0.5f;
            int q0 = (int)MathF.Floor(t);
            float f = t - q0;
            int qa = Math.Clamp(q0, 0, EnvelopeBins - 1);
            int qb = Math.Clamp(q0 + 1, 0, EnvelopeBins - 1);
            return prof[b, qa] + (prof[b, qb] - prof[b, qa]) * Math.Clamp(f, 0f, 1f);
        }

        // How much the envelope actually rises and falls across each band — the size of whatever feature
        // that row contains. Everything below is measured in it; see the block at the clamp for why.
        var bandRelief = new float[bands];
        for (int b = 0; b < bands; b++)
        {
            float lo2 = float.MaxValue, hi2 = float.MinValue;
            for (int q = 0; q < EnvelopeBins; q++)
            {
                if (binMax[b, q] == float.MinValue) continue;
                lo2 = MathF.Min(lo2, binMax[b, q]); hi2 = MathF.Max(hi2, binMax[b, q]);
            }
            bandRelief[b] = hi2 > lo2 ? hi2 - lo2 : 0f;
        }

        // Smoothed down the bands for the same reason the chord smooths its apex line: each band takes its
        // samples from whichever vertices fall in it, and that wobbles by a fraction of an edge from one
        // row to the next. One column per lateral bin, so the profile is filtered in BOTH directions —
        // across the corridor above, and up the body here.
        var col = new float[bands];
        for (int q = 0; q < EnvelopeBins; q++)
        {
            for (int b = 0; b < bands; b++) col[b] = prof[b, q];
            for (int pass = 0; pass < BustBandSmoothing; pass++) Smooth1D(col, bands);
            for (int b = 0; b < bands; b++) prof[b, q] = col[b];
        }
        for (int pass = 0; pass < BustBandSmoothing; pass++) Smooth1D(bandRelief, bands);

        int flattened = 0;
        double askSum = 0, gotSum = 0, fadeSum = 0; int deep = 0;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            int b = Math.Clamp((int)((ver[n] - loV) / bandH), 0, bands - 1);
            if (!fitOk[b]) continue;

            // ONLY the outer surface. A node well behind its own bin's frontmost is inside the fold, and
            // moving it onto the silhouette would drag the invagination's walls out through the skin.
            //
            // FADED rather than cut. A hard threshold has to sit somewhere, and anywhere near the fold's
            // own amplitude it runs through the middle of the feature: neighbouring nodes either side of
            // it then get snapped and not-snapped, and the row comes out MORE irregular than it started —
            // measured at y 0.880, 4.75mm of spread becoming 7.06mm. The interior is tens of millimetres
            // back and the surface relief is single digits, so there is plenty of room for a ramp between
            // them that never lands on a real edge.
            int q = Bin(lat[n]);
            if (binMax[b, q] == float.MinValue) continue;

            // BOTH thresholds are measured against the band's OWN relief — how far its envelope rises and
            // falls across the corridor — and not against the region's width.
            //
            // The width is the wrong ruler and it failed hard. It says nothing about how deep the feature
            // in front of it is, so widening the region widened the threshold with it: feathering the
            // region's edges took the corridor from 33mm to 60mm, the skin threshold went to 30mm with it,
            // and nodes half an inch inside the belly were suddenly counted as its outer surface and
            // snapped onto a fitted curve that does not describe them. That published as a 23.6mm hole
            // through the stomach.
            //
            // Relief is the honest scale: on a flat band it is small, so almost nothing counts as surface
            // and almost nothing moves; across the fold it is the fold's own depth. The clamp then says a
            // node may never travel further than the feature it is part of is tall, which bounds this pass
            // by construction rather than by a constant that has to be right on every body.
            float relief = bandRelief[b];
            if (relief <= 1e-6f) continue;
            float behind = binMax[b, q] - h0[n];
            float skin = relief * EnvelopeSkin;
            if (behind >= skin) { deep++; continue; }
            float onSurface = 1f - Smoothstep(behind / skin);

            float want = Envelope(b, lat[n]);

            // TAPERED ACROSS THE CORRIDOR. The fold sits on the midline — shoulders at ±10mm — and
            // everything past it is inner thigh, so the flatten has no business being at full strength out
            // there. Without this it was not merely present at the edge but STRONGEST there: measured per
            // lateral bin, the mean move peaked at 3.41mm in the 15-18mm band against 0.94mm on the
            // midline, with a 4.65mm spread inside that one band. Large and uneven, on the line a garment's
            // trim follows.
            //
            // The region's own ramp (nW) does not cover this. It fades from the region's OUTER boundary at
            // ~30mm, so at 16mm it is still saturated; this taper is about the fold's own lateral scale,
            // which is a different and smaller thing.
            float taper = 1f - Smoothstep(Math.Clamp(MathF.Abs(lat[n] - midL) / (latW * 0.5f), 0f, 1f));
            float cap = relief * EnvelopeMaxMove;
            float move = Math.Clamp((want - h0[n]) * onSurface * taper, -cap, cap);
            target[n] = h0[n] + move;
            askSum += Math.Abs(want - h0[n]);
            gotSum += Math.Abs(target[n] - h0[n]);
            fadeSum += onSurface;
            flattened++;
        }

        double resid = 0; int rn = 0;
        for (int b = 0; b < bands; b++)
        {
            if (!fitOk[b]) continue;
            for (int q = 0; q < EnvelopeBins; q++)
            {
                if (binMax[b, q] == float.MinValue) continue;
                double d = binMax[b, q] - Envelope(b, binLat[b, q]);
                resid += d * d; rn++;
            }
        }

        log?.Invoke($"crotch fold: {bands} band(s) of {bandH:0.#####}, {fitOk.Count(x => x)} fitted, "
                  + $"{flattened} node(s) flattened onto the envelope");
        if (flattened > 0)
            log?.Invoke($"crotch fold: ask {askSum / flattened * 1000:0.###}mm, applied "
                      + $"{gotSum / flattened * 1000:0.###}mm, mean fade {fadeSum / flattened:0.###}, "
                      + $"{deep} node(s) judged interior; fit leaves "
                      + $"{(rn > 0 ? Math.Sqrt(resid / rn) * 1000 : 0):0.###}mm rms on the envelope");
        return target;
    }

    /// <summary>
    /// How many rings out from the rim of a hole the relax fades back in over. The rim itself is held
    /// exactly; its neighbours recover smoothly rather than in one step.
    /// <para/>
    /// Three because the crotch's mesh runs about a millimetre an edge, so this is a ~3mm skirt around a
    /// hole roughly 3mm across — the same order as the feature, which is the scale a fade has to be at to
    /// be invisible. One ring is not a fade at all, and much more starts protecting surface the pass is
    /// there to flatten.
    /// </summary>
    private const int RimPinFade = 3;

    /// <summary>
    /// How wide the envelope's lateral low-pass is, as a distance on the body — the number that decides
    /// where "the body's shape" ends and "the fold" begins.
    /// <para/>
    /// IN METRES, not in bins or passes, because the bins span the region's own lateral extent and that
    /// varies with the body and with how far the corridor is feathered. Expressed as a pass count it was
    /// silently 1.8x wider than intended on the first body it met. See the derivation at the use site.
    /// <para/>
    /// 5mm sits above the fold's own features (shoulders at ±10mm, grooves at ±6mm, so wavelengths of
    /// 12-20mm) and well below the width of the corridor, which is the scale the body's curvature runs at.
    /// Turning it up indefinitely converges on a straight line across the corridor — the failure the
    /// parabola was originally chosen to avoid, which moved rows with no fold in them by up to 17mm.
    /// </summary>
    private const float EnvelopeSmoothSigma = 0.005f;

    /// <summary>
    /// The furthest a node may be moved onto the envelope, as a multiple of its own band's relief.
    /// <para/>
    /// The clamp exists so a node can never travel further than the feature it is part of is tall — a
    /// bound that scales with the body instead of a constant that has to be right on all of them. At 1.0
    /// it was the binding constraint rather than a backstop: the flatten asked for 4.7mm and delivered
    /// 1.9mm, and after the region ramp about 0.9mm reached the mesh.
    /// <para/>
    /// A modeller's own pass over the same crotch is the calibration. Diffed against the shipped result it
    /// still moved the midline by 5.6mm on average and up to 13.5mm — so the ask was about right all along
    /// and the clamp was throwing most of it away. Three gives the ask room to land while still refusing
    /// to move a node several times the height of anything near it.
    /// </summary>
    private const float EnvelopeMaxMove = 3.0f;

    /// <summary>Lateral bins the envelope is sampled in, across the region's width.</summary>
    private const int EnvelopeBins = 16;

    /// <summary>Fitted samples a band needs before its line is trusted.</summary>
    private const int EnvelopeMinBins = 4;


    /// <summary>
    /// How far behind its bin's frontmost node a node may sit and still count as the outer surface, as a
    /// multiple of its own band's RELIEF. Past this it is inside the fold, and flattening it would pull the
    /// invagination's walls out through the skin.
    /// <para/>
    /// Above 1 so the threshold always sits clear of the feature it is separating. Anywhere inside the
    /// relief it runs through the middle of the fold, and neighbouring nodes either side of it get snapped
    /// and not-snapped: the row then comes out MORE irregular than it started, measured at y 0.880 as
    /// 4.75mm of spread becoming 7.06mm. The interior of an invagination sits tens of millimetres back
    /// while its surface relief is single digits, so there is room for the ramp between them.
    /// <para/>
    /// A fraction of the REGION'S WIDTH is what this used to be, and that is the bug recorded at the clamp:
    /// the ruler has to be the feature, not the frame around it.
    /// </summary>
    private const float EnvelopeSkin = 1.5f;

    /// <summary>How close to the midline a vertex counts as being ON it, when looking for the lowest place
    /// the body still spans between the legs. A fraction of the hip region's half-width.</summary>
    private const float FoldMidlineProbe = 0.06f;

    /// <summary>
    /// How far above the crotch the fold's region reaches, as a multiple of the crotch's own width. The
    /// fold sits directly above where the legs meet and is done well before the belly starts; the hip bone
    /// is not, so something has to say where to stop.
    /// </summary>
    private const float FoldHeight = 0.3f;

    /// <summary>How tall a slice above that lowest point the crotch's width is measured over, as a
    /// fraction of the hip region's half-width. Enough rows to average out one ragged one.</summary>
    private const float FoldCrotchBand = 0.10f;

    /// <summary>
    /// The widest a crotch may be as a fraction of the hip region around it. Measured: 0.22 on a body
    /// whose legs meet where they should, 0.83 on a skin mesh that simply stops at the hips. Set between
    /// them with room on both sides, because the consequence of getting this wrong is not a weak result
    /// but a hole in someone's stomach.
    /// </summary>
    private const float FoldCrotchMaxWidth = 0.45f;

    /// <summary>How far the body must continue below the crotch before it counts as having legs, as a
    /// multiple of the crotch's own half-width. A limb goes on for many; a mesh boundary for none.</summary>
    private const float FoldLegsBelow = 1.0f;

    /// <summary>
    /// How far past the corridor and the ceiling the region fades to nothing, as a multiple of each. The
    /// displacement at those edges runs to about 5mm, and the eye reads a step of that size over anything
    /// under a centimetre or so; at 1.8 the fade has 13mm sideways and 27mm upward to spend, which puts
    /// the worst slope well under a twentieth and takes it below what a highlight can pick out.
    /// </summary>
    private const float FoldFeather = 1.8f;

    /// <summary>
    /// How far forward of the body's mid-plane the height field reaches full strength, as a fraction of
    /// the crotch's half-width. Its axis is +Z, so at z=0 that axis lies IN the surface and moving a node
    /// along it slides the skin sideways instead of raising it. Fading in over this leaves the mid-plane
    /// to the relax, which does not care which way a surface points.
    /// </summary>
    private const float FoldFrontFade = 0.25f;

    /// <summary>
    /// How far BELOW the crotch the region reaches at full strength, as a fraction of the crotch's own
    /// half-width. Load-bearing only for the relax: the span's thigh veto used to end the region down here
    /// on its own, and the relax does not have one.
    /// <para/>
    /// SMALL, and much smaller than the ceiling above, because the two directions are not symmetric. The
    /// fold sits at and above the line where the legs meet; BELOW it there is no fold to smooth, only the
    /// fillet where the two thighs part — and that is anatomy. Smoothing a concave fillet necessarily
    /// pushes it OUT, so reaching down there does not merely waste effort, it inflates the leg. Measured
    /// against the real upstream body at 0.5: 80 vertices in the y 0.80-0.85 band moved a mean of 2.5mm
    /// outward and up to 7.6mm, which reads in game as the thigh bowing.
    /// </summary>
    private const float FoldDrop = 0.1f;

    /// <summary>How far past <see cref="FoldDrop"/> the region fades to nothing, as a fraction of the
    /// crotch's half-width. Its own constant rather than the ceiling's feather, for the asymmetry set out
    /// above: there is nothing below the crotch this pass wants, so it only needs enough room to land
    /// softly.</summary>
    private const float FoldDropFeather = 0.3f;

    /// <summary>
    /// How far BEHIND the mid-plane the finishing relax reaches, as a fraction of the crotch's half-width.
    /// Far enough to take in the surface directly under the crotch, which turns to face down and which the
    /// span therefore cannot touch at all; not so far as to meet the seat, which is the cleft's business.
    /// <para/>
    /// Measured back along the underside, roughness runs 0.66mm at the mid-plane and 0.54mm ten to twenty
    /// millimetres behind it, then drops to 0.37mm and stays there — so the rough part of the underside is
    /// the first thirty millimetres and the fade belongs past them. At half a crotch-width it ended at
    /// -17mm, which put the fade through the middle of the rough band and left it MORE irregular than it
    /// started (0.54mm becoming 0.78mm), the same way a threshold through the middle of any feature does.
    /// A full width clears it and still stops well short of the buttocks.
    /// </summary>
    private const float FoldBack = 1.0f;

    /// <summary>
    /// Rounds of the finishing Laplacian. Longer than the nipple's six because it is doing more than
    /// de-jaggying here: under the crotch it is the only operator acting at all, so it has to take out the
    /// fold's own relief and not merely the vertex noise on top of it.
    /// <para/>
    /// A Laplacian pulls a feature of radius R in by about λh²/4R per pass. On the flanks, where R is the
    /// thigh's own 40mm and edges run 2.5mm, that is two hundredths of a millimetre a pass and twelve of
    /// them cost a quarter of a millimetre of leg. In the crease, where R is nearer 5mm, the same figure is
    /// 0.16mm a pass — which is not a cost but the entire purpose.
    /// <para/>
    /// TWELVE AND NOT MORE, measured rather than reasoned. Roughness is a converging quantity and it has
    /// converged by here; past it the pass stops buying smoothness and starts spending shape. At 28 the
    /// front got WORSE (0.243mm to 0.253mm) and so did the underside (0.671 to 0.693) and both fade edges
    /// with them, while the largest displacement grew from 8.6mm to 12.9mm — which is not the fold going
    /// anywhere, it is the legs pulling in. Twelve dominates it on every measure at once.
    /// </summary>
    private const int FoldRelaxPasses = 60;

    /// <summary>How far each relax pass moves a node toward its neighbours' centroid. Under-relaxed for
    /// the reason every other relaxation here is: at 1 a Jacobi step has eigenvalue -1 on the checkerboard
    /// mode and oscillates instead of converging.</summary>
    private const float FoldRelaxLambda = 0.5f;

    /// <summary>
    /// How far a node must move before its normal is fully recomputed rather than kept, as a fraction of
    /// the distance to its own neighbours. Half an edge is a genuinely different surface; a hundredth of
    /// one is the same surface with the artist's normal still describing it correctly. See the block that
    /// uses it for why handing every node in the region a fresh normal tore the garment's hem open.
    /// </summary>
    private const float NormalReshadeSpan = 0.5f;

    /// <summary>
    /// The most two neighbouring nodes' free 3-D displacements may differ, per unit of the edge between
    /// them.
    /// <para/>
    /// This is a SMOOTHNESS setting, not a safety one — <see cref="UnfoldTriangles"/> owns safety, and it
    /// owns it by checking rather than by bounding. Tuned as such: a relax works by making neighbouring
    /// displacements differ, so limiting exactly that fights the operator, and at 0.3 it ate the pass
    /// outright and left the front rougher than it found it (0.548mm becoming 0.558mm against 0.223mm
    /// unconstrained). Loose enough to let the relax work, tight enough that the unfolder rarely has
    /// anything to do.
    /// </summary>
    private const float FoldRelaxMaxSlope = 1.0f;

    /// <summary>
    /// How square to the span's axis a surface has to be before the span acts on it at full strength — the
    /// forward component of its normal, faded in from zero. Half is about sixty degrees off axis, which
    /// keeps the mons and the front of the fold and drops the surface where it turns under.
    /// </summary>
    private const float FoldFacing = 0.5f;

    /// <summary>
    /// Back off the displacement wherever it would turn a triangle inside out or collapse it flat, until
    /// none does. The guarantee that this pass cannot punch a hole through the character.
    /// <para/>
    /// A halving sweep rather than a solve. Each round finds the triangles that have folded under the
    /// displacement so far and halves it on their three vertices, which is enough because the test is
    /// monotone: scaling a vertex's displacement toward zero moves its triangles back toward the shape
    /// they started at, and at zero every one of them is valid by definition. Eight rounds take the worst
    /// offender to a two-hundred-and-fifty-sixth of what it asked for, and in practice one or two rounds
    /// on a handful of vertices is the whole story.
    /// <para/>
    /// It is deliberately the LAST thing that touches the displacement. Every bound above it — the region
    /// ramp, the strength, both slope limits — is a number chosen against the bodies that happened to be
    /// available, and a fold is not a matter of degree: the surface either renders or the player sees the
    /// scenery through their character. So the constants stay tuned for how the result LOOKS, and this
    /// stays responsible for whether it exists.
    /// </summary>
    private static void UnfoldTriangles(Vec3[] pos, Vec3[] delta, int[] tris, Action<string>? log)
    {
        int vc = pos.Length;
        var keep = new float[vc];
        Array.Fill(keep, 1f);

        Vec3 At(int i) => new(pos[i].X + delta[i].X * keep[i],
                              pos[i].Y + delta[i].Y * keep[i],
                              pos[i].Z + delta[i].Z * keep[i]);

        int touched = 0;
        for (int pass = 0; pass < UnfoldPasses; pass++)
        {
            int bad = 0;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (a >= vc || b >= vc || c >= vc) continue;
                if (keep[a] == 0f && keep[b] == 0f && keep[c] == 0f) continue;

                var n0 = TriNormal(pos[a], pos[b], pos[c]);
                float area0 = Len(n0);
                if (area0 <= 1e-12f) continue;      // already degenerate; not this pass's doing

                var n1 = TriNormal(At(a), At(b), At(c));
                bool folded = n0.X * n1.X + n0.Y * n1.Y + n0.Z * n1.Z <= 0f
                           || Len(n1) < area0 * UnfoldMinArea;
                if (!folded) continue;

                keep[a] *= 0.5f; keep[b] *= 0.5f; keep[c] *= 0.5f;
                bad++;
            }
            if (bad == 0) break;
            touched = Math.Max(touched, bad);
        }

        if (touched == 0) return;
        int held = 0;
        for (int i = 0; i < vc; i++)
        {
            if (keep[i] >= 1f) continue;
            delta[i] = new Vec3(delta[i].X * keep[i], delta[i].Y * keep[i], delta[i].Z * keep[i]);
            held++;
        }
        log?.Invoke($"bust bridge: held {held} vertex(es) back to keep {touched} triangle(s) from folding");
    }

    /// <summary>A triangle's un-normalised normal: the cross product of two of its edges, whose direction
    /// says which way it faces and whose length is twice its area.</summary>
    private static Vec3 TriNormal(Vec3 a, Vec3 b, Vec3 c)
    {
        float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        return new Vec3(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
    }

    /// <summary>Rounds of halving <see cref="UnfoldTriangles"/> gets. Each one quarters the worst case, so
    /// eight is a factor of 256 — past any displacement this file can produce.</summary>
    private const int UnfoldPasses = 8;

    /// <summary>How much of its original area a triangle may lose before it counts as collapsed. A sliver
    /// this thin is already invisible; the point is that it must not go through zero and come out the
    /// other side.</summary>
    private const float UnfoldMinArea = 0.02f;

    /// <summary>
    /// <see cref="BustMaxSlope"/>'s sweep for a displacement that is a VECTOR rather than a height: pull
    /// each node's displacement toward its neighbours' until no edge carries more difference than its own
    /// length allows, repeated because one pass propagates one edge.
    /// <para/>
    /// Only ever TOWARD ZERO, the same restriction the scalar sweep carries and for the same reason. A
    /// plain "clamp into the neighbour's window" also drags an untouched node along with a displaced one,
    /// which invents displacement where the region deliberately has none and unpins the boundary.
    /// </summary>
    private static void LimitSlopeVector(Vec3[] v, Vec3[] pos, List<int>[] adj, int count, float maxSlope)
    {
        for (int pass = 0; pass < BustSlopePasses; pass++)
        {
            float worst = 0f;
            for (int n = 0; n < count; n++)
            {
                float here = Len(v[n]);
                if (here <= 0f) continue;
                foreach (int k in adj[n])
                {
                    float dx = pos[k].X - pos[n].X, dy = pos[k].Y - pos[n].Y, dz = pos[k].Z - pos[n].Z;
                    float room = maxSlope * MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    var diff = new Vec3(v[n].X - v[k].X, v[n].Y - v[k].Y, v[n].Z - v[k].Z);
                    float gap = Len(diff);
                    if (gap <= room || gap <= 1e-12f) continue;

                    float back = (gap - room) / gap;
                    var cand = new Vec3(v[n].X - diff.X * back,
                                        v[n].Y - diff.Y * back,
                                        v[n].Z - diff.Z * back);
                    float after = Len(cand);
                    if (after >= here) continue;          // never grow a displacement to satisfy a slope
                    worst = MathF.Max(worst, gap - room);
                    v[n] = cand;
                    here = after;
                }
            }
            if (worst <= BustBridgeEpsilon) break;
        }
    }

    /// <summary>
    /// The other half of the nipple smooth: the same relax applied to the BODY the shell is cut from,
    /// returned as a modified copy of the model. Null when nothing qualified, so the caller publishes
    /// nothing.
    /// <para/>
    /// It has to exist. Smoothing a shell LOWERS it, and a shell rides one millimetre off the skin, so a
    /// smoothed garment sits inside the body and the body's own nipple appears through it — a patch of
    /// bare skin with the point still standing in the middle. There is no way around that from the shell
    /// alone: lifting the patch back out clears the body but restores the nipple exactly, because the lift
    /// is uniform and the largest drop is at the tip.
    /// <para/>
    /// Deliberately the SAME <see cref="BustBridgeSolve"/> the shell uses, on the same coverage. A second
    /// implementation of "how the nipple is smoothed" would drift from the first, and the failure would be
    /// the two surfaces disagreeing by a fraction of a millimetre — which is exactly the size of gap that
    /// shows through.
    /// <para/>
    /// GATED ON COVERAGE, so only skin a garment actually covers is touched. Smoothing the body wherever
    /// the bones say "breast" would take the nipple off a character who is not wearing anything.
    /// <para/>
    /// Positions and normals, in place. Nothing here changes the file's length, so no offset in the header
    /// moves and none of <c>ModelAttributeWriter</c>'s splice-and-shift machinery is needed — the bytes are
    /// copied and overwritten where they sit.
    /// </summary>
    /// <param name="nippleStrength">The nipple relax, on the torso. 0 leaves it alone.</param>
    /// <param name="foldStrength">
    /// The crotch fold, on the legs part. 0 leaves it alone. Both live here rather than in two passes
    /// because they are two regions of one body: a second pass would parse the model again, walk the same
    /// meshes, and rewrite the same bytes to touch a part the first one never reached.
    /// </param>
    internal static byte[]? SmoothBodyNipples(byte[] mdl, SecondSkinLayer gate, float nippleStrength,
                                              Action<string>? log = null, float foldStrength = 0f)
    {
        if (mdl is not { Length: > 0 } || (nippleStrength <= 0f && foldStrength <= 0f)) return null;

        Source src;
        try { src = Parse(mdl); }
        catch { return null; }

        var outBytes = (byte[])mdl.Clone();
        var s = src.S;
        int meshesTouched = 0, vertsMoved = 0, normalsWritten = 0;
        float most = 0f;
        // Hoisted: a stackalloc inside the mesh loop is a stack leak, since the frame only unwinds on
        // return.
        Span<float> tmp = stackalloc float[4];

        // SKIN MESHES ONLY, by the same filter the shell is cut with. A body model is not all skin: it
        // carries the smallclothes, the nails and the PIERCINGS, and a body0 measured here had all three.
        // Relaxing a nipple piercing is not smoothing a breast, it is deforming jewellery.
        var matNames = ReadMaterialNames(s, src);

        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > s.Length) break;
            ushort vc = BitConverter.ToUInt16(s, mo);
            if (vc == 0) continue;

            ushort matIdx = BitConverter.ToUInt16(s, mo + 8);
            if (matIdx >= matNames.Count || SkinMaterialBodyType(matNames[matIdx]) == null) continue;

            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            VElem? pe = null, ne = null, ue = null;
            foreach (var el in decl)
            {
                if (el.Usage == UsePosition) pe ??= el;
                else if (el.Usage == UseNormal) ne ??= el;
                else if (el.Usage == UseUV && el.UsageIndex == 0) ue ??= el;
            }
            if (pe is not { } pos || ne is not { } nrm || ue is not { } uvE) continue;
            if (pos.Stream > 2 || nrm.Stream > 2 || uvE.Stream > 2) continue;

            uint[] vbo = { BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                           BitConverter.ToUInt32(s, mo + 28) };
            byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
            if (bs[pos.Stream] == 0) continue;

            // Every mesh but the torso names neither bust bone and drops out for the cost of one walk
            // over a handful of names — the same early-out the shell's pass leans on. The fold rides the
            // LEGS part rather than the torso, so on a real body the two never meet; asking for both here
            // costs one more walk over the same names and keeps one traversal and one rewrite.
            var bust = nippleStrength > 0f
                ? MeshRegionWeights(src, m, vc, decl, vbo, bs, [BustBoneL, BustBoneR])
                : null;
            var fold = foldStrength > 0f
                ? MeshRegionWeights(src, m, vc, decl, vbo, bs, [HipBone], [ThighBoneL, ThighBoneR])
                : null;
            // The same region without the thigh veto, for the fold's relax. See FoldPlan's `near`.
            var foldNear = fold != null
                ? MeshRegionWeights(src, m, vc, decl, vbo, bs, [HipBone, ThighBoneL, ThighBoneR])
                : null;
            if (bust == null && fold == null) continue;

            ushort subIdx = BitConverter.ToUInt16(s, mo + 10), subCount = BitConverter.ToUInt16(s, mo + 12);
            var tris = MeshTriangles(src, subIdx, subCount);
            if (tris.Length < 3) continue;

            var p3 = new Vec3[vc];
            var n3 = new Vec3[vc];
            var uv = new (float U, float V)[vc];
            for (int i = 0; i < vc; i++)
            {
                ReadTyped(s, src.Vb + (int)vbo[pos.Stream] + i * bs[pos.Stream] + pos.Offset, pos.Type, tmp);
                p3[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
                ReadTyped(s, src.Vb + (int)vbo[nrm.Stream] + i * bs[nrm.Stream] + nrm.Offset, nrm.Type, tmp);
                float nx = tmp[0], ny = tmp[1], nz = tmp[2];
                if (nrm.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                n3[i] = len > 1e-6f ? new Vec3(nx / len, ny / len, nz / len) : new Vec3(0, 0, 1);
                ReadTyped(s, src.Vb + (int)vbo[uvE.Stream] + i * bs[uvE.Stream] + uvE.Offset, uvE.Type, tmp);
                uv[i] = (tmp[0], tmp[1]);
            }

            // Sampled with WRAP, like every other body-UV read here: a body's UVs need not sit in the
            // [0,1] tile, and wrapping makes an integer tile offset irrelevant rather than putting a whole
            // mesh on row 0.
            var covered = CoveredVertices(uv, gate, vc);

            var plan = bust == null ? null
                  // pinBoundary: the body again — see the parameter's note. The bust is nowhere near a
                  // part seam on a real torso, so this is insurance rather than a fix, but it is the same
                  // surface the fold runs on and the two should not disagree about whether a rim moves.
                : BustBridgeSolve(p3, n3, tris, bust, 0f, log, covered, nippleStrength,
                                  pinBoundary: true);

            if (fold != null && foldNear != null)
                plan = MergePlans(plan, FoldPlan(p3, n3, tris, fold, foldNear, covered, foldStrength, log));

            if (plan == null) continue;

            // RESHADE, for the same reason the shell does: a flattened nipple still carrying the nipple's
            // normals reads as a nipple however far the vertices moved. It matters more here than it does
            // on a shell, because the shell is CUT FROM THIS and pushed out along the normal it finds — so
            // a stale normal does not merely mis-shade the skin, it splays the garment above it.
            var finalNrm = RelaxedNormals(p3, n3, plan.Delta, plan.NodeOf, plan.NodeWeight,
                                          plan.NodeNormal, tris);

            int stride = bs[pos.Stream];
            int nStride = bs[nrm.Stream];
            for (int i = 0; i < vc; i++)
            {
                // Position and normal have DIFFERENT tests. A vertex moves only if its own delta is real;
                // it reshades if it is anywhere in the relaxed region, because a normal is a property of
                // the neighbours — a vertex that stayed put beside one that dropped has a new normal.
                var d = plan.Delta[i];
                float mag = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
                if (mag > BustBridgeEpsilon)
                {
                    WriteXYZ(outBytes, src.Vb + (int)vbo[pos.Stream] + i * stride + pos.Offset, pos.Type,
                             p3[i].X + d.X, p3[i].Y + d.Y, p3[i].Z + d.Z);
                    vertsMoved++;
                    most = MathF.Max(most, mag);
                }

                if (plan.NodeWeight[plan.NodeOf[i]] <= 0f) continue;
                var fn = finalNrm[i];
                if (WriteNormal(outBytes, src.Vb + (int)vbo[nrm.Stream] + i * nStride + nrm.Offset,
                                nrm.Type, fn.X, fn.Y, fn.Z))
                    normalsWritten++;
            }
            meshesTouched++;
        }

        if (vertsMoved == 0) return null;
        log?.Invoke($"body smooth: {vertsMoved} vertex(es) across {meshesTouched} mesh(es), "
                  + $"moved by up to {most:0.#####}, {normalsWritten} normal(s) reshaded");
        return outBytes;
    }

    /// <summary>What <see cref="RewriteFaceUv0"/> actually did, for the log line that has to make a wrong
    /// answer visible. <paramref name="Unsided"/> is the one to watch: on a real face it is 0, and anything
    /// else means vertices were sent to the +X half on no evidence at all.</summary>
    internal readonly record struct FaceUvStats(int MeshesTouched, int VerticesWritten, int LodsTouched,
                                                int Conflicted, int Straddling, int MorphSided, int Unsided);

    /// <summary>
    /// Rewrite a FACE model's uv0 into the doubled (<see cref="UVRemapService.FaceSplitSpace"/>) layout, in
    /// place, so the character's own face samples an un-mirrored sheet — the +X side into the right half,
    /// the -X side into the left. Returns a modified copy, or null when nothing qualified.
    /// <para/>
    /// It exists because the alternative — cutting a second-skin shell — cannot carry a whole face. A shell
    /// is emitted with its shape block zeroed (see the ModelHeader writes in the builder), so it is a frozen
    /// duplicate of the head riding <see cref="BaseOffset"/> proud of a face that is still blinking and
    /// talking underneath, and an opaque whole-face texture keeps every triangle of it. Moving the UVs of
    /// the face the game is already drawing keeps the material, the skin tone, the expressions and the
    /// geometry, and adds nothing to the scene.
    /// <para/>
    /// In place and LENGTH-NEUTRAL, exactly like <see cref="SmoothBodyNipples"/>: the file is cloned and
    /// uv0 is overwritten where it sits, in the type the mesh already declares, so no offset in the header
    /// moves and none of <c>ModelAttributeWriter</c>'s splice-and-shift machinery is needed. uv1 is never
    /// touched — the common Half4/Float4 uv0 packs uv1 into its z/w lanes, and only x/y are written.
    /// <para/>
    /// TANGENTS ARE DELIBERATELY LEFT ALONE. The game shades with the tangent frame stored in the model.
    /// Today the -X side's frame is already the mirror of its +X partner's and both sample the same texel;
    /// the doubled sheet's left half is an exact pixel mirror of its right (<see cref="UVRemapService.ExpandMirrored"/>),
    /// so after this rewrite that side reads texels of identical value through an unchanged frame and
    /// symmetric art renders bit-for-bit as it does now. <see cref="RetangentMesh"/> refits frames for the
    /// shell, whose triangles are re-cut; it must not be dragged in here.
    /// <para/>
    /// ALL-OR-NOTHING. Every LOD is rewritten or none is: a face that keeps vanilla UVs at LOD1 samples the
    /// doubled sheet with the wrong coordinates the moment the camera pulls back, and that is invisible in
    /// the mirror. Anything this cannot vouch for — a UV type it cannot write, a buffer span that does not
    /// fit, a mesh whose UVs sit outside the [0,1] tile the affine assumes — returns null, and the caller
    /// falls back to folding the sheet.
    /// </summary>
    /// <param name="keepMaterial">Which meshes to convert, by material name — the face material only.</param>
    /// <param name="convert">
    /// <c>UvConverter(FaceSpace, FaceSplitSpace, unmirror: true)</c>. Taken rather than built so the one
    /// affine the shell path already uses is the one applied here (see <see cref="UVRemapService.UvConverter"/>).
    /// </param>
    internal static byte[]? RewriteFaceUv0(byte[] mdl, Func<string, bool> keepMaterial,
                                           UVRemapService.UvConversion convert,
                                           out FaceUvStats stats, Action<string>? log = null)
    {
        stats = default;
        if (mdl is not { Length: > 0 }) return null;

        Source src;
        try { src = Parse(mdl); }
        catch { return null; }

        var s = src.S;
        uint U32(int o) => BitConverter.ToUInt32(s, o);
        ushort U16(int o) => BitConverter.ToUInt16(s, o);

        // Each LOD's buffers are found from the FILE HEADER's own per-LOD arrays — vertexOffset[3] at 0x10
        // and indexOffset[3] at 0x1C, of which Parse already reads [0] as Vb/Ib. Reading them per LOD is
        // what makes LOD1/LOD2 reachable at all: every other walk in this file is bounded to LOD0 and uses
        // src.Vb, which would address LOD1's meshes into LOD0's buffer.
        int VertexBase(int l) => (int)U32(16 + l * 4);
        int IndexBase(int l) => (int)U32(28 + l * 4);
        long VertexBytes(int l) => U32(0x28 + l * 4);

        // The header read, cross-checked against the one value Parse derived independently. If these
        // disagree the layout is not what this method believes, and every write below would land in
        // someone else's bytes.
        if (mdl.Length < 0x44 || VertexBase(0) != src.Vb || IndexBase(0) != src.Ib) return null;

        var matNames = ReadMaterialNames(s, src);
        var outBytes = (byte[])mdl.Clone();
        // A malformed LOD range that repeats LOD0's meshes would otherwise convert the same vertices twice
        // — u -> 0.5 + u/2 applied twice is 0.75 + u/4, which is not a visible seam so much as a face
        // wearing a quarter of its own texture.
        var done = new HashSet<int>();
        int meshes = 0, written = 0, lods = 0, conflicted = 0, straddled = 0, morphSided = 0, unsided = 0;
        Span<float> tmp = stackalloc float[4];

        for (int lod = 0; lod < 3; lod++)
        {
            int ls = src.LodStart + lod * 60;
            if (ls + 60 > s.Length) break;
            ushort mi = U16(ls), mc = U16(ls + 2);
            if (mc == 0) continue;
            int vbBase = VertexBase(lod), ibBase = IndexBase(lod);
            if (vbBase <= 0 || ibBase <= 0) continue;      // a LOD the file does not actually carry
            long vbBytes = VertexBytes(lod);
            bool touchedLod = false;

            int end = Math.Min(mi + mc, src.MeshCount);
            for (int m = mi; m < end; m++)
            {
                if (!done.Add(m)) continue;
                int mo = src.MeshStart + m * 36;
                if (mo + 36 > s.Length) break;
                ushort vc = U16(mo);
                if (vc == 0) continue;
                ushort matIdx = U16(mo + 8);
                if (matIdx >= matNames.Count || !keepMaterial(matNames[matIdx])) continue;

                var decl = m < src.Decls.Length ? src.Decls[m] : [];
                VElem? pe = null, ue = null;
                foreach (var el in decl)
                {
                    if (el.Usage == UsePosition) pe ??= el;
                    else if (el.Usage == UseUV && el.UsageIndex == 0) ue ??= el;
                }
                if (pe is not { } pos || ue is not { } uvE) continue;
                if (pos.Stream > 2 || uvE.Stream > 2) continue;

                uint[] vbo = { U32(mo + 20), U32(mo + 24), U32(mo + 28) };
                byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
                if (bs[pos.Stream] == 0 || bs[uvE.Stream] == 0) continue;

                // Both spans, inside the file AND inside this LOD's own declared vertex buffer. A refusal
                // here is all-or-nothing rather than a skip: a half-converted face is worse than none.
                //
                // Measured against what ReadTyped will ACTUALLY read for the type each element declares.
                // A fixed 8 under-measures a Float3 position (12 bytes) and a Float4 uv (16), which lets a
                // model through that then reads past the end of its own buffer; the blanket 16 the geometry
                // reader uses over-measures the other way and would refuse a perfectly ordinary tightly
                // packed buffer, whose last element ends exactly at the buffer's end.
                bool Fits(VElem el)
                {
                    long rel = vbo[el.Stream] + (long)(vc - 1) * bs[el.Stream] + el.Offset + TypeWidth(el.Type);
                    return vbBase + rel <= s.Length && (vbBytes <= 0 || rel <= vbBytes);
                }
                if (!Fits(pos) || !Fits(uvE)) return null;

                int posAt = vbBase + (int)vbo[pos.Stream] + pos.Offset, posStride = bs[pos.Stream];
                int uvAt = vbBase + (int)vbo[uvE.Stream] + uvE.Offset, uvStride = bs[uvE.Stream];

                var x = new float[vc];
                var uv = new (float U, float V)[vc];
                float uLo = float.MaxValue, uHi = float.MinValue;
                for (int i = 0; i < vc; i++)
                {
                    ReadTyped(s, posAt + i * posStride, pos.Type, tmp);
                    x[i] = tmp[0];
                    ReadTyped(s, uvAt + i * uvStride, uvE.Type, tmp);
                    uv[i] = (tmp[0], tmp[1]);
                    if (tmp[0] < uLo) uLo = tmp[0];
                    if (tmp[0] > uHi) uHi = tmp[0];
                }

                // The affine reads u as a position in the [0,1] sheet. A mesh tiled outside it — the shell
                // path shifts such a mesh onto the tile per mesh — would be sent somewhere this method has
                // no business sending the player's own face, so refuse the whole model instead.
                if (uLo < -UvTileSlack || uHi > 1f + UvTileSlack)
                {
                    log?.Invoke($"face uv: mesh {m} sits outside the [0,1] tile (u {uLo:F3}..{uHi:F3}) — "
                              + "refusing to rewrite this model");
                    return null;
                }

                ushort subIdx = U16(mo + 10), subCount = U16(mo + 12);
                var tris = MeshTrianglesAt(src, ibBase, subIdx, subCount);
                var sides = SurfaceMirror.AssignSides(x, tris, out int conf, out int strad);
                conflicted += conf;
                straddled += strad;

                // Which vertices a triangle actually names. The rest are morph replacements: they live
                // inside [0, vc) and the game swaps an index entry onto one when a shape is enabled, but no
                // triangle references them as authored, so AssignSides has nothing to go on and leaves them
                // at 0 — the +X branch. Half of every expression would jump to the other half of the sheet.
                var claimed = new bool[vc];
                foreach (var t in tris)
                    if (t < vc) claimed[t] = true;

                // So take the side of the vertex each one REPLACES. LOD0 only, because that is the LOD Parse
                // reads shapes for; a LOD1/LOD2 morph vertex falls through to its own X below, which is the
                // same answer everywhere except the midline band and is not resolvable at that distance.
                if (lod == 0 && src.Shapes.Count > 0)
                {
                    uint meshStartIndex = U32(mo + 16);
                    foreach (var entries in src.Shapes.Values)
                        foreach (var e in entries)
                        {
                            if (e.MeshIndexOffset != meshStartIndex) continue;
                            foreach (var (bIdx, rep) in e.Values)
                            {
                                if (rep >= vc || claimed[rep] || sides[rep] != 0) continue;
                                int slot = ibBase + (int)(meshStartIndex + bIdx) * 2;
                                if (slot < 0 || slot + 2 > s.Length) continue;
                                ushort bv = U16(slot);
                                if (bv >= vc || sides[bv] == 0) continue;
                                sides[rep] = sides[bv];
                                morphSided++;
                            }
                        }
                }

                // Anything still unplaced and named by no triangle answers from its own X. A vertex a
                // triangle DID claim and that still reads 0 is genuinely disputed — two triangles on
                // opposite sides — and keeps the documented +X default rather than being overruled here.
                for (int i = 0; i < vc; i++)
                {
                    if (claimed[i] || sides[i] != 0) continue;
                    sides[i] = x[i] > SurfaceMirror.Midline ? (sbyte)1
                             : x[i] < -SurfaceMirror.Midline ? (sbyte)-1 : (sbyte)0;
                    if (sides[i] == 0) unsided++;
                }

                for (int i = 0; i < vc; i++)
                {
                    if (convert(uv[i].U, uv[i].V, sides[i]) is not { } r) continue;   // unmapped: as authored
                    // Clamped because the affine's ends land a rounding step outside the sheet, and a u of
                    // 1.0005 does not clip — it WRAPS to the far edge and drags the triangle across the face.
                    if (!WriteUv0(outBytes, uvAt + i * uvStride, uvE.Type,
                                  Math.Clamp(r.U, 0f, 1f), r.V))
                    {
                        log?.Invoke($"face uv: mesh {m} declares a uv0 type ({uvE.Type}) this cannot write "
                                  + "— refusing to rewrite this model");
                        return null;
                    }
                    written++;
                }
                meshes++;
                touchedLod = true;
            }
            if (touchedLod) lods++;
        }

        if (written == 0) return null;
        stats = new FaceUvStats(meshes, written, lods, conflicted, straddled, morphSided, unsided);
        log?.Invoke($"face uv: {written} vertex(es) across {meshes} mesh(es) in {lods} LOD(s) "
                  + $"(conflicted {conflicted}, straddling {straddled}, morph-sided {morphSided}, "
                  + $"unsided {unsided})");
        return outBytes;
    }

    /// <summary>
    /// How many bytes <see cref="ReadTyped"/> consumes for a vertex element of this type — the exact number,
    /// so a bounds check neither passes a read that runs off the end nor refuses a buffer that simply ends
    /// where its last element does. 16 (the widest) for a type it does not know, which is the conservative
    /// direction for a check.
    /// </summary>
    private static int TypeWidth(byte type) => type switch
    {
        0 => 4,                      // Float1
        1 => 8,                      // Float2
        2 => 12,                     // Float3
        3 => 16,                     // Float4
        5 or 8 => 4,                 // Ubyte4 / Ubyte4n
        6 or 9 or 13 or 16 => 4,     // Short2 / Short2n / Half2 / Ushort2
        7 or 10 or 14 or 17 => 8,    // Short4 / Short4n / Half4 / Ushort4
        _ => 16,
    };

    /// <summary>How far outside the [0,1] tile a face mesh's u may stray before
    /// <see cref="RewriteFaceUv0"/> refuses it. A real face measures 0.003..0.989; this is for the
    /// rounding at an island's edge, not for a tiled mesh.</summary>
    private const float UvTileSlack = 0.01f;

    /// <summary>
    /// Write uv0 at <paramref name="off"/> in the type the mesh declares, leaving any third and fourth
    /// component alone — those lanes are uv1 on the Half4/Float4 packing FFXIV uses, and the face's uv1 is
    /// none of this method's business. False for a type it will not write, which the caller must treat as
    /// a refusal of the whole model rather than of one mesh.
    /// </summary>
    internal static bool WriteUv0(byte[] a, int off, byte type, float u, float v)
    {
        switch (type)
        {
            case 1: case 3:        // Float2 / Float4
                W32(a, off, (uint)BitConverter.SingleToInt32Bits(u));
                W32(a, off + 4, (uint)BitConverter.SingleToInt32Bits(v));
                return true;
            case 13: case 14:      // Half2 / Half4
                W16(a, off, Half(u));
                W16(a, off + 2, Half(v));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Smooth the nipple out: find each one, then relax a small disc around it — literally what a modeller
    /// does with a relax brush, which is where the shape of this came from and what it is measured against.
    /// <para/>
    /// Relaxing LOWERS a surface, so this runs on the BODY and the shell is cut from the result. Doing it
    /// to the shell instead does not work and cannot be tuned into working: the shell rides one millimetre
    /// off the skin, so a relaxed shell sits inside the body and the body's own nipple stands through it.
    /// Measured on a real capture, the shell finished 1.98mm (worst 3.33mm) BEHIND the skin across the
    /// areola against a correct +1.2mm elsewhere — a well with a point in the middle of it, which is
    /// exactly what it looked like in game.
    /// <para/>
    /// FINDING THE NIPPLE IS THE WHOLE PROBLEM, and it took a hand-edited mesh to settle. On a real body
    /// it is NOT the forward-most point of the breast: measured against a modeller's own relax pass, the
    /// nipples sat at x ±0.0737 while the shell's frontmost vertex was at x −0.0453, 34mm away and a
    /// millimetre further forward. A breast's broad curve out-reaches the small bump sitting on it.
    /// <para/>
    /// What does find it is PROMINENCE — height above the mean of a ring at roughly a nipple's own radius.
    /// The bump wins that even though the curve wins on raw height. Measured on the same mesh, the peak of
    /// it landed 3mm from where the modeller had brushed, symmetrically on both sides. A tighter ring does
    /// not work: at 8–16mm the peak jumped to the collarbone and the armpit, because at that scale other
    /// detail competes.
    /// <para/>
    /// THE OPERATOR IS A 3-D RELAX, and every part of that matters. Three earlier constructions are
    /// recorded so they are not retried, and all three failed for the same underlying reason.
    /// <list type="bullet">
    /// <item>Doming OVER the tip (raise-only, so nothing could come through) cannot work: under raise-only
    /// the tip cannot move, so the bump keeps its height and only its sharpness changes.</item>
    /// <item>Smoothing the whole bust region at a fixed SCALE avoids needing a landmark at all and is too
    /// blunt — it takes the breast's own curvature with it.</item>
    /// <item>Relaxing the HEIGHT FIELD along the span's axis, which is what this used to do. It reads as
    /// lumpy and cannot be tuned out. Most of what makes a relax look smooth is the TANGENTIAL component:
    /// vertices slide along the surface and even out their spacing. Measured against the modeller's own
    /// pass, past 6mm from the tip the tangential move was 1.92mm against 0.61mm along the normal — three
    /// times more sideways than inward. A height-field operator produces exactly none of it, so the uneven
    /// spacing survives untouched and only the depth changes.</item>
    /// </list>
    /// <para/>
    /// IT MUST NOT RUN TO CONVERGENCE. A masked Laplacian at its fixed point is a discrete HARMONIC patch
    /// pinned to the disc's rim, and a harmonic patch over a convex cap is a BOWL — it interpolates the rim
    /// and sits below the original everywhere inside. That is the dished areola with the nipple still
    /// standing in it. The previous version stopped either at a prominence target or when nothing moved;
    /// the target was never reached (54% of a 65% goal) so it always took the second and ran 1345 passes to
    /// full convergence. A brush is a few strokes: it takes off high curvature and leaves low curvature —
    /// the breast — alone. Hence a fixed, small <see cref="NipplePasses"/> and no stopping rule at all.
    /// <para/>
    /// The falloff is applied to the RESULT, not to each pass. Modulating the rate does nothing in the
    /// limit — every node with any weight ends at the same place, so the falloff cancels out entirely and
    /// the disc gets a hard rim. Blending the finished relax is what a brush's falloff actually is.
    /// <para/>
    /// Returns a per-node 3-D displacement, or null if there was nothing to do.
    /// </summary>
    /// <param name="pos">Node positions to relax — the surface AFTER the span, so the two compose.</param>
    /// <param name="h">Height along the chest axis, for the prominence locator only.</param>
    /// <param name="w">How much each node may be smoothed — the region ramp, gated on this layer's
    /// coverage, so the effect fades out at the garment's edge like everything else here.</param>
    /// <param name="ax">The chest's outward axis — the direction the dome floor lifts along.</param>
    /// <param name="onBust">
    /// Every node the bust bones reach, WITHOUT the coverage gate — the breast as it exists, rather than
    /// the part of it this garment happens to cover.
    /// <para/>
    /// Measuring and editing need different sets and conflating them broke the feature outright on a
    /// small garment. Where the nipple is, how proud it stands and what curve the breast has around it are
    /// facts about the body; a bralette does not change them. Read off the coverage-gated region instead,
    /// the dome ring fell mostly outside the cloth and came back with 22 nodes fitting to a curvature of
    /// -10.74/+2.18 — not a dome, so it was rejected on both sides, the floor never ran, the finishing
    /// relax got zero nodes, and the whole pass moved the tip by three tenths of a millimetre.
    /// <para/>
    /// So everything measured here reads <paramref name="onBust"/>, and everything WRITTEN is still gated
    /// by <paramref name="w"/> — which is what keeps the pass off skin no garment covers.
    /// </param>
    private static Vec3[]? NippleSmoothTarget(Vec3[] pos, float[] h, float[] lat, float[] ver, float[] w,
                                              bool[] onBust, int count, List<int>[] adj, Vec3 ax,
                                              float strength, Action<string>? log)
    {
        var measure = new List<int>();
        var region = new List<int>();
        float loLat = float.MaxValue, hiLat = float.MinValue, midLat = 0f;
        for (int n = 0; n < count; n++)
        {
            if (w[n] > 0f) region.Add(n);
            if (!onBust[n]) continue;
            measure.Add(n);
            loLat = MathF.Min(loLat, lat[n]);
            hiLat = MathF.Max(hiLat, lat[n]);
            midLat += lat[n];
        }
        if (measure.Count < MinBustBridgeNodes || region.Count == 0) return null;
        midLat /= measure.Count;

        // Every distance here is a fraction of the bust's own width, so it means the same thing on any
        // body. The ring is a nipple's radius; the disc is what the brush covered.
        float extent = hiLat - loLat;
        if (extent <= 1e-6f) return null;
        float ringIn = extent * NippleRingInner, ringOut = extent * NippleRingOuter;
        float radius = extent * NippleDiscRadius;

        float Across(int i, int j)
        {
            float du = lat[i] - lat[j], dv = ver[i] - ver[j];
            return MathF.Sqrt(du * du + dv * dv);
        }

        // The nipple on each side: the node standing proudest of a ring around it.
        int nipL = -1, nipR = -1;
        float promL = 0f, promR = 0f;
        foreach (int n in measure)
        {
            float sum = 0f;
            int ring = 0;
            foreach (int k in measure)
            {
                float r = Across(n, k);
                if (r < ringIn || r > ringOut) continue;
                sum += h[k];
                ring++;
            }
            if (ring < 4) continue;
            float prom = h[n] - sum / ring;
            if (lat[n] < midLat) { if (prom > promL) { promL = prom; nipL = n; } }
            else                 { if (prom > promR) { promR = prom; nipR = n; } }
        }
        if (nipL < 0 && nipR < 0)
        {
            log?.Invoke("nipple smooth: nothing stands proud of its surroundings — nothing to smooth");
            return null;
        }

        // How much each node is relaxed: full at a nipple, nothing at the disc's edge.
        var amount = new float[count];
        int touched = 0;
        foreach (int n in region)
        {
            float best = 0f;
            foreach (int nip in stackalloc[] { nipL, nipR })
            {
                if (nip < 0) continue;
                float r = Across(n, nip) / radius;
                if (r >= 1f) continue;
                best = MathF.Max(best, 1f - Smoothstep(r));
            }
            if (best <= 0f) continue;
            amount[n] = best * w[n];
            touched++;
        }
        if (touched == 0) return null;

        // THE BRUSH. Move every node in the patch toward the centroid of its neighbours, in 3-D, a fixed
        // small number of times. Uniform lambda — the falloff is applied to the finished result below, not
        // here, for the reason in the summary.
        //
        // Jacobi, not Gauss-Seidel: `cur` is read and `next` is written, so the answer does not depend on
        // the order nodes happen to sit in the array. Lambda stays at or below 0.5 because a plain
        // "replace with the neighbour average" (lambda 1) has eigenvalue -1 on the checkerboard mode and
        // oscillates rather than converging.
        var patch = region.Where(n => amount[n] > 0f).ToList();
        var cur = (Vec3[])pos.Clone();
        var next = (Vec3[])pos.Clone();
        for (int pass = 0; pass < NipplePasses; pass++)
        {
            foreach (int n in patch)
            {
                if (adj[n] is not { Count: > 0 } near) continue;
                float sx = 0f, sy = 0f, sz = 0f;
                foreach (int k in near) { sx += cur[k].X; sy += cur[k].Y; sz += cur[k].Z; }
                float inv = 1f / near.Count;
                next[n] = new Vec3(
                    cur[n].X + (sx * inv - cur[n].X) * NippleRelaxLambda,
                    cur[n].Y + (sy * inv - cur[n].Y) * NippleRelaxLambda,
                    cur[n].Z + (sz * inv - cur[n].Z) * NippleRelaxLambda);
            }
            (cur, next) = (next, cur);
        }

        // Falloff and strength on the RESULT. Turning the strength down then keeps the same smoothing and
        // travels less of the way toward it, instead of changing what the brush does.
        var delta = new Vec3[count];
        foreach (int n in patch)
        {
            float a = amount[n] * strength;
            delta[n] = new Vec3((cur[n].X - pos[n].X) * a, (cur[n].Y - pos[n].Y) * a, (cur[n].Z - pos[n].Z) * a);
        }

        // ── the dome floor ──────────────────────────────────────────────────────────────────────
        //
        // A nipple sits in a TROUGH, and the relax cannot fill it. Measured on a real body as height
        // above the mean of a ring 4-7mm away: the untouched breast reads +2.0mm at the tip and then
        // -1.36, -1.46, -1.19mm through 5-13mm — a dish ringing the nipple. Shaving the tip leaves that
        // ring behind, which is the "still slightly concave, it should still be convex slightly" report.
        //
        // Widening the relax to cover it does not work and the numbers say why: a bigger disc lowers the
        // surround, so the tip stands PROUDER relative to it (0.65mm at a 10mm disc, 1.04mm at an 18mm
        // one) and the nipple comes back. The two jobs are opposed, so they are two passes.
        //
        // This one only ever RAISES, toward a quadric fitted to the breast beyond it and extrapolated
        // inward. That makes it safe by construction: it cannot dish anything, and it cannot invent a
        // bulge, because its ceiling is the breast's own curve. The lift also goes to zero on its own at
        // the annulus, where the surface IS the fit, so the region needs no falloff of its own — the edge
        // is continuous by construction rather than by tapering.
        //
        // Skipped rather than forced when the fit is not a dome. A garment covering half a breast leaves
        // a crescent, and extrapolating a quadric off one is how a "fix" invents a bulge that was never
        // there.
        float floorRadius = radius * NippleDomeReach;
        float domeCap = MathF.Max(promL, promR);
        int lifted = 0, shortNodes = 0;
        float mostLift = 0f, shortfall = 0f;
        // Which nodes this pass has any business touching, and how much — the floor's own reach, faded.
        // The finishing relax below borrows it rather than working over the whole bust region: with no
        // coverage map the region is the entire chest, and smoothing all of it spread the edit over 188mm
        // of body when the feature being fixed is 20mm across.
        var finishW = new float[count];
        foreach (int nip in stackalloc[] { nipL, nipR })
        {
            if (nip < 0) continue;
            // Sampled off the BUST, not off the covered part of it — see the onBust note.
            var ring = new List<(double, double, double)>();
            foreach (int n in measure)
            {
                float r = Across(n, nip);
                if (r < radius * NippleDomeRingInner || r > radius * NippleDomeRingOuter) continue;
                ring.Add((lat[n] - lat[nip], ver[n] - ver[nip], h[n]));
            }
            var q = FitQuadric(ring);
            if (q == null || q[3] >= 0 || q[5] >= 0)
            {
                log?.Invoke($"nipple smooth: no dome to fill toward on one side ({ring.Count} ring node(s)"
                          + (q == null ? ", fit failed" : $", curvature {q[3]:0.##}/{q[5]:0.##} is not convex")
                          + ") — that side keeps whatever trough it has");
                continue;
            }
            // A quadric extrapolated inward from a ring can run away if the ring stopped describing the
            // breast — swept far enough out it starts taking in the chest wall and the underbust, and the
            // fit stops being a breast at all. The apex is the sanity check: the dome may reach the
            // nipple's own height, since that is the surface it is continuing, but a dome that wants to
            // stand PROUD of where the nipple was is extrapolating, not fitting.
            if (q[0] > h[nip])
            {
                log?.Invoke($"nipple smooth: the dome fit on one side extrapolates past the nipple itself "
                          + $"({q[0]:0.#####} against {h[nip]:0.#####}) — discarded, that side keeps its trough");
                continue;
            }

            foreach (int n in region)
            {
                float rr = Across(n, nip);
                if (rr > floorRadius) continue;
                // Each node belongs to its nearer nipple, so the two domes cannot fight over the nodes
                // between them.
                if (nipL >= 0 && nipR >= 0 && rr > Across(n, nip == nipL ? nipR : nipL)) continue;

                // TAPER THE EDGE. "The lift goes to zero on its own where the surface meets the fit, so
                // the region needs no falloff" was wrong, and the silhouette showed it: a 3.6mm step at
                // the boundary and a cliff below the breast. A quadric is not a good enough model of a
                // breast for the fit to actually meet the surface at its own inner edge, so the lift
                // arrives at the boundary still worth millimetres. Faded explicitly instead.
                float edge = rr / floorRadius;
                float fade = edge <= NippleDomeSolid ? 1f
                           : 1f - Smoothstep((edge - NippleDomeSolid) / (1f - NippleDomeSolid));
                finishW[n] = MathF.Max(finishW[n], w[n] * fade);

                double u = lat[n] - lat[nip], v = ver[n] - ver[nip];
                double dome = q[0] + q[1] * u + q[2] * v + q[3] * u * u + q[4] * u * v + q[5] * v * v;
                // Measured against where the relax has already left it, so the two compose instead of
                // the floor undoing the shave.
                float here = (pos[n].X + delta[n].X) * ax.X
                           + (pos[n].Y + delta[n].Y) * ax.Y
                           + (pos[n].Z + delta[n].Z) * ax.Z;
                // TOWARD the dome, from either side. Raise-only was tried and is self-defeating: it fills
                // the trough, but it also lifts the tip the relax has just brought down, so the nipple
                // came back — measured at 1.06mm proud against 0.65mm with no floor at all. The relax and
                // the floor were pulling against each other on the same nodes.
                //
                // The dome is what this whole patch should BE: the breast's own curve, continued through
                // where the nipple was. So the height goes to it, and the relax's job narrows to what
                // only it can do — sliding vertices tangentially so the patch is evenly spaced.
                // BOUNDED BY THE BUMP. The trough a nipple sits in is a feature of that nipple, so it
                // cannot be deeper than the nipple is tall — and a quadric extrapolated inward can be, if
                // the ring stopped describing a breast. The apex guard above only tests the dome AT the
                // nipple and so misses a fit that is sane there and wild further out: on a second body it
                // asked for 27mm of lift, against a nipple standing 19mm proud.
                float want = Math.Clamp((float)dome - here, -domeCap, domeCap);
                float lift = want * w[n] * strength * fade;
                if (want > 0f) { shortfall += want - lift; shortNodes++; }
                if (MathF.Abs(lift) <= 1e-7f) continue;
                delta[n] = new Vec3(delta[n].X + ax.X * lift,
                                    delta[n].Y + ax.Y * lift,
                                    delta[n].Z + ax.Z * lift);
                lifted++;
                mostLift = MathF.Max(mostLift, MathF.Abs(lift));
            }
        }

        // ── the light relax ─────────────────────────────────────────────────────────────────────
        //
        // The body arrives BUMPY at vertex scale and nothing above fixes it. Measured as the mean offset
        // of a vertex from its own neighbours' centroid, over the 20mm around a nipple: the upstream body
        // reads 0.192mm and this pass had been leaving 0.162mm, against 0.022mm on the same geometry after
        // a modeller's light relax. So the shape was right and the surface was still lumpy.
        //
        // The reason none of the passes above removed it is that every one of them lands as
        // `position + (target - position) * weight`, and wherever that weight is short of 1 — which is
        // most of a falloff — the original's roughness survives in proportion. Smoothing the RESULT is the
        // only place it can be taken out.
        //
        // A PLAIN Laplacian, and Taubin is specifically wrong for this. What makes a surface read as
        // smooth at vertex scale is largely that each vertex sits at its neighbours' centroid, and moving
        // it there is a tangential correction as much as a normal one. Taubin's whole design is to undo
        // part of that so it cannot lose shape — which is right for de-jaggying and wrong here: over the
        // same geometry it stalled at 0.119mm however long it ran, against 0.022mm for the modeller's own
        // relax and 0.192mm untouched.
        //
        // Its shrinkage is not worth protecting against at this scale. A Laplacian pulls a sphere of
        // radius R in by about λh²/4R per pass; on a 50mm breast at 0.8mm spacing that is under two
        // hundredths of a millimetre across the whole pass, which is far below what any of this moves on
        // purpose.
        if (NippleFinishPasses > 0)
        {
            var cur2 = new Vec3[count];
            var next2 = new Vec3[count];
            for (int n = 0; n < count; n++)
                cur2[n] = new Vec3(pos[n].X + delta[n].X, pos[n].Y + delta[n].Y, pos[n].Z + delta[n].Z);
            Array.Copy(cur2, next2, count);

            // KEPT SHORT rather than made safe. Two ways of removing the shape cost were measured and
            // both are worse than simply not running long enough to incur it:
            //
            // Taubin stalls at 0.119mm however long it runs — the negative step that protects its shape
            // is the same one that undoes the redistribution this is for. Projecting out the normal
            // component (a purely tangential relax, shape-free by construction) plateaus at 0.075mm and
            // slides vertices up to 10mm ALONG the surface, which drags their UVs with them and smears
            // the texture.
            //
            // At six passes the plain step reaches 0.059mm and adds 0.06mm to the largest displacement in
            // the whole pass — the shrinkage only becomes real past about twelve, where the same figure
            // starts climbing through 7mm and the breast quietly deflates.
            var finishNodes = region.Where(n => finishW[n] > 0f).ToList();
            for (int pass = 0; pass < NippleFinishPasses; pass++)
            {
                foreach (int n in finishNodes)
                {
                    if (adj[n] is not { Count: > 0 } near) continue;
                    float sx = 0f, sy = 0f, sz = 0f;
                    foreach (int j in near) { sx += cur2[j].X; sy += cur2[j].Y; sz += cur2[j].Z; }
                    float inv = 1f / near.Count;
                    next2[n] = new Vec3(cur2[n].X + (sx * inv - cur2[n].X) * NippleFinishLambda,
                                        cur2[n].Y + (sy * inv - cur2[n].Y) * NippleFinishLambda,
                                        cur2[n].Z + (sz * inv - cur2[n].Z) * NippleFinishLambda);
                }
                (cur2, next2) = (next2, cur2);
            }

            // Faded by coverage and strength like everything else, so it stops at the garment's edge.
            foreach (int n in finishNodes)
            {
                float a = finishW[n] * strength;
                if (a <= 0f) continue;
                float bx = pos[n].X + delta[n].X, by = pos[n].Y + delta[n].Y, bz = pos[n].Z + delta[n].Z;
                delta[n] = new Vec3(delta[n].X + (cur2[n].X - bx) * a,
                                    delta[n].Y + (cur2[n].Y - by) * a,
                                    delta[n].Z + (cur2[n].Z - bz) * a);
            }

            if (log != null)
            {
                // Measured on the graph the pass actually smooths, over the nodes it actually touched —
                // an export re-welds and re-splits, and that has made this number lie before.
                double Rough(Func<int, Vec3> at)
                {
                    double sum = 0; int n2 = 0;
                    foreach (int n in patch)
                    {
                        if (adj[n] is not { Count: > 2 } near) continue;
                        float sx = 0f, sy = 0f, sz = 0f;
                        foreach (int j in near) { var q2 = at(j); sx += q2.X; sy += q2.Y; sz += q2.Z; }
                        float inv = 1f / near.Count;
                        var me = at(n);
                        sum += Len(new Vec3(sx * inv - me.X, sy * inv - me.Y, sz * inv - me.Z));
                        n2++;
                    }
                    return n2 == 0 ? 0 : sum / n2;
                }
                float meanW = patch.Count == 0 ? 0f : patch.Average(n => w[n]);
                log($"nipple smooth: finish {NippleFinishPasses} pass(es) over {finishNodes.Count} node(s), "
                  + $"roughness {Rough(n => pos[n]):0.######} -> "
                  + $"{Rough(n => new Vec3(pos[n].X + delta[n].X, pos[n].Y + delta[n].Y, pos[n].Z + delta[n].Z)):0.######}"
                  + $", mean coverage weight {meanW:0.###}");
            }
        }

        float most = 0f;
        foreach (int n in region) most = MathF.Max(most, Len(delta[n]));

        int tip = promL >= promR ? nipL : nipR;
        string Where(int n) => n < 0 ? "none"
            : $"({pos[n].X:0.###},{pos[n].Y:0.###},{pos[n].Z:0.###})";
        log?.Invoke($"nipple smooth: {measure.Count} bust node(s), {region.Count} of them covered; "
                  + $"nipples {Where(nipL)} prom {promL:0.#####} / {Where(nipR)} prom "
                  + $"{promR:0.#####}, ring {ringIn:0.####}-{ringOut:0.####}, disc {radius:0.####}, "
                  + $"{touched} node(s) over {NipplePasses} pass(es), {lifted} filled toward the breast's "
                  + $"own curve by up to {mostLift:0.#####} (left {(shortNodes > 0 ? shortfall / shortNodes : 0f):0.#####} "
                  + $"short on average over {shortNodes}), tip moved "
                  + $"{(tip >= 0 ? Len(delta[tip]) : 0f):0.#####}, up to {most:0.#####}");
        return delta;
    }

    /// <summary>
    /// The ring a node's prominence is measured against, as a fraction of the bust's lateral extent —
    /// roughly a nipple's own radius out to twice it.
    /// <para/>
    /// Not smaller. At half these values the peak moved off the breast entirely, onto the collarbone and
    /// the armpit, because at that scale a body has plenty of other detail to compete with. At these it
    /// landed within 3mm of where a modeller brushed, on both sides.
    /// </summary>
    private const float NippleRingInner = 0.045f, NippleRingOuter = 0.09f;

    /// <summary>
    /// The relaxed disc's radius, as a fraction of the bust's lateral extent — about 10mm on the body this
    /// was fitted against, which is the "roughly twice the nipple" a modeller reaches for.
    /// <para/>
    /// THIS is the constant that decides whether the result follows the breast. Past a few hundred passes
    /// the relax has converged inside the disc, so the displacement is just the falloff's own shape; the
    /// radius therefore sets the PROFILE and <see cref="NipplePasses"/> only sets its depth.
    /// <para/>
    /// Fitted on the profile's SHAPE — each band as a fraction of the tip — against the modeller's pass,
    /// because that is what the eye reads. An earlier fit minimised absolute band error instead and chose
    /// 0.055, which matched the millimetres while taking proportionally too much off the surround and too
    /// little off the peak: a flat spot on a curved breast, reported as caving in rather than following it.
    /// <para/>
    /// Nudged from 0.044 to here once the dome floor existed, on measured convexity rather than on that
    /// shape fit. This disc no longer decides the result by itself — the floor sets how the surround
    /// finishes — and what this one still owns is how proud the tip is left, which comes out lowest here.
    /// </summary>
    private const float NippleDiscRadius = 0.050f;

    /// <summary>
    /// How far each relax pass moves a vertex toward its neighbours' average. UNDER-RELAXED, and that is
    /// not a refinement — at 1 the pass does not converge at all.
    /// <para/>
    /// Taking the neighbour average outright is a Jacobi step with λ = 1, whose highest-frequency mode has
    /// eigenvalue −1: it flips sign every pass instead of decaying. The falloff meant that only the middle
    /// of the patch ran at 1, so the surroundings smoothed normally while the tip sat there oscillating —
    /// a bump that would not move, inside a recess that did, which is exactly how it was reported. The tip
    /// netted 0.3mm in 400 passes.
    /// <para/>
    /// Halving the step makes every mode decay monotonically, which is the textbook cure and costs only
    /// twice as many passes.
    /// </summary>
    private const float NippleRelaxLambda = 0.5f;

    /// <summary>
    /// How many relax passes. A FIXED count, because that is what a brush is — a few strokes, then the
    /// modeller lifts the pen. There is deliberately no stopping rule: every version that had one ran to
    /// convergence instead, and a converged masked Laplacian is a harmonic patch, which over a convex
    /// breast is a bowl. See <c>NippleSmoothTarget</c>.
    /// <para/>
    /// Sets the DEPTH only; <see cref="NippleDiscRadius"/> sets the shape. It saturates — on the fitting
    /// body 1000 passes reached 2.55mm at the tip and 1400 reached 2.70mm, against the modeller's 3.17mm —
    /// because once the relax has converged inside the disc there is nothing left to take.
    /// <para/>
    /// Large because the feature is large next to the vertex spacing: a Laplacian attenuates a wavelength
    /// L at a rate set by (spacing/L)², and a 10mm nipple on a 1mm mesh needs hundreds of passes to move
    /// at all. Twelve did essentially nothing (0.12mm). It is still only a few million float operations on
    /// a patch of about a thousand nodes.
    /// </summary>
    private const int NipplePasses = 1400;

    /// <summary>Hermite smoothstep, clamped.</summary>
    private static float Smoothstep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>One [1,2,1] pass over a band series, ends held.</summary>
    private static void Smooth1D(float[] a, int count)
    {
        if (count < 3) return;
        var src = new float[count];
        Array.Copy(a, src, count);
        for (int i = 1; i < count - 1; i++)
            a[i] = (src[i - 1] + 2f * src[i] + src[i + 1]) * 0.25f;
    }


    /// <summary>
    /// The region's principal direction ACROSS <paramref name="ax"/> — on a chest, the line from one
    /// breast to the other. Derived from the geometry in hand rather than taken as model-space X, because
    /// nothing else in this pass assumes a model-space convention and a caller on another surface would
    /// get a plainly wrong answer from a constant.
    /// </summary>
    private static Vec3? PrincipalAcross(Vec3[] pos, float[] weight, int count, Vec3 ax)
    {
        var mid = default(Vec3);
        float n = 0;
        for (int i = 0; i < count; i++)
        {
            if (weight[i] <= 0f) continue;
            mid = new Vec3(mid.X + pos[i].X, mid.Y + pos[i].Y, mid.Z + pos[i].Z);
            n++;
        }
        if (n < 2f) return null;
        mid = new Vec3(mid.X / n, mid.Y / n, mid.Z / n);

        Basis(ax, out var bu, out var bv);
        float suu = 0f, svv = 0f, suv = 0f;
        for (int i = 0; i < count; i++)
        {
            if (weight[i] <= 0f) continue;
            var d = new Vec3(pos[i].X - mid.X, pos[i].Y - mid.Y, pos[i].Z - mid.Z);
            float a = d.X * bu.X + d.Y * bu.Y + d.Z * bu.Z;
            float b = d.X * bv.X + d.Y * bv.Y + d.Z * bv.Z;
            suu += a * a; svv += b * b; suv += a * b;
        }
        // Leading eigenvector of the 2x2 covariance, in closed form.
        float theta = 0.5f * MathF.Atan2(2f * suv, suu - svv);
        float cu = MathF.Cos(theta), cv = MathF.Sin(theta);
        return Normalize(new Vec3(bu.X * cu + bv.X * cv, bu.Y * cu + bv.Y * cv, bu.Z * cu + bv.Z * cv));
    }

    /// <summary>Weighted mean of a per-node vector, unnormalized.</summary>
    private static Vec3 WeightedMean(Vec3[] v, float[] w, int count)
    {
        var acc = default(Vec3);
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            acc = new Vec3(acc.X + v[n].X * w[n], acc.Y + v[n].Y * w[n], acc.Z + v[n].Z * w[n]);
        }
        return acc;
    }

    /// <summary>
    /// How deep the dish between the two apexes was, and how much of it is left — the number that says
    /// whether the bridge WORKED. "N moved" only says it did something, and a run that stalls half way
    /// (too few passes, or a region cut in two by a neckline so the sides never see each other) moves
    /// plenty of vertices while leaving the dish it was meant to remove.
    /// <para/>
    /// Measured ONLY along the cleavage — nodes within <see cref="ChordBand"/> of the segment joining the
    /// apexes. Measuring the whole region against that one line is the obvious version and it is
    /// meaningless: the top of a breast near the collarbone and its underside at the ribcage both project
    /// between the apexes and both sit a long way behind the line, quite correctly. On a real torso that
    /// reported a residual of 0.079 against an apex gap of 0.156 for a bridge that had done its job.
    /// <para/>
    /// Lobes are separated along <paramref name="lateral"/> — the same direction the relax spans, so the
    /// report cannot disagree with the solve about which way the cleavage runs.
    /// </summary>
    private static string ChordReport(Vec3[] start, float[] h0, float[] h, float[] w, int count,
                                      Vec3 ax, Vec3 lateral)
    {
        // Lateral direction: the widest spread of the region, taken across the outward axis. On a chest
        // that is left-to-right, and it is derived the same way the axis is.
        var mid = default(Vec3);
        float wsum = 0f;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            mid = new Vec3(mid.X + start[n].X, mid.Y + start[n].Y, mid.Z + start[n].Z);
            wsum++;
        }
        if (wsum < 2f) return "";
        mid = new Vec3(mid.X / wsum, mid.Y / wsum, mid.Z / wsum);

        int apexL = -1, apexR = -1;
        float bestL = float.MinValue, bestR = float.MinValue;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            var d = new Vec3(start[n].X - mid.X, start[n].Y - mid.Y, start[n].Z - mid.Z);
            float lat = d.X * lateral.X + d.Y * lateral.Y + d.Z * lateral.Z;
            if (lat >= 0f) { if (h0[n] > bestR) { bestR = h0[n]; apexR = n; } }
            else           { if (h0[n] > bestL) { bestL = h0[n]; apexL = n; } }
        }
        if (apexL < 0 || apexR < 0) return "";

        // The dish along the chord, before and after. Which nodes count is decided ACROSS the axis only —
        // their depth along it is the very thing being measured, so letting it into the "is this node on
        // the cleavage" test excludes exactly the nodes the report exists to look at. Measured in full 3D
        // it did: the sternum sits 0.05 behind the nipple line on a real torso against a band of 0.019, so
        // every deep node was filtered out and what came back was the dish beside the nipples, which is
        // convex, does not move, and reported an unchanged 0.01641 for a bridge that had filled 0.012 of it.
        Vec3 Across(Vec3 p)
        {
            float along = p.X * ax.X + p.Y * ax.Y + p.Z * ax.Z;
            return new Vec3(p.X - ax.X * along, p.Y - ax.Y * along, p.Z - ax.Z * along);
        }

        var pl = start[apexL];
        var pr = start[apexR];
        var seg = Across(new Vec3(pr.X - pl.X, pr.Y - pl.Y, pr.Z - pl.Z));
        float span = seg.X * seg.X + seg.Y * seg.Y + seg.Z * seg.Z;
        if (span <= 1e-12f) return "";
        float band = ChordBand * ChordBand * span;

        // Mean AND worst, because they answer different questions and the worst alone misleads. On a real
        // torso the deepest sternum node came within 0.005 of the chord while the reported max stayed at
        // 0.044, held up by a single node at the edge of the band that the fit could not place — which
        // reads as "the bridge did nothing" when almost all of it had worked.
        float wasMax = 0f, nowMax = 0f;
        double wasSum = 0, nowSum = 0;
        int sampled = 0;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            var d = Across(new Vec3(start[n].X - pl.X, start[n].Y - pl.Y, start[n].Z - pl.Z));
            float t = (d.X * seg.X + d.Y * seg.Y + d.Z * seg.Z) / span;
            if (t <= 0f || t >= 1f) continue;
            // How far off the apex-to-apex line it sits, across the axis — on a chest, how far above or
            // below the nipple line. Further off than the band and the chord says nothing about it.
            var perp = new Vec3(d.X - seg.X * t, d.Y - seg.Y * t, d.Z - seg.Z * t);
            if (perp.X * perp.X + perp.Y * perp.Y + perp.Z * perp.Z > band) continue;
            float chord = h0[apexL] + (h0[apexR] - h0[apexL]) * t;
            float a = MathF.Max(0f, chord - h0[n]), b = MathF.Max(0f, chord - h[n]);
            wasMax = MathF.Max(wasMax, a); nowMax = MathF.Max(nowMax, b);
            wasSum += a; nowSum += b;
            sampled++;
        }
        string where = $", apexes ({pl.X:0.###},{pl.Y:0.###},{pl.Z:0.###})-({pr.X:0.###},{pr.Y:0.###},{pr.Z:0.###})"
                     + $" gap {MathF.Sqrt(span):0.####}";
        if (sampled == 0) return where + ", nothing on the chord to measure";
        return where + $", dish mean {wasSum / sampled:0.#####} -> {nowSum / sampled:0.#####}"
                     + $", worst {wasMax:0.#####} -> {nowMax:0.#####}, over {sampled} node(s)";
    }

    /// <summary>
    /// How far off the apex-to-apex segment a node may be and still count as part of the cleavage —
    /// measured ACROSS the chest axis, as a fraction of the segment's length. Only used for reporting.
    /// </summary>
    private const float ChordBand = 0.12f;

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
    /// Rotate a rim loop to start near angle zero and run counter-clockwise, WITHOUT reordering it —
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

    /// <summary>"PTCB" — the authored cap's binding to the body, see <see cref="BakeCapBind"/>.</summary>
    private const uint CapBindMagic = 0x42435450;

    /// <summary>
    /// Where the authored cap sits, expressed so it survives a change of foot: per vertex a coordinate in
    /// the BODY's UV atlas, how far off that surface it sits along the normal, and which side of the body
    /// it is on. Every body shares the atlas — that is the premise the whole overlay system rests on — so
    /// a vertex recorded this way can be put back on any foot in any shape.
    /// <para/>
    /// The side matters because the atlas is MIRRORED: measured on this body, the cap vertices at
    /// x = +0.0440 and x = -0.0440 both land on uv (0.874, 0.297). A coordinate alone would be ambiguous
    /// between the two feet.
    /// <para/>
    /// The reference foot is NOT shipped and must not be — it is somebody's body mod, and bundling it
    /// would redistribute it. Only these four numbers per vertex travel.
    /// </summary>
    /// <param name="offsetsFrom">
    /// An existing binding to take the OFFSETS from, leaving only the atlas coordinate to be measured
    /// here. The coordinate has to be per-body, because two bodies parameterise the same layout
    /// differently; the offset must not be, because it is how high the cap was MODELLED above the skin.
    /// Re-measuring it against another body bakes that body's shape difference into the cap — on Rue,
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
        // Every body model carries its OWN [0,1] atlas — the feet and the torso both use the whole square
        // — so an atlas coordinate is meaningless without knowing which one it belongs to. Without this
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
                // be covered by more than one triangle — the sole and the top of a toe can be packed over
                // each other — and the two candidates face opposite ways. Without it the round trip onto
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
                // The height the cap was MODELLED at, where one is on offer — see offsetsFrom.
                if (authored != null && vi < authored.Length) off = authored[vi];

                int side = p.X >= 0f ? 1 : -1;

                // WHAT THE PLACEMENT WILL ACTUALLY REBUILD, and the difference from what was authored.
                // The atlas coordinate is recovered by inverting the UV parameterisation, which is not
                // the operation that produced it (that was a closest-point in 3D), so the two disagree
                // wherever the atlas is compressed or a coordinate is shared by more than one triangle.
                // Alone that is sub-millimetre, but the offset multiplies it, and the offset is largest
                // exactly over the toenails — the nails are their own mesh and not in the skin surface,
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
        head.Write(2);   // version — 2 adds the tangent-frame residual, see the note at the write site
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

        /// <summary>How many vertices were actually resolved — all of them, or the sample when scoring.</summary>
        public required int Considered;
    }

    /// <summary>
    /// Fit the authored cap to the body actually equipped, by looking each baked atlas coordinate back up
    /// on it and stepping off along the normal there. This is what makes one authored cap work on a foot
    /// it was never modelled against — a heeled foot sits a median of 0.067 from the flat one it was
    /// authored on, which is about eighteen edge lengths and reads in game as the cap floating clear of
    /// the toes altogether.
    /// </summary>
    /// <param name="stride">
    /// Resolve only every n-th vertex. For scoring a binding against a body, where the hit rate is all
    /// that is wanted and a full placement is every cap vertex against every skin triangle.
    /// </param>
    /// <summary>
    /// How closely a placed cap reproduces the shape it was authored as - the mean distance from each
    /// placed vertex to the same vertex of the cap's own model.
    /// <para/>
    /// On the body a binding was baked against this is very nearly zero, because that is precisely what
    /// the binding stores. On any other body it cannot be: the offsets and the residual were measured on
    /// a foot shaped differently, and the reconstruction lands somewhere else. It is therefore an
    /// identity test rather than a quality one - "was this cap made for the body being worn" - which is
    /// the question bone coverage cannot answer for two bodies sharing a skeleton.
    /// <para/>
    /// float.MaxValue when the cap will not parse or its vertex count does not line up, so a cap that
    /// cannot be checked never wins on this.
    /// </summary>
    private static float CapRoundTrip(IReadOnlyList<CapPlacement> placed, byte[] capMdl)
    {
        try
        {
            var src = ParseCached(capMdl);
            double sum = 0; int n = 0;
            foreach (var pl in placed)
            {
                ReadCapVertices(src, pl.Mesh, out var authored, out _);
                int c = Math.Min(authored.Length, pl.Pos.Length);
                for (int i = 0; i < c; i++)
                {
                    if (pl.Pos[i] is { X: 0, Y: 0, Z: 0 }) continue;   // never placed; says nothing
                    sum += Dist(pl.Pos[i], authored[i]);
                    n++;
                }
            }
            return n == 0 ? float.MaxValue : (float)(sum / n);
        }
        catch
        {
            return float.MaxValue;
        }
    }

    /// <summary>
    /// How unevenly a placed cap sits off the body's skin, as the spread of its standoffs between the
    /// tenth and ninetieth percentile.
    /// <para/>
    /// This is what separates two caps that a body can equally carry. A cap on the foot it was modelled
    /// for stands off it by very nearly one figure everywhere, whatever that figure is; the same cap on
    /// a foot with different toes lands parts of itself under the skin and parts of it in the air, and
    /// the spread widens even though every vertex still found somewhere to land.
    /// <para/>
    /// Percentiles rather than the extremes, because a handful of vertices at the rim of any cap sit
    /// oddly on any body and should not decide which cap a whole foot gets.
    /// </summary>
    private static float CapStandoffSpread(IReadOnlyList<CapPlacement> placed, List<SkinTri> skin,
                                           out float median)
    {
        median = 0f;
        if (skin.Count == 0) return float.MaxValue;
        var d = new List<float>();
        foreach (var pl in placed)
            foreach (var q in pl.Pos)
            {
                if (q is { X: 0, Y: 0, Z: 0 }) continue;      // never placed
                if (!NearestOnSkin(q, skin, CapStandoffReach, out var at)) continue;
                d.Add(Dist(q, at));
            }
        if (d.Count < 8) return float.MaxValue;
        d.Sort();
        median = d[d.Count / 2];
        return d[(int)(d.Count * 0.90f)] - d[(int)(d.Count * 0.10f)];
    }

    /// <summary>
    /// Which body a cap is for, from its file name, for saying out loud. The caps are named
    /// <c>toecap.&lt;body&gt;.mdl</c>, so the body is the part after the last dot - "toecap.neolithe"
    /// becomes "Neolithe". A name with no body in it is reported as it stands rather than guessed at.
    /// <para/>
    /// This exists because the chat line used to print the file stem: "Toe cap: toecap (100% placed)"
    /// tells the wearer nothing, and on a body whose cap happens to be the unsuffixed one it reads like
    /// a bug even when the right cap was chosen.
    /// </summary>
    private static string CapBodyName(string fileStem)
    {
        int dot = fileStem.LastIndexOf('.');
        if (dot < 0 || dot == fileStem.Length - 1) return fileStem;
        var body = fileStem[(dot + 1)..];
        return char.ToUpperInvariant(body[0]) + body[1..];
    }

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
        // The UNFILTERED surface, kept rather than decoded again. `tris` is reassigned to the bone-filtered
        // subset below, and two later uses want the whole thing — the nail-socket discs and the triangle
        // count in the diagnostic. Both used to call BindSurface(bodies) afresh, so every candidate paid
        // THREE full decodes of the same four body models (Parse + LOD0 + union-find + a SkinTri per
        // triangle, and BindSurface is deliberately not ParseCached) where one would do. `Where(...).ToList()`
        // allocates a new list, so this reference keeps pointing at the unfiltered set.
        var allTris = tris;

        var r = new BinaryReader(new MemoryStream(bind));
        r.ReadUInt32();
        int version = r.ReadInt32();
        // 1: (u, v, offset, side, facing). 2: the same plus a residual in the landing's tangent frame.
        // Version 1 still loads — a cap bound before the residual existed is imperfect, not unusable.
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
        // Diagnostic only, and gated on someone listening. The scoring probe passes diag: null, so this
        // block used to decode the whole body a third time and sweep every triangle's UVs to build a string
        // that was then thrown away — the `var all = …` and `var sp = …` statements ran unconditionally
        // because only the Invoke was null-conditional.
        if (diag != null)
        {
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
            diag($"cap bind: {tris.Count} of {allTris.Count} triangle(s) carry the bound bones; "
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
                // out by up to 0.0039, and every one of the worst offenders sits on a toenail — the cap
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

            // A vertex whose coordinate falls in a gap of the atlas has nowhere to go — and leaving it at
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

            // OVER A NAIL SOCKET, KEEP THE AUTHORED SHAPE. Only on the full placement - the scoring
            // probe above has already returned, and inflating its miss count would make a cap look
            // unplaceable on the very bodies this exists to serve.
            var socketOf = new int[vc];
            Array.Fill(socketOf, -1);
            var discs2 = NailSocketDiscs(allTris);
            {
                var discs = discs2;
                int over = 0;
                for (int i = 0; i < vc && discs.Count > 0; i++)
                {
                    if (!found[i]) continue;
                    var (u3, v3) = uvs[i];
                    // WHICH socket, and the nearest one when discs overlap. Grouping the patches by
                    // connectivity instead put ten nails into six pieces, and a piece spanning two toes
                    // cannot be carried by one rigid transform on a foot that bends between them - the
                    // cap came out a median 0.0030 off the body against a standoff of 0.0010, reaching
                    // 0.0079. One socket, one transform.
                    float bestD = float.MaxValue;
                    for (int k = 0; k < discs.Count; k++)
                    {
                        var (du, dv, rr) = discs[k];
                        float ddu = u3 - du, ddv = v3 - dv;
                        float dd = ddu * ddu + ddv * ddv;
                        if (dd > rr * rr || dd >= bestD) continue;
                        bestD = dd; socketOf[i] = k;
                    }
                    if (socketOf[i] < 0) continue;
                    found[i] = false; missed++; over++;
                }
                if (over > 0)
                    diag?.Invoke($"cap bind: {over} vertex/vertices sit over a hole in the skin where a "
                               + "toenail is carried on its own mesh - their landings are measured off "
                               + "the rim of the hole and cannot be trusted");
            }

            if (missed > 0 && capMdl != null)
            {
                try
                {
                    var capSrc2 = ParseCached(capMdl);
                    ReadCapVertices(capSrc2, mesh, out var asAuthored, out var authoredNrm);
                    var tri2 = CapTriangles(capSrc2, mesh, (ushort)vc);
                    var near2 = new List<int>[vc];
                    for (int i = 0; i < vc; i++) near2[i] = [];
                    for (int t = 0; t + 2 < tri2.Count; t += 3)
                        for (int k = 0; k < 3; k++)
                        {
                            int a = tri2[t + k], b = tri2[t + (k + 1) % 3];
                            if (a < vc && b < vc) { near2[a].Add(b); near2[b].Add(a); }
                        }

                    // A SOCKET PATCH MOVES AS ONE PIECE. Spreading inwards from the landed rim, a
                    // ring at a time, reached 217 of 424 and stopped: past the first ring or two a
                    // patch has no landed neighbour left to copy, and the half that moved while the
                    // rest stayed put deepened the step instead of removing it (0.0035 -> 0.0061).
                    //
                    // The cap is not wrong over a nail. It is authored correctly and it is very nearly
                    // rigid there, because a foot bends behind the toes and not across them. So take
                    // the rotation and translation that carry the authored cap onto where it actually
                    // landed AROUND the patch, and move the whole patch by it. The nail keeps the shape
                    // it was drawn with and still follows the body's bend, and it does not matter
                    // whether the patch has a landed border at all.
                    int fitted = 0, patches = 0, clipped = 0;
                    float medianSocketR = 0f;
                    float worstMove = 0f;
                    {
                        var bySocket = new Dictionary<int, List<int>>();
                        for (int i = 0; i < vc; i++)
                        {
                            if (socketOf[i] < 0) continue;
                            (bySocket.TryGetValue(socketOf[i], out var l) ? l
                                : bySocket[socketOf[i]] = []).Add(i);
                        }
                        // Sized against the sockets the CAP actually sits over, not every hole in the
                        // body. Taken across all of them the median lands among holes elsewhere that are
                        // ten times a nail's size, every socket scores below it, and the scaling clamps
                        // to its floor - which is to say it does nothing at all.
                        if (bySocket.Count > 0)
                        {
                            var radii = bySocket.Keys.Select(k => discs2[k].R).OrderBy(x => x).ToArray();
                            medianSocketR = radii[radii.Length / 2];
                        }
                        foreach (var members in bySocket.Values)
                        {
                            patches++;

                            // Anchors: the landed vertices nearest the patch, found by walking outwards
                            // from it. Rings rather than a radius, so a patch on a small toe and one on
                            // the big toe are both answered at the scale of their own neighbourhood.
                            var anchors = new List<int>();
                            var seenA = new HashSet<int>(members);
                            var frontier = new List<int>(members);
                            for (int ring = 0; ring < CapFitAnchorRings && anchors.Count < CapFitMinAnchors; ring++)
                            {
                                var nextF = new List<int>();
                                foreach (int x in frontier)
                                    foreach (int y in near2[x])
                                    {
                                        if (!seenA.Add(y)) continue;
                                        nextF.Add(y);
                                        if (found[y] && socketOf[y] < 0 && y < asAuthored.Length) anchors.Add(y);
                                    }
                                if (nextF.Count == 0) break;
                                frontier = nextF;
                            }
                            if (anchors.Count < CapFitMinAnchors) continue;

                            Vec3 ca = default, cp = default;
                            foreach (int j in anchors)
                            {
                                ca = new Vec3(ca.X + asAuthored[j].X, ca.Y + asAuthored[j].Y,
                                              ca.Z + asAuthored[j].Z);
                                cp = new Vec3(cp.X + pos[j].X, cp.Y + pos[j].Y, cp.Z + pos[j].Z);
                            }
                            float invA = 1f / anchors.Count;
                            ca = new Vec3(ca.X * invA, ca.Y * invA, ca.Z * invA);
                            cp = new Vec3(cp.X * invA, cp.Y * invA, cp.Z * invA);

                            var fromP = anchors.Select(j => asAuthored[j]).ToList();
                            var toP = anchors.Select(j => pos[j]).ToList();
                            var rot = BestRotation(fromP, toP, ca, cp);
                            Vec3 Apply(Vec3 q)
                            {
                                float ax = q.X - ca.X, ay = q.Y - ca.Y, az = q.Z - ca.Z;
                                return new Vec3(cp.X + rot[0] * ax + rot[1] * ay + rot[2] * az,
                                                cp.Y + rot[3] * ax + rot[4] * ay + rot[5] * az,
                                                cp.Z + rot[6] * ax + rot[7] * ay + rot[8] * az);
                            }
                            Vec3 Turn(Vec3 q) =>
                                new(rot[0] * q.X + rot[1] * q.Y + rot[2] * q.Z,
                                    rot[3] * q.X + rot[4] * q.Y + rot[5] * q.Z,
                                    rot[6] * q.X + rot[7] * q.Y + rot[8] * q.Z);

                            // FADED OUT AT THE EDGE OF THE PATCH, and bounded. Replacing the landing
                            // outright leaves a step wherever the patch ends, and the fit is least
                            // trustworthy exactly there - furthest from the socket, where the resolved
                            // landing was becoming reliable again. Full weight over the middle of the
                            // nail, none at the rim, so the two agree where they meet.
                            var (cu, cv, cr) = discs2[socketOf[members[0]]];

                            // THE BOUND SCALES WITH THE SOCKET. One number for all ten held the big toe
                            // back: its socket is the twelve-edge one where the rest are ten, so it is
                            // both wider and deeper, and the ceiling that suits a little toe leaves it
                            // still sunk. Measured against the median socket, so the ones already sitting
                            // right keep exactly the bound they have now and only the larger get more.
                            float bound = CapFitMaxMove;
                            if (medianSocketR > 1e-9f)
                                bound = Math.Clamp(CapFitMaxMove * (cr / medianSocketR),
                                                   CapFitMaxMove, CapFitMaxMove * CapFitMoveScaleMax);
                            foreach (int x in members)
                            {
                                if (x >= asAuthored.Length) continue;
                                var was = pos[x];
                                var want = Apply(asAuthored[x]);

                                float du2 = uvs[x].U - cu, dv2 = uvs[x].V - cv;
                                float rel = cr > 1e-9f ? MathF.Sqrt(du2 * du2 + dv2 * dv2) / cr : 1f;
                                // Full weight over the nail itself and the fade kept to the outer band.
                                // Fading from the very centre instead left the correction with almost no
                                // weight anywhere - 154 vertices moved a furthest of 0.0009, and the dish
                                // came back to where it started.
                                float t2 = (1f - rel) / MathF.Max(1e-6f, 1f - CapFitFeatherStart);
                                float wgt = Math.Clamp(t2, 0f, 1f);
                                wgt *= wgt * (3f - 2f * wgt);          // smoothstep: flat at both ends

                                float mx = want.X - was.X, my = want.Y - was.Y, mz = want.Z - was.Z;
                                mx *= wgt; my *= wgt; mz *= wgt;
                                float len = MathF.Sqrt(mx * mx + my * my + mz * mz);
                                if (len > bound)
                                {
                                    float k2 = bound / len;
                                    mx *= k2; my *= k2; mz *= k2;
                                    clipped++;
                                }
                                pos[x] = new Vec3(was.X + mx, was.Y + my, was.Z + mz);
                                if (x < authoredNrm.Length && wgt > 0.5f)
                                    nrm[x] = NormalizeOr(Turn(authoredNrm[x]), nrm[x]);
                                found[x] = true;      // settled; the ring fill below is for atlas gaps
                                missed--;
                                fitted++;
                                worstMove = MathF.Max(worstMove, MathF.Sqrt(mx * mx + my * my + mz * mz));
                            }
                        }
                    }
                    // RELAX THE PATCHES. The fit places each nail as a rigid piece and the feather
                    // blends it back to the resolved landing at the rim, and where those two disagree
                    // the seam between them reads as a crease - over the big toe it came to a visible
                    // spike across the nail. Laplacian smoothing, the way a modelling package's relax
                    // works: each vertex eased toward the average of its neighbours, the vertices around
                    // the patch held fixed so the nail stays where the fit put it and only its interior
                    // settles. Bounded against the fitted position so smoothing cannot flatten the nail
                    // back into the dish it was lifted out of.
                    if (fitted > 0 && CapRelaxPasses > 0)
                    {
                        var anchorPos = new Vec3[vc];
                        for (int i = 0; i < vc; i++) anchorPos[i] = pos[i];
                        var next = new Vec3[vc];
                        for (int pass = 0; pass < CapRelaxPasses; pass++)
                        {
                            for (int i = 0; i < vc; i++) next[i] = pos[i];
                            for (int i = 0; i < vc; i++)
                            {
                                if (socketOf[i] < 0 || near2[i].Count == 0) continue;
                                Vec3 sum = default;
                                int c2 = 0;
                                foreach (int j in near2[i])
                                {
                                    sum = new Vec3(sum.X + pos[j].X, sum.Y + pos[j].Y, sum.Z + pos[j].Z);
                                    c2++;
                                }
                                if (c2 == 0) continue;
                                float invC = 1f / c2;
                                var avg = new Vec3(sum.X * invC, sum.Y * invC, sum.Z * invC);
                                var to = new Vec3(pos[i].X + (avg.X - pos[i].X) * CapRelaxWeight,
                                                  pos[i].Y + (avg.Y - pos[i].Y) * CapRelaxWeight,
                                                  pos[i].Z + (avg.Z - pos[i].Z) * CapRelaxWeight);
                                // Never further from where the fit put it than this.
                                float dx2 = to.X - anchorPos[i].X, dy2 = to.Y - anchorPos[i].Y,
                                      dz2 = to.Z - anchorPos[i].Z;
                                float dl = MathF.Sqrt(dx2 * dx2 + dy2 * dy2 + dz2 * dz2);
                                // Sized per socket, exactly as the height bound is: the big toe's socket
                                // is the wider one and its nail carries the longer crease, so it needs
                                // more settling than a little toe whose patch is a dozen vertices. Every
                                // socket at or under the median keeps the drift it already had.
                                float drift = CapRelaxMaxDrift;
                                if (medianSocketR > 1e-9f)
                                {
                                    // Squared, not linear. The big toe's socket comes out 1.79x the
                                    // median and a linear scale gave it 1.79x the drift - which it then
                                    // sat exactly on, still creased. A crease runs the LENGTH of a nail
                                    // while the drift bound is a distance, so the room a patch needs
                                    // grows faster than its radius does.
                                    float ratio = discs2[socketOf[i]].R / medianSocketR;
                                    drift = Math.Clamp(CapRelaxMaxDrift * ratio * ratio,
                                                       CapRelaxMaxDrift,
                                                       CapRelaxMaxDrift * CapRelaxDriftScaleMax);
                                }
                                if (dl > drift)
                                {
                                    float k3 = drift / dl;
                                    to = new Vec3(anchorPos[i].X + dx2 * k3, anchorPos[i].Y + dy2 * k3,
                                                  anchorPos[i].Z + dz2 * k3);
                                }
                                next[i] = to;
                            }
                            (pos, next) = (next, pos);
                        }
                        float worstDrift = 0f;
                        for (int i = 0; i < vc; i++)
                            if (socketOf[i] >= 0) worstDrift = MathF.Max(worstDrift, Dist(pos[i], anchorPos[i]));
                        diag?.Invoke($"cap bind: relaxed the socket patches over {CapRelaxPasses} pass(es), "
                                   + $"furthest a vertex settled {worstDrift:F5}");
                    }

                    if (fitted > 0)
                        diag?.Invoke($"cap bind: {fitted} vertex/vertices over {patches} toenail socket(s) "
                                   + "placed by fitting the authored cap onto where it landed around "
                                   + $"them, furthest moved {worstMove:F5}"
                                   + (clipped > 0 ? $", {clipped} held back at the bound" : ""));

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
                            // triangle — measured, faces at aspect 2.7e9 and the cap no longer manifold.
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

            // KEEP THE CAP'S OWN SHAPE, FOLLOW THE BODY ONLY IN THE LARGE.
            //
            // Every vertex above is reconstructed on its own, from its own baked atlas coordinate. On the
            // body the binding was measured against that is exact. On a foot wearing heels it is a
            // thousand independent estimates of where a point should go, and neighbours disagree by a
            // little: measured, the shell came out five times rougher than the same cap barefoot (p90
            // 0.00172 against 0.00033), with the clearance pass and the relax each accounting for about a
            // tenth of it - the crunch is in the placement itself.
            //
            // This is Laplacian surface editing (Sorkine et al., 2004). A vertex is described not by
            // where it is but by where it sits RELATIVE to its neighbours - its differential coordinate -
            // and that is what carries "round" and "smooth". So hold the cap's authored differential
            // coordinates and treat the landings as a soft constraint, rather than taking the landings as
            // the answer:
            //
            //     x_i  <-  ( sum_j x_j  +  deg_i * delta_i  +  w * p_i ) / (deg_i + w)
            //
            // solved by relaxation instead of a sparse least-squares, which needs no linear algebra
            // library and converges in a few dozen sweeps at this size. A deformation that is a pure
            // offset reproduces exactly, because delta is unchanged by translation; disagreement between
            // neighbours is what the sum averages away.
            //
            // It does NOT leave the reference body untouched, which an earlier note here claimed. Even
            // where the landings already reproduce the authored cap, 48 sweeps at w=0.2 do not reach the
            // fixed point: measured on the body a binding was baked against, 860 vertices move by a mean
            // 0.00007 and up to 0.00132. Small, and it costs the round trip its exact zero - the YAB bake
            // comes back at mean 0.000046 rather than 0.000000 - but it is not nothing, and anything
            // relying on placement being exact on the reference body has to allow for it.
            //
            // Two things are deliberately NOT smoothed. The cap's own boundary is pinned hard, because it
            // has to meet the shell where the weld expects it. And a vertex that never landed is left
            // entirely to its neighbours (w = 0), which is a better answer than the ring-fill this
            // replaces: it takes the authored shape with it instead of averaging positions.
            if (capMdl != null && CapShapePasses > 0)
            {
                try
                {
                    var shapeSrc = ParseCached(capMdl);
                    ReadCapVertices(shapeSrc, mesh, out var authored, out _);
                    var shapeTri = CapTriangles(shapeSrc, mesh, (ushort)vc);
                    if (authored.Length >= vc && shapeTri.Count >= 3)
                    {
                        var nb = new List<int>[vc];
                        var edge = new Dictionary<(int, int), int>();
                        for (int t = 0; t + 2 < shapeTri.Count; t += 3)
                            for (int k = 0; k < 3; k++)
                            {
                                int a = shapeTri[t + k], b = shapeTri[t + (k + 1) % 3];
                                if (a >= vc || b >= vc) continue;
                                (nb[a] ??= []).Add(b);
                                (nb[b] ??= []).Add(a);
                                var e = (Math.Min(a, b), Math.Max(a, b));
                                edge[e] = edge.GetValueOrDefault(e) + 1;
                            }
                        // The cap's open boundary: the rim that welds to the shell.
                        var onRim = new bool[vc];
                        foreach (var (e, n) in edge)
                            if (n == 1) { onRim[e.Item1] = true; onRim[e.Item2] = true; }

                        // The authored differential coordinate - what "round" actually means here.
                        var delta = new Vec3[vc];
                        for (int i = 0; i < vc; i++)
                        {
                            if (nb[i] is not { Count: > 0 }) continue;
                            Vec3 sum = default;
                            foreach (int j in nb[i]) sum = new Vec3(sum.X + authored[j].X,
                                                                    sum.Y + authored[j].Y,
                                                                    sum.Z + authored[j].Z);
                            float inv = 1f / nb[i].Count;
                            delta[i] = new Vec3(authored[i].X - sum.X * inv,
                                                authored[i].Y - sum.Y * inv,
                                                authored[i].Z - sum.Z * inv);
                        }

                        var cur = (Vec3[])pos.Clone();
                        var nxt = new Vec3[vc];
                        for (int pass = 0; pass < CapShapePasses; pass++)
                        {
                            Array.Copy(cur, nxt, vc);
                            for (int i = 0; i < vc; i++)
                            {
                                if (nb[i] is not { Count: > 0 }) continue;
                                if (onRim[i]) continue;                  // pinned to meet the shell
                                Vec3 sum = default;
                                foreach (int j in nb[i])
                                    sum = new Vec3(sum.X + cur[j].X, sum.Y + cur[j].Y, sum.Z + cur[j].Z);
                                int deg = nb[i].Count;
                                float w = found[i] ? CapShapeFollow : 0f;
                                nxt[i] = new Vec3(
                                    (sum.X + deg * delta[i].X + w * pos[i].X) / (deg + w),
                                    (sum.Y + deg * delta[i].Y + w * pos[i].Y) / (deg + w),
                                    (sum.Z + deg * delta[i].Z + w * pos[i].Z) / (deg + w));
                            }
                            (cur, nxt) = (nxt, cur);
                        }

                        int shaped = 0;
                        float worst = 0f, tot = 0f;
                        for (int i = 0; i < vc; i++)
                        {
                            if (nb[i] is not { Count: > 0 } || onRim[i]) continue;
                            float d = Dist(cur[i], pos[i]);
                            if (d <= 1e-7f) continue;
                            // IT MAY SMOOTH, BUT IT MAY NOT LIFT OFF THE FOOT. Holding the cap's
                            // curvature means pulling toward a shape the landings do not quite agree
                            // with, and where the body falls away - the outside of the pinky toe is
                            // exactly such a place - that pull takes the surface off the skin: measured,
                            // 28 vertices standing more than 5 mm proud against 6 without the fit, which
                            // is the shard that shows there.
                            //
                            // Bounding the DISTANCE MOVED does not fix it; those moves are already small
                            // (a 3 mm bound left 24 of the 28). The constraint has to be on the thing
                            // that is wrong, so the move is walked back until the vertex sits no further
                            // from the skin than its landing did.
                            if (NearestOnSkin(pos[i], tris, CapStandoffReach, out var hadAt))
                            {
                                float had = Dist(pos[i], hadAt);
                                for (int back = 0; back < CapShapeBackoffSteps; back++)
                                {
                                    if (!NearestOnSkin(cur[i], tris, CapStandoffReach, out var nowAt)
                                        || Dist(cur[i], nowAt) <= had + CapShapeLiftAllow) break;
                                    cur[i] = new Vec3((cur[i].X + pos[i].X) * 0.5f,
                                                      (cur[i].Y + pos[i].Y) * 0.5f,
                                                      (cur[i].Z + pos[i].Z) * 0.5f);
                                }
                                d = Dist(cur[i], pos[i]);
                                if (d <= 1e-7f) continue;
                            }
                            pos[i] = cur[i];
                            tot += d; worst = MathF.Max(worst, d); shaped++;
                        }
                        if (shaped > 0)
                        {
                            diag?.Invoke($"cap bind: reshaped {shaped} vertices to hold the cap's own "
                                       + $"curvature while following the body (mean {tot / shaped:F5}, "
                                       + $"furthest {worst:F5}, {CapShapePasses} passes at w={CapShapeFollow})");
                            // Everything now has a position, from its neighbours if not from a landing.
                            for (int i = 0; i < vc; i++)
                                if (!found[i] && nb[i] is { Count: > 0 }) { found[i] = true; missed--; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    diag?.Invoke($"cap bind: could not hold the cap's shape ({ex.Message})");
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
    /// brought back to the same cell — before this, every lookup on the heeled foot missed by 56 to 58
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
        // "strictly inside" and the search stops there — measured, three different coordinates all
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
    /// Rings a vertex with no atlas coordinate may be filled from. A handful is plenty — the gaps are a
    /// vertex or two wide — and a bound stops a cap that failed to place at all from being smeared into
    /// one point by repeated averaging.
    /// </summary>
    private const int CapBindFillPasses = 6;

    /// <summary>
    /// Share of a cap's vertices that may fail to place before the binding is judged not to describe this
    /// body at all, and the cap is declined rather than emitted. Measured: the foot it was authored on
    /// gives 0%, the same foot in heels 4%, and a different body 80% — so the two cases are nowhere near
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

    /// <summary>
    /// How close two caps' placement rates must be before the tie falls through to how evenly each sits
    /// off the skin.
    /// <para/>
    /// Deliberately TIGHT. It was widened to 0.05 once, on the reading that a heeled foot was getting the
    /// wrong cap — the Neolithe cap leaves 3% unplaced there against the Bibo cap's 0%, so the comparison
    /// stopped at the rate and never reached the fit, where Neolithe scores better. That reading came from
    /// a stale dump: with heels on the foot mesh belongs to the SHOE and carries the body material's name
    /// whichever body is worn, so "this looks like a Neolithe foot" was never something the materials
    /// could say. On a Bibo body the Bibo cap is the right answer, and the placement rate is what
    /// identifies it where the fit score cannot.
    /// </summary>
    private const float CapPlaceRateTie = 0.02f;

    /// <summary>
    /// How much closer one cap's round trip must be than another's to decide between them, as a ratio.
    /// A cap baked against the body being worn beats a foreign one by orders of magnitude, so this only
    /// has to be clear of the noise between two caps that are equally native - which cannot happen for
    /// one body, but two bindings baked against very similar feet come close.
    /// </summary>
    private const float CapRoundTripTie = 0.5f;

    /// <summary>
    /// How far a placed cap vertex may look for the skin when measuring its standoff. Past this it did
    /// not land on the foot at all and says nothing about the fit.
    /// </summary>
    private const float CapStandoffReach = 0.030f;

    private const float CapBindMaxUnplaced = 0.15f;

    /// <summary>
    /// Vertices skipped between samples when scoring a binding against a body. Choosing between bindings
    /// only needs the rough hit rate, and the difference being measured is 0% against 80%.
    /// </summary>
    private const int CapBindProbeStride = 8;

    /// <summary>
    /// Coverage texels the cap's own trim is widened by, so it reaches at least as far as the shell's.
    /// See the capDef note in the layer loop — the shared "any texel visible" test dilates by the asking
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
    /// <summary>
    /// How close to the skin a non-skin triangle must sit before the clearance pass treats it as part of
    /// the body. A toenail lies on the flesh; a sandal strap stands well off it, and the shell is meant
    /// to pass under the strap rather than balloon around it.
    /// </summary>
    private const float NailHugsSkin = 0.003f;

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
    /// as already set — it must match the threshold the CONSUMER reads the mask at, or the growth lands on
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
    /// Set by a diagnostic to receive each cut mask as it is built — name, texels, side. The cut is a UV
    /// mask and every argument about it so far has been made from texel COUNTS, which say nothing about
    /// where a mask actually lands in the atlas. Two masks of similar size can describe completely
    /// different regions, and that is exactly the confusion this exists to end.
    /// </summary>
    internal static Action<string, byte[], int>? MaskDump;


    /// <summary>
    /// Bone tables and submesh bone windows, as the GAME reads them. A modelling package builds its
    /// skin from the mesh bone table alone and ignores the submesh bone map entirely, so a model whose
    /// window is wrong imports perfectly and deforms as garbage in game — which is the one failure mode
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
    /// Comparing per-bone weight TOTALS between the two is not a check: the cap is near-symmetric —
    /// <c>iv_asi_oya_b_l</c> and <c>_r</c> both carry 16.8% — so a left/right swap, or any per-vertex
    /// permutation that preserves the totals, produces an identical summary. A vertex driven by the
    /// opposite foot's toe bone is perfect in bind pose and ruinous once the toes move, which is
    /// exactly the failure this exists to catch and exactly what the summary cannot see.
    /// <para/>
    /// Matched authored → shipped by nearest position; placement round-trips at a mean of 0.0003, well
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
        // seam split, so identify them by bone-table content instead — a cap mesh's table is the cap's
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
            // and the indices still point at the wrong ones — that is invisible in a bone list and it is
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
    /// vertex over a nail measures from the rim of that hole rather than from a surface under it — the
    /// offset came out at 0.0085 there against about 0.001 elsewhere, and every one of the worst
    /// placements landed on a toenail. Neolithe never showed it because its skin continues under its
    /// nails. But adding the nail mesh to this set is not the cure: a binding is an ATLAS coordinate, and
    /// the nails carry their own UV island in gear space, so a coordinate measured on a nail is looked up
    /// somewhere unrelated on the body. Measured — 8 vertices placed over a metre out, and the round trip
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
    /// Where the body's skin has a small hole, expressed as a disc in the atlas.
    /// <para/>
    /// A body is free to carry its toenails on their own mesh - many do, so the nails can take their own
    /// material, and a shoe model routinely brings them along with it. What that leaves in the skin is a
    /// ten- or twelve-edge hole per nail. The cap still has to cross the gap, and the landings it gets
    /// there are measured off the rim: the triangles around a socket fan their normals out over the
    /// hole, so stepping off along one lands where the rim points rather than where the nail is. On a
    /// heeled foot that came to a dish 0.0035 deep against a 0.0010 standoff.
    /// </summary>
    private static List<(float U, float V, float R)> NailSocketDiscs(IReadOnlyList<SkinTri> tris)
    {
        (long, long, long) Key(Vec3 q) => ((long)MathF.Round(q.X * 1e5f), (long)MathF.Round(q.Y * 1e5f),
                                           (long)MathF.Round(q.Z * 1e5f));
        var count = new Dictionary<((long, long, long), (long, long, long)), int>();
        var uvOf = new Dictionary<(long, long, long), (float U, float V)>();
        void Edge(Vec3 x, Vec3 y)
        {
            var (kx, ky) = (Key(x), Key(y));
            var k = kx.CompareTo(ky) <= 0 ? (kx, ky) : (ky, kx);
            count[k] = count.GetValueOrDefault(k) + 1;
        }
        foreach (var t in tris)
        {
            // TILE-NORMALISED, the same way a landing is resolved. A body's foot islands sit a whole tile
            // down the atlas - these sockets measure at v -0.66 - and comparing a raw coordinate against
            // a baked one silently never matches: 35 sockets found and not one cap vertex over any.
            var tile = TileOf(t);
            uvOf[Key(t.A)] = (t.Ua.U - tile.U, t.Ua.V - tile.V);
            uvOf[Key(t.B)] = (t.Ub.U - tile.U, t.Ub.V - tile.V);
            uvOf[Key(t.C)] = (t.Uc.U - tile.U, t.Uc.V - tile.V);
            Edge(t.A, t.B); Edge(t.B, t.C); Edge(t.C, t.A);
        }

        var side = new Dictionary<(long, long, long), List<(long, long, long)>>();
        foreach (var (k, n) in count)
        {
            if (n != 1) continue;
            (side.TryGetValue(k.Item1, out var l1) ? l1 : side[k.Item1] = []).Add(k.Item2);
            (side.TryGetValue(k.Item2, out var l2) ? l2 : side[k.Item2] = []).Add(k.Item1);
        }

        var discs = new List<(float, float, float)>();
        var seen = new HashSet<(long, long, long)>();
        foreach (var start in side.Keys)
        {
            if (!seen.Add(start)) continue;
            var loop = new List<(long, long, long)> { start };
            var at = start;
            var from = (long.MinValue, 0L, 0L);
            while (true)
            {
                var next = (long.MinValue, 0L, 0L);
                bool got = false;
                foreach (var cand in side[at])
                    if (!cand.Equals(from) && !seen.Contains(cand)) { next = cand; got = true; break; }
                if (!got) break;
                seen.Add(next); loop.Add(next);
                from = at; at = next;
                if (loop.Count > CapHoleMaxEdges) break;
            }
            if (loop.Count < 3 || loop.Count > CapHoleMaxEdges) continue;

            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            bool all = true;
            foreach (var k in loop)
            {
                if (!uvOf.TryGetValue(k, out var q)) { all = false; break; }
                u0 = MathF.Min(u0, q.U); u1 = MathF.Max(u1, q.U);
                v0 = MathF.Min(v0, q.V); v1 = MathF.Max(v1, q.V);
            }
            if (!all || u1 - u0 > CapHoleMaxUvSpan || v1 - v0 > CapHoleMaxUvSpan) continue;
            discs.Add(((u0 + u1) * 0.5f, (v0 + v1) * 0.5f,
                       MathF.Max(u1 - u0, v1 - v0) * 0.5f * CapSocketReach));
        }
        return discs;
    }

    /// <summary>
    /// The rotation carrying one set of points onto another, by Kabsch. Solved as a polar decomposition
    /// rather than an SVD - iterating M -> (M + M^-T)/2 converges on the orthogonal factor in a handful
    /// of steps and needs nothing but a 3x3 inverse.
    /// </summary>
    private static float[] BestRotation(IReadOnlyList<Vec3> from, IReadOnlyList<Vec3> to,
                                        Vec3 cFrom, Vec3 cTo)
    {
        var h = new float[9];
        for (int i = 0; i < from.Count && i < to.Count; i++)
        {
            float ax = from[i].X - cFrom.X, ay = from[i].Y - cFrom.Y, az = from[i].Z - cFrom.Z;
            float bx = to[i].X - cTo.X, by = to[i].Y - cTo.Y, bz = to[i].Z - cTo.Z;
            h[0] += ax * bx; h[1] += ax * by; h[2] += ax * bz;
            h[3] += ay * bx; h[4] += ay * by; h[5] += ay * bz;
            h[6] += az * bx; h[7] += az * by; h[8] += az * bz;
        }
        float Det(float[] m) => m[0] * (m[4] * m[8] - m[5] * m[7])
                              - m[1] * (m[3] * m[8] - m[5] * m[6])
                              + m[2] * (m[3] * m[7] - m[4] * m[6]);
        var r = (float[])h.Clone();
        for (int it = 0; it < CapFitPolarSteps; it++)
        {
            float d = Det(r);
            if (MathF.Abs(d) < 1e-20f) return [1, 0, 0, 0, 1, 0, 0, 0, 1];
            // inverse-transpose of r
            var inv = new float[9];
            inv[0] = (r[4] * r[8] - r[5] * r[7]) / d;
            inv[1] = (r[2] * r[7] - r[1] * r[8]) / d;
            inv[2] = (r[1] * r[5] - r[2] * r[4]) / d;
            inv[3] = (r[5] * r[6] - r[3] * r[8]) / d;
            inv[4] = (r[0] * r[8] - r[2] * r[6]) / d;
            inv[5] = (r[2] * r[3] - r[0] * r[5]) / d;
            inv[6] = (r[3] * r[7] - r[4] * r[6]) / d;
            inv[7] = (r[1] * r[6] - r[0] * r[7]) / d;
            inv[8] = (r[0] * r[4] - r[1] * r[3]) / d;
            var next = new float[9];
            for (int k = 0; k < 3; k++)
                for (int j = 0; j < 3; j++)
                    next[k * 3 + j] = 0.5f * (r[k * 3 + j] + inv[j * 3 + k]);
            float move = 0f;
            for (int k = 0; k < 9; k++) move += MathF.Abs(next[k] - r[k]);
            r = next;
            if (move < 1e-7f) break;
        }
        // H is built as (from)^T(to), so the rotation that carries `from` onto `to` is its transpose.
        return [r[0], r[3], r[6], r[1], r[4], r[7], r[2], r[5], r[8]];
    }

    /// <summary>
    /// A tangent frame for a triangle, taken from its UV parameterisation rather than its edges. The
    /// residual a cap vertex carries is stored in this frame, so it means the same thing on any body
    /// laid out in the same atlas however that body is posed — an edge-derived frame would rotate with
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
    /// over the toes they are frequently the NEAREST surface — but they carry their own UV island. A cap
    /// triangle with one corner landing on a nail and another on skin then stretches clean across the gap
    /// between two islands and samples whatever lies between, which shows as a jagged transparent band
    /// through the middle of the cap. They are separate connected components, so drop the small ones.
    /// </summary>
    /// <param name="skinOnly">See <see cref="TryReadLod0Geometry"/>.</param>
    /// <param name="dropIslands">
    /// False keeps the small connected components. They are dropped when collecting a surface to take UV
    /// from — a toenail is its own island in the atlas and projecting onto one stretches a triangle
    /// across the gap — but they must be KEPT when collecting a surface to take skinning from, since a
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
    /// mesh with a different bone table — an index would mean something else there.
    /// </summary>
    /// <param name="max">
    /// Influences to keep. Eight where the destination format holds eight (see BlendCount) — trimming to
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

    /// <summary>Area of a triangle — what it actually covers, as opposed to how thin it is.</summary>
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
    /// opposite corner and is replaced by one triangle per sub-segment. Nothing moves — the new vertices
    /// sit exactly where the shell already is — so this cannot reopen a join or distort the cap.
    /// </summary>
    /// <returns>How many vertices were inserted.</returns>
    /// <param name="atVertex">Landings skipped because the boundary already has a vertex there.</param>
    /// <param name="offBoundary">Landings skipped because no boundary edge was within reach — those are
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
                // The position is the shell's landing exactly, not the interpolation — that is the whole
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
        // Start from the nearer endpoint, so anything not explicitly interpolated below — blend indices,
        // blend weights, colour — arrives as a coherent set rather than a mix of two.
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

    /// <summary>How close to an existing rim vertex a landing must be before splitting is pointless — a
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
        // problem it was aimed at turned out to be unwelded boundary instead — see WeldRounds.
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
        // vertex-preference above — the vertex has to be within CapRimSplitMargin, so the correction is
        // under a fifth of a millimetre and cannot drag anything to a distant rim vertex. It is here
        // because the alternative is worse: a landing a hair off a rim vertex is too close to split the
        // boundary at (the split would only make a sliver, so both sides decline it) and too far to share
        // a position with, and what is left is a T-junction of exactly the size this margin allows.
        // Measured on Rue, that was the whole residual — 5 pairs from 4.6e-5 to 1.3e-4 apart.
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
    /// The mesh's triangle list as the cap leaves it — the source triangles it did not cut out, plus the
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
    /// the geometry moved. It also fixes the push — <c>position += normal * push</c> along a stale
    /// sidewall normal drives the two halves of a bridged gap apart instead of offsetting them together.
    /// <para/>
    /// Faces contribute their unnormalized cross product, so area weights itself and the slivers left
    /// where a gap closed contribute almost nothing. Normals are accumulated per WELDED NODE and every
    /// copy of a node gets the same answer, so UV seams inside the cap don't crack.
    /// </summary>
    private static Vec3[] CapNormals(Vec3[] basePos, Vec3[] baseNrm, ToeCapPlan plan, ushort[] tris)
        => RelaxedNormals(basePos, baseNrm, plan.Delta, plan.NodeOf, plan.NodeWeight, plan.NodeNormal, tris);

    /// <inheritdoc cref="CapNormals"/>
    /// <remarks>
    /// The plan-free form, shared by every pass that moves vertices without changing which vertices exist.
    /// The toe cap hands it a rebuilt topology; the bust bridge hands it the mesh's own, unchanged.
    /// </remarks>
    internal static Vec3[] RelaxedNormals(Vec3[] basePos, Vec3[] baseNrm, Vec3[] delta, int[] nodeOf,
                                          float[] nodeWeight, Vec3[] nodeNormal, ushort[] tris)
    {
        int vc = basePos.Length;
        int nodeCount = nodeWeight.Length;

        var def = new Vec3[vc];
        for (int i = 0; i < vc; i++)
            def[i] = new Vec3(basePos[i].X + delta[i].X, basePos[i].Y + delta[i].Y, basePos[i].Z + delta[i].Z);

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

        // SMOOTH THE NORMAL FIELD. Everything above gives each node the area-weighted sum of the faces
        // around it, which is faceted whenever the triangles are — and on a real body they are: edge
        // lengths across the chest vary with a coefficient of variation of 0.735, so neighbouring edges
        // differ in length by about 70%.
        //
        // This matters more than the geometry it comes from, twice over. Shading displays the surface's
        // DERIVATIVE, so noise in the normals is far more visible than the position noise underneath it;
        // and a shell is built as `position + normal * BaseOffset`, so scattered normals become up to a
        // millimetre of scattered POSITION in the garment. The noise is amplified, not merely copied.
        //
        // Jacobi over the welded-node graph, so the answer does not depend on the order nodes sit in.
        // Only nodes the pass touched are smoothed — everything else must keep its original bytes — but
        // neighbours are READ regardless of weight, so a node on the region's edge averages against real
        // normals rather than against zero, which would drag it toward nothing and crease the boundary.
        //
        // Normalized once, after the last pass rather than during: these are area-weighted sums, and
        // normalizing between passes throws away the weighting that makes a large triangle count for more
        // than a sliver.
        if (NormalSmoothPasses > 0)
        {
            var nbr = new List<int>[nodeCount];
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                ushort ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                if (ia >= vc || ib >= vc || ic >= vc) continue;
                int na = nodeOf[ia], nb = nodeOf[ib], nc = nodeOf[ic];
                if (na == nb || nb == nc || na == nc) continue;
                void Link(int a, int b)
                {
                    (nbr[a] ??= new List<int>()).Add(b);
                    (nbr[b] ??= new List<int>()).Add(a);
                }
                Link(na, nb); Link(nb, nc); Link(nc, na);
            }

            var swap = new Vec3[nodeCount];
            for (int pass = 0; pass < NormalSmoothPasses; pass++)
            {
                Array.Copy(accum, swap, nodeCount);
                for (int n = 0; n < nodeCount; n++)
                {
                    if (nodeWeight[n] <= 0f || nbr[n] is not { Count: > 0 } near) continue;
                    float sx = accum[n].X, sy = accum[n].Y, sz = accum[n].Z;
                    foreach (int k in near) { sx += accum[k].X; sy += accum[k].Y; sz += accum[k].Z; }
                    float inv = 1f / (near.Count + 1);
                    swap[n] = new Vec3(sx * inv, sy * inv, sz * inv);
                }
                (accum, swap) = (swap, accum);
            }
        }

        // Winding is not guaranteed here. Getting it backwards shades the cap inside out AND makes the
        // push drive the shell into the body, so decide it once from the source normals we trust.
        float agree = 0;
        for (int n = 0; n < nodeCount; n++)
            if (nodeWeight[n] > 0f)
                agree += accum[n].X * nodeNormal[n].X + accum[n].Y * nodeNormal[n].Y + accum[n].Z * nodeNormal[n].Z;
        float sign = agree < 0f ? -1f : 1f;

        var outN = new Vec3[vc];
        for (int i = 0; i < vc; i++)
        {
            int n = nodeOf[i];
            float w = nodeWeight[n];
            if (w <= 0f) { outN[i] = baseNrm[i]; continue; }   // untouched: original bytes must survive

            var a = accum[n];
            var fresh = Normalize(new Vec3(a.X * sign, a.Y * sign, a.Z * sign)) ?? nodeNormal[n];
            var src = nodeNormal[n];

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
    /// no defined scale to encode into — the caller then leaves the original bytes and says so.
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
            case 8:           // Ubyte4n — inverts ReadTyped's /255 AND BuildVerbatim's *2-1 unbias
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
    /// This is what carries the overlay's texture — and its alpha — across the cap, continued over the
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

            // Where the binding put it, if there is one. Everything below — the landing search, the seam
            // split, the rim — then describes the cap as it will actually be emitted rather than as it
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
            // flesh, and over the toes they are frequently the NEAREST surface — but they carry their
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
        // over a nail follows the nail. It made things worse in game — the cap stood visibly off the toes
        // — because the nails are weighted to their own IVCS bones and following them pulls the cap away
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

        // Every landing worth considering, nearest first — not just the nearest one. A UV seam is a cut
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
        // away than about one cap edge — so the vast interior, where there is one landing and no dispute,
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
        // is cut, the cap straddles the cut, and BOTH sides are locally self-consistent — every vertex on
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
        // face has corners in two charts any more, so none can stretch between them — the same trick the
        // body's own mesh uses at the same place, which is why the seam is there to begin with.
        //
        // What marks an edge as crossing the cut is STRETCH — UV travelled per unit of 3D travelled — and
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

        // Each copy takes the landing that best suits ITS chart — for the copy on the far side of the
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
        /// what keeps the cap moving with the skin it sits on — the authored weights coincided with the
        /// shell's in bind pose and differed by up to 12.5%, which opens the join in any real pose.
        /// </summary>
        public required (string Bone, float W)[][] Weights;

        /// <summary>The cap vertex each output vertex is a copy of — everything but the UV comes from it.</summary>
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
    /// cut lands well behind the cap's rim, past the weld's reach — 329 lip vertices too far to weld and
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
            // Only the part of the cap this layer will actually emit — the footprint has to describe the
            // hole the cap fills, and a coverage-trimmed cap fills less of one.
            if (cov?.Coverage != null && !AnyVisible(cov, uv[a], uv[b], uv[c])) continue;

            {
                float ax = uv[a].U * size, ay = uv[a].V * size;
                float bx = uv[b].U * size, by = uv[b].V * size;
                float cx = uv[c].U * size, cy = uv[c].V * size;
                // TILED, not clamped. A body's UVs need not live in the [0,1] tile — the foot model a
                // heel swaps in sits a whole tile down (v -0.70..-0.56 measured, against the v 0.30..0.44
                // the same cap occupies barefoot) — and the mask's consumer samples it with wrap, so the
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
                // far denser in UV than the map it replaces — 3432 faces over about 1351 texels, so the
                // typical face is SMALLER than a texel — and point-sampling a mesh like that leaves a
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
        // the gaps between the toes, so its faces never touch the atlas where those crevices live — but
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

    /// <summary>Mean length of the edges touching the given nodes — the mesh's own resolution.</summary>
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

    private static float Len(Vec3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    /// <summary>
    /// Least-squares fit of h = a + bu + cv + du² + euv + fv² to scattered (u,v,h), by Gaussian
    /// elimination with partial pivoting on the 6x6 normal equations. Null if it is underdetermined or
    /// singular — a caller must have a fallback, since a coverage-clipped region can supply neither
    /// enough points nor enough spread.
    /// <para/>
    /// Six terms rather than a radial h = h0 + kr²: a nipple does not sit on the apex of the breast, so
    /// the surface under it SLOPES, and a rotationally symmetric fit averages that slope away and returns
    /// a dome tilted out of the surface it is meant to continue. The linear pair carry the slope and the
    /// three quadratic terms carry anisotropic curvature.
    /// </summary>
    private static double[]? FitQuadric(List<(double U, double V, double H)> pts)
    {
        if (pts.Count < 12) return null;
        var m = new double[6, 7];
        Span<double> t = stackalloc double[6];
        foreach (var (u, v, h) in pts)
        {
            // WEIGHTED toward the near edge of the ring, by 1/r². Unweighted, the far side of the ring
            // dominates simply by being further out, and the two axes are not equivalent out there: a ring
            // wide enough to be stable reaches the collarbone above and the underbust below long before it
            // runs out of breast sideways. So the vertical curvature collapses while the horizontal barely
            // moves, and the dome comes out as a cylinder — measured on a real body as 3.86 across against
            // 1.68 up, from a breast that was very nearly isotropic (5.45 / 5.45). That is a surface which
            // reads round from one angle and flat from another.
            double r2 = u * u + v * v;
            double wt = r2 > 1e-12 ? 1.0 / r2 : 1e12;
            t[0] = 1; t[1] = u; t[2] = v; t[3] = u * u; t[4] = u * v; t[5] = v * v;
            for (int i = 0; i < 6; i++)
            {
                for (int j = 0; j < 6; j++) m[i, j] += wt * t[i] * t[j];
                m[i, 6] += wt * t[i] * h;
            }
        }
        for (int c = 0; c < 6; c++)
        {
            int piv = c;
            for (int r = c + 1; r < 6; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            if (Math.Abs(m[piv, c]) < 1e-18) return null;
            if (piv != c) for (int j = 0; j <= 6; j++) (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]);
            for (int r = 0; r < 6; r++)
            {
                if (r == c) continue;
                double f = m[r, c] / m[c, c];
                for (int j = c; j <= 6; j++) m[r, j] -= f * m[c, j];
            }
        }
        var x = new double[6];
        for (int i = 0; i < 6; i++) x[i] = m[i, 6] / m[i, i];
        return x;
    }

    /// <summary>
    /// Where the dome fit samples the breast, as multiples of the relaxed disc's radius — about 18 to
    /// 26mm on the body this was measured against. The band has to start OUTSIDE the trough the nipple
    /// sits in, which runs to roughly 18mm, or the fit is dragged down by the very dent it exists to
    /// fill; and it has to stop before the ring stops being breast at all.
    /// <para/>
    /// Held in as tightly as that because of ISOTROPY, which is what a wider band costs. The untouched
    /// breast is very nearly as curved up-and-down as it is side-to-side (5.45 against 5.45, measured
    /// over 10mm). Fitted at 2.0-3.1 the result came out 3.85 across against 1.81 up — a cylinder, round
    /// from one angle and flat from another, which is exactly how it was reported. At 1.6-2.3 it is
    /// 3.51 against 2.44.
    /// <para/>
    /// The cost is paid at the tip: a nearer band extrapolates to a higher apex, so slightly more of the
    /// nipple survives (1.09 against 0.79 as a band average). That trade is monotonic along this axis —
    /// moving the band out lowers the tip and flattens the vertical — so these two numbers are the knob
    /// to reach for if either end needs adjusting.
    /// </summary>
    /// <summary>
    /// Rounds of the finishing Taubin relax, each a positive step and a negative one. See the block in
    /// <c>NippleSmoothTarget</c>.
    /// </summary>
    private const int NippleFinishPasses = 6;

    /// <summary>How far each finishing pass moves a vertex toward its neighbours' centroid. Under-relaxed
    /// for the same reason the main relax is: at 1 a Jacobi step oscillates on the checkerboard mode
    /// instead of converging.</summary>
    private const float NippleFinishLambda = 0.5f;

    /// <inheritdoc cref="NippleDomeRingOuter"/>
    private const float NippleDomeRingInner = 1.6f;

    /// <inheritdoc cref="NippleDomeRingInner"/>
    private const float NippleDomeRingOuter = 2.3f;

    /// <summary>
    /// The fraction of the region's radius that is pulled fully to the dome, before the pull fades out to
    /// nothing at the edge. See the taper in <c>NippleSmoothTarget</c> for why it is not 1.
    /// <para/>
    /// Chosen on the SIDE-VIEW SILHOUETTE — the furthest-forward point at each height, which is the
    /// outline a side view actually draws and the only measurement here that has not misled. At 0.7 the
    /// breast's outline lifted 3.5mm; at 0.5 it lifts 2.6mm and keeps a real peak instead of flattening
    /// into a plateau.
    /// </summary>
    private const float NippleDomeSolid = 0.5f;

    /// <summary>
    /// How far the dome floor reaches beyond the relaxed disc, as a multiple of its radius. It has to
    /// cover the trough the nipple sits in, which on a real body runs to about 13mm — well past the disc
    /// that shaves the tip.
    /// <para/>
    /// Chosen against the side-view silhouette together with <see cref="NippleDomeSolid"/>, since the two
    /// trade directly: reaching further fills more of the trough and moves the breast's outline more.
    /// 1.6 leaves the outline untouched but barely fills; 2.6 fills no better than 2.0 and lifts the
    /// outline further. 2.0 fills the trough best of any of them and costs 2.6mm of outline.
    /// <para/>
    /// Not swept past about 2.6. The annulus sits beyond this, and out there it stops describing a breast
    /// and starts taking in the chest wall — visible in an earlier sweep as convexity that improved
    /// smoothly to 2.4, scored best at 2.6 and collapsed at 2.8. A value next to that cliff would be tuned
    /// to one body rather than to the shape of the problem.
    /// </summary>
    private const float NippleDomeReach = 2.0f;

    /// <summary>
    /// How many times the recomputed normal field is averaged over the welded-node graph before it is
    /// written. See the block in <see cref="RelaxedNormals"/> for why this exists at all.
    /// <para/>
    /// WITHOUT IT THE GEOMETRY PASS MAKES SHADING WORSE, which is the measurement that put it here.
    /// Mean angle between the normals of vertices sharing an edge, within 30mm of the nipple on a real
    /// body: the untouched body reads 6.19°, and the relax alone takes it to 7.67°. The shape improves
    /// and the surface shades rougher than it started, which is exactly "it matches the skin perfectly,
    /// but it's not a graceful curve".
    /// <para/>
    /// Chosen off the curve on that body — 4 passes give 4.92°, 8 give 3.81°, 16 give 2.66°, and it
    /// saturates around 2.43° by 32. Sixteen is past the knee without being at the floor: less than half
    /// the faceting the body started with, at a smoothing radius of about 3mm on a 0.8mm mesh, which is
    /// far too small to touch a breast's own form. Pushing to the floor buys 0.2° and starts flattening
    /// the shading cues that make a surface read as curved at all — waxy, a breast lit like a balloon.
    /// <para/>
    /// This only ever changes SHADING. No vertex moves because of it — verified across that whole sweep,
    /// where the largest vertex displacement stayed 4.389mm at every pass count — which is what keeps it
    /// separable from the geometry passes and testable on its own.
    /// </summary>
    private const int NormalSmoothPasses = 16;

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
    internal static void WriteXYZ(byte[] a, int off, byte type, float x, float y, float z)
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
    /// keeps geometry under alpha of 1/255, which is invisible — and a resampled or compressed coverage
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
