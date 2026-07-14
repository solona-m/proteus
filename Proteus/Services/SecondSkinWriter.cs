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
/// along its normals, transcoded from body to GEAR vertex format, and MERGED into a single model so the
/// whole thing rides one invisible accessory (the right ring). Each part × layer becomes its own mesh
/// group, and each group carries its layer's material — so different regions can run different shaders.
///
/// Hard-won constraints, each of which was a crash or a silent no-render:
///  - The model must be UNIFORMLY gear format; never mix body-format and gear-format meshes.
///  - Every mesh needs its own vertex declaration (vertDeclCount == meshCount).
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
    private const byte GearStride0 = 20;   // pos f32x3 + weights ubyte4 + indices ubyte4
    private const byte GearStride1 = 24;   // normal half4 + tangent ubyte4n + colour ubyte4n + uv half4
    private const int BBoxSize = 32;       // min Vec4 + max Vec4

    /// <summary>Gear vertex declaration, lifted verbatim from a shipping Dawntrail gear model.</summary>
    private static readonly byte[] GearDecl = BuildGearDecl();

    private static byte[] BuildGearDecl()
    {
        var d = new byte[DeclSize];
        ReadOnlySpan<byte> elems = new byte[]
        {
            0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,   // stream0 +0   f32x3    Position
            0x00, 0x0C, 0x05, 0x01, 0x00, 0x00, 0x00, 0x00,   // stream0 +12  ubyte4   BlendWeight
            0x00, 0x10, 0x05, 0x02, 0x00, 0x00, 0x00, 0x00,   // stream0 +16  ubyte4   BlendIndices
            0x01, 0x00, 0x0E, 0x03, 0x00, 0x00, 0x00, 0x00,   // stream1 +0   half4    Normal
            0x01, 0x08, 0x08, 0x06, 0x00, 0x00, 0x00, 0x00,   // stream1 +8   ubyte4n  Binormal
            0x01, 0x0C, 0x08, 0x07, 0x00, 0x00, 0x00, 0x00,   // stream1 +12  ubyte4n  Colour
            0x01, 0x10, 0x0E, 0x04, 0x00, 0x00, 0x00, 0x00,   // stream1 +16  half4    Texcoord
            0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,   // terminator
        };
        elems.CopyTo(d);
        return d;
    }

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

    /// <summary>A parsed body part.</summary>
    private sealed class Source
    {
        public required byte[] S;
        public int Mh, MeshStart, SubmeshStart, Vb, Ib, StrBlock, MatOffStart;
        public ushort MeshCount, SubmeshCount, BoneCount, MatCount;
        public List<string> MatNames = [];
        public string[] BoneNames = [];
        public ushort[][] BoneTables = [];
        public ushort[] SubmeshBoneMap = [];
        public byte[] BoneBBoxes = [];    // BoneCount * 32
        public byte[] ModelBBoxes = [];   // 4 * 32
        public float Radius, ModelClip, ShadowClip;
        public byte Flags1, Flags2;
        public byte[] Lods = [];          // 3 * 60
    }

    /// <summary>
    /// Build the merged shell. <paramref name="sources"/> are the body models the character is currently
    /// drawing (resolve them live — never ship a prebuilt shell); every one contributes its own mesh
    /// groups. Every layer is applied to every source.
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> sources, IReadOnlyList<SecondSkinLayer> layers, out Stats stats)
    {
        if (sources.Count == 0) throw new ArgumentException("need at least one source model", nameof(sources));
        if (layers.Count == 0) throw new ArgumentException("need at least one layer", nameof(layers));

        var parsed = sources.Select(Parse).ToList();

        // Union bone list. u16 indices, so hundreds of bones are fine.
        var boneNames = new List<string>();
        var boneIndex = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var boneBBox = new List<byte[]>();
        foreach (var src in parsed)
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
        var subOut = new List<byte[]>();
        var boneTables = new List<ushort[]>();   // one per emitted mesh
        var submeshBoneMap = new List<ushort>();
        uint idxCursor = 0;
        ushort subCursor = 0;
        int triIn = 0, triOut = 0, vertOut = 0;

        for (ushort layer = 0; layer < layers.Count; layer++)
        {
            var def = layers[layer];
            float push = BaseOffset + LayerSeparation * layer;

            foreach (var src in parsed)
            {
                // Each (source, layer) pair contributes its own copy of the source's submesh bone map;
                // submesh boneStart values are kept as authored and rebased onto it.
                int mapBase = submeshBoneMap.Count;
                bool mapAppended = false;

                var s = src.S;
                uint U32(int o) => BitConverter.ToUInt32(s, o);
                ushort U16(int o) => BitConverter.ToUInt16(s, o);

                for (int m = 0; m < src.MeshCount; m++)
                {
                    int mo = src.MeshStart + m * 36;
                    ushort vc = U16(mo);
                    if (vc == 0) continue;

                    // SKIN ONLY. A body model also holds the smallclothes/undies mesh (gear UV), nails,
                    // piercings and pubes; duplicating those and painting them with a body-UV overlay
                    // smears the art across the hips and hands.
                    ushort srcMat = U16(mo + 8);
                    if (srcMat >= src.MatNames.Count || SkinMaterialBodyType(src.MatNames[srcMat]) == null)
                        continue;

                    ushort srcSubIdx = U16(mo + 10), srcSubCount = U16(mo + 12), srcBoneTbl = U16(mo + 14);
                    uint vbo0 = U32(mo + 20), vbo1 = U32(mo + 24);
                    byte bs0 = s[mo + 32], bs1 = s[mo + 33];

                    Transcode(s, src.Vb, vc, vbo0, vbo1, bs0, bs1, push, out var g0all, out var g1all);

                    var uv = new (float U, float V)[vc];
                    for (int i = 0; i < vc; i++)
                    {
                        int bo1 = (int)vbo1 + i * bs1;
                        uv[i] = (BitConverter.ToSingle(s, src.Vb + bo1 + 20), BitConverter.ToSingle(s, src.Vb + bo1 + 24));
                    }

                    // Keep a triangle if ANY texel under its UV footprint is visible. Sampling only the
                    // corners culls triangles whose interior is visible, leaving a sawtooth edge.
                    var keptPerSub = new List<ushort[]>();
                    var used = new bool[vc];
                    for (int su = 0; su < srcSubCount; su++)
                    {
                        int ss = src.SubmeshStart + (srcSubIdx + su) * 16;
                        uint so = U32(ss), sc = U32(ss + 4);
                        var keep = new List<ushort>();
                        for (uint t = 0; t + 2 < sc; t += 3)
                        {
                            int p = src.Ib + (int)(so + t) * 2;
                            ushort a = BitConverter.ToUInt16(s, p), b = BitConverter.ToUInt16(s, p + 2), c = BitConverter.ToUInt16(s, p + 4);
                            triIn++;
                            if (!AnyVisible(def, uv[a], uv[b], uv[c])) continue;
                            keep.Add(a); keep.Add(b); keep.Add(c);
                            used[a] = used[b] = used[c] = true;
                            triOut++;
                        }
                        keptPerSub.Add(keep.ToArray());
                    }
                    if (keptPerSub.All(k => k.Length == 0)) continue;   // this layer paints nothing here

                    if (!mapAppended)
                    {
                        submeshBoneMap.AddRange(src.SubmeshBoneMap);
                        mapAppended = true;
                    }

                    // Compact the vertex buffer down to the vertices the surviving triangles reference.
                    var remap = new ushort[vc];
                    ushort nv = 0;
                    var c0 = new MemoryStream();
                    var c1 = new MemoryStream();
                    for (int i = 0; i < vc; i++)
                    {
                        if (!used[i]) continue;
                        remap[i] = nv++;
                        c0.Write(g0all, i * GearStride0, GearStride0);
                        c1.Write(g1all, i * GearStride1, GearStride1);
                    }
                    vertOut += nv;

                    uint v0 = (uint)vBuf.Position; vBuf.Write(c0.GetBuffer(), 0, (int)c0.Length);
                    uint v1 = (uint)vBuf.Position; vBuf.Write(c1.GetBuffer(), 0, (int)c1.Length);

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
                    W16(nm, 8, layer);                      // material index == layer
                    W16(nm, 10, subCursor);
                    W16(nm, 12, keptSubs);
                    W16(nm, 14, (ushort)boneTables.Count);  // this mesh's own table
                    W32(nm, 16, meshStartIdx);
                    W32(nm, 20, v0);
                    W32(nm, 24, v1);
                    W32(nm, 28, 0);
                    nm[32] = GearStride0; nm[33] = GearStride1; nm[34] = 0; nm[35] = 2;

                    meshOut.Add(nm);
                    boneTables.Add(table);
                    subOut.AddRange(subsForMesh);
                    subCursor += keptSubs;
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
        for (int i = 0; i < meshCount; i++) ms.Write(GearDecl);
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
        W16(mh, 10, (ushort)layers.Count);
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
        ms.Write(UnionModelBBoxes(parsed));
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
        W16(o, 14, (ushort)layers.Count);
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
        };
    }

    /// <summary>
    /// Body vertex format -> gear vertex format, pushing each vertex out along its normal.
    /// The body carries up to 8 bone influences and gear holds 4, but 99.8% of body vertices use =&lt;4
    /// and the rest discard ~0.4% of their weight — measured, harmless.
    /// </summary>
    private static void Transcode(
        byte[] s, int vb, ushort vc, uint vbo0, uint vbo1, byte bs0, byte bs1, float push,
        out byte[] gs0, out byte[] gs1)
    {
        gs0 = new byte[vc * GearStride0];
        gs1 = new byte[vc * GearStride1];

        for (int i = 0; i < vc; i++)
        {
            int bo0 = (int)vbo0 + i * bs0, bo1 = (int)vbo1 + i * bs1;
            int go0 = i * GearStride0, go1 = i * GearStride1;

            float nx = BitConverter.ToSingle(s, vb + bo1);
            float ny = BitConverter.ToSingle(s, vb + bo1 + 4);
            float nz = BitConverter.ToSingle(s, vb + bo1 + 8);
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; }

            W32(gs0, go0 + 0, (uint)BitConverter.SingleToInt32Bits(BitConverter.ToSingle(s, vb + bo0) + nx * push));
            W32(gs0, go0 + 4, (uint)BitConverter.SingleToInt32Bits(BitConverter.ToSingle(s, vb + bo0 + 4) + ny * push));
            W32(gs0, go0 + 8, (uint)BitConverter.SingleToInt32Bits(BitConverter.ToSingle(s, vb + bo0 + 8) + nz * push));

            if (bs0 >= 28)
            {
                Span<byte> w = stackalloc byte[8];
                Span<byte> ix = stackalloc byte[8];
                for (int k = 0; k < 8; k++) { w[k] = s[vb + bo0 + 12 + k]; ix[k] = s[vb + bo0 + 20 + k]; }
                for (int a = 0; a < 4; a++)
                {
                    int best = a;
                    for (int b = a + 1; b < 8; b++) if (w[b] > w[best]) best = b;
                    (w[a], w[best]) = (w[best], w[a]);
                    (ix[a], ix[best]) = (ix[best], ix[a]);
                }
                int sum = w[0] + w[1] + w[2] + w[3];
                if (sum == 0) { w[0] = 255; sum = 255; }
                int acc = 0;
                for (int k = 0; k < 4; k++)
                {
                    byte q = k < 3 ? (byte)Math.Round(w[k] * 255.0 / sum) : (byte)Math.Clamp(255 - acc, 0, 255);
                    acc += q;
                    gs0[go0 + 12 + k] = q;
                    gs0[go0 + 16 + k] = ix[k];
                }
            }
            else
            {
                Array.Copy(s, vb + bo0 + 12, gs0, go0 + 12, 4);
                Array.Copy(s, vb + bo0 + 16, gs0, go0 + 16, 4);
            }

            W16(gs1, go1 + 0, Half(nx));
            W16(gs1, go1 + 2, Half(ny));
            W16(gs1, go1 + 4, Half(nz));
            W16(gs1, go1 + 6, Half(0f));
            Array.Copy(s, vb + bo1 + 12, gs1, go1 + 8, 4);   // tangent

            // Vertex colour gates the gear shaders' emissive; the body's own colour would switch it off.
            gs1[go1 + 12] = 255; gs1[go1 + 13] = 255; gs1[go1 + 14] = 255; gs1[go1 + 15] = 255;

            W16(gs1, go1 + 16, Half(BitConverter.ToSingle(s, vb + bo1 + 20)));   // uv0.x
            W16(gs1, go1 + 18, Half(BitConverter.ToSingle(s, vb + bo1 + 24)));   // uv0.y
            W16(gs1, go1 + 20, Half(-1f));                                       // uv1: a constant the gear
            W16(gs1, go1 + 22, Half(2f));                                        // shaders read, not a texcoord
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
