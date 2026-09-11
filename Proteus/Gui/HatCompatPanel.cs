using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Hats section: the two settings that govern hat compatibility, and what Proteus makes of the
/// hairstyle currently being worn.
/// <para/>
/// A view and nothing more. The examining, the geometry and the writing all belong to
/// <see cref="HatCompatWatcher"/>, which does them off this thread and keeps doing them while this window
/// is shut — a hairstyle change has to be noticed whether or not anyone is looking at the settings.
/// </summary>
internal sealed class HatCompatPanel(HatCompatWatcher watcher, Configuration config)
{
    public void Draw()
    {
        var s = Strings.HatCompat;

        var auto = config.AutoHatCompat;
        if (ImGui.Checkbox(s.AutoFit, ref auto))
        {
            config.AutoHatCompat = auto;
            config.Save();
            // Turning it ON should act on what is already being worn rather than waiting for the next
            // hairstyle change, which could be hours away. Turning it off changes nothing already written.
            if (auto) watcher.Refresh(mayApply: true, force: true);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.AutoFitTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.AutoFitTip);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawState();
        DrawUndoAll();
    }

    /// <summary>
    /// The escape hatch for everything the per-hairstyle undo cannot see.
    /// <para/>
    /// Drawn outside <see cref="DrawState"/> on purpose, because the case it exists for is the one where
    /// the hairstyle on screen is NOT the patched one: a hair mod ships one model per race, so fitting as
    /// a Miqo'te and then changing to a Midlander leaves a patch that the worn hairstyle names nothing of.
    /// Undo then reports success having restored the wrong file, or nothing at all.
    /// <para/>
    /// Hidden when the mod's only patched file is the one being worn, where the plain Undo above already
    /// says the same thing in fewer words.
    /// </summary>
    private void DrawUndoAll()
    {
        var view = watcher.Current;
        if (view.Target == null || view.Busy) return;
        var count = watcher.PatchedInMod;
        if (count == 0 || (count == 1 && view.Patched)) return;

        var s = Strings.HatCompat;
        ImGui.Spacing();
        if (ImGui.Button(string.Format(s.UndoAllFmt, count))) watcher.RevertAll();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.UndoAllTip);
    }

    private void DrawState()
    {
        var s = Strings.HatCompat;
        var view = watcher.Current;

        if (view.Target == null)
        {
            ImGui.TextWrapped(s.NoModdedHair);
            DrawMessage(view);
            return;
        }

        ImGui.TextWrapped(string.Format(s.WearingFmt,
            ContentSlot.Parse(view.Target.GamePath)?.SetTag ?? "?",
            System.IO.Path.GetFileName(view.Target.ModRoot)));

        if (view.Busy)
        {
            ImGui.TextDisabled(s.Working);
            return;
        }

        ImGui.Spacing();
        if (view.Patched)
        {
            ImGui.TextWrapped(s.AlreadyPatched);
            if (ImGui.Button(s.Undo)) watcher.Revert();
            DrawMessage(view);
            return;
        }

        if (view.Proposal is not { } proposal) { DrawMessage(view); return; }

        // No Apply button on this branch, deliberately: there is no hat line to fit against, so the button
        // would offer to do the one thing that cannot be done correctly.
        if (proposal.Unmeasurable) { DrawMessage(view); return; }

        if (proposal.AlreadyCompatible)
        {
            ImGui.TextWrapped(s.AuthorDidIt);
            return;
        }

        // Said BEFORE the press numbers, because it changes what the button means: this is not "your hair
        // has no hat support", it is "the support it came with is hiding the wrong hair".
        if (proposal.TookOver)
        {
            ImGui.TextWrapped(s.InheritedTag);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.InheritedTagTip);
            ImGui.Spacing();
        }

        var solve = proposal.Solve;
        ImGui.TextWrapped(string.Format(s.PressFmt, solve.Considered, solve.MedianPress * 100f,
                                        solve.MaxPressed * 100f));
        // Nothing to choose. Hiding whole ponytails was a setting here and is withdrawn: judging which
        // strands a hat cannot cover is a call the geometry cannot make reliably, and getting it wrong makes
        // hair disappear. The cut at the hat line is not affected — it is part of the fit, not an option.
        ImGui.Spacing();
        if (ImGui.Button(s.Apply)) watcher.Apply();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.ApplyTip);
        DrawMessage(view);
    }

    private static void DrawMessage(HatCompatWatcher.View view)
    {
        if (view.Message.Length == 0) return;
        ImGui.Spacing();
        if (view.Failed) ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), view.Message);
        else ImGui.TextWrapped(view.Message);
    }
}
