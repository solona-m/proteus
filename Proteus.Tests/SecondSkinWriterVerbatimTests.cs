using System;
using System.IO;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Structural validation for the verbatim second-skin writer: builds a shell from a REAL body model
/// and re-parses the output to confirm each mesh's declared stream strides match its vertex declaration
/// (position/normal/uv/blend all fit), so the model is at least self-consistent before an in-game test.
/// Skipped automatically when the local Neolithe model isn't present.
/// </summary>
public class SecondSkinWriterVerbatimTests
{
    private const string NeoTop =
        @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT CHEST - SmallClothes\0201e0000_top.mdl";
    private const string BiboTop =
        @"E:\Penumbradt\Bibo+\Breasts - Small Clothes\Nude - Large\chara\equipment\e0000\model\c0201e0000_top.mdl";
    private const string HostRing =
        @"E:\Penumbradt\classic gold\classic gold accessories\rings\chara\accessory\a0001\model\c0201a0001_rir.mdl";

    [Fact]
    public void Verbatim_output_is_structurally_consistent()
    {
        if (!File.Exists(NeoTop)) return;   // model not available on this machine — nothing to check

        var body = File.ReadAllBytes(NeoTop);
        var layers = new[]
        {
            new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null },
        };

        var outBytes = SecondSkinWriter.Build(new[] { body }, layers, out var stats);
        Assert.True(outBytes.Length > 0);
        Assert.True(stats.Meshes > 0);

        // Re-parse the output and check every mesh: for each vertex-declaration element, offset + size
        // must fit within that stream's declared stride. A mismatch means a bad stride/decl pairing.
        Validate(outBytes);
    }

    [Fact]
    public void Merged_heterogeneous_bodies_stay_consistent()
    {
        // Neolithe (ushort4 blend, stride 28) merged with Bibo (ubyte4 blend, stride 20): each mesh must
        // keep its OWN declaration/stride in the output. Validates the merge across mixed vertex formats.
        if (!File.Exists(NeoTop) || !File.Exists(BiboTop)) return;

        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };
        var outBytes = SecondSkinWriter.Build(
            new[] { File.ReadAllBytes(NeoTop), File.ReadAllBytes(BiboTop) }, layers, out var stats);

        Assert.True(stats.Meshes >= 2);
        Validate(outBytes);
    }

    [Fact]
    public void Appended_host_ring_keeps_its_materials_and_meshes()
    {
        // Append the shell INTO an equipped ring: the ring's own materials/meshes must survive at the FRONT
        // (so the accessory still renders) and the shell's material is added after them.
        if (!File.Exists(NeoTop) || !File.Exists(HostRing)) return;

        var body = File.ReadAllBytes(NeoTop);
        var ring = File.ReadAllBytes(HostRing);
        var ringMats = SecondSkinWriter.MaterialNames(ring);

        var layers = new[]
        {
            new SecondSkinLayer { MaterialName = "/mt_c0201a0001_rir_b.mtrl", Coverage = null },
        };

        // Same shell WITHOUT the host, so the mesh delta is exactly the host's kept meshes.
        SecondSkinWriter.Build(new[] { body }, layers, out var shellOnly);
        var outBytes = SecondSkinWriter.Build(new[] { body }, layers, ring, out var stats);

        // Materials: the ring's, then ours.
        var outMats = SecondSkinWriter.MaterialNames(outBytes);
        Assert.Equal(ringMats.Count + layers.Length, outMats.Count);
        Assert.Equal("/mt_c0201a0001_rir_b.mtrl", outMats[^1]);
        for (int i = 0; i < ringMats.Count; i++)
            Assert.Equal(ringMats[i], outMats[i]);

        // Meshes: the shell's, plus the host's own (at least one).
        Assert.True(stats.Meshes > shellOnly.Meshes, "host added no meshes");

        Validate(outBytes);
    }

    private static void Validate(byte[] m)
    {
        ushort U16(int o) => BitConverter.ToUInt16(m, o);
        uint U32(int o) => BitConverter.ToUInt32(m, o);

        int declCount = U16(12);
        int declEnd = 0x44 + declCount * 17 * 8;
        int strSize = (int)U32(declEnd + 4);
        int mh = declEnd + 8 + strSize;
        int meshCount = U16(mh + 4);
        int elemCount = U16(mh + 24);
        byte flags2 = m[mh + 27];
        int lodStart = mh + 56 + elemCount * 32;
        int meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);

        Assert.Equal(declCount, meshCount);   // one declaration per mesh

        int TypeSize(byte t) => t switch
        {
            0 => 4, 1 => 8, 2 => 12, 3 => 16, 5 => 4, 6 => 4, 7 => 8, 8 => 4,
            9 => 4, 10 => 8, 13 => 4, 14 => 8, 16 => 4, 17 => 8, _ => 0,
        };

        for (int mi = 0; mi < meshCount; mi++)
        {
            int mo = meshStart + mi * 36;
            byte[] strides = { m[mo + 32], m[mo + 33], m[mo + 34] };
            int db = 0x44 + mi * 17 * 8;
            for (int e = 0; e < 17; e++)
            {
                int o = db + e * 8;
                if (m[o] == 0xFF) break;
                byte stream = m[o], off = m[o + 1], type = m[o + 2];
                Assert.True(stream < 3, $"mesh {mi} elem {e}: stream {stream} out of range");
                int end = off + TypeSize(type);
                Assert.True(end <= strides[stream],
                    $"mesh {mi} elem {e}: element end {end} exceeds stream {stream} stride {strides[stream]}");
            }
        }
    }
}
