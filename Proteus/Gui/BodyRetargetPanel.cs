using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Proteus.Localization;
using Proteus.Interop;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Studio tool that refits a garment from one body size onto another.
/// <para/>
/// A class of its own rather than another <c>PartsPanel</c> partial: it owns some twenty fields of its own — the body
/// mod, its catalog, a source and target per slot, three worker tasks, the plan, the preview, the group name — and
/// that file's field block is long enough already. It reaches nothing of the panel directly; everything it needs
/// arrives in <see cref="RetargetContext"/>.
/// </summary>
internal sealed class BodyRetargetPanel(PenumbraBridge penumbra, IPluginLog log)
{
    /// <summary>What the Studio tab lends this tool for the frame: the open model, and the things only it can do.</summary>
    /// <param name="FlushPending">Save a pending brush stroke, so the retarget starts from what the user can see.</param>
    /// <param name="PushPreview">Put bytes on the character for this model's game paths.</param>
    /// <param name="SetStatus">Say something in the tab's status line.</param>
    /// <param name="AfterModChange">The mod's files changed on disk: reload it and redraw.</param>
    internal readonly record struct RetargetContext(
        string ModRoot, string ModDir, string ModelRel, string GamePath, string ModelLabel,
        ModelParts Garment, byte[] GarmentBytes,
        Action FlushPending, Action<byte[]> PushPreview, Action EndPreview,
        Action<string, bool> SetStatus, Action AfterModChange);

    // ── the body, and the pair chosen per slot ──────────────────────────────

    private string? bodyDir;
    private string bodyFilter = "";
    private BodySizeCatalog? catalog;

    /// <summary>Per slot: the option the garment was built for, and the one to refit it onto.</summary>
    private readonly Dictionary<string, BodyOption> from = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BodyOption> to = new(StringComparer.Ordinal);

    /// <summary>Per slot, why the chosen pair cannot be used; empty when it can.</summary>
    private readonly Dictionary<string, string> refusals = new(StringComparer.Ordinal);

    /// <summary>Per slot, what the detector made of the garment.</summary>
    private readonly Dictionary<string, BodySizeMatch.Ranking> detected = new(StringComparer.Ordinal);

    private string groupName = "";
    private string? groupNameFor;

    private BodyRetarget.Planned? planned;

    private Task<DetectResult>? detectTask;
    private Task<PlanResult>? planTask;
    private Task<BodyRetargetWriter.Outcome>? saveTask;

    private sealed record DetectResult(string ModelRel, Dictionary<string, BodySizeMatch.Ranking> Rankings);
    private sealed record PlanResult(string Key, BodyRetarget.Planned? Planned, string Error);

    /// <summary>Forget everything model-specific. Called when the open mod or model changes, and on leaving the tab.</summary>
    public void Clear()
    {
        from.Clear();
        to.Clear();
        refusals.Clear();
        detected.Clear();
        planned = null;
        groupNameFor = null;
    }

    public void Draw(in RetargetContext ctx)
    {
        var ps = Strings.Parts;
        Consume(ctx);

        if (groupNameFor != ctx.ModelRel)
        {
            groupNameFor = ctx.ModelRel;
            groupName = string.Format(ps.RetargetGroupFmt, ctx.ModelLabel);
        }

        DrawBodyPicker(ctx);
        if (catalog is not { IsBody: true })
        {
            ImGui.TextWrapped(ps.RetargetPickBody);
            return;
        }

        ImGui.Separator();
        foreach (string slot in Slots(ctx))
            DrawSlot(ctx, slot);

        ImGui.Separator();
        DrawActions(ctx);
    }

    // ── the body mod ────────────────────────────────────────────────────────

    private void DrawBodyPicker(in RetargetContext ctx)
    {
        var ps = Strings.Parts;
        ImGui.TextUnformatted(ps.RetargetBody);
        ImGui.SetNextItemWidth(-1);

        var bodies = Bodies();
        string current = bodyDir != null && bodies.TryGetValue(bodyDir, out string? name) ? name : ps.RetargetNoBody;
        using var combo = ImRaii.Combo("##retargetBody", current);
        if (!combo) return;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##retargetBodyFilter", Strings.Export.FilterHint, ref bodyFilter, 64);
        foreach (var (dir, label) in bodies.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (bodyFilter.Length > 0 && label.IndexOf(bodyFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!ImGui.Selectable(label, dir == bodyDir)) continue;

            bodyDir = dir;
            catalog = BodySizeCatalog.Read(BodyRoot(dir) ?? "");
            Clear();
            StartDetect(ctx);
        }
    }

    /// <summary>
    /// Installed mods that publish a choice of body models. Recomputed only when the mod list changes: reading every
    /// mod's manifest is not something to do per frame.
    /// </summary>
    private Dictionary<string, string> bodyCache = [];
    private int bodyCacheCount = -1;

    private Dictionary<string, string> Bodies()
    {
        // Null means Penumbra could not be asked, which is not the same as "no mods": keep the last answer rather
        // than emptying the picker while the user is looking at it.
        if (penumbra.GetAllMods() is not { } all) return bodyCache;
        if (all.Count == bodyCacheCount) return bodyCache;
        bodyCacheCount = all.Count;

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, name) in all)
        {
            string? root = BodyRoot(dir);
            if (root == null) continue;
            try
            {
                if (BodySizeCatalog.Read(root).IsBody) found[dir] = name;
            }
            catch (Exception ex)
            {
                log.Verbose("[Proteus] retarget: {0} is not a body ({1})", dir, ex.Message);
            }
        }
        return bodyCache = found;
    }

    private string? BodyRoot(string dir)
    {
        string? root = penumbra.GetModDirectory();
        return root == null ? null : Path.Combine(root, dir);
    }

    /// <summary>
    /// The slot this garment is worn in, which is the one its cloth was cut against. Always required.
    /// </summary>
    private string Primary(in RetargetContext ctx)
    {
        var slots = catalog!.Slots.ToList();
        string? own = BodySizeCatalog.SlotOf(ctx.GamePath);
        return own != null && slots.Contains(own) ? own : slots.FirstOrDefault() ?? "_top";
    }

    /// <summary>
    /// Every slot worth offering, the garment's own first.
    /// <para/>
    /// All of them are offered and only <see cref="Primary"/> is required, rather than trying to work out which halves
    /// of the body a garment reaches. A long dress is worn in the chest slot and hangs over the legs, so it genuinely
    /// needs both; a bikini top needs one. Nothing in the file says which, and the cheap guesses are wrong often
    /// enough to be worse than a second dropdown the user can ignore. Slots with a single option are left out: there
    /// is no size to change there.
    /// </summary>
    private List<string> Slots(in RetargetContext ctx)
    {
        string primary = Primary(ctx);
        var slots = new List<string> { primary };
        foreach (string slot in catalog!.Slots)
            if (slot != primary && Distinct(slot) > 1) slots.Add(slot);
        return slots;
    }

    /// <summary>The slots actually taking part: the ones with both ends chosen.</summary>
    private List<string> Chosen(in RetargetContext ctx)
        => Slots(ctx).Where(s => from.ContainsKey(s) && to.ContainsKey(s)).ToList();

    /// <summary>How many different models a slot offers — several options can point at one file.</summary>
    private int Distinct(string slot)
        => catalog!.For(slot).Select(o => o.Rel).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    // ── one slot's from/to ──────────────────────────────────────────────────

    private void DrawSlot(in RetargetContext ctx, string slot)
    {
        var ps = Strings.Parts;
        var options = catalog!.For(slot);
        if (options.Count == 0) return;

        bool optional = slot != Primary(ctx);
        ImGui.TextUnformatted(optional
                                  ? string.Format(ps.RetargetSlotOptionalFmt, SlotName(slot))
                                  : string.Format(ps.RetargetSlotFmt, SlotName(slot), Distinct(slot)));
        if (optional && ImGui.IsItemHovered()) ImGui.SetTooltip(ps.RetargetSlotOptionalTip);

        DrawOptionCombo($"##retargetFrom{slot}", ps.RetargetFrom, options, from, slot, ctx);
        DrawConfidence(slot);
        DrawOptionCombo($"##retargetTo{slot}", ps.RetargetTo, options, to, slot, ctx);

        if (refusals.TryGetValue(slot, out string? why) && why.Length > 0)
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(why);

        ImGui.Spacing();
    }

    private void DrawOptionCombo(string id, string label, IReadOnlyList<BodyOption> options,
                                 Dictionary<string, BodyOption> into, string slot, in RetargetContext ctx)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);

        string current = into.TryGetValue(slot, out var chosen) ? chosen.Label : Strings.Parts.RetargetChoose;
        using var combo = ImRaii.Combo(id, current);
        if (!combo) return;

        string? group = null;
        foreach (var option in options)
        {
            if (option.Group != group)
            {
                group = option.Group;
                ImGui.Separator();
                ImGui.TextDisabled(group);
            }
            if (!ImGui.Selectable(option.Label + "##" + option.Rel, chosen == option)) continue;

            into[slot] = option;
            planned = null;
            Revalidate(slot, ctx);
        }
    }

    private void DrawConfidence(string slot)
    {
        if (!detected.TryGetValue(slot, out var ranking)) return;
        var ps = Strings.Parts;

        (string text, System.Numerics.Vector4 colour) = ranking.Confidence switch
        {
            BodySizeMatch.Confidence.NoBodyMesh => (ps.RetargetNoBodyMesh, ProteusStyle.Warn),
            BodySizeMatch.Confidence.Exact      => (string.Format(ps.RetargetExactFmt, ranking.Best!.Value.Option.Label),
                                                    ProteusStyle.Ok),
            BodySizeMatch.Confidence.Likely     => (string.Format(ps.RetargetLikelyFmt,
                                                                  ranking.Best!.Value.HitRate), ProteusStyle.Ok),
            BodySizeMatch.Confidence.Guess      => (string.Format(ps.RetargetGuessFmt,
                                                                  ranking.Best!.Value.Rms * 1000f), ProteusStyle.Warn),
            _                                   => (Ambiguous(ranking), ProteusStyle.Warn),
        };

        using (ImRaii.PushColor(ImGuiCol.Text, colour))
            ImGui.TextWrapped(text);
    }

    private static string Ambiguous(BodySizeMatch.Ranking ranking)
    {
        var names = ranking.Scores.Take(3).Select(s => s.Option.Label);
        return string.Format(Strings.Parts.RetargetAmbiguousFmt, string.Join(", ", names));
    }

    private static string SlotName(string slot) => slot switch
    {
        "_top" => Strings.Parts.RetargetChest,
        "_dwn" => Strings.Parts.RetargetLegs,
        "_glv" => Strings.Parts.RetargetHands,
        "_sho" => Strings.Parts.RetargetFeet,
        _      => slot,
    };

    // ── the buttons ─────────────────────────────────────────────────────────

    private void DrawActions(in RetargetContext ctx)
    {
        var ps = Strings.Parts;
        bool busy = detectTask != null || planTask != null || saveTask != null;
        // The garment's own slot is required; the others take part only if both their ends are chosen.
        //
        // Only a MISSING SOURCE holds the button, never a missing target. The detector fills the source of every slot
        // it recognises, including ones this garment does not reach, so "source but no target" is the normal state of
        // an optional slot the user is ignoring — treating that as unfinished would leave the button permanently
        // disabled. "Target but no source" is the genuinely incomplete case.
        string primary = Primary(ctx);
        var slots = Slots(ctx);
        bool sourceMissing = slots.Any(s => to.ContainsKey(s) && !from.ContainsKey(s));
        bool refused = slots.Any(s => refusals.TryGetValue(s, out string? w) && w.Length > 0);
        bool ready = from.ContainsKey(primary) && to.ContainsKey(primary) && !sourceMissing && !refused;

        if (detectTask != null) ImGui.TextUnformatted(ps.RetargetChecking);

        using (ImRaii.Disabled(busy || !ready))
            if (ImGui.Button(ps.RetargetPreview, FullWidth()))
                StartPlan(ctx);

        if (planTask != null) ImGui.TextUnformatted(ps.RetargetWorking);

        if (planned is { } done)
        {
            ImGui.TextWrapped(Describe(done.Report));
            if (done.Report.HasOtherLods)
                using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                    ImGui.TextWrapped(ps.RetargetOtherLods);
            if (done.Report.SnapRate is > 0f and < 0.8f)
                using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                    ImGui.TextWrapped(ps.RetargetLowSnap);

            if (ImGui.Button(ps.RetargetClearPreview, FullWidth())) { ctx.EndPreview(); planned = null; }

            ImGui.Spacing();
            ImGui.TextUnformatted(ps.RetargetGroupName);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##retargetGroup", ref groupName, 128);

            foreach (string clash in BodyRetargetWriter.ClashingGroups(ctx.ModRoot, ctx.GamePath, groupName))
                using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                    ImGui.TextWrapped(string.Format(ps.RetargetClashFmt, clash));

            using (ImRaii.Disabled(busy || groupName.Trim().Length == 0))
                if (ImGui.Button(ps.RetargetSave, FullWidth()))
                    StartSave(ctx);
        }

        if (BodyRetargetWriter.ReadRecord(ctx.ModRoot) is { Options.Count: > 0 } record)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(string.Format(ps.RetargetSavedFmt, record.Options.Count, record.Group));
            if (ImGui.Button(ps.RetargetOpenInPenumbra, FullWidth())) penumbra.OpenToMod(ctx.ModDir);

            bool armed = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
            using (ImRaii.Disabled(busy || !armed))
                if (ImGui.Button(ps.RetargetUndo, FullWidth()))
                    StartUndo(ctx, record);
            if (!armed && ImGui.IsItemHovered()) ImGui.SetTooltip(ps.BrushRevertArmTip);
        }
    }

    private static string Describe(BodyRetarget.Report r)
    {
        var ps = Strings.Parts;
        var lines = new List<string>
        {
            string.Format(ps.RetargetMovedFmt, r.WorstMove * 1000f),
        };
        if (r.Snapped > 0) lines.Add(string.Format(ps.RetargetSnappedFmt, r.Snapped, r.SnapRate));
        if (r.Pushed > 0) lines.Add(string.Format(ps.RetargetPushedFmt, r.Pushed, r.WorstPush * 1000f));
        if (r.Missed > 0) lines.Add(string.Format(ps.RetargetMissedFmt, r.Missed));
        return string.Join("\n", lines);
    }

    private static System.Numerics.Vector2 FullWidth() => new(-1, 0);

    // ── the worker half ─────────────────────────────────────────────────────

    private void StartDetect(in RetargetContext ctx)
    {
        if (detectTask != null || catalog is not { IsBody: true }) return;

        var snapshot = catalog;
        var garment = ctx.Garment;
        var slots = Slots(ctx);
        string rel = ctx.ModelRel;

        detectTask = Task.Run(() =>
        {
            var rankings = new Dictionary<string, BodySizeMatch.Ranking>(StringComparer.Ordinal);
            foreach (string slot in slots)
                rankings[slot] = BodySizeMatch.Rank(garment, snapshot.For(slot), snapshot.PathOf);
            return new DetectResult(rel, rankings);
        });
    }

    private void StartPlan(in RetargetContext ctx)
    {
        if (planTask != null || catalog is not { } snapshot) return;
        ctx.FlushPending();

        var slots = Chosen(ctx);
        var chosen = slots.Select(s => (Slot: s, From: from[s], To: to[s])).ToList();
        var garment = ctx.Garment;
        var bytes = ctx.GarmentBytes;
        string key = Key(ctx);

        planTask = Task.Run(() =>
        {
            try
            {
                var pairs = new List<BodyRetarget.SlotPair>();
                foreach (var (slot, source, target) in chosen)
                {
                    var sourceBytes = File.ReadAllBytes(snapshot.PathOf(source));
                    var targetBytes = File.ReadAllBytes(snapshot.PathOf(target));
                    var sourceParts = ModelPartReader.Read(sourceBytes);
                    var targetParts = ModelPartReader.Read(targetBytes);
                    if (sourceParts == null || targetParts == null)
                        return new PlanResult(key, null, string.Format(Strings.Parts.RetargetUnreadableFmt, slot));

                    if (!IdentityCorrespondence.TryBuild(sourceParts, targetParts, SlotName(slot), out var built,
                                                         out string refusal, Uv(sourceBytes), Uv(targetBytes)))
                        return new PlanResult(key, null, refusal);

                    pairs.Add(new BodyRetarget.SlotPair(slot, built!, targetParts));
                }

                return new PlanResult(key, BodyRetarget.Plan(garment, bytes, pairs), "");
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Proteus] retarget: planning failed");
                return new PlanResult(key, null, ex.Message);
            }
        });
    }

    private static float[] Uv(byte[] mdl)
        => SecondSkinWriter.TryReadLod0Geometry(mdl, out _, out var uv, out _, out _, out _, false, false, null)
            ? uv
            : [];

    private void StartSave(in RetargetContext ctx)
    {
        if (saveTask != null || planned is not { } done) return;

        string root = ctx.ModRoot;
        string group = groupName.Trim();
        string option = OptionName(ctx);
        string path = ctx.GamePath;
        byte[] model = done.Model;
        string body = bodyDir ?? "";
        string labelFrom = string.Join(" + ", Chosen(ctx).Select(s => from[s].Label));
        string labelTo = string.Join(" + ", Chosen(ctx).Select(s => to[s].Label));

        saveTask = Task.Run(() => BodyRetargetWriter.Save(root, group, option, path, model, body, labelFrom, labelTo));
    }

    private void StartUndo(in RetargetContext ctx, BodyRetargetWriter.Record record)
    {
        if (saveTask != null) return;
        string root = ctx.ModRoot;
        string group = record.Group;
        string option = record.Options[^1].Name;
        saveTask = Task.Run(() => BodyRetargetWriter.Undo(root, group, option));
    }

    /// <summary>The option's name: what the user will pick in Penumbra, so it says which body and which size.</summary>
    private string OptionName(in RetargetContext ctx)
    {
        var slots = Chosen(ctx);
        string body = bodyDir != null && Bodies().TryGetValue(bodyDir, out string? name) ? name : "Body";
        return $"{body} — {string.Join(" + ", slots.Select(s => to[s].Label))}";
    }

    private string Key(in RetargetContext ctx)
        => ctx.ModelRel + "|" + string.Join("|", Chosen(ctx).Select(s => $"{s}:{from[s].Rel}>{to[s].Rel}"));

    /// <summary>The framework-thread half: take up whatever finished, and do the parts only this thread may.</summary>
    private void Consume(in RetargetContext ctx)
    {
        if (detectTask is { IsCompleted: true } dt)
        {
            detectTask = null;
            if (Faulted(dt, ctx)) { }
            else if (dt.Result.ModelRel == ctx.ModelRel)
            {
                foreach (var (slot, ranking) in dt.Result.Rankings)
                {
                    detected[slot] = ranking;
                    if (ranking.Preselect && ranking.Best is { } best && !from.ContainsKey(slot))
                    {
                        from[slot] = best.Option;
                        Revalidate(slot, ctx);
                    }
                }
            }
        }

        if (planTask is { IsCompleted: true } pt)
        {
            planTask = null;
            if (Faulted(pt, ctx)) { }
            else if (pt.Result.Error.Length > 0)
            {
                ctx.SetStatus(pt.Result.Error, true);
            }
            else if (pt.Result.Planned is { } done && pt.Result.Key == Key(ctx))
            {
                planned = done;
                ctx.PushPreview(done.Model);
                ctx.SetStatus(Describe(done.Report).Replace('\n', ' '), false);
            }
        }

        if (saveTask is { IsCompleted: true } st)
        {
            saveTask = null;
            if (Faulted(st, ctx)) return;

            var outcome = st.Result;
            ctx.SetStatus(outcome.Message, !outcome.Ok);
            if (!outcome.Ok) return;

            ctx.EndPreview();
            planned = null;
            ctx.AfterModChange();
        }
    }

    private bool Faulted(Task task, in RetargetContext ctx)
    {
        if (!task.IsFaulted && !task.IsCanceled) return false;
        log.Warning(task.Exception, "[Proteus] retarget: a background step failed");
        ctx.SetStatus(task.Exception?.GetBaseException().Message ?? "cancelled", true);
        return true;
    }

    /// <summary>
    /// Check one slot's chosen pair now rather than at save time, so the refusal appears beside the two option names
    /// that caused it. Reads two models, which is why it runs only when a choice changes.
    /// </summary>
    private void Revalidate(string slot, in RetargetContext ctx)
    {
        refusals.Remove(slot);
        if (catalog is not { } snapshot) return;
        if (!from.TryGetValue(slot, out var source) || !to.TryGetValue(slot, out var target)) return;
        if (source.Rel == target.Rel) return;

        try
        {
            var sourceBytes = File.ReadAllBytes(snapshot.PathOf(source));
            var targetBytes = File.ReadAllBytes(snapshot.PathOf(target));
            var sourceParts = ModelPartReader.Read(sourceBytes);
            var targetParts = ModelPartReader.Read(targetBytes);
            if (sourceParts == null || targetParts == null)
            {
                refusals[slot] = string.Format(Strings.Parts.RetargetUnreadableFmt, SlotName(slot));
                return;
            }

            if (!IdentityCorrespondence.TryBuild(sourceParts, targetParts, SlotName(slot), out _, out string refusal,
                                                 Uv(sourceBytes), Uv(targetBytes)))
                refusals[slot] = refusal;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] retarget: could not check {0}", slot);
            refusals[slot] = ex.Message;
        }
    }
}
