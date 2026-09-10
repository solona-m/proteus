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
    /// <summary>Parts the user has UNticked, keyed "mesh.submesh", for the hairstyle on screen.</summary>
    private readonly HashSet<string> unticked = new(StringComparer.Ordinal);

    /// <summary>Which hairstyle <see cref="unticked"/> describes, so a change starts a fresh list.</summary>
    private string forPath = "";

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

        // A different hairstyle means the previous corrections describe geometry that is no longer here.
        if (view.Target.GamePath != forPath)
        {
            forPath = view.Target.GamePath;
            unticked.Clear();
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

        // The hide list is only offered while the setting that uses it is on — otherwise it is a list of
        // checkboxes that governs nothing, which reads as broken rather than as disabled.
        var chosen = new List<ModelPart>();
        if (config.HatCompatHidePonytails && proposal.Hide.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(s.HidePrompt);
            using (ImRaii.PushIndent())
                foreach (var part in proposal.Hide)
                {
                    var key = $"{part.Mesh}.{part.Submesh}";
                    bool on = !unticked.Contains(key);
                    if (ImGui.Checkbox($"{part.Label}###hatHide{key}", ref on))
                    {
                        if (on) unticked.Remove(key); else unticked.Add(key);
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled(string.Format(s.TrianglesFmt, part.TriangleCount));
                    if (on) chosen.Add(part);
                }
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
