using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>One equipment or accessory item an IMC entry belongs to: kind letter ('e' or 'a'), set, slot label.</summary>
internal readonly record struct ImcItem(char Kind, int SetId, string SlotLabel)
{
    /// <summary>"e0666" — the kind letter and set number, as <see cref="ContentSlot.Parsed.SetTag"/> spells it.</summary>
    public string SetTag => $"{Kind}{SetId:D4}";
}

/// <summary>One IMC attribute mask a pack writes: from an option (<paramref name="Group"/>/<paramref name="Option"/>),
/// or from default data (both null).</summary>
internal sealed record ImcEdit(string? Group, string? Option, ImcItem Item, int Variant, ushort Mask);

/// <summary>
/// The IMC part toggles a pack carries as <c>Imc</c> manipulations inside ordinary Single/Multi options and default data
/// — the shape TexTools packs arrive in. Penumbra applies them to the item the pack replaces; an imported piece rides a
/// host accessory instead, so the composite has to work out the mask itself (<see cref="ContentImcVisibility"/>).
/// Penumbra's <c>Imc</c>-TYPE groups are not here: they keep their own path (<see cref="ContentAttributeGroup"/>).
/// <para/>
/// Immutable, so one build can share it across pieces. The edits are kept in Penumbra's order: groups by priority, the
/// later group first on a tie; a Multi group's options by priority, the earlier first on a tie; default data last. The
/// first active edit wins, as Penumbra's first write does (<c>Mod.GetData</c>, <c>MetaDictionary.UnionWith</c>).
/// </summary>
internal sealed class ContentImcEdits
{
    /// <summary>One group (or default data) and its options' edits, in application order.</summary>
    private sealed record Source(PenumbraPackage.PackGroup? Group, IReadOnlyList<(int Index, PenumbraPackage.PackOption Option, IReadOnlyList<ImcEdit> Edits)> Options);

    private readonly IReadOnlyList<Source> sources;
    private readonly IReadOnlyDictionary<ImcItem, int> governing;

    private ContentImcEdits(IReadOnlyList<Source> sources, IReadOnlyDictionary<ImcItem, int> governing)
    {
        this.sources = sources;
        this.governing = governing;
    }

    /// <summary>The edits <paramref name="pack"/>'s Single/Multi options and default data write, or null for none.</summary>
    internal static ContentImcEdits? From(PenumbraPackage.Contents pack)
    {
        var sources = new List<Source>();
        var ordered = pack.Groups
            .Where(g => string.Equals(g.Type, "Single", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(g.Type, "Multi", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Priority).ThenByDescending(g => g.Index);
        foreach (var g in ordered)
        {
            var options = g.Options
                .Select((o, i) => (Index: i, Option: o, Edits: (IReadOnlyList<ImcEdit>)EditsOf(o.Imc, g.Name, o.Name)))
                .Where(x => x.Edits.Count > 0)
                .OrderByDescending(x => x.Option.Priority).ThenBy(x => x.Index)
                .ToList();
            if (options.Count > 0) sources.Add(new Source(g, options));
        }

        var defaults = EditsOf(pack.DefaultImc, null, null);
        if (defaults.Count > 0)
            sources.Add(new Source(null, [(0, new PenumbraPackage.PackOption("", null,
                new Dictionary<string, string>(), []), defaults)]));
        if (sources.Count == 0) return null;

        // The variant whose entry governs each item — see GoverningVariant.
        var redirects = pack.AllFiles.Select(f => new PenumbraModMeta.Redirect(f.Key, f.Value, "")).ToList();
        var governing = new Dictionary<ImcItem, int>();
        foreach (var byItem in sources.SelectMany(s => s.Options).SelectMany(o => o.Edits).GroupBy(e => e.Item))
        {
            var written = byItem.Select(e => e.Variant).ToHashSet();
            int? material = ImcEntrySource.MaterialVariantOf(redirects, byItem.Key.SetTag);
            governing[byItem.Key] = material is { } m && written.Contains(m) ? m
                : written.Where(v => v >= 1).DefaultIfEmpty(0).Min();
        }
        return new ContentImcEdits(sources, governing);
    }

    /// <summary>
    /// <see cref="From"/> over an installed mod's manifest (<see cref="PenumbraPackage.ReadInstalledManifest"/>). Never
    /// throws: the composite must go on without one mod's toggles. A manifest that cannot be read, or anything else
    /// that goes wrong reading it, comes back as <c>Error</c> — the exception itself, so a fault here is logged as one
    /// rather than passing for a bad file.
    /// </summary>
    internal static (ContentImcEdits? Edits, Exception? Error) ReadInstalled(string modRoot)
    {
        try { return (From(PenumbraPackage.ReadInstalledManifest(modRoot)), null); }
        catch (Exception ex) { return (null, ex); }
    }

    /// <summary>Whether any option or default data writes an entry for this item.</summary>
    internal bool Names(ImcItem item) => governing.ContainsKey(item);

    /// <summary>
    /// The variant whose entry decides the item's parts: the variant the pack's material paths name, when it writes that
    /// one; else the lowest it writes from 1 up; else 0. TexTools writes every variant from 0, and 0 is the default set
    /// items do not wear. -1 for an item no edit names.
    /// </summary>
    internal int GoverningVariant(ImcItem item) => governing.TryGetValue(item, out var v) ? v : -1;

    /// <summary>
    /// The edit Penumbra would apply to <paramref name="item"/>'s governing entry under <paramref name="selected"/>, or
    /// null when none is active. A group missing from <paramref name="selected"/>, or a null selection, reads the
    /// group's <c>DefaultSettings</c>. Names match case-insensitively, as <see cref="ImcEntrySource.MaskFor"/>.
    /// </summary>
    internal ImcEdit? Active(ImcItem item, IReadOnlyDictionary<string, List<string>>? selected)
    {
        if (!governing.TryGetValue(item, out var variant)) return null;
        foreach (var source in sources)
        {
            var on = source.Group is { } g ? IsOn(g, selected) : (_ => true);
            foreach (var (index, option, edits) in source.Options)
            {
                if (!on((index, option))) continue;
                if (edits.FirstOrDefault(e => e.Item == item && e.Variant == variant) is { } hit) return hit;
            }
        }
        return null;
    }

    /// <summary>
    /// "Group / Option" of every option whose edit of <paramref name="item"/>'s governing entry draws one of
    /// <paramref name="hiddenParts"/>: what to tick to bring them back.
    /// </summary>
    internal IReadOnlyList<string> OptionsThatShow(ImcItem item, IEnumerable<string> hiddenParts)
    {
        if (!governing.TryGetValue(item, out var variant)) return [];
        int bits = 0;
        foreach (var name in hiddenParts)
            if (SecondSkinWriter.IsVariantAttribute(name)) bits |= 1 << (name[7] - 'a');
        if (bits == 0) return [];

        return sources.Where(s => s.Group != null)
            .SelectMany(s => s.Options.SelectMany(o => o.Edits))
            .Where(e => e.Item == item && e.Variant == variant && (e.Mask & bits) != 0)
            .Select(e => $"{e.Group} / {e.Option}")
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The item a content model belongs to, read off its file name; null for anything but gear.</summary>
    internal static ImcItem? ItemOf(string modelRel)
    {
        if (ContentSlot.Parse(modelRel) is not { } p
            || ContentSlot.VariantLetter(p.Label) == null
            || ContentSlot.SetIdOf(p.SetTag) is not { } set) return null;
        char kind = p.SetTag[0];
        return kind is 'e' or 'a' ? new ImcItem(kind, set, p.Label) : null;
    }

    /// <summary>
    /// The variant parts of the slot <paramref name="slotLetter"/> names whose bit <paramref name="mask"/> leaves off.
    /// Another slot's variant tags are never judged by this mask, as the game judges each by its own slot's entry
    /// (the rule <c>BodyRetarget.UndrawnVariants</c> applies to body mods).
    /// </summary>
    internal static IReadOnlySet<string> HiddenVariants(IEnumerable<string> attrNames, char slotLetter, ushort mask)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in attrNames)
        {
            if (!SecondSkinWriter.IsVariantAttribute(name) || name[4] != slotLetter) continue;
            if ((mask & (1 << (name[7] - 'a'))) == 0) hidden.Add(name);
        }
        return hidden;
    }

    private static List<ImcEdit> EditsOf(IEnumerable<PenumbraPackage.PackImc> imc, string? group, string? option)
    {
        var result = new List<ImcEdit>();
        foreach (var m in imc)
        {
            char kind = string.Equals(m.ObjectType, "Equipment", StringComparison.OrdinalIgnoreCase) ? 'e'
                      : string.Equals(m.ObjectType, "Accessory", StringComparison.OrdinalIgnoreCase) ? 'a'
                      : '\0';
            if (kind == '\0' || m.PrimaryId < 0 || m.Variant < 0
                || ContentSlot.LabelForEquipSlot(m.EquipSlot) is not { } label) continue;
            result.Add(new ImcEdit(group, option, new ImcItem(kind, m.PrimaryId, label), m.Variant,
                                   (ushort)(m.AttributeMask & 0x3FF)));
        }
        return result;
    }

    /// <summary>Which of a group's options are switched on under <paramref name="selected"/>.</summary>
    private static Func<(int Index, PenumbraPackage.PackOption Option), bool> IsOn(
        PenumbraPackage.PackGroup group, IReadOnlyDictionary<string, List<string>>? selected)
    {
        List<string>? names = null;
        if (selected != null)
            foreach (var (name, list) in selected)
                if (string.Equals(name, group.Name, StringComparison.OrdinalIgnoreCase)) { names = list; break; }

        bool single = string.Equals(group.Type, "Single", StringComparison.OrdinalIgnoreCase);
        // A Single group always has a choice; an empty report means none was made, so its default stands.
        if (names != null && !(single && names.Count == 0))
        {
            var chosen = single ? names.Take(1).ToList() : names;
            return o => chosen.Contains(o.Option.Name, StringComparer.OrdinalIgnoreCase);
        }
        return single
            ? o => (ulong)o.Index == group.DefaultSettings
            : o => o.Index < 64 && (group.DefaultSettings & (1ul << o.Index)) != 0;
    }
}

/// <summary>Session-wide "said it once" sets, keyed by mod root, so a composite that runs every few seconds does not repeat itself.</summary>
internal static class ContentImcNotices
{
    private static readonly ConcurrentDictionary<string, byte> ReadFailures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> GameEntryHints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True the first time this session a mod's manifest could not be read for its part toggles.</summary>
    internal static bool FirstReadFailure(string modRoot) => ReadFailures.TryAdd(modRoot, 0);

    /// <summary>True the first time this session a mod's parts are hidden by the game's own entry.</summary>
    internal static bool FirstGameEntryHint(string modRoot) => GameEntryHints.TryAdd(modRoot, 0);

    private static readonly ConcurrentDictionary<string, byte> SlotDisagreements = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> UntrustedSlots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True the first time this session a mod's piece is named for one slot and was imported as another.</summary>
    internal static bool FirstSlotDisagreement(string modRoot) => SlotDisagreements.TryAdd(modRoot, 0);

    /// <summary>True the first time this session a mod's piece has no usable recorded slot for its toggle groups to go by.</summary>
    internal static bool FirstUntrustedSlot(string modRoot) => UntrustedSlots.TryAdd(modRoot, 0);
}

/// <summary>Where a content model's IMC mask came from.</summary>
internal enum ImcMaskSource
{
    /// <summary>Not decided here: no toggle speaks for the model.</summary>
    None,
    /// <summary>One of the pack's <c>Imc</c>-type groups (<see cref="ContentAttributeGroup"/>).</summary>
    ImcGroup,
    /// <summary>An <c>Imc</c> edit inside a selected Single/Multi option.</summary>
    Option,
    /// <summary>The pack's default data.</summary>
    DefaultData,
    /// <summary>No edit is active: the game's own entry for the item.</summary>
    GameEntry,
    /// <summary>No edit is active and the game's entry could not be read: every part shown.</summary>
    AllParts,
}

/// <summary>
/// How one content model's IMC-toggled parts are drawn. <see cref="Hidden"/> is dropped; <see cref="OwnAttributes"/>
/// emits survivors untagged; <see cref="DropVariantAttributes"/> takes only the variant tags off — see
/// <see cref="ContentGeometry"/>. <see cref="Source"/> says where the mask came from; <see cref="Why"/> spells it
/// out for the log.
/// </summary>
internal readonly record struct ContentVisibility(
    IReadOnlySet<string>? Hidden, bool OwnAttributes, bool DropVariantAttributes, ImcMaskSource Source, string? Why);

/// <summary>Which of a content model's toggled parts are drawn, from the pack's IMC groups or its option edits.</summary>
internal static class ContentImcVisibility
{
    /// <summary>
    /// A model an <c>Imc</c>-type group governs keeps exactly today's answer (<see cref="ContentPieceResolver.HiddenAttributes"/>).
    /// Else, a model whose item the pack's option or default edits name takes the active edit's mask, else the game's
    /// own entry (<paramref name="vanillaMask"/>, asked for the governing variant), else every part shown; its own
    /// slot's variant parts are hidden by that mask and every variant tag is dropped, so the host's mask cannot judge
    /// them again. A piece stored off a game path whose recorded slot (<paramref name="pieceSlot"/>) no group names shows
    /// every part with its variant tags dropped: its item cannot be named, so no edit can speak for it either.
    /// Anything else is left alone.
    /// </summary>
    internal static ContentVisibility For(
        IReadOnlyList<ContentAttributeGroup>? attributeGroups, ContentImcEdits? edits,
        string modelRel, string? pieceSlot, IReadOnlyList<string> attrNames,
        IReadOnlyDictionary<string, List<string>>? selected,
        Func<ImcItem, int, ushort?> vanillaMask)
    {
        var governing = ContentPieceResolver.GoverningGroups(attributeGroups, modelRel, pieceSlot);
        if (governing.Count > 0)
            return new ContentVisibility(
                ContentPieceResolver.HiddenAttributes(governing, modelRel, pieceSlot, attrNames, selected),
                OwnAttributes: true, DropVariantAttributes: false, ImcMaskSource.ImcGroup,
                Why: "imc group " + string.Join(", ", governing.Select(g => g.Group)));

        // No group speaks for it only because its recorded slot told them apart: the other slots' masks are not its own.
        if (ContentPieceResolver.HasSlottedGroup(attributeGroups) && ContentSlot.Parse(modelRel) == null
            && ContentSlot.EquipLabel(pieceSlot) is { } label)
            return new ContentVisibility(null, OwnAttributes: false, DropVariantAttributes: true,
                ImcMaskSource.AllParts, Why: $"all parts (no imc group for {label})");

        if (edits == null || ContentImcEdits.ItemOf(modelRel) is not { } item || !edits.Names(item)
            || ContentSlot.VariantLetter(item.SlotLabel) is not { } letter)
            return default;

        ushort mask;
        ImcMaskSource source;
        string why;
        if (edits.Active(item, selected) is { } hit)
        {
            mask = hit.Mask;
            (source, why) = hit.Group == null
                ? (ImcMaskSource.DefaultData, "default data")
                : (ImcMaskSource.Option, $"option {hit.Group}/{hit.Option}");
        }
        else if (vanillaMask(item, edits.GoverningVariant(item)) is { } vanilla)
        {
            mask = vanilla;
            (source, why) = (ImcMaskSource.GameEntry, "game entry");
        }
        else
        {
            mask = 0x3FF;
            (source, why) = (ImcMaskSource.AllParts, "all parts");
        }

        var hidden = ContentImcEdits.HiddenVariants(attrNames, letter, mask);
        return new ContentVisibility(hidden.Count > 0 ? hidden : null,
            OwnAttributes: false, DropVariantAttributes: true, source, why);
    }
}
