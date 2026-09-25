using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Proteus.Services;

/// <summary>
/// Edits a .mdl's STRUCTURE: splits submeshes, adds attributes and shapes, so welded-on geometry can become a
/// Penumbra checkbox. Inserts relocate everything after them; the few ABSOLUTE offsets are moved by
/// <see cref="Shift"/> alone. A shape key addresses an index-buffer POSITION, so any reorder must remap shapes.
/// </summary>
public static class ModelAttributeWriter
{
    /// <summary>
    /// Attribute masks are 32 bits wide. This is the format's limit, not the (smaller) IMC budget.
    /// </summary>
    public const int MaxAttributes = 32;

    /// <summary>
    /// How many pieces one submesh may be cut into (one record per contiguous run of triangles); more is
    /// pathologically interleaved geometry and is refused.
    /// </summary>
    public const int MaxRuns = 256;

    public sealed class ModelEditException(string message) : InvalidOperationException(message);

    // ── attributes ──────────────────────────────────────────────────────────

    /// <summary>
    /// Take <paramref name="attributeName"/> off LOD0's submeshes, leaving the name in the table; a no-op on a
    /// model that never declared it. For an attribute whose meaning Proteus decides (e.g. <c>atr_kam</c>
    /// inherited from vanilla hair). LOD0 ONLY: the replacement cut can only name LOD0's submeshes.
    /// </summary>
    public static byte[] ClearAttribute(byte[] mdl, string attributeName)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int bit = Array.IndexOf(src.AttrNames, attributeName);
        if (bit < 0) return mdl;

        var o = (byte[])mdl.Clone();
        uint keep = ~(1u << bit);
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > o.Length) break;
            ushort subIdx = BitConverter.ToUInt16(o, mo + 10), subCount = BitConverter.ToUInt16(o, mo + 12);
            for (int s = 0; s < subCount; s++)
            {
                int at = src.SubmeshStart + (subIdx + s) * 16;
                if (at + 16 > o.Length) break;
                W32(o, at + 8, BitConverter.ToUInt32(o, at + 8) & keep);
            }
        }
        return o;
    }

    /// <summary>
    /// Add <paramref name="attributeName"/> to the model's attribute table and tag every named submesh with it.
    /// The name goes at the END of the table (a mask indexes it positionally); the IMC bit comes from the
    /// name's trailing letter, see <c>ContentPieceResolver.PartAttributeBit</c>. An already-declared name is
    /// tagged against its existing bit.
    /// </summary>
    /// <param name="targets">(mesh index, submesh index within that mesh) pairs.</param>
    public static byte[] AddAttribute(
        byte[] mdl, string attributeName, IReadOnlyCollection<(int Mesh, int Submesh)> targets)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int attrCount = src.AttrNames.Length;

        // Already there: tag against the existing bit rather than refusing. Nothing is inserted, so this needs
        // no free slot and must be tested BEFORE the table-full guard.
        int existing = Array.IndexOf(src.AttrNames, attributeName);
        if (existing >= 0)
        {
            var reused = (byte[])mdl.Clone();
            uint reusedBit = 1u << existing;
            foreach (var (mesh, submesh) in targets)
            {
                int mo = src.MeshStart + mesh * 36;
                ushort subIdx = BitConverter.ToUInt16(reused, mo + 10);
                ushort subCount = BitConverter.ToUInt16(reused, mo + 12);
                if (submesh < 0 || submesh >= subCount)
                    throw new ModelEditException($"mesh {mesh} has no submesh {submesh}");
                int at = src.SubmeshStart + (subIdx + submesh) * 16;
                W32(reused, at + 8, BitConverter.ToUInt32(reused, at + 8) | reusedBit);
            }
            return reused;
        }

        // Only a NEW name needs a slot, so the ceiling is enforced here rather than at the top.
        if (attrCount >= MaxAttributes)
            throw new ModelEditException(
                $"this model already declares {attrCount} attributes, which is all a submesh mask can hold");

        var text = Encoding.ASCII.GetBytes(attributeName);
        int nameLen = text.Length + 1;
        // Padded to four bytes so every table after the string block keeps its alignment. The padding goes at the
        // END of the block, never beside the name: a reader that walks the block as a LIST would read each padding
        // NUL as another empty string and shift every name after it.
        int pad = (4 - nameLen % 4) % 4;

        // The name goes at the end of the ATTRIBUTE region, not the end of the block. The block is grouped by kind
        // — attributes, bones, materials, shapes, measured across 2996 and 1064 game and author models — and a
        // reader that walks it in order (Lumina, and so Penumbra and TexTools) assigns the first attributeCount
        // strings to attributes. A name parked after the materials makes that reader take the first BONE name as
        // an attribute and misname every bone after it.
        uint nameOffset = 0;
        for (int i = 0; i < attrCount; i++)
        {
            uint end = BitConverter.ToUInt32(mdl, src.AttrStart + i * 4)
                     + (uint)Encoding.ASCII.GetByteCount(src.AttrNames[i]) + 1;
            if (end > nameOffset) nameOffset = end;
        }

        var nameBytes = new byte[nameLen];
        text.CopyTo(nameBytes, 0);

        int insertStr = src.StrBlock + (int)nameOffset;      // end of the attribute region
        int insertPad = src.StrBlock + (int)src.StrSize;     // end of the block, for the alignment padding
        int insertOff = src.AttrStart + attrCount * 4;       // end of the attribute offset table

        var offBytes = new byte[4];
        BitConverter.TryWriteBytes(offBytes, nameOffset);

        // Ascending positions: Splice rebases each insert past the ones before it.
        var o = pad > 0
            ? Splice(mdl, [(insertStr, nameBytes), (insertPad, new byte[pad]), (insertOff, offBytes)])
            : Splice(mdl, [(insertStr, nameBytes), (insertOff, offBytes)]);
        int dStr = nameLen + pad, delta = dStr + 4;

        // Every name the insert pushed along — the bones, materials and shapes, which all sit after the attributes,
        // and any attribute a model of some other layout happens to keep there. Offsets are relative to the block,
        // so only the VALUES move, by the name's length; the trailing padding sits past them all.
        ShiftNameOffsets(o, src.AttrStart + dStr, attrCount, nameOffset, (uint)nameLen);
        int matOffStart = src.MatOffStart + delta;
        ShiftNameOffsets(o, matOffStart, src.MatCount, nameOffset, (uint)nameLen);
        ShiftNameOffsets(o, matOffStart + src.MatCount * 4, src.BoneCount, nameOffset, (uint)nameLen);
        for (int i = 0; i < src.Shapes.Count; i++)
        {
            int at = src.ShapeBlock + delta + i * 16;
            uint v = BitConverter.ToUInt32(o, at);
            if (v >= nameOffset) W32(o, at, v + (uint)nameLen);
        }

        W32(o, src.DeclEnd + 4, src.StrSize + (uint)dStr);          // string block size
        // The string COUNT: the game ignores it, but Penumbra and TexTools walk exactly this many strings
        // (MdlFile.LoadStrings), so a stale count leaves the new name unreadable there.
        W16(o, src.DeclEnd, (ushort)(BitConverter.ToUInt16(o, src.DeclEnd) + 1));
        W16(o, src.Mh + dStr + 6, (ushort)(attrCount + 1));         // attribute count
        Shift(o, src.LodStart + dStr, delta);

        // The NEW file's coordinates. The mesh table sits between the two inserts, so it moves by the string
        // growth only; the submeshes follow the full delta.
        int meshStart = src.MeshStart + dStr;
        int submeshStart = src.SubmeshStart + delta;
        uint bit = 1u << attrCount;

        foreach (var (mesh, submesh) in targets)
        {
            int mo = meshStart + mesh * 36;
            ushort subIdx = BitConverter.ToUInt16(o, mo + 10), subCount = BitConverter.ToUInt16(o, mo + 12);
            if (submesh < 0 || submesh >= subCount)
                throw new ModelEditException($"mesh {mesh} has no submesh {submesh}");
            int ss = submeshStart + (subIdx + submesh) * 16;
            W32(o, ss + 8, BitConverter.ToUInt32(o, ss + 8) | bit);
        }
        return o;
    }

    /// <summary>
    /// Add <paramref name="by"/> to every entry of a name-offset table that points at or past
    /// <paramref name="from"/> — the names a mid-block insert pushed along.
    /// </summary>
    private static void ShiftNameOffsets(byte[] o, int tableAt, int count, uint from, uint by)
    {
        for (int i = 0; i < count; i++)
        {
            int at = tableAt + i * 4;
            uint v = BitConverter.ToUInt32(o, at);
            if (v >= from) W32(o, at, v + by);
        }
    }

    // ── splitting ───────────────────────────────────────────────────────────

    /// <summary>
    /// Cut one submesh into several at the CONTIGUOUS RUNS of <paramref name="ordinals"/>: nothing moves, and
    /// every record inherits the original's mask and bone window, so a split alone changes nothing drawn.
    /// </summary>
    /// <param name="ordinals">Triangle ordinals within the submesh that belong to the piece being split
    /// out — <see cref="ModelPart.Ordinals"/>.</param>
    /// <returns>The new file, and which of the mesh's submesh indices now hold those triangles.</returns>
    public static (byte[] Model, List<int> Submeshes) SplitSubmesh(
        byte[] mdl, int mesh, int submesh, IReadOnlySet<int> ordinals)
    {
        var (o, byGroup) = SplitSubmesh(mdl, mesh, submesh, t => ordinals.Contains(t) ? 0 : -1);
        return (o, byGroup.TryGetValue(0, out var subs) ? subs : []);
    }

    /// <summary>
    /// <summary>
    /// The general split: <paramref name="groupOf"/> labels each triangle ordinal, and the submesh is cut so
    /// that no record mixes two labels. Several labels in one pass avoid re-cutting records.
    /// </summary>
    /// <returns>The new file, and, per label, the submesh indices now holding its triangles.</returns>
    public static (byte[] Model, Dictionary<int, List<int>> ByGroup) SplitSubmesh(
        byte[] mdl, int mesh, int submesh, Func<int, int> groupOf)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int mo = src.MeshStart + mesh * 36;
        ushort subIdx = BitConverter.ToUInt16(mdl, mo + 10), subCount = BitConverter.ToUInt16(mdl, mo + 12);
        if (submesh < 0 || submesh >= subCount)
            throw new ModelEditException($"mesh {mesh} has no submesh {submesh}");

        int ss = src.SubmeshStart + (subIdx + submesh) * 16;
        uint so = BitConverter.ToUInt32(mdl, ss), sc = BitConverter.ToUInt32(mdl, ss + 4);
        int tris = (int)(sc / 3);

        // Maximal runs of one label, in order.
        var runs = new List<(int Start, int Count, int Group)>();
        for (int t = 0; t < tris;)
        {
            int group = groupOf(t);
            int start = t;
            while (t < tris && groupOf(t) == group) t++;
            runs.Add((start, t - start, group));
        }

        if (runs.Count == 0) throw new ModelEditException("that submesh has no triangles");
        if (runs.Count > MaxRuns)
            throw new ModelEditException(
                $"those triangles are interleaved with the rest of the part across {runs.Count} runs, which "
              + "is too fragmented to split cleanly");

        // Already its own submesh: nothing to cut.
        if (runs.Count == 1)
            return (mdl, new Dictionary<int, List<int>> { [runs[0].Group] = [submesh] });

        var mask = BitConverter.ToUInt32(mdl, ss + 8);
        ushort boneStart = BitConverter.ToUInt16(mdl, ss + 12), boneCount = BitConverter.ToUInt16(mdl, ss + 14);

        var records = new byte[runs.Count][];
        var byGroup = new Dictionary<int, List<int>>();
        for (int i = 0; i < runs.Count; i++)
        {
            var r = new byte[16];
            W32(r, 0, so + (uint)(runs[i].Start * 3));
            W32(r, 4, (uint)(runs[i].Count * 3));
            W32(r, 8, mask);
            W16(r, 12, boneStart);
            W16(r, 14, boneCount);
            records[i] = r;
            if (!byGroup.TryGetValue(runs[i].Group, out var list)) byGroup[runs[i].Group] = list = [];
            list.Add(submesh + i);
        }

        // The first run overwrites the original record in place; the rest are inserted after it, so nothing
        // before this submesh moves at all.
        int added = runs.Count - 1;
        var extra = new byte[added * 16];
        for (int i = 1; i < runs.Count; i++) records[i].CopyTo(extra, (i - 1) * 16);

        var o = Splice(mdl, [(ss + 16, extra)]);
        records[0].CopyTo(o, ss);

        int delta = extra.Length;
        W16(o, src.Mh + 8, (ushort)(BitConverter.ToUInt16(o, src.Mh + 8) + added));   // model submesh count
        W16(o, mo + 12, (ushort)(subCount + added));                                  // this mesh's count

        // Every mesh whose submeshes sit after these now starts later. Compared on the ORIGINAL submeshIndex:
        // meshes need not be listed in submesh order.
        for (int m = 0; m < src.MeshCount; m++)
        {
            if (m == mesh) continue;
            int other = src.MeshStart + m * 36;   // the mesh table is before the insert, so it has not moved
            ushort otherIdx = BitConverter.ToUInt16(o, other + 10);
            if (otherIdx > subIdx + submesh) W16(o, other + 10, (ushort)(otherIdx + added));
        }

        Shift(o, src.LodStart, delta);
        return (o, byGroup);
    }

    /// <summary>
    /// Cut a submesh by label like <see cref="SplitSubmesh(byte[], int, int, Func{int, int})"/>, but REORDER
    /// its triangles first so each label is contiguous, for sets chosen by geometry. Safe because a triangle
    /// list's triples stand alone; order within a label is kept. Existing shapes are remapped; must run BEFORE
    /// <see cref="AddShape"/>, never after.
    /// </summary>
    /// <returns>The new file, and, per label, the submesh indices now holding its triangles.</returns>
    public static (byte[] Model, Dictionary<int, List<int>> ByGroup) RegroupSubmesh(
        byte[] mdl, int mesh, int submesh, Func<int, int> groupOf)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int mo = src.MeshStart + mesh * 36;
        ushort subIdx = BitConverter.ToUInt16(mdl, mo + 10), subCount = BitConverter.ToUInt16(mdl, mo + 12);
        if (submesh < 0 || submesh >= subCount)
            throw new ModelEditException($"mesh {mesh} has no submesh {submesh}");

        int ss = src.SubmeshStart + (subIdx + submesh) * 16;
        uint so = BitConverter.ToUInt32(mdl, ss), sc = BitConverter.ToUInt32(mdl, ss + 4);
        int tris = (int)(sc / 3);
        if (tris == 0) throw new ModelEditException("that submesh has no triangles");
        if ((long)src.Ib + (so + sc) * 2 > mdl.Length)
            throw new ModelEditException($"mesh {mesh}'s index range runs past the end of the file");

        // Labels in first-appearance order, original order kept within each, so a grouped submesh is a no-op.
        var labels = new int[tris];
        var seen = new List<int>();
        for (int t = 0; t < tris; t++)
        {
            labels[t] = groupOf(t);
            if (!seen.Contains(labels[t])) seen.Add(labels[t]);
        }

        var order = new int[tris];       // order[newTriangle] = old triangle
        int at = 0;
        foreach (var label in seen)
            for (int t = 0; t < tris; t++)
                if (labels[t] == label) order[at++] = t;

        bool moved = false;
        for (int t = 0; t < tris && !moved; t++) moved = order[t] != t;

        var regrouped = mdl;
        if (moved)
        {
            regrouped = (byte[])mdl.Clone();
            int at0 = src.Ib + (int)so * 2;
            for (int t = 0; t < tris; t++)
                Buffer.BlockCopy(mdl, at0 + order[t] * 6, regrouped, at0 + t * 6, 6);

            var newOf = new int[tris];   // the inverse, for the shape remap
            for (int t = 0; t < tris; t++) newOf[order[t]] = t;
            RemapShapeValues(regrouped, src, so, sc, newOf);
        }

        // The labels are contiguous now, so the plain split finds exactly one run per label.
        var byNewOrdinal = new int[tris];
        for (int t = 0; t < tris; t++) byNewOrdinal[t] = labels[order[t]];
        return SplitSubmesh(regrouped, mesh, submesh, t => byNewOrdinal[t]);
    }

    /// <summary>
    /// Follow every existing <c>ShapeValue</c> that names a slot inside <paramref name="so"/>..+<paramref
    /// name="sc"/> through a triangle permutation. A remap past a value's u16 window is refused.
    /// </summary>
    private static void RemapShapeValues(byte[] o, SecondSkinWriter.Source src, uint so, uint sc, int[] newOf)
    {
        ushort shapeCount = BitConverter.ToUInt16(o, src.Mh + 16);
        ushort shapeMeshCount = BitConverter.ToUInt16(o, src.Mh + 18);
        ushort shapeValueCount = BitConverter.ToUInt16(o, src.Mh + 20);
        if (shapeMeshCount == 0 || shapeValueCount == 0) return;

        int shapeMeshBlock = src.ShapeBlock + shapeCount * 16;
        int shapeValBlock = shapeMeshBlock + shapeMeshCount * 12;
        if (shapeValBlock + shapeValueCount * 4 > o.Length)
            throw new ModelEditException("this model's shape block runs past the end of the file");

        for (int m = 0; m < shapeMeshCount; m++)
        {
            uint windowBase = BitConverter.ToUInt32(o, shapeMeshBlock + m * 12);
            uint count = BitConverter.ToUInt32(o, shapeMeshBlock + m * 12 + 4);
            uint start = BitConverter.ToUInt32(o, shapeMeshBlock + m * 12 + 8);
            if (start + count > shapeValueCount) continue;

            for (uint v = start; v < start + count; v++)
            {
                int vo = shapeValBlock + (int)v * 4;
                long slot = windowBase + BitConverter.ToUInt16(o, vo);
                if (slot < so || slot >= so + sc) continue;

                long within = slot - so;
                long moved = (long)newOf[within / 3] * 3 + within % 3;
                long rebased = so + moved - windowBase;
                if (rebased < 0 || rebased > ushort.MaxValue)
                    throw new ModelEditException(
                        "this model's existing shape addresses this submesh from too far away to follow a "
                      + "reorder — it cannot be split by geometry");
                W16(o, vo, (ushort)rebased);
            }
        }
    }

    /// <summary>
    /// Cut <paramref name="parts"/> out of whatever submeshes they share with other geometry, so they can be
    /// tagged alone. A part that already IS a whole submesh passes through. Cuts through
    /// <see cref="RegroupSubmesh"/>, so it reorders and must run before any shape is written.
    /// </summary>
    /// <returns>The new model, and the submeshes now holding exactly those parts.</returns>
    public static (byte[] Model, List<(int Mesh, int Submesh)> Targets) IsolateParts(
        byte[] mdl, IReadOnlyList<ModelPart> parts)
    {
        var targets = new List<(int Mesh, int Submesh)>();
        if (parts.Count == 0) return (mdl, targets);

        // Which triangles are claimed within each submesh, and which submeshes are claimed entire.
        var byOrdinal = new Dictionary<(int Mesh, int Submesh), HashSet<int>>();
        var whole = new HashSet<(int Mesh, int Submesh)>();
        foreach (var part in parts)
        {
            var key = (part.Mesh, part.Submesh);
            if (part.Island < 0) { whole.Add(key); continue; }
            if (!byOrdinal.TryGetValue(key, out var set)) byOrdinal[key] = set = [];
            foreach (var t in part.Ordinals) set.Add(t);
        }
        // Claiming a submesh whole makes any island claim on it redundant.
        foreach (var key in whole) byOrdinal.Remove(key);

        var edited = mdl;
        foreach (var mesh in byOrdinal.Keys.Concat(whole).Select(k => k.Mesh).Distinct().OrderBy(m => m))
        {
            // Ascending, with a running shift: a split inserts records after the submesh it cut.
            int shift = 0;
            var here = byOrdinal.Keys.Concat(whole).Where(k => k.Mesh == mesh)
                .Select(k => k.Submesh).Distinct().OrderBy(s => s);

            foreach (var submesh in here)
            {
                if (whole.Contains((mesh, submesh))) { targets.Add((mesh, submesh + shift)); continue; }

                var claimed = byOrdinal[(mesh, submesh)];
                // Regroup rather than split: geometry-chosen parts are scattered through the author's layout.
                var (next, groups) = RegroupSubmesh(
                    edited, mesh, submesh + shift, t => claimed.Contains(t) ? 0 : -1);
                edited = next;

                if (groups.TryGetValue(0, out var mine))
                    targets.AddRange(mine.Select(s => (mesh, s)));
                shift += groups.Values.Sum(v => v.Count) - 1;
            }
        }
        return (edited, targets);
    }

    // ── shapes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Position element types <see cref="SecondSkinWriter.WriteXYZ"/> can encode a 3-vector into; it silently
    /// writes nothing for any other. Half2 has no z.
    /// </summary>
    internal static readonly byte[] PositionTypes = [2, 3, 14];   // Float3, Float4, Half4

    /// <summary>
    /// Add a shape key named <paramref name="shapeName"/>, moving the named vertices of the named LOD0 meshes.
    /// Each <c>ShapeValue</c> rewires one index-buffer slot to a spare vertex appended at the end of the mesh's
    /// own block, so existing vertex indices and shapes stay valid. LOD1/LOD2 are not shaped.
    /// </summary>
    /// <param name="moved">Per mesh index, the MESH-RELATIVE vertex indices to move and where to. Not
    /// <c>ModelPart.Triangles</c> indices, which are rebased across meshes.</param>
    /// <param name="normals">Optional replacement normals, addressed the same way; omitted, a spare keeps its
    /// source's normal.</param>
    public static byte[] AddShape(
        byte[] mdl, string shapeName,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>> moved,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>>? normals = null)
        => AddShape(mdl, shapeName, moved, out _, normals);

    /// <inheritdoc cref="AddShape(byte[], string, IReadOnlyDictionary{int, IReadOnlyDictionary{int, Vector3}}, IReadOnlyDictionary{int, IReadOnlyDictionary{int, Vector3}})"/>
    /// <param name="unaddressable">How many requested vertices were left in place because every triangle
    /// drawing them sits past index slot 65535 of every window.</param>
    public static byte[] AddShape(
        byte[] mdl, string shapeName,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>> moved,
        out int unaddressable,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>>? normals = null)
    {
        unaddressable = 0;
        if (string.IsNullOrEmpty(shapeName))
            throw new ModelEditException("a shape needs a name");

        var src = SecondSkinWriter.Parse(mdl);

        ushort shapeCount = BitConverter.ToUInt16(mdl, src.Mh + 16);
        ushort shapeMeshCount = BitConverter.ToUInt16(mdl, src.Mh + 18);
        ushort shapeValueCount = BitConverter.ToUInt16(mdl, src.Mh + 20);

        int shapeBlock = src.ShapeBlock;
        int shapeMeshBlock = shapeBlock + shapeCount * 16;
        int shapeValBlock = shapeMeshBlock + shapeMeshCount * 12;
        int shapeValEnd = shapeValBlock + shapeValueCount * 4;

        // The shape block's position proves the version-dependent bone-table walk was right: on a mis-walk the
        // submesh bone map's length prefix is arbitrary bytes and fails these checks.
        if (shapeValEnd < 0 || shapeValEnd + 4 > mdl.Length)
            throw new ModelEditException("this model's shape block runs past the end of the file");
        uint boneMapBytes = BitConverter.ToUInt32(mdl, shapeValEnd);
        if (boneMapBytes % 2 != 0 || (long)shapeValEnd + 4 + boneMapBytes > mdl.Length)
            throw new ModelEditException(
                "this model's tables do not add up — the shape block is not where the format says it is");

        if (DeclaresShape(mdl, shapeName))
            throw new ModelEditException($"this model already declares a shape named {shapeName}");

        // Neck-morph arrays are not accounted for here or in the parse; refuse rather than splice past them.
        if (mdl[src.Mh + 43] != 0)
            throw new ModelEditException("this model carries neck morph data, which cannot be edited here");

        var plans = BuildShapePlans(mdl, src, moved, normals, out unaddressable);
        if (plans.Count == 0)
            throw new ModelEditException(
                "none of those vertices are used by a triangle, so a shape over them would deform nothing");

        int totalValues = plans.Sum(p => p.ValueCount);
        int totalMeshes = plans.Sum(p => p.Windows.Count);
        if (shapeCount + 1 > ushort.MaxValue
         || shapeMeshCount + totalMeshes > ushort.MaxValue
         || shapeValueCount + (long)totalValues > ushort.MaxValue)
            throw new ModelEditException(
                $"this shape needs {totalValues} shape values, which overflows what the format counts");

        // ── the three table records ──────────────────────────────────────────
        // Appended at the ends of their arrays: shapeMeshStart and valueStart are GLOBAL indices, so no
        // existing record needs renumbering.
        var shapeRec = new byte[16];
        W32(shapeRec, 0, src.StrSize);                     // name offset — the string goes at the block's end
        W16(shapeRec, 4, shapeMeshCount);                  // LOD0 start
        W16(shapeRec, 6, (ushort)(shapeMeshCount + totalMeshes));   // LOD1/2 start: empty, past ours
        W16(shapeRec, 8, (ushort)(shapeMeshCount + totalMeshes));
        W16(shapeRec, 10, (ushort)totalMeshes);            // LOD0 count; LOD1/2 counts stay zero

        var shapeMeshRecs = new byte[totalMeshes * 12];
        var shapeValRecs = new byte[totalValues * 4];
        int valueBase = 0, rec = 0;
        foreach (var p in plans)
            foreach (var (windowBase, values) in p.Windows)
            {
                W32(shapeMeshRecs, rec * 12, windowBase);
                W32(shapeMeshRecs, rec * 12 + 4, (uint)values.Count);
                W32(shapeMeshRecs, rec * 12 + 8, (uint)(shapeValueCount + valueBase));
                for (int v = 0; v < values.Count; v++)
                {
                    W16(shapeValRecs, (valueBase + v) * 4, values[v].Item1);
                    W16(shapeValRecs, (valueBase + v) * 4 + 2, values[v].Item2);
                }
                valueBase += values.Count;
                rec++;
            }

        var text = Encoding.ASCII.GetBytes(shapeName);
        int nameLen = text.Length + 1;
        var strBytes = new byte[nameLen + (4 - nameLen % 4) % 4];
        text.CopyTo(strBytes, 0);

        // ── splice ───────────────────────────────────────────────────────────
        // With no shapes, inserts 2-4 share an offset and rely on Splice's STABLE sort. Do not reorder these.
        var inserts = new List<(int At, byte[] Bytes)>
        {
            (src.StrBlock + (int)src.StrSize, strBytes),
            (shapeMeshBlock, shapeRec),
            (shapeValBlock, shapeMeshRecs),
            (shapeValEnd, shapeValRecs),
        };
        foreach (var p in plans)
            foreach (var (at, bytes) in p.Inserts)
                inserts.Add((at, bytes));

        int dStr = strBytes.Length;
        int dMeta = dStr + shapeRec.Length + shapeMeshRecs.Length + shapeValRecs.Length;
        int dV = plans.Sum(p => p.Inserts.Sum(i => i.Bytes.Length));

        var o = Splice(mdl, inserts.ToArray());

        // ── the new file's coordinates ───────────────────────────────────────
        // The LOD and mesh tables sit between the string insert and the shape inserts: string growth only.
        int mhN = src.Mh + dStr, lodStartN = src.LodStart + dStr, meshStartN = src.MeshStart + dStr;

        W32(o, src.DeclEnd + 4, src.StrSize + (uint)dStr);                          // string block size
        W16(o, src.DeclEnd, (ushort)(BitConverter.ToUInt16(o, src.DeclEnd) + 1));    // string COUNT
        W16(o, mhN + 16, (ushort)(shapeCount + 1));
        W16(o, mhN + 18, (ushort)(shapeMeshCount + totalMeshes));
        W16(o, mhN + 20, (ushort)(shapeValueCount + totalValues));

        foreach (var p in plans)
            W16(o, meshStartN + p.Mesh * 36, (ushort)(p.VertexCount + p.SrcVerts.Length));

        // Every LOD0 mesh's stream offset moves past the inserts at OR below it: Splice puts inserted bytes
        // before the byte originally at that offset.
        var vInserts = plans.SelectMany(p => p.Inserts).ToArray();
        int lod0End = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < lod0End; m++)
        {
            int mo = src.MeshStart + m * 36;
            for (int j = 0; j < 3; j++)
            {
                if (mdl[mo + 32 + j] == 0) continue;
                long at = src.Vb + BitConverter.ToUInt32(mdl, mo + 20 + j * 4);
                int shift = vInserts.Where(i => i.At <= at).Sum(i => i.Bytes.Length);
                if (shift != 0)
                    W32(o, meshStartN + m * 36 + 20 + j * 4,
                        BitConverter.ToUInt32(o, meshStartN + m * 36 + 20 + j * 4) + (uint)shift);
            }
        }

        W32(o, 40, BitConverter.ToUInt32(o, 40) + (uint)dV);                    // VertexBufferSize[0]
        W32(o, lodStartN + 44, BitConverter.ToUInt32(o, lodStartN + 44) + (uint)dV);

        // Two phases: the metadata grew before everything, the vertices only inside buffer 0. RuntimeSize (in
        // Shift) is the distance to the START of vertex data, so the second phase leaves it.
        Shift(o, lodStartN, dMeta);
        ShiftOffsets(o, lodStartN, dV, (long)src.Vb + dMeta);
        return o;
    }

    /// <summary>
    /// How many index slots one <c>ShapeMesh</c> can address: <c>BaseIndicesIndex</c> is a u16 added to the
    /// record's <c>MeshIndexOffset</c>.
    /// </summary>
    private const int SlotWindow = ushort.MaxValue + 1;

    /// <summary>One mesh's share of a shape: the spare vertices to append and the slots that select them.</summary>
    private sealed class ShapePlan
    {
        public required int Mesh { get; init; }
        public required ushort VertexCount { get; init; }
        public required int[] SrcVerts { get; init; }
        public required List<(int At, byte[] Bytes)> Inserts { get; init; }

        /// <summary>
        /// The slots to rewire, split into <see cref="SlotWindow"/>-sized runs of the index buffer: one
        /// <c>ShapeMesh</c> record each, at absolute base <c>Base</c>. Meshes over 65535 indices need several.
        /// </summary>
        public required List<(uint Base, List<(ushort Slot, ushort Replace)> Values)> Windows { get; init; }

        public int ValueCount => Windows.Sum(w => w.Values.Count);
    }

    private static List<ShapePlan> BuildShapePlans(
        byte[] mdl, SecondSkinWriter.Source src,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>> moved,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>>? normals,
        out int unaddressable)
    {
        unaddressable = 0;
        int lod0End = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        var plans = new List<ShapePlan>();

        foreach (var mesh in moved.Keys.OrderBy(k => k))
        {
            var wanted = moved[mesh];
            if (wanted.Count == 0) continue;
            if (mesh < src.Lod0MeshIndex || mesh >= lod0End)
                throw new ModelEditException(
                    $"mesh {mesh} is not part of LOD0, whose meshes are the only ones this can shape");

            int mo = src.MeshStart + mesh * 36;
            ushort vc = BitConverter.ToUInt16(mdl, mo);
            uint ic = BitConverter.ToUInt32(mdl, mo + 4), startIndex = BitConverter.ToUInt32(mdl, mo + 16);

            var asked = wanted.Keys.OrderBy(k => k).ToArray();
            foreach (var v in asked)
                if (v < 0 || v >= vc)
                    throw new ModelEditException($"mesh {mesh} has no vertex {v} — it has {vc}");
            if (vc + asked.Length > ushort.MaxValue)
                throw new ModelEditException(
                    $"mesh {mesh} would need {vc + asked.Length} vertices, past the {ushort.MaxValue} a "
                  + "model can count — this is the format reason a hair meant for hats is kept under 30k polys");

            if ((long)src.Ib + (startIndex + ic) * 2 > mdl.Length)
                throw new ModelEditException($"mesh {mesh}'s index range runs past the end of the file");

            var srcVerts = asked;

            // One value per SLOT, not per vertex: every slot naming a vertex must be rewired or the shape tears.
            var newIndexOf = new Dictionary<int, ushort>(srcVerts.Length);
            for (int r = 0; r < srcVerts.Length; r++) newIndexOf[srcVerts[r]] = (ushort)(vc + r);

            // Split into windows: a mesh longer than a u16 can count gets a record based 65536 slots further in.
            var windows = new List<(uint Base, List<(ushort, ushort)> Values)>();
            for (uint w = 0; w * SlotWindow < ic; w++)
            {
                uint from = w * (uint)SlotWindow, to = Math.Min(ic, from + SlotWindow);
                List<(ushort, ushort)>? here = null;
                for (uint s = from; s < to; s++)
                {
                    int idx = BitConverter.ToUInt16(mdl, src.Ib + (int)(startIndex + s) * 2);
                    if (newIndexOf.TryGetValue(idx, out var rep))
                        (here ??= []).Add(((ushort)(s - from), rep));
                }
                if (here != null) windows.Add((startIndex + from, here));
            }
            // Nothing draws these vertices, so a shape over them would grow the file and deform nothing.
            if (windows.Count == 0) continue;

            plans.Add(new ShapePlan
            {
                Mesh = mesh,
                VertexCount = vc,
                SrcVerts = srcVerts,
                Windows = windows,
                Inserts = SpareVertices(mdl, src, mesh, mo, vc, srcVerts, wanted,
                                        normals != null && normals.TryGetValue(mesh, out var n) ? n : null),
            });
        }
        return plans;
    }

    /// <summary>
    /// The bytes of one mesh's spare vertices, per stream, positioned at the end of that stream's block. EVERY
    /// stream is grown, or later meshes read their other streams one stride out of register.
    /// </summary>
    private static List<(int At, byte[] Bytes)> SpareVertices(
        byte[] mdl, SecondSkinWriter.Source src, int mesh, int mo, ushort vc,
        int[] srcVerts, IReadOnlyDictionary<int, Vector3> positions, IReadOnlyDictionary<int, Vector3>? normals)
    {
        var decl = src.Decls[mesh];
        var pos = Array.Find(decl, e => e.Usage == SecondSkinWriter.UsePosition);
        if (pos == default && !Array.Exists(decl, e => e.Usage == SecondSkinWriter.UsePosition))
            throw new ModelEditException($"mesh {mesh} declares no position element");
        if (Array.IndexOf(PositionTypes, pos.Type) < 0)
            throw new ModelEditException(
                $"mesh {mesh} stores its positions as vertex type {pos.Type}, which cannot be written here");

        var nrm = Array.Find(decl, e => e.Usage == SecondSkinWriter.UseNormal);
        bool haveNormal = normals != null && Array.Exists(decl, e => e.Usage == SecondSkinWriter.UseNormal);

        var inserts = new List<(int At, byte[] Bytes)>();
        for (int j = 0; j < 3; j++)
        {
            byte stride = mdl[mo + 32 + j];
            if (stride == 0) continue;
            uint vbo = BitConverter.ToUInt32(mdl, mo + 20 + j * 4);
            int blockStart = src.Vb + (int)vbo;
            if (blockStart < 0 || blockStart + (vc + 1) * stride > mdl.Length + stride)
                throw new ModelEditException($"mesh {mesh}'s stream {j} runs past the end of the file");

            var bytes = new byte[srcVerts.Length * stride];
            for (int r = 0; r < srcVerts.Length; r++)
            {
                // Verbatim, then overwrite: the spare keeps its source's weights, UV, tangents and colour.
                Array.Copy(mdl, blockStart + srcVerts[r] * stride, bytes, r * stride, stride);
                if (pos.Stream == j)
                {
                    var p = positions[srcVerts[r]];
                    SecondSkinWriter.WriteXYZ(bytes, r * stride + pos.Offset, pos.Type, p.X, p.Y, p.Z);
                }
                if (haveNormal && nrm.Stream == j && normals!.TryGetValue(srcVerts[r], out var n))
                    SecondSkinWriter.WriteNormal(bytes, r * stride + nrm.Offset, nrm.Type, n.X, n.Y, n.Z);
            }
            inserts.Add((blockStart + vc * stride, bytes));
        }
        return inserts;
    }

    /// <summary>
    /// Whether the model declares a shape by this name. Reads the <c>Shape</c> records directly:
    /// <see cref="SecondSkinWriter.Source.Shapes"/> drops shapes without LOD0 entries.
    /// </summary>
    public static bool DeclaresShape(byte[] mdl, string shapeName)
    {
        var src = SecondSkinWriter.Parse(mdl);
        ushort shapeCount = BitConverter.ToUInt16(mdl, src.Mh + 16);
        for (int si = 0; si < shapeCount; si++)
        {
            int at = src.ShapeBlock + si * 16;
            if (at + 16 > mdl.Length) break;
            if (string.Equals(StringAt(mdl, src, BitConverter.ToUInt32(mdl, at)), shapeName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>A string from the model's own block, by the block-relative offset the tables store.</summary>
    private static string StringAt(byte[] mdl, SecondSkinWriter.Source src, uint rel)
    {
        if (rel >= src.StrSize) return "";
        int o = src.StrBlock + (int)rel, e = o;
        while (e < mdl.Length && mdl[e] != 0) e++;
        return Encoding.ASCII.GetString(mdl, o, e - o);
    }

    // ── shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Insert byte runs at the given ORIGINAL offsets, lowest first. The offsets all address the input, so a
    /// caller never has to think about how one insert moves another.
    /// </summary>
    internal static byte[] Splice(byte[] src, (int At, byte[] Bytes)[] inserts)
    {
        var ordered = inserts.OrderBy(i => i.At).ToArray();
        var o = new byte[src.Length + ordered.Sum(i => i.Bytes.Length)];

        int read = 0, write = 0;
        foreach (var (at, bytes) in ordered)
        {
            Array.Copy(src, read, o, write, at - read);
            write += at - read;
            read = at;
            bytes.CopyTo(o, write);
            write += bytes.Length;
        }
        Array.Copy(src, read, o, write, src.Length - read);
        return o;
    }

    /// <summary>
    /// Move every absolute file offset on by <paramref name="delta"/>: the header's vertex/index offset triples,
    /// each LOD's vertex/index data offsets, and <c>RuntimeSize</c>. Everything else is relative; the LOD
    /// edge-geometry field is not a file offset (Penumbra's <c>MdlFile.Write</c> leaves it).
    /// </summary>
    /// <param name="lodStart">The first LOD struct's position IN THE OUTPUT.</param>
    private static void Shift(byte[] o, int lodStart, int delta)
    {
        W32(o, 8, BitConverter.ToUInt32(o, 8) + (uint)delta);       // RuntimeSize
        ShiftOffsets(o, lodStart, delta, -1);
    }

    /// <summary>
    /// The eight offsets alone, moving only those past <paramref name="past"/>, for inserts inside LOD0's
    /// vertex buffer. <c>RuntimeSize</c> is deliberately not here.
    /// </summary>
    /// <param name="past">Only offsets strictly greater than this move; -1 moves all of them.</param>
    internal static void ShiftOffsets(byte[] o, int lodStart, int delta, long past)
    {
        for (int i = 0; i < 3; i++)
        {
            Bump(o, 16 + i * 4, delta, past);                       // VertexOffset[i]
            Bump(o, 28 + i * 4, delta, past);                       // IndexOffset[i]
            Bump(o, lodStart + i * 60 + 52, delta, past);           // LOD VertexDataOffset
            Bump(o, lodStart + i * 60 + 56, delta, past);           // LOD IndexDataOffset
        }
    }

    /// <summary>Add to a u32, leaving a zero alone — an unused LOD or stream reads 0 and must stay 0.</summary>
    private static void Bump(byte[] o, int at, int delta, long past = -1)
    {
        if (at + 4 > o.Length) return;
        var v = BitConverter.ToUInt32(o, at);
        if (v != 0 && v > past) W32(o, at, v + (uint)delta);
    }

    private static void W16(byte[] b, int o, ushort v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
    private static void W32(byte[] b, int o, uint v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
}
