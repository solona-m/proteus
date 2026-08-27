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
    private const byte Float2 = 1, Float3 = 2;
    private const byte UsePosition = 0, UseUV = 4;

    private const int DeclSize = 17 * 8;
    private const int BBoxSize = 32;
    private const int Stride = 20;   // position float3 at 0, uv float2 at 12

    /// <summary>One submesh: three vertices forming a triangle, plus the attribute bits it is tagged with.
    /// The mask indexes the model's own attribute list, which is what the merge has to renumber.</summary>
    internal sealed record Sub(uint AttrMask);

    /// <summary>One LOD0 mesh, drawn with <paramref name="Material"/>.</summary>
    internal sealed record Mesh(string Material, params Sub[] Submeshes);

    /// <summary>
    /// Assemble the model. <paramref name="attrNames"/> is the attribute table in the order submesh masks
    /// index it — bit <c>i</c> of a mask means <c>attrNames[i]</c>.
    /// </summary>
    internal static byte[] Build(IReadOnlyList<string> attrNames, params Mesh[] meshes)
    {
        var materials = meshes.Select(m => m.Material).Distinct(StringComparer.Ordinal).ToList();
        var bones = new[] { "n_root" };

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
        var boneOff = Intern(bones);
        var attrOff = Intern(attrNames);
        var matOff  = Intern(materials);
        var strings = strMs.ToArray();

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

            foreach (var sub in mesh.Submeshes)
            {
                var so = new byte[16];
                W32(so, 0, indexCursor);
                W32(so, 4, 3);
                W32(so, 8, sub.AttrMask);
                W16(so, 12, 0);   // boneStart
                W16(so, 14, 1);   // boneCount
                subBytes.Add(so);

                // Three vertices, mesh-relative indices, spread so the model's bounding box is not degenerate.
                for (int v = 0; v < 3; v++)
                {
                    var vtx = new byte[Stride];
                    BitConverter.GetBytes(v == 0 ? 0f : 1f).CopyTo(vtx, 0);
                    BitConverter.GetBytes(v == 2 ? 1f : 0f).CopyTo(vtx, 4);
                    BitConverter.GetBytes(0f).CopyTo(vtx, 8);
                    BitConverter.GetBytes(v == 0 ? 0f : 0.5f).CopyTo(vtx, 12);
                    BitConverter.GetBytes(v == 2 ? 0.5f : 0f).CopyTo(vtx, 16);
                    vBuf.Write(vtx);

                    var idx = new byte[2];
                    BitConverter.TryWriteBytes(idx, (ushort)(meshVerts + v));
                    iBuf.Write(idx);
                }
                meshVerts += 3;
                indexCursor += 3;
            }

            var mo = new byte[36];
            W16(mo, 0, meshVerts);
            W32(mo, 4, (uint)(mesh.Submeshes.Length * 3));                     // indexCount
            W16(mo, 8, (ushort)materials.IndexOf(mesh.Material));
            W16(mo, 10, (ushort)(subBytes.Count - mesh.Submeshes.Length));     // submeshIndex
            W16(mo, 12, (ushort)mesh.Submeshes.Length);
            W16(mo, 14, 0);                                                    // bone table
            W32(mo, 16, meshStartIndex);
            W32(mo, 20, meshVtxOffset);
            mo[32] = Stride;
            mo[35] = 1;                                                        // one vertex stream
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
        WriteElem(decl, 1, 0, 12, Float2, UseUV, 0);
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
        mh[22] = 1;                                                            // lodCount
        BitConverter.GetBytes(1f).CopyTo(mh, 28);                              // model clip
        BitConverter.GetBytes(1f).CopyTo(mh, 32);                              // shadow clip
        W16(mh, 44, 2);                                                        // bone table shorts, padded even
        ms.Write(mh);

        long lodPos = ms.Position;
        ms.Write(new byte[3 * 60]);                                            // LODs, patched below

        foreach (var m in meshBytes) ms.Write(m);
        foreach (var off in attrOff) ms.Write(U32(off));                       // between meshes and submeshes
        foreach (var sb in subBytes) ms.Write(sb);
        foreach (var off in matOff) ms.Write(U32(off));
        foreach (var off in boneOff) ms.Write(U32(off));

        // One v6 bone table: { u16 offsetInDwords, u16 count } then the entries, padded to an even count.
        ms.Write(U16Bytes(1));
        ms.Write(U16Bytes((ushort)bones.Length));
        ms.Write(U16Bytes(0));
        ms.Write(U16Bytes(0));                                                 // pad to an even short count

        ms.Write(U32(0));                                                      // submesh bone map: no bytes
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
        W32(o, 0, 0x01000006);                                                 // version
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

    private static void WriteElem(byte[] d, int slot, byte stream, byte offset, byte type, byte usage, byte usageIndex)
    {
        int at = slot * 8;
        d[at] = stream; d[at + 1] = offset; d[at + 2] = type; d[at + 3] = usage; d[at + 4] = usageIndex;
        d[at + 5] = d[at + 6] = d[at + 7] = 0;
    }

    private static byte[] U32(uint v) { var b = new byte[4]; BitConverter.TryWriteBytes(b, v); return b; }
    private static byte[] U16Bytes(ushort v) { var b = new byte[2]; BitConverter.TryWriteBytes(b, v); return b; }
    private static void W16(byte[] b, int o, ushort v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
    private static void W32(byte[] b, int o, uint v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
}
