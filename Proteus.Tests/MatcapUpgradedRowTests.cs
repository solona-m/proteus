using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NSubstitute;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A legacy accessory (the Enaretos bracelet's paths, <c>a0153</c> wrs) upgraded by the real gear shader upgrade, then
/// given a row matcap. Layouts: everything in default data (normal and index from the game); the Enaretos layout (material,
/// normal and index in one option of a Single group, the model in another group's options); two options holding the
/// material (identical bytes in different files).
/// </summary>
internal sealed class UpgradedRowFixture : IDisposable
{
    public enum Layout { Default, Enaretos, TwoOptions }

    public const string ModelPath = "chara/accessory/a0153/model/c0201a0153_wrs.mdl";
    public const string Mat = "/mt_c0201a0153_wrs_a.mtrl";
    public static string MatPath(int folder = 1) => $"chara/accessory/a0153/material/v{folder:D4}{Mat}";
    public const int Size = RowFixture.Size;

    public readonly ShaderUpgradeTestKit Kit = new();
    public readonly string Temp = Path.Combine(Path.GetTempPath(), "proteus-urow-" + Guid.NewGuid().ToString("N")[..8]);
    public readonly TextureLoader Loader = new(Substitute.For<Dalamud.Plugin.Services.IDataManager>(),
                                               Substitute.For<Dalamud.Plugin.Services.IPluginLog>());
    public readonly Dictionary<string, byte[]> Game = new(StringComparer.OrdinalIgnoreCase);
    public string ModRoot { get; }
    public string ModelRel { get; }
    /// <summary>The upgraded material file(s): the worn one first.</summary>
    public List<string> MatRels { get; } = [];
    public string MatRel => MatRels[0];
    public byte[] Legacy { get; }
    public bool FormB { get; }
    public string NormalPath { get; private set; } = "";
    public string IndexPath { get; private set; } = "";

    public UpgradedRowFixture(bool formB = false, Layout layout = Layout.Default, string name = "Bracelet", bool upgradeAll = true)
    {
        Directory.CreateDirectory(Temp);
        FormB = formB;
        Legacy = formB ? ShaderUpgradeTestKit.FormB() : ShaderUpgradeTestKit.FormA();
        var model = SyntheticModel.Build([], [new SyntheticModel.Mesh(Mat, new SyntheticModel.Sub(0, Corners: RowFixture.Corners()))],
                                         SyntheticModel.V6, (IReadOnlyList<string>?)null);
        var spec = formB ? ShaderUpgradeTestKit.SpecPath : null;
        switch (layout)
        {
            case Layout.Enaretos:
            {
                var opt = new Dictionary<string, string> { [MatPath()] = "enaretos bracelet\\wrs_a.mtrl" };
                if (spec != null) opt[spec] = "enaretos bracelet\\wrs_s.tex";
                ModRoot = Kit.Mod(name, null,
                    ("Enaretos Bracelet", "Single", [("Everything", opt)]),
                    ("Body Shape", "Single", [("Bibo+", new Dictionary<string, string> { [ModelPath] = "body shape\\bibo+\\wrs.mdl" }),
                                              ("Rue+", new Dictionary<string, string> { [ModelPath] = "body shape\\rue+\\wrs.mdl" })]));
                ModelRel = "body shape/bibo+/wrs.mdl";
                ShaderUpgradeTestKit.Put(ModRoot, "body shape/rue+/wrs.mdl", model);
                MatRels.Add("enaretos bracelet/wrs_a.mtrl");
                if (spec != null) ShaderUpgradeTestKit.Put(ModRoot, "enaretos bracelet/wrs_s.tex", ShaderUpgradeTestKit.Fixture("legacy_spec_s.tex"));
                break;
            }
            case Layout.TwoOptions:
            {
                var red = new Dictionary<string, string> { [MatPath()] = "red\\wrs_a.mtrl" };
                var blue = new Dictionary<string, string> { [MatPath()] = "blue\\wrs_a.mtrl" };
                if (spec != null) { red[spec] = "tex\\wrs_s.tex"; blue[spec] = "tex\\wrs_s.tex"; }
                ModRoot = Kit.Mod(name, new Dictionary<string, string> { [ModelPath] = "model\\wrs.mdl" },
                    ("Colour", "Single", [("Red", red), ("Blue", blue)]));
                ModelRel = "model/wrs.mdl";
                MatRels.Add("red/wrs_a.mtrl");
                MatRels.Add("blue/wrs_a.mtrl");
                if (spec != null) ShaderUpgradeTestKit.Put(ModRoot, "tex/wrs_s.tex", ShaderUpgradeTestKit.Fixture("legacy_spec_s.tex"));
                break;
            }
            default:
            {
                var defaults = new Dictionary<string, string> { [ModelPath] = "model\\wrs.mdl", [MatPath()] = "mat\\wrs_a.mtrl" };
                if (spec != null) defaults[spec] = "tex\\wrs_s.tex";
                ModRoot = Kit.Mod(name, defaults);
                ModelRel = "model/wrs.mdl";
                MatRels.Add("mat/wrs_a.mtrl");
                if (spec != null) ShaderUpgradeTestKit.Put(ModRoot, "tex/wrs_s.tex", ShaderUpgradeTestKit.Fixture("legacy_spec_s.tex"));
                break;
            }
        }
        ShaderUpgradeTestKit.Put(ModRoot, ModelRel, model);
        foreach (var rel in MatRels) ShaderUpgradeTestKit.Put(ModRoot, rel, Legacy);

        foreach (var rel in upgradeAll ? MatRels : MatRels.Take(1))
            Assert.True(Upgrade(rel).ChangedAny, "the fixture's upgrade wrote nothing for " + rel);

        var h = MatcapMaterial.ReadHeader(ShaderUpgradeTestKit.Get(ModRoot, MatRel))!;
        Assert.Equal(MatcapRowCut.CutShader, h.Shader);
        NormalPath = h.TextureOf(MatcapMaterial.SamplerNormal)!;
        IndexPath = h.TextureOf(ShaderUpgradePlan.SamplerIndex) ?? "";
        var normal = Tex(RowFixture.NormalRgba());
        var index = Tex(RowFixture.IndexRgba());
        if (layout == Layout.Enaretos)
        {
            // The realistic legacy layout: the maps ship in the material's own option.
            ShaderUpgradeTestKit.Put(ModRoot, "enaretos bracelet/wrs_n.tex", normal);
            var add = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [NormalPath] = "enaretos bracelet\\wrs_n.tex" };
            if (IndexPath.Length > 0)
            {
                ShaderUpgradeTestKit.Put(ModRoot, "enaretos bracelet/wrs_id.tex", index);
                add[IndexPath] = "enaretos bracelet\\wrs_id.tex";
            }
            PenumbraModMeta.MergeOptionRedirects(ModRoot, "Enaretos Bracelet", "Everything", add);
        }
        else
        {
            Game[NormalPath] = normal;
            if (IndexPath.Length > 0) Game[IndexPath] = index;
        }
    }

    public ShaderUpgradeWriter.Outcome Upgrade(string rel)
    {
        var spec = FormB ? (MatRels.Contains("enaretos bracelet/wrs_a.mtrl") ? "enaretos bracelet/wrs_s.tex" : "tex/wrs_s.tex") : null;
        return Kit.Upgrade(ModRoot, [ShaderUpgradeTestKit.Worn(MatPath(), ModRoot, rel)],
                           spec == null ? null : _ => Path.Combine(ModRoot, spec.Replace('/', Path.DirectorySeparatorChar)));
    }

    public void Dispose()
    {
        MatcapRowCut.AfterClassify = null;
        MatcapWriter.AfterStep = null;
        Kit.Dispose();
        try { Directory.Delete(Temp, true); } catch { }
    }

    public byte[] Tex(byte[] rgba)
    {
        var path = Path.Combine(Temp, Guid.NewGuid().ToString("N") + ".tex");
        Assert.True(Loader.WriteTex(rgba, Size, Size, path));
        return File.ReadAllBytes(path);
    }

    public MatcapStudio.Environment Env(bool? gearShadersOn = true) =>
        new(p => p == MatcapMaterial.VanillaTemplatePath ? SyntheticMatcapMod.Reflection() : Game.GetValueOrDefault(p),
            Loader, false, Temp, WornFolder: 1, RowOnly: 15, RowSub: SubRowPick.B, Affordable: () => false, GearShadersOn: gearShadersOn);

    public MatcapImage Image()
    {
        var rgba = new byte[32 * 32 * 4];
        for (int i = 0; i < 32 * 32; i++) { rgba[i * 4] = (byte)i; rgba[i * 4 + 1] = 90; rgba[i * 4 + 2] = 200; rgba[i * 4 + 3] = 255; }
        var path = Path.Combine(Temp, "gem.tex");
        if (!File.Exists(path)) Assert.True(Loader.WriteTex(rgba, 32, 32, path));
        return new MatcapImage("gem.tex", path, false);
    }

    public byte[] Current() => File.ReadAllBytes(MatcapWriter.Full(ModRoot, ModelRel));

    public IReadOnlyList<ModelPart> Pool() =>
        MatcapPlan.RowPool(ModelPartReader.Read(Current())!.Parts, MatcapPlan.Kind.Gear, [], Mat);

    public MatcapStudio.Prepared Prepare(MatcapStudio.Environment? env = null, bool withSiblings = true) =>
        MatcapStudio.Prepare(ModRoot, ModelRel, ModelPath, Current(), Pool(), MatcapChoice.OfImage(Image()), env ?? Env(),
                             withSiblings: withSiblings);

    public MatcapWriter.Outcome Apply(MatcapStudio.Environment? env = null)
    {
        var p = Prepare(env);
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        var o = MatcapWriter.Apply(ModRoot, p.Request!);
        Assert.True(o.Ok, o.Code + ": " + o.Message);
        return o;
    }

    public ShaderUpgradeWriter.Undone RevertUpgrade(string? rel = null, bool decline = false) =>
        ShaderUpgradeWriter.RevertOrForget(ModRoot, rel ?? MatRel, decline, out LastMessage);

    public string LastMessage = "";

    /// <summary>Every file (the manifest by content) → hash; the superseded archives aside.</summary>
    public Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(ModRoot, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("-superseded"))
            .ToDictionary(f => Path.GetRelativePath(ModRoot, f), f => Path.GetFileName(f) == "meta.json"
                ? HairPortPlan.Sha8(System.Text.Encoding.UTF8.GetBytes(JsonNode.Parse(File.ReadAllText(f))!.ToJsonString()))
                : HairPortPlan.Sha8(File.ReadAllBytes(f)));

    public static void SameAs(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        Assert.Equal(before.Keys.Order(StringComparer.OrdinalIgnoreCase), after.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (k, v) in before) Assert.True(v == after[k], $"{k} changed");
    }

    public ShaderUpgradeWriter.FileEntry Entry(string? rel = null) =>
        ShaderUpgradeWriter.ReadRecord(ModRoot)!.Files.Single(f => f.Rel == (rel ?? MatRel));

    public string Matcap(MatcapRowCut.RowHoldKind kind, string model, string? later = null, string? rel = null) =>
        ShaderUpgradeWriter.RowMatcapMessage(rel ?? MatRel, new MatcapRowCut.RowHold(kind, model, later));

    /// <summary>A later feature's backup of the model (the brush), newer than the matcap's, and the model changed.</summary>
    public void BrushModel()
    {
        var full = MatcapWriter.Full(ModRoot, ModelRel);
        var backup = Path.Combine(ModRoot, "Proteus", MeshVolumeService.BackupSubdir, ModelRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(full, backup, true);
        File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddMinutes(5));
        File.AppendAllText(full, "\0\0\0\0");
    }

    /// <summary>The model edited on top of the matcap (no later backup): still names it, other bytes.</summary>
    public void EditModelOnTop() => File.AppendAllText(MatcapWriter.Full(ModRoot, ModelRel), "\0\0\0\0");

    /// <summary>The author's update: the pre-matcap model back (names none of ours); backup and record kept.</summary>
    public void SupersedeModel()
    {
        var backup = Path.Combine(ModRoot, "Proteus", MatcapWriter.BackupSubdir, ModelRel.Replace('/', Path.DirectorySeparatorChar));
        File.Copy(backup, MatcapWriter.Full(ModRoot, ModelRel), true);
    }

    /// <summary>
    /// The state an Undo leaves when refits or hair ports still draw the matcap (#96): the model restored, its backup and
    /// model record gone, the material entry kept.
    /// </summary>
    public void KeptForPorts()
    {
        var backup = Path.Combine(ModRoot, "Proteus", MatcapWriter.BackupSubdir, ModelRel.Replace('/', Path.DirectorySeparatorChar));
        File.Copy(backup, MatcapWriter.Full(ModRoot, ModelRel), true);
        File.Delete(backup);
        var path = MatcapRecord.PathOf(ModRoot);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["Models"] = new JsonArray();
        File.WriteAllText(path, json.ToJsonString());
    }
}

/// <summary>
/// Row matcaps on materials the gear shader upgrade moved to character.shpk, and the upgrade's undo held while one
/// depends on them (Sensitive writers: ShaderUpgradeWriter, MatcapWriter — in the quick suite).
/// </summary>
public class MatcapUpgradedRowTests
{
    private static List<PenumbraModMeta.ContainerRedirect> Redirects(string mod) => PenumbraModMeta.ReadContainerRedirects(mod);

    [Theory]
    [Trait("Plan", "AC-upgrade-vs-matcap-1")]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-1")]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-20")]
    [InlineData(false, UpgradedRowFixture.Layout.Default)]
    [InlineData(true, UpgradedRowFixture.Layout.Default)]
    [InlineData(false, UpgradedRowFixture.Layout.Enaretos)]
    [InlineData(true, UpgradedRowFixture.Layout.Enaretos)]
    internal void Row_matcap_applies_on_an_upgraded_legacy_accessory(bool formB, UpgradedRowFixture.Layout layout)
    {
        using var f = new UpgradedRowFixture(formB, layout);
        var entry = f.Entry();
        var upgradeFiles = new[] { f.MatRel, entry.Mask?.Rel }.OfType<string>()
            .Concat(Directory.EnumerateFiles(Path.Combine(f.ModRoot, "Proteus", ShaderUpgradeWriter.BackupSubdir), "*", SearchOption.AllDirectories)
                             .Select(p => Path.GetRelativePath(f.ModRoot, p).Replace('\\', '/')))
            .Append("Proteus/" + ShaderUpgradeWriter.RecordFile).ToList();
        var before = upgradeFiles.ToDictionary(r => r, r => HairPortPlan.Sha8(ShaderUpgradeTestKit.Get(f.ModRoot, r)));
        var maskRedirects = Redirects(f.ModRoot).Where(r => entry.Mask != null && PenumbraModMeta.SameFile(r.GamePath, entry.Mask.GamePath)).ToList();

        f.Apply();

        var row = MatcapRecord.Read(f.ModRoot).Materials.Single().Row!;
        var file = Assert.Single(row.Files);
        Assert.True(PenumbraModMeta.SameFile(f.MatRel, file.Source.File!));
        if (layout == UpgradedRowFixture.Layout.Enaretos)
        {
            Assert.True(PenumbraModMeta.SameFile("enaretos bracelet/wrs_n.tex", file.Normal.File!));
            Assert.Equal("Everything", Assert.Single(file.Redirects).Option);
        }
        // The cut copy keeps the upgrade's textures, its derived mask included.
        var cutTextures = MatcapMaterial.ReadHeader(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, file.File)))!.Textures;
        if (formB) Assert.Contains(cutTextures, t => string.Equals(t, entry.Mask!.GamePath, StringComparison.OrdinalIgnoreCase));
        // Nothing of the upgrade's changed.
        foreach (var (rel, sha) in before) Assert.Equal(sha, HairPortPlan.Sha8(ShaderUpgradeTestKit.Get(f.ModRoot, rel)));
        foreach (var r in maskRedirects) Assert.Contains(Redirects(f.ModRoot), x => x.Container.SameAs(r.Container) && x.GamePath == r.GamePath && x.File == r.File);
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-20")]
    public void Rival_option_normal_refuses_texture_varies()
    {
        using var f = new UpgradedRowFixture(layout: UpgradedRowFixture.Layout.Enaretos);
        ShaderUpgradeTestKit.Put(f.ModRoot, "rival/n.tex", f.Tex(RowFixture.NormalRgba(blue: 200)));
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(f.ModRoot, "meta.json")))!.AsObject();
        meta["Groups"]!.AsArray().Add(new JsonObject
        {
            ["Name"] = "Extras", ["Type"] = "Multi", ["Priority"] = 0, ["DefaultSettings"] = 0,
            ["Options"] = new JsonArray(new JsonObject { ["Name"] = "Shiny", ["Files"] = new JsonObject { [f.NormalPath] = "rival\\n.tex" },
                                                         ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray() }),
        });
        File.WriteAllText(Path.Combine(f.ModRoot, "meta.json"), meta.ToJsonString());
        var before = f.Snapshot();
        var p = f.Prepare();
        Assert.Equal(MatcapRowCut.CodeTextureVaries, p.Code);
        UpgradedRowFixture.SameAs(before, f.Snapshot());
    }

    [Theory]
    [Trait("Plan", "AC-upgrade-vs-matcap-3")]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-11")]
    [InlineData(false)]
    [InlineData(true)]
    public void Undo_of_an_upgrade_a_row_matcap_was_cut_from_is_refused(bool decline)
    {
        using var f = new UpgradedRowFixture(formB: true);
        f.Apply();
        var before = f.Snapshot();
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade(decline: decline));
        Assert.Equal(f.Matcap(MatcapRowCut.RowHoldKind.Active, f.ModelRel), f.LastMessage);
        Assert.Contains(f.MatRel, f.LastMessage);
        UpgradedRowFixture.SameAs(before, f.Snapshot());
        Assert.DoesNotContain(f.MatRel, ShaderUpgradeWriter.ReadRecord(f.ModRoot)!.Declined);
    }

    [Fact]
    [Trait("Plan", "AC-upgrade-vs-matcap-5")]
    public void Undoing_the_row_matcap_keeps_the_upgrade_then_upgrade_undo_restores()
    {
        using var f = new UpgradedRowFixture(formB: true);
        var upgraded = f.Snapshot();
        var mask = f.Entry().Mask!;
        f.Apply();
        Assert.True(MatcapWriter.Undo(f.ModRoot, f.ModelRel).Ok);
        // The matcap's undo leaves the mod exactly as the upgrade left it.
        UpgradedRowFixture.SameAs(upgraded, f.Snapshot());
        Assert.DoesNotContain(Redirects(f.ModRoot), r => r.GamePath.Contains("_mcut"));

        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, f.RevertUpgrade());
        Assert.Equal(f.Legacy, ShaderUpgradeTestKit.Get(f.ModRoot, f.MatRel));
        Assert.False(File.Exists(MatcapWriter.Full(f.ModRoot, mask.Rel)));
        Assert.DoesNotContain(Redirects(f.ModRoot), r => PenumbraModMeta.SameFile(r.GamePath, mask.GamePath));
        Assert.Null(ShaderUpgradeWriter.ReadRecord(f.ModRoot));
    }

    [Fact]
    [Trait("Plan", "AC-upgrade-vs-matcap-7")]
    public void Author_update_under_a_row_matcap_is_left_alone_until_the_matcap_is_undone()
    {
        using var f = new UpgradedRowFixture(formB: true);
        f.Apply();
        var entry = f.Entry();
        var record = File.ReadAllText(Path.Combine(f.ModRoot, "Proteus", ShaderUpgradeWriter.RecordFile));
        var maskSha = HairPortPlan.Sha8(ShaderUpgradeTestKit.Get(f.ModRoot, entry.Mask!.Rel));
        // The author ships another legacy material at the same file.
        ShaderUpgradeTestKit.Put(f.ModRoot, f.MatRel, ShaderUpgradeTestKit.FormA());
        var outcome = f.Upgrade(f.MatRel);
        Assert.Contains((f.MatRel, LeftReason.RowMatcap), outcome.Left);
        Assert.Empty(outcome.Written);
        Assert.Equal(record, File.ReadAllText(Path.Combine(f.ModRoot, "Proteus", ShaderUpgradeWriter.RecordFile)));
        Assert.Equal(maskSha, HairPortPlan.Sha8(ShaderUpgradeTestKit.Get(f.ModRoot, entry.Mask.Rel)));
        Assert.Contains(Redirects(f.ModRoot), r => PenumbraModMeta.SameFile(r.GamePath, entry.Mask.GamePath));

        Assert.True(MatcapWriter.Undo(f.ModRoot, f.ModelRel).Ok);
        var again = f.Upgrade(f.MatRel);
        Assert.Contains(f.MatRel, again.Written);
        Assert.Equal("index-mask", f.Entry().Form);
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-30")]
    public void WriteOne_never_classifies()
    {
        using var f = new UpgradedRowFixture(formB: true);
        f.Apply();
        var classified = new List<string>();
        MatcapRowCut.AfterClassify = classified.Add;
        ShaderUpgradeTestKit.Put(f.ModRoot, f.MatRel, ShaderUpgradeTestKit.FormA());
        Assert.Contains((f.MatRel, LeftReason.RowMatcap), f.Upgrade(f.MatRel).Left);
        Assert.Empty(classified);
        // The undo's message does classify.
        f.RevertUpgrade();
        Assert.NotEmpty(classified);
    }

    [Theory]
    [Trait("Plan", "AC-upgrade-vs-matcap-8")]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-6")]
    [InlineData(false)]
    [InlineData(true)]
    public void Undo_without_a_row_matcap_is_unchanged(bool wholePartMatcap)
    {
        using var f = new UpgradedRowFixture();
        if (wholePartMatcap)
        {
            var parts = ModelPartReader.Read(f.Current())!.Parts.Where(p => p.Island < 0).ToList();
            var p = MatcapStudio.Prepare(f.ModRoot, f.ModelRel, UpgradedRowFixture.ModelPath, f.Current(), parts,
                MatcapChoice.OfImage(f.Image()), f.Env() with { RowOnly = 0 });
            Assert.True(p.Ok, p.Message);
            Assert.True(MatcapWriter.Apply(f.ModRoot, p.Request!).Ok);
            Assert.Null(MatcapRecord.Read(f.ModRoot).Materials.Single().Row);
        }
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, f.RevertUpgrade());
        Assert.Equal(f.Legacy, ShaderUpgradeTestKit.Get(f.ModRoot, f.MatRel));
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-2")]
    public void Identical_files_in_two_options_are_both_held()
    {
        using var f = new UpgradedRowFixture(layout: UpgradedRowFixture.Layout.TwoOptions);
        Assert.Equal(ShaderUpgradeTestKit.Get(f.ModRoot, f.MatRels[0]), ShaderUpgradeTestKit.Get(f.ModRoot, f.MatRels[1]));
        f.Apply();
        var file = Assert.Single(MatcapRecord.Read(f.ModRoot).Materials.Single().Row!.Files);
        Assert.Equal(["Blue", "Red"], file.Redirects.Select(r => r.Option!).Order());
        var before = f.Snapshot();
        foreach (var rel in f.MatRels)
        {
            Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade(rel));
            Assert.Contains(rel, f.LastMessage);
        }
        UpgradedRowFixture.SameAs(before, f.Snapshot());
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-28")]
    public void Other_items_same_leaf_is_not_held()
    {
        using var f = new UpgradedRowFixture(layout: UpgradedRowFixture.Layout.TwoOptions);
        // Another item's folder, same file name, in the Red option: upgraded separately.
        const string other = "chara/accessory/a0154/material/v0001/mt_c0201a0153_wrs_a.mtrl";
        ShaderUpgradeTestKit.Put(f.ModRoot, "other/wrs_a.mtrl", ShaderUpgradeTestKit.FormA());
        PenumbraModMeta.MergeOptionRedirects(f.ModRoot, "Colour", "Red",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [other] = "other\\wrs_a.mtrl" });
        Assert.True(f.Kit.Upgrade(f.ModRoot, [ShaderUpgradeTestKit.Worn(other, f.ModRoot, "other/wrs_a.mtrl")]).ChangedAny);
        f.Apply();
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, f.RevertUpgrade("other/wrs_a.mtrl"));
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade(f.MatRels[0]));
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade(f.MatRels[1]));
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-3")]
    public void Sibling_sharing_the_mask_is_held()
    {
        using var f = new UpgradedRowFixture(formB: true);
        // A sibling in default data on the same specular map: the same content-addressed mask.
        const string sibling = "chara/accessory/a0153/material/v0001/mt_c0201a0153_wrs_c.mtrl";
        ShaderUpgradeTestKit.Put(f.ModRoot, "mat/wrs_c.mtrl", f.Legacy);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(f.ModRoot, "meta.json")))!.AsObject();
        meta["DefaultData"]!["Files"]![sibling] = "mat\\wrs_c.mtrl";
        File.WriteAllText(Path.Combine(f.ModRoot, "meta.json"), meta.ToJsonString());
        Assert.True(f.Kit.Upgrade(f.ModRoot, [ShaderUpgradeTestKit.Worn(sibling, f.ModRoot, "mat/wrs_c.mtrl")],
            _ => Path.Combine(f.ModRoot, "tex", "wrs_s.tex")).ChangedAny);
        Assert.Equal(f.Entry().Mask!.GamePath, f.Entry("mat/wrs_c.mtrl").Mask!.GamePath);
        f.Apply();
        var before = f.Snapshot();
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade("mat/wrs_c.mtrl"));
        UpgradedRowFixture.SameAs(before, f.Snapshot());
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-4")]
    public void Unrelated_upgrade_in_the_same_mod_still_undoes()
    {
        using var f = new UpgradedRowFixture();
        const string other = "chara/accessory/a0153/material/v0001/mt_c0201a0153_wrs_b.mtrl";
        ShaderUpgradeTestKit.Put(f.ModRoot, "mat/wrs_b.mtrl", ShaderUpgradeTestKit.FormA());
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(f.ModRoot, "meta.json")))!.AsObject();
        meta["DefaultData"]!["Files"]![other] = "mat\\wrs_b.mtrl";
        File.WriteAllText(Path.Combine(f.ModRoot, "meta.json"), meta.ToJsonString());
        Assert.True(f.Kit.Upgrade(f.ModRoot, [ShaderUpgradeTestKit.Worn(other, f.ModRoot, "mat/wrs_b.mtrl")]).ChangedAny);
        f.Apply();
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, f.RevertUpgrade("mat/wrs_b.mtrl"));
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade());
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-4")]
    public void Unrelated_undo_reads_no_matcap_model()
    {
        using var f = new UpgradedRowFixture();
        const string other = "chara/accessory/a0153/material/v0001/mt_c0201a0153_wrs_b.mtrl";
        ShaderUpgradeTestKit.Put(f.ModRoot, "mat/wrs_b.mtrl", ShaderUpgradeTestKit.FormA());
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(f.ModRoot, "meta.json")))!.AsObject();
        meta["DefaultData"]!["Files"]![other] = "mat\\wrs_b.mtrl";
        File.WriteAllText(Path.Combine(f.ModRoot, "meta.json"), meta.ToJsonString());
        Assert.True(f.Kit.Upgrade(f.ModRoot, [ShaderUpgradeTestKit.Worn(other, f.ModRoot, "mat/wrs_b.mtrl")]).ChangedAny);
        f.Apply();
        var classified = new List<string>();
        MatcapRowCut.AfterClassify = classified.Add;
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, f.RevertUpgrade("mat/wrs_b.mtrl"));
        Assert.Empty(classified);
    }

    [Fact]
    [Trait("Plan", "AC-upgrade-vs-matcap-4")]
    public void Two_row_matcaps_on_one_file_the_one_needing_most_speaks()
    {
        var a = new MatcapRowCut.RowEntryDeps(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mat/x.mtrl" }, [], ["a.mdl"]);
        var b = new MatcapRowCut.RowEntryDeps(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mat/x.mtrl" }, [], ["b.mdl"]);
        var c = new MatcapRowCut.RowEntryDeps(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mat/other.mtrl" }, [], ["c.mdl"]);
        var asked = new List<string>();
        var deps = new MatcapRowCut.RowDependencies(false, [a, b, c], e =>
        {
            asked.Add(e.Models[0]);
            return new MatcapRowCut.RowHold(e == b ? MatcapRowCut.RowHoldKind.LaterFeature : MatcapRowCut.RowHoldKind.Active, e.Models[0], "the brush");
        });
        var hold = deps.HoldOf("mat\\x.mtrl", null, null)!;
        Assert.Equal(MatcapRowCut.RowHoldKind.LaterFeature, hold.Kind);
        Assert.Equal("b.mdl", hold.Model);
        Assert.Equal(["a.mdl", "b.mdl"], asked);   // only the matching ones are classified
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-13")]
    public void Author_record_with_null_lists_neither_throws_nor_blocks_a_walk()
    {
        using var f = new UpgradedRowFixture();
        var path = MatcapRecord.PathOf(f.ModRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {"Version":2,"Token":"abc123","Models":[],"Materials":[
             {"Name":"/mt_x_mcapabc1231.mtrl","File":"Proteus Matcaps/x.mtrl","Meshes":null,
              "Row":{"Row":15,"Cut":null,"CutMeshes":null,"Files":null}},
             {"Name":"/mt_y_mcapabc1232.mtrl","File":"Proteus Matcaps/y.mtrl","Meshes":[],
              "Row":{"Row":15,"Cut":"/mt_c0201a0153_wrs_a_mcutabc1232.mtrl","Files":[{"File":"Proteus Matcaps/c.mtrl","Redirects":null,
               "Source":{"File":"mat/wrs_a.mtrl","GamePath":"x"},"Normal":{"Game":true,"GamePath":"y"}}]}}]}
            """);
        var deps = MatcapRowCut.Dependencies(f.ModRoot, classify: true);
        Assert.NotNull(deps.HoldOf(f.MatRel, null, null));
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade());
        ShaderUpgradeTestKit.Put(f.ModRoot, f.MatRel, ShaderUpgradeTestKit.FormB());
        Assert.Contains((f.MatRel, LeftReason.RowMatcap), f.Upgrade(f.MatRel).Left);
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-2")]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-21")]
    public void Kept_entry_still_holds_both_identical_option_files()
    {
        using var f = new UpgradedRowFixture(layout: UpgradedRowFixture.Layout.TwoOptions);
        f.Apply();
        f.KeptForPorts();   // the model records are gone: the item comes from the cut's own game paths
        foreach (var rel in f.MatRels) Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade(rel));
    }

    [Theory]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-5")]
    [InlineData("corrupt")]
    [InlineData("version")]
    public void Unreadable_matcap_record_refuses_undo_and_leaves_rewrites(string how)
    {
        using var f = new UpgradedRowFixture(formB: true);
        f.Apply();
        var path = MatcapRecord.PathOf(f.ModRoot);
        if (how == "corrupt") File.WriteAllText(path, "{ not json");
        else
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["Version"] = 3;
            File.WriteAllText(path, json.ToJsonString());
        }
        var before = f.Snapshot();
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade());
        Assert.Equal(MatcapWriter.RecordUnreadableMessage, f.LastMessage);
        UpgradedRowFixture.SameAs(before, f.Snapshot());
        ShaderUpgradeTestKit.Put(f.ModRoot, f.MatRel, ShaderUpgradeTestKit.FormA());
        Assert.Contains((f.MatRel, LeftReason.RowMatcap), f.Upgrade(f.MatRel).Left);
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-7")]
    public void Pending_row_entry_still_holds()
    {
        using var f = new UpgradedRowFixture();
        f.Apply();
        var path = MatcapRecord.PathOf(f.ModRoot);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["Materials"]![0]!["Pending"] = true;
        File.WriteAllText(path, json.ToJsonString());
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade());
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-8")]
    public void Upgrade_undone_between_prepare_and_apply_refuses_the_apply()
    {
        using var f = new UpgradedRowFixture(formB: true);
        var p = f.Prepare();
        Assert.True(p.Ok, p.Message);
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, f.RevertUpgrade());
        var afterUndo = f.Snapshot();
        var o = MatcapWriter.Apply(f.ModRoot, p.Request!);
        Assert.Equal(MatcapWriter.CodeEdited, o.Code);
        Assert.Contains(f.MatRel, o.Message.Replace('\\', '/'));
        UpgradedRowFixture.SameAs(afterUndo, f.Snapshot());
        Assert.False(MatcapRecord.Exists(f.ModRoot));
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-8")]
    public void Source_gone_between_prepare_and_apply_refuses_the_apply()
    {
        using var f = new UpgradedRowFixture();
        var p = f.Prepare();
        Assert.True(p.Ok, p.Message);
        File.Delete(MatcapWriter.Full(f.ModRoot, f.MatRel));
        var before = f.Snapshot();
        var o = MatcapWriter.Apply(f.ModRoot, p.Request!);
        Assert.Equal(MatcapWriter.CodeEdited, o.Code);
        Assert.Contains(f.MatRel, o.Message.Replace('\\', '/'));
        UpgradedRowFixture.SameAs(before, f.Snapshot());
        Assert.False(MatcapRecord.Exists(f.ModRoot));
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-5")]
    public void Unreadable_manifest_with_a_row_matcap_holds()
    {
        using var f = new UpgradedRowFixture(layout: UpgradedRowFixture.Layout.TwoOptions);
        f.Apply();
        var meta = Path.Combine(f.ModRoot, "meta.json");
        var text = File.ReadAllText(meta);
        File.WriteAllText(meta, "{ not json");
        var before = ShaderUpgradeTestKit.Hashes(f.ModRoot);
        // Which option files the cut follows can't be told: both are held, the second one included.
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade(f.MatRels[1]));
        Assert.Contains("meta.json", f.LastMessage);
        Assert.Equal(before, ShaderUpgradeTestKit.Hashes(f.ModRoot));
        Assert.Equal("{ not json", File.ReadAllText(meta));
        File.WriteAllText(meta, text);
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade(f.MatRels[1]));
        Assert.Contains(f.MatRels[1], f.LastMessage);
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-24")]
    public void Apply_recheck_hashes_only_on_size_or_mtime_change()
    {
        using var f = new UpgradedRowFixture();
        var p = f.Prepare();
        Assert.True(p.Ok, p.Message);
        var src = p.Request!.Materials.Single().Row!.Cut.Files.Single().Source;
        // Same bytes, a new write time: hashed, matches.
        File.SetLastWriteTimeUtc(MatcapWriter.Full(f.ModRoot, f.MatRel), DateTime.UtcNow.AddMinutes(3));
        Assert.True(MatcapWriter.SourceUnchanged(f.ModRoot, src));
        // Size and time as recorded: not read at all (a file of other bytes with the recorded time and size passes).
        var full = MatcapWriter.Full(f.ModRoot, f.MatRel);
        var bytes = File.ReadAllBytes(full);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(full, bytes);
        File.SetLastWriteTimeUtc(full, new DateTime(src.MtimeUtc, DateTimeKind.Utc));
        Assert.True(MatcapWriter.SourceUnchanged(f.ModRoot, src));
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(full, bytes);
        Assert.True(MatcapWriter.Apply(f.ModRoot, p.Request!).Ok);
    }

    [Theory]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-27")]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_recheck_always_hashes_an_uncaptured_mtime(bool changed)
    {
        using var f = new UpgradedRowFixture();
        var p = f.Prepare();
        Assert.True(p.Ok, p.Message);
        var src = p.Request!.Materials.Single().Row!.Cut.Files.Single().Source;
        src.MtimeUtc = 0;
        var full = MatcapWriter.Full(f.ModRoot, f.MatRel);
        if (changed)
        {
            var bytes = File.ReadAllBytes(full);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(full, bytes);
        }
        var o = MatcapWriter.Apply(f.ModRoot, p.Request!);
        Assert.Equal(changed ? MatcapWriter.CodeEdited : "", o.Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-9")]
    public void Out_of_band_upgrade_undo_reads_RowStale()
    {
        using var f = new UpgradedRowFixture();
        f.Apply();
        var backup = Path.Combine(f.ModRoot, "Proteus", ShaderUpgradeWriter.BackupSubdir, f.MatRel.Replace('/', Path.DirectorySeparatorChar));
        File.Copy(backup, MatcapWriter.Full(f.ModRoot, f.MatRel), true);
        var name = MatcapRecord.Read(f.ModRoot).Materials.Single().Name;
        Assert.Equal(MatcapWriter.State.RowStale, MatcapWriter.Detect(f.ModRoot, f.Game.GetValueOrDefault)[name]);
        Assert.True(MatcapWriter.Undo(f.ModRoot, f.ModelRel).Ok);
        Assert.Equal(f.Legacy, ShaderUpgradeTestKit.Get(f.ModRoot, f.MatRel));
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-25")]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-10")]
    public void Superseded_row_model_hold_is_plain_active()
    {
        using var f = new UpgradedRowFixture();
        f.Apply();
        f.SupersedeModel();
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade());
        Assert.Equal(f.Matcap(MatcapRowCut.RowHoldKind.Active, f.ModelRel), f.LastMessage);
        Assert.True(MatcapWriter.Undo(f.ModRoot, f.ModelRel).Ok);
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, f.RevertUpgrade());
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-10")]
    public void Superseded_row_matcap_holds_until_forgotten()
    {
        using var f = new UpgradedRowFixture();
        f.Apply();
        f.SupersedeModel();
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade());
        Assert.True(MatcapWriter.Forget(f.ModRoot, f.ModelRel).Ok);
        Assert.DoesNotContain(MatcapRecord.Read(f.ModRoot).Materials, m => m.Row != null);
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, f.RevertUpgrade());
    }

    [Fact]
    [Trait("Plan", "AC-upgrade-vs-matcap-4")]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-21")]
    public void Kept_for_regenerated_hold_says_remake_first()
    {
        using var f = new UpgradedRowFixture();
        f.Apply();
        f.KeptForPorts();
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade());
        Assert.Equal(f.Matcap(MatcapRowCut.RowHoldKind.KeptForRegenerated, f.ModelRel), f.LastMessage);
        Assert.Contains(f.ModelRel, f.LastMessage);
    }

    [Fact]
    [Trait("Plan", "AC-upgrade-vs-matcap-4")]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-22")]
    public void Model_edited_on_top_hold_says_undo_the_change_first()
    {
        using var f = new UpgradedRowFixture();
        f.Apply();
        f.EditModelOnTop();
        Assert.Equal(MatcapWriter.CodeEdited, MatcapWriter.Undo(f.ModRoot, f.ModelRel).Code);
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade());
        Assert.Equal(f.Matcap(MatcapRowCut.RowHoldKind.Edited, f.ModelRel), f.LastMessage);
    }

    [Fact]
    [Trait("Plan", "AC-upgrade-vs-matcap-4")]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-26")]
    public void Brush_edited_row_model_hold_names_the_brush()
    {
        using var f = new UpgradedRowFixture();
        f.Apply();
        f.BrushModel();
        Assert.Equal(MatcapWriter.CodeLaterFeature, MatcapWriter.Undo(f.ModRoot, f.ModelRel).Code);
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade());
        var brush = ProteusLoc.Localize("MeshVolume.Feature.Brush", "the brush");
        Assert.Equal(f.Matcap(MatcapRowCut.RowHoldKind.LaterFeature, f.ModelRel, brush), f.LastMessage);
        Assert.Contains(brush, f.LastMessage);
        Assert.Contains(f.ModelRel, f.LastMessage);
    }

    public static TheoryData<string, bool> AgreementCases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var s in new[] { "written", "superseded", "brush", "edited", "kept" })
            foreach (var forget in new[] { false, true }) data.Add(s, forget);
        return data;
    }

    [Theory]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-29")]
    [MemberData(nameof(AgreementCases))]
    public void Classifier_agrees_with_matcap_undo_and_forget(string setup, bool forgetOnly)
    {
        using var f = new UpgradedRowFixture();
        f.Apply();
        switch (setup)
        {
            case "superseded": f.SupersedeModel(); break;
            case "brush": f.BrushModel(); break;
            case "edited": f.EditModelOnTop(); break;
            case "kept": f.KeptForPorts(); break;
        }
        var c = MatcapWriter.ClassifyForUndo(f.ModRoot, MatcapRecord.Read(f.ModRoot), f.ModelRel, forgetOnly, out _, out _);
        var o = forgetOnly ? MatcapWriter.Forget(f.ModRoot, f.ModelRel) : MatcapWriter.Undo(f.ModRoot, f.ModelRel);
        switch (c)
        {
            case MatcapWriter.UndoClass.Restore or MatcapWriter.UndoClass.Forget or MatcapWriter.UndoClass.Remade:
                Assert.True(o.Ok, $"{setup}/{forgetOnly}: {c} but {o.Code} {o.Message}"); break;
            case MatcapWriter.UndoClass.LaterFeature: Assert.Equal(MatcapWriter.CodeLaterFeature, o.Code); break;
            case MatcapWriter.UndoClass.Edited: Assert.Equal(MatcapWriter.CodeEdited, o.Code); break;
            case MatcapWriter.UndoClass.Keep:
                Assert.True(forgetOnly);
                Assert.Equal(MatcapWriter.CodeNothing, o.Code); break;
            case MatcapWriter.UndoClass.AlreadyUndone:
                // No work on the model: nothing restored. (The kept entry itself goes only with its last user — here no
                // refit draws it, so the reconcile pass may drop it.)
                Assert.True(o.Code is "" or MatcapWriter.CodeNothing, o.Code);
                Assert.DoesNotContain(o.Files, x => PenumbraModMeta.SameFile(x, f.ModelRel)); break;
        }
        // What each setup is expected to be, so the agreement is not vacuous.
        var expected = (setup, forgetOnly) switch
        {
            ("written", false) => MatcapWriter.UndoClass.Restore,
            ("superseded", _) => MatcapWriter.UndoClass.Forget,
            ("brush", false) => MatcapWriter.UndoClass.LaterFeature,
            ("edited", false) => MatcapWriter.UndoClass.Edited,
            ("kept", _) => MatcapWriter.UndoClass.AlreadyUndone,
            (_, true) => MatcapWriter.UndoClass.Keep,
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(expected, c);
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-12")]
    public void Preview_on_an_upgraded_material_builds_and_writes_nothing()
    {
        using var f = new UpgradedRowFixture(formB: true);
        var before = f.Snapshot();
        var p = f.Prepare(withSiblings: false);
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        UpgradedRowFixture.SameAs(before, f.Snapshot());
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-13")]
    public void Author_shipped_record_outside_the_mod_holds_nothing()
    {
        using var f = new UpgradedRowFixture();
        var path = MatcapRecord.PathOf(f.ModRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string Record(string source) => $$$"""
            {"Version":2,"Token":"abc123","Models":[],"Materials":[{"Name":"/mt_x_mcapabc1231.mtrl","File":"Proteus Matcaps/x.mtrl",
             "Meshes":[],"Row":{"Row":15,"Cut":"/mt_c0201a0153_wrs_a_mcutabc1231.mtrl","Files":[{"File":"Proteus Matcaps/c.mtrl",
             "Source":{"File":"{{{source}}}","GamePath":"x"},"Normal":{"Game":true,"GamePath":"y"}}]}}]}
            """;
        File.WriteAllText(path, Record("../outside.mtrl"));
        var parent = Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(f.ModRoot)!).Order().ToList();
        Assert.Null(MatcapRowCut.Dependencies(f.ModRoot, classify: true).HoldOf(f.MatRel, null, null));
        File.WriteAllText(path, Record("mat/wrs_a.mtrl"));
        var before = f.Snapshot();
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade());
        UpgradedRowFixture.SameAs(before, f.Snapshot());
        Assert.Equal(parent, Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(f.ModRoot)!).Order().ToList());
        File.WriteAllText(path, Record("../outside.mtrl"));
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, f.RevertUpgrade());
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-14")]
    public void Missing_cut_file_still_holds_without_throwing()
    {
        using var f = new UpgradedRowFixture(formB: true);
        f.Apply();
        File.Delete(MatcapWriter.Full(f.ModRoot, MatcapRecord.Read(f.ModRoot).Materials.Single().Row!.Files.Single().File));
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, f.RevertUpgrade());
    }

    // ── a part still on the old shader ──

    private static (UpgradedRowFixture F, string Blue) OnlyRedUpgraded()
    {
        var f = new UpgradedRowFixture(layout: UpgradedRowFixture.Layout.TwoOptions, upgradeAll: false);
        return (f, f.MatRels[1]);
    }

    private static string Blue => MatcapRowCut.Describe(new PenumbraModMeta.ContainerRef("Colour", "Blue"));
    private static string SettingsPath =>
        $"{ProteusLoc.Localize("Tab.Settings", "Settings")} > {ProteusLoc.Localize("Settings.Section.GearShaders", "Gear shaders")}";

    [Fact]
    [Trait("Plan", "AC-upgrade-vs-matcap-2")]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-16")]
    public void Only_worn_option_upgraded_refuses_with_wear_text()
    {
        var (f, _) = OnlyRedUpgraded();
        using var _f = f;
        var before = f.Snapshot();
        var p = f.Prepare(f.Env(gearShadersOn: true));
        Assert.Equal(MatcapRowCut.CodeShader, p.Code);
        // Built here from the Wear key itself, not by the function under test.
        var wear = string.Format(ProteusLoc.Localize("Matcap.Refused.RowLegacyWear.Fmt",
            "{0} is still on the old shader (characterlegacy.shpk), and a row matcap needs character.shpk. Wear it once so Gear shaders moves it to character.shpk, then add the row matcap. If it stays on the old shader, hover the count under {1} to see why."),
            Blue, SettingsPath);
        Assert.Equal(char.ToUpperInvariant(wear[0]) + wear[1..], p.Message);
        Assert.DoesNotContain(ProteusLoc.Localize("Settings.GearShaders.AutoUpgrade.Label", "Upgrade old gear to Dawntrail shaders"), p.Message);
        Assert.DoesNotContain(ProteusLoc.Localize("Settings.GearShaders.UpgradeNow", "Upgrade what I'm wearing now"), p.Message);
        Assert.Contains(Blue[1..], p.Message);
        Assert.Contains(SettingsPath, p.Message);
        UpgradedRowFixture.SameAs(before, f.Snapshot());
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-17")]
    public void Gear_shaders_off_refuses_with_switch_on_text()
    {
        var (f, _) = OnlyRedUpgraded();
        using var _f = f;
        var p = f.Prepare(f.Env(gearShadersOn: false));
        Assert.Equal(MatcapRowCut.CodeShader, p.Code);
        Assert.Contains(ProteusLoc.Localize("Settings.GearShaders.AutoUpgrade.Label", "Upgrade old gear to Dawntrail shaders"), p.Message);
        Assert.Contains(SettingsPath, p.Message);
        Assert.True(char.IsUpper(p.Message[0]), p.Message);   // the place leads the sentence ("The option …")
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-18")]
    public void Declined_option_refuses_with_upgrade_now_text()
    {
        var (f, blue) = OnlyRedUpgraded();
        using var _f = f;
        // Blue was upgraded and undone by the user: declined.
        Assert.True(f.Upgrade(blue).ChangedAny);
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, f.RevertUpgrade(blue, decline: true));
        var p = f.Prepare(f.Env(gearShadersOn: true));
        Assert.Equal(MatcapRowCut.CodeShader, p.Code);
        Assert.Contains(ProteusLoc.Localize("Settings.GearShaders.UpgradeNow", "Upgrade what I'm wearing now"), p.Message);
    }

    [Fact]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-19")]
    public void Unknown_setting_or_other_shader_keeps_the_generic_text()
    {
        var (f, blue) = OnlyRedUpgraded();
        using var _f = f;
        var generic = string.Format(ProteusLoc.Localize("Matcap.Refused.RowShader.Fmt",
            "A row matcap needs the part on character.shpk, and {0} uses {1}."), Blue, MatcapRowCut.LegacyShader);
        Assert.Equal(generic, f.Prepare(f.Env(gearShadersOn: null)).Message);
        ShaderUpgradeTestKit.Put(f.ModRoot, blue, RowFixture.Material(shader: "hair.shpk"));
        var glass = f.Prepare(f.Env(gearShadersOn: true));
        Assert.Equal(MatcapRowCut.CodeShader, glass.Code);
        Assert.Contains("hair.shpk", glass.Message);
        Assert.DoesNotContain(MatcapRowCut.LegacyShader, glass.Message);
    }

    [Fact]
    [Trait("Plan", "AC-upgrade-vs-matcap-2")]
    public void Legacy_row_refusals_quote_the_ui_labels_in_every_language()
    {
        foreach (var lang in new[] { "en", "de", "es", "fr", "ja", "ko", "ru", "zh" })
        {
            using var s = typeof(Proteus.Plugin).Assembly.GetManifestResourceStream($"Proteus.Localization.{lang}.json")!;
            var json = JsonNode.Parse(s)!.AsObject();
            string M(string key) => json[key]?["message"]?.GetValue<string>() ?? throw new Xunit.Sdk.XunitException($"{lang}: {key} missing");
            // The labels are passed in at runtime ({1}/{2}), so every locale quotes its own words for them.
            Assert.Contains("{2}", M("Matcap.Refused.RowLegacyOff.Fmt"));
            Assert.Contains("{1}", M("Matcap.Refused.RowLegacyOff.Fmt"));
            Assert.Contains("{2}", M("Matcap.Refused.RowLegacyDeclined.Fmt"));
            Assert.Contains("{1}", M("Matcap.Refused.RowLegacyWear.Fmt"));
            Assert.Contains("{1}", M("Settings.GearShaders.RowMatcap.Fmt"));
            foreach (var k in new[] { "Matcap.Refused.RowLegacyOff.Fmt", "Matcap.Refused.RowLegacyDeclined.Fmt", "Matcap.Refused.RowLegacyWear.Fmt" })
            {
                Assert.Contains("characterlegacy.shpk", M(k));
                Assert.Contains("character.shpk", M(k));   // not a substring of characterlegacy.shpk
                // A placeholder right after the shader name means a sentence was cut out between them.
                Assert.DoesNotMatch(@"character\.shpk\s*\{\d\}", M(k));
            }
        }
    }
}
