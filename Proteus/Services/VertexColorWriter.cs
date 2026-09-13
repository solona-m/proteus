using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// The wind channel: the RED of a mesh's SECOND vertex colour (element usage Color, usage index 1), which the
/// game reads as how much the wind moves that vertex. Black is still, full red is the most sway; the first
/// colour should be white.
/// <para/>
/// Most modded meshes do not carry the second colour at all — Penumbra's model import writes only the first,
/// and TexTools writes the second only when it is not all black — so painting wind usually means ADDING it.
/// That is a file-length-changing edit made the way <see cref="ModelAttributeWriter.AddShape"/> makes one: the
/// LOD0 vertex records grow in place through <see cref="ModelAttributeWriter.Splice"/>, and every offset that
/// counts past them follows. LOD1 and LOD2 are left alone, so a model painted here sways up close and stops at
/// distance rather than breaking.
/// </summary>
internal static class VertexColorWriter
{
    private const byte UseColor = SecondSkinWriter.UseColor;
    private const byte NByte4 = 8;

    /// <summary>Bytes per element slot in a declaration block, and slots per block.</summary>
    private const int ElementSize = 8, ElementSlots = 17;

    /// <summary>The second colour's neutral value: no wind.</summary>
    private static readonly byte[] Still = [0, 0, 0, 255];

    /// <summary>The first colour's neutral value, which the wind shader expects.</summary>
    private static readonly byte[] White = [255, 255, 255, 255];

    /// <param name="MeshesAdded">LOD0 meshes the second colour was added to.</param>
    /// <param name="MeshesRefused">Meshes that could not take it — every declaration slot used, or a vertex record
    /// that would pass 255 bytes. They keep no wind.</param>
    /// <param name="AddedFirstColor">A mesh had no first colour either; it was added as white.</param>
    public readonly record struct Report(int MeshesAdded, int MeshesRefused, bool AddedFirstColor);

    /// <summary>Every LOD0 mesh that draws anything already has the second colour.</summary>
    public static bool HasWindChannel(byte[] mdl)
    {
        var src = SecondSkinWriter.Parse(mdl);
        foreach (int m in Lod0Meshes(src))
            if (!src.Decls[m].Any(IsSecondColor)) return false;
        return true;
    }

    /// <summary>
    /// Some mesh's first colour is not white. The wind guide asks for white, and a first colour the author tinted
    /// is theirs to keep — so this is only ever reported, never changed.
    /// </summary>
    public static bool FirstColorNotWhite(byte[] mdl) => FirstColorNotWhite(SecondSkinWriter.Parse(mdl));

    /// <inheritdoc cref="FirstColorNotWhite(byte[])"/>
    internal static bool FirstColorNotWhite(SecondSkinWriter.Source src)
    {
        var s = src.S;
        foreach (int m in Lod0Meshes(src))
        {
            var el = src.Decls[m].FirstOrDefault(e => e.Usage == UseColor && e.UsageIndex == 0);
            if (el.Usage != UseColor || el.Type is not (NByte4 or 5)) continue;
            int mo = src.MeshStart + m * 36;
            ushort vc = BitConverter.ToUInt16(s, mo);
            uint vbo = BitConverter.ToUInt32(s, mo + 20 + el.Stream * 4);
            byte stride = s[mo + 32 + el.Stream];
            for (int k = 0; k < vc; k++)
            {
                int at = (int)(src.Vb + vbo) + k * stride + el.Offset;
                if (at + 3 > s.Length) break;
                if (s[at] != 255 || s[at + 1] != 255 || s[at + 2] != 255) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Add the second colour, still (0,0,0,255), to every LOD0 mesh that lacks it — and the first colour, white,
    /// to any that lacks that too. Returns the input unchanged when nothing needed adding.
    /// </summary>
    public static byte[] EnsureSecondColor(byte[] mdl, out Report report)
    {
        var src = SecondSkinWriter.Parse(mdl);
        var s = src.S;
        int added = 0, refused = 0;
        bool addedFirst = false;

        var inserts = new List<(int At, byte[] Bytes)>();
        var declEdits = new List<(int Mesh, List<SecondSkinWriter.VElem> Elements)>();
        var strideEdits = new List<(int StrideAt, byte Stride)>();
        uint vbSize = BitConverter.ToUInt32(s, 40);

        foreach (int m in Lod0Meshes(src))
        {
            var decl = src.Decls[m];
            if (decl.Any(IsSecondColor)) continue;

            int mo = src.MeshStart + m * 36;
            ushort vc = BitConverter.ToUInt16(s, mo);
            bool hasFirst = decl.Any(e => e.Usage == UseColor && e.UsageIndex == 0);

            // The last stream the mesh uses, so the new element lands after every existing one and the block
            // stays in stream-then-offset order.
            int stream = -1;
            for (int j = 2; j >= 0; j--)
                if (s[mo + 32 + j] != 0) { stream = j; break; }
            if (stream < 0) continue;

            byte stride = s[mo + 32 + stream];
            int grow = hasFirst ? 4 : 8;
            uint vbo = BitConverter.ToUInt32(s, mo + 20 + stream * 4);
            long blockEnd = (long)vbo + vc * (long)stride;
            if (decl.Length + (hasFirst ? 1 : 2) > ElementSlots || stride + grow > 255 || blockEnd > vbSize)
            {
                refused++;
                continue;
            }

            var elements = decl.ToList();
            var record = new byte[grow];
            if (!hasFirst)
            {
                elements.Add(new SecondSkinWriter.VElem((byte)stream, stride, NByte4, UseColor, 0));
                White.CopyTo(record, 0);
                addedFirst = true;
            }
            elements.Add(new SecondSkinWriter.VElem((byte)stream, (byte)(stride + grow - 4), NByte4, UseColor, 1));
            Still.CopyTo(record, grow - 4);

            // After each vertex's record: the new bytes are that vertex's, not the next one's.
            int blockStart = (int)(src.Vb + vbo);
            for (int k = 1; k <= vc; k++) inserts.Add((blockStart + k * stride, record));

            declEdits.Add((m, elements));
            strideEdits.Add((mo + 32 + stream, (byte)(stride + grow)));
            added++;
        }

        report = new Report(added, refused, addedFirst);
        if (inserts.Count == 0) return mdl;

        int dV = inserts.Sum(i => i.Bytes.Length);
        var o = ModelAttributeWriter.Splice(mdl, inserts.ToArray());

        // Declarations and mesh structs sit ahead of the vertex data, so they did not move.
        foreach (var (m, elements) in declEdits) WriteDecl(o, m, elements);
        foreach (var (at, stride) in strideEdits) o[at] = stride;

        // Every LOD0 stream block moves by the bytes inserted at or before its start — at, because Splice puts an
        // insert BEFORE the byte originally at that offset, so the block that begins where the last vertex of the
        // previous block ended moves with it, while the block that grew does not.
        // Sorted once, with running totals, so each block's shift is a binary search rather than a scan of every
        // insert — a dense garment is hundreds of thousands of them. Sizes vary by mesh (4 or 8 bytes).
        var ordered = inserts.OrderBy(i => i.At).ToArray();
        var positions = ordered.Select(i => i.At).ToArray();
        var prefix = new long[ordered.Length + 1];
        for (int i = 0; i < ordered.Length; i++) prefix[i + 1] = prefix[i] + ordered[i].Bytes.Length;

        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            for (int j = 0; j < 3; j++)
            {
                if (s[mo + 32 + j] == 0) continue;
                long at = src.Vb + BitConverter.ToUInt32(s, mo + 20 + j * 4);
                int count = UpperBound(positions, at);
                long shift = prefix[count];
                if (shift != 0)
                    BitConverter.TryWriteBytes(o.AsSpan(mo + 20 + j * 4),
                                               BitConverter.ToUInt32(s, mo + 20 + j * 4) + (uint)shift);
            }
        }

        BitConverter.TryWriteBytes(o.AsSpan(40), BitConverter.ToUInt32(s, 40) + (uint)dV);                      // VertexBufferSize[0]
        BitConverter.TryWriteBytes(o.AsSpan(src.LodStart + 44), BitConverter.ToUInt32(s, src.LodStart + 44) + (uint)dV);
        ModelAttributeWriter.ShiftOffsets(o, src.LodStart, dV, src.Vb);
        return o;
    }

    /// <summary>
    /// Write each vertex's wind into the RED of its second colour, in place, leaving green, blue and alpha as
    /// they are. <paramref name="windAt"/> is indexed like <see cref="ModelParts.Positions"/>, whose runs
    /// <paramref name="spans"/> describes. A shape key's spare vertex takes the value of the vertex it stands in
    /// for, so enabling the shape does not still a part that was painted.
    /// </summary>
    /// <returns>How many vertices were written.</returns>
    public static int WriteWind(byte[] o, IReadOnlyList<MeshSpan> spans, Func<int, float> windAt)
    {
        var src = SecondSkinWriter.Parse(o);
        int written = 0;
        var spanOf = new Dictionary<int, MeshSpan>();
        foreach (var span in spans) spanOf[span.Mesh] = span;

        foreach (var span in spans)
        {
            if (!TrySecondColor(o, src, span.Mesh, out var ce, out uint vbo, out byte stride)) continue;
            for (int k = 0; k < span.Count; k++)
                if (WriteRed(o, (int)(src.Vb + vbo) + k * stride + ce.Offset, ce.Type, windAt(span.BaseVertex + k)))
                    written++;
        }

        // Spares after the main pass, reading the value just written for their base vertex.
        int lodEnd = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        foreach (var (_, entries) in src.Shapes)
        foreach (var entry in entries)
        {
            int owner = -1;
            for (int m = src.Lod0MeshIndex; m < lodEnd && owner < 0; m++)
            {
                int mo = src.MeshStart + m * 36;
                uint start = BitConverter.ToUInt32(o, mo + 16), count = BitConverter.ToUInt32(o, mo + 4);
                if (entry.MeshIndexOffset >= start && entry.MeshIndexOffset < start + count) owner = m;
            }
            if (owner < 0 || !spanOf.TryGetValue(owner, out var span)) continue;
            if (!TrySecondColor(o, src, owner, out var ce, out uint vbo, out byte stride)) continue;

            foreach (var (baseIdx, replace) in entry.Values)
            {
                long slot = entry.MeshIndexOffset + baseIdx;
                if (src.Ib + slot * 2 + 2 > o.Length) continue;
                int baseVertex = BitConverter.ToUInt16(o, src.Ib + (int)slot * 2);
                if (baseVertex >= span.Count || replace >= span.Count) continue;
                WriteRed(o, (int)(src.Vb + vbo) + replace * stride + ce.Offset, ce.Type, windAt(span.BaseVertex + baseVertex));
            }
        }
        return written;
    }

    /// <summary>
    /// The red of a vertex's second colour, 0..1, for the vertex at <paramref name="k"/> of mesh
    /// <paramref name="mesh"/>, or null when the mesh has no second colour.
    /// </summary>
    internal static float? ReadWind(byte[] s, SecondSkinWriter.Source src, int mesh, int k)
    {
        if (!TrySecondColor(s, src, mesh, out var ce, out uint vbo, out byte stride)) return null;
        int at = (int)(src.Vb + vbo) + k * stride + ce.Offset;
        if (at < 0 || at + 16 > s.Length) return null;
        Span<float> tmp = stackalloc float[4];
        SecondSkinWriter.ReadTyped(s, at, ce.Type, tmp);
        return ce.Type == 5 ? tmp[0] / 255f : tmp[0];
    }

    /// <summary>
    /// The wind of the first <paramref name="count"/> vertices of mesh <paramref name="mesh"/> into
    /// <paramref name="dest"/> from <paramref name="destStart"/>. Leaves them untouched when the mesh has no second
    /// colour. One element lookup for the whole run, not one per vertex.
    /// </summary>
    internal static void ReadWind(byte[] s, SecondSkinWriter.Source src, int mesh, int count, float[] dest, int destStart)
    {
        if (!TrySecondColor(s, src, mesh, out var ce, out uint vbo, out byte stride)) return;
        Span<float> tmp = stackalloc float[4];
        for (int k = 0; k < count; k++)
        {
            int at = (int)(src.Vb + vbo) + k * stride + ce.Offset;
            if (at < 0 || at + 16 > s.Length) break;
            SecondSkinWriter.ReadTyped(s, at, ce.Type, tmp);
            dest[destStart + k] = ce.Type == 5 ? tmp[0] / 255f : tmp[0];
        }
    }

    internal static bool IsSecondColor(SecondSkinWriter.VElem e) => e.Usage == UseColor && e.UsageIndex == 1;

    private static bool TrySecondColor(byte[] s, SecondSkinWriter.Source src, int mesh,
                                       out SecondSkinWriter.VElem element, out uint vbo, out byte stride)
    {
        element = default;
        vbo = 0;
        stride = 0;
        if (mesh >= src.Decls.Length) return false;
        var found = src.Decls[mesh].Where(IsSecondColor).ToArray();
        if (found.Length == 0 || found[0].Stream > 2) return false;
        element = found[0];
        int mo = src.MeshStart + mesh * 36;
        vbo = BitConverter.ToUInt32(s, mo + 20 + element.Stream * 4);
        stride = s[mo + 32 + element.Stream];
        return stride != 0;
    }

    private static bool WriteRed(byte[] o, int at, byte type, float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        switch (type)
        {
            case NByte4:
            case 5:
                if (at < 0 || at + 1 > o.Length) return false;
                o[at] = (byte)MathF.Round(value * 255f);
                return true;
            case 2:
            case 3:
                if (at < 0 || at + 4 > o.Length) return false;
                BitConverter.TryWriteBytes(o.AsSpan(at), value);
                return true;
            case 14:
                if (at < 0 || at + 2 > o.Length) return false;
                BitConverter.TryWriteBytes(o.AsSpan(at), (Half)value);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Rewrite one mesh's declaration block, elements ordered by stream then offset.</summary>
    private static void WriteDecl(byte[] o, int mesh, List<SecondSkinWriter.VElem> elements)
    {
        int block = 0x44 + mesh * ElementSlots * ElementSize;
        var ordered = elements.OrderBy(e => e.Stream).ThenBy(e => e.Offset).ToList();
        for (int e = 0; e < ElementSlots; e++)
        {
            int at = block + e * ElementSize;
            if (e < ordered.Count)
            {
                var el = ordered[e];
                o[at] = el.Stream; o[at + 1] = el.Offset; o[at + 2] = el.Type; o[at + 3] = el.Usage; o[at + 4] = el.UsageIndex;
                o[at + 5] = o[at + 6] = o[at + 7] = 0;
            }
            else if (e == ordered.Count)
            {
                o[at] = 0xFF;
            }
        }
    }

    /// <summary>LOD0 meshes that draw something.</summary>
    private static IEnumerable<int> Lod0Meshes(SecondSkinWriter.Source src)
    {
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > src.S.Length) yield break;
            if (BitConverter.ToUInt16(src.S, mo) > 0 && m < src.Decls.Length) yield return m;
        }
    }

    /// <summary>How many of the sorted <paramref name="sorted"/> are at or below <paramref name="value"/>.</summary>
    private static int UpperBound(int[] sorted, long value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (sorted[mid] <= value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
