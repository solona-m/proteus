using System;
using System.Collections.Generic;
using System.Text;

namespace Proteus.Services;

public static partial class SecondSkinWriter
{
    private sealed class MdlParser
    {
        private readonly byte[] s;
        private bool isV6;
        private ushort declCount;
        private uint vtxOff;
        private uint idxOff;
        private int declEnd;
        private VElem[][] decls = null!;
        private uint strSize;
        private int strBlock;
        private int mh;
        private ushort meshCount;
        private ushort attrCount;
        private ushort submeshCount;
        private ushort matCount;
        private ushort boneCount;
        private ushort boneTableCount;
        private ushort shapeCount;
        private ushort shapeMeshCount;
        private ushort shapeValueCount;
        private byte flags1;
        private byte flags2;
        private byte tsMesh;
        private ushort tsSubmesh;
        private int lodStart;
        private ushort lod0MeshIndex;
        private ushort lod0MeshCount;
        private int meshStart;
        private int attrStart;
        private int submeshStart;
        private int matOffStart;
        private int p;
        private string[] boneNames = null!;
        private string[] attrNames = null!;
        private ushort[][] tables = null!;
        private Dictionary<string, List<ShapeMeshEntry>> shapes = null!;
        private int shapeBlock;

        public MdlParser(byte[] s)
        {
            this.s = s;
        }

        public Source Run()
        {
            ReadHeader();
            ReadDeclarations();
            ReadMeshLayout();
            ReadBoneTables();
            ReadShapes();
            return ReadTail();
        }

        private void ReadHeader()
        {
            // Dawntrail (v6) or earlier (v5). Only the bone-table block differs — see the read below.
            isV6 = U32(0) >= MdlVersionV6;

            declCount = U16(12);
            vtxOff = U32(16);
            idxOff = U32(28);
            declEnd = 0x44 + declCount * DeclSize;
        }

        private void ReadDeclarations()
        {
            // Vertex declarations: declCount blocks of up to 17 elements (8 bytes each), one block per mesh,
            // terminated by a Stream == 0xFF sentinel. { Stream, Offset, Type, Usage, UsageIndex, 3× pad }.
            decls = new VElem[declCount][];
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
            strSize = U32(declEnd + 4);
            strBlock = declEnd + 8;
            mh = strBlock + (int)strSize;

            meshCount = U16(mh + 4);
            attrCount = U16(mh + 6);
            submeshCount = U16(mh + 8);
            matCount = U16(mh + 10);
            boneCount = U16(mh + 12);
            boneTableCount = U16(mh + 14);
            shapeCount = U16(mh + 16);
            shapeMeshCount = U16(mh + 18);
            shapeValueCount = U16(mh + 20);
            flags1 = s[mh + 23];
            flags2 = s[mh + 27];
            ushort elemCount = U16(mh + 24);
            tsMesh = s[mh + 26];
            tsSubmesh = U16(mh + 38);

            lodStart = mh + 56 + elemCount * 32;
        }

        private void ReadMeshLayout()
        {
            // LOD0's mesh range; only LOD0 is ever shelled. LOD struct: { u16 MeshIndex, u16 MeshCount, … }.
            lod0MeshIndex = U16(lodStart);
            lod0MeshCount = U16(lodStart + 2);
            meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);
            attrStart = meshStart + meshCount * 36;
            submeshStart = attrStart + attrCount * 4 + tsMesh * 20;
            matOffStart = submeshStart + submeshCount * 16 + tsSubmesh * 12;
            int boneOffStart = matOffStart + matCount * 4;
            p = boneOffStart + boneCount * 4;

            boneNames = new string[boneCount];
            for (int i = 0; i < boneCount; i++) boneNames[i] = Str(U32(boneOffStart + i * 4));

            // Attribute names, in the order the submesh masks index them — see Source.AttrNames.
            attrNames = new string[attrCount];
            for (int i = 0; i < attrCount; i++) attrNames[i] = Str(U32(attrStart + i * 4));
        }

        private void ReadBoneTables()
        {
            // ── Bone tables ──────────────────────────────────────────────────────
            // The ONE block whose layout changed at Dawntrail; everything after it is positioned relative to its
            // end, and mods still ship v5 models.
            tables = new ushort[boneTableCount][];
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
                    // CLAMPED both ways: BoneCount is read straight out of the file, and a value past int.MaxValue
                    // casts NEGATIVE, so Math.Min alone would let `new ushort[negative]` throw out of Parse.
                    long declared = at + V5BoneTableBytes <= s.Length ? U32(at + 128) : 0;
                    var t = new ushort[Math.Clamp(declared, 0, 64)];
                    for (int k = 0; k < t.Length; k++) t[k] = U16(at + k * 2);
                    tables[i] = t;
                }
                p += boneTableCount * V5BoneTableBytes;
            }
        }

        private void ReadShapes()
        {
            // ── Shape (morph) block ──────────────────────────────────────────────
            // Layout: Shape[shapeCount] (16 B) then ShapeMesh[shapeMeshCount] (12 B) then ShapeValue[..] (4 B).
            //   Shape:     u32 nameOffset; u16 shapeMeshStart[3]; u16 shapeMeshCount[3]   (LOD0 = index 0)
            //   ShapeMesh: u32 meshIndexOffset; u32 valueCount; u32 valueStart
            //   ShapeValue:u16 baseIndicesIndex; u16 replacingVertexIndex
            // LOD0 only. Bounds-guarded: a malformed block leaves Shapes empty.
            shapes = new Dictionary<string, List<ShapeMeshEntry>>(StringComparer.Ordinal);
            shapeBlock = p;
            int shapeMeshBlock = p + shapeCount * 16;
            int shapeValBlock = p + shapeCount * 16 + shapeMeshCount * 12;
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
        }

        private Source ReadTail()
        {
            // CLAMPED to what is left in the file: this length is read straight off the disk, and a corrupt one
            // would allocate gigabytes. A short map degrades to geometry the shell does not split.
            uint mapBytes = U32(p); p += 4;
            var map = new ushort[Math.Clamp((long)mapBytes, 0, Math.Max(0, s.Length - p)) / 2];
            for (int i = 0; i < map.Length; i++) map[i] = U16(p + i * 2);
            p += (int)mapBytes;

            // Two tables only FACE models carry, walked past unread: the neck morph table (count at header +43,
            // 32 bytes each) and the Patch 7.2 table of 16-byte records (count, u16, at header +48). Layout per
            // xivModdingFramework's Mdl.cs.
            if (isV6)
            {
                int neckMorphs = s[mh + 43], patch72 = U16(mh + 48);
                p += neckMorphs * 32 + patch72 * 16;
            }

            byte padding = s[p]; p += 1 + padding;

            int modelBBAt = p;
            var modelBB = new byte[4 * BBoxSize];
            Array.Copy(s, p, modelBB, 0, Math.Min(modelBB.Length, s.Length - p));
            p += 4 * BBoxSize;

            int boneBBAt = p;
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
                ModelBBoxAt = modelBBAt,
                BoneBBoxAt = boneBBAt,
                Radius = BitConverter.ToSingle(s, mh),
                ModelClip = BitConverter.ToSingle(s, mh + 28),
                ShadowClip = BitConverter.ToSingle(s, mh + 32),
                Flags1 = flags1,
                Flags2 = flags2,
                Lods = lods,
                Shapes = shapes,
            };
        }

        private uint U32(int o) => BitConverter.ToUInt32(s, o);

        private ushort U16(int o) => BitConverter.ToUInt16(s, o);

        // Bounds-guarded: the shape block reads a NAME before it can judge anything, and an offset outside the
        // string table must leave Shapes empty rather than throw out of the whole parse.
        private string Str(uint rel)
        {
            int o = strBlock + (int)rel;
            // >= strSize, not > : an offset EQUAL to the block size is one past its last byte, which lands on
            // the model header and reads its bytes back as a name.
            if (rel >= strSize || o < 0 || o >= s.Length) return "";
            int e = o;
            while (e < s.Length && s[e] != 0) e++;
            return Encoding.ASCII.GetString(s, o, e - o);
        }
    }
}
