using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Proteus.Services;

/// <summary>One selectable body option: a single model, in one equipment slot, behind one option of one group.</summary>
/// <param name="Group">The option group, as the author named it.</param>
/// <param name="Name">The option, as the author named it.</param>
/// <param name="Rel">The model file, relative to the body mod's root.</param>
/// <param name="GamePath">The game path it replaces.</param>
/// <param name="Slot">"_top", "_dwn", "_glv", "_sho" — which half of the body this is.</param>
/// <param name="Section">
/// The heading this option sits under, or "". Authors build a long single-select list by putting file-less options
/// like <c>--- DEFAULT ---</c> between the real ones, and Neolithe reuses the SAME option name under each heading —
/// there are eight options called "SFW M". The heading is therefore not decoration, it is the only thing telling two
/// identically-named sizes apart, and dropping it would put several indistinguishable entries in the picker.
/// </param>
internal sealed record BodyOption(string Group, string Section, string Name, string Rel, string GamePath, string Slot)
{
    /// <summary>What the picker shows: unique within a group, and spelled the way the author spelled it.</summary>
    public string Label => Heading.Length > 0 ? $"{Heading} · {Name}" : Name;

    /// <summary>
    /// <see cref="Section"/> without the rule the author drew around it. A heading is written <c>--- DEFAULT ---</c>
    /// because it has to stand out in a flat list of 114 radio buttons; beside an option name it reads as noise.
    /// </summary>
    private string Heading => Section.Trim(' ', '-', '–', '—', '=', '*', '~');

    /// <summary>The same, with the group in front, for a message that has no group heading of its own.</summary>
    public string FullLabel => $"{Group} / {Label}";
}

/// <summary>
/// A body mod's size options, read off its Penumbra manifest.
/// <para/>
/// No attempt is made to parse size out of an option's NAME. Neolithe's chest axis alone is a pre-multiplied cross
/// product of shape, flavour, SFW-ness and size, spelled inconsistently, with separator entries like
/// <c>--- DEFAULT ---</c> mixed in; any rule for reading it would be guessing at one author's convention. Instead every
/// option that contributes a model is offered, any source-to-target pair is allowed, and
/// <see cref="IdentityCorrespondence"/> is what refuses the pairs that are not two sizes of one mesh. That is also why
/// the separators drop out for the right reason — they carry no file — rather than by matching dashes.
/// </summary>
internal sealed record BodySizeCatalog(string ModRoot, IReadOnlyList<BodyOption> Options)
{
    /// <summary>Equipment slots a body model can occupy, longest-lived first.</summary>
    private static readonly string[] KnownSlots = ["_top", "_dwn", "_glv", "_sho"];

    /// <summary>The slots this mod actually sizes, in <see cref="KnownSlots"/> order.</summary>
    public IEnumerable<string> Slots => KnownSlots.Where(s => Options.Any(o => o.Slot == s));

    /// <summary>This mod's options for one slot, in the author's own order.</summary>
    public IReadOnlyList<BodyOption> For(string slot) => Options.Where(o => o.Slot == slot).ToList();

    /// <summary>
    /// This mod's options for one slot that fit an outfit made for <paramref name="race"/> (<c>"0101"</c>): the bodies
    /// of that race when the mod has any, else those of the same sex, else none. A body mod usually publishes one race
    /// per sex and lets the others fall back to it, so a Highlander outfit still refits against a Midlander body; but a
    /// male outfit is never offered a female body, nor a female one a male — the skeletons, the skin layouts and the
    /// path the refit is saved under all differ. Every option when the race is not known.
    /// </summary>
    public IReadOnlyList<BodyOption> For(string slot, string? race)
    {
        var all = For(slot);
        if (race == null) return all;
        var exact = all.Where(o => RaceOf(o.GamePath) == race).ToList();
        if (exact.Count > 0) return exact;
        bool male = IsMaleRace(race);
        return all.Where(o => RaceOf(o.GamePath) is { } r && IsMaleRace(r) == male).ToList();
    }

    /// <summary>The slots this mod sizes for an outfit made for <paramref name="race"/> — see <see cref="For(string, string?)"/>.</summary>
    public IEnumerable<string> SlotsFor(string? race) => KnownSlots.Where(s => For(s, race).Count > 0);

    /// <summary>The race code an equipment model's game path names (<c>"0201"</c> for <c>…/c0201e0141_top.mdl</c>), or null.</summary>
    internal static string? RaceOf(string gamePath)
        => RaceCode.Match(gamePath) is { Success: true } m ? m.Groups[1].Value : null;

    private static readonly System.Text.RegularExpressions.Regex RaceCode = new(@"(?:^|/)c(\d{4})e\d{4}_",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Whether a race code is a male one. The codes come in pairs, male then female — 01 and 02 are Midlander, 03 and
    /// 04 Highlander, on to 17 and 18 for Viera — and the last two digits are the NPC variant (<c>0104</c> is still a
    /// Midlander man), so an odd hundreds means male.
    /// </summary>
    internal static bool IsMaleRace(string race) => int.TryParse(race, out int code) && code / 100 % 2 == 1;

    /// <summary>
    /// True when the mod offers a choice of body models at all — what the mod picker filters on. Only smallclothes
    /// paths count (see <see cref="IsBodyModel"/>), so an outfit with its own size group is not mistaken for a body.
    /// </summary>
    public bool IsBody => Options.Count > 0;

    /// <summary>
    /// Read a mod's body options.
    /// <para/>
    /// Walks the groups directly rather than through <c>PenumbraModMeta.ReadAllRedirects</c>, for two reasons that
    /// both come down to option ORDER. A file-less option is a heading and has to be remembered for the options that
    /// follow it, and <c>ReadAllRedirects</c> never yields one because it has no files. And option names repeat within
    /// a group, so its <c>"Group / Option"</c> source string cannot say which of eight options called "SFW M" a
    /// redirect belongs to.
    /// </summary>
    public static BodySizeCatalog Read(string modRoot)
    {
        var options = new List<BodyOption>();

        // Files the mod ships with no option at all. A body pack can be a single replacement and nothing else — one
        // smallclothes model, no groups — and read only through the groups such a mod has no options, so IsBody is
        // false and it never reaches the picker. It is still a body, and it ships exactly one, which is what this
        // stands for. Before the groups, so a mod that has both lists its plain files first.
        if (PenumbraModMeta.TryReadDefaultData(modRoot) is { } dflt)
            foreach (var (gamePath, rel) in dflt.Files)
            {
                if (rel is not { Length: > 0 }) continue;
                if (!IsBodyModel(gamePath) || SlotOf(gamePath) is not { } dfltSlot) continue;
                options.Add(new BodyOption(DefaultGroupName, Section: "", DefaultSizeName, rel, gamePath, dfltSlot));
            }

        var groups = PenumbraModMeta.TryReadGroups(modRoot);
        if (groups == null) return new BodySizeCatalog(modRoot, options);

        foreach (var (groupName, group) in groups)
        {
            if (!group.TryGetProperty("Options", out var list) || list.ValueKind != JsonValueKind.Array) continue;

            string section = "";
            foreach (var option in list.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object) continue;
                string name = option.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";

                bool anyFile = option.TryGetProperty("Files", out var files)
                            && files.ValueKind == JsonValueKind.Object
                            && files.EnumerateObject().Any();
                if (!anyFile)
                {
                    // Carries nothing at all, so it exists to label the options below it.
                    section = name;
                    continue;
                }

                foreach (var file in files.EnumerateObject())
                {
                    if (file.Value.ValueKind != JsonValueKind.String) continue;
                    if (file.Value.GetString() is not { Length: > 0 } rel) continue;
                    if (!IsBodyModel(file.Name) || SlotOf(file.Name) is not { } slot) continue;

                    options.Add(new BodyOption(groupName, section, name, rel, file.Name, slot));
                }
            }
        }

        return new BodySizeCatalog(modRoot, options);
    }

    /// <summary>
    /// Whether a game path is the body itself: the smallclothes model (<c>e0000</c>) of some slot and race, which is
    /// what every body mod replaces. An outfit's models are other set ids, so an outfit mod with a size group of its
    /// own — which is every outfit this tool refits — does not qualify.
    /// <para/>
    /// The Emperor's New set (<c>e0279</c>) is left out on purpose, although body mods replace it too: they point it at
    /// the very same files as <c>e0000</c>, so counting it lists every size twice.
    /// </summary>
    internal static bool IsBodyModel(string gamePath) => BodyModelPath.IsMatch(gamePath);

    /// <summary>
    /// What a body shipped with no option is filed under. The picker has already named the mod by the time these are
    /// shown, so this only has to say WHERE in the mod it came from — and "no option" is the honest answer.
    /// </summary>
    internal const string DefaultGroupName = "No options";

    /// <inheritdoc cref="DefaultGroupName"/>
    internal const string DefaultSizeName = "The mod's own body";

    private static readonly System.Text.RegularExpressions.Regex BodyModelPath = new(
        @"^chara/equipment/e0000/model/c\d{4}e0000_(top|dwn|glv|sho)\.mdl$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Which half of the body a game path is, or null when it is not an equipment model at all.</summary>
    internal static string? SlotOf(string gamePath)
    {
        foreach (string slot in KnownSlots)
            if (gamePath.EndsWith(slot + ".mdl", StringComparison.OrdinalIgnoreCase)) return slot;
        return null;
    }

    /// <summary>
    /// The option's file on disk. Penumbra writes these lowercase with backslashes while the real folders are
    /// mixed-case, and resolves them case-insensitively; so must this.
    /// </summary>
    public string PathOf(BodyOption option) => ResolveCaseInsensitive(ModRoot, option.Rel);

    internal static string ResolveCaseInsensitive(string root, string rel)
    {
        string direct = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)
                                              .Replace('\\', Path.DirectorySeparatorChar));
        if (File.Exists(direct)) return direct;

        string at = root;
        foreach (string segment in rel.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(at, segment);
            if (Directory.Exists(next) || File.Exists(next)) { at = next; continue; }

            string? match = null;
            try
            {
                match = Directory.EnumerateFileSystemEntries(at)
                                 .FirstOrDefault(e => string.Equals(Path.GetFileName(e), segment,
                                                                    StringComparison.OrdinalIgnoreCase));
            }
            catch { /* unreadable directory: fall through to the direct path and let the caller fail to open it */ }

            if (match == null) return direct;
            at = match;
        }
        return at;
    }
}
