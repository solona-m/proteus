using System;
using System.Collections.Generic;

namespace Proteus.Services;

public static partial class HatCompatSolve
{
    /// <summary>
    /// The bones a Miqo'te's ears hang from. The fur on those ears ships inside the HAIRSTYLE, weighted to
    /// these — and it is not scalp: hats leave a Miqo'te's ears out, so fur cut away or pressed into the skull
    /// with the rest of the hair just leaves the ears bald.
    /// <para/>
    /// Only Miqo'te hair carries them. Of every installed hair model, the 47 that name a <c>j_mimi</c> bone are
    /// all <c>c0701</c>/<c>c0801</c>; Viera and Hrothgar ears live in the face model, not the hair. So no other
    /// race pays anything for this.
    /// </summary>
    private const string EarBonePrefix = "j_mimi";

    /// <summary>
    /// How much of a vertex's weight must ride an ear bone before it counts as fur. A quarter: real fur runs
    /// 0.3–1.0 even where its root row blends into the scalp, while an earless conversion can leave a single
    /// hair vertex holding a thousandth of an ear bone, which is not fur.
    /// </summary>
    internal const float EarWeightFloor = 0.25f;

    /// <summary>
    /// How far from the fur the ear's own skin reaches. Many Miqo'te hairstyles ship the ear as one piece: fur on the
    /// ear bones, and a ring of skin around the ear's root that rides the HEAD bone, so the skinning alone calls it
    /// hair and the cut hides it. Measured over every installed Miqo'te hair (2026-10-16): 22 of the 39 with fur ship
    /// that skin, 2 to 60 triangles of it, every triangle within 60 mm of the fur — most within 30.
    /// </summary>
    internal const float EarSkinReach = 0.06f;

    /// <param name="Fur">Vertices an ear bone carries — the fur itself — and the ear's own skin beside it (see
    /// <see cref="EarSkinReach"/>).</param>
    /// <param name="Touched">Those, plus every vertex sharing a triangle with one — the row the fur is rooted
    /// in. The press must leave that row where it is, or the fur tears away from its own base.</param>
    internal readonly record struct EarGeometry(HashSet<long> Fur, HashSet<long> Touched)
    {
        /// <summary>Nothing to spare: any race but a Miqo'te, and an earless Miqo'te conversion.</summary>
        public static EarGeometry None => new([], []);
    }

    /// <summary>
    /// Which of a hairstyle's vertices are ear fur, read from the skinning rather than from where they sit:
    /// fur hugs the ear, which is exactly where a hat's rim plane says "under the hat".
    /// </summary>
    internal static EarGeometry ReadEarGeometry(
        byte[] mdl, SecondSkinWriter.Source src, IReadOnlyList<MeshVerts> meshes)
    {
        var fur = new HashSet<long>();
        var touched = new HashSet<long>();

        bool anyEarBone = false;
        foreach (var name in src.BoneNames)
            if (name.StartsWith(EarBonePrefix, StringComparison.Ordinal)) { anyEarBone = true; break; }
        if (!anyEarBone) return new EarGeometry(fur, touched);

        foreach (var mv in meshes)
        {
            int mo = src.MeshStart + mv.Mesh * 36;
            if (mo + 36 > mdl.Length) continue;
            ushort vc = BitConverter.ToUInt16(mdl, mo);
            ushort tbl = BitConverter.ToUInt16(mdl, mo + 14);
            if (vc == 0 || tbl >= src.BoneTables.Length) continue;

            var decl = mv.Mesh < src.Decls.Length ? src.Decls[mv.Mesh] : [];
            SecondSkinWriter.VElem? wEl = null, iEl = null;
            foreach (var el in decl)
            {
                if (el.Usage == SecondSkinWriter.UseBlendWeight) wEl ??= el;
                else if (el.Usage == SecondSkinWriter.UseBlendIndices) iEl ??= el;
            }
            if (wEl is not { } we || iEl is not { } ie) continue;          // unskinned: no ear to find

            // Which LOCAL slots of THIS mesh's bone table are ear bones. Resolved once per mesh, since the
            // blend indices a vertex carries are indices into the mesh's own table, not into BoneNames.
            var table = src.BoneTables[tbl];
            var earLocal = new bool[table.Length];
            bool anyLocal = false;
            for (int i = 0; i < table.Length; i++)
            {
                if (table[i] >= src.BoneNames.Length) continue;
                if (!src.BoneNames[table[i]].StartsWith(EarBonePrefix, StringComparison.Ordinal)) continue;
                earLocal[i] = true;
                anyLocal = true;
            }
            if (!anyLocal) continue;

            uint[] vOff =
            {
                BitConverter.ToUInt32(mdl, mo + 20), BitConverter.ToUInt32(mdl, mo + 24),
                BitConverter.ToUInt32(mdl, mo + 28),
            };
            byte[] strides = { mdl[mo + 32], mdl[mo + 33], mdl[mo + 34] };
            if (we.Stream > 2 || ie.Stream > 2 || strides[we.Stream] == 0 || strides[ie.Stream] == 0) continue;
            int nInf = SecondSkinWriter.BlendCount(we.Type);

            int end = Math.Min(vc, mv.Positions.Length);
            for (int v = 0; v < end; v++)
            {
                int wa = (int)(src.Vb + vOff[we.Stream]) + v * strides[we.Stream] + we.Offset;
                int ia = (int)(src.Vb + vOff[ie.Stream]) + v * strides[ie.Stream] + ie.Offset;
                if (wa < 0 || ia < 0 || wa + nInf > mdl.Length || ia + nInf > mdl.Length) break;

                float ear = 0f;
                for (int k = 0; k < nInf; k++)
                {
                    int local = mdl[ia + k];
                    if (local < earLocal.Length && earLocal[local]) ear += mdl[wa + k] / 255f;
                }
                if (ear >= EarWeightFloor) fur.Add(VertexKey(mv.Mesh, v));
            }
        }

        if (fur.Count == 0) return new EarGeometry(fur, touched);

        AddEarSkin(mdl, src, meshes, fur);

        // One ring out, over whole triangles: a triangle with a fur corner is fur, and so are its other corners.
        foreach (var mv in meshes)
        {
            int mo = src.MeshStart + mv.Mesh * 36;
            if (mo + 36 > mdl.Length) continue;
            uint ic = BitConverter.ToUInt32(mdl, mo + 4), start = BitConverter.ToUInt32(mdl, mo + 16);
            if ((long)src.Ib + (start + ic) * 2 > mdl.Length) continue;

            for (uint t = 0; t + 3 <= ic; t += 3)
            {
                int a = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t) * 2);
                int b = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t + 1) * 2);
                int c = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t + 2) * 2);
                long ka = VertexKey(mv.Mesh, a), kb = VertexKey(mv.Mesh, b), kc = VertexKey(mv.Mesh, c);
                if (!fur.Contains(ka) && !fur.Contains(kb) && !fur.Contains(kc)) continue;
                touched.Add(ka);
                touched.Add(kb);
                touched.Add(kc);
            }
        }

        // A fur vertex no triangle draws is still fur; the ring walk above would never have reached it.
        touched.UnionWith(fur);
        return new EarGeometry(fur, touched);
    }

    /// <summary>
    /// Add the ear's own skin to <paramref name="fur"/>: in an EAR submesh — one where triangles with a fur corner
    /// outnumber the rest — the other triangles within <see cref="EarSkinReach"/> of a fur vertex. Only there: one hair
    /// (Kikiro) welds its whole scalp into the fur's submesh, and that hair still goes under the hat.
    /// </summary>
    private static void AddEarSkin(
        byte[] mdl, SecondSkinWriter.Source src, IReadOnlyList<MeshVerts> meshes, HashSet<long> fur)
    {
        var skin = new List<long>();
        float reach2 = EarSkinReach * EarSkinReach;
        foreach (var mv in meshes)
        {
            int mo = src.MeshStart + mv.Mesh * 36;
            if (mo + 36 > mdl.Length) continue;
            ushort subIdx = BitConverter.ToUInt16(mdl, mo + 10), subCount = BitConverter.ToUInt16(mdl, mo + 12);
            var pos = mv.Positions;

            for (int s = 0; s < subCount; s++)
            {
                int ss = src.SubmeshStart + (subIdx + s) * 16;
                if (ss + 16 > mdl.Length) break;
                uint io = BitConverter.ToUInt32(mdl, ss), ic = BitConverter.ToUInt32(mdl, ss + 4);
                if ((long)src.Ib + (io + ic) * 2 > mdl.Length) break;

                var furCorners = new List<System.Numerics.Vector3>();
                var plain = new List<(int A, int B, int C)>();
                for (uint t = 0; t + 3 <= ic; t += 3)
                {
                    int a = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t) * 2);
                    int b = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t + 1) * 2);
                    int c = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t + 2) * 2);
                    if (a >= pos.Length || b >= pos.Length || c >= pos.Length) continue;
                    bool fa = fur.Contains(VertexKey(mv.Mesh, a)), fb = fur.Contains(VertexKey(mv.Mesh, b)),
                         fc = fur.Contains(VertexKey(mv.Mesh, c));
                    if (!fa && !fb && !fc) { plain.Add((a, b, c)); continue; }
                    if (fa) furCorners.Add(pos[a]);
                    if (fb) furCorners.Add(pos[b]);
                    if (fc) furCorners.Add(pos[c]);
                }
                int furTriangles = (int)(ic / 3) - plain.Count;
                if (plain.Count == 0 || furTriangles <= plain.Count) continue;   // not an ear submesh

                foreach (var (a, b, c) in plain)
                {
                    var centre = (pos[a] + pos[b] + pos[c]) / 3f;
                    bool near = false;
                    foreach (var f in furCorners)
                        if (System.Numerics.Vector3.DistanceSquared(f, centre) <= reach2) { near = true; break; }
                    if (!near) continue;
                    skin.Add(VertexKey(mv.Mesh, a));
                    skin.Add(VertexKey(mv.Mesh, b));
                    skin.Add(VertexKey(mv.Mesh, c));
                }
            }
        }
        // Added after the walk, so a skin triangle does not make its neighbours count as fur-cornered above.
        fur.UnionWith(skin);
    }
}
