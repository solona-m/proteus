using System;
using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>One stacked shell of the second skin: its own copy of the geometry, material and coverage.</summary>
public sealed class SecondSkinLayer
{
    /// <summary>Material game path as the model stores it, e.g. "/mt_c0201a0053_rir_a.mtrl".</summary>
    public required string MaterialName { get; init; }

    /// <summary>
    /// Coverage mask (one byte per texel = opacity). Triangles whose entire UV footprint is zero are
    /// dropped, so a shell only carries the geometry its layer actually paints. Null keeps everything.
    /// </summary>
    public byte[]? Coverage { get; init; }

    public int CoverageWidth { get; init; }
    public int CoverageHeight { get; init; }

    /// <summary>
    /// Optional toe-cap mask (one byte per texel, body UV, 0 = untouched .. 255 = fully capped): where it is
    /// non-zero the shell webs the gaps between the toes rather than sleeving each one. Null = off.
    /// </summary>
    public byte[]? ToeCap { get; init; }

    public int ToeCapWidth { get; init; }
    public int ToeCapHeight { get; init; }

    /// <summary>How far the masked region inflates toward its envelope (0 = off, 1 = full).</summary>
    public float ToeCapStrength { get; init; } = 1f;

    /// <summary>
    /// Texture-sheet size to draw this layer's reinforced-toe region at, or 0 when it has none. Set to the
    /// sheet size so the region comes back texel for texel. See <see cref="ToeLine"/> and
    /// <see cref="SecondSkinWriter.Stats.ToeReinforceMaps"/>.
    /// </summary>
    public int ToeReinforceSize { get; init; }

    /// <summary>
    /// How far this layer's cloth relaxes across the cleavage (0 = off, the default; 1 = a flat span). No map:
    /// the region is the bust bones' influence intersected with this layer's <see cref="Coverage"/>. See
    /// <c>BustBridgeSolve</c>.
    /// </summary>
    public float BustBridgeStrength { get; init; }

    /// <summary>
    /// How far this layer's cloth is smoothed over the nipple (0 = off, the default; 1 = full). Independent
    /// of <see cref="BustBridgeStrength"/>. See <c>NippleSmoothTarget</c>.
    /// </summary>
    public float NippleSmoothStrength { get; init; }

    /// <summary>
    /// How far this layer's cloth spans the gluteal cleft (0 = off, the default; 1 = a flat span). Its own
    /// solve, seeded from the ONE midline hip bone rather than the bust's symmetric pair — see <c>HipBone</c>
    /// and <c>Facing</c>.
    /// </summary>
    public float CleftBridgeStrength { get; init; }

    /// <summary>
    /// How far this layer's shell flattens the crotch fold (0 = off, the default; 1 = flat). Carried only as
    /// the DECLARATION, like <see cref="NippleSmoothStrength"/>: the flattening is a body pass.
    /// </summary>
    public float FoldSmoothStrength { get; init; }

    /// <summary>
    /// When non-empty, this layer IS geometry: the named meshes of each <see cref="ContentGeometry.Model"/> are
    /// emitted verbatim under this layer's single material. Empty for an ordinary shell. A LIST because a
    /// material is what costs a host slot, not a mesh.
    /// </summary>
    public IReadOnlyList<ContentGeometry> Geometry { get; init; } = [];

    /// <summary>
    /// Multiplies how far this layer is pushed off its surface; 1 keeps the tuned offset. Set from
    /// <c>ShellSurfaceKey.PushScale</c>: a surface far smaller than a torso needs less of <see cref="BaseOffset"/>.
    /// </summary>
    public float PushScale { get; init; } = 1f;
}

/// <summary>
/// One imported model and the meshes of it that belong to a layer. <paramref name="KeepMaterial"/> is
/// matched against the model's own material names (leading slash included) — see
/// <see cref="SecondSkinWriter.KeepByLeaf"/>. <paramref name="MirrorUv1"/> overwrites every uv1 slot with
/// uv0, the ONE deviation from a byte-for-byte copy, set only when the material was rebuilt onto
/// <c>characterscroll.shpk</c> (it samples its scroll map with uv1).
/// </summary>
/// <param name="HiddenAttributes">
/// Attribute names this pack's own toggles switch OFF; a submesh tagged with any is dropped. Baked at build
/// time because the game gates visibility by the IMC mask of the item WORN, and this geometry rides a host.
/// </param>
/// <param name="OwnAttributes">
/// Proteus has already decided this geometry's visibility, so surviving submeshes are emitted UNTAGGED: left
/// tagged, the host's own IMC mask would judge them again. Not for a pack that toggles by NAME through
/// Penumbra's <c>Atr</c> manipulation, which still needs its tags.
/// </param>
/// <param name="DropVariantAttributes">
/// Keep every tag EXCEPT the IMC variant ones (<see cref="SecondSkinWriter.IsVariantAttribute"/>). For geometry that
/// becomes part of the host item: the tags the game drives from what else is worn — a body skin's <c>atr_ude</c>,
/// <c>atr_hij</c>, <c>atr_nek</c>, which long gloves or a high collar switch off — have to survive, while a variant
/// tag would be judged against the host item's own IMC mask, which was never written for it. Ignored with
/// <paramref name="OwnAttributes"/>, which drops every tag.
/// </param>
public sealed record ContentGeometry(
    byte[] Model, Func<string, bool> KeepMaterial, bool MirrorUv1 = false,
    IReadOnlySet<string>? HiddenAttributes = null, bool OwnAttributes = false, bool DropVariantAttributes = false);

/// <summary>
/// A host's shell came out with no meshes. <see cref="ByToggle"/> separates a fault (coverage trimming
/// removed everything) from the user switching off the only thing on that host.
/// </summary>
public sealed class EmptyShellException(string message, bool byToggle) : InvalidOperationException(message)
{
    /// <summary>The pack's own show/hide toggles emptied it, rather than anything going wrong.</summary>
    public bool ByToggle { get; } = byToggle;
}
