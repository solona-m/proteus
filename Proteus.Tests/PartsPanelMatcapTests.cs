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
/// The Studio's side of the Matcaps tool, read from the source as the panel's other wiring tests are (the panel needs
/// ImGui): where the tool sits, that it ticks parts like Pick parts, and that the vanilla first write only places bytes
/// prepared elsewhere.
/// </summary>
public class PartsPanelMatcapTests
{
    private static string Gui([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Gui");

    private static string Main() => File.ReadAllText(Path.Combine(Gui(), "PartsPanel.cs"));
    private static string Wiring() => File.ReadAllText(Path.Combine(Gui(), "PartsPanel.Matcaps.cs"));

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
    [Trait("Plan", "AC-matcaps-1")]
    public void Tool_list_has_Matcaps_after_Wind_without_brush_lock_hint()
    {
        var buttons = Method(Main(), "ToolButtons()");
        int wind = buttons.IndexOf("(Tool.Wind,", StringComparison.Ordinal);
        int matcap = buttons.IndexOf("(Tool.Matcap,", StringComparison.Ordinal);
        int retarget = buttons.IndexOf("(Tool.Retarget,", StringComparison.Ordinal);
        Assert.True(wind >= 0 && matcap > wind && retarget > matcap, "Matcaps sits after Wind and before Body size");
        Assert.Contains("FontAwesomeIcon.Gem", buttons);
        Assert.Contains("ShowsBrushLockHint(value) is false", Method(Main(), "private void DrawToolPicker()"));
        Assert.Contains("Tool.Matcap", Wiring()[Wiring().IndexOf("ShowsBrushLockHint", StringComparison.Ordinal)..]);
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-1")]
    public void Matcap_tool_picks_parts_like_toggle_parts()
    {
        var main = Main();
        Assert.Contains("TicksParts(Tool t) => t is Tool.Navigate or Tool.Matcap", main);
        Assert.Contains("bool pickParts = TicksParts(tool);", main);
        Assert.Contains("HidesLocked => tool is not (Tool.Navigate or Tool.Retarget or Tool.Matcap)", main);
        Assert.Contains("tool is Tool.Navigate or Tool.Retarget or Tool.Matcap ? PartViewport.ViewportMode.Navigate", main);
        Assert.Contains("else if (tool == Tool.Matcap) DrawMatcaps();", main);
        Assert.Contains("!CanTick(part)", Method(main, "private void DrawPartRows("));
        Assert.Contains("tool == Tool.Matcap ? MatcapPlan.Tickable(part, MatcapTargetKind(), parts?.AttributeNames ?? []) : part.Toggleable", Wiring());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-22")]
    public void Apply_requires_successful_flush()
    {
        var apply = Method(Wiring(), "private void StartMatcapApply(");
        int flush = apply.IndexOf("if (!FlushPending()) return;", StringComparison.Ordinal);
        int prepare = apply.IndexOf("MatcapStudio.Prepare", StringComparison.Ordinal);
        Assert.True(flush >= 0 && prepare > flush, "the brush's pending save is flushed before anything is prepared");
        Assert.Contains("if (!FlushPending()) return;", Method(Wiring(), "private void StartMatcapUndo("));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-44")]
    public void Vanilla_delegate_only_places_prepared_bytes()
    {
        var w = Wiring();
        // The image is baked on a worker before the copy...
        var prebake = Method(w, "private void ApplyMatcapToVanilla(");
        Assert.Contains("Task.Run", prebake);
        Assert.Contains("MatcapImages.Bake", prebake);
        // ...and the first write, on the framework thread, re-bakes nothing: Bake copies a .tex already at its size.
        Assert.Contains("CopyVanillaForEdit", Method(w, "private void FinishVanillaMatcap("));
        var bake = File.ReadAllText(Path.Combine(Gui(), "..", "Services", "Matcap", "MatcapImages.cs"));
        Assert.Contains("File.Copy(imagePath, tempTex, overwrite: true)", bake);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-43")]
    [Trait("Plan", "EDGE-matcaps-63")]
    public void Leaving_Matcaps_closes_vanilla_face_or_ear()
    {
        var sw = Method(Main(), "private void SwitchTool(Tool value)");
        Assert.Contains("CustomizationMaterialPath.Parse(open.GamePath) != null", sw);
        Assert.Contains("CloseVanillaEar();", sw);
        // Ears, the face (horns) and tails are offered by the live pick, the worn list and the opener only under Matcaps.
        Assert.Contains("tool == Tool.Matcap && VanillaPick.FromCharacterPart(gamePath)", Main());
        Assert.Contains("(tool == Tool.Matcap ? VanillaPick.FromCharacterPart(path) : null)", Main());
        Assert.Contains("(tool == Tool.Matcap ? VanillaPick.FromCharacterPart(gamePath) : null)", Main());
        // A managed face rewrite is never opened for editing: the overlay note instead.
        Assert.Contains("MatcapPanelModel.IsManagedFaceRewrite(", Main());
        Assert.Contains("Strings.Matcap.FaceOverlayOn", Main());
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-2")]
    public void Settings_has_the_matcap_folder_button()
    {
        var settings = File.ReadAllText(Path.Combine(Gui(), "SettingsTab.cs"));
        Assert.Contains("discovery.MatcapsLibraryPath()", settings);
        Assert.Contains("Strings.Matcap.SettingsLibrary", settings);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-61")]
    public void Face_upstream_forgotten_before_the_recomposite_on_apply_undo_forget_not_change()
    {
        var finish = Method(Wiring(), "private void Finish(MatcapJobResult r)");
        int forget = finish.IndexOf("compositor.ForgetFaceUpstream(r.GamePath)", StringComparison.Ordinal);
        int after = finish.IndexOf("AfterModChange(", StringComparison.Ordinal);
        Assert.True(forget >= 0 && after > forget, "the face upstream is forgotten before AfterModChange");
        Assert.Contains("Kind: MatcapPlan.Kind.Face } && !r.IsChange", finish);
        // The drawn-file line only after an Apply.
        Assert.Contains("r.IsApply && r.GamePath != null && DrawnElsewhere(", finish);
    }

    [Fact]
    public void A_finished_job_keeps_one_line_in_the_panel_and_says_the_rest_in_chat()
    {
        var finish = Method(Wiring(), "private void Finish(MatcapJobResult r)");
        Assert.Contains("MatcapPanelModel.Report(", finish);
        Assert.Contains("status = panel;", finish);
        Assert.Contains("foreach (var line in chat) Plugin.ChatGui.Print(", finish);
    }

    [Fact]
    public void An_apply_report_is_short_names_options_and_counts_the_rest()
    {
        var skipped = new[]
        {
            "body retarget/rosaline m/chara/equipment/e0041/model/c0201e0041_top.mdl",
            "body retarget/rosaline l/chara/equipment/e0041/model/c0201e0041_top.mdl",
            "body retarget/rosaline coconut/chara/equipment/e0041/model/c0201e0041_top.mdl",
        };
        var long1 = "Matcap applied: mt_c0201e0041_top_top_mcapa49a531 on 19 model file(s).";
        var (headline, status, chat) = MatcapPanelModel.Report(new MatcapPanelModel.JobReport(long1, true, 19, true, skipped, true, false, []));
        Assert.Equal(string.Format(Strings.Matcap.ChatAppliedFmt, 19), headline);
        Assert.Equal(headline, status);   // nothing to act on: the panel keeps the one line
        Assert.DoesNotContain("mcap", headline);
        Assert.Equal(headline, chat[0]);
        Assert.DoesNotContain(MatcapWriter.PortKeptText, chat);
        Assert.DoesNotContain(Strings.Matcap.Lod0Cut, chat);
        Assert.Contains(Strings.Matcap.ChatHoles, chat);
        Assert.DoesNotContain(Strings.Matcap.ChatEdges, chat);   // the holes line already says it
        Assert.Contains(string.Format(Strings.Matcap.ChatSkippedFmt, "rosaline m, rosaline l, rosaline coconut"), chat);
        Assert.DoesNotContain(chat, l => l.Contains("chara/", StringComparison.Ordinal));

        var many = Enumerable.Range(0, 18).Select(i => $"size/jacket/s{i}/chara/accessory/a0111/model/c0201a0111_wrs.mdl").ToList();
        var (head2, status2, chat2) = MatcapPanelModel.Report(new MatcapPanelModel.JobReport(long1, true, 1, false, many, true, true, ["", "drawn"]));
        var skipLine = Assert.Single(chat2, l => l.Contains("s0, s1, s2, s3, s4, s5", StringComparison.Ordinal));
        Assert.Contains(string.Format(Strings.Matcap.ChatMoreFmt, 18 - MatcapPanelModel.SkippedNamed), skipLine);
        Assert.DoesNotContain("s6,", skipLine);
        Assert.Contains(Strings.Matcap.ChatEdges, chat2);
        Assert.Contains(Strings.Matcap.Lod0Cut, chat2);
        Assert.Equal("drawn", chat2[^1]);
        Assert.DoesNotContain("", chat2);                    // empty extras are dropped
        Assert.Equal(head2 + "\n" + "drawn", status2);       // a line to act on stays in the panel too

        // The "more" boundary: six names in full, a seventh is counted.
        static List<string> Files(int n) => [.. Enumerable.Range(0, n).Select(i => $"g/o{i}/chara/x.mdl")];
        string Skip(int n) => MatcapPanelModel.Report(new MatcapPanelModel.JobReport("m", true, 1, false, Files(n), false, false, [])).Chat[1];
        Assert.Equal(string.Format(Strings.Matcap.ChatSkippedFmt, "o0, o1, o2, o3, o4, o5"), Skip(6));
        Assert.Equal(string.Format(Strings.Matcap.ChatSkippedFmt, "o0, o1, o2, o3, o4, o5 " + string.Format(Strings.Matcap.ChatMoreFmt, 1)), Skip(7));
        // Two files of one option are named once.
        var same = MatcapPanelModel.Report(new MatcapPanelModel.JobReport("m", true, 1, false, ["a/m/chara/x.mdl", "a/M/chara/y.mdl"], false, false, []));
        Assert.Equal(string.Format(Strings.Matcap.ChatSkippedFmt, "m"), same.Chat[1]);

        // A hair port: the kept sentence, from the flag (never by searching the message), on an Apply only.
        var port = MatcapPanelModel.Report(new MatcapPanelModel.JobReport("m", true, 1, false, [], false, false, [], PortKept: true));
        Assert.Contains(MatcapWriter.PortKeptText, port.Chat);
        var portChange = MatcapPanelModel.Report(new MatcapPanelModel.JobReport("changed", false, 0, false, [], false, false, [], PortKept: true));
        Assert.DoesNotContain(MatcapWriter.PortKeptText, portChange.Chat);

        // Change, Undo and Forget keep their own message.
        var (undoHead, _, undoChat) = MatcapPanelModel.Report(new MatcapPanelModel.JobReport("Undone.", false, 0, false, [], false, false, []));
        Assert.Equal("Undone.", undoHead);
        Assert.Single(undoChat);
    }

    [Theory]
    [InlineData("body retarget/rosaline m/chara/equipment/e0041/model/c0201e0041_top.mdl", "rosaline m")]
    [InlineData(@"size\jacket\xs\chara\accessory\a0111\model\c0201a0111_wrs.mdl", "xs")]
    [InlineData("models/top.mdl", "top")]
    [InlineData("chara/equipment/e0041/model/c0201e0041_top.mdl", "c0201e0041_top")]
    public void Option_name_is_the_folder_before_chara_else_the_file(string file, string expected) =>
        Assert.Equal(expected, MatcapPanelModel.OptionName(file));

    [Fact]
    [Trait("Plan", "AC-matcaps-5")]
    public void Library_off_is_asked_before_anything_is_written()
    {
        var apply = Method(Wiring(), "private void StartMatcapApply(");
        int ask = apply.IndexOf("if (choice.Library is { } lib && !confirmEnable)", StringComparison.Ordinal);
        int write = apply.IndexOf("MatcapWriter.Apply(", StringComparison.Ordinal);
        int vanilla = apply.IndexOf("ApplyMatcapToVanilla(", StringComparison.Ordinal);
        Assert.True(ask >= 0 && ask < write && ask < vanilla);
    }
}

public class VanillaPickEarTests
{
    [Fact]
    [Trait("Plan", "EDGE-matcaps-43")]
    public void FromEar_and_FromFace_labels_not_refittable()
    {
        var ear = VanillaPick.FromEar("chara/human/c1801/obj/zear/z0001/model/c1801z0001_zer.mdl")!.Value;
        Assert.Equal("Ears z0001 (c1801)", ear.ItemName);
        Assert.False(ear.Refittable);
        var face = VanillaPick.FromFace("chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl")!.Value;
        Assert.Equal("Horns (face f0001, c1401)", face.ItemName);
        Assert.False(face.Refittable);
        Assert.Null(VanillaPick.FromFace("chara/human/c1801/obj/zear/z0001/model/c1801z0001_zer.mdl"));
        Assert.Null(VanillaPick.FromFace("chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl"));   // no horns
        Assert.Null(VanillaPick.FromEar("chara/equipment/e6255/model/c0201e6255_top.mdl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-43")]
    public void From_still_null_for_zear()
    {
        Assert.Null(VanillaPick.From("chara/human/c1401/obj/zear/z0003/model/c1401z0003_zer.mdl", (_, _) => null));
    }
}

public class MatcapPanelModelTests
{
    private static ModelPart Part(string material, string label = "1.1") => new()
    {
        Mesh = 0, Submesh = 0, Island = -1, Label = label, Material = material, Triangles = [0, 1, 2], Ordinals = [0],
        AttributeMask = 0, Min = default, Max = default, Toggleable = true,
    };

    [Fact]
    [Trait("Plan", "AC-matcaps-1")]
    [Trait("Plan", "AC-matcap-preview-2")]
    public void Tickable_excludes_skin_only()
    {
        Assert.True(MatcapPlan.Tickable(Part("/mt_c0201e6255_top_a.mtrl")));
        Assert.False(MatcapPlan.Tickable(Part("/mt_c0201b0001_a.mtrl")));
        // A part with a matcap is ticked to change it.
        Assert.True(MatcapPlan.Tickable(Part("/mt_c0201e6255_top_a_mcap3fa21c1.mtrl")));
        Assert.Equal(Strings.Matcap.SkinTip, MatcapPanelModel.UntickableTip(Part("/mt_c0201b0001_bibo.mtrl")));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-8")]
    public void Apply_disabled_reasons()
    {
        var s = Strings.Matcap;
        var choice = MatcapChoice.OfImage(new MatcapImage("a.png", "a.png", false));
        IReadOnlyList<ModelPart> two = [Part("/a.mtrl"), Part("/b.mtrl", "1.2")];
        var record = new MatcapRecord();
        Assert.Equal(s.NoPenumbra, MatcapPanelModel.ApplyDisabledReason(false, two, choice, 3, record));
        Assert.Equal(s.NeedParts, MatcapPanelModel.ApplyDisabledReason(true, [], choice, 3, record));
        Assert.Equal(s.NeedMatcap, MatcapPanelModel.ApplyDisabledReason(true, two, null, 3, record));
        Assert.Contains("10", MatcapPanelModel.ApplyDisabledReason(true, two, choice, 0, record));
        Assert.Null(MatcapPanelModel.ApplyDisabledReason(true, two, choice, 3, record));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-8")]
    public void Material_room_shown()
    {
        Assert.Contains("3", string.Format(Strings.Matcap.RoomFmt, 3));
        Assert.Equal(SecondSkinWriter.MaxMaterials - 2, ModelMaterialWriter.MaterialRoom(
            SyntheticModel.Build([], new SyntheticModel.Mesh("/a.mtrl", new SyntheticModel.Sub(0)),
                                     new SyntheticModel.Mesh("/b.mtrl", new SyntheticModel.Sub(0, OffsetX: 5f)))));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-2")]
    public void Empty_images_show_folder_and_open_button()
    {
        Assert.Contains(@"C:\mods\Proteus\Matcaps", MatcapPanelModel.NoImages(@"C:\mods\Proteus\Matcaps"));
        Assert.Contains("###matcapOpenFolder", Strings.Matcap.OpenFolder);
    }

    private static MatcapRecord RecordWith(params (string Name, string Texture)[] libs)
    {
        var r = new MatcapRecord();
        foreach (var (name, tex) in libs)
            r.Materials.Add(new MatcapRecord.MaterialEntry
            {
                Name = name,
                Source = new MatcapRecord.SourceRef { Kind = "library", Library = new MatcapRecord.LibraryRef { Texture = tex, Name = "L", Option = "Pearl" } },
            });
        return r;
    }

    private static readonly MatcapLibrary Lib = new("Lib", "Test Library", "https://x.test", "Texture Slot A",
        "chara/common/texture/a.tex", "m.mtrl", [new MatcapOption("Pearl", "p.tex")], []);

    [Fact]
    [Trait("Plan", "AC-matcaps-5")]
    public void Share_note_names_library_and_website()
    {
        var note = MatcapPanelModel.ShareNote("Test Library", "https://x.test");
        Assert.Contains("Test Library", note);
        Assert.Contains("https://x.test", note);
        var st = new MatcapSelection.Status(true, "Mine", true, false, null, "Pearl", false);
        Assert.Contains(note, MatcapPanelModel.LibraryWarnings(Lib, st, new MatcapRecord(), ["Lib"]));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-5")]
    public void Shared_slot_warning_counts_this_mods_users()
    {
        var record = RecordWith(("/a_mcapx1.mtrl", "chara/common/texture/a.tex"), ("/b_mcapx2.mtrl", "chara/common/texture/b.tex"));
        Assert.Equal(["/a_mcapx1.mtrl"], MatcapPanelModel.SlotUsers(record, "chara/common/texture/a.tex"));
        var st = new MatcapSelection.Status(true, "Default", true, true, "Glamourer", "Pearl", true);
        var warnings = MatcapPanelModel.LibraryWarnings(Lib, st, record, ["Lib", "Other"]);
        Assert.Contains(warnings, w => w == string.Format(Strings.Matcap.SharedSlotFmt, 1));
        Assert.Contains(warnings, w => w == string.Format(Strings.Matcap.InheritedFmt, "Default"));
        Assert.Contains(warnings, w => w == string.Format(Strings.Matcap.BaseCollectionFmt, "Default"));
        Assert.Contains(warnings, w => w == string.Format(Strings.Matcap.HeldFmt, "Glamourer"));
        Assert.Contains(warnings, w => w == string.Format(Strings.Matcap.DuplicatesFmt, "Other"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-40")]
    public void Entry_states_library_missing_or_off()
    {
        var entry = RecordWith(("/a_mcapx1.mtrl", "chara/common/texture/a.tex")).Materials[0];
        Assert.Equal(string.Format(Strings.Matcap.LibraryMissingFmt, "L"), MatcapPanelModel.EntryState(entry, MatcapWriter.State.Ok, false, false));
        Assert.Equal(string.Format(Strings.Matcap.LibraryOffFmt, "L"), MatcapPanelModel.EntryState(entry, MatcapWriter.State.Ok, true, false));
        Assert.Null(MatcapPanelModel.EntryState(entry, MatcapWriter.State.Ok, true, true));
        Assert.Equal(Strings.Matcap.Superseded, MatcapPanelModel.EntryState(entry, MatcapWriter.State.Superseded, true, true));
        Assert.Equal(Strings.Matcap.Edited, MatcapPanelModel.EntryState(entry, MatcapWriter.State.Edited, true, true));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-48")]
    public void Alpha_tested_part_warned()
    {
        using var f = new MatcapFixture(extra: new Dictionary<string, (string, byte[])>
        {
            [MatcapFixture.MatPath] = ("mat/top_a.mtrl", SyntheticMatcapMod.Gear(MatcapFixture.Normal, alphaThreshold: 0.4f)),
        });
        var p = f.Prepare(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        Assert.True(p.AlphaTested);
        Assert.Equal(0.4f, p.Request!.AlphaThreshold);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-30")]
    public void No_penumbra_no_write()
    {
        Assert.Equal(Strings.Matcap.NoPenumbra, MatcapPanelModel.ApplyDisabledReason(false, [Part("/a.mtrl")],
            MatcapChoice.OfImage(new MatcapImage("a.png", "a.png", false)), 5, new MatcapRecord()));
    }
}
