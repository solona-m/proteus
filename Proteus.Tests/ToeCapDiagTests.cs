using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>Scratch diagnostics — runs the real toe-cap mask against the real bibo foot model.</summary>
public class ToeCapDiagTests
{
    private const string Sho =
        @"E:\Penumbradt\Bibo+\Feet\Small Clothes\chara\equipment\e0000\model\c0201e0000_sho.mdl";
    private const string Scratch =
        @"C:\Users\solon\AppData\Local\Temp\claude\e--repos-Proteus\c157041f-f61a-45b3-8a2b-72bc7dcbef80\scratchpad";

    private readonly ITestOutputHelper o;
    public ToeCapDiagTests(ITestOutputHelper o) => this.o = o;

    [Fact]
    public void Diagnose()
    {
        // A developer harness, not an assertion: it re-exports the shell as OBJ (positions, STORED
        // normals, UVs) so scratchpad/render.py can shade it the way the game does. Rendering from
        // recomputed face normals is what hid a cap whose normals were never rewritten.
        var maskPath = Path.Combine(Scratch, "toecap512.raw");
        if (!File.Exists(Sho) || !File.Exists(maskPath)) return;   // not this machine — nothing to dump

        var body = File.ReadAllBytes(Sho);
        var mask = File.ReadAllBytes(maskPath);

        SecondSkinLayer Layer(byte[]? cap) => new()
        {
            MaterialName = "/mt_c0201a0053_rir_a.mtrl",
            Coverage = null,
            ToeCap = cap,
            ToeCapWidth = cap == null ? 0 : 512,
            ToeCapHeight = cap == null ? 0 : 512,
            ToeCapStrength = 1f,
        };

        var lines = new List<string>();
        var plain  = SecondSkinWriter.Build(new[] { body }, new[] { Layer(null) }, null, false, out _);
        var capped = SecondSkinWriter.Build(new[] { body }, new[] { Layer(mask) }, null, false, out var stats,
            null, m => lines.Add(m));

        foreach (var l in lines) o.WriteLine(l);
        o.WriteLine($"stats: meshes={stats.Meshes} submeshes={stats.Submeshes} bones={stats.Bones} " +
                    $"triIn={stats.TrianglesIn} triOut={stats.TrianglesOut} verts={stats.VerticesOut}");

        WriteObj(plain,  Path.Combine(Scratch, "foot_plain.obj"));
        WriteObj(capped, Path.Combine(Scratch, "foot_capped.obj"));
        o.WriteLine("wrote objs");

        foreach (var l in Submeshes(body)) o.WriteLine(l);
    }

    /// <summary>Per-mesh submesh layout of a SOURCE model — a generated triangle has to fit in one.</summary>
    private static List<string> Submeshes(byte[] m)
    {
        ushort U16(int x) => BitConverter.ToUInt16(m, x);
        uint U32(int x) => BitConverter.ToUInt32(m, x);

        int declCount = U16(12);
        int declEnd = 0x44 + declCount * 17 * 8;
        int mh = declEnd + 8 + (int)U32(declEnd + 4);
        int meshCount = U16(mh + 4);
        int elemCount = U16(mh + 24);
        byte flags2 = m[mh + 27];
        int lodStart = mh + 56 + elemCount * 32;
        int meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);
        int subStart = meshStart + meshCount * 36;

        var outp = new List<string>();
        for (int mi = 0; mi < meshCount; mi++)
        {
            int mo = meshStart + mi * 36;
            ushort vc = U16(mo), si = U16(mo + 10), sc = U16(mo + 12);
            var parts = new List<string>();
            for (int s = 0; s < sc; s++)
            {
                int ss = subStart + (si + s) * 16;
                parts.Add($"[idx {U32(ss)}+{U32(ss + 4)} bones {U16(ss + 12)}+{U16(ss + 14)}]");
            }
            outp.Add($"mesh {mi}: {vc} verts, {sc} submesh(es) {string.Join(" ", parts)}");
        }
        return outp;
    }

    /// <summary>Parse a built shell model and dump its LOD0 geometry as a wavefront OBJ.</summary>
    private static void WriteObj(byte[] m, string path)
    {
        ushort U16(int x) => BitConverter.ToUInt16(m, x);
        uint U32(int x) => BitConverter.ToUInt32(m, x);

        int declCount = U16(12);
        int declEnd = 0x44 + declCount * 17 * 8;
        int strSize = (int)U32(declEnd + 4);
        int mh = declEnd + 8 + strSize;
        int meshCount = U16(mh + 4);
        int elemCount = U16(mh + 24);
        byte flags2 = m[mh + 27];
        int lodStart = mh + 56 + elemCount * 32;
        int meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);
        int subStart = meshStart + meshCount * 36;
        uint vtxOff = U32(16), idxOff = U32(28);

        // Grouped sections and fully-qualified face references. Interleaving v/vn/vt per vertex and
        // then emitting bare "f a b c" is legal OBJ and reads back fine, but 3ds Max makes nonsense of
        // it — it imported a clean cap as a handful of enormous fins. Every importer handles the
        // conventional layout, so write that.
        var vs = new StringBuilder();
        var ts = new StringBuilder();
        var ns = new StringBuilder();
        var fs = new StringBuilder();
        int baseVert = 1;
        for (int mi = 0; mi < meshCount; mi++)
        {
            int mo = meshStart + mi * 36;
            ushort vc = U16(mo);
            uint idxCount = U32(mo + 4);
            uint startIdx = U32(mo + 16);
            uint[] vOff = { U32(mo + 20), U32(mo + 24), U32(mo + 28) };
            byte[] str = { m[mo + 32], m[mo + 33], m[mo + 34] };

            int db = 0x44 + mi * 17 * 8;
            int pStream = -1, pOff = 0, pType = 0;
            int tStream = -1, tOff = 0, tType = 0;
            int nStream = -1, nOff = 0, nType = 0;
            for (int e = 0; e < 17; e++)
            {
                int x = db + e * 8;
                if (m[x] == 0xFF) break;
                if (m[x + 3] == 0 && pStream < 0) { pStream = m[x]; pOff = m[x + 1]; pType = m[x + 2]; }
                if (m[x + 3] == 3 && nStream < 0) { nStream = m[x]; nOff = m[x + 1]; nType = m[x + 2]; }
                if (m[x + 3] == 4 && m[x + 4] == 0 && tStream < 0) { tStream = m[x]; tOff = m[x + 1]; tType = m[x + 2]; }
            }
            if (pStream < 0) continue;

            for (int i = 0; i < vc; i++)
            {
                int a = (int)(vtxOff + vOff[pStream]) + i * str[pStream] + pOff;
                float x, y, z;
                if (pType == 14) { x = (float)BitConverter.ToHalf(m, a); y = (float)BitConverter.ToHalf(m, a + 2); z = (float)BitConverter.ToHalf(m, a + 4); }
                else { x = BitConverter.ToSingle(m, a); y = BitConverter.ToSingle(m, a + 4); z = BitConverter.ToSingle(m, a + 8); }
                vs.Append("v ").Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(F(z)).Append('\n');

                // The STORED normal — what the game shades with. Renders that recompute normals from
                // geometry are exactly how a shell full of stale normals looked correct offline.
                float nx = 0, ny = 0, nz = 0;
                if (nStream >= 0)
                {
                    int b = (int)(vtxOff + vOff[nStream]) + i * str[nStream] + nOff;
                    switch (nType)
                    {
                        case 2: case 3:
                            nx = BitConverter.ToSingle(m, b); ny = BitConverter.ToSingle(m, b + 4); nz = BitConverter.ToSingle(m, b + 8); break;
                        case 14:
                            nx = (float)BitConverter.ToHalf(m, b); ny = (float)BitConverter.ToHalf(m, b + 2); nz = (float)BitConverter.ToHalf(m, b + 4); break;
                        case 10:
                            nx = BitConverter.ToInt16(m, b) / 32767f; ny = BitConverter.ToInt16(m, b + 2) / 32767f; nz = BitConverter.ToInt16(m, b + 4) / 32767f; break;
                        case 8:
                            nx = m[b] / 255f * 2 - 1; ny = m[b + 1] / 255f * 2 - 1; nz = m[b + 2] / 255f * 2 - 1; break;
                    }
                }
                ns.Append("vn ").Append(F(nx)).Append(' ').Append(F(ny)).Append(' ').Append(F(nz)).Append('\n');

                float u = 0, v = 0;
                if (tStream >= 0)
                {
                    int b = (int)(vtxOff + vOff[tStream]) + i * str[tStream] + tOff;
                    if (tType is 13 or 14) { u = (float)BitConverter.ToHalf(m, b); v = (float)BitConverter.ToHalf(m, b + 2); }
                    else { u = BitConverter.ToSingle(m, b); v = BitConverter.ToSingle(m, b + 4); }
                }
                ts.Append("vt ").Append(F(u)).Append(' ').Append(F(v)).Append('\n');
            }

            for (uint t = 0; t + 2 < idxCount; t += 3)
            {
                int p = (int)(idxOff + (startIdx + t) * 2);
                int a = U16(p) + baseVert, b = U16(p + 2) + baseVert, c = U16(p + 4) + baseVert;
                fs.Append("f ")
                  .Append(a).Append('/').Append(a).Append('/').Append(a).Append(' ')
                  .Append(b).Append('/').Append(b).Append('/').Append(b).Append(' ')
                  .Append(c).Append('/').Append(c).Append('/').Append(c).Append('\n');
            }
            baseVert += vc;
        }
        File.WriteAllText(path, vs.Append(ts).Append(ns).Append(fs).ToString());
    }

    private static string F(float f) => f.ToString("0.######", CultureInfo.InvariantCulture);
}
