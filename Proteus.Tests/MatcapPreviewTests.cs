using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Matcaps tool's click preview: the files it would put on the character (<see cref="MatcapPreview.Build"/>), when
/// it can't be shown (<see cref="MatcapPanelModel.PreviewBlocked"/>), the state that follows the ticks
/// (<see cref="MatcapPreviewState"/>), and the panel's click.
/// </summary>
public class MatcapPreviewTests
{
    private static ModelPart Part(string material, string label = "1.1") => new()
    {
        Mesh = 0, Submesh = 0, Island = -1, Label = label, Material = material, Triangles = [0, 1, 2], Ordinals = [0],
        AttributeMask = 0, Min = default, Max = default, Toggleable = true,
    };

    private static MatcapChoice Img(string name = "a.png") => MatcapChoice.OfImage(new MatcapImage(name, name, false));

    private static MatcapPreview.Input Input(MatcapFixture f, MatcapChoice choice, IReadOnlyList<ModelPart> ticked, string? modRoot = null) =>
        new(modRoot ?? f.ModRoot, MatcapFixture.ModelRel, MatcapFixture.ModelPath, [MatcapFixture.ModelPath], f.Current(), ticked, choice,
            f.Env(), f.Mods);

    private static IReadOnlyList<ModelPart> OnMatcap(MatcapFixture f) =>
        ModelPartReader.Read(f.Current())!.Parts.Where(p => MatcapPlan.IsOnMatcap(p) && p.Island < 0).ToList();

    private static IReadOnlyList<string> Paths(MatcapPreview.Built b, string ext) =>
        b.Files.Where(x => x.Extension == ext).SelectMany(x => x.GamePaths).ToList();

    // ── Build ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-matcap-preview-5")]
    [Trait("Plan", "EDGE-matcap-preview-19")]
    public void Build_new_maps_model_materials_textures_and_writes_nothing()
    {
        using var f = new MatcapFixture();
        var snapshot = f.Snapshot();
        var built = MatcapPreview.Build(Input(f, MatcapChoice.OfImage(f.Image("gem.tex")), f.Parts("1.2")));
        Assert.True(built.Ok, built.Message);

        Assert.Equal([MatcapFixture.ModelPath], Paths(built, ".mdl"));
        var model = built.Files.Single(x => x.Extension == ".mdl").Bytes;
        var name = SecondSkinWriter.MaterialNames(model).Single(MatcapPlan.IsMatcapName);
        Assert.Contains($"chara/equipment/e6255/material/v0001{name}", Paths(built, ".mtrl"), StringComparer.OrdinalIgnoreCase);
        var token = MatcapPlan.TokenFor("Gear Mod");
        Assert.Contains(MatcapWriter.MaskGamePath(token), Paths(built, ".tex"));
        Assert.Contains(MatcapWriter.ImageTexGamePath(token, name), Paths(built, ".tex"));
        // Nothing in the mod changed, no record, and the prepare's temp textures are gone.
        Assert.Equal(snapshot, f.Snapshot());
        Assert.False(MatcapRecord.Exists(f.ModRoot));
        Assert.Empty(Directory.Exists(f.Temp) ? Directory.GetFiles(f.Temp, "*.tex") : []);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-5")]
    public void Build_change_image_and_library()
    {
        // Image: the model again, the material re-emitted as it is, the new texture at its path.
        using (var f = new MatcapFixture())
        {
            f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
            var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
            var snapshot = f.Snapshot();
            var built = MatcapPreview.Build(Input(f, MatcapChoice.OfImage(f.Image("pearl.tex", seed: 9)), OnMatcap(f)));
            Assert.True(built.Ok, built.Message);
            Assert.Equal(f.Current(), built.Files.Single(x => x.Extension == ".mdl").Bytes);
            var mtrl = built.Files.Single(x => x.Extension == ".mtrl");
            Assert.Equal(mat.GamePaths, mtrl.GamePaths);
            Assert.Equal(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, mat.File)), mtrl.Bytes);
            Assert.Equal([mat.Source.Tex!.GamePath], Paths(built, ".tex"));
            Assert.NotEqual(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, mat.Source.Tex.File)), built.Files.Single(x => x.Extension == ".tex").Bytes);
            Assert.Equal(snapshot, f.Snapshot());
        }
        // Library on another slot: the material rebuilt for the new slot, which gets a copy of the option's file.
        using (var f = new MatcapFixture())
        {
            var a = f.Library("chara/common/texture/testlib_a.tex", "LibA");
            var b = f.Library("chara/common/texture/testlib_b.tex", "LibB");
            f.Apply(MatcapChoice.OfLibrary(a, "Pearl"), "1.2");
            var built = MatcapPreview.Build(Input(f, MatcapChoice.OfLibrary(b, "Gem Red"), OnMatcap(f)));
            Assert.True(built.Ok, built.Message);
            var h = MatcapMaterial.ReadHeader(built.Files.Single(x => x.Extension == ".mtrl").Bytes)!;
            Assert.Equal(b.Texture, h.TextureOf(MatcapMaterial.SamplerMatcap));
            Assert.Equal([b.Texture], Paths(built, ".tex"));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-7")]
    [Trait("Plan", "EDGE-matcap-preview-19")]
    public void Build_library_carries_a_private_copy_of_the_option()
    {
        using var f = new MatcapFixture();
        var lib = f.Library();
        var built = MatcapPreview.Build(Input(f, MatcapChoice.OfLibrary(lib, "Pearl"), f.Parts("1.2")));
        Assert.True(built.Ok, built.Message);
        var opt = lib.Options.Single(o => o.Name == "Pearl");
        var slot = built.Files.Single(x => x.GamePaths.Contains(lib.Texture));
        Assert.Equal(File.ReadAllBytes(Path.Combine(f.Mods, lib.Mod, opt.File)), slot.Bytes);
        // No setting is part of a preview: nothing here talks to Penumbra; the bytes go to the private folder (LayOut).
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-20")]
    public void Build_library_file_missing()
    {
        using var f = new MatcapFixture();
        var lib = f.Library();
        File.Delete(Path.Combine(f.Mods, lib.Mod, lib.Options.Single(o => o.Name == "Pearl").File));
        var built = MatcapPreview.Build(Input(f, MatcapChoice.OfLibrary(lib, "Pearl"), f.Parts("1.2")));
        Assert.Equal(MatcapPreview.CodeLibraryMissing, built.Code);
        Assert.Equal(string.Format(Strings.Matcap.LibraryMissingFmt, lib.Name), built.Message);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-2")]
    public void Build_mixed_refused()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var plain = ModelPartReader.Read(f.Current())!.Parts.First(p => !MatcapPlan.IsOnMatcap(p) && p.Island < 0);
        var built = MatcapPreview.Build(Input(f, MatcapChoice.OfImage(f.Image("b.tex")), [OnMatcap(f)[0], plain]));
        Assert.Equal("already-matcap", built.Code);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-8")]
    public void Build_change_unreadable_record_and_not_square_refused_without_temps()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var on = OnMatcap(f);
        var temps = Directory.GetFiles(f.Temp, "*.tex").ToHashSet();
        Assert.Equal("not-square", MatcapPreview.Build(Input(f, MatcapChoice.OfImage(f.Image("wide.tex", w: 64, h: 32)), on)).Code);
        Assert.Empty(Directory.GetFiles(f.Temp, "*.tex").Where(t => !temps.Contains(t)));
        File.WriteAllText(MatcapRecord.PathOf(f.ModRoot), "{ not json");
        Assert.Equal(MatcapWriter.CodeUnreadable, MatcapPreview.Build(Input(f, MatcapChoice.OfImage(f.Image("b.tex", seed: 3)), on)).Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-9")]
    public void Build_non_gear_or_nowhere_drawn_refused()
    {
        using var f = new MatcapFixture();
        var hair = new MatcapPreview.Input(f.ModRoot, MatcapFixture.ModelRel, "chara/human/c0101/obj/hair/h0001/model/c0101h0001_hir.mdl",
                                           ["chara/human/c0101/obj/hair/h0001/model/c0101h0001_hir.mdl"], f.Current(), f.Parts("1.2"),
                                           MatcapChoice.OfImage(f.Image("gem.tex")), f.Env(), f.Mods);
        Assert.Equal(MatcapPreview.CodeNotGear, MatcapPreview.Build(hair).Code);
        var nowhere = Input(f, MatcapChoice.OfImage(f.Image("gem.tex")), f.Parts("1.2")) with { DrawnAt = [] };
        Assert.Equal(MatcapPreview.CodeNotGear, MatcapPreview.Build(nowhere).Code);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-8")]
    public void Build_change_unreadable_material_becomes_a_note()
    {
        using var f = new MatcapFixture();
        var lib = f.Library();
        f.Apply(MatcapChoice.OfLibrary(lib, "Pearl"), "1.2");
        var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
        MatcapPreview.Built built;
        using (new FileStream(MatcapWriter.Full(f.ModRoot, mat.File), FileMode.Open, FileAccess.Read, FileShare.None))
            built = MatcapPreview.Build(Input(f, MatcapChoice.OfLibrary(lib, "Gem Red"), OnMatcap(f)));
        Assert.Equal(MatcapWriter.CodeFailed, built.Code);
        Assert.StartsWith(Strings.Matcap.PreviewFailedFmt[..Strings.Matcap.PreviewFailedFmt.IndexOf('(')], built.Message);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-10")]
    public void Build_managed_root_refused()
    {
        using var f = new MatcapFixture(folder: SidecarDiscoveryService.ManagedModDir);
        var built = MatcapPreview.Build(Input(f, MatcapChoice.OfImage(f.Image("gem.tex")), f.Parts("1.2")));
        Assert.Equal(MatcapWriter.CodeManaged, built.Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-12")]
    [Trait("Plan", "AC-matcap-preview-8")]
    public void Build_not_square_refused()
    {
        using var f = new MatcapFixture();
        var built = MatcapPreview.Build(Input(f, MatcapChoice.OfImage(f.Image("wide.tex", w: 64, h: 32)), f.Parts("1.2")));
        Assert.Equal("not-square", built.Code);
        Assert.Contains("wide.tex", built.Message);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-8")]
    public void Build_refusals_become_the_note()
    {
        using var f = new MatcapFixture();
        var built = MatcapPreview.Build(Input(f, MatcapChoice.OfImage(f.Image("wide.tex", w: 64, h: 32)), f.Parts("1.2")));
        var s = new MatcapPreviewState();
        s.Click(Img());
        Assert.Equal(MatcapPreviewState.Step.Build, s.Next("k", null, false));
        Assert.True(s.Accept(s.Generation, "k", "k"));
        s.Failed("k", built.Message);
        Assert.Equal(built.Message, s.Note);
        // The same key never rebuilds over and over.
        Assert.Equal(MatcapPreviewState.Step.Note, s.Next("k", null, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-25")]
    public void Build_shows_open_model_only()
    {
        // A sibling body option at the same game path whose parts line up: Apply would split it too; the preview doesn't.
        using var f = new MatcapFixture();
        var sibling = MatcapWriterTests.SiblingOf(f, "model/top_b.mdl", 1.05f);
        MatcapWriterTests.AddOption(f, "Body", ("YAB", MatcapFixture.ModelRel), ("Rue", "model/top_b.mdl"));
        Assert.Contains("model/top_b.mdl", f.Prepare(MatcapChoice.OfImage(f.Image("x.tex")), "1.2").Request!.Models.Select(m => m.Rel.Replace('\\', '/')));
        var built = MatcapPreview.Build(Input(f, MatcapChoice.OfImage(f.Image("gem.tex")), f.Parts("1.2")));
        Assert.True(built.Ok, built.Message);
        Assert.Single(built.Files.Where(x => x.Extension == ".mdl"));
        Assert.Equal(sibling, File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, "model/top_b.mdl")));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-29")]
    public void Change_preview_reemits_unchanged_material()
    {
        // Same library, another option: the material's bytes don't change, yet it is in every build.
        using var f = new MatcapFixture();
        var lib = f.Library();
        f.Apply(MatcapChoice.OfLibrary(lib, "Pearl"), "1.2");
        var built = MatcapPreview.Build(Input(f, MatcapChoice.OfLibrary(lib, "Gem Red"), OnMatcap(f)));
        Assert.True(built.Ok, built.Message);
        var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
        Assert.Equal(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, mat.File)), built.Files.Single(x => x.Extension == ".mtrl").Bytes);
    }

    // ── PreviewBlocked ─────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-matcap-preview-8")]
    public void PreviewBlocked_notes_in_order()
    {
        var s = Strings.Matcap;
        var r = new MatcapRecord();
        IReadOnlyList<ModelPart> one = [Part("/a.mtrl")];
        Assert.Equal(s.NoPenumbra, MatcapPanelModel.PreviewBlocked(false, [], MatcapPlan.Kind.Hair, true, true, null, 0, r));
        Assert.Equal(s.PreviewTick, MatcapPanelModel.PreviewBlocked(true, [], MatcapPlan.Kind.Hair, true, true, Img(), 3, r));
        Assert.Equal(s.PreviewGearOnly, MatcapPanelModel.PreviewBlocked(true, one, MatcapPlan.Kind.Hair, true, true, Img(), 3, r));
        Assert.Equal(s.PreviewGameGear, MatcapPanelModel.PreviewBlocked(true, one, MatcapPlan.Kind.Gear, true, true, Img(), 3, r));
        Assert.NotNull(MatcapPanelModel.PreviewBlocked(true, one, MatcapPlan.Kind.Gear, false, true, Img(), 3, r));
        Assert.Contains("10", MatcapPanelModel.PreviewBlocked(true, one, MatcapPlan.Kind.Gear, false, false, Img(), 0, r));
        Assert.Null(MatcapPanelModel.PreviewBlocked(true, one, MatcapPlan.Kind.Gear, false, false, Img(), 3, r));
        Assert.Null(MatcapPanelModel.PreviewBlocked(true, one, null, false, false, Img(), 3, r));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-8")]
    public void PreviewBlocked_nothing_ticked() =>
        Assert.Equal(Strings.Matcap.PreviewTick, MatcapPanelModel.PreviewBlocked(true, [], MatcapPlan.Kind.Gear, false, false, Img(), 3, new MatcapRecord()));

    [Theory]
    [Trait("Plan", "EDGE-matcap-preview-9")]
    [InlineData("Hair")]
    [InlineData("Tail")]
    [InlineData("Ear")]
    [InlineData("Face")]
    public void PreviewBlocked_non_gear_kinds(string kind) =>
        Assert.Equal(Strings.Matcap.PreviewGearOnly, MatcapPanelModel.PreviewBlocked(true, [Part("/a.mtrl")], Enum.Parse<MatcapPlan.Kind>(kind), false, false, Img(), 3, new MatcapRecord()));

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-10")]
    public void PreviewBlocked_imported_piece() =>
        Assert.Contains("imported", MatcapPanelModel.PreviewBlocked(true, [Part("/a.mtrl")], MatcapPlan.Kind.Gear, false, true, Img(), 3, new MatcapRecord()));

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-11")]
    public void PreviewBlocked_no_penumbra() =>
        Assert.Equal(Strings.Matcap.NoPenumbra, MatcapPanelModel.PreviewBlocked(false, [Part("/a.mtrl")], MatcapPlan.Kind.Gear, false, false, Img(), 3, new MatcapRecord()));

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-21")]
    public void PreviewBlocked_game_gear() =>
        Assert.Equal(Strings.Matcap.PreviewGameGear, MatcapPanelModel.PreviewBlocked(true, [Part("/a.mtrl")], MatcapPlan.Kind.Gear, true, false, Img(), 3, new MatcapRecord()));

    // ── the state ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-matcap-preview-6")]
    [Trait("Plan", "EDGE-matcap-preview-26")]
    public void State_rebuilds_on_new_key_and_ends_when_unticked()
    {
        var s = new MatcapPreviewState();
        Assert.Equal(MatcapPreviewState.Step.None, s.Next("a", null, false));   // nothing clicked yet
        s.Click(Img());
        Assert.Equal(MatcapPreviewState.Step.Build, s.Next("a", null, false));
        Assert.True(s.Accept(s.Generation, "a", "a"));
        s.Shown("a", null);
        Assert.Equal(MatcapPreviewState.Step.None, s.Next("a", null, false));
        // Another tick or click: a new key, a new build.
        Assert.Equal(MatcapPreviewState.Step.Build, s.Next("b", null, false));
        Assert.True(s.Accept(s.Generation, "b", "b"));
        s.Shown("b", null);
        // Every part unticked: it comes down, and comes back when parts are ticked again.
        Assert.Equal(MatcapPreviewState.Step.End, s.Next("c", Strings.Matcap.PreviewTick, false));
        s.Ended(keepWanted: true);
        Assert.True(s.Wanted);
        Assert.Equal(MatcapPreviewState.Step.Note, s.Next("c", Strings.Matcap.PreviewTick, false));
        Assert.Equal(MatcapPreviewState.Step.Build, s.Next("d", null, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-26")]
    public void State_ends_when_unticked()
    {
        var s = new MatcapPreviewState();
        s.Click(Img());
        s.Next("a", null, false);
        s.Accept(s.Generation, "a", "a");
        s.Shown("a", null);
        Assert.Equal(MatcapPreviewState.Step.End, s.Next("", Strings.Matcap.PreviewTick, false));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-7")]
    public void State_ended_ignores_late_builds()
    {
        var s = new MatcapPreviewState();
        s.Click(Img());
        s.Next("a", null, false);
        int gen = s.Generation;
        s.Ended();
        Assert.False(s.Active);
        Assert.False(s.Accept(gen, "a", "a"));
        Assert.Equal(MatcapPreviewState.Step.None, s.Next("a", null, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-13")]
    public void State_discards_stale_generation_and_key()
    {
        var s = new MatcapPreviewState();
        s.Click(Img());
        s.Next("a", null, false);
        int first = s.Generation;
        Assert.Equal(MatcapPreviewState.Step.None, s.Next("b", null, false));   // one build at a time
        Assert.False(s.Accept(first, "a", "b"));                                 // built for a key no longer ticked
        Assert.Equal(MatcapPreviewState.Step.Build, s.Next("b", null, false));
        Assert.False(s.Accept(first, "a", "b"));                                 // an older generation never lands
        Assert.True(s.Accept(s.Generation, "b", "b"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-14")]
    public void State_busy_waits()
    {
        var s = new MatcapPreviewState();
        s.Click(Img());
        Assert.Equal(MatcapPreviewState.Step.None, s.Next("a", null, busy: true));
        Assert.Null(s.BuildingKey);
        Assert.Equal(MatcapPreviewState.Step.Build, s.Next("a", null, busy: false));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-30")]
    public void State_keeps_drawn_note_until_ended()
    {
        var s = new MatcapPreviewState();
        s.Click(Img());
        s.Next("a", null, false);
        s.Accept(s.Generation, "a", "a");
        s.Shown("a", "drawn elsewhere");
        s.Next("b", null, false);
        s.Accept(s.Generation, "b", "b");
        s.Shown("b", null);                       // later pushes don't recompute it (our redirect wins by then)
        Assert.Equal("drawn elsewhere", s.DrawnNote);
        s.Ended();
        Assert.Null(s.DrawnNote);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-32")]
    public void PushThrottle_allows_one_redraw_push_per_interval()
    {
        Assert.True(MatcapPreviewState.PushDue(1000, 0, redraw: true, 150));
        Assert.False(MatcapPreviewState.PushDue(1100, 1000, redraw: true, 150));
        Assert.True(MatcapPreviewState.PushDue(1150, 1000, redraw: true, 150));
        Assert.True(MatcapPreviewState.PushDue(1001, 1000, redraw: false, 150));   // in-place pushes aren't throttled
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-6")]
    public void Key_follows_model_choice_and_ticks_in_any_order()
    {
        var a = MatcapPreviewState.Key("m.mdl", Img("x"), [Part("/a.mtrl", "1.2"), Part("/a.mtrl", "1.1")]);
        Assert.Equal(a, MatcapPreviewState.Key("m.mdl", Img("x"), [Part("/a.mtrl", "1.1"), Part("/a.mtrl", "1.2")]));
        Assert.NotEqual(a, MatcapPreviewState.Key("m.mdl", Img("y"), [Part("/a.mtrl", "1.1"), Part("/a.mtrl", "1.2")]));
        Assert.NotEqual(a, MatcapPreviewState.Key("m.mdl", Img("x"), [Part("/a.mtrl", "1.1")]));
    }

    // ── the panel's click ──────────────────────────────────────────────────

    private static MatcapPanel.Context Ctx(IReadOnlyList<MatcapImage> images, IReadOnlyList<MatcapImage> pack, MatcapIndex.State? libs,
                                           Action<MatcapChoice> clicked) => new()
    {
        Penumbra = true, Busy = false, Ticked = [], MaterialRoom = 1, Images = images, ImageFolder = null, Libraries = libs,
        Scanning = false, ModsRoot = null, Record = new MatcapRecord(), States = new Dictionary<string, MatcapWriter.State>(),
        Entries = [], GroupFiles = [], LibraryStatus = _ => null!, Apply = (_, _) => { }, Undo = () => { },
        Forget = () => { }, Rescan = () => { }, RefreshImages = () => { }, PackImages = pack, Clicked = clicked,
    };

    [Fact]
    [Trait("Plan", "AC-matcap-preview-5")]
    public void Click_fires_Clicked_with_the_choice_for_each_source()
    {
        var mine = new MatcapImage("mine.png", "mine.png", false);
        var gold = new MatcapImage("Gold.png", "gold.png", false, true);
        var lib = new MatcapLibrary("Lib", "Lib", "", "Slot", "t.tex", "t.mtrl", [new MatcapOption("Pearl", "p.tex")], []);
        var got = new List<MatcapChoice>();
        var panel = new MatcapPanel();
        var ctx = Ctx([mine], [gold], new MatcapIndex.State([lib], new Dictionary<string, IReadOnlyList<string>>()), got.Add);

        panel.Click(ctx, MatcapPanel.SourceKind.Images, "mine.png");
        panel.Click(ctx, MatcapPanel.SourceKind.Proteus, "Gold.png");
        panel.OpenLibrary(lib);
        panel.Click(ctx, MatcapPanel.SourceKind.Libraries, "Pearl");

        Assert.Equal(3, got.Count);
        Assert.Same(mine, got[0].Image);
        Assert.Same(gold, got[1].Image);
        Assert.Equal(("Lib", "Pearl"), (got[2].Library!.Mod, got[2].Option));
        Assert.Equal(got[2], panel.Choice(ctx));
    }
}

/// <summary>The Studio's wiring of the click preview, read from the source (the panel needs ImGui).</summary>
public class MatcapPreviewWiringTests
{
    private static string Gui([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Gui");
    private static string Main() => File.ReadAllText(Path.Combine(Gui(), "PartsPanel.cs"));
    private static string Wiring() => File.ReadAllText(Path.Combine(Gui(), "PartsPanel.Matcaps.cs"));
    private static string Services(string file) => File.ReadAllText(Path.Combine(Gui(), "..", "Services", file));

    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced {signature}");
        return "";
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-7")]
    public void Preview_ends_on_apply_undo_forget_tool_model_view_leave()
    {
        var main = Main();
        Assert.Contains("matcapPreview.Ended(keepMatcapWanted)", Method(main, "private void EndLivePreview("));
        Assert.Contains("if (tool == Tool.Matcap && matcapPreview.Active) EndLivePreview(refreshGame: true);", Method(main, "private void SwitchTool(Tool value)"));
        foreach (var sig in new[] { "private void ResetForNewModel()", "private void SwitchView(bool modelView)", "public void Leave(", "private void SelectMod(string dir)" })
            Assert.Contains("EndLivePreview(", Method(main, sig));

        var w = Wiring();
        var apply = Method(w, "private void StartMatcapApply(");
        int down = apply.IndexOf("TakeDownMatcapPreviewForJob();", StringComparison.Ordinal);
        Assert.True(down >= 0 && down < apply.IndexOf("Task.Run(", StringComparison.Ordinal) && down < apply.IndexOf("ApplyMatcapToVanilla(", StringComparison.Ordinal));
        var undo = Method(w, "private void StartMatcapUndo(");
        Assert.True(undo.IndexOf("TakeDownMatcapPreviewForJob();", StringComparison.Ordinal) is var u && u >= 0 && u < undo.IndexOf("Task.Run(", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-15")]
    public void Refused_job_pays_the_owed_redraw()
    {
        var w = Wiring();
        Assert.Contains("matcapRedrawOwed |= shown;", Method(w, "private void TakeDownMatcapPreviewForJob()"));
        var finish = Method(w, "private void Finish(MatcapJobResult r)");
        int refuse = finish.IndexOf("if (!r.Outcome.Ok)", StringComparison.Ordinal);
        int pay = finish.IndexOf("PayMatcapRedraw();", StringComparison.Ordinal);
        int clear = finish.IndexOf("matcapRedrawOwed = false;", StringComparison.Ordinal);
        Assert.True(refuse >= 0 && pay > refuse && clear > pay);
        Assert.Contains("PayMatcapRedraw();", Method(w, "private void FinishMatcapJob()"));
        Assert.Contains("compositor.RedrawForChangedModel(\"matcap preview\")", Method(w, "private void PayMatcapRedraw()"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-16")]
    public void PushFiles_redraws_when_gear_cannot_reload()
    {
        var src = Services("LiveBrushPreview.cs");
        Assert.Contains("redraw ? Reload.Redraw : Reload.InPlaceOrRedraw", Method(src, "public bool PushFiles("));
        Assert.Contains("if (reload == Reload.InPlaceOrRedraw) compositor.RedrawForChangedModel(\"matcap preview\");", src);
        Assert.Contains("bool redraw = preview.Unsupported;", Method(Wiring(), "private void PushMatcapPreview("));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-23")]
    public void Brush_ticks_stand_down_while_matcap_preview_active()
    {
        var main = Main();
        Assert.Contains("if (matcapPreview.Active) return;", Method(main, "private void TickLivePreview()"));
        Assert.Contains("matcapPreview.Active) return;", Method(main, "private void RetryOwedShare()"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-23")]
    public void Entering_Matcaps_ends_any_brush_preview()
    {
        // Any brush preview (hiding locked parts, or a shared save) comes down on entering Matcaps.
        var sw = Method(Main(), "private void SwitchTool(Tool value)");
        int at = sw.IndexOf("if (value == Tool.Matcap)", StringComparison.Ordinal);
        int enter = sw.IndexOf("if (preview.Active) EndLivePreview(refreshGame: true);", at, StringComparison.Ordinal);
        Assert.True(at >= 0 && enter > at && enter < sw.IndexOf("tool = value;", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-30")]
    public void Drawn_note_resolves_with_nothing_of_ours_redirected()
    {
        // A brush preview still up would win the game path at int.MaxValue: it comes down before the drawn-file check.
        var push = Method(Wiring(), "private void PushMatcapPreview(");
        int down = push.IndexOf("if (preview.Active) preview.End(redraw: false);", StringComparison.Ordinal);
        Assert.True(down >= 0 && down < push.IndexOf("DrawnElsewhere(", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-8")]
    [Trait("Plan", "EDGE-matcap-preview-30")]
    public void Drawn_note_computed_before_the_first_push()
    {
        var push = Method(Wiring(), "private void PushMatcapPreview(");
        int drawn = push.IndexOf("DrawnElsewhere(", StringComparison.Ordinal);
        int files = push.IndexOf("preview.PushFiles(", StringComparison.Ordinal);
        Assert.True(drawn >= 0 && drawn < files);
        Assert.Contains("matcapPreview.ShownKey == null", push);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-32")]
    public void Redraw_pushes_wait_for_PreviewIntervalMs()
    {
        var push = Method(Wiring(), "private void PushMatcapPreview(");
        Assert.Contains("MatcapPreviewState.PushDue(nowMs, lastMatcapPushAt, redraw, PreviewIntervalMs)", push);
        Assert.Contains("QueueMatcapPump(", push);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-14")]
    public void Pump_waits_for_a_running_job()
    {
        var pump = Method(Wiring(), "private void PumpMatcapPreview()");
        Assert.Contains("matcapJob is { IsCompleted: false }", pump);
        Assert.Contains("preview.Busy", pump);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-5")]
    public void Ticked_wash_steps_aside_while_a_preview_is_building_or_shown()
    {
        // The amber tint of ticked parts covered the parts the preview recolours: under Matcaps it is off meanwhile.
        var main = Main();
        Assert.Contains("if (MatcapPreviewHidesTicks ||", Method(main, "private bool PartTicked(int index)"));
        Assert.Contains("tool == Tool.Matcap && matcapPreview.HidesTicks(matcapPending != null)", main);
        // The tint's cache is rebuilt when the preview starts or ends.
        int at = main.IndexOf("private int TickedVersion()", StringComparison.Ordinal);
        Assert.Contains("MatcapPreviewHidesTicks ?", main[at..main.IndexOf(';', at)]);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-5")]
    public void Ticks_are_hidden_only_while_building_pending_or_shown()
    {
        var s = new MatcapPreviewState();
        Assert.False(s.HidesTicks(false));
        s.Click(MatcapChoice.OfImage(new MatcapImage("a.png", "a.png", false)));
        Assert.False(s.HidesTicks(false));                    // wanted, nothing built yet
        s.Next("a", null, false);
        Assert.True(s.HidesTicks(false));                     // building
        Assert.True(s.Accept(s.Generation, "a", "a"));
        Assert.True(s.HidesTicks(true));                      // built, push waiting
        Assert.False(s.HidesTicks(false));
        s.Failed("a", "not square");
        Assert.False(s.HidesTicks(false));                    // a refused build brings the tint back
        s.Next("b", null, false);
        s.Accept(s.Generation, "b", "b");
        s.Shown("b", null);
        Assert.True(s.HidesTicks(false));                     // shown
        Assert.Equal(MatcapPreviewState.Step.End, s.Next("", Strings.Matcap.PreviewTick, false));
        s.Ended(keepWanted: true);
        Assert.False(s.HidesTicks(false));                    // ended (still following the ticks): tint back
    }

    [Fact]
    public void Proteus_matcaps_are_listed_first_and_open_first()
    {
        var panel = File.ReadAllText(Path.Combine(Gui(), "MatcapPanel.cs"));
        var draw = Method(panel, "public void Draw(Context ctx)");
        Assert.True(draw.IndexOf("s.SourceProteus", StringComparison.Ordinal) < draw.IndexOf("s.SourceImages", StringComparison.Ordinal));
        Assert.Contains("private SourceKind source = SourceKind.Proteus;", panel);
    }
}
