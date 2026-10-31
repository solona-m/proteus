using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Which of a body model's custom attributes (<c>atrx_</c>) its mod's options switch off — Penumbra's <c>Atr</c>
/// manipulations, under a choice of those options. NIRÆ ships all its chests in one chest model and lets its
/// "Chest" group pick the one drawn: everything a refit does with the body has to see only that one, as with the IMC
/// variants of <see cref="BodyRetarget.UndrawnVariants"/>.
/// <para/>
/// A copy of how Penumbra decides (<c>Collections/Cache/ShapeAttributeHashSet.CheckEntry</c>, <c>Mods/Mod.GetData</c>,
/// <c>Mods/Groups/MultiModGroup</c>, <c>SingleModGroup.FixSetting</c>): attributes are drawn unless an entry turns them
/// off (<c>Meta/ShapeAttributeManager</c>). The body is judged as worn bare: its own slot, set 0. Whatever cannot be
/// judged — an entry Penumbra would read differently, one gated by a <c>Condition</c>, one in a <c>Combining</c> group, a
/// race-limited one with no race to judge it by — leaves its attribute exactly as before: drawn, and its tag kept.
/// </summary>
internal static class BodyAtrSwitches
{
    /// <param name="Off">The attributes the options switch off on the body.</param>
    /// <param name="Switched">Every attribute the mod switches on a body slot, under any choice of its options: the tags
    /// that must not stay on skin a refit brings in, or the skin vanishes when the user picks another option.</param>
    internal sealed record Result(IReadOnlySet<string> Off, IReadOnlySet<string> Switched);

    /// <summary>One body mod's manifest, read for one slot under one choice of its options: what
    /// <see cref="Resolve"/> needs, with no file left to read.</summary>
    /// <param name="EquipSlot">"Body", "Legs", "Hands" or "Feet" (<see cref="BodyRetarget.ImcSlotName"/>).</param>
    /// <param name="Selected">The ticked options, group name to option names, as Penumbra reports them; null, or a group
    /// missing from it, reads the group's own <c>DefaultSettings</c>.</param>
    /// <param name="Race">The body's race as Penumbra codes it (201 for c0201); null when unknown.</param>
    internal sealed record Query(PenumbraPackage.Contents Manifest, string EquipSlot,
                                 IReadOnlyDictionary<string, List<string>>? Selected, ushort? Race);

    private const string Prefix = "atrx_";

    private static readonly string[] BodySlots = ["Body", "Legs", "Hands", "Feet"];

    private static readonly ConcurrentDictionary<string, (long Fingerprint, PenumbraPackage.Contents Manifest)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The mod's manifest, parsed once per edit (<see cref="BodyModIndex.Fingerprint"/>). Null when it cannot be read.
    /// Never throws.
    /// </summary>
    internal static PenumbraPackage.Contents? Manifest(string modRoot)
    {
        try
        {
            long fingerprint = BodyModIndex.Fingerprint(modRoot);
            if (fingerprint != 0 && Cache.TryGetValue(modRoot, out var have) && have.Fingerprint == fingerprint)
                return have.Manifest;
            var manifest = PenumbraPackage.ReadInstalledManifest(modRoot);
            if (fingerprint != 0) Cache[modRoot] = (fingerprint, manifest);
            return manifest;
        }
        catch (Exception)
        {
            return null;   // a mod whose manifest cannot be read has no say, as before
        }
    }

    /// <summary>
    /// What the mod switches for the body under <paramref name="q"/>, with <paramref name="onto"/> — the size being
    /// refitted onto — taken as chosen in its own group. Null when the mod switches nothing. Never throws.
    /// </summary>
    internal static Result? Resolve(Query q, BodyOption? onto = null)
    {
        try
        {
            return ResolveOrThrow(q, onto);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the mod at <paramref name="modRoot"/> switches body attributes with its options — one model carrying
    /// several shapes, like NIRÆ's nine chests. Never throws; false when its manifest cannot be read.
    /// </summary>
    internal static bool SwitchesShapes(string modRoot)
    {
        if (Manifest(modRoot) is not { } manifest) return false;
        return BodySlots.Any(slot => Resolve(new Query(manifest, slot, null, null)) is { Switched.Count: > 0 });
    }

    /// <summary>A group of options that picks one of several shapes a body model carries — NIRÆ's "Chest".</summary>
    /// <param name="Options">Its options' names, the first of each name, in the author's order.</param>
    /// <param name="Default">The one drawn when nothing is ticked: an index into <paramref name="Options"/>.</param>
    /// <param name="Attributes">The attributes it switches: the shapes, as the model's parts are tagged.</param>
    internal sealed record ShapeAxis(string Group, IReadOnlyList<string> Options, int Default, IReadOnlySet<string> Attributes);

    /// <summary>
    /// The group of <paramref name="manifest"/> that picks which shape of <paramref name="equipSlot"/>'s body model is
    /// drawn, or null: a single-choice group with no conditions, no files and at least two options, switching custom
    /// attributes of that slot for the body (set 0) — of several such, the one switching the most attributes (NIRÆ's
    /// Chest over its Belly, Nipples and Piercings), the earlier on a tie. Never throws.
    /// </summary>
    internal static ShapeAxis? ShapeGroup(PenumbraPackage.Contents manifest, string equipSlot)
    {
        try
        {
            ShapeAxis? best = null;
            int most = 0;
            foreach (var g in manifest.Groups)
            {
                if (!string.Equals(g.Type, "Single", StringComparison.OrdinalIgnoreCase) || g.HasCondition
                    || g.Options.Count < 2 || g.Options.Any(o => o.HasCondition || o.Files.Count > 0)) continue;
                var attributes = g.Options.SelectMany(o => o.Atr)
                                  .Where(e => Custom(e) && e.Readable && (e.Id is null or 0)
                                              && (e.Slot == null || string.Equals(e.Slot, equipSlot, StringComparison.OrdinalIgnoreCase)))
                                  .Select(e => e.Attribute).ToHashSet(StringComparer.Ordinal);
                int count = attributes.Count;
                if (count <= most) continue;
                var names = new List<string>();
                foreach (var o in g.Options)
                    if (!names.Contains(o.Name, StringComparer.OrdinalIgnoreCase)) names.Add(o.Name);
                // SingleModGroup.FixSetting: past the last option is the last.
                string dflt = g.Options[(int)Math.Min(g.DefaultSettings, (ulong)(g.Options.Count - 1))].Name;
                best = new ShapeAxis(g.Name, names, names.FindIndex(n => string.Equals(n, dflt, StringComparison.OrdinalIgnoreCase)),
                                     attributes);
                most = count;
            }
            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record Owner(IReadOnlyList<PenumbraPackage.PackAtr> Entries, bool Judgeable);

    private static Result? ResolveOrThrow(Query q, BodyOption? onto)
    {
        var manifest = q.Manifest;
        var applied = new List<Owner>();   // in the order Penumbra applies them: the first to set an entry wins
        var every = new List<Owner>();     // every owner, applied or not

        // Mod.GetData: groups by priority, highest first, a tie to the later group; the default data last.
        var groups = manifest.Groups.Select((g, i) => (Group: g, At: i)).Reverse()
                             .OrderByDescending(x => x.Group.Priority).Select(x => x.Group).ToList();
        foreach (var g in manifest.Groups)
        {
            if (string.Equals(g.Type, "Combining", StringComparison.OrdinalIgnoreCase))
            {
                every.Add(new Owner(g.ContainerAtr, Judgeable: false));
                foreach (var o in g.Options) every.Add(new Owner(o.Atr, Judgeable: false));
                continue;
            }
            foreach (var o in g.Options) every.Add(new Owner(o.Atr, Judgeable: !g.HasCondition && !o.HasCondition && Known(g)));
        }
        foreach (var g in groups)
        {
            if (string.Equals(g.Type, "Imc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(g.Type, "Combining", StringComparison.OrdinalIgnoreCase)) continue;
            var chosen = Chosen(g, q.Selected, onto, out bool judgeable);
            judgeable &= !g.HasCondition && Known(g);
            // MultiModGroup: ticked options by their own priority, highest first, a tie to the earlier option.
            foreach (int i in chosen.OrderByDescending(i => g.Options[i].Priority).ThenBy(i => i))
                applied.Add(new Owner(g.Options[i].Atr, judgeable && !g.Options[i].HasCondition));
            if (!judgeable)
                foreach (var o in g.Options) every.Add(new Owner(o.Atr, Judgeable: false));
        }
        applied.Add(new Owner(manifest.DefaultAtr, Judgeable: true));
        every.Add(new Owner(manifest.DefaultAtr, Judgeable: true));

        // Names left as they were: an entry this cannot read as Penumbra does, an owner it cannot judge, a race-limited
        // entry with no race to judge it by.
        var noSay = new HashSet<string>(StringComparer.Ordinal);
        foreach (var owner in every.Concat(applied))
            foreach (var e in owner.Entries)
                if (Custom(e) && (!e.Readable || !owner.Judgeable || (q.Race == null && e.GenderRace != 0)))
                    noSay.Add(e.Attribute);

        // The applied entries by (slot, set, race, attribute): the first owner to set one wins (SubMod.UnionWith), and
        // within one owner its first entry (MetaDeserialization's TryAdd).
        var table = new Dictionary<(string Slot, int? Id, int Race, string Attribute), bool>();
        foreach (var owner in applied)
            foreach (var e in owner.Entries)
                if (Custom(e) && e.Readable && owner.Judgeable && !noSay.Contains(e.Attribute))
                    table.TryAdd((SlotKey(e.Slot), e.Id, e.GenderRace, e.Attribute), e.Entry);

        var off = new HashSet<string>(StringComparer.Ordinal);
        int race = q.Race ?? 0;
        if (PenumbraPackage.AtrGenderRaces.Contains(race))   // a race Penumbra does not judge: nothing is off
        {
            string slot = SlotKey(q.EquipSlot);
            foreach (string name in table.Keys.Select(k => k.Attribute).Distinct())
                if (CheckEntry(table, slot, race, name) == false) off.Add(name);
        }

        var switched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var owner in every.Concat(applied))
            foreach (var e in owner.Entries)
                if (Custom(e) && e.Readable && !noSay.Contains(e.Attribute)
                    && (e.Slot == null || BodySlots.Contains(e.Slot, StringComparer.OrdinalIgnoreCase)))
                    switched.Add(e.Attribute);

        return off.Count == 0 && switched.Count == 0 ? null : new Result(off, switched);
    }

    /// <summary>
    /// ShapeAttributeHashSet.CheckEntry for the body (set 0): the first of (slot, set, race), (slot, set, any race),
    /// (slot, no set, race), (any slot, no set, race), (slot, no set, any race), (any slot, no set, any race) that has an
    /// entry decides. Null when none does — drawn.
    /// </summary>
    private static bool? CheckEntry(Dictionary<(string Slot, int? Id, int Race, string Attribute), bool> table,
                                    string slot, int race, string name)
    {
        (string, int?, int)[] tiers =
        [
            (slot, 0, race), (slot, 0, 0), (slot, null, race), ("", null, race), (slot, null, 0), ("", null, 0),
        ];
        foreach (var (s, id, r) in tiers)
            if (table.TryGetValue((s, id, r, name), out bool entry)) return entry;
        return null;
    }

    /// <summary>
    /// The options of <paramref name="g"/> applied under <paramref name="selected"/>, by index. Not judgeable when the
    /// selection names an option the group does not have.
    /// </summary>
    private static List<int> Chosen(PenumbraPackage.PackGroup g, IReadOnlyDictionary<string, List<string>>? selected,
                                    BodyOption? onto, out bool judgeable)
    {
        judgeable = true;
        bool multi = string.Equals(g.Type, "Multi", StringComparison.OrdinalIgnoreCase);
        var chosen = new List<int>();
        List<string>? names = null;
        if (selected != null)
            foreach (var (group, ticked) in selected)
                if (string.Equals(group, g.Name, StringComparison.OrdinalIgnoreCase)) { names = ticked; break; }

        if (names != null)
        {
            foreach (string name in names)
            {
                int at = IndexOf(g, name);
                if (at < 0) { judgeable = false; continue; }
                if (!chosen.Contains(at)) chosen.Add(at);
                if (!multi) break;
            }
            if (!multi && chosen.Count == 0 && names.Count > 0) judgeable = false;
        }
        else if (multi)
        {
            for (int i = 0; i < g.Options.Count && i < 64; i++)
                if ((g.DefaultSettings & (1ul << i)) != 0) chosen.Add(i);
        }
        else if (g.Options.Count > 0)
        {
            // SingleModGroup.FixSetting: past the last option is the last.
            chosen.Add((int)Math.Min(g.DefaultSettings, (ulong)(g.Options.Count - 1)));
        }

        // The size being refitted onto is that size's option, chosen.
        if (onto != null && !string.Equals(onto.Group, BodySizeCatalog.DefaultGroupName, StringComparison.Ordinal)
            && string.Equals(onto.Group, g.Name, StringComparison.OrdinalIgnoreCase) && OptionOf(g, onto) is { } pick)
        {
            if (!multi) chosen.Clear();
            if (!chosen.Contains(pick)) chosen.Add(pick);
            if (!multi) judgeable = true;
        }
        return chosen;
    }

    /// <summary>
    /// The option of <paramref name="g"/> a catalogued size is: the one of its name whose files put its model where the
    /// size has it (Neolithe names eight options "SFW M", each under its own heading), else the first of its name.
    /// </summary>
    private static int? OptionOf(PenumbraPackage.PackGroup g, BodyOption onto)
    {
        int first = -1;
        for (int i = 0; i < g.Options.Count; i++)
        {
            var o = g.Options[i];
            if (!string.Equals(o.Name, onto.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (first < 0) first = i;
            foreach (var (game, rel) in o.Files)
                if (RefitCore.Normal(game) == RefitCore.Normal(onto.GamePath) && RefitCore.Normal(rel) == RefitCore.Normal(onto.Rel))
                    return i;
        }
        return first >= 0 ? first : null;
    }

    private static int IndexOf(PenumbraPackage.PackGroup g, string name)
    {
        for (int i = 0; i < g.Options.Count; i++)
            if (string.Equals(g.Options[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>A group type whose choice this can read.</summary>
    private static bool Known(PenumbraPackage.PackGroup g)
        => string.Equals(g.Type, "Single", StringComparison.OrdinalIgnoreCase)
           || string.Equals(g.Type, "Multi", StringComparison.OrdinalIgnoreCase);

    /// <summary>Penumbra only reads custom attributes (<c>ShapeAttributeString.ValidateCustomAttributeString</c>).</summary>
    private static bool Custom(PenumbraPackage.PackAtr e) => e.Attribute.StartsWith(Prefix, StringComparison.Ordinal);

    private static string SlotKey(string? slot) => slot?.ToLowerInvariant() ?? "";
}
