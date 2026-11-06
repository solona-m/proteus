using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Proteus.Services;

/// <summary>
/// Moves chosen triangles of a model onto a NEW material — the matcap tool's split. A model binds its material per
/// MESH, so triangles drawn with another material need a mesh of their own.
/// <para/>
/// The split is copy-and-collapse: each touched mesh M gets a copy M′ appended at the end of the mesh table — its
/// vertex declaration, vertex streams, index run, submesh records and shape entries, byte for byte — bound to the new
/// material. Then the moved triangles are collapsed in M and every other triangle in M′, with
/// <see cref="ModelPartHider.CollapseTriangles"/>, which also rewires every shape value on a collapsed slot so no body
/// slider can open one again. So nothing is renumbered: M keeps its vertices, index slots, submesh records and
/// ordinals, M′'s shapes and bone table are verbatim (vertex indices are mesh-local, shape windows are re-based by the
/// same amount as the copied run), and the only cost is the copy's bytes. A mesh whose every triangle moves is simply
/// rebound, with no copy.
/// <para/>
/// Considered and rejected: compacting M′ to the vertices it uses (new binary code in a crash-sensitive writer, with
/// shape-value remapping and submesh renumbering), and the second-skin host append (it drops shape keys).
/// <para/>
/// The file is re-emitted the way <see cref="ModelLodTrimmer"/> does it — parse, check every table is where the
/// format says, emit, read back. A multi-LOD model is first cut to LOD0, so the new mesh is drawn at every distance.
/// What this does not understand is refused and the model is left alone.
/// </summary>
internal static class ModelMaterialWriter
{
    private const int FileHeaderSize = 0x44;
    private const int LodSize = 60, MeshSize = 36, SubmeshSize = 16;
    private const int ShapeSize = 16, ShapeMeshSize = 12, ShapeValueSize = 4;

    /// <summary>Why a model can't take the split. <see cref="Code"/> is stable (the harness reports it).</summary>
    public sealed class Refused(string code, string reason) : Exception(reason)
    {
        public string Code { get; } = code;
    }

    /// <summary>The material table is full: <see cref="SecondSkinWriter.MaxMaterials"/>, Penumbra's import limit.</summary>
    public const string CodeMaterialCap = "material-cap";

    /// <summary>The layout is one this writer (or the LOD cut before it) does not handle.</summary>
    public const string CodeLayout = "model-layout";

    /// <summary>A part named triangles the model does not have.</summary>
    public const string CodeUnknownPart = "unknown-part";

    /// <param name="Model">The new file.</param>
    /// <param name="MaterialIndex">The new material's index in the material table.</param>
    /// <param name="Meshes">Per touched source mesh, the mesh that now draws the moved triangles: a new copy, or the
    /// source itself when it moved whole.</param>
    /// <param name="Lod0Cut">The model was cut to its first level of detail first.</param>
    public sealed record Result(byte[] Model, int MaterialIndex, IReadOnlyList<(int Source, int Drawn)> Meshes, bool Lod0Cut)
    {
        public int MeshesAdded => Meshes.Count(m => m.Source != m.Drawn);
    }

    /// <summary>How many more materials the model can take before <see cref="SecondSkinWriter.MaxMaterials"/>.</summary>
    public static int MaterialRoom(byte[] mdl)
    {
        var src = SecondSkinWriter.Parse(mdl);
        return Math.Max(0, SecondSkinWriter.MaxMaterials - src.MatCount);
    }

    /// <summary>
    /// The model with <paramref name="parts"/>' triangles drawn by a new material named <paramref name="materialName"/>.
    /// The parts are <see cref="ModelPartReader.Read"/>'s, read from THIS <paramref name="mdl"/>.
    /// </summary>
    /// <exception cref="Refused">The model or the parts can't take it; nothing was changed.</exception>
    public static Result MoveToMaterial(byte[] mdl, IReadOnlyList<ModelPart> parts, string materialName)
    {
        if (parts.Count == 0) throw new Refused(CodeUnknownPart, "no parts were chosen");
        if (string.IsNullOrEmpty(materialName) || Encoding.ASCII.GetByteCount(materialName) != materialName.Length)
            throw new Refused(CodeLayout, "the new material name is not plain text");

        bool cut = false;
        var s = mdl;
        if (ModelLodTrimmer.LodCount(s) > 1)
        {
            s = ModelLodTrimmer.KeepLod0(s, out var why)
                ?? throw new Refused(CodeLayout, why);
            cut = true;
        }

        SecondSkinWriter.Source src;
        try { src = SecondSkinWriter.Parse(s); }
        catch (Exception ex) { throw new Refused(CodeLayout, $"it could not be read ({ex.Message})"); }

        if (src.MatNames.Contains(materialName, StringComparer.OrdinalIgnoreCase))
            throw new Refused(CodeLayout, $"it already names {materialName}");
        if (src.MatCount + 1 > SecondSkinWriter.MaxMaterials)
            throw new Refused(CodeMaterialCap,
                $"it already has {src.MatCount} materials, and a model can carry at most {SecondSkinWriter.MaxMaterials}");

        byte[] o;
        List<(int Source, int Drawn)> meshes;
        try
        {
            (o, meshes) = Emit(s, src, parts, materialName);
        }
        catch (Refused) { throw; }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or IOException or OverflowException)
        {
            throw new Refused(CodeLayout, $"its tables are not where the format says they are ({ex.Message})");
        }

        // Read back the way everything downstream will read it.
        try
        {
            var back = SecondSkinWriter.Parse(o);
            if (back.MeshCount != src.MeshCount + meshes.Count(m => m.Source != m.Drawn)
             || back.MatNames.Count != src.MatCount + 1
             || !string.Equals(back.MatNames[^1], materialName, StringComparison.Ordinal)
             || ModelPartReader.Read(o) == null)
                throw new Refused(CodeLayout, "the split model did not read back");
        }
        catch (Refused) { throw; }
        catch (Exception ex) { throw new Refused(CodeLayout, $"the split model did not read back ({ex.Message})"); }

        return new Result(o, src.MatCount, meshes, cut);
    }

    private static (byte[] Model, List<(int Source, int Drawn)> Meshes) Emit(
        byte[] s, SecondSkinWriter.Source src, IReadOnlyList<ModelPart> parts, string materialName)
    {
        int mh = src.Mh;
        bool v6 = U32(s, 0) >= SecondSkinWriter.MdlVersionV6;
        ushort meshCount = U16(s, mh + 4), attrCount = U16(s, mh + 6), submeshCount = U16(s, mh + 8);
        ushort matCount = U16(s, mh + 10), boneCount = U16(s, mh + 12);
        ushort shapeCount = U16(s, mh + 16), shapeMeshCount = U16(s, mh + 18), shapeValueCount = U16(s, mh + 20);

        // ── what this writer understands: one level of detail holding every mesh ──
        if (s[mh + 22] != 1 || s[64] != 1) Layout("it has more than one level of detail");
        if ((s[mh + 27] & 0x10) != 0) Layout("it carries extra levels of detail");
        if (s[mh + 26] != 0 || U16(s, mh + 38) != 0) Layout("it has terrain shadow meshes");
        if (U16(s, 12) != meshCount) Layout("its vertex declarations do not match its meshes");
        int lod0 = src.LodStart;
        if (U16(s, lod0) != 0 || U16(s, lod0 + 2) != meshCount)
            Layout("some of its meshes are not in its first level of detail");
        if (U16(s, lod0 + 14) != 0 || U16(s, lod0 + 18) != 0 || U16(s, lod0 + 22) != 0 || U16(s, lod0 + 26) != 0)
            Layout("it has special-purpose meshes");
        if (U32(s, lod0 + 28) != 0) Layout("it has edge geometry");

        long vb = U32(s, 16), ib = U32(s, 28), vSize = U32(s, 40), iSize = U32(s, 52);
        if (vb != src.Vb || ib != src.Ib || ib < vb + vSize || ib + iSize > s.Length)
            Layout("its vertex data is not laid out in order");

        int boneOffEnd = src.MatOffStart + (matCount + boneCount) * 4;
        int shapeMeshAt = src.ShapeBlock + shapeCount * ShapeSize;
        int shapeValueAt = shapeMeshAt + shapeMeshCount * ShapeMeshSize;
        int shapesEnd = shapeValueAt + shapeValueCount * ShapeValueSize;
        int padAt = shapesEnd + 4 + (int)U32(s, shapesEnd);
        if (v6) padAt += s[mh + 43] * 32 + U16(s, mh + 48) * 16;
        if (padAt + 1 + s[padAt] != src.ModelBBoxAt || src.ModelBBoxAt > vb)
            Layout("its tables are not where the format says they are");

        // ── which triangles move, per mesh, as absolute index-buffer slots ──
        var moving = new SortedDictionary<int, HashSet<long>>();
        foreach (var part in parts)
        {
            if (part.Mesh < 0 || part.Mesh >= meshCount) Unknown($"mesh {part.Mesh} is not in the model");
            int mo = src.MeshStart + part.Mesh * MeshSize;
            ushort subIdx = U16(s, mo + 10), subCnt = U16(s, mo + 12);
            if (part.Submesh < 0 || part.Submesh >= subCnt)
                Unknown($"mesh {part.Mesh} has no submesh {part.Submesh}");
            int ss = src.SubmeshStart + (subIdx + part.Submesh) * SubmeshSize;
            uint so = U32(s, ss), sc = U32(s, ss + 4);
            if (!moving.TryGetValue(part.Mesh, out var set)) moving[part.Mesh] = set = [];
            foreach (var ord in part.Ordinals)
            {
                if (ord < 0 || (long)ord * 3 + 3 > sc)
                    Unknown($"part {part.Label} names a triangle its submesh does not have");
                set.Add(so + (long)ord * 3);
            }
        }

        // ── per touched mesh: rebind whole, or copy ──
        var copies = new List<int>();            // source meshes that get a copy, in mesh order
        var wholes = new List<int>();
        foreach (var (mesh, set) in moving)
        {
            int mo = src.MeshStart + mesh * MeshSize;
            uint ic = U32(s, mo + 4), start = U32(s, mo + 16);
            if ((long)ib + ((long)start + ic) * 2 > s.Length) Layout($"mesh {mesh}'s indices run past the end of the file");
            bool whole = true;
            for (uint t = 0; t + 3 <= ic && whole; t += 3)
            {
                long slot = start + t;
                if (set.Contains(slot)) continue;
                int at = (int)(ib + slot * 2);
                // A triangle already collapsed draws nothing either way.
                if (U16(s, at) == U16(s, at + 2) && U16(s, at) == U16(s, at + 4)) continue;
                whole = false;
            }
            if (whole) wholes.Add(mesh); else copies.Add(mesh);
        }

        int added = copies.Count;
        int newMeshCount = meshCount + added;
        int addedSubmeshes = copies.Sum(m => U16(s, src.MeshStart + m * MeshSize + 12));
        if (newMeshCount > ushort.MaxValue || submeshCount + addedSubmeshes > ushort.MaxValue)
            Layout("it would have more meshes than the format counts");

        // ── the string block, with the name at the end of the MATERIAL region ──
        // The block is grouped by kind (attributes, bones, materials, shapes) and readers that walk it as a list assign
        // names by position, so the new name goes after the last material — see ModelAttributeWriter.AddAttribute.
        uint insertAt = 0;
        for (int i = 0; i < matCount; i++)
        {
            uint end = U32(s, src.MatOffStart + i * 4) + (uint)Encoding.ASCII.GetByteCount(src.MatNames[i]) + 1;
            if (end > insertAt) insertAt = end;
        }
        var nameBytes = new byte[materialName.Length + 1];
        Encoding.ASCII.GetBytes(materialName).CopyTo(nameBytes, 0);
        int nameLen = nameBytes.Length;
        int strPad = (int)((4 - (src.StrSize + nameLen) % 4) % 4);
        var block = new List<byte>(s.AsSpan(src.StrBlock, (int)src.StrSize).ToArray());
        block.InsertRange((int)insertAt, nameBytes);
        for (int i = 0; i < strPad; i++) block.Add(0);
        uint Moved(uint off) => off >= insertAt ? off + (uint)nameLen : off;

        // ── vertex and index data for the copies ──
        // Streams appended after the vertex region, each 16-aligned; index runs appended after the index region,
        // each starting on an 8-index boundary as the game's own runs do.
        using var extraV = new MemoryStream();
        using var extraI = new MemoryStream();
        var copyMesh = new Dictionary<int, byte[]>();
        var copyStart = new Dictionary<int, uint>();
        long vEnd = vSize, iEnd = iSize / 2;
        foreach (var m in copies)
        {
            int mo = src.MeshStart + m * MeshSize;
            var rec = s.AsSpan(mo, MeshSize).ToArray();
            ushort vc = U16(s, mo);
            uint ic = U32(s, mo + 4), start = U32(s, mo + 16);
            for (int j = 0; j < 3; j++)
            {
                byte stride = s[mo + 32 + j];
                if (stride == 0) continue;
                long from = vb + U32(s, mo + 20 + j * 4);
                long len = (long)vc * stride;
                if (from + len > ib) Layout($"mesh {m}'s vertex stream {j} runs past the vertex data");
                while ((vEnd + extraV.Length) % 16 != 0) extraV.WriteByte(0);
                W32(rec, 20 + j * 4, (uint)(vEnd + extraV.Length));
                extraV.Write(s, (int)from, (int)len);
            }
            while ((iEnd + extraI.Length / 2) % 8 != 0) { extraI.WriteByte(0); extraI.WriteByte(0); }
            uint newStart = (uint)(iEnd + extraI.Length / 2);
            extraI.Write(s, (int)(ib + start * 2), (int)ic * 2);
            W32(rec, 16, newStart);
            W16(rec, 8, matCount);                                                         // the new material
            copyMesh[m] = rec;
            copyStart[m] = newStart;
        }
        // The index region ends on a 16-byte boundary, as the game's does.
        while ((iEnd * 2 + extraI.Length) % 16 != 0) extraI.WriteByte(0);

        // ── submesh records for the copies, re-based onto the copied runs ──
        var extraSub = new MemoryStream();
        int subCursor = submeshCount;
        foreach (var m in copies)
        {
            int mo = src.MeshStart + m * MeshSize;
            ushort subIdx = U16(s, mo + 10), subCnt = U16(s, mo + 12);
            uint delta = copyStart[m] - U32(s, mo + 16);
            for (int k = 0; k < subCnt; k++)
            {
                var rec = s.AsSpan(src.SubmeshStart + (subIdx + k) * SubmeshSize, SubmeshSize).ToArray();
                W32(rec, 0, U32(rec, 0) + delta);
                extraSub.Write(rec);
            }
            W16(copyMesh[m], 10, (ushort)subCursor);
            subCursor += subCnt;
        }

        // ── shapes, rebuilt per shape: its entries, then the copies' entries at the end of its list ──
        var shapeRecords = new byte[shapeCount * ShapeSize];
        using var shapeMeshes = new MemoryStream();
        using var shapeValues = new MemoryStream();
        int outShapeMeshes = 0, outShapeValues = 0;
        for (int si = 0; si < shapeCount; si++)
        {
            int shp = src.ShapeBlock + si * ShapeSize;
            int start = U16(s, shp + 4), count = U16(s, shp + 10);
            if (U16(s, shp + 12) != 0 || U16(s, shp + 14) != 0) Layout("a shape has levels of detail past the first");
            if (start + count > shapeMeshCount) Layout("a shape names meshes it does not have");

            W32(shapeRecords, si * ShapeSize, Moved(U32(s, shp)));
            W16(shapeRecords, si * ShapeSize + 4, (ushort)outShapeMeshes);
            int emitted = 0;

            void EmitEntry(int sm, uint indexOffset)
            {
                uint values = U32(s, sm + 4), first = U32(s, sm + 8);
                if (first + values > shapeValueCount) Layout("a shape names values it does not have");
                var record = new byte[ShapeMeshSize];
                W32(record, 0, indexOffset);
                W32(record, 4, values);
                W32(record, 8, (uint)outShapeValues);
                shapeMeshes.Write(record);
                shapeValues.Write(s, shapeValueAt + (int)first * ShapeValueSize, (int)values * ShapeValueSize);
                outShapeValues += (int)values;
                emitted++;
            }

            for (int k = 0; k < count; k++)
            {
                int sm = shapeMeshAt + (start + k) * ShapeMeshSize;
                EmitEntry(sm, U32(s, sm));
            }
            // A copy carries every entry its source mesh had in this shape, re-based by the same amount as its run.
            foreach (var m in copies)
            {
                int mo = src.MeshStart + m * MeshSize;
                uint mStart = U32(s, mo + 16), ic = U32(s, mo + 4);
                for (int k = 0; k < count; k++)
                {
                    int sm = shapeMeshAt + (start + k) * ShapeMeshSize;
                    uint off = U32(s, sm);
                    if (off < mStart || off >= mStart + ic) continue;
                    EmitEntry(sm, off - mStart + copyStart[m]);
                }
            }
            W16(shapeRecords, si * ShapeSize + 10, (ushort)emitted);
            outShapeMeshes += emitted;
            if (outShapeMeshes > ushort.MaxValue || outShapeValues > ushort.MaxValue)
                Layout("its shapes would be too large for the format");
        }
        for (int si = 0; si < shapeCount; si++)
        {
            W16(shapeRecords, si * ShapeSize + 6, (ushort)outShapeMeshes);                // LOD1/2: empty, past ours
            W16(shapeRecords, si * ShapeSize + 8, (ushort)outShapeMeshes);
        }

        // ── emit ─────────────────────────────────────────────────────────────
        using var ms = new MemoryStream(s.Length + (int)extraV.Length + (int)extraI.Length + 4096);
        ms.Write(s, 0, FileHeaderSize);                                                    // patched below
        ms.Write(s, FileHeaderSize, meshCount * SecondSkinWriter.DeclSize);
        foreach (var m in copies) ms.Write(s, FileHeaderSize + m * SecondSkinWriter.DeclSize, SecondSkinWriter.DeclSize);
        long declEndOut = ms.Position;
        ms.Write(s, src.DeclEnd, src.StrBlock - src.DeclEnd);                              // string count, size
        ms.Write(block.ToArray());
        ms.Write(s, src.StrBlock + (int)src.StrSize, mh - (src.StrBlock + (int)src.StrSize));
        long mhOut = ms.Position;
        ms.Write(s, mh, src.LodStart - mh);                                                // model header, element ids
        long lodOut = ms.Position;
        ms.Write(s, src.LodStart, src.MeshStart - src.LodStart);
        long meshOut = ms.Position;
        ms.Write(s, src.MeshStart, meshCount * MeshSize);
        foreach (var m in copies) ms.Write(copyMesh[m]);
        ms.Write(s, src.MeshStart + meshCount * MeshSize, src.AttrStart - (src.MeshStart + meshCount * MeshSize));
        long attrOut = ms.Position;
        ms.Write(s, src.AttrStart, src.SubmeshStart - src.AttrStart);
        ms.Write(s, src.SubmeshStart, submeshCount * SubmeshSize);
        extraSub.Position = 0; extraSub.CopyTo(ms);
        ms.Write(s, src.SubmeshStart + submeshCount * SubmeshSize, src.MatOffStart - (src.SubmeshStart + submeshCount * SubmeshSize));
        long matOffOut = ms.Position;
        ms.Write(s, src.MatOffStart, matCount * 4);
        ms.Write(BitConverter.GetBytes(insertAt));                                         // the new material's name
        ms.Write(s, src.MatOffStart + matCount * 4, boneCount * 4);
        ms.Write(s, boneOffEnd, src.ShapeBlock - boneOffEnd);                              // bone tables, verbatim
        ms.Write(shapeRecords);
        shapeMeshes.Position = 0; shapeMeshes.CopyTo(ms);
        shapeValues.Position = 0; shapeValues.CopyTo(ms);
        ms.Write(s, shapesEnd, padAt - shapesEnd);                                         // submesh bone map, face tables

        long bboxes = vb - src.ModelBBoxAt;
        long afterPadByte = ms.Position + 1;
        int pad = (int)(((vb - (afterPadByte + bboxes)) % 16 + 16) % 16);
        ms.WriteByte((byte)pad);
        for (int i = 0; i < pad; i++) ms.WriteByte(0);
        ms.Write(s, src.ModelBBoxAt, (int)bboxes);

        long vbOut = ms.Position;
        ms.Write(s, (int)vb, (int)vSize);
        extraV.Position = 0; extraV.CopyTo(ms);
        long newVSize = ms.Position - vbOut;
        ms.Write(s, (int)(vb + vSize), (int)(ib - (vb + vSize)));                          // any gap, verbatim
        long ibOut = ms.Position;
        ms.Write(s, (int)ib, (int)iSize);
        extraI.Position = 0; extraI.CopyTo(ms);
        long newISize = ms.Position - ibOut;
        long tail = s.Length - (ib + iSize);
        if (tail > 0) ms.Write(s, (int)(ib + iSize), (int)tail);
        var o = ms.ToArray();

        // ── file header ──────────────────────────────────────────────────────
        uint stack = (uint)(newMeshCount * SecondSkinWriter.DeclSize);
        W32(o, 4, stack);
        W32(o, 8, (uint)(vbOut - FileHeaderSize - stack));                                 // runtime size
        W16(o, 12, (ushort)newMeshCount);
        W16(o, 14, (ushort)(matCount + 1));
        W32(o, 16, (uint)vbOut);
        W32(o, 28, (uint)ibOut);
        W32(o, 40, (uint)newVSize);
        W32(o, 52, (uint)newISize);

        // ── string block header ──────────────────────────────────────────────
        int decl = (int)declEndOut;
        W16(o, decl, (ushort)(U16(o, decl) + 1));                                          // string count
        W32(o, decl + 4, (uint)block.Count);                                               // string block size

        // ── model header ─────────────────────────────────────────────────────
        int mo2 = (int)mhOut;
        W16(o, mo2 + 4, (ushort)newMeshCount);
        W16(o, mo2 + 8, (ushort)(submeshCount + addedSubmeshes));
        W16(o, mo2 + 10, (ushort)(matCount + 1));
        W16(o, mo2 + 18, (ushort)outShapeMeshes);
        W16(o, mo2 + 20, (ushort)outShapeValues);
        int elements = U16(o, mo2 + 24);
        for (int i = 0; i < elements; i++)
        {
            int at = mo2 + 56 + i * 32 + 4;                                                // ElementId.ParentBoneName
            if (at + 4 <= o.Length) W32(o, at, Moved(U32(o, at)));
        }

        // ── name offsets that point past the insert ──────────────────────────
        for (int i = 0; i < attrCount; i++) W32(o, (int)attrOut + i * 4, Moved(U32(o, (int)attrOut + i * 4)));
        for (int i = 0; i < matCount; i++) W32(o, (int)matOffOut + i * 4, Moved(U32(o, (int)matOffOut + i * 4)));
        int boneOut = (int)matOffOut + (matCount + 1) * 4;
        for (int i = 0; i < boneCount; i++) W32(o, boneOut + i * 4, Moved(U32(o, boneOut + i * 4)));

        // ── levels of detail ─────────────────────────────────────────────────
        int lo = (int)lodOut;
        W16(o, lo + 2, (ushort)newMeshCount);
        foreach (var f in new[] { 12, 16, 20, 24 })
            if (U16(o, lo + f) == meshCount) W16(o, lo + f, (ushort)newMeshCount);
        W32(o, lo + 44, (uint)newVSize);
        W32(o, lo + 48, (uint)newISize);
        W32(o, lo + 52, (uint)vbOut);
        W32(o, lo + 56, (uint)ibOut);
        for (int l = 1; l < 3; l++)
        {
            int at = lo + l * LodSize;
            foreach (var f in new[] { 0, 12, 16, 20, 24 })
                if (U16(o, at + f) == meshCount) W16(o, at + f, (ushort)newMeshCount);
        }

        // ── rebinds and collapses ────────────────────────────────────────────
        int meshAt = (int)meshOut;
        foreach (var m in wholes) W16(o, meshAt + m * MeshSize + 8, matCount);

        var collapse = new List<long>();
        foreach (var m in copies)
        {
            var set = moving[m];
            int mo = src.MeshStart + m * MeshSize;
            uint ic = U32(s, mo + 4), start = U32(s, mo + 16), newStart = copyStart[m];
            for (uint t = 0; t + 3 <= ic; t += 3)
            {
                // Moved: gone from the source. Not moved: gone from the copy.
                if (set.Contains(start + t)) collapse.Add(start + t);
                else collapse.Add(newStart + t);
            }
        }
        if (collapse.Count > 0) ModelPartHider.CollapseTriangles(o, collapse);

        var result = new List<(int, int)>();
        foreach (var m in moving.Keys)
            result.Add(wholes.Contains(m) ? (m, m) : (m, meshCount + copies.IndexOf(m)));
        return (o, result);
    }

    private static void Layout(string why) => throw new Refused(CodeLayout, why);

    private static void Unknown(string why) => throw new Refused(CodeUnknownPart, why);

    private static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);

    private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);

    private static void W16(byte[] b, int o, ushort v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);

    private static void W32(byte[] b, int o, uint v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
}
