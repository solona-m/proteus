using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Studio's side of copying the game's own gear on its first edit. The panel needs ImGui, so how it is wired is read
/// from its source — opening never copies, only the two saves do, and the copy keeps the edit open — and the pure
/// helpers it decides with are tested directly.
/// </summary>
public class PartsPanelVanillaCopyTests
{
    private static string Read(string file, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Gui", file));

    private static string Panel => Read("PartsPanel.cs");
    private static string Copy => Read("PartsPanel.VanillaCopy.cs");

    /// <summary>The body of the first method whose signature contains <paramref name="signature"/>, braces matched.</summary>
    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, System.StringComparison.Ordinal);
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

    private static int Count(string source, string text) => Regex.Matches(source, Regex.Escape(text)).Count;

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-1")]
    [Trait("Plan", "AC-vanilla-copy-on-edit-2")]
    [Trait("Plan", "AC-vanilla-copy-on-edit-6")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-18")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-19")]
    public void Opening_game_gear_never_makes_a_mod()
    {
        var open = Method(Panel, "private void OpenGameGear(");
        Assert.Contains("OpenVanilla(item.GamePath)", open);
        Assert.DoesNotContain("Ensure", open);
        Assert.DoesNotContain("EditVanilla", Panel);

        // A click on the character and the list's game rows both come here, the list only for another piece.
        Assert.Contains("OpenGameGear(item)", Method(Panel, "private void OnLivePicked("));
        Assert.Contains("if (pickedGame && vanilla?.GamePath != item.GamePath) OpenGameGear(item);", Panel);

        var openVanilla = Method(Panel, "private void OpenVanilla(");
        foreach (var write in new[] { "Ensure", "AddGameModel", "CopyVanillaForEdit", "AtomicWrite", "WriteAll" })
            Assert.DoesNotContain(write, openVanilla);

        // The only mods the panel makes: Body size's refit mod, and the copy made by a save.
        Assert.Equal(1, Count(Panel, "refitMods.Ensure("));
        Assert.Contains("refitMods.Ensure(subject, bodyName", Method(Panel, "private (string Root, string Dir)? RefitModFor("));
        Assert.Equal(1, Count(Copy, "refitMods.Ensure("));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-6")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-18")]
    public void A_click_on_the_piece_just_copied_does_not_reopen_it_as_the_game_s()
    {
        var open = Method(Panel, "private void OpenGameGear(");
        Assert.True(open.IndexOf("copyNotDrawnYet", System.StringComparison.Ordinal)
                    < open.IndexOf("OpenVanilla(", System.StringComparison.Ordinal));
        // Forgotten when another model opens, after the flush that may itself have made a copy.
        var reset = Method(Panel, "private void ResetForNewModel(");
        Assert.True(reset.IndexOf("FlushPending()", System.StringComparison.Ordinal)
                    < reset.IndexOf("copyNotDrawnYet = null", System.StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-3")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-23")]
    public void The_tool_picker_does_not_copy()
    {
        var picker = Method(Panel, "private void DrawToolPicker(");
        Assert.DoesNotContain("vanilla", picker);
        Assert.DoesNotContain("CopyVanillaForEdit", picker);
        Assert.DoesNotContain("VanillaEditToolTip", picker);
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-3")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-17")]
    public void Only_saves_copy()
    {
        Assert.DoesNotContain("CopyVanillaForEdit(", Panel);
        Assert.Equal(1, Count(Method(Copy, "private void SaveVanillaBrush("), "CopyVanillaForEdit("));
        Assert.Equal(1, Count(Method(Copy, "private void CommitVanilla("), "CopyVanillaForEdit("));
        // Declared once, called from those two only.
        Assert.Equal(3, Count(Copy, "CopyVanillaForEdit("));

        Assert.Contains("SaveVanillaBrush(piece, refreshGame)", Method(Panel, "private void SaveBrush("));
        Assert.Contains("CommitVanilla(piece, plans)", Method(Panel, "private void Commit()"));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-5")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-3")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-4")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-20")]
    public void Saves_route_through_the_copy()
    {
        // Every save path reaches SaveBrush, whose first branch is the game's own gear.
        Assert.Contains("SaveBrush();", Method(Panel, "private void TickAutosave("));
        Assert.Contains("SaveBrush(refreshGame)", Method(Panel, "public bool FlushPending("));
        var save = Method(Panel, "private void SaveBrush(");
        Assert.True(save.IndexOf("vanilla is { } piece", System.StringComparison.Ordinal)
                    < save.IndexOf("ModRoot()", System.StringComparison.Ordinal));

        // Opening anything else flushes first, while the piece is still the open one.
        var reset = Method(Panel, "private void ResetForNewModel(");
        Assert.Contains("FlushPending();", reset);
        var selectMod = Method(Panel, "private void SelectMod(");
        Assert.True(selectMod.IndexOf("FlushPending()", System.StringComparison.Ordinal)
                    < selectMod.IndexOf("vanilla = null", System.StringComparison.Ordinal));

        // Switches: a pending brush edit is saved (and so copied) first; then the switches go into that same copy.
        var commit = Method(Panel, "private void Commit()");
        Assert.True(commit.IndexOf("FlushPending()", System.StringComparison.Ordinal)
                    < commit.IndexOf("vanilla is { } piece", System.StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-20")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-13")]
    public void Switches_wait_while_the_flush_has_just_made_the_copy()
    {
        // The flush may have made a copy Penumbra is still registering: the switches are not written into it yet.
        var commit = Method(Panel, "private void Commit()");
        int flush = commit.IndexOf("FlushPending()", System.StringComparison.Ordinal);
        int wait = commit.IndexOf("copyAwaitingEnable != null", System.StringComparison.Ordinal);
        int write = commit.IndexOf("MeshToggleService.Write(", System.StringComparison.Ordinal);
        Assert.True(flush >= 0 && flush < wait && wait < write);
        Assert.Contains("writeError = ps.VanillaCopyRegistering", commit);
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-10")]
    public void A_brush_copy_says_the_mod_it_went_into()
    {
        var save = Method(Copy, "private void SaveVanillaBrush(");
        int keep = save.IndexOf("string copied = status", System.StringComparison.Ordinal);
        int after = save.IndexOf("AfterBrushWritten(", System.StringComparison.Ordinal);
        int join = save.IndexOf("status = copied + ", System.StringComparison.Ordinal);
        Assert.True(keep >= 0 && keep < after && after < join);
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-7")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-1")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-2")]
    public void Nothing_to_write_makes_no_mod()
    {
        var save = Method(Copy, "private void SaveVanillaBrush(");
        Assert.True(save.IndexOf("!volume.Dirty", System.StringComparison.Ordinal)
                    < save.IndexOf("CopyVanillaForEdit(", System.StringComparison.Ordinal));
        // An edit that cannot be written is found before any mod is made.
        Assert.True(save.IndexOf("Inflate(", System.StringComparison.Ordinal)
                    < save.IndexOf("CopyVanillaForEdit(", System.StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-5")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-14")]
    public void The_copy_keeps_the_open_edit()
    {
        var adopt = Method(Copy, "private bool AdoptCopy(");
        foreach (var reopen in new[] { "ResetForNewModel", "AdoptModel", "SelectModel", "viewport.Clear" })
            Assert.DoesNotContain(reopen, adopt);
        foreach (var set in new[] { "lockedParts", "heldParts", "keepShapeParts" })
            Assert.Contains($"MoveKeyed({set}, oldKey, newKey)", adopt);
        Assert.Contains("viewport.Rekey(oldKey, newKey)", adopt);
        // The piece's own file, never another option's at its game path.
        Assert.Contains("SameFile(m.File, rel) && SameFile(m.GamePath, piece.GamePath)", adopt);
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-14")]
    public void MoveKeyed_moves_overwrites_and_leaves_alone()
    {
        var map = new Dictionary<string, HashSet<string>> { ["a"] = ["1"], ["b"] = ["2"] };
        PartsPanel.MoveKeyed(map, "a", "b");
        Assert.False(map.ContainsKey("a"));
        Assert.Equal(["1"], map["b"]);

        PartsPanel.MoveKeyed(map, "missing", "b");
        Assert.Equal(["1"], map["b"]);
        PartsPanel.MoveKeyed(map, "b", "b");
        Assert.Equal(["1"], map["b"]);
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-4")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-15")]
    public void The_live_brush_arms_on_a_vanilla_piece_by_its_game_path()
    {
        const string coat = "chara/equipment/e0543/model/c0201e0543_top.mdl";
        Assert.Equal(coat, PartsPanel.ArmedModelFile(coat, null, ""));
        Assert.Equal(coat, PartsPanel.ArmedModelFile(coat, @"E:\mods\Coat", coat));   // copied, not drawn from it yet
        Assert.Equal(Path.Combine(@"E:\mods\Coat", "a", "b.mdl"), PartsPanel.ArmedModelFile(null, @"E:\mods\Coat", "a/b.mdl"));
        Assert.Null(PartsPanel.ArmedModelFile(null, null, "a/b.mdl"));

        Assert.Contains("ArmedModelFile(vanilla?.GamePath ?? copyNotDrawnYet, ModRoot(), models[modelIndex].File)", Panel);
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-16")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-13")]
    public void A_just_made_copy_is_redrawn_once_when_switched_on()
    {
        var after = Method(Panel, "private void AfterBrushWritten(");
        Assert.True(after.IndexOf("if (!reload)", System.StringComparison.Ordinal)
                    < after.IndexOf("ReloadModDirectory", System.StringComparison.Ordinal));
        Assert.Contains("RedrawIfCopyEnabled();", Method(Panel, "private void DrawCore("));
        var redraw = Method(Copy, "private void RedrawIfCopyEnabled(");
        Assert.Contains("refitMods.IsPending", redraw);
        Assert.Contains("preview.Active", redraw);

        // Switches into a just-made copy: read back from what was written, nothing reloaded before it is switched on.
        var change = Method(Panel, "private void AfterModChange(");
        Assert.Contains("SelectModel(modelIndex, copied?.Model)", change);
        Assert.True(change.IndexOf("if (copied != null) return;", System.StringComparison.Ordinal)
                    < change.IndexOf("ReloadModDirectory", System.StringComparison.Ordinal));
        Assert.Contains("c.Made ? (model, record) : null", Method(Copy, "private void CommitVanilla("));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-5")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-6")]
    public void Leaving_flushes_a_vanilla_edit_and_teardown_makes_no_mod()
    {
        Assert.Contains("FlushPending(refreshGame)", Method(Panel, "public void Leave("));
        var save = Method(Copy, "private void SaveVanillaBrush(");
        Assert.True(save.IndexOf("if (!refreshGame)", System.StringComparison.Ordinal)
                    < save.IndexOf("CopyVanillaForEdit(", System.StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-7")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-11")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-12")]
    public void A_refused_copy_is_retried_or_kept_not_dropped()
    {
        Assert.Contains("brushChangedAt = Environment.TickCount64", Method(Copy, "private void SaveVanillaBrush("));
        var commit = Method(Copy, "private void CommitVanilla(");
        Assert.True(commit.IndexOf("writeError = status", System.StringComparison.Ordinal)
                    < commit.LastIndexOf("pending.Clear()", System.StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-9")]
    [Trait("Plan", "AC-vanilla-copy-on-edit-10")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-22")]
    public void Vanilla_notes_follow_the_tool()
    {
        var ps = Strings.Parts;
        Assert.Equal(new List<(string, bool)> { (ps.VanillaSaveNote, false), (ps.VanillaCaveats, true) },
                     PartsPanel.VanillaNotes(bodySize: true, refittable: true, "Coat"));
        Assert.Equal(new List<(string, bool)> { (ps.VanillaNotRefittable, true) }, PartsPanel.VanillaNotes(bodySize: true, refittable: false, "Ring"));

        var other = Assert.Single(PartsPanel.VanillaNotes(bodySize: false, refittable: true, "Coat"));
        Assert.Contains(VanillaEditCopy.ModName("Coat"), other.Text);
        Assert.False(other.Warn);
        Assert.Contains("VanillaNotes(tool == Tool.Retarget, item.Refittable, item.ItemName)",
                        Method(Panel, "private void DrawVanillaHeader("));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-9")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-22")]
    public void Body_size_still_makes_its_own_mod_and_refuses_what_it_cannot_refit()
    {
        Assert.Contains("refitMods.Ensure(subject, bodyName", Method(Panel, "private (string Root, string Dir)? RefitModFor("));
        var retarget = Method(Panel, "private void DrawRetarget(");
        Assert.Contains("vanilla is { Refittable: false }", retarget);
        Assert.Contains("ps.VanillaNotRefittable", retarget);
    }
}
