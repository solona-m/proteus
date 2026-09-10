using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The hat-compatibility panel: says what the hairstyle you are wearing needs, lets you correct which parts
/// should vanish under a hat, and writes it.
/// <para/>
/// The confirmation step is the reason this is a panel and not a background task. Deciding that a ponytail
/// cannot fit under a hat is a judgement made from geometry alone, and getting it wrong makes hair vanish
/// under every hat in the game — on somebody else's mod, in their own files. So the solve only ever
/// proposes, the list is editable, and nothing is written until the button is pressed.
/// </summary>
internal sealed class HatCompatPanel(CompositorService compositor, IPluginLog log)
{
    /// <summary>What the last inspection found, and which parts the user has left ticked.</summary>
    private HatCompatService.Target? target;
    private HatCompatService.Proposal? proposal;
    private readonly HashSet<string> hide = new(StringComparer.Ordinal);
    private string message = "";
    private bool failed;

    /// <summary>The hair the current <see cref="proposal"/> describes, so a hairstyle change invalidates it.</summary>
    private string inspectedPath = "";

    /// <summary>Whether the mod already carries a Proteus patch, and can therefore be undone.</summary>
    private bool patched;

    public void Draw()
    {
        var s = Strings.HatCompat;

        // Re-inspect whenever the equipped hair changes. Cheap to check (a volatile snapshot and a string
        // compare) and the alternative — a Refresh button — makes the panel lie after every hairstyle
        // change until somebody notices and presses it.
        var now = compositor.HatCompatTarget();
        if ((now?.GamePath ?? "") != inspectedPath)
        {
            inspectedPath = now?.GamePath ?? "";
            target = now;
            proposal = null;
            message = "";
            failed = false;
            hide.Clear();
            if (now != null) Inspect(now);
        }

        if (target == null)
        {
            ImGui.TextWrapped(s.NoModdedHair);
            return;
        }

        ImGui.TextWrapped(string.Format(s.WearingFmt, ContentSlot.Parse(target.GamePath)?.SetTag ?? "?",
                                        System.IO.Path.GetFileName(target.ModRoot)));
        ImGui.Spacing();

        if (patched)
        {
            ImGui.TextWrapped(s.AlreadyPatched);
            if (ImGui.Button(s.Undo)) Revert();
            DrawMessage();
            return;
        }

        if (proposal == null) { DrawMessage(); return; }
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

        ImGui.Spacing();
        if (proposal.Hide.Count == 0)
        {
            ImGui.TextWrapped(s.NothingToHide);
        }
        else
        {
            ImGui.TextWrapped(s.HidePrompt);
            using (ImRaii.PushIndent())
                foreach (var part in proposal.Hide)
                {
                    var key = $"{part.Mesh}.{part.Submesh}";
                    bool on = hide.Contains(key);
                    if (ImGui.Checkbox($"{part.Label}###hatHide{key}", ref on))
                    {
                        if (on) hide.Add(key); else hide.Remove(key);
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled(string.Format(s.TrianglesFmt, part.TriangleCount));
                }
        }

        ImGui.Spacing();
        if (ImGui.Button(s.Apply)) Apply();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.ApplyTip);
        DrawMessage();
    }

    private void DrawMessage()
    {
        if (message.Length == 0) return;
        ImGui.Spacing();
        if (failed) ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), message);
        else ImGui.TextWrapped(message);
    }

    private void Inspect(HatCompatService.Target t)
    {
        try
        {
            // Per FILE, not per mod. A hair pack routinely ships a dozen hairstyles out of one folder, and
            // a mod-level test would report every one of them as already done the moment any one was.
            patched = HatCompatService.ReadRecord(t.ModRoot)?.Files
                .Contains(t.Rel, StringComparer.OrdinalIgnoreCase) ?? false;
            if (patched) return;

            proposal = HatCompatService.Inspect(t.Model, t.Rel, t.Head);
            if (proposal == null)
            {
                Fail(Strings.HatCompat.Unreadable);
                return;
            }
            // Everything the solve proposes starts ticked. It is a proposal, not a default to argue with —
            // the user's job here is to UNtick anything it got wrong, which is a much easier reading task
            // than deciding from scratch which of forty parts is a ponytail.
            foreach (var p in proposal.Hide) hide.Add($"{p.Mesh}.{p.Submesh}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "hat compat: inspecting {0} failed", t.Rel);
            Fail(Strings.HatCompat.Unreadable);
        }
    }

    private void Apply()
    {
        if (target == null || proposal == null) return;
        try
        {
            var chosen = proposal.Hide.Where(p => hide.Contains($"{p.Mesh}.{p.Submesh}")).ToList();
            var outcome = HatCompatService.Apply(target.ModRoot, target.Model, proposal, chosen);
            if (!outcome.Ok) { Fail(outcome.Message); return; }

            patched = true;
            failed = false;
            message = string.Format(Strings.HatCompat.DoneFmt, chosen.Count);
            // The game has the old model open under this path, and caches by resolved path — so nothing
            // changes on screen until it is made to load the file again.
            compositor.RestoreChangedAccessory();
        }
        catch (Exception ex)
        {
            log.Error(ex, "hat compat: applying to {0} failed", target.Rel);
            Fail(ex.Message);
        }
    }

    private void Revert()
    {
        if (target == null) return;
        try
        {
            var outcome = HatCompatService.Revert(target.ModRoot, target.Rel);
            if (!outcome.Ok) { Fail(outcome.Message); return; }
            patched = false;
            failed = false;
            message = Strings.HatCompat.Undone;
            inspectedPath = "";                 // re-inspect the restored file on the next frame
            compositor.RestoreChangedAccessory();
        }
        catch (Exception ex)
        {
            log.Error(ex, "hat compat: reverting {0} failed", target.ModRoot);
            Fail(ex.Message);
        }
    }

    private void Fail(string text)
    {
        failed = true;
        message = text;
    }
}
