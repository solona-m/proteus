using System;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>"Move skin too" in the Studio: the checkbox, the binding built behind it, and what it unlocks.</summary>
public sealed partial class PartsPanel
{
    /// <summary>The checkbox. Off every time the plugin starts — see <see cref="SkinMovePolicy.DefaultOn"/>.</summary>
    private bool moveSkin = SkinMovePolicy.DefaultOn;

    /// <summary>Skin moves under the tool in use now — see <see cref="SkinMovePolicy.Applies"/>. Worked out once a frame
    /// by <see cref="UpdateSkinMoves"/>, before anything that draws or picks reads it.</summary>
    private bool SkinMovesNow { get; set; }

    /// <summary>The binding being built on a worker, and the solve it is for.</summary>
    private Task<SkinFollow>? followTask;

    /// <summary>Why the binding for the open model failed, or null; the checkbox is disabled while it is set.</summary>
    private string? followError;

    /// <summary>The open model's binding has been logged, so it is logged once.</summary>
    private bool followLogged;

    /// <summary>
    /// Once a frame: take a finished binding, start one the checkbox wants, and hand the solve, the model view and the
    /// views' caches the answer to whether skin moves now. A change drops what was measured or built without it.
    /// </summary>
    private void UpdateSkinMoves()
    {
        ConsumeFollow();
        LogGuardLeftovers();
        if (moveSkin && volume is { FollowReady: false } && followTask == null && followError == null) StartFollow();

        bool now = SkinMovePolicy.Applies(moveSkin, tool is Tool.Bridge or Tool.Wind, volume?.FollowReady == true)
                   && (PaintTool || PartTool);
        if (volume != null) volume.MoveSkin = now;
        viewport.SkinMoves = now;
        if (now == SkinMovesNow) return;

        SkinMovesNow = now;
        polySelection = null;   // rebuilt with or without skin
        ForgetMoveFalloff();
        viewport.Recolour();
    }

    /// <summary>The guard run the log last heard about, so each is logged once.</summary>
    private int guardLogged;

    /// <summary>
    /// After a skin-moving operation: if the guard gave up with skin still through the cloth, say how much, so a report
    /// of skin showing after a sculpt has its answer in the log.
    /// </summary>
    private void LogGuardLeftovers()
    {
        if (volume is not { } vol || vol.GuardVersion == guardLogged) return;
        guardLogged = vol.GuardVersion;
        if (vol.SkinStillOutside > 0)
            log.Information("[Proteus] brush: skin follow: {0} skin points still outside the cloth", vol.SkinStillOutside);
    }

    /// <summary>Bind the open model's clothes to its skin on a worker; see <see cref="SkinFollow.Bind"/>.</summary>
    private void StartFollow()
    {
        if (volume == null) return;
        var inputs = volume.FollowInputs;
        followTask = Task.Run(() => SkinFollow.Bind(inputs));
    }

    /// <summary>Hand a finished binding to its solve — dropped if the model changed meanwhile — or note why it failed.</summary>
    private void ConsumeFollow()
    {
        if (followTask is not { IsCompleted: true } task) return;
        followTask = null;
        if (task.IsFaulted || task.IsCanceled)
        {
            followError = task.Exception?.GetBaseException().Message ?? "cancelled";
            log.Error(task.Exception?.GetBaseException(), "[Proteus] brush: skin follow: could not bind the clothes to the skin");
            return;
        }
        var binding = task.Result;
        if (volume?.AttachFollow(binding) != true) return;
        if (!followLogged)
        {
            followLogged = true;
            log.Information("[Proteus] brush: skin follow: bound {0} cloth points in {1:F0} ms",
                            binding.RiderCount, binding.BuildMilliseconds);
        }
    }

    /// <summary>A new model is on screen: its binding is built afresh, and with the checkbox off it carries no skin locks.</summary>
    private void SkinMovesModelAdopted()
    {
        followTask = null;   // one still running was for the old solve, which AttachFollow would refuse anyway
        followError = null;
        followLogged = false;
        guardLogged = 0;
        SkinMovesNow = false;
        if (!moveSkin && parts != null) SkinMovePolicy.DropSkinLocks(LockSet(lockedParts), parts.Parts);
    }

    /// <summary>The checkbox, under the brush's settings and the Move tools' alike.</summary>
    private void DrawSkinMoves()
    {
        var ps = Strings.Parts;
        bool notThisBrush = tool is Tool.Bridge or Tool.Wind;
        bool busy = volume is { Moving: true } or { Grabbing: true } or { StrokeOpen: true };

        ImGui.Spacing();
        bool on = moveSkin;
        var (enabled, tip) = SkinMovePolicy.Checkbox(busy, notThisBrush, followError != null);
        using (ImRaii.Disabled(!enabled))
            if (ImGui.Checkbox(ps.SkinMoves, ref on)) SetMoveSkin(on);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tip switch
            {
                SkinMovePolicy.CheckboxTip.Failed => ps.SkinMovesFailed,
                SkinMovePolicy.CheckboxTip.NotThisBrush => ps.SkinMovesNotThisBrushTip,
                _ => ps.SkinMovesTip,
            });

        if (!moveSkin || notThisBrush) return;
        ImGui.PushTextWrapPos(0);
        if (volume is { FollowReady: false } && followError == null) ImGui.TextDisabled(ps.SkinMovesPreparing);
        if (parts != null && SkinMovePolicy.IsBodyModel(parts)) ImGui.TextColored(ProteusStyle.Warn, ps.SkinMovesBodyModelWarn);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// Turn the checkbox on or off. Off, the skin already moved stays where it is, but skin can no longer be locked,
    /// chosen or picked: its locks are dropped and a skin choice for Move is let go.
    /// </summary>
    private void SetMoveSkin(bool on)
    {
        moveSkin = on;
        if (!on && parts != null)
        {
            if (SkinMovePolicy.DropSkinLocks(LockSet(lockedParts), parts.Parts) > 0) ApplyLocks();
            if (MovePart() is { } chosen && IsSkin(chosen)) movePart = null;
            polySelection = null;
            int before = movePolys.Count;
            movePolys.RemoveWhere(k => PartOfVertex(k.A) is var p and >= 0 && IsSkin(parts.Parts[p]));
            if (movePolys.Count != before) movePolysVersion++;
        }
        ForgetMoveFalloff();
        viewport.Recolour();
    }
}
