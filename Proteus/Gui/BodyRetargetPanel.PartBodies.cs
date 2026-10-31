using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// A body mod of its own for a part other than the garment's own — a long dress's legs onto another body than its chest
/// (see <see cref="StudioPartBodies"/>). Kept for the open garment and the body picked above it; not remembered past
/// them.
/// </summary>
internal sealed partial class BodyRetargetPanel
{
    /// <summary>The body mod each part with one of its own goes onto, by slot. A part not here goes onto <see cref="bodyDir"/>.</summary>
    private Dictionary<string, string> partBodies = new(StringComparer.Ordinal);

    /// <summary>What has been typed into each part's body dropdown's search box.</summary>
    private readonly Dictionary<string, string> partBodyFilters = new(StringComparer.Ordinal);

    /// <summary>Whether part bodies can be used for the garment as laid out now (see <see cref="StudioPartBodies.Offered"/>).</summary>
    private bool PartsOffered(in RetargetContext ctx) => StudioPartBodies.Offered(Layout(ctx), active?.Explicit == true);

    /// <summary>
    /// The body a part's sizes are of: the one place it is looked up, so a part's sizes are never read against another
    /// body's files. The body kept for it, set aside or not — a part set aside sits out of the refit instead (see
    /// <see cref="Chosen"/>), so its sizes stay its own body's. The garment's own part never has one (see
    /// <see cref="DropPrimaryPartBody"/>).
    /// </summary>
    private StudioPartBodies.Target PartTarget(string slot)
        => StudioPartBodies.Resolve(slot, null, bodyDir, Onto, partBodies, CachedCatalog);

    /// <summary>A part's sizes to refit onto, from its body, at the garment's race.</summary>
    private IReadOnlyList<BodyOption> PartOptions(string slot) => PartTarget(slot).Catalog?.For(slot, race) ?? [];

    /// <summary>
    /// The rows changed so that a part with a body of its own became the garment's own part (a shoe keeping its feet is
    /// laid out by its legs): that part goes onto the body picked above, so its own body goes, with the sizes of it.
    /// </summary>
    private void DropPrimaryPartBody(string primary)
    {
        if (!partBodies.Remove(primary)) return;
        to.Remove(primary);
        foreach (string key in refusals.Keys.Where(k => k.StartsWith(primary + ">", StringComparison.Ordinal)).ToList())
            refusals.Remove(key);
    }

    /// <summary>The part bodies in force, for the plan's key: none while they are set aside.</summary>
    private IReadOnlyDictionary<string, string> EffectiveParts(in RetargetContext ctx)
        => StudioPartBodies.Effective(partBodies, PartsOffered(ctx));

    /// <summary>
    /// A part's "Refit onto body" dropdown, between its "Made for" and "Refit onto": the body picked above until another
    /// is picked here. Under it, what that body lacks, and a warning when it is across the Rue line from the one above.
    /// </summary>
    private void DrawSlotBody(in RetargetContext ctx, string slot)
    {
        var ps = Strings.Parts;
        ImGui.TextUnformatted(ps.RetargetToBody);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.RetargetPartBodyTip);
        string filter = partBodyFilters.GetValueOrDefault(slot, "");
        string? current = partBodies.GetValueOrDefault(slot);
        if (DrawBodyCombo("##retargetPartBody" + slot, current, ref filter,
                          sameAsMain: string.Format(ps.RetargetPartBodySameFmt, BodyName(bodyDir))) is { } picked)
            PickPartBody(ctx, slot, picked);
        partBodyFilters[slot] = filter;

        if (partBodies.GetValueOrDefault(slot) is not { } own) return;
        var target = PartTarget(slot);
        if (target.Catalog?.For(slot, race).Count is not > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetNoSlotInBodyFmt, BodyName(own), SlotName(slot)));
        }
        else if (bodyDir != null && StudioPartBodies.RueApart(bodyDir, BodyName(bodyDir), own, BodyName(own)))
        {
            bool mainRue = AutoRefitTargets.IsRue(bodyDir, BodyName(bodyDir));
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetPartBodyRueApartFmt,
                                                mainRue ? BodyName(bodyDir) : BodyName(own),
                                                mainRue ? BodyName(own) : BodyName(bodyDir)));
        }
    }

    /// <summary>
    /// A body picked for one part: its sizes go — they were the old body's — and the size worn in the new one is put in
    /// their place, as a part put in use gets the size worn (see <see cref="DrawSlotFrom"/>).
    /// </summary>
    private void PickPartBody(in RetargetContext ctx, string slot, string dir)
    {
        if (StudioPartBodies.Override(bodyDir, dir) is { } own) partBodies[slot] = own;
        else partBodies.Remove(slot);
        to.Remove(slot);
        foreach (string key in refusals.Keys.Where(k => k.StartsWith(slot + ">", StringComparison.Ordinal)).ToList())
            refusals.Remove(key);
        if (PartTarget(slot).Catalog is { } snapshot && WornOption(snapshot, slot, PartTarget(slot).Dir) is { } worn)
            to[slot] = [worn];
        log.Information("[Proteus] retarget: {0} refitted onto {1}", slot, partBodies.GetValueOrDefault(slot) ?? bodyDir ?? "");
        DropPlan(ctx);
        StartValidate(slot);
    }

    /// <summary>
    /// Under a part set to a body of its own that cannot be used now: why it sits out, and that it comes back.
    /// </summary>
    private bool DrawSetAside(in RetargetContext ctx, string slot)
    {
        if (!StudioPartBodies.SetAside(slot, partBodies, PartsOffered(ctx))) return false;
        ImGui.TextDisabled(string.Format(Strings.Parts.RetargetPartBodySetAsideFmt, BodyName(partBodies[slot])));
        ImGui.Spacing();
        return true;
    }

    /// <summary>
    /// Why Preview is held when the garment's own part stays on the body it was made for and another part goes onto
    /// another body (see <see cref="StudioPartBodies.KeptOnOwnBody"/>); null when it is not.
    /// </summary>
    private string? KeptOnOwnWhy(in RetargetContext ctx)
    {
        var chosen = Chosen(ctx);
        string primary = Primary(ctx);
        var others = chosen.Where(s => s != primary).ToList();
        if (!StudioPartBodies.KeptOnOwnBody(SourceDir, bodyDir, others.Select(s => PartTarget(s).Dir)))
            return null;
        string moved = others.First(s => !string.Equals(PartTarget(s).Dir, bodyDir, StringComparison.OrdinalIgnoreCase));
        return string.Format(Strings.Parts.RetargetPartBodyKeptOnOwnFmt, SlotName(Inputs(ctx).GarmentSlot).ToLowerInvariant(),
                             BodyName(bodyDir), SlotName(moved).ToLowerInvariant());
    }
}
