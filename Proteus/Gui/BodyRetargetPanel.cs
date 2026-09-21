using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    /// <param name="Held">Labels of the parts unticked in the Studio's list, which the refit leaves exactly where the
    /// author put them. The same locks the brush honours.</param>
    internal readonly record struct RetargetContext(
        string ModRoot, string ModDir, string ModelRel, string GamePath, string ModelLabel,
        ModelParts Garment, byte[] GarmentBytes, IReadOnlyList<PenumbraModMeta.Redirect> Redirects,
        Action FlushPending, Func<byte[], bool> PushPreview, Action EndPreview,
        Action<string, bool> SetStatus, Action AfterModChange, IReadOnlyCollection<string> Held);

    // ── the body, and the pair chosen per slot ──────────────────────────────

    private string? bodyDir;
    private string bodyFilter = "";
    private BodySizeCatalog? catalog;

    /// <summary>Per slot: the option the garment was built for.</summary>
    private readonly Dictionary<string, BodyOption> from = new(StringComparer.Ordinal);

    /// <summary>
    /// Per slot: the options to refit it onto, in the order they were ticked. Several for the garment's own slot — one
    /// refit, and one saved option, per size — and at most one for the others, which ride along with every one of
    /// them. Several there too would mean pairing chest sizes with leg sizes, and nothing says which goes with which.
    /// </summary>
    private readonly Dictionary<string, List<BodyOption>> to = new(StringComparer.Ordinal);

    /// <summary>Per slot and target (<see cref="PairKey"/>), why that pair cannot be used. Absent when it can, or has
    /// not been checked.</summary>
    private readonly Dictionary<string, string> refusals = new(StringComparer.Ordinal);

    /// <summary>Per slot, what the detector made of the garment.</summary>
    private readonly Dictionary<string, BodySizeMatch.Ranking> detected = new(StringComparer.Ordinal);

    /// <summary>The model the detector last ran for, so opening another model runs it again.</summary>
    private string? detectedFor;

    /// <summary>The name typed for a new group — used when <see cref="saveTo"/> is null.</summary>
    private string groupName = "";
    private string? groupNameFor;

    /// <summary>
    /// The existing group the refit is saved into, or null for a new one named <see cref="groupName"/>. Defaults to
    /// the author's group that already switches this model, so a new size sits beside the sizes it joins.
    /// </summary>
    private string? saveTo;

    /// <summary>The mod's single-choice groups, in the author's order, re-read with <see cref="record"/>.</summary>
    private List<string> singleGroups = [];

    /// <summary>
    /// Lay the garment's own body skin exactly onto the new body instead of resizing the skin it came with. On by
    /// default: the seam where garment skin meets body skin is what most often looks wrong, and this makes the two one
    /// surface. Off keeps an author's reshaping of the skin (a top that lifts the chest) at the new size.
    /// </summary>
    private bool replaceSkin = true;

    /// <summary>The model the worn body was last looked up for, so it is looked up once per model, not per frame.</summary>
    private string? wornFor;

    /// <summary>One refit per target of the garment's own slot, in the order the targets were ticked.</summary>
    private List<(BodyOption To, BodyRetarget.Planned Planned)>? planned;

    /// <summary>Which of <see cref="planned"/> is on the character.</summary>
    private int showing;

    /// <summary>How far the running plan has got, for the progress line. Written by the worker.</summary>
    private int planDone, planTotal;

    /// <summary>
    /// What <see cref="planned"/> was made from. Ticking or unticking a part changes it, and a plan made with other
    /// locks must not be saved: the preview is taken down and the refit has to be run again.
    /// </summary>
    private string plannedKey = "";

    /// <summary>A preview the live preview was too busy to take, retried each frame until it goes.</summary>
    private byte[]? pendingPreview;

    // ── worker tasks ────────────────────────────────────────────────────────

    private Task<Dictionary<string, string>>? bodiesTask;
    private Task<DetectResult>? detectTask;
    private Task<PlanResult>? planTask;
    private Task<SaveResult>? saveTask;

    /// <summary>Pair checks in flight, per <see cref="PairKey"/>, each tagged with the source it was started for.</summary>
    private readonly Dictionary<string, (string Source, Task<string> Refusal)> validating = new(StringComparer.Ordinal);

    private sealed record DetectResult(string ModelRel, Dictionary<string, BodySizeMatch.Ranking> Rankings);
    private sealed record PlanResult(string Key, List<(BodyOption To, BodyRetarget.Planned Planned)>? Planned,
                                     string Error);
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
        wornFor = null;
        planned = null;
        showing = 0;
        pendingPreview = null;
        groupNameFor = null;
        recordFor = null;
    }

    public void Draw(in RetargetContext ctx)
    {
        var ps = Strings.Parts;
        Consume(ctx);

        if (recordFor != ctx.ModRoot)
        {
            // One read per mod, not per frame, and again after a save or an undo changes what is there.
            recordFor = ctx.ModRoot;
            record = BodyRetargetWriter.ReadRecord(ctx.ModRoot);
            singleGroups = (PenumbraModMeta.TryReadGroups(ctx.ModRoot) ?? [])
                .Where(g => string.Equals(PenumbraModMeta.TypeOf(g.Group), "Single", StringComparison.OrdinalIgnoreCase))
                .Select(g => g.Name).ToList();
        }

        if (groupNameFor != ctx.ModelRel)
        {
            groupNameFor = ctx.ModelRel;
            groupName = string.Format(ps.RetargetGroupFmt, ctx.ModelLabel);
            saveTo = SwitchingGroup(ctx);
        }

        if (bodyDir == null && bodies != null) PickWornBody(ctx);

        DrawBodyPicker(ctx);
        if (catalog is not { IsBody: true })
        {
            ImGui.TextWrapped(bodies == null ? ps.RetargetFindingBodies : ps.RetargetPickBody);
            DrawSaved(ctx);
            return;
        }

        // A different model opened under the same body: the old detection was about the old model.
        if (detectedFor != ctx.ModelRel && detectTask == null) StartDetect(ctx);

        // The sizes being refitted ONTO default to the ones the character is wearing right now.
        if (wornFor != ctx.ModelRel) PresetWornTargets(ctx);

        // A part ticked or unticked since the refit ran: that plan is not what the user now asks for, so it may not be
        // saved. Taken down rather than kept, so the character does not show a refit the Save button would not write.
        if (planned != null && plannedKey != Key(ctx))
        {
            planned = null;
            pendingPreview = null;
            ctx.EndPreview();
        }

        ImGui.Separator();
        var slots = Slots(ctx);
        DrawSlot(ctx, slots[0]);

        // The other slots in a panel of their own, closed until opened: most garments need none of them, and four
        // dropdown pairs for one top bury the one that matters. The header says when any of them is taking part, so a
        // closed panel never hides a choice that changes the refit.
        if (slots.Count > 1)
        {
            int inUse = slots.Skip(1).Count(s => Targets(s).Count > 0);
            string header = (inUse > 0 ? string.Format(ps.RetargetOtherPartsInUseFmt, inUse) : ps.RetargetOtherParts)
                          + "###retargetOtherParts";
            if (ImGui.CollapsingHeader(header))
            {
                ImGui.TextDisabled(ps.RetargetSlotOptionalTip);
                foreach (string slot in slots.Skip(1))
                    DrawSlot(ctx, slot);
            }
        }

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

        ComboSearch.Box("##retargetBody", ref bodyFilter);
        bool any = false;
        foreach (var (dir, label) in bodies.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            // Mod names match anywhere, as the Studio's own mod picker does: people type fragments of a name ("lithe").
            // Word starts are for the size lists, where a single letter has to mean a size.
            if (bodyFilter.Length > 0 && !label.Contains(bodyFilter, StringComparison.OrdinalIgnoreCase)
                && !dir.Contains(bodyFilter, StringComparison.OrdinalIgnoreCase)) continue;
            any = true;
            if (!ImGui.Selectable(label, dir == bodyDir)) continue;

            bodyDir = dir;
            catalog = BodyRoot(dir) is { } root ? BodySizeCatalog.Read(root) : null;
            Clear();
        }
        if (!any) ImGui.TextDisabled(ps.NoMatches);
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

    // ── what the character is wearing ───────────────────────────────────────
    //
    // Both of these ask Penumbra to RESOLVE a body model's game path through the player's own collection, and match the
    // file it answers with. That is the one reliable way to know which option is selected: option names are no good,
    // because Neolithe has eight options all called "SFW M" and only their position tells them apart, while the file
    // each one points at is unique. It also answers the question actually being asked — which body is being drawn —
    // rather than which options happen to be ticked in some group.

    /// <summary>
    /// Choose, once, the body mod the character is wearing: the installed body mod that supplies the body model for the
    /// garment's own slot.
    /// </summary>
    private void PickWornBody(in RetargetContext ctx)
    {
        if (wornBodyTried || bodies == null) return;
        wornBodyTried = true;

        string? modsRoot = penumbra.GetModDirectory();
        if (modsRoot == null) return;
        string slot = BodySizeCatalog.SlotOf(ctx.GamePath) ?? "_top";
        string race = RaceCode.Match(ctx.GamePath) is { Success: true } m ? m.Groups[1].Value : "0201";
        if (penumbra.ResolvePlayer($"chara/equipment/e0000/model/c{race}e0000{slot}.mdl") is not { } resolved) return;

        string full = Path.GetFullPath(resolved);
        foreach (string dir in bodies.Keys)
        {
            string root = Path.GetFullPath(Path.Combine(modsRoot, dir)) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            bodyDir = dir;
            catalog = BodySizeCatalog.Read(Path.Combine(modsRoot, dir));
            return;
        }
    }

    /// <summary>Only ever tried once per session: the user may well choose another body mod on purpose.</summary>
    private bool wornBodyTried;

    private static readonly System.Text.RegularExpressions.Regex RaceCode = new(@"/c(\d{4})e\d{4}_",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Default the garment's own slot's target to the option the character is wearing, leaving a choice already made.
    /// <para/>
    /// Only the garment's own slot. The others are optional, and a target preset there — hands, feet, for a top that
    /// reaches neither — is a target with no source, which holds the refit back until the user empties a slot they
    /// never touched.
    /// </summary>
    private void PresetWornTargets(in RetargetContext ctx)
    {
        wornFor = ctx.ModelRel;
        if (catalog is not { } snapshot) return;
        string slot = Primary(ctx);
        if (to.ContainsKey(slot) || WornOption(snapshot, slot) is not { } worn) return;
        to[slot] = [worn];
        StartValidate(slot);
    }

    /// <summary>The option of this slot whose file the player's collection resolves the body model to.</summary>
    private BodyOption? WornOption(BodySizeCatalog snapshot, string slot)
    {
        var options = snapshot.For(slot);
        foreach (string gamePath in options.Select(o => o.GamePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (penumbra.ResolvePlayer(gamePath) is not { } resolved) continue;
            string full = Path.GetFullPath(resolved);
            foreach (var option in options)
                if (string.Equals(option.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFullPath(snapshot.PathOf(option)), full, StringComparison.OrdinalIgnoreCase))
                    return option;
        }
        return null;
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
        => Slots(ctx).Where(s => from.ContainsKey(s) && Targets(s).Count > 0).ToList();

    /// <summary>A slot's targets; empty when none is ticked.</summary>
    private IReadOnlyList<BodyOption> Targets(string slot)
        => to.TryGetValue(slot, out var list) ? list : [];

    /// <summary>What a pair check and its refusal are filed under: a slot and one of its targets.</summary>
    private static string PairKey(string slot, BodyOption target) => slot + ">" + target.Rel;

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

        from.TryGetValue(slot, out var source);
        if (DrawOptionCombo($"##retargetFrom{slot}", ps.RetargetFrom, options,
                            source == null ? [] : [source], many: false) is { } pickedFrom)
        {
            from[slot] = pickedFrom;
            DropPlan(ctx);
            StartValidate(slot);
        }
        DrawConfidence(slot);

        // One size at a time for now. The rest of the panel still handles several (the plan, the preview picker, the
        // batch save), so turning this back on is this line.
        bool many = false;
        var targets = Targets(slot);
        if (DrawOptionCombo($"##retargetTo{slot}", many ? ps.RetargetToMany : ps.RetargetTo, options, targets,
                            many) is { } pickedTo)
        {
            // Clicking a ticked size unticks it, in either list — which is also the only way to take an optional
            // slot back out once something has been chosen for it.
            var list = to.TryGetValue(slot, out var had) ? had : to[slot] = [];
            if (list.Remove(pickedTo)) { }
            else if (many) list.Add(pickedTo);
            else { list.Clear(); list.Add(pickedTo); }
            if (list.Count == 0) to.Remove(slot);
            DropPlan(ctx);
            StartValidate(slot);
        }

        if (targets.Any(t => validating.ContainsKey(PairKey(slot, t))))
            ImGui.TextDisabled(ps.RetargetCheckingPair);
        foreach (var target in targets)
        {
            if (!refusals.TryGetValue(PairKey(slot, target), out string? why)) continue;
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(targets.Count > 1 ? $"{target.Label}: {why}" : why);
        }

        ImGui.Spacing();
    }

    /// <summary>
    /// A dropdown of a slot's options. Returns the option clicked this frame, if any; the caller decides what a click
    /// means. With <paramref name="many"/> the list stays open, so several sizes can be ticked in one go.
    /// </summary>
    private BodyOption? DrawOptionCombo(string id, string label, IReadOnlyList<BodyOption> options,
                                        IReadOnlyList<BodyOption> chosen, bool many)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);

        string current = chosen.Count == 0 ? Strings.Parts.RetargetChoose
                       : string.Join(", ", chosen.Select(o => o.Label));
        using var combo = ImRaii.Combo(id, current, ImGuiComboFlags.HeightLarge);
        if (!combo) return null;

        string filter = searches.GetValueOrDefault(id, "");
        ComboSearch.Box(id, ref filter);
        searches[id] = filter;

        var flags = many ? ImGuiSelectableFlags.DontClosePopups : ImGuiSelectableFlags.None;
        BodyOption? picked = null;
        string? group = null;
        bool any = false;
        foreach (var option in options)
        {
            if (!ComboSearch.Matches(filter, option.FullLabel)) continue;
            any = true;
            if (option.Group != group)
            {
                group = option.Group;
                ImGui.Separator();
                ImGui.TextDisabled(group);
            }
            if (ImGui.Selectable(option.Label + "##" + option.Rel, chosen.Contains(option), flags)) picked = option;
        }
        if (!any) ImGui.TextDisabled(Strings.Parts.NoMatches);
        return picked;
    }

    /// <summary>The choices changed: a refit made from the old ones must not be shown or saved.</summary>
    private void DropPlan(in RetargetContext ctx)
    {
        if (planned == null) return;
        planned = null;
        pendingPreview = null;
        ctx.EndPreview();
    }

    /// <summary>What has been typed into each dropdown's search box, by the dropdown's id.</summary>
    private readonly Dictionary<string, string> searches = new(StringComparer.Ordinal);
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
        bool sourceMissing = slots.Any(s => Targets(s).Count > 0 && !from.ContainsKey(s));
        bool refused = Chosen(ctx).Any(s => Targets(s).Any(t => refusals.ContainsKey(PairKey(s, t))));
        bool ready = from.ContainsKey(primary) && Targets(primary).Count > 0 && !sourceMissing && !refused;

        if (detectTask != null) ImGui.TextUnformatted(ps.RetargetChecking);

        ImGui.Checkbox(ps.RetargetReplaceSkin, ref replaceSkin);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ps.RetargetReplaceSkinTip);

        using (ImRaii.Disabled(busy || !ready))
            if (ImGui.Button(ps.RetargetPreview, FullWidth()))
                StartPlan(ctx);

        // A greyed-out button says why, or the user is left guessing which of several dropdowns is holding it.
        if (!busy && !ready)
        {
            string why = !from.ContainsKey(primary) ? string.Format(ps.RetargetNeedFromFmt, SlotName(primary))
                       : Targets(primary).Count == 0 ? string.Format(ps.RetargetNeedToFmt, SlotName(primary))
                       : sourceMissing ? string.Format(ps.RetargetNeedFromFmt,
                                                       SlotName(slots.First(s => Targets(s).Count > 0 && !from.ContainsKey(s))))
                       : ps.RetargetRefusedHold;
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(why);
        }

        if (planTask != null)
            ImGui.TextUnformatted(Volatile.Read(ref planTotal) > 1
                                      ? string.Format(ps.RetargetWorkingFmt, Volatile.Read(ref planDone) + 1,
                                                      Volatile.Read(ref planTotal))
                                      : ps.RetargetWorking);
        if (planned is not { Count: > 0 } all) return;

        // Several sizes refitted: one is on the character at a time, and choosing another puts it there.
        if (all.Count > 1)
        {
            ImGui.TextUnformatted(ps.RetargetShowing);
            ImGui.SetNextItemWidth(-1);
            using (var combo = ImRaii.Combo("##retargetShowing", all[showing].To.Label))
                if (combo)
                    for (int i = 0; i < all.Count; i++)
                        if (ImGui.Selectable(all[i].To.Label + "##show" + i, i == showing) && i != showing)
                        {
                            showing = i;
                            if (!ctx.PushPreview(all[i].Planned.Model)) pendingPreview = all[i].Planned.Model;
                        }
        }

        var done = all[showing].Planned;
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
        DrawSaveTo(ctx);

        using (ImRaii.Disabled(busy || Destination().Length == 0))
            if (ImGui.Button(all.Count > 1 ? string.Format(ps.RetargetSaveManyFmt, all.Count) : ps.RetargetSave,
                             FullWidth()))
                StartSave(ctx);
    }

    /// <summary>
    /// Where the save goes: an existing single-choice group — the author's, or one an earlier save made — or a new
    /// group. Multi-choice groups are not offered: two sizes of one model ticked together would fight over the file.
    /// </summary>
    private void DrawSaveTo(in RetargetContext ctx)
    {
        var ps = Strings.Parts;
        var own = record?.OwnGroups.ToHashSet(StringComparer.OrdinalIgnoreCase)
                  ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ImGui.TextUnformatted(ps.RetargetSaveTo);
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##retargetSaveTo", saveTo ?? ps.RetargetNewGroup))
            if (combo)
            {
                if (ImGui.Selectable(ps.RetargetNewGroup + "##new", saveTo == null)) saveTo = null;
                foreach (string group in singleGroups)
                    if (ImGui.Selectable((own.Contains(group) ? group + "  " + ps.RetargetMadeHere : group) + "##g_" + group,
                                         saveTo == group))
                        saveTo = group;
            }

        if (saveTo == null)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##retargetGroup", ref groupName, 128);
        }

        // Only a group of our own competes with the author's for the file. Added to the author's group, a size is one
        // more choice beside the others, and there is nothing to outrank.
        string destination = Destination();
        if (saveTo == null || own.Contains(destination))
            foreach (string clash in BodyRetargetWriter.ClashingGroups(ctx.Redirects, ctx.GamePath, destination))
                using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                    ImGui.TextWrapped(string.Format(ps.RetargetClashFmt, clash));
    }

    /// <summary>The group the save writes to.</summary>
    private string Destination() => saveTo ?? groupName.Trim();

    /// <summary>
    /// The author's single-choice group that already switches this model — the size group a new size belongs in — or
    /// null when there is none and the refit gets a group of its own.
    /// </summary>
    private string? SwitchingGroup(in RetargetContext ctx)
    {
        var own = record?.OwnGroups.ToHashSet(StringComparer.OrdinalIgnoreCase)
                  ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ctx.Redirects)
        {
            if (!string.Equals(r.GamePath, ctx.GamePath, StringComparison.OrdinalIgnoreCase)) continue;
            int split = r.Source.IndexOf(" / ", StringComparison.Ordinal);
            if (split <= 0) continue;
            string group = r.Source[..split];
            if (!own.Contains(group) && singleGroups.Contains(group, StringComparer.OrdinalIgnoreCase)) return group;
        }
        return null;
    }

    /// <summary>What this mod already has saved, and the ways to see it in Penumbra or take the last one back.</summary>
    private void DrawSaved(in RetargetContext ctx)
    {
        if (record is not { Options.Count: > 0 } saved) return;
        var ps = Strings.Parts;

        ImGui.Spacing();
        ImGui.TextWrapped(string.Format(ps.RetargetSavedFmt, saved.Options.Count, saved.GroupOf(saved.Options[^1])));
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
        if (r.Held > 0) lines.Add(string.Format(ps.RetargetHeldFmt, r.Held));
        if (r.Swap is { } swap)
        {
            lines.Add(string.Format(ps.RetargetSwappedFmt, swap.Removed, swap.Added));
            if (swap.Kept > 0) lines.Add(string.Format(ps.RetargetSwapKeptFmt, swap.Kept));
            if (swap.LostShapes > 0) lines.Add(string.Format(ps.RetargetSwapShapesFmt, swap.LostShapes));
        }
        else if (r.Laid > 0) lines.Add(string.Format(ps.RetargetLaidFmt, r.Laid));
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
        // Every earlier answer for this slot goes, including those for sizes no longer ticked.
        foreach (string key in refusals.Keys.Where(k => k.StartsWith(slot + ">", StringComparison.Ordinal)).ToList())
            refusals.Remove(key);
        foreach (string key in validating.Keys.Where(k => k.StartsWith(slot + ">", StringComparison.Ordinal)).ToList())
            validating.Remove(key);

        if (catalog is not { } snapshot || !from.TryGetValue(slot, out var source)) return;
        string sourcePath = snapshot.PathOf(source);
        string name = SlotName(slot);

        foreach (var target in Targets(slot))
        {
            if (string.Equals(source.Rel, target.Rel, StringComparison.OrdinalIgnoreCase)) continue;
            string targetPath = snapshot.PathOf(target);

            // A newer choice replaces an older check outright; the older one's answer is dropped in Consume by its tag.
            validating[PairKey(slot, target)] = (source.Rel, Task.Run(() =>
            {
                try
                {
                    return Build(sourcePath, targetPath, name, out _, out _, out _) ?? "";
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            }));
        }
    }

    private void StartPlan(in RetargetContext ctx)
    {
        if (planTask != null || catalog is not { } snapshot) return;
        ctx.FlushPending();

        string garmentSlot = Primary(ctx);
        string garmentSource = snapshot.PathOf(from[garmentSlot]);
        var targets = Targets(garmentSlot).Select(t => (Option: t, Path: snapshot.PathOf(t))).ToList();
        var others = Chosen(ctx).Where(s => s != garmentSlot)
                                .Select(s => (Slot: s, Source: snapshot.PathOf(from[s]),
                                              Target: snapshot.PathOf(Targets(s)[0]), Name: SlotName(s)))
                                .ToList();
        string garmentName = SlotName(garmentSlot);
        var garment = ctx.Garment;
        var bytes = ctx.GarmentBytes;
        var heldLabels = new HashSet<string>(ctx.Held, StringComparer.Ordinal);
        bool layOnBody = replaceSkin;
        string key = Key(ctx);
        planDone = 0;
        planTotal = targets.Count;

        planTask = Task.Run(() =>
        {
            try
            {
                // The other slots are the same pair for every size, so each is built once and shared.
                var shared = new List<BodyRetarget.SlotPair>();
                foreach (var (slot, sourcePath, targetPath, name) in others)
                {
                    if (Build(sourcePath, targetPath, name, out var built, out var target, out var body) is { } refusal)
                        return new PlanResult(key, null, refusal);
                    shared.Add(new BodyRetarget.SlotPair(slot, built!, target!, body));
                }

                // A submesh's triangles already include every island of it, so the labels alone are enough — the
                // brush's own ApplyLocks reads them the same way.
                var held = new HashSet<int>();
                foreach (var part in garment.Parts)
                    if (heldLabels.Contains(part.Label))
                        held.UnionWith(part.Triangles);

                var results = new List<(BodyOption, BodyRetarget.Planned)>();
                foreach (var (option, targetPath) in targets)
                {
                    if (Build(garmentSource, targetPath, garmentName, out var built, out var target, out var body) is { } refusal)
                        return new PlanResult(key, null, targets.Count > 1 ? $"{option.Label}: {refusal}" : refusal);

                    var pairs = new List<BodyRetarget.SlotPair> { new(garmentSlot, built!, target!, body) };
                    pairs.AddRange(shared);
                    results.Add((option, BodyRetarget.Plan(garment, bytes, pairs, garmentSlot, held: held,
                                                           replaceSkin: layOnBody)));
                    Interlocked.Increment(ref planDone);
                }
                return new PlanResult(key, results, "");
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
    /// <param name="targetBytes">The target body's file, which swapping the garment's skin copies the body's skin
    /// out of.</param>
    private static string? Build(string sourcePath, string targetPath, string name,
                                 out IBodyCorrespondence? correspondence, out ModelParts? target, out byte[] targetBytes)
    {
        correspondence = null;
        target = null;

        var sourceBytes = File.ReadAllBytes(sourcePath);
        targetBytes = File.ReadAllBytes(targetPath);
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
        if (saveTask != null || planned is not { Count: > 0 } all) return;

        string root = ctx.ModRoot;
        string group = Destination();
        string path = ctx.GamePath;
        string body = bodyDir ?? "";
        var slots = Chosen(ctx);
        string labelFrom = string.Join(" + ", slots.Select(s => from[s].Label));
        var refits = new List<BodyRetargetWriter.Refit>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, plan) in all)
        {
            // Two sizes can carry the same label under different headings; each still needs an option of its own.
            string option = OptionName(ctx, target);
            for (int n = 2; !used.Add(option); n++) option = $"{OptionName(ctx, target)} ({n})";
            refits.Add(new BodyRetargetWriter.Refit(option, plan.Model, ToLabel(ctx, target)));
        }

        saveTask = Task.Run(() => new SaveResult(
            BodyRetargetWriter.Save(root, group, path, body, labelFrom, refits),
            BodyRetargetWriter.ReadRecord(root)));
    }

    private void StartUndo(in RetargetContext ctx, BodyRetargetWriter.Record saved)
    {
        if (saveTask != null) return;
        string root = ctx.ModRoot;
        string group = saved.GroupOf(saved.Options[^1]);
        string option = saved.Options[^1].Name;
        saveTask = Task.Run(() => new SaveResult(BodyRetargetWriter.Undo(root, group, option),
                                                 BodyRetargetWriter.ReadRecord(root)));
    }

    /// <summary>
    /// The option's name for one refitted size: what the user will pick in Penumbra, so it says which body and which
    /// size.
    /// </summary>
    private string OptionName(in RetargetContext ctx, BodyOption target)
    {
        string body = bodyDir != null && bodies != null && bodies.TryGetValue(bodyDir, out string? name) ? name : "Body";
        return $"{body} — {ToLabel(ctx, target)}";
    }

    /// <summary>One refit's sizes: the garment slot's <paramref name="target"/>, then each other slot's.</summary>
    private string ToLabel(in RetargetContext ctx, BodyOption target)
    {
        string primary = Primary(ctx);
        return string.Join(" + ", Chosen(ctx).Select(s => s == primary ? target.Label : Targets(s)[0].Label));
    }

    /// <summary>What a plan was made from: the model, each chosen pair, and which parts were held. A plan whose key no
    /// longer matches is stale — see <see cref="plannedKey"/>.</summary>
    private string Key(in RetargetContext ctx)
        => ctx.ModelRel + "|"
         + string.Join("|", Chosen(ctx).Select(s => $"{s}:{from[s].Rel}>{string.Join(",", Targets(s).Select(t => t.Rel))}"))
         + "|held:" + string.Join(",", ctx.Held.OrderBy(h => h, StringComparer.Ordinal))
         + (replaceSkin ? "|lay" : "");

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

                    // The best guess is chosen even when it is only a guess — the line under the dropdown says how
                    // sure it is, and changing it is one click. Only a ranking with no evidence behind it at all (no
                    // body mesh, or none reaching this slot and no cloth either) leaves the choice empty.
                    bool evidence = ranking.Confidence is not (BodySizeMatch.Confidence.NoBodyMesh
                                                               or BodySizeMatch.Confidence.TooLittle);
                    if (evidence && ranking.Best is { } best && !from.ContainsKey(slot))
                    {
                        from[slot] = best.Option;
                        StartValidate(slot);
                    }
                }
        }

        foreach (string key in validating.Where(v => v.Value.Refusal.IsCompleted).Select(v => v.Key).ToList())
        {
            var (sourceRel, task) = validating[key];
            validating.Remove(key);

            // Only the check for the pair that is chosen NOW counts; a slower check for an earlier choice is noise.
            string slot = key[..key.IndexOf('>')];
            if (!from.TryGetValue(slot, out var source) || source.Rel != sourceRel) continue;
            if (!Targets(slot).Any(t => PairKey(slot, t) == key)) continue;

            string refusal = task.IsCompletedSuccessfully ? task.Result : task.Exception?.GetBaseException().Message ?? "";
            if (refusal.Length > 0) refusals[key] = refusal;
            else refusals.Remove(key);
        }

        if (planTask is { IsCompleted: true } pt)
        {
            planTask = null;
            if (Faulted(pt, ctx)) { }
            else if (pt.Result.Error.Length > 0) ctx.SetStatus(pt.Result.Error, true);
            else if (pt.Result.Planned is { Count: > 0 } done && pt.Result.Key == Key(ctx))
            {
                planned = done;
                plannedKey = pt.Result.Key;
                showing = 0;
                var first = done[0].Planned;
                if (!ctx.PushPreview(first.Model)) pendingPreview = first.Model;
                ctx.SetStatus(Describe(first.Report).Replace('\n', ' '), false);
            }
        }

        if (saveTask is { IsCompleted: true } st)
        {
            saveTask = null;
            if (Faulted(st, ctx)) return;

            var (outcome, _) = st.Result;
            recordFor = null;   // the record and the group list are both re-read next frame
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
