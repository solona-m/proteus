using System;

namespace Proteus.Services;

/// <summary>
/// What one shell material wants from the scene light, published by the composite and read every frame by
/// <see cref="Proteus.Interop.ShellColorsetApplier"/> (the glow) and
/// <see cref="Proteus.Interop.ShellCoverageFade"/> (the surface).
///
/// <para>Kept beside the material rather than inside it because the material has nowhere to put it: the
/// colour table's 32 halves are all spoken for, and a light response is not something the shader reads —
/// it is something Proteus applies to the values the shader reads. The material on disk stays the
/// authored, full-brightness one, which is also what makes the feature free to switch off.</para>
/// </summary>
/// <param name="RowResponse">
/// Per game row (0–31, i.e. row pair × sub-row), how much of that row's glow the light takes away: 0 keeps
/// today's unconditional glow, 1 is dark-only. Always 32 long.
/// </param>
/// <param name="RowHide">
/// Per game row, how far the light also takes that row's SURFACE away — its opacity follows its glow, so
/// where it has stopped glowing there is nothing left but skin. Always 32 long.
/// <para/>
/// Per row rather than per layer because opacity is per texel here: it is the shell normal's blue channel,
/// and the index texture says which texel belongs to which row. An earlier attempt moved the material's
/// alpha constants instead, which are material-wide — that forced a layer to agree with itself and made a
/// mixed overlay give up its surface fade entirely.
/// </param>
/// <param name="ProbeHeight">
/// Roughly how far up the wearer this shell's surface sits, in game units, so the light is sampled near the
/// art rather than at the character's feet. An estimate of a body part, not a measurement of geometry.
/// </param>
/// <param name="IsScroll">
/// Whether this shell runs <c>characterscroll.shpk</c>, where colour-table half 21 is the scrolling
/// effect's VISIBILITY and has to come down with the emissive or the pattern stays drawn and merely unlit.
/// <para/>
/// Load-bearing, not informational: on <c>character.shpk</c> that same half is the SPHERE MAP's intensity,
/// which <see cref="GearMaterialWriter"/> writes for any row that has one. Dimming it there would fade a
/// latex highlight with the daylight — something no control in the editor offers and nothing in the docs
/// promises — so the light response must know which of the two meanings it is looking at.
/// </param>
public sealed record ShellLightProfile(float[] RowResponse, float[] RowHide, float ProbeHeight, bool IsScroll)
{
    public const int RowCount = 32;

    /// <summary>Whether anything here asks the light for anything. A profile that doesn't is not published:
    /// the applier's fast path is "this leaf has no profile", and an all-zero one would cost a lookup and a
    /// 2 KB table copy per redraw to change nothing.</summary>
    public bool Any => AnyHide || Array.Exists(RowResponse, r => r > 0f);

    /// <summary>Whether any row's surface follows its glow — the question the coverage fade asks before it
    /// decodes anything.</summary>
    public bool AnyHide => Array.Exists(RowHide, r => r > 0f);
}
