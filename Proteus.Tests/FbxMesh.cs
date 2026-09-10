using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Proteus.Tests;

/// <summary>
/// Just enough binary FBX to get one mesh's triangles out of a TexTools export.
/// <para/>
/// Test-only, and deliberately minimal: this exists so a hat exported from the game can be used as an
/// OFFLINE ORACLE — "does the smushed hair still fit inside a real hat" — without shipping any of that
/// geometry. Nothing derived from an SE model is checked in or published; the file stays on the machine
/// that owns the game.
/// <para/>
/// Reads FBX 7500+ only (64-bit node offsets), which is what TexTools writes. Nothing here handles
/// materials, skinning, or the scene graph — a hat is one mesh and that is all this is asked for.
/// </summary>
internal static class FbxMesh
{
    /// <summary>Vertex positions and triangle corner indices, in the file's own coordinates and units.</summary>
    internal sealed record Mesh(Vector3[] Positions, int[] Triangles);

    /// <summary>
    /// The first <c>Geometry</c> node's mesh, or null if the file has none.
    /// <para/>
    /// FBX stores polygons as a flat corner list where the LAST corner of each face is written with its
    /// bits inverted (<c>~i</c>) to mark the end — so faces are found by that marker, not by a stride, and
    /// an n-gon is fanned into triangles.
    /// </summary>
    internal static Mesh? Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 27 || Encoding.ASCII.GetString(bytes, 0, 20) != "Kaydara FBX Binary  ")
            return null;
        uint version = BitConverter.ToUInt32(bytes, 23);
        if (version < 7500) return null;   // 32-bit offsets, a different record header

        double[]? verts = null;
        int[]? idx = null;
        Walk(bytes, 27, bytes.Length, (name, props) =>
        {
            if (verts == null && name == "Vertices" && props.Count > 0 && props[0] is double[] d) verts = d;
            else if (idx == null && name == "PolygonVertexIndex" && props.Count > 0 && props[0] is int[] i) idx = i;
            return verts == null || idx == null;
        });
        if (verts == null || idx == null) return null;

        var positions = new Vector3[verts.Length / 3];
        for (int i = 0; i < positions.Length; i++)
            positions[i] = new Vector3((float)verts[i * 3], (float)verts[i * 3 + 1], (float)verts[i * 3 + 2]);

        var tris = new List<int>(idx.Length);
        var face = new List<int>(4);
        foreach (var raw in idx)
        {
            bool last = raw < 0;
            face.Add(last ? ~raw : raw);
            if (!last) continue;
            for (int k = 2; k < face.Count; k++) { tris.Add(face[0]); tris.Add(face[k - 1]); tris.Add(face[k]); }
            face.Clear();
        }
        return new Mesh(positions, tris.ToArray());
    }

    /// <summary>Walk sibling records in [pos, end), depth first. The visitor returns false to stop.</summary>
    private static bool Walk(byte[] b, int pos, int end, Func<string, List<object>, bool> visit)
    {
        while (pos + 25 <= end)
        {
            long endOffset = BitConverter.ToInt64(b, pos);
            long propCount = BitConverter.ToInt64(b, pos + 8);
            long propLen = BitConverter.ToInt64(b, pos + 16);
            int nameLen = b[pos + 24];
            if (endOffset == 0) return true;                      // the null record ends a sibling list
            if (endOffset > end || endOffset <= pos) return true;  // malformed; stop rather than loop

            int p = pos + 25;
            var name = Encoding.ASCII.GetString(b, p, nameLen);
            p += nameLen;

            var props = new List<object>((int)propCount);
            int propEnd = p + (int)propLen;
            for (long i = 0; i < propCount && p < propEnd; i++)
                props.Add(ReadProp(b, ref p));

            if (!visit(name, props)) return false;
            if (propEnd < endOffset && !Walk(b, propEnd, (int)endOffset, visit)) return false;
            pos = (int)endOffset;
        }
        return true;
    }

    private static object ReadProp(byte[] b, ref int p)
    {
        char t = (char)b[p++];
        switch (t)
        {
            case 'Y': { var v = BitConverter.ToInt16(b, p); p += 2; return v; }
            case 'C': return b[p++] != 0;
            case 'I': { var v = BitConverter.ToInt32(b, p); p += 4; return v; }
            case 'F': { var v = BitConverter.ToSingle(b, p); p += 4; return v; }
            case 'D': { var v = BitConverter.ToDouble(b, p); p += 8; return v; }
            case 'L': { var v = BitConverter.ToInt64(b, p); p += 8; return v; }
            case 'S':
            case 'R': { int n = BitConverter.ToInt32(b, p); p += 4 + n; return string.Empty; }
            case 'f': return ReadArray(b, ref p, 4, (s, n) => { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = BitConverter.ToSingle(s, i * 4); return a; });
            case 'd': return ReadArray(b, ref p, 8, (s, n) => { var a = new double[n]; for (int i = 0; i < n; i++) a[i] = BitConverter.ToDouble(s, i * 8); return a; });
            case 'i': return ReadArray(b, ref p, 4, (s, n) => { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = BitConverter.ToInt32(s, i * 4); return a; });
            case 'l': return ReadArray(b, ref p, 8, (s, n) => { var a = new long[n]; for (int i = 0; i < n; i++) a[i] = BitConverter.ToInt64(s, i * 8); return a; });
            case 'b': return ReadArray(b, ref p, 1, (s, n) => { var a = new bool[n]; for (int i = 0; i < n; i++) a[i] = s[i] != 0; return a; });
            default: throw new InvalidDataException($"unknown FBX property type '{t}'");
        }
    }

    /// <summary>An array property, inflated when the file stored it deflated (encoding 1).</summary>
    private static object ReadArray(byte[] b, ref int p, int elemSize, Func<byte[], int, object> build)
    {
        int count = BitConverter.ToInt32(b, p);
        int encoding = BitConverter.ToInt32(b, p + 4);
        int compressed = BitConverter.ToInt32(b, p + 8);
        p += 12;

        byte[] raw;
        if (encoding == 0)
        {
            raw = new byte[count * elemSize];
            Array.Copy(b, p, raw, 0, raw.Length);
            p += raw.Length;
        }
        else
        {
            using var src = new MemoryStream(b, p, compressed);
            using var z = new ZLibStream(src, CompressionMode.Decompress);
            raw = new byte[count * elemSize];
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n <= 0) break;
                read += n;
            }
            p += compressed;
        }
        return build(raw, count);
    }
}
