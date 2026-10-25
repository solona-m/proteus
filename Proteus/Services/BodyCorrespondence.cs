using System;
using System.Linq;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// Chooses how one body maps onto another. The one place that decides, so the Studio, the tests and the golden
/// baselines all get the same answer for the same pair.
/// <para/>
/// Two bodies that are the same mesh are mapped vertex for vertex (<see cref="IdentityCorrespondence"/>): exact, and
/// the common case for two sizes of one body. Anything else that shares the Bibo+ texture layout — a different
/// Neolithe family, an NSFW chest that re-maps some uvs, Rue+ — is mapped by texture coordinate
/// (<see cref="UvAtlasCorrespondence"/>). Only bodies with different layouts are refused, because for those there is
/// genuinely no way to say which point is which. Two layouts a transfer map or the gen2 affine converts between are
/// mapped through it; a mirrored layout's body (gen2) is judged there by how much of its skin's surface lands — counting
/// only the landings the skin around them agrees with — and the skin its target's layout has no place for follows the
/// skin around it. A body option that draws underwear where the other body has skin — Muse+'s SFW legs model boxers
/// with the skin cut away under them — is in one layout too: the skin with no place there follows the skin around it
/// onto that underwear, and the rest of the refit reads the target as it is worn.
/// <para/>
/// This replaced a guard that refused any pair that was not the same mesh. That was the wrong kind of answer: the pair
/// that first hit it was two Neolithe chests with identical structure and 98.8% of their uvs in common, and every
/// refit onto Rue+ would have hit it too.
/// </summary>
internal static class BodyCorrespondence
{
    /// <param name="sourceUv">uv0 per source vertex, in <see cref="ModelParts.Positions"/> order.</param>
    /// <param name="targetUv">uv0 per target vertex, in <see cref="ModelParts.Positions"/> order.</param>
    /// <param name="uvRemap">Converts between texture layouts, for two bodies that do not share one. Null keeps the
    /// same-layout requirement: a pair in different layouts is refused.</param>
    /// <param name="male">Both bodies are men's — known from the models' game paths, which the caller has (see
    /// <see cref="BodySizeCatalog.For(string, string?)"/>, which never offers a body of the other sex). Never from the
    /// skin material: a body mod may name a woman's skin <c>mt_c0101b0001_bibo</c> (Tre does), since the game swaps
    /// the race code for the wearer's own. With <paramref name="targetMale"/>, the source's sex alone.</param>
    /// <param name="targetMale">The target body's sex, when it is not the source's: a refit for the other sex (see
    /// <see cref="CrossSexRefit"/>). Null — every caller but that one — reads both as <paramref name="male"/>.</param>
    public static bool TryBuild(ModelParts source, float[] sourceUv, ModelParts target, float[] targetUv, string what,
                                out IBodyCorrespondence? correspondence, out string refusal,
                                UVRemapService? uvRemap = null, bool male = false, bool? targetMale = null)
        => TryBuild(source, sourceUv, target, targetUv, what, out correspondence, out refusal, out _, uvRemap, male, targetMale);

    /// <summary>As above, also giving how the atlas landed (null for an identity match).</summary>
    internal static bool TryBuild(ModelParts source, float[] sourceUv, ModelParts target, float[] targetUv, string what,
                                  out IBodyCorrespondence? correspondence, out string refusal,
                                  out UvAtlasCorrespondence.Stats? stats,
                                  UVRemapService? uvRemap = null, bool male = false, bool? targetMale = null)
    {
        stats = null;
        bool tMale = targetMale ?? male;
        if (male != tMale)
            return TryBuildAcrossSexes(source, sourceUv, target, targetUv, what, out correspondence, out refusal,
                                       out stats, uvRemap, male, tMale);

        // A body may store its uvs a whole tile away from another's — Bibo+'s legs run -0.67..-0.02 where Neolithe's
        // run 0.02..0.98. The sheet repeats, so both draw the same texture, but compared literally they have nothing in
        // common: the pair was refused as two layouts, which is what sent a stocking to be refitted on the feet alone.
        sourceUv = SameTile(sourceUv);
        targetUv = SameTile(targetUv);

        if (IdentityCorrespondence.TryBuild(source, target, what, out var identity, out _, sourceUv, targetUv))
        {
            correspondence = identity;
            refusal = "";
            return true;
        }

        // Two layouts: carried across by the texture maps, or refused — landing a bibo uv on a gen3 atlas as it stands
        // would put the chest on the back.
        UVRemapService.UvConversion? convert = null, onSheet = null;
        bool byArea = false;
        string? from = LayoutOf(source, male), to = LayoutOf(target, male);
        if (from != null && to != null && from != to)
        {
            // A mirrored layout (gen2) shares one half between both sides; unmirroring sends each side to its own, and
            // folding lands a point of the other side on the half a mirrored target keeps — the atlas then picks the
            // twin on the point's own side. The transfer maps are all between women's layouts; of a man's, only the
            // game's and The Body's convert, and those by the affine alone (TBSE is the vanilla sheet doubled).
            bool mapped = IsMaleLayout(from) == IsMaleLayout(to)
                       && (!IsMaleLayout(from) || (IsAffineMale(from) && IsAffineMale(to)));
            convert = mapped ? uvRemap?.UvConverter(from, to, unmirror: true, fold: true) : null;
            if (convert == null)
            {
                correspondence = null;
                refusal = string.Format(ProteusLoc.Localize("Parts.Retarget.Refuse.NoLayoutMap.Fmt",
                              "The {0} models use different texture layouts ({1} and {2}), and Proteus has no map " +
                              "between them, so there is no way to tell which point of one body is which point of the " +
                              "other."), what, from, to);
                return false;
            }
            onSheet = uvRemap?.UvConverter(from, to, unmirror: true, fold: true, reach: OnSheetReach);

            // A mirrored layout's body (gen2: Tight&Firm Gen2, the game's own) may draw detail where its doubled layout
            // has no skin; judged by surface, that detail follows the skin around it. Only onto that doubled layout,
            // which the affine alone reaches: through a transfer map (gen2 onto gen3) the nearest-pixel search snaps
            // whole runs of a sparse shin onto the wrong island, and AB Body's shin turned over. Every other pair is
            // judged by count alone, as it always was.
            byArea = string.Equals(UVRemapService.DoubledSpaceOf(from), to, StringComparison.OrdinalIgnoreCase);
        }

        if (UvAtlasCorrespondence.TryBuild(source, sourceUv, target, targetUv, what, out var atlas, out refusal, convert,
                                           onSheet, byArea, sameLayout: from != null && from == to))
        {
            correspondence = atlas;
            return true;
        }

        correspondence = null;
        return false;
    }

    /// <summary>
    /// A women's body and a men's matched for a refit across the sexes: always through the bibo↔TBSE maps, one hop, and
    /// never as the same mesh — a body mod's two sexes can share a topology without sharing a shape, and an identity
    /// match would carry the garment across untouched.
    /// </summary>
    private static bool TryBuildAcrossSexes(ModelParts source, float[] sourceUv, ModelParts target, float[] targetUv,
                                            string what, out IBodyCorrespondence? correspondence, out string refusal,
                                            out UvAtlasCorrespondence.Stats? stats, UVRemapService? uvRemap,
                                            bool sourceMale, bool targetMale)
    {
        correspondence = null;
        stats = null;
        sourceUv = SameTile(sourceUv);
        targetUv = SameTile(targetUv);
        string? from = LayoutOf(source, sourceMale), to = LayoutOf(target, targetMale);
        string? women = sourceMale ? to : from, men = sourceMale ? from : to;
        if (from == null || to == null || women == null || men == null || !CrossSexRefit.LayoutsPair(women, men))
        {
            refusal = string.Format(ProteusLoc.Localize("Parts.Retarget.Refuse.CrossSexLayout.Fmt",
                          "Across the sexes, Proteus can only match women's bodies in the Bibo+ or game layout to men's " +
                          "bodies in the TBSE or game layout. The {0} models use {1} and {2}."),
                          what, from ?? "?", to ?? "?");
            return false;
        }

        var convert = uvRemap?.UvConverter(from, to, unmirror: true, fold: true);
        if (convert == null)
        {
            // On disk but unreadable is a map that failed, not one still to come.
            refusal = uvRemap?.MenMapsPresent() == true
                ? string.Format(ProteusLoc.Localize("Parts.Retarget.Refuse.NoLayoutMap.Fmt",
                      "The {0} models use different texture layouts ({1} and {2}), and Proteus has no map " +
                      "between them, so there is no way to tell which point of one body is which point of the " +
                      "other."), what, from, to)
                : ProteusLoc.Localize("Parts.Retarget.Refuse.MenMapsMissing",
                      "The body maps a refit across the sexes needs aren't here yet. Proteus is downloading them; " +
                      "refit again once they've arrived.");
            return false;
        }
        var onSheet = uvRemap!.UvConverter(from, to, unmirror: true, fold: true, reach: OnSheetReach);

        if (UvAtlasCorrespondence.TryBuild(source, sourceUv, target, targetUv, what, out var atlas, out refusal,
                                           out var measured, convert, onSheet, byArea: false, sameLayout: false))
        {
            correspondence = atlas;
            stats = measured;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The texture layout a body's skin is drawn in, or null when none is known. A woman's is "bibo", "gen3" or "gen2";
    /// a man's (<paramref name="male"/>) is named apart, because the material suffixes mean something else on a male
    /// body: its <c>_b</c> is The Body's layout ("tbse", which TBSE and the bodies built on it share) and its <c>_a</c>
    /// the game's own ("male vanilla").
    /// </summary>
    internal static string? LayoutOf(ModelParts body, bool male = false)
    {
        foreach (var part in body.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.SkinMaterialBodyType(part.Material) is not { } layout) continue;
            if (!male) return layout;
            return layout switch
            {
                "gen3" => "tbse",
                "gen2" => "male vanilla",
                _ => "male " + layout,
            };
        }
        return null;
    }

    /// <summary>
    /// Every uv brought into the same tile of the sheet, 0 to 1, so two bodies can be compared. A texture repeats, so a
    /// uv of -0.67 and one of 0.33 read the same texel and are the same point of the body; only their tile differs.
    /// Measured: Bibo+'s legs sit a tile below Neolithe's, and without this 0% of them land, with it 86%.
    /// </summary>
    internal static float[] SameTile(float[] uv)
    {
        var wrapped = (float[])uv.Clone();
        for (int i = 0; i < wrapped.Length; i++) wrapped[i] -= MathF.Floor(wrapped[i]);
        return wrapped;
    }

    /// <summary>
    /// How far, in transfer-map pixels (a 4096 sheet), a uv may be from the sheet and still count as on it: enough for
    /// a vertex on an island's border, whose own texel can fall just outside, and no more.
    /// </summary>
    internal const int OnSheetReach = 4;

    private static bool IsMaleLayout(string layout) => layout == "tbse" || layout.StartsWith("male ", StringComparison.Ordinal);

    private static bool IsAffineMale(string layout)
        => layout == UVRemapService.TbseSpace || layout == UVRemapService.MaleVanillaSpace;

}
