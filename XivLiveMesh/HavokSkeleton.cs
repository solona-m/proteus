// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XivLiveMesh;

/// <summary>
/// The bone hierarchy of a skeleton file (<c>.sklb</c>): every bone's name and its parent, read from the Havok
/// binary tagfile the file carries — what a race's skeleton looks like without a character of that race on screen.
/// <para/>
/// The tagfile is a self-describing stream: class layouts, each before the first object that needs it, and the
/// objects one after another, numbered from 1 in the order they come. An object refers to another by that number.
/// Nothing in an object can be stepped over without knowing its type, so every one is walked; only what a skeleton
/// needs is kept.
/// <para/>
/// The game writes version 3 of the format; Havok 2014 tools, which skeleton mods are made with, version 5, which only
/// adds the SDK's name and a short block after it. Files come from any mod a player has installed, so the reader
/// trusts nothing it reads: nesting is capped, every count is checked against the bytes left before anything is
/// allocated for it, and a file it cannot make sense of gives null rather than an exception.
/// </summary>
public sealed class HavokSkeleton
{
    /// <summary>Bone names, in the skeleton's own order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Each bone's parent, as an index into <see cref="Names"/>; -1 at a root.</summary>
    public IReadOnlyList<int> Parents { get; }

    private HavokSkeleton(string[] names, int[] parents)
    {
        Names = names;
        Parents = parents;
    }

    /// <summary>Each bone's parent's NAME, null at a root — the lookup <see cref="PbdFile.Resolve"/> walks.</summary>
    public Dictionary<string, string?> ParentMap()
    {
        var map = new Dictionary<string, string?>(Names.Count, StringComparer.Ordinal);
        for (int i = 0; i < Names.Count; i++)
            map.TryAdd(Names[i], Parents[i] >= 0 ? Names[Parents[i]] : null);
        return map;
    }

    private static readonly byte[] Magic = [0x1E, 0x0D, 0xB0, 0xCA, 0xCE, 0xFA, 0x11, 0xD0];

    /// <summary>
    /// The skeleton of a <c>.sklb</c> (or of a bare tagfile): the one its animation container names first — the
    /// file may hold others, such as the two a skeleton mapper maps between — else the largest. Null when the file
    /// is not a tagfile this reader understands, or its hierarchy is not a tree.
    /// </summary>
    public static HavokSkeleton? Read(ReadOnlySpan<byte> file) => Read(file, out _);

    /// <inheritdoc cref="Read(ReadOnlySpan{byte})"/>
    /// <param name="why">Why it was turned down, for the log; empty when it was not.</param>
    public static HavokSkeleton? Read(ReadOnlySpan<byte> file, out string why)
    {
        why = "";
        int start = file.IndexOf(Magic);
        if (start < 0)
        {
            why = "no Havok tagfile in it";
            return null;
        }
        var reader = new Reader(file[(start + Magic.Length)..].ToArray());
        try
        {
            reader.ReadAll();
        }
        catch (Bad bad)
        {
            why = $"unreadable at byte {start + Magic.Length + reader.At}: {bad.Message}";
            return null;
        }
        var skeleton = Pick(reader.Objects);
        if (skeleton == null) why = $"no bone tree among its {reader.Objects.Count} objects";
        return skeleton;
    }

    private static HavokSkeleton? Pick(List<Obj> objects)
    {
        Obj? ById(object? value) => value is long id && id >= 1 && id <= objects.Count ? objects[(int)id - 1] : null;

        foreach (var o in objects)
            if (o.Class.Name == "hkaAnimationContainer" && o.Get("skeletons") is List<long> { Count: > 0 } listed
                && ById(listed[0]) is { Class.Name: "hkaSkeleton" } first)
                return From(first);
        return objects.Where(o => o.Class.Name == "hkaSkeleton").Select(From).Where(s => s != null)
                      .MaxBy(s => s!.Names.Count);
    }

    private static HavokSkeleton? From(Obj skeleton)
    {
        if (skeleton.Get("parentIndices") is not List<long> parents) return null;
        if (skeleton.Get("bones") is not List<Obj> bones || bones.Count != parents.Count || bones.Count == 0) return null;

        var names = new string[bones.Count];
        var indices = new int[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].Get("name") is not string { Length: > 0 } name) return null;
            long p = parents[i];
            // A tree as Havok stores one: every parent before its child, so no walk up it can loop.
            if (p < -1 || p >= i) return null;
            names[i] = name;
            indices[i] = (int)p;
        }
        return new HavokSkeleton(names, indices);
    }

    /// <summary>A file the reader cannot make sense of; caught at the top and turned into null.</summary>
    private sealed class Bad(string what) : Exception(what);

    private sealed record Member(string Name, int Type, int Tuple, Class? Class);

    private sealed class Class(string name)
    {
        public string Name { get; } = name;
        public List<Member> Members { get; } = [];   // the parent's first, as the stream lists them
    }

    private sealed class Obj(Class klass)
    {
        public Class Class { get; } = klass;
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
        public object? Get(string member) => Values.TryGetValue(member, out var v) ? v : null;
    }

    /// <summary>
    /// The tagfile stream. Tags and integers are variable-length: the low bit of the first byte is the sign, its
    /// next six bits the low value bits, and its top bit says another byte follows with seven more.
    /// </summary>
    private sealed class Reader(byte[] data)
    {
        // Member types: a base type, flagged as an array or as a fixed-size tuple of it.
        private const int TypeVoid = 0, TypeByte = 1, TypeInt = 2, TypeReal = 3, TypeVec4 = 4, TypeVec8 = 5,
                          TypeVec12 = 6, TypeVec16 = 7, TypeObject = 8, TypeStruct = 9, TypeString = 10;
        private const int FlagArray = 0x10, FlagTuple = 0x20, BaseMask = 0x0F;

        private const int TagFileInfo = 1, TagMetadata = 2, TagObject = 3, TagRemember = 4, TagEnd = 7;

        /// <summary>Structs nest inside structs; no skeleton file comes near this, and it keeps the stack bounded.</summary>
        private const int MaxDepth = 32;

        /// <summary>No array in a skeleton file comes near this many elements; a count past it is damage.</summary>
        private const int MaxCount = 1 << 20;

        private int at;
        private int depth;
        private readonly List<string?> strings = ["", null];
        private readonly List<Class?> classes = [null];
        private readonly Dictionary<string, Class> byName = new(StringComparer.Ordinal);

        public int At => at;
        public List<Obj> Objects { get; } = [];

        public void ReadAll()
        {
            while (true)
            {
                long tag = ReadInt();
                switch (tag)
                {
                    case TagFileInfo:
                        long version = ReadInt();
                        if (version is < 1 or > 5) throw new Bad($"version {version}");
                        if (version < 4) break;
                        ReadString();   // the SDK that wrote it
                        // Then a block every file seen so far spells the same six bytes (15 00 01 00 14 00), its
                        // layout unknown: stepped over to the first class layout, which always comes next.
                        for (int guard = 0; at < data.Length && data[at] != TagMetadata << 1; guard++)
                            if (guard > 32) throw new Bad("no class layout after the file info");
                            else at++;
                        break;
                    case TagMetadata:
                        ReadClass();
                        break;
                    case TagObject or TagRemember:
                        long ci = ReadInt();
                        if (ci <= 0 || ci >= classes.Count || classes[(int)ci] is not { } klass)
                            throw new Bad($"class {ci} of {classes.Count}");
                        var obj = new Obj(klass);
                        ReadMembers(obj);
                        Objects.Add(obj);
                        break;
                    case TagEnd:
                        return;
                    default:
                        throw new Bad($"tag {tag}");
                }
            }
        }

        private void ReadClass()
        {
            string className = ReadString() ?? throw new Bad("a class without a name");
            // A member may name a class before its layout comes; that member already holds this very instance.
            var klass = byName.TryGetValue(className, out var named) && named.Members.Count == 0 && !classes.Contains(named)
                ? named
                : byName[className] = new Class(className);
            ReadInt();   // the class version
            long parent = ReadInt();
            if (parent < 0 || parent >= classes.Count) throw new Bad($"parent class {parent}");
            if (classes[(int)parent] is { } p) klass.Members.AddRange(p.Members);

            long count = ReadInt();
            if (count < 0 || count > Left) throw new Bad($"{count} members");
            for (int i = 0; i < count; i++)
            {
                string name = ReadString() ?? throw new Bad("a member without a name");
                int type = (int)ReadInt();
                int tuple = (type & FlagTuple) != 0 ? Count() : 0;
                Class? of = null;
                if ((type & BaseMask) is TypeObject or TypeStruct)
                {
                    string ofName = ReadString() ?? throw new Bad("a member of no class");
                    if (!byName.TryGetValue(ofName, out of)) byName[ofName] = of = new Class(ofName);
                }
                klass.Members.Add(new Member(name, type, tuple, of));
            }
            classes.Add(klass);
        }

        private void ReadMembers(Obj obj)
        {
            if (++depth > MaxDepth) throw new Bad("nested too deep");
            var members = obj.Class.Members;
            var present = ReadBitfield(members.Count);
            for (int i = 0; i < members.Count; i++)
                if (present[i]) obj.Values[members[i].Name] = ReadValue(members[i]);
            depth--;
        }

        private bool[] ReadBitfield(int count)
        {
            int bytes = (count + 7) / 8;
            if (bytes > Left) throw new Bad("cut short");
            var bits = new bool[count];
            for (int i = 0; i < count; i++) bits[i] = (data[at + i / 8] & (1 << (i % 8))) != 0;
            at += bytes;
            return bits;
        }

        private object? ReadValue(Member m)
        {
            int baseType = m.Type & BaseMask;
            if ((m.Type & FlagArray) != 0) return ReadArray(baseType, Count(), m.Class);
            if ((m.Type & FlagTuple) != 0) return ReadArray(baseType, m.Tuple, m.Class);
            return ReadOne(baseType, m.Class);
        }

        /// <summary>One value; an object is its number, which <see cref="Pick"/> looks up once all are read.</summary>
        private object? ReadOne(int baseType, Class? klass)
        {
            switch (baseType)
            {
                case TypeVoid: return null;
                case TypeByte: Skip(1); return null;
                case TypeInt or TypeObject: return ReadInt();
                case TypeReal: Skip(4); return null;
                case TypeVec4: Skip(16); return null;
                case TypeVec8: Skip(32); return null;
                case TypeVec12: Skip(48); return null;
                case TypeVec16: Skip(64); return null;
                case TypeString: return ReadString();
                case TypeStruct:
                    var obj = new Obj(klass ?? throw new Bad("a struct of no class"));
                    ReadMembers(obj);
                    return obj;
                default: throw new Bad($"member type {baseType}");
            }
        }

        private object? ReadArray(int baseType, int count, Class? klass)
        {
            switch (baseType)
            {
                case TypeVoid: return null;
                case TypeByte: Skip(count); return null;
                case TypeReal: Skip(count * 4L); return null;
                case TypeVec4: Skip(count * 16L); return null;
                case TypeVec8: Skip(count * 32L); return null;
                case TypeVec12: Skip(count * 48L); return null;
                case TypeVec16: Skip(count * 64L); return null;
                case TypeInt or TypeObject:
                {
                    if (count == 0) return new List<long>();
                    // Ints are preceded by the width they were stored at; each is still written variable-length.
                    if (baseType == TypeInt) ReadInt();
                    if (count > Left) throw new Bad($"{count} ints");
                    var ints = new List<long>(count);
                    for (int i = 0; i < count; i++) ints.Add(ReadInt());
                    return ints;
                }
                case TypeString:
                {
                    if (count > Left) throw new Bad($"{count} strings");
                    var list = new List<string?>(count);
                    for (int i = 0; i < count; i++) list.Add(ReadString());
                    return list;
                }
                case TypeStruct:
                    return ReadStructArray(count, klass ?? throw new Bad("structs of no class"));
                default:
                    throw new Bad($"array of type {baseType}");
            }
        }

        /// <summary>
        /// An array of structs, stored column by column: which members any element carries, then each such member's
        /// values for every element in turn.
        /// </summary>
        private List<Obj> ReadStructArray(int count, Class klass)
        {
            if (++depth > MaxDepth) throw new Bad("nested too deep");
            var members = klass.Members;
            var present = ReadBitfield(members.Count);
            // Every present column takes at least a byte an element; with none, only the cap bounds the count.
            if (count > MaxCount || (count > Left && present.Any(p => p))) throw new Bad($"{count} structs");

            var items = new List<Obj>(count);
            for (int i = 0; i < count; i++) items.Add(new Obj(klass));
            for (int k = 0; k < members.Count; k++)
            {
                if (!present[k]) continue;
                var m = members[k];
                if ((m.Type & (FlagArray | FlagTuple)) != 0)
                {
                    // An array member: each element's own array, one after another.
                    for (int i = 0; i < count; i++) items[i].Values[m.Name] = ReadValue(m);
                    continue;
                }
                // Anything else is a column written as an array of its type — ints with their width first, structs
                // column by column again.
                var column = ReadArray(m.Type & BaseMask, count, m.Class) as System.Collections.IList;
                for (int i = 0; i < count; i++) items[i].Values[m.Name] = column?[i];
            }
            depth--;
            return items;
        }

        private int Left => data.Length - at;

        private int Count()
        {
            long n = ReadInt();
            if (n < 0 || n > MaxCount) throw new Bad($"count {n}");
            return (int)n;
        }

        private void Skip(long bytes)
        {
            if (bytes < 0 || bytes > Left) throw new Bad("cut short");
            at += (int)bytes;
        }

        private long ReadInt()
        {
            if (at >= data.Length) throw new Bad("cut short");
            byte b = data[at++];
            bool negative = (b & 1) != 0;
            ulong value = (ulong)((b >> 1) & 0x3F);
            int shift = 6;
            while ((b & 0x80) != 0)
            {
                if (at >= data.Length || shift > 62) throw new Bad("an int too long");
                b = data[at++];
                value |= (ulong)(b & 0x7F) << shift;
                shift += 7;
            }
            long v = (long)(value & long.MaxValue);
            return negative ? -v : v;
        }

        /// <summary>A string: a positive length is a new one, which joins the table; otherwise an index into it.</summary>
        private string? ReadString()
        {
            long n = ReadInt();
            if (n > 0)
            {
                if (n > Left) throw new Bad("cut short");
                string s = Encoding.UTF8.GetString(data, at, (int)n);
                at += (int)n;
                strings.Add(s);
                return s;
            }
            long index = -n;
            if (index >= strings.Count) throw new Bad($"string {index} of {strings.Count}");
            return strings[(int)index];
        }
    }
}
