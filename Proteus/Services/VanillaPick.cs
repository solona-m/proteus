using System;

namespace Proteus.Services;

/// <summary>
/// A piece of the game's own gear, opened in the Studio with no mod behind it.
/// <para/>
/// Its game path is its whole identity: there is no mod folder, no relative file, and no manifest row. Everything the
/// tab needs about it — which slot it fills, which item it is, what to call the mod a refit of it goes into — is read
/// off that one string and the game's item sheet.
/// </summary>
/// <param name="GamePath">Where the game draws it from, which is also what a refit of it redirects.</param>
/// <param name="ItemName">What the game calls it, or its set tag when the sheet cannot be read.</param>
/// <param name="SetTag">"e6085" — the set, as modders write it.</param>
/// <param name="Label">Slot and item together: "Body — Ala Mhigan Coat of Fending".</param>
internal readonly record struct VanillaPick(string GamePath, string ItemName, string SetTag, string Label)
{
    /// <summary>
    /// Read a drawn model's game path as a piece of equipment, or null when it is not one — a body, a face, a
    /// weapon, or anything else whose path does not name an equipment set.
    /// </summary>
    /// <param name="gamePath">The path the game draws the model from.</param>
    /// <param name="lookup">
    /// Item names by (equip-slot category, set id) — <see cref="ItemNames.Lookup"/>. One that answers null costs a
    /// nicer label and nothing else.
    /// </param>
    internal static VanillaPick? From(string gamePath, Func<int, int, string?> lookup)
    {
        if (ContentSlot.Parse(gamePath) is not { } p) return null;

        // Equipment only, and said of the PATH: a body model is called "..._top.mdl" too, and the slot suffix alone
        // would read the character's own body as a garment to refit.
        if (!gamePath.Replace('\\', '/').StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase)) return null;

        // A refit needs a body slot to sit in, and the writer needs an item to name a mod after.
        if (BodySizeCatalog.SlotOf(gamePath) is null) return null;
        if (ContentSlot.SetIdOf(p.SetTag) is not { } setId) return null;

        string? name = null;
        try
        {
            if (p.Category != 0) name = lookup(p.Category, setId);
        }
        catch (Exception)
        {
            // The sheet is the game's, and a reader that throws is no worse than one that answers null.
        }

        return new VanillaPick(gamePath, string.IsNullOrWhiteSpace(name) ? p.SetTag : name!, p.SetTag,
                               ContentSlot.Label(p, name));
    }
}
