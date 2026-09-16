using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>
/// A model as <see cref="XivLiveMesh"/> poses it: bind-pose vertices, their bone weights, and the triangles
/// the game is drawing with the model's shape keys applied.
/// <para/>
/// VERTEX ORDER IS <see cref="ModelPartReader.Read"/>'S, index for index: the same LOD0 meshes, skipped for
/// the same reasons, concatenated the same way. A point picked on the live character therefore names the
/// same vertices the brush's <see cref="MeshVolumeSolve"/> edits, with no mapping in between.
/// </summary>
public static partial class ModelSkinReader
{
    /// <summary>Read a model, or null when it cannot be read.</summary>
    /// <param name="enabledShapes">Shape keys the game has on for this model; null or empty for none.</param>
    /// <param name="gamePath">The path the game asked for, used for the race the model was authored for.</param>
    public static SkinnedMesh? Read(byte[] mdl, IReadOnlySet<string>? enabledShapes, string? gamePath)
    {
        SecondSkinWriter.Source src;
        try { src = SecondSkinWriter.Parse(mdl); }
        catch { return null; }

        var s = src.S;
        var positions = new List<Vector3>();
        var boneIdx = new List<ushort>();
        var boneW = new List<float>();
        var tris = new List<int>();
        var baseTris = new List<int>();
        var triMats = new List<ushort>();
        Span<float> tmp = stackalloc float[4];

        // Shape edits keyed by absolute index-buffer slot, for the enabled shapes only.
        var rewire = new Dictionary<long, ushort>();
        if (enabledShapes is { Count: > 0 })
            foreach (var (name, entries) in src.Shapes)
            {
                if (!enabledShapes.Contains(name)) continue;
                foreach (var entry in entries)
                foreach (var (b, replace) in entry.Values)
                    rewire[entry.MeshIndexOffset + (long)b] = replace;
            }

        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > s.Length) break;

            ushort vc = BitConverter.ToUInt16(s, mo);
            if (vc == 0) continue;                                     // same skips as ModelPartReader.Read

            var decl = m < src.Decls.Length ? src.Decls[m] : [];
            SecondSkinWriter.VElem? posEl = null, wEl = null, iEl = null;
            foreach (var el in decl)
            {
                if (el.Usage == SecondSkinWriter.UsePosition) posEl = el;
                else if (el.Usage == SecondSkinWriter.UseBlendWeight) wEl ??= el;
                else if (el.Usage == SecondSkinWriter.UseBlendIndices) iEl ??= el;
            }
            if (posEl is not { } pe) continue;

            uint[] vbo =
            {
                BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                BitConverter.ToUInt32(s, mo + 28),
            };
            byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
            if (pe.Stream > 2 || bs[pe.Stream] == 0) continue;

            ushort meshBoneTbl = BitConverter.ToUInt16(s, mo + 14);
            var boneTbl = meshBoneTbl < src.BoneTables.Length ? src.BoneTables[meshBoneTbl] : [];
            bool weighted = wEl is { } we && iEl is { } ie && we.Stream <= 2 && ie.Stream <= 2
                            && bs[we.Stream] != 0 && bs[ie.Stream] != 0;
            int nInf = weighted && wEl!.Value.Type == 17 ? 8 : 4;

            int baseVertex = positions.Count;
            bool ok = true;
            for (int k = 0; k < vc; k++)
            {
                int pa = (int)(src.Vb + vbo[pe.Stream]) + k * bs[pe.Stream] + pe.Offset;
                if (pa < 0 || pa + 16 > s.Length) { ok = false; break; }
                SecondSkinWriter.ReadTyped(s, pa, pe.Type, tmp);
                positions.Add(new Vector3(tmp[0], tmp[1], tmp[2]));

                int written = 0;
                if (weighted)
                {
                    var w = wEl!.Value;
                    var ix = iEl!.Value;
                    int wa = (int)(src.Vb + vbo[w.Stream]) + k * bs[w.Stream] + w.Offset;
                    int ia = (int)(src.Vb + vbo[ix.Stream]) + k * bs[ix.Stream] + ix.Offset;
                    if (wa >= 0 && ia >= 0 && wa + nInf <= s.Length && ia + nInf <= s.Length)
                        for (int q = 0; q < nInf && written < SkinnedMesh.MaxInfluences; q++)
                        {
                            float f = s[wa + q] / 255f;
                            int local = s[ia + q];
                            if (f <= 0f || local >= boneTbl.Length || boneTbl[local] >= src.BoneNames.Length) continue;
                            boneIdx.Add(boneTbl[local]);
                            boneW.Add(f);
                            written++;
                        }
                }
                for (; written < SkinnedMesh.MaxInfluences; written++) { boneIdx.Add(0); boneW.Add(0f); }
            }
            if (!ok)
            {
                positions.RemoveRange(baseVertex, positions.Count - baseVertex);
                boneIdx.RemoveRange(baseVertex * SkinnedMesh.MaxInfluences, boneIdx.Count - baseVertex * SkinnedMesh.MaxInfluences);
                boneW.RemoveRange(baseVertex * SkinnedMesh.MaxInfluences, boneW.Count - baseVertex * SkinnedMesh.MaxInfluences);
                continue;
            }

            ushort matIdx = BitConverter.ToUInt16(s, mo + 8);
            uint ic = BitConverter.ToUInt32(s, mo + 4), start = BitConverter.ToUInt32(s, mo + 16);
            for (uint t = 0; t + 2 < ic; t += 3)
            {
                int at = (int)(src.Ib + (start + t) * 2);
                if (at < 0 || at + 6 > s.Length) break;
                int a = BitConverter.ToUInt16(s, at), b = BitConverter.ToUInt16(s, at + 2), c = BitConverter.ToUInt16(s, at + 4);
                if (a >= vc || b >= vc || c >= vc) continue;

                long slot = start + t;
                int sa = rewire.TryGetValue(slot, out var ra) && ra < vc ? ra : a;
                int sb = rewire.TryGetValue(slot + 1, out var rb) && rb < vc ? rb : b;
                int sc = rewire.TryGetValue(slot + 2, out var rc) && rc < vc ? rc : c;
                tris.Add(baseVertex + sa); tris.Add(baseVertex + sb); tris.Add(baseVertex + sc);
                baseTris.Add(baseVertex + a); baseTris.Add(baseVertex + b); baseTris.Add(baseVertex + c);
                triMats.Add(matIdx);
            }
        }

        if (tris.Count == 0) return null;
        var mesh = new SkinnedMesh
        {
            Positions = positions.ToArray(),
            Triangles = tris.ToArray(),
            BaseTriangles = baseTris.ToArray(),
            MaterialNames = [.. src.MatNames],
            TriangleMaterials = triMats.ToArray(),
            BoneNames = src.BoneNames,
            BoneIndices = boneIdx.ToArray(),
            BoneWeights = boneW.ToArray(),
            GenderRace = RaceOf(gamePath),
        };
        mesh.Validate();
        return mesh;
    }

    /// <summary>The cXXXX race code in a game path as a number (c0201 → 201), or 0 when it has none.</summary>
    public static ushort RaceOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        var match = RaceCode().Match(path);
        return match.Success ? ushort.Parse(match.Groups[1].Value) : (ushort)0;
    }

    // A 'c' and four digits not preceded by a letter, so "chara" and "acc0101" do not match.
    [GeneratedRegex(@"(?<![a-z])c(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex RaceCode();
}
