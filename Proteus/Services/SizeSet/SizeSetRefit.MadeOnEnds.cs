using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Localization;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;

namespace Proteus.Services;

/// <summary>
/// A women's shoe with no skin at all — a boot all leather — sized on the body chosen under "Made on": each of the
/// author's sizes placed on that body's legs by where its shaft's cloth sits, or, where that is unclear, by the size its
/// option name gives (as KAN-6 places a skinless chest or legs piece, <see cref="TrustOn"/>). From there it is sized as
/// any shoe is (<c>SizeSetRefit.Ends.cs</c>): the author's foot kept, only the shaft refitted. The rules, with no file
/// read here but the bodies the cloth is measured against.
/// </summary>
internal static partial class SizeSetRefit
{
    /// <summary>How one of a skinless shoe's sizes was placed on the "Made on" body.</summary>
    internal enum EndMadeOnHow
    {
        /// <summary>By where its shaft's cloth sits: one size, clearly (<see cref="ClothClear"/>).</summary>
        Cloth,
        /// <summary>By the size its option name gives (<see cref="TrustOn"/>).</summary>
        Name,
        /// <summary>Neither.</summary>
        None,
    }

    /// <summary>One of a skinless shoe's sizes as placed on the "Made on" body.</summary>
    /// <param name="On">The body's legs it was placed on; null for <see cref="EndMadeOnHow.None"/>.</param>
    /// <param name="Confidence">The cloth reading's; <see cref="Confidence.NoBodyMesh"/> for a placing by name.</param>
    /// <param name="ClothOn">How its shaft's cloth sits on <paramref name="On"/> (<see cref="BodySizeMatch.ClothAgainst"/>):
    /// what Check in game is told by; null when not measured.</param>
    /// <param name="Own">The table size it is (<see cref="TrustedOwn"/>): that size of the body is the author's own,
    /// never remade. Null off the table.</param>
    /// <param name="Fit">What the placing came to: <see cref="MadeOnFit.Taken"/> by cloth, <see cref="MadeOnFit.Trusted"/>
    /// by name, else the name's reason (<see cref="MadeOnFit.NoMatch"/>, <see cref="MadeOnFit.NoTable"/>).</param>
    /// <param name="Why">For the log.</param>
    internal sealed record EndMadeOnRead(EndMadeOnHow How, BodyOption? On, Confidence Confidence,
                                         (int Near, int Inside, float Gap)? ClothOn, TrustedSize? Own, MadeOnFit Fit,
                                         string Why);

    /// <summary>
    /// The "Made on" body a shoe's own sizes are read on: only a shoe read as having no skin at all
    /// (<see cref="KeepEnds.Route.NoSkin"/>), with "Made on" set to an installed body that has legs of the shoe's race.
    /// Null otherwise — and the shoe is read exactly as before. Never a glove's.
    /// </summary>
    internal static BodyModIndex.Entry? EndMadeOnBody(Request request, string slot, string race, KeepEnds.Route route)
    {
        if (route != KeepEnds.Route.NoSkin || slot != "_sho" || request.MadeOn is not { } dir) return null;
        var body = request.Bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase));
        return body != null && body.Catalog.For("_dwn", race).Count > 0 ? body : null;
    }

    /// <summary>
    /// The legs a skinless shoe's shaft is ranked among on the "Made on" body: the named sizes' legs
    /// (<see cref="SupportedBodies.MadeOnTable"/> — for a shoe following a chest, the legs each chest size is worn with)
    /// that the body holds, so a reading never lands on a variant the author's sizes are not named by; all its legs of the
    /// race when it has no such table.
    /// </summary>
    internal static IReadOnlyList<BodyOption> MadeOnLegs(BodyModIndex.Entry madeOn, string race, bool yas, bool followsChest)
    {
        var all = madeOn.Catalog.For("_dwn", race);
        if (SupportedBodies.MadeOnTable(madeOn, followsChest ? "_top" : "_dwn", race, yas) is not { } table) return all;
        var named = table.Sizes.Select(s => followsChest ? s.LegsRel : s.Rel).OfType<string>()
                         .Select(rel => OptionByFile(all, rel)).OfType<BodyOption>()
                         .DistinctBy(o => RefitCore.Normal(o.Rel)).ToList();
        return named.Count > 0 ? named : all;
    }

    /// <summary>
    /// Whether a shaft's cloth reading names one size clearly: <see cref="Confidence.Likely"/> or
    /// <see cref="Confidence.Exact"/>. A <see cref="Confidence.Guess"/> clears every size — nothing it was fitted to — and
    /// an <see cref="Confidence.Ambiguous"/> one tells none apart: the option name is the better evidence.
    /// </summary>
    internal static bool ClothClear((BodyOption Option, float Gap, BodySizeMatch.Ranking Ranking)? sits)
        => sits is { Ranking.Confidence: Confidence.Likely or Confidence.Exact };

    /// <summary>
    /// Whether a clear cloth reading puts one of the author's sizes on a size of the body another of them is placed on —
    /// by its own cloth, or by its name where its cloth is unclear. The shaft then cannot tell the author's sizes apart
    /// there, and every size is placed by its name instead. Two sizes placed on one by name alone are no collision: the
    /// author named them so.
    /// </summary>
    /// <param name="reads">Each of the author's options: its clear cloth reading (null when unclear) and the size its name
    /// gives (null for none).</param>
    /// <param name="why">For the log; "" when none collide.</param>
    internal static bool ClothCollides(IReadOnlyList<(string? Option, BodyOption? Cloth, BodyOption? ByName)> reads,
                                       out string why)
    {
        why = "";
        for (int i = 0; i < reads.Count; i++)
            for (int j = i + 1; j < reads.Count; j++)
            {
                var (a, b) = (reads[i].Cloth ?? reads[i].ByName, reads[j].Cloth ?? reads[j].ByName);
                if (a == null || b == null || (reads[i].Cloth == null && reads[j].Cloth == null)
                    || RefitCore.Normal(a.Rel) != RefitCore.Normal(b.Rel)) continue;
                why = $"its shaft's cloth puts \"{reads[i].Option ?? "its file"}\" and \"{reads[j].Option ?? "its file"}\" "
                    + $"both on {a.Label}: every size taken by its name";
                return true;
            }
        return false;
    }

    /// <summary>
    /// One of a skinless shoe's sizes placed on the "Made on" body: on the size its shaft's cloth sits on when that
    /// reading is kept (clear, and no collision), else on the size its option name gives (<see cref="TrustOn"/>), else
    /// nowhere — with the name's reason, the last step tried.
    /// </summary>
    /// <param name="keptCloth">The shaft's cloth reading, when clear and kept; null to place by name.</param>
    /// <param name="yas">The shoe is rigged for YAS: the table it is placed on by name is the body's YAS one.</param>
    /// <param name="followsChest">It follows a chest: named by the chest table, placed on the legs that size is worn with.</param>
    /// <param name="clothOn">How the shaft's cloth sits on a size (<see cref="BodySizeMatch.ClothAgainst"/>), for Check
    /// in game; given by the caller, which holds the reading.</param>
    internal static EndMadeOnRead PlaceEndOnMadeOn((BodyOption Option, float Gap, BodySizeMatch.Ranking Ranking)? keptCloth,
                                                   string? option, BodyModIndex.Entry madeOn, string race, bool yas,
                                                   bool followsChest, Func<BodyOption, (int Near, int Inside, float Gap)?> clothOn)
    {
        var table = SupportedBodies.MadeOnTable(madeOn, followsChest ? "_top" : "_dwn", race, yas);
        if (keptCloth is { } c)
        {
            string want = RefitCore.Normal(c.Option.Rel);
            var size = table?.Sizes.FirstOrDefault(s => (followsChest ? s.LegsRel : s.Rel) is { } rel
                                                        && RefitCore.Normal(rel) == want);
            var own = table != null && size != null ? new TrustedSize(table.Key, size.Name, c.Option, null) : null;
            return new EndMadeOnRead(EndMadeOnHow.Cloth, c.Option, c.Ranking.Confidence, clothOn(c.Option), own,
                                     MadeOnFit.Taken,
                                     $"read on {madeOn.Name} by its shaft's cloth as {c.Option.Label} ({c.Ranking.Confidence}, "
                                     + $"{c.Gap * 1000f:F1} mm off)");
        }
        var (trusted, fit, why) = TrustOn(option, table, madeOn.Catalog, "_dwn", race, followsChest);
        return trusted is { } t
            ? new EndMadeOnRead(EndMadeOnHow.Name, t.On, Confidence.NoBodyMesh, clothOn(t.On), t, MadeOnFit.Trusted,
                                $"taken as {madeOn.Name} {t.On.Label} by its name ({why})")
            : new EndMadeOnRead(EndMadeOnHow.None, null, Confidence.NoBodyMesh, null, null, fit,
                                $"not placed on {madeOn.Name}: {why}");
    }

    /// <summary>
    /// The key one of the author's shoe sizes is kept under for the run: its file — or, placed by name, its file and
    /// option, since the size a name gives hangs on the option, not the file (as <c>Sources.ReadMadeOn</c> places by name
    /// again on a cached reading).
    /// </summary>
    internal static string EndSourceKey(string file, string? option, bool byName)
        => byName ? RefitCore.Normal(file) + "|" + option : RefitCore.Normal(file);

    /// <summary>How one of the author's sizes reads, for choosing the one the sizes are cut from (<see cref="PrimaryOf"/>).</summary>
    internal enum EndReadKind
    {
        None,
        /// <summary>Read off the shoe itself, or by its shaft's cloth on the "Made on" body.</summary>
        Read,
        /// <summary>Placed on the "Made on" body by its name.</summary>
        Name,
    }

    /// <summary>
    /// Which of the author's sizes the sizes are cut from: the first, in the author's order, that is read (reading stops
    /// there, as it always has), else the first placed by name; -1 for none. Without "Made on" none is placed by name, so
    /// this is the first that reads, as before.
    /// </summary>
    internal static int PrimaryOf(int count, Func<int, EndReadKind> read)
    {
        int byName = -1;
        for (int i = 0; i < count; i++)
            switch (read(i))
            {
                case EndReadKind.Read: return i;
                case EndReadKind.Name when byName < 0: byName = i; break;
            }
        return byName;
    }

    /// <summary>
    /// What Check in game says of a size cut from one of a skinless shoe's sizes placed on the "Made on" body: that the
    /// size was taken by its name (<see cref="Strings.SizeSet.MadeOnTrustedFmt"/>); and that its cloth sits more than
    /// <see cref="EndClothGapWarn"/> off the size it was placed on — or, placed by name, inside it — when enough of it is
    /// near to tell (<see cref="Strings.SizeSet.EndClothGapFmt"/>).
    /// </summary>
    /// <param name="authorOption">The author's size the sizes were cut from.</param>
    internal static IReadOnlyList<string> EndMadeOnWarnings(string piece, string? authorOption, EndMadeOnRead read,
                                                            string bodyName)
    {
        var warnings = new List<string>();
        if (read.How == EndMadeOnHow.None) return warnings;
        if (read.How == EndMadeOnHow.Name) warnings.Add(string.Format(Strings.SizeSet.MadeOnTrustedFmt, piece, bodyName));
        if (read.ClothOn is { Near: >= BodySizeMatch.MinEvidence } on
            && (on.Gap > EndClothGapWarn
                || (read.How == EndMadeOnHow.Name && on.Inside > on.Near * KeepEnds.SiblingInsideAtMost)))
            warnings.Add(string.Format(Strings.SizeSet.EndClothGapFmt, piece, authorOption ?? piece, on.Gap * 1000f, bodyName));
        return warnings;
    }

    /// <summary>
    /// What is said of a shoe none of whose sizes could be read or placed: with "Made on" tried on it, why none could be
    /// placed there (<see cref="UnreadableNote"/>); else as before.
    /// </summary>
    /// <param name="anyRead">Any of its files could be read at all.</param>
    /// <param name="fits">The reasons of the sizes tried on the "Made on" body; empty when none was.</param>
    internal static (string Note, string Code) EndsUnplaced(string piece, bool anyRead, string? madeOn, string? madeOnName,
                                                            IReadOnlyCollection<MadeOnFit> fits)
    {
        if (madeOn != null && fits.Count > 0) return UnreadableNote(piece, madeOn, madeOnName, false, fits, noneParsed: false);
        return anyRead ? (string.Format(Strings.SizeSet.UnreadableFmt, piece), Codes.Unreadable)
                       : (string.Format(Strings.SizeSet.EndsUnreadableFmt, piece), Codes.EndsUnreadable);
    }

    /// <summary>
    /// How far a kept foot's join to the refitted shaft stretched: of every triangle edge from a held vertex to a moved
    /// one, cloth and skin alike, how many there are and the most any grew (a shrunk edge counts as none). A skinless
    /// boot's ankle is all cloth, which <see cref="KeepEnds.SeamStretch"/> — skin only — never sees.
    /// </summary>
    internal static (int Edges, float Worst) JoinStretch(IEnumerable<int[]> triangleLists, IReadOnlySet<int> held,
                                                         Func<int, Vector3> before, Func<int, Vector3> after)
    {
        int edges = 0;
        float worst = 0f;
        foreach (var triangles in triangleLists)
            for (int t = 0; t + 2 < triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                    if (held.Contains(a) == held.Contains(b)) continue;
                    edges++;
                    worst = MathF.Max(worst, Vector3.Distance(after(a), after(b)) - Vector3.Distance(before(a), before(b)));
                }
        return (edges, worst);
    }

    /// <inheritdoc cref="JoinStretch(IEnumerable{int[]}, IReadOnlySet{int}, Func{int, Vector3}, Func{int, Vector3})"/>
    internal static (int Edges, float Worst) JoinStretch(ModelParts garment, IReadOnlySet<int> held, BodyRetarget.Planned plan)
    {
        Vector3 Before(int v) => new(garment.Positions[v * 3], garment.Positions[v * 3 + 1], garment.Positions[v * 3 + 2]);
        Vector3 After(int v)
        {
            var d = plan.Edit.DeltaAt(v);
            return Before(v) + new Vector3(d.X, d.Y, d.Z);
        }
        return JoinStretch(garment.Parts.Select(p => p.Triangles), held, Before, After);
    }
}
