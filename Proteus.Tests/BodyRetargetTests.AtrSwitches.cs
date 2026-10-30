using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Localization;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A body mod whose options switch custom attributes (<see cref="BodyAtrSwitches"/>): NIRÆ ships nine chests in one
/// model, each tagged <c>atrx_cb_chest_*</c>, and draws one. The refit sees only that one, carries only it, and takes the
/// switched tag off the skin it brings in so a later pick never leaves a hole.
/// </summary>
public partial class BodyRetargetTests
{
    private static readonly HashSet<string> None = new(StringComparer.Ordinal);

    private static HashSet<string> Names(params string[] names) => new(names, StringComparer.Ordinal);

    /// <summary>A body with two chests: A (drawn) where the garment's skin lies, and B 30 mm out.</summary>
    private static byte[] TwoChests(params string[] extraAttrs)
        => SyntheticModel.Build(["atrx_a", "atrx_b", .. extraAttrs],
            new SyntheticModel.Mesh(SkinMaterial,
                new SyntheticModel.Sub(0b01, TrianglesPerIsland: 5),
                new SyntheticModel.Sub(0b10, TrianglesPerIsland: 5, OffsetZ: 0.03f)));

    private static byte[] SkinOnly() => SyntheticModel.Build([],
        new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)));

    /// <summary>The pair <see cref="BodyRetarget.BuildPair"/> makes of a body whose options draw it narrowed: the target
    /// without its hidden parts, the hidden and switched tags carried to the swap.</summary>
    private static BodyRetarget.SlotPair Narrowed(string slot, byte[] body, IReadOnlySet<string>? hidden,
                                                  IReadOnlySet<string>? switched)
    {
        var whole = ModelPartReader.Read(body)!;
        var target = BodyRetarget.Without(whole, hidden);
        Assert.True(IdentityCorrespondence.TryBuild(target, target, slot, out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair(slot, built!, target, body, TargetHidden: hidden, TargetSwitched: switched);
    }

    /// <summary>Each drawn submesh of a model: its material, triangle count and the attribute names it carries.</summary>
    private static List<(string Material, int Triangles, HashSet<string> Tags)> Tagged(byte[] mdl)
    {
        var model = ModelPartReader.Read(mdl)!;
        return model.Parts.Where(p => p.Island < 0)
                    .Select(p => (p.Material.TrimStart('/'), p.Triangles.Length / 3,
                                  model.AttributeNames.Where((_, i) => i < 32 && (p.AttributeMask & (1u << i)) != 0)
                                                      .ToHashSet(StringComparer.Ordinal)))
                    .ToList();
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-1")]
    public void Atr_alternatives_no_longer_cut_the_drawn_one()
    {
        // [AC-body-atr-switches-1] The cut judges the drawn chest alone: none of it is cut, all of it goes in, and the
        // chest the options switch off neither goes in nor counts. Read whole, B is "uncovered" and counted cut.
        var body = TwoChests();
        var narrowed = BodyRetarget.SwapSkin(SkinOnly(), [Narrowed("_top", body, Names("atrx_b"), Names("atrx_a"))],
                                             out var report);
        var whole = BodyRetarget.SwapSkin(SkinOnly(), [Resized("_top", body)], out var wholeReport);

        Assert.Equal(0, report.Cut);
        Assert.Equal(5, report.Added);
        Assert.Equal(5, Drawn(narrowed!)[SkinMaterial.TrimStart('/')]);
        Assert.True(wholeReport.Cut > 0, "read whole, the undrawn chest is judged by the cut");
        Assert.Equal(5, Drawn(whole!)[SkinMaterial.TrimStart('/')]);
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-2")]
    public void BuildPair_narrows_the_target()
    {
        // [AC-body-atr-switches-2] The new body read without the chest its options switch off; the swap carries none of it.
        var body = TwoChests();
        var draw = new BodyRetarget.BodyDraw(null, Names("atrx_b"), Names("atrx_a", "atrx_b"));
        Assert.Null(BodyRetarget.BuildPair("_top", body, body, "chest", false, null, draw, null, out var pair));

        uint b = 1u << pair.Target.AttributeNames.ToList().IndexOf("atrx_b");
        Assert.DoesNotContain(pair.Target.Parts, p => (p.AttributeMask & b) != 0);
        Assert.Equal(Names("atrx_b"), pair.TargetHidden);
        Assert.Equal(Names("atrx_a"), pair.TargetSwitched);

        var swapped = BodyRetarget.SwapSkin(SkinOnly(), [pair], out var report);
        Assert.Equal(5, report.Added);
        Assert.Equal(5, Drawn(swapped!)[SkinMaterial.TrimStart('/')]);
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-3")]
    public void The_swapped_skin_drops_the_body_switched_tag()
    {
        // [AC-body-atr-switches-3] Brought in tagged atrx_a, the skin would vanish the moment the user picked chest B.
        var swapped = BodyRetarget.SwapSkin(SkinOnly(), [Narrowed("_top", TwoChests(), Names("atrx_b"), Names("atrx_a"))],
                                            out _);
        Assert.All(Tagged(swapped!), s => Assert.DoesNotContain("atrx_a", s.Tags));
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-4")]
    public void Tags_the_body_does_not_switch_stay()
    {
        // [AC-body-atr-switches-4] atr_ude (long gloves hide the arm) and a nail mod's atrx_ tag stay on the skin; a
        // piercing the body switches keeps its tag, and follows the body's choice.
        var body = SyntheticModel.Build(["atrx_a", "atr_ude", "atrx_nt_mani", "atrx_p"],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0b0111, TrianglesPerIsland: 5)),
            new SyntheticModel.Mesh(Piercings, new SyntheticModel.Sub(0b1000, OffsetZ: 0.001f)));
        var swapped = BodyRetarget.SwapSkin(SkinOnly(), [Narrowed("_top", body, None, Names("atrx_a", "atrx_p"))],
                                            out var report, carryPiercings: true);

        var skin = Assert.Single(Tagged(swapped!), s => s.Material == SkinMaterial.TrimStart('/'));
        Assert.Equal(Names("atr_ude", "atrx_nt_mani"), skin.Tags);
        Assert.Equal(1, report.Adorned);
        Assert.Contains("atrx_p", Assert.Single(Tagged(swapped!), s => s.Material == Piercings.TrimStart('/')).Tags);
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-4")]
    [Trait("Plan", "EDGE-body-atr-switches-10")]
    public void A_conditional_chest_group_brings_every_alternative_in_with_its_tags()
    {
        // [EDGE-body-atr-switches-10] Options this cannot judge (a Condition) leave the body exactly as before: both
        // chests come in, each still tagged, for Penumbra to choose between on the garment.
        string root = Path.Combine(Path.GetTempPath(), "proteus-atr-cond-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Atr(string a, bool on) => "{\"Type\":\"Atr\",\"Manipulation\":{\"Slot\":\"Body\",\"Attribute\":\"" + a
                                             + "\",\"Entry\":" + (on ? "true" : "false") + "}}";
            File.WriteAllText(Path.Combine(root, "meta.json"),
                "{\"FileVersion\":4,\"Name\":\"Body\",\"Author\":\"x\",\"Groups\":[{\"Name\":\"Chest\",\"Type\":\"Single\","
                + "\"Condition\":{\"Group\":\"Build\"},\"DefaultSettings\":0,\"Options\":["
                + "{\"Name\":\"A\",\"Manipulations\":[" + Atr("atrx_a", true) + "," + Atr("atrx_b", false) + "]},"
                + "{\"Name\":\"B\",\"Manipulations\":[" + Atr("atrx_a", false) + "," + Atr("atrx_b", true) + "]}]}]}");
            var draw = RefitCore.MaskOf(root, BodySizeCatalog.Read(root), "_top", null, 201);
            Assert.Null(draw);

            var body = TwoChests();
            Assert.Null(BodyRetarget.BuildPair("_top", body, body, "chest", false, null, draw, null, out var pair));
            var swapped = BodyRetarget.SwapSkin(SkinOnly(), [pair], out _, cutHidden: false);
            var tags = Tagged(swapped!).SelectMany(s => s.Tags).ToHashSet();
            Assert.Contains("atrx_a", tags);
            Assert.Contains("atrx_b", tags);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-7")]
    public void No_switches_writes_the_same_bytes()
    {
        // [AC-body-atr-switches-7] A body mod with no Atr options: the swap and the pair exactly as before.
        var body = TwoChests();
        var none = BodyRetarget.SwapSkin(SkinOnly(), [Narrowed("_top", body, null, null)], out _);
        var empty = BodyRetarget.SwapSkin(SkinOnly(), [Narrowed("_top", body, null, None)], out _);
        Assert.Equal(none, empty);

        var variants = SyntheticModel.Build(["atr_tv_a", "atr_tv_b"],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0b01, TrianglesPerIsland: 3),
                                    new SyntheticModel.Sub(0b10, TrianglesPerIsland: 3, OffsetZ: 0.01f)));
        var names = ModelPartReader.Read(variants)!.AttributeNames;
        var draw = new BodyRetarget.BodyDraw(0b01, None, None);
        Assert.Equal(BodyRetarget.UndrawnVariants(names, "_top", 0b01), draw.Hidden(names, "_top", atr: true));
        Assert.Null(BodyRetarget.BuildPair("_top", variants, variants, "chest", false, draw, draw, null, out var pair));
        Assert.Equal(Names("atr_tv_b"), pair.TargetHidden);
        Assert.Null(pair.TargetSwitched);
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-8")]
    public void The_source_keeps_all_its_alternatives()
    {
        // [AC-body-atr-switches-8] Which chest the author made the garment on, the reader's options cannot say: the
        // source is read whole, but for its undrawn IMC variants.
        var body = SyntheticModel.Build(["atrx_a", "atrx_b", "atr_tv_b"],
            new SyntheticModel.Mesh(SkinMaterial,
                new SyntheticModel.Sub(0b001, TrianglesPerIsland: 5),
                new SyntheticModel.Sub(0b010, TrianglesPerIsland: 5, OffsetZ: 0.03f),
                new SyntheticModel.Sub(0b100, TrianglesPerIsland: 5, OffsetZ: 0.06f)));
        var draw = new BodyRetarget.BodyDraw(0b01, Names("atrx_b"), Names("atrx_a", "atrx_b"));
        Assert.Null(BodyRetarget.BuildPair("_top", body, body, "chest", false, draw, draw, null, out var pair));

        var source = pair.Correspondence.Source;
        int Bit(string name) => 1 << source.AttributeNames.ToList().IndexOf(name);
        Assert.Contains(source.Parts, p => (p.AttributeMask & Bit("atrx_b")) != 0);
        Assert.DoesNotContain(source.Parts, p => (p.AttributeMask & Bit("atr_tv_b")) != 0);
        Assert.DoesNotContain(pair.Target.Parts, p => (p.AttributeMask & Bit("atrx_b")) != 0);
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-5")]
    public void BuildPair_takes_no_bare_imc_mask()
    {
        // [AC-body-atr-switches-5] Every refit path — the automatic one, the Studio, the size set, the CLI — gives the
        // pair a BodyDraw: no overload is left that takes the IMC mask alone and would skip the Atr switches.
        var overloads = typeof(BodyRetarget).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                                            .Where(m => m.Name == nameof(BodyRetarget.BuildPair)).ToList();
        Assert.NotEmpty(overloads);
        Assert.All(overloads, m => Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(ushort?)));
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-1")]
    public void An_unnamed_attribute_stays_drawn_and_tagged()
    {
        // [EDGE-body-atr-switches-1] An atrx_ tag the body mod never names: drawn, and kept on the skin brought in.
        var body = TwoChests("atrx_z");
        var swapped = BodyRetarget.SwapSkin(SkinOnly(), [Narrowed("_top", body, Names("atrx_b"), Names("atrx_a"))], out _);
        Assert.True(BodyRetarget.BuildPair("_top", body, body, "chest", false, null,
                                           new BodyRetarget.BodyDraw(null, Names("atrx_b"), Names("atrx_a", "atrx_b")),
                                           null, out var pair) == null);
        Assert.DoesNotContain("atrx_z", pair.TargetHidden!);
        Assert.DoesNotContain("atrx_z", pair.TargetSwitched!);
        Assert.NotNull(swapped);
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-13")]
    public void Imc_variants_and_atr_alternatives_hide_together()
    {
        var body = SyntheticModel.Build(["atrx_a", "atrx_b", "atr_tv_a", "atr_tv_b"],
            new SyntheticModel.Mesh(SkinMaterial,
                new SyntheticModel.Sub(0b0101, TrianglesPerIsland: 5),
                new SyntheticModel.Sub(0b0010, TrianglesPerIsland: 5, OffsetZ: 0.03f),
                new SyntheticModel.Sub(0b1000, TrianglesPerIsland: 5, OffsetZ: 0.06f)));
        var draw = new BodyRetarget.BodyDraw(0b01, Names("atrx_b"), Names("atrx_a", "atrx_b"));
        Assert.Null(BodyRetarget.BuildPair("_top", body, body, "chest", false, null, draw, null, out var pair));
        Assert.Equal(Names("atrx_b", "atr_tv_b"), pair.TargetHidden);
        Assert.Equal(5, pair.Target.Parts.Sum(p => p.Triangles.Length / 3));
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-14")]
    public void Every_skin_part_switched_off_is_the_unreadable_refusal()
    {
        var body = TwoChests();
        var draw = new BodyRetarget.BodyDraw(null, Names("atrx_a", "atrx_b"), Names("atrx_a", "atrx_b"));
        Assert.Equal(string.Format(Strings.Parts.RetargetUnreadableFmt, "chest"),
                     BodyRetarget.BuildPair("_top", body, body, "chest", false, null, draw, null, out _));
    }

    [Fact]
    [Trait("Plan", "AC-body-atr-switches-7")]
    public void The_no_skin_refusal_is_only_for_atr_hidden_skin()
    {
        // [AC-body-atr-switches-7] An IMC mask that leaves no skin drawn goes on as it always did; so does an Atr
        // switch naming nothing on this model.
        var variants = SyntheticModel.Build(["atr_tv_a", "atr_tv_b"],
            new SyntheticModel.Mesh(ClothMaterial, new SyntheticModel.Sub(0b01, TrianglesPerIsland: 3)),
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0b10, TrianglesPerIsland: 3, OffsetZ: 0.01f)));
        string unreadable = string.Format(Strings.Parts.RetargetUnreadableFmt, "chest");
        Assert.NotEqual(unreadable, BodyRetarget.BuildPair("_top", variants, variants, "chest", false, null,
                                                           new BodyRetarget.BodyDraw(0b01, None, None), null, out _));
        // An IMC mask hiding the skin under a body mod whose Atr switches name nothing here: as before, not the refusal.
        Assert.NotEqual(unreadable, BodyRetarget.BuildPair("_top", variants, variants, "chest", false, null,
                                                           new BodyRetarget.BodyDraw(0b01, Names("atrx_elsewhere"),
                                                                                     Names("atrx_elsewhere")), null, out _));
        var skin = SkinOnly();
        Assert.Null(BodyRetarget.BuildPair("_top", skin, skin, "chest", false, null,
                                           new BodyRetarget.BodyDraw(null, Names("atrx_elsewhere"), Names("atrx_elsewhere")),
                                           null, out _));
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-15")]
    public void A_switched_off_piercing_stays_out_and_a_switched_on_one_keeps_its_tag()
    {
        var body = SyntheticModel.Build(["atrx_p_on", "atrx_p_off"],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)),
            new SyntheticModel.Mesh(Piercings, new SyntheticModel.Sub(0b01, OffsetZ: 0.001f),
                                    new SyntheticModel.Sub(0b10, TrianglesPerIsland: 2, OffsetZ: 0.001f)));
        var swapped = BodyRetarget.SwapSkin(SkinOnly(), [Narrowed("_top", body, Names("atrx_p_off"), Names("atrx_p_on"))],
                                            out var report, carryPiercings: true);
        var rings = Tagged(swapped!).Where(s => s.Material == Piercings.TrimStart('/')).ToList();
        Assert.Equal(1, rings.Sum(r => r.Triangles));
        Assert.All(rings, r => Assert.Contains("atrx_p_on", r.Tags));
        Assert.Equal(1, report.Adorned);
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-18")]
    public void The_garment_s_own_atrx_tags_survive_the_swap()
    {
        var garment = SyntheticModel.Build(["atrx_cg_s"],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)),
            new SyntheticModel.Mesh(ClothMaterial, new SyntheticModel.Sub(0b1, OffsetZ: 0.01f)));
        var swapped = BodyRetarget.SwapSkin(garment, [Narrowed("_top", TwoChests(), Names("atrx_b"), Names("atrx_a"))], out _);
        Assert.Contains("atrx_cg_s", Assert.Single(Tagged(swapped!), s => s.Material == ClothMaterial.TrimStart('/')).Tags);
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-19")]
    public void Within_one_body_mod_the_pair_still_builds()
    {
        var body = TwoChests();
        var draw = new BodyRetarget.BodyDraw(null, Names("atrx_b"), Names("atrx_a", "atrx_b"));
        Assert.Null(BodyRetarget.BuildPair("_top", body, body, "chest", false, draw, draw, null, out var pair));
        Assert.Equal(10, pair.Correspondence.Source.Parts.Sum(p => p.Triangles.Length / 3));
        Assert.Equal(5, pair.Target.Parts.Sum(p => p.Triangles.Length / 3));
    }

    [Fact]
    [Trait("Plan", "EDGE-body-atr-switches-20")]
    public void Reached_onto_legs_skin_brings_the_drawn_alternative_and_drops_its_switched_tags()
    {
        // [EDGE-body-atr-switches-20] The legs a top reaches onto: only the drawn alternative comes in, untagged.
        var legs = SyntheticModel.Build(["atrx_v_o", "atrx_v_c"],
            new SyntheticModel.Mesh(SkinMaterial,
                new SyntheticModel.Sub(0b01, TrianglesPerIsland: 5),
                new SyntheticModel.Sub(0b10, TrianglesPerIsland: 3, OffsetZ: 0.03f)));
        var swapped = BodyRetarget.SwapSkin(SkinOnly(), [Narrowed("_dwn", legs, Names("atrx_v_c"), Names("atrx_v_o"))],
                                            out var report);
        Assert.Equal(5, report.Added);
        Assert.All(Tagged(swapped!), s => Assert.Empty(s.Tags));
    }
}
