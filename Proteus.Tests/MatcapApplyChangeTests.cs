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
/// Apply on parts that already have a matcap changes that matcap in place (the "Use the chosen matcap" button is gone):
/// what Apply decides from the ticked parts, why it is disabled, what the pane says the change reaches, and
/// <see cref="MatcapStudio.ChangeAll"/> on a synthetic mod.
/// </summary>
public class MatcapApplyChangeTests
{
    private static ModelPart Part(string material, string label = "1.1", uint mask = 0) => new()
    {
        Mesh = 0, Submesh = 0, Island = -1, Label = label, Material = material, Triangles = [0, 1, 2], Ordinals = [0],
        AttributeMask = mask, Min = default, Max = default, Toggleable = true,
    };

    private static IReadOnlyList<ModelPart> OnMatcap(MatcapFixture f) =>
        ModelPartReader.Read(f.Current())!.Parts.Where(p => MatcapPlan.IsOnMatcap(p) && p.Island < 0).ToList();

    private static IReadOnlyList<ModelPart> Plain(MatcapFixture f) =>
        ModelPartReader.Read(f.Current())!.Parts.Where(p => !MatcapPlan.IsOnMatcap(p) && p.Island < 0).ToList();

    private static MatcapRecord.MaterialEntry Entry(string name, MatcapRecord.SourceRef src) => new() { Name = name, Source = src };

    private static MatcapRecord.SourceRef ImageSrc => new() { Kind = MatcapRecord.SourceRef.KindImage, Image = "a.png" };
    private static MatcapRecord.SourceRef LibrarySrc => new() { Kind = MatcapRecord.SourceRef.KindLibrary, Library = new MatcapRecord.LibraryRef { Texture = "t.tex" } };

    private static string Gui([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Gui");

    // ── what Apply decides ─────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-matcap-preview-2")]
    public void Matcap_parts_are_tickable_skin_is_not()
    {
        Assert.True(MatcapPlan.Tickable(Part("/mt_c0201e6255_top_a.mtrl")));
        Assert.True(MatcapPlan.Tickable(Part("/mt_c0201e6255_top_a_mcap3fa21c1.mtrl")));
        Assert.False(MatcapPlan.Tickable(Part("/mt_c0201b0001_a.mtrl")));
        Assert.Equal(Strings.Matcap.SkinTip, MatcapPanelModel.UntickableTip(Part("/mt_c0201b0001_bibo.mtrl")));
        Assert.Equal(Strings.Matcap.NotHornTip, MatcapPanelModel.UntickableTip(Part("/mt_x_mcap3fa21c1.mtrl")));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-2")]
    public void KindOfApply_and_ChangeNames()
    {
        var a = Part("/a_mcap3fa21c1.mtrl", "1.1");
        var a2 = Part("/A_MCAP3FA21C1.mtrl", "1.2");
        var b = Part("/b_mcap3fa21c2.mtrl", "2.1");
        var plain = Part("/c.mtrl", "3.1");
        Assert.Equal(MatcapPlan.ApplyKind.None, MatcapPlan.KindOfApply([]));
        Assert.Equal(MatcapPlan.ApplyKind.New, MatcapPlan.KindOfApply([plain]));
        Assert.Equal(MatcapPlan.ApplyKind.Change, MatcapPlan.KindOfApply([a, b]));
        Assert.Equal(MatcapPlan.ApplyKind.Mixed, MatcapPlan.KindOfApply([a, plain]));
        Assert.Equal(["/a_mcap3fa21c1.mtrl", "/b_mcap3fa21c2.mtrl"], MatcapPlan.ChangeNames([a, a2, b, plain]));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-3")]
    public void Apply_disabled_reasons_mixed_crosskind_room()
    {
        var s = Strings.Matcap;
        var img = MatcapChoice.OfImage(new MatcapImage("b.png", "b.png", false));
        var record = new MatcapRecord();
        record.Materials.Add(Entry("/a_mcap3fa21c1.mtrl", ImageSrc));
        var onA = Part("/a_mcap3fa21c1.mtrl");
        var plain = Part("/c.mtrl", "2.1");

        Assert.Equal(s.NoPenumbra, MatcapPanelModel.ApplyDisabledReason(false, [plain], img, 3, record));
        Assert.Equal(s.NeedParts, MatcapPanelModel.ApplyDisabledReason(true, [], img, 3, record));
        Assert.Equal(s.NeedMatcap, MatcapPanelModel.ApplyDisabledReason(true, [plain], null, 3, record));
        Assert.Equal(s.AlreadyMatcap, MatcapPanelModel.ApplyDisabledReason(true, [onA, plain], img, 3, record));
        Assert.Null(MatcapPanelModel.ApplyDisabledReason(true, [onA], img, 3, record));
        Assert.Contains("10", MatcapPanelModel.ApplyDisabledReason(true, [plain], img, 0, record));
        Assert.Null(MatcapPanelModel.ApplyDisabledReason(true, [plain], img, 3, record));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-2")]
    public void Mixed_is_disabled()
    {
        var img = MatcapChoice.OfImage(new MatcapImage("b.png", "b.png", false));
        var record = new MatcapRecord();
        record.Materials.Add(Entry("/a_mcap3fa21c1.mtrl", ImageSrc));
        Assert.Equal(Strings.Matcap.AlreadyMatcap,
            MatcapPanelModel.ApplyDisabledReason(true, [Part("/a_mcap3fa21c1.mtrl"), Part("/c.mtrl", "2.1")], img, 3, record));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-3")]
    public void Cross_kind_is_disabled()
    {
        var record = new MatcapRecord();
        record.Materials.Add(Entry("/a_mcap3fa21c1.mtrl", ImageSrc));
        var lib = new MatcapLibrary("Lib", "Lib", "", "Group", "t.tex", "t.mtrl", [new MatcapOption("Pearl", "p.tex")], []);
        Assert.Equal(Strings.Matcap.ChangeKindTip,
            MatcapPanelModel.ApplyDisabledReason(true, [Part("/a_mcap3fa21c1.mtrl")], MatcapChoice.OfLibrary(lib, "Pearl"), 3, record));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-4")]
    public void Room_zero_blocks_new_not_change()
    {
        var img = MatcapChoice.OfImage(new MatcapImage("b.png", "b.png", false));
        var record = new MatcapRecord();
        record.Materials.Add(Entry("/a_mcap3fa21c1.mtrl", ImageSrc));
        Assert.Null(MatcapPanelModel.ApplyDisabledReason(true, [Part("/a_mcap3fa21c1.mtrl")], img, 0, record));
        Assert.NotNull(MatcapPanelModel.ApplyDisabledReason(true, [Part("/c.mtrl")], img, 0, record));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-5")]
    public void Unrecorded_matcap_part_NoEntry()
    {
        var img = MatcapChoice.OfImage(new MatcapImage("b.png", "b.png", false));
        Assert.Equal(Strings.Matcap.NoEntry,
            MatcapPanelModel.ApplyDisabledReason(true, [Part("/a_mcap3fa21c1.mtrl")], img, 3, new MatcapRecord()));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-27")]
    public void Horn_matcap_tickable_on_face()
    {
        var horn = Part("/mt_c1401f0001_hrn_mcap3fa21c1.mtrl", mask: 1u);
        Assert.True(MatcapPlan.Tickable(horn, MatcapPlan.Kind.Face, [MatcapPlan.HornAttribute]));
        Assert.False(MatcapPlan.Tickable(Part("/mt_c1401f0001_hrn_mcap3fa21c1.mtrl"), MatcapPlan.Kind.Face, [MatcapPlan.HornAttribute]));
    }

    // ── what the pane says the change reaches ──────────────────────────────

    [Fact]
    [Trait("Plan", "AC-matcap-preview-2")]
    [Trait("Plan", "EDGE-matcap-preview-31")]
    public void ChangesLines_name_stems_unticked_parts_and_other_files()
    {
        var record = new MatcapRecord();
        var e = Entry("/mt_top_a_mcap3fa21c1.mtrl", ImageSrc);
        e.Meshes = [new MatcapRecord.MeshRef { Model = "model/top.mdl", Mesh = 2 }, new MatcapRecord.MeshRef { Model = "model/top_b.mdl", Mesh = 2 }];
        record.Materials.Add(e);
        var t1 = Part("/mt_top_a_mcap3fa21c1.mtrl", "3.1");
        var t2 = Part("/mt_top_a_mcap3fa21c1.mtrl", "3.2");
        var other = Part("/mt_top_b.mtrl", "2.1");

        var lines = MatcapPanelModel.ChangesLines([t1], [t1, t2, other], record, "model/top.mdl");
        Assert.Equal(3, lines.Count);
        Assert.Contains("mt_top_a", lines[0]);
        Assert.Equal(string.Format(Strings.Matcap.ChangesUntickedFmt, 1), lines[1]);
        Assert.Equal(string.Format(Strings.Matcap.ChangesFilesFmt, "model/top_b.mdl"), lines[2]);

        // All ticked, no other recorded file: only the first line, which still says refits are included.
        e.Meshes = [new MatcapRecord.MeshRef { Model = "model/top.mdl", Mesh = 2 }];
        var only = MatcapPanelModel.ChangesLines([t1, t2], [t1, t2, other], record, "model/top.mdl");
        Assert.Single(only);
        Assert.Equal(string.Format(Strings.Matcap.ChangesFmt, MatcapWriter.StemOf(e.Name)), only[0]);
    }

    // ── ChangeAll on a mod ─────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-matcap-preview-2")]
    public void ChangeAll_repoints_in_place_without_touching_models()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var before = MatcapRecord.Read(f.ModRoot).Materials.Single();
        var texBefore = File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, before.Source.Tex!.File));
        var model = f.Current();
        var names = MatcapPlan.ChangeNames(OnMatcap(f));

        var outcome = MatcapStudio.ChangeAll(f.ModRoot, names, MatcapChoice.OfImage(f.Image("pearl.tex", seed: 77)), f.Env());
        Assert.True(outcome.Ok, outcome.Message);

        var after = MatcapRecord.Read(f.ModRoot).Materials;
        Assert.Single(after);
        Assert.Equal("pearl.tex", after[0].Source.Image);
        Assert.NotEqual(texBefore, File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, after[0].Source.Tex!.File)));
        Assert.Equal(model, f.Current());
        Assert.Equal(SecondSkinWriter.MaterialNames(model), SecondSkinWriter.MaterialNames(f.Current()));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-1")]
    public void ChangeAll_two_materials()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2", "2.1");
        var names = MatcapPlan.ChangeNames(OnMatcap(f));
        Assert.Equal(2, names.Count);

        var outcome = MatcapStudio.ChangeAll(f.ModRoot, names, MatcapChoice.OfImage(f.Image("pearl.tex", seed: 77)), f.Env());
        Assert.True(outcome.Ok, outcome.Message);
        var mats = MatcapRecord.Read(f.ModRoot).Materials;
        Assert.All(mats, m => Assert.Equal("pearl.tex", m.Source.Image));
        // Baked once: both materials carry the same texture bytes.
        var texes = mats.Select(m => HairPortPlan.Sha8(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, m.Source.Tex!.File)))).Distinct().ToList();
        Assert.Single(texes);
        Assert.Equal(2, outcome.Files.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-1")]
    public void ChangeAll_partial_on_library_change_is_ok_with_warning()
    {
        using var f = new MatcapFixture();
        var a = f.Library("chara/common/texture/testlib_a.tex", "LibA");
        var b = f.Library("chara/common/texture/testlib_b.tex", "LibB");
        f.Apply(MatcapChoice.OfLibrary(a, "Pearl"), "1.2", "2.1");
        var names = MatcapPlan.ChangeNames(OnMatcap(f));
        Assert.Equal(2, names.Count);

        MatcapStudio.BeforeChange = n => { if (n == names[1]) throw new IOException("disk full"); };
        MatcapWriter.Outcome outcome;
        try { outcome = MatcapStudio.ChangeAll(f.ModRoot, names, MatcapChoice.OfLibrary(b, "Pearl"), f.Env()); }
        finally { MatcapStudio.BeforeChange = null; }

        Assert.True(outcome.Ok, outcome.Message);
        var mats = MatcapRecord.Read(f.ModRoot).Materials.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(b.Texture, mats[names[0]].Source.Library!.Texture);
        Assert.Equal(a.Texture, mats[names[1]].Source.Library!.Texture);
        Assert.Contains(string.Format(Strings.Matcap.ChangePartialFmt, "").TrimEnd('.', ' '), outcome.Message);
        Assert.Contains(MatcapWriter.StemOf(names[1]), outcome.Message);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-3")]
    public void ChangeAll_cross_kind_refused_writes_nothing()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var snapshot = f.Snapshot();
        var outcome = MatcapStudio.ChangeAll(f.ModRoot, MatcapPlan.ChangeNames(OnMatcap(f)), MatcapChoice.OfLibrary(f.Library(), "Pearl"), f.Env());
        Assert.Equal(MatcapWriter.CodeCrossKind, outcome.Code);
        Assert.Equal(snapshot, f.Snapshot());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-7")]
    public void Same_library_other_option_writes_no_file()
    {
        using var f = new MatcapFixture();
        var lib = f.Library();
        f.Apply(MatcapChoice.OfLibrary(lib, "Pearl"), "1.2");
        var name = MatcapPlan.ChangeNames(OnMatcap(f)).Single();
        var mtrl = File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, MatcapRecord.Read(f.ModRoot).Materials.Single().File));
        var outcome = MatcapStudio.ChangeAll(f.ModRoot, [name], MatcapChoice.OfLibrary(lib, "Gem Red"), f.Env());
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Empty(outcome.Files);
        Assert.Equal(mtrl, File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, MatcapRecord.Read(f.ModRoot).Materials.Single().File)));
        Assert.Equal("Gem Red", MatcapRecord.Read(f.ModRoot).Materials.Single().Source.Library!.Option);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-3")]
    [Trait("Plan", "EDGE-matcap-preview-28")]
    public void ChangeAll_hand_edited_mtrl_and_tex_refuse_before_writing()
    {
        // Image: the second material's texture edited by hand; the first is not changed either.
        using (var f = new MatcapFixture())
        {
            f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2", "2.1");
            var names = MatcapPlan.ChangeNames(OnMatcap(f));
            var second = MatcapRecord.Read(f.ModRoot).Materials.Single(m => string.Equals(m.Name, names[1], StringComparison.OrdinalIgnoreCase));
            File.AppendAllText(MatcapWriter.Full(f.ModRoot, second.Source.Tex!.File), "edited");
            var snapshot = f.Snapshot();
            var outcome = MatcapStudio.ChangeAll(f.ModRoot, names, MatcapChoice.OfImage(f.Image("pearl.tex", seed: 7)), f.Env());
            Assert.Equal(MatcapWriter.CodeEdited, outcome.Code);
            Assert.Equal(snapshot, f.Snapshot());
        }
        // Library repoint: the material edited by hand.
        using (var f = new MatcapFixture())
        {
            var a = f.Library("chara/common/texture/testlib_a.tex", "LibA");
            var b = f.Library("chara/common/texture/testlib_b.tex", "LibB");
            f.Apply(MatcapChoice.OfLibrary(a, "Pearl"), "1.2");
            var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
            File.AppendAllText(MatcapWriter.Full(f.ModRoot, mat.File), "edited");
            var snapshot = f.Snapshot();
            var outcome = MatcapStudio.ChangeAll(f.ModRoot, [mat.Name], MatcapChoice.OfLibrary(b, "Pearl"), f.Env());
            Assert.Equal(MatcapWriter.CodeEdited, outcome.Code);
            Assert.Equal(snapshot, f.Snapshot());
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-28")]
    public void ChangeAll_missing_tex_is_not_an_edit()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
        File.Delete(MatcapWriter.Full(f.ModRoot, mat.Source.Tex!.File));
        var outcome = MatcapStudio.ChangeAll(f.ModRoot, [mat.Name], MatcapChoice.OfImage(f.Image("pearl.tex", seed: 7)), f.Env());
        Assert.True(outcome.Ok, outcome.Message);
        Assert.True(File.Exists(MatcapWriter.Full(f.ModRoot, mat.Source.Tex.File)));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-28")]
    public void ChangeAll_empty_recorded_sha8_is_not_an_edit()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var record = MatcapRecord.Read(f.ModRoot);
        var mat = record.Materials.Single();
        mat.Source.Tex!.Sha8 = "";
        MatcapRecord.Write(f.ModRoot, record);
        File.AppendAllText(MatcapWriter.Full(f.ModRoot, mat.Source.Tex.File), "edited");
        var outcome = MatcapStudio.ChangeAll(f.ModRoot, [mat.Name], MatcapChoice.OfImage(f.Image("pearl.tex", seed: 7)), f.Env());
        Assert.True(outcome.Ok, outcome.Message);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-28")]
    public void ChangeAll_unreadable_file_refuses()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
        var tex = MatcapWriter.Full(f.ModRoot, mat.Source.Tex!.File);
        var before = File.ReadAllBytes(tex);
        MatcapWriter.Outcome outcome;
        using (new FileStream(tex, FileMode.Open, FileAccess.Read, FileShare.None))
            outcome = MatcapStudio.ChangeAll(f.ModRoot, [mat.Name], MatcapChoice.OfImage(f.Image("pearl.tex", seed: 7)), f.Env());
        Assert.Equal(MatcapWriter.CodeEdited, outcome.Code);
        Assert.Equal(before, File.ReadAllBytes(tex));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-5")]
    public void ChangeAll_missing_entry_refused()
    {
        using var f = new MatcapFixture();
        var outcome = MatcapStudio.ChangeAll(f.ModRoot, ["/nope_mcap3fa21c1.mtrl"], MatcapChoice.OfImage(f.Image("pearl.tex")), f.Env());
        Assert.Equal(MatcapWriter.CodeNothing, outcome.Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-2")]
    public void Prepare_still_refuses_matcap_parts_for_a_new_apply()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var p = MatcapStudio.Prepare(f.ModRoot, MatcapFixture.ModelRel, MatcapFixture.ModelPath, f.Current(),
                                     [OnMatcap(f)[0], Plain(f)[0]], MatcapChoice.OfImage(f.Image("b.tex", seed: 1)), f.Env());
        Assert.Equal("already-matcap", p.Code);
        Assert.Equal(Strings.Matcap.AlreadyMatcap, p.Message);
    }

    // ── the panel's wiring (source-read: the panel needs ImGui) ────────────

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
    [Trait("Plan", "AC-matcap-preview-1")]
    public void Existing_list_has_no_change_button()
    {
        var panel = File.ReadAllText(Path.Combine(Gui(), "MatcapPanel.cs"));
        var existing = Method(panel, "private void DrawExisting(Context ctx)");
        Assert.DoesNotContain("SmallButton", existing);
        Assert.DoesNotContain("s.Change", existing);
        Assert.DoesNotContain("ChangeKindTip", existing);
        Assert.Contains("EntryState(", existing);
        Assert.Contains("s.Forget", existing);
        Assert.Contains("s.Undo", existing);
        Assert.Null(typeof(MatcapPanel.Context).GetField("Change"));
        Assert.Null(typeof(MatcapPanel.Context).GetProperty("Change"));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-2")]
    public void Apply_routes_change_through_ChangeAll()
    {
        var w = File.ReadAllText(Path.Combine(Gui(), "PartsPanel.Matcaps.cs"));
        Assert.DoesNotContain("StartMatcapChange", w);
        var apply = Method(w, "private void StartMatcapApply(");
        Assert.Contains("MatcapPlan.KindOfApply(", apply);
        Assert.Contains("MatcapStudio.ChangeAll(", apply);
        Assert.Contains("IsChange = true", apply);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-4")]
    [Trait("Plan", "EDGE-matcap-preview-6")]
    public void Change_asks_before_switching_a_library_on()
    {
        var apply = Method(File.ReadAllText(Path.Combine(Gui(), "PartsPanel.Matcaps.cs")), "private void StartMatcapApply(");
        int ask = apply.IndexOf("if (choice.Library is { } lib && !confirmEnable)", StringComparison.Ordinal);
        int change = apply.IndexOf("MatcapStudio.ChangeAll(", StringComparison.Ordinal);
        Assert.True(ask >= 0 && change > ask, "the library-off question comes before the change");
        // The job carries the user's answer, never a literal true.
        Assert.DoesNotContain(", true, [], false, false, gamePath)", apply);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-1")]
    public void Finish_runs_select_and_reload_for_a_partial_change()
    {
        // A partial ChangeAll is an Ok outcome: Finish's success branch (library selection, AfterModChange) runs.
        var finish = Method(File.ReadAllText(Path.Combine(Gui(), "PartsPanel.Matcaps.cs")), "private void Finish(MatcapJobResult r)");
        int refuse = finish.IndexOf("if (!r.Outcome.Ok)", StringComparison.Ordinal);
        Assert.True(refuse >= 0 && finish.IndexOf("MatcapSelection.Select(", StringComparison.Ordinal) > refuse);
        Assert.True(finish.IndexOf("AfterModChange(", StringComparison.Ordinal) > refuse);
    }
}
