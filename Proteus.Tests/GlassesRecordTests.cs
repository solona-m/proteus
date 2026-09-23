using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The record of having equipped the invisible-glasses carrier decides two things at once: whether the shell may
/// host on that pair, and whether Proteus may ever take it off again. Nothing re-adopts a carrier, so a record
/// dropped while the pair is still worn is not recoverable — the player is left wearing a Gold Eyepatch that
/// Proteus will neither hide nor remove, and has to clear the slot in Glamourer by hand.
/// </summary>
public sealed class GlassesRecordTests
{
    private const ulong Ours = 5521;    // the carrier's sheet row
    private const ulong Theirs = 4242;  // any other Glasses row

    [Fact]
    public void NoRecordDecidesNothing()
        => Assert.Equal(GlassesRecord.Decision.Keep,
            GlassesRecord.Evaluate(recorded: false, wornRow: 0, Ours, ourModelDrawn: true));

    [Fact]
    public void UnreadableStateKeepsTheRecord()
        => Assert.Equal(GlassesRecord.Decision.Hold,
            GlassesRecord.Evaluate(recorded: true, wornRow: null, Ours, ourModelDrawn: true));

    [Fact]
    public void OurPairStillWornKeepsTheRecord()
        => Assert.Equal(GlassesRecord.Decision.Keep,
            GlassesRecord.Evaluate(recorded: true, wornRow: Ours, Ours, ourModelDrawn: true));

    /// <summary>
    /// The regression this type exists for. ReconcileInvisibleGlasses runs off the redraw hook, so Glamourer's
    /// state and the walk of drawn models are read at different instants; mid-redraw the state can already say
    /// "no glasses" while the carrier's model is still on the character. Forgetting on that reading is what left a
    /// user's eyepatch visible with nothing in the log to say why.
    /// </summary>
    [Fact]
    public void NothingWornWhileOurModelIsStillDrawnIsARaceNotARemoval()
        => Assert.Equal(GlassesRecord.Decision.Hold,
            GlassesRecord.Evaluate(recorded: true, wornRow: 0, Ours, ourModelDrawn: true));

    [Fact]
    public void NothingWornAndNothingDrawnForgetsIt()
        => Assert.Equal(GlassesRecord.Decision.Forget,
            GlassesRecord.Evaluate(recorded: true, wornRow: 0, Ours, ourModelDrawn: false));

    /// <summary>Their own pair of the carrier item: ours is gone AND the shell has to move off its model.</summary>
    [Fact]
    public void AnotherItemOnOurModelForgetsAndReleases()
        => Assert.Equal(GlassesRecord.Decision.ForgetAndRelease,
            GlassesRecord.Evaluate(recorded: true, wornRow: Theirs, Ours, ourModelDrawn: true));

    /// <summary>An unrelated pair: ours simply went, and there is no model of ours to release.</summary>
    [Fact]
    public void AnotherItemOnAnotherModelJustForgets()
        => Assert.Equal(GlassesRecord.Decision.Forget,
            GlassesRecord.Evaluate(recorded: true, wornRow: Theirs, Ours, ourModelDrawn: false));

    /// <summary>
    /// The safety property behind all of it: while the carrier's model is on the character, no reading may drop the
    /// record without also moving the shell off it. Those are the only outcomes that leave the pair reachable —
    /// Keep and Hold preserve the record, which is what <c>CompositorService.OurGlassesAreOn</c> removes on, and
    /// ForgetAndRelease hands the slot back deliberately and recomposites off it.
    /// <para/>
    /// Dropping it any other way is unrecoverable: nothing re-adopts a carrier, so the pair stays on the player's
    /// face, unhidden because the shell moves to another host and unremoved because there is no record to remove on.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0UL)]
    [InlineData(Ours)]
    [InlineData(Theirs)]
    public void WhileOurModelIsDrawnTheCarrierIsNeverStranded(ulong? wornRow)
    {
        var d = GlassesRecord.Evaluate(recorded: true, wornRow, Ours, ourModelDrawn: true);
        Assert.True(d is GlassesRecord.Decision.Keep
                      or GlassesRecord.Decision.Hold
                      or GlassesRecord.Decision.ForgetAndRelease,
            $"reading {wornRow?.ToString() ?? "unreadable"} dropped the record while the carrier was still drawn, " +
            "which leaves it visible with no way for Proteus to take it off");
    }

    /// <summary>
    /// The other half of that: every outcome that keeps the record must carry a reading the caller can act on
    /// without inventing one. <c>JudgeGlassesRecord</c> matches ForgetAndRelease with <c>worn is { } theirs</c> to
    /// name the item in its log, so a decision that paired ForgetAndRelease with an unreadable state would silently
    /// stop matching and skip the release that moves the shell off the player's pair.
    /// </summary>
    [Fact]
    public void ReleasingAlwaysCarriesTheItemThatCausedIt()
    {
        foreach (var drawn in new[] { true, false })
            Assert.NotEqual(GlassesRecord.Decision.ForgetAndRelease,
                GlassesRecord.Evaluate(recorded: true, wornRow: null, Ours, drawn));
    }
}
