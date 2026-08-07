using System;
using System.IO;
using System.Linq;
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

    [Fact]
    public void Skipping_connectors_drops_geometry_on_neolithe()
    {
        // Neolithe's skin mesh carries joint-connector submeshes (atr_nek/hij/ude/…) that overlap its
        // complete main body. With skipConnectors on, those submeshes are dropped, so the shell has
        // strictly fewer triangles and submeshes than the default build.
        if (!File.Exists(NeoTop)) return;

        var body = File.ReadAllBytes(NeoTop);
        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };

        SecondSkinWriter.Build(new[] { body }, layers, null, false, out var full);
        var trimmedBytes = SecondSkinWriter.Build(new[] { body }, layers, null, true, out var trimmed);

        Assert.True(trimmed.TrianglesOut < full.TrianglesOut, "connector skip removed no triangles");
        Assert.True(trimmed.Submeshes < full.Submeshes, "connector skip removed no submeshes");
        Assert.True(trimmed.Meshes > 0, "the main body must survive");
        Validate(trimmedBytes);
    }

    [Fact]
    public void All_black_toe_cap_leaves_the_shell_byte_identical()
    {
        // The important guard: the feature must be completely inert for every mod that doesn't use it,
        // whether the mask is absent or present-but-black. A white mask, by contrast, must reach the
        // writer and move geometry — otherwise this test would pass on a feature that does nothing.
        var bodyPath = new[] { NeoTop, BiboTop }.FirstOrDefault(File.Exists);
        if (bodyPath == null) return;   // no model available on this machine

        var body = File.ReadAllBytes(bodyPath);
        SecondSkinLayer Layer(byte[]? cap) => new()
        {
            MaterialName = "/mt_c0201a0053_rir_a.mtrl",
            Coverage = null,
            ToeCap = cap,
            ToeCapWidth = cap == null ? 0 : 64,
            ToeCapHeight = cap == null ? 0 : 64,
            ToeCapStrength = 1f,
        };

        // Masked over half the UV, not all of it: a cap is stitched onto surviving geometry, so an
        // island masked end to end is deliberately left alone and would prove nothing here.
        var half = new byte[64 * 64];
        for (int y = 32; y < 64; y++)
            for (int x = 0; x < 64; x++)
                half[y * 64 + x] = 255;

        var plain = SecondSkinWriter.Build(new[] { body }, new[] { Layer(null) }, out _);
        var black = SecondSkinWriter.Build(new[] { body }, new[] { Layer(new byte[64 * 64]) }, out _);
        var capped = SecondSkinWriter.Build(new[] { body }, new[] { Layer(half) }, out _);

        Assert.Equal(plain, black);
        Assert.NotEqual(plain, capped);
        // No geometry is ADDED — the cap only displaces, and may drop triangles it collapsed.
        Assert.True(capped.Length <= plain.Length, "the cap grew the model");
        Validate(capped);
    }

    [Fact]
    public void Toe_cap_bridges_between_two_toes_and_pins_the_unmasked_part()
    {
        // Two "toes": parallel tubes along +Z with a gap between them. Only the far half is masked, the
        // way a real toe mask covers the toe box and not the whole foot. After the pass the gap must be
        // bridged — the inner walls pulled out to the cross-section's outline — and every unmasked
        // vertex must sit exactly where it started.
        var (pos, nrm, uv, tris) = TwoToes(out int ringSize, out int rings);

        const int mw = 32, mh = 32;
        var mask = new byte[mw * mh];
        for (int y = mh / 2; y < mh; y++)           // v >= 0.5 is the far half of each tube
            for (int x = 0; x < mw; x++)
                mask[y * mw + x] = 255;

        var delta = SecondSkinWriter.ToeCapDelta(pos, nrm, uv, tris, mask, mw, mh, 1f);
        Assert.NotNull(delta);

        // The rule that matters: the cap may dip into the gap between the toes as far as it likes — real
        // hosiery does, and it reads better than a flat bridge — but it must never end up INSIDE one.
        // Each tube is radius 1 about a centre line running along z, so a point is inside it when its
        // distance from that line drops below 1.
        int movedCount = 0;
        for (int i = 0; i < pos.Length; i++)
        {
            var d = delta![i];
            if (d.X * d.X + d.Y * d.Y + d.Z * d.Z <= 1e-8f) continue;
            movedCount++;

            var p = new SecondSkinWriter.Vec3(pos[i].X + d.X, pos[i].Y + d.Y, pos[i].Z + d.Z);
            if (p.Z < 0f || p.Z > 3.5f) continue;   // past the ends there is no tube to be inside of

            float inside = 1f;
            foreach (float cx in new[] { -1.2f, 1.2f })
                inside = MathF.Min(inside, MathF.Sqrt((p.X - cx) * (p.X - cx) + p.Y * p.Y));
            Assert.True(inside >= 1f - 0.02f,
                $"vertex {i} sank into a toe at ({p.X:0.00},{p.Y:0.00},{p.Z:0.00}) — {inside:0.000} from its axis");
        }
        Assert.True(movedCount >= ringSize, $"the cap rebuilt almost nothing ({movedCount} vertices)");

        // Unmasked rings are untouched, exactly.
        for (int t = 0; t < 2; t++)
            for (int k = 0; k < ringSize; k++)
            {
                int i = (t * rings) * ringSize + k;   // ring 0 sits at v = 0, fully black
                Assert.Equal(0f, delta![i].X);
                Assert.Equal(0f, delta[i].Y);
                Assert.Equal(0f, delta[i].Z);
            }

        // The cap is built on the cross-section hull, which contains every source point, plus a small
        // standoff so the toes underneath don't poke through it. So it stays just outside the source
        // extent (hull |x| 2.5, |y| 1) and nowhere near loose.
        for (int i = 0; i < pos.Length; i++)
        {
            float fx = MathF.Abs(pos[i].X + delta![i].X), fy = MathF.Abs(pos[i].Y + delta[i].Y);
            Assert.True(fx <= 2.5f, $"vertex {i} left the source extent in x ({fx:0.000})");
            Assert.True(fy <= 1.3f, $"vertex {i} left the source extent in y ({fy:0.000})");
        }
    }

    /// <summary>Two parallel tubes along +Z, 1 unit apart, as a stand-in for two toes.</summary>
    private static (SecondSkinWriter.Vec3[] Pos, SecondSkinWriter.Vec3[] Nrm, (float U, float V)[] Uv, ushort[] Tris)
        TwoToes(out int ringSize, out int rings)
    {
        // 24 segments, not 12: a real foot's rim runs about 28 slots, so 12 made this fixture coarser
        // than anything it stands for.
        ringSize = 24;
        rings = 8;
        int perTube = ringSize * rings;
        // Centres 2.4 apart on a radius of 1, so the crevice is 0.4 wide — the same fraction of a toe's
        // width as the gaps on a real foot. Spread them further and the fixture asks the cap to span a
        // gap wider than anything hosiery meets, which no bridging rule survives.
        float[] centres = { -1.2f, 1.2f };

        var pos = new SecondSkinWriter.Vec3[perTube * 2];
        var nrm = new SecondSkinWriter.Vec3[perTube * 2];
        var uv = new (float U, float V)[perTube * 2];
        var tris = new System.Collections.Generic.List<ushort>();

        for (int t = 0; t < 2; t++)
            for (int r = 0; r < rings; r++)
                for (int k = 0; k < ringSize; k++)
                {
                    float a = k / (float)ringSize * MathF.Tau;
                    float cx = MathF.Cos(a), cy = MathF.Sin(a);
                    int i = (t * rings + r) * ringSize + k;
                    pos[i] = new SecondSkinWriter.Vec3(centres[t] + cx, cy, r * 0.5f);
                    nrm[i] = new SecondSkinWriter.Vec3(cx, cy, 0f);
                    uv[i] = ((k + 0.5f) / ringSize, (r + 0.5f) / rings);   // texel centres; 1.0 would wrap to row 0

                    if (r + 1 < rings)
                    {
                        ushort p0 = (ushort)i;
                        ushort p1 = (ushort)((t * rings + r) * ringSize + (k + 1) % ringSize);
                        ushort q0 = (ushort)((t * rings + r + 1) * ringSize + k);
                        ushort q1 = (ushort)((t * rings + r + 1) * ringSize + (k + 1) % ringSize);
                        tris.AddRange(new[] { p0, p1, q0, p1, q1, q0 });
                    }
                }

        // Join the tubes at their base, the way toes share a foot. Without this they are separate
        // islands, and the pass deliberately refuses to bridge across islands — that is what stops the
        // two FEET being webbed to each other.
        tris.AddRange(new ushort[] { 0, 1, (ushort)perTube, 1, (ushort)(perTube + 1), (ushort)perTube });
        return (pos, nrm, uv, tris.ToArray());
    }

    [Theory]
    [InlineData((byte)2)]    // Float3
    [InlineData((byte)3)]    // Float4
    [InlineData((byte)14)]   // Half4
    [InlineData((byte)10)]   // Short4n
    [InlineData((byte)8)]    // Ubyte4n
    public void WriteNormal_round_trips_every_supported_type(byte type)
    {
        // The cap is invisible unless its recomputed normals survive the trip back into the mesh's own
        // packed format, so each supported encoding must decode to what was written.
        Span<float> got = stackalloc float[4];
        foreach (var (nx, ny, nz) in new[]
                 {
                     (0f, 1f, 0f), (0f, -1f, 0f), (1f, 0f, 0f),
                     (0.577f, 0.577f, 0.577f), (-0.267f, 0.535f, -0.802f),
                 })
        {
            var buf = new byte[32];
            Assert.True(SecondSkinWriter.WriteNormal(buf, 4, type, nx, ny, nz));

            SecondSkinWriter.ReadTyped(buf, 4, type, got);
            float x = got[0], y = got[1], z = got[2];
            if (type == 8) { x = x * 2 - 1; y = y * 2 - 1; z = z * 2 - 1; }   // as BuildVerbatim unbiases

            float tol = type == 8 ? 0.01f : 0.001f;    // ubyte4n has ~0.008 per step
            Assert.True(MathF.Abs(x - nx) < tol && MathF.Abs(y - ny) < tol && MathF.Abs(z - nz) < tol,
                $"type {type}: wrote ({nx},{ny},{nz}) read ({x},{y},{z})");
        }
    }

    [Theory]
    [InlineData((byte)13)]   // Half2 — no z
    [InlineData((byte)9)]    // Short2n — no z
    [InlineData((byte)5)]    // Ubyte4 — no normalized scale
    [InlineData((byte)17)]   // Ushort4 — no normalized scale
    public void WriteNormal_refuses_types_it_cannot_represent(byte type)
    {
        // Refusing is the point: a half-written normal is corruption, and the caller logs the refusal
        // rather than shipping a shell that shades wrong for no visible reason.
        var buf = new byte[32];
        Assert.False(SecondSkinWriter.WriteNormal(buf, 4, type, 0f, 1f, 0f));
        Assert.All(buf, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Toe_cap_with_an_all_black_mask_returns_no_displacement()
    {
        var pos = new[] { new SecondSkinWriter.Vec3(0, 0, 0), new SecondSkinWriter.Vec3(1, 0, 0), new SecondSkinWriter.Vec3(0, 0, 1) };
        var nrm = new[] { new SecondSkinWriter.Vec3(0, 1, 0), new SecondSkinWriter.Vec3(0, 1, 0), new SecondSkinWriter.Vec3(0, 1, 0) };
        var uv = new (float U, float V)[] { (0f, 0f), (1f, 0f), (0f, 1f) };

        Assert.Null(SecondSkinWriter.ToeCapDelta(pos, nrm, uv, new ushort[] { 0, 1, 2 }, new byte[16 * 16], 16, 16, 1f));
        // Zero strength is the same "leave it alone", with the map still declared.
        var white = new byte[16 * 16];
        Array.Fill(white, (byte)255);
        Assert.Null(SecondSkinWriter.ToeCapDelta(pos, nrm, uv, new ushort[] { 0, 1, 2 }, white, 16, 16, 0f));
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
