using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Proteus.Tests;

/// <summary>
/// Skeleton files and racial deformers built in memory, for tests that run without the game: a Havok binary tagfile
/// laid out the way the game's and the Havok 2014 tools' are, and a <c>human.pbd</c> with the races asked for.
/// </summary>
internal static class SyntheticSkeleton
{
    /// <summary>A bone tree: names, and each one's parent index (-1 at the root).</summary>
    internal sealed record Tree(string Name, string[] Bones, int[] Parents);

    /// <summary>
    /// A <c>.sklb</c>: a junk header, then a tagfile of <paramref name="version"/> holding a skeleton mapper over two
    /// decoy skeletons, then an animation container naming <paramref name="main"/> — the order the IVCS files have,
    /// where the decoys come first.
    /// </summary>
    /// <param name="withContainer">False leaves the container out, so the reader must pick by size.</param>
    internal static byte[] Sklb(Tree main, int version = 3, Tree? decoyA = null, Tree? decoyB = null,
                                bool withContainer = true)
    {
        var w = new Writer();
        w.Raw(Encoding.ASCII.GetBytes("blks0031"));
        w.Raw([0, 0, 0, 0, 0x10, 0, 0, 0]);
        w.Raw([0x1E, 0x0D, 0xB0, 0xCA, 0xCE, 0xFA, 0x11, 0xD0]);

        w.Int(1);
        w.Int(version);
        if (version >= 4)
        {
            w.Str("hk_2014.1.0-r1");
            w.Raw([0x15, 0x00, 0x01, 0x00, 0x14, 0x00]);
        }

        // Classes, numbered from 1 in the order they come.
        w.Class("hkBaseObject", 0);                                                        // 1
        w.Class("hkReferencedObject", 1, ("memSizeAndRefCount", 0, null));                 // 2
        w.Class("hkaBone", 0, ("name", 10, null), ("lockTranslation", 1, null));           // 3
        w.Class("hkaSkeleton", 2, ("name", 10, null), ("parentIndices", 0x12, null),
                ("bones", 0x19, "hkaBone"));                                               // 4
        w.Class("hkaAnimationContainer", 2, ("skeletons", 0x18, "hkaSkeleton"));           // 5
        w.Class("hkaSkeletonMapperData", 0, ("skeletonA", 8, "hkaSkeleton"),
                ("skeletonB", 8, "hkaSkeleton"));                                          // 6
        w.Class("hkaSkeletonMapper", 2, ("mapping", 9, "hkaSkeletonMapperData"));          // 7

        // Objects, numbered from 1: the mapper and its two skeletons, then the container and the main one.
        int next = 1;
        var a = decoyA ?? new Tree("decoyA", ["n_root", "a", "b", "c", "d", "e", "f", "g", "h", "i"], [-1, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
        var b = decoyB ?? new Tree("decoyB", ["n_root", "x"], [-1, 0]);
        int mapper = next++, idA = next++, idB = next++;
        w.Object(7, [0x02], () => { w.Raw([0x03]); w.Int(idA); w.Int(idB); });
        w.Skeleton(a);
        w.Skeleton(b);
        if (withContainer)
        {
            int container = next++, idMain = next++;
            w.Object(5, [0x02], () => { w.Int(1); w.Int(idMain); });
            _ = container;
        }
        w.Skeleton(main);
        _ = mapper;
        w.Int(7);
        return w.Bytes();
    }

    private sealed class Writer
    {
        private readonly List<byte> bytes = [];
        private readonly List<string?> strings = ["", null];
        private readonly List<string> classes = [""];

        public byte[] Bytes() => bytes.ToArray();

        public void Raw(byte[] b) => bytes.AddRange(b);

        /// <summary>Sign in the low bit, six value bits, then seven a byte while the top bit says more follow.</summary>
        public void Int(long value)
        {
            bool negative = value < 0;
            ulong u = (ulong)Math.Abs(value);
            byte first = (byte)(((u & 0x3F) << 1) | (negative ? 1u : 0u));
            u >>= 6;
            if (u > 0) first |= 0x80;
            bytes.Add(first);
            while (u > 0)
            {
                byte b = (byte)(u & 0x7F);
                u >>= 7;
                if (u > 0) b |= 0x80;
                bytes.Add(b);
            }
        }

        public void Str(string s)
        {
            int at = strings.IndexOf(s);
            if (at >= 0) { Int(-at); return; }
            var b = Encoding.UTF8.GetBytes(s);
            Int(b.Length);
            bytes.AddRange(b);
            strings.Add(s);
        }

        public void Class(string name, int parent, params (string Name, int Type, string? Of)[] members)
        {
            Int(2);
            Str(name);
            Int(0);
            Int(parent);
            Int(members.Length);
            foreach (var (member, type, of) in members)
            {
                Str(member);
                Int(type);
                if (of != null) Str(of);
            }
            classes.Add(name);
        }

        public void Object(int klass, byte[] bitfield, Action members)
        {
            Int(4);
            Int(klass);
            Raw(bitfield);
            members();
        }

        /// <summary>hkaSkeleton: four members with the inherited one; name, parents and bones present.</summary>
        public void Skeleton(Tree t)
            => Object(4, [0x0E], () =>
            {
                Str(t.Name);
                Int(t.Parents.Length);
                Int(4);
                foreach (int p in t.Parents) Int(p);
                Int(t.Bones.Length);
                Raw([0x01]);                       // of hkaBone, only the name
                foreach (string bone in t.Bones) Str(bone);
            });
    }

    /// <summary>
    /// A <c>human.pbd</c> with <paramref name="root"/> at the top of the tree and each of <paramref name="children"/>
    /// under it, each with a deformer that moves the bones named up by <paramref name="lift"/> metres.
    /// </summary>
    internal static byte[] Pbd(ushort root, IReadOnlyDictionary<ushort, string[]> children, float lift = 0.01f)
    {
        var races = new List<ushort> { root };
        races.AddRange(children.Keys);
        int count = races.Count;
        int headerAt = 4, linkAt = headerAt + count * 12, dataAt = linkAt + count * 8;

        var deformers = new List<byte[]>();
        var offsets = new int[count];
        int at = dataAt;
        for (int i = 0; i < count; i++)
        {
            if (i == 0) { offsets[i] = 0; continue; }
            var d = Deformer(children[races[i]], lift);
            offsets[i] = at;
            deformers.Add(d);
            at += d.Length;
        }

        var file = new List<byte>();
        file.AddRange(BitConverter.GetBytes(count));
        for (int i = 0; i < count; i++)
        {
            file.AddRange(BitConverter.GetBytes(races[i]));
            file.AddRange(BitConverter.GetBytes((ushort)i));
            file.AddRange(BitConverter.GetBytes(offsets[i]));
            file.AddRange(BitConverter.GetBytes(1f));   // the header's scale, which the reader steps over
        }
        for (int i = 0; i < count; i++)
        {
            file.AddRange(BitConverter.GetBytes(i == 0 ? ushort.MaxValue : (ushort)0));   // parent link
            file.AddRange(BitConverter.GetBytes(ushort.MaxValue));
            file.AddRange(BitConverter.GetBytes(ushort.MaxValue));
            file.AddRange(BitConverter.GetBytes((ushort)i));                              // header
        }
        foreach (var d in deformers) file.AddRange(d);
        return file.ToArray();
    }

    /// <summary>One deformer: bone count, name offsets, padding to four, a 3×4 matrix each, then the names.</summary>
    private static byte[] Deformer(string[] bones, float lift)
    {
        int n = bones.Length;
        int matrices = 4 + n * 2 + n * 2 % 4;
        int names = matrices + n * 48;
        var nameBytes = new List<byte>();
        var nameOffsets = new int[n];
        for (int i = 0; i < n; i++)
        {
            nameOffsets[i] = names + nameBytes.Count;
            nameBytes.AddRange(Encoding.UTF8.GetBytes(bones[i]));
            nameBytes.Add(0);
        }

        var d = new List<byte>();
        d.AddRange(BitConverter.GetBytes(n));
        foreach (int o in nameOffsets) d.AddRange(BitConverter.GetBytes((short)o));
        while (d.Count < matrices) d.Add(0);
        for (int i = 0; i < n; i++)
            foreach (float f in new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, lift, 0f, 0f, 1f, 0f })
                d.AddRange(BitConverter.GetBytes(f));
        d.AddRange(nameBytes);
        return d.ToArray();
    }

    /// <summary>A skinned mesh of one vertex per bone, each leaning wholly on its bone — what a weighted-bone check reads.</summary>
    internal static XivLiveMesh.SkinnedMesh Leaning(params string[] bones)
    {
        int n = bones.Length;
        var indices = new ushort[n * XivLiveMesh.SkinnedMesh.MaxInfluences];
        var weights = new float[n * XivLiveMesh.SkinnedMesh.MaxInfluences];
        for (int v = 0; v < n; v++)
        {
            indices[v * XivLiveMesh.SkinnedMesh.MaxInfluences] = (ushort)v;
            weights[v * XivLiveMesh.SkinnedMesh.MaxInfluences] = 1f;
        }
        return new XivLiveMesh.SkinnedMesh
        {
            Positions = new System.Numerics.Vector3[n],
            Triangles = [],
            BaseTriangles = [],
            MaterialNames = [],
            TriangleMaterials = [],
            BoneNames = bones,
            BoneIndices = indices,
            BoneWeights = weights,
        };
    }

    /// <summary>The game's base human skeleton, trimmed: the spine, a leg and an arm.</summary>
    internal static Tree Human(string name = "human", params (string Bone, string Parent)[] extra)
    {
        var bones = new List<string> { "n_root", "n_hara", "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi", "j_kao",
                                       "j_asi_a_l", "j_ude_a_l" };
        var parents = new List<int> { -1, 0, 1, 1, 3, 4, 5, 6, 2, 5 };
        foreach (var (bone, parent) in extra)
        {
            parents.Add(bones.IndexOf(parent));
            bones.Add(bone);
        }
        return new Tree(name, bones.ToArray(), parents.ToArray());
    }

    /// <summary>The bones a deformer lists for one race in these tests.</summary>
    internal static readonly string[] Listed = ["n_root", "n_hara", "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi"];

    /// <summary>A pbd with c0101 at the root and c0301 under it.</summary>
    internal static byte[] HighlanderPbd() => Pbd(101, new Dictionary<ushort, string[]> { [301] = Listed });
}
