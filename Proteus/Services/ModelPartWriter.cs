using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Edits a model's geometry without touching its structure.
/// <para/>
/// Everything here works by rewriting INDEX ENTRIES and nothing else: no header field moves, no table grows,
/// no offset shifts, no bone table or LOD is touched, and the file's length is identical. That is what makes
/// it safe to point the game at the result of an operation the user asked for on a whim and will undo a
/// second later.
/// </summary>
public static class ModelPartWriter
{
    /// <summary>
    /// A copy of <paramref name="mdl"/> showing only <paramref name="keep"/>.
    /// <para/>
    /// Hidden triangles are made DEGENERATE — all three corners set to one vertex — rather than removed.
    /// A zero-area triangle rasterizes nothing, so the geometry disappears while every index range, every
    /// submesh count and every bone window keeps exactly the shape the game expects. Removing them properly
    /// means recomputing all of that, which is the right thing to do when publishing a permanent edit and
    /// far too much machinery to put behind a preview button.
    /// <para/>
    /// The kept corner is the triangle's OWN first index, never 0: a vertex index must stay inside the
    /// mesh's own range, and index 0 belongs to whichever mesh the buffer starts with.
    /// <para/>
    /// Returns null if the model cannot be parsed — the caller shows the part list without a preview rather
    /// than publishing a model it does not understand.
    /// </summary>
    public static byte[]? Isolate(byte[] mdl, IEnumerable<ModelPart> keep)
    {
        SecondSkinWriter.Source src;
        try { src = SecondSkinWriter.Parse(mdl); }
        catch { return null; }

        // Which (mesh, submesh) survive whole, and which survive in part. An island's siblings share its
        // submesh, so a submesh can be half-kept — those are the ones that need triangle-level work.
        var wholeSubs = new HashSet<(int, int)>();
        var keptTriangles = new HashSet<(int, int, int)>();   // (mesh, submesh, triangle ordinal in submesh)
        foreach (var part in keep)
        {
            if (part.Island < 0) wholeSubs.Add((part.Mesh, part.Submesh));
            else foreach (var t in part.Ordinals) keptTriangles.Add((part.Mesh, part.Submesh, t));
        }

        var o = (byte[])mdl.Clone();
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > o.Length || BitConverter.ToUInt16(o, mo) == 0) continue;

            ushort subIdx = BitConverter.ToUInt16(o, mo + 10), subCount = BitConverter.ToUInt16(o, mo + 12);
            for (int su = 0; su < subCount; su++)
            {
                if (wholeSubs.Contains((m, su))) continue;

                int ss = src.SubmeshStart + (subIdx + su) * 16;
                if (ss + 16 > o.Length) break;
                uint so = BitConverter.ToUInt32(o, ss), sc = BitConverter.ToUInt32(o, ss + 4);

                for (uint t = 0; t + 2 < sc; t += 3)
                {
                    if (keptTriangles.Contains((m, su, (int)(t / 3)))) continue;
                    int ia = (int)(src.Ib + (so + t) * 2);
                    if (ia < 0 || ia + 6 > o.Length) break;
                    var a = BitConverter.ToUInt16(o, ia);
                    BitConverter.TryWriteBytes(o.AsSpan(ia + 2), a);
                    BitConverter.TryWriteBytes(o.AsSpan(ia + 4), a);
                }
            }
        }
        return o;
    }

}
