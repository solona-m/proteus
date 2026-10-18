using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Which extra skeleton a hair port moves with, and how the target race gets it. A race's hairstyle loads the skeleton
/// its entry in the game's table names (<see cref="HairSkeletonTable"/>) — most races' hairstyle 8 moves with skeleton
/// 9 — unless a mod's <c>Est</c> entry says otherwise. A port is checked against the skeleton the target race will load;
/// when that lacks bones the model moves with, the target race is pointed at its own skeleton that has them, or given a
/// copy of the source race's at a skeleton slot no other hairstyle of the race loads, so no other hairstyle changes.
/// The one place the builder and <see cref="HairPortBuilder.IsOutOfDate"/> take this from, so they agree.
/// </summary>
internal static class HairPortSkeleton
{
    /// <summary>The highest skeleton id a free slot is looked for up to.</summary>
    internal const int MaxSkeleton = 9999;

    /// <summary>How the target race gets the skeleton the port moves with.</summary>
    internal enum Route
    {
        /// <summary>Nothing added: it moves with what the race loads already (<see cref="Choice.Check"/>), or with
        /// nothing that would do better, and the bend's own check has the last word.</summary>
        Own,
        /// <summary>An <c>Est</c> entry to the race's own skeleton <see cref="Choice.Check"/>.</summary>
        Entry,
        /// <summary>The source race's skeleton <see cref="Choice.From"/> copied to a free slot, with an entry to it.</summary>
        Carry,
        /// <summary>The game's table could not be read: no verdict.</summary>
        Unknown,
    }

    /// <param name="Check">The skeleton the port is checked against (Own, Entry); 0 for none.</param>
    /// <param name="Loaded">The skeleton the target race loads for the hairstyle without the port; 0 for none.</param>
    /// <param name="From">For a carry, the source race's skeleton to copy.</param>
    /// <param name="Bones">The extra bones the checked skeleton has.</param>
    internal sealed record Choice(Route Route, int Check, int Loaded, int From, IReadOnlySet<string> Bones);

    /// <summary>One port's place in the mod, and what it reads: the inputs every decision here shares.</summary>
    /// <param name="Was">This place's port of the target from before, or null.</param>
    /// <param name="Ports">Every port the mod's record holds.</param>
    internal sealed record Place(string ModRoot, IReadOnlyList<HairPortPlan.Owner> Owners, HairPortPlan.Owner Owner,
                                 HairPortRecord.Entry? Was, IReadOnlyList<HairPortRecord.Entry> Ports, ushort From,
                                 ushort To, int Id, Func<string, byte[]?> ReadGame, Func<HairSkeletonTable?> Table)
    {
        public HairPortPlan.Owner? Defaults => Owners.FirstOrDefault(o => o.IsDefault);
        public string Target => HairPortPlan.HairPath(To, Id);
    }

    /// <summary>The hairstyle-skeleton bones (<c>j_ex_…</c>) a model moves with.</summary>
    internal static List<string> ExtraBones(byte[] model)
        => SecondSkinWriter.Parse(model).BoneNames.Where(b => b.StartsWith("j_ex_", StringComparison.Ordinal)).ToList();

    /// <summary>The bones of <paramref name="extra"/> that <paramref name="have"/> lacks.</summary>
    internal static List<string> Missing(IEnumerable<string> extra, IReadOnlySet<string> have)
        => extra.Where(b => !have.Contains(b)).ToList();

    /// <summary>
    /// How the target race gets the skeleton the model moves with, first rule that holds:
    /// <list type="number">
    /// <item>The author gave the target race an entry of their own: that skeleton, alone, and nothing added.</item>
    /// <item>The mod gives the source race a skeleton the target race has one of its own with every bone of: an entry to
    /// it, as ports made before the game's table was read did — so none of them changes.</item>
    /// <item>The model moves with no hairstyle bones: nothing added, and the table is not read.</item>
    /// <item>The table cannot be read: no verdict.</item>
    /// <item>What the target race loads has every bone: nothing added.</item>
    /// <item>The target race's own copy of the skeleton the source loads has them: an entry to it.</item>
    /// <item>The source loads a skeleton: a copy of it.</item>
    /// <item>Otherwise what the target loads, which the bend's own check refuses.</item>
    /// </list>
    /// An entry is judged by the file the game will load with the port on: the author's own where the port's place or
    /// the default data publishes one, the game's otherwise — and never points where another place of the mod (or a
    /// port of another hairstyle) publishes the skeleton, which may be what loads instead.
    /// </summary>
    internal static Choice Choose(Place p, byte[] model)
    {
        var extra = ExtraBones(model);
        int defaultsOurs = HairPortBuilder.DefaultsPortEst(p.Ports, p.Target);
        if (HairPortBuilder.AuthorTargetEst(p.Owner, p.Defaults, p.Was, p.To, p.Id, defaultsOurs) is { } author)
            return new Choice(Route.Own, author, author, 0, Bones(p, p.To, author));

        int? modSource = HairPortPlan.SourceEst(p.Owner, p.Defaults, p.From, p.Id);
        if (modSource is > 0 and var borrowed && Fits(p, extra, borrowed) is { } fit)
            return new Choice(Route.Entry, borrowed, 0, 0, fit);
        if (extra.Count == 0) return new Choice(Route.Own, 0, 0, 0, new HashSet<string>());
        if (p.Table() is not { } table) return new Choice(Route.Unknown, 0, 0, 0, new HashSet<string>());

        int loaded = table.Loaded(p.To, p.Id);
        var own = Bones(p, p.To, loaded);
        if (loaded > 0 && Missing(extra, own).Count == 0) return new Choice(Route.Own, loaded, loaded, 0, own);
        int source = modSource ?? table.Loaded(p.From, p.Id);
        if (source > 0 && source != loaded && Fits(p, extra, source) is { } theirs)
            return new Choice(Route.Entry, source, loaded, 0, theirs);
        if (source > 0) return new Choice(Route.Carry, 0, loaded, source, own);
        return new Choice(Route.Own, loaded, loaded, 0, own);
    }

    /// <summary>
    /// The target race's own skeleton <paramref name="skeleton"/>'s bones, when it has every one the model moves with:
    /// the game's file, or the author's when the port's place (or the default data) publishes one — what the game loads
    /// with the port on. Null otherwise, and when another place of the mod publishes it, which may win or not.
    /// </summary>
    private static HashSet<string>? Fits(Place p, List<string> extra, int skeleton)
    {
        string skl = HairPortPlan.SkeletonPath(p.To, skeleton), phy = HairPortPlan.PhysicsPath(p.To, skeleton);
        if (!ExplainedOrAuthors(p, skl) || !ExplainedOrAuthors(p, phy)) return null;
        var author = AuthorsOwn(p, skl);
        var bones = author != null ? HairPortPlan.SkeletonBones(HairPortBuilder.ReadMod(p.ModRoot, author))
                                   : Bones(p, p.To, skeleton);
        return Missing(extra, bones).Count > 0 ? null : bones;
    }

    /// <summary>The author's file the port's place — else the default data — publishes at <paramref name="gamePath"/>:
    /// what the game loads there with the port on. Null for none, or one of our ports'.</summary>
    private static string? AuthorsOwn(Place p, string gamePath)
        => new[] { p.Owner, p.Defaults }.OfType<HairPortPlan.Owner>()
                                         .Select(o => HairPortPlan.FileFor(o, gamePath))
                                         .FirstOrDefault(f => f != null && !HairPortPlan.IsPortFile(f));

    /// <summary>Whether every place publishing <paramref name="gamePath"/> does so for this target's own ports, or is
    /// the port's place or the default data publishing the author's own file — the one the game loads with the port on.
    /// Another place's file, or a port of another hairstyle, may be what the game loads instead.</summary>
    private static bool ExplainedOrAuthors(Place p, string gamePath)
        => p.Owners.All(o => HairPortPlan.FileFor(o, gamePath) is not { } file
                             || ((HairPortPlan.Same(o, p.Owner) || o.IsDefault) && !HairPortPlan.IsPortFile(file))
                             || p.Ports.Any(e => HairPortPlan.Same(e.Owner, o)
                                                 && e.TargetGamePath.Equals(p.Target, StringComparison.OrdinalIgnoreCase)
                                                 && e.Materials.ContainsKey(gamePath)));

    /// <summary>The bones of a race's skeleton as the game ships it; none for skeleton 0 or no file.</summary>
    private static HashSet<string> Bones(Place p, ushort race, int skeleton)
        => skeleton > 0 ? HairPortPlan.SkeletonBones(p.ReadGame(HairPortPlan.SkeletonPath(race, skeleton))) : [];

    /// <summary>Whether every place of the mod that publishes <paramref name="gamePath"/> does so for one of this
    /// target's own ports, as its record says — the port's own files from before, or its sibling's in another place.
    /// True when nothing publishes it.</summary>
    private static bool Explained(Place p, string gamePath)
        => p.Owners.All(o => HairPortPlan.FileFor(o, gamePath) == null
                             || p.Ports.Any(e => HairPortPlan.Same(e.Owner, o)
                                                 && e.TargetGamePath.Equals(p.Target, StringComparison.OrdinalIgnoreCase)
                                                 && e.Materials.ContainsKey(gamePath)));

    /// <summary>
    /// Whether a carried skeleton may go to slot <paramref name="slot"/> of the target race: no other hairstyle of the
    /// race loads it in the game's table, no entry of the mod points another hairstyle of the race at it, no place of
    /// the mod publishes its skeleton or physics but this target's own ports, and the game ships no kinematics there
    /// that would be paired with a foreign skeleton.
    /// </summary>
    internal static bool SlotFree(Place p, HairSkeletonTable table, int slot) => SlotFree(p, Taken(p, table), slot);

    /// <summary>The slots of the target race another of its hairstyles loads, in the game's table or by an entry of the
    /// mod.</summary>
    private static HashSet<int> Taken(Place p, HairSkeletonTable table)
    {
        var taken = table.SkeletonsLoaded(p.To, except: p.Id);
        foreach (var o in p.Owners)
            taken.UnionWith(HairPortPlan.RaceHairEsts(o, p.To).Where(e => e.Set != p.Id).Select(e => e.Entry));
        return taken;
    }

    private static bool SlotFree(Place p, HashSet<int> taken, int slot)
        => slot is > 0 and <= MaxSkeleton && !taken.Contains(slot)
           && Explained(p, HairPortPlan.SkeletonPath(p.To, slot)) && Explained(p, HairPortPlan.PhysicsPath(p.To, slot))
           && p.ReadGame(HairPortPlan.KinematicsPath(p.To, slot)) == null;

    /// <summary>
    /// The slots a carry tries, best first: the one <paramref name="preferred"/> (another place's port of the same target
    /// chose it in this build), the ones this target's ports use already — its sibling's first, so both places agree —
    /// what the race loads for the hairstyle, the hairstyle's own number, then the lowest that is free.
    /// </summary>
    internal static IEnumerable<int> Slots(Place p, HairSkeletonTable table, int loaded, int preferred)
    {
        var used = p.Ports.Where(e => e.TargetGamePath.Equals(p.Target, StringComparison.OrdinalIgnoreCase)
                                      && e.Est > 0 && e.SkeletonFrom > 0)
                          .OrderBy(e => HairPortPlan.Same(e.Owner, p.Owner) ? 1 : 0)
                          .Select(e => e.Est);
        var taken = Taken(p, table);
        return new[] { preferred }.Concat(used).Concat([loaded, p.Id]).Concat(Enumerable.Range(1, MaxSkeleton))
                                  .Where(f => f > 0).Distinct().Where(f => SlotFree(p, taken, f));
    }

    /// <summary>
    /// The source race's skeleton <paramref name="skeleton"/> and its physics, copied to the first slot of
    /// <see cref="Slots"/> they can go to (<see cref="CarryAt"/>). Null when none will do — the source's skeleton lacking
    /// a bone the model moves with is seen once, not slot by slot.
    /// </summary>
    internal static (IReadOnlyList<HairPortWriter.MaterialWrite> Files, HashSet<string> Bones, int Slot)? Carry(
        Place p, HairPortRecord record, byte[] model, Choice choice, int preferred)
    {
        if (choice.Route != Route.Carry || p.Table() is not { } table) return null;
        var source = HairPortPlan.SkeletonPath(p.From, choice.From);
        var (skl, bytes) = HairPortBuilder.Carry(p.ModRoot, p.Owners, p.Owner, source, source, p.ReadGame);
        if (skl == null || Missing(ExtraBones(model), HairPortPlan.SkeletonBones(bytes)).Count > 0) return null;
        foreach (int slot in Slots(p, table, choice.Loaded, preferred))
            if (CarryAt(p, record, model, choice.From, slot) is { } carried)
                return (carried.Files, carried.Bones, slot);
        return null;
    }

    /// <summary>
    /// The source race's skeleton <paramref name="skeleton"/> — the author's file when the port's place (or the default
    /// data) publishes it, otherwise a copy — and its physics, published at slot <paramref name="slot"/> of the target
    /// race. Null when the source's skeleton lacks a bone the model moves with, the target has physics at the slot the
    /// source has none to replace, or the mod may not publish there.
    /// </summary>
    internal static (IReadOnlyList<HairPortWriter.MaterialWrite> Files, HashSet<string> Bones)? CarryAt(
        Place p, HairPortRecord record, byte[] model, int skeleton, int slot)
    {
        var (skl, sklBytes) = HairPortBuilder.Carry(p.ModRoot, p.Owners, p.Owner, HairPortPlan.SkeletonPath(p.From, skeleton),
                                                    HairPortPlan.SkeletonPath(p.To, slot), p.ReadGame);
        var bones = HairPortPlan.SkeletonBones(sklBytes);
        if (skl == null || Missing(ExtraBones(model), bones).Count > 0) return null;
        var files = new List<HairPortWriter.MaterialWrite> { skl };

        string physics = HairPortPlan.PhysicsPath(p.To, slot);
        var (phy, _) = HairPortBuilder.Carry(p.ModRoot, p.Owners, p.Owner, HairPortPlan.PhysicsPath(p.From, skeleton), physics,
                                             p.ReadGame);
        // Physics the slot keeps would swing bones the carried skeleton does not have.
        if (phy == null && (p.ReadGame(physics) != null || !Explained(p, physics))) return null;
        if (phy != null) files.Add(phy);

        if (files.Any(f => !HairPortWriter.MayPublish(p.Owners, p.Owner, record, f.GamePath))) return null;
        return (files, bones);
    }

    /// <summary>
    /// Whether a port's skeleton is still the one <see cref="Choose"/> gives it: nothing for nothing, the same entry, or a
    /// copy of the same source skeleton at a slot that is still free. A carry from before the game's table was read — a
    /// copy at the hairstyle's own number, with no entry — is only current where the race loads exactly that slot for the
    /// hairstyle and nothing else does. With the table unreadable, ports are left as they are, those old carries aside.
    /// </summary>
    internal static bool Current(Place p, HairPortRecord.Entry entry, byte[] model)
    {
        bool old = entry.Est == 0 && entry.SkeletonFrom > 0;
        var choice = Choose(p, model);
        switch (choice.Route)
        {
            case Route.Unknown: return !old;
            case Route.Own: return entry is { Est: 0, SkeletonFrom: 0 };
            case Route.Entry: return entry.Est == choice.Check && entry.SkeletonFrom == 0;
        }
        if (entry.SkeletonFrom != choice.From || p.Table() is not { } table) return false;
        int slot = old ? (table.Loaded(p.To, p.Id) == p.Id ? p.Id : 0) : entry.Est;
        return slot > 0 && SlotFree(p, table, slot);
    }

    /// <summary>Whether a port carries its skeleton to a slot another hairstyle of the race loads — by the game's table,
    /// or an entry of the mod — so it changes that hairstyle: one written before the table was read, at the hairstyle's
    /// own number, or one whose slot the game gave another hairstyle since.</summary>
    internal static bool Harmful(Place p, HairPortRecord.Entry entry)
    {
        if (entry.SkeletonFrom <= 0 || p.Table() is not { } table) return false;
        return Taken(p, table).Contains(entry.Est > 0 ? entry.Est : p.Id);
    }
}
