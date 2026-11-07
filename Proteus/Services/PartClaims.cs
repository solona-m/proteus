using System;

namespace Proteus.Services;

/// <summary>
/// Whether a part read from one model file is the same part in a sibling file (another body option published at the
/// same game path): the same mesh, submesh and island, with the same ordinals (not just counts), material and attribute
/// mask. Vertex positions are not compared — sizes of a garment should differ there. The rule the part switches and the
/// matcap split both apply to sibling files, so the same edit means the same geometry there.
/// </summary>
internal static class PartClaims
{
    public static bool LineUp(ModelPart claim, ModelPart theirs) =>
        theirs.Mesh == claim.Mesh && theirs.Submesh == claim.Submesh && theirs.Island == claim.Island
        && theirs.AttributeMask == claim.AttributeMask
        && string.Equals(theirs.Material, claim.Material, StringComparison.OrdinalIgnoreCase)
        && theirs.Ordinals.AsSpan().SequenceEqual(claim.Ordinals);
}
