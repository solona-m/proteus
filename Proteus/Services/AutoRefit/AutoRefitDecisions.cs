using System;
using System.Collections.Generic;
using System.Linq;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;

namespace Proteus.Services;

/// <summary>
/// The choices the automatic refit makes without asking — which size, which body, whether to act at all — kept apart
/// from the watcher's threading and IPC so each can be tested on plain values.
/// </summary>
internal static class AutoRefitDecisions
{
    /// <summary>The body slots the automatic refit acts on, in the order a design changing several is worked through.</summary>
    internal static readonly string[] Slots = ["_top", "_dwn", "_glv", "_sho"];

    /// <summary>Whether a reading is sure enough to refit from unattended.</summary>
    internal static bool Proceed(Confidence confidence)
        => confidence is Confidence.Exact or Confidence.Likely or Confidence.Guess;

    /// <summary>The less sure of two readings: Exact, then Likely, Guess, Ambiguous, and no evidence below all of them.</summary>
    internal static Confidence Lower(Confidence a, Confidence b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(Confidence c) => c switch
    {
        Confidence.Exact => 0,
        Confidence.Likely => 1,
        Confidence.Guess => 2,
        Confidence.Ambiguous => 3,
        Confidence.TooLittle => 4,
        _ => 5,
    };

    /// <summary>
    /// The option a remembered size names, among <paramref name="options"/> (one slot, one race): the same file, or
    /// failing that the same option by group, heading and name — the same size at another race.
    /// </summary>
    internal static BodyOption? Resolve(IReadOnlyList<BodyOption> options, BodySizeRef? size)
    {
        if (size == null) return null;
        string rel = RefitCore.Normal(size.Rel);
        return options.FirstOrDefault(o => RefitCore.Normal(o.Rel) == rel)
            ?? options.FirstOrDefault(o => Same(o.Group, size.Group) && Same(o.Section, size.Section)
                                           && Same(o.Name, size.Name));
    }

    /// <summary>A size to remember, from the option picked.</summary>
    internal static BodySizeRef RefOf(BodyOption option)
        => new() { Rel = option.Rel, Group = option.Group, Section = option.Section, Name = option.Name };

    /// <summary>
    /// The size of a part nobody chose — hands or feet — to go with the size that was: the option in the same heading
    /// with the same name ("SFW M" under "--- DEFAULT ---" beside the chest's own), then by name alone, then the first
    /// of the same heading. Failing all of those, the size the character already wears there, and then the first the
    /// author lists — a body mod with one pair of hands has exactly one answer.
    /// </summary>
    internal static BodyOption? Companion(IReadOnlyList<BodyOption> options, BodyOption? anchor, BodyOption? worn)
    {
        if (options.Count == 0) return null;
        if (anchor != null)
        {
            if (options.FirstOrDefault(o => Same(o.Section, anchor.Section) && Same(o.Name, anchor.Name)) is { } both)
                return both;
            if (options.FirstOrDefault(o => Same(o.Name, anchor.Name)) is { } named) return named;
            if (anchor.Section.Length > 0 && options.FirstOrDefault(o => Same(o.Section, anchor.Section)) is { } under)
                return under;
        }
        if (worn != null && options.Contains(worn)) return worn;
        return options[0];
    }

    /// <summary>
    /// Whether a reading of ANOTHER part than the garment's own slot is good enough to refit that part too. A clear
    /// reading is; so is a guess that lands on the same size, under the same heading, as the garment's own slot — the
    /// author built the outfit on one body, and the two readings agree on which.
    /// </summary>
    internal static bool AcceptOther(BodySizeMatch.Ranking ranking, BodyOption primarySource)
        => ranking.Best is { } best
           && (ranking.Preselect
               || (ranking.Confidence == Confidence.Guess && Same(best.Option.Section, primarySource.Section)
                   && Same(best.Option.Name, primarySource.Name)));

    /// <summary>The two options are the same model of the same body mod — a refit between them has nothing to do.</summary>
    internal static bool SameFile(string sourceDir, BodyOption source, string targetDir, BodyOption target)
        => string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase)
           && RefitCore.Normal(source.Rel) == RefitCore.Normal(target.Rel);

    /// <summary>The body a garment was made on, and how sure the reading is.</summary>
    internal sealed record Pick(string Dir, BodyOption Option, Confidence Confidence, bool FromCloth);

    // ── which body mod it was made on ────────────────────────────────────────
    //
    // Measured over 28 installed body mods: ranking every size of every mod (BodySizeMatch.Rank) to find a chest's body
    // took 14 s and allocated 3.4 GB — the garbage collector's pauses froze the game for the length of it. So the mod
    // is chosen first from a far cheaper reading (BodySizeMatch.Shared: the share of the garment's skin points lying
    // exactly on a body's vertices), and only the chosen mod is ranked. That reading tells body FAMILIES apart
    // cleanly — a Neolithe chest shares 41% with Neolithe's sizes and at most 1% with any other body mod — and, across
    // every size, the mod within a family: a LaRue chest shares 56% with LavaBod+'s own LaRue sizes and 41% with Rue's.
    // It cannot tell sizes of one mod apart (all of Neolithe's chests share the same 42%), which is Rank's job.

    /// <summary>How many of a mod's sizes are read to place it in a family.</summary>
    internal const int Samples = 3;

    /// <summary>
    /// How many of a family mod's sizes are read to choose between the family's mods. Not every size: Neolithe has 114
    /// chests, and reading them all for each of its forks cost a gigabyte to settle what a dozen settle as well.
    /// </summary>
    internal const int ChoiceSamples = 12;

    /// <summary>Below this share with every body, the garment's skin matches no installed body at all.</summary>
    internal const float MinShared = 0.05f;

    /// <summary>
    /// A mod within this of the best sampled share is in the garment's family. Generous: three samples can miss the
    /// sizes that share the most.
    /// </summary>
    internal const float FamilyMargin = 0.10f;

    /// <summary>Mods within this of the best share over every size are the same answer — usually one body and its fork.</summary>
    internal const float TieMargin = 0.02f;

    /// <param name="Share">The best share of any size read.</param>
    /// <param name="Preferred">The body mod refitted onto: among equals, a garment that already fits it stays on it.</param>
    /// <param name="Enabled">Switched on in the collection — what the player wears is what their gear was likely made for.</param>
    /// <param name="Sizes">How many sizes it offers: among equals, a body pack before a one-body copy of it.</param>
    internal readonly record struct ModShare(string Dir, float Share, bool Preferred, bool Enabled, int Sizes);

    /// <summary>
    /// <paramref name="count"/> of a mod's sizes spread evenly over the author's order, first and last included — with
    /// the default, the first, the middle and the last.
    /// </summary>
    internal static List<BodyOption> SamplesOf(IReadOnlyList<BodyOption> options, int count = Samples)
    {
        if (options.Count <= count) return options.ToList();
        var picked = new List<BodyOption>(count);
        for (int i = 0; i < count; i++)
            picked.Add(options[(int)Math.Round(i * (options.Count - 1) / (double)(count - 1))]);
        return picked.Distinct().ToList();
    }

    /// <summary>The mods in the garment's family, from their sampled shares, in the order given. Empty when the garment
    /// matches no installed body.</summary>
    internal static List<string> Family(IReadOnlyList<ModShare> sampled)
    {
        if (sampled.Count == 0) return [];
        float best = sampled.Max(m => m.Share);
        if (best < MinShared) return [];
        return sampled.Where(m => m.Share >= best - FamilyMargin).Select(m => m.Dir).ToList();
    }

    /// <summary>
    /// The one mod to rank the garment's sizes in, from each family mod's best share over every size: the highest, and
    /// among those within <see cref="TieMargin"/> of it, the preferred body, then one switched on, then the larger pack.
    /// </summary>
    internal static string? Choose(IReadOnlyList<ModShare> full)
    {
        if (full.Count == 0) return null;
        float best = full.Max(m => m.Share);
        if (best < MinShared) return null;
        return full.Select((m, i) => (m, i))
                   .Where(x => x.m.Share >= best - TieMargin)
                   .OrderByDescending(x => x.m.Preferred)
                   .ThenByDescending(x => x.m.Enabled)
                   .ThenByDescending(x => x.m.Sizes)
                   .ThenBy(x => x.i)
                   .First().m.Dir;
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // ── what a walk of the character asks for ──────────────────────────────

    /// <summary>
    /// Which mod draws a slot, and from which file: the identity a change of gear is noticed by.
    /// </summary>
    /// <param name="ModDir">The mod supplying the model, or null for the game's own gear.</param>
    /// <param name="Rel">The model's file relative to that mod, forward slashes; null for the game's own gear.</param>
    internal readonly record struct Worn(string GamePath, string? ModDir, string? Rel)
    {
        /// <summary>What has to change for the slot to count as newly equipped: the item, or the mod drawing it — but
        /// not which of the mod's own options does, which is the player's choice to make.</summary>
        public string Key => GamePath.ToLowerInvariant() + "|" + (ModDir ?? "").ToLowerInvariant();
    }

    /// <summary>Why a newly worn piece is not refitted; <see cref="Skip.None"/> when it is.</summary>
    internal enum Skip
    {
        None,

        /// <summary>Proteus's own managed mod: a composite, never gear an author made.</summary>
        Managed,

        /// <summary>A refit this feature or the Studio already made — refitting it again would compound.</summary>
        OwnRefit,

        /// <summary>The game's own gear, with its switch off.</summary>
        VanillaOff,

        /// <summary>A file served from outside the mods folder that is not the game's either.</summary>
        Unknown,
    }

    /// <summary>Whether a newly worn piece may be refitted.</summary>
    /// <param name="resolvedOutsideMods">The file the game draws is not under Penumbra's mods folder and is not the
    /// game path itself — some other plugin's redirect.</param>
    internal static Skip ShouldRefit(Worn worn, bool vanillaEnabled, bool resolvedOutsideMods)
    {
        if (worn.ModDir == null)
            return resolvedOutsideMods ? Skip.Unknown : vanillaEnabled ? Skip.None : Skip.VanillaOff;
        if (string.Equals(worn.ModDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return Skip.Managed;
        if (worn.Rel != null && RefitCore.Normal(worn.Rel).StartsWith(
                RefitCore.Normal(BodyRetargetWriter.Subfolder) + "/", StringComparison.Ordinal))
            return Skip.OwnRefit;
        return Skip.None;
    }

    /// <summary>The key a mod group is remembered by in <see cref="AutoRefitPreference.SwitchedOn"/>.</summary>
    internal static string GroupKey(string modDir, string group) => modDir + "|" + group;

    /// <summary>
    /// Whether the player has chosen something other than the refit for this piece: a refit was switched on in this
    /// group before, and the group now holds none of the mod's refits. Picking the made mod's "Original" — the game's own
    /// model showing again — or one of the author's sizes reads as the piece newly put on, and switching the refit back
    /// on would undo the choice the moment Penumbra redraws. Never true for a group no refit was switched on in here: a
    /// refit made in another collection, worn here for the first time, still goes on.
    /// </summary>
    /// <param name="switchedOnBefore">The group is in <see cref="AutoRefitPreference.SwitchedOn"/>.</param>
    /// <param name="ticked">The group's selected options in this collection; null when the mod has no setting here.</param>
    /// <param name="refits">The options of the group that are refits (the mod's retarget record).</param>
    internal static bool ChoseOtherwise(bool switchedOnBefore, IEnumerable<string>? ticked, IEnumerable<string> refits)
    {
        if (!switchedOnBefore) return false;
        var ours = new HashSet<string>(refits, StringComparer.OrdinalIgnoreCase);
        return !(ticked ?? []).Any(ours.Contains);
    }

    /// <summary>
    /// The slots whose piece is newly worn since <paramref name="before"/>, with a missing slot counting as bare. Empty
    /// when <paramref name="before"/> is null — the first look after loading, enabling or a collection change is a
    /// baseline, so logging in refits nothing.
    /// </summary>
    internal static List<string> Changed(IReadOnlyDictionary<string, Worn>? before, IReadOnlyDictionary<string, Worn> now)
    {
        var changed = new List<string>();
        if (before == null) return changed;
        foreach (string slot in Slots)
        {
            if (!now.TryGetValue(slot, out var worn)) continue;
            if (before.TryGetValue(slot, out var was) && was.Key == worn.Key) continue;
            changed.Add(slot);
        }
        return changed;
    }
}
