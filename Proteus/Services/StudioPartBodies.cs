using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// A body mod of its own for one part of a garment refitted in the Studio's Body size tool — a long dress's legs onto
/// another body than its chest — decided away from the panel so it can be tested without ImGui.
/// <para/>
/// The garment's own part always goes onto the body picked above ("Refit onto body"); every other part goes there too
/// unless it is given a body of its own. Only the refit-onto side: a garment is read as made on one body.
/// <para/>
/// Only the whole garment's refit takes part bodies (<see cref="Offered"/>). A shoe or glove keeping its ends, or a race
/// picked under "Refit for", was measured against one body; there the part bodies are set aside, not forgotten, and the
/// parts holding one sit out of the refit until they can be used again (<see cref="TakingPart"/>).
/// </summary>
internal static class StudioPartBodies
{
    /// <summary>The body picked for a part as kept: none when it is the body every part goes onto anyway.</summary>
    internal static string? Override(string? mainDir, string pickedDir)
        => string.Equals(mainDir, pickedDir, StringComparison.OrdinalIgnoreCase) ? null : pickedDir;

    /// <summary>Whether part bodies can be used: the whole garment refitted, with no race picked under "Refit for".</summary>
    internal static bool Offered(RetargetInputs.Layout layout, bool explicitRace) => !layout.KeepEnds && !explicitRace;

    /// <summary>The part bodies in force: those kept, while they can be used; none otherwise. Never cleared here.</summary>
    internal static IReadOnlyDictionary<string, string> Effective(IReadOnlyDictionary<string, string> stored, bool offered)
        => offered ? stored : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The parts taking part in the refit: <paramref name="chosen"/>, less every part holding a body of its own while
    /// part bodies cannot be used. Such a part's sizes are that body's, and are never read against the main body's —
    /// two body mods laid out alike (Rue and LaRue) would silently give the main body's file.
    /// </summary>
    internal static List<string> TakingPart(IEnumerable<string> chosen, IReadOnlyDictionary<string, string> stored,
                                            bool offered)
        => offered ? chosen.ToList() : chosen.Where(s => !stored.ContainsKey(s)).ToList();

    /// <summary>
    /// The sizes chosen to refit onto, less those of every part set aside (see <see cref="TakingPart"/>): what a refit is
    /// assembled, keyed and named from, so a set-aside part leaves no trace in it.
    /// </summary>
    internal static IReadOnlyDictionary<string, List<BodyOption>> Active(IReadOnlyDictionary<string, List<BodyOption>> to,
                                                                         IReadOnlyDictionary<string, string> stored, bool offered)
        => offered || stored.Count == 0
            ? to
            : to.Where(p => !stored.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    /// <summary>Whether a part is set aside: it holds a body of its own that cannot be used now.</summary>
    internal static bool SetAside(string slot, IReadOnlyDictionary<string, string> stored, bool offered)
        => !offered && stored.ContainsKey(slot);

    /// <summary>A part's body to refit onto: its folder and its sizes, or null sizes when it cannot be read.</summary>
    internal readonly record struct Target(string? Dir, BodySizeCatalog? Catalog);

    /// <summary>
    /// The body a part goes onto: the garment's own part (<paramref name="primary"/>, when given) always the main one;
    /// another its own, when it has one in <paramref name="bodies"/>; else the main one. The one place a part's body is
    /// looked up, so no lookup can miss it.
    /// </summary>
    internal static Target Resolve(string slot, string? primary, string? mainDir, BodySizeCatalog? main,
                                   IReadOnlyDictionary<string, string> bodies, Func<string, BodySizeCatalog?> catalogOf)
        => slot != primary && bodies.TryGetValue(slot, out string? dir) ? new Target(dir, catalogOf(dir)) : new Target(mainDir, main);

    /// <summary>
    /// Each part's sizes at a new race on the body KEPT for it, set aside or not — what a race change keeps a chosen size
    /// by (see <see cref="RetargetInputs.StillOffered"/>).
    /// </summary>
    internal static Func<string, IReadOnlyList<BodyOption>> SizesAtRace(string? primary, string? mainDir, BodySizeCatalog? main,
                                                                        IReadOnlyDictionary<string, string> stored,
                                                                        Func<string, BodySizeCatalog?> catalogOf, string? race)
        => slot => Resolve(slot, primary, mainDir, main, stored, catalogOf).Catalog?.For(slot, race) ?? [];

    /// <summary>
    /// Whether the refit is one between body mods: some part goes onto another body mod than the garment was made on,
    /// or a race was picked (its bent bodies are another rig). With no part bodies this is the rule the tool always had:
    /// a "made for" body other than the refit-onto one.
    /// </summary>
    internal static bool Across(string? sourceDir, string? mainDir, IEnumerable<string?> partDirs, bool explicitRace)
        => explicitRace || AutoRefitTargets.Across(sourceDir ?? "", partDirs.Prepend(mainDir).Select(d => d ?? ""));

    /// <summary>
    /// Whether the garment's own part stays on the body mod it was made on while a part taking part goes onto another.
    /// The refit cannot do that yet: the moved part's skin is lost or folded (measured; see
    /// <see cref="AutoRefitTargets.PlanParts"/>, which keeps such a part on the garment's body instead).
    /// </summary>
    /// <param name="partDirs">The bodies of the other parts taking part — only those (see <see cref="TakingPart"/>).</param>
    internal static bool KeptOnOwnBody(string? sourceDir, string? mainDir, IEnumerable<string?> partDirs)
        => string.Equals(sourceDir, mainDir, StringComparison.OrdinalIgnoreCase)
           && partDirs.Any(d => !string.Equals(d, mainDir, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a part's body is on the other side of the Rue line from the main one (see <see cref="AutoRefitTargets.IsRue"/>).
    /// Only warned of in the Studio: the refit is watched there, and previewed before it is saved.
    /// </summary>
    internal static bool RueApart(string mainDir, string? mainName, string partDir, string? partName)
        => AutoRefitTargets.IsRue(mainDir, mainName) != AutoRefitTargets.IsRue(partDir, partName);

    /// <summary>
    /// Every body the garment's skin may be drawn by, the lead's first (RefitCore.MatchSkinToBody with a list of bodies,
    /// as the automatic refit calls it):
    /// the lead's file, then each other part's on a body other than the main one, once.
    /// </summary>
    internal static List<string> TargetPaths(string leadPath, string? mainDir, IEnumerable<(string? Dir, string Path)> parts)
    {
        var paths = new List<string> { leadPath };
        foreach (var (dir, path) in parts)
            if (!string.Equals(dir, mainDir, StringComparison.OrdinalIgnoreCase)
                && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                paths.Add(path);
        return paths;
    }

    /// <summary>
    /// One refit's sizes, each with the name of the body it goes onto, for RefitCore.OptionName's list of parts.
    /// The labels are the option's own (see <see cref="RetargetInputs.ToLabels"/>); when they are not one per part (a
    /// skin-only redraw) they are all the main body's.
    /// </summary>
    internal static List<(string Body, string Label)> NamedParts(IReadOnlyList<string> chosen, IReadOnlyList<string> labels,
                                                                 string mainName, Func<string, string> bodyNameOf)
        => labels.Count == chosen.Count
            ? chosen.Select((s, i) => (bodyNameOf(s), labels[i])).ToList()
            : labels.Select(l => (mainName, l)).ToList();

    /// <summary>
    /// Whether a part's size made for and its size refitted onto are one size of one body mod: the same file within the
    /// same mod. With no part bodies this is the check the tool always made within one mod; across two mods it is never
    /// true, however alike they lay their files out.
    /// </summary>
    internal static bool SameSize(string? sourceDir, BodyOption source, string? targetDir, BodyOption target)
        => string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase)
           && string.Equals(source.Rel, target.Rel, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether two model paths are one file: by the whole path, never the file within its mod — two body mods
    /// laid out alike share those.</summary>
    internal static bool SameFile(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a part already has its choice, which a remembered size never fills over (see
    /// <see cref="RememberedTargets.Fillable"/>): a size chosen, or a body of its own — the remembered sizes are the main
    /// body's files, never that one's.
    /// </summary>
    internal static bool HasChoice<T>(string slot, IReadOnlyDictionary<string, T> to, IReadOnlyDictionary<string, string> stored)
        => to.ContainsKey(slot) || stored.ContainsKey(slot);

    /// <summary>The part bodies with <paramref name="slot"/>'s taken out: the part is left out of the refit.</summary>
    internal static Dictionary<string, string> Without(IReadOnlyDictionary<string, string> stored, string slot)
        => stored.Where(p => p.Key != slot).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
}
