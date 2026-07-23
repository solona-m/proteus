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

    /// <summary>A model carries at most 4 materials — the host-selection cap (enforced in the caller).</summary>
    public const int MaxMaterials = 4;

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
        IReadOnlyList<HashSet<string>?>? enabledShapes = null, Action<string>? diag = null)
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
        int baseMatCount = baseSrc?.MatNames.Count ?? 0;
        if (baseMatCount + layers.Count > MaxMaterials)
            throw new InvalidOperationException(
                $"host has {baseMatCount} materials + {layers.Count} layers > {MaxMaterials} max");

        // Union bone list. u16 indices, so hundreds of bones are fine. The host (if any) goes FIRST so its
        // own meshes can remap their bone tables by name.
        var boneNames = new List<string>();
        var boneIndex = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var boneBBox = new List<byte[]>();
        var boneSources = baseSrc != null ? new[] { baseSrc }.Concat(parsed) : parsed;
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
                      SecondSkinLayer? cov, int mapBase, ref bool mapAppended, bool dropConnectors)
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
            if (preserve)
            {
                CopyVerbatim(s, src.Vb, 0x44 + m * DeclSize, vc, decl, vbo, bs,
                    out outStreams, out outStrides, out declBlock);
                uv = [];   // no coverage trim for the host mesh
            }
            else
            {
                BuildVerbatim(s, src.Vb, 0x44 + m * DeclSize, vc, decl, vbo, bs, push,
                    out outStreams, out outStrides, out declBlock, out uv);
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

        for (ushort layer = 0; layer < layers.Count; layer++)
        {
            var def = layers[layer];
            float push = BaseOffset + LayerSeparation * layer;
            ushort matIndex = (ushort)(baseMatCount + layer);

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

                    EmitMesh(src, m, matIndex, push, preserve: false, cov: def, mapBase, ref mapAppended,
                        dropConnectors: skipConnectors);
                }
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
        out byte[][] outStreams, out byte[] outStrides, out byte[] declBlock, out (float U, float V)[] uvs)
    {
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
    private static void ReadTyped(byte[] s, int addr, byte type, Span<float> o)
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
