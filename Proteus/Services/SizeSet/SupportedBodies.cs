using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// The body mods a mod author can add every size of in one go (<see cref="SizeSetRefit"/>), and which of each mod's
/// options those sizes are — the mod packer's own table (<c>modpacker/refit.py</c>), carried over so a size made here is
/// the size the packer makes, under the same option name.
/// <para/>
/// A curated list, not every installed body: Neolithe alone has 114 chests, and "every size" of it has to mean the four
/// a pack offers, not all of them. Each body is found among the installed mods by its FILES (<see cref="Find"/>), never by
/// folder name — Penumbra lets a player name a mod's folder anything at import.
/// </summary>
internal static class SupportedBodies
{
    /// <summary>One size of a body: the option name it is saved under, and the models it is made on.</summary>
    /// <param name="Name">The size as the option names it, after the body: "S", "WC", "Medium"; "" for a body of one size,
    /// whose option is the body's name alone.</param>
    /// <param name="Rel">The model of the garment's own slot, relative to the body mod.</param>
    /// <param name="LegsRel">For a chest garment, the legs its hem moves onto, in the same body mod; null for none.</param>
    internal sealed record Size(string Name, string Rel, string? LegsRel);

    /// <summary>One body's sizes for one slot.</summary>
    /// <param name="Key">What the author ticks; the same for a body's plain and YAS sizes.</param>
    /// <param name="Label">The body in an option name: "Rue" makes "Rue S".</param>
    /// <param name="Slot">"_top" or "_dwn".</param>
    /// <param name="Yas">Weighted to Yet Another Skeleton.</param>
    /// <param name="Family">
    /// A garment made on this very body mod takes its sizes from the option it was made on rather than from
    /// <see cref="Sizes"/>: a top made on Neolithe's SFW Almond XS gets Almond S, M and L, not the NSFW sizes the table
    /// falls back to. Neolithe files its sizes as one name with the size at the end, which is what makes that possible.
    /// </param>
    /// <param name="Race">The race its models are (<c>"0201"</c>, <c>"0101"</c>): only a garment of that race is refitted
    /// onto it.</param>
    internal sealed record Body(string Key, string Label, string Slot, bool Yas, IReadOnlyList<Size> Sizes,
                                bool Family = false, string Race = "0201");

    /// <summary>The bodies an author can choose, in the order their sizes are added to a group: women's, then men's.</summary>
    internal static readonly IReadOnlyList<(string Key, string Label, bool Male)> Choices =
    [
        ("neolithe", "Neolithe", false),
        ("bibo", "Bibo", false),
        ("rue", "Rue", false),
        ("yab", "YAB", false),
        ("yab-buff", "YAB Buff", false),
        ("tre", "Tre", false),
        ("rosaline", "Rosaline", false),
        ("tbse", "TBSE", true),
        ("tbse-x", "TBSE-X", true),
    ];

    /// <summary>What a size is saved as: "Rue S", or the body alone for a body of one size ("TBSE").</summary>
    internal static string OptionName(Body body, Size size)
        => size.Name.Length > 0 ? body.Label + " " + size.Name : body.Label;

    private static readonly string[] Words = ["small", "medium", "large"];
    private static readonly string[] Letters = ["S", "M", "L"];

    private static string Word(string letter) => letter switch { "S" => "small", "M" => "medium", "L" => "large", _ => "" };

    // Neolithe's plain NSFW chest, the fallback when a garment was not made on Neolithe; its legs by size word.
    private const string NeoChest = "default chest - smallclothes/nsfw {0}.mdl";
    private const string NeoLegs = "default legs - smallclothes/sfw {0}.mdl";

    // Rue+ and Yet Another Body+ file every size as c0201e0000_top.mdl in a folder of its own. Their hips do not change
    // with the chest size, so one legs option, Watermelon Crushers A, serves them all.
    private const string HsChest = "files/chest - smallclothes/{0}/chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string HsLegs = "files/legs - smallclothes/{0}/chara/equipment/e0000/model/c0201e0000_dwn.mdl";

    // Tre's NSFW chests and Gen A legs: Neolithe's SFW legs are cut differently at the crotch from Tre's SFW legs and are
    // refused; they pair with Gen A. Tre has no Small legs, so S and M go to Medium. Its YAS release is a mod of its own
    // and numbers its folders differently.
    private static readonly Dictionary<string, string> TreChest = new()
    {
        ["S"] = "common/17/c0201e0000_top.mdl", ["M"] = "common/18/c0201e0000_top.mdl", ["L"] = "common/19/c0201e0000_top.mdl",
    };
    private static readonly Dictionary<string, string> TreLegs = new()
    {
        ["S"] = "common/28/c0201e0000_dwn.mdl", ["M"] = "common/28/c0201e0000_dwn.mdl", ["L"] = "common/23/c0201e0000_dwn.mdl",
    };
    private static readonly Dictionary<string, string> TreYasChest = new()
    {
        ["S"] = "files/common/3/c0201e0000_top.mdl", ["M"] = "files/common/4/c0201e0000_top.mdl",
        ["L"] = "files/common/5/c0201e0000_top.mdl",
    };
    private static readonly Dictionary<string, string> TreYasLegs = new()
    {
        ["S"] = "files/common/15/c0201e0000_dwn.mdl", ["M"] = "files/common/15/c0201e0000_dwn.mdl",
        ["L"] = "files/common/10/c0201e0000_dwn.mdl",
    };

    // Bibo+'s Nude chest; its Type A legs pair with Neolithe's completely where its SFW Undies are cut differently. Its
    // hips do change with size.
    private const string BiboChest = "breasts - small clothes/nude - {0}/chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string BiboLegs = "bottoms - small clothes/type a - {0}/chara/equipment/e0000/model/c0201e0000_dwn.mdl";

    // Rosaline's Medium, Large and Coconut chests, in that order — the order their options are added in — and the
    // Vagina A legs under its "Hip Dips" heading (the user's choice; the same name is listed again under "Round Hips",
    // common/21), worn under every one. Its folders are numbered like Tre's YAS release — files/common/5 is both
    // Rosaline's Medium chest and YAS Tre's Large one — so Find tells them apart by name.
    private static readonly (string Name, string Rel)[] RosalineChest =
    [
        ("M", "files/common/5/c0201e0000_top.mdl"),
        ("L", "files/common/6/c0201e0000_top.mdl"),
        ("Coconut", "files/common/7/c0201e0000_top.mdl"),
    ];
    private const string RosalineLegs = "files/common/13/c0201e0000_dwn.mdl";

    // The men's bodies: one chest each (Midlander, which Miqo'te, Au Ra, Elezen and Viera men share) and their SFW legs.
    // Neither has chest sizes; TBSE-X's many legs are crotches, not sizes, and only SFW is offered.
    private const string TbseChest = "common/1/c0101e0000_top.mdl";
    private const string TbseLegs = "p2/step 6- smallclothes options/sfw/chara/equipment/e0000/model/c0101e0000_dwn.mdl";
    private const string TbseXChest =
        "p1/torso/m midlander miqo'te au'ra elezen viera/chara/equipment/e0000/model/c0101e0000_top.mdl";
    private const string TbseXLegs = "p1/legs (smallclothes legs)/sfw/chara/equipment/e0000/model/c0101e0000_dwn.mdl";

    private static Body Chests(string key, string label, bool yas, Func<string, string> chest, Func<string, string?> legs,
                               bool family = false)
        => new(key, label, "_top", yas, Letters.Select(s => new Size(s, chest(s), legs(s))).ToList(), family);

    private static readonly Body[] All =
    [
        // ── chest garments ──
        new("neolithe", "Neolithe", "_top", false,
            [new Size("XS", string.Format(NeoChest, "xs"), null),
             ..Letters.Select(s => new Size(s, string.Format(NeoChest, s.ToLowerInvariant()), string.Format(NeoLegs, Word(s))))],
            Family: true),
        Chests("bibo", "Bibo", false, s => string.Format(BiboChest, Word(s)), s => string.Format(BiboLegs, Word(s))),
        Chests("rue", "Rue", false, s => string.Format(HsChest, Word(s)),
               _ => string.Format(HsLegs, "watermelon crushers - a")),
        Chests("rue", "Rue", true, s => string.Format(HsChest, "yiggle - " + Word(s)),
               _ => string.Format(HsLegs, "yiggle - watermelon crushers - a")),
        Chests("yab", "YAB", false, s => string.Format(HsChest, Word(s)),
               _ => string.Format(HsLegs, "watermelon crushers - a")),
        Chests("yab", "YAB", true, s => string.Format(HsChest, "yiggle - " + Word(s)),
               _ => string.Format(HsLegs, "yiggle - watermelon crushers - a")),
        Chests("yab-buff", "YAB Buff", false, s => string.Format(HsChest, "buff - " + Word(s)),
               _ => string.Format(HsLegs, "watermelon crushers - a")),
        Chests("yab-buff", "YAB Buff", true, s => string.Format(HsChest, "yiggle - buff - " + Word(s)),
               _ => string.Format(HsLegs, "yiggle - watermelon crushers - a")),
        Chests("tre", "Tre", false, s => TreChest[s], s => TreLegs[s]),
        Chests("tre", "Tre", true, s => TreYasChest[s], s => TreYasLegs[s]),
        new("rosaline", "Rosaline", "_top", false,
            RosalineChest.Select(c => new Size(c.Name, c.Rel, RosalineLegs)).ToList()),

        // ── legs garments: sized the way legs mods name them — Rue and YAB by their hips, Tre by its two sizes ──
        new("neolithe", "Neolithe", "_dwn", false,
            Letters.Select(s => new Size(s, string.Format(NeoLegs, Word(s)), null)).ToList(), Family: true),
        new("bibo", "Bibo", "_dwn", false, Letters.Select(s => new Size(s, string.Format(BiboLegs, Word(s)), null)).ToList()),
        new("rue", "Rue", "_dwn", false, [new Size("WC", string.Format(HsLegs, "watermelon crushers - a"), null)]),
        new("rue", "Rue", "_dwn", true, [new Size("WC", string.Format(HsLegs, "yiggle - watermelon crushers - a"), null)]),
        new("yab", "YAB", "_dwn", false,
            [new Size("WC", string.Format(HsLegs, "watermelon crushers - a"), null),
             new Size("SC", string.Format(HsLegs, "skull crushers - a"), null)]),
        new("yab", "YAB", "_dwn", true,
            [new Size("WC", string.Format(HsLegs, "yiggle - watermelon crushers - a"), null),
             new Size("SC", string.Format(HsLegs, "yiggle - skull crushers - a"), null)]),
        new("tre", "Tre", "_dwn", false, [new Size("Medium", TreLegs["M"], null), new Size("Large", TreLegs["L"], null)]),
        new("tre", "Tre", "_dwn", true, [new Size("Medium", TreYasLegs["M"], null), new Size("Large", TreYasLegs["L"], null)]),
        new("rosaline", "Rosaline", "_dwn", false, [new Size("", RosalineLegs, null)]),

        // ── men's ──
        new("tbse", "TBSE", "_top", false, [new Size("", TbseChest, TbseLegs)], Race: "0101"),
        new("tbse", "TBSE", "_dwn", false, [new Size("", TbseLegs, null)], Race: "0101"),
        new("tbse-x", "TBSE-X", "_top", false, [new Size("", TbseXChest, TbseXLegs)], Race: "0101"),
        new("tbse-x", "TBSE-X", "_dwn", false, [new Size("", TbseXLegs, null)], Race: "0101"),
    ];

    /// <summary>A body's sizes for one slot, plain or YAS; null when the table has none (YAB Buff has no legs of its own).</summary>
    internal static Body? Of(string key, string slot, bool yas)
        => All.FirstOrDefault(b => b.Key == key && b.Slot == slot && b.Yas == yas);

    /// <summary>
    /// The installed mod holding <paramref name="body"/>: the one whose options carry most of its sizes, and of two that
    /// carry as many — an old version left installed beside a new one — the later by name. Null when none has any.
    /// <para/>
    /// The legs a chest's hem goes to count too: a path as plain as TBSE's <c>common/1/c0101e0000_top.mdl</c> may well be
    /// some other mod's, but not together with TBSE's legs.
    /// <para/>
    /// Files alone cannot tell every body apart: Rue+ and Yet Another Body+ file their sizes under the very same paths,
    /// and Neolithe's YAS release copies the plain one's. So among the mods holding the files, one whose name says which
    /// body it is (<see cref="NameHints"/>) comes first, then one whose name says YAS exactly when the body is YAS — never
    /// Neolithe's YAS release for its plain sizes — and only then the count.
    /// </summary>
    internal static BodyModIndex.Entry? Find(Body body, IEnumerable<BodyModIndex.Entry> installed)
    {
        var wanted = body.Sizes.SelectMany(s => new[] { s.Rel, s.LegsRel }).OfType<string>()
                         .Select(RefitCore.Normal).ToHashSet(StringComparer.Ordinal);
        var hints = NameHints.GetValueOrDefault(body.Key, []);
        bool Says(BodyModIndex.Entry e, string word) => e.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                                                        || e.Dir.Contains(word, StringComparison.OrdinalIgnoreCase);
        return installed.Select(e => (Entry: e, Count: e.Catalog.Options
                                                          .Select(o => RefitCore.Normal(o.Rel))
                                                          .Where(wanted.Contains).Distinct().Count()))
                        .Where(m => m.Count > 0)
                        .OrderByDescending(m => hints.Any(h => Says(m.Entry, h)))
                        .ThenByDescending(m => Says(m.Entry, "yas") == body.Yas)
                        .ThenByDescending(m => m.Count)
                        .ThenByDescending(m => m.Entry.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(m => m.Entry)
                        .FirstOrDefault();
    }

    /// <summary>
    /// Words a body's mod is usually named with, for telling apart mods that hold the same files (see <see cref="Find"/>).
    /// Only a tie-break among mods that DO hold the body's files, so a loose word — "tre" — cannot pull in an outfit.
    /// </summary>
    private static readonly Dictionary<string, string[]> NameHints = new(StringComparer.Ordinal)
    {
        ["neolithe"] = ["neolithe"],
        ["bibo"] = ["bibo"],
        ["rue"] = ["rue"],
        ["yab"] = ["yet another body", "yab"],
        ["yab-buff"] = ["yet another body", "yab"],
        ["tre"] = ["tre"],
        ["rosaline"] = ["rosaline"],
        ["tbse"] = ["body se", "tbse"],
        ["tbse-x"] = ["se-x", "tbse-x", "tbsex"],
    };

    /// <summary>Whether any of a choice's sizes — chest or legs, plain or YAS — are in an installed mod.</summary>
    internal static bool Installed(string key, IReadOnlyList<BodyModIndex.Entry> installed)
        => All.Any(b => b.Key == key && Find(b, installed) != null);

    /// <summary>
    /// <paramref name="body"/>'s sizes for a garment made on <paramref name="sourceRel"/>, when that is an option of
    /// the very same body mod and its file names its size at the end: the same family's other sizes — "sfw almond xs.mdl"
    /// gives "sfw almond s.mdl" and so on, legs "gen a small.mdl" gives "gen a large.mdl". Otherwise the table's own.
    /// A size the family does not have is left out by the caller, which finds no option for it.
    /// </summary>
    internal static IReadOnlyList<Size> SizesFor(Body body, string? sourceRel)
    {
        if (!body.Family || sourceRel == null || Template(sourceRel) is not { } template) return body.Sizes;
        return body.Sizes.Select(s => s with
        {
            Rel = template.Word ? string.Format(template.Pattern, Word(s.Name)) : string.Format(template.Pattern,
                                                                                               s.Name.ToLowerInvariant()),
        }).Where(s => s.Rel.Length > 0 && (!template.Word || Word(s.Name).Length > 0)).ToList();
    }

    /// <summary>
    /// A file name's size made a placeholder: <c>…/sfw almond xs.mdl</c> → <c>…/sfw almond {0}.mdl</c> by letter, or
    /// <c>…/gen a small.mdl</c> → <c>…/gen a {0}.mdl</c> by word. Null when the name ends in neither.
    /// </summary>
    internal static (string Pattern, bool Word)? Template(string rel)
    {
        rel = rel.Replace('\\', '/');
        int slash = rel.LastIndexOf('/');
        string folder = slash >= 0 ? rel[..(slash + 1)] : "";
        string name = rel[(slash + 1)..];
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        int space = stem.LastIndexOf(' ');
        if (space <= 0) return null;
        string head = stem[..space], last = stem[(space + 1)..].ToLowerInvariant();
        string pattern = folder + head.Replace("{", "{{").Replace("}", "}}") + " {0}" + ext;
        if (last is "xs" or "s" or "m" or "l" or "xl") return (pattern, false);
        if (Words.Contains(last)) return (pattern, true);
        return null;
    }

    /// <summary>
    /// The legs a chest size of the same family moves its hem to: the source's legs with their size word swapped —
    /// <c>sfw small.mdl</c> to <c>sfw large.mdl</c> for L. Null for a size with no legs word (XS) or legs whose name ends
    /// in none.
    /// </summary>
    internal static string? FamilyLegs(string sourceLegsRel, string size)
    {
        string word = Word(size);
        if (word.Length == 0 || Template(sourceLegsRel) is not { Word: true } template) return null;
        return string.Format(template.Pattern, word);
    }

    /// <summary>Whether a model's bones are Yet Another Skeleton's — names the game's own skeleton has none of.</summary>
    internal static bool IsYas(IEnumerable<string> bones)
        => bones.Any(b => b.StartsWith("iv_", StringComparison.Ordinal) || b.StartsWith("ya_", StringComparison.Ordinal));
}
