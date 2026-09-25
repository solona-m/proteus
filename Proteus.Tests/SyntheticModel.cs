using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Proteus.Tests;

/// <summary>
/// A minimal but genuinely valid v6 <c>.mdl</c>, built in code.
/// <para/>
/// Exists because the writer's binary-format behaviour was tested only through real mod packs sitting at
/// absolute paths on one machine — and when one of those packs was moved off the Desktop, three tests began
/// returning at their first line and passing without asserting anything. The attribute merge, which
/// relocates every table after the meshes, had no executing test at all while looking fully covered.
/// <para/>
/// It is also the only way to test the part that matters most. <see cref="SecondSkinWriter"/>'s attribute
/// remap renumbers each source's mask bits onto a merged union list, and that is the identity function
/// whenever there is one source — which every pack-driven test is. Two synthetic models declaring the same
/// attribute names in DIFFERENT orders is the case where the remap does real work, and no real pack pair on
/// disk is guaranteed to produce it.
/// <para/>
/// Deliberately not a binary fixture file. The point is to vary attribute names, masks and their ordering
/// per test; a checked-in <c>.mdl</c> would fix one arrangement and hide the parameter that matters.
/// </summary>
internal static class SyntheticModel
{
    // Vertex element types and usages, as the model format numbers them (see SecondSkinWriter.ReadTyped).
    private const byte Float2 = 1, Float3 = 2, UByte4 = 5;
    private const byte UsePosition = 0, UseBlendWeight = 1, UseBlendIndices = 2, UseUV = 4;

    /// <summary>A skinned model's stream 0: position, four blend weights, four blend indices — the game's own layout.</summary>
    private const int SkinnedStride = 20;

    /// <summary>A skinned model's stream 1: uv0.</summary>
    private const int UvStride = 8;

    private const int DeclSize = 17 * 8;
    private const int BBoxSize = 32;
    private const int Stride = 20;   // position float3 at 0, uv float2 at 12

    /// <summary>
    /// One submesh, tagged with the attribute bits it carries. The mask indexes the model's own attribute
    /// list, which is what the merge has to renumber.
    /// <para/>
    /// <paramref name="Islands"/> and <paramref name="TrianglesPerIsland"/> shape the geometry for
    /// <see cref="ModelPartReader"/>'s island split. Islands are placed far apart in X; triangles WITHIN an
    /// island share a corner POSITION but never a vertex index, which is the case that matters — a real
    /// model duplicates vertices along every UV seam, so connectivity is only visible by position and an
    /// index-based split would report every triangle as its own island.
    /// <para/>
    /// <paramref name="OffsetX"/> and its siblings move the whole submesh, and they are what lets a test say
    /// whether two submeshes are the SAME surface. Without them every submesh of a mesh is emitted at the
    /// same coordinates, because a triangle's position depends only on its index within its own submesh — so
    /// each one is exactly coincident with the first N triangles of every larger sibling, and any test of
    /// "does something else already draw this?" passes for a reason the test did not intend.
    /// <para/>
    /// With <paramref name="Islands"/> = 1 a submesh of N triangles spans Y in
    /// [<paramref name="OffsetY"/>, <paramref name="OffsetY"/> + N] — the convention every band assertion
    /// here is written against.
    /// </summary>
    /// <param name="Weights">The bones every vertex of this submesh follows, at most four. Any submesh carrying them
    /// makes the whole model SKINNED: a real bone table, and position, blend weights and blend indices in stream 0 with
    /// uv0 in stream 1, the layout game models use. Submeshes without them then follow <c>n_root</c> alone.</param>
    internal sealed record Sub(uint AttrMask, int Islands = 1, int TrianglesPerIsland = 1,
                               float OffsetX = 0f, float OffsetY = 0f, float OffsetZ = 0f,
                               (string Bone, float W)[]? Weights = null)
    {
        internal int TriangleCount => Islands * TrianglesPerIsland;
    }

    /// <summary>One LOD0 mesh, drawn with <paramref name="Material"/>.</summary>
    internal sealed record Mesh(string Material, params Sub[] Submeshes);

    /// <summary>Dawntrail .mdl. Bone tables are a header array plus one shared pool of indices.</summary>
    internal const uint V6 = 0x01000006;

    /// <summary>
    /// Pre-Dawntrail .mdl, still shipped by plenty of mods. The ONLY layout difference that matters to the
    /// readers here is the bone table: v5 stores one fixed 132-byte struct per table
    /// (<c>u16 BoneIndex[64]; u8 BoneCount; u8 pad[3]</c>) with no shared pool, so a v6 reader walks the
    /// wrong distance and lands mid-file for everything that follows.
    /// </summary>
    internal const uint V5 = 0x01000005;

    private const int V5BoneTableBytes = 132;

    /// <summary>
    /// Assemble the model. <paramref name="attrNames"/> is the attribute table in the order submesh masks
    /// index it — bit <c>i</c> of a mask means <c>attrNames[i]</c>.
    /// </summary>
    /// <param name="version"><see cref="V6"/> (default) or <see cref="V5"/>.</param>
    /// <param name="shapeNames">
    /// Shape (morph) keys to emit, one ShapeMesh and one ShapeValue each. Worth setting on a version
    /// fixture: the shape block is the FIRST thing after the bone table, so it is where a reader that walked
    /// the wrong bone-table width lands — and because a shape is found by a name offset into the string
    /// block, a misaligned read sends that offset outside the file rather than merely returning wrong data.
    /// </param>
    /// <param name="boneTableOrder">
    /// The bones in BONE TABLE order, when that must differ from the model's name list. Real models are like this —
    /// a mesh's table holds the handful of bones that mesh uses, in no particular relation to the file's name list —
    /// and anything that confuses a table SLOT with a bone INDEX only misbehaves when the two disagree. Names not in
    /// the table are still in the model; names outside the model are rejected. Null gives the identity table.
    /// </param>
    internal static byte[] Build(IReadOnlyList<string> attrNames, Mesh[] meshes, uint version,
                                 IReadOnlyList<string>? shapeNames = null,
                                 IReadOnlyList<string>? boneTableOrder = null)
        => BuildCore(attrNames, meshes, version, shapeNames, boneTableOrder);

    /// <inheritdoc cref="Build(IReadOnlyList{string}, Mesh[], uint, IReadOnlyList{string}, IReadOnlyList{string})"/>
    internal static byte[] Build(IReadOnlyList<string> attrNames, params Mesh[] meshes)
        => BuildCore(attrNames, meshes, V6, null, null);

    private static byte[] BuildCore(IReadOnlyList<string> attrNames, Mesh[] meshes, uint version,
                                    IReadOnlyList<string>? shapeNames, IReadOnlyList<string>? boneTableOrder)
    {
        var materials = meshes.Select(m => m.Material).Distinct(StringComparer.Ordinal).ToList();
        bool skinned = meshes.Any(m => m.Submeshes.Any(x => x.Weights != null));
        var bones = new[] { "n_root" }
            .Concat(meshes.SelectMany(m => m.Submeshes).SelectMany(x => x.Weights ?? []).Select(w => w.Bone))
            .Distinct(StringComparer.Ordinal).ToArray();
        var shapes = shapeNames ?? [];
        // Slot -> name. The blend indices a vertex carries are slots in THIS list; the table's entries are where
        // each of those lands in `bones`.
        var tableBones = boneTableOrder ?? bones;
        foreach (var tb in tableBones)
            if (Array.IndexOf(bones, tb) < 0)
                throw new ArgumentException($"bone table names '{tb}', which is not a bone of this model");
        // Every bone a vertex will actually follow has to have a SLOT, or its blend index cannot be written. The
        // unweighted fallback below follows n_root, so a table that leaves n_root out has to say so up front
        // rather than emit an index into nothing.
        foreach (var used in meshes.SelectMany(m => m.Submeshes)
                                   .SelectMany(x => (x.Weights ?? [("n_root", 1f)]).Select(w => w.Bone))
                                   .Distinct(StringComparer.Ordinal))
            if (IndexOf(tableBones, used) < 0)
                throw new ArgumentException(
                    $"a vertex follows '{used}', which the bone table has no slot for — add it to boneTableOrder");

        // ── string block: bones, attributes, materials, each NUL-terminated ──
        var strMs = new MemoryStream();
        uint[] Intern(IEnumerable<string> names) =>
        [
            .. names.Select(n =>
            {
                var at = (uint)strMs.Position;
                strMs.Write(Encoding.ASCII.GetBytes(n));
                strMs.WriteByte(0);
                return at;
            })
        ];
        var boneOff  = Intern(bones);
        var attrOff  = Intern(attrNames);
        var matOff   = Intern(materials);
        var shapeOff = Intern(shapes);
        var strings  = strMs.ToArray();

        // ── geometry: one triangle per submesh, three fresh vertices each ──
        var vBuf = new MemoryStream();
        var iBuf = new MemoryStream();
        int subTotal = meshes.Sum(m => m.Submeshes.Length);

        var meshBytes = new List<byte[]>();
        var subBytes = new List<byte[]>();
        ushort vertexCursor = 0;
        uint indexCursor = 0;

        foreach (var mesh in meshes)
        {
            uint meshVtxOffset = (uint)vBuf.Position;
            uint meshStartIndex = indexCursor;
            ushort meshVerts = 0;
            var stream0 = new MemoryStream();
            var stream1 = new MemoryStream();

            foreach (var sub in mesh.Submeshes)
            {
                int tris = sub.TriangleCount;
                var so = new byte[16];
                W32(so, 0, indexCursor);
                W32(so, 4, (uint)(tris * 3));
                W32(so, 8, sub.AttrMask);
                // Each submesh gets its OWN window, as every game and author model does — never one shared.
                W16(so, 12, (ushort)(subBytes.Count * tableBones.Count));   // boneStart
                W16(so, 14, (ushort)tableBones.Count);                      // boneCount
                subBytes.Add(so);

                // Three fresh vertices per triangle, mesh-relative indices, spread so the model's bounding
                // box is not degenerate. Corner 0 sits on the island's shared hub — same position every
                // triangle, a different vertex each time.
                for (int i = 0; i < sub.Islands; i++)
                for (int j = 0; j < sub.TrianglesPerIsland; j++)
                {
                    float ix = i * 10f;
                    for (int v = 0; v < 3; v++)
                    {
                        float x = (v == 0 ? ix : ix + 1f) + sub.OffsetX;
                        float y = (v == 0 ? 0f : j + (v == 2 ? 1f : 0f)) + sub.OffsetY;
                        float u = v == 0 ? 0f : 0.5f, w = v == 2 ? 0.5f : 0f;
                        if (!skinned)
                        {
                            var vtx = new byte[Stride];
                            BitConverter.GetBytes(x).CopyTo(vtx, 0);
                            BitConverter.GetBytes(y).CopyTo(vtx, 4);
                            BitConverter.GetBytes(sub.OffsetZ).CopyTo(vtx, 8);
                            BitConverter.GetBytes(u).CopyTo(vtx, 12);
                            BitConverter.GetBytes(w).CopyTo(vtx, 16);
                            stream0.Write(vtx);
                        }
                        else
                        {
                            var vtx = new byte[SkinnedStride];
                            BitConverter.GetBytes(x).CopyTo(vtx, 0);
                            BitConverter.GetBytes(y).CopyTo(vtx, 4);
                            BitConverter.GetBytes(sub.OffsetZ).CopyTo(vtx, 8);
                            var (weights, indices) = Blend(sub.Weights ?? [("n_root", 1f)], tableBones);
                            weights.CopyTo(vtx, 12);
                            indices.CopyTo(vtx, 16);
                            stream0.Write(vtx);

                            var uvBytes = new byte[UvStride];
                            BitConverter.GetBytes(u).CopyTo(uvBytes, 0);
                            BitConverter.GetBytes(w).CopyTo(uvBytes, 4);
                            stream1.Write(uvBytes);
                        }

                        var idx = new byte[2];
                        BitConverter.TryWriteBytes(idx, (ushort)(meshVerts + v));
                        iBuf.Write(idx);
                    }
                    meshVerts += 3;
                    indexCursor += 3;
                }
            }

            stream0.Position = 0; stream0.CopyTo(vBuf);
            uint meshUvOffset = (uint)vBuf.Position;
            stream1.Position = 0; stream1.CopyTo(vBuf);

            var mo = new byte[36];
            W16(mo, 0, meshVerts);
            W32(mo, 4, (uint)(mesh.Submeshes.Sum(x => x.TriangleCount) * 3));  // indexCount
            W16(mo, 8, (ushort)materials.IndexOf(mesh.Material));
            W16(mo, 10, (ushort)(subBytes.Count - mesh.Submeshes.Length));     // submeshIndex
            W16(mo, 12, (ushort)mesh.Submeshes.Length);
            W16(mo, 14, 0);                                                    // bone table
            W32(mo, 16, meshStartIndex);
            W32(mo, 20, meshVtxOffset);
            if (!skinned)
            {
                mo[32] = Stride;
                mo[35] = 1;                                                    // one vertex stream
            }
            else
            {
                W32(mo, 24, meshUvOffset);
                mo[32] = SkinnedStride;
                mo[33] = UvStride;
                mo[35] = 2;                                                    // position+skin, then uv
            }
            meshBytes.Add(mo);
            vertexCursor += meshVerts;
        }
        _ = vertexCursor;

        // ── assemble ──
        var ms = new MemoryStream();
        ms.Write(new byte[0x44]);                                              // file header, patched below

        var decl = new byte[DeclSize];
        for (int i = 0; i < DeclSize; i++) decl[i] = 0xFF;
        WriteElem(decl, 0, 0, 0, Float3, UsePosition, 0);
        if (!skinned) WriteElem(decl, 1, 0, 12, Float2, UseUV, 0);
        else
        {
            WriteElem(decl, 1, 0, 12, UByte4, UseBlendWeight, 0);
            WriteElem(decl, 2, 0, 16, UByte4, UseBlendIndices, 0);
            WriteElem(decl, 3, 1, 0, Float2, UseUV, 0);
        }
        for (int m = 0; m < meshes.Length; m++) ms.Write(decl);

        ms.Write(new byte[4]);                                                 // string count (unused)
        var lenBuf = new byte[4];
        BitConverter.TryWriteBytes(lenBuf, (uint)strings.Length);
        ms.Write(lenBuf);
        ms.Write(strings);

        var mh = new byte[56];
        BitConverter.GetBytes(1f).CopyTo(mh, 0);                               // radius
        W16(mh, 4, (ushort)meshes.Length);
        W16(mh, 6, (ushort)attrNames.Count);
        W16(mh, 8, (ushort)subTotal);
        W16(mh, 10, (ushort)materials.Count);
        W16(mh, 12, (ushort)bones.Length);
        W16(mh, 14, 1);                                                        // one bone table
        W16(mh, 16, (ushort)shapes.Count);                                     // shapeCount
        W16(mh, 18, (ushort)shapes.Count);                                     // one ShapeMesh each
        W16(mh, 20, (ushort)shapes.Count);                                     // one ShapeValue each
        mh[22] = 1;                                                            // lodCount
        BitConverter.GetBytes(1f).CopyTo(mh, 28);                              // model clip
        BitConverter.GetBytes(1f).CopyTo(mh, 32);                              // shadow clip
        // v6 only: the size of the shared bone-index pool, padded even — the TABLE's size, not the name list's.
        // v5 has no pool, so this stays 0 there, and a reader that consults it regardless is exactly the bug the
        // v5 fixture exists to catch.
        W16(mh, 44, version == V6 ? (ushort)((tableBones.Count + 1) & ~1) : (ushort)0);
        ms.Write(mh);

        long lodPos = ms.Position;
        ms.Write(new byte[3 * 60]);                                            // LODs, patched below

        foreach (var m in meshBytes) ms.Write(m);
        foreach (var off in attrOff) ms.Write(U32(off));                       // between meshes and submeshes
        foreach (var sb in subBytes) ms.Write(sb);
        foreach (var off in matOff) ms.Write(U32(off));
        foreach (var off in boneOff) ms.Write(U32(off));

        if (version == V6)
        {
            // One v6 bone table: { u16 offsetInDwords, u16 count } then the entries (every bone, in order), padded to
            // an even count.
            ms.Write(U16Bytes(1));
            ms.Write(U16Bytes((ushort)tableBones.Count));
            foreach (var tb in tableBones) ms.Write(U16Bytes((ushort)Array.IndexOf(bones, tb)));
            if (tableBones.Count % 2 == 1) ms.Write(U16Bytes(0));
        }
        else
        {
            // One v5 bone table: u16 BoneIndex[64], then u8 BoneCount and 3 bytes of padding. Fixed width,
            // no shared pool — index 0 is the single bone, the rest stay zero.
            var table = new byte[V5BoneTableBytes];
            for (int b = 0; b < tableBones.Count; b++)
                BitConverter.TryWriteBytes(table.AsSpan(b * 2), (ushort)Array.IndexOf(bones, tableBones[b]));
            table[128] = (byte)tableBones.Count;
            ms.Write(table);
        }

        // ── shape block: Shape[] (16 B), then ShapeMesh[] (12 B), then ShapeValue[] (4 B) ──
        for (int i = 0; i < shapes.Count; i++)
        {
            var shp = new byte[16];
            W32(shp, 0, shapeOff[i]);                                          // name offset
            W16(shp, 4, (ushort)i);                                            // LOD0 shapeMeshStart
            W16(shp, 10, 1);                                                   // LOD0 shapeMeshCount
            ms.Write(shp);
        }
        for (int i = 0; i < shapes.Count; i++)
        {
            var sm = new byte[12];
            W32(sm, 0, 0);                                                     // mesh index offset
            W32(sm, 4, 1);                                                     // value count
            W32(sm, 8, (uint)i);                                               // value start
            ms.Write(sm);
        }
        for (int i = 0; i < shapes.Count; i++)
        {
            var sv = new byte[4];
            W16(sv, 0, 0);                                                     // base indices index
            W16(sv, 2, 0);                                                     // replacing vertex index
            ms.Write(sv);
        }

        // Submesh bone map: one window PER SUBMESH, each holding the table's bones as INDICES into the name list
        // — the same thing a real model holds, and what each submesh above claims a window onto.
        ms.Write(U32((uint)(subTotal * tableBones.Count * 2)));
        for (int i = 0; i < subTotal; i++)
            foreach (var tb in tableBones) ms.Write(U16Bytes((ushort)IndexOf(bones, tb)));
        ms.WriteByte(0);                                                       // padding amount

        ms.Write(new byte[4 * BBoxSize]);                                      // model bounding boxes
        ms.Write(new byte[bones.Length * BBoxSize]);                           // per-bone bounding boxes

        uint vtxOff = (uint)ms.Position;
        vBuf.Position = 0; vBuf.CopyTo(ms);
        uint idxOff = (uint)ms.Position;
        iBuf.Position = 0; iBuf.CopyTo(ms);

        // Trailing slack, and it is load-bearing for a model this small. SecondSkinWriter's geometry reader
        // bounds-checks every vertex element against a blanket 16 bytes — the width of the widest type it
        // can decode — rather than the width of the element it is actually reading. A real .mdl always has
        // enough after its last vertex to satisfy that; a one-mesh fixture does not, and the reader quietly
        // reports "no geometry" for a model that is perfectly well formed. Sixteen bytes buys the guard
        // what it asks for without pretending the file needs them.
        ms.Write(new byte[16]);

        var o = ms.ToArray();
        uint stackSize = (uint)(meshes.Length * DeclSize);
        W32(o, 0, version);                                                    // version
        W32(o, 4, stackSize);
        W32(o, 8, (uint)(vtxOff - 0x44 - stackSize));                          // runtime size
        W16(o, 12, (ushort)meshes.Length);                                     // one declaration per mesh
        W16(o, 14, (ushort)materials.Count);
        W32(o, 16, vtxOff);
        W32(o, 28, idxOff);
        W32(o, 40, (uint)vBuf.Length);
        W32(o, 52, (uint)iBuf.Length);
        o[64] = 1;                                                             // lodCount

        int ol = (int)lodPos;
        W16(o, ol, 0);
        W16(o, ol + 2, (ushort)meshes.Length);
        W32(o, ol + 44, (uint)vBuf.Length);
        W32(o, ol + 48, (uint)iBuf.Length);
        W32(o, ol + 52, vtxOff);
        W32(o, ol + 56, idxOff);
        for (int l = 1; l < 3; l++)                                            // LOD 1/2 carry no meshes
        {
            W16(o, ol + l * 60, (ushort)meshes.Length);
            W16(o, ol + l * 60 + 2, 0);
        }
        return o;
    }

    /// <summary>Four weight bytes summing to 255 and four SLOTS in the bone table.</summary>
    private static (byte[] Weights, byte[] Indices) Blend((string Bone, float W)[] weights,
                                                          IReadOnlyList<string> bones)
    {
        var wb = new byte[4];
        var ib = new byte[4];
        int total = 0;
        for (int k = 0; k < weights.Length && k < 4; k++)
        {
            wb[k] = (byte)Math.Clamp((int)MathF.Round(weights[k].W * 255f), 0, 255);
            ib[k] = (byte)SlotOf(bones, weights[k].Bone);
            total += wb[k];
        }
        wb[0] = (byte)Math.Clamp(wb[0] + (255 - total), 0, 255);
        return (wb, ib);
    }

    private static void WriteElem(byte[] d, int slot, byte stream, byte offset, byte type, byte usage, byte usageIndex)
    {
        int at = slot * 8;
        d[at] = stream; d[at + 1] = offset; d[at + 2] = type; d[at + 3] = usage; d[at + 4] = usageIndex;
        d[at + 5] = d[at + 6] = d[at + 7] = 0;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>As <see cref="IndexOf"/>, but a miss is a broken fixture rather than a blend index of 255.</summary>
    private static int SlotOf(IReadOnlyList<string> names, string name)
        => IndexOf(names, name) is var at && at >= 0 ? at
               : throw new ArgumentException($"'{name}' has no slot in the bone table");

    private static byte[] U32(uint v) { var b = new byte[4]; BitConverter.TryWriteBytes(b, v); return b; }
    private static byte[] U16Bytes(ushort v) { var b = new byte[2]; BitConverter.TryWriteBytes(b, v); return b; }
    private static void W16(byte[] b, int o, ushort v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
    private static void W32(byte[] b, int o, uint v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
}
