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
    public void Leaving_Matcaps_closes_vanilla_face_or_ear()
    {
        var sw = Method(Main(), "private void SwitchTool(Tool value)");
        Assert.Contains("CustomizationMaterialPath.Parse(open.GamePath) != null", sw);
        Assert.Contains("CloseVanillaEar();", sw);
        // Ears and the face (horns) are offered by the live pick and the worn list only under Matcaps.
        Assert.Contains("tool == Tool.Matcap && (VanillaPick.FromEar(gamePath) ?? VanillaPick.FromFace(gamePath))", Main());
        Assert.Contains("(tool == Tool.Matcap ? VanillaPick.FromEar(path) ?? VanillaPick.FromFace(path) : null)", Main());
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
    public void Tickable_excludes_skin_and_matcap_parts()
    {
        Assert.True(MatcapPlan.Tickable(Part("/mt_c0201e6255_top_a.mtrl")));
        Assert.False(MatcapPlan.Tickable(Part("/mt_c0201b0001_a.mtrl")));
        Assert.False(MatcapPlan.Tickable(Part("/mt_c0201e6255_top_a_mcap3fa21c1.mtrl")));
        Assert.Equal(Strings.Matcap.SkinTip, MatcapPanelModel.UntickableTip(Part("/mt_c0201b0001_bibo.mtrl")));
        Assert.Equal(Strings.Matcap.AlreadyTip, MatcapPanelModel.UntickableTip(Part("/mt_x_mcap3fa21c1.mtrl")));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-8")]
    public void Apply_disabled_reasons()
    {
        var s = Strings.Matcap;
        Assert.Equal(s.NoPenumbra, MatcapPanelModel.ApplyDisabledReason(false, 2, true, 3));
        Assert.Equal(s.NeedParts, MatcapPanelModel.ApplyDisabledReason(true, 0, true, 3));
        Assert.Equal(s.NeedMatcap, MatcapPanelModel.ApplyDisabledReason(true, 2, false, 3));
        Assert.Contains("10", MatcapPanelModel.ApplyDisabledReason(true, 2, true, 0));
        Assert.Null(MatcapPanelModel.ApplyDisabledReason(true, 2, true, 3));
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
        Assert.Equal(Strings.Matcap.NoPenumbra, MatcapPanelModel.ApplyDisabledReason(false, 1, true, 5));
    }
}
