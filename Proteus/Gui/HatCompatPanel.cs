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

        using (ImRaii.PushIndent())
        {
            var hide = config.HatCompatHidePonytails;
            if (ImGui.Checkbox(s.HidePonytails, ref hide))
            {
                config.HatCompatHidePonytails = hide;
                config.Save();
                // A patch is a one-time write, so a hairstyle that already has one has to be done again
                // for this to mean anything. Without it the box appeared to do nothing at all.
                if (watcher.Current.Patched) watcher.Reapply();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.HidePonytailsTip);
            ImGui.SameLine();
            ImGuiComponents.HelpMarker(s.HidePonytailsTip);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawState();
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
        if (proposal.AlreadyCompatible)
        {
            ImGui.TextWrapped(s.AuthorDidIt);
            return;
        }

        var solve = proposal.Solve;
        ImGui.TextWrapped(string.Format(s.PressFmt, solve.Considered, solve.MedianPress * 100f,
                                        solve.MaxPressed * 100f));
        if (proposal.Unaddressable > 0)
            ImGui.TextWrapped(string.Format(s.TooWeldedFmt, proposal.Unaddressable));

        // What WILL happen, not a decision to make. This used to offer a checkbox per strand, and on a real
        // hairstyle that is three hundred of them named "2.1.106" — a choice nobody has the information to
        // make, presented as though they did. The classifier has the geometry and is the only thing here
        // that can tell a ponytail from a parting.
        var chosen = config.HatCompatHidePonytails ? proposal.Hide : [];
        if (config.HatCompatHidePonytails)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(chosen.Count > 0
                ? string.Format(s.WillHideFmt, chosen.Count)
                : s.NothingToHide);
        }

        ImGui.Spacing();
        if (ImGui.Button(s.Apply)) watcher.Apply(chosen);
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
