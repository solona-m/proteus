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
    public static bool TryBuild(ModelParts source, float[] sourceUv, ModelParts target, float[] targetUv, string what,
                                out IBodyCorrespondence? correspondence, out string refusal,
                                UVRemapService? uvRemap = null)
    {
        if (IdentityCorrespondence.TryBuild(source, target, what, out var identity, out _, sourceUv, targetUv))
        {
            correspondence = identity;
            refusal = "";
            return true;
        }

        // Two layouts: carried across by the texture maps, or refused — landing a bibo uv on a gen3 atlas as it stands
        // would put the chest on the back.
        UVRemapService.UvConversion? convert = null;
        string? from = LayoutOf(source), to = LayoutOf(target);
        if (from != null && to != null && from != to)
        {
            // A mirrored layout (gen2) shares one half between both sides; unmirroring sends each side to its own.
            convert = uvRemap?.UvConverter(from, to, unmirror: true);
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

    /// <summary>The texture layout a body's skin is drawn in ("bibo", "gen3", "gen2"), or null when none is known.</summary>
    internal static string? LayoutOf(ModelParts body)
    {
        foreach (var part in body.Parts)
            if (part.Island < 0 && SecondSkinWriter.SkinMaterialBodyType(part.Material) is { } layout)
                return layout;
        return null;
    }
}
