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
    public static bool TryBuild(ModelParts source, float[] sourceUv, ModelParts target, float[] targetUv, string what,
                                out IBodyCorrespondence? correspondence, out string refusal)
    {
        if (IdentityCorrespondence.TryBuild(source, target, what, out var identity, out _, sourceUv, targetUv))
        {
            correspondence = identity;
            refusal = "";
            return true;
        }

        if (UvAtlasCorrespondence.TryBuild(source, sourceUv, target, targetUv, what, out var atlas, out refusal))
        {
            correspondence = atlas;
            return true;
        }

        correspondence = null;
        return false;
    }
}
