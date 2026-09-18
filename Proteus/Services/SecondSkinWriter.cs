using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Proteus.Services;

using static Proteus.Services.BodyBridge;

using static Proteus.Services.ToeCapSolver;

using static Proteus.Services.FaceUvRewriter;

using static Proteus.Services.MeshMath;

/// <summary>
/// Builds the "second skin" model: every skin part duplicated, pushed out along its normals, and MERGED
/// into a single model that rides one invisible accessory. Each part × layer is its own mesh group with
/// its layer's material. Each mesh keeps its SOURCE vertex format verbatim; only position (pushed),
/// vertex colour (whitened) and uv1 (mirrored from uv0) are rewritten — see <see cref="BuildVerbatim"/>.
///
/// Hard-won constraints, each a crash or a silent no-render:
///  - Every mesh needs its own vertex declaration (vertDeclCount == meshCount).
///  - RuntimeSize must be recomputed (vtxOffset - 0x44 - StackSize).
///  - Each mesh keeps its source's FULL submesh structure, or the bone range misses vertices (ModelDrawInit fault).
///  - Only declare materials that are actually used; an unresolvable declared material faults.
///  - Sources MUST be the body models the character is actually drawing, or the body pokes through the shell.
///  - Vertex BlendIndices are ubyte4 into the MESH'S OWN bone table (max 255 entries); only the table's
///    ENTRIES are remapped onto the union bone list, never vertex indices.
/// </summary>
public static partial class SecondSkinWriter
{
    /// <summary>How far the first shell sits off the skin (0.05 mm); <see cref="PushSweep"/> scales it.</summary>
    public const float BaseOffset = 5e-5f;

    /// <summary>Separation between adjacent shells (0.01 mm): layer k sits at BaseOffset + k * LayerSeparation.</summary>
    public const float LayerSeparation = 1e-5f;

    private const int DeclSize = 17 * 8;   // vertex declaration block, one per mesh
    private const int BBoxSize = 32;       // min Vec4 + max Vec4

    /// <param name="CapDeclined">Set when a toe cap was asked for but no binding placed it on this body, so none was emitted.</param>
    /// <param name="CapUsed">Which authored cap this shell got, and how well its binding fitted.</param>
    /// <param name="RedundantSubs">Submeshes the redundancy pass dropped as already drawn by something else.</param>
    /// <param name="TrimmedTris">Triangles removed as a second layer over an overlapping part (waist, wrist, ankle, thigh seams).</param>
    /// <param name="ToeReinforceMaps">
    /// Each layer's reinforced-toe region, keyed by material name: a square body-UV weight map at the layer's
    /// <see cref="SecondSkinLayer.ToeReinforceSize"/>, 255 over the toes fading to 0 behind <see cref="ToeLine"/>.
    /// Null when no layer asked for one or no cap was placed. Measured from the PLACED cap.
    /// </param>
    public readonly record struct Stats(int Meshes, int Submeshes, int Bones, int TrianglesIn,
                                        int TrianglesOut, int VerticesOut, string? CapDeclined = null,
                                        string? CapUsed = null,
                                        int RedundantSubs = 0, int RedundantTris = 0,
                                        int TrimmedTris = 0,
                                        IReadOnlyDictionary<string, (byte[] Mask, int Size)>? ToeReinforceMaps = null);

    /// <summary>
    /// A toe cap modelled for one body, with the binding that says where it sits on it. One per body; the
    /// binding covers the other axis (heels and any other foot model for the same body).
    /// </summary>
    /// <param name="Bind">Null only for a cap shipped without one, which is then used as authored.</param>
    /// <param name="Name">For the log.</param>
    public readonly record struct AuthoredCapSet(byte[] Cap, byte[]? Bind, string Name);

    /// <summary>The default mesh filter: body skin only.</summary>
    public static bool IsBodySkinMaterial(string materialName) => SkinMaterialBodyType(materialName) != null;

    /// <summary>
    /// A mesh filter keeping exactly the meshes bound to the named materials, compared by leaf name (the model
    /// stores them with a leading slash). A face model carries eyes, lashes and brows with their own UV layouts,
    /// so a non-body overlay names its material and gets that mesh and nothing else.
    /// </summary>
    public static Func<string, bool> KeepByLeaf(IReadOnlySet<string> leaves)
        => n => leaves.Contains(n.TrimStart('/'));

    /// <summary>One source model and everything the merge needs to know about it.</summary>
    /// <param name="Model">The .mdl bytes.</param>
    /// <param name="KeepMaterial">Which meshes to copy, by material name. Null = <see cref="IsBodySkinMaterial"/>.</param>
    /// <param name="EnabledShapes">Shape keys the game has enabled on this model, to bake.</param>
    /// <param name="UvConv">Vertex UV conversion into the shell's space. Null = already there, leave alone.</param>
    /// <param name="DropConnectors">
    /// Run the redundancy pass on this source — see <see cref="PlanConnectorDrops"/>. Per-source because the
    /// seam-ring rule needs a neighbouring part, which a lone face, tail or ear has none of.
    /// </param>
    /// <param name="Profile">This source measured, if the caller has it cached; null means measure it here.</param>
    /// <param name="UnmirrorSides">
    /// This source's UV is MIRRORED and <paramref name="UvConv"/> sends the two sides to different halves of
    /// the shell's sheet. Costs a per-mesh pass, so only set when a layer needs it — see <see cref="SurfaceMirror.AssignSides"/>.
    /// </param>
    /// <param name="HiddenAttributes">
    /// Attribute names the game has switched off on this model: a submesh carrying any is not drawn, not copied,
    /// and neither cover nor a join for the redundancy pass.
    /// </param>
    public readonly record struct SourceSpec(
        byte[] Model,
        Func<string, bool>? KeepMaterial = null,
        HashSet<string>? EnabledShapes = null,
        UVRemapService.UvConversion? UvConv = null,
        bool DropConnectors = false,
        bool UnmirrorSides = false,
        ConnectorProfile? Profile = null,
        IReadOnlySet<string>? HiddenAttributes = null,
        // What KeepMaterial and UvConv ARE, as text: delegates cannot be compared, so null makes a build
        // uncacheable. See SecondSkinService.ShellGeometryKey.
        string? DelegateKey = null);

    // Vertex Usage ids (FFXIV mdl).
    internal const byte UsePosition = 0, UseBlendWeight = 1, UseBlendIndices = 2,
                        UseNormal = 3, UseUV = 4, UseTangent2 = 5, UseTangent1 = 6, UseColor = 7;

    /// <summary>A model carries at most 10 materials (Penumbra's ModelImporter.MaterialLimit). Enforced in the caller.</summary>
    public const int MaxMaterials = 10;

    /// <summary>A position/normal/displacement in model space.</summary>
    internal readonly record struct Vec3(float X, float Y, float Z);
}
