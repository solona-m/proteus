using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// The game's table of which extra skeleton each race's hairstyle moves with — <c>hairskeletontemplate.est</c>, what a
/// Penumbra <c>Est</c> entry for the hair slot overrides. A hairstyle loads <c>skl_c{race}h{skeleton}.sklb</c> (and its
/// physics beside it) for the skeleton its entry names, not its own number: most races' hairstyle 8 moves with skeleton
/// 9's bones. A hairstyle with no entry loads none.
/// <para/>
/// Layout (Penumbra <c>EstFile</c>): a count, then that many (set id, race code) pairs of u16, sorted, then that many
/// u16 skeleton ids in the same order.
/// </summary>
internal sealed class HairSkeletonTable
{
    public const string GamePath = "chara/xls/charadb/hairskeletontemplate.est";

    private readonly Dictionary<(ushort Race, int Set), int> entries;

    private HairSkeletonTable(Dictionary<(ushort Race, int Set), int> entries) => this.entries = entries;

    /// <summary>How many entries the table has.</summary>
    public int Count => entries.Count;

    /// <summary>The table in <paramref name="bytes"/>; null for no file, or one that is not a whole table — never a part
    /// of one.</summary>
    public static HairSkeletonTable? Parse(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 4) return null;
        long count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (4 + count * 6 > bytes.Length) return null;
        var entries = new Dictionary<(ushort, int), int>((int)count);
        for (int i = 0; i < count; i++)
        {
            ushort set = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4 + i * 4));
            ushort race = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4 + i * 4 + 2));
            ushort skeleton = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4 + (int)count * 4 + i * 2));
            // A pair twice is not a table the game wrote.
            if (!entries.TryAdd((race, set), skeleton)) return null;
        }
        return new HairSkeletonTable(entries);
    }

    /// <summary>The game's own table, through <paramref name="readGame"/>; null when it cannot be read.</summary>
    public static HairSkeletonTable? Read(Func<string, byte[]?> readGame)
    {
        try { return Parse(readGame(GamePath)); }
        catch (Exception) { return null; }
    }

    /// <summary>A reader that reads the table once, on first use — and not at all when nothing asks.</summary>
    public static Func<HairSkeletonTable?> Lazy(Func<string, byte[]?> readGame)
    {
        var table = new Lazy<HairSkeletonTable?>(() => Read(readGame));
        return () => table.Value;
    }

    /// <summary>Whether a table's pairs are in the order the game writes them, by race then set.</summary>
    internal static bool IsSorted(byte[] bytes)
    {
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var keys = Enumerable.Range(0, count)
            .Select(i => (Race: BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4 + i * 4 + 2)),
                          Set: BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4 + i * 4))))
            .ToList();
        return keys.SequenceEqual(keys.OrderBy(k => k.Race).ThenBy(k => k.Set));
    }

    /// <summary>The skeleton <paramref name="race"/>'s hairstyle <paramref name="set"/> loads; 0 for none.</summary>
    public int Loaded(ushort race, int set) => entries.TryGetValue((race, set), out var s) ? s : 0;

    /// <summary>Every skeleton a hairstyle of <paramref name="race"/> other than <paramref name="except"/> loads.</summary>
    public HashSet<int> SkeletonsLoaded(ushort race, int except)
        => entries.Where(e => e.Key.Race == race && e.Key.Set != except).Select(e => e.Value).ToHashSet();

    /// <summary>Every hairstyle of <paramref name="race"/> that loads <paramref name="skeleton"/>.</summary>
    public IEnumerable<int> SetsLoading(ushort race, int skeleton)
        => entries.Where(e => e.Key.Race == race && e.Value == skeleton).Select(e => e.Key.Set);
}
