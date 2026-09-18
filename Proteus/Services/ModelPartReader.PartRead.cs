using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

public static partial class ModelPartReader
{
    private sealed class PartRead
    {
        private readonly byte[] mdl;
        private SecondSkinWriter.Source src = null!;
        private byte[] s = null!;
        private List<float> pos = null!;
        private List<float> nrm = null!;
        private List<MeshSpan> spans = null!;
        private List<ModelPart> parts = null!;
        private Dictionary<string, int> shattered = null!;
        private int end;
        private int ordinal;

        public PartRead(byte[] mdl)
        {
            this.mdl = mdl;
        }

        public ModelParts? Run()
        {
            if (!Begin()) return null;
            if (!ReadMeshes()) return null;
            return BuildParts();
        }

        private bool Begin()
        {
            try { src = SecondSkinWriter.Parse(mdl); }
            catch { return false; }

            s = src.S;
            pos = new List<float>();
            nrm = new List<float>();
            spans = new List<MeshSpan>();
            parts = new List<ModelPart>();
            shattered = new Dictionary<string, int>(StringComparer.Ordinal);

            end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
            ordinal = 0;
            return true;
        }

        private bool ReadMeshes()
        {
            Span<float> tmp = stackalloc float[4];
            for (int m = src.Lod0MeshIndex; m < end; m++)
            {
                int mo = src.MeshStart + m * 36;
                if (mo + 36 > s.Length) break;

                ushort vc = BitConverter.ToUInt16(s, mo);
                // An emptied mesh draws nothing, so it is not a part and takes no ordinal.
                if (vc == 0) continue;

                ushort matIdx = BitConverter.ToUInt16(s, mo + 8);
                var material = matIdx < src.MatNames.Count ? src.MatNames[matIdx] : "?";

                var decl = m < src.Decls.Length ? src.Decls[m] : [];
                SecondSkinWriter.VElem? posEl = null, nrmEl = null;
                foreach (var el in decl)
                {
                    if (el.Usage == SecondSkinWriter.UsePosition) posEl = el;
                    else if (el.Usage == SecondSkinWriter.UseNormal) nrmEl = el;
                }
                if (posEl is not { } pe) continue;

                uint[] vbo =
                {
                    BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                    BitConverter.ToUInt32(s, mo + 28),
                };
                byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
                if (pe.Stream > 2 || bs[pe.Stream] == 0) continue;

                int baseVertex = pos.Count / 3;
                bool ok = true;
                for (int k = 0; k < vc; k++)
                {
                    int pa = (int)(src.Vb + vbo[pe.Stream]) + k * bs[pe.Stream] + pe.Offset;
                    // 16 bytes is the widest element ReadTyped touches (Float4).
                    if (pa < 0 || pa + 16 > s.Length) { ok = false; break; }
                    SecondSkinWriter.ReadTyped(s, pa, pe.Type, tmp);
                    pos.Add(tmp[0]); pos.Add(tmp[1]); pos.Add(tmp[2]);

                    // Appended for every vertex, whatever the normal: a skip would shift every later normal.
                    float nx = 0f, ny = 0f, nz = 0f;
                    if (nrmEl is { } ne && ne.Stream <= 2 && bs[ne.Stream] != 0)
                    {
                        int na = (int)(src.Vb + vbo[ne.Stream]) + k * bs[ne.Stream] + ne.Offset;
                        if (na >= 0 && na + 16 <= s.Length)
                        {
                            SecondSkinWriter.ReadTyped(s, na, ne.Type, tmp);
                            nx = tmp[0]; ny = tmp[1]; nz = tmp[2];
                            // Ubyte4n stores a normal biased into 0..1; unbias it.
                            if (ne.Type == 8) { nx = nx * 2f - 1f; ny = ny * 2f - 1f; nz = nz * 2f - 1f; }
                            float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                            if (nl > 1e-6f) { nx /= nl; ny /= nl; nz /= nl; }
                            else { nx = 0f; ny = 0f; nz = 0f; }
                        }
                    }
                    nrm.Add(nx); nrm.Add(ny); nrm.Add(nz);
                }
                // A truncated buffer costs this mesh only: rewind positions and normals and record no span, or the
                // next mesh's rebased indices would land on garbage.
                if (!ok)
                {
                    pos.RemoveRange(baseVertex * 3, pos.Count - baseVertex * 3);
                    nrm.RemoveRange(baseVertex * 3, nrm.Count - baseVertex * 3);
                    continue;
                }

                spans.Add(new MeshSpan(m, baseVertex, vc));
                ordinal++;
                ushort subIdx = BitConverter.ToUInt16(s, mo + 10), subCount = BitConverter.ToUInt16(s, mo + 12);
                for (int su = 0; su < subCount; su++)
                {
                    int ss = src.SubmeshStart + (subIdx + su) * 16;
                    if (ss + 16 > s.Length) break;
                    uint so = BitConverter.ToUInt32(s, ss), sc = BitConverter.ToUInt32(s, ss + 4);
                    uint mask = BitConverter.ToUInt32(s, ss + 8);

                    // Two questions off one walk of the mask — see ModelPart.Toggleable and ModelPart.AuthorSwitched.
                    // No short-circuit: both answers need every set bit.
                    bool authorSwitched = false, unnamed = false;
                    for (int b = 0; b < 32; b++)
                    {
                        if ((mask & (1u << b)) == 0) continue;
                        if (b >= src.AttrNames.Length) unnamed = true;
                        else if (ContentPieceResolver.PartAttributeBit(src.AttrNames[b]) != null) authorSwitched = true;
                    }
                    bool toggleable = !unnamed;

                    var tris = new List<int>((int)sc);
                    var ordinals = new List<int>((int)sc / 3);
                    for (uint t = 0; t + 2 < sc; t += 3)
                    {
                        int ia = (int)(src.Ib + (so + t) * 2);
                        if (ia < 0 || ia + 6 > s.Length) break;
                        int a = BitConverter.ToUInt16(s, ia),
                            b = BitConverter.ToUInt16(s, ia + 2),
                            c = BitConverter.ToUInt16(s, ia + 4);
                        // A stale index must never reach another mesh's vertices through the rebase; skipping it is
                        // why the ordinal is recorded rather than inferred.
                        if (a >= vc || b >= vc || c >= vc) continue;
                        tris.Add(baseVertex + a); tris.Add(baseVertex + b); tris.Add(baseVertex + c);
                        ordinals.Add((int)(t / 3));
                    }
                    if (tris.Count == 0) continue;

                    var label = $"{ordinal}.{su + 1}";
                    var triArr = tris.ToArray();
                    var ordArr = ordinals.ToArray();
                    parts.Add(Make(m, su, -1, label, material, triArr, ordArr, mask, toggleable, authorSwitched, pos));

                    // Islands are offered only when there are several; a shattered submesh is reported — see MaxIslands.
                    var islands = SplitIslands(triArr, pos);
                    if (islands.Count <= 1) continue;
                    if (islands.Count > MaxIslands) { shattered[label] = islands.Count; continue; }

                    // Largest first: the big island is the garment, the small ones its trimmings. Numbered, not
                    // lettered, since there can be more islands than letters.
                    int i = 0;
                    foreach (var island in islands.OrderByDescending(x => x.Count))
                    {
                        parts.Add(Make(m, su, i, $"{label}.{i + 1}", material,
                            [.. island.SelectMany(k => new[] { triArr[k * 3], triArr[k * 3 + 1], triArr[k * 3 + 2] })],
                            [.. island.Select(k => ordArr[k])], mask, toggleable, authorSwitched, pos));
                        i++;
                    }
                }
            }

            return parts.Count > 0;
        }

        private ModelParts? BuildParts()
        {
            // Wind per vertex, by span, so it lines up with Positions however many meshes were skipped above.
            var wind = new float[pos.Count / 3];
            foreach (var span in spans)
                VertexColorWriter.ReadWind(s, src, span.Mesh, span.Count, wind, span.BaseVertex);

            var (min, max) = Bounds(pos, null);
            return new ModelParts
            {
                Positions = pos.ToArray(),
                Normals = nrm.ToArray(),
                Wind = wind,
                HasWindChannel = spans.All(sp => src.Decls[sp.Mesh].Any(VertexColorWriter.IsSecondColor)),
                FirstColorNotWhite = VertexColorWriter.FirstColorNotWhite(src),
                MeshSpans = spans,
                Parts = parts,
                AttributeNames = src.AttrNames,
                Min = min,
                Max = max,
                ShatteredSubmeshes = shattered,
            };
        }
    }
}
