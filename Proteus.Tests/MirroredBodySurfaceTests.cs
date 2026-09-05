using System;
using System.Collections.Generic;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="CompositorService.HasMirroredBodySurface"/> and
/// <see cref="CompositorService.NeedsUnmirroredShell(OverlayDescriptor, bool)"/> — the pair that decides
/// whether asymmetric art is about to be folded in half.
/// <para/>
/// The case these exist for is the one that is easy to get wrong: a character's skin is NOT one surface.
/// Gear ships its own skin, and a garment model routinely carries the body it exposes as vanilla geometry
/// pointing at <c>mt_c0201b0001_a.mtrl</c>. So a Bibo+ wearer can have a bibo body and a gen2 torso at the
/// same time, and the art on that torso really is folded — an earlier "gen2 and nothing else" test read
/// that wardrobe as safe and silently did nothing.
/// </summary>
public class MirroredBodySurfaceTests
{
    private static HashSet<string> Types(params string[] t) => new(t, StringComparer.OrdinalIgnoreCase);

    private const string BiboMtrl = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";
    private const string VanillaMtrl = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl";

    [Fact]
    public void A_vanilla_body_is_mirrored()
    {
        Assert.True(CompositorService.HasMirroredBodySurface(Types("gen2")));
    }

    /// <summary>
    /// The regression this file is really about: gen2 ALONGSIDE an asymmetric body still folds the art on
    /// the gen2 surface. A bibo body elsewhere on the same character rescues nothing.
    /// </summary>
    [Fact]
    public void A_gen2_surface_beside_a_bibo_body_still_counts()
    {
        Assert.True(CompositorService.HasMirroredBodySurface(Types("bibo", "gen2")));
        Assert.True(CompositorService.HasMirroredBodySurface(Types("gen3", "gen2")));
        Assert.True(CompositorService.HasMirroredBodySurface(Types("bibo", "gen3", "gen2")));
    }

    [Fact]
    public void Bodies_that_own_both_sides_are_not_mirrored()
    {
        Assert.False(CompositorService.HasMirroredBodySurface(Types("bibo")));
        Assert.False(CompositorService.HasMirroredBodySurface(Types("gen3")));
        Assert.False(CompositorService.HasMirroredBodySurface(Types("bibo", "gen3")));
        Assert.False(CompositorService.HasMirroredBodySurface(Types()));
    }

    /// <summary>The body-type set is built case-insensitively; the lookup has to agree.</summary>
    [Fact]
    public void The_lookup_is_case_insensitive()
    {
        Assert.True(CompositorService.HasMirroredBodySurface(Types("GEN2")));
    }

    // ── NeedsUnmirroredShell ─────────────────────────────────────────────────

    private static OverlayDescriptor Art(bool? asymmetric, string? source, string mtrl = BiboMtrl) =>
        new()
        {
            Layer = OverlayLayer.Skin,
            AsymmetricArt = asymmetric,
            SourceBodyType = source,
            MaterialGamePaths = [mtrl],
        };

    [Fact]
    public void Asymmetric_art_over_a_mirrored_surface_needs_a_shell()
    {
        Assert.True(CompositorService.NeedsUnmirroredShell(Art(true, "bibo"), wearingMirroredBody: true));
        Assert.True(CompositorService.NeedsUnmirroredShell(Art(true, "gen3"), wearingMirroredBody: true));
    }

    [Fact]
    public void Nothing_is_needed_when_no_surface_folds_it()
    {
        Assert.False(CompositorService.NeedsUnmirroredShell(Art(true, "bibo"), wearingMirroredBody: false));
    }

    /// <summary>
    /// Symmetric art keeps the cheap path, and art never measured (null) behaves exactly as before — a shell
    /// is expensive and must not be built on an unanswered question.
    /// </summary>
    [Fact]
    public void Symmetric_or_unmeasured_art_is_left_alone()
    {
        Assert.False(CompositorService.NeedsUnmirroredShell(Art(false, "bibo"), wearingMirroredBody: true));
        Assert.False(CompositorService.NeedsUnmirroredShell(Art(null, "bibo"), wearingMirroredBody: true));
    }

    /// <summary>
    /// Art authored in the mirrored space itself has only one side to begin with, whatever a measurement
    /// said — there is no second half to spread onto the shell.
    /// </summary>
    [Fact]
    public void Art_already_in_the_mirrored_space_has_nothing_to_unfold()
    {
        Assert.False(CompositorService.NeedsUnmirroredShell(Art(true, "gen2"), wearingMirroredBody: true));
        Assert.False(CompositorService.NeedsUnmirroredShell(
            Art(true, "GEN2", VanillaMtrl), wearingMirroredBody: true));
    }

    /// <summary>
    /// With no declared SourceBodyType the space is inferred from the material the art targets, so a
    /// bibo-targeting overlay still qualifies and a vanilla-targeting one still doesn't.
    /// </summary>
    [Fact]
    public void The_space_falls_back_to_the_material_it_paints()
    {
        Assert.True(CompositorService.NeedsUnmirroredShell(Art(true, null, BiboMtrl), wearingMirroredBody: true));
        Assert.False(CompositorService.NeedsUnmirroredShell(Art(true, null, VanillaMtrl), wearingMirroredBody: true));
    }
}
