using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

    // A .pmp that ships geometry, used to exercise the content-append path against a real pack rather than
    // a synthesised model. Absent on other machines, in which case these tests no-op like the ones above.
    private const string ContentPack = @"E:\ModPacks\Neolithe Piercings for Proteus.pmp";
    private const string ContentEntry = "top/belly button heart/chara/equipment/e0000/model/c0201e0000_top.mdl";

    /// <summary>
    /// The material the pack's own mesh is bound to, read OUT of the model rather than written down here:
    /// which material that is belongs to the pack's author, and a hard-coded name turns a legitimate
    /// rebind into a red test.
    /// </summary>
    private static string ContentMaterialOf(byte[] model)
        => SecondSkinService.UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0];

    private static byte[]? ReadPackEntry(string entry)
    {
        if (!File.Exists(ContentPack)) return null;
        using var zip = ZipFile.OpenRead(ContentPack);
        var e = zip.GetEntry(entry);
        if (e == null) return null;
        using var st = e.Open();
        using var ms = new MemoryStream();
        st.CopyTo(ms);
        return ms.ToArray();
    }

    private static ContentGeometry Geometry(byte[] model, string materialLeaf)
        => new(model, SecondSkinWriter.KeepByLeaf(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { materialLeaf.TrimStart('/') }));

    [Fact]
    public void Content_layer_appends_the_packs_own_geometry_into_the_host()
    {
        var content = ReadPackEntry(ContentEntry);
        if (content == null || !File.Exists(HostRing)) return;

        var ring = File.ReadAllBytes(HostRing);
        var ringMats = SecondSkinWriter.MaterialNames(ring);

        var layers = new[]
        {
            new SecondSkinLayer
            {
                MaterialName = "/mt_c0201a0001_rir_b.mtrl",
                Geometry = Geometry(content, ContentMaterialOf(content)),
            },
        };

        // No shell sources at all - the pack brought every vertex in the output.
        var outBytes = SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(), layers, ring, out var stats);

        var outMats = SecondSkinWriter.MaterialNames(outBytes);
        Assert.Equal(ringMats.Count + 1, outMats.Count);
        Assert.Equal("/mt_c0201a0001_rir_b.mtrl", outMats[^1]);
        for (int i = 0; i < ringMats.Count; i++)
            Assert.Equal(ringMats[i], outMats[i]);

        // The pack's mesh is REAL geometry, so it must have survived with vertices of its own - and with
        // more than the ring alone would contribute.
        SecondSkinWriter.Build(Array.Empty<SecondSkinWriter.SourceSpec>(),
            new[] { new SecondSkinLayer { MaterialName = "/x.mtrl", Geometry = Geometry(content, "nothing.mtrl") } },
            ring, out var ringOnly);
        Assert.True(stats.VerticesOut > ringOnly.VerticesOut, "the content mesh contributed no vertices");

        Validate(outBytes);
    }

    [Fact]
    public void Content_only_build_needs_no_shell_sources()
    {
        var content = ReadPackEntry(ContentEntry);
        if (content == null) return;

        var layers = new[]
        {
            new SecondSkinLayer
            {
                MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                Geometry = Geometry(content, ContentMaterialOf(content)),
            },
        };

        var outBytes = SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(), layers, null, out var stats);

        Assert.True(stats.Meshes > 0, "no mesh emitted");
        Assert.True(stats.VerticesOut > 0, "no vertices emitted");
        // Every triangle is carried through: a content piece has no coverage map to be trimmed by.
        Assert.Equal(stats.TrianglesIn, stats.TrianglesOut);
        Assert.Equal(new[] { "/mt_c0201a0053_rir_a.mtrl" }, SecondSkinWriter.MaterialNames(outBytes));
        Validate(outBytes);
    }

    [Fact]
    public void Content_build_without_geometry_still_demands_a_source()
    {
        // The relaxed guard is for content ONLY. A shell layer with no source would emit nothing at all,
        // so that case must keep throwing rather than silently producing an empty model.
        var layers = new[] { new SecondSkinLayer { MaterialName = "/a.mtrl", Coverage = null } };
        Assert.Throws<ArgumentException>(() => SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(), layers, null, out _));
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

    /// <summary>
    /// Every submesh bone map entry must name a bone that exists in the merged model's union bone list.
    /// <para/>
    /// This is the regression guard for the map's ENTRIES being bone indices in each SOURCE's own namespace:
    /// they are remapped by name onto the union list, exactly as the per-mesh bone tables are. Appending them
    /// verbatim was correct only for whichever source seeded the union list first, so a merged build could
    /// name arbitrary bones — invisible on today's body parts, which happen to share a bone list in the same
    /// order, and not invisible at all once a model with a different skeleton subset joins them.
    /// <para/>
    /// The obvious companion check — that each submesh's [boneStart, boneStart+boneCount) window fits inside
    /// the map — is deliberately absent. Real body models fail it as authored: a Neolithe e0000 top declares
    /// five submeshes with boneStart 0/23/46/69/92 and boneCount 23, reaching 115, against a 35-entry map.
    /// Those are the source's own numbers and shells carrying them render fine, so the game does not read
    /// that field as the struct layout implies. Asserting it would fail every fixture and prove nothing.
    /// </summary>
    private static void ValidateBoneMap(byte[] m, int mh, int meshStart, int meshCount)
    {
        ushort U16(int o) => BitConverter.ToUInt16(m, o);
        uint U32(int o) => BitConverter.ToUInt32(m, o);

        int submeshCount    = U16(mh + 8);
        int matCount        = U16(mh + 10);
        int boneCount       = U16(mh + 12);
        int boneTableCount  = U16(mh + 14);
        int boneTableShorts = U16(mh + 44);

        int subStart = meshStart + meshCount * 36;
        int p = subStart + submeshCount * 16
              + matCount * 4                                   // material name offsets
              + boneCount * 4                                  // bone name offsets
              + boneTableCount * 4 + boneTableShorts * 2;      // v6 bone tables: headers then data

        int mapCount = (int)U32(p) / 2;
        p += 4;

        for (int i = 0; i < mapCount; i++)
        {
            int bone = U16(p + i * 2);
            Assert.True(bone < boneCount,
                $"submesh bone map[{i}] = {bone}, past the {boneCount}-bone union list");
        }
    }

    // ── SourceSpec: the per-source refactor must change nothing ────────────────

    [Fact]
    public void SourceSpec_api_matches_the_legacy_api_byte_for_byte()
    {
        // The whole safety argument for collapsing the parallel arrays (enabled shapes, uv converters, one
        // shared connector flag) into SourceSpec: the same inputs must still produce the same model. Run
        // over a MERGED build, since misalignment between per-source arrays is exactly what could not
        // happen with one source.
        if (!File.Exists(NeoTop) || !File.Exists(BiboTop)) return;

        var neo = File.ReadAllBytes(NeoTop);
        var bibo = File.ReadAllBytes(BiboTop);
        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };

        var legacy = SecondSkinWriter.Build(new[] { neo, bibo }, layers, null, true, out var legacyStats);
        var spec = SecondSkinWriter.Build(
            new[]
            {
                new SecondSkinWriter.SourceSpec(neo,  DropConnectors: true),
                new SecondSkinWriter.SourceSpec(bibo, DropConnectors: true),
            },
            layers, null, out var specStats);

        Assert.Equal(legacy.Length, spec.Length);
        Assert.True(legacy.AsSpan().SequenceEqual(spec), "SourceSpec build differs from the legacy build");
        Assert.Equal(legacyStats.Meshes, specStats.Meshes);
        Assert.Equal(legacyStats.TrianglesOut, specStats.TrianglesOut);
    }

    [Fact]
    public void DropConnectors_is_per_source()
    {
        // The connector heuristic is Neolithe-tuned ("under 200 triangles, and the last submesh") and is
        // wrong for anything that is not a body. Proving it is now per-source is what lets a face or tail
        // source sit beside a body one without being eaten by it.
        if (!File.Exists(NeoTop) || !File.Exists(BiboTop)) return;

        var neo = File.ReadAllBytes(NeoTop);
        var bibo = File.ReadAllBytes(BiboTop);
        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };

        SecondSkinWriter.Build(new[] { neo, bibo }, layers, null, false, out var neither);
        SecondSkinWriter.Build(new[] { neo, bibo }, layers, null, true, out var both);
        SecondSkinWriter.Build(
            new[]
            {
                new SecondSkinWriter.SourceSpec(neo,  DropConnectors: true),
                new SecondSkinWriter.SourceSpec(bibo, DropConnectors: false),
            },
            layers, null, out var onlyNeo);

        // Trimming one source of the two lands strictly between trimming neither and trimming both.
        Assert.True(onlyNeo.TrianglesOut < neither.TrianglesOut, "the trimmed source kept every triangle");
        Assert.True(onlyNeo.TrianglesOut > both.TrianglesOut, "the untrimmed source was trimmed anyway");
    }

    [Fact]
    public void KeepByLeaf_selects_only_the_named_material()
    {
        // How every non-body surface picks its geometry: name the material the overlay targets and get
        // exactly the meshes bound to it. Here it is pointed at a name no mesh carries, which must select
        // nothing at all — and a shell with no geometry is an error, not a silently empty model.
        if (!File.Exists(NeoTop)) return;

        var neo = File.ReadAllBytes(NeoTop);
        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };
        var keepNothing = SecondSkinWriter.KeepByLeaf(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mt_c1401f0001_fac_a.mtrl" });

        Assert.Throws<InvalidOperationException>(() => SecondSkinWriter.Build(
            new[] { new SecondSkinWriter.SourceSpec(neo, KeepMaterial: keepNothing) },
            layers, null, out _));

        // And the body default still selects the body's skin, so the leaf filter is opt-in.
        var body = SecondSkinWriter.Build(
            new[] { new SecondSkinWriter.SourceSpec(neo) }, layers, null, out var stats);
        Assert.True(stats.Meshes > 0);
        Validate(body);
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

        ValidateBoneMap(m, mh, meshStart, meshCount);

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
