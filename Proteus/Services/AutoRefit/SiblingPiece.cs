using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Proteus.Services;

/// <summary>
/// The chest or legs piece its author ships in the same option as another piece — the "sibling" a glove, shoe or
/// accessory is sized by when its own body skin cannot say.
/// <para/>
/// Geometric's cropped jacket is its hands model, sized with the top in each option of its "Chest Size" group. Its arm
/// skin matched no body, so its size was read off how the jacket's cloth sat on the chest — the tightest chest it
/// cleared — and a loose jacket clears a smaller bust: it read Smallclothes where the author made it on Medium, and the
/// refitted bust came through it. The top beside it in the same option reads Medium from its own skin.
/// <para/>
/// Only the SAME option of the same mod: any top worn from the same mod may be another outfit's. Proteus's own refit
/// options are not the author's — a refit option copies the other files of the option it was cut from, the glove's
/// among them — and never count.
/// </summary>
internal static class SiblingPiece
{
    /// <summary>A sibling: the game path it is worn at, its file in the mod, and the option ("Group / Option", or "" for
    /// the mod's default files) that serves it beside the piece.</summary>
    internal sealed record Candidate(string GamePath, string File, string Source);

    /// <summary>More siblings than this are not read at all: a file every size of a pack-made outfit shares would read
    /// thirty tops on its first equip, only to find them disagree.</summary>
    internal const int MostRead = 3;

    /// <summary>
    /// The author's <paramref name="limb"/> files of <paramref name="race"/> served by every option that serves
    /// <paramref name="wornRel"/>, distinct by file and in the mod's order. Empty when there are none, or more than
    /// <see cref="MostRead"/> (noted).
    /// </summary>
    /// <param name="limb">"_top" or "_dwn".</param>
    /// <param name="record">The mod's own refits, whose options are ignored; null for none.</param>
    internal static IReadOnlyList<Candidate> Find(IReadOnlyList<PenumbraModMeta.Redirect> redirects,
                                                  BodyRetargetWriter.Record? record, string wornRel, string limb,
                                                  string race, Action<string>? note = null)
    {
        var ours = new HashSet<string>(record?.Options.Select(e => record.GroupOf(e) + " / " + e.Name) ?? [],
                                       StringComparer.OrdinalIgnoreCase);
        string worn = RefitCore.Normal(wornRel);
        var sources = redirects.Where(d => RefitCore.Normal(d.File) == worn && !ours.Contains(d.Source))
                               .Select(d => d.Source)
                               .ToHashSet(StringComparer.Ordinal);
        if (sources.Count == 0) return [];

        var limbPath = new Regex($@"^chara/equipment/e\d{{4}}/model/c{Regex.Escape(race)}e\d{{4}}{Regex.Escape(limb)}\.mdl$",
                                 RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string retarget = RefitCore.Normal(BodyRetargetWriter.Subfolder) + "/";
        var found = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in redirects)
        {
            if (!sources.Contains(d.Source) || !limbPath.IsMatch(d.GamePath)) continue;
            string file = RefitCore.Normal(d.File);
            if (file.StartsWith(retarget, StringComparison.Ordinal) || !seen.Add(file)) continue;
            found.Add(new Candidate(d.GamePath, d.File, d.Source));
        }
        if (found.Count > MostRead)
        {
            note?.Invoke($"{found.Count} {limb} pieces share its options — more than {MostRead}, none read");
            return [];
        }
        return found;
    }

    /// <summary>
    /// One reading out of the siblings', read in order and stopped at the first that is not clear or does not agree:
    /// every sibling must read the same body and the same size. Null otherwise (noted).
    /// </summary>
    /// <param name="read">Each candidate's reading, in order — called lazily, so a disagreement stops the reading.</param>
    internal static AutoRefitDecisions.Pick? Agree(IReadOnlyList<Candidate> candidates,
                                                   Func<Candidate, AutoRefitDecisions.Pick?> read, Action<string>? note = null)
    {
        AutoRefitDecisions.Pick? agreed = null;
        foreach (var candidate in candidates)
        {
            var pick = read(candidate);
            if (pick == null || !AutoRefitDecisions.Proceed(pick.Confidence))
            {
                note?.Invoke($"the {candidate.GamePath} in \"{Shown(candidate.Source)}\" could not be read clearly");
                return null;
            }
            if (agreed != null
                && (!string.Equals(agreed.Dir, pick.Dir, StringComparison.OrdinalIgnoreCase)
                    || RefitCore.Normal(agreed.Option.Rel) != RefitCore.Normal(pick.Option.Rel)))
            {
                note?.Invoke($"its options' pieces disagree: {agreed.Option.Label}, {pick.Option.Label}");
                return null;
            }
            agreed ??= pick;
        }
        return agreed;
    }

    /// <summary>
    /// What a piece's siblings add to its cache key: "" for none, so a piece without one keeps the key — and the reading —
    /// it always had; otherwise each sibling's file as it stands on disk (<paramref name="stamp"/>: its length and time
    /// written), so a piece read by its cloth before it had a sibling is read again, as is one whose sibling the author
    /// changed — and a piece put on again is not, without its siblings even being opened.
    /// </summary>
    /// <param name="stamp">A file's state on disk, by its path in the mod.</param>
    internal static string CacheSuffix(IReadOnlyList<Candidate> siblings, Func<string, string> stamp)
        => siblings.Count == 0
            ? ""
            : "|sib=" + string.Join(";", siblings.Select(c => RefitCore.Normal(c.File) + "@" + stamp(c.File)));

    /// <summary>
    /// A sibling's model, its parts and its rig, read from the mod — or null, noted, when it cannot be: missing, locked,
    /// or not a model. A sibling that cannot be read leaves the piece read as before; it never fails the piece's refit.
    /// </summary>
    internal static (byte[] Bytes, ModelParts Model, IReadOnlySet<string> Bones)? TryOpen(string modRoot, string file,
                                                                                         Action<string>? note = null)
    {
        try
        {
            var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(modRoot, file));
            if (ModelPartReader.Read(bytes) is not { } model)
            {
                note?.Invoke($"{file} could not be read");
                return null;
            }
            return (bytes, model, new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            note?.Invoke($"{file} could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Whether a piece is sized by its siblings at all: a mod's piece, refitted at the race it is drawn — one
    /// baked for another race is read in that race's sizes, which its siblings are not.</summary>
    internal static bool Applies(string? modRoot, ushort bakeTo) => modRoot != null && bakeTo == 0;

    /// <summary>An option as the log names it.</summary>
    internal static string Shown(string source) => source.Length == 0 ? "default files" : source;
}
