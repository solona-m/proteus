using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// "Refit for" the other sex: a women's garment made a men's, and a men's a women's — the decisions alone, shared by
/// the Body size panel, the headless job and the CLI.
/// <para/>
/// Only between the two shared shapes every body mod is made for, Midlander M (c0101) and Midlander F (c0201): the bend
/// between them is the one racial deformer link the bibo↔TBSE maps were baked with, and every other race reaches the
/// result by falling through to it. c0201 falls through to c0101, so women's→men's is a race BELOW the drawn one
/// (<see cref="RacialModelBake.Route.Lowered"/>), and men's→women's a race above it (an up route) — both saved beside
/// the drawn model at the picked race's path, with its EQDP entry.
/// <para/>
/// Only chest and legs garments (<see cref="Supported"/>), and only bodies whose skin layouts the maps join: a woman's
/// bibo or the game's, a man's TBSE or the game's (<see cref="LayoutsPair"/>). The auto refit never crosses the sexes:
/// it writes into mods with nobody looking. The size set does, women's to TBSE and TBSE-X only, when the author ticks
/// those bodies "for men" (see <c>SizeSetRefit.CrossSex.cs</c>), into a group of their own.
/// <para/>
/// The game's own gear crosses the sexes in fewer cells (<see cref="OfferableForGame"/>): women's legs made men's, from
/// the game's women's body, measured on the game's own garments (CrossSexRefitVanillaLocalTests).
/// </summary>
internal static class CrossSexRefit
{
    internal const ushort Men = 101, Women = 201;

    /// <summary>
    /// The directions and slots that ship: each was measured against the quality gates on a real garment
    /// (CrossSexRefitLocalTests). A cell left out is not offered anywhere; the game's own gear is offered fewer still
    /// (<see cref="GameCells"/>).
    /// <para/>
    /// A women's top made a men's turns more of its cloth over than any other — flattening a bust onto a pec, Sheer
    /// Elegance's turned 115 of its 10,436 cloth faces, against 8 between women's bodies (measured 2026-10-20) — and is
    /// offered all the same: the folds are said, in the warning colour, never a reason to refuse.
    /// </summary>
    private static readonly HashSet<(ushort From, ushort To, string Slot)> Cells =
    [
        (Women, Men, "_top"), (Women, Men, "_dwn"),
        (Men, Women, "_top"), (Men, Women, "_dwn"),
    ];

    /// <summary>Whether a garment drawn as <paramref name="from"/> in <paramref name="slot"/> may be refitted for
    /// <paramref name="to"/> of the other sex.</summary>
    internal static bool Supported(ushort from, ushort to, string? slot)
        => slot != null && Cells.Contains((from, to, slot.ToLowerInvariant()));

    /// <summary>Whether two races are of different sexes (odd race numbers are men's).</summary>
    internal static bool CrossesSex(ushort a, ushort b) => a != 0 && b != 0 && a / 100 % 2 != b / 100 % 2;

    /// <summary>Whether the other-sex race <paramref name="race"/> is offered for a garment drawn as <paramref name="drawn"/>.</summary>
    internal static bool Offerable(ushort drawn, ushort race, string? slot)
        => CrossesSex(drawn, race) && Supported(drawn, race, slot);

    /// <summary>
    /// The cells the game's own gear is offered in: women's legs made men's, alone. Each cell is measured on real
    /// garments before it ships, and the game's tops and its men's garments made women's have not been — its garments are
    /// weighted against the game's bodies, not a body mod's, and fit the other way round.
    /// </summary>
    private static readonly HashSet<(ushort From, ushort To, string Slot)> GameCells = [(Women, Men, "_dwn")];

    /// <summary>Whether the other-sex race <paramref name="race"/> is offered for the game's own garment drawn as
    /// <paramref name="drawn"/>: <see cref="Offerable"/>, in the narrower <see cref="GameCells"/>.</summary>
    internal static bool OfferableForGame(ushort drawn, ushort race, string? slot)
        => Offerable(drawn, race, slot) && GameCells.Contains((drawn, race, slot!.ToLowerInvariant()));

    /// <summary>Women's→men's: the picked race is one the drawn race falls through to (c0201 → c0101).</summary>
    internal static bool Below(ushort drawn, ushort race)
        => CrossesSex(drawn, race) && ModelRace.Fallback(drawn / 100) == race / 100;

    /// <summary>
    /// Whether a body of <paramref name="from"/> is bent DOWN to reach <paramref name="to"/>: on the same-sex line as
    /// before, or across the sexes to a race <paramref name="from"/> falls through to.
    /// </summary>
    internal static bool BendsDown(ushort from, ushort to)
        => from != to && (RacialModelBake.SameSexLine(from).Contains(to) || Below(from, to));

    /// <summary>
    /// The races whose bodies the made-for side is read for. Across the sexes only the drawn race's own: a made-for mod
    /// with none of them would otherwise hand over the picked race's own bodies, the same mesh as the target, and the
    /// refit would move nothing — a women's top bent and saved floating over a man.
    /// </summary>
    internal static IReadOnlyList<ushort> MadeForLine(ushort drawn, ushort to)
        => CrossesSex(drawn, to) ? [drawn] : OtherRaceRefit.LineDown(drawn, to).ToList();

    /// <summary>
    /// The sexes of the two sides a body pair is built between: the made-for side's and the refit-onto side's. Under an
    /// explicit cross-sex pick the made-for bodies are the drawn race's and the targets the picked race's; otherwise
    /// both are the garment's, exactly as before (the automatic route keeps reading both as the garment's sex).
    /// </summary>
    internal static (bool Source, bool Target) Sexes(RacialModelBake.Route route, bool explicitPick, bool garmentMale)
        => explicitPick && route.Any && CrossesSex(route.Drawn, route.To)
            ? (IsMale(route.Drawn), IsMale(route.To))
            : (garmentMale, garmentMale);

    internal static bool IsMale(ushort race) => race / 100 % 2 == 1;

    /// <summary>
    /// Whether a women's layout and a men's layout are joined by the shipped maps, one hop: bibo or the game's women's
    /// layout (gen2, the mirrored half of bibo's sheet) with TBSE or the men's game layout (its half). gen3 has no map
    /// of its own to TBSE, and a refit never takes a route through two maps.
    /// </summary>
    internal static bool LayoutsPair(string women, string men)
        => women is "bibo" or "gen2" && men is UVRemapService.TbseSpace or UVRemapService.MaleVanillaSpace;

    /// <summary>
    /// The races besides the picked one that wear a model saved at <paramref name="saved"/>'s path: every playable race
    /// whose fall-through reaches it before it reaches <paramref name="drawn"/> — the model refitted, which the item has,
    /// and which stops every chain through it. A c0101 save made from a c0201 model is worn by the men and by the
    /// Lalafell women (c1201 → c1101 → c0101), not by the women who fall through to c0201.
    /// </summary>
    /// <param name="hasOwn">Whether a race has a model of its own of the item (<see cref="OwnModels"/>): a race that has
    /// one — or passes one on its way down — draws that, not the save. Asked of each race and those between it and
    /// <paramref name="saved"/>, never of <paramref name="saved"/> itself: the save is the model there. Null: none
    /// has.</param>
    internal static IReadOnlyList<ushort> FallThroughTo(ushort saved, ushort drawn, Func<ushort, bool>? hasOwn = null)
    {
        var races = new List<ushort>();
        for (int n = 1; n <= ModelRace.Names.Length * 2; n++)
        {
            ushort code = (ushort)(n * 100 + 1);
            if (code == saved || code == drawn || hasOwn?.Invoke(code) == true) continue;
            for (int cur = ModelRace.Fallback(n), guard = 0; cur != 0 && guard < 8; cur = ModelRace.Fallback(cur), guard++)
            {
                int at = cur * 100 + 1;
                if (at == drawn) break;
                if (at == saved) { races.Add(code); break; }
                if (hasOwn?.Invoke((ushort)at) == true) break;
            }
        }
        return races;
    }

    /// <summary>
    /// Which races have a model of their own of the garment drawn at <paramref name="drawnPath"/>, for
    /// <see cref="FallThroughTo"/>: one the garment's mod publishes at that race's path — a refit saved there earlier as
    /// much as the author's, since either is what the race draws — or one the game ships there. A mod's garment sits on a
    /// game item, so a race the game has a model of draws that one just as it would the mod's.
    /// </summary>
    /// <param name="redirects">The garment mod's redirects; null for the game's own gear.</param>
    /// <param name="gameHas">Whether the game's own data ships a file at a game path; null: none asked.</param>
    internal static Func<ushort, bool> OwnModels(string drawnPath, IEnumerable<PenumbraModMeta.Redirect>? redirects,
                                                 Func<string, bool>? gameHas)
    {
        var published = (redirects ?? []).Select(r => r.GamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return race =>
        {
            string path = RacialModelBake.WithRace(drawnPath, race);
            return published.Contains(path) || gameHas?.Invoke(path) == true;
        };
    }

    /// <summary>
    /// Whether a refit from <paramref name="drawn"/> for <paramref name="to"/> lifts the cloth under the new breasts: never
    /// across the sexes (see <c>BodyRetarget.Plan</c>'s liftUnderbust), every other refit as before. Measured both ways:
    /// a men's top made a women's folds 8 faces without it, 13 with (Lumberjack); a women's top made a men's 101 without,
    /// 115 with, and clips no more (Sheer Elegance, 2026-10-21) — a man's chest has no breast, but the lift still seeds
    /// over every skin face that looks down and forward.
    /// </summary>
    internal static bool LiftsUnderbust(ushort drawn, ushort to) => !CrossesSex(drawn, to);

    /// <summary>
    /// The usage error for a CLI <c>--for-race</c> run, or null when it may run. <paramref name="given"/> holds every
    /// option given on the command line.
    /// </summary>
    /// <param name="fromSkeleton">The <c>--from-skeleton</c> file, when given: one that is not there would be passed over
    /// for the game's skeleton, and the run would then ask for it.</param>
    /// <param name="exists">Whether a file exists (the disk's, by default).</param>
    internal static string? ValidateCli(string? race, string? forRace, string slot, IReadOnlyCollection<string> given,
                                        string? fromSkeleton = null, Func<string, bool>? exists = null)
    {
        if (fromSkeleton != null && !(exists ?? System.IO.File.Exists)(fromSkeleton))
            return $"--from-skeleton names no file: \"{fromSkeleton}\"";
        if (!ushort.TryParse(race, out ushort from) || !ushort.TryParse(forRace, out ushort to)
            || !(from is Men or Women && to is Men or Women) || !CrossesSex(from, to))
            return "--for-race crosses the sexes between 0101 and 0201 only";
        if (!Supported(from, to, slot))
            return "--for-race refits chest (_top) and legs (_dwn) garments only";
        if (!given.Contains("game") || !given.Contains("uvmaps") || !given.Contains("from-root"))
            return "--for-race needs --game, --uvmaps and --from-root";
        if (new[] { "legs", "legs-to", "chest-from", "chest-to", "finish", "detect", "whole", "keep-default" }.Any(given.Contains))
            return "--for-race is not combined with --legs, --legs-to, --chest-from, --chest-to, --finish, --detect, "
                 + "--whole or --keep-default yet";
        return null;
    }

    /// <summary>
    /// The run-time check a cross-sex refit must pass before it may be saved: how much of the body the maps matched.
    /// Under it the refit would be read against a body it barely knows. Never its folds: those are said
    /// (<see cref="FoldLineFor"/>), and the creator decides.
    /// </summary>
    internal static bool CoverageAllowed(double coverage) => coverage >= MinCoverage;

    internal const double MinCoverage = 0.9;

    internal enum FoldLine { None, Report, CrossSex }

    /// <summary>
    /// Which fold line a refit shows: across the sexes, the cloth faces it turned over (<see cref="ClothFolded(byte[], BodyRetarget.Planned, Action{string}?)"/>),
    /// said as holes in game; otherwise, or with none of those, the refit's own count of faces left turned over.
    /// One line, so two different counts never stand together.
    /// </summary>
    internal static FoldLine FoldLineFor(bool crossSex, int clothFolded, int reportFolded)
        => crossSex && clothFolded > 0 ? FoldLine.CrossSex
         : reportFolded > 0 ? FoldLine.Report
         : FoldLine.None;

    /// <summary>
    /// Cloth faces of 1 mm² or more that a refit turned over: <paramref name="garment"/> as it went in against the same
    /// vertices as the plan laid them, before the rebuild. A face turned over is a hole in game. Body skin is left out —
    /// the skin swap replaces it.
    /// </summary>
    internal static int ClothFolded(byte[] garment, BodyRetarget.Planned plan, Action<string>? where = null)
    {
        var before = ModelPartReader.Read(garment);
        var after = ModelPartReader.Read(MeshVolumeService.Inflate(garment, plan.Edit).Model);
        return before == null || after == null ? 0 : ClothFolded(before, after, where);
    }

    /// <inheritdoc cref="ClothFolded(byte[], BodyRetarget.Planned, Action{string}?)"/>
    internal static int ClothFolded(ModelParts before, ModelParts after, Action<string>? where = null)
    {
        int folded = 0;
        foreach (var part in before.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                var was = System.Numerics.Vector3.Cross(At(before.Positions, b) - At(before.Positions, a),
                                                        At(before.Positions, c) - At(before.Positions, a));
                if (was.Length() * 0.5f < 1e-6f) continue;
                var now = System.Numerics.Vector3.Cross(At(after.Positions, b) - At(after.Positions, a),
                                                        At(after.Positions, c) - At(after.Positions, a));
                if (System.Numerics.Vector3.Dot(was, now) > 0f) continue;
                folded++;
                where?.Invoke($"{part.Label}: {was.Length() * 0.5f * 1e6f:F1} mm² at {At(after.Positions, a)}");
            }
        return folded;
    }

    private static System.Numerics.Vector3 At(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
}
