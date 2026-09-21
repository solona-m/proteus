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
public class SecondSkinWriterVerbatimTests(Xunit.Abstractions.ITestOutputHelper o)
{
    internal const string NeoTop =
        @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT CHEST - SmallClothes\0201e0000_top.mdl";
    /// <summary>The other parts of the SAME body, so the redundancy pass can be measured against a layout a
    /// character actually wears rather than two tops stacked on each other.</summary>
    private const string NeoLegs =
        @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT LEGS - SmallClothes\SFW Medium.mdl";
    private const string NeoHands =
        @"E:\Penumbradt\Neolithe [ALL IN ONE]\HANDS\Hands short.mdl";

    internal const string BiboTop =
        @"E:\Penumbradt\Bibo+\Breasts - Small Clothes\Nude - Large\chara\equipment\e0000\model\c0201e0000_top.mdl";
    internal const string HostRing =
        @"E:\Penumbradt\classic gold\classic gold accessories\rings\chara\accessory\a0001\model\c0201a0001_rir.mdl";

    /// <summary>
    /// A whole body, as the four parts a character wears at once. The redundancy pass is only answerable
    /// against a LAYOUT — a seam ring is redundant because the neighbouring part draws it — so a body
    /// measured one part at a time is not being measured at all.
    /// </summary>
    internal static readonly (string Body, string[] Parts)[] Bodies =
    [
        ("Neolithe",
        [
            @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT CHEST - SmallClothes\0201e0000_top.mdl",
            @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT LEGS - SmallClothes\SFW Medium.mdl",
            @"E:\Penumbradt\Neolithe [ALL IN ONE]\HANDS\Hands short.mdl",
            @"E:\Penumbradt\Neolithe [ALL IN ONE]\FEET\Feet.mdl",
        ]),
        ("Bibo+",
        [
            @"E:\Penumbradt\Bibo+\Breasts - Small Clothes\Nude - Large\chara\equipment\e0000\model\c0201e0000_top.mdl",
            @"E:\Penumbradt\Bibo+\Bottoms - Small Clothes\Type A - Medium\chara\equipment\e0000\model\c0201e0000_dwn.mdl",
            @"E:\Penumbradt\Bibo+\Hands\Small Clothes\chara\equipment\e0000\model\c0201e0000_glv.mdl",
            @"E:\Penumbradt\Bibo+\Feet\Small Clothes\chara\equipment\e0000\model\c0201e0000_sho.mdl",
        ]),
        ("Rue+",
        [
            @"E:\Penumbradt\hs-Rue+-2.2.7-y0f\files\chest - smallclothes\medium\chara\equipment\e0000\model\c0201e0000_top.mdl",
            @"E:\Penumbradt\hs-Rue+-2.2.7-y0f\files\legs - smallclothes\yanilla - a\chara\equipment\e0000\model\c0201e0000_dwn.mdl",
            @"E:\Penumbradt\hs-Rue+-2.2.7-y0f\files\Hands - Smallclothes\Short Nails\chara\equipment\e0000\model\c0201e0000_glv.mdl",
            @"E:\Penumbradt\hs-Rue+-2.2.7-y0f\files\Feet - Smallclothes\Rue Feet\chara\equipment\e0000\model\c0201e0000_sho.mdl",
        ]),
    ];

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
        => ContentPieceResolver.UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model))[0];

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
    public void A_lone_part_keeps_its_connector_rings_on_a_real_body()
    {
        // Neolithe's top carries joint-connector submeshes (atr_nek/hij/ude/…) beside its complete main
        // body — the neck ring is 250 triangles of a 10280-triangle mesh, the wrist ring 120.
        //
        // Alone, NOTHING of it goes, and that is the point of this test rather than an absence of one. A
        // ring is redundant because a NEIGHBOURING PART draws the same band; with no neighbour there is no
        // such band, and dropping one anyway leaves the wearer a bare neck. The old pass did exactly that
        // whenever the caller supplied no part layout, and this build has no way left to ask for it.
        if (!File.Exists(NeoTop)) return;

        var body = File.ReadAllBytes(NeoTop);
        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };

        SecondSkinWriter.Build(new[] { body }, layers, null, false, out var full);
        var keptBytes = SecondSkinWriter.Build(new[] { body }, layers, null, true, out var kept);

        Assert.Equal(full.TrianglesOut, kept.TrianglesOut);
        Assert.Equal(full.Submeshes, kept.Submeshes);
        Assert.Equal(0, kept.RedundantSubs);
        Validate(keptBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_lone_neolithe_part_loses_nothing_to_the_join_cut(int part)
    {
        // The join cut's lap rule once read a connector ring that runs PAST the end of the region it is
        // stitched to as tucked BEHIND it: the ring's closest points were clamped to that region's rim, and
        // the sign against a rim triangle is meaningless. Alone, the top lost both wrist rings (120 tri), the
        // hands theirs and the feet their ankle ring — every one a band nothing else draws. A lone part has
        // no neighbour to lap, and Neolithe has no inner lap either, so nothing at all may be cut.
        var path = Array.Find(Bodies, b => b.Body == "Neolithe").Parts[part];
        if (!File.Exists(path)) return;

        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };
        var lines = new List<string>();
        SecondSkinWriter.Build(new[] { File.ReadAllBytes(path) }, layers, null, true, out var stats, diag: lines.Add);

        foreach (var l in lines) if (l.Contains("join cut")) o.WriteLine(l);
        Assert.Equal(0, stats.TrimmedTris);
    }

    [Fact]
    public void A_lone_part_still_loses_a_true_lap()
    {
        // The other side of the rule above: Bibo+'s thigh runs six centimetres down inside its knee, every
        // vertex of it behind a face of the knee. Worn alone the legs must still lose it, or the fix for the
        // rings has simply switched the lap rule off.
        var path = Array.Find(Bodies, b => b.Body == "Bibo+").Parts[1];
        if (!File.Exists(path)) return;

        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };
        SecondSkinWriter.Build(new[] { File.ReadAllBytes(path) }, layers, null, true, out var stats);

        Assert.True(stats.TrimmedTris > 0, "the thigh lap inside the knee was not cut");
    }

    [Fact]
    public void A_real_layout_loses_the_wrist_ring_and_keeps_the_neck()
    {
        // The top with the legs and hands of the SAME body beside it — the arrangement a character
        // actually wears, and the only one that can answer whether this rule is safe on by default.
        //
        // What it finds, measured: exactly ONE submesh goes, the 120-triangle wrist connector at
        // y 0.995..1.027, which the hands draw. The 250-triangle neck ring (y 1.397..1.451) survives
        // because nothing else reaches that high, and the 840-triangle shoulder region (y 1.001..1.115)
        // survives because no other part encloses it. Both would be lost to a comparison that asked only
        // about height, and a bare neck is the regression this rule has produced before.
        if (!File.Exists(NeoTop) || !File.Exists(NeoLegs) || !File.Exists(NeoHands)) return;

        var neo = File.ReadAllBytes(NeoTop);
        var legs = File.ReadAllBytes(NeoLegs);
        var hands = File.ReadAllBytes(NeoHands);
        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };

        SecondSkinWriter.Build(new[] { neo, legs, hands }, layers, null, false, out var full);
        var trimmedBytes = SecondSkinWriter.Build(
            new[]
            {
                new SecondSkinWriter.SourceSpec(neo,   DropConnectors: true),
                new SecondSkinWriter.SourceSpec(legs,  DropConnectors: false),
                new SecondSkinWriter.SourceSpec(hands, DropConnectors: false),
            },
            layers, null, out var trimmed);

        Assert.Equal(1, trimmed.RedundantSubs);
        Assert.Equal(120, trimmed.RedundantTris);

        // The overlap cut was also at this shell, and its counter is deliberately NOT in the same units:
        // it counts triangles TOUCHED, and cutting one straddling triangle replaces it with one or two
        // smaller ones. So the output's triangle count is no longer the input minus a sum — it is the
        // input, minus what was dropped, minus the covered part of what was cut, plus the pieces the cut
        // left behind. There is no arithmetic identity to assert, and asserting one anyway is how a test
        // starts encoding the shape of a bug.
        //
        // What IS exact is the drop, so that is what this holds to: one submesh, the wrist ring, 120
        // triangles. The cut is measured on its own in the gated report.
        // The join cut is also at this shell, removing each part's margin past the ring it is stitched on.
        // It is counted separately and in different units from the submesh drop, so there is no arithmetic
        // identity between the two and the counts below hold only to the drop.
        Assert.True(trimmed.TrimmedTris > 0, "no flap was found at any join");
        Assert.True(full.Submeshes - trimmed.Submeshes >= trimmed.RedundantSubs * layers.Length,
            "fewer submeshes went missing than the plan says it dropped");
        Assert.True(trimmed.Meshes > 0, "the main body must survive");
        Validate(trimmedBytes);
    }

    /// <summary>
    /// What the pass actually finds on each body installed here, printed rather than asserted.
    /// <para/>
    /// The thresholds — 3 mm, 90%, a tenth of the mesh — were reasoned from ONE body's vertex spacing, and
    /// a reasoned number is not a measured one. Running the same pass across bodies that do and do not have
    /// the defect is what says whether they generalise: a duplicate should score near 100 and nothing else
    /// should come close, and a body with no doubled geometry should lose nothing but true joint rings.
    /// <para/>
    /// Every part runs the pass, exactly as the service configures it — the shell is cut from all of them
    /// at once and each is judged against the others.
    /// </summary>
    [Theory]
    [InlineData("Neolithe")]
    [InlineData("Bibo+")]
    [InlineData("Rue+")]
    public void Report_what_the_redundancy_pass_finds_on(string bodyName)
    {
        var parts = Array.Find(Bodies, b => b.Body == bodyName).Parts;
        if (parts == null || !Array.TrueForAll(parts, File.Exists)) return;

        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };
        var lines = new List<string>();
        var sources = parts
            .Select(p => new SecondSkinWriter.SourceSpec(File.ReadAllBytes(p), DropConnectors: true))
            .ToList();

        SecondSkinWriter.Build(sources, layers, null, out var stats, lines.Add);

        o.WriteLine($"=== {bodyName}: {sources.Count} part(s) ===");
        for (int i = 0; i < parts.Length; i++)
        {
            o.WriteLine($"source {i}: {Path.GetFileName(parts[i])}");
            if (SecondSkinWriter.ReadConnectorProfile(sources[i].Model) is not { } profile)
            {
                o.WriteLine("  (no skin geometry)");
                continue;
            }
            // The attribute NAMES, not just the mask. A bare "attrs 0x41" cannot tell a joint seam from a
            // mutually-exclusive body variant, and that distinction is the open question about this pass:
            // two variants of one region are coincident by construction, and only one of them is drawn.
            var attrNames = SecondSkinWriter.AttributeNames(sources[i].Model);
            string Attrs(uint mask)
            {
                if (mask == 0) return "none";
                var names = new List<string>();
                for (int bit = 0; bit < 32 && bit < attrNames.Count; bit++)
                    if ((mask & (1u << bit)) != 0) names.Add(attrNames[bit]);
                return names.Count == 0 ? $"0x{mask:x}" : string.Join(',', names);
            }

            foreach (var mesh in profile.Meshes)
            {
                o.WriteLine($"  mesh {mesh.Index}: largest submesh {mesh.LargestSubTriangles} tri");
                for (int k = 0; k < mesh.SubCount; k++)
                {
                    var sub = profile.Subs[mesh.SubFirst + k];
                    o.WriteLine($"    sub {sub.Index}: {sub.Triangles} tri, "
                              + $"x {sub.Box.MinX:F3}..{sub.Box.MaxX:F3}, "
                              + $"y {sub.Box.MinY:F3}..{sub.Box.MaxY:F3}, "
                              + $"z {sub.Box.MinZ:F3}..{sub.Box.MaxZ:F3}, attrs [{Attrs(sub.AttrMask)}]");
                }
            }
        }

        foreach (var l in lines)
            if (l.Contains("redundant") || l.Contains("overlap") || l.Contains("join ")) o.WriteLine(l);
        o.WriteLine($"TOTAL: dropped {stats.RedundantSubs} submesh(es) / {stats.RedundantTris} tri, "
                  + $"{stats.TrimmedTris} tri cut or dropped as a second layer, of {stats.TrianglesIn} in");

        // What the distance threshold is actually buying. A connector band does not sit ON the skin — it
        // sits slightly proud of it, which is its job — so the question is whether its coverage jumps to
        // near-total at some small distance (a displaced copy) or creeps up slowly (a different surface).
        // Sweeping it here is what makes 3 mm a measured number rather than a reasoned one.
        o.WriteLine("coverage by distance:");
        var boxes = new List<SecondSkinWriter.ConnectorProfile.Box>();
        var profiles = new SecondSkinWriter.ConnectorProfile?[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            profiles[i] = SecondSkinWriter.ReadConnectorProfile(sources[i].Model);
            if (profiles[i]?.PartBox is { } pb) boxes.Add(pb);
        }
        foreach (float mm in new[] { 2f, 3f, 4f, 5f, 6f, 8f })
        {
            int subs = 0, tris = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (profiles[i] is not { } pr) continue;
                var others = boxes.Where((_, k) => k != i).ToList();
                SecondSkinWriter.PlanConnectorDrops(pr, others, null, null, $"source {i}",
                    out int s2, out int t2, eps: mm / 1000f);
                subs += s2; tris += t2;
            }
            o.WriteLine($"  {mm:F0}mm: {subs} submesh(es), {tris} triangle(s)");
        }

        // Are the parts stitched to each other at an authored ring of shared vertices? If so the seam has
        // an exact boundary and no distance field is needed to find it.
        o.WriteLine("join rings (0.1mm):");
        SecondSkinWriter.DescribeJoinRings(profiles, 0.0001f, s => o.WriteLine($"  {s}"));

        // And the same stitching one level down, between REGIONS of one part — a thigh lapping over a
        // knee is the same shape of problem as a top lapping over the legs.
        o.WriteLine("internal rings (0.1mm):");
        for (int i = 0; i < parts.Length; i++)
            if (profiles[i] is { } pr)
                SecondSkinWriter.DescribeInternalRings(pr, 0.0001f, s => o.WriteLine($"  source {i}: {s}"));

        // Every pair that shares any surface, and over what height. A band at one end is two regions
        // MEETING, which doubles the alpha exactly like a duplicate does but cannot be fixed by dropping
        // either one.
        o.WriteLine("overlaps:");
        for (int i = 0; i < parts.Length; i++)
            if (profiles[i] is { } pr)
                SecondSkinWriter.DescribeOverlaps(pr, 0.005f,
                    s => o.WriteLine($"  source {i}: {s}"));
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
        // shared redundancy flag) into SourceSpec: the same inputs must still produce the same model. Run
        // over a MERGED build, since misalignment between per-source arrays is exactly what could not
        // happen with one source.
        //
        // Stronger than it was. The legacy overload used to pass no part layout at all and take a
        // shape-only shortcut the SourceSpec path never took, so "identical" held over two rules that were
        // not the same rule. The writer derives the layout from the sources now, so both really do run the
        // same pass on the same evidence.
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
        // The pass is per-source because its seam-ring rule reads a part as one of SEVERAL — it drops a ring
        // because a neighbouring part draws the same band, which means nothing for a lone face, tail or ear.
        // Proving the flag is honoured per source is what lets one of those sit beside a body source without
        // being eaten by it.
        if (!File.Exists(NeoTop) || !File.Exists(BiboTop)) return;

        var neo = File.ReadAllBytes(NeoTop);
        var bibo = File.ReadAllBytes(BiboTop);
        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };

        SecondSkinWriter.Stats Run(bool neoOn, bool biboOn)
        {
            SecondSkinWriter.Build(
                new[]
                {
                    new SecondSkinWriter.SourceSpec(neo,  DropConnectors: neoOn),
                    new SecondSkinWriter.SourceSpec(bibo, DropConnectors: biboOn),
                },
                layers, null, out var st);
            return st;
        }

        var neither = Run(false, false);
        var onlyNeo = Run(true, false);
        var onlyBibo = Run(false, true);
        var both = Run(true, true);

        // Each source's drops are decided by ITS OWN flag, and so are additive. Asserted as an equality
        // rather than as "trimming both removes more than trimming one", which was the old shape: that
        // silently assumed BOTH bodies have redundant geometry, and Bibo+ measures out with none at all —
        // which is the right answer for it, and made the inequality fail for a good reason.
        Assert.Equal(0, neither.RedundantSubs);
        Assert.Equal(both.RedundantSubs, onlyNeo.RedundantSubs + onlyBibo.RedundantSubs);
        Assert.Equal(both.RedundantTris, onlyNeo.RedundantTris + onlyBibo.RedundantTris);

        // And the flagged source really is trimmed, so the equality above is not two zeroes agreeing.
        Assert.True(onlyNeo.RedundantSubs > 0, "the flagged source was not trimmed at all");
        Assert.True(onlyNeo.TrianglesOut < neither.TrianglesOut, "the trimmed source kept every triangle");
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

    private const string RueHands =
        @"E:\Penumbradt\hs-Rue+-2.2.7-y0f\files\Hands - Smallclothes\Short Nails\chara\equipment\e0000\model\c0201e0000_glv.mdl";

    private const string BiboHands =
        @"E:\Penumbradt\Bibo+\Hands\Small Clothes\chara\equipment\e0000\model\c0201e0000_glv.mdl";

    /// <summary>
    /// A glove has to cover the nails. Every one of these hands lays a small UV island of skin under each nail,
    /// apart from the finger in the atlas, and glove art paints the fingers only — modelled here by painting every
    /// island but the small ones. Without CoverNails the trim opens a hole over each nail; with it the nail beds
    /// take the fingertip's UV and stay. Set PROTEUS_NAIL_OBJ to a folder to get the shells as .obj files.
    /// </summary>
    [Theory]
    [InlineData(NeoHands)]
    [InlineData(RueHands)]
    [InlineData(BiboHands)]
    public void A_glove_covers_the_nails(string path)
    {
        if (!File.Exists(path)) return;

        var hand = File.ReadAllBytes(path);
        const int size = 1024;
        var layers = new[]
        {
            new SecondSkinLayer
            {
                MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = PaintFingersNotNailBeds(hand, size),
                CoverageWidth = size, CoverageHeight = size,
            },
        };
        var log = new List<string>();

        var bareShell = SecondSkinWriter.Build(new[] { new SecondSkinWriter.SourceSpec(hand) }, layers, null, out var bare);
        var nailed = SecondSkinWriter.Build(new[] { new SecondSkinWriter.SourceSpec(hand, CoverNails: true) },
                                            layers, null, out var stats, log.Add);
        foreach (var l in log.Where(l => l.StartsWith("nail beds", StringComparison.Ordinal))) o.WriteLine(l);
        o.WriteLine($"triangles {bare.TrianglesOut} -> {stats.TrianglesOut}");

        Assert.True(Rescued(log) > 0, "no nail bed moved onto the fingertip");
        Assert.True(stats.TrianglesOut > bare.TrianglesOut, "the nail beds were still trimmed away");
        Validate(nailed);

        // Art that paints the nails too STILL moves them: what it puts there is the nail, which is what shows
        // through a glove.
        var all = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                                                Coverage = Enumerable.Repeat((byte)255, size * size).ToArray(),
                                                CoverageWidth = size, CoverageHeight = size } };
        var allLog = new List<string>();
        SecondSkinWriter.Build(new[] { new SecondSkinWriter.SourceSpec(hand, CoverNails: true) },
                               all, null, out _, allLog.Add);
        Assert.True(Rescued(allLog) > 0, "art painting the nails should still put the glove over them");

        // ...but a FINGERLESS glove leaves them alone: the fingertip they would land on is unpainted, so the nails
        // are trimmed away with the fingers rather than dressed in fabric from elsewhere.
        var none = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                                                 Coverage = PaintNailBedsOnly(hand, size),
                                                 CoverageWidth = size, CoverageHeight = size } };
        var noneLog = new List<string>();
        SecondSkinWriter.Build(new[] { new SecondSkinWriter.SourceSpec(hand, CoverNails: true) },
                               none, null, out _, noneLog.Add);
        Assert.Equal(0, Rescued(noneLog));

        if (Environment.GetEnvironmentVariable("PROTEUS_NAIL_OBJ") is { Length: > 0 } dir)
        {
            var stem = Path.Combine(dir, Path.GetFileNameWithoutExtension(path).Replace(' ', '_'));
            ToeCapDiagTests.WriteObj(bareShell, stem + "_glove_before.obj");
            ToeCapDiagTests.WriteObj(nailed, stem + "_glove_after.obj");
            o.WriteLine($"wrote {stem}_glove_before.obj / _after.obj");
        }
    }

    /// <summary>
    /// The body-material nails (the hands' atr_gv_a submesh) survive the redundancy pass and the join cut in a
    /// whole layout. Both read them as copies of the fingertips they lie on — every nail vertex is within the 5 mm
    /// coincidence distance — and dropped them, so no glove could ever cover them.
    /// </summary>
    [Fact]
    public void Body_material_nails_survive_the_redundancy_pass()
    {
        var parts = Bodies.First(b => b.Body == "Neolithe").Parts;
        if (!parts.All(File.Exists)) return;
        var layers = new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } };
        var hidden = new HashSet<string> { "atr_gv_b" };   // the game's pick: body-material nails, not the nail mesh

        (int Tris, List<string> Log) Build(bool coverNails)
        {
            var log = new List<string>();
            var specs = parts.Select((p, i) => new SecondSkinWriter.SourceSpec(File.ReadAllBytes(p),
                DropConnectors: true, HiddenAttributes: i == 2 ? hidden : null, CoverNails: i == 2 && coverNails)).ToList();
            var bytes = SecondSkinWriter.Build(specs, layers, null, out var st, log.Add);
            Validate(bytes);
            return (st.TrianglesOut, log);
        }

        var (before, beforeLog) = Build(false);
        var (after, afterLog) = Build(true);
        foreach (var l in afterLog.Where(l => l.StartsWith("nail beds", StringComparison.Ordinal))) o.WriteLine(l);
        o.WriteLine($"triangles {before} -> {after}");

        static bool DropsNails(List<string> log) => log.Any(l => l.StartsWith("redundant drop: source 2", StringComparison.Ordinal)
                                                                  && l.Contains("attrs 0x1", StringComparison.Ordinal));
        Assert.True(DropsNails(beforeLog), "the unflagged build no longer drops the nails — this test measures nothing");
        Assert.False(DropsNails(afterLog), "the hands' nails were dropped as redundant");
        Assert.True(after - before >= 1288, $"only {after - before} triangles came back; the nail submesh has 1288");
    }

    /// <summary>
    /// The hands as the GAME built them, from %TEMP%\proteus-shell-dump (see SecondSkinService.DumpShellInputs):
    /// per layer, how many of the nail mesh's vertices have shell over them, and the coverage the shell samples
    /// there. Writes each layer's hand shell and the nails to the Desktop. Does nothing without a dump.
    /// </summary>
    [Fact]
    public void Nails_from_game_dump()
    {
        var dir = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        if (!Directory.Exists(dir)) return;
        var desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Desktop", "nails");
        Directory.CreateDirectory(desk);

        foreach (var info in Directory.GetFiles(dir, "host*_inputs.txt"))
        {
            var pre = info[..^"inputs.txt".Length];
            var text = File.ReadAllLines(info);
            // A dump from a build that predates the flag names no hands, so every source gets a turn.
            bool anyFlagged = text.Any(l => l.Contains("coverNails=True", StringComparison.Ordinal));
            for (int s = 0; ; s++)
            {
                var line = Array.Find(text, l => l.StartsWith($"source[{s}] ", StringComparison.Ordinal));
                if (line == null) break;
                if (anyFlagged && !line.Contains("coverNails=True", StringComparison.Ordinal)) continue;
                o.WriteLine($"{Path.GetFileName(info)}: {line}");
                var hand = File.ReadAllBytes($"{pre}body{s}.mdl");
                SecondSkinWriter.TryReadLod0Geometry(hand, out var np, out _, out var nt, out _, out _,
                                                     skinOnly: false, nonSkin: true);
                // Nails are a handful of small patches. Without the flag to go on, anything bigger is some other
                // part — a face carries eyes, lashes and brows here — and measuring it says nothing about nails.
                if (!anyFlagged && np.Length / 3 > 2000) continue;
                o.WriteLine($"  nail mesh: {np.Length / 3} verts, {nt.Length / 3} tris; materials: "
                          + string.Join(", ", SecondSkinWriter.MaterialNames(hand)));
                var hidden = System.Text.RegularExpressions.Regex.Match(line, @"hiddenAttrs=(\S*)").Groups[1].Value;
                var shapes = System.Text.RegularExpressions.Regex.Match(line, @"shapes=(\S*)").Groups[1].Value;

                for (int i = 0; ; i++)
                {
                    var ll = Array.Find(text, l => l.StartsWith($"layer[{i}] ", StringComparison.Ordinal));
                    if (ll == null) break;
                    var cvm = System.Text.RegularExpressions.Regex.Match(ll, @"coverage=(\d+)x(\d+)");
                    var covPath = $"{pre}layer{i}_coverage.raw";
                    var layer = new SecondSkinLayer
                    {
                        MaterialName = "/mt_c0201a0053_rir_a.mtrl",
                        Coverage = cvm.Success && File.Exists(covPath) ? File.ReadAllBytes(covPath) : null,
                        CoverageWidth = cvm.Success ? int.Parse(cvm.Groups[1].Value) : 0,
                        CoverageHeight = cvm.Success ? int.Parse(cvm.Groups[2].Value) : 0,
                    };
                    var log = new List<string>();
                    byte[] shell;
                    try
                    {
                        // EVERY source, with the flags the game gave it: the redundancy pass decides per LAYOUT.
                        var specs = new List<SecondSkinWriter.SourceSpec>();
                        for (int q = 0; ; q++)
                        {
                            var sl = Array.Find(text, l => l.StartsWith($"source[{q}] ", StringComparison.Ordinal));
                            if (sl == null) break;
                            var sh = System.Text.RegularExpressions.Regex.Match(sl, @"shapes=(\S*)").Groups[1].Value;
                            var hd = System.Text.RegularExpressions.Regex.Match(sl, @"hiddenAttrs=(\S*)").Groups[1].Value;
                            specs.Add(new SecondSkinWriter.SourceSpec(File.ReadAllBytes($"{pre}body{q}.mdl"),
                                EnabledShapes: sh.Length > 0 ? new HashSet<string>(sh.Split(',')) : null,
                                HiddenAttributes: hd.Length > 0 ? new HashSet<string>(hd.Split(',')) : null,
                                DropConnectors: sl.Contains("dropRedundant=True", StringComparison.Ordinal),
                                CoverNails: anyFlagged ? sl.Contains("coverNails=True", StringComparison.Ordinal) : q == s));
                        }
                        shell = SecondSkinWriter.Build(specs, new[] { layer }, null, out _, log.Add);
                    }
                    catch (EmptyShellException) { o.WriteLine($"  layer {i}: nothing on the hands"); continue; }
                    o.WriteLine($"  layer {i} ({ll}):");
                    foreach (var l in log.Where(l => l.StartsWith("nail beds", StringComparison.Ordinal)
                                                  || l.Contains("redundan", StringComparison.OrdinalIgnoreCase)
                                                  || l.Contains("drop", StringComparison.OrdinalIgnoreCase)
                                                  || l.StartsWith("attributes", StringComparison.Ordinal)))
                        o.WriteLine("    " + l);

                    // Nail vertices with shell over them: any shell triangle within 1 mm.
                    SecondSkinWriter.TryReadLod0Geometry(shell, out var sp, out var su, out var st, out _, out _, skinOnly: false);
                    int covered = 0;
                    float worstGap = 0f;
                    for (int v = 0; v < np.Length / 3; v++)
                    {
                        float best = float.MaxValue;
                        for (int t = 0; t + 2 < st.Length; t += 3)
                        {
                            if (Math.Max(st[t], Math.Max(st[t + 1], st[t + 2])) * 3 + 2 >= sp.Length) continue;
                            float cx = (sp[st[t] * 3] + sp[st[t + 1] * 3] + sp[st[t + 2] * 3]) / 3f - np[v * 3];
                            float cy = (sp[st[t] * 3 + 1] + sp[st[t + 1] * 3 + 1] + sp[st[t + 2] * 3 + 1]) / 3f - np[v * 3 + 1];
                            float cz = (sp[st[t] * 3 + 2] + sp[st[t + 1] * 3 + 2] + sp[st[t + 2] * 3 + 2]) / 3f - np[v * 3 + 2];
                            best = MathF.Min(best, cx * cx + cy * cy + cz * cz);
                        }
                        float d = MathF.Sqrt(best);
                        if (d <= 0.002f) covered++;
                        worstGap = MathF.Max(worstGap, d);
                    }
                    o.WriteLine($"    nail verts with shell (a triangle centre within 2 mm): {covered}/{np.Length / 3}, "
                              + $"furthest from any shell triangle {worstGap:F4}");
                    ToeCapDiagTests.WriteObj(shell, Path.Combine(desk, $"hands_layer{i}_shell.obj"));

                    // How big each nail-bed island is on the coverage map, and what the map says under it.
                    if (layer.Coverage is { } cov)
                    {
                        SecondSkinWriter.TryReadLod0Geometry(hand, out _, out var hu, out var ht);
                        int nvv = hu.Length / 2;
                        var par = new int[nvv];
                        for (int q = 0; q < nvv; q++) par[q] = q;
                        int Find(int x) { while (par[x] != x) x = par[x] = par[par[x]]; return x; }
                        for (int t = 0; t + 2 < ht.Length; t += 3)
                        {
                            int a = Find(ht[t]), b = Find(ht[t + 1]); if (a != b) par[a] = b;
                            a = Find(ht[t + 1]); b = Find(ht[t + 2]); if (a != b) par[a] = b;
                        }
                        var cnt = new Dictionary<int, int>();
                        for (int t = 0; t + 2 < ht.Length; t += 3) { int r = Find(ht[t]); cnt[r] = cnt.GetValueOrDefault(r) + 1; }
                        int bedMax = (int)(cnt.Values.Max() * 0.2f);
                        int w = layer.CoverageWidth;
                        foreach (var r in cnt.Keys.Where(r => cnt[r] <= bedMax).Take(6))
                        {
                            float u0 = 9, u1 = -9, v0 = 9, v1 = -9;
                            for (int q = 0; q < nvv; q++)
                                if (Find(q) == r)
                                {
                                    u0 = MathF.Min(u0, hu[q * 2]); u1 = MathF.Max(u1, hu[q * 2]);
                                    v0 = MathF.Min(v0, hu[q * 2 + 1]); v1 = MathF.Max(v1, hu[q * 2 + 1]);
                                }
                            int x0 = (int)(u0 * w), x1 = (int)(u1 * w), y0 = (int)(v0 * w), y1 = (int)(v1 * w);
                            var vals = new List<string>();
                            for (int y = y0 - 1; y <= y1 + 1; y++)
                            {
                                var row = new List<string>();
                                for (int x = x0 - 1; x <= x1 + 1; x++)
                                    row.Add(cov[(((y % w) + w) % w) * w + (((x % w) + w) % w)].ToString("D3"));
                                vals.Add(string.Join(" ", row));
                            }
                            o.WriteLine($"    island {cnt[r]} tris: uv u {u0:F4}..{u1:F4} v {v0:F4}..{v1:F4} = "
                                      + $"{x1 - x0 + 1}x{y1 - y0 + 1} texels; map around it:");
                            foreach (var row in vals) o.WriteLine("      " + row);
                        }
                    }
                }
                ToeCapDiagTests.WriteObj(hand, Path.Combine(desk, "hands_body.obj"));
                o.WriteLine($"  wrote {desk}");

                // Do the nails skin like the skin under them? Nearest skin vertex per nail vertex, weights compared.
                SecondSkinWriter.TryReadLod0Geometry(hand, out var kp, out _, out _, out var kw, out _);
                SecondSkinWriter.TryReadLod0Geometry(hand, out var np2, out _, out _, out var nw, out _,
                                                     skinOnly: false, nonSkin: true);
                int same = 0, differ = 0;
                float worstDiff = 0f;
                var examples = new List<string>();
                for (int v = 0; v < np2.Length / 3; v++)
                {
                    int best = -1; float bd = float.MaxValue;
                    for (int k = 0; k < kp.Length / 3; k++)
                    {
                        float dx = kp[k * 3] - np2[v * 3], dy = kp[k * 3 + 1] - np2[v * 3 + 1], dz = kp[k * 3 + 2] - np2[v * 3 + 2];
                        float d = dx * dx + dy * dy + dz * dz;
                        if (d < bd) { bd = d; best = k; }
                    }
                    var a = nw[v].ToDictionary(x => x.Bone, x => x.W);
                    var b = kw[best].ToDictionary(x => x.Bone, x => x.W);
                    float diff = a.Keys.Union(b.Keys).Sum(bn => MathF.Abs(a.GetValueOrDefault(bn) - b.GetValueOrDefault(bn)));
                    if (diff < 0.05f) same++; else differ++;
                    if (diff > worstDiff) worstDiff = diff;
                    if (diff >= 0.05f && examples.Count < 6)
                        examples.Add($"nail [{string.Join(" ", nw[v].Select(x => $"{x.Bone}:{x.W:F2}"))}] vs skin "
                                   + $"[{string.Join(" ", kw[best].Select(x => $"{x.Bone}:{x.W:F2}"))}] at {MathF.Sqrt(bd):F5}");
                }
                o.WriteLine($"  nail weights vs nearest skin vertex: {same} match, {differ} differ (worst L1 {worstDiff:F2})");
                foreach (var e in examples) o.WriteLine("    " + e);
            }
        }
    }

    /// <summary>The point of triangle abc closest to p.</summary>
    private static (float X, float Y, float Z) ClosestPointOnTriangle(
        float px, float py, float pz, float ax, float ay, float az,
        float bx, float by, float bz, float cx, float cy, float cz)
    {
        float abx = bx - ax, aby = by - ay, abz = bz - az;
        float acx = cx - ax, acy = cy - ay, acz = cz - az;
        float apx = px - ax, apy = py - ay, apz = pz - az;
        float d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
        if (d1 <= 0 && d2 <= 0) return (ax, ay, az);

        float bpx = px - bx, bpy = py - by, bpz = pz - bz;
        float d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
        if (d3 >= 0 && d4 <= d3) return (bx, by, bz);

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            float v0 = d1 / (d1 - d3);
            return (ax + abx * v0, ay + aby * v0, az + abz * v0);
        }

        float cpx = px - cx, cpy = py - cy, cpz = pz - cz;
        float d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
        if (d6 >= 0 && d5 <= d6) return (cx, cy, cz);

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            float w0 = d2 / (d2 - d6);
            return (ax + acx * w0, ay + acy * w0, az + acz * w0);
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            float w1 = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return (bx + (cx - bx) * w1, by + (cy - by) * w1, bz + (cz - bz) * w1);
        }

        float den = 1f / (va + vb + vc);
        float v2 = vb * den, w2 = vc * den;
        return (ax + abx * v2 + acx * w2, ay + aby * v2 + acy * w2, az + abz * v2 + acz * w2);
    }

    /// <summary>
    /// A glove whose art paints the fingers and NOT the nail charts still takes the nails off: that is the usual
    /// glove, and the reason the nails showed through in the first place. Asking only the nail's own island whether
    /// it is covered left every one of them on the hand.
    /// </summary>
    [Theory]
    [InlineData(NeoHands)]
    [InlineData(RueHands)]
    public void A_glove_that_paints_only_the_fingers_takes_the_nails_off(string path)
    {
        if (!File.Exists(path)) return;
        var hand = File.ReadAllBytes(path);
        const int size = 256;
        SecondSkinLayer Gate(byte[] cov) => new()
        {
            MaterialName = "/gate.mtrl", Coverage = cov, CoverageWidth = size, CoverageHeight = size,
        };

        var log = new List<string>();
        var flat = BodyBridge.FlattenNails(hand, Gate(PaintFingersNotNailBeds(hand, size)), log.Add);
        foreach (var l in log) o.WriteLine(l);
        Assert.NotNull(flat);
        Assert.Contains(log, l => l.Contains(" 0 left uncovered", StringComparison.Ordinal));
        Assert.True(NailBedArea(flat!, hand) < 1e-9f, "a glove over the fingers must leave no nail to draw");
    }

    /// <summary>
    /// The nails come off the BODY under a garment: each nail is relaxed into the socket it sits in, so there is
    /// nothing left for a glove to poke through. Measured as the nail's height over a bridge pinned to its rim —
    /// about a millimetre before, nothing after — and the file must not change length, since the edit is
    /// positions and normals in place.
    /// </summary>
    [Theory]
    [InlineData(NeoHands)]
    [InlineData(RueHands)]
    [InlineData(BiboHands)]
    public void A_garment_flattens_the_nails_off_the_hand(string path)
    {
        if (!File.Exists(path)) return;
        var hand = File.ReadAllBytes(path);
        const int size = 256;
        var covered = new SecondSkinLayer
        {
            MaterialName = "/gate.mtrl", Coverage = Enumerable.Repeat((byte)255, size * size).ToArray(),
            CoverageWidth = size, CoverageHeight = size,
        };

        var log = new List<string>();
        var flat = BodyBridge.FlattenNails(hand, covered, log.Add);
        foreach (var l in log) o.WriteLine(l);
        Assert.NotNull(flat);
        Assert.Equal(hand.Length, flat!.Length);

        // Whatever is left of the nails has to draw nothing: a collapsed nail has no area, and a nail laid down
        // onto the finger is inside it. Areas come first — a collapsed island has no surface to measure a height
        // against.
        float areaBefore = NailBedArea(hand, hand), areaAfter = NailBedArea(flat, hand);
        o.WriteLine($"the nail beds drew {areaBefore * 1e6f:F1}mm² before, {areaAfter * 1e6f:F1}mm² after");

        // Measured against the SURROUNDING FINGER, never against the nail's own rim: the rim is the nail's outline,
        // and a patch spanning it is still a nail — flat instead of domed. That mistake passed the old check.
        float before = NailHeightOverTheFinger(hand, hand), after = NailHeightOverTheFinger(flat, hand);
        o.WriteLine($"the worst nail stands {before * 1000:F3}mm above the finger before, {after * 1000:F3}mm after");

        // Either way of taking a nail off has to leave nothing to see: collapsed, it has no area to draw; laid down
        // onto the finger, it is inside it. A tenth of a millimetre of slack on the second, since a vertex landing
        // on a socket's rim is level with the finger by definition.
        bool gone = areaAfter <= areaBefore * 0.01f;
        bool inside = after <= 0.0001f;
        Assert.True(gone || inside, $"the nails still draw {areaAfter * 1e6f:F1}mm² and stand "
                                  + $"{after * 1000:F3}mm above the finger");
        // Bibo+'s nails are flush with the finger to begin with — nothing to take down, and nothing to prove here.
        if (before > 0.0005f && !gone)
            Assert.True(after < before * 0.05f, $"the nails barely moved: {before * 1000:F3}mm -> {after * 1000:F3}mm");

        // A collapsed nail has to stay collapsed once the hand MOVES. Its vertices share a position, but if they
        // keep their own bone weights the game's skinning pulls them apart and the triangles get their area back —
        // which is the speck that survives at a fingertip. So every collapsed island must share one skinning too.
        foreach (var (mesh, islands) in SecondSkinWriter.NailBedIslands(flat))
            foreach (var island in islands)
            {
                var verts = new HashSet<int>(island);
                var weights = SecondSkinWriter.SkinningOf(flat, [.. verts]);
                if (weights.Count == 0) continue;
                // Only the collapsed ones: a nail laid down onto the finger keeps its own skinning, and should.
                var spread = SecondSkinWriter.SpreadOf(flat, mesh, [.. verts]);
                if (spread > 0.0001f) continue;
                Assert.True(weights.Distinct().Count() == 1,
                    $"mesh {mesh}: a collapsed nail has {weights.Distinct().Count()} different skinnings, so it "
                  + "comes apart as soon as the finger bends");
            }

        // ...and it has to stay gone through the SECOND SKIN, which pushes every vertex out along its own normal:
        // a collapsed point whose vertices still face different ways fans back open into slivers wearing the
        // nail's texture. That is what survived on the shell while the body measured clean.
        {
            var shell = SecondSkinWriter.Build(
                new[] { new SecondSkinWriter.SourceSpec(flat, CoverNails: true) },
                new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } },
                null, out _);
            float onNails = NailUvArea(shell, hand);
            o.WriteLine($"the shell draws {onNails * 1e6f:F2}mm² with nail UVs");
            // Only where every nail was COLLAPSED. A bridged nail keeps its own UVs on purpose — it is lying flush
            // against the finger, and the shell's own rescue moves those UVs onto the fingertip.
            var counts = System.Text.RegularExpressions.Regex.Match(
                string.Join("\n", log), @"nails: (\d+) of (\d+) collapsed");
            if (counts.Success && counts.Groups[1].Value == counts.Groups[2].Value)
                Assert.True(onNails < 0.1e-6f, $"the shell still draws {onNails * 1e6f:F2}mm² of nail");
        }

        // ...and a separate NAIL MESH is gone, not merely laid flat: every triangle of it has no area left.
        SecondSkinWriter.TryReadLod0Geometry(hand, out var wasP, out _, out var wasT, out _, out _,
                                             skinOnly: false, nonSkin: true);
        if (wasT.Length > 0)
        {
            SecondSkinWriter.TryReadLod0Geometry(flat, out var nowP, out _, out var nowT, out _, out _,
                                                 skinOnly: false, nonSkin: true);
            Assert.Equal(wasT.Length, nowT.Length);   // nothing removed from the file, only collapsed
            o.WriteLine($"the nail mesh drew {Area(wasP, wasT) * 1e6f:F1}mm² before, "
                      + $"{Area(nowP, nowT) * 1e6f:F1}mm² after");
            Assert.True(Area(wasP, wasT) > 1e-5f, "this hand has no nail mesh to remove");
            Assert.True(Area(nowP, nowT) < Area(wasP, wasT) * 0.01f, "the nail mesh is still drawing");
        }

        // A garment that covers nothing leaves the hand exactly as it was.
        var bare = new SecondSkinLayer
        {
            MaterialName = "/gate.mtrl", Coverage = new byte[size * size],
            CoverageWidth = size, CoverageHeight = size,
        };
        Assert.Null(BodyBridge.FlattenNails(hand, bare));
    }

    /// <summary>
    /// Area drawn with NAIL texture coordinates anywhere in a model, whatever geometry carries them: the nail art
    /// lives in those islands of the sheet, so anything sampling them shows a nail.
    /// </summary>
    private static float NailUvArea(byte[] model, byte[] hand)
    {
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(hand, out _, out var huv, out _));
        var boxes = new List<(float U0, float U1, float V0, float V1)>();
        foreach (var (_, beds) in SecondSkinWriter.NailBedIslands(hand))
            foreach (var b in beds)
            {
                float u0 = 9, u1 = -9, v0 = 9, v1 = -9;
                foreach (int v in b)
                {
                    if (v * 2 + 1 >= huv.Length) continue;
                    u0 = MathF.Min(u0, huv[v * 2]); u1 = MathF.Max(u1, huv[v * 2]);
                    v0 = MathF.Min(v0, huv[v * 2 + 1]); v1 = MathF.Max(v1, huv[v * 2 + 1]);
                }
                if (u1 > u0) boxes.Add((u0, u1, v0, v1));
            }
        if (boxes.Count == 0) return 0f;

        SecondSkinWriter.TryReadLod0Geometry(model, out var p, out var u, out var t, out _, out _, skinOnly: false);
        float area = 0f;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            if (Math.Max(t[i], Math.Max(t[i + 1], t[i + 2])) * 3 + 2 >= p.Length) continue;
            float cu = (u[t[i] * 2] + u[t[i + 1] * 2] + u[t[i + 2] * 2]) / 3f;
            float cv = (u[t[i] * 2 + 1] + u[t[i + 1] * 2 + 1] + u[t[i + 2] * 2 + 1]) / 3f;
            if (!boxes.Any(b => cu >= b.U0 && cu <= b.U1 && cv >= b.V0 && cv <= b.V1)) continue;
            var (ax, ay, az) = (p[t[i] * 3], p[t[i] * 3 + 1], p[t[i] * 3 + 2]);
            float e1x = p[t[i + 1] * 3] - ax, e1y = p[t[i + 1] * 3 + 1] - ay, e1z = p[t[i + 1] * 3 + 2] - az;
            float e2x = p[t[i + 2] * 3] - ax, e2y = p[t[i + 2] * 3 + 1] - ay, e2z = p[t[i + 2] * 3 + 2] - az;
            float nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            area += 0.5f * MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        }
        return area;
    }

    /// <summary>What the nail beds put on screen, in square metres: their islands' triangles in these positions.</summary>
    private static float NailBedArea(byte[] hand, byte[] facing)
    {
        var byMesh = SecondSkinWriter.ReadCapMeshes(hand).ToDictionary(x => x.Mesh, x => x.Pos);
        float sum = 0f;
        foreach (var (mesh, beds) in SecondSkinWriter.NailBedIslands(facing))
        {
            if (!byMesh.TryGetValue(mesh, out var mp)) continue;
            foreach (var tris in beds)
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    if (tris[t] >= mp.Length || tris[t + 1] >= mp.Length || tris[t + 2] >= mp.Length) continue;
                    var (a, b, c) = (mp[tris[t]], mp[tris[t + 1]], mp[tris[t + 2]]);
                    float nx = (b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y);
                    float ny = (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z);
                    float nz = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                    sum += 0.5f * MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                }
        }
        return sum;
    }

    /// <summary>
    /// The furthest any nail-bed vertex stands OUTSIDE the surrounding finger: its distance to the nearest point of
    /// the skin that is not a nail, signed by that skin's own outward normal. Islands come from
    /// <paramref name="facing"/> so the same nails are measured before and after.
    /// </summary>
    private static float NailHeightOverTheFinger(byte[] hand, byte[] facing)
    {
        var byMesh = SecondSkinWriter.ReadCapMeshes(hand).ToDictionary(x => x.Mesh, x => x.Pos);
        float worst = float.MinValue;
        foreach (var (mesh, beds) in SecondSkinWriter.NailBedIslands(facing))
        {
            if (!byMesh.TryGetValue(mesh, out var mp)) continue;
            // The rest of that mesh: everything no nail bed uses.
            var isBed = new HashSet<int>();
            foreach (var b in beds) foreach (int v in b) isBed.Add(v);
            SecondSkinWriter.TryReadLod0Geometry(hand, out var ap, out _, out var at);
            var rest = new List<(int A, int B, int C)>();
            for (int t = 0; t + 2 < at.Length; t += 3)
            {
                if (at[t] >= mp.Length || at[t + 1] >= mp.Length || at[t + 2] >= mp.Length) continue;
                if (isBed.Contains(at[t]) || isBed.Contains(at[t + 1]) || isBed.Contains(at[t + 2])) continue;
                rest.Add((at[t], at[t + 1], at[t + 2]));
            }
            if (rest.Count == 0) continue;

            foreach (var b in beds)
                foreach (int v in new HashSet<int>(b))
                {
                    if (v >= mp.Length) continue;
                    var p = mp[v];
                    float best = float.MaxValue, signed = 0f;
                    foreach (var (ia, ib, ic) in rest)
                    {
                        var (a, bb, c) = (mp[ia], mp[ib], mp[ic]);
                        var (qx, qy, qz) = ClosestPointOnTriangle(p.X, p.Y, p.Z, a.X, a.Y, a.Z, bb.X, bb.Y, bb.Z, c.X, c.Y, c.Z);
                        float dx = p.X - qx, dy = p.Y - qy, dz = p.Z - qz;
                        float d = dx * dx + dy * dy + dz * dz;
                        if (d >= best) continue;
                        best = d;
                        float nx = (bb.Y - a.Y) * (c.Z - a.Z) - (bb.Z - a.Z) * (c.Y - a.Y);
                        float ny = (bb.Z - a.Z) * (c.X - a.X) - (bb.X - a.X) * (c.Z - a.Z);
                        float nz = (bb.X - a.X) * (c.Y - a.Y) - (bb.Y - a.Y) * (c.X - a.X);
                        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                        signed = len < 1e-12f ? 0f : (dx * nx + dy * ny + dz * nz) / len;
                    }
                    if (best < float.MaxValue) worst = MathF.Max(worst, signed);
                }
        }
        return worst == float.MinValue ? 0f : worst;
    }

    /// <summary>Total area of a triangle list, in square metres: what it puts on screen.</summary>
    private static float Area(float[] p, int[] tri)
    {
        float sum = 0f;
        for (int t = 0; t + 2 < tri.Length; t += 3)
        {
            if (Math.Max(tri[t], Math.Max(tri[t + 1], tri[t + 2])) * 3 + 2 >= p.Length) continue;
            float ax = p[tri[t + 1] * 3] - p[tri[t] * 3], ay = p[tri[t + 1] * 3 + 1] - p[tri[t] * 3 + 1],
                  az = p[tri[t + 1] * 3 + 2] - p[tri[t] * 3 + 2];
            float bx = p[tri[t + 2] * 3] - p[tri[t] * 3], by = p[tri[t + 2] * 3 + 1] - p[tri[t] * 3 + 1],
                  bz = p[tri[t + 2] * 3 + 2] - p[tri[t] * 3 + 2];
            float cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
            sum += 0.5f * MathF.Sqrt(cx * cx + cy * cy + cz * cz);
        }
        return sum;
    }

    /// <summary>Nail beds the build moved onto the fingertip, summed over its "nail beds: N of M under painted
    /// fingertips" lines.</summary>
    private static int Rescued(IEnumerable<string> log)
        => log.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^nail beds: (\d+) of \d+ under painted"))
              .Where(m => m.Success).Sum(m => int.Parse(m.Groups[1].Value));

    /// <summary>A coverage map painting every UV island of the hand's skin except its nail beds.</summary>
    private static byte[] PaintFingersNotNailBeds(byte[] hand, int size) => PaintIslands(hand, size, beds: false);

    /// <summary>...and its opposite: only the nail beds, as a glove that bares the fingers leaves them.</summary>
    private static byte[] PaintNailBedsOnly(byte[] hand, int size) => PaintIslands(hand, size, beds: true);

    private static byte[] PaintIslands(byte[] hand, int size, bool beds)
    {
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(hand, out _, out var uv, out var tri));
        int nv = uv.Length / 2;
        var parent = new int[nv];
        for (int i = 0; i < nv; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int t = 0; t + 2 < tri.Length; t += 3)
        {
            int a = Find(tri[t]), b = Find(tri[t + 1]);
            if (a != b) parent[a] = b;
            a = Find(tri[t + 1]); b = Find(tri[t + 2]);
            if (a != b) parent[a] = b;
        }
        var count = new Dictionary<int, int>();
        for (int t = 0; t + 2 < tri.Length; t += 3) { int r = Find(tri[t]); count[r] = count.GetValueOrDefault(r) + 1; }
        int bedMax = (int)(count.Values.Max() * 0.2f);

        var mask = new byte[size * size];
        for (int t = 0; t + 2 < tri.Length; t += 3)
        {
            if (count[Find(tri[t])] <= bedMax != beds) continue;
            // The mesh's UVs as the shell sees them: shifted onto the tile by the floor of the minimum.
            float Px(int v) => (uv[v * 2] - MathF.Floor(uv[v * 2])) * size;
            float Py(int v) => (uv[v * 2 + 1] - MathF.Floor(uv[v * 2 + 1])) * size;
            float ax = Px(tri[t]), ay = Py(tri[t]), bx = Px(tri[t + 1]), by = Py(tri[t + 1]), cx = Px(tri[t + 2]), cy = Py(tri[t + 2]);
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
            int x1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
            int y1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
            float den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (MathF.Abs(den) < 1e-9f) continue;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float l1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den;
                    float l2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den;
                    if (l1 >= -0.01f && l2 >= -0.01f && 1f - l1 - l2 >= -0.01f) mask[y * size + x] = 255;
                }
        }
        return mask;
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
