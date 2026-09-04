using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Proteus;
using Proteus.Interop;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Light-sensitive glow: the maths that decides how much of a row's glow the scene takes away, and the
/// rules that decide whether a shell's surface may go with it.
///
/// <para>All of it is pure — the game reads are one method away in each case — because none of it can be
/// checked in a running client without waiting for dusk, and every one of these numbers is invisible when
/// it is wrong: a response applied to the wrong row, or a surface fade let loose on a layer that never
/// asked for one, both just look like art that is missing.</para>
/// </summary>
public sealed class LightResponseTests
{
    // ── the sky term ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.00f, 0f)]      // midnight
    [InlineData(0.25f, 0f)]      // dawn — the sine's zero crossing
    [InlineData(0.50f, 1f)]      // noon
    [InlineData(0.75f, 0f)]      // dusk
    public void SkyPeaksAtNoonAndIsDarkAllNight(float dayFraction, float expected)
        => Assert.Equal(expected, SceneLightService.SkyFromTime(dayFraction, 0f), 3);

    [Fact]
    public void NightNeverGoesNegative()
    {
        // The elevation sine is below zero for half the day; a negative sky term would SUBTRACT from the
        // lamps beside it and make a lit room read as darker than an empty one.
        for (float t = 0.75f; t < 1.25f; t += 0.02f)
            Assert.True(SceneLightService.SkyFromTime(t, 0f) >= 0f);
    }

    [Fact]
    public void RainDimsNoonButNeverPastHalf()
    {
        Assert.Equal(1f, SceneLightService.SkyFromTime(0.5f, 0f), 3);
        Assert.Equal(0.5f, SceneLightService.SkyFromTime(0.5f, 1f), 3);
        // Out-of-range rain is clamped rather than allowed to invert the term.
        Assert.Equal(0.5f, SceneLightService.SkyFromTime(0.5f, 5f), 3);
        Assert.Equal(1f, SceneLightService.SkyFromTime(0.5f, -1f), 3);
    }

    // ── placed-light falloff ─────────────────────────────────────────────────

    [Fact]
    public void ALightIsFullAtItsSourceAndGoneAtItsRange()
    {
        Assert.Equal(1f, SceneLightService.Attenuate(0f, 10f, LightFalloffType.Quadratic, 0f,
            LightShape.PointLight), 3);
        Assert.Equal(0f, SceneLightService.Attenuate(10f, 10f, LightFalloffType.Quadratic, 0f,
            LightShape.PointLight), 3);
    }

    [Fact]
    public void SharperFalloffCurvesReachLessFarIn()
    {
        float linear = SceneLightService.Attenuate(5f, 10f, LightFalloffType.Linear, 0f, LightShape.PointLight);
        float quad   = SceneLightService.Attenuate(5f, 10f, LightFalloffType.Quadratic, 0f, LightShape.PointLight);
        float cubic  = SceneLightService.Attenuate(5f, 10f, LightFalloffType.Cubic, 0f, LightShape.PointLight);

        Assert.True(linear > quad && quad > cubic,
            $"expected linear > quadratic > cubic at half range, got {linear} {quad} {cubic}");
    }

    [Fact]
    public void ALightThatDeclaresNoRangeReachesNothing()
    {
        // Regression: Range 0 used to fall back to "reaches the whole cull distance", which turned every
        // such light into a floodlight on the character. A night street summed to 0.98 — full daylight —
        // and every dark-only tattoo in it went out.
        Assert.Equal(0f, SceneLightService.Attenuate(1f, 0f, LightFalloffType.Quadratic, 0f,
            LightShape.PointLight), 3);
    }

    [Fact]
    public void AWorldLightIgnoresDistanceEntirely()
    {
        // A WorldLight is the zone's own directional rig, not a lamp in a room: it has no position to be
        // far from, and attenuating it would switch the sun off for standing in the wrong place.
        Assert.Equal(1f, SceneLightService.Attenuate(999f, 1f, LightFalloffType.Quadratic, 0f,
            LightShape.WorldLight), 3);
    }

    [Fact]
    public void AnAbsurdFalloffFactorIsIgnoredRatherThanObeyed()
    {
        // An unset or nonsense factor reads as "leave the curve alone". Obeying it would raise the curve to
        // a huge power and switch every placed light in the zone off.
        float plain  = SceneLightService.Attenuate(5f, 10f, LightFalloffType.Quadratic, 0f, LightShape.PointLight);
        float absurd = SceneLightService.Attenuate(5f, 10f, LightFalloffType.Quadratic, 500f, LightShape.PointLight);
        Assert.Equal(plain, absurd, 3);
    }

    // ── the colour-table modulation ──────────────────────────────────────────

    private static ShellLightProfile Profile(bool isScroll, params (int Row, float Response)[] rows)
    {
        var response = new float[ShellLightProfile.RowCount];
        foreach (var (row, r) in rows) response[row] = r;
        return new ShellLightProfile(response, new float[ShellLightProfile.RowCount], 0.9f, isScroll);
    }

    private static ShellLightProfile Profile(params (int Row, float Response)[] rows)
        => Profile(isScroll: true, rows);

    private static byte[] TableWith(int row, float emissive, float visibility)
    {
        var table = new byte[ColorTableInterop.TableBytes];
        ColorTableInterop.WriteHalfColor(table, row, ColorTableInterop.OffEmissive, emissive, emissive, emissive);
        WriteHalf(table, row, ColorTableInterop.OffEffectVisibility, visibility);
        return table;
    }

    private static void WriteHalf(byte[] table, int row, int byteOffset, float value)
    {
        int o = row * ColorTableInterop.RowBytes + byteOffset;
        BitConverter.TryWriteBytes(table.AsSpan(o), (ushort)BitConverter.HalfToInt16Bits((Half)value));
    }

    private static float ReadHalf(byte[] table, int row, int byteOffset)
        => (float)BitConverter.Int16BitsToHalf(
            BitConverter.ToInt16(table, row * ColorTableInterop.RowBytes + byteOffset));

    [Fact]
    public void FullLightTakesADarkOnlyRowToNothing()
    {
        var table = TableWith(4, 3.0f, 1.0f);
        ShellColorsetApplier.ApplyLightResponse(table, Profile((4, 1f)), 1f);

        Assert.Equal(0f, ReadHalf(table, 4, ColorTableInterop.OffEmissive), 3);
        // The effect's visibility comes down with it. On characterscroll the emissive is only the scroll
        // map's brightness dial, so dimming it alone leaves the pattern drawn and merely unlit.
        Assert.Equal(0f, ReadHalf(table, 4, ColorTableInterop.OffEffectVisibility), 3);
    }

    [Fact]
    public void DarknessLeavesTheTableExactlyAsAuthored()
    {
        var table = TableWith(4, 3.0f, 1.0f);
        var before = (byte[])table.Clone();
        ShellColorsetApplier.ApplyLightResponse(table, Profile((4, 1f)), 0f);

        // Byte-identical, not merely close: the applier starts from a fresh copy of the material's own
        // DataSet every time, so "no light" has to mean "nothing was touched" or the glow would drift.
        Assert.Equal(before, table);
    }

    [Fact]
    public void HalfResponseInFullLightHalvesTheGlow()
    {
        var table = TableWith(2, 2.0f, 1.0f);
        ShellColorsetApplier.ApplyLightResponse(table, Profile((2, 0.5f)), 1f);

        Assert.Equal(1.0f, ReadHalf(table, 2, ColorTableInterop.OffEmissive), 2);
        Assert.Equal(0.5f, ReadHalf(table, 2, ColorTableInterop.OffEffectVisibility), 2);
    }

    [Fact]
    public void ASphereMapIsNotDimmedAlongWithTheGlow()
    {
        // Half 21 is the scrolling effect's visibility on characterscroll, but the SPHERE MAP's intensity on
        // character.shpk — and GearMaterialWriter writes it there for any row that has one. Scaling it
        // unconditionally faded a latex highlight with the daylight, which no control offers and no
        // documentation promises.
        var table = TableWith(3, 2.0f, 0.8f);   // 0.8 here is a sphere-map intensity, not a glow visibility
        ShellColorsetApplier.ApplyLightResponse(table, Profile(isScroll: false, (3, 1f)), 1f);

        Assert.Equal(0f, ReadHalf(table, 3, ColorTableInterop.OffEmissive), 3);          // the glow goes
        Assert.Equal(0.8f, ReadHalf(table, 3, ColorTableInterop.OffEffectVisibility), 2); // the sphere stays
    }

    [Fact]
    public void RowsThatAskedForNothingAreNotTouched()
    {
        // The whole point of a per-row response: one half of a tattoo goes dark-only while the other half
        // carries on glowing. A modulation that leaked to its neighbours would take both.
        var table = TableWith(6, 3.0f, 1.0f);
        ColorTableInterop.WriteHalfColor(table, 7, ColorTableInterop.OffEmissive, 3f, 3f, 3f);

        ShellColorsetApplier.ApplyLightResponse(table, Profile((6, 1f)), 1f);

        Assert.Equal(0f, ReadHalf(table, 6, ColorTableInterop.OffEmissive), 3);
        Assert.Equal(3f, ReadHalf(table, 7, ColorTableInterop.OffEmissive), 3);
    }

    // ── what the composite publishes ─────────────────────────────────────────

    private static List<ColorTableRowPreset> Rows(params ColorTableRowPreset[] rows) => [.. rows];

    private static ColorTableRowPreset Row(int row, float emissive, float? response = null, bool hide = false)
        => new()
        {
            Row = row,
            SubRowA = new ColorTableSubRowPreset
            {
                Emissive = emissive,
                LightResponse = response,
                HideInLight = hide,
            },
        };

    [Fact]
    public void APresetsResponseLandsOnTheGameRowItsGlowIsOn()
    {
        // Row 3 sub-row A is game row 4 — (3−1)×2. Landing it anywhere else would dim a region the user
        // never marked and leave the one they did at full brightness.
        var profile = SecondSkinService.BuildLightProfile(
            Rows(Row(3, 1f, response: 1f)), isMaskShell: false, ShellSurfaceKind.Body);

        Assert.NotNull(profile);
        Assert.Equal(1f, profile!.RowResponse[4]);
        Assert.Equal(0f, profile.RowResponse[5]);   // sub-row B was never set
    }

    [Fact]
    public void RowsThatWantNothingPublishNoProfileAtAll()
    {
        // The runtime's fast path is "this material has no profile". A profile of all zeroes would cost a
        // lookup and a 2 KB table copy on every redraw to change nothing.
        Assert.Null(SecondSkinService.BuildLightProfile(
            Rows(Row(1, 1f)), isMaskShell: false, ShellSurfaceKind.Body));
        Assert.Null(SecondSkinService.BuildLightProfile(null, isMaskShell: false, ShellSurfaceKind.Body));
    }

    [Fact]
    public void AMaskShellMirrorsTheResponseOntoTheHalfPairItMirrorsTheGlowOnto()
    {
        // BuildRows mirrors a half-authored pair on a mask shell, carrying the emissive across with it. The
        // response has to travel the same way or half of a mask would fade while the other half stayed lit.
        var profile = SecondSkinService.BuildLightProfile(
            Rows(Row(2, 1f, response: 1f)), isMaskShell: true, ShellSurfaceKind.Body);

        Assert.NotNull(profile);
        Assert.Equal(1f, profile!.RowResponse[2]);
        Assert.Equal(1f, profile.RowResponse[3]);
    }

    [Fact]
    public void OneRowMayHideItsSurfaceWhileItsNeighbourStaysVisible()
    {
        // The point of doing this on the COVERAGE rather than on the material's alpha constants: opacity is
        // per texel here, so one region of a tattoo can vanish in daylight while the region beside it — on
        // the same shell, the same material — carries on glowing and stays solid.
        var profile = SecondSkinService.BuildLightProfile(
            Rows(Row(1, 1f, response: 1f, hide: true), Row(2, 1f, response: 1f)),
            isMaskShell: false, ShellSurfaceKind.Body);

        Assert.NotNull(profile);
        Assert.True(profile!.AnyHide);
        Assert.Equal(1f, profile.RowHide[0]);   // row 1 sub-row A
        Assert.Equal(0f, profile.RowHide[2]);   // row 2 sub-row A asked for no such thing
        // Both still dim their glow.
        Assert.Equal(1f, profile.RowResponse[0]);
        Assert.Equal(1f, profile.RowResponse[2]);
    }

    [Fact]
    public void HidingWithNoResponseFollowsTheGlowAllTheWay()
    {
        // "Hide when the light does nothing" is not a setting anyone can mean, so a bare Hide reads as a
        // full follow rather than as a fade of zero — which would silently do nothing.
        var profile = SecondSkinService.BuildLightProfile(
            Rows(Row(1, 1f, hide: true)), isMaskShell: false, ShellSurfaceKind.Body);

        Assert.NotNull(profile);
        Assert.Equal(1f, profile!.RowHide[0]);
    }

    [Fact]
    public void APartialResponseHidesOnlyAsFarAsItDims()
    {
        // Opacity follows the glow, so a row that only half-fades must only half-vanish. Taking the surface
        // all the way out from under a glow that is still burning would look like a hole.
        var profile = SecondSkinService.BuildLightProfile(
            Rows(Row(1, 1f, response: 0.5f, hide: true)), isMaskShell: false, ShellSurfaceKind.Body);

        Assert.NotNull(profile);
        Assert.Equal(0.5f, profile!.RowHide[0]);
    }

    [Fact]
    public void AScrollShellIsMarkedAsOneSoHalf21MeansWhatTheApplierThinks()
    {
        var scroll = SecondSkinService.BuildLightProfile(
            Rows(Row(1, 1f, response: 1f)), isMaskShell: false, ShellSurfaceKind.Body, isScroll: true);
        var cloth = SecondSkinService.BuildLightProfile(
            Rows(Row(1, 1f, response: 1f)), isMaskShell: false, ShellSurfaceKind.Body, isScroll: false);

        Assert.True(scroll!.IsScroll);
        Assert.False(cloth!.IsScroll);
    }

    [Fact]
    public void FaceArtIsProbedNearTheFaceAndBodyArtNearTheBody()
    {
        var face = SecondSkinService.BuildLightProfile(
            Rows(Row(1, 1f, response: 1f)), isMaskShell: false, ShellSurfaceKind.Face);
        var body = SecondSkinService.BuildLightProfile(
            Rows(Row(1, 1f, response: 1f)), isMaskShell: false, ShellSurfaceKind.Body);

        Assert.True(face!.ProbeHeight > body!.ProbeHeight,
            "a face sits above a body, so a lamp at head height should reach it first");
    }
}
