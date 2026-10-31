using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Proteus.Interop;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Body refit section of Settings: the switch, and per Penumbra collection the body and sizes gear is refitted
/// onto as it is put on.
/// <para/>
/// A view and nothing more. Noticing gear, reading bodies and refitting all belong to <see cref="AutoRefitWatcher"/>,
/// which keeps doing them while this window is shut.
/// </summary>
internal sealed class AutoRefitPanel(AutoRefitWatcher watcher, PenumbraBridge penumbra, Configuration config)
{
    /// <summary>Penumbra's collections, re-asked now and then rather than every frame.</summary>
    private Dictionary<Guid, string>? collections;
    private (Guid Id, string Name)? currentCollection;
    private long collectionsAt;

    /// <summary>A collection picked from the list other than the active one; null follows the active collection.</summary>
    private Guid? picked;

    /// <summary>The active collection when <see cref="picked"/> was chosen. Once the player moves to another, the pick
    /// is dropped and the panel follows them there.</summary>
    private Guid? pickedUnder;

    /// <summary>The collection being edited: the player's active one, unless another was picked while it was active.</summary>
    private Guid? Editing => picked ?? currentCollection?.Id;

    /// <summary>The player's race, which the size lists are filtered by — re-read now and then.</summary>
    private string? race;
    private long raceAt;

    /// <summary>Each row's body and size searches, by slot.</summary>
    private readonly Dictionary<string, string> bodyFilters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sizeFilters = new(StringComparer.Ordinal);
    private bool askedForBodies;

    private const long RefreshMs = 2000;

    public void Draw()
    {
        var s = Strings.AutoRefit;
        ImGui.TextWrapped(s.Intro);
        ImGui.Spacing();

        bool on = config.AutoRefitEnabled;
        if (ImGui.Checkbox(s.Enable, ref on))
        {
            config.AutoRefitEnabled = on;
            config.Save();
            if (on) watcher.RefreshBodies();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.EnableTip);
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(s.EnableTip);

        using (ImRaii.Disabled(!on))
        {
            bool vanilla = config.AutoRefitVanilla;
            if (ImGui.Checkbox(s.Vanilla, ref vanilla))
            {
                config.AutoRefitVanilla = vanilla;
                config.Save();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(s.VanillaTip);

        using (ImRaii.Disabled(!on))
        {
            bool piercings = config.AutoRefitPiercings;
            if (ImGui.Checkbox(s.Piercings, ref piercings))
            {
                config.AutoRefitPiercings = piercings;
                config.Save();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(s.PiercingsTip);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        RefreshIfDue();
        if (collections == null)
        {
            ImGui.TextDisabled(s.NoCollections);
            return;
        }
        DrawCollectionCombo();
        if (Editing is not { } collection) return;

        string key = collection.ToString("D");
        config.AutoRefitByCollection.TryGetValue(key, out var pref);

        if (!askedForBodies)
        {
            askedForBodies = true;
            watcher.RefreshBodies();
        }
        var bodies = watcher.Index.Snapshot;
        DrawParts(bodies, pref, key, collection);

        ImGui.Spacing();
        using (ImRaii.Disabled(!CanRunNow(on, pref, watcher.Current.Busy)))
            if (ImGui.Button(s.RunNow))
                watcher.RunNow();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(s.RunNowTip);

        if (pref != null)
        {
            ImGui.SameLine();
            if (ImGui.Button(s.Clear))
            {
                config.AutoRefitByCollection.Remove(key);
                watcher.NotePrefilled(key, []);
                config.Save();
            }
        }

        // What is worn now, in the collection worn in — whichever one is shown above. Armed by a held modifier, as every
        // destructive button in Proteus is: there are no confirmation dialogs.
        ImGui.SameLine();
        bool armed = ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyShift;
        using (ImRaii.Disabled(!armed || watcher.Undoing))
            if (ImGui.Button(s.UndoWorn))
                watcher.UndoWorn();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(s.UndoWornTip);

        var view = watcher.Current;
        if (view.Busy) ImGui.TextDisabled(watcher.Undoing ? s.UndoWorking : s.Working);
        if (view.Message.Length > 0)
        {
            ImGui.Spacing();
            if (view.Failed)
                using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                    ImGui.TextWrapped(view.Message);
            else
                ImGui.TextWrapped(view.Message);
        }
    }

    private void RefreshIfDue()
    {
        long now = Environment.TickCount64;
        if (collections != null && now - collectionsAt < RefreshMs) return;
        collectionsAt = now;
        collections = penumbra.GetCollections();
        currentCollection = penumbra.GetPlayerCollection();
        // Unknown for a moment while zoning: keep the pick until the player is back and their collection can be told.
        if (picked != null && currentCollection is { } active && active.Id != pickedUnder) picked = null;

        if (race == null || now - raceAt >= RefreshMs)
        {
            raceAt = now;
            if (LiveCharacter.PlayerSkeleton(Plugin.ObjectTable) is { } skeleton) race = $"{skeleton.GenderRace:D4}";
        }
    }

    private string CollectionLabel(Guid id)
    {
        string name = collections != null && collections.TryGetValue(id, out var n) ? n
                    : config.AutoRefitByCollection.TryGetValue(id.ToString("D"), out var p) ? p.CollectionName
                    : id.ToString("D");
        return currentCollection?.Id == id ? string.Format(Strings.AutoRefit.CurrentFmt, name) : name;
    }

    private void DrawCollectionCombo()
    {
        var s = Strings.AutoRefit;
        ImGui.TextUnformatted(s.Collection);
        ImGui.SetNextItemWidth(-1);
        var editing = Editing;
        using var combo = ImRaii.Combo("##autoRefitCollection", editing is { } e ? CollectionLabel(e) : s.Choose);
        if (!combo) return;

        foreach (var (id, _) in collections!.OrderBy(c => c.Key == currentCollection?.Id ? 0 : 1)
                                            .ThenBy(c => c.Value, StringComparer.OrdinalIgnoreCase))
            if (ImGui.Selectable(CollectionLabel(id) + "##" + id.ToString("N"), editing == id))
            {
                // Picking the active collection goes back to following it.
                picked = id == currentCollection?.Id ? null : id;
                pickedUnder = currentCollection?.Id;
            }
    }

    // ── a body and a size per part ────────────────────────────────────────────

    /// <summary>One row of the parts table, as <see cref="Rows"/> works it out.</summary>
    /// <param name="BodyDir">The body chosen for the part, by folder; empty for none.</param>
    /// <param name="Body">That body installed; null when none is chosen or it is gone.</param>
    /// <param name="NoSizesForSex">The body has sizes of the part, none of them for the player's sex.</param>
    /// <param name="Options">The part's sizes at the player's race.</param>
    /// <param name="Shown">The size the refit uses: the one chosen, or for hands and feet the matched one.</param>
    /// <param name="Matched">Hands or feet with no size of their own, following the chest or legs.</param>
    /// <param name="Basis">What a matched size was found by (see <see cref="AutoRefitTargets.Matched"/>).</param>
    internal sealed record PartRow(string Slot, string BodyDir, BodyModIndex.Entry? Body, bool NoSizesForSex,
                                   IReadOnlyList<BodyOption> Options, BodyOption? Shown, bool Matched,
                                   AutoRefitTargets.Basis Basis)
    {
        /// <summary>A body is chosen for the part and is not installed.</summary>
        public bool Missing => BodyDir.Length > 0 && Body == null;
    }

    /// <summary>The four rows — chest, legs, hands, feet — each from its own body, for a preference (null for none).</summary>
    internal static List<PartRow> Rows(AutoRefitPreference? pref, Func<string, BodyModIndex.Entry?> find, string? race)
    {
        var targets = new AutoRefitTargets(pref ?? new AutoRefitPreference(), find, race);
        var rows = new List<PartRow>();
        foreach (string slot in AutoRefitDecisions.Slots)
        {
            var body = targets.Body(slot);
            var options = targets.Options(slot);
            bool noSex = body != null && race != null && options.Count == 0 && body.Catalog.For(slot, null).Count > 0;
            bool matched = slot is "_glv" or "_sho" && pref?.SizeOf(slot) == null;
            var basis = AutoRefitTargets.Basis.Anchor;
            var shown = matched
                ? targets.Anchor is { } anchor ? AutoRefitTargets.Matched(options, anchor, null, out basis) : null
                : targets.Chosen(slot);
            rows.Add(new PartRow(slot, targets.DirOf(slot), body, noSex, options, shown, matched, basis));
        }
        return rows;
    }

    /// <summary>The body mods a part's list offers: only those that have it — a hands-only mod under Hands alone.</summary>
    internal static List<BodyModIndex.Entry> BodiesFor(string slot, IReadOnlyList<BodyModIndex.Entry> bodies)
        => bodies.Where(b => b.Catalog.For(slot, null).Count > 0).ToList();

    /// <summary>"Refit what I'm wearing now" asks for the feature on, a body for some part, and nothing running.</summary>
    internal static bool CanRunNow(bool enabled, AutoRefitPreference? pref, bool busy)
        => enabled && pref?.HasAnyBody() == true && !busy;

    /// <summary>
    /// What a size column shows. A matched hand or foot says so, with the size the refit takes — or, where that is the
    /// size worn, which Settings cannot read, that it is the worn one, else the size named.
    /// </summary>
    internal static string SizeLabel(PartRow row)
    {
        var s = Strings.AutoRefit;
        if (!row.Matched) return row.Shown?.Label ?? s.Choose;
        if (row.Shown == null) return string.Format(s.MatchedFmt, "—");
        return row.Basis == AutoRefitTargets.Basis.Anchor
            ? string.Format(s.MatchedFmt, row.Shown.Label)
            : string.Format(s.MatchedWornFmt, row.Shown.Label);
    }

    private static string PartName(string slot) => AutoRefitWatcher.SlotName(slot);

    /// <summary>
    /// What Settings says of parts on both sides of the Rue line (see <see cref="AutoRefitTargets.RueSides"/>): the parts
    /// on each side, that gear reaching both is refitted on its own part's side only, the shoes or gloves that may be
    /// left as they are, and the button that puts one body on every part.
    /// </summary>
    internal static string RueSplitText(AutoRefitTargets.RueSplit split, Func<string, string> partName)
    {
        var s = Strings.AutoRefit;
        string text = string.Format(s.RueSplitFmt, string.Join(", ", split.Rue.Select(partName)),
                                    string.Join(", ", split.Other.Select(partName)), s.ClearLabel);
        if (split.ShoesLeft) text += " " + s.RueSplitShoes;
        if (split.GlovesLeft) text += " " + s.RueSplitGloves;
        return text;
    }

    private void DrawParts(IReadOnlyList<BodyModIndex.Entry>? bodies, AutoRefitPreference? pref, string key, Guid collection)
    {
        var s = Strings.AutoRefit;
        ImGui.TextWrapped(s.PartsHint);
        var rows = Rows(pref, watcher.Index.Find, race);
        if (ImGui.BeginTable("##autoRefitParts", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            // Column ids, not display text, so they stay English.
            ImGui.TableSetupColumn("Part", ImGuiTableColumnFlags.WidthFixed, ProteusStyle.S(70f));
            // Explicit weights: unweighted, both share by content width, and a full-width combo has none, so Size
            // shrank to its arrow.
            ImGui.TableSetupColumn("Body", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            ImGui.TableNextColumn(); ImGui.TableHeader(s.Part);
            ImGui.TableNextColumn(); ImGui.TableHeader(s.Body);
            ImGui.TableNextColumn(); ImGui.TableHeader(s.Size);
            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(PartName(row.Slot));
                ImGui.TableNextColumn();
                DrawBodyCombo(row, bodies, pref, key, collection);
                ImGui.TableNextColumn();
                DrawSizeCombo(row, pref, key);
            }
            ImGui.EndTable();
        }

        if (pref?.HasAnyBody() != true)
            ImGui.TextDisabled(s.NoBodyChosen);
        if (bodies != null)
            foreach (var row in rows)
            {
                if (row.Missing)
                    using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                        ImGui.TextWrapped(PartName(row.Slot) + ": " + string.Format(s.BodyMissingFmt, row.BodyDir));
                else if (row.NoSizesForSex && race != null)
                {
                    string sex = BodySizeCatalog.IsMaleRace(race) ? Strings.Parts.RetargetMale : Strings.Parts.RetargetFemale;
                    using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                        ImGui.TextWrapped(PartName(row.Slot) + ": "
                                        + string.Format(Strings.Parts.RetargetNoBodiesForSexFmt, row.Body!.Name, sex));
                }
            }
        // Each part keeps its own pick across the Rue line, which the refit itself does not cross: said, so a split the
        // player did not mean is seen, with the way to one body on every part.
        if (pref != null && new AutoRefitTargets(pref, watcher.Index.Find, race).RueSides() is { } split)
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(RueSplitText(split, PartName));
        if (pref is { PendingFill.Count: > 0 })
            ImGui.TextDisabled(s.SizesPending);
        if (watcher.PrefilledIn(key) is { Count: > 0 } first)
            ImGui.TextWrapped(string.Format(s.PrefilledFmt, string.Join(", ", first.Select(PartName))));
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
            ImGui.TextWrapped(s.EndsKept);
    }

    private void DrawBodyCombo(PartRow row, IReadOnlyList<BodyModIndex.Entry>? bodies, AutoRefitPreference? pref,
                               string key, Guid collection)
    {
        var s = Strings.AutoRefit;
        string id = "##autoRefitBody" + row.Slot;
        ImGui.SetNextItemWidth(-1);
        string shown = row.BodyDir.Length > 0 ? row.Body?.Name ?? row.BodyDir : s.Choose;
        using var combo = ImRaii.Combo(id, shown, ImGuiComboFlags.HeightLarge);
        if (!combo) return;

        // Opening the list looks again, so a body mod installed since appears.
        if (ImGui.IsWindowAppearing()) watcher.RefreshBodies();
        if (bodies == null)
        {
            ImGui.TextDisabled(s.FindingBodies);
            return;
        }
        var offered = BodiesFor(row.Slot, bodies);
        if (offered.Count == 0)
        {
            ImGui.TextDisabled(s.NoBodies);
            return;
        }

        string filter = bodyFilters.GetValueOrDefault(row.Slot, "");
        ComboSearch.Box(id, ref filter);
        bodyFilters[row.Slot] = filter;
        bool any = false;
        foreach (var body in offered)
        {
            if (filter.Length > 0 && !body.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !body.Dir.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            any = true;
            if (!ImGui.Selectable(body.Name + id + body.Dir,
                                  string.Equals(row.BodyDir, body.Dir, StringComparison.OrdinalIgnoreCase)))
                continue;

            var chosen = pref ?? new AutoRefitPreference();
            // What is worn can be read only in the collection being worn; elsewhere a first pick takes first sizes.
            var first = AutoRefitDecisions.ChooseBody(chosen, row.Slot, body, race,
                                                      canReadWorn: collection == currentCollection?.Id);
            if (first.Count > 0) watcher.NotePrefilled(key, first);
            chosen.CollectionName = collections != null && collections.TryGetValue(collection, out var name) ? name : "";
            config.AutoRefitByCollection[key] = chosen;
            config.Save();
        }
        if (!any) ImGui.TextDisabled(Strings.Parts.NoMatches);
    }

    /// <summary>A dropdown of one part's sizes, grouped as the author grouped them; hands and feet start with "Match".</summary>
    private void DrawSizeCombo(PartRow row, AutoRefitPreference? pref, string key)
    {
        if (pref == null || row.Body == null || row.Options.Count == 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(row.Matched && row.Shown != null ? SizeLabel(row) : "—");
            return;
        }
        string id = "##autoRefitSize" + row.Slot;
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo(id, SizeLabel(row), ImGuiComboFlags.HeightLarge);
        if (!combo) return;

        bool ends = row.Slot is "_glv" or "_sho";
        if (ends && ImGui.Selectable(Strings.AutoRefit.Match + id, row.Matched))
        {
            pref.SetSize(row.Slot, null);
            config.Save();
        }

        string filter = sizeFilters.GetValueOrDefault(row.Slot, "");
        ComboSearch.Box(id, ref filter);
        sizeFilters[row.Slot] = filter;
        string? group = null;
        bool any = false;
        foreach (var option in row.Options)
        {
            if (!ComboSearch.Matches(filter, option.FullLabel)) continue;
            any = true;
            if (option.Group != group)
            {
                group = option.Group;
                ImGui.Separator();
                ImGui.TextDisabled(group);
            }
            if (!ImGui.Selectable(option.Label + id + option.Rel, !row.Matched && option == row.Shown)) continue;
            pref.SetSize(row.Slot, AutoRefitDecisions.RefOf(option));
            watcher.NotePrefilled(key, watcher.PrefilledIn(key).Where(p => p != row.Slot).ToList());
            config.Save();
        }
        if (!any) ImGui.TextDisabled(Strings.Parts.NoMatches);
    }
}
