using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Proteus.Tests;

/// <summary>
/// The one mesh of a binary FBX (7.x) — what the featureless bulge's generator needs from the owner's export, and
/// nothing more: positions in body space (metres, the model's translation and X rotations applied), triangles, a uv per
/// triangle corner, and each vertex's bone weights by name. Test-only: the plugin never reads FBX.
/// </summary>
internal sealed class SculptFbx
{
    public required Vector3[] Positions { get; init; }
    public required int[] Triangles { get; init; }
    public required Vector2[] CornerUv { get; init; }
    public required Dictionary<string, float>[] Weights { get; init; }

    private sealed record Node(string Name, List<object?> Props, List<Node> Children)
    {
        public IEnumerable<Node> Kids(string name) => Children.Where(c => c.Name == name);
    }

    public static SculptFbx Read(string path)
    {
        var buf = File.ReadAllBytes(path);
        if (Encoding.ASCII.GetString(buf, 0, 20) != "Kaydara FBX Binary  ") throw new InvalidDataException("not a binary FBX");
        uint version = BitConverter.ToUInt32(buf, 23);
        bool big = version >= 7500;
        int pos = 27;
        var top = new List<Node>();
        while (pos < buf.Length - 160)
        {
            var n = ReadNode(buf, ref pos, big);
            if (n == null) break;
            top.Add(n);
        }
        var objects = top.Single(n => n.Name == "Objects").Children;
        var conns = top.Single(n => n.Name == "Connections").Children;
        var byId = objects.ToDictionary(o => (long)o.Props[0]!);
        var parentsOf = new Dictionary<long, List<long>>();
        var childrenOf = new Dictionary<long, List<long>>();
        foreach (var c in conns.Where(c => (string)c.Props[0]! == "OO"))
        {
            long a = (long)c.Props[1]!, b = (long)c.Props[2]!;
            (parentsOf.TryGetValue(a, out var pl) ? pl : parentsOf[a] = []).Add(b);
            (childrenOf.TryGetValue(b, out var cl) ? cl : childrenOf[b] = []).Add(a);
        }
        static string NameOf(Node o) => ((string)o.Props[1]!).Split('\0')[0];
        static double[] P70(Node o, string key, double[] fallback)
        {
            foreach (var blk in o.Kids("Properties70"))
                foreach (var p in blk.Children)
                    if ((string)p.Props[0]! == key) return [Convert.ToDouble(p.Props[4]), Convert.ToDouble(p.Props[5]), Convert.ToDouble(p.Props[6])];
            return fallback;
        }

        var geom = objects.Single(o => o.Name == "Geometry");
        var model = parentsOf[(long)geom.Props[0]!].Select(id => byId.GetValueOrDefault(id)).Single(o => o?.Name == "Model")!;
        var t = P70(model, "Lcl Translation", [0, 0, 0]);
        var r = P70(model, "Lcl Rotation", [0, 0, 0]);
        var pre = P70(model, "PreRotation", [0, 0, 0]);
        var s = P70(model, "Lcl Scaling", [1, 1, 1]);
        if (Math.Abs(r[1]) > 1e-6 || Math.Abs(r[2]) > 1e-6 || Math.Abs(pre[1]) > 1e-6 || Math.Abs(pre[2]) > 1e-6)
            throw new InvalidDataException("only rotations about X are read");
        double ang = (pre[0] + r[0]) * Math.PI / 180, cs = Math.Cos(ang), sn = Math.Sin(ang);

        var v = (double[])geom.Kids("Vertices").Single().Props[0]!;
        var positions = new Vector3[v.Length / 3];
        for (int i = 0; i < positions.Length; i++)
        {
            double x = v[i * 3] * s[0], y = v[i * 3 + 1] * s[1], z = v[i * 3 + 2] * s[2];
            (y, z) = (y * cs - z * sn, y * sn + z * cs);
            positions[i] = new Vector3((float)((x + t[0]) / 100), (float)((y + t[1]) / 100), (float)((z + t[2]) / 100));
        }
        var pvi = (int[])geom.Kids("PolygonVertexIndex").Single().Props[0]!;
        var uvl = geom.Kids("LayerElementUV").First();
        var uv = (double[])uvl.Kids("UV").Single().Props[0]!;
        var uvIdx = (int[])uvl.Kids("UVIndex").Single().Props[0]!;
        var tris = new List<int>();
        var cornerUv = new List<Vector2>();
        int run = 0;
        for (int k = 0; k < pvi.Length; k++)
        {
            int i = pvi[k] < 0 ? ~pvi[k] : pvi[k];
            tris.Add(i);
            cornerUv.Add(new Vector2((float)uv[uvIdx[k] * 2], (float)uv[uvIdx[k] * 2 + 1]));
            run++;
            if (pvi[k] < 0)
            {
                if (run != 3) throw new InvalidDataException("only triangles are read");
                run = 0;
            }
        }
        var weights = Enumerable.Range(0, positions.Length).Select(_ => new Dictionary<string, float>(StringComparer.Ordinal)).ToArray();
        foreach (var cl in objects.Where(o => o.Name == "Deformer" && (string)o.Props[2]! == "Cluster"))
        {
            var bone = childrenOf.GetValueOrDefault((long)cl.Props[0]!)?.Select(id => byId.GetValueOrDefault(id))
                                 .FirstOrDefault(o => o?.Name == "Model");
            if (bone == null) continue;
            var idx = cl.Kids("Indexes").FirstOrDefault()?.Props[0] as int[] ?? [];
            var w = cl.Kids("Weights").FirstOrDefault()?.Props[0] as double[] ?? [];
            for (int k = 0; k < Math.Min(idx.Length, w.Length); k++)
                weights[idx[k]][NameOf(bone)] = weights[idx[k]].GetValueOrDefault(NameOf(bone)) + (float)w[k];
        }
        return new SculptFbx { Positions = positions, Triangles = [.. tris], CornerUv = [.. cornerUv], Weights = weights };
    }

    private static Node? ReadNode(byte[] b, ref int pos, bool big)
    {
        long end, nprops;
        if (big) { end = BitConverter.ToInt64(b, pos); nprops = BitConverter.ToInt64(b, pos + 8); pos += 24; }
        else { end = BitConverter.ToUInt32(b, pos); nprops = BitConverter.ToUInt32(b, pos + 4); pos += 12; }
        int nlen = b[pos++];
        if (end == 0) return null;
        string name = Encoding.ASCII.GetString(b, pos, nlen);
        pos += nlen;
        var props = new List<object?>();
        for (long i = 0; i < nprops; i++) props.Add(ReadProp(b, ref pos));
        var kids = new List<Node>();
        while (pos < end)
        {
            var k = ReadNode(b, ref pos, big);
            if (k == null) break;
            kids.Add(k);
        }
        pos = (int)end;
        return new Node(name, props, kids);
    }

    private static object? ReadProp(byte[] b, ref int pos)
    {
        char code = (char)b[pos++];
        switch (code)
        {
            case 'Y': pos += 2; return (long)BitConverter.ToInt16(b, pos - 2);
            case 'C': pos += 1; return (long)b[pos - 1];
            case 'I': pos += 4; return (long)BitConverter.ToInt32(b, pos - 4);
            case 'F': pos += 4; return (double)BitConverter.ToSingle(b, pos - 4);
            case 'D': pos += 8; return BitConverter.ToDouble(b, pos - 8);
            case 'L': pos += 8; return BitConverter.ToInt64(b, pos - 8);
            case 'S': case 'R':
            {
                int n = BitConverter.ToInt32(b, pos);
                pos += 4;
                var raw = b.AsSpan(pos, n).ToArray();
                pos += n;
                return code == 'S' ? Encoding.UTF8.GetString(raw) : raw;
            }
            case 'f': case 'd': case 'l': case 'i': case 'b':
            {
                int n = BitConverter.ToInt32(b, pos), enc = BitConverter.ToInt32(b, pos + 4), clen = BitConverter.ToInt32(b, pos + 8);
                pos += 12;
                var data = b.AsSpan(pos, clen).ToArray();
                pos += clen;
                if (enc == 1)
                {
                    using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                    using var m = new MemoryStream();
                    z.CopyTo(m);
                    data = m.ToArray();
                }
                return code switch
                {
                    'f' => Enumerable.Range(0, n).Select(i => (double)BitConverter.ToSingle(data, i * 4)).ToArray(),
                    'd' => Enumerable.Range(0, n).Select(i => BitConverter.ToDouble(data, i * 8)).ToArray(),
                    'l' => Enumerable.Range(0, n).Select(i => BitConverter.ToInt64(data, i * 8)).ToArray(),
                    'i' => Enumerable.Range(0, n).Select(i => BitConverter.ToInt32(data, i * 4)).ToArray(),
                    _ => (object)data,
                };
            }
            default: throw new InvalidDataException($"FBX property type '{code}'");
        }
    }
}
