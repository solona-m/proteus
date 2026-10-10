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

    /// <summary>The collection being edited; the player's own until another is picked.</summary>
    private Guid? editing;

    /// <summary>The player's race, which the size lists are filtered by — re-read now and then.</summary>
    private string? race;
    private long raceAt;

    private string bodyFilter = "", chestFilter = "", legsFilter = "";
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        RefreshIfDue();
        if (collections == null)
        {
            ImGui.TextDisabled(s.NoCollections);
            return;
        }
        editing ??= currentCollection?.Id;

        DrawCollectionCombo();
        if (editing is not { } collection) return;

        string key = collection.ToString("D");
        config.AutoRefitByCollection.TryGetValue(key, out var pref);

        if (!askedForBodies)
        {
            askedForBodies = true;
            watcher.RefreshBodies();
        }
        var bodies = watcher.Index.Snapshot;
        DrawBodyCombo(bodies, pref, key, collection);

        if (pref is not { BodyDir.Length: > 0 })
        {
            ImGui.TextDisabled(s.NoBodyChosen);
        }
        else if (bodies != null && watcher.Index.Find(pref.BodyDir) is { } body)
        {
            DrawSizes(body, pref);
        }
        else if (bodies != null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(s.BodyMissingFmt, pref.BodyDir));
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(!on || pref is not { BodyDir.Length: > 0 } || watcher.Current.Busy))
            if (ImGui.Button(s.RunNow))
                watcher.RunNow();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(s.RunNowTip);

        if (pref != null)
        {
            ImGui.SameLine();
            if (ImGui.Button(s.Clear))
            {
                config.AutoRefitByCollection.Remove(key);
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
        using var combo = ImRaii.Combo("##autoRefitCollection", editing is { } e ? CollectionLabel(e) : s.Choose);
        if (!combo) return;

        foreach (var (id, _) in collections!.OrderBy(c => c.Key == currentCollection?.Id ? 0 : 1)
                                            .ThenBy(c => c.Value, StringComparer.OrdinalIgnoreCase))
            if (ImGui.Selectable(CollectionLabel(id) + "##" + id.ToString("N"), editing == id))
                editing = id;
    }

    private void DrawBodyCombo(IReadOnlyList<BodyModIndex.Entry>? bodies, AutoRefitPreference? pref, string key,
                               Guid collection)
    {
        var s = Strings.AutoRefit;
        ImGui.TextUnformatted(s.Body);
        ImGui.SetNextItemWidth(-1);
        string shown = pref is { BodyDir.Length: > 0 }
            ? watcher.Index.Find(pref.BodyDir)?.Name ?? pref.BodyDir
            : s.Choose;
        using var combo = ImRaii.Combo("##autoRefitBody", shown, ImGuiComboFlags.HeightLarge);
        if (!combo) return;

        // Opening the list looks again, so a body mod installed since appears.
        if (ImGui.IsWindowAppearing()) watcher.RefreshBodies();
        if (bodies == null)
        {
            ImGui.TextDisabled(s.FindingBodies);
            return;
        }
        if (bodies.Count == 0)
        {
            ImGui.TextDisabled(s.NoBodies);
            return;
        }

        ComboSearch.Box("##autoRefitBody", ref bodyFilter);
        bool any = false;
        foreach (var body in bodies)
        {
            if (bodyFilter.Length > 0 && !body.Name.Contains(bodyFilter, StringComparison.OrdinalIgnoreCase)
                && !body.Dir.Contains(bodyFilter, StringComparison.OrdinalIgnoreCase)) continue;
            any = true;
            if (!ImGui.Selectable(body.Name + "##" + body.Dir,
                                  string.Equals(pref?.BodyDir, body.Dir, StringComparison.OrdinalIgnoreCase)))
                continue;

            var chosen = pref ?? new AutoRefitPreference();
            if (!string.Equals(chosen.BodyDir, body.Dir, StringComparison.OrdinalIgnoreCase))
            {
                // Another body's sizes: the old ones name files of the old mod.
                chosen.Chest = null;
                chosen.Legs = null;
            }
            chosen.BodyDir = body.Dir;
            chosen.CollectionName = collections != null && collections.TryGetValue(collection, out var name) ? name : "";
            config.AutoRefitByCollection[key] = chosen;
            config.Save();
        }
        if (!any) ImGui.TextDisabled(Strings.Parts.NoMatches);
    }

    private void DrawSizes(BodyModIndex.Entry body, AutoRefitPreference pref)
    {
        var s = Strings.AutoRefit;
        var catalog = body.Catalog;
        if (race != null && !catalog.SlotsFor(race).Any())
        {
            string sex = BodySizeCatalog.IsMaleRace(race) ? Strings.Parts.RetargetMale : Strings.Parts.RetargetFemale;
            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
                ImGui.TextWrapped(string.Format(Strings.Parts.RetargetNoBodiesForSexFmt, body.Name, sex));
            return;
        }

        var chestOptions = catalog.For("_top", race);
        var legsOptions = catalog.For("_dwn", race);
        var chest = AutoRefitDecisions.Resolve(chestOptions, pref.Chest);
        var legs = AutoRefitDecisions.Resolve(legsOptions, pref.Legs);

        if (DrawSizeCombo("##autoRefitChest", s.ChestSize, chestOptions, chest, ref chestFilter) is { } pickedChest)
        {
            pref.Chest = AutoRefitDecisions.RefOf(pickedChest);
            chest = pickedChest;
            config.Save();
        }
        if (DrawSizeCombo("##autoRefitLegs", s.LegsSize, legsOptions, legs, ref legsFilter) is { } pickedLegs)
        {
            pref.Legs = AutoRefitDecisions.RefOf(pickedLegs);
            legs = pickedLegs;
            config.Save();
        }

        // Nothing until chest or legs is chosen: the refit follows only a chosen size, never the body's first.
        var anchor = chest ?? legs;
        string hands = anchor == null ? "—" : AutoRefitDecisions.Companion(catalog.For("_glv", race), anchor, null)?.Label ?? "—";
        string feet = anchor == null ? "—" : AutoRefitDecisions.Companion(catalog.For("_sho", race), anchor, null)?.Label ?? "—";
        ImGui.TextDisabled(string.Format(s.HandsFeetFmt, hands, feet));
    }

    /// <summary>A dropdown of one slot's sizes, grouped as the author grouped them. Returns the size clicked, if any.</summary>
    private static BodyOption? DrawSizeCombo(string id, string label, IReadOnlyList<BodyOption> options,
                                             BodyOption? chosen, ref string filter)
    {
        if (options.Count == 0) return null;
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo(id, chosen?.Label ?? Strings.AutoRefit.Choose, ImGuiComboFlags.HeightLarge);
        if (!combo) return null;

        ComboSearch.Box(id, ref filter);
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
            if (ImGui.Selectable(option.Label + id + option.Rel, option == chosen)) picked = option;
        }
        if (!any) ImGui.TextDisabled(Strings.Parts.NoMatches);
        return picked;
    }
}
