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
    /// <summary>How far the first shell sits off the skin (0.05 mm) above the ankle. <see cref="FootPushAt"/>
    /// scales it over the foot, and <see cref="PushSweep"/> replaces that scaling while it is on.</summary>
    public const float BaseOffset = 5e-5f;

    /// <summary>Separation between adjacent shells (0.01 mm): layer k sits at BaseOffset + k * LayerSeparation.</summary>
    public const float LayerSeparation = 1e-5f;

    // ── the foot keeps the offset the rest of the body gave up ──────────────────────────────────────────
    //
    // BaseOffset went 1 mm -> 0.05 mm on the strength of a height-banded sweep that PINNED THE FEET AT 1.00x as
    // its control (see PushSweep.DefaultLadder), so the feet were never measured below 1 mm and went to 0.05 mm
    // on evidence gathered from the shins up. At 0.05 mm they z-fight the skin they were cut from: whole
    // triangles flip between shell and bare skin frame to frame, over exactly the extent of the feet-slot
    // model's own skin mesh. Swept again down the foot, 0.50 mm and 0.75 mm were both short and 1.00 mm was
    // clean — the value the foot had all along.
    //
    // Banded by height rather than by source part: the ankle is where the feet and legs sources overlap, and
    // which of them survives the redundancy pass there is not fixed, so a source-based rule would leave the
    // ankle at whichever offset that pass happened to choose. The bands are ABSOLUTE metres in the body's own
    // model space, which is race-sensitive — see FootPushAt.
    //
    /// <summary>Multiplier over the foot: 1.00 mm, what the whole body had before the 20x change.</summary>
    public const float FootPushScale = 20f;

    /// <summary>
    /// Where the taper begins. The feet-slot skin mesh reaches y 0.151 on c0201 and the legs source starts at
    /// 0.133, so 0.151 is the measured requirement — this sits ABOVE it, deliberately: see <see cref="FootPushAt"/>
    /// on why over-reaching is the free direction and under-reaching is not.
    /// </summary>
    public const float FootBandTop = 0.20f;

    /// <summary>Where the taper ends; above this the shipped <see cref="BaseOffset"/> stands unscaled.</summary>
    public const float FootRampTop = 0.32f;

    /// <summary>
    /// How much of <see cref="BaseOffset"/> a shell vertex at height <paramref name="y"/> gets. A heeled foot is
    /// modelled BELOW the origin (the sole sits at y -0.032 on the shoes this was measured against), so the foot
    /// band has no bottom.
    /// <para/>
    /// CONTINUOUS, and smoothly so. The shell is one mesh: a step here puts the two ends of an edge at different
    /// heights, which is a fold in the surface and — worse — a GAP wherever a part join straddles the step, since
    /// the two sources meeting at the ankle would be pushed apart and the crack weld only closes 1 mm. A
    /// smoothstep taper has no step and no crease in its slope either, so nothing downstream sees an edge at all.
    /// Over 12 cm the whole taper is under 0.008 mm of height per mm of travel.
    /// <para/>
    /// The heights are absolute, and a body's model space scales with the race: a Lalafell's knee and a
    /// Roegadyn's ankle do not sit at the same y as a Midlander's. The two ways that can be wrong are not
    /// symmetric. Reaching too high only returns that region to at most 1 mm, which EVERY race shipped with for
    /// the plugin's whole life (BaseOffset was 1 mm before the 20x change), so it costs nothing; reaching too low
    /// leaves an ankle short of what was measured, and the z-fight this exists to stop survives there. So the band
    /// is set from the TALLEST race rather than the measured one: a male Roegadyn is about 1.22x c0201, putting
    /// that ankle near y 0.184, and the taper starts above it.
    /// </summary>
    public static float FootPushAt(float y)
    {
        if (y <= FootBandTop) return FootPushScale;
        if (y >= FootRampTop) return 1f;
        float t = (y - FootBandTop) / (FootRampTop - FootBandTop);
        t = t * t * (3f - 2f * t);                        // smoothstep: C1, so the slope has no edge either
        return FootPushScale + (1f - FootPushScale) * t;
    }

    /// <summary>
    /// The height the cap passes ask the band at. A toe box is the lowest part of the body, so it is inside the
    /// foot band by construction whatever the body — and on a heeled foot it is below the origin, which the band
    /// also covers. Named rather than inlined so the two cap passes cannot drift apart.
    /// </summary>
    internal const float ToeBandHeight = 0f;

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
    /// <param name="CoverNails">
    /// This source is the HANDS: a nail bed the layer leaves unpainted takes the fingertip's UV — see
    /// <see cref="NailBeds"/>. Each is its own little island in the atlas that glove art never reaches, so
    /// without this a glove leaves every nail bare.
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
        string? DelegateKey = null,
        bool CoverNails = false);

    // Vertex Usage ids (FFXIV mdl).
    internal const byte UsePosition = 0, UseBlendWeight = 1, UseBlendIndices = 2,
                        UseNormal = 3, UseUV = 4, UseTangent2 = 5, UseTangent1 = 6, UseColor = 7;

    /// <summary>A model carries at most 10 materials (Penumbra's ModelImporter.MaterialLimit). Enforced in the caller.</summary>
    public const int MaxMaterials = 10;

    /// <summary>A position/normal/displacement in model space.</summary>
    internal readonly record struct Vec3(float X, float Y, float Z);
}
