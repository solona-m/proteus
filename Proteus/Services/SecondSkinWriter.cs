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
    /// LOD0 triangle geometry — object-space position and uv0 per vertex, plus triangle indices — for UV
    /// seam analysis (see <see cref="UvSeamMapService"/>). Every LOD0 mesh is concatenated into one vertex
    /// array with its indices rebased, so a seam BETWEEN two meshes is found exactly like one inside a
    /// mesh; that matters because a body's torso and legs are frequently separate meshes.
    /// <para/>
    /// Returns false rather than throwing on a model this can't read — a missing position or uv0 element,
    /// a truncated buffer, anything Parse rejects. The caller treats that as "no seam data" and falls back.
    /// </summary>
    public static bool TryReadLod0Geometry(byte[] mdl, out float[] positions, out float[] uvs, out int[] triangles)
    {
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
            if (matIdx >= matNames.Count || SkinMaterialBodyType(matNames[matIdx]) == null) continue;

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
    /// One entry of a mesh's vertex declaration: where and in what format a given attribute (Usage) sits
    /// within its vertex stream. Read so the transcoder can locate attributes by declaration instead of
    /// assuming a fixed layout — vanilla and modded models declare different offsets and types (half vs
    /// float, compressed positions), so a fixed layout skins the wrong bytes as garbage.
    /// </summary>
    private readonly record struct VElem(byte Stream, byte Offset, byte Type, byte Usage, byte UsageIndex);

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

        // Shape-key morphs parsed from the .mdl (LOD0), keyed by shape name → its per-mesh index edits.
        // A ShapeValue redirects one index-buffer entry (at BaseIdx, absolute) to a morphed replacement
        // vertex (Replace). Enabled shapes for THIS body model (from BodyShapeReader); only these bake.
        public Dictionary<string, List<ShapeMeshEntry>> Shapes = new(StringComparer.Ordinal);
        public HashSet<string>? EnabledShapes;
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
        byte[]? authoredCap = null)
    {
        if (sources.Count == 0) throw new ArgumentException("need at least one source model", nameof(sources));
        if (layers.Count == 0) throw new ArgumentException("need at least one layer", nameof(layers));

        var parsed = sources.Select(Parse).ToList();

        // Attach each source's enabled shape keys and (Stage 2a) verify the parse against them: does the
        // .mdl actually contain the enabled shape, and how many of its index edits resolve to in-range
        // positions/vertices. This confirms the format read before any geometry is mutated.
        for (int i = 0; i < parsed.Count; i++)
        {
            var en = enabledShapes != null && i < enabledShapes.Count ? enabledShapes[i] : null;
            parsed[i].EnabledShapes = en;
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
        Source? capSrc = null;
        if (authoredCap != null)
        {
            try { capSrc = Parse(authoredCap); }
            catch (Exception ex) { diag?.Invoke($"authored cap failed to parse, ignoring: {ex.Message}"); }
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
            (float U, float V)[] uv;
            Vec3[]? capSrcPos = null, capOutPos = null;   // set only where a toe cap actually moved geometry
            ToeCapPlan? capPlan = null;
            if (preserve)
            {
                CopyVerbatim(s, src.Vb, 0x44 + m * DeclSize, vc, decl, vbo, bs,
                    out outStreams, out outStrides, out declBlock);
                uv = [];   // no coverage trim for the host mesh

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
                var capTris = wantCap ? MeshTriangles(src, srcSubIdx, srcSubCount) : null;
                BuildVerbatim(s, src.Vb, 0x44 + m * DeclSize, vc, decl, vbo, bs, push,
                    out outStreams, out outStrides, out declBlock, out uv,
                    out capSrcPos, out capOutPos, out capPlan, cov, capTris, diag,
                    buildCapGeometry: capSrc == null);
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

            // Keep a triangle if ANY texel under its UV footprint is visible (cov null = keep all).
            var keptPerSub = new List<ushort[]>();
            var used = new bool[vc];
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
                // joints (wrist/ankle/…, ~100-120 tris — real skin parts are 800+), plus the mesh's LAST
                // submesh (a duplicate variant, e.g. the second calf). Kept empty ⇒ contributes nothing;
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
                    if (capPlan != null && capPlan.IsCut(a, b, c)) continue;
                    if (capPlan != null && capPlan.IsDropped(a, b, c)) continue;
                    if (capOutPos != null && CapDegenerate(capSrcPos!, capOutPos, a, b, c)) continue;
                    keep.Add(a); keep.Add(b); keep.Add(c);
                    used[a] = used[b] = used[c] = true;
                    triOut++;
                }
                keptPerSub.Add(keep.ToArray());
            }

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
            if (keptPerSub.All(k => k.Length == 0)) return;   // paints nothing here

            if (!mapAppended)
            {
                submeshBoneMap.AddRange(src.SubmeshBoneMap);
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
                W32(ns, 8, 0);                                            // attributes dropped
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

        // The cap's projection depends only on the cap and the bodies, never on the layer wearing it, and
        // it is the most expensive thing in the build — every cap vertex against every skin triangle.
        var capUvCache = new Dictionary<int, CapUvPlan?>();

        for (ushort layer = 0; layer < layers.Count; layer++)
        {
            var def = layers[layer];
            float push = BaseOffset + LayerSeparation * layer;
            ushort matIndex = (ushort)(baseMatCount + layer);

            // Where an authored cap fills the toe box, pull the CUT in a little. The painted map says
            // where a cap is wanted, not where the modelled one reaches, and it runs about 0.002 past
            // the cap's rear edge — leaving a strip of bare skin whose edge follows the map's texel
            // grid, which is what makes it look jagged. Eroding the map is enough: the cap then laps
            // over the shell that survives instead of meeting it exactly.
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
                int lit = 0;
                foreach (byte px in eroded) if (px >= 128) lit++;
                diag?.Invoke($"authored cap: cut map eroded by {CapCutErode}, {lit} texels remain");
                cutDef = new SecondSkinLayer
                {
                    MaterialName = def.MaterialName,
                    Coverage = def.Coverage,
                    CoverageWidth = def.CoverageWidth,
                    CoverageHeight = def.CoverageHeight,
                    ToeCap = eroded,
                    ToeCapWidth = mwp,
                    ToeCapHeight = mhp,
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

                // LOD0 meshes only — never the lower LODs (a full game model has all three; merging them
                // stacks overlapping low-poly copies that fling geometry across the scene).
                int mEnd = src.Lod0MeshIndex + src.Lod0MeshCount;
                for (int m = src.Lod0MeshIndex; m < mEnd && m < src.MeshCount; m++)
                {
                    int mo = src.MeshStart + m * 36;
                    if (U16(mo) == 0) continue;   // empty mesh

                    // SKIN ONLY. A body model also holds the smallclothes/undies mesh (gear UV), nails,
                    // piercings and pubes; duplicating those and painting them with a body-UV overlay
                    // smears the art across the hips and hands.
                    ushort srcMat = U16(mo + 8);
                    if (srcMat >= src.MatNames.Count || SkinMaterialBodyType(src.MatNames[srcMat]) == null)
                        continue;

                    EmitMesh(src, m, matIndex, push, preserve: false, cov: cutDef, mapBase, ref mapAppended,
                        dropConnectors: skipConnectors);
                }
            }

            // Graft the authored cap on for any layer that asked for one, wearing that layer's material.
            // Emitted VERBATIM for now — no push, no coverage trim, no displacement: it is already
            // modelled where it belongs on the foot it was authored against. Fitting it to a different
            // foot comes later and is a separate step; proving the merge itself comes first.
            if (capSrc is { } cs && def.ToeCap != null)
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
                    EmitMesh(cs, m, matIndex, 0f, preserve: true, cov: null, capMapBase,
                             ref capMapAppended, dropConnectors: false,
                             capUv: capUvCache.TryGetValue(m, out var cached) ? cached
                                  : capUvCache[m] = ProjectCapUV(cs, m, sources, diag));
                    emitted++;
                }
                diag?.Invoke($"authored toe cap: grafted {emitted} mesh(es) onto layer {layer}");
            }
        }

        if (meshOut.Count == 0) throw new InvalidOperationException("no geometry survived coverage trimming");

        int meshCount = meshOut.Count;
        int boneCount = boneNames.Count;

        // ── string block: bone names (union) + material names. Attributes are dropped. ──
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

        var head = parsed[0];
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
        BitConverter.GetBytes(head.Radius).CopyTo(mh, 0);
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
        BitConverter.GetBytes(head.ModelClip).CopyTo(mh, 28);
        BitConverter.GetBytes(head.ShadowClip).CopyTo(mh, 32);
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

        if (shapedTotal > 0) diag?.Invoke($"shape bake: {shapedTotal} index entries rewired to morphed vertices");

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

        for (int i = 0; i < tables.Count; i++)
        {
            long headerPos = start + i * 4;
            ms.Position = headerPos;
            Span<byte> t = stackalloc byte[2];

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

        string Str(uint rel)
        {
            int o = strBlock + (int)rel, e = o;
            while (s[e] != 0) e++;
            return Encoding.ASCII.GetString(s, o, e - o);
        }

        var boneNames = new string[boneCount];
        for (int i = 0; i < boneCount; i++) boneNames[i] = Str(U32(boneOffStart + i * 4));

        // v6 bone tables — offset is in dwords, relative to each table's own header.
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

    private static void BuildVerbatim(
        byte[] s, int vb, int srcDeclOff, ushort vc, VElem[] decl, uint[] vbo, byte[] bs, float push,
        out byte[][] outStreams, out byte[] outStrides, out byte[] declBlock, out (float U, float V)[] uvs,
        out Vec3[]? capSrcPos, out Vec3[]? capOutPos, out ToeCapPlan? capPlan,
        SecondSkinLayer? cap = null, ushort[]? capTris = null, Action<string>? capLog = null,
        bool buildCapGeometry = true)
    {
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
            for (int i = 0; i < vc; i++)
            {
                float u = uvs[i].U - uOff, v = uvs[i].V - vOff;
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

            // Normals recomputed from the REBUILT surface — the source triangles minus the ones the cut
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
                // bytes it arrived with. The normal element has its own stream — it need not be pos's.
                if (plan is not null && norm is { } ne2 && plan.NodeWeight[plan.NodeOf[i]] > 0f)
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
                    capLog($"toe cap: no encoder for normal type {norm?.Type} — that mesh keeps its old shading");
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
    /// The map is painted to say where a cap is wanted; the modelled mesh decides where one actually is,
    /// and it does not reach as far.
    /// </summary>
    private const int CapCutErode = 3;

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
            if (masked is not { Count: >= MinToeCapNodes }) continue;

            // The CORE of the mask — where it is actually painted in, not its antialiased fringe. A soft
            // edge covers a lot of ground at a value of 1 or 2/255, and letting that define the region
            // stretches it over the whole foot: the axis tilts and the slices below land mostly behind the
            // toes, where they do nothing. Everything that sets up the frame uses the core; the fringe
            // still moves, just by its own small weight.
            var core = new List<int>();
            foreach (int n in masked)
                if (nW[n] >= ToeCapCoreWeight) core.Add(n);
            if (core.Count < MinToeCapNodes) continue;

            // A cap is sewn onto surviving geometry. An island that is ENTIRELY masked — each toenail is
            // — has no rim to sew to, so no cap can be built for it. It used to be left where it was, on
            // the assumption it would end up inside the cap; that held only while the cap ballooned over
            // the toes. Now that the cap hugs them, the nails stand proud of it in ten little scallops,
            // which is exactly the crunch it reads as. They are underneath a stocking, so drop them.
            //
            // Only ever a SMALL island: a mask painted over a whole foot would otherwise swallow the foot.
            if (core.Count > MaxCoreFraction * islandSize[c]) continue;   // marked by the pre-pass above

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
        if ((!capped || newTris.Count == 0) && !anyDropped) return null;

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
                                           Action<string>? diag)
    {
        var s = cap.S;
        int mo = cap.MeshStart + mesh * 36;
        ushort vc = BitConverter.ToUInt16(s, mo);
        if (vc == 0) return null;

        var decl = mesh < cap.Decls.Length ? cap.Decls[mesh] : [];
        VElem? pos = null;
        foreach (var el in decl) if (el.Usage == UsePosition) { pos = el; break; }
        if (pos is not { } pe) return null;

        uint[] vbo = { BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                       BitConverter.ToUInt32(s, mo + 28) };
        byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
        var cp = new Vec3[vc];
        {
            Span<float> tmp = stackalloc float[4];
            for (int i = 0; i < vc; i++)
            {
                ReadTyped(s, cap.Vb + (int)vbo[pe.Stream] + i * bs[pe.Stream] + pe.Offset, pe.Type, tmp);
                cp[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
            }
        }

        // Every body's LOD0 SKIN geometry, in one list. Reuses the reader the seam analysis uses, which
        // already applies the same skin-only filter the shell builder does.
        var tri = new List<(Vec3 A, Vec3 B, Vec3 C, (float U, float V) Ua, (float U, float V) Ub, (float U, float V) Uc, Vec3 Ctr)>();
        foreach (var body in bodies)
        {
            if (!TryReadLod0Geometry(body, out var bp, out var bu, out var bt)) continue;

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
                                                              (pa.Z + pb.Z + pc.Z) / 3f)));
            }
        }
        if (tri.Count == 0) { diag?.Invoke("authored cap: no body geometry to project UVs from"); return null; }

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
        var candN = new int[vc];
        float worst = 0f;
        for (int i = 0; i < vc; i++)
        {
            var p = cp[i];
            int b0 = i * ProjectCandidates;
            int n = 0;
            float cull = float.MaxValue;   // squared distance the K-th best already achieves
            foreach (var (a, b, c, ua, ub, uc, ctr) in tri)
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
                if (MathF.Abs(den) < 1e-20f) { hu = ua.U; hv = ua.V; }
                else
                {
                    float wb = (d11 * d20 - d01 * d21) / den;
                    float wc = (d00 * d21 - d01 * d20) / den;
                    float wa = 1f - wb - wc;
                    hu = ua.U * wa + ub.U * wb + uc.U * wc;
                    hv = ua.V * wa + ub.V * wb + uc.V * wc;
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
                        candD[b0 + k] = candD[b0 + k + 1];
                    }
                    n--;
                }
                else if (n == ProjectCandidates) n--;

                int ins = n;
                while (ins > 0 && candD[b0 + ins - 1] > d)
                {
                    candU[b0 + ins] = candU[b0 + ins - 1]; candV[b0 + ins] = candV[b0 + ins - 1];
                    candD[b0 + ins] = candD[b0 + ins - 1];
                    ins--;
                }
                candU[b0 + ins] = hu; candV[b0 + ins] = hv; candD[b0 + ins] = d;
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
        return new CapUvPlan { Uv = finalUV, SourceOf = sourceOf.ToArray(), Corner = corner };
    }

    /// <summary>
    /// How the authored cap's vertices are laid out once the body's UV seams have been cut into it:
    /// one entry per output vertex, and where every triangle corner points.
    /// </summary>
    private sealed class CapUvPlan
    {
        /// <summary>Projected coordinate per OUTPUT vertex.</summary>
        public required (float U, float V)[] Uv;

        /// <summary>The cap vertex each output vertex is a copy of — everything but the UV comes from it.</summary>
        public required int[] SourceOf;

        /// <summary>
        /// Output vertex per triangle corner, in the cap's own submesh-then-triangle order. The emitter
        /// walks its index buffer in that same order and substitutes these.
        /// </summary>
        public required int[] Corner;
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
    /// A cut mask covering exactly where the authored cap actually is, rasterised from its projected
    /// UVs. Substituted for the painted ToeCap map when a cap is grafted.
    /// <para/>
    /// The painted map says where a cap is WANTED; it cannot say where the modelled one reaches. Cutting
    /// by it leaves a strip of skin bare wherever the paint runs past the mesh — measured at 0.002 past
    /// the cap's rear edge, showing in game as a jagged band along the top of the foot, jagged because
    /// it follows the map's texel grid. Eroded by a texel or two so the cap's boundary always laps over
    /// the shell that survives rather than meeting it exactly.
    /// </summary>
    private static byte[] CapFootprintMask(Source cap, int mesh, (float U, float V)[] uv, int size, int erode)
    {
        var s = cap.S;
        var mask = new byte[size * size];
        int mo = cap.MeshStart + mesh * 36;
        ushort si = BitConverter.ToUInt16(s, mo + 10), sc = BitConverter.ToUInt16(s, mo + 12);
        ushort vc = BitConverter.ToUInt16(s, mo);

        for (int su = 0; su < sc; su++)
        {
            int ss = cap.SubmeshStart + (si + su) * 16;
            uint so = BitConverter.ToUInt32(s, ss), cnt = BitConverter.ToUInt32(s, ss + 4);
            for (uint t = 0; t + 2 < cnt; t += 3)
            {
                int q = cap.Ib + (int)(so + t) * 2;
                int a = BitConverter.ToUInt16(s, q), b = BitConverter.ToUInt16(s, q + 2), c = BitConverter.ToUInt16(s, q + 4);
                if (a >= vc || b >= vc || c >= vc || a >= uv.Length || b >= uv.Length || c >= uv.Length) continue;

                float ax = uv[a].U * size, ay = uv[a].V * size;
                float bx = uv[b].U * size, by = uv[b].V * size;
                float cx = uv[c].U * size, cy = uv[c].V * size;
                int x0 = Math.Clamp((int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))) - 1, 0, size - 1);
                int x1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))) + 1, 0, size - 1);
                int y0 = Math.Clamp((int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))) - 1, 0, size - 1);
                int y1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))) + 1, 0, size - 1);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1 << 18) continue;   // a seam-straddling triangle

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
                        mask[y * size + x] = 255;
                    }
            }
        }

        // Pull the edge in, so the shell that survives is always overlapped rather than merely abutted.
        for (int step = 0; step < erode; step++)
        {
            var next = (byte[])mask.Clone();
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (mask[y * size + x] == 0) continue;
                    bool edge = false;
                    for (int dy = -1; dy <= 1 && !edge; dy++)
                        for (int dx = -1; dx <= 1 && !edge; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= size || ny >= size || mask[ny * size + nx] == 0) edge = true;
                        }
                    if (edge) next[y * size + x] = 0;
                }
            mask = next;
        }
        return mask;
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
