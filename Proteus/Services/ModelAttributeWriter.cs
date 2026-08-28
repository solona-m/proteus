using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Proteus.Services;

/// <summary>
/// Edits a .mdl's STRUCTURE: splits a submesh, and adds an attribute to the table so the game can switch
/// that submesh off. Together with an IMC group over the same attribute, this is what turns geometry an
/// author welded on permanently into an ordinary Penumbra checkbox.
/// <para/>
/// Both edits grow the file, and everything hard about them is the consequence. The model format puts its
/// tables one after another with no offsets between them — each is found by stepping over the last — so an
/// insert relocates everything after it, and the handful of genuinely ABSOLUTE offsets have to be moved to
/// match. There are exactly eight of those and <see cref="Shift"/> owns all of them; get one wrong and the
/// model either fails to load or renders at the wrong LOD, which is why the shift is one function and not
/// spread across the two edits.
/// <para/>
/// Nothing here reorders an index entry, and that is deliberate rather than incidental. A shape key
/// (<c>ShapeValue.BaseIndicesIndex</c>) addresses a POSITION in the index buffer, so permuting the buffer
/// silently breaks every body slider the garment supports. <see cref="SplitSubmesh"/> therefore cuts a
/// submesh at the run boundaries it already has instead of gathering triangles together.
/// </summary>
public static class ModelAttributeWriter
{
    /// <summary>
    /// Attribute masks are 32 bits wide, so a model already carrying 32 attributes has nowhere to put
    /// another. Well above the ten an IMC entry can actually drive — this is the format's limit, not the
    /// budget the user sees.
    /// </summary>
    public const int MaxAttributes = 32;

    /// <summary>
    /// How many pieces one submesh may be cut into.
    /// <para/>
    /// A split makes one record per CONTIGUOUS RUN of triangles, and an island that interleaves with its
    /// neighbours triangle by triangle would want one record each. That is legal but absurd, and a sign the
    /// island split found something that is not really a separate object. Refused rather than written.
    /// <para/>
    /// Generous, because a record costs sixteen bytes and models routinely carry dozens of submeshes: the
    /// bound is here to catch geometry that is pathologically interleaved, not to second-guess a garment
    /// whose author happened to export its straps out of order.
    /// </summary>
    public const int MaxRuns = 256;

    public sealed class ModelEditException(string message) : InvalidOperationException(message);

    // ── attributes ──────────────────────────────────────────────────────────

    /// <summary>
    /// Add <paramref name="attributeName"/> to the model's attribute table and tag every named submesh with
    /// it. The submeshes then draw only while the attribute is enabled, which an IMC entry decides.
    /// <para/>
    /// The name goes at the END of the table and the bit is its position there, because a submesh's mask
    /// indexes the table positionally. Which IMC bit ends up driving it is a different question with a
    /// different answer — the trailing letter of the NAME, see <c>SecondSkinService.PartAttributeBit</c> —
    /// so the two never have to agree and the caller picks the letter.
    /// </summary>
    /// <param name="targets">(mesh index, submesh index within that mesh) pairs.</param>
    public static byte[] AddAttribute(
        byte[] mdl, string attributeName, IReadOnlyCollection<(int Mesh, int Submesh)> targets)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int attrCount = src.AttrNames.Length;
        if (attrCount >= MaxAttributes)
            throw new ModelEditException(
                $"this model already declares {attrCount} attributes, which is all a submesh mask can hold");
        if (src.AttrNames.Contains(attributeName, StringComparer.Ordinal))
            throw new ModelEditException($"this model already declares an attribute named {attributeName}");

        // Padded to four bytes so every table after the string block keeps its alignment. The tables are
        // read by byte offset and would parse either way, but a u32 array landing on an odd address is not
        // something to hand the game to find out about.
        var text = Encoding.ASCII.GetBytes(attributeName);
        int nameLen = text.Length + 1;
        int pad = (4 - nameLen % 4) % 4;
        var strBytes = new byte[nameLen + pad];
        text.CopyTo(strBytes, 0);

        uint nameOffset = src.StrSize;                       // relative to the string block, as attrs are
        int insertStr = src.StrBlock + (int)src.StrSize;     // end of the string block
        int insertOff = src.AttrStart + attrCount * 4;       // end of the attribute offset table

        var offBytes = new byte[4];
        BitConverter.TryWriteBytes(offBytes, nameOffset);

        var o = Splice(mdl, [(insertStr, strBytes), (insertOff, offBytes)]);
        int dStr = strBytes.Length, delta = dStr + 4;

        W32(o, src.DeclEnd + 4, src.StrSize + (uint)dStr);          // string block size
        W16(o, src.Mh + dStr + 6, (ushort)(attrCount + 1));         // attribute count
        Shift(o, src.LodStart + dStr, delta);

        // The NEW file's coordinates, and the two tables did NOT move by the same amount. The mesh table sits
        // BETWEEN the two inserts — the attribute offsets go in at its far end — so it follows only the
        // string block's growth, while the submeshes are behind both and follow the full delta.
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

    // ── splitting ───────────────────────────────────────────────────────────

    /// <summary>
    /// Cut one submesh into several, so part of it can be tagged on its own.
    /// <para/>
    /// The cut follows the CONTIGUOUS RUNS of <paramref name="ordinals"/> within the submesh's own index
    /// range: nothing is moved, only described differently. Every new record inherits the original's
    /// attribute mask and its bone window, so a split on its own changes precisely nothing about how the
    /// model draws — which is the property that makes it safe to do before knowing whether the user will
    /// keep the toggle.
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
    /// The general split: <paramref name="groupOf"/> labels each triangle ordinal, and the submesh is cut so
    /// that no record mixes two labels.
    /// <para/>
    /// More than two labels is not hypothetical — two switches can each claim a different island of the same
    /// submesh, and cutting for one at a time would have the second split re-cut records the first had just
    /// made. Doing it in one pass keeps the record count to the runs that are genuinely there.
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

        // Already its own submesh — nothing to cut, and inserting a zero-length record would be worse than
        // doing nothing.
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

        // Every mesh whose submeshes sit after these now starts later in the table. Compared on the ORIGINAL
        // submeshIndex, not on the mesh number: a model is not obliged to list its meshes in submesh order,
        // and one that does not would otherwise have a mesh renumbered onto another's records.
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

    // ── shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Insert byte runs at the given ORIGINAL offsets, lowest first. The offsets all address the input, so a
    /// caller never has to think about how one insert moves another.
    /// </summary>
    private static byte[] Splice(byte[] src, (int At, byte[] Bytes)[] inserts)
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
    /// Move every absolute file offset on by <paramref name="delta"/>. These eight fields are the entire
    /// list, and the reason it is short: the mesh structs' own vertex offsets are relative to the vertex
    /// BUFFER, the attribute and material offsets are relative to the string block, and the bone tables are
    /// relative to themselves. Only the file header's two offset triples and each LOD's vertex/index data
    /// pointers count from the start of the file.
    /// <para/>
    /// The LOD struct carries an edge-geometry offset too, which is deliberately NOT touched: Penumbra's own
    /// model writer rebases vertex and index and nothing else (<c>MdlFile.Write</c>), so that field is not a
    /// file offset.
    /// <para/>
    /// <c>RuntimeSize</c> comes along because it is defined as the distance from the end of the header to
    /// the vertex data, so growing the metadata grows it one for one.
    /// </summary>
    /// <param name="lodStart">The first LOD struct's position IN THE OUTPUT.</param>
    private static void Shift(byte[] o, int lodStart, int delta)
    {
        W32(o, 8, BitConverter.ToUInt32(o, 8) + (uint)delta);       // RuntimeSize
        for (int i = 0; i < 3; i++)
        {
            Bump(o, 16 + i * 4, delta);                             // VertexOffset[i]
            Bump(o, 28 + i * 4, delta);                             // IndexOffset[i]
            Bump(o, lodStart + i * 60 + 52, delta);                 // LOD VertexDataOffset
            Bump(o, lodStart + i * 60 + 56, delta);                 // LOD IndexDataOffset
        }
    }

    /// <summary>Add to a u32, leaving a zero alone — an unused LOD or stream reads 0 and must stay 0.</summary>
    private static void Bump(byte[] o, int at, int delta)
    {
        if (at + 4 > o.Length) return;
        var v = BitConverter.ToUInt32(o, at);
        if (v != 0) W32(o, at, v + (uint)delta);
    }

    private static void W16(byte[] b, int o, ushort v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
    private static void W32(byte[] b, int o, uint v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
}
