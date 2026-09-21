using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Proteus.Interop;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Studio tool that refits a garment from one body size onto another.
/// <para/>
/// A class of its own rather than another <c>PartsPanel</c> partial: it owns some twenty fields — the body mod, its
/// catalog, a source and target per slot, several worker tasks, the plan, the group name — and that file's field
/// block is long enough already. It reaches nothing of the panel directly; everything it needs arrives in
/// <see cref="RetargetContext"/>.
/// <para/>
/// Everything that touches a file runs on a worker, and that is a rule rather than a preference. Finding the body mods
/// reads every installed mod's manifest; checking a pair reads two 900 KB models; a manifest here can be 400 KB of
/// json. Any of those on the draw thread is a visible hitch at best and, for the mod scan, a freeze the first time the
/// tool is opened.
/// </summary>
internal sealed class BodyRetargetPanel(PenumbraBridge penumbra, IPluginLog log)
{
    /// <summary>What the Studio tab lends this tool for the frame: the open model, and the things only it can do.</summary>
    /// <param name="Redirects">The open mod's redirects, already read by the tab. Used to spot a group of the author's
    /// that also replaces this model, without re-reading the manifest every frame.</param>
    /// <param name="FlushPending">Save a pending brush stroke, so the retarget starts from what the user can see.</param>
    /// <param name="PushPreview">Put bytes on the character for this model's game paths. False means "busy, try again
    /// next frame"; true means pushed, or that this model can never be previewed and there is no point retrying.</param>
    /// <param name="SetStatus">Say something in the tab's status line.</param>
    /// <param name="AfterModChange">The mod's files changed on disk: reload it and redraw.</param>
    internal readonly record struct RetargetContext(
        string ModRoot, string ModDir, string ModelRel, string GamePath, string ModelLabel,
        ModelParts Garment, byte[] GarmentBytes, IReadOnlyList<PenumbraModMeta.Redirect> Redirects,
        Action FlushPending, Func<byte[], bool> PushPreview, Action EndPreview,
        Action<string, bool> SetStatus, Action AfterModChange);

    // ── the body, and the pair chosen per slot ──────────────────────────────

    private string? bodyDir;
    private string bodyFilter = "";
    private BodySizeCatalog? catalog;

    /// <summary>Per slot: the option the garment was built for, and the one to refit it onto.</summary>
    private readonly Dictionary<string, BodyOption> from = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BodyOption> to = new(StringComparer.Ordinal);

    /// <summary>Per slot, why the chosen pair cannot be used. Absent when it can, or has not been checked.</summary>
    private readonly Dictionary<string, string> refusals = new(StringComparer.Ordinal);

    /// <summary>Per slot, what the detector made of the garment.</summary>
    private readonly Dictionary<string, BodySizeMatch.Ranking> detected = new(StringComparer.Ordinal);

    /// <summary>The model the detector last ran for, so opening another model runs it again.</summary>
    private string? detectedFor;

    private string groupName = "";
    private string? groupNameFor;

    private BodyRetarget.Planned? planned;

    /// <summary>A preview the live preview was too busy to take, retried each frame until it goes.</summary>
    private byte[]? pendingPreview;

    // ── worker tasks ────────────────────────────────────────────────────────

    private Task<Dictionary<string, string>>? bodiesTask;
    private Task<DetectResult>? detectTask;
    private Task<PlanResult>? planTask;
    private Task<SaveResult>? saveTask;

    /// <summary>Pair checks in flight, per slot, each tagged with the pair it was started for.</summary>
    private readonly Dictionary<string, (string Pair, Task<string> Refusal)> validating = new(StringComparer.Ordinal);

    private sealed record DetectResult(string ModelRel, Dictionary<string, BodySizeMatch.Ranking> Rankings);
    private sealed record PlanResult(string Key, BodyRetarget.Planned? Planned, string Error);
    private sealed record SaveResult(BodyRetargetWriter.Outcome Outcome, BodyRetargetWriter.Record? Record);

    // ── caches for things that would otherwise be read every frame ──────────

    /// <summary>Installed body mods, dir to display name; null until the first scan has come back.</summary>
    private Dictionary<string, string>? bodies;

    /// <summary>The mod's retarget record, and the mod it was read for. Re-read after a save or an undo.</summary>
    private BodyRetargetWriter.Record? record;
    private string? recordFor;

    /// <summary>Forget everything model-specific. Called when the open mod or model changes, and on leaving the tab.</summary>
    public void Clear()
    {
        from.Clear();
        to.Clear();
        refusals.Clear();
        detected.Clear();
        validating.Clear();
        detectedFor = null;
        planned = null;
        pendingPreview = null;
        groupNameFor = null;
        recordFor = null;
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

        if (recordFor != ctx.ModRoot)
        {
            // One read per mod, not per frame; saves and undos hand back the fresh record themselves.
            recordFor = ctx.ModRoot;
            record = BodyRetargetWriter.ReadRecord(ctx.ModRoot);
        }

        DrawBodyPicker(ctx);
        if (catalog is not { IsBody: true })
        {
            ImGui.TextWrapped(bodies == null ? ps.RetargetFindingBodies : ps.RetargetPickBody);
            DrawSaved(ctx);
            return;
        }

        // A different model opened under the same body: the old detection was about the old model.
        if (detectedFor != ctx.ModelRel && detectTask == null) StartDetect(ctx);

        ImGui.Separator();
        foreach (string slot in Slots(ctx))
            DrawSlot(ctx, slot);

        ImGui.Separator();
        DrawActions(ctx);
        DrawSaved(ctx);
    }

    // ── the body mod ────────────────────────────────────────────────────────

    private void DrawBodyPicker(in RetargetContext ctx)
    {
        var ps = Strings.Parts;
        ImGui.TextUnformatted(ps.RetargetBody);
        ImGui.SetNextItemWidth(-1);

        // The first scan starts as soon as the tool is shown, so the list is usually ready by the time it is opened.
        if (bodies == null && bodiesTask == null) StartBodyScan();

        string current = bodyDir != null && bodies != null && bodies.TryGetValue(bodyDir, out string? name)
                             ? name
                             : ps.RetargetNoBody;
        using var combo = ImRaii.Combo("##retargetBody", current);
        if (!combo) return;

        // Opening the list re-scans in the background, so a body mod installed since the last look appears.
        if (ImGui.IsWindowAppearing() && bodiesTask == null) StartBodyScan();

        if (bodies == null)
        {
            ImGui.TextDisabled(ps.RetargetFindingBodies);
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##retargetBodyFilter", Strings.Export.FilterHint, ref bodyFilter, 64);
        foreach (var (dir, label) in bodies.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (bodyFilter.Length > 0 && label.IndexOf(bodyFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!ImGui.Selectable(label, dir == bodyDir)) continue;

            bodyDir = dir;
            catalog = BodyRoot(dir) is { } root ? BodySizeCatalog.Read(root) : null;
            Clear();
        }
    }

    /// <summary>
    /// Find installed mods that publish a choice of body models. The mod list is asked for here, on the framework
    /// thread, because it is Penumbra IPC; only the manifest reads go to the worker.
    /// </summary>
    private void StartBodyScan()
    {
        var all = penumbra.GetAllMods();
        string? modsRoot = penumbra.GetModDirectory();
        if (all == null || modsRoot == null) return;

        bodiesTask = Task.Run(() =>
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, name) in all)
            {
                try
                {
                    if (BodySizeCatalog.Read(Path.Combine(modsRoot, dir)).IsBody) found[dir] = name;
                }
                catch
                {
                    // A mod whose manifest cannot be read is not a body as far as the picker is concerned.
                }
            }
            return found;
        });
    }

    private string? BodyRoot(string dir)
    {
        string? root = penumbra.GetModDirectory();
        return root == null ? null : Path.Combine(root, dir);
    }

    // ── which slots take part ───────────────────────────────────────────────

    /// <summary>The slot this garment is worn in, which is the one its cloth was cut against. Always required.</summary>
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
    /// enough to be worse than a second dropdown the user can ignore. Slots with a single model are left out: there
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

        DrawOptionCombo($"##retargetFrom{slot}", ps.RetargetFrom, options, from, slot);
        DrawConfidence(slot);
        DrawOptionCombo($"##retargetTo{slot}", ps.RetargetTo, options, to, slot);

        if (validating.ContainsKey(slot))
            ImGui.TextDisabled(ps.RetargetCheckingPair);
        else if (refusals.TryGetValue(slot, out string? why))
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(why);

        ImGui.Spacing();
    }

    private void DrawOptionCombo(string id, string label, IReadOnlyList<BodyOption> options,
                                 Dictionary<string, BodyOption> into, string slot)
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
            StartValidate(slot);
        }
    }

    private void DrawConfidence(string slot)
    {
        if (!detected.TryGetValue(slot, out var ranking)) return;
        var ps = Strings.Parts;

        (string text, System.Numerics.Vector4 colour) = ranking.FromCloth && ranking.Best is { } fromCloth
            && ranking.Confidence is BodySizeMatch.Confidence.Likely or BodySizeMatch.Confidence.Guess
            ? (string.Format(ranking.Confidence == BodySizeMatch.Confidence.Likely
                                 ? ps.RetargetClothLikelyFmt
                                 : ps.RetargetClothGuessFmt,
                             fromCloth.Option.Label),
               ranking.Confidence == BodySizeMatch.Confidence.Likely ? ProteusStyle.Ok : ProteusStyle.Warn)
            : ranking.Confidence switch
        {
            BodySizeMatch.Confidence.NoBodyMesh => (ps.RetargetNoBodyMesh, ProteusStyle.Warn),
            BodySizeMatch.Confidence.TooLittle  => (string.Format(ps.RetargetTooLittleFmt, SlotName(slot).ToLowerInvariant()),
                                                    ProteusStyle.Warn),
            BodySizeMatch.Confidence.Exact      => (string.Format(ps.RetargetExactFmt, ranking.Best!.Value.Option.Label),
                                                    ProteusStyle.Ok),
            BodySizeMatch.Confidence.Likely     => (string.Format(ps.RetargetLikelyFmt, ranking.Best!.Value.Rms * 1000f),
                                                    ProteusStyle.Ok),
            BodySizeMatch.Confidence.Guess      => (string.Format(ps.RetargetGuessFmt, ranking.Best!.Value.Rms * 1000f),
                                                    ProteusStyle.Warn),
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
        bool busy = detectTask != null || planTask != null || saveTask != null || validating.Count > 0;

        // The garment's own slot is required; the others take part only if both their ends are chosen.
        //
        // Only a MISSING SOURCE holds the button, never a missing target. The detector fills the source of every slot
        // it recognises, including ones this garment does not reach, so "source but no target" is the normal state of
        // an optional slot the user is ignoring — treating that as unfinished would leave the button permanently
        // disabled. "Target but no source" is the genuinely incomplete case.
        string primary = Primary(ctx);
        var slots = Slots(ctx);
        bool sourceMissing = slots.Any(s => to.ContainsKey(s) && !from.ContainsKey(s));
        bool refused = Chosen(ctx).Any(s => refusals.ContainsKey(s));
        bool ready = from.ContainsKey(primary) && to.ContainsKey(primary) && !sourceMissing && !refused;

        if (detectTask != null) ImGui.TextUnformatted(ps.RetargetChecking);

        using (ImRaii.Disabled(busy || !ready))
            if (ImGui.Button(ps.RetargetPreview, FullWidth()))
                StartPlan(ctx);

        if (planTask != null) ImGui.TextUnformatted(ps.RetargetWorking);
        if (planned is not { } done) return;

        ImGui.TextWrapped(Describe(done.Report));
        if (done.Report.HasOtherLods)
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(ps.RetargetOtherLods);
        if (done.Report.SnapRate is > 0f and < 0.8f)
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(ps.RetargetLowSnap);

        if (ImGui.Button(ps.RetargetClearPreview, FullWidth()))
        {
            ctx.EndPreview();
            planned = null;
            pendingPreview = null;
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(ps.RetargetGroupName);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##retargetGroup", ref groupName, 128);

        foreach (string clash in BodyRetargetWriter.ClashingGroups(ctx.Redirects, ctx.GamePath, groupName))
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(ps.RetargetClashFmt, clash));

        using (ImRaii.Disabled(busy || groupName.Trim().Length == 0))
            if (ImGui.Button(ps.RetargetSave, FullWidth()))
                StartSave(ctx);
    }

    /// <summary>What this mod already has saved, and the ways to see it in Penumbra or take the last one back.</summary>
    private void DrawSaved(in RetargetContext ctx)
    {
        if (record is not { Options.Count: > 0 } saved) return;
        var ps = Strings.Parts;

        ImGui.Spacing();
        ImGui.TextWrapped(string.Format(ps.RetargetSavedFmt, saved.Options.Count, saved.Group));
        if (ImGui.Button(ps.RetargetOpenInPenumbra, FullWidth())) penumbra.OpenToMod(ctx.ModDir);

        // Armed by a held modifier, like every other destructive button in the tab.
        var io = ImGui.GetIO();
        bool armed = io.KeyCtrl || io.KeyShift;
        using (ImRaii.Disabled(saveTask != null || !armed))
            if (ImGui.Button(ps.RetargetUndo, FullWidth()))
                StartUndo(ctx, saved);
        if (!armed && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ps.BrushRevertArmTip);
    }

    private static string Describe(BodyRetarget.Report r)
    {
        var ps = Strings.Parts;
        var lines = new List<string> { string.Format(ps.RetargetMovedFmt, r.WorstMove * 1000f) };
        if (r.Snapped > 0) lines.Add(string.Format(ps.RetargetSnappedFmt, r.Snapped, r.SnapRate));
        if (r.Pushed > 0) lines.Add(string.Format(ps.RetargetPushedFmt, r.Pushed, r.WorstPush * 1000f));
        if (r.Missed > 0) lines.Add(string.Format(ps.RetargetMissedFmt, r.Missed));
        return string.Join("\n", lines);
    }

    private static System.Numerics.Vector2 FullWidth() => new(-1, 0);

    // ── the worker half ─────────────────────────────────────────────────────

    private void StartDetect(in RetargetContext ctx)
    {
        if (detectTask != null || catalog is not { IsBody: true } snapshot) return;

        var garment = ctx.Garment;
        var slots = Slots(ctx);
        string rel = ctx.ModelRel;
        detectedFor = rel;

        detectTask = Task.Run(() =>
        {
            var rankings = new Dictionary<string, BodySizeMatch.Ranking>(StringComparer.Ordinal);
            foreach (string slot in slots)
                rankings[slot] = BodySizeMatch.Rank(garment, snapshot.For(slot), snapshot.PathOf);
            return new DetectResult(rel, rankings);
        });
    }

    /// <summary>
    /// Check one slot's chosen pair as soon as it is chosen, so a refusal appears beside the two option names that
    /// caused it rather than only when the user presses Preview.
    /// </summary>
    private void StartValidate(string slot)
    {
        refusals.Remove(slot);
        if (catalog is not { } snapshot) return;
        if (!from.TryGetValue(slot, out var source) || !to.TryGetValue(slot, out var target)) return;
        if (string.Equals(source.Rel, target.Rel, StringComparison.OrdinalIgnoreCase)) return;

        string pair = source.Rel + ">" + target.Rel;
        string sourcePath = snapshot.PathOf(source), targetPath = snapshot.PathOf(target);
        string name = SlotName(slot);

        // A newer choice replaces an older check outright; the older one's answer is dropped in Consume by its tag.
        validating[slot] = (pair, Task.Run(() =>
        {
            try
            {
                return Build(sourcePath, targetPath, name, out _, out _) ?? "";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }));
    }

    private void StartPlan(in RetargetContext ctx)
    {
        if (planTask != null || catalog is not { } snapshot) return;
        ctx.FlushPending();

        string garmentSlot = Primary(ctx);
        var chosen = Chosen(ctx).Select(s => (Slot: s, Source: snapshot.PathOf(from[s]), Target: snapshot.PathOf(to[s]),
                                              Name: SlotName(s))).ToList();
        var garment = ctx.Garment;
        var bytes = ctx.GarmentBytes;
        string key = Key(ctx);

        planTask = Task.Run(() =>
        {
            try
            {
                var pairs = new List<BodyRetarget.SlotPair>();
                foreach (var (slot, sourcePath, targetPath, name) in chosen)
                {
                    if (Build(sourcePath, targetPath, name, out var built, out var target) is { } refusal)
                        return new PlanResult(key, null, refusal);
                    pairs.Add(new BodyRetarget.SlotPair(slot, built!, target!));
                }
                return new PlanResult(key, BodyRetarget.Plan(garment, bytes, pairs, garmentSlot), "");
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Proteus] retarget: planning failed");
                return new PlanResult(key, null, ex.Message);
            }
        });
    }

    /// <summary>
    /// Read a source and target body and work out which point of one is which point of the other — vertex for vertex
    /// when they are the same mesh, by texture coordinate otherwise (see <see cref="BodyCorrespondence"/>). Null on
    /// success; otherwise the reason, worded for the user. Worker thread only.
    /// </summary>
    private static string? Build(string sourcePath, string targetPath, string name,
                                 out IBodyCorrespondence? correspondence, out ModelParts? target)
    {
        correspondence = null;
        target = null;

        var sourceBytes = File.ReadAllBytes(sourcePath);
        var targetBytes = File.ReadAllBytes(targetPath);
        var source = ModelPartReader.Read(sourceBytes);
        target = ModelPartReader.Read(targetBytes);
        if (source == null || target == null) return string.Format(Strings.Parts.RetargetUnreadableFmt, name);

        return BodyCorrespondence.TryBuild(source, Uv(sourceBytes), target, Uv(targetBytes), name,
                                           out correspondence, out string refusal)
                   ? null
                   : refusal;
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
        var slots = Chosen(ctx);
        string labelFrom = string.Join(" + ", slots.Select(s => from[s].Label));
        string labelTo = string.Join(" + ", slots.Select(s => to[s].Label));

        saveTask = Task.Run(() => new SaveResult(
            BodyRetargetWriter.Save(root, group, option, path, model, body, labelFrom, labelTo),
            BodyRetargetWriter.ReadRecord(root)));
    }

    private void StartUndo(in RetargetContext ctx, BodyRetargetWriter.Record saved)
    {
        if (saveTask != null) return;
        string root = ctx.ModRoot;
        string group = saved.Group;
        string option = saved.Options[^1].Name;
        saveTask = Task.Run(() => new SaveResult(BodyRetargetWriter.Undo(root, group, option),
                                                 BodyRetargetWriter.ReadRecord(root)));
    }

    /// <summary>The option's name: what the user will pick in Penumbra, so it says which body and which size.</summary>
    private string OptionName(in RetargetContext ctx)
    {
        string body = bodyDir != null && bodies != null && bodies.TryGetValue(bodyDir, out string? name) ? name : "Body";
        return $"{body} — {string.Join(" + ", Chosen(ctx).Select(s => to[s].Label))}";
    }

    private string Key(in RetargetContext ctx)
        => ctx.ModelRel + "|" + string.Join("|", Chosen(ctx).Select(s => $"{s}:{from[s].Rel}>{to[s].Rel}"));

    /// <summary>The framework-thread half: take up whatever finished, and do the parts only this thread may.</summary>
    private void Consume(in RetargetContext ctx)
    {
        if (pendingPreview != null && ctx.PushPreview(pendingPreview)) pendingPreview = null;

        if (bodiesTask is { IsCompleted: true } bt)
        {
            bodiesTask = null;
            if (bt.IsCompletedSuccessfully) bodies = bt.Result;
            else log.Warning(bt.Exception, "[Proteus] retarget: finding body mods failed");
        }

        if (detectTask is { IsCompleted: true } dt)
        {
            detectTask = null;
            if (!Faulted(dt, ctx) && dt.Result.ModelRel == ctx.ModelRel)
                foreach (var (slot, ranking) in dt.Result.Rankings)
                {
                    detected[slot] = ranking;
                    if (ranking.Preselect && ranking.Best is { } best && !from.ContainsKey(slot))
                    {
                        from[slot] = best.Option;
                        StartValidate(slot);
                    }
                }
        }

        foreach (string slot in validating.Where(v => v.Value.Refusal.IsCompleted).Select(v => v.Key).ToList())
        {
            var (pair, task) = validating[slot];
            validating.Remove(slot);

            // Only the check for the pair that is chosen NOW counts; a slower check for an earlier choice is noise.
            if (!from.TryGetValue(slot, out var source) || !to.TryGetValue(slot, out var target)) continue;
            if (pair != source.Rel + ">" + target.Rel) continue;

            string refusal = task.IsCompletedSuccessfully ? task.Result : task.Exception?.GetBaseException().Message ?? "";
            if (refusal.Length > 0) refusals[slot] = refusal;
            else refusals.Remove(slot);
        }

        if (planTask is { IsCompleted: true } pt)
        {
            planTask = null;
            if (Faulted(pt, ctx)) { }
            else if (pt.Result.Error.Length > 0) ctx.SetStatus(pt.Result.Error, true);
            else if (pt.Result.Planned is { } done && pt.Result.Key == Key(ctx))
            {
                planned = done;
                if (!ctx.PushPreview(done.Model)) pendingPreview = done.Model;
                ctx.SetStatus(Describe(done.Report).Replace('\n', ' '), false);
            }
        }

        if (saveTask is { IsCompleted: true } st)
        {
            saveTask = null;
            if (Faulted(st, ctx)) return;

            var (outcome, fresh) = st.Result;
            record = fresh;
            recordFor = ctx.ModRoot;
            ctx.SetStatus(outcome.Message, !outcome.Ok);
            if (!outcome.Ok) return;

            ctx.EndPreview();
            planned = null;
            pendingPreview = null;
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
}
