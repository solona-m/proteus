using System;
using System.Collections.Generic;
using System.Linq;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;

namespace Proteus.Services;

/// <summary>
/// The automatic refit of shoes and gloves: the foot or hand stays exactly as its author made it, and only the leg or
/// arm the piece reaches is refitted — sized from that leg or arm alone.
/// <para/>
/// Read through the piece's own slot, a shoe was sized by its foot. A heeled foot is turned onto the toe and lies on no
/// body's flat foot, so the reading came out weak or refused even where the stocking above it read cleanly; and where it
/// went ahead, the author's foot was swapped for the new body's. Here the foot and hand are found by their weights
/// (<see cref="BodyRetarget.EndVertices"/>), held where they are, and left out of every reading.
/// <para/>
/// Three ways to read a piece, in order:
/// <list type="bullet">
/// <item><see cref="Route.Skin"/>: the leg or arm carries skin, and it is read like any garment.</item>
/// <item><see cref="Route.Cloth"/>: it carries only cloth — a boot's shaft — so the body is read off the foot, as
/// before, and the size off how the shaft's cloth sits on that body's legs.</item>
/// <item><see cref="Route.SkinOnly"/>: it reaches no leg or arm at all. Nothing is reshaped; the kept skin is only
/// drawn the way the new body draws its own, when it is not already.</item>
/// </list>
/// Static and free of IPC: the watcher gathers, this decides, the watcher saves.
/// </summary>
internal static class KeepEnds
{
    /// <summary>How a piece was read; the last two are the ways it can fail to be.</summary>
    internal enum Route
    {
        Skin,
        Cloth,
        SkinOnly,

        /// <summary>The leg or arm was readable but no size of it was clear: left as is, as any unclear reading is.</summary>
        Unsure,

        /// <summary>Not even which body the piece was made on could be told: left as is.</summary>
        NoFamily,

        /// <summary>The piece carries no body skin anywhere — a boot all leather, a gauntlet — so there is nothing to read
        /// a body off, as before: left as is.</summary>
        NoSkin,
    }

    /// <summary>What a skin-only option is called after the body's name: the shape is the author's at every size, so it
    /// is one option per body, not per size.</summary>
    internal const string SkinOnlyLabel = "skin";

    /// <summary>The piece split into what it keeps and what it reaches.</summary>
    /// <param name="Slot">"_sho" or "_glv".</param>
    /// <param name="Limb">The slot of the body refitted on: "_dwn" or "_top" (see <see cref="AutoRefitDecisions.LimbOf"/>).</param>
    /// <param name="Ends">The foot's or hand's vertices, held by the refit.</param>
    /// <param name="LimbView">The piece without them — what is read (see <see cref="RefitCore.LimbView"/>).</param>
    /// <param name="FreePieces">The hard pieces that move with the leg or arm; every other is wholly in
    /// <paramref name="Ends"/>.</param>
    /// <param name="Band">Vertices with 40-60% of their weight on the end, for the log.</param>
    internal sealed record Reading(string Slot, string Limb, IReadOnlySet<int> Ends, ModelParts LimbView,
                                   IReadOnlyList<IReadOnlyCollection<int>> FreePieces, int Band, int Vertices)
    {
        /// <summary>The slot a reading is cached under: never the piece's own, which the old way of reading used.</summary>
        public string CacheSlot => Slot + ">" + Limb;
    }

    /// <summary>
    /// Split a shoe or glove into its end and the rest. Null for any other slot, or when its rig cannot be read — there
    /// is then no telling the foot from the leg, and the piece is left as it is.
    /// </summary>
    internal static Reading? Read(string slot, ModelParts garment, byte[] bytes)
    {
        if (AutoRefitDecisions.LimbOf(slot) is not { } limb) return null;
        if (ModelSkinReader.Read(bytes, null, null) is not { } skin) return null;
        var pieces = RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment));
        var ends = BodyRetarget.EndVertices(garment, skin, slot, pieces, out int band);
        var free = pieces.Where(p => !p.All(ends.Contains)).ToList();
        return new Reading(slot, limb, ends, RefitCore.LimbView(garment, ends), free, band,
                           garment.Positions.Length / 3);
    }

    /// <summary>
    /// Whether an author's own size is looked for before a piece in <paramref name="slot"/> is refitted: a shoe's, by
    /// its legs. Not a glove's: every chest size of a body has the same arms, so no author's size can be the one exact
    /// copy of the chosen size, and the search would only ever find nothing.
    /// </summary>
    internal static bool ComparesAuthorSizes(string slot) => slot == "_sho";

    /// <summary>What a piece was read as: its route, and the body and size read — on the limb for
    /// <see cref="Route.Skin"/> and <see cref="Route.Cloth"/>, on the piece's own slot for <see cref="Route.SkinOnly"/>.
    /// Null for the two failures.</summary>
    internal sealed record Found(Route Route, AutoRefitDecisions.Pick? Pick);

    /// <summary>
    /// Read the piece — the expensive half, kept in the detection cache (see <see cref="ToCache"/>).
    /// </summary>
    /// <param name="detect">Which body and size a model was made on in a slot, searched over every installed body
    /// (<see cref="RefitCore.DetectSource"/>, or the game's own body for the game's gear).</param>
    /// <param name="catalogOf">A body mod's sizes, by its folder.</param>
    /// <param name="rank">How the leg or arm is ranked against one body mod's sizes there, given whether it carries skin:
    /// <see cref="BodySizeMatch.Rank"/> with skin, its cloth reading without. Null for exactly that; a test passes its
    /// own.</param>
    internal static Found Find(Reading reading, ModelParts garment, IReadOnlySet<string> bones, string race,
                               Func<ModelParts, string, AutoRefitDecisions.Pick?> detect,
                               Func<string, BodySizeCatalog?> catalogOf,
                               Func<ModelParts, IReadOnlyList<BodyOption>, BodySizeCatalog, bool, BodySizeMatch.Ranking?>? rank = null)
    {
        rank ??= (view, options, catalog, skin) => skin ? BodySizeMatch.Rank(view, options, catalog.PathOf, bones)
                                                        : BodySizeMatch.RankByCloth(view, options, catalog.PathOf);
        // The leg's skin, read as any garment's.
        bool limbSkin = BodySizeMatch.SkinPoints(reading.LimbView).Count > 0;
        if (limbSkin && detect(reading.LimbView, reading.Limb) is { } read
            && read.Confidence is not (Confidence.NoBodyMesh or Confidence.TooLittle))
            return AutoRefitDecisions.Proceed(read.Confidence) ? new Found(Route.Skin, read) : new Found(Route.Unsure, null);

        // Too little leg skin to read: which body from the foot, as the piece was always read; which size of its legs
        // from the leg's cloth. A boot's shaft was refitted so before, as a part the boot reaches, and still is.
        var own = detect(garment, reading.Slot);
        if (own?.Confidence == Confidence.NoBodyMesh) return new Found(Route.NoSkin, null);
        if (own == null || catalogOf(own.Dir) is not { } catalog) return new Found(Route.NoFamily, null);
        var options = catalog.For(reading.Limb, race);
        // One size of legs is the answer once the body is known: nothing to tell apart. The game's own body is one —
        // its gear is made on it — and the cloth reading can only guess at a single size, which is not accepted.
        if (options.Count == 1 && AutoRefitDecisions.Proceed(own.Confidence))
            return new Found(Route.Cloth, new AutoRefitDecisions.Pick(own.Dir, options[0], own.Confidence, !limbSkin));
        if (options.Count > 0 && AutoRefitDecisions.Proceed(own.Confidence))
        {
            var ranking = rank(reading.LimbView, options, catalog, limbSkin);
            if (ranking is { Best: { } best } && ranking.Confidence is not (Confidence.NoBodyMesh or Confidence.TooLittle))
                return AutoRefitDecisions.AcceptOther(ranking, own.Option)
                    ? new Found(Route.Cloth, new AutoRefitDecisions.Pick(own.Dir, best.Option, ranking.Confidence,
                                                                          ranking.FromCloth))
                    : new Found(Route.Unsure, null);
        }

        // Nothing of the legs to read: a plain shoe. Its skin is redrawn if the new body draws skin another way.
        return new Found(Route.SkinOnly, own);
    }

    /// <summary>What to do with a piece read as <see cref="Found"/>.</summary>
    internal abstract record Decision;

    /// <summary>Refit the leg or arm from <paramref name="Source"/> onto <paramref name="Target"/>, keeping the end.</summary>
    /// <param name="EndFrom">The source body's feet or hands that go with <paramref name="Source"/>, and
    /// <paramref name="EndTo"/> the new body's: their shapes only (see <see cref="Plan"/>). Null when either body has none.</param>
    internal sealed record Refit(AutoRefitDecisions.Pick Source, BodyOption Target, BodyOption? EndFrom = null,
                                 BodyOption? EndTo = null) : Decision;

    /// <summary>Keep the shape; draw the skin as <paramref name="Target"/>'s body does. <paramref name="Own"/> is the body
    /// and size read off the piece's own slot, whose texture layout the skin is in.</summary>
    internal sealed record SkinOnly(AutoRefitDecisions.Pick Own, BodyOption Target) : Decision;

    /// <summary>The leg or arm already is <paramref name="Target"/>, or one the piece cannot tell from it.</summary>
    internal sealed record AlreadyFits(BodyOption Target) : Decision;

    /// <summary>The body refitted onto has no size for <paramref name="Slot"/> at this race.</summary>
    internal sealed record NoSize(string Slot) : Decision;

    /// <summary>Left as it is: <see cref="Route.Unsure"/> or <see cref="Route.NoFamily"/>.</summary>
    internal sealed record LeftAsIs(Route Why) : Decision;

    /// <summary>
    /// Decide, from a reading, before anything is planned: a piece already refitted, or switched away from, is let be
    /// by the watcher on the option this names alone, without a refit worked out and thrown away.
    /// </summary>
    /// <param name="targetFor">The size the refit goes onto, per slot: the chosen chest or legs, and what the hands and
    /// feet follow (see <see cref="AutoRefitDecisions.Companion"/>).</param>
    internal static Decision Decide(Found found, Reading reading, IReadOnlySet<string> bones, string race, string targetDir,
                                    Func<string, BodySizeCatalog?> catalogOf, Func<string, BodyOption?> targetFor)
    {
        if (found.Pick is not { } pick) return new LeftAsIs(found.Route);
        if (found.Route == Route.SkinOnly)
            return targetFor(reading.Slot) is { } own ? new SkinOnly(pick, own) : new NoSize(reading.Slot);

        if (targetFor(reading.Limb) is not { } target) return new NoSize(reading.Limb);
        if (AutoRefitDecisions.SameFile(pick.Dir, pick.Option, targetDir, target)) return new AlreadyFits(target);
        var source = catalogOf(pick.Dir);
        // Arms, and often the lower legs, are one shape across a body mod's sizes: a glove read as one size and aimed at
        // another would be "refitted" to exactly where it is, and saved into the player's mod for nothing.
        if (string.Equals(pick.Dir, targetDir, StringComparison.OrdinalIgnoreCase) && source != null
            && SameShape(reading.LimbView, pick.Option, target, source, bones, pick.FromCloth))
            return new AlreadyFits(target);
        var endFrom = source == null ? null : AutoRefitDecisions.Companion(source.For(reading.Slot, race), pick.Option, null);
        return new Refit(pick, target, endFrom, endFrom == null ? null : targetFor(reading.Slot));
    }

    /// <summary>Whether <paramref name="view"/> reads <paramref name="a"/> and <paramref name="b"/> as the same answer
    /// (<see cref="BodySizeMatch.SameAnswer"/>) — the same body wherever the piece is.</summary>
    internal static bool SameShape(ModelParts view, BodyOption a, BodyOption b, BodySizeCatalog catalog,
                                   IReadOnlySet<string> bones, bool fromCloth)
    {
        var ranking = fromCloth ? BodySizeMatch.RankByCloth(view, [a, b], catalog.PathOf)
                                : BodySizeMatch.Rank(view, [a, b], catalog.PathOf, bones);
        if (ranking == null || ranking.Scores.Count == 0) return false;
        // One score for two options: the ranking found them one geometry.
        if (ranking.Scores.Count == 1) return true;
        float best = MathF.Max(ranking.Scores[0].Rms, 1e-6f);
        return ranking.Scores[1].Rms <= best * (1f + BodySizeMatch.SameAnswer);
    }

    /// <summary>
    /// Refit the leg or arm, holding the end: the garment's own slot as the slot it is worn in (so the push-out keeps the
    /// legs under a shoe, and a glove is never taken for a top), and the limb as the slot whose skin is swapped. The
    /// limb's skin goes in cut to where the piece drew skin, whatever the unattended refit does elsewhere: uncut, a shoe
    /// would carry the whole legs body, waist to ankles, through any skirt worn over it.
    /// <para/>
    /// <paramref name="end"/>, the feet or hands pair, goes in for its shape only (<see cref="BodyRetarget.SlotPair.ShapeOnly"/>),
    /// so its skin is never swapped in. A body's feet run up the shin, rigged to the calf, and its legs stop above them,
    /// so with the legs alone the shin had only the bottom of the new legs to follow: Heartthrob, Bibo+ Medium onto
    /// Neolithe SFW M, moved up to 19.0 mm and stretched the ankle by 5.1 mm; with the feet's shape, 11.9 mm and 4.3 mm.
    /// Null leaves it out.
    /// </summary>
    internal static BodyRetarget.Planned Plan(ModelParts garment, byte[] bytes, Reading reading,
                                              BodyRetarget.SlotPair limb, BodyRetarget.SlotPair? end, bool across)
    {
        List<BodyRetarget.SlotPair> pairs = [limb];
        if (end is { } shape) pairs.Add(shape with { ShapeOnly = true });
        return BodyRetarget.Plan(garment, bytes, pairs, reading.Slot, held: reading.Ends, replaceSkin: across,
                                 acrossBodies: across, clearBody: false, cutHidden: true, keepShape: reading.FreePieces,
                                 swapSlot: reading.Limb);
    }

    /// <summary>An ankle or wrist stretched by more than this (5 mm) is logged as worth a look in game.</summary>
    internal const float SeamWarn = 0.005f;

    /// <summary>
    /// How far the kept end's seam stretched: of every skin edge from a kept vertex to a moved one, how many there are and
    /// the most any grew, on the refit's own move — inside the pass, never measured on an export. The end is held rigid,
    /// so where two bodies' ankles differ this is where it shows.
    /// </summary>
    internal static (int Edges, float Worst) SeamStretch(ModelParts garment, IReadOnlySet<int> ends,
                                                         BodyRetarget.Planned plan)
    {
        System.Numerics.Vector3 Before(int v) => new(garment.Positions[v * 3], garment.Positions[v * 3 + 1],
                                                     garment.Positions[v * 3 + 2]);
        System.Numerics.Vector3 After(int v)
        {
            var d = plan.Edit.DeltaAt(v);
            return Before(v) + new System.Numerics.Vector3(d.X, d.Y, d.Z);
        }
        int edges = 0;
        float worst = 0f;
        foreach (var part in garment.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = part.Triangles[t + k], b = part.Triangles[t + (k + 1) % 3];
                    if (ends.Contains(a) == ends.Contains(b)) continue;
                    edges++;
                    worst = MathF.Max(worst, System.Numerics.Vector3.Distance(After(a), After(b))
                                           - System.Numerics.Vector3.Distance(Before(a), Before(b)));
                }
        return (edges, worst);
    }

    /// <summary>The name of a skin-only option: the body's name and <see cref="SkinOnlyLabel"/>.</summary>
    internal static string SkinOnlyOption(string bodyName) => RefitCore.OptionName(bodyName, [SkinOnlyLabel]);

    private const string RouteKey = "route";

    /// <summary>A reading as the detection cache keeps it.</summary>
    internal static DetectionCache.Entry ToCache(Found found)
        => new(found.Pick?.Dir ?? "", found.Pick?.Option.Rel ?? "", found.Pick?.Confidence ?? Confidence.Ambiguous,
               found.Pick?.FromCloth ?? false, new Dictionary<string, string> { [RouteKey] = found.Route.ToString() });

    /// <summary>
    /// A cached reading back, its size looked up in the slot its route read — or null when the entry is not one of these
    /// readings, or the bodies installed now no longer have that size, which reads the piece again.
    /// </summary>
    internal static Found? FromCache(DetectionCache.Entry hit, Reading reading, string race,
                                     Func<string, BodySizeCatalog?> catalogOf)
    {
        if (!hit.Others.TryGetValue(RouteKey, out string? name) || !Enum.TryParse(name, out Route route)) return null;
        if (route is Route.Unsure or Route.NoFamily or Route.NoSkin) return new Found(route, null);
        if (hit.Dir.Length == 0 || catalogOf(hit.Dir) is not { } catalog) return null;
        string slot = route == Route.SkinOnly ? reading.Slot : reading.Limb;
        string want = RefitCore.Normal(hit.Rel);
        if (catalog.For(slot, race).FirstOrDefault(o => RefitCore.Normal(o.Rel) == want) is not { } option) return null;
        return new Found(route, new AutoRefitDecisions.Pick(hit.Dir, option, hit.Confidence, hit.FromCloth));
    }
}
