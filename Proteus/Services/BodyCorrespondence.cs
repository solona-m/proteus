using System;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Chooses how one body maps onto another. The one place that decides, so the Studio, the tests and the golden
/// baselines all get the same answer for the same pair.
/// <para/>
/// Two bodies that are the same mesh are mapped vertex for vertex (<see cref="IdentityCorrespondence"/>): exact, and
/// the common case for two sizes of one body. Anything else that shares the Bibo+ texture layout — a different
/// Neolithe family, an NSFW chest that re-maps some uvs, Rue+ — is mapped by texture coordinate
/// (<see cref="UvAtlasCorrespondence"/>). Only bodies with different layouts are refused, because for those there is
/// genuinely no way to say which point is which.
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
    /// the race code for the wearer's own.</param>
    public static bool TryBuild(ModelParts source, float[] sourceUv, ModelParts target, float[] targetUv, string what,
                                out IBodyCorrespondence? correspondence, out string refusal,
                                UVRemapService? uvRemap = null, bool male = false)
    {
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
        UVRemapService.UvConversion? convert = null;
        string? from = LayoutOf(source, male), to = LayoutOf(target, male);
        if (from != null && to != null && from != to)
        {
            // A mirrored layout (gen2) shares one half between both sides; unmirroring sends each side to its own.
            // The maps are all between women's layouts; a man's has none, so a male pair in two layouts is refused.
            convert = IsMaleLayout(from) || IsMaleLayout(to) ? null : uvRemap?.UvConverter(from, to, unmirror: true);
            if (convert == null)
            {
                correspondence = null;
                refusal = $"The {what} models use different texture layouts ({from} and {to}), and Proteus has no map " +
                          "between them, so there is no way to tell which point of one body is which point of the other.";
                return false;
            }
        }

        if (UvAtlasCorrespondence.TryBuild(source, sourceUv, target, targetUv, what, out var atlas, out refusal, convert))
        {
            correspondence = atlas;
            return true;
        }

        correspondence = null;
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

    private static bool IsMaleLayout(string layout) => layout == "tbse" || layout.StartsWith("male ", StringComparison.Ordinal);

}
