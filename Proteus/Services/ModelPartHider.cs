using System;
using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// Takes parts out of a model's drawing without changing its layout: every triangle of theirs collapses to one corner,
/// which draws nothing in any pose — skinning moves one point to one place. The file keeps its length, vertex order and
/// every offset, so the game, the brush and the vertex numbering all see the same model; only what it draws changes.
/// <para/>
/// For the live preview of the Move tools: a part locked out of the way (a skirt over the underwear being moved) is
/// hidden on the character as well as in the model view.
/// </summary>
internal static class ModelPartHider
{
    /// <summary>
    /// Collapse, in place, every LOD0 triangle of <paramref name="mdl"/> whose corners — in the concatenated numbering
    /// <paramref name="spans"/> describes — are a polygon in <paramref name="hidden"/>.
    /// </summary>
    /// <returns>How many triangles were collapsed.</returns>
    public static int Hide(byte[] mdl, IReadOnlyList<MeshSpan> spans, IReadOnlySet<PolygonSelection.Key> hidden)
    {
        if (hidden.Count == 0 || spans.Count == 0) return 0;
        var src = SecondSkinWriter.Parse(mdl);

        // Index-buffer slot (absolute, in indices) → the corner its triangle now collapses to, mesh-local.
        var collapsed = new Dictionary<long, ushort>();
        int count = 0;
        foreach (var span in spans)
        {
            int mo = src.MeshStart + span.Mesh * 36;
            if (mo + 36 > mdl.Length) continue;
            uint ic = BitConverter.ToUInt32(mdl, mo + 4), start = BitConverter.ToUInt32(mdl, mo + 16);
            if ((long)src.Ib + ((long)start + ic) * 2 > mdl.Length) continue;

            for (uint t = 0; t + 3 <= ic; t += 3)
            {
                int at = src.Ib + (int)(start + t) * 2;
                ushort a = BitConverter.ToUInt16(mdl, at), b = BitConverter.ToUInt16(mdl, at + 2),
                       c = BitConverter.ToUInt16(mdl, at + 4);
                if (!hidden.Contains(PolygonSelection.Key.Of(span.BaseVertex + a, span.BaseVertex + b, span.BaseVertex + c)))
                    continue;

                BitConverter.TryWriteBytes(mdl.AsSpan(at + 2), a);
                BitConverter.TryWriteBytes(mdl.AsSpan(at + 4), a);
                for (uint k = 0; k < 3; k++) collapsed[start + t + k] = a;
                count++;
            }
        }
        if (count > 0) CollapseShapes(mdl, src, collapsed);
        return count;
    }

    /// <summary>
    /// A shape key rewires index slots to vertices of its own, so with a body slider on, two corners of a collapsed
    /// triangle could be put back apart and draw a sliver of the part. Every slot a collapse took is rewired to the
    /// collapse's corner instead, in every shape, so no shape can open it again.
    /// </summary>
    private static void CollapseShapes(byte[] mdl, SecondSkinWriter.Source src, Dictionary<long, ushort> collapsed)
    {
        // Shape[16] then ShapeMesh[12] then ShapeValue[4]; LOD0 meshes only — see SecondSkinWriter.ReadShapes.
        ushort shapeCount = BitConverter.ToUInt16(mdl, src.Mh + 16), shapeMeshCount = BitConverter.ToUInt16(mdl, src.Mh + 18);
        int shapeMeshBlock = src.ShapeBlock + shapeCount * 16;
        int shapeValueBlock = shapeMeshBlock + shapeMeshCount * 12;
        for (int si = 0; si < shapeCount; si++)
        {
            int shp = src.ShapeBlock + si * 16;
            if (shp + 16 > mdl.Length) return;
            int smStart = BitConverter.ToUInt16(mdl, shp + 4), smCount = BitConverter.ToUInt16(mdl, shp + 10);
            for (int mi = 0; mi < smCount; mi++)
            {
                int sm = shapeMeshBlock + (smStart + mi) * 12;
                if (sm + 12 > mdl.Length) break;
                uint meshIndexOffset = BitConverter.ToUInt32(mdl, sm), valueCount = BitConverter.ToUInt32(mdl, sm + 4),
                     valueStart = BitConverter.ToUInt32(mdl, sm + 8);
                if (shapeValueBlock + ((long)valueStart + valueCount) * 4 > mdl.Length) continue;
                for (uint vi = 0; vi < valueCount; vi++)
                {
                    int sv = shapeValueBlock + (int)(valueStart + vi) * 4;
                    long slot = meshIndexOffset + (long)BitConverter.ToUInt16(mdl, sv);
                    if (collapsed.TryGetValue(slot, out var corner))
                        BitConverter.TryWriteBytes(mdl.AsSpan(sv + 2), corner);
                }
            }
        }
    }
}
