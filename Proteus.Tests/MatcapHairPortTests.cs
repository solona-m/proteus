using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>
/// Matcaps on Proteus's hair ports: a port takes one like any hair, published where its race reads, and a port with a
/// matcap counts as edited (HairPortWriter.EditedBy), so it is neither remade nor undone until the matcap is undone.
/// </summary>
public sealed class MatcapHairPortTests : IDisposable
{
    private static readonly string Source = HairPortPlan.HairPath(801, Id);
    private static readonly string Target = HairPortPlan.HairPath(201, Id);
    private readonly string temp = Path.Combine(Path.GetTempPath(), "proteus-mchp-" + Guid.NewGuid().ToString("N"));
    private readonly TextureLoader loader = new(Substitute.For<Dalamud.Plugin.Services.IDataManager>(),
                                                Substitute.For<Dalamud.Plugin.Services.IPluginLog>());
    private readonly Dictionary<string, byte[]> game = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        try { Directory.Delete(temp, true); } catch { }
    }

    private const string AuthorNormal = "chara/x/author_n.tex";

    /// <summary>A Miqo'te hair mod: two cuts of one hairstyle (each its own port set) and the author's hair material.</summary>
    private static Mod HairMod()
    {
        var mod = new Mod("[" + Group("Cut", "Single",
                              ("Long", new() { [Source] = "long/c0801h0130_hir.mdl" }),
                              ("Short", new() { [Source] = "short/c0801h0130_hir.mdl" })) + "]");
        mod.Put("long/c0801h0130_hir.mdl", HairModel(801));
        mod.Put("short/c0801h0130_hir.mdl", HairModel(801));
        mod.Put("mats/a.mtrl", SyntheticMatcapMod.Gear(AuthorNormal, 0.5f, "hair.shpk"));
        return mod;
    }

    /// <summary>A port of <paramref name="option"/> to <paramref name="race"/>, with a redirect for its hair material at the
    /// path that race reads, to the author's file.</summary>
    private static HairPortWriter.PortWrite Port(Mod mod, string option, ushort race = 201, string? material = null)
    {
        var owner = mod.Owner("Cut", option);
        var target = HairPortPlan.HairPath(race, Id);
        material ??= $"/mt_c{race:D4}h{Id:D4}_hir_a.mtrl";
        var mtrlPath = HairMaterialPath.GamePath(race, Id, material)!;
        return new HairPortWriter.PortWrite(owner, Source, HairPortPlan.FileFor(owner, Source)!, "abcd1234", target,
                                            HairModel(race, material), [new HairPortWriter.MaterialWrite(mtrlPath, "mats/a.mtrl", null)]);
    }

    private static string PortFile(Mod mod, string option, ushort race = 201) =>
        HairPortPlan.FileFor(mod.Owner("Cut", option), HairPortPlan.HairPath(race, Id))!;

    private MatcapStudio.Environment Env() =>
        new(p => p == MatcapMaterial.VanillaTemplatePath ? SyntheticMatcapMod.Reflection() : game.GetValueOrDefault(p),
            loader, false, Path.Combine(temp, "t"), WornFolder: 1);

    private MatcapChoice Image(string name = "gem.tex", byte seed = 0)
    {
        Directory.CreateDirectory(temp);
        var rgba = new byte[64 * 64 * 4];
        for (int i = 0; i < 64 * 64; i++) { rgba[i * 4] = (byte)(i + seed); rgba[i * 4 + 1] = 90; rgba[i * 4 + 2] = 200; rgba[i * 4 + 3] = 255; }
        var path = Path.Combine(temp, name);
        Assert.True(loader.WriteTex(rgba, 64, 64, path));
        return MatcapChoice.OfImage(new MatcapImage(name, path, false));
    }

    private MatcapStudio.Prepared Prepare(Mod mod, string rel, string gamePath, MatcapChoice? choice = null)
    {
        var bytes = mod.Get(rel);
        var parts = ModelPartReader.Read(bytes)!.Parts.Where(p => p.Island < 0 && MatcapPlan.Tickable(p)).ToList();
        Assert.NotEmpty(parts);
        return MatcapStudio.Prepare(mod.Root, rel, gamePath, bytes, parts, choice ?? Image(), Env());
    }

    private MatcapWriter.Outcome Apply(Mod mod, string rel, string gamePath, MatcapChoice? choice = null)
    {
        var p = Prepare(mod, rel, gamePath, choice);
        Assert.True(p.Ok, p.Message);
        return MatcapWriter.Apply(mod.Root, p.Request!);
    }

    private static Dictionary<string, string> Defaults(Mod mod) =>
        PenumbraModMeta.TryReadDefaultData(mod.Root)?.Files ?? new Dictionary<string, string>();

    private static string Blocked => ModelBackupOrder.BlockedMessage(ProteusLoc.Localize("MeshVolume.Feature.Matcaps", "matcaps"));

    /// <summary>Every file in the mod with its bytes plus the manifest: what "untouched" compares.</summary>
    private static Dictionary<string, string> Snapshot(Mod mod) =>
        Directory.EnumerateFiles(mod.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(mod.Root, f), f => HairPortPlan.Sha8(File.ReadAllBytes(f)));

    // ── a port takes a matcap, published where its race reads ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-matcap-hair-ports-1")]
    public void A_same_sex_port_takes_a_matcap_published_where_its_race_reads()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        var written = mod.Get(rel);
        var p = Prepare(mod, rel, Target);
        Assert.True(p.Ok, p.Message);
        var mat = p.Request!.Materials.Single();
        Assert.Equal([HairMaterialPath.GamePath(201, Id, mat.Name)!], mat.GamePaths);
        Assert.StartsWith("chara/human/c0201/obj/hair/h0130/material/v0001/mt_c0201h0130_hir_a_mcap", mat.GamePaths[0]);

        var outcome = MatcapWriter.Apply(mod.Root, p.Request!);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains(mat.Name, SecondSkinWriter.MaterialNames(mod.Get(rel)));
        Assert.True(Defaults(mod).TryGetValue(mat.GamePaths[0], out var file) && mod.Has(file));
        Assert.Equal(written, File.ReadAllBytes(MatcapWriter.BackupPath(mod.Root, MatcapWriter.BackupSubdir, rel)));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-hair-ports-1")]
    public void A_shared_folder_port_takes_a_matcap_in_the_folder_it_reads()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long", 401)], auto: true).Ok);
        var rel = PortFile(mod, "Long", 401);
        var p = Prepare(mod, rel, HairPortPlan.HairPath(401, Id));
        Assert.True(p.Ok, p.Message);
        var mat = p.Request!.Materials.Single();
        Assert.Equal(HairMaterialPath.GamePath(401, Id, mat.Name), Assert.Single(mat.GamePaths));
        Assert.Contains("/c0201/obj/hair/h0130/", mat.GamePaths[0]);      // the Midlander folder c0401 reads
    }

    [Fact]
    [Trait("Plan", "AC-matcap-hair-ports-1")]
    [Trait("Plan", "EDGE-matcap-hair-ports-7")]
    public void A_cross_sex_own_named_port_takes_a_matcap()
    {
        using var mod = HairMod();
        const string own = "/mt_c0101h0130_hir_a_port0101.mtrl";
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long", 101, own)], auto: true).Ok);
        var rel = PortFile(mod, "Long", 101);
        var outcome = Apply(mod, rel, HairPortPlan.HairPath(101, Id));
        Assert.True(outcome.Ok, outcome.Message);
        var mat = MatcapRecord.Read(mod.Root).Materials.Single();
        Assert.StartsWith("/mt_c0101h0130_hir_a_port0101_mcap", mat.Name);
        Assert.False(HairMaterialPath.IsOwnName(mat.Name));               // never in the ports' own-material bookkeeping
        Assert.Equal(HairMaterialPath.GamePath(101, Id, mat.Name), Assert.Single(mat.GamePaths));

        // Undone in order (matcap, then port): nothing of either is left, and the author's redirects are as before.
        Assert.True(MatcapWriter.Undo(mod.Root, rel).Ok);
        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        Assert.DoesNotContain(PenumbraModMeta.ReadAllRedirects(mod.Root), r => r.GamePath.Contains("_mcap") || r.GamePath.Contains("_port"));
        Assert.False(Directory.Exists(Path.Combine(mod.Root, HairPortPlan.Folder)));
        Assert.Equal("long/c0801h0130_hir.mdl",
            PenumbraModMeta.ReadAllRedirects(mod.Root).Single(r => r.GamePath == Source && r.File.StartsWith("long")).File.Replace('\\', '/'));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-hair-ports-2")]
    public void The_apply_line_on_a_port_says_it_and_its_sharers_are_kept()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        const string kept = "doesn't by itself remake the ports of other races that share its material";
        var onPort = Apply(mod, PortFile(mod, "Long"), Target);
        Assert.True(onPort.Ok, onPort.Message);
        Assert.EndsWith("Porting one of those races by hand in the Studio still remakes it.", onPort.Message);
        Assert.Contains(kept, onPort.Message);
        var onAuthor = Apply(mod, "short/c0801h0130_hir.mdl", Source, Image("b.tex", 9));
        Assert.True(onAuthor.Ok, onAuthor.Message);
        Assert.DoesNotContain(kept, onAuthor.Message);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-hair-ports-2")]
    public void PortKept_is_translated_in_every_locale()
    {
        var dir = Path.Combine(DefaultMatcapsDownloadServiceTests.RepoRoot(), "Proteus", "Localization");
        string Message(string lang) =>
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, lang + ".json")))
                .RootElement.GetProperty("Parts.Matcap.PortKept").GetProperty("message").GetString()!;
        var en = Message("en");
        foreach (var lang in new[] { "de", "es", "fr", "ja", "ko", "ru", "zh" })
        {
            var m = Message(lang);
            Assert.False(string.IsNullOrWhiteSpace(m), lang);
            Assert.NotEqual(en, m);
        }
    }

    // ── a matcapped port is kept as it is ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-matcap-hair-ports-3")]
    [Trait("Plan", "EDGE-matcap-hair-ports-10")]
    [Trait("Plan", "EDGE-matcap-hair-ports-12")]
    [Trait("Plan", "EDGE-matcap-hair-ports-13")]
    public void A_matcapped_port_is_not_remade_whatever_asks()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        // A remake planned before the matcap (source changed, bend version up, the Port button): saved after it.
        var remake = Port(mod, "Long") with { Model = HairModel(201, "/mt_x.mtrl") };
        Assert.True(Apply(mod, rel, Target).Ok);
        Assert.Equal(ProteusLoc.Localize("MeshVolume.Feature.Matcaps", "matcaps"), HairPortWriter.EditedBy(mod.Root, rel));
        var before = Snapshot(mod);

        foreach (var auto in new[] { true, false })
        {
            var saved = HairPortWriter.Save(mod.Root, [remake], auto);
            Assert.False(saved.Ok);
            Assert.Contains(Blocked, saved.Message);
        }
        Assert.Equal(before, Snapshot(mod));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-hair-ports-4")]
    [Trait("Plan", "EDGE-matcap-hair-ports-16")]
    public void Removing_ports_leaves_the_matcapped_set_and_its_matcap_whole_and_others_go()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long"), Port(mod, "Short")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        var other = PortFile(mod, "Short");
        Assert.True(Apply(mod, rel, Target).Ok);
        var mat = MatcapRecord.Read(mod.Root).Materials.Single();

        // Only the matcapped set picked: refused outright.
        var alone = HairPortWriter.Undo(mod.Root, e => e.Option == "Long", decline: true);
        Assert.False(alone.Ok);
        Assert.Contains(Blocked, alone.Message);

        // With another set: that one goes, and the answer says why the matcapped one stayed.
        var both = HairPortWriter.Undo(mod.Root, _ => true, decline: true);
        Assert.True(both.Ok, both.Message);
        Assert.Equal(1, both.Count);
        Assert.Contains(Blocked, both.Message);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Short"), Target));
        Assert.False(mod.Has(other));

        Assert.Equal(rel, PortFile(mod, "Long"));
        Assert.True(mod.Has(rel));
        Assert.Contains(HairPortRecord.Read(mod.Root).Ports, e => e.Option == "Long");
        Assert.All(mat.GamePaths, gp => Assert.True(Defaults(mod).ContainsKey(gp)));
        Assert.True(mod.Has(mat.File));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-hair-ports-5")]
    public void Undoing_the_matcap_restores_the_port_and_lets_it_be_remade_and_undone()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        var written = mod.Get(rel);
        Assert.True(Apply(mod, rel, Target).Ok);

        var undo = MatcapWriter.Undo(mod.Root, rel);
        Assert.True(undo.Ok, undo.Message);
        Assert.Equal(written, mod.Get(rel));
        Assert.DoesNotContain(Defaults(mod).Keys, k => k.Contains("_mcap", StringComparison.Ordinal));
        Assert.False(File.Exists(MatcapWriter.BackupPath(mod.Root, MatcapWriter.BackupSubdir, rel)));
        Assert.Null(HairPortWriter.EditedBy(mod.Root, rel));

        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long") with { Model = HairModel(201, null, null) }], auto: true).Ok);
        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        Assert.False(Directory.Exists(Path.Combine(mod.Root, HairPortPlan.Folder)));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-hair-ports-6")]
    public void A_ports_matcap_stays_on_that_option()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long"), Port(mod, "Short")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        var other = PortFile(mod, "Short");
        var otherBytes = mod.Get(other);
        var p = Prepare(mod, rel, Target);
        Assert.True(p.Ok, p.Message);
        Assert.Equal(rel, Assert.Single(p.Request!.Models).Rel);
        Assert.Contains(p.SiblingsSkipped, s => PenumbraModMeta.SameFile(s, other));
        Assert.True(MatcapWriter.Apply(mod.Root, p.Request!).Ok);
        Assert.Equal(otherBytes, mod.Get(other));
        Assert.DoesNotContain(MatcapWriter.ApplyGroupOf(MatcapRecord.Read(mod.Root), rel), g => PenumbraModMeta.SameFile(g, other));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-hair-ports-8")]
    [Trait("Plan", "EDGE-matcap-hair-ports-6")]
    public void A_ports_matcap_reads_the_material_the_port_draws()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long", 401)], auto: true).Ok);
        // The game's own material at that (Midlander) path is something else entirely: never the one read.
        var shared = HairMaterialPath.GamePath(401, Id, "/mt_c0401h0130_hir_a.mtrl")!;
        game[shared] = SyntheticMatcapMod.Gear("chara/x/vanilla_n.tex", 0.1f, "hair.shpk");
        var p = Prepare(mod, PortFile(mod, "Long", 401), HairPortPlan.HairPath(401, Id));
        Assert.True(p.Ok, p.Message);
        var mat = p.Request!.Materials.Single();
        Assert.Equal(AuthorNormal, mat.Normal);
        Assert.Equal(0.5f, mat.AlphaThreshold);
    }

    // ── what counts as a matcap edit ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-1")]
    public void An_unreadable_matcap_record_counts_as_an_edit()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        Assert.True(Apply(mod, rel, Target).Ok);
        File.WriteAllText(Path.Combine(mod.Root, SidecarDiscoveryService.SidecarSubdir, "matcaps.json"), "{ not json");
        Assert.NotNull(HairPortWriter.EditedBy(mod.Root, rel));
        Assert.False(HairPortWriter.Save(mod.Root, [Port(mod, "Long") with { Model = HairModel(201, "/mt_x.mtrl") }], auto: true).Ok);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-1")]
    public void A_matcapped_port_that_cannot_be_read_counts_as_an_edit()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        Assert.True(Apply(mod, rel, Target).Ok);
        mod.Put(rel, [1, 2, 3]);                                    // there, but no model
        Assert.NotNull(HairPortWriter.EditedBy(mod.Root, rel));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-2")]
    public void A_stale_matcap_backup_is_not_an_edit()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        // A backup with no matcap in the port (a failed retire, a deleted record).
        mod.Put($"{SidecarDiscoveryService.SidecarSubdir}/{MatcapWriter.BackupSubdir}/{rel}", mod.Get(rel));
        Assert.Null(HairPortWriter.EditedBy(mod.Root, rel));
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long") with { Model = HairModel(201, null, null) }], auto: true).Ok);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-3")]
    public void A_retired_matcap_backup_is_not_an_edit()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        mod.Put($"{SidecarDiscoveryService.SidecarSubdir}/{MatcapWriter.BackupSubdir}-superseded/{rel}", mod.Get(rel));
        Assert.Null(HairPortWriter.EditedBy(mod.Root, rel));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-11")]
    public void EditedBy_finds_the_matcap_whatever_the_rel_spelling()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        Assert.True(Apply(mod, rel, Target).Ok);
        Assert.NotNull(HairPortWriter.EditedBy(mod.Root, rel.Replace('/', '\\')));
        Assert.NotNull(HairPortWriter.EditedBy(mod.Root, rel.ToUpperInvariant()));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-4")]
    public void A_skeleton_strip_of_a_matcapped_port_still_saves()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        Assert.True(Apply(mod, rel, Target).Ok);
        var matcapped = mod.Get(rel);
        var mat = MatcapRecord.Read(mod.Root).Materials.Single();
        var strip = Port(mod, "Long") with { Stripped = true, StrippedFrom = (0, 0) };
        var saved = HairPortWriter.Save(mod.Root, [strip], auto: true);
        Assert.True(saved.Ok, saved.Message);
        Assert.Equal(matcapped, mod.Get(rel));
        Assert.All(mat.GamePaths, gp => Assert.True(Defaults(mod).ContainsKey(gp)));
    }

    // ── the matcap's own paths ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [Trait("Plan", "EDGE-matcap-hair-ports-5")]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_ports_in_one_shared_folder_get_separate_matcaps(bool midlanderFirst)
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long", 201)], auto: true).Ok);
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long", 401)], auto: true).Ok);
        var a = (Rel: PortFile(mod, "Long", 201), Path: HairPortPlan.HairPath(201, Id));
        var b = (Rel: PortFile(mod, "Long", 401), Path: HairPortPlan.HairPath(401, Id));
        var (first, second) = midlanderFirst ? (a, b) : (b, a);
        Assert.True(Apply(mod, first.Rel, first.Path).Ok);
        Assert.True(Apply(mod, second.Rel, second.Path, Image("c.tex", 3)).Ok);

        var mats = MatcapRecord.Read(mod.Root).Materials;
        Assert.Equal(2, mats.Count);
        Assert.Empty(mats[0].GamePaths.Intersect(mats[1].GamePaths, StringComparer.OrdinalIgnoreCase));
        Assert.All(mats.SelectMany(m => m.GamePaths), gp => Assert.True(Defaults(mod).ContainsKey(gp)));

        Assert.True(MatcapWriter.Undo(mod.Root, first.Rel).Ok);
        var left = MatcapRecord.Read(mod.Root).Materials.Single();
        Assert.All(left.GamePaths, gp => Assert.True(Defaults(mod).ContainsKey(gp)));
        Assert.True(mod.Has(left.File));
        Assert.Contains(left.Name, SecondSkinWriter.MaterialNames(mod.Get(second.Rel)));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-7")]
    public void Another_ports_remake_or_undo_never_takes_a_matcap_redirect()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long", 101, "/mt_c0101h0130_hir_a_port0101.mtrl"),
                                                    Port(mod, "Short", 101, "/mt_c0101h0130_hir_a_port0101.mtrl")], auto: true).Ok);
        var rel = PortFile(mod, "Long", 101);
        Assert.True(Apply(mod, rel, HairPortPlan.HairPath(101, Id)).Ok);
        var mat = MatcapRecord.Read(mod.Root).Materials.Single();
        // The other option's port remade, then undone: own-material bookkeeping runs, and leaves the matcap alone.
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Short", 101, "/mt_c0101h0130_hir_a_port0101.mtrl")
                                                       with { Model = HairModel(101, "/mt_c0101h0130_hir_a_port0101.mtrl", null) }], auto: true).Ok);
        HairPortWriter.Undo(mod.Root, e => e.Option == "Short", decline: false);
        Assert.All(mat.GamePaths, gp => Assert.True(Defaults(mod).ContainsKey(gp)));
    }

    // ── gone, raced, layered, changed ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-8")]
    public void A_matcapped_port_that_is_gone_is_forgotten()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        Assert.True(Apply(mod, rel, Target).Ok);
        var mat = MatcapRecord.Read(mod.Root).Materials.Single();
        File.Delete(Path.Combine(mod.Root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var undo = MatcapWriter.Undo(mod.Root, rel);
        Assert.True(undo.Ok, undo.Message);
        Assert.Empty(MatcapRecord.Read(mod.Root).Materials);
        Assert.DoesNotContain(Defaults(mod).Keys, k => k.Contains("_mcap", StringComparison.Ordinal));
        Assert.False(mod.Has(mat.File));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-9")]
    public void A_port_remade_while_the_matcap_was_prepared_is_not_written()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        var p = Prepare(mod, rel, Target);
        Assert.True(p.Ok, p.Message);
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long") with { Model = HairModel(201, null, null, SphereExtra()) }], auto: true).Ok);
        var before = Snapshot(mod);
        var outcome = MatcapWriter.Apply(mod.Root, p.Request!);
        Assert.False(outcome.Ok);
        Assert.Equal(MatcapWriter.CodeEdited, outcome.Code);
        Assert.Equal(before, Snapshot(mod));
    }

    private static SyntheticModel.SurfaceMesh SphereExtra() => Ear(201);

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-14")]
    public void The_brush_on_a_ports_matcap_keeps_it_kept_and_orders_undo()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        Assert.True(Apply(mod, rel, Target).Ok);
        var brushBackup = $"{SidecarDiscoveryService.SidecarSubdir}/{MeshVolumeService.BackupSubdir}/{rel}";
        mod.Put(brushBackup, mod.Get(rel));                       // a later backup time than the matcap's
        var brushed = mod.Get(rel);
        brushed[^1] ^= 0x5A;
        mod.Put(rel, brushed);

        Assert.NotNull(HairPortWriter.EditedBy(mod.Root, rel));
        Assert.False(HairPortWriter.Save(mod.Root, [Port(mod, "Long") with { Model = HairModel(201, "/mt_x.mtrl") }], auto: true).Ok);
        Assert.Equal(MatcapWriter.CodeLaterFeature, MatcapWriter.Undo(mod.Root, rel).Code);

        mod.Put(rel, mod.Get(brushBackup));
        File.Delete(Path.Combine(mod.Root, brushBackup.Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(MatcapWriter.Undo(mod.Root, rel).Ok);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-15")]
    public void Changing_a_ports_matcap_leaves_the_port_alone()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        Assert.True(Apply(mod, rel, Target).Ok);
        var matcapped = mod.Get(rel);
        var mat = MatcapRecord.Read(mod.Root).Materials.Single();
        var c = MatcapStudio.PrepareChange(mod.Root, mat.Name, Image("other.tex", 77), Env());
        Assert.NotNull(c.Source);
        Assert.True(MatcapWriter.Change(mod.Root, mat.Name, c.Source!, c.Material, c.TexTemp).Ok);
        Assert.Equal(matcapped, mod.Get(rel));
        Assert.NotNull(HairPortWriter.EditedBy(mod.Root, rel));
    }

    // ── sharers, the Port button, carried matcaps, fresh ports, the material's own place ──────────────────────────────

    /// <summary>A Miqo'te's own hairstyle 105, ported to Hyur Midlander and Highlander: both read one Midlander material.</summary>
    private (Mod Mod, string Source, Func<string, byte[]?> Game) SharedStyle()
    {
        const int id = 105;
        string source = HairPortPlan.HairPath(801, id);
        var mod = new Mod(defaults: new Dictionary<string, string> { [source] = "a.mdl" });
        mod.Put("a.mdl", HairModel(801, material: "/mt_c0801h0105_hir_a.mtrl"));
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["chara/human/c0801/obj/hair/h0105/material/v0001/mt_c0801h0105_hir_a.mtrl"] = SyntheticMatcapMod.Gear(AuthorNormal, 0.5f, "hair.shpk"),
        };
        foreach (ushort r in new ushort[] { 101, 201, 401, 601, 801, 1001, 1101, 1201, 1401, 1601, 1801 })
        {
            files[HairPortPlan.HeadPaths(r).First()] = HeadModel(r);
            files[HairPortPlan.HairPath(r, id)] = HairModel(r);
        }
        foreach (var (k, v) in files) game[k] = v;
        Func<string, byte[]?> g = p => files.GetValueOrDefault(p);
        Assert.True(HairPortWriter.Save(mod.Root, HairPortBuilder.Build(mod.Root, source, [201], Pbd(), g, true).Writes, true).Ok);
        return (mod, source, g);
    }

    private static void ChangeAuthor(Mod mod) =>
        mod.Put("a.mdl", HairModel(801, material: "/mt_c0801h0105_hir_a.mtrl", extra: Sphere("/mt_c0801h0105_hir_a.mtrl",
            Head + Offset(801), 0.105f, [("j_kao", 1f)], minY: 0.5f)));

    private static string PortOf(Mod mod, ushort race) =>
        HairPortRecord.Read(mod.Root).Ports.Single(e => e.TargetGamePath == HairPortPlan.HairPath(race, 105)).File;

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-12")]
    public void A_matcapped_port_freezes_the_races_sharing_its_material_with_no_verdict()
    {
        var (mod, source, g) = SharedStyle();
        using var _ = mod;
        var highlander = PortOf(mod, 401);
        Assert.True(Apply(mod, highlander, HairPortPlan.HairPath(401, 105)).Ok);
        ChangeAuthor(mod);

        var plan = HairPortBuilder.Build(mod.Root, source, [201], Pbd(), g, withSharers: true);
        Assert.Contains((ushort)401, plan.EditBlocked);
        Assert.Contains((ushort)201, plan.EditBlocked);               // the sharer goes with it
        Assert.Contains(Blocked, plan.Refused[401]);
        Assert.False(plan.IsVerdict(401));
        Assert.False(plan.IsVerdict(201));
        Assert.DoesNotContain(plan.Writes, w => w.TargetGamePath == HairPortPlan.HairPath(401, 105));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-19")]
    public void The_port_button_still_remakes_an_unmatcapped_sharer_alone()
    {
        var (mod, source, g) = SharedStyle();
        using var _ = mod;
        var highlander = PortOf(mod, 401);
        Assert.True(Apply(mod, highlander, HairPortPlan.HairPath(401, 105)).Ok);
        var before = mod.Get(highlander);
        var matcapKeys = Defaults(mod).Where(kv => kv.Key.Contains("_mcap")).ToList();
        ChangeAuthor(mod);

        // By hand, for the Midlander only (no sharers): remade; the matcapped Highlander port is untouched.
        var plan = HairPortBuilder.Build(mod.Root, source, [201], Pbd(), g, withSharers: false);
        var w = Assert.Single(plan.Writes);
        Assert.Equal(HairPortPlan.HairPath(201, 105), w.TargetGamePath);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);
        Assert.Equal(before, mod.Get(highlander));
        Assert.Equal(matcapKeys, Defaults(mod).Where(kv => kv.Key.Contains("_mcap")).ToList());
    }

    [Theory]
    [Trait("Plan", "EDGE-matcap-hair-ports-17")]
    [InlineData(false)]
    [InlineData(true)]
    public void A_port_naming_only_an_authors_matcap_is_not_frozen_and_is_forgotten(bool forget)
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        Assert.True(Apply(mod, rel, Target).Ok);                                         // M1, the port's own
        Assert.True(Apply(mod, "long/c0801h0130_hir.mdl", Source, Image("m2.tex", 5)).Ok); // M2, the author's
        var record = MatcapRecord.Read(mod.Root);
        var m1 = record.MaterialsOf(rel).Single();
        var m2 = record.MaterialsOf("long/c0801h0130_hir.mdl").Single();

        // The port as if remade from the matcapped author file: it names the author's M2 only.
        var carried = HairModel(201, $"/{m2.Name.TrimStart('/')}");
        mod.Put(rel, carried);
        Assert.Null(HairPortWriter.EditedBy(mod.Root, rel));

        var outcome = forget ? MatcapWriter.Forget(mod.Root, rel) : MatcapWriter.Undo(mod.Root, rel);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(carried, mod.Get(rel));                                             // not restored
        var after = MatcapRecord.Read(mod.Root);
        Assert.DoesNotContain(after.Materials, m => m.Name == m1.Name);
        Assert.Contains(after.Materials, m => m.Name == m2.Name);
        Assert.All(m2.GamePaths, gp => Assert.True(Defaults(mod).ContainsKey(gp)));
        Assert.DoesNotContain(m1.GamePaths, gp => Defaults(mod).ContainsKey(gp));
        Assert.False(File.Exists(MatcapWriter.BackupPath(mod.Root, MatcapWriter.BackupSubdir, rel)));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-18")]
    public void A_fresh_port_over_a_matcapped_file_is_superseded_and_cleaned()
    {
        using var mod = HairMod();
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        var rel = PortFile(mod, "Long");
        Assert.True(Apply(mod, rel, Target).Ok);
        var m1 = MatcapRecord.Read(mod.Root).Materials.Single();

        // The port's redirect removed by hand: the ports record forgets it, and the next port lands on the same file.
        PenumbraModMeta.RemoveOptionRedirects(mod.Root, "Cut", "Long", new Dictionary<string, string> { [Target] = rel });
        Assert.DoesNotContain(HairPortRecord.Read(mod.Root).Ports, e => e.Option == "Long");
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long")], auto: true).Ok);
        Assert.Equal(rel, PortFile(mod, "Long"));
        var fresh = mod.Get(rel);
        Assert.Equal(MatcapWriter.State.Superseded, MatcapWriter.Detect(mod.Root)[m1.Name]);
        foreach (var name in SecondSkinWriter.MaterialNames(fresh))
            Assert.False(MatcapPlan.IsMatcapName(name), name);

        // A matcap on the fresh port retires the stale backup first: its new backup is the fresh port.
        Assert.True(Apply(mod, rel, Target, Image("n.tex", 4)).Ok);
        Assert.Equal(fresh, File.ReadAllBytes(MatcapWriter.BackupPath(mod.Root, MatcapWriter.BackupSubdir, rel)));
        Assert.True(Directory.EnumerateFiles(Path.Combine(mod.Root, SidecarDiscoveryService.SidecarSubdir, MatcapWriter.BackupSubdir + "-superseded"),
                                             "*", SearchOption.AllDirectories).Any());

        // Undo clears the old matcap's files and redirects along with the new one.
        Assert.True(MatcapWriter.Undo(mod.Root, rel).Ok);
        Assert.DoesNotContain(Defaults(mod).Keys, k => k.Contains("_mcap", StringComparison.Ordinal));
        Assert.False(mod.Has(m1.File));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-hair-ports-20")]
    public void SourceMaterial_prefers_the_models_own_place()
    {
        using var mod = HairMod();
        mod.Put("mats/b.mtrl", SyntheticMatcapMod.Gear("chara/x/short_n.tex", 0.25f, "hair.shpk"));
        var shortPort = Port(mod, "Short");
        shortPort = shortPort with { Materials = [new HairPortWriter.MaterialWrite(shortPort.Materials[0].GamePath, "mats/b.mtrl", null)] };
        Assert.True(HairPortWriter.Save(mod.Root, [Port(mod, "Long"), shortPort], auto: true).Ok);

        var onShort = Prepare(mod, PortFile(mod, "Short"), Target);
        Assert.True(onShort.Ok, onShort.Message);
        Assert.Equal("chara/x/short_n.tex", onShort.Request!.Materials.Single().Normal);
        var onLong = Prepare(mod, PortFile(mod, "Long"), Target);
        Assert.Equal(AuthorNormal, onLong.Request!.Materials.Single().Normal);
    }
}
