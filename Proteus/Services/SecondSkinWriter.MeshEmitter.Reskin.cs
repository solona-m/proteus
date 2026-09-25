using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

public static partial class SecondSkinWriter
{
    /// <summary>
    /// Write one vertex's influences as blend bytes: up to <paramref name="nInf"/> of them, each weight rounded to a
    /// byte and the rounding error folded into the LARGEST so the bytes come to exactly 255 (short of it, the vertex
    /// shrinks toward the origin; over it, it grows). Not the first: a list may lead with what must survive rather
    /// than what weighs most, and a first influence of one byte cannot absorb an error of three. An influence whose
    /// bone <paramref name="slotOf"/> cannot place (-1), or whose weight rounds to nothing, is skipped and counted in
    /// <paramref name="dropped"/>.
    /// <para/>
    /// A list LONGER than the slots keeps its heaviest, in the order it gave them, and counts the rest in
    /// <paramref name="trimmed"/>. Not simply the first <paramref name="nInf"/>: the planner lists a vertex's own
    /// garment bones ahead of the new body's share (<see cref="BodyRetarget.Combine"/>) and plans to eight slots, so
    /// on a four-slot cloth mesh taking the front of the list drops the BODY share whole wherever the garment's own
    /// bones already fill four — leaving that vertex rigged to the body the garment came from while the cloth beside
    /// it follows the new one.
    /// </summary>
    /// <param name="wb">Weight bytes, <paramref name="nInf"/> long; cleared first, so unused slots are zero.</param>
    /// <param name="ib">Index bytes into the mesh's bone table, likewise.</param>
    /// <param name="trimmed">Influences left out because the mesh had no slot for them.</param>
    /// <returns>How many influences were written.</returns>
    internal static int EncodeBlend(IReadOnlyList<(string Bone, float W)> w, int nInf, Func<string, int> slotOf,
                                    Span<byte> wb, Span<byte> ib, ref int dropped, ref int trimmed)
    {
        wb[..nInf].Clear();
        ib[..nInf].Clear();

        // The heaviest nInf, still in the caller's order. Untouched when the list already fits, which is every
        // eight-slot mesh and most four-slot ones.
        if (w.Count > nInf)
        {
            var order = Enumerable.Range(0, w.Count).OrderByDescending(i => w[i].W).Take(nInf)
                                  .OrderBy(i => i).Select(i => w[i]).ToList();
            trimmed += w.Count - order.Count;
            w = order;
        }

        int used = 0, total = 0;
        foreach (var (bone, f) in w)
        {
            if (used == nInf) break;
            int at = slotOf(bone);
            if (at < 0) { dropped++; continue; }
            byte q = (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
            if (q == 0) continue;
            ib[used] = (byte)at;
            wb[used] = q;
            total += q;
            used++;
        }
        if (used > 0)
        {
            int largest = 0;
            for (int k = 1; k < used; k++)
                if (wb[k] > wb[largest]) largest = k;
            wb[largest] = (byte)Math.Clamp(wb[largest] + (255 - total), 0, 255);
        }
        return used;
    }

    private sealed partial class ShellBuild
    {
        private sealed partial class MeshEmitter
        {
            /// <summary>
            /// Widen this mesh's blend elements from four influences to eight, rewriting stream 0 and the declaration.
            /// Only when stream 0 holds exactly position, weights and indices with position first — the layout every
            /// game model uses; anything else is left alone rather than guessed at, and false comes back.
            /// </summary>
            /// <param name="nv">Vertices in the output streams.</param>
            private bool WidenToEight(int nv)
            {
                VElem? wEl = null, iEl = null;
                foreach (var el in decl)
                {
                    if (el.Usage == UseBlendWeight) wEl ??= el;
                    if (el.Usage == UseBlendIndices) iEl ??= el;
                }
                if (wEl is not { } wUp || iEl is not { } iUp) return false;
                if (BlendCount(wUp.Type) == 8) return true;
                if (wUp.Stream != 0 || iUp.Stream != 0 || decl.Count(e => e.Stream == 0) != 3) return false;
                if (decl.FirstOrDefault(e => e.Stream == 0 && e.Usage == UsePosition) is not { Offset: 0 }) return false;
                if (outStrides[0] < 20) return false;

                const int wOffNew = 12, iOffNew = 20, strideNew = 28;
                var wide = new byte[nv * strideNew];
                for (int v = 0; v < nv; v++)
                {
                    int from = v * outStrides[0], to = v * strideNew;
                    Buffer.BlockCopy(outStreams[0], from, wide, to, 12);   // position
                    for (int q = 0; q < 4; q++)
                    {
                        wide[to + wOffNew + q] = outStreams[0][from + wUp.Offset + q];
                        wide[to + iOffNew + q] = outStreams[0][from + iUp.Offset + q];
                    }
                }
                outStreams[0] = wide;
                outStrides[0] = strideNew;

                // The declaration has to say so too, or the game reads the old layout.
                for (int e = 0; e < 17; e++)
                {
                    int x = e * 8;
                    if (declBlock[x] == 0xFF) break;
                    if (declBlock[x + 3] == UseBlendWeight) { declBlock[x + 1] = wOffNew; declBlock[x + 2] = 17; }
                    else if (declBlock[x + 3] == UseBlendIndices) { declBlock[x + 1] = iOffNew; declBlock[x + 2] = 17; }
                }
                decl = decl.Select(e =>
                    e.Usage == UseBlendWeight ? e with { Offset = wOffNew, Type = 17 } :
                    e.Usage == UseBlendIndices ? e with { Offset = iOffNew, Type = 17 } : e).ToArray();
                return true;
            }

            /// <summary>
            /// Give a host mesh new skinning: every vertex <see cref="reskin"/> names takes those influences, written
            /// against a bone table grown to fit. The table starts as the host's own, so every untouched vertex's
            /// indices still mean what they did; a bone it lacks is appended by its place in the merged bone list,
            /// which is why every bone a reskin names has to come from a model the build was given.
            /// <para/>
            /// Widened to eight influences when a vertex needs more than four AND the mesh is SKIN; otherwise the
            /// first four of each list are kept — the lists put what must survive first — and <see cref="EncodeBlend"/>
            /// folds what was dropped back into the largest, so the weights still come to 255.
            /// <para/>
            /// Only skin, because only <c>skin.shpk</c> reads eight. The character shaders declare their input as
            /// <c>float4 blendWeight</c> and <c>int4 blendIndices</c>, so on a garment the influences past the fourth
            /// are never read: the weights the shader does see then sum to less than one and the vertex is drawn
            /// pulled toward the origin — a dent whose inner surface shows as a dark band. Measured on "Rana"
            /// refitted to Neolithe, 143 of the shirt's vertices wanted a fifth influence, 51 of them in a symmetric
            /// pair of bands on the sleeves, which is where the artifact was. A body mod's own eight-wide meshes are
            /// evidence the other way — the Neolithe body carries 1058 five-influence vertices and draws correctly.
            /// <para/>
            /// Nothing here narrows a mesh: a source already eight-wide keeps its eight whatever it wears.
            /// </summary>
            private void ReskinHost()
            {
                if (reskin is not { } plan) return;

                if (plan.Any(w => w is { Length: > 4 }) && MeshIsSkin()) WidenToEight(vc);

                VElem? wEl = null, iEl = null;
                foreach (var el in decl)
                {
                    if (el.Usage == UseBlendWeight) wEl ??= el;
                    if (el.Usage == UseBlendIndices) iEl ??= el;
                }
                if (wEl is not { } we || iEl is not { } ie) return;

                var srcTbl = srcBoneTbl < src.BoneTables.Length ? src.BoneTables[srcBoneTbl] : [];
                var tbl = new List<ushort>();
                var slot = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var bi in srcTbl)
                {
                    var name = bi < src.BoneNames.Length ? src.BoneNames[bi] : null;
                    if (name != null && build.boneIndex.TryGetValue(name, out var union))
                    {
                        slot.TryAdd(name, tbl.Count);
                        tbl.Add(union);
                    }
                    else tbl.Add(0);
                }

                int SlotOf(string bone)
                {
                    if (slot.TryGetValue(bone, out int at)) return at;
                    if (!build.boneIndex.TryGetValue(bone, out var union) || tbl.Count >= 255) return -1;
                    slot[bone] = tbl.Count;
                    tbl.Add(union);
                    return tbl.Count - 1;
                }

                int nInf = BlendCount(we.Type);
                Span<byte> wb = stackalloc byte[8], ib = stackalloc byte[8];
                int written = 0;
                for (int i = 0; i < vc && i < plan.Length; i++)
                {
                    if (plan[i] is not { Length: > 0 } w) continue;
                    if (EncodeBlend(w, nInf, SlotOf, wb, ib, ref reskinDropped, ref reskinTrimmed) == 0) continue;
                    int wo = i * outStrides[we.Stream] + we.Offset;
                    int io = i * outStrides[ie.Stream] + ie.Offset;
                    for (int q = 0; q < nInf; q++)
                    {
                        outStreams[we.Stream][wo + q] = wb[q];
                        outStreams[ie.Stream][io + q] = ib[q];
                    }
                    written++;
                }
                capBoneTable = tbl.ToArray();
                build.reskinned += written;
                build.reskinDropped += reskinDropped;
                build.reskinTrimmed += reskinTrimmed;
            }

            /// <summary>Whether this mesh draws with a body-skin material, and so with <c>skin.shpk</c>.</summary>
            private bool MeshIsSkin()
            {
                int mat = BitConverter.ToUInt16(src.S, src.MeshStart + m * 36 + 8);
                return mat < src.MatNames.Count && IsBodySkinMaterial(src.MatNames[mat]);
            }

            /// <summary>Influences this mesh's reskin could not place: a bone in no model the build was given, or a
            /// full table.</summary>
            private int reskinDropped;

            /// <summary>Influences left out because this mesh had no slot for them — see <see cref="EncodeBlend"/>.
            /// Counted separately: a trim is the format's limit, not anything gone wrong.</summary>
            private int reskinTrimmed;
        }
    }
}
