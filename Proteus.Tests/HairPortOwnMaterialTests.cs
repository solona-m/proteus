using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using XivLiveMesh;

namespace Proteus.Tests;

/// <summary>
/// A port across the sexes into a material folder the target's sex shares names a material of its own
/// (<see cref="HairMaterialPath.PortName"/>), so one race of the other sex can be ported alone; and such a material is
/// never left unpublished where its port is on (the hair would draw invisible) — through saves, remakes and undos.
/// Hairstyle 130: every man but Hrothgar reads Midlander's folder for it.
/// </summary>
public class HairPortOwnMaterialTests
{
    private const int Id = 130;
    private static readonly string Source = HairPortPlan.HairPath(201, Id);
    private static readonly ushort[] Races = [101, 201, 301, 401, 701, 801, 1101, 1201, 1301];

    private static Func<string, byte[]?> Game() => CrossSexFixtures.Game(CrossSexFixtures.GameFiles(Races, [Id, 105]));

    private static string Target(ushort race) => HairPortPlan.HairPath(race, Id);

    /// <summary>The own-named game path of a race's material <paramref name="suffix"/> for hairstyle 130.</summary>
    internal static string Own(ushort race, string suffix = "a", int id = Id)
        => HairMaterialPath.GamePath(race, id, $"/mt_c{race:D4}h{id:D4}_hir_{suffix}_port{race:D4}.mtrl")!;

    /// <summary>The game-named path the race's own model of hairstyle 130 reads.</summary>
    private static string Shared(ushort race, string suffix = "a", int id = Id)
        => HairMaterialPath.GamePath(race, id, $"/mt_c{race:D4}h{id:D4}_hir_{suffix}.mtrl")!;

    private static HairPortFixtures.Mod WomensMod(string[]? options = null) => CrossSexFixtures.WomensMod(Id, options: options);

    private static HairPortBuilder.Plan Hand(HairPortFixtures.Mod mod, params ushort[] targets)
        => HairPortPanel.PlanByHand(mod.Root, Source, targets, CrossSexFixtures.Pbd(), Game(), default, null);

    private static void HandPort(HairPortFixtures.Mod mod, params ushort[] targets)
    {
        var plan = Hand(mod, targets);
        Assert.Empty(plan.Refused);
        var saved = HairPortWriter.Save(mod.Root, plan.Writes, auto: false);
        Assert.True(saved.Ok, saved.Message);
        AssertOwnMaterialsPublished(mod.Root);
    }

    /// <summary>
    /// The invariant: every own-named material a recorded port's MODEL names (read from its file, not the record, which
    /// leaves a borrowed one out) is published by the port's own place or by the default data.
    /// </summary>
    internal static void AssertOwnMaterialsPublished(string root)
    {
        var owners = HairPortPlan.ReadOwners(root)!;
        var defaults = owners.FirstOrDefault(o => o.IsDefault);
        foreach (var e in HairPortRecord.Read(root).Ports)
        {
            var (race, id) = HairPortPlan.Parse(e.TargetGamePath)!.Value;
            var owner = owners.First(o => HairPortPlan.Same(o, e.Owner));
            var model = File.ReadAllBytes(Path.Combine(root, e.File.Replace('/', Path.DirectorySeparatorChar)));
            foreach (var name in SecondSkinWriter.Parse(model).MatNames.Where(HairMaterialPath.IsOwnName))
            {
                var path = HairMaterialPath.GamePath(race, id, name)!;
                Assert.True(HairPortPlan.FileFor(owner, path) != null || (defaults != null && HairPortPlan.FileFor(defaults, path) != null),
                            $"{e.TargetGamePath} in {HairPortWriter.Describe(owner)} names {path}, published nowhere it is on");
            }
        }
    }

    /// <summary>[AC-hair-material-scalp-fit-10] The helper reads the model: a record edited to forget the own material,
    /// with the redirect gone, still fails it.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-10")]
    public void Invariant_helper_reads_the_model_not_the_record()
    {
        using var mod = WomensMod();
        HandPort(mod, 101);
        var record = HairPortRecord.Read(mod.Root);
        record.Ports.Single().Materials.Clear();
        HairPortRecord.Write(mod.Root, record);
        AssertOwnMaterialsPublished(mod.Root);   // the redirect is still there
        PenumbraModMeta.RemoveOptionRedirects(mod.Root, "Cut", "Long",
            new Dictionary<string, string> { [Own(101)] = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Own(101))! });
        Assert.ThrowsAny<Exception>(() => AssertOwnMaterialsPublished(mod.Root));
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-1] The report: only Midlander M ticked, for a hairstyle every man reads one material for.
    /// It ports — no "port to them too" — the Studio says one hairstyle ported, and the row reads Ported.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-1")]
    public void Studio_ports_one_man_alone_from_a_shared_mens_material()
    {
        using var mod = WomensMod();
        var plan = Hand(mod, 101);
        Assert.Empty(plan.Refused);
        Assert.Single(plan.Writes);
        var outcome = HairPortPanel.SaveByHand(mod.Root, plan, null);
        var (text, failed, _) = HairPortPanel.ResultOf(plan, outcome, removed: false);
        Assert.False(failed);
        Assert.Equal(string.Format(Strings.HairPort.DoneFmt, 1), text.Split('\n')[0]);
        var row = HairPortPanel.ReadRows(mod.Root, Source, null, Game()).Rows.Single(r => r.Race == 101);
        Assert.Equal(HairPortPanel.State.Ported, row.State);
        AssertOwnMaterialsPublished(mod.Root);
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-2] The port's model names its own material; the option redirects that path, and not
    /// the game's shared one, which every other man's own hairstyle 130 still reads unredirected.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-2")]
    public void The_own_material_is_a_path_only_the_port_reads()
    {
        using var mod = WomensMod();
        HandPort(mod, 101);
        var entry = HairPortRecord.Read(mod.Root).Ports.Single();
        Assert.Contains("/mt_c0101h0130_hir_a_port0101.mtrl", SecondSkinWriter.Parse(mod.Get(entry.File)).MatNames);
        var option = mod.Owner("Cut", "Long");
        Assert.NotNull(HairPortPlan.FileFor(option, Own(101)));
        Assert.Equal(Own(101), entry.Materials.Keys.Single());
        Assert.Null(HairPortPlan.FileFor(option, Shared(101)));
        Assert.Equal(Shared(101), Shared(1101));
        Assert.Equal(Shared(101), Shared(1301));
    }

    /// <summary>[AC-hair-material-scalp-fit-10] Whatever was ported together, every own name the saved models name is
    /// published where they are.</summary>
    [Theory]
    [InlineData(new ushort[] { 101 })]
    [InlineData(new ushort[] { 101, 1101, 1301 })]
    [InlineData(new ushort[] { 101, 401, 801 })]
    [Trait("Plan", "AC-hair-material-scalp-fit-10")]
    public void Every_own_named_material_has_its_redirect(ushort[] targets)
    {
        using var mod = WomensMod(["Long", "Short"]);
        HandPort(mod, targets);
        Assert.Equal(targets.Length * 2, HairPortRecord.Read(mod.Root).Ports.Count);
    }

    /// <summary>Another group of the author's publishing the own path stands in its way: always on beside the option,
    /// the two would fight.</summary>
    private static void BlockOwnPath(HairPortFixtures.Mod mod)
    {
        mod.Put("extra.mtrl", [9, 9, 9]);
        CrossSexFixtures.EditMeta(mod, n => n["Groups"]!.AsArray().Add(JsonNode.Parse(
            HairPortFixtures.Group("Extra", "Single", ("On", new Dictionary<string, string> { [Own(101)] = "extra.mtrl" })))));
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-10] [EDGE-hair-material-scalp-fit-24] An own material that cannot be published where
    /// the port is on — the author publishes that path elsewhere, always on with it — refuses the race, with the reason.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-10")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-24")]
    public void An_own_material_that_cannot_be_published_refuses_the_race()
    {
        using var mod = WomensMod();
        BlockOwnPath(mod);
        var plan = Hand(mod, 101);
        Assert.Empty(plan.Writes);
        Assert.Equal(HairPortWriter.Covered(Own(101)), plan.Refused[101]);
    }

    /// <summary>[AC-hair-material-scalp-fit-10] [EDGE-hair-material-scalp-fit-24] A save handed such a port — planned
    /// before the author's update — refuses whole: manifest, record and files as they were.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-10")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-24")]
    public void Save_refuses_a_write_whose_own_material_it_cannot_publish()
    {
        using var mod = WomensMod();
        var plan = Hand(mod, 101);
        Assert.Single(plan.Writes);
        BlockOwnPath(mod);
        var manifest = mod.Manifest;
        var outcome = HairPortWriter.Save(mod.Root, plan.Writes, auto: false);
        Assert.False(outcome.Ok);
        Assert.Equal(HairPortWriter.Covered(Own(101)), outcome.Message);
        Assert.Equal(manifest, mod.Manifest);
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
        Assert.False(Directory.Exists(Path.Combine(mod.Root, HairPortPlan.Folder)));
    }

    /// <summary>[AC-hair-material-scalp-fit-3] Remove ports takes the own material out with the port: no redirect left,
    /// the copied material gone, the record empty.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-3")]
    public void Remove_ports_takes_the_own_material_out()
    {
        using var mod = WomensMod();
        HandPort(mod, 101);
        var copy = HairPortRecord.Read(mod.Root).Ports.Single().Materials[Own(101)];
        Assert.True(mod.Has(copy));
        var outcome = HairPortPanel.RemoveByHand(mod.Root, Source);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Own(101)));
        Assert.False(mod.Has(copy));
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
        Assert.Empty(outcome.Also ?? []);
    }

    /// <summary>[EDGE-hair-material-scalp-fit-5] With no material of the author's anywhere, the game's is copied in under
    /// the own name, and deleted with the port.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-5")]
    public void A_material_from_elsewhere_is_copied_under_the_own_name()
    {
        using var mod = WomensMod();
        HandPort(mod, 101);
        var rel = HairPortRecord.Read(mod.Root).Ports.Single().Materials[Own(101)];
        Assert.Equal(HairPortPlan.PortFile(mod.Owner("Cut", "Long"), Own(101)), rel);
        Assert.Equal(Game()(Shared(201))!, mod.Get(rel));
        HairPortWriter.Undo(mod.Root, _ => true, decline: false);
        Assert.False(mod.Has(rel));
    }

    /// <summary>[EDGE-hair-material-scalp-fit-6] The same hairstyle in two pick-one options: each option gets its own port
    /// and redirect, and undoing one leaves the other's.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-6")]
    public void Two_options_each_get_their_own_redirect()
    {
        using var mod = WomensMod(["Long", "Short"]);
        HandPort(mod, 101);
        Assert.NotNull(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Own(101)));
        Assert.NotNull(HairPortPlan.FileFor(mod.Owner("Cut", "Short"), Own(101)));
        var outcome = HairPortWriter.Undo(mod.Root, e => e.Option == "Long", decline: false);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Own(101)));
        Assert.NotNull(HairPortPlan.FileFor(mod.Owner("Cut", "Short"), Own(101)));
        AssertOwnMaterialsPublished(mod.Root);
    }

    /// <summary>[EDGE-hair-material-scalp-fit-3] Every short material a model names gets its own name and redirect; an
    /// absolute one is left as it is, with none.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-3")]
    public void Every_short_material_gets_its_own_name_and_redirect()
    {
        using var mod = WomensMod();
        mod.Put("long.mdl", SyntheticModel.BuildSurface([],
        [
            HairPortFixtures.Sphere("/mt_c0201h0130_hir_a.mtrl", HairPortFixtures.Head + CrossSexFixtures.Off(201), 0.1f, [("j_kao", 1f)], minY: -0.2f),
            HairPortFixtures.Sphere("/mt_c0201h0130_acc_b.mtrl", HairPortFixtures.Head + CrossSexFixtures.Off(201), 0.105f, [("j_kao", 1f)], minY: 0.2f),
            HairPortFixtures.Sphere("chara/common/texture/abs.mtrl", HairPortFixtures.Head + CrossSexFixtures.Off(201), 0.11f, [("j_kao", 1f)], minY: 0.5f),
        ]));
        var accShared = HairMaterialPath.GamePath(201, Id, "/mt_c0201h0130_acc_b.mtrl")!;
        mod.Put("acc.mtrl", [4, 4]);
        CrossSexFixtures.EditMeta(mod, n => CrossSexFixtures.Option(n, "Long")["Files"]![accShared] = "acc.mtrl");
        HandPort(mod, 101);
        var entry = HairPortRecord.Read(mod.Root).Ports.Single();
        var names = SecondSkinWriter.Parse(mod.Get(entry.File)).MatNames;
        Assert.Contains("/mt_c0101h0130_hir_a_port0101.mtrl", names);
        Assert.Contains("/mt_c0101h0130_acc_b_port0101.mtrl", names);
        Assert.Contains("chara/common/texture/abs.mtrl", names);
        Assert.Equal(new[] { Own(101), HairMaterialPath.GamePath(101, Id, "/mt_c0101h0130_acc_b_port0101.mtrl")! }.Order(),
                     entry.Materials.Keys.Order());
        Assert.Equal("acc.mtrl", entry.Materials[HairMaterialPath.GamePath(101, Id, "/mt_c0101h0130_acc_b_port0101.mtrl")!]);
    }

    /// <summary>[AC-hair-material-scalp-fit-4] The automatic path's remake of a Studio port keeps the same material path
    /// and file.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-4")]
    public void Automatic_remake_keeps_the_studio_ports_material()
    {
        using var mod = WomensMod();
        HandPort(mod, 101);
        var before = HairPortRecord.Read(mod.Root).Ports.Single().Materials.ToDictionary(p => p.Key, p => p.Value);
        mod.Put("long.mdl", CrossSexFixtures.Cap(201, Id, 0.104f));
        var plan = HairPortBuilder.Build(mod.Root, Source, [101], CrossSexFixtures.Pbd(), Game(), withSharers: true);
        Assert.Empty(plan.Refused);
        Assert.Single(plan.Writes);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: true).Ok);
        var after = HairPortRecord.Read(mod.Root).Ports.Single().Materials;
        Assert.Equal(before.OrderBy(p => p.Key), after.OrderBy(p => p.Key));
        Assert.Equal(before[Own(101)], HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Own(101)));
        AssertOwnMaterialsPublished(mod.Root);
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-5] [EDGE-hair-material-scalp-fit-13] Same-sex is as it was: a Miqo'te hairstyle every
    /// other woman reads one material for is refused by hand while they are unported, ported together by the automatic
    /// path under the game's name, and undone together — every one declined, no "also".
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-5")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-13")]
    public void Same_sex_shared_material_is_unchanged()
    {
        var source = HairPortPlan.HairPath(801, 105);
        using var mod = new HairPortFixtures.Mod("[" + HairPortFixtures.Group("Cut", "Single",
            ("Long", new Dictionary<string, string> { [source] = "long.mdl" })) + "]");
        mod.Put("long.mdl", CrossSexFixtures.Cap(801, 105));
        var hand = HairPortBuilder.Build(mod.Root, source, [201], CrossSexFixtures.Pbd(), Game(), withSharers: false);
        Assert.Equal(string.Format(ProteusLoc.Localize("HairPort.Refused.SharedMaterial.Fmt",
                         "its material is the one {0} read for their own hairstyle {1} — port to them too"), "c0401, c1201", 105),
                     hand.Refused[201]);

        var auto = HairPortBuilder.Build(mod.Root, source, [201], CrossSexFixtures.Pbd(), Game(), withSharers: true);
        Assert.Empty(auto.Refused);
        var raced = auto.Writes.Select(w => HairPortPlan.Parse(w.TargetGamePath)!.Value.Race).Order().ToList();
        Assert.Equal(new ushort[] { 201, 401, 1201 }, raced);
        Assert.All(auto.Writes, w => Assert.All(w.Materials, m => Assert.False(HairMaterialPath.IsOwnName(m.GamePath))));
        Assert.True(HairPortWriter.Save(mod.Root, auto.Writes, auto: true).Ok);

        var undone = HairPortWriter.Undo(mod.Root, e => e.TargetGamePath.Contains("c0201"), decline: true);
        Assert.True(undone.Ok, undone.Message);
        Assert.Equal(raced.Count, undone.Count);
        Assert.Empty(undone.Also ?? []);
        Assert.Equal("", undone.Message);
        Assert.Equal(raced.Count, HairPortRecord.Read(mod.Root).Declined.Count);
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-10] [AC-hair-material-scalp-fit-3] [EDGE-hair-material-scalp-fit-25] A hairstyle
    /// served from the default data and, after an update, from an option too: the option's port borrows the default
    /// data's own redirect (it may not publish its own beside the author's file there). Remaking or undoing the default
    /// data's port keeps the redirect while the option's port relies on it; it goes with the last port.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-10")]
    [Trait("Plan", "AC-hair-material-scalp-fit-3")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-25")]
    public void Borrowed_own_redirect_survives_undo_and_remake()
    {
        using var mod = DefaultsThenOption(out var defaults);
        // The option's port borrows: nothing of its own for the own path.
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Own(101)));
        Assert.Equal(2, HairPortRecord.Read(mod.Root).Ports.Count);

        // A remake of the default data's port keeps it.
        mod.Put("default.mdl", CrossSexFixtures.Cap(201, Id, 0.104f));
        var remake = HairPortBuilder.Build(mod.Root, Source, [101], CrossSexFixtures.Pbd(), Game(), withSharers: true);
        Assert.Contains(remake.Writes, w => w.Into.IsDefault);
        Assert.True(HairPortWriter.Save(mod.Root, remake.Writes, auto: true).Ok);
        Assert.NotNull(HairPortPlan.FileFor(mod.Owners.First(o => o.IsDefault), Own(101)));
        AssertOwnMaterialsPublished(mod.Root);

        // Undoing the default data's port alone keeps it: the option's port still draws with it.
        var undone = HairPortWriter.Undo(mod.Root, e => e.Group.Length == 0, decline: false);
        Assert.True(undone.Ok, undone.Message);
        Assert.Single(HairPortRecord.Read(mod.Root).Ports);
        Assert.Equal(defaults, HairPortPlan.FileFor(mod.Owners.First(o => o.IsDefault), Own(101)));
        AssertOwnMaterialsPublished(mod.Root);

        // The last port goes, and the redirect with it.
        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        Assert.Null(HairPortPlan.FileFor(mod.Owners.First(o => o.IsDefault), Own(101)));
    }

    /// <summary>[AC-hair-material-scalp-fit-10] [EDGE-hair-material-scalp-fit-25] An undo of both whose removal of the
    /// option's port fails: that port stays in the record, and the default data's own redirect it relies on stays too.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-10")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-25")]
    public void Borrowed_own_redirect_survives_a_partial_undo()
    {
        using var mod = DefaultsThenOption(out var defaults);
        HairPortWriter.BeforeRemove = (option, remove) =>
        {
            if (option == "Long" && remove.ContainsKey(Target(101))) throw new IOException("locked");
        };
        try
        {
            var outcome = HairPortWriter.Undo(mod.Root, _ => true, decline: false);
            Assert.True(outcome.Ok);
            Assert.Equal(1, outcome.Count);
        }
        finally { HairPortWriter.BeforeRemove = null; }
        var left = Assert.Single(HairPortRecord.Read(mod.Root).Ports);
        Assert.Equal("Long", left.Option);
        Assert.Equal(defaults, HairPortPlan.FileFor(mod.Owners.First(o => o.IsDefault), Own(101)));
        AssertOwnMaterialsPublished(mod.Root);
    }

    /// <summary>A mod whose default data publishes the hairstyle and its material (an author file), ported to c0101; then
    /// an option publishing the hairstyle too, ported again — its port borrowing the default data's own redirect.</summary>
    private static HairPortFixtures.Mod DefaultsThenOption(out string defaults)
    {
        var mod = new HairPortFixtures.Mod("[]", new Dictionary<string, string> { [Source] = "default.mdl", [Shared(201)] = "mat.mtrl" });
        mod.Put("default.mdl", CrossSexFixtures.Cap(201, Id));
        mod.Put("mat.mtrl", [7, 7, 7]);
        HandPort(mod, 101);
        defaults = HairPortPlan.FileFor(mod.Owners.First(o => o.IsDefault), Own(101))!;
        Assert.Equal("mat.mtrl", defaults);

        mod.Put("long.mdl", CrossSexFixtures.Cap(201, Id, 0.102f));
        CrossSexFixtures.EditMeta(mod, n => n["Groups"]!.AsArray().Add(JsonNode.Parse(
            HairPortFixtures.Group("Cut", "Single", ("Long", new Dictionary<string, string> { [Source] = "long.mdl" })))));
        HandPort(mod, 101);
        return mod;
    }

    /// <summary>
    /// [EDGE-hair-material-scalp-fit-27] Sibling options of a group that can have several on, each with its own copy of
    /// the material: ported one after the other, the second is refused (the first's redirect, an author's file, is on
    /// beside it); ported together, each publishes its own, and undoing one keeps the other's.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-27")]
    public void Multi_select_siblings_one_after_another_and_together()
    {
        using (var mod = Siblings(["A"]))
        {
            HandPort(mod, 101);
            Assert.Equal("a.mtrl", HairPortPlan.FileFor(mod.Owner("Cut", "A"), Own(101)));
            AddSibling(mod, "B");
            var plan = Hand(mod, 101);
            Assert.Equal(HairPortWriter.Covered(Own(101)), plan.Refused[101]);
        }
        using (var mod = Siblings(["A", "B"]))
        {
            HandPort(mod, 101);
            Assert.Equal("a.mtrl", HairPortPlan.FileFor(mod.Owner("Cut", "A"), Own(101)));
            Assert.Equal("b.mtrl", HairPortPlan.FileFor(mod.Owner("Cut", "B"), Own(101)));
            Assert.True(HairPortWriter.Undo(mod.Root, e => e.Option == "A", decline: false).Ok);
            Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "A"), Own(101)));
            Assert.Equal("b.mtrl", HairPortPlan.FileFor(mod.Owner("Cut", "B"), Own(101)));
            AssertOwnMaterialsPublished(mod.Root);
        }
    }

    /// <summary>[EDGE-hair-material-scalp-fit-27] [EDGE-hair-material-scalp-fit-25] [AC-hair-material-scalp-fit-10] The
    /// default data and two siblings, ported together so each publishes the own path: undoing the default data's port
    /// keeps each sibling's, and the default data's goes — no port left draws with it.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-27")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-25")]
    [Trait("Plan", "AC-hair-material-scalp-fit-10")]
    public void Undoing_the_default_data_port_keeps_a_siblings_own_redirect()
    {
        using var mod = Siblings(["A", "B"], withDefaults: true);
        HandPort(mod, 101);
        Assert.Equal(3, HairPortRecord.Read(mod.Root).Ports.Count);
        Assert.True(HairPortWriter.Undo(mod.Root, e => e.Group.Length == 0, decline: false).Ok);
        Assert.Equal("a.mtrl", HairPortPlan.FileFor(mod.Owner("Cut", "A"), Own(101)));
        Assert.Equal("b.mtrl", HairPortPlan.FileFor(mod.Owner("Cut", "B"), Own(101)));
        Assert.Null(HairPortPlan.FileFor(mod.Owners.First(o => o.IsDefault), Own(101)));
        AssertOwnMaterialsPublished(mod.Root);
    }

    private static HairPortFixtures.Mod Siblings(string[] options, bool withDefaults = false)
    {
        var defaults = withDefaults
            ? new Dictionary<string, string> { [Source] = "default.mdl", [Shared(201)] = "d.mtrl" }
            : null;
        var mod = new HairPortFixtures.Mod("[" + HairPortFixtures.Group("Cut", "Multi", options.Select(o => (o, Files(o))).ToArray()) + "]",
                                           defaults);
        if (withDefaults) { mod.Put("default.mdl", CrossSexFixtures.Cap(201, Id, 0.099f)); mod.Put("d.mtrl", [3]); }
        for (int i = 0; i < options.Length; i++) PutSibling(mod, options[i], i);
        return mod;
    }

    private static Dictionary<string, string> Files(string option)
        => new() { [Source] = $"{option.ToLowerInvariant()}.mdl", [Shared(201)] = $"{option.ToLowerInvariant()}.mtrl" };

    private static void PutSibling(HairPortFixtures.Mod mod, string option, int i)
    {
        mod.Put($"{option.ToLowerInvariant()}.mdl", CrossSexFixtures.Cap(201, Id, 0.1f + i * 0.002f));
        mod.Put($"{option.ToLowerInvariant()}.mtrl", [(byte)(10 + i)]);
    }

    private static void AddSibling(HairPortFixtures.Mod mod, string option)
    {
        PutSibling(mod, option, 1);
        CrossSexFixtures.EditMeta(mod, n => n["Groups"]![0]!["Options"]!.AsArray().Add(JsonNode.Parse(JsonSerializer.Serialize(new
        {
            Name = option, Description = "", Files = Files(option), FileSwaps = new Dictionary<string, string>(),
            Manipulations = Array.Empty<object>(),
        }))));
    }

    // ── the shared sets build 1430 left ──────────────────────────────────────────────────────────────────────────

    private static readonly ushort[] Men1430 = [101, 301, 501, 701, 901, 1101, 1301, 1701];

    /// <summary>
    /// What build 1430 left in a mod like LM Stryx Rainbow: every man ported together from the default data, each port
    /// recording the shared Midlander-folder redirects of <c>_hir_b</c> and <c>_hir_c</c> (backslashed, as an old record may
    /// hold them), PortVersion 1.
    /// </summary>
    internal static HairPortFixtures.Mod Legacy1430Set()
    {
        var files = new Dictionary<string, string>
        {
            [Source] = "default group\\source.mdl",
            [Shared(101, "b")] = "default group\\mat_b.mtrl",
            [Shared(101, "c")] = "common\\1\\mat_c.mtrl",
            // The author's own materials, where the source reads them: what a remade port carries.
            [Shared(201, "b")] = "default group\\mat_b.mtrl",
            [Shared(201, "c")] = "common\\1\\mat_c.mtrl",
        };
        var record = new HairPortRecord();
        foreach (var race in Men1430)
        {
            var file = $"Hair Ports/Default/{Target(race)}";
            files[Target(race)] = file.Replace('/', '\\');
            record.Ports.Add(new HairPortRecord.Entry
            {
                SourceGamePath = Source, SourceFile = "default group\\source.mdl", SourceSha8 = "00000000",
                TargetGamePath = Target(race), File = file, PortVersion = 1,
                Materials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [Shared(101, "b")] = "default group\\mat_b.mtrl",
                    [Shared(101, "c")] = "common\\1\\mat_c.mtrl",
                },
            });
        }
        var mod = new HairPortFixtures.Mod("[]", files);
        mod.Put("default group/source.mdl", TwoMaterialCap(201));
        mod.Put("default group/mat_b.mtrl", [1]);
        mod.Put("common/1/mat_c.mtrl", [2]);
        foreach (var race in Men1430) mod.Put($"Hair Ports/Default/{Target(race)}", TwoMaterialCap(race));
        HairPortRecord.Write(mod.Root, record);
        return mod;
    }

    private static byte[] TwoMaterialCap(ushort race)
        => SyntheticModel.BuildSurface([],
        [
            HairPortFixtures.Sphere($"/mt_c{race:D4}h{Id:D4}_hir_b.mtrl", HairPortFixtures.Head + CrossSexFixtures.Off(race), 0.1f, [("j_kao", 1f)], minY: -0.2f),
            HairPortFixtures.Sphere($"/mt_c{race:D4}h{Id:D4}_hir_c.mtrl", HairPortFixtures.Head + CrossSexFixtures.Off(race), 0.102f, [("j_kao", 1f)], minY: 0.3f),
        ]);

    private static Func<string, byte[]?> Game1430()
    {
        var files = CrossSexFixtures.GameFiles([.. Races, 501, 901, 1701], [Id]);
        return path => files.TryGetValue(path, out var b) ? b : null;
    }

    private static PbdFile Pbd1430() => new(HairPortFixtures.BuildPbd(
    [
        (101, ushort.MaxValue, new() { ["j_kao"] = System.Numerics.Matrix4x4.Identity }),
        (201, 101, new() { ["j_kao"] = System.Numerics.Matrix4x4.CreateTranslation(0f, 0.06f, 0f) }),
        .. new ushort[] { 301, 501, 701, 901, 1301, 1701 }.Select(r => (r, (ushort)101, new Dictionary<string, System.Numerics.Matrix4x4> { ["j_kao"] = System.Numerics.Matrix4x4.Identity })),
        (1101, 101, new() { ["j_kao"] = System.Numerics.Matrix4x4.CreateTranslation(0f, -0.6f, 0f) }),
    ]));

    /// <summary>Remake one man of the old set, as the watcher does when he is worn: by himself.</summary>
    private static void Remake(HairPortFixtures.Mod mod, ushort race)
    {
        var plan = HairPortBuilder.Build(mod.Root, Source, [race], Pbd1430(), Game1430(), withSharers: true);
        Assert.Empty(plan.Refused);
        var saved = HairPortWriter.Save(mod.Root, plan.Writes, auto: true);
        Assert.True(saved.Ok, saved.Message);
        AssertOwnMaterialsPublished(mod.Root);
    }

    private static HairPortPlan.Owner Defaults(HairPortFixtures.Mod mod) => mod.Owners.First(o => o.IsDefault);

    /// <summary>
    /// [AC-hair-material-scalp-fit-3] [EDGE-hair-material-scalp-fit-8] Midlander M of an old set remade alone takes his
    /// own names — the shared redirects stay for the rest. Undone, he takes every old port holding a redirect in his
    /// folder with him, says so, and only he is declined.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-3")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-8")]
    public void Undoing_a_remade_port_takes_the_old_shared_set_with_it()
    {
        using var mod = Legacy1430Set();
        Assert.Equal(8, HairPortRecord.Read(mod.Root).Ports.Count);
        Remake(mod, 101);
        Assert.NotNull(HairPortPlan.FileFor(Defaults(mod), Shared(101, "b")));
        Assert.NotNull(HairPortPlan.FileFor(Defaults(mod), Own(101, "b")));

        var outcome = HairPortWriter.Undo(mod.Root, e => e.TargetGamePath == Target(101), decline: true);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(8, outcome.Count);
        Assert.Equal(Men1430.Where(r => r != 101).Select(Target).Order(), outcome.Also!.Order());
        Assert.Contains(HairPortWriter.AlsoMessage(outcome.Also!), outcome.Message);
        Assert.Equal([HairPortPlan.DeclineKey(Target(101))], HairPortRecord.Read(mod.Root).Declined);
        Assert.Null(HairPortPlan.FileFor(Defaults(mod), Shared(101, "b")));
        Assert.Null(HairPortPlan.FileFor(Defaults(mod), Shared(101, "c")));
        Assert.Null(HairPortPlan.FileFor(Defaults(mod), Own(101, "b")));
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
    }

    /// <summary>[EDGE-hair-material-scalp-fit-8] Remade one man at a time, the old set keeps its shared redirects until the
    /// last old port is remade, which takes them out.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-8")]
    public void Old_shared_redirect_stays_until_the_last_old_port_is_remade()
    {
        using var mod = Legacy1430Set();
        foreach (var race in Men1430)
        {
            Remake(mod, race);
            bool last = race == Men1430[^1];
            Assert.Equal(!last, HairPortPlan.FileFor(Defaults(mod), Shared(101, "b")) != null);
            Assert.Equal(!last, HairPortPlan.FileFor(Defaults(mod), Shared(101, "c")) != null);
        }
        Assert.All(HairPortRecord.Read(mod.Root).Ports, e => Assert.Equal(HairPortBaker.Version, e.PortVersion));
    }

    /// <summary>[EDGE-hair-material-scalp-fit-9] [EDGE-hair-material-scalp-fit-26] An old port undone takes its old set by
    /// its shared keys — all declined, as before — and no remade man's own-named port.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-9")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-26")]
    public void Undoing_an_old_port_leaves_the_own_named_ports()
    {
        using var mod = Legacy1430Set();
        Remake(mod, 101);
        var outcome = HairPortWriter.Undo(mod.Root, e => e.TargetGamePath == Target(301), decline: true);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(7, outcome.Count);
        Assert.Empty(outcome.Also ?? []);
        Assert.Equal(7, HairPortRecord.Read(mod.Root).Declined.Count);
        var left = Assert.Single(HairPortRecord.Read(mod.Root).Ports);
        Assert.Equal(Target(101), left.TargetGamePath);
        AssertOwnMaterialsPublished(mod.Root);
    }

    /// <summary>[EDGE-hair-material-scalp-fit-26] The folder clause takes nothing it should not: another hairstyle's port
    /// in the same race's hair folder, or the same hairstyle's in another option.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-26")]
    public void Folder_clause_negatives()
    {
        var picked = Entry("", Target(101), Own(101, "b"));
        var otherId = Entry("", HairPortPlan.HairPath(301, 140), Shared(101, "b", 140));
        var otherOption = Entry("Long", Target(301), Shared(101, "b"));
        var ownOther = Entry("", Target(1101), Own(1101, "b"));
        var record = new HairPortRecord { Ports = [picked, otherId, otherOption, ownOther] };
        var set = HairPortWriter.SharerSet(record, [picked]);
        Assert.Equal([(picked, HairPortWriter.Join.Picked)], set);
    }

    /// <summary>[EDGE-hair-material-scalp-fit-26] [AC-hair-material-scalp-fit-5] A port that shares a key with the pick
    /// and also holds a redirect in its folder is Key: declined, never "also". One reached only through a Folder member
    /// is Folder.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-26")]
    [Trait("Plan", "AC-hair-material-scalp-fit-5")]
    public void A_port_reachable_by_key_and_by_folder_is_key()
    {
        var picked = Entry("", Target(101), Own(101, "b"), Shared(101, "c"));
        var both = Entry("", Target(301), Shared(101, "c"));
        var folder = Entry("", Target(501), Shared(101, "b"));
        var throughFolder = Entry("", Target(701), Shared(101, "b"), HairMaterialPath.GamePath(701, 140, "/mt_c0701h0140_x.mtrl")!);
        var record = new HairPortRecord { Ports = [throughFolder, folder, both, picked] };
        var set = HairPortWriter.SharerSet(record, [picked]).ToDictionary(x => x.Entry, x => x.How);
        Assert.Equal(HairPortWriter.Join.Picked, set[picked]);
        Assert.Equal(HairPortWriter.Join.Key, set[both]);
        Assert.Equal(HairPortWriter.Join.Folder, set[folder]);
        Assert.Equal(HairPortWriter.Join.Folder, set[throughFolder]);
    }

    private static HairPortRecord.Entry Entry(string option, string target, params string[] materials) => new()
    {
        Group = option.Length == 0 ? "" : "Cut", Option = option, SourceGamePath = Source, TargetGamePath = target,
        File = $"Hair Ports/{target}", PortVersion = 1,
        Materials = materials.ToDictionary(m => m, m => "x.mtrl", StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>[EDGE-hair-material-scalp-fit-10] An old port across the sexes the mod has since answered with its own men's
    /// model is never remade: its material set stays as build 1430 left it.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-10")]
    public void A_settled_old_cross_sex_port_keeps_its_material()
    {
        using var mod = Legacy1430Set();
        var manEntry = HairPortPlan.HairPath(1301, Id);
        mod.Put("default group/mens.mdl", TwoMaterialCap(1301));
        CrossSexFixtures.EditMeta(mod, n => n["DefaultData"]!["Files"]![manEntry] = "default group\\mens.mdl");
        // The author's own c1301 model takes that race's path: the old port there is gone from the record by itself.
        var plan = HairPortBuilder.Build(mod.Root, Source, [301], Pbd1430(), Game1430(), withSharers: true);
        Assert.Empty(plan.Writes);
        Assert.NotNull(HairPortPlan.FileFor(Defaults(mod), Shared(101, "b")));
        Assert.Contains(HairPortRecord.Read(mod.Root).Ports, e => e.TargetGamePath == Target(301) && e.PortVersion == 1);
    }

    /// <summary>[EDGE-hair-material-scalp-fit-11] An old port the user edited with the brush is not remade: refused for an
    /// edit, its file as it was.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-11")]
    public void An_edited_old_port_is_not_remade()
    {
        using var mod = Legacy1430Set();
        var file = $"Hair Ports/Default/{Target(101)}";
        mod.Put(Path.Combine(SidecarDiscoveryService.SidecarSubdir, MeshVolumeService.BackupSubdir, file), [1]);
        var before = mod.Get(file);
        var plan = HairPortBuilder.Build(mod.Root, Source, [101], Pbd1430(), Game1430(), withSharers: true);
        Assert.Contains((ushort)101, plan.EditBlocked!);
        Assert.Empty(plan.Writes.Where(w => !w.Stripped));
        Assert.Equal(before, mod.Get(file));
    }

    /// <summary>[AC-hair-material-scalp-fit-9] [EDGE-hair-material-scalp-fit-12] A record from build 1430 reads whole, is
    /// out of date, lists its races Stale and ticked in the Studio, and a remake writes the current version.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-9")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-12")]
    public void A_1430_record_is_out_of_date_and_remade()
    {
        using var mod = Legacy1430Set();
        var record = HairPortRecord.Read(mod.Root);
        Assert.Equal(8, record.Ports.Count);
        Assert.All(record.Ports, e => Assert.Equal(2, e.Materials.Count));
        var rows = HairPortPanel.ReadRows(mod.Root, Source, null, Game1430()).Rows;
        Assert.Equal(HairPortPanel.State.Stale, rows.Single(r => r.Race == 101).State);
        Assert.Contains((ushort)101, HairPortPanel.DefaultTicks(rows));
        Remake(mod, 101);
        Assert.Equal(HairPortBaker.Version, HairPortRecord.Read(mod.Root).Ports.Single(e => e.TargetGamePath == Target(101)).PortVersion);
    }
}
