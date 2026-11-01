using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// What the Body size tool remembers between garments: the body mod last refitted onto for each garment part, and
/// the sizes last chosen in each body mod.
/// <para/>
/// A player refits onto the body they are wearing over and over, across every garment they own, so the last answer
/// is very nearly always the next one. But a player can wear the chest from one body mod and the legs from another:
/// one remembered body for every part would send each leg piece to the chest's mod, and every switch back would cost a
/// click and the other mod's sizes. So the body is remembered per garment part (<see cref="Configuration.RetargetBodyByPart"/>),
/// and the sizes per body mod (<see cref="Configuration.RetargetSizesByBody"/>).
/// <para/>
/// Sizes are filled in for every part the panel draws, including ones this garment cannot reach: a part takes part in
/// the refit only once BOTH its ends are chosen, so a size to refit onto with no "made for" size beside it simply sits
/// out. The panel says so on the row and counts only paired parts in its header.
/// <para/>
/// Every lookup goes through here. The config's dictionaries come back from deserialisation with the ordinal
/// comparer, and an explicit <c>null</c> in the file replaces their initializers, so folders are matched without
/// regard to case by scanning the keys, and a null dictionary reads as empty.
/// </summary>
internal static class RememberedTargets
{
    /// <summary>The answer to <see cref="BodyToOpen"/>.</summary>
    /// <param name="Known">False while the installed body mods have not been found yet: ask again next frame.</param>
    /// <param name="Dir">The installed body mod to switch to, in the casing it is installed under; null when the
    /// current body stays, or there is nothing to restore.</param>
    internal readonly record struct Restore(bool Known, string? Dir);

    /// <summary>
    /// The body mod a garment of <paramref name="part"/> opens on: the one last picked for that part, if it is
    /// installed and sizes the garment; else, while no body is chosen yet, the one last picked for any part, on the
    /// same terms. Otherwise the current body stays — or, with none, the panel guesses the worn one. Writes nothing.
    /// </summary>
    /// <param name="part">The garment's part (<c>_top</c>, <c>_dwn</c>, <c>_glv</c>, <c>_sho</c>), or null for one
    /// that is not a body part.</param>
    /// <param name="current">The body mod refitted onto now, or null.</param>
    /// <param name="installed">The installed body mods, or null while they have not been found yet.</param>
    /// <param name="usable">Whether a body mod sizes this garment (see <see cref="Sizes"/>).</param>
    internal static Restore BodyToOpen(Configuration c, string? part, string? current,
                                       IReadOnlyCollection<string>? installed, Func<string, bool> usable)
    {
        if (installed == null) return new Restore(false, null);

        string? Fit(string? remembered)
        {
            if (Blank(remembered)) return null;
            string? dir = installed.FirstOrDefault(d => Same(d, remembered));
            return dir != null && usable(dir) ? dir : null;
        }

        string? pick = (part != null ? Fit(Lookup(c.RetargetBodyByPart, part)) : null)
                    ?? (Blank(current) ? Fit(c.RetargetBodyDir) : null);
        return new Restore(true, pick == null || Same(pick, current) ? null : pick);
    }

    /// <summary>
    /// Whether a body mod sizes a garment: for a body part, it has that part — or the leg or arm a shoe or glove
    /// keeping its ends is refitted by — at the garment's race (<see cref="StudioBodyChoice.Sizes"/>, the same rule
    /// the "Use …" button offers a body by); for a garment of no body part, it has any sizes at that race.
    /// </summary>
    /// <param name="offered">The parts the body mod has sizes for at this race, or null when it cannot be read.</param>
    internal static bool Sizes(IReadOnlyCollection<string>? offered, string? part, bool limbUsable)
        => offered is { Count: > 0 } && (part == null || StudioBodyChoice.Sizes(offered, part, limbUsable));

    /// <summary>A body mod picked to refit onto: remembered for the garment's part, and as the last body.</summary>
    internal static void PickedBody(Configuration c, string? part, string dir)
    {
        if (Blank(dir)) return;
        if (part != null)
        {
            c.RetargetBodyByPart ??= new(StringComparer.Ordinal);
            c.RetargetBodyByPart[part] = dir;
        }
        c.RetargetBodyDir = dir;
    }

    /// <summary>
    /// A size chosen — or, with <paramref name="rel"/> null, cleared — for <paramref name="slot"/> in
    /// <paramref name="bodyDir"/>. The body is remembered as picked for the garment's part too: a body the panel chose
    /// for the user (the worn guess) becomes theirs once they choose a size in it. Another body mod's sizes are never
    /// touched, and a body left with none is forgotten.
    /// </summary>
    internal static void ChoseSize(Configuration c, string? part, string bodyDir, string slot, string? rel)
    {
        if (Blank(bodyDir)) return;
        PickedBody(c, part, bodyDir);

        c.RetargetSizesByBody ??= new(StringComparer.Ordinal);
        string key = c.RetargetSizesByBody.Keys.FirstOrDefault(k => Same(k, bodyDir)) ?? bodyDir;
        c.RetargetSizesByBody.TryGetValue(key, out var sizes);
        if (rel != null)
        {
            sizes ??= new(StringComparer.Ordinal);
            sizes[slot] = rel;
            c.RetargetSizesByBody[key] = sizes;
        }
        else if (sizes != null)
        {
            sizes.Remove(slot);
            if (sizes.Count == 0) c.RetargetSizesByBody.Remove(key);
        }
        else if (c.RetargetSizesByBody.ContainsKey(key))
        {
            c.RetargetSizesByBody.Remove(key);   // a null entry, as a hand-edited file can hold
        }
    }

    /// <summary>The sizes remembered in <paramref name="bodyDir"/>, slot to option file; null when there are none.</summary>
    internal static IReadOnlyDictionary<string, string>? SizesOf(Configuration c, string? bodyDir)
    {
        if (Blank(bodyDir) || c.RetargetSizesByBody == null) return null;
        foreach (var (dir, sizes) in c.RetargetSizesByBody)
            if (Same(dir, bodyDir) && sizes is { Count: > 0 }) return sizes;
        return null;
    }

    /// <summary>
    /// The slots to fill, and the option file to fill each with: <paramref name="bodyDir"/>'s own remembered sizes only.
    /// </summary>
    /// <param name="bodyDir">The body mod being refitted onto now, or null while none is chosen.</param>
    /// <param name="isDrawn">
    /// Whether the panel offers that part at all for the garment now open. A part the panel leaves out — one this body
    /// mod has a single model for, or none for this garment's race — has no dropdown to show a filled-in size in, and
    /// no way to untick one, so filling it would put state where the user cannot see or reach it.
    /// </param>
    /// <param name="hasTarget">Whether that slot already has a size chosen, which is never overwritten.</param>
    internal static IEnumerable<(string Slot, string Rel)> Fillable(Configuration c, string? bodyDir,
                                                                    Func<string, bool> isDrawn, Func<string, bool> hasTarget)
    {
        // Sizes belong to the mod they were chosen in: another's are at best not found and at worst — two mods
        // laying their files out alike — a size of this mod the user never picked.
        if (SizesOf(c, bodyDir) is not { } remembered) yield break;

        foreach (var (slot, rel) in remembered.ToList())
        {
            if (hasTarget(slot)) continue;      // a choice already made, including one made moments ago
            if (!isDrawn(slot)) continue;       // no dropdown to show it in, and none to untick it from
            yield return (slot, rel);
        }
    }

    /// <summary>The garment parts a body mod is remembered for.</summary>
    private static readonly string[] GarmentParts = ["_top", "_dwn", "_glv", "_sho"];

    /// <summary>
    /// v8 -> v9: the one remembered body and its sizes (<see cref="Configuration.RetargetBodyDir"/>,
    /// <see cref="Configuration.RetargetTargets"/>) become that body's sizes, and the body is remembered for each part it
    /// had a size for — it served those parts before, so they open on it as they did. Never over what is already there:
    /// a config migrated, then saved by an older build, then loaded again keeps what the newer one wrote.
    /// </summary>
    internal static void MigrateToPerBody(Configuration c)
    {
        var old = c.RetargetTargets;
        string? body = c.RetargetBodyDir;
        if (old is { Count: > 0 } && !Blank(body) && SizesOf(c, body) == null)
        {
            c.RetargetSizesByBody ??= new(StringComparer.Ordinal);
            string key = c.RetargetSizesByBody.Keys.FirstOrDefault(k => Same(k, body)) ?? body!;
            c.RetargetSizesByBody[key] = new Dictionary<string, string>(old, StringComparer.Ordinal);

            c.RetargetBodyByPart ??= new(StringComparer.Ordinal);
            foreach (string part in GarmentParts)
                if (old.ContainsKey(part) && Blank(Lookup(c.RetargetBodyByPart, part)))
                    c.RetargetBodyByPart[part] = body!;
        }
        c.RetargetTargets = new(StringComparer.Ordinal);
    }

    private static string? Lookup(Dictionary<string, string>? map, string key)
        => map != null && map.TryGetValue(key, out string? v) ? v : null;

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
