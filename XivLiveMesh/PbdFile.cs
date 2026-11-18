// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) Solona
// Written from docs/pbd-format-spec.md (clean-room implementation).

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;

namespace XivLiveMesh;

/// <summary>
/// The game's racial bone deformer file (<c>chara/xls/boneDeformer/human.pbd</c>): per race, a set of bone matrices
/// that bend a model authored for one race onto another race's skeleton, and the tree the races form.
/// <para/>
/// Everything is read once, in the constructor; the object never changes afterwards, so one instance can be shared
/// across threads. See <c>docs/pbd-format-spec.md</c> for the format and the rules followed here.
/// </summary>
public sealed class PbdFile
{
    private const ushort NoLink = 0xFFFF;
    private const int MaxAncestorNames = 256;   // the bone itself and up to 255 ancestors (spec §5)

    private readonly int _count;
    private readonly ushort[] _entryRace;
    private readonly ushort[] _entryLink;
    private readonly IReadOnlyDictionary<string, Matrix4x4>?[] _entryBlock;
    private readonly ushort[] _linkParent;
    private readonly ushort[] _linkEntry;
    private readonly Dictionary<ushort, int> _firstEntryOfRace;

    public PbdFile(ReadOnlySpan<byte> data)
    {
        int count = ReadInt32(data, 0);

        // Check the race count against the bytes there are before allocating anything from it (spec §3).
        if (count < 0 || 4L + 20L * count > data.Length)
            throw new ArgumentOutOfRangeException(nameof(data),
                $"The deformer file claims {count} races, which do not fit in its {data.Length} bytes.");

        _count = count;
        _entryRace = new ushort[count];
        _entryLink = new ushort[count];
        _entryBlock = new IReadOnlyDictionary<string, Matrix4x4>?[count];
        _linkParent = new ushort[count];
        _linkEntry = new ushort[count];
        _firstEntryOfRace = new Dictionary<ushort, int>(count);

        var blocksByOffset = new Dictionary<int, IReadOnlyDictionary<string, Matrix4x4>>();
        int linkTable = 4 + 12 * count;

        for (int i = 0; i < count; i++)
        {
            int entry = 4 + 12 * i;
            ushort race = ReadUInt16(data, entry);
            _entryRace[i] = race;
            _entryLink[i] = ReadUInt16(data, entry + 2);
            _firstEntryOfRace.TryAdd(race, i);

            int offset = ReadInt32(data, entry + 4);
            if (offset > 0 && offset < data.Length)
            {
                if (!blocksByOffset.TryGetValue(offset, out var block))
                {
                    block = ReadBlock(data, offset);
                    blocksByOffset.Add(offset, block);
                }
                _entryBlock[i] = block;
            }

            int link = linkTable + 8 * i;
            _linkParent[i] = ReadUInt16(data, link);
            _linkEntry[i] = ReadUInt16(data, link + 6);
        }
    }

    // The deformers that take a model authored for `from` onto a `to` skeleton, in the order they apply;
    // empty when no bending is needed or none is defined.
    public IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> Chain(ushort from, ushort to)
    {
        if (from == to || from == 0 || to == 0)
            return new List<IReadOnlyDictionary<string, Matrix4x4>>();

        var collected = new List<IReadOnlyDictionary<string, Matrix4x4>>();
        ushort current = to;
        while (true)
        {
            if (!_firstEntryOfRace.TryGetValue(current, out int entry))
                return Empty();
            var block = _entryBlock[entry];
            if (block == null)
                return Empty();
            if (collected.Count >= _count)
                return Empty();   // a loop: N blocks gathered without meeting the source
            collected.Add(block);

            ushort link = _entryLink[entry];
            if (link >= _count)
                return Empty();
            ushort parentLink = _linkParent[link];
            if (parentLink == NoLink || parentLink >= _count)
                return Empty();
            ushort parentEntry = _linkEntry[parentLink];
            if (parentEntry >= _count)
                return Empty();

            ushort parentRace = _entryRace[parentEntry];
            if (parentRace == from)
            {
                collected.Reverse();   // source side first, the target's own block last
                return collected;
            }
            current = parentRace;
        }

        static List<IReadOnlyDictionary<string, Matrix4x4>> Empty() => new();
    }

    // One bone's deformation through a whole chain, as a single matrix.
    public static Matrix4x4 Resolve(IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> chain, string bone,
                                    Func<string, string?> parentOf)
    {
        var result = Matrix4x4.Identity;
        for (int i = 0; i < chain.Count; i++)
            if (TryFind(chain[i], bone, parentOf, out var m))
                result *= m;
        return result;
    }

    // Whether any deformer in the chain lists the bone or one of its ancestors.
    public static bool Lists(IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> chain, string bone,
                             Func<string, string?> parentOf)
    {
        for (int i = 0; i < chain.Count; i++)
            if (TryFind(chain[i], bone, parentOf, out _))
                return true;
        return false;
    }

    /// <summary>The bone's own matrix in one deformer, else its nearest listed ancestor's, trying at most 256 names.</summary>
    private static bool TryFind(IReadOnlyDictionary<string, Matrix4x4> deformer, string bone,
                                Func<string, string?> parentOf, out Matrix4x4 matrix)
    {
        string? name = bone;
        for (int tried = 1; ; tried++)
        {
            if (deformer.TryGetValue(name, out matrix))
                return true;
            if (tried >= MaxAncestorNames)
                break;
            name = parentOf(name);
            if (name == null)
                break;
        }
        matrix = default;
        return false;
    }

    /// <summary>One deformer block (spec §2): bone names and their matrices; the first of a repeated name wins.</summary>
    private static IReadOnlyDictionary<string, Matrix4x4> ReadBlock(ReadOnlySpan<byte> data, int origin)
    {
        int bones = ReadInt32(data, origin);
        long pad = (2L * bones) % 4;
        // Check the bone count against the bytes left before allocating anything from it (spec §3).
        if (bones < 0 || origin + 4L + 2L * bones + pad + 48L * bones > data.Length)
            throw new ArgumentOutOfRangeException(nameof(data),
                $"A deformer block at byte {origin} claims {bones} bones, which do not fit in the file.");

        int matrices = origin + 4 + 2 * bones + (int)pad;
        var map = new Dictionary<string, Matrix4x4>(bones, StringComparer.Ordinal);
        for (int b = 0; b < bones; b++)
        {
            string name = ReadName(data, origin + (long)ReadUInt16(data, origin + 4 + 2 * b));
            if (map.ContainsKey(name))
                continue;
            map.Add(name, ReadMatrix(data, matrices + 48 * b));
        }
        return new ReadOnlyDictionary<string, Matrix4x4>(map);
    }

    /// <summary>Bytes up to the next zero (or the end of the file), decoded as UTF-8.</summary>
    private static string ReadName(ReadOnlySpan<byte> data, long position)
    {
        if (position < 0 || position > data.Length)
            throw new ArgumentOutOfRangeException(nameof(data),
                $"A bone name at byte {position} lies past the end of the file ({data.Length} bytes).");
        var rest = data[(int)position..];
        int end = rest.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]);
    }

    /// <summary>Twelve floats, the top three rows of a column-vector transform, stored transposed (spec §2.3).</summary>
    private static Matrix4x4 ReadMatrix(ReadOnlySpan<byte> data, int at)
    {
        var f = data.Slice(at, 48);
        return new Matrix4x4(
            F(f, 0), F(f, 4), F(f, 8), 0f,
            F(f, 1), F(f, 5), F(f, 9), 0f,
            F(f, 2), F(f, 6), F(f, 10), 0f,
            F(f, 3), F(f, 7), F(f, 11), 1f);

        static float F(ReadOnlySpan<byte> floats, int n) => BinaryPrimitives.ReadSingleLittleEndian(floats[(4 * n)..]);
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, long at)
        => BinaryPrimitives.ReadInt32LittleEndian(Bytes(data, at, 4));

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, long at)
        => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(data, at, 2));

    private static ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> data, long at, int width)
    {
        if (at < 0 || at + width > data.Length)
            throw new ArgumentOutOfRangeException(nameof(data),
                $"Reading {width} bytes at byte {at} runs past the end of the file ({data.Length} bytes).");
        return data.Slice((int)at, width);
    }
}
