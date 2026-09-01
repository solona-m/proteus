using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

    private static ContentGeometry Geometry(byte[] model, string materialLeaf, bool mirrorUv1 = false)
        => new(model, SecondSkinWriter.KeepByLeaf(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { materialLeaf.TrimStart('/') }), mirrorUv1);

    /// <summary>
    /// A pack's own mesh toggles survive the merge.
    /// <para/>
    /// An accessory pack ships one model holding a dozen pieces, tags each piece's submeshes with a named
    /// attribute, and switches them with Penumbra's <c>Atr</c> manipulation — that is what the checkboxes in
    /// its option groups drive. This writer used to drop attributes outright ("attributes dropped", twice),
    /// so the merged model had nothing to toggle: every piece drew at once and the mod's own options did
    /// nothing at all.
    /// <para/>
    /// Built from a synthetic model rather than a pack on disk. The pack version of this test spent its life
    /// returning at its first line, because the file it named had been moved off the Desktop — the riskiest
    /// change in the writer looked covered and was not. See <see cref="SyntheticModel"/>.
    /// </summary>
    [Fact]
    public void A_packs_own_attribute_toggles_survive_the_merge()
    {
        string[] attrs = ["atrx_ears", "atrx_belly", "atrx_shins"];
        var content = SyntheticModel.Build(attrs,
            new SyntheticModel.Mesh("/mt_pack_a.mtrl",
                new SyntheticModel.Sub(1u << 0),      // ears
                new SyntheticModel.Sub(1u << 2)),     // shins
            new SyntheticModel.Mesh("/mt_pack_b.mtrl",
                new SyntheticModel.Sub(1u << 1)));    // belly

        // The fixture has to be a model the production parser accepts, or this proves nothing about the
        // writer. Reading its names back through the real reader is that check.
        Assert.Equal(attrs, SecondSkinWriter.MaterialsAndAttributes(content).Attributes
            .SelectMany(kv => kv.Value).Distinct().OrderBy(n => n, StringComparer.Ordinal)
            .ToList().OrderBy(n => Array.IndexOf(attrs, n)).ToArray());

        var outBytes = SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(),
            [new SecondSkinLayer
            {
                MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                Geometry = [Geometry(content, "/mt_pack_a.mtrl"), Geometry(content, "/mt_pack_b.mtrl")],
            }],
            null, out _);

        Validate(outBytes);

        // Every name carried is one the source actually had — a merged model naming an attribute nobody
        // tagged would be a checkbox that toggles nothing.
        var names = AttributeNames(outBytes);
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.Contains(n, attrs));

        // Names without masks is the same failure in a different disguise: the toggle exists and moves no
        // geometry. Checked by NAME rather than by "some mask is non-zero", which a scrambled remap passes.
        var tagged = SubmeshAttributeMasks(outBytes)
            .SelectMany(m => Enumerable.Range(0, names.Count).Where(b => (m & (1u << b)) != 0))
            .Select(b => names[b])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(3, tagged.Count);
        Assert.All(attrs, a => Assert.Contains(a, tagged));
    }

    /// <summary>
    /// Merging two models whose attribute tables list the SAME names in different orders renumbers both onto
    /// one union — every submesh still toggles with the attribute it was authored against.
    /// <para/>
    /// This is the case <see cref="SecondSkinWriter"/>'s remap exists for, and the one a single-source test
    /// cannot reach: with one source the union is that source's own table in its own order, so the remap is
    /// the identity and a version that ignored the lookup entirely would pass. Getting it wrong here is
    /// worse than dropping attributes, because the checkbox then moves the WRONG accessory.
    /// </summary>
    [Fact]
    public void Two_models_with_differently_ordered_attributes_merge_onto_one_union()
    {
        // Same three names, deliberately opposite orders, so bit 0 in one model means bit 2 in the other.
        var first = SyntheticModel.Build(["atrx_a", "atrx_b", "atrx_c"],
            new SyntheticModel.Mesh("/mt_one.mtrl", new SyntheticModel.Sub(1u << 0)));   // atrx_a
        var second = SyntheticModel.Build(["atrx_c", "atrx_b", "atrx_a"],
            new SyntheticModel.Mesh("/mt_two.mtrl", new SyntheticModel.Sub(1u << 0)));   // atrx_c

        var outBytes = SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(),
            [new SecondSkinLayer
            {
                MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                Geometry = [Geometry(first, "/mt_one.mtrl"), Geometry(second, "/mt_two.mtrl")],
            }],
            null, out _);

        Validate(outBytes);

        var names = AttributeNames(outBytes);
        var masks = SubmeshAttributeMasks(outBytes).Where(m => m != 0).ToList();
        Assert.Equal(2, masks.Count);

        // Each submesh names exactly the attribute it was authored with. Carried through verbatim — the raw
        // masks are both bit 0 — this would read as "atrx_a" twice.
        var named = masks
            .Select(m => Enumerable.Range(0, names.Count).First(b => (m & (1u << b)) != 0))
            .Select(b => names[b])
            .ToList();
        Assert.Contains("atrx_a", named);
        Assert.Contains("atrx_c", named);
        Assert.DoesNotContain("atrx_b", named);   // nothing was tagged with it
    }

    /// <summary>
    /// A pack's own hide toggle removes the geometry it names, and nothing else.
    /// <para/>
    /// This is the half of the toggle feature that actually deletes something. The resolution step —
    /// mask bits to attribute names — is unit-tested elsewhere; what is checked here is that the names
    /// reach the writer and that the right submesh is the one that disappears.
    /// </summary>
    [Fact]
    public void A_hidden_attribute_drops_its_own_submeshes_and_leaves_the_rest()
    {
        // One mesh, three submeshes: one tagged atr_a, one tagged atr_b, one untagged.
        var content = SyntheticModel.Build(["atr_a", "atr_b"],
            new SyntheticModel.Mesh("/mt_pack.mtrl",
                new SyntheticModel.Sub(1u << 0),
                new SyntheticModel.Sub(1u << 1),
                new SyntheticModel.Sub(0)));

        byte[] BuildWith(IReadOnlySet<string>? hidden) => SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(),
            [new SecondSkinLayer
            {
                MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                Geometry = [new ContentGeometry(content, _ => true, HiddenAttributes: hidden)],
            }],
            null, out _);

        // Nothing hidden: all three survive.
        var all = BuildWith(null);
        Validate(all);
        Assert.Equal(3, SubmeshAttributeMasks(all).Count);

        // atr_a hidden: its submesh goes, the other tagged one and the untagged one stay. Counting is not
        // enough — a filter that dropped the WRONG submesh would also leave two — so the surviving masks
        // are resolved back to names.
        var less = BuildWith(new HashSet<string>(StringComparer.Ordinal) { "atr_a" });
        Validate(less);
        var names = AttributeNames(less);
        var survivors = SubmeshAttributeMasks(less)
            .Select(m => m == 0
                ? "(untagged)"
                : names[Enumerable.Range(0, names.Count).First(b => (m & (1u << b)) != 0)])
            .Order()
            .ToList();
        Assert.Equal(["(untagged)", "atr_b"], survivors);

        // An untagged submesh is drawn unconditionally, so hiding every NAMED attribute still leaves it.
        var bare = BuildWith(new HashSet<string>(StringComparer.Ordinal) { "atr_a", "atr_b" });
        Validate(bare);
        Assert.Equal([0u], SubmeshAttributeMasks(bare));
    }

    /// <summary>
    /// Geometry emitted untagged spends nothing from the merged model's attribute table.
    /// <para/>
    /// The table holds 32 names because a submesh mask is a u32, and names past that are dropped — with
    /// whatever they switched stuck on. A pack whose visibility Proteus resolved has no submesh left
    /// referencing its names, so carrying them would spend that budget on nothing and could push a
    /// name-toggled pack's own attributes off the end.
    /// </summary>
    [Fact]
    public void Untagged_geometry_contributes_no_names_to_the_attribute_table()
    {
        var owned = SyntheticModel.Build(["atr_owned_a", "atr_owned_b"],
            new SyntheticModel.Mesh("/mt_owned.mtrl", new SyntheticModel.Sub(1u << 0)));
        var runtime = SyntheticModel.Build(["atrx_runtime"],
            new SyntheticModel.Mesh("/mt_runtime.mtrl", new SyntheticModel.Sub(1u << 0)));

        var outBytes = SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(),
            [new SecondSkinLayer
            {
                MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                Geometry =
                [
                    // Proteus resolved this one's visibility, so its tags are stripped…
                    new ContentGeometry(owned, _ => true, OwnAttributes: true),
                    // …while this one is switched at runtime by name and must keep its own.
                    new ContentGeometry(runtime, _ => true),
                ],
            }],
            null, out _);

        Validate(outBytes);

        // Only the name something still references survives into the table.
        Assert.Equal(["atrx_runtime"], AttributeNames(outBytes));

        // And the mask that names it still points at it, so the runtime toggle keeps working.
        var names = AttributeNames(outBytes);
        var tagged = SubmeshAttributeMasks(outBytes)
            .SelectMany(m => Enumerable.Range(0, names.Count).Where(b => (m & (1u << b)) != 0))
            .Select(b => names[b])
            .ToList();
        Assert.Equal(["atrx_runtime"], tagged);
    }

    /// <summary>
    /// A submesh tagged with several attributes needs them ALL on — one hidden name drops it.
    /// <para/>
    /// The deadrose dress is what settles this. Its dress material has a submesh tagged <c>atr_tv_b</c> and,
    /// separately, ones tagged <c>atr_tv_b + atr_tv_c</c>. Under a "keep while any name is on" rule the
    /// second pair could never differ from the first, so authoring both would be pointless — they are
    /// distinct only if the extra tag is a further requirement.
    /// </summary>
    [Fact]
    public void A_submesh_tagged_with_several_attributes_needs_all_of_them()
    {
        // The deadrose shape in miniature: "b alone", "b and c", and an untagged base.
        var content = SyntheticModel.Build(["atr_a", "atr_b", "atr_c"],
            new SyntheticModel.Mesh("/mt_pack.mtrl",
                new SyntheticModel.Sub(1u << 1),                 // atr_b
                new SyntheticModel.Sub((1u << 1) | (1u << 2)),   // atr_b + atr_c
                new SyntheticModel.Sub(0)));                     // always drawn

        byte[] BuildWith(params string[] hide) => SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(),
            [new SecondSkinLayer
            {
                MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                Geometry =
                [
                    new ContentGeometry(content, _ => true,
                        HiddenAttributes: new HashSet<string>(hide, StringComparer.Ordinal)),
                ],
            }],
            null, out _);

        // Hiding only atr_c drops the PAIR and leaves the atr_b-only one — the distinction that makes the
        // two worth authoring. A "keep while any is on" rule would leave all three.
        var less = BuildWith("atr_c");
        Validate(less);
        Assert.Equal([0u, 1u << 1], SubmeshAttributeMasks(less).Order());

        // Hiding atr_b takes both tagged submeshes, since both require it.
        var fewer = BuildWith("atr_b");
        Validate(fewer);
        Assert.Equal([0u], SubmeshAttributeMasks(fewer));
    }

    /// <summary>
    /// When Proteus owns the visibility answer, the surviving submeshes come out UNTAGGED.
    /// <para/>
    /// Otherwise the decision is made twice. A submesh's attribute mask is a gate the game closes from the
    /// IMC entry of the item being WORN, and this geometry ends up on a host accessory — so a piece the
    /// pack's toggles said to keep would be judged again by the host's mask, which knows nothing about the
    /// garment. That is arbitrary per bit, and it is why a dress's toggles could look inert while the same
    /// pack's shoes toggles worked.
    /// </summary>
    [Fact]
    public void Geometry_whose_visibility_proteus_resolved_is_emitted_untagged()
    {
        var content = SyntheticModel.Build(["atr_a", "atr_b"],
            new SyntheticModel.Mesh("/mt_pack.mtrl",
                new SyntheticModel.Sub(1u << 0),
                new SyntheticModel.Sub(1u << 1)));

        byte[] BuildWith(bool own) => SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(),
            [new SecondSkinLayer
            {
                MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                Geometry =
                [
                    new ContentGeometry(content, _ => true,
                        HiddenAttributes: new HashSet<string>(StringComparer.Ordinal) { "atr_a" },
                        OwnAttributes: own),
                ],
            }],
            null, out _);

        // Owned: atr_a's submesh is dropped, and the one we kept carries no tag for anything else to cull.
        var owned = BuildWith(true);
        Validate(owned);
        Assert.Equal([0u], SubmeshAttributeMasks(owned));

        // Not owned — a pack switched by Penumbra's Atr manipulation, where the runtime IS the mechanism —
        // keeps its tag, or the mod's own checkboxes would have nothing left to act on.
        var tagged = BuildWith(false);
        Validate(tagged);
        Assert.Contains(SubmeshAttributeMasks(tagged), m => m != 0);
    }

    /// <summary>
    /// A host carrying nothing but hidden geometry fails in a way the caller can tell apart from a fault —
    /// switching off the only piece on a carrier is the user getting what they asked for, not a broken
    /// build, and it used to arrive as "no geometry survived coverage trimming".
    /// </summary>
    [Fact]
    public void Hiding_every_mesh_reports_the_toggle_rather_than_coverage_trimming()
    {
        var content = SyntheticModel.Build(["atr_a"],
            new SyntheticModel.Mesh("/mt_pack.mtrl", new SyntheticModel.Sub(1u << 0)));

        var ex = Assert.Throws<EmptyShellException>(() => SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(),
            [new SecondSkinLayer
            {
                MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                Geometry =
                [
                    new ContentGeometry(content, _ => true,
                        HiddenAttributes: new HashSet<string>(StringComparer.Ordinal) { "atr_a" }),
                ],
            }],
            null, out _));

        Assert.True(ex.ByToggle);
        Assert.Contains("toggles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("coverage", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where a .mdl's tables begin — derived ONCE, because these offsets are the thing under test and three
    /// hand-rolled copies of the same arithmetic can agree with each other while all disagreeing with the
    /// writer. The attribute table in particular sits between the meshes and the submeshes, so getting
    /// <see cref="AttrStart"/> wrong silently shifts <see cref="SubStart"/> and everything after it.
    /// </summary>
    private readonly record struct Tables(int StrBlock, int Mh, int MeshStart, int AttrStart, int SubStart)
    {
        internal int AttrCount(byte[] m) => BitConverter.ToUInt16(m, Mh + 6);
        internal int SubmeshCount(byte[] m) => BitConverter.ToUInt16(m, Mh + 8);

        internal static Tables Of(byte[] m)
        {
            int declCount = BitConverter.ToUInt16(m, 12);
            int declEnd = 0x44 + declCount * 17 * 8;
            int strSize = (int)BitConverter.ToUInt32(m, declEnd + 4);
            int strBlock = declEnd + 8;
            int mh = strBlock + strSize;

            int meshCount = BitConverter.ToUInt16(m, mh + 4);
            int attrCount = BitConverter.ToUInt16(m, mh + 6);
            int elemCount = BitConverter.ToUInt16(m, mh + 24);
            int lodStart = mh + 56 + elemCount * 32;
            int meshStart = lodStart + 3 * 60 + ((m[mh + 27] & 0x10) != 0 ? 3 * 40 : 0);
            int attrStart = meshStart + meshCount * 36;
            return new Tables(strBlock, mh, meshStart, attrStart, attrStart + attrCount * 4);
        }
    }

    /// <summary>The attribute name table of a .mdl, in the order submesh masks index it.</summary>
    private static List<string> AttributeNames(byte[] m)
    {
        var t = Tables.Of(m);
        var names = new List<string>();
        for (int i = 0; i < t.AttrCount(m); i++)
        {
            int o = t.StrBlock + (int)BitConverter.ToUInt32(m, t.AttrStart + i * 4), e = o;
            while (m[e] != 0) e++;
            names.Add(System.Text.Encoding.ASCII.GetString(m, o, e - o));
        }
        return names;
    }

    private static List<uint> SubmeshAttributeMasks(byte[] m)
    {
        var t = Tables.Of(m);
        var masks = new List<uint>();
        for (int i = 0; i < t.SubmeshCount(m); i++)
            masks.Add(BitConverter.ToUInt32(m, t.SubStart + i * 16 + 8));
        return masks;
    }

    [Fact]
    public void A_glowing_content_mesh_gets_uv1_mirrored_from_its_own_uv0()
    {
        var content = ReadPackEntry(ContentEntry);
        if (content == null) return;

        var mat = ContentMaterialOf(content);
        SecondSkinLayer[] Layers(bool mirror) =>
        [
            new SecondSkinLayer
            {
                MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                Geometry = [Geometry(content, mat, mirrorUv1: mirror)],
            },
        ];

        var plain = SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(), Layers(false), null, out var plainStats);
        var mirrored = SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(), Layers(true), null, out var mirrorStats);

        // Whatever the declaration shape, the output must still parse: every element inside its stream's
        // stride, one declaration per mesh, every submesh bone index inside the union list. This is the
        // check that catches a stride grown without its declaration, or the reverse.
        Validate(plain);
        Validate(mirrored);

        // Mirroring changes uv1 and NOTHING about the geometry itself.
        Assert.Equal(plainStats.Meshes, mirrorStats.Meshes);
        Assert.Equal(plainStats.VerticesOut, mirrorStats.VerticesOut);
        Assert.Equal(plainStats.TrianglesOut, mirrorStats.TrianglesOut);

        // The mesh must actually HAVE a uv1 to check — a test that silently found none would pass forever.
        Assert.True(CountUv1Slots(mirrored) > 0, "no uv1 slot in the output to verify");

        // Copied verbatim, this pack's uv1 is a CONSTANT (0, 1) on every vertex — in the .zw of its Float4
        // uv0 and in its separate uidx1 element alike — while uv0 carries the real island. That is the whole
        // reason mirroring exists: characterscroll would sample the scroll map at one texel and render a
        // flat, colourless wash. Asserted so this test can never quietly go vacuous on a pack whose uv1
        // already happened to match.
        Assert.NotNull(FirstUv1Mismatch(plain));
        Assert.Null(FirstUv1Mismatch(mirrored));
    }

    // ── uv1 inspection ────────────────────────────────────────────────────────
    // A model reader narrow enough to answer one question: for every LOD0 vertex, does each uv1 slot hold
    // the same value as uv0? Offsets follow Validate's walk; the vertex buffer base is the ModelFileHeader's
    // LOD0 offset at 16.

    private static IEnumerable<(byte[] M, int Vb, int Mo, int Db)> Lod0Meshes(byte[] m)
    {
        int declCount = BitConverter.ToUInt16(m, 12);
        int declEnd = 0x44 + declCount * 17 * 8;
        int strSize = (int)BitConverter.ToUInt32(m, declEnd + 4);
        int mh = declEnd + 8 + strSize;
        int meshCount = BitConverter.ToUInt16(m, mh + 4);
        int elemCount = BitConverter.ToUInt16(m, mh + 24);
        byte flags2 = m[mh + 27];
        int lodStart = mh + 56 + elemCount * 32;
        int meshStart = lodStart + 3 * 60 + ((flags2 & 0x10) != 0 ? 3 * 40 : 0);
        int vb = (int)BitConverter.ToUInt32(m, 16);

        for (int mi = 0; mi < meshCount; mi++)
            yield return (m, vb, meshStart + mi * 36, 0x44 + mi * 17 * 8);
    }

    /// <summary>uv0 and every uv1 slot of one mesh, as (offsetInVertex, isHalf) pairs in its stream.</summary>
    private static (int Stream, int Offset, byte Type)? Uv0Of(byte[] m, int db)
    {
        for (int e = 0; e < 17; e++)
        {
            int o = db + e * 8;
            if (m[o] == 0xFF) break;
            if (m[o + 3] == 4 && m[o + 4] == 0) return (m[o], m[o + 1], m[o + 2]);
        }
        return null;
    }

    private static List<(int Stream, int Offset, bool Half)> Uv1SlotsOf(byte[] m, int db)
    {
        var slots = new List<(int, int, bool)>();
        var uv0 = Uv0Of(m, db);
        // The .zw half of a Float4 / Half4 uv0 IS a uv1 — the same rule PlanUv1 encodes.
        if (uv0 is { } u)
        {
            if (u.Type == 3)  slots.Add((u.Stream, u.Offset + 8, false));
            if (u.Type == 14) slots.Add((u.Stream, u.Offset + 4, true));
        }
        for (int e = 0; e < 17; e++)
        {
            int o = db + e * 8;
            if (m[o] == 0xFF) break;
            if (m[o + 3] == 4 && m[o + 4] == 1) slots.Add((m[o], m[o + 1], m[o + 2] is 13 or 14));
        }
        return slots;
    }

    private static int CountUv1Slots(byte[] m)
        => Lod0Meshes(m).Where(x => BitConverter.ToUInt16(x.M, x.Mo) > 0).Sum(x => Uv1SlotsOf(m, x.Db).Count);

    /// <summary>The first uv1 slot that does not mirror its mesh's uv0, described; null when every one
    /// does. A finding, not an assertion, so a caller can require EITHER answer.</summary>
    private static string? FirstUv1Mismatch(byte[] m)
    {
        foreach (var (_, vb, mo, db) in Lod0Meshes(m))
        {
            ushort vc = BitConverter.ToUInt16(m, mo);
            if (vc == 0) continue;
            if (Uv0Of(m, db) is not { } uv0) continue;

            uint[] vbo = { BitConverter.ToUInt32(m, mo + 20), BitConverter.ToUInt32(m, mo + 24),
                           BitConverter.ToUInt32(m, mo + 28) };
            byte[] bs = { m[mo + 32], m[mo + 33], m[mo + 34] };
            bool uv0Half = uv0.Type is 13 or 14;

            foreach (var (st, off, half) in Uv1SlotsOf(m, db))
                for (int i = 0; i < vc; i++)
                {
                    var (u0, v0) = ReadUv(m, vb + (int)vbo[uv0.Stream] + i * bs[uv0.Stream] + uv0.Offset, uv0Half);
                    var (u1, v1) = ReadUv(m, vb + (int)vbo[st] + i * bs[st] + off, half);
                    // Half-precision on either side, so compare at half's resolution rather than exactly.
                    if (Math.Abs(u0 - u1) >= 1e-2f || Math.Abs(v0 - v1) >= 1e-2f)
                        return $"vertex {i}: uv1 ({u1}, {v1}) does not mirror uv0 ({u0}, {v0})";
                }
        }
        return null;
    }

    private static (float U, float V) ReadUv(byte[] m, int at, bool half)
        => half
            ? ((float)BitConverter.ToHalf(m, at), (float)BitConverter.ToHalf(m, at + 2))
            : (BitConverter.ToSingle(m, at), BitConverter.ToSingle(m, at + 4));

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
                Geometry = [Geometry(content, ContentMaterialOf(content))],
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
            new[] { new SecondSkinLayer { MaterialName = "/x.mtrl", Geometry = [Geometry(content, "nothing.mtrl")] } },
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
                Geometry = [Geometry(content, ContentMaterialOf(content))],
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
    public void One_layer_can_carry_several_meshes_against_a_single_material()
    {
        // The point of the whole thing: a material is what costs a slot on the host, so two pieces of a pack
        // that want the same material publish it ONCE and both draw with it. A pack of five piercings on one
        // material would otherwise spend five of the host's ten slots.
        var top = ReadPackEntry(ContentEntry);
        var bottom = ReadPackEntry("bottom/hip dermals/chara/equipment/e0000/model/c0201e0000_dwn.mdl");
        if (top == null || bottom == null) return;

        SecondSkinWriter.Build(Array.Empty<SecondSkinWriter.SourceSpec>(),
            [new SecondSkinLayer { MaterialName = "/m.mtrl", Geometry = [Geometry(top, ContentMaterialOf(top))] }],
            null, out var topOnly);
        SecondSkinWriter.Build(Array.Empty<SecondSkinWriter.SourceSpec>(),
            [new SecondSkinLayer { MaterialName = "/m.mtrl", Geometry = [Geometry(bottom, ContentMaterialOf(bottom))] }],
            null, out var bottomOnly);

        var merged = SecondSkinWriter.Build(
            Array.Empty<SecondSkinWriter.SourceSpec>(),
            [
                new SecondSkinLayer
                {
                    MaterialName = "/m.mtrl",
                    Geometry =
                    [
                        Geometry(top, ContentMaterialOf(top)),
                        Geometry(bottom, ContentMaterialOf(bottom)),
                    ],
                },
            ],
            null, out var stats);

        // Both meshes are in there — nothing was deduped away — and they cost ONE material between them.
        Assert.Equal(new[] { "/m.mtrl" }, SecondSkinWriter.MaterialNames(merged));
        Assert.Equal(topOnly.VerticesOut + bottomOnly.VerticesOut, stats.VerticesOut);
        Assert.Equal(topOnly.TrianglesOut + bottomOnly.TrianglesOut, stats.TrianglesOut);
        Assert.Equal(stats.TrianglesIn, stats.TrianglesOut);   // no coverage map, so nothing is trimmed

        Validate(merged);
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

    [Fact]
    public void Connector_ring_threshold_is_relative_to_its_own_mesh()
    {
        // The regression guard for the neck. Neolithe's torso holds ONE skin mesh (13150 triangles, the
        // undies/piercings meshes are not skin and never reach the shell) split into five submeshes:
        //
        //   sub 0  atr_nek     250 tris   neck connector ring
        //   sub 1             10280 tris  the torso itself
        //   sub 2  atr_ude    1660 tris   elbow
        //   sub 3  atr_hij     840 tris   wrist skin
        //   sub 4  atr_hij     120 tris   wrist connector ring   (also the last submesh)
        //
        // The old flat "under 200 triangles" cutoff took sub 4 and left the 250-triangle NECK ring in, so
        // it kept doubling up. Relative to its own mesh the neck ring is 1.9%, the same order as the two
        // that were already caught, and the three real skin parts are 6.4% and up. So the shell must end
        // up with exactly subs 1, 2 and 3.
        if (!File.Exists(NeoTop)) return;

        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };
        var bytes = SecondSkinWriter.Build(new[] { File.ReadAllBytes(NeoTop) }, layers, null, true, out var trimmed);

        Assert.Equal(3, trimmed.Submeshes);
        Assert.Equal(10280 + 1660 + 840, trimmed.TrianglesOut);
        Validate(bytes);
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

        int attrCount       = U16(mh + 6);
        int submeshCount    = U16(mh + 8);
        int matCount        = U16(mh + 10);
        int boneCount       = U16(mh + 12);
        int boneTableCount  = U16(mh + 14);
        int boneTableShorts = U16(mh + 44);

        // The attribute name table sits between the meshes and the submeshes, so every table after it moves
        // by attrCount * 4. Stepping over it was free while the writer dropped attributes and emitted none;
        // it is not now that a pack's own mesh toggles are carried through.
        int subStart = meshStart + meshCount * 36 + attrCount * 4;
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

        // The OTHER half of EmptyShellException: nothing was hidden by a toggle here, the material filter
        // simply matched no mesh. ByToggle false is what keeps this reported as the fault it is, rather
        // than as a user having switched something off.
        var empty = Assert.Throws<EmptyShellException>(() => SecondSkinWriter.Build(
            new[] { new SecondSkinWriter.SourceSpec(neo, KeepMaterial: keepNothing) },
            layers, null, out _));
        Assert.False(empty.ByToggle);

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
