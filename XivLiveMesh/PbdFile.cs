// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Meddle (https://github.com/PassiveModding/Meddle), Copyright (c) Meddle contributors,
// Meddle.Utils/Files/PbdFile.cs and Meddle.Utils/RaceDeformer.cs, as vendored in SkinTattoo @bc3663a.
// Changes: matrices converted to System.Numerics row-vector form, a working identity, the parent-bone
// fallback moved onto a caller-supplied parent lookup, and deformers composed per bone.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace XivLiveMesh;

/// <summary>
/// The racial bone deformer, <c>chara/xls/boneDeformer/human.pbd</c>.
/// <para/>
/// Gear is authored once per body shape family — most of it for Midlanders — and the game bends it onto
/// every other race at draw time with these per-bone matrices, rather than shipping a model per race. A
/// vertex posed without them sits where a Midlander's would, which on a Roegadyn or a Lalafell is nowhere
/// near the cloth the player can see.
/// </summary>
public sealed class PbdFile
{
    private readonly record struct Header(ushort GenderRace, ushort LinkIndex, int Offset);
    private readonly record struct Link(ushort Parent, ushort FirstChild, ushort NextSibling, ushort HeaderIndex);

    private readonly Header[] headers;
    private readonly Link[] links;
    private readonly Dictionary<int, Dictionary<string, Matrix4x4>> deformers = [];

    public PbdFile(ReadOnlySpan<byte> data)
    {
        int count = BitConverter.ToInt32(data);
        headers = new Header[count];
        links = new Link[count];

        int p = 4;
        for (int i = 0; i < count; i++, p += 12)
            headers[i] = new Header(BitConverter.ToUInt16(data[p..]), BitConverter.ToUInt16(data[(p + 2)..]),
                                    BitConverter.ToInt32(data[(p + 4)..]));
        for (int i = 0; i < count; i++, p += 8)
            links[i] = new Link(BitConverter.ToUInt16(data[p..]), BitConverter.ToUInt16(data[(p + 2)..]),
                                BitConverter.ToUInt16(data[(p + 4)..]), BitConverter.ToUInt16(data[(p + 6)..]));

        foreach (var h in headers)
            if (h.Offset > 0 && h.Offset < data.Length && !deformers.ContainsKey(h.Offset))
                deformers[h.Offset] = ReadDeformer(data, h.Offset);
    }

    private static Dictionary<string, Matrix4x4> ReadDeformer(ReadOnlySpan<byte> data, int start)
    {
        int boneCount = BitConverter.ToInt32(data[start..]);
        var names = new string[boneCount];
        int p = start + 4;
        for (int i = 0; i < boneCount; i++, p += 2)
        {
            int at = start + BitConverter.ToInt16(data[p..]);
            int end = at;
            while (end < data.Length && data[end] != 0) end++;
            names[i] = Encoding.UTF8.GetString(data[at..end]);
        }
        p += boneCount * 2 % 4;

        var result = new Dictionary<string, Matrix4x4>(boneCount, StringComparer.Ordinal);
        Span<float> f = stackalloc float[12];   // outside the loop: a stackalloc is only freed on return
        for (int i = 0; i < boneCount; i++, p += 48)
        {
            // Stored as three rows of a column-vector matrix, translation in the last column. System.Numerics
            // transforms row vectors, so the same matrix is its transpose.
            for (int k = 0; k < 12; k++) f[k] = BitConverter.ToSingle(data[(p + k * 4)..]);
            var m = new Matrix4x4(
                f[0], f[4], f[8], 0f,
                f[1], f[5], f[9], 0f,
                f[2], f[6], f[10], 0f,
                f[3], f[7], f[11], 1f);
            result.TryAdd(names[i], m);
        }
        return result;
    }

    /// <summary>
    /// The deformers that take a model authored for <paramref name="from"/> onto a <paramref name="to"/>
    /// skeleton, in the order they apply. Empty when no bending is needed — or none is defined, in which case
    /// the model is drawn as authored.
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> Chain(ushort from, ushort to)
    {
        var chain = new List<IReadOnlyDictionary<string, Matrix4x4>>();
        if (from == to || from == 0 || to == 0) return chain;

        var current = to;
        for (int guard = 0; guard < headers.Length && current != from; guard++)
        {
            int hi = Array.FindIndex(headers, h => h.GenderRace == current);
            if (hi < 0) return [];
            var header = headers[hi];
            if (!deformers.TryGetValue(header.Offset, out var deformer)) return [];
            chain.Add(deformer);

            if (header.LinkIndex >= links.Length) return [];
            var parent = links[header.LinkIndex].Parent;
            if (parent == ushort.MaxValue || parent >= links.Length) return [];
            var parentHeader = links[parent].HeaderIndex;
            if (parentHeader >= headers.Length) return [];
            current = headers[parentHeader].GenderRace;
        }
        if (current != from) return [];

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// One bone's deformation through a whole chain, as a single matrix: each deformer in turn, a bone the
    /// deformer does not list taking its nearest listed ancestor's matrix, and identity where none is listed.
    /// </summary>
    /// <param name="parentOf">The bone's parent name in the character's skeleton, or null at the root.</param>
    public static Matrix4x4 Resolve(IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> chain, string bone,
                                    Func<string, string?> parentOf)
    {
        var result = Matrix4x4.Identity;
        foreach (var deformer in chain)
        {
            string? name = bone;
            for (int guard = 0; name != null && guard < 256; guard++)
            {
                if (deformer.TryGetValue(name, out var m)) { result *= m; break; }
                name = parentOf(name);
            }
        }
        return result;
    }
}
