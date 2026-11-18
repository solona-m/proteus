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
/// A gear mod for row matcaps: one character.shpk material (base, normal, mask, index; Dawntrail colour table) whose
/// index map puts row 15 B on the left half of the texture and row 1 A on the right, and a model of four triangles —
/// two over each half (UV x &lt; 0.5 left) — plus helpers to give it options, variants and a template.
/// </summary>
internal sealed class RowFixture : IDisposable
{
    public const string ModelPath = "chara/equipment/e6255/model/c0201e6255_top.mdl";
    public const string ModelRel = "model/top.mdl";
    public const string Mat = "/mt_c0201e6255_top_a.mtrl";
    public static string MatPath(int folder = 1) => $"chara/equipment/e6255/material/v{folder:D4}{Mat}";
    public const string NormalPath = "chara/equipment/e6255/texture/v01_c0201e6255_top_n.tex";
    public const string IndexPath = "chara/equipment/e6255/texture/v01_c0201e6255_top_id.tex";
    public const int Size = 64;

    public readonly string Root = Path.Combine(Path.GetTempPath(), "proteus-mrow-" + Guid.NewGuid().ToString("N")[..8]);
    public string Mods => Path.Combine(Root, "mods");
    public string ModRoot { get; private set; } = "";
    public string Temp => Path.Combine(Root, "temp");
    public readonly TextureLoader Loader = new(Substitute.For<Dalamud.Plugin.Services.IDataManager>(),
                                               Substitute.For<Dalamud.Plugin.Services.IPluginLog>());
    public readonly Dictionary<string, byte[]> Game = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Model { get; }

    /// <summary>Two triangles over the left half (row 15 B) and two over the right half (row 1 A), UV v in 0.1–0.9.</summary>
    public static (float, float, float)[] Corners()
    {
        static (float, float, float) At(float u, float v) => (u * 0.2f - 0.1f, v * 0.5f + 0.9f, 0f);
        return
        [
            At(0.05f, 0.1f), At(0.40f, 0.1f), At(0.05f, 0.4f),
            At(0.05f, 0.5f), At(0.40f, 0.5f), At(0.05f, 0.9f),
            At(0.60f, 0.1f), At(0.95f, 0.1f), At(0.60f, 0.4f),
            At(0.60f, 0.5f), At(0.95f, 0.5f), At(0.60f, 0.9f),
        ];
    }

    public RowFixture(float? threshold = null, byte[]? normal = null, byte[]? index = null, bool materialInOption = false,
                      byte[]? model = null, string folder = "Row Mod")
    {
        Model = model ?? SyntheticModel.Build([], [new SyntheticModel.Mesh(Mat, new SyntheticModel.Sub(0, Corners: Corners()))],
                                              SyntheticModel.V6, (IReadOnlyList<string>?)null);
        Directory.CreateDirectory(Temp);
        var published = new Dictionary<string, (string, byte[])>
        {
            [ModelPath] = (ModelRel, Model),
            [NormalPath] = ("tex/n.tex", normal ?? Tex(NormalRgba())),
            [IndexPath] = ("tex/id.tex", index ?? Tex(IndexRgba())),
        };
        if (!materialInOption) published[MatPath()] = ("mat/top_a.mtrl", Material(threshold));
        ModRoot = SyntheticMatcapMod.TargetMod(Mods, folder, published);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }

    /// <summary>A character.shpk gear material: base, normal, mask, index; a Dawntrail colour table.</summary>
    public static byte[] Material(float? threshold = null, string shader = MatcapRowCut.CutShader, string normal = NormalPath,
                                  string index = IndexPath, byte tint = 0)
    {
        var consts = new List<(uint, float[])> { (0x59BDA0B1, [tint]) };
        if (threshold is { } t) consts.Add((MatcapMaterial.ConstAlphaThreshold, [t]));
        return SyntheticMatcapMod.Material(shader, ["chara/x/base.tex", normal, "chara/x/mask.tex", index],
            [(0x115306BE, 0), (MatcapMaterial.SamplerNormal, 1), (MatcapMaterial.SamplerMask, 2), (ShaderUpgradePlan.SamplerIndex, 3)],
            [.. consts], colorTableBytes: 2048);
    }

    /// <summary>A flat normal: (128, 128, <paramref name="blue"/>, 255).</summary>
    public static byte[] NormalRgba(byte blue = 255)
    {
        var rgba = new byte[Size * Size * 4];
        for (int i = 0; i < Size * Size; i++) { rgba[i * 4] = 128; rgba[i * 4 + 1] = 128; rgba[i * 4 + 2] = blue; rgba[i * 4 + 3] = 255; }
        return rgba;
    }

    /// <summary>Left half row 15 B (red 238, green 0), right half row 1 A (red 0, green 255).</summary>
    public static byte[] IndexRgba(int leftRow = 15, bool leftA = false)
    {
        var rgba = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int p = (y * Size + x) * 4;
                bool left = x < Size / 2;
                rgba[p] = (byte)(left ? (leftRow - 1) * 17 : 0);
                rgba[p + 1] = (byte)(left ? (leftA ? 255 : 0) : 255);
                rgba[p + 3] = 255;
            }
        return rgba;
    }

    public byte[] Tex(byte[] rgba)
    {
        var path = Path.Combine(Temp, Guid.NewGuid().ToString("N") + ".tex");
        Assert.True(Loader.WriteTex(rgba, Size, Size, path));
        return File.ReadAllBytes(path);
    }

    public MatcapStudio.Environment Env(int row = 15, SubRowPick sub = SubRowPick.B, Func<bool>? affordable = null) =>
        new(p => p == MatcapMaterial.VanillaTemplatePath ? SyntheticMatcapMod.Reflection() : Game.GetValueOrDefault(p),
            Loader, false, Temp, WornFolder: 1, RowOnly: row, RowSub: sub, Affordable: affordable ?? (() => false));

    public MatcapImage Image(string name = "gem.tex")
    {
        var rgba = new byte[32 * 32 * 4];
        byte seed = (byte)name.Sum(c => c);
        for (int i = 0; i < 32 * 32; i++) { rgba[i * 4] = (byte)(i + seed); rgba[i * 4 + 1] = 90; rgba[i * 4 + 2] = 200; rgba[i * 4 + 3] = 255; }
        var dir = Path.Combine(Root, "images");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        Assert.True(Loader.WriteTex(rgba, 32, 32, path));
        return new MatcapImage(name, path, false);
    }

    public byte[] Current() => File.ReadAllBytes(MatcapWriter.Full(ModRoot, ModelRel));

    public IReadOnlyList<ModelPart> Pool() =>
        MatcapPlan.RowPool(ModelPartReader.Read(Current())!.Parts, MatcapPlan.Kind.Gear, [], Mat);

    public MatcapStudio.Prepared Prepare(MatcapStudio.Environment? env = null, IReadOnlyList<ModelPart>? parts = null, MatcapChoice? choice = null) =>
        MatcapStudio.Prepare(ModRoot, ModelRel, ModelPath, Current(), parts ?? Pool(), choice ?? MatcapChoice.OfImage(Image()), env ?? Env());

    public MatcapWriter.Outcome Apply(MatcapStudio.Environment? env = null, IReadOnlyList<ModelPart>? parts = null)
    {
        var p = Prepare(env, parts);
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        return MatcapWriter.Apply(ModRoot, p.Request!);
    }

    /// <summary>
    /// Every file of the mod with its hash — what "untouched" compares. The manifest is compared by its content (the
    /// manifest writer re-indents JSON), every other file byte for byte.
    /// </summary>
    public Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(ModRoot, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("Proteus", "matcap-backup-superseded")))
            .ToDictionary(f => Path.GetRelativePath(ModRoot, f), f => Path.GetFileName(f) == "meta.json"
                ? HairPortPlan.Sha8(System.Text.Encoding.UTF8.GetBytes(JsonNode.Parse(File.ReadAllText(f))!.ToJsonString()))
                : HairPortPlan.Sha8(File.ReadAllBytes(f)));

    /// <summary>Move the default material into a Single group "Colour" with options Red and Blue (different colour tables).</summary>
    public void ColourOptions(bool sameName = false)
    {
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(ModRoot, "meta.json")))!.AsObject();
        meta["DefaultData"]!["Files"]!.AsObject().Remove(MatPath());
        File.WriteAllBytes(Path.Combine(ModRoot, "mat", "red.mtrl").EnsureDir(), Material(tint: 1));
        File.WriteAllBytes(Path.Combine(ModRoot, "mat", "blue.mtrl"), Material(tint: 2));
        meta["Groups"] = new JsonArray(new JsonObject
        {
            ["Name"] = "Colour", ["Type"] = "Single", ["DefaultSettings"] = 0,
            ["Options"] = new JsonArray(
                new JsonObject { ["Name"] = "Red", ["Files"] = new JsonObject { [MatPath()] = "mat\\red.mtrl" } },
                new JsonObject { ["Name"] = sameName ? "Red" : "Blue", ["Files"] = new JsonObject { [MatPath()] = "mat\\blue.mtrl" } }),
        });
        File.WriteAllText(Path.Combine(ModRoot, "meta.json"), meta.ToJsonString());
    }

    public void EditDefaults(Action<JsonObject> edit, string property = "Files")
    {
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(ModRoot, "meta.json")))!.AsObject();
        edit(meta["DefaultData"]![property]!.AsObject());
        File.WriteAllText(Path.Combine(ModRoot, "meta.json"), meta.ToJsonString());
    }
}

internal static class PathExt
{
    public static string EnsureDir(this string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}

/// <summary>
/// Row matcaps, end to end on disk (Sensitive writer: MatcapWriter, ModelMaterialWriter, writes into users' mods — these
/// stay in the quick suite): Prepare, Apply, Undo, Forget, Change and Detect.
/// </summary>
public class MatcapRowApplyTests
{
    private static List<PenumbraModMeta.ContainerRedirect> Redirects(string mod) => PenumbraModMeta.ReadContainerRedirects(mod);

    private static (int Mesh, string Material, List<(float U, float V)> Centroids)[] Draws(byte[] mdl)
    {
        var src = SecondSkinWriter.Parse(mdl);
        var parts = ModelPartReader.Read(mdl)!.Parts.Where(p => p.Island < 0).ToList();
        return parts.Select(p =>
        {
            var uvs = ModelMaterialWriter.TriangleUvs(mdl, [p]);
            // Collapsed triangles draw nothing: the reader leaves them out of Ordinals.
            return (p.Mesh, p.Material, uvs.Select(t => ((t.U0 + t.U1 + t.U2) / 3f, (t.V0 + t.V1 + t.V2) / 3f)).ToList());
        }).ToArray();
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-1")]
    [Trait("Plan", "AC-matcap-row-11")]
    public void No_ticks_row_apply_covers_the_picker_material()
    {
        using var f = new RowFixture();
        var outcome = f.Apply();
        Assert.True(outcome.Ok, outcome.Message);

        var record = MatcapRecord.Read(f.ModRoot);
        var entry = Assert.Single(record.Materials);
        var row = Assert.IsType<MatcapRecord.RowRef>(entry.Row);
        Assert.Equal(15, row.Row);
        Assert.Equal(MatcapRecord.RowRef.SubB, row.Sub);
        Assert.Matches(@"_mcut[0-9a-f]{6}\d+\.mtrl$", row.Cut);
        Assert.Equal(2, record.Version);

        // Only the left (row 15 B) triangles: copied onto the matcap, moved onto the cut; the right ones stay on the author's.
        var draws = Draws(f.Current());
        Assert.All(draws.Where(d => d.Material == entry.Name).SelectMany(d => d.Centroids), c => Assert.True(c.U < 0.5f));
        Assert.All(draws.Where(d => d.Material == row.Cut).SelectMany(d => d.Centroids), c => Assert.True(c.U < 0.5f));
        Assert.Equal(2, draws.Where(d => d.Material == row.Cut).Sum(d => d.Centroids.Count));
        Assert.Equal(2, draws.Where(d => d.Material == entry.Name).Sum(d => d.Centroids.Count));
        Assert.All(draws.Where(d => d.Material == RowFixture.Mat).SelectMany(d => d.Centroids), c => Assert.True(c.U > 0.5f));

        // The cut copy: normal repointed at the cut normal, threshold 0.5 (the source had none); blue 0 exactly on the row.
        var cutFile = Assert.Single(row.Files);
        var cut = File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, cutFile.File));
        var h = MatcapMaterial.ReadHeader(cut)!;
        var normal = Assert.Single(row.Normals);
        Assert.Equal(normal.GamePath, h.TextureOf(MatcapMaterial.SamplerNormal));
        Assert.Equal(0.5f, h.AlphaThreshold);
        var n = f.Loader.LoadTexBytesAsRgba(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, normal.File)), "cut")!.Value;
        for (int y = 0; y < n.height; y += 7)
            for (int x = 0; x < n.width; x += 5)
                Assert.Equal(x < n.width / 2 ? 0 : 255, n.rgba[(y * n.width + x) * 4 + 2]);

        // Redirects: the cut at its own path in default data (where the author's material is), the normal too.
        var defaults = Redirects(f.ModRoot).Where(r => r.Container.IsDefault).ToList();
        Assert.Contains(defaults, r => r.GamePath.EndsWith(row.Cut.TrimStart('/'), StringComparison.OrdinalIgnoreCase) && r.File == cutFile.File);
        Assert.Contains(defaults, r => PenumbraModMeta.SameFile(r.GamePath, normal.GamePath));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-2")]
    public void Ticked_subset_leaves_unticked_parts_on_the_author_material()
    {
        // Two meshes on the material: only the ticked one is cut.
        var corners = RowFixture.Corners();
        var model = SyntheticModel.Build([],
            [new SyntheticModel.Mesh(RowFixture.Mat, new SyntheticModel.Sub(0, Corners: corners)),
             new SyntheticModel.Mesh(RowFixture.Mat, new SyntheticModel.Sub(0, Corners: corners))], SyntheticModel.V6, (IReadOnlyList<string>?)null);
        using var f = new RowFixture(model: model);
        var ticked = ModelPartReader.Read(f.Current())!.Parts.Where(p => p.Island < 0 && p.Mesh == 0).ToList();
        Assert.True(f.Apply(parts: ticked).Ok);
        var draws = Draws(f.Current());
        Assert.Equal(4, draws.Where(d => d.Mesh == 1).Sum(d => d.Centroids.Count));
        Assert.All(draws.Where(d => d.Mesh == 1), d => Assert.Equal(RowFixture.Mat, d.Material));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-3")]
    [Trait("Plan", "AC-matcap-row-5")]
    public void Row_apply_changes_only_the_model_and_undo_returns_the_mod_to_its_snapshot()
    {
        using var f = new RowFixture();
        var before = f.Snapshot();
        var authorRedirects = Redirects(f.ModRoot);
        Assert.True(f.Apply().Ok);

        var after = f.Snapshot();
        // Only the model changed among the files that were there; the author's material bytes are untouched.
        foreach (var (rel, sha) in before)
            if (rel != Path.Combine("model", "top.mdl") && rel != "meta.json") Assert.Equal(sha, after[rel]);
        Assert.NotEqual(before[Path.Combine("model", "top.mdl")], after[Path.Combine("model", "top.mdl")]);
        // Every author redirect is still there, unchanged; the added ones are our own names only.
        var now = Redirects(f.ModRoot);
        foreach (var r in authorRedirects) Assert.Contains(now, x => x.Container.SameAs(r.Container) && x.GamePath == r.GamePath && x.File == r.File);
        Assert.All(now.Except(authorRedirects), r => Assert.Matches("_mcap|_mcut|/matcap/", r.GamePath.ToLowerInvariant()));

        var undo = MatcapWriter.Undo(f.ModRoot, RowFixture.ModelRel);
        Assert.True(undo.Ok, undo.Message);
        var restored = f.Snapshot();
        Assert.Equal(before.Keys.Order(), restored.Keys.Order());
        foreach (var (rel, sha) in before) Assert.True(sha == restored[rel], $"{rel} differs after undo");
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-4")]
    public void Cut_mirrors_every_container_that_ships_the_material()
    {
        using var f = new RowFixture(materialInOption: true);
        f.ColourOptions();
        var outcome = f.Apply();
        Assert.True(outcome.Ok, outcome.Message);
        var row = MatcapRecord.Read(f.ModRoot).Materials.Single().Row!;
        Assert.Equal(2, row.Files.Count);                       // one cut per option's own file
        var now = Redirects(f.ModRoot);
        foreach (var (option, src) in new[] { ("Red", "mat\\red.mtrl"), ("Blue", "mat\\blue.mtrl") })
        {
            var cut = Assert.Single(now, r => r.Container.Option == option && r.GamePath.Contains("_mcut"));
            var file = row.Files.Single(x => PenumbraModMeta.SameFile(x.File, cut.File));
            Assert.True(PenumbraModMeta.SameFile(src, file.Source.File!));
            // The cut keeps that option's colour table (its tint constant marks it).
            var cutBytes = File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, file.File));
            Assert.Equal(TextureLoader.ConstantValue(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, src)), 0x59BDA0B1),
                         TextureLoader.ConstantValue(cutBytes, 0x59BDA0B1));
        }
        Assert.DoesNotContain(now, r => r.Container.IsDefault && r.GamePath.Contains("_mcut") && r.GamePath.EndsWith(".mtrl"));

        Assert.True(MatcapWriter.Undo(f.ModRoot, RowFixture.ModelRel).Ok);
        Assert.DoesNotContain(Redirects(f.ModRoot), r => r.GamePath.Contains("_mcut") || r.GamePath.Contains("_mcap"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-48")]
    public void Game_ships_folder_material_gets_game_cut_in_default_data()
    {
        using var f = new RowFixture();
        // The item's game IMC lists folder 2 too, and the game ships the material there.
        f.Game[RowFixture.MatPath(2)] = RowFixture.Material(tint: 7);
        var env = f.Env();
        var p = MatcapStudio.Prepare(f.ModRoot, RowFixture.ModelRel, RowFixture.ModelPath, f.Current(), f.Pool(),
            MatcapChoice.OfImage(f.Image()), env);
        Assert.True(p.Ok, p.Message);
        // Folder 2 isn't in GearFolders without an IMC entry, so drive the cut planner directly with both folders.
        var target = MatcapPlan.TargetOf(RowFixture.ModelPath, out _)!;
        var (plan, refused) = MatcapRowCut.Make(new MatcapRowCut.Inputs(f.ModRoot, target, RowFixture.Mat, [1, 2], 1, env, 15,
            SubRowPick.B, "abc123", "/mt_c0201e6255_top_a_mcutabc1231.mtrl"));
        Assert.Null(refused);
        Assert.Contains(plan!.Folders, x => x.Folder == 2 && x.Case == MatcapRecord.RowFingerprint.CaseGame);
        var game = Assert.Single(plan.Files, x => x.Source.Game);
        Assert.Contains(game.Redirects, r => r.Container.IsDefault && r.GamePath.Contains("/v0002/"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-16")]
    public void Failure_after_each_step_rolls_back_cut_files_and_container_redirects()
    {
        foreach (var step in new[] { "files", "redirects", "model" })
        {
            using var f = new RowFixture(materialInOption: true);
            f.ColourOptions();
            var before = f.Snapshot();
            var p = f.Prepare();
            Assert.True(p.Ok, p.Message);
            MatcapWriter.AfterStep = s => { if (s == step) throw new IOException("boom"); };
            try { Assert.False(MatcapWriter.Apply(f.ModRoot, p.Request!).Ok); }
            finally { MatcapWriter.AfterStep = null; }
            var after = f.Snapshot();
            foreach (var (rel, sha) in before) Assert.True(sha == after.GetValueOrDefault(rel), $"{step}: {rel} differs after rollback");
            Assert.DoesNotContain(Redirects(f.ModRoot), r => r.GamePath.Contains("_mcut"));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-21")]
    public void Whole_part_apply_writes_no_row_and_version_1()
    {
        using var f = new RowFixture();
        var whole = f.Env(row: 0);
        var outcome = f.Apply(whole, ModelPartReader.Read(f.Current())!.Parts.Where(p => p.Island < 0).ToList());
        Assert.True(outcome.Ok, outcome.Message);
        var record = MatcapRecord.Read(f.ModRoot);
        Assert.Null(record.Materials.Single().Row);
        Assert.Equal(1, record.Version);
        Assert.DoesNotContain(Redirects(f.ModRoot), r => r.GamePath.Contains("_mcut"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-22")]
    public void Newer_record_version_is_unreadable_and_never_swept()
    {
        using var f = new RowFixture();
        Assert.True(f.Apply().Ok);
        var path = MatcapRecord.PathOf(f.ModRoot);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["Version"] = 3;
        File.WriteAllText(path, json.ToJsonString());
        Assert.True(MatcapRecord.Read(f.ModRoot).Unreadable);
        var redirects = Redirects(f.ModRoot);
        Assert.False(MatcapWriter.Undo(f.ModRoot, RowFixture.ModelRel).Ok);
        Assert.Equal(redirects.Count, Redirects(f.ModRoot).Count);
    }

    [Theory]
    [Trait("Plan", "EDGE-matcap-row-24")]
    [InlineData(SubRowPick.B, true)]
    [InlineData(SubRowPick.A, false)]
    public void Sub_row_A_and_B_cut_only_their_column(SubRowPick sub, bool cuts)
    {
        using var f = new RowFixture();
        var p = f.Prepare(f.Env(sub: sub));
        Assert.Equal(cuts, p.Ok);
        if (!cuts) Assert.Equal(MatcapRowCut.CodeEmpty, p.Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-31")]
    public void Threshold0_bc5_style_normal_keeps_off_row_texels_opaque()
    {
        using var f = new RowFixture(threshold: null, normal: null);
        // A normal whose blue is 0 everywhere (two-channel export): off the row the cut writes 255.
        var blueless = f.Tex(RowFixture.NormalRgba(blue: 0));
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "tex/n.tex"), blueless);
        Assert.True(f.Apply().Ok);
        var normal = MatcapRecord.Read(f.ModRoot).Materials.Single().Row!.Normals.Single();
        var n = f.Loader.LoadTexBytesAsRgba(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, normal.File)), "cut")!.Value;
        Assert.Equal(255, n.rgba[(10 * n.width + n.width - 3) * 4 + 2]);   // right half: off the row
        Assert.Equal(0, n.rgba[(10 * n.width + 3) * 4 + 2]);               // left half: the row
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-46")]
    public void Same_maps_different_threshold_class_get_two_bakes()
    {
        using var f = new RowFixture(materialInOption: true);
        f.ColourOptions();
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/blue.mtrl"), RowFixture.Material(threshold: 0.3f, tint: 2));
        var p = f.Prepare();
        Assert.True(p.Ok, p.Message);
        var cut = p.Request!.Materials.Single().Row!.Cut;
        Assert.Equal(2, cut.Bakes.Count);
        Assert.Contains(cut.Bakes, b => b.ThresholdZero);
        Assert.Contains(cut.Bakes, b => !b.ThresholdZero && Math.Abs(b.Threshold - 0.3f) < 1e-4);
    }

    [Theory]
    [Trait("Plan", "AC-matcap-row-8")]
    [Trait("Plan", "EDGE-matcap-row-1")]
    [Trait("Plan", "EDGE-matcap-row-2")]
    [Trait("Plan", "EDGE-matcap-row-3")]
    [Trait("Plan", "EDGE-matcap-row-37")]
    [Trait("Plan", "EDGE-matcap-row-45")]
    [Trait("Plan", "EDGE-matcap-row-33")]
    [InlineData("row-empty")]
    [InlineData("row-shader")]
    [InlineData("row-no-normal")]
    [InlineData("row-ambiguous-option")]
    [InlineData("row-file-swap")]
    [Trait("Plan", "EDGE-upgrade-vs-matcap-15")]
    public void Row_refusals_write_nothing(string code)
    {
        using var f = new RowFixture(materialInOption: code == "row-ambiguous-option");
        var env = f.Env();
        switch (code)
        {
            case "row-empty": env = f.Env(row: 9); break;
            case "row-shader":
                File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/top_a.mtrl"), RowFixture.Material(shader: "characterlegacy.shpk"));
                break;
            case "row-no-normal":
                File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/top_a.mtrl"),
                    SyntheticMatcapMod.Material(MatcapRowCut.CutShader, ["chara/x/base.tex"], [(0x115306BE, 0)], colorTableBytes: 2048));
                break;
            case "row-ambiguous-option": f.ColourOptions(sameName: true); break;
            case "row-file-swap":
                f.EditDefaults(swaps => swaps[RowFixture.NormalPath] = "chara/x/other_n.tex", "FileSwaps");
                break;
        }
        var before = f.Snapshot();
        var p = f.Prepare(env);
        Assert.False(p.Ok);
        Assert.Equal(code, p.Code);
        Assert.False(string.IsNullOrWhiteSpace(p.Message));
        var after = f.Snapshot();
        Assert.Equal(before.Count, after.Count);
        foreach (var (rel, sha) in before) Assert.Equal(sha, after[rel]);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-7")]
    [Trait("Plan", "EDGE-matcap-row-13")]
    public void Two_materials_and_non_gear_are_refused()
    {
        using var f = new RowFixture();
        var parts = ModelPartReader.Read(f.Current())!.Parts.Where(p => p.Island < 0).ToList();
        var mixed = parts.Select(p => new ModelPart
        {
            Mesh = p.Mesh, Submesh = p.Submesh, Island = p.Island, Label = p.Label, Material = p.Material, Triangles = p.Triangles,
            Ordinals = p.Ordinals, AttributeMask = p.AttributeMask, Min = p.Min, Max = p.Max, Toggleable = p.Toggleable,
        }).Append(new ModelPart
        {
            Mesh = 0, Submesh = 0, Island = -1, Label = "9.9", Material = "/mt_other.mtrl", Triangles = [], Ordinals = [],
            AttributeMask = 0, Min = default, Max = default, Toggleable = true,
        }).ToList();
        Assert.Equal(MatcapStudio.CodeRowOneMaterial, f.Prepare(parts: mixed).Code);

        var hair = MatcapStudio.Prepare(f.ModRoot, RowFixture.ModelRel, "chara/human/c0201/obj/hair/h0001/model/c0201h0001_hir.mdl",
            f.Current(), f.Pool(), MatcapChoice.OfImage(f.Image()), f.Env());
        Assert.Equal(MatcapStudio.CodeRowNotGear, hair.Code);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-6")]
    public void Change_through_a_cut_part_changes_the_owner_and_keeps_the_cut()
    {
        using var f = new RowFixture();
        Assert.True(f.Apply().Ok);
        var record = MatcapRecord.Read(f.ModRoot);
        var entry = record.Materials.Single();
        var cutPart = ModelPartReader.Read(f.Current())!.Parts.First(p => p.Material == entry.Row!.Cut);
        var names = MatcapPlan.ChangeNames([cutPart], record);
        Assert.Equal([entry.Name], names);
        var cutSha = entry.Row!.Files.Single().Sha8;
        var texSha = entry.Source.Tex!.Sha8;

        var outcome = MatcapStudio.ChangeAll(f.ModRoot, names, MatcapChoice.OfImage(f.Image("pearl.tex")), f.Env());
        Assert.True(outcome.Ok, outcome.Message);
        var after = MatcapRecord.Read(f.ModRoot).Materials.Single();
        Assert.NotEqual(texSha, after.Source.Tex!.Sha8);
        Assert.Equal(cutSha, HairPortPlan.Sha8(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, after.Row!.Files.Single().File))));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-36")]
    public void Orphan_cut_redirects_are_swept_files_kept()
    {
        using var f = new RowFixture();
        Assert.True(f.Apply().Ok);
        var row = MatcapRecord.Read(f.ModRoot).Materials.Single().Row!;
        // An older Proteus rewrote the record without its row.
        var path = MatcapRecord.PathOf(f.ModRoot);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["Materials"]![0]!.AsObject().Remove("Row");
        json["Version"] = 1;
        File.WriteAllText(path, json.ToJsonString());

        Assert.True(MatcapWriter.Undo(f.ModRoot, RowFixture.ModelRel).Ok);
        // The model no longer names the cut: its redirects are swept; the files themselves stay.
        Assert.DoesNotContain(Redirects(f.ModRoot), r => r.GamePath.Contains("_mcut") || r.File.Contains("_mcut"));
        Assert.True(File.Exists(MatcapWriter.Full(f.ModRoot, row.Files.Single().File)));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-26")]
    public void Cut_normal_is_bc7_unless_unaffordable_and_never_bc5()
    {
        foreach (var affordable in new[] { true, false })
        {
            using var f = new RowFixture();
            Assert.True(f.Apply(f.Env(affordable: () => affordable)).Ok);
            var normal = MatcapRecord.Read(f.ModRoot).Materials.Single().Row!.Normals.Single();
            var format = TextureLoader.TexFormatOf(MatcapWriter.Full(f.ModRoot, normal.File));
            Assert.NotEqual(0x6230u, format);                      // never BC5: it would lose the blue the cut lives in
            if (affordable) Assert.Equal(0x6432u, format);         // BC7
            else Assert.NotEqual(0x6432u, format);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-32")]
    public void No_colour_element_reads_as_alpha_one_and_sets_nothing()
    {
        // The reader treats a model without a colour element as alpha 1, so this covers "nothing to set": no refusal,
        // nothing recorded as set. (Raising alpha on a model that has vertex colour: MatcapRowWriterTests.)
        using var f = new RowFixture();
        var p = f.Prepare();
        Assert.True(p.Ok, p.Message);
        Assert.False(p.CutAlphaSet);
    }
}
