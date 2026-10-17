using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Which body and size each part of one refit goes onto, now that a collection can choose a body per part — the chest on
/// one body mod, the legs on another (see <see cref="AutoRefitPreference.BodyOf"/>). One per job: the preference as
/// gathered, the installed bodies as that job read them, the race it refits at, and what the character wears in each
/// part's body. Pure: nothing here asks Penumbra or reads a file but <see cref="PathOf"/>'s catalog lookup.
/// <para/>
/// With one body for every part — every preference from before, and every player who never picks a second — each
/// answer is the one the single-body refit gave: the same sizes, the same companions, the same names.
/// </summary>
internal sealed class AutoRefitTargets
{
    private readonly AutoRefitPreference pref;
    private readonly Func<string, BodyModIndex.Entry?> find;
    private readonly IReadOnlyDictionary<string, BodyOption?> worn;

    /// <summary>The race sizes are listed at; null lists every race's.</summary>
    public string? Race { get; }

    /// <param name="find">An installed body mod by folder, or null when it is not installed.</param>
    /// <param name="worn">What the character wears in each part's own body (see <see cref="WornFrom"/>); a part left out
    /// wears nothing that can be told.</param>
    public AutoRefitTargets(AutoRefitPreference pref, Func<string, BodyModIndex.Entry?> find, string? race,
                            IReadOnlyDictionary<string, BodyOption?>? worn = null)
    {
        this.pref = pref;
        this.find = find;
        Race = race;
        this.worn = worn ?? new Dictionary<string, BodyOption?>(StringComparer.Ordinal);
    }

    /// <summary>The same preference and bodies at <paramref name="race"/>, with what is worn in each part's body.</summary>
    public AutoRefitTargets With(string? race, IReadOnlyDictionary<string, BodyOption?> wornNow)
        => new(pref, find, race, wornNow);

    /// <summary>The body mod folder a part is refitted onto; empty for none.</summary>
    public string DirOf(string slot) => pref.BodyOf(slot);

    /// <summary>The installed body mod a part is refitted onto; null when none is chosen or it is not installed.</summary>
    public BodyModIndex.Entry? Body(string slot) => DirOf(slot) is { Length: > 0 } dir ? find(dir) : null;

    /// <summary>The body chosen for a part when it is not installed; null otherwise.</summary>
    public string? MissingDir(string slot) => DirOf(slot) is { Length: > 0 } dir && find(dir) == null ? dir : null;

    /// <summary>The sizes of a part's body.</summary>
    public BodySizeCatalog? Catalog(string slot) => Body(slot)?.Catalog;

    /// <summary>A part's sizes at the race; none when it has no body.</summary>
    public IReadOnlyList<BodyOption> Options(string slot) => Catalog(slot)?.For(slot, Race) ?? [];

    /// <summary>The size chosen for a part, found in its own body at the race; null when none is.</summary>
    public BodyOption? Chosen(string slot) => AutoRefitDecisions.Resolve(Options(slot), pref.SizeOf(slot));

    /// <summary>What the character wears in a part's body, as handed in.</summary>
    public BodyOption? Worn(string slot) => worn.GetValueOrDefault(slot);

    /// <summary>The size every part nobody sized follows: the chest chosen, else the legs.</summary>
    public BodyOption? Anchor => Chosen("_top") ?? Chosen("_dwn");

    /// <summary>The body <see cref="Anchor"/> is a size of.</summary>
    public string? AnchorDir => Chosen("_top") != null ? DirOf("_top") : Chosen("_dwn") != null ? DirOf("_dwn") : null;

    /// <summary>
    /// The size <paramref name="slot"/> is refitted onto when a garment in <paramref name="primary"/> reaches it, or null
    /// to leave it out. The garment's own chest or legs goes only onto a size the player chose. A chest or legs it merely
    /// reaches — a dress's legs — follows the chosen one (<see cref="AutoRefitDecisions.Companion"/>) when it is on that
    /// one's body; on another body there is nothing to follow, and with no size chosen there it is left out rather than
    /// given one by guess. Hands and feet take the size chosen for them, else are matched (<see cref="Matched"/>).
    /// </summary>
    public BodyOption? TargetFor(string slot, string primary)
    {
        // Rue goes with Rue alone (see IsRue): a part on the other side of that line from the garment's own is left out.
        string lead = LeadSlot(primary);
        if (slot != lead && IsRue(DirOf(slot), Body(slot)?.Name) != IsRue(DirOf(lead), Body(lead)?.Name)) return null;
        if (slot is "_top" or "_dwn")
        {
            if (Chosen(slot) is { } chosen) return chosen;
            if (slot == primary || Anchor is not { } anchor) return null;
            return string.Equals(DirOf(slot), AnchorDir, StringComparison.OrdinalIgnoreCase)
                ? AutoRefitDecisions.Companion(Options(slot), anchor, Worn(slot))
                : null;
        }
        if (pref.SizeOf(slot) != null) return Chosen(slot);
        return Anchor is { } a ? Matched(Options(slot), a, Worn(slot), out _) : null;
    }

    /// <summary>
    /// Whether a body mod is Rue's — Rue+ or a fork of it such as LavaBod+'s LaRue — by a word of its folder or name.
    /// Rue's parts are made to meet only each other's: a part on Rue is never put beside a part on another body.
    /// </summary>
    internal static bool IsRue(string dir, string? name)
        => AutoRefitDecisions.Words(dir + " " + (name ?? "")).Any(w => w is "rue" or "larue");

    /// <summary>Whether a part on <paramref name="dir"/> is on the other side of the Rue line (see <see cref="IsRue"/>)
    /// from the bodies the other parts are on.</summary>
    internal bool CrossesRue(string slot, string dir, string? name)
        => AutoRefitDecisions.Slots.Where(s => s != slot && DirOf(s).Length > 0)
                                   .Any(s => IsRue(DirOf(s), Body(s)?.Name) != IsRue(dir, name));

    /// <summary>A part follows the anchor's size by name (<see cref="Basis.Anchor"/>), else the size worn, else the first.</summary>
    internal enum Basis { Anchor, Worn, First }

    /// <summary>
    /// The size a hand or foot nobody sized goes onto: <see cref="AutoRefitDecisions.Companion"/> — one function, so the
    /// Settings label and the refit name the same size — and what it was found by, which the label says when it is not
    /// the anchor and the worn size cannot be read there.
    /// </summary>
    internal static BodyOption? Matched(IReadOnlyList<BodyOption> options, BodyOption? anchor, BodyOption? worn,
                                        out Basis basis)
    {
        var found = AutoRefitDecisions.Companion(options, anchor, worn);
        basis = found == null ? Basis.First
              : anchor != null && AutoRefitDecisions.ByAnchor(options, anchor) == found ? Basis.Anchor
              : worn != null && options.Contains(worn) && found == worn ? Basis.Worn
              : Basis.First;
        return found;
    }

    /// <summary>The model file of a size of <paramref name="slot"/>, in that part's body.</summary>
    public string PathOf(string slot, BodyOption option) => Catalog(slot)!.PathOf(option);

    /// <summary>A part's body as chat names it.</summary>
    public string NameOf(string slot) => Body(slot)?.Name ?? DirOf(slot);

    /// <summary>
    /// The part whose body a garment in <paramref name="primary"/> is named and routed by — its own, or for shoes and
    /// gloves the legs or arms they are refitted on (see <see cref="AutoRefitDecisions.LimbOf"/>).
    /// </summary>
    public static string LeadSlot(string primary) => AutoRefitDecisions.LimbOf(primary) ?? primary;

    /// <summary>
    /// Whether a refit is one between body mods: some part of the garment goes onto a body mod other than the one it was
    /// made on. Only parts that swap count — the garment's own and those it reaches — never a part along for its shape.
    /// </summary>
    internal static bool Across(string sourceDir, IEnumerable<string> pairDirs)
        => pairDirs.Any(d => !string.Equals(d, sourceDir, StringComparison.OrdinalIgnoreCase));

    /// <summary>The target body's file chosen now for every part, by slot, with the body it is of (see
    /// <see cref="RefitCore.JoinOption"/>).</summary>
    public Dictionary<string, (string Dir, string Rel)> ChosenFiles(string primary)
    {
        var chosen = new Dictionary<string, (string Dir, string Rel)>(StringComparer.Ordinal);
        foreach (string slot in AutoRefitDecisions.Slots)
            if (TargetFor(slot, primary) is { } to) chosen[slot] = (DirOf(slot), to.Rel);
        return chosen;
    }

    /// <summary>
    /// The body each recorded part went onto, for a refit's record — null when they are all <paramref name="bodyMod"/>,
    /// the body the record is saved under, which is how a record has always said it (see
    /// <see cref="BodyRetargetWriter.Entry.TargetBodies"/>).
    /// </summary>
    internal static Dictionary<string, string>? BodiesOf(IReadOnlyDictionary<string, string> dirsByKey, string bodyMod)
        => dirsByKey.Values.Any(d => !string.Equals(d, bodyMod, StringComparison.OrdinalIgnoreCase))
            ? new Dictionary<string, string>(dirsByKey, StringComparer.Ordinal)
            : null;

    /// <summary>The body and sizes an accessory's cover is measured against and keyed by (see <see cref="AccessoryCover.Key"/>).
    /// One constructor for the walk and the refit, so the two keys are one.</summary>
    internal static AccessoryChosen ChosenOf(AutoRefitPreference pref)
        => new(pref.BodyOf("_top"), pref.Chest?.Rel, pref.BodyOf("_dwn"), pref.Legs?.Rel);

    /// <summary>The chest and legs bodies and sizes chosen, as an accessory's cover reading is keyed.</summary>
    internal sealed record AccessoryChosen(string ChestDir, string? ChestRel, string LegsDir, string? LegsRel);

    /// <summary>
    /// The option of <paramref name="slot"/> whose file the collection resolves the body model to, from those answers
    /// alone — matched off the framework thread, the asking done on it (see <see cref="RefitCore.WornOption"/>).
    /// </summary>
    /// <param name="resolved">Each game path as Penumbra resolved it for the player; a path left out resolves to nothing.</param>
    internal static BodyOption? WornFrom(BodySizeCatalog catalog, string slot, string? race,
                                         IReadOnlyDictionary<string, string?> resolved)
        => RefitCore.WornOption(catalog, slot, race, path => resolved.GetValueOrDefault(path));

    /// <summary>The game paths a part's body draws at the race: what is asked of Penumbra for <see cref="WornFrom"/>.</summary>
    internal static IEnumerable<string> GamePathsOf(BodySizeCatalog catalog, string slot, string? race)
        => catalog.For(slot, race).Select(o => o.GamePath).Distinct(StringComparer.OrdinalIgnoreCase);
}
